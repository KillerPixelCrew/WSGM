// The Game Library's page in Steam: bring games from other launchers into Steam, with their artwork.
//
// Laid out the way Steam ROM Manager lays out its preview, and drawn entirely with Steam's own
// components so it behaves like the rest of Big Picture under a controller: a sidebar of sources
// ticked with Steam's checkbox, Steam's tabs over a toolbar and a grid of Steam library capsules
// grouped by source, an all-artwork view with one row per title, and one title's artwork. WSGM owns
// the data and every decision; the toolkit owns the capsule, the folder picker and the fail-closed
// component discovery used here.
const LibraryImportPatchId = "steam-ui.library-import";
let importUi: any = null;
let importClasses: any = null;
let ImportCapsule: any = null;
let importDesired: any = null;
const importListeners = new Set<(state: any) => void>();

const sendImportCommand = (command: string, payload: any = {}) =>
  request(LibraryImportPatchId, command, payload, nextActionGeneration(LibraryImportPatchId));

// Steam's gamepad button codes for the triggers, as Focusable's onButtonDown reports them.
const ImportTriggerLeft = 7;
const ImportTriggerRight = 8;

const importAssets = [
  { id: "grid", label: "Portrait capsule", short: "Portrait" },
  { id: "wide", label: "Wide capsule", short: "Wide" },
  { id: "hero", label: "Hero", short: "Hero" },
  { id: "logo", label: "Logo", short: "Logo" },
  { id: "icon", label: "Icon", short: "Icon" },
];

// How wide a card is drawn for each artwork type; the height follows the type's aspect. Five
// portraits fit a row: Steam's tab panel pads the pane by 36px a side, leaving 812px at 1280.
const importCardWidths: Record<string, number> = { grid: 150, wide: 300, hero: 300, logo: 220, icon: 150 };

// What each planned action is called on a card, and its badge colours. An action without an entry
// here is shown by its own name rather than hidden.
const importActionBadges: Record<string, { label: string; tone: string; text: string }> = {
  Add: { label: "New", tone: "#1a9fff", text: "#ffffff" },
  Update: { label: "Update", tone: "#d9a441", text: "#1a1206" },
  Artwork: { label: "Artwork", tone: "#d9a441", text: "#1a1206" },
  Adopt: { label: "Adopt", tone: "#1a9fff", text: "#ffffff" },
  Remove: { label: "Remove", tone: "#c2463e", text: "#ffffff" },
  Skip: { label: "Imported", tone: "#3d4450", text: "#dcdedf" },
  Conflict: { label: "Edited by hand", tone: "#c2463e", text: "#ffffff" },
};
const importExcludedBadge = { label: "Left out", tone: "rgba(14,20,27,0.85)", text: "#b8bcbf" };

const importModeLabels: Record<string, string> = {
  ControllerOnly: "Controller only",
  SteamIntegration: "Steam overlay",
};

const importTabs = [
  { id: "all", title: "All", test: (_: any) => true },
  { id: "new", title: "New", test: (e: any) => !e.excluded && (e.action === "Add" || e.action === "Adopt") },
  {
    id: "imported",
    title: "Imported",
    test: (e: any) => !e.excluded && (e.action === "Skip" || e.action === "Update" || e.action === "Artwork"),
  },
  {
    id: "attention",
    title: "Needs attention",
    test: (e: any) => e.action === "Conflict" || e.action === "Remove" || e.artworkStatus === "failed",
  },
  { id: "excluded", title: "Left out", test: (e: any) => !!e.excluded },
];

// The page's own layout over Steam's components. Steam's stable class names (DialogCheckbox,
// DialogLabel, DialogInput, DialogButton) are what the compacting rules below hang on; the hashed
// ones are never named.
const importStyles = `
#wsgm-import { margin-top: var(--basicui-header-height, 40px); height: calc(100% - var(--basicui-header-height, 40px));
  display: flex; flex-direction: column; background: var(--gpSystemDarkestGrey, #0e141b); color: #dcdedf; }
#wsgm-import .wsgm-import-head { display: flex; align-items: baseline; gap: 14px; padding: 24px 48px 0; flex-shrink: 0; }
#wsgm-import .wsgm-import-head h1 { margin: 0; font-size: 30px; font-weight: 700; color: #fff; letter-spacing: 0.2px; }
#wsgm-import .wsgm-import-crumb { font-size: 30px; font-weight: 700; color: #8b929a; }
#wsgm-import .wsgm-import-crumb svg { margin: 0 -2px 2px 0; vertical-align: middle; }
#wsgm-import .wsgm-import-muted { color: #8b929a; font-size: 14px; }
#wsgm-import .wsgm-import-body { flex: 1; min-height: 0; display: flex; gap: 32px; padding: 16px 48px 0; }
#wsgm-import .wsgm-import-sidebar { width: 260px; flex: 0 0 auto; overflow-y: auto; min-height: 0;
  padding-bottom: 72px; display: flex; flex-direction: column; gap: 2px; }
#wsgm-import .wsgm-import-eyebrow { font-size: 13px; font-weight: 700; letter-spacing: 1.6px; text-transform: uppercase;
  color: #dcdedf; padding: 0 10px 8px; }
#wsgm-import .wsgm-import-sidebar .wsgm-import-custom { padding-top: 16px; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container { display: flex; flex-direction: row; flex-wrap: nowrap;
  align-items: center; gap: 12px; min-height: 38px; margin: 0; padding: 0 10px; border-radius: 2px; box-sizing: border-box; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container > .DialogCheckbox { flex: 0 0 auto; margin: 0; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container > div:empty { display: none; }
#wsgm-import .wsgm-import-source .DialogToggle_Label { flex: 1; min-width: 0; font-size: 15px; line-height: 1.2;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin: 0; padding: 0; }
#wsgm-import .wsgm-import-source .DialogToggle_Description { flex: 0 0 auto; margin: 0; padding: 0;
  font-size: 13px; color: #8b929a; white-space: nowrap; }
#wsgm-import .wsgm-import-source[data-missing="true"] .DialogCheckbox_Container { opacity: 0.6; }
#wsgm-import .wsgm-import-sidebar > .DialogButton { margin: 8px 0 0; width: auto; min-width: auto; height: 36px; }
#wsgm-import .wsgm-import-main { flex: 1; min-width: 0; min-height: 0; display: flex; flex-direction: column; }
#wsgm-import .wsgm-import-pane { display: flex; flex-direction: column; gap: 14px; padding: 12px 4px 72px; }
#wsgm-import .wsgm-import-bar { display: flex; align-items: center; gap: 10px; flex-wrap: nowrap; }
#wsgm-import .wsgm-import-bar .DialogButton { width: auto; min-width: auto; height: 40px; padding: 0 16px;
  white-space: nowrap; box-sizing: border-box; }
#wsgm-import .wsgm-import-spacer { flex: 1; }
#wsgm-import .wsgm-import-tool { flex: 0 0 auto; }
#wsgm-import .wsgm-import-tool .DialogButton { width: 100%; justify-content: space-between; }
#wsgm-import .wsgm-import-tool .DialogDropDown_CurrentDisplay { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-search { flex: 0 0 auto; width: 150px; }
#wsgm-import .wsgm-import-search .DialogLabel { display: none; }
#wsgm-import .wsgm-import-search .DialogInputLabelGroup, #wsgm-import .wsgm-import-search .DialogInput_Wrapper { margin: 0; }
#wsgm-import .wsgm-import-search .DialogInput { width: 100%; height: 40px; box-sizing: border-box; padding: 0 12px; }
#wsgm-import .wsgm-import-status { font-size: 14px; color: #b8bcbf; }
#wsgm-import .wsgm-import-status:empty { display: none; }
#wsgm-import .wsgm-import-status.wsgm-import-error { color: #ff6d6d; }
#wsgm-import .wsgm-import-group { display: flex; flex-direction: column; gap: 14px; margin-bottom: 8px; }
#wsgm-import .wsgm-import-grouphead { display: flex; align-items: baseline; gap: 14px; }
#wsgm-import .wsgm-import-grouphead .wsgm-import-eyebrow { padding: 0; }
#wsgm-import .wsgm-import-grid { display: flex; flex-wrap: wrap; gap: 22px 14px; padding: 4px; }
#wsgm-import .wsgm-import-card { display: flex; flex-direction: column; gap: 8px; }
#wsgm-import .wsgm-import-name { display: flex; align-items: center; gap: 6px; font-size: 13px; color: #b8bcbf; }
#wsgm-import .wsgm-import-name svg { flex: 0 0 auto; }
#wsgm-import .wsgm-import-name span { min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-launch { font-size: 12px; color: #8b929a; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-badge { position: absolute; top: 8px; right: 8px; padding: 3px 8px; border-radius: 2px;
  font-size: 11px; font-weight: 700; letter-spacing: 0.4px; text-transform: uppercase; pointer-events: none; }
#wsgm-import .wsgm-import-check { position: absolute; top: 8px; left: 8px; width: 24px; height: 24px; border-radius: 50%;
  background: #1a9fff; display: flex; align-items: center; justify-content: center; pointer-events: none;
  box-shadow: 0 1px 4px rgba(0,0,0,0.5); }
#wsgm-import .wsgm-import-rows { display: flex; flex-direction: column; gap: 10px; }
#wsgm-import .wsgm-import-row { display: flex; align-items: center; gap: 14px; padding: 6px 0; }
#wsgm-import .wsgm-import-rowname { width: 170px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 4px; }
#wsgm-import .wsgm-import-rowname span:first-child { font-size: 15px; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-colhead { display: flex; gap: 14px; align-items: flex-end; padding: 6px 0 8px; border-bottom: 1px solid #23262e; }
#wsgm-import .wsgm-import-colhead > * { flex: 0 0 auto; }
#wsgm-import .wsgm-import-col { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
#wsgm-import .wsgm-import-col .wsgm-import-eyebrow { padding: 0; font-size: 12px; }
#wsgm-import .wsgm-import-col .DialogButton { width: auto; min-width: auto; height: 24px; padding: 0 8px; font-size: 11px; }
#wsgm-import .wsgm-import-split { display: flex; gap: 36px; flex: 1; min-height: 0; }
#wsgm-import .wsgm-import-side { width: 260px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 12px; }
#wsgm-import .wsgm-import-side .DialogButton { width: auto; min-width: auto; height: 40px; }
#wsgm-import .wsgm-import-side .DialogLabel { display: none; }
#wsgm-import .wsgm-import-side .DialogInputLabelGroup, #wsgm-import .wsgm-import-side .DialogInput_Wrapper { margin: 0; }
#wsgm-import .wsgm-import-side .DialogInput { width: 100%; height: 40px; box-sizing: border-box; padding: 0 12px; }
#wsgm-import .wsgm-import-chosen { display: flex; align-items: center; gap: 12px; padding: 8px; border-radius: 2px; }
#wsgm-import .wsgm-import-chosen[data-on="true"] { background: #23262e; }
#wsgm-import .wsgm-import-chosen img, #wsgm-import .wsgm-import-chosen .wsgm-import-thumb { flex: 0 0 auto; border-radius: 2px;
  object-fit: cover; background: rgba(255,255,255,0.06); }
#wsgm-import .wsgm-import-chosen div { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
#wsgm-import .wsgm-import-chosen div span:first-child { font-size: 14px; color: #fff; }
#wsgm-import .wsgm-import-chosen div span:last-child { font-size: 12px; color: #8b929a; }
#wsgm-import .wsgm-import-rule { height: 1px; background: #23262e; margin: 6px 0; }
#wsgm-import .wsgm-import-matches { display: flex; flex-direction: column; gap: 4px; }
.wsgm-import-detail { display: flex; flex-direction: column; gap: 8px; min-width: min(640px, 80vw); }
.wsgm-import-detail dl { margin: 0; }
.wsgm-import-detail dt { opacity: 0.7; font-size: 13px; }
.wsgm-import-detail dd { margin: 0 0 8px; font-size: 14px; word-break: break-word; }
.wsgm-import-detail .wsgm-import-bar { display: flex; gap: 8px; flex-wrap: wrap; }
.wsgm-import-detail .wsgm-import-bar > button { width: auto; min-width: auto; }
.wsgm-import-risk { display: flex; flex-direction: column; gap: 12px; }
.wsgm-import-risk p { margin: 0; line-height: 1.45; }
`;

// The few glyphs the page draws itself: a check on a selected card, the two launch modes beside a
// title's name, a search glass, and the breadcrumb's chevron.
const importIcon = (h: any, name: string, size = 16) => {
  const base = {
    width: size,
    height: size,
    viewBox: "0 0 24 24",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 2,
    strokeLinecap: "round",
    strokeLinejoin: "round",
    "aria-hidden": "true",
  };
  switch (name) {
    case "check":
      return h("svg", { ...base, stroke: "#ffffff", strokeWidth: 3 }, h("path", { d: "M5 12l5 5 9-10" }));
    case "controller":
      return h(
        "svg",
        { ...base, "aria-label": "Controller only" },
        h("path", {
          d: "M6 9h12a4 4 0 0 1 3.9 4.9l-.8 3.3a2 2 0 0 1-3.5.8L15.5 16h-7l-2.1 2a2 2 0 0 1-3.5-.8l-.8-3.3A4 4 0 0 1 6 9z",
        }),
        h("path", { d: "M8 11.5v3M6.5 13h3" }),
      );
    case "overlay":
      return h(
        "svg",
        { ...base, "aria-label": "Steam overlay" },
        h("rect", { x: 3, y: 4, width: 18, height: 14, rx: 2 }),
        h("path", { d: "M3 9h18" }),
        h("path", { d: "M8 21h8" }),
      );
    case "launcher":
      return h(
        "svg",
        { ...base, "aria-label": "Through its launcher" },
        h("path", { d: "M5 12h14" }),
        h("path", { d: "M13 6l6 6-6 6" }),
      );
    case "chevron":
      return h("svg", { ...base, strokeWidth: 2.5 }, h("path", { d: "M9 6l6 6-6 6" }));
    default:
      return null;
  }
};

const showImportModal = (render: (close: () => void) => any, title: string) => {
  if (!importUi?.showModal || !importUi?.modalRoot) return;
  const Root = importUi.modalRoot;
  let close = () => {};
  const Modal = (props: any) => {
    close = props?.closeModal ?? (() => {});
    return importUi.react.createElement(
      Root,
      { onCancel: close, closeModal: close, strTitle: title },
      render(close),
    );
  };
  importUi.showModal(importUi.react.createElement(Modal, {}), window, { strTitle: title });
};

// A command whose refusal the page shows: the host explains every refusal, and a control that did
// nothing without saying why is the defect this avoids.
let importReportError: (message: string) => void = () => {};
const importAct = (command: string, payload: any = {}) =>
  sendImportCommand(command, payload).catch((error: any) => {
    importReportError(String(error?.message ?? error));
    return undefined;
  });

// A dropdown for a toolbar: Steam's bare control when the client offers it, its labelled field
// otherwise. Both take the same option props.
const importDropdown = (ui: any, props: any) =>
  ui.dropdownControl
    ? ui.react.createElement(ui.dropdownControl, {
        rgOptions: props.rgOptions,
        selectedOption: props.selectedOption,
        onChange: props.onChange,
        disabled: props.disabled,
        menuLabel: props.label,
      })
    : ui.react.createElement(ui.dropdown, {
        label: props.label,
        rgOptions: props.rgOptions,
        selectedOption: props.selectedOption,
        onChange: props.onChange,
        disabled: props.disabled,
        layout: "below",
      });

// The ban-risk acknowledgement. Deliberately a modal with its own checkbox rather than a switch on
// the card: the user is accepting a risk to their account, and that should not be one press away
// from a grid they are moving through.
const renderImportRiskModal = (entry: any, close: () => void) => {
  const react = importUi.react;
  const Body = () => {
    const [checked, setChecked] = react.useState(false);
    return react.createElement(
      "div",
      { className: "wsgm-import-risk" },
      react.createElement(
        "p",
        {},
        `${entry.name} is marked as a multiplayer title. The Steam overlay route loads Steam's ` +
          "overlay into the running game. Anti-cheat compatibility has not been established for " +
          "any title, and some anti-cheat systems treat that as tampering.",
      ),
      react.createElement(
        "p",
        {},
        "Controller-only support injects nothing and is the safer choice for a multiplayer game.",
      ),
      react.createElement(importUi.toggleField, {
        label: "I understand this may risk a ban on this title",
        checked,
        onChange: (value: boolean) => setChecked(value),
      }),
      react.createElement(
        "div",
        { className: "wsgm-import-bar" },
        react.createElement(importUi.dialogButton, { onClick: close }, "Keep controller only"),
        react.createElement(
          importUi.dialogButtonPrimary,
          {
            disabled: !checked,
            onClick: () => {
              if (!checked) return;
              void importAct("setMode", { id: entry.id, mode: "SteamIntegration", acknowledged: true });
              close();
            },
          },
          "Use the Steam overlay",
        ),
      ),
    );
  };
  return react.createElement(Body, {});
};

// X on a card: the Xbox route's input mode, or the next of a launcher title's routes.
const switchImportLaunch = (entry: any) => {
  const routes = entry.routes ?? [];
  if (routes.length > 1) {
    const index = routes.findIndex((route: any) => route.id === entry.route);
    const next = routes[(index + 1) % routes.length];
    void importAct("setRoute", { id: entry.id, route: next.id });
    return;
  }
  if (routes.length) return;
  if (entry.mode === "SteamIntegration") {
    void importAct("setMode", { id: entry.id, mode: "ControllerOnly", acknowledged: false });
    return;
  }
  if (!entry.canUseSteamIntegration) return;
  if (entry.requiresAcknowledgement) {
    showImportModal((close) => renderImportRiskModal(entry, close), entry.name);
    return;
  }
  void importAct("setMode", { id: entry.id, mode: "SteamIntegration", acknowledged: false });
};

const importLaunchSummary = (entry: any) =>
  entry.routes?.length ? entry.launchLabel : (importModeLabels[entry.mode] ?? entry.mode);

// The glyph beside a title's name: its input mode for a packaged title, a launcher arrow for one
// that starts through its launcher, nothing for one Steam starts directly.
const importLaunchIcon = (h: any, entry: any) => {
  if (!entry.routes?.length) {
    return importIcon(h, entry.mode === "SteamIntegration" ? "overlay" : "controller");
  }
  const route = entry.routes.find((candidate: any) => candidate.id === entry.route);
  return route?.follows || /launcher|through/i.test(String(route?.label ?? "")) ? importIcon(h, "launcher") : null;
};

// The entry's own sheet: what the review knows about it, and what can be done to it one entry at a
// time. Everything here is also refused by the host when it does not apply, so an action shown for a
// stale entry fails with a reason rather than acting on the wrong title.
const renderImportDetailModal = (entry: any, close: () => void, openArtwork: () => void) => {
  const react = importUi.react;
  const h = react.createElement;
  const act = (command: string) => {
    void importAct(command, { id: entry.id });
    close();
  };
  const launchControl = entry.routes?.length
    ? entry.routes.length > 1
      ? h(importUi.dropdown, {
          label: "Launch route",
          description: entry.launchEvidence,
          rgOptions: entry.routes.map((route: any) => ({ data: route.id, label: route.label })),
          selectedOption: entry.route,
          onChange: (option: any) => void importAct("setRoute", { id: entry.id, route: option?.data }),
        })
      : null
    : h(importUi.dropdown, {
        label: "Launch mode",
        description: entry.requiresAcknowledgement
          ? "Multiplayer title. The Steam overlay route needs the ban risk accepted first."
          : entry.launchEvidence,
        rgOptions: [
          { data: "ControllerOnly", label: importModeLabels.ControllerOnly },
          ...(entry.canUseSteamIntegration
            ? [{ data: "SteamIntegration", label: importModeLabels.SteamIntegration }]
            : []),
        ],
        selectedOption: entry.mode,
        onChange: (option: any) => {
          if (option?.data === entry.mode) return;
          if (option?.data === "SteamIntegration" && entry.requiresAcknowledgement) {
            close();
            showImportModal((next) => renderImportRiskModal(entry, next), entry.name);
            return;
          }
          void importAct("setMode", { id: entry.id, mode: option?.data, acknowledged: false });
        },
      });
  const artwork = (entry.artwork ?? [])
    .map((slot: any) => {
      const label = importAssets.find((asset) => asset.id === slot.asset)?.short ?? slot.asset;
      const value =
        slot.kind === "keep"
          ? "current"
          : slot.kind === "none"
            ? "none"
            : slot.kind === "loading"
              ? "loading"
              : `${slot.provider} ${slot.index}/${slot.count}`;
      return `${label}: ${value}`;
    })
    .join(" · ");
  const rows: [string, string][] = [
    ["Launch route", `${entry.launchLabel}. ${entry.launchEvidence}`],
    ["Multiplayer", `${entry.multiplayer}. ${entry.multiplayerEvidence}`],
    ["Saving would", `${importActionBadges[entry.action]?.label ?? entry.action}: ${entry.reason}`],
    ["Artwork", artwork],
    ["Matched to", entry.matchName ? `${entry.matchName}${entry.matchFixed ? " (fixed)" : ""}` : "—"],
    ["Installed at", entry.installPath || "unknown"],
    ["Identity", `${entry.source}: ${entry.identity}`],
  ];
  const actions = [
    entry.action !== "Remove" && entry.action !== "Conflict" && !entry.excluded
      ? { label: "Choose artwork…", run: () => (close(), openArtwork()) }
      : null,
    entry.appId > 0 && entry.action !== "Remove"
      ? {
          label: "Open Steam's artwork page…",
          run: () => {
            close();
            void importAct("openArtwork", { id: entry.id }).then((answer: any) => {
              if (answer?.route) navigateSteamRoute(answer.route);
            });
          },
        }
      : null,
    entry.excluded
      ? { label: "Offer again", run: () => act("include") }
      : entry.action === "Add" || entry.action === "Adopt"
        ? { label: "Don't import", run: () => act("exclude") }
        : null,
  ].filter((action) => action !== null) as { label: string; run: () => void }[];
  return h(
    "div",
    { className: "wsgm-import-detail" },
    launchControl,
    h(
      "dl",
      {},
      ...rows.flatMap(([term, value], index) => [
        h("dt", { key: `t${index}` }, term),
        h("dd", { key: `d${index}` }, value || "—"),
      ]),
      ...(entry.notes ?? []).map((note: string, index: number) => h("dd", { key: `n${index}` }, note)),
    ),
    h(
      importUi.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      ...actions.map((action) =>
        h(importUi.dialogButton, { key: action.label, onClick: action.run }, action.label),
      ),
      h(importUi.dialogButton, { key: "close", onClick: close }, "Close"),
    ),
  );
};

// A card's caption: which image of how many, and from whom.
const importSlotCaption = (slot: any) => {
  if (!slot) return null;
  switch (slot.kind) {
    case "keep":
      return "Keeps current";
    case "none":
      return slot.count ? `None · ${slot.count} found` : "No image";
    case "loading":
      return "Finding images…";
    default:
      return `${slot.provider} ${slot.index > 0 ? `${slot.index}/${slot.count}` : ""}`.trim();
  }
};

// The triggers cycle the focused card's image in place, the way SRM's arrows do.
const importCycleHandler = (entry: any, asset: string) => (event: any) => {
  const button = event?.detail?.button;
  if (button !== ImportTriggerLeft && button !== ImportTriggerRight) return;
  void importAct("cycleArtwork", {
    id: entry.id,
    asset,
    delta: button === ImportTriggerRight ? 1 : -1,
  });
};

// Steam's React, from the page host: Steam has exactly one, and the page draws before this gate has
// resolved on a cold start.
let importReact: any = null;

// One component for the life of the asset. The page host draws it on every router render, and a
// component declared inside the renderer would be a new type each time: React would remount the
// page and drop its selection and the controller's focus.
function LibraryImportPage() {
  const react = importReact;
  const h = react.createElement;
  {
    const [, setRevision] = react.useState(0);
    const [view, setView] = react.useState("grid");
    const [tab, setTab] = react.useState("all");
    const [asset, setAsset] = react.useState("grid");
    const [query, setQuery] = react.useState("");
    const [titleId, setTitleId] = react.useState("");
    const [titleAsset, setTitleAsset] = react.useState("grid");
    const [fillFrom, setFillFrom] = react.useState("");
    const [pageError, setPageError] = react.useState("");
    react.useEffect(() => {
      const listener = () => setRevision((value: number) => value + 1);
      importListeners.add(listener);
      importReportError = (message: string) => setPageError(message);
      return () => {
        importListeners.delete(listener);
        importReportError = () => {};
      };
    }, []);

    // After the hooks, so a render before the gate resolves calls the same ones as one after.
    if (!importUi || !ImportCapsule) return h("div", { className: "sgdb-loading" }, "Loading…");

    const ui = importUi;
    const state = importDesired ?? {};
    const entries: any[] = state.entries ?? [];
    const sources: any[] = state.sources ?? [];
    const busy = !!state.loading;
    const preference = fillFrom || state.artworkPreference || "Catalog";
    const openTitle = (entry: any, type = "grid") => {
      setTitleId(entry.id);
      setTitleAsset(type);
      setView("title");
    };
    const openDetails = (entry: any) =>
      showImportModal(
        (close) => renderImportDetailModal(entry, close, () => openTitle(entry)),
        entry.name,
      );
    const matchesQuery = (entry: any) =>
      !query || String(entry.name).toLowerCase().includes(query.toLowerCase());
    const allRows = entries.filter((entry) => entry.selected && matchesQuery(entry));

    const status = h(
      "div",
      {
        className: `wsgm-import-status${state.error || pageError ? " wsgm-import-error" : ""}`,
      },
      pageError ||
        state.error ||
        state.notice ||
        (state.phase === "applying" ? `Saving ${state.progress ?? 0} of ${state.progressTotal ?? 0}…` : "") ||
        state.launcherDetail ||
        "",
    );

    // The sources, each ticked with Steam's own checkbox. One that is not installed cannot be ticked.
    // The right-hand text is what the last scan found in it, or why it cannot be scanned.
    const Check = ui.checkbox ?? ui.toggleField;
    const sourceRow = (source: any) =>
      h(
        "div",
        { key: source.id, className: "wsgm-import-source", "data-missing": String(!source.installed) },
        h(Check, {
          label: source.name,
          description: !source.installed
            ? source.detail || "Not found"
            : source.count >= 0
              ? String(source.count)
              : source.detail,
          checked: source.installed && source.enabled,
          disabled: !source.installed || busy,
          bottomSeparator: "none",
          onChange: (value: boolean) => {
            setPageError("");
            void importAct("setSourceEnabled", { id: source.id, enabled: !!value });
          },
        }),
      );
    const addFolder = () => {
      void showSteamFilePicker(ui, { title: "Add a shortcuts folder", mode: "folder" }).then((path) => {
        if (!path) return;
        setPageError("");
        void importAct("addFolder", { path, includeSubfolders: true });
      });
    };
    const folders = sources.filter((source) => source.kind === "folder");
    const sidebar = h(
      ui.focusable,
      { className: "wsgm-import-sidebar", "flow-children": "column" },
      h("div", { className: "wsgm-import-eyebrow" }, "Sources"),
      ...sources.filter((source) => source.kind !== "folder").map(sourceRow),
      h("div", { className: "wsgm-import-eyebrow wsgm-import-custom" }, "Custom"),
      ...folders.map((source) =>
        h(
          ui.focusable,
          {
            key: `${source.id}-wrap`,
            onOptionsButton: () => void importAct("removeFolder", { id: source.id }),
            onOptionsActionDescription: "Remove folder",
          },
          sourceRow(source),
        ),
      ),
      h(ui.dialogButton, { onClick: addFolder, disabled: busy }, "Add folder…"),
    );

    const selectAllButton = h(
      ui.dialogButton,
      {
        disabled: !entries.length || busy,
        onClick: () => void importAct("selectAll", { selected: !state.selectedCount }),
      },
      state.selectedCount ? "Clear" : "Select all",
    );
    const saveButton = h(
      ui.dialogButtonPrimary,
      {
        disabled: !state.selectedCount || busy,
        onClick: () => {
          setPageError("");
          void importAct("apply");
        },
      },
      state.phase === "applying"
        ? `Saving ${state.progress ?? 0}/${state.progressTotal ?? 0}…`
        : `Save to Steam${state.selectedCount ? ` (${state.selectedCount})` : ""}`,
    );
    // Scan, or Stop while something is running: one place on the bar either way.
    const scanButton = busy
      ? h(ui.dialogButton, { onClick: () => void importAct("cancel") }, "Stop")
      : h(
          ui.dialogButton,
          {
            onClick: () => {
              setPageError("");
              void importAct("scan");
            },
          },
          "Scan",
        );

    // One poster per title, in the artwork type the toolbar shows: the image that will be applied,
    // a check when it is selected, what saving would do, and how it launches under the name.
    const card = (entry: any) => {
      const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === asset);
      const badge = entry.excluded
        ? importExcludedBadge
        : (importActionBadges[entry.action] ?? { label: entry.action, tone: "#3d4450", text: "#dcdedf" });
      const width = importCardWidths[asset] ?? importCardWidths.grid;
      return h(
        "div",
        { key: entry.id, className: "wsgm-import-card", style: { width: `${width}px` } },
        h(ImportCapsule, {
          asset,
          width,
          image: slot?.thumb ?? "",
          placeholder: entry.name,
          dimmed: entry.excluded,
          overlay: [
            entry.selected
              ? h("span", { key: "check", className: "wsgm-import-check" }, importIcon(h, "check", 14))
              : null,
            h(
              "span",
              { key: "badge", className: "wsgm-import-badge", style: { background: badge.tone, color: badge.text } },
              badge.label,
            ),
          ],
          caption: importSlotCaption(slot),
          focus: {
            onActivate: () => void importAct("toggleEntry", { id: entry.id }),
            onOKActionDescription: entry.selectable ? (entry.selected ? "Deselect" : "Select") : undefined,
            onSecondaryButton: () => switchImportLaunch(entry),
            onSecondaryActionDescription:
              entry.routes?.length > 1
                ? "Launch route"
                : !entry.routes?.length && entry.canUseSteamIntegration
                  ? "Launch mode"
                  : undefined,
            onOptionsButton: () => openTitle(entry, asset),
            onOptionsActionDescription: "Title artwork",
            onMenuButton: () => openDetails(entry),
            onMenuActionDescription: "Details",
            onContextMenu: (event: any) => {
              event?.preventDefault?.();
              openDetails(entry);
            },
            onButtonDown: importCycleHandler(entry, asset),
          },
        }),
        h("div", { className: "wsgm-import-name" }, importLaunchIcon(h, entry), h("span", {}, entry.name)),
        h("div", { className: "wsgm-import-launch" }, importLaunchSummary(entry)),
      );
    };

    const gridContent = (test: (entry: any) => boolean) => {
      const shown = entries.filter((entry) => test(entry) && matchesQuery(entry));
      const groups = sources
        .map((source) => ({ source, items: shown.filter((entry) => entry.sourceId === source.id) }))
        .filter((group) => group.items.length);
      return [
        shown.length === 0
          ? h("div", { className: "wsgm-import-muted" }, entries.length ? "Nothing here." : "No games listed yet.")
          : null,
        ...groups.map((group) => {
          const selected = group.items.filter((entry) => entry.selected).length;
          return h(
            "div",
            { key: group.source.id, className: "wsgm-import-group" },
            h(
              "div",
              { className: "wsgm-import-grouphead" },
              h("span", { className: "wsgm-import-eyebrow" }, group.source.name),
              h(
                "span",
                { className: "wsgm-import-muted" },
                `${group.items.length} title${group.items.length === 1 ? "" : "s"} · ${selected} selected`,
              ),
            ),
            h(ui.focusable, { className: "wsgm-import-grid", "flow-children": "grid" }, ...group.items.map(card)),
          );
        }),
      ];
    };

    // The toolbar under the tabs: which artwork the cards show, a search, and the actions.
    const reviewToolbar = h(
      ui.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      h(
        "div",
        { className: "wsgm-import-tool", style: { width: "200px" } },
        importDropdown(ui, {
          label: "Artwork shown",
          rgOptions: importAssets.map((type) => ({ data: type.id, label: type.label })),
          selectedOption: asset,
          onChange: (option: any) => setAsset(option?.data ?? "grid"),
        }),
      ),
      ui.textField
        ? h(
            "div",
            { className: "wsgm-import-search" },
            h(ui.textField, {
              value: query,
              placeholder: "Search titles",
              onChange: (event: any) => setQuery(event?.target?.value ?? ""),
            }),
          )
        : null,
      h("div", { className: "wsgm-import-spacer" }),
      h(ui.dialogButton, { disabled: !entries.length, onClick: () => setView("all") }, "All artwork"),
      scanButton,
      selectAllButton,
      saveButton,
    );

    const review = h(
      "div",
      { className: "wsgm-import-main" },
      h(ui.tabs, {
        autoFocusContents: true,
        activeTab: tab,
        onShowTab: (next: string) => setTab(next),
        tabs: importTabs.map((candidate) => ({
          id: candidate.id,
          title: `${candidate.title} ${entries.filter(candidate.test).length}`,
          content:
            candidate.id === tab
              ? h("div", { className: "wsgm-import-pane" }, reviewToolbar, status, ...gridContent(candidate.test))
              : null,
        })),
      }),
    );

    // Every selected title as a row, one cell per artwork type, so a whole import is dressed without
    // opening titles one by one. The triggers cycle a cell in place.
    const cellWidths: Record<string, number> = { grid: 70, wide: 224, hero: 300, logo: 160, icon: 64 };
    const allArtwork = h(
      ui.focusable,
      { className: "wsgm-import-main", onCancelButton: () => setView("grid"), onCancelActionDescription: "Back" },
      h(
        "div",
        { className: "wsgm-import-pane" },
        h(
          ui.focusable,
          { className: "wsgm-import-bar", "flow-children": "row" },
          h(
            "div",
            { className: "wsgm-import-tool", style: { width: "290px" } },
            importDropdown(ui, {
              label: "Fill every title from",
              rgOptions: [
                { data: "Catalog", label: "Fill from the launcher first" },
                { data: "Providers", label: "Fill from SteamGridDB first" },
              ],
              selectedOption: preference,
              onChange: (option: any) => setFillFrom(option?.data ?? "Catalog"),
            }),
          ),
          h(
            ui.dialogButton,
            { onClick: () => void importAct("fillArtwork", { preference, onlyEmpty: false, asset: "" }) },
            "Fill all",
          ),
          h(
            ui.dialogButton,
            { onClick: () => void importAct("fillArtwork", { preference, onlyEmpty: true, asset: "" }) },
            "Fill empty slots",
          ),
          h(ui.dialogButton, { onClick: () => void importAct("resetArtwork") }, "Reset all"),
          h("div", { className: "wsgm-import-spacer" }),
          h(ui.dialogButton, { onClick: () => setView("grid") }, "Back"),
          saveButton,
        ),
        status,
        h(
          ui.focusable,
          { className: "wsgm-import-colhead", "flow-children": "row" },
          h("div", { style: { width: "170px" }, className: "wsgm-import-eyebrow" }, "Title"),
          ...importAssets.map((type) =>
            h(
              "div",
              { key: type.id, className: "wsgm-import-col", style: { width: `${cellWidths[type.id]}px` } },
              h("span", { className: "wsgm-import-eyebrow" }, type.short),
              h(
                ui.dialogButton,
                {
                  onClick: () => void importAct("fillArtwork", { preference, onlyEmpty: false, asset: type.id }),
                },
                "Fill",
              ),
            ),
          ),
        ),
        allRows.length === 0
          ? h("div", { className: "wsgm-import-muted" }, "Select titles in the review to dress them here.")
          : null,
        h(
          ui.focusable,
          { className: "wsgm-import-rows", "flow-children": "column" },
          ...allRows.map((entry) =>
            h(
              ui.focusable,
              { key: entry.id, className: "wsgm-import-row", "flow-children": "row" },
              h(
                "div",
                { className: "wsgm-import-rowname" },
                h("span", {}, entry.name),
                h("span", { className: "wsgm-import-muted" }, entry.source),
              ),
              ...importAssets.map((type) => {
                const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === type.id);
                return h(ImportCapsule, {
                  key: type.id,
                  asset: type.id,
                  width: cellWidths[type.id],
                  image: slot?.thumb ?? "",
                  placeholder: slot?.kind === "loading" ? "…" : slot?.kind === "keep" ? "Current" : "None",
                  caption: type.id === "icon" ? null : importSlotCaption(slot),
                  focus: {
                    onActivate: () => openTitle(entry, type.id),
                    onOKActionDescription: "All options",
                    onOptionsButton: () => void importAct("clearArtwork", { id: entry.id, asset: type.id }),
                    onOptionsActionDescription: "Clear",
                    onMenuButton: () => openDetails(entry),
                    onMenuActionDescription: "Details",
                    onButtonDown: importCycleHandler(entry, type.id),
                  },
                });
              }),
            ),
          ),
        ),
      ),
    );

    const titleEntry = entries.find((entry) => entry.id === titleId);
    const titleView = titleEntry
      ? h(ImportTitleArtwork, {
          key: `${titleEntry.id}`,
          entry: titleEntry,
          asset: titleAsset,
          onAsset: setTitleAsset,
          onBack: () => setView(allRows.length ? "all" : "grid"),
        })
      : null;
    const shownView = view === "grid" ? "grid" : view === "all" ? "all" : titleView ? "title" : "grid";

    // The heading: the page's name, then where in it the user is and what the view holds.
    const installed = sources.filter((source) => source.installed).length;
    const crumb = (text: string) =>
      h("span", { className: "wsgm-import-crumb" }, text, " ", importIcon(h, "chevron", 18));
    const header =
      shownView === "grid"
        ? h(
            "div",
            { className: "wsgm-import-head" },
            h("h1", {}, "Game Library"),
            h(
              "span",
              { className: "wsgm-import-muted" },
              entries.length
                ? `${installed} source${installed === 1 ? "" : "s"} found · ${entries.length} title${entries.length === 1 ? "" : "s"} · ` +
                    `${state.selectedCount ?? 0} selected`
                : busy
                  ? "Scanning…"
                  : "Scan to find games in your launchers.",
            ),
          )
        : shownView === "all"
          ? h(
              "div",
              { className: "wsgm-import-head" },
              crumb("Game Library"),
              h("h1", {}, "All artwork"),
              h(
                "span",
                { className: "wsgm-import-muted" },
                `${allRows.length} selected title${allRows.length === 1 ? "" : "s"} · applied when you save to Steam`,
              ),
            )
          : h(
              "div",
              { className: "wsgm-import-head" },
              crumb("Game Library"),
              h("h1", {}, titleEntry.name),
              h("span", { className: "wsgm-import-muted" }, "Choose artwork · applied when you save to Steam"),
            );

    return h(
      "div",
      { id: "wsgm-import", "aria-label": "Game Library" },
      h("style", null, importStyles),
      header,
      h(
        "div",
        { className: "wsgm-import-body" },
        shownView === "grid" ? sidebar : null,
        shownView === "grid" ? review : shownView === "all" ? allArtwork : titleView,
      ),
    );
  }
}

// One title's artwork: what is chosen for each type, every candidate for the shown type grouped by
// provider, and the match to fix when the providers found the wrong game.
function ImportTitleArtwork(props: any) {
  const react = importReact;
  const h = react.createElement;
  const ui = importUi;
  const entry = props.entry;
  const [answer, setAnswer] = react.useState(null as any);
  const [search, setSearch] = react.useState("");
  const [matches, setMatches] = react.useState([] as any[]);
  const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === props.asset);
  const slotKey = `${slot?.kind}:${slot?.index}:${slot?.count}:${entry.artworkStatus}`;
  react.useEffect(() => {
    let live = true;
    void importAct("artworkOptions", { id: entry.id, asset: props.asset }).then((next: any) => {
      if (live && next) setAnswer(next);
    });
    return () => {
      live = false;
    };
  }, [entry.id, props.asset, slotKey]);

  const options: any[] = answer?.asset === props.asset ? (answer.options ?? []) : [];
  const providers = options.reduce((groups: any[], option: any) => {
    const group = groups.find((candidate) => candidate.name === option.provider);
    if (group) group.items.push(option);
    else groups.push({ name: option.provider, items: [option] });
    return groups;
  }, []);
  const width = props.asset === "grid" ? 124 : props.asset === "icon" ? 124 : props.asset === "logo" ? 200 : 280;
  const content = h(
    "div",
    { className: "wsgm-import-pane" },
    options.length === 0
      ? h(
          "div",
          { className: "wsgm-import-muted" },
          answer?.status === "ready" || answer?.status === "failed"
            ? "No images were found for this. Fix the match to search for the right game."
            : "Finding images…",
        )
      : null,
    ...providers.map((group: any) =>
      h(
        "div",
        { key: group.name, className: "wsgm-import-group" },
        h(
          "div",
          { className: "wsgm-import-grouphead" },
          h("span", { className: "wsgm-import-eyebrow" }, group.name),
          h("span", { className: "wsgm-import-muted" }, `${group.items.length} image${group.items.length === 1 ? "" : "s"}`),
        ),
        h(
          ui.focusable,
          { className: "wsgm-import-grid", "flow-children": "grid" },
          ...group.items.map((option: any) =>
            h(ImportCapsule, {
              key: option.url,
              asset: props.asset,
              width,
              image: option.thumb,
              placeholder: option.provider,
              overlay:
                answer?.selected > 0 && options[answer.selected - 1]?.url === option.url
                  ? h("span", { className: "wsgm-import-check" }, importIcon(h, "check", 14))
                  : null,
              caption: option.width ? `${option.width} × ${option.height}` : null,
              focus: {
                onActivate: () =>
                  void importAct("pickArtwork", { id: entry.id, asset: props.asset, url: option.url }),
                onOKActionDescription: "Use this",
                onOptionsButton: () => void importAct("clearArtwork", { id: entry.id, asset: props.asset }),
                onOptionsActionDescription: "Use none",
              },
            }),
          ),
        ),
      ),
    ),
  );

  // What each type would get, the shown type highlighted, with a small preview of the image.
  const chosenRows = importAssets.map((type) => {
    const chosen = (entry.artwork ?? []).find((candidate: any) => candidate.asset === type.id);
    const tall = type.id === "grid";
    const size = { width: tall ? "36px" : type.id === "icon" ? "36px" : "60px", height: tall ? "54px" : type.id === "icon" ? "36px" : "28px" };
    return h(
      "div",
      { key: type.id, className: "wsgm-import-chosen", "data-on": String(type.id === props.asset) },
      chosen?.thumb
        ? h("img", { src: chosen.thumb, alt: "", loading: "lazy", draggable: false, style: size })
        : h("span", { className: "wsgm-import-thumb", style: size }),
      h("div", {}, h("span", {}, type.label), h("span", {}, importSlotCaption(chosen) ?? "—")),
    );
  });

  const side = h(
    ui.focusable,
    { className: "wsgm-import-side", "flow-children": "column" },
    h("div", { className: "wsgm-import-eyebrow" }, "Chosen for this title"),
    ...chosenRows,
    h("div", { className: "wsgm-import-rule" }),
    h(
      "div",
      { className: "wsgm-import-muted", style: { lineHeight: 1.45 } },
      entry.matchName ? `Matched as “${entry.matchName}”. Wrong game?` : "Not matched to a game yet.",
    ),
    ui.textField
      ? h(ui.textField, {
          value: search,
          placeholder: "Search for the right game",
          onChange: (event: any) => setSearch(event?.target?.value ?? ""),
        })
      : null,
    h(
      ui.dialogButton,
      {
        onClick: () =>
          void importAct("searchMatch", { id: entry.id, query: search }).then((next: any) =>
            setMatches(next?.matches ?? []),
          ),
      },
      "Fix match",
    ),
    entry.matchFixed
      ? h(
          ui.dialogButton,
          {
            onClick: () => void importAct("setMatch", { id: entry.id, provider: "", gameId: "", name: "" }),
          },
          "Use the automatic match",
        )
      : null,
    h(
      "div",
      { className: "wsgm-import-matches" },
      ...matches.map((match: any) =>
        h(
          ui.dialogButton,
          {
            key: `${match.provider}:${match.id}`,
            onClick: () => {
              setMatches([]);
              void importAct("setMatch", {
                id: entry.id,
                provider: match.provider,
                gameId: match.id,
                name: match.name,
              });
            },
          },
          `${match.name}${match.exact ? "" : " ?"}`,
        ),
      ),
    ),
    h(ui.dialogButton, { onClick: props.onBack }, "Back"),
  );

  return h(
    ui.focusable,
    {
      className: "wsgm-import-main",
      onCancelButton: props.onBack,
      onCancelActionDescription: "Back",
    },
    h(
      "div",
      { className: "wsgm-import-split" },
      side,
      h(
        "div",
        { className: "wsgm-import-main" },
        h(ui.tabs, {
          autoFocusContents: true,
          activeTab: props.asset,
          onShowTab: (next: string) => props.onAsset(next),
          tabs: importAssets.map((type) => ({
            id: type.id,
            title: type.label,
            content: type.id === props.asset ? content : null,
          })),
        }),
      ),
    ),
  );
}

function renderLibraryImportPage(react: any) {
  importReact ??= react;
  return react.createElement(LibraryImportPage, {});
}

function createLibraryImport() {
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let lastError = "";

  const resolve = () => {
    const runtime = getWebpackRuntime("library-import");
    importUi = resolveSteamUiComponents(runtime);
    importClasses = resolveSteamLibraryClasses(runtime);
    // Only what this page actually renders. Requiring a component it never draws would make the
    // gate refuse over something that does not matter. The checkbox and the bare dropdown are
    // wanted, not required: the sidebar falls back to Steam's toggle and the toolbar to its field.
    const required = [
      "react",
      "focusable",
      "toggleField",
      "dropdown",
      "dialogButton",
      "dialogButtonPrimary",
      "tabs",
      "modalRoot",
      "showModal",
    ];
    const missing = required.filter((name) => !importUi?.[name]);
    if (!importClasses) missing.push("library classes");
    if (missing.length) {
      lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
      importUi = null;
      importClasses = null;
      ImportCapsule = null;
      return false;
    }
    ImportCapsule = createSteamCapsule(importUi, importClasses);
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (!attemptResolution(resolve, (error) => (lastError = String(error)))) {
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    unsubscribe = subscribe(LibraryImportPatchId, (state) => {
      importDesired = state;
      importListeners.forEach((listener) => listener(state));
    });
    return { ok: true, installed: true };
  };

  const remove = () => {
    installed = false;
    endSubscription(unsubscribe);
    unsubscribe = null;
    importDesired = null;
    importUi = null;
    importClasses = null;
    ImportCapsule = null;
    return { ok: true };
  };

  const status = () => ({
    installed,
    resolved: !!importUi,
    subscribed: !!unsubscribe,
    checkbox: !!importUi?.checkbox,
    dropdownControl: !!importUi?.dropdownControl,
    entries: importDesired?.entries?.length ?? 0,
    lastError,
  });

  return { install, remove, status };
}

registerSteamPageRenderer("library-import", renderLibraryImportPage);
registerGate("libraryImport", createLibraryImport());
