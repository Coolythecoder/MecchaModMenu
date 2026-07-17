using MecchaCamouflage.Core;
using System.Text.Json;

namespace MecchaCamouflage.Controller;

public sealed record UiSnapshot(
    string Version,
    string Language,
    RuntimeSnapshot Runtime,
    ModsSnapshot Mods,
    PaintStudioSnapshot PaintStudio,
    IReadOnlyList<ExternalModuleSnapshot> ExternalModules,
    IReadOnlyList<ModuleDiagnosticSnapshot> ModuleDiagnostics,
    SettingsSnapshot Settings,
    SettingsSnapshot Defaults,
    ResetSnapshot ResetState,
    IReadOnlyList<LocaleSnapshot> Locales,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations);

public sealed record ModsSnapshot(
    AutoPaintModSnapshot AutoPaint,
    PaintStudioModSnapshot PaintStudio);

public sealed record AutoPaintModSnapshot(
    bool Available,
    bool Running);

public sealed record PaintStudioModSnapshot(bool Available);

public sealed record PaintStudioSnapshot(
    int DetailResolutionPercent,
    IReadOnlyList<PaintPresetSnapshot> Presets,
    bool CanUndoSettings,
    PaintCoverageSnapshot Coverage);

public sealed record PaintPresetSnapshot(string Name, string UpdatedAt);

public sealed record PaintCoverageSnapshot(
    bool Available,
    double CoveragePercent,
    int EnabledSamples,
    int TotalSamples,
    int PlannedStrokes,
    int DetailSelected,
    int DetailBudget,
    string Result);

public sealed record ExternalModuleSnapshot(
    string Id,
    string Name,
    string Version,
    string Description,
    string EntryUrl,
    IReadOnlyList<string> Permissions);

public sealed record ModuleDiagnosticSnapshot(string Code, string Message, string ModuleId);

public sealed record RuntimeSnapshot(
    string Process,
    string Bridge,
    string Service,
    double ProgressPercent,
    string PaintProgressSource,
    string PaintPass,
    string PaintPassProgress,
    string PaintPassEta,
    string PaintEta,
    string PaintElapsed,
    string Batch,
    string Pacing,
    string Queue,
    string Logs,
    bool PaintRunning,
    bool ProgressVisible,
    DiagnosticsSnapshot Diagnostics);

public sealed record SettingsSnapshot(PaintSnapshot Paint, AppSnapshot App);

public sealed record PaintSnapshot(
    double Brush1SizeTexels,
    double Brush2SizeTexels,
    int DetailResolutionPercent,
    int PackedBatchLimit,
    int PackedBatchPacingMs,
    bool AutoMaterial,
    double Metallic,
    double Roughness,
    string FrontRegionMode,
    string SideRegionMode,
    string BackRegionMode,
    string FillColor,
    double FillMetallic,
    double FillRoughness,
    bool UsesFill);

public sealed record AppSnapshot(
    string ProcessName,
    bool AlwaysOnTop,
    double Opacity,
    string ThemeColor,
    string StartHotkey,
    string PreviewHotkey,
    string UnPreviewHotkey,
    string StopHotkey);

public sealed record ResetSnapshot(
    IReadOnlyDictionary<string, bool> Settings,
    IReadOnlyDictionary<string, bool> Sections);

public sealed record LocaleSnapshot(string Code, string NativeName);

public sealed record HostCommandResult(bool Success, string Message = "");

public sealed record SettingChange(string Key, JsonElement Value);

public sealed record ProgressSnapshot(
    string Phase,
    string Result,
    bool Terminal,
    int Step,
    int TotalSteps,
    double Progress,
    int ServerBatchLimit,
    int ServerBatchPacingMs,
    double PaintEtaMs,
    double PaintElapsedMs,
    bool ReplicationPacingEnabled,
    int ReplicationPacingRequestedBatchLimit,
    int ReplicationPacingResolvedBatchLimit,
    int ReplicationPacingRequestedMs,
    int ReplicationPacingBatchLimit,
    int ReplicationPacingMs,
    string ReplicationPacingPressureLevel,
    int ReplicationPacingBackoffCount,
    double ReplicationPacingQueueDrainStrokesPerSec,
    double ReplicationPacingSendStrokesPerSec,
    double ReplicationPacingModelEtaMs,
    int ReplicationQueuedBatchCount,
    int ReplicationQueuedStrokeCount,
    int ReplicationMaxStrokesPerTick,
    double ReplicationEstimatedTicksToDrain,
    string ReplayCurrentPass = "",
    int ReplayCurrentPassStart = -1,
    int ReplayCurrentPassEnd = -1,
    int ReplayServerOffset = -1,
    int ReplayLocalOffset = -1,
    string ReplayProgressSource = "",
    int ReplayCurrentPassCompleted = -1,
    int ReplayCurrentPassTotal = -1,
    double ReplayCurrentPassEtaMs = -1.0,
    int LocalPackedQueueDrainCurrentQueue = -1,
    double LocalPackedQueueDrainStrokesPerSec = -1.0);

public sealed record HotkeySet(string Start, string Preview, string UnPreview, string Stop)
{
    public static HotkeySet From(AppSettings settings) =>
        new(settings.StartHotkey, settings.PreviewHotkey, settings.UnPreviewHotkey, settings.StopHotkey);

    public void ApplyTo(AppSettings settings)
    {
        settings.StartHotkey = Start;
        settings.PreviewHotkey = Preview;
        settings.UnPreviewHotkey = UnPreview;
        settings.StopHotkey = Stop;
    }

    public bool TryValidate(out string message)
    {
        message = "";
        var values = new[] { Start, Preview, UnPreview, Stop };
        foreach (var value in values)
        {
            if (!IsFunctionKey(value))
            {
                message = "Hotkeys must be F1 through F24.";
                return false;
            }
        }
        if (values.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
        {
            message = "Hotkeys must not be duplicated.";
            return false;
        }
        return true;
    }

    public static bool IsFunctionKey(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length >= 2 &&
               normalized[0] == 'F' &&
               int.TryParse(normalized[1..], out var number) &&
               number is >= 1 and <= 24;
    }

    public static string Normalize(string? value) => (value ?? "").Trim().ToUpperInvariant();
}
