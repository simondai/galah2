using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Galah2.Persistence;

namespace Galah2;

internal static class Program
{
    // Avalonia needs an STA thread and no synthetic sync context during boot.
    // 默认使用 Software 渲染避免拉起 ANGLE/D3D11 与显卡着色器驱动（可省 30~50 MB 私有内存）；
    // 若用户在设置中勾选了“开启 GPU 加速”，则沿用 Avalonia 默认硬件加速链（需重启生效）。
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (!ReadGpuAccelerationPreference())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
            });
        }

        builder.LogToTrace().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>启动窗口引擎前只读检查 settings.json 的 gpuAcceleration 字段（AOT 零反射、绝不写盘）。</summary>
    private static bool ReadGpuAccelerationPreference()
    {
        try
        {
            var path = AppDataPaths.ForExecutable().SettingsFile;
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("gpuAcceleration", out var prop)
                && prop.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
