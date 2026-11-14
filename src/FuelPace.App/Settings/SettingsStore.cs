using System.Text.Json;
using FuelPace.Core.Storage;

namespace FuelPace.App.Settings;

/// <summary>What one <see cref="SettingsStore.Load"/> did to the file, for the log.</summary>
/// <param name="MigratedFrom">The schema version it upgraded from (and wrote back); null when it did not migrate.</param>
/// <param name="WasCorrupt">It could not parse the file, renamed it to <c>.corrupt</c> and returned the defaults.</param>
public sealed record SettingsLoadReport(int? MigratedFrom, bool WasCorrupt)
{
    public static SettingsLoadReport Of(SettingsStore store) => new(store.LastLoadMigratedFrom, store.LastLoadWasCorrupt);
}

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

    /// <summary>The <c>schemaVersion</c> the last <see cref="Load"/> upgraded from (and wrote back as the current one); null when it did not migrate.</summary>
    public int? LastLoadMigratedFrom { get; private set; }

    /// <summary>True when the last <see cref="Load"/> found a file it could not parse, renamed it to <c>.corrupt</c> and returned the defaults.</summary>
    public bool LastLoadWasCorrupt { get; private set; }

    /// <summary>
    /// An absent or unreadable file (missing, locked by another process, access denied) gives the defaults and is left
    /// alone; a corrupt file is renamed to <c>.corrupt</c> and the defaults are returned. A locked file is retried for
    /// about 1.5 s unless <paramref name="retryLocked"/> is false (one attempt: a cheap re-check after a fallback).
    /// </summary>
    public AppSettings Load(bool retryLocked = true)
    {
        string text;
        LastLoadWasFallback = false;
        LastLoadMigratedFrom = null;
        LastLoadWasCorrupt = false;
        // Checked first: the read retries a missing file like a locked one (1.5 s).
        if (!File.Exists(_path) && !Directory.Exists(_path)) return new AppSettings().Normalize();
        try { text = retryLocked ? AtomicFile.ReadAllText(_path) : ReadOnce(_path); }
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
            if (s is not null)
            {
                // Peeked from the text: a missing schemaVersion deserializes as the current one, which would hide a v1 file.
                int version = PeekSchemaVersion(text);
                s = s.Normalize();
                if (version < AppSettings.CurrentSchemaVersion)
                {
                    s = s with { SchemaVersion = AppSettings.CurrentSchemaVersion };
                    LastLoadMigratedFrom = version;
                    // Only a file that was read fine gets here, so nothing unread is overwritten. If the write fails the
                    // settings are still right in memory and the next load migrates again.
                    try { Save(s); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                return s;
            }
        }
        catch (JsonException) { }

        LastLoadWasCorrupt = true;
        Quarantine();
        return new AppSettings().Normalize();
    }

    /// <summary>The top-level <c>schemaVersion</c>; 1 when absent, not a number or below 1 (every file before v3).</summary>
    static int PeekSchemaVersion(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return doc.RootElement is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("schemaVersion", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n >= 1 ? n : 1;
    }

    static string ReadOnce(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
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
