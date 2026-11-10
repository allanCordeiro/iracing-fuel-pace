using System.Diagnostics;

namespace FuelPace.Windows.Tests;

[Collection(AgentProcessCollection.Name)] // the Agent's mutex and pipe are machine-wide
public class SmokeTests
{
    [Fact]
    public void Agent_starts_and_exits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var exe = Path.Combine(AppContext.BaseDirectory, "FuelPace.Agent.exe");
        Assert.True(File.Exists(exe), "missing " + exe);

        using var p = Process.Start(new ProcessStartInfo(exe, "--exit-after 2") { UseShellExecute = false })!;
        Assert.True(p.WaitForExit(30_000), "Agent did not exit");
        Assert.Equal(0, p.ExitCode);
    }
}
