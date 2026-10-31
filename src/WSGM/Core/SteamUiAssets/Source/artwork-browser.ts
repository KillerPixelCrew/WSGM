// Artwork frontend; host services own searches and file writes, and the toolkit owns the page frame,
// its tabs and the kit around Steam's components. What stays here is SteamGridDB's own: its result
// cards and chips, its filter, the official and local artwork, the logo anchor editor and the game
// the results are for.
const ArtworkBrowserPatchId = "wsgm.artwork-browser";

// The thumbnail size the user chose; one per page session, so it survives a tab change.
let artworkCardSize = 170;

const artworkFilterOptions = (tab: string) => ({
  styles:
    tab === "logo"
      ? ["official", "white", "black", "custom"]
      : tab === "icon"
        ? ["official", "custom"]
        : tab === "hero"
          ? ["alternate", "blurred", "material"]
          : ["alternate", "white_logo", "no_logo", "blurred", "material"],
  dimensions:
    tab === "grid"
      ? ["600x900", "342x482", "660x930", "512x512", "1024x1024"]
      : tab === "wide"
        ? ["460x215", "920x430", "512x512", "1024x1024"]
        : tab === "hero"
          ? ["1920x620", "3840x1240", "1600x650"]
          : tab === "icon"
            ? ["1024", "512", "310", "256", "192", "128", "96", "64", "48", "32", "16"]
            : [],
  mimes:
    tab === "logo"
      ? ["image/png", "image/webp"]
      : tab === "icon"
        ? ["image/png", "image/vnd.microsoft.icon"]
        : ["image/png", "image/jpeg", "image/webp"],
});

const readableFilter = (value: string) =>
  value.replace("image/", "").replaceAll("_", " ").replace("x", "×");

// Sends one artwork command; a refusal shows in the page's banner and resolves undefined.
const artworkAct = (command: string, payload: any = {}) => artworkBrowserPage.act(command, payload);

// The file-pick action that applies a local image to one slot; cancelling sends nothing.
const artworkLocalPick = (tab: string): SteamUiAction => ({
  id: `local-${tab}`,
  label: "Browse Local",
  pick: {
    title: "Choose an image",
    mode: "file",
    extensions:
      tab === "icon"
        ? [".png", ".jpg", ".jpeg", ".webp", ".ico"]
        : [".png", ".jpg", ".jpeg", ".webp"],
  },
  onPick: (path) => void artworkAct("applyLocal", { tab, path }),
});

const artworkTabLabel = (state: any) =>
  (Array.isArray(state?.tabs) ? state.tabs : []).find((tab) => tab.id === state?.activeTab)
    ?.label || "Artwork";

/**
 * Opens a page-styled modal through Steam's native modal host. The modal is drawn outside the
 * page's tree, so it takes the components it was opened with and reads live state through the
 * frame's `useState`.
 * @param ui The page's resolved components.
 * @param className Page-specific layout classes.
 * @param render Body renderer receiving the close callback.
 * @returns True if a modal was opened, or false when required native components are unavailable.
 */
const showArtworkModal = (ui, className: string, render: (close: () => void) => any) =>
  showSteamModal(ui, { title: "SteamGridDB", className: `sgdb-modal ${className}`, render });

function ArtworkDetailsModal({ ui, asset, label, closeModal }) {
  const h = ui.react.createElement;
  const PrimaryButton = ui.dialogButtonPrimary;
  const Focusable = ui.focusable;
  const apply = () => {
    void artworkAct("apply", { id: asset.id });
    closeModal?.();
  };
  return h(
    "div",
    {},
    h(
      "div",
      { className: `sgdb-modal-details-wrapper${asset.width > asset.height ? " wide" : ""}` },
      h(
        Focusable,
        {
          className: "image-wrap modal-image",
          onActivate: apply,
          onOKActionDescription: `Apply ${label}`,
        },
        h("img", { src: asset.imageUrl, alt: "" }),
      ),
      h(
        "div",
        { className: "info" },
        h(PrimaryButton, { onClick: apply, onOKActionDescription: `Apply ${label}` }, `Apply ${label}`),
        h(
          "span",
          { className: "meta" },
          [asset.format, asset.style, asset.width > 0 ? `${asset.width}×${asset.height}` : null]
            .filter(Boolean)
            .join(" • "),
        ),
        asset.author ? h(Focusable, { className: "author" }, h("span", null, asset.author)) : null,
        asset.notes ? h("p", { className: "notes" }, asset.notes) : null,
      ),
    ),
  );
}

function ArtworkOfficialModal({ ui, label, closeModal }) {
  const h = ui.react.createElement;
  const PrimaryButton = ui.dialogButtonPrimary;
  const state = artworkBrowserPage.useState();
  const assets = Array.isArray(state?.officialAssets) ? state.officialAssets : [];
  return h(
    "div",
    {},
    h("h2", null, `Official ${label}`),
    assets.length
      ? null
      : renderSteamUiEmpty(ui, { kind: "unavailable", text: `Steam has no official ${label} here.` }),
    ...assets.map((asset) =>
      h(
        "div",
        { className: "official-steam-asset", key: asset.id },
        h("img", { src: asset.imageUrl, alt: asset.label || `Official ${label}` }),
        h(
          "div",
          { className: "official-steam-asset-action" },
          h("span", null, asset.label),
          h(
            PrimaryButton,
            {
              onClick: () => {
                void artworkAct("applyOfficial", { id: asset.id });
                closeModal?.();
              },
            },
            `Apply ${label}`,
          ),
        ),
      ),
    ),
  );
}

function ArtworkFilterModal({ ui, tab, label, initialFilter, closeModal }) {
  const react = ui.react;
  const h = react.createElement;
  const Button = ui.dialogButton;
  const PrimaryButton = ui.dialogButtonPrimary;
  const ToggleField = ui.toggleField;
  const TextField = ui.textField;
  const Focusable = ui.focusable;
  // The game matches the host publishes after a search reach the open modal through the frame.
  const state = artworkBrowserPage.useState();
  const options = artworkFilterOptions(tab);
  const [filter, setFilter] = react.useState({ ...initialFilter });
  const [searchTerm, setSearchTerm] = react.useState(state?.selectedGame || "");
  const matches = Array.isArray(state?.gameMatches) ? state.gameMatches : [];

  const multiSelect = (title: string, name: string, values: string[]) =>
    h(
      "div",
      { className: "sgdb-filter-field" },
      h("label", null, title),
      h(
        Focusable,
        { className: "sgdb-filter-options", "flow-children": "row" },
        ...values.map((value) => {
          const selected = (filter[name] || []).includes(value);
          return h(
            Button,
            {
              key: value,
              className: selected ? "sgdb-filter-selected" : "",
              onClick: () =>
                setFilter({
                  ...filter,
                  [name]: selected
                    ? filter[name].filter((item) => item !== value)
                    : [...(filter[name] || []), value],
                }),
            },
            `${selected ? "✓ " : ""}${readableFilter(value)}`,
          );
        }),
      ),
    );

  const toggle = (name: string, labelText: string) =>
    h(ToggleField, {
      key: name,
      label: labelText,
      checked: !!filter[name],
      onChange: (checked) => {
        const next = { ...filter, [name]: checked };
        if ((name === "static" || name === "animated") && !next.static && !next.animated) {
          next[name === "static" ? "animated" : "static"] = true;
        }
        setFilter(next);
      },
    });

  return h(
    "div",
    {},
    h("h2", null, `${label} Filter`),
    h(
      "div",
      { className: "sgdb-filter-game" },
      TextField
        ? h(TextField, {
            label: "Game",
            value: searchTerm,
            placeholder: state?.appName || "Search for a game",
            onChange: (event) => setSearchTerm(event.currentTarget.value),
          })
        : null,
      h(
        Button,
        {
          onClick: () =>
            void artworkAct("searchGames", { term: searchTerm || state?.appName || "Steam" }),
        },
        "Search",
      ),
      state?.selectedGame
        ? h(
            Button,
            { onClick: () => void artworkAct("selectGame", { id: null }) },
            "Use Steam game",
          )
        : null,
    ),
    matches.length
      ? h(
          Focusable,
          { className: "sgdb-game-matches" },
          ...matches.map((match) =>
            h(
              Button,
              { key: match.id, onClick: () => void artworkAct("selectGame", { id: match.id }) },
              `${match.name} · ${match.provider}`,
            ),
          ),
        )
      : null,
    options.dimensions.length ? multiSelect("Dimensions", "dimensions", options.dimensions) : null,
    multiSelect("Styles", "styles", options.styles),
    multiSelect("File Types", "mimes", options.mimes),
    h("h3", null, "Types"),
    toggle("animated", "Animated"),
    toggle("static", "Static"),
    h("h3", null, "Tags"),
    toggle("adult", "Adult Content"),
    toggle("humor", "Humor"),
    toggle("epilepsy", "Epilepsy"),
    toggle("untagged", "Untagged"),
    h(
      Focusable,
      { className: "sgdb-modal-actions", "flow-children": "row" },
      h(Button, { onClick: closeModal }, "Cancel"),
      h(
        PrimaryButton,
        {
          onClick: () => {
            void artworkAct("setFilter", filter);
            closeModal?.();
          },
        },
        "Apply Filters",
      ),
    ),
  );
}

function ArtworkLogoModal({ ui, closeModal }) {
  const react = ui.react;
  const h = react.createElement;
  const Button = ui.dialogButton;
  const PrimaryButton = ui.dialogButtonPrimary;
  const SliderField = ui.sliderField;
  const Focusable = ui.focusable;
  const [position, setPosition] = react.useState({ anchor: "BottomLeft", width: 50, height: 50 });
  const anchors = [
    "TopLeft",
    "TopCenter",
    "TopRight",
    "CenterLeft",
    "CenterCenter",
    "CenterRight",
    "BottomLeft",
    "BottomCenter",
    "BottomRight",
  ];
  return h(
    "div",
    {},
    h("h2", null, "Adjust Logo Position"),
    h(
      "div",
      { className: "sgdb-logo-preview" },
      h("div", { className: `sgdb-logo-sample anchor-${position.anchor}` }, "GAME LOGO"),
    ),
    h(
      Focusable,
      { className: "sgdb-logo-anchors", "flow-children": "row" },
      ...anchors.map((anchor) =>
        h(
          Button,
          {
            key: anchor,
            className: position.anchor === anchor ? "sgdb-filter-selected" : "",
            onClick: () => setPosition({ ...position, anchor }),
          },
          anchor.replace(/([A-Z])/g, " $1").trim(),
        ),
      ),
    ),
    h(SliderField, {
      label: "Logo width",
      value: position.width,
      min: 5,
      max: 100,
      step: 1,
      showValue: true,
      valueSuffix: "%",
      onChange: (width) => setPosition({ ...position, width }),
    }),
    h(SliderField, {
      label: "Logo height",
      value: position.height,
      min: 5,
      max: 100,
      step: 1,
      showValue: true,
      valueSuffix: "%",
      onChange: (height) => setPosition({ ...position, height }),
    }),
    h(
      Focusable,
      { className: "sgdb-modal-actions", "flow-children": "row" },
      h(Button, { onClick: closeModal }, "Cancel"),
      h(
        PrimaryButton,
        {
          onClick: () => {
            void artworkAct("saveLogoPosition", position);
            closeModal?.();
          },
        },
        "Save",
      ),
    ),
  );
}

// The filter for the slot on screen, opened from the toolbar, from a card's X and from the tab's
// footer action.
const openArtworkFilters = () => {
  const ui = artworkBrowserPage.ui();
  const state = artworkBrowserPage.state();
  if (!ui || !state) return;
  showArtworkModal(ui, "sgdb-modal-filters", (close) =>
    ui.react.createElement(ArtworkFilterModal, {
      ui,
      tab: state.activeTab,
      label: artworkTabLabel(state),
      initialFilter: state.filter,
      closeModal: close,
    }),
  );
};

const openArtworkLogo = (ui) =>
  showArtworkModal(ui, "sgdb-modal-logo", (close) =>
    ui.react.createElement(ArtworkLogoModal, { ui, closeModal: close }),
  );

// Whose artwork this is: the Steam title, and the SteamGridDB game the results are for.
const artworkGameContext = (ui, state) =>
  ui.react.createElement(
    "div",
    { className: "sgdb-game-context" },
    renderSteamUiHeader(ui, {
      sub: true,
      title: state.appName ? `Artwork for ${state.appName}` : `Artwork for app ${state.appId}`,
      detail: state.selectedGame ? `Results for ${state.selectedGame}` : undefined,
    }),
  );

function ArtworkAssets({ ui, state }: any) {
  const react = ui.react;
  const h = react.createElement;
  const Focusable = ui.focusable;
  const Button = ui.dialogButton;
  const SliderField = ui.sliderField;
  const [cardSize, setCardSize] = react.useState(artworkCardSize);
  const assets = Array.isArray(state.assets) ? state.assets : [];
  const officialAssets = Array.isArray(state.officialAssets) ? state.officialAssets : [];
  const label = artworkTabLabel(state);
  const filter = state.filter ?? {};
  const filtersActive = !!(
    filter.styles?.length ||
    filter.dimensions?.length ||
    filter.mimes?.length ||
    filter.static === false ||
    filter.animated === false ||
    filter.adult === false ||
    filter.humor === false ||
    filter.epilepsy === false ||
    filter.untagged === false
  );
  // Notices can contain both provider failures and deliberately disabled optional providers.
  // Only the latter belong in the secondary note; every other reason is a status line.
  const notices = String(state.notice || "")
    .split(/ {2,}/)
    .filter(Boolean);
  const disabledNotices = notices.filter((notice) => notice.endsWith(": Turned off in Settings."));
  const providerNotices = notices.filter((notice) => !disabledNotices.includes(notice));
  const openDetails = (asset) =>
    showArtworkModal(ui, "sgdb-modal-details", (close) =>
      h(ArtworkDetailsModal, { ui, asset, label, closeModal: close }),
    );
  const assetCard = (asset) =>
    h(
      "div",
      { className: "asset-box-wrap", key: asset.id },
      h(
        Focusable,
        {
          className: `image-wrap type-${state.activeTab}`,
          style: {
            paddingBottom: `${asset.width === asset.height ? 100 : (asset.height / asset.width) * 100}%`,
          },
          onActivate: () => void artworkAct("apply", { id: asset.id }),
          onSecondaryButton: openArtworkFilters,
          onMenuButton: () => openDetails(asset),
          onContextMenu: (event) => {
            event.preventDefault();
            openDetails(asset);
          },
          onOKActionDescription: `Apply ${label}`,
          onSecondaryActionDescription: "Filter",
          onMenuActionDescription: "Details",
        },
        h(
          "div",
          { className: "thumb" },
          h("img", { src: asset.thumbnailUrl || asset.imageUrl, alt: "", loading: "lazy" }),
        ),
        asset.animated || asset.nsfw || asset.humor || asset.epilepsy
          ? h(
              "ul",
              { className: "chips" },
              asset.animated ? h("li", { className: "chip animated" }, "Animated") : null,
              asset.nsfw ? h("li", { className: "chip nsfw" }, "Adult") : null,
              asset.humor ? h("li", { className: "chip humor" }, "Humor") : null,
              asset.epilepsy ? h("li", { className: "chip epilepsy" }, "Epilepsy") : null,
            )
          : null,
      ),
      asset.author ? h("div", { className: "author" }, asset.author) : null,
    );
  const toolbarButton = (action: SteamUiAction) =>
    h(
      Button,
      { key: action.id, noFocusRing: true, onClick: () => runSteamUiAction(ui, action) },
      action.label,
    );

  let empty: any = null;
  if (!assets.length && !state.loading && !state.error) {
    empty = filtersActive
      ? renderSteamUiEmpty(ui, {
          kind: "noMatch",
          text: `No ${label.toLowerCase()} matches the filter.`,
          action: { label: "Change Filter", onClick: openArtworkFilters },
        })
      : renderSteamUiEmpty(ui, {
          kind: "empty",
          text: `No ${label.toLowerCase()} found for this game.`,
          action: { label: "Search Another Game", onClick: openArtworkFilters },
        });
  }

  return h(
    "div",
    { className: "tabcontents-wrap" },
    artworkGameContext(ui, state),
    h(
      Focusable,
      { className: "sgdb-asset-toolbar", "flow-children": "row" },
      h(
        Focusable,
        { className: "filter-buttons", "flow-children": "row" },
        toolbarButton({
          id: "filter",
          label: filtersActive ? "Filter · Active" : "Filter",
          onClick: openArtworkFilters,
        }),
        officialAssets.length
          ? toolbarButton({
              id: "official",
              label: `Official ${label}`,
              onClick: () =>
                showArtworkModal(ui, "sgdb-modal-official-assets", (close) =>
                  h(ArtworkOfficialModal, { ui, label, closeModal: close }),
                ),
            })
          : null,
        toolbarButton(artworkLocalPick(state.activeTab)),
        state.activeTab === "logo"
          ? toolbarButton({
              id: "logo",
              label: "Adjust Logo Position",
              onClick: () => openArtworkLogo(ui),
            })
          : null,
      ),
      h(
        "div",
        { className: "size-slider" },
        h(SliderField, {
          label: "Thumbnail size",
          value: cardSize,
          min: 100,
          max: 260,
          step: 5,
          showValue: true,
          valueSuffix: "px",
          layout: "below",
          bottomSeparator: "none",
          onChange: (size) => {
            artworkCardSize = size;
            setCardSize(size);
          },
        }),
      ),
    ),
    renderSteamUiStatus(ui, [
      state.error ? { key: "error", text: String(state.error), error: true } : null,
      ...providerNotices.map((notice) => ({ key: notice, text: notice })),
    ]),
    disabledNotices.length ? h("p", { className: "steam-ui-kit-sheet-note" }, disabledNotices.join("  ")) : null,
    assets.length
      ? h(
          Focusable,
          { id: "images-container", style: { "--asset-size": `${cardSize}px` } },
          ...assets.map(assetCard),
        )
      : null,
    state.loading
      ? renderSteamUiEmpty(ui, { kind: "loading", text: `Asking for ${label.toLowerCase()}…` })
      : null,
    empty,
    state.hasMore && !state.loading
      ? renderSteamUiMore(ui, { onClick: () => void artworkAct(SteamUiBrowse.more) })
      : null,
  );
}

function ArtworkManage({ ui, state }: any) {
  const h = ui.react.createElement;
  const Focusable = ui.focusable;
  const Button = ui.dialogButton;
  const managed = Array.isArray(state.managedSlots) ? state.managedSlots : [];
  return h(
    Focusable,
    { id: "local-images-container" },
    artworkGameContext(ui, state),
    ...managed.map((slot) =>
      h(
        "div",
        { className: `asset-wrap asset-wrap-${slot.id}`, key: slot.id },
        h("div", { className: "asset-label" }, `Current ${slot.label}`),
        h(
          Focusable,
          { className: "manage-asset", focusWithinClassName: "is-focused" },
          h(
            "div",
            { className: "asset" },
            slot.imageUrl
              ? h("img", { className: "asset-img", src: slot.imageUrl, alt: "" })
              : h("span", null, slot.hasCustomArtwork ? "Custom artwork" : "Steam default"),
          ),
          h(
            Focusable,
            { className: "action-overlay", "flow-children": "row" },
            h(Button, { onClick: () => void artworkAct("clear", { tab: slot.id }) }, "Clear"),
            h(
              Button,
              { onClick: () => runSteamUiAction(ui, { ...artworkLocalPick(slot.id), label: "Browse" }) },
              "Browse",
            ),
            slot.id !== "icon"
              ? h(
                  Button,
                  { onClick: () => void artworkAct("applyInvisible", { tab: slot.id }) },
                  "Invisible",
                )
              : null,
          ),
        ),
      ),
    ),
    h(
      Focusable,
      { className: "manage-actions", "flow-children": "row" },
      h(Button, { onClick: () => openArtworkLogo(ui) }, "Adjust Logo Position"),
      h(Button, { onClick: () => void artworkAct("resetLogoPosition") }, "Reset Logo Position"),
    ),
  );
}

/**
 * Draws the active tab of the artwork page: a slot's SteamGridDB results, or the title's current
 * artwork on the manage tab.
 * @param props The frame's context, the host's state and the active tab.
 * @returns The tab's content.
 */
function ArtworkBrowserPage({ context, state, tab }: any) {
  const ui = context.ui();
  const h = ui.react.createElement;
  const manage = (Array.isArray(state.tabs) ? state.tabs : []).find((entry) => entry.id === tab)?.manage;
  return manage ? h(ArtworkManage, { ui, state }) : h(ArtworkAssets, { ui, state });
}

// SteamGridDB's own layout: its result cards and chips, the manage tab's slots, its modals and the
// logo anchor editor. The frame draws the header offset, the background, the tab header and the kit.
const artworkBrowserStyles = `
#sgdb-wrap{--asset-size:170px;color:#fff}
#sgdb-wrap .tabcontents-wrap{display:flex;height:100%;width:100%;flex-direction:column}
#sgdb-wrap .sgdb-game-context{grid-column:1 / -1;padding:0 0 .5em;min-width:0}
#sgdb-wrap .sgdb-asset-toolbar{display:flex;align-items:center;flex-wrap:wrap;width:100%;gap:var(--gpSpace-Gap,.6em)}
#sgdb-wrap .filter-buttons{align-items:center;display:flex;flex-wrap:wrap;gap:.5em;min-width:0}
#sgdb-wrap .filter-buttons>button{min-width:auto;flex:1;white-space:nowrap}
#sgdb-wrap .size-slider{flex:0 1 280px;width:280px;max-width:100%;min-width:0;margin-left:auto;box-sizing:border-box;--field-negative-horizontal-margin:0px}
#sgdb-wrap .size-slider>*{width:100%;max-width:100%;min-width:0;box-sizing:border-box;margin-inline:0}
#sgdb-wrap #images-container{display:grid;padding-top:1em;padding-bottom:var(--gamepadui-current-footer-height);justify-content:space-evenly;grid-auto-flow:dense;row-gap:1em;column-gap:.65em;grid-template-columns:repeat(auto-fill,minmax(min(var(--asset-size),100%),var(--asset-size)))}
#sgdb-wrap .asset-box-wrap{display:flex;align-items:center;flex-wrap:wrap;position:relative}
#sgdb-wrap .image-wrap{background:url('/images/defaultappimage.png') center/cover;position:relative;width:100%;margin-top:auto;outline:2px solid transparent;transition:outline-color 200ms}
#sgdb-wrap .image-wrap.gpfocus,#sgdb-wrap .image-wrap:hover{z-index:10005;outline-color:rgba(255,255,255,.5)}
#sgdb-wrap .image-wrap.type-logo{padding-bottom:0!important;height:185px}
#sgdb-wrap .image-wrap.type-logo>.thumb,#sgdb-wrap .image-wrap.type-icon>.thumb{background:url('/images/defaultappimage.png') center/cover}
#sgdb-wrap .image-wrap>.thumb{position:absolute;inset:0}
#sgdb-wrap .image-wrap>.thumb img{position:absolute;inset:0;max-height:100%;max-width:100%;width:100%;height:auto;margin:0 auto}
#sgdb-wrap .image-wrap.type-logo>.thumb img,#sgdb-wrap .image-wrap.type-icon>.thumb img{position:static;width:auto;height:100%;object-fit:contain}
#sgdb-wrap .author{font-size:.65em;padding-top:.15em;overflow:hidden;text-shadow:0 1px 1px #000;white-space:nowrap;text-overflow:ellipsis}
#sgdb-wrap .chips{margin:0;padding:0;list-style:none;display:flex;flex-direction:column;position:absolute;right:-.5em;top:0;font-size:.5em;font-weight:bold;text-transform:uppercase;z-index:-1}
#sgdb-wrap .chip{display:flex;align-items:center;justify-content:center;padding:.3em .8em;min-height:2em;border-radius:0 5px 5px 0;transition:transform 300ms cubic-bezier(.33,1,.68,1)}
#sgdb-wrap .chip.animated{background:#e2a256}.chip.nsfw{background:#e5344c}.chip.humor{background:#eec314;color:#434343}.chip.epilepsy{background:#735f9f}
#sgdb-wrap .image-wrap.gpfocus .chip,#sgdb-wrap .image-wrap:hover .chip{transform:translateX(calc(100% - .5em - 1px));box-shadow:1px 2px 3px #0004}
#sgdb-wrap #local-images-container{display:grid;grid-template-columns:30% 1fr;gap:1em;margin-bottom:2em}
#sgdb-wrap .asset-label{color:#fff;font-weight:500;letter-spacing:1px;text-transform:uppercase;line-height:20px;margin-bottom:.5em}
#sgdb-wrap .manage-asset{position:relative}.manage-asset .asset{display:flex;min-height:100px;overflow:hidden;background:url('/images/defaultappimage.png') center/cover;align-items:center;justify-content:center}.manage-asset .asset-img{display:block;width:100%;max-height:260px;object-fit:contain}
#sgdb-wrap .action-overlay{display:none;position:absolute;gap:.25em;right:.5em;bottom:.5em;z-index:2}.manage-asset.is-focused .action-overlay,.manage-asset:hover .action-overlay{display:flex}.manage-actions{grid-column:span 2;display:flex;gap:.5em}
.sgdb-modal h2,.sgdb-modal h3{color:#fff}.sgdb-modal-details-wrapper{display:flex;gap:1em}.sgdb-modal-details-wrapper.wide{flex-direction:column}.sgdb-modal-details .modal-image{flex:1}.sgdb-modal-details .modal-image img{display:block;max-width:100%;max-height:55vh;margin:auto}.sgdb-modal-details .info{display:flex;flex-direction:column;flex:1;gap:.5em}.sgdb-modal-details .meta{text-transform:capitalize;opacity:.5;font-size:.8em;text-align:right}.sgdb-modal-details .author{margin-top:1em;font-weight:bold}.sgdb-modal-details .notes{max-width:300px;word-break:break-word}
.sgdb-modal-official-assets .official-steam-asset{margin:0 auto 1em}.sgdb-modal-official-assets img{display:block;max-width:100%;max-height:55vh;margin:auto}.official-steam-asset-action{display:flex;align-items:center;justify-content:space-between;gap:1em;margin-top:.5em}
.sgdb-filter-game{display:flex;align-items:end;gap:.5em}.sgdb-filter-game>:first-child{flex:1;min-width:0}.sgdb-filter-game input{width:100%;box-sizing:border-box}.sgdb-filter-game>.DialogButton{flex:0 0 auto;width:auto;min-width:0}.sgdb-filter-field{margin-top:1em}.sgdb-filter-field>label{display:block;margin-bottom:.4em;font-weight:600}.sgdb-filter-options,.sgdb-game-matches{display:flex;flex-wrap:wrap;gap:.4em}.sgdb-filter-options>button,.sgdb-game-matches>button{min-width:auto}.sgdb-filter-selected{box-shadow:inset 0 0 0 2px var(--gpStoreLightestGrey,#fff)}.sgdb-modal-actions{display:flex;justify-content:flex-end;gap:.5em;margin-top:1em}
.sgdb-logo-preview{height:250px;position:relative;background:#1b2838;overflow:hidden}.sgdb-logo-sample{position:absolute;padding:10px;font-size:28px;font-weight:bold}.anchor-TopLeft{left:0;top:0}.anchor-TopCenter{left:50%;top:0;transform:translateX(-50%)}.anchor-TopRight{right:0;top:0}.anchor-CenterLeft{left:0;top:50%;transform:translateY(-50%)}.anchor-CenterCenter{left:50%;top:50%;transform:translate(-50%,-50%)}.anchor-CenterRight{right:0;top:50%;transform:translateY(-50%)}.anchor-BottomLeft{left:0;bottom:0}.anchor-BottomCenter{left:50%;bottom:0;transform:translateX(-50%)}.anchor-BottomRight{right:0;bottom:0}.sgdb-logo-anchors{display:grid;grid-template-columns:repeat(3,1fr);gap:.4em;margin:1em 0}.sgdb-logo-anchors>button{min-width:auto}
`;

const artworkBrowserPage = registerSteamPage({
  template: "artwork-browser",
  gate: "artworkBrowser",
  patchId: ArtworkBrowserPatchId,
  title: "Artwork",
  id: "sgdb-wrap",
  style: artworkBrowserStyles,
  loading: "Loading artwork…",
  // The search's error and the providers' notices are drawn beside the results they belong to.
  banner: false,
  tabs: {
    source: "host",
    list: (state) =>
      (Array.isArray(state?.tabs) ? state.tabs : []).map((tab) => ({
        id: tab.id,
        title: tab.label,
        footer: tab.manage ? null : { label: "Filter", onActivate: openArtworkFilters },
      })),
  },
  status: () => ({ appId: artworkBrowserPage.state()?.appId ?? 0 }),
  Page: ArtworkBrowserPage,
});
