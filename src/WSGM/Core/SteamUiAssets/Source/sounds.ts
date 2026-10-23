// UI sounds frontend; SoundPackService owns the library, the store, the choice and the previews.
const SoundsPatchId = "wsgm.sounds";
const SoundsRoute = "/wsgm/sounds";

let soundsUi: any = null;

const soundsAct = wsgmPageAct(SoundsPatchId);

const soundsTabs = [
  { id: "installed", title: "Installed" },
  { id: "browse", title: "Browse" },
];

// The store's pages: the next page asks the store again, as the overlay pages through it.
const soundsBrowse = (page: number, search: string) => void soundsAct("browse", { page, search });

const soundsPackLine = (pack) =>
  [pack.author ? `By ${pack.author}` : "", pack.version].filter(Boolean).join(" · ");

// A card in the store's grid: the screenshot, counts, an Installed or Update badge, author and date.
const soundsCard = (ui, item, open) => {
  const react = ui.react;
  const badge =
    item.installedVersion == null
      ? null
      : item.installedVersion === item.version
        ? { text: "Installed" }
        : { text: "Update", warn: true };
  return renderSteamUiCard(ui, {
    key: item.id,
    image: item.imageUrl,
    stats: [
      { glyph: renderSteamUiGlyph(react, "download"), text: String(item.downloads ?? 0) },
      { glyph: renderSteamUiGlyph(react, "star"), text: String(item.stars ?? 0) },
    ],
    badge,
    title: item.name,
    meta: [
      item.updated ? `${item.version} - Last Updated ${item.updated}` : item.version,
      item.author ? `By ${item.author}` : "",
    ],
    onActivate: () => open(item.id),
  });
};

function SoundsListing({ state, item, onBack }: any) {
  const ui = soundsUi;
  const react = ui.react;
  const h = react.createElement;
  const installed = item.installedVersion != null;
  const label = !installed
    ? "Install"
    : item.installedVersion === item.version
      ? "Reinstall"
      : "Update";
  return renderSteamUiDetail(ui, {
    title: item.name,
    badge: item.version,
    media: item.imageUrl
      ? h("img", { className: "wsgm-sounds-shot", src: item.imageUrl, alt: "" })
      : null,
    main: [
      h(
        "div",
        { className: "steam-ui-kit-muted" },
        [item.author ? `By ${item.author}` : "", item.updated ? `Last Updated ${item.updated}` : ""]
          .filter(Boolean)
          .join(" · "),
      ),
      h(
        "p",
        { className: "steam-ui-kit-muted" },
        installed
          ? `Installed: ${item.installedVersion}. Choose it under Installed or on Settings > Customization.`
          : "An Audio Loader pack from DeckThemes. Sounds it does not cover keep Steam's own.",
      ),
    ],
    aside: [
      renderSteamUiBox(
        react,
        `${label} ${item.name}`,
        h("div", { className: "steam-ui-kit-muted" }, `${item.downloads ?? 0} Downloads`),
        h(
          ui.dialogButtonPrimary,
          { disabled: !!state.busy, onClick: () => void soundsAct("install", { id: item.id }) },
          state.busy ? "Working…" : label,
        ),
        h("div", { className: "steam-ui-kit-muted" }, "Downloads into WSGM's sound library."),
      ),
    ],
    onBack,
  });
}

function SoundsPack({ state, pack, onBack }: any) {
  const ui = soundsUi;
  const react = ui.react;
  const h = react.createElement;
  const selected = state.selected === pack.id;
  const usable = !pack.error;
  const back = () => {
    void soundsAct("stopPreview");
    onBack();
  };
  const list = (title: string, items: string[]) =>
    items.length
      ? h(
          react.Fragment,
          null,
          h("h3", null, title),
          h("div", { className: "steam-ui-kit-muted wsgm-sounds-list" }, items.join(", ")),
        )
      : null;
  return renderSteamUiDetail(ui, {
    title: pack.name,
    badge: pack.version,
    main: [
      h("div", { className: "steam-ui-kit-muted" }, soundsPackLine(pack)),
      h("h3", null, "Description"),
      h(
        "p",
        { className: pack.description ? "" : "steam-ui-kit-muted" },
        pack.error || pack.description || "No description provided.",
      ),
      pack.source ? h("div", { className: "steam-ui-kit-muted" }, `Source: ${pack.source}`) : null,
      pack.compatibility
        ? h(
            react.Fragment,
            null,
            h("h3", null, "Client compatibility"),
            h("p", null, pack.compatibility),
          )
        : null,
      list("Replaced", pack.mapped ?? []),
      list("Steam's own sound", pack.defaults ?? []),
      list("Problems", pack.problems ?? []),
      usable && (pack.assets ?? []).length
        ? h(
            react.Fragment,
            null,
            h("h3", null, "Preview"),
            renderSteamUiChips(ui, [
              ...pack.assets.map((asset) => ({
                label: asset,
                description: "Play",
                onClick: () => void soundsAct("preview", { id: pack.id, asset }),
              })),
              { label: "Stop", onClick: () => void soundsAct("stopPreview") },
            ]),
          )
        : null,
    ],
    aside: [
      usable
        ? renderSteamUiBox(
            react,
            selected ? "Playing in Steam" : "Use in Steam",
            h(
              ui.dialogButtonPrimary,
              {
                disabled: selected || !!state.busy,
                onClick: () => void soundsAct("select", { id: pack.id }),
              },
              selected ? "Selected" : "Use these sounds",
            ),
            h(
              "div",
              { className: "steam-ui-kit-muted" },
              "Sounds the pack does not cover keep Steam's own.",
            ),
          )
        : null,
      pack.storeId
        ? h(
            ui.dialogButton,
            {
              disabled: !!state.busy,
              onClick: () => void soundsAct("install", { id: pack.storeId }),
            },
            "Update from DeckThemes",
          )
        : null,
      h(
        ui.dialogButton,
        {
          disabled: !!state.busy,
          onClick: () =>
            showSteamUiConfirm(ui, {
              title: "Remove Sound Pack",
              text: selected
                ? `Remove ${pack.name}? Steam's own sounds play again.`
                : `Remove ${pack.name} from WSGM's sound library?`,
              confirmLabel: "Remove",
              onConfirm: () => void soundsAct("delete", { id: pack.id }).then(back),
            }),
        },
        "Remove",
      ),
    ],
    onBack: back,
  });
}

function SoundsInstalled({ state }: any) {
  const ui = soundsUi;
  const react = ui.react;
  const h = react.createElement;
  const [open, setOpen] = react.useState("");
  const packs: any[] = state.packs ?? [];
  const pack = packs.find((candidate) => candidate.id === open);
  if (pack) return h(SoundsPack, { state, pack, onBack: () => setOpen("") });
  const importZip = () =>
    void showSteamFilePicker(ui, {
      title: "Choose an Audio Loader ZIP",
      mode: "file",
      extensions: [".zip"],
    }).then((chosen) => chosen && soundsAct("import", { path: chosen }));
  const choice = renderSteamSettingRow(
    ui,
    {
      key: "sounds-selected",
      kind: "choice",
      label: "Sound pack",
      // With no pack chosen the coverage line only repeats that Steam's sounds play.
      description: state.selected ? state.compatibility : undefined,
      text: state.selected ?? "",
      choices: [
        { value: "", label: "Steam default" },
        ...packs
          .filter((item) => !item.error)
          .map((item) => ({ value: item.id, label: item.name })),
      ],
    },
    undefined,
    (_row, value, commit = true) => {
      if (commit) void soundsAct("select", { id: String(value ?? "") });
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
        { disabled: !!state.busy, onClick: () => void soundsAct("refresh") },
        "Refresh",
      ),
      h(ui.dialogButton, { disabled: !!state.busy, onClick: importZip }, "Import a ZIP…"),
      h(
        ui.dialogButton,
        {
          disabled: !!state.busy || !state.selected,
          onClick: () => void soundsAct("select", { id: "" }),
        },
        "Restore Steam Defaults",
      ),
    ),
    h(
      ui.settingsSection,
      { label: "Steam UI sounds" },
      choice,
      renderSteamSettingRow(
        ui,
        {
          key: "sounds-integration",
          kind: "note",
          label: "Steam integration",
          text: "",
          description: state.integration,
        },
        undefined,
        () => {},
        () => {},
      ),
    ),
    packs.length === 0
      ? renderSteamUiEmpty(
          react,
          "No sound packs installed. Get one under Browse, or import an Audio Loader ZIP.",
        )
      : h(
          ui.settingsSection,
          { label: "Installed packs" },
          ...packs.map((item) =>
            renderSteamUiSelectRow(ui, {
              key: item.id,
              title: item.name,
              detail: item.error ?? soundsPackLine(item),
              status:
                state.selected === item.id
                  ? h("span", { className: "wsgm-sounds-active" }, "Selected")
                  : null,
              selected: state.selected === item.id,
              onClick: () => setOpen(item.id),
            }),
          ),
        ),
  );
}

function SoundsBrowse({ state }: any) {
  const ui = soundsUi;
  const react = ui.react;
  const h = react.createElement;
  const browse = state.browse ?? {};
  const [search, setSearch] = react.useState("");
  const [searched, setSearched] = react.useState("");
  const [open, setOpen] = react.useState("");
  // The first look at the store is the page's own: nothing is fetched until someone opens the tab.
  react.useEffect(() => {
    if (!browse.page && !state.busy) soundsBrowse(1, "");
  }, []);
  const items: any[] = browse.items ?? [];
  const item = items.find((candidate) => candidate.id === open);
  if (item) return h(SoundsListing, { state, item, onBack: () => setOpen("") });
  const page = browse.page ?? 0;
  const pages = Math.max(1, Math.ceil((browse.total ?? 0) / Math.max(1, browse.pageSize ?? 1)));
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiToolbar(
      ui,
      renderSteamUiTool(
        ui,
        null,
        h(ui.textField, {
          label: "Search",
          value: search,
          onChange: (event) => setSearch(event?.target?.value ?? ""),
          onBlur: () => {
            if (search === searched) return;
            setSearched(search);
            soundsBrowse(1, search);
          },
        }),
        true,
      ),
      h(
        ui.dialogButton,
        { disabled: !!state.busy, onClick: () => soundsBrowse(Math.max(1, page), searched) },
        "Refresh",
      ),
    ),
    renderSteamUiGrid(
      ui,
      items.map((listing) => soundsCard(ui, listing, setOpen)),
    ),
    state.busy && items.length === 0
      ? renderSteamUiEmpty(react, "Asking the store…")
      : page && items.length === 0
        ? renderSteamUiEmpty(react, "Nothing matched.")
        : null,
    page > 0 && pages > 1
      ? renderSteamUiActions(ui, [
          {
            id: "previous",
            label: "Previous Page",
            disabled: !!state.busy || page <= 1,
            onClick: () => soundsBrowse(page - 1, searched),
          },
          {
            id: "next",
            label: `Next Page (${page} of ${pages})`,
            disabled: !!state.busy || page >= pages,
            onClick: () => soundsBrowse(page + 1, searched),
          },
        ])
      : null,
  );
}

/**
 * Renders sound-pack selection, details, previews and the DeckThemes store from host state.
 * @param context Registered page accessors for Steam components, latest state and publication refusal.
 * @returns The page React tree, including loading or refusal state when data is unavailable.
 */
function SoundsPage({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  soundsUi = ui;
  const state = context.state();
  const [tab, setTab] = react.useState("installed");
  // A preview belongs to the page: leaving it stops whatever is playing.
  react.useEffect(() => () => void soundsAct("stopPreview"), []);
  if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading sound packs…");
  return renderSteamUiTabbedPage(ui, {
    id: "wsgm-sounds",
    label: "UI sounds",
    style: soundsStyles,
    tabs: soundsTabs,
    active: tab,
    onTab: setTab,
    // The service reports the last refusal until its next operation; there is nothing to dismiss.
    banner: state.error
      ? { text: state.error, error: true, onDismiss: () => void soundsAct("refresh") }
      : null,
    content: (id) => (id === "browse" ? h(SoundsBrowse, { state }) : h(SoundsInstalled, { state })),
  });
}

// The page's own layout: where the kit's elements go, not how they look.
const soundsStyles = `
#wsgm-sounds .wsgm-sounds-active { color: #59bf40; margin-left: auto; }
#wsgm-sounds .wsgm-sounds-list { line-height: 1.5; }
#wsgm-sounds .wsgm-sounds-shot { width: 100%; max-height: 320px; object-fit: contain; border-radius: 4px; }
`;

const soundsPage = registerSteamPage({
  template: "sounds",
  gate: "sounds",
  patchId: SoundsPatchId,
  components: resolveSteamSettingsComponents,
  required: SteamUiTabbedPageRequired,
  status: () => ({ packs: soundsPage.state()?.packs?.length ?? 0 }),
  Page: SoundsPage,
});
