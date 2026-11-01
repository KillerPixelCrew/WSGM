// Boot-animation frontend; host services own downloads, selection and override files.
// Windows does not drive Steam suspend movies, so this page offers boot movies only.
const AnimationsPatchId = "wsgm.animations";

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
          { glyph: renderSteamUiIcon(react, "heart", 13), text: String(item.likes ?? 0) },
          { glyph: renderSteamUiIcon(react, "download", 13), text: String(item.downloads ?? 0) },
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

function AnimationsDetail({ context, state }: any) {
  const ui = context.ui();
  const item = state.detail;
  const playing = state.selected === item.id;
  const back = () => void context.act(SteamUiBrowse.closeDetail);
  return renderSteamUiItem(ui, {
    title: item.name,
    media: renderSteamUiVideo(ui.react, {
      src: item.previewUrl,
      poster: item.thumbnailUrl,
      empty: item.custom ? "Your file has no preview here" : "No preview",
    }),
    byline: [item.author ? `By ${item.author}` : "", item.updated].filter(Boolean).join(" · "),
    description: item.description ?? "",
    acquire: item.downloaded
      ? {
          title: playing ? "Plays at boot" : "In the library",
          label: playing ? "Big Picture starts with this" : "Start Big Picture with this",
          disabled: playing,
          onAcquire: () => void context.act("select", { id: item.id }),
          note: "Steam reads the movie when it starts, so a change shows at the next Steam start.",
        }
      : {
          title: `Download ${item.name}`,
          meta: `${item.downloads ?? 0} downloads · ${item.likes ?? 0} likes`,
          label: "Download",
          busy: !!state.busy,
          onAcquire: () => void context.act("download", { id: item.id }),
          note: "Into WSGM's library; choose it there afterwards.",
        },
    remove: item.downloaded
      ? {
          label: "Remove from library",
          title: "Remove movie",
          text: `Remove ${item.name} from the library? If it plays at boot, Steam's own movie plays again.`,
          // Back to the collection once the movie is gone; a refusal stays here and says why.
          onRemove: () =>
            void context.send("delete", { id: item.id }).then(back, (error) => context.report(error)),
        }
      : null,
    onBack: back,
  });
}

function AnimationsBrowse({ context, state }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  if (state.detail) return h(AnimationsDetail, { context, state });
  const browse = state.browse ?? {};
  const query = { sort: browse.sort ?? "", search: browse.search ?? "" };
  const ask = (changes: any) => void context.act(SteamUiBrowse.browse, { ...query, ...changes });
  const items: any[] = browse.items ?? [];
  const open = (id: string) => void context.act(SteamUiBrowse.open, { id });
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiCollection(ui, {
      id: "wsgm-animations-browse",
      items,
      itemKey: (item) => item.id,
      render: (item) => animationsCard(ui, item, state.selected, open),
      choices: [
        {
          label: "Sort",
          options: (browse.sorts ?? []).map((sort) => ({ data: sort.id, label: sort.label })),
          selected: browse.sort,
          onChange: (sort) => ask({ sort }),
        },
      ],
      search: { value: browse.search ?? "", onSubmit: (search) => ask({ search }) },
      actions: [
        {
          id: "refresh",
          label: "Refresh",
          disabled: !!browse.loading,
          onClick: () => void context.act("refresh"),
        },
      ],
      loading: !!browse.loading,
      loadingText: "Asking the repository…",
      error: browse.error ?? null,
      onRetry: () => void context.act("refresh"),
      empty: {
        text: "The repository has not answered yet.",
        action: { label: "Refresh", onClick: () => void context.act("refresh") },
      },
      noMatch: { text: "Nothing matched." },
      // The repository answered with movies, and the search or sort left none of them.
      filtered: !!browse.total,
      // A page at a time: the repository lists thousands, and a card for each stalls Steam.
      more:
        items.length < (browse.matched ?? 0)
          ? { total: browse.matched, onMore: () => void context.act(SteamUiBrowse.more) }
          : null,
      // The first look at the repository is the page's own: nothing is fetched until someone opens the tab.
      onFirstShow: !browse.loading && !browse.error && !browse.total ? () => ask({}) : null,
    }),
  );
}

function AnimationsLibrary({ context, state }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  if (state.detail) return h(AnimationsDetail, { context, state });
  const library: any[] = state.library ?? [];
  const open = (id: string) => void context.act(SteamUiBrowse.open, { id });
  const known = !state.selected || library.some((item) => item.id === state.selected);
  return renderSteamUiPane(
    ui,
    {},
    renderSteamUiCollection(ui, {
      id: "wsgm-animations-library",
      header: renderSteamUiFields(ui, {
        title: "Boot",
        rows: [
          {
            key: "boot",
            kind: "choice",
            label: "Boot movie",
            description: "What Big Picture starts with.",
            text: state.selected ?? "",
            disabled: !!state.busy,
            choices: [
              { value: "", label: state.stockName ?? "" },
              ...(known
                ? []
                : [{ value: state.selected, label: "Unavailable movie", selectable: false }]),
              ...library.map((item) => ({ value: item.id, label: item.name })),
            ],
          },
        ],
        revision: state.revision,
        onChange: (_row, value) => context.send("select", { id: String(value) }),
      }),
      items: library,
      itemKey: (item) => item.id,
      render: (item) => animationsCard(ui, item, state.selected, open),
      actions: [
        {
          id: "shuffle",
          label: "Shuffle",
          disabled: !!state.busy || library.length === 0,
          onClick: () => void context.act("shuffle"),
        },
        {
          id: "add",
          label: "Add a video file…",
          disabled: !!state.busy,
          pick: { title: "Choose a WebM movie", mode: "file", extensions: [".webm"] },
          onPick: (path) => void context.act("addFile", { path }),
        },
      ],
      status: [
        state.settings?.restartNeeded
          ? "The boot movie changed since Steam started. Restart Steam to see it."
          : null,
      ],
      empty: {
        title: "Nothing in the library yet",
        text: "Download a movie under Browse, or add a WebM file.",
        action: { label: "Browse movies", onClick: () => context.setTab("browse") },
      },
    }),
  );
}

function AnimationsSettings({ context, state }: any) {
  const ui = context.ui();
  const settings = state.settings ?? {};
  const rows = [
    {
      key: "shuffleOnStart",
      kind: "boolean",
      label: "Shuffle on start",
      description:
        "Picks the boot movie anew from the library each time WSGM starts, before Steam does.",
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
    renderSteamUiFields(ui, {
      title: "Boot animation",
      rows,
      revision: state.revision,
      onChange: (row, value) =>
        row.key === "shuffleOnStart"
          ? context.send("setShuffleOnStart", { value: !!value })
          : row.key === "bootVolume"
            ? context.send("setBootVolume", { value: Math.round(Number(value)) })
            : undefined,
    }),
  );
}

/**
 * Draws the active tab of the Animations page: the repository, the library and settings.
 * @param props The frame's context, the host's state and the active tab.
 * @returns The tab's content; the frame draws the tabs, banner and loading state around it.
 */
function AnimationsPage({ context, state, tab }: any) {
  const h = context.react().createElement;
  switch (tab) {
    case "library":
      return h(AnimationsLibrary, { context, state });
    case "settings":
      return h(AnimationsSettings, { context, state });
    default:
      return h(AnimationsBrowse, { context, state });
  }
}

// The page's own layout: where the kit's elements go, not how they look.
const animationsStyles = `
#wsgm-animations .steam-ui-kit-tool:not(.grow) { width: 200px; }
`;

const animationsPage = registerSteamPage({
  template: "animations",
  gate: "animations",
  patchId: AnimationsPatchId,
  title: "Boot animation",
  id: "wsgm-animations",
  style: animationsStyles,
  loading: "Loading boot movies…",
  tabs: { source: "host", list: animationsTabs },
  status: () => ({ tab: animationsPage.state()?.activeTab ?? "" }),
  Page: AnimationsPage,
});
