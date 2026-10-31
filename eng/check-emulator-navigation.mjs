// Exercise the shipped emulator page with Steam's native navigation-container contract.
// Equal-size cells model wide and wrapped layouts; no Steam session or hardware is started. The
// page frame owns the tabs, so the check plays the frame: it keeps the active tab, remounts a tab's
// content when the tab changes and runs each mounted content's effects once.
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
  resolve(root, process.argv[2] ?? "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
  "utf8",
);
let hooks = createHooks();
const effects = [];
const react = createReact({
  useState: (initial) => hooks.useState(typeof initial === "function" ? initial() : initial),
  useRef: (initial) => hooks.useRef(initial),
  // Once per mount, after the render, as React runs an effect with an empty dependency list.
  useEffect: (effect) => {
    const ran = hooks.useRef(false);
    if (!ran.current) {
      ran.current = true;
      effects.push(effect);
    }
  },
});
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
const { Page, emulatorTabs, memory } = instantiate(
  {
    window: {},
    importUi: ui,
    useEmulatorProgress: () => progress,
    emulatorAct: (command) => commands.push(command),
    emulatorPage: { send: (command) => Promise.resolve(commands.push(command)) },
    EmulatorPatchId: "wsgm.emulators",
  },
  helperFragments(asset) + "\n" + fragment(asset, "consumer/emulators.ts"),
  "({ Page: ImportEmulatorPage, emulatorTabs, memory: emulatorRouteMemory })",
);
// The frame's local tab: a change remounts the tab's content.
let activeTab = "installed";
let mountedTab = activeTab;
const context = {
  tab: () => activeTab,
  setTab: (tab) => {
    activeTab = tab;
  },
};
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
  if (mountedTab !== activeTab) {
    hooks = createHooks();
    mountedTab = activeTab;
  }
  hooks.reset();
  const tree = Page({ context, state, tab: activeTab });
  const ran = effects.splice(0);
  for (const effect of ran) effect();
  return ran.length && mountedTab !== activeTab ? render() : tree;
};
const classNode = (tree, name) => find(react, tree, (node) => node.props.className === name)[0];
const text = (node) =>
  typeof node === "string"
    ? node
    : react.Children.toArray(node?.props?.children).map(text).join("");
const button = (tree, label) =>
  find(react, tree, (node) => node.type === Button && text(node) === label)[0];
const tab = (_tree, label) => {
  const target = emulatorTabs(state).find((item) => item.title === label);
  assert.ok(target, label);
  context.setTab(target.id);
  return render();
};
const routeContent = (tree) => classNode(tree, "wsgm-emu-pane").props.children.at(-1);

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
const installedContent = routeContent(tree);
assert.equal(installedContent.props.autoFocus, true, "the mounted route takes native focus");
assert.deepEqual(
  emulatorTabs(state).map((item) => item.id),
  ["installed", "available", "bios", "defaults"],
  "the frame draws the four tabs",
);

for (const label of ["Installed (3)", "Available (3)"]) {
  tree = tab(tree, label);
  const list = classNode(tree, "wsgm-emu-list");
  const rows = children(list);
  assert.equal(rows.length, 3);
  assert.equal(move(list, rows[0], "down"), rows[1], `${label}: Down moves to the next row`);
  assert.equal(move(list, rows[1], "up"), rows[0], `${label}: Up returns to the previous row`);
  assert.equal(
    find(react, list, (node) => node.props.className === "steam-ui-kit-card-shot").length,
    0,
    "emulators do not have fake artwork",
  );
  const content = routeContent(tree);
  progress.status = "Checking releases";
  const refreshed = routeContent(render());
  assert.equal(refreshed.key, content.key, "publications retain the current route's focus owner");
  progress.status = "";
  rows[0].props.onClick();
  tree = render();
  const detail = routeContent(tree);
  assert.notEqual(detail.key, content.key, "opening a card mounts a new route focus owner");
  const split = classNode(tree, "steam-ui-kit-detail");
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
  assert.equal(routeContent(tree).key, content.key, "Back returns to the list route");
  assert.equal(
    children(classNode(tree, "wsgm-emu-list"))[0].props.autoFocus,
    true,
    "Back restores focus to the opened row",
  );
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
state.emulators.installations = [];
tree = tab(render(), "Installed (0)");
const browse = button(tree, "Browse available emulators");
assert.ok(browse, "an empty installed list has an actionable catalogue entry");
browse.props.onClick();
tree = render();
assert.equal(activeTab, "available");
// The first look at an empty installation is the catalogue, chosen once per page session.
memory.reset();
context.setTab("installed");
hooks = createHooks();
mountedTab = "installed";
render();
assert.equal(
  activeTab,
  "available",
  "an empty installation opens directly into the available catalogue",
);
context.setTab("installed");
render();
assert.equal(activeTab, "installed", "Installed stays reachable once the first tab was chosen");
assert.deepEqual(commands, [], "directional traversal never performs a backend write");
console.log(
  "Emulator native navigation: frame tabs, list rows, detail columns, return focus and BIOS/default groups passed.",
);
