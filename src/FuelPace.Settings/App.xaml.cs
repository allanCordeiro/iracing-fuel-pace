using System.Windows;
using FuelPace.App.Ipc;
using FuelPace.App.SettingsUi;
using FuelPace.App.SettingsUi.ViewModels;

namespace FuelPace.Settings;

/// <summary>
/// The Settings process: one window per session (<see cref="SettingsInstance"/>), linked to the Agent by its named pipe. It
/// runs only while the window is open: closing the window ends the process (PERF-07).
/// </summary>
public partial class App : Application
{
    /// <summary>The Agent's pipe (<c>AgentOptions.PipeName</c>'s default).</summary>
    public const string PipeName = "FuelPace";

    SettingsInstance? _instance;
    IpcClient? _client;
    ShellViewModel? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Queued on the dispatcher, so a request that arrives before the window exists is served once it does.
        _instance = SettingsInstance.TryAcquire(() => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()));
        if (_instance is null)
        {
            Shutdown(0); // the open window was asked to come forward
            return;
        }

        _client = new IpcClient(PipeName);
        var connection = new IpcAgentConnection(_client, AgentLauncher.Launch);
        _shell = new ShellViewModel(connection, new DispatcherContext(Dispatcher), new TimerDelayScheduler(), new ShellServices(),
            new WpfTextMeasurer(EmbeddedFonts.Shared));
        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();
        _ = _shell.ConnectAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        _instance?.Dispose();
        // The pipe closes on a pool thread; the UI thread only gives it a moment (the process ends right after, and Windows
        // closes the handle anyway), so nothing keeps the process alive once the window has closed (PERF-07).
        if (_client is { } client)
        {
            var closing = Task.Run(async () => await client.DisposeAsync().ConfigureAwait(false));
            closing.Wait(TimeSpan.FromMilliseconds(300));
        }
        base.OnExit(e);
    }
}
