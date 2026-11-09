namespace FuelPace.Core.Strategy;

/// <summary>Laps-remaining formulas for lap-limited races, timed races (leader-based finish), post-checkered and stints. Allocation-free.</summary>
public static class LapsRemainingCalculator
{
    /// <summary>Guards ceil() against crossing-exactly-at-zero floating point noise; a crossing exactly at the clock zero counts as one more lap.</summary>
    public const double CeilEpsilon = 1e-9;

    const int UnlimitedLaps = 32767;

    /// <summary>Laps remaining in a lap-limited session. NaN for unlimited (>= 32767) or negative <paramref name="sessionLapsRemainEx"/>.</summary>
    public static double ByLaps(int sessionLapsRemainEx, double myPct)
    {
        if (sessionLapsRemainEx < 0 || sessionLapsRemainEx >= UnlimitedLaps) return double.NaN;
        return sessionLapsRemainEx - myPct + 1;
    }

    /// <summary>
    /// Laps remaining in a timed race: the leader finishes the lap in progress when the clock reaches zero, and I finish the lap in
    /// progress when the leader's finish time is over. A non-positive or NaN leader pace (or NaN leader pct) falls back to my own;
    /// NaN <paramref name="timeRemainS"/> and a NaN or non-positive <paramref name="myPaceS"/> give NaN. A clock at or below zero is treated as zero.
    /// </summary>
    public static double ByTime(double timeRemainS, double leaderPct, double leaderPaceS, double myPct, double myPaceS)
    {
        if (double.IsNaN(timeRemainS) || !(myPaceS > 0) || double.IsNaN(myPct)) return double.NaN;
        double t = Math.Max(0, timeRemainS);
        if (!(leaderPaceS > 0) || double.IsNaN(leaderPct))
        {
            leaderPct = myPct;
            leaderPaceS = myPaceS;
        }
        double leaderLaps = Math.Ceiling(leaderPct + t / leaderPaceS + CeilEpsilon) - leaderPct;
        double finishS = leaderLaps * leaderPaceS;
        // -epsilon here: when my arrival coincides with the leader's finish (same pace), I finish on the leader's lap, not one later.
        return Math.Ceiling(myPct + finishS / myPaceS - CeilEpsilon) - myPct;
    }

    /// <summary>After the checkered flag: the rest of the current lap.</summary>
    public static double AfterCheckered(double myPct) => 1 - myPct;

    /// <summary>Laps left in a stint of <paramref name="stintLaps"/>, never negative.</summary>
    public static double Stint(int stintLaps, int lapsDone, double myPct) => Math.Max(0, stintLaps - lapsDone - myPct);
}
