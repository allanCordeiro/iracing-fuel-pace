namespace FuelPace.Core.Telemetry;

/// <summary>One telemetry tick. Plain struct, reused by the caller; fields absent from the source are zeroed.</summary>
public struct TelemetryFrame
{
    public double SessionTime, SessionTimeRemain;
    public int SessionNum, SessionState, SessionLapsRemainEx, Lap, LapCompleted, PlayerTrackSurface,
        PlayerCarMyIncidentCount, PlayerCarIdx, TrackWetness;
    public uint SessionFlags;
    public float LapDistPct, LapLastLapTime, FuelLevel, FuelLevelPct, FuelUsePerHour, PlayerCarTowTime, Speed,
        Throttle, TrackTempCrew, AirTemp, PlayerCarPowerAdjust, PlayerCarWeightPenalty;
    public bool OnPitRoad, PlayerCarInPitStall, IsOnTrack, IsReplayPlaying, HasCarIdx;
    public CarArray<int> CarIdxPosition, CarIdxLapCompleted, CarIdxTrackSurface, CarIdxClass;
    public CarArray<float> CarIdxLapDistPct, CarIdxLastLapTime;
    public CarArray<bool> CarIdxOnPitRoad;
}
