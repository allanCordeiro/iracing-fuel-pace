namespace FuelPace.Core.Diagnostics;

/// <summary>Values 1-999 are reserved for Core; the App registers its own from 1000.</summary>
public enum DiagEvent : ushort
{
    SourceOpened = 1,
    SourceEnded,
    LayoutResolved,
    SessionInfoChanged,
    TornRead,
    FrameGap,
    LapBoundary,
    LapTimeResolved,
    LapReasonTriggered,
    LapCompleted,
    StintStarted,
    LeaderChanged,
    LeaderLapAccepted,
    LeaderLapRejected,
    LeaderPaceSource,
    StateChanged,
    InsightChanged,
    OverlayVersion,
    StrategyTrace,
    CaptureStarted,
    CaptureLap,
    CaptureStopped,
    ProfileSaved,
    ProfileSaveFailed,
    ProfileQuarantined,
    PerfMinute,
    UserMark,
}
