using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Live;

/// <summary>
/// Writes the iRacing shared-memory image (PRD §10.1) that <see cref="LiveSnapshotReader"/> reads: the 112-byte header, one
/// var header per <c>vars</c> entry, a session-info region of <c>yamlCapacity</c> bytes (latin-1, NUL-terminated) and
/// <c>numBuf</c> rotating var buffers. Used by the FakeSim and the tests, over a byte array or a mapped view.
/// <para>
/// Variables are matched to <see cref="TelemetryFrame"/> fields by iRacing name (<see cref="TelemetryVars.All"/>) and written in
/// the type the spec declares; a name outside the catalog stays zero. <see cref="WriteFrame"/> goes to the buffer after the
/// newest one: it first sets that buffer's <c>tickCount</c> to -1 (a reader copying it then sees the tick change and discards
/// the copy), writes the row, then publishes the next, always rising, tick. Unwritten buffers have tick -1, so a reader sees
/// no frame before the first <see cref="WriteFrame"/>. Allocation free after construction; one writer at a time.
/// </para>
/// <para>
/// This is more cooperative than iRacing, which (as far as the SDK shows) only publishes the new tick after the row: a reader
/// facing the real sim relies on the tick re-check alone, and a buffer rewritten while it is still the newest cannot happen
/// there either (the sim writes the one after it). Tests that pass against this writer therefore do not prove tolerance of
/// every interleaving the real sim allows; <c>LiveSnapshotReaderTests</c> cover the re-check itself.
/// </para>
/// </summary>
public sealed class IrsdkLiveImageWriter
{
    private const int VarHeaderSize = 144, VarHeaderOffset = 144, MaxBuf = 4, NoTick = -1;
    private const int StatusOffset = 4, SessionInfoUpdateOffset = 12;

    private readonly IReadOnlyList<VarSpec> _vars;
    private readonly int[] _ids;     // catalog index per var, or -1
    private readonly int[] _offsets; // row offset per var
    private readonly int _numBuf, _yamlCapacity, _yamlOffset, _bufStart, _bufLen;
    private int _newest = -1, _tick;

    public IrsdkLiveImageWriter(IReadOnlyList<VarSpec> vars, int numBuf = 3, int yamlCapacity = 1 << 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numBuf, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(numBuf, MaxBuf);
        ArgumentOutOfRangeException.ThrowIfLessThan(yamlCapacity, 1);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("the irsdk image is little-endian");
        _vars = vars;
        _numBuf = numBuf;
        _yamlCapacity = yamlCapacity;

        var catalog = TelemetryVars.All;
        _ids = new int[vars.Count];
        _offsets = new int[vars.Count];
        int len = 0;
        for (int i = 0; i < vars.Count; i++)
        {
            _ids[i] = -1;
            for (int k = 0; k < catalog.Count; k++)
                if (string.Equals(catalog[k].Name, vars[i].Name, StringComparison.Ordinal)) { _ids[i] = k; break; }
            _offsets[i] = len;
            len += VarTypeExt.VarTypeSize(vars[i].Type) * Math.Max(1, vars[i].Count);
        }
        _bufLen = len;
        _yamlOffset = VarHeaderOffset + vars.Count * VarHeaderSize;
        _bufStart = Align16(_yamlOffset + yamlCapacity);
        TotalSize = _bufStart + numBuf * _bufLen;
    }

    /// <summary>Bytes the image needs (the size of the mapping to create).</summary>
    public int TotalSize { get; }

    /// <summary>Clears the image and writes the header, the var headers and <paramref name="yaml"/> (<c>sessionInfoUpdate</c> 1).
    /// The image is left connected (<c>status</c> bit 0 set) with no frame yet.</summary>
    public void Initialize(Span<byte> image, string yaml)
    {
        if (image.Length < TotalSize) throw new ArgumentException($"the image needs {TotalSize} bytes", nameof(image));
        CheckYaml(yaml);
        var s = image[..TotalSize];
        s.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], 2);  // ver
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], 60); // tickRate
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], _yamlCapacity);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], _yamlOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], _vars.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], VarHeaderOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], _numBuf);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], _bufLen);
        for (int b = 0; b < _numBuf; b++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[(48 + 16 * b)..], NoTick);
            BinaryPrimitives.WriteInt32LittleEndian(s[(52 + 16 * b)..], _bufStart + b * _bufLen);
        }
        for (int i = 0; i < _vars.Count; i++)
        {
            var h = s[(VarHeaderOffset + i * VarHeaderSize)..];
            var v = _vars[i];
            BinaryPrimitives.WriteInt32LittleEndian(h[0..], (int)v.Type);
            BinaryPrimitives.WriteInt32LittleEndian(h[4..], _offsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], Math.Max(1, v.Count));
            Latin1(v.Name, h.Slice(16, 32));
            Latin1(v.Unit, h.Slice(112, 32));
        }
        WriteYaml(s, yaml);
        _newest = -1;
        _tick = 0;
        Volatile.Write(ref Int(s, SessionInfoUpdateOffset), 1);
        Volatile.Write(ref Int(s, StatusOffset), 1);
    }

    /// <summary>Rewrites the session info in place, then increments <c>sessionInfoUpdate</c> (as the sim does).</summary>
    public void UpdateSessionInfo(Span<byte> image, string yaml)
    {
        CheckYaml(yaml);
        WriteYaml(image, yaml);
        ref int counter = ref Int(image, SessionInfoUpdateOffset);
        Volatile.Write(ref counter, Volatile.Read(ref counter) + 1);
    }

    /// <summary>Writes <paramref name="f"/> into the next buffer and publishes it with the next tick.</summary>
    public void WriteFrame(Span<byte> image, in TelemetryFrame f)
    {
        int b = (_newest + 1) % _numBuf;
        ref int tick = ref Int(image, 48 + 16 * b);
        Volatile.Write(ref tick, NoTick);
        var row = image.Slice(_bufStart + b * _bufLen, _bufLen);
        for (int i = 0; i < _ids.Length; i++)
        {
            int id = _ids[i];
            if (id < 0) continue;
            var spec = _vars[i];
            var dst = row[_offsets[i]..];
            if (TelemetryVars.IsCarIdx((TelemetryVarId)id))
            {
                int n = Math.Min(Math.Max(1, spec.Count), CarArray<int>.Length);
                for (int c = 0; c < n; c++) Put(dst, spec.Type, c, CarValue(in f, (TelemetryVarId)id, c));
            }
            else Put(dst, spec.Type, 0, Value(in f, (TelemetryVarId)id));
        }
        _newest = b;
        Volatile.Write(ref tick, ++_tick);
    }

    /// <summary>Sets or clears bit 0 of <c>status</c> (the sim is in a session).</summary>
    public void SetConnected(Span<byte> image, bool on)
    {
        ref int status = ref Int(image, StatusOffset);
        int v = Volatile.Read(ref status);
        Volatile.Write(ref status, on ? v | 1 : v & ~1);
    }

    private void CheckYaml(string yaml)
    {
        if (Encoding.Latin1.GetByteCount(yaml) + 1 > _yamlCapacity)
            throw new ArgumentException($"session info does not fit the {_yamlCapacity}-byte region", nameof(yaml));
    }

    private void WriteYaml(Span<byte> image, string yaml)
    {
        var region = image.Slice(_yamlOffset, _yamlCapacity);
        int n = Encoding.Latin1.GetBytes(yaml, region);
        region[n] = 0;
    }

    private static void Latin1(string text, Span<byte> field)
    {
        int n = Math.Min(text.Length, field.Length - 1); // keep a NUL
        Encoding.Latin1.GetBytes(text.AsSpan(0, n), field);
    }

    private static int Align16(int v) => (v + 15) & ~15;

    private static ref int Int(Span<byte> image, int offset) =>
        ref Unsafe.As<byte, int>(ref MemoryMarshal.GetReference(image[offset..(offset + 4)]));

    private static void Put(Span<byte> dst, VarType type, int index, double v)
    {
        var p = dst[(index * VarTypeExt.VarTypeSize(type))..];
        switch (type)
        {
            case VarType.Double: BinaryPrimitives.WriteDoubleLittleEndian(p, v); break;
            case VarType.Float: BinaryPrimitives.WriteSingleLittleEndian(p, (float)v); break;
            case VarType.Int: BinaryPrimitives.WriteInt32LittleEndian(p, (int)v); break;
            case VarType.BitField: BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)v); break;
            default: p[0] = v != 0 ? (byte)1 : (byte)0; break; // Char, Bool
        }
    }

    private static double Value(in TelemetryFrame f, TelemetryVarId id) => id switch
    {
        TelemetryVarId.SessionTime => f.SessionTime,
        TelemetryVarId.SessionTimeRemain => f.SessionTimeRemain,
        TelemetryVarId.SessionNum => f.SessionNum,
        TelemetryVarId.SessionState => f.SessionState,
        TelemetryVarId.SessionLapsRemainEx => f.SessionLapsRemainEx,
        TelemetryVarId.Lap => f.Lap,
        TelemetryVarId.LapCompleted => f.LapCompleted,
        TelemetryVarId.PlayerTrackSurface => f.PlayerTrackSurface,
        TelemetryVarId.PlayerCarMyIncidentCount => f.PlayerCarMyIncidentCount,
        TelemetryVarId.PlayerCarIdx => f.PlayerCarIdx,
        TelemetryVarId.TrackWetness => f.TrackWetness,
        TelemetryVarId.SessionFlags => f.SessionFlags,
        TelemetryVarId.LapDistPct => f.LapDistPct,
        TelemetryVarId.LapLastLapTime => f.LapLastLapTime,
        TelemetryVarId.FuelLevel => f.FuelLevel,
        TelemetryVarId.FuelLevelPct => f.FuelLevelPct,
        TelemetryVarId.FuelUsePerHour => f.FuelUsePerHour,
        TelemetryVarId.PlayerCarTowTime => f.PlayerCarTowTime,
        TelemetryVarId.Speed => f.Speed,
        TelemetryVarId.Throttle => f.Throttle,
        TelemetryVarId.TrackTempCrew => f.TrackTempCrew,
        TelemetryVarId.AirTemp => f.AirTemp,
        TelemetryVarId.PlayerCarPowerAdjust => f.PlayerCarPowerAdjust,
        TelemetryVarId.PlayerCarWeightPenalty => f.PlayerCarWeightPenalty,
        TelemetryVarId.OnPitRoad => f.OnPitRoad ? 1 : 0,
        TelemetryVarId.PlayerCarInPitStall => f.PlayerCarInPitStall ? 1 : 0,
        TelemetryVarId.IsOnTrack => f.IsOnTrack ? 1 : 0,
        TelemetryVarId.IsReplayPlaying => f.IsReplayPlaying ? 1 : 0,
        _ => 0,
    };

    private static double CarValue(in TelemetryFrame f, TelemetryVarId id, int c) => id switch
    {
        TelemetryVarId.CarIdxPosition => f.CarIdxPosition[c],
        TelemetryVarId.CarIdxLapCompleted => f.CarIdxLapCompleted[c],
        TelemetryVarId.CarIdxTrackSurface => f.CarIdxTrackSurface[c],
        TelemetryVarId.CarIdxClass => f.CarIdxClass[c],
        TelemetryVarId.CarIdxLapDistPct => f.CarIdxLapDistPct[c],
        TelemetryVarId.CarIdxLastLapTime => f.CarIdxLastLapTime[c],
        TelemetryVarId.CarIdxOnPitRoad => f.CarIdxOnPitRoad[c] ? 1 : 0,
        _ => 0,
    };
}
