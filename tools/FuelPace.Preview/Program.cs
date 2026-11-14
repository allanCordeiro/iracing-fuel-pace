using FuelPace.App.Overlay;

namespace FuelPace.Preview;

/// <summary>
/// Writes the v3 overlay review previews (SVG, PRD v3 V3-10) from fixed views that reproduce the mocks. Default folder
/// <c>docs/v3/preview</c>; <c>args[0]</c> picks another one.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine("docs", "v3", "preview");
        int n = GlancePreviews.WriteAll(dir);
        Console.WriteLine($"{n} previews written to {dir}");
        return 0;
    }
}
