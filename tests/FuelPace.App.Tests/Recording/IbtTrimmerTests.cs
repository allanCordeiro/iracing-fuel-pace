using FuelPace.App.Recording;
using FuelPace.App.Simulation;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;
using static FuelPace.App.Tests.Recording.RecordingTestSupport;

namespace FuelPace.App.Tests.Recording;

public sealed class IbtTrimmerTests : IDisposable
{
    private readonly string _dir = TempDir();
    public void Dispose() => DeleteDir(_dir);

    [Fact]
    public void Trim_keeps_only_requested_window()
    {
        var input = Path.Combine(_dir, "in.ibt");
        var output = Path.Combine(_dir, "out.ibt");
        SyntheticRace.WriteIbt(input, 5, withCarIdx: true);

        IbtTrimmer.Trim(input, output, 20.0, 45.0);

        var frames = ReadAll(output);
        Assert.NotEmpty(frames);
        Assert.InRange(frames[0].SessionTime, 20.0, 20.1);
        Assert.InRange(frames[^1].SessionTime, 44.9, 45.0);
        Assert.All(frames, f => Assert.InRange(f.SessionTime, 20.0, 45.0));
        using var a = new IbtTelemetrySource(input);
        using var b = new IbtTelemetrySource(output);
        Assert.Equal(a.ReadSessionInfoYaml(), b.ReadSessionInfoYaml());

        // the trimmed file still runs through the Core laps
        var laps = DetectLaps(frames);
        Assert.NotEmpty(laps);
    }

    [Fact]
    public void Trim_with_empty_window_throws()
    {
        var input = Path.Combine(_dir, "in.ibt");
        SyntheticRace.WriteIbt(input, 1, withCarIdx: false);
        Assert.Throws<ArgumentException>(() => IbtTrimmer.Trim(input, Path.Combine(_dir, "o.ibt"), 1000, 2000));
        Assert.Throws<ArgumentException>(() => IbtTrimmer.Trim(input, Path.Combine(_dir, "o.ibt"), 5, 4));
    }
}
