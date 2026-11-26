using Galah2.Models;

namespace Galah2.Persistence;

/// <summary>
/// ui-state.json access: window geometry + visibility (design §3.1 — cheap, high-frequency state is
/// isolated from the content library so write amplification cannot touch items). Visibility changes are
/// committed immediately (they are user-commit actions per spec §1); geometry arrives pre-debounced
/// from the panel (500 ms) and is only written when the values actually changed.
/// </summary>
public sealed class UiStateStore
{
    private readonly JsonFileStore<UiStateDocument> _store;
    private string? _lastSaveError;

    /// <summary>Raised when a commit failed (INV-9).</summary>
    public event Action<string>? SaveFailed;

    /// <summary>Current ui-state snapshot.</summary>
    public UiStateDocument Current { get; private set; } = new();

    /// <summary>Creates the store over the given data paths.</summary>
    public UiStateStore(AppDataPaths paths)
        => _store = new JsonFileStore<UiStateDocument>(paths.UiStateFile, UiStateDocument.CurrentSchema,
            static () => new UiStateDocument(), "ui-state");

    /// <summary>Store used by this domain (tests, refusal reporting).</summary>
    public JsonFileStore<UiStateDocument> FileStore => _store;

    /// <summary>Message of the last failed commit, null when clean.</summary>
    public string? LastSaveError => _lastSaveError;

    /// <summary>
    /// Load geometry/visibility. Damage in this domain only costs the window position (spec §5.1-4);
    /// the default restored state is "resident, no window".
    /// </summary>
    public LoadOutcome<UiStateDocument> Load()
    {
        var outcome = _store.Load();
        var doc = outcome.Data;
        // An explicit JSON null for a nested object deserializes without error: System.Text.Json
        // overwrites the property initializer with null, and the NRE would surface after Load()
        // has already left the quarantine chain. Normalize here, before touching any member.
        if (doc.Window is null) doc.Window = new WindowState();
        if (doc.RandPassWindow is null) doc.RandPassWindow = new WindowState { Width = 580, Height = 540 };
        NormalizeWindow(doc.Window, defaultWidth: 720, defaultHeight: 460);
        NormalizeWindow(doc.RandPassWindow, defaultWidth: 580, defaultHeight: 540);
        Current = doc;
        _lastSaveError = null;
        return outcome;
    }

    private static void NormalizeWindow(WindowState state, double defaultWidth, double defaultHeight)
    {
        state.SanitizePosition();   // a hand-edited NaN/Infinity coordinate is treated as "never positioned"
        if (!double.IsFinite(state.Width) || state.Width < 200) state.Width = defaultWidth;
        if (!double.IsFinite(state.Height) || state.Height < 160) state.Height = defaultHeight;
    }

    /// <summary>Commit a visibility change immediately (spec §1: 可见性变化即写).</summary>
    public bool SetVisibility(bool visible)
    {
        if (Current.Window.IsVisible == visible) return false;
        Current.Window.IsVisible = visible;
        return Save();
    }

    /// <summary>
    /// Remember the selected category (schema 2). Selecting a category is a user action, so the id
    /// is committed immediately; an unchanged id costs no write.
    /// </summary>
    /// <param name="tabId">Category id, or null when nothing is selected.</param>
    /// <returns>True when the file was written.</returns>
    public bool SetActiveTab(string? tabId)
    {
        if (string.Equals(Current.ActiveTabId, tabId, StringComparison.Ordinal)) return false;
        Current.ActiveTabId = tabId;
        return Save();
    }

    /// <summary>Commit the maximised flag (schema 2).</summary>
    /// <param name="maximized">True when the panel is maximised.</param>
    /// <returns>True when the file was written.</returns>
    public bool SetMaximized(bool maximized)
    {
        if (Current.Window.Maximized == maximized) return false;
        Current.Window.Maximized = maximized;
        return Save();
    }

    /// <summary>
    /// Store new geometry in the snapshot (called by the debounced writer before <see cref="CommitGeometry"/>).
    /// Non-finite values are ignored: they would make the document unserialisable (boot-abort bug, see notes D-6).
    /// </summary>
    public void UpdateGeometry(double x, double y, double width, double height)
    {
        if (double.IsFinite(x) && double.IsFinite(y))
        {
            Current.Window.X = x;
            Current.Window.Y = y;
        }
        if (double.IsFinite(width) && width > 0) Current.Window.Width = width;
        if (double.IsFinite(height) && height > 0) Current.Window.Height = height;
    }

    /// <summary>Persist the current geometry snapshot; zero write when values are unchanged.</summary>
    public bool CommitGeometry()
    {
        if (!Current.Window.HasPosition) return false;
        return Save();
    }

    /// <summary>Persist the snapshot; failures are surfaced, never swallowed (INV-9).</summary>
    public bool Save()
    {
        try
        {
            var wrote = _store.Save(Current);
            _lastSaveError = null;
            return wrote;
        }
        catch (PersistenceException ex)
        {
            _lastSaveError = ex.UserMessage;
            SaveFailed?.Invoke(ex.UserMessage);
            return false;
        }
    }

    /// <summary>Re-attempt a failed ui-state commit.</summary>
    public bool RetryPendingSave() => _lastSaveError is null || Save();
}
