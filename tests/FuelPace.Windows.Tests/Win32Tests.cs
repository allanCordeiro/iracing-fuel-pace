using System.Collections.Concurrent;
using FuelPace.Agent.Win32;
using FuelPace.App.Diagnostics;
using Microsoft.Win32;

namespace FuelPace.Windows.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void Second_instance_is_refused_while_the_first_holds_the_mutex()
    {
        string name = @"Local\FuelPace.Tests.Agent." + Guid.NewGuid().ToString("N");
        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));
        Assert.Null(SingleInstance.TryAcquire(name));

        first.Dispose();
        using var next = SingleInstance.TryAcquire(name);
        Assert.NotNull(next);
    }

    [Fact]
    public void Other_names_do_not_collide()
    {
        using var a = SingleInstance.TryAcquire(@"Local\FuelPace.Tests.A." + Guid.NewGuid().ToString("N"));
        using var b = SingleInstance.TryAcquire(@"Local\FuelPace.Tests.B." + Guid.NewGuid().ToString("N"));
        Assert.NotNull(a);
        Assert.NotNull(b);
    }
}

public class MessageWindowTests
{
    /// <summary>Runs a window and its loop on a thread of its own, like the Agent's main thread.</summary>
    private sealed class UiThread : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        public MessageWindow Window = null!;
        public Exception? RunError;
        public int ThreadId;

        public UiThread()
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                using var w = new MessageWindow();
                Window = w;
                _ready.Set();
                try { w.Run(); }
                catch (Exception e) { RunError = e; }
            }) { IsBackground = true, Name = "test ui" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)));
        }

        public bool Join() => _thread.Join(TimeSpan.FromSeconds(10));

        public void Dispose()
        {
            Window.Quit();
            Join();
        }
    }

    [Fact]
    public void Posts_from_many_threads_run_in_order_on_the_window_thread()
    {
        using var ui = new UiThread();
        const int Threads = 4, Each = 2000;
        var seen = new ConcurrentQueue<(int thread, int seq, int on)>();
        using var done = new CountdownEvent(Threads * Each);
        var posters = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            for (int i = 0; i < Each; i++)
            {
                int seq = i;
                ui.Window.Post(() =>
                {
                    seen.Enqueue((t, seq, Environment.CurrentManagedThreadId));
                    done.Signal();
                });
            }
        })).ToList();
        posters.ForEach(p => p.Start());
        Assert.True(done.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), $"{done.CurrentCount} actions did not run");

        var items = seen.ToArray();
        Assert.All(items, x => Assert.Equal(ui.ThreadId, x.on));
        for (int t = 0; t < Threads; t++)
        {
            var mine = items.Where(x => x.thread == t).Select(x => x.seq).ToArray();
            Assert.Equal(Enumerable.Range(0, Each), mine);
        }
    }

    [Fact]
    public void Quit_ends_the_loop_and_a_failing_action_is_rethrown_by_run()
    {
        var ui = new UiThread();
        ui.Window.Quit();
        Assert.True(ui.Join());
        Assert.Null(ui.RunError);

        var failing = new UiThread();
        failing.Window.Post(() => throw new InvalidOperationException("boom"));
        Assert.True(failing.Join());
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(failing.RunError).Message);
    }

    [Fact]
    public void Monitors_are_listed()
    {
        var list = new MonitorProvider().GetMonitors();
        // A CI runner may have no interactive display; when it has one, it is sane.
        foreach (var m in list)
        {
            Assert.StartsWith(@"\\.\", m.DeviceName);
            Assert.True(m.Bounds.Width > 0 && m.Bounds.Height > 0);
            Assert.True(m.DpiScale >= 1.0);
        }
        if (list.Count > 0) Assert.Single(list, m => m.IsPrimary);
    }
}

public class SystemTests
{
    [Fact]
    public void System_info_has_every_agent_key_and_no_user_identity()
    {
        var info = SystemInfoCollector.Collect();
        foreach (var key in new[] { SystemInfoKeys.Os, SystemInfoKeys.Dotnet, SystemInfoKeys.CpuCount, SystemInfoKeys.Monitors,
                     SystemInfoKeys.Gpu, SystemInfoKeys.SimWindow })
            Assert.False(string.IsNullOrWhiteSpace(info[key]), key);
        Assert.Equal("not running", info[SystemInfoKeys.SimWindow]);
        Assert.DoesNotContain("error", info[SystemInfoKeys.Gpu]);

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var v in info.Values)
        {
            Assert.DoesNotContain(profile, v, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, v, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Fullscreen_query_answers()
    {
        Assert.False(FullscreenDetector.IsD3DFullscreenActive()); // nothing runs fullscreen on the test machine
        Assert.False(string.IsNullOrEmpty(FullscreenDetector.Describe()));
    }

    [Fact]
    public void Autostart_writes_the_quoted_path_and_removes_it()
    {
        string key = @"Software\FuelPace.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var a = new Autostart(key);
            Assert.Null(a.Get());
            a.Set(true, @"C:\Program Files\FuelPace\FuelPace.Agent.exe");
            Assert.Equal("\"C:\\Program Files\\FuelPace\\FuelPace.Agent.exe\"", a.Get());
            a.Set(true); // this process
            Assert.Equal("\"" + Environment.ProcessPath + "\"", a.Get());
            a.Set(false);
            Assert.Null(a.Get());
            a.Set(false); // already off
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\FuelPace.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Autostart_defaults_to_the_run_key()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", Autostart.RunKey);
        Assert.Equal("FuelPace", Autostart.ValueName);
    }
}
