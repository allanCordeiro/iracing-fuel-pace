namespace FuelPace.Core.Strategy;

/// <summary>
/// <see cref="LapsRemainingCalculator.ByTime"/> with memory (feedback 2026-11-14). Its two ceil() stages (the leader's crossings
/// until the clock runs out, then mine until he finishes) flip by one lap when the finish sits near a lap boundary: within a lap
/// LapDistPct runs up to ~0.02 lap ahead of or behind a linear advance, so pct + time/pace swings by ~0.04 lap. Each stage keeps
/// its whole count while the value stays within <see cref="BandLaps"/> of it, follows each car across the line, and starts over
/// from the plain formula after a gap, a clock going backwards, a new leader, or unavailable inputs. With the clock at zero the
/// leader's count is exact (he finishes the lap in progress). Allocation-free.
/// </summary>
public struct TimedLapsHold
{
    /// <summary>The leader key when there is no usable leader data and my own pct and pace stand in for him.</summary>
    public const int OwnPaceKey = -2;
    /// <summary>How far, in laps, the prediction must move past a boundary before the count changes (~5 s at 100 s laps).</summary>
    public const double BandLaps = 0.05;
    /// <summary>A longer gap between calls (in session seconds) breaks the history: a crossing may have been missed.</summary>
    public const double MaxGapS = 1.0;

    Stage _leader, _me;
    bool _started;
    double _last;
    int _leaderKey;

    /// <summary>Laps remaining for me; same inputs and fallbacks as <see cref="LapsRemainingCalculator.ByTime"/>, plus the session
    /// time <paramref name="now"/> and the leader's car index <paramref name="leaderKey"/>.</summary>
    public double ByTime(double now, double timeRemainS, int leaderKey, double leaderPct, double leaderPaceS, double myPct, double myPaceS)
    {
        if (double.IsNaN(timeRemainS) || !(myPaceS > 0) || double.IsNaN(myPct))
        {
            _started = false;
            return double.NaN;
        }
        bool own = !(leaderPaceS > 0) || double.IsNaN(leaderPct);
        if (own)
        {
            leaderPct = myPct;
            leaderPaceS = myPaceS;
            leaderKey = OwnPaceKey;
        }
        if (!_started || now < _last || now - _last > MaxGapS || leaderKey != _leaderKey)
        {
            _leader.Valid = false;
            _me.Valid = false;
        }
        _started = true;
        _last = now;
        _leaderKey = leaderKey;

        double t = Math.Max(0, timeRemainS);
        double y = leaderPct + t / leaderPaceS;
        int nLeader = t <= 0
            ? _leader.Set(Ceil(y + LapsRemainingCalculator.CeilEpsilon), leaderPct)
            : _leader.Hold(y, leaderPct, +LapsRemainingCalculator.CeilEpsilon);
        // On my own pace I am the "leader": I finish on his lap, never one later.
        if (own) return _me.Set(nLeader, myPct) - myPct;

        double finishS = (nLeader - leaderPct) * leaderPaceS;
        double x = myPct + finishS / myPaceS;
        return _me.Hold(x, myPct, -LapsRemainingCalculator.CeilEpsilon) - myPct;
    }

    static int Ceil(double v) => (int)Math.Ceiling(v);

    /// <summary>One ceil() stage: the number of crossings from the start of the car's current lap, at least one.</summary>
    struct Stage
    {
        public bool Valid;
        int _n;
        double _pct;

        public int Hold(double v, double pct, double epsilon)
        {
            if (!Valid) return Set(Ceil(v + epsilon), pct);
            int n = _n;
            if (_pct - pct > 0.5) n--;      // crossed the line
            else if (pct - _pct > 0.5) n++; // backed over it
            return Set(Math.Clamp(n, Ceil(v - BandLaps), Ceil(v + BandLaps)), pct);
        }

        public int Set(int n, double pct)
        {
            Valid = true;
            _n = Math.Max(1, n);
            _pct = pct;
            return _n;
        }
    }
}
