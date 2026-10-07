// WSGM settings frontend; host descriptors and commands are shared with the overlay.
const WsgmSettingsPatchId = "wsgm.settings";
const WsgmSettingsRoute = "/wsgm/settings";

/**
 * Renders host settings sections with the shared native settings renderer.
 * @param context Registered page accessors for Steam components, latest state and publication refusal.
 * @returns The page React tree, including loading or refusal state when data is unavailable.
 */
function WsgmSettingsPage({ context }: any) {
  const state = context.state() ?? {};
  return renderSteamSettings(context.ui(), {
    route: WsgmSettingsRoute,
    pages: state.pages ?? [],
    revision: state.revision ?? 0,
    // The request is the answer: the renderer drops a refused row's draft and shows why on that row.
    onChange: (row: any, value: any) =>
      request(WsgmSettingsPatchId, "set", { key: row.key, value }),
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
