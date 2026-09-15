// Steam network management store probes (module 77347), merged from the former
// probe-network-gate.js and probe-network-store.js.
//   node run-file.mjs probe-network.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   network-gate   what computes networkManagementAvailable, and live wireless device and access points
//   network-store  the store's methods, fields, snapshot and exports, and SteamClient.System.Network
// There are no mutating sections.
(() => {
  const webpack = () => {
    let runtime;
    window.webpackChunksteamui.push([
      ["wsgm_probe_net_" + Date.now()],
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
    "network-gate"() {
      const runtime = webpack();
      const out = {};
      const mod = runtime("77347");
      const proto = Object.getPrototypeOf(mod.OQ.Get());

      // Getter sources for the gates.
      for (const name of [
        "networkManagementAvailable",
        "hasWirelessDevice",
        "wirelessNetworkDevice",
        "userVisibleAccessPoints",
        "presentAccessPoints",
        "isWifiEnabled",
      ]) {
        safe(out, "src_" + name, () => {
          const d = Object.getOwnPropertyDescriptor(proto, name);
          if (!d) return "no descriptor";
          return String(d.get || d.value).slice(0, 500);
        });
      }

      const store = mod.OQ.Get();
      safe(out, "live_hasWirelessDevice", () => store.hasWirelessDevice);
      safe(out, "live_isWifiEnabled", () => store.isWifiEnabled);
      safe(out, "live_accessPointCount", () => {
        const a = store.accessPoints;
        return Array.isArray(a) ? a.length : typeof a;
      });
      safe(out, "live_userVisibleCount", () => {
        const a = store.userVisibleAccessPoints;
        return Array.isArray(a) ? a.length : typeof a;
      });
      safe(out, "live_wirelessDevice", () => {
        const d = store.wirelessNetworkDevice;
        if (!d) return null;
        const r = {};
        for (const k of Object.keys(d)) {
          const v = d[k];
          r[k] = v && typeof v === "object" ? typeof v : v;
        }
        return r;
      });
      safe(out, "live_firstAccessPoints", () => {
        const a = store.userVisibleAccessPoints || store.accessPoints;
        if (!Array.isArray(a)) return typeof a;
        return a.slice(0, 5).map((p) => ({
          ssid: p?.strSSID ?? p?.ssid ?? "?",
          strength: p?.nStrength ?? p?.strength,
          sec: p?.eSecurity ?? p?.security,
        }));
      });
      return JSON.stringify(out);
    },

    "network-store"() {
      const runtime = webpack();
      const out = {};
      const mod = runtime("77347");

      // OQ looked like the store singleton in the Wi-Fi row: A.OQ.Get().SetWifiEnabled(e)
      safe(out, "OQ_type", () => typeof mod.OQ);
      safe(out, "store_methods", () => {
        const s = mod.OQ.Get();
        return Object.getOwnPropertyNames(Object.getPrototypeOf(s));
      });
      safe(out, "store_fields", () => Object.keys(mod.OQ.Get()));
      safe(out, "networkManagementAvailable", () => mod.OQ.Get().networkManagementAvailable);

      // Anything on the store that looks like device / access-point state.
      safe(out, "store_snapshot", () => {
        const s = mod.OQ.Get();
        const r = {};
        for (const k of Object.keys(s)) {
          const v = s[k];
          if (v === null || ["boolean", "number", "string"].includes(typeof v)) r[k] = v;
          else if (Array.isArray(v)) r[k] = "array[" + v.length + "]";
          else r[k] = typeof v;
        }
        return r;
      });

      // The exported hooks, so we can see what the settings page reads.
      safe(out, "exports", () => {
        const r = {};
        for (const k of Object.keys(mod)) {
          const v = mod[k];
          r[k] = typeof v === "function" ? String(v).slice(0, 180) : typeof v;
        }
        return r;
      });

      safe(out, "SteamClient_Network", () =>
        Object.keys(window.SteamClient?.System?.Network || {}),
      );
      safe(out, "SteamClient_Network_Device", () =>
        Object.keys(window.SteamClient?.System?.Network?.Device || {}),
      );
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
