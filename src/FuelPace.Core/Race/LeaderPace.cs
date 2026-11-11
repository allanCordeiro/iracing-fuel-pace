namespace FuelPace.Core.Race;

public enum LeaderPaceSource : byte { None, Mean, Best102, Held }

/// <summary>PaceS is NaN when there is no data; LeaderCarIdx is -1 when there is no leader.</summary>
public readonly record struct LeaderPace(int LeaderCarIdx, double PaceS, double LeaderLapDistPct, LeaderPaceSource Source, bool Frozen)
{
    public static readonly LeaderPace None = new(-1, double.NaN, double.NaN, LeaderPaceSource.None, false);
}
