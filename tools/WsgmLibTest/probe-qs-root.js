// Probe: the Quick Settings panel's own root, so a row can be appended there rather than to the
// Performance root. Reads module factory sources; resolves nothing by loop, constructs nothing.
(() => {
  let req;
  window.webpackChunksteamui.push([["wsgm_qs_" + Date.now()], {}, (r) => { req = r; }]);
  const sources = Object.entries(req.m).map(([id, f]) => [id, String(f)]);
  const out = {};

  // Every Quick Settings localization token, to learn what the panel is built from.
  const tokens = new Set();
  for (const [, s] of sources) {
    for (const m of s.matchAll(/#QuickAccess_Tab_Settings[A-Za-z0-9_]*/g)) tokens.add(m[0]);
  }
  out.tokens = [...tokens].sort().slice(0, 30);

  // Which module carries the panel itself: the brightness slider and the airplane toggle are both
  // on it, so a module holding both is the root's own.
  out.candidates = sources
    .filter(([, s]) => s.includes("#QuickAccess_Tab_Settings") && s.includes("Brightness"))
    .map(([id, s]) => ({ id, len: s.length, onFrame: s.includes("TS.ON_FRAME") }));
  return JSON.stringify(out);
})();
