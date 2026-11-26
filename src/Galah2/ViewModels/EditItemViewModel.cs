using System.Windows.Input;
using Avalonia.Media.Imaging;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>What the caller does with the result.</summary>
public enum EditOutcome
{
    /// <summary>Nothing was committed (dialog cancelled).</summary>
    Cancelled,
    /// <summary>The draft passed validation and was written.</summary>
    Committed,
    /// <summary>The draft failed validation; the store was not touched.</summary>
    Rejected,
}

/// <summary>
/// Add/edit dialog state.
///
/// The commit gate is 1.x's: <see cref="ItemValidator"/> decides, and a rejected draft never
/// reaches the store, so a failed dialog leaves all three documents byte-identical.
/// </summary>
public sealed class EditItemViewModel : ObservableObject
{
    private readonly CatalogStore _catalog;
    private readonly IPanelHost _host;
    private readonly AppDataPaths _paths;
    private readonly string? _tabId;
    private readonly CatalogItem _draft;
    private readonly bool _isNew;

    private string _name = "";
    private string _target = "";
    private string _arguments = "";
    private string _workingDirectory = "";
    private string _errorText = "";
    private bool _runAsAdmin;
    private bool _suggestKind;
    private Bitmap? _iconPreview;
    private bool _isFetchingIcon;
    private int _previewVersion;

    public EditItemViewModel(CatalogStore catalog, string? tabId, CatalogItem? existing,
        IPanelHost host, AppDataPaths paths)
    {
        _catalog = catalog;
        _tabId = tabId;
        _host = host;
        _paths = paths;
        // Id 为空或 "draft" 均视为新建，但保留调用方传入的初始 Kind（如菜单点“添加文件夹/网址”）。
        _isNew = existing is null || string.IsNullOrEmpty(existing.Id) || existing.Id == "draft";
        _draft = existing?.Clone() ?? new CatalogItem
        {
            Id = "draft",
            Kind = ItemKinds.App,
        };

        Title = _isNew
            ? _draft.Kind switch
            {
                ItemKinds.File => "添加文件",
                ItemKinds.Folder => "添加文件夹",
                ItemKinds.Url => "添加网址",
                _ => "添加快捷方式",
            }
            : "编辑快捷方式";
        BrowseTargetCommand = new RelayCommand(BrowseTarget);
        BrowseWorkdirCommand = new RelayCommand(BrowseWorkdir);
        PickIconCommand = new RelayCommand(PickIcon);
        FetchIconCommand = new RelayCommand(FetchIcon);
        ResetIconCommand = new RelayCommand(ResetIcon);
        SaveCommand = new RelayCommand(() => Commit());
        CancelCommand = new RelayCommand(() => { Outcome = EditOutcome.Cancelled; CloseRequested?.Invoke(); });
        ApplySuggestedKindCommand = new RelayCommand(() => ApplySuggestedKind());

        Load();
    }

    public event Action? CloseRequested;

    public string Title { get; }
    public bool IsNew => _isNew;

    public ICommand BrowseTargetCommand { get; }
    public ICommand BrowseWorkdirCommand { get; }
    public ICommand PickIconCommand { get; }
    public ICommand FetchIconCommand { get; }
    public ICommand ResetIconCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ApplySuggestedKindCommand { get; }

    public EditOutcome Outcome { get; private set; } = EditOutcome.Cancelled;

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            Revalidate();
        }
    }

    public string TargetText
    {
        get => _target;
        set
        {
            if (!Set(ref _target, value)) return;
            // Typing a target re-suggests the kind and, for a new item, the name: 1.x did this so
            // dropping a path and forgetting to name it still produced a sensible entry.
            var suggested = KindInference.Suggest(value, _draft.Kind);
            SuggestKind = suggested != _draft.Kind;
            if (_isNew && string.IsNullOrWhiteSpace(_name))
                Name = KindInference.DeriveName(suggested, value);
            Revalidate();
            _ = RefreshPreviewAsync();
        }
    }

    public string Arguments
    {
        get => _arguments;
        set { if (Set(ref _arguments, value)) _draft.Args = string.IsNullOrWhiteSpace(value) ? null : value; }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { if (Set(ref _workingDirectory, value)) _draft.Workdir = string.IsNullOrWhiteSpace(value) ? null : value; }
    }

    public bool RunAsAdmin
    {
        get => _runAsAdmin;
        set { if (Set(ref _runAsAdmin, value)) _draft.RunAsAdmin = value; }
    }

    public bool SuggestKind
    {
        get => _suggestKind;
        private set => Set(ref _suggestKind, value);
    }

    public Bitmap? IconPreview
    {
        get => _iconPreview;
        private set => Set(ref _iconPreview, value);
    }

    public bool IsFetchingIcon
    {
        get => _isFetchingIcon;
        private set { if (Set(ref _isFetchingIcon, value)) Raise(nameof(CanSave)); }
    }

    public bool CanSave => !_isFetchingIcon;

    /// <summary>Problems shown under the form; empty means the draft may be committed.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set { if (Set(ref _errorText, value)) Raise(nameof(HasErrors)); }
    }

    public bool HasErrors => _errorText.Length > 0;

    /// <summary>True when the draft passed and was written; the caller closes the dialog.</summary>
    public bool Completed { get; private set; }

    /// <summary>The draft that was written, null when nothing was committed.</summary>
    public CatalogItem? CommittedItem { get; private set; }

    private void Load()
    {
        _name = _draft.Name ?? "";
        _target = _draft.Target ?? "";
        _arguments = _draft.Args ?? "";
        _workingDirectory = _draft.Workdir ?? "";
        _runAsAdmin = _draft.RunAsAdmin;

        foreach (var n in new[]
                 {
                     nameof(Name), nameof(TargetText), nameof(Arguments), nameof(WorkingDirectory),
                     nameof(RunAsAdmin), nameof(IconPreview), nameof(ErrorText)
                 })
            Raise(n);

        _ = RefreshPreviewAsync();
    }

    private void ApplySuggestedKind()
    {
        _draft.Kind = KindInference.Suggest(_target, _draft.Kind);
        SuggestKind = false;
        Revalidate();
        _ = RefreshPreviewAsync();
    }

    private void Revalidate()
    {
        var errors = ItemValidator.Validate(BuildDraft(), _isNew);
        ErrorText = errors.Count == 0
            ? ""
            : string.Join("\n", errors.Select(e => "· " + e.Message));
    }

    private CatalogItem BuildDraft()
    {
        var draft = _draft.Clone();
        draft.Name = _name.Trim();
        draft.Target = PathNorm.ToStored(_target);
        draft.Args = string.IsNullOrWhiteSpace(_arguments) ? null : _arguments;
        draft.Workdir = string.IsNullOrWhiteSpace(_workingDirectory) ? null : PathNorm.ToStored(_workingDirectory);
        return draft;
    }

    private async Task RefreshPreviewAsync()
    {
        var version = Interlocked.Increment(ref _previewVersion);
        try
        {
            var draft = BuildDraft();
            var bitmap = string.IsNullOrWhiteSpace(draft.Target)
                ? null
                : await IconService.GetAsync(draft, 32).ConfigureAwait(true);
            // 仅在没有更新的预览请求时回写，避免慢返回的老请求覆盖新结果。
            if (version == Volatile.Read(ref _previewVersion))
                IconPreview = bitmap;
        }
        catch (Exception)
        {
            if (version == Volatile.Read(ref _previewVersion))
                IconPreview = null;
        }
    }

    /// <summary>Pick the target file or folder; the kind follows what was picked.</summary>
    private async void BrowseTarget()
    {
        try
        {
            var picked = _draft.Kind == ItemKinds.Folder
                ? await _host.PickFolder("选择文件夹")
                : await _host.PickFile("选择程序或文件");
            if (string.IsNullOrEmpty(picked)) return;

            TargetText = picked;   // the setter re-derives kind and name
        }
        catch (Exception) { }
    }

    private async void BrowseWorkdir()
    {
        try
        {
            var picked = await _host.PickFolder("选择工作目录");
            if (!string.IsNullOrEmpty(picked)) WorkingDirectory = picked;
        }
        catch (Exception) { }
    }

    /// <summary>Pick an image to use as the icon override.</summary>
    private async void PickIcon()
    {
        try
        {
            var picked = await _host.PickFile("选择图标图片");
            if (string.IsNullOrEmpty(picked)) return;

            _draft.Icon.Mode = IconModes.Override;
            _draft.Icon.OverrideSource = PathNorm.ToStored(picked);
            await RefreshPreviewAsync();
        }
        catch (Exception) { }
    }

    /// <summary>Drop an override and go back to the target's own icon.</summary>
    private async void ResetIcon()
    {
        _draft.Icon.Mode = IconModes.Auto;
        _draft.Icon.OverrideSource = null;
        await RefreshPreviewAsync();
        Toast?.Show("已恢复为使用目标图标");
    }

    /// <summary>Toast channel supplied by the shell, optional.</summary>
    public ToastService? Toast { get; init; }

    private async void FetchIcon()
    {
        IsFetchingIcon = true;
        try
        {
            var draft = BuildDraft();

            // A URL has no file to extract from, so "fetch the icon" means its site icon. This is
            // the only place the product touches the network, and only because a button was pressed.
            if (draft.Kind == ItemKinds.Url && UrlSafety.IsHttpUrl(draft.Target, out _))
            {
                var result = await FaviconService.FetchAsync(draft.Target, draft.Id, _paths)
                    .ConfigureAwait(true);

                if (result.Ok && result.StoredPath is not null)
                {
                    _draft.Icon.Mode = IconModes.Override;
                    _draft.Icon.OverrideSource = result.StoredPath;
                    await RefreshPreviewAsync();
                }

                Toast?.Show(result.Message);
                return;
            }

            var bitmap = await IconService.GetAsync(draft, 64).ConfigureAwait(true);
            IconPreview = bitmap;
        }
        catch (Exception)
        {
            Toast?.Show("获取图标失败");
        }
        finally { IsFetchingIcon = false; }
    }

    private void Commit()
    {
        var draft = BuildDraft();
        var errors = ItemValidator.Validate(draft, _isNew);
        if (errors.Count > 0)
        {
            ErrorText = string.Join("\n", errors.Select(e => "· " + e.Message));
            Outcome = EditOutcome.Rejected;
            return;   // the store is untouched: 1.x's commit gate
        }

        if (_isNew)
        {
            if (_tabId is null) return;
            _catalog.AddItems(_tabId, new[] { draft });
        }
        else
        {
            _catalog.ReplaceItem(draft);
        }

        CommittedItem = draft;
        Completed = true;
        Outcome = EditOutcome.Committed;
        CloseRequested?.Invoke();
    }
}
