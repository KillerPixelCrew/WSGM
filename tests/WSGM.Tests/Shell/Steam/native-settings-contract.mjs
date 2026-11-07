// The shipped native Settings gate against WSGM's own producer output: every page, every row kind,
// unknown values, and one row the gate must drop while the rest still draw.
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
const invalidKey = process.argv[3];
const asset = loadAsset("src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js");
const react = createReact({
  useMemo: (factory) => factory(),
  useSyncExternalStore: (_subscribe, revision) => revision(),
  useEffect: () => {},
});
const NativePages = ["Display", "Power", "Audio", "Controller"];
const descriptors = Object.fromEntries(
  NativePages.map((key) => [
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
const requests = [];
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
    request: (_patch, command, body) => {
      requests.push([command, body]);
      return Promise.resolve();
    },
  },
  substitute(sharedFragments(asset), "resolveSteamUiComponents", "fixtureComponents") +
    gateSource(asset, "gates/native-settings.ts"),
  "({gate:createNativeSettings()})",
);
assert.equal(gate.install().ok, true);
publish(payload);

// The invalid row is dropped and reported once, with its key; nothing else is.
assert.match(
  gate.status().publicationError,
  new RegExp(
    `^1 native Settings row\\(s\\) could not be drawn: ${invalidKey.replace(/[.]/gu, "\\.")}$`,
    "u",
  ),
);
assert.deepEqual(requests, [
  ["rejected", { rows: [{ key: invalidKey, reason: "unsupported kind unsupported-kind" }] }],
]);
publish({ ...payload, revision: payload.revision + 1 });
assert.equal(requests.length, 1, "the same rejection is reported once");

const drawn = (page) => {
  const content = NativeRoot()[page].content;
  const slot = content.props.children.find((child) => typeof child?.type === "function");
  assert.ok(slot, `${page} carries WSGM's sections`);
  return slot.type(slot.props).props.children.map((section) => ({
    label: section.props.label,
    keys: section.props.children.map((field) => field.props.row.key),
  }));
};
let rows = 0;
for (const page of payload.pages) {
  const name = NativePages.find((key) => key.toLowerCase() === page.id);
  const sections = drawn(name);
  const expected = page.sections
    .map((section) => ({
      label: section.title ?? undefined,
      keys: section.rows.map((row) => row.key).filter((key) => key !== invalidKey),
    }))
    .filter((section) => section.keys.length);
  assert.deepEqual(sections, expected, `${page.id} draws every valid row in host order`);
  rows += expected.reduce((sum, section) => sum + section.keys.length, 0);
}
const all = payload.pages.flatMap((page) => page.sections.flatMap((section) => section.rows));
assert.ok(
  all.some((row) => row.kind === "range" && row.number == null),
  "unknown ranges are part of the contract",
);
assert.ok(
  all.some((row) => row.kind === "curve"),
  "a fan curve is part of the contract",
);
assert.ok(
  all.some((row) => row.kind === "color"),
  "lighting colours are part of the contract",
);
assert.equal(rows, all.length - 1);
assert.equal(gate.remove().ok, true);
console.log(
  `native Settings contract: ${rows} rows on ${payload.pages.length} pages drawn, 1 refused and reported.`,
);
