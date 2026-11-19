using Galah2.Models;
using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>
/// Turns dropped or pasted content into catalog drafts.
///
/// The recognition rules are 1.x's, reused unchanged: a dropped .lnk is imported as the thing it
/// points at (not the shortcut file), a .url is read through <see cref="UrlFileParser"/>, and a
/// directory becomes a folder entry. Only the payload reader is new — Avalonia's storage API
/// replaces WPF's IDataObject.
/// </summary>
public static class ImportService
{
    /// <summary>What a drop would add, so the caller can preview before committing.</summary>
    public sealed record ImportPreview(IReadOnlyList<CatalogItem> Items, IReadOnlyList<string> Skipped);

    /// <summary>Build drafts from dropped file paths (one item per path).</summary>
    public static ImportPreview FromFilePaths(IEnumerable<string> paths)
    {
        var items = new List<CatalogItem>();
        var skipped = new List<string>();

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var extension = Path.GetExtension(raw);

            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var info = LinkResolver.Resolve(raw);
                if (info.Ok)
                {
                    items.Add(new CatalogItem
                    {
                        Id = "drop",
                        Name = KindInference.DeriveName(info.Ok ? KindOf(info.Target!) : ItemKinds.File, info.Target!),
                        Kind = KindOf(info.Target!),
                        Target = info.Target!,
                        Args = info.Arguments,
                        Workdir = info.WorkingDirectory,
                    });
                    continue;
                }
                skipped.Add(raw);   // a broken shortcut is not silently imported as a file
                continue;
            }

            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                var url = UrlFileParser.ReadUrl(raw);
                if (url is not null && UrlSafety.IsHttpUrl(url, out var normalized))
                {
                    items.Add(new CatalogItem
                    {
                        Id = "drop",
                        Name = displayName(url),
                        Kind = ItemKinds.Url,
                        Target = normalized
                    });
                    continue;
                }
                skipped.Add(raw);
                continue;
            }

            if (Directory.Exists(raw))
            {
                items.Add(new CatalogItem
                {
                    Id = "drop",
                    Name = KindInference.DeriveName(ItemKinds.Folder, PathNorm.ToStored(raw)),
                    Kind = ItemKinds.Folder,
                    Target = PathNorm.ToStored(raw)
                });
                continue;
            }

            if (File.Exists(raw))
            {
                items.Add(new CatalogItem
                {
                    Id = "drop",
                    Name = KindInference.DeriveName(ItemKinds.File, PathNorm.ToStored(raw)),
                    Kind = ItemKinds.File,
                    Target = PathNorm.ToStored(raw)
                });
                continue;
            }

            skipped.Add(raw);
        }

        return new ImportPreview(items, skipped);
    }

    /// <summary>Build one draft from a pasted URL, or null when the text is not a usable URL.</summary>
    public static CatalogItem? FromText(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (!UrlSafety.IsHttpUrl(trimmed, out var normalized)) return null;

        return new CatalogItem
        {
            Id = "paste",
            Name = displayName(normalized),
            Kind = ItemKinds.Url,
            Target = normalized
        };
    }

    /// <summary>Kind for an already-resolved path.</summary>
    private static string KindOf(string path) =>
        Directory.Exists(PathNorm.ToOs(path)) ? ItemKinds.Folder : ItemKinds.File;

    /// <summary>A readable label for a URL: its host, which beats a 200-character address.</summary>
    private static string displayName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host + uri.AbsolutePath.TrimEnd('/')
            : url;
}
