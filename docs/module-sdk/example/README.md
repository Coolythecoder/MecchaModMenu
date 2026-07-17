# Example module

Copy this `example` directory to:

`%LOCALAPPDATA%\MecchaCamouflage\modules\example`

The catalog reads `module.json`, validates the package, and exposes a descriptor
to the host. Discovery alone does not execute the HTML or grant native access.
The example requests a sanitized snapshot and demonstrates the host-managed
persistent and session JSON stores. It declares only the permissions it uses:

- `snapshot.read`
- `storage.read` and `storage.write`
- `memory.read` and `memory.write`

The package directory must match `id`, and `entry` must be a relative `.html`
path contained entirely within the package without symbolic links or reparse
points. The example's Promise `sdk` object is an ordinary wrapper around the
documented `postMessage` protocol; the host still verifies the frame, current
generation, module ID, and permission for every call.

See the parent [Module SDK v1 guide](../README.md) for the complete message
protocol, full permission table, quotas, and trust model.
