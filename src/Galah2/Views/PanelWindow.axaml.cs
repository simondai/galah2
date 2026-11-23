using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Galah2.Services;
using Galah2.Services.Interop;
using Galah2.ViewModels;

namespace Galah2.Views;

public partial class PanelWindow : Window
{
    private Point _dragOriginScreen;
    private PixelPoint _dragWindowOrigin;
    private bool _dragging;

    // Arming: the origin fields above are persistent, and a release can land on another HWND
    // (the ☰ MenuFlyout popup) that the panel never sees. Without explicit arming, the next
    // button-down movement would re-fire the drag from the stale origin and teleport the panel
    // (2026-11-23: panel flew off the desktop after clicking 设置 in the menu).
    private readonly DragArmState _arm = new();

    public PanelWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Keyboard navigation: Escape hides, arrows move the category, Ctrl+1..9 selects one.
        //
        // Registered on the TUNNEL route deliberately: Avalonia's KeyboardNavigationHandler
        // watches KeyDown on the window (bubble) and marks arrows and Tab handled for focus
        // movement, so a bubble-registered handler here would never see those keys - the same
        // 1.x PreviewKeyDown semantics, rebuilt. The other keys fall through untouched.
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, Window_PointerPressedTunnel, RoutingStrategies.Tunnel);

        // Wheel: switch category, but only when the tile list cannot scroll any further.
        // The handler is attached with handledEventsToo so it runs AFTER the presenter's own
        // scroll handling: while the list can still scroll, the event arrives already handled
        // and this does nothing; at an edge it arrives unhandled and the gesture becomes a tab
        // change - 1.x's PreviewMouseWheel semantics, rebuilt for Avalonia's tunnel order.
        // (FindControl, not the x:Name field: the named fields are not populated at ctor time
        // in this setup - the same reason ApplyMaterial looks up RootBorder this way.)
        if (this.FindControl<ScrollViewer>("TileScroll") is { } tileScroll)
            tileScroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Bubble,
                handledEventsToo: true);

        // Material follows the user's choice; the level is recorded because the compositor can
        // downgrade the request, and a panel that quietly fell back would look like a bug.
        ApplyMaterial();
        DiagnosticsLog.Line("material",
            "requested=" + RequestedMaterial + " actual=" + ActualTransparencyLevel);

        // Hide when focus leaves, if the user asked for it. Worth knowing: a frameless panel can
        // deactivate when its own context menu opens, so this is opt-in and never the default.
        Deactivated += (_, _) =>
        {
            // A flyout or dialog taking activation ends any in-flight press, even though the
            // release event will arrive on the popup and never here.
            _arm.Deactivated();
            if (DataContext is PanelViewModel { HideOnBlur: true }) Hide();
        };

        // Dropping files or a URL onto the panel adds them to the active category.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.HideCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Left:
                vm.MoveTabLeftCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.Right:
                vm.MoveTabRightCommand.Execute(null);
                e.Handled = true;
                return;

            // Ctrl+Tab cycles forward, Ctrl+Shift+Tab backward - 1.x navigation that a user
            // reaching for "next category" tries before anything else.
            case Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                var backward = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                vm.SwitchTabRelative(backward ? -1 : 1);
                e.Handled = true;
                return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.NumberKeysSwitchTab)
        {
            // Digits from both rows: 1.x mapped NumPad0-9 as well as D0-9, and a user whose hand
            // is already on the numpad should not have to move it.
            var digit = e.Key switch
            {
                >= Key.D0 and <= Key.D9 => e.Key - Key.D0,
                >= Key.NumPad0 and <= Key.NumPad9 => e.Key - Key.NumPad0,
                _ => -1
            };
            // TabNavigation is 0-based, SelectTabByIndex 1-based; 0 never maps (no silent jump).
            if (TabNavigation.DigitToIndex(digit) is >= 0 and var index
                && vm.SelectTabByIndex(index + 1))
            {
                e.Handled = true;
            }
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled) return;   // the presenter scrolled the list; the wheel is a scroll
        if (DataContext is not PanelViewModel vm) return;
        if (!vm.WheelSwitchTab) return;
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (sender is not ScrollViewer scroll) return;

        var direction = e.Delta.Y > 0 ? -1 : 1;
        var atTop = scroll.Offset.Y <= 0;
        var atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 0.5;
        var edge = direction > 0 ? atBottom : atTop;
        if (!edge) return;

        vm.SwitchTabRelative(direction);
        e.Handled = true;
    }

    /// <summary>The material asked for, resolved from the panel view model.</summary>
    private string RequestedMaterial =>
        (DataContext as PanelViewModel)?.PanelMaterial ?? Galah2.Models.PanelMaterials.None;

    /// <summary>
    /// Apply the material setting: the window stays Transparent either way (that is what keeps the
    /// rounded corners), and only the fill changes.
    ///
    /// Asking for Mica here was tried and reverted: the compositor reports Mica but renders it
    /// nearly opaque, which sealed the backdrop off and made the two settings look identical.
    /// 1.x never used Mica either - translucent there was a plain alpha fill over a transparent
    /// window, which is visible everywhere Windows runs.
    /// </summary>
    public void ApplyMaterial()
    {
        TransparencyLevelHint = new[] { Avalonia.Controls.WindowTransparencyLevel.Transparent };

        var translucent = RequestedMaterial == Galah2.Models.PanelMaterials.Translucent;
        if (this.FindControl<Border>("RootBorder") is { } border)
        {
            border.Background = this.FindResource(translucent
                    ? "PanelFillBrush"
                    : "PanelFillOpaqueBrush") as Avalonia.Media.IBrush;
        }
    }

    private ItemViewModel? _dragTile;
    private Point _dragTileOrigin;

    /// <summary>Remember which tile a press started on, in case it becomes a drag.</summary>
    private void Tile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button { Tag: ItemViewModel tile }) { _dragTile = null; return; }
        // The placeholder tile has nothing to reorder; its press belongs to the flyout.
        if (tile.IsAddTile) { _dragTile = null; return; }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragTile = null; return; }

        _dragTile = tile;
        _dragTileOrigin = e.GetPosition(this);
    }

    /// <summary>
    /// Start a drag once the pointer has moved far enough to mean it. Without the threshold every
    /// click would begin a drag and the tile could never be clicked.
    /// </summary>
    private async void Tile_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTile is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _dragTile = null; return; }

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragTileOrigin.X) + Math.Abs(now.Y - _dragTileOrigin.Y) < 8) return;

        var tile = _dragTile;
        _dragTile = null;   // one drag per press

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ItemIdFormat, tile.Id));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
    }

    private void TabChip_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(ItemIdFormat)
            ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>Move the dragged item into the category it was dropped on.</summary>
    private void TabChip_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is not Button { Tag: TabViewModel tab }) return;
        // TryGetValue returns the value or null - the DataTransfer idiom, not a bool + out.
        var itemId = e.DataTransfer.TryGetValue(ItemIdFormat);
        if (string.IsNullOrEmpty(itemId)) return;

        vm.MoveItemToTab(itemId, tab.Id);
        e.Handled = true;
    }

    /// <summary>
    /// The custom drag format for tile moves. Both ends of the drag share this one static
    /// instance. Identifier rules: ASCII letters, digits, dot and hyphen only - the slash of the
    /// old IDataObject name "galah/item-id" is rejected here.
    /// </summary>
    private static readonly DataFormat<string> ItemIdFormat =
        DataFormat.CreateStringApplicationFormat("galah.item-id");

    /// <summary>Single click. In double-click mode this only selects the tile.</summary>
    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is not Button { Tag: ItemViewModel tile }) return;

        vm.LaunchTile(tile, clickCount: 1);
    }

    /// <summary>Double click always launches, whichever mode is configured.</summary>
    private void Tile_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is not Button { Tag: ItemViewModel tile }) return;

        vm.LaunchTile(tile, clickCount: 2);
        e.Handled = true;   // stop the Click that follows from re-launching it
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Files by storage item, text by format probe - the DataTransfer replacements for
        // DataFormats.Files/Text + IDataObject.Contains.
        var hasPayload = e.DataTransfer.TryGetFiles() is not null
                         || e.DataTransfer.TryGetText() is not null;
        e.DragEffects = hasPayload ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is not null)
            {
                var paths = files.Select(f => f.Path.LocalPath)
                                 .Where(p => !string.IsNullOrEmpty(p))
                                 .ToList();
                if (paths.Count > 0) { vm.HandleDrop(paths); return; }
            }

            var text = e.DataTransfer.TryGetText();
            if (!string.IsNullOrWhiteSpace(text)) vm.HandlePasteText(text);
        }
        catch (Exception ex) { DiagnosticsLog.Line("drop", "failed: " + ex.Message); }
    }
    // ---- window drag (chrome) and edge resize ----
    //
    // SystemDecorations=None removes the system's resize borders, so the edges are hit-tested
    // here: the outer 6 DIP of the window belong to the resize zone, and a press there is
    // handled on the tunnel route so tiles and the chrome drag never see it.

    // Avalonia 11 has no diagonal resize cursors, so corners share one; the 6 px corner zones
    // are too small for the difference to matter in practice.
    private static readonly Cursor CornerCursor = new(StandardCursorType.SizeAll);

    public const double ResizeBorder = 6;
    public const int EdgeN = 1, EdgeS = 2, EdgeE = 4, EdgeW = 8;

    private int _resizeEdges;
    private Point _resizeOriginScreen;
    private PixelPoint _resizeWindowOrigin;
    private Size _resizeOriginalSize;

    private void Window_PointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edges = EdgeAt(e.GetPosition(this));
        if (edges == 0) return;

        if (!Win32.GetCursorPos(out var pt)) return;
        _resizeEdges = edges;
        _resizeOriginScreen = new Point(pt.X, pt.Y);
        _resizeWindowOrigin = Position;
        _resizeOriginalSize = new Size(Bounds.Width, Bounds.Height);
        _arm.ArmResize();
        e.Handled = true;   // a border press resizes; tiles and the chrome drag must not act
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
        if (_dragging || _resizeEdges != 0) return;
        Cursor = EdgeAt(p) switch
        {
            // Avalonia 11 has no diagonal resize cursors, so corners share one; the 6 px corner
            // zones are too small for the difference to matter in practice.
            var e when (e & (EdgeN | EdgeS)) != 0 && (e & (EdgeE | EdgeW)) != 0 => CornerCursor,
            var e when (e & (EdgeN | EdgeS)) != 0 => new Cursor(StandardCursorType.SizeNorthSouth),
            var e when (e & (EdgeE | EdgeW)) != 0 => new Cursor(StandardCursorType.SizeWestEast),
            _ => null,
        };
    }

    /// <summary>
    /// Drag the frameless panel by its chrome strip.
    ///
    /// Uses Win32 GetCursorPos for the pointer, not Avalonia GetPosition(null). The managed call
    /// resolves against whichever root Visual is current, so while the window moves the reference
    /// moves with it and the drag feeds on itself. Screen pixels have no such dependency: the
    /// target is always origin + (cursorNow - cursorAtPress).
    /// </summary>
    private void Chrome_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!Win32.GetCursorPos(out var pt)) return;

        _dragOriginScreen = new Point(pt.X, pt.Y);
        _dragWindowOrigin = Position;
        _dragging = false;
        _arm.ArmDrag();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // The Win32 check is the truth. If the button was released outside the window, Avalonia's
        // pressed flag can go stale and the panel would stick to the cursor - reported as the
        // panel "following the mouse" out of the screen.
        var pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                      && Win32.IsKeyDown(Win32.VkLButton);

        if (!pressed)
        {
            _dragging = false;
            _resizeEdges = 0;
            _arm.ButtonUpMoved();
            UpdateResizeCursor(e.GetPosition(this));
            return;
        }

        if (!Win32.GetCursorPos(out var pt)) return;

        // Arming, not button state, decides what this movement does. A button-down movement with
        // no arming press is a ghost (the flyout-click case) and must never move the window.
        if (_arm.ResizeArmed && _resizeEdges != 0) { ResizeTo(pt.X, pt.Y); return; }
        if (!_arm.DragArmed) return;

        var dx = pt.X - _dragOriginScreen.X;
        var dy = pt.Y - _dragOriginScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // ignore jitter

        _dragging = true;
        // Clamp every step and keep the whole window inside the nearest work area: a launcher
        // dragged off-screen is lost there, and the position would be remembered for the next
        // summon. (The 30% rule in MonitorGeometry.ClampToScreens is for restoring saved
        // geometry after a monitor change, not for a live drag.)
        ClampDragPosition(_dragWindowOrigin.X + dx, _dragWindowOrigin.Y + dy);
    }

    /// <summary>Move to a device-pixel target, kept fully inside the nearest work area.</summary>
    private void ClampDragPosition(double targetX, double targetY)
    {
        var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var w = Width * scale;
        var h = Height * scale;
        var areas = MonitorGeometry.EnumerateWorkAreas();
        var area = areas[0];
        var cx = targetX + w / 2;
        var cy = targetY + h / 2;
        var best = double.MaxValue;
        foreach (var a in areas)
        {
            var d = (a.CenterX - cx) * (a.CenterX - cx) + (a.CenterY - cy) * (a.CenterY - cy);
            if (d < best) { best = d; area = a; }
        }
        var x = Math.Clamp(targetX, area.Left, Math.Max(area.Left, area.Right - w));
        var y = Math.Clamp(targetY, area.Top, Math.Max(area.Top, area.Bottom - h));
        Position = new PixelPoint((int)x, (int)y);
    }

    private void ResizeTo(double cursorX, double cursorY)
    {
        // Cursor deltas are physical pixels; the window geometry is in DIPs.
        double dx = (cursorX - _resizeOriginScreen.X) / RenderScaling;
        double dy = (cursorY - _resizeOriginScreen.Y) / RenderScaling;

        var x = (double)_resizeWindowOrigin.X;
        var y = (double)_resizeWindowOrigin.Y;
        var w = _resizeOriginalSize.Width;
        var h = _resizeOriginalSize.Height;

        if ((_resizeEdges & EdgeE) != 0) w += dx;
        if ((_resizeEdges & EdgeS) != 0) h += dy;
        if ((_resizeEdges & EdgeW) != 0) { x += dx; w -= dx; }
        if ((_resizeEdges & EdgeN) != 0) { y += dy; h -= dy; }

        if (w < MinWidth) w = MinWidth;
        if (h < MinHeight) h = MinHeight;

        // Keep the whole window inside the work area it mostly sits on. When the min size or the
        // screen stops a west/north resize, the position is re-derived so the opposite edge stays
        // where the user left it.
        var area = BestArea(x, y, w, h);
        if (w > area.Width) { w = area.Width; }
        if (h > area.Height) { h = area.Height; }
        if ((_resizeEdges & EdgeW) != 0) x = area.Right - w;
        if ((_resizeEdges & EdgeN) != 0) y = area.Bottom - h;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - w));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - h));

        Position = new PixelPoint((int)x, (int)y);
        Width = w;
        Height = h;
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
        _dragging = false;
        _resizeEdges = 0;
        _arm.Release();
        UpdateResizeCursor(e.GetPosition(this));
    }
}
