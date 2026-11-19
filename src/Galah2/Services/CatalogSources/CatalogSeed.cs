namespace Galah2.Services.CatalogSources;

/// <summary>Picker categories, in display order (Sprint 1: four sources; store apps deferred).</summary>
public enum SeedCategory
{
    /// <summary>Windows built-in tools and Control Panel applets.</summary>
    SystemTools,

    /// <summary>Settings pages (ms-settings: protocol links).</summary>
    SettingsPages,

    /// <summary>Standard shell folders (Documents, Downloads, ...).</summary>
    ShellFolders,

    /// <summary>Applications registered in the uninstall registry.</summary>
    InstalledApps,
}

/// <summary>
/// One candidate entry offered by a catalog source, ready to become a <see cref="Models.CatalogItem"/>.
/// Deliberately a plain value object: sources are pure data producers, unit-testable without UI.
/// </summary>
/// <param name="Category">Which picker tab it belongs to.</param>
/// <param name="Name">Display name (our own wording, never copied from other products).</param>
/// <param name="Kind">ItemKinds value the resulting entry will carry.</param>
/// <param name="Target">Absolute path or allowed protocol URI.</param>
/// <param name="Args">Command line arguments (nullable).</param>
/// <param name="Note">Secondary line shown under the name (nullable).</param>
public sealed record CatalogSeed(
    SeedCategory Category,
    string Name,
    string Kind,
    string Target,
    string? Args = null,
    string? Note = null);

/// <summary>A source enumerates picker candidates of one category. Implementations must be side-effect free.</summary>
public interface ICatalogSource
{
    /// <summary>Category this source fills.</summary>
    SeedCategory Category { get; }

    /// <summary>Enumerate candidates. Must not throw on a missing/unreadable subsystem - return what works.</summary>
    IEnumerable<CatalogSeed> Enumerate();
}
