using FuelPace.App.Diagnostics;
using FuelPace.App.Overlay;
using FuelPace.App.Settings;
using FuelPace.Core.Diagnostics;

namespace FuelPace.App.Tests.Overlay;

public sealed class BackendSelectorTests
{
    const int DeviceRemoved = unchecked((int)0x887A0005);

    sealed class FakeComposition(bool works)
    {
        public int Calls;
        public bool Try() { Calls++; return works; }
    }

    static List<DiagRecord> Records(DiagLog log)
    {
        var buf = new DiagRecord[16];
        int n = log.DrainRecords(buf);
        return buf.Take(n).ToList();
    }

    static List<DiagText> Texts(DiagLog log)
    {
        var r = new List<DiagText>();
        while (log.TryDequeueText(out var t)) r.Add(t);
        return r;
    }

    [Fact]
    public void Auto_uses_composition_when_it_starts()
    {
        var f = new FakeComposition(works: true);
        Assert.Equal(RenderBackend.Composition, BackendSelection.Choose(RenderBackend.Auto, f.Try, out var reason));
        Assert.Equal(1, f.Calls);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Auto_falls_back_to_layered_with_the_reason_when_composition_fails()
    {
        var f = new FakeComposition(works: false);
        Assert.Equal(RenderBackend.Layered, BackendSelection.Choose(RenderBackend.Auto, f.Try, out var reason));
        Assert.Equal(1, f.Calls);
        Assert.Contains("composition", reason);
        Assert.Contains("layered", reason);
    }

    [Fact]
    public void A_throwing_composition_factory_counts_as_a_failure()
    {
        Assert.Equal(RenderBackend.Layered,
            BackendSelection.Choose(RenderBackend.Auto, () => throw new InvalidOperationException("no DComp"), out var reason));
        Assert.Contains("no DComp", reason);
    }

    [Fact]
    public void Forced_composition_falls_back_to_layered_and_warns()
    {
        var f = new FakeComposition(works: false);
        var chosen = BackendSelection.Choose(RenderBackend.Composition, f.Try, out var reason);
        Assert.Equal(RenderBackend.Layered, chosen);
        Assert.Equal(1, f.Calls);

        AppDiagEvents.RegisterAll();
        var log = new DiagLog(DiagLevel.Trace);
        BackendSelection.Report(log, RenderBackend.Composition, chosen, reason, DeviceRemoved);
        var r = Assert.Single(Records(log));
        Assert.Equal(AppDiagEvents.OverlayBackend, r.Event);
        Assert.Equal(DiagLevel.Warn, r.Level);
        Assert.Equal((int)RenderBackend.Layered, r.I0);
        Assert.Equal(DeviceRemoved, r.I1);
        var t = Assert.Single(Texts(log));
        Assert.Equal(DiagLevel.Warn, t.Level);
        Assert.Contains(reason, t.Message);
        Assert.Contains("0x887A0005", t.Message);
    }

    [Fact]
    public void Automatic_fallback_and_success_are_reported_at_info()
    {
        var log = new DiagLog(DiagLevel.Trace);
        BackendSelection.Report(log, RenderBackend.Auto, RenderBackend.Layered, "x", DeviceRemoved);
        BackendSelection.Report(log, RenderBackend.Auto, RenderBackend.Composition, "y", 0);
        var rs = Records(log);
        Assert.Equal(2, rs.Count);
        Assert.All(rs, r => Assert.Equal(DiagLevel.Info, r.Level));
        Assert.Equal((int)RenderBackend.Composition, rs[1].I0);
    }

    [Fact]
    public void Forced_layered_never_tries_composition()
    {
        var f = new FakeComposition(works: true);
        Assert.Equal(RenderBackend.Layered, BackendSelection.Choose(RenderBackend.Layered, f.Try, out var reason));
        Assert.Equal(0, f.Calls);
        Assert.Contains("layered", reason);
    }
}
