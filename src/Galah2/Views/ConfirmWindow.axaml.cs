using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

/// <summary>
/// Yes/no confirmation.
///
/// Same rule as PromptWindow: the answer travels through a callback, never through a blocking
/// ShowDialog().GetAwaiter().GetResult(). That call blocks the UI thread the dialog needs to pump
/// input, so the window paints and then ignores every click and key press.
/// </summary>
public partial class ConfirmWindow : Window
{
    private Action<bool>? _onClosed;
    private bool _settled;

    public ConfirmWindow() => AvaloniaXamlLoader.Load(this);

    public ConfirmWindow(string title, string message, Action<bool> onClosed) : this()
    {
        Title = title;
        _onClosed = onClosed;
        this.FindControl<TextBlock>("Message")!.Text = message;
    }

    /// <summary>Show the confirmation; <paramref name="onClosed"/> receives true for confirm.</summary>
    public static void Ask(Window? owner, string title, string message, Action<bool> onClosed)
    {
        var window = new ConfirmWindow(title, message, onClosed);
        if (owner is not null && owner.IsVisible) window.Show(owner);
        else window.Show();
        window.Activate();
    }

    private void Settle(bool result)
    {
        if (_settled) return;   // closing can fire both a button handler and OnClosed
        _settled = true;
        _onClosed?.Invoke(result);
        _onClosed = null;
        Close();                // answering must also dismiss the window
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Settle(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Settle(false);

    protected override void OnClosed(EventArgs e)
    {
        // Dismissing with ✕ or Escape must be reported as "no", or the caller waits forever.
        Settle(false);
        base.OnClosed(e);
    }
}
