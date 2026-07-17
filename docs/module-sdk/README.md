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
| `network.https` | `fetch`, `XMLHttpRequest`, and `EventSource` over HTTPS only |
| `network.http` | `fetch`, `XMLHttpRequest`, and `EventSource` over HTTP or HTTPS |
| `network.websocket` | `WebSocket` connections over `ws:` or `wss:` |

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
  if (event.origin !== "https://meccha.localhost") return;
  if (event.data?.source !== "meccha-host" || event.data?.apiVersion !== 1) return;
  // Handle response or snapshotChanged here.
});
```

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

Module package directories remain editable by the signed-in Windows user, but
the running frame uses the host's isolated snapshot. Choose **Reload modules**
after modifying a package so the host validates it again, creates a fresh
snapshot, and replaces the existing frame. The permission list and browser
isolation reduce accidental privilege; they do not make code from an untrusted
package safe to install.
