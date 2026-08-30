// Probe: what gates Steam's Bluetooth SETTINGS page, as opposed to the QAM toggle that now works.
// Reads module factory sources as strings; resolves nothing by loop, constructs nothing.
(() => {
  let req;
  window.webpackChunksteamui.push([["wsgm_btpage_" + Date.now()], {}, (r) => { req = r; }]);
  const out = {};
  const sources = Object.entries(req.m).map(([id, f]) => [id, String(f)]);

  // Which modules mention the Bluetooth settings page at all.
  const tokens = new Set();
  for (const [, s] of sources) {
    for (const m of s.matchAll(/#Settings_Bluetooth[A-Za-z0-9_]*/g)) tokens.add(m[0]);
  }
  out.tokens = [...tokens].sort();

  // The modules carrying them, and whether they mention an availability gate.
  out.modules = sources
    .filter(([, s]) => s.includes("#Settings_Bluetooth"))
    .map(([id, s]) => ({
      id,
      len: s.length,
      bAvailable: s.includes("m_bAvailable"),
      isAvailable: /[Ii]sAvailable/.test(s),
      useQuery: s.includes("useQuery"),
      rf: s.includes("RF") || s.includes("Rf"),
    }));
  return JSON.stringify(out);
})();
