using System.Text;
using System.Text.Json;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;

namespace FuelPace.Core.Storage;

/// <summary>Reads and writes <c>{rootDir}/profiles/{car}__{track}.json</c>. Call from a background thread, never the telemetry thread.</summary>
public sealed class ProfileStore
{
    private static readonly string[] Reserved =
        ["con", "prn", "aux", "nul",
         "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
         "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    private readonly string _dir;
    private readonly DiagLog _log;

    public ProfileStore(string rootDir, DiagLog? log = null)
    {
        _dir = Path.Combine(rootDir, "profiles");
        _log = log ?? DiagLog.Null;
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FuelPace");

    /// <summary>Windows file-name rules applied on every OS: lower case, invalid chars and spaces to '_', no trailing '.', reserved device names prefixed.</summary>
    public static string FileNameFor(string carPath, string trackName) =>
        Sanitize(carPath) + "__" + Sanitize(trackName) + ".json";

    public string PathFor(string carPath, string trackName) => Path.Combine(_dir, FileNameFor(carPath, trackName));

    public ProfileDocument? Load(string carPath, string trackName)
    {
        var path = PathFor(carPath, trackName);
        if (!File.Exists(path)) return null;
        try
        {
            var doc = JsonSerializer.Deserialize(File.ReadAllText(path), ProfileJsonContext.Default.ProfileDocument);
            if (doc is null) throw new JsonException("document is null");
            return doc;
        }
        catch (JsonException e)
        {
            Quarantine(path, e);
            return null;
        }
    }

    public void Save(ProfileDocument doc)
    {
        var path = PathFor(doc.Car.Path, doc.Track.Name);
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument));
            _log.Emit(DiagLevel.Info, DiagEvent.ProfileSaved, 0, doc.Captures.Count, CountValid(doc));
            _log.Text(DiagLevel.Info, "storage", "saved " + path);
        }
        catch (Exception e)
        {
            _log.Emit(DiagLevel.Error, DiagEvent.ProfileSaveFailed, 0, doc.Captures.Count, CountValid(doc));
            _log.Text(DiagLevel.Error, "storage", $"save failed {path}: {e.Message}");
            throw;
        }
    }

    private void Quarantine(string path, Exception cause)
    {
        _log.Emit(DiagLevel.Warn, DiagEvent.ProfileQuarantined, 0);
        _log.Text(DiagLevel.Warn, "storage", $"corrupt profile {path}: {cause.Message}");
        File.Move(path, path + ".corrupt", overwrite: true);
    }

    private static int CountValid(ProfileDocument doc)
    {
        int n = 0;
        foreach (var c in doc.Captures)
            foreach (var l in c.Laps)
                if (l.Valid) n++;
        return n;
    }

    private static string Sanitize(string part)
    {
        var sb = new StringBuilder(part.Length + 1);
        foreach (var ch in part.ToLowerInvariant())
            sb.Append(ch == ' ' || ch < ' ' || "<>:\"/\\|?*".Contains(ch) ? '_' : ch);
        if (sb.Length > 0 && sb[^1] == '.') sb[^1] = '_';
        var s = sb.ToString();
        int dot = s.IndexOf('.');
        if (Array.IndexOf(Reserved, dot < 0 ? s : s[..dot]) >= 0) s = "_" + s;
        return s;
    }
}
