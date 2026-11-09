using FuelPace.Core.Laps;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Capture;

public class FixtureCaptureTests
{
    [Fact]
    public void Fixture_capture_matches_reference_table()   // acceptance criterion 1 (PRD §12/§13)
    {
        var (rec, _) = ReplayHarness.CaptureWholeFile(Fixtures.RequireZolder());
        var expected = new (int lap, double? time, double fuel, string[] reasons)[]
        {
            (16, 80.897, 2.382, []), (17, 80.272, 2.404, []), (18, 79.964, 2.373, []),
            (19, 93.175, 2.388, ["incident", "offtrack"]), (20, 79.133, 2.386, ["incident"]),
            (21, 79.514, 2.403, []), (22, 79.134, 2.389, []), (23, 81.062, 2.289, []),
            // §12 lists lap 24 as just `incomplete`, but in the recording's last 6.3 s the car leaves the track
            // (6.27 s off, PlayerTrackSurface 0) and PlayerCarMyIncidentCount goes 17 -> 19. Decision 1: an
            // incomplete lap keeps the reasons observed, as lap 15 keeps pit and incident.
            (24, null, 0.986, ["incident", "offtrack", "incomplete"]),
        };

        foreach (var (lap, time, fuel, reasons) in expected)
        {
            var l = Assert.Single(rec.Laps, x => x.Lap == lap);
            Assert.Equal(LapReasonCodes.FromCodes(reasons), l.Reasons);
            Assert.InRange(Math.Abs(l.FuelL - fuel), 0, 0.005);
            if (time is null) Assert.Null(l.TimeS);
            else
            {
                Assert.NotNull(l.TimeS);
                Assert.InRange(Math.Abs(l.TimeS.Value - time.Value), 0, 0.01);
            }
        }

        // Lap 15 starts before the recording (decision 1): Incomplete on top of what was observed.
        var lap15 = Assert.Single(rec.Laps, x => x.Lap == 15);
        Assert.False(lap15.Valid);
        Assert.Equal(LapReasons.Pit | LapReasons.Incident, lap15.Reasons & (LapReasons.Pit | LapReasons.Incident));
        Assert.Null(lap15.TimeS);

        Assert.Equal(Enumerable.Range(15, 10), rec.Laps.Select(l => l.Lap));
        Assert.Equal(6, rec.Laps.Count(l => l.Valid));
    }
}
