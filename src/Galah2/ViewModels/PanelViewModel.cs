using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>Services the panel needs from the app shell.</summary>
public interface IPanelHost
{
    /// <summary>Hide the panel (Escape, launch, the close button).</summary>
    void HidePanel();

    /// <summary>Open the add-picker for a category.</summary>
    void ShowAddPicker(string tabId);

    /// <summary>Open the add/edit dialog. Returns the draft it committed, if any.</summary>
    CatalogItem? ShowEditItem(string tabId, CatalogItem? existing);

    /// <summary>
    /// Ask for a single line of text. The answer arrives through a callback: a dialog result cannot
    /// be awaited synchronously on the UI thread without deadlocking the dialog itself.
    /// </summary>
    void Prompt(string title, string initial, Action<string?> onAnswer);

    /// <summary>Confirm a destructive action; the answer arrives through a callback.</summary>
    void Confirm(string title, string message, Action<bool> onAnswer);

    /// <summary>Open the maintenance window.</summary>
    void ShowMaintenance();

    /// <summary>Open the RandPass generator (activate it if already open).</summary>
    void ShowRandPass();

    /// <summary>Open settings.</summary>
    void ShowSettings();

    /// <summary>Launch an item; returns true when the shell accepted it.</summary>
    bool Launch(CatalogItem item);

    /// <summary>Pick a file. Returns null when the picker was cancelled or unavailable.</summary>
    Task<string?> PickFile(string title);

    /// <summary>Pick a folder. Returns null when the picker was cancelled or unavailable.</summary>
    Task<string?> PickFolder(string title);
}

/// <summary>One tile in the panel.</summary>
public sealed class ItemViewModel : ObservableObject
{
    private Bitmap? _icon;

    public ItemViewModel(CatalogItem item, int iconEdge)
    {
        Item = item;
        Id = item.Id;
        Name = item.Name;
        Initial = string.IsNullOrWhiteSpace(item.Name) ? "?" : item.Name[..1].ToUpperInvariant();

        // The tile paints the letter immediately and swaps in the real icon when it arrives:
        // shell extraction can take tens of milliseconds and must never block a panel open.
        _ = LoadIconAsync(item, iconEdge);
    }

    /// <summary>
    /// The permanent last cell of the grid: a placeholder tile that opens the add-entry menu
    /// instead of launching anything. It belongs to the list view, not the library - Item is
    /// null for it, so every consumer of Item must check IsAddTile first (LaunchTile does, and
    /// the view never starts a drag from it).
    /// </summary>
    public static ItemViewModel AddTile() => new();

    private ItemViewModel()
    {
        IsAddTile = true;
        Item = null!;
        Id = "add-tile";
        Name = "添加";
        Initial = "＋";
    }

    /// <summary>True for the add-entry placeholder - see the AddTile factory.</summary>
    public bool IsAddTile { get; }

    /// <summary>The library entry behind this tile. Null only when IsAddTile.</summary>
    public CatalogItem Item { get; }
    public string Id { get; }
    public string Name { get; }
    public string Initial { get; }

    public Bitmap? Icon
    {
        get => _icon;
        private set
        {
            if (Set(ref _icon, value)) Raise(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    // ── context-menu commands ─────────────────────────────────────────────────
    // A ContextMenu's rows can only bind against what the menu inherits from its host control -
    // for a tile, that is this ItemViewModel. Nothing else reaches the menu: $parent paths die at
    // the popup boundary, and XAML-attached events on a ContextMenu (Opening="...") never fire at
    // all (both proven in the 2026-11-24 grey-menu round, where the tile menu and the chip menu
    // shipped grey to the user while every in-process probe passed). So every row command lives
    // here, and the owning PanelViewModel fills it in when it creates the tile. The no-op
    // defaults keep an owner-less tile inert instead of throwing.
    public ICommand OpenCommand { get; set; } = new RelayCommand(() => { });
    public ICommand EditCommand { get; set; } = new RelayCommand(() => { });
    public ICommand MoveLeftCommand { get; set; } = new RelayCommand(() => { });
    public ICommand MoveRightCommand { get; set; } = new RelayCommand(() => { });
    public ICommand DeleteCommand { get; set; } = new RelayCommand(() => { });
    // The sentinel placeholder carries the four add commands (the same ones the ☰ menu and the
    // blank-space menu expose). Real tiles never use them - their menu branch is hidden.
    public ICommand AddPickerCommand { get; set; } = new RelayCommand(() => { });
    public ICommand AddFileCommand { get; set; } = new RelayCommand(() => { });
    public ICommand AddFolderCommand { get; set; } = new RelayCommand(() => { });
    public ICommand AddUrlCommand { get; set; } = new RelayCommand(() => { });

    private async Task LoadIconAsync(CatalogItem item, int iconEdge)
    {
        try
        {
            var bitmap = await IconService.GetAsync(item, iconEdge).ConfigureAwait(true);
            if (bitmap is not null) Icon = bitmap;
        }
        catch (Exception) { }
    }
}

/// <summary>One category chip.</summary>
public sealed class TabViewModel : ObservableObject
{
    private bool _isActive;
    private bool _hasNoEntries;

    public TabViewModel(string id, string name, bool hasNoEntries)
    {
        Id = id;
        Name = name;
        _hasNoEntries = hasNoEntries;
    }

    public string Id { get; }
    public string Name { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>
    /// True when the category holds no entries. Drives the chip context menu's 删除分类 row:
    /// a category with entries cannot be deleted from the chip (the rule is user-stated).
    /// </summary>
    public bool HasNoEntries
    {
        get => _hasNoEntries;
        set => Set(ref _hasNoEntries, value);
    }

    // The chip's right-click menu rows bind to THIS tab's own commands (the menu inherits the
    // chip button's DataContext and nothing else can reach it - see ItemViewModel's comment).
    // Wired by the PanelViewModel in Refresh; the no-op defaults keep a lone tab inert.
    public ICommand RenameCommand { get; set; } = new RelayCommand(() => { });
    public ICommand DeleteCommand { get; set; } = new RelayCommand(() => { });
}

/// <summary>
/// The panel's view state and command surface.
///
/// Every operation that changes the library goes through <see cref="CatalogStore"/>, which owns the
/// single-commit rule (one user action = one write). Reads never rewrite anything.
/// </summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;
    private readonly IPanelHost _host;
    private TabViewModel? _selectedTab;
    private ItemViewModel? _selectedItem;
    private int _iconEdge = 32;
    private string _saveProblem = "";

    public PanelViewModel(CatalogStore catalog, IPanelHost host, ToastService toast)
    {
        _catalog = catalog;
        _host = host;
        Toast = toast;

        SelectTabCommand = new RelayCommand(p => { if (p is TabViewModel t) SelectTab(t); });
        LaunchCommand = new RelayCommand(p => { if (p is ItemViewModel i) LaunchTile(i); });
        EditItemCommand = new RelayCommand(p => EditItem(p as ItemViewModel));
        DeleteItemCommand = new RelayCommand(p => DeleteItem(p as ItemViewModel));
        MoveItemLeftCommand = new RelayCommand(p => MoveItem(p as ItemViewModel, -1));
        MoveItemRightCommand = new RelayCommand(p => MoveItem(p as ItemViewModel, +1));
        AddPickerCommand = new RelayCommand(() => GuardTab(t => _host.ShowAddPicker(t.Id)));
        AddFileCommand = new RelayCommand(() => GuardTab(t => AddSingle(t.Id, ItemKinds.File)));
        AddFolderCommand = new RelayCommand(() => GuardTab(t => AddSingle(t.Id, ItemKinds.Folder)));
        AddUrlCommand = new RelayCommand(() => GuardTab(t => AddSingle(t.Id, ItemKinds.Url)));
        NewTabCommand = new RelayCommand(NewTab);
        RenameTabCommand = new RelayCommand(() => RenameTab(_selectedTab));
        DeleteTabCommand = new RelayCommand(() => DeleteTab(_selectedTab));
        // The chip's right-click menu no longer needs panel-level commands: each TabViewModel
        // carries its own RenameCommand/DeleteCommand (wired in Refresh), because a ContextMenu
        // can only bind against its host's DataContext - see WireTileMenu's comment.
        MoveTabLeftCommand = new RelayCommand(() => MoveTab(_selectedTab, -1));
        MoveTabRightCommand = new RelayCommand(() => MoveTab(_selectedTab, +1));
        RetrySaveCommand = new RelayCommand(RetrySave);
        ShowSettingsCommand = new RelayCommand(() => _host.ShowSettings());
        ShowMaintenanceCommand = new RelayCommand(() => _host.ShowMaintenance());
        ShowRandPassCommand = new RelayCommand(() => _host.ShowRandPass());
        HideCommand = new RelayCommand(() => _host.HidePanel());
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

        Refresh();
    }

    public event Action? ExitRequested;

    /// <summary>Transient message strip (launch failures, first-run hints).</summary>
    public ToastService Toast { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<ItemViewModel> Items { get; } = new();

    public ICommand SelectTabCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand EditItemCommand { get; }
    public ICommand DeleteItemCommand { get; }
    public ICommand MoveItemLeftCommand { get; }
    public ICommand MoveItemRightCommand { get; }
    public ICommand AddPickerCommand { get; }
    public ICommand AddFileCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand AddUrlCommand { get; }
    public ICommand NewTabCommand { get; }
    public ICommand RenameTabCommand { get; }
    public ICommand DeleteTabCommand { get; }
    /// <summary>Chip menu (bound per tab): rename that category by id.</summary>
    private void RenameTabById(string id)
    {
        if (Tabs.FirstOrDefault(t => t.Id == id) is TabViewModel t) RenameTab(t);
    }

    /// <summary>Chip menu (bound per tab): delete that category by id, only while empty.</summary>
    private void DeleteTabIfEmptyById(string id)
    {
        if (Tabs.FirstOrDefault(t => t.Id == id) is TabViewModel t) DeleteTabIfEmpty(t);
    }
    public ICommand MoveTabLeftCommand { get; }
    public ICommand MoveTabRightCommand { get; }
    public ICommand RetrySaveCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowMaintenanceCommand { get; }
    public ICommand ShowRandPassCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ExitCommand { get; }

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        private set => Set(ref _selectedTab, value);
    }

    public ItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => Set(ref _selectedItem, value);
    }

    /// <summary>Product name shown in the panel corner (never the active category).</summary>
    public string AppTitle => "Galah";

    public string ActiveTabName => _selectedTab?.Name ?? "";

    /// <summary>Non-empty when the last commit failed; the banner offers a retry (INV-9).</summary>
    public string SaveProblem
    {
        get => _saveProblem;
        private set { if (Set(ref _saveProblem, value)) Raise(nameof(HasSaveProblem)); }
    }

    public bool HasSaveProblem => _saveProblem.Length > 0;

    /// <summary>Re-project the whole panel from the catalog document.</summary>
    public void Refresh()
    {
        var previous = _selectedTab?.Id;

        Tabs.Clear();
        foreach (var tab in _catalog.Current.TabsInOrder())
        {
            // The chip menu binds each tab's own commands (the menu cannot reach this VM), so
            // every tab must carry its own, captured by id - a Refresh mid-menu must not make
            // a stale row act on the wrong category.
            var tabId = tab.Id;
            var tabVm = new TabViewModel(tabId, tab.Name, tab.Items.Count == 0);
            tabVm.RenameCommand = new RelayCommand(() => RenameTabById(tabId));
            tabVm.DeleteCommand = new RelayCommand(() => DeleteTabIfEmptyById(tabId));
            Tabs.Add(tabVm);
        }

        SelectTab(Tabs.FirstOrDefault(t => t.Id == previous) ?? Tabs.FirstOrDefault());
        CheckSaveState();
    }
    /// <summary>True when the category strip is a left rail instead of a top row.</summary>
    public bool IsVerticalTabs { get; private set; }

    /// <summary>Icon edge in DIPs for the current size step.</summary>
    public double IconEdge => _iconEdge > 0 ? _iconEdge : 32;

    /// <summary>Tile width: icon plus padding, so a bigger icon never clips its label.</summary>
    public double TileWidth => Math.Max(76, IconEdge + 44);

    /// <summary>Category chip label font size in DIPs (the size step resolved for layout).</summary>
    public double ChipFontSize => ChipFontSizes.FontSize(_chipFontSize);
    private string _chipFontSize = ChipFontSizes.Medium;

    /// <summary>Apply layout-affecting settings (icon size, strip position).</summary>
    public void ApplyLayout(SettingsDocument settings)
    {
        // Font size is binding-driven, so it applies even when the tile geometry is unchanged.
        ApplyChipFontSize(settings.TabFontSize);

        var edge = (int)IconSizes.IconEdge(settings.IconSize);
        var vertical = settings.TabPosition == TabPositions.Left;

        if (edge == _iconEdge && vertical == IsVerticalTabs) return;

        _iconEdge = edge;
        IsVerticalTabs = vertical;

        // Re-project so each tile asks for the new edge length.
        SelectTab(_selectedTab);

        foreach (var n in new[] { nameof(IconEdge), nameof(TileWidth), nameof(IsVerticalTabs) })
            Raise(n);
    }

    /// <summary>Chip label size is binding-driven (no re-projection); just keep the step and re-raise.</summary>
    public void ApplyChipFontSize(string size)
    {
        if (_chipFontSize == size) return;
        _chipFontSize = size;
        Raise(nameof(ChipFontSize));
    }

    /// <summary>Live icon-size change: rebuild tiles so each asks for the new edge length.</summary>
    public void ApplyIconSize(string? size) =>
        ApplyLayout(new SettingsDocument { IconSize = size ?? IconSizes.Medium, TabPosition = IsVerticalTabs ? TabPositions.Left : TabPositions.Top });
    /// <summary>Cycle categories with the wheel or Ctrl+number (1.x's tab navigation).</summary>
    public void SwitchTabRelative(int delta)
    {
        // Wrapping lives in TabNavigation so the rule stays testable without a dispatcher.
        var current = _selectedTab is null ? -1 : Tabs.IndexOf(_selectedTab);
        var next = TabNavigation.StepIndex(Tabs.Count, current, delta);
        if (next >= 0) SelectTab(Tabs[next]);
    }

    /// <summary>
    /// Ctrl+number: 1..9 select a category directly.
    /// NOTE: this overload is 1-BASED. Galah 1.x had a same-named method that was 0-based;
    /// TabNavigation.DigitToIndex (0-based) feeds this with index + 1.
    /// </summary>
    public bool SelectTabByIndex(int oneBased)
    {
        if (oneBased < 1 || oneBased > Tabs.Count) return false;
        SelectTab(Tabs[oneBased - 1]);
        return true;
    }

    public void SelectTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            foreach (var t in Tabs) t.IsActive = false;
            SelectedTab = null;
            Items.Clear();
            return;
        }

        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        SelectedTab = tab;
        SelectedItem = null;
        Raise(nameof(ActiveTabName));

        Items.Clear();
        var source = _catalog.Current.FindTab(tab.Id);
        if (source is not null)
        {
            foreach (var item in _catalog.Current.ItemsInOrder(source))
            {
                var tile = new ItemViewModel(item, _iconEdge);
                WireTileMenu(tile);
                Items.Add(tile);
            }
        }

        // The add-entry placeholder is the last cell when the setting asks for it, in an empty
        // category too. With the setting off (the default) the grid ends with the real entries
        // and adding goes through the blank-space right-click or the ☰ menu.
        if (_showAddTile)
        {
            var sentinel = ItemViewModel.AddTile();
            WireSentinelMenu(sentinel);
            Items.Add(sentinel);
        }
    }

    /// <summary>
    /// A ContextMenu's rows can only bind against the menu's inherited DataContext (the tile's
    /// ItemViewModel) - $parent paths die at the popup boundary and XAML-attached events on a
    /// ContextMenu never fire (2026-11-24 grey-menu round). So the tile carries its own menu
    /// commands, wired here at creation time.
    /// </summary>
    private void WireTileMenu(ItemViewModel tile)
    {
        tile.OpenCommand = new RelayCommand(() => LaunchTile(tile));
        tile.EditCommand = new RelayCommand(() => EditItem(tile));
        tile.MoveLeftCommand = new RelayCommand(() => MoveItem(tile, -1));
        tile.MoveRightCommand = new RelayCommand(() => MoveItem(tile, +1));
        tile.DeleteCommand = new RelayCommand(() => DeleteItem(tile));
    }

    /// <summary>The sentinel's add menu: the same four commands as the ☰ menu (current category).</summary>
    private void WireSentinelMenu(ItemViewModel sentinel)
    {
        sentinel.AddPickerCommand = AddPickerCommand;
        sentinel.AddFileCommand = AddFileCommand;
        sentinel.AddFolderCommand = AddFolderCommand;
        sentinel.AddUrlCommand = AddUrlCommand;
    }

    /// <summary>
    /// Launch a tile when the click count matches the configured click mode. Returns false when this
    /// click was only a selection, so the view knows not to treat it as an action.
    /// </summary>
    public bool LaunchTile(ItemViewModel tile, int clickCount = -1)
    {
        // The placeholder tile has no target: its gesture is the menu, never a launch.
        if (tile.IsAddTile) return false;

        var wanted = ClickMode == ClickModes.Double ? 2 : 1;
        // -1 marks a non-gesture entry point (context-menu "Open", commands, probes): those
        // must launch regardless of ClickMode. Only explicit mouse gestures pass through the
        // gate, where an exact match stops a double tap launching twice in single-click mode.
        if (clickCount >= 0 && clickCount != wanted)
        {
            SelectedItem = tile;   // a single click in double-click mode only selects
            return false;
        }

        var ok = _host.Launch(tile.Item);
        if (ok)
        {
            SelectedItem = null;
            if (_hideOnLaunch) _host.HidePanel();
        }
        else
        {
            // A launch failure is news, not a standing problem: show it in the toast instead of the
            // save banner, which is reserved for "your library could not be written".
            Toast.Show("无法启动：" + tile.Name + "（目标可能已失效）");
        }

        return ok;
    }

    private bool _hideOnLaunch = true;
    private string _clickMode = ClickModes.Single;
    private string _panelMaterial = PanelMaterials.None;

    /// <summary>Reflect a settings change (called by the shell after the store commits).</summary>
    public void ApplySettings(SettingsDocument settings)
    {
        _hideOnLaunch = settings.HideOnLaunch;
        _clickMode = settings.ClickMode;
        _panelMaterial = settings.PanelMaterial;
        _hideOnBlur = settings.HideOnBlur;
        _wheelSwitchTab = settings.WheelSwitchTab;
        _numberKeysSwitchTab = settings.NumberKeysSwitchTab;

        // Toggling the placeholder changes the projection, not just a flag: re-project so the
        // grid gains or loses its last cell immediately, without reopening the panel.
        var addTileChanged = _showAddTile != settings.ShowAddTile;
        _showAddTile = settings.ShowAddTile;
        if (addTileChanged) SelectTab(_selectedTab);

        foreach (var n in new[]
                 {
                     nameof(HideOnBlur), nameof(PanelMaterial),
                     nameof(WheelSwitchTab), nameof(NumberKeysSwitchTab)
                 }) Raise(n);
    }

    /// <summary>Whether the grid ends with the dashed add placeholder (settings-driven).</summary>
    public bool ShowAddTile => _showAddTile;
    private bool _showAddTile;

    /// <summary>Current click semantics ("single" | "double").</summary>
    public string ClickMode => _clickMode;

    /// <summary>True when the panel should hide as soon as it loses focus.</summary>
    public bool HideOnBlur => _hideOnBlur;

    private bool _hideOnBlur;

    /// <summary>Current material token, so the window can pick its transparency level.</summary>
    public string PanelMaterial => _panelMaterial;

    /// <summary>True when the wheel may switch categories (at a list edge). 1.x parity.</summary>
    public bool WheelSwitchTab => _wheelSwitchTab;

    private bool _wheelSwitchTab = true;

    /// <summary>True when Ctrl+0..9 may select a category. 1.x parity.</summary>
    public bool NumberKeysSwitchTab => _numberKeysSwitchTab;

    private bool _numberKeysSwitchTab = true;

    private void EditItem(ItemViewModel? tile)
    {
        if (tile is null || _selectedTab is null) return;

        var committed = _host.ShowEditItem(_selectedTab.Id, tile.Item);
        if (committed is null) return;   // cancelled or rejected: nothing to re-project

        Refresh();
        CheckSaveState();
    }

    private void DeleteItem(ItemViewModel? tile)
    {
        if (tile is null) return;

        _host.Confirm("删除条目", "确定删除「" + tile.Name + "」？", confirmed =>
        {
            if (!confirmed) return;
            _catalog.DeleteItem(tile.Id);
            Refresh();
            CheckSaveState();
        });
    }

    private void MoveItem(ItemViewModel? tile, int delta)
    {
        if (tile is null) return;
        _catalog.MoveItem(tile.Id, delta);
        Refresh();
        CheckSaveState();
    }

    private void AddSingle(string tabId, string kind)
    {
        var committed = _host.ShowEditItem(tabId, new CatalogItem { Id = "draft", Kind = kind });
        if (committed is null) return;
        Refresh();
        CheckSaveState();
    }

    private void GuardTab(Action<CatalogTab> action)
    {
        if (_selectedTab is null) return;
        var tab = _catalog.Current.FindTab(_selectedTab.Id);
        if (tab is not null) action(tab);
    }

    private void NewTab()
    {
        _host.Prompt("新建分类", "新分类", name =>
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            var created = _catalog.AddTab(name.Trim());
            Refresh();
            // Select what was just created: a new empty category is the thing the user is about
            // to fill, so leaving the old one selected makes the action look like it did nothing.
            SelectTab(Tabs.FirstOrDefault(t => t.Id == created.Id));
            CheckSaveState();
        });
    }

    private void RenameTab(TabViewModel? tab)
    {
        if (tab is null) return;

        _host.Prompt("重命名分类", tab.Name, name =>
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim() == tab.Name) return;

            _catalog.RenameTab(tab.Id, name.Trim());
            Refresh();
            CheckSaveState();
        });
    }


    private void DeleteTab(TabViewModel? tab)
    {
        if (tab is null) return;

        _host.Confirm("删除分类", "确定删除分类「" + tab.Name + "」及其全部条目？", confirmed =>
        {
            if (!confirmed) return;
            _catalog.DeleteTab(tab.Id);
            Refresh();
            CheckSaveState();
        });
    }

    /// <summary>
    /// Chip-menu delete: a category can only be deleted while it holds no entries (user rule,
    /// 2026-11-24). The menu row is also disabled by the HasNoEntries binding; this guard is the
    /// second lock so the rule holds even if the row is ever re-enabled or called programmatically.
    /// </summary>
    private void DeleteTabIfEmpty(TabViewModel? tab)
    {
        if (tab is null) return;

        var count = _catalog.Current.Tabs.FirstOrDefault(t => t.Id == tab.Id)?.Items.Count ?? 0;
        if (count > 0)
        {
            Toast.Show($"分类「{tab.Name}」下还有 {count} 个条目，不能删除");
            return;
        }

        DeleteTab(tab);
    }

    /// <summary>Move an item into another category (drag a tile onto a chip).</summary>
    public void MoveItemToTab(string itemId, string targetTabId)
    {
        _catalog.MoveItemToTab(itemId, targetTabId);
        Refresh();
        CheckSaveState();
    }

    /// <summary>
    /// Reorder inside the current category (drag a tile onto another tile). The display
    /// projection keeps real items first and the placeholder last, so a projection index is a
    /// data index - except for the placeholder itself, which the store refuses by id anyway.
    /// </summary>
    public void MoveItemToIndex(string itemId, int targetIndex)
    {
        if (!_catalog.MoveItemToIndex(itemId, targetIndex)) return;
        Refresh();
        CheckSaveState();
    }

    /// <summary>Reorder the strip (drag a chip onto another chip: the target IS the slot).</summary>
    public void MoveTabToIndex(string tabId, int targetIndex)
    {
        if (!_catalog.MoveTabToIndex(tabId, targetIndex)) return;
        Refresh();
        CheckSaveState();
    }

    private void MoveTab(TabViewModel? tab, int delta)
    {
        if (tab is null) return;
        _catalog.MoveTab(tab.Id, delta);
        Refresh();
        CheckSaveState();
    }

    /// <summary>
    /// Commit a drop. One commit for the whole payload, so an interrupted drop cannot leave part
    /// of the selection in the library.
    /// </summary>
    public int HandleDrop(IEnumerable<string> paths)
    {
        if (_selectedTab is null) return 0;

        var preview = ImportService.FromFilePaths(paths);
        if (preview.Items.Count == 0)
        {
            SaveProblem = preview.Skipped.Count > 0
                ? "拖入的内容无法识别：" + string.Join("、", preview.Skipped.Take(3))
                : "";
            return 0;
        }

        _catalog.AddItems(_selectedTab.Id, preview.Items);
        Refresh();
        CheckSaveState();
        return preview.Items.Count;
    }

    /// <summary>Commit pasted text as a URL entry. Returns false when the text is not a URL.</summary>
    public bool HandlePasteText(string? text)
    {
        if (_selectedTab is null) return false;

        var item = ImportService.FromText(text);
        if (item is null) return false;

        _catalog.AddItems(_selectedTab.Id, new[] { item });
        Refresh();
        CheckSaveState();
        return true;
    }

    private void RetrySave()
    {
        _catalog.RetryPendingSave();
        CheckSaveState();
    }

    private void CheckSaveState() =>
        SaveProblem = _catalog.LastSaveError is { Length: > 0 } error ? error : "";
}
