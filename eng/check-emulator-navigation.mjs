// Exercise the shipped emulator page with Steam's native navigation-container contract.
// Equal-size cells model wide and wrapped layouts; no Steam session or hardware is started.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  createHooks,
  createReact,
  find,
  fragment,
  helperFragments,
  instantiate,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const asset = readFileSync(
  resolve(root, "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
  "utf8",
);
const hooks = createHooks();
const react = createReact(hooks);
// The shared fixture preserves keys on clones; real React replaces an explicitly supplied key.
const clone = react.cloneElement;
react.cloneElement = (source, props, ...children) => ({
  ...clone(source, props, ...children),
  key: props?.key === undefined ? source.key : String(props.key),
});
const Focusable = "NativeFocusable";
const Button = "NativeButton";
const TextField = "NativeTextField";
const Dropdown = "NativeDropdown";
const ui = {
  react,
  focusable: Focusable,
  dialogButton: Button,
  dialogButtonPrimary: Button,
  textField: TextField,
  dropdownControl: Dropdown,
};
const progress = { busy: false, status: "" };
const commands = [];
const Page = instantiate(
  {
    window: {},
    importUi: ui,
    useEmulatorProgress: () => progress,
    emulatorAct: (command) => commands.push(command),
    EmulatorPatchId: "wsgm.emulators",
  },
  helperFragments(asset) + "\n" + fragment(asset, "consumer/emulators.ts"),
  "ImportEmulatorPage",
);
const definitions = Array.from({ length: 6 }, (_, index) => ({
  id: "definition" + index,
  name: "Emulator " + index,
  systems: ["ps2"],
  channels: ["stable"],
  source: "https://example.test",
  prerequisites: [{ name: "BIOS", description: "Supply a local BIOS" }],
  dataPolicy: {},
}));
const installations = definitions.slice(0, 3).map((definition, index) => ({
  ...definition,
  id: "installation" + index,
  definitionId: definition.id,
  channel: "stable",
  managed: true,
  version: "1",
  architecture: "x64",
  releaseId: "old",
  missingRequirements: [],
  cores: [],
  dataPolicy: { prerequisites: [] },
}));
const state = {
  architecture: "x64",
  emulators: { definitions, installations, offers: [], systemPreferences: [] },
  installed: [],
  romSystems: [{ id: "ps2", name: "PlayStation 2" }],
  choices: [],
  bios: {
    folder: "C:\\BIOS",
    checked: true,
    systems: [
      { id: "ps2", name: "PlayStation 2", status: "Missing", emulators: [], files: [], links: [] },
    ],
  },
};
const render = () => {
  hooks.reset();
  return Page({ state });
};
const classNode = (tree, name) => find(react, tree, (node) => node.props.className === name)[0];
const text = (node) =>
  typeof node === "string"
    ? node
    : react.Children.toArray(node?.props?.children).map(text).join("");
const button = (tree, label) =>
  find(react, tree, (node) => node.type === Button && text(node) === label)[0];
const tab = (tree, label) => {
  const target = button(tree, label);
  assert.ok(target, label);
  target.props.onClick();
  return render();
};

// Plain divs do not register navigation nodes. Their native descendants register with the
// nearest native ancestor, which is why the split needs both a grid and column containers.
const native = (node) => [Focusable, Button, TextField, Dropdown].includes(node.type);
const children = (node) =>
  react.Children.toArray(node.props.children).flatMap((child) => {
    if (!react.isValidElement(child)) return [];
    return native(child) ? [child] : children(child);
  });

// Steam's row/column layouts accept only their respective axes; grid accepts both and uses
// cell geometry. Fixed equal cells keep the expected move unambiguous at either width.
const move = (group, current, direction, columns = 2) => {
  assert.equal(group.type, Focusable, "the direction owner must be a native Steam node");
  const targets = children(group).filter((child) => !child.props.disabled);
  const index = targets.indexOf(current);
  assert.ok(index >= 0, "the current target belongs to the navigation group");
  const horizontal = direction === "left" || direction === "right";
  const flow = group.props["flow-children"];
  if ((flow === "row" && !horizontal) || (flow === "column" && horizontal)) return null;
  const step = flow === "grid" && !horizontal ? columns : 1;
  if (
    flow === "grid" &&
    horizontal &&
    (direction === "left" ? index % columns === 0 : index % columns === columns - 1)
  )
    return null;
  const next = index + (direction === "left" || direction === "up" ? -step : step);
  return targets[next] ?? null;
};

let tree = render();
const installedContent = tree.props.children.at(-1);
assert.equal(installedContent.props.autoFocus, true, "the mounted route takes native focus");
const tabs = classNode(tree, "wsgm-emu-tabs");
const tabButtons = children(tabs);
assert.equal(move(tabs, tabButtons[0], "right", 4), tabButtons[1], "Right reaches Available");
assert.equal(move(tabs, tabButtons[1], "left", 4), tabButtons[0], "Left reaches Installed");
assert.equal(
  move(tabs, tabButtons[0], "down", 2),
  tabButtons[2],
  "wrapped tabs remain reachable vertically",
);

for (const label of ["Installed 3", "Available 3"]) {
  tree = tab(tree, label);
  const grid = classNode(tree, "wsgm-emu-grid");
  const cards = children(grid);
  assert.equal(cards.length, 3);
  assert.equal(move(grid, cards[0], "right"), cards[1], `${label}: Right moves between cards`);
  assert.equal(move(grid, cards[1], "left"), cards[0], `${label}: Left moves between cards`);
  assert.equal(move(grid, cards[0], "down"), cards[2], `${label}: Down reaches the second row`);
  assert.equal(move(grid, cards[2], "up"), cards[0], `${label}: Up returns to the first row`);
  assert.equal(
    move(grid, cards[0], "down", 1),
    cards[1],
    `${label}: one-column layouts still move Down`,
  );
  const content = tree.props.children.at(-1);
  progress.status = "Checking releases";
  const refreshed = render().props.children.at(-1);
  assert.equal(refreshed.key, content.key, "publications retain the current route's focus owner");
  progress.status = "";
  cards[0].props.onActivate();
  tree = render();
  const detail = tree.props.children.at(-1);
  assert.notEqual(detail.key, content.key, "opening a card mounts a new route focus owner");
  const split = classNode(tree, "wsgm-emu-split");
  const columns = children(split);
  assert.equal(
    columns.length,
    2,
    "the split retains two native column nodes rather than flattened controls",
  );
  assert.equal(move(split, columns[0], "right"), columns[1], "Right crosses detail columns");
  assert.equal(move(split, columns[1], "left"), columns[0], "Left returns to the main column");
  assert.equal(
    move(split, columns[0], "down", 1),
    columns[1],
    "stacked narrow detail columns remain reachable",
  );
  assert.equal(columns[0].props["flow-children"], "column");
  assert.equal(columns[1].props["flow-children"], "column");
  tree.props.onCancelButton();
  tree = render();
  assert.equal(tree.props.children.at(-1).key, content.key, "Back returns to the list route");
}
tree = tab(tree, "BIOS & firmware");
const biosSplit = classNode(tree, "wsgm-emu-split");
assert.equal(children(biosSplit).length, 2);
assert.equal(
  move(biosSplit, children(biosSplit)[0], "right"),
  children(biosSplit)[1],
  "BIOS actions can be reached from systems",
);
tree = tab(tree, "System defaults");
const defaultRow = classNode(tree, "wsgm-emu-default");
assert.equal(defaultRow.type, Focusable);
assert.equal(
  defaultRow.props["flow-children"],
  "grid",
  "stacked default editors retain vertical navigation",
);
assert.deepEqual(commands, [], "directional traversal never performs a backend write");
console.log(
  "Emulator native navigation: tabs, multi-row/one-column cards, detail columns, route focus lifetime and BIOS/default groups passed.",
);
