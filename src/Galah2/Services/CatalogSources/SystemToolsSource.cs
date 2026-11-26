using Galah2.Models;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// 纯净的 Windows 系统应用与管理工具（不再混入 App Paths 第三方软件）。
/// 在不同 Windows 版本上不存在的项会自动跳过。
/// </summary>
public sealed class SystemToolsSource : ICatalogSource
{
    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.SystemTools;

    private static readonly string Sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // (显示名称, 相对路径, 参数, 说明, 可选图标覆盖)
    private static readonly (string Name, string Exe, string? Args, string? Note, string? Icon)[] Tools =
    [
        ("本地安全策略", "secpol.msc", null, "仅专业版/企业版", null),
        ("步骤记录器", "psr.exe", null, "操作步骤录制", null),
        ("磁盘清理", "cleanmgr.exe", null, "清理系统垃圾", null),
        ("磁盘管理", "diskmgmt.msc", null, "分区与卷管理", null),
        ("打印管理", "printmanagement.msc", null, "打印机队列管理", null),
        ("服务", "services.msc", null, "系统服务管理", null),
        ("高级安全 Windows 防火墙", "wf.msc", null, "入站与出站规则", null),
        ("恢复驱动器", "RecoveryDrive.exe", null, "创建恢复介质", null),
        ("计算机管理", "compmgmt.msc", null, "系统综合管理", null),
        ("控制面板", "control.exe", null, "经典控制面板", null),
        ("命令提示符", "cmd.exe", null, "经典命令行", null),
        ("PowerShell", @"WindowsPowerShell\v1.0\powershell.exe", null, "Windows PowerShell", null),
        ("任务管理器", "Taskmgr.exe", null, "进程与性能监控", null),
        ("任务计划程序", "taskschd.msc", null, "定时任务管理", null),
        ("设备管理器", "devmgmt.msc", null, "硬件与驱动程序", null),
        ("事件查看器", "eventvwr.msc", null, "系统运行日志", null),
        ("碎片整理和优化驱动器", "dfrgui.exe", null, "磁盘优化", null),
        ("系统配置", "msconfig.exe", null, "引导与启动项配置", null),
        ("系统信息", "msinfo32.exe", null, "硬件与系统摘要", null),
        ("系统还原", "rstrui.exe", null, "还原系统状态", null),
        ("性能监视器", "perfmon.msc", null, "性能计数器", null),
        ("远程桌面连接", "mstsc.exe", null, "RDP 远程连接", null),
        ("运行", "explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00104216072f}", "快速运行对话框", "shell32.dll,-25"),
        ("注册表编辑器", "regedit.exe", null, "高级配置工具", null),
        ("资源监视器", "resmon.exe", null, "CPU/磁盘/网络实时监控", null),
        ("字符映射表", "charmap.exe", null, "特殊符号选择", null),
        ("组件服务", "comexp.msc", null, "COM+ 与 DCOM", null),
        ("DirectX 诊断工具", "dxdiag.exe", null, "显卡与多媒体诊断", null),
        ("iSCSI 发起程序", "iscsicpl.exe", null, "iSCSI 存储连接", null),
        ("ODBC 数据源 (64 位)", "odbcad32.exe", null, "64 位数据库连接", null),
        ("ODBC 数据源 (32 位)", @"..\SysWOW64\odbcad32.exe", null, "32 位数据库连接", null),
        ("Windows 内存诊断", "mdsched.exe", null, "内存条检测", null),
    ];

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, exe, args, note, iconSpec) in Tools)
        {
            var path = Path.GetFullPath(Path.Combine(Sys32, exe));
            if (!File.Exists(path)) path = Path.Combine(WinDir, exe);
            if (!File.Exists(path)) continue;
            if (!seen.Add(path + "|" + (args ?? ""))) continue;

            var kind = exe.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase) ||
                       exe.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)
                ? ItemKinds.File
                : ItemKinds.App;

            string? iconOverride = null;
            if (!string.IsNullOrWhiteSpace(iconSpec))
            {
                var comma = iconSpec.LastIndexOf(',');
                var file = comma > 0 ? iconSpec[..comma] : iconSpec;
                var idx = comma > 0 ? iconSpec[comma..] : "";
                iconOverride = Path.Combine(Sys32, file) + idx;
            }

            yield return new CatalogSeed(Category, name, kind, path, args, note, iconOverride);
        }
    }
}
