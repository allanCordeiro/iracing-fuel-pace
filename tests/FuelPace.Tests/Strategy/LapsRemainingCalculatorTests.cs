using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Strategy;

[Collection(FuelPace.Tests.Perf.PerfCollection.Name)]
public class LapsRemainingCalculatorTests
{
    [Fact] public void Time_race_uses_leader_pace() => Assert.Equal(7.2, LapsRemainingCalculator.ByTime(600, 0.30, 100, 0.80, 102), 6); // criterion 9
    [Fact] public void Own_pace_gives_one_lap_less() => Assert.Equal(6.2, LapsRemainingCalculator.ByTime(600, 0.80, 102, 0.80, 102), 6);

    [Fact]
    public void Being_the_leader_equals_own_pace() =>
        Assert.Equal(LapsRemainingCalculator.ByTime(600, 0.8, double.NaN, 0.8, 102), LapsRemainingCalculator.ByTime(600, 0.8, 102, 0.8, 102));

    [Fact] public void Non_positive_leader_pace_falls_back_to_own_pace() =>
        Assert.Equal(LapsRemainingCalculator.ByTime(600, 0.8, 102, 0.8, 102), LapsRemainingCalculator.ByTime(600, 0.3, 0, 0.8, 102));

    [Fact] public void Clock_at_zero_leader_finishes_current_lap() => Assert.Equal(1.2, LapsRemainingCalculator.ByTime(0, 0.30, 100, 0.80, 102), 6);

    [Fact]
    public void Negative_time_remaining_is_treated_as_zero()
    {
        double r = LapsRemainingCalculator.ByTime(-30, 0.30, 100, 0.80, 102);
        Assert.Equal(LapsRemainingCalculator.ByTime(0, 0.30, 100, 0.80, 102), r);
        Assert.True(r > 0);
    }

    [Fact]
    public void Leader_crossing_exactly_at_zero_gets_one_more_lap()
    {
        // T=0, p_L=0 -> N_L=1 -> T_fin=100 -> me at 0.5, 102s: ceil(0.5+0.98+eps)-0.5 = 1.5
        Assert.Equal(1.5, LapsRemainingCalculator.ByTime(0, 0.0, 100, 0.5, 102), 6);
    }

    [Fact]
    public void Time_race_never_zero_negative_or_nan_after_clock_zero()
    {
        foreach (double t in new[] { 0.0, -1, -600 })
            foreach (double pl in new[] { 0.0, 0.5, 0.999999 })
                foreach (double pm in new[] { 0.0, 0.5, 0.999999 })
                {
                    double r = LapsRemainingCalculator.ByTime(t, pl, 100, pm, 102);
                    Assert.False(double.IsNaN(r));
                    Assert.True(r > 0 && r <= 2.0, $"t={t} pl={pl} pm={pm} r={r}");
                }
    }

    [Fact] public void Laps_race_formula() => Assert.Equal(10.75, LapsRemainingCalculator.ByLaps(10, 0.25), 6);

    [Theory]
    [InlineData(32767)]
    [InlineData(40000)]
    [InlineData(-1)]
    public void Laps_unlimited_or_invalid_is_nan(int remain) => Assert.True(double.IsNaN(LapsRemainingCalculator.ByLaps(remain, 0.25)));

    [Fact] public void After_checkered_is_rest_of_lap() => Assert.Equal(0.4, LapsRemainingCalculator.AfterCheckered(0.6), 6);

    [Fact]
    public void Stint_counts_down_and_never_negative()
    {
        Assert.Equal(19.5, LapsRemainingCalculator.Stint(25, 5, 0.5), 6);
        Assert.Equal(0, LapsRemainingCalculator.Stint(25, 26, 0.5));
    }

    [Fact]
    public void Nan_pace_gives_nan()
    {
        Assert.True(double.IsNaN(LapsRemainingCalculator.ByTime(600, 0.3, 100, 0.8, double.NaN)));
        Assert.True(double.IsNaN(LapsRemainingCalculator.ByTime(600, 0.3, 100, 0.8, 0)));
        Assert.True(double.IsNaN(LapsRemainingCalculator.ByTime(600, 0.3, 100, 0.8, -5)));
        Assert.True(double.IsNaN(LapsRemainingCalculator.ByTime(double.NaN, 0.3, 100, 0.8, 102)));
    }

    [Fact]
    public void Methods_do_not_allocate()
    {
        double sink = 0;
        sink += LapsRemainingCalculator.ByTime(600, 0.3, 100, 0.8, 102) + LapsRemainingCalculator.ByLaps(10, 0.2)
              + LapsRemainingCalculator.AfterCheckered(0.6) + LapsRemainingCalculator.Stint(25, 5, 0.5);
        var window = AllocationMeasure.Start();
        for (int i = 0; i < 1000; i++)
            sink += LapsRemainingCalculator.ByTime(600, 0.3, 100, 0.8, 102) + LapsRemainingCalculator.ByLaps(10, 0.2)
                  + LapsRemainingCalculator.AfterCheckered(0.6) + LapsRemainingCalculator.Stint(25, 5, 0.5);
        window.AssertNone();
        Assert.False(double.IsNaN(sink));
    }
}
