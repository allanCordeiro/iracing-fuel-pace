namespace FuelPace.App.Tests.Fakes;

/// <summary>
/// A clock the test (or a fake source, one frame at a time) moves by hand, with timers that fire inside
/// <see cref="Advance"/> in due order, on the thread that advances. Timestamps are UTC ticks.
/// </summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _advance = new();
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer(this, callback, state);
        lock (_gate) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        lock (_advance)
        {
            long target = Interlocked.Read(ref _ticks) + by.Ticks;
            while (true)
            {
                FakeTimer? next = null;
                lock (_gate)
                    foreach (var t in _timers)
                        if (t.Due <= target && (next is null || t.Due < next.Due)) next = t;
                if (next is null) break;
                long due = next.Due;
                if (due > Interlocked.Read(ref _ticks)) Interlocked.Exchange(ref _ticks, due);
                next.Fire();
            }
            Interlocked.Exchange(ref _ticks, target);
        }
    }

    private sealed class FakeTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long _period;
        public long Due { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + Math.Max(0, dueTime.Ticks);
                _period = period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero ? 0 : period.Ticks;
            }
            return true;
        }

        public void Fire()
        {
            lock (clock._gate) Due = _period > 0 ? Due + _period : long.MaxValue;
            callback(state);
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                Due = long.MaxValue;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
