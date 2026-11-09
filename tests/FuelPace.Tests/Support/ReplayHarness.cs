using FuelPace.Core.Capture;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Support;

/// <summary>Replays a whole .ibt through <see cref="LapDetector"/> into a <see cref="CaptureSession"/>.</summary>
public static class ReplayHarness
{
    /// <summary>
    /// Capture is started before the first frame is fed; at the end the detector is flushed (the lap in progress
    /// comes out <c>Incomplete</c>) and the capture is stopped with no snapshot.
    /// </summary>
    public static (CaptureRecord record, SessionInfo info) CaptureWholeFile(string ibtPath)
    {
        using var source = new IbtTelemetrySource(ibtPath);
        var info = SessionInfoParser.Parse(source.ReadSessionInfoYaml());
        var capture = new CaptureSession(TimeProvider.System);
        var detector = new LapDetector(capture);
        detector.SetTrackLength(info.TrackLengthKm * 1000);

        var f = new TelemetryFrame();
        bool first = true;
        while (source.TryRead(ref f) == ReadResult.Frame)
        {
            if (first)
            {
                first = false;
                var conditions = new CaptureConditions(f.TrackTempCrew, f.AirTemp, f.TrackWetness,
                    f.PlayerCarPowerAdjust, f.PlayerCarWeightPenalty, info.TankCapacityL);
                capture.Start(f.SessionTime, info.ProfileKey, info.FindSession(f.SessionNum)?.Type ?? "", conditions);
            }
            detector.Update(in f);
        }
        detector.Flush();
        return (capture.Stop(false, default), info);
    }
}
