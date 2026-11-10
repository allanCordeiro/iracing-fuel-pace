using FuelPace.App.Placement;

namespace FuelPace.App.Overlay;

/// <summary>What the mouse grabs in edit mode: a corner handle (resize) or anything else (move).</summary>
public enum OverlayHit : byte { Caption, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Edit-mode mouse geometry for the overlay window (Windows answers <c>WM_NCHITTEST</c> and drives the corner drag
/// with these). Allocation-free.</summary>
public static class OverlayEditGeometry
{
    /// <summary>
    /// <paramref name="x"/>, <paramref name="y"/> in the list's pixels (the window's). The handles are the list's
    /// <see cref="CommandKind.FillRect"/> squares (only the edit chrome draws them); each grabs half its size around it too.
    /// </summary>
    public static OverlayHit HitTest(DrawList list, float x, float y)
    {
        var cmds = list.Commands;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        int n = 0;
        foreach (ref readonly var c in cmds)
        {
            if (c.Kind != CommandKind.FillRect) continue;
            float cx = c.Rect.X + c.Rect.W / 2, cy = c.Rect.Y + c.Rect.H / 2;
            minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
            minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
            n++;
        }
        if (n == 0) return OverlayHit.Caption;
        float midX = (minX + maxX) / 2, midY = (minY + maxY) / 2;
        foreach (ref readonly var c in cmds)
        {
            if (c.Kind != CommandKind.FillRect) continue;
            var r = c.Rect;
            float mx = r.W / 2, my = r.H / 2;
            if (x < r.X - mx || x > r.X + r.W + mx || y < r.Y - my || y > r.Y + r.H + my) continue;
            bool left = r.X + r.W / 2 < midX, top = r.Y + r.H / 2 < midY;
            return top ? (left ? OverlayHit.TopLeft : OverlayHit.TopRight) : (left ? OverlayHit.BottomLeft : OverlayHit.BottomRight);
        }
        return OverlayHit.Caption;
    }

    /// <summary>
    /// The window rect while a corner is dragged by (<paramref name="dx"/>, <paramref name="dy"/>) from <paramref name="start"/>:
    /// the opposite corner stays, the ratio is kept, the axis with the larger relative change wins, and neither side gets
    /// shorter than <paramref name="minSide"/>. <paramref name="scale"/> is new size / start size.
    /// </summary>
    public static PixelRect ProportionalResize(PixelRect start, OverlayHit corner, int dx, int dy, int minSide, out float scale)
    {
        if (corner == OverlayHit.Caption || start.Width <= 0 || start.Height <= 0)
        {
            scale = 1;
            return start;
        }
        bool left = corner is OverlayHit.TopLeft or OverlayHit.BottomLeft;
        bool top = corner is OverlayHit.TopLeft or OverlayHit.TopRight;
        float fx = (start.Width + (left ? -dx : dx)) / (float)start.Width;
        float fy = (start.Height + (top ? -dy : dy)) / (float)start.Height;
        float f = Math.Abs(fx - 1) >= Math.Abs(fy - 1) ? fx : fy;
        f = Math.Max(f, minSide / (float)Math.Min(start.Width, start.Height));
        int w = (int)MathF.Round(start.Width * f, MidpointRounding.AwayFromZero);
        int h = (int)MathF.Round(start.Height * f, MidpointRounding.AwayFromZero);
        scale = f;
        return new PixelRect(left ? start.Right - w : start.X, top ? start.Bottom - h : start.Y, w, h);
    }
}
