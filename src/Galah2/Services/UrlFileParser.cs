using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>
/// Minimal INI reader for Windows internet shortcuts (.url), used by the drag/drop import
/// (spec §2.1: 拖放 .url → 读 URL= 行 → kind=url). Tolerates BOM, CRLF, missing section headers and
/// comment lines; returns only the URL because that is all Galah snapshots.
/// </summary>
public static class UrlFileParser
{
    /// <summary>Section carrying the shortcut data.</summary>
    public const string InternetShortcutSection = "InternetShortcut";

    /// <summary>
    /// Read the <c>URL=</c> value of a .url file.
    /// </summary>
    /// <param name="path">Path of the .url file (OS or stored form).</param>
    /// <returns>The trimmed URL, or null when the file is missing, unreadable or has no URL value.</returns>
    public static string? ReadUrl(string path)
    {
        var os = PathNorm.ToOs(path);
        if (string.IsNullOrEmpty(os) || !File.Exists(os)) return null;
        try
        {
            return Parse(File.ReadAllText(os));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Parse .url content already in memory. Prefers <c>URL=</c> inside <c>[InternetShortcut]</c>,
    /// falls back to the first <c>URL=</c> line anywhere in the file.
    /// </summary>
    /// <param name="content">File content.</param>
    public static string? Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        string? firstOutsideSection = null;
        var inShortcutSection = false;

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == ';' || line[0] == '\'') continue;

            if (line[0] == '[' && line.EndsWith("]"))
            {
                inShortcutSection = string.Equals(line[1..^1].Trim(), InternetShortcutSection, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim();
            if (!key.Equals("URL", StringComparison.OrdinalIgnoreCase)) continue;

            var value = Unquote(line[(equals + 1)..].Trim());
            if (value.Length == 0) continue;
            if (inShortcutSection) return value;
            firstOutsideSection ??= value;
        }

        return firstOutsideSection;
    }

    /// <summary>Write a .url file (used by the test fixture; mirrors what browsers/Explorer produce).</summary>
    /// <param name="path">Destination path.</param>
    /// <param name="url">URL to store.</param>
    public static void Write(string path, string url)
    {
        var os = PathNorm.ToOs(path);
        Directory.CreateDirectory(Path.GetDirectoryName(os)!);
        File.WriteAllText(os,
            "[{000214A0-0000-0000-C000-000000000046}]" + Environment.NewLine +
            "Prop3=19,11" + Environment.NewLine +
            "[InternetShortcut]" + Environment.NewLine +
            "IDList=" + Environment.NewLine +
            "URL=" + url + Environment.NewLine +
            "HotKey=0" + Environment.NewLine);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1];
        return value;
    }
}
