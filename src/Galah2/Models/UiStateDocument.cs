using System.Text.Json.Serialization;

namespace Galah2.Models;

/// <summary>
/// Document model of ui-state.json: window geometry, visibility and the last active category
/// (design §3.1 — the high-frequency, cheap state is deliberately isolated from the authoritative
/// content library). Schema 2 added <c>activeTabId</c> and the maximised flag; schema-1 files
/// still load, the new fields simply default.
/// </summary>
public sealed class UiStateDocument : Persistence.ISchemaBearing
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentSchema = 2;

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("window")]
    public WindowState Window { get; set; } = new();

    /// <summary>Id of the category that was selected last (null when never selected).</summary>
    [JsonPropertyName("activeTabId")]
    public string? ActiveTabId { get; set; }

    /// <summary>Deep copy for snapshot comparisons.</summary>
    public UiStateDocument Clone() => new()
    {
        Schema = Schema,
        Window = Window.Clone(),
        ActiveTabId = ActiveTabId,
    };
}

/// <summary>
/// Window rectangle in DIPs plus the restored visibility flag (BS-2/BS-6 mirror, design §1.1 S3).
/// Position is nullable on purpose: "never positioned yet" has to serialise as JSON <c>null</c>.
/// The old NaN sentinel aborted boot, because System.Text.Json refuses to write non-finite doubles.
/// </summary>
public sealed class WindowState
{
    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; } = 720;

    [JsonPropertyName("height")]
    public double Height { get; set; } = 460;

    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; set; }

    /// <summary>True when the panel was maximised when it was closed (schema 2).</summary>
    [JsonPropertyName("maximized")]
    public bool Maximized { get; set; }

    /// <summary>True when a usable saved position exists; otherwise the caller picks a default spot.</summary>
    [JsonIgnore]
    public bool HasPosition => X.HasValue && Y.HasValue && double.IsFinite(X.Value) && double.IsFinite(Y.Value);

    /// <summary>Drop a non-finite (hand-edited) coordinate back to "unpositioned".</summary>
    public void SanitizePosition()
    {
        if (X is { } x && !double.IsFinite(x)) X = null;
        if (Y is { } y && !double.IsFinite(y)) Y = null;
    }

    /// <summary>Deep copy.</summary>
    public WindowState Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        IsVisible = IsVisible,
        Maximized = Maximized,
    };
}
