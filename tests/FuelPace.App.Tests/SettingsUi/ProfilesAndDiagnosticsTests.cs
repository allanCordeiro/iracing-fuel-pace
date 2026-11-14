using System.Collections.Specialized;
using FuelPace.App.Ipc;

namespace FuelPace.App.Tests.SettingsUi;

public class ProfilesViewModelTests
{
    static readonly ProfileSummary Ligier = new("ligier__zolder", "Ligier JS P320", "Zolder Circuit (Alternate)", 2, 14, "Media");
    static readonly ProfileSummary Other = new("gt3__spa", "BMW M4 GT3", "Spa-Francorchamps", 1, 3, null);

    [Fact]
    public async Task Profile_list_is_requested_on_connect_and_formatted()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.Contains(r.Conn.Sent, m => m is ListProfiles);
        r.Conn.Raise(new ProfileList([Ligier, Other]));
        Assert.Equal(["Ligier JS P320 · Zolder Circuit (Alternate) · 2 capturas · Confiança média", "BMW M4 GT3 · Spa-Francorchamps · 1 captura · sem baseline"],
            r.Profiles.Items.Select(i => i.Text));
    }

    [Fact]
    public async Task Selecting_loads_the_captures()
    {
        var r = new SettingsRig();
        r.Profiles.TimeZone = TimeZoneInfo.Utc;
        await r.ConnectAsync();
        r.Conn.Raise(new ProfileList([Ligier]));
        r.Profiles.SelectedItem = r.Profiles.Items[0];
        Assert.Equal(new GetProfile("ligier__zolder"), Assert.Single(r.Conn.Of<GetProfile>()));

        r.Conn.Raise(new Profile("ligier__zolder", ProfileJson.Doc("2026-10-29T20:03:40Z",
            [(null, 1, 80.0, 2.4, 60, 0), (null, 2, 81.0, 2.4, 60, 0), (null, 3, 82.0, 2.4, 60, 64)])));
        var c = Assert.Single(r.Profiles.Captures);
        Assert.Equal("29/10/2026 20:03", c.Date);
        Assert.Equal("Practice", c.SessionType);
        Assert.Equal("pista 31 °C · ar 24 °C · seca", c.Conditions);
        Assert.Equal("2 válidas / 3", c.Laps);
        Assert.True(c.Active);
    }

    [Fact]
    public async Task Toggle_active_and_confirmed_delete_send_commands()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Conn.Raise(new ProfileList([Ligier]));
        r.Profiles.SelectedItem = r.Profiles.Items[0];
        r.Conn.Raise(new Profile("ligier__zolder", ProfileJson.Doc("cap-1", [(null, 1, 80.0, 2.4, 60, 0)])));
        var c = r.Profiles.Captures[0];

        c.Active = false;
        Assert.Equal(new ProfileCommand("ligier__zolder", "setCaptureActive", "cap-1", null, false), Assert.Single(r.Conn.Of<ProfileCommand>()));

        c.ApagarCommand.Execute(null);
        Assert.Same(c, r.Profiles.PendingDelete);
        Assert.Single(r.Conn.Of<ProfileCommand>()); // nothing yet: it asks first
        r.Profiles.CancelDeleteCommand.Execute(null);
        Assert.Null(r.Profiles.PendingDelete);

        c.ApagarCommand.Execute(null);
        r.Profiles.ConfirmDeleteCommand.Execute(null);
        Assert.Equal(new ProfileCommand("ligier__zolder", "deleteCapture", "cap-1", null, null), r.Conn.Of<ProfileCommand>()[^1]);
        Assert.Null(r.Profiles.PendingDelete);
    }

    [Fact]
    public async Task Incoming_profile_refreshes_the_selected_captures_without_echoing_a_command()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Conn.Raise(new ProfileList([Ligier]));
        r.Profiles.SelectedItem = r.Profiles.Items[0];
        r.Conn.Raise(new Profile("ligier__zolder", ProfileJson.Doc("cap-1", [(null, 1, 80.0, 2.4, 60, 0)])));
        r.Conn.Raise(new Profile("ligier__zolder", ProfileJson.Doc("cap-1", [(null, 1, 80.0, 2.4, 60, 0)], active: false)));
        Assert.False(r.Profiles.Captures[0].Active);
        Assert.Empty(r.Conn.Of<ProfileCommand>());

        // deleted: the capture is gone and the list is requested again
        int lists = r.Conn.Of<ListProfiles>().Count;
        r.Conn.Raise(new Profile("ligier__zolder", ProfileJson.Doc("cap-2", [(null, 1, 80.0, 2.4, 60, 0)])));
        Assert.Equal("cap-2", Assert.Single(r.Profiles.Captures).Id);
        Assert.True(r.Conn.Of<ListProfiles>().Count > lists);
    }

    [Fact] // feedback 2026-11-13: the list that the profile's arrival asks for emptied the captures table
    public async Task Captures_stay_when_the_list_is_refreshed()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        // What WPF's ListBox does: clearing its items drops the selection, through the two-way binding.
        r.Profiles.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) r.Profiles.SelectedItem = null;
        };
        r.Conn.Raise(new ProfileList([Ligier, Other]));
        r.Profiles.SelectedItem = r.Profiles.Items[0];
        r.Conn.Raise(new Profile("ligier__zolder", ProfileJson.Doc("cap-1", [(null, 1, 80.0, 2.4, 60, 0)])));
        Assert.Equal("cap-1", Assert.Single(r.Profiles.Captures).Id);

        r.Conn.Raise(new ProfileList([Ligier with { Captures = 3 }, Other])); // the reply to the refresh: the counts changed
        Assert.Equal("ligier__zolder", r.Profiles.SelectedItem?.Key);
        Assert.Contains("3 capturas", r.Profiles.SelectedItem!.Text);
        Assert.Equal("cap-1", Assert.Single(r.Profiles.Captures).Id);
        Assert.Single(r.Conn.Of<GetProfile>()); // the selection did not really change: nothing is asked again

        // the selected profile is gone (its file was deleted): nothing selected, no captures
        r.Conn.Raise(new ProfileList([Other]));
        Assert.Null(r.Profiles.SelectedItem);
        Assert.Empty(r.Profiles.Captures);
    }

    [Fact]
    public async Task Finished_capture_refreshes_the_list()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        int lists = r.Conn.Of<ListProfiles>().Count;
        r.Conn.Raise(new CaptureFinished("ligier__zolder", "cap-1"));
        Assert.Equal(lists + 1, r.Conn.Of<ListProfiles>().Count);
    }
}

public class DiagnosticsViewModelTests
{
    [Fact]
    public async Task Debug_toggle_sends_set_debug_mode()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap() with { DebugMode = false, OverlayBackend = "layered" });
        var d = r.Diagnostics;
        Assert.Equal("0.1.0+test", d.Version);
        Assert.Equal("layered", d.OverlayBackend);
        Assert.False(d.DebugMode);
        Assert.Empty(r.Conn.Of<SetDebugMode>());
        d.DebugMode = true;
        Assert.Equal(new SetDebugMode(true), Assert.Single(r.Conn.Of<SetDebugMode>()));
    }

    [Theory] // the debug zip's marker (or --debug) forces debug: the toggle shows it, says why and cannot be unchecked
    [InlineData("marker", "Ativado pelo arquivo FuelPace.debug")]
    [InlineData("commandLine", "Ativado por --debug")]
    public async Task Forced_debug_mode_disables_the_toggle_and_says_why(string by, string text)
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap() with { DebugMode = true, DebugForcedBy = by });
        var d = r.Diagnostics;
        Assert.True(d.DebugMode);
        Assert.True(d.DebugForced);
        Assert.False(d.DebugToggleEnabled);
        Assert.Equal(text, d.DebugForcedText);

        r.Conn.Raise(new Snapshot(SettingsRig.Snap() with { DebugMode = false, DebugForcedBy = null }));
        Assert.False(d.DebugForced);
        Assert.True(d.DebugToggleEnabled);
        Assert.Null(d.DebugForcedText);
    }

    [Fact]
    public async Task Bundle_creation_shows_the_path_and_opens_the_folder()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        var d = r.Diagnostics;
        d.IncludeRecordings = false;
        d.GerarPacoteCommand.Execute(null);
        Assert.Equal(new CreateBundle(false), Assert.Single(r.Conn.Of<CreateBundle>()));
        Assert.True(d.Busy);

        var zip = Path.Combine(Path.GetTempPath(), "bundle", "FuelPace-diag-20261110-101010.zip");
        r.Conn.Raise(new BundleCreated(zip));
        Assert.False(d.Busy);
        Assert.Equal(zip, d.BundlePath);
        d.AbrirPastaDoPacoteCommand.Execute(null);
        Assert.Equal(Path.GetDirectoryName(zip), Assert.Single(r.Shell.Opened));

        d.AbrirPastaDeLogsCommand.Execute(null);
        Assert.Equal("/logs", r.Shell.Opened[^1]);
    }

    [Fact]
    public async Task Bundle_error_ends_busy_and_shows_the_message()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Diagnostics.IncludeRecordings = true;
        r.Diagnostics.GerarPacoteCommand.Execute(null);
        Assert.True(r.Conn.Of<CreateBundle>()[0].IncludeRecordings);
        r.Conn.Raise(new Error("Falha ao gerar o pacote de diagnóstico (IOException)."));
        Assert.False(r.Diagnostics.Busy);
        Assert.Null(r.Diagnostics.BundlePath);
        Assert.Equal("Falha ao gerar o pacote de diagnóstico (IOException).", r.Vm.Message);
    }
}
