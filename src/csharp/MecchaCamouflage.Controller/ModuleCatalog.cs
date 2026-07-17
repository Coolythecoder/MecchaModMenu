using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

public sealed record ModuleDescriptor(
    int SchemaVersion,
    int ApiVersion,
    string Id,
    string Name,
    string Version,
    string Description,
    string ModuleDirectory,
    string Entry,
    string EntryPath,
    IReadOnlyList<string> Permissions);

public sealed record ModuleCatalogDiagnostic(
    string Code,
    string Message,
    string Path,
    string? ModuleId = null);

public sealed record ModuleCatalogResult(
    IReadOnlyList<ModuleDescriptor> Modules,
    IReadOnlyList<ModuleCatalogDiagnostic> Diagnostics);

/// <summary>
/// Discovers and validates module manifests. This catalog never opens module HTML,
/// executes scripts, or translates permissions into native commands.
/// </summary>
public static class ModuleCatalog
{
    private static readonly HashSet<string> ManifestProperties = new(StringComparer.Ordinal)
    {
        "schema_version",
        "api_version",
        "id",
        "name",
        "version",
        "description",
        "entry",
        "permissions"
    };

    public static ModuleCatalogResult Scan(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ScanDirectory(paths.ModulesDirectory);
    }

    public static ModuleCatalogResult ScanDirectory(string modulesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesDirectory);
        var modules = new List<ModuleDescriptor>();
        var diagnostics = new List<ModuleCatalogDiagnostic>();
        var acceptedIds = new HashSet<string>(ModuleSdkV1.IdComparer);
        var root = Path.GetFullPath(modulesDirectory);

        if (!Directory.Exists(root))
            return new ModuleCatalogResult(modules.AsReadOnly(), diagnostics.AsReadOnly());

        try
        {
            if (IsLink(new DirectoryInfo(root)))
            {
                diagnostics.Add(new ModuleCatalogDiagnostic(
                    "modules_root_link",
                    "The modules directory must not be a symbolic link or reparse point.",
                    root));
                return new ModuleCatalogResult(modules.AsReadOnly(), diagnostics.AsReadOnly());
            }
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            diagnostics.Add(new ModuleCatalogDiagnostic(
                "modules_root_error",
                "The modules directory could not be inspected: " + ex.Message,
                root));
            return new ModuleCatalogResult(modules.AsReadOnly(), diagnostics.AsReadOnly());
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(root)
                .Take(ModuleSdkV1.MaxModuleDirectories + 1)
                .ToArray();
            Array.Sort(directories, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                Path.GetFileName(left),
                Path.GetFileName(right)));
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            diagnostics.Add(new ModuleCatalogDiagnostic(
                "modules_root_error",
                "Module packages could not be enumerated: " + ex.Message,
                root));
            return new ModuleCatalogResult(modules.AsReadOnly(), diagnostics.AsReadOnly());
        }

        if (directories.Length > ModuleSdkV1.MaxModuleDirectories)
        {
            diagnostics.Add(new ModuleCatalogDiagnostic(
                "module_directory_limit",
                $"Only the first {ModuleSdkV1.MaxModuleDirectories} module directories were scanned.",
                root));
            directories = directories[..ModuleSdkV1.MaxModuleDirectories];
        }

        foreach (var moduleDirectory in directories)
        {
            try
            {
                TryAddModule(root, moduleDirectory, acceptedIds, modules, diagnostics);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                diagnostics.Add(new ModuleCatalogDiagnostic(
                    "module_io_error",
                    "The module package could not be inspected: " + ex.Message,
                    moduleDirectory));
            }
        }

        return new ModuleCatalogResult(modules.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static void TryAddModule(
        string modulesRoot,
        string moduleDirectory,
        HashSet<string> acceptedIds,
        List<ModuleDescriptor> modules,
        List<ModuleCatalogDiagnostic> diagnostics)
    {
        if (!IsStrictChild(modulesRoot, moduleDirectory) || IsLink(new DirectoryInfo(moduleDirectory)))
        {
            AddDiagnostic(diagnostics, "module_directory_link",
                "Module directories must be direct, non-linked children of the modules directory.",
                moduleDirectory);
            return;
        }

        var manifestPath = Path.Combine(moduleDirectory, "module.json");
        if (!File.Exists(manifestPath))
        {
            AddDiagnostic(diagnostics, "manifest_missing", "The module package has no module.json.", manifestPath);
            return;
        }
        if (IsLink(new FileInfo(manifestPath)))
        {
            AddDiagnostic(diagnostics, "manifest_link", "module.json must not be a symbolic link or reparse point.", manifestPath);
            return;
        }

        if (!TryReadBounded(manifestPath, ModuleSdkV1.MaxManifestBytes, out var manifestBytes))
        {
            AddDiagnostic(diagnostics, "manifest_too_large",
                $"module.json exceeds the {ModuleSdkV1.MaxManifestBytes}-byte limit.", manifestPath);
            return;
        }

        if (!TryParseManifest(manifestBytes, out var manifest, out var code, out var message))
        {
            AddDiagnostic(diagnostics, code, message, manifestPath);
            return;
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(moduleDirectory));
        if (!string.Equals(folderName, manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic(diagnostics, "manifest_directory_mismatch",
                "The module id must match its package directory name.", manifestPath, manifest.Id);
            return;
        }

        if (!TryValidatePackage(
                moduleDirectory,
                out var packageErrorCode,
                out var packageError,
                out var packageErrorPath))
        {
            AddDiagnostic(
                diagnostics,
                packageErrorCode,
                packageError,
                packageErrorPath,
                manifest.Id);
            return;
        }

        var entryPath = Path.GetFullPath(Path.Combine(
            moduleDirectory,
            manifest.Entry.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictChild(moduleDirectory, entryPath))
        {
            AddDiagnostic(diagnostics, "entry_escape", "The module entry must stay inside its package directory.", entryPath, manifest.Id);
            return;
        }
        if (!TryValidateEntryPath(moduleDirectory, manifest.Entry, out var entryErrorCode, out var entryError))
        {
            AddDiagnostic(diagnostics, entryErrorCode, entryError, entryPath, manifest.Id);
            return;
        }

        var entryInfo = new FileInfo(entryPath);
        if (entryInfo.Length > ModuleSdkV1.MaxEntryBytes)
        {
            AddDiagnostic(diagnostics, "entry_too_large",
                $"The module entry exceeds the {ModuleSdkV1.MaxEntryBytes}-byte limit.", entryPath, manifest.Id);
            return;
        }

        if (!acceptedIds.Add(manifest.Id))
        {
            AddDiagnostic(diagnostics, "duplicate_id", "Another valid module already uses this id.", manifestPath, manifest.Id);
            return;
        }

        modules.Add(new ModuleDescriptor(
            ModuleSdkV1.SchemaVersion,
            ModuleSdkV1.ApiVersion,
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Description,
            Path.GetFullPath(moduleDirectory),
            manifest.Entry,
            entryPath,
            Array.AsReadOnly(manifest.Permissions)));
    }

    private static bool TryParseManifest(
        byte[] bytes,
        out ParsedManifest manifest,
        out string code,
        out string message)
    {
        manifest = ParsedManifest.Empty;
        code = "manifest_invalid_json";
        message = "module.json is not valid JSON.";
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                message = "module.json must contain a JSON object.";
                return false;
            }

            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    code = "manifest_duplicate_property";
                    message = $"module.json repeats the '{property.Name}' property.";
                    return false;
                }
                if (!ManifestProperties.Contains(property.Name))
                {
                    code = "manifest_unknown_property";
                    message = $"module.json contains the unsupported '{property.Name}' property.";
                    return false;
                }
            }

            if (!TryReadInt(root, "schema_version", out var schemaVersion) || schemaVersion != ModuleSdkV1.SchemaVersion)
            {
                code = "manifest_schema";
                message = $"schema_version must be {ModuleSdkV1.SchemaVersion}.";
                return false;
            }
            if (!TryReadInt(root, "api_version", out var apiVersion) || apiVersion != ModuleSdkV1.ApiVersion)
            {
                code = "manifest_api";
                message = $"api_version must be {ModuleSdkV1.ApiVersion}.";
                return false;
            }
            if (!TryReadString(root, "id", out var id) || !ModuleSdkV1.IsValidId(id))
            {
                code = "manifest_id";
                message = $"id must be a lowercase ASCII identifier of at most {ModuleSdkV1.MaxIdLength} characters.";
                return false;
            }
            if (!TryReadString(root, "name", out var name) || !IsDisplayText(name, ModuleSdkV1.MaxNameLength, allowEmpty: false))
            {
                code = "manifest_name";
                message = $"name must be trimmed text of at most {ModuleSdkV1.MaxNameLength} characters.";
                return false;
            }
            if (!TryReadString(root, "version", out var version) || !IsVersion(version))
            {
                code = "manifest_version";
                message = $"version must be a safe value of at most {ModuleSdkV1.MaxVersionLength} characters.";
                return false;
            }
            if (!TryReadOptionalString(root, "description", out var description) ||
                !IsDisplayText(description, ModuleSdkV1.MaxDescriptionLength, allowEmpty: true))
            {
                code = "manifest_description";
                message = $"description must be trimmed text of at most {ModuleSdkV1.MaxDescriptionLength} characters.";
                return false;
            }
            if (!TryReadString(root, "entry", out var entry) || !ModuleSdkV1.IsValidRelativeHtmlEntry(entry))
            {
                code = "manifest_entry";
                message = "entry must be a safe relative path to an .html file.";
                return false;
            }
            if (!TryReadPermissions(root, out var permissions, out message))
            {
                code = "manifest_permissions";
                return false;
            }

            manifest = new ParsedManifest(id, name, version, description, entry, permissions);
            code = "";
            message = "";
            return true;
        }
        catch (JsonException ex)
        {
            message = "module.json is not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static bool TryReadPermissions(JsonElement root, out string[] permissions, out string message)
    {
        permissions = [];
        message = "";
        if (!root.TryGetProperty("permissions", out var value))
            return true;
        if (value.ValueKind != JsonValueKind.Array)
        {
            message = "permissions must be an array of permission strings.";
            return false;
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string permission ||
                !ModuleSdkV1.IsAllowedPermission(permission))
            {
                message = "permissions contains a value outside the API v1 allowlist.";
                return false;
            }
            if (!seen.Add(permission))
            {
                message = "permissions must not contain duplicates.";
                return false;
            }
            values.Add(permission);
        }
        permissions = values.ToArray();
        return true;
    }

    private static bool TryValidateEntryPath(
        string moduleDirectory,
        string entry,
        out string code,
        out string message)
    {
        var current = moduleDirectory;
        var segments = entry.Split('/');
        for (var index = 0; index < segments.Length; ++index)
        {
            current = Path.Combine(current, segments[index]);
            var isEntry = index == segments.Length - 1;
            FileSystemInfo info = isEntry ? new FileInfo(current) : new DirectoryInfo(current);
            if (!info.Exists)
            {
                code = "entry_missing";
                message = isEntry
                    ? "The declared module entry does not exist."
                    : "A directory in the declared module entry does not exist.";
                return false;
            }
            if (IsLink(info))
            {
                code = "entry_link";
                message = "The module entry path must not contain symbolic links or reparse points.";
                return false;
            }
        }

        code = "";
        message = "";
        return true;
    }

    private static bool TryValidatePackage(
        string moduleDirectory,
        out string code,
        out string message,
        out string problemPath)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(moduleDirectory));
        var directoryCount = 0;
        var fileCount = 0;
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var fullPath = Path.GetFullPath(directory);
                if (!IsStrictChild(moduleDirectory, fullPath) || IsLink(new DirectoryInfo(fullPath)))
                {
                    code = "package_link";
                    message = "Module packages must not contain linked or escaping directories.";
                    problemPath = fullPath;
                    return false;
                }
                ++directoryCount;
                if (directoryCount > ModuleSdkV1.MaxPackageDirectories)
                {
                    code = "package_directory_limit";
                    message = $"A module package may contain at most {ModuleSdkV1.MaxPackageDirectories} directories.";
                    problemPath = moduleDirectory;
                    return false;
                }
                pending.Push(fullPath);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                var fullPath = Path.GetFullPath(file);
                var info = new FileInfo(fullPath);
                if (!IsStrictChild(moduleDirectory, fullPath) || IsLink(info))
                {
                    code = "package_link";
                    message = "Module packages must not contain linked or escaping files.";
                    problemPath = fullPath;
                    return false;
                }
                ++fileCount;
                if (fileCount > ModuleSdkV1.MaxPackageFiles)
                {
                    code = "package_file_limit";
                    message = $"A module package may contain at most {ModuleSdkV1.MaxPackageFiles} files.";
                    problemPath = moduleDirectory;
                    return false;
                }
                if (info.Length > ModuleSdkV1.MaxAssetBytes)
                {
                    code = "package_asset_too_large";
                    message = $"A module asset exceeds the {ModuleSdkV1.MaxAssetBytes}-byte limit.";
                    problemPath = fullPath;
                    return false;
                }
                if (info.Length > ModuleSdkV1.MaxPackageBytes - totalBytes)
                {
                    code = "package_too_large";
                    message = $"A module package exceeds the {ModuleSdkV1.MaxPackageBytes}-byte limit.";
                    problemPath = moduleDirectory;
                    return false;
                }
                totalBytes += info.Length;
            }
        }

        code = "";
        message = "";
        problemPath = "";
        return true;
    }

    private static bool TryReadBounded(string path, int maxBytes, out byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[maxBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
                break;
            length += read;
        }
        if (length > maxBytes)
        {
            bytes = [];
            return false;
        }
        bytes = buffer[..length];
        return true;
    }

    private static bool IsStrictChild(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var childPath = Path.GetFullPath(child);
        var prefix = parentPath + Path.DirectorySeparatorChar;
        return childPath.StartsWith(prefix, comparison) && childPath.Length > prefix.Length;
    }

    private static bool IsLink(FileSystemInfo info)
    {
        info.Refresh();
        return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool TryReadInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return true;
    }

    private static bool TryReadOptionalString(JsonElement root, string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return true;
    }

    private static bool IsDisplayText(string value, int maxLength, bool allowEmpty) =>
        value.Length <= maxLength &&
        (allowEmpty || value.Length > 0) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsVersion(string value) =>
        value.Length is > 0 and <= ModuleSdkV1.MaxVersionLength &&
        IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_');

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    private static void AddDiagnostic(
        List<ModuleCatalogDiagnostic> diagnostics,
        string code,
        string message,
        string path,
        string? moduleId = null) =>
        diagnostics.Add(new ModuleCatalogDiagnostic(code, message, path, moduleId));

    private sealed record ParsedManifest(
        string Id,
        string Name,
        string Version,
        string Description,
        string Entry,
        string[] Permissions)
    {
        public static ParsedManifest Empty { get; } = new("", "", "", "", "", []);
    }
}
