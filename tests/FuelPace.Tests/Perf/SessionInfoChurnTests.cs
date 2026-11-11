using System.Globalization;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Perf;

/// <summary>
/// I-4: in a live race <c>SessionInfoUpdate</c> bumps often (drivers join, results change). With the session info on the
/// background worker, a bump must not reload the profile for an unchanged key, must not re-parse identical YAML, and must
/// not allocate on the telemetry thread, even when the session context is rebuilt.
/// </summary>
[Collection(PerfCollection.Name)]
public class SessionInfoChurnTests : IDisposable
{
    const int Bumps = 100;
    readonly string _root = Path.Combine(Path.GetTempPath(), "fuelpace-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { } }

    sealed class Tap : IFrameTap
    {
        public int Count;
        public readonly List<SessionInfo> Infos = [];
        public void OnFrame(in TelemetryFrame f) { }
        public void OnSessionInfo(string yaml, int version, SessionInfo info)
        {
            lock (Infos) Infos.Add(info);
            Interlocked.Increment(ref Count);
        }
        public void OnSourceEnded() { }
    }

    sealed class Rig : IDisposable
    {
        public readonly ScriptedTelemetrySource Source = new() { WhenEmpty = ReadResult.Frame };
        public readonly DiagLog Log = new(DiagLevel.Debug, 1 << 14);
        public readonly Tap Tap = new();
        public readonly TelemetryPipeline Pipeline;
        public readonly TelemetryLoop Loop;

        public Rig(string root)
        {
            // An on-track frame of a time race, so the strategy (and the BoP check against the baseline) really runs.
            Source.Frame = new LapSim(lapTimeS: 20).TimeRace(3600).Run(0, endPct: 0.5).Last();
            var store = new ProfileStore(root, Log);
            var info = SessionInfoParser.Parse(Source.Yaml);
            store.Save(SyntheticRace.Profile(info)); // baseline with one BoP condition
            Pipeline = new TelemetryPipeline(new StrategyConfig(), new FixedTimeProvider(SyntheticRace.T0), store, null, Log);
            Loop = new TelemetryLoop(Source, Pipeline, store, inlineSessionInfo: false, Log, Tap);
        }

        /// <summary>One frame, then waits (without allocating) until the worker has applied <paramref name="sessions"/> session infos.</summary>
        public void StepAndWait(int sessions)
        {
            Assert.True(Loop.Step());
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref Tap.Count) < sessions)
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("session info never applied");
                Thread.Sleep(1);
            }
        }

        public int ProfileLoads()
        {
            int n = 0;
            while (Log.TryDequeueText(out var t)) if (t.Category == "storage" && t.Message.StartsWith("loaded ", StringComparison.Ordinal)) n++;
            return n;
        }

        public void Dispose() => Loop.Dispose();
    }

    [Fact]
    public void Identical_yaml_bumps_load_the_profile_once_parse_once_and_allocate_nothing_on_the_tick()
    {
        using var rig = new Rig(_root);
        rig.StepAndWait(1);
        for (int i = 0; i < 60; i++) rig.StepAndWait(1); // warm-up
        Assert.NotEqual(SignalState.NoBaseline, rig.Pipeline.Overlay.Read().State);

        var window = AllocationMeasure.Start();
        for (int v = 2; v <= 1 + Bumps; v++)
        {
            rig.Source.SessionInfoVersion = v;
            rig.StepAndWait(v);
            for (int k = 0; k < TelemetryPipeline.StrategyEvery; k++) rig.StepAndWait(v);
        }
        window.AssertNone();

        Assert.True(rig.ProfileLoads() <= 1, "the profile was reloaded for an unchanged key");
        lock (rig.Tap.Infos)
        {
            Assert.Equal(1 + Bumps, rig.Tap.Infos.Count);
            Assert.All(rig.Tap.Infos, i => Assert.Same(rig.Tap.Infos[0], i)); // the YAML was parsed once
        }
        Assert.NotEqual(SignalState.NoBaseline, rig.Pipeline.Overlay.Read().State);
    }

    [Fact]
    public void Changed_yaml_with_the_same_profile_key_keeps_the_profile_and_the_context_swap_allocates_nothing_on_the_tick()
    {
        using var rig = new Rig(_root);
        // Pre-built before the measured window: each version changes the estimated lap time, so the context is rebuilt.
        var yamls = new string[Bumps + 2];
        for (int v = 1; v < yamls.Length; v++)
            yamls[v] = SampleYaml.Zolder.Replace("DriverCarEstLapTime: 78.9567",
                "DriverCarEstLapTime: " + (70 + v * 0.01).ToString("F4", CultureInfo.InvariantCulture));
        rig.Source.YamlHook = () => yamls[rig.Source.SessionInfoVersion];
        rig.StepAndWait(1);
        for (int i = 0; i < 60; i++) rig.StepAndWait(1);

        var window = AllocationMeasure.Start();
        for (int v = 2; v <= 1 + Bumps; v++)
        {
            rig.Source.SessionInfoVersion = v;
            rig.StepAndWait(v);
            for (int k = 0; k < TelemetryPipeline.StrategyEvery; k++) rig.StepAndWait(v);
        }
        window.AssertNone();

        Assert.True(rig.ProfileLoads() <= 1, "the profile was reloaded for an unchanged key");
        lock (rig.Tap.Infos) Assert.Equal(70 + (1 + Bumps) * 0.01, rig.Tap.Infos[^1].EstLapTimeS, 6);
        var s = rig.Pipeline.Overlay.Read();
        Assert.NotEqual(SignalState.NoBaseline, s.State);
        Assert.False(s.BopMismatch);
    }
}
