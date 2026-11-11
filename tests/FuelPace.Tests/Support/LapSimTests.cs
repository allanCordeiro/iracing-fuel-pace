using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Support;

public class LapSimTests
{
    private static TelemetryFrame[] Run(LapSim sim, int laps = 3, double endPct = 0.1) => sim.Run(laps, endPct).ToArray();

    private static TelemetryFrame[] Lap(TelemetryFrame[] frames, int lap) => frames.Where(f => f.LapCompleted == lap - 1).ToArray();

    [Fact]
    public void Defaults_are_steady_and_deterministic()
    {
        var a = Run(new LapSim());
        var b = Run(new LapSim());
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a.Select(f => (f.SessionTime, f.LapDistPct, f.FuelLevel, f.Lap)), b.Select(f => (f.SessionTime, f.LapDistPct, f.FuelLevel, f.Lap)));

        var f0 = a[0];
        Assert.True(f0.IsOnTrack);
        Assert.Equal(4, f0.SessionState);
        Assert.Equal(LapSim.SurfaceOnTrack, f0.PlayerTrackSurface);
        Assert.Equal(50f, f0.Speed);
        Assert.Equal(100f, f0.FuelLevel);
        Assert.Equal(-1f, f0.LapLastLapTime);
        Assert.Equal(LapSim.UnlimitedLaps, f0.SessionLapsRemainEx);
        Assert.Equal(LapSim.UnlimitedTimeRemainS, f0.SessionTimeRemain);
        Assert.Equal(0u, f0.SessionFlags);
        Assert.False(f0.HasCarIdx);
        Assert.All(a, f => Assert.False(f.OnPitRoad));
        Assert.All(a, f => Assert.Equal(0, f.PlayerCarMyIncidentCount));
        Assert.Equal(100 - 3 * 2.4 - 2.4 * LapSim.FuelCurve(a[^1].LapDistPct), a[^1].FuelLevel, 0.001);
    }

    [Fact]
    public void Official_time_is_published_after_the_delay()
    {
        var frames = Run(new LapSim().LapTimes(81, 82), laps: 2, endPct: 0.1);
        Assert.Equal(-1f, frames.Last(f => f.SessionTime < 81 + 1.5).LapLastLapTime);
        Assert.Equal(81f, frames.First(f => f.SessionTime >= 81 + 1.5).LapLastLapTime);
        Assert.Equal(82f, frames.First(f => f.SessionTime >= 163 + 1.5).LapLastLapTime);
    }

    [Fact]
    public void Range_mutators_apply_only_inside_their_lap_window()
    {
        var frames = Run(new LapSim().PitRoad(2, 0.9, 1.0).Caution(2, 0.2, 0.3, 0x8000));
        Assert.All(frames, f => Assert.Equal(f.Lap == 2 && f.LapDistPct >= 0.9f, f.OnPitRoad));
        Assert.All(frames, f => Assert.Equal(f.Lap == 2 && f.LapDistPct >= 0.2f && f.LapDistPct < 0.3f ? 0x8000u : 0u, f.SessionFlags));
    }

    [Fact]
    public void Point_mutators_fire_once_at_their_position()
    {
        var frames = Run(new LapSim().Incident(2, 0.3, 2).Refuel(2, 0.5, 1.0).OffTrack(2, 0.4, 0.6).Tow(3, 0.5));
        var lap2 = Lap(frames, 2);
        Assert.Equal(0, lap2.First().PlayerCarMyIncidentCount);
        Assert.Equal(2, lap2.First(f => f.LapDistPct >= 0.3f).PlayerCarMyIncidentCount);
        Assert.Equal(2, frames[^1].PlayerCarMyIncidentCount);

        int iRefuel = Array.FindIndex(frames, f => f.Lap == 2 && f.LapDistPct >= 0.5f);
        Assert.Equal(1.0, frames[iRefuel].FuelLevel - frames[iRefuel - 1].FuelLevel, 0.01);

        double off = frames.Count(f => f.PlayerTrackSurface == LapSim.SurfaceOffTrack) / 60.0;
        Assert.Equal(0.6, off, 1.0 / 60 + 1e-9);
        Assert.All(frames.Where(f => f.PlayerTrackSurface == LapSim.SurfaceOffTrack), f => Assert.Equal(2, f.Lap));

        var towed = frames.Where(f => f.PlayerCarTowTime > 0).ToArray();
        Assert.Equal(LapSim.TowDurationS, towed.Length / 60.0, 1.0 / 60 + 1e-9);
        Assert.All(towed, f => Assert.Equal(3, f.Lap));
    }

    [Fact]
    public void Time_jumps_shift_session_time_but_not_the_trajectory()
    {
        var plain = Run(new LapSim());
        var gap = Run(new LapSim().TimeGap(2, 0.5, 3.0));
        var back = Run(new LapSim().TimeBackwards(2, 0.5, 10));
        Assert.Equal(plain.Length, gap.Length);
        Assert.Equal(plain.Select(f => f.LapDistPct), gap.Select(f => f.LapDistPct));
        Assert.Equal(plain.Select(f => f.FuelLevel), back.Select(f => f.FuelLevel));

        int i = Array.FindIndex(plain, f => f.Lap == 2 && f.LapDistPct >= 0.5f);
        Assert.Equal(1.0 / 60 + 3.0, gap[i].SessionTime - gap[i - 1].SessionTime, 1e-9);
        Assert.Equal(1.0 / 60 - 10.0, back[i].SessionTime - back[i - 1].SessionTime, 1e-9);
        Assert.Equal(plain[^1].SessionTime + 3.0, gap[^1].SessionTime, 1e-9);
    }

    [Fact]
    public void Lap_increments_early_by_the_requested_frames()
    {
        var plain = Run(new LapSim(), laps: 1);
        var early = Run(new LapSim().IncrementLapEarlyFrames(3), laps: 1);
        int cross = Array.FindIndex(plain, f => f.Lap == 2);
        Assert.Equal(cross - 3, Array.FindIndex(early, f => f.Lap == 2));
        Assert.Equal(plain.Select(f => f.LapCompleted), early.Select(f => f.LapCompleted));
    }

    [Fact]
    public void Session_limits_count_down()
    {
        var laps = Run(new LapSim().SessionLaps(10));
        Assert.Equal(10, laps[0].SessionLapsRemainEx);
        Assert.Equal(7, laps[^1].SessionLapsRemainEx);

        var timed = Run(new LapSim().TimeRace(1800));
        Assert.Equal(1800, timed[0].SessionTimeRemain);
        Assert.Equal(1800 - timed[^1].SessionTime, timed[^1].SessionTimeRemain, 1e-9);
    }

    [Fact]
    public void Start_position_and_per_lap_overrides()
    {
        var frames = Run(new LapSim(startPct: 0.5, startLap: 5, startFuelL: 50).FuelPerLap(2.0, 3.0), laps: 2);
        Assert.Equal(5, frames[0].Lap);
        Assert.Equal(0.5f, frames[0].LapDistPct);
        // lap 5 from 0.5: 2.0 × (1 − f(0.5)); lap 6: 3.0; lap 7 (default 2.4) to the end pct
        double expected = 50 - 2.0 * (1 - LapSim.FuelCurve(0.5)) - 3.0 - 2.4 * LapSim.FuelCurve(frames[^1].LapDistPct);
        Assert.Equal(expected, frames[^1].FuelLevel, 0.001);

        var sim = new LapSim();
        Assert.Same(sim, sim.Leader());
    }
}
