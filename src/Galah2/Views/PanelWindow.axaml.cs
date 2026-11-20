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

        // Material follows the user's choice: translucent asks the compositor for Mica, opaque asks
        // for nothing. The applied level is recorded because the compositor can downgrade the
        // request, and a panel that quietly fell back to opaque would look like a bug.
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
    /// Ask the compositor for the transparency level the material setting implies.
    ///
    /// "None" is not an option even for an opaque panel: a window without an alpha channel cannot
    /// clip its content, so the rounded corners render square. Transparent keeps the corners, and
    /// an opaque Border fill is what makes it look solid.
    /// </summary>
    public void ApplyMaterial()
    {
        TransparencyLevelHint = RequestedMaterial == Galah2.Models.PanelMaterials.Translucent
            ? new[] { Avalonia.Controls.WindowTransparencyLevel.Mica,
                      Avalonia.Controls.WindowTransparencyLevel.AcrylicBlur,
                      Avalonia.Controls.WindowTransparencyLevel.Transparent }
            : new[] { Avalonia.Controls.WindowTransparencyLevel.Transparent };
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            return;
        }

        if (!Win32.GetCursorPos(out var pt)) return;
        var dx = pt.X - _dragOriginScreen.X;
        var dy = pt.Y - _dragOriginScreen.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // ignore jitter

        _dragging = true;
        Position = new PixelPoint((int)(_dragWindowOrigin.X + dx), (int)(_dragWindowOrigin.Y + dy));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }
}
