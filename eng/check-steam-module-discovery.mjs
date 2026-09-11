import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
// Keep the desktop recovery launch wired to the configured switch before process creation.
// The toolkit tests the flag writer against temporary directories; never launch live Steam here.
const sessionModes = readFileSync(resolve(root, "src/WSGM/Shell/SessionModes.cs"), "utf8");
const desktopStart = sessionModes.match(
  /private void StartSteamDesktop\(\)([\s\S]*?)\n    \/\/\//u,
)[1];
assert.match(
  desktopStart,
  /SteamCdp\.EnsureRemoteDebuggingEnabled\(_config\.Cef\.Enabled\);\s*Log\.Info\([^;]+;\s*AppLauncher\.Start\(exe,/u,
);
const resolver = readFileSync(
  resolve(
    root,
    "external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/module-resolver.ts",
  ),
  "utf8",
);
const resident = (file) =>
  readFileSync(resolve(root, "src/WSGM/Core", file), "utf8").match(
    /private const string ResidentSetup = """\s*([\s\S]*?)\s*""";/u,
  )[1];

function fixture() {
  const calls = [];
  const cache = {};
  const factories = {
    unrelated() {
      throw new Error("unrelated service was initialized");
    },
    react(_module, exports) {
      // react.transitional.element useState cloneElement createElement
      Object.assign(exports, {
        createElement() {},
        useMemo() {},
        version: "test",
        __CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE: { H: null },
      });
    },
    focus(_module, exports) {
      exports.Focusable = function () {
        // "flow-children" onActivate: focusClassName focusWithinClassName
      };
    },
    progress(_module, exports) {
      // k_EAppUpdateProgress_Preallocating= k_EAppUpdateProgress_Download=
      exports.progress = { k_EAppUpdateProgress_Download: 2 };
    },
    jsx(_module, exports) {
      // react.transitional.element
      exports.jsx = function () {};
      exports.jsxs = function () {};
    },
  };
  function runtime(id) {
    calls.push(id);
    if (cache[id]) return cache[id];
    const exports = (cache[id] = {});
    factories[id]({}, exports);
    return exports;
  }
  runtime.m = factories;
  const window = { webpackChunksteamui: { push: (chunk) => chunk[2](runtime) } };
  const steamModules = runInNewContext(`(${resolver})("fixture")`, { window });
  return { window, factories, calls, cache, steamModules };
}

const sort = resident("SteamDownloadSort.cs");
const tabs = resident("SteamLibraryTabs.cs");
// The bridge's "elements" gate, standing in for the toolkit's shared JSX-runtime claim.
const withElementsGate = (f) => {
  const registered = new Map();
  f.bridgeNamespace = "__bridge_fixture";
  f.window.__bridge_fixture = {
    gate: (name) =>
      name === "elements"
        ? {
            register: (id, transform) => {
              registered.set(id, transform);
              return { ok: true };
            },
            unregister: (id) => {
              registered.delete(id);
              return { ok: true };
            },
            registered: (id) => registered.has(id),
          }
        : null,
  };
  return registered;
};
{
  const f = fixture();
  const registered = withElementsGate(f);
  runInNewContext(sort, f);
  const w = f.window.__wsgm;
  assert.equal(JSON.parse(w.dlSortInstall()).ok, true);
  assert.deepEqual(f.calls, ["react", "focus", "progress", "jsx"]);
  const jsx = f.cache.jsx;
  assert.equal(jsx.jsx.__wsgmDlOrig, undefined, "download sort must not wrap the runtime itself");
  const transform = registered.get("wsgm.download-sort");
  assert.equal(typeof transform, "function", "the header transform must be registered");
  const created = [];
  const create = (type, props, key) => {
    created.push({ type, props, key });
    return { type, props };
  };
  assert.equal(
    transform(create, "section", { sectionTitle: "#Other", count: 1, labelId: "x" }, null),
    undefined,
  );
  assert.equal(created.length, 0, "other elements must be left to the runtime");
  transform(
    create,
    "section",
    { sectionTitle: "#Downloads_Section_Current", count: 2, labelId: "q" },
    "k",
  );
  assert.equal(created.length, 1, "the queue header must be created once, through the runtime");
  assert.equal(created[0].props.style.flex, "1 1 auto");
  assert.equal(created[0].key, "k");
  w.dlSortRemove();
  assert.equal(registered.size, 0, "removal must withdraw the transform");
  assert.equal(JSON.parse(w.dlSortInstall()).ok, true);
  assert.equal(registered.size, 1);
}
{
  // A version 2 wrapper left on the runtime by an older build is unwound, and nothing is wrapped.
  const f = fixture();
  withElementsGate(f);
  const exports = f.steamModules.resolve(["react.transitional.element", ".jsx", ".jsxs"]);
  const original = exports.jsx;
  const legacy = function () {};
  legacy.__wsgmDlOrig = original;
  exports.jsx = legacy;
  f.calls.length = 0;
  runInNewContext(sort, f);
  assert.equal(JSON.parse(f.window.__wsgm.dlSortInstall()).ok, true);
  assert.equal(exports.jsx, original, "the legacy wrapper must be unwound");
}
{
  // Without the bridge there is nothing to register with, and nothing is wrapped as a fallback.
  const f = fixture();
  runInNewContext(sort, { ...f, bridgeNamespace: "__absent" });
  const result = JSON.parse(f.window.__wsgm.dlSortInstall());
  assert.equal(result.ok, false);
  assert.equal(result.err, "bridge unavailable");
  assert.equal(f.cache.jsx?.jsx.__wsgmDlOrig, undefined);
}
for (const source of [sort, tabs]) {
  for (const state of ["missing", "ambiguous"]) {
    const f = fixture();
    if (state === "missing") delete f.factories.react;
    else f.factories.duplicateReact = f.factories.react;
    const act = () => {
      runInNewContext(source, f);
      if (source === sort) f.window.__wsgm.dlSortInstall();
    };
    assert.throws(act, /absent|ambiguous/u);
    assert.deepEqual(f.calls, []);
  }
}
{
  const f = fixture();
  runInNewContext(tabs, f);
  assert.equal(f.window.__wsgm.tabsInstalled, true);
  assert.deepEqual(f.calls, ["react"]);
  f.window.__wsgm.suspendTabs();
  assert.equal(f.window.__wsgm.tabsInstalled, false);
}
console.log(
  "Steam module discovery: unrelated factories stay untouched; missing and ambiguous matches refuse; hooks restore.",
);
