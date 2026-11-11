using System.Globalization;
using FuelPace.App.Recording;
using FuelPace.Core.Baselines;
using FuelPace.Core.Storage;
using FuelPace.Core.Telemetry;

namespace FuelPace.App.Simulation;

/// <summary>
/// A synthetic time race as an <c>.ibt</c>: a <see cref="LapSim"/> player (car 3, 20 s laps at 60 Hz) plus 20 cars in the
/// CarIdx arrays, each with its own lap time (the fastest, car 0, leads). The player makes a pit stop with a refuel on
/// lap 5, a caution covers part of lap 8, and there is an incident on lap 3, so a run crosses many laps and exercises
/// every branch of the strategy. Shared with the Bench project through <c>&lt;Compile Include&gt;</c>.
/// <para>
/// The FakeSim publishes it live with its own car count and with each car's <c>CarIdxLastLapTime</c> arriving some time
/// after its <c>CarIdxLapCompleted</c> increment, as the sim does (the order <see cref="Core.Race.LeaderPaceTracker"/> waits
/// for); the default race keeps both in the same frame.
/// </para>
/// </summary>
public static class SyntheticRace
{
    public const double LapTimeS = 20, Hz = 60;
    public const int FramesPerLap = 1200, PlayerCarIdx = 3, Cars = 20;
    public static readonly DateTimeOffset T0 = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly string RaceSession = SampleYaml.SessionBlock(0, "Race", "unlimited", "3600.0000 sec");

    /// <summary>A one-hour race (no lap count), so the time-race projection and the leader pace are in use.</summary>
    public static string Yaml { get; } = SampleYaml.WithSessions(RaceSession);

    /// <summary><see cref="Yaml"/> once <paramref name="lapsComplete"/> laps are complete: the race session carries
    /// <c>ResultsLapsComplete</c>, as the sim's results do (a new text, so a new <c>sessionInfoUpdate</c>, same session).</summary>
    public static string YamlWithResults(int lapsComplete) =>
        SampleYaml.WithSessions(RaceSession + "\n   ResultsLapsComplete: " + lapsComplete.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whole lap crossings of the player among the frame indices <c>[from, to)</c>.</summary>
    public static int LapsCrossed(int from, int to) => (to - 1) / FramesPerLap - (from - 1) / FramesPerLap;

    /// <summary>Writes <paramref name="laps"/> laps of the synthetic race (<c>laps × 1200 + 1</c> frames) to <paramref name="path"/>.</summary>
    public static string WriteIbt(string path, int laps, bool withCarIdx)
    {
        using var writer = new IbtStreamWriter(path, (int)Hz, reservedYamlBytes: 16 * 1024, includeCarIdx: withCarIdx);
        writer.WriteSessionInfo(Yaml);
        foreach (var f in Frames(laps, withCarIdx)) writer.Append(f);
        return path;
    }

    /// <summary>The profile the app would load: a six-lap baseline recorded under the same BoP and tank as the session.</summary>
    public static ProfileDocument Profile(SessionInfo info)
    {
        LapSample[] laps =
        [
            new(2.382, 80.897, 64, null), new(2.404, 80.272, 66, null), new(2.373, 79.964, 65, null),
            new(2.403, 79.514, 67, null), new(2.389, 79.134, 64, null), new(2.289, 81.062, 58, null),
        ];
        var doc = ProfileDocument.CreateFor(info);
        doc.Baseline = BaselineCalculator.Compute(laps, info.TankCapacityL, [new BopSignature(0, 0, info.TankCapacityL)], T0);
        return doc;
    }

    /// <summary>Car <paramref name="car"/>'s place by pace among <paramref name="cars"/> (0 is the fastest): a permutation
    /// that spreads the fast cars over the indices (×7 when 7 does not divide the count).</summary>
    private static int Rank(int car, int cars) => cars % 7 != 0 ? car * 7 % cars : car;

    private static double CarLapTimeS(int rank) => 19.0 + 0.2 * rank;

    /// <summary>The frames <see cref="WriteIbt"/> writes.</summary>
    public static IEnumerable<TelemetryFrame> Frames(int laps, bool withCarIdx) =>
        Frames(laps, withCarIdx ? Cars : 0, carLapTimeDelayS: 0);

    /// <param name="cars">Cars in the CarIdx arrays (0 to 64; 0 leaves the arrays empty, as a file without them).</param>
    /// <param name="carLapTimeDelayS">How long after a car's <c>CarIdxLapCompleted</c> increment its <c>CarIdxLastLapTime</c>
    /// shows the new lap (0: the same frame).</param>
    public static IEnumerable<TelemetryFrame> Frames(int laps, int cars, double carLapTimeDelayS)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cars);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cars, CarArray<int>.Length);
        if (!(carLapTimeDelayS >= 0)) throw new ArgumentOutOfRangeException(nameof(carLapTimeDelayS), "must be >= 0");
        return Generate(laps, cars, carLapTimeDelayS);
    }

    private static IEnumerable<TelemetryFrame> Generate(int laps, int cars, double carLapTimeDelayS)
    {
        var sim = new LapSim(lapTimeS: LapTimeS, fuelPerLapL: 2.4, startFuelL: 80, hz: Hz)
            .TimeRace(3600)
            .Incident(3, 0.5)
            .PitRoad(5, 0.2, 0.4).Refuel(5, 0.3, 10)
            .Caution(8, 0.3, 0.6);
        foreach (var frame in sim.Run(laps))
        {
            var f = frame;
            f.PlayerCarIdx = PlayerCarIdx;
            if (cars > 0) FillCars(ref f, cars, carLapTimeDelayS);
            yield return f;
        }
    }

    private static void FillCars(ref TelemetryFrame f, int cars, double delayS)
    {
        for (int c = 0; c < CarArray<int>.Length; c++)
        {
            f.CarIdxLapCompleted[c] = -1; f.CarIdxTrackSurface[c] = -1;
        }
        double t = f.SessionTime;
        for (int c = 0; c < cars; c++)
        {
            int rank = Rank(c, cars);
            double lapTime = CarLapTimeS(rank), g = t / lapTime;
            int done = (int)g;
            int timed = delayS > 0 ? (int)Math.Max(0, (t - delayS) / lapTime) : done; // laps whose time is out
            f.CarIdxPosition[c] = rank + 1;
            f.CarIdxLapCompleted[c] = done;
            f.CarIdxTrackSurface[c] = 3;
            f.CarIdxClass[c] = 1;
            f.CarIdxLapDistPct[c] = (float)(g - done);
            f.CarIdxLastLapTime[c] = timed >= 1 ? (float)(lapTime + 0.01 * (timed % 3)) : -1f;
            f.CarIdxOnPitRoad[c] = c == 7 && done == 4; // one rival pits: that lap must not count for its pace
        }
    }
}
