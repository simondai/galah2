using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

/// <summary>Yes/no confirmation. Owner is always a visible window (see PromptWindow).</summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => AvaloniaXamlLoader.Load(this);

    public ConfirmWindow(string title, string message) : this()
    {
        Title = title;
        this.FindControl<TextBlock>("Message")!.Text = message;
    }

    /// <summary>Ask for confirmation; false when cancelled or dismissed.</summary>
    public static bool Ask(Window? owner, string title, string message)
    {
        var window = new ConfirmWindow(title, message) { Owner = owner };
        return window.ShowDialog<bool>(owner ?? window).GetAwaiter().GetResult();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
