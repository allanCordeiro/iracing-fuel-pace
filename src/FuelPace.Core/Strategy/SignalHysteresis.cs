namespace FuelPace.Core.Strategy;

/// <summary>
/// Holds the signal colour until a new state persists for <see cref="HoldSeconds"/> (§7.4). Only transitions among
/// Blue/Red/NotFeasible are held; to or from Inactive/NoBaseline/NoTarget, the first call, and a clock that goes
/// backwards apply the raw state at once.
/// </summary>
public struct SignalHysteresis
{
    public const double HoldSeconds = 1.5;

    bool _started;
    SignalState _shown, _pending;
    bool _hasPending;
    double _pendingSince, _last;

    /// <summary>The state to display for <paramref name="raw"/> at session time <paramref name="now"/>.</summary>
    public SignalState Apply(SignalState raw, double now)
    {
        if (!_started || now < _last || !IsColor(raw) || !IsColor(_shown))
        {
            _started = true;
            _last = now;
            _hasPending = false;
            return _shown = raw;
        }

        _last = now;
        if (raw == _shown)
        {
            _hasPending = false;
            return _shown;
        }

        if (!_hasPending || _pending != raw)
        {
            _hasPending = true;
            _pending = raw;
            _pendingSince = now;
        }

        if (now - _pendingSince >= HoldSeconds)
        {
            _hasPending = false;
            _shown = raw;
        }
        return _shown;
    }

    static bool IsColor(SignalState s) => s is SignalState.Blue or SignalState.Red or SignalState.NotFeasible;
}
