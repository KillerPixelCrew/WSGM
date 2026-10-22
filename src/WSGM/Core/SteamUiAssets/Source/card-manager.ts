// SD Card Manager presentation. Card identity, naming, tab state, capacity and every write belong
// to LibraryManagerService; this page draws its state and sends its exact commands back.
const CardManagerPatchId = "wsgm.cards";

const cardManagerAct = wsgmPageAct(CardManagerPatchId);

// The page's own layout: where the kit's elements go, not how they look.
const cardManagerStyles = `
#wsgm-cards .wsgm-cards-list { display: flex; flex-direction: column; gap: 8px; }
#wsgm-cards .wsgm-cards-list .DialogButton { width: 100%; min-width: 0; height: auto; padding: 10px 14px; }
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
      h("span", { className: "wsgm-cards-state", "data-inserted": card.inserted ? "true" : "false" },
        card.inserted ? "Connected" : "Not connected"),
    ),
    h("div", { className: "wsgm-cards-detail" }, cardManagerDetail(card)),
    card.hasCapacity ? h("div", { className: "wsgm-cards-meter" }, h("div", { style: { width: `${percent}%` } })) : null,
  );
};

/**
 * The root level: every tracked card, connected or remembered, and a rescan.
 * @param props Resolved components, published state, the card to put the focus back on and the open callback.
 * @returns The list pane.
 */
function CardManagerList({ ui, state, returnTo, onOpen }: any) {
  const react = ui.react;
  const h = react.createElement;
  const cards = state.cards ?? [];
  const connected = cards.filter((card) => card.inserted).length;
  let body;
  if (!state.loaded) {
    body = renderSteamUiEmpty(react, "Scanning cards…");
  } else if (cards.length === 0) {
    // A read failure is never dressed up as "no cards": that invites re-formatting a card that is
    // in fact still tracked.
    body = state.error
      ? renderSteamUiEmpty(react, state.error, true)
      : renderSteamUiBox(
          react,
          "No cards tracked yet",
          h("p", null, "Connect an SD card with a Steam library, then rescan. To prepare a card or add a library, open Steam's Storage settings."),
        );
  } else {
    body = h(
      ui.focusable,
      { className: "wsgm-cards-list", "flow-children": "column" },
      ...cards.map((card) =>
        h(
          ui.dialogButton,
          {
            key: card.contentId,
            // Back from a card lands on that card again, not on the top of the list.
            autoFocus: card.contentId === returnTo,
            onClick: () => onOpen(card.contentId),
            onOKActionDescription: "Manage",
          },
          cardManagerRow(h, card),
        ),
      ),
    );
  }
  return renderSteamUiPane(
    ui,
    { key: "list", bounded: true },
    renderSteamUiHeader(ui, {
      page: true,
      title: "SD Card Manager",
      detail: state.loaded ? `${cards.length} tracked · ${connected} connected` : "Scanning cards…",
    }),
    renderSteamUiActions(ui, [
      // Steam's installed route table defines Settings.Storage as /settings/storage. This only
      // opens the existing page; formatting and library writes remain explicit actions there.
      {
        id: "storage",
        label: "Open Storage",
        primary: cards.length === 0,
        onClick: () => { navigateSteamRoute("/settings/storage"); },
      },
      { id: "refresh", label: "Rescan", disabled: !!state.busy, onClick: () => void cardManagerAct("refresh") },
    ], { compact: true }),
    body,
  );
}

/**
 * One card: its state and space, its tab and hidden switches, and the rename, games and forget actions.
 * @param props Resolved components, published state, the card, and the back, games and forgotten callbacks.
 * @returns The nested level; Back returns to the list.
 */
function CardManagerCard({ ui, state, card, onBack, onGames, onForgotten }: any) {
  const react = ui.react;
  const h = react.createElement;
  const busy = !!state.busy;
  const used = Math.max(0, card.capacityBytes - card.freeBytes);
  const space = card.hasCapacity
    ? `${formatCardBytes(used)} used · ${formatCardBytes(card.freeBytes)} free · ` +
      `${formatCardBytes(card.capacityBytes)} in all${card.inserted ? "" : " (last reading)"}`
    : "Not read yet; connect the card once.";
  const rows = [
    { key: "status", kind: "note", label: "Status", text: card.inserted ? "Connected" : "Not connected (remembered)" },
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
    { key: "rename", kind: "action", label: "Name", description: card.name, buttonLabel: "Rename…", disabled: busy },
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
  // The switches send at once; the row keeps showing what the host last published until it answers.
  const change = (row, value) => {
    if (row.key === "enabled") void cardManagerAct("setEnabled", { contentId: card.contentId, enabled: !!value });
    if (row.key === "hidden") void cardManagerAct("setHidden", { contentId: card.contentId, hidden: !!value });
  };
  const action = (row) => {
    switch (row.key) {
      case "rename":
        showSteamUiPrompt(ui, {
          title: "Rename card",
          text: "The name is written onto the card itself, so it follows the card to any reader.",
          label: "Card name",
          initial: card.name,
          confirmLabel: "Rename",
          onConfirm: (name) => void cardManagerAct("rename", { contentId: card.contentId, name }),
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
          onConfirm: () => void request(CardManagerPatchId, "forget", { contentId: card.contentId }).then(onForgotten, () => {}),
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
    h(ui.settingsSection, { label: "Card" }, ...rows.map((row) => renderSteamSettingRow(ui, row, undefined, change, action))),
    renderSteamUiActions(ui, [{ id: "back", label: "Back", onClick: onBack }], { compact: true }),
  );
}

/**
 * The games on one card, named from Steam's library when it answered.
 * @param props Resolved components, published state, the card and the back callback.
 * @returns The nested level; Back returns to the card.
 */
function CardManagerGames({ ui, state, card, onBack }: any) {
  const react = ui.react;
  const h = react.createElement;
  const games = state.games && state.games.contentId === card.contentId ? state.games : null;
  let body;
  if (!games || games.loading) {
    body = renderSteamUiEmpty(react, "Reading the library…");
  } else if (games.games.length === 0) {
    body = renderSteamUiEmpty(react, "No games recorded on this card.");
  } else {
    body = h(
      "div",
      { className: "wsgm-cards-games" },
      ...games.games.map((game) => h(ui.valueField, { key: String(game.appId), name: game.name, value: "" })),
    );
  }
  return renderSteamUiLevel(
    ui,
    { bounded: true, onBack },
    renderSteamUiHeader(ui, { page: true, title: `${card.name} — Games`, detail: `${card.gameCount} installed` }),
    games?.error ? renderSteamUiEmpty(react, games.error, true) : null,
    body,
    renderSteamUiActions(ui, [{ id: "back", label: "Back", onClick: onBack }], { compact: true }),
  );
}

/**
 * Renders the card list, one card and its games from host state, with Back walking the levels up.
 * @param context Registered page accessors for Steam components, latest state and publication refusal.
 * @returns The page React tree, including loading or refusal state when data is unavailable.
 */
function CardManagerPage({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const ui = context.ui();
  const state = context.state();
  // Where the user is: the list, one card, or that card's games. A reload of the asset starts at the
  // list, so no stale detail outlives the state it was opened from.
  const [level, setLevel] = react.useState({ kind: "list", id: "" });
  const [returnTo, setReturnTo] = react.useState("");
  const card = level.kind === "list" ? null : (state?.cards ?? []).find((entry) => entry.contentId === level.id);
  react.useEffect(() => {
    void cardManagerAct("refresh");
  }, []);
  react.useEffect(() => {
    // Forgotten elsewhere, or no longer tracked: the level it belonged to is gone with it.
    if (level.kind !== "list" && state?.loaded && !card) setLevel({ kind: "list", id: "" });
  }, [level, card, state?.loaded]);
  if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading cards…");
  const banner = state.error || state.notice;
  const toList = (id: string) => {
    setReturnTo(id);
    setLevel({ kind: "list", id: "" });
  };
  let body;
  if (level.kind === "games" && card) {
    body = h(CardManagerGames, { ui, state, card, onBack: () => setLevel({ kind: "card", id: card.contentId }) });
  } else if (level.kind === "card" && card) {
    body = h(CardManagerCard, {
      ui,
      state,
      card,
      onBack: () => toList(card.contentId),
      onGames: () => {
        void cardManagerAct("loadGames", { contentId: card.contentId });
        setLevel({ kind: "games", id: card.contentId });
      },
      onForgotten: () => toList(""),
    });
  } else {
    body = h(CardManagerList, { ui, state, returnTo, onOpen: (id: string) => setLevel({ kind: "card", id }) });
  }
  return h(
    "div",
    { id: "wsgm-cards", className: "steam-ui-kit-page", "aria-label": "SD Card Manager" },
    steamUiKitStyle(react),
    h("style", null, cardManagerStyles),
    banner
      ? h(
          "div",
          { className: "steam-ui-kit-page-banner" },
          renderSteamUiBanner(ui, { text: banner, error: !!state.error, onDismiss: () => void cardManagerAct("dismiss") }),
        )
      : null,
    body,
  );
}

const cardManagerPage = registerSteamPage({
  template: "card-manager",
  gate: "cardManager",
  patchId: CardManagerPatchId,
  components: resolveSteamSettingsComponents,
  required: [...SteamSettingsRequired, "dialogButtonPrimary", "modalRoot"],
  status: () => ({ cards: cardManagerPage.state()?.cards?.length ?? 0 }),
  Page: CardManagerPage,
});
