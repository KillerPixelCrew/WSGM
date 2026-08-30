// Fire the exact call Valve's overlay selector makes and report what comes back.
(async () => {
  let req;
  window.webpackChunksteamui.push([["wsgm_wr_" + Date.now()], {}, (r) => { req = r; }]);
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  const out = { bridge: !!b, asset: b && b.assetHash && b.assetHash.slice(0, 8) };
  const perf = window.SteamClient?.System?.Perf;
  out.perfPresent = !!perf;
  out.perfOwned = !!(perf && perf.__wsgmOwnedNamespace === true);
  try {
    const m = req("28013");
    const update = new m.TR();
    const settings = new m.SW();
    // Build the real protobuf delta the way the store does: global.perf_overlay_level = 2.
    const globalCtor = Object.values(req("28013")).find(
      (v) => { try { return new v().getClassName?.() === "CMsgSystemPerfSettingsGlobal"; } catch { return false; } });
    out.globalCtorFound = !!globalCtor;
    if (globalCtor) {
      const g = new globalCtor();
      g.set_perf_overlay_level(2);
      settings.set_global(g);
      update.set_settings_delta(settings);
      const r = await perf.UpdateSettings(update);
      out.updateResult = r === undefined ? "undefined" : JSON.stringify(r).slice(0, 120);
    }
  } catch (e) { out.writeErr = String(e); }
  return JSON.stringify(out);
})();
