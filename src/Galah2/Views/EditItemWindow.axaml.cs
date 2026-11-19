using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

public partial class EditItemWindow : Window
{
    public EditItemWindow() => AvaloniaXamlLoader.Load(this);

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
