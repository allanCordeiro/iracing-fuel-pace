namespace FuelPace.Core.Strategy;

/// <summary>How the laps remaining are counted: from the session (auto) or a fixed stint length.</summary>
public enum LapsMode { Auto, Stint }

/// <summary>Session limit as read from the session YAML (decision 10).</summary>
public enum RaceKind : byte { Unlimited, Laps, Time }

/// <summary>User-tunable strategy parameters (CFG-06). Defaults per PRD §7.</summary>
public sealed record StrategyConfig
{
    public const int MinMeanWindow = 2;
    public const int MaxMeanWindow = 16;

    readonly int _meanWindow = 5;

    /// <summary>Fuel that never enters the calculation (L).</summary>
    public double ReserveL { get; init; } = 0.5;

    /// <summary>N of the moving average (2–16).</summary>
    public int MeanWindow
    {
        get => _meanWindow;
        init => _meanWindow = value is >= MinMeanWindow and <= MaxMeanWindow
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MeanWindow), value, "MeanWindow must be between 2 and 16.");
    }

    /// <summary><c>economia_max</c>: the largest sustainable saving as a fraction of the baseline mean.</summary>
    public double MaxSavingFraction { get; init; } = 0.06;

    /// <summary>Safety laps added in auto mode.</summary>
    public double ExtraLaps { get; init; }

    public LapsMode Mode { get; init; } = LapsMode.Auto;

    public int StintLaps { get; init; } = 25;

    /// <summary>In a non-race timed session, derive the laps from the time left; otherwise it has no target.</summary>
    public bool PracticeLapsFromTime { get; init; } = true;
}
