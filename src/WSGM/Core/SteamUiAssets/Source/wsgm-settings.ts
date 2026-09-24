// WSGM's settings page in Steam, opened from WSGM's row in Steam's main menu.
//
// Thin on purpose. The toolkit's settings renderer draws every row with Steam's own Settings
// components - the routed sidebar, sections, fields and confirm modal - so the page looks and
// navigates exactly like Steam's Settings. WSGM owns the rows and every decision about them.
const WsgmSettingsPatchId = "steam-ui.wsgm-settings";
const WsgmSettingsRoute = "/wsgm/settings";
let wsgmSettingsUi: any = null;
let wsgmSettingsState: any = null;
const wsgmSettingsListeners = new Set<() => void>();

function renderWsgmSettingsPage() {
  const react = wsgmSettingsUi?.react;
  if (!react) return null;

  const Page = () => {
    const [, setPublished] = react.useState(0);
    // A refused change is not republished, so the page counts refusals itself: each one is a new
    // revision for the renderer, which drops the draft and shows the host's value again.
    const [refusals, setRefusals] = react.useState(0);
    react.useEffect(() => {
      const listener = () => setPublished((value: number) => value + 1);
      wsgmSettingsListeners.add(listener);
      return () => wsgmSettingsListeners.delete(listener);
    }, []);

    const state = wsgmSettingsState ?? {};
    return renderSteamSettings(wsgmSettingsUi, {
      route: WsgmSettingsRoute,
      pages: state.pages ?? [],
      revision: `${state.revision ?? 0}:${refusals}`,
      onChange: (row: any, value: any) => {
        request(
          WsgmSettingsPatchId,
          "set",
          { key: row.key, value },
          nextActionGeneration(WsgmSettingsPatchId),
        ).catch(() => setRefusals((count: number) => count + 1));
      },
      // No row on this page is an action.
      onAction: () => {},
    });
  };

  return react.createElement(Page, {});
}

function createWsgmSettings() {
  let installed = false;
  let unsubscribe: (() => void) | null = null;
  let lastError = "";

  const resolve = () => {
    wsgmSettingsUi = resolveSteamSettingsComponents(getWebpackRuntime("wsgm-settings"));
    const missing = SteamSettingsRequired.filter((name) => !wsgmSettingsUi?.[name]);
    if (missing.length) {
      lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
      wsgmSettingsUi = null;
      return false;
    }
    return true;
  };

  const install = () => {
    if (installed) return { ok: true, alreadyInstalled: true };
    if (!attemptResolution(resolve, (error) => (lastError = String(error)))) {
      return { ok: false, error: lastError };
    }
    installed = true;
    lastError = "";
    unsubscribe = subscribe(WsgmSettingsPatchId, (state) => {
      wsgmSettingsState = state;
      wsgmSettingsListeners.forEach((listener) => listener());
    });
    return { ok: true, installed: true };
  };

  const remove = () => {
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    wsgmSettingsState = null;
    wsgmSettingsUi = null;
    return { ok: true };
  };

  const status = () => ({
    installed,
    resolved: !!wsgmSettingsUi,
    subscribed: !!unsubscribe,
    pages: wsgmSettingsState?.pages?.length ?? 0,
    lastError,
  });

  return { install, remove, status };
}

registerSteamPageRenderer("wsgm-settings", renderWsgmSettingsPage);
registerGate("wsgmSettings", createWsgmSettings());
