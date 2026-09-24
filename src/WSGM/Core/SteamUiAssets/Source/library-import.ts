// The Game Library's page in Steam: bring games from other launchers into Steam.
//
// Rendered entirely with Steam's own component exports, so it behaves like the rest of Big Picture
// under a controller. WSGM owns the data and every decision; the toolkit owns only the fail-closed
// component discovery used here.
const LibraryImportPatchId = "steam-ui.library-import";
let importUi: any = null;
let importDesired: any = null;
const importListeners = new Set<(state: any) => void>();

const sendImportCommand = (command: string, payload: any = {}) =>
  request(LibraryImportPatchId, command, payload, nextActionGeneration(LibraryImportPatchId));

// What each planned action is called on screen, and whether it reads as a warning. An action
// without an entry here is shown by its own name rather than hidden.
const importActionLabels: Record<string, string> = {
  Add: "Add",
  Update: "Update",
  Adopt: "Adopt",
  Remove: "Remove",
  Skip: "Skip",
  Conflict: "Edited by hand",
};

const importModeLabels: Record<string, string> = {
  ControllerOnly: "Controller only",
  SteamIntegration: "Steam overlay",
};

const importStyles = `
#wsgm-import { padding: 24px 32px 48px; display: flex; flex-direction: column; gap: 16px; }
#wsgm-import .wsgm-import-head { display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap; }
#wsgm-import h1 { margin: 0; font-size: 28px; }
#wsgm-import .wsgm-import-counts { opacity: 0.7; font-size: 14px; }
#wsgm-import .wsgm-import-bar { display: flex; gap: 8px; flex-wrap: wrap; }
#wsgm-import .wsgm-import-status { min-height: 20px; font-size: 14px; }
#wsgm-import .wsgm-import-status.wsgm-import-error { color: #ff6d6d; }
#wsgm-import .wsgm-import-list { display: flex; flex-direction: column; gap: 6px; }
#wsgm-import .wsgm-import-row { display: flex; align-items: center; gap: 12px; padding: 10px 14px;
  border-radius: 4px; background: rgba(255,255,255,0.04); }
#wsgm-import .wsgm-import-row[data-selected="true"] { background: rgba(255,255,255,0.12); }
#wsgm-import .wsgm-import-row[data-selectable="false"] { opacity: 0.55; }
#wsgm-import .wsgm-import-name { flex: 1 1 auto; min-width: 0; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap; }
#wsgm-import .wsgm-import-chips { display: flex; gap: 6px; flex: 0 0 auto; }
#wsgm-import .wsgm-import-chip { font-size: 12px; padding: 2px 8px; border-radius: 10px;
  background: rgba(255,255,255,0.1); white-space: nowrap; }
#wsgm-import .wsgm-import-chip[data-warn="true"] { background: rgba(255,109,109,0.22); }
#wsgm-import .wsgm-import-detail { display: flex; flex-direction: column; gap: 10px; padding: 8px 4px; }
#wsgm-import .wsgm-import-detail dt { opacity: 0.7; font-size: 13px; }
#wsgm-import .wsgm-import-detail dd { margin: 0 0 8px; font-size: 14px; word-break: break-word; }
#wsgm-import .wsgm-import-risk { display: flex; flex-direction: column; gap: 12px; }
#wsgm-import .wsgm-import-risk p { margin: 0; line-height: 1.45; }
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

// The ban-risk acknowledgement. Deliberately a modal with its own toggle rather than a switch on
// the row: the user is accepting a risk to their account, and that should not be one press away
// from a list they are scrolling through.
const renderImportRiskModal = (entry: any, close: () => void) => {
  const react = importUi.react;
  let accepted = false;
  const Body = () => {
    const [checked, setChecked] = react.useState(false);
    accepted = checked;
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
        react.createElement(
          importUi.dialogButton,
          { onClick: close, onActivate: close },
          "Keep controller only",
        ),
        react.createElement(
          importUi.dialogButtonPrimary,
          {
            disabled: !checked,
            onClick: () => {
              if (!accepted) return;
              void sendImportCommand("setMode", {
                id: entry.id,
                mode: "SteamIntegration",
                acknowledged: true,
              });
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

// The entry's own sheet: what the review knows about it, and what can be done to it one entry at a
// time. Everything here is also refused by the host when it does not apply, so an action shown for a
// stale entry fails with a reason rather than acting on the wrong title.
const renderImportDetailModal = (entry: any, close: () => void) => {
  const react = importUi.react;
  const act = (command: string) => {
    void sendImportCommand(command, { id: entry.id });
    close();
  };
  // The artwork stage. The host opens the page for this entry's shortcut first and answers with its
  // route, exactly as the game menu's Change Artwork does; Steam's own back returns here.
  const openArtwork = () => {
    close();
    void sendImportCommand("openArtwork", { id: entry.id }).then(
      (answer: any) => {
        if (answer?.route) navigateSteamRoute(answer.route);
      },
      () => {},
    );
  };
  const actions = [
    entry.appId > 0 && entry.action !== "Remove"
      ? { label: "Change artwork…", command: "", run: openArtwork }
      : null,
    entry.excluded
      ? { label: "Offer again", command: "include" }
      : entry.action === "Add" || entry.action === "Adopt"
        ? { label: "Don't import", command: "exclude" }
        : null,
  ].filter((action) => action !== null) as { label: string; command: string; run?: () => void }[];
  const rows: [string, string][] = [
    ["Launch route", entry.launchLabel],
    ["Why", entry.launchEvidence],
    ["Multiplayer", entry.multiplayer],
    ["Why", entry.multiplayerEvidence],
    ["This sync would", `${importActionLabels[entry.action] ?? entry.action}: ${entry.reason}`],
    [
      "Store artwork",
      `${entry.artworkOffered ?? 0} offered` +
        (entry.artworkApplied === null || entry.artworkApplied === undefined
          ? ""
          : `, ${entry.artworkApplied} applied`),
    ],
    ["Identity", entry.identity],
    ["Installed at", entry.installPath || "unknown"],
  ];
  return react.createElement(
    "dl",
    { className: "wsgm-import-detail" },
    ...rows.flatMap(([term, value], index) => [
      react.createElement("dt", { key: `t${index}` }, term),
      react.createElement("dd", { key: `d${index}` }, value || "—"),
    ]),
    ...(entry.notes ?? []).map((note: string, index: number) =>
      react.createElement("dd", { key: `n${index}` }, note),
    ),
    actions.length
      ? react.createElement(
          importUi.focusable,
          { key: "actions", className: "wsgm-import-bar", "flow-children": "row" },
          ...actions.map((action) =>
            react.createElement(
              importUi.dialogButton,
              {
                key: action.label,
                onActivate: action.run ?? (() => act(action.command)),
                onClick: action.run ?? (() => act(action.command)),
              },
              action.label,
            ),
          ),
        )
      : null,
  );
};

const renderImportRow = (entry: any) => {
  const react = importUi.react;
  const chips = [
    // The source names its own route; the page only marks one that has no validated launcher.
    { text: entry.launchLabel, warn: !entry.launchValidated },
    { text: importModeLabels[entry.mode] ?? entry.mode, warn: false },
    // What the Store's artwork did: offered before an import, applied after one.
    {
      text:
        entry.artworkApplied === null || entry.artworkApplied === undefined
          ? entry.artworkOffered
            ? `Store art: ${entry.artworkOffered}`
            : "No Store art"
          : `Art: ${entry.artworkApplied} of ${entry.artworkOffered}`,
      warn: false,
    },
    entry.excluded
      ? { text: "Not importing", warn: false }
      : {
          text: importActionLabels[entry.action] ?? entry.action,
          warn: entry.action === "Conflict" || entry.action === "Remove",
        },
  ];

  const openMode = () => {
    // Switching to the overlay route for a multiplayer title goes through the risk modal. The host
    // refuses it without an acknowledgement regardless, so this is the way in, not the guard.
    if (entry.mode === "SteamIntegration") {
      void sendImportCommand("setMode", {
        id: entry.id,
        mode: "ControllerOnly",
        acknowledged: false,
      });
      return;
    }
    if (!entry.canUseSteamIntegration) return;
    if (entry.requiresAcknowledgement) {
      showImportModal((close) => renderImportRiskModal(entry, close), entry.name);
      return;
    }
    void sendImportCommand("setMode", {
      id: entry.id,
      mode: "SteamIntegration",
      acknowledged: false,
    });
  };

  const openDetails = () =>
    showImportModal((close) => renderImportDetailModal(entry, close), entry.name);

  return react.createElement(
    importUi.focusable,
    {
      key: entry.id,
      className: "wsgm-import-row",
      "data-selected": String(!!entry.selected),
      "data-selectable": String(!!entry.selectable),
      onActivate: () => void sendImportCommand("toggleEntry", { id: entry.id }),
      onOKActionDescription: entry.selectable ? (entry.selected ? "Deselect" : "Select") : undefined,
      onSecondaryButton: openMode,
      onSecondaryActionDescription: entry.canUseSteamIntegration ? "Launch mode" : undefined,
      onMenuButton: openDetails,
      onMenuActionDescription: "Details and actions",
      onContextMenu: openDetails,
    },
    react.createElement("div", { className: "wsgm-import-name" }, entry.name),
    react.createElement(
      "div",
      { className: "wsgm-import-chips" },
      ...chips.map((chip, index) =>
        react.createElement(
          "span",
          { key: index, className: "wsgm-import-chip", "data-warn": String(chip.warn) },
          chip.text,
        ),
      ),
    ),
  );
};

// React comes from the router when the gate has not resolved yet, so the route always carries a
// component: one built with a null child stays null until the routes are rebuilt, and the page
// opened blank when the gate resolved a second after the routes did (2026-09-24).
function renderLibraryImportPage(routerReact: any) {
  const react = importUi?.react ?? routerReact;
  if (!react) return null;

  const Page = () => {
    const [, setRevision] = react.useState(0);
    react.useEffect(() => {
      const listener = () => setRevision((value: number) => value + 1);
      importListeners.add(listener);
      return () => importListeners.delete(listener);
    }, []);

    // After the hooks, so a render before the gate resolves calls the same ones as one after.
    if (!importUi) return react.createElement("div", { className: "sgdb-loading" }, "Loading…");

    const state = importDesired ?? {};
    const entries = state.entries ?? [];
    const busy = !!state.loading;
    const applyLabel = state.selectedCount ? `Apply (${state.selectedCount})` : "Apply";

    return react.createElement(
      "div",
      { id: "wsgm-import" },
      react.createElement("style", {}, importStyles),
      react.createElement(
        "div",
        { className: "wsgm-import-head" },
        react.createElement("h1", {}, "Game Library"),
        react.createElement(
          "span",
          { className: "wsgm-import-counts" },
          (state.sources ?? []).length ? `From ${state.sources.join(", ")}` : "",
        ),
        react.createElement(
          "span",
          { className: "wsgm-import-counts" },
          entries.length
            ? `${state.addCount ?? 0} to add, ${state.updateCount ?? 0} to update, ` +
                `${state.skipCount ?? 0} already imported`
            : "",
        ),
      ),
      react.createElement(
        importUi.focusable,
        { className: "wsgm-import-bar", "flow-children": "row" },
        react.createElement(
          importUi.dialogButton,
          { disabled: busy, onClick: () => void sendImportCommand("scan") },
          busy && state.phase === "scanning" ? "Scanning…" : "Scan",
        ),
        react.createElement(
          importUi.dialogButton,
          {
            disabled: !entries.length || busy,
            onClick: () => void sendImportCommand("selectAll", { selected: !state.selectedCount }),
          },
          state.selectedCount ? "Clear" : "Select all",
        ),
        react.createElement(
          importUi.dialogButtonPrimary,
          {
            disabled: !state.selectedCount || busy || !state.launcherAvailable,
            onClick: () => void sendImportCommand("apply"),
          },
          state.phase === "applying"
            ? `Applying ${state.progress ?? 0}/${state.progressTotal ?? 0}…`
            : applyLabel,
        ),
        busy
          ? react.createElement(
              importUi.dialogButton,
              { onClick: () => void sendImportCommand("cancel") },
              "Stop",
            )
          : null,
      ),
      react.createElement(
        "div",
        {
          className: `wsgm-import-status${state.error ? " wsgm-import-error" : ""}`,
        },
        state.error || state.launcherDetail || state.notice || "",
      ),
      react.createElement(
        "div",
        { className: "wsgm-import-list" },
        ...entries.map((entry: any) => renderImportRow(entry)),
      ),
    );
  };

  return react.createElement(Page, {});
}

function createLibraryImport() {
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let lastError = "";

  const resolve = () => {
    const runtime = getWebpackRuntime("library-import");
    importUi = resolveSteamUiComponents(runtime);
    // Only what this page actually renders. Requiring a component it never draws would make the
    // gate refuse over something that does not matter.
    const required = [
      "react",
      "focusable",
      "toggleField",
      "dialogButton",
      "dialogButtonPrimary",
      "modalRoot",
      "showModal",
    ];
    const missing = required.filter((name) => !importUi?.[name]);
    if (missing.length) {
      lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
      importUi = null;
      return false;
    }
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
    return { ok: true };
  };

  const status = () => ({
    installed,
    resolved: !!importUi,
    subscribed: !!unsubscribe,
    entries: importDesired?.entries?.length ?? 0,
    lastError,
  });

  return { install, remove, status };
}

registerSteamPageRenderer("library-import", renderLibraryImportPage);
registerGate("libraryImport", createLibraryImport());
