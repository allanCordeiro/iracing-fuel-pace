using System.Buffers.Binary;
using System.Text;
using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Support;

public static class IbtFileBuilder
{
    public static void Write(string path, string yaml, IReadOnlyList<VarSpec> vars, IEnumerable<double[]> rows, int tickRate = 60)
    {
        var yamlBytes = Encoding.Latin1.GetBytes(yaml + "\0");
        const int varHeaderSize = 144;
        int varHeaderOffset = 144;
        int yamlOffset = varHeaderOffset + vars.Count * varHeaderSize;
        int bufOffset = yamlOffset + yamlBytes.Length;

        var offsets = new int[vars.Count];
        int bufLen = 0;
        for (int i = 0; i < vars.Count; i++)
        {
            offsets[i] = bufLen;
            bufLen += VarTypeExt.VarTypeSize(vars[i].Type) * vars[i].Count;
        }

        var rowList = rows.ToList();
        var buf = new byte[bufOffset + rowList.Count * bufLen];
        var s = buf.AsSpan();

        // irsdk header (112 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(s[0..], 2);               // ver
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 1);               // status
        BinaryPrimitives.WriteInt32LittleEndian(s[8..], tickRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], 0);              // sessionInfoUpdate
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], yamlBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], yamlOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], vars.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], varHeaderOffset);
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], 1);              // numBuf
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], bufLen);
        BinaryPrimitives.WriteInt32LittleEndian(s[48..], 0);              // buf0.tickCount
        BinaryPrimitives.WriteInt32LittleEndian(s[52..], bufOffset);      // buf0.bufOffset

        // disk sub-header (32 bytes at 112)
        BinaryPrimitives.WriteInt64LittleEndian(s[112..], 0);
        BinaryPrimitives.WriteDoubleLittleEndian(s[120..], 0);
        BinaryPrimitives.WriteDoubleLittleEndian(s[128..], rowList.Count / (double)tickRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[136..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(s[140..], rowList.Count);

        for (int i = 0; i < vars.Count; i++)
        {
            var h = s[(varHeaderOffset + i * varHeaderSize)..];
            BinaryPrimitives.WriteInt32LittleEndian(h[0..], (int)vars[i].Type);
            BinaryPrimitives.WriteInt32LittleEndian(h[4..], offsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], vars[i].Count);
            Encoding.Latin1.GetBytes(vars[i].Name, h.Slice(16, 32));
            Encoding.Latin1.GetBytes(vars[i].Unit, h.Slice(112, 32));
        }

        yamlBytes.CopyTo(s[yamlOffset..]);

        for (int r = 0; r < rowList.Count; r++)
        {
            var row = s[(bufOffset + r * bufLen)..];
            int e = 0;
            for (int i = 0; i < vars.Count; i++)
            {
                int size = VarTypeExt.VarTypeSize(vars[i].Type);
                for (int k = 0; k < vars[i].Count; k++, e++)
                {
                    var dst = row[(offsets[i] + k * size)..];
                    double v = rowList[r][e];
                    switch (vars[i].Type)
                    {
                        case VarType.Char: case VarType.Bool: dst[0] = (byte)v; break;
                        case VarType.Int: BinaryPrimitives.WriteInt32LittleEndian(dst, (int)v); break;
                        case VarType.BitField: BinaryPrimitives.WriteInt32LittleEndian(dst, unchecked((int)(uint)v)); break;
                        case VarType.Float: BinaryPrimitives.WriteSingleLittleEndian(dst, (float)v); break;
                        case VarType.Double: BinaryPrimitives.WriteDoubleLittleEndian(dst, v); break;
                    }
                }
            }
        }

        File.WriteAllBytes(path, buf);
    }

    /// <summary>Writes the full <see cref="TelemetryVars.All"/> catalog (minus CarIdx* when asked) with native types.</summary>
    public static void WriteFrames(string path, string yaml, IEnumerable<TelemetryFrame> frames,
        bool includeCarIdx = true, string fuelUnit = "l")
    {
        var vars = new List<VarSpec>();
        var ids = new List<int>();
        for (int i = 0; i < TelemetryVars.All.Count; i++)
        {
            if (!includeCarIdx && TelemetryVars.IsCarIdx((TelemetryVarId)i)) continue;
            var spec = TelemetryVars.All[i];
            vars.Add(i == (int)TelemetryVarId.FuelLevel ? spec with { Unit = fuelUnit } : spec);
            ids.Add(i);
        }

        int width = vars.Sum(v => v.Count);
        var rows = frames.Select(f =>
        {
            var row = new double[width];
            int e = 0;
            foreach (int id in ids) e = Fill(f, (TelemetryVarId)id, row, e);
            return row;
        });
        Write(path, yaml, vars, rows);
    }

    private static int Fill(in TelemetryFrame f, TelemetryVarId id, double[] row, int e)
    {
        switch (id)
        {
            case TelemetryVarId.SessionTime: row[e++] = f.SessionTime; break;
            case TelemetryVarId.SessionTimeRemain: row[e++] = f.SessionTimeRemain; break;
            case TelemetryVarId.SessionNum: row[e++] = f.SessionNum; break;
            case TelemetryVarId.SessionState: row[e++] = f.SessionState; break;
            case TelemetryVarId.SessionLapsRemainEx: row[e++] = f.SessionLapsRemainEx; break;
            case TelemetryVarId.Lap: row[e++] = f.Lap; break;
            case TelemetryVarId.LapCompleted: row[e++] = f.LapCompleted; break;
            case TelemetryVarId.PlayerTrackSurface: row[e++] = f.PlayerTrackSurface; break;
            case TelemetryVarId.PlayerCarMyIncidentCount: row[e++] = f.PlayerCarMyIncidentCount; break;
            case TelemetryVarId.PlayerCarIdx: row[e++] = f.PlayerCarIdx; break;
            case TelemetryVarId.TrackWetness: row[e++] = f.TrackWetness; break;
            case TelemetryVarId.SessionFlags: row[e++] = f.SessionFlags; break;
            case TelemetryVarId.LapDistPct: row[e++] = f.LapDistPct; break;
            case TelemetryVarId.LapLastLapTime: row[e++] = f.LapLastLapTime; break;
            case TelemetryVarId.FuelLevel: row[e++] = f.FuelLevel; break;
            case TelemetryVarId.FuelLevelPct: row[e++] = f.FuelLevelPct; break;
            case TelemetryVarId.FuelUsePerHour: row[e++] = f.FuelUsePerHour; break;
            case TelemetryVarId.PlayerCarTowTime: row[e++] = f.PlayerCarTowTime; break;
            case TelemetryVarId.Speed: row[e++] = f.Speed; break;
            case TelemetryVarId.Throttle: row[e++] = f.Throttle; break;
            case TelemetryVarId.TrackTempCrew: row[e++] = f.TrackTempCrew; break;
            case TelemetryVarId.AirTemp: row[e++] = f.AirTemp; break;
            case TelemetryVarId.PlayerCarPowerAdjust: row[e++] = f.PlayerCarPowerAdjust; break;
            case TelemetryVarId.PlayerCarWeightPenalty: row[e++] = f.PlayerCarWeightPenalty; break;
            case TelemetryVarId.OnPitRoad: row[e++] = f.OnPitRoad ? 1 : 0; break;
            case TelemetryVarId.PlayerCarInPitStall: row[e++] = f.PlayerCarInPitStall ? 1 : 0; break;
            case TelemetryVarId.IsOnTrack: row[e++] = f.IsOnTrack ? 1 : 0; break;
            case TelemetryVarId.IsReplayPlaying: row[e++] = f.IsReplayPlaying ? 1 : 0; break;
            case TelemetryVarId.CarIdxPosition: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxPosition[i]; break;
            case TelemetryVarId.CarIdxLapCompleted: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxLapCompleted[i]; break;
            case TelemetryVarId.CarIdxTrackSurface: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxTrackSurface[i]; break;
            case TelemetryVarId.CarIdxClass: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxClass[i]; break;
            case TelemetryVarId.CarIdxLapDistPct: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxLapDistPct[i]; break;
            case TelemetryVarId.CarIdxLastLapTime: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxLastLapTime[i]; break;
            case TelemetryVarId.CarIdxOnPitRoad: for (int i = 0; i < 64; i++) row[e++] = f.CarIdxOnPitRoad[i] ? 1 : 0; break;
            default: throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
        return e;
    }
}
