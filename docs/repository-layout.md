# Repository Layout

This repository separates source code, shipped app resources, documentation
assets, generated build output, and research output.

## Source Code

Source code lives under `src/`.

```text
src/
  csharp/
    MecchaCamouflage.Core/
    MecchaCamouflage.Controller/
    MecchaCamouflage.WebHost/
    MecchaCamouflage.Tests/
  native/
    bridge/
      bridge.cpp
    injector/
      injector.cpp
    include/
      direct_bridge_abi.hpp
      sdk.hpp
```

`src/csharp/` contains the supported .NET projects. The supported controller UI
is `MecchaCamouflage.WebHost`. The third-party Paint Studio module manifest,
catalog, permission boundary, and example package are documented in
[`docs/module-sdk/`](module-sdk/README.md).

`src/native/` contains native code used by the injected bridge and injector.
The bridge source is intentionally split conservatively because it depends on
runtime UE reflection, RPC layout, and multiplayer paint behavior.

## Shipped Resources

Resources that are packaged into the app live under `resources/`.

```text
resources/
  app-icons/
  mesh-profiles/
```

`resources/mesh-profiles/` contains reviewed mesh profile JSON files that ship
with the app. `make mesh` writes regenerated profiles there after game updates.

`resources/app-icons/` contains app icon files used by the WebHost app and
packaged WebView2 assets.

## Documentation Assets

Images used by README, GitHub metadata, release notes, or docs live under
`docs/assets/`.

These files are not runtime app resources.

## Build Output

Generated build output lives under `.build/`.

```text
.build/
  bin/
  obj/
  cache/
  dotnet/
  tools/
  tmp/
  package/
```

Expected contents:

- `.build/bin/`: runnable development EXE from `make build`
- `.build/obj/`: native object files and native package inputs
- `.build/cache/`: downloaded build caches such as the WebView2 Evergreen bootstrapper
- `.build/dotnet/`: .NET `bin` and `obj` output redirected from source projects
- `.build/tools/`: generated helper tools
- `.build/tmp/`: scratch directories
- `.build/package/`: release-ready single EXE artifacts

It should be safe to delete `.build/` at any time.

`make clean` deletes `.build/` only. It intentionally does not delete
`artifacts/`, because review and research outputs are often useful evidence
while investigating runtime or multiplayer behavior.

Use `make clean-artifacts` when review/research outputs should be discarded.
Use `make clean-all` when both `.build/` and `artifacts/` should be discarded.

## Review and Research Output

Human-inspected generated reports live under `artifacts/`.

```text
artifacts/
  review/
  research/
```

Use `artifacts/review/` for static review reports such as dead-code inventory.
Use `artifacts/research/` for repeatable reverse-engineering and multiplayer
paint investigation output.

## Third-Party Tools

Third-party source checkouts and submodules live under `third_party/`.

Generated outputs from helper tools should go to `.build/tools/` where possible.

`tools/asset_probe/` is currently ignored local output from an older research
tool. Only local `bin/` and `obj/` output remains. Keep it only when a local
investigation still needs it. Do not treat it as maintained source unless the
tool source is restored.

## Local App Runtime State

The installed/running app writes runtime state under LocalAppData, not inside
the repository.

Important paths:

```text
%LOCALAPPDATA%\MecchaCamouflage\config\config.json
%LOCALAPPDATA%\MecchaCamouflage\versions\<version>\
%LOCALAPPDATA%\MecchaCamouflage\bridge-instances\
%LOCALAPPDATA%\MecchaCamouflage\bridge-state\
%LOCALAPPDATA%\MecchaCamouflage\modules\
%LOCALAPPDATA%\MecchaCamouflage\module-data\
```

User settings live in the stable `config/config.json` file so layout migrations
and user choices carry across releases. On first use, the app imports the newest
valid legacy `versions/<version>/config/config.json` (or the original
`versions/<version>/config.json`) and writes its normalized current-layout
settings to the stable path.

Versioned app logs, diagnostics, and extracted package assets live under
`versions/<version>/`. They are never copied during settings migration.
WebView2 user data lives in the stable shared folder
`%LOCALAPPDATA%\MecchaCamouflage\webview2-user-data\` so it is not tied to a
single-file extraction directory or app version.

Bridge live state, such as progress snapshots tied to an injected bridge in the
game process, lives under `bridge-state/`.

User-installed Paint Studio modules live under `modules/<module-id>/`. Each
package is discovered from its `module.json` manifest and is served to a
sandboxed WebView frame; it is not loaded as native code.

Host-managed persistent SDK data lives as hashed per-key `.entry` JSON records
inside hashed module directories under `module-data/`. Its internal names are
not a public filesystem API. It is separate from editable module packages,
per-run secured snapshots, and volatile `sdk.memory` data. Volatile module
memory exists only in the running app process and is never written here.

Each direct bridge injection stages a uniquely named DLL and its profiles under
`bridge-instances/<instance-guid>/`. Older bridge instances may remain loaded
in a game process; the current host only connects to the instance it started.

Repository `src/` and app LocalAppData runtime state are intentionally separate
concepts.

## Do Not Commit

Do not commit:

- `.build/`
- `artifacts/`
- source-tree `bin/` or `obj/`
- root-level generated DLLs or EXEs
- game archives or cooked assets
- generated Unreal mappings
- research dumps, screenshots, traces, or injected runtime outputs
