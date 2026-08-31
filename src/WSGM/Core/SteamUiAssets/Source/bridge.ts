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
  if (prior) {
    // Older bridge versions disposed only the component host. Ask every exposed gate to unwind
    // while its closure still has the original methods/descriptors, then dispose the bridge. This
    // is the compatibility bridge that lets the new uniform ownership markers replace the old
    // per-gate ones without stacking on dead wrappers.
    for (const gateName of [
      "steamOsManager",
      "brightness",
      "bluetooth",
      "network",
      "audio",
      "perf",
    ]) {
      try {
        prior[gateName]?.remove?.();
      } catch {}
    }
    if (typeof prior.dispose === "function") prior.dispose("generation replaced");
  }

  const pending = new Map();
  const subscribers = new Map();
  const latestStates = new Map();
  const nativeComponents = createNativeComponentHost();
  let nextSequence = 0;
  let disposed = false;

  // One reviewed runtime tap for every gate. Capturing webpack's runtime by pushing an empty
  // chunk is the proven primitive; six private copies only made it possible for their safety and
  // diagnostics to drift. This helper captures the runtime but never evaluates an unknown module.
  const getWebpackRuntime = (scope) => {
    let runtime;
    window.webpackChunksteamui.push([
      [`wsgm_${scope}_${Date.now()}`],
      {},
      (value) => {
        runtime = value;
      },
    ]);
    return runtime;
  };

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
  const actionGenerations = new Map<string, number>();
  const nextActionGeneration = (patchId) => {
    const next = (actionGenerations.get(patchId) || 0) + 1;
    actionGenerations.set(patchId, next);
    return next;
  };
  const validActionGeneration = (patchId, actionGeneration) => {
    if (Number.isInteger(actionGeneration) && actionGeneration > 0) {
      actionGenerations.set(
        patchId,
        Math.max(actionGenerations.get(patchId) || 0, actionGeneration),
      );
      return actionGeneration;
    }
    return nextActionGeneration(patchId);
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
    // Resident gates own callbacks, service overlays and timers outside the bridge namespace.
    // Removing only nativeComponents left the Manager gate polling every second after the bridge
    // that answered it had gone away, and left the other service wrappers calling dead closures.
    for (const gate of [
      steamOsManagerGate,
      brightnessGate,
      bluetoothService,
      networkGate,
      audioNamespace,
      perfNamespace,
    ]) {
      try {
        gate.remove();
      } catch {}
    }
    nativeComponents.dispose();
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "WSGM bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
    latestStates.clear();
    actionGenerations.clear();
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
