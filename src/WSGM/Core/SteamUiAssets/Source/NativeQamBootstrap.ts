type BridgeConfiguration = Readonly<{
  version: number;
  namespace: string;
  binding: string;
  assetHash: string;
  contextGeneration: number;
  documentGeneration: number;
  maximumPending: number;
  timeoutMilliseconds: number;
  allowed: Readonly<Record<string, readonly string[]>>;
}>;

declare const __WSGM_CONFIGURATION_JSON__: BridgeConfiguration;

// This file is a script, not a module, so the interface merges with the global
// Window directly; a `declare global` block would need a module context.
interface Window {
  // Steam's own webpack chunk registry. Untyped by Steam, and the only route to
  // the module runtime the native components are installed into.
  webpackChunksteamui: unknown[];
  [key: string]: any;
}

// @wsgm-bundle-start
(() => {
  "use strict";
  const config: BridgeConfiguration = __WSGM_CONFIGURATION_JSON__;
  const prior = window[config.namespace];
  if (
    prior &&
    prior.version === config.version &&
    // Neither generation changes when WSGM is updated, so without the asset hash a new build kept
    // running the previous build's script until Steam itself restarted.
    prior.assetHash === config.assetHash &&
    prior.contextGeneration === config.contextGeneration &&
    prior.documentGeneration === config.documentGeneration &&
    prior.nativeComponents &&
    typeof prior.nativeComponents.install === "function"
  ) {
    return JSON.stringify({ ok: true, reused: true, version: prior.version });
  }
  if (prior && typeof prior.dispose === "function") prior.dispose("generation replaced");

  const pending = new Map();
  const subscribers = new Map();
  const latestStates = new Map();
  const nativeComponents = createNativeComponentHost();
  let nextSequence = 0;
  let disposed = false;

  const allowed = (patchId, command) => {
    const commands = config.allowed[patchId];
    return Array.isArray(commands) && commands.includes(command);
  };
  const send = (envelope) => {
    if (disposed) throw new Error("WSGM bridge disposed");
    const binding = window[config.binding];
    if (typeof binding !== "function") throw new Error("WSGM Runtime binding unavailable");
    binding(JSON.stringify(envelope));
  };
  const request = (patchId, command, payload, actionGeneration) => {
    if (!allowed(patchId, command)) return Promise.reject(new Error("command not allowlisted"));
    if (pending.size >= config.maximumPending) return Promise.reject(new Error("bridge busy"));
    const sequence = ++nextSequence;
    const envelope = {
      version: config.version,
      type: "request",
      patchId,
      command,
      sequence,
      actionGeneration,
      contextGeneration: config.contextGeneration,
      documentGeneration: config.documentGeneration,
      payload: payload ?? null,
    };
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(sequence);
        try {
          send({ ...envelope, type: "cancel" });
        } catch {}
        reject(new Error("WSGM bridge request timed out"));
      }, config.timeoutMilliseconds);
      pending.set(sequence, { resolve, reject, timer, patchId, command });
      try {
        send(envelope);
      } catch (error) {
        clearTimeout(timer);
        pending.delete(sequence);
        reject(error);
      }
    });
  };
  const subscribe = (patchId, callback) => {
    if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
      throw new Error("subscription not allowlisted");
    let set = subscribers.get(patchId);
    if (!set) subscribers.set(patchId, (set = new Set()));
    set.add(callback);
    if (latestStates.has(patchId)) callback(latestStates.get(patchId));
    return () => set.delete(callback);
  };
  const deliver = (envelope) => {
    if (
      !envelope ||
      envelope.version !== config.version ||
      envelope.contextGeneration !== config.contextGeneration ||
      envelope.documentGeneration !== config.documentGeneration
    )
      return false;
    if (envelope.type === "response") {
      const item = pending.get(envelope.sequence);
      if (!item || item.patchId !== envelope.patchId || item.command !== envelope.command)
        return false;
      clearTimeout(item.timer);
      pending.delete(envelope.sequence);
      if (envelope.ok) item.resolve(envelope.payload);
      else item.reject(new Error(String(envelope.error || "command rejected")));
      return true;
    }
    if (envelope.type === "state") {
      if (!Object.hasOwn(config.allowed, envelope.patchId)) return false;
      latestStates.set(envelope.patchId, envelope.payload);
      const set = subscribers.get(envelope.patchId);
      if (!set) return true;
      for (const callback of [...set]) {
        try {
          callback(envelope.payload);
        } catch {}
      }
      return true;
    }
    return false;
  };
  const dispose = (reason) => {
    if (disposed) return;
    disposed = true;
    nativeComponents.dispose();
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "WSGM bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
    latestStates.clear();
  };

  const audioNamespace = createAudioNamespace();
  const networkGate = createNetworkGate();
  const bluetoothService = createBluetoothService();
  const brightnessGate = createBrightnessGate();
  const perfNamespace = createPerfNamespace();
  const bridge = Object.freeze({
    version: config.version,
    assetHash: config.assetHash,
    contextGeneration: config.contextGeneration,
    documentGeneration: config.documentGeneration,
    request,
    subscribe,
    deliver,
    dispose,
    nativeComponents: Object.freeze({
      install: nativeComponents.install,
      remove: nativeComponents.remove,
      status: nativeComponents.status,
    }),
    audio: Object.freeze({
      install: audioNamespace.install,
      remove: audioNamespace.remove,
      status: audioNamespace.status,
    }),
    network: Object.freeze({
      install: networkGate.install,
      remove: networkGate.remove,
      status: networkGate.status,
    }),
    bluetooth: Object.freeze({
      install: bluetoothService.install,
      remove: bluetoothService.remove,
      status: bluetoothService.status,
    }),
    brightness: Object.freeze({
      install: brightnessGate.install,
      remove: brightnessGate.remove,
      status: brightnessGate.status,
    }),
    perf: Object.freeze({
      install: perfNamespace.install,
      remove: perfNamespace.remove,
      status: perfNamespace.status,
    }),
  });
  Object.defineProperty(window, config.namespace, {
    value: bridge,
    configurable: true,
    enumerable: false,
    writable: false,
  });
  return JSON.stringify({ ok: true, reused: false, version: config.version });

  // The performance surface is the largest absent backend: SystemPerfStore's constructor
  // optional-chains through a SteamClient.System.Perf that does not exist on Windows, so its state
  // stays empty and every control renders null. Availability for each control is read out of that
  // same state, which is why supplying it also decides what appears — omit a limits field and
  // Valve's own wrapper renders nothing.
  //
  // State is written into m_msgState directly rather than pushed through OnStateChanged, which
  // would mean building a CMsgSystemPerfState protobuf in injected JavaScript to have the store
  // immediately decode it again. Live-verified 2026-08-30 that the direct write is observed through
  // every accessor the hooks use and restores cleanly.
  function createPerfNamespace() {
    const patchId = "wsgm.native-qam.perf";
    let installed = false;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;

    const store = () => window.SystemPerfStore ?? null;

    const onState = (state) => {
      if (!installed || !state) return;
      const target = store();
      if (!target || !target.m_msgState) return;
      try {
        target.m_msgState.limits = state.limits ?? {};
        target.m_msgState.settings = {
          global: state.global ?? {},
          per_app: state.perApp ?? {},
        };
        // Steam identifies the per-game profile by comparing these two: equal means the running
        // game's own profile is the one being edited.
        target.m_msgState.current_game_id = state.currentGameId ?? "0";
        target.m_msgState.active_profile_game_id = state.activeProfileGameId ?? "0";
      } catch (error) {
        lastError = String(error);
      }
    };

    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const system = window.SteamClient?.System;
      if (!system) {
        lastError = "SteamClient.System unavailable";
        return { ok: false, error: lastError };
      }

      if (system.Perf) {
        lastError = "SteamClient.System.Perf already exists";
        return { ok: false, error: lastError };
      }

      if (!store()) {
        lastError = "SystemPerfStore unavailable";
        return { ok: false, error: lastError };
      }

      // Every setter builds a protobuf delta and hands it to UpdateSettings, so that one method is
      // where all of them arrive. The delta is decoded on WSGM's side rather than here, because the
      // message shapes belong to the client and this half only forwards.
      const api = {
        // toObject() first, always. The argument is a protobuf message whose own JSON form is
        // jspb's internal positional array, so forwarding it verbatim would send tag-indexed
        // nesting with no field names and WSGM would have to reimplement the wire format to read
        // its own client's message back.
        UpdateSettings: (payload) =>
          request(patchId, "updateSettings", { delta: payload?.toObject?.() ?? payload ?? {} }, 0),
        RegisterForStateChanges: () => ({ unregister: () => {} }),
        RegisterForDiagnosticInfoChanges: () => ({ unregister: () => {} }),
      };

      try {
        Object.defineProperty(system, "Perf", {
          value: api,
          configurable: true,
          enumerable: true,
          writable: false,
        });
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true };
    };

    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }

      const target = store();
      if (target?.m_msgState) {
        try {
          // Back to the empty state the Windows client leaves it in, so every control returns to
          // rendering nothing rather than keeping WSGM's last answer.
          target.m_msgState.limits = undefined;
          target.m_msgState.settings = undefined;
          target.m_msgState.current_game_id = undefined;
          target.m_msgState.active_profile_game_id = undefined;
        } catch (error) {
          lastError = String(error);
        }
      }

      try {
        delete window.SteamClient.System.Perf;
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      return { ok: true, removed: true };
    };

    const status = () => {
      const target = store();
      return {
        ok: true,
        installed,
        namespacePresent: !!window.SteamClient?.System?.Perf,
        limitsPresent: !!target?.msgLimits,
        // Which controls can draw at all, since each reads its own availability out of limits.
        frameLimitOptions: target?.msgLimits?.fps_limit_options?.length ?? 0,
        vrrSupported: target?.msgLimits?.is_vrr_supported === true,
        lastError,
      };
    };

    return { install, remove, status };
  }

  // Brightness is one flag away, not a transport away. Steam already tracks the real panel
  // brightness on Windows and both SetBrightness and RegisterForBrightnessChanges exist; the system
  // settings message simply reports is_display_brightness_available as false, and the hook reads
  // `?? true` which never applies to an explicit false. Live-verified 2026-08-30: the flag is
  // writable, flips the answer, and restores.
  //
  // Nothing else is touched. This does not supply a backend, because there already is one.
  function createBrightnessGate() {
    const field = "is_display_brightness_available";
    let originalValue: unknown;
    let installed = false;
    let lastError = "";

    const settings = () => {
      try {
        let req;
        window.webpackChunksteamui.push([
          ["wsgm_brightness_" + Date.now()],
          {},
          (r) => {
            req = r;
          },
        ]);
        return req?.("59547")?.mG?.Get?.()?.m_msgSettings ?? null;
      } catch {
        return null;
      }
    };

    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const message = settings();
      if (!message || !(field in message)) {
        lastError = "display settings message unavailable";
        return { ok: false, error: lastError };
      }

      // A client already reporting brightness available needs nothing from WSGM, and overwriting
      // the flag would mean restoring a value that was never ours to change.
      if (message[field] === true) {
        lastError = "brightness already available";
        return { ok: false, error: lastError };
      }

      try {
        originalValue = message[field];
        message[field] = true;
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      installed = true;
      lastError = "";
      return { ok: true, installed: true, available: message[field] === true };
    };

    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const message = settings();
      installed = false;
      if (!message) return { ok: true, removed: true, storeGone: true };
      try {
        message[field] = originalValue;
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      return { ok: true, removed: true };
    };

    const status = () => {
      const message = settings();
      return {
        ok: true,
        installed,
        available: message ? message[field] === true : false,
        lastError,
      };
    };

    return { install, remove, status };
  }

  // Bluetooth is a WebUI transport service whose backend does not exist on Windows. The service,
  // its message shapes and every operation are present — GetState round-trips and answers
  // is_service_available:false with empty adapters and devices — so WSGM replaces the stub's
  // methods rather than implementing the service. `*Handler` exports are message descriptors,
  // not registration hooks, so implementing it is not on offer.
  //
  // The second gate matters here as much as the first: availability is read through react-query
  // with staleTime Infinity, so replacing the methods changes nothing until that cache is
  // invalidated. Live-verified 2026-08-30 that RF's methods are writable and configurable and that
  // the query client's invalidateQueries is reachable.
  function createBluetoothService() {
    const patchId = "wsgm.steam-bluetooth.service";
    const queryKey = ["BluetoothManagerService", "State"];
    const originals = new Map<string, unknown>();
    let installed = false;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;
    // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
    // and WSGM only carries them through from the state it was given.
    let latest: {
      is_service_available: boolean;
      adapters: any[];
      devices: any[];
    } = { is_service_available: false, adapters: [], devices: [] };

    const modules = () => {
      let req;
      window.webpackChunksteamui.push([
        ["wsgm_bluetooth_" + Date.now()],
        {},
        (r) => {
          req = r;
        },
      ]);
      return req;
    };

    // Steam reads a transport reply, never a bare value: BSuccess decides whether the caller
    // proceeds at all, and Body().toObject() is what the store consumes.
    const reply = (body) => ({
      BSuccess: () => true,
      BFailed: () => false,
      GetEResult: () => 1,
      Body: () => ({ ...body, toObject: () => body }),
    });

    const invalidate = (req) => {
      try {
        req?.("21371")?.L?.invalidateQueries({ queryKey });
      } catch {
        // A client whose query layer moved keeps the stale answer; the row simply does not update.
      }
    };

    // WSGM sends its own field names and the mapping into Steam's lives here, so the client's
    // schema stays in the half that has to change when the client is rebuilt.
    const onState = (state) => {
      if (!installed || !state) return;
      const devices = Array.isArray(state.devices) ? state.devices : [];
      latest = {
        is_service_available: state.available === true,
        // One synthetic adapter, because the panel needs something to hang the radio toggle on and
        // Windows exposes no adapter identity WSGM could pass through truthfully.
        adapters:
          state.available === true
            ? [
                {
                  id: 1,
                  mac: "",
                  name: "Bluetooth",
                  is_enabled: state.enabled === true,
                  is_discovering: state.discovering === true,
                },
              ]
            : [],
        devices: devices.map((device) => ({
          id: device.id,
          mac: device.mac ?? "",
          name: device.name ?? device.id,
          etype: device.eType ?? 0,
          is_paired: device.isPaired === true,
          is_connected: device.isConnected === true,
          // Steam sorts by signal and shows a battery when one is reported. WSGM knows neither, and
          // a fabricated strength would order the list by a number that means nothing.
          strength_raw: 0,
          battery_percent: null,
          should_hide_hint: false,
        })),
      };
      invalidate(modules());
    };

    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const req = modules();
      const RF = req?.("60517")?.RF;
      if (!RF || typeof RF.GetState !== "function") {
        lastError = "BluetoothManagerService stub unavailable";
        return { ok: false, error: lastError };
      }

      const forward = (command) => (payload) =>
        request(patchId, command, payload ?? null, 0).then(
          () => reply({ success: true }),
          () => reply({ success: false }),
        );
      const replace = (name, replacement) => {
        originals.set(name, RF[name]);
        RF[name] = replacement;
      };

      try {
        replace("GetState", () => Promise.resolve(reply(latest)));
        replace("GetDeviceDetails", (payload) => {
          const id = payload?.id;
          const device = latest.devices.find((entry) => entry.id === id) ?? null;
          return Promise.resolve(reply({ device }));
        });
        replace("GetAdapterDetails", () =>
          Promise.resolve(reply({ adapter: latest.adapters[0] ?? null })),
        );
        replace("SetDiscovering", forward("setDiscovering"));
        replace("Pair", forward("pair"));
        replace("CancelPair", forward("cancelPair"));
        replace("Connect", forward("connect"));
        replace("Disconnect", forward("disconnect"));
        replace("Forget", forward("forget"));
        replace("SetTrusted", forward("setTrusted"));
        replace("SetWakeAllowed", forward("setWakeAllowed"));
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      invalidate(req);
      return { ok: true, installed: true, replaced: originals.size };
    };

    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }

      const req = modules();
      const RF = req?.("60517")?.RF;
      if (RF) {
        for (const [name, original] of originals) RF[name] = original;
      }

      originals.clear();
      latest = { is_service_available: false, adapters: [], devices: [] };
      invalidate(req);
      return { ok: true, removed: true };
    };

    const status = () => ({
      ok: true,
      installed,
      replaced: originals.size,
      available: latest.is_service_available,
      devices: latest.devices.length,
      lastError,
    });

    return { install, remove, status };
  }

  // Wi-Fi is hidden by one getter, not by an absent backend. Steam's Windows client genuinely
  // tracks the wireless device — hasWirelessDevice and isWifiEnabled are true here without any
  // help — and only `get networkManagementAvailable(){return TS.IS_STEAMOS}` keeps the UI away.
  //
  // Overriding that one property is narrow and reversible and affects one surface. Setting the
  // constant it reads would produce the same row while changing unrelated client behaviour
  // everywhere, which is the spoof D16 forbids. Live-verified 2026-08-30: the descriptor is
  // configurable, the override flips the value, and restoring the saved descriptor puts it back.
  function createNetworkGate() {
    const property = "networkManagementAvailable";
    const patchId = "wsgm.steam-network.gate";
    let original: PropertyDescriptor | undefined;
    let target: object | null = null;
    let lastError = "";
    let scanWrapped = false;
    let originalStart: ((...args: unknown[]) => unknown) | null = null;
    let originalStop: ((...args: unknown[]) => unknown) | null = null;

    const store = () => {
      try {
        let req;
        window.webpackChunksteamui.push([
          ["wsgm_network_store_" + Date.now()],
          {},
          (r) => {
            req = r;
          },
        ]);
        return req?.("77347")?.OQ?.Get() ?? null;
      } catch {
        return null;
      }
    };

    const install = () => {
      if (target) return { ok: true, alreadyInstalled: true };
      const instance = store();
      if (!instance) {
        lastError = "network store unavailable";
        return { ok: false, error: lastError };
      }

      // The getter lives on the prototype, so that is what is replaced and restored. Defining it
      // on the instance would shadow rather than replace, and removal would leave the shadow.
      const proto = Object.getPrototypeOf(instance);
      const descriptor = Object.getOwnPropertyDescriptor(proto, property);
      if (!descriptor || descriptor.configurable !== true) {
        lastError = "network availability getter is not configurable";
        return { ok: false, error: lastError };
      }

      try {
        Object.defineProperty(proto, property, { get: () => true, configurable: true });
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      original = descriptor;
      target = proto;
      lastError = "";
      wrapScanning();
      return { ok: true, installed: true, available: instance[property] === true };
    };

    // Steam's own UI calls these when its network page opens and closes, so they are exactly the
    // signal for when a scan is worth running. WSGM's radio manager is otherwise driven by WSGM's
    // own panel, and a list refreshed only then would be stale on Steam's page — which is worse
    // than an empty one, because the user picks a network that is gone and the join fails silently.
    //
    // Both originals are always called through: this observes the lifetime, it does not take it
    // over, so a client that grows a working backend keeps behaving exactly as before.
    const wrapScanning = () => {
      const net = window.SteamClient?.System?.Network;
      if (!net || scanWrapped) return;
      const wrap = (name: string, command: string) => {
        const inner = net[name];
        if (typeof inner !== "function") return null;
        net[name] = function (...args) {
          try {
            request(patchId, command, null, 0);
          } catch {
            // A scan request that cannot reach WSGM must not stop Steam's own call.
          }

          return inner.apply(this, args);
        };
        return inner;
      };

      originalStart = wrap("StartScanningForNetworks", "startScan");
      originalStop = wrap("StopScanningForNetworks", "stopScan");
      scanWrapped = !!(originalStart || originalStop);
    };

    const unwrapScanning = () => {
      const net = window.SteamClient?.System?.Network;
      if (!net || !scanWrapped) return;
      if (originalStart) net.StartScanningForNetworks = originalStart;
      if (originalStop) net.StopScanningForNetworks = originalStop;
      originalStart = null;
      originalStop = null;
      scanWrapped = false;
    };

    const remove = () => {
      unwrapScanning();
      if (!target || !original) return { ok: true, absent: true };
      try {
        Object.defineProperty(target, property, original);
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      target = null;
      original = undefined;
      return { ok: true, removed: true };
    };

    const status = () => {
      const instance = store();
      return {
        ok: true,
        installed: !!target,
        available: instance ? instance[property] === true : false,
        // Reported because the row can be on while the list is empty: Steam's Windows backend
        // never populates wireless.aps, so an access point count of zero here means WSGM has not
        // supplied one, not that the machine cannot see any networks.
        accessPoints: Array.isArray(instance?.accessPoints) ? instance.accessPoints.length : -1,
        hasWirelessDevice: instance?.hasWirelessDevice === true,
        scanWrapped,
        lastError,
      };
    };

    return { install, remove, status };
  }

  // Audio is supplied as the namespace Steam's own store looks for, rather than drawn as a row.
  // The store's availability flag is literally `null != SteamClient.System.Audio`, so defining this
  // object is the entire gate — there is nothing to patch and nothing to hide.
  function createAudioNamespace() {
    const patchId = "wsgm.native-qam.audio";
    let installed = false;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;

    // Every registration Steam makes at construction. Held here so a state push can reach them and
    // so removal drops them all rather than leaving callbacks pointed at a torn-down bridge.
    const callbacks = {
      serviceConnection: null as ((connected: boolean) => void) | null,
      deviceAdded: null as ((device: unknown) => void) | null,
      deviceRemoved: null as ((id: number) => void) | null,
      deviceVolumeChanged: null as ((id: number, direction: number, volume: number) => void) | null,
      volumeButtonPressed: null as ((pressed: unknown) => void) | null,
      appAdded: null as ((app: unknown) => void) | null,
      appRemoved: null as ((id: number) => void) | null,
      appVolumeChanged: null as ((id: number, volume: number) => void) | null,
    };
    const register = (slot: keyof typeof callbacks) => (callback) => {
      callbacks[slot] = typeof callback === "function" ? callback : null;
      // Steam expects an unregister handle from every RegisterFor* call and stores it.
      return { unregister: () => (callbacks[slot] = null) };
    };

    let known: string[] = [];
    const toDevice = (entry) => ({
      id: entry.id,
      sName: entry.name,
      bHasOutput: entry.hasOutput === true,
      bHasInput: entry.hasInput === true,
      // Speaker configuration and HDMI CEC reach a service WSGM does not supply. Reported empty and
      // false rather than invented, so those controls simply do not appear.
      currentConfig: {},
      availableConfigs: [],
      eConnectorType: 0,
      eBus: 0,
      bSupportsHdmiCec: false,
      bHdmiCecEnabled: false,
      bHdmiCecActive: false,
    });

    // The store that is already running. Defining the namespace is not enough on a live client:
    // `m_bAvailable` is computed once in the constructor, which ran at client start when
    // SteamClient.System.Audio did not exist, so the audio section would stay hidden forever.
    // Live-verified 2026-08-30: the flag is writable and RegisterOrUpdateDevice is the store's own
    // ingestion path, exactly as SteamNetworkIndicator already does for the network store.
    const liveStore = () => {
      try {
        let req;
        window.webpackChunksteamui.push([
          ["wsgm_audio_store_" + Date.now()],
          {},
          (r) => {
            req = r;
          },
        ]);
        const store = req?.("1409")?.F5;
        return store && "m_bAvailable" in store ? store : null;
      } catch {
        return null;
      }
    };

    const onState = (state) => {
      if (!installed || !state || !Array.isArray(state.devices)) return;
      const seen = state.devices.map((device) => String(device.id));

      // Removals first: a device that has gone must leave the store before a re-read of the device
      // list can describe the set as complete, or the picker keeps an endpoint that is not there.
      for (const id of known) {
        if (!seen.includes(id) && callbacks.deviceRemoved) callbacks.deviceRemoved(id as never);
      }
      for (const device of state.devices) {
        if (callbacks.deviceAdded) callbacks.deviceAdded(toDevice(device));
      }
      known = seen;

      // The registrations above only reach a store constructed after the namespace existed. The
      // running one has to be fed through its own path, and told it is available at all.
      const store = liveStore();
      if (!store) return;
      try {
        store.m_bAvailable = true;
        for (const id of known) {
          if (!seen.includes(id)) store.m_mapAudioDevices?.delete(id);
        }
        for (const device of state.devices) store.RegisterOrUpdateDevice(toDevice(device));
      } catch {
        // A store whose shape moved is a compatibility loss, not a fault: the namespace stays and
        // a client rebuilt around a different store simply shows no audio section.
      }
    };

    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const system = window.SteamClient?.System;
      if (!system) {
        lastError = "SteamClient.System unavailable";
        return { ok: false, error: lastError };
      }

      // Never replace a real backend. On a client that grows one, WSGM must stand aside rather than
      // shadow it with a projection of a different machine's audio.
      if (system.Audio) {
        lastError = "SteamClient.System.Audio already exists";
        return { ok: false, error: lastError };
      }

      const api = {
        GetDevices: () =>
          request(patchId, "getDevices", null, 0).then((state: any) => ({
            activeOutputDeviceId: state?.activeOutputDeviceId ?? "",
            activeInputDeviceId: state?.activeInputDeviceId ?? "",
            overrideOutputDeviceId: "",
            overrideInputDeviceId: "",
            vecDevices: Array.isArray(state?.devices) ? state.devices.map(toDevice) : [],
          })),
        // Empty until a session mixer exists. Steam then lists no per-application entries, which is
        // the honest outcome rather than inventing volumes it cannot move.
        GetApps: () => Promise.resolve({ rgApps: [] }),
        SetDefaultDeviceOverride: (id, direction) =>
          request(patchId, "setDefaultDevice", { id: String(id), input: direction === 1 }, 0),
        SetDeviceVolume: (id, volume) =>
          request(patchId, "setVolume", { percent: Math.round((volume ?? 0) * 100) }, 0),
        SetAppVolume: () => Promise.resolve(),
        ClearDefaultDeviceOverride: () => Promise.resolve(),
        RegisterForServiceConnectionStateChanges: register("serviceConnection"),
        RegisterForDeviceAdded: register("deviceAdded"),
        RegisterForDeviceRemoved: register("deviceRemoved"),
        RegisterForDeviceVolumeChanged: register("deviceVolumeChanged"),
        RegisterForVolumeButtonPressed: register("volumeButtonPressed"),
        RegisterForAppAdded: register("appAdded"),
        RegisterForAppRemoved: register("appRemoved"),
        RegisterForAppVolumeChanged: register("appVolumeChanged"),
      };

      try {
        Object.defineProperty(system, "Audio", {
          value: api,
          configurable: true,
          enumerable: true,
          writable: false,
        });
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true };
    };

    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }

      for (const slot of Object.keys(callbacks)) callbacks[slot] = null;
      known = [];
      try {
        delete window.SteamClient.System.Audio;
      } catch (error) {
        lastError = String(error);
        return { ok: false, error: lastError };
      }

      return { ok: true, removed: true };
    };

    const status = () => ({
      ok: true,
      installed,
      namespacePresent: !!window.SteamClient?.System?.Audio,
      registrations: Object.keys(callbacks).filter((slot) => callbacks[slot] !== null),
      knownDevices: known.length,
      lastError,
    });

    return { install, remove, status };
  }

  function createNativeComponentHost() {
    const registrations = new Map();
    const listeners = new Set<() => void>();
    const actionGenerations = new Map();
    let runtime;
    let controlRuntime;
    let tdpControl;
    let autoTdpControl;
    let frameLimitControl;
    let overlayLevelControl;
    let controllerControl;
    let resolutionControl;
    let performanceRoot;
    let originalUseMemo;
    let patchedUseMemo;
    let disposedHost = false;

    // What the last append attempt actually did, surfaced through status(). Without it a panel that
    // inserted nothing was indistinguishable from a bridge that never ran.
    let lastAppend: {
      controls: number;
      inserted: boolean;
      ownSection: boolean;
      tree?: string;
      nativeFiltered?: boolean;
      nativeRowsHidden?: number;
    } | null = null;

    // Why each control did or did not draw. A control that renders null leaves no trace anywhere:
    // the row is built and appended, the panel simply has one fewer child, and every other signal
    // still reports success. This is the difference between "WSGM did not add it" and "WSGM added
    // it and the device had nothing to show".
    const renderOutcomes: Record<string, string> = {};
    const note = (kind, reason) => {
      // "no state" is what every render sees while a delivery is being rejected, and the wrapper
      // re-renders on each host notification, so the generic reason must not overwrite the precise
      // one the subscription recorded.
      if (
        reason === "no state" &&
        renderOutcomes[kind] === "state received but rejected by validation"
      ) {
        return null;
      }

      renderOutcomes[kind] = reason;
      return null;
    };

    const definitions = Object.freeze({
      tdp: Object.freeze({
        patchId: "wsgm.native-qam.tdp",
        command: "setPrimaryLimit",
      }),
      autoTdp: Object.freeze({
        patchId: "wsgm.native-qam.auto-tdp",
        command: "setAutoTdp",
      }),
      frameLimit: Object.freeze({
        patchId: "wsgm.native-qam.frame-limit",
        command: "setFrameLimit",
      }),
      overlayLevel: Object.freeze({
        patchId: "wsgm.native-qam.overlay-level",
        command: "setOverlayLevel",
      }),
      controllerTarget: Object.freeze({
        patchId: "wsgm.native-qam.controller-target",
        command: "setControllerTarget",
      }),
      // Hand-built, unlike the frame limit and VRR rows. SteamOS drives resolution through
      // gamescope and this client ships no component for it, so there is nothing to mount.
      resolution: Object.freeze({
        patchId: "wsgm.native-qam.resolution",
        command: "setResolution",
      }),
    });

    const nextActionGeneration = (patchId) => {
      const next = (actionGenerations.get(patchId) || 0) + 1;
      actionGenerations.set(patchId, next);
      return next;
    };
    const notify = () => {
      for (const listener of [...listeners]) {
        try {
          listener();
        } catch {}
      }
    };
    const subscribeHost = (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    };
    const uniqueFactory = (requiredTokens) => {
      const matches = Object.entries(runtime.m).filter(([, factory]) => {
        const source = String(factory);
        return requiredTokens.every((token) => source.includes(token));
      });
      return matches.length === 1 ? matches[0] : null;
    };
    const uniqueFunction = (exports, requiredTokens) => {
      const matches = Object.values(exports).filter(
        (value) =>
          typeof value === "function" &&
          requiredTokens.every((token) => String(value).includes(token)),
      );
      return matches.length === 1 ? matches[0] : null;
    };
    const uniqueObject = (exports, predicate) => {
      const matches = Object.values(exports).filter(
        (value) => value && typeof value === "object" && predicate(value),
      );
      return matches.length === 1 ? matches[0] : null;
    };
    const getRuntime = () => {
      let found;
      window.webpackChunksteamui.push([
        ["wsgm_native_components_" + Date.now()],
        {},
        (value) => {
          found = value;
        },
      ]);
      return found;
    };
    const createControlRuntime = () => {
      const reactFactory = uniqueFactory([
        "react.transitional.element",
        "useState",
        "cloneElement",
        "createElement",
      ]);
      const fieldsFactory = uniqueFactory([
        "DialogSlider_Container",
        "DropDownField",
        "SliderField",
      ]);
      const layoutFactory = uniqueFactory(["PanelSectionTitle", "PanelSectionRow", "spinner"]);
      const localizationFactory = uniqueFactory([
        "Attempting to localize token",
        "Unable to find localization token",
        "LocalizeString",
      ]);
      if (!reactFactory || !fieldsFactory || !layoutFactory || !localizationFactory) return null;

      const react = runtime(reactFactory[0]);
      const fields = runtime(fieldsFactory[0]);
      const layout = runtime(layoutFactory[0]);
      const localization = runtime(localizationFactory[0]);
      const slider = uniqueFunction(fields, [
        "onChangeComplete",
        "notchCount",
        "valueSuffix",
        "explainerTitle",
      ]);
      const dropdown = uniqueFunction(fields, [
        "contextMenuPositionOptions",
        "childrenContainerWidth",
        "menuLabel",
      ]);
      // Steam's own ToggleField, from the same module as the slider and dropdown above. Selected by
      // the two markers of its class body rather than by its export name, which is minified and
      // changes with every client build. Live-verified 2026-08-29: exactly one export matches, and
      // the provider that names the module's fields lists that same class as ToggleField.
      const toggle = uniqueFunction(fields, ["OnToggleChange", "this.Toggle()"]);
      const section = uniqueFunction(layout, ["PanelSectionTitle", "spinner"]);
      const row = uniqueObject(
        layout,
        (value) => value.$$typeof && typeof value.render === "function",
      );
      const localize = uniqueFunction(localization, ["LocalizeString(e)", "void 0===r?e"]);
      if (!slider || !dropdown || !section || !row || !localize) return null;
      // The toggle is deliberately not in that guard. It arrived after the other four, so a client
      // whose toggle cannot be found still gets every control that does not need one, rather than
      // losing the whole native surface.
      return { react, slider, dropdown, toggle, section, row, localize };
    };
    const normalizeText = (value) => (typeof value === "string" ? value.slice(0, 240) : "");
    const normalizeAutoTdpState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (typeof value.enabled !== "boolean" || typeof value.controlling !== "boolean") return null;
      // The watts figure is only ever a display detail beside the switch, so a value outside the
      // range any power limit uses is dropped rather than rejecting the whole state and taking the
      // switch away with it.
      const watts =
        typeof value.watts === "number" &&
        Number.isInteger(value.watts) &&
        value.watts >= 1 &&
        value.watts <= 200
          ? value.watts
          : null;
      return Object.freeze({
        available: value.available,
        enabled: value.enabled,
        controlling: value.controlling,
        watts,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeTdpState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (!value.available) {
        return Object.freeze({
          available: false,
          minimumWatts: null,
          maximumWatts: null,
          stepWatts: null,
          desiredWatts: null,
          observedWatts: null,
          progress: normalizeText(value.progress),
          statusText: normalizeText(value.statusText),
        });
      }
      const min = Number(value.minimumWatts);
      const max = Number(value.maximumWatts);
      const step = Number(value.stepWatts);
      const desired = typeof value.desiredWatts === "number" ? value.desiredWatts : null;
      const observed = typeof value.observedWatts === "number" ? value.observedWatts : null;
      if (
        !Number.isInteger(min) ||
        !Number.isInteger(max) ||
        !Number.isInteger(step) ||
        min < 1 ||
        max > 200 ||
        min >= max ||
        step < 1 ||
        step > max - min ||
        (desired !== null && (!Number.isInteger(desired) || desired < min || desired > max)) ||
        (observed !== null && (!Number.isInteger(observed) || observed < min || observed > max))
      )
        return null;
      return Object.freeze({
        available: value.available,
        minimumWatts: min,
        maximumWatts: max,
        stepWatts: step,
        desiredWatts: desired,
        observedWatts: observed,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeControllerState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (!Array.isArray(value.targets) || value.targets.length > 8) return null;
      const targets: Readonly<{ id: string; label: string; available: boolean }>[] = [];
      const ids = new Set();
      for (const item of value.targets) {
        if (!item || typeof item !== "object") return null;
        const id = normalizeText(item.id);
        const label = normalizeText(item.label);
        // Uppercase is allowed because the ids WSGM actually sends are PascalCase —
        // SteamDeckComposite, Xbox360, DualShock4. A lowercase-only pattern rejected every one of
        // them, so the whole state normalised to null and the controller row never drew, with
        // nothing anywhere saying a state had been received and thrown away.
        if (!/^[A-Za-z0-9._-]{1,64}$/.test(id) || !label || ids.has(id)) return null;
        ids.add(id);
        targets.push(Object.freeze({ id, label, available: item.available !== false }));
      }
      const selectedTarget = normalizeText(value.selectedTarget);
      const observedTarget = normalizeText(value.observedTarget);
      if (
        (selectedTarget && !ids.has(selectedTarget)) ||
        (observedTarget && !ids.has(observedTarget))
      )
        return null;
      return Object.freeze({
        available: value.available,
        targets: Object.freeze(targets),
        selectedTarget,
        observedTarget,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
        applicationRestartRequired: value.applicationRestartRequired === true,
      });
    };
    const validEnum = (value, allowed) =>
      typeof value === "string" && allowed.includes(value) ? value : null;
    const normalizePerformanceCommon = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      const readbackQuality = validEnum(value.readbackQuality, [
        "unavailable",
        "verified",
        "applied-unverified",
        "stale",
      ]);
      const policyLayer = validEnum(value.policyLayer, ["none", "global", "application"]);
      const adapterAvailability = validEnum(value.adapterAvailability, [
        "unknown",
        "not-installed",
        "not-running",
        "incompatible",
        "adapter-unavailable",
        "ready",
        "degraded",
      ]);
      const progress = validEnum(value.progress, [
        "idle",
        "queued",
        "applying",
        "succeeded-verified",
        "applied-unverified",
        "rejected",
        "timed-out",
        "indeterminate",
        "failed",
        "external-change",
      ]);
      if (!readbackQuality || !policyLayer || !adapterAvailability || !progress) return null;
      return Object.freeze({
        available: value.available,
        supportsReadback: value.supportsReadback === true,
        readbackQuality,
        policyLayer,
        applicationTargetAvailable: value.applicationTargetAvailable === true,
        targetProfile: normalizeText(value.targetProfile),
        adapterAvailability,
        progress,
        fault: normalizeText(value.fault),
        statusText: normalizeText(value.statusText),
      });
    };
    // Validated rather than trusted, like every other semantic state: this arrives over the bridge
    // and a malformed option list would render a dropdown whose entries select nothing.
    const normalizeResolutionState = (value) => {
      if (!value || typeof value !== "object") return null;
      const options = Array.isArray(value.options)
        ? value.options.filter(
            (option) =>
              typeof option === "string" && /^[1-9][0-9]{2,4}x[1-9][0-9]{2,4}$/.test(option),
          )
        : [];
      return {
        available: value.available === true,
        options: options.slice(0, 64),
        current: typeof value.current === "string" ? value.current : "",
        statusText: typeof value.statusText === "string" ? value.statusText : "",
      };
    };

    const normalizeFrameLimitState = (value) => {
      const common = normalizePerformanceCommon(value);
      if (!common) return null;
      const minimumFps = value.minimumFps === null ? null : Number(value.minimumFps);
      const maximumFps = value.maximumFps === null ? null : Number(value.maximumFps);
      const desiredFps = value.desiredFps === null ? null : Number(value.desiredFps);
      const observedFps = value.observedFps === null ? null : Number(value.observedFps);
      // The bounds are a pair: either both are present or neither is. Rejecting a
      // half-populated range here rather than inside the big test below is also what
      // lets the rest of it treat maximumFps as a number.
      if ((minimumFps === null) !== (maximumFps === null)) return null;
      if (
        (minimumFps !== null &&
          maximumFps !== null &&
          (!Number.isInteger(minimumFps) ||
            !Number.isInteger(maximumFps) ||
            minimumFps < 0 ||
            maximumFps < minimumFps ||
            maximumFps > 1000)) ||
        (desiredFps !== null &&
          (!Number.isInteger(desiredFps) ||
            minimumFps === null ||
            maximumFps === null ||
            desiredFps < minimumFps ||
            desiredFps > maximumFps)) ||
        (observedFps !== null &&
          (!Number.isInteger(observedFps) ||
            minimumFps === null ||
            maximumFps === null ||
            observedFps < minimumFps ||
            observedFps > maximumFps)) ||
        (common.available && minimumFps === null)
      )
        return null;
      return Object.freeze({ ...common, minimumFps, maximumFps, desiredFps, observedFps });
    };
    const normalizeOverlayLevelState = (value) => {
      const common = normalizePerformanceCommon(value);
      if (!common || !Array.isArray(value.levels) || value.levels.length > 5) return null;
      const levels: number[] = [];
      for (const item of value.levels) {
        const level = Number(item);
        if (!Number.isInteger(level) || level < 0 || level > 4 || levels.includes(level))
          return null;
        levels.push(level);
      }
      levels.sort((left, right) => left - right);
      const desiredLevel = value.desiredLevel === null ? null : Number(value.desiredLevel);
      const observedLevel = value.observedLevel === null ? null : Number(value.observedLevel);
      if (
        (desiredLevel !== null &&
          (!Number.isInteger(desiredLevel) || !levels.includes(desiredLevel))) ||
        (observedLevel !== null &&
          (!Number.isInteger(observedLevel) || !levels.includes(observedLevel))) ||
        (common.available && levels.length === 0)
      )
        return null;
      return Object.freeze({
        ...common,
        levels: Object.freeze(levels),
        desiredLevel,
        observedLevel,
      });
    };
    const useSemanticState = (controlRuntime, kind, normalize) => {
      const definition = definitions[kind];
      const [state, setState] = controlRuntime.react.useState(null);
      controlRuntime.react.useEffect(
        () =>
          subscribe(definition.patchId, (value) => {
            const normalized = normalize(value);

            // A state that arrives and fails validation is not the same as one that never
            // arrived, and both used to end as a null the control returned on. The controller row
            // was invisible for exactly this reason: WSGM sends PascalCase target ids and the
            // validator only accepted lowercase, so every delivery was discarded in silence.
            if (normalized === null && value) {
              renderOutcomes[kind] = "state received but rejected by validation";
            }

            setState(normalized);
          }),
        [],
      );
      return state;
    };
    const isBusy = (progress) =>
      progress === "queued" || progress === "applying" || progress === "replacing";

    /// Lets a controlled slider follow the user's input before the hardware confirms it.
    ///
    /// These sliders are controlled by the observed hardware value, so with a no-op onChange the
    /// handle snapped back to that value on every render: dragging did nothing at all, and a single
    /// press moved exactly one step because only onChangeComplete ever committed. The echo holds
    /// what the user is pointing at until the release, then clears so the observed value governs
    /// again — including when the device refuses the write and the handle must spring back to what
    /// the hardware really is.
    const useEchoedValue = (controlRuntime, observed) => {
      const [echo, setEcho] = controlRuntime.react.useState(null);
      const [echoOf, setEchoOf] = controlRuntime.react.useState(observed);

      // A new observation supersedes an echo taken against the previous one; without this the
      // handle would keep showing a value the hardware had already moved away from.
      if (echoOf !== observed) {
        setEchoOf(observed);
        if (echo !== null) setEcho(null);
      }

      return {
        value: echo ?? observed,
        onChange: (next) => setEcho(typeof next === "number" ? next : null),
        onChangeComplete: (next, commit) => {
          setEcho(null);
          commit(next);
        },
      };
    };
    // Steam's localizer returns the token itself when it has no string for it, which is truthy and
    // would render "#QuickAccess_..." as a label. Live-verified 2026-08-29: a known token localizes,
    // an unknown one comes straight back.
    //
    // EVERY label goes through this, not only the WSGM-invented ones. The Valve performance tokens
    // are SteamOS strings and are not all present in the Windows client's localization set: with
    // the rows finally rendering on the reference Claw, "#QuickAccess_Tab_Perf_FramerateLimit" and
    // "#QuickAccess_Tab_Perf_PerfOverlayLevel" both came back raw and were shown to the user as
    // their token text. A bare localize() call here is a bug waiting for the next missing string.
    // Steam's localizer does not return a string. It returns a React element wrapping one, so
    // `typeof text === "string"` was false for every token and every WSGM label fell back to its
    // English default while Steam's own rows beside them were in the user's language. The element
    // is what should be handed to the field — only the "#" test needs the text inside it.
    const textOf = (value) => {
      if (typeof value === "string") return value;
      return value && typeof value === "object" && typeof value.props?.children === "string"
        ? value.props.children
        : null;
    };
    const localizeOr = (controlRuntime, token, fallback) => {
      const localized = controlRuntime.localize(token);
      const text = textOf(localized);
      return text && text.length > 0 && text[0] !== "#" ? localized : fallback;
    };
    const createTdpControl = (controlRuntime) =>
      function WsgmNativeTdpControl() {
        const state = useSemanticState(controlRuntime, "tdp", normalizeTdpState);

        // Both hooks run before any early return: a control that renders nothing until its state
        // arrives would otherwise change its hook count the moment it does, which React treats as a
        // fatal error and would take the whole panel down with it.
        const value = state ? (state.observedWatts ?? state.desiredWatts) : null;
        const echoed = useEchoedValue(controlRuntime, value);
        if (!state) return note("tdp", "no state");
        if (!state.available)
          return note("tdp", "unavailable: " + (state.statusText || "no reason"));
        if (value === null) return note("tdp", "no observed or desired watts");
        renderOutcomes.tdp = "rendered";
        const definition = definitions.tdp;
        const setValue = (watts) => {
          if (!Number.isInteger(watts) || watts < state.minimumWatts || watts > state.maximumWatts)
            return;
          void request(
            definition.patchId,
            definition.command,
            { watts },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.slider, {
          label: localizeOr(controlRuntime, "#QuickAccess_Tab_Perf_TDPLimitEnabled", "TDP limit"),
          explainer: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_TDPLimit_Explainer",
            "Sets the sustained power limit for the processor.",
          ),
          explainerTitle: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_TDPLimitEnabled",
            "TDP limit",
          ),
          valueSuffix: localizeOr(controlRuntime, "#QuickAccess_Tab_Perf_TDPLimitUnits", "W"),
          min: state.minimumWatts,
          max: state.maximumWatts,
          step: state.stepWatts,
          value: echoed.value,
          showValue: true,
          showBookendLabels: true,
          disabled: isBusy(state.progress),
          description: state.statusText || undefined,
          onChange: echoed.onChange,
          onChangeComplete: (next) => echoed.onChangeComplete(next, setValue),
        });
      };
    const createAutoTdpControl = (controlRuntime) =>
      function WsgmNativeAutoTdpControl() {
        const state = useSemanticState(controlRuntime, "autoTdp", normalizeAutoTdpState);
        if (!state) return note("autoTdp", "no state");
        if (!state.available)
          return note("autoTdp", "unavailable: " + (state.statusText || "no reason"));
        // Deliberately outside createControlRuntime's guard, so a client whose ToggleField cannot
        // be located loses only this row. That silence is exactly what needed a name.
        if (!controlRuntime.toggle) return note("autoTdp", "Steam ToggleField was not resolved");
        renderOutcomes.autoTdp = "rendered";
        const definition = definitions.autoTdp;
        const setEnabled = (enabled) => {
          if (typeof enabled !== "boolean" || enabled === state.enabled) return;
          void request(
            definition.patchId,
            definition.command,
            { enabled },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        // While controlling, the watts AutoTDP settled on go in the description: a user watching the
        // slider move needs to see that something is driving it, and what it decided.
        const description =
          state.controlling && state.watts !== null
            ? state.watts + " W · " + state.statusText
            : state.statusText;
        return controlRuntime.react.createElement(controlRuntime.toggle, {
          label: localizeOr(controlRuntime, "#QuickAccess_Tab_Perf_AutoTDP", "Automatic TDP"),
          description: description || undefined,
          checked: state.enabled,
          // Controlled, so the switch shows the stored setting rather than its own click. A command
          // that does not land leaves the switch where the setting actually is instead of showing a
          // change that did not happen.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: setEnabled,
        });
      };
    const createControllerControl = (controlRuntime) =>
      function WsgmNativeControllerTargetControl() {
        const state = useSemanticState(
          controlRuntime,
          "controllerTarget",
          normalizeControllerState,
        );
        if (!state) return note("controllerTarget", "no state");
        if (!state.available)
          return note("controllerTarget", "unavailable: " + (state.statusText || "no reason"));
        const options = state.targets
          .filter((target) => target.available)
          .map((target) => ({ data: target.id, label: target.label }));
        const selected = state.observedTarget || state.selectedTarget;
        if (!options.some((option) => option.data === selected))
          return note(
            "controllerTarget",
            `selected '${selected}' is not among ${options.length} available target(s)`,
          );
        renderOutcomes.controllerTarget = "rendered";
        const definition = definitions.controllerTarget;
        const setTarget = (option) => {
          if (!option || !options.some((candidate) => candidate.data === option.data)) return;
          void request(
            definition.patchId,
            definition.command,
            { target: option.data },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        const restart = state.applicationRestartRequired
          ? " Restart the application to rebind."
          : "";
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Settings_Section_Controller_Title",
            "Controller",
          ),
          rgOptions: options,
          selectedOption: selected,
          onChange: setTarget,
          disabled: isBusy(state.progress) || options.length < 2,
          description: (state.statusText || "") + restart || undefined,
          layout: "below",
        });
      };
    const createResolutionControl = (controlRuntime) =>
      function WsgmNativeResolutionControl() {
        const state = useSemanticState(controlRuntime, "resolution", normalizeResolutionState);
        if (!state) return note("resolution", "no state");
        if (!state.available)
          return note("resolution", "unavailable: " + (state.statusText || "no reason"));
        if (state.options.length < 2)
          return note("resolution", `only ${state.options.length} option(s)`);
        renderOutcomes.resolution = "rendered";
        const definition = definitions.resolution;
        const options = state.options.map((option) => ({ data: option, label: option }));
        const setResolution = (option) => {
          // Checked against the offered list before sending. The row cannot be the only thing
          // standing between a stray value and a mode change, but it should not be the source of
          // one either.
          if (!option || !state.options.includes(option.data)) return;
          // "target" rather than "value": that is the payload shape every dropdown here uses, and
          // the host's reader rejects an object carrying anything else.
          void request(
            definition.patchId,
            definition.command,
            { target: option.data },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: localizeOr(
            controlRuntime,
            "#Settings_Display_Resolution_Title",
            "Display resolution",
          ),
          rgOptions: options,
          // A current mode outside the offered list selects nothing rather than the first entry,
          // which would silently misreport what the display is doing.
          selectedOption: state.options.includes(state.current) ? state.current : undefined,
          onChange: setResolution,
          description: state.statusText || undefined,
          layout: "below",
        });
      };
    const createFrameLimitControl = (controlRuntime) =>
      function WsgmNativeFrameLimitControl() {
        const state = useSemanticState(controlRuntime, "frameLimit", normalizeFrameLimitState);
        const value = state ? (state.observedFps ?? state.desiredFps) : null;
        const echoed = useEchoedValue(controlRuntime, value);
        if (!state) return note("frameLimit", "no state");
        if (!state.available)
          return note("frameLimit", "unavailable: " + (state.statusText || "no reason"));
        if (value === null) return note("frameLimit", "no observed or desired fps");
        renderOutcomes.frameLimit = "rendered";
        const definition = definitions.frameLimit;
        const setValue = (nextValue) => {
          if (
            !Number.isInteger(nextValue) ||
            nextValue < state.minimumFps ||
            nextValue > state.maximumFps
          )
            return;
          void request(
            definition.patchId,
            definition.command,
            { value: nextValue, persistence: "automatic" },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.slider, {
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_FramerateLimit",
            "Frame rate limit",
          ),
          min: state.minimumFps,
          max: state.maximumFps,
          step: 1,
          value: echoed.value,
          valueSuffix: " FPS",
          showValue: true,
          showBookendLabels: true,
          disabled: isBusy(state.progress),
          description: state.fault || state.statusText || undefined,
          onChange: echoed.onChange,
          onChangeComplete: (next) => echoed.onChangeComplete(next, setValue),
        });
      };
    const createOverlayLevelControl = (controlRuntime) =>
      function WsgmNativeOverlayLevelControl() {
        const state = useSemanticState(controlRuntime, "overlayLevel", normalizeOverlayLevelState);
        if (!state) return note("overlayLevel", "no state");
        if (!state.available)
          return note("overlayLevel", "unavailable: " + (state.statusText || "no reason"));
        const selected = state.observedLevel ?? state.desiredLevel;
        if (selected === null || !state.levels.includes(selected))
          return note("overlayLevel", `level ${selected} is not among [${state.levels}]`);
        renderOutcomes.overlayLevel = "rendered";
        const options = state.levels.map((level) => ({
          data: level,
          label: level === 0 ? "Off" : level === 1 ? "On" : String(level),
        }));
        const definition = definitions.overlayLevel;
        const setValue = (option) => {
          if (!option || !state.levels.includes(option.data)) return;
          void request(
            definition.patchId,
            definition.command,
            { value: option.data, persistence: "automatic" },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        };
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_PerfOverlayLevel",
            "Performance overlay",
          ),
          rgOptions: options,
          selectedOption: selected,
          onChange: setValue,
          disabled: isBusy(state.progress) || options.length < 2,
          description: state.fault || state.statusText || undefined,
          layout: "below",
        });
      };
    // Steam's own FPS counter rows, which WSGM replaces with its RTSS-driven overlay. Identified by
    // localising the same tokens Steam did rather than by CSS class or visible text: the classes
    // are hashed per client build and the text changes with the user's language, while the token is
    // the one thing that is neither.
    const NativeFpsTokens = [
      "#QuickAccess_Tab_Perf_FPS_Corner",
      "#QuickAccess_Tab_Perf_FPS_Contrast",
    ];
    let filteredNative: { inner: unknown; component: unknown } | null = null;
    let lastHidden = 0;

    // Wrappers that carry the filter into a component's own render output, cached against the
    // component so React keeps seeing one stable type per original and never remounts the subtree.
    const descendCache = new WeakMap();

    /// Removes the native rows whose label matches one of the tokens above.
    ///
    /// Descends through RENDERED output, not just props.children. The rows sit about ten levels
    /// inside Steam's panel behind component elements, and a component's children do not exist
    /// until React renders it — so a walk over props.children alone reaches nothing, which is why
    /// the filter previously ran and hid zero rows. Each function component met on the way down is
    /// replaced by a wrapper that renders the original and filters what it returns, which is the
    /// same mechanism Decky's createReactTreePatcher uses to reach into this panel.
    const hideNativeRows = (controlRuntime, element, labels, depth) => {
      if (depth > 12 || !controlRuntime.react.isValidElement(element)) return element;

      // Compared as text on both sides: a label is sometimes a localiser element and sometimes a
      // plain string, and matching the raw prop found nothing at all.
      const label = textOf(element.props && element.props.label);
      if (label !== null && labels.includes(label)) {
        lastHidden++;
        return null;
      }

      const type: any = element.type;
      if (typeof type === "function" && !type.prototype?.isReactComponent) {
        // A plain function component: render it through a wrapper so its output is filtered too.
        // Class components, memo and forwardRef objects are left alone — they cannot be called
        // directly, and wrapping them would change identity for refs.
        let wrapper = descendCache.get(type);
        if (!wrapper) {
          wrapper = function WsgmNativeQamDescend(props) {
            return hideNativeRows(controlRuntime, type(props), labels, 0);
          };
          descendCache.set(type, wrapper);
        }

        // The key rides along explicitly: it lives on the element, not in props, and dropping it
        // would re-key this node inside its parent's child list on every render.
        return controlRuntime.react.createElement(
          wrapper,
          element.key === null ? element.props : { ...element.props, key: element.key },
        );
      }

      const kids = controlRuntime.react.Children.toArray(element.props?.children);
      if (!kids.length) return element;
      let changed = false;
      const next: unknown[] = [];
      for (const kid of kids) {
        const replacement = hideNativeRows(controlRuntime, kid, labels, depth + 1);
        changed ||= replacement !== kid;
        if (replacement !== null) next.push(replacement);
      }

      return changed ? controlRuntime.react.cloneElement(element, {}, ...next) : element;
    };

    /// Wraps Steam's performance root so its OUTPUT can be filtered.
    ///
    /// The root returns a single component element with no static children, so its rows exist only
    /// once React renders it. Calling it from inside a component of our own is what puts its output
    /// in reach; the wrapper is cached against the inner component so React sees a stable type and
    /// does not remount the panel on every render.
    const withNativeRowsHidden = (controlRuntime, tree) => {
      const inner: any = tree && tree.type;
      if (typeof inner !== "function") return tree;
      const labels = NativeFpsTokens.map((token) => textOf(controlRuntime.localize(token))).filter(
        (text) => typeof text === "string" && text.length > 0 && text[0] !== "#",
      );
      if (!labels.length) return tree;
      if (!filteredNative || filteredNative.inner !== inner) {
        filteredNative = {
          inner,
          component: function WsgmNativeQamFilteredPerformance(props) {
            lastHidden = 0;
            return hideNativeRows(controlRuntime, inner(props), labels, 0);
          },
        };
      }

      return controlRuntime.react.createElement(filteredNative.component, tree.props);
    };

    const appendControls = (controlRuntime, tree) => {
      // Rendered React elements from Steam's own untyped runtime.
      const controls: unknown[] = [];
      if (registrations.has("tdp")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-tdp" },
            controlRuntime.react.createElement(tdpControl),
          ),
        );
      }
      // Straight after the power limit, because AutoTDP is what moves it. A user who sees the
      // slider change on its own finds the explanation in the next row rather than hunting for it.
      if (registrations.has("autoTdp")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-auto-tdp" },
            controlRuntime.react.createElement(autoTdpControl),
          ),
        );
      }
      if (registrations.has("frameLimit")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-frame-limit" },
            controlRuntime.react.createElement(frameLimitControl),
          ),
        );
      }
      // After the frame limit, because under the pairing strategies the cap is what moves the
      // refresh rate, and a resolution change carries the current rate with it: the user reads the
      // thing that drives before the thing that follows.
      if (registrations.has("resolution")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-resolution" },
            controlRuntime.react.createElement(resolutionControl),
          ),
        );
      }
      if (registrations.has("overlayLevel")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-overlay-level" },
            controlRuntime.react.createElement(overlayLevelControl),
          ),
        );
      }
      if (registrations.has("controllerTarget")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-controller-target" },
            controlRuntime.react.createElement(controllerControl),
          ),
        );
      }
      if (!controls.length) {
        lastAppend = { controls: 0, inserted: false, ownSection: false };
        return tree;
      }

      // WSGM's rows go into a PanelSection of their own, appended after whatever the native
      // performance panel rendered.
      //
      // The previous implementation searched the tree for a component identical to
      // controlRuntime.section and inserted into it. That could never work, on any OS: `tree` is
      // the ELEMENT returned by performanceRoot(props), and an element's props.children holds only
      // what was passed IN, never what its component produces when React renders it. Steam's
      // section exists only after that rendering, so the walk terminated on a root with no
      // children — measured on the reference Claw as depthReached 0, sectionSeen false, with the
      // section component itself resolved and all five rows built. It failed silently, which is
      // why an empty Quick Access panel survived so long: every other signal said success.
      //
      // Appending a section instead depends on nothing about Steam's internal tree shape, so it
      // cannot be broken by a Steam UI change or by the fields Windows hides.
      const own = controlRuntime.react.createElement(
        controlRuntime.section,
        { key: "wsgm-native-qam-section" },
        ...controls,
      );

      // Shape of what Steam's performance root returned, so the rows it renders can be identified
      // without guessing. Needed to suppress Steam's own FPS counter rows in favour of WSGM's
      // RTSS overlay: their DOM classes are hashed per client build and unusable as selectors.
      const describe = (element, depth) => {
        if (!controlRuntime.react.isValidElement(element)) return typeof element;
        const t: any = element.type;
        const name = typeof t === "string" ? t : t?.displayName || t?.name || "anonymous";
        const kids = controlRuntime.react.Children.toArray(element.props?.children);
        return depth >= 2 || !kids.length
          ? name
          : { [name]: kids.map((k) => describe(k, depth + 1)) };
      };
      // Steam's FPS rows are suppressed only on this path, which runs when WSGM has rows of its own
      // to put in their place. Hiding them and then rendering nothing would leave the user neither.
      const native = withNativeRowsHidden(controlRuntime, tree);
      lastAppend = {
        controls: controls.length,
        inserted: true,
        ownSection: true,
        tree: JSON.stringify(describe(tree, 0)).slice(0, 600),
        nativeFiltered: native !== tree,
        nativeRowsHidden: lastHidden,
      };
      return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, native, own);
    };
    const ensurePatched = () => {
      if (
        controlRuntime &&
        performanceRoot &&
        patchedUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      )
        return true;
      runtime = getRuntime();
      if (!runtime || !runtime.m) return false;
      const performanceFactory = uniqueFactory([
        "#QuickAccess_Tab_Perf_Common_Settings",
        "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
        "TS.ON_FRAME",
      ]);
      controlRuntime = createControlRuntime();
      if (!performanceFactory || !controlRuntime) return false;
      performanceRoot = uniqueFunction(runtime(performanceFactory[0]), ["TS.ON_FRAME", "return"]);
      if (!performanceRoot) return false;
      tdpControl = createTdpControl(controlRuntime);
      autoTdpControl = createAutoTdpControl(controlRuntime);
      frameLimitControl = createFrameLimitControl(controlRuntime);
      overlayLevelControl = createOverlayLevelControl(controlRuntime);
      controllerControl = createControllerControl(controlRuntime);
      resolutionControl = createResolutionControl(controlRuntime);
      function WsgmNativeQamPerformanceRoot(props) {
        const [, setRevision] = controlRuntime.react.useState(0);
        controlRuntime.react.useEffect(
          () => subscribeHost(() => setRevision((value) => value + 1)),
          [],
        );
        return appendControls(controlRuntime, performanceRoot(props));
      }
      originalUseMemo = controlRuntime.react.useMemo;
      patchedUseMemo = function WsgmNativeQamUseMemo(factory, dependencies) {
        const value = originalUseMemo(factory, dependencies);
        if (!Array.isArray(value)) return value;
        const matches = value.filter(
          (item) =>
            item &&
            typeof item === "object" &&
            controlRuntime.react.isValidElement(item.panel) &&
            item.panel.type === performanceRoot,
        );
        if (matches.length !== 1) return value;
        return value.map((item) => {
          if (item !== matches[0]) return item;
          const panel = controlRuntime.react.createElement(WsgmNativeQamPerformanceRoot, {
            ...item.panel.props,
            key: item.panel.key ?? "wsgm-native-qam-performance-root",
          });
          return { ...item, panel };
        });
      };
      controlRuntime.react.useMemo = patchedUseMemo;
      return controlRuntime.react.useMemo === patchedUseMemo;
    };
    const install = (kind) => {
      if (disposedHost || !Object.hasOwn(definitions, kind))
        return { ok: false, error: "component is not allowlisted" };
      if (!ensurePatched())
        return {
          ok: false,
          error: "native performance root was already initialized or incompatible",
        };
      registrations.set(kind, definitions[kind].patchId);
      notify();
      return { ok: true, kind, registered: true, hostVersion: 1 };
    };
    const remove = (kind) => {
      if (!Object.hasOwn(definitions, kind)) return { ok: true, absent: true };
      registrations.delete(kind);
      actionGenerations.delete(definitions[kind].patchId);
      notify();
      if (
        !registrations.size &&
        controlRuntime &&
        originalUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      ) {
        controlRuntime.react.useMemo = originalUseMemo;
      }
      return { ok: true, kind, registered: false };
    };
    const status = (kind) => ({
      ok: Object.hasOwn(definitions, kind),
      kind,
      registered: registrations.has(kind),
      hostVersion: 1,
      performanceRootWrapped:
        !!controlRuntime && !!patchedUseMemo && controlRuntime.react.useMemo === patchedUseMemo,
      // Everything above can be true while the panel still shows nothing, because insertion
      // depends on the shape of the tree Steam renders. This is the part that says so.
      lastAppend,
      // And this says which rows drew, and why the others did not.
      renderOutcomes,
      toggleResolved: !!(controlRuntime && controlRuntime.toggle),
    });
    const disposeHostResources = () => {
      disposedHost = true;
      registrations.clear();
      actionGenerations.clear();
      notify();
      listeners.clear();
      if (controlRuntime && originalUseMemo && controlRuntime.react.useMemo === patchedUseMemo)
        controlRuntime.react.useMemo = originalUseMemo;
    };
    return { install, remove, status, dispose: disposeHostResources };
  }
})();
