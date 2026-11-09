using System.Collections.Concurrent;

namespace FuelPace.Core.Diagnostics;

public sealed class DiagLog
{
    private const int MaxQueuedTexts = 4096;

    public static readonly DiagLog Null = new(DiagLevel.Off, 1);

    private readonly DiagRecord[] _ring;
    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private readonly ConcurrentQueue<DiagText> _texts = new();
    private int _textCount;
    private int _head, _count;
    private long _seq;
    private long _dropped;
    private volatile int _level;

    public DiagLog(DiagLevel level, int capacity = 16384, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new DiagRecord[capacity];
        _clock = clock ?? TimeProvider.System;
        _level = (int)level;
    }

    public DiagLevel Level
    {
        get => (DiagLevel)_level;
        set => _level = (int)value;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool IsEnabled(DiagLevel l) => l != DiagLevel.Off && (int)l <= _level;

    public void Emit(DiagLevel l, DiagEvent e, double sessionTime, int i0 = 0, int i1 = 0,
        double a = double.NaN, double b = double.NaN, double c = double.NaN, double d = double.NaN)
    {
        if (!IsEnabled(l)) return;
        long ticks = _clock.GetUtcNow().UtcTicks;
        using (_lock.EnterScope())
        {
            if (_count == _ring.Length) { _dropped++; return; }
            ref var r = ref _ring[(_head + _count) % _ring.Length];
            r.Seq = ++_seq;
            r.WallTicksUtc = ticks;
            r.SessionTime = sessionTime;
            r.Event = e;
            r.Level = l;
            r.I0 = i0; r.I1 = i1;
            r.A = a; r.B = b; r.C = c; r.D = d;
            _count++;
        }
    }

    /// <summary>Cold path; may allocate.</summary>
    public void Text(DiagLevel l, string category, string message)
    {
        if (!IsEnabled(l)) return;
        if (Interlocked.Increment(ref _textCount) > MaxQueuedTexts)
        {
            Interlocked.Decrement(ref _textCount);
            Interlocked.Increment(ref _dropped);
            return;
        }
        long seq = Interlocked.Increment(ref _seq);
        _texts.Enqueue(new DiagText(seq, _clock.GetUtcNow().UtcTicks, l, category, message));
    }

    public void Exception(string category, Exception ex) => Text(DiagLevel.Error, category, ex.ToString());

    public int DrainRecords(Span<DiagRecord> dest)
    {
        using (_lock.EnterScope())
        {
            int n = Math.Min(dest.Length, _count);
            for (int i = 0; i < n; i++)
                dest[i] = _ring[(_head + i) % _ring.Length];
            _head = (_head + n) % _ring.Length;
            _count -= n;
            return n;
        }
    }

    public bool TryDequeueText(out DiagText t)
    {
        if (_texts.TryDequeue(out t))
        {
            Interlocked.Decrement(ref _textCount);
            return true;
        }
        return false;
    }
}
