using FuelPace.Core.Diagnostics;
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
    static Result Run(string path, string yaml, DiagLog? log = null, bool tap = false, bool baseline = true)
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
        int gen0 = GC.CollectionCount(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Measured; i++)
            if (!loop.Step()) Assert.Fail("unexpected end of stream");
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        return new Result(bytes, GC.CollectionCount(0) - gen0, pipeline.Overlay.PublishCount - publishes, ring?.Count ?? 0, pipeline.Overlay.Read());
    }

    static string Synthetic() => SyntheticRace.WriteIbt(Fixtures.TempPath(".ibt"), laps: 12, withCarIdx: true);

    [Fact]
    public void Pipeline_allocates_zero_bytes_per_tick_synthetic()
    {
        var path = Synthetic();
        try
        {
            var r = Run(path, SyntheticRace.Yaml);
            Assert.Equal(0, r.Bytes);
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
        var r = Run(path, yaml);
        Assert.Equal(0, r.Bytes);
        Assert.True(r.Publishes > 0);
    }

    [Fact]
    public void No_gen0_collections_during_replay()
    {
        var path = Synthetic();
        try
        {
            var r = Run(path, SyntheticRace.Yaml);
            Assert.Equal(0, r.Gen0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Zero_bytes_with_debug_logging_and_frame_tap()
    {
        var path = Synthetic();
        var log = new DiagLog(DiagLevel.Trace, 1 << 14);
        using var stop = new CancellationTokenSource();
        var drain = new Thread(() =>
        {
            var buf = new DiagRecord[4096];
            while (!stop.IsCancellationRequested) { if (log.DrainRecords(buf) == 0) Thread.Sleep(1); }
        }) { IsBackground = true };
        drain.Start();
        try
        {
            var r = Run(path, SyntheticRace.Yaml, log, tap: true);
            Assert.Equal(0, r.Bytes);
            Assert.Equal(1 + Warmup + Measured, r.RingFrames);
        }
        finally { stop.Cancel(); drain.Join(); File.Delete(path); }
    }
}
