using FuelPace.App.Host;
using FuelPace.App.Ipc;

namespace FuelPace.App.Tests.SettingsUi;

public class ShellViewModelTests
{
    [Fact]
    public void Static_texts_and_sections()
    {
        var r = new SettingsRig();
        Assert.Equal("FuelPace · Configurações", r.Vm.Title);
        Assert.Equal(["Captura", "Overlay e cálculo", "Perfis", "Diagnóstico"], r.Vm.Sections);
        Assert.Equal("Captura", r.Vm.SelectedSection);
    }

    [Fact]
    public async Task Agent_offline_banner_and_launch()
    {
        var r = new SettingsRig();
        r.Conn.Reachable = false;
        await r.Vm.ConnectAsync();
        Assert.False(r.Vm.AgentOnline);
        Assert.True(r.Vm.ShowAgentOfflineBanner);
        Assert.Equal("Agent não está rodando", r.Vm.AgentOfflineText);

        r.Vm.IniciarAgentCommand.Execute(null);
        Assert.Equal(1, r.Conn.Launches);

        // the window keeps trying: once the Agent is up it connects by itself
        Assert.Equal(1, r.Sched.Pending);
        r.Conn.Reachable = true;
        r.Sched.FireAll();
        Assert.True(r.Vm.AgentOnline);
        Assert.False(r.Vm.ShowAgentOfflineBanner);
        Assert.IsType<Hello>(r.Conn.Sent[0]);
    }

    [Fact]
    public async Task Offline_controls_do_not_send_and_do_not_throw()
    {
        var r = new SettingsRig();
        r.Conn.Reachable = false;
        await r.Vm.ConnectAsync();
        r.Capture.Capturing = true;
        r.Overlay.ScalePercent = 150;
        r.Sched.FireAll();
        Assert.Empty(r.Conn.Sent);
    }

    [Fact]
    public async Task Fullscreen_banner_follows_the_snapshot()
    {
        var r = new SettingsRig();
        await r.ConnectAsync(SettingsRig.Snap(fullscreen: true));
        Assert.True(r.Vm.ShowFullscreenBanner);
        Assert.Equal(AgentHost.FullscreenText, r.Vm.FullscreenText);
        r.Conn.Raise(new Snapshot(SettingsRig.Snap(fullscreen: false)));
        Assert.False(r.Vm.ShowFullscreenBanner);
    }

    [Fact]
    public async Task Reconnect_restores_state_from_snapshot()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        Assert.Equal(9, r.Capture.Laps.Count);
        Assert.True(r.Capture.Capturing);

        r.Conn.Drop();
        Assert.False(r.Vm.AgentOnline);
        Assert.True(r.Vm.ShowAgentOfflineBanner);
        Assert.Equal(1, r.Sched.Pending);

        // the Agent kept capturing meanwhile: two more laps
        var laps = SettingsRig.MockLaps().Append(SettingsRig.Lap(24, 80.0, 2.38, 64)).Append(SettingsRig.Lap(25, 80.1, 2.39, 64)).ToArray();
        r.Sched.FireAll();
        Assert.True(r.Vm.AgentOnline);
        Assert.IsType<Hello>(r.Conn.Sent[^1]);
        r.Conn.Raise(new Snapshot(SettingsRig.Snap(laps: laps)));
        Assert.Equal(11, r.Capture.Laps.Count);
        Assert.Equal("8 válidas / 11", r.Capture.CounterText);
        Assert.True(r.Capture.Capturing);
        Assert.Equal("0.1.0+test", r.Diagnostics.Version);
        Assert.Contains(r.Conn.Sent, m => m is ListProfiles);
    }

    [Fact]
    public async Task Error_message_from_the_agent_is_shown()
    {
        var r = new SettingsRig();
        await r.ConnectAsync();
        r.Conn.Raise(new Error("Falha ao alterar o perfil: x"));
        Assert.Equal("Falha ao alterar o perfil: x", r.Vm.Message);
    }
}
