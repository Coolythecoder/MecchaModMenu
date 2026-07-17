using System.Diagnostics;
using System.Text.Json;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.Controller;

public sealed class HostSession : IDisposable
{
    private const int DefaultPackedBatchLimit = 20;
    private const int DefaultPackedPacingMs = 50;
    private const int NativeCancelAdmissionRetryAttempts = 40;
    private static readonly TimeSpan NativeCancelAdmissionRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly string[] ResetKeys =
    [
        "paint.brush1SizeTexels",
        "paint.brush2SizeTexels",
        "paint.detailResolutionPercent",
        "paint.packedBatchLimit",
        "paint.packedBatchPacingMs",
        "paint.autoMaterial",
        "paint.metallic",
        "paint.roughness",
        "paint.frontRegionMode",
        "paint.sideRegionMode",
        "paint.backRegionMode",
        "paint.fillColor",
        "paint.fillMetallic",
        "paint.fillRoughness",
        "app.processName",
        "app.alwaysOnTop",
        "app.opacity",
        "app.themeColor",
        "app.startHotkey",
        "app.previewHotkey",
        "app.unpreviewHotkey",
        "app.stopHotkey"
    ];

    private enum PaintCancelState
    {
        None,
        PreDispatchPending,
        Sending,
        AcceptedAwaitingTerminal
    }

    public HostSession(string version)
    {
        Paths = new AppPaths(version);
        Paths.EnsureBaseDirectories();
        DiagnosticsState.EnsureInitialized(Paths, version);
        Store = new SettingsStore(Paths);
        Settings = Store.Load();
        PresetStore = new PaintPresetStore(Paths.RootDirectory);
        paintPresets = PresetStore.Load();
        ModuleData = new ModuleDataService(Paths.ModuleDataDirectory);
        ModuleSourceCatalog = ModuleCatalog.Scan(Paths);
        Modules = ModuleSourceCatalog;
        Log = new RuntimeLog(Paths);
        Runtime = new RuntimeBridgeService(Paths, Log);
    }

    public LocalizationCatalog Localization { get; } = LocalizationCatalog.Load();
    public AppPaths Paths { get; }
    public SettingsStore Store { get; }
    public PaintPresetStore PresetStore { get; }
    public ModuleDataService ModuleData { get; }
    public ModuleCatalogResult ModuleSourceCatalog { get; private set; }
    public ModuleCatalogResult Modules { get; private set; }
    public RuntimeLog Log { get; }
    public RuntimeBridgeService Runtime { get; }
    public AppSettings Settings { get; private set; }
    public bool PaintRunning { get; private set; }
    public string ModuleHostGeneration
    {
        get
        {
            lock (moduleDataGate)
                return moduleCatalogGeneration;
        }
    }
    private readonly SemaphoreSlim bridgeWarmupGate = new(1, 1);
    private readonly object paintStateGate = new();
    private DateTimeOffset nextBridgeWarmupAttempt;
    private DateTimeOffset currentPaintStartedAt = DateTimeOffset.MinValue;
    private bool finalProgressLogged;
    private bool currentProgressIsServerPaint;
    private string moduleCatalogGeneration = Guid.NewGuid().ToString("N");
    private bool nativePaintMayBeRunning;
    private PaintCancelState cancelState;
    private int nextPaintGeneration;
    private int activePaintGeneration;
    private int cancelPaintGeneration;
    // This becomes non-zero only immediately before the authenticated paint request begins.
    // A stop during process/bridge attachment must latch locally instead of asking native to
    // cancel a job which does not exist yet and then allowing the late send to proceed.
    private int paintRequestDispatchGeneration;
    private readonly object replayPassLogGate = new();
    private readonly HashSet<string> loggedReplayPasses = new(StringComparer.Ordinal);
    private readonly object paintStudioGate = new();
    private readonly object moduleDataGate = new();
    private readonly SemaphoreSlim moduleDataOperationSlots = new(8, 8);
    private readonly object moduleDataModuleSlotsGate = new();
    private readonly Dictionary<string, SemaphoreSlim> moduleDataModuleSlots =
        new(ModuleSdkV1.IdComparer);
    private int disposed;
    private IReadOnlyList<PaintPreset> paintPresets;
    private PaintSettings? paintSettingsUndo;
    private PaintCoverageSnapshot paintCoverage = new(
        Available: false,
        CoveragePercent: 0.0,
        EnabledSamples: 0,
        TotalSamples: 0,
        PlannedStrokes: 0,
        DetailSelected: 0,
        DetailBudget: 0,
        Result: "No paint analysis yet.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lock (moduleDataGate)
            moduleCatalogGeneration = Guid.NewGuid().ToString("N");

        // New operations fail the disposed check. Taking every permit drains
        // workers that were already admitted before volatile memory is cleared.
        for (var index = 0; index < 8; ++index)
            moduleDataOperationSlots.Wait();
        try
        {
            ModuleData.Dispose();
        }
        finally
        {
            moduleDataOperationSlots.Release(8);
        }
        GC.SuppressFinalize(this);
    }

    public async Task<UiSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var process = Runtime.FindGameProcess(Settings.GameProcessName);
        var ping = await Runtime.PingAsync(cancellationToken, RuntimeBridgeService.BridgeProbeTimeout);
        var progress = ReadCurrentProgressSnapshot(liveOnly: true);
        LogReplayPassTransition(progress);
        var bridgeReady = process is not null &&
            Runtime.IsConnected &&
            ping.Ok &&
            ping.Success &&
            (ping.ProcessId is null || ping.ProcessId == process.Id);
        return CreateSnapshot(
            process is null ? "waiting" : "attached",
            bridgeReady ? "connected" : "waiting",
            bridgeReady ? "ready" : "stopped",
            progress);
    }

    public async Task WarmupBridgeAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextBridgeWarmupAttempt)
            return;
        if (!await bridgeWarmupGate.WaitAsync(0, cancellationToken))
            return;
        try
        {
            using var process = Runtime.FindGameProcess(Settings.GameProcessName);
            if (process is null)
            {
                _ = await Runtime.EnsureReadyAsync(Settings.GameProcessName, cancellationToken);
                nextBridgeWarmupAttempt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
                return;
            }
            var ready = await Runtime.EnsureReadyAsync(process, cancellationToken);
            nextBridgeWarmupAttempt = ready
                ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2)
                : DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Bridge warmup failed: " + ex.Message);
            nextBridgeWarmupAttempt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        }
        finally
        {
            bridgeWarmupGate.Release();
        }
    }

    public HostCommandResult SavePaintPreset(string name)
    {
        try
        {
            var saved = PresetStore.Save(name, Settings.Paint);
            lock (paintStudioGate)
                paintPresets = PresetStore.Load();
            return new HostCommandResult(true, $"Paint Studio: saved preset '{saved.Name}'.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new HostCommandResult(false, "Paint Studio: " + ex.Message);
        }
    }

    public HostCommandResult ApplyPaintPreset(string name)
    {
        try
        {
            if (!PresetStore.TryGet(name, out var preset))
                return new HostCommandResult(false, "Paint Studio: preset was not found.");
            var previous = Clone(Settings);
            var next = Clone(Settings);
            next.Paint = PaintPresetStore.CloneAndClamp(preset.Paint);
            var result = CommitSettings(next, previous);
            if (!result.Success)
                return result;
            lock (paintStudioGate)
                paintSettingsUndo = PaintPresetStore.CloneAndClamp(previous.Paint);
            return new HostCommandResult(true, $"Paint Studio: applied preset '{preset.Name}'.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new HostCommandResult(false, "Paint Studio: " + ex.Message);
        }
    }

    public HostCommandResult DeletePaintPreset(string name)
    {
        try
        {
            if (!PresetStore.Delete(name))
                return new HostCommandResult(false, "Paint Studio: preset was not found.");
            lock (paintStudioGate)
                paintPresets = PresetStore.Load();
            return new HostCommandResult(true, "Paint Studio: preset deleted.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new HostCommandResult(false, "Paint Studio: " + ex.Message);
        }
    }

    public HostCommandResult UndoPaintSettings()
    {
        PaintSettings? undo;
        lock (paintStudioGate)
            undo = paintSettingsUndo is null ? null : PaintPresetStore.CloneAndClamp(paintSettingsUndo);
        if (undo is null)
            return new HostCommandResult(false, "Paint Studio: no preset change to undo.");

        var previous = Clone(Settings);
        var next = Clone(Settings);
        next.Paint = undo;
        var result = CommitSettings(next, previous);
        if (!result.Success)
            return result;
        lock (paintStudioGate)
            paintSettingsUndo = PaintPresetStore.CloneAndClamp(previous.Paint);
        return new HostCommandResult(true, "Paint Studio: restored the previous paint settings.");
    }

    public HostCommandResult UpdateSetting(string key, JsonElement value)
    {
        return UpdateSettings([new SettingChange(key, value)]);
    }

    public HostCommandResult UpdateSettings(IEnumerable<SettingChange> changes)
    {
        var previous = Clone(Settings);
        try
        {
            var next = Clone(Settings);
            foreach (var change in changes)
                ApplySetting(next, change.Key, change.Value);
            return CommitSettings(next, previous);
        }
        catch (Exception ex)
        {
            Settings = previous;
            return new HostCommandResult(false, ex.Message);
        }
    }

    public HostCommandResult ResetSetting(string key)
    {
        var previous = Clone(Settings);
        try
        {
            var next = Clone(Settings);
            ResetOne(next, new AppSettings(), key);
            return CommitSettings(next, previous);
        }
        catch (Exception ex)
        {
            Settings = previous;
            return new HostCommandResult(false, ex.Message);
        }
    }

    public HostCommandResult ResetSection(string section)
    {
        var previous = Clone(Settings);
        var next = Clone(Settings);
        var defaults = new AppSettings();
        switch (section.Trim().ToLowerInvariant())
        {
            case "runtime":
                return new HostCommandResult(true);
            case "paint.geometry":
            case "geometry":
                next.Paint.Brush1SizeTexels = defaults.Paint.Brush1SizeTexels;
                next.Paint.Brush2SizeTexels = defaults.Paint.Brush2SizeTexels;
                next.Paint.DetailResolutionPercent = defaults.Paint.DetailResolutionPercent;
                next.Paint.CoverageStepTexels = defaults.Paint.Brush2SizeTexels;
                next.Paint.PackedBatchLimit = defaults.Paint.PackedBatchLimit;
                next.Paint.PackedBatchPacingMs = defaults.Paint.PackedBatchPacingMs;
                break;
            case "paint.material":
            case "material":
                next.Paint.AutoMaterial = defaults.Paint.AutoMaterial;
                next.Paint.Metallic = defaults.Paint.Metallic;
                next.Paint.Roughness = defaults.Paint.Roughness;
                break;
            case "regions":
                next.Paint.FrontRegionMode = defaults.Paint.FrontRegionMode;
                next.Paint.SideRegionMode = defaults.Paint.SideRegionMode;
                next.Paint.BackRegionMode = defaults.Paint.BackRegionMode;
                break;
            case "fill":
            case "fill.material":
                next.Paint.FillColor = defaults.Paint.FillColor;
                next.Paint.FillMetallic = defaults.Paint.FillMetallic;
                next.Paint.FillRoughness = defaults.Paint.FillRoughness;
                break;
            case "app":
                next.GameProcessName = defaults.GameProcessName;
                next.AlwaysOnTop = defaults.AlwaysOnTop;
                next.Opacity = defaults.Opacity;
                next.ThemeColor = defaults.ThemeColor;
                next.StartHotkey = defaults.StartHotkey;
                next.PreviewHotkey = defaults.PreviewHotkey;
                next.UnPreviewHotkey = defaults.UnPreviewHotkey;
                next.StopHotkey = defaults.StopHotkey;
                break;
            default:
                return new HostCommandResult(false, $"Unknown section: {section}");
        }
        return CommitSettings(next, previous);
    }

    public HostCommandResult ResetAllSettings()
    {
        var defaults = new AppSettings
        {
            Language = Settings.Language,
            PanelX = Settings.PanelX,
            PanelY = Settings.PanelY,
            PanelWidth = Settings.PanelWidth,
            PanelHeight = Settings.PanelHeight
        };
        Settings = defaults;
        Store.Save(Settings);
        return new HostCommandResult(true);
    }

    public void SetWindowSnapshot(double width, double height, double x, double y)
    {
        if (width > 0)
            Settings.PanelWidth = width;
        if (height > 0)
            Settings.PanelHeight = height;
        Settings.PanelX = x;
        Settings.PanelY = y;
        Settings = SettingsStore.Clamp(Settings);
        Store.Save(Settings);
    }

    public async Task<HostCommandResult> RunPaintAsync(bool previewOnly, bool unpreviewOnly, CancellationToken cancellationToken = default)
    {
        int runGeneration;
        lock (paintStateGate)
        {
            if (PaintRunning || nativePaintMayBeRunning)
            {
                const string alreadyRunning = "Paint: already running.";
                Log.Warn(alreadyRunning);
                return new HostCommandResult(false, alreadyRunning);
            }
            PaintRunning = true;
            runGeneration = ++nextPaintGeneration;
            activePaintGeneration = runGeneration;
            cancelState = PaintCancelState.None;
            cancelPaintGeneration = 0;
            paintRequestDispatchGeneration = 0;
        }
        currentPaintStartedAt = DateTimeOffset.UtcNow;
        currentProgressIsServerPaint = !previewOnly && !unpreviewOnly;
        finalProgressLogged = false;
        ResetReplayPassLog();
        TryDeleteProgressSnapshot();
        try
        {
            using var process = Runtime.FindGameProcess(Settings.GameProcessName);
            if (process is null)
            {
                Log.Warn("Game process not found.");
                return new HostCommandResult(false, "Game process not found.");
            }
            var ready = await Runtime.EnsureReadyAsync(process, cancellationToken);
            if (IsPreDispatchCancellationPending(runGeneration))
            {
                const string canceledBeforeDispatch = "Paint: canceled.";
                Log.Info(canceledBeforeDispatch);
                return new HostCommandResult(false, canceledBeforeDispatch);
            }
            if (!ready)
                return new HostCommandResult(false, "Bridge is not connected.");
            var startedMessage = previewOnly ? "Preview: started." : (unpreviewOnly ? "UnPreview: started." : "Paint: started.");
            Log.Info(startedMessage);
            var payload = BridgePayloadBuilder.BuildPaintPayload(
                Settings,
                process.Id,
                Settings.GameProcessName,
                new PaintRequestOptions(previewOnly, unpreviewOnly, BuildFeatures.ResearchArtifactsEnabled));
            if (!TryBeginPaintDispatch(runGeneration))
            {
                const string canceledBeforeDispatch = "Paint: canceled.";
                Log.Info(canceledBeforeDispatch);
                return new HostCommandResult(false, canceledBeforeDispatch);
            }
            var response = await Runtime.SendPaintAsync(payload, cancellationToken);
            var message = FriendlyBridgeMessage(response.Message.Length > 0 ? response.Message : response.Stage);
            if (!previewOnly && !unpreviewOnly)
                CapturePaintCoverage(response);
            if (response.Success)
            {
                message = DescribePaintCompletion(message, serverPaint: !previewOnly && !unpreviewOnly);
                lock (paintStateGate)
                {
                    if (activePaintGeneration == runGeneration)
                        nativePaintMayBeRunning = false;
                }
                LogFinalProgressOnce();
                Log.Info(message);
                return new HostCommandResult(true, message);
            }
            if (IsPaintCancellationMessage(message))
            {
                lock (paintStateGate)
                {
                    if (activePaintGeneration == runGeneration)
                        nativePaintMayBeRunning = false;
                }
                LogFinalProgressOnce();
                Log.Info(message);
                return new HostCommandResult(false, message);
            }
            if (IsGuardWarning(message))
            {
                lock (paintStateGate)
                {
                    if (activePaintGeneration == runGeneration)
                        nativePaintMayBeRunning = message == "Paint: already running.";
                }
                Log.Warn(message);
                return new HostCommandResult(false, message);
            }
            lock (paintStateGate)
            {
                if (activePaintGeneration == runGeneration)
                    nativePaintMayBeRunning = false;
            }
            LogFailureProgressOnce();
            Log.Error(message);
            return new HostCommandResult(false, message);
        }
        finally
        {
            lock (paintStateGate)
            {
                if (activePaintGeneration == runGeneration)
                {
                    PaintRunning = false;
                    activePaintGeneration = 0;
                    cancelState = PaintCancelState.None;
                    cancelPaintGeneration = 0;
                    paintRequestDispatchGeneration = 0;
                }
            }
            currentProgressIsServerPaint = false;
        }
    }

    public async Task<HostCommandResult> StopPaintAsync(CancellationToken cancellationToken = default)
    {
        int requestedGeneration;
        bool controllerOwnedPaint;
        lock (paintStateGate)
        {
            if (cancelState == PaintCancelState.Sending)
                return new HostCommandResult(true, "Paint: cancel requested.");
            if (cancelState == PaintCancelState.PreDispatchPending)
                return new HostCommandResult(true, "Paint: cancel requested.");
            if (cancelState == PaintCancelState.AcceptedAwaitingTerminal)
                return new HostCommandResult(true, "Paint: cancel requested.");
            if (!PaintRunning && !nativePaintMayBeRunning)
            {
                const string noActivePaint = "Paint: no active paint to cancel.";
                Log.Warn(noActivePaint);
                return new HostCommandResult(false, noActivePaint);
            }
            controllerOwnedPaint = PaintRunning;
            requestedGeneration = controllerOwnedPaint ? activePaintGeneration : 0;
            if (controllerOwnedPaint && paintRequestDispatchGeneration != requestedGeneration)
            {
                cancelState = PaintCancelState.PreDispatchPending;
                cancelPaintGeneration = requestedGeneration;
                const string canceledBeforeDispatch = "Paint: canceled.";
                Log.Info(canceledBeforeDispatch);
                return new HostCommandResult(true, canceledBeforeDispatch);
            }
            cancelState = PaintCancelState.Sending;
            cancelPaintGeneration = requestedGeneration;
        }
        BridgeReply response;
        try
        {
            response = await Runtime.CancelPaintAsync(cancellationToken);
            for (var attempt = 0;
                 attempt < NativeCancelAdmissionRetryAttempts &&
                 ShouldRetryCancelAfterEarlyAcknowledgement(requestedGeneration, controllerOwnedPaint, response);
                 ++attempt)
            {
                await Task.Delay(NativeCancelAdmissionRetryDelay, cancellationToken);
                response = await Runtime.CancelPaintAsync(cancellationToken);
            }
        }
        catch
        {
            ClearCancelStateIfOwned(requestedGeneration);
            throw;
        }
        var message = FriendlyBridgeMessage(response.Message.Length > 0 ? response.Message : response.Stage);
        var cancelledJobs = CancelledPaintJobCount(response);
        var nativeCancellationLatched = NativePaintRequestCancellationLatched(response);
        lock (paintStateGate)
        {
            // The original command can terminalize while the independent cancel request is in
            // flight. Never revive a completed generation with a late acknowledgement.
            var sameControllerPaint = controllerOwnedPaint &&
                                      PaintRunning &&
                                      activePaintGeneration == requestedGeneration;
            var ownsCancelState = cancelState == PaintCancelState.Sending &&
                                  cancelPaintGeneration == requestedGeneration;
            if (!ownsCancelState)
                return new HostCommandResult(response.Success, "Paint: cancellation completed after the paint terminalized.");

            if (!response.Success)
            {
                cancelState = PaintCancelState.None;
                cancelPaintGeneration = 0;
                if (IsGuardWarning(message))
                    Log.Warn(message);
                else
                    Log.Error("Paint: cancel failed: " + message);
                return new HostCommandResult(false, message);
            }

            if (cancelledJobs == 0 && !nativeCancellationLatched)
            {
                cancelState = PaintCancelState.None;
                cancelPaintGeneration = 0;
                nativePaintMayBeRunning = false;
                if (sameControllerPaint && paintRequestDispatchGeneration == requestedGeneration)
                {
                    const string nativeAdmissionMissed = "Paint: cancel was not observed by the native paint request. Retry stop.";
                    Log.Warn(nativeAdmissionMissed);
                    return new HostCommandResult(false, nativeAdmissionMissed);
                }
                const string noActivePaint = "Paint: no active paint to cancel.";
                Log.Warn(noActivePaint);
                return new HostCommandResult(false, noActivePaint);
            }

            if (cancelledJobs is null)
            {
                cancelState = PaintCancelState.None;
                cancelPaintGeneration = 0;
                const string malformed = "Paint: cancel response did not include native job counts.";
                Log.Error(malformed);
                return new HostCommandResult(false, malformed);
            }

            if (!sameControllerPaint)
            {
                // A foreign native job has no controller-owned terminal request to clear this
                // state. Its acknowledgement is terminal from the UI's perspective.
                cancelState = PaintCancelState.None;
                cancelPaintGeneration = 0;
                nativePaintMayBeRunning = false;
                const string completed = "Paint: cancel requested.";
                Log.Info(completed);
                return new HostCommandResult(true, completed);
            }

            // The original request remains active only long enough to observe the bounded local
            // queue tail reaching zero.
            cancelState = PaintCancelState.AcceptedAwaitingTerminal;
            nativePaintMayBeRunning = true;
            const string pending = "Paint: cancel requested.";
            Log.Info(pending);
            return new HostCommandResult(true, pending);
        }
    }

    private bool ShouldRetryCancelAfterEarlyAcknowledgement(
        int generation,
        bool controllerOwnedPaint,
        BridgeReply response)
    {
        if (!controllerOwnedPaint ||
            !response.Success ||
            CancelledPaintJobCount(response) is not 0 ||
            NativePaintRequestCancellationLatched(response))
        {
            return false;
        }
        lock (paintStateGate)
        {
            return PaintRunning &&
                   activePaintGeneration == generation &&
                   paintRequestDispatchGeneration == generation &&
                   cancelState == PaintCancelState.Sending &&
                   cancelPaintGeneration == generation;
        }
    }

    private void ClearCancelStateIfOwned(int generation)
    {
        lock (paintStateGate)
        {
            if (cancelState == PaintCancelState.Sending && cancelPaintGeneration == generation)
            {
                cancelState = PaintCancelState.None;
                cancelPaintGeneration = 0;
            }
        }
    }

    private bool TryBeginPaintDispatch(int generation)
    {
        lock (paintStateGate)
        {
            if (!PaintRunning || activePaintGeneration != generation ||
                IsPreDispatchCancellationPendingLocked(generation))
            {
                return false;
            }
            paintRequestDispatchGeneration = generation;
            return true;
        }
    }

    private bool IsPreDispatchCancellationPending(int generation)
    {
        lock (paintStateGate)
            return IsPreDispatchCancellationPendingLocked(generation);
    }

    private bool IsPreDispatchCancellationPendingLocked(int generation) =>
        cancelState == PaintCancelState.PreDispatchPending &&
        cancelPaintGeneration == generation;

    public static int? CancelledPaintJobCount(BridgeReply response)
    {
        if (string.IsNullOrWhiteSpace(response.Raw))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(response.Raw);
            if (!doc.RootElement.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var active = Int(metadata, "cancelled_active_paint_jobs", 0);
            var queued = Int(metadata, "cancelled_queued_paint_jobs", 0);
            return Math.Max(0, active) + Math.Max(0, queued);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Native reports this when cancel raced with admission: there was not yet a queued/executing
    /// job to count, but the current paint request owns a cancellation latch and will terminalize
    /// before it can dispatch work.
    /// </summary>
    public static bool NativePaintRequestCancellationLatched(BridgeReply response)
    {
        if (string.IsNullOrWhiteSpace(response.Raw))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(response.Raw);
            return doc.RootElement.TryGetProperty("metadata", out var metadata) &&
                   metadata.ValueKind == JsonValueKind.Object &&
                   metadata.TryGetProperty("cancel_latched_paint_request", out var latched) &&
                   latched.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                   latched.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    public void OpenLogs()
    {
        Directory.CreateDirectory(Paths.DiagnosticsDirectory);
        Process.Start(new ProcessStartInfo(Paths.VersionRoot) { UseShellExecute = true });
    }

    public string ClipboardLogText()
    {
        if (!currentProgressIsServerPaint)
            return Log.Text;
        var progress = ReadCurrentProgressSnapshot(liveOnly: false);
        if (progress is null)
            return Log.Text;
        var line = FormatProgressLogLine(progress);
        if (line.Length == 0)
            return Log.Text;
        return string.IsNullOrWhiteSpace(Log.Text)
            ? line
            : Log.Text.TrimEnd() + Environment.NewLine + line;
    }

    public async Task ShutdownBridgeAsync(CancellationToken cancellationToken = default)
    {
        if (!Runtime.HasActiveBridgeInstance)
            return;
        var shutdown = await Runtime.ShutdownAsync(cancellationToken);
        if (!shutdown.Ok || !shutdown.Success)
        {
            var detail = string.IsNullOrWhiteSpace(shutdown.Message) ? shutdown.Stage : shutdown.Message;
            Log.Warn("Bridge shutdown was not confirmed: " + detail);
        }
    }

    public HostCommandResult ReloadModules()
    {
        var sourceCatalog = ModuleCatalog.Scan(Paths);
        lock (moduleDataGate)
        {
            ModuleSourceCatalog = sourceCatalog;
            Modules = sourceCatalog;
            moduleCatalogGeneration = Guid.NewGuid().ToString("N");
        }
        foreach (var diagnostic in sourceCatalog.Diagnostics)
            Log.Warn($"Module {diagnostic.ModuleId ?? "catalog"}: {diagnostic.Message}");
        return new HostCommandResult(
            true,
            $"Modules: loaded {sourceCatalog.Modules.Count}; rejected {sourceCatalog.Diagnostics.Count}.");
    }

    public string BeginModuleHostGeneration()
    {
        lock (moduleDataGate)
        {
            moduleCatalogGeneration = Guid.NewGuid().ToString("N");
            return moduleCatalogGeneration;
        }
    }

    public void ApplyHostedModules(
        IEnumerable<ModuleDescriptor> modules,
        IEnumerable<ModuleCatalogDiagnostic> stagingDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(stagingDiagnostics);
        var accepted = modules.ToArray();
        var diagnostics = ModuleSourceCatalog.Diagnostics.Concat(stagingDiagnostics).ToArray();
        lock (moduleDataGate)
        {
            Modules = new ModuleCatalogResult(
                Array.AsReadOnly(accepted),
                Array.AsReadOnly(diagnostics));
        }
    }

    public ModuleDataCommandResult ExecuteModuleData(
        string moduleId,
        string generation,
        string operation,
        JsonElement payload) =>
        ExecuteModuleDataAsync(moduleId, generation, operation, payload).GetAwaiter().GetResult();

    public async Task<ModuleDataCommandResult> ExecuteModuleDataAsync(
        string moduleId,
        string generation,
        string operation,
        JsonElement payload)
    {
        if (Volatile.Read(ref disposed) != 0)
            return ModuleDataFailure("module_unavailable", "The module host is shutting down.");
        if (!ModuleSdkV1.IsValidId(moduleId))
            return ModuleDataFailure("module_unavailable", "The module is not currently hosted.");
        var moduleSlot = ModuleDataModuleSlot(moduleId);
        if (!await moduleSlot.WaitAsync(0).ConfigureAwait(false))
            return ModuleDataFailure("storage_busy", "This module has too many data requests in flight; retry the request.");
        try
        {
            if (!await moduleDataOperationSlots.WaitAsync(0).ConfigureAwait(false))
                return ModuleDataFailure("storage_busy", "Too many module data requests are in flight; retry the request.");
            try
            {
                return await ExecuteAdmittedModuleDataAsync(
                    moduleId,
                    generation,
                    operation,
                    payload).ConfigureAwait(false);
            }
            finally
            {
                moduleDataOperationSlots.Release();
            }
        }
        finally
        {
            moduleSlot.Release();
        }
    }

    private async Task<ModuleDataCommandResult> ExecuteAdmittedModuleDataAsync(
        string moduleId,
        string generation,
        string operation,
        JsonElement payload)
    {
        if (Volatile.Read(ref disposed) != 0)
            return ModuleDataFailure("module_unavailable", "The module host is shutting down.");
        var authorization = CheckModuleDataAuthorization(moduleId, generation, operation, out var module);
        if (authorization is not null)
            return authorization;
        var ownedPayload = payload.Clone();

        try
        {
            var canonicalId = module!.Id;
            Action<Action> commit = mutation =>
            {
                lock (moduleDataGate)
                {
                    var commitAuthorization = CheckModuleDataAuthorization(
                        canonicalId,
                        generation,
                        operation,
                        out _);
                    if (commitAuthorization is not null)
                    {
                        throw new ModuleDataException(
                            commitAuthorization.Code,
                            commitAuthorization.Message);
                    }
                    mutation();
                }
            };
            var data = await Task.Run(() => ExecuteAuthorizedModuleData(
                canonicalId,
                operation,
                ownedPayload,
                commit)).ConfigureAwait(false);
            var finalAuthorization = CheckModuleDataAuthorization(
                canonicalId,
                generation,
                operation,
                out var currentModule);
            if (finalAuthorization is not null)
                return finalAuthorization;
            return new ModuleDataCommandResult(
                true,
                "",
                "",
                SanitizeModuleDataWriteResult(operation, data, currentModule!));
        }
        catch (ModuleDataException ex)
        {
            return ModuleDataFailure(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            return ModuleDataFailure("invalid_payload", "The module data payload is invalid.");
        }
    }

    private SemaphoreSlim ModuleDataModuleSlot(string moduleId)
    {
        lock (moduleDataModuleSlotsGate)
        {
            if (!moduleDataModuleSlots.TryGetValue(moduleId, out var slot))
            {
                slot = new SemaphoreSlim(4, 4);
                moduleDataModuleSlots[moduleId] = slot;
            }
            return slot;
        }
    }

    private object ExecuteAuthorizedModuleData(
        string moduleId,
        string operation,
        JsonElement payload,
        Action<Action> commit)
    {
        if (operation is "storage.list" or "memory.list")
            ValidateEmptyDataPayload(payload);
        return operation switch
        {
            "storage.get" => ModuleData.StorageGet(moduleId, RequiredDataKey(payload, valueRequired: false)),
            "storage.set" => ModuleData.StorageSet(
                moduleId,
                RequiredDataKey(payload, valueRequired: true),
                payload.GetProperty("value"),
                commit),
            "storage.delete" => ModuleData.StorageDelete(
                moduleId,
                RequiredDataKey(payload, valueRequired: false),
                commit),
            "storage.list" => ModuleData.StorageList(moduleId),
            "memory.get" => ModuleData.MemoryGet(moduleId, RequiredDataKey(payload, valueRequired: false)),
            "memory.set" => ModuleData.MemorySet(
                moduleId,
                RequiredDataKey(payload, valueRequired: true),
                payload.GetProperty("value"),
                commit),
            "memory.delete" => ModuleData.MemoryDelete(
                moduleId,
                RequiredDataKey(payload, valueRequired: false),
                commit),
            "memory.list" => ModuleData.MemoryList(moduleId),
            _ => throw new ModuleDataException("unsupported_command", "Unsupported module data command.")
        };
    }

    private ModuleDataCommandResult? CheckModuleDataAuthorization(
        string moduleId,
        string generation,
        string operation,
        out ModuleDescriptor? module)
    {
        lock (moduleDataGate)
        {
            module = null;
            if (Volatile.Read(ref disposed) != 0)
                return ModuleDataFailure("module_unavailable", "The module host is shutting down.");
            if (!string.Equals(generation, moduleCatalogGeneration, StringComparison.Ordinal))
                return ModuleDataFailure("stale_generation", "The module host generation is no longer active.");
            module = Modules.Modules.FirstOrDefault(candidate =>
                ModuleSdkV1.IdComparer.Equals(candidate.Id, moduleId));
            if (module is null)
                return ModuleDataFailure("module_unavailable", "The module is not currently hosted.");
            var permission = ModuleSdkV1.RequiredPermissionForDataCommand(operation);
            if (permission is null)
                return ModuleDataFailure("unsupported_command", "Unsupported module data command.");
            return module.Permissions.Contains(permission, StringComparer.Ordinal)
                ? null
                : ModuleDataFailure("permission_denied", "The module does not have permission for this command.");
        }
    }

    private static object SanitizeModuleDataWriteResult(
        string operation,
        object data,
        ModuleDescriptor module)
    {
        if (!operation.EndsWith(".set", StringComparison.Ordinal) &&
            !operation.EndsWith(".delete", StringComparison.Ordinal))
        {
            return data;
        }
        var readPermission = operation.StartsWith("storage.", StringComparison.Ordinal)
            ? ModuleSdkV1.StorageReadPermission
            : ModuleSdkV1.MemoryReadPermission;
        if (module.Permissions.Contains(readPermission, StringComparer.Ordinal))
            return data;
        return data switch
        {
            ModuleDataSetResult result => new
            {
                result.Key,
                result.Stored,
                result.SizeBytes,
                result.QuotaBytes
            },
            ModuleDataDeleteResult result => new
            {
                result.Key,
                Deleted = true,
                result.QuotaBytes
            },
            _ => data
        };
    }

    private static string RequiredDataKey(JsonElement payload, bool valueRequired)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("payload must be an object.");
        var allowed = valueRequired
            ? new HashSet<string>(["key", "value"], StringComparer.Ordinal)
            : new HashSet<string>(["key"], StringComparer.Ordinal);
        if (payload.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
            !payload.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String ||
            (valueRequired && !payload.TryGetProperty("value", out _)))
        {
            throw new ArgumentException("payload fields are invalid.");
        }
        return keyValue.GetString() ?? "";
    }

    private static void ValidateEmptyDataPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any())
            throw new ArgumentException("list payload must be an empty object.");
    }

    private static ModuleDataCommandResult ModuleDataFailure(string code, string message) =>
        new(false, code, message, null);

    public void OpenModulesDirectory()
    {
        Directory.CreateDirectory(Paths.ModulesDirectory);
        Process.Start(new ProcessStartInfo(Paths.ModulesDirectory) { UseShellExecute = true });
    }

    public static PaintCoverageSnapshot? ParsePaintCoverage(BridgeReply response)
    {
        if (string.IsNullOrWhiteSpace(response.Raw))
            return null;
        try
        {
            using var document = JsonDocument.Parse(response.Raw);
            if (!document.RootElement.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var totalSamples = Math.Max(0, Int(metadata, "planner_samples_total", 0));
            var enabledSamples = Math.Clamp(
                Int(metadata, "planner_samples_enabled", 0),
                0,
                Math.Max(totalSamples, 0));
            var plannedStrokes = Math.Max(
                0,
                Int(metadata, "planner_strokes_total", Int(metadata, "replay_strokes_total", 0)));
            var detailSelected = Math.Max(
                0,
                Int(metadata, "detail_selected_samples", Int(metadata, "replay_strokes_detail_refinement", 0)));
            var detailBudget = Math.Max(0, Int(metadata, "detail_budget", 0));
            if (totalSamples == 0 && plannedStrokes == 0 && detailBudget == 0)
                return null;
            var percent = totalSamples > 0
                ? Math.Round(enabledSamples * 100.0 / totalSamples, 1)
                : 0.0;
            var result = response.Success
                ? "Complete"
                : (string.IsNullOrWhiteSpace(response.Message) ? "Failed" : response.Message.Trim());
            return new PaintCoverageSnapshot(
                Available: true,
                CoveragePercent: percent,
                EnabledSamples: enabledSamples,
                TotalSamples: totalSamples,
                PlannedStrokes: plannedStrokes,
                DetailSelected: detailSelected,
                DetailBudget: detailBudget,
                Result: result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void CapturePaintCoverage(BridgeReply response)
    {
        var coverage = ParsePaintCoverage(response);
        if (coverage is null)
            return;
        lock (paintStudioGate)
            paintCoverage = coverage;
    }

    private HostCommandResult CommitSettings(AppSettings next, AppSettings previous)
    {
        next = SettingsStore.Clamp(next);
        var hotkeys = HotkeySet.From(next);
        if (!hotkeys.TryValidate(out var message))
        {
            Settings = previous;
            return new HostCommandResult(false, message);
        }
        Settings = next;
        Store.Save(Settings);
        return new HostCommandResult(true);
    }

    public static string FriendlyBridgeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "";

        var value = message.Trim();
        var lower = value.ToLowerInvariant();

        if (lower.Contains("already running"))
            return "Paint: already running.";
        if (lower is "mesh_first_paint_done" ||
            lower is "paint completed." ||
            lower is "paint completed" ||
            lower is "paint: completed." ||
            lower is "paint: completed" ||
            lower.Contains("mesh-first paint completed") ||
            lower.Contains("sent through serverpaintbatch"))
            return "Paint: completed.";
        if (lower.Contains("paint cancellation") ||
            lower.Contains("paint canceled") ||
            lower.Contains("mesh-first paint cancelled") ||
            lower.Contains("mesh-first paint canceled"))
            return "Paint: canceled.";
        if (lower is "mesh_preview_done" || lower.Contains("local preview material texture imported"))
            return "Preview: applied.";
        if (lower is "mesh_unpreview_done" || lower.Contains("local preview material texture restored"))
            return "Preview: restored.";
        if (lower is "mesh_preview_failed" || lower.Contains("local preview material texture import failed"))
            return "Preview: failed.";
        if (lower is "mesh_unpreview_failed" || lower.Contains("local preview material restore failed"))
            return "Preview: restore failed.";
        if (lower is "mesh_unpreview_snapshot_unavailable" || lower.Contains("no local preview snapshot is available"))
            return "Preview: no active preview to restore.";
        if (lower is "mesh_unpreview_component_mismatch")
            return "The saved preview belongs to a different mesh.";
        if (lower.Contains("strokes were submitted, but local rendering failed"))
            return "Paint: strokes were sent, but local rendering failed. Do not retry automatically.";
        if (lower is "mesh_local_visual_sync_failed")
            return "Paint: strokes were sent, but local rendering failed. Do not retry automatically.";
        if (lower is "mesh_paint_context_changed" || lower.Contains("paint_context_changed"))
            return "Paint: stopped because the game paint component changed.";
        if (lower.Contains("game paint component is no longer available"))
            return "Paint: stopped because the game paint component is no longer available.";
        if (lower.Contains("local pawn is no longer available"))
            return "Paint: stopped because the local pawn is no longer available.";
        if (lower.Contains("local pawn changed"))
            return "Paint: stopped because the local pawn changed.";
        if (lower.Contains("paint_component_unavailable"))
            return "Paint: stopped because the game paint component is unavailable.";
        if (lower.Contains("unsafe color-transfer candidates"))
            return "Paint: blocked because the current mesh sampling was unsafe.";
        if (lower is "mesh_server_packed_batch_unavailable" || lower.Contains("serverpackedpaintbatch is unavailable"))
            return "Packed multiplayer paint sync is unavailable.";
        if (lower is "mesh_server_packed_batch_incompatible" || lower.Contains("serverpackedpaintbatch requires"))
            return "Paint failed because strokes could not be packed.";
        if (lower is "mesh_server_packed_source_id_unavailable" || lower.Contains("source id is unavailable"))
            return "Packed multiplayer paint source id is unavailable.";
        if (lower is "mesh_internal_no_resend_resolver_failed" ||
            (lower.Contains("internal no-resend route") && lower.Contains("failed")))
            return "Paint: this game build is not supported.";
        if (lower is "mesh_server_batch_failed")
            return "Paint: failed while sending strokes.";

        return value
            .Replace("mesh-first ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("mesh first ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("mesh_first_", "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A server-paint terminal reply proves the host's local queue reached its terminal state;
    /// it cannot prove a joining client's game-owned receiver has presented its final pixels.
    /// </summary>
    public static string DescribePaintCompletion(string message, bool serverPaint) =>
        serverPaint && string.Equals(message, "Paint: completed.", StringComparison.Ordinal)
            ? "Paint: completed locally; joined clients may still be rendering."
            : message;

    private static bool IsPaintCancellationMessage(string message) =>
        message == "Paint: canceled.";

    private static bool IsGuardWarning(string message) =>
        message.Equals("Paint: already running.", StringComparison.OrdinalIgnoreCase) ||
        message.Equals("Paint: no active paint to cancel.", StringComparison.OrdinalIgnoreCase) ||
        message.Equals("Preview: no active preview to restore.", StringComparison.OrdinalIgnoreCase);

    private static AppSettings Clone(AppSettings source) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source)) ?? new AppSettings();

    private UiSnapshot CreateSnapshot(string process, string bridge, string service, ProgressSnapshot? progress)
    {
        var defaults = new AppSettings();
        var percent = 0.0;
        var progressSource = "";
        var pass = "-";
        var passProgress = "-";
        var passEta = "-";
        var eta = "-";
        var elapsed = "-";
        var batch = "-";
        var pacing = "-";
        var queue = "-";
        if (progress is not null)
        {
            percent = progress.TotalSteps > 0
                ? Math.Clamp(progress.Step * 100.0 / progress.TotalSteps, 0.0, 100.0)
                : Math.Clamp(progress.Progress * 100.0, 0.0, 100.0);
            progressSource = progress.ReplayProgressSource;
            pass = ReplayPassLabel(progress.ReplayCurrentPass);
            passProgress = FormatReplayPassProgress(progress);
            passEta = FormatDuration(progress.ReplayCurrentPassEtaMs);
            eta = FormatEta(progress);
            elapsed = FormatDuration(progress.PaintElapsedMs);
            batch = FormatBatch(progress);
            pacing = FormatPacing(progress);
            queue = FormatQueue(progress);
        }

        IReadOnlyList<PaintPreset> presets;
        bool canUndoPaintSettings;
        PaintCoverageSnapshot coverage;
        lock (paintStudioGate)
        {
            presets = paintPresets.ToArray();
            canUndoPaintSettings = paintSettingsUndo is not null;
            coverage = paintCoverage;
        }
        var modsAvailable = string.Equals(service, "ready", StringComparison.Ordinal);
        ModuleCatalogResult moduleCatalog;
        string moduleHostGeneration;
        lock (moduleDataGate)
        {
            moduleCatalog = Modules;
            moduleHostGeneration = moduleCatalogGeneration;
        }
        var externalModules = moduleCatalog.Modules.Select(module => new ExternalModuleSnapshot(
            module.Id,
            module.Name,
            module.Version,
            module.Description,
            ModuleEntryUrl(module, moduleHostGeneration),
            module.Permissions)).ToArray();
        var moduleDiagnostics = moduleCatalog.Diagnostics.Select(diagnostic => new ModuleDiagnosticSnapshot(
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.ModuleId ?? "")).ToArray();

        return new UiSnapshot(
            VersionInfo.Current,
            Settings.Language,
            new RuntimeSnapshot(process, bridge, service, percent, progressSource, pass, passProgress, passEta, eta, elapsed, batch, pacing, queue, Log.Text, PaintRunning, progress is not null, DiagnosticsState.Snapshot(Paths)),
            new ModsSnapshot(
                new AutoPaintModSnapshot(modsAvailable, PaintRunning),
                new PaintStudioModSnapshot(Available: true)),
            new PaintStudioSnapshot(
                Settings.Paint.DetailResolutionPercent,
                presets.Select(preset => new PaintPresetSnapshot(
                    preset.Name,
                    preset.UpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))).ToArray(),
                canUndoPaintSettings,
                coverage),
            externalModules,
            moduleDiagnostics,
            ToSnapshot(Settings),
            ToSnapshot(defaults),
            BuildResetSnapshot(Settings, defaults),
            LocalizationCatalog.SupportedLocales.Select(locale => new LocaleSnapshot(locale.Code, locale.NativeName)).ToArray(),
            Localization.All);
    }

    public string ModuleEntryUrl(ModuleDescriptor module)
    {
        lock (moduleDataGate)
            return ModuleEntryUrl(module, moduleCatalogGeneration);
    }

    private static string ModuleEntryUrl(ModuleDescriptor module, string generation)
    {
        var encodedEntry = string.Join(
            '/',
            module.Entry.Split('/').Select(Uri.EscapeDataString));
        return $"https://{ModuleSdkV1.VirtualHostName(module.Id)}/{generation}/{encodedEntry}";
    }

    private static SettingsSnapshot ToSnapshot(AppSettings settings)
    {
        var paint = settings.Paint;
        return new SettingsSnapshot(
            new PaintSnapshot(
                paint.Brush1SizeTexels,
                paint.Brush2SizeTexels,
                paint.DetailResolutionPercent,
                paint.PackedBatchLimit,
                paint.PackedBatchPacingMs,
                paint.AutoMaterial,
                paint.Metallic,
                paint.Roughness,
                SettingsStore.RegionModeText(paint.FrontRegionMode),
                SettingsStore.RegionModeText(paint.SideRegionMode),
                SettingsStore.RegionModeText(paint.BackRegionMode),
                paint.FillColor.ToHex(),
                paint.FillMetallic,
                paint.FillRoughness,
                paint.UsesFill),
            new AppSnapshot(
                settings.GameProcessName,
                settings.AlwaysOnTop,
                settings.Opacity,
                settings.ThemeColor.ToHex(),
                settings.StartHotkey,
                settings.PreviewHotkey,
                settings.UnPreviewHotkey,
                settings.StopHotkey));
    }

    private static string FormatBatch(ProgressSnapshot progress)
    {
        var effectiveBatch = EffectiveBatch(progress);
        if (progress.ReplicationPacingEnabled && effectiveBatch > 0)
        {
            var max = progress.ReplicationPacingResolvedBatchLimit > 0 ? progress.ReplicationPacingResolvedBatchLimit : progress.ReplicationPacingRequestedBatchLimit;
            return max > 0 ? $"{effectiveBatch}/{max}" : effectiveBatch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var batch = effectiveBatch;
        return batch > 0 ? batch.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
    }

    private static string FormatPacing(ProgressSnapshot progress)
    {
        var pacing = EffectivePacing(progress);
        return pacing >= 0 ? $"{pacing}ms" : "-";
    }

    private static int EffectiveBatch(ProgressSnapshot progress)
    {
        if (progress.ReplicationPacingEnabled && progress.ReplicationPacingBatchLimit > 0)
            return progress.ReplicationPacingBatchLimit;
        if (progress.ServerBatchLimit > 0)
            return progress.ServerBatchLimit;
        return DefaultPackedBatchLimit;
    }

    private static int EffectivePacing(ProgressSnapshot progress)
    {
        if (progress.ReplicationPacingEnabled && progress.ReplicationPacingMs > 0)
            return progress.ReplicationPacingMs;
        if (progress.ServerBatchPacingMs > 0)
            return progress.ServerBatchPacingMs;
        return DefaultPackedPacingMs;
    }

    private static string FormatQueue(ProgressSnapshot progress)
    {
        var queueCount = string.Equals(progress.ReplayProgressSource, "receiver_queue_drain", StringComparison.OrdinalIgnoreCase) &&
                         progress.LocalPackedQueueDrainCurrentQueue >= 0
            ? progress.LocalPackedQueueDrainCurrentQueue
            : progress.ReplicationQueuedStrokeCount;
        if (queueCount < 0)
            return "-";
        var strokes = queueCount == 1 ? "stroke" : "strokes";
        var drainRate = string.Equals(progress.ReplayProgressSource, "receiver_queue_drain", StringComparison.OrdinalIgnoreCase) &&
                        progress.LocalPackedQueueDrainStrokesPerSec > 0.0 &&
                        double.IsFinite(progress.LocalPackedQueueDrainStrokesPerSec)
            ? progress.LocalPackedQueueDrainStrokesPerSec
            : progress.ReplicationPacingQueueDrainStrokesPerSec;
        if (drainRate > 0.0 && double.IsFinite(drainRate))
        {
            var drain = drainRate >= 10.0
                ? Math.Round(drainRate).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Math.Round(drainRate, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{queueCount} {strokes} (drain {drain}/s)";
        }
        return $"{queueCount} {strokes}";
    }

    private static ResetSnapshot BuildResetSnapshot(AppSettings settings, AppSettings defaults)
    {
        var map = ResetKeys.ToDictionary(key => key, key => !SettingEquals(settings, defaults, key), StringComparer.OrdinalIgnoreCase);
        var sections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["paint.geometry"] = map["paint.brush1SizeTexels"] || map["paint.brush2SizeTexels"] ||
                                 map["paint.detailResolutionPercent"] || map["paint.packedBatchLimit"] ||
                                 map["paint.packedBatchPacingMs"],
            ["paint.material"] = map["paint.autoMaterial"] || map["paint.metallic"] || map["paint.roughness"],
            ["regions"] = map["paint.frontRegionMode"] || map["paint.sideRegionMode"] || map["paint.backRegionMode"],
            ["fill.material"] = map["paint.fillColor"] || map["paint.fillMetallic"] || map["paint.fillRoughness"],
            ["app"] = map["app.processName"] || map["app.alwaysOnTop"] || map["app.opacity"] || map["app.themeColor"] ||
                    map["app.startHotkey"] || map["app.previewHotkey"] || map["app.unpreviewHotkey"] || map["app.stopHotkey"]
        };
        return new ResetSnapshot(map, sections);
    }

    private static bool SettingEquals(AppSettings left, AppSettings right, string key) => key switch
    {
        "paint.brush1SizeTexels" => Nearly(left.Paint.Brush1SizeTexels, right.Paint.Brush1SizeTexels),
        "paint.brush2SizeTexels" => Nearly(left.Paint.Brush2SizeTexels, right.Paint.Brush2SizeTexels),
        "paint.detailResolutionPercent" => left.Paint.DetailResolutionPercent == right.Paint.DetailResolutionPercent,
        "paint.packedBatchLimit" => left.Paint.PackedBatchLimit == right.Paint.PackedBatchLimit,
        "paint.packedBatchPacingMs" => left.Paint.PackedBatchPacingMs == right.Paint.PackedBatchPacingMs,
        "paint.autoMaterial" => left.Paint.AutoMaterial == right.Paint.AutoMaterial,
        "paint.metallic" => Nearly(left.Paint.Metallic, right.Paint.Metallic),
        "paint.roughness" => Nearly(left.Paint.Roughness, right.Paint.Roughness),
        "paint.frontRegionMode" => left.Paint.FrontRegionMode == right.Paint.FrontRegionMode,
        "paint.sideRegionMode" => left.Paint.SideRegionMode == right.Paint.SideRegionMode,
        "paint.backRegionMode" => left.Paint.BackRegionMode == right.Paint.BackRegionMode,
        "paint.fillColor" => left.Paint.FillColor == right.Paint.FillColor,
        "paint.fillMetallic" => Nearly(left.Paint.FillMetallic, right.Paint.FillMetallic),
        "paint.fillRoughness" => Nearly(left.Paint.FillRoughness, right.Paint.FillRoughness),
        "app.processName" => left.GameProcessName == right.GameProcessName,
        "app.alwaysOnTop" => left.AlwaysOnTop == right.AlwaysOnTop,
        "app.opacity" => Nearly(left.Opacity, right.Opacity),
        "app.themeColor" => left.ThemeColor == right.ThemeColor,
        "app.startHotkey" => left.StartHotkey == right.StartHotkey,
        "app.previewHotkey" => left.PreviewHotkey == right.PreviewHotkey,
        "app.unpreviewHotkey" => left.UnPreviewHotkey == right.UnPreviewHotkey,
        "app.stopHotkey" => left.StopHotkey == right.StopHotkey,
        _ => true
    };

    private static void ResetOne(AppSettings settings, AppSettings defaults, string key)
    {
        switch (key)
        {
            case "paint.brush1SizeTexels": settings.Paint.Brush1SizeTexels = defaults.Paint.Brush1SizeTexels; break;
            case "paint.brush2SizeTexels":
                settings.Paint.Brush2SizeTexels = defaults.Paint.Brush2SizeTexels;
                settings.Paint.CoverageStepTexels = defaults.Paint.Brush2SizeTexels;
                break;
            case "paint.detailResolutionPercent": settings.Paint.DetailResolutionPercent = defaults.Paint.DetailResolutionPercent; break;
            case "paint.packedBatchLimit": settings.Paint.PackedBatchLimit = defaults.Paint.PackedBatchLimit; break;
            case "paint.packedBatchPacingMs": settings.Paint.PackedBatchPacingMs = defaults.Paint.PackedBatchPacingMs; break;
            case "paint.autoMaterial": settings.Paint.AutoMaterial = defaults.Paint.AutoMaterial; break;
            case "paint.metallic": settings.Paint.Metallic = defaults.Paint.Metallic; break;
            case "paint.roughness": settings.Paint.Roughness = defaults.Paint.Roughness; break;
            case "paint.frontRegionMode": settings.Paint.FrontRegionMode = defaults.Paint.FrontRegionMode; break;
            case "paint.sideRegionMode": settings.Paint.SideRegionMode = defaults.Paint.SideRegionMode; break;
            case "paint.backRegionMode": settings.Paint.BackRegionMode = defaults.Paint.BackRegionMode; break;
            case "paint.fillColor": settings.Paint.FillColor = defaults.Paint.FillColor; break;
            case "paint.fillMetallic": settings.Paint.FillMetallic = defaults.Paint.FillMetallic; break;
            case "paint.fillRoughness": settings.Paint.FillRoughness = defaults.Paint.FillRoughness; break;
            case "app.processName": settings.GameProcessName = defaults.GameProcessName; break;
            case "app.alwaysOnTop": settings.AlwaysOnTop = defaults.AlwaysOnTop; break;
            case "app.opacity": settings.Opacity = defaults.Opacity; break;
            case "app.themeColor": settings.ThemeColor = defaults.ThemeColor; break;
            case "app.startHotkey": settings.StartHotkey = defaults.StartHotkey; break;
            case "app.previewHotkey": settings.PreviewHotkey = defaults.PreviewHotkey; break;
            case "app.unpreviewHotkey": settings.UnPreviewHotkey = defaults.UnPreviewHotkey; break;
            case "app.stopHotkey": settings.StopHotkey = defaults.StopHotkey; break;
            default: throw new ArgumentException($"Unknown setting: {key}");
        }
    }

    private static void ApplySetting(AppSettings settings, string key, JsonElement value)
    {
        switch (key)
        {
            case "paint.brush1SizeTexels": settings.Paint.Brush1SizeTexels = value.GetDouble(); break;
            case "paint.brush2SizeTexels":
                settings.Paint.Brush2SizeTexels = value.GetDouble();
                settings.Paint.CoverageStepTexels = settings.Paint.Brush2SizeTexels;
                break;
            case "paint.detailResolutionPercent": settings.Paint.DetailResolutionPercent = RoundedInteger(value); break;
            case "paint.packedBatchLimit": settings.Paint.PackedBatchLimit = RoundedInteger(value); break;
            case "paint.packedBatchPacingMs": settings.Paint.PackedBatchPacingMs = RoundedInteger(value); break;
            case "paint.autoMaterial": settings.Paint.AutoMaterial = value.GetBoolean(); break;
            case "paint.metallic": settings.Paint.Metallic = value.GetDouble(); break;
            case "paint.roughness": settings.Paint.Roughness = value.GetDouble(); break;
            case "paint.frontRegionMode": settings.Paint.FrontRegionMode = ParseRegionMode(value.GetString()); break;
            case "paint.sideRegionMode": settings.Paint.SideRegionMode = ParseRegionMode(value.GetString()); break;
            case "paint.backRegionMode": settings.Paint.BackRegionMode = ParseRegionMode(value.GetString()); break;
            case "paint.fillColor":
                if (!RgbColor.TryParse(value.GetString(), out var fill))
                    throw new ArgumentException("Fill color must be #RRGGBB.");
                settings.Paint.FillColor = fill;
                break;
            case "paint.fillMetallic": settings.Paint.FillMetallic = value.GetDouble(); break;
            case "paint.fillRoughness": settings.Paint.FillRoughness = value.GetDouble(); break;
            case "app.language": settings.Language = value.GetString() ?? settings.Language; break;
            case "app.processName": settings.GameProcessName = value.GetString() ?? settings.GameProcessName; break;
            case "app.alwaysOnTop": settings.AlwaysOnTop = value.GetBoolean(); break;
            case "app.opacity": settings.Opacity = value.GetDouble(); break;
            case "app.themeColor":
                if (!RgbColor.TryParse(value.GetString(), out var theme))
                    throw new ArgumentException("Theme color must be #RRGGBB.");
                settings.ThemeColor = theme;
                break;
            case "app.startHotkey": settings.StartHotkey = HotkeySet.Normalize(value.GetString()); break;
            case "app.previewHotkey": settings.PreviewHotkey = HotkeySet.Normalize(value.GetString()); break;
            case "app.unpreviewHotkey": settings.UnPreviewHotkey = HotkeySet.Normalize(value.GetString()); break;
            case "app.stopHotkey": settings.StopHotkey = HotkeySet.Normalize(value.GetString()); break;
            default: throw new ArgumentException($"Unknown setting: {key}");
        }
    }

    private static int RoundedInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            throw new ArgumentException("Integer setting must be numeric.");
        if (value.TryGetInt32(out var exact))
            return exact;
        return checked((int)Math.Round(value.GetDouble(), MidpointRounding.AwayFromZero));
    }

    private static RegionMode ParseRegionMode(string? value)
    {
        if (Enum.TryParse<RegionMode>(value, true, out var mode) && Enum.IsDefined(mode))
            return mode;
        throw new ArgumentException("Region mode must be paint, fill, or skip.");
    }

    private void TryDeleteProgressSnapshot()
    {
        try
        {
            var path = Runtime.ProgressPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only; stale progress is still rejected by timestamp.
        }
    }

    private ProgressSnapshot? ReadCurrentProgressSnapshot(bool liveOnly)
    {
        if (currentPaintStartedAt == DateTimeOffset.MinValue)
            return null;
        var preferredPath = Runtime.ProgressPath;
        var hasPreferredPath = !string.IsNullOrWhiteSpace(preferredPath);
        ProgressSnapshot? progress;
        DateTimeOffset writeTime;
        if (hasPreferredPath)
        {
            // The configured path belongs to the authenticated bridge instance.
            // Until its first atomic snapshot appears (or if it is malformed),
            // show no progress rather than borrowing another instance's file.
            progress = ReadProgressSnapshot(preferredPath, out writeTime);
            if (progress is null)
                return null;
        }
        else
        {
            progress = ReadFallbackProgressSnapshot(out writeTime);
        }
        if (progress is null)
            return null;
        var cutoff = currentPaintStartedAt.AddSeconds(-1);
        if (writeTime < cutoff)
        {
            if (hasPreferredPath)
                return null;
            progress = ReadFallbackProgressSnapshot(out writeTime);
            if (progress is null || writeTime < cutoff)
                return null;
        }
        if (liveOnly && !PaintRunning)
            return null;
        if (liveOnly && !currentProgressIsServerPaint)
            return null;
        return progress;
    }

    private ProgressSnapshot? ReadFallbackProgressSnapshot(out DateTimeOffset writeTime)
    {
        writeTime = DateTimeOffset.MinValue;
        if (currentPaintStartedAt == DateTimeOffset.MinValue)
            return null;
        var cutoff = currentPaintStartedAt.AddSeconds(-1);
        try
        {
            foreach (var path in ProgressSnapshotCandidatePaths(Paths, Runtime.ProgressPath))
            {
                var progress = ReadProgressSnapshot(path, out var candidateWriteTime);
                if (progress is null || candidateWriteTime < cutoff)
                    continue;
                writeTime = candidateWriteTime;
                return progress;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    public static string[] ProgressSnapshotCandidatePaths(AppPaths paths, string? preferredPath = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        AddCandidate(preferredPath);
        AddProgressDirectory(paths.BridgeProgressDirectory);

        return candidates
            .OrderByDescending(SafeLastWriteTimeUtc)
            .ToArray();

        void AddProgressDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return;
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.progress.json"))
                    AddCandidate(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        void AddCandidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                    candidates.Add(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
            }
        }

        static DateTime SafeLastWriteTimeUtc(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }

    private void LogFinalProgressOnce()
    {
        if (!currentProgressIsServerPaint)
            return;
        if (finalProgressLogged)
            return;
        var progress = ReadCurrentProgressSnapshot(liveOnly: false);
        if (progress is null)
            return;
        LogReplayPassTransition(progress);
        var line = FormatProgressLogLine(progress);
        if (line.Length == 0)
            return;
        finalProgressLogged = true;
        Log.Info(line);
    }

    private void LogFailureProgressOnce()
    {
        if (!currentProgressIsServerPaint)
            return;
        if (finalProgressLogged)
            return;
        var progress = ReadCurrentProgressSnapshot(liveOnly: false);
        if (progress is null || !ShouldLogFailureProgress(progress))
            return;
        LogReplayPassTransition(progress);
        var line = FormatProgressLogLine(progress);
        if (line.Length == 0)
            return;
        finalProgressLogged = true;
        Log.Info(line);
    }

    private static bool ShouldLogFailureProgress(ProgressSnapshot progress)
    {
        var phase = progress.Phase.Trim().ToLowerInvariant();
        return phase is "server_batch" or "local_sync" or "local_texture_import" or "texture_sync_observe" or "server_texture_sync" or "failed" or "cancelled" ||
               phase.StartsWith("mesh_server_batch", StringComparison.OrdinalIgnoreCase) ||
               phase.StartsWith("mesh_paint_", StringComparison.OrdinalIgnoreCase);
    }

    private void ResetReplayPassLog()
    {
        lock (replayPassLogGate)
            loggedReplayPasses.Clear();
    }

    private void LogReplayPassTransition(ProgressSnapshot? progress)
    {
        if (!currentProgressIsServerPaint || progress is null)
            return;
        var key = progress.ReplayCurrentPass.Trim().ToLowerInvariant();
        if (key is not ("fill" or "coarse_paint" or "fine_paint" or "complete"))
            return;
        lock (replayPassLogGate)
        {
            if (!loggedReplayPasses.Add(key))
                return;
        }
        var stage = ReplayProgressStageLabel(progress.ReplayProgressSource);
        var stageSuffix = stage.Length > 0 ? $" ({stage})" : "";
        Log.Info($"Paint: pass {ReplayPassLabel(key)}{stageSuffix}.");
    }

    private string FormatProgressLogLine(ProgressSnapshot progress)
    {
        var percent = progress.TotalSteps > 0
            ? Math.Clamp(progress.Step * 100.0 / progress.TotalSteps, 0.0, 100.0)
            : Math.Clamp(progress.Progress * 100.0, 0.0, 100.0);
        var rounded = (int)Math.Round(percent);
        var pass = FormatReplayPass(progress);
        return $"Paint: overall {rounded}% {ProgressBar(rounded)} | pass {pass} | pass ETA {FormatDuration(progress.ReplayCurrentPassEtaMs)} | total ETA {FormatEta(progress)} | batch {FormatBatch(progress)} | pacing {FormatPacing(progress)} | queue {FormatQueue(progress)} | elapsed {FormatDuration(progress.PaintElapsedMs)}";
    }

    private static string FormatReplayPass(ProgressSnapshot progress)
    {
        var stage = ReplayProgressStageLabel(progress.ReplayProgressSource);
        var pass = ReplayPassLabel(progress.ReplayCurrentPass);
        var passProgress = FormatReplayPassProgress(progress);
        return string.Join(' ', new[] { stage, pass, passProgress }.Where(value => !string.IsNullOrWhiteSpace(value) && value != "-")) switch
        {
            "" => "-",
            var detail => detail
        };
    }

    private static string ReplayPassLabel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "fill" => "Fill",
        "coarse_paint" => "Brush 1",
        "fine_paint" => "Brush 2",
        "complete" => "Complete",
        "" => "-",
        _ => value.Trim()
    };

    private static string ReplayProgressStageLabel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "receiver_queue_drain" => "painting",
        "submission" => "queueing",
        _ => ""
    };

    private static string FormatReplayPassProgress(ProgressSnapshot progress)
    {
        var pass = ReplayPassLabel(progress.ReplayCurrentPass);
        var completed = progress.ReplayCurrentPassCompleted;
        var total = progress.ReplayCurrentPassTotal;
        if (total < 0 && progress.ReplayCurrentPassStart >= 0 && progress.ReplayCurrentPassEnd >= progress.ReplayCurrentPassStart)
        {
            total = progress.ReplayCurrentPassEnd - progress.ReplayCurrentPassStart;
            completed = Math.Clamp(progress.Step - progress.ReplayCurrentPassStart, 0, total);
        }
        if (pass == "Complete" && total <= 0 && progress.TotalSteps > 0)
        {
            completed = progress.TotalSteps;
            total = progress.TotalSteps;
        }
        if (total <= 0)
            return pass == "Complete" ? "100%" : "-";
        completed = Math.Clamp(completed, 0, total);
        var percent = (int)Math.Round(completed * 100.0 / total);
        return $"{completed}/{total} ({percent}%)";
    }

    private static string ProgressBar(int percent)
    {
        const int width = 16;
        var filled = Math.Clamp((int)Math.Round((percent / 100.0) * width), 0, width);
        return "[" + new string('#', filled) + new string('-', width - filled) + "]";
    }

    private static ProgressSnapshot? ReadProgressSnapshot(string path, out DateTimeOffset writeTime)
    {
        writeTime = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            writeTime = File.GetLastWriteTimeUtc(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new ProgressSnapshot(
                Text(root, "phase", Text(root, "stage", "")),
                Text(root, "result", ""),
                Bool(root, "terminal", false),
                Int(root, "step", 0),
                Int(root, "total_steps", Int(root, "total_strokes", 0)),
                Number(root, "progress", 0.0),
                Int(root, "server_batch_limit", -1),
                Int(root, "server_batch_pacing_ms", Int(root, "server_batch_delay_ms", -1)),
                Number(root, "paint_eta_ms", -1.0),
                Number(root, "paint_elapsed_ms", Number(root, "elapsed_ms", -1.0)),
                Bool(root, "replication_pacing_enabled", Bool(root, "adaptive_batch_enabled", false)),
                Int(root, "replication_pacing_requested_batch_limit", Int(root, "adaptive_requested_batch_limit", -1)),
                Int(root, "replication_pacing_resolved_batch_limit", Int(root, "adaptive_resolved_batch_limit", -1)),
                Int(root, "replication_pacing_requested_ms",
                    Int(root, "replication_pacing_requested_delay_ms", Int(root, "adaptive_requested_delay_ms", -1))),
                Int(root, "replication_pacing_batch_limit", Int(root, "adaptive_batch_limit", -1)),
                Int(root, "replication_pacing_ms",
                    Int(root, "replication_pacing_delay_ms", Int(root, "adaptive_delay_ms", -1))),
                Text(root, "replication_pacing_pressure_level", Text(root, "adaptive_pressure_level", "unknown")),
                Int(root, "replication_pacing_backoff_count", Int(root, "adaptive_backoff_count", 0)),
                Number(root, "replication_pacing_queue_drain_strokes_per_sec", Number(root, "adaptive_queue_drain_strokes_per_sec", -1.0)),
                Number(root, "replication_pacing_send_strokes_per_sec", Number(root, "adaptive_send_strokes_per_sec", -1.0)),
                Number(root, "replication_pacing_model_eta_ms", Number(root, "adaptive_model_eta_ms", -1.0)),
                Int(root, "replication_queued_batch_count", -1),
                Int(root, "replication_queued_stroke_count", -1),
                Int(root, "replication_max_strokes_per_tick", -1),
                Number(root, "replication_estimated_ticks_to_drain", -1.0),
                Text(root, "replay_current_pass", ""),
                Int(root, "replay_current_pass_start", -1),
                Int(root, "replay_current_pass_end", -1),
                Int(root, "replay_server_offset", -1),
                Int(root, "replay_local_offset", -1),
                Text(root, "replay_progress_source", ""),
                Int(root, "replay_current_pass_completed", -1),
                Int(root, "replay_current_pass_total", -1),
                Number(root, "replay_current_pass_eta_ms", -1.0),
                Int(root, "local_packed_queue_drain_current_queue", -1),
                Number(root, "local_packed_queue_drain_strokes_per_sec", -1.0));
        }
        catch
        {
            return null;
        }
    }

    private static string FormatDuration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0.0)
            return "-";
        if (milliseconds < 1000.0)
            return "0s";
        var totalSeconds = (int)Math.Round(milliseconds / 1000.0);
        if (totalSeconds < 60)
            return totalSeconds + "s";
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        if (minutes < 60)
            return $"{minutes}m {seconds:00}s";
        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours}h {minutes:00}m";
    }

    private static string FormatEta(ProgressSnapshot progress)
    {
        if (progress.Terminal)
            return string.Equals(progress.Result, "done", StringComparison.OrdinalIgnoreCase) ? "0s" : "-";

        return FormatDuration(progress.PaintEtaMs);
    }

    private static string Text(JsonElement root, string key, string fallback) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static bool Bool(JsonElement root, string key, bool fallback) =>
        root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;

    private static int Int(JsonElement root, string key, int fallback) =>
        root.TryGetProperty(key, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static double Number(JsonElement root, string key, double fallback) =>
        root.TryGetProperty(key, out var value) && value.TryGetDouble(out var parsed) ? parsed : fallback;

    private static bool Nearly(double left, double right) => Math.Abs(left - right) < 0.000001;
}
