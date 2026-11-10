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
        Assert.True(s.Overlay.ShowInsightLine);
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
                ContentOpacity = 0.8, ShowPaceLine = true, ShowInsightLine = false, AutoHide = false,
                RenderBackend = RenderBackend.Layered,
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
        Assert.Contains("\"schemaVersion\": 1", text);
        Assert.Contains("\"meanWindow\": 5", text);
    }

    [Fact]
    public void Save_creates_missing_directory()
    {
        var nested = Path.Combine(_dir, "a", "b");
        new SettingsStore(nested).Save(new AppSettings());
        Assert.True(File.Exists(Path.Combine(nested, "settings.json")));
    }
}
