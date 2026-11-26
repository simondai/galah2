using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Galah2.Views;

public partial class SettingsWindow : Window
{
    private TextBox? _hotkeyBox;

    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _hotkeyBox = this.FindControl<TextBox>("HotkeyBox");
        if (_hotkeyBox is not null)
        {
            // 用 Tunnel 隧道拦截按键，避免 Alt 组合键被系统菜单或窗口默认快捷键吞掉。
            _hotkeyBox.AddHandler(KeyDownEvent, OnHotkeyBoxKeyDown, RoutingStrategies.Tunnel);
            _hotkeyBox.GotFocus += OnHotkeyBoxGotFocus;
        }
    }

    // Borderless chrome (the system title bar is gone): forward to the shared handlers.
    private void TitleBar_Drag(object? sender, PointerPressedEventArgs e) => DialogChrome.Drag(this, e);
    private void TitleBar_Close(object? sender, RoutedEventArgs e) => DialogChrome.Close(this, e);

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e) =>
        Services.LauncherService.Launch(new Services.LaunchRequest(
            Models.ItemKinds.Url, "https://github.com/simondai/galah2", null, null, false));

    private void OnHotkeyBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.ShowHotkeyRecordingPrompt();
    }

    private void OnHotkeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc 退出录制焦点，不直接关窗
        if (e.Key == Key.Escape)
        {
            Focus();
            e.Handled = true;
            return;
        }

        // 单独按下修饰键时等待主键
        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (DataContext is not ViewModels.SettingsViewModel vm) return;

        var parts = new List<string>(5);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("win");

        var keyName = MapKeyToName(e.Key);
        if (keyName is null)
        {
            e.Handled = true;
            return;
        }

        if (parts.Count == 0)
        {
            vm.ShowHotkeyNeedsModifier();
            e.Handled = true;
            return;
        }

        parts.Add(keyName);
        vm.HotkeyText = string.Join("+", parts);
        e.Handled = true;
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or Key.System;

    private static string? MapKeyToName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.F1 and <= Key.F24)
            return "f" + (1 + (key - Key.F1));

        return key switch
        {
            Key.Space => "space",
            Key.Tab => "tab",
            Key.Enter => "enter",
            Key.Back => "backspace",
            Key.Delete => "delete",
            Key.Insert => "insert",
            Key.Home => "home",
            Key.End => "end",
            Key.PageUp => "pageup",
            Key.PageDown => "pagedown",
            Key.Left => "left",
            Key.Up => "up",
            Key.Right => "right",
            Key.Down => "down",
            Key.Oem3 => "grave",
            Key.OemMinus => "minus",
            Key.OemPlus => "plus",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.OemQuestion => "slash",
            Key.OemSemicolon => "semicolon",
            Key.OemQuotes => "quote",
            Key.OemOpenBrackets => "bracketleft",
            Key.OemCloseBrackets => "bracketright",
            Key.OemPipe => "backslash",
            _ => null,
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.CloseRequested += Close;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DialogChrome.HandleEscape(this, e)) return;
        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyBox is not null)
            _hotkeyBox.GotFocus -= OnHotkeyBoxGotFocus;
        if (DataContext is ViewModels.SettingsViewModel vm)
            vm.CloseRequested -= Close;
        base.OnClosed(e);
    }
}
