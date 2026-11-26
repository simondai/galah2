using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Galah2.Services;

namespace Galah2.ViewModels;

/// <summary>剪贴板历史列表中的单条纯文本卡片。</summary>
public sealed class ClipboardItemViewModel : ObservableObject
{
    public ClipboardItemViewModel(ClipboardEntry entry, Action<string> onTogglePin, Action<string> onDelete)
    {
        Id = entry.Id;
        Text = entry.Text;
        IsPinned = entry.IsPinned;
        Preview = BuildPreview(entry.Text);
        MetaText = FormatTime(entry.CopiedAt) + $" · {entry.Text.Length} 字";
        SearchKey = entry.Text.ToLowerInvariant();
        TogglePinCommand = new RelayCommand(() => onTogglePin(Id));
        DeleteCommand = new RelayCommand(() => onDelete(Id));
    }

    public string Id { get; }
    public string Text { get; }
    public string Preview { get; }
    public string MetaText { get; }
    public string SearchKey { get; }
    public bool IsPinned { get; }
    public string PinTooltip => IsPinned ? "取消置顶" : "置顶";
    public string PinGlyph => IsPinned ? "★" : "☆";

    public ICommand TogglePinCommand { get; }
    public ICommand DeleteCommand { get; }

    private static string BuildPreview(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > 320) trimmed = trimmed[..320] + "…";
        return trimmed;
    }

    private static string FormatTime(DateTimeOffset copiedAt)
    {
        var diff = DateTimeOffset.Now - copiedAt;
        if (diff.TotalSeconds < 60) return "刚刚";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        return copiedAt.ToString("MM-dd HH:mm");
    }
}

/// <summary>剪贴板历史弹窗的视图模型（支持搜索、置顶筛选、键盘选择与自动回贴）。</summary>
public sealed class ClipboardHistoryViewModel : ObservableObject, IDisposable
{
    private readonly ClipboardHistoryService _service;
    private readonly Func<bool> _getAutoPaste;
    private IntPtr _targetWindow;
    private string _filter = "";
    private bool _pinnedOnly;
    private ClipboardItemViewModel? _selectedItem;

    public ClipboardHistoryViewModel(ClipboardHistoryService service, Func<bool> getAutoPaste)
    {
        _service = service;
        _getAutoPaste = getAutoPaste;
        _service.Changed += OnServiceChanged;

        ShowAllCommand = new RelayCommand(() => PinnedOnly = false);
        ShowPinnedCommand = new RelayCommand(() => PinnedOnly = true);
        ClearCommand = new RelayCommand(() => _service.ClearUnpinned());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());

        Refresh();
    }

    public event Action? CloseRequested;

    public ObservableCollection<ClipboardItemViewModel> Items { get; } = new();

    public ICommand ShowAllCommand { get; }
    public ICommand ShowPinnedCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CloseCommand { get; }

    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) Refresh(); }
    }

    public bool PinnedOnly
    {
        get => _pinnedOnly;
        set
        {
            if (!Set(ref _pinnedOnly, value)) return;
            Raise(nameof(ShowAllActive));
            Refresh();
        }
    }

    public bool ShowAllActive => !PinnedOnly;

    public ClipboardItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => Set(ref _selectedItem, value);
    }

    public bool IsEmpty => Items.Count == 0;

    public string EmptyText => string.IsNullOrWhiteSpace(Filter)
        ? (PinnedOnly ? "暂无置顶条目" : "暂无剪贴板记录，复制任意文本即可自动收集")
        : "没有匹配的剪贴板记录";

    public string CountLabel => $"{Items.Count} 条";

    public string FooterHint => _getAutoPaste()
        ? "Enter / 双击 自动粘贴 · ↑↓ 选择 · Esc 关闭 · 退出自动清空"
        : "Enter / 双击 复制 · ↑↓ 选择 · Esc 关闭 · 退出自动清空";

    /// <summary>每次呼出前更新目标窗口句柄并刷新列表与相对时间。</summary>
    public void PrepareForShow(IntPtr targetWindow)
    {
        _targetWindow = targetWindow;
        Raise(nameof(FooterHint));
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = _service.GetSnapshot();
        var query = _filter.Trim().ToLowerInvariant();
        var previousId = _selectedItem?.Id;

        Items.Clear();
        foreach (var entry in snapshot)
        {
            if (_pinnedOnly && !entry.IsPinned) continue;
            var vm = new ClipboardItemViewModel(entry, _service.TogglePin, _service.Remove);
            if (query.Length > 0 && !vm.SearchKey.Contains(query, StringComparison.Ordinal)) continue;
            Items.Add(vm);
        }

        SelectedItem = Items.FirstOrDefault(i => i.Id == previousId) ?? Items.FirstOrDefault();
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyText));
        Raise(nameof(CountLabel));
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;
        var idx = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        var next = Math.Clamp(idx + delta, 0, Items.Count - 1);
        SelectedItem = Items[next];
    }

    public void CommitSelected() => CommitItem(SelectedItem);

    public void CommitItem(ClipboardItemViewModel? item)
    {
        if (item is null) return;
        var target = _targetWindow;
        var autoPaste = _getAutoPaste();
        CloseRequested?.Invoke();
        _service.CopyAndPaste(item.Text, target, autoPaste);
    }

    private void OnServiceChanged()
    {
        Dispatcher.UIThread.Post(Refresh);
    }

    public void Dispose()
    {
        _service.Changed -= OnServiceChanged;
    }
}
