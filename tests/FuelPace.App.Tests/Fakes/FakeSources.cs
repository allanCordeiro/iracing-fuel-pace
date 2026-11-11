using System.Collections.Concurrent;
using FuelPace.App.Host;
using FuelPace.App.Recording;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Tests.Fakes;

/// <summary>
/// Replays a synthetic <c>.ibt</c> and moves the <see cref="ManualClock"/> 1/60 s per frame, so the Agent's wall clock
/// follows the data. Gated: frames are handed out only as the test allows them (NoData otherwise).
/// </summary>
public sealed class FakeSource : ITelemetrySource
{
    private static readonly TimeSpan FrameTime = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);
    private readonly IbtTelemetrySource _inner;
    private readonly ManualClock _clock;
    private readonly bool _endsDisconnected;
    private long _budget;
    private int _consumed, _disposed, _idleAt = -1;
    private bool _ended;

    public FakeSource(string ibtPath, ManualClock clock, bool gated = false, bool endsDisconnected = false)
    {
        _inner = new IbtTelemetrySource(ibtPath);
        _clock = clock;
        _endsDisconnected = endsDisconnected;
        _budget = gated ? 0 : long.MaxValue;
    }

    public int Frames => _inner.RecordCount;
    public int Consumed => Volatile.Read(ref _consumed);
    public bool Disposed => Volatile.Read(ref _disposed) != 0;
    /// <summary>Frames handed out when the source last had nothing to give: the loop is done with all of them.</summary>
    public int IdleAt => Volatile.Read(ref _idleAt);
    public VarLayout Layout => _inner.Layout;
    public int SessionInfoVersion => 1;
    public string ReadSessionInfoYaml() => _inner.ReadSessionInfoYaml();

    public void Allow(int frames) => Interlocked.Add(ref _budget, frames);

    /// <summary>Lets every remaining frame through.</summary>
    public void Release() => Interlocked.Exchange(ref _budget, long.MaxValue);

    public ReadResult TryRead(ref TelemetryFrame frame)
    {
        if (_ended) return _endsDisconnected ? ReadResult.Disconnected : ReadResult.EndOfStream;
        if (Volatile.Read(ref _budget) <= 0)
        {
            Volatile.Write(ref _idleAt, _consumed);
            Thread.Sleep(1);
            return ReadResult.NoData;
        }
        var r = _inner.TryRead(ref frame);
        if (r != ReadResult.Frame)
        {
            _ended = true;
            return _endsDisconnected ? ReadResult.Disconnected : r;
        }
        if (Volatile.Read(ref _budget) != long.MaxValue) Interlocked.Decrement(ref _budget);
        _clock.Advance(FrameTime);
        Interlocked.Increment(ref _consumed);
        return r;
    }

    /// <summary>Waits until the source has handed out <paramref name="n"/> frames.</summary>
    public void WaitConsumed(int n)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (Consumed < n)
        {
            Assert.True(DateTime.UtcNow < end, $"source stuck at {Consumed}/{n} frames");
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _inner.Dispose();
    }
}

/// <summary>Hands out the scripted sources in order (null = the sim is not running), then null forever.</summary>
public sealed class FakeSourceFactory : ITelemetrySourceFactory
{
    private readonly ConcurrentQueue<Func<ITelemetrySource?>> _script = new();
    public int Calls;

    public FakeSourceFactory Then(Func<ITelemetrySource?> next)
    {
        _script.Enqueue(next);
        return this;
    }

    public FakeSourceFactory ThenNone(int times = 1)
    {
        for (int i = 0; i < times; i++) _script.Enqueue(() => null);
        return this;
    }

    public ITelemetrySource? TryOpen()
    {
        Interlocked.Increment(ref Calls);
        return _script.TryDequeue(out var next) ? next() : null;
    }
}

public static class SyntheticIbt
{
    /// <summary>Writes <paramref name="frames"/> under <paramref name="yaml"/> as a 60 Hz <c>.ibt</c>.</summary>
    public static string Write(string path, string yaml, IEnumerable<TelemetryFrame> frames, bool includeCarIdx = false)
    {
        using var w = new IbtStreamWriter(path, 60, reservedYamlBytes: 16 * 1024, includeCarIdx: includeCarIdx);
        w.WriteSessionInfo(yaml);
        foreach (var f in frames) w.Append(f);
        return path;
    }
}
