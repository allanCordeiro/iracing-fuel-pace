using FuelPace.Core.Strategy;

namespace FuelPace.App.Overlay;

/// <summary>One review preview: a fixed state and view that reproduce an approved mock.</summary>
public sealed record OverlayPreview(string Name, OverlayState State, OverlayViewOptions Options);

/// <summary>
/// The nine review previews (the approved mocks): the SVGs in <c>docs/overlay-preview</c> (tools/FuelPace.Preview) and the
/// Windows render test draw these same states. Car "Ligier", track "Zolder Alt".
/// </summary>
public static class OverlayPreviews
{
    public const string Car = "Ligier", Track = "Zolder Alt";

    public static IReadOnlyList<OverlayPreview> All { get; } = Build();

    static List<OverlayPreview> Build()
    {
        var blue = new OverlayState
        {
            State = SignalState.Blue, FuelL = 41.8, AutonomyLaps = 17.3, AutonomyMin = 23,
            LapProjectionL = 2.36, LastLapL = 2.39, MeanL = 2.38, TargetL = 2.49, LapsRemaining = 16.6,
            LastLapTimeS = 79.51, MeanLapTimeS = 80.14, BaselineLapTimeS = 80.12,
            Insight = new Insight(InsightKind.Slack, 1.8),
        };
        var red = blue;
        red.State = SignalState.Red; red.LapProjectionL = 2.42; red.LastLapL = 2.40; red.MeanL = 2.39;
        red.TargetL = 2.35; red.LapsRemaining = 17.6; red.Insight = new Insight(InsightKind.Save, 0.07, 3, 0.8);
        var notFeasible = blue;
        notFeasible.State = SignalState.NotFeasible; notFeasible.FuelL = 30.2; notFeasible.AutonomyLaps = 12.5; notFeasible.AutonomyMin = 17;
        notFeasible.LapProjectionL = 2.39; notFeasible.LastLapL = 2.38; notFeasible.MeanL = 2.38; notFeasible.TargetL = 2.01;
        notFeasible.FeasibleMinL = 2.23; notFeasible.LapsRemaining = 14.8; notFeasible.Insight = new Insight(InsightKind.StopRequired, 3.3);
        var practice = new OverlayState
        {
            State = SignalState.NoBaseline, FuelL = 88.1, AutonomyLaps = 36.8, AutonomyMin = 49,
            LapProjectionL = 2.40, LastLapL = 2.39, MeanL = 2.39, TargetL = double.NaN, LapsRemaining = double.NaN,
            IsPractice = true, Recording = true, RecValid = 4, RecTotal = 5,
            LastLapTimeS = double.NaN, MeanLapTimeS = double.NaN, BaselineLapTimeS = double.NaN,
            Insight = new Insight(InsightKind.NoBaseline),
        };
        var inactive = blue;
        inactive.State = SignalState.Inactive;

        var normal = Options();
        return
        [
            new("azul", blue, normal),
            new("vermelho", red, normal),
            new("nao-factivel", notFeasible, normal),
            new("treino-sem-baseline", practice, normal),
            new("edicao", blue, Options(scale: 2, edit: true)),
            new("inativo", inactive, normal),
            new("opacidade-30", blue, Options(bg: 0.3)),
            new("opacidade-90", blue, Options(bg: 0.9)),
            new("escala-200", blue, Options(scale: 2)),
        ];
    }

    static OverlayViewOptions Options(double scale = 1, double bg = 0.6, bool edit = false) =>
        new(scale, bg, 1, false, true, edit, false, "Ctrl+Shift+F", 1840, 980);
}
