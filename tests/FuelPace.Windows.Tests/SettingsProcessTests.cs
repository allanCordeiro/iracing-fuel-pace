using System.Diagnostics;

namespace FuelPace.Windows.Tests;

/// <summary>The real FuelPace.Settings.exe: one window per session, and closing it ends the process (PERF-07).</summary>
[Collection(AgentProcessCollection.Name)] // the Settings mutex is session-wide and the window looks for the Agent's pipe
public sealed class SettingsProcessTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static Process Start()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "FuelPace.Settings.exe");
        Assert.True(File.Exists(exe), "missing " + exe);
        return Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })!;
    }

    static IntPtr WaitForWindow(Process p)
    {
        var end = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < end)
        {
            p.Refresh();
            if (p.HasExited) Assert.Fail($"Settings exited with {p.ExitCode} before showing its window");
            if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            Thread.Sleep(100);
        }
        throw new TimeoutException("the Settings window did not appear");
    }

    [Fact]
    public void A_second_launch_hands_over_to_the_open_window_and_closing_it_ends_the_process()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var first = Start();
        try
        {
            WaitForWindow(first);
            Assert.Equal("FuelPace · Configurações", first.MainWindowTitle);

            using var second = Start();
            Assert.True(second.WaitForExit((int)Timeout.TotalMilliseconds), "the second Settings instance did not exit");
            Assert.Equal(0, second.ExitCode);
            first.Refresh();
            Assert.False(first.HasExited);

            Assert.True(first.CloseMainWindow());
            Assert.True(first.WaitForExit((int)Timeout.TotalMilliseconds), "closing the window did not end the process");
            Assert.Equal(0, first.ExitCode);
        }
        finally
        {
            if (!first.HasExited) first.Kill();
        }
    }
}
