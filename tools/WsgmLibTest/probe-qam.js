// Steam Quick Access and Quick Settings probes, merged from the former probe-qam-*.js,
// probe-qs-root.js and probe-settings-change.js iterations.
//   node run-file.mjs probe-qam.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections (qam-live calls the Wi-Fi availability gate):
//   qam-gates       source around each Quick Settings availability gate, and SteamClient surfaces
//   qam-gates2      the exported gate and row functions read directly
//   qam-live        live gate values, store exports and window stores
//   qam-settings    QuickAccess localization tokens grouped by owning module
//   qam-settings2   the Quick Settings tab composition (79476) and where its rows live
//   qs-root         the `function De(` tab-tap handler that reads GetControllers
// Mutating sections, attended only:
//   settings-change writes steamos_tdp_limit = 21 and captures the settings change it produces
(() => {
  const webpack = () => {
    let runtime;
    window.webpackChunksteamui.push([
      ["wsgm_probe_qam_" + Date.now()],
      {},
      (r) => {
        runtime = r;
      },
    ]);
    return runtime;
  };

  const readOnly = {
    // The availability gates behind each Quick Settings section, and which SteamClient surfaces back
    // them.
    "qam-gates"() {
      const runtime = webpack();
      const out = {};
      const grab = (label, id, needle, before, after) => {
        try {
          const src = String(runtime.m[id]);
          const i = src.indexOf(needle);
          out[label] =
            i < 0 ? "NOT FOUND: " + needle : src.slice(Math.max(0, i - before), i + after);
        } catch (e) {
          out[label] = "ERR " + e;
        }
      };
      // Brightness / night mode / airplane store (59547)
      grab("brightness_store", "59547", "SetNightModeEnabled", 1400, 600);
      // Wi-Fi availability (77347) and the Wi-Fi row (89600)
      grab("wifi_gate", "77347", "function Ev", 0, 700);
      grab("wifi_row", "89600", "cV", 0, 900);
      // Bluetooth availability (25467) and row (66943)
      grab("bt_gate", "25467", "function Iz", 0, 700);
      grab("bt_row", "66943", "ToggleLabel", 900, 500);
      // Audio availability (1409)
      grab("audio_gate", "1409", "function In", 0, 700);
      // The unidentified section (17386)
      grab("unknown_section", "17386", "function DP", 0, 700);
      // What SteamClient surfaces exist for these?
      try {
        const sc = window.SteamClient || {};
        out.hasSystemAudio = typeof sc.System?.Audio;
        out.hasSystemBluetooth = typeof sc.System?.Bluetooth;
        out.systemNetwork = Object.keys(sc.System?.Network || {});
        out.systemDisplay = Object.keys(sc.System?.Display || {});
        out.systemPerf = typeof sc.System?.Perf;
      } catch (e) {
        out.scErr = String(e);
      }
      return JSON.stringify(out);
    },

    // Resolve the Quick Settings availability gates by reading the exported functions directly
    // rather than searching minified text.
    "qam-gates2"() {
      const runtime = webpack();
      const out = {};
      const fn = (label, id, name) => {
        try {
          const mod = runtime(id);
          const v = mod[name];
          out[label] = typeof v === "function" ? String(v).slice(0, 900) : typeof v;
        } catch (e) {
          out[label] = "ERR " + e;
        }
      };
      fn("wifi_available_77347_Ev", "77347", "Ev");
      fn("wifi_row_89600_cV", "89600", "cV");
      fn("bt_available_25467_Iz", "25467", "Iz");
      fn("bt_row_66943_ty", "66943", "ty");
      fn("audio_available_1409_In", "1409", "In");
      fn("brightness_available_59547_zx", "59547", "zx");
      fn("brightness_row_83571_PS", "83571", "PS");
      fn("brightness_row_83571_zt", "83571", "zt");
      fn("section_17386_DP", "17386", "DP");
      fn("section_17386_vB", "17386", "vB");
      fn("nightmode_supported_96555_hb", "96555", "hb");
      return JSON.stringify(out);
    },

    // The live value of each Quick Settings availability gate on this Windows client, so we know
    // which rows would render if mounted. Toggles nothing.
    "qam-live"() {
      const runtime = webpack();
      const out = {};
      const tryGet = (label, f) => {
        try {
          out[label] = f();
        } catch (e) {
          out[label] = "ERR " + String(e).slice(0, 160);
        }
      };
      // Wi-Fi / network management store (77347).
      tryGet("network_module_exports", () => Object.keys(runtime("77347")));
      tryGet("wifi_available", () => {
        const m = runtime("77347");
        return typeof m.Ev === "function" ? m.Ev() : "no Ev";
      });
      // Audio store (1409).
      tryGet("audio_module_exports", () => Object.keys(runtime("1409")));
      // Brightness / system manager settings store (59547).
      tryGet("brightness_module_exports", () => Object.keys(runtime("59547")));
      // Bluetooth availability comes from the SteamOS manager state (33706).
      tryGet("steamos_manager_state", () => {
        const m = runtime("33706");
        return Object.keys(m);
      });
      // Global stores Steam exposes on window, which are readable without hooks.
      tryGet("windowStores", () =>
        Object.keys(window)
          .filter((k) => /Store$|Manager$/.test(k))
          .slice(0, 60),
      );
      return JSON.stringify(out);
    },

    // What the QAM Quick Settings tab is made of: which modules own it, which localization tokens it
    // renders, and therefore which rows exist.
    "qam-settings"() {
      const runtime = webpack();
      const out = { owners: {}, tokens: {} };
      const ids = Object.keys(runtime.m);
      out.moduleCount = ids.length;

      // Every QuickAccess settings-tab localization token, grouped by owning module.
      const tokenRe = /#QuickAccess_[A-Za-z0-9_]+/g;
      const perModule = {};
      for (const id of ids) {
        let src;
        try {
          src = String(runtime.m[id]);
        } catch {
          continue;
        }
        if (!src.includes("#QuickAccess_")) continue;
        const found = new Set();
        let m;
        while ((m = tokenRe.exec(src)) !== null) found.add(m[0]);
        if (found.size) perModule[id] = { len: src.length, count: found.size, tokens: [...found] };
      }
      out.owners = perModule;
      return JSON.stringify(out);
    },

    // The Quick Settings tab composition (module 79476) and where Wi-Fi / network / brightness /
    // audio rows live.
    "qam-settings2"() {
      const runtime = webpack();
      const out = {};
      try {
        out.tab = String(runtime.m["79476"]);
      } catch (e) {
        out.tabErr = String(e);
      }
      // Where do wifi / network / brightness / volume rows live?
      const probes = ["Wifi", "WiFi", "wifi", "Network_", "Brightness", "Volume", "AirplaneMode"];
      out.hits = {};
      for (const id of Object.keys(runtime.m)) {
        let src;
        try {
          src = String(runtime.m[id]);
        } catch {
          continue;
        }
        for (const p of probes) {
          if (!src.includes(p)) continue;
          (out.hits[p] = out.hits[p] || []).push({ id, len: src.length });
        }
      }
      for (const p of Object.keys(out.hits)) {
        out.hits[p] = out.hits[p].sort((a, b) => a.len - b.len).slice(0, 6);
      }
      return JSON.stringify(out);
    },

    // All `function De(` occurrences, matched to the tab-tap capture by its head (GetControllers
    // + ~2 KB), and dump that one's full source.
    "qs-root"() {
      const req = webpack();
      const results = [];
      for (const [id, f] of Object.entries(req.m)) {
        const s = String(f);
        let at = -1;
        while ((at = s.indexOf("function De(", at + 1)) >= 0) {
          let depth = 0,
            end = at;
          for (let i = s.indexOf("{", at); i < s.length; i++) {
            if (s[i] === "{") depth++;
            else if (s[i] === "}" && --depth === 0) {
              end = i + 1;
              break;
            }
          }
          const body = s.slice(at, end);
          if (body.includes("GetControllers")) results.push({ module: id, len: body.length, body });
        }
      }
      return JSON.stringify(results.slice(0, 2));
    },
  };

  const mutating = {
    async "settings-change"() {
      const req = webpack();
      const out = {};
      // The message class qt serializes with: `b.Ne` where b is an import of 33867.
      const src = String(req.m["33867"]);
      const importMatch = src.match(
        /([A-Za-z_$]+)=r\((\d+)\)[^;]*;?[\s\S]{0,600}?\1\.Ne\.serializeBinaryToWriter/,
      );
      const clsModule = importMatch ? importMatch[2] : null;
      out.clsModule = clsModule;
      const Ne = clsModule ? req(clsModule).Ne : null;
      out.clsName = Ne
        ? (() => {
            try {
              return new Ne().getClassName?.();
            } catch {
              return null;
            }
          })()
        : null;

      const captured = [];
      let handle = null;
      try {
        handle = window.SteamClient.Settings.RegisterForSettingsArrayChanges((...args) => {
          captured.push(args.map((a) => (typeof a === "string" ? a.slice(0, 80) : typeof a)));
          if (Ne && typeof args[0] === "string") {
            try {
              const bytes = Uint8Array.from(atob(args[0]), (c) => c.charCodeAt(0));
              const obj = Ne.deserializeBinary(bytes).toObject();
              out.decoded = Object.fromEntries(
                Object.entries(obj).filter(([, v]) => v !== undefined && v !== null),
              );
            } catch (e) {
              out.decodeErr = String(e);
            }
          }
        });
      } catch (e) {
        out.regErr = String(e);
      }
      try {
        req("33867").qt("steamos_tdp_limit", 21);
      } catch (e) {
        out.writeErr = String(e);
      }
      await new Promise((resolve) => setTimeout(resolve, 1200));
      out.captured = captured.slice(0, 2);
      try {
        handle?.unregister?.();
      } catch {}
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
