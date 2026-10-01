// The native-QAM localization token check, formerly probe-token-exists.js.
//   node run-file.mjs probe-register.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   token-exists  counts module factory sources containing each native-QAM localization token; it
//                 reads sources as strings, never calls runtime(id) and constructs nothing
// There are no mutating sections.
(() => {
  const readOnly = {
    // Whether every localization token the injected shim asks for actually exists in the client
    // bundle. A token that does not exist falls back to WSGM's English default silently and makes
    // Steam log an unresolved token on every render, so it only shows up on a localized client.
    "token-exists"() {
      let req;
      window.webpackChunksteamui.push([
        ["wsgm_token_probe_" + Date.now()],
        {},
        (r) => {
          req = r;
        },
      ]);
      if (!req || !req.m) return JSON.stringify({ error: "webpack unavailable" });

      // Every token passed to localizeOr in the native-QAM component source.
      const wanted = [
        "#QuickAccess_Tab_Perf_AutoTDP",
        "#QuickAccess_Tab_Perf_LimitFrameRate",
        "#QuickAccess_Tab_Perf_PerfOverlayLevel",
        "#QuickAccess_Tab_Perf_TDPLimitEnabled",
        "#QuickAccess_Tab_Perf_TDPLimitUnits",
        "#QuickAccess_Tab_Perf_TDPLimit_Explainer",
        "#QuickAccess_Tab_Settings_Section_Controller_Title",
      ];

      const sources = Object.values(req.m).map((factory) => String(factory));
      const out = {};
      for (const token of wanted) {
        out[token] = sources.reduce((total, source) => total + (source.includes(token) ? 1 : 0), 0);
      }
      return JSON.stringify(out);
    },
  };

  const mutating = {};

  const section = typeof __probe === "string" ? __probe : null;
  if (section !== null && Object.hasOwn(readOnly, section)) return readOnly[section]();
  if (section !== null && Object.hasOwn(mutating, section)) return mutating[section]();
  return JSON.stringify({
    section,
    readOnly: Object.keys(readOnly),
    mutating: Object.keys(mutating),
  });
})();
