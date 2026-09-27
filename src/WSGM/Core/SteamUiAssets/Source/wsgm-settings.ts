// WSGM's settings page in Steam, opened from WSGM's row in Steam's main menu.
//
// Thin on purpose. The toolkit's settings renderer draws every row with Steam's own Settings
// components - the routed sidebar, sections, fields and confirm modal - so the page looks and
// navigates exactly like Steam's Settings, and the toolkit's page gate owns its lifecycle. WSGM owns
// the rows and every decision about them.
const WsgmSettingsPatchId = "steam-ui.wsgm-settings";
const WsgmSettingsRoute = "/wsgm/settings";

// Declared once for the life of the asset, so the page keeps its drafts and the controller's focus
// across router renders.
function WsgmSettingsPage({ context }: any) {
  const react = context.react();
  // A refused change is not republished, so the page counts refusals itself: each one is a new
  // revision for the renderer, which drops the draft and shows the host's value again.
  const [refusals, setRefusals] = react.useState(0);
  const state = context.state() ?? {};
  return renderSteamSettings(context.ui(), {
    route: WsgmSettingsRoute,
    pages: state.pages ?? [],
    revision: `${state.revision ?? 0}:${refusals}`,
    onChange: (row: any, value: any) => {
      request(WsgmSettingsPatchId, "set", { key: row.key, value }).catch(() =>
        setRefusals((count: number) => count + 1),
      );
    },
    // No row on this page is an action.
    onAction: () => {},
  });
}

const wsgmSettingsPage = registerSteamPage({
  template: "wsgm-settings",
  gate: "wsgmSettings",
  patchId: WsgmSettingsPatchId,
  components: resolveSteamSettingsComponents,
  required: SteamSettingsRequired,
  status: () => ({ pages: wsgmSettingsPage.state()?.pages?.length ?? 0 }),
  Page: WsgmSettingsPage,
});
