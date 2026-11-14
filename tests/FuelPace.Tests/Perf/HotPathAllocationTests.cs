using FuelPace.Core.Diagnostics;
using FuelPace.Core.Display;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Perf;

/// <summary>PERF-03: the steady-state tick allocates nothing, even with Trace diagnostics and a frame tap attached.</summary>
[Collection(PerfCollection.Name)]
public class HotPathAllocationTests
{
    const int Warmup = 1000, Measured = 10_000;

    /// <summary>Copies every frame into a pre-allocated ring, like the App's telemetry recorder.</summary>
    sealed class RingTap : IFrameTap
    {
        readonly TelemetryFrame[] _ring = new TelemetryFrame[256];
        public int Count;
        public void OnFrame(in TelemetryFrame f) => _ring[Count++ & 255] = f;
        public void OnSessionInfo(string yaml, int version, SessionInfo info) { }
        public void OnSourceEnded() { }
    }

    sealed record Result(long Bytes, int Gen0, long Publishes, int RingFrames, OverlayState Final);

    /// <summary>Replays <paramref name="path"/>: warm-up, then <see cref="Measured"/> steps measured on this thread.</summary>
    static Result Run(string test, string path, string yaml, DiagLog? log = null, bool tap = false, bool baseline = true)
    {
        var info = SessionInfoParser.Parse(yaml);
        using var source = new IbtTelemetrySource(path);
        var pipeline = new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(SyntheticRace.T0),
            store: null, io: null, log);
        var ring = tap ? new RingTap() : null;
        var loop = new TelemetryLoop(source, pipeline, store: null, inlineSessionInfo: true, log, ring);
        Assert.True(loop.Step());
        if (baseline) pipeline.OnSessionContext(info, SyntheticRace.Profile(info));
        for (int i = 0; i < Warmup; i++) Assert.True(loop.Step());
        Assert.True(source.RecordCount >= Warmup + Measured + 2, "replay too short for the measured window");

        long publishes = pipeline.Overlay.PublishCount;
        var window = AllocationMeasure.Start(test);
        for (int i = 0; i < Measured; i++)
            if (!loop.Step()) Assert.Fail("unexpected end of stream");
        return new Result(window.Bytes, window.Gen0, pipeline.Overlay.PublishCount - publishes, ring?.Count ?? 0, pipeline.Overlay.Read());
    }

    static string Synthetic() => SyntheticRace.WriteIbt(Fixtures.TempPath(".ibt"), laps: 12, withCarIdx: true);

    [Fact]
    public void Pipeline_allocates_zero_bytes_per_tick_synthetic()
    {
        var path = Synthetic();
        try
        {
            var r = Run(nameof(Pipeline_allocates_zero_bytes_per_tick_synthetic), path, SyntheticRace.Yaml);
            Assert.True(r.Bytes == 0, $"allocated {r.Bytes} bytes");
            Assert.InRange(r.Publishes, Measured / 6, Measured / 6 + 1); // the strategy really ran (10 Hz)
            Assert.True(SyntheticRace.LapsCrossed(Warmup + 1, Warmup + 1 + Measured) >= 2);
            // The strategy ran on real data: laps were completed, the baseline and the time race are in use.
            Assert.True(r.Final.LastLapL > 1);
            Assert.NotEqual(SignalState.Inactive, r.Final.State);
            Assert.NotEqual(SignalState.NoBaseline, r.Final.State);
            Assert.False(r.Final.BopMismatch);
            Assert.True(r.Final.LapsRemaining > 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pipeline_allocates_zero_bytes_per_tick_fixture()
    {
        var path = Fixtures.RequireZolder();
        string yaml;
        using (var s = new IbtTelemetrySource(path)) yaml = s.ReadSessionInfoYaml();
        var r = Run(nameof(Pipeline_allocates_zero_bytes_per_tick_fixture), path, yaml);
        Assert.True(r.Bytes == 0, $"allocated {r.Bytes} bytes");
        Assert.True(r.Publishes > 0);
    }

    /// <summary>V3-8: the v3 builder and the number formatting allocate nothing over 10,000 calls, in every state.</summary>
    [Fact]
    public void Glance_builder_and_number_format_allocate_zero_bytes()
    {
        var cfg = GlanceConfig.Default with { ComplementSeconds = true };
        var states = new OverlayState[6];
        for (int k = 0; k < states.Length; k++)
            states[k] = new OverlayState
            {
                State = (SignalState)k, FuelL = 74.04, LapsRemaining = 31.3, LapProjectionL = 2.385, LastLapL = 2.39, MeanL = 2.37,
                TargetL = 2.296875, FeasibleMinL = 2.23, PaceS = 108, FuelVsTimeB = -11.4, Recording = k % 2 == 0,
                LastLapTimeS = 108.456, MeanLapTimeS = 108.1, BaselineLapTimeS = 107.9,
            };
        Span<char> buf = stackalloc char[32];
        int sink = 0;

        for (int i = 0; i < Warmup; i++)
            sink += Once(states, i, cfg, buf);

        var window = AllocationMeasure.Start(nameof(Glance_builder_and_number_format_allocate_zero_bytes));
        for (int i = 0; i < Measured; i++)
            sink += Once(states, i, cfg, buf);
        long bytes = window.Bytes;
        Assert.True(bytes == 0, $"allocated {bytes} bytes");
        Assert.NotEqual(0, sink);
    }

    static int Once(OverlayState[] states, int i, in GlanceConfig cfg, Span<char> buf)
    {
        var v = GlanceViewBuilder.Build(in states[i % states.Length], in cfg);
        int n = NumberFormat.Fixed(v.DecisionValue, 2, buf);
        n += NumberFormat.Fixed(-v.Fuel, 1, buf);
        n += NumberFormat.Count(v.LapsPossible, buf);
        n += NumberFormat.Percent(v.ComplementPct, buf);
        n += NumberFormat.SignedSeconds(v.ComplementSec, buf);
        return n + (v.Equals(default) ? 1 : 0);
    }

    [Fact]
    public void No_gen0_collections_during_replay()
    {
        var path = Synthetic();
        try
        {
            var r = Run(nameof(No_gen0_collections_during_replay), path, SyntheticRace.Yaml);
            Assert.True(r.Gen0 == 0, $"{r.Gen0} gen0 collections");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Zero_bytes_with_debug_logging_and_frame_tap()
    {
        var path = Synthetic();
        var log = new DiagLog(DiagLevel.Trace, 1 << 14);
        long drained = 0;
        using var stop = new CancellationTokenSource();
        var drain = new Thread(() =>
        {
            var buf = new DiagRecord[4096];
            while (!stop.IsCancellationRequested)
            {
                int n = log.DrainRecords(buf);
                if (n == 0) Thread.Sleep(1); else Interlocked.Add(ref drained, n);
            }
        }) { IsBackground = true };
        drain.Start();
        try
        {
            var r = Run(nameof(Zero_bytes_with_debug_logging_and_frame_tap), path, SyntheticRace.Yaml, log, tap: true);
            Assert.True(r.Bytes == 0, $"allocated {r.Bytes} bytes");
            Assert.Equal(1 + Warmup + Measured, r.RingFrames);
            Assert.True(Interlocked.Read(ref drained) > 0, "the drain thread never saw a record");
        }
        finally { stop.Cancel(); drain.Join(); File.Delete(path); }
    }
}
