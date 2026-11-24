namespace Galah2.Models;

/// <summary>
/// RandPass generation parameters (defaults: everything checked, length 8, count 10, custom
/// chars "@#$", format off. The 2026-11-24 follow-up changed length/count from the original
/// 14/14 screenshot state — see spec 2026-11-24-randpass §7.)
/// </summary>
public sealed class PasswordOptions
{
    public const int MinLength = 4;
    public const int MaxLength = 64;
    public const int MinCount = 1;
    public const int MaxCount = 100;
    public const string DefaultCustomChars = "@#$";

    /// <summary>Password length (content characters; format dashes are extra).</summary>
    public int Length { get; set; } = 8;

    /// <summary>How many passwords one 生成密码 click produces.</summary>
    public int Count { get; set; } = 10;

    /// <summary>a–z in the pool.</summary>
    public bool Lower { get; set; } = true;

    /// <summary>A–Z in the pool.</summary>
    public bool Upper { get; set; } = true;

    /// <summary>0–9 in the pool.</summary>
    public bool Digits { get; set; } = true;

    /// <summary>
    /// Two-digit numbers (11/22/…/00): when a digit slot is drawn, a 25% roll emits the doubled
    /// digit taking TWO slots (content length stays Length). User-confirmed semantics.
    /// </summary>
    public bool DoubleDigits { get; set; } = true;

    /// <summary>Strip the look-alike set {1, l, I, 3, L} from the pool.</summary>
    public bool NoConfusables { get; set; } = true;

    /// <summary>Extra characters mixed into the pool (printable ASCII only).</summary>
    public string CustomChars { get; set; } = DefaultCustomChars;

    /// <summary>Whether CustomChars participates at all.</summary>
    public bool CustomEnabled { get; set; } = true;

    /// <summary>When true, dashes are inserted per Format. Default off (screenshot state).</summary>
    public bool UseFormat { get; set; }

    /// <summary>Format pattern, one of <see cref="PasswordFormats.All"/>.</summary>
    public string Format { get; set; } = PasswordFormats.FourStarFour;
}

/// <summary>
/// The two format presets. Syntax (user-stated): numbers = fixed segment sizes, `*` = flexible
/// segments, dashes join the segments, and the CONTENT total is always the password length.
/// Flexible segments share (Length − Σfixed) evenly, remainder to the first `*`.
/// The third RandPass Lite preset ([8-4-4-4-12]) was explicitly dropped by the user.
/// </summary>
public static class PasswordFormats
{
    public const string FourStarFour = "4-*-4";
    public const string TwoStarFourStarTwo = "2-*-4-*-2";
    public static readonly string[] All = [FourStarFour, TwoStarFourStarTwo];
}
