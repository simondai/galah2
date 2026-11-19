using Galah2.Models;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// Settings pages as ms-settings: protocol links (Sprint 1 / ROADMAP B2). The URI table is Galah's
/// own, transcribed from Microsoft's public Settings URI documentation; names are plain Chinese
/// descriptions, not copied from any launcher product. Unknown URIs degrade safely: the scheme is
/// allowed, and a missing page just shows the Settings app.
/// </summary>
public sealed class SettingsPageSource : ICatalogSource
{
    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.SettingsPages;

    // (display name, ms-settings URI, note)
    private static readonly (string Name, string Uri, string? Note)[] Pages =
    [
        ("蓝牙和其他设备", "ms-settings:bluetooth", null),
        ("背景", "ms-settings:personalization-background", null),
        ("颜色", "ms-settings:colors", null),
        ("锁屏界面", "ms-settings:lockscreen", null),
        ("任务栏", "ms-settings:taskbar", null),
        ("开始", "ms-settings:appsfeatures", "应用和功能"),
        ("显示", "ms-settings:display", null),
        ("夜间模式", "ms-settings:nightlight", null),
        ("显示亮度", "ms-settings:batterysaver", "电源相关"),
        ("通知和操作", "ms-settings:notifications", null),
        ("专注助手", "ms-settings:focusassist", null),
        ("电源和睡眠", "ms-settings:power", null),
        ("电池", "ms-settings:batterysaver", null),
        ("存储感知", "ms-settings:storagesense", null),
        ("复制粘贴", "ms-settings:clipboard", null),
        ("个性化字体", "ms-settings:fonts", null),
        ("语言和区域", "ms-settings:regionlanguage", null),
        ("日期和时间", "ms-settings:datetime", null),
        ("查找我的设备", "ms-settings:findmydevice", null),
        ("网络状态", "ms-settings:network-status", null),
        ("WLAN", "ms-settings:network-wifi", null),
        ("以太网", "ms-settings:network-ethernet", null),
        ("代理", "ms-settings:network-proxy", null),
        ("网络重置", "ms-settings:network-reset", null),
        ("打印机和扫描仪", "ms-settings:printers", null),
        ("鼠标", "ms-settings:mousetouchpad", null),
        ("触摸板", "ms-settings:devices-touchpad", null),
        ("USB", "ms-settings:devices-usb", null),
        ("自动播放", "ms-settings:autoplay", null),
        ("默认应用", "ms-settings:defaultapps", null),
        ("离线地图", "ms-settings:maps", null),
        ("可选择的显示语言", "ms-settings:regionlanguage-addda", null),
        ("Windows 更新", "ms-settings:windowsupdate", null),
        ("Windows 功能", "ms-settings:optionalfeatures", null),
        ("恢复", "ms-settings:recovery", null),
        ("疑难解答", "ms-settings:troubleshoot", null),
        ("针对开发者的设置", "ms-settings:developers", null),
        ("账户信息", "ms-settings:accountinfo", null),
        ("连接 Windows", "ms-settings:connectworkspace", null),
        ("邮箱和账户", "ms-settings:emailandaccounts", null),
        ("其他用户", "ms-settings:otherusers", null),
        ("设置同步", "ms-settings:sync", null),
        ("时间和语言", "ms-settings:time", null),
        ("隐私和安全性", "ms-settings:privacy", null),
        ("位置", "ms-settings:privacy-location", null),
        ("相机", "ms-settings:privacy-webcam", null),
        ("麦克风", "ms-settings:privacy-microphone", null),
        ("诊断和反馈", "ms-settings:privacy-feedback", null),
        ("任务管理器", "ms-settings:task-manager", null),
        ("启动", "ms-settings:startupapps", null),
        ("文件夹选项", "ms-settings:fileexp", "文件资源管理器选项"),
        ("Windows 安全中心", "ms-settings:windowsdefender", null),
        ("家庭和安全", "ms-settings:home", null),
        ("设备使用情况", "ms-settings:usage", null),
        ("卸载和清理", "ms-settings:appsfeatures-app", null),
    ];

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        foreach (var (name, uri, note) in Pages)
        {
            // Defensive: a table entry that fails the allowlist must never reach the picker.
            if (!ProtocolSafety.IsAllowed(uri, out _)) continue;
            yield return new CatalogSeed(Category, name, ItemKinds.Protocol, uri, Note: note ?? "设置页");
        }
    }
}
