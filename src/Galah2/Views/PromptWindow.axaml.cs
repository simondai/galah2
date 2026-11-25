using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

/// <summary>
/// Single-line prompt (rename a category, name a new one).
///
/// The result travels through a callback, never through ShowDialog().GetAwaiter().GetResult().
/// That pattern deadlocks: it blocks the UI thread that the dialog itself needs in order to pump
/// keyboard and mouse input, so the window appears but cannot be typed in, confirmed, or closed.
/// </summary>
public partial class PromptWindow : Window
{
    private Action<string?>? _onClosed;
    private bool _settled;

    public PromptWindow() => AvaloniaXamlLoader.Load(this);

    // Borderless chrome (the system title bar is gone): forward to the shared handlers.
    private void TitleBar_Drag(object? sender, Avalonia.Input.PointerPressedEventArgs e) => DialogChrome.Drag(this, e);
    private void TitleBar_Close(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => DialogChrome.Close(this, e);

    public PromptWindow(string title, string initial, Action<string?> onClosed) : this()
    {
        Title = title;
        _onClosed = onClosed;
        this.FindControl<TextBox>("Input")!.Text = initial;

        // Focus the field so the user can type immediately.
        Opened += (_, _) =>
        {
            var input = this.FindControl<TextBox>("Input");
            input?.Focus();
            input?.SelectAll();
        };
    }

    /// <summary>Show the prompt; <paramref name="onClosed"/> receives the text, or null if cancelled.</summary>
    public static void Ask(Window? owner, string title, string initial, Action<string?> onClosed)
    {
        var window = new PromptWindow(title, initial, onClosed);
        if (owner is not null && owner.IsVisible) window.Show(owner);
        else window.Show();
        window.Activate();
    }

    private void Settle(string? result)
    {
        if (_settled) return;   // a window can close and fire both handlers
        _settled = true;
        _onClosed?.Invoke(result);
        _onClosed = null;
        Close();                // answering must also dismiss the window
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        // Escape cancels, Enter confirms with the current text. The TextBox does not handle
        // either key by default, so the routed event reaches the window.
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            Settle(null);
            e.Handled = true;
            return;
        }
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)
        {
            Settle(this.FindControl<TextBox>("Input")?.Text);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) =>
        Settle(this.FindControl<TextBox>("Input")?.Text);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Settle(null);

    protected override void OnClosed(EventArgs e)
    {
        // Dismissing with ✕ or Escape counts as cancelling: the caller is always told.
        Settle(null);
        base.OnClosed(e);
    }
}
