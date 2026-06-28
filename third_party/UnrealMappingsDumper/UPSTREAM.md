# UnrealMappingsDumper Upstream

- Repository: https://github.com/TheNaeem/UnrealMappingsDumper
- Pinned commit: `4da8c66c23ce66ef86d75962d66b12cf39185092`
- License: MIT, see `LICENSE`

## Local Policy

- Do not use the upstream prebuilt DLL.
- Keep this copy as source so crash guards, logging, and MECCHA-specific resolver work can be reviewed.
- Build outputs, generated `.usmap` files, logs, and injected DLLs are research artifacts and must not be committed.

## Local Changes

- Default mode is `probe`; `dump` must be explicitly set in `UnrealMappingsDumper.config`.
- Logging writes to a sidecar log file with shared read access and closes the handle after each line.
- Top-level SEH guard records access violations instead of silently crashing without context.
- Signature and dynamic-offset results are logged before any `.usmap` generation.
- Dump mode logs phase/progress markers around type collection, name writing, enum writing, struct/property writing, compression, and output writing.
- `objectscan` mode walks `GObjects` with guarded object/class pointer reads and never writes `.usmap`.
- Dump mode avoids full-path `StaticClass()` lookup, skips unreadable object/property pointers, guards `FNameToString`, and writes usmap `LargeEnums` format.
- UE 5.6 `FField` / `FProperty` offsets are patched for MECCHA CHAMELEON, including separate Array/Set/Map subclass offsets.
- Nested property payload reads are guarded so an unexpected game update logs the bad pointer instead of crashing the process.
