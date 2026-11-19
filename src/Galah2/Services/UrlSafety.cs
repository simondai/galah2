namespace Galah2.Services;

/// <summary>
/// URL scheme gate (spec §2.3 / §3.1, TL-A7): only http and https may enter the library or be
/// handed to the default browser. file://, javascript: and everything else are rejected.
/// </summary>
public static class UrlSafety
{
    /// <summary>Accepted schemes.</summary>
    public static readonly string[] AllowedSchemes = ["http", "https"];

    /// <summary>
    /// Validate a stored URL and return a form safe for ShellExecute.
    /// </summary>
    /// <param name="raw">User-supplied URL (may carry surrounding whitespace or stored slashes).</param>
    /// <param name="normalized">The validated URL when the method returns true.</param>
    /// <returns>True when the scheme is http/https and a host is present.</returns>
    public static bool IsHttpUrl(string? raw, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        if (Array.IndexOf(AllowedSchemes, uri.Scheme) < 0) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        normalized = uri.AbsoluteUri;
        return true;
    }

    /// <summary>Scheme-only test used by the validator when it must not rewrite the stored value.</summary>
    /// <param name="raw">Candidate URL.</param>
    public static bool IsHttpUrl(string? raw) => IsHttpUrl(raw, out _);

    /// <summary>Display name for a URL: the host, per spec §2.2 (url ← 主机名).</summary>
    /// <param name="raw">Candidate URL.</param>
    public static string? HostName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        return string.IsNullOrEmpty(uri.Host) ? null : uri.Host;
    }
}
