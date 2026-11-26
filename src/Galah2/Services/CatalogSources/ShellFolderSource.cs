using Galah2.Models;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// 标准文件夹：高频系统入口（此电脑、回收站、网络、控制面板等）+ 用户常用目录 + 动态本地磁盘。
/// </summary>
public sealed class ShellFolderSource : ICatalogSource
{
    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.ShellFolders;

    private static readonly string Sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    private static readonly (string Name, Environment.SpecialFolder Folder)[] UserFolders =
    [
        ("桌面", Environment.SpecialFolder.DesktopDirectory),
        ("下载", Environment.SpecialFolder.UserProfile),           // 拼接 \Downloads
        ("文档", Environment.SpecialFolder.MyDocuments),
        ("图片", Environment.SpecialFolder.MyPictures),
        ("音乐", Environment.SpecialFolder.MyMusic),
        ("视频", Environment.SpecialFolder.MyVideos),
    ];

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        var imageres = Path.Combine(Sys32, "imageres.dll");
        var ncpa = Path.Combine(Sys32, "ncpa.cpl");

        // 1. 顶部高频系统虚拟文件夹
        yield return new CatalogSeed(Category, "此电脑", ItemKinds.Protocol,
            "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Note: "系统入口", IconOverride: $"{imageres},-109");
        yield return new CatalogSeed(Category, "回收站", ItemKinds.Protocol,
            "shell:::{645FF040-5081-101B-9F08-00AA002F954E}", Note: "系统入口", IconOverride: $"{imageres},-55");

        // 2. 用户常用物理目录
        foreach (var (name, folder) in UserFolders)
        {
            string path;
            try
            {
                path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            }
            catch (Exception) { continue; }

            if (folder == Environment.SpecialFolder.UserProfile)
                path = Path.Combine(path, "Downloads");

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            yield return new CatalogSeed(Category, name, ItemKinds.Folder, path, Note: Path.GetFileName(path));
        }

        // 3. 网络、打印机与所有控制面板项
        yield return new CatalogSeed(Category, "网络", ItemKinds.Protocol,
            "shell:::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", Note: "系统入口", IconOverride: $"{imageres},-25");
        yield return new CatalogSeed(Category, "网络连接", ItemKinds.Protocol,
            "shell:::{7007ACC7-3202-11D1-AAD2-00805FC1270E}", Note: "网络适配器", IconOverride: $"{ncpa},0");
        yield return new CatalogSeed(Category, "打印机", ItemKinds.Protocol,
            "shell:::{2227A280-3AEA-1069-A2DE-08002B30309D}", Note: "设备与打印机", IconOverride: $"{imageres},-51");
        yield return new CatalogSeed(Category, "所有控制面板项", ItemKinds.Protocol,
            "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}", Note: "控制面板总览", IconOverride: $"{imageres},-27");

        // 4. 动态本地磁盘（带真实卷标，如 WIN11 (C:)、DATA (D:)）
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception) { yield break; }

        foreach (var drive in drives)
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            string? root;
            string label;
            try
            {
                if (!drive.IsReady) continue;
                root = drive.RootDirectory.FullName;
                var letter = root.TrimEnd('\\');
                var vol = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel.Trim();
                label = $"{vol} ({letter})";
            }
            catch (Exception) { continue; }

            yield return new CatalogSeed(Category, label, ItemKinds.Folder, root, Note: "本地磁盘");
        }
    }
}
