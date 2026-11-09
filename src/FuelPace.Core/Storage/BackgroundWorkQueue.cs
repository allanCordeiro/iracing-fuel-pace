using System.Collections.Concurrent;

namespace FuelPace.Core.Storage;

/// <summary>Single dedicated low-priority thread running queued actions in order (profile I/O stays off the telemetry thread).</summary>
public sealed class BackgroundWorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly object _idleLock = new();
    private int _pending;

    public Action<Exception>? OnError { get; set; }

    public BackgroundWorkQueue(string threadName)
    {
        _thread = new Thread(Run) { Name = threadName, IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Enqueue(Action work)
    {
        Interlocked.Increment(ref _pending);
        try { _queue.Add(work); }
        catch (InvalidOperationException) { Done(); throw new ObjectDisposedException(nameof(BackgroundWorkQueue)); }
    }

    /// <summary>True when everything enqueued so far has finished, false on timeout.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        lock (_idleLock)
        {
            while (Volatile.Read(ref _pending) > 0)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                Monitor.Wait(_idleLock, left);
            }
        }
        return true;
    }

    /// <summary>Stops accepting work, runs what is queued, and joins the thread.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join();
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception e)
            {
                try { OnError?.Invoke(e); } catch { /* the queue must keep going */ }
            }
            Done();
        }
    }

    private void Done()
    {
        lock (_idleLock)
        {
            Interlocked.Decrement(ref _pending);
            Monitor.PulseAll(_idleLock);
        }
    }
}
