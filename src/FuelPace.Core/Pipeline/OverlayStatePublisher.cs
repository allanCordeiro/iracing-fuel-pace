using FuelPace.Core.Display;
using FuelPace.Core.Strategy;

namespace FuelPace.Core.Pipeline;

/// <summary>
/// Hands the latest <see cref="OverlayState"/> and the <see cref="GlanceView"/> built from it from the telemetry thread to the
/// renderer. <see cref="Version"/> only moves when the view changed by value (PRD v3 §8.4: the view is already rounded as
/// displayed, so equal views draw the same picture), and the renderer skips redraws while it stands still.
/// </summary>
public sealed class OverlayStatePublisher
{
    private readonly Lock _lock = new();
    private OverlayState _last;
    private GlanceView _lastView;
    private bool _has;
    private long _version, _publishCount;

    /// <summary>Incremented by a publish whose view differs from the previous one (the first publish counts).</summary>
    public long Version => Interlocked.Read(ref _version);

    public long PublishCount => Interlocked.Read(ref _publishCount);

    public void Publish(in OverlayState s, in GlanceView v)
    {
        using (_lock.EnterScope())
        {
            bool changed = !_has || !v.Equals(_lastView);
            _last = s;
            _lastView = v;
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

    /// <summary>A copy of the latest view (default before the first publish).</summary>
    public GlanceView ReadView()
    {
        using (_lock.EnterScope()) return _lastView;
    }
}
