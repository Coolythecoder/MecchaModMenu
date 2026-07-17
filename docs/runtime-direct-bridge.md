# Direct Bridge Injection (v1.6)

This is the authoritative description of the v1.6 injection and bootstrap
path. The production system has one supported native path: the controller
directly injects one uniquely named bridge DLL into the exact game process it
selected, starts that bridge, and authenticates the resulting loopback
endpoint.

## Scope and invariants

- The shipped native components are the bridge and the injector only.
- There is no loader, bridge switching, conditional unload, named-pipe loader
  lifecycle, loader cache, or restart-required state caused by an existing
  module.
- Existing bridge DLLs in a game process are ignored. The new instance is
  identified by its GUID, token, build hash, and endpoint. It is not selected
  by DLL age or by a module-name scan.
- A bridge is never unloaded by the production path. An injection timeout is
  indeterminate: remote memory remains owned until the remote operation has
  finished, and the user must explicitly retry.
- The paint command protocol after bootstrap is unchanged.

The relevant implementation is:

- `src/native/bridge/bridge.cpp`: injected bridge and `BridgeStartV1`.
- `src/native/injector/injector.cpp`: one direct injection attempt.
- `src/native/include/direct_bridge_abi.hpp`: fixed startup ABI.
- `src/csharp/MecchaCamouflage.Controller/RuntimeBridgeService.cs`: staging,
  identity, synchronization, and connection ownership.
- `src/csharp/MecchaCamouflage.Controller/BridgeBootstrap.cs`: startup-block
  and endpoint identity models.
- `src/csharp/MecchaCamouflage.Controller/BridgeClient.cs`: HELLO and command
  sequencing.

## Per-instance staging

For every attempt the controller creates a new directory under:

```text
%LOCALAPPDATA%\MecchaCamouflage\bridge-instances\<instance-guid>\
```

It copies the packaged bridge, injector, and mesh profiles into that directory.
The bridge file name is generated from the build hash and instance GUID:

```text
meccha-direct-bridge-v1-<sha256>-<guid>.dll
```

The staged name is intentionally outside old `runtime-bridge-*` naming
patterns. The controller stores the endpoint, instance GUID, token, and
expected hash in one `BridgeInstance` record. The token is never logged.
Live instance directories are not deleted while a game may still have the DLL
loaded or may still read its profiles.

## Exact target identity

The caller selects a concrete `Process`. The controller captures:

1. PID;
2. process creation `FILETIME`; and
3. normalized executable path.

The injector receives those exact values and the exact staged bridge path. It
owns one target-process handle for the attempt and verifies the identity again
before writing remote memory. It never searches for a process by executable
name and never chooses between same-name processes.

## Injection sequence

1. Acquire the per-PID direct-injection mutex.
2. Stage the unique bridge, injector, and mesh profiles.
3. Start the injector with `--direct <pid> <creation-filetime> <exe-path>
   <bridge-path>`.
4. The injector verifies target identity and architecture, allocates remote
   memory for the bridge path and startup block, and calls `LoadLibraryW`.
5. After `LoadLibraryW` completes, it locates the newly loaded module by the
   exact normalized full path. This is the only bridge-module enumeration.
6. It resolves the exported `BridgeStartV1` RVA from the local inspection map,
   calls it in the target, and waits for both remote operations to finish.
7. It reports the fixed startup result as JSON to the controller.

Remote path and startup-block memory is not released until its corresponding
remote thread has exited. The production path contains no `TerminateThread`,
target `FreeLibrary`, bridge switch, or loader fallback. A canceled or timed
out wait is reported as indeterminate and does not attempt unsafe cleanup.

## `BridgeStartBlockV1` ABI

The startup block is exactly 128 bytes, pointer-free, little-endian, and shared
between C# and native code. Native `static_assert`s and C# serialization tests
must fail if the layout changes.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic (`MCS1`) |
| 4 | 4 | structure size (`128`) |
| 8 | 4 | ABI version (`1`) |
| 12 | 4 | expected PID |
| 16 | 16 | instance GUID |
| 32 | 32 | random connection token |
| 64 | 32 | expected bridge SHA-256 |
| 96 | 4 | requested port (`0`; OS chooses) |
| 100 | 4 | result state |
| 104 | 4 | bound port |
| 108 | 4 | bootstrap protocol version |
| 112 | 4 | Win32 error |
| 116 | 4 | Winsock error |
| 120 | 8 | reserved; must be zero |

`BridgeStartV1(void*)` validates the block, copies it into bridge-owned state,
binds `127.0.0.1:0`, obtains the assigned port, starts the listener, writes
the result state and errors, and returns only after the listener is accepting
connections. `DllMain` only records module state and calls
`DisableThreadLibraryCalls`; runtime startup is not performed from `DllMain`.

## TCP bootstrap and commands

Each command uses one short-lived TCP connection. The first line is always a
`hello` containing:

- bootstrap protocol version;
- instance GUID; and
- the random token.

The bridge validates all three and replies with its PID, instance GUID, build
hash, and protocol version. Only after a successful reply does the client send
the existing command line on that same connection. `ping`, paint, preview,
cancel, and shutdown payloads are unchanged after HELLO.

Auto Paint and Paint Studio send `detail_resolution_percent` as bounded paint
tuning. Native code validates it at 50–500 with a default of 500. The 100%
scaling baseline remains a channel threshold of 16, a half-brush radius, and a
512-stroke correction budget; the 500% default uses threshold 4, one-fifth of
that baseline radius, and up to 2,560 correction strokes. The 100,000-stroke
total-plan safety cap remains fixed. Third-party Web
modules never connect to this TCP bridge directly; their SDK permissions are
relayed through the controller's existing allowlisted paint commands.

The client accepts a reply only when PID, GUID, token-associated endpoint, and
expected build hash match its `BridgeInstance`. It does not read `.port`
sidecars or probe another bridge for compatibility. The `.progress.path` and
`.progress.json` sidecars remain solely for paint progress; research event
watch artifacts remain research-only.

Shutdown closes command admission and the listener before cancellation. An
already accepted handler must recheck admission before dispatch, and paint is
tracked from queue removal through planning, async execution, and terminal
completion. The bridge restores its hooks only after that pipeline is
quiescent; the DLL remains resident and may start a later authenticated
instance after listener/callback rundown. A timed-out shutdown does not report
success or authorize an automatic retry while cancellation is still pending.

## Failure handling

Startup failures identify the stage (staging, target identity, remote load,
module path match, bridge start, listener, HELLO, or command). A bridge that
has not completed a valid HELLO accepts no application command and performs no
paint action. Existing modules do not change this state and do not require a
game restart.

## Verification gates

Before v1.6 release, run the direct-injection tests and Windows 10/11 smoke
matrix from [`release-checklist.md`](release-checklist.md), including 25
sequential injections into one game process, same-name processes, target exit
during each stage, concurrent hosts, old direct bridges, and resident modules.
Every successful connection must match the selected PID, generated GUID,
token, and build hash. Run the existing paint, preview, cancellation, and
multiplayer checks unchanged.
