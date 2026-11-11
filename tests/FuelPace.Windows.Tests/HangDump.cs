using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace FuelPace.Windows.Tests;

/// <summary>
/// What a hung test leaves behind: the managed stack of every thread of this process (the hung one marked) and every held
/// monitor with its owner, read from a snapshot of the process (ClrMD), to <c>artifacts/settings/hang-&lt;test&gt;-&lt;time&gt;.txt</c>
/// (CI uploads it). Never throws: a failed snapshot is written as such.
/// </summary>
internal static class HangDump
{
    public static string Write(string test, int hungManagedThreadId)
    {
        var dir = Path.Combine(SettingsScreenshotTests.RepoRoot(), "artifacts", "settings");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"hang-{test}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.txt");
        string text;
        try { text = Stacks(test, hungManagedThreadId); }
        catch (Exception e) { text = $"{test}: snapshot of process {Environment.ProcessId} failed\n{e}"; }
        File.WriteAllText(path, text);
        return path;
    }

    internal static string Stacks(string test, int hung)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{test}: STA body did not finish; process {Environment.ProcessId}, {DateTime.UtcNow:O}, hung managed thread {hung}");
        using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var owners = new Dictionary<ulong, ClrThread>();
        foreach (var t in runtime.Threads) owners[t.Address] = t;

        sb.AppendLine().AppendLine("== held monitors (object, owner, recursion, waiters)");
        foreach (var s in runtime.Heap.EnumerateSyncBlocks())
        {
            if (!s.IsMonitorHeld) continue;
            var type = runtime.Heap.GetObjectType(s.Object)?.Name ?? "?";
            var owner = owners.TryGetValue(s.HoldingThreadAddress, out var o) ? $"managed thread {o.ManagedThreadId}" : $"0x{s.HoldingThreadAddress:X}";
            sb.AppendLine($"0x{s.Object:X} {type}: held by {owner}, recursion {s.RecursionCount}, {s.WaitingThreadCount} waiting");
        }

        foreach (var t in runtime.Threads)
        {
            if (!t.IsAlive) continue;
            sb.AppendLine().AppendLine($"== managed thread {t.ManagedThreadId} (OS 0x{t.OSThreadId:X}){(t.ManagedThreadId == hung ? "  <-- HUNG STA BODY" : "")}, " +
                $"state {t.State}, locks {t.LockCount}");
            foreach (var f in t.EnumerateStackTrace())
                sb.AppendLine("   " + (f.Method?.Signature ?? f.FrameName ?? f.Kind.ToString()));
        }
        return sb.ToString();
    }
}
