using System.Buffers.Binary;
using System.Text;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;
using Microsoft.Win32.SafeHandles;

namespace FuelPace.App.Recording;

/// <summary>
/// Appends frames to an <c>.ibt</c> file the Core's <see cref="IbtReader"/> reads unchanged: the irsdk header (PRD §10.1), the
/// 32-byte disk sub-header, one var header per <see cref="TelemetryVars.All"/> entry, a reserved YAML region (latin-1,
/// NUL-terminated, rewritten in place) and the fixed-size records. Every write goes straight to the file (no user-space
/// buffer), so a process crash loses nothing the OS already has; <see cref="Checkpoint"/> keeps the header's record count and
/// end time current, and a file whose count is still 0 is read by size (<see cref="IbtReader"/> fallback).
/// </summary>
public sealed class IbtStreamWriter : IDisposable
{
    private const int VarHeaderSize = 144, DiskHeaderOffset = IrsdkHeader.Size, VarHeaderOffset = 144;

    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly int _reserved, _yamlOffset, _bufOffset, _bufLen;
    private readonly int[] _offsets;
    private readonly byte[] _row;
    private readonly DiagLog _log;
    private readonly bool _includeCarIdx;
    private int _count, _infoVersion;
    private double _firstTime = double.NaN, _lastTime;
    private bool _disposed;

    public IbtStreamWriter(string path, int tickRate, int reservedYamlBytes = 1 << 20, DiagLog? log = null,
        bool includeCarIdx = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reservedYamlBytes, 2);
        _path = path;
        _reserved = reservedYamlBytes;
        _log = log ?? DiagLog.Null;
        _includeCarIdx = includeCarIdx;
        var all = TelemetryVars.All;
        _offsets = new int[all.Count];
        var ids = new List<int>(all.Count);
        int len = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (!includeCarIdx && TelemetryVars.IsCarIdx((TelemetryVarId)i)) { _offsets[i] = -1; continue; }
            _offsets[i] = len;
            ids.Add(i);
            len += VarTypeExt.VarTypeSize(all[i].Type) * all[i].Count;
        }
        _bufLen = len;
        _row = new byte[len];
        _yamlOffset = VarHeaderOffset + ids.Count * VarHeaderSize;
        _bufOffset = _yamlOffset + reservedYamlBytes;

        _handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        try
        {
            var head = new byte[_yamlOffset];
            var s = head.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(s[0..], 2);                 // ver
            BinaryPrimitives.WriteInt32LittleEndian(s[4..], 1);                 // status
            BinaryPrimitives.WriteInt32LittleEndian(s[8..], tickRate);
            BinaryPrimitives.WriteInt32LittleEndian(s[16..], reservedYamlBytes); // sessionInfoLen
            BinaryPrimitives.WriteInt32LittleEndian(s[20..], _yamlOffset);
            BinaryPrimitives.WriteInt32LittleEndian(s[24..], ids.Count);
            BinaryPrimitives.WriteInt32LittleEndian(s[28..], VarHeaderOffset);
            BinaryPrimitives.WriteInt32LittleEndian(s[32..], 1);                // numBuf
            BinaryPrimitives.WriteInt32LittleEndian(s[36..], _bufLen);
            BinaryPrimitives.WriteInt32LittleEndian(s[52..], _bufOffset);       // buf0.bufOffset
            BinaryPrimitives.WriteInt64LittleEndian(s[DiskHeaderOffset..], DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            for (int k = 0; k < ids.Count; k++)
            {
                int i = ids[k];
                var h = s[(VarHeaderOffset + k * VarHeaderSize)..];
                var spec = all[i];
                BinaryPrimitives.WriteInt32LittleEndian(h[0..], (int)spec.Type);
                BinaryPrimitives.WriteInt32LittleEndian(h[4..], _offsets[i]);
                BinaryPrimitives.WriteInt32LittleEndian(h[8..], spec.Count);
                Encoding.Latin1.GetBytes(spec.Name, h.Slice(16, 32));
                string unit = i == (int)TelemetryVarId.FuelLevel ? "l" : spec.Unit;
                Encoding.Latin1.GetBytes(unit, h.Slice(112, 32));
            }
            RandomAccess.Write(_handle, head, 0);
            RandomAccess.SetLength(_handle, _bufOffset); // the reserved YAML region reads as zeros
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public int RecordCount => _count;

    /// <summary>Rewrites the reserved region. A YAML that does not fit goes to <c>&lt;path&gt;.yaml</c> (and a <c>Warn</c> text) instead.</summary>
    public void WriteSessionInfo(string yaml)
    {
        var bytes = Encoding.Latin1.GetBytes(yaml + "\0");
        if (bytes.Length > _reserved)
        {
            _log.Text(DiagLevel.Warn, "record", $"session info ({bytes.Length} bytes) does not fit the reserved {_reserved}; saved beside the recording as {_path}.yaml");
            File.WriteAllText(_path + ".yaml", yaml, Encoding.Latin1);
            return;
        }
        RandomAccess.Write(_handle, bytes, _yamlOffset);
        Span<byte> v = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(v, ++_infoVersion);
        RandomAccess.Write(_handle, v, 12); // sessionInfoUpdate
    }

    public void Append(in TelemetryFrame f)
    {
        WriteRow(in f, _row);
        RandomAccess.Write(_handle, _row, _bufOffset + (long)_count * _bufLen);
        _count++;
        if (double.IsNaN(_firstTime)) _firstTime = f.SessionTime;
        _lastTime = f.SessionTime;
    }

    /// <summary>Updates the header's <c>RecordCount</c> and session times.</summary>
    public void Checkpoint()
    {
        if (_disposed) return;
        Span<byte> d = stackalloc byte[24];
        BinaryPrimitives.WriteDoubleLittleEndian(d, double.IsNaN(_firstTime) ? 0 : _firstTime);
        BinaryPrimitives.WriteDoubleLittleEndian(d[8..], _lastTime);
        BinaryPrimitives.WriteInt32LittleEndian(d[16..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(d[20..], _count);
        RandomAccess.Write(_handle, d, DiskHeaderOffset + 8);
        Span<byte> tick = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tick, _count > 0 ? _count - 1 : 0);
        RandomAccess.Write(_handle, tick, 48); // buf0.tickCount
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { Checkpoint(); }
        finally
        {
            _disposed = true;
            _handle.Dispose();
        }
    }

    private void WriteRow(in TelemetryFrame f, Span<byte> row)
    {
        var o = _offsets;
        BinaryPrimitives.WriteDoubleLittleEndian(row[o[(int)TelemetryVarId.SessionTime]..], f.SessionTime);
        BinaryPrimitives.WriteDoubleLittleEndian(row[o[(int)TelemetryVarId.SessionTimeRemain]..], f.SessionTimeRemain);
        I(row, o, TelemetryVarId.SessionNum, f.SessionNum);
        I(row, o, TelemetryVarId.SessionState, f.SessionState);
        I(row, o, TelemetryVarId.SessionLapsRemainEx, f.SessionLapsRemainEx);
        I(row, o, TelemetryVarId.Lap, f.Lap);
        I(row, o, TelemetryVarId.LapCompleted, f.LapCompleted);
        I(row, o, TelemetryVarId.PlayerTrackSurface, f.PlayerTrackSurface);
        I(row, o, TelemetryVarId.PlayerCarMyIncidentCount, f.PlayerCarMyIncidentCount);
        I(row, o, TelemetryVarId.PlayerCarIdx, f.PlayerCarIdx);
        I(row, o, TelemetryVarId.TrackWetness, f.TrackWetness);
        BinaryPrimitives.WriteUInt32LittleEndian(row[o[(int)TelemetryVarId.SessionFlags]..], f.SessionFlags);
        F(row, o, TelemetryVarId.LapDistPct, f.LapDistPct);
        F(row, o, TelemetryVarId.LapLastLapTime, f.LapLastLapTime);
        F(row, o, TelemetryVarId.FuelLevel, f.FuelLevel);
        F(row, o, TelemetryVarId.FuelLevelPct, f.FuelLevelPct);
        F(row, o, TelemetryVarId.FuelUsePerHour, f.FuelUsePerHour);
        F(row, o, TelemetryVarId.PlayerCarTowTime, f.PlayerCarTowTime);
        F(row, o, TelemetryVarId.Speed, f.Speed);
        F(row, o, TelemetryVarId.Throttle, f.Throttle);
        F(row, o, TelemetryVarId.TrackTempCrew, f.TrackTempCrew);
        F(row, o, TelemetryVarId.AirTemp, f.AirTemp);
        F(row, o, TelemetryVarId.PlayerCarPowerAdjust, f.PlayerCarPowerAdjust);
        F(row, o, TelemetryVarId.PlayerCarWeightPenalty, f.PlayerCarWeightPenalty);
        row[o[(int)TelemetryVarId.OnPitRoad]] = f.OnPitRoad ? (byte)1 : (byte)0;
        row[o[(int)TelemetryVarId.PlayerCarInPitStall]] = f.PlayerCarInPitStall ? (byte)1 : (byte)0;
        row[o[(int)TelemetryVarId.IsOnTrack]] = f.IsOnTrack ? (byte)1 : (byte)0;
        row[o[(int)TelemetryVarId.IsReplayPlaying]] = f.IsReplayPlaying ? (byte)1 : (byte)0;

        if (!_includeCarIdx) return;
        int pos = o[(int)TelemetryVarId.CarIdxPosition], lap = o[(int)TelemetryVarId.CarIdxLapCompleted],
            surf = o[(int)TelemetryVarId.CarIdxTrackSurface], cls = o[(int)TelemetryVarId.CarIdxClass],
            dist = o[(int)TelemetryVarId.CarIdxLapDistPct], last = o[(int)TelemetryVarId.CarIdxLastLapTime],
            pit = o[(int)TelemetryVarId.CarIdxOnPitRoad];
        for (int c = 0; c < CarArray<int>.Length; c++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(row[(pos + 4 * c)..], f.CarIdxPosition[c]);
            BinaryPrimitives.WriteInt32LittleEndian(row[(lap + 4 * c)..], f.CarIdxLapCompleted[c]);
            BinaryPrimitives.WriteInt32LittleEndian(row[(surf + 4 * c)..], f.CarIdxTrackSurface[c]);
            BinaryPrimitives.WriteInt32LittleEndian(row[(cls + 4 * c)..], f.CarIdxClass[c]);
            BinaryPrimitives.WriteSingleLittleEndian(row[(dist + 4 * c)..], f.CarIdxLapDistPct[c]);
            BinaryPrimitives.WriteSingleLittleEndian(row[(last + 4 * c)..], f.CarIdxLastLapTime[c]);
            row[pit + c] = f.CarIdxOnPitRoad[c] ? (byte)1 : (byte)0;
        }
    }

    private static void I(Span<byte> row, int[] o, TelemetryVarId id, int v) => BinaryPrimitives.WriteInt32LittleEndian(row[o[(int)id]..], v);
    private static void F(Span<byte> row, int[] o, TelemetryVarId id, float v) => BinaryPrimitives.WriteSingleLittleEndian(row[o[(int)id]..], v);
}
