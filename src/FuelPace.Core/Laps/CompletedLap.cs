namespace FuelPace.Core.Laps;

/// <summary>One lap as emitted by <see cref="LapDetector"/>. Large struct: pass by <c>in</c>.</summary>
public struct CompletedLap
{
    public int LapNumber;
    public double StartTime, EndTime;
    /// <summary>Official lap time; NaN = no time.</summary>
    public double TimeS;
    public double MeasuredTimeS, FuelUsedL, FullThrottlePct;
    public LapReasons Reasons;
    public LapStats Stats;
    public bool HasProfile;
    /// <summary>Cumulative fuel fraction 0..1; meaningful only when <see cref="HasProfile"/>.</summary>
    public FuelProfile101 FuelProfile;
}
