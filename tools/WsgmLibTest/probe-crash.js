// Diagnostic: the perf store's state while SteamClient.System.Perf exists but WSGM's bridge does
// not. Resolves two named module ids and reads fields; constructs nothing.
(() => {
  let runtime;
  window.webpackChunksteamui.push([["wsgm_crash_probe_" + Date.now()], {}, (r) => { runtime = r; }]);
  const out = {};
  try {
    const mod = runtime("74514");
    const holder = Object.values(mod).find((v) => v && typeof v.Get === "function");
    const store = holder ? holder.Get() : null;
    out.storeFound = !!store;
    if (store) {
      out.msgStateKeys = store.m_msgState ? Object.getOwnPropertyNames(store.m_msgState) : null;
      try { out.limits = store.msgLimits ? Object.keys(store.msgLimits) : String(store.msgLimits); }
      catch (e) { out.limitsError = String(e); }
      try { out.perApp = store.msgSettingsPerApp ? Object.keys(store.msgSettingsPerApp) : String(store.msgSettingsPerApp); }
      catch (e) { out.perAppError = String(e); }
      try { out.global = store.msgSettingsGlobal ? Object.keys(store.msgSettingsGlobal) : String(store.msgSettingsGlobal); }
      catch (e) { out.globalError = String(e); }
    }
  } catch (e) { out.error = String(e); }
  try {
    const perf = window.SteamClient.System.Perf;
    out.perfMethods = Object.keys(perf);
  } catch (e) { out.perfError = String(e); }
  return JSON.stringify(out);
})();
