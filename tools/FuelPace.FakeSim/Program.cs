using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using FuelPace.App.Simulation;
using FuelPace.Core.Telemetry;

namespace FuelPace.FakeSim;

/// <summary>
/// Simulates iRacing's shared memory so the Agent can be tried on Windows without the sim:
/// <c>FuelPace.FakeSim &lt;file.ibt | --synthetic-race &lt;laps&gt;&gt; [--speed 1|10|max] [--loop] [--cars 20]
/// [--disconnect-after &lt;s&gt;] [--map &lt;name&gt;] [--event &lt;name&gt;]</c>.
/// <para>
/// Publishes one frame per tick at the pace of its <c>SessionTime</c> (60 Hz at <c>--speed 1</c>, 600 Hz at 10, as fast as
/// possible at max), every frame of the source in order. The synthetic race is <see cref="SyntheticRace"/> with
/// <c>--cars</c> rivals whose lap times arrive <see cref="CarLapTimeDelayS"/> after their lap count, as the sim publishes
/// them, and a new session info (a new <c>sessionInfoUpdate</c>) at every lap the player completes. At the end of the
/// source (unless <c>--loop</c> starts it again, like a replay rewound), after <c>--disconnect-after</c> seconds or on
/// Ctrl+C / Ctrl+Break / closing the console, the status bit is cleared and the handles are closed: the Agent sees the sim
/// leave. It never opens a mapping or an event that already exists (iRacing open, see <see cref="SharedMemorySim"/>).
/// Exit codes: 0 done, 1 failure, 2 bad command line or the names already in use (nothing was written).
/// </para>
/// </summary>
internal static partial class Program
{
    /// <summary>How long after a rival's lap count its lap time shows (the player's official time comes 1.5 s late too).</summary>
    public const double CarLapTimeDelayS = 1.5;
    private const double MaxSleepMs = 250, MaxLagS = 1;

    public static int Main(string[] args)
    {
        if (!FakeSimArgs.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(FakeSimArgs.Usage);
            return 2;
        }
        using var stop = new CancellationTokenSource();
        using var done = new ManualResetEventSlim(false);
        void OnSignal(PosixSignalContext ctx)
        {
            stop.Cancel();
            if (ctx.Signal is PosixSignal.SIGINT or PosixSignal.SIGQUIT) ctx.Cancel = true; // Main ends normally
            else done.Wait(TimeSpan.FromSeconds(3)); // closing or shutdown: the process ends when this returns
        }
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigQuit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);
        using var sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        try { return Run(options, stop.Token); }
        catch (SharedMemorySim.NamesInUseException e)
        {
            Console.Error.WriteLine("FakeSim refused: " + e.Message);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException
                                      or InvalidDataException or WaitHandleCannotBeOpenedException)
        {
            Console.Error.WriteLine($"FakeSim failed: {e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally { done.Set(); }
    }

    private static int Run(FakeSimArgs o, CancellationToken stop)
    {
        var feed = o.IbtPath is { } path ? Feed.FromIbt(path) : Feed.Synthetic(o.SyntheticLaps!.Value, o.Cars);
        bool fineTimer = TimeBeginPeriod(1) == 0; // this process only (Windows 10 2004+): 1 ms sleeps for the 600 Hz pace
        try
        {
            using var sim = SharedMemorySim.Create(o.MapName, o.EventName, feed.Vars, feed.Yaml);
            Say($"map {o.MapName}, event {o.EventName}: {feed.Vars.Count} variables, {sim.Size} bytes; {feed.Description}, " +
                $"speed {o.Speed}{(o.Loop ? ", loop" : "")}{(o.DisconnectAfter is { } d ? $", disconnect after {d.TotalSeconds:F1} s" : "")}");
            string reason = Publish(o, feed, sim, stop);
            Say($"{reason}: leaving the session");
        } // clears the status bit and closes the handles
        finally
        {
            if (fineTimer) TimeEndPeriod(1);
        }
        return 0;
    }

    /// <summary>Publishes until the source ends (without <c>--loop</c>), the time is up or a stop was requested.</summary>
    private static string Publish(FakeSimArgs o, Feed feed, SharedMemorySim sim, CancellationToken stop)
    {
        double speed = o.Speed switch { ReplaySpeed.X1 => 1, ReplaySpeed.X10 => 10, _ => 0 };
        var clock = Stopwatch.StartNew();
        long frames = 0;
        for (int pass = 1; ; pass++)
        {
            var pacer = new Pacer(speed);
            int lap = -1;
            if (pass > 1) sim.UpdateSessionInfo(feed.Yaml);
            foreach (var f in feed.Frames())
            {
                if (stop.IsCancellationRequested) return "stopped";
                if (o.DisconnectAfter is { } limit && clock.Elapsed >= limit) return "--disconnect-after";
                pacer.WaitFor(f.SessionTime, stop);
                if (stop.IsCancellationRequested) return "stopped";
                sim.Publish(in f);
                frames++;
                if (f.LapCompleted == lap) continue;
                lap = f.LapCompleted;
                bool newInfo = feed.YamlAfterLap is { } next && lap > 0 && sim.UpdateSessionInfo(next(lap));
                Say(string.Create(CultureInfo.InvariantCulture,
                    $"pass {pass} lap {f.Lap} (completed {lap}): SessionTime {f.SessionTime:F1} s, fuel {f.FuelLevel:F1} L, " +
                    $"{frames} frames{(newInfo ? ", session info updated" : "")}"));
            }
            if (!o.Loop) return "end of the source";
        }
    }

    private static void Say(string line) =>
        Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture) + line);

    /// <summary>
    /// Holds each frame until <c>(SessionTime − first SessionTime) / speed</c> of wall clock passed (speed 0: no wait). Sleeps
    /// at most <see cref="MaxSleepMs"/> at a time and starts over from the current frame after one (a gap in a file, a
    /// session restarting), or when it fell more than <see cref="MaxLagS"/> behind (a stalled machine), instead of bursting.
    /// </summary>
    private sealed class Pacer(double speed)
    {
        private long _wall0;
        private double _sim0;
        private bool _started;

        public void WaitFor(double sessionTime, CancellationToken stop)
        {
            if (speed <= 0) return;
            if (!_started)
            {
                Restart(sessionTime);
                return;
            }
            double remainingMs = ((sessionTime - _sim0) / speed - Stopwatch.GetElapsedTime(_wall0).TotalSeconds) * 1000;
            if (remainingMs > MaxSleepMs)
            {
                stop.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(MaxSleepMs));
                Restart(sessionTime);
            }
            else if (remainingMs >= 1) stop.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remainingMs));
            else if (remainingMs < -MaxLagS * 1000) Restart(sessionTime);
        }

        private void Restart(double sessionTime)
        {
            _started = true;
            _wall0 = Stopwatch.GetTimestamp();
            _sim0 = sessionTime;
        }
    }

    /// <summary>What is published: the variables of the image, the session info and the frames (enumerated once per pass).</summary>
    private sealed record Feed(IReadOnlyList<VarSpec> Vars, string Yaml, Func<IEnumerable<TelemetryFrame>> Frames,
        Func<int, string>? YamlAfterLap, string Description)
    {
        public static Feed Synthetic(int laps, int cars)
        {
            var vars = new List<VarSpec>();
            for (int i = 0; i < TelemetryVars.All.Count; i++)
                if (cars > 0 || !TelemetryVars.IsCarIdx((TelemetryVarId)i)) vars.Add(TelemetryVars.All[i]);
            return new Feed(vars, SyntheticRace.Yaml, () => SyntheticRace.Frames(laps, cars, CarLapTimeDelayS),
                SyntheticRace.YamlWithResults, $"synthetic race of {laps} laps with {cars} cars");
        }

        /// <summary>The file's own variables (types, counts, units) among the ones the Agent reads, and its session info.</summary>
        public static Feed FromIbt(string path)
        {
            string yaml;
            var vars = new List<VarSpec>();
            int records;
            using (var reader = IbtReader.Open(path))
            {
                yaml = reader.SessionInfoYaml;
                records = reader.RecordCount;
                foreach (var spec in TelemetryVars.All)
                    foreach (var h in reader.Variables)
                        if (string.Equals(h.Name, spec.Name, StringComparison.Ordinal))
                        {
                            vars.Add(new VarSpec(h.Name, h.Type, h.Count, h.Unit));
                            break;
                        }
            }
            return new Feed(vars, yaml, () => Read(path), null, $"{Path.GetFileName(path)} ({records} frames)");
        }

        private static IEnumerable<TelemetryFrame> Read(string path)
        {
            using var src = new IbtTelemetrySource(path); // as fast as asked: the publishing loop paces
            var f = new TelemetryFrame();
            while (src.TryRead(ref f) == ReadResult.Frame) yield return f;
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint periodMs);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint periodMs);
}
