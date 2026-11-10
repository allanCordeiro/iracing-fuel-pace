using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Pipeline;

/// <summary>
/// Moving mean of the valid laps of the current stint (decision 4). A stint starts with the first frame, on pit exit
/// (<c>OnPitRoad</c> true → false) or on a refuel: <c>FuelLevel</c> more than <see cref="RefuelRiseL"/> above its running
/// minimum since the stint started, which also catches gradual pit refuelling (R14). A refuel during a pit stop already
/// started the stint, so that stop's exit does not start another. The last <see cref="MaxWindow"/> valid laps of the stint
/// are kept, so <see cref="Resize"/> changes the mean window without losing the stint (R15). Allocation-free per frame.
/// </summary>
public sealed class StintTracker
{
    public const double RefuelRiseL = 0.3;
    public const int MaxWindow = 16;
    private const int CausePitExit = 0, CauseRefuel = 1;

    private int _window;
    private readonly DiagLog _log;
    private readonly double[] _fuel = new double[MaxWindow], _time = new double[MaxWindow];
    private int _head, _count;
    private bool _started, _prevOnPit;
    private float _prevFuel, _minFuel;
    /// <summary>After a refuel start the minimum follows the level up until it falls again, so one refuel starts one stint.</summary>
    private bool _followRise;
    private bool _refuelStartedThisStop;
    private double _stintStart;

    public StintTracker(int window, DiagLog? log = null)
    {
        CheckWindow(window);
        _window = window;
        _log = log ?? DiagLog.Null;
    }

    /// <summary>Changes the mean window (1–<see cref="MaxWindow"/>); the stint start, <see cref="LapsDone"/> and the lap history are kept.</summary>
    public void Resize(int window)
    {
        CheckWindow(window);
        _window = window;
        Recompute();
    }

    /// <summary>Forgets the stint and its laps (new session or new connection, I-5); the next <see cref="Update"/> starts a
    /// new stint, like the first frame did.</summary>
    public void Reset()
    {
        _started = _prevOnPit = _followRise = _refuelStartedThisStop = false;
        _prevFuel = _minFuel = 0;
        _stintStart = 0;
        _head = _count = 0;
        LapsDone = 0;
        MeanFuelL = MeanLapS = double.NaN;
    }

    private static void CheckWindow(int window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(window, MaxWindow);
    }

    /// <summary>Mean fuel of the last <c>window</c> valid laps of the stint; NaN with fewer than 2.</summary>
    public double MeanFuelL { get; private set; } = double.NaN;

    /// <summary>Mean official time of those laps (laps with no time are skipped); NaN with fewer than 2.</summary>
    public double MeanLapS { get; private set; } = double.NaN;

    /// <summary>Laps (valid or not) completed since the stint started.</summary>
    public int LapsDone { get; private set; }

    public void Update(in TelemetryFrame f)
    {
        if (!_started)
        {
            _started = true;
            _stintStart = f.SessionTime;
            _minFuel = f.FuelLevel;
        }
        else
        {
            if (_prevOnPit && !f.OnPitRoad)
            {
                if (_refuelStartedThisStop) _refuelStartedThisStop = false;
                else StartStint(CausePitExit, in f);
            }

            if (_followRise)
            {
                if (f.FuelLevel >= _prevFuel) _minFuel = f.FuelLevel;
                else _followRise = false;
            }
            if (f.FuelLevel < _minFuel) _minFuel = f.FuelLevel;
            else if (!_followRise && f.FuelLevel - _minFuel > RefuelRiseL)
            {
                StartStint(CauseRefuel, in f);
                _followRise = true;
                _refuelStartedThisStop = f.OnPitRoad || f.PlayerCarInPitStall;
            }
        }

        _prevOnPit = f.OnPitRoad;
        _prevFuel = f.FuelLevel;
    }

    /// <summary>Counts laps that ended inside the stint; only those with no reason enter the mean. A lap emitted
    /// while still open (end not observed, e.g. by a flush), or before the stint's first frame (after <see cref="Reset"/>), is ignored.</summary>
    public void OnLapCompleted(in CompletedLap lap)
    {
        if (!_started || !lap.Stats.EndObserved || !(lap.EndTime > _stintStart)) return;
        LapsDone++;
        if (lap.Reasons != LapReasons.None) return;

        _fuel[_head] = lap.FuelUsedL;
        _time[_head] = lap.TimeS;
        _head = (_head + 1) % MaxWindow;
        if (_count < MaxWindow) _count++;
        Recompute();
    }

    private void StartStint(int cause, in TelemetryFrame f)
    {
        _stintStart = f.SessionTime;
        _minFuel = f.FuelLevel;
        _head = _count = 0;
        LapsDone = 0;
        MeanFuelL = MeanLapS = double.NaN;
        _log.Emit(DiagLevel.Info, DiagEvent.StintStarted, f.SessionTime, cause, 0, f.FuelLevel);
    }

    private void Recompute()
    {
        int n = Math.Min(_count, _window);
        double fuel = 0, time = 0;
        int timed = 0;
        for (int k = 1; k <= n; k++)
        {
            int i = (_head - k + MaxWindow) % MaxWindow;
            fuel += _fuel[i];
            if (!double.IsNaN(_time[i])) { time += _time[i]; timed++; }
        }
        MeanFuelL = n >= 2 ? fuel / n : double.NaN;
        MeanLapS = timed >= 2 ? time / timed : double.NaN;
    }
}
