using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

public partial class EditItemWindow : Window
{
    public EditItemWindow() => AvaloniaXamlLoader.Load(this);

    // Borderless chrome (the system title bar is gone): forward to the shared handlers.
    private void TitleBar_Drag(object? sender, Avalonia.Input.PointerPressedEventArgs e) => DialogChrome.Drag(this, e);
    private void TitleBar_Close(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => DialogChrome.Close(this, e);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is ViewModels.EditItemViewModel vm)
            vm.CloseRequested += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is ViewModels.EditItemViewModel vm)
            vm.CloseRequested -= Close;
        base.OnClosed(e);
    }
}
