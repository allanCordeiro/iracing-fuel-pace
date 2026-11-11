using FuelPace.App.Simulation;
using FuelPace.Core.Race;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Tests.Simulation;

/// <summary>The CarIdx side of the synthetic race the FakeSim publishes: enough for the leader logic.</summary>
public class SyntheticRaceTests
{
    [Fact]
    public void The_twenty_car_race_without_delay_is_the_default_race()
    {
        var a = SyntheticRace.Frames(2, withCarIdx: true).ToArray();
        var b = SyntheticRace.Frames(2, cars: SyntheticRace.Cars, carLapTimeDelayS: 0).ToArray();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i += 97) AssertSameCars(a[i], b[i], i);

        var none = SyntheticRace.Frames(1, withCarIdx: false).ToArray();
        var zero = SyntheticRace.Frames(1, cars: 0, carLapTimeDelayS: 0).ToArray();
        Assert.Equal(none.Length, zero.Length);
        for (int i = 0; i < none.Length; i += 97) AssertSameCars(none[i], zero[i], i);
    }

    [Fact]
    public void A_car_lap_time_arrives_after_its_lap_completed_increment()
    {
        var frames = SyntheticRace.Frames(2, cars: 20, carLapTimeDelayS: 1.5).ToArray();
        int inc = Array.FindIndex(frames, f => f.CarIdxLapCompleted[0] == 1);
        Assert.True(inc > 0);
        Assert.Equal(-1f, frames[inc].CarIdxLastLapTime[0]); // the lap counts first, the time comes later
        int time = Array.FindIndex(frames, f => f.CarIdxLastLapTime[0] > 0);
        Assert.InRange(frames[time].SessionTime - frames[inc].SessionTime, 1.5 - 1 / 60.0, 1.5 + 1 / 60.0);
        Assert.Equal(19.01f, frames[time].CarIdxLastLapTime[0], 3); // car 0: 19 s laps (+0.01 on lap 1)
        Assert.Equal(1, frames[time].CarIdxLapCompleted[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(20)]
    [InlineData(64)]
    public void Positions_are_one_to_cars_and_the_leader_is_the_fastest(int cars)
    {
        var last = SyntheticRace.Frames(3, cars, carLapTimeDelayS: 1.5).Last();
        var positions = new List<int>();
        for (int c = 0; c < cars; c++) positions.Add(last.CarIdxPosition[c]);
        Assert.Equal(Enumerable.Range(1, cars), positions.Order());
        int leader = positions.IndexOf(1);
        for (int c = 0; c < cars; c++)
        {
            Assert.True(last.CarIdxLapCompleted[leader] + last.CarIdxLapDistPct[leader]
                >= last.CarIdxLapCompleted[c] + last.CarIdxLapDistPct[c], $"car {c} ahead of the leader");
        }
        for (int c = cars; c < CarArray<int>.Length; c++)
        {
            Assert.Equal(-1, last.CarIdxLapCompleted[c]); // no such car
            Assert.Equal(-1, last.CarIdxTrackSurface[c]);
        }
    }

    [Fact]
    public void The_leader_tracker_takes_the_rival_laps_but_not_the_pit_lap()
    {
        var tracker = new LeaderPaceTracker();
        foreach (var f in SyntheticRace.Frames(6, cars: 20, carLapTimeDelayS: 1.5)) tracker.Update(in f);
        Assert.Equal(0, tracker.Current.LeaderCarIdx);
        Assert.Equal(LeaderPaceSource.Mean, tracker.Current.Source);
        Assert.InRange(tracker.Current.PaceS, 19.0, 19.03);
        Assert.Equal(6, tracker.ValidLapCount(0));
        // Car 7 is on pit road from the line ending its 4th lap to the line ending its 5th: in-lap and out-lap don't count.
        Assert.Equal(3, tracker.ValidLapCount(7));
    }

    [Fact]
    public void Car_count_is_checked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticRace.Frames(1, cars: 65, carLapTimeDelayS: 0).First());
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticRace.Frames(1, cars: -1, carLapTimeDelayS: 0).First());
        Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticRace.Frames(1, cars: 20, carLapTimeDelayS: -1).First());
    }

    [Fact]
    public void Session_info_with_results_parses_as_the_same_session()
    {
        var race = SessionInfoParser.Parse(SyntheticRace.Yaml);
        var after3 = SyntheticRace.YamlWithResults(3);
        Assert.Contains("ResultsLapsComplete: 3", after3);
        Assert.NotEqual(after3, SyntheticRace.YamlWithResults(4));
        var info = SessionInfoParser.Parse(after3);
        Assert.Equal(race.ProfileKey, info.ProfileKey);
        Assert.Equal(race.Sessions.Count, info.Sessions.Count);
        Assert.Equal(race.Sessions[0].TimeS, info.Sessions[0].TimeS);
    }

    private static void AssertSameCars(in TelemetryFrame a, in TelemetryFrame b, int i)
    {
        Assert.Equal(a.SessionTime, b.SessionTime);
        Assert.Equal(a.PlayerCarIdx, b.PlayerCarIdx);
        for (int c = 0; c < CarArray<int>.Length; c++)
        {
            Assert.True(a.CarIdxPosition[c] == b.CarIdxPosition[c] && a.CarIdxLapCompleted[c] == b.CarIdxLapCompleted[c]
                && a.CarIdxTrackSurface[c] == b.CarIdxTrackSurface[c] && a.CarIdxClass[c] == b.CarIdxClass[c]
                && a.CarIdxLapDistPct[c] == b.CarIdxLapDistPct[c] && a.CarIdxLastLapTime[c] == b.CarIdxLastLapTime[c]
                && a.CarIdxOnPitRoad[c] == b.CarIdxOnPitRoad[c], $"frame {i} car {c} differs");
        }
    }
}
