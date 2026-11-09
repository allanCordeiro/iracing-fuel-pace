namespace FuelPace.Core.Strategy;

/// <summary>Holds the signal colour until a new state persists for <see cref="HoldSeconds"/> (§7.4).</summary>
public struct SignalHysteresis
{
    public const double HoldSeconds = 1.5;

    /// <summary>The state to display for <paramref name="raw"/> at session time <paramref name="now"/>.</summary>
    // Stub: Task 13 implements the hold.
    public SignalState Apply(SignalState raw, double now) => raw;
}
