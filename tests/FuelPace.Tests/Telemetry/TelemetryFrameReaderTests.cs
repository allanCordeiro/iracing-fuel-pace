using System.Buffers.Binary;
using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Telemetry;

public class TelemetryFrameReaderTests
{
    private static TelemetryFrame ReadPct(float pct)
    {
        var layout = VarLayout.Resolve([new VarHeader(VarType.Float, 0, 1, false, "LapDistPct", "", "%")]);
        var row = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(row, pct);
        var f = new TelemetryFrame();
        TelemetryFrameReader.Read(row, layout, ref f);
        return f;
    }

    // Feedback 2026-11-18: at Monza iRacing reported LapDistPct = -0.000009 for one frame at 242 km/h, right after
    // the line (Lap already incremented). Read as "not in world", it marked a clean lap as reset.
    [Theory]
    [InlineData(-0.000009f)]
    [InlineData(-0.000018f)]
    [InlineData(-0.0009f)]
    public void A_pct_just_below_zero_is_the_line(float pct) => Assert.Equal(0f, ReadPct(pct).LapDistPct);

    [Fact]
    public void Not_in_world_stays_minus_one() => Assert.Equal(-1f, ReadPct(-1f).LapDistPct);

    [Theory]
    [InlineData(0f)]
    [InlineData(0.000187f)]
    [InlineData(0.9998f)]
    public void Pct_in_range_is_unchanged(float pct) => Assert.Equal(pct, ReadPct(pct).LapDistPct);
}
