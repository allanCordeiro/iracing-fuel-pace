using FuelPace.Core.Storage;

namespace FuelPace.Tests.Storage;

public class BackgroundWorkQueueTests
{
    [Fact]
    public void Runs_in_order_on_a_background_thread()
    {
        using var q = new BackgroundWorkQueue("test-queue");
        var order = new List<int>(); var threads = new List<int>(); var names = new List<string?>();
        for (int i = 0; i < 3; i++)
        {
            int n = i;
            q.Enqueue(() => { order.Add(n); threads.Add(Environment.CurrentManagedThreadId); names.Add(Thread.CurrentThread.Name); });
        }
        Assert.True(q.WaitIdle(TimeSpan.FromSeconds(5)));
        Assert.Equal([0, 1, 2], order);
        Assert.All(threads, t => Assert.NotEqual(Environment.CurrentManagedThreadId, t));
        Assert.All(names, n => Assert.Equal("test-queue", n));
    }

    [Fact]
    public void Exception_does_not_stop_queue()
    {
        using var q = new BackgroundWorkQueue("test-queue");
        Exception? seen = null; bool ran = false;
        q.OnError = e => seen = e;
        q.Enqueue(() => throw new InvalidOperationException("boom"));
        q.Enqueue(() => ran = true);
        Assert.True(q.WaitIdle(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(seen);
        Assert.True(ran);
    }

    [Fact]
    public void Dispose_drains_pending_work()
    {
        int count = 0;
        var q = new BackgroundWorkQueue("test-queue");
        for (int i = 0; i < 20; i++) q.Enqueue(() => { Thread.Sleep(1); Interlocked.Increment(ref count); });
        q.Dispose();
        Assert.Equal(20, count);
    }

    [Fact]
    public void WaitIdle_times_out_while_busy()
    {
        using var q = new BackgroundWorkQueue("test-queue");
        using var gate = new ManualResetEventSlim();
        q.Enqueue(() => gate.Wait());
        Assert.False(q.WaitIdle(TimeSpan.FromMilliseconds(50)));
        gate.Set();
        Assert.True(q.WaitIdle(TimeSpan.FromSeconds(5)));
    }
}
