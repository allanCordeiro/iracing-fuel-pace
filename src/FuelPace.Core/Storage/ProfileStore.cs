using System.Text;
using System.Text.Json;
using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;

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

    /// <summary>Highest <see cref="ProfileDocument.SchemaVersion"/> this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Where a capture that could not be added to its profile is kept: <c>{car}__{track}.pending-{captureId}.json</c>, next
    /// to the profile, holding a profile document with only that capture. The next <see cref="AppendCapture"/> for the
    /// same profile merges every pending file and deletes it once the profile is saved.
    /// </summary>
    public string PendingPathFor(string carPath, string trackName, string captureId) =>
        Path.Combine(_dir, PendingPrefix(carPath, trackName) + Sanitize(captureId) + ".json");

    private static string PendingPrefix(string carPath, string trackName) =>
        Sanitize(carPath) + "__" + Sanitize(trackName) + ".pending-";

    /// <summary>
    /// The profile, or null when there is none. A file that is not valid JSON or misses a required value is renamed to
    /// <c>.corrupt</c> (Review Focus 2) and null is returned; if that rename fails the file stays and null is returned. A
    /// file with a newer <c>schemaVersion</c> is left untouched and null is returned. Read errors (file locked, access
    /// denied) are thrown.
    /// </summary>
    public ProfileDocument? Load(string carPath, string trackName)
    {
        TryLoad(PathFor(carPath, trackName), out var doc);
        return doc;
    }

    /// <summary>Writes the profile atomically. Refuses (throws) to overwrite a file with a newer <c>schemaVersion</c>; an
    /// existing file that is corrupt is quarantined first. Failures emit <see cref="DiagEvent.ProfileSaveFailed"/> and rethrow.</summary>
    public void Save(ProfileDocument doc)
    {
        var path = PathFor(doc.Car.Path, doc.Track.Name);
        try
        {
            Write(path, doc);
        }
        catch (Exception e)
        {
            SaveFailed(path, doc.Captures.Count, CountValid(doc), e);
            throw;
        }
    }

    /// <summary>
    /// I/O thread: adds <paramref name="rec"/> to the profile of <paramref name="info"/> (with any pending captures),
    /// recomputes the baseline and saves. Never throws and never loses the capture: on any failure it emits
    /// <see cref="DiagEvent.ProfileSaveFailed"/> and writes the capture to <see cref="PendingPathFor"/>. Returns the
    /// updated document, also when only the final write failed (it is still valid for this session), or null when the
    /// profile could not be read or belongs to a newer schema.
    /// </summary>
    public ProfileDocument? AppendCapture(SessionInfo info, CaptureRecord rec, DateTimeOffset now)
    {
        var path = PathFor(info.CarPath, info.TrackName);
        ProfileDocument? built = null;
        try
        {
            if (TryLoad(path, out var doc) == LoadResult.NewerSchema)
                throw new IOException($"{path} has a newer schemaVersion; captures are kept as pending files");
            doc ??= ProfileDocument.CreateFor(info);
            var merged = MergePending(doc, info);
            ProfileOperations.AddCapture(doc, rec, now);
            built = doc;
            Write(path, doc);
            foreach (var p in merged)
            {
                try { File.Delete(p); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Text(DiagLevel.Warn, "storage", $"merged pending capture not deleted {p}: {e.Message}");
                }
            }
            return doc;
        }
        catch (Exception e)
        {
            int valid = 0;
            foreach (var l in rec.Laps)
                if (l.Valid) valid++;
            SaveFailed(path, built?.Captures.Count ?? 1, built is null ? valid : CountValid(built), e);
            WritePending(info, rec);
            return built;
        }
    }

    private enum LoadResult { Missing, Loaded, Corrupt, NewerSchema }

    private LoadResult TryLoad(string path, out ProfileDocument? doc)
    {
        doc = null;
        if (!File.Exists(path)) return LoadResult.Missing;
        var text = File.ReadAllText(path);
        try
        {
            int schema = PeekSchemaVersion(text);
            if (schema > CurrentSchemaVersion)
            {
                _log.Text(DiagLevel.Warn, "storage",
                    $"profile {path} has schemaVersion {schema} (this version reads {CurrentSchemaVersion}); not loaded, left untouched");
                return LoadResult.NewerSchema;
            }
            var loaded = JsonSerializer.Deserialize(text, ProfileJsonContext.Default.ProfileDocument)
                ?? throw new JsonException("document is null");
            Validate(loaded);
            doc = loaded;
            _log.Text(DiagLevel.Debug, "storage", "loaded " + path);
            return LoadResult.Loaded;
        }
        catch (JsonException e)
        {
            Quarantine(path, e);
            return LoadResult.Corrupt;
        }
    }

    /// <summary>The top-level <c>schemaVersion</c> (1 when absent), read before binding so a newer layout is never
    /// mistaken for corruption. Throws <see cref="JsonException"/> when the text is not a JSON object.</summary>
    private static int PeekSchemaVersion(string text)
    {
        using var json = JsonDocument.Parse(text);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("profile is not a JSON object");
        return json.RootElement.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n : CurrentSchemaVersion;
    }

    /// <summary><c>required</c> only checks presence; a <c>null</c> where the code dereferences is corruption too.</summary>
    private static void Validate(ProfileDocument doc)
    {
        if (doc.Car?.Path is null) throw new JsonException("car or car.path is missing");
        if (doc.Track?.Name is null) throw new JsonException("track or track.name is missing");
        if (doc.Captures is null) throw new JsonException("captures is missing");
        foreach (var c in doc.Captures)
        {
            if (c is null || c.Id is null || c.SessionType is null || c.Conditions is null || c.Laps is null)
                throw new JsonException("a capture is missing id, sessionType, conditions or laps");
            foreach (var l in c.Laps)
                if (l is null) throw new JsonException($"capture {c.Id} has a null lap");
        }
        if (doc.Baseline is { } b && (b.FuelProfile is null || b.Conditions is null || b.Conditions.Contains(null!)))
            throw new JsonException("baseline is missing fuelProfile or conditions");
    }

    private void Write(string path, ProfileDocument doc)
    {
        if (File.Exists(path))
        {
            int schema;
            try { schema = PeekSchemaVersion(File.ReadAllText(path)); }
            catch (JsonException e)
            {
                if (!Quarantine(path, e)) throw new IOException($"corrupt profile {path} could not be quarantined; not overwritten", e);
                schema = CurrentSchemaVersion;
            }
            if (schema > CurrentSchemaVersion)
                throw new IOException($"{path} has schemaVersion {schema}; a newer FuelPace wrote it, not overwritten");
        }
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument));
        _log.Emit(DiagLevel.Info, DiagEvent.ProfileSaved, 0, doc.Captures.Count, CountValid(doc));
        _log.Text(DiagLevel.Info, "storage", "saved " + path);
    }

    private void SaveFailed(string path, int captures, int validLaps, Exception e)
    {
        _log.Emit(DiagLevel.Error, DiagEvent.ProfileSaveFailed, 0, captures, validLaps);
        _log.Text(DiagLevel.Error, "storage", $"save failed {path}: {e.Message}");
    }

    /// <summary>Adds the captures of every readable pending file (skipping ids already present); returns those files.</summary>
    private List<string> MergePending(ProfileDocument doc, SessionInfo info)
    {
        var merged = new List<string>();
        if (!Directory.Exists(_dir)) return merged;
        foreach (var file in Directory.GetFiles(_dir, PendingPrefix(info.CarPath, info.TrackName) + "*.json"))
        {
            if (TryLoad(file, out var pending) != LoadResult.Loaded) continue; // corrupt: quarantined; newer: left alone
            foreach (var c in pending!.Captures)
                if (!doc.Captures.Exists(x => x.Id == c.Id)) doc.Captures.Add(c);
            merged.Add(file);
        }
        return merged;
    }

    private void WritePending(SessionInfo info, CaptureRecord rec)
    {
        var path = PendingPathFor(info.CarPath, info.TrackName, rec.Id);
        try
        {
            var doc = ProfileDocument.CreateFor(info);
            doc.Captures.Add(rec);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(doc, ProfileJsonContext.Default.ProfileDocument));
            _log.Text(DiagLevel.Warn, "storage", $"capture {rec.Id} kept in {path}; merged by the next save of this profile");
        }
        catch (Exception e)
        {
            _log.Text(DiagLevel.Error, "storage", $"capture {rec.Id} LOST: pending file {path} not written: {e.Message}");
        }
    }

    /// <summary>Renames a corrupt file to <c>.corrupt</c>; false (logged) when the rename itself fails.</summary>
    private bool Quarantine(string path, Exception cause)
    {
        _log.Emit(DiagLevel.Warn, DiagEvent.ProfileQuarantined, 0);
        _log.Text(DiagLevel.Warn, "storage", $"corrupt profile {path}: {cause.Message}");
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Text(DiagLevel.Warn, "storage", $"corrupt profile {path} not quarantined: {e.Message}");
            return false;
        }
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
