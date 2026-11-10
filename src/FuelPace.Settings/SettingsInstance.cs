using System.Runtime.InteropServices;

namespace FuelPace.Settings;

/// <summary>
/// One Settings window per user session: the named mutex <c>Local\FuelPace.Settings</c> (only its existence matters) and the
/// event <c>Local\FuelPace.Settings.Show</c>. A second launch sets the event, lets the first instance take the foreground and
/// exits; the first one raises <see cref="ShowRequested"/> (on a background thread) and brings its window to the front.
/// </summary>
public sealed partial class SettingsInstance : IDisposable
{
    public const string MutexName = @"Local\FuelPace.Settings";
    public const string ShowEventName = @"Local\FuelPace.Settings.Show";

    readonly Mutex _mutex;
    readonly EventWaitHandle _show;
    readonly ManualResetEvent _stop = new(false);
    readonly Thread _listener;

    SettingsInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
        _listener = new Thread(Listen) { IsBackground = true, Name = "settings-activation" };
        _listener.Start();
    }

    /// <summary>Raised on a background thread when another launch asked for the window.</summary>
    public event Action? ShowRequested;

    /// <summary>The instance guard, or null when another Settings window is already open (it has been asked to come forward).</summary>
    public static SettingsInstance? TryAcquire()
    {
        Mutex m;
        bool createdNew;
        try { m = new Mutex(initiallyOwned: false, MutexName, out createdNew); }
        catch (UnauthorizedAccessException) { createdNew = false; m = null!; }
        if (createdNew)
            return new SettingsInstance(m, new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName));
        m?.Dispose();
        SignalFirst();
        return null;
    }

    /// <summary>The first instance creates the event right after the mutex: retry briefly if it is just starting.</summary>
    static void SignalFirst()
    {
        for (int i = 0; i < 20; i++)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
            {
                using (ev)
                {
                    AllowSetForegroundWindow(AsfwAny); // this launch is the user's: hand the foreground to the open window
                    ev.Set();
                }
                return;
            }
            Thread.Sleep(100);
        }
    }

    void Listen()
    {
        var handles = new WaitHandle[] { _show, _stop };
        while (WaitHandle.WaitAny(handles) == 0) ShowRequested?.Invoke();
    }

    public void Dispose()
    {
        _stop.Set();
        _listener.Join(TimeSpan.FromSeconds(1));
        _show.Dispose();
        _stop.Dispose();
        _mutex.Dispose();
    }

    const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
