// The Graphics page in Steam, opened from its row in Steam's main menu while a graphics package runs.
//
// Thin on purpose, like WSGM's settings page: the toolkit's settings renderer draws every row with
// Steam's own Settings components, one sidebar page per adapter and display. WSGM owns the rows and
// every decision about them. A game override is marked by colour, as on Quick Access, with no Use
// global control: Steam's Reset button is the way back.
const WsgmGraphicsPatchId = "steam-ui.wsgm-graphics";
const WsgmGraphicsRoute = "/wsgm/graphics";

// Declared once for the life of the asset, so the page keeps its drafts and the controller's focus
// across router renders.
function WsgmGraphicsPage({ context }: any) {
  const react = context.react();
  // A refused change is not republished, so the page counts refusals itself: each one is a new
  // revision for the renderer, which drops the draft and shows the host's value again.
  const [refusals, setRefusals] = react.useState(0);
  const state = context.state() ?? {};
  const refused = () => setRefusals((count: number) => count + 1);
  return renderSteamSettings(context.ui(), {
    route: WsgmGraphicsRoute,
    pages: state.pages ?? [],
    revision: `${state.revision ?? 0}:${refusals}`,
    onChange: (row: any, value: any) => {
      request(WsgmGraphicsPatchId, "set", { key: row.key, value }).catch(refused);
    },
    // An action row runs its capability, which the host reads as a value-less write.
    onAction: (row: any) => {
      request(WsgmGraphicsPatchId, "set", { key: String(row.key ?? ""), value: true }).catch(refused);
    },
  });
}

const wsgmGraphicsPage = registerSteamPage({
  template: "wsgm-graphics",
  gate: "wsgmGraphics",
  patchId: WsgmGraphicsPatchId,
  components: resolveSteamSettingsComponents,
  required: SteamSettingsRequired,
  status: () => ({ pages: wsgmGraphicsPage.state()?.pages?.length ?? 0 }),
  Page: WsgmGraphicsPage,
});
