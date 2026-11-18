namespace FuelPace.Core.Laps;

public static class LapValidator
{
    /// <summary>Yellow (0x0008), caution (0x4000) and caution waving (0x8000).</summary>
    public const uint CautionMask = 0x0008 | 0x4000 | 0x8000;
    public const double RefuelThresholdL = 0.05;

    /// <summary>
    /// A lap is only invalid when iRacing would not count it either. <see cref="LapStats.OffTrackSeconds"/> is not a
    /// reason: <c>PlayerTrackSurface</c> reads OffTrack on kerbs and run-off that iRacing tolerates (Monza, feedback
    /// 2026-11-18), and an off iRacing punishes shows up as an incident. <see cref="LapReasons.OffTrack"/> stays only
    /// to read old profiles (<see cref="Storage.ProfileStore"/> drops it on load).
    /// </summary>
    public static LapReasons Evaluate(in LapStats s)
    {
        var r = LapReasons.None;
        if (s.PitSeen) r |= LapReasons.Pit;
        if (s.IncidentsAtEnd > s.IncidentsAtStart) r |= LapReasons.Incident;
        if (s.CautionSeen) r |= LapReasons.Caution;
        if (s.ResetSeen) r |= LapReasons.Reset;
        if (s.RefuelSeen) r |= LapReasons.Refuel;
        if (!s.StartObserved || !s.EndObserved) r |= LapReasons.Incomplete;
        return r;
    }
}
