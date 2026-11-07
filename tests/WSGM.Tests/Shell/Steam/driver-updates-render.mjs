// Execute the emitted native Settings gate with the actual WSGM producer payload.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  createReact,
  element,
  gateSource,
  instantiate,
  loadAsset,
  sharedFragments,
  substitute,
  withSource,
} from "../../../../external/steam-ui-toolkit/eng/check-harness.mjs";

const payload = JSON.parse(readFileSync(process.argv[2], "utf8"));
const asset = loadAsset("src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js");
const react = createReact({
  useMemo: (factory) => factory(),
  useSyncExternalStore: (_subscribe, revision) => revision(),
  useEffect: () => {},
});
const descriptors = Object.fromEntries(
  ["Display", "Power", "Audio", "Controller"].map((key) => [
    key,
    { route: `/settings/${key.toLowerCase()}`, visible: true, content: element("native", {}) },
  ]),
);
const NativeRoot = withSource(
  () => react.useMemo(() => descriptors),
  '#Settings_Title show-icon "Settings"',
);
const ui = Object.fromEntries(
  [
    "settingsSection",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "valueField",
    "dialogButton",
    "smallButton",
    "showModal",
    "modalRoot",
    "confirmModal",
  ].map((name) => [name, name]),
);
ui.react = react;
let publish;
const { gate } = instantiate(
  {
    fixtureComponents: () => ui,
    getWebpackRuntime: () => ({
      count: () => 1,
      findUnique: () => ["fixture", ""],
      exported: () => NativeRoot,
      resolve: () => ({ jsx: element, jsxs: element }),
    }),
    useSteamSettingDrafts: () => ({
      change: (send) => send,
      row: (row) => row,
      draft: () => undefined,
    }),
    renderSteamSettingRow: (_ui, row) => element("row", { row }),
    steamUiKitStyle: () => element("style", {}),
    subscribe: (_id, callback) => {
      publish = callback;
      return () => {};
    },
    request: () => Promise.resolve(),
  },
  substitute(sharedFragments(asset), "resolveSteamUiComponents", "fixtureComponents") +
    gateSource(asset, "gates/native-settings.ts"),
  "({gate:createNativeSettings()})",
);
assert.equal(gate.install().ok, true);
publish(payload);
assert.equal(gate.status().publicationError, "");
// The gate decorates the descriptors through the native root's memo claim.
const mapped = NativeRoot();
assert.equal(mapped.Display.content.props.children[0], descriptors.Display.content);
const slot = mapped.Display.content.props.children[1];
const rendered = slot.type(slot.props).props.children;
const expected = payload.pages.find((page) => page.id === "display").sections;
assert.equal(rendered.length, expected.length);
for (const [index, section] of expected.entries()) {
  assert.equal(rendered[index].props.label, section.title ?? undefined);
  assert.deepEqual(
    rendered[index].props.children.map((field) => field.props.row.key),
    section.rows.map((row) => row.key),
  );
}
assert.ok(expected.some((section) => section.id === "driver-updates"));
// A previously unknown section id renders first in host order as well as after native content.
const display = payload.pages.find((page) => page.id === "display");
display.sections.reverse();
payload.revision++;
publish(payload);
assert.deepEqual(
  slot.type(slot.props).props.children.map((section) => section.props.label),
  display.sections.map((section) => section.title ?? undefined),
);
assert.equal(gate.remove().ok, true);
