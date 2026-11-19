using System.Globalization;

namespace Galah2.Services;

/// <summary>
/// The one name-ordering rule of the product. Chinese must sort by pinyin (甲 before 乙) on
/// EVERY machine, including a CI runner or an English Windows without the zh sorting tables:
/// CurrentCulture silently degrades to ordinal there, which scrambles Chinese names. We pin
/// zh-CN and fall back to ordinal only if that culture is genuinely unavailable.
/// </summary>
public static class NameOrder
{
    private static readonly StringComparer Comparer = Create();

    private static StringComparer Create()
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo("zh-CN");
            return StringComparer.Create(culture, ignoreCase: true);
        }
        catch (CultureNotFoundException)
        {
            return StringComparer.OrdinalIgnoreCase;
        }
    }

    /// <summary>Comparer used by every name sort in the product.</summary>
    public static StringComparer Sort => Comparer;
}
