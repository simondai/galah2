using Avalonia;
using Avalonia.Styling;
using Galah2.Models;

namespace Galah2.Services;

/// <summary>
/// Applies the theme preference and keeps a "follow system" choice live.
///
/// Avalonia picks the palette itself from <c>Application.RequestedThemeVariant</c> (our
/// Styles/Tokens.axaml supplies a Dark and a Light dictionary), so applying a preference is one
/// assignment. The only work left is the system case: a theme switch in Windows settings must be
/// noticed, and Avalonia does not forward WM_SETTINGCHANGE.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const int PollIntervalMs = 500;

    private readonly CancellationTokenSource _stop = new();
    private Thread? _watcher;
    private volatile bool _following;

    /// <summary>Raised with the new dark state when the system theme changes.</summary>
    public event Action<bool>? SystemThemeChanged;

    /// <summary>Apply a preference ("system" | "light" | "dark").</summary>
    public void Apply(string? preference)
    {
        var mode = ThemeModes.All.Contains(preference) ? preference! : ThemeModes.System;

        switch (mode)
        {
            case ThemeModes.Light:
                _following = false;
                SetVariant(ThemeVariant.Light);
                break;

            case ThemeModes.Dark:
                _following = false;
                SetVariant(ThemeVariant.Dark);
                break;

            default:
                _following = true;
                SetVariant(SystemPrefersDark() ? ThemeVariant.Dark : ThemeVariant.Light);
                StartWatching();
                break;
        }
    }

    private static void SetVariant(ThemeVariant variant)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = variant;
    }

    /// <summary>True when Windows is currently in dark mode.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            // A locked-down or redirected profile: assume light rather than guessing dark.
            return false;
        }
    }

    private void StartWatching()
    {
        if (_watcher is not null) return;

        _watcher = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "galah2-theme-watch",
        };
        _watcher.Start();
    }

    /// <summary>
    /// Poll the personalisation value. A registry change notification would be tidier but needs a
    /// waitable event and stops reporting once Explorer rewrites the key handle; two registry
    /// reads per half second is negligible next to that, and a theme switch is a rare,
    /// user-initiated event.
    /// </summary>
    private void WatchLoop()
    {
        var last = SystemPrefersDark();

        while (!_stop.IsCancellationRequested && _following)
        {
            try { _stop.Token.WaitHandle.WaitOne(PollIntervalMs); }
            catch (ObjectDisposedException) { return; }
            if (_stop.IsCancellationRequested || !_following) return;

            bool now;
            try { now = SystemPrefersDark(); }
            catch (Exception) { continue; }

            if (now == last) continue;
            last = now;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_following) return;
                SetVariant(now ? ThemeVariant.Dark : ThemeVariant.Light);
                SystemThemeChanged?.Invoke(now);
            });
        }
    }

    public void Dispose()
    {
        _following = false;
        _stop.Cancel();
        _watcher?.Join(1000);
        _stop.Dispose();
    }
}
