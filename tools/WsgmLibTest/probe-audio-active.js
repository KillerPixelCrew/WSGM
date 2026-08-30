// Probe: audio store identity types — device ids vs active ids. Wider singleton lookup: the
// earlier .Get-based search missed it, so find the export holding m_bAvailable however held.
(() => {
  let req;
  window.webpackChunksteamui.push([["wsgm_audio2_" + Date.now()], {}, (r) => { req = r; }]);
  const mod = req("1409");
  let store = null;
  for (const v of Object.values(mod)) {
    if (v && typeof v === "object") {
      if ("m_bAvailable" in v) { store = v; break; }
      if (typeof v.Get === "function") { try { const s = v.Get(); if (s && "m_bAvailable" in s) { store = s; break; } } catch {} }
    }
  }
  if (!store) return JSON.stringify({ error: "no export holds m_bAvailable", keys: Object.keys(mod) });
  const out = { available: store.m_bAvailable };
  for (const k of Object.getOwnPropertyNames(store)) {
    if (!/[Aa]ctive|[Dd]evice/.test(k)) continue;
    const v = store[k];
    if (v instanceof Map) {
      out[k] = [...v.entries()].slice(0, 3).map(([key, val]) => ({
        keyType: typeof key, key,
        id: val && val.id, idType: typeof (val && val.id),
        name: val && (val.sName ?? val.name),
      }));
    } else if (Array.isArray(v)) {
      out[k] = v.slice(0, 3).map((val) => ({ id: val && val.id, idType: typeof (val && val.id), name: val && (val.sName ?? val.name) }));
    } else {
      out[k] = { type: typeof v, value: typeof v === "object" && v !== null ? Object.keys(v).slice(0, 6) : v };
    }
  }
  return JSON.stringify(out);
})();
