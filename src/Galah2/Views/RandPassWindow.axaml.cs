using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Galah2.Services;
using Galah2.Services.Interop;
using Galah2.ViewModels;

namespace Galah2.Views;

public partial class RandPassWindow : Window
{
    // ── edge resize (frameless window, spec 2026-11-24-randpass §7) ──
    //
    // SystemDecorations=None removes the OS resize borders, so the outer 6 DIP of the window
    // are hit-tested as resize zones. The pattern is a deliberate COPY of PanelWindow's
    // verified implementation, not a shared one: the panel's handlers carry tile/chip drag
    // semantics a dialog does not have, and this window must not reach into PanelWindow's
    // state (and the panel's code must not grow a dialog dependency either).
    //
    // Avalonia 11 has no diagonal resize cursors, so corners share one; the 6 px corner
    // zones are too small for the difference to matter in practice.
    private static readonly Cursor CornerCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);

    public const double ResizeBorder = 6;
    public const int EdgeN = 1, EdgeS = 2, EdgeE = 4, EdgeW = 8;

    // Arming: the origin fields below are persistent, and a release can land on another HWND
    // (a flyout/dialog popup) that this window never sees. Without explicit arming the next
    // button-down movement would re-fire the resize from the stale origin - the same incident
    // class that sent the panel flying off the desktop in 2026-11-23.
    private readonly DragArmState _arm = new();

    private int _resizeEdges;
    private Point _resizeOriginScreen;
    private PixelPoint _resizeWindowOrigin;
    private Size _resizeOriginalSize;

    public RandPassWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Tunnel route: an edge press is ours before the title-bar drag (bubble) or any control
        // can see it - the same registration point PanelWindow uses.
        AddHandler(PointerPressedEvent, Window_PointerPressedTunnel, RoutingStrategies.Tunnel);

        // A flyout or dialog taking activation ends any in-flight press, even though the
        // release event will arrive on the popup and never here.
        Deactivated += (_, _) => _arm.Deactivated();

        // Placement self-heal (spec 2026-11-24-randpass §9): the platform window exists now, so
        // re-asserting the intended geometry is a live SetWindow that cannot be dropped.
        if (_intendedSize is { } size &&
            (Math.Abs(Width - size.Width) > 0.5 || Math.Abs(Height - size.Height) > 0.5))
        {
            Width = size.Width;
            Height = size.Height;
        }
        if (_intendedPosition is { } pos && Position != pos)
            Position = pos;
    }

    // Borderless chrome (the system title bar is gone): forward to the shared handlers.
    private void TitleBar_Drag(object? sender, PointerPressedEventArgs e) => DialogChrome.Drag(this, e);
    private void TitleBar_Close(object? sender, RoutedEventArgs e) => DialogChrome.Close(this, e);

    private void Window_PointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var edges = EdgeAt(e.GetPosition(this));
        if (edges == 0) return;

        // Screen pixels, not GetPosition(null): while the window resizes the managed reference
        // moves with it and the delta would feed on itself (PanelWindow's documented rationale).
        if (!Win32.GetCursorPos(out var pt)) return;
        _resizeEdges = edges;
        _resizeOriginScreen = new Point(pt.X, pt.Y);
        _resizeWindowOrigin = Position;
        _resizeOriginalSize = new Size(Bounds.Width, Bounds.Height);
        _arm.ArmResize();
        e.Handled = true;   // an edge press resizes; the chrome drag and the content must not act
    }

    private int EdgeAt(Point p) => EdgesFor(new Size(Bounds.Width, Bounds.Height), p);

    /// <summary>Pure edge maths so the probes can assert every zone without a window.</summary>
    public static int EdgesFor(Size size, Point p)
    {
        var b = ResizeBorder;
        int edges = 0;
        if (p.X <= b) edges |= EdgeW; else if (p.X >= size.Width - b) edges |= EdgeE;
        if (p.Y <= b) edges |= EdgeN; else if (p.Y >= size.Height - b) edges |= EdgeS;
        return edges;
    }

    private void UpdateResizeCursor(Point p)
    {
        if (_resizeEdges != 0) return;
        Cursor = EdgeAt(p) switch
        {
            var e when (e & (EdgeN | EdgeS)) != 0 && (e & (EdgeE | EdgeW)) != 0 => CornerCursor,
            var e when (e & (EdgeN | EdgeS)) != 0 => NorthSouthCursor,
            var e when (e & (EdgeE | EdgeW)) != 0 => WestEastCursor,
            _ => null,
        };
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // The Win32 check is the truth. If the button was released outside the window, Avalonia's
        // pressed flag can go stale and the resize would stick to the cursor.
        var pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                      && Win32.IsKeyDown(Win32.VkLButton);

        if (!pressed)
        {
            _resizeEdges = 0;
            _arm.ButtonUpMoved();
            UpdateResizeCursor(e.GetPosition(this));
            return;
        }

        if (!Win32.GetCursorPos(out var pt)) return;

        // Arming, not button state, decides what this movement does. A button-down movement with
        // no arming press is a ghost (the flyout-click case) and must never resize the window.
        if (!_arm.ResizeArmed || _resizeEdges == 0) return;
        ResizeTo(pt.X, pt.Y);
    }

    private void ResizeTo(double cursorX, double cursorY)
    {
        // Position and work areas are device pixels; Width/Height are DIPs. Keep every
        // computation in device pixels and convert back only when assigning Width/Height.
        var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var dx = cursorX - _resizeOriginScreen.X;
        var dy = cursorY - _resizeOriginScreen.Y;

        var x = (double)_resizeWindowOrigin.X;
        var y = (double)_resizeWindowOrigin.Y;
        var w = _resizeOriginalSize.Width * scale;
        var h = _resizeOriginalSize.Height * scale;

        if ((_resizeEdges & EdgeE) != 0) w += dx;
        if ((_resizeEdges & EdgeS) != 0) h += dy;
        if ((_resizeEdges & EdgeW) != 0) { x += dx; w -= dx; }
        if ((_resizeEdges & EdgeN) != 0) { y += dy; h -= dy; }

        var minW = MinWidth * scale;
        var minH = MinHeight * scale;
        if (w < minW) w = minW;
        if (h < minH) h = minH;

        // Keep the opposite edge where the user left it when the min size or the screen stops a
        // west/north resize, instead of pinning the window to the work-area border.
        var area = BestArea(x, y, w, h);
        var rightEdge = x + w;
        var bottomEdge = y + h;
        if (w > area.Width) w = area.Width;
        if (h > area.Height) h = area.Height;
        if ((_resizeEdges & EdgeW) != 0) x = rightEdge - w;
        if ((_resizeEdges & EdgeN) != 0) y = bottomEdge - h;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));

        Position = new PixelPoint((int)x, (int)y);
        Width = w / scale;
        Height = h / scale;
    }

    private static WorkArea BestArea(double x, double y, double w, double h)
    {
        var areas = MonitorGeometry.EnumerateWorkAreas();
        if (areas.Count == 0)
            return new WorkArea(0, 0, 1920, 1040, true);

        var best = areas[0];
        var bestRatio = -1.0;
        foreach (var area in areas)
        {
            var ratio = MonitorGeometry.OverlapRatio(x, y, w, h, area);
            if (ratio > bestRatio) { bestRatio = ratio; best = area; }
        }
        return best;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _resizeEdges = 0;
        _arm.Release();
        UpdateResizeCursor(e.GetPosition(this));
    }

    /// <summary>
    /// Double-click a result row = copy that password to the clipboard (user-requested beyond
    /// the screenshot: a generator without copy is unusable).
    /// </summary>
    private async void ResultList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: string password } ||
            DataContext is not RandPassViewModel vm) return;

        // Avalonia 11.3: IClipboard lives in Avalonia.Input.Platform, exposed as the TopLevel
        // .Clipboard property (there is no GetServiceProvider on TopLevel in this version).
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await clipboard.SetTextAsync(password);
            vm.MarkCopied();
        }
        catch (Exception)
        {
            // Clipboard can be owned/blocked; a failed copy must not crash the dialog.
        }
    }

    /// <summary>Persist the parameter section once, on close (single-commit rule).</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is RandPassViewModel vm) vm.Commit();
        base.OnClosed(e);
    }

    // ── placement self-heal (spec 2026-11-24-randpass §9) ──
    //
    // AppRuntime places the window (Position/Width/Height) BEFORE Show. A position set before
    // the platform window exists can be dropped by some platform states: on one RDP session
    // the centred fallback never stuck, the window opened in the top-left corner, and the
    // geometry rememberer then "restored" (0,0) on every following open. The window is the
    // only party that knows the platform window exists, so it re-asserts its intended
    // geometry once on Opened — a set at that moment is a plain live SetWindow that cannot
    // be lost. The probe asserts the real post-Show position equals the intent.
    private Avalonia.PixelPoint? _intendedPosition;
    private Avalonia.Size? _intendedSize;

    /// <summary>The rectangle the caller placed this window at, for the Opened-time re-assert.</summary>
    public (Avalonia.PixelPoint? Position, Avalonia.Size? Size) IntendedGeometry
    {
        get => (_intendedPosition, _intendedSize);
        set
        {
            _intendedPosition = value.Position;
            _intendedSize = value.Size;
        }
    }

}
