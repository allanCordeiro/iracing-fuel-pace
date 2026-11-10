using System.Text;
using FuelPace.App.Overlay;
using FuelPace.Core.Strategy;

namespace FuelPace.Preview;

/// <summary>Writes the overlay review previews (SVG) from fixed states that reproduce the approved mocks.</summary>
public static class Program
{
    const int Padding = 40;

    public static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine("docs", "overlay-preview");
        Directory.CreateDirectory(dir);

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
        Write(dir, "azul", in blue, normal);
        Write(dir, "vermelho", in red, normal);
        Write(dir, "nao-factivel", in notFeasible, normal);
        Write(dir, "treino-sem-baseline", in practice, normal);
        Write(dir, "edicao", in blue, Options(scale: 2, edit: true));
        Write(dir, "inativo", in inactive, normal);
        Write(dir, "opacidade-30", in blue, Options(bg: 0.3));
        Write(dir, "opacidade-90", in blue, Options(bg: 0.9));
        Write(dir, "escala-200", in blue, Options(scale: 2));
        Console.WriteLine($"9 previews written to {dir}");
        return 0;
    }

    static OverlayViewOptions Options(double scale = 1, double bg = 0.6, bool edit = false) =>
        new(scale, bg, 1, false, true, edit, false, "Ctrl+Shift+F", 1840, 980);

    static void Write(string dir, string name, in OverlayState s, OverlayViewOptions o)
    {
        var list = new DrawList();
        OverlayLayout.Build(in s, "Ligier", "Zolder Alt", o, new FixedMetricsMeasurer(), list);
        File.WriteAllText(Path.Combine(dir, name + ".svg"), SvgRenderer.Render(list, Padding, true), new UTF8Encoding(false));
    }
}
