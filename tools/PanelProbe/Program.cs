using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;
using Galah2.ViewModels;
using Galah2.Views;

/// <summary>
/// Panel probes: the command surface 1.x had, exercised against a real catalog document.
/// A scripted host stands in for the shell so every command can be driven without a desktop.
/// </summary>
internal static class Program
{
    private static int _fail;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    /// <summary>Records what the panel asked for and answers prompts with canned values.</summary>
    private sealed class FakeHost : IPanelHost
    {
        public bool HideCalled, MaintenanceCalled, SettingsCalled, ExitRequested;
        public string? LastPrompt;
        public string? PromptAnswer = "答案";
        public bool ConfirmAnswer = true;
        public bool LaunchResult = true;
        public int LaunchCount;
        public string? AddPickerTab;
        public CatalogItem? EditResult;

        public void HidePanel() => HideCalled = true;
        public void ShowAddPicker(string tabId) => AddPickerTab = tabId;
        public CatalogItem? ShowEditItem(string tabId, CatalogItem? existing) => EditResult;
        // The probe answers immediately: it is testing what the panel does WITH the answer, not
        // how a real window collects it (DialogProbe covers that).
        public void Prompt(string title, string initial, Action<string?> onAnswer)
        {
            LastPrompt = title;
            onAnswer(PromptAnswer);
        }

        public void Confirm(string title, string message, Action<bool> onAnswer) =>
            onAnswer(ConfirmAnswer);
        public void ShowMaintenance() => MaintenanceCalled = true;
        public void ShowSettings() => SettingsCalled = true;
        public bool Launch(CatalogItem item) { LaunchCount++; return LaunchResult; }

        // Pickers are window capabilities; the probe answers with canned paths.
        public string? PickFileAnswer { get; set; }
        public string? PickFolderAnswer { get; set; }
        public Task<string?> PickFile(string title) => Task.FromResult(PickFileAnswer);
        public Task<string?> PickFolder(string title) => Task.FromResult(PickFolderAnswer);
    }

    private static string _root = "";

    [STAThread]
    public static int Main(string[] args)
    {
        AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }

    public static void RunChecks()
    {
        _root = Path.Combine(Path.GetTempPath(), "panelprobe-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new AppDataPaths(_root).EnsureCreated();
        IconService.Initialize(paths);

        var store = new CatalogStore(paths);
        store.Load();

        var tab = store.AddTab("测试分类");
        store.AddItems(tab.Id, new[]
        {
            new CatalogItem { Id = "a", Name = "A 项", Kind = ItemKinds.App, Target = "C:/Windows/System32/calc.exe" },
            new CatalogItem { Id = "b", Name = "B 项", Kind = ItemKinds.App, Target = "C:/Windows/System32/notepad.exe" },
        });

        var host = new FakeHost();
        var vm = new PanelViewModel(store, host, new ToastService());

        // ---- projection ----
        Check("tabs projected", vm.Tabs.Count >= 2, vm.Tabs.Count + " tab(s)");

        // Select the tab the items went into: the panel opens on the first tab, which here is the
        // empty default one created by a fresh store.
        var testTabIndex = vm.Tabs.ToList().FindIndex(t => t.Name == "测试分类");
        vm.SelectTabByIndex(testTabIndex + 1);
        Check("items projected", vm.Items.Count == 2, vm.Items.Count + " item(s)");
        Check("first tab active", vm.Tabs.Any(t => t.IsActive));
        Check("active tab name shown", vm.ActiveTabName.Length > 0, vm.ActiveTabName);


        // Wheel navigation must move one step and be reversible, whichever tab happens to be
        // active (it wraps, so the starting point decides which neighbour comes next).
        var startId = vm.Tabs.First(t => t.IsActive).Id;
        vm.SwitchTabRelative(1);
        var afterForward = vm.Tabs.First(t => t.IsActive).Id;
        Check("wheel moves to next tab", afterForward != startId, afterForward);

        vm.SwitchTabRelative(-1);
        Check("wheel goes back", vm.Tabs.First(t => t.IsActive).Id == startId);
        vm.SelectTabByIndex(2);
        Check("ctrl+2 selects second tab", vm.Tabs[1].IsActive, vm.ActiveTabName);
        Check("ctrl+99 ignored", !vm.SelectTabByIndex(99));

        // ---- launch ----
        vm.SelectTabByIndex(testTabIndex + 1);
        var tile = vm.Items[0];
        vm.LaunchTile(tile);
        Check("launch reached the host", host.LaunchCount == 1, host.LaunchCount + " call(s)");
        Check("panel hid after launch", host.HideCalled);

        vm.ApplySettings(new SettingsDocument { HideOnLaunch = false });
        host.HideCalled = false;
        vm.LaunchTile(vm.Items[0]);
        Check("hide-on-launch off keeps panel", !host.HideCalled);

        host.LaunchResult = false;
        vm.LaunchTile(vm.Items[0]);
        // Launch failures are news, not a standing problem: they belong in the toast, and the
        // save banner stays reserved for "your library could not be written".
        Check("failed launch reports through the toast", vm.Toast.IsOpen, vm.Toast.Message);

        // ---- prompts: new tab, rename, delete ----
        host.PromptAnswer = "第二个分类";
        vm.NewTabCommand.Execute(null);
        Check("new tab created", store.Current.Tabs.Any(t => t.Name == "第二个分类"),
            store.Current.Tabs.Count + " tab(s)");

        vm.SelectTabByIndex(1);
        host.PromptAnswer = "改过的名字";
        vm.RenameTabCommand.Execute(null);
        Check("rename persisted", store.Current.Tabs.Any(t => t.Name == "改过的名字"));

        var beforeDelete = store.Current.Tabs.Count;
        host.ConfirmAnswer = true;
        vm.DeleteTabCommand.Execute(null);
        Check("delete removed a tab", store.Current.Tabs.Count == beforeDelete - 1,
            store.Current.Tabs.Count + " left");

        // ---- confirm is honoured: saying no must not delete ----
        vm.SelectTabByIndex(1);
        host.ConfirmAnswer = false;
        var count = store.Current.Tabs.Count;
        host.PromptAnswer = "x";
        vm.DeleteTabCommand.Execute(null);
        Check("cancelled delete changed nothing", store.Current.Tabs.Count == count);

        // ---- add picker routing ----
        vm.AddPickerCommand.Execute(null);
        Check("add picker opened", host.AddPickerTab is not null, host.AddPickerTab ?? "null");

        // ---- settings and maintenance routing ----
        vm.ShowSettingsCommand.Execute(null);
        Check("settings routed", host.SettingsCalled);
        vm.ShowMaintenanceCommand.Execute(null);
        Check("maintenance routed", host.MaintenanceCalled);

        // ---- layout: vertical rail and icon size ----
        vm.ApplyLayout(new SettingsDocument { IconSize = IconSizes.Large, TabPosition = TabPositions.Left });
        Check("vertical rail enabled", vm.IsVerticalTabs);
        Check("large icon edge", vm.IconEdge == IconSizes.IconEdge(IconSizes.Large),
            vm.IconEdge.ToString());
        Check("tile widens for a big icon", vm.TileWidth > 76, vm.TileWidth.ToString());

        vm.ApplyLayout(new SettingsDocument { IconSize = IconSizes.Small, TabPosition = TabPositions.Top });
        Check("rail turned off", !vm.IsVerticalTabs);
        Check("small icon edge", vm.IconEdge == IconSizes.IconEdge(IconSizes.Small),
            vm.IconEdge.ToString());

        // ---- click mode decides whether a single click acts ----
        vm.ApplySettings(new SettingsDocument { ClickMode = ClickModes.Double, HideOnLaunch = false });
        var clickedTile = vm.Items[0];
        var launchedBefore = host.LaunchCount;
        vm.LaunchTile(clickedTile, clickCount: 1);
        Check("single click selects in double mode", host.LaunchCount == launchedBefore,
            host.LaunchCount + " launch(es)");
        vm.LaunchTile(clickedTile, clickCount: 2);
        Check("double click launches in double mode", host.LaunchCount == launchedBefore + 1);

        vm.ApplySettings(new SettingsDocument { ClickMode = ClickModes.Single, HideOnLaunch = false });
        launchedBefore = host.LaunchCount;
        vm.LaunchTile(clickedTile, clickCount: 1);
        Check("single click launches in single mode", host.LaunchCount == launchedBefore + 1);

        // ---- material and blur flags reach the view model ----
        vm.ApplySettings(new SettingsDocument
        {
            PanelMaterial = PanelMaterials.Translucent, HideOnBlur = true
        });
        Check("material reaches the panel", vm.PanelMaterial == PanelMaterials.Translucent,
            vm.PanelMaterial);
        Check("hide-on-blur reaches the panel", vm.HideOnBlur);

        // ---- window construction ----
        var window = new PanelWindow { DataContext = vm };
        window.Show();
        Check("panel window opens", window.IsVisible);
        var size = window.Bounds.Size;
        Check("panel has layout", size.Width > 100 && size.Height > 100,
            $"{size.Width}x{size.Height}");
        window.Close();

        // ---- edit dialog keeps the commit gate ----
        var editVm = new EditItemViewModel(store, tab.Id, null, host, paths);
        editVm.Name = "";
        editVm.TargetText = "";
        editVm.SaveCommand.Execute(null);
        Check("empty draft rejected", editVm.Outcome == EditOutcome.Rejected, editVm.ErrorText);
        Check("rejected draft wrote nothing", store.Current.FindTab(tab.Id)!.Items.Count == 2);

        editVm.Name = "新条目";
        editVm.TargetText = "C:/Windows/System32/calc.exe";
        var itemsBefore = store.Current.FindTab(tab.Id)!.Items.Count;
        editVm.SaveCommand.Execute(null);
        Check("valid draft committed", editVm.Outcome == EditOutcome.Committed);
        Check("store grew by one", store.Current.FindTab(tab.Id)!.Items.Count == itemsBefore + 1);

        // ---- drop and paste ----
        vm.SelectTabByIndex(testTabIndex + 1);
        var dropCount = vm.Items.Count;
        var tempFile = Path.Combine(_root, "dropped.txt");
        File.WriteAllText(tempFile, "x");

        var added = vm.HandleDrop(new[] { tempFile });
        Check("file drop adds an item", added == 1, added + " added");
        Check("store grew after drop", vm.Items.Count == dropCount + 1,
            vm.Items.Count + " item(s)");

        var skipped = vm.HandleDrop(new[] { Path.Combine(_root, "ghost.exe") });
        Check("unrecognised drop adds nothing", skipped == 0,
            vm.HasSaveProblem ? vm.SaveProblem : "no banner");

        Check("pasted url accepted", vm.HandlePasteText("https://example.com/a"));
        Check("pasted junk rejected", !vm.HandlePasteText("not a url"));

        // ---- toast is transient news, not a standing banner ----
        var toast = new ToastService();
        vm.Toast.Show("hello");
        Check("toast opens", vm.Toast.IsOpen, vm.Toast.Message);
        vm.Toast.Close();
        Check("toast closes", !vm.Toast.IsOpen);

        // A failed launch goes to the toast, not the save banner: the library was not touched.
        vm.ApplySettings(new SettingsDocument { HideOnLaunch = false });
        host.LaunchResult = false;
        vm.LaunchTile(vm.Items[0]);
        Check("launch failure uses the toast", vm.Toast.IsOpen, vm.Toast.Message);
        Check("launch failure does not raise a save banner", !vm.HasSaveProblem);
        host.LaunchResult = true;

        // ---- favicon constraints: the "no network unless asked" rule lives in these ----
        var notHttp = FaviconService.FetchAsync("ftp://example.com", "id", paths).GetAwaiter().GetResult();
        Check("favicon refuses a non-http target", !notHttp.Ok, notHttp.Message);
        Check("favicon has a byte cap", FaviconService.MaxBytes == 512 * 1024,
            FaviconService.MaxBytes + " bytes");
        Check("favicon has a time budget", FaviconService.Timeout.TotalSeconds <= 5,
            FaviconService.Timeout.TotalSeconds + "s");

        // ---- maintenance preview is read-only until acted on ----
        var maintenanceVm = new MaintenanceViewModel(store, new SettingsStore(paths), paths);
        Check("maintenance scanned", maintenanceVm.Summary.Length > 0, maintenanceVm.Summary);
        var snapshot = File.ReadAllText(paths.CatalogFile);
        maintenanceVm.Rescan();
        Check("rescan wrote nothing", File.ReadAllText(paths.CatalogFile) == snapshot);

        // ---- export then import round trip ----
        var exported = MigrationService.Export(paths, store.Current,
            new SettingsStore(paths).Current);
        Check("export produced a file", File.Exists(exported), exported);

        var bundle = MigrationService.ReadBundle(exported);
        Check("bundle reads back", bundle.Catalog.Tabs.Count == store.Current.Tabs.Count,
            bundle.Catalog.Tabs.Count + " tab(s)");

        // A foreign file must be refused whole, not half-applied.
        var foreign = Path.Combine(_root, "foreign.json");
        File.WriteAllText(foreign, "{ this is not a galah bundle }");
        var refused = false;
        try { MigrationService.ReadBundle(foreign); }
        catch (MigrationException) { refused = true; }
        Check("foreign file refused", refused);

        var missing = false;
        try { MigrationService.ReadBundle(Path.Combine(_root, "ghost.json")); }
        catch (MigrationException) { missing = true; }
        Check("missing file refused", missing);

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
