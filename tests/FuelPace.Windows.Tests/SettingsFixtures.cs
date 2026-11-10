using System.Runtime.ExceptionServices;
using System.Text.Json;
using FuelPace.App.Ipc;
using FuelPace.App.Settings;
using FuelPace.App.SettingsUi;
using FuelPace.Core.Capture;
using FuelPace.Core.Laps;
using FuelPace.Core.Storage;

namespace FuelPace.Windows.Tests;

/// <summary>An Agent link that is always up and answers nothing on its own: the test raises what the Agent would send.</summary>
internal sealed class FakeAgentConnection : IAgentConnection
{
    public readonly List<IpcMessage> Sent = [];
    public bool IsConnected { get; private set; }
    public event Action<IpcMessage>? Received;
    public event Action? Disconnected;

    public Task<bool> ConnectAsync() => Task.FromResult(IsConnected = true);

    public ValueTask SendAsync(IpcMessage m)
    {
        Sent.Add(m);
        return ValueTask.CompletedTask;
    }

    public void LaunchAgent() { }
    public void Raise(IpcMessage m) => Received?.Invoke(m);
    public void Drop() { IsConnected = false; Disconnected?.Invoke(); }
}

internal sealed class NeverScheduler : IDelayScheduler
{
    sealed class Nothing : IDisposable { public void Dispose() { } }
    public IDisposable Schedule(TimeSpan delay, Action action) => new Nothing();
}

internal sealed class NoShell : IShellServices
{
    public string LogsFolder => @"C:\logs";
    public void OpenFolder(string path) { }
}

/// <summary>The values of the approved mocks 7 and 8 (lap times and fuel from the Zolder fixture).</summary>
internal static class SettingsMockData
{
    static CapturedLapDto Lap(int n, double? time, double fuel, double thr, params string[] reasons) =>
        new(n, time, fuel, thr, reasons, reasons.Length == 0);

    /// <summary>Laps 15–23 of mock 7; lap 24 is the one in progress.</summary>
    public static CapturedLapDto[] Laps() =>
    [
        Lap(15, null, 2.378, 51, "pit", "incident"),
        Lap(16, 80.897, 2.382, 64), Lap(17, 80.272, 2.404, 64), Lap(18, 79.964, 2.373, 64),
        Lap(19, 93.175, 2.388, 55, "incident", "offtrack"),
        Lap(20, 79.133, 2.386, 65, "incident"),
        Lap(21, 79.514, 2.403, 65), Lap(22, 79.134, 2.389, 67), Lap(23, 81.062, 2.289, 58),
    ];

    /// <summary>Mock 8: monitor 1 at 1840 × 980, pace line off, insight line and auto-hide on, the default calculation.</summary>
    public static AppSettings Settings() => new()
    {
        Overlay = new OverlaySettings
        {
            MonitorDeviceName = @"\\.\DISPLAY1", OffsetX = 1840, OffsetY = 980, Scale = 1.0, BackgroundOpacity = 0.6,
            ContentOpacity = 1.0, ShowPaceLine = false, ShowInsightLine = true, AutoHide = true,
        },
    };

    public static AgentSnapshot Snapshot(bool debug = false)
    {
        var laps = Laps();
        return new AgentSnapshot("0.1.0+128.24accf3", true, "ligierjsp320__zolder_alt", "Ligier JS P320", "Zolder Circuit (Alternate)",
            "Practice", true, laps, laps.Count(l => l.Valid), laps.Length, Settings(), debug, "composition", false, [],
            ["1 · 2560 × 1440", "2 · 1920 × 1080"], 100, null, [@"\\.\DISPLAY1", @"\\.\DISPLAY2"]);
    }

    public static ProfileList Profiles() => new(
    [
        new ProfileSummary("ligierjsp320__zolder_alt", "Ligier JS P320", "Zolder Circuit (Alternate)", 2, 13, "Media"),
        new ProfileSummary("mx5_cup__lagunaseca", "Global Mazda MX-5 Cup", "WeatherTech Raceway Laguna Seca", 1, 4, null),
    ]);

    /// <summary>The Ligier profile: the capture of mock 7 (now saved) and an older one, switched off.</summary>
    public static Profile Profile()
    {
        var doc = new ProfileDocument
        {
            Car = new CarInfo("ligierjsp320", "Ligier JS P320", 100, 0.75),
            Track = new TrackInfo("zolder alt", "Zolder Circuit", "Alternate", 4.0),
        };
        doc.Captures.Add(Capture("2026-10-29T20:03:40Z", true, "Practice", new CaptureConditions(31.4, 24.0, 1, 0, 0, 100), Laps()));
        doc.Captures.Add(Capture("2026-10-27T18:41:05Z", false, "Race", new CaptureConditions(27.0, 21.0, 3, 0, 0, 100),
            Laps().Take(7).ToArray()));
        return new Profile("ligierjsp320__zolder_alt", JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument));
    }

    static CaptureRecord Capture(string id, bool active, string session, CaptureConditions c, CapturedLapDto[] laps) => new()
    {
        Id = id, Active = active, SessionType = session, Conditions = c,
        Laps = laps.Select(l => new CapturedLap
        {
            Lap = l.Lap, TimeS = l.TimeS, FuelL = l.FuelL, FullThrottlePct = l.FullThrottlePct,
            Reasons = LapReasonCodes.FromCodes(l.Reasons),
        }).ToList(),
    };
}

internal static class Sta
{
    /// <summary>Runs <paramref name="body"/> on a new STA thread (WPF) and rethrows what it threw.</summary>
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromMinutes(2))) throw new TimeoutException("STA body did not finish");
        error?.Throw();
    }
}
