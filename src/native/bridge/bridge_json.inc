    auto json_number_field(const std::string& text, const std::string& key, double fallback) -> double
    {
        const std::string needle = std::string("\"") + key + "\":";
        const auto start = text.find(needle);
        if (start == std::string::npos)
            return fallback;
        const char* begin = text.c_str() + start + needle.size();
        char* end = nullptr;
        const double value = std::strtod(begin, &end);
        return end == begin || !std::isfinite(value) ? fallback : value;
    }

    auto json_int_field(const std::string& text, const std::string& key, int fallback, int min_value, int max_value) -> int
    {
        const double value = json_number_field(text, key, static_cast<double>(fallback));
        return std::max(min_value, std::min(max_value, static_cast<int>(value)));
    }

    auto json_bool_field(const std::string& text, const std::string& key, bool fallback) -> bool
    {
        const std::string needle = std::string("\"") + key + "\":";
        const auto start = text.find(needle);
        if (start == std::string::npos)
            return fallback;
        auto pos = start + needle.size();
        while (pos < text.size() && std::isspace(static_cast<unsigned char>(text[pos])))
            ++pos;
        if (text.compare(pos, 4, "true") == 0)
            return true;
        if (text.compare(pos, 5, "false") == 0)
            return false;
        return fallback;
    }

    auto json_string_field(const std::string& text, const std::string& key, const std::string& fallback = {}) -> std::string
    {
        const std::string needle = std::string("\"") + key + "\":";
        const auto start = text.find(needle);
        if (start == std::string::npos)
            return fallback;
        auto pos = text.find('"', start + needle.size());
        if (pos == std::string::npos)
            return fallback;
        ++pos;
        std::string out{};
        bool escaped = false;
        for (; pos < text.size(); ++pos)
        {
            const char ch = text[pos];
            if (escaped)
            {
                switch (ch)
                {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                default: out.push_back(ch); break;
                }
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                return out;
            }
            out.push_back(ch);
        }
        return fallback;
    }

    auto json_escape(const std::string& text) -> std::string
    {
        std::string out{};
        out.reserve(text.size() + 8);
        for (const char c : text)
        {
            const auto value = static_cast<unsigned char>(c);
            if (c == '\\' || c == '"')
            {
                out.push_back('\\');
                out.push_back(c);
            }
            else if (c == '\n')
            {
                out += "\\n";
            }
            else if (c == '\r')
            {
                out += "\\r";
            }
            else if (c == '\t')
            {
                out += "\\t";
            }
            else if (value < 0x20)
            {
                static constexpr char digits[] = "0123456789abcdef";
                out += "\\u00";
                out.push_back(digits[(value >> 4) & 0x0f]);
                out.push_back(digits[value & 0x0f]);
            }
            else
            {
                out.push_back(c);
            }
        }
        return out;
    }

    auto metadata_contains_bool(const std::string& metadata, const char* key, bool value) -> bool
    {
        return metadata.find(std::string("\"") + key + "\":" + (value ? "true" : "false")) != std::string::npos;
    }

    auto metadata_contains_string(const std::string& metadata, const char* key, const char* value) -> bool
    {
        return metadata.find(std::string("\"") + key + "\":\"" + value + "\"") != std::string::npos;
    }

    auto append_metadata_field(std::string& out, const std::string& metadata, const char* key) -> void
    {
        const std::string pattern = std::string("\"") + key + "\":";
        const auto begin = metadata.find(pattern);
        if (begin == std::string::npos)
        {
            return;
        }

        std::size_t pos = begin + pattern.size();
        bool in_string = false;
        bool escaped = false;
        int depth = 0;
        for (; pos < metadata.size(); ++pos)
        {
            const char c = metadata[pos];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (in_string && c == '\\')
            {
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                in_string = !in_string;
                continue;
            }
            if (in_string)
            {
                continue;
            }
            if (c == '{' || c == '[')
            {
                ++depth;
                continue;
            }
            if (c == '}' || c == ']')
            {
                --depth;
                continue;
            }
            if (c == ',' && depth == 0)
            {
                break;
            }
        }

        if (!out.empty())
        {
            out += ",";
        }
        out += pattern;
        out += metadata.substr(begin + pattern.size(), pos - (begin + pattern.size()));
    }

    auto compact_mesh_response_metadata(const std::string& metadata) -> std::string
    {
        if (!metadata_contains_string(metadata, "route", "mesh_first_paint") ||
            metadata_contains_bool(metadata, "research_artifacts_requested", true))
        {
            return metadata;
        }

        std::string out;
        for (const char* key : {
                 "route",
                 "mesh_first_pipeline",
                 "preview_only",
                 "unpreview_only",
                 "front_region_mode",
                 "side_region_mode",
                 "back_region_mode",
                 "paint_region_count",
                 "fill_region_count",
                 "skip_region_count",
                 "brush_pipeline",
                 "brush_pipeline_version_requested",
                 "brush_pipeline_version_supported",
                 "brush_pipeline_version_required",
                 "brush_pipeline_supported_version",
                 "brush_1_size_texels",
                 "brush_2_size_texels",
                 "brush_1_radius_texels",
                 "brush_2_radius_texels",
                 "detail_resolution_percent_requested",
                 "detail_resolution_percent",
                 "detail_resolution_percent_clamped",
                 "detail_resolution_percent_min",
                 "detail_resolution_percent_max",
                 "detail_resolution_percent_default",
                 "detail_refinement_enabled",
                 "detail_refinement_policy",
                 "detail_channel_threshold",
                 "detail_maximum_strokes",
                 "detail_maximum_plan_strokes",
                 "detail_geometry_candidate_limit",
                 "detail_side_source_candidate_limit",
                 "detail_geometry_candidates",
                 "detail_safe_candidates",
                 "detail_eligible_candidates",
                 "detail_selected_samples",
                 "detail_unsafe_candidates",
                 "detail_parent_deduplicated",
                 "detail_cell_deduplicated",
                 "detail_budget",
                 "detail_geometry_overflow",
                 "detail_budget_limited",
                 "detail_brush_radius_texels",
                 "detail_brush_radius_uv",
                 "detail_brush_packed_radius_uv",
                 "fill_stroke_radius_texels",
                 "server_batch_limit",
                 "server_batch_limit_effective",
                 "server_batch_pacing_ms",
                 "server_batch_pacing_requested_ms",
                 "server_batch_pacing_effective_ms",
                 "server_batch_delay_ms",
                 "server_batch_rpc",
                 "server_send_custom_static_pacing",
                 "server_packed_paint_batch_used",
                 "server_packed_paint_batch_route",
                 "server_packed_paint_batch_ignored",
                 "replication_pacing_enabled",
                 "replication_pacing_control",
                 "replication_pacing_source",
                 "replication_pacing_used_contract_fallback",
                 "replication_pacing_fallback_reason",
                 "replication_pacing_requested_batch_limit",
                 "replication_pacing_resolved_batch_limit",
                 "replication_pacing_requested_ms",
                 "replication_pacing_requested_delay_ms",
                 "replication_pacing_resolved_pacing_ms",
                 "replication_pacing_batch_limit",
                 "replication_pacing_ms",
                 "replication_pacing_delay_ms",
                 "replication_pacing_max_outgoing_strokes_per_batch",
                 "replication_pacing_max_outgoing_network_batches_per_second",
                 "replication_pacing_pressure_level",
                 "replication_pacing_backoff_count",
                 "replication_pacing_queue_wait_count",
                 "replication_pacing_queue_gate_limit",
                 "replication_pacing_batch_queue_gate_limit",
                 "replication_pacing_queue_drain_strokes_per_sec",
                 "replication_pacing_send_strokes_per_sec",
                 "replication_pacing_model_eta_ms",
                 "local_batch_limit",
                 "local_batch_pacing_ms",
                 "replication_queued_batch_count",
                 "replication_queued_stroke_count",
                 "replication_max_strokes_per_tick",
                 "replication_estimated_ticks_to_drain",
                 "replication_component_max_replicated_strokes_per_tick",
                 "replication_manager_max_replicated_strokes_per_tick",
                 "replication_manager_max_outgoing_strokes_per_batch",
                 "replication_manager_max_outgoing_network_batches_per_second",
                 "total_strokes",
                 "paint_strokes_submitted",
                 "paint_strokes_rendered",
                 "paint_strokes_completed",
                 "server_batch_calls",
                 "server_batch_success",
                 "server_batch_failures",
                 "server_strokes_sent",
                 "server_batch_elapsed_ms",
                 "server_elapsed_ms",
                 "local_visual_sync_used",
                 "local_visual_sync_elapsed_ms",
                 "local_elapsed_ms",
                 "local_batch_calls",
                 "local_stroke_success",
                 "local_stroke_failures",
                 "local_strokes_synced",
                 "local_strokes_total",
                 "local_apply_route",
                 "local_packed_queue_resolver_status",
                 "local_packed_queue_resolver_failure",
                 "local_packed_queue_exact_manager",
                 "local_packed_queue_exact_manager_class",
                 "local_packed_queue_exact_manager_class_ok",
                 "local_packed_queue_precommit_expected_manager",
                 "local_packed_queue_precommit_resolved_manager",
                 "local_packed_queue_precommit_manager_match",
                 "local_packed_queue_precommit_manager_resolver_exception",
                 "local_packed_queue_precommit_manager_resolver_failure",
                 "local_packed_queue_calls_returned",
                 "local_packed_queue_call_exceptions",
                 "local_packed_queue_strokes_submitted",
                 "local_packed_queue_delta_mismatches",
                 "local_packed_queue_last_queue_before",
                 "local_packed_queue_last_queue_after",
                 "local_packed_queue_last_queue_delta",
                 "local_packed_queue_initial_queue",
                 "local_packed_queue_drain_started",
                 "local_packed_queue_drain_current_queue",
                 "local_packed_queue_drained_strokes",
                 "local_packed_queue_drain_polls",
                 "local_packed_queue_drain_zero_observations",
                 "local_packed_queue_drain_queue_increase_observations",
                 "local_packed_queue_drain_stall_observations",
                 "local_packed_queue_drain_idle_timeout_ms",
                 "local_packed_queue_drain_strokes_per_sec",
                 "local_packed_queue_drain_elapsed_ms",
                 "local_packed_queue_drain_ok",
                 "local_packed_queue_raw_i64_170_before",
                 "local_packed_queue_raw_i64_170_after",
                 "local_packed_queue_raw_i64_170_delta",
                 "per_call_no_resend",
                 "no_resend_resolver_status",
                 "no_resend_resolver_failure",
                 "local_apply_calls_returned",
                 "local_apply_calls_validated",
                 "local_apply_call_exceptions",
                 "local_apply_preflight_complete",
                 "local_apply_preflight_strokes_validated",
                 "local_render_target_write_budget",
                 "local_render_target_writes_scheduled",
                 "local_write_budget_yields",
                 "local_cpu_budget_us",
                 "local_cpu_budget_yields",
                 "local_common_call_total_ms",
                 "local_common_call_max_ms",
                 "local_dispatch_total_ms",
                 "local_dispatch_max_ms",
                 "local_apply_preflight_failure",
                 "local_apply_postcondition",
                 "local_apply_completion_semantics",
                 "local_apply_cpu_submission_ok",
                 "internal_no_resend_max_calls_per_tick",
                 "server_call_completion_semantics",
                 "partial_commit",
                 "server_local_diverged",
                 "automatic_retry_safe",
                 "total_replay_elapsed_ms",
                 "paint_elapsed_ms",
                 "paint_eta_ms",
                 "replay_pass_order",
                 "replay_region_order",
                 "replay_fill_end",
                 "replay_coarse_end",
                 "replay_fine_begin",
                 "replay_server_offset",
                 "replay_local_offset",
                 "replay_server_current_pass",
                 "replay_server_current_pass_start",
                 "replay_server_current_pass_end",
                 "replay_local_current_pass",
                 "replay_local_current_pass_start",
                 "replay_local_current_pass_end",
                 "replay_current_pass",
                 "replay_current_pass_start",
                 "replay_current_pass_end",
                 "replay_progress_source",
                 "replay_current_pass_completed",
                 "replay_current_pass_total",
                 "replay_current_pass_eta_ms",
                 "replay_effective_fill_end",
                 "replay_effective_coarse_end",
                 "replay_effective_fine_begin",
                 "replay_strokes_fill",
                 "replay_strokes_coarse_paint",
                 "replay_strokes_fine_paint",
                 "replay_strokes_detail_refinement",
                 "replay_plan_detail_refinement_count",
                 "replay_strokes_fill_candidates",
                 "replay_strokes_fill_coarse_skipped",
                 "replay_strokes_coarse_paint_candidates",
                 "replay_strokes_coarse_paint_deduplicated",
                 "replay_spatial_order",
                 "replay_spatial_reference_z_min",
                 "replay_spatial_reference_z_max",
                 "replay_spatial_reference_position_fallback_used",
                 "replay_spatial_reference_position_fallback_candidates",
                 "replay_spatial_fallback_order",
                 "replay_spatial_camera_right_available",
                 "replay_spatial_sort_partitions",
                 "replay_spatial_order_violations",
                 "replay_spatial_sort_elapsed_ms",
                 "progress_updates_written",
                 "progress_updates_suppressed",
                 "replay_pass_boundary_limited_batches",
                 "progress_min_interval_ms",
                 "first_failure",
                 "pid",
                 "port",
                 "cancel_reason",
                 "cancel_after_submission",
                 "queued_rendering_drained",
                 "rendering_remaining_strokes",
                 "paint_strokes_remaining_total",
                 "research_artifacts_requested",
             })
        {
            append_metadata_field(out, metadata, key);
        }
        return out.empty() ? metadata : out;
    }

    auto compact_mesh_progress_metadata(const std::string& metadata) -> std::string
    {
        std::string out;
        for (const char* key : {
                 "progress_schema_version",
                 "phase",
                 "terminal",
                 "result",
                 "detail_resolution_percent",
                 "detail_channel_threshold",
                 "detail_maximum_strokes",
                 "detail_budget",
                 "detail_selected_samples",
                 "detail_brush_radius_texels",
                 "paint_elapsed_ms",
                 "paint_eta_ms",
                 "replay_pass_order",
                 "replay_fill_end",
                 "replay_coarse_end",
                 "replay_fine_begin",
                 "replay_server_offset",
                 "replay_local_offset",
                 "replay_server_current_pass",
                 "replay_server_current_pass_start",
                 "replay_server_current_pass_end",
                 "replay_local_current_pass",
                 "replay_local_current_pass_start",
                 "replay_local_current_pass_end",
                 "replay_current_pass",
                 "replay_current_pass_start",
                 "replay_current_pass_end",
                 "replay_progress_source",
                 "replay_current_pass_completed",
                 "replay_current_pass_total",
                 "replay_current_pass_eta_ms",
                 "cancel_requested",
                 "cancel_reason",
                 "failure_stage",
                 "first_failure",
                 "remaining_strokes",
                 "cancel_phase",
                 "server_batch_limit",
                 "server_batch_pacing_ms",
                 "server_batch_delay_ms",
                 "replication_pacing_enabled",
                 "replication_pacing_control",
                 "replication_pacing_source",
                 "replication_pacing_used_contract_fallback",
                 "replication_pacing_fallback_reason",
                 "replication_pacing_requested_batch_limit",
                 "replication_pacing_resolved_batch_limit",
                 "replication_pacing_requested_ms",
                 "replication_pacing_requested_delay_ms",
                 "replication_pacing_resolved_pacing_ms",
                 "replication_pacing_batch_limit",
                 "replication_pacing_ms",
                 "replication_pacing_delay_ms",
                 "replication_pacing_max_outgoing_strokes_per_batch",
                 "replication_pacing_max_outgoing_network_batches_per_second",
                 "replication_pacing_pressure_level",
                 "replication_pacing_backoff_count",
                 "replication_pacing_queue_wait_count",
                 "replication_pacing_queue_gate_limit",
                 "replication_pacing_batch_queue_gate_limit",
                 "replication_pacing_queue_drain_strokes_per_sec",
                 "replication_pacing_send_strokes_per_sec",
                 "replication_pacing_model_eta_ms",
                 "replication_queued_batch_count",
                 "replication_queued_stroke_count",
                 "replication_max_strokes_per_tick",
                 "replication_estimated_ticks_to_drain",
                 "server_batch_rpc",
                 "server_packed_paint_batch_enabled",
                 "server_packed_paint_batch_use_relay",
                 "server_batch_calls",
                 "server_batch_success",
                 "server_batch_failures",
                 "server_strokes_sent",
                 "local_strokes_synced",
                 "local_packed_queue_resolver_status",
                 "local_packed_queue_resolver_failure",
                 "local_packed_queue_exact_manager",
                 "local_packed_queue_calls_returned",
                 "local_packed_queue_call_exceptions",
                 "local_packed_queue_strokes_submitted",
                 "local_packed_queue_delta_mismatches",
                 "local_packed_queue_last_queue_before",
                 "local_packed_queue_last_queue_after",
                 "local_packed_queue_last_queue_delta",
                 "local_packed_queue_initial_queue",
                 "local_packed_queue_drain_started",
                 "local_packed_queue_drain_current_queue",
                 "local_packed_queue_drained_strokes",
                 "local_packed_queue_drain_polls",
                 "local_packed_queue_drain_zero_observations",
                 "local_packed_queue_drain_queue_increase_observations",
                 "local_packed_queue_drain_stall_observations",
                 "local_packed_queue_drain_idle_timeout_ms",
                 "local_packed_queue_drain_strokes_per_sec",
                 "local_packed_queue_drain_elapsed_ms",
                 "local_packed_queue_raw_i64_170_before",
                 "local_packed_queue_raw_i64_170_after",
                 "local_packed_queue_raw_i64_170_delta",
                 "local_packed_queue_last_thread_id",
                 "no_resend_resolver_status",
                 "no_resend_resolver_failure",
                 "local_apply_calls_validated",
                 "local_apply_call_exceptions",
                 "local_apply_preflight_complete",
                 "local_apply_preflight_strokes_validated",
                 "local_batch_limit",
                 "local_batch_pacing_ms",
                 "local_render_target_write_budget",
                 "local_render_target_writes_scheduled",
                 "local_write_budget_yields",
                 "local_cpu_budget_us",
                 "local_cpu_budget_yields",
                 "local_common_call_total_ms",
                 "local_common_call_max_ms",
                 "local_dispatch_total_ms",
                 "local_dispatch_max_ms",
                 "progress_updates_written",
                 "progress_updates_suppressed",
                 "progress_min_interval_ms",
                 "local_apply_preflight_failure",
                 "local_apply_postcondition",
                 "local_apply_completion_semantics",
                 "local_apply_cpu_submission_ok",
                 "internal_no_resend_max_calls_per_tick",
                 "partial_commit",
                 "server_local_diverged",
                 "automatic_retry_safe",
             })
        {
            append_metadata_field(out, metadata, key);
        }
        return out.empty() ? metadata : out;
    }

    auto response_json(bool success,
                       const char* stage,
                       int applied,
                       int failures,
                       const std::string& message,
                       const std::string& metadata = "") -> std::string
    {
        std::string out = "{\"success\":";
        out += success ? "true" : "false";
        out += ",\"stage\":\"";
        out += stage;
        out += "\",\"applied\":";
        out += std::to_string(applied);
        out += ",\"failures\":";
        out += std::to_string(failures);
        out += ",\"message\":\"";
        out += json_escape(message);
        out += "\",\"timing_ms\":{},\"metadata\":{\"bridge\":\"meccha-direct-bridge-v1\"";
        if (!metadata.empty())
        {
            out += ",";
            out += compact_mesh_response_metadata(metadata);
        }
        out += "}}\n";
        return out;
    }
