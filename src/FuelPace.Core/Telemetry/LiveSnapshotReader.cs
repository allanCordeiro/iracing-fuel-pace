using System.Buffers.Binary;
using System.Text;
using FuelPace.Core.Diagnostics;

namespace FuelPace.Core.Telemetry;

public enum LiveReadStatus { Frame, NotConnected, NoNewData, TornRead, LayoutChanged }

/// <summary>
/// The portable half of the live iRacing reader: everything that can be done on a plain span over the shared memory
/// (<c>Local\IRSDKMemMapFileName</c>) without OS handles. The caller (the App, Windows only) maps the file, waits on
/// the data-valid event and passes <c>new ReadOnlySpan&lt;byte&gt;(ptr, size)</c> on every wake-up.
/// <para>
/// Protocol: <c>status &amp; 1</c> means connected; the header has <c>numBuf</c> rotating var buffers (usually 3) and the one
/// with the highest <c>tickCount</c> is the newest. The producer may rotate into a buffer while we copy it, so the row is
/// copied into a pre-allocated array and the buffer's <c>tickCount</c> is re-read afterwards: if it changed the copy is
/// discarded (<see cref="LiveReadStatus.TornRead"/>).
/// </para>
/// <para>Threading: <see cref="TryRead"/> is for the telemetry thread only. <see cref="ReadSessionInfoYaml"/> touches no
/// instance state and may run on another thread concurrently.</para>
/// </summary>
public sealed class LiveSnapshotReader
{
    private const int MaxBuf = 4; // the header has room for four VarBufInfo entries

    private readonly byte[] _row;
    private readonly DiagLog _log;

    private bool _haveLayout;
    private int _numVars, _varHeaderOffset, _bufLen;
    private int _lastTick = -1;
    private int _tornCount;

    public LiveSnapshotReader(int maxBufLen = 64 * 1024, DiagLog? log = null)
    {
        _row = new byte[maxBufLen];
        _log = log ?? DiagLog.Null;
    }

    /// <summary>Layout of the current connection; null until the first connected read.</summary>
    public VarLayout? Layout { get; private set; }

    /// <summary>The header's <c>sessionInfoUpdate</c> counter as of the last <see cref="TryRead"/> that parsed a header.</summary>
    public int SessionInfoUpdate { get; private set; }

    /// <summary>Test hook, called between the row copy and the tickCount re-check.</summary>
    public Action? AfterCopyForTests { get; set; }

    /// <summary>
    /// Reads the newest row into <paramref name="frame"/> (touched only when <see cref="LiveReadStatus.Frame"/>). Memory that
    /// is too short or whose header is inconsistent (bad <c>numBuf</c>, <c>bufLen</c> above the maximum, offsets outside the
    /// span) counts as not connected. Allocation free except when the layout is (re)resolved.
    /// </summary>
    public LiveReadStatus TryRead(ReadOnlySpan<byte> memory, ref TelemetryFrame frame)
    {
        if (memory.Length < IrsdkHeader.Size) return Disconnect();
        var h = IrsdkHeader.Parse(memory);
        SessionInfoUpdate = h.SessionInfoUpdate;
        if ((h.Status & 1) == 0) return Disconnect();
        if (h.NumBuf < 1 || h.NumBuf > MaxBuf || h.BufLen <= 0 || h.BufLen > _row.Length) return Disconnect();

        if (!_haveLayout || h.NumVars != _numVars || h.VarHeaderOffset != _varHeaderOffset || h.BufLen != _bufLen)
        {
            long end = (long)h.VarHeaderOffset + (long)h.NumVars * VarHeader.Size;
            if (h.NumVars < 0 || h.VarHeaderOffset < 0 || end > memory.Length) return Disconnect();
            Layout = VarLayout.Resolve(VarHeader.ParseAll(memory[h.VarHeaderOffset..], h.NumVars));
            _numVars = h.NumVars;
            _varHeaderOffset = h.VarHeaderOffset;
            _bufLen = h.BufLen;
            _haveLayout = true;
            _lastTick = -1;
            return LiveReadStatus.LayoutChanged;
        }

        int best = 0, bestTick = TickCount(memory, 0);
        for (int i = 1; i < h.NumBuf; i++)
        {
            int t = TickCount(memory, i);
            if (t > bestTick) { best = i; bestTick = t; }
        }
        if (bestTick == _lastTick) return LiveReadStatus.NoNewData;

        int offset = BinaryPrimitives.ReadInt32LittleEndian(memory[(BufInfo(best) + 4)..]);
        if (offset < 0 || (long)offset + _bufLen > memory.Length) return Disconnect();
        memory.Slice(offset, _bufLen).CopyTo(_row);
        AfterCopyForTests?.Invoke();
        if (TickCount(memory, best) != bestTick)
        {
            _tornCount++;
            _log.Emit(DiagLevel.Debug, DiagEvent.TornRead, 0, _tornCount);
            return LiveReadStatus.TornRead;
        }

        _lastTick = bestTick;
        TelemetryFrameReader.Read(_row.AsSpan(0, _bufLen), Layout!, ref frame);
        return LiveReadStatus.Frame;
    }

    /// <summary>
    /// Reads the session-info YAML (latin-1, NUL-terminated within <c>sessionInfoLen</c>). Reads only the header and the YAML
    /// region and uses no instance state, so it is safe to call from a background thread while <see cref="TryRead"/> runs on
    /// the telemetry thread. Returns an empty string when the memory is too short. The sim rewrites the region in place when
    /// <see cref="SessionInfoUpdate"/> increments, so callers that need consistency compare the counter before and after.
    /// </summary>
    public string ReadSessionInfoYaml(ReadOnlySpan<byte> memory)
    {
        if (memory.Length < IrsdkHeader.Size) return "";
        var h = IrsdkHeader.Parse(memory);
        if (h.SessionInfoLen <= 0 || h.SessionInfoOffset < 0 || (long)h.SessionInfoOffset + h.SessionInfoLen > memory.Length) return "";
        var region = memory.Slice(h.SessionInfoOffset, h.SessionInfoLen);
        int nul = region.IndexOf((byte)0);
        return Encoding.Latin1.GetString(nul >= 0 ? region[..nul] : region);
    }

    private LiveReadStatus Disconnect()
    {
        _haveLayout = false;
        _lastTick = -1;
        return LiveReadStatus.NotConnected;
    }

    private static int BufInfo(int i) => 48 + 16 * i;

    private static int TickCount(ReadOnlySpan<byte> memory, int i) => BinaryPrimitives.ReadInt32LittleEndian(memory[BufInfo(i)..]);
}
