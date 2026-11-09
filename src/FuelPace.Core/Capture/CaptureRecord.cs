namespace FuelPace.Core.Capture;

/// <summary>One capture: the laps recorded between Start and Stop, with the conditions at the start.</summary>
public sealed class CaptureRecord
{
    /// <summary>UTC start time, ISO 8601 to the second (<c>2026-10-29T20:03:40Z</c>).</summary>
    public required string Id { get; set; }
    public bool Active { get; set; } = true;
    public required string SessionType { get; set; }
    public required CaptureConditions Conditions { get; set; }
    public List<CapturedLap> Laps { get; set; } = [];
}
