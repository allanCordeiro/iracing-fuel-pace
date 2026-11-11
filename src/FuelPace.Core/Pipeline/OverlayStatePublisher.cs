using FuelPace.Core.Strategy;

namespace FuelPace.Core.Pipeline;

/// <summary>
/// Hands the latest <see cref="OverlayState"/> from the telemetry thread to the renderer. <see cref="Version"/> only moves
/// when something visible changed, so the renderer can skip redraws. Also carries the car/track short names the
/// renderer needs to format the no-baseline insight.
/// </summary>
public sealed class OverlayStatePublisher
{
    private readonly Lock _lock = new();
    private OverlayState _last;
    private bool _has;
    private long _version, _publishCount;
    private volatile string _carShort = "", _trackShort = "";

    /// <summary>Incremented by a publish that differs visibly from the previous one (the first publish counts).</summary>
    public long Version => Interlocked.Read(ref _version);

    public long PublishCount => Interlocked.Read(ref _publishCount);

    public string CarShort => _carShort;
    public string TrackShort => _trackShort;

    public void Publish(in OverlayState s)
    {
        using (_lock.EnterScope())
        {
            bool changed = !_has || !s.VisibleEquals(in _last);
            _last = s;
            _has = true;
            Interlocked.Increment(ref _publishCount);
            if (changed) Interlocked.Increment(ref _version);
        }
    }

    /// <summary>A copy of the latest state (default before the first publish).</summary>
    public OverlayState Read()
    {
        using (_lock.EnterScope()) return _last;
    }

    internal void SetLabels(string carShort, string trackShort)
    {
        _carShort = carShort;
        _trackShort = trackShort;
    }
}
