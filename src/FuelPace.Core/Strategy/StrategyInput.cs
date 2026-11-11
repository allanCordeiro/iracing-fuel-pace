namespace FuelPace.Core.Strategy;

/// <summary>Per-tick inputs to <see cref="FuelStrategyEngine"/>. A NaN double means "unavailable".</summary>
public struct StrategyInput
{
    public double SessionTime, FuelL, LapDistPct, LapProjectionL, LastLapL, StintMeanL, StintMeanLapS, LastLapTimeS,
        SessionTimeRemain, LeaderPaceS, LeaderLapDistPct, EstLapTimeS;
    public bool LastLapInvalid, Inactive, IsElectric, Caution, IsRaceSession, LeaderFinished, IAmLeader, BopMismatch, Recording;
    public int StintLapsDone, SessionLapsRemainEx, RecValid, RecTotal;
    public RaceKind Race;
}
