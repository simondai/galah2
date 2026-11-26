using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galah2.Persistence;

/// <summary>Anything the JSON layer needs from a document: a schema field (design §3.2/§3.3).</summary>
public interface ISchemaBearing
{
    int Schema { get; set; }
}

/// <summary>Outcome of a load, so the boot sequence can report exactly what happened (spec §5.1).</summary>
public enum LoadStatus
{
    /// <summary>Main file parsed fine.</summary>
    Loaded,

    /// <summary>No file existed; defaults written once as first-run bootstrap.</summary>
    FirstRunInitialized,

    /// <summary>Main file was unreadable and got quarantined; data came from a rolling backup.</summary>
    RecoveredFromBackup,

    /// <summary>Main and all backups unreadable; started with defaults after quarantining the bad main file.</summary>
    StartedEmpty,

    /// <summary>File carries a newer schema than this build understands — refused, file untouched, writes sealed.</summary>
    RefusedNewerSchema,
}

/// <summary>Result of <see cref="JsonFileStore{T}.Load"/>.</summary>
public sealed record LoadOutcome<T>(T Data, LoadStatus Status, string? Detail) where T : class;

/// <summary>
/// Raised when a write could not be completed. The in-memory model is kept so the user can retry
/// (INV-9 — a failed save must be visible and retryable, never silently dropped).
/// </summary>
public sealed class PersistenceException : Exception
{
    /// <summary>Human-readable (Chinese) reason shown in the global banner.</summary>
    public string UserMessage { get; }

    public PersistenceException(string userMessage, Exception? inner = null)
        : base(userMessage, inner) => UserMessage = userMessage;
}

/// <summary>
/// One JSON document on disk with the persistence contract of the item-loop spec:
/// serialize → temp file → Flush(true) → rolling backups → atomic install; a corrupt main file is
/// renamed to <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;.json</c> (never overwritten) and the load falls
/// back main → .bak.1 → .bak.2 → defaults. Each domain (catalog / settings / ui-state) owns a
/// separate instance, so damage in one domain can never reset another (spec §5.1-3/4, TL-D5/TL-D6).
/// </summary>
public sealed class JsonFileStore<T> where T : class, ISchemaBearing, new()
{
    /// <summary>Rolling backups kept per domain (spec §1: 旧主本滚 .bak.1, 留 2).</summary>
    public const int BackupCount = 2;

    // AOT-safe options: the JsonTypeInfo is resolved through the source-generated
    // JsonContext rather than reflection. JsonSerializerOptions itself cannot carry metadata;
    // it is kept only for the encoder settings shared by every document.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        TypeInfoResolver = JsonContext.Default,
    };

    private readonly string _mainPath;
    private readonly Func<T> _defaultsFactory;
    private readonly object _writeLock = new();
    private string? _canonicalText;
    private int _tempCounter;

    /// <summary>Domain name used in messages and quarantine file names ("catalog", "settings", "ui-state").</summary>
    public string Domain { get; }

    /// <summary>Highest schema version this build can read and write.</summary>
    public int CurrentSchema { get; }

    /// <summary>True when the store refused to load a newer file — writes are then blocked to protect it.</summary>
    public bool SealedForUpgrade { get; private set; }

    /// <summary>Absolute path of the main file.</summary>
    public string FilePath => _mainPath;

    /// <summary>Creates a store for one document file.</summary>
    public JsonFileStore(string mainPath, int currentSchema, Func<T> defaultsFactory, string domain)
    {
        _mainPath = mainPath;
        CurrentSchema = currentSchema;
        _defaultsFactory = defaultsFactory;
        Domain = domain;
    }

    /// <summary>Path of rolling backup <paramref name="index"/> (1 or 2).</summary>
    public string BackupPath(int index) => _mainPath + ".bak." + index;

    /// <summary>
    /// Read the document following the fallback chain. Never writes the authoritative file; the only
    /// write it may perform is the first-run bootstrap of a missing file (spec §5.1, INV-1).
    /// </summary>
    public LoadOutcome<T> Load()
    {
        SealedForUpgrade = false;

        string? text = ReadIfPresent(_mainPath);
        if (text is null)
        {
            if (!File.Exists(_mainPath))
            {
                // First run: materialize defaults once so the data directory has the documented files.
                var fresh = _defaultsFactory();
                var freshText = Serialize(fresh);
                _canonicalText = freshText;
                try { WriteAtomic(freshText); } catch (PersistenceException) { /* still start with defaults */ }
                return new LoadOutcome<T>(fresh, LoadStatus.FirstRunInitialized, null);
            }
            return QuarantineAndRecover("主文件无法读取");
        }

        T? doc;
        try
        {
            doc = JsonSerializer.Deserialize<T>(text, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return QuarantineAndRecover("主文件 JSON 损坏: " + ex.Message);
        }
        catch (NotSupportedException ex)
        {
            // NativeAOT: a type missing from the source-generated context surfaces here instead
            // of as JsonException. It must still reach the quarantine/backup chain.
            return QuarantineAndRecover("主文件包含不支持的类型: " + ex.Message);
        }
        catch (ArgumentException ex)
        {
            return QuarantineAndRecover("主文件内容无效: " + ex.Message);
        }

        if (doc is null) return QuarantineAndRecover("主文件内容为空");

        if (doc.Schema > CurrentSchema)
        {
            // Newer data than this build: refuse, touch nothing, block writes (spec §5.1-3, TL-D8).
            SealedForUpgrade = true;
            _canonicalText = null;
            return new LoadOutcome<T>(_defaultsFactory(), LoadStatus.RefusedNewerSchema,
                $"{Domain}.json 的 schema 版本为 {doc.Schema}，高于当前程序支持的 {CurrentSchema}");
        }

        doc.Schema = CurrentSchema;
        try
        {
            _canonicalText = Serialize(doc);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            // Guard the AOT case where a persisted type is missing from JsonContext, plus any
            // non-finite number that cannot be serialized. Field-level nulls DO serialize fine;
            // those are normalized by each store (UiStateStore / CatalogStore), not here.
            return QuarantineAndRecover("主文件内容无法规范化: " + ex.Message);
        }
        return new LoadOutcome<T>(doc, LoadStatus.Loaded, null);
    }

    private LoadOutcome<T> QuarantineAndRecover(string reason)
    {
        var quarantined = TryQuarantineMain();
        for (var i = 1; i <= BackupCount; i++)
        {
            var bak = BackupPath(i);
            var text = ReadIfPresent(bak);
            if (text is null) continue;
            T? doc;
            try { doc = JsonSerializer.Deserialize<T>(text, SerializerOptions); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { continue; }
            if (doc is null || doc.Schema > CurrentSchema) continue;

            doc.Schema = CurrentSchema;
            try { _canonicalText = Serialize(doc); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { continue; }
            var detail = reason + (quarantined is null ? "" : "；坏件已保全为 " + Path.GetFileName(quarantined));
            return new LoadOutcome<T>(doc, LoadStatus.RecoveredFromBackup, detail);
        }

        var empty = _defaultsFactory();
        try { _canonicalText = Serialize(empty); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            _canonicalText = null;   // no canonical snapshot yet; the next Save() will write one
        }
        var detailText = reason + (quarantined is null ? "" : "；坏件已保全为 " + Path.GetFileName(quarantined)) + "；备份不可用，已从空库开始";
        return new LoadOutcome<T>(empty, LoadStatus.StartedEmpty, detailText);
    }

    /// <summary>
    /// Rename an unreadable main file to <c>&lt;domain&gt;.corrupt-&lt;timestamp&gt;.json</c>. Existing
    /// quarantine files are never overwritten (INV-6); returns null when the file could not be moved.
    /// </summary>
    public string? TryQuarantineMain()
    {
        try
        {
            if (!File.Exists(_mainPath)) return null;
            var stem = Path.GetFileNameWithoutExtension(_mainPath);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var target = Path.Combine(Path.GetDirectoryName(_mainPath)!, $"{stem}.corrupt-{stamp}.json");
            var n = 1;
            while (File.Exists(target))
                target = Path.Combine(Path.GetDirectoryName(_mainPath)!, $"{stem}.corrupt-{stamp}-{n++}.json");
            File.Move(_mainPath, target);
            return target;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Persist <paramref name="data"/> if and only if its serialized form differs from the last known
    /// content — this is what keeps a no-op restart byte-identical (INV-1/INV-5). Throws
    /// <see cref="PersistenceException"/> on write failure after retries; the caller keeps the model.
    /// </summary>
    public bool Save(T data)
    {
        var text = Serialize(data);
        lock (_writeLock)
        {
            if (SealedForUpgrade)
                throw new PersistenceException($"{Domain}.json 版本高于当前程序，已禁止写入以保护数据");
            if (_canonicalText == text) return false;
            WriteAtomic(text);
            _canonicalText = text;
            return true;
        }
    }

    /// <summary>True when the in-memory content equals what is on disk (no pending commit).</summary>
    public bool IsClean(T data) => Serialize(data) == _canonicalText;

    /// <summary>Serialize with the documented options (used by stores and by tests).</summary>
    public static string Serialize(T data) => JsonSerializer.Serialize(data, SerializerOptions);

    private static string? ReadIfPresent(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return null;
            // Tolerate a stray BOM written by external editors.
            return (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                : Encoding.UTF8.GetString(bytes);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void WriteAtomic(string text)
    {
        var dir = Path.GetDirectoryName(_mainPath)!;
        Exception? last = null;
        var backupsRolled = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var temp = Path.Combine(dir, Path.GetFileName(_mainPath) + $".tmp-{Interlocked.Increment(ref _tempCounter):x4}");
                try
                {
                    using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var payload = Encoding.UTF8.GetBytes(text);
                        fs.Write(payload, 0, payload.Length);
                        fs.Flush(true);           // fsync before the rename (spec §1 write model)
                    }
                    // 每次保存只滚动一次备份，防止重试时反复拷贝把历史 .bak.2 冲掉。
                    if (!backupsRolled)
                    {
                        RollBackups();
                        backupsRolled = true;
                    }
                    Install(temp, _mainPath);
                }
                finally
                {
                    TryDeleteTemp(temp);
                }
                return;
            }
            catch (IOException ex) { last = ex; Thread.Sleep(15 * (attempt + 1)); }
            catch (UnauthorizedAccessException ex) { last = ex; Thread.Sleep(15 * (attempt + 1)); }
        }
        throw new PersistenceException($"无法保存 {Domain}.json（{(last is IOException ? "磁盘忙或权限不足" : "磁盘写入失败")}）", last);
    }

    /// <summary>Roll main → .bak.1 → .bak.2 before the new content is installed.</summary>
    private void RollBackups()
    {
        for (var i = BackupCount; i >= 1; i--)
        {
            var source = i == 1 ? _mainPath : BackupPath(i - 1);
            var destination = BackupPath(i);
            try
            {
                if (File.Exists(source)) File.Copy(source, destination, overwrite: true);
            }
            catch (IOException) { /* a missing backup only shortens the fallback chain */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Install(string temp, string main)
    {
        if (File.Exists(main))
        {
            try
            {
                File.Replace(temp, main, null);   // same-volume atomic swap
                return;
            }
            catch (IOException) { /* some file systems reject Replace — fall back to a rename */ }
            catch (PlatformNotSupportedException) { }
        }
        File.Move(temp, main, overwrite: true);
    }

    private static void TryDeleteTemp(string temp)
    {
        try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
