// Library-import frontend; shared host services own discovery, review decisions and writes.
const LibraryImportPatchId = "wsgm.library-import";
const EmulatorPatchId = "wsgm.emulators";
const EmulatorProgressId = "wsgm.emulators.progress";
const importError = (failure: any) => String(failure?.message ?? failure);
const importSplitValues = (value: string, separator = /[\s,;]+/) =>
  value
    .split(separator)
    .map((item) => item.trim())
    .filter(Boolean);
const importExtensions = (value: string) =>
  importSplitValues(value).map((item) =>
    item.startsWith(".") ? item.toLowerCase() : "." + item.toLowerCase(),
  );
const importFact = (label: string, value: string, key?: string) =>
  importUi.react.createElement(
    "div",
    { key, className: "wsgm-import-fact" },
    importUi.react.createElement("span", {}, label),
    importUi.react.createElement("span", {}, value || "—"),
  );

// The resolved components, for the modals drawn outside the page's tree. Both pages resolve the
// same components through the toolkit's one resolver, so whichever page drew last supplies them.
let importUi: any = null;
let ImportCapsule: any = null;
let ImportCardType: any = null;

// The emulator tool's progress is a publication of its own, so a progress tick does not resend the
// whole catalogue. The emulator page's gate follows it while installed; the import page's emulator
// setup sheet reads the same value.
const emulatorProgressStore = createLocalStore();
let emulatorProgress: any = { busy: false, status: "" };
let emulatorProgressEnd: (() => void) | null = null;

/**
 * Sends an import command through the page frame: the previous page message clears first, and a
 * refusal shows in the page's banner.
 * @param command Allowlisted import command.
 * @param payload JSON action arguments.
 * @returns The backend result, or undefined after a refusal.
 */
const importAct = (command: string, payload: any = {}) => libraryImportPage.act(command, payload);
// A page message from a modal: a notice, or an error.
const importReport = (message: { text: string; error: boolean } | null) =>
  libraryImportPage.report(message?.text ?? null, message?.error ?? true);
const emulatorAct = (command: string, payload: any = {}) => emulatorPage.act(command, payload);
const emulatorReport = (message: { text: string; error: boolean } | null) =>
  emulatorPage.report(message?.text ?? null, message?.error ?? true);

// A page's live state for a component that may be drawn on either page, or in a modal over it:
// the page's own state when its gate has supplied React, else nothing. Either way it calls exactly
// one hook, so a component keeps its hook order when the other gate installs later.
const usePageState = (page: SteamPageContext, other: SteamPageContext) => {
  if (page.react()) return page.useState();
  if (other.react()) other.useState();
  return null;
};
const useImportState = () => usePageState(libraryImportPage, emulatorPage);
const useEmulatorState = () => usePageState(emulatorPage, libraryImportPage);
// What the ROM editors read: whether the library is busy, and the emulator tool's systems and
// choices, which reach the import page through the emulator page's publication.
const useImportEditorState = () => {
  const imported = useImportState();
  const emulators = useEmulatorState();
  return {
    loading: !!imported?.loading,
    romSystems: emulators?.romSourceSystems ?? emulators?.romSystems ?? [],
    emulators: {
      choices: emulators?.choices ?? [],
      systemPreferences: emulators?.emulators?.systemPreferences ?? [],
    },
  };
};
const useEmulatorProgress = () => {
  importUi.react.useSyncExternalStore(
    emulatorProgressStore.subscribe,
    emulatorProgressStore.revision,
    emulatorProgressStore.revision,
  );
  return emulatorProgress;
};

const importAssets = [
  {
    id: "grid",
    label: "Portrait capsule",
    short: "Portrait",
    card: 150,
    cell: 70,
    option: 124,
    thumb: [36, 54],
  },
  {
    id: "wide",
    label: "Wide capsule",
    short: "Wide",
    card: 300,
    cell: 224,
    option: 280,
    thumb: [60, 28],
  },
  { id: "hero", label: "Hero", short: "Hero", card: 300, cell: 300, option: 280, thumb: [60, 28] },
  { id: "logo", label: "Logo", short: "Logo", card: 220, cell: 160, option: 200, thumb: [60, 28] },
  { id: "icon", label: "Icon", short: "Icon", card: 150, cell: 64, option: 124, thumb: [36, 36] },
];
const importAsset = (id: string) =>
  importAssets.find((asset) => asset.id === id) ?? importAssets[0];
const importUnavailable = (entry: any) => !!entry.unavailable;
const importAvailability = (entry: any) => entry.availabilityLabel || "Not checked yet";

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

// The page's own layout over Steam's components. Steam's stable class names (DialogCheckbox,
// DialogLabel, DialogInput, DialogButton) are what the compacting rules below hang on; the hashed
// ones are never named.
const importStyles = `
#wsgm-import .wsgm-import-head { display: flex; align-items: baseline; flex-wrap:wrap; gap: 8px 14px; padding: 24px 48px 0; flex-shrink: 0; }
#wsgm-import .wsgm-import-head h1 { margin: 0; min-width:0; max-width:100%; overflow-wrap:anywhere; font-size: 30px; font-weight: 700; color: #fff; letter-spacing: 0.2px; }
#wsgm-import .wsgm-import-crumb { font-size: 30px; font-weight: 700; color: #8b929a; }
#wsgm-import .wsgm-import-crumb svg { width: 18px; height: 18px; margin: 0 -2px 2px 0; vertical-align: middle; }
#wsgm-import .wsgm-import-muted { color: #8b929a; font-size: 14px; }
#wsgm-import .wsgm-import-body { flex: 1; min-height: 0; display: flex; gap: 32px; padding: 16px 48px 0; }
#wsgm-import .wsgm-import-sidebar { width: 260px; flex: 0 0 auto; overflow-y: auto; min-height: 0;
  padding-bottom: 72px; display: flex; flex-direction: column; gap: 2px; }
#wsgm-import .wsgm-import-eyebrow { font-size: 13px; font-weight: 700; letter-spacing: 1.6px; text-transform: uppercase;
  color: #dcdedf; padding: 0 10px 8px; }
#wsgm-import .wsgm-import-sidebar .wsgm-import-custom { padding-top: 16px; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container { display: grid; grid-template-columns:auto minmax(0,1fr);
  align-items: center; gap: 2px 10px; min-height: 44px; margin: 0; padding: 6px 10px; border-radius: 2px; box-sizing: border-box; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container > .DialogCheckbox { grid-row:1 / span 2; margin: 0; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container > div:empty { display: none; }
#wsgm-import .wsgm-import-source .DialogToggle_Label { grid-column:2; min-width: 0; font-size: 15px; line-height: 1.3;
  white-space: normal; overflow-wrap:anywhere; margin: 0; padding: 0; }
#wsgm-import .wsgm-import-source .DialogToggle_Description { grid-column:2; margin: 0; padding: 0;
  font-size: 12px; line-height:1.3; color: #8b929a; white-space: normal; overflow-wrap:anywhere; }
#wsgm-import .wsgm-import-source[data-missing="true"] .DialogCheckbox_Container { opacity: 0.6; }
#wsgm-import .wsgm-import-sidebar > .DialogButton { margin: 8px 0 0; width: auto; min-width: auto; height: 36px; }
#wsgm-import .wsgm-import-main { flex: 1; min-width: 0; min-height: 0; display: flex; flex-direction: column; }
#wsgm-import .wsgm-import-pane { display: flex; flex-direction: column; gap: 14px; padding: 12px 4px 72px; }
#wsgm-import .wsgm-import-bar { display: flex; align-items: flex-end; gap: 10px; flex-wrap: wrap; }
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
#wsgm-import .wsgm-import-empty { max-width:640px; margin-top:12px; gap:16px; }
#wsgm-import .wsgm-import-empty h2 { margin:0; font-size:24px; }
#wsgm-import .wsgm-import-empty p { margin:0; line-height:1.5; }
#wsgm-import .wsgm-import-add { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:8px; }
#wsgm-import .wsgm-import-add .DialogButton { width:100%; min-width:0; height:auto; min-height:40px; margin:0; padding:8px 12px; box-sizing:border-box; white-space:normal; }
#wsgm-import .wsgm-import-empty > .DialogButton { width:auto; align-self:flex-start; min-width:180px; }
#wsgm-import .wsgm-import-main { overflow:auto; }
#wsgm-import .wsgm-import-side { max-width:100%; }
@media(max-width:1000px) { #wsgm-import .wsgm-import-body { gap:20px; padding-inline:24px; } #wsgm-import .wsgm-import-head { padding-inline:24px; } #wsgm-import .wsgm-import-sidebar { width:220px; } }
@media(max-width:750px) { #wsgm-import .wsgm-import-add { grid-template-columns:1fr; } #wsgm-import .wsgm-import-split { flex-wrap:wrap; } }
#wsgm-import .wsgm-import-group { display: flex; flex-direction: column; gap: 14px; margin-bottom: 8px; }
#wsgm-import .wsgm-import-grouphead { display: flex; align-items: baseline; gap: 14px; }
#wsgm-import .wsgm-import-grouphead .wsgm-import-eyebrow { padding: 0; }
#wsgm-import .wsgm-import-grid { display: flex; flex-wrap: wrap; gap: 22px 14px; padding: 4px; }
#wsgm-import .wsgm-import-card { display: flex; flex-direction: column; gap: 8px; }
#wsgm-import .wsgm-import-card[data-unavailable="true"] img { filter: grayscale(1); }
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
.steam-ui-kit-sheet .wsgm-import-sheethead { display: flex; align-items: center; gap: 16px; margin-bottom: 8px; }
.steam-ui-kit-sheet .wsgm-import-sheethead img, .steam-ui-kit-sheet .wsgm-import-sheethead .wsgm-import-thumb { width: 60px; height: 90px;
  border-radius: 2px; object-fit: cover; background: rgba(255,255,255,0.06); flex: 0 0 auto; }
.steam-ui-kit-sheet .wsgm-import-sheethead div { display: flex; flex-direction: column; gap: 6px; }
.steam-ui-kit-sheet .wsgm-import-sheethead span:last-child { font-size: 14px; color: #8b929a; }
.steam-ui-kit-sheet .wsgm-import-fact { display: flex; align-items: flex-start; justify-content: space-between; gap: 24px;
  padding: 12px 0; border-top: 1px solid #2f343c; }
.steam-ui-kit-sheet .wsgm-import-fact > span:first-child { font-size: 15px; color: #dcdedf; flex-shrink: 0; }
.steam-ui-kit-sheet .wsgm-import-fact > span:last-child { font-size: 14px; color: #8b929a; text-align: right; line-height: 1.4;
  max-width: 420px; word-break: break-word; }
.steam-ui-kit-sheet .wsgm-import-muted { font-size: 13px; color: #8b929a; }
.steam-ui-kit-sheet .wsgm-import-spacer { flex: 1; }
.steam-ui-kit-sheet .wsgm-import-path { max-width: 280px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
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
  return renderSteamUiSheet(
    importUi,
    {
      className: "wsgm-import-risk",
      note: `${entry.name} is marked as a multiplayer title. The Steam overlay route loads Steam's overlay into the running game. Anti-cheat compatibility has not been established for any title, and some anti-cheat systems treat that as tampering. Controller-only support injects nothing and is the safer choice for a multiplayer game.`,
      actions: [
        { id: "cancel", label: "Keep controller only", onClick: close },
        {
          id: "save",
          label: "Use the Steam overlay",
          primary: true,
          disabled: !checked,
          onClick: () => {
            if (!checked) return;
            void importAct("setMode", {
              id: entry.id,
              mode: "SteamIntegration",
              acknowledged: true,
            });
            close();
          },
        },
      ],
    },
    h(importUi.toggleField, {
      label: "I understand this may risk a ban on this title",
      checked,
      controlled: true,
      onChange: (value: boolean) => setChecked(!!value),
    }),
  );
}

const showImportRisk = (entry: any) =>
  showSteamModal(importUi, {
    title: entry.name,
    render: (close) => importUi.react.createElement(ImportRiskBody, { entry, close }),
  });
const cycleImportLaunch = (entry: any) =>
  void importAct("cycleLaunch", { id: entry.id }).then((answer: any) => {
    if (answer?.acknowledge) showImportRisk(entry);
  });

function ImportDetailsBody({ id, close, openTitle }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const state = useImportState();
  const [details, setDetails] = react.useState(null as any);
  react.useEffect(() => {
    let current = true;
    void importAct("details", { id }).then(
      (answer: any) => current && answer && setDetails(answer),
    );
    return () => {
      current = false;
    };
  }, [id]);

  const entry = (state?.entries ?? []).find((candidate: any) => candidate.id === id);
  const name = react.useRef("");
  if (entry) name.current = entry.name;
  // A title can go while its sheet is open, such as when its files were deleted. Close rather than
  // leave a sheet about nothing; the page says what happened and keeps its own focus. A scan or save
  // in progress may only be replacing the list, so the sheet waits for it to settle first.
  const pending = !entry && (!state || !!state.loading);
  react.useEffect(() => {
    if (entry || pending) return;
    importReport({
      text: name.current ? `${name.current} is no longer listed.` : "That title is no longer listed.",
      error: false,
    });
    close();
  }, [!entry, pending]);
  if (!entry) {
    return renderSteamUiSheet(importUi, {
      note: pending
        ? "The library is being updated. This title returns when the update finishes, unless it " +
          "was removed."
        : "This title is no longer listed.",
      actions: [{ id: "close", label: "Close", onClick: close }],
    });
  }
  const tracked =
    entry.appId > 0
      ? (state?.shortcuts ?? []).some((shortcut: any) => shortcut.appId === entry.appId)
      : false;

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
            ...(entry.canUseSteamIntegration
              ? [{ data: "SteamIntegration", label: "Steam overlay" }]
              : []),
          ],
          selectedOption: entry.mode,
          onChange: (option: any) => {
            const mode = option?.data;
            if (!mode || mode === entry.mode) return;
            if (
              mode === "SteamIntegration" &&
              entry.requiresAcknowledgement &&
              !entry.acknowledged
            ) {
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
    [
      "Launch route",
      details ? `${entry.launchLabel}. ${details.launchEvidence}` : entry.launchLabel,
    ],
    ["Multiplayer", details ? `${details.multiplayer}. ${details.multiplayerEvidence}` : "…"],
    ["Saving would", `${entry.actionLabel}: ${entry.reason}`],
    ["Artwork", artwork || importArtworkLine(entry) || "—"],
    [
      "Matched to",
      entry.matchName ? `${entry.matchName}${entry.matchFixed ? " (fixed)" : ""}` : "—",
    ],
    ["Installed at", details?.installPath || "unknown"],
    ...(entry.managedId || tracked
      ? ([
          ["Location", entry.location || entry.contentPath || "—"],
          [
            "Availability",
            entry.unavailable && entry.availabilityDetail
              ? `${importAvailability(entry)}. ${entry.availabilityDetail}`
              : importAvailability(entry),
          ],
        ] as [string, string][])
      : []),
    ["Identity", details?.identity ?? "…"],
    ...((details?.notes ?? []) as string[]).map((note: string): [string, string] => ["Note", note]),
  ];
  const grid = (entry.artwork ?? []).find((slot: any) => slot.asset === "grid");
  const actions = [
    entry.managedId || tracked
      ? {
          label: "Recheck availability",
          run: () => void importAct("recheckAvailability"),
        }
      : null,
    tracked ? { label: "Storage…", run: () => showImportStorage(openTitle) } : null,
    entry.systemId
      ? {
          label: "Rename title…",
          run: () =>
            showSteamUiPrompt(importUi, {
              title: "Title",
              label: "Title",
              initial: entry.name,
              confirmLabel: "Save title",
              onConfirm: (name: string) => void importAct("setRomTitle", { id: entry.id, name }),
            }),
        }
      : null,
    entry.systemId
      ? {
          label: "Use library or system default",
          run: () =>
            void importAct("setRomEmulator", { id: entry.id, installationId: "", coreId: "" }),
        }
      : null,
    entry.systemId
      ? {
          label: "Launch arguments…",
          run: () =>
            showSteamModal(importUi, {
              title: "Launch arguments",
              render: (done) =>
                h(ImportArgumentsBody, {
                  tokens: entry.arguments ?? [],
                  onSave: (values: string[]) =>
                    libraryImportPage.send("setRomArguments", {
                      id: entry.id,
                      arguments: values,
                    }),
                  close: done,
                }),
            }),
        }
      : null,
    entry.managedId && entry.appId > 0 && entry.action !== "Conflict"
      ? entry.action === "Remove"
        ? {
            label: "Keep this shortcut",
            run: () => void importAct("setCleanup", { id: entry.id, enabled: false }),
          }
        : {
            label: "Stage shortcut removal…",
            run: () =>
              showSteamUiConfirm(importUi, {
                title: "Stage shortcut removal?",
                text: "The next apply will remove this shortcut from Steam. ROMs and saves are preserved.",
                confirmLabel: "Stage removal",
                onConfirm: () => {
                  close();
                  void importAct("setCleanup", { id: entry.id, enabled: true });
                },
              }),
          }
      : null,
    entry.editable
      ? { label: "Choose artwork…", run: () => (close(), openTitle(entry, "grid")) }
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
      ? { label: "Offer again", run: () => void importAct("include", { id: entry.id }) }
      : entry.action === "Add" || entry.action === "Adopt"
        ? {
            label: "Don't import",
            run: () => (void importAct("exclude", { id: entry.id }), close()),
          }
        : null,
  ].filter((action) => action !== null) as { label: string; run: () => void }[];

  return renderSteamUiSheet(
    importUi,
    {
      actions: [
        ...actions.map((action) => ({
          id: action.label,
          label: action.label,
          onClick: action.run,
        })),
        { id: "close", label: "Close", onClick: close },
      ],
    },
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
    entry.systemId && entry.editable
      ? h(ImportRomLaunchControls, {
          state,
          systemId: entry.systemId,
          installationId: entry.emulatorInstallationId,
          coreId: entry.coreId,
          choose: (installationId: string, coreId: string) =>
            void importAct("setRomEmulator", { id: entry.id, installationId, coreId }),
        })
      : null,
    ...facts.map(([term, value], index) => importFact(term, value, `${term}${index}`)),
  );
}

// Storage: every non-Steam shortcut grouped by the card, disk or drive its files are on, with each card
// library's immediate Repair library or Use this card. The overlay builds the same groups from the same state.
const importStorageAttention = (storage: any) =>
  !!storage.canRepair || !!storage.canAcceptBinding || storage.status !== "Connected";
const importStorageLine = (storage: any) =>
  `${storage.status} · ${storage.titleCount} title${storage.titleCount === 1 ? "" : "s"}`;
const importShortcutLine = (shortcut: any) =>
  [shortcut.label, shortcut.owned ? "" : "Your shortcut", shortcut.available ? "" : shortcut.detail]
    .filter(Boolean)
    .join(" · ");
const importStorageSummary = (state: any) => {
  const shortcuts: any[] = state?.shortcuts ?? [];
  const attention = (state?.storageLibraries ?? []).filter(importStorageAttention).length;
  const unavailable = shortcuts.filter((shortcut) => !shortcut.available).length;
  return [
    state?.storageChecked
      ? `${shortcuts.length} shortcut${shortcuts.length === 1 ? "" : "s"}`
      : "Checking shortcuts",
    state?.storageChecked && unavailable ? `${unavailable} unavailable` : "",
    attention ? `${attention} card${attention === 1 ? " needs" : "s need"} attention` : "",
  ]
    .filter(Boolean)
    .join(" · ");
};
// Card libraries first by name, then every other location; a library groups its shortcuts by id.
const importStorageGroups = (shortcuts: any[], storages: any[]) => {
  const placed = new Set<any>();
  const groups: { name: string; library: any; shortcuts: any[] }[] = storages.map((library: any) => {
    const members = shortcuts.filter(
      (shortcut) =>
        shortcut.libraryId === library.id ||
        (!shortcut.libraryId && shortcut.location === library.name),
    );
    members.forEach((member) => placed.add(member));
    return { name: library.name, library, shortcuts: members };
  });
  const others = new Map<string, any[]>();
  for (const shortcut of shortcuts) {
    if (placed.has(shortcut)) continue;
    const name = shortcut.location || "Unknown location";
    others.set(name, [...(others.get(name) ?? []), shortcut]);
  }
  for (const [name, members] of others) groups.push({ name, library: null, shortcuts: members });
  return groups.sort((left, right) => left.name.localeCompare(right.name));
};

const ImportStoragePage = 48;

function ImportStorageBody({ close, openTitle }: any) {
  const react = importUi.react;
  const state = useImportState();
  // Shortcuts are shown a page at a time; the first of each newly loaded page takes focus.
  const [shown, setShown] = react.useState(ImportStoragePage);
  const [focusAt, setFocusAt] = react.useState("");
  const busy = !!state?.loading;
  const shortcuts: any[] = state?.shortcuts ?? [];
  const entries: any[] = state?.entries ?? [];
  const groups = importStorageGroups(shortcuts, state?.storageLibraries ?? []);
  const rows: any[] = [];
  let count = 0;
  let next = "";
  for (const group of groups) {
    if (next) break;
    const unavailable = group.shortcuts.filter((shortcut) => !shortcut.available).length;
    rows.push(
      importFact(
        group.name,
        [
          group.library ? importStorageLine(group.library) : "",
          `${group.shortcuts.length} shortcut${group.shortcuts.length === 1 ? "" : "s"}`,
          unavailable ? `${unavailable} unavailable` : "",
          group.library?.detail ?? "",
        ]
          .filter(Boolean)
          .join(" · "),
        `group:${group.name}`,
      ),
    );
    if (group.library?.canRepair) {
      rows.push(
        renderSteamUiSelectRow(importUi, {
          key: `repair:${group.library.id}`,
          title: "Repair library",
          detail: "Rebuild this card's game list from the titles WSGM knows. Your game files stay.",
          onClick: () => {
            if (!busy) void importAct("repairLibrary", { id: group.library.id });
          },
        }),
      );
    }
    if (group.library?.canAcceptBinding) {
      rows.push(
        renderSteamUiSelectRow(importUi, {
          key: `use:${group.library.id}`,
          title: "Use this card",
          detail: "Launch this card's games on this device from where it is connected now.",
          onClick: () => {
            if (!busy) void importAct("useLibrary", { id: group.library.id });
          },
        }),
      );
    }
    for (const shortcut of group.shortcuts) {
      if (count === shown) {
        next = `shortcut:${shortcut.appId}`;
        break;
      }
      count++;
      const entry = entries.find((candidate) => candidate.appId === shortcut.appId);
      const row = renderSteamUiSelectRow(importUi, {
        key: `shortcut:${shortcut.appId}`,
        title: shortcut.name,
        detail: importShortcutLine(shortcut),
        onClick: entry
          ? () => showImportDetails(entry, openTitle)
          : () =>
              importReport({
                text: shortcut.available
                  ? `${shortcut.name} is available on ${shortcut.location}.`
                  : `${shortcut.name}: ${shortcut.detail}`,
                error: false,
              }),
      });
      rows.push(
        focusAt === `shortcut:${shortcut.appId}` ? react.cloneElement(row, { autoFocus: true }) : row,
      );
    }
  }
  const more = next
    ? renderSteamUiSelectRow(importUi, {
        key: "shortcuts-more",
        title: "Load more",
        detail: `${count} of ${shortcuts.length}`,
        onClick: () => {
          setFocusAt(next);
          setShown(shown + ImportStoragePage);
        },
      })
    : null;
  return renderSteamUiSheet(
    importUi,
    {
      note:
        "Every non-Steam shortcut in Steam, grouped by where its files are. WSGM shows when a card is " +
        "out or files are missing, and never removes a shortcut itself.",
      actions: [
        {
          id: "recheck",
          label: "Recheck",
          primary: true,
          disabled: busy,
          onClick: () => void importAct("recheckAvailability"),
        },
        { id: "close", label: "Close", onClick: close },
      ],
    },
    ...(groups.length
      ? rows
      : [
          importFact(
            "Storage",
            state?.storageChecked
              ? "No shortcut in Steam has its files on this device's storage, and no game library card is known."
              : "Shortcuts are listed here once Steam has been checked.",
            "empty",
          ),
        ]),
    more,
  );
}
const showImportStorage = (openTitle: any) =>
  showSteamModal(importUi, {
    title: "Storage",
    render: (close) => importUi.react.createElement(ImportStorageBody, { close, openTitle }),
  });
// One title's details, from the page or the Storage sheet.
const showImportDetails = (entry: any, openTitle: any) =>
  showSteamModal(importUi, {
    title: entry.name,
    render: (close) =>
      importUi.react.createElement(ImportDetailsBody, { id: entry.id, close, openTitle }),
  });
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
    void showSteamFilePicker(importUi, {
      title: "Choose the shortcuts folder",
      mode: "folder",
    }).then((chosen) => chosen && setPath(chosen));
  const extensions = importExtensions(types);
  const add = () => {
    setError("");
    void libraryImportPage.send("addFolder", {
      path,
      includeSubfolders: subfolders,
      extensions,
    }).then(
      () => close(),
      (failure: any) => setError(importError(failure)),
    );
  };
  return renderSteamUiSheet(
    importUi,
    {
      note:
        "Every shortcut in the folder becomes a title and keeps its program, arguments and Start in folder. " +
        "A game on removable storage follows that storage, wherever this folder is.",
      error,
      actions: [
        { id: "cancel", label: "Cancel", onClick: close },
        {
          id: "save",
          label: "Add source",
          primary: true,
          disabled: !path || extensions.length === 0,
          onClick: add,
        },
      ],
    },
    h(
      importUi.focusable,
      { className: "wsgm-import-fact", "flow-children": "row" },
      h(
        "div",
        { style: { display: "flex", flexDirection: "column", gap: "4px" } },
        h("span", {}, "Folder"),
        h("span", { className: "wsgm-import-muted" }, "Pick a drive and folder."),
      ),
      h(
        importUi.dialogButton,
        { onClick: choose, style: { width: "280px" } },
        h("span", { className: "wsgm-import-path" }, path || "Choose…"),
      ),
    ),
    h(importUi.toggleField, {
      label: "Include subfolders",
      description: "Scan folders inside it too.",
      checked: subfolders,
      onChange: (value: boolean) => setSubfolders(value),
    }),
    importUi.textField
      ? h(importUi.textField, {
          label: "File types",
          description: "Separated by spaces.",
          value: types,
          onChange: (event: any) => setTypes(event?.target?.value ?? ""),
        })
      : null,
  );
}

// The emulators that can run a system, from the emulator tool's publication.
const importSystemChoices = (editor: any, systemId: string) =>
  (editor?.emulators?.choices ?? []).find((item: any) => item.systemId === systemId)
    ?.installations ?? [];

function ImportArgumentsBody({ tokens, onSave, close }: any) {
  const react = importUi.react;
  const [values, setValues] = react.useState([...(tokens ?? [])]);
  const [error, setError] = react.useState("");
  const save = () =>
    Promise.resolve()
      .then(() => onSave(values.filter((value: string) => value.trim().length > 0)))
      .then(close, (failure: any) => setError(importError(failure)));
  return renderSteamUiSheet(
    importUi,
    {
      note: "Each row is one argument, including spaces inside it. Use {rom} for the game path. RetroArch also supports {core}, {data} and {config}. Leave the list empty to use the library or emulator default.",
      error,
      actions: [
        { id: "cancel", label: "Cancel", onClick: close },
        { id: "save", label: "Save arguments", primary: true, onClick: () => void save() },
      ],
    },
    renderSteamUiStringList(importUi, {
      values,
      onChange: setValues,
      label: (index: number) => `Argument ${index + 1}`,
      addLabel: "Add argument",
      removeLabel: (index: number) => `Remove argument ${index + 1}`,
      resetLabel: "Use default arguments",
    }),
  );
}

function ImportRomLaunchControls({ systemId, installationId, coreId, choose }: any) {
  const h = importUi.react.createElement;
  const state = useImportEditorState();
  if (systemId === "auto")
    return h("div", { style: { color: "#b8bcbf" } },
      "Recognizes system folders such as gc, n3ds and genesis. Each system uses its preferred emulator. You can preview before installing an emulator.");
  const installations = importSystemChoices(state, systemId);
  const selected = installations.find((installation: any) => installation.id === installationId);
  const options = [
    { data: "", label: "System default" },
    ...installations.map((installation: any) => ({
      data: installation.id,
      label: installation.label,
    })),
    ...(installationId && !selected
      ? [{ data: installationId, label: "Selected emulator is unavailable" }]
      : []),
  ];
  const cores = selected?.cores ?? [];
  return h(
    importUi.react.Fragment,
    {},
    h(importUi.dropdown, {
      label: "Emulator",
      rgOptions: options,
      selectedOption: installationId,
      onChange: (option: any) => {
        if (!option) return;
        const installation = installations.find((item: any) => item.id === option.data);
        choose(option.data, installation?.defaultCoreId ?? "");
      },
    }),
    selected?.requiresCore
      ? h(importUi.dropdown, {
          label: "Core",
          rgOptions: [
            { data: "", label: "Choose a core" },
            ...cores.map((core: any) => ({ data: core.id, label: core.label })),
            ...(coreId && !cores.some((core: any) => core.id === coreId)
              ? [{ data: coreId, label: "Selected core is unavailable" }]
              : []),
          ],
          selectedOption: coreId,
          onChange: (option: any) => option && choose(installationId, option.data),
        })
      : null,
  );
}

function ImportRomSourceBody({ sourceId, singleFile = false, close }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const state = useImportEditorState();
  const [draft, setDraft] = react.useState(() => {
    const manual = singleFile
      ? (libraryImportPage.state()?.manualSources ?? []).find((item: any) => item.id === sourceId)
      : null;
    const source = (libraryImportPage.state()?.romSources ?? []).find((item: any) => item.id === sourceId);
    const system =
      state?.romSystems?.find((item: any) => item.id === manual?.systemId) ??
      state?.romSystems?.find((item: any) => !singleFile || item.id !== "auto");
    const preferred = state.emulators.systemPreferences.find(
      (item: any) => item.systemId === system?.id,
    );
    if (manual?.romPath)
      return {
        id: manual.id,
        name: manual.name,
        root: { ...manual.romPath, directory: false },
        systemId: manual.systemId,
        emulatorInstallationId: manual.emulatorInstallationId,
        coreId: manual.coreId,
        includeSubfolders: false,
        titleCleanup: true,
        extensions: [],
        exclusions: [],
        arguments: [...(manual.romArguments ?? [])],
      };
    if (source)
      return {
        ...source,
        root: { ...source.root },
        extensions: [...source.extensions],
        exclusions: [...source.exclusions],
        arguments: [...source.arguments],
      };
    return {
      id: "",
      name: singleFile ? "ROM" : "ROM library",
      root: { absolutePath: "", volumeId: "", relativePath: "", directory: !singleFile },
      systemId: system?.id ?? "",
      emulatorInstallationId: preferred?.installationId ?? "",
      coreId: preferred?.coreId ?? "",
      includeSubfolders: !singleFile,
      titleCleanup: true,
      extensions: system?.extensions ?? [],
      exclusions: [],
      arguments: [],
    };
  });
  const [error, setError] = react.useState("");
  const change = (fields: any) => setDraft((current: any) => ({ ...current, ...fields }));
  const systems = (state?.romSystems ?? []).filter((item: any) => !singleFile || item.id !== "auto");
  const field = (label: string, value: string, onChange: any) =>
    h(importUi.textField, {
      label,
      value,
      onChange: (event: any) => onChange(event?.target?.value ?? ""),
    });
  const choosePath = () =>
    void showSteamFilePicker(importUi, {
      title: singleFile ? "Choose a ROM" : "Choose the ROM library",
      mode: singleFile ? "file" : "folder",
    }).then(
      (chosen) =>
        chosen &&
        change({
          root: { absolutePath: chosen, volumeId: "", relativePath: "", directory: !singleFile },
        }),
    );
  const save = () => {
    setError("");
    void libraryImportPage.send("addRomSource", { source: draft }).then(
      close,
      (failure: any) => setError(importError(failure)),
    );
  };
  const remove = () =>
    showSteamUiConfirm(importUi, {
      title: singleFile ? "Remove this ROM source?" : "Remove this ROM library?",
      text: "Stops scanning this source. ROMs and imported shortcuts stay.",
      confirmLabel: "Remove source",
      onConfirm: () =>
        void libraryImportPage.send(singleFile ? "removeManualSource" : "removeRomSource", {
          id: draft.id,
        }).then(close, (failure: any) => setError(importError(failure))),
    });
  const actions = [
    ...(draft.id
      ? [{ id: "remove", label: "Remove source…", disabled: state?.loading, onClick: remove }]
      : []),
    { id: "cancel", label: "Cancel", onClick: close },
    {
      id: "save",
      label: "Save and preview",
      primary: true,
      disabled:
        state?.loading ||
        !draft.root.absolutePath ||
        !draft.systemId,
      onClick: save,
    },
  ];
  return renderSteamUiSheet(
    importUi,
    {
      note: "Review the titles before saving to Steam. ROM files stay in their current location.",
      error,
      actions,
    },
    field(singleFile ? "Title" : "Library name", draft.name, (name: string) => change({ name })),
    h(
      importUi.dialogButton,
      { onClick: choosePath },
      draft.root.absolutePath || (singleFile ? "Choose ROM…" : "Choose folder…"),
    ),
    h(importUi.dropdown, {
      label: "System",
      rgOptions: systems.map((system: any) => ({ data: system.id, label: system.name })),
      selectedOption: draft.systemId,
      onChange: (option: any) => {
        const system = systems.find((item: any) => item.id === option?.data);
        const preferred = state.emulators.systemPreferences.find(
          (item: any) => item.systemId === system?.id,
        );
        if (system)
          change({
            systemId: system.id,
            extensions: system.extensions,
            emulatorInstallationId: preferred?.installationId ?? "",
            coreId: preferred?.coreId ?? "",
          });
      },
    }),
    h(ImportRomLaunchControls, {
      state,
      systemId: draft.systemId,
      installationId: draft.emulatorInstallationId,
      coreId: draft.coreId,
      choose: (emulatorInstallationId: string, coreId: string) =>
        change({ emulatorInstallationId, coreId }),
    }),
    ...(!singleFile
      ? [
          h(importUi.toggleField, {
            label: "Include subfolders",
            checked: draft.includeSubfolders,
            onChange: (value: boolean) => change({ includeSubfolders: value }),
          }),
          h(importUi.toggleField, {
            label: "Clean title names",
            checked: draft.titleCleanup,
            onChange: (value: boolean) => change({ titleCleanup: value }),
          }),
          field(
            "File extensions, separated by spaces",
            draft.extensions.join(" "),
            (value: string) => change({ extensions: importExtensions(value) }),
          ),
          field(
            "Excluded paths, separated by semicolons",
            draft.exclusions.join("; "),
            (value: string) => change({ exclusions: importSplitValues(value, /;/) }),
          ),
        ]
      : []),
    h(
      importUi.dialogButton,
      {
        onClick: () =>
          showSteamModal(importUi, {
            title: "Launch arguments",
            render: (done) =>
              h(ImportArgumentsBody, {
                tokens: draft.arguments,
                onSave: (args: string[]) => change({ arguments: args }),
                close: done,
              }),
          }),
      },
      draft.arguments.length
        ? `Launch arguments (${draft.arguments.length})…`
        : "Launch arguments: emulator default…",
    ),
  );
}

function ImportManualSourceBody({ sourceId, close }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const [draft, setDraft] = react.useState(() => {
    const source = (libraryImportPage.state()?.manualSources ?? []).find((item: any) => item.id === sourceId);
    return {
      id: source?.id ?? "",
      name: source?.name ?? "",
      target: source?.target ?? "",
      arguments: source?.arguments ?? "",
      workingDirectory: source?.workingDirectory ?? "",
      contentPath: source?.contentPath ?? "",
      location: source?.location ?? "",
      libraryBacked: source?.libraryBacked ?? true,
    };
  });
  const [error, setError] = react.useState("");
  const [showOptional, setShowOptional] = react.useState(false);
  const change = (fields: any) => setDraft((current: any) => ({ ...current, ...fields }));
  const field = (label: string, key: string) =>
    h(importUi.textField, {
      label,
      value: (draft as any)[key],
      onChange: (event: any) => change({ [key]: event?.target?.value ?? "" }),
    });
  const pick = (key: string, title: string, mode: "file" | "folder" | "any") =>
    void showSteamFilePicker(importUi, { title, mode }).then(
      (chosen) => chosen && change({ [key]: chosen }),
    );
  const save = () =>
    void libraryImportPage.send("addManualSource", { source: draft }).then(
      () => {
        close();
      },
      (failure: any) => setError(importError(failure)),
    );
  const remove = () =>
    showSteamUiConfirm(importUi, {
      title: "Remove this manual source?",
      text: "Stops scanning this source. The imported shortcut and backing files stay.",
      confirmLabel: "Remove source",
      onConfirm: () =>
        void libraryImportPage.send("removeManualSource", { id: draft.id }).then(
          close,
          (failure: any) => setError(importError(failure)),
        ),
    });
  return renderSteamUiSheet(
    importUi,
    {
      error,
      actions: [
        ...(draft.id ? [{ id: "remove", label: "Remove source…", onClick: remove }] : []),
        { id: "cancel", label: "Cancel", onClick: close },
        {
          id: "save",
          label: draft.id ? "Save and preview" : "Add and preview",
          primary: true,
          disabled: !draft.name || !draft.target,
          onClick: save,
        },
      ],
    },
    renderSteamUiHeader(importUi,{title:draft.id ? "Edit manual shortcut" : "Add manual shortcut",page:true}),
    h("p",{className:"steam-ui-kit-sheet-note"},"Preview this command before saving it to Steam."),
    field("Title", "name"),
    h(
      importUi.dialogButton,
      { onClick: () => pick("target", "Choose executable", "file") },
      draft.target || "Choose executable…",
    ),
    renderSteamUiGroup(importUi,{title:"Launch options & storage",collapsed:!showOptional,onToggle:()=>setShowOptional(!showOptional)},
    field("Arguments (optional)", "arguments"),
    h(
      importUi.dialogButton,
      { onClick: () => pick("workingDirectory", "Choose working folder", "folder") },
      draft.workingDirectory || "Working folder (optional)…",
    ),
    h(
      importUi.dialogButton,
      { onClick: () => pick("contentPath", "Choose backing game file", "file") },
      draft.contentPath || "Backing game file (optional)…",
    ),
    field("Location name (optional)", "location"),
    h(importUi.toggleField, {
      label: "Track backing storage",
      checked: draft.libraryBacked,
      onChange: (value: boolean) => change({ libraryBacked: value }),
    })),
  );
}

function ImportEmulatorSetupBody({ installationId, close }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const state = useEmulatorState();
  const progress = useEmulatorProgress();
  const [error, setError] = react.useState("");

  const installation = (state?.emulators?.installations ?? []).find(
    (item: any) => item.id === installationId,
  );
  const definition = (state?.emulators?.definitions ?? []).find(
    (item: any) => item.id === installation?.definitionId,
  );
  const choose = (prerequisite: any, mode: "file" | "folder" | "any") =>
    void showSteamFilePicker(importUi, {
      title: `Choose ${prerequisite.name}`,
      mode,
      extensions: prerequisite.extensions,
    }).then(
      (path) =>
        path &&
        void emulatorPage.send("configureEmulatorPrerequisite", {
          installationId,
          path,
          kind: prerequisite.kind,
        }).then(
          () => setError(""),
          (failure: any) => setError(importError(failure)),
        ),
    );
  return renderSteamUiSheet(
    importUi,
    {
      note: "Choose BIOS, firmware or system files you already have.",
      error,
      actions: [
        {
          id: "recheck",
          label: "Recheck setup",
          disabled: progress.busy,
          onClick: () => void emulatorAct("refreshEmulators"),
        },
        ...(progress.busy
          ? [{ id: "stop", label: "Stop operation", onClick: () => void emulatorAct("cancel") }]
          : []),
        { id: "close", label: "Close", onClick: close },
      ],
    },
    progress.busy
      ? renderSteamUiProgress(importUi, { phase: progress.status || "Working…" })
      : renderSteamUiStatus(importUi, [progress.status]),
    ...(definition?.prerequisites ?? []).map((prerequisite: any) =>
      h(
        importUi.focusable,
        { key: prerequisite.kind, className: "wsgm-import-pane", "flow-children": "column" },
        h("h3", {}, prerequisite.name),
        h("p", {}, prerequisite.description),
        installation?.configuredPrerequisites?.[prerequisite.kind]
          ? h("p", {}, "Configured: " + installation.configuredPrerequisites[prerequisite.kind])
          : null,
        h(
          importUi.dialogButton,
          { disabled: progress.busy, onClick: () => choose(prerequisite, "file") },
          "Choose file…",
        ),
        prerequisite.allowDirectory
          ? h(
              importUi.dialogButton,
              { disabled: progress.busy, onClick: () => choose(prerequisite, "folder") },
              "Choose folder…",
            )
          : null,
      ),
    ),
  );
}

// One poster in the grid. Memoized on what it draws: after a scan a publication changes a few
// titles, and redrawing every card for it was what made the D-pad lag on a large library.
function ImportCard({ entry, slot, asset, actions, returning }: any) {
  const react = importUi.react;
  const h = react.createElement;
  const tone = entry.excluded
    ? importExcludedTone
    : (importBadgeTones[entry.action] ?? importBadgeTones.Skip);
  const width = importAsset(asset).card;
  const launchGlyph = entry.packaged
    ? renderSteamUiIcon(react, entry.mode === "SteamIntegration" ? "display" : "controller", 16)
    : entry.follows
      ? renderSteamUiIcon(react, "launch", 16)
      : null;
  const canCycle =
    entry.editable && (entry.packaged ? entry.canUseSteamIntegration : entry.routes.length > 1);
  return h(
    "div",
    {
      className: "wsgm-import-card",
      "data-unavailable": String(importUnavailable(entry)),
      style: { width: `${width}px` },
    },
    h(ImportCapsule, {
      asset,
      width,
      image: slot?.thumb ?? "",
      placeholder: entry.name,
      dimmed: entry.excluded || importUnavailable(entry),
      overlay: [
        entry.selected
          ? h("span", { key: "check", className: "wsgm-import-check" }, renderSteamUiIcon(react, "check", 16))
          : null,
        h(
          "span",
          {
            key: "badge",
            className: "wsgm-import-badge",
            style: { background: tone.tone, color: tone.text },
          },
          entry.actionLabel,
        ),
      ],
      caption: importSlotCaption(slot),
      focus: {
        // Back from a title's artwork lands on the card it was opened from.
        autoFocus: returning,
        // A card that cannot be ticked says why rather than doing nothing.
        onActivate: () =>
          entry.selectable
            ? actions.toggle(entry)
            : importReport({ text: entry.reason, error: false }),
        onOKActionDescription: entry.selectable
          ? entry.selected
            ? "Deselect"
            : "Select"
          : "Why not",
        onSecondaryButton: canCycle ? () => cycleImportLaunch(entry) : undefined,
        onSecondaryActionDescription: canCycle
          ? entry.packaged
            ? "Launch mode"
            : "Launch route"
          : undefined,
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
    importUnavailable(entry)
      ? h("div", { className: "wsgm-import-launch" }, importAvailability(entry))
      : null,
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
    entry.availability,
    entry.availabilityLabel,
    entry.unavailable,
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
  const empty = answer?.failed
    ? "The images could not be listed."
    : answer?.status === "loading" || answer?.status === "pending" || !answer
      ? "Finding images…"
      : importArtworkLine(entry) ||
        "No images were found for this. Fix the match to search for the right game.";
  const content = h(
    "div",
    { className: "wsgm-import-pane" },
    status,
    answer?.detail && options.length
      ? h("div", { className: "wsgm-import-muted" }, answer.detail)
      : null,
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
                  ? h("span", { className: "wsgm-import-check" }, renderSteamUiIcon(react, "check", 16))
                  : null,
              caption: option.width ? `${option.width} × ${option.height}` : null,
              focus: {
                autoFocus: groupIndex === 0 && index === 0,
                onActivate: () =>
                  void importAct("pickArtwork", { id: entry.id, asset, url: option.url }),
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
      entry.matchName
        ? `Matched as “${entry.matchName}”. Wrong game?`
        : "Not matched to a game yet.",
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
      { disabled: searching, onClick: runSearch },
      searching ? "Searching…" : "Fix match",
    ),
    entry.matchFixed
      ? h(
          ui.dialogButton,
          {
            onClick: () =>
              void importAct("setMatch", { id: entry.id, provider: "", gameId: "", name: "" }),
          },
          "Use the automatic match",
        )
      : null,
    h(
      "div",
      { className: "wsgm-import-matches" },
      matches && matches.length === 0
        ? h("div", { className: "wsgm-import-muted" }, "No provider knows that name.")
        : null,
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
 * @param page Identifies the importer or standalone emulator manager route.
 * @returns The page React tree, including loading or refusal state when data is unavailable.
 */
function LibraryImportPage({ context, state }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  importUi = ui;
  const [view, setView] = react.useState({ name: "grid", title: "", asset: "grid", from: "grid" });
  const [tab, setTab] = react.useState("all");
  const [asset, setAsset] = react.useState("grid");
  const [query, setQuery] = react.useState("");
  const [fillFrom, setFillFrom] = react.useState("");
  const [returnTo, setReturnTo] = react.useState("");

  const entries: any[] = state.entries ?? [];
  const sources: any[] = state.sources ?? [];
  const busy = !!state.loading;
  const titleEntry =
    view.name === "title" ? entries.find((entry) => entry.id === view.title) : null;

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
      openDetails: (entry: any) => showImportDetails(entry, actions.openTitle),
    }),
    [],
  );

  // Every message stays visible: a refusal does not hide what the host says, and the other way
  // round. A command the page sent that was refused is the frame's banner; the save's progress is
  // the kit's progress over the stack.
  const refusal = context.refusal();
  const status = h(
    react.Fragment,
    null,
    state.phase === "applying"
      ? renderSteamUiProgress(ui, {
          phase: "Saving to Steam",
          done: state.progress ?? 0,
          total: state.progressTotal ?? 0,
        })
      : null,
    renderSteamUiStatus(ui, [
      refusal
        ? { key: "refusal", text: `The library could not be shown in full: ${refusal}`, error: true }
        : null,
      state.error ? { key: "error", text: state.error, error: true } : null,
      state.phase !== "applying" && state.notice ? { key: "notice", text: state.notice } : null,
      state.launcherDetail ? { key: "launcher", text: state.launcherDetail } : null,
    ]),
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
    h("span", { className: "wsgm-import-crumb" }, text, " ", renderSteamUiIcon(react, "chevron", 16));

  if (view.name === "title" && titleEntry) {
    header = h(
      "div",
      { className: "wsgm-import-head" },
      crumb("Game Library"),
      h("h1", {}, titleEntry.name),
      h(
        "span",
        { className: "wsgm-import-muted" },
        "Choose artwork · applied when you save to Steam",
      ),
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
          { className: "wsgm-import-bar", "flow-children": "grid" },
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
          ? renderSteamUiEmpty(ui, {
              kind: "empty",
              text: "Select titles in the review to dress them here.",
              action: { label: "Back to the review", onClick: back },
            })
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
                const slot = (entry.artwork ?? []).find(
                  (candidate: any) => candidate.asset === type.id,
                );
                return h(ImportCapsule, {
                  key: type.id,
                  asset: type.id,
                  width: type.cell,
                  image: slot?.thumb ?? "",
                  placeholder:
                    slot?.kind === "loading" ? "…" : slot?.kind === "keep" ? "Current" : "None",
                  caption: type.id === "icon" ? null : importSlotCaption(slot),
                  focus: {
                    autoFocus: rowIndex === 0 && columnIndex === 0,
                    onActivate: () => actions.openTitle(entry, type.id),
                    onOKActionDescription: "All options",
                    onOptionsButton: () =>
                      void importAct("clearArtwork", { id: entry.id, asset: type.id }),
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
        ? search
          ? renderSteamUiEmpty(ui, {
              kind: "noMatch",
              text: `No title matches "${query.trim()}".`,
              action: { label: "Clear search", onClick: () => setQuery("") },
            })
          : renderSteamUiEmpty(ui, {
              kind: "empty",
              text: entries.length ? "Nothing in this group." : "No games listed yet.",
            })
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
              const slot = (entry.artwork ?? []).find(
                (candidate: any) => candidate.asset === asset,
              );
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
        {
          key: source.id,
          className: "wsgm-import-source",
          "data-missing": String(!source.installed),
        },
        h(Check, {
          label: source.name,
          description: !source.installed
            ? source.detail || "Not found"
            : source.count >= 0
              ? String(source.count)
              : source.detail,
          checked: source.checked,
          controlled: true,
          disabled: !source.canToggle || busy,
          bottomSeparator: "none",
          onChange: (value: boolean) =>
            void importAct("setSourceEnabled", { id: source.id, enabled: !!value }),
        }),
      );
    const sourceActions = h(ui.focusable,{className:"wsgm-import-add","flow-children":"grid"},
      h(
        ui.dialogButton,
        {
          disabled: busy,
          onClick: () =>
            showSteamModal(ui, {
              title: "Add a ROM library",
              render: (close) => h(ImportRomSourceBody, { close }),
            }),
        },
        "Add ROM library…",
      ),
      h(
        ui.dialogButton,
        {
          disabled: busy,
          onClick: () =>
            showSteamModal(ui, {
              title: "Add a ROM",
              render: (close) => h(ImportRomSourceBody, { singleFile: true, close }),
            }),
        },
        "Add a ROM…",
      ),
      h(
        ui.dialogButton,
        {
          disabled: busy,
          onClick: () =>
            showSteamModal(ui, {
              title: "Add a manual shortcut",
              render: (close) => h(ImportManualSourceBody, { close }),
            }),
        },
        "Add manual shortcut…",
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
    );
    const sidebar = h(
      ui.focusable,
      { className: "wsgm-import-sidebar", "flow-children": "column" },
      h("div", { className: "wsgm-import-eyebrow" }, "Sources"),
      ...sources
        .filter(
          (source) => source.kind !== "Folder" && source.kind !== "Rom" && source.kind !== "Manual",
        )
        .map(sourceRow),
      h("div", { className: "wsgm-import-eyebrow wsgm-import-custom" }, "Custom"),
      ...sources
        .filter((source) => source.kind === "Folder")
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
      ...sources
        .filter((source) => source.kind === "Rom")
        .map((source) =>
          h(
            ui.focusable,
            { key: `${source.id}-custom`, "flow-children": "column" },
            sourceRow(source),
            h(
              ui.dialogButton,
              {
                disabled: busy,
                onClick: () =>
                  showSteamModal(ui, {
                    title: `Configure ${source.name}`,
                    render: (close) =>
                      h(source.kind === "Rom" ? ImportRomSourceBody : ImportManualSourceBody, {
                        sourceId: source.id,
                        close,
                      }),
                  }),
              },
              "Configure source…",
            ),
          ),
        ),
      ...sources.filter((source) => source.kind === "Manual").map(sourceRow),
      ...(state.manualSources ?? []).map((source: any) =>
        h(
          ui.dialogButton,
          {
            key: source.id,
            disabled: busy,
            onClick: () =>
              showSteamModal(ui, {
                title: `Configure ${source.name}`,
                render: (close) =>
                  h(source.romPath ? ImportRomSourceBody : ImportManualSourceBody, {
                    sourceId: source.id,
                    singleFile: !!source.romPath,
                    close,
                  }),
              }),
          },
          `Configure ${source.name}…`,
        ),
      ),
      entries.length ? sourceActions : null,
      // Every tracked shortcut and card library, in one sheet: the overlay's Storage level.
      h("div", { key: "storage-head", className: "wsgm-import-eyebrow wsgm-import-custom" }, "Storage"),
      renderSteamUiSelectRow(ui, {
        key: "storage",
        title: "Storage",
        detail: importStorageSummary(state),
        onClick: () => showImportStorage(actions.openTitle),
      }),
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
        entry.selected &&
        (!active.group || entry.group === active.group) &&
        (!search || String(entry.name).toLowerCase().includes(search)),
    );
    const toolbar = h(
      ui.focusable,
      { className: "wsgm-import-bar", "flow-children": "grid" },
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
              placeholder: "Search titles",
              onChange: (event: any) => setQuery(event?.target?.value ?? ""),
            }),
          )
        : null,
      h("div", { className: "wsgm-import-spacer" }),
      h(
        ui.dialogButton,
        {
          disabled: !state.selectedCount,
          onClick: () => setView({ name: "all", title: "", asset: "grid", from: "grid" }),
        },
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
          onClick: () =>
            void importAct("select", {
              group: active.group,
              query: search,
              selected: !anySelected,
            }),
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
        entries.length ? h(ui.tabs, {
          autoFocusContents: true,
          activeTab: active.id,
          onShowTab: (next: string) => setTab(next),
          tabs: importTabs.map((candidate) => ({
            id: candidate.id,
            title: `${candidate.title} ${counts[candidate.group || "all"] ?? 0}`,
            content:
              candidate.id === active.id
                ? h("div", { className: "wsgm-import-pane" }, toolbar, status, ...grid)
                : null,
          })),
        }) : h(ui.focusable,{className:"wsgm-import-pane","flow-children":"column",autoFocus:true},
          status,
          h("div",{className:"steam-ui-kit-box wsgm-import-empty"},
            h("h2",{},busy ? "Finding your games…" : "Add games to Steam"),
            h("p",{},"Scan the checked sources to review your games before saving. You can also add ROMs, a shortcut or a folder."),
            busy ? h(ui.dialogButton,{onClick:()=>void importAct("cancel")},"Stop scan")
              : h(ui.dialogButtonPrimary,{autoFocus:true,onClick:()=>void importAct("scan")},"Scan for games"),
            sourceActions)),
      ),
    ];
  }

  return h(
    react.Fragment,
    null,
    header,
    h("div", { className: "wsgm-import-body" }, ...(Array.isArray(body) ? body : [body])),
  );
}

const libraryImportPage = registerSteamPage({
  template: "library-import",
  gate: "libraryImport",
  patchId: LibraryImportPatchId,
  title: "Game Library",
  id: "wsgm-import",
  style: importStyles,
  loading: "Loading your game library…",
  // The host's error, notice and launcher detail are lines in the review's status stack.
  banner: false,
  // Steam's checkbox and bare dropdown are wanted, not required: the sidebar falls back to Steam's
  // toggle and the toolbar to its labelled field.
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

/**
 * Draws one tab of the emulator tool from the host's state.
 * @param props The frame's context, the host's state and the active tab.
 * @returns The tab's content.
 */
function EmulatorManagerPage({ context, state, tab }: any) {
  importUi = context.ui();
  return context.react().createElement(ImportEmulatorPage, { context, state, tab });
}

const emulatorPage = registerSteamPage({
  template: "emulators",
  gate: "emulatorManager",
  patchId: EmulatorPatchId,
  title: "Emulator Manager",
  id: "wsgm-emulators",
  style: emulatorStyles,
  loading: "Loading emulators…",
  // The emulator tool publishes no notice of its own; a refused command is the frame's banner.
  banner: false,
  tabs: { source: "local", initial: "installed", list: (state) => emulatorTabs(state) },
  // The progress publication is followed while the gate holds, so every sheet sees it.
  prepare: () => {
    try {
      emulatorProgressEnd = subscribe(EmulatorProgressId, (progress: any) => {
        emulatorProgress = progress ?? { busy: false, status: "" };
        emulatorProgressStore.changed();
      });
    } catch {
      // A host without the progress publication runs every operation without a progress line.
      emulatorProgressEnd = null;
    }
    return null;
  },
  release: () => {
    endSubscription(emulatorProgressEnd);
    emulatorProgressEnd = null;
    emulatorProgress = { busy: false, status: "" };
    emulatorProgressStore.changed();
    emulatorRouteMemory.reset();
  },
  status: () => ({ busy: !!emulatorProgress.busy }),
  Page: EmulatorManagerPage,
});
