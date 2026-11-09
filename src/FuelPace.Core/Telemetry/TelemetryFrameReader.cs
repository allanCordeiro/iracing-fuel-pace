using System.Buffers.Binary;

namespace FuelPace.Core.Telemetry;

/// <summary>Converts one raw record into a <see cref="TelemetryFrame"/>. Allocation free.</summary>
public static class TelemetryFrameReader
{
    public static void Read(ReadOnlySpan<byte> row, VarLayout layout, ref TelemetryFrame f)
    {
        f.SessionTime = ReadDouble(row, layout[TelemetryVarId.SessionTime]);
        f.SessionTimeRemain = ReadDouble(row, layout[TelemetryVarId.SessionTimeRemain]);
        f.SessionNum = ReadInt(row, layout[TelemetryVarId.SessionNum]);
        f.SessionState = ReadInt(row, layout[TelemetryVarId.SessionState]);
        f.SessionLapsRemainEx = ReadInt(row, layout[TelemetryVarId.SessionLapsRemainEx]);
        f.Lap = ReadInt(row, layout[TelemetryVarId.Lap]);
        f.LapCompleted = ReadInt(row, layout[TelemetryVarId.LapCompleted]);
        f.PlayerTrackSurface = ReadInt(row, layout[TelemetryVarId.PlayerTrackSurface]);
        f.PlayerCarMyIncidentCount = ReadInt(row, layout[TelemetryVarId.PlayerCarMyIncidentCount]);
        f.PlayerCarIdx = ReadInt(row, layout[TelemetryVarId.PlayerCarIdx]);
        f.TrackWetness = ReadInt(row, layout[TelemetryVarId.TrackWetness]);
        f.SessionFlags = ReadUInt(row, layout[TelemetryVarId.SessionFlags]);
        f.LapDistPct = ReadFloat(row, layout[TelemetryVarId.LapDistPct]);
        f.LapLastLapTime = ReadFloat(row, layout[TelemetryVarId.LapLastLapTime]);
        f.FuelLevel = ReadFloat(row, layout[TelemetryVarId.FuelLevel]);
        f.FuelLevelPct = ReadFloat(row, layout[TelemetryVarId.FuelLevelPct]);
        f.FuelUsePerHour = ReadFloat(row, layout[TelemetryVarId.FuelUsePerHour]);
        f.PlayerCarTowTime = ReadFloat(row, layout[TelemetryVarId.PlayerCarTowTime]);
        f.Speed = ReadFloat(row, layout[TelemetryVarId.Speed]);
        f.Throttle = ReadFloat(row, layout[TelemetryVarId.Throttle]);
        f.TrackTempCrew = ReadFloat(row, layout[TelemetryVarId.TrackTempCrew]);
        f.AirTemp = ReadFloat(row, layout[TelemetryVarId.AirTemp]);
        f.PlayerCarPowerAdjust = ReadFloat(row, layout[TelemetryVarId.PlayerCarPowerAdjust]);
        f.PlayerCarWeightPenalty = ReadFloat(row, layout[TelemetryVarId.PlayerCarWeightPenalty]);
        f.OnPitRoad = ReadBool(row, layout[TelemetryVarId.OnPitRoad]);
        f.PlayerCarInPitStall = ReadBool(row, layout[TelemetryVarId.PlayerCarInPitStall]);
        f.IsOnTrack = ReadBool(row, layout[TelemetryVarId.IsOnTrack]);
        f.IsReplayPlaying = ReadBool(row, layout[TelemetryVarId.IsReplayPlaying]);
        f.HasCarIdx = layout.HasCarIdx;

        for (int i = 0; i < CarArray<int>.Length; i++)
        {
            f.CarIdxPosition[i] = ReadInt(row, layout[TelemetryVarId.CarIdxPosition], i);
            f.CarIdxLapCompleted[i] = ReadInt(row, layout[TelemetryVarId.CarIdxLapCompleted], i);
            f.CarIdxTrackSurface[i] = ReadInt(row, layout[TelemetryVarId.CarIdxTrackSurface], i);
            f.CarIdxClass[i] = ReadInt(row, layout[TelemetryVarId.CarIdxClass], i);
            f.CarIdxLapDistPct[i] = ReadFloat(row, layout[TelemetryVarId.CarIdxLapDistPct], i);
            f.CarIdxLastLapTime[i] = ReadFloat(row, layout[TelemetryVarId.CarIdxLastLapTime], i);
            f.CarIdxOnPitRoad[i] = ReadBool(row, layout[TelemetryVarId.CarIdxOnPitRoad], i);
        }
    }

    private static bool Valid(VarSlot s, int index) => s.Offset >= 0 && (uint)index < (uint)s.Count;

    public static double ReadDouble(ReadOnlySpan<byte> row, VarSlot s, int index = 0)
    {
        if (!Valid(s, index)) return 0;
        var p = row[(s.Offset + index * s.Type.VarTypeSize())..];
        return s.Type switch
        {
            VarType.Double => BinaryPrimitives.ReadDoubleLittleEndian(p),
            VarType.Float => BinaryPrimitives.ReadSingleLittleEndian(p),
            VarType.Int => BinaryPrimitives.ReadInt32LittleEndian(p),
            VarType.BitField => BinaryPrimitives.ReadUInt32LittleEndian(p),
            _ => p[0],
        };
    }

    public static float ReadFloat(ReadOnlySpan<byte> row, VarSlot s, int index = 0)
    {
        if (!Valid(s, index)) return 0;
        if (s.Type == VarType.Float)
            return BinaryPrimitives.ReadSingleLittleEndian(row[(s.Offset + index * 4)..]);
        return (float)ReadDouble(row, s, index);
    }

    public static int ReadInt(ReadOnlySpan<byte> row, VarSlot s, int index = 0)
    {
        if (!Valid(s, index)) return 0;
        var p = row[(s.Offset + index * s.Type.VarTypeSize())..];
        return s.Type switch
        {
            VarType.Int => BinaryPrimitives.ReadInt32LittleEndian(p),
            VarType.BitField => unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(p)),
            VarType.Char or VarType.Bool => p[0],
            _ => (int)ReadDouble(row, s, index),
        };
    }

    public static uint ReadUInt(ReadOnlySpan<byte> row, VarSlot s, int index = 0)
    {
        if (!Valid(s, index)) return 0;
        var p = row[(s.Offset + index * s.Type.VarTypeSize())..];
        return s.Type switch
        {
            VarType.BitField or VarType.Int => BinaryPrimitives.ReadUInt32LittleEndian(p),
            VarType.Char or VarType.Bool => p[0],
            _ => (uint)ReadDouble(row, s, index),
        };
    }

    public static bool ReadBool(ReadOnlySpan<byte> row, VarSlot s, int index = 0)
        => Valid(s, index) && ReadDouble(row, s, index) != 0;
}
