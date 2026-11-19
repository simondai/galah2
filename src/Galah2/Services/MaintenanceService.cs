using Galah2.Models;
using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>One entry that a maintenance action would touch (Sprint 2 / ROADMAP C1-C3).</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">Item kind.</param>
/// <param name="Target">Stored target (for the reason line).</param>
/// <param name="TabName">Category the item lives in.</param>
/// <param name="Reason">Why it was flagged.</param>
public sealed record MaintenanceFinding(
    string Id,
    string Name,
    string Kind,
    string Target,
    string TabName,
    string Reason);

/// <summary>A duplicate group: the first entry is kept, the rest are removable.</summary>
/// <param name="Key">Normalised launchable key.</param>
/// <param name="Keep">Entry that stays.</param>
/// <param name="Extras">Entries a cleanup would remove.</param>
public sealed record DuplicateGroup(string Key, MaintenanceFinding Keep, IReadOnlyList<MaintenanceFinding> Extras);

/// <summary>Read-only snapshot of what maintenance would do. Nothing here writes anything.</summary>
/// <param name="Invalid">Items whose target no longer resolves.</param>
/// <param name="Groups">Duplicate launchable groups (only groups with extras).</param>
/// <param name="TotalItems">Item count across all categories.</param>
/// <param name="UnsortedTabs">Categories whose visible order is not by name.</param>
public sealed record MaintenancePreview(
    IReadOnlyList<MaintenanceFinding> Invalid,
    IReadOnlyList<DuplicateGroup> Groups,
    int TotalItems,
    IReadOnlyList<string> UnsortedTabs);

/// <summary>
/// Library maintenance (Sprint 2): detect invalid entries, duplicate launchables and unsorted
/// categories, then apply exactly one commit per user action. Detection is pure and side-effect
/// free so it is unit-testable and safe to run from a preview screen.
/// </summary>
public static class MaintenanceService
{
    /// <summary>Scan the catalog without touching it.</summary>
    /// <param name="document">Live catalog snapshot.</param>
    public static MaintenancePreview Preview(CatalogDocument document)
    {
        var invalid = new List<MaintenanceFinding>();
        var byKey = new Dictionary<string, List<MaintenanceFinding>>(StringComparer.OrdinalIgnoreCase);
        var unsorted = new List<string>();
        var total = 0;

        foreach (var tab in document.TabsInOrder())
        {
            var ordered = tab.Items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
            total += ordered.Count;

            var names = ordered.Select(i => i.Name).ToList();
            var sortedNames = names
                .OrderBy(n => n, NameOrder.Sort)
                .ToList();
            if (!names.SequenceEqual(sortedNames, StringComparer.Ordinal))
                unsorted.Add(tab.Name);

            foreach (var item in ordered)
            {
                var finding = new MaintenanceFinding(
                    item.Id, item.Name, item.Kind, item.Target, tab.Name,
                    Reason: LauncherService.LooksValid(item) ? "" : "目标不存在或协议不受支持");

                if (finding.Reason.Length > 0)
                {
                    invalid.Add(finding);
                    continue;   // a dead entry is not worth grouping
                }

                var key = KeyOf(item);
                if (!byKey.TryGetValue(key, out var list))
                    byKey[key] = list = new List<MaintenanceFinding>();
                list.Add(finding);
            }
        }

        var groups = byKey.Values
            .Where(l => l.Count > 1)
            .Select(l => new DuplicateGroup(l[0].Kind + "|" + l[0].Target, l[0], l.Skip(1).ToList()))
            .OrderBy(g => g.Keep.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MaintenancePreview(invalid, groups, total, unsorted);
    }

    /// <summary>Remove the flagged invalid entries (one commit).</summary>
    /// <param name="store">Catalog store.</param>
    /// <param name="preview">Preview produced by <see cref="Preview"/>.</param>
    public static int RemoveInvalid(CatalogStore store, MaintenancePreview preview) =>
        store.DeleteItems(preview.Invalid.Select(i => i.Id).ToList());

    /// <summary>Remove all but the first entry of every duplicate group (one commit).</summary>
    /// <param name="store">Catalog store.</param>
    /// <param name="preview">Preview produced by <see cref="Preview"/>.</param>
    public static int RemoveDuplicates(CatalogStore store, MaintenancePreview preview) =>
        store.DeleteItems(preview.Groups.SelectMany(g => g.Extras).Select(e => e.Id).ToList());

    /// <summary>Sort every category by name; already-sorted categories cost no write.</summary>
    /// <param name="store">Catalog store.</param>
    /// <param name="preview">Preview produced by <see cref="Preview"/>.</param>
    public static int SortAllTabs(CatalogStore store, MaintenancePreview preview)
    {
        var changed = 0;
        foreach (var tabName in preview.UnsortedTabs)
        {
            var tab = store.Current.Tabs.FirstOrDefault(t => t.Name == tabName);
            if (tab is not null) changed += store.SortTabItemsByName(tab.Id);
        }
        return changed;
    }

    /// <summary>
    /// Drop every derived icon bitmap so the next render re-extracts (ROADMAP C4). Only the
    /// cache files are removed: user overrides (icons/override) and the tray seed (galah.ico)
    /// are never touched.
    /// </summary>
    /// <param name="paths">Data directory of the running instance.</param>
    public static int ClearIconCache(AppDataPaths paths)
    {
        var dir = paths.IconsDirectory;
        if (!Directory.Exists(dir)) return 0;
        var cleared = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.png"))
        {
            try
            {
                File.Delete(file);
                cleared++;
            }
            catch (Exception)
            {
                // A locked bitmap (mid-render) is skipped; the cache is disposable by contract.
            }
        }
        return cleared;
    }

    /// <summary>Launchable identity used for duplicate detection.</summary>
    private static string KeyOf(CatalogItem item) =>
        item.Kind + "|" + item.Target.Trim() + "|" + (item.Args ?? "").Trim();
}
