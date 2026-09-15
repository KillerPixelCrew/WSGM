// Quick Settings gate and native row probes, merged from the former probe-brightness-gate.js,
// probe-nightmode-gate.js, probe-overlay-level.js, probe-resolution-row.js and
// probe-valve-vrr-mount.js.
//   node run-file.mjs probe-gates.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   brightness-gate  display brightness availability in store 59547 and the SteamClient display API
//   overlay-level    the display store's brightness value and the store source that sets it
//   resolution-row   the structural counts NativeQamResolutionPatch requires, its own probe verbatim
//   valve-vrr-mount  that the shim's Valve VRR selector resolves to exactly one module and export
// Mutating sections, attended only:
//   nightmode-gate   overrides the 96555 night-mode support getter, then restores the descriptor
(() => {
  const webpack = () => {
    let runtime;
    window.webpackChunksteamui.push([
      ["wsgm_probe_gates_" + Date.now()],
      {},
      (r) => {
        runtime = r;
      },
    ]);
    return runtime;
  };

  const readOnly = {
    "brightness-gate"() {
      const req = webpack();
      const store = req("59547").mG.Get();
      const s = store.m_msgSettings || {};
      return JSON.stringify({
        is_display_brightness_available: s.is_display_brightness_available,
        display_brightness_overdrive_hdr_split: s.display_brightness_overdrive_hdr_split,
        m_flDisplayBrightness: store.m_flDisplayBrightness,
        settingsKeyCount: Object.keys(s).length,
        hasSetBrightness: typeof window.SteamClient?.System?.Display?.SetBrightness === "function",
        hasRegisterBrightness:
          typeof window.SteamClient?.System?.Display?.RegisterForBrightnessChanges === "function",
      });
    },

    // Reads the display store's brightness value and the store source around it.
    "overlay-level"() {
      const chunk = window.webpackChunksteamui;
      if (!chunk) return "no webpackChunksteamui";
      let runtime = null;
      chunk.push([
        [Symbol("wsgm-brightness2-probe")],
        {},
        (r) => {
          runtime = r;
        },
      ]);
      if (!runtime) return "no runtime";
      const store = runtime("59547")?.mG?.Get?.();
      const out = { flDisplayBrightness: store?.m_flDisplayBrightness };
      // The store class source around brightness: who sets m_flDisplayBrightness.
      const src = String(runtime.m["59547"]);
      const at = src.indexOf("m_flDisplayBrightness");
      out.storeSource = src.slice(Math.max(0, at - 500), at + 700);
      return JSON.stringify(out, null, 1);
    },

    // The structural counts NativeQamResolutionPatch requires, so the row's fingerprint is checked
    // against the live client rather than assumed from the frame-limit row it copies. This is the
    // patch's OWN probe expression, verbatim. It reads module factory SOURCES as strings and never
    // calls runtime(id) or constructs anything; see the rule in docs\steam-cef.md.
    "resolution-row"() {
      let req;
      window.webpackChunksteamui.push([
        ["steam_ui_resolution_probe_" + Date.now()],
        {},
        (r) => {
          req = r;
        },
      ]);
      if (!req || !req.m) return JSON.stringify({ error: "webpack unavailable" });
      const count = (tokens) =>
        Object.values(req.m).reduce((total, factory) => {
          const source = String(factory);
          return total + (tokens.every((token) => source.includes(token)) ? 1 : 0);
        }, 0);
      return JSON.stringify({
        performanceActions: count([
          "SetFPSLimitEnabled",
          "SetFPSLimit",
          "SetPerfOverlayLevel",
          "SteamClient.System.Perf",
        ]),
        performanceRoot: count([
          "#QuickAccess_Tab_Perf_Common_Settings",
          "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
          "TS.ON_FRAME",
        ]),
        nativeFields: count(["DialogSlider_Container", "DropDownField", "SliderField"]),
        nativeLayout: count(["PanelSectionTitle", "PanelSectionRow", "spinner"]),
        localization: count([
          "Attempting to localize token",
          "Unable to find localization token",
          "LocalizeString",
        ]),
        react: count(["react.transitional.element", "useState", "cloneElement", "createElement"]),
      });
    },

    // That the selector the shim uses to mount Valve's VRR control resolves to exactly one module
    // and one export. This is the selector verbatim (module by structural tokens, then export by the
    // localization token it draws), so a pass here is evidence for the real code path. Resolves ONE
    // module id, found by reading factory sources; see the rule in docs\steam-cef.md.
    "valve-vrr-mount"() {
      const runtime = webpack();

      const uniqueFactory = (requiredTokens) => {
        const matches = Object.entries(runtime.m).filter(([, factory]) => {
          const source = String(factory);
          return requiredTokens.every((token) => source.includes(token));
        });
        return matches.length === 1 ? matches[0] : null;
      };

      const out = {};
      try {
        const matches = Object.entries(runtime.m).filter(([, factory]) => {
          const source = String(factory);
          return (
            source.includes("#QuickAccess_Tab_Perf_EnableVRR") &&
            source.includes("#QuickAccess_Tab_Perf_LimitFrameRate")
          );
        });
        out.moduleMatches = matches.length;

        const factory = uniqueFactory([
          "#QuickAccess_Tab_Perf_EnableVRR",
          "#QuickAccess_Tab_Perf_LimitFrameRate",
        ]);
        if (!factory) return JSON.stringify({ ...out, error: "module not unique" });
        out.moduleId = factory[0];

        const exports = runtime(factory[0]);
        const components = Object.values(exports).filter(
          (value) =>
            typeof value === "function" &&
            String(value).includes("#QuickAccess_Tab_Perf_EnableVRR"),
        );
        out.exportMatches = components.length;
        out.isFunction = components.length === 1 && typeof components[0] === "function";
      } catch (error) {
        out.error = String(error);
      }

      return JSON.stringify(out);
    },
  };

  const mutating = {
    "nightmode-gate"() {
      const req = webpack();
      const mod = req("96555");
      const d = Object.getOwnPropertyDescriptor(mod, "hb");
      const out = {
        descriptor: d
          ? {
              hasGet: typeof d.get === "function",
              writable: d.writable,
              configurable: d.configurable,
            }
          : "absent",
      };
      if (d && d.configurable) {
        try {
          Object.defineProperty(mod, "hb", { get: () => () => true, configurable: true });
          out.overrideWorks = mod.hb() === true;
          Object.defineProperty(mod, "hb", d);
          out.restored = mod.hb() === false;
        } catch (e) {
          out.error = String(e).slice(0, 200);
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
