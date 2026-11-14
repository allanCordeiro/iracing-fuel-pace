using FuelPace.App.Ipc;
using FuelPace.App.SettingsUi;
using FuelPace.App.Settings;
using FuelPace.Core.Strategy;

namespace FuelPace.App.Tests.SettingsUi;

public class OverlayViewModelTests
{
    static AppSettings Custom()
    {
        var s = new AppSettings();
        s.Overlay.Scale = 1.25;
        s.Overlay.OffsetX = 10;
        s.Overlay.MonitorDeviceName = @"\\.\DISPLAY2";
        s.Debug.Enabled = true;
        return s;
    }

    [Fact]
    public async Task Fields_show_the_snapshot_settings()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(Custom()));
        var o = r.Overlay;
        Assert.Equal(["1 · 2560 × 1440", "2 · 1920 × 1080"], o.MonitorLabels);
        Assert.Equal(1, o.SelectedMonitorIndex);
        Assert.Equal(10, o.OffsetX);
        Assert.Equal(125, o.ScalePercent);
        Assert.Equal(60, o.BackgroundOpacityPercent);
        Assert.Equal(100, o.ContentOpacityPercent);
        Assert.False(o.ShowPaceLine);
        Assert.True(o.ShowDeltaBar);
        Assert.Equal(0.20, o.DeltaBarScale);
        Assert.Equal("±0.20 L/v", o.DeltaBarScaleText);
        Assert.False(o.RedComplementSeconds);
        Assert.True(o.ShowAvgInSupport);
        Assert.True(o.AutoHide);
        Assert.Equal(0.5, o.ReserveL);
        Assert.Equal(5, o.MeanWindow);
        Assert.Equal(6, o.MaxSavingPercent);
        Assert.Equal(0, o.ExtraLaps);
        Assert.True(o.LapsAuto);
        Assert.Equal("Ctrl+Shift+F", o.ToggleEdit.Text);
        Assert.Equal("Ctrl+Shift+H", o.ToggleVisible.Text);
        Assert.Equal("PRÉVIA · 125%", o.PreviewTitle);
    }

    [Fact]
    public async Task Overlay_changes_debounced_into_single_update()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 150;
        r.Overlay.BackgroundOpacityPercent = 40;
        r.Overlay.OffsetX = 120;
        r.Overlay.ShowPaceLine = true;
        r.Overlay.MeanWindow = 8;
        Assert.Empty(r.Conn.Of<UpdateSettings>());
        Assert.Equal(TimeSpan.FromMilliseconds(300), r.Sched.LastDelay);
        Assert.Equal(1, r.Sched.Pending); // each change restarts the one timer

        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal(1.5, u.Overlay.Scale);
        Assert.Equal(0.4, u.Overlay.BackgroundOpacity);
        Assert.Equal(120, u.Overlay.OffsetX);
        Assert.True(u.Overlay.ShowPaceLine);
        Assert.Equal(8, u.Strategy.MeanWindow);
        Assert.Equal(0.5, u.Strategy.ReserveL);

        r.Sched.FireAll();
        Assert.Single(r.Conn.Of<UpdateSettings>());
    }

    [Fact]
    public async Task Values_are_clamped_to_the_allowed_ranges()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 400;
        r.Overlay.ContentOpacityPercent = 5;
        r.Overlay.MeanWindow = 40;
        r.Overlay.ReserveL = -3;
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal(2.5, u.Overlay.Scale);
        Assert.Equal(0.3, u.Overlay.ContentOpacity);
        Assert.Equal(16, u.Strategy.MeanWindow);
        Assert.Equal(0, u.Strategy.ReserveL);
        Assert.Equal(250, r.Overlay.ScalePercent);
    }

    [Fact]
    public async Task Calculation_and_mode_and_monitor_reach_the_update()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ReserveL = 1.2;
        r.Overlay.MaxSavingPercent = 4;
        r.Overlay.ExtraLaps = 1;
        r.Overlay.LapsAuto = false;
        r.Overlay.StintLaps = 30;
        r.Overlay.SelectedMonitorIndex = 1;
        r.Overlay.AutoHide = false;
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal(1.2, u.Strategy.ReserveL);
        Assert.Equal(0.04, u.Strategy.MaxSavingFraction, 6);
        Assert.Equal(1, u.Strategy.ExtraLaps);
        Assert.Equal(LapsMode.Stint, u.Strategy.Mode);
        Assert.Equal(30, u.Strategy.StintLaps);
        Assert.Equal(@"\\.\DISPLAY2", u.Overlay.MonitorDeviceName);
        Assert.False(u.Overlay.AutoHide);
    }

    [Fact]
    public async Task Preview_is_rebuilt_when_options_change()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        var first = r.Overlay.Preview;
        Assert.NotEmpty(first.Commands.ToArray());
        long hash = first.ContentHash;

        r.Overlay.ScalePercent = 200;
        Assert.NotSame(first, r.Overlay.Preview);
        Assert.NotEqual(hash, r.Overlay.Preview.ContentHash);
        Assert.Equal("PRÉVIA · 200%", r.Overlay.PreviewTitle);
        Assert.True(r.Overlay.Preview.Size.W > first.Size.W);

        r.Overlay.ShowPaceLine = true;
        Assert.True(r.Overlay.Preview.Size.H > 0);
    }

    [Fact]
    public async Task Preview_notifies_the_view()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        var changed = new List<string?>();
        r.Overlay.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        r.Overlay.BackgroundOpacityPercent = 30;
        Assert.Contains("Preview", changed);
    }

    [Fact]
    public async Task Position_in_game_enters_edit_mode()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.Equal("Posicionar no jogo", r.Overlay.PositionInGameText);
        r.Overlay.PosicionarNoJogoCommand.Execute(null);
        Assert.Equal(new SetEditMode(true), Assert.Single(r.Conn.Of<SetEditMode>()));
    }

    [Fact]
    public async Task Invalid_hotkey_text_is_flagged_and_not_sent()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ToggleEdit.Text = "Ctrl+Shift+";
        Assert.False(string.IsNullOrEmpty(r.Overlay.ToggleEdit.Error));
        Assert.True(r.Overlay.ToggleEdit.IsRed);
        r.Sched.FireAll();
        Assert.Empty(r.Conn.Of<UpdateSettings>());

        // a valid change elsewhere goes out, with the last good hotkey in place of the broken text
        r.Overlay.ScalePercent = 110;
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal("Ctrl+Shift+F", u.Hotkeys.ToggleEdit);
        Assert.Equal(1.1, u.Overlay.Scale);

        r.Overlay.ToggleEdit.Text = "ctrl + alt + k";
        Assert.Null(r.Overlay.ToggleEdit.Error);
        r.Sched.FireAll();
        Assert.Equal("Ctrl+Alt+K", r.Conn.Of<UpdateSettings>()[^1].Settings.Hotkeys.ToggleEdit);
    }

    [Fact]
    public async Task Same_key_on_two_actions_is_rejected()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ToggleVisible.Text = "ctrl+shift+f"; // the toggle-edit key
        Assert.NotNull(r.Overlay.ToggleVisible.Error);
        Assert.NotNull(r.Overlay.ToggleEdit.Error);
        r.Sched.FireAll();
        Assert.Empty(r.Conn.Of<UpdateSettings>());

        r.Overlay.ToggleVisible.Text = "Ctrl+Shift+H";
        Assert.Null(r.Overlay.ToggleVisible.Error);
        Assert.Null(r.Overlay.ToggleEdit.Error);
    }

    [Fact]
    public async Task Hotkeys_the_agent_could_not_register_are_red()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(failures: ["Ctrl+Shift+F"]));
        Assert.True(r.Overlay.ToggleEdit.IsRed);
        Assert.Equal("Já em uso por outro programa", r.Overlay.ToggleEdit.Warning);
        Assert.False(r.Overlay.ToggleVisible.IsRed);

        r.Overlay.ToggleEdit.Text = "Ctrl+Alt+J"; // typing another key clears it
        Assert.False(r.Overlay.ToggleEdit.IsRed);
    }

    [Fact]
    public async Task Settings_applied_replaces_the_whole_model()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 180; // an edit waiting for its debounce
        var recovered = Custom();
        recovered.Overlay.OffsetY = 77;
        recovered.Strategy = new StrategyConfig { ReserveL = 2, Mode = LapsMode.Stint, StintLaps = 12 };
        r.Conn.Raise(new SettingsApplied(recovered));

        Assert.Equal(125, r.Overlay.ScalePercent);
        Assert.Equal(77, r.Overlay.OffsetY);
        Assert.Equal(2, r.Overlay.ReserveL);
        Assert.False(r.Overlay.LapsAuto);
        Assert.Equal(12, r.Overlay.StintLaps);
        r.Sched.FireAll(); // the stale edit must not be sent
        Assert.Empty(r.Conn.Of<UpdateSettings>());

        // and the next edit starts from the new model, not the old snapshot
        r.Overlay.ExtraLaps = 2;
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal(77, u.Overlay.OffsetY);
        Assert.Equal(2, u.Strategy.ReserveL);
        Assert.True(u.Debug.Enabled);
    }

    [Fact]
    public async Task Edit_before_the_first_snapshot_is_not_sent_until_the_snapshot_and_is_built_on_it()
    {
        var r = new SettingsRig();
        await r.Vm.ConnectAsync(); // Hello sent, no snapshot yet
        r.Overlay.ScalePercent = 150;
        r.Sched.FireAll();
        Assert.Empty(r.Conn.Of<UpdateSettings>());

        var agent = Custom();
        agent.Overlay.OffsetY = 77;
        r.Conn.Raise(new Snapshot(SettingsRig.Snap(agent)));
        Assert.Equal(150, r.Overlay.ScalePercent); // the typed edit survives
        Assert.Equal(77, r.Overlay.OffsetY);
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal(1.5, u.Overlay.Scale);
        Assert.Equal(77, u.Overlay.OffsetY); // not the defaults
        Assert.Equal(10, u.Overlay.OffsetX);
        Assert.True(u.Debug.Enabled);
    }

    [Fact]
    public async Task Edit_after_reconnect_waits_for_the_new_snapshot()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Conn.Drop();
        r.Overlay.BackgroundOpacityPercent = 25;
        r.Sched.FireAll(); // also reconnects (Reachable)
        Assert.True(r.Vm.AgentOnline);
        r.Sched.FireAll();
        Assert.Empty(r.Conn.Of<UpdateSettings>());

        var agent = new AppSettings();
        agent.Overlay.OffsetX = 5; // changed meanwhile, e.g. by dragging the overlay in the game
        r.Conn.Raise(new Snapshot(SettingsRig.Snap(agent)));
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings;
        Assert.Equal(0.25, u.Overlay.BackgroundOpacity);
        Assert.Equal(5, u.Overlay.OffsetX);
    }

    [Fact]
    public async Task Two_updates_in_flight_do_not_flicker_the_fields()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 150;
        r.Sched.FireAll();
        r.Overlay.BackgroundOpacityPercent = 20;
        r.Sched.FireAll();
        var (a, b) = (r.Conn.Of<UpdateSettings>()[0].Settings, r.Conn.Of<UpdateSettings>()[1].Settings);

        r.Conn.Raise(new SettingsApplied(a));
        Assert.Equal(150, r.Overlay.ScalePercent);
        Assert.Equal(20, r.Overlay.BackgroundOpacityPercent);
        r.Conn.Raise(new SettingsApplied(b));
        Assert.Equal(20, r.Overlay.BackgroundOpacityPercent);
        r.Sched.FireAll();
        Assert.Equal(2, r.Conn.Of<UpdateSettings>().Count);
    }

    [Fact]
    public async Task Echo_of_our_own_update_keeps_newer_edits()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 150;
        r.Sched.FireAll();
        var sent = r.Conn.Of<UpdateSettings>()[0].Settings;

        r.Overlay.BackgroundOpacityPercent = 20; // typed while the Agent was still applying
        r.Conn.Raise(new SettingsApplied(sent));
        Assert.Equal(20, r.Overlay.BackgroundOpacityPercent);
        r.Sched.FireAll();
        var u = r.Conn.Of<UpdateSettings>()[^1].Settings;
        Assert.Equal(0.2, u.Overlay.BackgroundOpacity);
        Assert.Equal(1.5, u.Overlay.Scale);
    }

    [Fact]
    public async Task Update_is_built_on_the_latest_confirmed_settings()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        // debug mode toggled elsewhere (Diagnostics) changes a part of the model the Overlay page does not edit
        var withDebug = new AppSettings();
        withDebug.Debug.Enabled = true;
        r.Conn.Raise(new SettingsApplied(withDebug));
        r.Overlay.ScalePercent = 120;
        r.Sched.FireAll();
        Assert.True(r.Conn.Of<UpdateSettings>()[0].Settings.Debug.Enabled);
    }

    [Fact]
    public async Task Unreadable_settings_notice_is_shown_and_the_edit_is_retried()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 150;
        r.Sched.FireAll();
        Assert.Single(r.Conn.Of<UpdateSettings>());

        r.Conn.Raise(new Notice("warn", "configurações ainda não puderam ser lidas; tente novamente"));
        Assert.Equal("As configurações ainda não puderam ser lidas. Tentando de novo…", r.Overlay.StatusMessage);
        Assert.Equal(150, r.Overlay.ScalePercent); // the edit is kept
        Assert.Equal(1, r.Sched.Pending);
        r.Sched.FireAll();
        Assert.Equal(2, r.Conn.Of<UpdateSettings>().Count);
        Assert.Equal(1.5, r.Conn.Of<UpdateSettings>()[1].Settings.Overlay.Scale);
    }

    [Fact]
    public async Task Retries_give_up_after_a_few_attempts()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.ScalePercent = 150;
        r.Sched.FireAll();
        for (int i = 0; i < 10; i++)
        {
            r.Conn.Raise(new Notice("warn", "configurações ainda não puderam ser lidas; tente novamente"));
            r.Sched.FireAll();
        }
        Assert.InRange(r.Conn.Of<UpdateSettings>().Count, 2, 5);
        Assert.Equal(0, r.Sched.Pending);
        Assert.Contains("Não foi possível", r.Overlay.StatusMessage);
    }

    [Fact]
    public async Task Reload_notice_replaces_the_model_through_settings_applied()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Conn.Raise(new Notice("warn", "configurações recarregadas do arquivo; confira e tente novamente"));
        Assert.Equal("Configurações recarregadas do arquivo; confira e tente novamente.", r.Overlay.StatusMessage);
    }

    [Fact]
    public async Task Layout_fields_show_the_snapshot_and_reach_the_update()
    {
        var snap = new AppSettings();
        snap.Overlay.ShowDeltaBar = false;
        snap.Overlay.DeltaBarScaleLpl = 0.35;
        snap.Overlay.RedComplement = RedComplement.Seconds;
        snap.Overlay.ShowAvgInSupport = false;
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(snap));
        var o = r.Overlay;
        Assert.False(o.ShowDeltaBar);
        Assert.False(o.DeltaBarScaleEnabled);
        Assert.Equal(0.35, o.DeltaBarScale);
        Assert.Equal("±0.35 L/v", o.DeltaBarScaleText);
        Assert.True(o.RedComplementSeconds);
        Assert.False(o.ShowAvgInSupport);

        o.ShowDeltaBar = true;
        o.DeltaBarScale = 0.12;
        o.RedComplementSeconds = false;
        o.ShowAvgInSupport = true;
        Assert.True(o.DeltaBarScaleEnabled);
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings.Overlay;
        Assert.True(u.ShowDeltaBar);
        Assert.Equal(0.12, u.DeltaBarScaleLpl);
        Assert.Equal(RedComplement.Pct, u.RedComplement);
        Assert.True(u.ShowAvgInSupport);
    }

    [Theory]
    [InlineData(0.01, 0.05)]
    [InlineData(0.9, 0.50)]
    [InlineData(0.234, 0.23)]
    [InlineData(double.NaN, 0.20)]
    public async Task Delta_bar_scale_is_clamped_to_005_050_in_steps_of_001(double typed, double expected)
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Overlay.DeltaBarScale = typed;
        Assert.Equal(expected, r.Overlay.DeltaBarScale);
        r.Sched.FireAll();
        if (expected == 0.20) { Assert.Empty(r.Conn.Of<UpdateSettings>()); return; } // NaN lands on the default: nothing changed, nothing sent
        Assert.Equal(expected, Assert.Single(r.Conn.Of<UpdateSettings>()).Settings.Overlay.DeltaBarScaleLpl);
    }

    [Fact]
    public async Task Delta_bar_scale_field_accepts_comma_or_dot()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.True(NumberInput.TryParseDouble("0,3", out double a));
        Assert.True(NumberInput.TryParseDouble("0.3", out double b));
        Assert.Equal(a, b);
        r.Overlay.DeltaBarScale = a;
        Assert.Equal(0.3, r.Overlay.DeltaBarScale);
    }

    [Fact]
    public async Task A_pending_layout_edit_survives_the_first_snapshot()
    {
        var r = new SettingsRig();
        await r.Vm.ConnectAsync(); // no snapshot yet
        r.Overlay.DeltaBarScale = 0.4;
        r.Overlay.RedComplementSeconds = true;
        r.Overlay.ShowAvgInSupport = false;
        r.Overlay.ShowDeltaBar = false;
        r.Conn.Raise(new Snapshot(SettingsRig.Snap(new AppSettings())));
        Assert.Equal(0.4, r.Overlay.DeltaBarScale);
        Assert.True(r.Overlay.RedComplementSeconds);
        Assert.False(r.Overlay.ShowAvgInSupport);
        Assert.False(r.Overlay.ShowDeltaBar);
        r.Sched.FireAll();
        var u = Assert.Single(r.Conn.Of<UpdateSettings>()).Settings.Overlay;
        Assert.Equal(0.4, u.DeltaBarScaleLpl);
        Assert.Equal(RedComplement.Seconds, u.RedComplement);
        Assert.False(u.ShowAvgInSupport);
        Assert.False(u.ShowDeltaBar);
    }
}
