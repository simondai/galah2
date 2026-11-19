namespace Galah2.Services.CatalogSources;

/// <summary>
/// Aggregates the picker's sources behind one call so the UI never knows about individual
/// feeds, and tests can assert the whole picker surface at once.
/// </summary>
public sealed class CatalogSourceService
{
    private readonly ICatalogSource[] _sources;

    /// <summary>Build the service with the default source set.</summary>
    public CatalogSourceService()
    {
        var installed = new InstalledAppSource();
        _sources =
        [
            new SystemToolsSource(),
            new SettingsPageSource(),
            new ShellFolderSource(),
            // InstalledAppSource is not an ICatalogSource (it caches); wrapped here:
            new InstalledAppSourceAdapter(installed),
        ];
        Installed = installed;
    }

    /// <summary>The installed-app feed (exposed for cache reset after add/remove cycles).</summary>
    public InstalledAppSource Installed { get; }

    /// <summary>Categories in display order.</summary>
    public IReadOnlyList<SeedCategory> Categories { get; } =
    [
        SeedCategory.SystemTools,
        SeedCategory.SettingsPages,
        SeedCategory.ShellFolders,
        SeedCategory.InstalledApps,
    ];

    /// <summary>Chinese label of a category (picker tab text).</summary>
    /// <param name="category">Category to label.</param>
    public static string LabelOf(SeedCategory category) => category switch
    {
        SeedCategory.SystemTools => "系统工具",
        SeedCategory.SettingsPages => "设置项",
        SeedCategory.ShellFolders => "标准文件夹",
        SeedCategory.InstalledApps => "已安装应用",
        _ => category.ToString(),
    };

    /// <summary>Enumerate one category. Never throws; an unavailable source yields nothing.</summary>
    /// <param name="category">Category to fill.</param>
    public IReadOnlyList<CatalogSeed> Enumerate(SeedCategory category)
    {
        foreach (var source in _sources)
        {
            if (source.Category != category) continue;
            try
            {
                // One storage convention for the whole picker: paths are persisted in forward-slash
                // form (PathNorm), protocol URIs are left exactly as authored.
                return source.Enumerate().Select(Normalize).ToList();
            }
            catch (Exception)
            {
                return Array.Empty<CatalogSeed>();
            }
        }
        return Array.Empty<CatalogSeed>();
    }

    /// <summary>Applies the stored-path convention to one seed.</summary>
    /// <param name="seed">Seed as produced by a source.</param>
    private static CatalogSeed Normalize(CatalogSeed seed)
    {
        if (seed.Kind == Models.ItemKinds.Protocol) return seed;
        return seed with { Target = Persistence.PathNorm.ToStored(seed.Target) };
    }

    /// <summary>Wraps the caching installed-app source in the common interface.</summary>
    private sealed class InstalledAppSourceAdapter(InstalledAppSource inner) : ICatalogSource
    {
        public SeedCategory Category => inner.Category;
        public IEnumerable<CatalogSeed> Enumerate() => inner.Enumerate();
    }
}
