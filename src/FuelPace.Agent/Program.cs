using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using FuelPace.Agent.Live;
using FuelPace.Agent.Win32;
using FuelPace.App;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;
using FuelPace.Core.Diagnostics;

namespace FuelPace.Agent;

internal static class Program
{
    private static readonly TimeSpan FirstInstanceTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 0 on a normal exit and when another instance is running (it is asked to show the Settings window); 1 when the UI thread
    /// failed (logged as <c>fatal</c>); 2 for a bad command line (the reason goes to stderr).
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
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

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            AskFirstInstanceAsync(options.PipeName).GetAwaiter().GetResult();
            return 0;
        }

        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Win32Exception) { /* keeps the default priority; nothing else depends on it */ }
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        using var window = new MessageWindow();
        var monitors = new MonitorProvider(window);
        AgentHost? host = null;
        var sources = new LiveSourceFactory(options.ReplayPath, options.ReplaySpeed, () => host?.Log ?? DiagLog.Null);
        host = new AgentHost(options, sources, new NullOverlaySurface(), window, new NullHotkeys(), new NullTrayIcon(), monitors,
            new SystemServices(), new FixedMetricsMeasurer(), TimeProvider.System);
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
            host.Dispose(); // on the UI thread, after the loop (AgentHost's contract)
        }
        return exitCode;
    }

    /// <summary>Review Focus 4: the running Agent opens the Settings window; this one just leaves.</summary>
    private static async Task AskFirstInstanceAsync(string pipeName)
    {
        try
        {
            await using var client = new IpcClient(pipeName);
            if (await client.ConnectAsync(FirstInstanceTimeout)) await client.SendAsync(new ShowSettingsRequest());
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // The first instance is starting or exiting: nothing to ask.
        }
    }
}
