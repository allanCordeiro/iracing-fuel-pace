using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Strategy;

public class OverlayStateTests
{
    static OverlayState Base() => new()
    {
        State = SignalState.Blue, FuelL = 77.61, AutonomyLaps = 34.04, AutonomyMin = 43.4, LapProjectionL = 2.384, LastLapL = 2.38,
        MeanL = 2.37, TargetL = 2.384, LapsRemaining = 30.04, FeasibleMinL = 2.2, LastLapTimeS = 79.514, MeanLapTimeS = 79.5,
        BaselineLapTimeS = 79.4, RecValid = 3, RecTotal = 5, Insight = new Insight(InsightKind.Slack, 1.21),
    };

    [Fact]
    public void Equal_when_display_is_equal()
    {
        var a = Base();
        var b = Base();
        b.FuelL = 77.64; b.TargetL = 2.381; b.LastLapTimeS = 79.512; b.AutonomyMin = 43.2; b.Insight = new Insight(InsightKind.Slack, 1.24);
        Assert.True(a.VisibleEquals(in b));
        a.LastLapL = double.NaN; b.LastLapL = double.NaN;
        Assert.True(a.VisibleEquals(in b));
    }

    [Fact]
    public void Different_when_display_changes()
    {
        var a = Base();
        Assert.False(StillEqual(a, (ref OverlayState s) => s.FuelL = 77.7));
        Assert.False(StillEqual(a, (ref OverlayState s) => s.State = SignalState.Red));
        Assert.False(StillEqual(a, (ref OverlayState s) => s.RecValid = 4));
    }

    delegate void Mut(ref OverlayState s);

    // True when the mutation is not visible on screen.
    static bool StillEqual(OverlayState a, Mut m)
    {
        var b = a;
        m(ref b);
        return a.VisibleEquals(in b);
    }

    [Fact]
    public void Every_visible_field_is_compared()
    {
        var a = Base();
        Mut[] muts =
        [
            (ref OverlayState s) => s.AutonomyLaps = 35.0, (ref OverlayState s) => s.AutonomyMin = 45,
            (ref OverlayState s) => s.LapProjectionL = 2.5, (ref OverlayState s) => s.LastLapL = 2.5,
            (ref OverlayState s) => s.MeanL = 2.5, (ref OverlayState s) => s.TargetL = double.NaN,
            (ref OverlayState s) => s.LapsRemaining = 31, (ref OverlayState s) => s.FeasibleMinL = 2.3,
            (ref OverlayState s) => s.LastLapTimeS = 80, (ref OverlayState s) => s.MeanLapTimeS = 80,
            (ref OverlayState s) => s.BaselineLapTimeS = 80, (ref OverlayState s) => s.RecTotal = 6,
            (ref OverlayState s) => s.LastLapInvalid = true, (ref OverlayState s) => s.MeanFromBaseline = true,
            (ref OverlayState s) => s.TargetIsEstimate = true, (ref OverlayState s) => s.IsStintMode = true,
            (ref OverlayState s) => s.IsPractice = true, (ref OverlayState s) => s.Recording = true,
            (ref OverlayState s) => s.BopMismatch = true,
            (ref OverlayState s) => s.Insight = new Insight(InsightKind.OnTarget, 1.21),
            (ref OverlayState s) => s.Insight = new Insight(InsightKind.Slack, 1.3),
        ];
        for (int k = 0; k < muts.Length; k++)
            Assert.False(StillEqual(a, muts[k]), $"mutation {k} not detected");
    }
}
