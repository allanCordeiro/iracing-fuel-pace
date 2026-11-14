namespace FuelPace.Core.Strategy;

/// <summary>Signal states of §7.4 plus <see cref="NoTarget"/> (decision 8).</summary>
public enum SignalState : byte { Inactive, NoBaseline, NoTarget, Blue, Red, NotFeasible }

/// <summary>Everything the overlay shows. A NaN double is displayed as "—".</summary>
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
    public Insight Insight;

    /// <summary>True when both states look identical on screen (values rounded as displayed, NaN == NaN).</summary>
    public readonly bool VisibleEquals(in OverlayState o) =>
        State == o.State
        && LastLapInvalid == o.LastLapInvalid && MeanFromBaseline == o.MeanFromBaseline && TargetIsEstimate == o.TargetIsEstimate
        && IsStintMode == o.IsStintMode && IsPractice == o.IsPractice && Recording == o.Recording && BopMismatch == o.BopMismatch
        && RecValid == o.RecValid && RecTotal == o.RecTotal
        && Same(FuelL, o.FuelL, 1) && Same(AutonomyLaps, o.AutonomyLaps, 1) && Same(AutonomyMin, o.AutonomyMin, 0)
        && Same(LapProjectionL, o.LapProjectionL, 2) && Same(LastLapL, o.LastLapL, 2) && Same(MeanL, o.MeanL, 2)
        && Same(TargetL, o.TargetL, 2) && Same(LapsRemaining, o.LapsRemaining, 1) && Same(FeasibleMinL, o.FeasibleMinL, 2)
        && Same(LastLapTimeS, o.LastLapTimeS, 2) && Same(MeanLapTimeS, o.MeanLapTimeS, 2) && Same(BaselineLapTimeS, o.BaselineLapTimeS, 2)
        && InsightEquals(Insight, o.Insight);

    static bool InsightEquals(in Insight a, in Insight b)
    {
        if (a.Kind != b.Kind) return false;
        // Same digits as OverlayText.Insight prints: Save shows L/lap with 2 decimals, the others liters with 1.
        int aDigits = a.Kind == InsightKind.Save ? 2 : 1;
        return Same(a.A, b.A, aDigits) && Same(a.B, b.B, 0) && Same(a.C, b.C, 1);
    }

    static bool Same(double a, double b, int digits) =>
        double.IsNaN(a) ? double.IsNaN(b)
        : !double.IsNaN(b) && Math.Round(a, digits, MidpointRounding.AwayFromZero) == Math.Round(b, digits, MidpointRounding.AwayFromZero);
}
