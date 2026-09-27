// The Game Library's page in Steam: bring games from other launchers into Steam, with their artwork.
//
// Laid out the way Steam ROM Manager lays out its preview, and drawn entirely with Steam's own
// components so it behaves like the rest of Big Picture under a controller: a sidebar of sources
// ticked with Steam's checkbox, Steam's tabs over a grid of Steam library capsules, an all-artwork
// view with one row per title, and one title's artwork. WSGM owns the data and every decision; the
// toolkit owns the capsule, the folder picker and the fail-closed component discovery used here.
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

// What each planned action is called on a card, and its badge colour. An action without an entry
// here is shown by its own name rather than hidden.
const importActionBadges: Record<string, { label: string; tone: string }> = {
  Add: { label: "New", tone: "#1a9fff" },
  Update: { label: "Update", tone: "#d9a441" },
  Artwork: { label: "Artwork", tone: "#d9a441" },
  Adopt: { label: "Adopt", tone: "#1a9fff" },
  Remove: { label: "Remove", tone: "#c2463e" },
  Skip: { label: "Imported", tone: "#3d4450" },
  Conflict: { label: "Edited by hand", tone: "#c2463e" },
};

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

const importStyles = `
#wsgm-import { margin-top: var(--basicui-header-height, 40px); height: calc(100% - var(--basicui-header-height, 40px));
  display: flex; flex-direction: column; background: var(--gpSystemDarkestGrey, #0e141b); color: #dcdedf; }
#wsgm-import .wsgm-import-head { display: flex; align-items: baseline; gap: 16px; padding: 20px 32px 0; flex-wrap: wrap; }
#wsgm-import h1 { margin: 0; font-size: 28px; color: #fff; }
#wsgm-import .wsgm-import-muted { opacity: 0.65; font-size: 14px; }
#wsgm-import .wsgm-import-body { flex: 1; min-height: 0; display: flex; gap: 24px; padding: 12px 32px 0; }
#wsgm-import .wsgm-import-sidebar { width: 260px; flex: 0 0 auto; overflow-y: auto;
  padding-bottom: var(--gamepadui-current-footer-height, 60px); display: flex; flex-direction: column; gap: 2px; }
#wsgm-import .wsgm-import-main { flex: 1; min-width: 0; display: flex; flex-direction: column; }
#wsgm-import .wsgm-import-eyebrow { font-size: 12px; font-weight: 700; letter-spacing: 1.6px; text-transform: uppercase;
  color: #dcdedf; padding: 12px 0 6px; }
#wsgm-import .wsgm-import-bar { display: flex; gap: 8px; align-items: flex-end; flex-wrap: wrap; padding: 8px 0; }
#wsgm-import .wsgm-import-bar > button { width: auto; min-width: auto; white-space: nowrap; }
#wsgm-import .wsgm-import-status { min-height: 20px; font-size: 14px; padding-bottom: 4px; }
#wsgm-import .wsgm-import-status.wsgm-import-error { color: #ff6d6d; }
#wsgm-import .wsgm-import-scroll { flex: 1; min-height: 0; overflow-y: auto;
  padding-bottom: var(--gamepadui-current-footer-height, 60px); }
#wsgm-import .wsgm-import-grid { display: flex; flex-wrap: wrap; gap: 20px 16px; padding: 8px 4px 16px; }
#wsgm-import .wsgm-import-card { display: flex; flex-direction: column; gap: 6px; }
#wsgm-import .wsgm-import-name { font-size: 13px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; opacity: 0.8; }
#wsgm-import .wsgm-import-badge { position: absolute; top: 6px; right: 6px; padding: 2px 7px; border-radius: 2px;
  font-size: 11px; font-weight: 700; text-transform: uppercase; color: #fff; pointer-events: none; }
#wsgm-import .wsgm-import-check { position: absolute; top: 6px; left: 6px; width: 22px; height: 22px; border-radius: 50%;
  box-sizing: border-box; border: 2px solid rgba(255,255,255,0.75); background: rgba(14,20,27,0.35);
  display: flex; align-items: center; justify-content: center; pointer-events: none; color: #fff; font-size: 13px; font-weight: 700; }
#wsgm-import .wsgm-import-check[data-on="true"] { border: none; background: #1a9fff; }
#wsgm-import .wsgm-import-rows { display: flex; flex-direction: column; gap: 10px; padding: 8px 4px 16px; }
#wsgm-import .wsgm-import-row { display: flex; align-items: center; gap: 14px; }
#wsgm-import .wsgm-import-rowname { width: 170px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 2px; }
#wsgm-import .wsgm-import-colhead { display: flex; gap: 14px; align-items: center; padding: 4px 4px 8px; border-bottom: 1px solid #23262e; }
#wsgm-import .wsgm-import-colhead > * { flex: 0 0 auto; }
#wsgm-import .wsgm-import-colhead button { width: auto; min-width: auto; padding: 2px 8px; font-size: 12px; }
#wsgm-import .wsgm-import-split { display: flex; gap: 24px; height: 100%; }
#wsgm-import .wsgm-import-side { width: 260px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 8px; }
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

    const header = h(
      "div",
      { className: "wsgm-import-head" },
      h("h1", {}, view === "all" ? "All artwork" : view === "title" ? "Choose artwork" : "Game Library"),
      h(
        "span",
        { className: "wsgm-import-muted" },
        entries.length
          ? `${state.addCount ?? 0} to add · ${state.updateCount ?? 0} to update · ` +
              `${state.skipCount ?? 0} already imported · ${state.selectedCount ?? 0} selected`
          : busy
            ? "Scanning…"
            : "Scan to find games in your launchers.",
      ),
    );

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
    const Check = ui.checkbox ?? ui.toggleField;
    const sourceRow = (source: any) =>
      h(Check, {
        key: source.id,
        label: source.name,
        description: !source.installed
          ? source.detail || "Not found"
          : source.count >= 0
            ? `${source.count} found${source.kind === "folder" ? ` · ${source.detail}` : ""}`
            : source.detail,
        checked: source.installed && source.enabled,
        disabled: !source.installed || busy,
        onChange: (value: boolean) => {
          setPageError("");
          void importAct("setSourceEnabled", { id: source.id, enabled: !!value });
        },
      });
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
      h("div", { className: "wsgm-import-eyebrow" }, "Custom"),
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
    const stopButton = busy ? h(ui.dialogButton, { onClick: () => void importAct("cancel") }, "Stop") : null;

    // One poster per title, in the artwork type the toolbar shows.
    const card = (entry: any) => {
      const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === asset);
      const badge = entry.excluded
        ? { label: "Left out", tone: "#3d4450" }
        : (importActionBadges[entry.action] ?? { label: entry.action, tone: "#3d4450" });
      const width = asset === "grid" || asset === "icon" ? 150 : asset === "logo" ? 220 : 300;
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
            entry.selectable || entry.selected
              ? h(
                  "span",
                  { key: "check", className: "wsgm-import-check", "data-on": String(!!entry.selected) },
                  entry.selected ? "✓" : "",
                )
              : null,
            h("span", { key: "badge", className: "wsgm-import-badge", style: { background: badge.tone } }, badge.label),
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
        h("div", { className: "wsgm-import-name" }, `${entry.name} · ${importLaunchSummary(entry)}`),
      );
    };

    const matchesQuery = (entry: any) =>
      !query || String(entry.name).toLowerCase().includes(query.toLowerCase());
    const gridContent = (test: (entry: any) => boolean) => {
      const shown = entries.filter((entry) => test(entry) && matchesQuery(entry));
      const groups = sources
        .map((source) => ({ source, items: shown.filter((entry) => entry.sourceId === source.id) }))
        .filter((group) => group.items.length);
      return h(
        "div",
        { className: "wsgm-import-scroll" },
        shown.length === 0
          ? h(
              "div",
              { className: "wsgm-import-muted", style: { padding: "16px 4px" } },
              entries.length ? "Nothing here." : "No games listed yet.",
            )
          : null,
        ...groups.map((group) =>
          h(
            "div",
            { key: group.source.id },
            h(
              "div",
              { className: "wsgm-import-eyebrow" },
              `${group.source.name} · ${group.items.length}`,
            ),
            h(ui.focusable, { className: "wsgm-import-grid", "flow-children": "grid" }, ...group.items.map(card)),
          ),
        ),
      );
    };

    const reviewToolbar = h(
      ui.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      h(
        "div",
        { style: { width: "240px" } },
        h(ui.dropdown, {
          label: "Artwork shown",
          rgOptions: importAssets.map((type) => ({ data: type.id, label: type.label })),
          selectedOption: asset,
          onChange: (option: any) => setAsset(option?.data ?? "grid"),
        }),
      ),
      ui.textField
        ? h(
            "div",
            { style: { width: "200px" } },
            h(ui.textField, {
              label: "Search",
              value: query,
              onChange: (event: any) => setQuery(event?.target?.value ?? ""),
            }),
          )
        : null,
      h(ui.dialogButton, { disabled: !entries.length, onClick: () => setView("all") }, "All artwork"),
      h(
        ui.dialogButton,
        {
          disabled: busy,
          onClick: () => {
            setPageError("");
            void importAct("scan");
          },
        },
        state.phase === "scanning" ? "Scanning…" : "Scan",
      ),
      selectAllButton,
      saveButton,
      stopButton,
    );

    const review = h(
      "div",
      { className: "wsgm-import-main" },
      reviewToolbar,
      status,
      h(ui.tabs, {
        autoFocusContents: true,
        activeTab: tab,
        onShowTab: (next: string) => setTab(next),
        tabs: importTabs.map((candidate) => ({
          id: candidate.id,
          title: `${candidate.title} ${entries.filter(candidate.test).length}`,
          content: candidate.id === tab ? gridContent(candidate.test) : null,
        })),
      }),
    );

    // Every selected title as a row, one cell per artwork type, so a whole import is dressed without
    // opening titles one by one. The triggers cycle a cell in place.
    const cellWidths: Record<string, number> = { grid: 70, wide: 224, hero: 300, logo: 160, icon: 64 };
    const allRows = entries.filter((entry) => entry.selected && matchesQuery(entry));
    const allArtwork = h(
      ui.focusable,
      { className: "wsgm-import-main", onCancelButton: () => setView("grid"), onCancelActionDescription: "Back" },
      h(
        ui.focusable,
        { className: "wsgm-import-bar", "flow-children": "row" },
        h(
          "div",
          { style: { width: "280px" } },
          h(ui.dropdown, {
            label: "Fill every title from",
            rgOptions: [
              { data: "Catalog", label: "The launcher's own images first" },
              { data: "Providers", label: "SteamGridDB first" },
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
        h(ui.dialogButton, { onClick: () => setView("grid") }, "Back to review"),
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
            {
              key: type.id,
              style: {
                width: `${cellWidths[type.id]}px`,
                display: "flex",
                flexDirection: "column",
                gap: "4px",
              },
            },
            h("span", { className: "wsgm-import-eyebrow", style: { padding: 0 } }, type.short),
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
      h(
        "div",
        { className: "wsgm-import-scroll" },
        allRows.length === 0
          ? h(
              "div",
              { className: "wsgm-import-muted", style: { padding: "16px 4px" } },
              "Select titles in the review to dress them here.",
            )
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
                h("span", { style: { color: "#fff" } }, entry.name),
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

    return h(
      "div",
      { id: "wsgm-import", "aria-label": "Game Library" },
      h("style", null, importStyles),
      header,
      h(
        "div",
        { className: "wsgm-import-body" },
        view === "grid" ? sidebar : null,
        view === "grid" ? review : view === "all" ? allArtwork : (titleView ?? review),
      ),
    );
  }
}

// One title's artwork: every candidate for each type, grouped by provider, and the match to fix
// when the providers found the wrong game.
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
  const width = props.asset === "grid" || props.asset === "icon" ? 130 : props.asset === "logo" ? 200 : 280;
  const content = h(
    "div",
    { className: "wsgm-import-scroll" },
    options.length === 0
      ? h(
          "div",
          { className: "wsgm-import-muted", style: { padding: "16px 4px" } },
          answer?.status === "ready" || answer?.status === "failed"
            ? "No images were found for this. Fix the match to search for the right game."
            : "Finding images…",
        )
      : null,
    ...providers.map((group: any) =>
      h(
        "div",
        { key: group.name },
        h("div", { className: "wsgm-import-eyebrow" }, `${group.name} · ${group.items.length}`),
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
                  ? h("span", { className: "wsgm-import-check", "data-on": "true" }, "✓")
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

  const side = h(
    ui.focusable,
    { className: "wsgm-import-side", "flow-children": "column" },
    h("div", { className: "wsgm-import-eyebrow" }, entry.name),
    h(
      "div",
      { className: "wsgm-import-muted" },
      entry.matchName ? `Matched to ${entry.matchName}.` : "Not matched to a game yet.",
    ),
    ui.textField
      ? h(ui.textField, {
          label: "Wrong game? Search",
          value: search,
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
    // gate refuse over something that does not matter. The checkbox is wanted, not required: the
    // sidebar falls back to Steam's toggle.
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
    entries: importDesired?.entries?.length ?? 0,
    lastError,
  });

  return { install, remove, status };
}

registerSteamPageRenderer("library-import", renderLibraryImportPage);
registerGate("libraryImport", createLibraryImport());
