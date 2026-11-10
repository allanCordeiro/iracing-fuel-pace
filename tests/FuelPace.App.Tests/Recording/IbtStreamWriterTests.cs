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

        // crash before the first checkpoint: the reader derives the count from the file size
        using (var r = IbtReader.Open(path)) Assert.Equal(50, r.RecordCount);

        w.Checkpoint();
        for (int i = 50; i < 70; i++) w.Append(frames[i]);
        using (var r = IbtReader.Open(path))
        {
            Assert.Equal(50, r.RecordCount); // the last checkpoint
            Assert.Equal(Yaml(), r.SessionInfoYaml);
            Assert.Equal(frames[49].SessionTime, r.DiskHeader.SessionEndTime);
        }
        var back = ReadAll(path);
        Assert.Equal(50, back.Count);
        for (int i = 0; i < back.Count; i++) AssertSameFrame(frames[i], back[i], i);
        w.Dispose();
        using var final = IbtReader.Open(path);
        Assert.Equal(70, final.RecordCount);
    }

    [Fact]
    public void Oversized_yaml_goes_to_a_side_file_and_warns()
    {
        var path = Path.Combine(_dir, "big.ibt");
        var log = new DiagLog(DiagLevel.Trace);
        using (var w = new IbtStreamWriter(path, 60, reservedYamlBytes: 256, log: log))
        {
            w.WriteSessionInfo(new string('x', 1000));
            w.Append(SyntheticRace.Frames(1, withCarIdx: false).First());
        }
        Assert.Equal(new string('x', 1000), File.ReadAllText(path + ".yaml"));
        Assert.True(log.TryDequeueText(out var t));
        Assert.Equal(DiagLevel.Warn, t.Level);
        Assert.Single(ReadAll(path));
    }
}
