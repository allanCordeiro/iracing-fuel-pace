using System.Collections.Concurrent;
using FuelPace.App.Host;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;

namespace FuelPace.App.Tests.Fakes;

/// <summary>A UI thread of its own: actions run one at a time, in order, like the Agent's message loop.</summary>
public sealed class FakeUi : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _pending;

    public FakeUi()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "fake ui" };
        _thread.Start();
    }

    public ConcurrentQueue<Exception> Errors { get; } = new();
    public int Posts;

    public void Post(Action a)
    {
        Interlocked.Increment(ref Posts);
        Interlocked.Increment(ref _pending);
        try { _queue.Add(a); }
        catch (InvalidOperationException) { Interlocked.Decrement(ref _pending); }
    }

    /// <summary>Runs <paramref name="a"/> on the UI thread and waits for it.</summary>
    public T Invoke<T>(Func<T> a)
    {
        T result = default!;
        using var done = new ManualResetEventSlim();
        Post(() => { try { result = a(); } finally { done.Set(); } });
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "UI thread did not run the action");
        return result;
    }

    /// <summary>Waits until everything posted so far (and anything it posts) has run.</summary>
    public void Drain()
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Volatile.Read(ref _pending) > 0)
        {
            Assert.True(DateTime.UtcNow < end, "UI queue did not drain");
            Thread.Sleep(1);
        }
    }

    private void Run()
    {
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception e) { Errors.Enqueue(e); }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

public sealed class FakeSurface : IOverlaySurface
{
    private readonly Lock _gate = new();
    private readonly List<PixelRect> _bounds = [];
    private int _renders;

    public string BackendName => "fake";
    public event Action<PixelRect>? UserMoved;
    public event Action<PixelRect>? UserResized;

    public int Renders => Volatile.Read(ref _renders);
    public bool Visible { get; private set; }
    public bool EditMode { get; private set; }
    public SizeF LastSize { get; private set; }
    public PixelRect? LastBounds { get { using (_gate.EnterScope()) return _bounds.Count == 0 ? null : _bounds[^1]; } }
    public List<PixelRect> AllBounds { get { using (_gate.EnterScope()) return [.. _bounds]; } }

    public void SetBounds(PixelRect r) { using (_gate.EnterScope()) _bounds.Add(r); }

    public List<string> LastTexts { get; private set; } = [];

    public void Render(DrawList list)
    {
        var texts = new List<string>();
        foreach (var c in list.Commands)
            if (c.Kind == CommandKind.Text) texts.Add(new string(list.TextOf(in c)));
        LastTexts = texts;
        LastSize = list.Size;
        Interlocked.Increment(ref _renders);
    }

    public void SetVisible(bool v) => Visible = v;
    public void SetEditMode(bool on) => EditMode = on;
    public void RaiseMoved(PixelRect r) => UserMoved?.Invoke(r);
    public void RaiseResized(PixelRect r) => UserResized?.Invoke(r);
    public void Dispose() { }
}

public sealed class FakeHotkeys : IHotkeyService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Hotkey> _registered = [];

    /// <summary>Keys "owned by another program": registering them fails.</summary>
    public HashSet<Hotkey> Taken { get; } = [];
    public int RegisterCalls;

    public event Action<int>? Pressed;

    public bool Register(int id, Hotkey key)
    {
        Interlocked.Increment(ref RegisterCalls);
        if (Taken.Contains(key)) return false;
        using (_gate.EnterScope()) _registered[id] = key;
        return true;
    }

    public void UnregisterAll() { using (_gate.EnterScope()) _registered.Clear(); }

    public Dictionary<int, Hotkey> Registered { get { using (_gate.EnterScope()) return new(_registered); } }

    public void Press(int id) => Pressed?.Invoke(id);
}

public sealed class FakeTray : ITrayIcon
{
    public ConcurrentQueue<(string Title, string Text)> Notifications { get; } = new();
    public string Tooltip { get; private set; } = "";
    public bool DebugMenu { get; private set; }
    public event Action<TrayCommand>? Command;

    public void SetTooltip(string text) => Tooltip = text;
    public void Notify(string title, string text) => Notifications.Enqueue((title, text));
    public void SetDebugMenu(bool visible) => DebugMenu = visible;
    public void Raise(TrayCommand c) => Command?.Invoke(c);
}

public sealed class FakeMonitors : IMonitorProvider
{
    public static readonly MonitorDesc Primary =
        new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);

    public volatile IReadOnlyList<MonitorDesc> List = [Primary];
    public event Action? Changed;

    public IReadOnlyList<MonitorDesc> GetMonitors() => List;

    public void Set(params MonitorDesc[] monitors)
    {
        List = monitors;
        Changed?.Invoke();
    }
}

public sealed class FakeSystem(string desktopDir) : ISystemServices
{
    public volatile bool Fullscreen;
    public int LaunchSettingsCalls, FullscreenQueries;
    public ConcurrentQueue<string> OpenedFolders { get; } = new();
    public ConcurrentQueue<bool> Autostart { get; } = new();
    public Dictionary<string, string> Info { get; } = new() { ["os"] = "Windows 11 (fake)" };

    public string DesktopDir => desktopDir;
    public IReadOnlyDictionary<string, string> CollectSystemInfo() => new Dictionary<string, string>(Info);

    public bool IsFullscreenExclusiveActive()
    {
        Interlocked.Increment(ref FullscreenQueries);
        return Fullscreen;
    }

    public void OpenFolder(string path) => OpenedFolders.Enqueue(path);
    public void LaunchSettings() => Interlocked.Increment(ref LaunchSettingsCalls);
    public void SetAutostart(bool on) => Autostart.Enqueue(on);
}
