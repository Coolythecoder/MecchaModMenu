# Runtime Dead-Code Candidate Classification

This document tracks dead-code classification and cleanup status. The first
classification pass was generated from `make review-dead-code` after commit
`66b7cae`.

No native runtime code is deleted in this pass. The goal is to separate real
delete candidates from dynamic/reflection/research code that only looks unused
to static search.

## Summary

Highest-confidence cleanup candidates were outside the active packed paint path:

- legacy WPF controller project
- unused localization strings for removed batch/adaptive controls
- legacy config write/edit surface for `adaptive_batching`,
  `server_batch_limit`, and `server_batch_delay_ms`
- old non-packed native paint RPC dispatch

Cleanup has removed the production old-RPC dispatch, but event-watch and
research inventory patterns may still mention old route names so regressions can
be detected.

## Keep: Dynamic Entries

These are not dead even when references are sparse:

- `DllMain`
- Win32 hook/message dispatch
- bridge listener command dispatch
- C# / WebView2 command strings:
  - `ping`
  - `capabilities`
  - `paint_full_route`
  - `cancel_paint`
  - `shutdown`
  - `paint_replication_probe`
  - `paint_replication_pressure_probe`
  - `paint_packed_replay_probe`

Reason: runtime entry is by LoadLibrary, Win32 callbacks, TCP IPC, or
WebView2/C# message text rather than ordinary C++ references.

## Keep: Reflection And SDK Layout

These are not dead solely because static references are hard to follow:

- `ProcessEvent` wrappers and vtable lookup
- `FName`, `UObject`, `UFunction`, `FProperty` scanning helpers
- RPC parameter structs and padding/static assertions
- reflected function/property name strings

Reason: renaming, reordering, or deleting these can break binary layout or
runtime lookup without a compile error.

## Keep: Research-Only

These are intentionally outside production behavior but should remain available
for multiplayer and game-update investigations:

- `paint_replication_probe`
- `paint_replication_pressure_probe`
- `paint_packed_replay_probe`
- event-watch sidecar
- `MECCHA_RESEARCH_ARTIFACTS` debug output path
- projection/UV debug artifacts

Rule: keep them out of normal UI and normal paint decisions, but do not delete
until their investigation value is replaced by a better tool.

## Removed: WPF Controller Project

Classification: removed.

Evidence:

- canonical `scripts/build.ps1` publishes
  `runtime/csharp/MecchaCamouflage.WebHost/MecchaCamouflage.WebHost.csproj`
- build script does not publish or test
  `runtime/csharp/MecchaCamouflage.Wpf/MecchaCamouflage.Wpf.csproj`
- WPF still contains old delay/batch-related UI, while the supported WebHost UI
  hides legacy batch tuning

Risk:

- low for release artifact if WebHost is the only supported controller
- medium for developers if anyone still runs the WPF project manually

Result:

- deleted `runtime/csharp/MecchaCamouflage.Wpf/`
- canonical build still publishes WebHost only

## Removed: Batch/Adaptive Localization Keys

Classification: removed.

Keys:

- `batch.size`
- `batch.delay`
- `adaptive.batching`

Evidence:

- Web UI no longer exposes legacy batch/adaptive controls
- search hits are only localization data, tests/docs, and legacy WPF surface

Risk:

- low after WPF removal
- medium before WPF removal because WPF still has legacy controls

Result:

- removed these keys from every locale
- locale completeness test still passes

## Removed: Legacy Settings Edit/Write Surface

Classification: removed from the current settings model and config writer.

Fields:

- `PaintSettings.AdaptiveBatching`
- `PaintSettings.ServerBatchLimit`
- `PaintSettings.ServerBatchDelayMs`
- serialized keys:
  - `adaptive_batching`
  - `server_batch_limit`
  - `server_batch_delay_ms`

Previous state:

- old values are not sent in the normal paint payload
- Web UI snapshot no longer exposes `adaptiveBatching` or `serverBatchLimit`
- settings loader still reads/clamps/writes them for old config compatibility
- progress formatting still uses batch/pacing values coming from bridge

Risk:

- medium. Removing read compatibility can silently discard old configs.
- low to stop writing old keys once a release has migrated users.

Result:

- removed the model fields
- stopped reading and writing the old config keys
- removed the legacy clamp test
- progress display still accepts bridge progress fields with similar names
  because those are runtime telemetry, not user settings

## Removed: Legacy Native Non-Packed Paint RPC Dispatch

Classification: production dispatch removed.

Code families:

- `ServerPaintBatch`
- `ServerCompactPaintBatch`
- `SendCustomStrokeBatchToServer`
- `FCompactPaintStroke` / `FCompactPaintStrokeBatch`
- `sdk_call_server_paint_batch`
- `sdk_call_server_compact_paint_batch`
- compact/send-custom metadata fields

Evidence:

- normal paint requires the packed component route
- failure to prepare packed route stops paint instead of falling back

Result:

- removed production context/job fields for old route function pointers
- removed `ServerPaintBatch` / `ServerCompactPaintBatch` dispatch helpers
- removed compact paint payload generation helpers
- removed old compact/server batch SDK parameter structs
- made paint component discovery prefer `ServerPackedPaintBatch`
- trimmed normal metadata and probe candidates to packed routes

Remaining intentional references:

- event-watch can still observe old route names if the game calls them
- review inventory still searches old route names as regression indicators

## Candidate: Internal `adaptive_*` Names

Classification: `RENAME_CANDIDATE`, not dead code.

Evidence:

- UI no longer lets users tune adaptive batching
- bridge still uses these fields for packed-route pacing, queue gate, drain
  estimate, and progress text

Risk:

- high if deleted: this code still protects packed replication pacing

Next action:

- rename internal terms from `adaptive_*` to `replication_pacing_*` or
  `packed_queue_gate_*`
- keep JSON compatibility for progress parsing during the rename
- do this as a behavior-preserving refactor after beta.3 stabilization

## Candidate: Native Metadata Noise

Classification: `DELETE_CANDIDATE`, low priority.

Examples:

- metadata fields that only report ignored old routes
- fields describing disabled texture sync experiments
- old `experimental_*_requested` metadata once no caller can send them

Risk:

- low for behavior
- medium for issue triage because old logs may become harder to compare

Next action:

- remove only fields that are not used by WebHost progress parsing, community
  diagnostics, or research scripts

## Not Candidates

Do not treat these as dead in this cleanup cycle:

- `ServerPackedPaintBatch` route and payload structs
- packed source id read at component offset `0x2A8`
- local visual sync with `PaintAtUVWithBrush`
- preview/unpreview snapshot code
- runtime triangle cache and unsafe-sample guards
- startup diagnostics, asset cache repair, fixed WebView2 setup
- injector phase diagnostics

These are active beta.3 behavior or high-risk infrastructure.

## Suggested Cleanup Order

1. Decide whether WPF is supported. If not, remove/archive WPF first.
2. Remove unused batch/adaptive localization keys after WPF is gone.
3. Stop writing legacy batch/adaptive config keys while keeping read
   compatibility.
4. Rename internal `adaptive_*` pacing names to packed replication pacing names.
5. After multiplayer validation, remove old non-packed native paint RPC dispatch.
6. Re-run `make review-dead-code` and update this document after each cleanup.
