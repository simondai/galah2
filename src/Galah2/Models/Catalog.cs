using System.Text.Json.Serialization;

namespace Galah2.Models;

/// <summary>
/// Item kind constants. Kinds are stored as plain strings so an unknown value written by a
/// future schema never makes the whole catalog unreadable (forward compatibility, design §3.3).
/// </summary>
public static class ItemKinds
{
    public const string App = "app";
    public const string File = "file";
    public const string Folder = "folder";
    public const string Url = "url";

    /// <summary>Windows protocol link (ms-settings:, shell:, search-ms:...). Launched via ShellExecute.</summary>
    public const string Protocol = "protocol";

    /// <summary>Returns true when <paramref name="kind"/> is one of the five supported kinds.</summary>
    public static bool IsKnown(string? kind) =>
        kind is App or File or Folder or Url or Protocol;
}

/// <summary>Icon resolution mode for an item (design §3.2: icon = { mode, override }).</summary>
public static class IconModes
{
    /// <summary>Extract the icon from the item target lazily.</summary>
    public const string Auto = "auto";

    /// <summary>Use <see cref="IconReference.OverrideSource"/> as the icon source (image file or "path,index").</summary>
    public const string Override = "override";
}

/// <summary>Icon pointer of an item. Serialized as <c>{ "mode": "...", "override": null }</c>.</summary>
public sealed class IconReference
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = IconModes.Auto;

    [JsonPropertyName("override")]
    public string? OverrideSource { get; set; }

    /// <summary>Deep copy used when building edit snapshots.</summary>
    public IconReference Clone() => new() { Mode = Mode, OverrideSource = OverrideSource };
}

/// <summary>A single launcher entry (design §3.2 items[]).</summary>
public sealed class CatalogItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = ItemKinds.App;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("args")]
    public string? Args { get; set; }

    [JsonPropertyName("workdir")]
    public string? Workdir { get; set; }

    [JsonPropertyName("runAsAdmin")]
    public bool RunAsAdmin { get; set; }

    [JsonPropertyName("icon")]
    public IconReference Icon { get; set; } = new();

    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>Full deep copy — the value snapshot used by the edit dialog and by launches.</summary>
    public CatalogItem Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        Target = Target,
        Args = Args,
        Workdir = Workdir,
        RunAsAdmin = RunAsAdmin,
        Icon = Icon.Clone(),
        Order = Order,
    };
}

/// <summary>A tab (= category) holding items (design §3.2 tabs[]).</summary>
public sealed class CatalogTab
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("items")]
    public List<CatalogItem> Items { get; set; } = new();

    /// <summary>Deep copy of the tab including all items.</summary>
    public CatalogTab Clone() => new()
    {
        Id = Id,
        Name = Name,
        Order = Order,
        Items = Items.Select(i => i.Clone()).ToList(),
    };
}

/// <summary>Root document of catalog.json — the authoritative content library.</summary>
public sealed class CatalogDocument : Persistence.ISchemaBearing
{
    /// <summary>Schema version written by this build (design §3.2).</summary>
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "local";

    [JsonPropertyName("tabs")]
    public List<CatalogTab> Tabs { get; set; } = new();

    /// <summary>Tabs in ascending <c>order</c>; the stored order value is authoritative (spec §5.2: never "repair").</summary>
    public IEnumerable<CatalogTab> TabsInOrder() => Tabs.OrderBy(t => t.Order).ThenBy(t => t.Id, StringComparer.Ordinal);

    /// <summary>Items of a tab in ascending <c>order</c>.</summary>
    public IEnumerable<CatalogItem> ItemsInOrder(CatalogTab tab) => tab.Items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal);

    /// <summary>New catalog with one empty tab, used on a genuinely first run and after total data loss.</summary>
    public static CatalogDocument CreateEmpty()
    {
        var tab = new CatalogTab { Id = Ids.NewTabId(), Name = "默认", Order = 0 };
        return new CatalogDocument { Tabs = { tab } };
    }

    /// <summary>Deep copy of every tab - the snapshot handed to export and to undo-safe paths.</summary>
    public CatalogDocument CloneTabs() => new()
    {
        Schema = Schema,
        ProfileId = ProfileId,
        Tabs = Tabs.Select(t => t.Clone()).ToList(),
    };

    /// <summary>Find a tab by id (null when missing).</summary>
    public CatalogTab? FindTab(string tabId) => Tabs.FirstOrDefault(t => t.Id == tabId);

    /// <summary>Find an item by id across all tabs; also returns its owning tab.</summary>
    public (CatalogTab Tab, CatalogItem Item)? FindItem(string itemId)
    {
        foreach (var tab in Tabs)
        {
            var item = tab.Items.FirstOrDefault(i => i.Id == itemId);
            if (item is not null) return (tab, item);
        }
        return null;
    }
}

/// <summary>
/// Identifier factory. Ids are never reused and never renumbered (INV-7): a random suffix makes
/// collisions with historical (deleted) ids practically impossible, and deleted ids are never handed out again.
/// </summary>
public static class Ids
{
    private static long _counter;

    /// <summary>New stable item id, e.g. <c>i-3f2b1c9a4d7e0518</c>.</summary>
    public static string NewItemId() => "i-" + Tail();

    /// <summary>New stable tab id, e.g. <c>t-3f2b1c9a4d7e0518</c>.</summary>
    public static string NewTabId() => "t-" + Tail();

    private static string Tail()
    {
        var n = Interlocked.Increment(ref _counter);
        var guid = Guid.NewGuid().ToString("N");
        // 取计数器低 8 位十六进制 + 8 位随机 GUID = 固定 16 位十六进制，既防同毫秒碰撞又保持定长。
        var counterHex = n.ToString("x16");
        var offset = (int)(n & 7);
        return counterHex[8..] + guid[offset..(offset + 8)];
    }
}
