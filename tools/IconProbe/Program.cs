using System.Runtime.InteropServices;
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

        // Degradation: a protocol target is not a file, and a missing path is not a crash.
        foreach (var (label, kind, target) in new[]
                 {
                     ("protocol degrades", ItemKinds.Protocol, "ms-settings:"),
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

            // A second call must be served from the cache and still produce a bitmap.
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
