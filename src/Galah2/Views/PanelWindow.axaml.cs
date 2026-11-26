using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Galah2.Services;
using Galah2.Services.Interop;
using Galah2.ViewModels;

namespace Galah2.Views;

public partial class PanelWindow : Window
{
    private Point _dragOriginScreen;
    private PixelPoint _dragWindowOrigin;
    private bool _dragging;
    private bool _openedWired;

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

        // 面板隐藏后再次 Show() 时 OnOpened 可能再次触发，事件处理器只绑定一次，避免累积重复触发。
        if (!_openedWired)
        {
            _openedWired = true;

            AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(PointerPressedEvent, Window_PointerPressedTunnel, RoutingStrategies.Tunnel);

            if (this.FindControl<ScrollViewer>("TileScroll") is { } tileScroll)
                tileScroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Bubble,
                    handledEventsToo: true);

            Deactivated += (_, _) =>
            {
                _arm.Deactivated();
                if (DataContext is PanelViewModel { HideOnBlur: true }) Hide();
            };

            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        // Material follows the user's choice; the level is recorded because the compositor can
        // downgrade the request, and a panel that quietly fell back would look like a bug.
        ApplyMaterial();
        DiagnosticsLog.Line("material",
            "requested=" + RequestedMaterial + " actual=" + ActualTransparencyLevel);
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
                vm.SwitchTabRelative(-1);
                e.Handled = true;
                return;

            case Key.Right:
                vm.SwitchTabRelative(1);
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
        // A purely horizontal wheel/trackpad gesture has no vertical intent; "> 0 ? -1 : 1"
        // used to classify it as "scroll down" and switch the category at the bottom edge.
        if (e.Delta.Y == 0) return;

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

    /// <summary>
    /// A movement counts as a drag only past 8 px, so a normal click stays a click and the tile
    /// can still be launched (1.x's SystemParameters.MinimumHorizontalDragDistance semantics).
    /// </summary>
    public static bool PassedThreshold(Point now, Point origin) =>
        Math.Abs(now.X - origin.X) + Math.Abs(now.Y - origin.Y) >= 8;

    /// <summary>Begin one move-drag carrying the given id under the given format.</summary>
    private async void StartMoveDrag(PointerEventArgs e, DataFormat<string> format, string id)
    {
        DiagnosticsLog.Line("drag", "start id=" + id);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(format, id));
        try
        {
            var effect = await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            DiagnosticsLog.Line("drag", "end id=" + id + " effect=" + effect);
        }
        catch (Exception ex) { DiagnosticsLog.Line("drag", "failed: " + ex.Message); }
    }

    private void TabChip_DragOver(object? sender, DragEventArgs e)
    {
        // A chip accepts an incoming item (move it into this category) and an incoming chip
        // (reorder the strip). Two formats, one drop target.
        e.DragEffects = e.DataTransfer.Contains(ItemIdFormat) || e.DataTransfer.Contains(TabIdFormat)
            ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>Move the dragged item into the category it was dropped on.</summary>
    private void TabChip_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is not Button { Tag: TabViewModel tab }) return;

        // Chip onto chip: the target IS the destination slot (projection order, placeholder
        // never appears here - the tab strip has no sentinel).
        var draggedTab = e.DataTransfer.TryGetValue(TabIdFormat);
        if (!string.IsNullOrEmpty(draggedTab))
        {
            var target = vm.Tabs.ToList().FindIndex(t => t.Id == tab.Id);
            DiagnosticsLog.Line("drag", "drop tab=" + draggedTab + " -> slot " + target);
            if (target >= 0) vm.MoveTabToIndex(draggedTab!, target);
            e.Handled = true;
            return;
        }

        // TryGetValue returns the value or null - the DataTransfer idiom, not a bool + out.
        var itemId = e.DataTransfer.TryGetValue(ItemIdFormat);
        if (string.IsNullOrEmpty(itemId)) return;

        vm.MoveItemToTab(itemId, tab.Id);
        e.Handled = true;
    }

    // ---- category reorder by dragging a chip onto a chip ----
    //
    // The press is captured in Window_PointerPressedTunnel and the drag is started in
    // OnPointerMoved, both at the window, for the reason documented there: a Button swallows
    // the press before any instance handler on it can see it.

    private TabViewModel? _dragTab;
    private Point _dragTabOrigin;

    // ---- item reorder by dragging a tile onto a tile ----

    /// <summary>A tile accepts another tile (reorder inside the category) only.</summary>
    private void Tile_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(ItemIdFormat)
            ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>Drop onto a real tile: the dragged item takes this tile's slot.</summary>
    private void Tile_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;
        if (sender is not Button { Tag: ItemViewModel target }) return;
        if (target.IsAddTile) return;   // the placeholder handles its own drop

        var itemId = e.DataTransfer.TryGetValue(ItemIdFormat);
        if (string.IsNullOrEmpty(itemId)) return;

        DiagnosticsLog.Line("drag", "drop tile=" + itemId + " -> slot " + vm.Items.IndexOf(target));
        vm.MoveItemToIndex(itemId, vm.Items.IndexOf(target));
        e.Handled = true;
    }

    /// <summary>
    /// Drop onto the placeholder: "move to the end". The sentinel sits at Items.Count - 1, so
    /// its own index is exactly the last data slot - which is also why the placeholder can never
    /// be displaced: it is not draggable, and every end-destination lands on it.
    /// </summary>
    private void AddTile_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not PanelViewModel vm) return;

        var itemId = e.DataTransfer.TryGetValue(ItemIdFormat);
        if (string.IsNullOrEmpty(itemId)) return;

        vm.MoveItemToIndex(itemId, vm.Items.Count - 1);
        e.Handled = true;
    }

    /// <summary>
    /// The placeholder's gesture is its menu. A ContextMenu opens on right-click by itself;
    /// the left click that the button reports must open the same menu explicitly. No DataContext
    /// work is needed: the rows bind the sentinel's own add commands (PanelViewModel wires them).
    /// NOTE (2026-11-24): the earlier design swapped the menu's DataContext in Opening - that
    /// handler was never attached, because XAML event wiring on a ContextMenu does not fire.
    /// </summary>
    private void AddTile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu }) return;
        menu.Open();   // IsOpen has a protected setter; Open() is the public door.
    }

    /// <summary>
    /// The custom drag format for tile moves. Both ends of the drag share this one static
    /// instance. Identifier rules: ASCII letters, digits, dot and hyphen only - the slash of the
    /// old IDataObject name "galah/item-id" is rejected here.
    /// </summary>
    private static readonly DataFormat<string> ItemIdFormat =
        DataFormat.CreateStringApplicationFormat("galah.item-id");

    /// <summary>The custom drag format for category reorder (chip onto chip). Same rules.</summary>
    private static readonly DataFormat<string> TabIdFormat =
        DataFormat.CreateStringApplicationFormat("galah.tab-id");

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
        // Internal reorder drags (tile/chip) carry their own formats and already set their own
        // effect in the tile/chip handlers. This window-level handler sits ABOVE them on the
        // bubble route, so it must not overwrite their effect - doing so forced None and refused
        // every internal drop (the 2026-11-23 "all four drags dead" bug).
        if (e.DataTransfer.Contains(ItemIdFormat) || e.DataTransfer.Contains(TabIdFormat)) return;

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
    private static readonly Cursor NorthSouthCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor WestEastCursor = new(StandardCursorType.SizeWestEast);

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
        if (edges != 0)
        {
            if (!Win32.GetCursorPos(out var pt)) return;
            _resizeEdges = edges;
            _resizeOriginScreen = new Point(pt.X, pt.Y);
            _resizeWindowOrigin = Position;
            _resizeOriginalSize = new Size(Bounds.Width, Bounds.Height);
            _arm.ArmResize();
            e.Handled = true;   // a border press resizes; tiles and the chrome drag must not act
            return;
        }

        // Tile and category-chip drags START here, not on the button itself.
        //
        // A Button's class handler marks PointerPressed handled (Button.cs: e.Handled = true for
        // a left press), and the route only calls an instance handler when the event is unhandled
        // (EventRoute.cs). So `PointerPressed="..."` on a tile or a chip is dead code - the
        // handler never runs, the drag origin stays null, and the drag can never start. The
        // tunnel phase runs BEFORE the class handler, which is why the chrome drag and the edge
        // resize were always registered here; the reorder gestures belong in the same place.
        // The hit element is usually a descendant of the button (label/icon), not the button,
        // so the lookup walks up. The round new-category chip has no Tag and the placeholder
        // tile is refused by IsAddTile - both fall through to the clearing default, which is
        // exactly right: neither is draggable.
        var point = e.GetPosition(this);
        var hit = DragButtonFrom(e.Source);
        switch (hit)
        {
            case Button { Tag: ItemViewModel { IsAddTile: false } tile }:
                _dragTab = null;
                _dragTile = tile;
                _dragTileOrigin = point;
                return;

            case Button { Tag: TabViewModel tab }:
                _dragTile = null;
                _dragTab = tab;
                _dragTabOrigin = point;
                return;

            default:
                _dragTile = null;
                _dragTab = null;
                return;
        }
    }

    /// <summary>
    /// The draggable button a press really started on. The hit element is usually a descendant
    /// of the tile (its label TextBlock or the icon Panel), never the Button itself, so the
    /// search walks up. public like EdgesFor: pure, no dispatcher, asserted directly by probes.
    /// </summary>
    public static Button? DragButtonFrom(object? source)
    {
        var v = source as Visual;
        while (v is not null)
        {
            if (v is Button { Tag: ItemViewModel or TabViewModel } hit) return hit;
            v = v.GetVisualParent();
        }
        return null;
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
            var e when (e & (EdgeN | EdgeS)) != 0 => NorthSouthCursor,
            var e when (e & (EdgeE | EdgeW)) != 0 => WestEastCursor,
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
            _dragTile = null;
            _dragTab = null;
            _arm.ButtonUpMoved();
            UpdateResizeCursor(e.GetPosition(this));
            return;
        }

        // Tile and chip reorder drags START here, at the window. The window override is the one
        // handler guaranteed to run for every move (it is the bubble terminus and Button does not
        // mark PointerMoved handled), and the press that armed _dragTile/_dragTab came through
        // the tunnel phase - the only place that sees a press before Button swallows it.
        var dragPoint = e.GetPosition(this);
        if (_dragTile is not null && PassedThreshold(dragPoint, _dragTileOrigin))
        {
            var tile = _dragTile;
            _dragTile = null;   // one drag per press
            StartMoveDrag(e, ItemIdFormat, tile.Id);
            return;
        }
        if (_dragTab is not null && PassedThreshold(dragPoint, _dragTabOrigin))
        {
            var tab = _dragTab;
            _dragTab = null;
            StartMoveDrag(e, TabIdFormat, tab.Id);
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
        // Position and work areas are device pixels; Width/Height are DIPs. Keep every
        // computation in device pixels and convert back only when assigning Width/Height.
        var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var dx = cursorX - _resizeOriginScreen.X;
        var dy = cursorY - _resizeOriginScreen.Y;

        var x = (double)_resizeWindowOrigin.X;
        var y = (double)_resizeWindowOrigin.Y;
        var w = _resizeOriginalSize.Width * scale;
        var h = _resizeOriginalSize.Height * scale;
        // 先记录拖动前的右边界/下边界，防止缩到最小尺寸后继续拖动导致整窗漂移。
        var rightEdge = x + w;
        var bottomEdge = y + h;

        if ((_resizeEdges & EdgeE) != 0) w += dx;
        if ((_resizeEdges & EdgeS) != 0) h += dy;
        if ((_resizeEdges & EdgeW) != 0) w -= dx;
        if ((_resizeEdges & EdgeN) != 0) h -= dy;

        var minW = MinWidth * scale;
        var minH = MinHeight * scale;
        if (w < minW) w = minW;
        if (h < minH) h = minH;

        if ((_resizeEdges & EdgeW) != 0) x = rightEdge - w;
        if ((_resizeEdges & EdgeN) != 0) y = bottomEdge - h;

        var area = BestArea(x, y, w, h);
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
        _dragging = false;
        _resizeEdges = 0;
        _arm.Release();
        UpdateResizeCursor(e.GetPosition(this));
    }
}
