using Galah2.Models;
using Galah2.Services;

// PasswordGenerator probes (spec 2026-11-24-randpass §3/§4). Pure logic - no Avalonia needed,
// which also means this exact code path runs under the AOT probe build with zero trimming risk.
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        if (!ok) _fail++;
    }

    private static int DoubleCount(string s)
    {
        var n = 0;
        for (var i = 1; i < s.Length; i++)
            if (s[i] == s[i - 1] && char.IsDigit(s[i])) n++;
        return n;
    }

    public static int Main()
    {
        // ── length discipline ──
        var o = new PasswordOptions { Length = 14, DoubleDigits = false, UseFormat = false };
        Check("content is exactly Length (no format)",
            Enumerable.Range(0, 200).All(i => PasswordGenerator.Generate(o).Length == 14));

        // ── single-class pools ──
        var lower = new PasswordOptions { Lower = true, Upper = false, Digits = false, CustomEnabled = false, NoConfusables = false, DoubleDigits = false };
        Check("lower-only pool stays in a-z",
            Enumerable.Range(0, 200).All(i => PasswordGenerator.Generate(lower).All(c => c >= 'a' && c <= 'z')));

        var upper = new PasswordOptions { Lower = false, Upper = true, Digits = false, CustomEnabled = false, NoConfusables = false, DoubleDigits = false };
        Check("upper-only pool stays in A-Z",
            Enumerable.Range(0, 200).All(i => PasswordGenerator.Generate(upper).All(c => c >= 'A' && c <= 'Z')));

        var digits = new PasswordOptions { Lower = false, Upper = false, Digits = true, CustomEnabled = false, NoConfusables = false, DoubleDigits = false };
        Check("digits-only pool stays in 0-9",
            Enumerable.Range(0, 200).All(i => PasswordGenerator.Generate(digits).All(c => c >= '0' && c <= '9')));

        // ── confusable stripping ──
        var noConfDig = new PasswordOptions { Lower = false, Upper = false, Digits = true, CustomEnabled = false, NoConfusables = true, DoubleDigits = false };
        Check("no-confusables strips 1 and 3 from digits",
            Enumerable.Range(0, 2000).All(i => PasswordGenerator.Generate(noConfDig).All(c => c != '1' && c != '3')));

        var noConfLtr = new PasswordOptions { Lower = true, Upper = true, Digits = false, CustomEnabled = false, NoConfusables = true, DoubleDigits = false };
        Check("no-confusables strips l and I from letters",
            Enumerable.Range(0, 2000).All(i => PasswordGenerator.Generate(noConfLtr).All(c => c != 'l' && c != 'I')));

        var withConf = new PasswordOptions { Lower = true, Upper = true, Digits = false, CustomEnabled = false, NoConfusables = false, DoubleDigits = false };
        Check("confusables kept when the option is off",
            Enumerable.Range(0, 2000).Any(i => PasswordGenerator.Generate(withConf).Contains('l'))
            && Enumerable.Range(0, 2000).Any(i => PasswordGenerator.Generate(withConf).Contains('I')));

        // ── custom chars ──
        var custom = new PasswordOptions { Lower = false, Upper = false, Digits = false, CustomEnabled = true, CustomChars = "X!", NoConfusables = false, DoubleDigits = false };
        Check("custom-only pool is exactly the custom chars",
            Enumerable.Range(0, 200).All(i => PasswordGenerator.Generate(custom).All(c => c == 'X' || c == '!')));

        var customOff = new PasswordOptions { Lower = true, Upper = false, Digits = false, CustomEnabled = false, CustomChars = "X!", NoConfusables = false, DoubleDigits = false };
        Check("custom chars excluded when CustomEnabled is off",
            Enumerable.Range(0, 200).All(i => PasswordGenerator.Generate(customOff).All(c => c >= 'a' && c <= 'z')));

        var emptyPool = new PasswordOptions { Lower = false, Upper = false, Digits = false, CustomEnabled = false, NoConfusables = false, DoubleDigits = false, Length = 10 };
        Check("empty pool falls back to the full set (never empty result)",
            Enumerable.Range(0, 20).All(i => PasswordGenerator.Generate(emptyPool).Length == 10));

        // ── two-digit numbers ──
        var ddOn = new PasswordOptions { Lower = false, Upper = false, Digits = true, CustomEnabled = false, NoConfusables = false, DoubleDigits = true, Length = 64 };
        var ddOnPasses = Enumerable.Range(0, 30).Select(i => PasswordGenerator.Generate(ddOn)).ToList();
        Check("double-digit ON: every 64-char password contains a doubled digit",
            ddOnPasses.All(p => DoubleCount(p) > 0),
            ddOnPasses.Count(p => DoubleCount(p) == 0) + " without");
        Check("double-digit ON: content length still exactly 64 (doubles take two slots)",
            ddOnPasses.All(p => p.Length == 64));

        var ddOff = new PasswordOptions { Lower = false, Upper = false, Digits = true, CustomEnabled = false, NoConfusables = false, DoubleDigits = false, Length = 64 };
        var ddOffAvg = Enumerable.Range(0, 200).Select(i => DoubleCount(PasswordGenerator.Generate(ddOff))).Average();
        var ddOnAvg = ddOnPasses.Select(DoubleCount).Average();
        // Accidental adjacent digits average ~6.3 per 64 chars; the 25% mechanism doubles that.
        Check("double-digit ON rate clearly exceeds accidental-only rate",
            ddOnAvg > ddOffAvg * 1.5, $"on={ddOnAvg:F1} off={ddOffAvg:F1}");

        // ── formats (content total always = Length; dashes are extra) ──
        var f4 = new PasswordOptions { Lower = true, Upper = true, Digits = true, CustomEnabled = false, NoConfusables = false, DoubleDigits = false, UseFormat = true, Format = "4-*-4", Length = 14 };
        var f4r = PasswordGenerator.Generate(f4);
        var f4seg = f4r.Split('-');
        Check("4-*-4 @14 → three segments [4,6,4], total 16",
            f4seg.Length == 3 && f4seg[0].Length == 4 && f4seg[1].Length == 6 && f4seg[2].Length == 4 && f4r.Length == 16,
            f4r);
        Check("4-*-4: 50 generations all hold the shape",
            Enumerable.Range(0, 50).All(i =>
            {
                var s = PasswordGenerator.Generate(f4).Split('-');
                return s.Length == 3 && s[0].Length == 4 && s[1].Length == 6 && s[2].Length == 4;
            }));

        var f2 = new PasswordOptions { Lower = true, Upper = true, Digits = true, CustomEnabled = false, NoConfusables = false, DoubleDigits = false, UseFormat = true, Format = "2-*-4-*-2", Length = 14 };
        var f2r = PasswordGenerator.Generate(f2);
        var f2seg = f2r.Split('-');
        Check("2-*-4-*-2 @14 → five segments [2,3,4,3,2], total 18",
            f2seg.Length == 5 && f2seg[0].Length == 2 && f2seg[1].Length == 3 && f2seg[2].Length == 4 && f2seg[3].Length == 3 && f2seg[4].Length == 2 && f2r.Length == 18,
            f2r);

        // Length 8: the star segment sizes to 0 and drops (with its dash).
        var fMin = new PasswordOptions { UseFormat = true, Format = "4-*-4", Length = 8, DoubleDigits = false, CustomEnabled = false };
        var fMinR = PasswordGenerator.Generate(fMin);
        Check("4-*-4 @8 → middle star is 0 and drops → [4,4] + 1 dash",
            fMinR.Length == 9 && fMinR.Split('-').Length == 2, fMinR);

        // Length 4 < fixed sum 8: stars vanish, the content clamps → plain 4 chars.
        var fTiny = new PasswordOptions { UseFormat = true, Format = "4-*-4", Length = 4, DoubleDigits = false, CustomEnabled = false };
        var fTinyR = PasswordGenerator.Generate(fTiny);
        Check("4-*-4 @4 (below fixed sum) → plain 4 chars, no dashes",
            fTinyR.Length == 4 && !fTinyR.Contains('-'), fTinyR);

        // 2-*-4-*-2 @6 < fixed sum 8: fixed segments clamp to the available content.
        var fTiny2 = new PasswordOptions { UseFormat = true, Format = "2-*-4-*-2", Length = 6, DoubleDigits = false, CustomEnabled = false };
        var fTiny2R = PasswordGenerator.Generate(fTiny2);
        var fTiny2Seg = fTiny2R.Split('-');
        Check("2-*-4-*-2 @6 → clamps to [2,4] + 1 dash",
            fTiny2Seg.Length == 2 && fTiny2Seg[0].Length == 2 && fTiny2Seg[1].Length == 4, fTiny2R);

        // Format + confusables interact: dashes are the only non-pool char.
        var fConf = new PasswordOptions { UseFormat = true, Format = "4-*-4", Length = 14, NoConfusables = true, DoubleDigits = false, CustomEnabled = false };
        Check("formatted output obeys confusable stripping in the segments",
            Enumerable.Range(0, 200).All(i =>
                PasswordGenerator.Generate(fConf).Replace("-", "").All(c => c != '1' && c != 'l' && c != 'I' && c != '3' && c != 'L')));

        // Format + double digits: the CONTENT (dashes removed) stays exactly Length.
        var fDd = new PasswordOptions { UseFormat = true, Format = "4-*-4", Length = 14, DoubleDigits = true, Digits = true, Lower = false, Upper = false, CustomEnabled = false, NoConfusables = false };
        Check("format + double digits: content stays 14 (total 16 with dashes)",
            Enumerable.Range(0, 50).All(i =>
            {
                var s = PasswordGenerator.Generate(fDd);
                return s.Length == 16 && s.Replace("-", "").Length == 14;
            }));

        // ── defaults match the screenshot state ──
        var def = new PasswordOptions();
        Check("defaults = screenshot state (14/14, all on, @#$, format off)",
            def.Length == 14 && def.Count == 14 && def.Lower && def.Upper && def.Digits
            && def.DoubleDigits && def.NoConfusables && def.CustomEnabled && def.CustomChars == "@#$"
            && !def.UseFormat && def.Format == "4-*-4");

        Console.WriteLine(_fail == 0 ? "=== ALL PASS ===" : "=== " + _fail + " FAILED ===");
        return _fail == 0 ? 0 : 1;
    }
}
