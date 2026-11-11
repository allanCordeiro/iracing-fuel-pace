using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Laps;

namespace FuelPace.Core.Storage;

/// <summary>Edits to a profile; each one ends by recomputing the baseline. Pure, no I/O.</summary>
public static class ProfileOperations
{
    public static void AddCapture(ProfileDocument doc, CaptureRecord rec, DateTimeOffset now)
    {
        doc.Captures.Add(rec);
        RecomputeBaseline(doc, now);
    }

    public static void SetLapExcluded(ProfileDocument doc, string captureId, int lap, bool excluded, DateTimeOffset now)
    {
        var l = Find(doc, captureId).Laps.Find(x => x.Lap == lap)
            ?? throw new ArgumentException($"lap {lap} not in capture {captureId}", nameof(lap));
        if (excluded) l.Reasons |= LapReasons.Manual;
        else l.Reasons &= ~LapReasons.Manual;
        RecomputeBaseline(doc, now);
    }

    public static void SetCaptureActive(ProfileDocument doc, string captureId, bool active, DateTimeOffset now)
    {
        Find(doc, captureId).Active = active;
        RecomputeBaseline(doc, now);
    }

    public static void DeleteCapture(ProfileDocument doc, string captureId, DateTimeOffset now)
    {
        doc.Captures.Remove(Find(doc, captureId));
        RecomputeBaseline(doc, now);
    }

    /// <summary>Re-applies the outlier rule to every capture (keeping manual exclusions), then aggregates the valid laps of the active ones.</summary>
    public static void RecomputeBaseline(ProfileDocument doc, DateTimeOffset now)
    {
        var samples = new List<LapSample>();
        var conditions = new List<BopSignature>();
        foreach (var c in doc.Captures)
        {
            OutlierRule.Apply(c.Laps);
            if (!c.Active) continue;
            var bop = new BopSignature(c.Conditions.PowerAdjustPct, c.Conditions.WeightPenaltyKg, c.Conditions.TankCapacityL);
            if (!conditions.Contains(bop)) conditions.Add(bop);
            foreach (var l in c.Laps)
                if (l.Valid && l.TimeS is { } t)
                    samples.Add(new LapSample(l.FuelL, t, l.FullThrottlePct, l.FuelProfile));
        }
        doc.Baseline = BaselineCalculator.Compute(samples, doc.Car.TankCapacityL, conditions, now);
    }

    private static CaptureRecord Find(ProfileDocument doc, string id) =>
        doc.Captures.Find(c => c.Id == id) ?? throw new ArgumentException($"capture {id} not found", nameof(id));
}
