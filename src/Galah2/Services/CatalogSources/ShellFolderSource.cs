using Galah2.Models;

namespace Galah2.Services.CatalogSources;

/// <summary>
/// Standard shell folders (Sprint 1 / ROADMAP B4). Backed by <see cref="Environment.SpecialFolder"/>
/// so paths follow the user's known-folder redirections; entries whose folder does not resolve are
/// skipped instead of offering a broken tile.
/// </summary>
public sealed class ShellFolderSource : ICatalogSource
{
    /// <inheritdoc />
    public SeedCategory Category => SeedCategory.ShellFolders;

    // (display name, special folder) - Galah's own wording
    private static readonly (string Name, Environment.SpecialFolder Folder)[] Folders =
    [
        ("桌面", Environment.SpecialFolder.DesktopDirectory),
        ("文档", Environment.SpecialFolder.MyDocuments),
        ("下载", Environment.SpecialFolder.UserProfile),           // \Downloads appended below
        ("图片", Environment.SpecialFolder.MyPictures),
        ("音乐", Environment.SpecialFolder.MyMusic),
        ("视频", Environment.SpecialFolder.MyVideos),
        ("收藏夹", Environment.SpecialFolder.Favorites),
        ("开始菜单", Environment.SpecialFolder.StartMenu),
        ("启动", Environment.SpecialFolder.Startup),
        ("程序", Environment.SpecialFolder.Programs),
        ("公共桌面", Environment.SpecialFolder.CommonDesktopDirectory),
        ("公共文档", Environment.SpecialFolder.CommonDocuments),
        ("Windows", Environment.SpecialFolder.Windows),
        ("System32", Environment.SpecialFolder.System),
        ("程序文件", Environment.SpecialFolder.ProgramFiles),
        ("临时文件", Environment.SpecialFolder.InternetCache),
        ("字体", Environment.SpecialFolder.Fonts),
        ("模板", Environment.SpecialFolder.Templates),
    ];

    /// <inheritdoc />
    public IEnumerable<CatalogSeed> Enumerate()
    {
        foreach (var (name, folder) in Folders)
        {
            string path;
            try
            {
                path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            }
            catch (Exception)
            {
                continue;   // a folder that cannot be resolved is simply not offered
            }

            if (folder == Environment.SpecialFolder.UserProfile)
                path = Path.Combine(path, "Downloads");

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            yield return new CatalogSeed(Category, name, ItemKinds.Folder, path, Note: Path.GetFileName(path));
        }
    }
}
