using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using FuelPace.Agent.Live;
using FuelPace.Agent.Overlay;
using FuelPace.Agent.Tray;
using FuelPace.Agent.Win32;
using FuelPace.App;
using FuelPace.App.Diagnostics;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;
using Windows.Win32;

namespace FuelPace.Agent;

internal static class Program
{
    private static readonly TimeSpan FirstInstanceTimeout = TimeSpan.FromSeconds(2);
    /// <summary>Windows may end the process soon after <c>WM_ENDSESSION</c> returns: the shutdown waits at most this long.</summary>
    private static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 0 on a normal exit and when another instance is running (it is asked to show the Settings window); 1 when the agent
    /// failed (logged as <c>fatal</c>); 2 for a bad command line (the reason goes to stderr). Nothing escapes as an unhandled
    /// exception: no error-reporting dialog over the sim.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        string? appData = null;
        try
        {
            if (!AgentArgs.TryParse(args, out var parsed, out var error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(AgentArgs.Usage);
                return 2;
            }
            var options = parsed.ToOptions(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuelPace"),
                AppInfo.DebugMarkerPresent);
            appData = options.AppDataRoot;

            using var instance = SingleInstance.TryAcquire();
            if (instance is null)
            {
                AskFirstInstanceAsync(options.PipeName).GetAwaiter().GetResult();
                return 0;
            }
            return Run(options);
        }
        catch (Exception e)
        {
            WriteFatal(appData, e);
            return 1;
        }
    }

    private static int Run(AgentOptions options)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Win32Exception) { /* keeps the default priority; nothing else depends on it */ }
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        using var window = new MessageWindow();
        var monitors = new MonitorProvider(window);
        AgentHost? hostRef = null; // the factory and the overlay read the host's log, which exists once Start ran
        DiagLog HostLog() => hostRef?.Log ?? DiagLog.Null;
        var sources = new LiveSourceFactory(options.ReplayPath, options.ReplaySpeed, HostLog);
        using var fonts = new DWriteFonts();
        // Only the backend is needed before the host starts (it reads, and owns, settings.json); a locked file means "auto".
        var backend = new SettingsStore(options.AppDataRoot).Load(retryLocked: false).Overlay.RenderBackend;
        using var overlay = new OverlayWindow(backend, fonts, HostLog);
        overlay.DpiChanged += monitors.Recheck; // the overlay's own monitor (the message window hears only the primary)
        var hotkeys = new Hotkeys(window, HostLog);
        using var tray = new TrayIcon(window, HostLog);
        var host = hostRef = new AgentHost(options, sources, overlay, window, hotkeys, tray, monitors,
            new SystemServices(), new DWriteTextMeasurer(fonts), TimeProvider.System);
        window.SessionEnding += () =>
        {
            // Off the UI thread so the wait is bounded; EndSession waits for nothing the UI thread would have to run (it
            // leaves the hotkeys to the process exit).
            host.Log.Text(DiagLevel.Info, "app", "the Windows session is ending");
            if (!Task.Run(host.EndSession).Wait(SessionEndBudget))
                host.Log.Text(DiagLevel.Warn, "app", "shutdown did not finish before the session ended");
        };
        int exitCode = 0;
        try
        {
            host.Start();
            host.Completion.ContinueWith(_ => window.Quit(), TaskScheduler.Default);
            window.Run();
        }
        catch (Exception e)
        {
            host.Log.Exception("fatal", e);
            exitCode = 1;
        }
        finally
        {
            host.Dispose(); // on the UI thread, after the loop (AgentHost's contract); a no-op after a session end
        }
        if (host.Completion.IsFaulted) exitCode = 1;
        return exitCode;
    }

    /// <summary>Review Focus 4: the running Agent opens the Settings window; this one just leaves.</summary>
    private static async Task AskFirstInstanceAsync(string pipeName)
    {
        // This process was started by the user, so it may hand the foreground on: the Settings window the first instance
        // starts can then come to the front.
        PInvoke.AllowSetForegroundWindow(PInvoke.ASFW_ANY);
        try
        {
            await using var client = new IpcClient(pipeName);
            if (await client.ConnectAsync(FirstInstanceTimeout)) await client.SendAsync(new ShowSettingsRequest());
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException
                                      or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
        {
            // The first instance is starting, exiting or runs with other rights: nothing to ask.
        }
    }

    /// <summary>A failure before (or outside) the host's log: best effort into <c>logs\agent-fatal.txt</c>, scrubbed, and stderr.</summary>
    private static void WriteFatal(string? appData, Exception e)
    {
        string text = Privacy.Scrub($"{DateTimeOffset.UtcNow:O} FuelPace.Agent {AppInfo.Version} fatal: {e}{Environment.NewLine}");
        try { Console.Error.Write(text); }
        catch (IOException) { }
        if (appData is null) return;
        try
        {
            var dir = Path.Combine(appData, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "agent-fatal.txt"), text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
