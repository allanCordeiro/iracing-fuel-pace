namespace FuelPace.Tests.Support;

/// <summary>A clock frozen at <paramref name="now"/>.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
