using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using FuelPace.App.Host;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FuelPace.Agent.Win32;

/// <summary>
/// The Agent's UI thread: a hidden window and its <c>GetMessage</c> loop. It is a hidden top-level window rather than a
/// message-only one (<c>HWND_MESSAGE</c>) because message-only windows get no broadcasts, and the Agent needs
/// <c>WM_DISPLAYCHANGE</c>, <c>WM_SETTINGCHANGE</c> and (Task 12) <c>TaskbarCreated</c>. It is never shown.
/// <list type="bullet">
/// <item><see cref="IUiDispatcher"/>: <see cref="Post"/> (any thread) enqueues on a lock-free queue and posts one
/// <c>WM_APP</c> only when none is pending; the window runs the queue in order. Posting a cached delegate allocates nothing
/// once the queue's segment has grown to the backlog.</item>
/// <item><see cref="DisplayMessage"/>: <c>WM_DISPLAYCHANGE</c>, <c>WM_DPICHANGED</c> and <c>WM_SETTINGCHANGE</c>
/// (<see cref="MonitorProvider"/> turns them into <see cref="IMonitorProvider.Changed"/>).</item>
/// <item><see cref="AddHook"/>: other messages for the hotkeys and the tray icon (Task 12).</item>
/// <item>Ending: <c>WM_CLOSE</c> (<c>taskkill</c> without <c>/F</c>, updaters, Restart Manager) ends <see cref="Run"/> without
/// destroying the window, so the caller shuts down normally; a <c>WM_DESTROY</c> not caused by <see cref="Dispose"/> also
/// ends it. <c>WM_ENDSESSION</c> raises <see cref="SessionEnding"/> first: Windows may end the process as soon as that
/// message returns, so the handler must finish the shutdown there.</item>
/// </list>
/// Create, <see cref="Run"/> and <see cref="Dispose"/> on the same thread. An exception thrown by a posted action or a hook
/// ends the loop and is rethrown by <see cref="Run"/> (so the crash handler logs it) instead of crossing the native callback.
/// </summary>
public sealed unsafe class MessageWindow : IUiDispatcher, IDisposable
{
    /// <summary>The dispatcher's message.</summary>
    public const uint WM_DISPATCH = PInvoke.WM_APP + 1;
    private const uint WM_QUIT_REQUEST = PInvoke.WM_APP + 2;

    /// <summary>True when the message was handled; <paramref name="result"/> is then returned to Windows.</summary>
    public delegate bool MessageHook(uint msg, nuint wParam, nint lParam, out nint result);

    private static int s_count;

    private readonly WNDPROC _proc; // referenced here so the delegate outlives the native window class
    private readonly string _className;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly List<MessageHook> _hooks = [];
    private readonly HWND _hwnd;
    private readonly HINSTANCE _instance;
    private ExceptionDispatchInfo? _failure;
    private readonly uint _threadId;
    private int _signaled, _disposed;

    public MessageWindow()
    {
        _proc = WndProc;
        _threadId = PInvoke.GetCurrentThreadId();
        _className = $"FuelPace.Agent.MessageWindow.{Environment.ProcessId}.{Interlocked.Increment(ref s_count)}";
        HINSTANCE instance = _instance = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* name = _className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _proc,
                hInstance = instance,
                lpszClassName = name,
            };
            if (PInvoke.RegisterClassEx(in wc) == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "RegisterClassEx failed");
            _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, name, default, WINDOW_STYLE.WS_POPUP,
                0, 0, 0, 0, HWND.Null, HMENU.Null, instance, null);
        }
        if (_hwnd.IsNull)
        {
            int error = Marshal.GetLastPInvokeError();
            Unregister();
            throw new Win32Exception(error, "CreateWindowEx failed");
        }
    }

    private unsafe void Unregister()
    {
        fixed (char* name = _className) PInvoke.UnregisterClass(name, _instance);
    }

    public nint Handle => (nint)_hwnd.Value;

    /// <summary>A display, resolution, DPI or work-area change may have happened (UI thread).</summary>
    public event Action? DisplayMessage;

    /// <summary>The user session ends (log off, shutdown): runs inside <c>WM_ENDSESSION</c> on the UI thread, before Windows may
    /// terminate the process; then <see cref="Run"/> ends.</summary>
    public event Action? SessionEnding;

    /// <summary>UI thread. Hooks are asked in the order added until one handles the message.</summary>
    public void AddHook(MessageHook hook) => _hooks.Add(hook);

    public void Post(Action a)
    {
        _queue.Enqueue(a);
        if (Interlocked.Exchange(ref _signaled, 1) == 0 && !PInvoke.PostMessage(_hwnd, WM_DISPATCH, default, default))
            Volatile.Write(ref _signaled, 0); // window gone (shutting down): nothing will run; a later post tries again
    }

    /// <summary>True on the window's thread (the only one that may call <c>RegisterHotKey</c> or touch the tray icon).</summary>
    public bool IsUiThread => PInvoke.GetCurrentThreadId() == _threadId;

    /// <summary>
    /// Runs <paramref name="a"/> on the UI thread and waits for it: at once when already there, else posted and awaited for at most
    /// <paramref name="timeout"/>. False means the action did not run and never will: on a timeout the posted action is
    /// abandoned (it checks that before starting), so the result always matches reality. An action already running when the
    /// timeout hits is awaited to the end. An exception of the action is rethrown to the caller.
    /// </summary>
    public bool Invoke(Action a, TimeSpan timeout)
    {
        if (IsUiThread) { a(); return true; }
        const int Pending = 0, Running = 1, Abandoned = 2;
        int state = Pending;
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(() =>
        {
            if (Interlocked.CompareExchange(ref state, Running, Pending) != Pending) return; // the caller gave up
            try { a(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeout))
        {
            if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending) return false;
            done.Wait(); // started just now: it finishes shortly
        }
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return true;
    }

    /// <summary>Ends <see cref="Run"/> (any thread).</summary>
    public void Quit() => PInvoke.PostMessage(_hwnd, WM_QUIT_REQUEST, default, default);

    /// <summary>Pumps messages until <see cref="Quit"/>. Rethrows the first exception of a posted action or a hook.</summary>
    public void Run()
    {
        while (true)
        {
            int r = PInvoke.GetMessage(out MSG msg, HWND.Null, 0, 0);
            if (r == 0) break;
            if (r == -1)
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "GetMessage failed");
            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }
        _failure?.Throw();
    }

    private LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_DISPATCH:
                    // A full fence: the reset must be visible before the queue is read, or a Post racing with the last
                    // TryDequeue could see 1, post nothing and strand its action.
                    Interlocked.Exchange(ref _signaled, 0);
                    while (_failure is null && _queue.TryDequeue(out var a)) a();
                    return (LRESULT)0;
                case WM_QUIT_REQUEST:
                case PInvoke.WM_CLOSE: // not DefWindowProc: that destroys the window and nothing would end the loop
                    PInvoke.PostQuitMessage(0);
                    return (LRESULT)0;
                case PInvoke.WM_DESTROY:
                    if (Volatile.Read(ref _disposed) == 0) PInvoke.PostQuitMessage(0); // destroyed from outside
                    return (LRESULT)0;
                case PInvoke.WM_ENDSESSION:
                    if (wParam.Value != 0)
                    {
                        SessionEnding?.Invoke();
                        PInvoke.PostQuitMessage(0);
                    }
                    return (LRESULT)0;
                case PInvoke.WM_DISPLAYCHANGE:
                case PInvoke.WM_DPICHANGED:
                case PInvoke.WM_SETTINGCHANGE:
                    DisplayMessage?.Invoke();
                    break;
            }
            foreach (var hook in _hooks)
                if (hook(msg, wParam.Value, lParam.Value, out nint result)) return (LRESULT)result;
        }
        catch (Exception e)
        {
            _failure ??= ExceptionDispatchInfo.Capture(e);
            PInvoke.PostQuitMessage(1);
            return (LRESULT)0;
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Destroys the window (its thread). Actions still queued are dropped.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        PInvoke.DestroyWindow(_hwnd);
        Unregister();
        GC.KeepAlive(_proc);
    }
}
