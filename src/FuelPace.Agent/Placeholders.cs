using FuelPace.App.Host;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;

namespace FuelPace.Agent;

// Stand-ins until the Windows overlay (Task 11) and the tray icon and hotkeys (Task 12) exist; Program wires them so the
// Agent already runs end to end (live source, IPC, logs, recordings). They draw nothing and report honestly: hotkeys are
// not registered.

internal sealed class NullOverlaySurface : IOverlaySurface
{
    public string BackendName => "none";
    public event Action<PixelRect>? UserMoved { add { } remove { } }
    public event Action<PixelRect>? UserResized { add { } remove { } }
    public void SetBounds(PixelRect r) { }
    public void Render(DrawList list) { }
    public void SetVisible(bool v) { }
    public void SetEditMode(bool on) { }
    public void Dispose() { }
}

internal sealed class NullTrayIcon : ITrayIcon
{
    public event Action<TrayCommand>? Command { add { } remove { } }
    public void SetTooltip(string text) { }
    public void Notify(string title, string text) { }
    public void SetDebugMenu(bool visible) { }
}

internal sealed class NullHotkeys : IHotkeyService
{
    public event Action<int>? Pressed { add { } remove { } }
    public bool Register(int id, Hotkey key) => false;
    public void UnregisterAll() { }
}
