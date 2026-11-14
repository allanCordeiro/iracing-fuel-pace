using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Strategy;

[Collection(FuelPace.Tests.Perf.PerfCollection.Name)]
public class OverlayTextTests
{
    [Fact]
    public void Numbers_format_invariant()
    {
        Span<char> b = stackalloc char[32];
        Assert.Equal("77.6", b[..OverlayText.Liters(77.64, b)].ToString());
        Assert.Equal("2.38", b[..OverlayText.PerLap(2.384, b)].ToString());
        Assert.Equal("—", b[..OverlayText.PerLap(double.NaN, b)].ToString());
        Assert.Equal("34.0", b[..OverlayText.Laps(34, b)].ToString());
        Assert.Equal("43", b[..OverlayText.Minutes(43.4, b)].ToString());
        Assert.Equal("79.51", b[..OverlayText.Seconds(79.514, b)].ToString());
    }

    [Fact]
    public void Formatting_allocates_nothing()
    {
        Span<char> b = stackalloc char[32];
        var window = AllocationMeasure.Start();
        int sink = 0;
        for (int n = 0; n < 10_000; n++)
        {
            sink += OverlayText.Liters(77.64, b) + OverlayText.PerLap(2.384, b) + OverlayText.Laps(34, b)
                + OverlayText.Minutes(43.4, b) + OverlayText.Seconds(79.514, b);
        }
        window.AssertNone();
        Assert.True(sink > 0);
    }
}
