using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MecchaCamouflage.Controller;
using MecchaCamouflage.Core;

namespace MecchaCamouflage.WebHost;

/// <summary>
/// Explicit, local-only runtime investigation entry point. It deliberately shares the packaged
/// controller and its authenticated bridge instead of opening a second diagnostic socket.
/// </summary>
internal static class ResearchRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record Options(
        int ProcessId,
        string DeclaredRole,
        string ArtifactDirectory,
        bool Paint,
        int HoldSeconds,
        int PressureSampleMs,
        bool TextureSnapshot,
        ResearchTextureTarget TextureTarget,
        int TextureDiscoverySeconds,
        string PaintMode,
        double? PackedRadiusScaleOverride,
        bool TriangleWorldRadius,
        int? PackedBatchLimitOverride,
        int? PackedBatchPacingOverrideMs,
        int StrokeLimit,
        int? ReplayStrokeIndex,
        int? CancelAfterMs,
        int? ShutdownAfterMs,
        RgbColor? FillColorOverride,
        RgbColor? PaintColorOverride,
        RegionMode? FrontRegionModeOverride,
        RegionMode? SideRegionModeOverride,
        RegionMode? BackRegionModeOverride);

    private sealed record ReplyArtifact(string Name, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, BridgeReply Reply);
    private sealed record TimedReply(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, BridgeReply Reply);

    public static bool IsRequested(string[] args) =>
        args.Any(arg => string.Equals(arg, "--research-replication", StringComparison.Ordinal));

    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Research runner arguments: " + ex.Message);
            return 2;
        }

        if (!ResearchArtifactsEnabled())
        {
            Console.Error.WriteLine("Research runner requires MECCHA_RESEARCH_ARTIFACTS=1.");
            return 2;
        }

        Directory.CreateDirectory(options.ArtifactDirectory);
        var runDirectory = Path.Combine(
            options.ArtifactDirectory,
            "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        var eventWatchPath = Path.Combine(runDirectory, "eventwatch-live.json");
        var summary = new Dictionary<string, object?>
        {
            ["runner"] = "research-replication",
            ["started_utc"] = DateTimeOffset.UtcNow,
            ["declared_role"] = options.DeclaredRole,
            ["pid"] = options.ProcessId,
            ["paint_requested"] = options.Paint,
            ["hold_seconds"] = options.HoldSeconds,
            ["pressure_sample_ms"] = options.PressureSampleMs,
            ["texture_snapshot_requested"] = options.TextureSnapshot,
            ["research_texture_target"] = options.TextureTarget.ToString(),
            ["texture_discovery_seconds"] = options.TextureDiscoverySeconds,
            ["paint_mode"] = options.PaintMode,
            ["packed_radius_scale_override"] = options.PackedRadiusScaleOverride,
            ["triangle_world_radius"] = options.TriangleWorldRadius,
            ["packed_batch_limit_override"] = options.PackedBatchLimitOverride,
            ["packed_batch_pacing_override_ms"] = options.PackedBatchPacingOverrideMs,
            ["stroke_limit"] = options.StrokeLimit,
            ["replay_stroke_index"] = options.ReplayStrokeIndex,
            ["fill_color_override"] = options.FillColorOverride?.ToHex(),
            ["paint_color_override"] = options.PaintColorOverride?.ToHex(),
            ["front_region_mode_override"] = options.FrontRegionModeOverride is RegionMode frontMode
                ? SettingsStore.RegionModeText(frontMode)
                : null,
            ["side_region_mode_override"] = options.SideRegionModeOverride is RegionMode sideMode
                ? SettingsStore.RegionModeText(sideMode)
                : null,
            ["back_region_mode_override"] = options.BackRegionModeOverride is RegionMode backMode
                ? SettingsStore.RegionModeText(backMode)
                : null,
            ["artifact_directory"] = runDirectory,
            ["eventwatch_path"] = eventWatchPath,
            ["success"] = false
        };
        if (options.CancelAfterMs is int configuredCancelAfterMs)
        {
            summary["cancel_after_ms"] = configuredCancelAfterMs;
            summary["cancel_requested"] = true;
        }
        if (options.ShutdownAfterMs is int configuredShutdownAfterMs)
        {
            summary["shutdown_after_ms"] = configuredShutdownAfterMs;
            summary["shutdown_during_paint_requested"] = true;
        }
        WriteJson(Path.Combine(runDirectory, "run-start.json"), summary);

        using var session = new HostSession(VersionInfo.Current);
        if (options.PackedBatchLimitOverride is int packedBatchLimitOverride)
            session.Settings.Paint.PackedBatchLimit = packedBatchLimitOverride;
        if (options.PackedBatchPacingOverrideMs is int packedBatchPacingOverrideMs)
            session.Settings.Paint.PackedBatchPacingMs = packedBatchPacingOverrideMs;
        if (options.FillColorOverride is not null)
            session.Settings.Paint.FillColor = options.FillColorOverride;
        if (options.FrontRegionModeOverride is RegionMode frontRegionMode)
            session.Settings.Paint.FrontRegionMode = frontRegionMode;
        if (options.SideRegionModeOverride is RegionMode sideRegionMode)
            session.Settings.Paint.SideRegionMode = sideRegionMode;
        if (options.BackRegionModeOverride is RegionMode backRegionMode)
            session.Settings.Paint.BackRegionMode = backRegionMode;
        var exitCode = 1;
        var shutdownDuringPaintAttempted = false;
        var shutdownDuringPaintEventWatchChecked = false;
        var shutdownDuringPaintEventWatchStopped = false;
        try
        {
            using var process = Process.GetProcessById(options.ProcessId);
            if (process.HasExited)
                throw new InvalidOperationException("The requested target process already exited.");
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("The requested target executable path is unavailable.");

            summary["game_executable"] = executablePath;
            summary["game_file_bytes"] = new FileInfo(executablePath).Length;
            summary["game_file_version"] = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
            summary["controller_version"] = VersionInfo.Current;
            summary["paint_settings"] = PaintSettingsArtifact(session.Settings);

            var ready = await session.Runtime.EnsureResearchReadyAsync(
                process.Id,
                new ResearchBridgeOptions(eventWatchPath));
            summary["bridge_ready"] = ready;
            if (!ready)
            {
                // Preserve the failed start as evidence. Normal direct-bridge startup intentionally
                // permits a fresh instance even when an older DLL remains resident, so this runner
                // must not turn an indeterminate startup failure into a forced game restart.
                summary["bridge_start_failed"] = true;
                throw new InvalidOperationException("The authenticated research bridge did not become ready.");
            }

            var bridgeIdentity = session.Runtime.ActiveResearchBridgeIdentity
                ?? throw new InvalidOperationException("Research bridge identity was unavailable after successful startup.");
            summary["bridge_instance_id"] = bridgeIdentity.InstanceId.ToString("N");
            summary["bridge_hash"] = bridgeIdentity.BridgeHash;
            summary["staged_bridge_path"] = bridgeIdentity.BridgePath;

            var eventWatch = await WaitForEventWatchAsync(eventWatchPath, bridgeIdentity, TimeSpan.FromSeconds(10));
            SnapshotEventWatch(eventWatchPath, Path.Combine(runDirectory, "eventwatch-start.json"));
            summary["eventwatch_ready"] = eventWatch.Ready;
            summary["eventwatch_stage"] = eventWatch.Stage;
            if (!eventWatch.Ready)
                throw new InvalidOperationException("Event-watch did not start with hooks and ServerPackedPaintBatch resolved.");

            await CaptureProbeAsync(session, ResearchProbeKind.Replication, "replication-before", runDirectory);
            await CaptureProbeAsync(session, ResearchProbeKind.ReplicationPressure, "pressure-before", runDirectory);
            string? textureExpectedComponent = null;
            if (options.TextureSnapshot)
            {
                if (options.TextureTarget == ResearchTextureTarget.EventwatchMulticastPackedReceiver)
                {
                    var discovery = await WaitForMulticastPackedReceiverAsync(
                        eventWatchPath,
                        bridgeIdentity,
                        TimeSpan.FromSeconds(options.TextureDiscoverySeconds));
                    summary["texture_target_discovered"] = discovery.Observed;
                    summary["texture_target_discovery_receiver"] = discovery.Component;
                    summary["texture_target_discovery_calls"] = discovery.Calls;
                    summary["texture_target_discovery_eventwatch_entry"] = "MulticastPackedPaintBatch";
                    SnapshotEventWatch(eventWatchPath, Path.Combine(runDirectory, "eventwatch-texture-target-discovered.json"));
                    if (!discovery.Observed)
                    {
                        throw new InvalidOperationException(
                            "No remote MulticastPackedPaintBatch receiver was observed during texture discovery.");
                    }
                    textureExpectedComponent = discovery.Component;
                    summary["texture_target_expected_component"] = textureExpectedComponent;
                }
                await CaptureProbeAsync(
                    session,
                    ResearchProbeKind.ReplicationTexture,
                    "texture-before",
                    runDirectory,
                    options.TextureTarget,
                    textureExpectedComponent);
            }

            if (options.Paint)
            {
                var payload = BridgePayloadBuilder.BuildPaintPayload(
                    session.Settings,
                    process.Id,
                    Path.GetFileName(executablePath),
                    new PaintRequestOptions(ResearchArtifacts: true));
                payload = AddResearchPaintControls(payload, options);
                WriteJson(
                    Path.Combine(runDirectory, "paint-request.json"),
                    new
                    {
                        declared_role = options.DeclaredRole,
                        requested_utc = DateTimeOffset.UtcNow,
                        settings = PaintSettingsArtifact(session.Settings),
                        payload
                    });
                var paintTask = SendPaintWithTimingAsync(session.Runtime, payload);
                Task<TimedReply>? cancelTask = options.CancelAfterMs is int cancelAfterMs
                    ? CancelPaintAfterDelayAsync(session.Runtime, cancelAfterMs, paintTask)
                    : null;
                Task<TimedReply>? shutdownTask = options.ShutdownAfterMs is int shutdownAfterMs
                    ? ShutdownAfterDelayAsync(session.Runtime, shutdownAfterMs)
                    : null;
                shutdownDuringPaintAttempted = shutdownTask is not null;
                if (cancelTask is not null)
                    await Task.WhenAll(paintTask, cancelTask);
                else if (shutdownTask is not null)
                    await Task.WhenAll(paintTask, shutdownTask);
                var paint = await paintTask;
                var reply = paint.Reply;
                WriteJson(
                    Path.Combine(runDirectory, "paint-reply.json"),
                    new ReplyArtifact("paint", paint.StartedUtc, paint.CompletedUtc, reply));
                summary["paint_success"] = reply.Success;
                summary["paint_stage"] = reply.Stage;
                if (reply.Ok && reply.Success)
                {
                    var uvReplayArtifact = ResearchUvReplayArtifacts.StageAndRender(reply, runDirectory);
                    summary["uv_replay_plan_written"] = uvReplayArtifact.Success;
                    summary["uv_replay_plan_path"] = uvReplayArtifact.PlanPath;
                    summary["uv_replay_atlas_path"] = uvReplayArtifact.AtlasPath;
                    if (!uvReplayArtifact.Success)
                    {
                        summary["uv_replay_artifact_error"] = uvReplayArtifact.Error;
                        throw new InvalidOperationException(
                            "Completed research paint did not produce a usable UV replay artifact: " + uvReplayArtifact.Error);
                    }
                }
                else
                {
                    // Native writes this sidecar at planning time. A failed or cancelled paint
                    // must not leave a planned path that looks like a rendered result.
                    summary["uv_replay_plan_written"] = false;
                    summary["uv_replay_plan_disposition"] = "not_staged_non_successful_paint";
                }
                if (cancelTask is not null)
                {
                    var cancel = await cancelTask;
                    WriteJson(
                        Path.Combine(runDirectory, "cancel-paint-reply.json"),
                        new ReplyArtifact("cancel-paint", cancel.StartedUtc, cancel.CompletedUtc, cancel.Reply));
                    var cancelledJobs = HostSession.CancelledPaintJobCount(cancel.Reply);
                    var cancelAdmissionLatched = HostSession.NativePaintRequestCancellationLatched(cancel.Reply);
                    var paintCancelled = IsPaintCancellationReply(reply);
                    summary["cancel_reply"] = ReplySummary(cancel.Reply);
                    summary["cancelled_paint_jobs"] = cancelledJobs;
                    summary["cancel_admission_latched"] = cancelAdmissionLatched;
                    summary["paint_terminal_result"] = ReplySummary(reply);
                    summary["paint_cancel_observed"] = paintCancelled;
                    if (!cancel.Reply.Ok || !cancel.Reply.Success)
                    {
                        throw new InvalidOperationException(
                            $"Scheduled paint cancel request failed at {cancel.Reply.Stage}: {cancel.Reply.Message}");
                    }
                    if (cancelledJobs is null || (cancelledJobs <= 0 && !cancelAdmissionLatched))
                    {
                        throw new InvalidOperationException(
                            "Scheduled paint cancel did not reach or latch the active native paint request.");
                    }
                    if (!paintCancelled)
                    {
                        throw new InvalidOperationException(
                            $"Scheduled paint cancel did not produce a cancellation terminal result at {reply.Stage}: {reply.Message}");
                    }
                }
                else if (shutdownTask is not null)
                {
                    var shutdown = await shutdownTask;
                    WriteJson(
                        Path.Combine(runDirectory, "shutdown-during-paint-reply.json"),
                        new ReplyArtifact("shutdown-during-paint", shutdown.StartedUtc, shutdown.CompletedUtc, shutdown.Reply));
                    var activePaintQuiescent = ReplyMetadataBool(shutdown.Reply, "active_paint_quiescent");
                    var paintRequestWasInProgress = ReplyMetadataBool(shutdown.Reply, "paint_request_was_in_progress");
                    var paintCancelled = IsPaintCancellationReply(reply);
                    shutdownDuringPaintEventWatchStopped =
                        await WaitForEventWatchStoppedAsync(eventWatchPath, TimeSpan.FromSeconds(3));
                    shutdownDuringPaintEventWatchChecked = true;
                    SnapshotEventWatch(
                        eventWatchPath,
                        Path.Combine(runDirectory, "eventwatch-stopped-during-paint.json"));
                    summary["shutdown_during_paint_reply"] = ReplySummary(shutdown.Reply);
                    summary["shutdown_success"] = shutdown.Reply.Ok && shutdown.Reply.Success;
                    summary["active_paint_quiescent"] = activePaintQuiescent;
                    summary["paint_request_was_in_progress"] = paintRequestWasInProgress;
                    summary["paint_terminal_result"] = ReplySummary(reply);
                    summary["paint_cancel_observed"] = paintCancelled;
                    summary["eventwatch_stopped"] = shutdownDuringPaintEventWatchStopped;
                    if (!shutdown.Reply.Ok || !shutdown.Reply.Success)
                    {
                        throw new InvalidOperationException(
                            $"Scheduled bridge shutdown failed at {shutdown.Reply.Stage}: {shutdown.Reply.Message}");
                    }
                    if (activePaintQuiescent != true)
                    {
                        throw new InvalidOperationException(
                            "Scheduled bridge shutdown did not prove that active paint was quiescent.");
                    }
                    if (!paintCancelled)
                    {
                        throw new InvalidOperationException(
                            $"Scheduled bridge shutdown did not produce a paint cancellation terminal result at {reply.Stage}: {reply.Message}");
                    }
                    if (!shutdownDuringPaintEventWatchStopped)
                    {
                        throw new InvalidOperationException(
                            "Scheduled bridge shutdown did not stop event-watch within the observation window.");
                    }
                }
                else if (!reply.Success)
                    throw new InvalidOperationException("Normal packed paint did not complete: " + reply.Message);
            }

            if (options.ShutdownAfterMs is null)
            {
                await HoldWithPressureSamplesAsync(session, options, runDirectory);

                SnapshotEventWatch(eventWatchPath, Path.Combine(runDirectory, "eventwatch-before-post-probes.json"));
                await CaptureProbeAsync(session, ResearchProbeKind.ReplicationPressure, "pressure-after", runDirectory);
                await CaptureProbeAsync(session, ResearchProbeKind.Replication, "replication-after", runDirectory);
                if (options.TextureSnapshot)
                {
                    await CaptureProbeAsync(
                        session,
                        ResearchProbeKind.ReplicationTexture,
                        "texture-after",
                        runDirectory,
                        options.TextureTarget,
                        textureExpectedComponent);
                    var textureDeltaArtifact = ResearchTextureDeltaArtifacts.StageAndRender(
                        Path.Combine(runDirectory, "texture-before.json"),
                        Path.Combine(runDirectory, "texture-after.json"),
                        runDirectory,
                        textureExpectedComponent);
                    summary["albedo_delta_png_written"] = textureDeltaArtifact.Success;
                    summary["albedo_delta_texture_size"] = textureDeltaArtifact.TextureSize;
                    summary["albedo_delta_changed_pixels"] = textureDeltaArtifact.ChangedPixels;
                    summary["albedo_delta_before_png_path"] = textureDeltaArtifact.BeforePngPath;
                    summary["albedo_delta_after_png_path"] = textureDeltaArtifact.AfterPngPath;
                    summary["albedo_delta_mask_path"] = textureDeltaArtifact.DeltaMaskPath;
                    summary["albedo_delta_component"] = textureDeltaArtifact.Component;
                    summary["albedo_delta_target_source"] = textureDeltaArtifact.TargetSource;
                    if (!textureDeltaArtifact.Success)
                    {
                        summary["albedo_delta_artifact_error"] = textureDeltaArtifact.Error;
                        throw new InvalidOperationException(
                            "Requested albedo texture delta is incomplete: " + textureDeltaArtifact.Error);
                    }
                }
                SnapshotEventWatch(eventWatchPath, Path.Combine(runDirectory, "eventwatch-final.json"));
            }

            summary["success"] = true;
            exitCode = 0;
        }
        catch (Exception ex)
        {
            summary["error"] = ex.ToString();
            WriteJson(Path.Combine(runDirectory, "error.json"), new { failed_utc = DateTimeOffset.UtcNow, error = ex.ToString() });
        }
        finally
        {
            try
            {
                if (shutdownDuringPaintAttempted)
                {
                    // The scheduled shutdown is the operation under test. Never hide an
                    // indeterminate result by automatically issuing a second shutdown.
                    summary["normal_shutdown_skipped"] = true;
                    if (!shutdownDuringPaintEventWatchChecked)
                    {
                        shutdownDuringPaintEventWatchStopped =
                            await WaitForEventWatchStoppedAsync(eventWatchPath, TimeSpan.FromSeconds(3));
                        shutdownDuringPaintEventWatchChecked = true;
                        SnapshotEventWatch(
                            eventWatchPath,
                            Path.Combine(runDirectory, "eventwatch-stopped-during-paint.json"));
                        summary["eventwatch_stopped"] = shutdownDuringPaintEventWatchStopped;
                    }
                    if (!shutdownDuringPaintEventWatchStopped)
                    {
                        summary["eventwatch_cleanup_failed"] = true;
                        summary["success"] = false;
                        exitCode = 1;
                    }
                }
                else if (session.Runtime.HasActiveBridgeInstance)
                {
                    var started = DateTimeOffset.UtcNow;
                    var reply = await session.Runtime.ShutdownAsync();
                    WriteJson(
                        Path.Combine(runDirectory, "shutdown-reply.json"),
                        new ReplyArtifact("shutdown", started, DateTimeOffset.UtcNow, reply));
                    var eventWatchStopped = await WaitForEventWatchStoppedAsync(eventWatchPath, TimeSpan.FromSeconds(3));
                    SnapshotEventWatch(eventWatchPath, Path.Combine(runDirectory, "eventwatch-stopped.json"));
                    summary["shutdown_success"] = reply.Ok && reply.Success;
                    summary["eventwatch_stopped"] = eventWatchStopped;
                    if (!reply.Ok || !reply.Success || !eventWatchStopped)
                    {
                        summary["eventwatch_cleanup_failed"] = true;
                        summary["success"] = false;
                        exitCode = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                summary["shutdown_error"] = ex.ToString();
                summary["eventwatch_cleanup_failed"] = true;
                summary["success"] = false;
                exitCode = 1;
            }

            summary["completed_utc"] = DateTimeOffset.UtcNow;
            summary["controller_log"] = session.Log.Text;
            WriteJson(Path.Combine(runDirectory, "run-summary.json"), summary);
        }

        return exitCode;
    }

    private static async Task<BridgeReply> CaptureProbeAsync(
        HostSession session,
        ResearchProbeKind kind,
        string name,
        string artifactDirectory,
        ResearchTextureTarget textureTarget = ResearchTextureTarget.ResolvedComponent,
        string? expectedTextureComponent = null)
    {
        var started = DateTimeOffset.UtcNow;
        var reply = await session.Runtime.SendResearchProbeAsync(kind, textureTarget, expectedTextureComponent);
        WriteJson(Path.Combine(artifactDirectory, name + ".json"), new ReplyArtifact(name, started, DateTimeOffset.UtcNow, reply));
        if (!reply.Ok || !reply.Success)
            throw new InvalidOperationException($"Research probe {name} failed: {reply.Message}");
        if (kind == ResearchProbeKind.ReplicationTexture && !string.IsNullOrWhiteSpace(expectedTextureComponent))
            VerifyTextureProbeTarget(reply, expectedTextureComponent);
        return reply;
    }

    private static void VerifyTextureProbeTarget(BridgeReply reply, string expectedTextureComponent)
    {
        using var document = JsonDocument.Parse(reply.Raw);
        if (!document.RootElement.TryGetProperty("metadata", out var metadata) ||
            !metadata.TryGetProperty("research_texture_export_target_component", out var component) ||
            component.ValueKind != JsonValueKind.String ||
            !string.Equals(component.GetString(), expectedTextureComponent, StringComparison.OrdinalIgnoreCase) ||
            !metadata.TryGetProperty("research_texture_export_target_expected_component", out var echoedExpected) ||
            echoedExpected.ValueKind != JsonValueKind.String ||
            !string.Equals(echoedExpected.GetString(), expectedTextureComponent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Research texture probe did not retain the receiver selected during discovery.");
        }
    }

    private static async Task<TimedReply> SendPaintWithTimingAsync(
        RuntimeBridgeService runtime,
        string payload)
    {
        var started = DateTimeOffset.UtcNow;
        var reply = await runtime.SendPaintAsync(payload);
        return new TimedReply(started, DateTimeOffset.UtcNow, reply);
    }

    private static async Task<TimedReply> CancelPaintAfterDelayAsync(
        RuntimeBridgeService runtime,
        int delayMs,
        Task paintTask)
    {
        await Task.Delay(delayMs);
        var started = DateTimeOffset.UtcNow;
        var reply = await runtime.CancelPaintAsync();
        for (var attempt = 0;
             attempt < 40 &&
             !paintTask.IsCompleted &&
             reply.Success &&
             HostSession.CancelledPaintJobCount(reply) is 0 &&
             !HostSession.NativePaintRequestCancellationLatched(reply);
             ++attempt)
        {
            await Task.Delay(25);
            reply = await runtime.CancelPaintAsync();
        }
        return new TimedReply(started, DateTimeOffset.UtcNow, reply);
    }

    private static async Task<TimedReply> ShutdownAfterDelayAsync(
        RuntimeBridgeService runtime,
        int delayMs)
    {
        await Task.Delay(delayMs);
        var started = DateTimeOffset.UtcNow;
        var reply = await runtime.ShutdownAsync();
        return new TimedReply(started, DateTimeOffset.UtcNow, reply);
    }

    private static object ReplySummary(BridgeReply reply) => new
    {
        ok = reply.Ok,
        success = reply.Success,
        stage = reply.Stage,
        message = reply.Message
    };

    private static bool IsPaintCancellationReply(BridgeReply reply) =>
        reply.Ok &&
        !reply.Success &&
        (reply.Stage.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
         reply.Message.Contains("paint cancelled", StringComparison.OrdinalIgnoreCase) ||
         reply.Message.Contains("paint canceled", StringComparison.OrdinalIgnoreCase));

    private static bool? ReplyMetadataBool(BridgeReply reply, string name)
    {
        if (string.IsNullOrWhiteSpace(reply.Raw))
            return null;
        try
        {
            using var document = JsonDocument.Parse(reply.Raw);
            if (!document.RootElement.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty(name, out var value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }
            return value.GetBoolean();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task HoldWithPressureSamplesAsync(
        HostSession session,
        Options options,
        string artifactDirectory)
    {
        if (options.HoldSeconds <= 0)
            return;
        if (options.PressureSampleMs <= 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
            return;
        }

        var pressureSample = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(options.HoldSeconds);
        var nextPressure = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(options.PressureSampleMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var now = DateTimeOffset.UtcNow;
            var delay = (nextPressure < deadline ? nextPressure : deadline) - now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            now = DateTimeOffset.UtcNow;
            if (now >= deadline)
                break;
            await CaptureProbeAsync(
                session,
                ResearchProbeKind.ReplicationPressure,
                $"pressure-sample-{pressureSample:D3}",
                artifactDirectory);
            ++pressureSample;
            nextPressure = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(options.PressureSampleMs);
        }
    }

    private static async Task<(bool Ready, string Stage)> WaitForEventWatchAsync(
        string path,
        ResearchBridgeIdentity expectedIdentity,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var stage = "missing";
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TryReadEventWatch(path, out var document))
            {
                using (document)
                {
                    stage = ReadStage(document.RootElement);
                    if (EventWatchReady(document.RootElement, expectedIdentity))
                        return (true, stage);
                }
            }
            await Task.Delay(100);
        }
        return (false, stage);
    }

    private static async Task<bool> WaitForEventWatchStoppedAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TryReadEventWatch(path, out var document))
            {
                using (document)
                {
                    if (string.Equals(ReadStage(document.RootElement), "event_watch_stopped", StringComparison.Ordinal))
                        return true;
                }
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static async Task<(bool Observed, string Component, int Calls)> WaitForMulticastPackedReceiverAsync(
        string path,
        ResearchBridgeIdentity expectedIdentity,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (TryReadEventWatch(path, out var document))
            {
                using (document)
                {
                    var root = document.RootElement;
                    if (EventWatchReady(root, expectedIdentity) &&
                        root.TryGetProperty("entries", out var entries) &&
                        entries.TryGetProperty("MulticastPackedPaintBatch", out var receiverEntry) &&
                        receiverEntry.TryGetProperty("calls", out var callsElement) &&
                        callsElement.TryGetInt32(out var calls) && calls > 0 &&
                        receiverEntry.TryGetProperty("last_process_event_object", out var receiverElement) &&
                        receiverElement.ValueKind == JsonValueKind.String &&
                        TryNormalizeNonZeroHexAddress(receiverElement.GetString(), out var receiver))
                    {
                        return (true, receiver, calls);
                    }
                }
            }
            await Task.Delay(100);
        }
        return (false, "", 0);
    }

    private static bool TryReadEventWatch(string path, out JsonDocument document)
    {
        document = null!;
        try
        {
            if (!File.Exists(path))
                return false;
            document = JsonDocument.Parse(File.ReadAllText(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool EventWatchReady(JsonElement root, ResearchBridgeIdentity expectedIdentity)
    {
        if (!root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var watchedPid) ||
            watchedPid != expectedIdentity.ProcessId)
            return false;
        if (!root.TryGetProperty("instance_id", out var instanceId) ||
            instanceId.ValueKind != JsonValueKind.String ||
            !string.Equals(instanceId.GetString(), expectedIdentity.InstanceId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!root.TryGetProperty("bridge_hash", out var bridgeHash) ||
            bridgeHash.ValueKind != JsonValueKind.String ||
            !string.Equals(bridgeHash.GetString(), expectedIdentity.BridgeHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!root.TryGetProperty("hook_slots", out var hooks) || !hooks.TryGetInt32(out var hookSlots) ||
            hookSlots <= 0)
            return false;
        if (!root.TryGetProperty("entries", out var entries) ||
            !entries.TryGetProperty("ServerPackedPaintBatch", out var packed) ||
            !packed.TryGetProperty("function", out var function))
        {
            return false;
        }
        return function.ValueKind == JsonValueKind.String && HasNonZeroHex(function.GetString());
    }

    private static bool HasNonZeroHex(string? value) =>
        TryNormalizeNonZeroHexAddress(value, out _);

    private static bool TryNormalizeNonZeroHexAddress(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3 ||
            value[0] != '0' || (value[1] != 'x' && value[1] != 'X') ||
            !ulong.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address) ||
            address == 0)
        {
            return false;
        }
        normalized = "0x" + address.ToString("x", CultureInfo.InvariantCulture);
        return true;
    }

    private static string ReadStage(JsonElement root) =>
        root.TryGetProperty("stage", out var stage) && stage.ValueKind == JsonValueKind.String
            ? stage.GetString() ?? "unknown"
            : "unknown";

    private static void SnapshotEventWatch(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
                File.Copy(source, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The live writer is best-effort. The runner's reply artifacts remain valid evidence.
        }
    }

    private static object PaintSettingsArtifact(AppSettings settings)
    {
        var paint = SettingsStore.Clamp(settings).Paint;
        return new
        {
            brush_1_size_texels = paint.Brush1SizeTexels,
            brush_2_size_texels = paint.Brush2SizeTexels,
            brush_pipeline_version = 2,
            packed_batch_limit = paint.PackedBatchLimit,
            packed_batch_pacing_ms = paint.PackedBatchPacingMs,
            coverage_step_texels = paint.CoverageStepTexels,
            front_region_mode = SettingsStore.RegionModeText(paint.FrontRegionMode),
            side_region_mode = SettingsStore.RegionModeText(paint.SideRegionMode),
            back_region_mode = SettingsStore.RegionModeText(paint.BackRegionMode),
            fill_color = paint.FillColor.ToHex()
        };
    }

    private static bool ResearchArtifactsEnabled() => BuildFeatures.ResearchArtifactsEnabled;

    private static string AddResearchPaintControls(string payload, Options options)
    {
        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("Normal paint payload was not a JSON object.");
        root["research_route_mode"] = options.PaintMode;
        root["research_stroke_limit"] = options.StrokeLimit;
        root["research_uv_replay_atlas"] = true;
        if (options.ReplayStrokeIndex is int replayStrokeIndex)
            root["research_replay_stroke_index"] = replayStrokeIndex;
        if (options.PackedRadiusScaleOverride is double packedRadiusScaleOverride)
            root["research_packed_radius_scale"] = packedRadiusScaleOverride;
        root["research_triangle_world_radius"] = options.TriangleWorldRadius;
        if (options.PaintColorOverride is RgbColor paintColor)
        {
            root["research_force_paint_color"] = true;
            root["research_paint_color_r"] = paintColor.R / 255.0;
            root["research_paint_color_g"] = paintColor.G / 255.0;
            root["research_paint_color_b"] = paintColor.B / 255.0;
        }
        return root.ToJsonString() + Environment.NewLine;
    }

    private static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var paint = false;
        var textureSnapshot = false;
        var triangleWorldRadius = false;
        var requested = false;
        for (var index = 0; index < args.Length; ++index)
        {
            switch (args[index])
            {
                case "--research-replication":
                    requested = true;
                    break;
                case "--paint":
                    paint = true;
                    break;
                case "--texture-snapshot":
                    textureSnapshot = true;
                    break;
                case "--triangle-world-radius":
                    triangleWorldRadius = true;
                    break;
                case "--pid":
                case "--role":
                case "--out":
                case "--hold-seconds":
                case "--pressure-sample-ms":
                case "--texture-target":
                case "--texture-discovery-seconds":
                case "--paint-mode":
                case "--packed-radius-scale":
                case "--batch-limit":
                case "--batch-pacing-ms":
                case "--stroke-limit":
                case "--replay-stroke-index":
                case "--cancel-after-ms":
                case "--shutdown-after-ms":
                case "--fill-color":
                case "--paint-color":
                case "--front-mode":
                case "--side-mode":
                case "--back-mode":
                    if (++index >= args.Length)
                        throw new ArgumentException($"Missing value for {args[index - 1]}.");
                    values[args[index - 1]] = args[index];
                    break;
                default:
                    throw new ArgumentException("Unknown research argument: " + args[index]);
            }
        }

        if (!requested)
            throw new ArgumentException("--research-replication is required.");
        if (!values.TryGetValue("--pid", out var pidText) ||
            !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
        {
            throw new ArgumentException("--pid must be a positive decimal process ID.");
        }
        if (!values.TryGetValue("--role", out var role) ||
            (role != "host" && role != "joining-client"))
        {
            throw new ArgumentException("--role must be host or joining-client. This is a declared test label, not a detected network role.");
        }
        if (!values.TryGetValue("--out", out var output) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--out is required.");

        var holdSeconds = 15;
        if (values.TryGetValue("--hold-seconds", out var holdText) &&
            (!int.TryParse(holdText, NumberStyles.None, CultureInfo.InvariantCulture, out holdSeconds) || holdSeconds < 0 || holdSeconds > 600))
        {
            throw new ArgumentException("--hold-seconds must be an integer from 0 through 600.");
        }

        var pressureSampleMs = 0;
        if (values.TryGetValue("--pressure-sample-ms", out var sampleText) &&
            (!int.TryParse(sampleText, NumberStyles.None, CultureInfo.InvariantCulture, out pressureSampleMs) ||
             (pressureSampleMs != 0 && (pressureSampleMs < 250 || pressureSampleMs > 10_000))))
        {
            throw new ArgumentException("--pressure-sample-ms must be 0 or an integer from 250 through 10000.");
        }

        var textureTarget = values.GetValueOrDefault("--texture-target", "resolved") switch
        {
            "resolved" => ResearchTextureTarget.ResolvedComponent,
            "eventwatch-multicast-packed-receiver" => ResearchTextureTarget.EventwatchMulticastPackedReceiver,
            _ => throw new ArgumentException(
                "--texture-target must be resolved or eventwatch-multicast-packed-receiver.")
        };
        if (textureTarget != ResearchTextureTarget.ResolvedComponent && !textureSnapshot)
        {
            throw new ArgumentException("an event-watch --texture-target requires --texture-snapshot.");
        }
        var textureDiscoverySeconds = 0;
        if (values.TryGetValue("--texture-discovery-seconds", out var discoveryText) &&
            (!int.TryParse(discoveryText, NumberStyles.None, CultureInfo.InvariantCulture, out textureDiscoverySeconds) ||
             textureDiscoverySeconds < 1 || textureDiscoverySeconds > 120))
        {
            throw new ArgumentException("--texture-discovery-seconds must be an integer from 1 through 120.");
        }
        if (textureDiscoverySeconds > 0 && textureTarget == ResearchTextureTarget.ResolvedComponent)
        {
            throw new ArgumentException("--texture-discovery-seconds requires an event-watch --texture-target.");
        }
        if (textureTarget != ResearchTextureTarget.ResolvedComponent && textureDiscoverySeconds == 0)
        {
            throw new ArgumentException("an event-watch --texture-target requires --texture-discovery-seconds.");
        }

        var paintMode = values.GetValueOrDefault("--paint-mode", "packed-local-queue");
        if (paintMode != "combined" && paintMode != "combined-no-resend" &&
            paintMode != "local-only" && paintMode != "packed-only" &&
            paintMode != "packed-local-queue")
        {
            throw new ArgumentException("--paint-mode must be combined, combined-no-resend, local-only, packed-only, or packed-local-queue.");
        }

        double? packedRadiusScaleOverride = null;
        if (values.TryGetValue("--packed-radius-scale", out var packedRadiusScaleText))
        {
            if (!double.TryParse(packedRadiusScaleText, NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out var parsedPackedRadiusScale) ||
                !double.IsFinite(parsedPackedRadiusScale) ||
                parsedPackedRadiusScale < 0.5 || parsedPackedRadiusScale > 4.0)
            {
                throw new ArgumentException("--packed-radius-scale must be a number from 0.5 through 4.0.");
            }
            packedRadiusScaleOverride = parsedPackedRadiusScale;
        }
        if (triangleWorldRadius && !paint)
            throw new ArgumentException("--triangle-world-radius requires --paint.");
        if (triangleWorldRadius && paintMode == "local-only")
            throw new ArgumentException("--triangle-world-radius requires a packed paint mode.");
        if (triangleWorldRadius && packedRadiusScaleOverride is not null)
            throw new ArgumentException("--triangle-world-radius and --packed-radius-scale are mutually exclusive.");
        if (packedRadiusScaleOverride is not null && !paint)
            throw new ArgumentException("--packed-radius-scale requires --paint.");

        int? packedBatchLimitOverride = null;
        if (values.TryGetValue("--batch-limit", out var packedBatchLimitText))
        {
            if (!int.TryParse(packedBatchLimitText, NumberStyles.None, CultureInfo.InvariantCulture,
                              out var parsedPackedBatchLimit) ||
                parsedPackedBatchLimit < 1 || parsedPackedBatchLimit > 20)
            {
                throw new ArgumentException("--batch-limit must be an integer from 1 through 20.");
            }
            packedBatchLimitOverride = parsedPackedBatchLimit;
        }

        int? packedBatchPacingOverrideMs = null;
        if (values.TryGetValue("--batch-pacing-ms", out var packedBatchPacingText))
        {
            if (!int.TryParse(packedBatchPacingText, NumberStyles.None, CultureInfo.InvariantCulture,
                              out var parsedPackedBatchPacingMs) ||
                parsedPackedBatchPacingMs < 50 || parsedPackedBatchPacingMs > 500)
            {
                throw new ArgumentException("--batch-pacing-ms must be an integer from 50 through 500.");
            }
            packedBatchPacingOverrideMs = parsedPackedBatchPacingMs;
        }

        var strokeLimit = 0;
        if (values.TryGetValue("--stroke-limit", out var strokeLimitText) &&
            (!int.TryParse(strokeLimitText, NumberStyles.None, CultureInfo.InvariantCulture, out strokeLimit) ||
             strokeLimit < 0 || strokeLimit > 100_000))
        {
            throw new ArgumentException("--stroke-limit must be an integer from 0 through 100000; 0 means unlimited.");
        }

        int? replayStrokeIndex = null;
        if (values.TryGetValue("--replay-stroke-index", out var replayStrokeIndexText))
        {
            if (!int.TryParse(replayStrokeIndexText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedReplayStrokeIndex) ||
                parsedReplayStrokeIndex < 0 || parsedReplayStrokeIndex > 100_000)
            {
                throw new ArgumentException("--replay-stroke-index must be an integer from 0 through 100000.");
            }
            replayStrokeIndex = parsedReplayStrokeIndex;
        }
        if (replayStrokeIndex is not null && !paint)
            throw new ArgumentException("--replay-stroke-index requires --paint.");

        int? cancelAfterMs = null;
        if (values.TryGetValue("--cancel-after-ms", out var cancelAfterText))
        {
            if (!int.TryParse(cancelAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCancelAfterMs) ||
                parsedCancelAfterMs < 1 || parsedCancelAfterMs > 60_000)
            {
                throw new ArgumentException("--cancel-after-ms must be an integer from 1 through 60000.");
            }
            cancelAfterMs = parsedCancelAfterMs;
        }

        int? shutdownAfterMs = null;
        if (values.TryGetValue("--shutdown-after-ms", out var shutdownAfterText))
        {
            if (!int.TryParse(shutdownAfterText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedShutdownAfterMs) ||
                parsedShutdownAfterMs < 1 || parsedShutdownAfterMs > 60_000)
            {
                throw new ArgumentException("--shutdown-after-ms must be an integer from 1 through 60000.");
            }
            shutdownAfterMs = parsedShutdownAfterMs;
        }

        if (cancelAfterMs is not null && shutdownAfterMs is not null)
            throw new ArgumentException("--cancel-after-ms and --shutdown-after-ms are mutually exclusive.");
        if (cancelAfterMs is not null && !paint)
            throw new ArgumentException("--cancel-after-ms requires --paint.");
        if (shutdownAfterMs is not null && !paint)
            throw new ArgumentException("--shutdown-after-ms requires --paint.");
        if (textureSnapshot && shutdownAfterMs is not null)
        {
            throw new ArgumentException(
                "--texture-snapshot cannot be combined with --shutdown-after-ms because it cannot safely capture an after image.");
        }
        RgbColor? fillColorOverride = null;
        if (values.TryGetValue("--fill-color", out var fillColorText))
        {
            if (!RgbColor.TryParse(fillColorText, out var parsedFillColor))
                throw new ArgumentException("--fill-color must be a six-digit RGB color such as #FF00AA.");
            fillColorOverride = parsedFillColor;
        }

        RgbColor? paintColorOverride = null;
        if (values.TryGetValue("--paint-color", out var paintColorText))
        {
            if (!RgbColor.TryParse(paintColorText, out var parsedPaintColor))
                throw new ArgumentException("--paint-color must be a six-digit RGB color such as #00FF00.");
            paintColorOverride = parsedPaintColor;
        }

        static RegionMode? ParseRegionModeOverride(
            IReadOnlyDictionary<string, string> parsedValues,
            string option)
        {
            if (!parsedValues.TryGetValue(option, out var text))
                return null;
            if (!Enum.TryParse<RegionMode>(text, true, out var mode))
                throw new ArgumentException($"{option} must be paint, fill, or skip.");
            return mode;
        }

        var frontRegionModeOverride = ParseRegionModeOverride(values, "--front-mode");
        var sideRegionModeOverride = ParseRegionModeOverride(values, "--side-mode");
        var backRegionModeOverride = ParseRegionModeOverride(values, "--back-mode");

        return new Options(
            processId,
            role,
            Path.GetFullPath(output),
            paint,
            holdSeconds,
            pressureSampleMs,
            textureSnapshot,
            textureTarget,
            textureDiscoverySeconds,
            paintMode,
            packedRadiusScaleOverride,
            triangleWorldRadius,
            packedBatchLimitOverride,
            packedBatchPacingOverrideMs,
            strokeLimit,
            replayStrokeIndex,
            cancelAfterMs,
            shutdownAfterMs,
            fillColorOverride,
            paintColorOverride,
            frontRegionModeOverride,
            sideRegionModeOverride,
            backRegionModeOverride);
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine);
    }
}
