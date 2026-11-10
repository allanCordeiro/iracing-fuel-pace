using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FuelPace.Core.Baselines;
using FuelPace.Core.Capture;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Storage;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.Tests.Storage;

public class ProfileStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "fuelpace-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    internal static ProfileDocument SampleDoc(bool withNaN = false)
    {
        var doc = new ProfileDocument
        {
            Car = new CarInfo("ligierjsp320", "Ligier JS P320", 100.5, 0.75),
            Track = new TrackInfo("zolder alt", "Circuit Zolder", "Alternate", 4.011),
        };
        var rec = new CaptureRecord
        {
            Id = "2026-10-29T20:03:40Z",
            SessionType = "Practice",
            Conditions = new CaptureConditions(30.5, 20, 1, -1.5, 12, 100.5),
            Laps =
            [
                new CapturedLap { Lap = 1, TimeS = 80.123, FuelL = 2.3812, FullThrottlePct = withNaN ? double.NaN : 64.5, Reasons = LapReasons.None, FuelProfile = Enumerable.Range(0, 101).Select(i => i / 100f).ToArray() },
                new CapturedLap { Lap = 2, TimeS = null, FuelL = 2.5, FullThrottlePct = 50, Reasons = LapReasons.OffTrack | LapReasons.Incident },
            ],
        };
        doc.Captures.Add(rec);
        doc.Baseline = new Baseline(T0, 5, 2.37, 2.38, 0.04, 2.28, 2.33, 2.4, 80.1, 80.0, 0.7, 79.1,
            withNaN ? double.NaN : 64.0, 42.1, 56.3, new FuelVsTime(200, -50), Confidence.Media,
            [0f, 0.5f, 1f], [new BopSignature(-1.5, 12, 100.5)]);
        return doc;
    }

    [Fact] public void File_name_replaces_spaces_and_invalid_chars() { Assert.Equal("ligierjsp320__zolder_alt.json", ProfileStore.FileNameFor("ligierjsp320", "zolder alt")); }

    [Theory]
    [InlineData("car", "a<b>c:d\"e/f\\g|h?i*j", "car__a_b_c_d_e_f_g_h_i_j.json")]
    [InlineData("CON", "Track.", "_con__track_.json")]
    [InlineData("Car", "Zolder Alt", "car__zolder_alt.json")]
    public void File_name_is_windows_safe(string car, string track, string expected) { Assert.Equal(expected, ProfileStore.FileNameFor(car, track)); }

    [Fact]
    public async Task Save_retries_when_target_is_locked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "rename over an open file only fails on Windows");
        var store = new ProfileStore(_root);
        var doc = SampleDoc();
        store.Save(doc);
        var path = store.PathFor(doc.Car.Path, doc.Track.Name);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150, TestContext.Current.CancellationToken); locked.Dispose(); }, TestContext.Current.CancellationToken);
        store.Save(doc);
        await release;
        Assert.NotNull(store.Load(doc.Car.Path, doc.Track.Name));
    }

    [Fact]
    public void Save_retries_a_locked_read_of_the_existing_file()
    {
        var plain = new ProfileStore(_root);
        var doc = SampleDoc();
        plain.Save(doc);
        int calls = 0;
        var store = new ProfileStore(_root, null, p =>
        {
            if (++calls <= 2) throw new IOException("locked");
            return AtomicFile.ReadOnce(p);
        });
        store.Save(doc);
        Assert.Equal(3, calls);
        Assert.NotNull(store.Load(doc.Car.Path, doc.Track.Name));
    }

    [Fact]
    public void Save_still_throws_when_the_existing_file_stays_locked()
    {
        var plain = new ProfileStore(_root);
        var doc = SampleDoc();
        plain.Save(doc);
        var store = new ProfileStore(_root, null, _ => throw new IOException("locked"));
        Assert.Throws<IOException>(() => store.Save(doc));
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new ProfileStore(_root);
        var doc = SampleDoc();
        store.Save(doc);
        var back = store.Load(doc.Car.Path, doc.Track.Name)!;
        Assert.Equal(1, back.SchemaVersion);
        Assert.Equal(doc.Car, back.Car);
        Assert.Equal(doc.Track, back.Track);
        var rec = Assert.Single(back.Captures);
        var src = doc.Captures[0];
        Assert.Equal(src.Id, rec.Id); Assert.Equal(src.Active, rec.Active); Assert.Equal(src.SessionType, rec.SessionType);
        Assert.Equal(src.Conditions, rec.Conditions);
        Assert.Equal(2, rec.Laps.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(src.Laps[i].Lap, rec.Laps[i].Lap);
            Assert.Equal(src.Laps[i].TimeS, rec.Laps[i].TimeS);
            Assert.Equal(src.Laps[i].FuelL, rec.Laps[i].FuelL);
            Assert.Equal(src.Laps[i].FullThrottlePct, rec.Laps[i].FullThrottlePct);
            Assert.Equal(src.Laps[i].Reasons, rec.Laps[i].Reasons);
            Assert.Equal(src.Laps[i].FuelProfile, rec.Laps[i].FuelProfile);
        }
        Assert.Equal(LapReasons.OffTrack | LapReasons.Incident, rec.Laps[1].Reasons);
        Assert.False(rec.Laps[1].Valid);
        var b = back.Baseline!; var e = doc.Baseline!;
        Assert.Equal(e.ComputedAt, b.ComputedAt); Assert.Equal(e.ValidLaps, b.ValidLaps);
        Assert.Equal(e.FuelMeanL, b.FuelMeanL); Assert.Equal(e.FuelP90L, b.FuelP90L);
        Assert.Equal(e.LapBestS, b.LapBestS); Assert.Equal(e.FullThrottleMeanPct, b.FullThrottleMeanPct);
        Assert.Equal(e.AutonomyLaps, b.AutonomyLaps); Assert.Equal(e.AutonomyMin, b.AutonomyMin);
        Assert.Equal(e.FuelVsTime, b.FuelVsTime); Assert.Equal(e.Confidence, b.Confidence);
        Assert.Equal(e.FuelProfile, b.FuelProfile); Assert.Equal(e.Conditions, b.Conditions);
    }

    [Fact]
    public void Nan_values_round_trip_as_null()
    {
        var store = new ProfileStore(_root);
        var doc = SampleDoc(withNaN: true);
        store.Save(doc);
        var text = File.ReadAllText(store.PathFor(doc.Car.Path, doc.Track.Name));
        Assert.Contains("\"fullThrottlePct\": null", text);
        Assert.Contains("\"fullThrottleMeanPct\": null", text);
        var back = store.Load(doc.Car.Path, doc.Track.Name)!;
        Assert.True(double.IsNaN(back.Captures[0].Laps[0].FullThrottlePct));
        Assert.True(double.IsNaN(back.Baseline!.FullThrottleMeanPct));
        Assert.Null(back.Captures[0].Laps[1].TimeS);
    }

    [Fact]
    public void Json_uses_prd_schema_names()
    {
        var store = new ProfileStore(_root);
        var doc = SampleDoc();
        store.Save(doc);
        var text = File.ReadAllText(store.PathFor(doc.Car.Path, doc.Track.Name));
        var flat = Regex.Replace(text, @"\s+", " ");
        Assert.Contains("\"schemaVersion\": 1", flat);
        foreach (var name in new[] { "tankCapacityL", "fuelKgPerL", "lengthKm", "sessionType", "fullThrottlePct" })
            Assert.Contains($"\"{name}\":", flat);
        Assert.Contains("\"reasons\": [ \"incident\", \"offtrack\" ]", flat);
        Assert.Contains("\"confidence\": \"media\"", flat);
        Assert.Contains("\"valid\": false", flat);
        Assert.Contains("\"valid\": true", flat);
    }

    [Fact]
    public void Valid_in_file_is_ignored_on_read()
    {
        var store = new ProfileStore(_root);
        var doc = SampleDoc();
        store.Save(doc);
        var path = store.PathFor(doc.Car.Path, doc.Track.Name);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"valid\": false", "\"valid\": true"));
        Assert.False(store.Load(doc.Car.Path, doc.Track.Name)!.Captures[0].Laps[1].Valid);
    }

    [Fact]
    public void Save_is_atomic_and_leaves_no_temp()
    {
        var store = new ProfileStore(_root);
        var doc = SampleDoc();
        store.Save(doc);
        doc.Captures[0].SessionType = "Race";
        store.Save(doc);
        var files = Directory.GetFiles(Path.GetDirectoryName(store.PathFor(doc.Car.Path, doc.Track.Name))!);
        var only = Assert.Single(files);
        Assert.EndsWith(".json", only);
        Assert.Equal("Race", store.Load(doc.Car.Path, doc.Track.Name)!.Captures[0].SessionType);
    }

    [Fact]
    public void Corrupt_file_is_quarantined_and_load_returns_null()
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var store = new ProfileStore(_root, log);
        var path = store.PathFor("car", "track");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ trunc");
        Assert.Null(store.Load("car", "track"));
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.False(File.Exists(path));
        Assert.Contains(Drain(log), r => r.Event == DiagEvent.ProfileQuarantined && r.Level == DiagLevel.Warn);

        // a second corrupt file overwrites the previous .corrupt
        File.WriteAllText(path, "{ again");
        Assert.Null(store.Load("car", "track"));
        Assert.Equal("{ again", File.ReadAllText(path + ".corrupt"));
    }

    // ---- I-3: semantic validation, quarantine robustness, newer schema ----

    /// <summary>Saves <see cref="SampleDoc"/>, then rewrites the file after <paramref name="edit"/> on its JSON tree.</summary>
    string WriteEdited(ProfileStore store, Action<JsonObject> edit)
    {
        var doc = SampleDoc();
        store.Save(doc);
        var path = store.PathFor(doc.Car.Path, doc.Track.Name);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    static JsonObject Capture0(JsonObject root) => root["captures"]![0]!.AsObject();

    public static TheoryData<string> NullFields => ["car", "track", "captures", "capture", "capture.id", "capture.laps", "capture.lap",
        "capture.conditions", "capture.sessionType", "car.path", "track.name", "baseline.conditions[0]"];

    [Theory]
    [MemberData(nameof(NullFields))]
    public void Null_required_field_is_quarantined_as_corrupt(string field)
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var store = new ProfileStore(_root, log);
        var path = WriteEdited(store, root =>
        {
            switch (field)
            {
                case "car": root["car"] = null; break;
                case "track": root["track"] = null; break;
                case "captures": root["captures"] = null; break;
                case "capture": root["captures"]![0] = null; break;
                case "capture.id": Capture0(root)["id"] = null; break;
                case "capture.laps": Capture0(root)["laps"] = null; break;
                case "capture.lap": Capture0(root)["laps"]![0] = null; break;
                case "capture.conditions": Capture0(root)["conditions"] = null; break;
                case "capture.sessionType": Capture0(root)["sessionType"] = null; break;
                case "car.path": root["car"]!["path"] = null; break;
                case "track.name": root["track"]!["name"] = null; break;
                case "baseline.conditions[0]": root["baseline"]!["conditions"]![0] = null; break;
            }
        });
        Drain(log);
        Assert.Null(store.Load("ligierjsp320", "zolder alt"));
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.False(File.Exists(path));
        Assert.Contains(Drain(log), r => r.Event == DiagEvent.ProfileQuarantined);
    }

    [Fact]
    public void Quarantine_failure_does_not_escape_Load()
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var store = new ProfileStore(_root, log);
        var path = store.PathFor("car", "track");
        Directory.CreateDirectory(path + ".corrupt"); // the move target is a directory: File.Move throws
        File.WriteAllText(path, "{ trunc");
        Assert.Null(store.Load("car", "track"));
        Assert.True(File.Exists(path)); // left in place, nothing overwritten
        bool logged = false;
        while (log.TryDequeueText(out var t)) logged |= t.Level == DiagLevel.Warn && t.Message.Contains("not quarantined");
        Assert.True(logged);
    }

    const string NewerSchemaJson =
        """{ "schemaVersion": 2, "car": { "path": "ligierjsp320", "name": "Ligier" }, "track": { "name": "zolder alt" }, "captures": { "v2": "shape" }, "future": [1, 2] }""";

    [Fact]
    public void Newer_schema_is_neither_loaded_nor_quarantined_nor_overwritten()
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var store = new ProfileStore(_root, log);
        var doc = SampleDoc();
        var path = store.PathFor(doc.Car.Path, doc.Track.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, NewerSchemaJson);

        Assert.Null(store.Load(doc.Car.Path, doc.Track.Name));
        Assert.Equal(NewerSchemaJson, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".corrupt"));
        bool warned = false;
        while (log.TryDequeueText(out var t)) warned |= t.Level == DiagLevel.Warn && t.Message.Contains("schemaVersion 2");
        Assert.True(warned);

        Assert.ThrowsAny<Exception>(() => store.Save(doc));
        Assert.Equal(NewerSchemaJson, File.ReadAllText(path));
        Assert.Contains(Drain(log), r => r.Event == DiagEvent.ProfileSaveFailed);
    }

    // ---- I-2a: AppendCapture never loses a capture ----

    static SessionInfo Info => new(0, "ligierjsp320", "Ligier JS P320", "Ligier", "zolder alt", "Zolder", "Circuit Zolder", "Alternate",
        4.011, 100.5, 0.75, 80, []);

    static CaptureRecord Rec(string id, int laps = 8)
    {
        var rec = new CaptureRecord { Id = id, SessionType = "Practice", Conditions = new CaptureConditions(30, 20, 1, 0, 0, 100.5) };
        for (int i = 1; i <= laps; i++)
            rec.Laps.Add(new CapturedLap { Lap = i, TimeS = 80 + 0.1 * i, FuelL = 2.4 + 0.01 * i, FullThrottlePct = 60, Reasons = LapReasons.None });
        return rec;
    }

    string[] PendingFiles() =>
        Directory.Exists(Path.Combine(_root, "profiles"))
            ? Directory.GetFiles(Path.Combine(_root, "profiles"), "*.pending-*.json") : [];

    [Fact]
    public void AppendCapture_creates_the_profile_with_a_baseline()
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var store = new ProfileStore(_root, log);
        var doc = store.AppendCapture(Info, Rec("2026-11-09T12:00:00Z"), T0);
        Assert.NotNull(doc);
        Assert.NotNull(doc.Baseline);
        var back = store.Load(Info.CarPath, Info.TrackName)!;
        Assert.Equal("2026-11-09T12:00:00Z", Assert.Single(back.Captures).Id);
        Assert.Empty(PendingFiles());
        Assert.Contains(Drain(log), r => r.Event == DiagEvent.ProfileSaved);
    }

    [Fact]
    public void AppendCapture_keeps_the_capture_in_a_pending_file_when_the_profile_cannot_be_read_and_merges_it_later()
    {
        var log = new DiagLog(DiagLevel.Trace, 256);
        var store = new ProfileStore(_root, log);
        store.AppendCapture(Info, Rec("2026-11-09T12:00:00Z"), T0);
        var path = store.PathFor(Info.CarPath, Info.TrackName);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // AV scanner / backup tool
            Assert.Null(store.AppendCapture(Info, Rec("2026-11-09T12:30:00Z"), T0));

        var pending = Assert.Single(PendingFiles());
        Assert.Equal(store.PendingPathFor(Info.CarPath, Info.TrackName, "2026-11-09T12:30:00Z"), pending);
        Assert.Single(store.Load(Info.CarPath, Info.TrackName)!.Captures); // the profile itself is untouched
        Assert.Contains(Drain(log), r => r.Event == DiagEvent.ProfileSaveFailed && r.Level == DiagLevel.Error);

        var doc = store.AppendCapture(Info, Rec("2026-11-09T13:00:00Z"), T0);
        Assert.NotNull(doc);
        Assert.Equal(["2026-11-09T12:00:00Z", "2026-11-09T12:30:00Z", "2026-11-09T13:00:00Z"],
            store.Load(Info.CarPath, Info.TrackName)!.Captures.Select(c => c.Id).Order());
        Assert.Equal(24, doc.Baseline!.ValidLaps);
        Assert.Empty(PendingFiles());
    }

    [Fact]
    public void AppendCapture_keeps_the_capture_when_the_save_fails_and_still_returns_the_document()
    {
        var store = new ProfileStore(_root, new DiagLog(DiagLevel.Trace, 256));
        var path = store.PathFor(Info.CarPath, Info.TrackName);
        Directory.CreateDirectory(path); // the rename onto the profile path fails
        var doc = store.AppendCapture(Info, Rec("2026-11-09T12:00:00Z"), T0);
        Assert.NotNull(doc?.Baseline); // usable in memory for this session
        Assert.Single(PendingFiles());

        Directory.Delete(path);
        store.AppendCapture(Info, Rec("2026-11-09T13:00:00Z"), T0);
        Assert.Equal(2, store.Load(Info.CarPath, Info.TrackName)!.Captures.Count);
        Assert.Empty(PendingFiles());
    }

    [Fact]
    public void AppendCapture_does_not_overwrite_a_newer_schema_file()
    {
        var store = new ProfileStore(_root, new DiagLog(DiagLevel.Trace, 256));
        var path = store.PathFor(Info.CarPath, Info.TrackName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, NewerSchemaJson);
        Assert.Null(store.AppendCapture(Info, Rec("2026-11-09T12:00:00Z"), T0));
        Assert.Equal(NewerSchemaJson, File.ReadAllText(path));
        Assert.Single(PendingFiles());
    }

    [Fact]
    public void AppendCapture_on_a_corrupt_profile_quarantines_it_and_saves()
    {
        var store = new ProfileStore(_root);
        var path = WriteEdited(store, root => root["captures"] = null);
        var doc = store.AppendCapture(Info, Rec("2026-11-09T12:00:00Z"), T0);
        Assert.NotNull(doc);
        Assert.True(File.Exists(path + ".corrupt"));
        Assert.Single(store.Load(Info.CarPath, Info.TrackName)!.Captures);
    }

    [Fact]
    public void A_corrupt_pending_file_is_quarantined_and_does_not_block_the_save()
    {
        var store = new ProfileStore(_root);
        var pending = store.PendingPathFor(Info.CarPath, Info.TrackName, "2026-11-09T11:00:00Z");
        Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.WriteAllText(pending, "{ trunc");
        Assert.NotNull(store.AppendCapture(Info, Rec("2026-11-09T12:00:00Z"), T0));
        Assert.Single(store.Load(Info.CarPath, Info.TrackName)!.Captures);
        Assert.True(File.Exists(pending + ".corrupt"));
        Assert.Empty(PendingFiles());
    }

    [Fact] public void Missing_file_returns_null() { Assert.Null(new ProfileStore(_root).Load("car", "nowhere")); }

    [Fact]
    public void Save_emits_ProfileSaved()
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var store = new ProfileStore(_root, log);
        store.Save(SampleDoc());
        var r = Assert.Single(Drain(log), x => x.Event == DiagEvent.ProfileSaved);
        Assert.Equal(DiagLevel.Info, r.Level);
        Assert.Equal(1, r.I0); Assert.Equal(1, r.I1);
    }

    [Fact]
    public void Failed_save_emits_ProfileSaveFailed_and_rethrows()
    {
        var log = new DiagLog(DiagLevel.Trace, 64);
        var blocker = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocker, "i am a file, not a directory");
        var store = new ProfileStore(_root, log);
        Assert.ThrowsAny<Exception>(() => store.Save(SampleDoc()));
        Assert.Contains(Drain(log), r => r.Event == DiagEvent.ProfileSaveFailed && r.Level == DiagLevel.Error);
        Assert.True(log.TryDequeueText(out var t));
        Assert.Contains(blocker, t.Message);
    }

    [Fact]
    public void CreateFor_copies_session_info()
    {
        var info = new SessionInfo(0, "ligierjsp320", "Ligier JS P320", "Ligier", "zolder alt", "Zolder", "Circuit Zolder", "Alternate", 4.011, 100.5, 0.75, 80, []);
        var doc = ProfileDocument.CreateFor(info);
        Assert.Equal(new CarInfo("ligierjsp320", "Ligier JS P320", 100.5, 0.75), doc.Car);
        Assert.Equal(new TrackInfo("zolder alt", "Circuit Zolder", "Alternate", 4.011), doc.Track);
        Assert.Empty(doc.Captures); Assert.Null(doc.Baseline);
    }

    static List<DiagRecord> Drain(DiagLog log)
    {
        var buf = new DiagRecord[64];
        int n = log.DrainRecords(buf);
        return buf.Take(n).ToList();
    }
}
