using FuelPace.Core.Pipeline;

namespace FuelPace.App.Host;

/// <summary>
/// Decides, on the telemetry thread, when the UI thread should redraw (PERF-06): when <see cref="OverlayStatePublisher.Version"/>
/// or the view-options version (<see cref="InvalidateOptions"/>) moved since the last request, at least
/// <see cref="MinInterval"/> after it, and never while a request is still waiting on the UI thread. A change that arrives too
/// early is picked up by a later <see cref="Check"/>. <see cref="Check"/> allocates nothing: it posts one cached delegate.
/// </summary>
public sealed class RenderScheduler
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly OverlayStatePublisher _publisher;
    private readonly IUiDispatcher _ui;
    private readonly Action _render;
    private readonly TimeProvider _clock;
    private readonly Action _run;
    private long _optionsVersion;
    private int _pending;

    // Check() thread only.
    private long _lastPublished = -1, _lastOptions = -1, _lastRequest;
    private bool _requested;

    public RenderScheduler(OverlayStatePublisher publisher, IUiDispatcher ui, Action render, TimeProvider clock)
    {
        _publisher = publisher;
        _ui = ui;
        _render = render;
        _clock = clock;
        _run = Run;
    }

    /// <summary>Any thread: what the overlay shows changed for a reason other than the telemetry (settings, edit mode, ...).</summary>
    public void InvalidateOptions() => Interlocked.Increment(ref _optionsVersion);

    /// <summary>Telemetry thread (one caller at a time). True when it posted a render request.</summary>
    public bool Check()
    {
        long version = _publisher.Version, options = Interlocked.Read(ref _optionsVersion);
        if (version == _lastPublished && options == _lastOptions) return false;
        if (Volatile.Read(ref _pending) != 0) return false;
        long now = _clock.GetTimestamp();
        if (_requested && _clock.GetElapsedTime(_lastRequest, now) < MinInterval) return false;
        _lastPublished = version;
        _lastOptions = options;
        _lastRequest = now;
        _requested = true;
        Volatile.Write(ref _pending, 1);
        _ui.Post(_run);
        return true;
    }

    private void Run()
    {
        // Cleared first: a change during the render gets its own request.
        Volatile.Write(ref _pending, 0);
        _render();
    }
}
