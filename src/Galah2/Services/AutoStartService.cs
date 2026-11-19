using Microsoft.Win32;

namespace Galah2.Services;

/// <summary>
/// Boot-with-Windows preference. Galah is not a store package, so it uses the HKCU Run key
/// (design §1.1 S8) — the only registry write the product is allowed to make. Value name: "Galah".
/// </summary>
public static class AutoStartService
{
    /// <summary>Run key path under HKEY_CURRENT_USER.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Registry value name owned by Galah.</summary>
    public const string ValueName = "Galah";

    /// <summary>Read the current registry state, optionally comparing it with a stored command.</summary>
    /// <param name="executablePath">Absolute path of the running executable.</param>
    /// <returns>True when the Run value points at this executable.</returns>
    public static bool IsEnabled(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value)) return false;
            return PathsMatch(value, executablePath);
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    /// <summary>
    /// Write or remove the Run value so the registry matches the preference (called on toggle and to
    /// re-assert on boot; a re-assert never rewrites settings.json).
    /// </summary>
    /// <param name="enabled">Desired state.</param>
    /// <param name="executablePath">Absolute path of the running executable.</param>
    /// <param name="error">Reason when the write was refused.</param>
    /// <returns>True when the registry now matches the request.</returns>
    public static bool SetEnabled(bool enabled, string executablePath, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                error = "无法打开系统启动项配置";
                return false;
            }
            if (enabled)
                key.SetValue(ValueName, "\"" + executablePath + "\"", RegistryValueKind.String);
            else if (key.GetValue(ValueName) is not null)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "启动项写入被系统拒绝";
            return false;
        }
        catch (System.Security.SecurityException)
        {
            error = "启动项写入被系统拒绝";
            return false;
        }
        catch (Exception ex)
        {
            error = "启动项写入失败：" + ex.Message;
            return false;
        }
    }

    private static bool PathsMatch(string registryValue, string executablePath)
    {
        var left = Unquote(registryValue);
        var right = Unquote(executablePath);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[1] != '"')
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return text[1..end];
        }
        return text;
    }

}
