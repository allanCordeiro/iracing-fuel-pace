using System.Diagnostics;

namespace FuelPace.App.Ipc;

/// <summary>Reads newline-delimited messages from a stream, capping the size of a single line.</summary>
internal sealed class IpcLineReader(Stream stream, int maxLineBytes)
{
    byte[] _buf = new byte[4096];
    int _start, _end;
    long _partialSince; // Stopwatch timestamp when the first byte of the current line arrived; 0 = nothing buffered

    static TimeSpan Elapsed(long since) => Stopwatch.GetElapsedTime(since);

    /// <summary>
    /// Next line (without the newline), or null at end of stream. Throws <see cref="InvalidDataException"/> when a line exceeds the cap
    /// and <see cref="TimeoutException"/> when <paramref name="totalTimeout"/> (whole call) or <paramref name="partialTimeout"/>
    /// (once bytes of the line are buffered) elapses. Null = no deadline.
    /// </summary>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct, TimeSpan? totalTimeout = null, TimeSpan? partialTimeout = null)
    {
        long callStart = Stopwatch.GetTimestamp();
        int scanned = 0;
        while (true)
        {
            int nl = Array.IndexOf(_buf, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (nl >= 0)
            {
                var line = _buf.AsSpan(_start, nl - _start).ToArray();
                _start = nl + 1;
                _partialSince = _end > _start ? Stopwatch.GetTimestamp() : 0;
                if (line.Length > maxLineBytes) throw new InvalidDataException("message too large");
                return line;
            }
            scanned = _end - _start;
            if (scanned > maxLineBytes) throw new InvalidDataException("message too large");
            if (_end == _buf.Length)
            {
                if (_start > 0)
                {
                    Buffer.BlockCopy(_buf, _start, _buf, 0, scanned);
                    _end = scanned; _start = 0;
                }
                else Array.Resize(ref _buf, Math.Min(_buf.Length * 2, maxLineBytes + 8192));
            }
            TimeSpan? remaining = null;
            if (totalTimeout is { } tt) remaining = tt - Elapsed(callStart);
            if (_end > _start && partialTimeout is { } pt)
            {
                if (_partialSince == 0) _partialSince = Stopwatch.GetTimestamp();
                var r = pt - Elapsed(_partialSince);
                if (remaining is not { } cur || r < cur) remaining = r;
            }
            int n;
            if (remaining is { } rem)
            {
                if (rem <= TimeSpan.Zero) throw new TimeoutException("read deadline");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(rem);
                try { n = await stream.ReadAsync(_buf.AsMemory(_end), linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("read deadline"); }
            }
            else n = await stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) return null;
            if (_end == _start) _partialSince = Stopwatch.GetTimestamp();
            _end += n;
        }
    }
}
