(() => {
  "use strict";
  const config = __WSGM_CONFIGURATION_JSON__;
  const prior = window[config.namespace];
  if (
    prior &&
    prior.version === config.version &&
    prior.contextGeneration === config.contextGeneration &&
    prior.documentGeneration === config.documentGeneration
  ) {
    return JSON.stringify({ ok: true, reused: true, version: prior.version });
  }
  if (prior && typeof prior.dispose === "function") prior.dispose("generation replaced");

  const pending = new Map();
  const subscribers = new Map();
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
      const set = subscribers.get(envelope.patchId);
      if (!set) return false;
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
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "WSGM bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
  };

  const bridge = Object.freeze({
    version: config.version,
    contextGeneration: config.contextGeneration,
    documentGeneration: config.documentGeneration,
    request,
    subscribe,
    deliver,
    dispose,
  });
  Object.defineProperty(window, config.namespace, {
    value: bridge,
    configurable: true,
    enumerable: false,
    writable: false,
  });
  return JSON.stringify({ ok: true, reused: false, version: config.version });
})()
