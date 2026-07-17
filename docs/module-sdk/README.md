# Module SDK v1

Meccha Mod Menu can load trusted local Web modules from:

```text
%LOCALAPPDATA%\MecchaCamouflage\modules\<module-id>\
```

Each package contains a validated `module.json` and an HTML entry point. Copy
the [`example`](example/) directory into the modules directory, then choose
**Reload modules** in the app.

The HTML entry must be UTF-8 and contain an explicit `<head>` before any
executable markup. The host serves an isolated runtime snapshot and injects
UTF-8, no-referrer, and permission-derived security metadata at the start of
that head before the document runs.

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
  "permissions": [
    "snapshot.read",
    "storage.read",
    "storage.write",
    "memory.read",
    "memory.write"
  ]
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
| `network.https` | `fetch`, `XMLHttpRequest`, and `EventSource` over HTTPS only |
| `network.http` | `fetch`, `XMLHttpRequest`, and `EventSource` over HTTP or HTTPS |
| `network.websocket` | `WebSocket` connections over `ws:` or `wss:` |
| `storage.read` | `storage.get` and `storage.list` for persistent module data |
| `storage.write` | `storage.set` and `storage.delete` for persistent module data |
| `memory.read` | `memory.get` and `memory.list` for session module data |
| `memory.write` | `memory.set` and `memory.delete` for session module data |

There is deliberately no generic native-command, filesystem, or process
permission. Network access is limited to the browser connection APIs covered by
the declared network permissions.

`navigator.sendBeacon()` and hyperlink ping requests are not exposed by any API
v1 permission.

## Package asset URLs

Use relative URLs for every packaged script, stylesheet, image, font, media
file, and other static asset. Paths must remain inside the package:

```html
<link rel="stylesheet" href="./styles.css">
<script src="./module.js"></script>
<img src="./assets/icon.png" alt="">
```

Do not use root-relative paths such as `/module.js`. The host places each
validated reload generation under a distinct entry-path prefix. Relative URLs
inherit that prefix, which ensures the frame loads assets from the current
runtime snapshot instead of reusing a cached URL from an earlier generation.

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
  if (event.source !== window.parent) return;
  if (event.origin !== "https://meccha.localhost") return;
  if (event.data?.source !== "meccha-host" || event.data?.apiVersion !== 1) return;
  // Handle response or snapshotChanged here.
});
```

## Promise SDK wrapper

The SDK uses the existing `postMessage` protocol; the host does not inject a
privileged JavaScript object. Modules can copy this small wrapper to expose
Promise-based `sdk.storage.*` and `sdk.memory.*` calls:

```js
const hostOrigin = "https://meccha.localhost";
const pending = new Map();

function sdkRequest(command, payload = {}) {
  return new Promise((resolve, reject) => {
    const requestId = crypto.randomUUID();
    const timeout = setTimeout(() => {
      pending.delete(requestId);
      reject(new Error(`SDK request timed out: ${command}`));
    }, 10_000);

    pending.set(requestId, { resolve, reject, timeout });
    window.parent.postMessage({
      source: "meccha-module",
      apiVersion: 1,
      requestId,
      command,
      payload
    }, hostOrigin);
  });
}

window.addEventListener("message", event => {
  if (event.source !== window.parent || event.origin !== hostOrigin) return;
  const message = event.data;
  if (message?.source !== "meccha-host" || message?.apiVersion !== 1 ||
      message?.type !== "response") return;

  const request = pending.get(message.requestId);
  if (!request) return;
  clearTimeout(request.timeout);
  pending.delete(message.requestId);
  if (message.ok) {
    request.resolve(message.data);
  } else {
    const error = new Error(message.error?.message || "SDK request failed");
    error.code = message.error?.code || "sdk_error";
    request.reject(error);
  }
});

const sdk = Object.freeze({
  storage: Object.freeze({
    get: key => sdkRequest("storage.get", { key }),
    set: (key, value) => sdkRequest("storage.set", { key, value }),
    delete: key => sdkRequest("storage.delete", { key }),
    list: () => sdkRequest("storage.list")
  }),
  memory: Object.freeze({
    get: key => sdkRequest("memory.get", { key }),
    set: (key, value) => sdkRequest("memory.set", { key, value }),
    delete: key => sdkRequest("memory.delete", { key }),
    list: () => sdkRequest("memory.list")
  })
});
```

The wrapper grants nothing by itself. The host binds every request to the exact
registered iframe, current reload generation, module ID, and declared
permission.

## Sandboxed module data

Both SDK data namespaces are host-managed, per-module JSON key/value stores:

| Namespace | Lifetime | Permissions | Quota |
| --- | --- | --- | --- |
| `sdk.storage` | Persists across module reloads, app restarts, and app versions | `storage.read`, `storage.write` | 4 MiB per module |
| `sdk.memory` | Current app process; survives module reload, clears on exit or crash | `memory.read`, `memory.write` | 4 MiB per module and 64 MiB across modules |

Read permission allows only `get` and `list`. Write permission allows only `set`
and `delete`; it does not imply read access. There is no clear operation. Values
must be JSON-compatible and are limited to 32 levels and 256 KiB when
serialized, with at most 8,192 total JSON nodes per value. Keys must match
`[a-z0-9][a-z0-9._-]{0,127}`, and each namespace may contain at most 256 keys
per module.

```js
await sdk.storage.set("preferences", {
  accent: "#5ac8fa",
  showCoverage: true
});

const result = await sdk.storage.get("preferences");
if (result.found) console.log(result.value);

const listing = await sdk.storage.list();
for (const key of listing.keys) console.log(key);

await sdk.storage.delete("preferences");
```

`list` returns sorted keys rather than values; call `get` for a value. `get`,
With the matching read permission, `set`, `delete`, and `list` responses include
the host's current usage and quota figures, which are authoritative. A
write-only module receives its submitted key, operation status, and quota but
not prior key existence or namespace usage; a write-only delete is idempotent.

Persistent data is written under
`%LOCALAPPDATA%\MecchaCamouflage\module-data\` as one hashed `.entry` JSON record
per key inside a hashed module directory. Those on-disk names and their layout
are internal and are not a filesystem API. Data is not encrypted and must not
be treated as a secret vault. Removing a module does not silently delete its
data, and a replacement package using the same ID inherits that namespace.
Browser `localStorage` and IndexedDB are separate WebView-profile features; the
four storage permissions control only these host-managed SDK namespaces.

`sdk.memory` has the same four calls and JSON rules, but never writes its values
to disk. The word “memory” does not mean raw game or process memory: these APIs
never accept addresses or pointers and never access the native bridge.

## Network APIs

Declare every type of connection the module needs in `module.json`. Use
`network.https` for HTTPS-only `fetch`, `XMLHttpRequest`, and `EventSource`
traffic. Use `network.http` instead when the module must be permitted to attempt
either HTTP or HTTPS. WebSockets require the separate `network.websocket`
permission for both `ws:` and `wss:` URLs.

```json
{
  "schema_version": 1,
  "api_version": 1,
  "id": "online-paint-tools",
  "name": "Online Paint Tools",
  "version": "1.0.0",
  "entry": "index.html",
  "permissions": ["network.https", "network.websocket"]
}
```

The permitted APIs use their standard browser interfaces; no host message is
needed:

```js
const response = await fetch("https://api.example.com/palettes", {
  headers: { "Accept": "application/json" }
});
if (!response.ok) throw new Error(`Request failed: ${response.status}`);
const palettes = await response.json();

const socket = new WebSocket("wss://api.example.com/updates");
socket.addEventListener("open", () => socket.send("subscribe"));
socket.addEventListener("message", event => console.log(event.data));
```

Each module has its own stable, isolated HTTPS origin. For cross-origin fetch,
XHR, and EventSource requests, the remote server must return suitable CORS
headers for the value of `location.origin` and must handle any preflight
request. Credentialed requests require an explicit allowed origin and the
corresponding credentials headers. WebSocket servers receive an `Origin` header
and must accept that module origin during the handshake.

All module frames use the same WebView profile. The modules have distinct,
unrelated `*.localhost` hostnames, so a host-only cookie for one module origin is
not a cookie for another module origin. Cookies and authentication state for a
remote target domain are profile state, however. A network-permitted module can
therefore include target-domain state that another module used, subject to the
browser's SameSite and third-party-cookie rules. Cross-origin `fetch` needs
`credentials: "include"`, XHR needs `withCredentials = true`, and EventSource
needs its credentials option when that behavior is intended.

Do not rely on CORS to prevent a request from being sent. CORS controls whether
module JavaScript can read the cross-origin response; a simple request may
already have reached the server even when the response is unreadable. A failed
preflight can stop the corresponding non-simple request, but the server must
still enforce authentication, authorization, and CSRF protections itself.

The permissions allow the host to pass matching connections; they do not turn
off Chromium/WebView2 security rules. Because a module is served from HTTPS,
mixed-content policy may still block plaintext `http:` fetches and `ws:`
connections. Prefer HTTPS and WSS. `network.http` is broader than
`network.https`, but it is not a mixed-content bypass.

These permissions cover connection APIs only. Remote scripts, images, and
files; form submissions; workers; and plug-ins remain blocked. Package scripts,
images, and other static assets inside the validated module directory.

## Trust model

Module HTML and JavaScript are local user-installed code. Install only packages
you trust. Each accepted package receives a separate virtual origin. The host
allows only the connection APIs covered by the module's declared network
permissions and continues to block file access and remote subresources. Module
documents also receive a host-enforced content security policy: packaged local
scripts, styles, images, fonts, and media are supported, while remote scripts,
images, and files, workers, forms, and plug-ins are disabled. The host denies
browser permission requests, prevents modules from framing the privileged main
interface, rejects direct WebView messages from a module origin, and checks the
declared permission before relaying an API request or allowing a network
connection.

The host also derives each storage namespace from the registered frame rather
than accepting a module ID or filesystem path from request data. Modules cannot
read or write another module's persistent or session namespace through the SDK.

Module package directories remain editable by the signed-in Windows user, but
the running frame uses the host's isolated snapshot. Choose **Reload modules**
after modifying a package so the host validates it again, creates a fresh
snapshot, and replaces the existing frame. The permission list and browser
isolation reduce accidental privilege; they do not make code from an untrusted
package safe to install.
