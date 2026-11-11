using FuelPace.App.Host;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Telemetry;

namespace FuelPace.Agent.Live;

/// <summary>
/// The Agent's telemetry sources. With <c>--replay</c> the file is played once (later calls return null: the overlay stays
/// inactive until exit); otherwise the live sim through <see cref="IRacingMemoryReader"/>, whose shared-memory and event names
/// can be overridden by the <c>FUELPACE_IRSDK_MAP</c> and <c>FUELPACE_IRSDK_EVENT</c> environment variables (FakeSim and the
/// end-to-end tests use private names).
/// </summary>
public sealed class LiveSourceFactory : ITelemetrySourceFactory
{
    public const string MapEnv = "FUELPACE_IRSDK_MAP", EventEnv = "FUELPACE_IRSDK_EVENT";

    private readonly string? _replayPath;
    private readonly ReplaySpeed _speed;
    private readonly Func<DiagLog> _log;
    private int _replayed;

    /// <param name="log">Read at every open: the host's log exists only once it started.</param>
    public LiveSourceFactory(string? replayPath, ReplaySpeed speed, Func<DiagLog>? log = null)
    {
        _replayPath = replayPath;
        _speed = speed;
        _log = log ?? (() => DiagLog.Null);
        MapName = Name(MapEnv, IRacingMemoryReader.DefaultMapName);
        EventName = Name(EventEnv, IRacingMemoryReader.DefaultEventName);
    }

    public string MapName { get; }
    public string EventName { get; }

    public ITelemetrySource? TryOpen()
    {
        if (_replayPath is not null)
            return Interlocked.Exchange(ref _replayed, 1) == 0 ? new IbtTelemetrySource(_replayPath, _speed) : null;
        return IRacingMemoryReader.TryOpen(MapName, EventName, _log());
    }

    private static string Name(string env, string fallback) =>
        Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? v : fallback;
}
