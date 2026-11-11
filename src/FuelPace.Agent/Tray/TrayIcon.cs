using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using FuelPace.Agent.Win32;
using FuelPace.App.Host;
using FuelPace.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FuelPace.Agent.Tray;

/// <summary>
/// The notification-area icon (<c>Shell_NotifyIcon</c>, version 4 callbacks), its context menu and balloons. Methods may be called
/// from any thread: the work runs on the <see cref="MessageWindow"/>'s thread. <see cref="Command"/> is raised there.
/// Focus: the overlay never takes it. The one exception is the context menu, which Windows only dismisses correctly when its
/// owner is the foreground window, so <c>SetForegroundWindow</c> is called there, in response to the user's right-click on
/// the icon and never otherwise. Balloons (<c>NIF_INFO</c>) do not activate anything.
/// </summary>
public sealed unsafe class TrayIcon : ITrayIcon, IDisposable
{
    private const uint WM_TRAY = PInvoke.WM_APP + 10;
    private const uint IconId = 1;
    private const int MaxTooltip = 127; // szTip holds 128 UTF-16 units including the terminator
    private const int MaxBalloonTitle = 63, MaxBalloonText = 255;
    private static readonly TimeSpan BalloonRepeat = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MarshalTimeout = TimeSpan.FromSeconds(2);

    private readonly MessageWindow _window;
    private readonly Func<DiagLog> _logSource; // the host's log exists only after the host started
    private readonly TimeProvider _time;
    private readonly uint _taskbarCreated;
    private readonly Dictionary<string, DateTimeOffset> _lastBalloon = [];
    // UI thread only from here on.
    private HICON _icon;
    private bool _ownsIcon, _added, _debug, _disposed;
    private string _tooltip = "FuelPace";

    /// <summary>Create on the UI thread (it adds a message hook) and the icon is added at once.</summary>
    public TrayIcon(MessageWindow window, Func<DiagLog>? log = null, TimeProvider? time = null)
    {
        _window = window;
        _logSource = log ?? (() => DiagLog.Null);
        _time = time ?? TimeProvider.System;
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        window.AddHook(OnMessage);
        LoadIcon();
        AddIcon();
    }

    private DiagLog _log => _logSource();

    public event Action<TrayCommand>? Command;

    /// <summary>False when the shell refused the icon (no notification area, e.g. a service session). UI thread.</summary>
    public bool IconAdded => _added;

    public void SetTooltip(string text) => OnUi(() =>
    {
        _tooltip = Truncate(text, MaxTooltip);
        if (_added) Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY, NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP, null, null);
    });

    public void SetDebugMenu(bool visible) => OnUi(() => _debug = visible);

    public void Notify(string title, string text) => OnUi(() =>
    {
        if (!_added) return;
        var now = _time.GetUtcNow();
        string key = title + "\n" + text;
        if (_lastBalloon.TryGetValue(key, out var last) && now - last < BalloonRepeat) return;
        if (_lastBalloon.Count > 32) _lastBalloon.Clear();
        _lastBalloon[key] = now;
        Notify(NOTIFY_ICON_MESSAGE.NIM_MODIFY, NOTIFY_ICON_DATA_FLAGS.NIF_INFO, Truncate(title, MaxBalloonTitle),
            Truncate(text, MaxBalloonText));
    });

    /// <summary>Cuts to <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        int n = max;
        if (char.IsHighSurrogate(text[n - 1])) n--;
        return text[..n];
    }

    private void OnUi(Action a)
    {
        if (_window.IsUiThread) { if (!_disposed) Run(a); }
        else _window.Post(() => { if (!_disposed) Run(a); });
    }

    private void Run(Action a)
    {
        try { a(); }
        catch (Exception e) when (e is not OutOfMemoryException) { _log.Exception("tray", e); }
    }

    private void LoadIcon()
    {
        try
        {
            _icon = IconFromResource(out _ownsIcon);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            _log.Exception("tray", e);
        }
        if (_icon.IsNull)
        {
            _icon = PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION);
            _ownsIcon = false;
        }
    }

    /// <summary>The embedded <c>icon.ico</c> image closest to the small-icon size (PNG or DIB entries both work).</summary>
    private static HICON IconFromResource(out bool owned)
    {
        owned = false;
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("FuelPace.icon.ico");
        if (stream is null) return default;
        byte[] ico = new byte[stream.Length];
        stream.ReadExactly(ico);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        int want = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        int best = -1, bestSize = 0;
        for (int i = 0; i < count; i++)
        {
            var e = ico.AsSpan(6 + i * 16, 16);
            int size = e[0] == 0 ? 256 : e[0];
            if (best < 0 || (bestSize < want ? size > bestSize : size >= want && size < bestSize)) { best = i; bestSize = size; }
        }
        if (best < 0) return default;
        var entry = ico.AsSpan(6 + best * 16, 16);
        uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
        if (offset < 0 || offset + bytes > ico.Length) throw new InvalidDataException("icon.ico is truncated");
        fixed (byte* p = &ico[offset])
        {
            var icon = PInvoke.CreateIconFromResourceEx(p, bytes, true, 0x00030000, bestSize, bestSize, 0);
            owned = !icon.IsNull;
            return icon;
        }
    }

    private void AddIcon()
    {
        _added = false;
        if (!Notify(NOTIFY_ICON_MESSAGE.NIM_ADD, NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON
                | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP, null, null))
        {
            _log.Text(DiagLevel.Warn, "tray", "the shell did not accept the notification icon");
            return;
        }
        _added = true;
        var data = NewData(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE);
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    private NOTIFYICONDATAW NewData(NOTIFY_ICON_DATA_FLAGS flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = new HWND(_window.Handle),
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAY,
        hIcon = _icon,
    };

    private bool Notify(NOTIFY_ICON_MESSAGE message, NOTIFY_ICON_DATA_FLAGS flags, string? title, string? info)
    {
        var data = NewData(flags);
        if ((flags & NOTIFY_ICON_DATA_FLAGS.NIF_TIP) != 0) Copy(_tooltip, data.szTip.AsSpan());
        if ((flags & NOTIFY_ICON_DATA_FLAGS.NIF_INFO) != 0)
        {
            Copy(title ?? "", data.szInfoTitle.AsSpan());
            Copy(info ?? "", data.szInfo.AsSpan());
            data.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_NOSOUND
                | NOTIFY_ICON_INFOTIP_FLAGS.NIIF_RESPECT_QUIET_TIME;
        }
        return PInvoke.Shell_NotifyIcon(message, in data);
    }

    private static void Copy(string text, Span<char> destination)
    {
        int n = Math.Min(text.Length, destination.Length - 1);
        text.AsSpan(0, n).CopyTo(destination);
        destination[n] = '\0';
    }

    private bool OnMessage(uint msg, nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (_disposed) return false;
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddIcon(); // Explorer restarted: its icons are gone
            _log.Text(DiagLevel.Info, "tray", "Explorer restarted; the notification icon was added again");
            return true;
        }
        if (msg != WM_TRAY) return false;
        switch ((uint)(lParam & 0xFFFF))
        {
            case PInvoke.WM_CONTEXTMENU:
                ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                break;
            case PInvoke.WM_LBUTTONDBLCLK:
                Command?.Invoke(TrayCommand.OpenSettings);
                break;
        }
        return true;
    }

    private void ShowMenu(int x, int y)
    {
        var hwnd = new HWND(_window.Handle);
        var menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull) return;
        try
        {
            Add(menu, TrayCommand.OpenSettings, "Abrir configurações");
            Add(menu, TrayCommand.ToggleVisible, "Mostrar/ocultar overlay");
            Add(menu, TrayCommand.ToggleEdit, "Modo edição");
            if (_debug)
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
                Add(menu, TrayCommand.InsertMark, "Inserir marcador");
                Add(menu, TrayCommand.CreateBundle, "Gerar pacote de diagnóstico");
                Add(menu, TrayCommand.OpenLogs, "Abrir pasta de logs");
            }
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, default(PCWSTR));
            Add(menu, TrayCommand.Exit, "Sair");

            // The one allowed foreground call (see the class remarks): without it the menu does not close on an outside click.
            PInvoke.SetForegroundWindow(hwnd);
            int chosen = PInvoke.TrackPopupMenuEx(menu, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON),
                x, y, hwnd, null);
            PInvoke.PostMessage(hwnd, PInvoke.WM_NULL, default, default); // documented companion of the call above
            if (chosen > 0) Command?.Invoke((TrayCommand)(chosen - 1));
        }
        finally { PInvoke.DestroyMenu(menu); }
    }

    private static void Add(HMENU menu, TrayCommand c, string text)
    {
        fixed (char* t = text) PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING, (nuint)((int)c + 1), t);
    }

    /// <summary>Removes the icon. Any thread; waits briefly for the UI thread.</summary>
    public void Dispose()
    {
        try
        {
            _window.Invoke(() =>
            {
                if (_disposed) return;
                _disposed = true;
                if (_added) Notify(NOTIFY_ICON_MESSAGE.NIM_DELETE, 0, null, null);
                _added = false;
                if (_ownsIcon && !_icon.IsNull) PInvoke.DestroyIcon(_icon);
            }, MarshalTimeout);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { _log.Exception("tray", e); }
    }
}
