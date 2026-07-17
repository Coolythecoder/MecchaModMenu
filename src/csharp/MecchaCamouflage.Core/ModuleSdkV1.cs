using System.Security.Cryptography;
using System.Text;

namespace MecchaCamouflage.Core;

/// <summary>
/// Stable, data-only contract for third-party web modules. Loading a manifest grants
/// no permission and does not execute its entry point.
/// </summary>
public static class ModuleSdkV1
{
    public const int SchemaVersion = 1;
    public const int ApiVersion = 1;
    public const int MaxModuleDirectories = 128;
    public const int MaxManifestBytes = 64 * 1024;
    public const long MaxEntryBytes = 4L * 1024 * 1024;
    public const int MaxPackageDirectories = 128;
    public const int MaxPackageFiles = 256;
    public const long MaxAssetBytes = 8L * 1024 * 1024;
    public const long MaxPackageBytes = 32L * 1024 * 1024;
    public const int MaxIdLength = 64;
    public const int MaxNameLength = 80;
    public const int MaxVersionLength = 40;
    public const int MaxDescriptionLength = 320;
    public const int MaxEntryLength = 240;
    public const int MaxDataKeyLength = 128;
    public const int MaxDataEntries = 256;
    public const int MaxDataValueBytes = 256 * 1024;
    public const int MaxDataModuleBytes = 4 * 1024 * 1024;
    public const int MaxMemoryTotalBytes = 64 * 1024 * 1024;

    public const string SnapshotReadPermission = "snapshot.read";
    public const string PaintStartPermission = "paint.start";
    public const string PaintPreviewPermission = "paint.preview";
    public const string PaintRestorePermission = "paint.restore";
    public const string PaintStopPermission = "paint.stop";
    public const string NetworkHttpPermission = "network.http";
    public const string NetworkHttpsPermission = "network.https";
    public const string NetworkBeaconPermission = "network.beacon";
    public const string NetworkWebSocketPermission = "network.websocket";
    public const string StorageReadPermission = "storage.read";
    public const string StorageWritePermission = "storage.write";
    public const string MemoryReadPermission = "memory.read";
    public const string MemoryWritePermission = "memory.write";

    private static readonly string[] PermissionValues =
    [
        SnapshotReadPermission,
        PaintStartPermission,
        PaintPreviewPermission,
        PaintRestorePermission,
        PaintStopPermission,
        NetworkHttpPermission,
        NetworkHttpsPermission,
        NetworkBeaconPermission,
        NetworkWebSocketPermission,
        StorageReadPermission,
        StorageWritePermission,
        MemoryReadPermission,
        MemoryWritePermission
    ];

    public static StringComparer IdComparer { get; } = StringComparer.OrdinalIgnoreCase;
    public static IReadOnlyList<string> AllowedPermissions { get; } = Array.AsReadOnly(PermissionValues);

    public static bool IsAllowedPermission(string? permission) =>
        permission is not null && PermissionValues.Contains(permission, StringComparer.Ordinal);

    public static string? RequiredPermissionForDataCommand(string? command) => command switch
    {
        "storage.get" or "storage.list" => StorageReadPermission,
        "storage.set" or "storage.delete" => StorageWritePermission,
        "memory.get" or "memory.list" => MemoryReadPermission,
        "memory.set" or "memory.delete" => MemoryWritePermission,
        _ => null
    };

    /// <summary>
    /// Module data keys are logical, case-sensitive names. They are never used as
    /// filesystem paths; the persistent store hashes them before choosing a file.
    /// </summary>
    public static bool IsValidDataKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxDataKeyLength ||
            (!IsLowerAsciiLetter(key[0]) && !IsAsciiDigit(key[0])))
        {
            return false;
        }

        return key.All(value =>
            IsLowerAsciiLetter(value) || IsAsciiDigit(value) || value is '.' or '-' or '_');
    }

    /// <summary>
    /// Builds the CSP connect-src value for one already-validated module. Browser
    /// networking is broad for every accepted module; network permission names
    /// remain valid manifest metadata but do not narrow this policy.
    /// </summary>
    public static string ConnectSourcePolicy(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return "http: https: ws: wss:";
    }

    /// <summary>
    /// Builds the complete host-owned policy for one validated module snapshot.
    /// Keeping this next to the permission contract prevents staging callers from
    /// applying a policy derived from stale manifest data.
    /// </summary>
    public static string ContentSecurityPolicy(IEnumerable<string> permissions) =>
        "default-src 'self'; base-uri 'none'; connect-src " +
        ConnectSourcePolicy(permissions) +
        "; object-src 'none'; form-action 'none'; frame-src 'self'; worker-src 'none'; " +
        "script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; media-src 'self'";

    /// <summary>
    /// Returns a stable, DNS-safe origin for one accepted module. Keeping every module on a
    /// distinct origin prevents same-origin scripts from borrowing another module's grants.
    /// </summary>
    public static string VirtualHostName(string id)
    {
        if (!IsValidId(id))
            throw new ArgumentException("A valid module id is required.", nameof(id));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        // Each module gets a direct child of the localhost public suffix. Avoiding a
        // shared intermediate parent prevents Domain cookies from spanning modules.
        return $"m-{Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant()}.localhost";
    }

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || !IsLowerAsciiLetter(id[0]))
            return false;

        var previousWasSeparator = false;
        foreach (var value in id)
        {
            if (IsLowerAsciiLetter(value) || IsAsciiDigit(value))
            {
                previousWasSeparator = false;
                continue;
            }

            if (value is not ('.' or '-' or '_') || previousWasSeparator)
                return false;
            previousWasSeparator = true;
        }
        return !previousWasSeparator;
    }

    public static bool IsValidRelativeHtmlEntry(string? entry)
    {
        if (string.IsNullOrEmpty(entry) || entry.Length > MaxEntryLength ||
            entry[0] == '/' || entry.Contains('\\') || entry.Contains(':') ||
            !entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var segment in entry.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                !IsSafeEntryCharacter(segment[0]) ||
                segment.Any(value => !IsSafeEntryCharacter(value)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafeEntryCharacter(char value) =>
        IsAsciiLetter(value) || IsAsciiDigit(value) || value is '.' or '-' or '_';

    private static bool IsAsciiLetter(char value) =>
        IsLowerAsciiLetter(value) || value is >= 'A' and <= 'Z';

    private static bool IsLowerAsciiLetter(char value) => value is >= 'a' and <= 'z';

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}
