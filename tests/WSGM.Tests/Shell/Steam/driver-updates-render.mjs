// Execute the emitted native Settings gate with the actual WSGM producer payload.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  createReact,
  element,
  fragments,
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
    fixtureDrafts: () => ({
      change: (send) => send,
      row: (row) => row,
      draft: () => undefined,
    }),
    fixtureRow: (_ui, row) => element("row", { row }),
    fixtureStyle: () => element("style", {}),
    subscribe: (_id, callback) => {
      publish = callback;
      return () => {};
    },
    request: () => Promise.resolve(),
  },
  substitute(sharedFragments(asset), "resolveSteamUiComponents", "fixtureComponents") +
    // The shipped section renderer and kit lay out the page and its subpages; rows stay fixtures.
    substitute(
      substitute(
        substitute(
          fragments(asset, ["icons.ts", "ui-kit.ts", "settings.ts"]),
          "useSteamSettingDrafts",
          "fixtureDrafts",
        ),
        "renderSteamSettingRow",
        "fixtureRow",
      ),
      "steamUiKitStyle",
      "fixtureStyle",
    ) +
    gateSource(asset, "gates/native-settings.ts"),
  "({gate:createNativeSettings()})",
);
assert.equal(gate.install().ok, true);
publish(payload);
assert.equal(gate.status().publicationError, "");
// The gate decorates the descriptors through the native root's memo claim.
const mapped = NativeRoot();
const valve = mapped.Display.content.props.children[0];
assert.equal(valve.props.content, descriptors.Display.content);
const slot = mapped.Display.content.props.children[1];
const expected = payload.pages.find((page) => page.id === "display").sections;
const keys = (section) => section.props.children.map((field) => field.props.row.key);
// Sections on the page draw in host order; every subpage is one row in the group that follows them.
const onPage = expected.filter((section) => !section.level);
const levels = [
  ...new Map(
    expected.filter((section) => section.level).map((section) => [section.level.id, section.level]),
  ).values(),
];
let rendered = slot.type(slot.props).props.children;
assert.deepEqual(
  rendered.filter((child) => child.props.label !== "More display settings").map(keys),
  onPage.map((section) => section.rows.map((row) => row.key)),
);
const group = rendered.find((child) => child.props.label === "More display settings");
assert.ok(group, "the subpages sit under one heading");
assert.deepEqual(
  group.props.children.map((row) => row.props["aria-label"].split(",")[0]),
  levels.map((level) => level.title),
);
const driverRow = group.props.children.find((row) => row.key === "level-driver-updates");
assert.ok(driverRow, "driver updates are a subpage");
// Opening it draws its section alone and hides Steam's own content; Back restores both.
driverRow.props.onActivate();
rendered = slot.type(slot.props).props.children;
assert.equal(rendered.length, 1);
const opened = rendered[0].props.children.filter((child) => child?.type === "settingsSection");
assert.deepEqual(
  opened.map(keys),
  expected
    .filter((section) => section.level?.id === "driver-updates")
    .map((section) => section.rows.map((row) => row.key)),
);
assert.equal(valve.type(valve.props), null);
rendered[0].props.onCancelButton();
assert.equal(valve.type(valve.props), descriptors.Display.content);
assert.equal(gate.remove().ok, true);
