namespace FuelPace.App.Ipc;

/// <summary>Reads newline-delimited messages from a stream, capping the size of a single line.</summary>
internal sealed class IpcLineReader(Stream stream, int maxLineBytes)
{
    byte[] _buf = new byte[4096];
    int _start, _end;

    /// <summary>Next line (without the newline), or null at end of stream. Throws <see cref="InvalidDataException"/> when a line exceeds the cap.</summary>
    public async ValueTask<byte[]?> ReadLineAsync(CancellationToken ct)
    {
        int scanned = 0;
        while (true)
        {
            int nl = Array.IndexOf(_buf, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (nl >= 0)
            {
                var line = _buf.AsSpan(_start, nl - _start).ToArray();
                _start = nl + 1;
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
            int n = await stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) return null;
            _end += n;
        }
    }
}
