using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FuelPace.Agent.Live;
using FuelPace.App.Ipc;
using FuelPace.Core.Storage;
using FuelPace.Core.Telemetry;

namespace FuelPace.Windows.Tests;

/// <summary>
/// Real processes: tests that start <c>FuelPace.Agent.exe</c> share the Agent's single-instance mutex and its pipe name, so
/// they run alone (never next to another collection of this assembly).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentProcessCollection
{
    public const string Name = "agent processes";
}

/// <summary>
/// The Agent against the FakeSim over real shared memory, a real data-valid event, the real instance mutex and the real pipe
/// (criteria 7 and 12, Review Focus 3 and 4). Nothing here needs the overlay to be visible or a notification area: the
/// assertions read the Agent's JSONL log, its recordings and its profiles. Every test copies what it produced to
/// <c>artifacts/e2e/&lt;test&gt;/</c> (an <c>%APPDATA%\FuelPace</c>-like folder, plus the processes' output), which CI uploads.
/// </summary>
[Collection(AgentProcessCollection.Name)]
public sealed class EndToEndTests
{
    private const string CarPath = "ligierjsp320", TrackName = "zolder alt";

    // A hung test fails instead of holding the CI job (every wait inside is bounded too).
    private const int TestTimeoutMs = 6 * 60_000;

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Agent_follows_fake_sim_and_reconnects() // criterion 7
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var run = new E2ERun(nameof(Agent_follows_fake_sim_and_reconnects), TestContext.Current.CancellationToken);
        // PerfMinute comes once a connection lasts 60 s of wall clock (TelemetryLoop, one per connection), so the second
        // connection must: the Agent runs 90 s (not 40) and the first sim publishes for 8 s.
        const int exitAfterS = 90;
        var agent = run.StartAgent("--debug", "--exit-after", exitAfterS.ToString(CultureInfo.InvariantCulture));
        await run.WaitForLogAsync(l => l.Count("Startup") == 1, "Agent startup");

        var sim1 = run.StartFakeSim("sim1", "--synthetic-race", "30", "--speed", "10", "--cars", "20", "--disconnect-after", "8");
        await run.WaitForExitAsync(sim1, TimeSpan.FromSeconds(60));
        Assert.Equal(0, sim1.ExitCode);
        await run.WaitForLogAsync(l => l.Count("SourceEnded") == 1, "the Agent noticing that the sim left");
        var first = run.Log();
        Assert.Equal(1, first.Count("SourceOpened"));
        Assert.Equal(1, first.Events("SourceEnded").Single().GetProperty("reason").GetInt32()); // disconnected, not EOF

        // Started only now: a sim back within 2 s would be the same connection (the sim reloading), by design. And not
        // right after SourceEnded is logged: the Agent closes its view of the old mapping just after, and FakeSim refuses
        // to start while a mapping of that name still exists.
        await Task.Delay(TimeSpan.FromSeconds(1.5), run.Token);
        var sim2 = run.StartFakeSim("sim2", "--synthetic-race", "80", "--speed", "10", "--cars", "20", "--disconnect-after", "300");
        await run.WaitForLogAsync(l => l.Count("SourceOpened") == 2, "the Agent reconnecting");
        double reconnectedAtS = run.AgentElapsed.TotalSeconds;

        await run.WaitForExitAsync(agent, TimeSpan.FromSeconds(exitAfterS + 120));
        Assert.Equal(0, agent.ExitCode);
        Assert.False(sim2.HasExited, "the second sim ended before the Agent (a second SourceEnded)");

        var log = run.Log();
        Assert.Equal(2, log.Count("SourceOpened"));
        Assert.Equal(1, log.Count("SourceEnded"));
        Assert.True(log.Count("LapCompleted") >= 3, $"{log.Count("LapCompleted")} LapCompleted");
        Assert.True(log.Count("PerfMinute") >= 1,
            $"no PerfMinute: the second connection opened {reconnectedAtS:F1} s after the Agent started (exit at {exitAfterS} s)");
        Assert.Empty(log.Errors());
        Assert.True(log.Count("LeaderChanged") >= 1, "the leader logic never saw a leader"); // CarIdx data arrived

        var recordings = Directory.GetFiles(Path.Combine(run.AppData, "recordings"), "*.ibt");
        Assert.NotEmpty(recordings);
        int frames = 0;
        foreach (var path in recordings)
        {
            using var r = IbtReader.Open(path);
            Assert.Contains("CarPath: " + CarPath, r.SessionInfoYaml);
            Assert.True(VarLayout.Resolve(r.Variables).HasCarIdx, Path.GetFileName(path) + " has no CarIdx variables");
            frames += r.RecordCount;
        }
        Assert.True(frames > 600, $"only {frames} recorded frames");
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Second_agent_instance_exits_and_requests_settings() // Review Focus 4
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var run = new E2ERun(nameof(Second_agent_instance_exits_and_requests_settings), TestContext.Current.CancellationToken);
        var first = run.StartAgent("--debug", "--exit-after", "25");
        await run.WaitForLogAsync(l => l.Count("Startup") == 1, "Agent startup");
        await using (var probe = new IpcClient(E2ERun.PipeName))
            Assert.True(await probe.ConnectAsync(TimeSpan.FromSeconds(30)), "the first Agent's pipe never accepted");

        string secondAppData = Path.Combine(run.Root, "second");
        var second = run.StartAgentIn(secondAppData, "agent2", "--debug", "--exit-after", "25");
        await run.WaitForExitAsync(second, TimeSpan.FromSeconds(20)); // long before its --exit-after: it never ran an agent
        Assert.Equal(0, second.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(secondAppData, "logs")), "the second instance started a host");
        Assert.False(first.HasExited);
        await run.WaitForLogAsync(l => l.HasText("another Agent instance asked for the Settings window"), "the Settings request");

        await run.WaitForExitAsync(first, TimeSpan.FromSeconds(120));
        Assert.Equal(0, first.ExitCode);
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Capture_end_to_end_with_ipc() // criterion 12, without the Settings window
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var run = new E2ERun(nameof(Capture_end_to_end_with_ipc), TestContext.Current.CancellationToken);
        var agent = run.StartAgent("--debug", "--exit-after", "60");
        run.StartFakeSim("sim", "--synthetic-race", "80", "--speed", "10", "--cars", "20", "--disconnect-after", "300");
        await run.WaitForLogAsync(l => l.Count("SessionInfoChanged") >= 1, "the session info");

        var timeout = TimeSpan.FromSeconds(60);
        var laps = new ConcurrentQueue<CaptureLapAdded>();
        await using (var settings = new IpcProbe(run))
        {
            settings.Client.Received += m =>
            {
                if (m is CaptureLapAdded a) laps.Enqueue(a);
            };
            await settings.ConnectAsync(timeout);
            await settings.Client.SendAsync(new SetCapture(true));
            await run.WaitAsync(() => laps.Count >= 4, timeout, "4 captured laps");
        } // the Settings window closes during the capture...

        int seen = laps.Count;
        await run.WaitForLogAsync(l => l.Count("CaptureLap") > seen, "a lap captured without a client");
        await using (var settings = new IpcProbe(run))
        {
            var snapshot = await settings.ConnectAsync(timeout); // ...and opens again: the capture is still running
            Assert.True(snapshot.S.CaptureActive);
            Assert.True(snapshot.S.CaptureLaps.Count > seen, $"{snapshot.S.CaptureLaps.Count} laps in the snapshot");
            await settings.Client.SendAsync(new SetCapture(false));
            var finished = await settings.WaitForAsync<CaptureFinished>(timeout);
            Assert.Equal(CarPath + "__" + TrackName, finished.ProfileKey);

            ProfileDocument? doc = null;
            await run.WaitAsync(() => (doc = TryLoadProfile(run.AppData)) is { Captures.Count: > 0 }, timeout, "the saved profile");
            var capture = Assert.Single(doc!.Captures);
            Assert.Equal(finished.CaptureId, capture.Id);
            Assert.True(capture.Laps.Count >= snapshot.S.CaptureLaps.Count, $"{capture.Laps.Count} laps saved");
        }

        await run.WaitForExitAsync(agent, TimeSpan.FromSeconds(180));
        Assert.Equal(0, agent.ExitCode);
        var log = run.Log();
        Assert.Equal(1, log.Count("CaptureStarted"));
        Assert.Equal(1, log.Count("CaptureStopped"));
        Assert.True(log.Count("ProfileSaved") >= 1, "no ProfileSaved");
        Assert.Empty(log.Errors());
    }

    private static ProfileDocument? TryLoadProfile(string appData)
    {
        try { return new ProfileStore(appData).Load(CarPath, TrackName); }
        catch (IOException) { return null; } // being replaced right now
    }

    /// <summary>A stand-in for the Settings window: the client plus every message it received.</summary>
    private sealed class IpcProbe(E2ERun run) : IAsyncDisposable
    {
        private readonly ConcurrentQueue<IpcMessage> _got = new();

        public IpcClient Client { get; } = new(E2ERun.PipeName);

        /// <summary>Connects and says hello, as the Settings window does; returns the snapshot it answers with.</summary>
        public async Task<Snapshot> ConnectAsync(TimeSpan timeout)
        {
            Client.Received += _got.Enqueue;
            Assert.True(await Client.ConnectAsync(timeout), "IPC connect");
            await Client.SendAsync(new Hello("e2e"));
            return await WaitForAsync<Snapshot>(timeout);
        }

        public async Task<T> WaitForAsync<T>(TimeSpan timeout) where T : IpcMessage
        {
            T? found = null;
            await run.WaitAsync(() => (found = _got.OfType<T>().FirstOrDefault()) is not null, timeout, typeof(T).Name);
            return found!;
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}

/// <summary>
/// One end-to-end test: a temporary <c>--appdata</c>, unique shared-memory and event names (the Agent gets them through
/// <see cref="LiveSourceFactory.MapEnv"/> and <see cref="LiveSourceFactory.EventEnv"/>), the child processes (always killed
/// on dispose) and the copy of everything to <c>artifacts/e2e/&lt;test&gt;/</c>.
/// </summary>
internal sealed class E2ERun : IDisposable
{
    /// <summary>The Agent's default pipe (<c>AgentOptions.PipeName</c>), the one a second instance asks.</summary>
    public const string PipeName = "FuelPace";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    private readonly string _test;
    private readonly string _map = @"Local\FuelPace.E2E.Map." + Guid.NewGuid().ToString("N");
    private readonly string _event = @"Local\FuelPace.E2E.Event." + Guid.NewGuid().ToString("N");
    private readonly List<Child> _children = [];
    private readonly DateTime _started = DateTime.Now;
    private Stopwatch? _agentClock;

    /// <param name="token">The test's: its timeout ends every wait with an exception, so Dispose still kills and copies.</param>
    public E2ERun(string test, CancellationToken token)
    {
        _test = test;
        Token = token;
        Root = Path.Combine(Path.GetTempPath(), "fuelpace-e2e-" + Guid.NewGuid().ToString("N")[..12]);
        AppData = Path.Combine(Root, "appdata");
        Directory.CreateDirectory(AppData);
    }

    public CancellationToken Token { get; }
    public string Root { get; }
    public string AppData { get; }

    /// <summary>Since the first Agent started.</summary>
    public TimeSpan AgentElapsed => _agentClock?.Elapsed ?? TimeSpan.Zero;

    public Process StartAgent(params string[] args) => StartAgentIn(AppData, "agent", args);

    public Process StartAgentIn(string appData, string name, params string[] args)
    {
        _agentClock ??= Stopwatch.StartNew();
        return Start(name, "FuelPace.Agent.exe", [.. args, "--appdata", appData]);
    }

    public Process StartFakeSim(string name, params string[] args) =>
        Start(name, "FuelPace.FakeSim.exe", [.. args, "--map", _map, "--event", _event]);

    private Process Start(string name, string exe, string[] args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, exe);
        Assert.True(File.Exists(path), "missing " + path);
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[LiveSourceFactory.MapEnv] = _map;
        psi.Environment[LiveSourceFactory.EventEnv] = _event;
        var child = new Child(name, Process.Start(psi)!, string.Join(' ', args));
        _children.Add(child);
        return child.Process;
    }

    /// <summary>Waits for the process itself, not for the end of its output: a process the Agent starts (the Settings window)
    /// inherits the Agent's redirected stdout and keeps the pipe open after the Agent exited.</summary>
    public async Task WaitForExitAsync(Process p, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!p.HasExited)
        {
            if (sw.Elapsed > timeout)
                Assert.Fail($"{_children.Find(c => c.Process == p)?.Name ?? "a process"} did not exit within {timeout.TotalSeconds} s");
            await Task.Delay(Poll, Token);
        }
    }

    /// <summary>Polls the Agent's log until <paramref name="cond"/> holds; fails after <paramref name="timeoutS"/> seconds.</summary>
    public async Task WaitForLogAsync(Func<AgentLog, bool> cond, string what, double timeoutS = 60)
    {
        var sw = Stopwatch.StartNew();
        while (!cond(Log()))
        {
            foreach (var c in _children)
                if (c.Name == "agent" && c.Process.HasExited) Assert.Fail($"the Agent exited ({c.Process.ExitCode}) while waiting for {what}");
            if (sw.Elapsed.TotalSeconds > timeoutS) Assert.Fail($"timed out after {timeoutS} s waiting for {what}");
            await Task.Delay(Poll, Token);
        }
    }

    public async Task WaitAsync(Func<bool> cond, TimeSpan timeout, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > timeout) Assert.Fail($"timed out after {timeout.TotalSeconds} s waiting for {what}");
            await Task.Delay(Poll, Token);
        }
    }

    /// <summary>Every complete JSON line in the Agent's logs so far (the file is still being written).</summary>
    public AgentLog Log()
    {
        var lines = new List<JsonElement>();
        var dir = Path.Combine(AppData, "logs");
        if (!Directory.Exists(dir)) return new AgentLog(lines);
        foreach (var f in Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            string text;
            try
            {
                using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(s, Encoding.UTF8);
                text = r.ReadToEnd();
            }
            catch (IOException) { continue; }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    lines.Add(doc.RootElement.Clone());
                }
                catch (JsonException) { } // the line being written
            }
        }
        return new AgentLog(lines);
    }

    public void Dispose()
    {
        foreach (var c in _children) c.Kill();
        KillSettingsWindows();
        CopyArtifacts();
        foreach (var c in _children) c.Process.Dispose();
        try { Directory.Delete(Root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The Agent starts <c>FuelPace.Settings.exe</c> from its folder when asked (a second instance does).</summary>
    private void KillSettingsWindows()
    {
        foreach (var p in Process.GetProcessesByName("FuelPace.Settings"))
        {
            using (p)
            {
                try
                {
                    var exe = p.MainModule?.FileName;
                    if (exe is not null && exe.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                        && p.StartTime >= _started)
                        p.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            }
        }
    }

    private void CopyArtifacts()
    {
        try
        {
            string? repo = null;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && repo is null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "FuelPace.sln"))) repo = d.FullName;
            if (repo is null) return;
            var dest = Path.Combine(repo, "artifacts", "e2e", _test);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            CopyTree(AppData, dest);
            foreach (var c in _children) File.WriteAllText(Path.Combine(dest, c.Name + ".out.txt"), c.Output());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Artifacts help a diagnosis; their absence must not fail or hide the test result.
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>A child process with its stdout and stderr collected.</summary>
    private sealed class Child
    {
        private readonly StringBuilder _out = new();
        private readonly Lock _gate = new();

        public Child(string name, Process p, string args)
        {
            Name = name;
            Process = p;
            Append($"> {Path.GetFileName(p.StartInfo.FileName)} {args}");
            p.OutputDataReceived += (_, e) => Append(e.Data);
            p.ErrorDataReceived += (_, e) => Append(e.Data is null ? null : "stderr: " + e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        public string Name { get; }
        public Process Process { get; }

        private void Append(string? line)
        {
            if (line is null) return;
            using (_gate.EnterScope()) _out.Append(DateTime.Now.ToString("HH:mm:ss.fff ")).AppendLine(line);
        }

        public string Output()
        {
            string exit;
            try { exit = Process.HasExited ? "exit code " + Process.ExitCode : "still running"; }
            catch (InvalidOperationException) { exit = "unknown"; }
            using (_gate.EnterScope()) return _out + exit + Environment.NewLine;
        }

        public void Kill()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Append("killed by the test");
                    Process.Kill(entireProcessTree: true);
                    for (int i = 0; i < 100 && !Process.HasExited; i++) Thread.Sleep(100); // not WaitForExit: see WaitForExitAsync
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}

/// <summary>The Agent's JSONL log lines.</summary>
internal sealed class AgentLog(List<JsonElement> lines)
{
    public IEnumerable<JsonElement> Events(string ev) =>
        lines.Where(l => l.TryGetProperty("ev", out var e) && e.GetString() == ev);

    public int Count(string ev) => Events(ev).Count();

    public bool HasText(string text) =>
        lines.Any(l => l.TryGetProperty("msg", out var m) && m.GetString()?.Contains(text, StringComparison.Ordinal) == true);

    /// <summary>Error lines, as text for the assertion message.</summary>
    public List<string> Errors() =>
        lines.Where(l => l.TryGetProperty("lvl", out var v) && v.GetString() == "error").Select(l => l.GetRawText()).ToList();
}
