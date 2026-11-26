using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Galah2.ViewModels;

namespace Galah2.Views;

public partial class ClipboardHistoryWindow : Window
{
    private TextBox? _searchBox;
    private ListBox? _historyList;

    public ClipboardHistoryWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _searchBox = this.FindControl<TextBox>("SearchBox");
        _historyList = this.FindControl<ListBox>("HistoryList");
        AddHandler(KeyDownEvent, OnWindowKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private void TitleBar_Drag(object? sender, PointerPressedEventArgs e) => DialogChrome.Drag(this, e);
    private void TitleBar_Close(object? sender, RoutedEventArgs e) => DialogChrome.Close(this, e);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is ClipboardHistoryViewModel vm)
            vm.CloseRequested += Close;
        _searchBox?.Focus();
    }

    private void OnWindowKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ClipboardHistoryViewModel vm) return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Down:
                vm.MoveSelection(1);
                ScrollSelectedIntoView(vm);
                e.Handled = true;
                break;

            case Key.Up:
                vm.MoveSelection(-1);
                ScrollSelectedIntoView(vm);
                e.Handled = true;
                break;

            case Key.Enter:
                vm.CommitSelected();
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectedIntoView(ClipboardHistoryViewModel vm)
    {
        if (_historyList is not null && vm.SelectedItem is not null)
            _historyList.ScrollIntoView(vm.SelectedItem);
    }

    private void HistoryList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ClipboardHistoryViewModel vm)
            vm.CommitSelected();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is ClipboardHistoryViewModel vm)
        {
            vm.CloseRequested -= Close;
            vm.Dispose();
        }
        base.OnClosed(e);
    }
}
