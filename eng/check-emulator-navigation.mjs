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
const react = createReact({
  ...hooks,
  useState: (initial) => hooks.useState(typeof initial === "function" ? initial() : initial),
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
const Tabs = "NativeTabs";
const ui = {
  react,
  focusable: Focusable,
  dialogButton: Button,
  dialogButtonPrimary: Button,
  textField: TextField,
  dropdownControl: Dropdown,
  tabs: Tabs,
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
  const tree = Page({ state });
  const tabs = tree.props.children.at(-1);
  // Valve owns tab navigation. Expose only its selected content to the inert tree traversal.
  return react.cloneElement(
    tree,
    {},
    ...tree.props.children.slice(0, -1),
    react.cloneElement(
      tabs,
      {},
      tabs.props.tabs.find((item) => item.id === tabs.props.activeTab)?.content,
    ),
  );
};
const classNode = (tree, name) => find(react, tree, (node) => node.props.className === name)[0];
const text = (node) =>
  typeof node === "string"
    ? node
    : react.Children.toArray(node?.props?.children).map(text).join("");
const button = (tree, label) =>
  find(react, tree, (node) => node.type === Button && text(node) === label)[0];
const tab = (tree, label) => {
  const tabs = find(react, tree, (node) => node.type === Tabs)[0];
  const target = tabs.props.tabs.find((item) => item.title === label);
  assert.ok(target, label);
  tabs.props.onShowTab(target.id);
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
const tabs = find(react, tree, (node) => node.type === Tabs)[0];
assert.equal(tabs.props.autoFocusContents, true, "Valve owns entry into the active tab");
assert.deepEqual(
  tabs.props.tabs.map((item) => item.id),
  ["installed", "available", "bios", "defaults"],
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
assert.equal(find(react, tree, (node) => node.type === Tabs)[0].props.activeTab, "available");
const freshHooks = createHooks();
ui.react = createReact({
  ...freshHooks,
  useState: (initial) => freshHooks.useState(typeof initial === "function" ? initial() : initial),
});
assert.equal(
  Page({ state }).props.children.at(-1).props.activeTab,
  "available",
  "an empty installation opens directly into the available catalogue",
);
assert.deepEqual(commands, [], "directional traversal never performs a backend write");
console.log(
  "Emulator native navigation: Valve tabs, list rows, detail columns, return focus and BIOS/default groups passed.",
);
