using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Galah2.Models;
using Galah2.Persistence;
using Galah2.Services.Interop;

namespace Galah2.Services;

/// <summary>Outcome of a launch attempt (design §4: Launcher.Launch(item) → LaunchResult).</summary>
public enum LaunchStatus
{
    /// <summary>The shell/process call succeeded.</summary>
    Ok,

    /// <summary>Target is missing or unreachable.</summary>
    NotFound,

    /// <summary>Elevation refused (UAC cancelled or access denied).</summary>
    Denied,

    /// <summary>Anything else, with a human-readable reason.</summary>
    Error,
}

/// <summary>Launch result carrying the message shown in the toast.</summary>
public readonly record struct LaunchResult(LaunchStatus Status, string? Message)
{
    /// <summary>True when the launch succeeded.</summary>
    public bool Ok => Status == LaunchStatus.Ok;

    /// <summary>Success.</summary>
    public static LaunchResult Success() => new(LaunchStatus.Ok, null);

    /// <summary>Failure with a message.</summary>
    public static LaunchResult Fail(LaunchStatus status, string message) => new(status, message);
}

/// <summary>
/// Value snapshot handed to the launcher: the item as it was at click time, so a concurrent edit
/// cannot change a running launch (spec §4.2 — snapshot semantics, no locks, no queue).
/// </summary>
public sealed record LaunchRequest(string Kind, string Target, string? Args, string? Workdir, bool RunAsAdmin)
{
    /// <summary>Build a request from a catalog item (deep snapshot of the launch-relevant fields).</summary>
    public static LaunchRequest From(CatalogItem item) => new(item.Kind, item.Target, item.Args, item.Workdir, item.RunAsAdmin);
}

/// <summary>
/// Launch dispatch of spec §3.1. This class is UI-free, store-free and headless-testable: it never
/// writes anything (INV-4) and performs no persistence at all.
/// </summary>
public static class LauncherService
{
    /// <summary>Extensions accepted for kind=app targets (spec §2.3; the superset beyond exe/lnk covers
    /// what a resolved .lnk or a user-typed path legitimately points at).</summary>
    public static readonly string[] ExecutableExtensions = [".exe", ".lnk", ".msi", ".bat", ".cmd", ".com"];

    /// <summary>
    /// Launch one item snapshot: app → CreateProcess (args + cwd, default cwd = target parent dir),
    /// runAsAdmin → ShellExecute "runas", url → http/https only via ShellExecute, file/folder → ShellExecute open.
    /// </summary>
    /// <param name="request">The launch snapshot.</param>
    /// <returns>Result to be rendered as a toast by the caller; nothing is persisted either way.</returns>
    public static LaunchResult Launch(LaunchRequest request)
    {
        var target = PathNorm.ToOs(request.Target);
        if (string.IsNullOrWhiteSpace(target))
            return LaunchResult.Fail(LaunchStatus.NotFound, "目标为空");

        switch (request.Kind)
        {
            case ItemKinds.Url:
                if (!UrlSafety.IsHttpUrl(request.Target, out var url))
                    return LaunchResult.Fail(LaunchStatus.Error, "仅允许 http/https 链接");
                return ShellOpen(url, null, null, false);

            case ItemKinds.Protocol:
                // Registered shell scheme (ms-settings:, shell:...) - ShellExecute is the only
                // correct launcher; CreateProcess cannot start a URI. Allowlist enforced upstream.
                if (!ProtocolSafety.IsAllowed(request.Target, out _))
                    return LaunchResult.Fail(LaunchStatus.Error, "该协议不在允许列表内");
                return ShellOpen(request.Target, null, null, false);

            case ItemKinds.Folder:
                if (!Directory.Exists(target))
                    return LaunchResult.Fail(LaunchStatus.NotFound, $"目录不存在：{request.Target}");
                return ShellOpen(target, null, null, request.RunAsAdmin);

            case ItemKinds.File:
                if (!File.Exists(target))
                    return LaunchResult.Fail(LaunchStatus.NotFound, $"文件不存在：{request.Target}");
                return ShellOpen(target, request.Args, request.Workdir, request.RunAsAdmin);

            case ItemKinds.App:
                // A .lnk that survived an older import is handed to the shell instead of CreateProcess.
                if (HasExtension(target, ".lnk"))
                {
                    if (!File.Exists(target))
                        return LaunchResult.Fail(LaunchStatus.NotFound, $"快捷方式不存在：{request.Target}");
                    return ShellOpen(target, request.Args, request.Workdir, request.RunAsAdmin);
                }
                return LaunchApplication(target, request.Args, request.Workdir, request.RunAsAdmin);

            default:
                return LaunchResult.Fail(LaunchStatus.Error, "未知的条目类型：" + request.Kind);
        }
    }

    /// <summary>
    /// Cheap, read-only validity probe used to paint the "invalid" badge (spec §3.4: derived state,
    /// never persisted — INV-8). It only looks at existence/scheme, never at executability.
    /// </summary>
    /// <param name="item">Item to probe.</param>
    public static bool LooksValid(CatalogItem item) => LooksValid(item.Kind, item.Target);

    /// <summary>Probe variant taking raw fields (used by tiles to avoid touching the model).</summary>
    public static bool LooksValid(string kind, string storedTarget)
    {
        switch (kind)
        {
            case ItemKinds.Url: return UrlSafety.IsHttpUrl(storedTarget);
            case ItemKinds.Protocol: return ProtocolSafety.IsAllowed(storedTarget, out _);
            case ItemKinds.Folder: return Directory.Exists(PathNorm.ToOs(storedTarget));
            case ItemKinds.File:
            case ItemKinds.App: return File.Exists(PathNorm.ToOs(storedTarget));
            default: return false;
        }
    }

    private static LaunchResult LaunchApplication(string osTarget, string? args, string? storedWorkdir, bool runAsAdmin)
    {
        if (!File.Exists(osTarget))
            return LaunchResult.Fail(LaunchStatus.NotFound, $"目标不存在：{osTarget.Replace('\\', '/')}");

        if (runAsAdmin)
            return ShellOpen(osTarget, args, storedWorkdir, true);

        var workingDir = ResolveWorkingDirectory(osTarget, storedWorkdir);

        var startup = new STARTUPINFOW
        {
            cb = Marshal.SizeOf<STARTUPINFOW>(),
            dwFlags = (int)Win32.STARTF_USESHOWWINDOW,
            wShowWindow = (short)Win32.SW_SHOWNORMAL,
        };

        var commandLine = BuildCommandLine(osTarget, args);
        var creationFlags = 0u;
        var environment = IntPtr.Zero;

        var started = Win32.CreateProcessW(
            null,
            new StringBuilder(commandLine),
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            creationFlags,
            environment,
            workingDir,
            ref startup,
            out var info);

        if (!started)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"CreateProcessW failed for {osTarget}: {error}");
            return FromError(error, osTarget.Replace('\\', '/'));
        }

        // Galah is a launcher, not a supervisor: close the handles immediately.
        Win32.CloseHandle(info.hThread);
        Win32.CloseHandle(info.hProcess);
        return LaunchResult.Success();
    }

    private static LaunchResult ShellOpen(string osPathOrUrl, string? args, string? storedWorkdir, bool runAsAdmin)
    {
        var info = new SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
            fMask = (int)Win32.SEE_MASK_NOCLOSEPROCESS,
            hwnd = IntPtr.Zero,
            lpVerb = runAsAdmin ? "runas" : "open",
            lpFile = osPathOrUrl,
            lpParameters = string.IsNullOrWhiteSpace(args) ? null : args,
            lpDirectory = string.IsNullOrWhiteSpace(storedWorkdir) ? null : PathNorm.ToOs(storedWorkdir),
            nShow = Win32.SW_SHOWNORMAL,
        };

        var ok = Win32.ShellExecuteExW(ref info);
        if (!ok)
        {
            var error = Marshal.GetLastWin32Error();
            Debug.WriteLine($"ShellExecuteExW failed for {osPathOrUrl}: {error}");
            if (info.hProcess != IntPtr.Zero) Win32.CloseHandle(info.hProcess);
            return FromError(error, osPathOrUrl);
        }
        if (info.hProcess != IntPtr.Zero) Win32.CloseHandle(info.hProcess);
        return LaunchResult.Success();
    }

    /// <summary>Translate a Win32 / ShellExecute error code into the four documented outcomes.</summary>
    internal static LaunchResult FromError(int error, string displayedTarget) => error switch
    {
        Win32Error.FileNotFound or Win32Error.PathNotFound or Win32Error.BadPath =>
            LaunchResult.Fail(LaunchStatus.NotFound, $"找不到目标：{displayedTarget}"),
        Win32Error.Cancelled =>
            LaunchResult.Fail(LaunchStatus.Denied, "授权被取消（UAC）"),
        Win32Error.AccessDenied or Win32Error.ElevationRequired =>
            LaunchResult.Fail(LaunchStatus.Denied, "需要更高权限，操作未完成"),
        Win32Error.NoAssociation =>
            LaunchResult.Fail(LaunchStatus.Error, "没有可用于该类型的默认程序"),
        _ => LaunchResult.Fail(LaunchStatus.Error, $"启动失败（Win32 错误 {error}）：{displayedTarget}"),
    };

    private static string ResolveWorkingDirectory(string osTarget, string? storedWorkdir)
    {
        if (!string.IsNullOrWhiteSpace(storedWorkdir))
        {
            var dir = PathNorm.ToOs(storedWorkdir);
            if (Directory.Exists(dir)) return dir;
        }
        // spec §3.1: empty workdir → the directory that contains the target.
        var parent = Path.GetDirectoryName(osTarget);
        return parent is not null && Directory.Exists(parent) ? parent : "";
    }

    /// <summary>Command line for CreateProcess: quoted program path + args verbatim (spec §2.3: args 原样存取).</summary>
    internal static string BuildCommandLine(string osTarget, string? args)
    {
        var builder = new StringBuilder(2 + osTarget.Length);
        builder.Append('"').Append(osTarget).Append('"');
        if (!string.IsNullOrWhiteSpace(args))
        {
            builder.Append(' ');
            builder.Append(args);
        }
        return builder.ToString();
    }

    private static bool HasExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
