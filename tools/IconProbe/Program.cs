using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

/// <summary>
/// Icon extraction probes. They must run inside a live Avalonia application because
/// <c>WriteableBitmap</c> needs a real render backend; a console-only Main cannot create one and
/// every extraction would fail with "Unable to locate IPlatformRenderInterface".
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
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
        var root = Path.Combine(Path.GetTempPath(), "iconprobe-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppDataPaths(root).EnsureCreated();
        IconService.Initialize(paths);

        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Real executables: the colour-icon path.
        foreach (var (label, path) in new[]
                 {
                     ("notepad.exe", Path.Combine(sys, "notepad.exe")),
                     ("calc.exe", Path.Combine(sys, "calc.exe")),
                     ("explorer.exe", Path.Combine(win, "explorer.exe")),
                 })
        {
            var item = new CatalogItem { Id = label, Name = label, Kind = ItemKinds.App, Target = path };
            var bmp = IconService.GetAsync(item).GetAwaiter().GetResult();
            Check(label, bmp is not null,
                bmp is null ? "null" : bmp.PixelSize.Width + "x" + bmp.PixelSize.Height);
        }

        // Snap-ins: the .msc route, including the shell handler fallback.
        //
        // Measured on this machine: the handler and the generic SHGetFileInfoW route return
        // essentially identical results for every .msc tried (services.msc 217 saturated pixels
        // either way; devmgmt.msc is a near-greyscale icon in both). 1.x recorded that
        // SHGetFileInfoW fell back to a grey document icon here, and that did not reproduce.
        // The handler is kept because it is what Explorer itself uses, but it is not what makes
        // these icons work on this build.
        foreach (var name in new[] { "services.msc", "devmgmt.msc" })
        {
            var path = Path.Combine(sys, name);
            if (!File.Exists(path)) { Check(name + " fixture", false, "missing"); continue; }

            var item = new CatalogItem { Id = name, Name = name, Kind = ItemKinds.App, Target = path };
            var bmp = IconService.GetAsync(item).GetAwaiter().GetResult();
            Check(name, bmp is not null,
                bmp is null ? "null" : bmp.PixelSize.Width + "x" + bmp.PixelSize.Height);
        }

        // An image override must be decoded straight from disk, not through the shell.
        var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
        var payload = new CatalogItem
        {
            Id = "o", Name = "override", Kind = ItemKinds.File, Target = ico,
            Icon = new IconReference { Mode = IconModes.Override, OverrideSource = ico }
        };
        var overrideBmp = IconService.GetAsync(payload).GetAwaiter().GetResult();
        Check("icon override", overrideBmp is not null,
            overrideBmp is null ? "null" : overrideBmp.PixelSize.Width + "x" + overrideBmp.PixelSize.Height);

        // Windows Settings protocol: resolves to native Settings gear icon.
        var settingsItem = new CatalogItem
        {
            Id = "settings", Name = "bluetooth", Kind = ItemKinds.Protocol, Target = "ms-settings:bluetooth"
        };
        var settingsBmp = IconService.GetAsync(settingsItem).GetAwaiter().GetResult();
        Check("ms-settings icon", settingsBmp is not null,
            settingsBmp is null ? "null" : settingsBmp.PixelSize.Width + "x" + settingsBmp.PixelSize.Height);

        // Catalog sources: all 6 categories enumerate cleanly, and StoreApps yields real UWP apps with icons.
        var sourceService = new Galah2.Services.CatalogSources.CatalogSourceService();
        Check("6 picker categories", sourceService.Categories.Count == 6, sourceService.Categories.Count + " tabs");
        var storeApps = sourceService.Enumerate(Galah2.Services.CatalogSources.SeedCategory.StoreApps);
        Check("store apps enumerated", storeApps.Count > 0, storeApps.Count + " app(s)");
        if (storeApps.Count > 0)
        {
            var first = storeApps[0];
            var storeBmp = IconService.GetAsync(new CatalogItem
            {
                Id = "uwp", Name = first.Name, Kind = first.Kind, Target = first.Target
            }).GetAwaiter().GetResult();
            Check("store app icon", storeBmp is not null,
                storeBmp is null ? "null (" + first.Name + ")" : first.Name + " " + storeBmp.PixelSize.Width + "x" + storeBmp.PixelSize.Height);
        }

        var folders = sourceService.Enumerate(Galah2.Services.CatalogSources.SeedCategory.ShellFolders);
        Check("shell folders & drives", folders.Count >= 10, folders.Count + " item(s)");
        var tools = sourceService.Enumerate(Galah2.Services.CatalogSources.SeedCategory.SystemTools);
        Check("system tools", tools.Count >= 15, tools.Count + " item(s)");
        var desktopApps = sourceService.Enumerate(Galah2.Services.CatalogSources.SeedCategory.InstalledApps);
        Check("desktop apps", desktopApps.Count > 0, desktopApps.Count + " item(s)");
        var quickActions = sourceService.Enumerate(Galah2.Services.CatalogSources.SeedCategory.QuickActions);
        Check("quick actions", quickActions.Count == 9, quickActions.Count + " action(s)");
        if (quickActions.Count > 0)
        {
            var qa = quickActions[0];
            var qaBmp = IconService.GetAsync(new CatalogItem
            {
                Id = "qa", Name = qa.Name, Kind = qa.Kind, Target = qa.Target,
                Icon = new IconReference { Mode = IconModes.Override, OverrideSource = qa.IconOverride }
            }).GetAwaiter().GetResult();
            Check("quick action icon", qaBmp is not null,
                qaBmp is null ? "null" : qa.Name + " " + qaBmp.PixelSize.Width + "x" + qaBmp.PixelSize.Height);
        }

        // Degradation: an unknown protocol target is not a file, and a missing path is not a crash.
        foreach (var (label, kind, target) in new[]
                 {
                     ("protocol degrades", ItemKinds.Protocol, "unknown-proto:test"),
                     ("missing path degrades", ItemKinds.App, Path.Combine(root, "nope.exe")),
                     ("empty target degrades", ItemKinds.File, ""),
                 })
        {
            var item = new CatalogItem { Id = label, Name = label, Kind = kind, Target = target };
            var bmp = IconService.GetAsync(item).GetAwaiter().GetResult();
            Check(label, bmp is null, bmp is null ? "no icon" : "unexpected icon");
        }

        var pngs = Directory.Exists(paths.IconsDirectory)
            ? Directory.GetFiles(paths.IconsDirectory, "*.png") : Array.Empty<string>();
        Check("cache wrote PNGs", pngs.Length > 0, pngs.Length + " file(s)");
        if (pngs.Length > 0)
        {
            var smallest = pngs.Min(p => new FileInfo(p).Length);
            Check("cached PNGs are real images", smallest > 300, "smallest " + smallest + " bytes");

            var cachedItem = new CatalogItem
            {
                Id = "c", Name = "cached", Kind = ItemKinds.App,
                Target = Path.Combine(sys, "notepad.exe")
            };
            var again = IconService.GetAsync(cachedItem).GetAwaiter().GetResult();
            Check("cache round trip", again is not null, again is null ? "null" : "ok");
        }

        try { Directory.Delete(root, true); } catch { }
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
