using Galah2.Models;
using Galah2.Services;

namespace Galah2.Persistence;

/// <summary>
/// Authoritative content library in memory + on disk. Every mutation is expressed as one command
/// = one atomic write (spec §1 write model, §2.4, §4.1); reads and renders never write (INV-1/INV-4).
/// Item and tab ids are immutable and never reused (INV-7).
/// </summary>
public sealed class CatalogStore
{
    private readonly JsonFileStore<CatalogDocument> _store;
    private bool _savePending;
    private string? _lastSaveError;

    /// <summary>Raised after a command has been committed to disk (view re-reads the snapshot).</summary>
    public event Action? Changed;

    /// <summary>Raised when a commit failed; the in-memory model keeps the change and can be retried (INV-9).</summary>
    public event Action<string>? SaveFailed;

    /// <summary>Current in-memory catalog (the view reads a snapshot of this; it must not mutate it).</summary>
    public CatalogDocument Current { get; private set; } = new();

    /// <summary>True when a newer-schema file refused loading — every write is blocked then.</summary>
    public bool IsSealed => _store.SealedForUpgrade;

    /// <summary>Message of the last failed commit (drives the retry banner), null when clean.</summary>
    public string? LastSaveError => _lastSaveError;

    /// <summary>Creates the store over the given data paths.</summary>
    public CatalogStore(AppDataPaths paths)
        => _store = new JsonFileStore<CatalogDocument>(paths.CatalogFile, CatalogDocument.CurrentSchema,
            CatalogDocument.CreateEmpty, "catalog");

    /// <summary>Store used by this domain (exposed for tests and for schema/refusal reporting).</summary>
    public JsonFileStore<CatalogDocument> FileStore => _store;

    /// <summary>Load with the documented fallback chain. Zero writes except first-run bootstrap.</summary>
    public LoadOutcome<CatalogDocument> Load()
    {
        var outcome = _store.Load();
        Current = outcome.Data;
        _savePending = false;
        _lastSaveError = null;
        return outcome;
    }

    /// <summary>Re-attempt the last failed commit (the "点击重试" action of the global banner).</summary>
    /// <returns>True when the pending change is now on disk.</returns>
    public bool RetryPendingSave()
    {
        if (!_savePending) return true;
        try
        {
            _store.Save(Current);
            _savePending = false;
            _lastSaveError = null;
            Changed?.Invoke();
            return true;
        }
        catch (PersistenceException ex)
        {
            _lastSaveError = ex.UserMessage;
            SaveFailed?.Invoke(ex.UserMessage);
            return false;
        }
    }

    /// <summary>
    /// Append a batch of items to the end of a tab in one commit (spec §2.4: a drop of n files is a
    /// single write). Ids and order values are assigned here.
    /// </summary>
    /// <returns>The ids of the added items, in insertion order.</returns>
    public IReadOnlyList<string> AddItems(string tabId, IEnumerable<CatalogItem> drafts)
    {
        var tab = RequireTab(tabId);
        var order = tab.Items.Count == 0 ? 0 : tab.Items.Max(i => i.Order) + 1;
        var added = new List<string>();
        foreach (var draft in drafts)
        {
            var item = draft.Clone();
            if (string.IsNullOrEmpty(item.Id)) item.Id = Ids.NewItemId();
            item.Order = order++;
            tab.Items.Add(item);
            added.Add(item.Id);
        }
        Commit();
        return added;
    }

    /// <summary>Replace the item with the same id in place; id and order are preserved (spec §4.1).</summary>
    public void ReplaceItem(CatalogItem updated)
    {
        var hit = Current.FindItem(updated.Id);
        if (hit is null) return;
        var (tab, existing) = hit.Value;
        var copy = updated.Clone();
        copy.Id = existing.Id;        // immutable (INV-7)
        copy.Order = existing.Order;  // unchanged by edits (INV-7)
        copy.Kind = existing.Kind;    // kind is immutable (spec §4.1)
        tab.Items[tab.Items.IndexOf(existing)] = copy;
        Commit();
    }

    /// <summary>Delete one item; no recycle bin, rolling backups are the safety net (spec §4.1).</summary>
    public void DeleteItem(string itemId)
    {
        var hit = Current.FindItem(itemId);
        if (hit is null) return;
        hit.Value.Tab.Items.Remove(hit.Value.Item);
        Renumber(hit.Value.Tab);
        Commit();
    }

    /// <summary>
    /// Delete several items with ONE commit (maintenance batch, spec INV-2: one user action,
    /// one write). Unknown ids are ignored so a stale preview list cannot corrupt anything.
    /// </summary>
    /// <param name="itemIds">Ids to remove.</param>
    /// <returns>How many items were actually removed.</returns>
    public int DeleteItems(IReadOnlyList<string> itemIds)
    {
        var removed = 0;
        var touched = new List<CatalogTab>();
        foreach (var id in itemIds)
        {
            var hit = Current.FindItem(id);
            if (hit is null) continue;
            hit.Value.Tab.Items.Remove(hit.Value.Item);
            if (!touched.Contains(hit.Value.Tab)) touched.Add(hit.Value.Tab);
            removed++;
        }
        if (removed == 0) return 0;
        foreach (var tab in touched) Renumber(tab);
        Commit();
        return removed;
    }

    /// <summary>
    /// Re-order one category's items by display name (locale-aware, case-insensitive) with a
    /// single commit. Ids and payloads are untouched; only Order changes.
    /// </summary>
    /// <param name="tabId">Category to sort.</param>
    /// <returns>Number of items whose position changed.</returns>
    public int SortTabItemsByName(string tabId)
    {
        var tab = RequireTab(tabId);
        var before = tab.Items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal)
            .Select(i => i.Id).ToList();
        var sorted = tab.Items
            .OrderBy(i => i.Name, NameOrder.Sort)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
        var changed = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].Order = i;
            if (!string.Equals(before[i], sorted[i].Id, StringComparison.Ordinal)) changed++;
        }
        if (changed == 0) return 0;   // already sorted: no write at all
        Commit();
        return changed;
    }

    /// <summary>
    /// Swap the whole library for an imported document (migration "replace" mode). One commit;
    /// the rolling backups are the undo path, exactly like every other destructive edit here.
    /// </summary>
    /// <param name="document">Already-sanitized document.</param>
    public void ReplaceWith(CatalogDocument document)
    {
        Current = document;
        Commit();
    }

    /// <summary>Move an item left/right inside its tab; the swap lands immediately (spec §4.1).</summary>
    public void MoveItem(string itemId, int delta)
    {
        var hit = Current.FindItem(itemId);
        if (hit is null) return;
        var tab = hit.Value.Tab;
        var ordered = Current.ItemsInOrder(tab).ToList();
        var index = ordered.FindIndex(i => i.Id == itemId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        Commit();
    }

    /// <summary>
    /// Move an item to an explicit position inside its own category (drag and drop reordering).
    /// The id and the payload are untouched; only the order changes, and one commit lands the
    /// whole move (spec §1: one user action, one write).
    /// </summary>
    /// <param name="itemId">Item to move.</param>
    /// <param name="targetIndex">Destination index inside the category (0-based, clamped).</param>
    /// <returns>True when the order actually changed and the library was written.</returns>
    public bool MoveItemToIndex(string itemId, int targetIndex)
    {
        var hit = Current.FindItem(itemId);
        if (hit is null) return false;
        var tab = hit.Value.Tab;
        var ordered = Current.ItemsInOrder(tab).ToList();
        var from = ordered.FindIndex(i => i.Id == itemId);
        if (from < 0) return false;

        var to = Math.Min(Math.Max(targetIndex, 0), ordered.Count - 1);
        if (to == from) return false;

        var moved = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(to, moved);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        Commit();
        return true;
    }

    /// <summary>Move an item to the end of another tab (kind/target are untouched, id stays).</summary>
    public void MoveItemToTab(string itemId, string targetTabId)
    {
        if (itemId == targetTabId) return;
        var hit = Current.FindItem(itemId);
        var target = Current.FindTab(targetTabId);
        if (hit is null || target is null || hit.Value.Tab.Id == target.Id) return;
        var item = hit.Value.Item;
        hit.Value.Tab.Items.Remove(item);
        Renumber(hit.Value.Tab);
        item.Order = target.Items.Count == 0 ? 0 : target.Items.Max(i => i.Order) + 1;
        target.Items.Add(item);
        Commit();
    }

    /// <summary>Create a tab; duplicate display names get a numeric suffix (design §3.2).</summary>
    public CatalogTab AddTab(string name)
    {
        var tab = new CatalogTab
        {
            Id = Ids.NewTabId(),
            Name = UniqueTabName(name),
            Order = Current.Tabs.Count == 0 ? 0 : Current.Tabs.Max(t => t.Order) + 1,
        };
        Current.Tabs.Add(tab);
        Commit();
        return tab;
    }

    /// <summary>Rename a tab (one commit).</summary>
    public void RenameTab(string tabId, string name)
    {
        var tab = RequireTab(tabId);
        tab.Name = UniqueTabName(name, tabId);
        Commit();
    }

    /// <summary>Delete a tab with all of its items (the caller confirms the item count, spec §4.1).</summary>
    public void DeleteTab(string tabId)
    {
        var tab = Current.FindTab(tabId);
        if (tab is null) return;
        Current.Tabs.Remove(tab);
        RenumberTabs();
        Commit();
    }

    /// <summary>Move a tab left/right in the strip (one commit).</summary>
    public void MoveTab(string tabId, int delta)
    {
        var ordered = Current.TabsInOrder().ToList();
        var index = ordered.FindIndex(t => t.Id == tabId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ordered.Count) return;
        (ordered[index].Order, ordered[target].Order) = (ordered[target].Order, ordered[index].Order);
        Commit();
    }

    /// <summary>
    /// Move a tab to an absolute position (drag a chip onto another chip: the drop target IS
    /// the destination slot). Orders are renumbered 0..n-1 afterwards, so a long drag cannot
    /// leave gaps or duplicates behind. Unknown ids and no-op moves write nothing.
    /// </summary>
    /// <param name="tabId">Tab to move.</param>
    /// <param name="targetIndex">Destination index in the ordered list (clamped, 0-based).</param>
    /// <returns>True when the order actually changed and the library was written.</returns>
    public bool MoveTabToIndex(string tabId, int targetIndex)
    {
        var ordered = Current.TabsInOrder().ToList();
        var from = ordered.FindIndex(t => t.Id == tabId);
        if (from < 0) return false;

        var to = Math.Min(Math.Max(targetIndex, 0), ordered.Count - 1);
        if (to == from) return false;

        var moved = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(to, moved);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
        Commit();
        return true;
    }

    /// <summary>Number of items in a tab (used by the delete-confirmation text).</summary>
    public int CountItems(string tabId) => Current.FindTab(tabId)?.Items.Count ?? 0;

    private string UniqueTabName(string name, string? keepId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "未命名" : name.Trim();
        var taken = Current.Tabs.Where(t => t.Id != keepId).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(baseName)) return baseName;
        var n = 2;
        while (taken.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    private CatalogTab RequireTab(string tabId) =>
        Current.FindTab(tabId) ?? throw new InvalidOperationException("分类不存在: " + tabId);

    private static void Renumber(CatalogTab tab)
    {
        var ordered = tab.Items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
    }

    private void RenumberTabs()
    {
        var ordered = Current.TabsInOrder().ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Order = i;
    }

    private void Commit()
    {
        try
        {
            if (_store.Save(Current)) _savePending = false;
            _lastSaveError = null;
            Changed?.Invoke();
        }
        catch (PersistenceException ex)
        {
            // INV-9: keep the model, surface the failure, allow retry. Never drop the change silently.
            _savePending = true;
            _lastSaveError = ex.UserMessage;
            SaveFailed?.Invoke(ex.UserMessage);
            Changed?.Invoke();
        }
    }
}
