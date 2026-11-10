using System.Text.Json;
using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Laps;
using FuelPace.Core.Storage;

namespace FuelPace.App.Tests.SettingsUi;

static class ProfileJson
{
    /// <summary>A profile document with one capture; each lap is (unused, lap, time, fuel, throttle, reasons bits).</summary>
    public static string Doc(string captureId, IEnumerable<(string? _, int Lap, double? Time, double Fuel, double Thr, int Reasons)> laps,
        bool active = true, string sessionType = "Practice", string? car = null)
    {
        var doc = new ProfileDocument
        {
            Car = new CarInfo("ligier", car ?? "Ligier JS P320", 100, 0.75),
            Track = new TrackInfo("zolder", "Zolder Circuit", "Alternate", 4.0),
        };
        doc.Captures.Add(new CaptureRecord
        {
            Id = captureId, Active = active, SessionType = sessionType,
            Conditions = new CaptureConditions(31.4, 24.0, 1, 0, 0, 100),
            Laps = laps.Select(l => new CapturedLap { Lap = l.Lap, TimeS = l.Time, FuelL = l.Fuel, FullThrottlePct = l.Thr, Reasons = (LapReasons)l.Reasons }).ToList(),
        });
        return JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument);
    }
}
