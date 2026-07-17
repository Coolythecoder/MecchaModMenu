#pragma once

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cmath>
#include <set>
#include <tuple>
#include <vector>

namespace runtime_contract
{
    // Supported Shipping build: FProperty ArrayDim@0x30,
    // ElementSize@0x34, PropertyFlags@0x38.
    constexpr std::size_t FPropertyElementSizeOffset = 0x34;
    constexpr int InternalNoResendMaxCallsPerTick = 6;
    constexpr int MaximumNetworkBatchLimit = 20;
    constexpr int FastLocalCadenceMs = 17;
    constexpr int FallbackOutgoingStrokesPerBatch = 20;
    constexpr int FallbackOutgoingBatchesPerSecond = 20;
    constexpr int FallbackReplicatedStrokesPerTick = 24;
    constexpr int FallbackRenderTargetWritesPerFrame = 6;
    constexpr int MinimumNetworkPacingMs = 50;
    constexpr int MaximumManualNetworkPacingMs = 500;
    constexpr std::uint64_t LocalDispatchCpuBudgetUs = 4'000;
    constexpr int SupportedBrushPipelineVersion = 2;

    // Packed skeletal strokes carry both a UV-space brush radius and an optional
    // world-space radius.  In the supported Shipping build, compact-stroke
    // expansion calls the skeletal preflight (update2.8.0 RVAs
    // 0x50F63B0 -> 0x50F5F20; prior supported Steam build
    // 0x50F65A0 -> 0x50F6110).
    // A non-positive world radius is the sentinel that asks that preflight to
    // derive the world radius from the mesh bounds and UV radius.  Supplying the
    // normalized UV radius here is not equivalent: it suppresses that conversion
    // and collapses otherwise large brushes to tiny world-space dots.
    constexpr float PackedMeshAnchorWorldRadiusAuto = 0.0f;
    // The supported paint mesh's cached world geometry and TargetMesh bounds
    // use different linear scales.  The native packed preflight multiplies the
    // normalized brush radius by TargetMesh bounds diameter, which produced a
    // 6--7 px footprint for a requested 10 texel radius.  Host RGBA A/B against
    // the direct-UV route found 3.4 to be the closest conservative calibration;
    // 4.0 and per-triangle maxima leaked onto non-Back atlas areas.
    // World-sphere painting begins to cross nearby folded surfaces before it
    // exactly matches a direct-UV circle.  Host mask A/B selected this
    // conservative factor (3.4/4.0 outperformed both 4.0 and per-anchor max).
    constexpr double PackedMeshAnchorCoverageSafetyFactor = 0.91;
    constexpr double PackedMeshAnchorExpectedRadiusCalibration = 3.5;
    constexpr int PackedMeshAnchorSubdivisionLevelAuto = 0;
    constexpr float PackedMeshAnchorSubdivisionPixelSizeAuto = 0.0f;
    constexpr int PackedMeshAnchorTemplateResolutionAuto = 0;

    constexpr bool packed_mesh_anchor_requests_world_radius_conversion(float effective_world_radius)
    {
        return effective_world_radius <= 0.0f;
    }

    inline bool packed_mesh_anchor_world_radius_contract_valid(float effective_world_radius,
                                                               float brush_uv_radius)
    {
        if (packed_mesh_anchor_requests_world_radius_conversion(effective_world_radius))
        {
            return true;
        }
        // A derived radius is expressed in world units and must be finite and
        // materially distinct from the normalized UV radius.  This rejects the
        // historical bug that copied BrushSettings.Radius into both fields.
        return std::isfinite(effective_world_radius) &&
               std::isfinite(brush_uv_radius) &&
               brush_uv_radius > 0.0f &&
               effective_world_radius > brush_uv_radius;
    }

    // BrushSettings.Radius is kept in planner/direct-UV units.  Only the packed
    // wire representation receives the mesh calibration so research direct and
    // internal-common routes cannot accidentally inherit the packed receiver's
    // normalization.  Returning the rounded float lets validation and encoding
    // use the exact same value without mutating the source stroke.
    inline bool resolve_packed_wire_brush_radius(float source_uv_radius,
                                                 double packed_wire_scale,
                                                 float& wire_uv_radius)
    {
        wire_uv_radius = 0.0f;
        if (!std::isfinite(source_uv_radius) || source_uv_radius <= 0.0f ||
            source_uv_radius > 1.0f || !std::isfinite(packed_wire_scale) ||
            packed_wire_scale <= 0.0)
        {
            return false;
        }
        const double scaled = static_cast<double>(source_uv_radius) * packed_wire_scale;
        if (!std::isfinite(scaled) || scaled <= 0.0 || scaled > 1.0)
        {
            return false;
        }
        const float rounded = static_cast<float>(scaled);
        if (!std::isfinite(rounded) || rounded <= 0.0f || rounded > 1.0f)
        {
            return false;
        }
        wire_uv_radius = rounded;
        return true;
    }

    // The compact decoder copies these fields verbatim into FPaintStroke before
    // running the skeletal-stroke preflight.  A non-positive value asks that
    // preflight to populate the component's native subdivision contract.  Do
    // not synthesize brush diameter or texture size into these wire fields.
    constexpr bool packed_mesh_anchor_requests_native_subdivision_preflight(
        int effective_subdivision_level,
        float effective_subdivision_pixel_size,
        int effective_template_resolution)
    {
        return effective_subdivision_level <= 0 &&
               effective_subdivision_pixel_size <= 0.0f &&
               effective_template_resolution <= 0;
    }

    // Receiver order in packed record bytes 23..26 (decoded into compact-stroke
    // fields at later offsets): subdivision level u8, subdivision pixel size
    // u8, template resolution u16 LE.
    constexpr std::array<std::uint8_t, 4> packed_mesh_anchor_auto_subdivision_tail()
    {
        return {0, 0, 0, 0};
    }

    // UObject flags are checked against Shipping disassembly for the supported
    // build.  0x20000000 is intentionally not rejected: it is not an object
    // destruction flag and treating it as one spuriously blocks live objects.
    constexpr std::uint32_t RFClassDefaultObject = 0x00000010u;
    constexpr std::uint32_t RFBeginDestroyed = 0x00008000u;
    constexpr std::uint32_t RFFinishDestroyed = 0x00010000u;
    constexpr std::uint32_t RFMirroredGarbage = 0x40000000u;
    constexpr std::uint32_t ObjectRejectMask =
        RFClassDefaultObject | RFBeginDestroyed | RFFinishDestroyed | RFMirroredGarbage;
    constexpr std::uint32_t ClassRejectMask = RFBeginDestroyed | RFFinishDestroyed | RFMirroredGarbage;

    constexpr bool uobject_flags_usable(std::uint32_t object_flags, std::uint32_t class_flags)
    {
        return (object_flags & ObjectRejectMask) == 0 && (class_flags & ClassRejectMask) == 0;
    }

    struct BrushPipelineVersionDecision
    {
        double requested_version;
        int supported_version;
        bool required;
        bool supported;
    };

    constexpr BrushPipelineVersionDecision resolve_brush_pipeline_version(
        double requested_version,
        bool preview_only,
        bool unpreview_only)
    {
        const bool required = !preview_only && !unpreview_only;
        return {requested_version,
                SupportedBrushPipelineVersion,
                required,
                !required || requested_version == static_cast<double>(SupportedBrushPipelineVersion)};
    }

    constexpr bool packed_manager_precommit_matches(std::uintptr_t captured_manager,
                                                     std::uintptr_t resolved_manager)
    {
        return captured_manager != 0 && resolved_manager == captured_manager;
    }

    constexpr bool paired_paint_cancel_safe_to_observe(bool paired_mode,
                                                       int server_strokes_sent,
                                                       int local_strokes_submitted)
    {
        return !paired_mode || server_strokes_sent == local_strokes_submitted;
    }

    struct PacingDecision
    {
        int remote_batch_limit;
        int remote_delay_ms;
        int local_batch_limit;
        int local_delay_ms;
        bool used_contract_fallback;
    };

    constexpr int positive_or(int value, int fallback)
    {
        return value > 0 ? value : fallback;
    }

    constexpr int min_value(int left, int right)
    {
        return left < right ? left : right;
    }

    constexpr int max_value(int left, int right)
    {
        return left > right ? left : right;
    }

    constexpr int clamp_value(int value, int minimum, int maximum)
    {
        return min_value(max_value(value, minimum), maximum);
    }

    constexpr int ceil_div(int numerator, int denominator)
    {
        return denominator > 0 ? (numerator + denominator - 1) / denominator : numerator;
    }

    struct BilinearAxisSample
    {
        int lower{0};
        int upper{0};
        double fraction{0.0};
    };

    struct BilinearPixelSample
    {
        BilinearAxisSample x{};
        BilinearAxisSample y{};
    };

    // Projection produces coordinates in pixel-edge space: 0 is the left/top
    // texture edge and width/height is the opposite edge.  Resolve those
    // coordinates against pixel centres before interpolating so subpixel paint
    // samples do not jump whenever std::round crosses an integer boundary.
    inline bool resolve_bilinear_pixel_sample(double x,
                                              double y,
                                              int width,
                                              int height,
                                              bool flip_x,
                                              bool flip_y,
                                              BilinearPixelSample& sample)
    {
        sample = {};
        if (width <= 0 || height <= 0 ||
            !std::isfinite(x) || !std::isfinite(y) ||
            x < 0.0 || y < 0.0 ||
            x >= static_cast<double>(width) ||
            y >= static_cast<double>(height))
        {
            return false;
        }

        const auto resolve_axis = [](double coordinate,
                                     int extent,
                                     bool flipped) -> BilinearAxisSample {
            const double oriented = flipped
                                        ? static_cast<double>(extent) - coordinate
                                        : coordinate;
            const double centred = std::clamp(oriented - 0.5,
                                              0.0,
                                              static_cast<double>(extent - 1));
            const int lower = static_cast<int>(std::floor(centred));
            const int upper = std::min(extent - 1, lower + 1);
            return {lower, upper, centred - static_cast<double>(lower)};
        };

        sample.x = resolve_axis(x, width, flip_x);
        sample.y = resolve_axis(y, height, flip_y);
        return true;
    }

    inline double bilinear_pixel_value(double top_left,
                                       double top_right,
                                       double bottom_left,
                                       double bottom_right,
                                       const BilinearPixelSample& sample)
    {
        const double top = top_left + (top_right - top_left) * sample.x.fraction;
        const double bottom = bottom_left + (bottom_right - bottom_left) * sample.x.fraction;
        return top + (bottom - top) * sample.y.fraction;
    }

    inline double srgb_to_linear_unit(double value)
    {
        const double srgb = std::max(0.0, std::min(1.0, value));
        return srgb <= 0.04045
                   ? srgb / 12.92
                   : std::pow((srgb + 0.055) / 1.055, 2.4);
    }

    inline double linear_to_srgb_unit(double value)
    {
        const double linear = std::max(0.0, std::min(1.0, value));
        return linear <= 0.0031308
                   ? linear * 12.92
                   : 1.055 * std::pow(linear, 1.0 / 2.4) - 0.055;
    }

    inline double bilinear_srgb_pixel_value(double top_left,
                                            double top_right,
                                            double bottom_left,
                                            double bottom_right,
                                            const BilinearPixelSample& sample)
    {
        const double linear = bilinear_pixel_value(
            srgb_to_linear_unit(top_left),
            srgb_to_linear_unit(top_right),
            srgb_to_linear_unit(bottom_left),
            srgb_to_linear_unit(bottom_right),
            sample);
        return linear_to_srgb_unit(linear);
    }

    constexpr PacingDecision resolve_pacing(int requested_batch_limit,
                                            int requested_pacing_ms,
                                            int max_outgoing_strokes_per_batch,
                                            int max_outgoing_network_batches_per_second,
                                            int max_replicated_strokes_per_tick,
                                            int max_render_target_writes_per_frame)
    {
        const bool fallback = max_outgoing_strokes_per_batch <= 0 ||
                              max_outgoing_network_batches_per_second <= 0 ||
                              max_replicated_strokes_per_tick <= 0 ||
                              max_render_target_writes_per_frame <= 0;
        const int outgoing_strokes = positive_or(max_outgoing_strokes_per_batch, FallbackOutgoingStrokesPerBatch);
        const int outgoing_batches = positive_or(max_outgoing_network_batches_per_second, FallbackOutgoingBatchesPerSecond);
        const int replicated_strokes = positive_or(max_replicated_strokes_per_tick, FallbackReplicatedStrokesPerTick);
        const int render_writes = positive_or(max_render_target_writes_per_frame, FallbackRenderTargetWritesPerFrame);
        const int requested_batch = clamp_value(requested_batch_limit, 1, MaximumNetworkBatchLimit);
        const int requested_delay = clamp_value(requested_pacing_ms,
                                                MinimumNetworkPacingMs,
                                                MaximumManualNetworkPacingMs);
        const int automatic_remote_batch = min_value(requested_batch, min_value(outgoing_strokes, replicated_strokes));
        const int automatic_remote_delay = max_value(requested_delay,
                                                     max_value(MinimumNetworkPacingMs,
                                                               ceil_div(1000, outgoing_batches)));
        const int local_batch = min_value(InternalNoResendMaxCallsPerTick, render_writes);
        return {automatic_remote_batch,
                automatic_remote_delay,
                local_batch,
                FastLocalCadenceMs,
                fallback};
    }

    // EPaintChannel: 0..3 address one render target, All addresses four, and
    // AlbedoMetallicRoughness addresses three.  The game limit is expressed in
    // render-target writes, not paint-stroke calls.
    constexpr int paint_channel_write_cost(int target_channel)
    {
        return target_channel == 4 ? 4 : (target_channel == 5 ? 3 : 1);
    }

    constexpr bool local_dispatch_can_append(int processed_calls,
                                             int scheduled_writes,
                                             int next_write_cost,
                                             int max_calls,
                                             int max_render_target_writes)
    {
        if (processed_calls <= 0)
        {
            return true;
        }
        return processed_calls < max_value(1, max_calls) &&
               scheduled_writes + max_value(1, next_write_cost) <=
                   max_value(1, max_render_target_writes);
    }

    constexpr bool local_dispatch_cpu_budget_reached(int processed_calls,
                                                     std::uint64_t elapsed_us)
    {
        return processed_calls > 0 && elapsed_us >= LocalDispatchCpuBudgetUs;
    }

    constexpr int recurring_scheduler_delay_ms(int requested_delay_ms)
    {
        return max_value(1, requested_delay_ms);
    }

    // The paired packed path must never place more than one configured batch
    // ahead of the painter's exact component queue.  An unavailable queue
    // observation is unsafe: do not submit another paired server/local batch.
    constexpr int paired_local_queue_available_capacity(int configured_batch_limit,
                                                        int queued_strokes)
    {
        if (queued_strokes < 0)
        {
            return 0;
        }
        return max_value(0, max_value(1, configured_batch_limit) - queued_strokes);
    }

    constexpr int paired_local_queue_commit_count(int requested_strokes,
                                                  int configured_batch_limit,
                                                  int queued_strokes)
    {
        return min_value(max_value(0, requested_strokes),
                         paired_local_queue_available_capacity(configured_batch_limit, queued_strokes));
    }

    constexpr bool paired_local_queue_cancel_needs_drain(bool cancel_requested,
                                                         int queued_strokes)
    {
        return cancel_requested && queued_strokes > 0;
    }

    // The supported manager appends strokes in order and advances one processed
    // cursor.  Once a job owns an initially empty component queue, submitted -
    // queued is therefore a conservative render-queue cursor.  Preserve the
    // previous value so a transient foreign queue increase cannot move UI pass
    // progress backwards.
    constexpr int receiver_queue_rendered_strokes(int submitted_strokes,
                                                   int queued_strokes,
                                                   int previous_rendered_strokes)
    {
        const int submitted = max_value(0, submitted_strokes);
        const int queued = max_value(0, queued_strokes);
        const int observed = clamp_value(submitted - queued, 0, submitted);
        return clamp_value(max_value(previous_rendered_strokes, observed), 0, submitted);
    }

    constexpr bool receiver_queue_drain_complete(int queued_strokes,
                                                  int consecutive_zero_observations)
    {
        return queued_strokes == 0 && consecutive_zero_observations >= 2;
    }

    constexpr bool receiver_queue_idle_threshold_reached(int queued_strokes,
                                                          std::uint64_t idle_ms,
                                                          std::uint64_t threshold_ms)
    {
        return queued_strokes > 0 && threshold_ms > 0 && idle_ms >= threshold_ms;
    }

    struct SpatialScanlineKey
    {
        int row;
        double horizontal;
        std::size_t original_ordinal;
    };

    inline int spatial_scanline_row(double top_z, double point_z, double row_height)
    {
        if (!std::isfinite(top_z) || !std::isfinite(point_z) ||
            !std::isfinite(row_height) || row_height <= 0.000001)
        {
            return 0;
        }
        return static_cast<int>(std::floor(std::max(0.0, top_z - point_z) / row_height));
    }

    inline bool spatial_scanline_less(const SpatialScanlineKey& left,
                                      const SpatialScanlineKey& right)
    {
        if (left.row != right.row)
        {
            return left.row < right.row;
        }
        if (left.horizontal != right.horizontal)
        {
            return left.horizontal < right.horizontal;
        }
        return left.original_ordinal < right.original_ordinal;
    }

    enum class ReplayRegion
    {
        Front,
        Side,
        Back,
    };

    enum class ReplayRegionMode
    {
        Paint,
        Fill,
        Skip,
    };

    enum class ReplayPass
    {
        Fill,
        CoarsePaint,
        FinePaint,
        Complete,
    };

    struct ReplayPassWindow
    {
        ReplayPass pass;
        std::size_t begin;
        std::size_t end;
    };

    // Resolve the pass containing an offset in the effective replay stream.  The
    // planner stores exclusive boundaries, so an offset exactly at a boundary is
    // reported as the next pass.  Clamp malformed boundaries here so diagnostics
    // remain safe even when a runtime limit truncates the planned stream.
    constexpr ReplayPassWindow replay_pass_window(std::size_t offset,
                                                  std::size_t total,
                                                  std::size_t fill_end,
                                                  std::size_t coarse_end)
    {
        const std::size_t safe_fill_end = std::min(fill_end, total);
        const std::size_t safe_coarse_end = std::min(std::max(coarse_end, safe_fill_end), total);
        const std::size_t safe_offset = std::min(offset, total);
        if (safe_offset >= total)
        {
            return {ReplayPass::Complete, total, total};
        }
        if (safe_offset < safe_fill_end)
        {
            return {ReplayPass::Fill, 0, safe_fill_end};
        }
        if (safe_offset < safe_coarse_end)
        {
            return {ReplayPass::CoarsePaint, safe_fill_end, safe_coarse_end};
        }
        return {ReplayPass::FinePaint, safe_coarse_end, total};
    }

    struct TwoBrushReplayCandidate
    {
        std::size_t sample_index;
        ReplayRegion region;
        ReplayRegionMode mode;
        int uv_island;
        double u;
        double v;
        bool has_reference_position;
        double reference_z;
        double fallback_z;
        double horizontal;
        std::size_t original_ordinal;
    };

    struct TwoBrushReplayEntry
    {
        std::size_t sample_index;
        ReplayPass pass;
        ReplayRegion region;
        SpatialScanlineKey spatial_key;
    };

    struct TwoBrushReplayPlan
    {
        std::vector<TwoBrushReplayEntry> entries{};
        std::size_t fill_end{0};
        std::size_t coarse_end{0};
        std::size_t fill_count{0};
        std::size_t coarse_paint_count{0};
        std::size_t fine_paint_count{0};
        std::size_t fill_candidates{0};
        std::size_t fill_deduplicated{0};
        std::size_t coarse_paint_candidates{0};
        std::size_t coarse_paint_deduplicated{0};
        bool reference_position_fallback_used{false};
        std::size_t reference_position_fallback_candidates{0};
    };

    inline TwoBrushReplayPlan build_two_brush_replay_plan(
        const std::vector<TwoBrushReplayCandidate>& candidates,
        int texture_size,
        double brush_1_size_texels,
        double brush_2_size_texels,
        double fill_radius_texels)
    {
        TwoBrushReplayPlan plan{};
        constexpr ReplayRegion region_order[]{ReplayRegion::Back,
                                               ReplayRegion::Side,
                                               ReplayRegion::Front};
        const double texture_size_double = static_cast<double>(max_value(1, texture_size));
        const double fill_cell_uv = std::max(fill_radius_texels * 0.75, brush_2_size_texels) /
                                    texture_size_double;
        const double coarse_cell_uv = brush_1_size_texels / texture_size_double;
        double vertical_top = 0.0;
        double vertical_bottom = 0.0;
        bool have_vertical_bounds = false;
        const auto selected_vertical = [](const TwoBrushReplayCandidate& candidate) {
            return candidate.has_reference_position && std::isfinite(candidate.reference_z)
                       ? candidate.reference_z
                       : (std::isfinite(candidate.fallback_z) ? candidate.fallback_z : 0.0);
        };
        for (const auto& candidate : candidates)
        {
            if (candidate.mode == ReplayRegionMode::Skip)
            {
                continue;
            }
            const double vertical = selected_vertical(candidate);
            if (!have_vertical_bounds)
            {
                vertical_top = vertical;
                vertical_bottom = vertical;
                have_vertical_bounds = true;
            }
            else
            {
                vertical_top = std::max(vertical_top, vertical);
                vertical_bottom = std::min(vertical_bottom, vertical);
            }
            if (!candidate.has_reference_position || !std::isfinite(candidate.reference_z))
            {
                ++plan.reference_position_fallback_candidates;
            }
        }
        plan.reference_position_fallback_used = plan.reference_position_fallback_candidates > 0;
        const double vertical_span = std::max(0.001, vertical_top - vertical_bottom);
        auto append_pass = [&](ReplayPass pass,
                               ReplayRegionMode required_mode,
                               double dedupe_cell_uv,
                               double row_size_texels) {
            std::set<std::tuple<int, int, int, int>> emitted_cells{};
            const double row_height = std::max(
                0.000001,
                vertical_span * std::max(0.001, row_size_texels) / texture_size_double);
            for (const auto region : region_order)
            {
                std::vector<TwoBrushReplayEntry> pending{};
                for (const auto& candidate : candidates)
                {
                    if (candidate.region != region || candidate.mode != required_mode)
                    {
                        continue;
                    }
                    if (pass == ReplayPass::Fill)
                    {
                        ++plan.fill_candidates;
                    }
                    else if (pass == ReplayPass::CoarsePaint)
                    {
                        ++plan.coarse_paint_candidates;
                    }
                    if (dedupe_cell_uv > 0.000001)
                    {
                        const auto cell_coordinate = [&](double value) {
                            const double finite_value = std::isfinite(value) ? value : 0.0;
                            return static_cast<int>(std::floor(
                                std::max(0.0, std::min(1.0, finite_value)) / dedupe_cell_uv));
                        };
                        const auto cell = std::make_tuple(
                            static_cast<int>(candidate.region),
                            candidate.uv_island,
                            cell_coordinate(candidate.u),
                            cell_coordinate(candidate.v));
                        if (!emitted_cells.insert(cell).second)
                        {
                            if (pass == ReplayPass::Fill)
                            {
                                ++plan.fill_deduplicated;
                            }
                            else if (pass == ReplayPass::CoarsePaint)
                            {
                                ++plan.coarse_paint_deduplicated;
                            }
                            continue;
                        }
                    }
                    pending.push_back(
                        {candidate.sample_index,
                         pass,
                         candidate.region,
                         {spatial_scanline_row(vertical_top,
                                               selected_vertical(candidate),
                                               row_height),
                          candidate.horizontal,
                          candidate.original_ordinal}});
                }
                std::stable_sort(pending.begin(), pending.end(), [](const auto& left, const auto& right) {
                    return spatial_scanline_less(left.spatial_key, right.spatial_key);
                });
                plan.entries.insert(plan.entries.end(), pending.begin(), pending.end());
            }
        };

        append_pass(ReplayPass::Fill, ReplayRegionMode::Fill, fill_cell_uv, fill_radius_texels);
        plan.fill_end = plan.entries.size();
        plan.fill_count = plan.fill_end;
        append_pass(ReplayPass::CoarsePaint,
                    ReplayRegionMode::Paint,
                    coarse_cell_uv,
                    brush_1_size_texels);
        plan.coarse_end = plan.entries.size();
        plan.coarse_paint_count = plan.coarse_end - plan.fill_end;
        append_pass(ReplayPass::FinePaint,
                    ReplayRegionMode::Paint,
                    0.0,
                    brush_2_size_texels);
        plan.fine_paint_count = plan.entries.size() - plan.coarse_end;
        return plan;
    }

    constexpr bool event_watch_generation_active(bool enabled,
                                                 std::uint64_t current_generation,
                                                 std::uint64_t captured_generation)
    {
        return enabled && current_generation == captured_generation;
    }

    constexpr bool requires_internal_no_resend(bool preview_only,
                                                bool unpreview_only,
                                                bool research_local_only,
                                                bool research_packed_only)
    {
        const bool packed_server_route = !preview_only && !unpreview_only && !research_local_only;
        const bool local_visual_apply = !preview_only && !unpreview_only && !research_packed_only;
        return packed_server_route && local_visual_apply;
    }
}
