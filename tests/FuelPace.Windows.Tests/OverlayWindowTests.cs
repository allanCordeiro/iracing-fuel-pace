using System.Runtime.InteropServices;
using FuelPace.Agent.Overlay;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;

namespace FuelPace.Windows.Tests;

internal static partial class User32
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;

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

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    /// <summary>The top-level window under a screen point (WindowFromPoint may return a child).</summary>
    public static nint TopLevelAt(int x, int y) => GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2 /* GA_ROOT */);
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

    [Theory]
    [InlineData(RenderBackend.Composition)]
    [InlineData(RenderBackend.Layered)]
    public void Locked_window_is_click_through_and_edit_mode_takes_the_mouse(RenderBackend backend)
    {
        if (backend == RenderBackend.Composition)
            Assert.SkipUnless(User32.DwmIsCompositionEnabled(out bool on) == 0 && on, "DWM composition is not available on this runner");
        using var fonts = new DWriteFonts();
        using var w = new OverlayWindow(backend, fonts);
        if (backend == RenderBackend.Composition)
            Assert.SkipUnless(w.Backend == RenderBackend.Composition, "DirectComposition did not start on this runner: " + w.SelectionReason);
        else
            Assert.Equal(RenderBackend.Layered, w.Backend);
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

        var edit = Build(edit: true);
        var re = Bounds(edit);
        w.SetEditMode(true);
        w.SetBounds(re);
        w.Render(edit);
        ex = User32.GetWindowLongPtr(w.Handle, User32.GWL_EXSTYLE);
        Assert.Equal(0, ex & User32.WS_EX_TRANSPARENT);
        Assert.Equal(w.Handle, User32.TopLevelAt(re.X + re.Width / 2, re.Y + re.Height / 2));

        w.SetEditMode(false);
        ex = User32.GetWindowLongPtr(w.Handle, User32.GWL_EXSTYLE);
        Assert.NotEqual(0, ex & User32.WS_EX_TRANSPARENT);
        Assert.NotEqual(w.Handle, User32.GetForegroundWindow()); // showing and toggling never took the focus
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
