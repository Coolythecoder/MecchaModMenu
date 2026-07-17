# Runtime Paint Replication Validation

This document records the durable conclusions from the Issue #87 multiplayer
paint investigation. It is not a release claim: a result is valid only for the
tested game build, topology, and route described here.

Runtime logs, event-watch data, texture exports, screenshots, and injected
artifacts are intentionally kept outside the repository. The relevant runner
and collection procedure are documented in
[`scripts/research/README.md`](../scripts/research/README.md).

The normal ReleaseSingleFile build does not compile the research runner or
enable WebView DevTools through `MECCHA_RESEARCH_ARTIFACTS`. Use the explicit
research build script when those capabilities are required.

## Evidence Terms

- **Verified**: supported by code inspection and one or more runtime probes,
  event-watch captures, queue snapshots, texture exports, or controlled A/B
  tests.
- **Tested**: exercised in a named runtime topology, but not generalized beyond
  it.
- **Unverified**: plausible or observed once, but insufficient for a product or
  release claim.

## update2.8.0 build contract

The latest statically revalidated target is the 17 July 2026 Steam release
`update2.8.0`, public Build ID `24256862`, depot `4704691`, manifest
`4871951631373356969`. The release label comes from the
[official announcement](https://steamcommunity.com/games/4704690/announcements/detail/711155348639056585);
the Build ID mapping is also recorded by
[SteamDB](https://steamdb.info/patchnotes/24256862/).

| Executable identity | update2.8.0 value |
| --- | ---: |
| SHA-256 | `91B6CC5A75F22CDBE81D3FD6A37F7F894931F04A1E5BDCC652740A73558B2CDA` |
| PE timestamp | `0x047BA867` |
| `SizeOfImage` | `0x0AAFD000` |
| PE checksum | `0x0A7328BB` |
| raw `.text` size | `0x07AA2800` |
| raw `.text` bridge FNV-1a-style 64 (basis `1469598103934665603`) | `0x079EDD688D78E96B` |

The native packed receiver contract for this build is:

| Target | RVA |
| --- | ---: |
| `MulticastPackedPaintBatch` thunk | `0x50E39A0` |
| vtable implementation | `0x50FD9F0` |
| packed decoder | `0x5105740` |
| enqueue inner | `0x50F5CF0` |
| compact-stroke expander | `0x50F63B0` |
| skeletal preflight | `0x50F5F20` |
| component-context resolver | `0x3AEACE0` |
| manager resolver | `0x50E8A70` |
| manager enqueue | `0x510AD60` |
| queue coalescer | `0x510AB60` |

The retained internal no-resend research route uses thunk `0x50E46E0`,
implementation `0x5100700`, stroke constructor `0x50D9F60`, and common apply
function `0x50ED9C0`.

These addresses were recovered independently from instruction signatures and
relative-call edges, then checked against every existing route byte contract.
The packed gate now also verifies the compact-stroke expander to skeletal
preflight edge and the preflight's mesh-bounds/world-radius behavior. The old
Steam contracts remain available only behind their own exact PE and `.text`
identities.

The shipped `paintman` and `paintman_cube` profiles did not need regeneration.
Their three current IoStore compression blocks all reside in Steam content
chunk SHA-1 `f6228dcda191ae381a1d29d20586bb50136039e9`, which is reused byte-for-byte
from the immediately preceding depot manifest `8387924540028786821` (Build ID
`24176442`). The update changed and extended the surrounding IoStore archives,
but not the compressed mesh data that produced either profile.

This is static compatibility evidence, not a multiplayer release claim. The
runtime still has to prove the reflected UFunction layouts, vtable slots,
live component/manager objects, queue offsets, and runtime-triangle layout.
Complete the host-initiated and joining-client-initiated checks in the Release
Boundary section before describing update2.8.0 multiplayer behavior as tested.

## Production Route

Normal multiplayer paint uses a paired packed route:

- `RuntimePaintableComponent.ServerPackedPaintBatch` sends the server batch.
- The painter's local game-owned packed receiver queue receives the same packed
  batch boundaries and cadence through the exact-build native receiver path.
- Each paired commit is fail-closed: resolver, payload, queue, and component
  continuity checks must pass before the first stroke is sent.
- No automatic fallback is allowed to reflected `PaintAtUVWithBrush`, the
  legacy per-stroke internal-common route, compact/adaptive routes, or a
  texture-sync route.
- Batch controls range from 1--20 strokes and 50--500 ms. The default is
  20 strokes / 50 ms.

The planner produces `Fill -> Brush 1 -> Brush 2`; no packed batch crosses a
pass boundary. Brush 1 ranges from 10--30 texels and defaults to 30. Brush 2
ranges from 5--10 texels. Coverage is intentionally synchronized to Brush 2:
setting Brush 2 to 5 also sets coverage to 5.

Projected source colors are sampled from the calibrated bulk capture with
pixel-centred, linear-light bilinear interpolation. Integer pixel reads remain
limited to bulk orientation/color calibration. This removes nearest-pixel
projection jitter without adding dark sRGB interpolation halos or changing UV
sample density, stroke totals, pass boundaries, or the packed wire contract.

Completion means that the initiating client's local game queue drained. It
does **not** prove that a joining client has presented its final pixels. The UI
therefore says that joined clients may still be rendering after a host-side
completion.

## Bounded Cancellation

Cancellation is designed to stop future work, not to rewrite game state:

- Before each paired server/local commit, the exact local component queue is
  read. Only enough strokes to keep `queued + nextBatch <= configuredBatchLimit`
  are submitted.
- Once a cancel is latched, no later server RPC or local enqueue starts.
- Already committed work, at most one configured batch ahead locally, drains
  naturally through the game queue before the terminal result is emitted.
- The implementation never calls `ClearRecordedStrokes`, rewrites queue memory,
  or attempts to purge remote queues.
- The terminal UI text is simply `Paint: canceled.`; pending acknowledgement is
  `Paint: cancel requested.`

On the host, a 400-stroke run at 20/50 with cancellation after three seconds
submitted 204 paired strokes, left 196 unsubmitted, never exceeded the 20-stroke
local queue cap, and terminalized only after two zero-queue observations. This
verifies host-local cancellation behavior. It does not retroactively stop work
already delivered to a joining client.

## Joining-Client Throughput

In a host plus Hyper-V joining-client comparison with variable colors and 400
planned strokes:

| Measurement | Observed result |
| --- | ---: |
| Host sender at 20/50 | about 272 strokes/s |
| Joining-client renderer drain | about 30--31.5 strokes/s |
| Host local renderer drain | about 54.7 strokes/s |

The sender stayed within its configured transport contract, but it outran the
joining client's game-owned renderer, so a remote backlog was expected. This
does not prove that Hyper-V alone caused the difference; it has not been
repeated on comparable physical joining hardware.

There is intentionally no profile that slows a host to the slowest receiver.
That policy cannot resolve the reciprocal case where a joining client initiates
paint, and it would require receiver-specific acknowledgements that the current
route does not provide.

In a later session, a fresh joining-client observer saw zero
`MulticastPackedPaintBatch` calls for 60 seconds. That session therefore did
not establish a valid multiplayer delivery path, and no remote cancel or PNG
claim was made from it. Confirm the game topology before repeating a
joining-client measurement.

## Brush 2 Seam Finding

The apparent oversized Brush 2 mark at the arm/torso seam is not Brush 1
leaking into the fine pass. A one-stroke Fine-pass experiment showed the
expected Brush 2 planner and packed-wire radius, and no packed batch crossed a
pass boundary.

The packed receiver instead applies a world-space sphere that can reach a
physically adjacent but separate UV island. In the measured case, the direct
UV reference produced one 314-texel blob; the packed host and joining receiver
both produced the same two blobs totaling 298 texels. Reducing the global
packed radius removed the cross-island mark only by reducing coverage to about
10% of the direct reference.

The current packed record has no UV-island clip field. A silent radius reduction,
stroke skip, or direct-UV fallback would trade the seam for underpaint and is
not a production fix. A real fix requires a validated UV-island-clipped packed
primitive, or an explicitly accepted coverage-erosion policy.

## Rejected Texture Multicast Candidate

`MulticastSyncChannelData` was tested only as a research candidate while the
host queue was empty. The host could execute a local 4 MiB Albedo loopback,
change one deterministic texel, and restore the original hash. A VM observer
received zero `MulticastSyncChannelData` calls during a 60-second discovery
window.

Therefore this call is **not validated as multiplayer transport** in this game.
The experiment was stopped before compression, other channels, queued-paint
mixing, or any fallback work. Its sender, runner option, and native command are
not part of the source or release path.

## Release Boundary

Before making a multiplayer release claim, collect fresh evidence for both
host-initiated and joining-client-initiated paint:

1. Verify the players are on the intended multiplayer topology.
2. Capture event-watch evidence for packed delivery and absence of legacy
   fallback routes.
3. Record sender counts, local queues, joining queues, and queue-zero times.
4. Export the selected texture before and after queue drain, and retain a
   changed-texel image rather than relying on checksum inequality alone.
5. Keep host-local terminal and remote visual completion as separate facts.

Do not claim EOS packet-level behavior, loss recovery, or remote GPU
presentation without measurements that specifically prove those properties.
