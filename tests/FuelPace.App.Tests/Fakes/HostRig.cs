using System.Collections.Concurrent;
using System.Text.Json;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;
using FuelPace.App.Settings;
using FuelPace.App.Simulation;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Tests.Fakes;

/// <summary>An <see cref="AgentHost"/> over fakes of every platform interface, in a temporary app-data folder.</summary>
public sealed class HostRig : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public readonly string Root = Path.Combine(Path.GetTempPath(), "fuelpace-host-tests", Guid.NewGuid().ToString("N"));
    public readonly ManualClock Clock = new(SyntheticRace.T0);
    public readonly FakeUi Ui = new();
    public readonly FakeSurface Surface = new();
    public readonly FakeHotkeys Hotkeys = new();
    public readonly FakeTray Tray = new();
    public readonly FakeMonitors Monitors = new();
    public readonly FakeSystem Sys;
    public readonly FakeSourceFactory Sources = new();
    public AgentOptions Options;
    public AgentHost Host = null!;
    private readonly List<IpcProbe> _probes = [];
    private int _files;

    public HostRig(bool replay = true, bool debug = false, TimeSpan? exitAfter = null)
    {
        Directory.CreateDirectory(Root);
        Sys = new FakeSystem(Path.Combine(Root, "Desktop"));
        Options = new AgentOptions(Path.Combine(Root, "appdata"), debug, false, replay ? "synthetic.ibt" : null,
            ReplaySpeed.Max, exitAfter, "fpt" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Options.AppDataRoot);
    }

    public string AppData => Options.AppDataRoot;
    public string SettingsPath => Path.Combine(AppData, "settings.json");

    public void WriteSettings(AppSettings s) => new SettingsStore(AppData).Save(s);

    public string TempFile(string ext) => Path.Combine(Root, "f" + Interlocked.Increment(ref _files) + ext);

    /// <summary>A synthetic race of <paramref name="laps"/> laps as a source (20 s laps, 1200 frames each).</summary>
    public FakeSource Race(int laps, bool gated = false, bool endsDisconnected = false) =>
        new(SyntheticRace.WriteIbt(TempFile(".ibt"), laps, withCarIdx: false), Clock, gated, endsDisconnected);

    public void Start()
    {
        Host = new AgentHost(Options, Sources, Surface, Ui, Hotkeys, Tray, Monitors, Sys, new FixedMetricsMeasurer(), Clock);
        Host.Start();
        Ui.Drain();
    }

    /// <summary>Polls <paramref name="cond"/>, advancing the clock by <paramref name="pump"/> between polls when given.</summary>
    public void WaitUntil(Func<bool> cond, string what, TimeSpan? pump = null)
    {
        var end = DateTime.UtcNow + Timeout;
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < end, "timed out waiting for: " + what);
            if (pump is { } p) Clock.Advance(p);
            Thread.Sleep(2);
        }
    }

    /// <summary>Waits until the telemetry thread has handled <paramref name="n"/> frames of <paramref name="src"/> and
    /// the UI thread is idle.</summary>
    public void Settle(FakeSource src, int n)
    {
        src.WaitConsumed(n);
        WaitUntil(() => src.IdleAt == n, $"telemetry idle after {n} frames");
        Ui.Drain();
    }

    public async Task<IpcProbe> ConnectAsync()
    {
        var p = new IpcProbe(Options.PipeName);
        Assert.True(await p.Client.ConnectAsync(Timeout), "IPC connect");
        await p.Client.SendAsync(new Hello("test"));
        await p.WaitFor<Snapshot>();
        _probes.Add(p);
        return p;
    }

    /// <summary>Every JSON line the Agent logged so far.</summary>
    public List<JsonElement> LogLines()
    {
        Host.FlushLogs();
        var lines = new List<JsonElement>();
        var dir = Path.Combine(AppData, "logs");
        if (!Directory.Exists(dir)) return lines;
        foreach (var f in Directory.GetFiles(dir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            while (r.ReadLine() is { } line)
                if (line.Length > 0) lines.Add(JsonDocument.Parse(line).RootElement.Clone());
        }
        return lines;
    }

    public List<JsonElement> Events(string name) =>
        LogLines().Where(l => l.TryGetProperty("ev", out var e) && e.GetString() == name).ToList();

    /// <summary>Error lines in the log fail the test unless it expects them.</summary>
    public bool ErrorsExpected;

    public void Dispose()
    {
        foreach (var p in _probes) p.DisposeAsync().AsTask().Wait(Timeout);
        Host?.Dispose();
        var errors = Host is null ? [] : LogLines().Where(l => l.GetProperty("lvl").GetString() == "error").ToList();
        Ui.Dispose();
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Assert.True(Ui.Errors.IsEmpty, "UI thread errors: " + string.Join("\n", Ui.Errors));
        Assert.True(ErrorsExpected || errors.Count == 0, "errors logged: " + string.Join("\n", errors));
    }
}

/// <summary>A real <see cref="IpcClient"/> that keeps every message it receives.</summary>
public sealed class IpcProbe : IAsyncDisposable
{
    public readonly IpcClient Client;
    public readonly ConcurrentQueue<IpcMessage> Received = new();

    public IpcProbe(string pipe)
    {
        Client = new IpcClient(pipe);
        Client.Received += Received.Enqueue;
    }

    public List<T> All<T>() where T : IpcMessage => Received.OfType<T>().ToList();

    public async Task<T> WaitFor<T>(Func<T, bool>? match = null, int skip = 0) where T : IpcMessage
    {
        var end = DateTime.UtcNow + HostRig.Timeout;
        while (DateTime.UtcNow < end)
        {
            var hit = Received.OfType<T>().Where(m => match?.Invoke(m) ?? true).Skip(skip).FirstOrDefault();
            if (hit is not null) return hit;
            await Task.Delay(5);
        }
        throw new TimeoutException("no " + typeof(T).Name);
    }

    /// <summary>Sends <paramref name="m"/> and waits until the Agent handled it (its handler runs messages in order, so the
    /// reply to a following GetSnapshot proves it).</summary>
    public async Task<AgentSnapshot> SendAndSync(IpcMessage m)
    {
        int before = Received.OfType<Snapshot>().Count();
        await Client.SendAsync(m);
        await Client.SendAsync(new GetSnapshot());
        return (await WaitFor<Snapshot>(skip: before)).S;
    }

    private int _disposed;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? Client.DisposeAsync() : ValueTask.CompletedTask;
}
