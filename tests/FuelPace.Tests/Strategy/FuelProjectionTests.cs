using FuelPace.Core.Baselines;
using FuelPace.Core.Strategy;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Strategy;

public class FuelProjectionTests
{
    static float[] SyntheticProfile()
    {
        var p = new float[101];
        for (int k = 0; k < p.Length; k++)
        {
            double x = k / 100.0;
            p[k] = (float)(x - 0.04 * Math.Sin(Math.PI * x));
        }
        return p;
    }

    [Fact]
    public void Hidden_below_15pct() =>
        Assert.True(double.IsNaN(FuelProjection.Project(0.3, 0.14, SyntheticProfile())));

    [Fact]
    public void Linear_without_profile() =>
        Assert.Equal(2.4, FuelProjection.Project(1.2, 0.5, []), 6);

    [Fact]
    public void Wrong_length_profile_is_treated_as_empty() =>
        Assert.Equal(2.4, FuelProjection.Project(1.2, 0.5, new float[50]), 6);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Out_of_range_pct_is_nan(double pct)
    {
        Assert.True(double.IsNaN(FuelProjection.Project(1.0, pct, SyntheticProfile())));
        Assert.True(double.IsNaN(FuelProjection.Interpolate(SyntheticProfile(), pct)));
    }

    [Fact]
    public void Non_positive_interpolated_value_is_nan() =>
        Assert.True(double.IsNaN(FuelProjection.Project(1.0, 0.5, new float[101])));

    [Fact]
    public void Uses_profile_interpolation()
    {
        var p = SyntheticProfile();
        double pct = 0.505;
        double used = 2.4 * (pct - 0.04 * Math.Sin(Math.PI * pct));
        Assert.InRange(FuelProjection.Project(used, pct, p), 2.4 - 0.002, 2.4 + 0.002);
    }

    [Fact]
    public void Interpolate_at_end_is_last_point() =>
        Assert.Equal(1.0, FuelProjection.Interpolate(SyntheticProfile(), 1.0), 6);

    [Fact]
    public void Project_and_Interpolate_allocate_nothing()
    {
        var p = SyntheticProfile();
        double sink = 0;
        for (int i = 0; i < 100; i++) sink += FuelProjection.Project(1.2, 0.5, p);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            sink += FuelProjection.Project(1.2, 0.3 + (i % 50) / 100.0, p) + FuelProjection.Interpolate(p, 0.77);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, delta);
        Assert.False(double.IsNaN(sink));
    }

    [Fact]
    public void Leave_one_out_on_fixture_beats_linear()   // acceptance criterion 8 (PRD §13.8)
    {
        var (rec, _) = ReplayHarness.CaptureWholeFile(Fixtures.RequireZolder());
        var valid = rec.Laps.Where(l => l.Valid && l.FuelProfile is { Length: 101 }).ToList();
        Assert.True(valid.Count >= 3, "need several valid laps");

        for (int i = 0; i < valid.Count; i++)
        {
            var others = valid.Where((_, j) => j != i).Select(l => l.FuelProfile!).ToList();
            var reference = BaselineCalculator.MeanProfile(others);
            Assert.Equal(101, reference.Length);
            double fuel = valid[i].FuelL;
            foreach (double pct in new[] { 0.5, 0.6 })
            {
                double used = FuelProjection.Interpolate(valid[i].FuelProfile!, pct) * fuel;
                double profErr = Math.Abs(FuelProjection.Project(used, pct, reference) - fuel);
                double linErr = Math.Abs(used / pct - fuel);
                Assert.True(profErr <= 0.07, $"lap {valid[i].Lap} pct {pct}: profile error {profErr:F4}");
                Assert.True(linErr > 0.13, $"lap {valid[i].Lap} pct {pct}: linear error {linErr:F4}");
            }
        }
    }
}
