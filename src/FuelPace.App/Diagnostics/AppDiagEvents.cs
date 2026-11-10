using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Diagnostics;

/// <summary>App-side diagnostic events (values from 1000; Core owns 1-999).</summary>
public static class AppDiagEvents
{
    public const DiagEvent Startup = (DiagEvent)1000;
    public const DiagEvent Shutdown = (DiagEvent)1001;
    public const DiagEvent OverlayBackend = (DiagEvent)1002;
    public const DiagEvent OverlayRedraws = (DiagEvent)1003;
    public const DiagEvent DeviceLost = (DiagEvent)1004;
    public const DiagEvent WindowPlaced = (DiagEvent)1005;
    public const DiagEvent MonitorsChanged = (DiagEvent)1006;
    public const DiagEvent HotkeyRegistered = (DiagEvent)1007;
    public const DiagEvent HotkeyPressed = (DiagEvent)1008;
    public const DiagEvent TrayCommand = (DiagEvent)1009;
    public const DiagEvent EditMode = (DiagEvent)1010;
    public const DiagEvent Visibility = (DiagEvent)1011;
    public const DiagEvent IpcClient = (DiagEvent)1012;
    public const DiagEvent IpcMessage = (DiagEvent)1013;
    public const DiagEvent SimWindow = (DiagEvent)1014;
    public const DiagEvent FullscreenExclusive = (DiagEvent)1015;
    public const DiagEvent TopmostReasserted = (DiagEvent)1016;
    public const DiagEvent SettingsSaved = (DiagEvent)1017;
    public const DiagEvent RecordingStarted = (DiagEvent)1018;
    public const DiagEvent RecordingStopped = (DiagEvent)1019;
    public const DiagEvent BundleCreated = (DiagEvent)1020;
    /// <summary>The overlay could not draw or show a frame (logged once per distinct HRESULT and stage until a frame succeeds).</summary>
    public const DiagEvent RenderFailed = (DiagEvent)1021;
    /// <summary>Once a minute (UI thread, with <see cref="OverlayRedraws"/>): where the memory is (PERF-05).</summary>
    public const DiagEvent MemoryMinute = (DiagEvent)1022;
    /// <summary>Once a minute of a live connection: how the sim's data-valid event behaves (ruling A21).</summary>
    public const DiagEvent LiveStats = (DiagEvent)1023;

    private static int _registered;

    public static void RegisterAll()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        Reg(Startup, nameof(Startup), "app");
        Reg(Shutdown, nameof(Shutdown), "app");
        Reg(OverlayBackend, nameof(OverlayBackend), "overlay", i0: "backend", i1: "hresult");
        Reg(OverlayRedraws, nameof(OverlayRedraws), "overlay", i0: "perMinute");
        Reg(DeviceLost, nameof(DeviceLost), "overlay", i1: "hresult");
        Reg(WindowPlaced, nameof(WindowPlaced), "overlay", a: "x", b: "y", c: "scale", d: "dpiScale");
        Reg(MonitorsChanged, nameof(MonitorsChanged), "overlay", i0: "count");
        Reg(HotkeyRegistered, nameof(HotkeyRegistered), "input", i0: "id", i1: "ok");
        Reg(HotkeyPressed, nameof(HotkeyPressed), "input", i0: "id");
        Reg(TrayCommand, nameof(TrayCommand), "input", i0: "command");
        Reg(EditMode, nameof(EditMode), "overlay", i0: "on");
        Reg(Visibility, nameof(Visibility), "overlay", i0: "visible", i1: "reason");
        Reg(IpcClient, nameof(IpcClient), "ipc", i0: "connected");
        Reg(IpcMessage, nameof(IpcMessage), "ipc");
        Reg(SimWindow, nameof(SimWindow), "overlay");
        Reg(FullscreenExclusive, nameof(FullscreenExclusive), "overlay", i0: "detected");
        Reg(TopmostReasserted, nameof(TopmostReasserted), "overlay", i0: "cause");
        Reg(SettingsSaved, nameof(SettingsSaved), "settings");
        Reg(RecordingStarted, nameof(RecordingStarted), "record");
        Reg(RecordingStopped, nameof(RecordingStopped), "record", i0: "frames", i1: "dropped");
        Reg(BundleCreated, nameof(BundleCreated), "app");
        Reg(RenderFailed, nameof(RenderFailed), "overlay", i0: "stage", i1: "hresult");
        Reg(MemoryMinute, nameof(MemoryMinute), "perf", i0: "assemblies", i1: "gen2", a: "workingSetMB", b: "privateMB",
            c: "gcHeapMB", d: "gcCommittedMB");
        Reg(LiveStats, nameof(LiveStats), "telemetry", i0: "waitTimeouts", i1: "staleWakeups", a: "frames", b: "backoffs",
            c: "maxStaleRun");
    }

    private static void Reg(DiagEvent e, string name, string category, string? i0 = null, string? i1 = null,
        string? a = null, string? b = null, string? c = null, string? d = null) =>
        DiagEventCatalog.Register(e, new DiagEventInfo(name, category, i0, i1, a, b, c, d));
}
