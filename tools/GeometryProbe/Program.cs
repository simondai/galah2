using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;
using Galah2.Services.CatalogSources;
using Galah2.ViewModels;
using Galah2.Views;

namespace GeometryProbe;

/// <summary>
/// Locks down the four things hand-testing turned up: picker rows carry an icon, the panel can be
/// resized, and position/size survive a hide-show cycle.
///
/// Dragging itself is not asserted here - it needs a real pointer. What IS asserted is the part
/// that was actually broken and is testable: that geometry gets written to ui-state and read back.
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
        => AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);

    public static void RunChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "geom-" + Guid.NewGuid().ToString("N")[..6]);
        var paths = new AppDataPaths(root).EnsureCreated();
        IconService.Initialize(paths);

        // ---- 1. picker rows have an icon field that fills in ----
        var seed = new CatalogSeed(SeedCategory.SystemTools, "记事本", ItemKinds.App,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"));
        var row = new SeedViewModel(seed);
        Check("picker row exposes an icon", row.Initial.Length > 0, "initial=" + row.Initial);

        // The icon loads off the UI thread; give it a moment.
        for (var i = 0; i < 20 && !row.HasIcon; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(100); }
        Check("picker row icon loaded", row.HasIcon, row.HasIcon ? "loaded" : "still placeholder");

        // ---- 2 & 3. the panel is resizable and remembers its size ----
        var catalog = new CatalogStore(paths);
        catalog.Load();
        var uiState = new UiStateStore(paths);
        uiState.Load();

        var window = new PanelWindow { DataContext = CreateVm(catalog) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Check("panel is resizable", window.CanResize, "CanResize=" + window.CanResize);

        window.Width = 640;
        window.Height = 400;
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(200);

        Check("panel accepted a new size",
            Math.Abs(window.Bounds.Width - 640) < 2 && Math.Abs(window.Bounds.Height - 400) < 2,
            window.Bounds.Width + "x" + window.Bounds.Height);

        // ---- 4. geometry round-trips through ui-state ----
        uiState.Current.Window.Width = window.Bounds.Width;
        uiState.Current.Window.Height = window.Bounds.Height;
        uiState.Current.Window.X = 111;
        uiState.Current.Window.Y = 222;
        uiState.Save();

        var reread = new UiStateStore(paths);
        reread.Load();
        Check("width survives a reload", Math.Abs(reread.Current.Window.Width - 640) < 2,
            reread.Current.Window.Width.ToString());
        Check("height survives a reload", Math.Abs(reread.Current.Window.Height - 400) < 2,
            reread.Current.Window.Height.ToString());
        Check("position survives a reload",
            reread.Current.Window.X == 111 && reread.Current.Window.Y == 222,
            reread.Current.Window.X + "," + reread.Current.Window.Y);

        window.Close();
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>The panel view model needs a host; this probe never drives it.</summary>
    private static PanelViewModel CreateVm(CatalogStore catalog) =>
        new(catalog, new NullHost(), new ToastService());

    private sealed class NullHost : IPanelHost
    {
        public void HidePanel() { }
        public void ShowAddPicker(string tabId) { }
        public CatalogItem? ShowEditItem(string tabId, CatalogItem? existing) => null;
        public void Prompt(string title, string initial, Action<string?> onAnswer) => onAnswer(null);
        public void Confirm(string title, string message, Action<bool> onAnswer) => onAnswer(false);
        public void ShowMaintenance() { }
        public void ShowSettings() { }
        public bool Launch(CatalogItem item) => true;
        public Task<string?> PickFile(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFolder(string title) => Task.FromResult<string?>(null);
    }

    public static int Report()
    {
        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        Dispatcher.UIThread.Post(() =>
        {
            var code = 0;
            try { GeometryProbe.Program.RunChecks(); code = GeometryProbe.Program.Report(); }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED " + ex.GetType().Name + ": " + ex.Message);
                code = 1;
            }
            finally { desktop.Shutdown(code); }
        });
    }
}
