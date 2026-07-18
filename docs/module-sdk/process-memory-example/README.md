# Process Memory Self-Test

This module demonstrates `sdk.processMemory` without reading or modifying an
existing game address. Its self-test runs only when the user clicks **Run safe
self-test**.

The test:

1. injects a deterministic sentinel into a new `read-write`, module-owned
   allocation;
2. reads the sentinel back and compares every byte;
3. changes the allocation to `read-only`;
4. restores `read-write`; and
5. frees the allocation.

It renders `PASS` only after all five operations succeed. On failure it renders
`FAIL` and still attempts to free any allocation it created.

## Install and run

1. Copy this whole directory to:

   ```text
   %LOCALAPPDATA%\MecchaCamouflage\modules\process-memory-example\
   ```

2. Start MECCHA CHAMELEON and wait for Meccha Mod Menu to show the game and
   bridge as attached and connected.
3. In **App → External modules**, choose **Reload modules**.
4. Open **Process Memory Self-Test** and click **Run safe self-test**.

The manifest requests both `process.memory.read` and `process.memory.write`.
Write permission covers allocate, write, protect, inject, and free; read
permission is separate.

Addresses in the protocol are `0x`-prefixed hexadecimal strings, and bytes are
strict hexadecimal on the wire. The wrapper in `index.html` exposes bytes as
`Uint8Array` values.

Allocate and inject use one of the six private-allocation modes: `no-access`,
`read-only`, `read-write`, `execute`, `execute-read`, or `execute-read-write`.
Only protect can also request `write-copy` or `execute-write-copy`; the host
rejects those two modes for allocate and inject instead of silently changing
them.

This example injects data bytes only. It does not create a thread, call an
address, execute the sentinel, inspect unrelated game memory, or free memory
owned by the game or another module. Raw process-memory permissions are still
native-trusted capabilities; review any module that requests them.
