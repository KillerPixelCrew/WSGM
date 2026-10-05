import assert from "node:assert/strict";
import {
  fragment,
  gateSource,
  instantiate,
  sharedFragments,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
// This checks the shipped JavaScript only. The order of a cold Steam start (shim reconcile, CEF
// flag, process start) is C# behaviour and belongs in WSGM.Tests, not in a regex over source text.
// Never launch live Steam here.
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
        useMemo(factory) {
          return factory();
        },
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

const tabs = resident("SteamLibraryTabs.cs").replaceAll(
  "__WSGM_BRIDGE_NAMESPACE__",
  JSON.stringify("__bridge_fixture"),
);
const composedAsset = readFileSync(
  resolve(root, "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
  "utf8",
);
// WSGM's whole library-tabs fragment over the toolkit's shared fragments it builds on.
const libraryClaim = fragment(composedAsset, "consumer/library-tabs.ts");
const withLibraryGate = (f) => {
  const api = new Function(
    "registerGate",
    `${sharedFragments(composedAsset)}\n${libraryClaim}\nreturn { gate: libraryTabsClaim, interceptMemo, releaseMemo };`,
  )(() => {});
  f.window.__bridge_fixture = { gate: (name) => (name === "wsgmLibraryTabs" ? api.gate : null) };
  return api;
};
// WSGM's download-sort gate, whole and without its registration, over the shared fragments it
// builds on. The bridge's own request and module runtime are stand-ins.
const downloadSort = gateSource(composedAsset, "consumer/download-sort.ts");
const sortGate = (f, request = () => Promise.resolve(null)) =>
  instantiate(
    {
      window: f.window,
      getWebpackRuntime: () => f.steamModules,
      request,
      setTimeout: f.setTimeout ?? setTimeout,
    },
    `${sharedFragments(composedAsset)}\n${downloadSort}`,
    "createWsgmDownloadSort()",
  );
const JsxTokens = ["react.transitional.element", ".jsx", ".jsxs"];
{
  const f = fixture();
  // The runtime's own jsx, recording what reaches it, so the claim's wrapper can be seen to pass
  // every other element through and to hand the original back on removal.
  const runtime = f.steamModules.resolve(JsxTokens);
  const created = [];
  const original = (type, props, key) => {
    created.push({ type, props, key });
    return { type, props };
  };
  runtime.jsx = runtime.jsxs = original;
  f.calls.length = 0;
  const gate = sortGate(f);
  assert.equal(gate.install().ok, true);
  assert.deepEqual(f.calls, ["react", "focus", "progress", "jsx"]);
  assert.notEqual(runtime.jsx, original, "the header transform must claim the runtime");
  assert.equal(gate.status().registered, true);
  const other = { sectionTitle: "#Other", count: 1, labelId: "x" };
  runtime.jsx("section", other, null);
  assert.equal(created.length, 1);
  assert.equal(created[0].props, other, "other elements must be left to the runtime");
  runtime.jsx(
    "section",
    { sectionTitle: "#Downloads_Section_Current", count: 2, labelId: "q" },
    "k",
  );
  assert.equal(created.length, 2, "the queue header must be created once, through the runtime");
  assert.equal(created[1].props.style.flex, "1 1 auto");
  assert.equal(created[1].key, "k");
  assert.equal(gate.remove().ok, true);
  assert.equal(runtime.jsx, original, "removal must hand the runtime back");
  assert.equal(gate.status().registered, false);
  assert.equal(gate.install().ok, true);
  assert.equal(gate.status().registered, true);
}
{
  // Without the JSX runtime there is nothing to register on, and nothing is wrapped as a fallback.
  const f = fixture();
  delete f.factories.jsx;
  const gate = sortGate(f);
  const result = gate.install();
  assert.equal(result.ok, false);
  assert.match(result.error, /absent/u);
  assert.equal(gate.status().installed, false);
}
{
  // A run Steam partly refuses tells WSGM how many through the bridge, once, when it ends.
  const f = fixture();
  const requests = [];
  const request = (patchId, command, payload) => {
    requests.push(JSON.parse(JSON.stringify({ patchId, command, payload })));
    return Promise.resolve(null);
  };
  f.setTimeout = (step) => step();
  f.window.downloadsStore = {
    QueuedTransfers: [
      { appid: 1, queue_index: 0 },
      { appid: 2, queue_index: 1 },
      { appid: 3, queue_index: 2 },
    ],
    CurrentViewingRemoteClientID: 0,
  };
  f.window.SteamClient = {
    Downloads: {
      SetQueueIndex(appid) {
        if (appid !== 1) throw new Error(`index refused for ${appid}`);
      },
    },
  };
  const gate = sortGate(f, request);
  assert.equal(gate.install().ok, true);
  gate.applySort("name");
  assert.deepEqual(requests, [
    {
      patchId: "wsgm.download-sort",
      command: "refused",
      payload: { refused: 2, total: 3, first: "index refused for 2" },
    },
  ]);
  assert.equal(gate.status().sorting, false);
}
for (const state of ["missing", "ambiguous"]) {
  const f = fixture();
  if (state === "missing") delete f.factories.react;
  else f.factories.duplicateReact = f.factories.react;
  const result = sortGate(f).install();
  assert.equal(result.ok, false);
  assert.match(result.error, /absent|ambiguous/u);
  assert.deepEqual(f.calls, []);
}
for (const state of ["missing", "ambiguous"]) {
  const f = fixture();
  if (state === "missing") delete f.factories.react;
  else f.factories.duplicateReact = f.factories.react;
  assert.throws(() => runInNewContext(tabs, f), /absent|ambiguous/u);
  assert.deepEqual(f.calls, []);
}
{
  const f = fixture();
  const api = withLibraryGate(f);
  const react = f.steamModules.resolve([
    "react.transitional.element",
    "useState",
    "cloneElement",
    "createElement",
  ]);
  const original = react.useMemo;
  api.interceptMemo(react, "another-consumer", (value) => value);
  f.calls.length = 0;
  runInNewContext(tabs, f);
  assert.equal(f.window.__wsgm.tabsInstalled, true);
  assert.deepEqual(f.calls, ["react"]);
  f.window.__wsgm.suspendTabs();
  assert.equal(f.window.__wsgm.tabsInstalled, false);
  assert.notEqual(
    react.useMemo,
    original,
    "library removal must retain the other consumer's shared claim",
  );
  api.releaseMemo(react, "another-consumer");
  assert.equal(react.useMemo, original, "the last consumer restores the native memo method");
}
console.log(
  "Steam module discovery: unrelated factories stay untouched; missing and ambiguous matches refuse; hooks restore.",
);
