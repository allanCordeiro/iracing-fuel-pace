using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Storage;

public sealed record CarInfo(string Path, string Name, double TankCapacityL, double FuelKgPerL);

public sealed record TrackInfo(string Name, string Display, string Config, double LengthKm);

/// <summary>One car/track profile: every capture plus the baseline derived from the active ones.</summary>
public sealed class ProfileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public required CarInfo Car { get; set; }
    public required TrackInfo Track { get; set; }
    public List<CaptureRecord> Captures { get; set; } = [];
    public Baseline? Baseline { get; set; }

    public static ProfileDocument CreateFor(SessionInfo info) => new()
    {
        Car = new CarInfo(info.CarPath, info.CarScreenName, info.TankCapacityL, info.FuelKgPerL),
        Track = new TrackInfo(info.TrackName, info.TrackDisplayName, info.TrackConfigName, info.TrackLengthKm),
    };
}
