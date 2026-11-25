using Galah2.Models;

namespace Galah2.Persistence;

/// <summary>
/// settings.json access. Own fallback/quarantine domain, so catalog damage never resets preferences
/// and vice versa (spec §5.1, TL-D6). Writes happen only on committed user actions (INV-1).
/// </summary>
public sealed class SettingsStore
{
    private readonly JsonFileStore<SettingsDocument> _store;
    private string? _lastSaveError;

    /// <summary>Raised when a preference commit failed (INV-9 banner).</summary>
    public event Action<string>? SaveFailed;

    /// <summary>Current preferences snapshot; already normalized to sane values after load.</summary>
    public SettingsDocument Current { get; private set; } = new();

    /// <summary>Creates the store over the given data paths.</summary>
    public SettingsStore(AppDataPaths paths)
        => _store = new JsonFileStore<SettingsDocument>(paths.SettingsFile, SettingsDocument.CurrentSchema,
            static () => new SettingsDocument(), "settings");

    /// <summary>Store used by this domain (tests, refusal reporting).</summary>
    public JsonFileStore<SettingsDocument> FileStore => _store;

    /// <summary>Message of the last failed commit, null when clean.</summary>
    public string? LastSaveError => _lastSaveError;

    /// <summary>Load settings, falling back to defaults on damage without touching other domains.</summary>
    public LoadOutcome<SettingsDocument> Load()
    {
        var outcome = _store.Load();
        // The returned document is the sanitized one: callers must not be able to observe an
        // invalid value through the return path while Current looks clean.
        Current = outcome.Data.Sanitized();
        _lastSaveError = null;
        return outcome with { Data = Current };
    }

    /// <summary>
    /// Apply a preference change and persist it immediately (≤100 ms, spec §1 write model).
    /// </summary>
    /// <param name="mutate">In-place edit of the settings document.</param>
    /// <returns>True when the new value reached disk (or was already identical).</returns>
    public bool Update(Action<SettingsDocument> mutate)
    {
        mutate(Current);
        return Commit();
    }

    /// <summary>
    /// Normalize every field, then persist. This is the single write gate: the settings UI mutates
    /// <see cref="Current"/> in place, so Commit (not Update) guarantees that no out-of-range or
    /// hand-edited value reaches disk. Replacing the snapshot with Sanitized() covers all fields;
    /// the previous implementation copied back only five of them and silently dropped the rest.
    /// </summary>
    public bool Commit()
    {
        Current = Current.Sanitized();
        return Save();
    }

    /// <summary>
    /// Replace the whole snapshot from a backup import. Copying field by field silently lost
    /// every preference that was added after the import code was written, so replace the
    /// document as a whole and let Sanitized() normalize it.
    /// </summary>
    public bool Replace(SettingsDocument snapshot)
    {
        Current = snapshot.Sanitized();
        // A bundle can carry a newer settings schema. Persisting it would make the next boot
        // refuse the whole domain (SealedForUpgrade), so always write the schema of this build.
        Current.Schema = SettingsDocument.CurrentSchema;
        return Save();
    }

    /// <summary>Persist the current snapshot; failure is reported through <see cref="SaveFailed"/> (INV-9).</summary>
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

    /// <summary>Re-attempt a failed preference commit.</summary>
    public bool RetryPendingSave() => _lastSaveError is null || Save();
}
