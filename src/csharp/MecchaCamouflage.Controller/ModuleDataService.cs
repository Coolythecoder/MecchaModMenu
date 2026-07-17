using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

public sealed record ModuleDataGetResult(
    string Key,
    bool Found,
    JsonElement? Value,
    int SizeBytes,
    int UsedBytes,
    int QuotaBytes);

public sealed record ModuleDataSetResult(
    string Key,
    bool Stored,
    int SizeBytes,
    int UsedBytes,
    int QuotaBytes);

public sealed record ModuleDataDeleteResult(
    string Key,
    bool Deleted,
    int UsedBytes,
    int QuotaBytes);

public sealed record ModuleDataListResult(
    IReadOnlyList<string> Keys,
    int UsedBytes,
    int QuotaBytes);

public sealed record ModuleDataCommandResult(
    bool Success,
    string Code,
    string Message,
    object? Data);

public sealed class ModuleDataException : Exception
{
    public ModuleDataException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Host-managed JSON data for third-party modules. Persistent entries are stored
/// below a host-owned root; volatile entries live only for this service instance.
/// Neither namespace accepts paths, pointers, or native process addresses.
/// </summary>
public sealed class ModuleDataService : IDisposable
{
    private const int StorageSchemaVersion = 1;
    private const int MaxJsonDepth = 32;
    private const int MaxJsonNodes = 8192;
    private const int MaxRecordOverheadBytes = 2048;
    private const int MaxTemporaryFiles = 32;
    private const string EntryExtension = ".entry";
    private const string LockFileName = "storage-v1.lock";
    private static readonly TimeSpan PersistentLockTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // ValidateJson is authoritative. The serializer needs one extra level
        // for its root-depth accounting at the documented boundary.
        MaxDepth = MaxJsonDepth + 1,
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions StorageRecordSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A persistent record wraps the already-validated value in one object.
        MaxDepth = MaxJsonDepth + 3,
        WriteIndented = false
    };

    private readonly string persistentRoot;
    private readonly object memoryGate = new();
    private readonly object persistentGatesGate = new();
    private readonly Dictionary<string, object> persistentGates = new(ModuleSdkV1.IdComparer);
    private readonly Dictionary<string, Dictionary<string, byte[]>> memory =
        new(ModuleSdkV1.IdComparer);
    private int memoryUsedBytes;
    private volatile bool disposed;

    public ModuleDataService(string persistentRoot)
    {
        if (string.IsNullOrWhiteSpace(persistentRoot))
            throw new ArgumentException("A persistent module-data root is required.", nameof(persistentRoot));
        this.persistentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(persistentRoot));
    }

    public ModuleDataGetResult StorageGet(string moduleId, string key)
    {
        ValidateIdentityAndKey(moduleId, key);
        lock (PersistentGate(moduleId))
        {
            ThrowIfDisposed();
            using var lease = AcquirePersistentLock(moduleId, createNamespace: false);
            if (ReferenceEquals(lease, EmptyLease.Instance))
            {
                return new ModuleDataGetResult(
                    key, false, null, 0, 0, ModuleSdkV1.MaxDataModuleBytes);
            }
            var state = ReadPersistentState(moduleId);
            return state.Values.TryGetValue(key, out var value)
                ? new ModuleDataGetResult(
                    key,
                    true,
                    ParseOwnedValue(value),
                    value.Length,
                    state.UsedBytes,
                    ModuleSdkV1.MaxDataModuleBytes)
                : new ModuleDataGetResult(
                    key,
                    false,
                    null,
                    0,
                    state.UsedBytes,
                    ModuleSdkV1.MaxDataModuleBytes);
        }
    }

    public ModuleDataSetResult StorageSet(
        string moduleId,
        string key,
        JsonElement value,
        Action<Action>? commit = null)
    {
        ValidateIdentityAndKey(moduleId, key);
        var valueBytes = CanonicalizeValue(value);
        lock (PersistentGate(moduleId))
        {
            ThrowIfDisposed();
            using var lease = AcquirePersistentLock(moduleId, createNamespace: true);
            try
            {
                EnsurePersistentRoot(create: true);
                var state = ReadPersistentState(moduleId);
                var exists = state.Values.TryGetValue(key, out var previous);
                if (!exists && state.Values.Count >= ModuleSdkV1.MaxDataEntries)
                    throw new ModuleDataException("key_limit", "This module data namespace has reached its key limit.");
                var projected = checked(state.UsedBytes - (previous?.Length ?? 0) + valueBytes.Length);
                if (projected > ModuleSdkV1.MaxDataModuleBytes)
                    throw new ModuleDataException("quota_exceeded", "This module data namespace has reached its quota.");

                var moduleDirectory = ModuleDirectory(moduleId);
                EnsureModuleDirectory(moduleDirectory, create: true);
                var targetPath = EntryPath(moduleDirectory, key);
                EnsureDirectChild(moduleDirectory, targetPath);
                if (File.Exists(targetPath) && IsReparsePoint(new FileInfo(targetPath)))
                    throw CorruptStore();

                var recordBytes = BuildStorageRecord(key, valueBytes);
                var temporaryPath = Path.Combine(moduleDirectory, $".tmp-{Guid.NewGuid():N}.tmp");
                EnsureDirectChild(moduleDirectory, temporaryPath);
                try
                {
                    using (var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        64 * 1024,
                        FileOptions.WriteThrough))
                    {
                        stream.Write(recordBytes);
                        stream.Flush(flushToDisk: true);
                    }
                    EnsurePersistentRoot(create: false);
                    EnsureModuleDirectory(moduleDirectory, create: false);
                    Commit(commit, () => File.Move(temporaryPath, targetPath, overwrite: true));
                }
                finally
                {
                    TryDeleteTemporaryFile(temporaryPath, moduleDirectory);
                }
                return new ModuleDataSetResult(
                    key,
                    true,
                    valueBytes.Length,
                    projected,
                    ModuleSdkV1.MaxDataModuleBytes);
            }
            catch (ModuleDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw StorageUnavailable(ex);
            }
        }
    }

    public ModuleDataDeleteResult StorageDelete(
        string moduleId,
        string key,
        Action<Action>? commit = null)
    {
        ValidateIdentityAndKey(moduleId, key);
        lock (PersistentGate(moduleId))
        {
            ThrowIfDisposed();
            using var lease = AcquirePersistentLock(moduleId, createNamespace: false);
            if (ReferenceEquals(lease, EmptyLease.Instance))
            {
                return new ModuleDataDeleteResult(
                    key, false, 0, ModuleSdkV1.MaxDataModuleBytes);
            }
            try
            {
                var state = ReadPersistentState(moduleId);
                if (!state.Values.TryGetValue(key, out var value))
                {
                    return new ModuleDataDeleteResult(
                        key,
                        false,
                        state.UsedBytes,
                        ModuleSdkV1.MaxDataModuleBytes);
                }
                var moduleDirectory = ModuleDirectory(moduleId);
                var targetPath = EntryPath(moduleDirectory, key);
                EnsureDirectChild(moduleDirectory, targetPath);
                if (!File.Exists(targetPath) || IsReparsePoint(new FileInfo(targetPath)))
                    throw CorruptStore();
                Commit(commit, () => File.Delete(targetPath));
                return new ModuleDataDeleteResult(
                    key,
                    true,
                    state.UsedBytes - value.Length,
                    ModuleSdkV1.MaxDataModuleBytes);
            }
            catch (ModuleDataException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                throw StorageUnavailable(ex);
            }
        }
    }

    public ModuleDataListResult StorageList(string moduleId)
    {
        ValidateIdentity(moduleId);
        lock (PersistentGate(moduleId))
        {
            ThrowIfDisposed();
            using var lease = AcquirePersistentLock(moduleId, createNamespace: false);
            if (ReferenceEquals(lease, EmptyLease.Instance))
                return new ModuleDataListResult([], 0, ModuleSdkV1.MaxDataModuleBytes);
            var state = ReadPersistentState(moduleId);
            return new ModuleDataListResult(
                state.Values.Keys.Order(StringComparer.Ordinal).ToArray(),
                state.UsedBytes,
                ModuleSdkV1.MaxDataModuleBytes);
        }
    }

    public ModuleDataGetResult MemoryGet(string moduleId, string key)
    {
        ValidateIdentityAndKey(moduleId, key);
        lock (memoryGate)
        {
            ThrowIfDisposed();
            var bucket = GetMemoryBucket(moduleId, create: false);
            var used = BucketBytes(bucket);
            return bucket is not null && bucket.TryGetValue(key, out var value)
                ? new ModuleDataGetResult(
                    key,
                    true,
                    ParseOwnedValue(value),
                    value.Length,
                    used,
                    ModuleSdkV1.MaxDataModuleBytes)
                : new ModuleDataGetResult(
                    key,
                    false,
                    null,
                    0,
                    used,
                    ModuleSdkV1.MaxDataModuleBytes);
        }
    }

    public ModuleDataSetResult MemorySet(
        string moduleId,
        string key,
        JsonElement value,
        Action<Action>? commit = null)
    {
        ValidateIdentityAndKey(moduleId, key);
        var valueBytes = CanonicalizeValue(value);
        lock (memoryGate)
        {
            ThrowIfDisposed();
            var bucket = GetMemoryBucket(moduleId, create: true)!;
            var exists = bucket.TryGetValue(key, out var previous);
            if (!exists && bucket.Count >= ModuleSdkV1.MaxDataEntries)
                throw new ModuleDataException("key_limit", "This module data namespace has reached its key limit.");
            var used = BucketBytes(bucket);
            var projected = checked(used - (previous?.Length ?? 0) + valueBytes.Length);
            if (projected > ModuleSdkV1.MaxDataModuleBytes)
                throw new ModuleDataException("quota_exceeded", "This module data namespace has reached its quota.");
            var projectedGlobal = checked(memoryUsedBytes - (previous?.Length ?? 0) + valueBytes.Length);
            if (projectedGlobal > ModuleSdkV1.MaxMemoryTotalBytes)
                throw new ModuleDataException("quota_exceeded", "The app-session module memory quota has been reached.");

            Commit(commit, () =>
            {
                if (previous is not null)
                    CryptographicOperations.ZeroMemory(previous);
                bucket[key] = valueBytes.ToArray();
                memoryUsedBytes = projectedGlobal;
            });
            return new ModuleDataSetResult(
                key,
                true,
                valueBytes.Length,
                projected,
                ModuleSdkV1.MaxDataModuleBytes);
        }
    }

    public ModuleDataDeleteResult MemoryDelete(
        string moduleId,
        string key,
        Action<Action>? commit = null)
    {
        ValidateIdentityAndKey(moduleId, key);
        lock (memoryGate)
        {
            ThrowIfDisposed();
            var bucket = GetMemoryBucket(moduleId, create: false);
            var used = BucketBytes(bucket);
            if (bucket is null || !bucket.TryGetValue(key, out var previous))
            {
                return new ModuleDataDeleteResult(
                    key,
                    false,
                    used,
                    ModuleSdkV1.MaxDataModuleBytes);
            }
            Commit(commit, () =>
            {
                bucket.Remove(key);
                memoryUsedBytes -= previous.Length;
                CryptographicOperations.ZeroMemory(previous);
            });
            return new ModuleDataDeleteResult(
                key,
                true,
                used - previous.Length,
                ModuleSdkV1.MaxDataModuleBytes);
        }
    }

    public ModuleDataListResult MemoryList(string moduleId)
    {
        ValidateIdentity(moduleId);
        lock (memoryGate)
        {
            ThrowIfDisposed();
            var bucket = GetMemoryBucket(moduleId, create: false);
            return new ModuleDataListResult(
                bucket?.Keys.Order(StringComparer.Ordinal).ToArray() ?? [],
                BucketBytes(bucket),
                ModuleSdkV1.MaxDataModuleBytes);
        }
    }

    public void Dispose()
    {
        lock (memoryGate)
        {
            if (disposed)
                return;
            foreach (var value in memory.Values.SelectMany(bucket => bucket.Values))
                CryptographicOperations.ZeroMemory(value);
            memory.Clear();
            memoryUsedBytes = 0;
            disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private PersistentState ReadPersistentState(string moduleId)
    {
        try
        {
            if (!Directory.Exists(persistentRoot))
                return new PersistentState(new Dictionary<string, byte[]>(StringComparer.Ordinal), 0);
            EnsurePersistentRoot(create: false);
            var moduleDirectory = ModuleDirectory(moduleId);
            if (!Directory.Exists(moduleDirectory))
                return new PersistentState(new Dictionary<string, byte[]>(StringComparer.Ordinal), 0);
            EnsureModuleDirectory(moduleDirectory, create: false);

            var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var usedBytes = 0;
            var maximumDirectoryEntries = ModuleSdkV1.MaxDataEntries + MaxTemporaryFiles + 1;
            var paths = Directory.EnumerateFileSystemEntries(moduleDirectory)
                .Take(maximumDirectoryEntries + 1)
                .ToArray();
            if (paths.Length > maximumDirectoryEntries)
                throw CorruptStore();
            var temporaryFiles = 0;
            foreach (var path in paths)
            {
                EnsureDirectChild(moduleDirectory, path);
                if (Directory.Exists(path))
                    throw CorruptStore();
                var file = new FileInfo(path);
                if (IsReparsePoint(file))
                    throw CorruptStore();
                if (string.Equals(file.Name, LockFileName, StringComparison.Ordinal))
                    continue;
                if (IsTemporaryFile(file.Name))
                {
                    if (++temporaryFiles > MaxTemporaryFiles)
                        throw CorruptStore();
                    TryDeleteTemporaryFile(path, moduleDirectory);
                    continue;
                }
                if (!string.Equals(file.Extension, EntryExtension, StringComparison.Ordinal))
                    throw CorruptStore();
                if (values.Count >= ModuleSdkV1.MaxDataEntries)
                    throw CorruptStore();
                var recordBytes = ReadBoundedFile(
                    path,
                    ModuleSdkV1.MaxDataValueBytes + MaxRecordOverheadBytes);
                var (key, value) = ParseStorageRecord(recordBytes);
                if (!string.Equals(file.Name, EntryFileName(key), StringComparison.Ordinal))
                    throw CorruptStore();
                if (!values.TryAdd(key, value))
                    throw CorruptStore();
                usedBytes = checked(usedBytes + value.Length);
                if (usedBytes > ModuleSdkV1.MaxDataModuleBytes)
                    throw CorruptStore();
            }
            return new PersistentState(values, usedBytes);
        }
        catch (ModuleDataException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ModuleDataException("storage_corrupt", "The module data store failed integrity validation.", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ModuleDataException("storage_corrupt", "The module data store failed integrity validation.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw StorageUnavailable(ex);
        }
    }

    private static byte[] CanonicalizeValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            throw new ModuleDataException("invalid_value", "value must be JSON-compatible.");
        var nodes = 0;
        ValidateJson(value, 0, ref nodes);
        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
            _ = StrictUtf8.GetString(bytes);
        }
        catch (Exception ex) when (ex is JsonException or EncoderFallbackException or InvalidOperationException)
        {
            throw new ModuleDataException("invalid_value", "value must be valid JSON.", ex);
        }
        if (bytes.Length > ModuleSdkV1.MaxDataValueBytes)
            throw new ModuleDataException("item_too_large", "The JSON value exceeds the per-item byte limit.");
        return bytes;
    }

    private static void ValidateJson(JsonElement value, int depth, ref int nodes)
    {
        if (depth > MaxJsonDepth || ++nodes > MaxJsonNodes)
            throw new ModuleDataException("invalid_value", "The JSON value is too complex.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new ModuleDataException("invalid_value", "JSON object property names must be unique.");
                    ValidateJson(property.Value, depth + 1, ref nodes);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    ValidateJson(item, depth + 1, ref nodes);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new ModuleDataException("invalid_value", "JSON numbers must be finite.");
                break;
            case JsonValueKind.String:
                try
                {
                    _ = StrictUtf8.GetByteCount(value.GetString() ?? "");
                }
                catch (EncoderFallbackException ex)
                {
                    throw new ModuleDataException("invalid_value", "JSON strings must be valid UTF-8 text.", ex);
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new ModuleDataException("invalid_value", "value must be JSON-compatible.");
        }
    }

    private static byte[] BuildStorageRecord(string key, byte[] valueBytes)
    {
        using var value = JsonDocument.Parse(valueBytes);
        return JsonSerializer.SerializeToUtf8Bytes(
            new StorageRecord(StorageSchemaVersion, key, value.RootElement.Clone()),
            StorageRecordSerializerOptions);
    }

    private static (string Key, byte[] Value) ParseStorageRecord(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxJsonDepth + 3
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw CorruptStore();
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != 3 ||
            properties.Count(property => property.NameEquals("schemaVersion")) != 1 ||
            properties.Count(property => property.NameEquals("key")) != 1 ||
            properties.Count(property => property.NameEquals("value")) != 1 ||
            !root.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var version) || version != StorageSchemaVersion ||
            !root.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("value", out var valueElement))
        {
            throw CorruptStore();
        }
        var key = keyElement.GetString() ?? "";
        if (!ModuleSdkV1.IsValidDataKey(key))
            throw CorruptStore();
        return (key, CanonicalizeValue(valueElement));
    }

    private void EnsurePersistentRoot(bool create)
    {
        if (!Directory.Exists(persistentRoot))
        {
            if (!create)
                throw CorruptStore();
            Directory.CreateDirectory(persistentRoot);
        }
        var info = new DirectoryInfo(persistentRoot);
        if (IsReparsePoint(info))
            throw CorruptStore();
    }

    private void EnsureModuleDirectory(string path, bool create)
    {
        EnsureDirectChild(persistentRoot, path);
        if (!Directory.Exists(path))
        {
            if (!create)
                throw CorruptStore();
            Directory.CreateDirectory(path);
        }
        if (IsReparsePoint(new DirectoryInfo(path)))
            throw CorruptStore();
    }

    private string ModuleDirectory(string moduleId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("meccha-module-data-v1\0" + moduleId));
        return Path.Combine(persistentRoot, Convert.ToHexString(digest).ToLowerInvariant());
    }

    private static string EntryPath(string moduleDirectory, string key) =>
        Path.Combine(moduleDirectory, EntryFileName(key));

    private static string EntryFileName(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("meccha-module-data-key-v1\0" + key));
        return Convert.ToHexString(digest).ToLowerInvariant() + EntryExtension;
    }

    private static JsonElement ParseOwnedValue(byte[] value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private Dictionary<string, byte[]>? GetMemoryBucket(string moduleId, bool create)
    {
        if (memory.TryGetValue(moduleId, out var bucket))
            return bucket;
        if (!create)
            return null;
        bucket = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        memory[moduleId] = bucket;
        return bucket;
    }

    private object PersistentGate(string moduleId)
    {
        lock (persistentGatesGate)
        {
            if (!persistentGates.TryGetValue(moduleId, out var value))
            {
                value = new object();
                persistentGates[moduleId] = value;
            }
            return value;
        }
    }

    private IDisposable AcquirePersistentLock(string moduleId, bool createNamespace)
    {
        try
        {
            if (!Directory.Exists(persistentRoot))
            {
                if (!createNamespace)
                    return EmptyLease.Instance;
                EnsurePersistentRoot(create: true);
            }
            else
            {
                EnsurePersistentRoot(create: false);
            }
            var moduleDirectory = ModuleDirectory(moduleId);
            if (!Directory.Exists(moduleDirectory))
            {
                if (!createNamespace)
                    return EmptyLease.Instance;
                EnsureModuleDirectory(moduleDirectory, create: true);
            }
            else
            {
                EnsureModuleDirectory(moduleDirectory, create: false);
            }

            var lockPath = Path.Combine(moduleDirectory, LockFileName);
            EnsureDirectChild(moduleDirectory, lockPath);
            if (File.Exists(lockPath) && IsReparsePoint(new FileInfo(lockPath)))
                throw CorruptStore();

            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.WriteThrough);
                    EnsurePersistentRoot(create: false);
                    EnsureModuleDirectory(moduleDirectory, create: false);
                    if (IsReparsePoint(new FileInfo(lockPath)))
                    {
                        stream.Dispose();
                        throw CorruptStore();
                    }
                    return stream;
                }
                catch (IOException) when (stopwatch.Elapsed < PersistentLockTimeout)
                {
                    Thread.Sleep(20);
                }
                catch (IOException ex)
                {
                    throw new ModuleDataException("storage_busy", "The module data store is busy; retry the request.", ex);
                }
            }
        }
        catch (ModuleDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw StorageUnavailable(ex);
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < 2 || stream.Length > maximumBytes)
            throw CorruptStore();
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw CorruptStore();
        return bytes;
    }

    private static int BucketBytes(Dictionary<string, byte[]>? bucket) =>
        bucket?.Values.Sum(value => value.Length) ?? 0;

    private static void Commit(Action<Action>? commit, Action mutation)
    {
        if (commit is null)
            mutation();
        else
            commit(mutation);
    }

    private static void ValidateIdentityAndKey(string moduleId, string key)
    {
        ValidateIdentity(moduleId);
        if (!ModuleSdkV1.IsValidDataKey(key))
            throw new ModuleDataException(
                "invalid_key",
                $"key must match [a-z0-9][a-z0-9._-]{{0,{ModuleSdkV1.MaxDataKeyLength - 1}}}.");
    }

    private static void ValidateIdentity(string moduleId)
    {
        if (!ModuleSdkV1.IsValidId(moduleId))
            throw new ModuleDataException("invalid_module", "The module identity is invalid.");
    }

    private static void EnsureDirectChild(string parent, string candidate)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (!string.Equals(
                Path.GetDirectoryName(normalizedCandidate),
                normalizedParent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw CorruptStore();
        }
    }

    private static bool IsReparsePoint(FileSystemInfo info)
    {
        info.Refresh();
        return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsTemporaryFile(string name) =>
        name.Length == 41 &&
        name.StartsWith(".tmp-", StringComparison.Ordinal) &&
        name.EndsWith(".tmp", StringComparison.Ordinal) &&
        Guid.TryParseExact(name.AsSpan(5, 32), "N", out _);

    private static void TryDeleteTemporaryFile(string path, string moduleDirectory)
    {
        try
        {
            EnsureDirectChild(moduleDirectory, path);
            if (File.Exists(path) && !IsReparsePoint(new FileInfo(path)) &&
                IsTemporaryFile(Path.GetFileName(path)))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static ModuleDataException CorruptStore() =>
        new("storage_corrupt", "The module data store failed integrity validation.");

    private static ModuleDataException StorageUnavailable(Exception exception) =>
        new("storage_unavailable", "The module data store is unavailable.", exception);

    private sealed record StorageRecord(int SchemaVersion, string Key, JsonElement Value);
    private sealed record PersistentState(Dictionary<string, byte[]> Values, int UsedBytes);

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();

        public void Dispose() { }
    }
}
