using FuelPace.Core.Display;

namespace FuelPace.App.Overlay;

/// <summary>One v3 review preview: a fixed view and options.</summary>
public sealed record GlancePreview(string Name, GlanceView View, OverlayViewOptions Options);

public static class GlancePreviews
{
    public static IReadOnlyList<GlancePreview> All { get; } = [];

    public static string Svg(GlancePreview p) => throw new NotImplementedException();

    public static int WriteAll(string dir) => throw new NotImplementedException();
}
