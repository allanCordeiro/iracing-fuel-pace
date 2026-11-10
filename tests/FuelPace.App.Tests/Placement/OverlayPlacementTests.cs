using FuelPace.App.Overlay;
using FuelPace.App.Placement;
using FuelPace.App.Settings;
using FuelPace.Core.Strategy;

namespace FuelPace.App.Tests.Placement;

public sealed class OverlayPlacementTests
{
    static readonly MonitorDesc Primary = new(@"\\.\DISPLAY1", new(0, 0, 2560, 1440), new(0, 0, 2560, 1400), 1.0, true);
    static readonly MonitorDesc Second = new(@"\\.\DISPLAY2", new(2560, 0, 1920, 1080), new(2560, 0, 1920, 1040), 1.0, false);
    static readonly MonitorDesc Left = new(@"\\.\DISPLAY3", new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040), 1.0, false);
    static readonly SizeF Logical = new(300, 100);

    static OverlaySettings Ov(string? dev, int x, int y, double scale = 1) =>
        new() { MonitorDeviceName = dev, OffsetX = x, OffsetY = y, Scale = scale };

    [Fact]
    public void Places_on_named_monitor_with_offsets()
    {
        var r = OverlayPlacement.Resolve(Ov(Second.DeviceName, 40, 50), [Primary, Second], Logical, out var m);
        Assert.Same(Second, m);
        Assert.Equal(new PixelRect(2600, 50, 300, 100), r);
    }

    [Fact]
    public void Missing_monitor_falls_back_to_primary_and_stays_visible()
    {
        var r = OverlayPlacement.Resolve(Ov(@"\\.\DISPLAY9", 5000, 5000), [Second, Primary], Logical, out var m);
        Assert.Same(Primary, m);
        Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.Width <= 2560 && r.Y + r.Height <= 1400);
        var r2 = OverlayPlacement.Resolve(Ov(null, 40, 40), [Second, Primary], Logical, out m);
        Assert.Same(Primary, m);
        Assert.Equal(new PixelRect(40, 40, 300, 100), r2);
    }

    [Fact]
    public void Clamps_into_work_area_after_resolution_drop()
    {
        var small = Second with { WorkArea = new(2560, 0, 1920, 1040) };
        var r = OverlayPlacement.Resolve(Ov(small.DeviceName, 3000, 2000), [Primary, small], Logical, out _);
        Assert.Equal(small.WorkArea.X + small.WorkArea.Width, r.X + r.Width);
        Assert.Equal(small.WorkArea.Y + small.WorkArea.Height, r.Y + r.Height);
        var neg = OverlayPlacement.Resolve(Ov(small.DeviceName, -500, -500), [Primary, small], Logical, out _);
        Assert.Equal(new PixelRect(2560, 0, 300, 100), neg);
    }

    [Fact]
    public void Larger_than_work_area_aligns_top_left()
    {
        var tiny = new MonitorDesc("T", new(100, 50, 200, 80), new(100, 50, 200, 80), 1.0, true);
        var r = OverlayPlacement.Resolve(Ov("T", 30, 30), [tiny], Logical, out _);
        Assert.Equal(100, r.X);
        Assert.Equal(50, r.Y);
        Assert.Equal(300, r.Width);
    }

    [Fact]
    public void Dpi_scale_multiplies_size()
    {
        var hi = Primary with { DpiScale = 1.5 };
        var r = OverlayPlacement.Resolve(Ov(null, 0, 0, 2), [hi], Logical, out _);
        Assert.Equal(900, r.Width);
        Assert.Equal(300, r.Height);
    }

    [Fact]
    public void LogicalSize_contract_is_the_Scale_1_panel_size_not_the_already_scaled_one()
    {
        // Callers must build the layout with Scale = 1 (non-edit) and let Resolve apply Scale x Dpi. Pin that the layout
        // at Scale 2 is exactly twice the Scale 1 size (so passing it would double-scale).
        var s = new OverlayState { State = SignalState.Blue, FuelL = 41.8, AutonomyLaps = 17.3, AutonomyMin = 23 };
        SizeF Build(double scale)
        {
            var d = new DrawList();
            OverlayLayout.Build(s, "LMP2", "Zolder",
                new OverlayViewOptions(scale, 0.6, 1, false, true, false, false, "Ctrl+Shift+F", 0, 0), new FixedMetricsMeasurer(), d);
            return d.Size;
        }
        var one = Build(1);
        var two = Build(2);
        Assert.Equal(300f, one.W);
        Assert.Equal(one.W * 2, two.W, 1);
        Assert.Equal(one.H * 2, two.H, 1);

        var hi = Primary with { DpiScale = 1.25 };
        var r = OverlayPlacement.Resolve(Ov(null, 0, 0, 2), [hi], one, out _);
        Assert.Equal((int)Math.Round(300 * 2 * 1.25), r.Width);
    }

    [Fact]
    public void Move_to_second_monitor_updates_device_and_offsets()
    {
        var o = Ov(Primary.DeviceName, 40, 40, 1.5);
        var n = OverlayPlacement.FromUserMove(o, new PixelRect(2560 + 100, 200, 300, 100), [Primary, Second]);
        Assert.Equal(Second.DeviceName, n.MonitorDeviceName);
        Assert.Equal(100, n.OffsetX);
        Assert.Equal(200, n.OffsetY);
        Assert.Equal(1.5, n.Scale);
        Assert.Equal(40, o.OffsetX); // original untouched
    }

    [Fact]
    public void Resize_maps_to_scale_and_clamps()
    {
        Assert.Equal(1.0, OverlayPlacement.ScaleFromUserResize(new(0, 0, 300, 100), Logical, 1.0), 6);
        Assert.Equal(1.0, OverlayPlacement.ScaleFromUserResize(new(0, 0, 450, 150), Logical, 1.5), 6);
        Assert.Equal(0.5, OverlayPlacement.ScaleFromUserResize(new(0, 0, 10, 10), Logical, 1.0));
        Assert.Equal(2.5, OverlayPlacement.ScaleFromUserResize(new(0, 0, 5000, 10), Logical, 1.0));
    }

    [Fact]
    public void Negative_coordinates_left_monitor()
    {
        var r = OverlayPlacement.Resolve(Ov(Left.DeviceName, 40, 40), [Primary, Left], Logical, out var m);
        Assert.Same(Left, m);
        Assert.Equal(new PixelRect(-1880, 40, 300, 100), r);
        var n = OverlayPlacement.FromUserMove(Ov(null, 0, 0), new PixelRect(-1000, 100, 300, 100), [Primary, Left]);
        Assert.Equal(Left.DeviceName, n.MonitorDeviceName);
        Assert.Equal(920, n.OffsetX);
        Assert.Equal(100, n.OffsetY);
    }

    [Fact]
    public void Describe_for_ui()
    {
        Assert.Equal("1 · 2560 × 1440", OverlayPlacement.DescribeForUi(Primary, 0));
        Assert.Equal("2 · 1920 × 1080", OverlayPlacement.DescribeForUi(Second, 1));
    }
}
