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
    private readonly object _gate = new();
    private Thread? _watcher;

    // A generation counter keeps a still-exiting watcher from clearing a newer one when the user
    // flips light -> system quickly. Each watch loop belongs to exactly one generation.
    private volatile int _generation;
    private int _disposed;
    private volatile bool _following;

    /// <summary>Raised with the new dark state when the system theme changes.</summary>
    public event Action<bool>? SystemThemeChanged;

    /// <summary>True while the system-theme watch loop is running (diagnostics / regression probes).</summary>
    public bool IsWatching
    {
        get { lock (_gate) { return _watcher is { IsAlive: true }; } }
    }

    /// <summary>Apply a preference ("system" | "light" | "dark").</summary>
    public void Apply(string? preference)
    {
        var mode = ThemeModes.All.Contains(preference) ? preference! : ThemeModes.System;

        switch (mode)
        {
            case ThemeModes.Light:
                StopWatching();
                SetVariant(ThemeVariant.Light);
                break;

            case ThemeModes.Dark:
                StopWatching();
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
        lock (_gate)
        {
            // The old loop clears _watcher when it exits. If it has not exited yet, a new
            // generation makes it a no-op and lets this one take over.
            if (_watcher is { IsAlive: true } && _following) return;
            var generation = ++_generation;
            _watcher = new Thread(() => WatchLoop(generation))
            {
                IsBackground = true,
                Name = "galah2-theme-watch",
            };
            _watcher.Start();
        }
    }

    /// <summary>
    /// Stop following the system without cancelling the shared token: the same service may be
    /// asked to follow again later, and a cancelled CancellationTokenSource cannot be reused.
    /// </summary>
    private void StopWatching()
    {
        _following = false;
        lock (_gate)
        {
            _generation++;
            _watcher = null;
        }
        // Do not Join on the UI thread: the old loop notices the generation change within one
        // poll interval and retires itself without touching the new watcher.
    }

    /// <summary>
    /// Poll the personalisation value. A registry change notification would be tidier but needs a
    /// waitable event and stops reporting once Explorer rewrites the key handle; two registry
    /// reads per half second is negligible next to that, and a theme switch is a rare,
    /// user-initiated event.
    /// </summary>
    private void WatchLoop(int generation)
    {
        var last = SystemPrefersDark();

        try
        {
            while (!_stop.IsCancellationRequested && _following && generation == _generation)
            {
                try { _stop.Token.WaitHandle.WaitOne(PollIntervalMs); }
                catch (ObjectDisposedException) { return; }
                if (_stop.IsCancellationRequested || !_following || generation != _generation) return;

                bool now;
                try { now = SystemPrefersDark(); }
                catch (Exception) { continue; }

                if (now == last) continue;
                last = now;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_following || generation != _generation) return;
                    SetVariant(now ? ThemeVariant.Dark : ThemeVariant.Light);
                    SystemThemeChanged?.Invoke(now);
                });
            }
        }
        finally
        {
            lock (_gate)
            {
                // Only the current generation may retire the field; an old loop must not clear
                // the handle of a watcher that replaced it.
                if (generation == _generation) _watcher = null;
            }
        }
    }

    public void Dispose()
    {
        // Idempotent: RequestExit() disposes the runtime and desktop.Exit disposes it again.
        // A second Cancel() on an already disposed CTS throws ObjectDisposedException.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _following = false;
        Thread? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }
        try { _stop.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }
        watcher?.Join(1000);
        _stop.Dispose();
    }
}
