using FuelPace.App.Settings;
using FuelPace.Core.Strategy;

namespace FuelPace.App.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "fuelpace-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(1.0, s.Overlay.Scale);
        Assert.Equal(0.6, s.Overlay.BackgroundOpacity);
        Assert.Equal(1.0, s.Overlay.ContentOpacity);
        Assert.True(s.Overlay.ShowDeltaBar);
        Assert.Equal(0.20, s.Overlay.DeltaBarScaleLpl);
        Assert.Equal(RedComplement.Pct, s.Overlay.RedComplement);
        Assert.True(s.Overlay.ShowAvgInSupport);
        Assert.Equal(2, s.SchemaVersion);
        Assert.False(s.Overlay.ShowPaceLine);
        Assert.True(s.Overlay.AutoHide);
        Assert.Equal("Ctrl+Shift+F", s.Hotkeys.ToggleEdit);
        Assert.Equal("Ctrl+Shift+H", s.Hotkeys.ToggleVisible);
        Assert.Equal("Ctrl+Shift+M", s.Hotkeys.DebugMark);
        Assert.Equal(0.5, s.Strategy.ReserveL);
        Assert.Equal(RenderBackend.Auto, s.Overlay.RenderBackend);
    }

    [Fact]
    public void Round_trip_preserves_values()
    {
        var original = new AppSettings
        {
            Overlay = new OverlaySettings
            {
                MonitorDeviceName = @"\\.\DISPLAY2", OffsetX = 12, OffsetY = 34, Scale = 1.5, BackgroundOpacity = 0.25,
                ContentOpacity = 0.8, ShowPaceLine = true, AutoHide = false,
                RenderBackend = RenderBackend.Layered, ShowDeltaBar = false, DeltaBarScaleLpl = 0.35,
                RedComplement = RedComplement.Seconds, ShowAvgInSupport = false,
            },
            Strategy = new StrategyConfig { ReserveL = 1.2, MeanWindow = 8, MaxSavingFraction = 0.04, ExtraLaps = 1, Mode = LapsMode.Stint, StintLaps = 30, PracticeLapsFromTime = false },
            Hotkeys = new HotkeySettings { ToggleEdit = "Alt+F9", ToggleVisible = "Ctrl+Alt+H", DebugMark = "Ctrl+Shift+1" },
            Debug = new DebugSettings { Enabled = true, RecordTelemetry = false, MaxRecordingsGB = 4 },
            StartWithWindows = true,
        };
        var store = new SettingsStore(_dir);
        store.Save(original);
        Assert.Equal(original, new SettingsStore(_dir).Load());
    }

    [Fact]
    public void Normalize_clamps_ranges()
    {
        var hi = new AppSettings { Overlay = new OverlaySettings { Scale = 3, BackgroundOpacity = 2, ContentOpacity = 5 } }.Normalize();
        Assert.Equal(2.5, hi.Overlay.Scale);
        Assert.Equal(1.0, hi.Overlay.BackgroundOpacity);
        Assert.Equal(1.0, hi.Overlay.ContentOpacity);
        var lo = new AppSettings { Overlay = new OverlaySettings { Scale = 0.1, BackgroundOpacity = -1, ContentOpacity = 0.1 } }.Normalize();
        Assert.Equal(0.5, lo.Overlay.Scale);
        Assert.Equal(0.0, lo.Overlay.BackgroundOpacity);
        Assert.Equal(0.3, lo.Overlay.ContentOpacity);
    }

    [Fact]
    public void Invalid_hotkey_falls_back_to_default()
    {
        var s = new AppSettings { Hotkeys = new HotkeySettings { ToggleEdit = "F", ToggleVisible = "garbage", DebugMark = "Alt+F9" } }.Normalize();
        Assert.Equal("Ctrl+Shift+F", s.Hotkeys.ToggleEdit);
        Assert.Equal("Ctrl+Shift+H", s.Hotkeys.ToggleVisible);
        Assert.Equal("Alt+F9", s.Hotkeys.DebugMark);
    }

    [Fact]
    public void Invalid_hotkey_in_file_falls_back_on_load()
    {
        File.WriteAllText(SettingsPath, """{"hotkeys":{"toggleEdit":"nope"}}""");
        Assert.Equal("Ctrl+Shift+F", new SettingsStore(_dir).Load().Hotkeys.ToggleEdit);
    }

    [Fact]
    public void Corrupt_settings_are_quarantined()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(new AppSettings().Normalize(), s);
        Assert.False(File.Exists(SettingsPath));
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Json_null_object_is_quarantined()
    {
        File.WriteAllText(SettingsPath, "null");
        Assert.Equal(new AppSettings().Normalize(), new SettingsStore(_dir).Load());
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Locked_file_gives_defaults_and_is_not_quarantined()
    {
        // FileShare.None makes the reader's open fail with IOException (a sharing violation on Windows, an flock
        // conflict on Unix), like an antivirus holding the file at startup.
        File.WriteAllText(SettingsPath, """{"overlay":{"scale":2}}""");
        var store = new SettingsStore(_dir);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(new AppSettings().Normalize(), store.Load());
            Assert.True(store.LastLoadWasFallback);
        }
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
        Assert.Equal(2.0, store.Load().Overlay.Scale);
        Assert.False(store.LastLoadWasFallback);
    }

    [Fact]
    public void Only_an_unreadable_file_counts_as_a_fallback_load()
    {
        var store = new SettingsStore(_dir);
        store.Load(); // missing
        Assert.False(store.LastLoadWasFallback);
        File.WriteAllText(SettingsPath, "{ not json");
        store.Load(); // corrupt: quarantined, nothing of the user's is left to protect
        Assert.False(store.LastLoadWasFallback);
    }

    [Fact]
    public void Unreadable_path_gives_defaults_and_is_not_quarantined()
    {
        // Opening a directory as a file throws UnauthorizedAccessException on every OS.
        Directory.CreateDirectory(SettingsPath);
        var store = new SettingsStore(_dir);
        Assert.Equal(new AppSettings().Normalize(), store.Load());
        Assert.True(store.LastLoadWasFallback);
        Assert.True(Directory.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
        Assert.False(Directory.Exists(SettingsPath + ".corrupt"));
    }

    [Theory] // a typo in one enum must not wipe the user's position and scale (the guide tells them to hand-edit renderBackend)
    [InlineData("\"layerd\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    public void A_bad_enum_value_falls_back_for_that_property_only(string backend)
    {
        File.WriteAllText(SettingsPath, """{"overlay":{"scale":1.7,"offsetX":321,"renderBackend":""" + backend + """},"strategy":{"mode":"nope","reserveL":1.5}}""");
        var store = new SettingsStore(_dir);
        var s = store.Load();
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(1.7, s.Overlay.Scale);
        Assert.Equal(321, s.Overlay.OffsetX);
        Assert.Equal(RenderBackend.Auto, s.Overlay.RenderBackend);
        Assert.Equal(LapsMode.Auto, s.Strategy.Mode);
        Assert.Equal(1.5, s.Strategy.ReserveL);
    }

    [Theory]
    [InlineData("\"layered\"", RenderBackend.Layered)]
    [InlineData("\"Layered\"", RenderBackend.Layered)]
    [InlineData("\"COMPOSITION\"", RenderBackend.Composition)]
    [InlineData("\"auto\"", RenderBackend.Auto)]
    public void Enum_values_are_read_ignoring_case(string backend, RenderBackend expected)
    {
        File.WriteAllText(SettingsPath, """{"overlay":{"renderBackend":""" + backend + "}}");
        Assert.Equal(expected, new SettingsStore(_dir).Load().Overlay.RenderBackend);
    }

    [Fact]
    public void Enums_are_still_written_in_camel_case()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { Overlay = new OverlaySettings { RenderBackend = RenderBackend.Layered } });
        Assert.Contains("\"renderBackend\": \"layered\"", File.ReadAllText(SettingsPath));
        Assert.Contains("\"mode\": \"auto\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Out_of_range_strategy_value_is_clamped_not_fatal()
    {
        File.WriteAllText(SettingsPath, """{"strategy":{"meanWindow":99,"reserveL":-3}}""");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(16, s.Strategy.MeanWindow);
        Assert.Equal(0.0, s.Strategy.ReserveL);
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
    }

    [Fact]
    public void Partial_file_keeps_defaults_for_missing_fields()
    {
        File.WriteAllText(SettingsPath, """{"overlay":{"scale":2}}""");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(2.0, s.Overlay.Scale);
        Assert.Equal(0.6, s.Overlay.BackgroundOpacity);
        Assert.Equal(5, s.Strategy.MeanWindow);
    }

    [Fact]
    public void Json_uses_camel_case_and_string_enums()
    {
        new SettingsStore(_dir).Save(new AppSettings());
        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"renderBackend\": \"auto\"", text);
        Assert.Contains("\"mode\": \"auto\"", text);
        Assert.Contains("\"schemaVersion\": 2", text);
        Assert.Contains("\"meanWindow\": 5", text);
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        var nested = Path.Combine(_dir, "a", "b");
        new SettingsStore(nested).Save(new AppSettings());
        Assert.True(File.Exists(Path.Combine(nested, "settings.json")));
    }

    // ---- PRD v3 §9 / V3-9: schema 2 and the migration

    static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FuelPace.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir.FullName, .. parts]);
    }

    [Fact] // V3-9
    public void V3_9_real_v1_file_migrates_to_v2_preserving_the_pilots_settings()
    {
        File.Copy(RepoFile("tests", "fixtures", "settings", "v1-real.json"), SettingsPath);
        var store = new SettingsStore(_dir);
        var s = store.Load();

        Assert.Equal(1, store.LastLoadMigratedFrom);
        Assert.False(store.LastLoadWasCorrupt);
        Assert.False(store.LastLoadWasFallback);
        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal(@"\\.\DISPLAY1", s.Overlay.MonitorDeviceName);
        Assert.Equal(1804, s.Overlay.OffsetX);
        Assert.Equal(900, s.Overlay.OffsetY);
        Assert.Equal(1.66, s.Overlay.Scale);
        Assert.Equal(0.6, s.Overlay.BackgroundOpacity);
        Assert.Equal(1.0, s.Overlay.ContentOpacity);
        Assert.False(s.Overlay.AutoHide);
        Assert.False(s.Overlay.ShowPaceLine);
        Assert.Equal(new StrategyConfig(), s.Strategy);
        Assert.Equal("Ctrl+Shift+F", s.Hotkeys.ToggleEdit);
        Assert.Equal("Ctrl+Shift+H", s.Hotkeys.ToggleVisible);
        Assert.Equal("Ctrl+Shift+M", s.Hotkeys.DebugMark);
        Assert.True(s.Overlay.ShowDeltaBar);
        Assert.Equal(0.20, s.Overlay.DeltaBarScaleLpl);
        Assert.Equal(RedComplement.Pct, s.Overlay.RedComplement);
        Assert.True(s.Overlay.ShowAvgInSupport);

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"schemaVersion\": 2", text);
        Assert.Contains("\"showDeltaBar\": true", text);
        Assert.Contains("\"deltaBarScaleLpl\": 0.2", text);
        Assert.Contains("\"redComplement\": \"pct\"", text);
        Assert.Contains("\"showAvgInSupport\": true", text);
        Assert.DoesNotContain("showInsightLine", text);
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
        Assert.Equal(s, new SettingsStore(_dir).Load()); // the rewritten file reads back the same, with no new migration
    }

    [Fact]
    public void A_v2_file_is_not_migrated_or_rewritten()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { Overlay = new OverlaySettings { Scale = 1.3 } });
        var before = File.GetLastWriteTimeUtc(SettingsPath);
        File.SetLastWriteTimeUtc(SettingsPath, before.AddMinutes(-5));
        var stamp = File.GetLastWriteTimeUtc(SettingsPath);
        Assert.Equal(1.3, store.Load().Overlay.Scale);
        Assert.Null(store.LastLoadMigratedFrom);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(SettingsPath));
    }

    [Theory] // no schemaVersion at all is a v1 (the app always wrote one, but a hand-made file may not have it)
    [InlineData("""{"overlay":{"scale":1.4}}""")]
    [InlineData("""{"schemaVersion":0,"overlay":{"scale":1.4}}""")]
    public void A_file_without_a_usable_schema_version_is_migrated(string json)
    {
        File.WriteAllText(SettingsPath, json);
        var store = new SettingsStore(_dir);
        Assert.Equal(1.4, store.Load().Overlay.Scale);
        Assert.NotNull(store.LastLoadMigratedFrom);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_newer_schema_is_read_as_is_and_never_rewritten()
    {
        const string json = """{"schemaVersion":3,"overlay":{"scale":1.4,"futureField":7},"future":{"x":1}}""";
        File.WriteAllText(SettingsPath, json);
        var store = new SettingsStore(_dir);
        var s = store.Load();
        Assert.Equal(1.4, s.Overlay.Scale);
        Assert.Equal(3, s.SchemaVersion);
        Assert.Null(store.LastLoadMigratedFrom);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void A_file_that_could_not_be_read_is_never_written_over()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion":1,"overlay":{"scale":2}}""");
        var store = new SettingsStore(_dir);
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store.Load(retryLocked: false);
            Assert.True(store.LastLoadWasFallback);
            Assert.Null(store.LastLoadMigratedFrom);
        }
        Assert.Contains("\"schemaVersion\":1", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Corruption_is_reported_and_migration_status_is_reset_on_every_load()
    {
        var store = new SettingsStore(_dir);
        File.WriteAllText(SettingsPath, "{ not json");
        store.Load();
        Assert.True(store.LastLoadWasCorrupt);
        Assert.Null(store.LastLoadMigratedFrom);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
        store.Load(); // now missing
        Assert.False(store.LastLoadWasCorrupt);
    }

    [Fact]
    public void A_showInsightLine_in_an_old_file_is_ignored()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion":2,"overlay":{"showInsightLine":false,"scale":1.2}}""");
        Assert.Equal(1.2, new SettingsStore(_dir).Load().Overlay.Scale);
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
    }

    [Theory]
    [InlineData(0.01, 0.05)]
    [InlineData(0.9, 0.50)]
    [InlineData(0.2349, 0.23)]
    [InlineData(double.NaN, 0.20)]
    [InlineData(double.PositiveInfinity, 0.50)]
    public void Delta_bar_scale_is_clamped_and_rounded_to_hundredths(double input, double expected)
        => Assert.Equal(expected, new OverlaySettings { DeltaBarScaleLpl = input }.Normalize().DeltaBarScaleLpl);

    [Theory]
    [InlineData("\"seconds\"", RedComplement.Seconds)]
    [InlineData("\"Pct\"", RedComplement.Pct)]
    [InlineData("\"percent\"", RedComplement.Pct)]
    [InlineData("3", RedComplement.Pct)]
    public void Red_complement_is_read_leniently_and_undefined_becomes_pct(string json, RedComplement expected)
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion":2,"overlay":{"redComplement":""" + json + "}}");
        Assert.Equal(expected, new SettingsStore(_dir).Load().Overlay.RedComplement);
    }

    [Fact]
    public void A_failed_migration_write_keeps_the_original_file_and_returns_the_migrated_settings()
    {
        var original = File.ReadAllBytes(RepoFile("tests", "fixtures", "settings", "v1-real.json"));
        File.WriteAllBytes(SettingsPath, original);
        // A directory where the atomic write puts its temp file: the write fails with UnauthorizedAccessException on Linux and Windows alike.
        Directory.CreateDirectory(SettingsPath + ".tmp");
        var store = new SettingsStore(_dir);

        var s = store.Load();

        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal(1, store.LastLoadMigratedFrom);
        Assert.Equal(1804, s.Overlay.OffsetX);
        Assert.Equal(1.66, s.Overlay.Scale);
        Assert.True(s.Overlay.ShowDeltaBar);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
    }
}
