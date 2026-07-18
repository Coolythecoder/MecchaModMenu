function reportUiStartupFailure(kind, value) {
  try {
    const message = value instanceof Error ? value.message : String(value ?? "unknown JavaScript error");
    window.chrome?.webview?.postMessage({
      type: "uiStartupFailure",
      kind,
      message: message.slice(0, 2000)
    });
  } catch {
    // A broken WebView bridge must not turn error reporting into another page error.
  }
}

window.addEventListener("error", event => {
  const location = event.filename ? ` (${event.filename}:${event.lineno}:${event.colno})` : "";
  reportUiStartupFailure("error", `${event.message || "JavaScript error"}${location}`);
});

window.addEventListener("unhandledrejection", event => {
  reportUiStartupFailure("unhandledrejection", event.reason);
});

const pending = new Map();
const hotkeyKeys = [
  "app.startHotkey",
  "app.previewHotkey",
  "app.unpreviewHotkey",
  "app.stopHotkey"
];

let liveSnapshot = null;
let draftSnapshot = null;
let editing = false;
let activeLogFilter = "all";
let recordingHotkey = null;
let lastRenderedLogValue = null;
let activeModule = "auto-paint";
let paintCommandPending = false;
let stopCommandPending = false;
let paintStudioCommandPending = "";
let moduleManagerCommandPending = false;
let paintPresetSignature = "";
let externalModuleInvalidCount = 0;
let externalModuleCatalogDiagnostics = [];
let lastExternalSnapshotSignature = "";
let externalModulesSuspended = false;
let externalModuleGeneration = "";
const externalModules = new Map();
const externalModuleActions = new Map();
const externalModuleStops = new Map();
const externalModuleDataCounts = new Map();
const externalModuleProcessMemoryPending = new Set();
const ExternalModuleApiVersion = 1;
const ExternalModuleHostPattern = /^m-[0-9a-f]{32}\.localhost$/;
const ModuleDataKeyPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/;
const ModuleDataValueMaxBytes = 256 * 1024;
const ProcessMemoryAddressPattern = /^0x[0-9a-fA-F]{1,16}$/;
const ProcessMemoryTransferMaxBytes = 3 * 1024 * 1024;
const ProcessMemoryAllocationMaxBytes = 64 * 1024 * 1024;
const ProcessMemoryProtectionChangeModes = new Set([
  "no-access", "read-only", "read-write", "write-copy",
  "execute", "execute-read", "execute-read-write", "execute-write-copy"
]);
const ProcessMemoryPrivateAllocationModes = new Set([
  "no-access", "read-only", "read-write",
  "execute", "execute-read", "execute-read-write"
]);
const ExternalModuleCommands = new Map([
  ["paint.start", "paint"],
  ["paint.preview", "preview"],
  ["paint.restore", "unpreview"],
  ["paint.stop", "stop"]
]);
const ExternalModulePermissions = new Map([
  ["snapshot.get", "snapshot.read"],
  ["paint.start", "paint.start"],
  ["paint.preview", "paint.preview"],
  ["paint.restore", "paint.restore"],
  ["paint.stop", "paint.stop"],
  ["storage.get", "storage.read"],
  ["storage.list", "storage.read"],
  ["storage.set", "storage.write"],
  ["storage.delete", "storage.write"],
  ["memory.get", "memory.read"],
  ["memory.list", "memory.read"],
  ["memory.set", "memory.write"],
  ["memory.delete", "memory.write"],
  ["process.memory.read", "process.memory.read"],
  ["process.memory.allocate", "process.memory.write"],
  ["process.memory.write", "process.memory.write"],
  ["process.memory.protect", "process.memory.write"],
  ["process.memory.inject", "process.memory.write"],
  ["process.memory.free", "process.memory.write"]
]);
const ModuleDataCommands = new Set([
  "storage.get", "storage.list", "storage.set", "storage.delete",
  "memory.get", "memory.list", "memory.set", "memory.delete"
]);
const ProcessMemoryCommands = new Set([
  "process.memory.allocate", "process.memory.read", "process.memory.write",
  "process.memory.protect", "process.memory.inject", "process.memory.free"
]);

window.chrome.webview.addEventListener("message", event => {
  const message = event.data;
  if (message.type === "response") {
    const entry = pending.get(message.id);
    if (entry) {
      pending.delete(message.id);
      message.ok ? entry.resolve(message.data) : entry.reject(message.data);
    }
    return;
  }
  if (message.type === "event" && message.name === "snapshotChanged") {
    liveSnapshot = message.data;
    render();
    return;
  }
  if (message.type === "event" && message.name === "toast") {
    toast(message.data.message, message.data.level || "success");
  }
});

function send(command, payload = {}) {
  const id = crypto.randomUUID();
  const promise = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
  window.chrome.webview.postMessage({ id, command, payload });
  return promise;
}

function byId(id) {
  return document.getElementById(id);
}

function text(id, value) {
  byId(id).textContent = value;
}

function setValue(id, next) {
  const element = byId(id);
  if (document.activeElement !== element) {
    element.value = next;
  }
}

function setChecked(id, next) {
  byId(id).checked = Boolean(next);
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function fmt(value) {
  return Number(value).toFixed(2).replace(/\.?0+$/, "");
}

function activeLocale() {
  return currentSnapshot()?.language || liveSnapshot?.language || "en";
}

function translationsFor(locale) {
  const translations = liveSnapshot?.translations || {};
  return translations[locale] || translations.en || {};
}

function i18n(key, ...args) {
  const locale = activeLocale();
  const local = translationsFor(locale);
  const english = translationsFor("en");
  let value = local[key] || english[key] || key;
  args.forEach((arg, index) => {
    value = value.replaceAll(`{${index}}`, arg);
  });
  return value;
}

function applyI18n() {
  for (const element of document.querySelectorAll("[data-i18n]")) {
    element.textContent = i18n(element.dataset.i18n);
  }
  for (const element of document.querySelectorAll("[data-i18n-aria-label]")) {
    element.setAttribute("aria-label", i18n(element.dataset.i18nAriaLabel));
  }
  for (const element of document.querySelectorAll("[data-i18n-placeholder]")) {
    element.setAttribute("placeholder", i18n(element.dataset.i18nPlaceholder));
  }
  document.title = i18n("app.title");
}

function currentSnapshot() {
  return editing && draftSnapshot ? draftSnapshot : liveSnapshot;
}

function render() {
  if (!liveSnapshot) {
    return;
  }
  applyI18n();
  renderRuntime(liveSnapshot);
  renderSettings(currentSnapshot());
  applyI18n();
  renderEditState();
  renderModules(liveSnapshot);
}

function renderRuntime(snapshot) {
  const runtime = snapshot.runtime;
  setStatus("footer-process", runtime.process);
  setStatus("footer-bridge", runtime.bridge);
  text("version", snapshot.version);
  renderLogs(runtime);
}

function renderModules(snapshot) {
  const runtime = snapshot?.runtime || {};
  const mods = snapshot?.mods && typeof snapshot.mods === "object" ? snapshot.mods : {};
  const autoPaint = objectValue(mods.autoPaint);

  const autoAvailable = booleanField(
    autoPaint,
    ["available"],
    runtime.process === "attached" || runtime.bridge === "connected"
  );
  const autoRunning = booleanField(
    autoPaint,
    ["running", "enabled"],
    Boolean(runtime.paintRunning)
  );
  const autoBusy = booleanField(autoPaint, ["busy"], autoRunning) || paintCommandPending;
  const autoState = autoRunning
    ? "running"
    : autoBusy
      ? "busy"
      : autoAvailable
        ? "ready"
        : "waiting";
  setModuleState("auto-paint-state", autoState);
  setModuleMessage("auto-paint-message", stringField(autoPaint, ["message"]));

  const paintActionLocked = editing || !autoAvailable || autoRunning || autoBusy;
  setButtonDisabled("paint-start", paintActionLocked);
  setButtonDisabled("paint-preview", paintActionLocked);
  setButtonDisabled("paint-unpreview", paintActionLocked);
  setButtonDisabled(
    "paint-stop",
    editing || stopCommandPending || (!autoRunning && !paintCommandPending)
  );

  renderPaintStudio(snapshot, autoAvailable, autoRunning, autoBusy);
  reconcileExternalModules(snapshot?.externalModules);
  renderExternalModuleDiagnostics(snapshot?.moduleDiagnostics);
  renderModuleNavigation();
  broadcastExternalSnapshotChanged(liveSnapshot);
}

function renderPaintStudio(snapshot, autoAvailable, autoRunning, autoBusy) {
  const studio = objectValue(snapshot?.paintStudio);
  const studioBusy = paintStudioCommandPending.length > 0 || paintCommandPending;
  setModuleState(
    "paint-studio-state",
    studioBusy ? "busy" : autoAvailable ? "ready" : "waiting"
  );

  const presets = normalizePaintPresets(studio.presets);
  renderPaintPresetOptions(presets);
  const selectedPreset = byId("paint-preset-select").value;
  const presetLocked = editing || paintStudioCommandPending.length > 0;
  byId("paint-preset-name").disabled = presetLocked;
  byId("paint-preset-select").disabled = presetLocked || presets.length === 0;
  setButtonDisabled("paint-preset-save", presetLocked);
  setButtonDisabled("paint-preset-apply", presetLocked || selectedPreset.length === 0);
  setButtonDisabled("paint-preset-delete", presetLocked || selectedPreset.length === 0);
  setButtonDisabled(
    "paint-settings-undo",
    presetLocked || !booleanField(studio, ["canUndoSettings"], false)
  );

  const previewLocked = editing || !autoAvailable || autoRunning || autoBusy || studioBusy;
  setButtonDisabled("paint-studio-preview", previewLocked);
  setButtonDisabled("paint-studio-restore", previewLocked);
  renderPaintCoverage(objectValue(studio.coverage));
}

function normalizePaintPresets(value) {
  if (!Array.isArray(value)) {
    return [];
  }
  const seen = new Set();
  const presets = [];
  for (const item of value) {
    if (!item || typeof item !== "object") {
      continue;
    }
    const name = safeText(item.name, 40);
    if (name.length === 0 || seen.has(name)) {
      continue;
    }
    seen.add(name);
    presets.push({ name, updatedAt: safeText(item.updatedAt, 80) });
  }
  return presets;
}

function renderPaintPresetOptions(presets) {
  const signature = JSON.stringify([activeLocale(), presets]);
  if (signature === paintPresetSignature) {
    return;
  }
  paintPresetSignature = signature;
  const select = byId("paint-preset-select");
  const previous = select.value;
  select.replaceChildren();
  const placeholder = document.createElement("option");
  placeholder.value = "";
  placeholder.textContent = i18n("paint.preset.choose");
  select.append(placeholder);
  for (const preset of presets) {
    const option = document.createElement("option");
    option.value = preset.name;
    option.textContent = preset.updatedAt.length > 0
      ? `${preset.name} · ${formatPresetUpdatedAt(preset.updatedAt)}`
      : preset.name;
    option.title = preset.updatedAt;
    select.append(option);
  }
  select.value = presets.some(preset => preset.name === previous) ? previous : "";
}

function formatPresetUpdatedAt(value) {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? date.toLocaleString(activeLocale()) : value;
}

function renderPaintCoverage(coverage) {
  const available = booleanField(coverage, ["available"], false);
  if (!available) {
    text("paint-coverage-percent", "-");
    text("paint-coverage-samples", "-");
    text("paint-coverage-strokes", "-");
    text("paint-coverage-detail", "-");
    text("paint-coverage-result", stringField(coverage, ["result"]) || i18n("state.unavailable"));
    return;
  }
  const percent = clamp(finiteNumber(coverage.coveragePercent, 0), 0, 100);
  const enabled = nonNegativeInteger(coverage.enabledSamples);
  const total = nonNegativeInteger(coverage.totalSamples);
  const strokes = nonNegativeInteger(coverage.plannedStrokes);
  const detailSelected = nonNegativeInteger(coverage.detailSelected);
  const detailBudget = nonNegativeInteger(coverage.detailBudget);
  text("paint-coverage-percent", `${fmt(percent)}%`);
  text("paint-coverage-samples", `${enabled} / ${total}`);
  text("paint-coverage-strokes", String(strokes));
  text("paint-coverage-detail", `${detailSelected} / ${detailBudget}`);
  text("paint-coverage-result", stringField(coverage, ["result"]) || i18n("state.ready"));
}

function objectValue(value) {
  return value && typeof value === "object" ? value : {};
}

function booleanField(source, names, fallback) {
  for (const name of names) {
    if (typeof source[name] === "boolean") {
      return source[name];
    }
  }
  return Boolean(fallback);
}

function stringField(source, names) {
  for (const name of names) {
    if (typeof source[name] === "string" && source[name].trim().length > 0) {
      return source[name].trim();
    }
  }
  return "";
}

function finiteNumber(value, fallback = 0) {
  const number = Number(value);
  return Number.isFinite(number) ? number : fallback;
}

function nonNegativeInteger(value, fallback = 0) {
  return Math.max(0, Math.round(finiteNumber(value, fallback)));
}

function safeText(value, maximumLength = 200) {
  return typeof value === "string" ? value.trim().slice(0, maximumLength) : "";
}

function setModuleState(id, state) {
  const element = byId(id);
  element.textContent = i18n(`state.${state}`);
  element.className = `status-token ${statusClass(state)}`;
}

function setModuleMessage(id, value) {
  const element = byId(id);
  element.textContent = value;
  element.hidden = value.length === 0;
}

function setButtonDisabled(id, disabled) {
  byId(id).disabled = Boolean(disabled);
}

function renderModuleNavigation() {
  for (const tab of document.querySelectorAll("[data-module-target]")) {
    const selected = tab.dataset.moduleTarget === activeModule;
    tab.classList.toggle("active", selected);
    tab.setAttribute("aria-selected", String(selected));
    tab.tabIndex = selected ? 0 : -1;
  }
  for (const panel of document.querySelectorAll("[data-module-panel]")) {
    const selected = panel.dataset.modulePanel === activeModule;
    panel.classList.toggle("active", selected);
    panel.hidden = !selected;
  }
  byId("settings-action-bar").hidden = activeModule.startsWith("external:");
}

function selectModule(module) {
  const exists = [...document.querySelectorAll("[data-module-panel]")]
    .some(panel => panel.dataset.modulePanel === module);
  if (!exists) {
    return;
  }
  activeModule = module;
  renderModuleNavigation();
}

function reconcileExternalModules(value) {
  if (externalModulesSuspended) {
    return;
  }
  const normalized = normalizeExternalModuleDescriptors(value);
  externalModuleInvalidCount = normalized.invalidCount;
  const desired = new Map(normalized.descriptors.map(descriptor => [descriptor.id, descriptor]));

  for (const [id, current] of externalModules) {
    const next = desired.get(id);
    if (next && next.signature === current.descriptor.signature) {
      continue;
    }
    current.tab.remove();
    current.panel.remove();
    externalModules.delete(id);
    if (!next && activeModule === current.target) {
      activeModule = "auto-paint";
    }
  }

  for (const descriptor of normalized.descriptors) {
    if (!externalModules.has(descriptor.id)) {
      externalModules.set(descriptor.id, createExternalModule(descriptor));
    }
  }

  const navigation = document.querySelector(".module-nav");
  const moduleColumn = document.querySelector(".module-column");
  const settingsBar = byId("settings-action-bar");
  let nextTab = null;
  let nextPanel = settingsBar;
  for (const descriptor of [...normalized.descriptors].reverse()) {
    const module = externalModules.get(descriptor.id);
    if (module.tab.parentNode !== navigation || module.tab.nextSibling !== nextTab) {
      navigation.insertBefore(module.tab, nextTab);
    }
    if (module.panel.parentNode !== moduleColumn || module.panel.nextSibling !== nextPanel) {
      moduleColumn.insertBefore(module.panel, nextPanel);
    }
    nextTab = module.tab;
    nextPanel = module.panel;
  }
}

window.mecchaUnloadExternalModulesForReload = () => {
  // Allocate/inject can succeed natively only once and return the sole address
  // needed to free the owned allocation. Keep the current frame and generation
  // intact until every process-memory response has been delivered.
  if (externalModuleProcessMemoryPending.size !== 0) {
    return false;
  }
  externalModulesSuspended = true;
  externalModuleGeneration = "";
  for (const module of externalModules.values()) {
    module.tab.remove();
    module.panel.remove();
  }
  externalModules.clear();
  lastExternalSnapshotSignature = "";
  if (activeModule.startsWith("external:")) {
    activeModule = "auto-paint";
  }
  renderModuleNavigation();
  return true;
};

window.mecchaFinishExternalModulesReload = generation => {
  if (typeof generation !== "string" || !/^[0-9a-f]{32}$/.test(generation)) {
    return false;
  }
  externalModuleGeneration = generation;
  externalModulesSuspended = false;
  render();
  return true;
};

function normalizeExternalModuleDescriptors(value) {
  if (value === undefined || value === null) {
    return { descriptors: [], invalidCount: 0 };
  }
  if (!Array.isArray(value)) {
    return { descriptors: [], invalidCount: 1 };
  }
  const descriptors = [];
  const ids = new Set();
  const origins = new Set();
  let invalidCount = 0;
  for (const item of value) {
    const descriptor = normalizeExternalModuleDescriptor(item);
    if (!descriptor ||
        (externalModuleGeneration && descriptor.generation !== externalModuleGeneration) ||
        ids.has(descriptor.id) ||
        origins.has(descriptor.origin)) {
      invalidCount += 1;
      continue;
    }
    ids.add(descriptor.id);
    origins.add(descriptor.origin);
    descriptors.push(descriptor);
  }
  return { descriptors, invalidCount };
}

function normalizeExternalModuleDescriptor(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }
  const id = safeText(value.id, 64);
  const name = safeText(value.name, 80);
  const version = safeText(value.version, 40);
  const description = safeText(value.description, 500);
  if (!/^[a-z0-9][a-z0-9._-]{0,63}$/.test(id) || name.length === 0 || !Array.isArray(value.permissions)) {
    return null;
  }
  let url;
  try {
    url = new URL(value.entryUrl);
  } catch {
    return null;
  }
  if (url.protocol !== "https:" ||
      !ExternalModuleHostPattern.test(url.hostname) ||
      url.port !== "" ||
      url.username ||
      url.password) {
    return null;
  }
  const pathSegments = url.pathname.split("/").filter(Boolean);
  if (pathSegments.length < 2 || !/^[0-9a-f]{32}$/.test(pathSegments[0])) {
    return null;
  }
  const permissions = [...new Set(value.permissions
    .filter(permission => typeof permission === "string")
    .map(permission => permission.trim())
    .filter(permission => permission.length > 0 && permission.length <= 64))].sort();
  const signature = JSON.stringify([id, name, version, description, url.href, permissions]);
  return {
    id,
    name,
    version,
    description,
    entryUrl: url.href,
    origin: url.origin,
    permissions: new Set(permissions),
    generation: pathSegments[0],
    signature
  };
}

function createExternalModule(descriptor) {
  const target = `external:${descriptor.id}`;
  const tabId = `module-tab-external-${descriptor.id}`;
  const panelId = `module-external-${descriptor.id}`;
  const tab = document.createElement("button");
  tab.id = tabId;
  tab.type = "button";
  tab.role = "tab";
  tab.dataset.moduleTarget = target;
  tab.setAttribute("aria-controls", panelId);
  tab.setAttribute("aria-selected", "false");
  tab.textContent = descriptor.name;
  tab.title = descriptor.description;

  const panel = document.createElement("section");
  panel.id = panelId;
  panel.className = "panel panel-fill corner-accent module-panel external-module-panel";
  panel.dataset.modulePanel = target;
  panel.setAttribute("role", "tabpanel");
  panel.setAttribute("aria-labelledby", tabId);
  panel.hidden = true;

  const title = document.createElement("div");
  title.className = "panel-title";
  const name = document.createElement("span");
  name.textContent = descriptor.name;
  const version = document.createElement("b");
  version.className = "external-module-version";
  version.textContent = descriptor.version;
  title.append(name, version);

  const description = document.createElement("div");
  description.className = "external-module-description";
  description.textContent = descriptor.description;

  const iframe = document.createElement("iframe");
  iframe.setAttribute("sandbox", "allow-scripts allow-same-origin");
  iframe.setAttribute("allow", "document-domain 'none'");
  iframe.title = descriptor.name;
  iframe.loading = "lazy";
  iframe.referrerPolicy = "no-referrer";
  iframe.src = descriptor.entryUrl;
  panel.append(title, description, iframe);
  tab.addEventListener("click", () => selectModule(target));
  const module = { descriptor, target, tab, panel, iframe, loaded: false };
  iframe.addEventListener("load", () => {
    module.loaded = true;
    broadcastExternalSnapshotChanged(liveSnapshot, module);
  });
  return module;
}

function renderExternalModuleDiagnostics(moduleDiagnostics) {
  if (moduleDiagnostics !== undefined) {
    externalModuleCatalogDiagnostics = normalizeModuleDiagnostics(moduleDiagnostics);
  }
  const diagnostics = byId("external-modules-diagnostics");
  if (!diagnostics) {
    return;
  }
  diagnostics.textContent = i18n(
    "external.modules.diagnostics",
    externalModules.size,
    externalModuleInvalidCount + externalModuleCatalogDiagnostics.length,
    new Set([...externalModuleActions.keys(), ...externalModuleStops.keys()]).size
  );
  diagnostics.title = externalModuleCatalogDiagnostics
    .map(item => item.moduleId.length > 0
      ? `${item.moduleId}: ${item.message}`
      : item.message)
    .join("\n");
  byId("open-modules").disabled = moduleManagerCommandPending;
  byId("reload-modules").disabled = moduleManagerCommandPending;
}

function normalizeModuleDiagnostics(value) {
  if (!Array.isArray(value)) {
    return [];
  }
  return value
    .filter(item => item && typeof item === "object" && !Array.isArray(item))
    .map(item => ({
      code: safeText(item.code, 80),
      message: safeText(item.message, 500),
      moduleId: safeText(item.moduleId, 64)
    }))
    .filter(item => item.message.length > 0);
}

function normalizeModuleDataPayload(command, value) {
  const payload = value === undefined ? {} : value;
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    return { ok: false, code: "invalid_payload", message: "payload must be an object." };
  }
  const keys = Object.keys(payload);
  if (command.endsWith(".list")) {
    return keys.length === 0
      ? { ok: true, payload: {} }
      : { ok: false, code: "invalid_payload", message: "list does not accept payload fields." };
  }
  const expectsValue = command.endsWith(".set");
  const expected = expectsValue ? ["key", "value"] : ["key"];
  if (keys.length !== expected.length || expected.some(key => !Object.hasOwn(payload, key))) {
    return { ok: false, code: "invalid_payload", message: "The command payload fields are invalid." };
  }
  if (typeof payload.key !== "string" || !ModuleDataKeyPattern.test(payload.key)) {
    return { ok: false, code: "invalid_key", message: "key must be a lowercase logical data name." };
  }
  if (!expectsValue) {
    return { ok: true, payload: { key: payload.key } };
  }
  try {
    const encoded = JSON.stringify(payload.value, (_, item) => {
      if (typeof item === "number" && !Number.isFinite(item)) {
        throw new TypeError("JSON numbers must be finite.");
      }
      if (["undefined", "function", "symbol", "bigint"].includes(typeof item)) {
        throw new TypeError("value must contain JSON-compatible values only.");
      }
      return item;
    });
    if (encoded === undefined) {
      throw new TypeError("value must be JSON-compatible.");
    }
    if (new TextEncoder().encode(encoded).length > ModuleDataValueMaxBytes) {
      return { ok: false, code: "item_too_large", message: "The JSON value exceeds the per-item byte limit." };
    }
    return { ok: true, payload: { key: payload.key, value: JSON.parse(encoded) } };
  } catch (error) {
    return { ok: false, code: "invalid_value", message: safeText(error?.message || String(error), 500) };
  }
}

function normalizeProcessMemoryPayload(command, value) {
  const payload = value === undefined ? {} : value;
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) {
    return { ok: false, code: "invalid_payload", message: "payload must be an object." };
  }

  const hasExactFields = (required, optional = []) => {
    const keys = Object.keys(payload);
    const allowed = new Set([...required, ...optional]);
    return required.every(key => Object.hasOwn(payload, key)) &&
      keys.every(key => allowed.has(key)) &&
      keys.length >= required.length && keys.length <= allowed.size;
  };
  const normalizeAddress = value => {
    if (typeof value !== "string" || !ProcessMemoryAddressPattern.test(value)) {
      return null;
    }
    try {
      const parsed = BigInt(value);
      return parsed > 0n ? `0x${parsed.toString(16)}` : null;
    } catch {
      return null;
    }
  };
  const normalizeSize = (value, maximum) =>
    Number.isSafeInteger(value) && value > 0 && value <= maximum ? value : null;
  const normalizeDataHex = value => {
    if (typeof value !== "string" || value.length === 0 || (value.length & 1) !== 0 ||
        value.length / 2 > ProcessMemoryTransferMaxBytes || !/^[0-9a-fA-F]+$/.test(value)) {
      return null;
    }
    return value.toLowerCase();
  };
  const normalizeProtection = (value, allowed, fallback = null) => {
    if (value === undefined && fallback !== null) {
      return fallback;
    }
    return typeof value === "string" && allowed.has(value) ? value : null;
  };

  if (command === "process.memory.allocate") {
    if (!hasExactFields(["size"], ["protection"])) {
      return { ok: false, code: "invalid_payload", message: "allocate accepts size and optional protection." };
    }
    const size = normalizeSize(payload.size, ProcessMemoryAllocationMaxBytes);
    const protection = normalizeProtection(
      payload.protection,
      ProcessMemoryPrivateAllocationModes,
      "read-write"
    );
    return size !== null && protection !== null
      ? { ok: true, payload: { size, protection } }
      : { ok: false, code: "invalid_payload", message: "size or protection is invalid." };
  }

  if (command === "process.memory.read") {
    if (!hasExactFields(["address", "size"])) {
      return { ok: false, code: "invalid_payload", message: "read requires address and size." };
    }
    const address = normalizeAddress(payload.address);
    const size = normalizeSize(payload.size, ProcessMemoryTransferMaxBytes);
    return address !== null && size !== null
      ? { ok: true, payload: { address, size } }
      : { ok: false, code: "invalid_payload", message: "address or size is invalid." };
  }

  if (command === "process.memory.write") {
    if (!hasExactFields(["address", "dataHex"])) {
      return { ok: false, code: "invalid_payload", message: "write requires address and dataHex." };
    }
    const address = normalizeAddress(payload.address);
    const dataHex = normalizeDataHex(payload.dataHex);
    return address !== null && dataHex !== null
      ? { ok: true, payload: { address, dataHex } }
      : { ok: false, code: "invalid_payload", message: "address or dataHex is invalid." };
  }

  if (command === "process.memory.protect") {
    if (!hasExactFields(["address", "size", "protection"])) {
      return { ok: false, code: "invalid_payload", message: "protect requires address, size, and protection." };
    }
    const address = normalizeAddress(payload.address);
    const size = normalizeSize(payload.size, ProcessMemoryAllocationMaxBytes);
    const protection = normalizeProtection(
      payload.protection,
      ProcessMemoryProtectionChangeModes
    );
    return address !== null && size !== null && protection !== null
      ? { ok: true, payload: { address, size, protection } }
      : { ok: false, code: "invalid_payload", message: "address, size, or protection is invalid." };
  }

  if (command === "process.memory.inject") {
    if (!hasExactFields(["dataHex"], ["protection"])) {
      return { ok: false, code: "invalid_payload", message: "inject accepts dataHex and optional protection." };
    }
    const dataHex = normalizeDataHex(payload.dataHex);
    const protection = normalizeProtection(
      payload.protection,
      ProcessMemoryPrivateAllocationModes,
      "read-write"
    );
    return dataHex !== null && protection !== null
      ? { ok: true, payload: { dataHex, protection } }
      : { ok: false, code: "invalid_payload", message: "dataHex or protection is invalid." };
  }

  if (command === "process.memory.free") {
    if (!hasExactFields(["address"])) {
      return { ok: false, code: "invalid_payload", message: "free requires an address." };
    }
    const address = normalizeAddress(payload.address);
    return address !== null
      ? { ok: true, payload: { address } }
      : { ok: false, code: "invalid_payload", message: "address is invalid." };
  }

  return { ok: false, code: "unsupported_command", message: "Unsupported process-memory command." };
}

async function handleExternalModuleMessage(event) {
  const module = [...externalModules.values()].find(candidate =>
    candidate.descriptor.origin !== "null" &&
    event.origin === candidate.descriptor.origin &&
    event.source === candidate.iframe.contentWindow
  );
  if (!module) {
    return;
  }
  const request = event.data;
  if (!request || typeof request !== "object" || Array.isArray(request) || request.source !== "meccha-module") {
    return;
  }
  const requestId = typeof request.requestId === "string" ? request.requestId : "";
  const command = typeof request.command === "string" ? request.command : "";
  if (requestId.length === 0 || requestId.length > 128 || requestId.trim().length === 0) {
    return;
  }
  if (request.apiVersion !== ExternalModuleApiVersion) {
    postExternalModuleResponse(module, requestId, command, false, null, "unsupported_version", "Unsupported module API version.");
    return;
  }
  const permission = ExternalModulePermissions.get(command);
  if (!permission) {
    postExternalModuleResponse(module, requestId, command, false, null, "unsupported_command", "Unsupported module command.");
    return;
  }
  if (!module.descriptor.permissions.has(permission)) {
    postExternalModuleResponse(module, requestId, command, false, null, "permission_denied", "The module does not have permission for this command.");
    return;
  }
  if (command === "snapshot.get") {
    postExternalModuleResponse(module, requestId, command, true, sanitizeSnapshotForModule(liveSnapshot));
    return;
  }
  if (ProcessMemoryCommands.has(command)) {
    if (externalModuleProcessMemoryPending.has(module.descriptor.id)) {
      postExternalModuleResponse(
        module,
        requestId,
        command,
        false,
        null,
        "process_memory_busy",
        "This module already has a process-memory operation in flight."
      );
      return;
    }
    const normalized = normalizeProcessMemoryPayload(command, request.payload);
    if (!normalized.ok) {
      postExternalModuleResponse(module, requestId, command, false, null, normalized.code, normalized.message);
      return;
    }
    const generation = module.descriptor.generation;
    externalModuleProcessMemoryPending.add(module.descriptor.id);
    try {
      const result = await send("moduleProcessMemory", {
        moduleId: module.descriptor.id,
        generation,
        operation: command,
        payload: normalized.payload
      });
      if (externalModulesSuspended || externalModules.get(module.descriptor.id) !== module ||
          module.descriptor.generation !== generation) {
        return;
      }
      if (result?.success) {
        postExternalModuleResponse(module, requestId, command, true, result.data);
      } else {
        postExternalModuleResponse(
          module,
          requestId,
          command,
          false,
          null,
          safeText(result?.code, 80) || "process_memory_failed",
          safeText(result?.message, 500) || "The process-memory command failed."
        );
      }
    } catch (error) {
      if (!externalModulesSuspended && externalModules.get(module.descriptor.id) === module) {
        postExternalModuleResponse(
          module,
          requestId,
          command,
          false,
          null,
          "host_error",
          safeText(error?.message || String(error), 500)
        );
      }
    } finally {
      externalModuleProcessMemoryPending.delete(module.descriptor.id);
    }
    return;
  }
  if (ModuleDataCommands.has(command)) {
    const currentDataRequests = externalModuleDataCounts.get(module.descriptor.id) || 0;
    if (currentDataRequests >= 4) {
      postExternalModuleResponse(module, requestId, command, false, null, "storage_busy", "This module has too many data requests in flight.");
      return;
    }
    const normalized = normalizeModuleDataPayload(command, request.payload);
    if (!normalized.ok) {
      postExternalModuleResponse(module, requestId, command, false, null, normalized.code, normalized.message);
      return;
    }
    const generation = module.descriptor.generation;
    externalModuleDataCounts.set(module.descriptor.id, currentDataRequests + 1);
    try {
      const result = await send("moduleData", {
        moduleId: module.descriptor.id,
        generation,
        operation: command,
        payload: normalized.payload
      });
      if (externalModulesSuspended || externalModules.get(module.descriptor.id) !== module ||
          module.descriptor.generation !== generation) {
        return;
      }
      if (result?.success) {
        postExternalModuleResponse(module, requestId, command, true, result.data);
      } else {
        postExternalModuleResponse(
          module,
          requestId,
          command,
          false,
          null,
          safeText(result?.code, 80) || "command_failed",
          safeText(result?.message, 500) || "The module data command failed."
        );
      }
    } catch (error) {
      if (!externalModulesSuspended && externalModules.get(module.descriptor.id) === module) {
        postExternalModuleResponse(module, requestId, command, false, null, "host_error", safeText(error?.message || String(error), 500));
      }
    } finally {
      const remaining = (externalModuleDataCounts.get(module.descriptor.id) || 1) - 1;
      if (remaining > 0) {
        externalModuleDataCounts.set(module.descriptor.id, remaining);
      } else {
        externalModuleDataCounts.delete(module.descriptor.id);
      }
    }
    return;
  }
  const stopping = command === "paint.stop";
  const actionBlocked = stopping
    ? externalModuleStops.has(module.descriptor.id)
    : externalModuleActions.has(module.descriptor.id) || externalModuleStops.has(module.descriptor.id);
  if (actionBlocked) {
    postExternalModuleResponse(module, requestId, command, false, null, "action_in_flight", "This module already has an action in flight.");
    return;
  }

  const pendingActions = stopping ? externalModuleStops : externalModuleActions;
  const actionToken = {};
  pendingActions.set(module.descriptor.id, actionToken);
  renderExternalModuleDiagnostics();
  try {
    const result = await send(ExternalModuleCommands.get(command));
    const message = safeText(result?.message, 500);
    if (result?.success) {
      postExternalModuleResponse(module, requestId, command, true, { success: true, message });
    } else {
      postExternalModuleResponse(module, requestId, command, false, null, "command_failed", message || "The host command failed.");
    }
  } catch (error) {
    postExternalModuleResponse(module, requestId, command, false, null, "host_error", safeText(error?.message || String(error), 500));
  } finally {
    if (pendingActions.get(module.descriptor.id) === actionToken) {
      pendingActions.delete(module.descriptor.id);
    }
    renderExternalModuleDiagnostics();
  }
}

function postExternalModuleResponse(module, requestId, command, ok, data = null, code = "", message = "") {
  const response = {
    source: "meccha-host",
    apiVersion: ExternalModuleApiVersion,
    type: "response",
    requestId,
    command,
    ok
  };
  if (ok) {
    response.data = data;
  } else {
    response.error = { code, message };
  }
  module.iframe.contentWindow?.postMessage(response, module.descriptor.origin);
}

function broadcastExternalSnapshotChanged(snapshot, targetModule = null) {
  if (!snapshot) {
    return;
  }
  const data = sanitizeSnapshotForModule(snapshot);
  if (!targetModule) {
    const signature = JSON.stringify(data);
    if (signature === lastExternalSnapshotSignature) {
      return;
    }
    lastExternalSnapshotSignature = signature;
  }
  const message = {
    source: "meccha-host",
    apiVersion: ExternalModuleApiVersion,
    type: "event",
    name: "snapshotChanged",
    data
  };
  const modules = targetModule ? [targetModule] : externalModules.values();
  for (const module of modules) {
    if (!module.loaded || !module.descriptor.permissions.has("snapshot.read")) {
      continue;
    }
    module.iframe.contentWindow?.postMessage(message, module.descriptor.origin);
  }
}

function sanitizeSnapshotForModule(snapshot) {
  const runtime = objectValue(snapshot?.runtime);
  const paint = objectValue(snapshot?.settings?.paint);
  const studio = objectValue(snapshot?.paintStudio);
  const coverage = objectValue(studio.coverage);
  return {
    version: safeText(snapshot?.version, 40),
    language: safeText(snapshot?.language, 20),
    runtime: {
      process: safeText(runtime.process, 32),
      bridge: safeText(runtime.bridge, 32),
      service: safeText(runtime.service, 32),
      paintRunning: Boolean(runtime.paintRunning),
      progressVisible: Boolean(runtime.progressVisible),
      progressPercent: clamp(finiteNumber(runtime.progressPercent, 0), 0, 100),
      paintPass: safeText(runtime.paintPass, 80),
      paintPassProgress: safeText(runtime.paintPassProgress, 80),
      paintPassEta: safeText(runtime.paintPassEta, 80),
      paintEta: safeText(runtime.paintEta, 80),
      paintElapsed: safeText(runtime.paintElapsed, 80)
    },
    settings: {
      paint: {
        brush1SizeTexels: finiteNumber(paint.brush1SizeTexels, 30),
        brush2SizeTexels: finiteNumber(paint.brush2SizeTexels, 10),
        detailResolutionPercent: clamp(finiteNumber(paint.detailResolutionPercent, 500), 50, 500),
        packedBatchLimit: nonNegativeInteger(paint.packedBatchLimit, 20),
        packedBatchPacingMs: nonNegativeInteger(paint.packedBatchPacingMs, 50),
        autoMaterial: paint.autoMaterial !== false,
        metallic: clamp(finiteNumber(paint.metallic, 0), 0, 1),
        roughness: clamp(finiteNumber(paint.roughness, 1), 0, 1),
        frontRegionMode: safeText(paint.frontRegionMode, 16) || "fill",
        sideRegionMode: safeText(paint.sideRegionMode, 16) || "paint",
        backRegionMode: safeText(paint.backRegionMode, 16) || "paint",
        fillColor: safeText(paint.fillColor, 16) || "#FFFFFF",
        fillMetallic: clamp(finiteNumber(paint.fillMetallic, 1), 0, 1),
        fillRoughness: clamp(finiteNumber(paint.fillRoughness, 0), 0, 1)
      }
    },
    paintStudio: {
      detailResolutionPercent: clamp(finiteNumber(studio.detailResolutionPercent, paint.detailResolutionPercent || 500), 50, 500),
      presets: normalizePaintPresets(studio.presets),
      canUndoSettings: Boolean(studio.canUndoSettings),
      coverage: {
        available: Boolean(coverage.available),
        coveragePercent: clamp(finiteNumber(coverage.coveragePercent, 0), 0, 100),
        enabledSamples: nonNegativeInteger(coverage.enabledSamples),
        totalSamples: nonNegativeInteger(coverage.totalSamples),
        plannedStrokes: nonNegativeInteger(coverage.plannedStrokes),
        detailSelected: nonNegativeInteger(coverage.detailSelected),
        detailBudget: nonNegativeInteger(coverage.detailBudget),
        result: safeText(coverage.result, 200)
      }
    }
  };
}

function renderLogs(runtime) {
  const logs = runtime.logs || "";
  const value = logs.trim().length > 0 ? logs : "";
  if (activeLogFilter === "all") {
    const progressLine = buildProgressLine(runtime);
    setLogHtml([value, progressLine].filter(Boolean).join("\n"));
    return;
  }
  const token = `[${activeLogFilter.toUpperCase()}]`;
  const filtered = value
    .split(/\r?\n/)
    .filter(line => line.toUpperCase().includes(token))
    .join("\n");
  setLogHtml(filtered);
}

function buildProgressLine(runtime) {
  if (!runtime.progressVisible) {
    return "";
  }
  const percent = Math.max(0, Math.min(100, Math.round(runtime.progressPercent)));
  const passStage = runtime.paintProgressSource === "receiver_queue_drain"
    ? "painting"
    : runtime.paintProgressSource === "submission"
      ? "queueing"
      : "";
  const pass = [passStage, runtime.paintPass, runtime.paintPassProgress]
    .filter(value => value && value !== "-")
    .join(" ");
  const detail = [
    `pass ${pass || "-"}`,
    `pass ETA ${runtime.paintPassEta || "-"}`,
    `total ETA ${runtime.paintEta || "-"}`,
    `batch ${runtime.batch || "-"}`,
    `pacing ${runtime.pacing || "-"}`,
    `queue ${runtime.queue || "-"}`,
    `elapsed ${runtime.paintElapsed || "-"}`
  ].join(" | ");
  return `${logPrefix("INFO")} Paint: overall ${percent}% ${progressBar(percent)} | ${detail}`;
}

function progressBar(percent) {
  const width = 16;
  const filled = Math.max(0, Math.min(width, Math.round((percent / 100) * width)));
  return `[${"#".repeat(filled)}${"-".repeat(width - filled)}]`;
}

function logPrefix(level) {
  const now = new Date();
  const part = value => String(value).padStart(2, "0");
  return `${part(now.getHours())}:${part(now.getMinutes())}:${part(now.getSeconds())} [${level}]`;
}

function setLogHtml(value) {
  const logs = byId("logs");
  if (value === lastRenderedLogValue) {
    return;
  }
  const stickToBottom = lastRenderedLogValue === null || (logs.scrollHeight - logs.scrollTop - logs.clientHeight) < 24;
  lastRenderedLogValue = value;
  const lines = String(value).split(/\r?\n/);
  if (lines[lines.length - 1].length > 0) {
    lines.push("");
  }
  logs.innerHTML = lines
    .map(line => `<span class="${logLineClass(line)}">&gt; ${escapeHtml(line)}</span>`)
    .join("\n");
  if (stickToBottom) {
    requestAnimationFrame(() => {
      logs.scrollTop = logs.scrollHeight;
    });
  }
}

function logLineClass(line) {
  const upper = line.toUpperCase();
  if ((upper.startsWith("PAINT: ") || /\[INFO\]\s+PAINT:\s+\d+%/.test(upper)) && upper.includes("% [")) {
    return "log-line progress";
  }
  if (upper.includes("[ERROR]")) {
    return "log-line error";
  }
  if (upper.includes("[WARN]")) {
    return "log-line warn";
  }
  return "log-line";
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function setStatus(id, value) {
  const element = byId(id);
  element.textContent = localizedStatus(value);
  element.className = `status-token ${statusClass(value)}`;
}

function localizedStatus(value) {
  const normalized = String(value || "").toLowerCase();
  return i18n(`state.${normalized}`);
}

function statusClass(value) {
  const normalized = String(value || "").toLowerCase();
  if (["attached", "connected", "ready", "running", "complete", "ok", "enabled"].includes(normalized)) {
    return "ok";
  }
  if (["waiting", "starting", "pending", "busy"].includes(normalized)) {
    return "wait";
  }
  if (["failed", "error", "cancelled"].includes(normalized)) {
    return "bad";
  }
  return "idle";
}

function renderSettings(snapshot) {
  const paint = snapshot.settings.paint;
  setNumberPair("brush-1-size", "brush-1-size-number", paint.brush1SizeTexels);
  setNumberPair("brush-2-size", "brush-2-size-number", paint.brush2SizeTexels);
  const detailResolution = clamp(
    finiteNumber(paint.detailResolutionPercent, snapshot.paintStudio?.detailResolutionPercent ?? 500),
    50,
    500
  );
  setNumberPair("auto-detail-resolution", "auto-detail-resolution-number", detailResolution);
  setNumberPair("detail-resolution", "detail-resolution-number", detailResolution);
  setNumberPair("packed-batch-limit", "packed-batch-limit-number", paint.packedBatchLimit);
  setNumberPair("packed-batch-pacing", "packed-batch-pacing-number", paint.packedBatchPacingMs);
  setChecked("auto-material", paint.autoMaterial);
  setNumberPair("metallic", "metallic-number", paint.metallic);
  setNumberPair("roughness", "roughness-number", paint.roughness);
  renderRegionButtons(document.querySelector('[data-region="paint.frontRegionMode"]'), "paint.frontRegionMode", paint.frontRegionMode);
  renderRegionButtons(document.querySelector('[data-region="paint.sideRegionMode"]'), "paint.sideRegionMode", paint.sideRegionMode);
  renderRegionButtons(document.querySelector('[data-region="paint.backRegionMode"]'), "paint.backRegionMode", paint.backRegionMode);
  setColor(paint.fillColor);
  setNumberPair("fill-metallic", "fill-metallic-number", paint.fillMetallic);
  setNumberPair("fill-roughness", "fill-roughness-number", paint.fillRoughness);

  const app = snapshot.settings.app;
  applyThemeColor(app.themeColor);
  setChecked("always-on-top", app.alwaysOnTop);
  setNumberPair("opacity", "opacity-number", Math.round(app.opacity * 100));
  setColorPair("theme-color-picker", "theme-color", app.themeColor);
  setValue("start-hotkey", app.startHotkey);
  setValue("preview-hotkey", app.previewHotkey);
  setValue("unpreview-hotkey", app.unPreviewHotkey);
  setValue("stop-hotkey", app.stopHotkey);

  const language = byId("language");
  if (language.options.length === 0) {
    for (const locale of liveSnapshot.locales) {
      const option = document.createElement("option");
      option.value = locale.code;
      option.textContent = locale.nativeName;
      language.append(option);
    }
  }
  setValue("language", snapshot.language);

  for (const control of document.querySelectorAll(".setting-control")) {
    setControlDisabled(control, !editing);
  }
  for (const button of document.querySelectorAll(".record-hotkey")) {
    button.disabled = !editing;
  }

  const materialLocked = paint.autoMaterial || !editing;
  setDisabled(["metallic", "metallic-number", "roughness", "roughness-number"], materialLocked);

  setDisabled([
    "packed-batch-limit",
    "packed-batch-limit-number",
    "packed-batch-pacing",
    "packed-batch-pacing-number"
  ], !editing);

  const fillLocked = !editing || !usesFill(paint);
  byId("fill-section").classList.toggle("disabled", !usesFill(paint));
  setDisabled([
    "fill-color-picker",
    "fill-color",
    "fill-metallic",
    "fill-metallic-number",
    "fill-roughness",
    "fill-roughness-number"
  ], fillLocked);
}

function setNumberPair(sliderId, numberId, value) {
  setValue(sliderId, value);
  setValue(numberId, fmt(value));
}

function setColor(value) {
  setColorPair("fill-color-picker", "fill-color", value);
}

function setColorPair(pickerId, inputId, value) {
  const color = normalizeColor(value) || "#FFFFFF";
  setValue(pickerId, color);
  setValue(inputId, color);
}

function applyThemeColor(value) {
  const color = normalizeColor(value) || "#FFFFFF";
  document.documentElement.style.setProperty("--primary", color);
}

function setDisabled(ids, disabled) {
  for (const id of ids) {
    setControlDisabled(byId(id), disabled);
  }
}

function isThemeVisibleReadOnlyControl(control) {
  return control instanceof HTMLInputElement &&
    (control.type === "range" || control.type === "checkbox");
}

function setControlDisabled(control, disabled) {
  const themeVisibleReadonly = disabled && isThemeVisibleReadOnlyControl(control);
  if (themeVisibleReadonly && document.activeElement === control) {
    control.blur();
  }
  control.disabled = disabled && !themeVisibleReadonly;
  control.classList.toggle("theme-visible-readonly", themeVisibleReadonly);
  if (themeVisibleReadonly) {
    control.setAttribute("aria-disabled", "true");
    control.tabIndex = -1;
  } else {
    control.removeAttribute("aria-disabled");
    control.removeAttribute("tabindex");
  }
}

function renderRegionButtons(container, key, current) {
  container.innerHTML = "";
  for (const mode of ["paint", "fill", "skip"]) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = i18n(`mode.${mode}`);
    button.className = mode === current ? "active" : "";
    button.disabled = !editing;
    button.addEventListener("click", () => {
      if (!editing) {
        return;
      }
      setDraftSetting(key, mode);
      renderSettings(draftSnapshot);
    });
    container.append(button);
  }
}

function renderEditState() {
  document.body.classList.toggle("editing", editing);
  byId("edit-settings").disabled = editing;
  byId("save-settings").disabled = !editing;
  byId("cancel-edit").disabled = !editing;
  byId("reset-settings").disabled = !editing;
}

function usesFill(paint) {
  return paint.frontRegionMode === "fill" || paint.sideRegionMode === "fill" || paint.backRegionMode === "fill";
}

function beginEdit() {
  if (!liveSnapshot) {
    return;
  }
  editing = true;
  draftSnapshot = clone(liveSnapshot);
  send("setEditing", { editing: true }).catch(error => showError(error.message || String(error)));
  render();
}

function cancelEdit() {
  editing = false;
  draftSnapshot = null;
  closeHotkeyDialog();
  send("setEditing", { editing: false }).catch(error => showError(error.message || String(error)));
  previewSavedWindow();
  render();
}

function resetDraft() {
  if (!editing || !liveSnapshot || !draftSnapshot) {
    return;
  }
  const currentProcessName = liveSnapshot.settings.app.processName;
  draftSnapshot.settings = clone(liveSnapshot.defaults);
  draftSnapshot.settings.app.processName = currentProcessName;
  draftSnapshot.language = liveSnapshot.language;
  render();
  previewDraftWindow();
}

async function saveDraft() {
  if (!editing || !liveSnapshot || !draftSnapshot) {
    return;
  }
  const changes = diffSnapshots(liveSnapshot, draftSnapshot);
  if (changes.length === 0) {
    cancelEdit();
    return;
  }
  const result = await send("updateSettings", { changes });
  if (!result.success) {
    showError(result.message || i18n("error.settings.not.saved"));
    document.activeElement?.blur();
    draftSnapshot = clone(liveSnapshot);
    previewSavedWindow();
    render();
    return;
  }
  editing = false;
  draftSnapshot = null;
  closeHotkeyDialog();
  await send("setEditing", { editing: false });
  toast(i18n("toast.settings.saved"));
  refresh().catch(error => showError(error.message || String(error)));
}

function previewSavedWindow() {
  if (!liveSnapshot) {
    return;
  }
  send("previewWindow", { opacity: liveSnapshot.settings.app.opacity }).catch(error => showError(error.message || String(error)));
  applyThemeColor(liveSnapshot.settings.app.themeColor);
}

function previewDraftWindow() {
  if (!draftSnapshot) {
    return;
  }
  send("previewWindow", { opacity: draftSnapshot.settings.app.opacity }).catch(error => showError(error.message || String(error)));
  applyThemeColor(draftSnapshot.settings.app.themeColor);
}

async function refresh() {
  liveSnapshot = await send("getSnapshot");
  render();
}

function setDraftSetting(key, value) {
  if (!draftSnapshot) {
    return;
  }
  if (key === "app.language") {
    draftSnapshot.language = value;
    return;
  }
  const path = snapshotPath(key);
  let node = draftSnapshot.settings;
  for (let index = 0; index < path.length - 1; ++index) {
    node = node[path[index]];
  }
  node[path.at(-1)] = value;
}

function canEditControl(control = null) {
  if (editing && control?.getAttribute("aria-disabled") !== "true" && !control?.disabled) {
    return true;
  }
  const snapshot = currentSnapshot();
  if (snapshot) {
    // Ranges and checkboxes stay paint-enabled solely so Chromium retains the
    // theme accent. Restore a keyboard/label-driven attempted change at once.
    renderSettings(snapshot);
  }
  return false;
}

function getSnapshotSetting(snapshot, key) {
  if (key === "app.language") {
    return snapshot.language;
  }
  const path = snapshotPath(key);
  let node = snapshot.settings;
  for (const part of path) {
    node = node[part];
  }
  return node;
}

function snapshotPath(key) {
  if (key === "app.unpreviewHotkey") {
    return ["app", "unPreviewHotkey"];
  }
  return key.split(".");
}

function diffSnapshots(before, after) {
  const keys = [
    "app.language",
    "paint.brush1SizeTexels",
    "paint.brush2SizeTexels",
    "paint.detailResolutionPercent",
    "paint.packedBatchLimit",
    "paint.packedBatchPacingMs",
    "paint.autoMaterial",
    "paint.metallic",
    "paint.roughness",
    "paint.frontRegionMode",
    "paint.sideRegionMode",
    "paint.backRegionMode",
    "paint.fillColor",
    "paint.fillMetallic",
    "paint.fillRoughness",
    "app.alwaysOnTop",
    "app.opacity",
    "app.themeColor",
    "app.startHotkey",
    "app.previewHotkey",
    "app.unpreviewHotkey",
    "app.stopHotkey"
  ];
  const changes = [];
  for (const key of keys) {
    const oldValue = getSnapshotSetting(before, key);
    const newValue = getSnapshotSetting(after, key);
    if (oldValue !== newValue) {
      changes.push({ key, value: newValue });
    }
  }
  return changes;
}

function normalizeColor(value) {
  const textValue = String(value || "").trim();
  const match = /^#?[0-9a-fA-F]{6}$/.exec(textValue);
  if (!match) {
    return null;
  }
  return ("#" + textValue.replace("#", "")).toUpperCase();
}

function bindRangePair(sliderId, numberId, key, transform = Number) {
  bindRangePairs([[sliderId, numberId]], key, transform);
}

function bindRangePairs(pairIds, key, transform = Number) {
  const pairs = pairIds.map(([sliderId, numberId]) => ({
    slider: byId(sliderId),
    number: byId(numberId)
  }));
  const commit = source => {
    if (!canEditControl(source)) {
      return;
    }
    const raw = Number(source.value);
    if (!Number.isFinite(raw)) {
      return;
    }
    const minimum = Number(source.min);
    const maximum = Number(source.max);
    const step = Number(source.step);
    const clamped = clamp(raw, minimum, maximum);
    const stepped = Number.isFinite(step) && step > 0
      ? minimum + Math.round((clamped - minimum) / step) * step
      : clamped;
    const normalized = clamp(stepped, minimum, maximum);
    for (const pair of pairs) {
      pair.slider.value = String(normalized);
      pair.number.value = fmt(normalized);
    }
    setDraftSetting(key, transform(normalized));
    if (key === "app.opacity") {
      send("previewWindow", { opacity: transform(normalized) }).catch(error => showError(error.message || String(error)));
    }
  };
  for (const pair of pairs) {
    pair.slider.addEventListener("input", () => commit(pair.slider));
    pair.number.addEventListener("change", () => commit(pair.number));
    pair.number.addEventListener("keydown", event => {
      if (event.key === "Enter") {
        pair.number.blur();
      }
    });
  }
}

function bindInput(id, key, transform = value => value) {
  const element = byId(id);
  element.addEventListener("change", () => {
    if (!canEditControl(element)) {
      return;
    }
    setDraftSetting(key, transform(element.value));
  });
  element.addEventListener("keydown", event => {
    if (event.key === "Enter") {
      element.blur();
    }
  });
}

function bindCheckbox(id, key) {
  byId(id).addEventListener("change", event => {
    if (!canEditControl(event.target)) {
      return;
    }
    setDraftSetting(key, event.target.checked);
    renderSettings(draftSnapshot);
  });
}

function bindColorPair(pickerId, inputId, key) {
  const picker = byId(pickerId);
  const textInput = byId(inputId);
  picker.addEventListener("input", () => {
    if (!canEditControl(picker)) {
      return;
    }
    const color = normalizeColor(picker.value);
    if (!color) {
      return;
    }
    textInput.value = color;
    setDraftSetting(key, color);
    if (key === "app.themeColor") {
      applyThemeColor(color);
    }
  });
  textInput.addEventListener("change", () => {
    if (!canEditControl(textInput)) {
      return;
    }
    const color = normalizeColor(textInput.value);
    if (!color) {
      setDraftSetting(key, textInput.value);
      return;
    }
    picker.value = color;
    textInput.value = color;
    setDraftSetting(key, color);
    if (key === "app.themeColor") {
      applyThemeColor(color);
    }
  });
  textInput.addEventListener("keydown", event => {
    if (event.key === "Enter") {
      textInput.blur();
    }
  });
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function beginHotkeyRecord(key, inputId) {
  if (!editing) {
    return;
  }
  recordingHotkey = { key, inputId };
  send("setHotkeyRecording", { recording: true }).catch(error => showError(error.message || String(error)));
  setHotkeyDialogMessage(i18n("dialog.hotkey.supported"), false);
  byId("hotkey-dialog").hidden = false;
}

function closeHotkeyDialog() {
  recordingHotkey = null;
  send("setHotkeyRecording", { recording: false }).catch(error => showError(error.message || String(error)));
  byId("hotkey-dialog").hidden = true;
}

function recordHotkeyFromEvent(event) {
  if (!recordingHotkey) {
    return;
  }
  event.preventDefault();
  if (event.key === "Escape" || event.key === "Esc") {
    closeHotkeyDialog();
    return;
  }
  const key = event.key.toUpperCase();
  if (!/^F([1-9]|1[0-9]|2[0-4])$/.test(key)) {
    toast(i18n("toast.hotkey.unsupported"), "error");
    return;
  }
  if (isDuplicateHotkey(key, recordingHotkey.key)) {
    toast(i18n("toast.hotkey.duplicate", key), "error");
    return;
  }
  setDraftSetting(recordingHotkey.key, key);
  setValue(recordingHotkey.inputId, key);
  closeHotkeyDialog();
}

function isDuplicateHotkey(value, ownKey) {
  return hotkeyKeys.some(key => key !== ownKey && getSnapshotSetting(draftSnapshot, key).toUpperCase() === value);
}

function setHotkeyDialogMessage(message, error) {
  const dialog = byId("hotkey-dialog");
  dialog.classList.toggle("error", error);
  byId("hotkey-dialog-message").textContent = message;
}

async function runPaintAction(command) {
  const stopping = command === "stop";
  if ((stopping && stopCommandPending) || (!stopping && paintCommandPending)) {
    return;
  }
  if (stopping) {
    stopCommandPending = true;
  } else {
    paintCommandPending = true;
  }
  render();
  try {
    const result = await send(command);
    showCommandResult(result);
  } catch (error) {
    showError(error?.message || String(error));
  } finally {
    if (stopping) {
      stopCommandPending = false;
    } else {
      paintCommandPending = false;
    }
    render();
    refresh().catch(error => showError(error?.message || String(error)));
  }
}

async function runPaintStudioCommand(command, payload = {}) {
  if (paintStudioCommandPending.length > 0) {
    return false;
  }
  paintStudioCommandPending = command;
  render();
  let succeeded = false;
  try {
    const result = await send(command, payload);
    succeeded = Boolean(result?.success);
    showCommandResult(result);
  } catch (error) {
    showError(error?.message || String(error));
  } finally {
    paintStudioCommandPending = "";
    render();
    refresh().catch(error => showError(error?.message || String(error)));
  }
  return succeeded;
}

async function savePaintPreset() {
  const input = byId("paint-preset-name");
  const name = input.value.trim();
  if (name.length === 0) {
    showError(i18n("error.preset.name.required"));
    return;
  }
  if (await runPaintStudioCommand("savePaintPreset", { name })) {
    input.value = "";
  }
}

function selectedPaintPreset() {
  return byId("paint-preset-select").value;
}

function applyPaintPreset() {
  const name = selectedPaintPreset();
  if (name.length === 0) {
    showError(i18n("error.preset.selection.required"));
    return;
  }
  runPaintStudioCommand("applyPaintPreset", { name });
}

function deletePaintPreset() {
  const name = selectedPaintPreset();
  if (name.length === 0) {
    showError(i18n("error.preset.selection.required"));
    return;
  }
  runPaintStudioCommand("deletePaintPreset", { name });
}

async function runModuleManagerCommand(command) {
  if (moduleManagerCommandPending) {
    return;
  }
  moduleManagerCommandPending = true;
  renderExternalModuleDiagnostics();
  try {
    const result = await send(command);
    showCommandResult(result);
  } catch (error) {
    showError(error?.message || String(error));
  } finally {
    moduleManagerCommandPending = false;
    renderExternalModuleDiagnostics();
    refresh().catch(error => showError(error?.message || String(error)));
  }
}

function showCommandResult(result) {
  if (!result?.success) {
    showError(result?.message || i18n("error.command.failed"));
    return;
  }
  if (result.message) {
    toast(result.message);
  }
}

function showError(message) {
  console.error(message);
  toast(message, "error");
}

function toast(message, level = "success") {
  const toastElement = byId("toast");
  toastElement.textContent = message;
  toastElement.className = `visible ${level}`;
  clearTimeout(toastElement._timer);
  toastElement._timer = setTimeout(() => {
    toastElement.className = "";
  }, 2400);
}

document.addEventListener("DOMContentLoaded", () => {
  for (const tab of document.querySelectorAll("[data-module-target]")) {
    tab.addEventListener("click", () => selectModule(tab.dataset.moduleTarget));
  }
  byId("paint-start").addEventListener("click", () => runPaintAction("paint"));
  byId("paint-preview").addEventListener("click", () => runPaintAction("preview"));
  byId("paint-unpreview").addEventListener("click", () => runPaintAction("unpreview"));
  byId("paint-stop").addEventListener("click", () => runPaintAction("stop"));
  byId("paint-studio-preview").addEventListener("click", () => runPaintAction("preview"));
  byId("paint-studio-restore").addEventListener("click", () => runPaintAction("unpreview"));
  byId("paint-preset-save").addEventListener("click", savePaintPreset);
  byId("paint-preset-apply").addEventListener("click", applyPaintPreset);
  byId("paint-preset-delete").addEventListener("click", deletePaintPreset);
  byId("paint-settings-undo").addEventListener("click", () => runPaintStudioCommand("undoPaintSettings"));
  byId("paint-preset-select").addEventListener("change", render);
  byId("paint-preset-name").addEventListener("keydown", event => {
    if (event.key === "Enter") {
      event.preventDefault();
      savePaintPreset();
    }
  });
  byId("open-modules").addEventListener("click", () => runModuleManagerCommand("openModules"));
  byId("reload-modules").addEventListener("click", () => runModuleManagerCommand("reloadModules"));
  window.addEventListener("message", handleExternalModuleMessage);
  bindRangePair("brush-1-size", "brush-1-size-number", "paint.brush1SizeTexels");
  bindRangePair("brush-2-size", "brush-2-size-number", "paint.brush2SizeTexels");
  bindRangePair("packed-batch-limit", "packed-batch-limit-number", "paint.packedBatchLimit");
  bindRangePair("packed-batch-pacing", "packed-batch-pacing-number", "paint.packedBatchPacingMs");
  bindRangePairs([
    ["auto-detail-resolution", "auto-detail-resolution-number"],
    ["detail-resolution", "detail-resolution-number"]
  ], "paint.detailResolutionPercent", value => Math.round(value));
  bindCheckbox("auto-material", "paint.autoMaterial");
  bindRangePair("metallic", "metallic-number", "paint.metallic");
  bindRangePair("roughness", "roughness-number", "paint.roughness");
  bindColorPair("fill-color-picker", "fill-color", "paint.fillColor");
  bindRangePair("fill-metallic", "fill-metallic-number", "paint.fillMetallic");
  bindRangePair("fill-roughness", "fill-roughness-number", "paint.fillRoughness");
  bindCheckbox("always-on-top", "app.alwaysOnTop");
  bindRangePair("opacity", "opacity-number", "app.opacity", value => value / 100);
  bindColorPair("theme-color-picker", "theme-color", "app.themeColor");
  const languageSelect = byId("language");
  const languageWrap = languageSelect.closest(".select-wrap");
  languageSelect.addEventListener("pointerdown", () => languageWrap?.classList.add("open"));
  languageSelect.addEventListener("keydown", event => {
    if (["ArrowDown", "ArrowUp", "Enter", " "].includes(event.key)) {
      languageWrap?.classList.add("open");
    }
  });
  languageSelect.addEventListener("blur", () => languageWrap?.classList.remove("open"));
  languageSelect.addEventListener("change", event => {
    languageWrap?.classList.remove("open");
    if (!canEditControl(languageSelect)) {
      return;
    }
    setDraftSetting("app.language", event.target.value);
    render();
  });
  byId("edit-settings").addEventListener("click", beginEdit);
  byId("cancel-edit").addEventListener("click", cancelEdit);
  byId("reset-settings").addEventListener("click", resetDraft);
  byId("save-settings").addEventListener("click", () => saveDraft().catch(error => showError(error.message || String(error))));
  byId("open-logs").addEventListener("click", () => send("openLogs").catch(error => showError(error.message || String(error))));
  byId("copy-logs").addEventListener("click", async () => {
    try {
      await send("copyLogs");
      toast(i18n("toast.log.copied"));
    } catch (error) {
      showError(error.message || String(error));
    }
  });
  for (const button of document.querySelectorAll(".record-hotkey")) {
    button.addEventListener("click", () => beginHotkeyRecord(button.dataset.hotkeyKey, button.dataset.hotkeyInput));
  }
  for (const button of document.querySelectorAll(".tab")) {
    button.addEventListener("click", () => {
      activeLogFilter = button.dataset.logFilter;
      for (const tab of document.querySelectorAll(".tab")) {
        tab.classList.toggle("active", tab === button);
      }
      renderLogs(liveSnapshot?.runtime || { logs: "" });
    });
  }
  document.addEventListener("keydown", recordHotkeyFromEvent);
  window.chrome.webview.postMessage({ type: "uiReady" });
  refresh().catch(error => showError(error.message || String(error)));
});
