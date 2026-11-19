using Galah2.Models;

namespace Galah2.Services;

/// <summary>
/// Helper for the "删除并另加" flow (spec §4.1): item kind is immutable, so changing the nature of a
/// target means delete + create. Galah suggests the kind that fits the typed target.
/// </summary>
public static class KindInference
{
    /// <summary>Suggest the kind a target most likely belongs to.</summary>
    /// <param name="storedTarget">Stored (forward-slash) target or URL.</param>
    /// <param name="fallback">Kind kept when nothing matches.</param>
    public static string Suggest(string? storedTarget, string fallback)
    {
        var text = (storedTarget ?? "").Trim();
        if (text.Length == 0) return fallback;
        if (UrlSafety.IsHttpUrl(text)) return ItemKinds.Url;

        var os = Persistence.PathNorm.ToOs(text);
        if (Directory.Exists(os)) return ItemKinds.Folder;
        if (File.Exists(os))
        {
            var extension = Path.GetExtension(os).ToLowerInvariant();
            return Array.IndexOf(LauncherService.ExecutableExtensions, extension) >= 0 ? ItemKinds.App : ItemKinds.File;
        }
        return fallback;
    }

    /// <summary>Derive the display name of a new draft (spec §2.2 naming rules).</summary>
    /// <param name="kind">Item kind.</param>
    /// <param name="storedTarget">Stored target.</param>
    public static string DeriveName(string kind, string storedTarget)
    {
        switch (kind)
        {
            case ItemKinds.Url:
                return UrlSafety.HostName(storedTarget) ?? storedTarget;
            case ItemKinds.Folder:
                return Path.GetFileName(storedTarget.TrimEnd('/')) is { Length: > 0 } folderName ? folderName : storedTarget;
            case ItemKinds.App:
                var exeName = Path.GetFileName(storedTarget);
                return Path.GetFileNameWithoutExtension(exeName) is { Length: > 0 } stem ? stem : exeName;
            default:
                var fileName = Path.GetFileName(storedTarget);
                return string.IsNullOrEmpty(fileName) ? storedTarget : fileName;
        }
    }
}
