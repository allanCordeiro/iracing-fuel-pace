using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Bench;

/// <summary>
/// Cost of the telemetry hot path: <see cref="TelemetryLoop.Step"/> (read the record, decode the frame, lap detection, stint,
/// leader pace, strategy at 10 Hz, overlay publish) over the synthetic time race with a baseline. One invocation is one lap
/// at 60 Hz, 4 800 ticks, so mean / 4 800 is the cost of a tick. The <c>Allocated</c> column must show <c>-</c>.
/// Each iteration is exactly three invocations (three laps) and reopens the file in <see cref="IterationSetup"/>, outside the
/// measurement, so the file's own allocations never reach the column.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(ThreeLapsPerIteration))]
public class PipelineTickBench
{
    private sealed class ThreeLapsPerIteration : ManualConfig
    {
        public ThreeLapsPerIteration() => AddJob(Job.Default.WithStrategy(RunStrategy.Throughput).WithLaunchCount(1)
            .WithWarmupCount(3).WithIterationCount(10).WithInvocationCount(3).WithUnrollFactor(1));
    }

    private const int TicksPerLap = 4800, SessionStartTicks = 200;

    private string _dir = "";
    private string _path = "";
    private SessionInfo _info = null!;
    private IbtTelemetrySource _source = null!;
    private TelemetryPipeline _pipeline = null!;
    private TelemetryLoop _loop = null!;
    private long _records;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fuelpace-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = SyntheticRace.WriteIbt(Path.Combine(_dir, "synthetic.ibt"), laps: 13, withCarIdx: true);
        _info = SessionInfoParser.Parse(SyntheticRace.Yaml);
        Open();
    }

    [IterationSetup]
    public void IterationSetup() => Open();

    [GlobalCleanup]
    public void Cleanup()
    {
        _source.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void Open()
    {
        _source?.Dispose();
        _source = new IbtTelemetrySource(_path);
        _pipeline = new TelemetryPipeline(new StrategyConfig(), TimeProvider.System, store: null, io: null);
        _loop = new TelemetryLoop(_source, _pipeline, store: null, inlineSessionInfo: true);
        _loop.Step();
        _pipeline.OnSessionContext(_info, SyntheticRace.Profile(_info));
        // The first ticks of a session allocate a few objects once (context pickup, first strategy run); they are not per-tick cost.
        for (_records = 1; _records <= SessionStartTicks; _records++) _loop.Step();
    }

    /// <summary>One lap at 60 Hz (4 800 steps). Reopens the file if it runs out (only when the job is overridden from the command line).</summary>
    [Benchmark]
    public void OneLap()
    {
        for (int i = 0; i < TicksPerLap; i++)
        {
            if (_records >= _source.RecordCount) Open();
            _loop.Step();
            _records++;
        }
    }
}
