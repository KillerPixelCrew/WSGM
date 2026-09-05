import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runInNewContext } from "node:vm";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
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
{
  const f = fixture();
  runInNewContext(sort, f);
  const w = f.window.__wsgm;
  assert.equal(JSON.parse(w.dlSortInstall()).ok, true);
  assert.deepEqual(f.calls, ["react", "focus", "progress", "jsx"]);
  const jsx = f.cache.jsx;
  const original = jsx.jsx.__wsgmDlOrig;
  assert.equal(typeof original, "function");
  w.dlSortRemove();
  assert.equal(jsx.jsx, original);
  assert.equal(JSON.parse(w.dlSortInstall()).ok, true);
  assert.equal(jsx.jsx.__wsgmDlOrig, original);
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
  const factory = f.factories.jsx;
  f.factories.jsx = function (_module, exports) {
    // react.transitional.element .jsx .jsxs
    factory(_module, exports);
    Object.defineProperty(exports, "jsxs", { writable: false });
  };
  runInNewContext(sort, f);
  assert.throws(() => f.window.__wsgm.dlSortInstall(), /not writable/u);
  assert.equal(f.cache.jsx.jsx.__wsgmDlOrig, undefined);
  assert.equal(f.window.__wsgm.dlSortPatched, null);
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
