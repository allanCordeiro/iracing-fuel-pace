using System.Collections.Concurrent;
using FuelPace.Core.Diagnostics;

namespace FuelPace.Core.Storage;

/// <summary>
/// Single dedicated low-priority thread running queued actions in order (profile I/O stays off the telemetry thread).
/// <see cref="Dispose"/> is idempotent and may be called from a queued action; work enqueued after it is dropped and logged.
/// </summary>
public sealed class BackgroundWorkQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly object _idleLock = new();
    private readonly DiagLog _log;
    private int _pending;
    private int _disposed;

    public Action<Exception>? OnError { get; set; }

    public BackgroundWorkQueue(string threadName, DiagLog? log = null)
    {
        _log = log ?? DiagLog.Null;
        _thread = new Thread(Run) { Name = threadName, IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Queues <paramref name="work"/>; false (and an Error log line) when the queue is already disposed.</summary>
    public bool Enqueue(Action work)
    {
        Interlocked.Increment(ref _pending);
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _queue.Add(work);
                return true;
            }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { /* disposed meanwhile */ }
        }
        Done();
        _log.Text(DiagLevel.Error, "storage", $"work dropped: queue '{_thread.Name}' is disposed");
        return false;
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

    /// <summary>Stops accepting work, runs what is queued, and joins the thread (not when called from it: the thread then
    /// finishes the queue and exits on its own). Later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join();
    }

    private void Run()
    {
        try
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
        finally
        {
            // The collection is disposed by its only consumer, after the last item: never under a running enumerator.
            _queue.Dispose();
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
