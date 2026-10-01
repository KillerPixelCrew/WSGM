import assert from "node:assert/strict";
import { sharedFragments } from "../external/steam-ui-toolkit/eng/check-harness.mjs";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
// Keep every cold Steam start wired to the configured switch before process creation. Both the
// Big Picture start and the desktop session's windowed client go through one helper, so the check
// follows it there. The toolkit tests the flag writer against temporary directories; never launch
// live Steam here.
const steam = readFileSync(resolve(root, "src/WSGM/Core/Steam.cs"), "utf8");
const coldStart = steam.match(
  /private static AppLauncher\.LaunchResult ColdStart\(([\s\S]*?)\n {4}\}/u,
)[1];
assert.match(
  coldStart,
  /SteamInputShim\.Reconcile\("steam-cold-start"\);[\s\S]*?SteamCef\.EnsureRemoteDebuggingEnabled\(InstallDirectory, cefEnabled\);[\s\S]*?AppLauncher\.Start\(exe, arguments,/u,
);
// The desktop client start must reach that helper with the user's own integrity and CEF choices.
const sessionModes = readFileSync(resolve(root, "src/WSGM/Shell/SessionModes.cs"), "utf8");
const desktopStart = sessionModes.match(/public void EnsureSteamDesktop\(\)([\s\S]*?)\n {4}\}/u)[1];
assert.match(
  desktopStart,
  /Steam\.LaunchDesktop\(_config\.SteamLaunchUnelevated, _config\.Cef\.Enabled\)/u,
);
const resolver = readFileSync(
  resolve(
    root,
    "external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/module-resolver.ts",
  ),
  "utf8",
);
// SteamDownloadSort.InstallExpression declares the script version before the resident body runs.
const dlSortVersion = Number(
  readFileSync(resolve(root, "src/WSGM/Core/SteamDownloadSort.cs"), "utf8").match(
    /internal const int ScriptVersion = (\d+);/u,
  )[1],
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
  return { window, factories, calls, cache, steamModules, dlSortVersion };
}

const sort = resident("SteamDownloadSort.cs");
const tabs = resident("SteamLibraryTabs.cs").replaceAll(
  "__WSGM_BRIDGE_NAMESPACE__",
  JSON.stringify("__bridge_fixture"),
);
const composedAsset = readFileSync(
  resolve(root, "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
  "utf8",
);
const libraryClaim = composedAsset.slice(
  composedAsset.indexOf("const libraryTabsClaim ="),
  composedAsset.indexOf('registerGate("wsgmLibraryTabs", libraryTabsClaim);'),
);
const withLibraryGate = (f) => {
  const api = new Function(
    `${sharedFragments(composedAsset)}\n${libraryClaim}\nreturn { gate: libraryTabsClaim, interceptMemo, releaseMemo };`,
  )();
  f.window.__bridge_fixture = { gate: (name) => (name === "wsgmLibraryTabs" ? api.gate : null) };
  return api;
};
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
  assert.deepEqual(f.calls, ["react", "focus", "progress"]);
  assert.equal(f.cache.jsx, undefined, "download sort must not resolve the runtime itself");
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
  // Without the bridge there is nothing to register with, and nothing is wrapped as a fallback.
  const f = fixture();
  runInNewContext(sort, { ...f, bridgeNamespace: "__absent" });
  const result = JSON.parse(f.window.__wsgm.dlSortInstall());
  assert.equal(result.ok, false);
  assert.equal(result.err, "bridge unavailable");
  assert.equal(f.cache.jsx, undefined);
}
{
  // A run Steam partly refuses tells WSGM how many through the bridge, once, when it ends.
  const f = fixture();
  withElementsGate(f);
  const requests = [];
  f.window.__bridge_fixture.request = (patchId, command, payload) => {
    requests.push(JSON.parse(JSON.stringify({ patchId, command, payload })));
    return Promise.resolve(null);
  };
  f.setTimeout = (step) => step();
  f.downloadsStore = f.window.downloadsStore = {
    QueuedTransfers: [
      { appid: 1, queue_index: 0 },
      { appid: 2, queue_index: 1 },
      { appid: 3, queue_index: 2 },
    ],
    CurrentViewingRemoteClientID: 0,
  };
  f.SteamClient = {
    Downloads: {
      SetQueueIndex(appid) {
        if (appid !== 1) throw new Error(`index refused for ${appid}`);
      },
    },
  };
  runInNewContext(sort, f);
  f.window.__wsgm.applyDownloadSort("name");
  assert.deepEqual(requests, [
    {
      patchId: "wsgm.download-sort",
      command: "refused",
      payload: { refused: 2, total: 3, first: "index refused for 2" },
    },
  ]);
  assert.equal(f.window.__wsgm.dlSortState.busy, false);
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
