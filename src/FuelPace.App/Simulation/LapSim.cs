using FuelPace.Core.Telemetry;

namespace FuelPace.App.Simulation;

/// <summary>
/// Deterministic synthetic driver on a 4000 m track. Frames are generated lazily at <c>hz</c>; the car moves at a
/// constant speed within each lap (<c>Speed = trackLen / lapTime</c>). Cumulative fuel used in a lap at
/// <c>p</c> is <c>(p − 0.04·sin(πp)) × fuelPerLap</c>. Mutators are chainable and address laps by their
/// <c>Lap</c> number; point events (incident, refuel, tow, offtrack start, time gaps) fire once when the car
/// passes <c>(lap, atPct)</c>, never on the very first frame.
/// </summary>
public sealed class LapSim
{
    public const double TrackLengthM = 4000;
    public const double TankCapacityL = 100;
    public const float FullThrottle = 1f, PartThrottle = 0.4f;
    public const int SurfaceOnTrack = 3, SurfaceOffTrack = 0;
    public const int UnlimitedLaps = 32767;
    public const double UnlimitedTimeRemainS = 604800;
    /// <summary>How long <c>PlayerCarTowTime</c> stays positive (counting down) after <see cref="Tow"/>.</summary>
    public const double TowDurationS = 1.0;

    private enum Kind { Incident, Refuel, TimeShift, OffTrack, Tow }
    private readonly record struct PointEvent(Kind Kind, int Lap, double AtPct, double Value);
    private readonly record struct RangeEvent(int Lap, double FromPct, double ToPct, uint Flags);

    private readonly double _lapTimeS, _fuelPerLapL, _startFuelL, _startPct, _officialDelayS, _ftFraction, _hz;
    private readonly int _startLap;
    private readonly bool _publishOfficialTimes;
    private double[] _lapTimes = [], _fuelPerLap = [];
    private readonly List<PointEvent> _points = [];
    private readonly List<RangeEvent> _pit = [], _caution = [];
    private int _earlyFrames;
    private int _sessionLaps = -1;
    private double _timeRaceS = double.NaN;

    public LapSim(double lapTimeS = 80, double fuelPerLapL = 2.4, double startFuelL = 100, double startPct = 0.0,
        int startLap = 1, double officialTimeDelayS = 1.5, bool publishOfficialTimes = true,
        double fullThrottleFraction = 0.65, double hz = 60)
    {
        _lapTimeS = lapTimeS; _fuelPerLapL = fuelPerLapL; _startFuelL = startFuelL; _startPct = startPct;
        _startLap = startLap; _officialDelayS = officialTimeDelayS; _publishOfficialTimes = publishOfficialTimes;
        _ftFraction = fullThrottleFraction; _hz = hz;
    }

    /// <summary>Lap times for laps <c>startLap, startLap+1, …</c>; laps beyond the list use <c>lapTimeS</c>.</summary>
    public LapSim LapTimes(params double[] times) { _lapTimes = times; return this; }

    /// <summary>Fuel per lap for laps <c>startLap, startLap+1, …</c>; laps beyond the list use <c>fuelPerLapL</c>.</summary>
    public LapSim FuelPerLap(params double[] liters) { _fuelPerLap = liters; return this; }

    /// <summary><c>PlayerTrackSurface = 0</c> for <paramref name="seconds"/> of driving from <c>(lap, fromPct)</c>.</summary>
    public LapSim OffTrack(int lap, double fromPct, double seconds) { _points.Add(new(Kind.OffTrack, lap, fromPct, seconds)); return this; }

    /// <summary><c>PlayerCarMyIncidentCount</c> grows by <paramref name="count"/> at <c>(lap, atPct)</c>.</summary>
    public LapSim Incident(int lap, double atPct, int count = 1) { _points.Add(new(Kind.Incident, lap, atPct, count)); return this; }

    /// <summary><c>OnPitRoad</c> while on <paramref name="lap"/> with <c>fromPct ≤ pct &lt; toPct</c> (<c>toPct ≥ 1</c>: to the line).</summary>
    public LapSim PitRoad(int lap, double fromPct, double toPct) { _pit.Add(new(lap, fromPct, toPct, 0)); return this; }

    /// <summary><c>SessionFlags |= flags</c> while on <paramref name="lap"/> with <c>fromPct ≤ pct &lt; toPct</c>.</summary>
    public LapSim Caution(int lap, double fromPct, double toPct, uint flags = 0x4000) { _caution.Add(new(lap, fromPct, toPct, flags)); return this; }

    /// <summary><c>FuelLevel</c> jumps up by <paramref name="liters"/> at <c>(lap, atPct)</c>.</summary>
    public LapSim Refuel(int lap, double atPct, double liters) { _points.Add(new(Kind.Refuel, lap, atPct, liters)); return this; }

    /// <summary><c>PlayerCarTowTime</c> counts down from <see cref="TowDurationS"/> to 0 starting at <c>(lap, atPct)</c>; the trajectory is unchanged.</summary>
    public LapSim Tow(int lap, double atPct) { _points.Add(new(Kind.Tow, lap, atPct, TowDurationS)); return this; }

    /// <summary><c>SessionTime</c> jumps forward by <paramref name="seconds"/> at <c>(lap, atPct)</c>; position and fuel follow driving time.</summary>
    public LapSim TimeGap(int lap, double atPct, double seconds) { _points.Add(new(Kind.TimeShift, lap, atPct, seconds)); return this; }

    /// <summary><c>SessionTime</c> jumps back by <paramref name="seconds"/> at <c>(lap, atPct)</c>; position and fuel follow driving time.</summary>
    public LapSim TimeBackwards(int lap, double atPct, double seconds) { _points.Add(new(Kind.TimeShift, lap, atPct, -seconds)); return this; }

    /// <summary><c>Lap</c> increments <paramref name="frames"/> frames before the frame that crosses the line.</summary>
    public LapSim IncrementLapEarlyFrames(int frames) { _earlyFrames = frames; return this; }

    /// <summary>Race by laps: <c>SessionLapsRemainEx = total − LapCompleted</c> (default 32767 = unlimited).</summary>
    public LapSim SessionLaps(int total) { _sessionLaps = total; return this; }

    /// <summary>Race by time: <c>SessionTimeRemain = max(0, totalS − SessionTime)</c> (default 604800 = unlimited).</summary>
    public LapSim TimeRace(double totalS) { _timeRaceS = totalS; return this; }

    /// <summary>Placeholder for the leader/CarIdx simulation (Task 14). Currently a no-op: <c>HasCarIdx</c> stays false.</summary>
    public LapSim Leader() => this;

    private double LapTime(int lap)
    {
        int i = lap - _startLap;
        return i >= 0 && i < _lapTimes.Length ? _lapTimes[i] : _lapTimeS;
    }

    private double LapFuel(int lap)
    {
        int i = lap - _startLap;
        return i >= 0 && i < _fuelPerLap.Length ? _fuelPerLap[i] : _fuelPerLapL;
    }

    public static double FuelCurve(double p) => p - 0.04 * Math.Sin(Math.PI * p);

    /// <summary>
    /// Drives from <c>(startLap, startPct)</c> across the line <paramref name="laps"/> times, then on to
    /// <paramref name="endPct"/> of the following lap; the first frame at or past that point is the last one.
    /// </summary>
    public IEnumerable<TelemetryFrame> Run(int laps, double endPct = 0.0)
    {
        int lap = _startLap, crossings = 0;
        double pct = _startPct, lapStartPct = _startPct;
        double consumedBeforeLap = 0, refueled = 0, timeOffset = 0;
        int incidents = 0;
        double offTrackUntil = double.NegativeInfinity, towUntil = double.NegativeInfinity;
        var fired = new bool[_points.Count];
        var publishes = new List<(double Tau, double Value)>();
        int publishIdx = 0;
        float llt = -1f;
        var queue = new Queue<TelemetryFrame>();
        double prevTau = 0;

        for (long i = 0; ; i++)
        {
            double tau = i / _hz;
            double gPrev = lap - 1 + pct;
            if (i > 0)
            {
                double remaining = tau - prevTau;
                while (true)
                {
                    double t = LapTime(lap);
                    double need = (1 - pct) * t;
                    if (remaining < need) { pct += remaining / t; break; }
                    remaining -= need;
                    consumedBeforeLap += LapFuel(lap) * (FuelCurve(1) - FuelCurve(lapStartPct));
                    publishes.Add((tau - remaining + _officialDelayS, t));
                    lap++; crossings++;
                    pct = 0; lapStartPct = 0;
                }
            }
            prevTau = tau;
            double gCur = lap - 1 + pct;

            for (int e = 0; e < _points.Count; e++)
            {
                var ev = _points[e];
                double gEv = ev.Lap - 1 + ev.AtPct;
                if (fired[e] || !(gPrev < gEv && gEv <= gCur)) continue;
                fired[e] = true;
                switch (ev.Kind)
                {
                    case Kind.Incident: incidents += (int)ev.Value; break;
                    case Kind.Refuel: refueled += ev.Value; break;
                    case Kind.TimeShift: timeOffset += ev.Value; break;
                    case Kind.OffTrack: offTrackUntil = tau + ev.Value; break;
                    case Kind.Tow: towUntil = tau + ev.Value; break;
                }
            }

            while (publishIdx < publishes.Count && publishes[publishIdx].Tau <= tau)
                llt = (float)publishes[publishIdx++].Value;

            double fuel = _startFuelL + refueled - consumedBeforeLap
                - LapFuel(lap) * (FuelCurve(pct) - FuelCurve(lapStartPct));
            fuel = Math.Max(0, fuel);
            double sessionTime = tau + timeOffset;
            int lapCompleted = Math.Max(0, lap - 1);
            uint flags = 0;
            bool onPit = false;
            foreach (var r in _caution) if (InRange(r, lap, pct)) flags |= r.Flags;
            foreach (var r in _pit) if (InRange(r, lap, pct)) onPit = true;

            var f = new TelemetryFrame
            {
                SessionTime = sessionTime,
                SessionTimeRemain = double.IsNaN(_timeRaceS) ? UnlimitedTimeRemainS : Math.Max(0, _timeRaceS - sessionTime),
                SessionNum = 0,
                SessionState = 4,
                SessionLapsRemainEx = _sessionLaps < 0 ? UnlimitedLaps : Math.Max(0, _sessionLaps - lapCompleted),
                Lap = lap,
                LapCompleted = lapCompleted,
                PlayerTrackSurface = tau < offTrackUntil ? SurfaceOffTrack : SurfaceOnTrack,
                PlayerCarMyIncidentCount = incidents,
                PlayerCarIdx = 0,
                TrackWetness = 1,
                SessionFlags = flags,
                LapDistPct = Math.Min((float)pct, 0.99999994f),
                LapLastLapTime = _publishOfficialTimes ? llt : -1f,
                FuelLevel = (float)fuel,
                FuelLevelPct = (float)(fuel / TankCapacityL),
                PlayerCarTowTime = tau < towUntil ? (float)(towUntil - tau) : 0f,
                Speed = (float)(TrackLengthM / LapTime(lap)),
                Throttle = pct < _ftFraction ? FullThrottle : PartThrottle,
                TrackTempCrew = 30f,
                AirTemp = 22f,
                OnPitRoad = onPit,
                IsOnTrack = true,
            };

            bool last = crossings >= laps && pct >= endPct;
            queue.Enqueue(f);
            if (queue.Count > _earlyFrames)
            {
                var head = queue.Dequeue();
                head.Lap = lap; // Lap of the frame _earlyFrames ahead
                yield return head;
            }
            if (last) break;
        }
        while (queue.Count > 0)
        {
            // No lookahead past the end: the tail takes the Lap of the last generated frame.
            var head = queue.Dequeue();
            head.Lap = lap;
            yield return head;
        }
    }

    private static bool InRange(RangeEvent r, int lap, double pct) =>
        lap == r.Lap && pct >= r.FromPct && (r.ToPct >= 1 || pct < r.ToPct);
}
