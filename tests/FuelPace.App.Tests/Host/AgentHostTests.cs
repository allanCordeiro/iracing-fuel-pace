using System.IO.Compression;
using System.Text.Json;
using FuelPace.App.Diagnostics;
using FuelPace.App.Host;
using FuelPace.App.Ipc;
using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;
using FuelPace.App.Simulation;
using FuelPace.App.Tests.Fakes;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Storage;
using FuelPace.Core.Strategy;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Tests.Host;

public sealed class AgentHostTests
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    static readonly Hotkey ToggleEditKey = Parse(HotkeySettings.DefaultToggleEdit);
    static readonly Hotkey ToggleVisibleKey = Parse(HotkeySettings.DefaultToggleVisible);

    static Hotkey Parse(string s) => Hotkey.TryParse(s, out var k) ? k : throw new ArgumentException(s);

    static SessionInfo RaceInfo => SessionInfoParser.Parse(SyntheticRace.Yaml);

    static int Int(JsonElement e, string p) => e.GetProperty(p).GetInt32();

    [Fact] // PERF-06
    public void Renders_at_most_10_per_second_and_zero_when_static()
    {
        // 60 s of identical frames: after the first second, at most one more render.
        using (var rig = new HostRig())
        {
            var frame = SyntheticRace.Frames(1, false).Skip(300).First();
            var ibt = SyntheticIbt.Write(rig.TempFile(".ibt"), SyntheticRace.Yaml, Enumerable.Repeat(frame, 3600));
            var src = new FakeSource(ibt, rig.Clock, gated: true);
            rig.Sources.Then(() => src);
            rig.Start();
            src.Allow(60);
            rig.Settle(src, 60);
            int first = rig.Surface.Renders;
            Assert.True(first >= 1);
            src.Allow(3599 - 60);
            rig.Settle(src, 3599);
            Assert.True(rig.Surface.Renders - first <= 1, $"static data rendered {rig.Surface.Renders - first} more times");
        }

        // 60 s of a race (values change every frame): at most 10 renders per second.
        using (var rig = new HostRig())
        {
            var src = rig.Race(3, gated: true);
            rig.Sources.Then(() => src);
            rig.Start();
            src.Allow(3600);
            rig.Settle(src, 3600);
            int renders = rig.Surface.Renders;
            Assert.InRange(renders, 5, 60 * 10 + 1);
        }
    }

    [Fact] // criterion 7, Review Focus 3
    public void Reconnects_after_disconnect_and_shows_data_again()
    {
        using var rig = new HostRig();
        var a = rig.Race(1);
        var b = rig.Race(2, gated: true);
        rig.Sources.ThenNone(2).Then(() => a).ThenNone().Then(() => b);
        rig.Start();

        rig.WaitUntil(() => a.Disposed, "source A opened and ended", pump: Tick);
        rig.WaitUntil(() => !rig.Host.SimConnected && double.IsNaN(rig.Host.LastRendered.FuelL) && !rig.Surface.Visible,
            "overlay inactive and hidden without the sim", pump: Tick);
        Assert.Equal(SignalState.Inactive, rig.Host.LastRendered.State);

        rig.WaitUntil(() => rig.Sources.Calls >= 5, "factory retried until source B", pump: Tick);
        b.Allow(120);
        rig.WaitUntil(() => rig.Host.SimConnected && !double.IsNaN(rig.Host.LastRendered.FuelL) && rig.Surface.Visible,
            "fuel shown again after reconnecting", pump: Tick);

        Assert.Equal(2, rig.Events("SourceOpened").Count);
        Assert.Single(rig.Events("SourceEnded"));
    }

    [Fact] // criterion 12 (Agent side)
    public async Task Capture_via_ipc_persists_and_broadcasts()
    {
        using var rig = new HostRig();
        var src = rig.Race(6, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();
        var probe = await rig.ConnectAsync();

        await probe.SendAndSync(new SetCapture(true));
        src.Allow(1200 * 4 + 400);
        rig.Settle(src, 1200 * 4 + 400);
        var added = await probe.WaitFor<CaptureLapAdded>(skip: 2);
        added = probe.All<CaptureLapAdded>()[^1];
        Assert.Equal(probe.All<CaptureLapAdded>().Count, added.Total);
        Assert.True(added.Valid >= 1);

        var snap = await probe.SendAndSync(new SetCapture(false));
        src.Allow(12);
        var finished = await probe.WaitFor<CaptureFinished>();
        Assert.Equal(RaceInfo.ProfileKey, finished.ProfileKey);

        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        var doc = new ProfileStore(rig.AppData).Load(RaceInfo.CarPath, RaceInfo.TrackName);
        Assert.NotNull(doc);
        var capture = Assert.Single(doc.Captures);
        Assert.Equal(finished.CaptureId, capture.Id);
        Assert.True(capture.Laps.Count >= added.Total);
        Assert.True(snap.CaptureActive);
    }

    [Fact] // Task 14: what the Settings lap table needs to edit and summarize a capture
    public async Task Snapshot_carries_tank_capture_id_and_monitor_device_names()
    {
        using var rig = new HostRig();
        var src = rig.Race(6, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();
        var probe = await rig.ConnectAsync();

        await probe.SendAndSync(new SetCapture(true));
        src.Allow(1200 * 2 + 400);
        rig.Settle(src, 1200 * 2 + 400);
        var running = await probe.SendAndSync(new GetSnapshot());
        Assert.True(running.CaptureActive);
        Assert.Null(running.CaptureId); // no id until the capture is saved
        Assert.Equal(RaceInfo.TankCapacityL, running.TankCapacityL);
        Assert.Equal(running.MonitorLabels.Count, running.MonitorDeviceNames!.Count);
        Assert.Equal(rig.Monitors.GetMonitors().Select(m => m.DeviceName), running.MonitorDeviceNames);

        await probe.SendAndSync(new SetCapture(false));
        src.Allow(12);
        var finished = await probe.WaitFor<CaptureFinished>();
        var after = await probe.SendAndSync(new GetSnapshot());
        Assert.Equal(finished.CaptureId, after.CaptureId);

        await probe.SendAndSync(new SetCapture(true));
        Assert.Null((await probe.SendAndSync(new GetSnapshot())).CaptureId);
    }

    [Fact] // criterion 12
    public async Task Capture_survives_settings_client_disconnect()
    {
        using var rig = new HostRig();
        var src = rig.Race(6, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();

        await using (var first = await rig.ConnectAsync())
            await first.SendAndSync(new SetCapture(true));

        src.Allow(1200 * 3 + 400);
        rig.Settle(src, 1200 * 3 + 400);

        var second = await rig.ConnectAsync();
        var s = await second.SendAndSync(new GetSnapshot());
        Assert.True(s.CaptureActive);
        Assert.True(s.CaptureLaps.Count >= 3, $"{s.CaptureLaps.Count} laps");
        Assert.Equal(s.CaptureLaps.Count, s.Total);
        Assert.Equal(s.CaptureLaps.Count(l => l.Valid), s.Valid);
        Assert.Equal(rig.Host.Pipeline.Overlay.Read().RecTotal, s.Total);
        Assert.Equal(RaceInfo.ProfileKey, s.ProfileKey);
    }

    [Fact] // Review Focus 2
    public async Task Hotkey_failure_is_reported_once_and_app_continues()
    {
        using var rig = new HostRig();
        var settings = new AppSettings();
        settings.Overlay.AutoHide = false;
        rig.WriteSettings(settings);
        rig.Hotkeys.Taken.Add(ToggleEditKey);
        rig.Start();

        var (title, text) = Assert.Single(rig.Tray.Notifications);
        Assert.Equal("FuelPace", title);
        Assert.Contains(HotkeySettings.DefaultToggleEdit, text);
        Assert.Equal(ToggleVisibleKey, rig.Hotkeys.Registered[AgentHost.HotkeyToggleVisible]);
        var failed = Assert.Single(rig.Events("HotkeyRegistered"), e => Int(e, "ok") == 0);
        Assert.Equal(AgentHost.HotkeyToggleEdit, Int(failed, "id"));

        var probe = await rig.ConnectAsync();
        var snap = await probe.SendAndSync(new GetSnapshot());
        Assert.Equal([HotkeySettings.DefaultToggleEdit], snap.HotkeyFailures);

        // The app keeps working: the other hotkey still toggles the overlay.
        rig.WaitUntil(() => rig.Surface.Visible, "overlay shown", pump: Tick);
        rig.Hotkeys.Press(AgentHost.HotkeyToggleVisible);
        rig.WaitUntil(() => !rig.Surface.Visible, "overlay hidden by the hotkey", pump: Tick);

        // A new failure later is shown in the snapshot but not notified again.
        rig.Hotkeys.Taken.Add(Parse("Ctrl+Alt+K"));
        var changed = snap.Settings with { Hotkeys = snap.Settings.Hotkeys with { ToggleVisible = "Ctrl+Alt+K" } };
        await probe.SendAndSync(new UpdateSettings(changed));
        snap = await probe.SendAndSync(new GetSnapshot());
        Assert.Equal([HotkeySettings.DefaultToggleEdit, "Ctrl+Alt+K"], snap.HotkeyFailures);
        Assert.Single(rig.Tray.Notifications);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Debug_mode_sources_are_ored(bool commandLine, bool marker, bool setting)
    {
        using var rig = new HostRig();
        rig.Options = rig.Options with { DebugFromCommandLine = commandLine, DebugMarker = marker };
        var settings = new AppSettings();
        settings.Debug.Enabled = setting;
        rig.WriteSettings(settings);
        var src = rig.Race(1);
        rig.Sources.Then(() => src);
        rig.Start();

        Assert.True(rig.Host.DebugMode);
        Assert.Equal(DiagLevel.Trace, rig.Host.Log.Level);
        Assert.True(rig.Host.RecordingEnabled);
        Assert.True(rig.Hotkeys.Registered.ContainsKey(AgentHost.HotkeyDebugMark));
        Assert.True(rig.Tray.DebugMenu);
        Assert.Contains("(debug)", rig.Tray.Tooltip);

        rig.WaitUntil(() => src.Disposed, "replay finished");
        var recordings = Directory.GetFiles(Path.Combine(rig.AppData, "recordings"), "*.ibt");
        Assert.Single(recordings);
    }

    [Fact]
    public void Debug_mode_is_off_by_default()
    {
        using var rig = new HostRig();
        rig.Start();
        Assert.False(rig.Host.DebugMode);
        Assert.Equal(DiagLevel.Info, rig.Host.Log.Level);
        Assert.False(rig.Host.RecordingEnabled);
        Assert.False(rig.Hotkeys.Registered.ContainsKey(AgentHost.HotkeyDebugMark));
        Assert.False(rig.Tray.DebugMenu);
        Assert.DoesNotContain("(debug)", rig.Tray.Tooltip);
        Assert.Contains(AppInfo.Version, rig.Tray.Tooltip);
    }

    [Fact]
    public void Mark_hotkey_emits_numbered_user_marks()
    {
        using var rig = new HostRig(debug: true);
        rig.Start();
        rig.Hotkeys.Press(AgentHost.HotkeyDebugMark);
        rig.Hotkeys.Press(AgentHost.HotkeyDebugMark);
        rig.Tray.Raise(TrayCommand.InsertMark);
        rig.Ui.Drain();

        Assert.Equal(["Marcador 1", "Marcador 2", "Marcador 3"], rig.Tray.Notifications.Select(n => n.Text));
        Assert.Equal([1, 2, 3], rig.Events("UserMark").Select(e => Int(e, "markNumber")));
    }

    [Fact]
    public async Task Settings_update_applies_live()
    {
        using var rig = new HostRig();
        new ProfileStore(rig.AppData).Save(SyntheticRace.Profile(RaceInfo)); // a baseline: the mean (and autonomy) is known from lap 1
        var src = rig.Race(3, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();
        src.Allow(300);
        rig.Settle(src, 300);
        static double Reserve(OverlayState s) => s.FuelL - s.AutonomyLaps * s.MeanL;
        var before = rig.Host.Pipeline.Overlay.Read();
        Assert.Equal(0.5, Reserve(before), 6);
        int width1 = rig.Surface.LastBounds!.Value.Width;

        var probe = await rig.ConnectAsync();
        var snap = await probe.SendAndSync(new GetSnapshot());
        var changed = snap.Settings with
        {
            Strategy = snap.Settings.Strategy with { ReserveL = 1.0 },
            Overlay = snap.Settings.Overlay with { Scale = 2 },
        };
        await probe.SendAndSync(new UpdateSettings(changed));
        var applied = await probe.WaitFor<SettingsApplied>();
        Assert.Equal(1.0, applied.Settings.Strategy.ReserveL);

        src.Allow(12);
        rig.Settle(src, 312);
        Assert.Equal(1.0, Reserve(rig.Host.Pipeline.Overlay.Read()), 6);
        Assert.Equal(width1 * 2, rig.Surface.LastBounds!.Value.Width);

        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        var saved = new SettingsStore(rig.AppData).Load();
        Assert.Equal(1.0, saved.Strategy.ReserveL);
        Assert.Equal(2.0, saved.Overlay.Scale);
    }

    [Fact]
    public async Task User_move_persists_after_debounce()
    {
        using var rig = new HostRig();
        rig.Start();
        var probe = await rig.ConnectAsync();

        rig.Surface.RaiseMoved(new PixelRect(500, 300, 300, 100));
        rig.Ui.Drain();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(300));
        rig.Surface.RaiseMoved(new PixelRect(600, 320, 300, 100)); // still dragging: the debounce restarts
        rig.Ui.Drain();
        rig.Clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        Assert.False(File.Exists(rig.SettingsPath));

        rig.Clock.Advance(TimeSpan.FromMilliseconds(200));
        rig.Ui.Drain();
        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        var saved = new SettingsStore(rig.AppData).Load();
        Assert.Equal((600, 320), (saved.Overlay.OffsetX, saved.Overlay.OffsetY));
        Assert.Equal(FakeMonitors.Primary.DeviceName, saved.Overlay.MonitorDeviceName);
        var applied = await probe.WaitFor<SettingsApplied>();
        Assert.Equal(600, applied.Settings.Overlay.OffsetX);
        Assert.Single(rig.Events("SettingsSaved"));
    }

    [Fact] // the window was moved by the user: the host's last bounds no longer describe it, so they are sent again
    public void A_user_move_is_always_followed_by_the_hosts_bounds()
    {
        using var rig = new HostRig();
        var settings = new AppSettings();
        settings.Overlay.AutoHide = false;
        rig.WriteSettings(settings);
        rig.Start();
        rig.Hotkeys.Press(AgentHost.HotkeyToggleEdit);
        rig.WaitUntil(() => rig.Surface.LastBounds is { X: 40 - 7 }, "edit-mode placement", pump: Tick);
        var placed = rig.Surface.LastBounds!.Value;
        int before = rig.Surface.AllBounds.Count;

        // A drop that resolves to the same placement (here: the same rect) must still reposition the window.
        rig.Surface.RaiseMoved(placed);
        rig.WaitUntil(() => rig.Surface.AllBounds.Count > before, "bounds re-applied after the drag", pump: Tick);
        Assert.Equal(placed, rig.Surface.LastBounds);
    }

    [Fact] // the edit chrome's width is known from the last render, not from bounds the first gesture invalidated
    public void Two_gestures_before_a_render_both_leave_out_the_edit_chrome()
    {
        using var rig = new HostRig();
        var settings = new AppSettings();
        settings.Overlay.AutoHide = false;
        rig.WriteSettings(settings);
        rig.Start();
        rig.Hotkeys.Press(AgentHost.HotkeyToggleEdit);
        rig.WaitUntil(() => rig.Surface.LastBounds is { X: 40 - 7 }, "edit-mode placement", pump: Tick);
        var placed = rig.Surface.LastBounds!.Value;

        rig.Surface.RaiseResized(placed); // same size twice, no render in between: the scale stays 100%
        rig.Surface.RaiseResized(placed);
        rig.Ui.Drain();
        Assert.Equal(1.0, rig.Host.Settings.Overlay.Scale);
    }

    static AppSettings UserSettings()
    {
        var mine = new AppSettings();
        mine.Hotkeys.ToggleEdit = "Ctrl+Alt+E";
        mine.Overlay.OffsetX = 77;
        return mine;
    }

    static FileStream Lock(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); // an antivirus holds it

    [Fact] // carry-over (a)
    public async Task Settings_are_not_overwritten_after_a_fallback_load()
    {
        using var rig = new HostRig();
        rig.WriteSettings(UserSettings());
        string original = File.ReadAllText(rig.SettingsPath);
        IpcProbe probe;
        using (Lock(rig.SettingsPath))
        {
            rig.Start();
            Assert.True(rig.Host.SettingsFallback);
            probe = await rig.ConnectAsync();
            var snap = await probe.SendAndSync(new GetSnapshot());
            Assert.Equal(HotkeySettings.DefaultToggleEdit, snap.Settings.Hotkeys.ToggleEdit);

            // Changes from the Settings window are refused while the file cannot be read: they would save the defaults.
            await probe.SendAndSync(new UpdateSettings(snap.Settings with { Strategy = snap.Settings.Strategy with { ReserveL = 1.0 } }));
            Assert.Equal(AgentHost.SettingsUnreadableText, (await probe.WaitFor<Notice>()).Text);
            Assert.Equal(0.5, rig.Host.Settings.Strategy.ReserveL);
            await probe.SendAndSync(new SetDebugMode(true));
            await probe.WaitFor<Notice>(skip: 1);
            Assert.False(rig.Host.DebugMode);

            // A drag applies at once but is not saved; the 5 s re-read keeps failing; protection stays armed.
            rig.Hotkeys.Press(AgentHost.HotkeyToggleEdit);
            rig.Surface.RaiseMoved(new PixelRect(500, 300, 300, 100));
            rig.Ui.Drain();
            Assert.Equal(507, rig.Host.Settings.Overlay.OffsetX); // edit mode: the panel sits (7, 31) px inside the window
            rig.Clock.Advance(TimeSpan.FromSeconds(6));
            rig.Ui.Drain();
            Assert.True(rig.Host.WaitIo(HostRig.Timeout));
            Assert.True(rig.Host.SettingsFallback);
        }
        Assert.Equal(original, File.ReadAllText(rig.SettingsPath));
        Assert.Empty(rig.Events("SettingsSaved"));

        // Readable again: the next 5 s re-read brings the user's settings back and tells the Settings window.
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Ui.Drain();
        var applied = await probe.WaitFor<SettingsApplied>();
        Assert.Equal("Ctrl+Alt+E", applied.Settings.Hotkeys.ToggleEdit);
        Assert.False(rig.Host.SettingsFallback);

        await probe.SendAndSync(new UpdateSettings(applied.Settings with { Strategy = applied.Settings.Strategy with { ReserveL = 1.0 } }));
        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        var saved = new SettingsStore(rig.AppData).Load();
        Assert.Equal(("Ctrl+Alt+E", 77, 1.0), (saved.Hotkeys.ToggleEdit, saved.Overlay.OffsetX, saved.Strategy.ReserveL));
    }

    [Fact] // carry-over (a)
    public async Task Snapshot_after_the_file_is_readable_again_carries_the_users_settings()
    {
        using var rig = new HostRig();
        rig.WriteSettings(UserSettings());
        using (Lock(rig.SettingsPath)) rig.Start();
        Assert.True(rig.Host.SettingsFallback);

        var probe = await rig.ConnectAsync(); // Hello re-reads the file before answering
        var snap = await probe.SendAndSync(new GetSnapshot());
        Assert.Equal("Ctrl+Alt+E", snap.Settings.Hotkeys.ToggleEdit);
        Assert.False(rig.Host.SettingsFallback);

        await probe.SendAndSync(new UpdateSettings(snap.Settings with { StartWithWindows = true }));
        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        var saved = new SettingsStore(rig.AppData).Load();
        Assert.Equal(("Ctrl+Alt+E", 77, true), (saved.Hotkeys.ToggleEdit, saved.Overlay.OffsetX, saved.StartWithWindows));
    }

    sealed class ThrowingTray : ITrayIcon
    {
        public event Action<TrayCommand>? Command { add => throw new InvalidOperationException("tray unavailable"); remove { } }
        public void SetTooltip(string text) { }
        public void Notify(string title, string text) { }
        public void SetDebugMenu(bool visible) { }
    }

    [Fact]
    public void Dispose_after_a_failed_start_does_not_throw()
    {
        using var rig = new HostRig();
        var host = new AgentHost(rig.Options, rig.Sources, rig.Surface, rig.Ui, rig.Hotkeys, new ThrowingTray(), rig.Monitors,
            rig.Sys, new FixedMetricsMeasurer(), rig.Clock);
        Assert.Throws<InvalidOperationException>(host.Start);
        host.Dispose();
        host.Dispose();
        Assert.False(host.TelemetryThreadAlive);
    }

    [Fact] // Review Focus 5 (host side) and the placement contract
    public void Placement_follows_monitors_and_dpi_and_survives_an_empty_monitor_list()
    {
        using var rig = new HostRig();
        var settings = new AppSettings();
        settings.Overlay.AutoHide = false;
        rig.WriteSettings(settings);
        rig.Start();
        rig.WaitUntil(() => rig.Surface.LastBounds is not null, "first placement", pump: Tick);
        var first = rig.Surface.LastBounds!.Value;
        Assert.Equal((40, 40, 300), (first.X, first.Y, first.Width));

        rig.Monitors.Set(); // a display change reporting no monitor for a moment
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Ui.Drain();
        Assert.Equal(first, rig.Surface.LastBounds);

        var hiDpi = new MonitorDesc(@"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 1.5, true);
        rig.Monitors.Set(hiDpi);
        rig.WaitUntil(() => rig.Surface.LastBounds!.Value.X == 1960, "moved to the remaining monitor", pump: Tick);
        var moved = rig.Surface.LastBounds!.Value;
        Assert.Equal((1960, 40, 450), (moved.X, moved.Y, moved.Width)); // 300 DIP × scale 1 × DPI 1.5
        Assert.Equal(450, (int)Math.Ceiling(rig.Surface.LastSize.W)); // the draw list itself is built at DPI scale

        // Edit mode: the chrome follows the DPI (7 × 1.5, 31 × 1.5 px around the panel), the footer the user's scale.
        rig.Hotkeys.Press(AgentHost.HotkeyToggleEdit);
        rig.WaitUntil(() => rig.Surface.LastBounds!.Value.X == 1960 - 11, "edit-mode window around the panel", pump: Tick);
        Assert.Equal(40 - 47, rig.Surface.LastBounds!.Value.Y);
        Assert.Contains("X 40 · Y 40 · escala 100%", rig.Surface.LastTexts);
    }

    [Fact]
    public void Auto_hide_rules()
    {
        var active = new OverlayState { State = SignalState.Blue };
        var inactive = new OverlayState { State = SignalState.Inactive };
        var shortly = TimeSpan.FromSeconds(2.9);
        var long_ = TimeSpan.FromSeconds(3.1);

        // (userVisible, autoHide, sim, state, edit, inactiveFor) -> shown
        Assert.True(AutoHidePolicy.ShouldShow(true, true, true, in active, false, TimeSpan.Zero));
        Assert.False(AutoHidePolicy.ShouldShow(true, true, false, in active, false, TimeSpan.Zero));  // no sim
        Assert.True(AutoHidePolicy.ShouldShow(true, true, true, in inactive, false, shortly));      // inactive ≤ 3 s
        Assert.False(AutoHidePolicy.ShouldShow(true, true, true, in inactive, false, long_));       // inactive > 3 s
        Assert.True(AutoHidePolicy.ShouldShow(true, false, false, in inactive, false, long_));     // auto-hide off
        Assert.False(AutoHidePolicy.ShouldShow(false, false, true, in active, false, TimeSpan.Zero)); // hidden by the user
        Assert.False(AutoHidePolicy.ShouldShow(false, true, true, in active, false, TimeSpan.Zero));
        Assert.True(AutoHidePolicy.ShouldShow(false, true, false, in inactive, true, long_));      // edit mode always shows
        Assert.True(AutoHidePolicy.ShouldShow(true, true, false, in inactive, true, long_));
    }

    [Fact]
    public void Auto_hide_hides_after_three_seconds_inactive()
    {
        using var rig = new HostRig();
        // Parked in the pit stall: the strategy is inactive, the sim is connected.
        var frame = SyntheticRace.Frames(1, false).Skip(300).First();
        frame.PlayerCarInPitStall = true;
        var ibt = SyntheticIbt.Write(rig.TempFile(".ibt"), SyntheticRace.Yaml, Enumerable.Repeat(frame, 600));
        var src = new FakeSource(ibt, rig.Clock, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();
        src.Allow(120); // 2 s
        rig.Settle(src, 120);
        Assert.True(rig.Surface.Visible);
        src.Allow(240); // 6 s: past the 1.5 s hysteresis and the 3 s delay
        rig.Settle(src, 360);
        Assert.False(rig.Surface.Visible);
        rig.Hotkeys.Press(AgentHost.HotkeyToggleEdit); // edit mode always shows
        rig.WaitUntil(() => rig.Surface.Visible && rig.Surface.EditMode, "shown in edit mode", pump: Tick);
    }

    [Fact] // Review Focus 1 (host side)
    public async Task Fullscreen_exclusive_notice_once_per_session()
    {
        using var rig = new HostRig();
        var src = rig.Race(2, gated: true);
        rig.Sources.Then(() => src);
        rig.Sys.Fullscreen = true;
        rig.Start();
        var probe = await rig.ConnectAsync();

        rig.Clock.Advance(TimeSpan.FromSeconds(5)); // no sim yet: nothing is checked
        rig.Ui.Drain();
        Assert.Equal(0, rig.Sys.FullscreenQueries);

        src.Allow(30);
        rig.Settle(src, 30);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Ui.Drain();
        var (_, text) = Assert.Single(rig.Tray.Notifications);
        Assert.Contains("tela cheia exclusiva", text);
        var notice = await probe.WaitFor<Notice>();
        Assert.Equal(text, notice.Text);
        Assert.True((await probe.SendAndSync(new GetSnapshot())).FullscreenExclusive);

        rig.Sys.Fullscreen = false;
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Ui.Drain();
        rig.Sys.Fullscreen = true;
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Ui.Drain();
        Assert.Single(rig.Tray.Notifications);
        Assert.Equal([1, 0, 1], rig.Events("FullscreenExclusive").Select(e => Int(e, "detected")));
        // every change reaches an open Settings window (its banner), not only the first notice
        await probe.WaitFor<FullscreenChanged>(m => m.On, skip: 1);
        Assert.Equal([true, false, true], probe.All<FullscreenChanged>().Select(m => m.On));
    }

    [Theory] // the Settings toggle cannot turn off a debug mode the marker or --debug forces
    [InlineData(false, true, "marker")]
    [InlineData(true, false, "commandLine")]
    [InlineData(false, false, null)]
    public async Task Snapshot_says_what_forces_debug_mode(bool commandLine, bool marker, string? by)
    {
        using var rig = new HostRig();
        rig.Options = rig.Options with { DebugFromCommandLine = commandLine, DebugMarker = marker };
        rig.Start();
        var probe = await rig.ConnectAsync();
        var snap = await probe.SendAndSync(new GetSnapshot());
        Assert.Equal(by, snap.DebugForcedBy);
        Assert.Equal(by is not null, snap.DebugMode);
    }

    [Fact] // Review Focus 4 (host side)
    public async Task Second_instance_request_launches_settings()
    {
        using var rig = new HostRig();
        rig.Start();
        var probe = await rig.ConnectAsync();
        await probe.SendAndSync(new ShowSettingsRequest());
        Assert.Equal(1, rig.Sys.LaunchSettingsCalls);
        rig.Tray.Raise(TrayCommand.OpenSettings);
        rig.Ui.Drain();
        Assert.Equal(2, rig.Sys.LaunchSettingsCalls);
    }

    [Fact]
    public async Task Bundle_command_creates_zip_on_desktop()
    {
        using var rig = new HostRig();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        rig.Sys.Info["exe"] = Path.Combine(home, "Downloads", "FuelPace", "FuelPace.Agent.exe");
        rig.Start();
        var probe = await rig.ConnectAsync();
        await probe.Client.SendAsync(new CreateBundle(false));
        var created = await probe.WaitFor<BundleCreated>();

        Assert.True(File.Exists(created.Path));
        Assert.Equal(rig.Sys.DesktopDir, Path.GetDirectoryName(created.Path));
        using var zip = ZipFile.OpenRead(created.Path);
        using var reader = new StreamReader(zip.GetEntry("system-info.txt")!.Open());
        string info = reader.ReadToEnd();
        Assert.Contains("os: Windows 11 (fake)", info);
        Assert.Contains(SystemInfoKeys.Version + ": " + AppInfo.Version, info);
        Assert.Contains("FuelPace.Agent.exe", info);
        Assert.DoesNotContain(home, info, StringComparison.OrdinalIgnoreCase);
        Assert.Single(rig.Events("BundleCreated"));
    }

    [Fact] // Ruling A20: at a Windows session end the 3 s budget goes to the logs and the recording, not to the hotkeys
    public async Task Session_end_skips_the_hotkeys_and_finishes_well_under_the_budget()
    {
        using var rig = new HostRig(replay: false);
        rig.Start();
        int before = Volatile.Read(ref rig.Hotkeys.UnregisterCalls); // the registration at start unregisters first
        rig.Hotkeys.UnregisterDelay = TimeSpan.FromSeconds(2); // the UI thread is blocked in WndProc: the real call times out

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(rig.Host.EndSession, TestContext.Current.CancellationToken); // a pool thread, as Program does
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"session-end dispose took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Equal(before, Volatile.Read(ref rig.Hotkeys.UnregisterCalls));
        Assert.False(rig.Host.TelemetryThreadAlive);
        Assert.Single(rig.Events("Shutdown")); // the log's tail made it to disk
        Assert.True(rig.Host.Completion.IsCompleted);
    }

    [Fact]
    public void A_normal_dispose_still_unregisters_the_hotkeys()
    {
        using var rig = new HostRig(replay: false);
        rig.Start();
        int before = Volatile.Read(ref rig.Hotkeys.UnregisterCalls);
        rig.Host.Dispose();
        Assert.Equal(before + 1, Volatile.Read(ref rig.Hotkeys.UnregisterCalls));
        Assert.Empty(rig.Hotkeys.Registered);
    }

    [Fact] // A16: Sair during "Gerar pacote" with big recordings does not wait for the zip
    public async Task Exit_during_a_bundle_cancels_it_and_shuts_down_promptly()
    {
        using var rig = new HostRig(replay: false);
        var rec = Path.Combine(rig.AppData, "recordings");
        Directory.CreateDirectory(rec);
        var rnd = new Random(3);
        var buf = new byte[1 << 20];
        foreach (var name in new[] { "20261109-140000_a.ibt", "20261109-150000_b.ibt" })
        {
            using var fs = File.Create(Path.Combine(rec, name));
            for (int i = 0; i < 64; i++) { rnd.NextBytes(buf); fs.Write(buf); }
        }
        rig.Start();
        rig.Tray.Raise(TrayCommand.CreateBundle);
        rig.WaitUntil(() => Directory.Exists(rig.Sys.DesktopDir) && Directory.GetFiles(rig.Sys.DesktopDir, "*.tmp").Length > 0,
            "the bundle started");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Host.Dispose();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"shutdown took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.Empty(Directory.GetFiles(rig.Sys.DesktopDir)); // no zip, no .tmp
        Assert.Contains(rig.LogLines(), l => l.TryGetProperty("msg", out var m) && m.GetString()!.Contains("bundle cancelled"));
        Assert.Single(rig.Events("Shutdown"));
    }

    [Fact]
    public async Task Exit_after_shuts_down_cleanly_and_logs()
    {
        var rig = new HostRig(exitAfter: TimeSpan.FromSeconds(30));
        try
        {
            rig.Start();
            rig.Clock.Advance(TimeSpan.FromSeconds(29));
            Assert.False(rig.Host.Completion.IsCompleted);
            rig.Clock.Advance(TimeSpan.FromSeconds(2));
            await rig.Host.Completion.WaitAsync(HostRig.Timeout, TestContext.Current.CancellationToken);
            rig.Host.Dispose();
            Assert.False(rig.Host.TelemetryThreadAlive);
            Assert.Single(rig.Events("Startup"));
            Assert.Single(rig.Events("Shutdown"));
        }
        finally { rig.Dispose(); }

        using var tray = new HostRig();
        tray.Start();
        tray.Tray.Raise(TrayCommand.Exit);
        await tray.Host.Completion.WaitAsync(HostRig.Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Log_contains_startup_lap_and_perf_events()
    {
        using var rig = new HostRig();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        rig.Sys.Info["exe"] = Path.Combine(home, "FuelPace", "FuelPace.Agent.exe");
        var src = rig.Race(9); // 3 minutes of synthetic race
        rig.Sources.Then(() => src);
        rig.Start();
        rig.WaitUntil(() => src.Disposed, "replay finished");
        rig.Ui.Drain();

        var lines = rig.LogLines();
        Assert.Single(lines, l => l.TryGetProperty("ev", out var e) && e.GetString() == "Startup");
        var startup = Assert.Single(lines, l => l.GetProperty("cat").GetString() == "startup" && l.TryGetProperty("msg", out _));
        string msg = startup.GetProperty("msg").GetString()!;
        Assert.Contains(AppInfo.Version, msg);
        Assert.Contains("\"reserveL\"", msg); // the settings JSON
        Assert.Contains("FuelPace.Agent.exe", msg);
        Assert.DoesNotContain(home, msg, StringComparison.OrdinalIgnoreCase);
        Assert.True(rig.Events("LapCompleted").Count >= 8);
        Assert.True(rig.Events("PerfMinute").Count >= 2);
        var redraws = rig.Events("OverlayRedraws");
        Assert.True(redraws.Count >= 2);
        Assert.True(Int(redraws[0], "perMinute") >= 1);
        var memory = rig.Events("MemoryMinute"); // PERF-05: where the working set goes, next to the redraws
        Assert.Equal(redraws.Count, memory.Count);
        Assert.True(memory[0].GetProperty("workingSetMB").GetDouble() > 0);
        Assert.True(memory[0].GetProperty("gcHeapMB").GetDouble() > 0);
        Assert.True(Int(memory[0], "assemblies") > 0);
    }

    [Fact]
    public async Task Profile_command_saves_broadcasts_and_updates_the_current_session()
    {
        using var rig = new HostRig();
        var store = new ProfileStore(rig.AppData);
        var doc = SyntheticRace.Profile(RaceInfo);
        var rec = new Core.Capture.CaptureRecord
        {
            Id = "2026-11-09T12:00:00Z", SessionType = "Practice",
            Conditions = new Core.Capture.CaptureConditions(30, 20, 0, 0, 0, RaceInfo.TankCapacityL),
        };
        for (int lap = 1; lap <= 4; lap++)
            rec.Laps.Add(new Core.Capture.CapturedLap { Lap = lap, TimeS = 80 + lap * 0.1, FuelL = 2.4, FullThrottlePct = 60 });
        ProfileOperations.AddCapture(doc, rec, SyntheticRace.T0);
        store.Save(doc);
        File.WriteAllText(store.PendingPathFor(RaceInfo.CarPath, RaceInfo.TrackName, rec.Id), "{}");
        var src = rig.Race(2, gated: true);
        rig.Sources.Then(() => src);
        rig.Start();
        src.Allow(60);
        rig.Settle(src, 60);
        Assert.NotEqual(SignalState.NoBaseline, rig.Host.Pipeline.Overlay.Read().State);

        var probe = await rig.ConnectAsync();
        var list = (await SendAndWait<ProfileList>(probe, new ListProfiles())).Items;
        var summary = Assert.Single(list);
        Assert.Equal(RaceInfo.ProfileKey, summary.Key);
        Assert.Equal(1, summary.Captures);

        await probe.Client.SendAsync(new ProfileCommand(RaceInfo.ProfileKey, "deleteCapture", rec.Id, null, null));
        var updated = await probe.WaitFor<Profile>();
        Assert.Equal(RaceInfo.ProfileKey, updated.Key);
        Assert.Contains("\"captures\": []", updated.Json);
        Assert.True(rig.Host.WaitIo(HostRig.Timeout));
        Assert.Empty(store.Load(RaceInfo.CarPath, RaceInfo.TrackName)!.Captures);
        Assert.False(File.Exists(store.PendingPathFor(RaceInfo.CarPath, RaceInfo.TrackName, rec.Id)));

        // The current session uses the edited profile (no baseline left) from the next publish on.
        src.Allow(12);
        rig.Settle(src, 72);
        Assert.Equal(SignalState.NoBaseline, rig.Host.Pipeline.Overlay.Read().State);

        await probe.Client.SendAsync(new ProfileCommand(RaceInfo.ProfileKey, "deleteCapture", "nope", null, null));
        Assert.Contains("nope", (await probe.WaitFor<Error>()).Text);
    }

    static async Task<T> SendAndWait<T>(IpcProbe p, IpcMessage m) where T : IpcMessage
    {
        int before = p.All<T>().Count;
        await p.Client.SendAsync(m);
        return await p.WaitFor<T>(skip: before);
    }
}
