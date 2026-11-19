using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

/// <summary>
/// Single-line prompt (rename a category, name a new one).
///
/// Shown non-modally relative to the panel and awaited on the caller's side: a modal dialog owned
/// by a hidden panel is the 1.x bug where the prompt opened behind every window and the app
/// appeared hung. Owner is always a visible window.
/// </summary>
public partial class PromptWindow : Window
{
    public PromptWindow() => AvaloniaXamlLoader.Load(this);

    public PromptWindow(string title, string initial) : this()
    {
        Title = title;
        this.FindControl<TextBox>("Input")!.Text = initial;
    }

    /// <summary>Ask for text; returns null when cancelled.</summary>
    public static string? Ask(Window? owner, string title, string initial)
    {
        var window = new PromptWindow(title, initial) { Owner = owner };
        var result = window.ShowDialog<string?>(owner ?? window).GetAwaiter().GetResult();
        return result;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) =>
        Close(this.FindControl<TextBox>("Input")?.Text);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
