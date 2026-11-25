using System.Security.Cryptography;
using System.Text;
using Galah2.Models;

namespace Galah2.Services;

/// <summary>
/// Pure RandPass generator - no UI, no IO, AOT-safe (RandomNumberGenerator, zero reflection).
///
/// Rules (spec 2026-11-24-randpass §3/§4, user-confirmed):
///  * pool = checked classes + distinct custom chars (printable ASCII), look-alikes stripped;
///  * content is EXACTLY Length characters; a two-digit number (11/22/…) takes two slots,
///    rolled at 25% when a digit slot is drawn and a second slot remains;
///  * a format pattern adds dashes between segments; the content total stays Length, so the
///    displayed total = Length + segments − 1.
/// </summary>
public static class PasswordGenerator
{
    private static readonly char[] Lower = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
    private static readonly char[] Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] Digits = "0123456789".ToCharArray();
    private static readonly char[] Confusables = "1lI3L".ToCharArray();

    /// <summary>Two-digit-number probability (spec: 25%).</summary>
    public const int DoubleDigitPercent = 25;

    public static string Generate(PasswordOptions o)
    {
        var length = Math.Clamp(o.Length, PasswordOptions.MinLength, PasswordOptions.MaxLength);
        var pool = BuildPool(o);
        var content = BuildContent(o, length, pool);
        return o.UseFormat ? Format(content, o.Format) : content;
    }

    /// <summary>Random int in [0, n). n == 0 is a caller bug; never throws.</summary>
    private static int RndInt(int n) => n <= 1 ? 0 : RandomNumberGenerator.GetInt32(n);

    private static char[] BuildPool(PasswordOptions o)
    {
        var list = new List<char>();
        if (o.Lower) list.AddRange(Lower);
        if (o.Upper) list.AddRange(Upper);
        if (o.Digits) list.AddRange(Digits);
        if (o.CustomEnabled && o.CustomChars is not null)
            foreach (var c in o.CustomChars.Distinct())
                if (c >= 32 && c < 127) list.Add(c);

        ApplyConfusables(list, o.NoConfusables);

        if (list.Count == 0)
        {
            // Degenerate selection (nothing checked, custom empty): fall back to the full set
            // so generation can never stall on an empty pool.
            list.AddRange(Lower); list.AddRange(Upper); list.AddRange(Digits);
            ApplyConfusables(list, o.NoConfusables);
        }
        return list.ToArray();
    }

    private static void ApplyConfusables(List<char> list, bool strip)
    {
        if (!strip) return;
        for (var i = list.Count - 1; i >= 0; i--)
            if (Array.IndexOf(Confusables, list[i]) >= 0) list.RemoveAt(i);
    }

    private static string BuildContent(PasswordOptions o, int length, char[] pool)
    {
        var buf = new char[length];
        var i = 0;
        while (i < length)
        {
            var c = pool[RndInt(pool.Length)];
            // "No similar characters" must apply here too: this branch draws its own digit, so
            // applying the filter only to the pool still produced 11 / 33. Duplicate the digit
            // that was actually drawn (CHANGELOG: "放大成 dd"), rather than replacing it with a
            // fresh random one.
            // Belt and braces: BuildPool already strips confusables from the pool, but keep the
            // check here too so a future pool change cannot reintroduce 11 / 33 in this branch.
            if (o.DoubleDigits && c is >= '0' and <= '9'
                && (!o.NoConfusables || Array.IndexOf(Confusables, c) < 0)
                && i + 1 < length && RndInt(100) < DoubleDigitPercent)
            {
                buf[i] = c;
                buf[i + 1] = c;
                i += 2;
            }
            else
            {
                buf[i] = c;
                i++;
            }
        }
        return new string(buf);
    }

    /// <summary>
    /// Insert dashes per the pattern. Segment sizes: numbers verbatim, `*` segments share
    /// (content length − Σnumbers) evenly with the remainder to the first `*`. Segments that
    /// end up empty (or exhaust the content) are dropped with their dash; an unparseable or
    /// fully-degenerate pattern returns the plain content.
    /// </summary>
    private static string Format(string content, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return content;

        var tokens = pattern.Split('-');
        var sizes = new int[tokens.Length];
        var starIdx = new List<int>();
        var fixedSum = 0;

        for (var k = 0; k < tokens.Length; k++)
        {
            var t = tokens[k].Trim();
            if (t == "*")
            {
                starIdx.Add(k);
            }
            else if (t.Length > 0 && t.All(char.IsDigit) && int.TryParse(t, out var n))
            {
                sizes[k] = n;
                fixedSum += n;
            }
            else
            {
                return content; // not a known pattern shape - plain
            }
        }

        if (starIdx.Count > 0)
        {
            var rem = content.Length - fixedSum;
            if (rem >= 0)
            {
                var baseSize = rem / starIdx.Count;
                var extra = rem % starIdx.Count;
                for (var s = 0; s < starIdx.Count; s++)
                    sizes[starIdx[s]] = baseSize + (s < extra ? 1 : 0);
            }
            // rem < 0: star sizes stay 0 and their segments drop below.
        }

        var sb = new StringBuilder(content.Length + tokens.Length);
        var pos = 0;
        var first = true;
        for (var k = 0; k < sizes.Length; k++)
        {
            var take = Math.Min(sizes[k], content.Length - pos);
            if (take <= 0) continue;
            if (!first) sb.Append('-');
            first = false;
            sb.Append(content.AsSpan(pos, take));
            pos += take;
        }
        return first ? content : sb.ToString();
    }
}
