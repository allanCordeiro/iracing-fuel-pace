using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Strategy;

public class SignalHysteresisTests
{
    [Fact]
    public void Holds_previous_color_for_1_5s()
    {
        var h = new SignalHysteresis();
        Assert.Equal(SignalState.Blue, h.Apply(SignalState.Blue, 0));
        Assert.Equal(SignalState.Blue, h.Apply(SignalState.Red, 1.0));
        Assert.Equal(SignalState.Blue, h.Apply(SignalState.Red, 2.4));
        Assert.Equal(SignalState.Red, h.Apply(SignalState.Red, 2.5));
    }

    [Fact]
    public void Flicker_never_switches()
    {
        var h = new SignalHysteresis();
        Assert.Equal(SignalState.Blue, h.Apply(SignalState.Blue, 0));
        for (int k = 1; k <= 20; k++)
            Assert.Equal(SignalState.Blue, h.Apply(k % 2 == 1 ? SignalState.Red : SignalState.Blue, k * 0.5));
    }

    [Theory]
    [InlineData(SignalState.Blue, SignalState.Inactive)]
    [InlineData(SignalState.Blue, SignalState.NoBaseline)]
    [InlineData(SignalState.Blue, SignalState.NoTarget)]
    [InlineData(SignalState.Red, SignalState.NoTarget)]
    [InlineData(SignalState.NotFeasible, SignalState.NoBaseline)]
    [InlineData(SignalState.Inactive, SignalState.Red)]
    [InlineData(SignalState.NoBaseline, SignalState.Blue)]
    [InlineData(SignalState.NoTarget, SignalState.NotFeasible)]
    public void Non_color_states_switch_immediately(SignalState from, SignalState to)
    {
        var h = new SignalHysteresis();
        h.Apply(from, 0);
        Assert.Equal(to, h.Apply(to, 0.1));
    }

    [Fact]
    public void Inactive_and_no_baseline_switch_immediately()
    {
        var h = new SignalHysteresis();
        h.Apply(SignalState.Blue, 0);
        Assert.Equal(SignalState.Inactive, h.Apply(SignalState.Inactive, 0.1));
        Assert.Equal(SignalState.Red, h.Apply(SignalState.Red, 0.2));
        Assert.Equal(SignalState.NoBaseline, h.Apply(SignalState.NoBaseline, 0.3));
    }

    [Fact]
    public void Time_going_backwards_resets()
    {
        var h = new SignalHysteresis();
        h.Apply(SignalState.Blue, 100);
        Assert.Equal(SignalState.Red, h.Apply(SignalState.Red, 50));
    }

    [Fact]
    public void Colored_transitions_between_all_pairs_are_held()
    {
        var h = new SignalHysteresis();
        h.Apply(SignalState.Red, 0);
        Assert.Equal(SignalState.Red, h.Apply(SignalState.NotFeasible, 0.5));
        Assert.Equal(SignalState.NotFeasible, h.Apply(SignalState.NotFeasible, 2.0));
    }
}
