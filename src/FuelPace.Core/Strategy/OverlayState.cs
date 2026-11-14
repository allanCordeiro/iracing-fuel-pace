namespace FuelPace.Core.Strategy;

/// <summary>Signal states of §7.4 plus <see cref="NoTarget"/> (decision 8).</summary>
public enum SignalState : byte { Inactive, NoBaseline, NoTarget, Blue, Red, NotFeasible }

/// <summary>What the strategy engine computed; the v3 overlay shows it through <see cref="Display.GlanceViewBuilder"/>.
/// A NaN double is "unknown" ("—" on screen).</summary>
public struct OverlayState
{
    public SignalState State;
    public double FuelL, AutonomyLaps, AutonomyMin, LapProjectionL, LastLapL, MeanL, TargetL, LapsRemaining, FeasibleMinL,
        LastLapTimeS, MeanLapTimeS, BaselineLapTimeS;

    /// <summary>PRD v3: exposed for the GlanceView builder, no new math. The pace the autonomy minutes use (stint mean,
    /// else baseline mean, else the car's estimate; s/lap) and the baseline's fuel-vs-time slope B (NaN without a fit).</summary>
    public double PaceS, FuelVsTimeB;
    public bool LastLapInvalid, MeanFromBaseline, TargetIsEstimate, IsStintMode, IsPractice, Recording, BopMismatch;
    public int RecValid, RecTotal;
    /// <summary>The §7.5 insight as values, for the <c>InsightChanged</c> diagnostic (the v3 overlay has no insight line).</summary>
    public Insight Insight;
}
