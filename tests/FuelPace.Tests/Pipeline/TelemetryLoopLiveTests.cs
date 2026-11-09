using FuelPace.Core.Diagnostics;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Pipeline;

/// <summary>Disconnect/reconnect handling and the session-info worker of <see cref="TelemetryLoop"/>, with a scripted source.</summary>
public class TelemetryLoopLiveTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    sealed class FakeSource : ITelemetrySource
    {
        public readonly Queue<ReadResult> Script = new();
        public VarLayout Layout { get; set; } = VarLayout.Resolve([]);
        public int SessionInfoVersion { get; set; } = 1;
        public string Yaml = SampleYaml.Zolder;
        public Func<string>? YamlHook;
        public ReadResult TryRead(ref TelemetryFrame frame) => Script.Count > 0 ? Script.Dequeue() : ReadResult.NoData;
        public string ReadSessionInfoYaml() => YamlHook?.Invoke() ?? Yaml;
        public void Dispose() { }
    }

    sealed class Tap : IFrameTap
    {
        public int Frames, Ended;
        public readonly List<(string yaml, int version)> Sessions = [];
        public void OnFrame(in TelemetryFrame f) => Frames++;
        public void OnSessionInfo(string yaml, int version, SessionInfo info) { lock (Sessions) Sessions.Add((yaml, version)); }
        public void OnSourceEnded() => Ended++;
    }

    static TelemetryLoop Make(FakeSource src, Tap tap, DiagLog log, bool inline = true) =>
        new(src, new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(T0), null, null, log), null, inline, log, tap);

    static DiagRecord[] Drain(DiagLog log)
    {
        var buf = new DiagRecord[256];
        return buf.AsSpan(0, log.DrainRecords(buf)).ToArray();
    }

    [Fact]
    public void Disconnect_then_reconnect_reloads_layout_and_session_info_and_frames_continue()
    {
        var log = new DiagLog(DiagLevel.Info);
        var src = new FakeSource();
        var tap = new Tap();
        var loop = Make(src, tap, log);

        src.Script.Enqueue(ReadResult.Frame);
        Assert.True(loop.Step());
        Assert.Equal(1, tap.Frames);
        Assert.Single(tap.Sessions);

        src.Script.Enqueue(ReadResult.Disconnected);
        Assert.True(loop.Step());
        Assert.Equal(1, tap.Ended);

        // iRacing restarted: new layout (one more variable present) and new session info
        src.Layout = VarLayout.Resolve([new VarHeader(VarType.Float, 0, 1, false, "FuelLevel", "", "l")]);
        src.SessionInfoVersion = 1; // restarted counters can repeat the old value
        src.Yaml = SampleYaml.WithSessions(SampleYaml.SessionBlock(0, "Race", "10", "unlimited"));
        src.Script.Enqueue(ReadResult.Frame);
        Assert.True(loop.Step());
        src.Script.Enqueue(ReadResult.Frame);
        Assert.True(loop.Step());

        Assert.Equal(3, tap.Frames);
        Assert.Equal(2, tap.Sessions.Count);
        Assert.Equal(src.Yaml, tap.Sessions[1].yaml);

        var layouts = Drain(log).Where(r => r.Event == DiagEvent.LayoutResolved).ToArray();
        Assert.Equal(2, layouts.Length); // constructor + after the reconnection
        Assert.Equal(layouts[0].I0 - 1, layouts[1].I0);
    }

    [Fact]
    public void Disconnected_then_end_of_stream_flushes_once()
    {
        var log = new DiagLog(DiagLevel.Info);
        var src = new FakeSource();
        var tap = new Tap();
        var loop = Make(src, tap, log);
        src.Script.Enqueue(ReadResult.Frame);
        src.Script.Enqueue(ReadResult.Disconnected);
        src.Script.Enqueue(ReadResult.Disconnected);
        src.Script.Enqueue(ReadResult.EndOfStream);
        src.Script.Enqueue(ReadResult.EndOfStream);
        Assert.True(loop.Step());
        Assert.True(loop.Step());
        Assert.True(loop.Step()); // repeated Disconnected: nothing more
        Assert.False(loop.Step());
        Assert.False(loop.Step());

        Assert.Equal(1, tap.Ended);
        Assert.Single(Drain(log), r => r.Event == DiagEvent.SourceEnded);
    }

    [Fact]
    public void Each_connection_ends_once()
    {
        var log = new DiagLog(DiagLevel.Info);
        var src = new FakeSource();
        var tap = new Tap();
        var loop = Make(src, tap, log);
        foreach (var r in new[] { ReadResult.Frame, ReadResult.Disconnected, ReadResult.Frame, ReadResult.EndOfStream })
            src.Script.Enqueue(r);
        loop.RunToEnd();
        Assert.Equal(2, tap.Ended);
    }

    [Fact]
    public void Background_session_info_uses_one_worker_and_newest_version_wins()
    {
        var log = new DiagLog(DiagLevel.Info);
        var src = new FakeSource();
        var tap = new Tap();
        var loop = Make(src, tap, log, inline: false);

        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        var threads = new HashSet<int>();
        int concurrent = 0, maxConcurrent = 0, calls = 0;
        src.YamlHook = () =>
        {
            lock (threads) threads.Add(Environment.CurrentManagedThreadId);
            int c = Interlocked.Increment(ref concurrent);
            int m;
            while (c > (m = Volatile.Read(ref maxConcurrent))) Interlocked.CompareExchange(ref maxConcurrent, c, m);
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); gate.Wait(TestContext.Current.CancellationToken); }
            Interlocked.Decrement(ref concurrent);
            return SampleYaml.Zolder;
        };

        src.Script.Enqueue(ReadResult.Frame);
        Assert.True(loop.Step());                  // version 1 -> worker blocks inside the first load
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        for (int v = 2; v <= 4; v++)
        {
            src.SessionInfoVersion = v;
            src.Script.Enqueue(ReadResult.Frame);
            Assert.True(loop.Step());
        }
        gate.Set();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (tap.Sessions) if (tap.Sessions.Count > 0) break; Thread.Sleep(5); }
        Thread.Sleep(100); // let a (wrong) extra load show up
        lock (tap.Sessions) Assert.Equal([4], tap.Sessions.Select(s => s.version));
        Assert.Equal(2, Volatile.Read(ref calls));  // 1 (superseded) and 4; 2 and 3 were overwritten in the slot
        Assert.Equal(1, maxConcurrent);
        Assert.Single(threads);
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);
    }

    [Fact]
    public void Dispose_stops_the_worker_and_no_load_runs_after_it()
    {
        var src = new FakeSource();
        var tap = new Tap();
        var loop = Make(src, tap, new DiagLog(DiagLevel.Info), inline: false);
        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        Thread? worker = null;
        int calls = 0;
        src.YamlHook = () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { worker = Thread.CurrentThread; entered.Set(); gate.Wait(TimeSpan.FromSeconds(5)); }
            return SampleYaml.Zolder;
        };
        src.Script.Enqueue(ReadResult.Frame);
        Assert.True(loop.Step());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        src.SessionInfoVersion = 2; // pending while the first load is in flight
        src.Script.Enqueue(ReadResult.Frame);
        Assert.True(loop.Step());
        gate.Set();
        loop.Dispose();

        Assert.False(worker!.IsAlive);
        Assert.Equal(1, Volatile.Read(ref calls)); // the pending version 2 never ran
        Thread.Sleep(50);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Disconnect_drops_the_pending_load()
    {
        var src = new FakeSource();
        var tap = new Tap();
        var loop = Make(src, tap, new DiagLog(DiagLevel.Info), inline: false);
        var entered = new ManualResetEventSlim();
        var gate = new ManualResetEventSlim();
        int calls = 0;
        src.YamlHook = () =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); gate.Wait(TimeSpan.FromSeconds(5)); }
            return SampleYaml.Zolder;
        };
        src.Script.Enqueue(ReadResult.Frame);
        loop.Step();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        src.SessionInfoVersion = 2;
        src.Script.Enqueue(ReadResult.Frame);
        loop.Step();
        src.Script.Enqueue(ReadResult.Disconnected);
        loop.Step();
        gate.Set();
        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Empty(tap.Sessions);
        loop.Dispose();
    }
}
