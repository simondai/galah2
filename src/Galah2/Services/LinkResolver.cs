
using Galah2.Persistence;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>Snapshot of the four fields Galah imports from a shell link (spec §2.1 / design §3.4).</summary>
public sealed record ShellLinkInfo(
    string Target,
    string? Arguments,
    string? WorkingDirectory,
    string? IconSource,
    int IconIndex,
    string? Error)
{
    /// <summary>True when a usable target path was recovered.</summary>
    public bool Ok => Error is null && !string.IsNullOrWhiteSpace(Target);
}

/// <summary>
/// Reads (and, for test fixtures, writes) .lnk files through IShellLinkW. Import is a one-way
/// snapshot: after committing, the item has no relationship left with the source .lnk (design §3.4).
/// </summary>
public static class LinkResolver
{
    private const uint SlrNoUi = 0x00000001;
    private const int StgmRead = 0x00000000;
    private const int StgmReadWrite = 0x00000002;

    /// <summary>
    /// Resolve a .lnk into target / arguments / working directory / icon location, following the
    public static ShellLinkInfo Resolve(string lnkPath, IntPtr owner = default)
    {
        if (string.IsNullOrWhiteSpace(lnkPath))
            return new ShellLinkInfo("", null, null, null, 0, "空路径");

        var osPath = PathNorm.ToOs(lnkPath);
        if (!File.Exists(osPath))
            return new ShellLinkInfo("", null, null, null, 0, "快捷方式不存在");

        if (!ShellLinkCom.TryLoad(Path.GetFullPath(osPath), out var link) || link is null)
            return new ShellLinkInfo("", null, null, null, 0, "快捷方式组件不可用");

        try
        {
            link.Resolve(owner, SlrNoUi);   // best effort: keep whatever the file already says

            var target = link.GetPath();
            var args = link.GetArguments();
            var workdir = link.GetWorkingDirectory();
            var (icon, iconIndex) = link.GetIconLocation();

            // Some authoring tools bake the index into the path string instead of the out parameter.
            if (icon.Length > 0)
            {
                var comma = icon.LastIndexOf(',');
                if (comma > 1 && int.TryParse(icon[(comma + 1)..], out var baked))
                {
                    icon = icon[..comma];
                    if (iconIndex == 0) iconIndex = baked;
                }
            }
            if (icon.Length == 0) iconIndex = 0;

            if (string.IsNullOrWhiteSpace(target))
                return new ShellLinkInfo("", null, null, null, 0, "快捷方式没有可用的目标路径");

            return new ShellLinkInfo(
                PathNorm.ToStored(target),
                string.IsNullOrWhiteSpace(args) ? null : args,
                string.IsNullOrWhiteSpace(workdir) ? null : PathNorm.ToStored(workdir),
                string.IsNullOrWhiteSpace(icon) ? null : PathNorm.ToStored(icon),
                iconIndex,
                null);
        }
        finally
        {
            link.Dispose();
        }
    }

    /// <summary>
    /// Write a .lnk with the given triple. Exposed for the quality-gate fixture: it exercises the
    /// same vtable as <see cref="Resolve"/>.
    /// </summary>
    /// <returns>True when the file was created.</returns>
    public static bool TryCreate(string lnkPath, string target, string? arguments, string? workingDirectory,
        string? iconLocation, int iconIndex)
    {
        using var link = ShellLinkCom.Create();
        if (link is null) return false;

        link.SetPath(PathNorm.ToOs(target));
        if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
        if (!string.IsNullOrEmpty(workingDirectory)) link.SetWorkingDirectory(PathNorm.ToOs(workingDirectory));
        if (!string.IsNullOrEmpty(iconLocation)) link.SetIconLocation(PathNorm.ToOs(iconLocation), iconIndex);
        link.Save(Path.GetFullPath(PathNorm.ToOs(lnkPath)));
        return File.Exists(PathNorm.ToOs(lnkPath));
    }
}
