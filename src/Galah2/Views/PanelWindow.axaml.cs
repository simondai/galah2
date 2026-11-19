using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Galah2.Services;
using Galah2.ViewModels;

namespace Galah2.Views;

public partial class PanelWindow : Window
{
    private Point _dragStart;
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

        // Record what transparency level actually got applied: the request can be downgraded by
        // the compositor, and a panel that silently falls back to opaque looks like a bug.
        // Logged here, not on a later Opened subscription: the event has already fired by now.
        DiagnosticsLog.Line("material", "requested=Mica actual=" + ActualTransparencyLevel);

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

    /// <summary>Drag the frameless panel by its chrome strip.</summary>
    private void Chrome_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(this);
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

        var now = e.GetPosition(this);
        var dx = now.X - _dragStart.X;
        var dy = now.Y - _dragStart.Y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // ignore jitter

        _dragging = true;
        Position = new PixelPoint((int)(Position.X + dx), (int)(Position.Y + dy));
        _dragStart = now;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }
}
