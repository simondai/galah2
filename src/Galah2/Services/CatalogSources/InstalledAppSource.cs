using System.Text.RegularExpressions;
using Galah2.Models;
using Galah2.Persistence;
using Microsoft.Win32;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// 桌面应用：以 Windows 开始菜单（公共 + 当前用户）中的 <c>.lnk</c> 快捷方式为主，
/// 补充卸载注册表中未在开始菜单建快捷方式的桌面软件，过滤掉卸载器、帮助文档等噪音项。
/// </summary>
public sealed class InstalledAppSource
{
    /// <summary>Category this source fills.</summary>
    public SeedCategory Category => SeedCategory.InstalledApps;

    /// <summary>Hard cap so a bloated machine cannot flood the picker.</summary>
    public const int MaxEntries = 400;

    private static readonly string[] NoiseNameKeywords =
    [
        "uninstall", "卸载", "remove ", "repair ", "readme", "自述", "帮助", "manual",
        "documentation", "release notes", "changelog", "license", "website", "官网",
        "官方网站", "在线帮助", "访问网站", "update", "更新程序", "检查更新",
    ];

    private static readonly string[] RegistryNoisePrefixes =
    [
        "KB", "Microsoft Visual C++", "Microsoft .NET", ".NET Runtime", "ASP.NET",
        "Update for", "Security Update", "Microsoft Edge WebView2", "Windows Software",
        "DirectX", "OpenOffice Custom Install", "Microsoft VSS", "SQL Server",
    ];

    private static readonly Regex IndexedIcon = new(@"^(.*?\.exe)(,\s*-?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<CatalogSeed>? _cache;
    private static readonly object Gate = new();

    /// <summary>Enumerate desktop apps (cached).</summary>
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
        var byName = new Dictionary<string, CatalogSeed>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 优先扫描开始菜单（当前用户 + 公共），拿到最完整的桌面应用与开发工具入口
        foreach (var root in GetStartMenuProgramDirs())
        {
            ScanStartMenuDir(root, byName, seenTargets);
        }

        // 2. 用卸载注册表补充未建开始菜单快捷方式的桌面应用
        ScanUninstallRegistry(byName, seenTargets);

        return byName.Values
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxEntries)
            .ToList();
    }

    private static IEnumerable<string> GetStartMenuProgramDirs()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void ScanStartMenuDir(
        string rootDir,
        Dictionary<string, CatalogSeed> byName,
        HashSet<string> seenTargets)
    {
        IEnumerable<string> links;
        try
        {
            links = Directory.EnumerateFiles(rootDir, "*.lnk", SearchOption.AllDirectories);
        }
        catch (Exception) { return; }

        foreach (var lnk in links)
        {
            try
            {
                // 跳过“启动”文件夹和“管理工具”（管理工具已归入“系统应用”）
                var rel = Path.GetRelativePath(rootDir, lnk);
                if (rel.StartsWith("Startup\\", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("StartUp\\", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("Administrative Tools\\", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("Windows Administrative Tools\\", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("System Tools\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileNameWithoutExtension(lnk).Trim();
                if (name.Length < 2 || IsNoiseName(name)) continue;

                var resolved = LinkResolver.Resolve(lnk);
                if (resolved.Ok)
                {
                    var targetFile = Path.GetFileName(resolved.Target);
                    if (IsNoiseExecutable(targetFile)) continue;
                    // 若带参数的命令行工具（如 Git Bash、Developer PowerShell），按 lnk 自身去重，不误杀同 exe 不同参数的入口
                    var dedupKey = string.IsNullOrWhiteSpace(resolved.Arguments)
                        ? PathNorm.ToStored(resolved.Target)
                        : PathNorm.ToStored(lnk);
                    if (!seenTargets.Add(dedupKey)) continue;
                }

                if (byName.ContainsKey(name)) continue;

                // 直接保存 .lnk 路径，既保留原始参数/工作目录，又由 IconService 解析高清图标
                var storedLnk = PathNorm.ToStored(lnk);
                byName[name] = new CatalogSeed(SeedCategory.InstalledApps, name, ItemKinds.App, storedLnk, Note: "桌面应用");
            }
            catch (Exception) { }
        }
    }

    private static void ScanUninstallRegistry(
        Dictionary<string, CatalogSeed> byName,
        HashSet<string> seenTargets)
    {
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
                using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var keyName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var app = root.OpenSubKey(keyName);
                        CollectRegistryOne(app, byName, seenTargets, label);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }
    }

    private static void CollectRegistryOne(
        RegistryKey? app,
        Dictionary<string, CatalogSeed> byName,
        HashSet<string> seenTargets,
        string hiveLabel)
    {
        if (app is null) return;
        var display = app.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(display)) return;
        display = display.Trim();
        if (display.Length < 2 || IsNoiseName(display)) return;

        foreach (var noise in RegistryNoisePrefixes)
            if (display.StartsWith(noise, StringComparison.OrdinalIgnoreCase)) return;

        var systemComponent = app.GetValue("SystemComponent");
        if (systemComponent is int scoped && scoped != 0) return;

        var exe = ResolveExe(app);
        if (exe is null) return;
        if (IsNoiseExecutable(Path.GetFileName(exe))) return;
        if (!seenTargets.Add(exe)) return;
        if (byName.ContainsKey(display)) return;

        var publisher = app.GetValue("Publisher") as string;
        var note = string.IsNullOrWhiteSpace(publisher) ? hiveLabel : publisher!.Trim();
        byName[display] = new CatalogSeed(SeedCategory.InstalledApps, display, ItemKinds.App, exe, Note: note);
    }

    private static bool IsNoiseName(string name)
    {
        foreach (var kw in NoiseNameKeywords)
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsNoiseExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
    }

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
