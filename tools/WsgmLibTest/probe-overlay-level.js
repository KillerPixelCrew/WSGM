// Looks for an exported handle on the client-settings store that Valve's hooks read
// (`G.clientSettings[name]`), and reports whether steamos_tdp_limit is present on it. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([
    [Symbol("wsgm-settings-store-probe")],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  if (!runtime) return "no runtime";

  const out = { exportsWithClientSettings: {}, windowCandidates: [] };
  let mod;
  try {
    mod = runtime("33867");
  } catch (e) {
    return "33867: " + e;
  }
  for (const key of Object.keys(mod)) {
    const value = mod[key];
    if (!value) continue;
    try {
      if (typeof value === "object" && "clientSettings" in value) {
        out.exportsWithClientSettings[key] = {
          hasTdp: "steamos_tdp_limit" in (value.clientSettings ?? {}),
          tdp: value.clientSettings?.steamos_tdp_limit ?? null,
          tdpEnabled: value.clientSettings?.steamos_tdp_limit_enabled ?? null,
          keys: Object.keys(value).slice(0, 25),
        };
      }
      if (typeof value === "function" && value.Get) {
        const got = value.Get();
        if (got && typeof got === "object" && "clientSettings" in got) {
          out.exportsWithClientSettings[key + ".Get()"] = {
            hasTdp: "steamos_tdp_limit" in (got.clientSettings ?? {}),
            keys: Object.keys(got).slice(0, 25),
          };
        }
      }
    } catch {}
  }
  for (const name of Object.keys(window)) {
    try {
      const value = window[name];
      if (value && typeof value === "object" && "clientSettings" in value) {
        out.windowCandidates.push(name);
      }
    } catch {}
  }
  return JSON.stringify(out, null, 1);
})();
