using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Race;

/// <summary>Per-car ring buffers of valid lap times; reports the race leader's pace (PRD 7.3). Zero allocation per Update.</summary>
public sealed class LeaderPaceTracker
{
    public const int MaxCars = 64, MaxHistory = 16;
    private const double TimeoutS = 5.0;
    private const byte FlagPit = 1, FlagNotInWorld = 2, FlagCaution = 4;

    private int _window;
    private readonly DiagLog _log;
    private readonly float[] _times = new float[MaxCars * MaxHistory];
    private readonly int[] _count = new int[MaxCars];
    private readonly int[] _head = new int[MaxCars];
    private readonly int[] _lastLapCompleted = new int[MaxCars];
    private readonly byte[] _flags = new byte[MaxCars];
    private readonly byte[] _pendingFlags = new byte[MaxCars];
    private readonly bool[] _pending = new bool[MaxCars];
    private readonly double[] _pendingSince = new double[MaxCars];
    private readonly float[] _prevLastLapTime = new float[MaxCars];
    private double _lastPace = double.NaN;
    private LeaderPaceSource _prevSource = LeaderPaceSource.None;

    public LeaderPace Current { get; private set; } = LeaderPace.None;

    public LeaderPaceTracker(int meanWindow = 5, DiagLog? log = null)
    {
        CheckWindow(meanWindow);
        _window = meanWindow;
        _log = log ?? DiagLog.Null;
        Reset();
    }

    /// <summary>Changes the mean window (2–16) from the next <see cref="Update"/>; the lap history is kept.</summary>
    public void SetMeanWindow(int meanWindow)
    {
        CheckWindow(meanWindow);
        _window = meanWindow;
    }

    private static void CheckWindow(int meanWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(meanWindow, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(meanWindow, MaxHistory);
    }

    public int ValidLapCount(int carIdx) => (uint)carIdx < MaxCars ? _count[carIdx] : 0;

    public void Reset()
    {
        Array.Clear(_times);
        for (int i = 0; i < MaxCars; i++) ClearCar(i);
        _lastPace = double.NaN;
        _prevSource = LeaderPaceSource.None;
        Current = LeaderPace.None;
    }

    private void ClearCar(int i)
    {
        _count[i] = 0; _head[i] = 0; _lastLapCompleted[i] = -1;
        _flags[i] = 0; _pendingFlags[i] = 0; _pending[i] = false;
        _pendingSince[i] = 0; _prevLastLapTime[i] = 0;
    }

    public void Update(in TelemetryFrame f)
    {
        double now = f.SessionTime;
        bool caution = (f.SessionFlags & LapValidator.CautionMask) != 0;
        int leader = -1;
        for (int i = 0; i < MaxCars; i++)
        {
            if (leader < 0 && f.CarIdxPosition[i] == 1) leader = i;
            UpdateCar(i, in f, now, caution);
        }
        Publish(in f, leader, caution);
    }

    private void UpdateCar(int i, in TelemetryFrame f, double now, bool caution)
    {
        int lc = f.CarIdxLapCompleted[i];
        float llt = f.CarIdxLastLapTime[i];
        if (lc < 0)
        {
            if (_lastLapCompleted[i] >= 0) ClearCar(i);
            return;
        }
        if (_lastLapCompleted[i] < 0)
        {
            _lastLapCompleted[i] = lc;
            _prevLastLapTime[i] = llt;
            return;
        }

        byte fl = _flags[i];
        if (f.CarIdxOnPitRoad[i]) fl |= FlagPit;
        if (f.CarIdxTrackSurface[i] < 0) fl |= FlagNotInWorld;
        if (caution) fl |= FlagCaution;
        _flags[i] = fl;

        if (lc != _lastLapCompleted[i])
        {
            if (lc < _lastLapCompleted[i])
            {
                _lastLapCompleted[i] = lc; _flags[i] = 0; _pending[i] = false; _prevLastLapTime[i] = llt;
                return;
            }
            if (_pending[i]) Reject(i, 5, _prevLastLapTime[i], now);
            _pending[i] = true;
            _pendingSince[i] = now;
            _pendingFlags[i] = _flags[i];
            _flags[i] = 0;
            _lastLapCompleted[i] = lc;
        }

        if (_pending[i])
        {
            if (llt != _prevLastLapTime[i])
            {
                _prevLastLapTime[i] = llt;
                _pending[i] = false;
                Resolve(i, llt, now);
            }
            else if (now - _pendingSince[i] > TimeoutS)
            {
                _pending[i] = false;
                Reject(i, 5, llt, now);
            }
        }
        else _prevLastLapTime[i] = llt;
    }

    private void Resolve(int i, float time, double now)
    {
        byte fl = _pendingFlags[i];
        if ((fl & FlagPit) != 0) { Reject(i, 0, time, now); return; }
        if ((fl & FlagNotInWorld) != 0) { Reject(i, 1, time, now); return; }
        if ((fl & FlagCaution) != 0) { Reject(i, 2, time, now); return; }
        if (!(time > 0f)) { Reject(i, 3, time, now); return; }
        int n = _count[i];
        if (n >= 3 && time > 1.07 * Median(i, n)) { Reject(i, 4, time, now); return; }

        _times[i * MaxHistory + _head[i]] = time;
        _head[i] = (_head[i] + 1) % MaxHistory;
        if (n < MaxHistory) _count[i] = n + 1;
        _log.Emit(DiagLevel.Trace, DiagEvent.LeaderLapAccepted, now, i, 0, time);
    }

    private void Reject(int i, int cause, float time, double now) =>
        _log.Emit(DiagLevel.Debug, DiagEvent.LeaderLapRejected, now, i, cause, time);

    private double Median(int i, int n)
    {
        Span<float> tmp = stackalloc float[MaxHistory];
        for (int k = 0; k < n; k++) tmp[k] = _times[i * MaxHistory + k];
        // insertion sort of n <= 16 values
        for (int a = 1; a < n; a++)
        {
            float v = tmp[a]; int b = a - 1;
            while (b >= 0 && tmp[b] > v) { tmp[b + 1] = tmp[b]; b--; }
            tmp[b + 1] = v;
        }
        return (n & 1) == 1 ? tmp[n / 2] : 0.5 * (tmp[n / 2 - 1] + tmp[n / 2]);
    }

    private double MeanOfLast(int i, int take)
    {
        double sum = 0;
        for (int k = 1; k <= take; k++)
            sum += _times[i * MaxHistory + ((_head[i] - k + MaxHistory) % MaxHistory)];
        return sum / take;
    }

    private void Publish(in TelemetryFrame f, int leader, bool caution)
    {
        var prev = Current;
        LeaderPace next;
        if (leader < 0)
            next = LeaderPace.None;
        else
        {
            double lapPct = f.CarIdxLapDistPct[leader];
            int n = _count[leader];
            double pace = double.NaN;
            var src = LeaderPaceSource.None;
            if (n >= 2) { pace = MeanOfLast(leader, Math.Min(n, _window)); src = LeaderPaceSource.Mean; }
            else if (n == 1) { pace = 1.02 * _times[leader * MaxHistory + ((_head[leader] - 1 + MaxHistory) % MaxHistory)]; src = LeaderPaceSource.Best102; }

            bool hold = (caution || f.CarIdxOnPitRoad[leader]) && !double.IsNaN(_lastPace);
            if (hold)
                next = new LeaderPace(leader, _lastPace, lapPct, LeaderPaceSource.Held, caution);
            else if (src == LeaderPaceSource.None && !double.IsNaN(_lastPace))
                next = new LeaderPace(leader, _lastPace, lapPct, LeaderPaceSource.Held, false);
            else
            {
                if (src != LeaderPaceSource.None) _lastPace = pace;
                next = new LeaderPace(leader, pace, lapPct, src, false);
            }
        }

        Current = next;
        if (next.LeaderCarIdx != prev.LeaderCarIdx)
            _log.Emit(DiagLevel.Info, DiagEvent.LeaderChanged, f.SessionTime, prev.LeaderCarIdx, next.LeaderCarIdx, next.PaceS);
        if (next.Source != _prevSource)
        {
            _prevSource = next.Source;
            _log.Emit(DiagLevel.Debug, DiagEvent.LeaderPaceSource, f.SessionTime, (int)next.Source, 0, next.PaceS);
        }
    }
}
