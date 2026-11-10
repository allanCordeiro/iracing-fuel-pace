using System.Buffers;
using System.Globalization;
using System.Text.Json;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Diagnostics;

/// <summary>
/// Drains a <see cref="DiagLog"/> every 250 ms into append-only JSON Lines files
/// (<c>fuelpace-yyyyMMdd-n.jsonl</c>), rotating by file size and pruning by total size.
/// Never throws from the background thread.
/// </summary>
public sealed class DiagFileSink : IDisposable
{
    private const int DrainBatch = 1024;
    private const int MaxPendingLines = 20000;

    private readonly DiagLog _log;
    private readonly string _dir;
    private readonly long _maxFileBytes;
    private readonly long _maxTotalBytes;
    private readonly Action<Utf8JsonWriter, DiagText>? _textFormatter;
    private readonly Lock _io = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private readonly List<byte[]> _pending = [];
    private readonly DiagRecord[] _recBuf = new DiagRecord[DrainBatch];
    private readonly ArrayBufferWriter<byte> _buffer = new(512);
    private readonly Utf8JsonWriter _json;
    private FileStream? _file;
    private string? _filePath;
    private long _fileLen;
    private long _reportedDropped;
    private long _lostSinceReport;
    private long _lostLines;
    private bool _disposed;

    public DiagFileSink(DiagLog log, string logsDir, long maxFileBytes = 20 << 20, long maxTotalBytes = 200 << 20)
        : this(log, logsDir, maxFileBytes, maxTotalBytes, null)
    {
    }

    /// <param name="textFormatter">Test seam: replaces the text-line formatter.</param>
    internal DiagFileSink(DiagLog log, string logsDir, long maxFileBytes, long maxTotalBytes, Action<Utf8JsonWriter, DiagText>? textFormatter)
    {
        _textFormatter = textFormatter;
        _log = log;
        _dir = logsDir;
        _maxFileBytes = maxFileBytes;
        _maxTotalBytes = maxTotalBytes;
        _json = new Utf8JsonWriter(_buffer);
        _thread = new Thread(Run) { IsBackground = true, Name = "FuelPace.DiagSink", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Lines that could not be written and were discarded (log directory unwritable).</summary>
    public long LostLines => Interlocked.Read(ref _lostLines);

    public void FlushNow()
    {
        try
        {
            using (_io.EnterScope())
            {
                if (_disposed) return;
                DrainLocked();
            }
        }
        catch
        {
            // flushing (e.g. from the crash handler) must never throw
        }
    }

    public void Dispose()
    {
        _stop.Set();
        try
        {
            if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
            if (!_io.TryEnter(TimeSpan.FromSeconds(2))) return;
            try
            {
                if (_disposed) return;
                try { DrainLocked(); }
                finally
                {
                    _disposed = true;
                    CloseFile();
                }
            }
            finally { _io.Exit(); }
        }
        catch
        {
            // Dispose must never throw
        }
        finally
        {
            try { _json.Dispose(); } catch { }
        }
    }

    private void Run()
    {
        while (!_stop.Wait(250))
        {
            try
            {
                using (_io.EnterScope())
                {
                    if (_disposed) return;
                    DrainLocked();
                }
            }
            catch
            {
                // never let the sink thread die; retry on the next tick
            }
        }
    }

    private void DrainLocked()
    {
        try
        {
            var records = new List<DiagRecord>();
            int n;
            while ((n = _log.DrainRecords(_recBuf)) > 0)
                for (int i = 0; i < n; i++) records.Add(_recBuf[i]);
            var texts = new List<DiagText>();
            while (_log.TryDequeueText(out var t)) texts.Add(t);

            long dropped = _log.Dropped;
            if (dropped > _reportedDropped)
            {
                AddDroppedLine("DiagDropped", dropped - _reportedDropped, null);
                _reportedDropped = dropped;
            }

            int ri = 0, ti = 0;
            while (ri < records.Count || ti < texts.Count)
            {
                if (ti >= texts.Count || (ri < records.Count && records[ri].Seq <= texts[ti].Seq))
                {
                    var r = records[ri++];
                    TrySerialize(w => DiagJsonFormatter.Write(w, in r));
                }
                else
                {
                    var t = texts[ti++];
                    t = t with { Message = Privacy.Scrub(t.Message) }; // exceptions and I/O errors carry user paths
                    TrySerialize(w => { if (_textFormatter is not null) _textFormatter(w, t); else DiagJsonFormatter.Write(w, in t); });
                }
            }
        }
        catch
        {
            // draining itself failed; whatever was serialized is still written below
        }
        WritePending();
        if (_lostSinceReport > 0 && _pending.Count == 0)
        {
            // report what was lost (serialization failures, or lines dropped while the directory was unwritable)
            long lost = _lostSinceReport;
            _lostSinceReport = 0;
            AddDroppedLine("DiagDropped", lost, "lost");
            WritePending();
        }
    }

    private void TrySerialize(Action<Utf8JsonWriter> write)
    {
        try { Serialize(write); }
        catch
        {
            _lostSinceReport++;
            Interlocked.Increment(ref _lostLines);
        }
    }

    private void AddDroppedLine(string ev, long count, string? cause)
    {
        Serialize(w =>
        {
            w.WriteStartObject();
            w.WriteString("t", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("lvl", "warn");
            w.WriteString("ev", ev);
            w.WriteNumber("count", count);
            if (cause is not null) w.WriteString("cause", cause);
            w.WriteEndObject();
        });
    }

    private void Serialize(Action<Utf8JsonWriter> write)
    {
        _buffer.ResetWrittenCount();
        _json.Reset(_buffer);
        write(_json);
        _json.Flush();
        var line = new byte[_buffer.WrittenCount + 1];
        _buffer.WrittenSpan.CopyTo(line);
        line[^1] = (byte)'\n';
        _pending.Add(line);
    }

    private void WritePending()
    {
        if (_pending.Count == 0) return;
        int written = 0;
        try
        {
            foreach (var line in _pending)
            {
                EnsureFile(line.Length);
                _file!.Write(line);
                _fileLen += line.Length;
                written++;
            }
            _file!.Flush();
            _pending.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _pending.RemoveRange(0, written);
            CloseFile();
            int excess = _pending.Count - MaxPendingLines;
            if (excess > 0)
            {
                _pending.RemoveRange(0, excess);
                _lostSinceReport += excess;
                Interlocked.Add(ref _lostLines, excess);
            }
        }
    }

    private void EnsureFile(int nextLineLen)
    {
        if (_file is not null && _fileLen > 0 && _fileLen + nextLineLen > _maxFileBytes)
        {
            CloseFile();
            OpenFile(forceNext: true);
        }
        else if (_file is null) OpenFile(forceNext: false);
    }

    private void OpenFile(bool forceNext)
    {
        Directory.CreateDirectory(_dir);
        string prefix = "fuelpace-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-";
        int max = 0;
        foreach (var f in Directory.GetFiles(_dir, prefix + "*.jsonl"))
        {
            var mid = Path.GetFileNameWithoutExtension(f)[prefix.Length..];
            if (int.TryParse(mid, NumberStyles.None, CultureInfo.InvariantCulture, out int idx) && idx > max) max = idx;
        }
        int index = max == 0 ? 1 : max;
        string path = Path.Combine(_dir, prefix + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");
        if (max != 0 && (forceNext || new FileInfo(path).Length >= _maxFileBytes))
        {
            index = max + 1;
            path = Path.Combine(_dir, prefix + index.ToString(CultureInfo.InvariantCulture) + ".jsonl");
        }
        _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _filePath = path;
        _fileLen = _file.Length;
        Prune();
    }

    private void CloseFile()
    {
        try { _file?.Dispose(); } catch (IOException) { }
        _file = null;
    }

    private void Prune()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("fuelpace-*.jsonl")
                .Where(f => !string.Equals(f.FullName, _filePath, StringComparison.Ordinal))
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name.Length).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            // reserve room for the current file to grow to its cap, so the total stays within the limit
            long total = files.Sum(f => f.Length) + Math.Max(_fileLen, _maxFileBytes);
            foreach (var f in files)
            {
                if (total <= _maxTotalBytes) break;
                try { total -= f.Length; f.Delete(); } catch (IOException) { total += f.Length; }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
