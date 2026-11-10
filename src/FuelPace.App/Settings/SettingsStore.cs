using System.Text.Json;
using FuelPace.Core.Storage;

namespace FuelPace.App.Settings;

/// <summary>Reads and writes <c>&lt;rootDir&gt;/settings.json</c>. Only the Agent writes it.</summary>
public sealed class SettingsStore(string rootDir)
{
    readonly string _path = Path.Combine(rootDir, "settings.json");

    /// <summary>
    /// An absent or unreadable file (missing, locked by another process, access denied) gives the defaults and is left
    /// alone; a corrupt file is renamed to <c>.corrupt</c> and the defaults are returned.
    /// </summary>
    public AppSettings Load()
    {
        string text;
        try { text = AtomicFile.ReadAllText(_path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new AppSettings().Normalize(); }

        try
        {
            var s = JsonSerializer.Deserialize(text, AppSettingsJsonContext.Default.AppSettings);
            if (s is not null) return s.Normalize();
        }
        catch (JsonException) { }

        Quarantine();
        return new AppSettings().Normalize();
    }

    public void Save(AppSettings s)
        => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(s.Normalize(), AppSettingsJsonContext.Default.AppSettings));

    void Quarantine()
    {
        try { File.Move(_path, _path + ".corrupt", overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
