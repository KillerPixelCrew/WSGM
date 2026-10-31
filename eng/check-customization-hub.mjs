// Exercise the emitted Customization hub gate against an inert React fixture.
//
//   node eng/check-customization-hub.mjs [<asset>]
//
// Resolution is checked live. What this proves is what the shipped bytes do once they hold Steam's
// Settings page list: only the customization page is wrapped, the hub lands once in front of Valve's
// Startup Movie section and Valve's sections stay, the toolkit's screensaver gate still decorates
// its own section in either transform order, the hub's rows send the Themes, UI sounds and
// Animations pages' own commands, a session without one of those services shows no section for it,
// a re-transformed list never nests a second hub, and removal and reinstallation (a CEF reload)
// hand Steam's tree back and draw exactly one hub again.
import assert from "node:assert/strict";
import { resolve } from "node:path";
import {
  createReact,
  element,
  find,
  gateSource,
  instantiate,
  loadAsset,
  named,
  sharedFragments,
  withSource,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";

const asset = loadAsset(
  process.argv[2] ??
    resolve(import.meta.dirname, "../src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
);

function originalUseMemo(factory) {
  return factory();
}
const react = createReact({
  useMemo: originalUseMemo,
  useSyncExternalStore: (_subscribe, snapshot) => snapshot(),
  useEffect: () => {},
});

// Valve's Customization page, reduced to its four sections in Steam's order.
const Section = () => null;
const LedSection = () => element(Section, { label: "LED" });
const StartupSection = withSource(function StartupSection() {
  return element(Section, { label: "Startup movie" });
}, 'function Bs(Rr){return(0,e.jsxs)(l.XY,{label:(0,c.we)("#Settings_Customization_StartupVideo")})}');
const ScreensaverSection = withSource(function ScreensaverSection() {
  return element(Section, { label: "Screensaver", children: [element("current", {})] });
}, 'function Us(){(0,d.we)("#Settings_Customization_Screensaver");Pi.b3.ForceScreensaver({enabled:!0})}');
const AdvancedSection = () => element(Section, { label: "Advanced" });
const SectionList = () => null;
const CustomizationPage = () =>
  element(SectionList, {
    children: [
      element(LedSection, {}),
      element(StartupSection, {}),
      element(ScreensaverSection, {}),
      element(AdvancedSection, {}),
    ],
  });
const SystemPage = () => element(SectionList, { children: [] });
const pageList = () => [
  { route: "/settings/system", content: element(SystemPage, {}) },
  { route: "/settings/customization", content: element(CustomizationPage, {}) },
];

// Steam's modules for the screensaver gate, resolved the way its own check resolves them.
const Dropdown = withSource(function Dropdown() {
  return null;
}, "function(){contextMenuPositionOptions childrenContainerWidth menuLabel}");
const native = {
  ForceScreensaver: () => Promise.resolve(),
  GetActiveState: () => Promise.resolve({ Body: () => ({ active: () => false }) }),
  RegisterForNotifyActiveStateChanged: () => ({ unregister: () => {} }),
};
const modules = {
  react,
  fields: { Vb: Dropdown, Xg: () => null },
  routes: {
    B: { Settings: { Customization: () => "/settings/customization" } },
    C: () => "/routes",
  },
  settings: {
    rV: {
      clientSettings: {
        system_idle_screensaver_ac_sec: 300,
        system_idle_screensaver_battery_sec: 600,
      },
    },
    VI: () => null,
  },
  section: {},
  observer: {},
  native: { service: native },
};
const moduleFor = (tokens) =>
  tokens.includes("useState")
    ? "react"
    : tokens.includes("SliderField")
      ? "fields"
      : tokens.includes("GameAPIOSK:")
        ? "routes"
        : tokens.includes("m_setDeferredSettings")
          ? "settings"
          : tokens.includes("Screensaver.GetActiveState#1")
            ? "native"
            : tokens.includes("ForceScreensaver")
              ? "section"
              : null;
const getWebpackRuntime = () => {
  const require = (id) => modules[id];
  require.findUnique = (tokens) => (moduleFor(tokens) ? [moduleFor(tokens), ""] : null);
  require.resolve = (tokens) => modules[moduleFor(tokens)];
  require.exported = (tokens, predicate) => {
    const fits = new Set(
      Object.values(modules[moduleFor(tokens)] ?? {}).filter((value) => predicate(value)),
    );
    assert.equal(fits.size, 1, `one export must fit ${tokens.join(", ")}`);
    return [...fits][0];
  };
  return require;
};

// The settings renderer and kit stylesheet are the toolkit's; here a row is an element carrying its
// descriptor and the callbacks the hub gave it.
const ui = {
  react,
  settingsSection: "SettingsSection",
  toggleField: "Toggle",
  dropdown: "Dropdown",
  valueField: "ValueField",
  dialogButton: "Button",
};
const Row = "Row";
const renderSteamSettingRow = (_ui, row, _draft, change, action) =>
  element(
    Row,
    { row, send: (value) => change(row, value), act: () => action(row) },
    `steam-setting-${row.key}`,
  );

const published = new Map();
const listeners = new Map();
const absent = new Set();
const requests = [];
const history = [];
const subscribe = (patchId, listener) => {
  if (absent.has(patchId)) throw new Error("subscription not allowlisted");
  if (!listeners.has(patchId)) listeners.set(patchId, new Set());
  listeners.get(patchId).add(listener);
  if (published.has(patchId)) listener(published.get(patchId));
  return () => listeners.get(patchId).delete(listener);
};
const publish = (patchId, state) => {
  published.set(patchId, state);
  for (const listener of [...(listeners.get(patchId) ?? [])]) listener(state);
};
const globals = {
  window: { tempNavStore: { m_history: { push: (route) => history.push(route) } } },
  setTimeout: () => 0,
  clearTimeout: () => {},
  getWebpackRuntime,
  resolveSteamSettingsComponents: () => ui,
  renderSteamSettingRow,
  steamUiKitStyle: () => element("style", {}),
  request: (patchId, command, payload) => {
    requests.push({ patchId, command, payload });
    return Promise.resolve();
  },
  subscribe,
};

const shared = sharedFragments(asset);
const createHub = () =>
  instantiate(
    globals,
    `${shared}\n${gateSource(asset, "consumer/customization.ts")}`,
    "createWsgmCustomization()",
  );
// Both gates in one scope, as the asset composes them, so they share the one useMemo claim.
const createBoth = () =>
  instantiate(
    globals,
    `${shared}\n${gateSource(asset, "consumer/customization.ts")}\n${gateSource(asset, "gates/screensaver.ts")}`,
    "({ hub: createWsgmCustomization(), screensaver: createScreensaverSettings() })",
  );

// What Steam's Settings root sees: the page list through the claimed useMemo, then the page rendered.
const renderCustomization = (list = pageList()) => {
  const pages = react.useMemo(() => list, []);
  const item = pages.find((page) => page.route === "/settings/customization");
  assert.equal(pages.find((page) => page.route === "/settings/system").content.type, SystemPage);
  const tree = item.content.type(item.content.props);
  // Render the section-level wrappers once, as React would, so a gate's section changes are visible.
  return react.cloneElement(
    tree,
    undefined,
    ...react.Children.toArray(tree.props.children).map((child) =>
      typeof child.type === "function" && child.type.name === "SteamUiScreensaverSection"
        ? { ...child, rendered: child.type(child.props) }
        : child,
    ),
  );
};
const hubsIn = (tree) =>
  react.Children.toArray(tree.props.children).filter(
    (child) => child.key === "wsgm-customization-hub",
  );
const order = (tree) =>
  react.Children.toArray(tree.props.children).map((child) =>
    child.key === "wsgm-customization-hub" ? "hub" : child.type.name,
  );
const renderHub = (tree) => {
  const [hub] = hubsIn(tree);
  return hub.type(hub.props);
};
const rows = (node) => find(react, node, (candidate) => candidate.type === Row);
const rowByKey = (node, key) => {
  const found = rows(node).filter((candidate) => candidate.props.row.key === key);
  assert.equal(found.length, 1, `exactly one row ${key}`);
  return found[0];
};
const sections = (node) =>
  find(react, node, (candidate) => candidate.type === "SettingsSection").map(
    (section) => section.props.label,
  );
const tick = () => new Promise((done) => setImmediate(done));

const themesState = {
  activeTab: "browse",
  themes: [
    {
      name: "a",
      displayName: "Alpha",
      version: "1",
      author: "x",
      enabled: true,
      status: "installed",
    },
    {
      name: "b",
      displayName: "Beta",
      version: "2",
      author: "",
      enabled: false,
      status: "outdated",
      latestVersion: "3",
    },
  ],
  presets: [{ name: "p", displayName: "Profile P" }],
  selectedPreset: "",
  settings: { enabled: true },
  busy: false,
  error: null,
  updates: 1,
};
const soundsState = {
  selected: "pack",
  packs: [
    { id: "pack", name: "Pack", error: null },
    { id: "broken", name: "Broken", error: "bad manifest" },
  ],
  busy: false,
  error: null,
  compatibility: "",
  integration: "Steam sound override connected.",
};
const animationsState = {
  selected: "",
  stockName: "Steam's own",
  library: [{ id: "m1", name: "Movie" }],
  settings: { restartNeeded: true },
  busy: false,
  error: null,
};

// 1. Alone: the hub lands once, in front of Valve's Startup Movie section, and Valve's sections stay.
const hub = createHub();
// The probe is the gate's own: ready once Steam's modules load, and it installs nothing.
assert.equal(hub.probe().outcome, "ready");
assert.equal(hub.status().claims, 0);
assert.deepEqual(hub.install(), { ok: true, installed: true });
assert.equal(hub.status().registered, true);
assert.equal(hub.status().held, true, "the standard status must show the transform held");
assert.equal(hub.probe().installed, true, "an installed gate answers from what it holds");
assert.deepEqual(hub.status().sources, {
  themes: "waiting",
  sounds: "waiting",
  animations: "waiting",
});
let tree = renderCustomization();
assert.deepEqual(order(tree), [
  "LedSection",
  "hub",
  "StartupSection",
  "ScreensaverSection",
  "AdvancedSection",
]);
assert.deepEqual(sections(renderHub(tree)), ["Themes", "UI sounds", "Boot animation"]);
assert.equal(rows(renderHub(tree)).length, 3, "each source says it is loading until it publishes");

// The same input list maps to the same output list; a transformed list is not wrapped again.
const list = pageList();
const once = react.useMemo(() => list, []);
assert.equal(
  react.useMemo(() => list, []),
  once,
);
const twice = react.useMemo(() => once, []);
assert.equal(
  hubsIn(renderCustomization(twice)).length,
  1,
  "a transformed list must not nest a second hub",
);

// 2. Published state: the rows show the services' choices and send their own commands.
publish("wsgm.themes", themesState);
publish("wsgm.sounds", soundsState);
publish("wsgm.animations", animationsState);
let body = renderHub(renderCustomization());
assert.equal(rowByKey(body, "themes-on").props.row.checked, true);
assert.equal(rowByKey(body, "themes-on").props.row.description, "1 of 2 themes on");
assert.equal(rowByKey(body, "themes-theme-b").props.row.description, "Update available (3)");
assert.deepEqual(
  rowByKey(body, "themes-profile").props.row.choices.map((choice) => choice.value),
  ["", "p"],
);
assert.equal(rowByKey(body, "sounds-pack").props.row.text, "pack");
assert.deepEqual(
  rowByKey(body, "sounds-pack").props.row.choices.map((choice) => choice.value),
  ["", "pack"],
  "Steam default stays first and an unusable pack is not offered",
);
assert.deepEqual(
  rowByKey(body, "animations-boot").props.row.choices.map((choice) => choice.label),
  ["Steam's own", "Movie"],
);
assert.equal(rowByKey(body, "animations-restart").props.row.accent, true);
rowByKey(body, "animations-suspend");

rowByKey(body, "themes-on").props.send(false);
rowByKey(body, "themes-theme-b").props.send(true);
rowByKey(body, "themes-profile").props.send("p");
rowByKey(body, "themes-update").props.act();
rowByKey(body, "sounds-pack").props.send("");
rowByKey(body, "animations-boot").props.send("m1");
assert.deepEqual(requests.splice(0), [
  { patchId: "wsgm.themes", command: "setSetting", payload: { key: "enabled", value: false } },
  { patchId: "wsgm.themes", command: "setEnabled", payload: { name: "b", enabled: true } },
  { patchId: "wsgm.themes", command: "setProfile", payload: { name: "p" } },
  { patchId: "wsgm.themes", command: "updateAll", payload: {} },
  { patchId: "wsgm.sounds", command: "select", payload: { id: "" } },
  { patchId: "wsgm.animations", command: "select", payload: { id: "m1" } },
]);

// Browse and manage: the page's tab is chosen first, then its route opens.
rowByKey(body, "themes-manage").props.act();
rowByKey(body, "sounds-manage").props.act();
rowByKey(body, "animations-manage").props.act();
await tick();
assert.deepEqual(requests.splice(0), [
  { patchId: "wsgm.themes", command: "setTab", payload: { tab: "installed" } },
  { patchId: "wsgm.animations", command: "setTab", payload: { tab: "library" } },
]);
assert.deepEqual(history.splice(0), ["/wsgm/sounds", "/wsgm/themes", "/wsgm/animations"]);

// Stock restore stays reachable: themes off disables only the per-theme rows and the profile.
publish("wsgm.themes", { ...themesState, settings: { enabled: false } });
body = renderHub(renderCustomization());
assert.equal(rowByKey(body, "themes-on").props.row.disabled, false);
assert.equal(rowByKey(body, "themes-theme-a").props.row.disabled, true);
assert.match(rowByKey(body, "themes-on").props.row.description, /Steam's own look/u);

// A choice the service no longer holds is shown as unavailable rather than silently as the default.
publish("wsgm.sounds", { ...soundsState, selected: "gone" });
body = renderHub(renderCustomization());
assert.deepEqual(
  rowByKey(body, "sounds-pack").props.row.choices.map((choice) => choice.value),
  ["", "gone", "pack"],
);

// 3. Removal hands Steam's page back whole; reinstalling (a CEF reload) draws one hub again.
assert.deepEqual(hub.remove(), { ok: true, removed: true });
assert.equal(hub.status().claims, 0, "removal must leave nothing held");
assert.equal(react.useMemo, originalUseMemo, "the last transform hands useMemo back");
tree = renderCustomization();
assert.deepEqual(order(tree), [
  "LedSection",
  "StartupSection",
  "ScreensaverSection",
  "AdvancedSection",
]);
assert.deepEqual(hub.remove(), { ok: true, absent: true });
const reloaded = createHub();
assert.equal(reloaded.install().ok, true);
assert.equal(hubsIn(renderCustomization()).length, 1);
assert.equal(reloaded.remove().ok, true);

// 4. Beside the screensaver gate, in both registration orders: one hub, one decorated section.
for (const first of ["hub", "screensaver"]) {
  const gates = createBoth();
  const order_ = first === "hub" ? ["hub", "screensaver"] : ["screensaver", "hub"];
  for (const name of order_)
    assert.equal(gates[name].install().ok, true, `${name} installs (${first} first)`);
  tree = renderCustomization();
  assert.equal(hubsIn(tree).length, 1, `one hub with ${first} first`);
  const children = react.Children.toArray(tree.props.children);
  const decorated = children.filter((child) => child.type.name === "SteamUiScreensaverSection");
  assert.equal(decorated.length, 1, `the screensaver section stays decorated with ${first} first`);
  assert.equal(
    children.findIndex((child) => child.key === "wsgm-customization-hub") + 1,
    children.findIndex((child) => child.type === StartupSection),
    "the hub stays directly in front of Valve's Startup Movie section",
  );
  assert.equal(find(react, decorated[0].rendered, named("SteamUiScreensaverTimeouts")).length, 1);
  for (const name of [...order_].reverse()) assert.equal(gates[name].remove().ok, true);
  assert.equal(react.useMemo, originalUseMemo);
}

// 5. A session without a service shows no section for it, and the others still render.
absent.add("wsgm.sounds");
const partial = createHub();
assert.equal(partial.install().ok, true);
assert.equal(partial.status().sources.sounds, "absent");
assert.deepEqual(sections(renderHub(renderCustomization())), ["Themes", "Boot animation"]);
assert.equal(partial.remove().ok, true);

// 6. A client without the Startup Movie section still gets the hub, last.
const hubAlone = createHub();
assert.equal(hubAlone.install().ok, true);
const NoStartupPage = () =>
  element(SectionList, { children: [element(LedSection, {}), element(ScreensaverSection, {})] });
tree = renderCustomization([
  { route: "/settings/system", content: element(SystemPage, {}) },
  { route: "/settings/customization", content: element(NoStartupPage, {}) },
]);
assert.deepEqual(order(tree), ["LedSection", "ScreensaverSection", "hub"]);
assert.equal(hubAlone.remove().ok, true);

console.log("Customization hub gate: ok");
