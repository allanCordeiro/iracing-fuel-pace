using FuelPace.App.Ipc;

namespace FuelPace.App.Tests.SettingsUi;

public class CaptureViewModelTests
{
    [Fact]
    public async Task Capture_toggle_sends_set_capture()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(capturing: false));
        Assert.False(r.Capture.Capturing);
        Assert.Empty(r.Conn.Of<SetCapture>()); // a snapshot is not a user action

        r.Capture.Capturing = true;
        r.Capture.Capturing = false;
        Assert.Equal([true, false], r.Conn.Of<SetCapture>().Select(c => c.On));

        r.Conn.Raise(new Snapshot(SettingsRig.Snap(capturing: true)));
        Assert.True(r.Capture.Capturing);
        Assert.Equal(2, r.Conn.Of<SetCapture>().Count);
    }

    [Fact]
    public async Task Header_lines_and_counter_match_the_mock()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.Equal("Ligier JS P320 · Zolder Circuit (Alternate) · Practice", r.Capture.SessionLine);
        Assert.Equal("6 válidas / 9", r.Capture.CounterText);
        Assert.Equal("Capturando", r.Capture.StatusText);
    }

    [Fact]
    public async Task Lap_rows_follow_capture_lap_added()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(laps: []));
        Assert.Empty(r.Capture.Laps);
        Assert.Equal("0 válidas / 0", r.Capture.CounterText);

        r.Conn.Raise(new CaptureLapAdded(SettingsRig.Lap(19, 93.175, 2.388, 55, "incident", "offtrack"), 0, 1));
        var row = Assert.Single(r.Capture.Laps);
        Assert.Equal(19, row.Lap);
        Assert.Equal("1:33.175", row.Time);
        Assert.Equal("2.388", row.Fuel);
        Assert.Equal("55%", row.FullThrottle);
        Assert.Equal("inválida · fora da pista, incidente", row.Status);
        Assert.Equal("Incluir", row.ActionText);

        r.Conn.Raise(new CaptureLapAdded(SettingsRig.Lap(20, 79.133, 2.386, 65), 1, 2));
        Assert.Equal(2, r.Capture.Laps.Count);
        Assert.Equal("válida", r.Capture.Laps[1].Status);
        Assert.Equal("Excluir", r.Capture.Laps[1].ActionText);
        Assert.Equal("1 válidas / 2", r.Capture.CounterText);

        // a new capture starts at total 1: the table starts over
        r.Conn.Raise(new CaptureLapAdded(SettingsRig.Lap(30, 80, 2.4, 60), 1, 1));
        Assert.Equal(30, Assert.Single(r.Capture.Laps).Lap);
    }

    [Fact]
    public async Task Row_without_time_shows_a_dash_and_in_progress_row_is_separate()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.Equal("—", r.Capture.Laps[0].Time);
        Assert.Equal("inválida · pit, incidente", r.Capture.Laps[0].Status);
        var cur = r.Capture.InProgress!;
        Assert.Equal(24, cur.Lap);
        Assert.Equal("em andamento", cur.Status);
        Assert.Equal("…", cur.Time);
        Assert.Equal("", cur.ActionText);

        r.Conn.Raise(new Snapshot(SettingsRig.Snap(capturing: false)));
        Assert.Null(r.Capture.InProgress);
    }

    [Fact]
    public async Task Economy_lap_is_labelled()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.Equal("válida · economia", r.Capture.Laps.Single(l => l.Lap == 23).Status);
        foreach (var l in r.Capture.Laps.Where(l => l.Lap is 16 or 17 or 18 or 21 or 22))
            Assert.Equal("válida", l.Status);
    }

    [Fact]
    public async Task Summary_cards_match_mock_text()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        var c = r.Capture;
        Assert.Equal("CONSUMO MÉDIO", c.ConsumptionTitle);
        Assert.Equal("2.373", c.ConsumptionValue);
        Assert.Equal("L/v", c.ConsumptionUnit);
        Assert.Equal("mediana 2.386 · mín 2.289", c.ConsumptionDetail);
        Assert.Equal("TEMPO MÉDIO", c.TimeTitle);
        Assert.Equal("1:20.140", c.TimeValue);
        Assert.Equal("mediana 1:20.118 · melhor 1:19.134", c.TimeDetail);
        Assert.Equal("AUTONOMIA", c.AutonomyTitle);
        Assert.Equal("42.1", c.AutonomyValue);
        Assert.Equal("v · 56 min", c.AutonomyUnit);
        Assert.Equal("tanque 100 L", c.AutonomyDetail);
        Assert.Equal("BASELINE", c.BaselineTitle);
        Assert.Equal("Confiança média", c.BaselineValue);
        Assert.Equal("6 voltas válidas · ideal ≥ 10", c.BaselineDetail);
    }

    [Fact]
    public async Task Summary_without_enough_valid_laps_is_a_dash()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(laps: [SettingsRig.Lap(1, 80, 2.4, 60), SettingsRig.Lap(2, 81, 2.5, 60)]));
        Assert.Equal("—", r.Capture.ConsumptionValue);
        Assert.Equal("—", r.Capture.TimeValue);
        Assert.Equal("—", r.Capture.AutonomyValue);
        Assert.Equal("Sem baseline", r.Capture.BaselineValue);
        Assert.Equal("2 voltas válidas · mínimo 3", r.Capture.BaselineDetail);
    }

    [Fact]
    public async Task Exclude_sends_profile_command()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(capturing: false, captureId: "2026-10-29T20:03:40Z"));
        var valid = r.Capture.Laps.Single(l => l.Lap == 16);
        var invalid = r.Capture.Laps.Single(l => l.Lap == 20);

        valid.ProfileCommand.Execute(null);
        invalid.ProfileCommand.Execute(null);

        Assert.Equal(
            [new ProfileCommand("ligier__zolder", "setLapExcluded", "2026-10-29T20:03:40Z", 16, true),
             new ProfileCommand("ligier__zolder", "setLapExcluded", "2026-10-29T20:03:40Z", 20, false)],
            r.Conn.Of<ProfileCommand>());
    }

    [Fact]
    public async Task Lap_actions_wait_for_the_capture_to_be_saved()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(); // capturing, no id yet
        var row = r.Capture.Laps[1];
        Assert.False(row.ProfileCommand.CanExecute(null));
        row.ProfileCommand.Execute(null);
        Assert.Empty(r.Conn.Of<ProfileCommand>());

        r.Conn.Raise(new CaptureFinished("ligier__zolder", "cap-1"));
        Assert.False(r.Capture.Capturing);
        Assert.True(row.ProfileCommand.CanExecute(null));
        row.ProfileCommand.Execute(null);
        Assert.Equal("cap-1", Assert.Single(r.Conn.Of<ProfileCommand>()).CaptureId);
    }

    [Fact]
    public async Task Edited_profile_updates_the_rows_and_counter()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(capturing: false, captureId: "cap-1"));
        var json = ProfileJson.Doc("cap-1", [
            ("1", 15, null, 2.378, 51, 1 | 2), (null, 16, 80.897, 2.382, 64, 256), (null, 17, 80.272, 2.404, 64, 0)]);
        r.Conn.Raise(new Profile("ligier__zolder", json));
        Assert.Equal([15, 16, 17], r.Capture.Laps.Select(l => l.Lap));
        Assert.Equal("inválida · excluída", r.Capture.Laps[1].Status);
        Assert.Equal("Incluir", r.Capture.Laps[1].ActionText);
        Assert.Equal("1 válidas / 3", r.Capture.CounterText);

        // another profile's document is not ours
        r.Conn.Raise(new Profile("other__track", ProfileJson.Doc("cap-1", [(null, 99, 80.0, 2.0, 60, 0)])));
        Assert.Equal(3, r.Capture.Laps.Count);
    }
}
