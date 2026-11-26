using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        public bool HideCalled, MaintenanceCalled, SettingsCalled, RandPassCalled, ExitRequested;
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
        public void ShowRandPass() => RandPassCalled = true;
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

        // ---- the add placeholder is opt-in (2026-11-23 setting, default OFF) ----
        Check("add placeholder is OFF by default",
            vm.Items.Count == 2 && vm.Items.All(i => !i.IsAddTile), vm.Items.Count + " cell(s)");
        vm.ApplySettings(new SettingsDocument { ShowAddTile = true });
        Check("switching ON adds the placeholder live",
            vm.Items.Count == 3 && vm.Items.Last().IsAddTile, vm.Items.Count + " cell(s)");

        // Items is the display projection; with the setting on it ends with the placeholder, so
        // the entry count is asserted over the real items, not the raw list length.
        Check("items projected", vm.Items.Count(i => !i.IsAddTile) == 2,
            vm.Items.Count(i => !i.IsAddTile) + " item(s)");
        Check("placeholder ends the grid", vm.Items.Last().IsAddTile);
        Check("first tab active", vm.Tabs.Any(t => t.IsActive));
        Check("active tab name shown", vm.ActiveTabName.Length > 0, vm.ActiveTabName);

        // ---- chip context menu: rename + delete (delete only while empty, user rule) ----
        // Operates on a throwaway tab: the rest of the probe assumes the layout
        // [默认, 测试分类] (the prompt block below selects by ABSOLUTE index 1).
        host.PromptAnswer = "探针空分类";
        vm.NewTabCommand.Execute(null);
        var emptyChip = vm.Tabs.First(t => t.Name == "探针空分类");
        var fullChip = vm.Tabs.First(t => t.Name == "测试分类");
        Check("chip knows whether its category is empty",
            emptyChip.HasNoEntries && !fullChip.HasNoEntries);
        Check("every chip carries its own rename/delete commands",
            vm.Tabs.All(t => t.RenameCommand is not null && t.DeleteCommand is not null));

        host.PromptAnswer = "改过名";
        emptyChip.RenameCommand.Execute(null);
        Check("chip menu renames the chip's own category",
            host.LastPrompt == "重命名分类" && vm.Tabs.Any(t => t.Name == "改过名")
            && vm.Tabs.Count == 3,
            host.LastPrompt ?? "no prompt");

        // Refresh rebuilt the TabViewModels; re-fetch by name.
        vm.Tabs.First(t => t.Name == "改过名").DeleteCommand.Execute(null);
        Check("chip menu deletes an empty category",
            !vm.Tabs.Any(t => t.Name == "改过名") && vm.Tabs.Count == 2,
            vm.Tabs.Count + " tab(s)");

        vm.Tabs.First(t => t.Name == "测试分类").DeleteCommand.Execute(null);
        Check("chip menu refuses to delete a non-empty category",
            vm.Toast.IsOpen && vm.Tabs.Count == 2, vm.Toast.Message);
        vm.Toast.Close();
        host.PromptAnswer = "答案"; // restore the probe default for the blocks below


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
        // By NAME, not the stale testTabIndex: the chip-menu block above deleted 默认,
        // which shifted every tab one slot to the left.
        vm.SelectTabByIndex(vm.Tabs.ToList().FindIndex(t => t.Name == "测试分类") + 1);
        var tile = vm.Items[0];
        vm.LaunchTile(tile);
        Check("launch reached the host", host.LaunchCount == 1, host.LaunchCount + " call(s)");
        Check("panel hid after launch", host.HideCalled);

        vm.ApplySettings(new SettingsDocument { HideOnLaunch = false, ShowAddTile = true });
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

        // ---- the add-entry placeholder tile (2026-11-23 entry points) ----
        // It is a cell of the LIST VIEW, not a library entry: it must never launch, never be
        // counted as content, and never reach the catalog document.
        var launchesBefore = host.LaunchCount;
        var placeholder = vm.Items.Last();
        Check("placeholder is last cell", placeholder.IsAddTile);
        Check("placeholder launch is refused", !vm.LaunchTile(placeholder) && host.LaunchCount == launchesBefore);
        Check("placeholder carries no library item", placeholder.Item is null);

        // An empty category still gets exactly the placeholder: the add flow must not need ☰.
        host.PromptAnswer = "空分类探针";
        vm.NewTabCommand.Execute(null);
        var emptyIndex = vm.Tabs.ToList().FindIndex(t => t.Name == "空分类探针");
        vm.SelectTabByIndex(emptyIndex + 1);
        Check("empty category shows only the placeholder",
            vm.Items.Count == 1 && vm.Items[0].IsAddTile, vm.Items.Count + " cell(s)");

        // And the placeholder is projection-only: the document itself has zero entries there.
        var probeEmptyTab = store.Current.Tabs.FirstOrDefault(t => t.Name == "空分类探针");
        Check("placeholder never reaches the catalog",
            probeEmptyTab is not null && probeEmptyTab.Items.Count == 0,
            probeEmptyTab?.Items.Count.ToString() ?? "tab missing");

        // ---- drag reorder: chips onto chips, tiles onto tiles (2026-11-23) ----
        vm.SelectTabByIndex(vm.Tabs.ToList().FindIndex(t => t.Name == "测试分类") + 1);

        // The store refuses the sentinel by id: it can never become a data entry.
        Check("sentinel cannot be reordered in the store",
            !store.MoveItemToIndex("add-tile", 0));

        // Tile onto tile: last item takes the first slot, orders stay 0..n-1, one write.
        var names0 = vm.Items.Where(i => !i.IsAddTile).Select(i => i.Name).ToList();
        var lastId = names0.Count > 1 ? vm.Items[^2].Id : null;   // last real item
        if (lastId is not null)
        {
            vm.MoveItemToIndex(lastId, 0);
            var names1 = vm.Items.Where(i => !i.IsAddTile).Select(i => i.Name).ToList();
            Check("tile drag moves to the target slot",
                names1[0] == names0[^1] && names1.Count == names0.Count,
                string.Join(",", names1));
            Check("orders renumber densely",
                store.Current.ItemsInOrder(store.Current.FindTab(vm.SelectedTab!.Id)!)
                    .Select((it, k) => it.Order == k).All(ok => ok));
            Check("placeholder still ends the grid after a reorder", vm.Items.Last().IsAddTile);

            // Move it back so later blocks see the original order.
            vm.MoveItemToIndex(lastId, names0.Count - 1);
        }

        // Chip onto chip: the target IS the slot.
        var tabNames0 = vm.Tabs.Select(t => t.Name).ToList();
        var tailTab = vm.Tabs.Last();
        vm.MoveTabToIndex(tailTab.Id, 0);
        var tabNames1 = vm.Tabs.Select(t => t.Name).ToList();
        Check("chip drag moves to the target slot",
            tabNames1[0] == tabNames0[^1] && tabNames1.Count == tabNames0.Count,
            string.Join(",", tabNames1));
        Check("tab orders renumber densely",
            store.Current.TabsInOrder().Select((t, k) => t.Order == k).All(ok => ok));
        vm.MoveTabToIndex(tailTab.Id, tabNames0.Count - 1);   // restore
        Check("chip drag restores", vm.Tabs.Select(t => t.Name).ToList().SequenceEqual(tabNames0));

        // A no-op move writes nothing (same index).
        var before = File.ReadAllText(paths.CatalogFile);
        vm.MoveTabToIndex(vm.Tabs[1].Id, 1);
        Check("no-op tab move writes nothing", File.ReadAllText(paths.CatalogFile) == before);

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

        // ---- category chip label size (2026-11-23 setting, three steps, no free-form) ----
        Check("chip font defaults to the shipped 12 DIP", vm.ChipFontSize == ChipFontSizes.FontSize(ChipFontSizes.Medium),
            vm.ChipFontSize.ToString());
        vm.ApplyLayout(new SettingsDocument { TabFontSize = ChipFontSizes.Large });
        Check("large chip font step", vm.ChipFontSize == 14, vm.ChipFontSize.ToString());
        vm.ApplyLayout(new SettingsDocument { TabFontSize = ChipFontSizes.XLarge });
        Check("extra-large chip font step", vm.ChipFontSize == 16, vm.ChipFontSize.ToString());
        vm.ApplyLayout(new SettingsDocument { TabFontSize = ChipFontSizes.Small });
        Check("small chip font step", vm.ChipFontSize == 11, vm.ChipFontSize.ToString());
        vm.ApplyLayout(new SettingsDocument());
        Check("chip font back to default", vm.ChipFontSize == 12, vm.ChipFontSize.ToString());

        // ---- click mode decides whether a single click acts ----
        // Back to the tab that has items - by NAME, because creating and deleting categories in
        // the blocks above shifted the indexes that testTabIndex was captured from.
        vm.SelectTabByIndex(vm.Tabs.ToList().FindIndex(t => t.Name == "测试分类") + 1);
        vm.ApplySettings(new SettingsDocument { ClickMode = ClickModes.Double, HideOnLaunch = false, ShowAddTile = true });
        var clickedTile = vm.Items[0];
        var launchedBefore = host.LaunchCount;
        vm.LaunchTile(clickedTile, clickCount: 1);
        Check("single click selects in double mode", host.LaunchCount == launchedBefore,
            host.LaunchCount + " launch(es)");
        vm.LaunchTile(clickedTile, clickCount: 2);
        Check("double click launches in double mode", host.LaunchCount == launchedBefore + 1);

        vm.ApplySettings(new SettingsDocument { ClickMode = ClickModes.Single, HideOnLaunch = false, ShowAddTile = true });
        launchedBefore = host.LaunchCount;
        vm.LaunchTile(clickedTile, clickCount: 1);
        Check("single click launches in single mode", host.LaunchCount == launchedBefore + 1);

        // Regression: Click and DoubleTapped both hit the same Button. In single-click mode the
        // second (double-tap) event must not launch the item a second time.
        launchedBefore = host.LaunchCount;
        vm.LaunchTile(clickedTile, clickCount: 2);
        Check("double tap does not launch again in single mode", host.LaunchCount == launchedBefore,
            host.LaunchCount + " launch(es)");

        // A non-gesture entry point (context-menu "Open") must launch in either click mode.
        launchedBefore = host.LaunchCount;
        vm.LaunchTile(clickedTile);
        Check("context-menu Open launches regardless of click mode", host.LaunchCount == launchedBefore + 1);

        // ---- material and blur flags reach the view model ----
        vm.ApplySettings(new SettingsDocument
        {
            PanelMaterial = PanelMaterials.Translucent, HideOnBlur = true, ShowAddTile = true
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

        // ---- RandPass entry points in the real window (spec 2026-11-24-randpass) ----
        // Entry 1: the title-bar keyhole button (a vector Path, bound to ShowRandPassCommand).
        // Entry 2: the RandPass row in the ☰ menu flyout. (Entry 3 - the tray menu - is asserted
        // in DialogProbe, where the product App provides the native tray.)
        var keyhole = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => ReferenceEquals(b.Command, vm.ShowRandPassCommand));
        // The keyhole is authored as a vector Path as the button's Content - a Path (not a font
        // glyph) so it renders identically on every machine and themes with the text brushes.
        Check("title bar carries the keyhole RandPass button (a vector Path)",
            keyhole is not null && keyhole.Content is Avalonia.Controls.Shapes.Path);
        // Invoking the command must reach the host (the real AppRuntime opens/activates the window).
        host.RandPassCalled = false;
        keyhole?.Command.Execute(null);
        Check("keyhole command asks the host to open RandPass", host.RandPassCalled);

        // The ☰ flyout's items live outside the logical tree, so their Command bindings only
        // resolve when the flyout is presented (IsOpen is read-only in-process). Assert the row
        // exists and that its Command binding is authored to the same property the siblings use -
        // the identical pattern the five working rows above it rely on.
        var menuButton = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Content as string == "☰");
        var flyout = menuButton?.Flyout as Avalonia.Controls.MenuFlyout;
        // A flyout is an AvaloniaObject, not a Control: its items only get a DataContext (and
        // their Command bindings only resolve) when the OS presents the flyout, which a probe
        // cannot trigger. So assert differentially: the RandPass row's command binding must be in
        // exactly the same state as a sibling row users actually click (维护与迁移…) - if it were
        // broken it would diverge. The keyhole check above already proves ShowRandPassCommand
        // itself works end-to-end.
        var randPassRow = flyout?.Items.OfType<Avalonia.Controls.MenuItem>()
            .FirstOrDefault(mi => mi.Header as string == "RandPass");
        var maintRow = flyout?.Items.OfType<Avalonia.Controls.MenuItem>()
            .FirstOrDefault(mi => mi.Header as string == "维护与迁移…");
        var rpCmd = randPassRow?.Command;
        var maintCmd = maintRow?.Command;
        var rpUnresolved = rpCmd is null;
        var maintUnresolved = maintCmd is null;
        Check("☰ menu RandPass row matches its working sibling's binding state",
            randPassRow is not null && maintRow is not null && rpUnresolved == maintUnresolved,
            $"randPass={(rpCmd == null ? "unresolved" : "resolved")}"
            + $" maint={(maintCmd == null ? "unresolved" : "resolved")}");
        if (rpCmd is not null)
            Check("resolved RandPass command is ShowRandPassCommand",
                ReferenceEquals(rpCmd, vm.ShowRandPassCommand));

        // ---- the two add-entry points exist in the real window (2026-11-23) ----
        // The grid containers are generated by the ItemsControl during a real layout/render
        // pass, which RunJobs alone does not pump - wait the same way GeometryProbe waits.
        List<Button> tileButtons = new();
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            tileButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("tile")).ToList();
            if (tileButtons.Count > 0) break;
        }
        // The template is one root with two mutually exclusive buttons (real tile / placeholder),
        // so every container holds both and IsVisible picks one. Count the VISIBLE ones.
        var visibleTiles = tileButtons.Where(b => b.IsVisible).ToList();
        var addTiles = visibleTiles.Where(b => b.Classes.Contains("addtile")).ToList();
        Check("every cell shows exactly one button",
            visibleTiles.Count == vm.Items.Count,
            visibleTiles.Count + " visible of " + tileButtons.Count + " buttons, " +
            vm.Items.Count + " cells");
        Check("placeholder tile is realised in the grid", addTiles.Count == 1,
            addTiles.Count + " add-tile(s)");
        Check("placeholder tile is the last cell",
            addTiles.Count == 1 && ReferenceEquals(visibleTiles.Last(), addTiles[0]));
        // The placeholder's menu must be a ContextMenu, not a MenuFlyout: flyout items are not
        // in the logical tree, $parent[ItemsControl] fails there, and MenuItem greys the row
        // out on the binding error (Avalonia MenuItem.cs) - the user's grey-menu screenshot.
        // Command RESOLUTION cannot be asserted in a probe: opening a menu without a real
        // display never establishes the popup's logical chain, so even the shipped, hand-
        // verified tile menus read Command=null here (measured 2026-11-23: both NULL). The
        // structural fact that IS assertable is the host type and the four rows; resolution
        // follows from using the identical pattern as the real tiles, and acceptance is the
        // user's manual click.
        var addMenu = addTiles.Count == 1 ? addTiles[0].ContextMenu : null;
        Check("placeholder tile menu is a ContextMenu with the 4 add rows",
            addMenu is not null && addMenu.Items.Count == 4
            && addMenu.Items.OfType<MenuItem>().Select(mi => mi.Header as string).SequenceEqual(
                new[] { "添加快捷方式…", "添加文件…", "添加文件夹…", "添加网址…" })
            && addTiles[0].Flyout is null,
            addMenu is null ? "no context menu" : addMenu.Items.Count + " row(s)");
        Check("placeholder tile accepts a drop (move to end)",
            addTiles.Count == 1 && DragDrop.GetAllowDrop(addTiles[0]));
        Check("real tiles keep their own context menu",
            visibleTiles.Where(b => !b.Classes.Contains("addtile"))
                .All(b => b.ContextMenu is not null && b.ContextMenu.Items.Count == 7));

        // Entry point 1: the blank-space right-click menu hangs on the scroll container.
        var scroll = window.FindControl<ScrollViewer>("TileScroll");
        Check("blank-space context menu present",
            scroll?.ContextMenu is not null && scroll.ContextMenu.Items.Count == 4,
            scroll?.ContextMenu is null ? "no context menu" : scroll.ContextMenu.Items.Count + " item(s)");

        // Switching the setting OFF removes the placeholder from the live grid, and the
        // blank-space menu survives it (it is an independent entry point).
        vm.ApplySettings(new SettingsDocument { ShowAddTile = false });
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            if (!window.GetVisualDescendants().OfType<Button>()
                    .Any(b => b.Classes.Contains("addtile") && b.IsVisible)) break;
        }
        Check("switching OFF removes the placeholder from the grid",
            !window.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Classes.Contains("addtile") && b.IsVisible));
        Check("blank-space menu survives the switch",
            scroll?.ContextMenu is not null && scroll.ContextMenu.Items.Count == 4);

        // ---- the chip label font size follows the setting in the live window ----
        var tabChip = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("chip") && b.DataContext is TabViewModel);
        Check("category chip renders at the default 12 DIP", tabChip is not null && tabChip.FontSize == 12,
            tabChip is null ? "no chip" : tabChip.FontSize.ToString());
        vm.ApplyLayout(new SettingsDocument { TabFontSize = ChipFontSizes.Large });
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            if (tabChip is null || tabChip.FontSize == 14) break;
        }
        Check("chip font size follows the setting live", tabChip?.FontSize == 14,
            tabChip is null ? "no chip" : tabChip.FontSize.ToString());

        // The chip's right-click menu: two rows (rename / delete-while-empty) on EVERY chip,
        // both orientations. Same host-type lock as the placeholder menu: in-process a popup's
        // command resolution is not assertable, so we lock the structure (ContextMenu + rows)
        // and the behaviour through the command objects; acceptance of the real click is manual.
        var realizedChips = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("chip") && b.DataContext is TabViewModel).ToList();
        Check("every category chip carries the 2-row right-click menu",
            realizedChips.Count >= 2 && realizedChips.All(b =>
            {
                var m = b.ContextMenu;
                return m is not null && m.Items.OfType<MenuItem>().Select(mi => mi.Header as string)
                    .SequenceEqual(new[] { "重命名分类", "删除分类" });
            }),
            realizedChips.Count + " chip(s)");

        // ---- REAL menu open: the 2026-11-24 blind spot ----
        // The probe runs on a real desktop with a real window, so each ContextMenu can be
        // genuinely opened and its realized rows inspected. This is the one thing structural
        // checks cannot see: whether the rows' Command bindings survive the open. A failure
        // here greys every row in the shipped app while every structural check passes - the
        // user report (chip AND tile menus greyed) was exactly this blind spot. The rows now
        // bind each control's OWN commands (no $parent - dies at the popup boundary; no XAML
        // Opening swap - the handler never fires), so a plain Open() must already yield live,
        // enabled rows bound to the right object.
        bool OpenAndPump(ContextMenu m)
        {
            m.Open();
            for (var i = 0; i < 40; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(100);
                if (m.IsOpen) return true;
            }
            return false;
        }
        void CloseAndPump(ContextMenu m)
        {
            m.Close();
            for (var i = 0; i < 20; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(50);
                if (!m.IsOpen) break;
            }
        }

        // (1) chip menu - rows bind the chip's own TabViewModel commands.
        var realChip = realizedChips.First(c => c.IsVisible);
        var realMenu = realChip.ContextMenu!;
        var realTab = (TabViewModel)realChip.DataContext;
        Check("chip menu actually opens", OpenAndPump(realMenu));
        Check("chip menu DataContext is the chip's own TabViewModel",
            ReferenceEquals(realMenu.DataContext, realTab),
            realMenu.DataContext?.GetType().Name ?? "null");
        var renameRow = realMenu.Items.OfType<MenuItem>().First(m => m.Header as string == "重命名分类");
        var deleteRow = realMenu.Items.OfType<MenuItem>().First(m => m.Header as string == "删除分类");
        Check("chip rename row holds a live command after a real open",
            renameRow.Command is not null, renameRow.Command?.GetType().Name ?? "null");
        Check("chip rename row is enabled after a real open", renameRow.IsEnabled);
        var realTabCount = store.Current.Tabs.First(t => t.Id == realTab.Id).Items.Count;
        Check("chip delete row enabled state matches its entries",
            deleteRow.IsEnabled == (realTabCount == 0),
            $"entries={realTabCount}, enabled={deleteRow.IsEnabled}");
        CloseAndPump(realMenu);

        // (2) tile menu - rows bind the tile's own ItemViewModel commands.
        var realTile = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("tile") && !b.Classes.Contains("addtile")
                        && b.DataContext is ItemViewModel { IsAddTile: false });
        var tileMenu = realTile.ContextMenu!;
        Check("tile menu actually opens", OpenAndPump(tileMenu));
        var tileOpenRow = tileMenu.Items.OfType<MenuItem>().First(m => m.Header as string == "打开");
        var tileDeleteRow = tileMenu.Items.OfType<MenuItem>().First(m => m.Header as string == "删除");
        Check("tile 打开 row holds a live command after a real open",
            tileOpenRow.Command is not null, tileOpenRow.Command?.GetType().Name ?? "null");
        Check("tile 打开 row is enabled after a real open", tileOpenRow.IsEnabled);
        Check("tile 删除 row holds a live command after a real open",
            tileDeleteRow.Command is not null, tileDeleteRow.Command?.GetType().Name ?? "null");
        Check("tile menu delete row is the tile's own DeleteCommand",
            ReferenceEquals(tileDeleteRow.Command,
                ((ItemViewModel)realTile.DataContext).DeleteCommand));
        CloseAndPump(tileMenu);

        // (3) sentinel add-tile menu - rows bind the sentinel's own add commands. The
        // placeholder block above left ShowAddTile OFF; enable it, test, restore.
        vm.ApplySettings(new SettingsDocument { ShowAddTile = true });
        Button? sentinel = null;
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(100);
            sentinel = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("addtile") && b.IsVisible);
            if (sentinel is not null) break;
        }
        Check("sentinel add tile present to carry its add menu", sentinel is not null);
        if (sentinel is not null)
        {
            var sentMenu = sentinel.ContextMenu!;
            Check("sentinel add menu actually opens", OpenAndPump(sentMenu));
            Check("sentinel add rows all hold live commands after a real open",
                sentMenu.Items.OfType<MenuItem>().Count(m => m.Command is null) == 0
                && sentMenu.Items.OfType<MenuItem>().All(m => m.IsEnabled),
                sentMenu.Items.OfType<MenuItem>().Count(m => m.Command is null) + " null row(s)");
            Check("sentinel add rows bind the sentinel's own add commands",
                ReferenceEquals(sentMenu.Items.OfType<MenuItem>().First().Command,
                    ((ItemViewModel)sentinel.DataContext).AddPickerCommand));
            CloseAndPump(sentMenu);
        }
        vm.ApplySettings(new SettingsDocument { ShowAddTile = false });
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
            if (window.GetVisualDescendants().OfType<Button>()
                    .All(b => !b.Classes.Contains("addtile") || !b.IsVisible)) break;
        }

        // ---- new-category chip on the strip (1.x parity) ----
        // The + lives on the strip itself, not only in the context menu. It must be present in
        // both orientations, and clicking it must create a tab and select it (NewTab semantics).
        // What the tab-position setting toggles is the parent strip (a StackPanel bound to
        // !IsVerticalTabs / IsVerticalTabs), not the chip's own IsVisible flag.
        var chips = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("newtab")).ToList();
        Check("both strips carry the new-category chip", chips.Count == 2,
            chips.Count + " chip(s)");
        var horizontalChip = chips.FirstOrDefault();
        var hStrip = horizontalChip?.GetVisualParent<Panel>();
        Check("chip sits on the visible strip", hStrip?.IsVisible == true,
            $"strip visible={hStrip?.IsVisible}");

        var tabsBefore = vm.Tabs.Count;
        host.PromptAnswer = "圆片分类";
        horizontalChip?.Command?.Execute(horizontalChip.CommandParameter);
        Check("chip click asks for a name", host.LastPrompt == "新建分类",
            host.LastPrompt ?? "no prompt");
        Check("chip click created a tab", vm.Tabs.Count == tabsBefore + 1,
            vm.Tabs.Count + " tab(s)");
        Check("chip click selected the new tab",
            vm.Tabs.FirstOrDefault(t => t.Name == "圆片分类")?.IsActive == true);

        vm.ApplyLayout(new SettingsDocument { TabPosition = TabPositions.Left });
        var verticalChip = chips.LastOrDefault();
        var vStrip = verticalChip?.GetVisualParent<Panel>();
        Check("chip follows the vertical rail",
            hStrip?.IsVisible == false && vStrip?.IsVisible == true,
            $"hStrip={hStrip?.IsVisible} vStrip={vStrip?.IsVisible}");

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
        var dropCount = vm.Items.Count(i => !i.IsAddTile);
        var tempFile = Path.Combine(_root, "dropped.txt");
        File.WriteAllText(tempFile, "x");

        var added = vm.HandleDrop(new[] { tempFile });
        Check("file drop adds an item", added == 1, added + " added");
        Check("store grew after drop", vm.Items.Count(i => !i.IsAddTile) == dropCount + 1,
            vm.Items.Count(i => !i.IsAddTile) + " item(s)");

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

        // ---- import MERGE semantics (replace: false): merge, never delete, dedup by
        // kind+target+args, corrupt items dropped with a count, fresh ids only for new items ----
        var probeSettings = new SettingsStore(paths);
        var liveTab = store.Current.Tabs.First();
        var survivor = store.Current.Tabs.SelectMany(t => t.Items).First();
        var mergeItemsBefore = store.Current.Tabs.Sum(t => t.Items.Count);
        var idsBefore = store.Current.Tabs.SelectMany(t => t.Items.Select(i => i.Id)).ToHashSet();
        // Earlier probe cases may have added more items with the survivor's target (the edit-dialog
        // test commits a second calc.exe), so the duplicate check compares the SAME-target count
        // before vs after the merge, not against a hardcoded 1.
        var survivorTargetMatchesBefore = store.Current.Tabs.SelectMany(t => t.Items).Count(i =>
            i.Kind == survivor.Kind
            && i.Target.Trim().Equals(survivor.Target.Trim(), StringComparison.OrdinalIgnoreCase));

        var mergeBundle = new MigrationBundle
        {
            Catalog = new CatalogDocument
            {
                Tabs =
                {
                    new CatalogTab
                    {
                        Name = liveTab.Name,
                        Items =
                        {
                            // a duplicate of an existing live item (same kind+target+args): must not land twice
                            new CatalogItem { Id = "dup", Name = survivor.Name, Kind = survivor.Kind, Target = survivor.Target, Args = survivor.Args },
                            // genuinely new
                            new CatalogItem { Id = "fresh", Name = "导入新项", Kind = ItemKinds.App, Target = "C:/Windows/System32/mmc.exe" },
                            // corrupt: empty target -> Sanitize drops it
                            new CatalogItem { Id = "bad", Name = "坏项", Kind = ItemKinds.App, Target = "" },
                        }
                    },
                    new CatalogTab
                    {
                        Name = "导入探针分类",
                        Items = { new CatalogItem { Id = "newtab-item", Name = "探针项", Kind = ItemKinds.File, Target = "C:/Windows/win.ini" } }
                    },
                }
            },
            Settings = probeSettings.Current,
        };

        var outcome = MigrationService.Apply(store, probeSettings, mergeBundle, replace: false);
        Check("import merge: exactly 2 added (duplicate and corrupt not counted)",
            outcome.ItemsAdded == 2, outcome.ItemsAdded + " added");
        Check("import merge: corrupt item dropped and counted", outcome.ItemsDropped == 1,
            outcome.ItemsDropped + " dropped");
        var liveNow = store.Current;
        var survivorNow = liveNow.Tabs.SelectMany(t => t.Items).FirstOrDefault(i => i.Id == survivor.Id);
        Check("import merge: existing item survives with its own id",
            survivorNow is not null && survivorNow.Target == survivor.Target,
            survivorNow?.Target ?? "gone");
        Check("import merge: the duplicate did not land twice",
            liveNow.Tabs.SelectMany(t => t.Items).Count(i => i.Kind == survivor.Kind
                && i.Target.Trim().Equals(survivor.Target.Trim(), StringComparison.OrdinalIgnoreCase))
                == survivorTargetMatchesBefore);
        Check("import merge: new item landed in the matched tab",
            liveNow.Tabs.First(t => t.Name == liveTab.Name).Items.Any(i => i.Target == "C:/Windows/System32/mmc.exe"));
        Check("import merge: new tab created with its single item",
            liveNow.Tabs.Any(t => t.Name == "导入探针分类" && t.Items.Count == 1));
        var totalAfter = liveNow.Tabs.Sum(t => t.Items.Count);
        Check("import merge: total = before + 2 (nothing deleted)", totalAfter == mergeItemsBefore + 2,
            mergeItemsBefore + " -> " + totalAfter);
        var liveIds = liveNow.Tabs.SelectMany(t => t.Items.Select(i => i.Id)).ToHashSet();
        Check("import merge: every pre-existing id still present (nothing deleted or replaced)",
            idsBefore.All(liveIds.Contains), idsBefore.Count + " before, " + liveIds.Count + " now");

        // Idempotence: the same bundle a second time adds nothing.
        var again = MigrationService.Apply(store, probeSettings, mergeBundle, replace: false);
        Check("import merge: re-applying the same bundle adds nothing", again.ItemsAdded == 0,
            again.ItemsAdded + " added");

        try { Directory.Delete(_root, true); } catch { }
    }
}

internal sealed class ProbeApp : Application
{
    // FluentTheme is mandatory, not cosmetic: without it ItemsControl generates no containers,
    // so the tile grid stays empty (the standing MEMORY note). Window-level assertions here
    // need real tile buttons in the visual tree.
    public ProbeApp() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

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
