namespace Galah2.Services.CatalogSources;

/// <summary>
/// 聚合「添加快捷方式」的全部 6 个数据源，对外统一枚举与中文标签。
/// </summary>
public sealed class CatalogSourceService
{
    private readonly ICatalogSource[] _sources;

    /// <summary>Build the service with the default 6-category source set.</summary>
    public CatalogSourceService()
    {
        var installed = new InstalledAppSource();
        _sources =
        [
            new ShellFolderSource(),
            new SystemToolsSource(),
            new InstalledAppSourceAdapter(installed),
            new StoreAppSource(),
            new QuickActionSource(),
            new SettingsPageSource(),
        ];
        Installed = installed;
    }

    /// <summary>The desktop-app feed (exposed for cache reset after add/remove cycles).</summary>
    public InstalledAppSource Installed { get; }

    /// <summary>Categories in display order.</summary>
    public IReadOnlyList<SeedCategory> Categories { get; } =
    [
        SeedCategory.ShellFolders,
        SeedCategory.SystemTools,
        SeedCategory.InstalledApps,
        SeedCategory.StoreApps,
        SeedCategory.QuickActions,
        SeedCategory.SettingsPages,
    ];

    /// <summary>Chinese label of a category (picker tab text).</summary>
    public static string LabelOf(SeedCategory category) => category switch
    {
        SeedCategory.ShellFolders => "标准文件夹",
        SeedCategory.SystemTools => "系统应用",
        SeedCategory.InstalledApps => "桌面应用",
        SeedCategory.StoreApps => "商店应用",
        SeedCategory.QuickActions => "快捷操作",
        SeedCategory.SettingsPages => "设置项",
        _ => category.ToString(),
    };

    /// <summary>Enumerate one category. Never throws; an unavailable source yields nothing.</summary>
    public IReadOnlyList<CatalogSeed> Enumerate(SeedCategory category)
    {
        foreach (var source in _sources)
        {
            if (source.Category != category) continue;
            try
            {
                return source.Enumerate().Select(Normalize).ToList();
            }
            catch (Exception)
            {
                return Array.Empty<CatalogSeed>();
            }
        }
        return Array.Empty<CatalogSeed>();
    }

    private static CatalogSeed Normalize(CatalogSeed seed)
    {
        var iconOverride = string.IsNullOrWhiteSpace(seed.IconOverride)
            ? null
            : seed.IconOverride.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
                ? seed.IconOverride
                : Persistence.PathNorm.ToStored(seed.IconOverride);

        if (seed.Kind == Models.ItemKinds.Protocol)
            return seed with { IconOverride = iconOverride };

        return seed with
        {
            Target = Persistence.PathNorm.ToStored(seed.Target),
            IconOverride = iconOverride,
        };
    }

    private sealed class InstalledAppSourceAdapter(InstalledAppSource inner) : ICatalogSource
    {
        public SeedCategory Category => inner.Category;
        public IEnumerable<CatalogSeed> Enumerate() => inner.Enumerate();
    }
}
