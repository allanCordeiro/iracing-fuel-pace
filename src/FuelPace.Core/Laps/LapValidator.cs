namespace FuelPace.Core.Laps;

public static class LapValidator
{
    /// <summary>Yellow (0x0008), caution (0x4000) and caution waving (0x8000).</summary>
    public const uint CautionMask = 0x0008 | 0x4000 | 0x8000;
    public const double OffTrackLimitS = 0.5, RefuelThresholdL = 0.05;

    public static LapReasons Evaluate(in LapStats s)
    {
        var r = LapReasons.None;
        if (!s.StartObserved || !s.EndObserved) r |= LapReasons.Incomplete;
        return r;
    }
}
