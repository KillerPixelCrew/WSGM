// Library-import frontend; shared host services own discovery, review decisions and writes.
const LibraryImportPatchId = "wsgm.library-import";

// Resolved once the gate holds; the modals are drawn outside the page's tree and read them here.
let importUi: any = null;
let ImportCapsule: any = null;
let ImportCardType: any = null;

// Modals hear about new state and page messages through these; the page is their only notifier.
const importStateListeners = new Set<(state: any) => void>();
const importReporters = new Set<(message: { text: string; error: boolean } | null) => void>();
let importLatest: any = null;

const importReport = (message: { text: string; error: boolean } | null) => {
  for (const reporter of [...importReporters]) reporter(message);
};

/**
 * Sends an import command and presents request rejection through the page reporters.
 * @param command Allowlisted import command.
 * @param payload JSON action arguments.
 * @returns The backend result, or undefined after reporting a rejection; clears the previous message first.
 */
const importAct = (command: string, payload: any = {}) => {
  importReport(null);
  return request(LibraryImportPatchId, command, payload).catch((error: any) => {
    importReport({ text: String(error?.message ?? error), error: true });
    return undefined;
  });
};

const importAssets = [
  { id: "grid", label: "Portrait capsule", short: "Portrait", card: 150, cell: 70, option: 124, thumb: [36, 54] },
  { id: "wide", label: "Wide capsule", short: "Wide", card: 300, cell: 224, option: 280, thumb: [60, 28] },
  { id: "hero", label: "Hero", short: "Hero", card: 300, cell: 300, option: 280, thumb: [60, 28] },
  { id: "logo", label: "Logo", short: "Logo", card: 220, cell: 160, option: 200, thumb: [60, 28] },
  { id: "icon", label: "Icon", short: "Icon", card: 150, cell: 64, option: 124, thumb: [36, 36] },
];
const importAsset = (id: string) => importAssets.find((asset) => asset.id === id) ?? importAssets[0];

// The tabs, by the group the host puts each title in. Membership is the host's, so the overlay and
// this page cannot disagree about what "needs attention" means.
const importTabs = [
  { id: "all", title: "All", group: "" },
  { id: "new", title: "New", group: "new" },
  { id: "imported", title: "Imported", group: "imported" },
  { id: "attention", title: "Needs attention", group: "attention" },
  { id: "excluded", title: "Left out", group: "excluded" },
];

// Badge colours by action. The words are the host's; only the colours are this page's.
const importBadgeTones: Record<string, { tone: string; text: string }> = {
  Add: { tone: "#1a9fff", text: "#ffffff" },
  Adopt: { tone: "#1a9fff", text: "#ffffff" },
  Update: { tone: "#d9a441", text: "#1a1206" },
  Artwork: { tone: "#d9a441", text: "#1a1206" },
  Remove: { tone: "#c2463e", text: "#ffffff" },
  Conflict: { tone: "#c2463e", text: "#ffffff" },
  Skip: { tone: "#3d4450", text: "#dcdedf" },
};
const importExcludedTone = { tone: "rgba(14,20,27,0.85)", text: "#b8bcbf" };

// The page's glyphs, as path data Steam's own convention draws: filled with the text colour, holes
// cut with the even-odd rule, sized by the page's CSS.
const importGlyphs = {
  check: "M9.6 15.6 5.4 11.4 4 12.8l5.6 5.6L20 8l-1.4-1.4z",
  controller:
    "M7.5 7h9A5.5 5.5 0 0 1 22 12.5v1.9a3.1 3.1 0 0 1-5.5 2L14.8 14.5H9.2L7.5 16.4A3.1 3.1 0 0 1 2 14.4v-1.9A5.5 5.5 0 0 1 7.5 7zM6.3 9.8v1.5H4.8v1.6h1.5v1.5h1.6v-1.5h1.5v-1.6H7.9V9.8zM15.5 10.2h1.6v1.6h-1.6zM17.6 12.3h1.6v1.6h-1.6z",
  overlay: "M3 4h18a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1h-7v2h3v2H7v-2h3v-2H3a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zm1 5v7h16V9z",
  launcher: "M4 11h11.2l-4.6-4.6L12 5l7 7-7 7-1.4-1.4 4.6-4.6H4z",
  chevron: "M9 5.6 10.4 4.2l7.8 7.8-7.8 7.8L9 18.4l6.4-6.4z",
};
const importGlyph = (react: any, name: keyof typeof importGlyphs) => renderSteamGlyph(react, importGlyphs[name]);

// The page's own layout over Steam's components. Steam's stable class names (DialogCheckbox,
// DialogLabel, DialogInput, DialogButton) are what the compacting rules below hang on; the hashed
// ones are never named.
const importStyles = `
#wsgm-import { margin-top: var(--basicui-header-height, 40px); height: calc(100% - var(--basicui-header-height, 40px));
  display: flex; flex-direction: column; background: var(--gpSystemDarkestGrey, #0e141b); color: #dcdedf; }
#wsgm-import .wsgm-import-head { display: flex; align-items: baseline; gap: 14px; padding: 24px 48px 0; flex-shrink: 0; }
#wsgm-import .wsgm-import-head h1 { margin: 0; font-size: 30px; font-weight: 700; color: #fff; letter-spacing: 0.2px; }
#wsgm-import .wsgm-import-crumb { font-size: 30px; font-weight: 700; color: #8b929a; }
#wsgm-import .wsgm-import-crumb svg { width: 18px; height: 18px; margin: 0 -2px 2px 0; vertical-align: middle; }
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
#wsgm-import .wsgm-import-status { display: flex; flex-direction: column; gap: 4px; font-size: 14px; color: #b8bcbf; }
#wsgm-import .wsgm-import-status:empty { display: none; }
#wsgm-import .wsgm-import-status .wsgm-import-error { color: #ff6d6d; }
#wsgm-import .wsgm-import-group { display: flex; flex-direction: column; gap: 14px; margin-bottom: 8px; }
#wsgm-import .wsgm-import-grouphead { display: flex; align-items: baseline; gap: 14px; }
#wsgm-import .wsgm-import-grouphead .wsgm-import-eyebrow { padding: 0; }
#wsgm-import .wsgm-import-grid { display: flex; flex-wrap: wrap; gap: 22px 14px; padding: 4px; }
#wsgm-import .wsgm-import-card { display: flex; flex-direction: column; gap: 8px; }
#wsgm-import .wsgm-import-name { display: flex; align-items: center; gap: 6px; font-size: 13px; color: #b8bcbf; }
#wsgm-import .wsgm-import-name svg { flex: 0 0 auto; width: 16px; height: 16px; }
#wsgm-import .wsgm-import-name span { min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-launch { font-size: 12px; color: #8b929a; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-badge { position: absolute; top: 8px; right: 8px; padding: 3px 8px; border-radius: 2px;
  font-size: 11px; font-weight: 700; letter-spacing: 0.4px; text-transform: uppercase; pointer-events: none; }
#wsgm-import .wsgm-import-check { position: absolute; top: 8px; left: 8px; width: 24px; height: 24px; border-radius: 50%;
  background: #1a9fff; color: #ffffff; display: flex; align-items: center; justify-content: center; pointer-events: none;
  box-shadow: 0 1px 4px rgba(0,0,0,0.5); }
#wsgm-import .wsgm-import-check svg { width: 14px; height: 14px; }
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
#wsgm-import .wsgm-import-matches .wsgm-import-eyebrow { padding: 8px 0 2px; }
.wsgm-import-sheet { display: flex; flex-direction: column; gap: 6px; min-width: min(680px, 80vw); }
.wsgm-import-sheet .wsgm-import-sheethead { display: flex; align-items: center; gap: 16px; margin-bottom: 8px; }
.wsgm-import-sheet .wsgm-import-sheethead img, .wsgm-import-sheet .wsgm-import-sheethead .wsgm-import-thumb { width: 60px; height: 90px;
  border-radius: 2px; object-fit: cover; background: rgba(255,255,255,0.06); flex: 0 0 auto; }
.wsgm-import-sheet .wsgm-import-sheethead div { display: flex; flex-direction: column; gap: 6px; }
.wsgm-import-sheet .wsgm-import-sheethead span:last-child { font-size: 14px; color: #8b929a; }
.wsgm-import-sheet .wsgm-import-fact { display: flex; align-items: flex-start; justify-content: space-between; gap: 24px;
  padding: 12px 0; border-top: 1px solid #2f343c; }
.wsgm-import-sheet .wsgm-import-fact > span:first-child { font-size: 15px; color: #dcdedf; flex-shrink: 0; }
.wsgm-import-sheet .wsgm-import-fact > span:last-child { font-size: 14px; color: #8b929a; text-align: right; line-height: 1.4;
  max-width: 420px; word-break: break-word; }
.wsgm-import-sheet .wsgm-import-muted { font-size: 13px; color: #8b929a; }
.wsgm-import-sheet .wsgm-import-note { font-size: 14px; color: #8b929a; line-height: 1.45; margin: 0 0 12px; }
.wsgm-import-sheet .wsgm-import-error { color: #ff6d6d; font-size: 14px; }
.wsgm-import-sheet .wsgm-import-bar { display: flex; gap: 10px; padding-top: 18px; }
.wsgm-import-sheet .wsgm-import-bar > .DialogButton { width: auto; min-width: auto; }
.wsgm-import-sheet .wsgm-import-spacer { flex: 1; }
.wsgm-import-sheet .wsgm-import-path { max-width: 280px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.wsgm-import-risk p { margin: 0 0 12px; line-height: 1.45; }
`;

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
      // A pick that is no longer among the candidates has no position to show.
      return slot.index > 0 ? `${slot.provider} ${slot.index}/${slot.count}` : slot.provider;
  }
};

// Why a title has no images, when the host said: a provider that failed or none that is set up.
const importArtworkLine = (entry: any) =>
  entry.artworkStatus === "failed" || entry.artworkStatus === "unavailable"
    ? entry.artworkDetail || "The artwork providers could not be asked."
    : entry.artworkStatus === "notFound"
      ? "No provider had images for this. Fix the match to search for the right game."
      : "";

// The triggers cycle a slot's image in place, the way SRM's arrows do.
const importCycle = (id: string, asset: string) =>
  onSteamTriggers((delta) => void importAct("cycleArtwork", { id, asset, delta }));

// The ban-risk acknowledgement. Deliberately a modal with its own checkbox rather than a switch on
// the card: the user is accepting a risk to their account, and that should not be one press away
// from a grid they are moving through.
function ImportRiskBody({ entry, close }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const [checked, setChecked] = react.useState(false);
  return h(
    "div",
    { className: "wsgm-import-sheet wsgm-import-risk" },
    h(
      "p",
      {},
      `${entry.name} is marked as a multiplayer title. The Steam overlay route loads Steam's overlay into ` +
        "the running game. Anti-cheat compatibility has not been established for any title, and some " +
        "anti-cheat systems treat that as tampering.",
    ),
    h("p", {}, "Controller-only support injects nothing and is the safer choice for a multiplayer game."),
    h(importUi.toggleField, {
      label: "I understand this may risk a ban on this title",
      checked,
      controlled: true,
      onChange: (value: boolean) => setChecked(!!value),
    }),
    h(
      importUi.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      h(importUi.dialogButton, { onClick: close }, "Keep controller only"),
      h(
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
}

const showImportRisk = (entry: any) =>
  showSteamModal(importUi, {
    title: entry.name,
    render: (close) => importUi.react.createElement(ImportRiskBody, { entry, close }),
  });

// X on a card: the host moves the title to its next mode or route, and says when that needs the
// risk accepted first.
const cycleImportLaunch = (entry: any) =>
  void importAct("cycleLaunch", { id: entry.id }).then((answer: any) => {
    if (answer?.acknowledge) showImportRisk(entry);
  });

// The entry's own sheet, as the mockup lays it out. It follows the live state, so a change made
// here or anywhere else is what it shows next, and the evidence behind the title is asked for once.
function ImportDetailsBody({ id, close, openTitle }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const [state, setState] = react.useState(importLatest);
  const [details, setDetails] = react.useState(null as any);
  react.useEffect(() => {
    const listener = (next: any) => setState(next);
    importStateListeners.add(listener);
    void importAct("details", { id }).then((answer: any) => answer && setDetails(answer));
    return () => importStateListeners.delete(listener);
  }, [id]);

  const entry = (state?.entries ?? []).find((candidate: any) => candidate.id === id);
  if (!entry) {
    return h(
      "div",
      { className: "wsgm-import-sheet" },
      h("p", { className: "wsgm-import-note" }, "This title is no longer listed."),
      h(importUi.dialogButton, { onClick: close }, "Close"),
    );
  }

  const launchControl = !entry.editable
    ? null
    : entry.packaged
      ? h(importUi.dropdown, {
          label: "Launch mode",
          description: entry.requiresAcknowledgement
            ? "Multiplayer title. The Steam overlay route loads Steam into the game and needs you to accept " +
              "the ban risk first."
            : details?.launchEvidence,
          rgOptions: [
            { data: "ControllerOnly", label: "Controller only" },
            ...(entry.canUseSteamIntegration ? [{ data: "SteamIntegration", label: "Steam overlay" }] : []),
          ],
          selectedOption: entry.mode,
          onChange: (option: any) => {
            const mode = option?.data;
            if (!mode || mode === entry.mode) return;
            if (mode === "SteamIntegration" && entry.requiresAcknowledgement && !entry.acknowledged) {
              showImportRisk(entry);
              return;
            }
            void importAct("setMode", { id: entry.id, mode, acknowledged: entry.acknowledged });
          },
        })
      : entry.routes.length > 1
        ? h(importUi.dropdown, {
            label: "Launch route",
            description: details?.launchEvidence,
            rgOptions: entry.routes.map((route: any) => ({ data: route.id, label: route.label })),
            selectedOption: entry.route,
            onChange: (option: any) => {
              if (option?.data && option.data !== entry.route) {
                void importAct("setRoute", { id: entry.id, route: option.data });
              }
            },
          })
        : null;

  const artwork = (entry.artwork ?? [])
    .map((slot: any) => `${importAsset(slot.asset).short}: ${importSlotCaption(slot) ?? "none"}`)
    .join(" · ");
  const facts: [string, string][] = [
    ["Launch route", details ? `${entry.launchLabel}. ${details.launchEvidence}` : entry.launchLabel],
    ["Multiplayer", details ? `${details.multiplayer}. ${details.multiplayerEvidence}` : "…"],
    ["Saving would", `${entry.actionLabel}: ${entry.reason}`],
    ["Artwork", artwork || importArtworkLine(entry) || "—"],
    ["Matched to", entry.matchName ? `${entry.matchName}${entry.matchFixed ? " (fixed)" : ""}` : "—"],
    ["Installed at", details?.installPath || "unknown"],
    ["Identity", details?.identity ?? "…"],
    ...((details?.notes ?? []) as string[]).map((note: string): [string, string] => ["Note", note]),
  ];
  const grid = (entry.artwork ?? []).find((slot: any) => slot.asset === "grid");
  const actions = [
    entry.editable ? { label: "Choose artwork…", run: () => (close(), openTitle(entry, "grid")) } : null,
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
      ? { label: "Offer again", run: () => void importAct("include", { id: entry.id }) }
      : entry.action === "Add" || entry.action === "Adopt"
        ? { label: "Don't import", run: () => (void importAct("exclude", { id: entry.id }), close()) }
        : null,
  ].filter((action) => action !== null) as { label: string; run: () => void }[];

  return h(
    "div",
    { className: "wsgm-import-sheet" },
    h(
      "div",
      { className: "wsgm-import-sheethead" },
      grid?.thumb
        ? h("img", { src: grid.thumb, alt: "", draggable: false })
        : h("span", { className: "wsgm-import-thumb" }),
      h(
        "div",
        {},
        h("h2", { style: { margin: 0 } }, entry.name),
        h(
          "span",
          {},
          `${entry.source} · ${entry.actionLabel} · ${entry.appId > 0 ? "in Steam" : "not in Steam yet"}`,
        ),
      ),
    ),
    launchControl,
    ...facts.map(([term, value], index) =>
      h(
        "div",
        { key: `${term}${index}`, className: "wsgm-import-fact" },
        h("span", {}, term),
        h("span", {}, value || "—"),
      ),
    ),
    h(
      importUi.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      ...actions.map((action) => h(importUi.dialogButton, { key: action.label, onClick: action.run }, action.label)),
      h("div", { className: "wsgm-import-spacer" }),
      h(importUi.dialogButton, { onClick: close }, "Close"),
    ),
  );
}

// The mockup's "Add a shortcuts folder" sheet: the folder, whether its subfolders are read too,
// and which file types it offers.
function ImportFolderBody({ close }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const [path, setPath] = react.useState("");
  const [subfolders, setSubfolders] = react.useState(true);
  const [types, setTypes] = react.useState(".lnk .url .exe");
  const [error, setError] = react.useState("");
  const choose = () =>
    void showSteamFilePicker(importUi, { title: "Choose the shortcuts folder", mode: "folder" }).then(
      (chosen) => chosen && setPath(chosen),
    );
  const extensions = types
    .split(/[\s,;]+/)
    .map((type: string) => type.trim())
    .filter((type: string) => type.length > 0);
  const add = () => {
    setError("");
    void request(LibraryImportPatchId, "addFolder", { path, includeSubfolders: subfolders, extensions }).then(
      () => close(),
      (failure: any) => setError(String(failure?.message ?? failure)),
    );
  };
  return h(
    "div",
    { className: "wsgm-import-sheet" },
    h(
      "p",
      { className: "wsgm-import-note" },
      "Every shortcut in the folder becomes a title. The folder appears in the sidebar and is scanned with the launchers.",
    ),
    h(
      importUi.focusable,
      { className: "wsgm-import-fact", "flow-children": "row" },
      h(
        "div",
        { style: { display: "flex", flexDirection: "column", gap: "4px" } },
        h("span", {}, "Folder"),
        h("span", { className: "wsgm-import-muted" }, "Pick a drive and folder."),
      ),
      h(importUi.dialogButton, { onClick: choose, style: { width: "280px" } },
        h("span", { className: "wsgm-import-path" }, path || "Choose…"),
      ),
    ),
    h(importUi.dropdown, {
      label: "Include subfolders",
      description: "Scan folders inside it too.",
      rgOptions: [
        { data: true, label: "Yes" },
        { data: false, label: "No" },
      ],
      selectedOption: subfolders,
      onChange: (option: any) => setSubfolders(option?.data !== false),
    }),
    importUi.textField
      ? h(importUi.textField, {
          label: "File types",
          description: "Separated by spaces.",
          value: types,
          maxLength: 32,
          onChange: (event: any) => setTypes(event?.target?.value ?? ""),
        })
      : null,
    error ? h("div", { className: "wsgm-import-error" }, error) : null,
    h(
      importUi.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      h("div", { className: "wsgm-import-spacer" }),
      h(importUi.dialogButton, { onClick: close }, "Cancel"),
      h(importUi.dialogButtonPrimary, { disabled: !path || extensions.length === 0, onClick: add }, "Add source"),
    ),
  );
}

// One poster in the grid. Memoized on what it draws: after a scan a publication changes a few
// titles, and redrawing every card for it was what made the D-pad lag on a large library.
function ImportCard({ entry, slot, asset, actions, returning }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const tone = entry.excluded ? importExcludedTone : (importBadgeTones[entry.action] ?? importBadgeTones.Skip);
  const width = importAsset(asset).card;
  const launchGlyph = entry.packaged
    ? importGlyph(react, entry.mode === "SteamIntegration" ? "overlay" : "controller")
    : entry.follows
      ? importGlyph(react, "launcher")
      : null;
  const canCycle = entry.editable && (entry.packaged ? entry.canUseSteamIntegration : entry.routes.length > 1);
  return h(
    "div",
    { className: "wsgm-import-card", style: { width: `${width}px` } },
    h(ImportCapsule, {
      asset,
      width,
      image: slot?.thumb ?? "",
      placeholder: entry.name,
      dimmed: entry.excluded,
      overlay: [
        entry.selected ? h("span", { key: "check", className: "wsgm-import-check" }, importGlyph(react, "check")) : null,
        h(
          "span",
          { key: "badge", className: "wsgm-import-badge", style: { background: tone.tone, color: tone.text } },
          entry.actionLabel,
        ),
      ],
      caption: importSlotCaption(slot),
      focus: {
        // Back from a title's artwork lands on the card it was opened from.
        autoFocus: returning,
        // A card that cannot be ticked says why rather than doing nothing.
        onActivate: () =>
          entry.selectable ? actions.toggle(entry) : importReport({ text: entry.reason, error: false }),
        onOKActionDescription: entry.selectable ? (entry.selected ? "Deselect" : "Select") : "Why not",
        onSecondaryButton: canCycle ? () => cycleImportLaunch(entry) : undefined,
        onSecondaryActionDescription: canCycle ? (entry.packaged ? "Launch mode" : "Launch route") : undefined,
        onOptionsButton: entry.editable ? () => actions.openTitle(entry, asset) : undefined,
        onOptionsActionDescription: entry.editable ? "Title artwork" : undefined,
        onMenuButton: () => actions.openDetails(entry),
        onMenuActionDescription: "Details",
        onContextMenu: (event: any) => {
          event?.preventDefault?.();
          actions.openDetails(entry);
        },
        onButtonDown: entry.editable ? importCycle(entry.id, asset) : undefined,
      },
    }),
    h("div", { className: "wsgm-import-name" }, launchGlyph, h("span", {}, entry.name)),
    h("div", { className: "wsgm-import-launch" }, entry.launchLabel),
  );
}

// What a card draws, so the memo redraws it only when that changed.
const importCardKey = (entry: any, slot: any) =>
  [
    entry.id,
    entry.name,
    entry.selected,
    entry.selectable,
    entry.excluded,
    entry.editable,
    entry.action,
    entry.actionLabel,
    entry.reason,
    entry.launchLabel,
    entry.follows,
    entry.packaged,
    entry.mode,
    entry.canUseSteamIntegration,
    entry.routes?.length ?? 0,
    slot?.kind,
    slot?.thumb,
    slot?.provider,
    slot?.index,
    slot?.count,
  ].join("\u001f");

const importCardType = (react: any) =>
  (ImportCardType ??= react.memo(
    ImportCard,
    (before: any, after: any) => before.drawn === after.drawn && before.asset === after.asset,
  ));

// One title's artwork: what is chosen for each type, every candidate for the shown type grouped by
// provider, and the match to fix when the providers found the wrong game.
function ImportTitleArtwork({ entry, asset, onAsset, onBack, status }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const ui = importUi;
  const [answer, setAnswer] = react.useState(null as any);
  const [search, setSearch] = react.useState(entry.matchName || entry.name);
  const [matches, setMatches] = react.useState(null as any);
  const [searching, setSearching] = react.useState(false);
  const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === asset);
  const slotKey = `${slot?.kind}:${slot?.index}:${slot?.count}:${entry.artworkStatus}`;
  react.useEffect(() => {
    let live = true;
    void importAct("artworkOptions", { id: entry.id, asset }).then((next: any) => {
      if (live) setAnswer(next ?? { asset, options: [], failed: true });
    });
    return () => {
      live = false;
    };
  }, [entry.id, asset, slotKey]);

  const options: any[] = answer?.asset === asset ? (answer.options ?? []) : [];
  const providers = options.reduce((groups: any[], option: any) => {
    const group = groups.find((candidate) => candidate.name === option.provider);
    if (group) group.items.push(option);
    else groups.push({ name: option.provider, items: [option] });
    return groups;
  }, []);
  const width = importAsset(asset).option;
  const empty =
    answer?.failed
      ? "The images could not be listed."
      : answer?.status === "loading" || answer?.status === "pending" || !answer
        ? "Finding images…"
        : importArtworkLine(entry) || "No images were found for this. Fix the match to search for the right game.";
  const content = h(
    "div",
    { className: "wsgm-import-pane" },
    status,
    answer?.detail && options.length ? h("div", { className: "wsgm-import-muted" }, answer.detail) : null,
    options.length === 0 ? h("div", { className: "wsgm-import-muted" }, empty) : null,
    ...providers.map((group: any, groupIndex: number) =>
      h(
        "div",
        { key: group.name, className: "wsgm-import-group" },
        h(
          "div",
          { className: "wsgm-import-grouphead" },
          h("span", { className: "wsgm-import-eyebrow" }, group.name),
          h(
            "span",
            { className: "wsgm-import-muted" },
            `${group.items.length} image${group.items.length === 1 ? "" : "s"}`,
          ),
        ),
        h(
          ui.focusable,
          { className: "wsgm-import-grid", "flow-children": "grid" },
          ...group.items.map((option: any, index: number) =>
            h(ImportCapsule, {
              key: option.url,
              asset,
              width,
              image: option.thumb,
              placeholder: option.provider,
              overlay:
                answer?.selected > 0 && options[answer.selected - 1]?.url === option.url
                  ? h("span", { className: "wsgm-import-check" }, importGlyph(react, "check"))
                  : null,
              caption: option.width ? `${option.width} × ${option.height}` : null,
              focus: {
                autoFocus: groupIndex === 0 && index === 0,
                onActivate: () => void importAct("pickArtwork", { id: entry.id, asset, url: option.url }),
                onOKActionDescription: "Use this",
                onOptionsButton: () => void importAct("clearArtwork", { id: entry.id, asset }),
                onOptionsActionDescription: "Use none",
                onButtonDown: importCycle(entry.id, asset),
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
    const size = { width: `${type.thumb[0]}px`, height: `${type.thumb[1]}px` };
    return h(
      "div",
      { key: type.id, className: "wsgm-import-chosen", "data-on": String(type.id === asset) },
      chosen?.thumb
        ? h("img", { src: chosen.thumb, alt: "", loading: "lazy", draggable: false, style: size })
        : h("span", { className: "wsgm-import-thumb", style: size }),
      h("div", {}, h("span", {}, type.label), h("span", {}, importSlotCaption(chosen) ?? "—")),
    );
  });

  // Fixing a match searches every provider at once and lists them together, the way the artwork
  // page does; the automatic match still asks SteamGridDB first.
  const runSearch = () => {
    setSearching(true);
    void importAct("searchMatch", { id: entry.id, query: search }).then((next: any) => {
      setSearching(false);
      setMatches(next?.matches ?? []);
    });
  };
  const byProvider = (matches ?? []).reduce((groups: any[], match: any) => {
    const group = groups.find((candidate) => candidate.name === match.providerName);
    if (group) group.items.push(match);
    else groups.push({ name: match.providerName, items: [match] });
    return groups;
  }, []);

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
          maxLength: 128,
          placeholder: "Search for the right game",
          onChange: (event: any) => setSearch(event?.target?.value ?? ""),
        })
      : null,
    h(ui.dialogButton, { disabled: searching, onClick: runSearch }, searching ? "Searching…" : "Fix match"),
    entry.matchFixed
      ? h(
          ui.dialogButton,
          { onClick: () => void importAct("setMatch", { id: entry.id, provider: "", gameId: "", name: "" }) },
          "Use the automatic match",
        )
      : null,
    h(
      "div",
      { className: "wsgm-import-matches" },
      matches && matches.length === 0 ? h("div", { className: "wsgm-import-muted" }, "No provider knows that name.") : null,
      ...byProvider.flatMap((group: any) => [
        h("div", { key: `head:${group.name}`, className: "wsgm-import-eyebrow" }, group.name),
        ...group.items.map((match: any) =>
          h(
            ui.dialogButton,
            {
              key: `${match.provider}:${match.id}`,
              onClick: () => {
                setMatches(null);
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
      ]),
    ),
    h(ui.dialogButton, { onClick: onBack }, "Back"),
  );

  return renderSteamUiLevel(
    ui,
    { className: "wsgm-import-main", onBack },
    h(
      "div",
      { className: "wsgm-import-split" },
      side,
      h(
        "div",
        { className: "wsgm-import-main" },
        h(ui.tabs, {
          autoFocusContents: true,
          activeTab: asset,
          onShowTab: (next: string) => onAsset(next),
          tabs: importAssets.map((type) => ({
            id: type.id,
            title: type.label,
            content: type.id === asset ? content : null,
          })),
        }),
      ),
    ),
  );
}

/**
 * Renders shared import review state and dispatches host commands for every persistent action.
 * @param context Registered page accessors for Steam components, latest state and publication refusal.
 * @returns The page React tree, including loading or refusal state when data is unavailable.
 */
function LibraryImportPage({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  importUi = ui;
  const state = context.state() ?? {};
  importLatest = state;
  const [view, setView] = react.useState({ name: "grid", title: "", asset: "grid", from: "grid" });
  const [tab, setTab] = react.useState("all");
  const [asset, setAsset] = react.useState("grid");
  const [query, setQuery] = react.useState("");
  const [fillFrom, setFillFrom] = react.useState("");
  const [message, setMessage] = react.useState(null as null | { text: string; error: boolean });
  const [returnTo, setReturnTo] = react.useState("");
  react.useEffect(() => {
    importReporters.add(setMessage);
    return () => {
      importReporters.delete(setMessage);
    };
  }, []);
  react.useEffect(() => {
    for (const listener of [...importStateListeners]) listener(state);
  }, [state]);

  const entries: any[] = state.entries ?? [];
  const sources: any[] = state.sources ?? [];
  const busy = !!state.loading;
  const titleEntry = view.name === "title" ? entries.find((entry) => entry.id === view.title) : null;

  // A title that is no longer listed, or can no longer be changed, takes the page back where it came
  // from rather than leaving an artwork view nothing can act on.
  react.useEffect(() => {
    if (view.name === "title" && (!titleEntry || !titleEntry.editable)) {
      setView({ name: view.from, title: "", asset: "grid", from: "grid" });
    }
  }, [view.name, titleEntry?.id, titleEntry?.editable]);

  // Stable for the life of the page, so a memoized card's handlers never go stale.
  const actions = react.useMemo(
    () => ({
      toggle: (entry: any) => void importAct("toggleEntry", { id: entry.id }),
      openTitle: (entry: any, type: string) => {
        setReturnTo(entry.id);
        setView((current: any) => ({
          name: "title",
          title: entry.id,
          asset: type,
          from: current.name === "title" ? current.from : current.name,
        }));
      },
      openDetails: (entry: any) =>
        showSteamModal(importUi, {
          title: entry.name,
          render: (close) =>
            importUi.react.createElement(ImportDetailsBody, { id: entry.id, close, openTitle: actions.openTitle }),
        }),
    }),
    [],
  );

  // Every message stays visible: a refusal does not hide what the host says, and the other way round.
  const refusal = context.refusal();
  const statusLines = [
    refusal ? { text: `The library could not be shown in full: ${refusal}`, error: true } : null,
    message,
    state.error ? { text: state.error, error: true } : null,
    state.phase === "applying"
      ? { text: `Saving ${state.progress ?? 0} of ${state.progressTotal ?? 0}…`, error: false }
      : state.notice
        ? { text: state.notice, error: false }
        : null,
    state.launcherDetail ? { text: state.launcherDetail, error: false } : null,
  ].filter((line) => line !== null) as { text: string; error: boolean }[];
  const status = h(
    "div",
    { className: "wsgm-import-status", role: "status" },
    ...statusLines.map((line, index) =>
      h("div", { key: index, className: line.error ? "wsgm-import-error" : undefined }, line.text),
    ),
  );

  const saveCount = state.selectedCount ?? 0;
  const saveButton = h(
    ui.dialogButtonPrimary,
    { disabled: !state.selectedCount || busy, onClick: () => void importAct("apply") },
    state.phase === "applying"
      ? `Saving ${state.progress ?? 0}/${state.progressTotal ?? 0}…`
      : !state.selectedCount
        ? "Save to Steam"
        : `Save to Steam (${saveCount})`,
  );

  let body: any;
  let header: any;
  const crumb = (text: string) =>
    h("span", { className: "wsgm-import-crumb" }, text, " ", importGlyph(react, "chevron"));

  if (view.name === "title" && titleEntry) {
    header = h(
      "div",
      { className: "wsgm-import-head" },
      crumb("Game Library"),
      h("h1", {}, titleEntry.name),
      h("span", { className: "wsgm-import-muted" }, "Choose artwork · applied when you save to Steam"),
    );
    body = h(ImportTitleArtwork, {
      key: titleEntry.id,
      entry: titleEntry,
      asset: view.asset,
      status,
      onAsset: (next: string) => setView({ ...view, asset: next }),
      onBack: () => setView({ name: view.from, title: "", asset: "grid", from: "grid" }),
    });
  } else if (view.name === "all") {
    // Every selected title that can take artwork: the review's search does not apply here, so what
    // Fill and Reset change is exactly the list on screen.
    const rows = entries.filter((entry) => entry.selected && entry.editable);
    const preference = fillFrom || state.artworkPreference || "Catalog";
    const fill = (onlyEmpty: boolean, type: string) =>
      void importAct("fillArtwork", { preference, onlyEmpty, asset: type });
    header = h(
      "div",
      { className: "wsgm-import-head" },
      crumb("Game Library"),
      h("h1", {}, "All artwork"),
      h(
        "span",
        { className: "wsgm-import-muted" },
        `${rows.length} selected title${rows.length === 1 ? "" : "s"} · applied when you save to Steam`,
      ),
    );
    const back = () => setView({ name: "grid", title: "", asset: "grid", from: "grid" });
    body = renderSteamUiLevel(
      ui,
      { className: "wsgm-import-main", onBack: back },
      h(
        "div",
        { className: "wsgm-import-pane" },
        h(
          ui.focusable,
          { className: "wsgm-import-bar", "flow-children": "row" },
          h(
            "div",
            { className: "wsgm-import-tool", style: { width: "290px" } },
            renderSteamDropdown(ui, {
              label: "Fill every title from",
              rgOptions: [
                { data: "Catalog", label: "Fill from the launcher first" },
                { data: "Providers", label: "Fill from SteamGridDB first" },
              ],
              selectedOption: preference,
              onChange: (option: any) => option?.data && setFillFrom(option.data),
            }),
          ),
          h(ui.dialogButton, { onClick: () => fill(false, "") }, "Fill all"),
          h(ui.dialogButton, { onClick: () => fill(true, "") }, "Fill empty slots"),
          h(ui.dialogButton, { onClick: () => void importAct("resetArtwork") }, "Reset all"),
          h("div", { className: "wsgm-import-spacer" }),
          h(ui.dialogButton, { onClick: back }, "Back"),
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
              { key: type.id, className: "wsgm-import-col", style: { width: `${type.cell}px` } },
              h("span", { className: "wsgm-import-eyebrow" }, type.short),
              h(ui.dialogButton, { onClick: () => fill(false, type.id) }, "Fill"),
            ),
          ),
        ),
        rows.length === 0
          ? h("div", { className: "wsgm-import-muted" }, "Select titles in the review to dress them here.")
          : null,
        h(
          ui.focusable,
          { className: "wsgm-import-rows", "flow-children": "column" },
          ...rows.map((entry, rowIndex) =>
            h(
              ui.focusable,
              { key: entry.id, className: "wsgm-import-row", "flow-children": "row" },
              h(
                "div",
                { className: "wsgm-import-rowname" },
                h("span", {}, entry.name),
                h("span", { className: "wsgm-import-muted" }, entry.source),
              ),
              ...importAssets.map((type, columnIndex) => {
                const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === type.id);
                return h(ImportCapsule, {
                  key: type.id,
                  asset: type.id,
                  width: type.cell,
                  image: slot?.thumb ?? "",
                  placeholder: slot?.kind === "loading" ? "…" : slot?.kind === "keep" ? "Current" : "None",
                  caption: type.id === "icon" ? null : importSlotCaption(slot),
                  focus: {
                    autoFocus: rowIndex === 0 && columnIndex === 0,
                    onActivate: () => actions.openTitle(entry, type.id),
                    onOKActionDescription: "All options",
                    onOptionsButton: () => void importAct("clearArtwork", { id: entry.id, asset: type.id }),
                    onOptionsActionDescription: "Clear",
                    onMenuButton: () => actions.openDetails(entry),
                    onMenuActionDescription: "Details",
                    onButtonDown: importCycle(entry.id, type.id),
                  },
                });
              }),
            ),
          ),
        ),
      ),
    );
  } else {
    // The review. One pass over the entries for the search, the tab counts and the source groups;
    // only the shown tab's cards are built.
    const search = query.trim().toLowerCase();
    const counts: Record<string, number> = { all: 0 };
    const active = importTabs.find((candidate) => candidate.id === tab) ?? importTabs[0];
    const groups = new Map<string, any[]>();
    for (const entry of entries) {
      if (search && !String(entry.name).toLowerCase().includes(search)) continue;
      counts.all++;
      counts[entry.group] = (counts[entry.group] ?? 0) + 1;
      if (active.group && entry.group !== active.group) continue;
      const key = String(entry.sourceId).toLowerCase();
      const items = groups.get(key);
      if (items) items.push(entry);
      else groups.set(key, [entry]);
    }
    const Card = importCardType(react);
    const shown = sources
      .map((source) => ({ source, items: groups.get(String(source.id).toLowerCase()) ?? [] }))
      .concat(
        [...groups.entries()]
          .filter(([key]) => !sources.some((source) => String(source.id).toLowerCase() === key))
          .map(([key, items]) => ({ source: { id: key, name: items[0]?.source ?? key }, items })),
      )
      .filter((group) => group.items.length);
    const grid = [
      shown.length === 0
        ? h("div", { className: "wsgm-import-muted" }, entries.length ? "Nothing here." : "No games listed yet.")
        : null,
      ...shown.map((group) =>
        h(
          "div",
          { key: group.source.id, className: "wsgm-import-group" },
          h(
            "div",
            { className: "wsgm-import-grouphead" },
            h("span", { className: "wsgm-import-eyebrow" }, group.source.name),
            h(
              "span",
              { className: "wsgm-import-muted" },
              `${group.items.length} title${group.items.length === 1 ? "" : "s"} · ` +
                `${group.items.filter((entry: any) => entry.selected).length} selected`,
            ),
          ),
          h(
            ui.focusable,
            { className: "wsgm-import-grid", "flow-children": "grid" },
            ...group.items.map((entry: any) => {
              const slot = (entry.artwork ?? []).find((candidate: any) => candidate.asset === asset);
              return h(Card, {
                key: entry.id,
                entry,
                slot,
                asset,
                actions,
                returning: entry.id === returnTo,
                drawn: importCardKey(entry, slot) + (entry.id === returnTo ? "\u001freturn" : ""),
              });
            }),
          ),
        ),
      ),
    ];

    // The sources, each ticked with Steam's own checkbox. One that is not installed cannot be
    // ticked. The right-hand text is what the last scan found in it, or why it cannot be scanned.
    const Check = steamCheckbox(ui);
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
          controlled: true,
          disabled: !source.installed || busy,
          bottomSeparator: "none",
          onChange: (value: boolean) => void importAct("setSourceEnabled", { id: source.id, enabled: !!value }),
        }),
      );
    const sidebar = h(
      ui.focusable,
      { className: "wsgm-import-sidebar", "flow-children": "column" },
      h("div", { className: "wsgm-import-eyebrow" }, "Sources"),
      ...sources.filter((source) => source.kind !== "folder").map(sourceRow),
      h("div", { className: "wsgm-import-eyebrow wsgm-import-custom" }, "Custom"),
      ...sources
        .filter((source) => source.kind === "folder")
        .map((source) =>
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
      h(
        ui.dialogButton,
        {
          disabled: busy,
          onClick: () =>
            showSteamModal(ui, {
              title: "Add a shortcuts folder",
              render: (close) => h(ImportFolderBody, { close }),
            }),
        },
        "Add folder…",
      ),
      h("div", { className: "wsgm-import-eyebrow wsgm-import-custom" }, "Steam"),
      h(
        "div",
        { className: "wsgm-import-source" },
        h(Check, {
          label: "Collections",
          description: "One per launcher and folder",
          checked: !!state.createCollections,
          controlled: true,
          bottomSeparator: "none",
          onChange: (value: boolean) => void importAct("setCollections", { enabled: !!value }),
        }),
      ),
    );

    const anySelected = entries.some(
      (entry) =>
        entry.selected && (!active.group || entry.group === active.group) &&
        (!search || String(entry.name).toLowerCase().includes(search)),
    );
    const toolbar = h(
      ui.focusable,
      { className: "wsgm-import-bar", "flow-children": "row" },
      h(
        "div",
        { className: "wsgm-import-tool", style: { width: "200px" } },
        renderSteamDropdown(ui, {
          label: "Artwork shown",
          rgOptions: importAssets.map((type) => ({ data: type.id, label: type.label })),
          selectedOption: asset,
          onChange: (option: any) => option?.data && setAsset(option.data),
        }),
      ),
      ui.textField
        ? h(
            "div",
            { className: "wsgm-import-search" },
            h(ui.textField, {
              value: query,
              maxLength: 128,
              placeholder: "Search titles",
              onChange: (event: any) => setQuery(event?.target?.value ?? ""),
            }),
          )
        : null,
      h("div", { className: "wsgm-import-spacer" }),
      h(
        ui.dialogButton,
        { disabled: !state.selectedCount, onClick: () => setView({ name: "all", title: "", asset: "grid", from: "grid" }) },
        "All artwork",
      ),
      busy
        ? h(ui.dialogButton, { onClick: () => void importAct("cancel") }, "Stop")
        : h(ui.dialogButton, { onClick: () => void importAct("scan") }, "Scan"),
      // What this tab and search show: "Select all" on the New tab never reaches another tab's titles.
      h(
        ui.dialogButton,
        {
          disabled: counts.all === 0 || busy,
          onClick: () => void importAct("select", { group: active.group, query: search, selected: !anySelected }),
        },
        anySelected ? "Clear" : "Select all",
      ),
      saveButton,
    );

    const installed = sources.filter((source) => source.installed).length;
    header = h(
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
    );
    body = [
      h(react.Fragment, { key: "sidebar" }, sidebar),
      h(
        "div",
        { key: "review", className: "wsgm-import-main" },
        h(ui.tabs, {
          autoFocusContents: true,
          activeTab: active.id,
          onShowTab: (next: string) => setTab(next),
          tabs: importTabs.map((candidate) => ({
            id: candidate.id,
            title: `${candidate.title} ${counts[candidate.group || "all"] ?? 0}`,
            content:
              candidate.id === active.id ? h("div", { className: "wsgm-import-pane" }, toolbar, status, ...grid) : null,
          })),
        }),
      ),
    ];
  }

  return h(
    "div",
    { id: "wsgm-import", "aria-label": "Game Library" },
    h("style", null, importStyles),
    header,
    h("div", { className: "wsgm-import-body" }, ...(Array.isArray(body) ? body : [body])),
  );
}

const libraryImportPage = registerSteamPage({
  template: "library-import",
  gate: "libraryImport",
  patchId: LibraryImportPatchId,
  components: resolveSteamUiComponents,
  // Only what this page actually renders. Steam's checkbox and bare dropdown are wanted, not
  // required: the sidebar falls back to Steam's toggle and the toolbar to its labelled field.
  required: ["react", "focusable", "toggleField", "dropdown", "dialogButton", "dialogButtonPrimary", "tabs", "modalRoot", "showModal"],
  prepare: (ui, runtime) => {
    const classes = resolveSteamLibraryClasses(runtime);
    if (!classes) return "Native Steam components unavailable: library classes";
    ImportCapsule = createSteamCapsule(ui, classes);
    ImportCardType = null;
    return null;
  },
  status: () => ({
    checkbox: !!libraryImportPage.ui()?.checkbox,
    dropdownControl: !!libraryImportPage.ui()?.dropdownControl,
    entries: libraryImportPage.state()?.entries?.length ?? 0,
  }),
  Page: LibraryImportPage,
});
