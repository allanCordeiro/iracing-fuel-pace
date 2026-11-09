namespace FuelPace.Core.Laps;

public static class LapValidator
{
    /// <summary>Yellow (0x0008), caution (0x4000) and caution waving (0x8000).</summary>
    public const uint CautionMask = 0x0008 | 0x4000 | 0x8000;
    public const double OffTrackLimitS = 0.5, RefuelThresholdL = 0.05;

    public static LapReasons Evaluate(in LapStats s)
    {
        var r = LapReasons.None;
        if (s.PitSeen) r |= LapReasons.Pit;
        if (s.IncidentsAtEnd > s.IncidentsAtStart) r |= LapReasons.Incident;
        if (s.OffTrackSeconds > OffTrackLimitS) r |= LapReasons.OffTrack;
        if (s.CautionSeen) r |= LapReasons.Caution;
        if (s.ResetSeen) r |= LapReasons.Reset;
        if (s.RefuelSeen) r |= LapReasons.Refuel;
        if (!s.StartObserved || !s.EndObserved) r |= LapReasons.Incomplete;
        return r;
    }
}
