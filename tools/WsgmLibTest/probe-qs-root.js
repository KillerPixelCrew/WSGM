// Probe: what DT and l8 in 79476 actually are, and where the QS tab's panel element comes from.
// Reads sources of named modules; constructs nothing.
(() => {
  let req;
  window.webpackChunksteamui.push([["wsgm_qs3_" + Date.now()], {}, (r) => { req = r; }]);
  const mod = req("79476");
  const out = {};
  for (const key of Object.keys(mod)) {
    const v = mod[key];
    out[key] = {
      type: typeof v,
      head: typeof v === "function" ? String(v).slice(0, 260) : String(v).slice(0, 120),
    };
  }
  // Who imports 79476? The tabs module that builds the QAM tab list references its panel export.
  const importers = [];
  for (const [id, f] of Object.entries(req.m)) {
    const s = String(f);
    if (/r\(79476\)|\(79476\)/.test(s)) importers.push({ id, len: s.length, qamTitle: s.includes("#QuickAccess_Tab_Settings_Title") });
  }
  out.importers = importers.slice(0, 6);
  return JSON.stringify(out);
})();
