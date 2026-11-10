using FuelPace.App.Diagnostics;
using FuelPace.App.Recording;
using FuelPace.App.Simulation;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;
using static FuelPace.App.Tests.Recording.RecordingTestSupport;

namespace FuelPace.App.Tests.Recording;

[Collection(PerfCollection.Name)]
public sealed class TelemetryRecorderTests : IDisposable
{
    private readonly string _dir = TempDir();
    public void Dispose() => DeleteDir(_dir);

    private string[] Files => Directory.GetFiles(_dir, "*.ibt");

    [Fact]
    public void Recording_round_trips_through_replay_source()
    {
        var frames = SyntheticRace.Frames(3, withCarIdx: true).ToList();
        string yaml = Yaml();
        var log = new DiagLog(DiagLevel.Trace);
        using (var rec = new TelemetryRecorder(_dir, log, 1, ringCapacity: 8192))
        {
            rec.OnSessionInfo(yaml, 1, Info(yaml));
            foreach (var f in frames) rec.OnFrame(in f);
            rec.OnSourceEnded();
        }

        var file = Assert.Single(Files);
        Assert.Matches(@"^\d{8}-\d{6}_ligierjsp320_zolder_alt.*\.ibt$", Path.GetFileName(file));
        var back = ReadAll(file);
        Assert.Equal(frames.Count, back.Count);
        for (int i = 0; i < frames.Count; i++) AssertSameFrame(frames[i], back[i], i);
        using var src = new IbtTelemetrySource(file);
        Assert.Equal(yaml, src.ReadSessionInfoYaml());
        Assert.True(src.Layout.HasCarIdx);

        var events = Drain(log);
        Assert.Contains(events, r => r.Event == AppDiagEvents.RecordingStarted);
        var stopped = Assert.Single(events, r => r.Event == AppDiagEvents.RecordingStopped);
        Assert.Equal(frames.Count, stopped.I0);
        Assert.Equal(0, stopped.I1);
    }

    [Fact]
    public void Core_pipeline_detects_same_laps_on_recording()
    {
        var frames = SyntheticRace.Frames(10, withCarIdx: true).ToList();
        string yaml = Yaml();
        using (var rec = new TelemetryRecorder(_dir, DiagLog.Null, 1, ringCapacity: 16384))
        {
            rec.OnSessionInfo(yaml, 1, Info(yaml));
            foreach (var f in frames) rec.OnFrame(in f);
            rec.OnSourceEnded();
        }
        var expected = DetectLaps(frames);
        var actual = DetectLaps(ReadAll(Assert.Single(Files)));
        Assert.True(expected.Count >= 9);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Rotates_on_profile_change()
    {
        string yamlA = Yaml(), yamlB = Yaml("othercar");
        var frames = SyntheticRace.Frames(1, withCarIdx: true).Take(100).ToList();
        using var rec = new TelemetryRecorder(_dir, DiagLog.Null, 1, ringCapacity: 1024);
        rec.OnSessionInfo(yamlA, 1, Info(yamlA));
        for (int i = 0; i < 50; i++) rec.OnFrame(frames[i]);
        rec.Drain();
        rec.OnSessionInfo(yamlB, 2, Info(yamlB));
        for (int i = 50; i < 100; i++) rec.OnFrame(frames[i]);
        rec.OnSourceEnded();

        Assert.Equal(2, Files.Length);
        var byYaml = Files.ToDictionary(p => { using var s = new IbtTelemetrySource(p); return s.ReadSessionInfoYaml(); });
        Assert.Equal(50, ReadAll(byYaml[yamlA]).Count);
        Assert.Equal(50, ReadAll(byYaml[yamlB]).Count);
        Assert.Contains("othercar", Path.GetFileName(byYaml[yamlB]));
    }

    [Fact]
    public void Rotates_on_session_num_change_and_rewrites_yaml_in_place_for_same_profile()
    {
        string yaml1 = Yaml(), yaml2 = yaml1.Replace("CurrentSessionNum: 0", "CurrentSessionNum: 1");
        var frames = SyntheticRace.Frames(1, withCarIdx: false).Take(60).ToList();
        using var rec = new TelemetryRecorder(_dir, DiagLog.Null, 1, ringCapacity: 1024);
        rec.OnSessionInfo(yaml1, 1, Info(yaml1));
        for (int i = 0; i < 20; i++) rec.OnFrame(frames[i]);
        rec.Drain();
        rec.OnSessionInfo(yaml2, 2, Info(yaml2)); // same profile key: no rotation, the YAML is rewritten in the open file
        for (int i = 20; i < 40; i++) rec.OnFrame(frames[i]);
        rec.Drain();
        for (int i = 40; i < 60; i++) { var f = frames[i]; f.SessionNum = 1; rec.OnFrame(f); }
        rec.OnSourceEnded();

        Assert.Equal(2, Files.Length);
        var sizes = Files.Select(p => ReadAll(p).Count).Order().ToArray();
        Assert.Equal([20, 40], sizes);
        foreach (var p in Files)
        {
            using var s = new IbtTelemetrySource(p);
            Assert.Equal(yaml2, s.ReadSessionInfoYaml());
        }
    }

    [Fact]
    public void OnFrame_allocates_nothing()
    {
        string yaml = Yaml();
        using var rec = new TelemetryRecorder(_dir, DiagLog.Null, 1, ringCapacity: 4096);
        rec.OnSessionInfo(yaml, 1, Info(yaml));
        var f = SyntheticRace.Frames(1, withCarIdx: true).First();
        for (int i = 0; i < 50; i++) rec.OnFrame(in f);
        rec.Drain();

        var m = AllocationMeasure.Start();
        for (int i = 0; i < 1000; i++) rec.OnFrame(in f);
        m.AssertNone();
    }

    [Fact]
    public void Full_ring_drops_and_counts()
    {
        string yaml = Yaml();
        var log = new DiagLog(DiagLevel.Trace);
        using var gate = new ManualResetEventSlim(false);
        using var rec = new TelemetryRecorder(_dir, log, 1, ringCapacity: 16);
        rec.WriterHook = gate.Wait;
        rec.OnSessionInfo(yaml, 1, Info(yaml));
        var f = SyntheticRace.Frames(1, withCarIdx: false).First();
        for (int i = 0; i < 100; i++) rec.OnFrame(in f);
        Assert.True(rec.DroppedFrames >= 84, "dropped " + rec.DroppedFrames);
        gate.Set();
        rec.OnSourceEnded();

        var stopped = Assert.Single(Drain(log), r => r.Event == AppDiagEvents.RecordingStopped);
        Assert.True(stopped.I1 >= 84);
        Assert.Equal(100, stopped.I0 + stopped.I1);
    }

    [Fact]
    public void Enforces_total_size_cap()
    {
        const int mib = 1024 * 1024;
        var old = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var p = Path.Combine(_dir, $"old{i}.ibt");
            File.WriteAllBytes(p, new byte[mib]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-10 + i));
            old.Add(p);
        }
        string yaml = Yaml();
        using (var rec = new TelemetryRecorder(_dir, DiagLog.Null, 3.5 * mib / (1024.0 * 1024 * 1024)))
        {
            rec.OnSessionInfo(yaml, 1, Info(yaml));
            var f = SyntheticRace.Frames(1, withCarIdx: false).First();
            rec.OnFrame(in f);
            rec.OnSourceEnded();
        }
        Assert.False(File.Exists(old[0]), "oldest must be deleted");
        Assert.True(File.Exists(old[2]));
        Assert.Equal(3, Files.Length);
    }

    [Fact]
    public void Frames_before_session_info_are_kept_until_it_arrives()
    {
        string yaml = Yaml();
        using var rec = new TelemetryRecorder(_dir, DiagLog.Null, 1, ringCapacity: 1024);
        var f = SyntheticRace.Frames(1, withCarIdx: false).First();
        for (int i = 0; i < 10; i++) rec.OnFrame(in f);
        rec.OnSessionInfo(yaml, 1, Info(yaml));
        rec.OnSourceEnded();
        Assert.Equal(10, ReadAll(Assert.Single(Files)).Count);
    }
}
