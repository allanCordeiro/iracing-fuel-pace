using FuelPace.Core.Display;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Pipeline;

/// <summary>PRD v3 §8.4: the redraw dirty flag is the GlanceView compared by value.</summary>
public class OverlayStatePublisherTests
{
    static readonly GlanceConfig Cfg = GlanceConfig.Default;

    static OverlayState Blue(double fuel, double autonomyMin) => new()
    {
        State = SignalState.Blue, FuelL = fuel, LapsRemaining = 30, LapProjectionL = 2.36, LastLapL = 2.37, MeanL = 2.37,
        TargetL = 2.45, FeasibleMinL = 2.23, AutonomyLaps = 31, AutonomyMin = autonomyMin, PaceS = 80,
        LastLapTimeS = double.NaN, MeanLapTimeS = double.NaN, BaselineLapTimeS = double.NaN, FuelVsTimeB = double.NaN,
    };

    static void Publish(OverlayStatePublisher p, in OverlayState s) => p.Publish(in s, GlanceViewBuilder.Build(in s, in Cfg));

    [Fact]
    public void Version_moves_only_when_the_view_changes_by_value()
    {
        var p = new OverlayStatePublisher();
        Assert.Equal(0, p.Version);
        Publish(p, Blue(74.0, 41));
        Assert.Equal(1, p.Version); // the first publish counts

        // The engine state changed (autonomy minutes, fuel below the displayed decimal) but the v3 panel did not.
        Publish(p, Blue(74.04, 43));
        Assert.Equal(1, p.Version);
        Assert.Equal(2, p.PublishCount);
        Assert.Equal(43, p.Read().AutonomyMin);

        Publish(p, Blue(73.9, 43));
        Assert.Equal(2, p.Version);
        Assert.Equal(73.9f, p.ReadView().Fuel);
    }

    [Fact]
    public void Read_and_ReadView_start_at_their_defaults()
    {
        var p = new OverlayStatePublisher();
        Assert.Equal(default, p.ReadView());
        Assert.Equal(SignalState.Inactive, p.Read().State);
    }
}
