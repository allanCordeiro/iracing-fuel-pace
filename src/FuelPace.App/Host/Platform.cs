using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Host;

/// <summary>Opens the telemetry source: the live sim or a replay.</summary>
public interface ITelemetrySourceFactory
{
    /// <summary>A new source, or null when the sim is not running (the host tries again every second).</summary>
    ITelemetrySource? TryOpen();
}

/// <summary>
/// The overlay window. Called on the UI thread only. Bounds and the user events are in physical pixels and describe the
/// whole window (in edit mode it includes the chrome around the panel). Topmost handling belongs to the surface.
/// </summary>
public interface IOverlaySurface : IDisposable
{
    void SetBounds(PixelRect r);
    void Render(DrawList list);
    void SetVisible(bool v);
    void SetEditMode(bool on);
    /// <summary>Fixed once the surface exists; read from any thread (snapshots, bundles).</summary>
    string BackendName { get; }
    /// <summary>The user finished dragging the window (edit mode).</summary>
    event Action<PixelRect>? UserMoved;
    /// <summary>The user finished resizing the window (edit mode).</summary>
    event Action<PixelRect>? UserResized;
}

/// <summary>Runs actions on the UI thread, in order. The host only posts cached delegates from the telemetry thread.</summary>
public interface IUiDispatcher
{
    void Post(Action a);
}

/// <summary>Global hotkeys (UI thread). <see cref="Pressed"/> carries the id given to <see cref="Register"/>.</summary>
public interface IHotkeyService
{
    /// <summary>False when the key cannot be registered (typically owned by another program).</summary>
    bool Register(int id, Hotkey key);
    void UnregisterAll();
    event Action<int>? Pressed;
}

public enum TrayCommand { OpenSettings, ToggleVisible, ToggleEdit, InsertMark, CreateBundle, OpenLogs, Exit }

public interface ITrayIcon
{
    void SetTooltip(string text);
    void Notify(string title, string text);
    /// <summary>Shows the debug-only items (insert mark, create bundle, open logs).</summary>
    void SetDebugMenu(bool visible);
    event Action<TrayCommand>? Command;
}

public interface IMonitorProvider
{
    IReadOnlyList<MonitorDesc> GetMonitors();
    /// <summary>Displays, resolution or DPI changed.</summary>
    event Action? Changed;
}

/// <summary>Called from the UI thread, the I/O queue and IPC threads: implementations must be thread-safe.</summary>
public interface ISystemServices
{
    /// <summary>Keys of <see cref="Diagnostics.SystemInfoKeys"/> (the host adds version, debug mode, backend, fullscreen).</summary>
    IReadOnlyDictionary<string, string> CollectSystemInfo();
    bool IsFullscreenExclusiveActive();
    void OpenFolder(string path);
    void LaunchSettings();
    void SetAutostart(bool on);
    string DesktopDir { get; }
}

/// <param name="AppDataRoot">Root of settings.json, logs, profiles and recordings (normally <c>%APPDATA%\FuelPace</c>).</param>
/// <param name="ReplayPath">Set when the source replays a file: session info is then loaded inline on the telemetry thread.</param>
/// <param name="ExitAfter">Smoke tests: <see cref="AgentHost.Completion"/> ends after this long.</param>
/// <param name="EarlierSettingsLoad">What a load of settings.json before the host existed did (the Agent reads the render
/// backend first): that load is the one that migrates or quarantines the file, so the host logs it.</param>
public sealed record AgentOptions(string AppDataRoot, bool DebugFromCommandLine, bool DebugMarker, string? ReplayPath,
    ReplaySpeed ReplaySpeed, TimeSpan? ExitAfter, string PipeName = "FuelPace", SettingsLoadReport? EarlierSettingsLoad = null);
