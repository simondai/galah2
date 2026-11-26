using System.Collections.ObjectModel;
using System.Windows.Input;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>One row in a maintenance list.</summary>
public sealed class FindingViewModel
{
    public FindingViewModel(MaintenanceFinding finding)
    {
        Title = finding.Name;
        Detail = finding.TabName + " · " + finding.Reason;
        Target = finding.Target;
    }

    public FindingViewModel(DuplicateGroup group)
    {
        Title = group.Keep.Name + " ×" + (group.Extras.Count + 1);
        Detail = group.Keep.TabName + " · 保留一份，移除 " + group.Extras.Count + " 份重复";
        Target = group.Keep.Target;
    }

    public string Title { get; }
    public string Detail { get; }
    public string Target { get; }
}

/// <summary>
/// Library maintenance.
///
/// Follows 1.x's rule exactly: detection is a read-only preview, and every cleanup is one explicit
/// action with one commit. Nothing here writes during a scan, and a cleanup never touches the
/// user's own assets — only derived icon cache and catalog entries.
/// </summary>
public sealed class MaintenanceViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;
    private readonly SettingsStore _settings;
    private readonly AppDataPaths _paths;
    private MaintenancePreview? _preview;
    private string _status = "";

    public MaintenanceViewModel(CatalogStore catalog, SettingsStore settings, AppDataPaths paths)
    {
        _catalog = catalog;
        _settings = settings;
        _paths = paths;

        RescanCommand = new RelayCommand(() => Rescan(resetStatus: true));
        RemoveInvalidCommand = new RelayCommand(RemoveInvalid);
        RemoveDuplicatesCommand = new RelayCommand(RemoveDuplicates);
        SortCommand = new RelayCommand(SortAll);
        ClearCacheCommand = new RelayCommand(ClearCache);
        ExportCommand = new RelayCommand(Export);
        ImportCommand = new RelayCommand(ImportAsync);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());

        Rescan(resetStatus: true);
    }

    public event Action? CloseRequested;

    /// <summary>Raised after a cleanup so the panel can re-project its content.</summary>
    public event Action? CatalogChanged;

    public ObservableCollection<FindingViewModel> Invalid { get; } = new();
    public ObservableCollection<FindingViewModel> Duplicates { get; } = new();
    public ObservableCollection<string> UnsortedTabs { get; } = new();

    public ICommand RescanCommand { get; }
    public ICommand RemoveInvalidCommand { get; }
    public ICommand RemoveDuplicatesCommand { get; }
    public ICommand SortCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand CloseCommand { get; }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Summary => _preview is null
        ? ""
        : $"{_preview.TotalItems} 个条目 · 失效 {_preview.Invalid.Count} · 重复组 {_preview.Groups.Count} · 未排序 {_preview.UnsortedTabs.Count}";

    public bool HasInvalid => Invalid.Count > 0;
    public bool HasDuplicates => Duplicates.Count > 0;
    public bool HasUnsorted => UnsortedTabs.Count > 0;
    public bool IsClean => !HasInvalid && !HasDuplicates && !HasUnsorted;

    public void Rescan() => Rescan(resetStatus: true);

    private void Rescan(bool resetStatus)
    {
        _preview = MaintenanceService.Preview(_catalog.Current);

        Invalid.Clear();
        foreach (var f in _preview.Invalid) Invalid.Add(new FindingViewModel(f));

        Duplicates.Clear();
        foreach (var g in _preview.Groups) Duplicates.Add(new FindingViewModel(g));

        UnsortedTabs.Clear();
        foreach (var t in _preview.UnsortedTabs) UnsortedTabs.Add(t);

        foreach (var n in new[]
                 {
                     nameof(Summary), nameof(HasInvalid), nameof(HasDuplicates),
                     nameof(HasUnsorted), nameof(IsClean)
                 })
            Raise(n);

        if (resetStatus) Status = "扫描完成，尚未改动任何内容";
    }

    private void RemoveInvalid()
    {
        if (_preview is null) return;
        var removed = MaintenanceService.RemoveInvalid(_catalog, _preview);
        Status = removed == 0 ? "没有失效项需要清理" : $"已清理 {removed} 个失效项";
        AfterCleanup();
    }

    private void RemoveDuplicates()
    {
        if (_preview is null) return;
        var removed = MaintenanceService.RemoveDuplicates(_catalog, _preview);
        Status = removed == 0 ? "没有重复项需要清理" : $"已移除 {removed} 个重复项";
        AfterCleanup();
    }

    private void SortAll()
    {
        if (_preview is null) return;
        var changed = MaintenanceService.SortAllTabs(_catalog, _preview);
        Status = changed == 0 ? "所有分类本来就按名称有序" : $"已重排 {changed} 个条目";
        AfterCleanup();
    }

    private void ClearCache()
    {
        var removed = MaintenanceService.ClearIconCache(_paths);
        Status = $"已清除 {removed} 个图标缓存文件，下次显示时会重建";
        AfterCleanup();
    }

    private void Export()
    {
        try
        {
            var file = MigrationService.Export(_paths, _catalog.Current, _settings.Current);
            Status = "已导出到 " + file;
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Import needs a file picker, which only the window can provide, so the shell supplies it.
    /// </summary>
    public Func<Task<string?>>? PickImportFile { get; set; }

    private async void ImportAsync()
    {
        if (PickImportFile is null) { Status = "导入需要窗口支持"; return; }

        string? file;
        try { file = await PickImportFile(); }
        catch (Exception ex) { Status = "选择文件失败：" + ex.Message; return; }
        if (string.IsNullOrEmpty(file)) return;

        try
        {
            // ReadBundle is the first gate: a foreign or malformed file is refused whole, so a
            // bad bundle can never half-apply.
            var bundle = MigrationService.ReadBundle(file);
            var outcome = MigrationService.Apply(_catalog, _settings, bundle, replace: false);

            Status = $"已导入 {outcome.ItemsAdded} 个条目（{outcome.Tabs} 个分类），跳过 {outcome.ItemsDropped} 个";
            CatalogChanged?.Invoke();
            Rescan(resetStatus: false);
        }
        catch (MigrationException ex) { Status = "导入被拒绝：" + ex.Message; }
        catch (Exception ex) { Status = "导入失败：" + ex.Message; }
    }

    private void AfterCleanup()
    {
        Rescan(resetStatus: false);
        CatalogChanged?.Invoke();
    }
}
