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
