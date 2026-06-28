# Mesh Direct Bake Research

The ideal side/back coverage path is direct mesh/UV reasoning rather than view-only paint expansion. This requires reliable access to mesh geometry, UVs, and eventually current skinned pose.

## Current Status

Offline StaticMesh and SkeletalMesh extraction are now working at the research-tool level.

The patched dumper can generate a `.usmap` that CUE4Parse accepts. The latest full asset probe mounted the game IoStore archives with `GAME_UE5_6`, scanned `5511` packages, found `805` StaticMesh exports and `16` SkeletalMesh exports, and converted all found SkeletalMeshes.

The live paint target has now been matched to a converted offline SkeletalMesh:

- runtime source: `runtime_paint_get_initialized_paint_mesh`
- component: `BP_FirstPersonCharacter_cLeon_Character_C.Mesh`
- component class: `SkeletalMeshComponent`
- runtime asset: `/Game/3Dmodel/cLeon/charactor/paintman/skeltal/paintman.paintman`
- offline package: `Chameleon/Content/3Dmodel/cLeon/charactor/paintman/skeltal/paintman.uasset`
- LOD0: `1660` vertices, `8352` indices, `2784` triangles, `28` bones, 1 UV channel, 1 material slot.
- UV0 range: approximately `U 0.000938..0.999023`, `V 0.041077..0.999023`.

Other strong player/body candidates are:

- `Chameleon/Content/3Dmodel/link/newpenguin/SANTA_ver/santapengun.uasset`: `4728` LOD0 vertices, `23712` indices, `40` bones, 1 UV channel, 2 material slots.
- `Chameleon/Content/3Dmodel/link/newpenguin/SK_LINK_Penguin.uasset`: `3468` LOD0 vertices, `18624` indices, `40` bones, 1 UV channel, 2 material slots.
- `Chameleon/Content/3Dmodel/skeltal/penngin_big/pengun.uasset`: `4042` LOD0 vertices, `17856` indices, `34` bones, 1 UV channel, 4 material slots.

`tools/asset_probe` can now export top SkeletalMesh LOD0 geometry JSON under `.build\research\mesh_exports\`. The JSON includes positions, normals, UVs, indices, bone influences, reference bones, skeleton asset, physics asset, bounds, and material slot names. These files are generated research outputs and should not be committed.

The next blocker is not asset identity anymore. It is deciding how much of v1 direct bake can be approximated from bind-pose geometry versus how much needs current skinned pose.

`tools/mesh_planner` now provides the first offline approximation. It reads the exported `paintman` LOD0 JSON plus the latest runtime camera direction, classifies triangles as front/side/back using bind-pose normals, and emits side/back UV target samples.

For color transfer, run `MECCHA_RESEARCH_ARTIFACTS=1 make run` and perform one normal paint. The bridge writes a generated front-sample sidecar containing front UV/color samples; the planner can then assign nearest front color to each side/back target sample. This keeps the next validation offline and avoids sending new side/back strokes until the generated plan is reviewed.

Latest observed colorized plan:

- front capture samples: `20029`
- side/back targets: `3105`
- target split: `2383` side, `722` back
- nearest-source UV distance: p50 `0.017271`, p90 `0.155525`, p95 `0.197603`, p99 `0.280566`, max `0.317105`

This confirms the pipeline works, but it also shows raw nearest-UV color transfer is not good enough as a replay input. Some targets borrow color from far-away UV locations, which can bleed across UV islands or unrelated body regions.

## Sampling Workflow Direction

The current front-only paint path should remain the authoritative visible-color sampling path. Direct bake should be layered on top of it:

1. Normal runtime pass gathers visible UV/color samples through hit test plus scene capture.
2. Optional research artifact writes the front samples to disk.
3. Offline mesh planner uses confirmed `paintman` geometry and latest camera direction to find candidate side/back UV targets.
4. Planner transfers color from front samples to side/back targets.
5. Planner filters candidates by source distance and UV region/island constraints.
6. Only filtered candidates become eligible for a future conservative replay path.

This is different from trying to make runtime sampling denser or orbiting the camera. The useful split is: runtime samples visible color, offline mesh reasoning expands coverage candidates.

## Intended Data Flow

1. Generate `Mappings.usmap` with the patched dumper.
2. Use CUE4Parse to load candidate `USkeletalMesh` / `UStaticMesh` packages.
3. Locate player/body/cosmetic mesh candidates and verify `USkeletalMesh` conversion.
4. Confirm LOD vertices, indices, UVs, material slots, skeleton reference data, and bind-pose data.
5. Compare offline mesh identity with runtime target actors/components using `front_mesh_candidates`.
6. Prototype UV-space side/back planning against the exported `paintman` LOD0 JSON.
7. Add front sample color transfer from generated research artifacts.
8. Add source-distance and UV-region filters.
9. Only after the filtered offline planner looks sane should runtime pose/skinning or server replay be investigated.

## What This Does Not Solve Yet

- Current animation pose / skinned vertices.
- Occlusion-aware side/back fill.
- UV island/region aware color transfer.
- Filtered replay candidate generation.
- Automatic server-safe paint expansion.

Those are downstream steps and should not be implemented until mapping and mesh extraction are stable.
