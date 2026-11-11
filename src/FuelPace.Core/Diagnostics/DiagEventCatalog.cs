namespace FuelPace.Core.Diagnostics;

public sealed record DiagEventInfo(string Name, string Category, string? I0, string? I1, string? A, string? B, string? C, string? D, bool I1IsLapReasons = false);

public static class DiagEventCatalog
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<DiagEvent, DiagEventInfo> Map = new();

    static DiagEventCatalog()
    {
        Register(DiagEvent.SourceOpened, new DiagEventInfo("SourceOpened", "telemetry", "numVars", "bufLen", "tickRate", null, null, null));
        Register(DiagEvent.SourceEnded, new DiagEventInfo("SourceEnded", "telemetry", "reason", null, null, null, null, null));
        Register(DiagEvent.LayoutResolved, new DiagEventInfo("LayoutResolved", "telemetry", "missingVars", "hasCarIdx", "isElectric", null, null, null));
        Register(DiagEvent.SessionInfoChanged, new DiagEventInfo("SessionInfoChanged", "telemetry", "version", null, null, null, null, null));
        Register(DiagEvent.TornRead, new DiagEventInfo("TornRead", "telemetry", "count", null, null, null, null, null));
        Register(DiagEvent.FrameGap, new DiagEventInfo("FrameGap", "laps", null, null, "dtS", "sessionTime", null, null));
        Register(DiagEvent.LapBoundary, new DiagEventInfo("LapBoundary", "laps", "lap", "kind", "prevPct", "pct", "interpFrac", null));
        Register(DiagEvent.LapTimeResolved, new DiagEventInfo("LapTimeResolved", "laps", "lap", "source", "timeS", "waitS", null, null));
        Register(DiagEvent.LapReasonTriggered, new DiagEventInfo("LapReasonTriggered", "laps", "lap", "reasonBit", "value", "pct", null, null));
        Register(DiagEvent.LapCompleted, new DiagEventInfo("LapCompleted", "laps", "lap", "reasons", "timeS", "fuelL", "fullThrottlePct", "measuredTimeS", I1IsLapReasons: true));
        Register(DiagEvent.StintStarted, new DiagEventInfo("StintStarted", "stint", "cause", null, "fuelL", null, null, null));
        Register(DiagEvent.LeaderChanged, new DiagEventInfo("LeaderChanged", "leader", "oldIdx", "newIdx", "paceS", null, null, null));
        Register(DiagEvent.LeaderLapAccepted, new DiagEventInfo("LeaderLapAccepted", "leader", "carIdx", null, "timeS", null, null, null));
        Register(DiagEvent.LeaderLapRejected, new DiagEventInfo("LeaderLapRejected", "leader", "carIdx", "cause", "timeS", null, null, null));
        Register(DiagEvent.LeaderPaceSource, new DiagEventInfo("LeaderPaceSource", "leader", "source", null, "paceS", null, null, null));
        Register(DiagEvent.StateChanged, new DiagEventInfo("StateChanged", "strategy", "raw", "displayed", "targetL", "refL", "lapsRemaining", "feasibleL"));
        Register(DiagEvent.InsightChanged, new DiagEventInfo("InsightChanged", "strategy", "kind", null, "a", "b", "c", null));
        Register(DiagEvent.OverlayVersion, new DiagEventInfo("OverlayVersion", "strategy", "state", "insightKind", "fuelL", "targetL", "lapsRemaining", "refL"));
        Register(DiagEvent.StrategyTrace, new DiagEventInfo("StrategyTrace", "strategy", "raceKind", "flags", "projectionL", "lastL", "meanL", "myPaceS"));
        Register(DiagEvent.CaptureStarted, new DiagEventInfo("CaptureStarted", "capture", "lapAtStart", null, null, null, null, null));
        Register(DiagEvent.CaptureLap, new DiagEventInfo("CaptureLap", "capture", "lap", "reasons", "fuelL", "timeS", null, null, I1IsLapReasons: true));
        Register(DiagEvent.CaptureStopped, new DiagEventInfo("CaptureStopped", "capture", "valid", "total", "auto", null, null, null));
        Register(DiagEvent.ProfileSaved, new DiagEventInfo("ProfileSaved", "storage", "captures", "validLaps", null, null, null, null));
        Register(DiagEvent.ProfileSaveFailed, new DiagEventInfo("ProfileSaveFailed", "storage", "captures", "validLaps", null, null, null, null));
        Register(DiagEvent.ProfileQuarantined, new DiagEventInfo("ProfileQuarantined", "storage", "captures", "validLaps", null, null, null, null));
        Register(DiagEvent.PerfMinute, new DiagEventInfo("PerfMinute", "perf", "gen0Delta", "publishes", "tickP99Us", "tickMaxUs", "workingSetMB", "droppedDiag"));
        Register(DiagEvent.UserMark, new DiagEventInfo("UserMark", "user", "markNumber", null, null, null, null, null));
    }

    public static DiagEventInfo Get(DiagEvent e) =>
        Map.TryGetValue(e, out var info) ? info : new DiagEventInfo(e.ToString(), "app", "i0", "i1", "a", "b", "c", "d");

    public static void Register(DiagEvent e, DiagEventInfo info) => Map[e] = info;
}
