using Avalonia.Controls;
using Avalonia.Input;

namespace Galah2.Views;

/// <summary>
/// Shared chrome behaviour for the borderless dialog windows (settings, picker, edit,
/// confirm, prompt, maintenance). They dropped the system title bar for the panel's
/// surface language (see PanelWindow's RootBorder), which costs them the OS-provided
/// drag and close affordances - these two static handlers give them back, wired from
/// XAML as <c>PointerPressed="DialogChrome.Drag"</c> / <c>Click="DialogChrome.Close"</c>.
///
/// Close goes through Window.Close on purpose: every dialog settles its callback in
/// OnClosed, so the chrome button needs no per-window logic.
/// </summary>
public static class DialogChrome
{
    /// <summary>Left-button press on the title bar starts a window drag.</summary>
    public static void Drag(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Window window) return;
        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
        // Avalonia's Win32 platform drag; there is no WPF-style parameterless DragMove.
        window.BeginMoveDrag(e);
    }

    /// <summary>Close the dialog the way the OS title bar ✕ would have.</summary>
    public static void Close(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Window window) window.Close();
    }
}
