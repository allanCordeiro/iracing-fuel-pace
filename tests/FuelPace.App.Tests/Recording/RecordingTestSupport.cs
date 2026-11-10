using FuelPace.App.Simulation;
using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Telemetry;
using FuelPace.Tests.Support;

namespace FuelPace.App.Tests.Recording;

internal static class RecordingTestSupport
{
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuelpace-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    public static string Yaml(string carPath = "ligierjsp320") => SyntheticRace.Yaml.Replace("CarPath: ligierjsp320", "CarPath: " + carPath);

    public static SessionInfo Info(string yaml) => SessionInfoParser.Parse(yaml);

    public static List<DiagRecord> Drain(DiagLog log)
    {
        var buf = new DiagRecord[1024];
        return [.. buf.AsSpan(0, log.DrainRecords(buf))];
    }

    public static List<TelemetryFrame> ReadAll(string path)
    {
        using var src = new IbtTelemetrySource(path);
        var list = new List<TelemetryFrame>();
        var f = new TelemetryFrame();
        while (src.TryRead(ref f) == ReadResult.Frame) list.Add(f);
        return list;
    }

    /// <summary>Field-by-field equality; <c>HasCarIdx</c> is a property of the file, not of the frame, so it is not compared.</summary>
    public static void AssertSameFrame(in TelemetryFrame e, in TelemetryFrame a, int index)
    {
        string at = " at frame " + index;
        Assert.True(e.SessionTime == a.SessionTime && e.SessionTimeRemain == a.SessionTimeRemain, "time" + at);
        Assert.True(e.SessionNum == a.SessionNum && e.SessionState == a.SessionState && e.SessionLapsRemainEx == a.SessionLapsRemainEx
            && e.Lap == a.Lap && e.LapCompleted == a.LapCompleted && e.PlayerTrackSurface == a.PlayerTrackSurface
            && e.PlayerCarMyIncidentCount == a.PlayerCarMyIncidentCount && e.PlayerCarIdx == a.PlayerCarIdx
            && e.TrackWetness == a.TrackWetness && e.SessionFlags == a.SessionFlags, "ints" + at);
        Assert.True(e.LapDistPct == a.LapDistPct && e.LapLastLapTime == a.LapLastLapTime && e.FuelLevel == a.FuelLevel
            && e.FuelLevelPct == a.FuelLevelPct && e.FuelUsePerHour == a.FuelUsePerHour && e.PlayerCarTowTime == a.PlayerCarTowTime
            && e.Speed == a.Speed && e.Throttle == a.Throttle && e.TrackTempCrew == a.TrackTempCrew && e.AirTemp == a.AirTemp
            && e.PlayerCarPowerAdjust == a.PlayerCarPowerAdjust && e.PlayerCarWeightPenalty == a.PlayerCarWeightPenalty, "floats" + at);
        Assert.True(e.OnPitRoad == a.OnPitRoad && e.PlayerCarInPitStall == a.PlayerCarInPitStall && e.IsOnTrack == a.IsOnTrack
            && e.IsReplayPlaying == a.IsReplayPlaying, "bools" + at);
        Assert.True(((ReadOnlySpan<int>)e.CarIdxPosition).SequenceEqual(a.CarIdxPosition), "CarIdxPosition" + at);
        Assert.True(((ReadOnlySpan<int>)e.CarIdxLapCompleted).SequenceEqual(a.CarIdxLapCompleted), "CarIdxLapCompleted" + at);
        Assert.True(((ReadOnlySpan<int>)e.CarIdxTrackSurface).SequenceEqual(a.CarIdxTrackSurface), "CarIdxTrackSurface" + at);
        Assert.True(((ReadOnlySpan<int>)e.CarIdxClass).SequenceEqual(a.CarIdxClass), "CarIdxClass" + at);
        Assert.True(((ReadOnlySpan<float>)e.CarIdxLapDistPct).SequenceEqual(a.CarIdxLapDistPct), "CarIdxLapDistPct" + at);
        Assert.True(((ReadOnlySpan<float>)e.CarIdxLastLapTime).SequenceEqual(a.CarIdxLastLapTime), "CarIdxLastLapTime" + at);
        Assert.True(((ReadOnlySpan<bool>)e.CarIdxOnPitRoad).SequenceEqual(a.CarIdxOnPitRoad), "CarIdxOnPitRoad" + at);
    }

    public sealed class LapCollector : ILapSink
    {
        public readonly List<(int Lap, double TimeS, double FuelL, LapReasons Reasons)> Laps = [];
        public void OnLapCompleted(in CompletedLap lap) => Laps.Add((lap.LapNumber, lap.TimeS, lap.FuelUsedL, lap.Reasons));
    }

    public static List<(int Lap, double TimeS, double FuelL, LapReasons Reasons)> DetectLaps(IEnumerable<TelemetryFrame> frames)
    {
        var sink = new LapCollector();
        var detector = new LapDetector(sink);
        detector.SetTrackLength(SessionInfoParser.Parse(SyntheticRace.Yaml).TrackLengthKm * 1000);
        foreach (var f in frames) detector.Update(in f);
        detector.Flush();
        return sink.Laps;
    }
}
