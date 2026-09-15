// Steam audio store probes (module 1409 and SteamClient.System.Audio), merged from the former
// probe-audio*.js and probe-speaker-config.js iterations.
//   node run-file.mjs probe-audio.js --section <name>
// Without --section the script only lists its sections.
//
// Read-only sections:
//   audio           SteamClient.System.Audio call sites, the availability flag and the store exports
//   audio-active    the OnAudioDeviceVolumeChanged definition
//   audio-shape     the GetDevices response and device, volume and app handler shapes
//   audio-volume    volume fields and the device update handlers
//   speaker-config  SetSpeakerConfiguration sites and AudioManager service stubs
// Mutating sections, attended only:
//   audio-gate      forces the store available, registers a fake device, then restores both
//   audio-install   defines a stand-in SteamClient.System.Audio, builds a store, then deletes it
(() => {
  const webpack = () => {
    let runtime;
    window.webpackChunksteamui.push([
      ["wsgm_probe_audio_" + Date.now()],
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
    audio() {
      const runtime = webpack();
      const out = {};
      const src = String(runtime.m["1409"]);
      out.len = src.length;

      // Every SteamClient.System.Audio call site with a little context, so the expected
      // signatures and payload shapes are visible.
      safe(out, "audio_call_sites", () => {
        const hits = [];
        const re = /SteamClient\.System\.Audio\.[A-Za-z]+/g;
        let m;
        while ((m = re.exec(src)) !== null && hits.length < 14) {
          hits.push(src.slice(Math.max(0, m.index - 220), m.index + 260));
        }
        return hits;
      });

      // The store's availability flag and device shape.
      safe(out, "bAvailable_context", () => {
        const i = src.indexOf("bAvailable");
        return src.slice(Math.max(0, i - 700), i + 500);
      });

      safe(out, "exports_1409", () => {
        const mod = runtime("1409");
        const r = {};
        for (const k of Object.keys(mod)) {
          const v = mod[k];
          r[k] = typeof v === "function" ? String(v).slice(0, 130) : typeof v;
        }
        return r;
      });

      safe(out, "SteamClient_System_Audio", () => typeof window.SteamClient?.System?.Audio);
      return JSON.stringify(out);
    },

    "audio-active"() {
      const req = webpack();
      const s = String(req.m["1409"]);
      // Find the DEFINITION, not the registration: look for the arrow/method body.
      const defAt = s.indexOf(
        "OnAudioDeviceVolumeChanged",
        s.indexOf("OnAudioDeviceVolumeChanged") + 10,
      );
      return JSON.stringify({ def: s.slice(Math.max(0, defAt - 30), defAt + 280) });
    },

    "audio-shape"() {
      const runtime = webpack();
      const src = String(runtime.m["1409"]);
      const out = {};
      const grab = (label, needle, before, after) => {
        const i = src.indexOf(needle);
        out[label] = i < 0 ? "NOT FOUND" : src.slice(Math.max(0, i - before), i + after);
      };

      // The GetDevices consumer, which names every field of the response.
      grab("getDevices", "GetDevices()", 60, 1400);
      // The device-added handler, which names the device object's fields.
      grab("onDeviceAdded", "OnAudioDeviceAdded", 40, 900);
      // The volume-changed handler.
      grab("onVolumeChanged", "OnAudioDeviceVolumeChanged", 40, 600);
      // The app-added handler, for the per-app mixer shape.
      grab("onAppAdded", "OnAudioAppAdded", 40, 700);
      return JSON.stringify(out);
    },

    "audio-volume"() {
      const req = webpack();
      const s = String(req.m["1409"]);
      const out = [];
      for (const name of [
        "flOutputVolume",
        "flInputVolume",
        "RegisterOrUpdateDevice",
        "OnVolumeUpdated",
      ]) {
        const at = s.indexOf(name);
        out.push({ name, slice: at < 0 ? null : s.slice(Math.max(0, at - 260), at + 200) });
      }
      return JSON.stringify(out);
    },

    "speaker-config"() {
      const runtime = webpack();
      const out = {};
      const src = String(runtime.m["1409"]);

      // The import that provides SetSpeakerConfiguration, and the enum of configurations.
      safe(out, "import_header", () => src.slice(0, 900));
      safe(out, "speaker_sites", () => {
        const hits = [];
        const re = /SetSpeakerConfiguration|SpeakerConfig|eConfig|speaker/gi;
        let m;
        const seen = new Set();
        while ((m = re.exec(src)) !== null && hits.length < 8) {
          const key = Math.floor(m.index / 400);
          if (seen.has(key)) continue;
          seen.add(key);
          hits.push(src.slice(Math.max(0, m.index - 300), m.index + 400));
        }
        return hits;
      });

      // Any module owning an AudioManagerService-style RPC stub.
      safe(out, "audio_services", () => {
        const found = [];
        for (const id of Object.keys(runtime.m)) {
          let s;
          try {
            s = String(runtime.m[id]);
          } catch {
            continue;
          }
          if (/SetSpeakerConfiguration|AudioManager\./.test(s)) {
            const names = [...new Set(s.match(/"[A-Za-z]+Audio[A-Za-z]*\.[A-Za-z]+#\d"/g) || [])];
            found.push({ id, len: s.length, msgs: names.slice(0, 20) });
          }
        }
        return found.sort((a, b) => a.len - b.len).slice(0, 6);
      });
      return JSON.stringify(out);
    },
  };

  const mutating = {
    "audio-gate"() {
      const req = webpack();
      const platform = req("72476");
      const store = req("1409").F5;
      const out = {
        ON_FRAME: platform?.TS?.ON_FRAME,
        IS_STEAMOS: platform?.TS?.IS_STEAMOS,
        IN_GAMESCOPE: platform?.TS?.IN_GAMESCOPE,
        bAvailableBefore: store?.bAvailable,
      };
      const dev = (id, name, o, i) => ({
        id,
        sName: name,
        bHasOutput: o,
        bHasInput: i,
        currentConfig: {},
        availableConfigs: [],
        eConnectorType: 0,
        eBus: 0,
        bSupportsHdmiCec: false,
        bHdmiCecEnabled: false,
        bHdmiCecActive: false,
      });
      try {
        store.m_bAvailable = true;
        store.RegisterOrUpdateDevice(dev(9101, "WSGM Gate Probe", true, false));
        out.bAvailableAfter = store.bAvailable;
        // The Quick Settings audio section renders when !IN_VR && bAvailable (non-VR desktop client).
        out.audioSectionGateWouldOpen =
          out.bAvailableAfter === true && platform?.TS?.ON_FRAME !== true;
      } catch (e) {
        out.error = String(e).slice(0, 200);
      }
      try {
        store.m_mapAudioDevices.delete(9101);
        store.m_bAvailable = false;
        out.restored = store.bAvailable === false && store.m_mapAudioDevices.size === 0;
      } catch (e) {
        out.restoreError = String(e).slice(0, 200);
      }
      return JSON.stringify(out);
    },

    // Does the audio namespace the bootstrap defines satisfy Steam's own audio store? Installs a
    // stand-in with the same shape, checks the store's availability flag and the device projection,
    // then removes it and confirms the client is left as found.
    "audio-install"() {
      const out = {};
      const system = window.SteamClient?.System;
      if (!system) return JSON.stringify({ error: "no SteamClient.System" });

      out.audioAbsentBefore = system.Audio === undefined;
      if (!out.audioAbsentBefore)
        return JSON.stringify({ ...out, skipped: "Audio already exists" });

      const toDevice = (entry) => ({
        id: entry.id,
        sName: entry.name,
        bHasOutput: entry.hasOutput === true,
        bHasInput: entry.hasInput === true,
        currentConfig: {},
        availableConfigs: [],
        eConnectorType: 0,
        eBus: 0,
        bSupportsHdmiCec: false,
        bHdmiCecEnabled: false,
        bHdmiCecActive: false,
      });
      const fake = [
        { id: 1, name: "Speakers", hasOutput: true, hasInput: false },
        { id: 2, name: "Headset", hasOutput: true, hasInput: true },
      ];
      const register = () => (cb) => ({ unregister: () => {} });

      Object.defineProperty(system, "Audio", {
        value: {
          GetDevices: () =>
            Promise.resolve({
              activeOutputDeviceId: 1,
              activeInputDeviceId: 2,
              overrideOutputDeviceId: "",
              overrideInputDeviceId: "",
              vecDevices: fake.map(toDevice),
            }),
          GetApps: () => Promise.resolve({ rgApps: [] }),
          SetDefaultDeviceOverride: () => Promise.resolve(),
          SetDeviceVolume: () => Promise.resolve(),
          SetAppVolume: () => Promise.resolve(),
          ClearDefaultDeviceOverride: () => Promise.resolve(),
          RegisterForServiceConnectionStateChanges: register(),
          RegisterForDeviceAdded: register(),
          RegisterForDeviceRemoved: register(),
          RegisterForDeviceVolumeChanged: register(),
          RegisterForVolumeButtonPressed: register(),
          RegisterForAppAdded: register(),
          RegisterForAppRemoved: register(),
          RegisterForAppVolumeChanged: register(),
        },
        configurable: true,
        enumerable: true,
        writable: false,
      });

      out.audioPresentAfter = system.Audio !== undefined;
      out.availabilityFlagWouldBeTrue = null != system.Audio;

      // Build a fresh store instance so its constructor runs against the namespace, exactly as it
      // would at client start with the bootstrap installed.
      const runtime = webpack();

      return new Promise((resolve) => {
        try {
          const mod = runtime("1409");
          const ctor = Object.values(mod).find(
            (v) => typeof v === "function" && /m_bAvailable/.test(String(v)),
          );
          out.storeConstructorFound = !!ctor;
          if (ctor) {
            const store = new ctor();
            out.storeReportsAvailable = store.m_bAvailable === true;
            setTimeout(() => {
              try {
                out.deviceCount = store.m_mapAudioDevices?.size ?? "n/a";
                out.activeOutput = store.m_activeOutputDeviceId;
                out.activeInput = store.m_activeInputDeviceId;
              } catch (e) {
                out.readError = String(e).slice(0, 200);
              }
              finish(resolve);
            }, 400);
            return;
          }
        } catch (e) {
          out.error = String(e).slice(0, 300);
        }
        finish(resolve);
      });

      function finish(resolve) {
        try {
          delete system.Audio;
        } catch (e) {
          out.removeError = String(e).slice(0, 200);
        }
        out.audioAbsentAfterRemoval = system.Audio === undefined;
        resolve(JSON.stringify(out));
      }
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
