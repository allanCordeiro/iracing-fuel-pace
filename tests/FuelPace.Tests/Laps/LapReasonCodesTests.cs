using FuelPace.Core.Laps;

namespace FuelPace.Tests.Laps;

public class LapReasonCodesTests
{
    [Fact]
    public void ToCodes_follows_bit_order()
    {
        var codes = LapReasonCodes.ToCodes(LapReasons.Manual | LapReasons.Incident | LapReasons.Pit | LapReasons.OffTrack);
        Assert.Equal(new[] { "pit", "incident", "offtrack", "manual" }, codes);
        Assert.Empty(LapReasonCodes.ToCodes(LapReasons.None));
    }

    [Fact]
    public void All_reasons_round_trip()
    {
        var all = LapReasons.Pit | LapReasons.Incident | LapReasons.OffTrack | LapReasons.Caution | LapReasons.Reset
                  | LapReasons.Refuel | LapReasons.Incomplete | LapReasons.Outlier | LapReasons.Manual;
        var codes = LapReasonCodes.ToCodes(all);
        Assert.Equal(new[] { "pit", "incident", "offtrack", "caution", "reset", "refuel", "incomplete", "outlier", "manual" }, codes);
        Assert.Equal(all, LapReasonCodes.FromCodes(codes));
    }

    [Fact]
    public void FromCodes_ignores_unknown()
    {
        Assert.Equal(LapReasons.Pit | LapReasons.Reset, LapReasonCodes.FromCodes(new[] { "reset", "bogus", "pit" }));
    }
}
