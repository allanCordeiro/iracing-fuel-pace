using FuelPace.Core.Baselines;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Baselines;

public class BaselineCalculatorTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    static readonly LapSample[] Fixture6 =
    [
        new(2.382, 80.897, 64, null), new(2.404, 80.272, 66, null), new(2.373, 79.964, 65, null),
        new(2.403, 79.514, 67, null), new(2.389, 79.134, 64, null), new(2.289, 81.062, 58, null),
    ];

    static LapSample[] Laps(params double[] fuel) =>
        fuel.Select(f => new LapSample(f, 200 - 50 * f, 60, null)).ToArray();

    [Fact]
    public void Matches_reference_baseline()
    {
        var b = BaselineCalculator.Compute(Fixture6, 100, [], T0)!;
        Assert.Equal(6, b.ValidLaps);
        Assert.Equal(2.3733, b.FuelMeanL, 3); Assert.Equal(2.3855, b.FuelMedianL, 3);
        Assert.Equal(2.289, b.FuelMinL, 3); Assert.Equal(0.0430, b.FuelStdL, 3);
        Assert.Equal(2.331, b.FuelP10L, 3); Assert.Equal(2.4035, b.FuelP90L, 3);
        Assert.Equal(80.1405, b.LapMeanS, 3); Assert.Equal(80.118, b.LapMedianS, 3); Assert.Equal(79.134, b.LapBestS, 3);
        Assert.Equal(42.14, b.AutonomyLaps, 1); Assert.Equal(56.3, b.AutonomyMin, 1);
        Assert.Equal(64.0, b.FullThrottleMeanPct, 1);
        Assert.Equal(Confidence.Media, b.Confidence);
        Assert.Null(b.FuelVsTime);
        Assert.Equal(T0, b.ComputedAt);
    }

    [Fact]
    public void Fewer_than_3_laps_has_no_baseline()
    {
        Assert.Null(BaselineCalculator.Compute(Fixture6.Take(2).ToArray(), 100, [], T0));
        Assert.Null(BaselineCalculator.Compute([], 100, [], T0));
    }

    [Theory]
    [InlineData(3, Confidence.Baixa)]
    [InlineData(4, Confidence.Baixa)]
    [InlineData(5, Confidence.Media)]
    [InlineData(9, Confidence.Media)]
    [InlineData(10, Confidence.Alta)]
    public void Confidence_by_count(int n, Confidence c)
    {
        var laps = Enumerable.Range(0, n).Select(i => new LapSample(2.38 + 0.001 * i, 80, 60, null)).ToArray();
        Assert.Equal(c, BaselineCalculator.Compute(laps, 100, [], T0)!.Confidence);
    }

    [Fact]
    public void Full_throttle_mean_ignores_NaN()
    {
        LapSample[] laps = [new(2.4, 80, 60, null), new(2.4, 80, double.NaN, null), new(2.4, 80, 70, null)];
        Assert.Equal(65.0, BaselineCalculator.Compute(laps, 100, [], T0)!.FullThrottleMeanPct, 6);
        LapSample[] none = [new(2.4, 80, double.NaN, null), new(2.4, 80, double.NaN, null), new(2.4, 80, double.NaN, null)];
        Assert.True(double.IsNaN(BaselineCalculator.Compute(none, 100, [], T0)!.FullThrottleMeanPct));
    }

    [Fact]
    public void Regression_when_enough_economy_laps()
    {
        var b = BaselineCalculator.Compute(Laps(2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.30, 2.29, 2.31), 100, [], T0)!;
        Assert.NotNull(b.FuelVsTime);
        Assert.Equal(200, b.FuelVsTime!.A, 2);
        Assert.Equal(-50, b.FuelVsTime.B, 2);
    }

    [Fact]
    public void No_regression_when_range_below_2pct()
    {
        var b = BaselineCalculator.Compute(Laps(2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.355, 2.355), 100, [], T0)!;
        Assert.Null(b.FuelVsTime);
    }

    [Fact]
    public void No_regression_below_8_laps()
    {
        var b = BaselineCalculator.Compute(Laps(2.40, 2.40, 2.40, 2.40, 2.40, 2.30, 2.29), 100, [], T0)!;
        Assert.Null(b.FuelVsTime);
    }

    [Fact]
    public void No_regression_with_single_economy_lap()
    {
        var b = BaselineCalculator.Compute(Laps(2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.40, 2.30), 100, [], T0)!;
        Assert.Null(b.FuelVsTime);
    }

    [Fact]
    public void Mean_profile_averages_pointwise()
    {
        var p1 = Enumerable.Repeat(0f, 101).ToArray();
        var p2 = Enumerable.Repeat(1f, 101).ToArray();
        p2[100] = 3f;
        var m = BaselineCalculator.MeanProfile([p1, p2]);
        Assert.Equal(101, m.Length);
        Assert.Equal(0.5f, m[0]);
        Assert.Equal(1.5f, m[100]);
        Assert.Empty(BaselineCalculator.MeanProfile([]));
    }

    [Fact]
    public void Compute_ignores_laps_without_profile_in_mean_profile()
    {
        var p = Enumerable.Repeat(0.5f, 101).ToArray();
        LapSample[] laps = [new(2.4, 80, 60, p), new(2.4, 80, 60, null), new(2.4, 80, 60, [1f, 2f])];
        var b = BaselineCalculator.Compute(laps, 100, [], T0)!;
        Assert.Equal(101, b.FuelProfile.Length);
        Assert.Equal(0.5f, b.FuelProfile[50]);
    }

    [Fact]
    public void Bop_signature_matches_within_tolerance()
    {
        var a = new BopSignature(1.0, 10, 100);
        Assert.True(a.Matches(new BopSignature(1.005, 10.05, 100.05)));
        Assert.False(a.Matches(new BopSignature(1.02, 10, 100)));
        Assert.False(a.Matches(new BopSignature(1.0, 10.2, 100)));
        Assert.False(a.Matches(new BopSignature(1.0, 10, 100.2)));
    }

    [Fact]
    public void Fixture_baseline_matches_reference()   // acceptance criterion 2
    {
        var (rec, _) = ReplayHarness.CaptureWholeFile(Fixtures.RequireZolder());
        var samples = rec.Laps.Where(l => l.Valid && l.TimeS is not null)
            .Select(l => new LapSample(l.FuelL, l.TimeS!.Value, l.FullThrottlePct, l.FuelProfile)).ToArray();
        var b = BaselineCalculator.Compute(samples, 100, [], T0)!;

        Assert.Equal(6, b.ValidLaps);
        Assert.InRange(Math.Abs(b.FuelMeanL - 2.373), 0, 0.005);
        Assert.InRange(Math.Abs(b.FuelMedianL - 2.386), 0, 0.005);
        Assert.InRange(Math.Abs(b.FuelMinL - 2.289), 0, 0.005);
        Assert.InRange(Math.Abs(b.FuelStdL - 0.043), 0, 0.005);
        Assert.InRange(Math.Abs(b.LapMeanS - 80.140), 0, 0.005);
        Assert.InRange(Math.Abs(b.LapMedianS - 80.118), 0, 0.005);
        Assert.Equal(Confidence.Media, b.Confidence);
        Assert.Null(b.FuelVsTime);
        Assert.Equal(101, b.FuelProfile.Length);
    }
}
