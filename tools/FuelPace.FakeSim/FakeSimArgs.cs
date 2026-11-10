using System.Globalization;
using FuelPace.Core.Telemetry;

namespace FuelPace.FakeSim;

/// <summary>
/// The FakeSim's command line: <c>&lt;file.ibt&gt;</c> or <c>--synthetic-race &lt;laps&gt;</c> (exactly one), <c>--speed 1|10|max</c>
/// (default 1), <c>--loop</c>, <c>--cars &lt;0..64&gt;</c> (synthetic race only, default 20), <c>--disconnect-after &lt;s&gt;</c>,
/// <c>--map &lt;name&gt;</c> and <c>--event &lt;name&gt;</c> (default: iRacing's names). A repeated option keeps its last value.
/// </summary>
public sealed record FakeSimArgs(string? IbtPath, int? SyntheticLaps, ReplaySpeed Speed, bool Loop, int Cars,
    TimeSpan? DisconnectAfter, string MapName, string EventName)
{
    /// <summary>iRacing's names, the ones the Agent opens unless <c>FUELPACE_IRSDK_MAP</c>/<c>FUELPACE_IRSDK_EVENT</c> say otherwise.</summary>
    public const string DefaultMapName = @"Local\IRSDKMemMapFileName", DefaultEventName = @"Local\IRSDKDataValidEvent";
    public const int DefaultCars = 20;

    public const string Usage =
        "FuelPace.FakeSim <file.ibt | --synthetic-race <laps>> [--speed 1|10|max] [--loop] [--cars 20] " +
        "[--disconnect-after <s>] [--map <name>] [--event <name>]";

    public static bool TryParse(IReadOnlyList<string> args, out FakeSimArgs result, out string? error)
    {
        result = new FakeSimArgs(null, null, ReplaySpeed.X1, false, DefaultCars, null, DefaultMapName, DefaultEventName);
        error = null;
        bool carsGiven = false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--loop")
            {
                result = result with { Loop = true };
                continue;
            }
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (result.IbtPath is not null) return Fail($"only one file can be played (got {result.IbtPath} and {a})", out error);
                result = result with { IbtPath = a };
                continue;
            }
            if (a is not ("--synthetic-race" or "--speed" or "--cars" or "--disconnect-after" or "--map" or "--event"))
                return Fail($"unknown option {a}", out error);
            if (i + 1 >= args.Count || args[i + 1].Length == 0) return Fail($"{a} needs a value", out error);
            string v = args[++i];
            switch (a)
            {
                case "--synthetic-race":
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int laps) || laps < 1)
                        return Fail($"--synthetic-race needs a number of laps >= 1 (got {v})", out error);
                    result = result with { SyntheticLaps = laps };
                    break;
                case "--speed":
                    ReplaySpeed? speed = v.ToLowerInvariant() switch
                    {
                        "max" => ReplaySpeed.Max, "1" => ReplaySpeed.X1, "10" => ReplaySpeed.X10, _ => null,
                    };
                    if (speed is null) return Fail($"--speed must be 1, 10 or max (got {v})", out error);
                    result = result with { Speed = speed.Value };
                    break;
                case "--cars":
                    if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int cars) || cars > CarArray<int>.Length)
                        return Fail($"--cars must be 0 to {CarArray<int>.Length} (got {v})", out error);
                    result = result with { Cars = cars };
                    carsGiven = true;
                    break;
                case "--disconnect-after":
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
                        || !double.IsFinite(s) || s < 0 || s > TimeSpan.MaxValue.TotalSeconds / 2)
                        return Fail($"--disconnect-after must be a number of seconds >= 0 (got {v})", out error);
                    result = result with { DisconnectAfter = TimeSpan.FromSeconds(s) };
                    break;
                case "--map":
                    result = result with { MapName = v };
                    break;
                default: // --event
                    result = result with { EventName = v };
                    break;
            }
        }
        if ((result.IbtPath is null) == (result.SyntheticLaps is null))
            return Fail("give exactly one source: a .ibt file or --synthetic-race <laps>", out error);
        if (carsGiven && result.IbtPath is not null)
            return Fail("--cars applies to --synthetic-race only (a file has its own cars)", out error);
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
