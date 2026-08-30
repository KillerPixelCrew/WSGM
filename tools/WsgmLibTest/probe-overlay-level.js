// Reports whether the SteamOS Manager gate installed and what its overlaid GetState now answers.
// Read-only against the running client.
(() => {
  const bridge = window.__wsgmSteamUi_v1_28d7c54a;
  if (!bridge) return "no WSGM bridge";
  const out = {};
  try { out.steamOsManager = bridge.steamOsManager.status(); } catch (e) { out.steamOsManager = "ERR " + e; }
  try {
    const s = bridge.nativeComponents.status("valveTdp");
    out.valveTdpRegistered = s.registered;
    out.lastAppend = s.lastAppend;
    out.renderOutcomes = s.renderOutcomes;
  } catch (e) { out.components = "ERR " + e; }
  const cs = window.settingsStore?.clientSettings;
  out.settings = cs ? { tdp: cs.steamos_tdp_limit, enabled: cs.steamos_tdp_limit_enabled } : null;
  return JSON.stringify(out, null, 1);
})();
