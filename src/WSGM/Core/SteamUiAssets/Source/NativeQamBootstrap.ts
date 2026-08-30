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
  // The host REJECTS an action generation of zero, and several gates were passing exactly that —
  // "sequence or action generation is invalid" against wsgm.native-qam.perf/updateSettings,
  // steam-network.gate/startScan and stopScan, and steam-bluetooth.service/setDiscovering, on the
  // reference device on 2026-08-30. Every Valve performance control's write, and every signal that
  // Steam's network page had started looking for networks, was dropped by the bridge before WSGM
  // ever saw it — which is why the Wi-Fi list never filled: WSGM was never told to scan.
  //
  // Zero was meant as "no user-initiated row action here", which is true of a gate. Rather than
  // repeat the counter at each such call site, an absent or non-positive generation is allocated
  // one here, so no caller can construct an invalid envelope at all.
  const gateActionGenerations = new Map<string, number>();
  const validActionGeneration = (patchId, actionGeneration) => {
    if (Number.isInteger(actionGeneration) && actionGeneration > 0) return actionGeneration;
    const next = (gateActionGenerations.get(patchId) || 0) + 1;
    gateActionGenerations.set(patchId, next);
    return next;
  };
  // The generation is optional: a gate has no user-initiated row action to number, and one is
  // allocated for it above. Row controls pass their own so an echo can be matched to the write.
  const request = (patchId, command, payload, requestedGeneration?: number) => {
    if (!allowed(patchId, command)) return Promise.reject(new Error("command not allowlisted"));
    if (pending.size >= config.maximumPending) return Promise.reject(new Error("bridge busy"));
    const actionGeneration = validActionGeneration(patchId, requestedGeneration);
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

  // Stamped on every namespace WSGM defines on SteamClient, so a later probe can tell OUR namespace
  // from a real backend. Without it the two are indistinguishable and the compatibility check reads
  // its own successful install as "a native backend exists", refuses, and tears the patch down —
  // which is exactly what left this client with an empty audio page and a crashing Performance tab.
  //
  // A string key rather than a Symbol: it has to survive being read back from a probe evaluated in
  // a separate CDP call, where a Symbol from this scope is not reachable.
  const ownedMarker = "__wsgmOwnedNamespace";

  // The same idea one level down: a method WSGM overlaid rather than a namespace it defined. The
  // second key carries the method that was replaced, so an overlay outliving the closure that made
  // it can still be unwound back to the client's own.
  const ownedGetStateMarker = "__wsgmOwnedGetState";
  const originalGetStateField = "__wsgmOriginalGetState";

  const audioNamespace = createAudioNamespace();
  const networkGate = createNetworkGate();
  const bluetoothService = createBluetoothService();
  const brightnessGate = createBrightnessGate();
  const steamOsManagerGate = createSteamOsManagerGate();
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
    steamOsManager: Object.freeze({
      install: steamOsManagerGate.install,
      remove: steamOsManagerGate.remove,
      status: steamOsManagerGate.status,
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

    // The message class is never named here — it is taken from an instance the store builds, so
    // this stays correct across minification and client updates. An object argument is still
    // accepted because that is what a caller other than the store would pass, and an
    // undecodable one is forwarded as-is so WSGM logs a readable rejection instead of nothing.
    const decodeSettingsUpdate = (payload) => {
      if (typeof payload !== "string") return payload?.toObject?.() ?? payload ?? {};
      try {
        const constructor = store()?.CreateSettingsUpdateRequest?.()?.constructor;
        if (typeof constructor?.deserializeBinary !== "function") {
          lastError = "settings update could not be decoded: no deserializeBinary";
          return {};
        }

        const binary = atob(payload);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index += 1) {
          bytes[index] = binary.charCodeAt(index);
        }

        return constructor.deserializeBinary(bytes).toObject();
      } catch (error) {
        lastError = "settings update could not be decoded: " + String(error);
        return {};
      }
    };

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

      // Same rule as the audio namespace: stand aside for a real backend, reclaim one of our own.
      // An orphaned Perf namespace is worse than an orphaned audio one — it leaves SystemPerfStore
      // holding half-written state, which renders Valve's controls with no values behind them.
      if (system.Perf && !system.Perf[ownedMarker]) {
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
        // Decode first, always. SystemPerfStore's setters all end in
        // `UpdateSettings(request.serializeBase64String())`, so what arrives here is a BASE64
        // STRING, not the message — live-verified 2026-08-30 by round-tripping a request built by
        // the store itself. Forwarding it verbatim made WSGM's reader reject every write as
        // "carried no delta object", which is why no control on the Performance tab did anything:
        // the overlay-level selector snapped back to off, the frame cap never took, VRR never
        // toggled. Decoding through the message's OWN deserializeBinary keeps the wire format the
        // client's business; toObject() then emits snake_case field names, which is what WSGM reads.
        UpdateSettings: (payload) =>
          request(patchId, "updateSettings", { delta: decodeSettingsUpdate(payload) }, 0),
        RegisterForStateChanges: () => ({ unregister: () => {} }),
        RegisterForDiagnosticInfoChanges: () => ({ unregister: () => {} }),
      };

      try {
        // Non-enumerable so it never shows up in a key walk of the namespace, and defined before
        // the namespace is published so nothing can observe an unmarked one.
        Object.defineProperty(api, ownedMarker, {
          value: true,
          configurable: true,
          enumerable: false,
        });
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
  // The SteamOS Manager seam, which is what puts Valve's own TDP row on the Performance tab. The
  // row binds two CLIENT SETTINGS (steamos_tdp_limit_enabled, steamos_tdp_limit — Steam persists
  // them itself) and hides behind is_tdp_limit_available from the Manager service's GetState,
  // cached by react-query with staleTime Infinity under ["SteamOSService","State","Manager"].
  //
  // So the gate does three things and no more: overlay the Manager GetState answer with the TDP
  // fields, sourced from the same published state the hand-rolled row used; invalidate that query
  // key when the state changes; and watch the one setting Valve writes so the chosen watts reach
  // the device through the existing setPrimaryLimit command. Valve owns the row, the storage and
  // the write UI — WSGM answers one RPC and observes one number. Live-mapped 2026-08-30: stub
  // export Bd beside the Telemetry service, own-writable GetState, body nested under `state`.
  function createSteamOsManagerGate() {
    const patchId = "wsgm.native-qam.tdp";
    const queryKey = ["SteamOSService", "State", "Manager"];
    let installed = false;
    let lastError = "";
    let unsubscribe: (() => void) | null = null;
    let unsubscribeSettings: (() => void) | null = null;
    let originalGetState: any = null;
    let manager: any = null;
    let latest: { available: boolean; min: number; max: number } = {
      available: false,
      min: 0,
      max: 0,
    };
    let lastSentWatts: number | null = null;
    let lastSentEnabled: boolean | null = null;
    // One forward in flight at a time. The timer ticks every second and the host's own command
    // budget is longer than that, so without this a slow write would be re-sent underneath itself.
    let forwarding = false;

    const modules = () => {
      let req;
      window.webpackChunksteamui.push([
        ["wsgm_steamos_" + Date.now()],
        {},
        (r) => {
          req = r;
        },
      ]);
      return req;
    };

    // The Manager service, found structurally: the export whose surface has GetState and the
    // screen-reader refresh no other service carries. Its sibling GV (Telemetry) also has
    // GetState, which is why a bare GetState match is not enough.
    const findManager = (req) => {
      for (const value of Object.values(req?.("90389") ?? {})) {
        if (
          value &&
          typeof value === "object" &&
          typeof (value as any).GetState === "function" &&
          typeof (value as any).RefreshScreenReaderAutoLocale === "function"
        ) {
          return value;
        }
      }
      return null;
    };

    const invalidate = (req) => {
      try {
        req?.("21371")?.L?.invalidateQueries({ queryKey });
      } catch {
        // A moved query layer keeps the stale answer; the row simply does not appear.
      }
    };

    const onState = (state) => {
      if (!installed || !state) return;
      latest = {
        available: state.available === true && Number(state.minimumWatts) > 0,
        min: Number(state.minimumWatts) || 0,
        max: Number(state.maximumWatts) || 0,
      };
      invalidate(modules());
    };

    // Valve's TDP rows do not call a namespace. The toggle and the slider are bound to the
    // steamos_tdp_limit_enabled and steamos_tdp_limit CLIENT SETTINGS, Steam persists them, and
    // WSGM's job is to notice the number and route it to hardware.
    //
    // Read from the settings store rather than from a change payload. Live-verified 2026-08-30:
    // Valve's own hooks read (0,a.q3)(() => G.clientSettings[name]) off the store reachable as
    // window.settingsStore, so that IS the value the rows are showing. Guessing at the shape of
    // whatever RegisterForSettingsChanges hands back would have been a second, weaker source for
    // the same fact.
    const readSettings = () => {
      try {
        const settings = window.settingsStore?.clientSettings;
        if (!settings) return null;
        const watts = Number(settings.steamos_tdp_limit);
        return {
          watts: Number.isInteger(watts) && watts > 0 ? watts : null,
          enabled: settings.steamos_tdp_limit_enabled === true,
        };
      } catch {
        return null;
      }
    };
    const forwardSettings = () => {
      const now = readSettings();
      if (!now) return;
      if (now.enabled === lastSentEnabled && now.watts === lastSentWatts) return;
      if (forwarding) return;
      forwarding = true;
      // The enabled flag rides along: a limit switched off is not the same as a limit of zero
      // watts, and WSGM has to release the cap rather than try to apply one.
      request(patchId, "setPrimaryLimit", { watts: now.watts ?? 0, enabled: now.enabled }).then(
        () => {
          // Latched on SUCCESS, never on the attempt. Recording the value before the answer meant a
          // forward that failed — a host not ready yet, a bridge busy, a refusal — was remembered
          // as sent and never tried again, so the limit stayed where it was with the row showing
          // the number the user had chosen. The timer is what retries; this is what lets it.
          lastSentEnabled = now.enabled;
          lastSentWatts = now.watts;
          forwarding = false;
        },
        (error) => {
          lastError = "power limit forward failed: " + String(error);
          forwarding = false;
        },
      );
    };
    const watchSettings = () => {
      // Steam's own change notification is the trigger, and a slow timer is the safety net. The
      // notification fires on the settings Steam persists, but its payload shape is Steam's and a
      // release that changes it must not silently strand the power limit — which is the failure
      // this whole surface exists to avoid. Both ends call the same reader, and forwardSettings
      // only sends on an actual change, so the timer costs two property reads a second.
      try {
        const handle = window.SteamClient?.Settings?.RegisterForSettingsChanges?.(() =>
          forwardSettings(),
        );
        if (handle && typeof handle.unregister === "function") {
          unsubscribeSettings = () => handle.unregister();
        }
      } catch (error) {
        // The row still renders and Steam still persists the setting; only the routing to
        // hardware is lost, and the status says so.
        lastError = "settings watch unavailable: " + String(error);
      }

      const timer = setInterval(forwardSettings, 1000);
      const stopNotification = unsubscribeSettings;
      unsubscribeSettings = () => {
        clearInterval(timer);
        if (stopNotification) stopNotification();
      };

      // The rows show what Steam persisted, so the hardware has to be brought to it rather than
      // the other way round: without this a limit set in a previous session stays on screen and
      // off the device until the user happens to move the slider.
      forwardSettings();
    };

    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const req = modules();
      manager = findManager(req);
      if (!manager) {
        lastError = "SteamOS Manager service stub unavailable";
        return { ok: false, error: lastError };
      }

      // Never wrap our own wrapper. A bridge replaced in place — a new asset hash, a reinstall
      // after a probe — leaves the previous overlay on the service with its closure gone, and
      // nesting a second one would make removal restore a wrapper instead of Valve's method,
      // leaving Steam overlaid for the rest of its life. The overlay therefore carries Valve's
      // method on itself, so a fresh closure can unwrap back to it and replace rather than stack.
      //
      // Refusing instead would be the same self-incompatibility trap the Perf and Audio namespaces
      // already paid for: a successful install would make the next probe declare the patch
      // incompatible, tearing down what it had just done.
      const existing = manager.GetState;
      originalGetState =
        existing?.[ownedGetStateMarker] === true ? existing[originalGetStateField] : existing;
      if (typeof originalGetState !== "function") {
        lastError = "SteamOS Manager GetState is not recoverable";
        return { ok: false, error: lastError };
      }
      const overlaid = async (payload) => {
        // The original answer is kept and overlaid, never replaced: it carries real fields —
        // screen-reader support among them — that a fabricated reply would silently zero.
        const result = await originalGetState.call(manager, payload ?? {});
        try {
          const body = result?.Body?.()?.toObject?.();
          if (!body || !body.state) return result;
          const merged = {
            ...body,
            state: {
              ...body.state,
              is_tdp_limit_available: latest.available,
              tdp_limit_min: latest.min,
              tdp_limit_max: latest.max,
            },
          };
          return {
            BSuccess: () => true,
            BFailed: () => false,
            GetEResult: () => 1,
            Body: () => ({ ...merged, toObject: () => merged }),
          };
        } catch {
          return result;
        }
      };
      Object.defineProperty(overlaid, ownedGetStateMarker, {
        value: true,
        configurable: true,
        enumerable: false,
      });
      Object.defineProperty(overlaid, originalGetStateField, {
        value: originalGetState,
        configurable: true,
        enumerable: false,
      });
      manager.GetState = overlaid;

      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      watchSettings();
      invalidate(req);
      return { ok: true, installed: true };
    };

    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      if (unsubscribeSettings) {
        unsubscribeSettings();
        unsubscribeSettings = null;
      }
      if (manager && originalGetState) {
        try {
          manager.GetState = originalGetState;
        } catch (error) {
          lastError = String(error);
          return { ok: false, error: lastError };
        }
      }
      latest = { available: false, min: 0, max: 0 };
      invalidate(modules());
      return { ok: true, removed: true };
    };

    const status = () => ({
      ok: true,
      installed,
      managerFound: !!manager,
      // What the C# verify step checks. "installed" alone is this closure's own bookkeeping; this
      // is the client actually carrying the overlay.
      getStateOverlaid: manager?.GetState?.[ownedGetStateMarker] === true,
      settingsWatched: unsubscribeSettings !== null,
      available: latest.available,
      min: latest.min,
      max: latest.max,
      // What the host ACCEPTED, not what was attempted, and what Steam has stored beside it. The
      // pair is the diagnosis: two different numbers mean the forward is failing, and lastError
      // says how.
      lastSentWatts,
      lastSentEnabled,
      storedSettings: readSettings(),
      lastError,
    });

    return { install, remove, status };
  }

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
        // Marked as ours for the same reason the namespaces are: the compatibility probe checks
        // that the getter currently reads false, and a successful override makes it read true. Left
        // unmarked, the patch reads its own success as "the client already reports this available,
        // stand aside", declares itself incompatible, and tears down — taking the network list with
        // it.
        const owned = () => true;
        Object.defineProperty(owned, "__wsgmOwnedGetter", {
          value: true,
          configurable: true,
          enumerable: false,
        });
        Object.defineProperty(proto, property, { get: owned, configurable: true });
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

    let known: number[] = [];
    // Steam's audio identities are NUMBERS: the live store keeps m_activeOutputDeviceId as a
    // uint32 with 0xFFFFFFFF for none (read off the running client, 2026-08-30). WSGM's endpoint
    // ids are Windows GUID strings, so devices listed by name but nothing could ever match as
    // active — which reads as "no default device" and disables the volume slider. Each GUID gets a
    // stable small number for Steam's side of the wire, translated back on every command.
    const NO_DEVICE = 4294967295;

    // The key m_mapVolumes is keyed by, and the second argument of both SetDeviceVolume and
    // OnAudioDeviceVolumeChanged. Named because it was silently confused with the volume itself in
    // both directions: as a map key it left the slider with no value, and as a volume it turned
    // every drag into 100% or 0%.
    const AudioDirection = Object.freeze({ Output: 0, Input: 1 });

    // Below one step of a hardware volume button, so a genuine press always counts and float
    // round-tripping through a whole-number percent never does.
    const VolumeEpsilon = 0.004;
    const deviceNumbers = new Map();
    const deviceGuids = new Map();
    let nextDeviceNumber = 1;
    const numberFor = (guid) => {
      if (typeof guid !== "string" || !guid) return NO_DEVICE;
      let value = deviceNumbers.get(guid);
      if (value === undefined) {
        value = nextDeviceNumber++;
        deviceNumbers.set(guid, value);
        deviceGuids.set(value, guid);
      }
      return value;
    };
    const guidFor = (value) => deviceGuids.get(Number(value)) ?? null;

    // The store's device constructor ingests flOutputVolume/flInputVolume (0..1) into the map the
    // sliders bind — omit them and every slider renders a grey bar over undefined. WSGM's volume is
    // system-wide, so every device carries the same value; Windows' default endpoint is the one the
    // user actually hears, and a per-device number WSGM cannot move would be an invented control.
    const toDevice = (entry, flVolume) => ({
      id: numberFor(entry.id),
      sName: entry.name,
      bHasOutput: entry.hasOutput === true,
      bHasInput: entry.hasInput === true,
      flOutputVolume: flVolume,
      flInputVolume: flVolume,
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

    // The one volume WSGM tracks, as the 0..1 float Steam's sliders use.
    const flVolumeOf = (state) =>
      Math.min(1, Math.max(0, (Number(state?.volumePercent) || 0) / 100));

    // Volume-changed dispatches fire ONLY when the volume moved. Steam shows its volume OSD on
    // every dispatch, and firing one per publish made the OSD pop up over and over while nothing
    // had changed. Null means no volume has been reported yet, so the first publish never counts
    // as a change either — construction already carries it.
    let lastFlVolume: number | null = null;

    const onState = (state) => {
      if (!installed || !state || !Array.isArray(state.devices)) return;
      const flVolume = flVolumeOf(state);
      const volumeChanged =
        lastFlVolume !== null && Math.abs(flVolume - lastFlVolume) > VolumeEpsilon;
      lastFlVolume = flVolume;
      // Numeric, because these ids flow to the store and its callbacks, and Steam's side of the
      // wire is numeric everywhere.
      const seen = state.devices.map((device) => numberFor(device.id));

      // Removals first: a device that has gone must leave the store before a re-read of the device
      // list can describe the set as complete, or the picker keeps an endpoint that is not there.
      for (const id of known) {
        if (!seen.includes(id) && callbacks.deviceRemoved) callbacks.deviceRemoved(id as never);
      }
      for (const device of state.devices) {
        if (callbacks.deviceAdded) callbacks.deviceAdded(toDevice(device, flVolume));
        // (deviceId, DIRECTION, volume) — in that order. Read off the store's own methods
        // 2026-08-30: OnAudioDeviceVolumeChanged(e,t,r) forwards to OnVolumeUpdated(t,r), which is
        // m_mapVolumes.set(t, r). The direction is the KEY and the volume is the VALUE, and WSGM
        // was passing them the other way round — every entry it wrote was keyed by a float volume
        // with 1 or 0 as its value, so getDeviceVolume(direction) found nothing and the slider had
        // no number to sit on.
        //
        // Still gated on an actual change, unlike the direct path below, which also has to seed:
        // a store that registered these callbacks was constructed after the namespace existed and
        // therefore already read the volumes at construction.
        if (volumeChanged && callbacks.deviceVolumeChanged) {
          const id = numberFor(device.id);
          callbacks.deviceVolumeChanged(id as never, AudioDirection.Output as never, flVolume as never);
          callbacks.deviceVolumeChanged(id as never, AudioDirection.Input as never, flVolume as never);
        }
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
        for (const device of state.devices) {
          store.RegisterOrUpdateDevice(toDevice(device, flVolume));
          // Update() copies the name, the directions and the CEC flags and nothing else — read
          // live 2026-08-30 — so registration never fills m_mapVolumes and this is its only path.
          //
          // But writing on every publish is wrong in both directions at once. It dispatches a
          // volume change once a second, which is Steam's OSD popping up forever; and while the
          // user is dragging, the store is already holding the value they chose, so pushing WSGM's
          // not-yet-observed one snaps the handle back under their thumb.
          //
          // So: seed a direction that has no value at all, and otherwise write only when WSGM's
          // OWN reading moved — something outside Steam changed the volume — and the store has not
          // already caught up. Both are suppressed, because neither is the user acting inside
          // Steam: a hardware button already shows WSGM's own overlay.
          const deviceId = numberFor(device.id);
          const entry = store.m_mapAudioDevices?.get(deviceId);
          for (const direction of [AudioDirection.Output, AudioDirection.Input]) {
            const held = entry?.getDeviceVolume?.(direction);
            const seeding = typeof held !== "number";
            if (!seeding && !(volumeChanged && Math.abs(held - flVolume) > VolumeEpsilon)) {
              continue;
            }

            store.SuppressVolumeOverlay?.();
            try {
              store.OnAudioDeviceVolumeChanged?.(deviceId, direction, flVolume);
            } finally {
              // Balanced whatever the dispatch does: the pair is a refcount, and leaking one would
              // suppress the user's own volume overlay for the rest of the session.
              store.UnSuppressVolumeOverlay?.();
            }
          }
        }
        // The running store learns the defaults from nothing else: a store constructed before the
        // namespace existed has 0xFFFFFFFF in both, which the settings page renders as "no default
        // device" and a disabled volume slider.
        store.m_activeOutputDeviceId = numberFor(state.activeOutputDeviceId ?? "");
        store.m_activeInputDeviceId = numberFor(state.activeInputDeviceId ?? "");
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

      // Never replace a REAL backend. On a client that grows one, WSGM must stand aside rather than
      // shadow it with a projection of a different machine's audio.
      //
      // One WSGM already installed is a different case and must be reclaimed, not refused. A
      // namespace outlives the bridge that backs it — the bridge is a window property and dies with
      // the JS context, while SteamClient does not — so after a context reload an orphaned
      // namespace is left behind whose methods call into a bridge that is gone. Refusing there
      // stranded the client permanently: the probe saw a namespace, called the patch incompatible,
      // and Steam's audio page stayed empty until Steam itself restarted.
      if (system.Audio && !system.Audio[ownedMarker]) {
        lastError = "SteamClient.System.Audio already exists";
        return { ok: false, error: lastError };
      }

      const api = {
        GetDevices: () =>
          request(patchId, "getDevices", null, 0).then((state: any) => ({
            activeOutputDeviceId: numberFor(state?.activeOutputDeviceId ?? ""),
            activeInputDeviceId: numberFor(state?.activeInputDeviceId ?? ""),
            overrideOutputDeviceId: NO_DEVICE,
            overrideInputDeviceId: NO_DEVICE,
            vecDevices: Array.isArray(state?.devices)
              ? state.devices.map((device) => toDevice(device, flVolumeOf(state)))
              : [],
          })),
        // Empty until a session mixer exists. Steam then lists no per-application entries, which is
        // the honest outcome rather than inventing volumes it cannot move.
        GetApps: () => Promise.resolve({ rgApps: [] }),
        SetDefaultDeviceOverride: (id, direction) => {
          // Steam hands back the number this side minted; the host only knows the GUID.
          const guid = guidFor(id);
          if (!guid) return Promise.resolve();
          return request(patchId, "setDefaultDevice", {
            id: guid,
            input: direction === AudioDirection.Input,
          });
        },
        // (deviceId, DIRECTION, volume) — three arguments. Read off the store's own device class
        // 2026-08-30: setDeviceVolume(e,t) calls SetDeviceVolume(this.m_id, e, t). WSGM declared
        // two parameters and so read the DIRECTION as the volume: dragging the slider sent
        // Math.round(1 * 100) or Math.round(0 * 100), which is why every drag set 100% or 0% and
        // the log showed "Taskbar volume set to 100%" the moment the slider was touched.
        //
        // Only the output direction is forwarded. WSGM's backend moves the default endpoint's
        // volume, which is the output one; forwarding the microphone's slider to it would have the
        // two controls fight over the same number.
        SetDeviceVolume: (id, direction, volume) => {
          if (direction !== AudioDirection.Output) return Promise.resolve();
          return request(patchId, "setVolume", {
            percent: Math.round(Math.min(1, Math.max(0, Number(volume) || 0)) * 100),
          });
        },
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
        Object.defineProperty(api, ownedMarker, {
          value: true,
          configurable: true,
          enumerable: false,
        });
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
    let vrrControl;

    // Valve's own VRR control, rendered rather than rebuilt. It reads its state from
    // SystemPerfStore and writes through SteamClient.System.Perf.UpdateSettings, both of which WSGM
    // now supplies, so mounting it needs no props and no shim of its own — which is the entire
    // point of reactivating a component instead of hand-building a row that looks like it.
    let valveVrrControl;

    // Valve's profile header, which carries the per-game profile toggle inside it — probed
    // 2026-08-30: the toggle is not a separately mountable export, so the two arrive together or
    // not at all. And Valve's reset button. Both are additive: WSGM built neither.
    let valveProfileHeaderControl;
    let valveResetControl;
    let valveRefreshRateControl;
    let valveFrameLimitControl;
    let valveOverlayLevelControl;

    // Valve's power-limit pair. They arrive as two exports, not one row: the toggle reveals the
    // slider through the steamos_tdp_limit_enabled setting, which is how SteamOS models "off" for
    // this control and why the slider has no zero position.
    let valveTdpToggleControl;
    let valveTdpSliderControl;
    let performanceRoot;

    // The Quick Settings panel Steam rendered, captured at match time. S14 puts resolution and
    // refresh rate in Quick Settings, not Performance — but the panel is a LOCAL function of the
    // tabs module, not an export, so it is only ever known once the tab array passes through the
    // patched memo. Null means it has not been seen yet, which the status reports.
    let quickSettingsRoot = null;
    const quickSettingsWrapCache = new Map();
    let originalUseMemo;
    let patchedUseMemo;
    let disposedHost = false;

    // What the last append attempt actually did, surfaced through status(). Without it a panel that
    // inserted nothing was indistinguishable from a bridge that never ran.
    type AppendDiagnostics = {
      controls: number;
      inserted: boolean;
      ownSection: boolean;
      tree?: string;
      nativeFiltered?: boolean;
      nativeRowsHidden?: number;
    } | null;
    // One entry per wrapped tab, because "the perf panel appended fine" and "Quick Settings never
    // rendered" are different facts that a single field could only report as one.
    const appendDiagnostics: { perf: AppendDiagnostics; quickSettings: AppendDiagnostics } = {
      perf: null,
      quickSettings: null,
    };

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
      // Two commands, because this is SteamOS's unified row: one slider that is the frame cap while
      // a cap is set and the refresh rate once it is switched off.
      frameLimit: Object.freeze({
        patchId: "wsgm.native-qam.frame-limit",
        command: "setFrameLimit",
        refreshCommand: "setRefreshRate",
      }),
      overlayLevel: Object.freeze({
        patchId: "wsgm.native-qam.overlay-level",
        command: "setOverlayLevel",
      }),
      controllerTarget: Object.freeze({
        patchId: "wsgm.native-qam.controller-target",
        command: "setControllerTarget",
      }),
      // Hand-built for the same reason resolution is: Valve ships a component, and its gate is a
      // namespace this client does not have. See createVrrControl.
      vrr: Object.freeze({
        patchId: "wsgm.native-qam.vrr",
        command: "setVariableRefreshRate",
      }),
      // Hand-built, unlike the frame limit and VRR rows. SteamOS drives resolution through
      // gamescope and this client ships no component for it, so there is nothing to mount.
      resolution: Object.freeze({
        patchId: "wsgm.native-qam.resolution",
        command: "setResolution",
      }),

      // Valve's own components. They carry no command because they never call WSGM directly: they
      // read SystemPerfStore and write through SteamClient.System.Perf.UpdateSettings, which is the
      // perf patch's vocabulary, not theirs. They still need an entry here — install() refuses any
      // kind that is not a declared definition.
      valveVrr: Object.freeze({
        patchId: "wsgm.native-qam.valve-vrr",
        command: "",
      }),
      valveProfileHeader: Object.freeze({
        patchId: "wsgm.native-qam.valve-profile-header",
        command: "",
      }),
      valveReset: Object.freeze({
        patchId: "wsgm.native-qam.valve-reset",
        command: "",
      }),
      // Valve's own refresh-rate row, mounted into Quick Settings per S14. It reads
      // limits.display_refresh_manual_hz_* from SystemPerfStore, which the projection supplies only
      // under FrameLimitOnly — the strategy gate is the state, not a check here.
      valveRefreshRate: Object.freeze({
        patchId: "wsgm.native-qam.valve-refresh-rate",
        command: "",
      }),
      // Valve's frame-limit slider and performance-overlay selector, replacing the hand-rolled
      // rows that imitated them — the retirement Q12 always intended once the Perf backend could
      // feed the real components.
      valveFrameLimit: Object.freeze({
        patchId: "wsgm.native-qam.valve-frame-limit",
        command: "",
      }),
      valveOverlayLevel: Object.freeze({
        patchId: "wsgm.native-qam.valve-overlay-level",
        command: "",
      }),
      // Valve's own power-limit toggle and slider, in place of the hand-rolled row. They carry no
      // command for the same reason the rows above do not: they write the steamos_tdp_limit client
      // settings, which the SteamOS Manager gate watches and forwards.
      valveTdp: Object.freeze({
        patchId: "wsgm.native-qam.valve-tdp",
        command: "",
      }),
    });

    // Which tab each kind renders in. Everything defaults to the Performance panel; Quick Settings
    // holds the display controls S14 puts there. A kind listed nowhere renders nowhere, which is
    // the honest failure for a typo.
    const quickSettingsKinds = new Set(["resolution", "valveRefreshRate"]);

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
    // Deliberately small. Everything the row needs is a switch position and a reason, because the
    // device capability behind it answers in exactly those terms.
    const normalizeVrrState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (typeof value.enabled !== "boolean") return null;
      return Object.freeze({
        available: value.available,
        enabled: value.enabled,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
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
        // Zero is OFF and is deliberately outside the slider's range, which now starts at a cap
        // worth playing at. Rejecting it here would have thrown away every state in which the user
        // has no cap set — which is the default one.
        (desiredFps !== null &&
          desiredFps !== 0 &&
          (!Number.isInteger(desiredFps) ||
            minimumFps === null ||
            maximumFps === null ||
            desiredFps < minimumFps ||
            desiredFps > maximumFps)) ||
        (observedFps !== null &&
          observedFps !== 0 &&
          (!Number.isInteger(observedFps) ||
            minimumFps === null ||
            maximumFps === null ||
            observedFps < minimumFps ||
            observedFps > maximumFps)) ||
        (common.available && minimumFps === null)
      )
        return null;

      // Cap to refresh rate, for the "(60 Hz)" half of the label. Absent under the uncoupled
      // strategy, where a cap moves no display mode and there is nothing to name.
      const refreshForCap = new Map<number, number>();
      if (value.refreshForCap && typeof value.refreshForCap === "object") {
        for (const [cap, hz] of Object.entries(value.refreshForCap)) {
          const capValue = Number(cap);
          const hzValue = Number(hz);
          if (Number.isInteger(capValue) && Number.isInteger(hzValue) && hzValue > 0) {
            refreshForCap.set(capValue, hzValue);
          }
        }
      }
      const refreshMinHz = value.refreshMinHz === null ? null : Number(value.refreshMinHz);
      const refreshMaxHz = value.refreshMaxHz === null ? null : Number(value.refreshMaxHz);
      const currentRefreshHz =
        value.currentRefreshHz === null ? null : Number(value.currentRefreshHz);
      // The refresh half is a pair like the cap half, and it is OPTIONAL: a display that offers no
      // rates leaves the row with only its frame-limit mode rather than rejecting the state.
      // The stops the refresh mode slides between. Windows takes a MODE or refuses: a panel that
      // has 60 and 75 does not have 72, so this mode is notched to exactly what the display
      // accepted, unlike the frame cap, where the limiter really does hold any integer.
      const refreshRates: number[] = [];
      if (Array.isArray(value.refreshRates)) {
        for (const item of value.refreshRates) {
          const hz = Number(item);
          if (Number.isInteger(hz) && hz > 0 && !refreshRates.includes(hz)) refreshRates.push(hz);
        }
        refreshRates.sort((left, right) => left - right);
      }
      const refreshUsable =
        refreshRates.length > 0 &&
        refreshMinHz !== null &&
        refreshMaxHz !== null &&
        currentRefreshHz !== null &&
        Number.isInteger(refreshMinHz) &&
        Number.isInteger(refreshMaxHz) &&
        Number.isInteger(currentRefreshHz) &&
        refreshMinHz > 0 &&
        refreshMaxHz >= refreshMinHz;
      return Object.freeze({
        ...common,
        minimumFps,
        maximumFps,
        desiredFps,
        observedFps,
        limitEnabled: value.limitEnabled === true,
        refreshForCap,
        refreshMinHz: refreshUsable ? refreshMinHz : null,
        refreshMaxHz: refreshUsable ? refreshMaxHz : null,
        currentRefreshHz: refreshUsable ? currentRefreshHz : null,
        refreshRates: refreshUsable ? Object.freeze(refreshRates) : Object.freeze([]),
      });
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
    // EVERY label goes through this, not only the WSGM-invented ones. With the rows finally
    // rendering on the reference Claw, "#QuickAccess_Tab_Perf_FramerateLimit" and
    // "#QuickAccess_Tab_Perf_PerfOverlayLevel" both came back raw and were shown to the user as
    // their token text. A bare localize() call here is a bug waiting for the next missing string.
    //
    // Live-probed 2026-08-30, which found the reason: neither token exists anywhere in the bundle.
    // They were never SteamOS strings absent from the Windows set — they were wrong names. The
    // client carries "#QuickAccess_Tab_Perf_LimitFrameRate" and "#QuickAccess_Tab_Perf_Overlay_Level",
    // and those localize. Both call sites now use the real names, so those two rows are translated
    // rather than permanently English.
    //
    // The fallback still earns its place, for the labels WSGM invents and Valve has no string for
    // (AutoTDP, the display-resolution row). Those pass no token at all rather than a plausible
    // one: a token that does not exist makes Steam log an unresolved string on every render and
    // still shows the English text.
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
    // WSGM's own variable-refresh switch. Valve ships one, and it cannot be used: its component is
    // gated on a react-query over SteamClient.System.DisplayManager, whose GetState this client
    // does not define — the query never succeeds and the component returns null before it reads a
    // single field WSGM publishes (live-probed 2026-08-30). The device capability behind this row
    // is the one already verified on the reference unit through IGCL Arc Sync.
    const createVrrControl = (controlRuntime) =>
      function WsgmNativeVrrControl() {
        const state = useSemanticState(controlRuntime, "vrr", normalizeVrrState);
        if (!state) return note("vrr", "no state");
        if (!state.available) return note("vrr", "unavailable: " + (state.statusText || "no reason"));
        if (!controlRuntime.toggle) return note("vrr", "Steam ToggleField was not resolved");
        renderOutcomes.vrr = "rendered";
        const definition = definitions.vrr;
        return controlRuntime.react.createElement(controlRuntime.toggle, {
          // Valve's own token for the row, so the label matches the client's language even though
          // the component behind it is WSGM's.
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_EnableVRR",
            "Variable refresh rate",
          ),
          description: state.statusText || undefined,
          checked: state.enabled,
          // Controlled: the switch shows what the device reports, so a write the panel refuses
          // leaves it where the hardware actually is rather than where it was clicked.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: (enabled) => {
            if (typeof enabled !== "boolean" || enabled === state.enabled) return;
            void request(
              definition.patchId,
              definition.command,
              { enabled },
              nextActionGeneration(definition.patchId),
            ).catch(() => {});
          },
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
          // WSGM's own control; Valve has no string for it, so no token is passed.
          label: "Automatic TDP",
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
          // Not localized, deliberately. The client has no token meaning "display resolution":
          // #Settings_Display_GameResolution is a per-game override and would read wrongly in every
          // language but English. Passing a token that does not exist is worse than passing none —
          // it makes Steam log an unresolved token on every render and still shows this string.
          label: "Display resolution",
          rgOptions: options,
          // A current mode outside the offered list selects nothing rather than the first entry,
          // which would silently misreport what the display is doing.
          selectedOption: state.options.includes(state.current) ? state.current : undefined,
          onChange: setResolution,
          description: state.statusText || undefined,
          layout: "below",
        });
      };
    // Which notch the display is currently sitting on. A rate that is not one of the listed modes —
    // something else can leave the panel on one — takes the nearest notch at or below it rather
    // than snapping the handle to the start and reporting a rate the display is not at.
    const currentRefreshNotch = (state) => {
      if (!state || !state.refreshRates || state.refreshRates.length === 0) return null;
      const current = state.currentRefreshHz;
      if (!Number.isInteger(current)) return null;
      let notch = 0;
      for (let index = 0; index < state.refreshRates.length; index += 1) {
        if (state.refreshRates[index] <= current) notch = index;
      }
      return notch;
    };
    const createFrameLimitControl = (controlRuntime) =>
      function WsgmNativeFrameLimitControl() {
        const state = useSemanticState(controlRuntime, "frameLimit", normalizeFrameLimitState);
        const value = state ? (state.observedFps ?? state.desiredFps) : null;
        const echoed = useEchoedValue(controlRuntime, value);
        // Its own echo, because the two modes are two different numbers on one slider: reusing one
        // would make the handle jump to a frame cap the moment the rate mode took over. It echoes
        // the notch INDEX, which is what a notch slider reports while it is being dragged.
        // Unconditional, ahead of every early return — these are hooks.
        const refreshEchoed = useEchoedValue(controlRuntime, currentRefreshNotch(state));
        if (!state) return note("frameLimit", "no state");
        if (!state.available)
          return note("frameLimit", "unavailable: " + (state.statusText || "no reason"));
        if (value === null) return note("frameLimit", "no observed or desired fps");
        renderOutcomes.frameLimit = "rendered";
        const definition = definitions.frameLimit;
        const send = (command, nextValue) =>
          void request(
            definition.patchId,
            command,
            { value: nextValue, persistence: "automatic" },
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        const setCap = (nextValue) => {
          if (
            !Number.isInteger(nextValue) ||
            nextValue < state.minimumFps ||
            nextValue > state.maximumFps
          )
            return;
          send(definition.command, nextValue);
        };
        // Takes a NOTCH INDEX, not a rate: the refresh mode is a notch slider, so what the control
        // hands back is a position in the accepted list.
        const setRefresh = (notchIndex) => {
          const hz = state.refreshRates[notchIndex];
          if (!Number.isInteger(hz)) return;
          send(definition.refreshCommand, hz);
        };

        // Off is zero, and the slider never shows it: the cap the user chose has to survive being
        // switched off and back on, so the switch below writes zero and the slider keeps sitting
        // where it was. That is how SteamOS's own "Disable Frame Limit" behaves next to its Frame
        // Limit slider, and it is why the slider can start at a cap worth playing at.
        const capped = state.limitEnabled && echoed.value > 0;
        const cappedValue = echoed.value > 0 ? echoed.value : (state.minimumFps ?? 0);
        // Recomputed every render, which is what makes it track a value still being dragged.
        const pairedHz = state.refreshForCap.get(cappedValue);

        // The row's second mode. With the cap off the slider IS the refresh rate — the whole reason
        // SteamOS merged the two rows is that they are one decision: the frame cap and the rate it
        // is presented at are the same frametime question, and vsync is what makes the pacing hold.
        // Switching the cap off does not leave a dead control behind, it hands the same slider over
        // to the rate.
        const refreshMode = !capped && state.refreshRates.length > 0;
        const sliderValue = refreshMode ? (refreshEchoed.value ?? 0) : cappedValue;
        // Guarded like the AutoTDP row: a client whose ToggleField cannot be located loses the
        // switch and keeps the slider, rather than losing the whole row silently.
        const disableSwitch = controlRuntime.toggle
          ? controlRuntime.react.createElement(controlRuntime.toggle, {
              // Not "#QuickAccess_Tab_Perf_LimitFrameRate_Off": that token is the notch slider's
              // first STOP and localizes to bare "Off" ("AUS"), which reads as a row with no
              // subject once it is a switch of its own. SteamOS names this switch outright.
              label: "Disable frame limit",
              description: refreshMode
                ? "The slider sets the refresh rate while the limit is off."
                : undefined,
              checked: !capped,
              controlled: true,
              disabled: isBusy(state.progress),
              // Turning it back on restores the cap the slider is already sitting on, so the
              // number the user was looking at is the one that takes effect.
              onChange: (next) => send(definition.command, next ? 0 : cappedValue),
            })
          : note("frameLimitSwitch", "Steam ToggleField was not resolved");
        const slider = controlRuntime.react.createElement(controlRuntime.slider, {
          // Live-verified 2026-08-30: these are tokens the client actually carries.
          // "#QuickAccess_Tab_Perf_FramerateLimit" appears nowhere in the bundle, so the row it was
          // written against fell back to English on every localized client.
          label: refreshMode
            ? localizeOr(controlRuntime, "#QuickAccess_Tab_Perf_RefreshRate", "Refresh rate")
            : localizeOr(
                controlRuntime,
                "#QuickAccess_Tab_Perf_LimitFrameRate",
                "Frame rate limit",
              ),
          // The two modes are two different sliders sharing one row. The frame cap is NOTCHLESS
          // under every strategy — the limiter holds any integer and the pairing is what snaps —
          // while the refresh rate is notched to exactly the modes the display accepted, because
          // Windows takes a mode or refuses and there is no continuum between 60 and 75.
          min: 0,
          max: refreshMode ? state.refreshRates.length - 1 : state.maximumFps,
          ...(refreshMode
            ? {
                notchCount: state.refreshRates.length,
                notchLabels: state.refreshRates.map((hz, notchIndex) => ({
                  notchIndex,
                  label: `${hz}`,
                  value: hz,
                })),
                notchTicksVisible: true,
              }
            : { min: state.minimumFps }),
          step: 1,
          value: sliderValue,
          // "60 FPS (60 Hz)" is how SteamOS's unified row names a cap and the rate it will be
          // presented at. In refresh mode the notch label already carries the number.
          valueSuffix: refreshMode ? " Hz" : pairedHz ? ` FPS (${pairedHz} Hz)` : " FPS",
          showValue: !refreshMode,
          showBookendLabels: !refreshMode,
          disabled: isBusy(state.progress),
          description: state.fault || state.statusText || undefined,
          onChange: refreshMode ? refreshEchoed.onChange : echoed.onChange,
          onChangeComplete: (next) =>
            refreshMode
              ? refreshEchoed.onChangeComplete(next, setRefresh)
              : echoed.onChangeComplete(next, setCap),
        });
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          slider,
          disableSwitch,
        );
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
            "#QuickAccess_Tab_Perf_Overlay_Level",
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

    const appendControls = (controlRuntime, tree, placement = "perf") => {
      // Rendered React elements from Steam's own untyped runtime.
      const controls: unknown[] = [];
      // A kind renders in exactly one tab. Registration says the host backs it; placement says
      // where it belongs, and the two are deliberately separate questions.
      const wants = (kind) =>
        registrations.has(kind) &&
        (quickSettingsKinds.has(kind) ? "quickSettings" : "perf") === placement;

      // First, because it names the profile everything below it edits. Valve's own header carries
      // the per-game toggle inside it, so mounting this is what gives the panel a per-application
      // profile concept at all.
      if (wants("valveProfileHeader") && valveProfileHeaderControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-profile-header" },
            controlRuntime.react.createElement(valveProfileHeaderControl),
          ),
        );
      }
      // The order below is the maintainer's, set on the device: overlay level, frame limit and its
      // switch, VRR, TDP with AutoTDP behind it, and the controller last. It reads from what you
      // look at while playing down to what you set once — not from the order the rows were built.

      // The overlay is what you turn on to judge everything under it, so it comes first.
      if (wants("valveOverlayLevel") && valveOverlayLevelControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-overlay-level" },
            controlRuntime.react.createElement(valveOverlayLevelControl),
          ),
        );
      }
      if (wants("overlayLevel")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-overlay-level" },
            controlRuntime.react.createElement(overlayLevelControl),
          ),
        );
      }
      // The unified row: one slider that is the frame cap while a cap is set and the refresh rate
      // once the switch beside it turns the cap off.
      if (wants("frameLimit")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-frame-limit" },
            controlRuntime.react.createElement(frameLimitControl),
          ),
        );
      }
      if (wants("valveFrameLimit") && valveFrameLimitControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-frame-limit" },
            controlRuntime.react.createElement(valveFrameLimitControl),
          ),
        );
      }
      // Directly under the frame limit, because variable refresh is the other answer to the same
      // question: hold a cadence by capping frames, or by letting the panel follow them.
      //
      // WSGM's own row, not Valve's. Valve's VRR component is gated on
      // SteamClient.System.DisplayManager, which this client does not have — its GetState returns
      // null, the query never succeeds, and the component returns null before it reads anything
      // WSGM publishes. Live-probed 2026-08-30; supplying that namespace is a separate piece of
      // work, and this row runs on the device capability that is already verified.
      if (wants("vrr")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-vrr" },
            controlRuntime.react.createElement(vrrControl),
          ),
        );
      }
      if (wants("valveVrr") && valveVrrControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-vrr" },
            controlRuntime.react.createElement(valveVrrControl),
          ),
        );
      }
      // Valve's own power-limit pair. Two rows, because that is how SteamOS models this control:
      // the toggle is "off" and the slider only appears behind it, which is why the slider has no
      // zero position. Both are gated on is_tdp_limit_available, which the SteamOS Manager gate
      // supplies — this row does not exist until that RPC answers.
      if (wants("valveTdp") && valveTdpToggleControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-tdp-enabled" },
            controlRuntime.react.createElement(valveTdpToggleControl),
          ),
        );
      }
      if (wants("valveTdp") && valveTdpSliderControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-tdp" },
            controlRuntime.react.createElement(valveTdpSliderControl),
          ),
        );
      }
      if (wants("tdp")) {
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
      if (wants("autoTdp")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-auto-tdp" },
            controlRuntime.react.createElement(autoTdpControl),
          ),
        );
      }
      if (wants("resolution")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-resolution" },
            controlRuntime.react.createElement(resolutionControl),
          ),
        );
      }
      // Valve's refresh-rate row follows resolution in Quick Settings: the two describe the same
      // display, and reading them apart would separate cause from consequence under the pairing
      // strategies.
      if (wants("valveRefreshRate") && valveRefreshRateControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-refresh-rate" },
            controlRuntime.react.createElement(valveRefreshRateControl),
          ),
        );
      }
      // Last of the performance controls, because it is the one setting that is not about this
      // session's frame pacing at all.
      if (wants("controllerTarget")) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-controller-target" },
            controlRuntime.react.createElement(controllerControl),
          ),
        );
      }

      // Last, because it undoes everything above it. A reset sitting among the controls it clears
      // is one mis-aimed press away from wiping a profile the user was in the middle of tuning.
      if (wants("valveReset") && valveResetControl) {
        controls.push(
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key: "wsgm-native-qam-valve-reset" },
            controlRuntime.react.createElement(valveResetControl),
          ),
        );
      }
      if (!controls.length) {
        appendDiagnostics[placement] = { controls: 0, inserted: false, ownSection: false };
        return tree;
      }

      // Quick Settings takes a plain appended section and nothing else. The native-row filtering
      // below is about Steam's FPS counter rows on the PERFORMANCE panel; running it against a
      // different tab's tree would be hiding rows this code has never even looked at.
      if (placement === "quickSettings") {
        const section = controlRuntime.react.createElement(
          controlRuntime.section,
          { key: "wsgm-native-qam-quick-settings-section" },
          ...controls,
        );
        appendDiagnostics[placement] = {
          controls: controls.length,
          inserted: true,
          ownSection: true,
        };
        // Display controls lead the tab rather than trailing it: brightness and the shortcut
        // toggles read below them naturally, and a dropdown at the bottom of a scrolling tab is
        // the control a user finds last.
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          section,
          tree,
        );
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
      appendDiagnostics.perf = {
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
      vrrControl = createVrrControl(controlRuntime);

      // Selected by the localization token it draws, never by a minified export name: the names are
      // right for today's build and are not guaranteed for the next. Live-probed 2026-08-30 that
      // this token matches exactly one export of the components module.
      const perfComponents = uniqueFactory([
        "#QuickAccess_Tab_Perf_EnableVRR",
        "#QuickAccess_Tab_Perf_LimitFrameRate",
      ]);
      const perfExports = perfComponents ? runtime(perfComponents[0]) : null;
      valveVrrControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_EnableVRR"])
        : null;
      valveProfileHeaderControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_GameSpecificSettings"])
        : null;
      valveResetControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ResetToDefault"])
        : null;
      valveRefreshRateControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_RefreshRate"])
        : null;
      valveFrameLimitControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_LimitFrameRate"])
        : null;
      valveOverlayLevelControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_Overlay_Level"])
        : null;

      // A DIFFERENT module from the perf components above: the power-limit rows live with the
      // GPU-clock and charge-limit rows, next to the SteamOS Manager hooks they read. Selected by
      // the setting each one is bound to plus its own token, because both rows carry
      // #QuickAccess_Tab_Perf_TDPLimitEnabled — the toggle as its label, the slider as its
      // explainer title. Live-verified 2026-08-30 that each pair matches exactly one export.
      const tdpComponents = uniqueFactory([
        "#QuickAccess_Tab_Perf_TDPLimitEnabled",
        "#QuickAccess_Tab_Perf_TDPLimitUnits",
      ]);
      const tdpExports = tdpComponents ? runtime(tdpComponents[0]) : null;
      valveTdpToggleControl = tdpExports
        ? uniqueFunction(tdpExports, [
            '"steamos_tdp_limit_enabled"',
            "#QuickAccess_Tab_Perf_TDPLimitEnabled",
          ])
        : null;
      valveTdpSliderControl = tdpExports
        ? uniqueFunction(tdpExports, ["#QuickAccess_Tab_Perf_TDPLimitUnits"])
        : null;

      function WsgmNativeQamPerformanceRoot(props) {
        const [, setRevision] = controlRuntime.react.useState(0);
        controlRuntime.react.useEffect(
          () => subscribeHost(() => setRevision((value) => value + 1)),
          [],
        );
        return appendControls(controlRuntime, performanceRoot(props));
      }
      originalUseMemo = controlRuntime.react.useMemo;
      // One wrapper per wrapped tab, matched by root identity in the same memoized tab array.
      // Each root must match exactly once or it is left alone — the discipline that kept the
      // performance wrap honest, applied per root rather than to the array as a whole.
      // The performance panel is matched by export identity; the Quick Settings panel CANNOT be —
      // a tap on the tab array (2026-08-30) showed its type is a local function no module exports.
      // It is matched by its own source instead, on two Valve strings WSGM's gates never touch: the
      // Other-section title and the reorder-controllers button. Deliberately NOT the brightness
      // title, because that is the surface WSGM's own gate reveals, and a selector must not be
      // entangled with a thing this code changes.
      const wrappers = [
        {
          match: (type) => type === performanceRoot,
          component: () => WsgmNativeQamPerformanceRoot,
          fallbackKey: "wsgm-native-qam-performance-root",
        },
        {
          match: (type) => {
            if (typeof type !== "function" || type === performanceRoot) return false;
            const source = String(type);
            return (
              source.includes("#QuickAccess_Tab_Settings_Section_Other_Title") &&
              source.includes("#QuickAccess_ReorderControllers_Button")
            );
          },
          // The original is only known at match time, so the wrapper is built then — and cached by
          // original, because a fresh component identity on every memo pass would remount the whole
          // tab on each render.
          component: (original) => {
            let wrapped = quickSettingsWrapCache.get(original);
            if (!wrapped) {
              wrapped = function WsgmNativeQamQuickSettingsRoot(props) {
                const [, setRevision] = controlRuntime.react.useState(0);
                controlRuntime.react.useEffect(
                  () => subscribeHost(() => setRevision((value) => value + 1)),
                  [],
                );
                quickSettingsRoot = original;
                return appendControls(controlRuntime, original(props), "quickSettings");
              };
              quickSettingsWrapCache.set(original, wrapped);
            }
            return wrapped;
          },
          fallbackKey: "wsgm-native-qam-quick-settings-root",
        },
      ];
      patchedUseMemo = function WsgmNativeQamUseMemo(factory, dependencies) {
        const value = originalUseMemo(factory, dependencies);
        if (!Array.isArray(value)) return value;
        let result = value;
        for (const wrapper of wrappers) {
          const matches = result.filter(
            (item) =>
              item &&
              typeof item === "object" &&
              controlRuntime.react.isValidElement(item.panel) &&
              wrapper.match(item.panel.type),
          );
          if (matches.length !== 1) continue;
          result = result.map((item) => {
            if (item !== matches[0]) return item;
            const panel = controlRuntime.react.createElement(wrapper.component(item.panel.type), {
              ...item.panel.props,
              key: item.panel.key ?? wrapper.fallbackKey,
            });
            return { ...item, panel };
          });
        }
        return result;
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
      lastAppend: appendDiagnostics.perf,
      lastAppendQuickSettings: appendDiagnostics.quickSettings,
      quickSettingsRootResolved: !!quickSettingsRoot,
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
