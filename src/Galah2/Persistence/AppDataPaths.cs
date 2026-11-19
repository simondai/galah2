namespace Galah2.Persistence;

/// <summary>
/// Canonical path helpers. Stored paths (catalog/ui-state/settings JSON) always use forward
/// slashes — the format written into the JSON files; <see cref="ToOs"/> converts back for IO.
/// </summary>
public static class PathNorm
{
    /// <summary>Normalize a path for storage: forward slashes, no trailing slash (except root), drive letter kept.</summary>
    public static string ToStored(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var p = path.Trim().Trim('"');
        p = p.Replace('\\', '/');
        while (p.Length > 3 && p.EndsWith("/")) p = p[..^1];
        return p;
    }

    /// <summary>Convert a stored path back to an OS path (backslashes for UNC friendliness).</summary>
    public static string ToOs(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        return stored.Replace('/', Path.DirectorySeparatorChar);
    }
}

/// <summary>
/// Locations of all Galah data. Green/portable: everything lives in <c>GalahData/</c> right next to
/// the executable — never under %LOCALAPPDATA% (see IMPLEMENTATION-NOTES, deviation D-1).
/// </summary>
public sealed class AppDataPaths
{
    /// <summary>Folder name next to the executable.</summary>
    public const string FolderName = "GalahData";

    /// <summary>Absolute path of the data root.</summary>
    public string Root { get; }

    public string CatalogFile => Path.Combine(Root, "catalog.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string UiStateFile => Path.Combine(Root, "ui-state.json");
    /// <summary>True when a settings file already exists (used to detect a first run).</summary>
    public bool SettingsFileExists => File.Exists(SettingsFile);

    /// <summary>True when a ui-state file already exists (used to detect a first run).</summary>
    public bool UiStateFileExists => File.Exists(UiStateFile);

    public string IconsDirectory => Path.Combine(Root, "icons");
    public string IconOverrideDirectory => Path.Combine(IconsDirectory, "override");

    /// <summary>Explicit root (used by tests and by the app after resolving the executable directory).</summary>
    public AppDataPaths(string root) => Root = Path.GetFullPath(root);

    /// <summary>Data root next to the running executable; falls back to the assembly base directory.</summary>
    public static AppDataPaths ForExecutable()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(exeDir)) exeDir = AppContext.BaseDirectory;
        return new AppDataPaths(Path.Combine(exeDir, FolderName));
    }

    /// <summary>Create the data root (and icon folders) if missing. Never deletes anything.</summary>
    public AppDataPaths EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        return this;
    }
}
