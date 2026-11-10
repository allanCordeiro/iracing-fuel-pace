using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using FuelPace.App.SettingsUi;

namespace FuelPace.Settings;

/// <summary>View-model state changes run on the WPF dispatcher.</summary>
public sealed class DispatcherContext(Dispatcher dispatcher) : ISynchronizationContext
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

/// <summary>Explorer for folders; the logs live in <c>%APPDATA%\FuelPace\logs</c> (Decision 3).</summary>
public sealed class ShellServices : IShellServices
{
    public string LogsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuelPace", "logs");

    public void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception) { }
    }
}

/// <summary>Starts <c>FuelPace.Agent.exe</c> from the folder this window runs from (they ship together, Decision 6).</summary>
public static class AgentLauncher
{
    public const string AgentExe = "FuelPace.Agent.exe";

    public static void Launch()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, AgentExe);
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            // Missing or blocked: the banner stays and the window keeps trying to connect.
        }
    }
}
