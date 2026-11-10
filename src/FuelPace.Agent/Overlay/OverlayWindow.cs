using System.ComponentModel;
using System.Runtime.InteropServices;
using FuelPace.App.Diagnostics;
using FuelPace.App.Host;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FuelPace.Agent.Overlay;

/// <summary>
/// The overlay window (<see cref="IOverlaySurface"/>). A <c>WS_POPUP</c> with <c>WS_EX_TOPMOST | WS_EX_TOOLWINDOW |
/// WS_EX_NOACTIVATE</c> (no taskbar button, never activated: <c>WM_MOUSEACTIVATE</c> → <c>MA_NOACTIVATE</c>, shown with
/// <c>SW_SHOWNOACTIVATE</c>), always <c>WS_EX_LAYERED</c>, plus <c>WS_EX_NOREDIRECTIONBITMAP</c> with composition (made
/// visible by <c>SetLayeredWindowAttributes(255, LWA_ALPHA)</c>) or per-pixel alpha with the layered fallback; the backend is
/// chosen by <see cref="BackendSelection"/> when the window is created.
/// <list type="bullet">
/// <item>Locked: <c>WS_EX_TRANSPARENT</c> as well, so every click goes to the game (NFR-02).</item>
/// <item>Edit mode: no <c>WS_EX_TRANSPARENT</c>; <c>WM_NCHITTEST</c> answers the corner codes on the handles and
/// <c>HTCAPTION</c> elsewhere. A drag is Windows' move loop and ends in <c>WM_EXITSIZEMOVE</c> → <see cref="UserMoved"/>.
/// A corner is dragged by the window itself (mouse capture; a <c>WS_POPUP</c> without <c>WS_THICKFRAME</c> gets no system
/// size loop, and a thick frame would bring Aero Snap): the ratio is kept (<see cref="OverlayEditGeometry"/>), the picture
/// is redrawn scaled while dragging, and the button release raises <see cref="UserResized"/>. Both report the whole window
/// rect. While the user drags, <see cref="SetBounds"/> is held back so the host does not pull the window away.</item>
/// <item>Topmost: re-asserted (<c>SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE)</c>) on every foreground change
/// (<c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c>, out of context) and every 2 s; logged as <c>TopmostReasserted</c>
/// (Trace).</item>
/// </list>
/// Create and use on the UI thread (its messages arrive through that thread's loop). The backend choice is logged on the
/// first call after creation (the host's log exists by then). <see cref="Dispose"/> from another thread (the session-end
/// shutdown) only posts the destruction to the window's thread.
/// </summary>
public sealed unsafe class OverlayWindow : IOverlaySurface
{
    private const uint WM_DESTROY_REQUEST = PInvoke.WM_APP + 0x40;
    private const nuint TopmostTimer = 1;
    private const uint TopmostPeriodMs = 2000;
    private const int MinSide = 40; // px, the smallest window a corner drag may make
    private const int CauseForeground = 0, CauseTimer = 1; // TopmostReasserted I0

    private static int s_count;

    private readonly WNDPROC _proc; // referenced here so the delegates outlive the native window class and hook
    private readonly WINEVENTPROC _foregroundProc;
    private readonly string _className;
    private readonly HINSTANCE _instance;
    private readonly uint _threadId;
    private readonly Func<DiagLog>? _log;
    private readonly RenderBackend _requested;
    private readonly int _hresult;
    private readonly HWND _hwnd;
    private readonly IOverlayRenderer _renderer;
    private readonly HWINEVENTHOOK _hook;
    private bool _reported, _visible, _edit, _moving, _sizing;
    private PixelRect _windowRect, _requestedBounds, _dragStart;
    private System.Drawing.Point _cursorStart;
    private OverlayHit _corner;
    private float _liveScale = 1;
    private DrawList? _last;
    private int _destroyed, _destroyPosted;

    /// <param name="log">The host's log, read when something is logged (it exists once the host started).</param>
    public OverlayWindow(RenderBackend requested, DWriteFonts fonts, Func<DiagLog>? log = null)
    {
        _proc = WndProc;
        _foregroundProc = OnForeground;
        _log = log;
        _requested = requested;
        _threadId = PInvoke.GetCurrentThreadId();
        _className = $"FuelPace.Agent.Overlay.{Environment.ProcessId}.{Interlocked.Increment(ref s_count)}";
        _instance = PInvoke.GetModuleHandle((PCWSTR)null);
        RegisterClass();
        try
        {
            var (backend, hwnd, renderer) = BackendSelector.Create(requested, CreateHwnd, h => PInvoke.DestroyWindow((HWND)h), fonts, log,
                out string reason, out _hresult);
            Backend = backend;
            SelectionReason = reason;
            _hwnd = (HWND)hwnd;
            _renderer = renderer;
        }
        catch
        {
            Unregister();
            throw;
        }
        if (Backend == RenderBackend.Composition)
            PInvoke.SetLayeredWindowAttributes(_hwnd, default, 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        _hook = PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND, HMODULE.Null, _foregroundProc,
            0, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        PInvoke.SetTimer(_hwnd, TopmostTimer, TopmostPeriodMs, null);
    }

    /// <summary><see cref="RenderBackend.Composition"/> or <see cref="RenderBackend.Layered"/>.</summary>
    public RenderBackend Backend { get; }

    public string BackendName => BackendSelection.Name(Backend);

    /// <summary>Why <see cref="Backend"/> was chosen (logged with the backend).</summary>
    public string SelectionReason { get; }

    public nint Handle => (nint)_hwnd.Value;

    public event Action<PixelRect>? UserMoved;
    public event Action<PixelRect>? UserResized;

    private DiagLog Log => _log?.Invoke() ?? DiagLog.Null;

    // ---------------------------------------------------------------- creation

    private void RegisterClass()
    {
        fixed (char* name = _className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _proc,
                hInstance = _instance,
                lpszClassName = name,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
            };
            if (PInvoke.RegisterClassEx(in wc) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed");
        }
    }

    private void Unregister()
    {
        fixed (char* name = _className) PInvoke.UnregisterClass(name, _instance);
    }

    private nint CreateHwnd(bool composition)
    {
        var ex = WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        if (composition) ex |= WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP;
        HWND hwnd;
        fixed (char* name = _className)
        fixed (char* title = "FuelPace")
            hwnd = PInvoke.CreateWindowEx(ex, name, title, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, _instance, null);
        if (hwnd.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed");
        return (nint)hwnd.Value;
    }

    /// <summary>The backend choice reaches the log on the first call (UI thread, after the host started).</summary>
    private void OnUiCall()
    {
        if (_reported) return;
        _reported = true;
        BackendSelection.Report(Log, _requested, Backend, SelectionReason, _hresult);
    }

    // ---------------------------------------------------------------- IOverlaySurface

    public void SetBounds(PixelRect r)
    {
        OnUiCall();
        _requestedBounds = r;
        if (_moving || _sizing) return; // the user is dragging; the host sends new bounds after the drop
        Apply(r);
    }

    private void Apply(PixelRect r)
    {
        int w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
        bool resized = w != _windowRect.Width || h != _windowRect.Height;
        PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, r.X, r.Y, w, h, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _windowRect = new PixelRect(r.X, r.Y, w, h);
        if (!resized) return;
        _renderer.Resize(w, h);
        if (_last is not null) _renderer.Render(_last, 1, _edit); // a resized buffer holds nothing until drawn
    }

    public void Render(DrawList list)
    {
        OnUiCall();
        _last = list;
        _renderer.Render(list, _sizing ? _liveScale : 1, _edit);
    }

    public void SetVisible(bool v)
    {
        OnUiCall();
        if (v == _visible) return;
        _visible = v;
        if (!v) CancelResize();
        PInvoke.ShowWindow(_hwnd, v ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE);
        if (v) PutOnTop();
    }

    public void SetEditMode(bool on)
    {
        OnUiCall();
        if (on == _edit) return;
        _edit = on;
        if (!on) CancelResize();
        int ex = PInvoke.GetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE); // extended styles are 32 bits
        int transparent = (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        ex = on ? ex & ~transparent : ex | transparent;
        PInvoke.SetWindowLong(_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, ex);
        PInvoke.SetWindowPos(_hwnd, HWND.Null, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE
            | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    // ---------------------------------------------------------------- topmost

    private void PutOnTop() => PInvoke.SetWindowPos(_hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
        SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private void Reassert(int cause)
    {
        if (!_visible || _moving || _sizing || Volatile.Read(ref _destroyed) != 0) return;
        PutOnTop();
        Log.Emit(DiagLevel.Trace, AppDiagEvents.TopmostReasserted, 0, cause);
    }

    private void OnForeground(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try { Reassert(CauseForeground); }
        catch (Exception e) { Log.Exception("overlay", e); }
    }

    // ---------------------------------------------------------------- edit mode: move and resize

    private PixelRect WindowRect()
    {
        PInvoke.GetWindowRect(_hwnd, out RECT r);
        return new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top);
    }

    private uint HitTest(nint lParam)
    {
        if (!_edit) return unchecked((uint)PInvoke.HTTRANSPARENT);
        if (_last is null) return PInvoke.HTCAPTION;
        int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
        var r = WindowRect();
        return OverlayEditGeometry.HitTest(_last, x - r.X, y - r.Y) switch
        {
            OverlayHit.TopLeft => PInvoke.HTTOPLEFT,
            OverlayHit.TopRight => PInvoke.HTTOPRIGHT,
            OverlayHit.BottomLeft => PInvoke.HTBOTTOMLEFT,
            OverlayHit.BottomRight => PInvoke.HTBOTTOMRIGHT,
            _ => PInvoke.HTCAPTION,
        };
    }

    private static OverlayHit Corner(nuint hit) => (uint)hit switch
    {
        PInvoke.HTTOPLEFT => OverlayHit.TopLeft,
        PInvoke.HTTOPRIGHT => OverlayHit.TopRight,
        PInvoke.HTBOTTOMLEFT => OverlayHit.BottomLeft,
        PInvoke.HTBOTTOMRIGHT => OverlayHit.BottomRight,
        _ => OverlayHit.Caption,
    };

    private void BeginResize(OverlayHit corner)
    {
        _sizing = true;
        _corner = corner;
        _dragStart = WindowRect();
        _windowRect = _dragStart;
        PInvoke.GetCursorPos(out _cursorStart);
        _liveScale = 1;
        PInvoke.SetCapture(_hwnd);
    }

    private void UpdateResize()
    {
        PInvoke.GetCursorPos(out var p);
        var r = OverlayEditGeometry.ProportionalResize(_dragStart, _corner, p.X - _cursorStart.X, p.Y - _cursorStart.Y, MinSide, out float f);
        if (r == _windowRect) return;
        PInvoke.SetWindowPos(_hwnd, HWND.Null, r.X, r.Y, r.Width, r.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        _windowRect = r;
        _liveScale = f;
        _renderer.Resize(r.Width, r.Height);
        if (_last is not null) _renderer.Render(_last, f, true);
    }

    private void EndResize()
    {
        if (!_sizing) return;
        _sizing = false; // first: releasing the capture sends WM_CAPTURECHANGED
        if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
        _liveScale = 1;
        var now = WindowRect();
        _windowRect = now;
        if (now != _dragStart) UserResized?.Invoke(now);
        else if (_last is not null) _renderer.Render(_last, 1, _edit);
    }

    /// <summary>Edit mode ended or the overlay hid mid-drag: back to the host's bounds, nothing reported.</summary>
    private void CancelResize()
    {
        if (!_sizing) return;
        _sizing = false;
        if (PInvoke.GetCapture() == _hwnd) PInvoke.ReleaseCapture();
        _liveScale = 1;
        _windowRect = default; // forces the resize and redraw
        Apply(_requestedBounds);
    }

    // ---------------------------------------------------------------- messages

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            switch (msg)
            {
                case PInvoke.WM_MOUSEACTIVATE:
                    return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                case PInvoke.WM_NCHITTEST:
                    return (LRESULT)(nint)(int)HitTest(lParam.Value);
                case PInvoke.WM_SETCURSOR:
                    if (_edit && (lParam.Value & 0xFFFF) == PInvoke.HTCAPTION)
                    {
                        PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_SIZEALL));
                        return (LRESULT)1;
                    }
                    break;
                case PInvoke.WM_NCLBUTTONDOWN:
                    if (_edit && Corner(wParam.Value) is var corner and not OverlayHit.Caption)
                    {
                        BeginResize(corner);
                        return (LRESULT)0;
                    }
                    break; // HTCAPTION: Windows' move loop
                case PInvoke.WM_NCLBUTTONDBLCLK:
                    return (LRESULT)0; // no maximize or restore on a double click
                case PInvoke.WM_ENTERSIZEMOVE:
                    _moving = true;
                    _dragStart = WindowRect();
                    break;
                case PInvoke.WM_EXITSIZEMOVE:
                {
                    _moving = false;
                    var now = WindowRect();
                    _windowRect = now;
                    if (now != _dragStart) UserMoved?.Invoke(now);
                    return (LRESULT)0;
                }
                case PInvoke.WM_MOUSEMOVE:
                    if (_sizing) { UpdateResize(); return (LRESULT)0; }
                    break;
                case PInvoke.WM_LBUTTONUP:
                    if (_sizing) { EndResize(); return (LRESULT)0; }
                    break;
                case PInvoke.WM_CAPTURECHANGED:
                    EndResize(); // the capture went elsewhere mid-drag: keep what was reached
                    return (LRESULT)0;
                case PInvoke.WM_TIMER:
                    if (wParam.Value == TopmostTimer) { Reassert(CauseTimer); return (LRESULT)0; }
                    break;
                case PInvoke.WM_DPICHANGED:
                    return (LRESULT)0; // the host sizes the window for the new monitor after the drop
                case WM_DESTROY_REQUEST:
                    DestroyNow();
                    return (LRESULT)0;
            }
        }
        catch (Exception e)
        {
            Log.Exception("overlay", e); // never across the native callback
            return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---------------------------------------------------------------- disposal

    public void Dispose()
    {
        if (Volatile.Read(ref _destroyed) != 0) return;
        if (PInvoke.GetCurrentThreadId() != _threadId)
        {
            // Session end: the host is disposed on a pool thread. Windows, hooks and timers belong to the UI thread.
            if (Interlocked.Exchange(ref _destroyPosted, 1) == 0)
                PInvoke.PostMessage(_hwnd, WM_DESTROY_REQUEST, default, default);
            return;
        }
        DestroyNow();
    }

    private void DestroyNow()
    {
        if (Interlocked.Exchange(ref _destroyed, 1) != 0) return;
        PInvoke.KillTimer(_hwnd, TopmostTimer);
        if (!_hook.IsNull) PInvoke.UnhookWinEvent(_hook);
        _renderer.Dispose();
        PInvoke.DestroyWindow(_hwnd);
        Unregister();
        GC.KeepAlive(_proc);
        GC.KeepAlive(_foregroundProc);
    }
}
