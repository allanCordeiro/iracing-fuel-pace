using System.Diagnostics;
using FuelPace.App.Host;

namespace FuelPace.Agent.Win32;

/// <summary>The host's <see cref="ISystemServices"/> on Windows. Thread-safe (no state of its own).</summary>
public sealed class SystemServices : ISystemServices
{
    public const string SettingsExe = "FuelPace.Settings.exe";

    private readonly Autostart _autostart = new();

    public IReadOnlyDictionary<string, string> CollectSystemInfo() => SystemInfoCollector.Collect();

    public bool IsFullscreenExclusiveActive() => FullscreenDetector.IsD3DFullscreenActive();

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    /// <summary>Starts the Settings window beside the Agent (the Settings app brings an open window forward itself).</summary>
    public void LaunchSettings()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, SettingsExe);
        if (!File.Exists(exe)) throw new FileNotFoundException(SettingsExe + " is not beside the Agent", SettingsExe);
        using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
    }

    public void SetAutostart(bool on) => _autostart.Set(on);

    public string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
}
