// Steam Performance tab probes (perf store 74514, protobuf module 28013, settings hooks 33867, TDP
// rows), merged from the former probe-perf-*.js and probe-tdp-rpc.js iterations. The token probe
// probe-perf-components.js stays a separate file because the docs cite it as the safe shape.
//   node run-file.mjs probe-perf.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   perf-backend        which modules own the steamos_*/gamescope_* settings the controls bind to
//   perf-dump           module 83571 source
//   perf-exports        which 83571 export renders each performance control token
//   perf-limiter        client setting keys the perf tab consults, and module 38747
//   perf-live           the l5 import of 74514 and whether the live store carries fps limit options
//   perf-modules        which modules carry SteamOS per-app performance tokens, and SteamClient.System
//   perf-nested-fields  field names of the nested CMsgSystemPerf* messages in 28013
//   perf-proto          class and enum shapes exported by 28013
//   perf-state          live perf store contents and the 33867 settings hook exports
//   perf-store          sources of 74514 and 33867 and the live store's shape
//   perf-tdp            TDP slider candidates in 90389 and 85857
//   perf-tdp2           the TDP Limit row (29788) and the SteamOS manager state fetch (33706)
// Mutating sections, attended only:
//   perf-shim           defines a stand-in SteamClient.System.Perf and store state, then removes both
//   tdp-rpc             delivers TDP state to the injected bridge and reads the merged manager answer
(() => {
  const webpack = () => {
    let runtime;
    window.webpackChunksteamui.push([
      ["wsgm_probe_perf_" + Date.now()],
      {},
      (r) => {
        runtime = r;
      },
    ]);
    return runtime;
  };

  const readOnly = {
    // Which backend the shipped TDP and frame-limit controls actually read and write: the
    // CMsgSystemPerf store, or the steamos_*/gamescope_* client settings.
    "perf-backend"() {
      const runtime = webpack();
      const out = { owners: {} };
      const tokens = [
        "steamos_tdp_limit",
        "steamos_manual_gpu_clock",
        "gamescope_app_target_framerate",
        "gamescope_enable_app_target_framerate",
        "gamescope_disable_framelimit",
        "steamos_platform_performance_profile",
      ];
      for (const id of Object.keys(runtime.m)) {
        let src;
        try {
          src = String(runtime.m[id]);
        } catch {
          continue;
        }
        for (const t of tokens) {
          if (!src.includes(t)) continue;
          (out.owners[t] = out.owners[t] || []).push({ id, len: src.length });
        }
      }
      // Dump the smallest owner of each token, with the surrounding function text.
      out.snippets = {};
      for (const t of tokens) {
        const list = (out.owners[t] || []).slice().sort((a, b) => a.len - b.len);
        if (!list.length) continue;
        const src = String(runtime.m[list[0].id]);
        const i = src.indexOf(t);
        out.snippets[t] = { id: list[0].id, text: src.slice(Math.max(0, i - 700), i + 500) };
      }
      return JSON.stringify(out);
    },

    // Dump the small candidate performance modules so their real shape is visible.
    "perf-dump"() {
      const runtime = webpack();
      const want = ["83571"];
      const out = {};
      for (const id of want) {
        try {
          out[id] = String(runtime.m[id]);
        } catch (e) {
          out[id] = "ERR " + e;
        }
      }
      return JSON.stringify(out);
    },

    // Which export of the performance-component module renders which control, so mounting can
    // select a component by the localization token it draws rather than by a minified export name
    // that rotates on every Steam build. Resolves ONE named module id and reads its exports'
    // SOURCES. It constructs nothing and calls nothing; see the rule in docs\steam-cef.md.
    "perf-exports"() {
      const runtime = webpack();
      const tokens = {
        header: "#QuickAccess_Tab_Perf_PerformanceSettings",
        perGame: "#QuickAccess_Tab_Perf_GameSpecificSettings",
        view: "#Common_Advanced_View",
        reset: "#QuickAccess_Tab_Perf_ResetToDefault",
        frameLimit: "#QuickAccess_Tab_Perf_LimitFrameRate",
        overlayLevel: "#QuickAccess_Tab_Perf_Overlay_Level",
        refreshRate: "#QuickAccess_Tab_Perf_RefreshRate",
        vrr: "#QuickAccess_Tab_Perf_EnableVRR",
      };

      const out = {};
      try {
        const module = runtime("83571");
        out.exportCount = Object.keys(module).length;
        for (const [name, token] of Object.entries(tokens)) {
          const matches = Object.keys(module).filter((key) => {
            const value = module[key];
            return typeof value === "function" && String(value).includes(token);
          });
          // The count is what matters: exactly one means the token identifies a component uniquely
          // and is safe to select by. More than one means the selector needs another discriminator.
          out[name] = { count: matches.length, exports: matches.slice(0, 4) };
        }
      } catch (error) {
        out.error = String(error);
      }

      return JSON.stringify(out);
    },

    // Which client settings gate the perf tab (the legacy frame-limit-only slider), and the TDP
    // component module.
    "perf-limiter"() {
      const runtime = webpack();
      const out = {};
      // Client settings the perf tab consults.
      try {
        const dev = runtime("33867");
        // rV is the non-function export; the store itself is closed over. Reach it via a hook's
        // observable target instead: SteamClient settings snapshot.
        out.settingsKeys = [];
        const seen = new Set();
        for (const id of Object.keys(runtime.m)) {
          let src;
          try {
            src = String(runtime.m[id]);
          } catch {
            continue;
          }
          const re =
            /["']([a-z0-9_]*(?:perf|frame|fps|deck|tdp|refresh|gpu_clock|legacy)[a-z0-9_]*)["']/g;
          let m;
          while ((m = re.exec(src)) !== null) {
            if (!seen.has(m[1]) && m[1].length > 3) {
              seen.add(m[1]);
              out.settingsKeys.push(m[1]);
            }
          }
          if (out.settingsKeys.length > 400) break;
        }
      } catch (e) {
        out.devErr = String(e);
      }
      try {
        out.tdpModule = String(runtime.m["38747"]);
      } catch (e) {
        out.tdpErr = String(e);
      }
      return JSON.stringify(out);
    },

    "perf-live"() {
      const req = webpack();
      const s = String(req.m["74514"]);
      const hImp = s.match(/\bh=r\((\d+)\)/);
      const out = { hModule: hImp ? hImp[1] : null };
      if (hImp) {
        const mod = req(hImp[1]);
        if (mod && typeof mod.l5 === "function") {
          out.l5Source = String(mod.l5).slice(0, 300);
          try {
            out.l5Value = mod.l5();
          } catch (e) {
            out.l5CallErr = String(e);
          }
        } else out.exports = Object.keys(mod).slice(0, 20);
      }
      const holder = Object.values(req("74514")).find((v) => v && typeof v.Get === "function");
      const st = holder.Get();
      out.hasExternalOptions = !!st.msgLimits?.fps_limit_options_external;
      out.hasInternalOptions = !!st.msgLimits?.fps_limit_options;
      return JSON.stringify(out);
    },

    // Does the Windows steamui bundle still carry SteamOS's own per-app performance settings panel,
    // and what gates it? Enumerates module factories and the SteamClient surface.
    "perf-modules"() {
      const out = { ok: true };
      let runtime;
      try {
        runtime = webpack();
      } catch (e) {
        return JSON.stringify({ ok: false, error: String(e) });
      }
      if (!runtime || !runtime.m) return JSON.stringify({ ok: false, error: "no runtime" });

      const ids = Object.keys(runtime.m);
      out.moduleCount = ids.length;

      // Tokens that only the real performance-settings panel would carry.
      const tokens = [
        "per_app_profile",
        "PerAppProfile",
        "UsePerAppProfile",
        "use_per_app_profile",
        "half_rate_shading",
        "HalfRateShading",
        "AllowTearing",
        "allow_tearing",
        "SetPerAppFrameLimit",
        "FrameLimit",
        "perf_overlay_level",
        "PerfOverlayLevel",
        "SetPerAppGPUPerformanceLevel",
        "gpu_performance_level",
        "TDPLimit",
        "tdp_limit",
        "Settings_SteamDeck",
        "PerformanceSettings",
        "PerfSettings",
        "scaling_filter",
        "SetPerAppScalingFilter",
        "composite_debug",
        "is_steam_deck",
        "IsSteamDeck",
        "BIsSteamDeck",
        "SteamDeckDevice",
        "device_supports",
      ];

      const hits = {};
      for (const token of tokens) hits[token] = [];
      for (const id of ids) {
        let source;
        try {
          source = String(runtime.m[id]);
        } catch {
          continue;
        }
        for (const token of tokens) {
          if (source.includes(token) && hits[token].length < 6) {
            hits[token].push({ id, len: source.length });
          }
        }
      }
      out.tokenHits = {};
      for (const token of tokens) {
        out.tokenHits[token] = { count: hits[token].length, modules: hits[token] };
      }

      // What the SteamClient side exposes on Windows.
      const describe = (obj, depth) => {
        if (!obj || typeof obj !== "object" || depth > 1) return typeof obj;
        const r = {};
        for (const k of Object.keys(obj)) {
          const v = obj[k];
          r[k] = typeof v === "function" ? "fn" : depth < 1 ? describe(v, depth + 1) : typeof v;
        }
        return r;
      };
      try {
        out.steamClientKeys = Object.keys(window.SteamClient || {});
        out.systemPerf = describe((window.SteamClient || {}).System, 0);
      } catch (e) {
        out.steamClientError = String(e);
      }

      return JSON.stringify(out);
    },

    // The field names on the nested perf limits/settings messages, which are not top-level exports
    // of the protobuf module, so the shim fills the fields Valve's own controls read. Reads ONE named
    // module's factory as a string and parses the generated field metadata out of it. The earlier
    // version of this probe instantiated every export it could reach and signed the developer's
    // Steam out; see the rule in docs\steam-cef.md.
    "perf-nested-fields"() {
      const runtime = webpack();
      const out = {};
      try {
        const source = String(runtime.m["28013"]);

        // Each generated class carries `fields:{name:{n:tag,...},...}` inside its static metadata
        // and declares its name in getClassName(). Taking the last fields block before the name
        // lands on that class's own metadata.
        const fieldsAt = [];
        for (
          let at = source.indexOf("fields:{");
          at >= 0;
          at = source.indexOf("fields:{", at + 1)
        ) {
          fieldsAt.push(at);
        }

        const names = (start) => {
          const keys = [];
          let depth = 0;
          let token = "";
          for (let i = start + "fields:".length; i < source.length; i++) {
            const ch = source[i];
            if (ch === "{") {
              depth++;
              if (depth === 1) token = "";
              continue;
            }
            if (ch === "}") {
              depth--;
              if (depth === 0) break;
              continue;
            }
            if (depth !== 1) continue;
            if (ch === ":") {
              if (token.trim()) keys.push(token.trim());
              token = "";
            } else if (ch === ",") {
              token = "";
            } else {
              token += ch;
            }
          }
          return keys;
        };

        for (const name of [
          "CMsgSystemPerfLimits",
          "CMsgSystemPerfSettingsGlobal",
          "CMsgSystemPerfSettingsPerApp",
        ]) {
          const declaredAt = source.indexOf('return"' + name + '"');
          if (declaredAt < 0) {
            out[name] = null;
            continue;
          }
          const owning = fieldsAt.filter((at) => at < declaredAt).pop();
          out[name] = owning === undefined ? null : names(owning);
        }
      } catch (error) {
        out.error = String(error);
      }

      return JSON.stringify(out);
    },

    // The protobuf message contract behind the Performance tab: the state message (cI), the
    // settings update request (TR), and the limits/global/per-app field sets. Instantiates messages
    // in JS, sends nothing.
    "perf-proto"() {
      const runtime = webpack();
      const out = {};
      const shape = (ctor) => {
        try {
          const proto = ctor.prototype;
          return Object.getOwnPropertyNames(proto).filter(
            (n) => n !== "constructor" && !n.startsWith("_"),
          );
        } catch (e) {
          return "ERR " + e;
        }
      };
      try {
        const m = runtime("28013");
        out.exports28013 = Object.keys(m);
        for (const k of Object.keys(m)) {
          const v = m[k];
          if (typeof v === "function" && v.prototype) {
            const names = shape(v);
            if (Array.isArray(names) && names.length > 3) out["cls_" + k] = names;
          } else if (typeof v === "object" && v) {
            out["enum_" + k] = Object.keys(v).slice(0, 40);
          }
        }
      } catch (e) {
        out.err28013 = String(e);
      }
      return JSON.stringify(out);
    },

    // The live contents of the perf store on this Windows client: does the backend populate
    // limits/state/global/per-app, and what does the availability gate resolve to?
    "perf-state"() {
      const runtime = webpack();
      const out = {};
      const plain = (msg) => {
        if (!msg) return null;
        try {
          if (typeof msg.toObject === "function") return msg.toObject();
        } catch {}
        try {
          return JSON.parse(JSON.stringify(msg));
        } catch (e) {
          return "unserializable " + String(e);
        }
      };
      try {
        const mod = runtime("74514");
        const store = mod.Hn.Get();
        out.nCurrentGameID = String(store.nCurrentGameID);
        out.nActiveProfileGameID = String(store.nActiveProfileGameID);
        out.nBatteryTemperatureC = store.nBatteryTemperatureC;
        out.msgState = plain(store.msgState);
        out.msgLimits = plain(store.msgLimits);
        out.msgSettingsGlobal = plain(store.msgSettingsGlobal);
        out.msgSettingsPerApp = plain(store.msgSettingsPerApp);
        out.msgDiagnosticInfo = plain(store.msgDiagnosticInfo);
      } catch (e) {
        out.storeError = String(e);
      }
      // The developer-settings hook that owns force_deck_perf_tab.
      try {
        const dev = runtime("33867");
        out.exports33867 = Object.keys(dev);
        for (const k of Object.keys(dev)) {
          const v = dev[k];
          out["fn_" + k] = typeof v === "function" ? String(v).slice(0, 400) : typeof v;
        }
      } catch (e) {
        out.devError = String(e);
      }
      return JSON.stringify(out);
    },

    // The perf-settings store (74514) that every Performance tab control reads and writes, and the
    // settings hook module (33867) that owns force_deck_perf_tab. Dumps sources and inspects the
    // live store, writes nothing.
    "perf-store"() {
      const runtime = webpack();
      const out = {};
      for (const id of ["74514", "33867"]) {
        try {
          out["src_" + id] = String(runtime.m[id]);
        } catch (e) {
          out["src_" + id] = "ERR " + e;
        }
      }
      // Live store instance: 74514 export Hn is the singleton in the minified source.
      try {
        const mod = runtime("74514");
        out.exports74514 = Object.keys(mod);
        const store = mod.Hn && mod.Hn.Get ? mod.Hn.Get() : null;
        if (store) {
          out.storeCtor = store.constructor && store.constructor.name;
          const proto = Object.getPrototypeOf(store);
          out.storeMethods = Object.getOwnPropertyNames(proto).slice(0, 200);
          out.storeFields = Object.keys(store).slice(0, 120);
        } else {
          out.storeMissing = true;
        }
      } catch (e) {
        out.storeError = String(e);
      }
      return JSON.stringify(out);
    },

    // Locate any reusable TDP slider component and see which backend it binds to.
    "perf-tdp"() {
      const runtime = webpack();
      const out = {};
      for (const id of ["90389", "85857"]) {
        let src;
        try {
          src = String(runtime.m[id]);
        } catch (e) {
          continue;
        }
        const hits = [];
        const re = /TDPLimit|tdp_limit|TDP_Limit|Tab_Perf_TDP/g;
        let m;
        while ((m = re.exec(src)) !== null && hits.length < 5) {
          hits.push(src.slice(Math.max(0, m.index - 650), m.index + 400));
        }
        out[id] = hits;
      }
      return JSON.stringify(out);
    },

    // The TDP Limit row itself: which component renders it, what it binds to, and how the SteamOS
    // Manager state is fetched.
    "perf-tdp2"() {
      const runtime = webpack();
      const out = {};
      try {
        const src = String(runtime.m["29788"]);
        out.len = src.length;
        const hits = [];
        const re = /steamos_tdp_limit/g;
        let m;
        while ((m = re.exec(src)) !== null && hits.length < 3) {
          hits.push(src.slice(Math.max(0, m.index - 900), m.index + 600));
        }
        out.tdpRows = hits;
      } catch (e) {
        out.err = String(e);
      }
      // How is the SteamOS manager state fetched? Find the transport behind GetState.
      try {
        const src = String(runtime.m["33706"]);
        const i = src.indexOf("GetState");
        out.stateFetch = src.slice(Math.max(0, i - 900), i + 300);
      } catch (e) {
        out.err2 = String(e);
      }
      return JSON.stringify(out);
    },
  };

  const mutating = {
    "perf-shim"() {
      const store = window.SystemPerfStore;
      const system = window.SteamClient?.System;
      if (!store || !system) return JSON.stringify({ error: "missing store/system" });
      if (system.Perf) return JSON.stringify({ skipped: "Perf already present" });
      const out = {};
      Object.defineProperty(system, "Perf", {
        configurable: true,
        enumerable: true,
        value: {
          UpdateSettings: () => Promise.resolve(),
          RegisterForStateChanges: () => ({ unregister: () => {} }),
          RegisterForDiagnosticInfoChanges: () => ({ unregister: () => {} }),
        },
      });
      store.m_msgState.limits = {
        fps_limit_options: [0, 30, 40, 60, 120],
        tdp_limit_min: 8,
        tdp_limit_max: 37,
        is_vrr_supported: true,
        disable_refresh_rate_management: false,
      };
      store.m_msgState.settings = {
        global: { perf_overlay_level: 2 },
        per_app: {
          fps_limit: 60,
          is_fps_limit_enabled: true,
          is_vrr_enabled: true,
          is_game_perf_profile_enabled: true,
        },
      };
      store.m_msgState.current_game_id = "12345";
      store.m_msgState.active_profile_game_id = "12345";
      // Read back through exactly the accessors the hooks use.
      out.namespacePresent = system.Perf != null;
      out.limits = !!store.msgLimits;
      out.fpsOptions = store.msgLimits.fps_limit_options;
      out.frameLimitAvailable = !store.msgLimits.disable_refresh_rate_management;
      out.vrrSupported = store.msgLimits.is_vrr_supported;
      out.overlayLevel = store.msgSettingsGlobal.perf_overlay_level;
      out.perGameProfileOn = store.msgSettingsPerApp.is_game_perf_profile_enabled;
      out.perGameActive = store.nCurrentGameID === store.nActiveProfileGameID;
      store.m_msgState.limits = undefined;
      store.m_msgState.settings = undefined;
      store.m_msgState.current_game_id = undefined;
      store.m_msgState.active_profile_game_id = undefined;
      delete system.Perf;
      out.restored = !store.msgLimits && system.Perf === undefined;
      return JSON.stringify(out);
    },

    async "tdp-rpc"() {
      const b = window.__steamUi_v1_28d7c54a;
      const out = { gate: null };
      try {
        out.gate = b.steamOsManager.status();
      } catch (e) {
        out.gateErr = String(e);
      }
      // Feed a TDP state and read the merged Manager answer plus the query invalidation.
      b.deliver({
        version: 1,
        contextGeneration: 1,
        documentGeneration: 1,
        type: "state",
        patchId: "wsgm.native-qam.tdp",
        payload: {
          available: true,
          minimumWatts: 8,
          maximumWatts: 30,
          stepWatts: 1,
          desiredWatts: 20,
          observedWatts: 20,
          progress: "idle",
          statusText: "",
        },
      });
      const req = webpack();
      const manager = Object.values(req("90389")).find(
        (v) =>
          v &&
          typeof v === "object" &&
          typeof v.GetState === "function" &&
          typeof v.RefreshScreenReaderAutoLocale === "function",
      );
      const r = await manager.GetState({});
      out.merged = r.Body().toObject().state;
      out.settingsApi = typeof window.SteamClient?.Settings?.RegisterForSettingsChanges;
      out.after = b.steamOsManager.status();
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
