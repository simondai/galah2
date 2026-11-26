using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Galah2.Services;

/// <summary>
/// Tray icon built on Avalonia's <see cref="TrayIcon"/>.
///
/// 1.0 spent 250 lines on a raw Win32 shell-icon implementation (NIF_MESSAGE, callback decoding,
/// message-only host windows) because WPF has no tray API. Avalonia ships one, and its Win32
/// backend already maps <c>WM_LBUTTONUP</c> → <see cref="TrayIcon.Clicked"/> and
/// <c>WM_RBUTTONUP</c> → automatic <see cref="NativeMenu"/> popup, which is exactly the 1.0
/// interaction contract (left = activate panel, right = menu).
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TrayIcon _icon;

    /// <summary>The native tray icon (probe hook: the menu rows are asserted through it).</summary>
    public TrayIcon Icon => _icon;

    public TrayService(Stream iconStream, Action onActivate, Action onSettings, Action onRandPass, Action onExit,
        Action? onClipboard = null)
    {
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(new Bitmap(iconStream)),
            IsVisible = true,
            ToolTipText = "Galah",
            Menu = BuildMenu(onSettings, onRandPass, onClipboard, onExit)
        };
        _icon.Clicked += (_, _) => onActivate();

        if (Application.Current is not null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
    }

    private static NativeMenu BuildMenu(Action onSettings, Action onRandPass, Action? onClipboard, Action onExit)
    {
        var menu = new NativeMenu();

        var settings = new NativeMenuItem("设置…");
        settings.Click += (_, _) => onSettings();
        menu.Items.Add(settings);

        // RandPass generator entry (spec 2026-11-24-randpass) - the third of the three entries,
        // beside the panel's ☰ menu row and the title-bar keyhole.
        var randpass = new NativeMenuItem("RandPass");
        randpass.Click += (_, _) => onRandPass();
        menu.Items.Add(randpass);

        if (onClipboard is not null)
        {
            var clipboard = new NativeMenuItem("剪贴板历史");
            clipboard.Click += (_, _) => onClipboard();
            menu.Items.Add(clipboard);
        }

        menu.Items.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("退出");
        exit.Click += (_, _) => onExit();
        menu.Items.Add(exit);

        return menu;
    }

    public void Dispose()
    {
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
