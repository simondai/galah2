using System.Runtime.InteropServices;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>Monitor work area in device pixels.</summary>
public readonly struct WorkArea
{
    public WorkArea(int left, int top, int right, int bottom, bool isPrimary)
    {
        Left = left; Top = top; Right = right; Bottom = bottom; IsPrimary = isPrimary;
    }

    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }
    public double Width => Right - Left;
    public double Height => Bottom - Top;
    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    /// <summary>True for the monitor carrying the taskbar/default desktop.</summary>
    public bool IsPrimary { get; }
}

/// <summary>
/// Multi-monitor geometry helpers. All overlap maths happens in device pixels; the caller passes the
/// DPI scale that was used to store the DIP values (spec §5.1-5: overlap below 30 percent on every
/// screen means the whole window is pulled into the nearest work area, TL-D7).
/// </summary>
public static class MonitorGeometry
{
    private const int MonitorinfoPrimary = 1;
    private const double MinimumOverlap = 0.30;

    /// <summary>Default panel size in DIPs when no usable geometry was saved.</summary>
    public const double DefaultWidth = 720;
    public const double DefaultHeight = 460;

    private static readonly Win32.MonitorEnumProc CollectMonitor = OnMonitor;
    private static List<WorkArea>? _collecting;

    /// <summary>All monitor work areas (device pixels). Never empty on a desktop session.</summary>
    public static IReadOnlyList<WorkArea> EnumerateWorkAreas()
    {
        var areas = new List<WorkArea>();
        _collecting = areas;
        try
        {
            Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectMonitor, IntPtr.Zero);
        }
        finally
        {
            _collecting = null;
        }
        if (areas.Count == 0)
        {
            // Last-resort fallback for headless / session-0 contexts: one virtual screen at the origin.
            areas.Add(new WorkArea(0, 0, 1920, 1040, true));
        }
        return areas;
    }

    // EnumDisplayMonitors needs a real method: a lambda cannot take the ref RECT parameter.
    private static bool OnMonitor(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData)
    {
        var target = _collecting;
        if (target is null) return false;
        var info = new MONITORINFOW { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOW>() };
        if (!Win32.GetMonitorInfoW(hMonitor, ref info)) return true;
        var w = info.rcWork;
        target.Add(new WorkArea(w.Left, w.Top, w.Right, w.Bottom, (info.dwFlags & MonitorinfoPrimary) != 0));
        return true;
    }

    /// <summary>System DPI scale (96 base), used before a window handle exists.</summary>
    public static double SystemScale()
    {
        var dpi = Win32.GetDpiForSystem();
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Clamp a saved rectangle (DIPs) so it is reachable: keep it when it overlaps some work area by
    /// at least 30%, otherwise move it (size preserved) into the nearest work area.
    /// </summary>
    /// <param name="x">Saved left in DIPs; null/NaN when nothing was saved.</param>
    /// <param name="y">Saved top in DIPs.</param>
    /// <param name="width">Saved width in DIPs.</param>
    /// <param name="height">Saved height in DIPs.</param>
    /// <param name="scale">DIP→pixel scale used when the values were stored.</param>
    /// <returns>A reachable rectangle in DIPs.</returns>
    public static (double X, double Y, double Width, double Height) ClampToScreens(
        double? x, double? y, double width, double height, double scale)
    {
        scale = scale <= 0 ? 1.0 : scale;
        var areas = EnumerateWorkAreas();
        var primary = areas.FirstOrDefault(a => a.IsPrimary);
        if (primary.Width <= 0) primary = areas[0];

        var w = Math.Max(240, width);
        var h = Math.Max(160, height);

        if (x is not { } sx || y is not { } sy || !double.IsFinite(sx) || !double.IsFinite(sy))
        {
            // No saved position: centre on the screen that currently holds the pointer, so the panel
            // appears where the user is looking; fall back to the primary work area.
            var target = WorkAreaAtCursor() ?? primary;
            if (target.Width <= 0) target = primary;

            // On a small screen the default size may not fit at all — shrink it instead of letting
            // the panel overflow (a 640x432 session is a real case, not a theoretical one).
            var margin = 16 * scale;
            var fitW = Math.Max(240 * scale, target.Width - margin);
            var fitH = Math.Max(160 * scale, target.Height - margin);
            var dw = Math.Min(w * scale, fitW) / scale;
            var dh = Math.Min(h * scale, fitH) / scale;

            var cx = target.Left + (target.Width - dw * scale) / 2;
            var cy = target.Top + (target.Height - dh * scale) / 2;
            return (Math.Max(target.Left, cx) / scale, Math.Max(target.Top, cy) / scale, dw, dh);
        }

        var left = sx * scale;
        var top = sy * scale;
        var areaPx = w * scale * (h * scale);

        var best = 0.0;
        foreach (var area in areas)
        {
            var ratio = OverlapRatio(left, top, w * scale, h * scale, area);
            if (ratio > best) best = ratio;
        }
        if (areaPx <= 0 || best >= MinimumOverlap) return (sx, sy, w, h);

        var nearest = NearestTo(left, top, w * scale, h * scale, areas);
        var winW = w * scale;
        var winH = h * scale;
        var newLeft = nearest.Left;
        var newTop = nearest.Top;
        if (winW <= nearest.Width) newLeft = Math.Min(Math.Max(left, nearest.Left), nearest.Right - winW);
        if (winH <= nearest.Height) newTop = Math.Min(Math.Max(top, nearest.Top), nearest.Bottom - winH);
        return (newLeft / scale, newTop / scale, w, h);
    }

    /// <summary>
    /// Work area of the monitor that currently holds the mouse pointer, null when the pointer
    /// cannot be located. Used as the default spot for a panel that has never been positioned.
    /// </summary>
    /// <returns>The work area under the cursor, or null.</returns>
    public static WorkArea? WorkAreaAtCursor()
    {
        if (!Win32.GetCursorPos(out var cursor)) return null;
        var monitor = Win32.MonitorFromPoint(cursor, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (monitor == IntPtr.Zero) return null;
        var info = new MONITORINFOW { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOW>() };
        if (!Win32.GetMonitorInfoW(monitor, ref info)) return null;
        return new WorkArea(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom,
            (info.dwFlags & MonitorinfoPrimary) != 0);
    }

    /// <summary>Intersection of a pixel rectangle with a work area, as a fraction of the rectangle.</summary>
    public static double OverlapRatio(double left, double top, double width, double height, WorkArea area)
    {
        var overlapW = Math.Min(left + width, area.Right) - Math.Max(left, area.Left);
        var overlapH = Math.Min(top + height, area.Bottom) - Math.Max(top, area.Top);
        if (overlapW <= 0 || overlapH <= 0) return 0;
        var rectArea = width * height;
        return rectArea <= 0 ? 0 : (overlapW * overlapH) / rectArea;
    }

    private static WorkArea NearestTo(double left, double top, double width, double height, IReadOnlyList<WorkArea> areas)
    {
        var cx = left + width / 2;
        var cy = top + height / 2;
        var best = areas[0];
        var bestDistance = double.MaxValue;
        foreach (var area in areas)
        {
            var dx = area.CenterX - cx;
            var dy = area.CenterY - cy;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = area;
            }
        }
        return best;
    }
}
