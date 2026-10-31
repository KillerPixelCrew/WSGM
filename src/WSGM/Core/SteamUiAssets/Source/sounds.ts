// UI sounds frontend; SoundPackService owns the library, the store, the choice and the previews.
const SoundsPatchId = "wsgm.sounds";
const SoundsRoute = "/wsgm/sounds";

const soundsTabs = [
  { id: "installed", title: "Installed" },
  { id: "browse", title: "Browse" },
];

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
      { glyph: renderSteamUiIcon(react, "download", 13), text: String(item.downloads ?? 0) },
      { glyph: renderSteamUiIcon(react, "star", 13), text: String(item.stars ?? 0) },
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

function SoundsListing({ context, state, item, onBack }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  const installed = item.installedVersion != null;
  const label = steamUiAcquireLabel(installed, item.installedVersion === item.version);
  return renderSteamUiItem(ui, {
    title: item.name,
    badge: item.version,
    media: item.imageUrl
      ? h("img", { className: "wsgm-sounds-shot", src: item.imageUrl, alt: "" })
      : null,
    byline: [item.author ? `By ${item.author}` : "", item.updated ? `Last Updated ${item.updated}` : ""]
      .filter(Boolean)
      .join(" · "),
    sections: [
      {
        title: installed ? "In your library" : "About",
        content: h(
          "p",
          { className: "steam-ui-kit-muted" },
          installed
            ? `Installed: ${item.installedVersion}. Choose it under Installed or on Settings > Customization.`
            : "An Audio Loader pack from DeckThemes. Sounds it does not cover keep Steam's own.",
        ),
      },
    ],
    acquire: {
      title: `${label} ${item.name}`,
      meta: `${item.downloads ?? 0} Downloads`,
      label,
      busy: !!state.busy,
      onAcquire: () => void context.act("install", { id: item.id }),
      note: "Downloads into WSGM's sound library.",
    },
    onBack,
  });
}

function SoundsPack({ context, state, pack, onBack }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const selected = state.selected === pack.id;
  const usable = !pack.error;
  const back = () => {
    void context.act("stopPreview");
    onBack();
  };
  const list = (title: string, items: string[]) =>
    items.length
      ? {
          title,
          content: h("div", { className: "steam-ui-kit-muted wsgm-sounds-list" }, items.join(", ")),
        }
      : null;
  return renderSteamUiItem(ui, {
    title: pack.name,
    badge: pack.version,
    byline: soundsPackLine(pack),
    description: pack.description ?? "",
    error: pack.error ?? null,
    sections: [
      pack.source
        ? { title: "Source", content: h("div", { className: "steam-ui-kit-muted" }, pack.source) }
        : null,
      pack.compatibility
        ? { title: "Client compatibility", content: h("p", null, pack.compatibility) }
        : null,
      list("Replaced", pack.mapped ?? []),
      list("Steam's own sound", pack.defaults ?? []),
      list("Problems", pack.problems ?? []),
      usable && (pack.assets ?? []).length
        ? {
            title: "Preview",
            content: renderSteamUiChips(ui, [
              ...pack.assets.map((asset) => ({
                label: asset,
                description: "Play",
                onClick: () => void context.act("preview", { id: pack.id, asset }),
              })),
              { label: "Stop", onClick: () => void context.act("stopPreview") },
            ]),
          }
        : null,
    ],
    acquire: usable
      ? {
          title: selected ? "Playing in Steam" : "Use in Steam",
          label: selected ? "Selected" : "Use these sounds",
          disabled: selected,
          busy: !!state.busy,
          onAcquire: () => void context.act("select", { id: pack.id }),
          note: "Sounds the pack does not cover keep Steam's own.",
        }
      : null,
    aside: [
      pack.storeId
        ? h(
            ui.dialogButton,
            {
              key: "update",
              disabled: !!state.busy,
              onClick: () => void context.act("install", { id: pack.storeId }),
            },
            "Update from DeckThemes",
          )
        : null,
    ],
    remove: {
      label: "Remove",
      title: "Remove Sound Pack",
      text: selected
        ? `Remove ${pack.name}? Steam's own sounds play again.`
        : `Remove ${pack.name} from WSGM's sound library?`,
      disabled: !!state.busy,
      // Back to the list once the pack is gone; a refusal stays on this pack and says why.
      onRemove: () =>
        void context.send("delete", { id: pack.id }).then(back, (error) => context.report(error)),
    },
    onBack: back,
  });
}

function SoundsInstalled({ context, state }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const [open, setOpen] = react.useState("");
  const packs: any[] = state.packs ?? [];
  const pack = packs.find((candidate) => candidate.id === open);
  if (pack) return h(SoundsPack, { context, state, pack, onBack: () => setOpen("") });
  const busy = !!state.busy;
  const rows = [
    {
      key: "sounds-selected",
      kind: "choice",
      label: "Sound pack",
      // With no pack chosen the coverage line only repeats that Steam's sounds play.
      description: state.selected ? state.compatibility : undefined,
      text: state.selected ?? "",
      disabled: busy,
      choices: [
        { value: "", label: "Steam default" },
        ...(state.selected && !packs.some((item) => item.id === state.selected && !item.error)
          ? [{ value: state.selected, label: "Unavailable pack", selectable: false }]
          : []),
        ...packs
          .filter((item) => !item.error)
          .map((item) => ({ value: item.id, label: item.name })),
      ],
    },
    {
      key: "sounds-integration",
      kind: "note",
      label: "Steam integration",
      text: "",
      description: state.integration,
    },
  ];
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiFields(ui, {
      title: "Steam UI sounds",
      rows,
      revision: state.revision,
      onChange: (_row, value) => context.send("select", { id: String(value ?? "") }),
    }),
    renderSteamUiManage(ui, {
      title: "Installed packs",
      items: packs,
      itemKey: (item) => item.id,
      label: (item) => item.name,
      detail: (item) => soundsPackLine(item),
      error: (item) => item.error ?? null,
      status: (item) =>
        state.selected === item.id ? h("span", { className: "wsgm-sounds-active" }, "Selected") : null,
      selected: state.selected,
      onOpen: (item) => setOpen(item.id),
      add: [
        { id: "refresh", label: "Refresh", disabled: busy, onClick: () => void context.act("refresh") },
        {
          id: "import",
          label: "Import a ZIP…",
          disabled: busy,
          pick: { title: "Choose an Audio Loader ZIP", mode: "file", extensions: [".zip"] },
          onPick: (path) => void context.act("import", { path }),
        },
        {
          id: "defaults",
          label: "Restore Steam Defaults",
          disabled: busy || !state.selected,
          onClick: () => void context.act("select", { id: "" }),
        },
      ],
      empty: {
        title: "No sound packs yet",
        text: "No sound packs installed. Get one under Browse, or import an Audio Loader ZIP.",
        action: { label: "Browse sound packs", onClick: () => context.setTab("browse") },
      },
    }),
  );
}

function SoundsBrowse({ context, state }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const browse = state.browse ?? {};
  const [open, setOpen] = react.useState("");
  const items: any[] = browse.items ?? [];
  const item = items.find((candidate) => candidate.id === open);
  if (item) return h(SoundsListing, { context, state, item, onBack: () => setOpen("") });
  const search = browse.search ?? "";
  const ask = (text: string) => void context.act(SteamUiBrowse.browse, { search: text });
  const busy = !!state.busy;
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiCollection(ui, {
      id: "wsgm-sounds-browse",
      items,
      itemKey: (listing) => listing.id,
      render: (listing) => soundsCard(ui, listing, setOpen),
      search: { value: search, onSubmit: ask },
      actions: [{ id: "refresh", label: "Refresh", disabled: busy, onClick: () => ask(search) }],
      // The store's answer arrives through the service's busy state; before the first one the tab
      // is asking, unless the asking failed, which the page's banner says.
      loading: (busy && items.length === 0) || (!browse.page && !state.error),
      loadingText: "Asking the store…",
      empty: { text: "The store lists no sound packs right now." },
      noMatch: { text: "Nothing matched." },
      filtered: !!search,
      more:
        items.length < (browse.total ?? 0)
          ? {
              total: browse.total,
              disabled: busy,
              onMore: () => void context.act(SteamUiBrowse.more),
            }
          : null,
      // The first look at the store is the page's own: nothing is fetched until someone opens the tab.
      onFirstShow: !browse.page && !busy ? () => ask("") : null,
    }),
  );
}

/**
 * Draws the active tab of the UI sounds page: the installed packs and the DeckThemes store.
 * @param props The frame's context, the host's state and the active tab.
 * @returns The tab's content; the frame draws the tabs, banner and loading state around it.
 */
function SoundsPage({ context, state, tab }: any) {
  const react = context.react();
  // A preview belongs to the page: leaving it stops whatever is playing.
  react.useEffect(() => () => void context.act("stopPreview"), []);
  return react.createElement(tab === "browse" ? SoundsBrowse : SoundsInstalled, { context, state });
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
  title: "UI sounds",
  id: "wsgm-sounds",
  style: soundsStyles,
  loading: "Loading sound packs…",
  tabs: { source: "local", list: soundsTabs, initial: "installed" },
  // The service reports the last refusal until its next operation; Dismiss asks it to look again.
  banner: (state) => (state.error ? { text: state.error, error: true, dismiss: "refresh" } : null),
  status: () => ({ packs: soundsPage.state()?.packs?.length ?? 0 }),
  Page: SoundsPage,
});
