using FuelPace.App.Overlay;
using FuelPace.Core.Display;
using FuelPace.Core.Strategy;

namespace FuelPace.App.Tests.Overlay;

/// <summary>PRD v3 V3-10 (generation): the review previews and the files tools/FuelPace.Preview writes.</summary>
public sealed class GlancePreviewsTests
{
    static readonly string[] Expected = ["vermelho", "azul", "pit", "compacto", "inativo", "saturado", "sem-barra", "edicao", "escala-250"];

    static GlancePreview Get(string name) => GlancePreviews.All.Single(p => p.Name == name);

    static string[] Texts(GlancePreview p)
    {
        var d = new DrawList();
        var v = p.View;
        GlanceLayout.Build(in v, p.Options, new FixedMetricsMeasurer(), d);
        var r = new List<string>();
        foreach (var c in d.Commands)
            if (c.Kind == CommandKind.Text) r.Add(new string(d.TextOf(in c)));
        return [.. r];
    }

    [Fact]
    public void Previews_cover_the_mocks_and_the_edge_cases()
    {
        Assert.Equal(Expected, GlancePreviews.All.Select(p => p.Name));
    }

    [Fact]
    public void Previews_use_the_V3_3_numbers()
    {
        var red = Texts(Get("vermelho"));
        foreach (var t in new[] { "ECONOMIZE", "0.08", "−3%", "2.38", "2.39", "2.37", "74.0", " L · dá 31 v · faltam 32", "ALVO", "2.30" })
            Assert.Contains(t, red);
        Assert.Equal((sbyte)8, Get("vermelho").View.BarMarkerQ);
        Assert.Equal((sbyte)9, Get("vermelho").View.BarGhostQ);

        var blue = Texts(Get("azul"));
        foreach (var t in new[] { "OK", "FOLGA", "2.7", "L", "2.36", "2.37", " L · dá 31 v · faltam 30", "ATÉ", "2.45" })
            Assert.Contains(t, blue);
        Assert.Equal((sbyte)-9, Get("azul").View.BarMarkerQ);
        Assert.Equal((sbyte)-8, Get("azul").View.BarGhostQ);

        var pit = Texts(Get("pit"));
        foreach (var t in new[] { "PIT", "FALTAM", "2.3", " L · dá 31 v", "faltam 34", "alvo 2.16 ·", "MÍN", "2.23" })
            Assert.Contains(t, pit);
        Assert.False(Get("pit").View.ShowBar);

        var compact = Get("compacto");
        Assert.Equal(GlanceMode.Compact, compact.View.Mode);
        Assert.Equal(DecisionKind.None, compact.View.Decision);
        foreach (var t in new[] { "74.0", "dá 25 v · 45 min", "2.91", "2.92", "REC 4/9" })
            Assert.Contains(t, Texts(compact));

        var sat = Get("saturado").View;
        Assert.Equal((sbyte)sat.BarSteps, sat.BarMarkerQ);
        Assert.Equal(StatusPills.Rec | StatusPills.BopMismatch | StatusPills.Caution, sat.Pills);
        Assert.Contains("~2.30", Texts(Get("saturado")));
        Assert.False(Get("sem-barra").View.ShowBar);
        Assert.True(Get("edicao").Options.EditMode);
        Assert.Equal(2, Get("edicao").Options.Scale);
        Assert.Equal(2.5, Get("escala-250").Options.Scale);
        Assert.Equal(GlanceMode.Inactive, Get("inativo").View.Mode);
    }

    [Theory] // V3-3 / V3-10: the hand-made mock views are exactly what the builder makes of the V3-3 engine outputs
    [InlineData("vermelho")]
    [InlineData("azul")]
    [InlineData("pit")]
    [InlineData("compacto")]
    public void The_builder_makes_the_mock_views_from_the_V3_3_inputs(string name)
    {
        var input = GlancePreviews.Inputs[name];
        var built = GlanceViewBuilder.Build(in input, GlancePreviews.SampleConfig);
        Assert.Equal(Get(name).View, built);
    }

    [Fact] // B1: the inactive preview is what the app draws in the box, not a hand-made view
    public void The_inactive_preview_is_what_the_engine_and_builder_make()
    {
        var i = new StrategyInput
        {
            SessionTime = 100, FuelL = 74, LapDistPct = 0, LapProjectionL = double.NaN, LastLapL = 2.39, StintMeanL = 2.37,
            StintMeanLapS = 80.14, LastLapTimeS = 79.51, SessionTimeRemain = double.NaN, LeaderPaceS = double.NaN,
            LeaderLapDistPct = double.NaN, EstLapTimeS = double.NaN, IsRaceSession = true, SessionLapsRemainEx = 31,
            Race = RaceKind.Laps, Inactive = true,
        };
        var h = new SignalHysteresis();
        var engine = FuelStrategyEngine.Compute(in i, null, new StrategyConfig(), ref h);
        Assert.Equal(engine, GlancePreviews.InactiveInput);
        var built = GlanceViewBuilder.Build(in engine, GlancePreviews.SampleConfig);
        Assert.Equal(built, Get("inativo").View);
        var texts = Texts(Get("inativo"));
        Assert.Equal(["74.0", "L", "dá 31 v · 41 min"], texts);
    }

    [Fact]
    public void The_V3_3_inputs_are_the_mock_numbers()
    {
        Assert.Equal(new GlanceConfig(0.5, 0.20, false, true, true), GlancePreviews.SampleConfig);
        Assert.Equal(["azul", "compacto", "pit", "vermelho"], GlancePreviews.Inputs.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, s) in GlancePreviews.Inputs)
        {
            Assert.Equal(74.0, s.FuelL);
            if (name == "compacto") continue;
            Assert.Equal((2.37, 2.23), (s.MeanL, s.FeasibleMinL));
            Assert.Equal(73.5 / s.LapsRemaining, s.TargetL, 12); // A = (C − res) / L_rest
        }
    }

    [Fact]
    public void Preview_writes_one_svg_per_preview()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fuelpace-glance-" + Guid.NewGuid().ToString("N"));
        try
        {
            int n = GlancePreviews.WriteAll(dir);
            Assert.Equal(Expected.Length, n);
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(Expected.Select(e => e + ".svg").Order(StringComparer.Ordinal), files);
            foreach (var f in Directory.GetFiles(dir))
            {
                string svg = File.ReadAllText(f);
                Assert.StartsWith("<svg", svg);
                Assert.Contains("#3A4552", svg); // the game-scene backdrop
            }
            Assert.Contains("ECONOMIZE", File.ReadAllText(Path.Combine(dir, "vermelho.svg")));
            Assert.Contains("<polygon points=", File.ReadAllText(Path.Combine(dir, "saturado.svg")).Split("<g ")[1]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Committed_previews_match_the_generator()
    {
        string repo = AppContext.BaseDirectory;
        while (repo.Length > 0 && !File.Exists(Path.Combine(repo, "FuelPace.sln"))) repo = Path.GetDirectoryName(repo) ?? "";
        Assert.NotEqual("", repo);
        string dir = Path.Combine(repo, "docs", "v3", "preview");
        foreach (var name in Expected)
        {
            string committed = File.ReadAllText(Path.Combine(dir, name + ".svg")).ReplaceLineEndings("\n");
            Assert.Equal(GlancePreviews.Svg(Get(name)), committed);
        }
    }
}
