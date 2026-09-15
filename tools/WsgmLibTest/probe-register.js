// Injected bridge registration probes and the localization token check, merged from the former
// probe-register.js, probe-register2.js, probe-subscribe.js and probe-token-exists.js.
//   node run-file.mjs probe-register.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   token-exists  counts module factory sources containing each native-QAM localization token; it
//                 reads sources as strings, never calls runtime(id) and constructs nothing
// Mutating sections, attended only (they target the injected bridge __steamUi_v1_28d7c54a, whose
// shape is obsolete):
//   register      installs every native component kind and reports the host's answer and status
//   register2     installs the Valve component kinds and reports only whether each succeeded
//   subscribe     subscribes to and immediately unsubscribes from three native-QAM patch ids
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

  const mutating = {
    // Register the component kinds and report what the host says, so "no rows" can be attributed
    // to a specific step rather than guessed at.
    register() {
      const b = window.__steamUi_v1_28d7c54a;
      if (!b) return JSON.stringify({ error: "bridge absent" });
      const out = { install: {} };
      for (const kind of [
        "tdp",
        "autoTdp",
        "frameLimit",
        "overlayLevel",
        "controllerTarget",
        "resolution",
        "valveVrr",
        "valveProfileHeader",
        "valveReset",
      ]) {
        try {
          out.install[kind] = b.nativeComponents.install(kind);
        } catch (e) {
          out.install[kind] = String(e);
        }
      }
      out.status = b.nativeComponents.status();
      return JSON.stringify(out);
    },

    register2() {
      const b = window.__steamUi_v1_28d7c54a;
      const out = {};
      for (const kind of [
        "valveFrameLimit",
        "valveOverlayLevel",
        "valveProfileHeader",
        "resolution",
        "valveRefreshRate",
      ]) {
        try {
          out[kind] = b.nativeComponents.install(kind).ok;
        } catch (e) {
          out[kind] = String(e);
        }
      }
      return JSON.stringify(out);
    },

    // Does the call that crashed the Performance tab now succeed? subscribe() throws "subscription
    // not allowlisted" for a patch id missing from config.allowed, and it throws during render,
    // which is why the whole tab went blank rather than one row disappearing.
    subscribe() {
      const b = window.__steamUi_v1_28d7c54a;
      if (!b) return JSON.stringify({ error: "bridge absent" });
      const out = {};
      for (const id of [
        "wsgm.native-qam.resolution",
        "wsgm.native-qam.vrr",
        "wsgm.native-qam.frame-limit",
      ]) {
        try {
          const off = b.subscribe(id, () => {});
          off();
          out[id] = "ok";
        } catch (e) {
          out[id] = String(e && e.message ? e.message : e);
        }
      }
      return JSON.stringify(out);
    },
  };

  const section = typeof __probe === "string" ? __probe : null;
  if (section !== null && Object.hasOwn(readOnly, section)) return readOnly[section]();
  if (section !== null && Object.hasOwn(mutating, section)) return mutating[section]();
  return JSON.stringify({
    section,
    readOnly: Object.keys(readOnly),
    mutating: Object.keys(mutating),
  });
})();
