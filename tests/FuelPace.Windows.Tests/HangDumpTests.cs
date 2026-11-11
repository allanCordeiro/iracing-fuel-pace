namespace FuelPace.Windows.Tests;

/// <summary>A hang report names the hung thread, shows its stack and who holds the monitor it waits for.</summary>
public sealed class HangDumpTests
{
    sealed class Gate;

    [Fact]
    public void Report_shows_the_hung_thread_and_the_owner_of_its_lock()
    {
        var gate = new Gate();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() => { lock (gate) { held.Set(); release.Wait(); } }) { IsBackground = true };
        owner.Start();
        held.Wait(TestContext.Current.CancellationToken);
        var waiter = new Thread(() => { lock (gate) { } }) { IsBackground = true };
        waiter.Start();
        try
        {
            while ((waiter.ThreadState & ThreadState.WaitSleepJoin) == 0) Thread.Sleep(10);
            var text = HangDump.Stacks("probe", waiter.ManagedThreadId);
            Assert.Contains($"== managed thread {waiter.ManagedThreadId} ", text);
            Assert.Contains("<-- HUNG STA BODY", text);
            Assert.Contains($"Gate: held by managed thread {owner.ManagedThreadId}", text);
            Assert.Contains("Report_shows_the_hung_thread_and_the_owner_of_its_lock", text); // this thread's own frames
        }
        finally
        {
            release.Set();
            owner.Join();
            waiter.Join();
        }
    }
}
