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
        _instance = SettingsInstance.TryAcquire();
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
        _instance.ShowRequested += () => Dispatcher.BeginInvoke(window.BringToFront);
        window.Show();
        _ = _shell.ConnectAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shell?.Dispose();
        // Bounded: the pipe's read loop ends at once on dispose; nothing may keep the process alive after the window closed.
        _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _instance?.Dispose();
        base.OnExit(e);
    }
}
