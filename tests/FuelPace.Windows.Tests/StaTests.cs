namespace FuelPace.Windows.Tests;

/// <summary>Sta.Run gives every WPF body its own STA thread and never runs two at once (WPF's process-wide state does not
/// survive two UI threads working together).</summary>
public sealed class StaTests
{
    [Fact]
    public void Bodies_started_together_run_one_at_a_time_each_on_its_own_STA_thread()
    {
        int running = 0, most = 0;
        var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var go = new Barrier(4);
        var errors = new Exception?[4];
        var callers = Enumerable.Range(0, 4).Select(i => new Thread(() =>
        {
            go.SignalAndWait();
            try
            {
                Sta.Run(() =>
                {
                    Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                    threads.Add(Environment.CurrentManagedThreadId);
                    int now = Interlocked.Increment(ref running);
                    InterlockedMax(ref most, now);
                    Thread.Sleep(100);
                    Interlocked.Decrement(ref running);
                });
            }
            catch (Exception e) { errors[i] = e; }
        }) { IsBackground = true }).ToArray();
        foreach (var c in callers) c.Start();
        foreach (var c in callers) c.Join();
        Assert.All(errors, e => Assert.Null(e));
        Assert.Equal(1, most);
        Assert.Equal(4, threads.Distinct().Count());
    }

    static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
