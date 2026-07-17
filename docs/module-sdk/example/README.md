# Example module

Copy this `example` directory to:

`%LOCALAPPDATA%\MecchaCamouflage\modules\example`

The catalog reads `module.json`, validates the package, and exposes a descriptor to the host. Discovery alone does not execute the HTML or grant native access. The example requests a sanitized snapshot through the iframe API. API v1 permissions are limited to:

- `snapshot.read`
- `paint.start`
- `paint.preview`
- `paint.restore`
- `paint.stop`

The package directory must match `id`, and `entry` must be a relative `.html` path contained entirely within the package without symbolic links or reparse points.

See the parent [Module SDK v1 guide](../README.md) for the complete message
protocol and trust model.
