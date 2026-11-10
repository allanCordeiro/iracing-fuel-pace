using FuelPace.Core.Diagnostics;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Pipeline;

/// <summary>Disconnect/reconnect handling and the session-info worker of <see cref="TelemetryLoop"/>, with a scripted source.</summary>
public class TelemetryLoopLiveTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    sealed class Tap : IFrameTap
    {
        public int Frames, Ended;
        public readonly List<(string yaml, int version)> Sessions = [];
        public void OnFrame(in TelemetryFrame f) => Frames++;
        public void OnSessionInfo(string yaml, int version, SessionInfo info) { lock (Sessions) Sessions.Add((yaml, version)); }
        public void OnSourceEnded() => Ended++;
    }

    static TelemetryLoop Make(ScriptedTelemetrySource src, Tap tap, DiagLog log, bool inline = true) =>
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
        var src = new ScriptedTelemetrySource();
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
        var src = new ScriptedTelemetrySource();
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
        var src = new ScriptedTelemetrySource();
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
        var src = new ScriptedTelemetrySource();
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
        var src = new ScriptedTelemetrySource();
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
        var src = new ScriptedTelemetrySource();
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

    [Fact]
    public void Profile_read_error_is_retried_on_the_next_session_info_and_never_drops_a_baseline()   // I-2b
    {
        var root = Path.Combine(Path.GetTempPath(), "fuelpace-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DiagLog(DiagLevel.Info, 1 << 14);
            var store = new ProfileStore(root, log);
            var info = SessionInfoParser.Parse(SampleYaml.Zolder);
            store.Save(SyntheticRace.Profile(info));
            var path = store.PathFor(info.CarPath, info.TrackName);
            var src = new ScriptedTelemetrySource { WhenEmpty = ReadResult.Frame, Frame = new LapSim(lapTimeS: 20).TimeRace(3600).Run(0, endPct: 0.5).Last() };
            var tap = new Tap();
            var pipeline = new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(T0), store, null, log);
            using var loop = new TelemetryLoop(src, pipeline, store, inlineSessionInfo: false, log, tap);

            void BumpAndWait(int version)
            {
                src.SessionInfoVersion = version;
                loop.Step();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (true) { lock (tap.Sessions) if (tap.Sessions.Count >= version) break; Assert.True(DateTime.UtcNow < deadline); Thread.Sleep(5); }
                for (int i = 0; i < TelemetryPipeline.StrategyEvery; i++) loop.Step();
            }

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                BumpAndWait(1);                              // every attempt fails: session installed without a profile
            Assert.Equal(SignalState.NoBaseline, pipeline.Overlay.Read().State);
            int errors = 0;
            while (log.TryDequeueText(out var t)) if (t.Category == "storage" && t.Level == DiagLevel.Error) errors++;
            Assert.Equal(TelemetryLoop.ProfileLoadAttempts, errors);

            BumpAndWait(2);                                  // same YAML: the failed read is retried
            Assert.NotEqual(SignalState.NoBaseline, pipeline.Overlay.Read().State);

            src.Yaml = SampleYaml.Zolder.Replace("DriverCarEstLapTime: 78.9567", "DriverCarEstLapTime: 77.0000");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                BumpAndWait(3);                              // same key: no read at all, the baseline stays
            Assert.NotEqual(SignalState.NoBaseline, pipeline.Overlay.Read().State);
        }
        finally { try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { } }
    }
}
