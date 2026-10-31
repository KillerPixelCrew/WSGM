// This trusted route hands off to Valve's browser in Steam's exact WindowContext. Websites
// never render in this document and receive no WSGM/Steam bridge.
const BrowserPatchId = "wsgm.browser";
let browserNative: any = null;

/**
 * Hands the route off to Steam's own browser once the host has published its home page, and says
 * why with a retry when Steam's browser is not reachable from this window.
 * @param props The frame's context and the host's published home and search prefix.
 * @returns The handoff status inside the page frame.
 */
function BrowserGuidePage({ context, state }: any) {
  const ui = context.ui();
  const react = ui.react;
  const h = react.createElement;
  const instance = browserNative.useWindow();
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
  // The frame draws the page once the host has published, so the first render may hand off.
  react.useEffect(() => {
    if (!state || attempted.current) return;
    attempted.current = true;
    open();
  }, [instance, !!state]);

  return renderSteamUiLevel(
    ui,
    { bounded: true },
    h("h2", null, "Browser"),
    error
      ? renderSteamUiEmpty(ui, { kind: "unavailable", text: error, action: { label: "Try again", onClick: open } })
      : renderSteamUiEmpty(ui, { kind: "loading", text: "Opening Steam's browser…" }),
  );
}

const browserGuidePage = registerSteamPage({
  template: "browser",
  gate: "browser",
  patchId: BrowserPatchId,
  title: "Browser",
  id: "wsgm-browser",
  loading: "Opening Steam's browser…",
  // The page publishes no notice of its own; a handoff failure is said on the page with its retry.
  banner: false,
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
