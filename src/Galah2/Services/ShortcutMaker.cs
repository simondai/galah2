namespace Galah2.Services;

/// <summary>Result of a desktop-shortcut request.</summary>
/// <param name="Ok">True when the link exists afterwards.</param>
/// <param name="Path">Absolute path of the .lnk.</param>
/// <param name="Message">User-facing line (created / updated / failure reason).</param>
public sealed record ShortcutResult(bool Ok, string Path, string Message);

/// <summary>
/// Creates a desktop launcher for this Galah build (Sprint 3 / ROADMAP E2). The writing itself is
/// delegated to <see cref="LinkResolver.TryCreate"/> so there is exactly one shell-link code path
/// in the product - the same one the import tests already exercise.
/// </summary>
public static class ShortcutMaker
{
    /// <summary>Suggested file name of the link.</summary>
    public const string LinkName = "Galah.lnk";

    /// <summary>
    /// Create (or overwrite) the link.
    /// </summary>
    /// <param name="targetExe">Absolute path of Galah.exe.</param>
    /// <param name="iconFile">Icon source, usually app.ico next to the exe; null falls back to the exe icon.</param>
    /// <param name="directory">Target folder; defaults to the user's desktop.</param>
    public static ShortcutResult Create(string targetExe, string? iconFile, string? directory = null)
    {
        var desktop = directory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var link = Path.Combine(desktop, LinkName);

        if (!File.Exists(targetExe))
            return new ShortcutResult(false, link, "找不到 Galah 程序本体，未创建快捷方式");

        try
        {
            Directory.CreateDirectory(desktop);
        }
        catch (Exception ex)
        {
            return new ShortcutResult(false, link, "桌面目录不可用：" + ex.Message);
        }

        var existed = File.Exists(link);
        var icon = iconFile is not null && File.Exists(iconFile) ? iconFile : targetExe;

        var created = LinkResolver.TryCreate(link, targetExe, null,
            Path.GetDirectoryName(targetExe), icon, 0);

        return created
            ? new ShortcutResult(true, link, existed ? "已更新桌面快捷方式" : "已在桌面创建 Galah 快捷方式")
            : new ShortcutResult(false, link, "快捷方式创建失败");
    }

    /// <summary>True when a Galah link already sits on the desktop.</summary>
    /// <param name="directory">Folder to test; defaults to the user's desktop.</param>
    public static bool Exists(string? directory = null)
    {
        var desktop = directory ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return File.Exists(Path.Combine(desktop, LinkName));
    }
}
