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

    public PanelWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Keyboard navigation: Escape hides, arrows move the category, Ctrl+1..9 selects one.
        KeyDown += OnKeyDown;
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, Window_PointerPressedTunnel, RoutingStrategies.Tunnel);

        // Material follows the user's choice; the level is recorded because the compositor can
        // downgrade the request, and a panel that quietly fell back would look like a bug.
        ApplyMaterial();
        DiagnosticsLog.Line("material",
            "requested=" + RequestedMaterial + " actual=" + ActualTransparencyLevel);

        // Hide when focus leaves, if the user asked for it. Worth knowing: a frameless panel can
        // deactivate when its own context menu opens, so this is opt-in and never the default.
        Deactivated += (_, _) =>
        {
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

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key >= Key.D1 && e.Key <= Key.D9)
        {
            vm.SelectTabByIndex(e.Key - Key.D1 + 1);
            e.Handled = true;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (e.KeyModifiers != KeyModifiers.None) return;

        vm.SwitchTabRelative(e.Delta.Y > 0 ? -1 : 1);
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

        var data = new DataObject();
        data.Set("galah/item-id", tile.Id);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
    }

    private void TabChip_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains("galah/item-id") ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>Move the dragged item into the category it was dropped on.</summary>
    private void TabChip_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is not Button { Tag: TabViewModel tab }) return;
        if (e.Data.Get("galah/item-id") is not string itemId || string.IsNullOrEmpty(itemId)) return;

        vm.MoveItemToTab(itemId, tab.Id);
        e.Handled = true;
    }

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
        e.DragEffects = e.Data.Contains(DataFormats.Files) || e.Data.Contains(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;

        try
        {
            var files = e.Data.GetFiles();
            if (files is not null)
            {
                var paths = files.Select(f => f.Path.LocalPath)
                                 .Where(p => !string.IsNullOrEmpty(p))
                                 .ToList();
                if (paths.Count > 0) { vm.HandleDrop(paths); return; }
            }

            var text = e.Data.GetText();
            if (!string.IsNullOrWhiteSpace(text)) vm.HandlePasteText(text);
        }
        catch (Exception ex) { Console.Error.WriteLine("drop failed: " + ex.Message); }
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
            UpdateResizeCursor(e.GetPosition(this));
            return;
        }

        if (!Win32.GetCursorPos(out var pt)) return;

        if (_resizeEdges != 0) { ResizeTo(pt.X, pt.Y); return; }

        var dx = pt.X - _dragOriginScreen.X;
        var dy = pt.Y - _dragOriginScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // ignore jitter

        _dragging = true;
        // Clamp every step: a window dragged off-screen can be lost there, and the off-screen
        // position would be remembered for the next summon.
        var (x, y, _, _) = MonitorGeometry.ClampToScreens(
            _dragWindowOrigin.X + dx, _dragWindowOrigin.Y + dy, Width, Height, RenderScaling);
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
        UpdateResizeCursor(e.GetPosition(this));
    }
}
