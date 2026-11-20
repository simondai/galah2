using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;
using Galah2.ViewModels;
using Galah2.Views;

namespace MaterialProbe;

/// <summary>
/// The two material options must actually look different.
///
/// Screenshot pixel comparison is unreliable on this machine (the RDP session returns black), so
/// this asserts what the renderer would be given: the window's transparency level AND the root
/// border's fill, for each setting. The original bug was that the fill never changed - the window
/// level did, so "不透明" still let the desktop through.
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
        // Use the product Application so Tokens.axaml is loaded exactly as it is in the real app.
        var app = AppBuilder.Configure<Galah2.App>().UsePlatformDetect().SetupWithoutStarting();
        var code = 0;
        try { RunChecks(); code = Report(); }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED " + ex.GetType().Name + ": " + ex.Message);
            code = 1;
        }
        return code;
    }

    public static void RunChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "mat-" + Guid.NewGuid().ToString("N")[..6]);
        var paths = new AppDataPaths(root).EnsureCreated();
        IconService.Initialize(paths);

        var catalog = new CatalogStore(paths);
        catalog.Load();

        var vm = new PanelViewModel(catalog, new NullHost(), new ToastService());
        var window = new PanelWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }

        var settings = new SettingsDocument();
        var alpha = new Dictionary<string, byte>();
        var levels = new Dictionary<string, string>();

        foreach (var material in PanelMaterials.All)
        {
            settings.PanelMaterial = material;
            vm.ApplySettings(settings);
            window.ApplyMaterial();
            Dispatcher.UIThread.RunJobs();

            var border = window.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(b => b.Name == "RootBorder");
            if (border?.Background is not ISolidColorBrush fill)
            {
                Check("fill readable for " + material, false, "no solid brush");
                continue;
            }

            var level = window.TransparencyLevelHint is { Count: > 0 } hint ? hint[0].ToString() : "unset";
            levels[material] = level;
            alpha[material] = fill.Color.A;

            Console.WriteLine("      " + material + ": level=" + level + "  fill=#" + fill.Color.A.ToString("X2")
                + fill.Color.R.ToString("X2") + fill.Color.G.ToString("X2") + fill.Color.B.ToString("X2"));
        }

        // Opaque means opaque: the fill has to be fully solid, or the desktop shows through it.
        Check("不透明 fill is fully opaque", alpha.GetValueOrDefault(PanelMaterials.None) == 0xFF,
            "alpha=" + alpha.GetValueOrDefault(PanelMaterials.None));

        // Translucent means the backdrop participates, so the fill must let some through.
        Check("半透明 fill lets the backdrop through", alpha.GetValueOrDefault(PanelMaterials.Translucent) < 0xFF,
            "alpha=" + alpha.GetValueOrDefault(PanelMaterials.Translucent));

        // The rounded corners live on the alpha channel, so BOTH settings keep the window level at
        // Transparent; Mica was tried and reverted - the compositor reports it but renders it
        // nearly opaque, which is exactly the "two identical settings" bug this probe exists for.
        Check("window level is Transparent for both settings",
            levels.GetValueOrDefault(PanelMaterials.None) == "Transparent"
            && levels.GetValueOrDefault(PanelMaterials.Translucent) == "Transparent",
            levels.GetValueOrDefault(PanelMaterials.None) + "/" + levels.GetValueOrDefault(PanelMaterials.Translucent));

        // The whole point: the two must not render the same.
        Check("the two materials differ",
            alpha.GetValueOrDefault(PanelMaterials.None) != alpha.GetValueOrDefault(PanelMaterials.Translucent),
            alpha.GetValueOrDefault(PanelMaterials.None) + " vs " + alpha.GetValueOrDefault(PanelMaterials.Translucent));

        window.Close();
        try { Directory.Delete(root, true); } catch { }
    }

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


