using FuelPace.Agent.Win32;
using FuelPace.App.Host;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace FuelPace.Agent.Tray;

/// <summary>
/// Global hotkeys on the <see cref="MessageWindow"/>. <c>RegisterHotKey</c> and <c>UnregisterHotKey</c> belong to the window's
/// thread, so calls from any other thread (IPC, pool, shutdown) are marshalled there; a call that cannot finish in time reports
/// failure instead of touching the API from the wrong thread. <see cref="Pressed"/> is raised on the UI thread.
/// </summary>
public sealed class Hotkeys : IHotkeyService
{
    private static readonly TimeSpan MarshalTimeout = TimeSpan.FromSeconds(2);

    private readonly MessageWindow _window;
    private readonly Func<DiagLog> _logSource; // the host's log exists only after the host started
    private readonly HashSet<int> _ids = []; // UI thread only

    /// <summary>Create on the UI thread (it adds a message hook).</summary>
    public Hotkeys(MessageWindow window, Func<DiagLog>? log = null)
    {
        _window = window;
        _logSource = log ?? (() => DiagLog.Null);
        window.AddHook(OnMessage);
    }

    private DiagLog _log => _logSource();

    public event Action<int>? Pressed;

    public bool Register(int id, Hotkey key)
    {
        bool ok = false;
        try { if (!_window.Invoke(() => ok = RegisterOnUi(id, key), MarshalTimeout)) return false; }
        catch (Exception e) when (e is not OutOfMemoryException) { _log.Exception("input", e); return false; }
        return ok;
    }

    public void UnregisterAll()
    {
        try { _window.Invoke(UnregisterAllOnUi, MarshalTimeout); }
        catch (Exception e) when (e is not OutOfMemoryException) { _log.Exception("input", e); }
    }

    private bool RegisterOnUi(int id, Hotkey key)
    {
        var hwnd = new HWND(_window.Handle);
        // Re-registering an id (settings changed) replaces its key.
        if (_ids.Remove(id)) PInvoke.UnregisterHotKey(hwnd, id);
        var mods = (HOT_KEY_MODIFIERS)(uint)key.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (!PInvoke.RegisterHotKey(hwnd, id, mods, key.VirtualKey)) return false;
        _ids.Add(id);
        return true;
    }

    private void UnregisterAllOnUi()
    {
        var hwnd = new HWND(_window.Handle);
        foreach (int id in _ids) PInvoke.UnregisterHotKey(hwnd, id);
        _ids.Clear();
    }

    private bool OnMessage(uint msg, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg != PInvoke.WM_HOTKEY) return false;
        Pressed?.Invoke((int)wParam);
        return true;
    }
}
