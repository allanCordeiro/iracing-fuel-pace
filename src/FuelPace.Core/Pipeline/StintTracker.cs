using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;

namespace FuelPace.Core.Pipeline;

/// <summary>
/// Moving mean of the valid laps of the current stint (decision 4). A stint starts with the first frame, on pit exit
/// (<c>OnPitRoad</c> true → false) or on a refuel: <c>FuelLevel</c> more than <see cref="RefuelRiseL"/> above its running
/// minimum since the stint started, which also catches gradual pit refuelling (R14). A refuel during a pit stop already
/// started the stint, so that stop's exit does not start another. Allocation-free per frame.
/// </summary>
public sealed class StintTracker
{
    public const double RefuelRiseL = 0.3;
    private const int CausePitExit = 0, CauseRefuel = 1;

    private readonly int _window;
    private readonly DiagLog _log;
    private readonly double[] _fuel, _time;
    private int _head, _count;
    private bool _started, _prevOnPit;
    private float _prevFuel, _minFuel;
    /// <summary>After a refuel start the minimum follows the level up until it falls again, so one refuel starts one stint.</summary>
    private bool _followRise;
    private bool _refuelStartedThisStop;
    private double _stintStart;

    public StintTracker(int window, DiagLog? log = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        _window = window;
        _log = log ?? DiagLog.Null;
        _fuel = new double[window];
        _time = new double[window];
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
    /// while still open (end not observed, e.g. by a flush) is ignored.</summary>
    public void OnLapCompleted(in CompletedLap lap)
    {
        if (!lap.Stats.EndObserved || !(lap.EndTime > _stintStart)) return;
        LapsDone++;
        if (lap.Reasons != LapReasons.None) return;

        _fuel[_head] = lap.FuelUsedL;
        _time[_head] = lap.TimeS;
        _head = (_head + 1) % _window;
        if (_count < _window) _count++;
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
        double fuel = 0, time = 0;
        int timed = 0;
        for (int i = 0; i < _count; i++)
        {
            fuel += _fuel[i];
            if (!double.IsNaN(_time[i])) { time += _time[i]; timed++; }
        }
        MeanFuelL = _count >= 2 ? fuel / _count : double.NaN;
        MeanLapS = timed >= 2 ? time / timed : double.NaN;
    }
}
