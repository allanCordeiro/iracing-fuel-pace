using System.Text.Json;
using FuelPace.Core.Storage;

namespace FuelPace.App.Settings;

/// <summary>Reads and writes <c>&lt;rootDir&gt;/settings.json</c>. Only the Agent writes it.</summary>
public sealed class SettingsStore(string rootDir)
{
    readonly string _path = Path.Combine(rootDir, "settings.json");

    /// <summary>
    /// True when the last <see cref="Load"/> could not read an existing file (locked by another process, access denied) and
    /// returned the defaults. The user's settings are still on disk: do not <see cref="Save"/> over them until a later load
    /// succeeds or the user explicitly changes something.
    /// </summary>
    public bool LastLoadWasFallback { get; private set; }

    /// <summary>
    /// An absent or unreadable file (missing, locked by another process, access denied) gives the defaults and is left
    /// alone; a corrupt file is renamed to <c>.corrupt</c> and the defaults are returned.
    /// </summary>
    public AppSettings Load()
    {
        string text;
        LastLoadWasFallback = false;
        // Checked first: the read retries a missing file like a locked one (1.5 s).
        if (!File.Exists(_path) && !Directory.Exists(_path)) return new AppSettings().Normalize();
        try { text = AtomicFile.ReadAllText(_path); }
        catch (FileNotFoundException) { return new AppSettings().Normalize(); }
        catch (DirectoryNotFoundException) { return new AppSettings().Normalize(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastLoadWasFallback = true;
            return new AppSettings().Normalize();
        }

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
