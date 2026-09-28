// The Animations page in Steam: SteamDeckRepo's boot movies browsed, downloaded, and chosen for
// Big Picture's start.
//
// Laid out the way Animation Changer lays out its browser: a toolbar over a grid of cards, one
// movie's preview and details, and the library with the choice. Drawn with Steam's own components
// where one fits and the toolkit's UI kit for the rest. WSGM owns the list, the library, the choice
// and the override file; the toolkit owns the page gate, the kit, the modal frame and the
// fail-closed component discovery used here. Only the boot movie is offered: nothing on Windows
// drives Steam's suspend flow, so its suspend movies never play.
const AnimationsPatchId = "steam-ui.animations";

let animationsUi: any = null;

const animationsAct = (command: string, payload: any = {}) =>
  request(AnimationsPatchId, command, payload).catch(() => undefined);

const animationsTabs = [
  { id: "browse", title: "Browse" },
  { id: "library", title: "Library" },
  { id: "settings", title: "Settings" },
];
const AnimationsStock = "Steam's own";
const AnimationsSorts = ["Newest", "Oldest", "Alphabetical", "Most popular", "Most liked"];

const animationsGlyphs = {
  download: "M11 3h2v9.2l3.6-3.6 1.4 1.4-6 6-6-6 1.4-1.4L11 12.2zM4 19h16v2H4z",
  heart:
    "M12 21s-7-4.6-9.3-9.1C1 8.5 3.2 5 6.7 5c2 0 3.4 1 4.3 2.3C12 6 13.4 5 15.3 5c3.5 0 5.7 3.5 4 6.9C19 16.4 12 21 12 21z",
};
const animationsGlyph = (react: any, name: keyof typeof animationsGlyphs) =>
  renderSteamGlyph(react, animationsGlyphs[name]);

// A card in the grid: the still, likes and downloads, a badge once the library holds it or it
// plays at boot, and the author and date under the name.
const animationsCard = (ui, item, selected, open) => {
  const react = ui.react;
  return renderSteamUiCard(ui, {
    key: item.id,
    image: item.thumbnailUrl,
    stats: item.custom
      ? []
      : [
          { glyph: animationsGlyph(react, "heart"), text: String(item.likes ?? 0) },
          { glyph: animationsGlyph(react, "download"), text: String(item.downloads ?? 0) },
        ],
    badge:
      item.id === selected
        ? { text: "Plays at boot", warn: true }
        : item.downloaded
          ? { text: item.custom ? "Your file" : "In library" }
          : null,
    title: item.name,
    meta: [item.custom ? "Your file" : item.updated || "", item.author ? `By ${item.author}` : ""],
    onActivate: () => open(item.id),
  });
};

function AnimationsDetail({ state }: any) {
  const ui = animationsUi;
  const react = ui.react;
  const h = react.createElement;
  const item = state.detail;
  const playing = state.selected === item.id;
  return h(
    ui.focusable,
    {
      className: "wsgm-animations-detail",
      onCancelButton: () => void animationsAct("closeDetail"),
      onCancelActionDescription: "Back",
    },
    h(
      "div",
      { className: "wsgm-animations-detail-left" },
      renderSteamUiVideo(react, {
        src: item.previewUrl,
        poster: item.thumbnailUrl,
        empty: item.custom ? "Your file has no preview here" : "No preview",
      }),
      h("div", { className: "wsgm-animations-heading" }, h("h2", null, item.name)),
      h(
        "div",
        { className: "steam-ui-kit-muted" },
        [item.author ? `By ${item.author}` : "", item.updated].filter(Boolean).join(" · "),
      ),
      h("h3", null, "Description"),
      h("p", { className: item.description ? "" : "steam-ui-kit-muted" }, item.description || "No description provided."),
    ),
    h(
      "div",
      { className: "wsgm-animations-detail-right" },
      item.downloaded
        ? renderSteamUiBox(
            react,
            playing ? "Plays at boot" : "In the library",
            h(
              ui.dialogButtonPrimary,
              { disabled: playing, onClick: () => void animationsAct("select", { id: item.id }) },
              playing ? "Big Picture starts with this" : "Start Big Picture with this",
            ),
            h(
              "div",
              { className: "steam-ui-kit-muted" },
              "Steam reads the movie when it starts, so a change shows at the next Steam start.",
            ),
          )
        : renderSteamUiBox(
            react,
            `Download ${item.name}`,
            h("div", { className: "steam-ui-kit-muted" }, `${item.downloads ?? 0} downloads · ${item.likes ?? 0} likes`),
            h(
              ui.dialogButtonPrimary,
              { disabled: !!state.busy, onClick: () => void animationsAct("download", { id: item.id }) },
              state.busy ? "Working…" : "Download",
            ),
            h("div", { className: "steam-ui-kit-muted" }, "Into WSGM's library; choose it there afterwards."),
          ),
      item.downloaded
        ? h(
            ui.dialogButton,
            {
              onClick: () =>
                showSteamUiConfirm(ui, {
                  title: "Remove movie",
                  text: `Remove ${item.name} from the library? If it plays at boot, Steam's own movie plays again.`,
                  confirmLabel: "Remove",
                  onConfirm: () => void animationsAct("delete", { id: item.id }).then(() => animationsAct("closeDetail")),
                }),
            },
            "Remove from library",
          )
        : null,
      h(ui.dialogButton, { onClick: () => void animationsAct("closeDetail") }, "Back"),
    ),
  );
}

function AnimationsBrowse({ state }: any) {
  const ui = animationsUi;
  const react = ui.react;
  const h = react.createElement;
  const browse = state.browse ?? {};
  const [search, setSearch] = react.useState(browse.search ?? "");
  react.useEffect(() => setSearch(browse.search ?? ""), [browse.search]);
  // The first look at the repository is the page's own: nothing is fetched until someone opens the tab.
  react.useEffect(() => {
    if (!browse.loading && !browse.error && !browse.total) {
      void animationsAct("browse", { sort: browse.sort ?? "Newest", search: browse.search ?? "" });
    }
  }, []);
  const ask = (changes: any) => void animationsAct("browse", { sort: browse.sort ?? "Newest", search, ...changes });
  if (state.detail) return h(AnimationsDetail, { state });

  const items: any[] = browse.items ?? [];
  const open = (id: string) => void animationsAct("open", { id });
  return h(
    "div",
    { className: "steam-ui-kit-pane" },
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        "Sort",
        renderSteamDropdown(ui, {
          label: "Sort",
          rgOptions: AnimationsSorts.map((sort) => ({ data: sort, label: sort })),
          selectedOption: browse.sort ?? "Newest",
          onChange: (option) => ask({ sort: option?.data }),
        }),
      ),
      renderSteamUiTool(
        ui,
        null,
        h(ui.textField, {
          label: "Search",
          value: search,
          onChange: (event) => setSearch(event?.target?.value ?? ""),
          onBlur: () => {
            if (search !== (browse.search ?? "")) ask({ search });
          },
        }),
        true,
      ),
      h(ui.dialogButton, { disabled: !!browse.loading, onClick: () => void animationsAct("refresh") }, "Refresh"),
    ),
    browse.error ? renderSteamUiEmpty(react, browse.error, true) : null,
    renderSteamUiGrid(
      ui,
      items.map((item) => animationsCard(ui, item, state.selected, open)),
    ),
    browse.loading
      ? renderSteamUiEmpty(react, "Asking the repository…")
      : items.length === 0 && !browse.error
        ? renderSteamUiEmpty(react, browse.total ? "Nothing matched." : "The repository has not answered yet.")
        : null,
  );
}

function AnimationsLibrary({ state }: any) {
  const ui = animationsUi;
  const react = ui.react;
  const h = react.createElement;
  if (state.detail) return h(AnimationsDetail, { state });
  const library: any[] = state.library ?? [];
  const open = (id: string) => void animationsAct("open", { id });
  const addFile = () =>
    void showSteamFilePicker(ui, { title: "Choose a WebM movie", mode: "file", extensions: [".webm"] }).then(
      (chosen) => chosen && animationsAct("addFile", { path: chosen }),
    );
  const choice = renderSteamSettingRow(
    ui,
    {
      key: "boot",
      kind: "choice",
      label: "Boot movie",
      description: "What Big Picture starts with.",
      text: state.selected ?? "",
      choices: [
        { value: "", label: AnimationsStock },
        ...library.map((item) => ({ value: item.id, label: item.name })),
      ],
    },
    undefined,
    (_row, value, commit = true) => {
      if (commit) void animationsAct("select", { id: String(value) });
    },
    () => {},
  );
  return h(
    "div",
    { className: "steam-ui-kit-pane" },
    renderSteamUiToolbar(
      ui,
      h(
        ui.dialogButton,
        { disabled: !!state.busy || library.length === 0, onClick: () => void animationsAct("shuffle") },
        "Shuffle",
      ),
      h(ui.dialogButton, { disabled: !!state.busy, onClick: addFile }, "Add a video file…"),
    ),
    h(ui.settingsSection, { label: "Boot" }, choice),
    state.settings?.restartNeeded
      ? renderSteamUiEmpty(react, "The boot movie changed since Steam started. Restart Steam to see it.")
      : null,
    library.length === 0
      ? renderSteamUiEmpty(react, "Nothing in the library yet. Download a movie under Browse, or add a WebM file.")
      : renderSteamUiGrid(
          ui,
          library.map((item) => animationsCard(ui, item, state.selected, open)),
        ),
  );
}

function AnimationsSettings({ state }: any) {
  const ui = animationsUi;
  const h = ui.react.createElement;
  const settings = state.settings ?? {};
  const rows = [
    {
      key: "shuffleOnStart",
      kind: "boolean",
      label: "Shuffle on start",
      description: "Picks the boot movie anew from the library each time WSGM starts, before Steam does.",
      checked: !!settings.shuffleOnStart,
    },
    {
      key: "note",
      kind: "note",
      label: "How it works",
      text: "The chosen movie is copied to the file Steam asks for under its uioverrides folder; Steam reads it when it starts. Keep Steam's own Startup Movie setting on the default.",
    },
    { key: "library", kind: "note", label: "Library folder", text: settings.libraryPath ?? "" },
    {
      key: "overrides",
      kind: "note",
      label: "Steam's override folder",
      text: settings.overridesPath ?? "Steam is not installed",
    },
  ];
  return h(
    "div",
    { className: "steam-ui-kit-pane" },
    h(
      ui.settingsSection,
      { label: "Boot animation" },
      ...rows.map((row) =>
        renderSteamSettingRow(
          ui,
          row,
          undefined,
          (changed, value, commit = true) => {
            if (commit && changed.key === "shuffleOnStart")
              void animationsAct("setSetting", { key: "shuffleOnStart", value: !!value });
          },
          () => {},
        ),
      ),
    ),
  );
}

// Declared once for the life of the asset, and drawn by the toolkit's page frame only once the gate
// holds: the frame says why when it does not.
function AnimationsPage({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  animationsUi = ui;
  const state = context.state();
  if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading boot movies…");

  const active = animationsTabs.some((tab) => tab.id === state.activeTab) ? state.activeTab : "browse";
  const content = (id: string) => {
    if (id !== active) return null;
    switch (id) {
      case "library":
        return h(AnimationsLibrary, { state });
      case "settings":
        return h(AnimationsSettings, { state });
      default:
        return h(AnimationsBrowse, { state });
    }
  };
  const banner = state.error || state.notice;
  return h(
    "div",
    { id: "wsgm-animations", className: "steam-ui-kit-page", "aria-label": "Boot animation" },
    steamUiKitStyle(react),
    h("style", null, animationsStyles),
    banner
      ? h(
          "div",
          { className: "wsgm-animations-banner" },
          renderSteamUiBanner(ui, { text: banner, error: !!state.error, onDismiss: () => void animationsAct("dismiss") }),
        )
      : null,
    h(ui.tabs, {
      autoFocusContents: true,
      activeTab: active,
      onShowTab: (tab) => void animationsAct("setTab", { tab }),
      tabs: animationsTabs.map((tab) => ({ id: tab.id, title: tab.title, content: content(tab.id) })),
    }),
  );
}

// The page's own layout: where the kit's elements go, not how they look.
const animationsStyles = `
#wsgm-animations div[class*="gamepadtabbedpage_TabHeaderRowWrapper"] { background: #1b2838; }
#wsgm-animations .wsgm-animations-banner { margin: 8px 48px 0; }
#wsgm-animations .steam-ui-kit-tool:not(.grow) { width: 200px; }
#wsgm-animations .wsgm-animations-detail { display: flex; gap: 32px; padding: 12px 4px 72px; }
#wsgm-animations .wsgm-animations-detail-left { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 10px; }
#wsgm-animations .wsgm-animations-detail-right { width: 300px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 14px; }
#wsgm-animations .wsgm-animations-heading h2 { margin: 0; font-size: 30px; font-weight: 700; color: #fff; }
#wsgm-animations h3 { margin: 6px 0 0; font-size: 15px; font-weight: 700; color: #fff; }
#wsgm-animations p { margin: 0; font-size: 14px; line-height: 1.5; color: #c6d4df; max-width: 700px; white-space: pre-wrap; }
`;

const animationsPage = registerSteamPage({
  template: "animations",
  gate: "animations",
  patchId: AnimationsPatchId,
  components: resolveSteamSettingsComponents,
  required: [
    "react",
    "focusable",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "dialogButton",
    "dialogButtonPrimary",
    "smallButton",
    "valueField",
    "settingsSection",
    "tabs",
    "modalRoot",
    "showModal",
  ],
  status: () => ({ tab: animationsPage.state()?.activeTab ?? "" }),
  Page: AnimationsPage,
});
