// The Graphics page in Steam, opened from its row in Steam's main menu while a graphics package runs.
//
// Thin on purpose, like WSGM's settings page: the toolkit's settings renderer draws every row with
// Steam's own Settings components, one sidebar page per adapter and display. WSGM owns the rows, the
// Use global rows that stand for a game override, and every decision about them.
const WsgmGraphicsPatchId = "steam-ui.wsgm-graphics";
const WsgmGraphicsRoute = "/wsgm/graphics";
const WsgmGraphicsGlobalPrefix = "global:";

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
    // A Use global row returns its setting to the Global profile; any other action row runs its
    // capability, which the host reads as a value-less write.
    onAction: (row: any) => {
      const key = String(row.key ?? "");
      const sent = key.startsWith(WsgmGraphicsGlobalPrefix)
        ? request(WsgmGraphicsPatchId, "useGlobal", { key })
        : request(WsgmGraphicsPatchId, "set", { key, value: true });
      sent.catch(refused);
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
