using Galah2.Models;
using Microsoft.Win32;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// Built-in Windows tools (Sprint 1 / ROADMAP B1). Two feeds, both resolved to real executables so
/// launch/validation behave like any app item: (a) a curated table of system binaries and Control
/// Panel applets, (b) the App Paths registry key, which is the machine's own list of launchable
/// programs. Names in (a) are Galah's own descriptions. Entries whose exe is missing on this
/// Windows edition are dropped (the table spans versions deliberately).
/// </summary>
public sealed class SystemToolsSource : ICatalogSource
{
    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.SystemTools;

    private static readonly string Sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // (display name, exe path relative to System32 or Windows, args, note)
    private static readonly (string Name, string Exe, string? Args, string? Note)[] Tools =
    [
        ("任务管理器", "Taskmgr.exe", null, "系统监视与进程管理"),
        ("设备管理器", "devmgmt.msc", null, "硬件与驱动"),
        ("服务", "services.msc", null, "系统服务管理"),
        ("注册表编辑器", "regedit.exe", null, "高级用户工具"),
        ("组件服务", "comexp.msc", null, "COM+ 与 DCOM"),
        ("本地安全策略", "secpol.msc", null, "仅专业版/企业版"),
        ("磁盘管理", "diskmgmt.msc", null, "分区与卷"),
        ("事件查看器", "eventvwr.msc", null, "系统日志"),
        ("性能监视器", "perfmon.msc", null, "性能计数器"),
        ("资源监视器", "resmon.exe", null, "CPU/磁盘/网络占用"),
        ("计划任务", "taskschd.msc", null, "定时任务"),
        ("控制面板", "control.exe", null, "经典控制面板"),
        ("网络和共享中心", "control.exe", "/name Microsoft.NetworkAndSharingCenter", null),
        ("网络连接", "ncpa.cpl", null, "适配器与代理"),
        ("电源选项", "control.exe", "powercfg.cpl", null),
        ("系统属性", "control.exe", "sysdm.cpl", null),
        ("程序和功能", "appwiz.cpl", null, "已安装程序"),
        ("日期和时间", "timedate.cpl", null, "经典设置"),
        ("区域设置", "intl.cpl", null, "格式与位置"),
        ("鼠标属性", "main.cpl", null, "指针与按钮"),
        ("声音设置", "mmsys.cpl", null, "播放/录制设备"),
        ("轻松访问中心", "control.exe", "/name Microsoft.EaseOfAccessCenter", "辅助功能"),
        ("DirectX 诊断工具", "dxdiag.exe", null, "多媒体诊断"),
        ("系统信息", "msinfo32.exe", null, "硬件与软件摘要"),
        ("系统还原", "rstrui.exe", null, "还原点"),
        ("命令提示符", "cmd.exe", null, "经典终端"),
        ("PowerShell", @"WindowsPowerShell\v1.0\powershell.exe", null, null),
        ("运行对话框", "explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00104216072f}", null),
    ];

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, exe, args, note) in Tools)
        {
            // 先查 System32，再查 Windows 目录（regedit.exe、explorer.exe 位于 Windows 根目录）。
            var path = Path.Combine(Sys32, exe);
            if (!File.Exists(path)) path = Path.Combine(WinDir, exe);
            if (!File.Exists(path)) continue;
            if (!seen.Add(path + "|" + (args ?? ""))) continue;

            var kind = exe.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) ||
                       exe.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)
                ? ItemKinds.File          // ShellExecute-openable control applet
                : ItemKinds.App;
            yield return new CatalogSeed(Category, name, kind, path, args, note);
        }

        foreach (var seed in EnumerateAppPaths(seen))
            yield return seed;
    }

    /// <summary>App Paths registry: the machine's own registered executables.</summary>
    /// <param name="seen">Dedup key set shared with the curated table.</param>
    private List<CatalogSeed> EnumerateAppPaths(HashSet<string> seen)
    {
        var results = new List<CatalogSeed>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (key is null) continue;
                var batch = new List<CatalogSeed>();
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var app = key.OpenSubKey(name);
                        var value = app?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                        if (!File.Exists(path)) continue;
                        var display = Path.GetFileNameWithoutExtension(name);
                        if (!seen.Add(path + "|")) continue;
                        batch.Add(new CatalogSeed(Category, display, ItemKinds.App, path, Note: "系统注册程序"));
                    }
                    catch (Exception)
                    {
                        // A malformed App Paths value is a registry quirk, not a user error.
                    }
                }
                results.AddRange(batch);
            }
            catch (Exception)
            {
                // An unreadable hive is skipped; the curated table already carries the essentials.
            }
        }
        return results;
    }
}
