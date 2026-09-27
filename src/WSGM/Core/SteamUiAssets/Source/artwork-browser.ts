// SteamGridDB-compatible artwork browser owned by WSGM.
//
// The page deliberately renders with Steam's own component exports. WSGM owns artwork data and
// behavior; steam-ui-toolkit owns the page gate, the modal frame, the file picker and the fail-closed
// component discovery used here.
const ArtworkBrowserPatchId = "steam-ui.artwork-browser";

// The resolved components and the latest state, for the modals: a modal is drawn outside the page's
// tree, so it reads them here and hears about new state through the listeners the page notifies.
let artworkUi: any = null;
let artworkDesired: any = null;
const artworkListeners = new Set<(state: any) => void>();

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

const sendArtworkCommand = (command: string, payload: any = {}) =>
  request(ArtworkBrowserPatchId, command, payload);

// Browse Local: Steam's own file picker, drawn from Steam's components and driven by the controller,
// rather than a Windows dialog that opens behind Big Picture. The host reads the file where it lies;
// a page request is held to a few kilobytes and an image would never fit in one.
const chooseLocalArtwork = (tab: string, failed: (message: string) => void) => {
  void showSteamFilePicker(artworkUi, {
    title: "Choose an image",
    mode: "file",
    extensions: tab === "icon" ? [".png", ".jpg", ".jpeg", ".webp", ".ico"] : [".png", ".jpg", ".jpeg", ".webp"],
  }).then((path) => {
    if (!path) return;
    void sendArtworkCommand("applyLocal", { tab, path }).catch((error) => failed(String(error?.message || error)));
  });
};

// Every modal on this page, in Steam's modal frame with the page's own class for its layout.
const showArtworkModal = (className: string, render: (close: () => void) => any) =>
  showSteamModal(artworkUi, { title: "SteamGridDB", className: `sgdb-modal ${className}`, render });

function ArtworkDetailsModal({ asset, label, closeModal }) {
  const h = artworkUi.react.createElement;
  const PrimaryButton = artworkUi.dialogButtonPrimary;
  const Focusable = artworkUi.focusable;
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
          onActivate: () => {
            void sendArtworkCommand("apply", { id: asset.id });
            closeModal?.();
          },
          onOKActionDescription: `Apply ${label}`,
        },
        h("img", { src: asset.imageUrl, alt: "" }),
      ),
      h(
        "div",
        { className: "info" },
        h(
          PrimaryButton,
          {
            onClick: () => {
              void sendArtworkCommand("apply", { id: asset.id });
              closeModal?.();
            },
            onOKActionDescription: `Apply ${label}`,
          },
          `Apply ${label}`,
        ),
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

function ArtworkOfficialModal({ assets, label, closeModal }) {
  const h = artworkUi.react.createElement;
  const PrimaryButton = artworkUi.dialogButtonPrimary;
  return h(
    "div",
    {},
    h("h2", null, `Official ${label}`),
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
                void sendArtworkCommand("applyOfficial", { id: asset.id });
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

function ArtworkFilterModal({ tab, label, initialFilter, closeModal }) {
  const react = artworkUi.react;
  const h = react.createElement;
  const Button = artworkUi.dialogButton;
  const PrimaryButton = artworkUi.dialogButtonPrimary;
  const ToggleField = artworkUi.toggleField;
  const TextField = artworkUi.textField;
  const Focusable = artworkUi.focusable;
  const options = artworkFilterOptions(tab);
  const [filter, setFilter] = react.useState({ ...initialFilter });
  const [searchTerm, setSearchTerm] = react.useState(artworkDesired?.selectedGame || "");
  const [matches, setMatches] = react.useState(artworkDesired?.gameMatches || []);
  react.useEffect(() => {
    const listener = (state) =>
      setMatches(Array.isArray(state?.gameMatches) ? state.gameMatches : []);
    artworkListeners.add(listener);
    return () => artworkListeners.delete(listener);
  }, []);

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
            placeholder: artworkDesired?.appName || "Search for a game",
            onChange: (event) => setSearchTerm(event.currentTarget.value),
          })
        : null,
      h(
        Button,
        {
          onClick: () =>
            void sendArtworkCommand("searchGames", {
              term: searchTerm || artworkDesired?.appName || "Steam",
            }),
        },
        "Search",
      ),
      artworkDesired?.selectedGame
        ? h(
            Button,
            { onClick: () => void sendArtworkCommand("selectGame", { id: null }) },
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
              {
                key: match.id,
                onClick: () => void sendArtworkCommand("selectGame", { id: match.id }),
              },
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
            void sendArtworkCommand("setFilter", filter);
            closeModal?.();
          },
        },
        "Apply Filters",
      ),
    ),
  );
}

function ArtworkLogoModal({ closeModal }) {
  const react = artworkUi.react;
  const h = react.createElement;
  const Button = artworkUi.dialogButton;
  const PrimaryButton = artworkUi.dialogButtonPrimary;
  const SliderField = artworkUi.sliderField;
  const Focusable = artworkUi.focusable;
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
            void sendArtworkCommand("saveLogoPosition", position);
            closeModal?.();
          },
        },
        "Save",
      ),
    ),
  );
}

// Declared once for the life of the asset, and drawn by the toolkit's page frame only once the gate
// holds: the frame says why when it does not.
function ArtworkBrowserPage({ context }: any) {
  const react = context.react();
  const h = react.createElement;
  const [actionError, setActionError] = react.useState("");
  const [cardSize, setCardSize] = react.useState(170);
  const state = context.state();
  artworkUi = context.ui();
  artworkDesired = state;

  // An open filter modal lists the matches the host publishes after its search.
  react.useEffect(() => {
    for (const listener of [...artworkListeners]) listener(state);
  }, [state]);

  // After the hooks, so a render before the first publication calls the same ones as one after.
  if (!state) return h("div", { className: "sgdb-loading" }, context.refusal() ?? "Loading artwork…");
    const Focusable = artworkUi.focusable;
    const Button = artworkUi.dialogButton;
    const SliderField = artworkUi.sliderField;
    const Tabs = artworkUi.tabs;

    const activate = (command: string, payload: any = {}) =>
      sendArtworkCommand(command, payload).catch((error) => {
        setActionError(String(error?.message || error));
        return undefined;
      });
    const tabs = Array.isArray(state.tabs) ? state.tabs : [];
    const assets = Array.isArray(state.assets) ? state.assets : [];
    const officialAssets = Array.isArray(state.officialAssets) ? state.officialAssets : [];
    const managed = Array.isArray(state.managedSlots) ? state.managedSlots : [];
    const active = tabs.find((tab) => tab.id === state.activeTab) || tabs[0];

    const openFilters = () =>
      showArtworkModal("sgdb-modal-filters", (close) =>
        h(ArtworkFilterModal, {
          tab: state.activeTab,
          label: active?.label || "Artwork",
          initialFilter: state.filter,
          closeModal: close,
        }),
      );
    const openDetails = (asset) =>
      showArtworkModal("sgdb-modal-details", (close) =>
        h(ArtworkDetailsModal, { asset, label: active?.label || "artwork", closeModal: close }),
      );
    const openLogo = () =>
      showArtworkModal("sgdb-modal-logo", (close) => h(ArtworkLogoModal, { closeModal: close }));
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
            onActivate: () => activate("apply", { id: asset.id }),
            onSecondaryButton: openFilters,
            onMenuButton: () => openDetails(asset),
            onContextMenu: (event) => {
              event.preventDefault();
              openDetails(asset);
            },
            onOKActionDescription: `Apply ${active?.label || "artwork"}`,
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

    const assetContent = h(
      "div",
      { className: "tabcontents-wrap" },
      state.loading
        ? h("div", { className: "spinnyboi" }, h("img", { src: "/images/steam_spinner.png" }))
        : null,
      h(
        Focusable,
        { className: "sgdb-asset-toolbar", "flow-children": "row" },
        h(
          Focusable,
          { className: "filter-buttons", "flow-children": "row" },
          h(Button, { noFocusRing: true, onClick: openFilters }, "Filter"),
          officialAssets.length
            ? h(
                Button,
                {
                  noFocusRing: true,
                  onClick: () =>
                    showArtworkModal("sgdb-modal-official-assets", (close) =>
                      h(ArtworkOfficialModal, {
                        assets: officialAssets,
                        label: active?.label || "Artwork",
                        closeModal: close,
                      }),
                    ),
                },
                `Official ${active?.label || "Artwork"}`,
              )
            : null,
          h(
            Button,
            {
              noFocusRing: true,
              onClick: () => chooseLocalArtwork(state.activeTab, setActionError),
            },
            "Browse Local",
          ),
          state.activeTab === "logo"
            ? h(
                Button,
                { noFocusRing: true, onClick: openLogo },
                "Adjust Logo Position",
              )
            : null,
        ),
        h(SliderField, {
          className: "size-slider",
          value: cardSize,
          min: 100,
          max: 260,
          step: 5,
          layout: "below",
          bottomSeparator: "none",
          onChange: setCardSize,
        }),
      ),
      state.selectedGame || state.filter?.styles?.length || state.filter?.dimensions?.length
        ? h(
            Button,
            { className: "sgdb-results-state", onClick: openFilters },
            state.selectedGame
              ? `Results for ${state.selectedGame}`
              : "Some assets may be hidden due to filter",
          )
        : null,
      state.error || state.notice || actionError
        ? h(
            "div",
            { className: `sgdb-status${state.error || actionError ? " error" : ""}` },
            state.error || actionError || state.notice,
          )
        : null,
      h(
        Focusable,
        {
          id: "images-container",
          style: { "--asset-size": `${cardSize}px` },
        },
        ...assets.map(assetCard),
      ),
      !state.loading && assets.length === 0 && !state.error
        ? h("div", { className: "sgdb-empty" }, "No Results Found.")
        : null,
      state.hasMore
        ? h(
            "div",
            { className: "sgdb-load-more" },
            h(Button, { onClick: () => activate("loadMore") }, "Load More"),
          )
        : null,
    );

    const manageContent = h(
      Focusable,
      { id: "local-images-container" },
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
              h(Button, { onClick: () => activate("clear", { tab: slot.id }) }, "Clear"),
              h(Button, { onClick: () => chooseLocalArtwork(slot.id, setActionError) }, "Browse"),
              slot.id !== "icon"
                ? h(Button, { onClick: () => activate("applyInvisible", { tab: slot.id }) }, "Invisible")
                : null,
            ),
          ),
        ),
      ),
      h(
        Focusable,
        { className: "manage-actions", "flow-children": "row" },
        h(Button, { onClick: openLogo }, "Adjust Logo Position"),
        h(Button, { onClick: () => activate("resetLogoPosition") }, "Reset Logo Position"),
      ),
    );

    const nativeTabs = tabs.map((tab) => ({
      id: tab.id,
      title: tab.label,
      content: tab.manage ? manageContent : tab.id === state.activeTab ? assetContent : null,
      footer: tab.manage
        ? undefined
        : {
            onSecondaryActionDescription: "Filter",
            onSecondaryButton: openFilters,
          },
    }));

    return h(
      "div",
      { id: "sgdb-wrap", "aria-label": `Artwork for ${state.appName}` },
      h("style", null, artworkBrowserStyles),
      h(Tabs, {
        autoFocusContents: true,
        activeTab: state.activeTab,
        onShowTab: (tab) => activate("selectTab", { tab }),
        tabs: nativeTabs,
      }),
    );
}

const artworkBrowserStyles = `
#sgdb-wrap{--asset-size:170px;margin-top:var(--basicui-header-height,40px);height:calc(100% - var(--basicui-header-height,40px));background:var(--gpSystemDarkestGrey,#0e141b);color:#fff}
#sgdb-wrap div[class*="gamepadtabbedpage_TabHeaderRowWrapper"]{background:#1b2838}
#sgdb-wrap .tabcontents-wrap{display:flex;height:100%;width:100%;flex-direction:column}
#sgdb-wrap .spinnyboi{display:flex;align-items:center;justify-content:center;position:fixed;inset:0;z-index:10008;background:#0e141b}
#sgdb-wrap .spinnyboi img{transform:scale(.75)}
#sgdb-wrap .sgdb-asset-toolbar{display:flex;width:100%;gap:var(--gpSpace-Gap,.6em)}
#sgdb-wrap .filter-buttons{align-items:center;display:flex;gap:.5em}
#sgdb-wrap .filter-buttons>button{min-width:auto;flex:1;white-space:nowrap}
#sgdb-wrap .size-slider{flex:1;padding:.5em 1em;justify-content:center}
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
#sgdb-wrap .sgdb-results-state{margin:1em 0 0;min-width:auto}.sgdb-status,.sgdb-empty{padding:1em;text-align:center}.sgdb-status.error{color:#ff6b6b}.sgdb-load-more{display:flex;justify-content:center;padding:1em 0 3em}
#sgdb-wrap #local-images-container{display:grid;grid-template-columns:30% 1fr;gap:1em;margin-bottom:2em}
#sgdb-wrap .asset-label{color:#fff;font-weight:500;letter-spacing:1px;text-transform:uppercase;line-height:20px;margin-bottom:.5em}
#sgdb-wrap .manage-asset{position:relative}.manage-asset .asset{display:flex;min-height:100px;overflow:hidden;background:url('/images/defaultappimage.png') center/cover;align-items:center;justify-content:center}.manage-asset .asset-img{display:block;width:100%;max-height:260px;object-fit:contain}
#sgdb-wrap .action-overlay{display:none;position:absolute;gap:.25em;right:.5em;bottom:.5em;z-index:2}.manage-asset.is-focused .action-overlay,.manage-asset:hover .action-overlay{display:flex}.manage-actions{grid-column:span 2;display:flex;gap:.5em}
.sgdb-modal h2,.sgdb-modal h3{color:#fff}.sgdb-modal-details-wrapper{display:flex;gap:1em}.sgdb-modal-details-wrapper.wide{flex-direction:column}.sgdb-modal-details .modal-image{flex:1}.sgdb-modal-details .modal-image img{display:block;max-width:100%;max-height:55vh;margin:auto}.sgdb-modal-details .info{display:flex;flex-direction:column;flex:1;gap:.5em}.sgdb-modal-details .meta{text-transform:capitalize;opacity:.5;font-size:.8em;text-align:right}.sgdb-modal-details .author{margin-top:1em;font-weight:bold}.sgdb-modal-details .notes{max-width:300px;word-break:break-word}
.sgdb-modal-official-assets .official-steam-asset{margin:0 auto 1em}.sgdb-modal-official-assets img{display:block;max-width:100%;max-height:55vh;margin:auto}.official-steam-asset-action{display:flex;align-items:center;justify-content:space-between;gap:1em;margin-top:.5em}
.sgdb-filter-game{display:flex;align-items:end;gap:.5em}.sgdb-filter-game>div:first-child{flex:1}.sgdb-filter-field{margin-top:1em}.sgdb-filter-field>label{display:block;margin-bottom:.4em;font-weight:600}.sgdb-filter-options,.sgdb-game-matches{display:flex;flex-wrap:wrap;gap:.4em}.sgdb-filter-options>button,.sgdb-game-matches>button{min-width:auto}.sgdb-filter-selected{box-shadow:inset 0 0 0 2px var(--gpStoreLightestGrey,#fff)}.sgdb-modal-actions{display:flex;justify-content:flex-end;gap:.5em;margin-top:1em}
.sgdb-logo-preview{height:250px;position:relative;background:#1b2838;overflow:hidden}.sgdb-logo-sample{position:absolute;padding:10px;font-size:28px;font-weight:bold}.anchor-TopLeft{left:0;top:0}.anchor-TopCenter{left:50%;top:0;transform:translateX(-50%)}.anchor-TopRight{right:0;top:0}.anchor-CenterLeft{left:0;top:50%;transform:translateY(-50%)}.anchor-CenterCenter{left:50%;top:50%;transform:translate(-50%,-50%)}.anchor-CenterRight{right:0;top:50%;transform:translateY(-50%)}.anchor-BottomLeft{left:0;bottom:0}.anchor-BottomCenter{left:50%;bottom:0;transform:translateX(-50%)}.anchor-BottomRight{right:0;bottom:0}.sgdb-logo-anchors{display:grid;grid-template-columns:repeat(3,1fr);gap:.4em;margin:1em 0}.sgdb-logo-anchors>button{min-width:auto}
`;

const artworkBrowserPage = registerSteamPage({
  template: "artwork-browser",
  gate: "artworkBrowser",
  patchId: ArtworkBrowserPatchId,
  components: resolveSteamUiComponents,
  required: [
    "react",
    "focusable",
    "sliderField",
    "toggleField",
    "dialogButton",
    "dialogButtonPrimary",
    "tabs",
    "modalRoot",
    "showModal",
  ],
  // A modal left open when the gate goes keeps drawing with Steam's components, which stay valid, so
  // it never throws inside Steam's modal layer; it only loses the state.
  release: () => {
    artworkDesired = null;
    for (const listener of [...artworkListeners]) listener(null);
  },
  status: () => ({ appId: artworkBrowserPage.state()?.appId ?? 0 }),
  Page: ArtworkBrowserPage,
});
