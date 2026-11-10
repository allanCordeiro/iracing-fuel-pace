using FuelPace.App.Host;
using FuelPace.App.Settings;

namespace FuelPace.Agent;

// Stand-ins until the tray icon and hotkeys (Task 12) exist; Program wires them so the Agent already runs end to end
// (live source, overlay, IPC, logs, recordings). They report honestly: hotkeys are not registered.

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
