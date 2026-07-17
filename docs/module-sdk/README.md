# Module SDK v1

Meccha Mod Menu can load trusted local Web modules from:

```text
%LOCALAPPDATA%\MecchaCamouflage\modules\<module-id>\
```

Each package contains a validated `module.json` and an HTML entry point. Copy
the [`example`](example/) directory into the modules directory, then choose
**Reload modules** in the app.

## Manifest

```json
{
  "schema_version": 1,
  "api_version": 1,
  "id": "example",
  "name": "Example Module",
  "version": "1.0.0",
  "description": "A small SDK example.",
  "entry": "index.html",
  "permissions": ["snapshot.read"]
}
```

The package directory must match `id`. IDs, entry paths, file sizes, duplicate
IDs, symbolic links, reparse points, and every permission are validated before
a module is shown. A bad package produces a nonfatal diagnostic and does not
prevent other modules from loading.

API v1 permissions are:

| Permission | Module request |
| --- | --- |
| `snapshot.read` | `snapshot.get` and sanitized snapshot events |
| `paint.start` | `paint.start` |
| `paint.preview` | `paint.preview` |
| `paint.restore` | `paint.restore` |
| `paint.stop` | `paint.stop` |

There is deliberately no generic native-command, filesystem, process, or
network-replication permission.

## Browser protocol

Modules run in a sandboxed, cross-origin iframe. Send requests to the parent:

```js
const requestId = crypto.randomUUID();
window.parent.postMessage({
  source: "meccha-module",
  apiVersion: 1,
  requestId,
  command: "snapshot.get",
  payload: {}
}, "https://meccha.localhost");
```

The host answers the registered iframe only:

```js
{
  source: "meccha-host",
  apiVersion: 1,
  type: "response",
  requestId: "...",
  ok: true,
  data: {}
}
```

Modules with `snapshot.read` also receive:

```js
{
  source: "meccha-host",
  apiVersion: 1,
  type: "event",
  name: "snapshotChanged",
  data: {}
}
```

The snapshot is intentionally reduced to paint settings, Paint Studio results,
and non-sensitive runtime status. Logs, diagnostics paths, app settings, bridge
identity, and native internals are not included.

Listen only for host messages and validate their origin:

```js
window.addEventListener("message", event => {
  if (event.origin !== "https://meccha.localhost") return;
  if (event.data?.source !== "meccha-host" || event.data?.apiVersion !== 1) return;
  // Handle response or snapshotChanged here.
});
```

## Trust model

Module HTML and JavaScript are local user-installed code. Install only packages
you trust. Each accepted package receives a separate virtual origin. The host
blocks HTTP, HTTPS, and file requests outside the packaged app and accepted
module origins. Module documents also receive a host-enforced content security
policy: local scripts, styles, images, fonts, and media are supported, while
connection APIs (including WebSockets), workers, forms, and plug-ins are
disabled. The host denies browser permission requests, prevents modules from
framing the privileged main interface, rejects direct WebView messages from a
module origin, and checks the declared permission before relaying an API
request.

Module directories remain live local files and may be changed by the signed-in
Windows user after validation. Choose **Reload modules** after modifying a
package. The permission list and browser isolation reduce accidental privilege;
they do not make code from an untrusted package safe to install.
