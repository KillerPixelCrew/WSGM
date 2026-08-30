// Confirms each selector for Valve's TDP rows matches exactly one export of module 38747, and reads
// the client settings the rows write. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([[Symbol("wsgm-tdp-select-probe")], {}, (r) => { runtime = r; }]);
  if (!runtime) return "no runtime";
  const mod = runtime("38747");
  const selectors = {
    toggle: ['"steamos_tdp_limit_enabled"', "#QuickAccess_Tab_Perf_TDPLimitEnabled"],
    slider: ["#QuickAccess_Tab_Perf_TDPLimitUnits"],
  };
  const out = { matches: {} };
  for (const [name, tokens] of Object.entries(selectors)) {
    const hits = [];
    for (const key of Object.keys(mod)) {
      let src = "";
      try { src = String(mod[key]); } catch { continue; }
      if (typeof mod[key] === "function" && tokens.every((t) => src.includes(t))) hits.push(key);
    }
    out.matches[name] = hits;
  }
  const cs = window.settingsStore?.clientSettings;
  out.settings = cs
    ? { tdp: cs.steamos_tdp_limit, enabled: cs.steamos_tdp_limit_enabled }
    : "no settingsStore";
  // Does the feature flag on those rows gate anything platform-shaped?
  const featureSrc = String(runtime("38747").n1 ?? "");
  out.featureProp = featureSrc.includes("feature:") ? "present" : "absent";
  return JSON.stringify(out, null, 1);
})();
