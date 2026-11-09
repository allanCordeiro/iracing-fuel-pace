using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Laps;
using FuelPace.Core.Storage;

namespace FuelPace.Tests.Storage;

public class ProfileOperationsTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T1 = T0.AddMinutes(5);

    static readonly (int lap, double fuel, double time)[] Six =
    [(18, 2.382, 80.897), (19, 2.404, 80.272), (20, 2.373, 79.964), (21, 2.403, 79.514), (22, 2.389, 79.134), (23, 2.289, 81.062)];

    static CaptureRecord Rec(string id, double power = 0, double kg = 0, double tank = 100, bool active = true,
        (int lap, double fuel, double time)[]? laps = null) => new()
    {
        Id = id, Active = active, SessionType = "Practice",
        Conditions = new CaptureConditions(30, 20, 1, power, kg, tank),
        Laps = (laps ?? Six).Select(l => new CapturedLap { Lap = l.lap, TimeS = l.time, FuelL = l.fuel, FullThrottlePct = 64 }).ToList(),
    };

    static ProfileDocument Doc() => new()
    {
        Car = new CarInfo("car", "Car", 100, 0.75),
        Track = new TrackInfo("track", "Track", "", 4),
    };

    [Fact]
    public void Excluding_a_lap_marks_manual_and_recomputes_baseline()
    {
        var doc = Doc();
        ProfileOperations.AddCapture(doc, Rec("a"), T0);
        Assert.Equal(6, doc.Baseline!.ValidLaps);

        ProfileOperations.SetLapExcluded(doc, "a", 23, true, T1);
        Assert.NotEqual(LapReasons.None, doc.Captures[0].Laps[5].Reasons & LapReasons.Manual);
        Assert.Equal(5, doc.Baseline!.ValidLaps);
        Assert.Equal(2.373, doc.Baseline.FuelMinL, 3);
        Assert.Equal(T1, doc.Baseline.ComputedAt);

        ProfileOperations.SetLapExcluded(doc, "a", 23, false, T1);
        Assert.Equal(LapReasons.None, doc.Captures[0].Laps[5].Reasons);
        Assert.Equal(6, doc.Baseline!.ValidLaps);
        Assert.Equal(2.289, doc.Baseline.FuelMinL, 3);
    }

    [Fact]
    public void Inactive_capture_is_left_out_of_baseline()
    {
        var doc = Doc();
        ProfileOperations.AddCapture(doc, Rec("a"), T0);
        ProfileOperations.AddCapture(doc, Rec("b", laps: [(1, 3.0, 90), (2, 3.0, 90), (3, 3.0, 90)]), T0);
        Assert.Equal(9, doc.Baseline!.ValidLaps);
        ProfileOperations.SetCaptureActive(doc, "b", false, T1);
        Assert.Equal(6, doc.Baseline!.ValidLaps);
        Assert.Equal(2.289, doc.Baseline.FuelMinL, 3);
        ProfileOperations.SetCaptureActive(doc, "b", true, T1);
        Assert.Equal(9, doc.Baseline!.ValidLaps);
    }

    [Fact]
    public void Deleting_last_capture_clears_baseline()
    {
        var doc = Doc();
        ProfileOperations.AddCapture(doc, Rec("a"), T0);
        Assert.NotNull(doc.Baseline);
        ProfileOperations.DeleteCapture(doc, "a", T1);
        Assert.Empty(doc.Captures);
        Assert.Null(doc.Baseline);
    }

    [Fact]
    public void Baseline_conditions_list_distinct_bop_of_active_captures()
    {
        var doc = Doc();
        ProfileOperations.AddCapture(doc, Rec("a", power: -1.5, kg: 10), T0);
        ProfileOperations.AddCapture(doc, Rec("b", power: -1.5, kg: 10), T0);
        ProfileOperations.AddCapture(doc, Rec("c", power: 0, kg: 0, tank: 90), T0);
        ProfileOperations.AddCapture(doc, Rec("d", power: 3, kg: 5, active: false), T0);
        var c = doc.Baseline!.Conditions;
        Assert.Equal(2, c.Count);
        Assert.Contains(new BopSignature(-1.5, 10, 100), c);
        Assert.Contains(new BopSignature(0, 0, 90), c);
    }

    [Fact]
    public void Recompute_reapplies_outlier_rule_but_keeps_manual()
    {
        var doc = Doc();
        var rec = Rec("a", laps: [(1, 2.4, 80), (2, 2.4, 80.5), (3, 2.4, 79.5), (4, 2.4, 100)]);
        ProfileOperations.AddCapture(doc, rec, T0);
        Assert.NotEqual(LapReasons.None, rec.Laps[3].Reasons & LapReasons.Outlier);
        Assert.Equal(3, doc.Baseline!.ValidLaps);
    }

    [Fact]
    public void Unknown_capture_throws()
    {
        Assert.Throws<ArgumentException>(() => ProfileOperations.SetCaptureActive(Doc(), "x", false, T0));
    }
}
