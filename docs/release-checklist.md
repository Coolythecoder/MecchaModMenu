# Release Checklist

This checklist is version-independent. Keep release decisions in GitHub issues
or release notes; keep this file focused on the repeatable process.

Do not tag a release until maintainer-owned game checks are complete.

## Local Checks

Run these before preparing a tag:

```bash
git status --branch --short
make review-dead-code
make clean
make package
git diff --check
```

`make clean` removes `.build/` only. It does not remove `artifacts/`, because
review and research reports are often useful during investigation.

Use `make clean-artifacts` only when ignored review/research reports should be
discarded. Use `make clean-all` to remove both `.build/` and `artifacts/`.

Confirm:

- release artifact is a single EXE under `.build/package/`
- `ReleaseSingleFile` output and package output contain no `.pdb`, `.dbg`, or
  `.ilk` files
- the normal release build excludes the research command runner and cannot
  enable WebView DevTools through `MECCHA_RESEARCH_ARTIFACTS`
- app uses the installed shared Evergreen Runtime; no browser runtime files are embedded
- root directory has no generated `*.dll` or `*.exe`
- source tree has no generated `bin/` or `obj`
- shipped app resources are under `resources/`
- source code is under `src/`

## Runtime Packaging Checks

The packaged EXE must include:

- native bridge DLL
- native injector EXE
- official WebView2 Evergreen bootstrapper
- Web UI assets
- mesh profile resources
- app icon resources

The app must extract these into LocalAppData and repair missing or corrupt
runtime cache files automatically.

## Maintainer Game Checks

These require MECCHA CHAMELEON.

- Start the app with no game running.
  - GUI initializes.
  - error state is clear and diagnostic.
  - no unhandled .NET dialog appears.
- Start the app with the game in menu or lobby.
  - bridge state is clear.
  - paint in lobby fails as a paint-time pawn/component error, not startup
    failure.
- Start the app in a valid paintable match.
  - preview applies.
  - unpreview restores.
  - repeated unpreview shows a guard warning.
  - cancel with no active paint shows a guard warning.
  - normal paint completes.
  - progress shows packed pacing and queue/drain data.
- Delete the LocalAppData runtime cache and restart.
  - cache rebuilds automatically.
  - WebView2 starts from the installed Evergreen Runtime, or prompts to install it with the embedded bootstrapper.
- Restart the controller against the same game process.
  - the new controller instance starts and authenticates its own direct bridge.
  - older direct bridges or any other resident module do not produce a restart-required state.
  - an indeterminate injector timeout is diagnosed and requires an explicit retry; it does not trigger unload or thread termination.
- Cancel and then shut down separate paints while each is still in the planning
  phase. Each command must report one active job, the paint must reach a
  cancelled terminal reply, shutdown must report
  `active_paint_quiescent=true`, and an immediate fresh injection must complete
  without restarting the game.

## v1.6 Direct Bridge and WebView2 Gates

- Test direct injection on Windows 10 and Windows 11 with a clean game,
  multiple same-name game processes, target exit during injection, concurrent
  host launches, and old direct bridges/modules already resident in the game.
- Run 25 sequential direct injections into one game process. Every successful
  connection must match the selected PID, generated instance GUID, token, and
  bridge hash; no attempt may become restart-required because an old module is
  present.
- Verify a timeout never frees remote path/start-block memory before the
  corresponding remote thread exits. The production path must contain no
  `TerminateThread`, unload, switch, or loader fallback. The source tree and
  packaged output contain no loader component.
- Verify Evergreen WebView2 with a runtime already present, absent with a
  successful bootstrapper install, bootstrapper failure/offline behavior, a
  clean user-data folder, two rapid app launches, and a forced WebView process
  failure. Diagnostics must identify the failed startup stage.

## Module SDK Process-Memory Gates

- Validate the documented `process-memory-example` with the production module
  catalog before packaging. Its manifest must request only
  `process.memory.read` and `process.memory.write`.
- Against a valid attached game, click the example's self-test once and require
  a visible `PASS`. The sequence must create only a module-owned allocation,
  inject or write a deterministic sentinel, read and compare every byte, change
  the complete allocation to `read-only`, restore `read-write`, and free it.
  The game must remain responsive and the bridge PID, instance GUID, and hash
  must remain the authenticated values throughout the sequence.
- Repeat the self-test after a module reload and after restarting the controller
  against the same game. No stale iframe generation may complete a late native
  write, and a request admitted for an old bridge instance must not run against
  the replacement instance.
- Separately retain one owned allocation across a module reload and verify the
  reloaded package with the same module ID can free it. Then leave one owned
  allocation outstanding and verify authenticated bridge shutdown releases it;
  no allocation address may carry into the next bridge instance.
- Verify a module cannot submit a PID, bridge endpoint, owner ID, or another
  module's allocation identity. Cross-module free, double free, zero addresses,
  overflowing ranges, malformed/odd hexadecimal bytes, inaccessible pages, and
  unknown protection values must fail without crashing the game or host.
- Exercise the exact limits: a 3 MiB transfer succeeds and 3 MiB plus one byte
  fails; a 64 MiB allocation succeeds and 64 MiB plus one byte fails; all
  modules combined can own exactly 256 MiB and the next allocation fails.
  Failures must reject the original request, never clamp, truncate, zero-fill,
  or report partial success.
- Verify read-only modules cannot allocate, write, protect, inject, or free;
  write-only modules cannot read. Removing or revoking a permission during
  reload must prevent every newly admitted operation.
- With no game, a stopped bridge, multiple same-name processes, and a game that
  exits during a request, verify raw-memory calls fail against the unavailable
  authenticated target and never fall back to another process.
- Confirm all eight documented protection names round-trip exactly. Allocate
  and inject default to `read-write`; `execute-read` and other executable values
  change page protection only. No SDK command creates a thread, calls an
  address, or executes injected bytes.

## Multiplayer Checks

Collect these separately for painter-as-host and painter-as-joining-client:

- whether other players see the paint
- painter-side completion time
- other-client visible completion time
- delay between painter completion and other-client completion
- crashes, disconnects, lobby returns, freezes, missing paint, or partial paint
- event-watch counts if available:
  - `ServerPackedPaintBatch > 0`
  - `SendCustomStrokeBatchToServer == 0`
  - `ServerRelayPackedStrokeBatch == 0`
  - `PaintAtUVWithBrush == 0`
  - legacy full-stroke multicast calls are `0`
- production-route metadata:
  - `local_packed_queue_resolver_status == "resolved"`
  - `local_packed_queue_exact_manager_class_ok == true`
  - packed/local batch boundaries and stroke totals are identical
  - `local_packed_queue_strokes_submitted == server_strokes_sent`
  - `local_packed_queue_delta_mismatches == 0`
  - `local_packed_queue_call_exceptions == 0`
  - `server_local_diverged == false` on interrupted runs
  - `packed_mesh_radius_calibration_ok == true`
  - `packed_mesh_radius_calibration_invalid_triangles == 0`
  - the effective scale, mesh bounds diameter, and weighted local/UV scale are
    finite and identical for server/local construction of the job
  - per-pass effective subdivision level/pixel-size/template-resolution are
    all zero sentinels before packed decode
- before/after texture probes show a nontrivial changed-texel area on the
  painter's resolved component after queue drain. Record
  `texture_delta.pixels_changed_any_channel` and its ratio; checksum inequality
  alone is insufficient because a few tiny dots also change the hash. Collect
  corresponding remote-client changed-texel evidence after replication and
  require both replication queues to return to zero. Inspect the coordinate
  dump or the character as well: a regular point grid with gaps is a failure
  even when changed-texel count is nontrivial

Do not release if joining-client paint crashes the server, or if other clients
finish closer to the old multi-minute replication drain path than to the new
packed route.
