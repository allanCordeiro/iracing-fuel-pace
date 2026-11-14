using FuelPace.App.Settings;
using FuelPace.App.Tests.Fakes;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.App.Tests.Host;

/// <summary>PRD v3 V3-8 (PERF-06 with the fixture): the whole Agent replays the real Zolder file at 1×.</summary>
public sealed class AgentHostFixtureTests
{
    const int FramesPerSecond = 60;

    /// <summary>The frames at the start of the file with the car parked in its pit stall (in the stall, not moving).</summary>
    static int ParkedFrames(string path)
    {
        using var src = new IbtTelemetrySource(path);
        var f = new TelemetryFrame();
        int n = 0;
        while (src.TryRead(ref f) == ReadResult.Frame && f.PlayerCarInPitStall && f.Speed < 0.1f) n++;
        return n;
    }

    [Fact]
    public void Fixture_at_1x_averages_at_most_10_redraws_per_second_and_none_while_parked_in_the_box()
    {
        var path = Fixtures.RequireZolder();
        int parked = ParkedFrames(path);
        Assert.True(parked > 3 * FramesPerSecond, $"the fixture starts with only {parked} parked frames");

        using var rig = new HostRig();
        var settings = new AppSettings();
        settings.Overlay.AutoHide = false; // the parked (inactive) overlay stays on screen, so its redraws are counted
        rig.WriteSettings(settings);
        // The fake source moves the host clock 1/60 s per frame: the Agent sees the file at 1×, whatever the test's pace.
        var src = new FakeSource(path, rig.Clock, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();

        // First second: connection, session info and the first picture.
        src.Allow(FramesPerSecond);
        rig.Settle(src, FramesPerSecond);
        int first = rig.Surface.Renders;
        Assert.True(first >= 1);

        // Still parked in the box: nothing on the panel changes, nothing is redrawn.
        src.Allow(parked - FramesPerSecond);
        rig.Settle(src, parked);
        Assert.Equal(first, rig.Surface.Renders);

        // The rest of the stint.
        src.Allow(src.Frames - parked);
        rig.Settle(src, src.Frames);
        double seconds = (double)src.Frames / FramesPerSecond;
        double perSecond = rig.Surface.Renders / seconds;
        Assert.True(perSecond <= 10, $"{rig.Surface.Renders} redraws in {seconds:F0} s = {perSecond:F2}/s");
        Assert.True(rig.Surface.Renders > seconds / 10, $"only {rig.Surface.Renders} redraws in {seconds:F0} s of driving");
    }
}
