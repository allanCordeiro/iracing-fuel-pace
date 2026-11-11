using System.Runtime.InteropServices;
using FuelPace.Agent.Overlay;
using FuelPace.Agent.Win32;
using FuelPace.App.Diagnostics;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;

namespace FuelPace.Windows.Tests;

internal static partial class User32
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    public const uint WM_NCHITTEST = 0x84, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_NCLBUTTONDOWN = 0xA1, WM_TIMER = 0x113, WM_DPICHANGED = 0x2E0, WM_CLOSE = 0x10;
    public const int HTTRANSPARENT = -1, HTCLIENT = 1, HTBOTTOMRIGHT = 17;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(Point p);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point p);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect r);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    /// <summary>The top-level window under a screen point (WindowFromPoint may return a child).</summary>
    public static nint TopLevelAt(int x, int y) => GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2 /* GA_ROOT */);

    public static nint ScreenPoint(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    public static int HitTest(nint hwnd, int x, int y) => (int)SendMessage(hwnd, WM_NCHITTEST, 0, ScreenPoint(x, y));

    public static PixelRect WindowRect(nint hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

/// <summary>Reads the composed screen (GDI with CAPTUREBLT, which includes layered and composition windows) and owns a plain
/// opaque window of a known color to put behind the overlay.</summary>
internal static unsafe partial class Screen
{
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint dc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] private static partial uint GetPixel(nint dc, int x, int y);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("dwmapi.dll")] private static partial int DwmFlush();
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] private static partial ushort RegisterClassEx(WndClassEx* wc);
    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UnregisterClass(string name, nint instance);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h,
        nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool UpdateWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(Msg* msg);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] private static partial nint DispatchMessage(Msg* msg);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] private static partial nint GetModuleHandle(nint name);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint Size, Style;
        public nint WndProc;
        public int ClsExtra, WndExtra;
        public nint Instance, Icon, Cursor, Background, MenuName, ClassName, IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y, Private;
    }

    /// <summary>The composed screen pixel at (x, y).</summary>
    public static (int R, int G, int B) Pixel(int x, int y)
    {
        nint screen = GetDC(0), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, 1, 1);
        nint old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, 1, 1, screen, x, y, 0x00CC0020 /* SRCCOPY */ | 0x40000000 /* CAPTUREBLT */);
            uint c = GetPixel(mem, 0, 0);
            return ((int)(c & 0xFF), (int)((c >> 8) & 0xFF), (int)((c >> 16) & 0xFF));
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Runs this thread's messages and waits for the compositor, for about <paramref name="ms"/>.</summary>
    public static void Settle(int ms = 100)
    {
        var end = Environment.TickCount64 + ms;
        Msg m;
        do
        {
            while (PeekMessage(&m, 0, 0, 0, 1 /* PM_REMOVE */))
            {
                TranslateMessage(&m);
                DispatchMessage(&m);
            }
            DwmFlush();
        } while (Environment.TickCount64 < end);
    }

    /// <summary>A topmost, never-activated, opaque window filled with <paramref name="rgb"/> (0xRRGGBB).</summary>
    public sealed class Backdrop : IDisposable
    {
        private readonly string _class = "FuelPace.Tests.Backdrop." + Guid.NewGuid().ToString("N");
        private readonly nint _brush;
        public readonly nint Hwnd;

        public Backdrop(PixelRect r, uint rgb)
        {
            _brush = CreateSolidBrush(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
            nint user32 = NativeLibrary.Load("user32.dll");
            fixed (char* name = _class)
            {
                var wc = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx), WndProc = NativeLibrary.GetExport(user32, "DefWindowProcW"),
                    Instance = GetModuleHandle(0), Background = _brush, ClassName = (nint)name,
                };
                Assert.NotEqual(0, RegisterClassEx(&wc));
            }
            Hwnd = CreateWindowEx(0x8 | 0x80 | 0x8000000 /* TOPMOST | TOOLWINDOW | NOACTIVATE */, _class, "", 0x80000000 /* POPUP */,
                r.X, r.Y, r.Width, r.Height, 0, 0, GetModuleHandle(0), 0);
            Assert.NotEqual(0, Hwnd);
            ShowWindow(Hwnd, 4 /* SW_SHOWNOACTIVATE */);
            UpdateWindow(Hwnd);
        }

        public void Dispose()
        {
            DestroyWindow(Hwnd);
            UnregisterClass(_class, GetModuleHandle(0));
            DeleteObject(_brush);
        }
    }
}

public sealed class OverlayWindowTests
{
    static DrawList Build(bool edit)
    {
        var p = OverlayPreviews.All[0];
        var s = p.State;
        var d = new DrawList();
        OverlayLayout.Build(in s, OverlayPreviews.Car, OverlayPreviews.Track, p.Options with { EditMode = edit }, new FixedMetricsMeasurer(), d);
        return d;
    }

    static PixelRect Bounds(DrawList d, int x = 300, int y = 300) =>
        new(x, y, (int)Math.Ceiling(d.Size.W), (int)Math.Ceiling(d.Size.H));

    static RectF LastHandle(DrawList d)
    {
        RectF last = default;
        foreach (var c in d.Commands) if (c.Kind == CommandKind.FillRect) last = c.Rect; // TL, TR, BL, BR
        return last;
    }

    static void SkipWithoutComposition(RenderBackend backend, OverlayWindow? w = null)
    {
        if (backend != RenderBackend.Composition) return;
        Assert.SkipUnless(User32.DwmIsCompositionEnabled(out bool on) == 0 && on, "DWM composition is not available on this runner");
        if (w is not null)
            Assert.SkipUnless(w.Backend == RenderBackend.Composition, "DirectComposition did not start on this runner: " + w.SelectionReason);
    }

    [Theory]
    [InlineData(RenderBackend.Composition)]
    [InlineData(RenderBackend.Layered)]
    public void Locked_overlay_is_visible_on_screen_over_what_is_behind_it(RenderBackend backend)
    {
        SkipWithoutComposition(backend);
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(backend, fonts);
        SkipWithoutComposition(backend, w);
        var list = Build(edit: false);
        var r = Bounds(list);
        // In the panel's left padding, half way down: only the panel's background is drawn there.
        int sx = r.X + 4, sy = r.Y + r.Height / 2;
        const uint Magenta = 0xFF00FF;
        using var backdrop = new Screen.Backdrop(new PixelRect(r.X - 40, r.Y - 40, r.Width + 80, r.Height + 80), Magenta);
        Screen.Settle(300);
        var before = Screen.Pixel(sx, sy);
        Assert.SkipUnless(Near(before, (255, 0, 255), 12), $"screen capture does not show windows on this runner (read {before})");

        w.SetBounds(r);
        w.Render(list);
        w.SetVisible(true);
        // rgba(10,12,16,0.6) over magenta
        var expected = (R: 0.6 * 10 + 0.4 * 255, G: 0.6 * 12, B: 0.6 * 16 + 0.4 * 255);
        var got = before;
        for (int i = 0; i < 30 && !Near(got, expected, 12); i++)
        {
            Screen.Settle(100);
            got = Screen.Pixel(sx, sy);
        }
        Assert.True(Near(got, expected, 12), $"{backend}: screen at ({sx},{sy}) is {got}, expected the panel over magenta {expected}");
        Assert.NotEqual(w.Handle, User32.GetForegroundWindow());
    }

    static bool Near((int R, int G, int B) a, (double R, double G, double B) b, int tol) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    [Theory]
    [InlineData(RenderBackend.Composition)]
    [InlineData(RenderBackend.Layered)]
    public void Edit_mode_drag_moves_and_a_corner_resizes_keeping_the_ratio_without_activation(RenderBackend backend)
    {
        SkipWithoutComposition(backend);
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(backend, fonts);
        SkipWithoutComposition(backend, w);
        var edit = Build(edit: true);
        var r = Bounds(edit);
        w.SetEditMode(true);
        w.SetBounds(r);
        w.SetVisible(true);
        w.Render(edit);
        var moves = new List<PixelRect>();
        var resizes = new List<PixelRect>();
        w.UserMoved += moves.Add;
        w.UserResized += resizes.Add;
        bool canRead = User32.GetCursorPos(out var saved);
        bool placed = User32.SetCursorPos(r.X + r.Width / 2, r.Y + r.Height / 2);
        User32.GetCursorPos(out var at);
        Assert.SkipUnless(canRead && placed && at.X == r.X + r.Width / 2, "the cursor cannot be positioned on this runner");
        try
        {
            // Drag the panel by (50, 30): the window follows while the button is down, the release reports it.
            User32.SendMessage(w.Handle, User32.WM_LBUTTONDOWN, 1, 0);
            User32.SetCursorPos(at.X + 50, at.Y + 30);
            User32.SendMessage(w.Handle, User32.WM_MOUSEMOVE, 1, 0);
            Assert.Equal(new PixelRect(r.X + 50, r.Y + 30, r.Width, r.Height), User32.WindowRect(w.Handle)); // live
            Assert.Empty(moves);
            User32.SendMessage(w.Handle, User32.WM_LBUTTONUP, 0, 0);
            var moved = Assert.Single(moves);
            Assert.Equal(new PixelRect(r.X + 50, r.Y + 30, r.Width, r.Height), moved);
            Assert.Empty(resizes);
            w.SetBounds(moved); // the host answers with the new placement

            // Pull the bottom-right handle 60 px right: the window grows keeping its ratio, the top-left stays.
            var h = LastHandle(edit);
            User32.SetCursorPos(moved.X + (int)(h.X + h.W / 2), moved.Y + (int)(h.Y + h.H / 2));
            User32.SendMessage(w.Handle, User32.WM_NCLBUTTONDOWN, (nuint)User32.HTBOTTOMRIGHT, 0);
            User32.GetCursorPos(out var c);
            User32.SetCursorPos(c.X + 60, c.Y);
            User32.SendMessage(w.Handle, User32.WM_MOUSEMOVE, 1, 0);
            User32.SendMessage(w.Handle, User32.WM_LBUTTONUP, 0, 0);
            var grown = Assert.Single(resizes);
            Assert.Equal((moved.X, moved.Y, moved.Width + 60), (grown.X, grown.Y, grown.Width));
            Assert.Equal(moved.Height * (moved.Width + 60) / (double)moved.Width, grown.Height, 0);
            Assert.Single(moves);
            Assert.NotEqual(w.Handle, User32.GetForegroundWindow());
        }
        finally { User32.SetCursorPos(saved.X, saved.Y); }
    }

    [Fact] // Review Focus 5: the overlay's own WM_DPICHANGED makes the monitor provider look again
    public void Dpi_change_on_the_overlay_rechecks_the_monitors()
    {
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(RenderBackend.Layered, fonts);
        var monitors = new MonitorProvider(); // nothing enumerated yet: the first recheck finds a change
        int changed = 0;
        monitors.Changed += () => changed++;
        w.DpiChanged += monitors.Recheck;
        Assert.Equal(0, (int)User32.SendMessage(w.Handle, User32.WM_DPICHANGED, 0x00900090, 0));
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(RenderBackend.Composition)]
    [InlineData(RenderBackend.Layered)]
    public void Locked_window_is_click_through_and_edit_mode_takes_the_mouse(RenderBackend backend)
    {
        SkipWithoutComposition(backend);
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(backend, fonts);
        SkipWithoutComposition(backend, w);
        if (backend == RenderBackend.Layered) Assert.Equal(RenderBackend.Layered, w.Backend);
        Assert.Equal(BackendSelection.Name(w.Backend), w.BackendName);

        var locked = Build(edit: false);
        var r = Bounds(locked);
        w.SetBounds(r);
        w.SetVisible(true);
        w.Render(locked);
        Assert.True(User32.IsWindowVisible(w.Handle));

        long ex = User32.GetWindowLongPtr(w.Handle, User32.GWL_EXSTYLE);
        long want = User32.WS_EX_TRANSPARENT | User32.WS_EX_LAYERED | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOPMOST;
        Assert.Equal(want, ex & want);
        Assert.Equal(0, ex & User32.WS_EX_APPWINDOW); // a tool window without APPWINDOW never gets a taskbar button
        int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        Assert.NotEqual(w.Handle, User32.TopLevelAt(cx, cy)); // clicks go to the game below
        Assert.Equal(User32.HTTRANSPARENT, User32.HitTest(w.Handle, cx, cy));

        var edit = Build(edit: true);
        var re = Bounds(edit);
        w.SetEditMode(true);
        w.SetBounds(re);
        w.Render(edit);
        ex = User32.GetWindowLongPtr(w.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(0, ex & User32.WS_EX_TRANSPARENT);
        Assert.Equal(w.Handle, User32.TopLevelAt(re.X + re.Width / 2, re.Y + re.Height / 2));
        // Between the banner and the outline nothing is drawn: still the overlay's (layered: the 1/255 background).
        Assert.Equal(w.Handle, User32.TopLevelAt(re.X + 150, re.Y + 25));
        // Hit-test codes: the bottom-right handle resizes, the panel moves (client area, our own drag).
        var handle = LastHandle(edit);
        Assert.Equal(User32.HTBOTTOMRIGHT, User32.HitTest(w.Handle, re.X + (int)(handle.X + handle.W / 2), re.Y + (int)(handle.Y + handle.H / 2)));
        Assert.Equal(User32.HTCLIENT, User32.HitTest(w.Handle, re.X + re.Width / 2, re.Y + re.Height / 2));

        w.SetEditMode(false);
        ex = User32.GetWindowLongPtr(w.Handle, User32.GWL_EXSTYLE);
        Assert.NotEqual(0, ex & User32.WS_EX_TRANSPARENT);
        Assert.NotEqual(w.Handle, User32.GetForegroundWindow()); // showing and toggling never took the focus
    }

    [Fact]
    public void Logs_the_backend_on_first_use_and_each_topmost_reassertion()
    {
        AppDiagEvents.RegisterAll();
        var log = new DiagLog(DiagLevel.Trace);
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(RenderBackend.Layered, fonts, () => log);
        var buf = new DiagRecord[16];
        Assert.Equal(0, log.DrainRecords(buf)); // the host's log may not exist yet while the window is created

        w.SetEditMode(false); // the host's first call (UI start)
        int n = log.DrainRecords(buf);
        var backend = Assert.Single(buf.Take(n), r => r.Event == AppDiagEvents.OverlayBackend);
        Assert.Equal((int)RenderBackend.Layered, backend.I0);
        Assert.True(log.TryDequeueText(out var text));
        Assert.Contains("layered", text.Message);

        var locked = Build(edit: false);
        w.SetBounds(Bounds(locked));
        w.Render(locked);
        w.SetVisible(true);
        User32.SendMessage(w.Handle, User32.WM_TIMER, 1, 0); // the 2 s timer
        n = log.DrainRecords(buf);
        var top = Assert.Single(buf.Take(n), r => r.Event == AppDiagEvents.TopmostReasserted);
        Assert.Equal(DiagLevel.Trace, top.Level);
        Assert.Equal(1, top.I0); // cause: timer
    }

    [Fact] // a tool closing the overlay window alone must not leave a headless Agent
    public void Wm_close_does_not_destroy_the_overlay()
    {
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(RenderBackend.Layered, fonts);
        var locked = Build(edit: false);
        w.SetBounds(Bounds(locked));
        w.Render(locked);
        w.SetVisible(true);
        Assert.Equal(0, (int)User32.SendMessage(w.Handle, User32.WM_CLOSE, 0, 0));
        Screen.Settle(50);
        Assert.True(User32.IsWindow(w.Handle));
        Assert.True(User32.IsWindowVisible(w.Handle));
        w.Render(Build(edit: true)); // still drawable
    }

    [Fact]
    public void Hidden_until_shown_and_disposal_from_another_thread_does_not_throw()
    {
        using var fonts = new DWriteFonts();
        var w = new OverlayWindow(RenderBackend.Layered, fonts);
        Assert.False(User32.IsWindowVisible(w.Handle));
        var locked = Build(edit: false);
        w.SetBounds(Bounds(locked));
        w.Render(locked);
        Assert.False(User32.IsWindowVisible(w.Handle));
        var t = new Thread(w.Dispose);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(5)));
        w.Dispose(); // owner thread: now destroys it
    }
}
