namespace FuelPace.Core.Laps;

/// <summary>Raw observations collected over one lap; <see cref="LapValidator"/> turns them into <see cref="LapReasons"/>.</summary>
public struct LapStats
{
    public bool PitSeen;
    public int IncidentsAtStart, IncidentsAtEnd;
    public double OffTrackSeconds;
    public bool CautionSeen, ResetSeen, RefuelSeen, StartObserved, EndObserved;
}
