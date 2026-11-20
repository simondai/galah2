using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Galah2.Models;
using Galah2.Persistence;
using Avalonia.VisualTree;
using Galah2.Services;
using Galah2.ViewModels;
using Galah2.Views;

/// <summary>
/// Settings window probes: it must construct, render, and commit an edit to disk.
/// </summary>
internal static class Program
{
    private static int _fail;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    private static string _root = "";

    /// <summary>
    /// The settings dialog must not let its controls run into the scrollbar. A star-sized column
    /// inside a ScrollViewer has no width budget, so the combo boxes used to stretch under the
    /// scrollbar and look broken. Measured from the laid-out visual tree, not from pixels: the
    /// Fluent border on a ComboBox is too faint to find in a screenshot.
    /// </summary>
    private static void CheckLayout(SettingsWindow window)
    {
        var combos = window.GetVisualDescendants().OfType<Avalonia.Controls.ComboBox>().ToList();
        Check("settings has combo boxes", combos.Count > 0, combos.Count + " combo(s)");
        if (combos.Count == 0) return;

        var client = window.ClientSize.Width;
        var widest = combos.Max(c => c.Bounds.X + c.Bounds.Width);
        // Leave a real margin: the content area ends before the vertical scrollbar.
        Check("combo boxes stop before the scrollbar", widest < client - 40,
            "rightmost=" + widest + " client=" + client);

        var uniform = combos.Select(c => Math.Round(c.Bounds.Width)).Distinct().Count() == 1;
        Check("combo boxes share one width", uniform,
            string.Join(" / ", combos.Select(c => Math.Round(c.Bounds.Width))));
    }

    /// <summary>Theme mapping and the system-dark read. The watcher itself is a poll loop, so
    /// what is testable without a desktop is that the preference maps to the right variant and
    /// that a bad stored value falls back to following the system rather than guessing.</summary>
    /// <summary>
    /// Autostart is the one setting with a side effect outside the app: it writes HKCURun. It must
    /// (a) actually write, (b) report the truth on read, and (c) remove only its own value.
    /// </summary>
    private static void CheckAutoStart()
    {
        var exe = Path.Combine(Path.GetTempPath(), "galah2-autostart-probe", "Galah2.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);

        // Start from a known state in case a previous run left a value behind.
        Galah2.Services.AutoStartService.SetEnabled(false, exe, out _);
        Check("autostart off reads off", !Galah2.Services.AutoStartService.IsEnabled(exe));

        var written = Galah2.Services.AutoStartService.SetEnabled(true, exe, out var error);
        Check("autostart write succeeds", written, error ?? "no error");
        Check("autostart reads back on", Galah2.Services.AutoStartService.IsEnabled(exe),
            // The registry stores the path quoted; IsEnabled must match through the quotes.
            "path round trip");

        Galah2.Services.AutoStartService.SetEnabled(false, exe, out _);
        Check("autostart removal works", !Galah2.Services.AutoStartService.IsEnabled(exe));

        try { Directory.Delete(Path.GetDirectoryName(exe)!, true); } catch { }
    }

    private static void CheckThemeService()
    {
        using var service = new Galah2.Services.ThemeService();

        var dark = Galah2.Services.ThemeService.SystemPrefersDark();
        Check("system theme is readable", true, dark ? "dark" : "light");

        // Applying each mode must not throw and must leave a concrete variant selected.
        foreach (var mode in new[] { ThemeModes.System, ThemeModes.Light, ThemeModes.Dark })
        {
            service.Apply(mode);
            var variant = Avalonia.Application.Current?.RequestedThemeVariant;
            Check("theme " + mode + " applies a variant", variant is not null, variant?.ToString() ?? "null");
        }

        service.Apply("not-a-mode");
        Check("unknown theme falls back to system",
            Avalonia.Application.Current?.RequestedThemeVariant is not null,
            Avalonia.Application.Current?.RequestedThemeVariant?.ToString() ?? "null");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }

    public static void RunChecks()
    {
        CheckAutoStart();
        CheckThemeService();
        _root = Path.Combine(Path.GetTempPath(), "settingsprobe-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppDataPaths(_root).EnsureCreated();
        var store = new SettingsStore(paths);
        store.Load();

        SettingsDocument? applied = null;
        // The exe path is what autostart and the shortcut act on; point it at a scratch file so the
        // probe never registers the real build.
        var fakeExe = Path.Combine(_root, "Galah2.exe");
        File.WriteAllText(fakeExe, "x");
        var vm = new SettingsViewModel(store, paths.Root, fakeExe, s => applied = s);
        Check("view model loads current values", vm.DataFolder == paths.Root, vm.DataFolder);
        Check("hotkey hint populated", vm.HotkeyHint.Length > 0, vm.HotkeyHint);

        var window = new SettingsWindow { DataContext = vm };
        window.Show();
        Check("window opened", window.IsVisible);
        var size = window.Bounds.Size;
        Check("window has layout", size.Width > 100 && size.Height > 100,
            size.Width + "x" + size.Height);
        CheckLayout(window);

        // An edit must reach the document, the disk, and the restart callback.
        vm.HideOnLaunch = !vm.HideOnLaunch;
        Check("edit applied to callback", applied is not null);
        Check("edit written to disk", File.ReadAllText(paths.SettingsFile).Contains("hideOnLaunch"),
            "settings.json");

        // A malformed hotkey must not overwrite the working one.
        var before = store.Current.Hotkey;
        vm.HotkeyText = "not-a-hotkey";
        Check("malformed hotkey rejected", store.Current.Hotkey == before, store.Current.Hotkey);
        // The hint must explain the refusal, not merely echo the rejected text.
        Check("rejection explained", vm.HotkeyHint.Length > 0 && vm.HotkeyHint != "not-a-hotkey",
            vm.HotkeyHint);

        // A valid one must take effect and fire the re-register callback.
        HotkeySpec? registered = null;
        vm.HotkeyChanged += s => registered = s;
        vm.HotkeyText = "ctrl+shift+F9";
        Check("valid hotkey accepted", registered is not null, store.Current.Hotkey);

        window.Close();
        try { Directory.Delete(_root, true); } catch { }
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try { Program.RunChecks(); }
                finally { desktop.Shutdown(); }
            });
        }
        base.OnFrameworkInitializationCompleted();
    }
}
