using Galah2.Models;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>
/// Where the panel appears when it is summoned (Sprint 3 / ROADMAP E1). Pure maths, no windows:
/// the caller supplies the cursor point (null when the session refuses to report one) and the
/// work area to place inside.
/// </summary>
public static class PanelPlacement
{
    /// <summary>Distance kept between the window and the work-area edge, in DIPs.</summary>
    public const double EdgeMargin = 24;

    /// <summary>
    /// Resolve the top-left of a window of the given size.
    /// </summary>
    /// <param name="mode">One of <see cref="PanelPositions.All"/>.</param>
    /// <param name="remembered">Stored (x, y) used by the remember mode; null when absent.</param>
    /// <param name="cursor">Cursor point in the same coordinate space, or null.</param>
    /// <param name="area">Work area to place and clamp inside.</param>
    /// <param name="width">Window width.</param>
    /// <param name="height">Window height.</param>
    /// <returns>Top-left position, always inside the work area.</returns>
    public static (double X, double Y) Resolve(
        string mode, (double X, double Y)? remembered, (double X, double Y)? cursor,
        WorkArea area, double width, double height)
    {
        double x, y;

        switch (mode)
        {
            case PanelPositions.Mouse when cursor is not null:
                // centred on the pointer, then pushed back inside the work area
                x = cursor.Value.X - width / 2;
                y = cursor.Value.Y - height / 2;
                break;

            case PanelPositions.Center:
                x = area.CenterX - width / 2;
                y = area.CenterY - height / 2;
                break;

            case PanelPositions.Remember:
            default:
                if (remembered is not null)
                {
                    x = remembered.Value.X;
                    y = remembered.Value.Y;
                }
                else
                {
                    // no memory yet: behave like center so the very first summon is predictable
                    x = area.CenterX - width / 2;
                    y = area.CenterY - height / 2;
                }
                break;
        }

        // Never let the panel hang off the usable desktop. A window larger than the work area
        // (tiny RDP session, huge saved size) would make the clamp bounds invert and throw,
        // so the lower bound is what is left when the fit is impossible.
        var minX = Math.Min(area.Left + EdgeMargin, Math.Max(area.Left, area.Right - width));
        var minY = Math.Min(area.Top + EdgeMargin, Math.Max(area.Top, area.Bottom - height));
        var maxX = Math.Max(area.Left, area.Right - width - EdgeMargin);
        var maxY = Math.Max(area.Top, area.Bottom - height - EdgeMargin);
        x = Math.Clamp(x, minX, Math.Max(minX, maxX));
        y = Math.Clamp(y, minY, Math.Max(minY, maxY));
        return (x, y);
    }

    /// <summary>Current pointer position in device pixels, or null when unavailable.</summary>
    public static (double X, double Y)? CursorPoint()
    {
        if (!Win32.GetCursorPos(out var pt)) return null;
        return (pt.X, pt.Y);
    }

    /// <summary>
    /// Work area that should host the summon: the cursor's monitor, else the primary, else the
    /// first area. The single fallback policy for placement - callers do not invent their own.
    /// </summary>
    /// <param name="cursor">Cursor point in device pixels, or null.</param>
    public static WorkArea AreaFor((double X, double Y)? cursor)
    {
        if (cursor is not null && MonitorGeometry.WorkAreaAtCursor() is { } at)
            return at;
        var areas = MonitorGeometry.EnumerateWorkAreas();
        foreach (var area in areas)
            if (area.IsPrimary) return area;
        return areas[0];
    }
}
