// This trusted route hands off to Valve's browser in Steam's exact WindowContext. Websites
// never render in this document and receive no WSGM/Steam bridge.
const BrowserPatchId = "wsgm.browser";
let browserNative: any = null;

function BrowserGuidePage({ context }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const instance = browserNative.useWindow();
  const state = context.state();
  const attempted = react.useRef(false);
  const [error, setError] = react.useState("");
  const open = () => {
    try {
      const target = browserNative.identify(instance);
      if (!state || !target || !browserNative.open(instance, undefined, state.home, true))
        throw new Error("Steam's browser is unavailable in this window. Reopen Browser from Quick Access.");
      // Gamepad/MainWindow replaces this handoff route, so native Back returns to its caller.
      // Desktop Overlay opens a separate native browser window and returns its hidden caller.
      if (target.kind === "desktop") instance.History?.goBack?.();
      setError("");
    } catch (failure) {
      setError(String(failure instanceof Error ? failure.message : failure));
    }
  };
  react.useEffect(() => {
    if (!state || attempted.current) return;
    attempted.current = true;
    open();
  }, [instance, !!state]);

  return h(
    "div",
    { id: "wsgm-browser", className: "steam-ui-kit-page", "aria-label": "Browser" },
    steamUiKitStyle(react),
    renderSteamUiLevel(
      ui,
      { bounded: true },
      h("h2", null, "Browser"),
      h("p", { role: error ? "alert" : "status" }, error || "Opening Steam's browser…"),
      error
        ? renderSteamUiActions(ui, [
            { id: "retry", label: "Try again", primary: true, onClick: open },
          ])
        : null,
    ),
  );
}

const browserGuidePage = registerSteamPage({
  template: "browser",
  gate: "browser",
  patchId: BrowserPatchId,
  components: resolveSteamSettingsComponents,
  required: ["react", "focusable", "dialogButton"],
  prepare: (ui, runtime) => {
    browserNative = resolveSteamOverlayBrowser(runtime);
    if (!browserNative) return "Steam's native browser contract is unavailable.";
    if (!browserNative.installChrome(ui, () => browserGuidePage.state()?.searchPrefix)) {
      browserNative.dispose();
      browserNative = null;
      return "Steam's native address editor is unavailable.";
    }
    return null;
  },
  release: () => {
    browserNative?.dispose?.();
    browserNative = null;
  },
  Page: BrowserGuidePage,
});
