using Galah2.Models;
using Galah2.Services.Interop;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// 商店应用（UWP / MSIX）：从 Windows 原生 <c>shell:AppsFolder</c> 枚举，
/// 以 <c>shell:AppsFolder\&lt;AUMID&gt;</c> 协议形式保存和启动。
/// </summary>
public sealed class StoreAppSource : ICatalogSource
{
    private static List<CatalogSeed>? _cache;
    private static readonly object Gate = new();

    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.StoreApps;

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;

            var apps = ShellAppsFolder.EnumerateStoreApps();
            _cache = apps
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(a => new CatalogSeed(
                    Category,
                    a.Name,
                    ItemKinds.Protocol,
                    "shell:AppsFolder\\" + a.Aumid,
                    Note: "商店应用"))
                .ToList();
            return _cache;
        }
    }

    /// <summary>清空缓存，以便安装/卸载商店应用后重新扫描。</summary>
    public static void ResetCache()
    {
        lock (Gate) { _cache = null; }
    }
}
