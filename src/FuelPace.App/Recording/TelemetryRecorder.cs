using System.Diagnostics;
using System.Globalization;
using System.Text;
using FuelPace.App.Diagnostics;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Recording;

/// <summary>
/// Records every frame the loop reads into <c>.ibt</c> files, one per session context (a new profile key or a new
/// <c>SessionNum</c>). Threading: <see cref="OnFrame"/> (telemetry thread) copies the frame into a preallocated
/// single-producer/single-consumer ring and never allocates; a background thread drains the ring into an
/// <see cref="IbtStreamWriter"/>, checkpoints it every 5 s and enforces the total size cap. <see cref="OnSessionInfo"/> runs
/// on the loop's session-info worker and only publishes an immutable context that the writer thread picks up.
/// <see cref="OnSourceEnded"/> waits (bounded) until the ring is written and the file closed. A full ring drops the new frame
/// and counts it. Frames that arrive before the first session info stay in the ring until it does.
/// </summary>
public sealed class TelemetryRecorder : IFrameTap, IDisposable
{
    private const long BytesPerGB = 1024L * 1024 * 1024;
    private static readonly TimeSpan CheckpointEvery = TimeSpan.FromSeconds(5);

    private sealed record SessionContext(string Yaml, string ProfileKey, string CarPath, string TrackName, int Version);

    private readonly string _dir;
    private readonly DiagLog _log;
    private readonly long _maxBytes;
    private readonly TelemetryFrame[] _ring;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ManualResetEventSlim _endAck = new(false);
    private readonly Thread _thread;

    private long _head, _tail;            // consumer / producer positions (monotonic)
    private long _dropped, _written;
    private volatile SessionContext? _ctx;
    private long _endAt = -1;             // ring position the source ended at; the writer closes the file when it gets there
    private volatile bool _stop;

    // writer-thread state
    private IbtStreamWriter? _writer;
    private SessionContext? _curCtx;
    private int _curSessionNum;
    private string? _curPath;
    private long _fileFrames, _droppedReported; // drops are reported by the file that closes next (some happen before it opens)
    private double _lastTime;
    private long _lastCheckpoint;

    /// <summary>Test hook: called on the writer thread before each frame is written.</summary>
    internal volatile Action? WriterHook;

    public TelemetryRecorder(string recordingsDir, DiagLog log, double maxTotalGB, int ringCapacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ringCapacity, 1);
        _dir = recordingsDir;
        _log = log ?? DiagLog.Null;
        _maxBytes = (long)(maxTotalGB * BytesPerGB);
        _ring = new TelemetryFrame[ringCapacity];
        Directory.CreateDirectory(recordingsDir);
        _thread = new Thread(Run) { IsBackground = true, Name = "fuelpace recorder", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Frames dropped because the ring was full.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public void OnFrame(in TelemetryFrame f)
    {
        long tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _ring.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _ring[tail % _ring.Length] = f;
        Volatile.Write(ref _tail, tail + 1);
        _wake.Set();
    }

    public void OnSessionInfo(string yaml, int version, SessionInfo info)
    {
        _ctx = new SessionContext(yaml, info.ProfileKey, info.CarPath, info.TrackName, version);
        _wake.Set();
    }

    public void OnSourceEnded()
    {
        if (!_thread.IsAlive) return;
        _endAck.Reset();
        Volatile.Write(ref _endAt, Volatile.Read(ref _tail));
        _wake.Set();
        _endAck.Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>Blocks until every frame handed to <see cref="OnFrame"/> so far has been written (tests).</summary>
    internal void Drain()
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _head) < Volatile.Read(ref _tail) && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(1);
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(10));
        _wake.Dispose();
        _endAck.Dispose();
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                bool stopping = _stop;
                _wake.Reset(); // before pumping: a Set that races with the pump leaves the event set, so the wait below returns at once
                bool progressed = Pump();
                if (stopping && !progressed) break;
                if (!progressed)
                {
                    _wake.Wait(250);
                    CheckpointIfDue();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Exception("record", ex);
        }
        finally
        {
            CloseFile();
            _endAck.Set();
        }
    }

    /// <summary>Writes what is in the ring (and handles a pending end). Returns whether it did anything.</summary>
    private bool Pump()
    {
        bool did = false;
        var ctx = _ctx;
        while (ctx is not null && _head < Volatile.Read(ref _tail))
        {
            WriterHook?.Invoke();
            ref readonly var f = ref _ring[_head % _ring.Length];
            if (_writer is null || ctx.ProfileKey != _curCtx!.ProfileKey || f.SessionNum != _curSessionNum)
                Rotate(ctx, in f);
            else if (!ReferenceEquals(ctx, _curCtx))
            {
                _writer.WriteSessionInfo(ctx.Yaml);
                _curCtx = ctx;
            }
            _writer!.Append(in f);
            _lastTime = f.SessionTime;
            _fileFrames++;
            _written++;
            Volatile.Write(ref _head, _head + 1);
            did = true;
            CheckpointIfDue();
            ctx = _ctx;
        }

        long end = Volatile.Read(ref _endAt);
        if (end >= 0 && ctx is null) Volatile.Write(ref _head, end); // ended before any session info: nothing to name the file after
        if (end >= 0 && _head >= end)
        {
            CloseFile();
            Volatile.Write(ref _endAt, -1);
            _endAck.Set();
            did = true;
        }
        return did;
    }

    private void Rotate(SessionContext ctx, in TelemetryFrame f)
    {
        CloseFile();
        string name = $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}_{Sanitize(ctx.CarPath)}_{Sanitize(ctx.TrackName)}";
        string path = Path.Combine(_dir, name + ".ibt");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(_dir, $"{name}_{n}.ibt");

        _writer = new IbtStreamWriter(path, 60, log: _log);
        _writer.WriteSessionInfo(ctx.Yaml);
        _curCtx = ctx;
        _curSessionNum = f.SessionNum;
        _curPath = path;
        _fileFrames = 0;
                _lastCheckpoint = Stopwatch.GetTimestamp();
        _log.Emit(DiagLevel.Info, AppDiagEvents.RecordingStarted, f.SessionTime);
        _log.Text(DiagLevel.Info, "record", "recording to " + path);
        EnforceCap();
    }

    private void CloseFile()
    {
        if (_writer is null) return;
        try { _writer.Dispose(); }
        catch (Exception ex) { _log.Exception("record", ex); }
        _writer = null;
        long total = Interlocked.Read(ref _dropped), dropped = total - _droppedReported;
        _droppedReported = total;
        _log.Emit(DiagLevel.Info, AppDiagEvents.RecordingStopped, _lastTime, (int)_fileFrames, (int)dropped);
        _curCtx = null;
        _curPath = null;
    }

    private void CheckpointIfDue()
    {
        if (_writer is null || Stopwatch.GetElapsedTime(_lastCheckpoint) < CheckpointEvery) return;
        _lastCheckpoint = Stopwatch.GetTimestamp();
        try
        {
            _writer.Checkpoint();
            EnforceCap();
        }
        catch (Exception ex) { _log.Exception("record", ex); }
    }

    /// <summary>Deletes the oldest recordings (never the open one) until the total fits the cap.</summary>
    private void EnforceCap()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.ibt");
            long total = 0;
            foreach (var fi in files) total += fi.Length;
            if (total <= _maxBytes) return;
            Array.Sort(files, static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (var fi in files)
            {
                if (total <= _maxBytes) break;
                if (string.Equals(fi.FullName, _curPath, StringComparison.Ordinal)) continue;
                long len = fi.Length;
                try
                {
                    fi.Delete();
                    File.Delete(fi.FullName + ".yaml");
                    total -= len;
                    _log.Text(DiagLevel.Info, "record", "recording removed (size cap): " + fi.Name);
                }
                catch (IOException ex) { _log.Text(DiagLevel.Warn, "record", "cannot remove " + fi.Name + ": " + ex.Message); }
                catch (UnauthorizedAccessException ex) { _log.Text(DiagLevel.Warn, "record", "cannot remove " + fi.Name + ": " + ex.Message); }
            }
        }
        catch (IOException ex) { _log.Text(DiagLevel.Warn, "record", "size cap check failed: " + ex.Message); }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
