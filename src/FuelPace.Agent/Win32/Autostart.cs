using Microsoft.Win32;

namespace FuelPace.Agent.Win32;

/// <summary>
/// "Iniciar com o Windows": the value <c>FuelPace</c> under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> holding the
/// Agent's path in quotes (the folder may have spaces). Turning it off deletes the value. The key path is a parameter for the
/// tests only.
/// </summary>
public sealed class Autostart(string keyPath = Autostart.RunKey, string valueName = Autostart.ValueName)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "FuelPace";

    /// <summary>Writes or deletes the value. <paramref name="exePath"/> defaults to this process's executable.</summary>
    public void Set(bool on, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (on) key.SetValue(valueName, Command(exePath ?? Environment.ProcessPath!), RegistryValueKind.String);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>The registered command, or null.</summary>
    public string? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    public static string Command(string exePath) => "\"" + exePath + "\"";
}
