using FuelPace.Core.Strategy;

namespace FuelPace.Tests.Strategy;

[Collection(FuelPace.Tests.Perf.PerfCollection.Name)]
public class OverlayTextTests
{
    [Theory]
    [InlineData(InsightKind.StopRequired, 3.4, double.NaN, double.NaN, "Faltam 3.4 L · parada necessária", 12)]
    [InlineData(InsightKind.BopMismatch, double.NaN, double.NaN, double.NaN, "Baseline com BoP diferente", 0)]
    [InlineData(InsightKind.Save, 0.08, 3, double.NaN, "Economize 0.08 L/v (−3%)", 18)]
    [InlineData(InsightKind.Save, 0.08, 3, 0.9, "Economize 0.08 L/v (−3%) · ≈ +0.9 s/v", 18)]
    [InlineData(InsightKind.Slack, 1.2, double.NaN, double.NaN, "Folga 1.2 L · pode atacar", 11)]
    [InlineData(InsightKind.OnTarget, 0.3, double.NaN, double.NaN, "No alvo · margem 0.3 L", 7)]
    [InlineData(InsightKind.NoBaseline, double.NaN, double.NaN, double.NaN, "Sem baseline para Ligier · Zolder Alt", 0)]
    [InlineData(InsightKind.ElectricUnsupported, double.NaN, double.NaN, double.NaN, "Carro elétrico: sem suporte", 0)]
    [InlineData(InsightKind.None, double.NaN, double.NaN, double.NaN, "", 0)]
    public void Insight_copy(InsightKind k, double a, double b, double c, string expected, int bold)
    {
        Span<char> buf = stackalloc char[128];
        int n = OverlayText.Insight(new Insight(k, a, b, c), "Ligier", "Zolder Alt", buf, out int boldLength);
        Assert.Equal(expected, buf[..n].ToString());
        Assert.Equal(bold, boldLength);
    }

    [Fact]
    public void Insight_truncates_when_destination_is_too_small()
    {
        Span<char> buf = stackalloc char[5];
        int n = OverlayText.Insight(new Insight(InsightKind.Slack, 1.2), "", "", buf, out int bold);
        Assert.True(n <= 5);
        Assert.True(bold <= n);
    }

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
        Span<char> b = stackalloc char[128];
        var ins = new Insight(InsightKind.Save, 0.08, 3, 0.9);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int sink = 0;
        for (int n = 0; n < 10_000; n++)
        {
            sink += OverlayText.Liters(77.64, b) + OverlayText.PerLap(2.384, b) + OverlayText.Laps(34, b)
                + OverlayText.Minutes(43.4, b) + OverlayText.Seconds(79.514, b)
                + OverlayText.Insight(in ins, "Ligier", "Zolder Alt", b, out _);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sink > 0);
    }
}
