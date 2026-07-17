using System.Globalization;
using System.Text.Json;

namespace MecchaCamouflage.Core;

public sealed record PaintRequestOptions(bool PreviewOnly = false, bool UnPreviewOnly = false, bool ResearchArtifacts = false);

public static class BridgePayloadBuilder
{
    public static string BuildPaintPayload(AppSettings settings, int processId, string processName, PaintRequestOptions options)
    {
        var paint = SettingsStore.Clamp(settings).Paint;
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "paint_full_route",
            ["native_apply_mode"] = "mesh_first_paint",
            ["route"] = "f10_mesh_first_paint",
            ["server_batch_rpc"] = "packed",
            ["packed_route"] = "component",
            ["preview_only"] = options.PreviewOnly,
            ["unpreview_only"] = options.UnPreviewOnly,
            ["research_artifacts"] = options.ResearchArtifacts,
            ["process"] = new Dictionary<string, object?>
            {
                ["pid"] = processId,
                ["name"] = processName
            },
            ["tuning"] = new Dictionary<string, object?>
            {
                ["brush_1_size_texels"] = paint.Brush1SizeTexels,
                ["brush_2_size_texels"] = paint.Brush2SizeTexels,
                ["detail_resolution_percent"] = paint.DetailResolutionPercent,
                ["brush_pipeline_version"] = 2,
                ["stroke_size_texels"] = paint.Brush2SizeTexels,
                ["server_batch_limit"] = paint.PackedBatchLimit,
                ["server_batch_pacing_ms"] = paint.PackedBatchPacingMs,
                ["coverage_step_texels"] = paint.Brush2SizeTexels,
                ["side_source_max_uv"] = paint.SideSourceMaxUv,
                ["front_back_source_max_uv"] = paint.FrontBackSourceMaxUv,
                ["auto_material"] = paint.AutoMaterial,
                ["metallic"] = paint.Metallic,
                ["roughness"] = paint.Roughness,
                ["front_region_mode"] = SettingsStore.RegionModeText(paint.FrontRegionMode),
                ["side_region_mode"] = SettingsStore.RegionModeText(paint.SideRegionMode),
                ["back_region_mode"] = SettingsStore.RegionModeText(paint.BackRegionMode),
                ["fill_color"] = paint.FillColor.ToHex(),
                ["fill_color_r"] = ToUnit(paint.FillColor.R),
                ["fill_color_g"] = ToUnit(paint.FillColor.G),
                ["fill_color_b"] = ToUnit(paint.FillColor.B),
                ["fill_metallic"] = paint.FillMetallic,
                ["fill_roughness"] = paint.FillRoughness
            }
        };
        return JsonSerializer.Serialize(payload) + "\n";
    }

    private static double ToUnit(byte value) =>
        double.Parse((value / 255.0).ToString("0.########", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
