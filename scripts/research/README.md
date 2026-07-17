# Runtime Research Notes

This directory is for report-only or explicitly invoked runtime investigation
helpers. These scripts are not part of normal app startup, normal paint, or
release packaging.

Use this area for research notes and future authenticated clients. Standalone
fixed-port bridge probes were removed in v1.6: every direct bridge uses an
instance-selected port and requires the GUID/token HELLO handshake documented
in [`docs/runtime-direct-bridge.md`](../../docs/runtime-direct-bridge.md).
Do not reintroduce port scanning or unauthenticated command scripts.

The native research commands remain available only through an explicitly
authenticated controller-owned bridge. `MECCHA_RESEARCH_ARTIFACTS=1` alone does
not enable event-watch: the staged bridge must receive its `.eventwatch.path`
or `.eventwatch` sidecar before injection.

An explicitly built diagnostic runner from this checkout has this entry point:

```text
meccha-mod-menu.exe --research-replication --pid <exact-pid> \
  --role host|joining-client --out <local-artifact-directory> [--paint] \
  [--hold-seconds N] [--pressure-sample-ms N] [--texture-snapshot] \
  [--texture-target resolved|eventwatch-multicast-packed-receiver --texture-discovery-seconds N] \
  [--paint-mode packed-local-queue|combined|combined-no-resend|local-only|packed-only] [--stroke-limit N] \
  [--replay-stroke-index N] \
  [--front-mode paint|fill|skip] [--side-mode paint|fill|skip] [--back-mode paint|fill|skip] \
  [--batch-limit 1..20] [--batch-pacing-ms 50..500] \
  [--cancel-after-ms N | --shutdown-after-ms N] [--fill-color '#RRGGBB'] \
  [--paint-color '#RRGGBB'] [--packed-radius-scale 0.5..4.0] \
  [--triangle-world-radius]
```

It requires `MECCHA_RESEARCH_ARTIFACTS=1`, stages the sidecar before the
injector starts, and uses the same authenticated bridge for probes and optional
normal packed paint. It follows normal direct-bridge behavior for a bridge DLL
already resident in the target: the new controller-owned instance is injected
rather than rejected or reused. Do not start a second research runner while a
research runner already owns an active event-watch bridge. Treat an observed
event-watch initialization or shutdown failure as an invalid run; restart the
game only when that evidence requires recovery. The runner records those as
`bridge_start_failed` or `eventwatch_cleanup_failed`; it does not turn either
condition into a blanket prohibition on reinjection.

For lifecycle-only same-module verification, `same-module-reinject.ps1` reuses
an already-loaded bridge DLL path and invokes the built direct injector with a
new authenticated identity on each iteration. It performs `ping` then
`shutdown`, waits for `event_watch_stopped`, and writes one JSON result per
iteration. This is a host-only teardown/restart test; it sends no paint and
does not provide EOS or joining-client evidence.

For a passive observer, combine `--hold-seconds` with
`--pressure-sample-ms` (250 through 10000). This records authenticated,
game-thread `paint_replication_pressure_probe` snapshots during the hold; it
does not send paint or texture-sync requests.

`--texture-snapshot` is an explicit, low-frequency diagnostic: it records
before/after checksums for the selected component's Albedo, Metallic, and
Roughness channels. It also retains a PNG and changed-pixel mask for that
component's **Albedo only**; do not interpret its zero count as proof that
Metallic or Roughness did not change. It deliberately does not export unrelated
components, so the observation adds as little game-thread work as possible.
The default target is the controller-resolved component. On a joining client,
`--texture-target eventwatch-multicast-packed-receiver` instead pins the exact
`MulticastPackedPaintBatch` ProcessEvent receiver. It requires
`--texture-discovery-seconds N`: send one harmless packed discovery stroke from
the host while the observer waits, then send the measured stroke after its
`texture-before.json` appears. The runner fails closed if that receiver is
missing, stale, or changes before the after probe. It does not freeze multicast
traffic; instead, each probe must prove that its export target is still the
discovered receiver. It does not call import, texture-sync, or paint functions.
The before/after readbacks run outside the pressure-sampling loop, but their
duration is not receiver-drain evidence. Hash inequality alone is not
visual-coverage proof: a few tiny dots change the whole-texture hash.

`--texture-snapshot` and `--shutdown-after-ms` are intentionally incompatible:
a scheduled shutdown cannot safely produce the required after snapshot and
changed-pixel PNG.

Every successful research paint requests a post-limit UV replay sidecar and
renders `uv-replay-atlas.png`: Fill, Brush 1, and Brush 2 occupy separate
columns, with planner and packed-wire radii in separate rows. The atlas is a
bounded proportional PNG even when the game atlas is large. A crossed marker
in the packed row means the route had no packed encoding (for example
`local-only`), not that it used the planner radius on the wire. A cancelled or
failed paint deliberately does not stage this planning-time sidecar as rendered
evidence. `--replay-stroke-index N` selects exactly one original post-plan
stroke for a controlled footprint comparison; its index is the full replay
order, before the selector reduces the emitted plan to one entry.

`--paint-mode` and `--stroke-limit` are research-only A/B controls.
`packed-local-queue` is the current production-shaped route: every successful
server packed batch is decoded directly into the painter's exact game-owned
receiver queue with the same stroke boundary and cadence, and is selected when
`--paint-mode` is omitted. `combined` and
`combined-no-resend` retain the superseded per-stroke internal-common route for
controlled comparison. `local-only` keeps the reflected `PaintAtUVWithBrush`
local call but suppresses the runner's explicit packed RPC; `packed-only` keeps
the packed RPC but suppresses the local call. A positive stroke limit
truncates the planned replay after planning so small attribution tests do not
create another full-size replication backlog. These controls are included only
in the explicit research payload and require the same environment gate.
`--fill-color` overrides the in-memory runner settings for that one diagnostic
payload only; it does not write the normal settings file. Use it with
`--texture-snapshot` when a distinct color is needed to make a checksum change
observable on an already-painted mesh.

`--paint-color` is a research-only deterministic coverage control. It skips
source-color capture/transfer for that payload and assigns one constant color
to Paint samples while retaining normal geometry planning, pass construction,
packed serialization, RPC pacing, and the native local receiver queue. It does
not alter production settings. `--packed-radius-scale` changes only the packed
brush radius for controlled footprint A/B runs; the planner spacing remains at
the configured Brush 2 step. When omitted, production derives one scale for the
validated mesh/job from the UV-area-weighted runtime triangle world/UV scale and
the live mesh bounds diameter, with the measured fold/seam safety factor. Pass
`1.0`, `2.0`, or another explicit value only for an A/B. The scale applies to
packed mesh-anchor strokes, not preview or direct-UV-only routes.
`--triangle-world-radius` keeps the UV radius
unchanged but derives each packed anchor's effective world radius from that
triangle's UV-to-world Jacobian. It is an explicit research A/B control, not a
production setting.

`--front-mode`, `--side-mode`, and `--back-mode` override region modes only in
the research runner. They minimize a reproduction (for example, isolating
Front Paint from camera-dependent unsafe Side/Back mappings) while still using
the normal payload and native planner.

`--batch-limit` and `--batch-pacing-ms` carry the same two remote-lane controls
as the production sliders. The fastest defaults are 20 strokes per packed RPC
and 50 ms between remote dispatches. The accepted ranges are 1--20 strokes and
50--500 ms; zero pacing is deliberately rejected because an immediate repost
loop can monopolize the game thread. To reproduce the former conservative
comparison without a special mode, pass `--batch-limit 6 --batch-pacing-ms 75`.
The production-shaped packed local route uses the same batch limit and pacing
as the server lane. Painter-local receiver backlog is observed but is not used
as evidence of a remote peer's EOS/game queue and therefore does not slow the
outgoing lane; the game's own receiver/render budgets drain it asynchronously.
The legacy `combined` A/B route still has its 6-call/4-ms CPU yield. Recurring
scheduler wakeups are always delayed by at least 1 ms. These controls change
neither EOS settings nor game-owned component/manager limits.

`--cancel-after-ms` (1 through 60000) requires `--paint` and is an explicit
native-cancellation test. The runner starts the normal authenticated paint
request, waits the requested number of milliseconds, then sends `cancel_paint`
concurrently through the same controller-owned bridge instance and awaits both
terminal replies. It writes the full replies to `paint-reply.json` and
`cancel-paint-reply.json`, plus their outcome fields and the native cancelled-job
count to `run-summary.json`. If cancel initially arrives before the native paint
handler admits its job, the runner retries briefly while the paint request is
still in flight. Native may instead report `cancel_latched_paint_request=true`:
that is accepted as an admission-time cancellation even when both job counts are
zero. A response with neither a job count nor that latch, or a paint reply that
does not terminate as cancelled, makes the run fail; increase the stroke limit
or shorten the delay and repeat as a new run. Omitting the option preserves the
normal paint behavior. In `packed-local-queue` mode each paired server/local
commit is additionally capped by the exact local component queue's remaining
capacity, at most one configured batch. A cancel therefore prevents every later
paired server RPC and local enqueue; only the already committed bounded local
tail drains naturally before the terminal cancelled reply. It never clears
recorded strokes, rewrites queue memory, or claims a joining client has stopped
rendering previously sent work.

`--shutdown-after-ms` has the same 1-through-60000 range and also requires
`--paint`; it is mutually exclusive with `--cancel-after-ms`. The runner starts
paint, delays, then calls `ShutdownAsync` concurrently through the same
authenticated bridge instance. It writes `shutdown-during-paint-reply.json`
and the paint terminal reply, requires a successful native shutdown whose
metadata reports `active_paint_quiescent=true`, requires the paint request to
terminate as cancelled, and requires event-watch to reach
`event_watch_stopped`. Because this shutdown is the operation under test, the
runner skips its normal `finally` shutdown even when the result is
indeterminate; it does not obscure the evidence with an automatic retry.

Normal production paint keeps the explicit server packed RPC and invokes the
validated native implementation behind `MulticastPackedPaintBatch` directly,
without calling the reflected multicast UFunction. A synthetic non-self source
GUID lets the receiver accept the local copy without changing component state.
The resolver verifies the current Shipping PE/text identity, reflected payload
layout, UFunction thunk, vtable slot, decoder, component-to-manager resolver,
and enqueue chain. It pins the exact manager used by the component and requires
an exact component-queue increase before accepting each paired batch. Any
mismatch fails closed; there is no per-stroke or reflected fallback. Queue
submission and checksum change are separate evidence: a returned receiver call
does not itself prove pixel/render completion.

The planner uses `Fill`, `CoarsePaint`, then `FinePaint`; each pass retains
`Back`, `Side`, then `Front`. Fill runs once, Paint regions receive a deduplicated
Brush 1 pass (10--30, default 30) and then the full Brush 2 pass (5--10, default
10), and Skip emits nothing. Pass boundaries never share a packed RPC. Within
each pass/region partition, bind/reference-pose Z supplies head-to-feet rows and
camera-right supplies left-to-right order; runtime surface Z is only a fallback.
Remote and local lanes consume the same final stroke vector.

For repeatable process/game-thread CPU sampling around a runner invocation, use
`sample-process-thread-cpu.ps1`. It records raw 100-ms cumulative samples and
idle/active/post interval summaries; it is not an FPS or GPU measurement.

For a non-elevated host-side GPU signal, use `sample-process-gpu.ps1` with the
same runner arguments. It samples only `GPU Engine(*)\Utilization Percentage`
instances whose path contains the target PID and writes `gpu-samples.csv` and
`gpu-summary.json`. Windows performance-counter collection can take roughly a
second per sample on some systems, so this is a coarse attribution signal, not
an Unreal frame-time, RenderThread, or GPU queue trace. It does not read back or
import any texture.

Single-host runs prove planning, local submission, packed-RPC submission,
pacing, cancellation, shutdown, and injection lifecycle. They cannot prove
EOS P2P arrival, joining-client multicast ordering, receiver queue drain, or a
remote texture update. Do not label the default 20/50 ms slider settings as
joining-client validated until that topology is available again.

Build it without replacing the normal `.build/` package output:

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/research/build-replication-runner.ps1
```

## Policy

- Keep production paint behavior out of `scripts/research/`.
- Keep generated research output under `artifacts/research/` or a local temp
  directory.
- Do not commit game archives, mappings, dumps, event-watch output, or bridge
  replay payloads.
- Prefer adding a documented authenticated research client over adding one-off
  command strings to user-facing UI.
