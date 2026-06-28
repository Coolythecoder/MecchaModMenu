# Research Toolchain

This repository keeps runtime code and reverse-engineering research code separate.

## Layout

- `third_party/CUE4Parse`: pinned submodule for offline pak/utoc/usmap/package inspection. Current pin: `c43d6707cbde0305e2970d7c527e2ccd26f393c7`.
- `third_party/UnrealMappingsDumper`: patched source snapshot for runtime `.usmap` experiments.
- `tools/asset_probe`: .NET probe that verifies whether CUE4Parse can mount archives and deserialize mesh packages.
- `tools/mesh_planner`: offline UV planner prototype that reads exported LOD0 mesh JSON, classifies front/side/back target UV samples, and optionally colorizes them from front-capture samples.
- `scripts/research`: explicit scripts for research builds and injection. Normal `make build` and `make package` do not call these.

Future agent turns should start with [`agent-state.md`](agent-state.md) before changing the research tooling.

## Bootstrap

```powershell
git submodule update --init --recursive
.\scripts\research\build-asset-probe.ps1
.\scripts\research\build-mesh-planner.ps1
.\scripts\research\build-mapping-dumper.ps1
```

## Asset Probe

Run without a mapping file to confirm the expected mapping failure:

```powershell
.\scripts\research\run-asset-probe.ps1 -AllowFailure
```

Run with a generated mapping:

```powershell
.\scripts\research\run-asset-probe.ps1 -UsmapPath .build\research\mappings\Mappings.usmap -GameVersion GAME_UE5_6 -PackageLimit 1000 -AllowFailure
```

The probe writes `.build\research\profiles\asset-probe-latest.json`. A useful mapping should get past package load, report nonzero mapping/read properties, and convert sampled meshes with nonzero LOD vertices and indices.

For direct-bake research, export top SkeletalMesh LOD0 geometry:

```powershell
.\scripts\research\run-asset-probe.ps1 -UsmapPath .build\research\mappings\Mappings.usmap -GameVersion GAME_UE5_6 -PackageLimit 13000 -ExportTopSkeletal 10 -AllowFailure
```

This writes generated JSON files to `.build\research\mesh_exports\`. These files include LOD0 positions, normals, UVs, indices, bone influences, reference bones, material slots, skeleton asset, physics asset, and bounds. They are generated research outputs and should not be committed.

## Mesh Planner

After exporting `paintman` LOD0 geometry and running one paint with the current bridge, generate an offline UV plan:

```powershell
.\scripts\research\run-mesh-planner.ps1
```

By default this reads:

- `.build\research\mesh_exports\paintman-Chameleon_Content_3Dmodel_cLeon_charactor_paintman_skeltal_paintman.uasset.lod0.json`
- `%LOCALAPPDATA%\MecchaCamouflage\runtime\last_status.json`

It writes `.build\research\uv_plans\paintman-uv-plan-latest.json`. The planner uses the latest runtime camera direction, bind-pose triangle normals, and UV triangles to classify front/side/back surfaces and emit side/back target UV samples. This is a research artifact only; it does not call the bridge or send paint to a server.

To attach nearest front-capture color to the side/back UV samples, run the app with research artifacts enabled and perform one normal hotkey paint:

```bash
MECCHA_RESEARCH_ARTIFACTS=1 make run
```

When running `scripts\dev.ps1` directly from PowerShell, pass `-EnableResearchArtifacts` instead.

The bridge then writes a generated `*.front_samples.json` sidecar under `%LOCALAPPDATA%\MecchaCamouflage\runtime\native\`. `run-mesh-planner.ps1` automatically picks the newest one and adds nearest front-sample color fields to each target sample. Without that file, the planner still emits UV-only targets.

Useful planner checks:

```powershell
.\scripts\research\run-mesh-planner.ps1
```

```bash
jq '{FrontSampleCount, TriangleStats, sample_count:(.TargetSamples|length), first:.TargetSamples[0]}' .build/research/uv_plans/paintman-uv-plan-latest.json
jq -r '.TargetSamples[] | select(.SourceDistanceUv != null) | .SourceDistanceUv' .build/research/uv_plans/paintman-uv-plan-latest.json | sort -n
```

Interpretation:

- `FrontSampleCount == 0`: UV-only plan. No color replay should be considered.
- `FrontSampleCount > 0`: colorized plan. Still offline-only.
- Large `SourceDistanceUv` values indicate unsafe color transfer. Current raw nearest-neighbor transfer has p95 around `0.20`, so filtering is required before replay.

Generated files under `.build\research\mesh_exports\`, `.build\research\uv_plans\`, and `%LOCALAPPDATA%\MecchaCamouflage\runtime\native\*.front_samples.json` are not source artifacts and should not be committed.

## Mapping Dumper

Only inject a locally built DLL. The upstream prebuilt DLL crashed in MECCHA CHAMELEON and must not be reused.

```powershell
.\scripts\research\build-mapping-dumper.ps1
.\scripts\research\inject-mapping-dumper.ps1 -Mode Probe
```

If probe mode is stable, use object scan before a full dump:

```powershell
.\scripts\research\inject-mapping-dumper.ps1 -Mode ObjectScan
```

Object scan validates guarded `GObjects` traversal without writing `.usmap`. Use it after game updates or dumper changes before full dump.

If probe and object scan are stable, dump mode can be tried once:

```powershell
.\scripts\research\inject-mapping-dumper.ps1 -Mode Dump
```

The injection script refuses to inject the same DLL hash twice unless `-Force` is passed intentionally.

Current MECCHA CHAMELEON UE 5.6 dump status:

- Local patched dumper generates `.build\research\mappings\Mappings.usmap` without crashing the game.
- Observed dump wrote `10807` structs and `37740` serializable properties.
- The generated mapping lets CUE4Parse convert sampled StaticMeshes and all currently found SkeletalMeshes to LOD vertex/index data.
- Latest full asset probe found `805` StaticMesh exports and `16` SkeletalMesh exports, and exported `10` top SkeletalMesh LOD0 geometry files under `.build\research\mesh_exports\`.
