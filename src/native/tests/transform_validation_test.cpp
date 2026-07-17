#include "../include/sdk.hpp"
#include "../include/runtime_contract.hpp"

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>
#include <vector>

int main()
{
    sdk::FTransform valid{};
    valid.Rotation = {0.0, 0.0, 0.705717, 0.708494};
    valid.Translation = {-295.483835, 6223.716973, 8.323874};
    valid.Scale3D = {1.1, 1.1, 1.1};

    sdk::FTransform malformed = valid;
    malformed.Rotation = {0.0, 0.0, -2889820.0, 11160673.0};

    if (!sdk::transform_is_plausible(valid))
    {
        return 1;
    }
    if (sdk::transform_is_plausible(malformed))
    {
        return 2;
    }

    std::array<std::uint8_t, 0x48> fake_property{};
    const std::int32_t array_dim = 1;
    const std::int32_t element_size = 0x20;
    const std::uint64_t property_flags = 0x0018001000000000ULL;
    std::memcpy(fake_property.data() + 0x30, &array_dim, sizeof(array_dim));
    std::memcpy(fake_property.data() + runtime_contract::FPropertyElementSizeOffset,
                &element_size,
                sizeof(element_size));
    std::memcpy(fake_property.data() + 0x38, &property_flags, sizeof(property_flags));
    std::int32_t decoded_element_size = 0;
    std::memcpy(&decoded_element_size,
                fake_property.data() + runtime_contract::FPropertyElementSizeOffset,
                sizeof(decoded_element_size));
    if (decoded_element_size != element_size || runtime_contract::FPropertyElementSizeOffset != 0x34)
    {
        return 3;
    }

    if (!runtime_contract::requires_internal_no_resend(false, false, false, false) ||
        runtime_contract::requires_internal_no_resend(true, false, false, false) ||
        runtime_contract::requires_internal_no_resend(false, true, false, false) ||
        runtime_contract::requires_internal_no_resend(false, false, true, false) ||
        runtime_contract::requires_internal_no_resend(false, false, false, true) ||
        runtime_contract::InternalNoResendMaxCallsPerTick != 6)
    {
        return 4;
    }

    if (!runtime_contract::uobject_flags_usable(0, 0) ||
        runtime_contract::uobject_flags_usable(runtime_contract::RFClassDefaultObject, 0) ||
        runtime_contract::uobject_flags_usable(runtime_contract::RFBeginDestroyed, 0) ||
        runtime_contract::uobject_flags_usable(runtime_contract::RFFinishDestroyed, 0) ||
        runtime_contract::uobject_flags_usable(runtime_contract::RFMirroredGarbage, 0) ||
        runtime_contract::uobject_flags_usable(0, runtime_contract::RFBeginDestroyed) ||
        runtime_contract::uobject_flags_usable(0, runtime_contract::RFFinishDestroyed) ||
        runtime_contract::uobject_flags_usable(0, runtime_contract::RFMirroredGarbage) ||
        !runtime_contract::uobject_flags_usable(0x20000000u, 0))
    {
        return 5;
    }

    const auto fastest = runtime_contract::resolve_pacing(
        20,
        50,
        20,
        20,
        24,
        6);
    if (fastest.remote_batch_limit != 20 || fastest.remote_delay_ms != 50 ||
        fastest.local_batch_limit != 6 || fastest.local_delay_ms != 17)
    {
        return 6;
    }

    const auto tuned = runtime_contract::resolve_pacing(
        7,
        125,
        20,
        20,
        24,
        6);
    if (tuned.remote_batch_limit != 7 || tuned.remote_delay_ms != 125 ||
        tuned.local_batch_limit != 6 || tuned.local_delay_ms != 17)
    {
        return 7;
    }

    const auto clamped = runtime_contract::resolve_pacing(0, 0, 20, 20, 24, 6);
    if (clamped.remote_batch_limit != 1 || clamped.remote_delay_ms != 50 ||
        clamped.local_batch_limit != 6 || clamped.local_delay_ms != 17)
    {
        return 8;
    }

    if (!runtime_contract::event_watch_generation_active(true, 7, 7) ||
        runtime_contract::event_watch_generation_active(false, 7, 7) ||
        runtime_contract::event_watch_generation_active(true, 8, 7))
    {
        return 9;
    }

    if (runtime_contract::paint_channel_write_cost(4) != 4 ||
        runtime_contract::paint_channel_write_cost(5) != 3 ||
        runtime_contract::paint_channel_write_cost(0) != 1 ||
        !runtime_contract::local_dispatch_can_append(0, 0, 4, 6, 6) ||
        runtime_contract::local_dispatch_can_append(1, 4, 4, 6, 6) ||
        !runtime_contract::local_dispatch_cpu_budget_reached(1, 4'000) ||
        runtime_contract::local_dispatch_cpu_budget_reached(0, 10'000) ||
        runtime_contract::recurring_scheduler_delay_ms(0) != 1)
    {
        return 10;
    }

    std::array<runtime_contract::SpatialScanlineKey, 4> scanline{{
        {runtime_contract::spatial_scanline_row(100.0, 100.0, 10.0), 10.0, 0},
        {runtime_contract::spatial_scanline_row(100.0, 90.0, 10.0), -20.0, 1},
        {runtime_contract::spatial_scanline_row(100.0, 100.0, 10.0), -10.0, 2},
        {runtime_contract::spatial_scanline_row(100.0, 100.0, 10.0), -10.0, 3},
    }};
    std::stable_sort(scanline.begin(), scanline.end(), runtime_contract::spatial_scanline_less);
    if (scanline[0].original_ordinal != 2 || scanline[1].original_ordinal != 3 ||
        scanline[2].original_ordinal != 0 || scanline[3].original_ordinal != 1)
    {
        return 11;
    }

    const std::vector<runtime_contract::TwoBrushReplayCandidate> routed_candidates{
        {0, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Fill,
         0, 0.10, 0.10, true, 100.0, 10.0, -5.0, 0},
        {1, runtime_contract::ReplayRegion::Side, runtime_contract::ReplayRegionMode::Paint,
         1, 0.20, 0.20, true, 90.0, 9.0, 0.0, 1},
        {2, runtime_contract::ReplayRegion::Front, runtime_contract::ReplayRegionMode::Skip,
         2, 0.30, 0.30, true, 80.0, 8.0, 5.0, 2},
    };
    const auto routed_plan = runtime_contract::build_two_brush_replay_plan(
        routed_candidates,
        1024,
        20.0,
        10.0,
        80.0);
    if (routed_plan.entries.size() != 3 ||
        routed_plan.fill_end != 1 || routed_plan.coarse_end != 2 ||
        routed_plan.fill_count != 1 || routed_plan.coarse_paint_count != 1 ||
        routed_plan.fine_paint_count != 1 ||
        routed_plan.entries[0].pass != runtime_contract::ReplayPass::Fill ||
        routed_plan.entries[0].sample_index != 0 ||
        routed_plan.entries[1].pass != runtime_contract::ReplayPass::CoarsePaint ||
        routed_plan.entries[1].sample_index != 1 ||
        routed_plan.entries[2].pass != runtime_contract::ReplayPass::FinePaint ||
        routed_plan.entries[2].sample_index != 1)
    {
        return 12;
    }

    const std::vector<runtime_contract::TwoBrushReplayCandidate> dedupe_candidates{
        {0, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Fill,
         0, 0.100, 0.100, true, 100.0, 10.0, -5.0, 0},
        {1, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Fill,
         0, 0.105, 0.105, true, 99.0, 9.0, -4.0, 1},
        {2, runtime_contract::ReplayRegion::Side, runtime_contract::ReplayRegionMode::Paint,
         1, 0.200, 0.200, true, 90.0, 8.0, -3.0, 2},
        {3, runtime_contract::ReplayRegion::Side, runtime_contract::ReplayRegionMode::Paint,
         1, 0.205, 0.205, true, 89.0, 7.0, -2.0, 3},
        {4, runtime_contract::ReplayRegion::Side, runtime_contract::ReplayRegionMode::Paint,
         1, 0.250, 0.250, true, 80.0, 6.0, -1.0, 4},
    };
    const auto dedupe_plan = runtime_contract::build_two_brush_replay_plan(
        dedupe_candidates,
        1024,
        20.0,
        10.0,
        80.0);
    if (dedupe_plan.entries.size() != 6 ||
        dedupe_plan.fill_end != 1 || dedupe_plan.coarse_end != 3 ||
        dedupe_plan.fill_count != 1 || dedupe_plan.coarse_paint_count != 2 ||
        dedupe_plan.fine_paint_count != 3 ||
        dedupe_plan.fill_candidates != 2 || dedupe_plan.fill_deduplicated != 1 ||
        dedupe_plan.coarse_paint_candidates != 3 || dedupe_plan.coarse_paint_deduplicated != 1 ||
        dedupe_plan.entries[0].sample_index != 0 ||
        dedupe_plan.entries[1].sample_index != 2 ||
        dedupe_plan.entries[2].sample_index != 4)
    {
        return 13;
    }

    const std::vector<runtime_contract::TwoBrushReplayCandidate> reference_order_candidates{
        {0, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Paint,
         0, 0.10, 0.10, true, 90.0, 1000.0, 10.0, 0},
        {1, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Paint,
         0, 0.20, 0.20, true, 100.0, 0.0, 10.0, 1},
        {2, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Paint,
         0, 0.30, 0.30, true, 100.0, 0.0, -10.0, 2},
        {3, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Paint,
         0, 0.40, 0.40, false, 999.0, 80.0, 0.0, 3},
    };
    const auto reference_order_plan = runtime_contract::build_two_brush_replay_plan(
        reference_order_candidates,
        1024,
        20.0,
        10.0,
        80.0);
    const std::array<std::size_t, 4> expected_reference_order{{2, 1, 0, 3}};
    for (std::size_t index = 0; index < expected_reference_order.size(); ++index)
    {
        if (reference_order_plan.entries[index].sample_index != expected_reference_order[index] ||
            reference_order_plan.entries[reference_order_plan.coarse_end + index].sample_index != expected_reference_order[index])
        {
            return 14;
        }
    }
    if (!reference_order_plan.reference_position_fallback_used ||
        reference_order_plan.reference_position_fallback_candidates != 1)
    {
        return 14;
    }

    const std::vector<runtime_contract::TwoBrushReplayCandidate> region_order_candidates{
        {0, runtime_contract::ReplayRegion::Front, runtime_contract::ReplayRegionMode::Paint,
         0, 0.10, 0.10, true, 100.0, 0.0, 0.0, 0},
        {1, runtime_contract::ReplayRegion::Side, runtime_contract::ReplayRegionMode::Paint,
         0, 0.10, 0.10, true, 100.0, 0.0, 0.0, 1},
        {2, runtime_contract::ReplayRegion::Back, runtime_contract::ReplayRegionMode::Paint,
         0, 0.10, 0.10, true, 100.0, 0.0, 0.0, 2},
    };
    const auto region_order_plan = runtime_contract::build_two_brush_replay_plan(
        region_order_candidates,
        1024,
        20.0,
        10.0,
        80.0);
    const std::array<std::size_t, 3> expected_region_order{{2, 1, 0}};
    for (std::size_t index = 0; index < expected_region_order.size(); ++index)
    {
        if (region_order_plan.entries[index].sample_index != expected_region_order[index] ||
            region_order_plan.entries[region_order_plan.coarse_end + index].sample_index != expected_region_order[index])
        {
            return 15;
        }
    }

    const auto supported_brush_pipeline =
        runtime_contract::resolve_brush_pipeline_version(2, false, false);
    const auto missing_brush_pipeline =
        runtime_contract::resolve_brush_pipeline_version(0, false, false);
    const auto legacy_brush_pipeline =
        runtime_contract::resolve_brush_pipeline_version(1, false, false);
    const auto future_brush_pipeline =
        runtime_contract::resolve_brush_pipeline_version(3, false, false);
    const auto fractional_brush_pipeline =
        runtime_contract::resolve_brush_pipeline_version(2.5, false, false);
    const auto preview_without_brush_pipeline =
        runtime_contract::resolve_brush_pipeline_version(0, true, false);
    if (!supported_brush_pipeline.required || !supported_brush_pipeline.supported ||
        missing_brush_pipeline.supported || legacy_brush_pipeline.supported ||
        future_brush_pipeline.supported || fractional_brush_pipeline.supported ||
        preview_without_brush_pipeline.required ||
        !preview_without_brush_pipeline.supported)
    {
        return 16;
    }

    if (!runtime_contract::packed_manager_precommit_matches(0x1000, 0x1000) ||
        runtime_contract::packed_manager_precommit_matches(0x1000, 0x2000) ||
        runtime_contract::packed_manager_precommit_matches(0x1000, 0) ||
        runtime_contract::packed_manager_precommit_matches(0, 0))
    {
        return 17;
    }

    if (!runtime_contract::paired_paint_cancel_safe_to_observe(false, 20, 0) ||
        !runtime_contract::paired_paint_cancel_safe_to_observe(true, 20, 20) ||
        runtime_contract::paired_paint_cancel_safe_to_observe(true, 20, 0) ||
        runtime_contract::paired_paint_cancel_safe_to_observe(true, 40, 20))
    {
        return 18;
    }

    if (runtime_contract::PackedMeshAnchorWorldRadiusAuto != 0.0f ||
        runtime_contract::PackedMeshAnchorCoverageSafetyFactor != 0.91 ||
        runtime_contract::PackedMeshAnchorExpectedRadiusCalibration != 3.5 ||
        !runtime_contract::packed_mesh_anchor_requests_world_radius_conversion(
            runtime_contract::PackedMeshAnchorWorldRadiusAuto) ||
        runtime_contract::packed_mesh_anchor_requests_world_radius_conversion(20.0f / 1024.0f) ||
        !runtime_contract::packed_mesh_anchor_world_radius_contract_valid(0.0f, 10.0f / 1024.0f) ||
        !runtime_contract::packed_mesh_anchor_world_radius_contract_valid(1.665f, 10.0f / 1024.0f) ||
        runtime_contract::packed_mesh_anchor_world_radius_contract_valid(10.0f / 1024.0f,
                                                                         10.0f / 1024.0f) ||
        runtime_contract::packed_mesh_anchor_world_radius_contract_valid(
            std::numeric_limits<float>::quiet_NaN(),
            10.0f / 1024.0f))
    {
        return 19;
    }

    const float source_wire_test_radius = 10.0f / 1024.0f;
    float resolved_wire_radius = -1.0f;
    if (!runtime_contract::resolve_packed_wire_brush_radius(source_wire_test_radius,
                                                            1.0,
                                                            resolved_wire_radius) ||
        resolved_wire_radius != source_wire_test_radius ||
        !runtime_contract::resolve_packed_wire_brush_radius(source_wire_test_radius,
                                                            3.5,
                                                            resolved_wire_radius) ||
        resolved_wire_radius != static_cast<float>(
                                    static_cast<double>(source_wire_test_radius) * 3.5) ||
        source_wire_test_radius != 10.0f / 1024.0f ||
        runtime_contract::resolve_packed_wire_brush_radius(0.0f, 1.0, resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(-0.1f, 1.0, resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(
            std::numeric_limits<float>::quiet_NaN(), 1.0, resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(
            std::numeric_limits<float>::infinity(), 1.0, resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(source_wire_test_radius,
                                                            0.0,
                                                            resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(
            source_wire_test_radius,
            std::numeric_limits<double>::quiet_NaN(),
            resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(
            source_wire_test_radius,
            std::numeric_limits<double>::infinity(),
            resolved_wire_radius) ||
        runtime_contract::resolve_packed_wire_brush_radius(0.5f, 3.0, resolved_wire_radius))
    {
        return 20;
    }

    constexpr auto auto_subdivision_tail =
        runtime_contract::packed_mesh_anchor_auto_subdivision_tail();
    if (runtime_contract::PackedMeshAnchorSubdivisionLevelAuto != 0 ||
        runtime_contract::PackedMeshAnchorSubdivisionPixelSizeAuto != 0.0f ||
        runtime_contract::PackedMeshAnchorTemplateResolutionAuto != 0 ||
        auto_subdivision_tail.size() != 4 ||
        auto_subdivision_tail[0] != 0 ||
        auto_subdivision_tail[1] != 0 ||
        auto_subdivision_tail[2] != 0 ||
        auto_subdivision_tail[3] != 0 ||
        !runtime_contract::packed_mesh_anchor_requests_native_subdivision_preflight(
            runtime_contract::PackedMeshAnchorSubdivisionLevelAuto,
            runtime_contract::PackedMeshAnchorSubdivisionPixelSizeAuto,
            runtime_contract::PackedMeshAnchorTemplateResolutionAuto) ||
        runtime_contract::packed_mesh_anchor_requests_native_subdivision_preflight(20, 0.0f, 0) ||
        runtime_contract::packed_mesh_anchor_requests_native_subdivision_preflight(0, 2.0f, 0) ||
        runtime_contract::packed_mesh_anchor_requests_native_subdivision_preflight(0, 0.0f, 1024))
    {
        return 21;
    }

    const auto fill_window = runtime_contract::replay_pass_window(0, 100, 20, 80);
    const auto coarse_window = runtime_contract::replay_pass_window(20, 100, 20, 80);
    const auto fine_window = runtime_contract::replay_pass_window(80, 100, 20, 80);
    const auto complete_window = runtime_contract::replay_pass_window(100, 100, 20, 80);
    const auto clamped_window = runtime_contract::replay_pass_window(999, 10, 50, 2);
    if (fill_window.pass != runtime_contract::ReplayPass::Fill ||
        fill_window.begin != 0 || fill_window.end != 20 ||
        coarse_window.pass != runtime_contract::ReplayPass::CoarsePaint ||
        coarse_window.begin != 20 || coarse_window.end != 80 ||
        fine_window.pass != runtime_contract::ReplayPass::FinePaint ||
        fine_window.begin != 80 || fine_window.end != 100 ||
        complete_window.pass != runtime_contract::ReplayPass::Complete ||
        complete_window.begin != 100 || complete_window.end != 100 ||
        clamped_window.pass != runtime_contract::ReplayPass::Complete ||
        clamped_window.begin != 10 || clamped_window.end != 10)
    {
        return 22;
    }

    if (runtime_contract::receiver_queue_rendered_strokes(5596, 4119, 0) != 1477 ||
        runtime_contract::receiver_queue_rendered_strokes(5596, 4200, 1477) != 1477 ||
        runtime_contract::receiver_queue_rendered_strokes(5596, 0, 1477) != 5596 ||
        runtime_contract::receiver_queue_rendered_strokes(5596, 7420, 0) != 0 ||
        runtime_contract::receiver_queue_drain_complete(1, 2) ||
        runtime_contract::receiver_queue_drain_complete(0, 1) ||
        !runtime_contract::receiver_queue_drain_complete(0, 2) ||
        runtime_contract::receiver_queue_idle_threshold_reached(0, 120000, 120000) ||
        runtime_contract::receiver_queue_idle_threshold_reached(1, 119999, 120000) ||
        !runtime_contract::receiver_queue_idle_threshold_reached(1, 120000, 120000) ||
        !runtime_contract::receiver_queue_idle_threshold_reached(1, 120001, 120000))
    {
        return 23;
    }

    if (runtime_contract::paired_local_queue_available_capacity(20, 0) != 20 ||
        runtime_contract::paired_local_queue_available_capacity(20, 7) != 13 ||
        runtime_contract::paired_local_queue_available_capacity(20, 20) != 0 ||
        runtime_contract::paired_local_queue_available_capacity(20, 21) != 0 ||
        runtime_contract::paired_local_queue_available_capacity(20, -1) != 0 ||
        runtime_contract::paired_local_queue_commit_count(20, 20, 7) != 13 ||
        runtime_contract::paired_local_queue_commit_count(5, 20, 7) != 5 ||
        runtime_contract::paired_local_queue_commit_count(20, 20, 20) != 0 ||
        runtime_contract::paired_local_queue_cancel_needs_drain(false, 20) ||
        runtime_contract::paired_local_queue_cancel_needs_drain(true, 0) ||
        !runtime_contract::paired_local_queue_cancel_needs_drain(true, 1))
    {
        return 24;
    }

    runtime_contract::BilinearPixelSample bilinear{};
    if (!runtime_contract::resolve_bilinear_pixel_sample(1.0, 1.0, 2, 2, false, false, bilinear) ||
        bilinear.x.lower != 0 || bilinear.x.upper != 1 || bilinear.x.fraction != 0.5 ||
        bilinear.y.lower != 0 || bilinear.y.upper != 1 || bilinear.y.fraction != 0.5 ||
        std::abs(runtime_contract::bilinear_pixel_value(0.0, 10.0, 20.0, 30.0, bilinear) - 15.0) > 0.000001)
    {
        return 25;
    }
    if (!runtime_contract::resolve_bilinear_pixel_sample(0.5, 0.5, 2, 2, false, false, bilinear) ||
        runtime_contract::bilinear_pixel_value(0.0, 10.0, 20.0, 30.0, bilinear) != 0.0)
    {
        return 26;
    }
    const std::vector<double> asymmetric_pixels{0.0, 10.0, 20.0,
                                                100.0, 110.0, 120.0};
    const auto sample_fixture = [&](const std::vector<double>& pixels,
                                    int width,
                                    int height,
                                    double x,
                                    double y,
                                    bool flip_x,
                                    bool flip_y,
                                    double& value) {
        runtime_contract::BilinearPixelSample fixture_sample{};
        if (pixels.size() != static_cast<std::size_t>(width * height) ||
            !runtime_contract::resolve_bilinear_pixel_sample(
                x, y, width, height, flip_x, flip_y, fixture_sample))
        {
            return false;
        }
        const auto pixel = [&](int px, int py) {
            return pixels[static_cast<std::size_t>(py * width + px)];
        };
        value = runtime_contract::bilinear_pixel_value(
            pixel(fixture_sample.x.lower, fixture_sample.y.lower),
            pixel(fixture_sample.x.upper, fixture_sample.y.lower),
            pixel(fixture_sample.x.lower, fixture_sample.y.upper),
            pixel(fixture_sample.x.upper, fixture_sample.y.upper),
            fixture_sample);
        return true;
    };
    double fixture_value = 0.0;
    if (!sample_fixture(asymmetric_pixels, 3, 2, 1.25, 0.75, false, false, fixture_value) ||
        std::abs(fixture_value - 32.5) > 0.000001 ||
        !sample_fixture(asymmetric_pixels, 3, 2, 1.25, 0.75, true, false, fixture_value) ||
        std::abs(fixture_value - 37.5) > 0.000001 ||
        !sample_fixture(asymmetric_pixels, 3, 2, 1.25, 0.75, false, true, fixture_value) ||
        std::abs(fixture_value - 82.5) > 0.000001 ||
        !sample_fixture(asymmetric_pixels, 3, 2, 1.25, 0.75, true, true, fixture_value) ||
        std::abs(fixture_value - 87.5) > 0.000001)
    {
        return 27;
    }
    if (!sample_fixture(asymmetric_pixels, 3, 2, 2.999, 1.999, false, false, fixture_value) ||
        fixture_value != 120.0 ||
        !sample_fixture(std::vector<double>{42.0}, 1, 1, 0.75, 0.25, true, true, fixture_value) ||
        fixture_value != 42.0)
    {
        return 28;
    }
    if (runtime_contract::resolve_bilinear_pixel_sample(-0.001, 0.5, 2, 2, false, false, bilinear) ||
        runtime_contract::resolve_bilinear_pixel_sample(2.0, 0.5, 2, 2, false, false, bilinear) ||
        runtime_contract::resolve_bilinear_pixel_sample(
            std::numeric_limits<double>::quiet_NaN(), 0.5, 2, 2, false, false, bilinear) ||
        runtime_contract::resolve_bilinear_pixel_sample(0.5, 0.5, 0, 2, false, false, bilinear))
    {
        return 29;
    }
    if (!runtime_contract::resolve_bilinear_pixel_sample(1.0, 0.5, 2, 1, false, false, bilinear) ||
        std::abs(runtime_contract::srgb_to_linear_unit(
                     runtime_contract::bilinear_srgb_pixel_value(
                         0.0, 1.0, 0.0, 1.0, bilinear)) -
                 0.5) > 0.000001)
    {
        return 30;
    }

    const runtime_contract::Rgb8 detail_black{0, 0, 0};
    const runtime_contract::Rgb8 detail_red_below_threshold{15, 0, 0};
    const runtime_contract::Rgb8 detail_red_at_threshold{16, 0, 0};
    if (runtime_contract::adaptive_detail_color_eligible(
            detail_black, detail_red_below_threshold) ||
        !runtime_contract::adaptive_detail_color_eligible(
            detail_black, detail_red_at_threshold) ||
        runtime_contract::adaptive_detail_color_score(
            detail_black, detail_red_at_threshold) != 54U * 16U * 16U ||
        runtime_contract::adaptive_detail_color_score(
            detail_red_at_threshold, detail_black) != 54U * 16U * 16U)
    {
        return 31;
    }

    if (runtime_contract::adaptive_detail_stroke_budget(0, 0) != 0 ||
        runtime_contract::adaptive_detail_stroke_budget(1, 0) != 1 ||
        runtime_contract::adaptive_detail_stroke_budget(5, 0) != 1 ||
        runtime_contract::adaptive_detail_stroke_budget(6, 0) != 2 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 0) != 512 ||
        runtime_contract::adaptive_detail_stroke_budget(100000, 0) !=
            runtime_contract::AdaptiveDetailMaximumStrokes ||
        runtime_contract::adaptive_detail_stroke_budget(10, 99999) != 1 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 100000) != 0 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 100001) != 0 ||
        runtime_contract::adaptive_detail_radius_texels(5.0) != 2.5 ||
        runtime_contract::adaptive_detail_radius_texels(10.0) != 5.0)
    {
        return 32;
    }

    const std::vector<runtime_contract::AdaptiveDetailCandidate> dedupe_detail_candidates{
        {10, 100, runtime_contract::ReplayRegion::Back, 0, 0, 0, 0, 0,
         detail_black, runtime_contract::Rgb8{0, 128, 0}},
        {11, 100, runtime_contract::ReplayRegion::Back, 0, 0, 0, 0, 1,
         detail_black, runtime_contract::Rgb8{255, 255, 255}},
        {12, 101, runtime_contract::ReplayRegion::Back, 0, 0, 0, 1, 0,
         detail_black, runtime_contract::Rgb8{250, 0, 0}},
        {13, 102, runtime_contract::ReplayRegion::Back, 0, 1, 0, 2, 0,
         detail_black, runtime_contract::Rgb8{0, 0, 200}},
        {14, 103, runtime_contract::ReplayRegion::Back, 0, 2, 0, 3, 0,
         detail_black, detail_red_below_threshold},
    };
    const auto dedupe_detail_selection =
        runtime_contract::select_adaptive_detail_candidates(dedupe_detail_candidates, 8);
    auto reversed_dedupe_detail_candidates = dedupe_detail_candidates;
    std::reverse(reversed_dedupe_detail_candidates.begin(),
                 reversed_dedupe_detail_candidates.end());
    const auto reversed_dedupe_detail_selection =
        runtime_contract::select_adaptive_detail_candidates(
            reversed_dedupe_detail_candidates, 8);
    const std::vector<std::size_t> expected_dedupe_detail_indices{11, 13};
    if (dedupe_detail_selection.sample_indices != expected_dedupe_detail_indices ||
        reversed_dedupe_detail_selection.sample_indices != expected_dedupe_detail_indices ||
        dedupe_detail_selection.eligible_candidates != 4 ||
        dedupe_detail_selection.parent_deduplicated != 1 ||
        dedupe_detail_selection.cell_deduplicated != 1 ||
        dedupe_detail_selection.budget_limited ||
        reversed_dedupe_detail_selection.eligible_candidates !=
            dedupe_detail_selection.eligible_candidates ||
        reversed_dedupe_detail_selection.parent_deduplicated !=
            dedupe_detail_selection.parent_deduplicated ||
        reversed_dedupe_detail_selection.cell_deduplicated !=
            dedupe_detail_selection.cell_deduplicated ||
        reversed_dedupe_detail_selection.budget_limited)
    {
        return 33;
    }

    const std::vector<runtime_contract::AdaptiveDetailCandidate> tied_detail_candidates{
        {21, 201, runtime_contract::ReplayRegion::Front, 0, 2, 0, 2, 0,
         detail_black, detail_red_at_threshold},
        {22, 202, runtime_contract::ReplayRegion::Back, 0, 0, 0, 0, 0,
         detail_black, detail_red_at_threshold},
        {23, 203, runtime_contract::ReplayRegion::Side, 0, 1, 0, 1, 0,
         detail_black, detail_red_at_threshold},
    };
    const auto tied_detail_selection =
        runtime_contract::select_adaptive_detail_candidates(tied_detail_candidates, 2);
    auto reversed_tied_detail_candidates = tied_detail_candidates;
    std::reverse(reversed_tied_detail_candidates.begin(),
                 reversed_tied_detail_candidates.end());
    const auto reversed_tied_detail_selection =
        runtime_contract::select_adaptive_detail_candidates(
            reversed_tied_detail_candidates, 2);
    const auto zero_budget_detail_selection =
        runtime_contract::select_adaptive_detail_candidates(tied_detail_candidates, 0);
    const std::vector<std::size_t> expected_tied_detail_indices{22, 23};
    if (tied_detail_selection.sample_indices != expected_tied_detail_indices ||
        reversed_tied_detail_selection.sample_indices != expected_tied_detail_indices ||
        !tied_detail_selection.budget_limited ||
        !reversed_tied_detail_selection.budget_limited ||
        !zero_budget_detail_selection.sample_indices.empty() ||
        zero_budget_detail_selection.eligible_candidates != tied_detail_candidates.size() ||
        !zero_budget_detail_selection.budget_limited)
    {
        return 34;
    }

    const std::vector<runtime_contract::TwoBrushReplayCandidate> refined_replay_candidates{
        {30, runtime_contract::ReplayRegion::Front, runtime_contract::ReplayRegionMode::Paint,
         0, 0.10, 0.10, true, 10.0, 10.0, 10.0, 0, false},
        {31, runtime_contract::ReplayRegion::Front, runtime_contract::ReplayRegionMode::Paint,
         0, 0.20, 0.20, true, 20.0, 20.0, 20.0, 1, false},
        {40, runtime_contract::ReplayRegion::Front, runtime_contract::ReplayRegionMode::Paint,
         0, 0.15, 0.15, true, 5.0, 5.0, -100.0, 2, true},
        {41, runtime_contract::ReplayRegion::Front, runtime_contract::ReplayRegionMode::Paint,
         0, 0.25, 0.25, true, 0.0, 0.0, -90.0, 3, true},
    };
    const auto refined_replay_plan = runtime_contract::build_two_brush_replay_plan(
        refined_replay_candidates,
        1024,
        20.0,
        10.0,
        80.0);
    const std::size_t base_fine_begin = refined_replay_plan.coarse_end;
    const std::size_t detail_begin =
        refined_replay_plan.entries.size() - refined_replay_plan.detail_refinement_count;
    if (refined_replay_plan.entries.size() != 6 ||
        refined_replay_plan.fill_end != 0 ||
        refined_replay_plan.coarse_end != 2 ||
        refined_replay_plan.coarse_paint_count != 2 ||
        refined_replay_plan.fine_paint_count != 4 ||
        refined_replay_plan.detail_refinement_count != 2 ||
        detail_begin != base_fine_begin + 2)
    {
        return 35;
    }
    for (std::size_t index = 0; index < refined_replay_plan.entries.size(); ++index)
    {
        const auto& entry = refined_replay_plan.entries[index];
        if ((index < refined_replay_plan.coarse_end &&
             (entry.pass != runtime_contract::ReplayPass::CoarsePaint ||
              entry.detail_refinement)) ||
            (index >= base_fine_begin && index < detail_begin &&
             (entry.pass != runtime_contract::ReplayPass::FinePaint ||
              entry.detail_refinement)) ||
            (index >= detail_begin &&
             (entry.pass != runtime_contract::ReplayPass::FinePaint ||
              !entry.detail_refinement)))
        {
            return 36;
        }
    }

    const std::vector<runtime_contract::TwoBrushReplayCandidate> base_replay_candidates(
        refined_replay_candidates.begin(),
        refined_replay_candidates.begin() + 2);
    const auto base_replay_plan = runtime_contract::build_two_brush_replay_plan(
        base_replay_candidates,
        1024,
        20.0,
        10.0,
        80.0);
    if (base_replay_plan.entries.size() != detail_begin ||
        base_replay_plan.fill_end != refined_replay_plan.fill_end ||
        base_replay_plan.coarse_end != refined_replay_plan.coarse_end)
    {
        return 37;
    }
    for (std::size_t index = 0; index < base_replay_plan.entries.size(); ++index)
    {
        const auto& base_entry = base_replay_plan.entries[index];
        const auto& refined_entry = refined_replay_plan.entries[index];
        if (base_entry.sample_index != refined_entry.sample_index ||
            base_entry.pass != refined_entry.pass ||
            base_entry.region != refined_entry.region ||
            base_entry.spatial_key.row != refined_entry.spatial_key.row ||
            base_entry.spatial_key.horizontal != refined_entry.spatial_key.horizontal ||
            base_entry.spatial_key.original_ordinal !=
                refined_entry.spatial_key.original_ordinal ||
            base_entry.detail_refinement || refined_entry.detail_refinement)
        {
            return 38;
        }
    }

    if (runtime_contract::clamp_detail_resolution_percent(49) != 50 ||
        runtime_contract::clamp_detail_resolution_percent(100) != 100 ||
        runtime_contract::clamp_detail_resolution_percent(201) != 201 ||
        runtime_contract::clamp_detail_resolution_percent(501) != 500 ||
        runtime_contract::adaptive_detail_channel_threshold(50) != 32 ||
        runtime_contract::adaptive_detail_channel_threshold(100) != 16 ||
        runtime_contract::adaptive_detail_channel_threshold(200) != 8 ||
        runtime_contract::adaptive_detail_channel_threshold(500) != 4 ||
        runtime_contract::adaptive_detail_channel_threshold() !=
            runtime_contract::AdaptiveDetailChannelThreshold)
    {
        return 39;
    }

    if (runtime_contract::adaptive_detail_color_eligible(
            detail_black, runtime_contract::Rgb8{31, 0, 0}, 50) ||
        !runtime_contract::adaptive_detail_color_eligible(
            detail_black, runtime_contract::Rgb8{32, 0, 0}, 50) ||
        runtime_contract::adaptive_detail_color_eligible(
            detail_black, runtime_contract::Rgb8{7, 0, 0}, 200) ||
        !runtime_contract::adaptive_detail_color_eligible(
            detail_black, runtime_contract::Rgb8{8, 0, 0}, 200) ||
        runtime_contract::adaptive_detail_color_eligible(
            detail_black, runtime_contract::Rgb8{3, 0, 0}, 500) ||
        !runtime_contract::adaptive_detail_color_eligible(
            detail_black, runtime_contract::Rgb8{4, 0, 0}, 500) ||
        runtime_contract::adaptive_detail_color_eligible(
            detail_black, detail_red_at_threshold) !=
            runtime_contract::adaptive_detail_color_eligible(
                detail_black, detail_red_at_threshold, 100))
    {
        return 40;
    }

    if (runtime_contract::adaptive_detail_maximum_strokes(50) != 256 ||
        runtime_contract::adaptive_detail_maximum_strokes(100) != 512 ||
        runtime_contract::adaptive_detail_maximum_strokes(200) != 1024 ||
        runtime_contract::adaptive_detail_maximum_strokes(500) != 2560 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 0, 50) != 1 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 0, 100) != 2 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 0, 200) != 4 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 0, 500) != 10 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 0, 50) != 256 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 0, 100) != 512 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 0, 200) != 1024 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 0, 500) != 2560 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 99999, 200) != 1 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 100000, 200) != 0 ||
        runtime_contract::adaptive_detail_stroke_budget(2560, 100001, 200) != 0 ||
        runtime_contract::adaptive_detail_stroke_budget(10, 0) !=
            runtime_contract::adaptive_detail_stroke_budget(10, 0, 100) ||
        runtime_contract::adaptive_detail_radius_texels(5.0, 50) != 5.0 ||
        runtime_contract::adaptive_detail_radius_texels(5.0, 100) != 2.5 ||
        runtime_contract::adaptive_detail_radius_texels(5.0, 200) != 1.25 ||
        runtime_contract::adaptive_detail_radius_texels(5.0, 500) != 0.5 ||
        runtime_contract::adaptive_detail_radius_texels(10.0, 50) != 10.0 ||
        runtime_contract::adaptive_detail_radius_texels(10.0, 100) != 5.0 ||
        runtime_contract::adaptive_detail_radius_texels(10.0, 200) != 2.5 ||
        runtime_contract::adaptive_detail_radius_texels(10.0, 500) != 1.0 ||
        runtime_contract::adaptive_detail_radius_texels(10.0) !=
            runtime_contract::adaptive_detail_radius_texels(10.0, 100))
    {
        return 41;
    }

    const std::vector<runtime_contract::AdaptiveDetailCandidate>
        resolution_detail_candidates{
            {49, 499, runtime_contract::ReplayRegion::Back, 0, 3, 0, 3, 0,
             detail_black, runtime_contract::Rgb8{4, 0, 0}},
            {50, 500, runtime_contract::ReplayRegion::Back, 0, 0, 0, 0, 0,
             detail_black, runtime_contract::Rgb8{8, 0, 0}},
            {51, 501, runtime_contract::ReplayRegion::Back, 0, 1, 0, 1, 0,
             detail_black, runtime_contract::Rgb8{16, 0, 0}},
            {52, 502, runtime_contract::ReplayRegion::Back, 0, 2, 0, 2, 0,
             detail_black, runtime_contract::Rgb8{32, 0, 0}},
        };
    const auto resolution_selection_50 =
        runtime_contract::select_adaptive_detail_candidates(
            resolution_detail_candidates, 10, 50);
    const auto resolution_selection_100 =
        runtime_contract::select_adaptive_detail_candidates(
            resolution_detail_candidates, 10, 100);
    const auto resolution_selection_200 =
        runtime_contract::select_adaptive_detail_candidates(
            resolution_detail_candidates, 10, 200);
    const auto resolution_selection_500 =
        runtime_contract::select_adaptive_detail_candidates(
            resolution_detail_candidates, 10, 500);
    const std::vector<std::size_t> expected_resolution_indices_50{52};
    const std::vector<std::size_t> expected_resolution_indices_100{52, 51};
    const std::vector<std::size_t> expected_resolution_indices_200{52, 51, 50};
    const std::vector<std::size_t> expected_resolution_indices_500{52, 51, 50, 49};
    if (resolution_selection_50.sample_indices != expected_resolution_indices_50 ||
        resolution_selection_100.sample_indices != expected_resolution_indices_100 ||
        resolution_selection_200.sample_indices != expected_resolution_indices_200 ||
        resolution_selection_500.sample_indices != expected_resolution_indices_500 ||
        resolution_selection_50.eligible_candidates != 1 ||
        resolution_selection_100.eligible_candidates != 2 ||
        resolution_selection_200.eligible_candidates != 3 ||
        resolution_selection_500.eligible_candidates != 4 ||
        runtime_contract::select_adaptive_detail_candidates(
            resolution_detail_candidates, 10).sample_indices !=
            resolution_selection_100.sample_indices)
    {
        return 42;
    }

    const auto refined_replay_plan_200 =
        runtime_contract::build_two_brush_replay_plan(
            refined_replay_candidates,
            1024,
            20.0,
            10.0,
            80.0,
            200);
    if (refined_replay_plan_200.entries.size() != refined_replay_plan.entries.size() ||
        refined_replay_plan_200.fill_end != refined_replay_plan.fill_end ||
        refined_replay_plan_200.coarse_end != refined_replay_plan.coarse_end ||
        refined_replay_plan_200.detail_refinement_count !=
            refined_replay_plan.detail_refinement_count)
    {
        return 43;
    }
    for (std::size_t index = 0; index < detail_begin; ++index)
    {
        const auto& baseline_entry = refined_replay_plan.entries[index];
        const auto& scaled_entry = refined_replay_plan_200.entries[index];
        if (baseline_entry.sample_index != scaled_entry.sample_index ||
            baseline_entry.pass != scaled_entry.pass ||
            baseline_entry.region != scaled_entry.region ||
            baseline_entry.spatial_key.row != scaled_entry.spatial_key.row ||
            baseline_entry.spatial_key.horizontal != scaled_entry.spatial_key.horizontal ||
            baseline_entry.spatial_key.original_ordinal !=
                scaled_entry.spatial_key.original_ordinal ||
            baseline_entry.detail_refinement || scaled_entry.detail_refinement)
        {
            return 44;
        }
    }
    bool detail_resolution_changed_row = false;
    for (std::size_t index = detail_begin;
         index < refined_replay_plan_200.entries.size();
         ++index)
    {
        detail_resolution_changed_row =
            detail_resolution_changed_row ||
            refined_replay_plan_200.entries[index].spatial_key.row !=
                refined_replay_plan.entries[index].spatial_key.row;
        if (!refined_replay_plan_200.entries[index].detail_refinement ||
            refined_replay_plan_200.entries[index].pass !=
                runtime_contract::ReplayPass::FinePaint)
        {
            return 45;
        }
    }
    if (!detail_resolution_changed_row)
    {
        return 46;
    }
    return 0;
}
