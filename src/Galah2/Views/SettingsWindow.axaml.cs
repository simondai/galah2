using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The dialog is dismissed by its own button, never by an implicit OK: every edit is
        // already committed, so close is just close.
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.CloseRequested += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.CloseRequested -= Close;
        base.OnClosed(e);
    }
}
