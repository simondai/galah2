using System.Globalization;
using System.Text;

namespace Galah2.Services;

/// <summary>Parsed hotkey: RegisterHotKey modifier mask + virtual key + the canonical string form.</summary>
public sealed record HotkeySpec(uint Modifiers, uint VirtualKey, string Text)
{
    /// <summary>Human-readable form shown in the settings page, e.g. <c>Alt+Space</c>.</summary>
    public string Display { get; } = ToDisplay(Text);

    private static string ToDisplay(string text)
    {
        var parts = text.Split('+');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join("+", parts);
    }
}

/// <summary>
/// Generic modifier+key string parser (spec task D: "parse modifier+key strings generically").
/// Accepted form: <c>ctrl+alt+shift+win+f12</c> in any order/case; the canonical stored form lists
/// modifiers in the fixed order ctrl, alt, shift, win followed by the lowercase key name.
/// </summary>
public static class HotkeyParser
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>Always added so the hotkey does not repeat while held.</summary>
    public const uint ModNoRepeat = 0x4000;

    private static readonly string[] ModifierOrder = ["ctrl", "alt", "shift", "win"];

    private static readonly Dictionary<string, uint> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = ModControl, ["control"] = ModControl,
        ["alt"] = ModAlt, ["option"] = ModAlt,
        ["shift"] = ModShift,
        ["win"] = ModWin, ["windows"] = ModWin, ["super"] = ModWin, ["cmd"] = ModWin,
    };

    private static readonly Dictionary<string, uint> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08, ["back"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21,
        ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["slash"] = 0xBF, ["dot"] = 0xBE, ["semicolon"] = 0xBA, ["quote"] = 0xDE,
        ["bracketleft"] = 0xDB, ["bracketright"] = 0xDD, ["backslash"] = 0xDC,
        ["grave"] = 0xC0, ["tilde"] = 0xC0, ["capslock"] = 0x14,
        ["printscreen"] = 0x2C, ["pause"] = 0x13, ["menu"] = 0x07, ["scrolllock"] = 0x91,
    };

    /// <summary>
    /// Parse a hotkey string.
    /// </summary>
    /// <param name="text">Candidate such as "Alt+Space", "ctrl+shift+f12", "WIN+`".</param>
    /// <param name="spec">Canonical spec when parsing succeeded.</param>
    /// <param name="error">Chinese, user-facing reason when parsing failed.</param>
    /// <returns>True when a usable modifier+key combination was produced.</returns>
    public static bool TryParse(string? text, out HotkeySpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "热键为空";
            return false;
        }

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            error = "热键格式无法识别";
            return false;
        }

        uint modifiers = 0;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (ModifierAliases.TryGetValue(tokens[i], out var mod))
            {
                modifiers |= mod;
                continue;
            }
            error = "无法识别的修饰键：" + tokens[i];
            return false;
        }

        if (modifiers == 0)
        {
            error = "热键至少需要一个修饰键（ctrl/alt/shift/win）";
            return false;
        }

        var key = tokens[^1].ToLowerInvariant();
        if (ModifierAliases.ContainsKey(key))
        {
            error = "热键缺少主键（例如 alt+space）";
            return false;
        }

        if (!TryMapKey(key, out var vk))
        {
            error = "不支持的按键名称：" + key;
            return false;
        }

        var canonical = Canonicalize(modifiers, key);
        spec = new HotkeySpec(modifiers | ModNoRepeat, vk, canonical);
        return true;
    }

    /// <summary>Render a RegisterHotKey mask + VK back into the canonical string form.</summary>
    /// <param name="modifiers">Modifier mask (as registered).</param>
    /// <param name="virtualKey">Virtual key code.</param>
    public static string Format(uint modifiers, uint virtualKey)
    {
        var builder = new StringBuilder();
        foreach (var (name, mask) in new[] { ("ctrl", ModControl), ("alt", ModAlt), ("shift", ModShift), ("win", ModWin) })
            if ((modifiers & mask) != 0) builder.Append(name).Append('+');
        builder.Append(KeyName(virtualKey));
        return builder.ToString();
    }

    private static string Canonicalize(uint modifiers, string key)
    {
        var builder = new StringBuilder();
        foreach (var name in ModifierOrder)
            if ((modifiers & ModifierAliases[name]) != 0) builder.Append(name).Append('+');
        builder.Append(key);
        return builder.ToString();
    }

    private static bool TryMapKey(string key, out uint vk)
    {
        if (KeyNames.TryGetValue(key, out vk)) return true;

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
            switch (key[0])
            {
                case '`': vk = 0xC0; return true;
                case ';': vk = 0xBA; return true;
                case '\'': vk = 0xDE; return true;
                case ',': vk = 0xBC; return true;
                case '.': vk = 0xBE; return true;
                case '/': vk = 0xBF; return true;
                case '\\': vk = 0xDC; return true;
                case '[': vk = 0xDB; return true;
                case ']': vk = 0xDD; return true;
                case '-': vk = 0xBD; return true;
            }
            vk = 0;
            return false;
        }

        if (key.StartsWith("f", StringComparison.Ordinal) && key.Length is >= 2 and <= 3)
        {
            if (int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                && number is >= 1 and <= 24)
            {
                vk = 0x70 + (uint)(number - 1);
                return true;
            }
        }

        vk = 0;
        return false;
    }

    private static string KeyName(uint vk)
    {
        foreach (var pair in KeyNames)
            if (pair.Value == vk) return pair.Key;
        if (vk is >= 'A' and <= 'Z') return ((char)vk).ToString().ToLowerInvariant();
        if (vk is >= '0' and <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "f" + (vk - 0x70 + 1);
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }
}
