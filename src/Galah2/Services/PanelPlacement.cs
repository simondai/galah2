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
    /// <param name="width">Window width, in device pixels.</param>
    /// <param name="height">Window height, in device pixels.</param>
    /// <param name="scale">Render scaling used to convert <see cref="EdgeMargin"/> from DIPs.</param>
    /// <returns>Top-left position, always inside the work area.</returns>
    public static (double X, double Y) Resolve(
        string mode, (double X, double Y)? remembered, (double X, double Y)? cursor,
        WorkArea area, double width, double height, double scale = 1.0)
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

        // 仅在“跟随鼠标”模式下保留边缘留白；“记住上次位置”模式下尊重用户手动摆放的贴边位置，只做工作区越界钳位。
        if (scale <= 0) scale = 1.0;
        var margin = mode == PanelPositions.Mouse ? EdgeMargin * scale : 0.0;
        var minX = Math.Min(area.Left + margin, Math.Max(area.Left, area.Right - width));
        var minY = Math.Min(area.Top + margin, Math.Max(area.Top, area.Bottom - height));
        var maxX = Math.Max(area.Left, area.Right - width - margin);
        var maxY = Math.Max(area.Top, area.Bottom - height - margin);
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
    /// True when the area cannot host anything (zero/negative width or height).
    /// </summary>
    public static bool Degenerate(WorkArea area) => area.Width <= 0 || area.Height <= 0;

    /// <summary>
    /// Work area that should host the summon: the cursor's monitor, else the primary, else the
    /// first area — and never a degenerate (zero-size) one. The single fallback policy for
    /// placement; callers do not invent their own.
    ///
    /// The guard is defensive: Resolve(Centre) against a (0,0,0,0) area clamps to exactly (0,0)
    /// (probe-locked), so a transient monitor glitch during an RDP session could silently park
    /// a window in the top-left corner. Added during the top-left investigation
    /// (spec 2026-11-24-randpass §9); the CONFIRMED root cause of that bug was the close-time
    /// Position reset, not a degenerate area (§9.1) — the guard stays as cheap insurance.
    /// </summary>
    /// <param name="cursor">Cursor point in device pixels, or null.</param>
    public static WorkArea AreaFor((double X, double Y)? cursor)
    {
        var at = cursor is not null ? MonitorGeometry.WorkAreaAtCursor() : null;
        if (at is { } a && !Degenerate(a)) return a;
        var areas = MonitorGeometry.EnumerateWorkAreas();
        foreach (var area in areas)
            if (area.IsPrimary && !Degenerate(area)) return area;
        foreach (var area in areas)
            if (!Degenerate(area)) return area;
        // Every area is degenerate (should not happen): hand back whatever the cursor saw,
        // else the raw first — the caller's clamp keeps the window somewhere finite.
        return at ?? areas[0];
    }
}
