# Module SDK v1

Meccha Mod Menu can load trusted local Web modules from:

```text
%LOCALAPPDATA%\MecchaCamouflage\modules\<module-id>\
```

Each package contains a validated `module.json` and an HTML entry point. Copy
the [`example`](example/) directory into the modules directory, then choose
**Reload modules** in the app.

For a raw-memory walkthrough, use the separate
[`process-memory-example`](process-memory-example/) package. Its self-test runs
only after a button click and touches only an allocation that it creates and
frees itself.

The HTML entry must be UTF-8 and contain an explicit `<head>` before any
executable markup. The host serves an isolated runtime snapshot and injects
UTF-8, no-referrer, and host-owned security metadata at the start of
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
| `storage.read` | `storage.get` and `storage.list` for persistent module data |
| `storage.write` | `storage.set` and `storage.delete` for persistent module data |
| `memory.read` | `memory.get` and `memory.list` for session module data |
| `memory.write` | `memory.set` and `memory.delete` for session module data |
| `process.memory.read` | `process.memory.read` against the authenticated attached game |
| `process.memory.write` | `process.memory.allocate`, `write`, `protect`, `inject`, and `free` |

There is deliberately no generic native-command or filesystem permission. Raw
process access is limited to the fixed `process.memory.*` commands, and those
commands always target the game instance authenticated by the host; request
payloads never select a PID. Browser networking is broad for every accepted
module: HTTP, HTTPS, WS, WSS, `fetch`, XHR, EventSource,
`navigator.sendBeacon()`, and hyperlink `ping` need no manifest permission. The
`network.http`, `network.https`, `network.websocket`, and `network.beacon`
values remain accepted as no-op network metadata, but they do not narrow or
expand access.

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
    }, command.startsWith("process.memory.") ? 20_000 : 10_000);

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

function normalizeAddress(address) {
  if (typeof address !== "string" || !/^0x[0-9a-f]{1,16}$/i.test(address)) {
    throw new TypeError("address must be a 0x-prefixed hexadecimal string");
  }
  const parsed = BigInt(address);
  if (parsed === 0n) throw new RangeError("address must be nonzero");
  return `0x${parsed.toString(16)}`;
}

function bytesToHex(value) {
  if (typeof value === "string") {
    if (value.length === 0 || value.length % 2 !== 0 ||
        value.length / 2 > 3 * 1024 * 1024 || !/^[0-9a-f]+$/i.test(value)) {
      throw new TypeError("hexadecimal bytes must contain complete byte pairs");
    }
    return value.toLowerCase();
  }
  const bytes = value instanceof Uint8Array
    ? value
    : value instanceof ArrayBuffer
      ? new Uint8Array(value)
      : ArrayBuffer.isView(value)
        ? new Uint8Array(value.buffer, value.byteOffset, value.byteLength)
        : null;
  if (!bytes) {
    throw new TypeError("bytes must be hexadecimal, a Uint8Array, or ArrayBuffer");
  }
  if (bytes.length === 0 || bytes.length > 3 * 1024 * 1024) {
    throw new RangeError("bytes must contain between 1 byte and 3 MiB");
  }
  const parts = [];
  for (let offset = 0; offset < bytes.length; offset += 16_384) {
    let part = "";
    const end = Math.min(offset + 16_384, bytes.length);
    for (let index = offset; index < end; index += 1) {
      part += bytes[index].toString(16).padStart(2, "0");
    }
    parts.push(part);
  }
  return parts.join("");
}

function hexToBytes(value) {
  if (typeof value !== "string" || value.length === 0 ||
      value.length % 2 !== 0 || value.length / 2 > 3 * 1024 * 1024 ||
      !/^[0-9a-f]+$/i.test(value)) {
    throw new TypeError("host returned invalid hexadecimal bytes");
  }
  const bytes = new Uint8Array(value.length / 2);
  for (let index = 0; index < bytes.length; index += 1) {
    bytes[index] = Number.parseInt(value.slice(index * 2, index * 2 + 2), 16);
  }
  return bytes;
}

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
  }),
  processMemory: Object.freeze({
    allocate: (size, protection = "read-write") =>
      sdkRequest("process.memory.allocate", { size, protection }),
    read: async (address, size) => {
      const result = await sdkRequest("process.memory.read", {
        address: normalizeAddress(address), size
      });
      return Object.freeze({ ...result, bytes: hexToBytes(result.dataHex) });
    },
    write: (address, bytes) => sdkRequest("process.memory.write", {
      address: normalizeAddress(address), dataHex: bytesToHex(bytes)
    }),
    protect: (address, size, protection) =>
      sdkRequest("process.memory.protect", {
        address: normalizeAddress(address), size, protection
      }),
    inject: (bytes, protection = "read-write") =>
      sdkRequest("process.memory.inject", {
        dataHex: bytesToHex(bytes), protection
      }),
    free: address => sdkRequest("process.memory.free", {
      address: normalizeAddress(address)
    })
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
to disk. The word “memory” in this namespace does not mean raw game or process
memory: these four APIs never accept addresses or pointers and never access the
native bridge. Raw attached-game access is deliberately separated as
`sdk.processMemory`.

## Raw attached-game process memory

`sdk.processMemory` exposes a fixed set of byte-oriented operations against the
game process that Meccha Mod Menu has already attached and authenticated. A
module never supplies a PID, executable name, bridge endpoint, or connection
token. If that authenticated game is not available, the request fails; it does
not fall back to another same-name process.

Declare only the access the module needs:

```json
"permissions": ["process.memory.read", "process.memory.write"]
```

`process.memory.read` permits `read`. `process.memory.write` permits
`allocate`, `write`, `protect`, `inject`, and `free`; it does not imply raw read
access. The host still binds each call to the registered iframe, module ID, and
current reload generation.

| Wrapper call | Result and behavior |
| --- | --- |
| `allocate(size, protection)` | Creates a module-owned allocation. Returns `address`, `size`, and `protection`. The default protection is `read-write`. |
| `read(address, size)` | Reads exactly `size` bytes. Returns `address`, `size`, and `dataHex`; the wrapper also supplies `bytes` as a `Uint8Array`. |
| `write(address, bytes)` | Writes the entire hexadecimal, `Uint8Array`, or `ArrayBuffer` value. Returns `address`, `bytesWritten`, and `verified`. Partial writes are failures. |
| `protect(address, size, protection)` | Changes protection for the complete range. Returns `address`, `size`, `protection`, and `previousProtection`. |
| `inject(bytes, protection)` | Creates an owned allocation, copies all bytes, and applies the final protection as one SDK operation. Returns `address`, `size`, `bytesWritten`, `protection`, and `verified`. The default is `read-write`; callers may explicitly request `execute-read`. |
| `free(address)` | Releases an allocation created by the same module. Returns `address` and `freed`; it cannot release the game's or another module's allocation. |

At the underlying `postMessage` layer, write and inject use a `dataHex` string;
the wrapper performs the byte conversion. `verified` appears on write and
inject results only and is `true` only after the bridge verifies the complete
copy. Successful operations never return `verified: false` as partial success.

Addresses are strings such as `"0x1234abcd"`. They are never JavaScript
numbers because x64 pointers can exceed JavaScript's exact integer range. The
host returns canonical addresses and accepts only a `0x` prefix followed by
hexadecimal digits. Byte payloads are strict, even-length hexadecimal on the
message wire; the documented wrapper converts them to and from `Uint8Array`.

`allocate` and `inject` accept only these six private-allocation protection
values:

- `no-access`
- `read-only`
- `read-write`
- `execute`
- `execute-read`
- `execute-read-write`

`protect` accepts those six plus `write-copy` and `execute-write-copy`. The two
copy-on-write modes are valid only for `protect`; Windows does not support them
when creating the private committed allocation used by `allocate` and
`inject`. Supplying either there is rejected as an invalid payload, with no
fallback or substitution. An otherwise valid `protect` request can still fail
if the selected range is incompatible with the requested Windows protection.

A successful `protect` result may report `previousProtection: "mixed"` when
the requested range previously spanned regions with different protection
values; `mixed` is descriptive output and is never accepted as a requested
protection.

The native capabilities metadata mirrors this operation-specific contract through
`module_process_memory_private_protections` (six allocate/inject modes) and
`module_process_memory_protect_protections` (all eight protect modes).

One read, write, or inject operation may transfer at most 3 MiB. A single
allocation is limited to 64 MiB, and the bridge tracks at most 256 MiB across
all modules in the attached game. Zero, overflowing, malformed, inaccessible,
and oversized requests fail. The host rejects values outside the contract
instead of clamping sizes, truncating bytes, filling a failed read with zeroes,
or reporting a partial operation as success.

Allocations remain native memory in that game process until their owning module
successfully calls `free`, the authenticated bridge shuts down, or the game
process exits. A module reload does not free them: ownership remains with the
same module ID so the reloaded module can free a retained address. They are not
SDK JSON data, are not written under LocalAppData, and are not transferred to a
replacement game process. Addresses become invalid when the authenticated game
or bridge instance changes.

```js
const marker = new Uint8Array([0x4d, 0x45, 0x43, 0x43, 0x48, 0x41]);
const allocation = await sdk.processMemory.inject(marker, "read-write");
try {
  const check = await sdk.processMemory.read(allocation.address, marker.length);
  console.log(check.bytes);
} finally {
  await sdk.processMemory.free(allocation.address);
}
```

Raw reads can disclose game memory, and the browser network APIs described
below can transmit what a module reads. Raw writes, protection changes, and
injection can corrupt or crash the game. Treat a module with either process
memory permission as native-trusted code.

These APIs inject data bytes only. They do not create threads, call an address,
or execute injected bytes. An executable protection value changes page
protection; it is not an execution request.

## Network APIs

Every validated module receives broad browser connection access. Network APIs
do not need a host message or a network entry in `permissions`; command and data
permissions remain enforced normally.

```json
{
  "schema_version": 1,
  "api_version": 1,
  "id": "online-paint-tools",
  "name": "Online Paint Tools",
  "version": "1.0.0",
  "entry": "index.html",
  "permissions": []
}
```

Use the standard browser interfaces:

```js
const response = await fetch("https://api.example.com/palettes", {
  headers: { "Accept": "application/json" }
});
if (!response.ok) throw new Error(`Request failed: ${response.status}`);
const palettes = await response.json();

const socket = new WebSocket("wss://api.example.com/updates");
socket.addEventListener("open", () => socket.send("subscribe"));
socket.addEventListener("message", event => console.log(event.data));

const queued = navigator.sendBeacon(
  "https://api.example.com/session-end",
  JSON.stringify({ reason: "module-hidden" })
);
if (!queued) console.warn("The browser did not queue the beacon");
```

A packaged hyperlink can request browser-managed auditing directly:

```html
<a href="#saved" ping="https://api.example.com/link-audit">Save preset</a>
```

Both transports are fire-and-forget POST requests. Module code cannot read the
response. A `true` return from `sendBeacon()` means the browser accepted the
payload for delivery; it is not a server acknowledgement. Hyperlink pings are
sent only when the browser activates the link. Target servers must accept the
browser-selected request shape and content type.
Cross-origin Beacon payloads with a non-safelisted content type can trigger a
CORS preflight, which the receiving server must accept before the POST.

A network-only module can omit permissions entirely:

```json
"permissions": []
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
remote target domain are profile state, however. Any module can therefore
include target-domain state that another module used, subject to the
browser's SameSite and third-party-cookie rules. Cross-origin `fetch` needs
`credentials: "include"`, XHR needs `withCredentials = true`, and EventSource
needs its credentials option when that behavior is intended.

Do not rely on CORS to prevent a request from being sent. CORS controls whether
module JavaScript can read the cross-origin response; a simple request may
already have reached the server even when the response is unreadable. A failed
preflight can stop the corresponding non-simple request, but the server must
still enforce authentication, authorization, and CSRF protections itself.

The host enables Chromium's plaintext connection path so `http:` and `ws:`
transports are not rejected as mixed content. This does not make cleartext
transport private or tamper-resistant. Prefer HTTPS and WSS whenever the server
supports them.
Browser CORS, cookie, redirect, DNS, TLS, and server-side rules still apply.

Broad network access covers the listed connection and fire-and-forget APIs only.
Remote scripts, images, and files; form submissions; workers; and plug-ins remain
blocked. Package scripts, images, and other static assets inside the validated
module directory.

## Trust model

Module HTML and JavaScript are local user-installed code. Install only packages
you trust. Each accepted package receives a separate virtual origin. The host
allows the listed connection APIs broadly and continues to block file access
and remote subresources. Module
documents also receive a host-enforced content security policy: packaged local
scripts, styles, images, fonts, and media are supported, while remote scripts,
images, and files, workers, forms, and plug-ins are disabled. The host denies
browser permission requests, prevents modules from framing the privileged main
interface, rejects direct WebView messages from a module origin, and checks the
declared permission before relaying command or data API requests.

The host also derives each storage namespace from the registered frame rather
than accepting a module ID or filesystem path from request data. Modules cannot
read or write another module's persistent or session namespace through the SDK.

Process-memory requests are likewise bound to the registered frame and current
authenticated game. The host derives the allocation owner from that frame;
payloads cannot impersonate another module, select a PID, or free another
module's allocation. These ownership and routing checks prevent accidental
cross-module operations, but they do not turn native memory access into a safe
sandbox. A module with `process.memory.read` can inspect native game data and
send it over the broadly available network APIs. A module with
`process.memory.write` can modify or corrupt the attached game. Either grant
must be treated as native-trusted access.

Module package directories remain editable by the signed-in Windows user, but
the running frame uses the host's isolated snapshot. Choose **Reload modules**
after modifying a package so the host validates it again, creates a fresh
snapshot, and replaces the existing frame. The permission list and browser
isolation reduce accidental privilege; they do not make code from an untrusted
package safe to install. They also cannot protect the game from a module that
the user explicitly grants process-memory access.
