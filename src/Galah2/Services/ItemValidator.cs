using Galah2.Models;
using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>One field-level validation failure (inline error text next to the field).</summary>
public sealed record ValidationError(string Field, string Message);

/// <summary>
/// Commit gate of spec §2.3. Pure and headless-testable: a failing draft never reaches the store,
/// therefore a rejected dialog leaves all three files byte-identical (INV-3, TL-A5).
/// </summary>
public static class ItemValidator
{
    /// <summary>Maximum accepted name length (spec §2.2/§2.3).</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// Validate a draft for commit.
    /// </summary>
    /// <param name="item">Draft to check (kind, name, target, workdir).</param>
    /// <param name="isNew">True for a new item; false when editing an existing one.</param>
    /// <returns>The list of problems; empty when the draft may be committed.</returns>
    public static IReadOnlyList<ValidationError> Validate(CatalogItem item, bool isNew)
    {
        var errors = new List<ValidationError>();
        var name = (item.Name ?? "").Trim();

        if (name.Length == 0)
            errors.Add(new ValidationError(nameof(item.Name), "名称不能为空"));
        else if (name.Length > MaxNameLength)
            errors.Add(new ValidationError(nameof(item.Name), $"名称不能超过 {MaxNameLength} 个字符"));

        if (!ItemKinds.IsKnown(item.Kind))
        {
            errors.Add(new ValidationError(nameof(item.Kind), "未知的条目类型"));
            return errors;
        }

        var target = (item.Target ?? "").Trim();
        if (target.Length == 0)
        {
            errors.Add(new ValidationError(nameof(item.Target), "目标不能为空"));
            return errors;
        }

        switch (item.Kind)
        {
            case ItemKinds.Url:
                if (!UrlSafety.IsHttpUrl(target))
                    errors.Add(new ValidationError(nameof(item.Target), "链接只支持 http/https（file:// 等协议被拒绝）"));
                break;

            case ItemKinds.Protocol:
                if (!ProtocolSafety.IsAllowed(target, out _))
                    errors.Add(new ValidationError(nameof(item.Target), "协议不在允许列表内（仅系统设置/Shell 协议）"));
                break;

            case ItemKinds.Folder:
                if (!IsAbsolute(target))
                    errors.Add(new ValidationError(nameof(item.Target), "文件夹必须是绝对路径"));
                else if (!Directory.Exists(PathNorm.ToOs(target)))
                    errors.Add(new ValidationError(nameof(item.Target), "文件夹不存在"));
                break;

            case ItemKinds.File:
                if (!IsAbsolute(target))
                    errors.Add(new ValidationError(nameof(item.Target), "文件必须是绝对路径"));
                else if (!File.Exists(PathNorm.ToOs(target)))
                    errors.Add(new ValidationError(nameof(item.Target), "文件不存在"));
                break;

            case ItemKinds.App:
                if (!IsAbsolute(target))
                    errors.Add(new ValidationError(nameof(item.Target), "程序必须是绝对路径"));
                else if (!HasKnownExecutableExtension(target))
                    errors.Add(new ValidationError(nameof(item.Target), "程序需要 exe/lnk/msi/bat/cmd/com 扩展名"));
                else if (isNew && !File.Exists(PathNorm.ToOs(target)))
                    errors.Add(new ValidationError(nameof(item.Target), "程序文件不存在"));
                break;
        }

        var workdir = (item.Workdir ?? "").Trim();
        if (workdir.Length > 0 && !Directory.Exists(PathNorm.ToOs(workdir)))
            errors.Add(new ValidationError(nameof(item.Workdir), "工作目录不存在"));

        return errors;
    }

    /// <summary>True when a stored path looks absolute (drive letter or UNC).</summary>
    /// <param name="stored">Stored (forward-slash) path.</param>
    public static bool IsAbsolute(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var os = PathNorm.ToOs(stored.Trim());
        return Path.IsPathRooted(os);
    }

    /// <summary>True when the target ends with an extension Galah accepts for kind=app.</summary>
    /// <param name="stored">Stored (forward-slash) path.</param>
    public static bool HasKnownExecutableExtension(string stored)
    {
        var extension = Path.GetExtension(PathNorm.ToOs(stored));
        return extension.Length > 0 &&
               Array.IndexOf(LauncherService.ExecutableExtensions, extension.ToLowerInvariant()) >= 0;
    }
}
