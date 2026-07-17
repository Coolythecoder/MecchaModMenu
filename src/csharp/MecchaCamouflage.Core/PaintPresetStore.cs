using System.Text.Json;
using System.Text.Json.Serialization;

namespace MecchaCamouflage.Core;

public sealed record PaintPreset(string Name, PaintSettings Paint, DateTimeOffset UpdatedAt);

public sealed class PaintPresetStore
{
    public const int SchemaVersion = 1;
    public const int MaximumPresets = 64;
    public const int MaximumNameLength = 40;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private readonly string path;
    private readonly object gate = new();

    public PaintPresetStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        path = System.IO.Path.Combine(rootDirectory, "paint-presets.json");
    }

    public string Path => path;

    public IReadOnlyList<PaintPreset> Load()
    {
        lock (gate)
            return LoadUnlocked();
    }

    public PaintPreset Save(string name, PaintSettings paint)
    {
        ArgumentNullException.ThrowIfNull(paint);
        var normalizedName = ValidateName(name);
        lock (gate)
        {
            var presets = LoadUnlocked().ToList();
            var existing = presets.FindIndex(item =>
                string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
            if (existing < 0 && presets.Count >= MaximumPresets)
                throw new InvalidOperationException($"Paint Studio supports at most {MaximumPresets} presets.");

            var saved = new PaintPreset(
                normalizedName,
                CloneAndClamp(paint),
                DateTimeOffset.UtcNow);
            if (existing >= 0)
                presets[existing] = saved;
            else
                presets.Add(saved);
            WriteUnlocked(presets);
            return saved;
        }
    }

    public bool TryGet(string name, out PaintPreset preset)
    {
        var normalizedName = ValidateName(name);
        lock (gate)
        {
            var found = LoadUnlocked().FirstOrDefault(item =>
                string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                preset = null!;
                return false;
            }
            preset = new PaintPreset(found.Name, CloneAndClamp(found.Paint), found.UpdatedAt);
            return true;
        }
    }

    public bool Delete(string name)
    {
        var normalizedName = ValidateName(name);
        lock (gate)
        {
            var presets = LoadUnlocked().ToList();
            var removed = presets.RemoveAll(item =>
                string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                WriteUnlocked(presets);
            return removed;
        }
    }

    public static string ValidateName(string? name)
    {
        var normalized = (name ?? "").Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Preset name is required.");
        if (normalized.Length > MaximumNameLength)
            throw new ArgumentException($"Preset name must be {MaximumNameLength} characters or fewer.");
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Preset name cannot contain control characters.");
        return normalized;
    }

    public static PaintSettings CloneAndClamp(PaintSettings paint)
    {
        var copy = new PaintSettings
        {
            Brush1SizeTexels = paint.Brush1SizeTexels,
            Brush2SizeTexels = paint.Brush2SizeTexels,
            DetailResolutionPercent = paint.DetailResolutionPercent,
            CoverageStepTexels = paint.CoverageStepTexels,
            PackedBatchLimit = paint.PackedBatchLimit,
            PackedBatchPacingMs = paint.PackedBatchPacingMs,
            SideSourceMaxUv = paint.SideSourceMaxUv,
            FrontBackSourceMaxUv = paint.FrontBackSourceMaxUv,
            FrontRegionMode = paint.FrontRegionMode,
            SideRegionMode = paint.SideRegionMode,
            BackRegionMode = paint.BackRegionMode,
            AutoMaterial = paint.AutoMaterial,
            Metallic = paint.Metallic,
            Roughness = paint.Roughness,
            FillColor = new RgbColor(paint.FillColor.R, paint.FillColor.G, paint.FillColor.B),
            FillMetallic = paint.FillMetallic,
            FillRoughness = paint.FillRoughness
        };
        return SettingsStore.Clamp(new AppSettings { Paint = copy }).Paint;
    }

    private IReadOnlyList<PaintPreset> LoadUnlocked()
    {
        if (!File.Exists(path))
            return [];
        try
        {
            var document = JsonSerializer.Deserialize<PresetDocument>(File.ReadAllText(path), Options);
            if (document is null || document.SchemaVersion != SchemaVersion)
                return [];
            return (document.Presets ?? [])
                .Where(item => item is not null && item.Paint is not null)
                .Select(item => new PaintPreset(
                    ValidateName(item!.Name),
                    CloneAndClamp(item.Paint!),
                    item.UpdatedAt))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumPresets)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private void WriteUnlocked(IEnumerable<PaintPreset> presets)
    {
        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Paint preset path has no parent directory.");
        Directory.CreateDirectory(directory);
        var ordered = presets
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new PaintPreset(item.Name, CloneAndClamp(item.Paint), item.UpdatedAt))
            .ToArray();
        var json = JsonSerializer.Serialize(
            new PresetDocument(SchemaVersion, ordered),
            Options);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json + Environment.NewLine);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record PresetDocument(int SchemaVersion, IReadOnlyList<PaintPreset>? Presets);
}
