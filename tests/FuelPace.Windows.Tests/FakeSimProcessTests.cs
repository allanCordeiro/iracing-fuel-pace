using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace FuelPace.Windows.Tests;

/// <summary>
/// The real FuelPace.FakeSim.exe never writes into shared memory it did not create: with iRacing open its default names
/// exist, and opening them would overwrite the live telemetry every tool reads. Private names here, so a real iRacing on the
/// machine is never touched.
/// </summary>
public sealed class FakeSimProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (int Code, string Stderr) Run(string map, string ev)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "FuelPace.FakeSim.exe");
        Assert.True(File.Exists(exe), "missing " + exe);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "--synthetic-race", "1", "--speed", "max", "--map", map, "--event", ev }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(Timeout))
        {
            p.Kill();
            Assert.Fail("FakeSim did not exit");
        }
        return (p.ExitCode, stderr.Result);
    }

    private static string Name(string kind) => @"Local\FuelPace.Test." + kind + "." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Refuses_a_mapping_that_already_exists_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        string map = Name("Map");
        using var existing = MemoryMappedFile.CreateNew(map, 1 << 20); // "iRacing's" mapping, bigger than the FakeSim layout
        using var view = existing.CreateViewAccessor();

        var (code, stderr) = Run(map, Name("Event"));

        Assert.Equal(2, code);
        Assert.Contains("already exists", stderr, StringComparison.Ordinal);
        Assert.Contains("iRacing", stderr, StringComparison.Ordinal);
        var head = new byte[4096];
        view.ReadArray(0, head, 0, head.Length);
        Assert.All(head, b => Assert.Equal(0, b)); // no header, no YAML: untouched
    }

    [Fact]
    public void Refuses_an_event_that_already_exists()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        string ev = Name("Event");
        using var existing = new EventWaitHandle(false, EventResetMode.ManualReset, ev);

        var (code, stderr) = Run(Name("Map"), ev);

        Assert.Equal(2, code);
        Assert.Contains("already exists", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_on_free_names()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var (code, stderr) = Run(Name("Map"), Name("Event"));
        Assert.True(code == 0, "exit " + code + ": " + stderr);
    }
}
