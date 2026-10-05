// The Animations page in Steam: SteamDeckRepo's boot movies browsed, downloaded, and chosen for
// Big Picture's start.
//
// Laid out the way Animation Changer lays out its browser: a toolbar over a grid of cards, one
// movie's preview and details, and the library with the choice. Drawn with Steam's own components
// where one fits and the toolkit's UI kit for the rest, the tabbed frame and the detail included.
// WSGM owns the list, the library, the choice, the sorts and the override file; the toolkit owns
// the page gate, the kit, the modal frame and the fail-closed component discovery used here. Only
// the boot movie is offered: nothing on Windows drives Steam's suspend flow, so its suspend movies
// never play.
const AnimationsPatchId = "wsgm.animations";

let animationsUi: any = null;

const animationsAct = wsgmPageAct(AnimationsPatchId);

const animationsTabs = [
  { id: "browse", title: "Browse" },
  { id: "library", title: "Library" },
  { id: "settings", title: "Settings" },
];

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
          { glyph: renderSteamUiGlyph(react, "heart"), text: String(item.likes ?? 0) },
          { glyph: renderSteamUiGlyph(react, "download"), text: String(item.downloads ?? 0) },
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
  const back = () => void animationsAct("closeDetail");
  return renderSteamUiDetail(ui, {
    title: item.name,
    media: renderSteamUiVideo(react, {
      src: item.previewUrl,
      poster: item.thumbnailUrl,
      empty: item.custom ? "Your file has no preview here" : "No preview",
    }),
    main: [
      h(
        "div",
        { className: "steam-ui-kit-muted" },
        [item.author ? `By ${item.author}` : "", item.updated].filter(Boolean).join(" · "),
      ),
      h("h3", null, "Description"),
      h("p", { className: item.description ? "" : "steam-ui-kit-muted" }, item.description || "No description provided."),
    ],
    aside: [
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
                  onConfirm: () => void animationsAct("delete", { id: item.id }).then(back),
                }),
            },
            "Remove from library",
          )
        : null,
    ],
    onBack: back,
  });
}

function AnimationsBrowse({ state }: any) {
  const ui = animationsUi;
  const react = ui.react;
  const h = react.createElement;
  const browse = state.browse ?? {};
  const sorts: any[] = browse.sorts ?? [];
  const [search, setSearch] = react.useState(browse.search ?? "");
  react.useEffect(() => setSearch(browse.search ?? ""), [browse.search]);
  // The first look at the repository is the page's own: nothing is fetched until someone opens the tab.
  react.useEffect(() => {
    if (!browse.loading && !browse.error && !browse.total) {
      void animationsAct("browse", { sort: browse.sort ?? "", search: browse.search ?? "" });
    }
  }, []);
  const ask = (changes: any) => void animationsAct("browse", { sort: browse.sort ?? "", search, ...changes });
  if (state.detail) return h(AnimationsDetail, { state });

  const items: any[] = browse.items ?? [];
  const open = (id: string) => void animationsAct("open", { id });
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        "Sort",
        renderSteamDropdown(ui, {
          label: "Sort",
          rgOptions: sorts.map((sort) => ({ data: sort.id, label: sort.label })),
          selectedOption: browse.sort,
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
    // A page at a time: the repository lists thousands, and a card for each stalls Steam.
    items.length < (browse.matched ?? 0)
      ? renderSteamUiMore(ui, {
          label: `Load More (${items.length} of ${browse.matched})`,
          onClick: () => void animationsAct("more"),
        })
      : null,
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
        { value: "", label: state.stockName ?? "" },
        ...library.map((item) => ({ value: item.id, label: item.name })),
      ],
    },
    undefined,
    (_row, value, commit = true) => {
      if (commit) void animationsAct("select", { id: String(value) });
    },
    () => {},
  );
  return renderSteamUiPane(
    ui,
    {},
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
      key: "bootVolume",
      kind: "range",
      label: "Volume",
      description:
        "How loud the boot movie plays, against its file. Steam plays a movie other than its own twice at once, so 100% takes that back. Works on movies whose sound is Opus.",
      number: settings.bootVolume ?? 100,
      minimum: 0,
      maximum: 100,
      step: 5,
      suffix: "%",
    },
    {
      key: "note",
      kind: "note",
      label: "How it works",
      text: "The chosen movie is copied to the file Steam asks for under its uioverrides folder; Steam reads it when it starts. While one of these movies is chosen, Steam's own Startup Movie choice is set aside, and it comes back when you choose Steam's own here.",
    },
    { key: "library", kind: "note", label: "Library folder", text: settings.libraryPath ?? "" },
    {
      key: "overrides",
      kind: "note",
      label: "Steam's override folder",
      text: settings.overridesPath ?? "Steam is not installed",
    },
  ];
  return renderSteamUiPane(
    ui,
    {},
    h(
      ui.settingsSection,
      { label: "Boot animation" },
      ...rows.map((row) =>
        renderSteamSettingRow(
          ui,
          row,
          undefined,
          (changed, value, commit = true) => {
            if (commit && changed.key === "shuffleOnStart") void animationsAct("setShuffleOnStart", { value: !!value });
            if (commit && changed.key === "bootVolume") void animationsAct("setBootVolume", { value: Math.round(Number(value)) });
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

  return renderSteamUiTabbedPage(ui, {
    id: "wsgm-animations",
    label: "Boot animation",
    style: animationsStyles,
    tabs: animationsTabs,
    ...wsgmPageFrame(animationsAct, state),
    content: (id) => {
      switch (id) {
        case "library":
          return h(AnimationsLibrary, { state });
        case "settings":
          return h(AnimationsSettings, { state });
        default:
          return h(AnimationsBrowse, { state });
      }
    },
  });
}

// The page's own layout: where the kit's elements go, not how they look.
const animationsStyles = `
#wsgm-animations .steam-ui-kit-tool:not(.grow) { width: 200px; }
`;

const animationsPage = registerSteamPage({
  template: "animations",
  gate: "animations",
  patchId: AnimationsPatchId,
  components: resolveSteamSettingsComponents,
  required: SteamUiTabbedPageRequired,
  status: () => ({ tab: animationsPage.state()?.activeTab ?? "" }),
  Page: AnimationsPage,
});
