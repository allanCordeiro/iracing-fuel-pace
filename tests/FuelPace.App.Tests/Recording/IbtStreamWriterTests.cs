using FuelPace.App.Recording;
using FuelPace.App.Simulation;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;
using static FuelPace.App.Tests.Recording.RecordingTestSupport;

namespace FuelPace.App.Tests.Recording;

public sealed class IbtStreamWriterTests : IDisposable
{
    private readonly string _dir = TempDir();
    public void Dispose() => DeleteDir(_dir);

    [Fact]
    public void Crash_without_dispose_is_still_readable()
    {
        var path = Path.Combine(_dir, "crash.ibt");
        var frames = SyntheticRace.Frames(1, withCarIdx: true).Take(120).ToList();
        var w = new IbtStreamWriter(path, 60);
        w.WriteSessionInfo(Yaml());
        for (int i = 0; i < 50; i++) w.Append(frames[i]);
        w.Checkpoint();
        for (int i = 50; i < 70; i++) w.Append(frames[i]); // after the last checkpoint: must not be lost

        using (var r = IbtReader.Open(path))
        {
            Assert.Equal(70, r.RecordCount);
            Assert.Equal(Yaml(), r.SessionInfoYaml);
            Assert.Equal(frames[49].SessionTime, r.DiskHeader.SessionEndTime);
        }
        var back = ReadAll(path);
        Assert.Equal(70, back.Count);
        for (int i = 0; i < back.Count; i++) AssertSameFrame(frames[i], back[i], i);
        w.Dispose();
    }

    [Fact]
    public void Disposed_file_has_the_exact_record_count_and_end_time()
    {
        var path = Path.Combine(_dir, "done.ibt");
        var frames = SyntheticRace.Frames(1, withCarIdx: false).Take(30).ToList();
        using (var w = new IbtStreamWriter(path, 60, sessionStartDate: 1234))
        {
            w.WriteSessionInfo(Yaml());
            foreach (var f in frames) w.Append(f);
        }
        using var r = IbtReader.Open(path);
        Assert.Equal(30, r.DiskHeader.RecordCount);
        Assert.Equal(30, r.RecordCount);
        Assert.Equal(1234, r.DiskHeader.SessionStartDate);
        Assert.Equal(frames[^1].SessionTime, r.DiskHeader.SessionEndTime);
    }

    [Fact]
    public void Oversized_yaml_goes_to_a_side_file_and_warns()
    {
        var path = Path.Combine(_dir, "big.ibt");
        var log = new DiagLog(DiagLevel.Trace);
        using (var w = new IbtStreamWriter(path, 60, reservedYamlBytes: 256, log: log))
        {
            w.WriteSessionInfo("a: b");
            w.WriteSessionInfo(new string('x', 1000));
            w.Append(SyntheticRace.Frames(1, withCarIdx: false).First());
        }
        Assert.Equal(new string('x', 1000), File.ReadAllText(path + ".yaml"));
        using (var r = IbtReader.Open(path)) Assert.Equal("", r.SessionInfoYaml); // no stale YAML left in the file
        Assert.True(log.TryDequeueText(out var t));
        Assert.Equal(DiagLevel.Warn, t.Level);
        Assert.Single(ReadAll(path));
    }
}
