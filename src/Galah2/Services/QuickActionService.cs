using System.Diagnostics;
using System.Runtime.InteropServices;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>
/// 内置系统快捷操作执行器（对应 <c>galah-action:*</c> 协议），直接调用 Windows 原生接口，无黑框闪烁。
/// </summary>
public static class QuickActionService
{
    public const string Scheme = "galah-action";

    public const string UriShutdown = "galah-action:shutdown";
    public const string UriRestart = "galah-action:restart";
    public const string UriSleep = "galah-action:sleep";
    public const string UriLock = "galah-action:lock";
    public const string UriMonitorOff = "galah-action:monitor-off";
    public const string UriClearClipboard = "galah-action:clear-clipboard";
    public const string UriEmptyRecycleBin = "galah-action:empty-recycle-bin";
    public const string UriRestartExplorer = "galah-action:restart-explorer";
    public const string UriEnvVars = "galah-action:env-vars";

    public static bool IsQuickAction(string? target) =>
        !string.IsNullOrWhiteSpace(target)
        && target.TrimStart().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断该快捷操作是否属于不可逆操作、需要弹出二次确认，并返回确认框标题与提示文案。
    /// </summary>
    public static bool TryGetConfirmation(string? target, out string title, out string message)
    {
        title = "";
        message = "";
        if (string.IsNullOrWhiteSpace(target)) return false;

        switch (target.Trim().ToLowerInvariant())
        {
            case UriShutdown:
                title = "确认关机";
                message = "确定要立即关闭计算机吗？请先保存未保存的工作。";
                return true;

            case UriRestart:
                title = "确认重启";
                message = "确定要立即重新启动计算机吗？请先保存未保存的工作。";
                return true;

            case UriEmptyRecycleBin:
                title = "确认清空回收站";
                message = "确定要永久清空回收站中的所有文件吗？此操作无法撤销。";
                return true;

            default:
                return false;
        }
    }

    /// <summary>执行一条 <c>galah-action:*</c> 指令。</summary>
    public static LaunchResult Execute(string target)
    {
        try
        {
            switch (target.Trim().ToLowerInvariant())
            {
                case UriShutdown:
                    return RunHidden("shutdown.exe", "/s /t 0");

                case UriRestart:
                    return RunHidden("shutdown.exe", "/r /t 0");

                case UriSleep:
                    return SetSuspendState(false, false, false)
                        ? LaunchResult.Success()
                        : LaunchResult.Fail(LaunchStatus.Error, "系统拒绝进入睡眠状态");

                case UriLock:
                    return LockWorkStation()
                        ? LaunchResult.Success()
                        : LaunchResult.Fail(LaunchStatus.Error, "无法锁定当前会话");

                case UriMonitorOff:
                    // 稍微延后 150ms 发熄屏消息，避免鼠标松开瞬间又把屏幕唤醒。
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(150).ConfigureAwait(false);
                        PostMessageW(HwndBroadcast, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)2);
                    });
                    return LaunchResult.Success();

                case UriClearClipboard:
                    return ClearClipboard();

                case UriEmptyRecycleBin:
                    // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND（已由 Galah 二次确认）
                    var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, 0x00000001 | 0x00000002 | 0x00000004);
                    // S_OK (0) 或回收站本就为空 (E_UNEXPECTED 0x8000FFFF) 均视为成功
                    return hr == 0 || hr == unchecked((int)0x8000FFFF)
                        ? LaunchResult.Success()
                        : LaunchResult.Fail(LaunchStatus.Error, $"清空回收站失败 (0x{hr:X8})");

                case UriRestartExplorer:
                    return RestartExplorer();

                case UriEnvVars:
                    return RunHidden("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables");

                default:
                    return LaunchResult.Fail(LaunchStatus.Error, "未知的快捷操作：" + target);
            }
        }
        catch (Exception ex)
        {
            return LaunchResult.Fail(LaunchStatus.Error, ex.Message);
        }
    }

    private static LaunchResult ClearClipboard()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return LaunchResult.Fail(LaunchStatus.Error, "剪贴板正被其他程序占用");
        try
        {
            return EmptyClipboard()
                ? LaunchResult.Success()
                : LaunchResult.Fail(LaunchStatus.Error, "清空剪贴板失败");
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static LaunchResult RestartExplorer()
    {
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName("explorer"))
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                    finally { proc.Dispose(); }
                }
                Thread.Sleep(300);
                // 若系统未自动拉起 explorer，则手动启动
                if (Process.GetProcessesByName("explorer").Length == 0)
                {
                    var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    Process.Start(new ProcessStartInfo(Path.Combine(winDir, "explorer.exe"))
                    {
                        UseShellExecute = true,
                    });
                }
            }
            catch { }
        });
        return LaunchResult.Success();
    }

    private static LaunchResult RunHidden(string fileName, string args)
    {
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var full = Path.Combine(sys32, fileName);
        Process.Start(new ProcessStartInfo(File.Exists(full) ? full : fileName, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return LaunchResult.Success();
    }

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);
    private const uint WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
