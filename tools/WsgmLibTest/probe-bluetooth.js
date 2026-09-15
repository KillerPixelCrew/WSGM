// Steam Bluetooth store and BluetoothManagerService probes, merged from the former
// probe-bluetooth*.js, probe-bt-*.js and probe-rf-and-bt.js iterations.
//   node run-file.mjs probe-bluetooth.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections (bt-page and bt-service call BluetoothManagerService.GetState; rf-and-bt
// calls the platform rf() predicate):
//   bluetooth     modules calling a Bluetooth backend, and the exports of 66943, 18931 and 25467
//   bluetooth2    the Bluetooth store (57421): backend calls, methods, exports and live singleton
//   bluetooth3    the QAM Bluetooth row module (66943) and modules naming Bluetooth operations
//   bluetooth4    the hook/store module 25467 whole and the device store 46938 head
//   bt-handler    whether *Handler members of 60517.RF are registration points or dispatch stubs
//   bt-page       cached and live BluetoothManagerService state
//   bt-service    the BluetoothManagerService method list and a live GetState
//   rf-and-bt     the rf() platform predicate, TS flags and the 18931 exports
// There are no mutating sections.
(() => {
  const webpack = () => {
    let runtime;
    window.webpackChunksteamui.push([
      ["wsgm_probe_bt_" + Date.now()],
      {},
      (r) => {
        runtime = r;
      },
    ]);
    return runtime;
  };
  const safe = (out, label, f) => {
    try {
      out[label] = f();
    } catch (e) {
      out[label] = "ERR " + String(e).slice(0, 200);
    }
  };

  const readOnly = {
    // Does a Bluetooth store exist with live state on Windows, and what backend do its
    // pair/connect operations call?
    bluetooth() {
      const runtime = webpack();
      const out = {};

      // Which modules mention a bluetooth backend call at all?
      safe(out, "modules_calling_bluetooth", () => {
        const hits = [];
        for (const id of Object.keys(runtime.m)) {
          let src;
          try {
            src = String(runtime.m[id]);
          } catch {
            continue;
          }
          if (/Bluetooth/.test(src) && /SteamClient|\.Get\(\)/.test(src)) {
            const calls = [...new Set(src.match(/SteamClient\.[A-Za-z.]+/g) || [])].filter((c) =>
              /Bluetooth|System/.test(c),
            );
            if (calls.length) hits.push({ id, len: src.length, calls: calls.slice(0, 12) });
          }
        }
        return hits.sort((a, b) => a.len - b.len).slice(0, 8);
      });

      // The store the QAM row uses: 66943 imported u = the bluetooth store module.
      for (const id of ["66943", "18931", "25467"]) {
        safe(out, "exports_" + id, () => {
          const m = runtime(id);
          const r = {};
          for (const k of Object.keys(m)) {
            const v = m[k];
            r[k] = typeof v === "function" ? String(v).slice(0, 150) : typeof v;
          }
          return r;
        });
      }
      return JSON.stringify(out);
    },

    // The Bluetooth store (57421): its operations and the exact backend surface it calls, so we
    // know what supplying it costs.
    bluetooth2() {
      const runtime = webpack();
      const out = {};
      const src = String(runtime.m["57421"]);
      out.len = src.length;

      safe(out, "all_steamclient_calls", () => [
        ...new Set(src.match(/SteamClient\.[A-Za-z0-9_.]+/g) || []),
      ]);
      safe(out, "bluetooth_mentions", () => [
        ...new Set(src.match(/[A-Za-z_]*Bluetooth[A-Za-z_]*/g) || []),
      ]);
      safe(out, "class_methods", () => {
        const m = src.match(/\b(async\s+)?([A-Z][A-Za-z0-9_]*)\s*\(/g) || [];
        return [...new Set(m.map((x) => x.replace(/\s*\($/, "").trim()))].slice(0, 60);
      });
      safe(out, "exports", () => {
        const mod = runtime("57421");
        const r = {};
        for (const k of Object.keys(mod)) {
          const v = mod[k];
          r[k] = typeof v === "function" ? String(v).slice(0, 160) : typeof v;
        }
        return r;
      });
      // Live state, if a singleton is reachable.
      safe(out, "live", () => {
        const mod = runtime("57421");
        for (const k of Object.keys(mod)) {
          const v = mod[k];
          if (typeof v === "function" && typeof v.Get === "function") {
            const s = v.Get();
            const r = { via: k, fields: {} };
            for (const f of Object.keys(s)) {
              const val = s[f];
              r.fields[f] = Array.isArray(val)
                ? "array[" + val.length + "]"
                : val === null || ["boolean", "number", "string"].includes(typeof val)
                  ? val
                  : typeof val;
            }
            return r;
          }
        }
        return "no singleton";
      });
      return JSON.stringify(out);
    },

    // Dump the QAM Bluetooth row module (66943) whole and follow its imports to the real Bluetooth
    // store, then report that store's backend surface.
    bluetooth3() {
      const runtime = webpack();
      const out = {};
      out.row = String(runtime.m["66943"]);

      // Any module whose source declares bluetooth operations by name.
      const opRe =
        /(SetBluetoothEnabled|PairDevice|UnpairDevice|ConnectDevice|DisconnectDevice|ForgetDevice|StartScanning|BluetoothEnabled|bluetooth_)/;
      const owners = [];
      for (const id of Object.keys(runtime.m)) {
        let src;
        try {
          src = String(runtime.m[id]);
        } catch {
          continue;
        }
        if (!opRe.test(src)) continue;
        const ops = [
          ...new Set(
            src.match(
              /(SetBluetoothEnabled|PairDevice|UnpairDevice|ConnectDevice|DisconnectDevice|ForgetDevice|StartScanning\w*|bluetooth_\w+)/g,
            ) || [],
          ),
        ];
        owners.push({ id, len: src.length, ops: ops.slice(0, 14) });
      }
      out.owners = owners.sort((a, b) => a.len - b.len).slice(0, 10);
      return JSON.stringify(out);
    },

    // Dump the Bluetooth hook/store module (25467) whole, which holds PairDevice and the
    // availability gate, plus the device store (46938) head.
    bluetooth4() {
      const runtime = webpack();
      return JSON.stringify({
        m25467: String(runtime.m["25467"]),
        m46938: String(runtime.m["46938"]).slice(0, 4500),
      });
    },

    // Is *Handler a registration point WSGM could implement the service through, or just a
    // client-side dispatch stub? Decides implement-vs-intercept.
    "bt-handler"() {
      const runtime = webpack();
      const RF = runtime("60517").RF;
      const out = {};
      const describe = (name) => {
        const v = RF[name] ?? Object.getPrototypeOf(RF)[name];
        if (typeof v === "function") return "fn: " + String(v).slice(0, 300);
        if (v && typeof v === "object")
          return "obj keys: " + Object.getOwnPropertyNames(v).slice(0, 20).join(",");
        return typeof v;
      };
      for (const n of [
        "GetStateHandler",
        "GetState",
        "SendMsgGetState",
        "PairHandler",
        "Pair",
        "ConnectHandler",
      ]) {
        out[n] = describe(n);
      }
      out.RF_ctor = RF.constructor && RF.constructor.name;
      out.RF_own = Object.getOwnPropertyNames(RF).slice(0, 30);
      return JSON.stringify(out);
    },

    async "bt-page"() {
      const req = webpack();
      const out = {};
      const st = req("21371").L.getQueryState(["BluetoothManagerService", "State"]);
      out.cached = st && st.data;
      const rf = Object.values(req("60517")).find((v) => v && typeof v === "object" && v.GetState);
      const r = await rf.GetState({});
      out.live = r.Body().toObject();
      return JSON.stringify(out);
    },

    // The full BluetoothManagerService method list and the device message shape, so the cost of
    // supplying it is exact.
    "bt-service"() {
      const runtime = webpack();
      const out = {};
      safe(out, "mod60517_exports", () => Object.keys(runtime("60517")));
      safe(out, "RF_methods", () => {
        const RF = runtime("60517").RF;
        if (!RF) return "no RF";
        const names = new Set();
        for (const n of Object.getOwnPropertyNames(RF)) names.add(n);
        const proto = Object.getPrototypeOf(RF);
        if (proto) for (const n of Object.getOwnPropertyNames(proto)) names.add(n);
        return [...names];
      });
      safe(out, "RF_type", () => typeof runtime("60517").RF);
      // Live call: is the service present on Windows at all?
      safe(out, "live_state", async () => {
        const RF = runtime("60517").RF;
        const r = await RF.GetState({});
        return {
          success: r.BSuccess ? r.BSuccess() : "?",
          body: r.Body ? r.Body().toObject() : null,
        };
      });
      return Promise.resolve(out.live_state)
        .then((v) => {
          out.live_state = v;
          return JSON.stringify(out);
        })
        .catch((e) => {
          out.live_state = "ERR " + String(e).slice(0, 200);
          return JSON.stringify(out);
        });
    },

    // What the rf() platform predicate tests (it gates networkManagementAvailable), and whether the
    // Bluetooth store carries live state on Windows the way the network one does.
    "rf-and-bt"() {
      const runtime = webpack();
      const out = {};

      // 72476 is the platform module referenced as E/h in the QAM and network modules.
      safe(out, "platform_exports", () => Object.keys(runtime("72476")));
      safe(out, "rf_source", () => String(runtime("72476").rf).slice(0, 400));
      safe(out, "rf_value", () => runtime("72476").rf());
      safe(out, "TS_flags", () => {
        const ts = runtime("72476").TS;
        const r = {};
        for (const k of Object.keys(ts || {})) r[k] = ts[k];
        return r;
      });
      safe(out, "Xk_source", () => String(runtime("72476").Xk).slice(0, 300));

      // Bluetooth: 18931 held the pairing UI tokens; find its store.
      safe(out, "bt_18931_exports", () => {
        const m = runtime("18931");
        const r = {};
        for (const k of Object.keys(m)) {
          const v = m[k];
          r[k] = typeof v === "function" ? String(v).slice(0, 140) : typeof v;
        }
        return r;
      });
      safe(out, "SteamClient_keys_system", () => Object.keys(window.SteamClient?.System || {}));
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
