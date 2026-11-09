using System.Globalization;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;

namespace FuelPace.Core.Capture;

/// <summary>
/// Records the laps the <see cref="LapDetector"/> emits between <see cref="Start"/> and <see cref="Stop"/>.
/// Laps that ended before the start are ignored; a lap that started before it is <c>Incomplete</c>;
/// the lap in progress at the stop (if any) is recorded as <c>Incomplete</c>. Runs on the telemetry thread:
/// allocates only once per recorded lap.
/// </summary>
public sealed class CaptureSession : ILapSink
{
    private readonly TimeProvider _clock;
    private readonly DiagLog _log;
    private CaptureRecord? _record;
    private bool _active;
    private double _startTime;
    private int _validCount;

    public CaptureSession(TimeProvider clock, DiagLog? log = null)
    {
        _clock = clock;
        _log = log ?? DiagLog.Null;
    }

    public bool IsActive => _active;
    /// <summary>Recorded laps with no reason so far (outliers are only decided at <see cref="Stop"/>).
    /// Counts refer to the current capture, or the last one until the next <see cref="Start"/>.</summary>
    public int ValidCount => _validCount;
    public int TotalCount => _record?.Laps.Count ?? 0;
    public string ProfileKey { get; private set; } = "";

    public void Start(double sessionTime, string profileKey, string sessionType, CaptureConditions conditions)
    {
        _startTime = sessionTime;
        _validCount = 0;
        ProfileKey = profileKey;
        _active = true;
        _record = new CaptureRecord
        {
            Id = _clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            SessionType = sessionType,
            Conditions = conditions,
        };
    }

    public void OnLapCompleted(in CompletedLap lap)
    {
        if (!_active || lap.EndTime <= _startTime) return;
        var extra = lap.StartTime < _startTime ? LapReasons.Incomplete : LapReasons.None;
        Record(in lap, extra);
    }

    /// <summary>Ends the capture; <paramref name="inProgress"/> is recorded as <c>Incomplete</c> when <paramref name="hasInProgress"/>.</summary>
    public CaptureRecord Stop(bool hasInProgress, in CompletedLap inProgress)
    {
        if (!_active) throw new InvalidOperationException("Capture is not active.");
        var rec = _record!;
        if (hasInProgress) Record(in inProgress, LapReasons.Incomplete);
        _active = false;
        OutlierRule.Apply(rec.Laps);
        return rec;
    }

    private void Record(in CompletedLap lap, LapReasons extra)
    {
        var c = CapturedLap.From(in lap);
        c.Reasons |= extra;
        _record!.Laps.Add(c);
        if (c.Valid) _validCount++;
        _log.Emit(DiagLevel.Info, DiagEvent.CaptureLap, lap.EndTime, c.Lap, (int)c.Reasons, c.FuelL, c.TimeS ?? double.NaN);
    }
}
