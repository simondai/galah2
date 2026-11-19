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

    public EditItemViewModel(CatalogStore catalog, string? tabId, CatalogItem? existing)
    {
        _catalog = catalog;
        _tabId = tabId;
        _isNew = existing is null;
        _draft = existing?.Clone() ?? new CatalogItem
        {
            Id = "draft",
            Kind = ItemKinds.App,
        };

        Title = _isNew ? "添加快捷方式" : "编辑快捷方式";
        BrowseTargetCommand = new RelayCommand(() => { });
        BrowseWorkdirCommand = new RelayCommand(() => { });
        PickIconCommand = new RelayCommand(() => { });
        FetchIconCommand = new RelayCommand(() => { });
        ResetIconCommand = new RelayCommand(() => { });
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
                Name = KindInference.DeriveName(_draft.Kind, value);
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
        try
        {
            var draft = BuildDraft();
            IconPreview = string.IsNullOrWhiteSpace(draft.Target)
                ? null
                : await IconService.GetAsync(draft, 32).ConfigureAwait(true);
        }
        catch (Exception) { IconPreview = null; }
    }

    private async Task FetchIconAsync()
    {
        IsFetchingIcon = true;
        try
        {
            var draft = BuildDraft();
            var bitmap = await IconService.GetAsync(draft, 64).ConfigureAwait(true);
            IconPreview = bitmap;
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
