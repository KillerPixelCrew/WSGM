// Exercise the shipped Emulators page with Steam's native navigation-container contract and the
// commands each control sends. Equal-size cells model the card grid; no Steam session or hardware is
// started. The page frame owns the tabs, so the check plays the frame: it keeps the active tab and
// remounts a tab's content when the tab changes.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  createHooks,
  createReact,
  fragment,
  helperFragments,
  instantiate,
  substitute,
} from "../external/steam-ui-toolkit/eng/check-harness.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const asset = readFileSync(
  resolve(root, process.argv[2] ?? "src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js"),
  "utf8",
);
let hooks = createHooks();
const react = createReact({
  useState: (initial) => hooks.useState(typeof initial === "function" ? initial() : initial),
  useRef: (initial) => hooks.useRef(initial),
  useEffect: () => {},
});
const Focusable = "NativeFocusable";
const Button = "NativeButton";
const Primary = "NativePrimary";
const TextField = "NativeTextField";
const Dropdown = "NativeDropdown";
const DropdownField = "NativeDropdownField";
const Toggle = "NativeToggle";
const Field = "NativeField";
const Section = "NativeSection";
const ConfirmModal = "NativeConfirm";
const modals = [];
const ui = {
  react,
  focusable: Focusable,
  dialogButton: Button,
  dialogButtonPrimary: Primary,
  smallButton: Button,
  textField: TextField,
  dropdownControl: Dropdown,
  dropdown: DropdownField,
  toggleField: Toggle,
  valueField: Field,
  settingsSection: Section,
  confirmModal: ConfirmModal,
  showModal: (modal) => modals.push(modal),
};
const progress = {
  busy: false,
  status: "",
  operation: "",
  targetId: "",
  phase: "",
  bytes: 0,
  total: 0,
};
const commands = [];
const sent = [];
const picks = [];
const reports = [];
const code = substitute(
  helperFragments(asset),
  "showSteamFilePicker",
  "(_ui, options) => { picks.push(options); return Promise.resolve('C:\\\\BIOS\\\\scph39001.bin'); }",
);
const { Tab, Tabs, memory } = instantiate(
  {
    window: {},
    picks,
    useEmulatorProgress: () => progress,
    emulatorAct: (command, payload) => commands.push([command, payload]),
    emulatorPage: {
      send: (command, payload) => (sent.push([command, payload]), Promise.resolve(true)),
    },
  },
  code +
    "\n" +
    fragment(asset, "consumer/format-bytes.ts") +
    "\n" +
    fragment(asset, "consumer/emulators.ts"),
  "({ Tab: EmulatorTab, Tabs: EmulatorTabs, memory: emulatorMemory })",
);

let activeTab = "installed";
let mountedTab = activeTab;
const context = {
  ui: () => ui,
  tab: () => activeTab,
  setTab: (tab) => {
    activeTab = tab;
  },
  report: (text) => reports.push(text),
};
const prerequisite = {
  kind: "bios",
  name: "PlayStation 2 BIOS",
  description: "Supply a PS2 BIOS dump",
  required: true,
  extensions: [".bin"],
  allowDirectory: false,
};
const definitions = [
  {
    id: "pcsx2",
    name: "PCSX2",
    systems: ["ps2"],
    channels: ["stable", "nightly"],
    source: "github.com/PCSX2",
    prerequisites: [prerequisite],
    dataPolicy: { prerequisites: [prerequisite] },
  },
  {
    id: "dolphin",
    name: "Dolphin",
    systems: ["gamecube", "wii"],
    channels: ["release"],
    source: "dolphin-emu.org",
    prerequisites: [],
    dataPolicy: { prerequisites: [] },
  },
  {
    id: "retroarch",
    name: "RetroArch",
    systems: ["psx"],
    channels: ["stable"],
    source: "libretro",
    prerequisites: [],
    dataPolicy: { hasCores: true, prerequisites: [] },
  },
];
const installation = (definition, extra = {}) => ({
  id: definition.id + "-1",
  definitionId: definition.id,
  name: definition.name,
  systems: definition.systems,
  channel: definition.channels[0],
  architecture: "x64",
  managed: true,
  version: "1.0",
  releaseId: "old",
  ignoredReleaseId: "",
  root: "D:\\Emulators\\" + definition.name,
  executablePath: "D:\\Emulators\\" + definition.name + "\\" + definition.id + ".exe",
  dataPath: "D:\\Emulators\\" + definition.name + "\\data",
  steamShortcutEnabled: true,
  steamShortcutLaunchMode: 2,
  missingRequirements: [],
  cores: [],
  dataPolicy: definition.dataPolicy,
  ...extra,
});
const installations = [
  installation(definitions[0]),
  installation(definitions[1]),
  installation(definitions[2], {
    cores: [
      {
        id: "beetle",
        name: "Beetle PSX",
        systems: ["psx"],
        extensions: [".cue"],
        path: "cores\\beetle.dll",
        requiredFiles: ["scph5501.bin"],
      },
      {
        id: "pcsx",
        name: "PCSX ReARMed",
        systems: ["psx"],
        extensions: [".cue"],
        path: "cores\\pcsx.dll",
        requiredFiles: [],
      },
    ],
  }),
];
const listed = installations.map((item, index) => ({
  id: item.id,
  name: item.name,
  versionLine: "1.0 · Stable",
  summary: item.systems.join(" · "),
  appId: index === 0 ? 3000000001 : 0,
  chips: [
    {
      kind: index === 0 ? "update" : "upToDate",
      label: index === 0 ? "Update to 2.0" : "Up to date",
      tone: index === 0 ? "action" : "good",
    },
  ],
  setup:
    index === 0
      ? [
          {
            kind: "bios",
            name: "PlayStation 2 BIOS",
            ready: false,
            detail: "Supply a PS2 BIOS dump",
            required: true,
            extensions: [".bin"],
            allowDirectory: false,
          },
        ]
      : [],
  setupLabel: index === 0 ? "1 file missing" : "",
}));
const state = {
  emulators: {
    initialized: true,
    definitions,
    installations,
    offers: [
      {
        definitionId: "pcsx2",
        channel: "stable",
        architecture: "x64",
        version: "2.0",
        releaseId: "new",
        notesUrl: "https://example.test/notes",
        error: "",
      },
    ],
    systemPreferences: [],
  },
  installed: listed,
  romSystems: [
    { id: "ps2", name: "PlayStation 2" },
    { id: "psx", name: "PlayStation" },
    { id: "gamecube", name: "GameCube" },
    { id: "wii", name: "Wii" },
    { id: "n64", name: "Nintendo 64" },
  ],
  choices: [
    {
      systemId: "ps2",
      installations: [
        {
          id: "pcsx2-1",
          label: "PCSX2 1.0 · managed",
          requiresCore: false,
          cores: [],
          defaultCoreId: "",
        },
      ],
    },
  ],
  dependencies: [
    {
      installationId: "pcsx2-1",
      summary: "Used by 1 configured ROM source. 12 imported titles use this installation.",
    },
  ],
  launchModes: [{ value: 2, label: "Give the game the controller" }],
  catalog: definitions.map((definition) => ({
    definitionId: definition.id,
    name: definition.name,
    systems: definition.systems.join(" · "),
    families: definition.id === "dolphin" ? ["Nintendo"] : ["Sony"],
    latestVersion: definition.id === "pcsx2" ? "2.0" : "",
    requirement: definition.prerequisites.length ? "Needs a PS2 BIOS" : "",
    installedVersion: "",
  })),
  families: ["Sony", "Nintendo"],
  updates: [
    { installationId: "pcsx2-1", name: "PCSX2", version: "2.0" },
    { installationId: "dolphin-1", name: "Dolphin", version: "2412" },
  ],
  updatesSummary: "2 updates ready: PCSX2 2.0 and Dolphin 2412",
  updateAll: null,
  coreStatus: [
    {
      installationId: "retroarch-1",
      coreId: "beetle",
      missing: true,
      missingFiles: ["scph5501.bin"],
    },
  ],
  systems: [
    {
      systemId: "ps2",
      name: "PlayStation 2",
      family: "Sony",
      sources: 1,
      titles: 12,
      summary: "12 games from 1 source",
      inLibrary: true,
      needsEmulator: false,
    },
    {
      systemId: "n64",
      name: "Nintendo 64",
      family: "Nintendo",
      sources: 1,
      titles: 9,
      summary: "9 games have no emulator yet",
      inLibrary: true,
      needsEmulator: true,
    },
    {
      systemId: "psx",
      name: "PlayStation",
      family: "Sony",
      sources: 0,
      titles: 0,
      summary: "No games yet",
      inLibrary: false,
      needsEmulator: false,
    },
  ],
  bios: {
    folder: "C:\\BIOS",
    checked: true,
    systems: [{ id: "ps2", links: ["Linked into PCSX2"] }],
  },
  biosSummary: {
    found: 0,
    needed: 1,
    label: "0 of 1 file found",
    systems: [
      {
        systemId: "ps2",
        name: "PlayStation 2",
        usedBy: "PCSX2",
        optional: false,
        items: [
          { label: "scph39001.bin", state: "missing", stateLabel: "Missing", optional: false },
        ],
      },
      {
        systemId: "dc",
        name: "Dreamcast",
        usedBy: "",
        optional: false,
        items: [
          { label: "dc_boot.bin", state: "verified", stateLabel: "Verified", optional: false },
        ],
      },
    ],
  },
};

const render = () => {
  if (mountedTab !== activeTab) {
    hooks = createHooks();
    mountedTab = activeTab;
  }
  hooks.reset();
  return Tab({ context, state, tab: activeTab });
};
// Steam's field draws its trailing controls from its `value` prop, so a search descends into it.
const find = (react, node, predicate, found = []) => {
  if (!react.isValidElement(node)) return found;
  if (predicate(node)) found.push(node);
  for (const child of [...react.Children.toArray(node.props?.children), node.props?.value])
    find(react, child, predicate, found);
  return found;
};
const text = (node) =>
  typeof node === "string"
    ? node
    : react.Children.toArray(node?.props?.children).map(text).join("");
const button = (tree, label) =>
  find(
    react,
    tree,
    (node) => (node.type === Button || node.type === Primary) && text(node) === label,
  )[0];
const byClass = (tree, name) => find(react, tree, (node) => node.props.className === name);
const native = (node) =>
  [Focusable, Button, Primary, TextField, Dropdown, DropdownField, Toggle].includes(node.type);
const children = (node) =>
  react.Children.toArray(node.props.children).flatMap((child) => {
    if (!react.isValidElement(child)) return [];
    return native(child) ? [child] : children(child);
  });
const move = (group, current, direction, columns = 2) => {
  assert.equal(group.type, Focusable, "the direction owner must be a native Steam node");
  const targets = children(group).filter((child) => !child.props.disabled);
  const index = targets.indexOf(current);
  assert.ok(index >= 0, "the current target belongs to the navigation group");
  const horizontal = direction === "left" || direction === "right";
  const flow = group.props["flow-children"];
  if ((flow === "row" && !horizontal) || (flow === "column" && horizontal)) return null;
  const step = flow === "grid" && !horizontal ? columns : 1;
  const next = index + (direction === "left" || direction === "up" ? -step : step);
  return targets[next] ?? null;
};
const tab = (id) => {
  context.setTab(id);
  return render();
};
const commandNames = () => commands.map(([command]) => command);

// The frame draws the Overlay's five views, in its order.
assert.deepEqual(
  Tabs.map((item) => item.id),
  ["installed", "catalog", "cores", "defaults", "bios"],
);
assert.deepEqual(
  Tabs.map((item) => item.title),
  ["Installed", "Get emulators", "RetroArch cores", "System defaults", "BIOS and firmware"],
);

// Installed: the page's purpose and Check for updates, the waiting updates with Update both, and a
// grid of cards that moves on both axes.
let tree = render();
assert.equal(tree.type, Focusable);
assert.equal(tree.props.autoFocus, true, "the tab's content takes native focus");
assert.equal(
  text(find(react, tree, (node) => node.type === "h1")[0]),
  "Emulators",
  "the page names itself",
);
button(tree, "Check for updates").props.onClick();
assert.deepEqual(commands.pop(), ["refreshEmulators", {}]);
const updates = byClass(tree, "steam-ui-kit-banner")[0];
assert.ok(text(updates).includes("2 updates ready"), "the banner names the waiting updates");
button(updates, "Update both").props.onClick();
assert.deepEqual(commands.pop(), ["updateAllEmulators", {}]);
const grid = byClass(tree, "steam-ui-kit-grid")[0];
const cards = children(grid);
assert.equal(cards.length, 3);
assert.equal(
  cards[0].props.className,
  "steam-ui-kit-card icon",
  "installed emulators are icon cards",
);
assert.equal(move(grid, cards[0], "right"), cards[1]);
assert.equal(move(grid, cards[0], "down"), cards[2]);
assert.equal(cards[0].props.onOKActionDescription, "Open");
const homeKey = tree.key;
progress.status = "Checking releases";
assert.equal(render().key, homeKey, "a progress line keeps the tab's focus owner");
progress.status = "";

// An installed emulator: the update, setup checklist and In Steam beside the installation, B back
// to its card.
cards[0].props.onActivate();
tree = render();
assert.notEqual(tree.key, homeKey, "opening a card mounts a new level");
assert.equal(typeof tree.props.onCancelButton, "function", "B leaves the level");
const body = byClass(tree, "wsgm-emu-body")[0];
const columns = children(body);
assert.equal(body.props["flow-children"], "row");
assert.equal(columns.length, 2, "the main column and the installation panel");
assert.equal(move(body, columns[0], "right"), columns[1], "Right reaches the installation panel");
assert.equal(columns[1].props.className, "steam-ui-kit-side");
button(tree, "Update to 2.0").props.onClick();
assert.deepEqual(commands.pop(), ["updateEmulator", { installationId: "pcsx2-1" }]);
button(tree, "Skip version").props.onClick();
assert.deepEqual(commands.pop(), [
  "ignoreEmulatorVersion",
  { installationId: "pcsx2-1", releaseId: "new" },
]);
button(tree, "Release notes").props.onClick();
assert.equal(commands.pop()[0], "openEmulatorReleaseNotes");
const setupRow = find(
  react,
  tree,
  (node) => node.type === Field && node.props.name === "PlayStation 2 BIOS",
)[0];
assert.ok(setupRow, "the setup checklist lists the prerequisite");
assert.equal(setupRow.props.icon.props.className, "steam-ui-kit-state missing");
button(setupRow, "Choose file…").props.onClick();
await new Promise((done) => setImmediate(done));
assert.deepEqual(picks.pop().extensions, [".bin"]);
assert.deepEqual(commands.pop(), [
  "configureEmulatorPrerequisite",
  { installationId: "pcsx2-1", path: "C:\\BIOS\\scph39001.bin", kind: "bios" },
]);
button(tree, "Recheck").props.onClick();
assert.deepEqual(commands.pop(), ["refreshEmulators", {}]);
const toggle = find(react, tree, (node) => node.type === Toggle)[0];
assert.equal(toggle.props.label, "Show PCSX2 in Steam");
toggle.props.onChange(false);
assert.deepEqual(commands.pop(), [
  "setEmulatorShortcut",
  { installationId: "pcsx2-1", enabled: false, launchMode: 2 },
]);
button(tree, "Repair installation").props.onClick();
assert.deepEqual(commands.pop(), ["repairEmulator", { installationId: "pcsx2-1" }]);

// Remove asks first, with Cancel focused, and says what is deleted and what stays.
button(tree, "Remove PCSX2…").props.onClick();
const confirm = modals.pop();
assert.equal(confirm.type, ConfirmModal);
assert.equal(confirm.props.strTitle, "Remove PCSX2?");
assert.equal(confirm.props.focusButton, "secondary", "Cancel takes the controller first");
const confirmText = text(confirm.props.strDescription);
assert.ok(confirmText.includes("deletes PCSX2's program files"), "it names what is deleted");
assert.ok(confirmText.includes("stay in"), "it names what stays");
assert.equal(sent.length, 0, "nothing is removed before the answer");
confirm.props.onOK();
await new Promise((done) => setImmediate(done));
assert.deepEqual(sent.pop(), ["removeEmulator", { installationId: "pcsx2-1" }]);
tree = render();
assert.equal(tree.key, homeKey, "a removal returns to the list");
assert.equal(
  children(byClass(tree, "steam-ui-kit-grid")[0])[0].props.autoFocus,
  true,
  "focus returns to the card",
);

// Running work on the emulator shows its steps, its meter and Stop inside its detail.
children(byClass(tree, "steam-ui-kit-grid")[0])[0].props.onActivate();
Object.assign(progress, {
  busy: true,
  status: "Downloading",
  operation: "update",
  targetId: "pcsx2-1",
  phase: "download",
  bytes: 1048576,
  total: 4194304,
});
tree = render();
const work = byClass(tree, "steam-ui-kit-progress")[0];
assert.ok(work, "the work shows inside the emulator");
assert.equal(
  byClass(work, "steam-ui-kit-progress-step current").length,
  1,
  "the download step is current",
);
assert.ok(text(work).includes("1 MB of 4 MB"));
assert.ok(text(work).includes("Stopping keeps the active version"));
button(work, "Stop operation").props.onClick();
assert.deepEqual(commands.pop(), ["cancel", {}]);
assert.equal(
  button(tree, "Repair installation").props.disabled,
  true,
  "other work waits while busy",
);
Object.assign(progress, {
  busy: false,
  status: "",
  operation: "",
  targetId: "",
  phase: "",
  bytes: 0,
  total: 0,
});
tree.props.onCancelButton();
tree = render();
assert.equal(tree.key, homeKey, "Back returns to the list");

// Get emulators: search, family filters, cards that open their details on A and install on X.
tree = tab("catalog");
const catalog = children(byClass(tree, "steam-ui-kit-grid")[0]);
assert.equal(catalog.length, 3);
assert.equal(catalog[0].props.onOKActionDescription, "Details");
assert.equal(catalog[0].props.onSecondaryActionDescription, "Install");
catalog[0].props.onSecondaryButton();
assert.deepEqual(commands.pop(), ["installEmulator", { definitionId: "pcsx2", channel: "stable" }]);
find(react, tree, (node) => node.type === TextField)[0].props.onChange({
  target: { value: "gamecube" },
});
tree = render();
assert.equal(
  children(byClass(tree, "steam-ui-kit-grid")[0]).length,
  1,
  "the search filters the catalogue",
);
find(react, tree, (node) => node.type === TextField)[0].props.onChange({
  target: { value: "no such" },
});
tree = render();
button(tree, "Clear search").props.onClick();
tree = render();
assert.equal(
  children(byClass(tree, "steam-ui-kit-grid")[0]).length,
  3,
  "Clear search lists everything again",
);
button(tree, "Nintendo (1)").props.onClick();
tree = render();
assert.equal(
  children(byClass(tree, "steam-ui-kit-grid")[0]).length,
  1,
  "a family filter narrows the list",
);
button(tree, "All (3)").props.onClick();
tree = render();
children(byClass(tree, "steam-ui-kit-grid")[0])[0].props.onActivate();
tree = render();
const channel = find(
  react,
  tree,
  (node) => node.type === DropdownField && node.props.label === "Release channel",
)[0];
channel.props.onChange({ data: "nightly" });
tree = render();
button(tree, "Install PCSX2 nightly").props.onClick();
assert.deepEqual(commands.pop(), [
  "installEmulator",
  { definitionId: "pcsx2", channel: "nightly" },
]);
button(tree, "Use my installation…").props.onClick();
await new Promise((done) => setImmediate(done));
assert.deepEqual(picks.pop().extensions, [".exe"]);
assert.equal(commands.pop()[0], "useExternalEmulator");
button(tree, "BIOS and firmware").props.onClick();
assert.equal(activeTab, "bios", "a link opens the BIOS view");

// RetroArch cores: the cores that need files first, X to BIOS and firmware, Update installed cores.
tree = tab("cores");
const attention = byClass(tree, "wsgm-emu-section").find((node) =>
  text(node).startsWith("Needs attention"),
);
assert.ok(attention, "a core missing files is listed under Needs attention");
const core = children(byClass(attention, "steam-ui-kit-list")[0])[0];
assert.ok(text(core).includes("needs scph5501.bin"));
assert.equal(core.props.onSecondaryActionDescription, "BIOS and firmware");
button(tree, "Update installed cores").props.onClick();
assert.deepEqual(commands.pop(), ["updateEmulator", { installationId: "retroarch-1" }]);
core.props.onClick();
tree = render();
assert.ok(
  text(tree).includes("Required: scph5501.bin"),
  "a core's details list its declared files",
);
button(tree, "Back to cores").props.onClick();
tree = render();
assert.equal(byClass(tree, "steam-ui-kit-list")[0] !== undefined, true);

// System defaults: each used system's emulator and core, Get an emulator where none runs it.
tree = tab("defaults");
const ps2 = find(
  react,
  tree,
  (node) => node.type === Field && node.props.name === "PlayStation 2",
)[0];
const choice = find(react, ps2, (node) => node.type === Dropdown)[0];
choice.props.onChange({ data: "pcsx2-1" });
assert.deepEqual(commands.pop(), [
  "setPreferredEmulator",
  { systemId: "ps2", installationId: "pcsx2-1", coreId: "" },
]);
const n64 = find(
  react,
  tree,
  (node) => node.type === Field && node.props.name === "Nintendo 64",
)[0];
button(n64, "Get an emulator").props.onClick();
assert.equal(activeTab, "catalog");
assert.equal(memory.catalogSearch, "Nintendo 64", "the catalogue opens searched for the system");
memory.catalogSearch = "";
tree = tab("defaults");
const others = byClass(tree, "steam-ui-kit-group closed")[0];
assert.ok(others, "systems no source uses start folded");

// BIOS and firmware: the shared folder with its count, a missing file with Choose file, Check files.
tree = tab("bios");
button(tree, "Check files").props.onClick();
assert.deepEqual(commands.pop(), ["verifyBios", {}]);
const file = find(
  react,
  tree,
  (node) => node.type === Field && node.props.name === "scph39001.bin",
)[0];
button(file, "Choose file…").props.onClick();
await new Promise((done) => setImmediate(done));
picks.pop();
assert.deepEqual(commands.pop(), [
  "addBiosFiles",
  { path: "C:\\BIOS\\scph39001.bin", systemId: "ps2" },
]);
const ps2System = find(
  react,
  tree,
  (node) => node.type === Field && node.props.name === "PlayStation 2",
)[0];
button(ps2System, "Relink emulators").props.onClick();
assert.deepEqual(commands.pop(), ["relinkBios", { systemId: "ps2" }]);

// An empty installation offers the catalogue; one still loading says so.
state.installed = [];
tree = tab("installed");
button(tree, "Get emulators").props.onClick();
assert.equal(activeTab, "catalog");
state.emulators.initialized = false;
tree = tab("installed");
assert.ok(text(tree).includes("Reading the installed emulators"), "loading is not an empty list");

assert.deepEqual(reports.filter(Boolean), [], "no command was refused");
assert.deepEqual(commandNames(), [], "every command was accounted for");
console.log(
  "Emulators page: five tabs, installed cards, update banner, detail with setup, In Steam and removal, item progress with Stop, catalogue, cores, defaults and BIOS passed.",
);
