// Exercise the production Library Manager tree without Steam, network or hardware. These checks
// cover native focus containers and user commands; real Steam still owns spatial focus movement.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import {
  createHooks,
  createReact,
  find,
  fragment,
  helperFragments,
  helperLabels,
  instantiate,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";

const asset = readFileSync(
  new URL("../src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js", import.meta.url),
  "utf8",
);
let hooks = createHooks();
const react = createReact({
  useState: (initial) => hooks.useState(initial),
  useRef: (initial) => hooks.useRef(initial),
  useEffect: () => {},
  useMemo: (factory) => factory(),
});
const ui = {
  react,
  focusable: "NativeFocusable",
  dialogButton: "NativeButton",
  dialogButtonPrimary: "NativePrimary",
  textField: "NativeTextField",
  toggleField: "NativeToggle",
  tabs: "NativeTabs",
  dropdown: "NativeDropdown",
};
const commands = [];
let state = {
  entries: [
    { id: "one", name: "New game", group: "new", editable: true, selected: true, artwork: [] },
  ],
  sources: [
    { id: "xbox", name: "Xbox", kind: "Launcher", installed: true, canToggle: true, checked: true },
  ],
  selectedCount: 1,
};
const context = {
  ui: () => ui,
  react: () => react,
  useState: () => state,
  refusal: () => null,
  act: (command, payload) => {
    commands.push([command, payload]);
    return Promise.resolve({});
  },
};
const api = instantiate(
  { window: {}, registerSteamPage: () => context, emulatorStyles: "", EmulatorTabs: [] },
  helperFragments(
    asset,
    helperLabels(asset).filter((label) => label !== "page-gate.ts"),
  ) +
    "\n" +
    fragment(asset, "consumer/library-import.ts"),
  "({LibraryImportPage, ImportTitleArtwork})",
);
const text = (node) =>
  typeof node === "string"
    ? node
    : react.Children.toArray(node?.props?.children).map(text).join("");
const button = (tree, label) =>
  find(react, tree, (node) => node.type === ui.dialogButton && text(node) === label)[0];
const byClass = (tree, name) => find(react, tree, (node) => node.props.className === name)[0];
// Native Tabs renders just the active tab's content.
const contents = (tree) => {
  const tabs = find(react, tree, (node) => node.type === ui.tabs)[0];
  return tabs ? tabs.props.tabs.find((tab) => tab.id === tabs.props.activeTab).content : tree;
};
const render = () => {
  hooks.reset();
  return api.LibraryImportPage({ context, state });
};

let tree = render();
const row = byClass(tree, "wsgm-import-body");
assert.equal(row.type, ui.focusable);
assert.equal(row.props["flow-children"], "row");
for (const name of ["wsgm-import-sidebar", "wsgm-import-main"]) {
  const pane = byClass(tree, name);
  assert.equal(pane.type, ui.focusable);
  assert.equal(pane.props["flow-children"], "column");
  assert.equal(pane.props.navEntryPreferPosition, 3, "cross-pane entry keeps the current row");
}

let review = contents(tree);
const search = find(react, review, (node) => node.type === ui.textField)[0];
search.props.onChange({ target: { value: "New" } });
tree = render();
review = contents(tree);
button(review, "Clear search").props.onClick();
review = contents(render());
assert.equal(find(react, review, (node) => node.type === ui.textField)[0].props.value, "");
assert.equal(state.entries[0].selected, true, "clearing the filter retains the import selection");
button(review, "Recheck all artwork").props.onClick();
assert.deepEqual(commands.pop(), ["recheckArtwork", { id: "" }]);

hooks = createHooks();
hooks.reset();
let chosenAsset;
tree = api.ImportTitleArtwork({
  entry: state.entries[0],
  asset: "grid",
  onAsset: (asset) => {
    chosenAsset = asset;
  },
  onBack: () => {},
  status: null,
});
const split = byClass(tree, "wsgm-import-split");
assert.equal(split.type, ui.focusable);
assert.equal(split.props["flow-children"], "row");
const side = byClass(tree, "wsgm-import-side");
assert.equal(side.props.navEntryPreferPosition, 3);
const slots = find(
  react,
  side,
  (node) => node.type === ui.dialogButton && node.props["aria-pressed"] !== undefined,
);
assert.equal(slots.length, 5, "every artwork slot is a native controller focus stop");
for (const slot of slots) slot.props.onClick();
assert.equal(chosenAsset, "icon");
button(side, "Recheck artwork").props.onClick();
assert.deepEqual(commands.pop(), ["recheckArtwork", { id: "one" }]);
button(side, "Clear search").props.onClick();
assert.deepEqual(commands.pop(), ["clearMatchSearch", { id: "one" }]);
console.log(
  "Library Manager: native pane navigation, five artwork slot actions, search clearing and artwork rechecks passed.",
);
