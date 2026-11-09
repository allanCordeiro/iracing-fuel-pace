namespace FuelPace.Core.Strategy;

/// <summary>Signal states of §7.4 plus <see cref="NoTarget"/> (decision 8).</summary>
public enum SignalState : byte { Inactive, NoBaseline, NoTarget, Blue, Red, NotFeasible }

/// <summary>Everything the overlay shows. A NaN double is displayed as "—".</summary>
public struct OverlayState
{
    public SignalState State;
    public double FuelL, AutonomyLaps, AutonomyMin, LapProjectionL, LastLapL, MeanL, TargetL, LapsRemaining, FeasibleMinL,
        LastLapTimeS, MeanLapTimeS, BaselineLapTimeS;
    public bool LastLapInvalid, MeanFromBaseline, TargetIsEstimate, IsStintMode, IsPractice, Recording, BopMismatch;
    public int RecValid, RecTotal;
    public Insight Insight;
}
