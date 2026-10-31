// SD Card Manager presentation. Card identity, naming, tab state, capacity and every write belong
// to LibraryManagerService; this page draws its state and sends its exact commands back.
const CardManagerPatchId = "wsgm.cards";

// The page's own layout: where the kit's elements go, not how they look.
const cardManagerStyles = `
#wsgm-cards .steam-ui-kit-list .DialogButton { width: 100%; min-width: 0; height: auto; padding: 10px 14px; }
#wsgm-cards .wsgm-cards-row { display: flex; flex-direction: column; gap: 4px; width: 100%; min-width: 0; text-align: left; }
#wsgm-cards .wsgm-cards-title { display: flex; justify-content: space-between; gap: 12px; font-size: 16px; font-weight: 600; color: #fff; }
#wsgm-cards .wsgm-cards-state { font-size: 12px; font-weight: 600; white-space: nowrap; color: #8b929a; }
#wsgm-cards .wsgm-cards-state[data-inserted="true"] { color: #a3dca3; }
#wsgm-cards .wsgm-cards-detail { font-size: 13px; color: #8b929a; overflow-wrap: anywhere; }
#wsgm-cards .wsgm-cards-meter { height: 6px; border-radius: 3px; background: rgba(255,255,255,.12); overflow: hidden; margin-top: 4px; }
#wsgm-cards .wsgm-cards-meter > div { height: 100%; background: var(--gpSystemBlue, #1a9fff); }
#wsgm-cards .wsgm-cards-games { display: flex; flex-direction: column; }
`;

/**
 * Formats a byte count the way the overlay's card manager prints it.
 * @param bytes The count.
 * @returns Gigabytes with one decimal, or megabytes below one gigabyte.
 */
const formatCardBytes = (bytes: number) => {
  const gigabyte = 1024 * 1024 * 1024;
  return bytes >= gigabyte
    ? `${(bytes / gigabyte).toFixed(1).replace(/\.0$/, "")} GB`
    : `${Math.round(bytes / (1024 * 1024))} MB`;
};

// What a card row says under its name: games, space with the last reading marked, tab and hidden state.
const cardManagerDetail = (card) => {
  const bits = [`${card.gameCount} games`];
  if (card.hasCapacity) {
    bits.push(
      `${formatCardBytes(card.freeBytes)} free of ${formatCardBytes(card.capacityBytes)}` +
        (card.inserted ? "" : " (last reading)"),
    );
  }
  if (!card.enabled) bits.push("tab off");
  if (card.hidden) bits.push("hidden");
  return bits.join(" · ");
};

const cardManagerRow = (h, card) => {
  const used = card.hasCapacity ? Math.max(0, card.capacityBytes - card.freeBytes) : 0;
  const percent = card.hasCapacity ? Math.min(100, Math.round((used / card.capacityBytes) * 100)) : 0;
  return h(
    "div",
    { className: "wsgm-cards-row" },
    h(
      "div",
      { className: "wsgm-cards-title" },
      h("span", null, card.name),
      h(
        "span",
        { className: "wsgm-cards-state", "data-inserted": card.inserted ? "true" : "false" },
        card.inserted ? "Connected" : "Not connected",
      ),
    ),
    h("div", { className: "wsgm-cards-detail" }, cardManagerDetail(card)),
    card.hasCapacity
      ? h("div", { className: "wsgm-cards-meter" }, h("div", { style: { width: `${percent}%` } }))
      : null,
  );
};

/**
 * The root level: every tracked card, connected or remembered, and a rescan.
 * @param props The frame's context, published state and the open callback.
 * @returns The list pane; the collection puts the focus back on the card it was left from.
 */
function CardManagerList({ context, state, onOpen }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  const cards = state.cards ?? [];
  const connected = cards.filter((card) => card.inserted).length;
  return renderSteamUiPane(
    ui,
    { key: "list", bounded: true },
    renderSteamUiHeader(ui, {
      page: true,
      title: "SD Card Manager",
      detail: state.loaded ? `${cards.length} tracked · ${connected} connected` : "Scanning cards…",
    }),
    renderSteamUiCollection(ui, {
      id: "wsgm-cards:list",
      layout: "list",
      items: cards,
      itemKey: (card) => card.contentId,
      render: (card) =>
        h(
          ui.dialogButton,
          { onClick: () => onOpen(card.contentId), onOKActionDescription: "Manage" },
          cardManagerRow(h, card),
        ),
      actions: [
        // Steam's installed route table defines Settings.Storage as /settings/storage. This only
        // opens the existing page; formatting and library writes remain explicit actions there.
        {
          id: "storage",
          label: "Open Storage",
          primary: !!state.loaded && cards.length === 0,
          onClick: () => {
            navigateSteamRoute("/settings/storage");
          },
        },
        {
          id: "refresh",
          label: "Rescan",
          disabled: !!state.busy,
          onClick: () => void context.act("refresh"),
        },
      ],
      loading: !state.loaded,
      loadingText: "Scanning cards…",
      // A read failure is never dressed up as "no cards": that invites re-formatting a card that is
      // in fact still tracked.
      error: cards.length === 0 ? state.error : null,
      onRetry: () => void context.act("refresh"),
      empty: {
        title: "No cards tracked yet",
        text: "Connect an SD card with a Steam library, then rescan. To prepare a card or add a library, open Steam's Storage settings.",
      },
    }),
  );
}

/**
 * One card: its state and space, its tab and hidden switches, and the rename, games and forget actions.
 * @param props The frame's context, published state, the card, and the back, games and forgotten callbacks.
 * @returns The nested level; Back returns to the list.
 */
function CardManagerCard({ context, state, card, onBack, onGames, onForgotten }: any) {
  const ui = context.ui();
  const busy = !!state.busy;
  const used = Math.max(0, card.capacityBytes - card.freeBytes);
  const space = card.hasCapacity
    ? `${formatCardBytes(used)} used · ${formatCardBytes(card.freeBytes)} free · ` +
      `${formatCardBytes(card.capacityBytes)} in all${card.inserted ? "" : " (last reading)"}`
    : "Not read yet; connect the card once.";
  const rows = [
    {
      key: "status",
      kind: "note",
      label: "Status",
      text: card.inserted ? "Connected" : "Not connected (remembered)",
    },
    { key: "space", kind: "note", label: "Space", text: space },
    {
      key: "enabled",
      kind: "boolean",
      label: "Steam tab",
      description: "Keep a library tab for this card in Steam",
      checked: !!card.enabled,
      disabled: busy,
    },
    {
      key: "hidden",
      kind: "boolean",
      label: "Hidden",
      description: "Keep the card tracked without a tab",
      checked: !!card.hidden,
      disabled: busy,
    },
    {
      key: "rename",
      kind: "action",
      label: "Name",
      description: card.name,
      buttonLabel: "Rename…",
      disabled: busy,
    },
    {
      key: "games",
      kind: "action",
      label: "Games on this card",
      description: `${card.gameCount} installed`,
      buttonLabel: "View",
    },
    {
      key: "forget",
      kind: "action",
      label: "Forget card",
      description: "Remove its tab and tracking; the games on the card stay",
      buttonLabel: "Forget…",
      disabled: busy,
    },
  ];
  // The switches send at once; a refusal is said on the switch that asked.
  const change = (row, value) =>
    row.key === "enabled"
      ? context.send("setEnabled", { contentId: card.contentId, enabled: !!value })
      : row.key === "hidden"
        ? context.send("setHidden", { contentId: card.contentId, hidden: !!value })
        : undefined;
  const action = (row) => {
    switch (row.key) {
      case "rename":
        showSteamUiPrompt(ui, {
          title: "Rename card",
          text: "The name is written onto the card itself, so it follows the card to any reader.",
          label: "Card name",
          initial: card.name,
          confirmLabel: "Rename",
          onConfirm: (name) => void context.act("rename", { contentId: card.contentId, name }),
        });
        break;
      case "games":
        onGames();
        break;
      case "forget":
        showSteamUiConfirm(ui, {
          title: `Forget ${card.name}?`,
          text: "Its Steam tab and tracking are removed. The games on the card stay; reinserting the card tracks it again as new.",
          confirmLabel: "Forget",
          // The list is only shown once the host has forgotten the card; a refusal keeps this level
          // and says why in the banner.
          onConfirm: () =>
            void context
              .send("forget", { contentId: card.contentId })
              .then(onForgotten, (error) => context.report(error)),
        });
        break;
      default:
        break;
    }
  };
  return renderSteamUiLevel(
    ui,
    { bounded: true, onBack },
    renderSteamUiHeader(ui, { page: true, title: card.name, detail: cardManagerDetail(card) }),
    renderSteamUiFields(ui, {
      key: `card-${card.contentId}`,
      title: "Card",
      rows,
      revision: state.revision,
      onChange: change,
      onAction: action,
    }),
    renderSteamUiActions(ui, [{ id: "back", label: "Back", onClick: onBack }], { compact: true }),
  );
}

/**
 * The games on one card, named from Steam's library when it answered.
 * @param props The frame's context, published state, the card and the back callback.
 * @returns The nested level; Back returns to the card.
 */
function CardManagerGames({ context, state, card, onBack }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  const games = state.games && state.games.contentId === card.contentId ? state.games : null;
  let body: any = null;
  if (!games || games.loading) {
    body = renderSteamUiEmpty(ui, { kind: "loading", text: "Reading the library…" });
  } else if (games.games.length > 0) {
    body = h(
      "div",
      { className: "wsgm-cards-games" },
      ...games.games.map((game) =>
        h(ui.valueField, { key: String(game.appId), name: game.name, value: "" }),
      ),
    );
  } else if (!games.error) {
    body = renderSteamUiEmpty(ui, { kind: "empty", text: "No games recorded on this card." });
  }
  return renderSteamUiLevel(
    ui,
    { bounded: true, onBack },
    renderSteamUiHeader(ui, {
      page: true,
      title: `${card.name} — Games`,
      detail: `${card.gameCount} installed`,
    }),
    games?.error
      ? renderSteamUiEmpty(ui, {
          kind: "error",
          text: games.error,
          action: {
            label: "Try again",
            onClick: () => void context.act("loadGames", { contentId: card.contentId }),
          },
        })
      : null,
    body,
    renderSteamUiActions(ui, [{ id: "back", label: "Back", onClick: onBack }], { compact: true }),
  );
}

/**
 * Walks the card list, one card and its games, with Back walking the levels up.
 * @param props The frame's context and the host's published state.
 * @returns The level on screen.
 */
function CardManagerPage({ context, state }: any) {
  const react = context.react();
  const h = react.createElement;
  // Where the user is: the list, one card, or that card's games. A reload of the asset starts at the
  // list, so no stale detail outlives the state it was opened from.
  const [level, setLevel] = react.useState({ kind: "list", id: "" });
  // The cards are read again whenever the page opens: one may have come or gone meanwhile.
  react.useEffect(() => {
    void context.act("refresh");
  }, []);
  const card =
    level.kind === "list"
      ? null
      : (state.cards ?? []).find((entry) => entry.contentId === level.id);
  react.useEffect(() => {
    // Forgotten elsewhere, or no longer tracked: the level it belonged to is gone with it.
    if (level.kind !== "list" && state.loaded && !card) setLevel({ kind: "list", id: "" });
  }, [level, card, state.loaded]);
  const toList = () => setLevel({ kind: "list", id: "" });
  if (level.kind === "games" && card) {
    return h(CardManagerGames, {
      context,
      state,
      card,
      onBack: () => setLevel({ kind: "card", id: card.contentId }),
    });
  }
  if (level.kind === "card" && card) {
    return h(CardManagerCard, {
      context,
      state,
      card,
      onBack: toList,
      onGames: () => {
        void context.act("loadGames", { contentId: card.contentId });
        setLevel({ kind: "games", id: card.contentId });
      },
      onForgotten: toList,
    });
  }
  return h(CardManagerList, {
    context,
    state,
    onOpen: (id: string) => setLevel({ kind: "card", id }),
  });
}

const cardManagerPage = registerSteamPage({
  template: "card-manager",
  gate: "cardManager",
  patchId: CardManagerPatchId,
  title: "SD Card Manager",
  id: "wsgm-cards",
  style: cardManagerStyles,
  loading: "Loading cards…",
  status: () => ({ cards: cardManagerPage.state()?.cards?.length ?? 0 }),
  Page: CardManagerPage,
});
