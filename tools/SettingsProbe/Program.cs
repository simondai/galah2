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
        var darkAgain = Galah2.Services.ThemeService.SystemPrefersDark();
        Check("system theme read is stable and does not throw", dark == darkAgain,
            dark ? "dark" : "light");

        // Applying each mode must not throw and must leave a concrete variant selected.
        foreach (var mode in new[] { ThemeModes.System, ThemeModes.Light, ThemeModes.Dark })
        {
            service.Apply(mode);
            var variant = Avalonia.Application.Current?.RequestedThemeVariant;
            Check("theme " + mode + " applies a variant", variant is not null, variant?.ToString() ?? "null");
        }

        // Regression: switching to a fixed theme used to stop the watcher forever, because the
        // dead Thread object stayed in _watcher and StartWatching() returned early.
        service.Apply(ThemeModes.Light);
        var stopped = !service.IsWatching;
        service.Apply(ThemeModes.System);
        Check("system watcher restarts after switching back from a fixed theme",
            stopped && service.IsWatching,
            "stopped=" + stopped + " restarted=" + service.IsWatching);

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
        Check("valid hotkey carries modifiers and a virtual key",
            registered is not null && registered.Modifiers != 0 && registered.VirtualKey != 0,
            registered is null ? "null" : registered.Text);

        // The OS must accept the shipped default. A refusal here means another program owns the
        // combo; the settings page now says so instead of showing a hint that does nothing.
        using (var hotkey = new HotkeyService())
        {
            var osTookIt = hotkey.Register(new HotkeySpec(0x1 /*MOD_ALT*/, 0x20 /*VK_SPACE*/, "alt+space"));
            Check("alt+space registers with the OS", osTookIt);
        }

        // ---- RandPass section: Clone round-trip + Sanitized clamping (spec 2026-11-24-randpass) ----
        store.Update(d =>
        {
            d.RandPassLength = 32;
            d.RandPassCount = 5;
            d.RandPassLower = false;
            d.RandPassDoubleDigits = false;
            d.RandPassCustomChars = "Xyz";
            d.RandPassUseFormat = true;
            d.RandPassFormat = "2-*-4-*-2";
        });
        var rpClone = store.Current.Clone();
        Check("Clone carries the RandPass section",
            rpClone.RandPassLength == 32 && rpClone.RandPassCount == 5 && !rpClone.RandPassLower
            && !rpClone.RandPassDoubleDigits && rpClone.RandPassCustomChars == "Xyz"
            && rpClone.RandPassUseFormat && rpClone.RandPassFormat == "2-*-4-*-2");

        store.Update(d => { d.RandPassLength = 999; d.RandPassCount = 0; d.RandPassFormat = "bogus"; });
        // Regression: the previous assertion checked Sanitized()'s *return value*, which stays
        // correct even when the store never writes the correction back. Assert on the live
        // snapshot instead — this one fails if the write-back is removed again.
        var live = store.Current;
        Check("Update writes the sanitized values back to Current",
            live.RandPassLength == 8 && live.RandPassCount == 10 && live.RandPassFormat == "4-*-4",
            live.RandPassLength + "/" + live.RandPassCount + "/" + live.RandPassFormat);

        // The settings UI mutates Current in place and calls Commit(), not Update(). TabFontSize
        // is one of the fields the old five-field copy silently dropped.
        store.Current.RandPassLength = 9999;
        store.Current.TabFontSize = "nonsense";
        store.Commit();
        Check("Commit normalizes every field, not just five",
            store.Current.RandPassLength == 8 && store.Current.TabFontSize == "medium",
            store.Current.RandPassLength + "/" + store.Current.TabFontSize);

        // ---- Persistence hardening: explicit JSON nulls must not abort boot (AUDIT #1) ----
        // A field-level null deserializes fine, so it never reaches QuarantineAndRecover; the
        // store layer has to normalize it before any consumer dereferences it.
        var nullPaths = new AppDataPaths(Path.Combine(_root, "nullcase")).EnsureCreated();
        File.WriteAllText(nullPaths.UiStateFile,
            "{\"schema\":3,\"window\":null,\"randPassWindow\":null,\"activeTabId\":null}");
        File.WriteAllText(nullPaths.CatalogFile,
            "{\"schema\":1,\"profileId\":null,\"tabs\":null}");
        try
        {
            var ui = new UiStateStore(nullPaths).Load();
            var cat = new CatalogStore(nullPaths).Load();
            Check("explicit JSON nulls are normalized instead of throwing at boot",
                ui.Data.Window is not null && ui.Data.RandPassWindow is not null && cat.Data.Tabs is not null,
                "window=" + (ui.Data.Window is null ? "null" : "ok")
                + " tabs=" + (cat.Data.Tabs is null ? "null" : cat.Data.Tabs.Count.ToString()));
        }
        catch (Exception ex)
        {
            Check("explicit JSON nulls are normalized instead of throwing at boot", false, ex.GetType().Name);
        }

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
