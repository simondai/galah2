using System.Collections.ObjectModel;
using System.Windows.Input;
using Galah2.Models;
using Galah2.Persistence;
using Avalonia.Media.Imaging;
using Galah2.Services;
using Galah2.Services.CatalogSources;

namespace Galah2.ViewModels;

/// <summary>One candidate row in the picker.</summary>
public sealed class SeedViewModel : ObservableObject
{
    private bool _isChecked;
    private Bitmap? _icon;

    public SeedViewModel(CatalogSeed seed)
    {
        Seed = seed;
        Name = seed.Name;
        Note = string.IsNullOrWhiteSpace(seed.Note) ? seed.Target : seed.Note;
        Initial = string.IsNullOrWhiteSpace(seed.Name) ? "?" : seed.Name[..1].ToUpperInvariant();
        // A name that cannot be searched is useless in a long list.
        SearchKey = (seed.Name + " " + seed.Target).ToLowerInvariant();

        // The icon arrives a moment later; the row shows a letter tile until then.
        _ = LoadIconAsync(seed);
    }

    public CatalogSeed Seed { get; }
    public string Name { get; }
    public string Note { get; }
    public string SearchKey { get; }
    public string Initial { get; }

    public Bitmap? Icon
    {
        get => _icon;
        private set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); }
    }

    public bool HasIcon => _icon is not null;

    private async Task LoadIconAsync(CatalogSeed seed)
    {
        try
        {
            var item = new CatalogItem
            {
                Id = "seed",
                Name = seed.Name,
                Kind = seed.Kind,
                Target = seed.Target,
                Args = seed.Args,
            };
            var bitmap = await IconService.GetAsync(item, 32).ConfigureAwait(true);
            if (bitmap is not null) Icon = bitmap;
        }
        catch (Exception) { }   // an icon is decoration: never break the list
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) Changed?.Invoke(); }
    }

    public event Action? Changed;
}

/// <summary>One picker category (chip).</summary>
public sealed class SeedCategoryViewModel : ObservableObject
{
    private bool _isActive;
    private int _count;

    public SeedCategoryViewModel(SeedCategory category)
    {
        Category = category;
        Label = CatalogSourceService.LabelOf(category);
    }

    public SeedCategory Category { get; }
    public string Label { get; }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}

/// <summary>
/// The add-picker: enumerate candidates from the catalog sources, let the user filter and tick
/// several, then commit them in one action.
///
/// Source enumeration runs off the UI thread because two of the sources hit the registry and the
/// shell, which can take hundreds of milliseconds on a cold machine.
/// </summary>
public sealed class AddPickerViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;
    private readonly string _tabId;
    private readonly CatalogSourceService _sources = new();
    private readonly Dictionary<SeedCategory, IReadOnlyList<CatalogSeed>> _cachedSeeds = new();
    private SeedCategoryViewModel? _selected;
    private string _filter = "";
    private bool _isBusy;

    public AddPickerViewModel(CatalogStore catalog, string tabId)
    {
        _catalog = catalog;
        _tabId = tabId;

        SelectCategoryCommand = new RelayCommand(p => { if (p is SeedCategoryViewModel c) SelectCategory(c); });
        ToggleAllCommand = new RelayCommand(ToggleAll);
        CommitCommand = new RelayCommand(Commit);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());

        foreach (var category in _sources.Categories)
            Categories.Add(new SeedCategoryViewModel(category));

        Load();
    }

    public event Action? CloseRequested;

    public ObservableCollection<SeedCategoryViewModel> Categories { get; } = new();
    public ObservableCollection<SeedViewModel> Visible { get; } = new();

    public ICommand SelectCategoryCommand { get; }
    public ICommand ToggleAllCommand { get; }
    public ICommand CommitCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>True when at least one candidate is ticked.</summary>
    public bool HasSelection => AllSeeds.Any(s => s.IsChecked);

    public int SelectedCount => AllSeeds.Count(s => s.IsChecked);

    public string CommitLabel => SelectedCount == 0 ? "添加" : $"添加 {SelectedCount} 项";

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) RebuildVisible(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string CountLabel => _selected is null ? "" : $"{Visible.Count} / {_selected.Count} 项";

    public bool HasNothing => !IsBusy && Visible.Count == 0;

    /// <summary>All candidates of the chosen category, ticked state preserved across filtering.</summary>
    private List<SeedViewModel> AllSeeds { get; } = new();

    private async void Load()
    {
        IsBusy = true;
        try
        {
            // 后台一次性枚举并缓存，切换分类时直接复用，不再在 UI 线程重复同步扫描。
            foreach (var category in Categories)
            {
                var seeds = await Task.Run(() => _sources.Enumerate(category.Category)).ConfigureAwait(true);
                _cachedSeeds[category.Category] = seeds;
                category.Count = seeds.Count;
            }

            SelectCategory(Categories.FirstOrDefault());
        }
        finally { IsBusy = false; }
    }

    private void SelectCategory(SeedCategoryViewModel? category)
    {
        if (category is null) return;

        foreach (var c in Categories) c.IsActive = ReferenceEquals(c, category);
        _selected = category;

        AllSeeds.Clear();
        if (!_cachedSeeds.TryGetValue(category.Category, out var seeds))
        {
            seeds = _sources.Enumerate(category.Category);
            _cachedSeeds[category.Category] = seeds;
        }
        foreach (var seed in seeds)
        {
            var vm = new SeedViewModel(seed);
            vm.Changed += OnSeedChanged;
            AllSeeds.Add(vm);
        }

        RebuildVisible();
    }

    private void RebuildVisible()
    {
        var query = _filter.Trim().ToLowerInvariant();

        Visible.Clear();
        foreach (var seed in AllSeeds)
        {
            if (query.Length > 0 && !seed.SearchKey.Contains(query, StringComparison.Ordinal)) continue;
            Visible.Add(seed);
        }

        foreach (var name in new[]
                 {
                     nameof(CountLabel), nameof(HasNothing), nameof(HasSelection),
                     nameof(SelectedCount), nameof(CommitLabel)
                 })
            Raise(name);
    }

    private void OnSeedChanged()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(SelectedCount));
        Raise(nameof(CommitLabel));
    }

    private void ToggleAll()
    {
        var target = !Visible.All(s => s.IsChecked);
        foreach (var seed in Visible) seed.IsChecked = target;
    }

    private void Commit()
    {
        var chosen = AllSeeds.Where(s => s.IsChecked).Select(s => s.Seed).ToList();
        if (chosen.Count == 0) { CloseRequested?.Invoke(); return; }

        var items = chosen.Select(seed => new CatalogItem
        {
            Id = "seed",
            Name = seed.Name,
            Kind = seed.Kind,
            Target = seed.Target,
            Args = seed.Args,
        }).ToList();

        // One commit, not one per item: 1.x's "一次动作一次写" rule, so an interrupted add can
        // never leave half the selection in the library.
        _catalog.AddItems(_tabId, items);
        CloseRequested?.Invoke();
    }
}
