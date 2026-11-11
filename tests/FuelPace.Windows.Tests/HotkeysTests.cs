using System.Runtime.InteropServices;
using FuelPace.Agent.Tray;
using FuelPace.Agent.Win32;
using FuelPace.App.Host;
using FuelPace.App.Settings;

namespace FuelPace.Windows.Tests;

/// <summary>A window and its loop on a thread of its own, like the Agent's main thread.</summary>
internal sealed class UiLoop : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    public MessageWindow Window = null!;
    public int ThreadId;

    public UiLoop()
    {
        _thread = new Thread(() =>
        {
            ThreadId = Environment.CurrentManagedThreadId;
            using var w = new MessageWindow();
            Window = w;
            _ready.Set();
            w.Run();
        }) { IsBackground = true, Name = "test ui" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Runs on the UI thread and returns the result.</summary>
    public T OnUi<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        Assert.True(Window.Invoke(() => { try { result = f(); } catch (Exception e) { error = e; } }, TimeSpan.FromSeconds(10)));
        if (error is not null) throw new InvalidOperationException("UI action failed", error);
        return result;
    }

    public void Dispose()
    {
        Window.Quit();
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}

public class HotkeysTests
{
    private static readonly Hotkey Unusual = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x70 + 23); // F24

    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nuint w, nint l);

    [Fact]
    public void Same_key_registers_once_and_a_second_window_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var a = new UiLoop();
        using var b = new UiLoop();
        var first = a.OnUi(() => new Hotkeys(a.Window));
        var second = b.OnUi(() => new Hotkeys(b.Window));

        Assert.True(first.Register(1, Unusual));
        Assert.False(second.Register(1, Unusual));

        first.UnregisterAll();
        Assert.True(second.Register(1, Unusual)); // the key is free again
        second.UnregisterAll();
    }

    /// <summary>Runs on a thread that is not the UI thread.</summary>
    private static T OffUi<T>(Func<T> f)
    {
        T result = default!;
        var t = new Thread(() => result = f());
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)));
        return result;
    }

    [Fact]
    public void Register_and_unregister_work_from_any_thread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var ui = new UiLoop();
        var keys = ui.OnUi(() => new Hotkeys(ui.Window));
        Assert.True(OffUi(() => keys.Register(7, Unusual)));
        OffUi(() => { keys.UnregisterAll(); return 0; });
        Assert.True(OffUi(() => keys.Register(7, Unusual))); // would fail if still registered
        keys.UnregisterAll();
    }

    [Fact]
    public void Register_that_times_out_leaves_the_key_unregistered()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var a = new UiLoop();
        using var b = new UiLoop();
        var keys = a.OnUi(() => new Hotkeys(a.Window));
        var other = b.OnUi(() => new Hotkeys(b.Window));
        using var release = new ManualResetEventSlim();
        a.Window.Post(() => release.Wait(TimeSpan.FromSeconds(30))); // holds the UI thread

        Assert.False(OffUi(() => keys.Register(1, Unusual))); // gives up after its 2 s
        release.Set();
        a.OnUi(() => 0); // the abandoned action has been through the queue by now
        Assert.True(other.Register(1, Unusual)); // not held by the first window
        other.UnregisterAll();
    }

    [Fact]
    public void Invoke_that_times_out_never_runs_the_action()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var ui = new UiLoop();
        using var release = new ManualResetEventSlim();
        ui.Window.Post(() => release.Wait(TimeSpan.FromSeconds(30)));
        int ran = 0;
        Assert.False(OffUi(() => ui.Window.Invoke(() => ran++, TimeSpan.FromMilliseconds(200))));
        release.Set();
        ui.OnUi(() => 0);
        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task WM_HOTKEY_raises_Pressed_with_the_registered_id()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var ui = new UiLoop();
        var keys = ui.OnUi(() => new Hotkeys(ui.Window));
        var got = new TaskCompletionSource<int>();
        keys.Pressed += id => got.TrySetResult(id);
        PostMessageW(ui.Window.Handle, 0x0312, 42, 0);
        Assert.Equal(42, await got.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}

public class TrayIconTests
{
    [Fact]
    public void Add_modify_notify_and_delete_smoke()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var ui = new UiLoop();
        var tray = ui.OnUi(() => new TrayIcon(ui.Window));
        if (!tray.IconAdded) Assert.Skip("the shell's notification area is not available on this machine");

        tray.SetTooltip(new string('x', 300)); // truncated to the 127 characters NOTIFYICONDATA holds
        tray.SetDebugMenu(true);
        tray.Notify("FuelPace", "teste");
        tray.Notify("FuelPace", "teste"); // identical: rate limited, not an error
        Assert.True(ui.OnUi(() => tray.IconAdded));
        tray.Dispose();
        Assert.False(ui.OnUi(() => tray.IconAdded));
    }

    [Theory]
    [InlineData("abc", 127, "abc")]
    public void Tooltip_limit_keeps_short_text(string text, int max, string expected)
        => Assert.Equal(expected, TrayIcon.Truncate(text, max));

    [Fact]
    public void Tooltip_truncation_never_splits_a_surrogate_pair()
    {
        string s = new string('a', 126) + "\U0001F600";
        string t = TrayIcon.Truncate(s, 127);
        Assert.Equal(new string('a', 126), t);
        Assert.Equal(127, TrayIcon.Truncate(new string('b', 200), 127).Length);
    }
}
