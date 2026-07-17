using System.Text.Json;
using System.Text.Json.Nodes;

namespace MecchaCamouflage.Core;

public sealed class SettingsStore
{
    private const int DetailResolution500DefaultLayoutVersion = 39;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly AppPaths paths;

    public SettingsStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public AppSettings Load()
    {
        if (!File.Exists(paths.ConfigPath))
            return Clamp(new AppSettings());

        var text = File.ReadAllText(paths.ConfigPath);
        var root = JsonNode.Parse(text)?.AsObject();
        if (root is null)
            return Clamp(new AppSettings());

        var settings = new AppSettings();
        // A persisted config without a layout version predates versioned migration.
        settings.LayoutVersion = ReadInt(root, "layout_version", 0);
        settings.PanelX = ReadDouble(root, "panel_x", settings.PanelX);
        settings.PanelY = ReadDouble(root, "panel_y", settings.PanelY);
        settings.PanelWidth = ReadDouble(root, "panel_width", settings.PanelWidth);
        settings.PanelHeight = ReadDouble(root, "panel_height", settings.PanelHeight);
        settings.Language = ReadString(root, "language", settings.Language);
        settings.GameProcessName = ReadString(root, "game_process_name", settings.GameProcessName);
        settings.AlwaysOnTop = ReadBool(root, "always_on_top", settings.AlwaysOnTop);
        settings.Opacity = ReadDouble(root, "opacity", settings.Opacity);
        if (RgbColor.TryParse(ReadString(root, "theme_color", settings.ThemeColor.ToHex()), out var theme))
            settings.ThemeColor = theme;
        settings.StartHotkey = ReadString(root, "start_hotkey", settings.StartHotkey);
        settings.StopHotkey = ReadString(root, "stop_hotkey", settings.StopHotkey);
        settings.PreviewHotkey = ReadString(root, "preview_hotkey", settings.PreviewHotkey);
        settings.UnPreviewHotkey = ReadString(root, "unpreview_hotkey", settings.UnPreviewHotkey);
        settings.LogRetentionDays = ReadInt(root, "log_retention_days", settings.LogRetentionDays);

        var paint = settings.Paint;
        paint.Brush1SizeTexels = ReadDouble(root, "brush_1_size_texels", paint.Brush1SizeTexels);
        paint.DetailResolutionPercent = settings.LayoutVersion < DetailResolution500DefaultLayoutVersion
            ? 500
            : ReadInt(root, "detail_resolution_percent", paint.DetailResolutionPercent);
        if (root.TryGetPropertyValue("brush_2_size_texels", out var brush2Value) && brush2Value is not null)
        {
            paint.Brush2SizeTexels = brush2Value.GetValue<double>();
        }
        else if (root.TryGetPropertyValue("stroke_size_texels", out var legacyStrokeValue) && legacyStrokeValue is not null)
        {
            var legacyStrokeSize = legacyStrokeValue.GetValue<double>();
            paint.Brush2SizeTexels = settings.LayoutVersion <= 36 && Math.Abs(legacyStrokeSize - 5.0) < 0.000001
                ? 10.0
                : legacyStrokeSize;
        }
        var hasLegacyPacingMode =
            root.TryGetPropertyValue("pacing_mode", out var legacyPacingModeValue) &&
            legacyPacingModeValue is not null;
        var legacyPacingMode = hasLegacyPacingMode
            ? legacyPacingModeValue!.GetValue<string>().Trim().ToLowerInvariant()
            : "";
        var hasLegacyBatchDelay =
            root.TryGetPropertyValue("packed_batch_delay_ms", out var legacyBatchDelayValue) &&
            legacyBatchDelayValue is not null;
        var legacyBatchDelayMs = ReadInt(root, "packed_batch_delay_ms", 75);
        paint.PackedBatchLimit = ReadInt(
            root,
            "packed_batch_limit",
            legacyPacingMode == "compatibility" ? 6 : 20);
        paint.PackedBatchPacingMs = ReadInt(
            root,
            "packed_batch_pacing_ms",
            legacyPacingMode switch
            {
                "manual_slower" => legacyBatchDelayMs,
                "compatibility" => 75,
                _ when !hasLegacyPacingMode && hasLegacyBatchDelay => legacyBatchDelayMs,
                _ => 50
            });
        paint.CoverageStepTexels = paint.Brush2SizeTexels;
        paint.SideSourceMaxUv = ReadDouble(root, "side_source_max_uv", paint.SideSourceMaxUv);
        paint.FrontBackSourceMaxUv = ReadDouble(root, "front_back_source_max_uv", paint.FrontBackSourceMaxUv);
        paint.FrontRegionMode = ReadRegionMode(root, "front_region_mode", paint.FrontRegionMode);
        paint.SideRegionMode = ReadRegionMode(root, "side_region_mode", paint.SideRegionMode);
        paint.BackRegionMode = ReadRegionMode(root, "back_region_mode", paint.BackRegionMode);
        paint.AutoMaterial = ReadBool(root, "auto_material", paint.AutoMaterial);
        paint.Metallic = ReadDouble(root, "metallic", paint.Metallic);
        paint.Roughness = ReadDouble(root, "roughness", paint.Roughness);
        if (RgbColor.TryParse(ReadString(root, "fill_color", paint.FillColor.ToHex()), out var fill))
            paint.FillColor = fill;
        paint.FillMetallic = ReadDouble(root, "fill_metallic", paint.FillMetallic);
        paint.FillRoughness = ReadDouble(root, "fill_roughness", paint.FillRoughness);

        return Clamp(settings);
    }

    public void Save(AppSettings settings)
    {
        paths.EnsureBaseDirectories();
        var clamped = Clamp(settings);
        var json = JsonSerializer.Serialize(ToConfigDto(clamped), Options);
        var tmp = paths.ConfigPath + ".tmp";
        File.WriteAllText(tmp, json + Environment.NewLine);
        if (File.Exists(paths.ConfigPath))
            File.Delete(paths.ConfigPath);
        File.Move(tmp, paths.ConfigPath);
    }

    public static AppSettings Clamp(AppSettings settings)
    {
        settings.LayoutVersion = AppSettings.CurrentLayoutVersion;
        settings.PanelWidth = Math.Clamp(settings.PanelWidth, 960.0, 3200.0);
        settings.PanelHeight = Math.Clamp(settings.PanelHeight, 640.0, 2200.0);
        settings.Opacity = Math.Clamp(settings.Opacity, 0.35, 1.0);
        if (string.IsNullOrWhiteSpace(settings.Language))
            settings.Language = LocalizationCatalog.DetectSystemLanguage();
        if (!LocalizationCatalog.IsSupported(settings.Language))
            settings.Language = "en";
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 90);
        if (string.IsNullOrWhiteSpace(settings.GameProcessName))
            settings.GameProcessName = "PenguinHotel-Win64-Shipping.exe";
        if (string.IsNullOrWhiteSpace(settings.StartHotkey))
            settings.StartHotkey = "F1";
        if (string.IsNullOrWhiteSpace(settings.PreviewHotkey))
            settings.PreviewHotkey = "F2";
        if (string.IsNullOrWhiteSpace(settings.UnPreviewHotkey))
            settings.UnPreviewHotkey = "F3";
        if (string.IsNullOrWhiteSpace(settings.StopHotkey))
            settings.StopHotkey = "F4";

        settings.Paint.Brush1SizeTexels = Math.Clamp(settings.Paint.Brush1SizeTexels, 10.0, 30.0);
        settings.Paint.Brush2SizeTexels = Math.Clamp(settings.Paint.Brush2SizeTexels, 5.0, 10.0);
        settings.Paint.DetailResolutionPercent = Math.Clamp(settings.Paint.DetailResolutionPercent, 50, 500);
        settings.Paint.PackedBatchLimit = Math.Clamp(settings.Paint.PackedBatchLimit, 1, 20);
        settings.Paint.PackedBatchPacingMs = Math.Clamp(settings.Paint.PackedBatchPacingMs, 50, 500);
        settings.Paint.CoverageStepTexels = settings.Paint.Brush2SizeTexels;
        settings.Paint.SideSourceMaxUv = Math.Clamp(settings.Paint.SideSourceMaxUv, 0.001, 0.50);
        settings.Paint.FrontBackSourceMaxUv = Math.Clamp(settings.Paint.FrontBackSourceMaxUv, 0.001, 2.00);
        settings.Paint.Metallic = Math.Clamp(settings.Paint.Metallic, 0.0, 1.0);
        settings.Paint.Roughness = Math.Clamp(settings.Paint.Roughness, 0.0, 1.0);
        settings.Paint.FillMetallic = Math.Clamp(settings.Paint.FillMetallic, 0.0, 1.0);
        settings.Paint.FillRoughness = Math.Clamp(settings.Paint.FillRoughness, 0.0, 1.0);
        return settings;
    }

    private static object ToConfigDto(AppSettings settings) => new
    {
        layout_version = settings.LayoutVersion,
        panel_x = settings.PanelX,
        panel_y = settings.PanelY,
        panel_width = settings.PanelWidth,
        panel_height = settings.PanelHeight,
        language = settings.Language,
        log_retention_days = settings.LogRetentionDays,
        game_process_name = settings.GameProcessName,
        always_on_top = settings.AlwaysOnTop,
        opacity = settings.Opacity,
        theme_color = settings.ThemeColor.ToHex(),
        start_hotkey = settings.StartHotkey,
        preview_hotkey = settings.PreviewHotkey,
        unpreview_hotkey = settings.UnPreviewHotkey,
        stop_hotkey = settings.StopHotkey,
        brush_1_size_texels = settings.Paint.Brush1SizeTexels,
        brush_2_size_texels = settings.Paint.Brush2SizeTexels,
        detail_resolution_percent = settings.Paint.DetailResolutionPercent,
        packed_batch_limit = settings.Paint.PackedBatchLimit,
        packed_batch_pacing_ms = settings.Paint.PackedBatchPacingMs,
        coverage_step_texels = settings.Paint.CoverageStepTexels,
        side_source_max_uv = settings.Paint.SideSourceMaxUv,
        front_back_source_max_uv = settings.Paint.FrontBackSourceMaxUv,
        front_region_mode = RegionModeText(settings.Paint.FrontRegionMode),
        side_region_mode = RegionModeText(settings.Paint.SideRegionMode),
        back_region_mode = RegionModeText(settings.Paint.BackRegionMode),
        auto_material = settings.Paint.AutoMaterial,
        metallic = settings.Paint.Metallic,
        roughness = settings.Paint.Roughness,
        fill_color = settings.Paint.FillColor.ToHex(),
        fill_metallic = settings.Paint.FillMetallic,
        fill_roughness = settings.Paint.FillRoughness
    };

    public static string RegionModeText(RegionMode mode) => mode switch
    {
        RegionMode.Fill => "fill",
        RegionMode.Skip => "skip",
        _ => "paint"
    };

    private static RegionMode ReadRegionMode(JsonObject root, string key, RegionMode fallback)
    {
        var mode = ReadString(root, key, "");
        if (Enum.TryParse<RegionMode>(mode, true, out var parsed))
            return parsed;
        return fallback;
    }

    private static string ReadString(JsonObject root, string key, string fallback) =>
        root.TryGetPropertyValue(key, out var value) && value is not null ? value.GetValue<string>() : fallback;

    private static bool ReadBool(JsonObject root, string key, bool fallback) =>
        root.TryGetPropertyValue(key, out var value) && value is not null ? value.GetValue<bool>() : fallback;

    private static int ReadInt(JsonObject root, string key, int fallback) =>
        root.TryGetPropertyValue(key, out var value) && value is not null ? value.GetValue<int>() : fallback;

    private static double ReadDouble(JsonObject root, string key, double fallback) =>
        root.TryGetPropertyValue(key, out var value) && value is not null ? value.GetValue<double>() : fallback;
}
