using System.Globalization;
using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

public sealed record ModuleProcessMemoryCommandResult(
    bool Success,
    string Code,
    string Message,
    object? Data);

/// <summary>
/// Strict, typed relay contract between trusted local modules and the authenticated
/// bridge already running inside the selected game process. Module input never
/// chooses a PID or supplies an arbitrary native bridge command.
/// </summary>
public static class ModuleProcessMemoryProtocol
{
    private static readonly HashSet<string> ProtectionChangeModes = new(StringComparer.Ordinal)
    {
        "no-access",
        "read-only",
        "read-write",
        "write-copy",
        "execute",
        "execute-read",
        "execute-read-write",
        "execute-write-copy"
    };

    private static readonly HashSet<string> PrivateAllocationModes = new(StringComparer.Ordinal)
    {
        "no-access",
        "read-only",
        "read-write",
        "execute",
        "execute-read",
        "execute-read-write"
    };

    public static IReadOnlyCollection<string> AllowedProtectionChanges => ProtectionChangeModes;
    public static IReadOnlyCollection<string> AllowedPrivateAllocationProtections => PrivateAllocationModes;

    public static bool TryBuildBridgeRequest(
        string moduleId,
        string operation,
        JsonElement payload,
        out string request,
        out string code,
        out string message)
    {
        request = "";
        code = "invalid_payload";
        message = "The process-memory payload is invalid.";
        if (!ModuleSdkV1.IsValidId(moduleId) || payload.ValueKind != JsonValueKind.Object)
            return false;

        switch (operation)
        {
            case "process.memory.allocate":
                if (!HasExactFields(payload, ["size"], ["protection"]) ||
                    !TryReadSize(payload, "size", ModuleSdkV1.MaxProcessMemoryAllocationBytes, out var allocationSize) ||
                    !TryReadProtection(
                        payload,
                        "protection",
                        "read-write",
                        PrivateAllocationModes,
                        out var allocationProtection))
                {
                    return false;
                }
                request = JsonSerializer.Serialize(new
                {
                    type = "module_process_memory_allocate",
                    owner = moduleId,
                    size = allocationSize,
                    protection = allocationProtection
                });
                return true;

            case "process.memory.read":
                if (!HasExactFields(payload, ["address", "size"], []) ||
                    !TryReadAddress(payload, "address", out var readAddress) ||
                    !TryReadSize(payload, "size", ModuleSdkV1.MaxProcessMemoryTransferBytes, out var readSize))
                {
                    return false;
                }
                request = JsonSerializer.Serialize(new
                {
                    type = "module_process_memory_read",
                    owner = moduleId,
                    address = readAddress,
                    size = readSize
                });
                return true;

            case "process.memory.write":
                if (!HasExactFields(payload, ["address", "dataHex"], []) ||
                    !TryReadAddress(payload, "address", out var writeAddress) ||
                    !TryReadDataHex(payload, "dataHex", out var writeData))
                {
                    return false;
                }
                request = JsonSerializer.Serialize(new
                {
                    type = "module_process_memory_write",
                    owner = moduleId,
                    address = writeAddress,
                    data_hex = writeData
                });
                return true;

            case "process.memory.protect":
                if (!HasExactFields(payload, ["address", "size", "protection"], []) ||
                    !TryReadAddress(payload, "address", out var protectAddress) ||
                    !TryReadSize(payload, "size", ModuleSdkV1.MaxProcessMemoryAllocationBytes, out var protectSize) ||
                    !TryReadProtection(
                        payload,
                        "protection",
                        null,
                        ProtectionChangeModes,
                        out var protection))
                {
                    return false;
                }
                request = JsonSerializer.Serialize(new
                {
                    type = "module_process_memory_protect",
                    owner = moduleId,
                    address = protectAddress,
                    size = protectSize,
                    protection
                });
                return true;

            case "process.memory.inject":
                if (!HasExactFields(payload, ["dataHex"], ["protection"]) ||
                    !TryReadDataHex(payload, "dataHex", out var injectData) ||
                    !TryReadProtection(
                        payload,
                        "protection",
                        "read-write",
                        PrivateAllocationModes,
                        out var injectProtection))
                {
                    return false;
                }
                request = JsonSerializer.Serialize(new
                {
                    type = "module_process_memory_inject",
                    owner = moduleId,
                    data_hex = injectData,
                    protection = injectProtection
                });
                return true;

            case "process.memory.free":
                if (!HasExactFields(payload, ["address"], []) ||
                    !TryReadAddress(payload, "address", out var freeAddress))
                {
                    return false;
                }
                request = JsonSerializer.Serialize(new
                {
                    type = "module_process_memory_free",
                    owner = moduleId,
                    address = freeAddress
                });
                return true;

            default:
                code = "unsupported_command";
                message = "Unsupported process-memory command.";
                return false;
        }
    }

    public static bool TrySanitizeBridgeResponse(
        string operation,
        string raw,
        out object? data,
        out string message)
    {
        data = null;
        message = "The native bridge returned an invalid process-memory response.";
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryResponseAddress(metadata, out var address))
                return false;

            switch (operation)
            {
                case "process.memory.allocate":
                    if (!TryPositiveResponseInt(metadata, "size", ModuleSdkV1.MaxProcessMemoryAllocationBytes, out var allocationSize) ||
                        !TryResponseProtection(
                            metadata,
                            "protection",
                            PrivateAllocationModes,
                            out var allocationProtection))
                    {
                        return false;
                    }
                    data = new { address, size = allocationSize, protection = allocationProtection };
                    return true;

                case "process.memory.read":
                    if (!TryPositiveResponseInt(metadata, "size", ModuleSdkV1.MaxProcessMemoryTransferBytes, out var readSize) ||
                        !TryResponseDataHex(metadata, readSize, out var dataHex))
                    {
                        return false;
                    }
                    data = new { address, size = readSize, dataHex };
                    return true;

                case "process.memory.write":
                    if (!TryPositiveResponseInt(metadata, "bytes_written", ModuleSdkV1.MaxProcessMemoryTransferBytes, out var bytesWritten) ||
                        !TryResponseBool(metadata, "verified", out var writeVerified) || !writeVerified)
                    {
                        return false;
                    }
                    data = new { address, bytesWritten, verified = true };
                    return true;

                case "process.memory.protect":
                    if (!TryPositiveResponseInt(metadata, "size", ModuleSdkV1.MaxProcessMemoryAllocationBytes, out var protectSize) ||
                        !TryResponseProtection(
                            metadata,
                            "protection",
                            ProtectionChangeModes,
                            out var currentProtection) ||
                        !TryResponsePreviousProtection(metadata, out var previousProtection))
                    {
                        return false;
                    }
                    data = new { address, size = protectSize, protection = currentProtection, previousProtection };
                    return true;

                case "process.memory.inject":
                    if (!TryPositiveResponseInt(metadata, "size", ModuleSdkV1.MaxProcessMemoryTransferBytes, out var injectSize) ||
                        !TryPositiveResponseInt(metadata, "bytes_written", ModuleSdkV1.MaxProcessMemoryTransferBytes, out var injectedBytes) ||
                        injectSize != injectedBytes ||
                        !TryResponseProtection(
                            metadata,
                            "protection",
                            PrivateAllocationModes,
                            out var injectProtection) ||
                        !TryResponseBool(metadata, "verified", out var injectVerified) || !injectVerified)
                    {
                        return false;
                    }
                    data = new
                    {
                        address,
                        size = injectSize,
                        bytesWritten = injectedBytes,
                        protection = injectProtection,
                        verified = true
                    };
                    return true;

                case "process.memory.free":
                    if (!TryResponseBool(metadata, "freed", out var freed) || !freed)
                        return false;
                    data = new { address, freed = true };
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactFields(
        JsonElement payload,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !found.Add(property.Name))
                return false;
        }
        return required.All(found.Contains);
    }

    private static bool TryReadSize(JsonElement payload, string name, int maximum, out int size)
    {
        size = 0;
        return payload.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out size) &&
               size is > 0 && size <= maximum;
    }

    private static bool TryReadAddress(JsonElement payload, string name, out string address)
    {
        address = "";
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        return TryNormalizeAddress(value.GetString(), out address);
    }

    private static bool TryNormalizeAddress(string? value, out string address)
    {
        address = "";
        if (value is null || value.Length is < 3 or > 18 ||
            value[0] != '0' || value[1] is not ('x' or 'X') ||
            !ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed) ||
            parsed == 0)
        {
            return false;
        }
        address = $"0x{parsed:x}";
        return true;
    }

    private static bool TryReadDataHex(JsonElement payload, string name, out string dataHex)
    {
        dataHex = "";
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString();
        if (string.IsNullOrEmpty(text) || (text.Length & 1) != 0 ||
            text.Length / 2 > ModuleSdkV1.MaxProcessMemoryTransferBytes ||
            text.Any(character => !IsHex(character)))
        {
            return false;
        }
        dataHex = text.ToLowerInvariant();
        return true;
    }

    private static bool TryReadProtection(
        JsonElement payload,
        string name,
        string? fallback,
        IReadOnlySet<string> allowed,
        out string protection)
    {
        protection = fallback ?? "";
        if (!payload.TryGetProperty(name, out var value))
            return fallback is not null;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        protection = value.GetString() ?? "";
        return allowed.Contains(protection);
    }

    private static bool TryResponseAddress(JsonElement metadata, out string address)
    {
        address = "";
        return metadata.TryGetProperty("address", out var value) &&
               value.ValueKind == JsonValueKind.String &&
               TryNormalizeAddress(value.GetString(), out address);
    }

    private static bool TryPositiveResponseInt(
        JsonElement metadata,
        string name,
        int maximum,
        out int value)
    {
        value = 0;
        return metadata.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value) &&
               value is > 0 && value <= maximum;
    }

    private static bool TryResponseDataHex(JsonElement metadata, int size, out string dataHex)
    {
        dataHex = "";
        if (!metadata.TryGetProperty("data_hex", out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        var value = element.GetString();
        if (value is null || value.Length != checked(size * 2) || value.Any(character => !IsHex(character)))
            return false;
        dataHex = value.ToLowerInvariant();
        return true;
    }

    private static bool TryResponseProtection(
        JsonElement metadata,
        string name,
        IReadOnlySet<string> allowed,
        out string protection)
    {
        protection = "";
        return metadata.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               allowed.Contains(protection = element.GetString() ?? "");
    }

    private static bool TryResponsePreviousProtection(
        JsonElement metadata,
        out string protection)
    {
        protection = "";
        if (!metadata.TryGetProperty("previous_protection", out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        protection = element.GetString() ?? "";
        return protection == "mixed" || ProtectionChangeModes.Contains(protection);
    }

    private static bool TryResponseBool(JsonElement metadata, string name, out bool value)
    {
        value = false;
        if (!metadata.TryGetProperty(name, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = element.GetBoolean();
        return true;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
