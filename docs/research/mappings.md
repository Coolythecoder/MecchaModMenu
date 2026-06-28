# Mapping Research

MECCHA CHAMELEON packages currently require `.usmap` type mappings before CUE4Parse can deserialize UE packages with unversioned properties.

## Current Findings

- CUE4Parse can mount the game archives and read IoStore indexes.
- AES keys were not required in the observed build.
- Without `global.utoc`, package load fails because IoStore global data is missing.
- With `global.utoc`, package load reaches the expected mapping failure: `Package has unversioned properties but mapping file is missing`.
- The upstream `UnrealMappingsDumper.dll` release crashed in-game with an access violation and did not produce `Mappings.usmap`.
- The patched local dumper now runs in `Probe` mode without crashing the game. It detected UE `5.6`, accepted `GObjects`, accepted `FNameToString`, and found standard dynamic offsets: `Class=0x10`, `Outer=0x20`, `Super=0x40`, `ChildProperties=0x50`.
- `ObjectScan` mode can walk `GObjects` without crashing. In the observed run it found readable `Class`, `ScriptStruct`, and `Enum` type objects, no object/class read faults, and one unreadable object entry near the end of the array.
- Patched `Dump` mode now generates `.build\research\mappings\Mappings.usmap` without crashing the game. The current observed dump wrote `10807` structs and `37740` serializable properties with no nested-property fallback.
- The UE 5.6 field/property layout used by the patched dumper is currently:
  - `UObject`: `Class=0x10`, `Name=0x18`, `Outer=0x20`
  - `UStruct`: `Super=0x40`, `ChildProperties=0x50`
  - `FField`: `ClassPrivate=0x8`, `Next=0x18`, `Name=0x20`, `Flags=0x28`
  - `FProperty`: `ArrayDim=0x30`, base subclass data starts around `0x70`
  - `FStructProperty.Struct=0x70`, `FByteProperty.Enum=0x70`
  - `FArrayProperty.Inner=0x78`, `FSetProperty.Element=0x70`
  - `FMapProperty.Key=0x70`, `FMapProperty.Value=0x78`
  - `FEnumProperty.Underlying=0x70`, `FEnumProperty.Enum=0x78`
- CUE4Parse can parse the generated `.usmap` and, with `GAME_UE5_6`, mount packages, deserialize StaticMesh and SkeletalMesh packages, and convert mesh render data.
- Latest asset probe profile: exe SHA-256 `c7547bcd42a6b72e26c5412ecfd0a52008772bde8e42eb6db8cadc6106d38e21`, usmap SHA-256 `91269446c657ce359deb6eedd93e53d9dfa4c20d067359a473fee3e0dc82011e`.
- Latest full asset probe result scanned `5511` packages, found `805` StaticMesh exports and `16` SkeletalMesh exports, and converted all found SkeletalMeshes.
- Runtime paint target identity is confirmed from `front_mesh_candidates`: `BP_FirstPersonCharacter_cLeon_Character_C.Mesh` references `/Game/3Dmodel/cLeon/charactor/paintman/skeltal/paintman.paintman`.
- The matching offline package is `Chameleon/Content/3Dmodel/cLeon/charactor/paintman/skeltal/paintman.uasset`. Its LOD0 export has `1660` vertices, `8352` indices, one UV channel, and `28` bones.
- Other SkeletalMesh candidates include `santapengun`, `SK_LINK_Penguin`, and `pengun`. `tools/asset_probe` can export their LOD0 geometry JSON with vertices, indices, UVs, bone influences, and reference bones.
- Mapping/mesh extraction are no longer the active blocker for side/back research. The current blocker is filtering the colorized offline UV transfer plan before any server replay.

## Rules

- Do not inject the upstream prebuilt DLL again.
- Do not commit `.usmap`, game archives, crash dumps, logs, or generated DLLs.
- First run every new dumper build in `Probe` mode.
- Stop after any crash or SEH-blocked dump and inspect the generated log before changing code or retrying.

## Expected Flow

1. Build the patched dumper source.
2. Inject in `Probe` mode and inspect `.build\research\logs\mapping-dumper-*.log`.
3. Confirm `GObjects`, `FNameToString`, object count, and dynamic offsets look sane.
4. If dump behavior changed or the game updated, run `ObjectScan` before full dump.
5. Inject in `Dump` mode only after probe/object scan returns without crashing.
6. Pass the generated `.build\research\mappings\Mappings.usmap` to `tools/asset_probe`.

Current status: usmap generation, offline StaticMesh conversion, offline SkeletalMesh conversion, candidate LOD0 geometry export, runtime actor-to-asset identity, and colorized offline UV plan generation are unblocked. The next research target is filtering the `paintman` side/back UV transfer plan by source distance and UV region before any server replay.

## Game Update Checklist

- Record the shipping exe SHA-256 from the asset probe profile.
- Re-run mapping probe after every game update.
- Treat changed signatures, changed object counts, missing mappings, or package load failures as blockers.
