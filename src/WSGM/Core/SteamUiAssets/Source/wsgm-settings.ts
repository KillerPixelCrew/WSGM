// WSGM's settings page in Steam, opened from WSGM's row in Steam's main menu.
//
// Thin on purpose. The toolkit's settings renderer draws every row with Steam's own Settings
// components - the routed sidebar, sections, fields and confirm modal - so the page looks and
// navigates exactly like Steam's Settings. WSGM owns the rows and every decision about them.
const WsgmSettingsPatchId = "steam-ui.wsgm-settings";
const WsgmSettingsRoute = "/wsgm/settings";
let wsgmSettingsUi: any = null;
// Steam's React, kept past remove(): a mounted page still calls its hooks on the render that finds
// the gate removed, and they have to come from the same React that mounted it.
let wsgmSettingsReact: any = null;
let wsgmSettingsState: any = null;
const wsgmSettingsListeners = new Set<() => void>();

// One component for the life of the asset. The page host calls the renderer on every router render,
// and a component declared inside it would be a new type each time: React would remount the page
// on every page switch and drop its drafts and the controller's focus.
function WsgmSettingsPage() {
  const react = wsgmSettingsReact;
  const [, setPublished] = react.useState(0);
  // A refused change is not republished, so the page counts refusals itself: each one is a new
  // revision for the renderer, which drops the draft and shows the host's value again.
  const [refusals, setRefusals] = react.useState(0);
  react.useEffect(() => {
    const listener = () => setPublished((value: number) => value + 1);
    wsgmSettingsListeners.add(listener);
    return () => wsgmSettingsListeners.delete(listener);
  }, []);

  // After the hooks, so a render that finds the gate removed still calls the same ones.
  const ui = wsgmSettingsUi;
  if (!ui) return null;
  const state = wsgmSettingsState ?? {};
  return renderSteamSettings(ui, {
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
}

// Always the component, never null: it draws nothing until the gate is there and re-renders on its
// first publication. React comes from the page host before the gate has supplied it; Steam has one.
function renderWsgmSettingsPage(react: any) {
  wsgmSettingsReact ??= react;
  return wsgmSettingsReact.createElement(WsgmSettingsPage, {});
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
    wsgmSettingsReact = wsgmSettingsUi.react;
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
    // A mounted page draws nothing from now on, rather than the rows it last had.
    wsgmSettingsListeners.forEach((listener) => listener());
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
