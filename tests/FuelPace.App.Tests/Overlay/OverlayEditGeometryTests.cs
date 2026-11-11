using FuelPace.App.Overlay;
using FuelPace.App.Placement;

namespace FuelPace.App.Tests.Overlay;

public sealed class OverlayEditGeometryTests
{
    static DrawList EditList(double scale = 1, double dpi = 1)
    {
        var p = OverlayPreviews.All.First(x => x.Name == "azul");
        var s = p.State;
        var d = new DrawList();
        OverlayLayout.Build(in s, OverlayPreviews.Car, OverlayPreviews.Track, p.Options with { EditMode = true, Scale = scale, DpiScale = dpi },
            new FixedMetricsMeasurer(), d);
        return d;
    }

    static List<RectF> Handles(DrawList d)
    {
        var r = new List<RectF>();
        foreach (var c in d.Commands)
            if (c.Kind == CommandKind.FillRect) r.Add(c.Rect);
        return r;
    }

    static (float X, float Y) Centre(RectF r) => (r.X + r.W / 2, r.Y + r.H / 2);

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 1.5)]
    public void Each_handle_is_its_corner_and_the_rest_drags(double scale, double dpi)
    {
        var d = EditList(scale, dpi);
        var hs = Handles(d);
        Assert.Equal(4, hs.Count); // top-left, top-right, bottom-left, bottom-right, in the layout's order
        OverlayHit[] expected = [OverlayHit.TopLeft, OverlayHit.TopRight, OverlayHit.BottomLeft, OverlayHit.BottomRight];
        for (int i = 0; i < 4; i++)
        {
            var (cx, cy) = Centre(hs[i]);
            Assert.Equal(expected[i], OverlayEditGeometry.HitTest(d, cx, cy));
            // a little outside the 7 px square still grabs it
            float dx = expected[i] is OverlayHit.TopLeft or OverlayHit.BottomLeft ? -hs[i].W * 0.8f : hs[i].W * 0.8f;
            Assert.Equal(expected[i], OverlayEditGeometry.HitTest(d, cx + dx, cy));
        }
        Assert.Equal(OverlayHit.Caption, OverlayEditGeometry.HitTest(d, d.Size.W / 2, d.Size.H / 2));
        Assert.Equal(OverlayHit.Caption, OverlayEditGeometry.HitTest(d, 2, 2)); // the banner drags too
    }

    [Fact]
    public void A_locked_list_has_no_handles()
    {
        var p = OverlayPreviews.All.First(x => x.Name == "azul");
        var s = p.State;
        var d = new DrawList();
        OverlayLayout.Build(in s, OverlayPreviews.Car, OverlayPreviews.Track, p.Options, new FixedMetricsMeasurer(), d);
        Assert.Equal(OverlayHit.Caption, OverlayEditGeometry.HitTest(d, 1, 1));
    }

    static readonly PixelRect Start = new(100, 200, 314, 180);

    [Fact]
    public void Bottom_right_grows_keeping_the_ratio_and_the_top_left_corner()
    {
        var r = OverlayEditGeometry.ProportionalResize(Start, OverlayHit.BottomRight, 157, 10, 40, out float f);
        Assert.Equal(1.5f, f, 3);
        Assert.Equal(new PixelRect(100, 200, 471, 270), r);
    }

    [Fact]
    public void Top_left_shrinks_keeping_the_bottom_right_corner()
    {
        var r = OverlayEditGeometry.ProportionalResize(Start, OverlayHit.TopLeft, 20, 90, 40, out float f); // the larger relative change (y) wins
        Assert.Equal(0.5f, f, 3);
        Assert.Equal((Start.Right, Start.Bottom), (r.Right, r.Bottom));
        Assert.Equal((157, 90), (r.Width, r.Height));
    }

    [Fact]
    public void Top_right_and_bottom_left_anchor_the_opposite_corner()
    {
        var tr = OverlayEditGeometry.ProportionalResize(Start, OverlayHit.TopRight, 314, 0, 40, out _);
        Assert.Equal((Start.X, Start.Bottom), (tr.X, tr.Bottom));
        Assert.Equal((628, 360), (tr.Width, tr.Height));
        var bl = OverlayEditGeometry.ProportionalResize(Start, OverlayHit.BottomLeft, 0, 180, 40, out _);
        Assert.Equal((Start.Right, Start.Y), (bl.Right, bl.Y));
        Assert.Equal((628, 360), (bl.Width, bl.Height));
    }

    [Fact]
    public void Never_shrinks_below_the_minimum_side()
    {
        var r = OverlayEditGeometry.ProportionalResize(Start, OverlayHit.BottomRight, -1000, -1000, 40, out float f);
        Assert.Equal(40, Math.Min(r.Width, r.Height));
        Assert.True(f > 0);
    }
}
