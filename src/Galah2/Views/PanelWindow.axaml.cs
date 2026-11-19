using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

public partial class PanelWindow : Window
{
    public PanelWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
