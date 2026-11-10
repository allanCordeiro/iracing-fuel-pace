using FuelPace.App.Ipc;
using FuelPace.App.Settings;
using FuelPace.App.SettingsUi;
using FuelPace.App.SettingsUi.ViewModels;

namespace FuelPace.App.Tests.SettingsUi;

public sealed class FakeConnection : IAgentConnection
{
    public readonly List<IpcMessage> Sent = [];
    public bool Reachable = true;
    public int Launches, ConnectCalls;
    public bool IsConnected { get; private set; }
    public event Action<IpcMessage>? Received;
    public event Action? Disconnected;

    public Task<bool> ConnectAsync()
    {
        ConnectCalls++;
        IsConnected = Reachable;
        return Task.FromResult(Reachable);
    }

    public ValueTask SendAsync(IpcMessage m)
    {
        if (!IsConnected) throw new InvalidOperationException("not connected");
        Sent.Add(m);
        return ValueTask.CompletedTask;
    }

    public void LaunchAgent() => Launches++;
    public void Raise(IpcMessage m) => Received?.Invoke(m);
    public void Drop() { IsConnected = false; Disconnected?.Invoke(); }
    public List<T> Of<T>() where T : IpcMessage => Sent.OfType<T>().ToList();
}

/// <summary>Timers that fire when the test says so.</summary>
public sealed class ManualScheduler : IDelayScheduler
{
    sealed class Entry(TimeSpan delay, Action a) : IDisposable
    {
        public readonly TimeSpan Delay = delay;
        public readonly Action Run = a;
        public bool Live = true;
        public void Dispose() => Live = false;
    }

    readonly List<Entry> _entries = [];
    public IDisposable Schedule(TimeSpan delay, Action a) { var e = new Entry(delay, a); _entries.Add(e); return e; }
    public int Pending => _entries.Count(e => e.Live);
    public TimeSpan? LastDelay => _entries.LastOrDefault(e => e.Live)?.Delay;

    /// <summary>Fires every live timer once (they are consumed).</summary>
    public void FireAll()
    {
        foreach (var e in _entries.Where(e => e.Live).ToList()) { e.Live = false; e.Run(); }
    }
}

public sealed class FakeShell : IShellServices
{
    public string LogsFolder => "/logs";
    public readonly List<string> Opened = [];
    public void OpenFolder(string path) => Opened.Add(path);
}

public sealed class SettingsRig
{
    public readonly FakeConnection Conn = new();
    public readonly ManualScheduler Sched = new();
    public readonly FakeShell Shell = new();
    public readonly ShellViewModel Vm;

    public SettingsRig() => Vm = new ShellViewModel(Conn, new ImmediateSynchronizationContext(), Sched, Shell);

    public CaptureViewModel Capture => Vm.Capture;
    public OverlayViewModel Overlay => Vm.Overlay;
    public ProfilesViewModel Profiles => Vm.Profiles;
    public DiagnosticsViewModel Diagnostics => Vm.Diagnostics;

    public static CapturedLapDto Lap(int n, double? time, double fuel, double thr, params string[] reasons) =>
        new(n, time, fuel, thr, reasons, reasons.Length == 0);

    /// <summary>The nine laps of mock 7 (lap 24 is in progress and not part of the data).</summary>
    public static CapturedLapDto[] MockLaps() =>
    [
        Lap(15, null, 2.378, 51, "pit", "incident"),
        Lap(16, 80.897, 2.382, 64), Lap(17, 80.272, 2.404, 64), Lap(18, 79.964, 2.373, 64),
        Lap(19, 93.175, 2.388, 55, "incident", "offtrack"),
        Lap(20, 79.133, 2.386, 65, "incident"),
        Lap(21, 79.514, 2.403, 65), Lap(22, 79.134, 2.389, 67), Lap(23, 81.0615, 2.289, 58), // unrounded: the mean must read 1:20.140 as in the mock, not 1:20.141
    ];

    public static AgentSnapshot Snap(AppSettings? settings = null, bool capturing = true, IReadOnlyList<CapturedLapDto>? laps = null,
        string[]? failures = null, string? captureId = null, bool fullscreen = false, bool sim = true)
    {
        laps ??= MockLaps();
        return new AgentSnapshot("0.1.0+test", sim, "ligier__zolder", "Ligier JS P320", "Zolder Circuit (Alternate)", "Practice",
            capturing, laps, laps.Count(l => l.Valid), laps.Count, settings ?? new AppSettings(), false, "layered", fullscreen,
            failures ?? [], ["1 · 2560 × 1440", "2 · 1920 × 1080"], 100, captureId, [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);
    }

    /// <summary>Connects and answers the Hello with a snapshot.</summary>
    public async Task ConnectAsync(AgentSnapshot? snap = null)
    {
        await Vm.ConnectAsync();
        Conn.Raise(new Snapshot(snap ?? Snap()));
    }
}
