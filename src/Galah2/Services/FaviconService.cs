using System.Net;
using System.Net.Http;
using Galah2.Persistence;

namespace Galah2.Services;

/// <summary>Outcome of a favicon attempt.</summary>
/// <param name="Ok">True when an image was stored.</param>
/// <param name="StoredPath">Stored (forward-slash) path of the saved image.</param>
/// <param name="Message">User-facing result line.</param>
public sealed record FaviconResult(bool Ok, string? StoredPath, string Message);

/// <summary>
/// Fetches a site icon for a URL entry (Sprint 4 / ROADMAP F2).
///
/// Product rule from the owner (2026-11-19): Galah does not talk to the network on its own.
/// This service is only ever reached from the explicit "获取图标" button in the URL editor -
/// nothing here is scheduled, prefetched, or triggered by rendering.
///
/// Only the site itself is contacted; there is no third-party icon proxy. The lookup order is
/// the de-facto standard one: /favicon.ico, then /apple-touch-icon.png.
/// </summary>
public static class FaviconService
{
    /// <summary>Refuse anything larger than this (a "favicon" of a megabyte is not an icon).</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Overall budget for one attempt (both candidates, connect plus body).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    private static readonly string[] Candidates = ["/favicon.ico", "/apple-touch-icon.png"];

    /// <summary>
    /// Try to fetch and store the icon of <paramref name="url"/>.
    /// </summary>
    /// <param name="url">Stored target of the item (must be http/https).</param>
    /// <param name="itemId">Item id, used in the file name so overrides stay attributable.</param>
    /// <param name="paths">Data directory of the running instance.</param>
    public static async Task<FaviconResult> FetchAsync(string url, string itemId, AppDataPaths paths)
    {
        if (!UrlSafety.IsHttpUrl(url, out var absolute))
            return new FaviconResult(false, null, "只有 http/https 链接可以获取图标");

        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var baseUri) || baseUri is null)
            return new FaviconResult(false, null, "链接无法解析");

        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            using var http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,          // one hop only: never chase a redirect chain
                UseCookies = false,
            })
            {
                Timeout = Timeout,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GalahLauncher/1.0 (icon lookup)");

            foreach (var candidate in Candidates)
            {
                var probe = new Uri(baseUri, candidate);
                var bytes = await TryDownloadAsync(http, probe, timeout.Token).ConfigureAwait(false);
                if (bytes is null) continue;

                if (!IsProbablyImage(bytes, out var reason))
                    return new FaviconResult(false, null, reason);

                var stored = Save(bytes, itemId, paths);
                return stored is null
                    ? new FaviconResult(false, null, "图标保存失败")
                    : new FaviconResult(true, stored, "已获取图标");
            }

            return new FaviconResult(false, null, "站点没有提供图标");
        }
        catch (TaskCanceledException)
        {
            return new FaviconResult(false, null, "获取超时，稍后再试");
        }
        catch (OperationCanceledException)
        {
            return new FaviconResult(false, null, "获取已取消");
        }
        catch (Exception ex)
        {
            return new FaviconResult(false, null, "获取失败：" + Shorten(ex.Message));
        }
    }

    private static async Task<byte[]?> TryDownloadAsync(HttpClient http, Uri uri, CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var declared = response.Content.Headers.ContentLength ?? -1;
            if (declared > MaxBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[MaxBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }

            // a body that keeps streaming past the cap is rejected, not truncated
            return total > 0 && total <= MaxBytes ? buffer[..total] : null;
        }
        catch (Exception)
        {
            // one candidate failing must not abort the chain
            return null;
        }
    }

    private static string? Save(byte[] bytes, string itemId, AppDataPaths paths)
    {
        try
        {
            Directory.CreateDirectory(paths.IconOverrideDirectory);
            var key = IconService.ContentHash(Convert.ToHexString(bytes));
            var safe = new string(itemId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (safe.Length == 0) safe = "item";
            var file = Path.Combine(paths.IconOverrideDirectory, $"web-{safe}-{key[..12]}.png");
            if (!File.Exists(file)) File.WriteAllBytes(file, bytes);
            return PathNorm.ToStored(file);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Magic-number gate so a friendly 404 page served with status 200 never becomes an icon.
    /// </summary>
    private static bool IsProbablyImage(byte[] bytes, out string reason)
    {
        reason = "";
        if (bytes.Length < 12)
        {
            reason = "返回内容太小，不像图标";
            return false;
        }

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;   // PNG
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return true;                                          // JPEG
        if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;                       // GIF
        if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0) return true;               // ICO
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return true; // WebP

        reason = "返回的不是图片（可能是网页）";
        return false;
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "…";
}
