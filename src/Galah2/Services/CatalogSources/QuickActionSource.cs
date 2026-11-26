using Galah2.Models;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// 内置系统快捷操作（关机、重启、睡眠、锁定、关闭显示器、清空剪贴板、清空回收站、重启资源管理器、编辑环境变量）。
/// 通过 <c>galah-action:&lt;id&gt;</c> 内部协议触发，由 <see cref="QuickActionService"/> 直接调用原生 Win32 API 执行。
/// </summary>
public sealed class QuickActionSource : ICatalogSource
{
    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.QuickActions;

    private static readonly string Sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        var shell32 = Path.Combine(Sys32, "shell32.dll");
        var imageres = Path.Combine(Sys32, "imageres.dll");
        var explorer = Path.Combine(WinDir, "explorer.exe");
        var sysdm = Path.Combine(Sys32, "sysdm.cpl");

        return
        [
            new CatalogSeed(Category, "关机", ItemKinds.Protocol, QuickActionService.UriShutdown,
                Note: "执行前会弹出确认", IconOverride: "avares://Galah2/Assets/actions/shutdown.png"),
            new CatalogSeed(Category, "重启", ItemKinds.Protocol, QuickActionService.UriRestart,
                Note: "执行前会弹出确认", IconOverride: "avares://Galah2/Assets/actions/restart.png"),
            new CatalogSeed(Category, "睡眠", ItemKinds.Protocol, QuickActionService.UriSleep,
                Note: "进入睡眠状态", IconOverride: $"{imageres},-106"),
            new CatalogSeed(Category, "锁定", ItemKinds.Protocol, QuickActionService.UriLock,
                Note: "锁定当前 Windows 会话", IconOverride: $"{shell32},-48"),
            new CatalogSeed(Category, "关闭显示器", ItemKinds.Protocol, QuickActionService.UriMonitorOff,
                Note: "立即熄屏节能", IconOverride: $"{imageres},-109"),
            new CatalogSeed(Category, "清空剪贴板", ItemKinds.Protocol, QuickActionService.UriClearClipboard,
                Note: "清除剪贴板中的文本与图片", IconOverride: $"{shell32},-260"),
            new CatalogSeed(Category, "清空回收站", ItemKinds.Protocol, QuickActionService.UriEmptyRecycleBin,
                Note: "执行前会弹出确认", IconOverride: $"{imageres},-55"),
            new CatalogSeed(Category, "重启文件资源管理器", ItemKinds.Protocol, QuickActionService.UriRestartExplorer,
                Note: "刷新桌面与任务栏", IconOverride: $"{explorer},0"),
            new CatalogSeed(Category, "编辑账户的环境变量", ItemKinds.Protocol, QuickActionService.UriEnvVars,
                Note: "打开用户环境变量设置", IconOverride: $"{sysdm},0"),
        ];
    }
}
