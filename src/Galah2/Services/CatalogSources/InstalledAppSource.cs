using System.Text.RegularExpressions;
using Galah2.Models;
using Galah2.Persistence;
using Microsoft.Win32;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// Installed applications from the uninstall registry (Sprint 1 / ROADMAP B5). The uninstall
/// hive is Windows' own inventory, so no directory crawling is needed. Only entries whose
/// DisplayIcon resolves to a real .exe are offered - pointing a tile at the *uninstaller*
/// (the usual fallback) would be a footgun, so those entries are dropped instead.
/// Results are cached for the process lifetime: the scan costs ~100 ms and the inventory
/// does not change while the picker is open.
/// </summary>
public sealed class InstalledAppSource
{
    /// <summary>Category this source fills.</summary>
    public SeedCategory Category => SeedCategory.InstalledApps;

    /// <summary>Hard cap so a bloated machine cannot flood the picker.</summary>
    public const int MaxEntries = 400;

    private static readonly string[] NoisePrefixes =
    [
        "KB", "Microsoft Visual C++", "Microsoft .NET", ".NET Runtime", "ASP.NET",
        "Update for", "Security Update", "Microsoft Edge WebView2", "Windows Software",
        "DirectX", "OpenOffice Custom Install", "Microsoft VSS", "SQL Server",
    ];

    private static readonly Regex IndexedIcon = new(@"^(.*?\.exe)(,\s*-?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<CatalogSeed>? _cache;
    private static readonly object Gate = new();

    /// <summary>Enumerate installed apps (cached).</summary>
    public IEnumerable<CatalogSeed> Enumerate()
    {
        lock (Gate)
        {
            if (_cache is not null) return _cache;
            _cache = Scan();
            return _cache;
        }
    }

    /// <summary>Drop the cache (used by tests and after an install/uninstall cycle).</summary>
    public static void ResetCache()
    {
        lock (Gate) { _cache = null; }
    }

    private static List<CatalogSeed> Scan()
    {
        var byPath = new Dictionary<string, CatalogSeed>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, label) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "machine"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "machine32"),
            (RegistryHive.CurrentUser,  RegistryView.Registry64, "user"),
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var app = root.OpenSubKey(name);
                        CollectOne(app, byPath, label, SeedCategory.InstalledApps);
                    }
                    catch (Exception)
                    {
                        // A single unreadable key never aborts the scan.
                    }
                }
            }
            catch (Exception)
            {
                // A hive view this Windows edition does not expose is skipped.
            }
        }

        return byPath.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxEntries)
            .ToList();
    }

    private static void CollectOne(RegistryKey? app, Dictionary<string, CatalogSeed> byPath, string hiveLabel, SeedCategory category)
    {
        if (app is null) return;
        var display = app.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(display)) return;
        display = display.Trim();
        if (display.Length < 2) return;
        foreach (var noise in NoisePrefixes)
            if (display.StartsWith(noise, StringComparison.OrdinalIgnoreCase)) return;

        // System components mark themselves; they are never launch targets a user wants.
        var systemComponent = app.GetValue("SystemComponent");
        if (systemComponent is int scoped && scoped != 0) return;

        var exe = ResolveExe(app);
        if (exe is null) return;

        var publisher = app.GetValue("Publisher") as string;
        var note = string.IsNullOrWhiteSpace(publisher) ? hiveLabel : publisher!.Trim();
        byPath[exe] = new CatalogSeed(category, display, ItemKinds.App, exe, Note: note);
    }

    /// <summary>
    /// Best exe for the tile: DisplayIcon when it points at a real .exe (the app's own binary,
    /// which is also where its icon lives). Never falls back to UninstallString.
    /// </summary>
    private static string? ResolveExe(RegistryKey app)
    {
        var icon = app.GetValue("DisplayIcon") as string;
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim().Trim('"');

        var match = IndexedIcon.Match(icon);
        var path = match.Success ? match.Groups[1].Value : icon;
        path = Environment.ExpandEnvironmentVariables(path).Trim();

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(path)) return null;
        return PathNorm.ToStored(path);
    }
}
