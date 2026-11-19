using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>One tile in the panel.</summary>
public sealed class ItemViewModel : ObservableObject
{
    private Bitmap? _icon;

    public ItemViewModel(CatalogItem item)
    {
        Id = item.Id;
        Name = item.Name;
        Initial = string.IsNullOrWhiteSpace(item.Name) ? "?" : item.Name[..1].ToUpperInvariant();

        // The tile paints the letter immediately and swaps in the real icon when it arrives:
        // shell extraction can take tens of milliseconds and must never block a panel open.
        _ = LoadIconAsync(item);
    }

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

    private async Task LoadIconAsync(CatalogItem item)
    {
        try
        {
            var bitmap = await IconService.GetAsync(item).ConfigureAwait(true);
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
/// The panel's view state. Reads the catalog once per show and projects it; the 1.0 rule that a
/// read never rewrites the library is preserved by never touching the store from here.
/// </summary>
public sealed class PanelViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;
    private readonly Func<string, bool> _launch;
    private TabViewModel? _selectedTab;

    public PanelViewModel(CatalogStore catalog, Func<string, bool> launch)
    {
        _catalog = catalog;
        _launch = launch;
        SelectTabCommand = new RelayCommand(p => { if (p is TabViewModel t) SelectTab(t); });
        LaunchCommand = new RelayCommand(p => { if (p is ItemViewModel i) Launch(i); });
        Refresh();
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();
    public ObservableCollection<ItemViewModel> Items { get; } = new();

    public ICommand SelectTabCommand { get; }
    public ICommand LaunchCommand { get; }

    /// <summary>Re-project the whole panel from the catalog document.</summary>
    public void Refresh()
    {
        var previous = _selectedTab?.Id;
        Tabs.Clear();
        foreach (var tab in _catalog.Current.Tabs)
            Tabs.Add(new TabViewModel(tab.Id, tab.Name));

        SelectTab(Tabs.FirstOrDefault(t => t.Id == previous) ?? Tabs.FirstOrDefault());
    }

    private void SelectTab(TabViewModel? tab)
    {
        if (tab is null)
        {
            Items.Clear();
            return;
        }

        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        _selectedTab = tab;

        Items.Clear();
        var source = _catalog.Current.Tabs.FirstOrDefault(t => t.Id == tab.Id);
        if (source is null) return;

        foreach (var item in source.Items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal))
            Items.Add(new ItemViewModel(item));
    }

    private void Launch(ItemViewModel item) => _launch(item.Id);
}
