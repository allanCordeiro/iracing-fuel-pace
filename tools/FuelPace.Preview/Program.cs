using System.Text;
using FuelPace.App.Overlay;

namespace FuelPace.Preview;

/// <summary>Writes the overlay review previews (SVG) from fixed states that reproduce the approved mocks.</summary>
public static class Program
{
    const int Padding = 40;

    public static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine("docs", "overlay-preview");
        Directory.CreateDirectory(dir);
        foreach (var p in OverlayPreviews.All)
        {
            var list = new DrawList();
            var s = p.State;
            OverlayLayout.Build(in s, OverlayPreviews.Car, OverlayPreviews.Track, p.Options, new FixedMetricsMeasurer(), list);
            File.WriteAllText(Path.Combine(dir, p.Name + ".svg"), SvgRenderer.Render(list, Padding, true), new UTF8Encoding(false));
        }
        Console.WriteLine($"{OverlayPreviews.All.Count} previews written to {dir}");
        return 0;
    }
}
