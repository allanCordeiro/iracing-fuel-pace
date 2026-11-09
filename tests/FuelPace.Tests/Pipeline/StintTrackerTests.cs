using FuelPace.Core.Diagnostics;
using FuelPace.Core.Laps;
using FuelPace.Core.Pipeline;
using FuelPace.Core.Telemetry;

namespace FuelPace.Tests.Pipeline;

public class StintTrackerTests
{
    static TelemetryFrame Frame(double t, float fuel, bool pit = false) =>
        new() { SessionTime = t, FuelLevel = fuel, OnPitRoad = pit, IsOnTrack = true };

    static CompletedLap Lap(double start, double end, double fuelL, double timeS, LapReasons reasons = LapReasons.None)
    {
        var l = new CompletedLap
        {
            StartTime = start, EndTime = end, FuelUsedL = fuelL, TimeS = timeS, MeasuredTimeS = end - start, Reasons = reasons,
        };
        l.Stats.StartObserved = l.Stats.EndObserved = true;
        return l;
    }

    static void Drive(StintTracker s, double t, float fuel, bool pit = false)
    {
        var f = Frame(t, fuel, pit);
        s.Update(in f);
    }

    static void Complete(StintTracker s, CompletedLap lap) => s.OnLapCompleted(in lap);

    [Fact]
    public void Stint_mean_resets_after_pit_exit()
    {
        var s = new StintTracker(5);
        Drive(s, 0, 100);
        Complete(s, Lap(0, 80, 2.4, 80));
        Complete(s, Lap(80, 160, 2.5, 81));
        Complete(s, Lap(160, 240, 2.3, 79));
        Assert.Equal(2.4, s.MeanFuelL, 9);
        Assert.Equal(80, s.MeanLapS, 9);
        Assert.Equal(3, s.LapsDone);

        Drive(s, 250, 92, pit: true);
        Drive(s, 260, 92, pit: false); // pit exit: new stint
        Complete(s, Lap(240, 320, 2.6, 90, LapReasons.Pit)); // out-lap: counts as done, not in the mean
        Complete(s, Lap(320, 400, 2.4, 80));

        Assert.True(double.IsNaN(s.MeanFuelL));
        Assert.True(double.IsNaN(s.MeanLapS));
        Assert.Equal(2, s.LapsDone);
    }

    [Fact]
    public void Lap_that_ended_before_the_stint_started_is_ignored()
    {
        var s = new StintTracker(5);
        Drive(s, 0, 50, pit: true);
        Drive(s, 10, 50, pit: false);
        Complete(s, Lap(-70, 5, 2.4, 75));
        Assert.Equal(0, s.LapsDone);
        Assert.True(double.IsNaN(s.MeanFuelL));
    }

    [Fact]
    public void Fuel_rising_above_the_running_minimum_starts_a_new_stint()
    {
        var log = new DiagLog(DiagLevel.Info);
        var s = new StintTracker(5, log);
        Drive(s, 0, 52);
        Drive(s, 80, 50); // running minimum 50
        Complete(s, Lap(0, 80, 2.4, 80));
        Complete(s, Lap(80, 160, 2.4, 80));
        Assert.Equal(2, s.LapsDone);

        Drive(s, 160.1, 50.25f); // +0.25 L above the minimum: not a refuel
        Assert.Equal(2, s.LapsDone);
        Drive(s, 160.2, 50.35f); // +0.35 L
        Assert.Equal(0, s.LapsDone);
        Assert.True(double.IsNaN(s.MeanFuelL));

        var buf = new DiagRecord[16];
        var records = buf.AsSpan(0, log.DrainRecords(buf)).ToArray();
        var started = Assert.Single(records, r => r.Event == DiagEvent.StintStarted);
        Assert.Equal(1, started.I0);
        Assert.Equal(50.35, started.A, 3);
        Assert.Equal(160.2, started.SessionTime, 9);
    }

    [Fact]
    public void Gradual_pit_refuel_starts_one_stint_and_pit_exit_does_not_start_another()   // R14
    {
        var log = new DiagLog(DiagLevel.Info);
        var s = new StintTracker(5, log);
        double t = 0;
        float fuel = 10;
        Drive(s, t, fuel);
        Complete(s, Lap(-80, 0.5, 2.4, 80));
        Drive(s, t += 1, fuel, pit: true);
        for (int i = 0; i < 4 * 60; i++) // +2.5 L/s at 60 Hz for 4 s: +0.042 L per frame
        {
            fuel += 2.5f / 60;
            Drive(s, t += 1.0 / 60, fuel, pit: true);
        }
        for (int i = 0; i < 60; i++) Drive(s, t += 1.0 / 60, fuel, pit: true); // done fuelling, still in the stall
        Drive(s, t += 1.0 / 60, fuel, pit: false); // pit exit
        for (int i = 0; i < 600; i++) Drive(s, t += 1.0 / 60, fuel -= 0.0005f); // driving

        var buf = new DiagRecord[16];
        var started = Assert.Single(buf.AsSpan(0, log.DrainRecords(buf)).ToArray(), r => r.Event == DiagEvent.StintStarted);
        Assert.Equal(1, started.I0);
        Assert.InRange(started.A, 10.3, 10.4);
        Complete(s, Lap(1, t, 2.4, 80));
        Assert.Equal(1, s.LapsDone);
    }

    [Fact]
    public void Pit_exit_emits_StintStarted_with_cause_zero()
    {
        var log = new DiagLog(DiagLevel.Info);
        var s = new StintTracker(5, log);
        Drive(s, 0, 30, pit: true);
        Drive(s, 1, 30, pit: false);

        var buf = new DiagRecord[16];
        var records = buf.AsSpan(0, log.DrainRecords(buf)).ToArray();
        Assert.Contains(records, r => r.Event == DiagEvent.StintStarted && r.I0 == 0 && r.SessionTime == 1);
    }

    [Fact]
    public void Mean_uses_the_last_window_valid_laps_and_skips_invalid_or_open_ones()
    {
        var s = new StintTracker(2);
        Drive(s, 0, 100);
        Complete(s, Lap(0, 80, 3.0, 90));
        Assert.True(double.IsNaN(s.MeanFuelL)); // fewer than 2
        Complete(s, Lap(80, 160, 2.0, 80));
        Complete(s, Lap(160, 240, 9.0, 99, LapReasons.Incident));
        Complete(s, Lap(240, 320, 2.2, 82));
        var open = Lap(320, 330, 0.3, double.NaN, LapReasons.Incomplete);
        open.Stats.EndObserved = false; // lap in progress emitted by a flush: not done
        Complete(s, open);

        Assert.Equal(2.1, s.MeanFuelL, 9);
        Assert.Equal(81, s.MeanLapS, 9);
        Assert.Equal(4, s.LapsDone);
    }
}
