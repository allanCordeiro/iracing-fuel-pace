namespace FuelPace.Tests.Support;

public static class SampleYaml
{
    public static string WithSessions(params string[] sessionBlocks) => Build(string.Join("\n", sessionBlocks), "1.000");

    public static string WithMaxFuelPct(string pct) => Build(Sessions, pct);

    public static string Zolder => Build(Sessions, "1.000");

    private const string Sessions =
        " - SessionNum: 0\n   SessionLaps: unlimited\n   SessionTime: 7200.0000 sec\n   SessionType: Practice\n" +
        " - SessionNum: 1\n   SessionLaps: 25\n   SessionTime: unlimited\n   SessionType: Race";

    public static string SessionBlock(int num, string type, string laps, string time) =>
        $" - SessionNum: {num}\n   SessionLaps: {laps}\n   SessionTime: {time}\n   SessionType: {type}";

    private static string Build(string sessions, string maxFuelPct) =>
        "---\n" +
        "WeekendInfo:\n" +
        " TrackName: zolder alt\n" +
        " TrackLength: 3.9342 km\n" +
        " TrackDisplayName: Zolder Circuit\n" +
        " TrackDisplayShortName: Zolder\n" +
        " TrackConfigName: Alternate\n" +
        "SessionInfo:\n" +
        " CurrentSessionNum: 0\n" +
        " Sessions:\n" +
        sessions + "\n" +
        "DriverInfo:\n" +
        " DriverCarIdx: 3\n" +
        " DriverCarFuelKgPerLtr: 0.750\n" +
        " DriverCarFuelMaxLtr: 100.000\n" +
        $" DriverCarMaxFuelPct: {maxFuelPct}\n" +
        " DriverCarEstLapTime: 78.9567\n" +
        " Drivers:\n" +
        " - CarIdx: 0\n" +
        "   UserName: Joe: The \"Rocket\"\n" +
        "   TeamName: Team #1\n" +
        "   CarPath: othercar\n" +
        "   CarScreenName: Other Car\n" +
        "   CarScreenNameShort: Other\n" +
        " - CarIdx: 3\n" +
        "   UserName: Allan Cordeiro\n" +
        "   TeamName: Allan Cordeiro\n" +
        "   CarPath: ligierjsp320\n" +
        "   CarScreenName: Ligier JS P320\n" +
        "   CarScreenNameShort: Ligier\n" +
        "...\n";
}
