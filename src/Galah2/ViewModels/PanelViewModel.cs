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

    /// <summary>Ask for a single line of text (rename, new category).</summary>
    string? Prompt(string title, string initial);

    /// <summary>Confirm a destructive action.</summary>
    bool Confirm(string title, string message);

    /// <summary>Open the maintenance window.</summary>
    void ShowMaintenance();

    /// <summary>Open settings.</summary>
    void ShowSettings();

    /// <summary>Launch an item; returns true when the shell accepted it.</summary>
    bool Launch(CatalogItem item);
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

    public TabViewModel(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }
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

    public PanelViewModel(CatalogStore catalog, IPanelHost host)
    {
        _catalog = catalog;
        _host = host;

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
        MoveTabLeftCommand = new RelayCommand(() => MoveTab(_selectedTab, -1));
        MoveTabRightCommand = new RelayCommand(() => MoveTab(_selectedTab, +1));
        RetrySaveCommand = new RelayCommand(RetrySave);
        ShowSettingsCommand = new RelayCommand(() => _host.ShowSettings());
        ShowMaintenanceCommand = new RelayCommand(() => _host.ShowMaintenance());
        HideCommand = new RelayCommand(() => _host.HidePanel());
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());

        Refresh();
    }

    public event Action? ExitRequested;

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
    public ICommand MoveTabLeftCommand { get; }
    public ICommand MoveTabRightCommand { get; }
    public ICommand RetrySaveCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowMaintenanceCommand { get; }
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
            Tabs.Add(new TabViewModel(tab.Id, tab.Name));

        SelectTab(Tabs.FirstOrDefault(t => t.Id == previous) ?? Tabs.FirstOrDefault());
        CheckSaveState();
    }
    /// <summary>True when the category strip is a left rail instead of a top row.</summary>
    public bool IsVerticalTabs { get; private set; }

    /// <summary>Icon edge in DIPs for the current size step.</summary>
    public double IconEdge => _iconEdge > 0 ? _iconEdge : 32;

    /// <summary>Tile width: icon plus padding, so a bigger icon never clips its label.</summary>
    public double TileWidth => Math.Max(76, IconEdge + 44);

    /// <summary>Apply layout-affecting settings (icon size, strip position).</summary>
    public void ApplyLayout(SettingsDocument settings)
    {
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

    /// <summary>Live icon-size change: rebuild tiles so each asks for the new edge length.</summary>
    public void ApplyIconSize(string? size) =>
        ApplyLayout(new SettingsDocument { IconSize = size ?? IconSizes.Medium, TabPosition = IsVerticalTabs ? TabPositions.Left : TabPositions.Top });
    /// <summary>Cycle categories with the wheel or Ctrl+number (1.x's tab navigation).</summary>
    public void SwitchTabRelative(int delta)
    {
        if (Tabs.Count == 0) return;
        var current = _selectedTab is null ? 0 : Tabs.IndexOf(_selectedTab);
        var next = (current + delta + Tabs.Count) % Tabs.Count;
        SelectTab(Tabs[next]);
    }

    /// <summary>Ctrl+number: 1..9 select a category directly.</summary>
    /// <summary>Ctrl+number: 1..9 select a category directly.</summary>
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
        if (source is null) return;

        foreach (var item in _catalog.Current.ItemsInOrder(source))
            Items.Add(new ItemViewModel(item, _iconEdge));
    }

    /// <summary>
    /// Launch a tile. The click mode decides whether a single click or a double click runs it;
    /// the view forwards the click count so both work without duplicating the handler.
    /// </summary>
    public void LaunchTile(ItemViewModel tile, int clickCount = 2)
    {
        var ok = _host.Launch(tile.Item);
        if (ok)
        {
            SelectedItem = null;
            if (_hideOnLaunch) _host.HidePanel();
        }
        else
        {
            SaveProblem = "无法启动：" + tile.Name + "（目标可能已失效）";
        }
    }

    private bool _hideOnLaunch = true;

    /// <summary>Reflect a settings change (called by the shell after the store commits).</summary>
    public void ApplySettings(SettingsDocument settings) => _hideOnLaunch = settings.HideOnLaunch;

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
        if (!_host.Confirm("删除条目", "确定删除「" + tile.Name + "」？")) return;

        _catalog.DeleteItem(tile.Id);
        Refresh();
        CheckSaveState();
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
        var name = _host.Prompt("新建分类", "新分类");
        if (string.IsNullOrWhiteSpace(name)) return;

        var created = _catalog.AddTab(name.Trim());
        Refresh();
        // Select what was just created: a new empty category is the thing the user is about to
        // fill, so leaving the old one selected makes the action look like it did nothing.
        SelectTab(Tabs.FirstOrDefault(t => t.Id == created.Id));
        CheckSaveState();
    }

    private void RenameTab(TabViewModel? tab)
    {
        if (tab is null) return;
        var name = _host.Prompt("重命名分类", tab.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == tab.Name) return;

        _catalog.RenameTab(tab.Id, name.Trim());
        Refresh();
        CheckSaveState();
    }

    private void DeleteTab(TabViewModel? tab)
    {
        if (tab is null) return;
        if (!_host.Confirm("删除分类", "确定删除分类「" + tab.Name + "」及其全部条目？")) return;

        _catalog.DeleteTab(tab.Id);
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
