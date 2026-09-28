// The Animations page in Steam: SteamDeckRepo's boot and suspend movies browsed, downloaded, and
// assigned to the slots Steam plays them from.
//
// Laid out the way Animation Changer lays out its browser: a toolbar over a grid of cards, one
// movie's preview and details, and the library with its slots. Drawn with Steam's own components
// where one fits and the toolkit's UI kit for the rest. WSGM owns the list, the library, the
// assignment and the override files; the toolkit owns the page gate, the kit, the modal frame and
// the fail-closed component discovery used here.
const AnimationsPatchId = "steam-ui.animations";

let animationsUi: any = null;

const animationsAct = (command: string, payload: any = {}) =>
  request(AnimationsPatchId, command, payload).catch(() => undefined);

const animationsTabs = [
  { id: "browse", title: "Browse" },
  { id: "library", title: "Library" },
  { id: "settings", title: "Settings" },
];

// The slots as the host names them, and the stock choice's label.
const animationSlots = [
  { id: "boot", label: "Boot", target: "boot" },
  { id: "suspend", label: "Suspend", target: "suspend" },
  { id: "throbber", label: "Suspend from a game", target: "suspend" },
];
const AnimationsStock = "Steam's own";
const animationFits = (item, slot) => item.target === "any" || item.target === slot.target;

const animationsGlyphs = {
  download: "M11 3h2v9.2l3.6-3.6 1.4 1.4-6 6-6-6 1.4-1.4L11 12.2zM4 19h16v2H4z",
  heart: "M12 21s-7-4.6-9.3-9.1C1 8.5 3.2 5 6.7 5c2 0 3.4 1 4.3 2.3C12 6 13.4 5 15.3 5c3.5 0 5.7 3.5 4 6.9C19 16.4 12 21 12 21z",
};
const animationsGlyph = (react: any, name: keyof typeof animationsGlyphs) =>
  renderSteamGlyph(react, animationsGlyphs[name]);

// A card in the grid: the still, likes and downloads, a badge once the library holds it, and the
// author and date under the name.
const animationsCard = (ui, item, open) => {
  const react = ui.react;
  return renderSteamUiCard(ui, {
    key: item.id,
    image: item.thumbnailUrl,
    stats: [
      { glyph: animationsGlyph(react, "heart"), text: String(item.likes ?? 0) },
      { glyph: animationsGlyph(react, "download"), text: String(item.downloads ?? 0) },
    ],
    badge: item.downloaded ? { text: item.custom ? "Your file" : "In library" } : null,
    title: item.name,
    meta: [
      [item.target === "boot" ? "Boot" : item.custom ? "Any slot" : "Suspend", item.updated].filter(Boolean).join(" · "),
      item.author ? `By ${item.author}` : "",
    ],
    onActivate: () => open(item.id),
  });
};

// The chips that put a movie into a slot it fits, marking the one it already plays in.
const animationsSlotChips = (ui, item, slots) =>
  renderSteamUiChips(
    ui,
    animationSlots
      .filter((slot) => animationFits(item, slot))
      .map((slot) => ({
        label: slots?.[slot.id] === item.id ? `${slot.label} ✓` : slot.label,
        description: `Play at ${slot.label.toLowerCase()}`,
        onClick: () => void animationsAct("setSlot", { slot: slot.id, id: item.id }),
      })),
  );

function AnimationsDetail({ state }: any) {
  const ui = animationsUi;
  const react = ui.react;
  const h = react.createElement;
  const item = state.detail;
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
        item.author ? `By ${item.author}` : "",
        item.updated ? ` · ${item.updated}` : "",
        ` · ${item.target === "boot" ? "Boot" : item.custom ? "Any slot" : "Suspend"}`,
      ),
      h("h3", null, "Description"),
      h(
        "p",
        { className: item.description ? "" : "steam-ui-kit-muted" },
        item.description || "No description provided.",
      ),
    ),
    h(
      "div",
      { className: "wsgm-animations-detail-right" },
      item.downloaded
        ? renderSteamUiBox(
            react,
            "Plays at",
            animationsSlotChips(ui, item, state.slots),
            h(
              "div",
              { className: "steam-ui-kit-muted" },
              "Steam reads the movie when it starts, so a change shows after the next Steam start.",
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
            h("div", { className: "steam-ui-kit-muted" }, "Into WSGM's library; choose a slot for it afterwards."),
          ),
      item.downloaded
        ? h(
            ui.dialogButton,
            {
              onClick: () =>
                showSteamUiConfirm(ui, {
                  title: "Remove animation",
                  text: `Remove ${item.name} from the library? A slot playing it goes back to Steam's own movie.`,
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
      void animationsAct("browse", { type: browse.type ?? "all", sort: browse.sort ?? "Newest", search: browse.search ?? "" });
    }
  }, []);
  const ask = (changes: any) =>
    void animationsAct("browse", { type: browse.type ?? "all", sort: browse.sort ?? "Newest", search, ...changes });
  if (state.detail) return h(AnimationsDetail, { state });

  const items: any[] = browse.items ?? [];
  const open = (id: string) => void animationsAct("open", { id });
  const typeOptions = [
    { data: "all", label: "All" },
    { data: "boot", label: "Boot" },
    { data: "suspend", label: "Suspend" },
  ];
  const sortOptions = ["Newest", "Oldest", "Alphabetical", "Most popular", "Most liked"].map((sort) => ({
    data: sort,
    label: sort,
  }));
  return h(
    "div",
    { className: "steam-ui-kit-pane" },
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        "Type",
        renderSteamDropdown(ui, {
          label: "Type",
          rgOptions: typeOptions,
          selectedOption: browse.type ?? "all",
          onChange: (option) => ask({ type: option?.data }),
        }),
      ),
      renderSteamUiTool(
        ui,
        "Sort",
        renderSteamDropdown(ui, {
          label: "Sort",
          rgOptions: sortOptions,
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
      items.map((item) => animationsCard(ui, item, open)),
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
  // One row per slot: Steam's own movie, or one of the library's that fits the slot.
  const slotRows = animationSlots.map((slot) => {
    const fitting = library.filter((item) => animationFits(item, slot));
    const choices = [
      { value: "", label: AnimationsStock },
      ...fitting.map((item) => ({ value: item.id, label: item.name })),
    ];
    const current = fitting.some((item) => item.id === state.slots?.[slot.id]) ? state.slots[slot.id] : "";
    return renderSteamSettingRow(
      ui,
      { key: `slot:${slot.id}`, kind: "choice", label: slot.label, text: current, choices },
      undefined,
      (_row, value, commit = true) => {
        if (commit) void animationsAct("setSlot", { slot: slot.id, id: String(value) });
      },
      () => {},
    );
  });
  return h(
    "div",
    { className: "steam-ui-kit-pane" },
    renderSteamUiToolbar(
      ui,
      h(ui.dialogButton, { disabled: !!state.busy || library.length === 0, onClick: () => void animationsAct("shuffle") }, "Shuffle"),
      h(ui.dialogButton, { disabled: !!state.busy, onClick: addFile }, "Add a video file…"),
    ),
    h(ui.settingsSection, { label: "Slots" }, ...slotRows),
    state.settings?.restartNeeded
      ? renderSteamUiEmpty(react, "A slot changed since Steam started. Restart Steam to see it.")
      : null,
    library.length === 0
      ? renderSteamUiEmpty(react, "Nothing in the library yet. Download an animation under Browse, or add a WebM file.")
      : renderSteamUiGrid(
          ui,
          library.map((item) => animationsCard(ui, item, open)),
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
      description: "Picks every slot anew from the library each time WSGM starts, before Steam does.",
      checked: !!settings.shuffleOnStart,
    },
    {
      key: "note",
      kind: "note",
      label: "How it works",
      text: "A slot's movie is copied to the file Steam asks for under its uioverrides folder; Steam reads it when it starts. Keep Steam's own Startup Movie setting on the default.",
    },
    { key: "library", kind: "note", label: "Library folder", text: settings.libraryPath ?? "" },
    { key: "overrides", kind: "note", label: "Steam's override folder", text: settings.overridesPath ?? "Steam is not installed" },
  ];
  return h(
    "div",
    { className: "steam-ui-kit-pane" },
    h(
      ui.settingsSection,
      { label: "Animations" },
      ...rows.map((row) =>
        renderSteamSettingRow(
          ui,
          row,
          undefined,
          (changed, value, commit = true) => {
            if (commit && changed.key === "shuffleOnStart") void animationsAct("setSetting", { key: "shuffleOnStart", value: !!value });
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
  if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading animations…");

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
    { id: "wsgm-animations", className: "steam-ui-kit-page", "aria-label": "Animations" },
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
