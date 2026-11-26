using System.Text.Json;
using System.Text.Json.Serialization;
using Galah2.Models;
using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>
/// The portable bundle shape of "导出/导入" (Sprint 2 / ROADMAP D1). Deliberately versioned and
/// self-describing: a future Galah must be able to read an older bundle, and a hand-edited or
/// foreign JSON must fail loudly rather than half-apply.
/// </summary>
public sealed class MigrationBundle
{
    /// <summary>Bundle format version written by this build.</summary>
    public const int BundleSchema = 1;

    [JsonPropertyName("bundleSchema")]
    public int Schema { get; set; } = BundleSchema;

    [JsonPropertyName("product")]
    public string Product { get; set; } = "Galah";

    [JsonPropertyName("exportedAt")]
    public string ExportedAt { get; set; } = "";

    [JsonPropertyName("catalog")]
    public CatalogDocument Catalog { get; set; } = new();

    [JsonPropertyName("settings")]
    public SettingsDocument Settings { get; set; } = new();
}

/// <summary>Outcome of an import: what landed, what was dropped, and why.</summary>
/// <param name="Tabs">Categories created or merged into.</param>
/// <param name="ItemsAdded">Items accepted.</param>
/// <param name="ItemsDropped">Items rejected (unknown kind, empty name/target).</param>
/// <param name="Replaced">True when the existing catalog was replaced rather than merged.</param>
public sealed record ImportOutcome(int Tabs, int ItemsAdded, int ItemsDropped, bool Replaced);

/// <summary>
/// Export/import of the whole library as ONE json file next to the data (no dialogs needed for
/// export; import picks a file). Writing an import always goes through the normal atomic+backup
/// path, so a bad bundle can never leave a half-written catalog.
/// </summary>
public static class MigrationService
{
    // TypeInfoResolver points at the source-generated context: reflection-based serialization
    // throws JsonSerializerIsReflectionDisabled under NativeAOT, and import runs before any UI
    // exists, so the failure would look like the app refusing to start.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = JsonContext.Default,
    };

    /// <summary>Default export drop folder (portable: it travels with GalahData).</summary>
    /// <param name="paths">Data directory of the running instance.</param>
    public static string ExportDirectory(AppDataPaths paths) => Path.Combine(paths.Root, "exports");

    /// <summary>
    /// Write the current library to <c>GalahData/exports/galah-export-yyyymmdd-HHmmss.json</c>.
    /// </summary>
    /// <param name="paths">Data directory.</param>
    /// <param name="catalog">Live catalog snapshot.</param>
    /// <param name="settings">Live settings snapshot.</param>
    /// <returns>Absolute path of the bundle.</returns>
    public static string Export(AppDataPaths paths, CatalogDocument catalog, SettingsDocument settings)
    {
        var dir = ExportDirectory(paths);
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var file = Path.Combine(dir, $"galah-export-{stamp}.json");
        var suffix = 1;
        while (File.Exists(file))
            file = Path.Combine(dir, $"galah-export-{stamp}-{suffix++}.json");

        var bundle = new MigrationBundle
        {
            Schema = MigrationBundle.BundleSchema,
            Product = "Galah",
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Catalog = catalog.CloneTabs(),
            Settings = settings.Clone(),
        };
        // JsonOptions carries the AOT-safe TypeInfoResolver plus UnsafeRelaxedJsonEscaping, so
        // Chinese text stays readable in the exported file (the source-generation attribute has
        // no Encoder property). Using JsonContext.Default.MigrationBundle directly used the
        // default encoder and escaped every non-ASCII character as \uXXXX.
        File.WriteAllText(file, JsonSerializer.Serialize(bundle, JsonOptions));
        return file;
    }

    /// <summary>
    /// Read and validate a bundle. Throws <see cref="MigrationException"/> on anything that is not
    /// a Galah bundle (wrong product, unreadable json, newer schema).
    /// </summary>
    /// <param name="file">Path chosen by the user.</param>
    public static MigrationBundle ReadBundle(string file)
    {
        if (!File.Exists(file)) throw new MigrationException("文件不存在");

        MigrationBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<MigrationBundle>(
                File.ReadAllText(file), JsonOptions);
        }
        catch (Exception ex)
        {
            throw new MigrationException("不是有效的 Galah 备份文件：" + ex.Message);
        }

        if (bundle is null) throw new MigrationException("文件内容为空");
        if (!string.Equals(bundle.Product, "Galah", StringComparison.OrdinalIgnoreCase))
            throw new MigrationException("不是 Galah 的导出文件");
        if (bundle.Schema > MigrationBundle.BundleSchema)
            throw new MigrationException("该文件由更新的 Galah 版本导出，请升级后再导入");
        return bundle;
    }

    /// <summary>
    /// Apply a bundle to the live stores. Merge keeps the current library and appends missing
    /// categories/items; replace swaps the catalog wholesale. Items are re-idd on merge so an
    /// import can never overwrite an existing entry by id collision.
    /// </summary>
    /// <param name="catalogStore">Catalog store to mutate (commits once).</param>
    /// <param name="settingsStore">Settings store to update.</param>
    /// <param name="bundle">Validated bundle.</param>
    /// <param name="replace">True to replace the catalog instead of merging.</param>
    public static ImportOutcome Apply(CatalogStore catalogStore, SettingsStore settingsStore,
        MigrationBundle bundle, bool replace)
    {
        var dropped = 0;
        var added = 0;
        var clean = Sanitize(bundle.Catalog, out dropped);

        if (replace)
        {
            catalogStore.ReplaceWith(clean);
            added = clean.Tabs.Sum(t => t.Items.Count);
        }
        else
        {
            foreach (var tab in clean.TabsInOrder())
            {
                var existing = catalogStore.Current.Tabs.FirstOrDefault(
                    t => string.Equals(t.Name, tab.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    var created = catalogStore.AddTab(tab.Name);
                    catalogStore.AddItems(created.Id, tab.Items);
                    added += tab.Items.Count;
                }
                else
                {
                    var fresh = tab.Items
                        .Where(i => !existing.Items.Any(e => Same(e, i)))
                        .ToList();
                    if (fresh.Count > 0)
                    {
                        catalogStore.AddItems(existing.Id, fresh);
                        added += fresh.Count;
                    }
                }
            }
        }

        // Import replaces the whole settings snapshot (null-safe when a hand-edited bundle omits it).
        var incoming = (bundle.Settings ?? new SettingsDocument()).Sanitized();
        settingsStore.Replace(incoming);
        return new ImportOutcome(clean.Tabs.Count, added, dropped, replace);
    }

    /// <summary>Reject items the current build cannot represent (forward-safe import).</summary>
    private static CatalogDocument Sanitize(CatalogDocument? source, out int dropped)
    {
        if (source is null)
        {
            dropped = 0;
            return new CatalogDocument();
        }

        // 归一化外部 JSON 中可能出现的字段级 null，避免后续 Clone/遍历空引用崩溃。
        source.ProfileId ??= "local";
        source.Tabs ??= new List<CatalogTab>();
        for (var t = source.Tabs.Count - 1; t >= 0; t--)
        {
            var tab = source.Tabs[t];
            if (tab is null) { source.Tabs.RemoveAt(t); continue; }
            tab.Id ??= "";
            tab.Name ??= "";
            tab.Items ??= new List<CatalogItem>();
            for (var i = tab.Items.Count - 1; i >= 0; i--)
            {
                var item = tab.Items[i];
                if (item is null) { tab.Items.RemoveAt(i); continue; }
                item.Id ??= "";
                item.Kind ??= "";
                item.Name ??= "";
                item.Target ??= "";
                item.Icon ??= new IconReference();
                item.Icon.Mode ??= IconModes.Auto;
            }
        }

        var result = source.CloneTabs();
        var count = 0;
        foreach (var tab in result.Tabs)
        {
            var keep = new List<CatalogItem>();
            var order = 0;
            foreach (var item in tab.Items.OrderBy(i => i.Order))
            {
                if (!ItemKinds.IsKnown(item.Kind) ||
                    string.IsNullOrWhiteSpace(item.Name) ||
                    string.IsNullOrWhiteSpace(item.Target))
                {
                    count++;
                    continue;
                }
                item.Order = order++;
                item.Icon ??= new IconReference { Mode = IconModes.Auto };
                keep.Add(item);
            }
            tab.Items = keep;
        }
        result.Schema = CatalogDocument.CurrentSchema;
        dropped = count;
        return result;
    }

    private static bool Same(CatalogItem a, CatalogItem b) =>
        a.Kind == b.Kind
        && string.Equals(a.Target.Trim(), b.Target.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals((a.Args ?? "").Trim(), (b.Args ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Raised for a bundle that must not be applied at all.</summary>
/// <param name="message">User-facing reason.</param>
public sealed class MigrationException(string message) : Exception(message);
