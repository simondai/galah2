using System.Diagnostics;

namespace Galah2.Services;

/// <summary>
/// Protocol-link gate (Sprint 1, ROADMAP B2). Windows registers many URI schemes; launching an
/// arbitrary one is a shell-execution surface, so Galah accepts only an explicit allowlist of
/// Microsoft shell/settings schemes. Anything else is refused before ShellExecute is reached.
/// </summary>
public static class ProtocolSafety
{
    /// <summary>Schemes Galah is willing to launch. Compared case-insensitively without the colon.</summary>
    public static readonly string[] AllowedSchemes =
    [
        "ms-settings",      // Settings pages
        "ms-drive-to",      // Drive tuning (same family as ms-settings)
        "ms-msa",           // Account settings
        "search",           // Search contract (search-ms:)
        "shell",            // shell:startup, shell:downloads ...
        "about",            // ms-about (edge)
        "windowsdefender",  // Defender UI
        "ms-edge",          // Edge entry points
    ];

    /// <summary>
    /// True when <paramref name="target"/> is "scheme:rest" with a scheme on the allowlist.
    /// </summary>
    /// <param name="target">Raw stored target.</param>
    /// <returns>Validity.</returns>
    public static bool IsAllowed(string? target, out string scheme)
    {
        scheme = "";
        if (string.IsNullOrWhiteSpace(target)) return false;
        var colon = target.IndexOf(':');
        if (colon <= 0 || colon == target.Length - 1) return false;
        var candidate = target[..colon].Trim().ToLowerInvariant();
        if (candidate.Length == 0 || !UriSchemeChars(candidate)) return false;
        if (Array.IndexOf(AllowedSchemes, candidate) < 0) return false;
        scheme = candidate;
        return true;
    }

    /// <summary>True for a syntactically valid URI scheme name (RFC 3986 prefix rule).</summary>
    /// <param name="candidate">Scheme text without the colon.</param>
    private static bool UriSchemeChars(string candidate)
    {
        if (!char.IsLetter(candidate[0])) return false;
        foreach (var c in candidate)
        {
            if (char.IsLetterOrDigit(c) || c is '+' or '.' or '-') continue;
            return false;
        }
        return true;
    }
}
