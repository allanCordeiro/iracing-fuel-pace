namespace FuelPace.App.Placement;

/// <summary>Rectangle in physical (device) pixels, virtual-desktop coordinates (may be negative).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
