using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Galah2.ViewModels;

namespace Galah2.Views;

public partial class RandPassWindow : Window
{
    public RandPassWindow() => AvaloniaXamlLoader.Load(this);

    // Borderless chrome (the system title bar is gone): forward to the shared handlers.
    private void TitleBar_Drag(object? sender, PointerPressedEventArgs e) => DialogChrome.Drag(this, e);
    private void TitleBar_Close(object? sender, RoutedEventArgs e) => DialogChrome.Close(this, e);

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
}
