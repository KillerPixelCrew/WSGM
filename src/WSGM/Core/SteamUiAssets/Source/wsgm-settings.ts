// WSGM settings frontend; host descriptors and commands are shared with the overlay.
const WsgmSettingsPatchId = "wsgm.settings";
const WsgmSettingsRoute = "/wsgm/settings";

/**
 * Renders host settings sections with the shared native settings renderer.
 * @param props The frame's context and the host's published state.
 * @returns Steam's routed settings pages over the host's descriptors.
 */
function WsgmSettingsPage({ context, state }: any) {
  return renderSteamSettings(context.ui(), {
    route: WsgmSettingsRoute,
    pages: state.pages ?? [],
    revision: state.revision ?? 0,
    // The request is the answer: the renderer drops a refused row's draft and shows why on that row.
    onChange: (row: any, value: any) => context.send("set", { key: row.key, value }),
    // No row on this page is an action.
    onAction: () => {},
  });
}

const wsgmSettingsPage = registerSteamPage({
  template: "wsgm-settings",
  gate: "wsgmSettings",
  patchId: WsgmSettingsPatchId,
  title: "WSGM Settings",
  id: "wsgm-settings",
  // Steam's routed settings layout draws its own header area and sidebar.
  header: false,
  loading: "Loading WSGM settings…",
  required: [...SteamUiPageRequired, "routedPages"],
  // Every refusal is shown on the row that asked.
  banner: false,
  status: () => ({ pages: wsgmSettingsPage.state()?.pages?.length ?? 0 }),
  Page: WsgmSettingsPage,
});
