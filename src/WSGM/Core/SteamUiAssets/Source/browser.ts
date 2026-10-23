// This trusted page never renders a website. It hands a validated, user-initiated request to
// Valve's browser for the actual overlay instance supplied by Steam's React WindowContext.
const BrowserPatchId = "wsgm.browser";
let browserNative: any = null;

function BrowserGuidePage({ context }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const instance = browserNative.useWindow();
  const target = browserNative.identify(instance);
  const [query, setQuery] = react.useState("");
  const [error, setError] = react.useState("");
  const state = context.state();

  const allowed = (address: string) => {
    if (address === "about:blank") return true;
    try {
      const url = new URL(address);
      return (
        address.length <= 4096 &&
        ["https:", "http:"].includes(url.protocol) &&
        !url.username &&
        !url.password
      );
    } catch {
      return false;
    }
  };

  const lookup = (withGame = false) => {
    try {
      const text = String(query).trim();
      if (!state || !target)
        throw new Error("Open Browser from the Steam Game Overlay of a running game.");
      if (!text || text.length > 4096)
        throw new Error("Enter an address or search (up to 4096 characters).");
      let address: string;
      if (!withGame && allowed(text)) address = text;
      else if (!withGame && text.includes("://"))
        throw new Error("Only HTTP and HTTPS addresses are allowed.");
      else if (!withGame && !text.includes(" ") && text.includes(".") && allowed(`https://${text}`))
        address = `https://${text}`;
      else {
        if (!withGame && /^(?:[a-z][a-z0-9+.-]*):/i.test(text))
          throw new Error("Only HTTP and HTTPS addresses are allowed.");
        const search = withGame ? `${target.title} ${text}` : text;
        address = `${state.searchPrefix}${encodeURIComponent(search)}`;
      }
      if (!allowed(address)) throw new Error("This address is not allowed.");
      if (!browserNative.open(instance, address))
        throw new Error("Steam's native overlay browser is unavailable.");
      setError("");
    } catch (failure) {
      setError(String(failure instanceof Error ? failure.message : failure));
    }
  };

  const resume = () => {
    if (!target || !browserNative.open(instance, undefined, state?.home)) {
      setError(
        "Steam's native overlay browser is unavailable. Return to the game and reopen the overlay.",
      );
    }
  };

  const content = renderSteamUiLevel(
    ui,
    { bounded: true },
    h("h2", null, "Browser / guides"),
    h(
      "p",
      { className: "steam-ui-kit-muted" },
      target
        ? `Quick lookups for ${target.title || "this game"}`
        : "Open this page from Steam's in-game overlay.",
    ),
    h(ui.textField, {
      label: "Search or enter an address",
      value: query,
      maxLength: 4096,
      disabled: !target,
      onChange: (event: any) => setQuery(String(event?.target?.value ?? "")),
      onKeyDown: (event: any) => {
        if (event.key === "Enter") lookup();
      },
    }),
    renderSteamUiActions(ui, [
      {
        id: "lookup",
        label: "Open lookup",
        primary: true,
        disabled: !target || !query.trim(),
        onClick: () => lookup(),
      },
      {
        id: "game-search",
        label: `Search ${target?.title || "current game"}…`,
        disabled: !target?.title || !query.trim(),
        onClick: () => lookup(true),
      },
      { id: "resume", label: "Resume native browser", disabled: !target, onClick: resume },
    ]),
    error ? h("p", { role: "alert" }, error) : null,
    h(
      "p",
      { className: "steam-ui-kit-muted" },
      "Use Steam's browser controls and keyboard, then close the overlay to return to your game. Resume reopens your last lookup.",
    ),
    h(
      "p",
      { className: "steam-ui-kit-muted" },
      "Steam manages its browser's memory, downloads and link prompts. WSGM's memory setting applies to its own Overlay browser.",
    ),
    h(
      "p",
      { className: "steam-ui-kit-muted" },
      "Search current game adds the game title only when you choose it. Your search goes directly to the search provider.",
    ),
  );
  return h(
    "div",
    { id: "wsgm-browser", className: "steam-ui-kit-page", "aria-label": "Browser / guides" },
    steamUiKitStyle(react),
    h("div", { style: { flex: 1, minHeight: 0, overflowY: "auto" } }, content),
  );
}

const browserGuidePage = registerSteamPage({
  template: "browser",
  gate: "browser",
  patchId: BrowserPatchId,
  components: resolveSteamSettingsComponents,
  required: ["react", "focusable", "dialogButton", "dialogButtonPrimary", "textField"],
  prepare: (_ui, runtime) => {
    browserNative = resolveSteamOverlayBrowser(runtime);
    return browserNative ? null : "Steam's native overlay browser contract is unavailable.";
  },
  release: () => {
    browserNative?.dispose?.();
    browserNative = null;
  },
  Page: BrowserGuidePage,
});
