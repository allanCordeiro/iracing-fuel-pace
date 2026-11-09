using System.Globalization;
using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Baselines;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Pipeline;

public class TelemetryPipelineTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    const string Key = "ligierjsp320__zolder alt";

    static string LapsRaceYaml(int laps) =>
        SampleYaml.WithSessions(SampleYaml.SessionBlock(0, "Race", laps.ToString(CultureInfo.InvariantCulture), "unlimited"));

    static string TimeRaceYaml(double seconds) =>
        SampleYaml.WithSessions(SampleYaml.SessionBlock(0, "Race", "unlimited",
            seconds.ToString("F4", CultureInfo.InvariantCulture) + " sec"));

    static string PracticeAndRaceYaml => SampleYaml.WithSessions(
        SampleYaml.SessionBlock(0, "Practice", "unlimited", "7200.0000 sec"),
        SampleYaml.SessionBlock(1, "Race", "25", "unlimited"));

    static ProfileDocument DocWithFixtureBaseline(SessionInfo info)
    {
        var doc = ProfileDocument.CreateFor(info);
        doc.Baseline = BaselineCalculator.Compute(BaselineCalculatorTests.Fixture6, 100, [], T0);
        return doc;
    }

    static DiagRecord[] Drain(DiagLog log)
    {
        var buf = new DiagRecord[1 << 17];
        return buf.AsSpan(0, log.DrainRecords(buf)).ToArray();
    }

    /// <summary>Writes the frames to a temporary .ibt and drives a <see cref="TelemetryLoop"/> over it one frame at a time.</summary>
    sealed class Rig : IDisposable
    {
        readonly string _path = Fixtures.TempPath(".ibt");
        readonly IbtTelemetrySource _source;
        public readonly TelemetryFrame[] Frames;
        public readonly SessionInfo Info;
        public readonly TelemetryPipeline Pipeline;
        public readonly TelemetryLoop Loop;
        public int Index;

        public Rig(IEnumerable<TelemetryFrame> frames, string yaml, bool includeCarIdx = false, string fuelUnit = "l",
            ProfileStore? store = null, BackgroundWorkQueue? io = null, DiagLog? log = null, TimeProvider? clock = null,
            StrategyConfig? cfg = null)
        {
            Frames = frames.ToArray();
            Info = SessionInfoParser.Parse(yaml);
            IbtFileBuilder.WriteFrames(_path, yaml, Frames, includeCarIdx, fuelUnit);
            _source = new IbtTelemetrySource(_path);
            Pipeline = new TelemetryPipeline(cfg ?? new StrategyConfig(), clock ?? new FixedTimeProvider(T0), store, io, log);
            Loop = new TelemetryLoop(_source, Pipeline, store, inlineSessionInfo: true, log);
        }

        public ref readonly TelemetryFrame Current => ref Frames[Index - 1];

        public void Step()
        {
            Assert.True(Loop.Step());
            Index++;
        }

        public void StepUntil(Func<TelemetryFrame, bool> condition)
        {
            while (Index < Frames.Length)
            {
                Step();
                if (condition(Current)) return;
            }
            Assert.Fail("condition never met");
        }

        public void StepAll()
        {
            while (Index < Frames.Length) Step();
        }

        /// <summary>The strategy runs on ticks 0, 6, 12, …: the frame behind the last publish.</summary>
        public TelemetryFrame LastPublishedFrame => Frames[(Index - 1) - (Index - 1) % 6];

        public void Dispose()
        {
            _source.Dispose();
            File.Delete(_path);
        }
    }

    sealed class TempDir : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fuelpace-tests", Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (DirectoryNotFoundException) { } }
    }

    static int ValidCount(CaptureRecord r) => r.Laps.Count(l => l.Valid);

    [Fact]
    public void Strategy_runs_at_10hz()
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20).SessionLaps(10).Run(0, endPct: 1).Take(600), LapsRaceYaml(10));
        rig.StepAll();
        Assert.Equal(600, rig.Index);
        Assert.Equal(100, rig.Pipeline.Overlay.PublishCount);
    }

    [Fact]
    public void End_to_end_laps_race_shows_last_mean_and_state()
    {
        var log = new DiagLog(DiagLevel.Trace, 1 << 16);
        using var rig = new Rig(new LapSim(lapTimeS: 20, fuelPerLapL: 2.4).SessionLaps(10).Run(4, endPct: 0.5),
            LapsRaceYaml(10), includeCarIdx: true, log: log);
        rig.Step();
        rig.Pipeline.OnSessionContext(rig.Info, DocWithFixtureBaseline(rig.Info));
        rig.StepAll();

        var s = rig.Pipeline.Overlay.Read();
        var f = rig.LastPublishedFrame;
        Assert.Equal(4, f.LapCompleted);
        Assert.Equal(2.4, s.LastLapL, 2);
        Assert.False(s.LastLapInvalid);
        Assert.Equal(2.4, s.MeanL, 2);
        Assert.False(s.MeanFromBaseline);
        Assert.Equal(10 - f.LapCompleted - f.LapDistPct + 1, s.LapsRemaining, 0.01);
        Assert.Equal(SignalState.Blue, s.State);
        Assert.Equal(InsightKind.Slack, s.Insight.Kind);

        var traces = Drain(log).Where(r => r.Event == DiagEvent.StrategyTrace).ToArray();
        Assert.NotEmpty(traces);
        Assert.All(traces, r => Assert.Equal((int)RaceKind.Laps, r.I0));
    }

    [Fact]
    public void Version_does_not_change_when_nothing_visible_changes()   // PERF-06 (Core part)
    {
        var frame = new LapSim(lapTimeS: 20).SessionLaps(10).Run(0, endPct: 0.5).Last();
        using var rig = new Rig(Enumerable.Repeat(frame, 600), LapsRaceYaml(10));
        rig.Step();
        Assert.Equal(1, rig.Pipeline.Overlay.Version);
        rig.StepAll();
        Assert.Equal(1, rig.Pipeline.Overlay.Version);
        Assert.Equal(100, rig.Pipeline.Overlay.PublishCount);
    }

    [Fact]
    public void Capture_start_stop_persists_profile_with_baseline()
    {
        using var dir = new TempDir();
        using var io = new BackgroundWorkQueue("test-io");
        var store = new ProfileStore(dir.Path);
        var log = new DiagLog(DiagLevel.Info, 1 << 16);
        using var rig = new Rig(new LapSim(lapTimeS: 20).SessionLaps(30).Run(8, endPct: 0.5), LapsRaceYaml(30),
            store: store, io: io, log: log);
        var completed = new List<(CaptureRecord Record, string Key)>();
        rig.Pipeline.CaptureCompleted += (r, k) => completed.Add((r, k));

        rig.Pipeline.RequestCaptureStart();
        rig.Step();
        Assert.True(rig.Pipeline.CaptureActive);
        rig.StepUntil(f => f.Lap == 7 && f.LapDistPct >= 0.35); // lap 6 resolved (constant times: 5 s timeout)
        var during = rig.Pipeline.Overlay.Read();
        Assert.True(during.Recording);
        Assert.Equal(6, during.RecTotal);
        Assert.Equal(5, during.RecValid); // lap 1 starts with the file: incomplete

        rig.Pipeline.RequestCaptureStop();
        for (int i = 0; i < 12; i++) rig.Step();
        Assert.False(rig.Pipeline.CaptureActive);
        Assert.False(rig.Pipeline.Overlay.Read().Recording);

        var (record, key) = Assert.Single(completed);
        Assert.Equal(Key, key);
        Assert.Equal(7, record.Laps.Count); // + lap 7 in progress
        Assert.Equal(5, ValidCount(record));
        Assert.Equal(LapReasons.Incomplete, record.Laps[^1].Reasons);

        Assert.True(io.WaitIdle(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(store.PathFor(rig.Info.CarPath, rig.Info.TrackName)));
        var doc = store.Load(rig.Info.CarPath, rig.Info.TrackName)!;
        var capture = Assert.Single(doc.Captures);
        Assert.True(capture.Laps.Count(l => l.Valid) >= 5);
        Assert.NotNull(doc.Baseline);

        // The saved document becomes the session context: the baseline is now in use.
        rig.StepAll();
        Assert.NotEqual(SignalState.NoBaseline, rig.Pipeline.Overlay.Read().State);

        var records = Drain(log);
        Assert.Contains(records, r => r.Event == DiagEvent.CaptureStarted && r.I0 == 1);
        var stopped = Assert.Single(records, r => r.Event == DiagEvent.CaptureStopped);
        Assert.Equal(5, stopped.I0);
        Assert.Equal(7, stopped.I1);
        Assert.Equal(0, stopped.A);
    }

    [Fact]
    public void Capture_stop_while_lap_waits_for_its_official_time_keeps_that_lap()
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20).Run(7, endPct: 0.5), LapsRaceYaml(30));
        CaptureRecord? record = null;
        rig.Pipeline.CaptureCompleted += (r, _) => record = r;

        rig.Pipeline.RequestCaptureStart();
        rig.StepUntil(f => f.Lap == 7 && f.LapDistPct >= 0.05); // 1 s past the line: lap 6 still waits (≤ 5 s)
        rig.Pipeline.RequestCaptureStop();
        rig.Step();

        Assert.NotNull(record);
        var lap6 = Assert.Single(record.Laps, l => l.Lap == 6);
        Assert.Equal(LapReasons.None, lap6.Reasons);
        Assert.Equal(20, lap6.TimeS!.Value, 0.05);
        var last = record.Laps[^1];
        Assert.Equal(7, last.Lap);
        Assert.Equal(LapReasons.Incomplete, last.Reasons);
        Assert.Equal(7, record.Laps.Count);
    }

    [Fact]
    public void Session_change_while_capturing_auto_stops_into_old_profile()   // Review Focus 1
    {
        using var dir = new TempDir();
        using var io = new BackgroundWorkQueue("test-io");
        var store = new ProfileStore(dir.Path);
        var log = new DiagLog(DiagLevel.Info, 1 << 16);
        using var rig = new Rig(new LapSim(lapTimeS: 20).SessionLaps(30).Run(9, endPct: 0.5), LapsRaceYaml(30),
            store: store, io: io, log: log);
        var completed = new List<(CaptureRecord Record, string Key)>();
        rig.Pipeline.CaptureCompleted += (r, k) => completed.Add((r, k));

        rig.Pipeline.RequestCaptureStart();
        rig.StepUntil(f => f.Lap == 6 && f.LapDistPct >= 0.35); // laps 1–5 recorded, 2–5 valid
        var other = rig.Info with { CarPath = "othercar" };
        rig.Pipeline.OnSessionContext(other, null);
        rig.Step();

        Assert.False(rig.Pipeline.CaptureActive);
        var (record, key) = Assert.Single(completed);
        Assert.Equal(Key, key);
        Assert.Equal(6, record.Laps.Count);

        rig.StepUntil(f => f.Lap == 9 && f.LapDistPct >= 0.35); // more laps, now under the new car
        Assert.True(io.WaitIdle(TimeSpan.FromSeconds(10)));
        var old = store.Load(rig.Info.CarPath, rig.Info.TrackName)!;
        var capture = Assert.Single(old.Captures);
        Assert.Equal(6, capture.Laps.Count);
        Assert.Equal(6, capture.Laps.Max(l => l.Lap));
        Assert.NotNull(old.Baseline);
        Assert.False(File.Exists(store.PathFor(other.CarPath, other.TrackName)));

        // Saving the old profile must not bring its context back: the new car still has no baseline.
        rig.StepAll();
        Assert.Equal(SignalState.NoBaseline, rig.Pipeline.Overlay.Read().State);

        var stopped = Assert.Single(Drain(log), r => r.Event == DiagEvent.CaptureStopped);
        Assert.Equal(1, stopped.A);
    }

    [Fact]
    public void Session_number_change_while_capturing_auto_stops()
    {
        var frames = new LapSim(lapTimeS: 20).Run(5, endPct: 0.5).ToArray();
        for (int i = frames.Length / 2; i < frames.Length; i++) frames[i].SessionNum = 1;
        using var rig = new Rig(frames, PracticeAndRaceYaml);
        string? key = null;
        rig.Pipeline.CaptureCompleted += (_, k) => key = k;

        rig.Pipeline.RequestCaptureStart();
        rig.StepUntil(f => f.SessionNum == 0 && rig.Index == frames.Length / 2);
        Assert.True(rig.Pipeline.CaptureActive);
        rig.Step();
        Assert.False(rig.Pipeline.CaptureActive);
        Assert.Equal(Key, key);
    }

    [Fact]
    public void Pit_road_makes_overlay_inactive()
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20).PitRoad(2, 0.3, 0.6).SessionLaps(10).Run(3), LapsRaceYaml(10));
        rig.StepUntil(f => f.Lap == 2 && f.LapDistPct >= 0.2);
        Assert.Equal(SignalState.NoBaseline, rig.Pipeline.Overlay.Read().State);
        rig.StepUntil(f => f.Lap == 2 && f.LapDistPct >= 0.4);
        Assert.Equal(SignalState.Inactive, rig.Pipeline.Overlay.Read().State);
        rig.StepUntil(f => f.Lap == 3 && f.LapDistPct >= 0.2);
        Assert.Equal(SignalState.NoBaseline, rig.Pipeline.Overlay.Read().State);
    }

    [Fact]
    public void Lap_projection_is_hidden_until_a_lap_start_is_observed()   // R13
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20, startPct: 0.3).SessionLaps(10).Run(1, endPct: 0.6), LapsRaceYaml(10));
        rig.StepUntil(f => f.Lap == 1 && f.LapDistPct >= 0.6);
        Assert.True(double.IsNaN(rig.Pipeline.Overlay.Read().LapProjectionL));
        rig.StepUntil(f => f.Lap == 2 && f.LapDistPct >= 0.5);
        Assert.Equal(2.4 * LapSim.FuelCurve(0.5) / 0.5, rig.Pipeline.Overlay.Read().LapProjectionL, 0.02);
    }

    [Fact]
    public void Time_race_without_caridx_uses_own_pace()
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20).LapTimes(20, 21, 22, 23).TimeRace(1800).Run(3, endPct: 0.5),
            TimeRaceYaml(1800), includeCarIdx: false);
        rig.StepAll();

        var s = rig.Pipeline.Overlay.Read();
        var f = rig.LastPublishedFrame;
        Assert.Equal(21.5, s.MeanLapTimeS, 3); // valid laps 2 and 3
        double expected = LapsRemainingCalculator.ByTime(f.SessionTimeRemain, f.LapDistPct, double.NaN, f.LapDistPct, s.MeanLapTimeS);
        Assert.Equal(expected, s.LapsRemaining, 9);
    }

    [Fact]
    public void Electric_car_is_inactive_with_its_insight()
    {
        var log = new DiagLog(DiagLevel.Info);
        using var rig = new Rig(new LapSim(lapTimeS: 20).Run(0, endPct: 0.3), LapsRaceYaml(10), fuelUnit: "kWh", log: log);
        rig.StepAll();

        var s = rig.Pipeline.Overlay.Read();
        Assert.Equal(SignalState.Inactive, s.State);
        Assert.Equal(InsightKind.ElectricUnsupported, s.Insight.Kind);
        var layout = Assert.Single(Drain(log), r => r.Event == DiagEvent.LayoutResolved);
        Assert.Equal(1, layout.A);
    }

    [Fact]
    public void Bop_mismatch_when_no_baseline_condition_matches()
    {
        var frames = new LapSim(lapTimeS: 20).SessionLaps(10).Run(1, endPct: 0.5).ToArray();
        using var rig = new Rig(frames, LapsRaceYaml(10));
        rig.Step();
        var doc = ProfileDocument.CreateFor(rig.Info);
        doc.Baseline = BaselineCalculator.Compute(BaselineCalculatorTests.Fixture6, 100,
            [new BopSignature(2.0, 0, 100)], T0);
        rig.Pipeline.OnSessionContext(rig.Info, doc);
        rig.StepUntil(f => f.Lap == 2 && f.LapDistPct >= 0.4);
        Assert.True(rig.Pipeline.Overlay.Read().BopMismatch);

        doc.Baseline = doc.Baseline! with { Conditions = [new BopSignature(2.0, 0, 100), new BopSignature(0, 0, 100)] };
        rig.Pipeline.OnSessionContext(rig.Info, doc);
        rig.StepAll();
        Assert.False(rig.Pipeline.Overlay.Read().BopMismatch);
    }

    [Fact]
    public void Car_and_track_short_names_follow_decision_3()
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20).Run(0, endPct: 0.1), LapsRaceYaml(10));
        rig.Step();
        Assert.Equal("Ligier", rig.Pipeline.CarShort);
        Assert.Equal("Zolder Alternate", rig.Pipeline.TrackShort);
        Assert.Equal("Ligier", rig.Pipeline.Overlay.CarShort);
        Assert.Equal("Zolder Alternate", rig.Pipeline.Overlay.TrackShort);

        rig.Pipeline.OnSessionContext(rig.Info with { TrackDisplayShortName = "", TrackConfigName = "", CarScreenNameShort = "" }, null);
        Assert.Equal("Ligier JS P320", rig.Pipeline.CarShort);
        Assert.Equal("Zolder Circuit", rig.Pipeline.TrackShort);
    }

    [Fact]
    public void SetConfig_applies_on_the_next_tick()
    {
        using var rig = new Rig(new LapSim(lapTimeS: 20).SessionLaps(10).Run(3, endPct: 0.5), LapsRaceYaml(10));
        rig.StepUntil(f => f.Lap == 4 && f.LapDistPct >= 0.4);
        Assert.False(rig.Pipeline.Overlay.Read().IsStintMode);
        Assert.False(double.IsNaN(rig.Pipeline.Overlay.Read().MeanL));

        rig.Pipeline.SetConfig(new StrategyConfig { Mode = LapsMode.Stint, StintLaps = 20, MeanWindow = 3 });
        for (int i = 0; i < 6; i++) rig.Step();
        var s = rig.Pipeline.Overlay.Read();
        Assert.True(s.IsStintMode);
        Assert.True(double.IsNaN(s.MeanL)); // new MeanWindow: the stint tracker starts over
        Assert.Equal(20 - rig.LastPublishedFrame.LapDistPct, s.LapsRemaining, 1e-6);
    }

    [Fact]
    public void Loop_emits_source_session_and_strategy_diagnostics()
    {
        var log = new DiagLog(DiagLevel.Debug, 1 << 16);
        using var rig = new Rig(new LapSim(lapTimeS: 20).SessionLaps(10).Run(2, endPct: 0.5), LapsRaceYaml(10), log: log);
        rig.StepAll();
        Assert.False(rig.Loop.Step()); // end of stream
        Assert.False(rig.Loop.Step());

        var records = Drain(log);
        var opened = Assert.Single(records, r => r.Event == DiagEvent.SourceOpened);
        Assert.Equal(TelemetryVars.All.Count - 7, opened.I0); // CarIdx* not written
        Assert.Equal(60, opened.A);
        var layout = Assert.Single(records, r => r.Event == DiagEvent.LayoutResolved);
        Assert.Equal(7, layout.I0);
        Assert.Equal(0, layout.I1);
        Assert.Equal(0, layout.A);
        Assert.Equal(1, Assert.Single(records, r => r.Event == DiagEvent.SessionInfoChanged).I0);
        var state = Assert.Single(records, r => r.Event == DiagEvent.StateChanged);
        Assert.Equal((int)SignalState.NoBaseline, state.I0);
        Assert.Equal((int)SignalState.NoBaseline, state.I1);
        Assert.Contains(records, r => r.Event == DiagEvent.InsightChanged && r.I0 == (int)InsightKind.NoBaseline);
        Assert.Contains(records, r => r.Event == DiagEvent.OverlayVersion);
        Assert.Equal(0, Assert.Single(records, r => r.Event == DiagEvent.SourceEnded).I0);
    }

    [Fact]
    public void PerfMinute_every_60_s_of_wall_clock()
    {
        var log = new DiagLog(DiagLevel.Info, 1 << 16);
        var clock = new ManualTimeProvider(T0);
        using var rig = new Rig(new LapSim(lapTimeS: 20).Run(0, endPct: 0.5), LapsRaceYaml(10), log: log, clock: clock);
        for (int i = 0; i < 120; i++) rig.Step();
        Assert.DoesNotContain(Drain(log), r => r.Event == DiagEvent.PerfMinute);

        clock.Advance(TimeSpan.FromSeconds(60));
        rig.Step();
        var perf = Assert.Single(Drain(log), r => r.Event == DiagEvent.PerfMinute);
        Assert.Equal(21, perf.I1); // publishes on ticks 0, 6, …, 120
        Assert.True(perf.A > 0 && perf.B >= perf.A);
        Assert.Equal(0, perf.D);

        for (int i = 0; i < 60; i++) rig.Step();
        Assert.DoesNotContain(Drain(log), r => r.Event == DiagEvent.PerfMinute);
    }

    [Fact]
    public void Frame_tap_sees_session_info_every_frame_and_the_end()
    {
        var tap = new CountingTap();
        var path = Fixtures.TempPath(".ibt");
        try
        {
            var frames = new LapSim(lapTimeS: 20).Run(0, endPct: 0.2).ToArray();
            IbtFileBuilder.WriteFrames(path, LapsRaceYaml(10), frames, includeCarIdx: false);
            using var source = new IbtTelemetrySource(path);
            var pipeline = new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(T0), null, null);
            var loop = new TelemetryLoop(source, pipeline, null, inlineSessionInfo: true, tap: tap);
            loop.RunToEnd();

            Assert.Equal(frames.Length, tap.Frames);
            Assert.Equal(1, tap.SessionInfos);
            Assert.Equal(1, tap.Ended);
            Assert.Equal("Ligier", tap.LastInfo!.CarScreenNameShort);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Background_session_info_reaches_the_pipeline()
    {
        var path = Fixtures.TempPath(".ibt");
        try
        {
            var frames = new LapSim(lapTimeS: 20).Run(0, endPct: 0.2).ToArray();
            IbtFileBuilder.WriteFrames(path, LapsRaceYaml(10), frames, includeCarIdx: false);
            using var source = new IbtTelemetrySource(path);
            var pipeline = new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(T0), null, null);
            var loop = new TelemetryLoop(source, pipeline, null, inlineSessionInfo: false);
            Assert.True(loop.Step());
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (pipeline.CarShort.Length == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Assert.Equal("Ligier", pipeline.CarShort);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fixture_replay_runs_end_to_end()
    {
        var path = Fixtures.RequireZolder();
        using var source = new IbtTelemetrySource(path);
        var pipeline = new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(T0), null, null);
        var loop = new TelemetryLoop(source, pipeline, null, inlineSessionInfo: true);
        loop.RunToEnd();

        Assert.Equal("Ligier JS P320", pipeline.CarShort);
        Assert.Equal("Zolder Alternate", pipeline.TrackShort);
        Assert.Equal((source.RecordCount + 5) / 6, pipeline.Overlay.PublishCount);
        Assert.True(pipeline.Overlay.Version > 1);
    }

    sealed class CountingTap : IFrameTap
    {
        public int Frames, SessionInfos, Ended;
        public SessionInfo? LastInfo;
        public void OnFrame(in TelemetryFrame f) => Frames++;
        public void OnSessionInfo(string yaml, int version, SessionInfo info) { SessionInfos++; LastInfo = info; }
        public void OnSourceEnded() => Ended++;
    }
}
