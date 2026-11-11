namespace FuelPace.Core.Telemetry;

/// <summary>One member per telemetry variable the app uses. Order matches <see cref="TelemetryVars.All"/>.</summary>
public enum TelemetryVarId
{
    SessionTime, SessionTimeRemain,
    SessionNum, SessionState, SessionLapsRemainEx, Lap, LapCompleted, PlayerTrackSurface,
    PlayerCarMyIncidentCount, PlayerCarIdx, TrackWetness,
    SessionFlags,
    LapDistPct, LapLastLapTime, FuelLevel, FuelLevelPct, FuelUsePerHour, PlayerCarTowTime, Speed,
    Throttle, TrackTempCrew, AirTemp, PlayerCarPowerAdjust, PlayerCarWeightPenalty,
    OnPitRoad, PlayerCarInPitStall, IsOnTrack, IsReplayPlaying,
    CarIdxPosition, CarIdxLapCompleted, CarIdxTrackSurface, CarIdxClass,
    CarIdxLapDistPct, CarIdxLastLapTime,
    CarIdxOnPitRoad,
}

public static class TelemetryVars
{
    public const int CarCount = CarArray<int>.Length;

    /// <summary>iRacing name, native type and count of every variable, indexed by <see cref="TelemetryVarId"/>.</summary>
    public static IReadOnlyList<VarSpec> All { get; } =
    [
        new("SessionTime", VarType.Double), new("SessionTimeRemain", VarType.Double),
        new("SessionNum", VarType.Int), new("SessionState", VarType.Int), new("SessionLapsRemainEx", VarType.Int),
        new("Lap", VarType.Int), new("LapCompleted", VarType.Int), new("PlayerTrackSurface", VarType.Int),
        new("PlayerCarMyIncidentCount", VarType.Int), new("PlayerCarIdx", VarType.Int), new("TrackWetness", VarType.Int),
        new("SessionFlags", VarType.BitField),
        new("LapDistPct", VarType.Float), new("LapLastLapTime", VarType.Float), new("FuelLevel", VarType.Float),
        new("FuelLevelPct", VarType.Float), new("FuelUsePerHour", VarType.Float), new("PlayerCarTowTime", VarType.Float),
        new("Speed", VarType.Float), new("Throttle", VarType.Float), new("TrackTempCrew", VarType.Float),
        new("AirTemp", VarType.Float), new("PlayerCarPowerAdjust", VarType.Float), new("PlayerCarWeightPenalty", VarType.Float),
        new("OnPitRoad", VarType.Bool), new("PlayerCarInPitStall", VarType.Bool), new("IsOnTrack", VarType.Bool),
        new("IsReplayPlaying", VarType.Bool),
        new("CarIdxPosition", VarType.Int, CarCount), new("CarIdxLapCompleted", VarType.Int, CarCount),
        new("CarIdxTrackSurface", VarType.Int, CarCount), new("CarIdxClass", VarType.Int, CarCount),
        new("CarIdxLapDistPct", VarType.Float, CarCount), new("CarIdxLastLapTime", VarType.Float, CarCount),
        new("CarIdxOnPitRoad", VarType.Bool, CarCount),
    ];

    public static bool IsCarIdx(TelemetryVarId id) => id >= TelemetryVarId.CarIdxPosition;
}
