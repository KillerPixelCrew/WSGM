(() => {
  "use strict";
  // What the whole bundle evaluates to, set at the end of this file and returned by epilogue.ts
  // after every fragment has registered. The early reuse return below is the one path that leaves
  // before the fragments run, and it returns its own result directly.
  let installResult;
  const config = __STEAM_UI_CONFIGURATION_JSON__;
  const prior = window[config.namespace];
  if (
    prior &&
    prior.version === config.version &&
    // Neither generation changes when the host is updated, so without the asset hash a new build kept
    // running the previous build's script until Steam itself restarted.
    prior.assetHash === config.assetHash &&
    prior.contextGeneration === config.contextGeneration &&
    prior.documentGeneration === config.documentGeneration &&
    // A prior bridge that can still hand out gates is one this build can stand aside for. Asking
    // for a specific gate by name would tie the reuse check to whichever surfaces the consumer
    // happens to have.
    typeof prior.gate === "function"
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
  let nextSequence = 0;
  let disposed = false;
  // One reviewed runtime tap for every gate. Capturing webpack's runtime by pushing an empty
  // chunk is the proven primitive; six private copies only made it possible for their safety and
  // diagnostics to drift. This helper captures the runtime but never evaluates an unknown module.
  const getWebpackRuntime = (scope) => createSteamUiModuleResolver(scope);
  const allowed = (patchId, command) => {
    const commands = config.allowed[patchId];
    return Array.isArray(commands) && commands.includes(command);
  };
  const send = (envelope) => {
    if (disposed) throw new Error("Steam UI bridge disposed");
    const binding = window[config.binding];
    if (typeof binding !== "function") throw new Error("Steam UI Runtime binding unavailable");
    binding(JSON.stringify(envelope));
  };
  // The host REJECTS an action generation of zero, and several gates were passing exactly that —
  // "sequence or action generation is invalid" against steam-ui.performance/updateSettings,
  // steam-network.gate/startScan and stopScan, and steam-bluetooth.service/setDiscovering, on the
  // reference device on 2026-08-30. Every Valve performance control's write, and every signal that
  // Steam's network page had started looking for networks, was dropped by the bridge before the host
  // ever saw it — which is why the Wi-Fi list never filled: the host was never told to scan.
  //
  // Zero was meant as "no user-initiated row action here", which is true of a gate. Rather than
  // repeat the counter at each such call site, an absent or non-positive generation is allocated
  // one here, so no caller can construct an invalid envelope at all.
  const actionGenerations = new Map();
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
  const request = (patchId, command, payload, requestedGeneration) => {
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
        reject(new Error("Steam UI bridge request timed out"));
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
    // Cached replay has the same isolation as later publications. A consumer callback must
    // not prevent its installer from receiving the unsubscribe handle and finishing setup.
    if (latestStates.has(patchId)) {
      try {
        callback(latestStates.get(patchId));
      } catch {}
    }
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
    // Removing only the component host left the Manager gate polling every second after the bridge
    // that answered it had gone away, and left the other service wrappers calling dead closures.
    //
    // Every registered gate, not a list: a gate this file does not know about is exactly the case
    // a list gets wrong, and it is the normal case once a consumer adds one.
    for (const gate of gates.values()) {
      const owned = gate;
      // Both, where present. `remove` unwinds what the gate installed in the client; `dispose`
      // releases what it holds inside this bridge, and the component host has only the latter.
      try {
        owned.remove?.();
      } catch {}
      try {
        owned.dispose?.();
      } catch {}
    }
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "Steam UI bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
    latestStates.clear();
    actionGenerations.clear();
  };
  // Stamped on every namespace the host defines on SteamClient, so a later probe can tell OUR namespace
  // from a real backend. Without it the two are indistinguishable and the compatibility check reads
  // its own successful install as "a native backend exists", refuses, and tears the patch down —
  // which is exactly what left this client with an empty audio page and a crashing Performance tab.
  //
  // A string key rather than a Symbol: it has to survive being read back from a probe evaluated in
  // a separate CDP call, where a Symbol from this scope is not reachable.
  const ownedMarker = "__steamUiOwnedNamespace";
  // The same idea one level down: a method the host overlaid rather than a namespace it defined. The
  // second key carries the method that was replaced, so an overlay outliving the closure that made
  // it can still be unwound back to the client's own.
  const getState = {
    marker: "__steamUiOwnedGetState",
    original: "__steamUiOriginalGetState",
  };
  // Gates register themselves rather than being named here. The bridge used to construct each one
  // by name and publish it under a fixed property, which meant this file had to list every surface
  // its consumer happened to have — the one thing a reusable bridge cannot do.
  //
  // Registration is a top-level statement in each fragment, so it runs after this file and before
  // anything asks for a gate. It also inherits the reuse check for free: when this file returns
  // early because an identical bridge is already installed, the whole IIFE returns and no fragment
  // registers over it.
  const gates = new Map();
  const registerGate = (name, gate) => {
    gates.set(name, gate);
  };
  const bridge = Object.freeze({
    version: config.version,
    assetHash: config.assetHash,
    contextGeneration: config.contextGeneration,
    documentGeneration: config.documentGeneration,
    request,
    subscribe,
    deliver,
    dispose,
    // Looked up at call time, not captured: a gate registers after this object is frozen, and the
    // host asks for one long after that. Returning null for an unknown name rather than throwing
    // keeps a patch whose fragment failed to load reporting "gate absent" instead of an exception
    // with no name in it.
    gate: (name) => gates.get(name) ?? null,
  });
  Object.defineProperty(window, config.namespace, {
    value: bridge,
    configurable: true,
    enumerable: false,
    writable: false,
  });
  // NOT a return: every fragment after this file is concatenated into the same IIFE, so returning
  // the install result here would make each gate's top-level registerGate call unreachable and the
  // bridge would publish with an empty registry. epilogue.ts returns this once the bundle has run.
  installResult = JSON.stringify({ ok: true, reused: false, version: config.version });
  const defineHidden = (host, key, value) => {
    Object.defineProperty(host, key, {
      value,
      configurable: true,
      enumerable: false,
      writable: false,
    });
  };
  // The names a previous build wrote the same markers under, before the library's identifiers left
  // its first consumer's namespace. A marker is a string key on a live client object, and the client
  // outlives the bridge that wrote it: refusing the old spelling would strand every surface until
  // Steam restarted, which is the orphan trap this file exists to close. Read as ours, never written.
  const legacyKey = (key) => key.replace(/^__steamUi/u, "__wsgm");
  const claimed = (host, keys) =>
    !!host && (host[keys.marker] === true || host[legacyKey(keys.marker)] === true);
  // What a claim stored as the displaced original, under either spelling of the key.
  const storedOriginal = (host, keys) => {
    const record = host;
    if (Object.hasOwn(record, keys.original)) return record[keys.original];
    if (Object.hasOwn(record, legacyKey(keys.original))) return record[legacyKey(keys.original)];
    return undefined;
  };
  const hasStoredOriginal = (host, keys) =>
    Object.hasOwn(host, keys.original) || Object.hasOwn(host, legacyKey(keys.original));
  // Removes both spellings of a claim's markers; releasing what an older build claimed must not
  // leave its keys behind for the next probe to read as a claim.
  const dropClaimKeys = (host, keys) => {
    for (const key of [
      keys.marker,
      keys.original,
      legacyKey(keys.marker),
      legacyKey(keys.original),
    ]) {
      delete host[key];
    }
  };
  const captureProperty = (host, property) => ({
    kind: "steam-ui-property-snapshot-v1",
    hadOwn: Object.hasOwn(host, property),
    descriptor: Object.getOwnPropertyDescriptor(host, property),
    value: host[property],
  });
  const isPropertySnapshot = (value) =>
    !!value &&
    typeof value === "object" &&
    // Both spellings of the kind, for the same reason claimed() reads both marker spellings.
    (value.kind === "steam-ui-property-snapshot-v1" ||
      value.kind === "wsgm-property-snapshot-v1") &&
    typeof value.hadOwn === "boolean";
  // An accessor-backed field is one whose value lives BEHIND the property — a MobX observable, a
  // store's computed flag — and the only safe way to change it is through its own setter.
  // Redefining or deleting the accessor destroys the store's bookkeeping while leaving the getter in
  // place: Steam's settings message (a MobX object) then throws
  // `Cannot read properties of undefined (reading 'get')` on every later read, which crashed the
  // Quick Access Menu until the client restarted (device-reproduced 2026-09-01, brightness flag).
  const accessorSetter = (host, property) => {
    const current = Object.getOwnPropertyDescriptor(host, property);
    if (!current || "value" in current) return null;
    return typeof current.set === "function" ? current.set : undefined;
  };
  const restoreProperty = (host, property, snapshot) => {
    const setter = accessorSetter(host, property);
    if (setter !== null) {
      if (setter === undefined) {
        throw new TypeError("restore target is a read-only accessor");
      }
      if (host[property] !== snapshot.value) host[property] = snapshot.value;
      return;
    }
    if (snapshot.hadOwn && snapshot.descriptor) {
      Object.defineProperty(host, property, snapshot.descriptor);
    } else {
      delete host[property];
    }
  };
  const legacyValueSnapshot = (host, property, value, absentMeansMissing) => {
    const current = Object.getOwnPropertyDescriptor(host, property);
    const hadOwn = !(absentMeansMissing && value === undefined) && !!current;
    return {
      kind: "steam-ui-property-snapshot-v1",
      hadOwn,
      descriptor: hadOwn && current && "value" in current ? { ...current, value } : undefined,
      value,
    };
  };
  const installDataValue = (host, property, value) => {
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (descriptor) {
      if (!("value" in descriptor)) {
        // Through the setter, never by redefinition — see accessorSetter. Read back because a
        // setter is free to ignore the write, and a claim that did not take must not be marked.
        if (typeof descriptor.set !== "function") {
          throw new TypeError("claim target is a read-only accessor");
        }
        host[property] = value;
        if (host[property] !== value) {
          throw new TypeError("claim target did not accept the value");
        }
        return;
      }
      Object.defineProperty(host, property, { ...descriptor, value });
    } else {
      Object.defineProperty(host, property, {
        value,
        configurable: true,
        enumerable: true,
        writable: true,
      });
    }
  };
  // Claims a plain data field — a flag or value the client set, that a gate replaces.
  //
  // `absent` is what the field reads as when nothing has claimed it. It is required rather than
  // inferred: reclaiming a previous bridge's work has to restore what THAT bridge displaced, and when
  // the stored original is missing the only honest answer is the value the client would have had.
  const claimValue = (host, field, keys, next, absent) => {
    if (!host || !(field in host)) {
      return { ok: false, error: "claim target unavailable" };
    }
    const reclaimed = claimed(host, keys);
    // Already at the target value and NOT marked means the client did this itself. Refusing is
    // correct: there is nothing to add, and restoring later would hand back a value we invented.
    if (!reclaimed && host[field] === next) {
      return { ok: false, error: "already set by the client" };
    }
    const fieldBefore = captureProperty(host, field);
    const markerBefore = Object.getOwnPropertyDescriptor(host, keys.marker);
    const originalBefore = Object.getOwnPropertyDescriptor(host, keys.original);
    try {
      const stored = hasStoredOriginal(host, keys) ? storedOriginal(host, keys) : absent;
      const original = reclaimed
        ? isPropertySnapshot(stored)
          ? stored
          : legacyValueSnapshot(host, field, stored, false)
        : fieldBefore;
      installDataValue(host, field, next);
      // Rewritten under the current spelling; an older build's keys are dropped so a probe from a
      // separate evaluation reads one claim, not two.
      dropClaimKeys(host, keys);
      defineHidden(host, keys.marker, true);
      defineHidden(host, keys.original, original);
      return { ok: true, reclaimed };
    } catch (error) {
      try {
        restoreProperty(host, field, fieldBefore);
        if (markerBefore) Object.defineProperty(host, keys.marker, markerBefore);
        else delete host[keys.marker];
        if (originalBefore) Object.defineProperty(host, keys.original, originalBefore);
        else delete host[keys.original];
      } catch {
        // The primary error remains the useful diagnosis; a hostile Proxy can also refuse rollback.
      }
      return { ok: false, error: String(error) };
    }
  };
  // Hands a claimed field back. Releasing something never claimed is success, not an error: a gate
  // that failed halfway must be able to unwind without knowing how far it got.
  const releaseValue = (host, field, keys) => {
    if (!host || !claimed(host, keys)) return { ok: true };
    try {
      const stored = storedOriginal(host, keys);
      const original = isPropertySnapshot(stored)
        ? stored
        : legacyValueSnapshot(host, field, stored, false);
      restoreProperty(host, field, original);
      dropClaimKeys(host, keys);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Claims a member — a method a gate overlays, or a namespace it supplies where the client has
  // none. The marker goes on the REPLACEMENT rather than the host, so `status` can ask the live
  // object whether what is installed is ours without consulting any closure.
  const claimMember = (host, member, keys, replacement) => {
    if (!host) {
      return { ok: false, error: "claim host unavailable" };
    }
    const current = host[member];
    const reclaimed = claimed(current, keys);
    try {
      const stored = reclaimed ? storedOriginal(current, keys) : undefined;
      const original = reclaimed
        ? isPropertySnapshot(stored)
          ? stored
          : legacyValueSnapshot(host, member, stored, true)
        : captureProperty(host, member);
      const next = replacement(original.value);
      // Functions as well as objects: every member claim so far replaces a METHOD, and `typeof` a
      // function is "function", not "object". Excluding it left the replacement unmarked, so the
      // release found nothing of ours and handed nothing back — the overlay outlived its own
      // removal.
      if (!next || (typeof next !== "object" && typeof next !== "function")) {
        return { ok: false, error: "claim replacement cannot carry its marker" };
      }
      defineHidden(next, keys.marker, true);
      defineHidden(next, keys.original, original);
      installDataValue(host, member, next);
      return { ok: true, reclaimed };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Hands a claimed member back to whatever it displaced. A member that was absent before the claim
  // is deleted rather than set to undefined, so `member in host` reads as it did.
  const releaseMember = (host, member, keys) => {
    if (!host) return { ok: true };
    const current = host[member];
    if (!claimed(current, keys)) return { ok: true };
    try {
      const stored = storedOriginal(current, keys);
      const original = isPropertySnapshot(stored)
        ? stored
        : legacyValueSnapshot(host, member, stored, true);
      restoreProperty(host, member, original);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  const memberClaimed = (host, member, keys) => claimed(host?.[member], keys);
  // Supplies a namespace the client does not have — the Performance and audio backends Valve's own
  // components were written against and the Windows client never defines.
  //
  // Distinct from claimMember, which overlays something that EXISTS. Three differences matter:
  //
  //   - Refusing a real backend is correct. A client that grows one must not be shadowed by a
  //     projection of a different machine's hardware.
  //   - Reclaiming our own is mandatory. A namespace outlives the bridge backing it — the bridge is a
  //     window property that dies with the JS context, SteamClient does not — so after a context
  //     reload an orphaned namespace is left whose methods call a bridge that is gone. Refusing there
  //     stranded the client permanently: the probe saw a namespace, called the patch incompatible,
  //     and Steam's audio page stayed empty until Steam itself restarted.
  //   - Removal DELETES rather than restores, because there was nothing there to hand back.
  //
  // Defined rather than assigned, and non-writable: assignment would throw against a previous
  // bridge's non-writable definition, under the "use strict" this whole asset runs in — turning a
  // reclaim into exactly the refusal above.
  // Takes a marker alone rather than a ClaimKeys pair, because nothing is displaced: there is no
  // original to remember, and removal deletes.
  const supplyNamespace = (host, name, marker, factory) => {
    if (!host) {
      return { ok: false, error: "namespace host unavailable" };
    }
    const current = host[name];
    if (current && !claimed(current, { marker, original: marker })) {
      return { ok: false, error: `${name} already exists` };
    }
    try {
      const api = factory();
      defineHidden(api, marker, true);
      Object.defineProperty(host, name, {
        value: api,
        configurable: true,
        enumerable: true,
        writable: false,
      });
      return { ok: true, reclaimed: !!current };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Withdraws a supplied namespace. Only ever deletes one this bridge marked, so a real backend that
  // appeared underneath is left alone.
  const withdrawNamespace = (host, name, marker) => {
    if (!host || !claimed(host[name], { marker, original: marker })) return { ok: true };
    try {
      delete host[name];
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Claims an accessor property — a getter the client computes, that a gate answers differently.
  //
  // Separate from claimMember because the write has to be defineProperty rather than assignment:
  // assigning to a getter-backed property either calls a setter that is not there or throws, and
  // defining the replacement on the INSTANCE instead of where the accessor lives would shadow rather
  // than replace, leaving the shadow behind after removal. The marker goes on the replacement getter
  // and carries the whole original descriptor, because that is what has to be handed back.
  //
  // Refuses a non-configurable property rather than throwing: a client that locked it is a client
  // this gate stands aside for.
  const claimAccessor = (host, property, keys, getter) => {
    if (!host) {
      return { ok: false, error: "claim host unavailable" };
    }
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (!descriptor || descriptor.configurable !== true) {
      return { ok: false, error: "property is not configurable" };
    }
    try {
      const reclaimed = claimed(descriptor.get, keys);
      const original = reclaimed ? storedOriginal(descriptor.get, keys) : descriptor;
      defineHidden(getter, keys.marker, true);
      defineHidden(getter, keys.original, original);
      Object.defineProperty(host, property, { get: getter, configurable: true });
      return { ok: true, reclaimed };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Restores the descriptor a claimed accessor displaced.
  const releaseAccessor = (host, property, keys) => {
    if (!host) return { ok: true };
    try {
      const descriptor = Object.getOwnPropertyDescriptor(host, property);
      if (!claimed(descriptor?.get, keys)) return { ok: true };
      const original = storedOriginal(descriptor.get, keys);
      if (original) {
        Object.defineProperty(host, property, original);
      }
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // Answering what Steam asks.
  //
  // The client calls a service method and reads a transport reply, not a bare value. Two gates
  // answer such calls — the SteamOS Manager's GetState and the Bluetooth service's stubs — and both
  // had built the same reply shape and the same query invalidation by hand.
  //
  // Overlaying the method itself is an ownership claim (claimMember); what is here is the rest of
  // the job, which is the half that is easy to forget.
  // The shape Steam reads back from a service call. BSuccess decides whether the caller proceeds at
  // all, so a reply that omits it is discarded before its body is ever looked at; Body().toObject()
  // is what the store then consumes.
  const transportReply = (body) => ({
    BSuccess: () => true,
    BFailed: () => false,
    GetEResult: () => 1,
    Body: () => ({ ...body, toObject: () => body }),
  });
  // Replacing a stub is only half the job: react-query still holds the answer the stub gave, so the
  // UI keeps rendering the refusal until the query that cached it is invalidated. Live-verified that
  // the query client's invalidateQueries is reachable at module 21371.
  //
  // Failure is swallowed on purpose. A client whose query layer moved keeps the stale answer and the
  // row simply does not update — which is a degraded surface, not a broken one, and never a reason to
  // tear down a gate that is otherwise working.
  const invalidateQuery = (req, queryKey) => {
    try {
      req?.("21371")?.L?.invalidateQueries({ queryKey });
    } catch {
      // Intentionally ignored; see above.
    }
  };
  const SteamUiIconShapes = Object.freeze({
    // -- Profile scope --------------------------------------------------------------------------
    // An ID card: the question the section answers is whose settings these are, not what they do.
    profile: [
      [
        "path",
        {
          d: "M3 5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5Zm2 0v14h14V5H5Z",
          fillRule: "evenodd",
        },
      ],
      ["circle", { cx: 9.5, cy: 10, r: 2.3 }],
      ["path", { d: "M6 17.2a3.5 3.5 0 0 1 7 0v.3H6v-.3Z" }],
      ["path", { d: "M14.5 8.4h4v1.8h-4V8.4Zm0 3.6h4v1.8h-4V12Z" }],
    ],
    // -- Power profiles -------------------------------------------------------------------------
    // Two faders for the Power profiles header: a profile is a set position, not a power source.
    sliders: [
      ["rect", { x: 3, y: 6.4, width: 18, height: 2.2, rx: 1.1 }],
      ["rect", { x: 6.6, y: 4.2, width: 3.4, height: 6.6, rx: 1.3 }],
      ["rect", { x: 3, y: 15.4, width: 18, height: 2.2, rx: 1.1 }],
      ["rect", { x: 14, y: 13.2, width: 3.4, height: 6.6, rx: 1.3 }],
    ],
    // The Windows power plan, drawn as the symbol Windows itself puts on one.
    power: [
      [
        "path",
        {
          d: "M5.87 7.86A8 8 0 1 0 18.13 7.86",
          fill: "none",
          stroke: "currentColor",
          strokeWidth: 2.4,
          strokeLinecap: "round",
        },
      ],
      ["rect", { x: 10.8, y: 2.6, width: 2.4, height: 8.6, rx: 1.2 }],
    ],
    // A processor die with its pins, for the core-preference row. Drawn rather than reusing the power
    // glyph because every control places exactly one glyph of its own, and this one chooses which
    // kind of core runs work rather than which power state the machine is in.
    cores: [
      [
        "rect",
        {
          x: 6.4,
          y: 6.4,
          width: 11.2,
          height: 11.2,
          rx: 1.8,
          fill: "none",
          stroke: "currentColor",
          strokeWidth: 2,
        },
      ],
      ["rect", { x: 10.4, y: 10.4, width: 3.2, height: 3.2, rx: 0.8 }],
      ["rect", { x: 9, y: 2.4, width: 1.8, height: 3.2, rx: 0.9 }],
      ["rect", { x: 13.2, y: 2.4, width: 1.8, height: 3.2, rx: 0.9 }],
      ["rect", { x: 9, y: 18.4, width: 1.8, height: 3.2, rx: 0.9 }],
      ["rect", { x: 13.2, y: 18.4, width: 1.8, height: 3.2, rx: 0.9 }],
      ["rect", { x: 2.4, y: 9, width: 3.2, height: 1.8, rx: 0.9 }],
      ["rect", { x: 2.4, y: 13.2, width: 3.2, height: 1.8, rx: 0.9 }],
      ["rect", { x: 18.4, y: 9, width: 3.2, height: 1.8, rx: 0.9 }],
      ["rect", { x: 18.4, y: 13.2, width: 3.2, height: 1.8, rx: 0.9 }],
    ],
    plug: [
      ["path", { d: "M8.5 2h2v5h-2V2Zm5 0h2v5h-2V2Z" }],
      ["path", { d: "M6 8h12v4a6 6 0 0 1-5 5.92V22h-2v-4.08A6 6 0 0 1 6 12V8Z" }],
    ],
    battery: [
      [
        "path",
        {
          d: "M3 7a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Zm2 0v10h11V7H5Z",
          fillRule: "evenodd",
        },
      ],
      ["rect", { x: 19.5, y: 10, width: 2, height: 4, rx: 1 }],
      // A part-full cell rather than a solid one: at 20px a fill that reaches the casing closes the
      // gap between them and the whole glyph reads as a rounded block.
      ["rect", { x: 7, y: 9, width: 5, height: 6, rx: 0.8 }],
    ],
    // The profile actually in effect right now, above the two assignments that choose it.
    check: [["path", { d: "M8.2 15.4 3.6 10.8 1.5 12.9 8.2 19.6 20.9 6.9 18.8 4.8 8.2 15.4Z" }]],
    // -- Charging -------------------------------------------------------------------------------
    // The same casing as `battery` with a bolt in it, because the Charging section and the On battery
    // assignment are related but not the same thing, and the eye should read both at once.
    batteryCharging: [
      [
        "path",
        {
          d: "M3 7a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Zm2 0v10h11V7H5Z",
          fillRule: "evenodd",
        },
      ],
      ["rect", { x: 19.5, y: 10, width: 2, height: 4, rx: 1 }],
      ["path", { d: "M11.6 7.8 7.4 12.9h2.6l-.6 3.9 4.2-5.1h-2.6l.6-3.9Z" }],
    ],
    // The charge limit is a percentage and nothing else, so it is drawn as one.
    percent: [
      ["path", { d: "M16.4 2.6 18.4 3.7 7.6 21.4 5.6 20.3 16.4 2.6Z" }],
      [
        "path",
        {
          d: "M7.6 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0 2a2 2 0 1 1 0 4 2 2 0 0 1 0-4Z",
          fillRule: "evenodd",
        },
      ],
      [
        "path",
        {
          d: "M16.4 13a4 4 0 1 0 0 8 4 4 0 0 0 0-8Zm0 2a2 2 0 1 1 0 4 2 2 0 0 1 0-4Z",
          fillRule: "evenodd",
        },
      ],
    ],
    // -- Display and frame rate -----------------------------------------------------------------
    display: [
      [
        "path",
        {
          d: "M2 5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5Zm2 0v10h16V5H4Z",
          fillRule: "evenodd",
        },
      ],
      ["path", { d: "M10 17h4v2h3v2H7v-2h3v-2Z" }],
    ],
    // Corner brackets for the resolution row: the mode is the size of the frame, not the panel.
    aspect: [
      [
        "path",
        {
          d: "M3 3h7v2.4H5.4V10H3V3Zm11 0h7v7h-2.4V5.4H14V3ZM3 14h2.4v4.6H10V21H3v-7Zm15.6 0H21v7h-7v-2.4h4.6V14Z",
        },
      ],
    ],
    // A stopwatch heads the Performance display section, where every row is about frame timing rather
    // than the panel itself.
    timer: [
      ["rect", { x: 9.6, y: 1.4, width: 4.8, height: 2.6, rx: 1 }],
      ["rect", { x: 11, y: 3.6, width: 2, height: 2.6 }],
      [
        "path",
        {
          d: "M12 5.8a8.2 8.2 0 1 0 0 16.4 8.2 8.2 0 0 0 0-16.4Zm0 2a6.2 6.2 0 1 1 0 12.4 6.2 6.2 0 0 1 0-12.4Z",
          fillRule: "evenodd",
        },
      ],
      ["rect", { x: 11.1, y: 8.6, width: 1.8, height: 5.4 }],
      ["rect", { x: 11.1, y: 13.1, width: 6, height: 1.8 }],
    ],
    // Valve's performance-overlay level, which is how much of the overlay is drawn. Stacked layers
    // rather than a rectangle: `display`, `profile` and `aspect` are already frames, and a fourth
    // would be the shape all four get confused for.
    layers: [
      ["path", { d: "M12 2.2 22.4 7.9 12 13.6 1.6 7.9 12 2.2Z" }],
      ["path", { d: "M4.3 11.2 1.6 12.7 12 18.4 22.4 12.7 19.7 11.2 12 15.4 4.3 11.2Z" }],
      ["path", { d: "M4.3 15.6 1.6 17.1 12 22.8 22.4 17.1 19.7 15.6 12 19.8 4.3 15.6Z" }],
    ],
    // Rising bars for the frame-rate row: the same shape reads as a cap while one is set and as the
    // refresh rate once the cap is off, which is exactly what that one slider does.
    frameRate: [
      ["rect", { x: 3, y: 13, width: 4, height: 8, rx: 1.2 }],
      ["rect", { x: 10, y: 8, width: 4, height: 13, rx: 1.2 }],
      ["rect", { x: 17, y: 3, width: 4, height: 18, rx: 1.2 }],
    ],
    // Switching the frame cap off is the slider's own negation, so the glyph is the cap removed
    // rather than a second frame-rate shape.
    infinity: [
      [
        "path",
        {
          d: "M8.6 8.4a3.6 3.6 0 1 0 0 7.2c3.6 0 5.2-7.2 6.8-7.2a3.6 3.6 0 1 1 0 7.2c-3.6 0-5.2-7.2-6.8-7.2Z",
          fill: "none",
          stroke: "currentColor",
          strokeWidth: 2.2,
          strokeLinecap: "round",
          strokeLinejoin: "round",
        },
      ],
    ],
    // Variable refresh: an uneven trace rather than a steady one. Stroked, not filled — a 2px line is
    // the only honest way to draw a waveform at this size.
    pulse: [
      [
        "path",
        {
          d: "M2.6 12h3.1l2.5-6.4 4 13 2.8-6.6h6.4",
          fill: "none",
          stroke: "currentColor",
          strokeWidth: 2.2,
          strokeLinecap: "round",
          strokeLinejoin: "round",
        },
      ],
    ],
    // -- Power limits ---------------------------------------------------------------------------
    // A dial with a needle for the header: the section is where the ceiling is set, and the rows
    // under it are the two watt figures themselves.
    gauge: [
      [
        "path",
        {
          d: "M2.82 12.54A9.5 9.5 0 0 1 21.18 12.54L18.47 13.27A6.7 6.7 0 0 0 5.53 13.27L2.82 12.54Z",
        },
      ],
      ["path", { d: "M16.3 8.9 13.5 16 10.5 14 16.3 8.9Z" }],
      ["circle", { cx: 12, cy: 15, r: 2.2 }],
    ],
    bolt: [["path", { d: "M13.6 2 4 13.6h5.6L8.4 22 18 10.4h-5.6L13.6 2Z" }]],
    // Boost sits above sustained on the same axis, so it is the sustained bolt's idea pointed upward
    // rather than a second unrelated glyph.
    boost: [
      ["path", { d: "M12 3 4 11l2.2 2.2L12 7.4l5.8 5.8L20 11 12 3Z" }],
      ["path", { d: "M12 11 4 19l2.2 2.2L12 15.4l5.8 5.8L20 19 12 11Z" }],
    ],
    auto: [
      ["path", { d: "M10 3.5c1 3.5 1.5 4 5 5-3.5 1-4 1.5-5 5-1-3.5-1.5-4-5-5 3.5-1 4-1.5 5-5Z" }],
      [
        "path",
        {
          d: "M17.4 12.4c.8 3 1.2 3.4 4.2 4.2-3 .8-3.4 1.2-4.2 4.2-.8-3-1.2-3.4-4.2-4.2 3-.8 3.4-1.2 4.2-4.2Z",
        },
      ],
    ],
    // -- Controller -----------------------------------------------------------------------------
    // Deliberately taller than a bare stadium would be: a 2:1 body leaves the pad and the two face
    // buttons too small to tell apart once the row draws it at 20px.
    controller: [
      [
        "path",
        {
          d: "M7 5.5h10a6.5 6.5 0 0 1 0 13H7a6.5 6.5 0 0 1 0-13Zm0 2.2a4.3 4.3 0 0 0 0 8.6h10a4.3 4.3 0 0 0 0-8.6H7Z",
          fillRule: "evenodd",
        },
      ],
      ["path", { d: "M6.3 9.4h1.5V11h1.6v1.5H7.8v1.6H6.3v-1.6H4.7V11h1.6V9.4Z" }],
      ["circle", { cx: 16.2, cy: 10.6, r: 1.4 }],
      ["circle", { cx: 18.4, cy: 13, r: 1.4 }],
    ],
    // The target row chooses which controller the game is shown, so it is an exchange rather than a
    // second gamepad under a gamepad header.
    swap: [
      ["path", { d: "M3 8.4h12.5V5.6L21 9.5l-5.5 3.9v-2.8H3V8.4Z" }],
      ["path", { d: "M21 15.4H8.5v-2.8L3 16.5l5.5 3.9v-2.8H21v-2.2Z" }],
    ],
    // -- Reset ----------------------------------------------------------------------------------
    reset: [
      ["path", { d: "M12 3a9 9 0 1 0 8.5 6.1l-1.9.6A7 7 0 1 1 12 5V3Z" }],
      // The head has to clear the band it grows out of, or the whole glyph reads as a plain broken
      // ring with a thick spot at the top.
      ["path", { d: "M13.2.6 7.8 4l5.4 3.4V.6Z" }],
    ],
    // -- RGB lighting ---------------------------------------------------------------------------
    // Three overlapping rings head the section: the additive triad the lighting hardware mixes.
    colors: [
      [
        "path",
        {
          d: "M12 2.6a5.2 5.2 0 1 0 0 10.4 5.2 5.2 0 0 0 0-10.4Zm0 2.2a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
          fillRule: "evenodd",
        },
      ],
      [
        "path",
        {
          d: "M7.6 11a5.2 5.2 0 1 0 0 10.4 5.2 5.2 0 0 0 0-10.4Zm0 2.2a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
          fillRule: "evenodd",
        },
      ],
      [
        "path",
        {
          d: "M16.4 11a5.2 5.2 0 1 0 0 10.4 5.2 5.2 0 0 0 0-10.4Zm0 2.2a3 3 0 1 1 0 6 3 3 0 0 1 0-6Z",
          fillRule: "evenodd",
        },
      ],
    ],
    // The LEDs' own brightness is a lamp, kept apart from the value slider inside the colour editor.
    bulb: [
      ["circle", { cx: 12, cy: 9.4, r: 6.2 }],
      ["rect", { x: 8.4, y: 13.4, width: 7.2, height: 3.6 }],
      ["rect", { x: 8.8, y: 17.6, width: 6.4, height: 1.9, rx: 0.95 }],
      ["rect", { x: 9.8, y: 20.1, width: 4.4, height: 1.9, rx: 0.95 }],
    ],
    // Edit color opens the editor, so the row is the act of editing rather than a second colour wheel.
    pencil: [
      ["path", { d: "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25Z" }],
      [
        "path",
        {
          d: "M20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83Z",
        },
      ],
    ],
    zones: [
      ["rect", { x: 3.4, y: 3.4, width: 7.6, height: 7.6, rx: 1.6 }],
      ["rect", { x: 13, y: 3.4, width: 7.6, height: 7.6, rx: 1.6 }],
      ["rect", { x: 3.4, y: 13, width: 7.6, height: 7.6, rx: 1.6 }],
      ["rect", { x: 13, y: 13, width: 7.6, height: 7.6, rx: 1.6 }],
    ],
    // Hue is the whole spectrum in one control, which is a rainbow and not a single swatch.
    rainbow: [
      [
        "path",
        {
          d: "M3.9 18.6a8.1 8.1 0 0 1 16.2 0M8.4 18.6a3.6 3.6 0 0 1 7.2 0",
          fill: "none",
          stroke: "currentColor",
          strokeWidth: 2.6,
          strokeLinecap: "round",
        },
      ],
    ],
    // Saturation: how much colour there is, which is a drop of it.
    droplet: [
      ["path", { d: "M12 2.4c4 4.7 6.4 8 6.4 11.1a6.4 6.4 0 0 1-12.8 0c0-3.1 2.4-6.4 6.4-11.1Z" }],
    ],
    // The colour's own value, drawn as the light-to-dark split it actually moves.
    contrast: [
      [
        "path",
        {
          d: "M12 2.6a9.4 9.4 0 1 0 0 18.8 9.4 9.4 0 0 0 0-18.8Zm0 2.2a7.2 7.2 0 1 1 0 14.4 7.2 7.2 0 0 1 0-14.4Z",
          fillRule: "evenodd",
        },
      ],
      ["path", { d: "M12 4.8a7.2 7.2 0 0 1 0 14.4V4.8Z" }],
    ],
  });
  // Builds icons with Steam's own React, and caches the result: a React element is immutable, so one
  // per name and size can be handed to every render of every row rather than rebuilt on each pass.
  // An unknown name returns null, which is what Field, PanelSection and the section header below all
  // treat as "no icon" — a mistyped name loses a glyph, never a row.
  const createIconRenderer = (react) => {
    const cache = new Map();
    return (name, size = 20) => {
      if (typeof name !== "string" || !Object.hasOwn(SteamUiIconShapes, name)) return null;
      const key = `${name}:${size}`;
      const cached = cache.get(key);
      if (cached) return cached;
      const element = react.createElement(
        "svg",
        {
          xmlns: "http://www.w3.org/2000/svg",
          viewBox: "0 0 24 24",
          width: size,
          height: size,
          fill: "currentColor",
          // Decorative in every place it is used: the row's own label is the accessible name, and a
          // second announcement of it would only make the panel noisier to listen to.
          "aria-hidden": true,
          focusable: false,
        },
        ...SteamUiIconShapes[name].map(([tag, attributes], index) =>
          react.createElement(tag, { key: index, ...attributes }),
        ),
      );
      cache.set(key, element);
      return element;
    };
  };
  // Keep this fragment valid JavaScript: the same bytes are embedded for standalone C# probes
  // and composed into the bridge. Features supply fingerprints, never their own registry scan.
  function createSteamUiModuleResolver(scope) {
    let runtime;
    window.webpackChunksteamui?.push([
      [`steam_ui_${scope}_${Date.now()}`],
      {},
      (value) => {
        runtime = value;
      },
    ]);
    if (!runtime?.m) throw new Error("Steam modules unavailable");
    const failed = new Set();
    const requirePresent = (id) => {
      if (typeof id !== "string" || typeof runtime.m[id] !== "function")
        throw new Error(`Steam module absent: ${id}`);
      if (failed.has(id)) throw new Error(`Steam module resolution previously failed: ${id}`);
      try {
        return runtime(id);
      } catch (error) {
        failed.add(id);
        throw new Error(`Steam module resolution failed: ${id}: ${String(error)}`);
      }
    };
    const matches = (tokens) => {
      if (
        !Array.isArray(tokens) ||
        tokens.length < 1 ||
        tokens.length > 16 ||
        !tokens.every(
          (token) => typeof token === "string" && token.length > 0 && token.length <= 512,
        )
      )
        throw new Error("Steam module fingerprint invalid");
      const ids = Object.keys(runtime.m);
      if (ids.length > 32768) throw new Error("Steam module registry exceeds the discovery bound");
      return ids.filter((id) => {
        const factory = runtime.m[id];
        if (typeof factory !== "function") return false;
        const source = Function.prototype.toString.call(factory);
        return tokens.every((token) => source.includes(token));
      });
    };
    requirePresent.count = (tokens) => matches(tokens).length;
    requirePresent.findUnique = (tokens) => {
      const ids = matches(tokens);
      return ids.length === 1
        ? [ids[0], Function.prototype.toString.call(runtime.m[ids[0]])]
        : null;
    };
    requirePresent.resolve = (tokens) => {
      const ids = matches(tokens);
      if (ids.length !== 1)
        throw new Error(
          `Steam module ${ids.length ? "ambiguous" : "absent"}: ${tokens.join(", ")}`,
        );
      return requirePresent(ids[0]);
    };
    return requirePresent;
  }
  // @steam-ui-module-resolver-end
  // Audio is supplied as the namespace Steam's own store looks for, rather than drawn as a row.
  // The store's availability flag is literally `null != SteamClient.System.Audio`, so defining this
  // object is the entire gate — there is nothing to patch and nothing to hide.
  function createAudioNamespace() {
    const patchId = "steam-ui.audio";
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // Every registration Steam makes at construction. Held here so a state push can reach them and
    // so removal drops them all rather than leaving callbacks pointed at a torn-down bridge.
    const callbacks = {
      serviceConnection: null,
      deviceAdded: null,
      deviceRemoved: null,
      deviceVolumeChanged: null,
      volumeButtonPressed: null,
      appAdded: null,
      appRemoved: null,
      appVolumeChanged: null,
    };
    const register = (slot) => (callback) => {
      callbacks[slot] = typeof callback === "function" ? callback : null;
      // Steam expects an unregister handle from every RegisterFor* call and stores it.
      return { unregister: () => (callbacks[slot] = null) };
    };
    let known = [];
    let originalStoreState = null;
    // Steam's audio identities are NUMBERS: the live store keeps m_activeOutputDeviceId as a
    // uint32 with 0xFFFFFFFF for none (read off the running client, 2026-08-30). The host's endpoint
    // ids are Windows GUID strings, so devices listed by name but nothing could ever match as
    // active — which reads as "no default device" and disables the volume slider. Each GUID gets a
    // stable small number for Steam's side of the wire, translated back on every command.
    const NO_DEVICE = 4294967295;
    // The key m_mapVolumes is keyed by, and the second argument of both SetDeviceVolume and
    // OnAudioDeviceVolumeChanged. INPUT IS ZERO — read out of the client's own enum (module 74362:
    // Input=0, Output=1) on 2026-08-30, after assuming the opposite: with the values swapped the
    // output slider's writes were filtered out as "input" and the speaker volume was stored under
    // the input key, which put it on the microphone slider. Named because it has now been confused
    // with the volume itself AND mirrored, and neither mistake may recur silently.
    const AudioDirection = Object.freeze({ Input: 0, Output: 1 });
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
    // sliders bind — omit them and that direction renders a grey bar over undefined. The host observes
    // the two Windows defaults, so every endpoint of a direction carries that direction's current
    // default value; exposing a per-device number for an inactive endpoint would be invented.
    const toDevice = (entry, flOutputVolume, flInputVolume) => ({
      id: numberFor(entry.id),
      sName: entry.name,
      bHasOutput: entry.hasOutput === true,
      bHasInput: entry.hasInput === true,
      flOutputVolume: entry.hasOutput === true ? flOutputVolume : undefined,
      flInputVolume: entry.hasInput === true && flInputVolume !== null ? flInputVolume : undefined,
      // Speaker configuration and HDMI CEC reach a service the host does not supply. Reported empty and
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
    // ingestion path, the same verified path the network gate now owns for the network store.
    const liveStore = () => {
      try {
        const req = getWebpackRuntime("audio-store");
        const store = req?.("1409")?.F5;
        return store && "m_bAvailable" in store ? store : null;
      } catch {
        return null;
      }
    };
    const flVolumeOf = (value) => {
      if (value === null || value === undefined || !Number.isFinite(Number(value))) return null;
      return Math.min(1, Math.max(0, Number(value) / 100));
    };
    // Volume-changed dispatches fire ONLY when the volume moved. Steam shows its volume OSD on
    // every dispatch, and firing one per publish made the OSD pop up over and over while nothing
    // had changed. Null means no volume has been reported yet, so the first publish never counts
    // as a change either — construction already carries it.
    let lastFlOutputVolume = null;
    let lastFlInputVolume = null;
    const onState = (state) => {
      if (!installed || !state || !Array.isArray(state.devices)) return;
      const flOutputVolume = flVolumeOf(state.volumePercent) ?? 0;
      const flInputVolume = flVolumeOf(state.inputVolumePercent);
      const outputVolumeChanged =
        lastFlOutputVolume !== null &&
        Math.abs(flOutputVolume - lastFlOutputVolume) > VolumeEpsilon;
      const inputVolumeChanged =
        lastFlInputVolume !== null &&
        flInputVolume !== null &&
        Math.abs(flInputVolume - lastFlInputVolume) > VolumeEpsilon;
      lastFlOutputVolume = flOutputVolume;
      lastFlInputVolume = flInputVolume;
      // Numeric, because these ids flow to the store and its callbacks, and Steam's side of the
      // wire is numeric everywhere.
      const seen = state.devices.map((device) => numberFor(device.id));
      const removed = known.filter((id) => !seen.includes(id));
      // Removals first: a device that has gone must leave the store before a re-read of the device
      // list can describe the set as complete, or the picker keeps an endpoint that is not there.
      for (const id of removed) {
        if (callbacks.deviceRemoved) callbacks.deviceRemoved(id);
      }
      for (const device of state.devices) {
        if (callbacks.deviceAdded) {
          callbacks.deviceAdded(toDevice(device, flOutputVolume, flInputVolume));
        }
        // (deviceId, DIRECTION, volume) — in that order. Read off the store's own methods
        // 2026-08-30: OnAudioDeviceVolumeChanged(e,t,r) forwards to OnVolumeUpdated(t,r), which is
        // m_mapVolumes.set(t, r). The direction is the KEY and the volume is the VALUE, and the host
        // was passing them the other way round — every entry it wrote was keyed by a float volume
        // with 1 or 0 as its value, so getDeviceVolume(direction) found nothing and the slider had
        // no number to sit on.
        //
        // Still gated on an actual change, unlike the direct path below, which also has to seed:
        // a store that registered these callbacks was constructed after the namespace existed and
        // therefore already read the volumes at construction.
        if (outputVolumeChanged && device.hasOutput === true && callbacks.deviceVolumeChanged) {
          const id = numberFor(device.id);
          callbacks.deviceVolumeChanged(id, AudioDirection.Output, flOutputVolume);
        }
        if (
          inputVolumeChanged &&
          flInputVolume !== null &&
          device.hasInput === true &&
          callbacks.deviceVolumeChanged
        ) {
          const id = numberFor(device.id);
          callbacks.deviceVolumeChanged(id, AudioDirection.Input, flInputVolume);
        }
      }
      known = seen;
      // The registrations above only reach a store constructed after the namespace existed. The
      // running one has to be fed through its own path, and told it is available at all.
      const store = liveStore();
      if (!store) return;
      try {
        originalStoreState ??= {
          available: store.m_bAvailable === true,
          output: Number(store.m_activeOutputDeviceId) || NO_DEVICE,
          input: Number(store.m_activeInputDeviceId) || NO_DEVICE,
        };
        store.m_bAvailable = true;
        for (const id of removed) {
          store.m_mapAudioDevices?.delete(id);
        }
        for (const device of state.devices) {
          store.RegisterOrUpdateDevice(toDevice(device, flOutputVolume, flInputVolume));
          // Update() copies the name, the directions and the CEC flags and nothing else — read
          // live 2026-08-30 — so registration never fills m_mapVolumes and this is its only path.
          //
          // But writing on every publish is wrong in both directions at once. It dispatches a
          // volume change once a second, which is Steam's OSD popping up forever; and while the
          // user is dragging, the store is already holding the value they chose, so pushing the host's
          // not-yet-observed one snaps the handle back under their thumb.
          //
          // So: seed a direction that has no value at all, and otherwise write only when the host's
          // OWN reading moved — something outside Steam changed the volume — and the store has not
          // already caught up. Both are suppressed, because neither is the user acting inside
          // Steam: a hardware button already shows the host's own overlay.
          const deviceId = numberFor(device.id);
          const entry = store.m_mapAudioDevices?.get(deviceId);
          const volumes = [];
          if (device.hasOutput === true) {
            volumes.push({
              direction: AudioDirection.Output,
              value: flOutputVolume,
              changed: outputVolumeChanged,
            });
          }
          if (device.hasInput === true && flInputVolume !== null) {
            volumes.push({
              direction: AudioDirection.Input,
              value: flInputVolume,
              changed: inputVolumeChanged,
            });
          } else if (device.hasInput === true) {
            entry?.m_mapVolumes?.delete?.(AudioDirection.Input);
          }
          for (const volume of volumes) {
            const { direction, value, changed } = volume;
            const held = entry?.getDeviceVolume?.(direction);
            const seeding = typeof held !== "number";
            if (!seeding && !(changed && Math.abs(held - value) > VolumeEpsilon)) {
              continue;
            }
            store.SuppressVolumeOverlay?.();
            try {
              store.OnAudioDeviceVolumeChanged?.(deviceId, direction, value);
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
      const buildApi = () => ({
        GetDevices: () =>
          request(patchId, "getDevices", null, 0).then((state) => ({
            activeOutputDeviceId: numberFor(state?.activeOutputDeviceId ?? ""),
            activeInputDeviceId: numberFor(state?.activeInputDeviceId ?? ""),
            overrideOutputDeviceId: NO_DEVICE,
            overrideInputDeviceId: NO_DEVICE,
            vecDevices: Array.isArray(state?.devices)
              ? state.devices.map((device) =>
                  toDevice(
                    device,
                    flVolumeOf(state?.volumePercent) ?? 0,
                    flVolumeOf(state?.inputVolumePercent),
                  ),
                )
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
        // 2026-08-30: setDeviceVolume(e,t) calls SetDeviceVolume(this.m_id, e, t). The host declared
        // two parameters and so read the DIRECTION as the volume: dragging the slider sent
        // Math.round(1 * 100) or Math.round(0 * 100), which is why every drag set 100% or 0% and
        // the log showed "Taskbar volume set to 100%" the moment the slider was touched.
        //
        SetDeviceVolume: (id, direction, volume) => {
          if (direction !== AudioDirection.Output && direction !== AudioDirection.Input) {
            return Promise.resolve();
          }
          return request(patchId, "setVolume", {
            percent: Math.round(Math.min(1, Math.max(0, Number(volume) || 0)) * 100),
            input: direction === AudioDirection.Input,
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
      });
      // Refusing a real backend and reclaiming our own orphan are both the primitive's job now; the
      // reasoning for each lives with it.
      const supplied = supplyNamespace(system, "Audio", ownedMarker, buildApi);
      if (!supplied.ok) {
        lastError = supplied.error;
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
      const store = liveStore();
      if (store) {
        try {
          for (const id of known) store.m_mapAudioDevices?.delete(id);
          store.m_bAvailable = originalStoreState?.available ?? false;
          store.m_activeOutputDeviceId = originalStoreState?.output ?? NO_DEVICE;
          store.m_activeInputDeviceId = originalStoreState?.input ?? NO_DEVICE;
        } catch (error) {
          lastError = "audio store cleanup failed: " + String(error);
        }
      }
      known = [];
      lastFlOutputVolume = null;
      lastFlInputVolume = null;
      originalStoreState = null;
      const withdrawn = withdrawNamespace(window.SteamClient?.System, "Audio", ownedMarker);
      if (!withdrawn.ok) {
        lastError = withdrawn.error ?? "audio namespace withdrawal failed";
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
  registerGate("audio", createAudioNamespace());
  // Bluetooth is a WebUI transport service whose backend does not exist on Windows. The service,
  // its message shapes and every operation are present — GetState round-trips and answers
  // is_service_available:false with empty adapters and devices — so the host replaces the stub's
  // methods rather than implementing the service. `*Handler` exports are message descriptors,
  // not registration hooks, so implementing it is not on offer.
  //
  // The second gate matters here as much as the first: availability is read through react-query
  // with staleTime Infinity, so replacing the methods changes nothing until that cache is
  // invalidated. Live-verified 2026-08-30 that RF's methods are writable and configurable and that
  // the query client's invalidateQueries is reachable.
  function createBluetoothService() {
    const patchId = "steam-ui.bluetooth";
    const queryKey = ["BluetoothManagerService", "State"];
    const methodMarker = "__steamUiOwnedBluetoothService";
    const originalMethodField = "__steamUiOriginalBluetoothServiceMethod";
    const originals = new Map();
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
    // and the host only carries them through from the state it was given.
    let latest = { is_service_available: false, adapters: [], devices: [] };
    const modules = () => getWebpackRuntime("bluetooth-service");
    const reply = transportReply;
    const invalidate = (req) => invalidateQuery(req, queryKey);
    // The host sends its own field names and the mapping into Steam's lives here, so the client's
    // schema stays in the half that has to change when the client is rebuilt.
    const onState = (state) => {
      if (!installed || !state) return;
      const devices = Array.isArray(state.devices) ? state.devices : [];
      latest = {
        is_service_available: state.available === true,
        // One synthetic adapter, because the panel needs something to hang the radio toggle on and
        // Windows exposes no adapter identity the host could pass through truthfully.
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
          operation_in_progress: device.operationInProgress === true,
          // Steam sorts by signal and shows a battery when one is reported. The host knows neither, and
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
        request(patchId, command, payload ?? null).then(
          () => {
            lastError = "";
            return reply({ success: true });
          },
          (error) => {
            lastError = String(error);
            return {
              ...reply({ success: false, error: lastError }),
              BSuccess: () => false,
              BFailed: () => true,
              GetEResult: () => 2,
            };
          },
        );
      const replace = (name, replacement) => {
        const current = RF[name];
        const original = claimed(current, { marker: methodMarker, original: originalMethodField })
          ? storedOriginal(current, { marker: methodMarker, original: originalMethodField })
          : current;
        originals.set(name, original);
        Object.defineProperty(replacement, methodMarker, {
          value: true,
          configurable: true,
          enumerable: false,
        });
        Object.defineProperty(replacement, originalMethodField, {
          value: original,
          configurable: true,
          enumerable: false,
        });
        RF[name] = replacement;
      };
      const restore = () => {
        for (const [name, original] of originals) {
          if (claimed(RF[name], { marker: methodMarker, original: originalMethodField })) {
            RF[name] = original;
          }
        }
      };
      try {
        replace("GetState", () => Promise.resolve(reply(latest)));
        replace("GetDeviceDetails", (payload) => {
          const id = payload?.device ?? payload?.id;
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
        restore();
        originals.clear();
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
        for (const [name, original] of originals) {
          if (claimed(RF[name], { marker: methodMarker, original: originalMethodField })) {
            RF[name] = original;
          }
        }
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
  registerGate("bluetooth", createBluetoothService());
  // Not availability-only, despite the founding comment that said Steam's own backend works on
  // Windows. It does not — device-disproved 2026-08-30: SetBrightness is a native stub and
  // RegisterForBrightnessChanges never fires, so the store's observable sits at its constructed 1
  // and the revealed slider moves nothing. The host is the backend: the gate forwards the slider's
  // writes over the bridge and feeds the store's observable from the published state, both through
  // the same \\.\LCD interface the host owns.
  function createBrightnessGate() {
    const patchId = "steam-ui.brightness";
    const field = "is_display_brightness_available";
    // A string key on the settings message, because the probe reads it from a separate CDP
    // evaluation where nothing from this scope is reachable. Without it this gate ran the
    // self-incompatibility teardown loop the audio namespace already paid for: the probe required
    // the flag to be hidden, a successful apply made it visible, and the patch manager tore down
    // its own work every poll — the row flickered in and out on a ~25-second cycle on the device.
    const availability = {
      marker: "__steamUiBrightnessRevealed",
      original: "__steamUiOriginalBrightnessAvailability",
    };
    const setter = {
      marker: "__steamUiOwnedSetBrightness",
      original: "__steamUiOriginalSetBrightness",
    };
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let lastPercent = null;
    let lastRevision = -1;
    let applyingState = false;
    let requestVersion = 0;
    let pendingWrite = false;
    let confirmedState = null;
    const displayStore = () => {
      try {
        const req = getWebpackRuntime("brightness-store");
        return req?.("59547")?.mG?.Get?.() ?? null;
      } catch {
        return null;
      }
    };
    const settings = () => displayStore()?.m_msgSettings ?? null;
    const onState = (state) => {
      if (!installed || !state) return;
      const percent = Number(state.percent);
      const revision = Number(state.revision);
      if (
        !Number.isInteger(percent) ||
        percent < 0 ||
        percent > 100 ||
        !Number.isSafeInteger(revision) ||
        revision < 0 ||
        revision < lastRevision
      )
        return;
      lastRevision = revision;
      confirmedState = { percent, revision };
      if (pendingWrite) return;
      try {
        const observable = displayStore()?.m_flDisplayBrightness;
        applyingState = true;
        if (
          observable?.Set &&
          Math.abs((observable.m_currentValue ?? -1) - percent / 100) > 0.004
        ) {
          observable.Set(percent / 100);
        }
        lastPercent = percent;
      } catch (error) {
        lastError = "brightness state apply failed: " + String(error);
      } finally {
        applyingState = false;
      }
    };
    // The slider's writes, taken over at the one method it calls. Same replace-not-stack rule as
    // the Manager's GetState: the overlay carries the stub it replaced, so a bridge replaced in
    // place unwinds to the client's own method instead of wrapping a dead closure.
    const overrideSetter = () => {
      const display = window.SteamClient?.System?.Display;
      if (!display || typeof display.SetBrightness !== "function") {
        lastError = "SteamClient.System.Display.SetBrightness unavailable";
        return false;
      }
      const claim = claimMember(display, "SetBrightness", setter, () => (flBrightness) => {
        if (!installed || applyingState) return Promise.resolve();
        const value = Number(flBrightness);
        if (!Number.isFinite(value) || value < 0 || value > 1) return Promise.resolve();
        const percent = Math.round(value * 100);
        if (!pendingWrite && percent === lastPercent) return Promise.resolve();
        const version = ++requestVersion;
        pendingWrite = true;
        return request(patchId, "setBrightness", { percent })
          .then((readback) => {
            if (!installed || version !== requestVersion) return;
            pendingWrite = false;
            onState(readback);
            // A later external observation can already have arrived while this request completed.
            if (confirmedState) onState(confirmedState);
          })
          .catch((error) => {
            if (!installed || version !== requestVersion) return;
            pendingWrite = false;
            lastError = "brightness write failed: " + String(error);
            if (confirmedState) onState(confirmedState);
          });
      });
      if (!claim.ok) {
        lastError = claim.error;
        return false;
      }
      return true;
    };
    const restoreSetter = () => {
      const released = releaseMember(
        window.SteamClient?.System?.Display ?? null,
        "SetBrightness",
        setter,
      );
      if (!released.ok) {
        lastError = released.error ?? "brightness setter release failed";
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const message = settings();
      if (!message || !(field in message)) {
        lastError = "display settings message unavailable";
        return { ok: false, error: lastError };
      }
      // A client already reporting brightness available needs nothing from the host, and overwriting
      // the flag would mean restoring a value that was never ours to change. Available AND MARKED
      // is different: that is this gate's own earlier reveal, surviving a bridge replaced in
      // place, and refusing it is the teardown trap. Both cases are the claim primitive's job now.
      //
      // `false` is the absent value: a client that hides the row has the flag false, so a reclaim
      // whose stored original went missing hands back a hidden row rather than `undefined`, which
      // Steam's `?? true` hook would have read as available forever.
      const claim = claimValue(message, field, availability, true, false);
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      if (!overrideSetter()) {
        // Revealing a slider whose writes go into the stub is the broken state this gate shipped
        // with; the reveal is undone rather than left half-working.
        releaseValue(message, field, availability);
        return { ok: false, error: lastError };
      }
      installed = true;
      lastPercent = null;
      lastRevision = -1;
      confirmedState = null;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true, available: message[field] === true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const message = settings();
      installed = false;
      ++requestVersion;
      pendingWrite = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      restoreSetter();
      if (!message) return { ok: true, removed: true, storeGone: true };
      const released = releaseValue(message, field, availability);
      if (!released.ok) {
        lastError = released.error ?? "brightness release failed";
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
        setterOwned: memberClaimed(window.SteamClient?.System?.Display, "SetBrightness", setter),
        lastPercent,
        lastRevision,
        pendingWrite,
        observable: displayStore()?.m_flDisplayBrightness?.m_currentValue ?? null,
        lastError,
      };
    };
    return { install, remove, status };
  }
  registerGate("brightness", createBrightnessGate());
  // Steam's left slideout navigation panel, as an extension surface.
  //
  // The panel is module-private. Mapped against the live client on 2026-09-10:
  //
  //   v_            an exported React.memo, the VR-aware outer wrapper
  //     fe          navID "MainNavMenuContainer", role "application"
  //       c.g       nav context
  //         Ie      the panel root, props { loggedIn, menuOpen }   <- local, not exported
  //           d.Z   role "menu", aria-label #MainMenu_Title, flow-children "column"
  //             Ae  one route entry, props { route, active, label, icon, onGamepadFocus }
  //
  // `Ie` builds its list from `ve(loggedIn)` and maps it to entry elements keyed by the descriptor's
  // own `key`. Neither `Ie` nor `ve` is exported, and `ve` calls hooks — calling the module's own
  // exported list builder from outside a render throws React error #321, which is how that was
  // established rather than assumed. So both reading the entries and changing them have to happen
  // during a render, and one wrapper serves both.
  //
  // The claim is on the exported memo's `type`, which is the only public handle on the panel. From
  // there the descent reaches `Ie` by rendering: a component's children do not exist until React
  // renders it, so a walk over props.children alone arrives nowhere. That is the same mechanism
  // `hideNativeRows` in components.ts already uses, pointed at a different target.
  //
  // Entries are identified by `route` and by their React key, never by index or by a generated class
  // name. Both come from Valve's own descriptor and are stable across builds and languages; the
  // rendered labels are localized and the class names are content hashes, so neither is an anchor.
  function createNavigationPanel() {
    const patchId = "steam-ui.navigation-panel";
    const claimKeys = {
      marker: "__steamUiNavigationPanelClaimed",
      original: "__steamUiNavigationPanelOriginal",
    };
    // The two tokens that identify the panel root. `#MainMenu_Title` occurs in exactly one module of
    // the 2581 the client loads, and `RunnningAppSeparator` — Valve's own typo — occurs in three, so
    // the pair is unique where neither is alone. Deliberately not the localized title: that changes
    // with the user's language, and this has to match on a client running in any of them.
    const PanelRootTokens = ["#MainMenu_Title", "RunnningAppSeparator"];
    const OuterToken = "MainNavMenuContainer";
    // A panel with more entries than this is not the panel this was written against, and cloning an
    // unbounded child list on every render is not something a navigation menu should ever ask for.
    const MaximumEntries = 64;
    const MaximumDescent = 12;
    let runtime;
    let react;
    let icon;
    let memo = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // What the last render actually saw and did. Everything else can report success while the panel
    // shows exactly what Valve shipped, because insertion depends on the tree Steam rendered.
    let observed = [];
    let lastOutcome = "never rendered";
    // The host's desired additions and hidden entries, replaced whole on each publication.
    let desired = { items: [], hidden: [] };
    const descendCache = new Map();
    const panelCache = new Map();
    const textOf = (value) => {
      if (typeof value === "string") return value;
      if (value && typeof value === "object" && typeof value.props?.children === "string") {
        return value.props.children;
      }
      return "";
    };
    // A rendered entry's identity. `route` is the descriptor's own destination and the anchor an
    // "insert after Library" is written against; the React key is Valve's descriptor key and is what
    // survives when an entry has no route at all, such as the power button.
    const identify = (element) => {
      const route = typeof element?.props?.route === "string" ? element.props.route : null;
      const key = typeof element?.key === "string" ? element.key.replace(/^\.\$/u, "") : "";
      return { key, route, label: textOf(element?.props?.label) };
    };
    const matchesAnchor = (element, anchor) => {
      if (typeof anchor !== "string" || !anchor) return false;
      const identity = identify(element);
      return identity.route === anchor || identity.key === anchor;
    };
    // One added entry. Rendered as Valve's own row would be if it could take arbitrary props: a
    // menuitem div carrying the same role and accessible name, so the panel's keyboard and controller
    // flow treats it as one of its own. It deliberately does not reuse Valve's route entry component —
    // that one resolves its own active state from the router, and an entry pointing at a toolkit
    // consumer's surface has no route in Steam's router to resolve.
    const renderItem = (item) =>
      react.createElement(
        "div",
        {
          key: `steam-ui-nav-${item.id}`,
          role: "menuitem",
          "aria-label": item.label,
          onClick: () => {
            request(patchId, "activate", { id: item.id }).catch(() => {});
          },
        },
        item.icon && icon ? icon(item.icon) : null,
        react.createElement("span", null, item.label),
      );
    // Applies the host's list to the panel's own children.
    //
    // Order of operations matters and is fixed: hide first, then insert. Anchoring an insertion to an
    // entry that was just hidden would otherwise place it against something the user cannot see, and
    // "after Library" would silently become "at the end" depending on an unrelated setting.
    const applyEntries = (children) => {
      const kept = [];
      observed = [];
      let hidden = 0;
      for (const child of children) {
        const identity = identify(child);
        if (react.isValidElement(child) && (identity.route || identity.key)) {
          observed.push(identity);
          if (
            desired.hidden.includes(identity.route ?? "") ||
            desired.hidden.includes(identity.key)
          ) {
            hidden++;
            continue;
          }
        }
        kept.push(child);
      }
      const pending = desired.items.slice(0, MaximumEntries);
      const placed = new Set();
      const result = [];
      for (const item of pending) {
        if (item.position === "start") {
          result.push(renderItem(item));
          placed.add(item.id);
        }
      }
      for (const child of kept) {
        for (const item of pending) {
          if (!placed.has(item.id) && matchesAnchor(child, item.before)) {
            result.push(renderItem(item));
            placed.add(item.id);
          }
        }
        result.push(child);
        for (const item of pending) {
          if (!placed.has(item.id) && matchesAnchor(child, item.after)) {
            result.push(renderItem(item));
            placed.add(item.id);
          }
        }
      }
      // Anything left over goes at the end, including an entry whose anchor is not in this panel.
      // Dropping it would be the silent-control failure the guidance forbids: the caller asked for a
      // row and would have no way to tell that Steam simply does not have the item it named.
      let orphaned = 0;
      for (const item of pending) {
        if (placed.has(item.id)) continue;
        if (item.before || item.after) orphaned++;
        result.push(renderItem(item));
      }
      lastOutcome = `entries=${observed.length} hidden=${hidden} added=${pending.length} orphaned=${orphaned}`;
      return result;
    };
    // Wraps the panel root so its OUTPUT can be changed. Cached against the original, because a fresh
    // component identity on every render would remount the whole menu each time React reconciles it.
    const wrapPanelRoot = (original) => {
      let wrapped = panelCache.get(original);
      if (wrapped) return wrapped;
      wrapped = function SteamUiNavigationPanel(props) {
        const tree = original(props);
        if (!react.isValidElement(tree)) return tree;
        const children = react.Children.toArray(tree.props?.children);
        if (!children.length || children.length > MaximumEntries) {
          lastOutcome = `panel had ${children.length} children; left alone`;
          return tree;
        }
        return react.cloneElement(tree, {}, ...applyEntries(children));
      };
      panelCache.set(original, wrapped);
      return wrapped;
    };
    const isPanelRoot = (type) => {
      if (typeof type !== "function") return false;
      const source = String(type);
      return PanelRootTokens.every((token) => source.includes(token));
    };
    // Descends the rendered tree to the panel root. Function components on the way down are replaced
    // by wrappers that render the original and keep descending, because their children do not exist
    // until they render. Class components, memo and forwardRef objects are left alone: they cannot be
    // called directly, and wrapping them would change identity for refs.
    const descend = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      const type = element.type;
      if (isPanelRoot(type)) {
        return react.createElement(
          wrapPanelRoot(type),
          element.key === null ? element.props : { ...element.props, key: element.key },
        );
      }
      if (typeof type === "function" && !type.prototype?.isReactComponent) {
        let wrapper = descendCache.get(type);
        if (!wrapper) {
          wrapper = function SteamUiNavigationDescend(props) {
            return descend(type(props), 0);
          };
          descendCache.set(type, wrapper);
        }
        return react.createElement(
          wrapper,
          element.key === null ? element.props : { ...element.props, key: element.key },
        );
      }
      const kids = react.Children.toArray(element.props?.children);
      if (!kids.length) return element;
      let changed = false;
      const next = [];
      for (const kid of kids) {
        const replacement = descend(kid, depth + 1);
        changed ||= replacement !== kid;
        next.push(replacement);
      }
      return changed ? react.cloneElement(element, {}, ...next) : element;
    };
    const resolve = () => {
      runtime = getWebpackRuntime("navigation-panel");
      const reactFactory = runtime.findUnique([
        "react.transitional.element",
        "useState",
        "cloneElement",
        "createElement",
      ]);
      if (!reactFactory) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      react = runtime(reactFactory[0]);
      icon = createIconRenderer(react);
      const menuFactory = runtime.findUnique([PanelRootTokens[0], OuterToken]);
      if (!menuFactory) {
        lastError = "main menu module was not a unique match";
        return false;
      }
      // The one export whose memo renders the outer container. Selected by what its component draws,
      // never by its minified export name: those are right for today's build and nothing more.
      const exports = runtime(menuFactory[0]);
      const candidates = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return (
          value &&
          typeof value === "object" &&
          typeof value.type === "function" &&
          String(value.type).includes(OuterToken)
        );
      });
      if (candidates.length !== 1) {
        lastError = `main menu export was ${candidates.length ? "ambiguous" : "absent"}`;
        return false;
      }
      memo = exports[candidates[0]];
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      try {
        if (!resolve()) return { ok: false, error: lastError };
      } catch (error) {
        lastError = "navigation panel resolution failed: " + String(error);
        return { ok: false, error: lastError };
      }
      // The memo object is the public handle, and every consumer holds the same one, so claiming its
      // `type` reaches the panel wherever it is rendered without patching a single caller.
      const claim = claimMember(memo, "type", claimKeys, (original) => {
        if (typeof original !== "function") return original;
        return function SteamUiNavigationRoot(props) {
          return descend(original(props), 0);
        };
      });
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        const items = Array.isArray(state?.items) ? state.items : [];
        const hidden = Array.isArray(state?.hidden) ? state.hidden : [];
        desired = {
          items: items
            .filter((item) => item && typeof item.id === "string" && typeof item.label === "string")
            .slice(0, MaximumEntries),
          hidden: hidden.filter((value) => typeof value === "string").slice(0, MaximumEntries),
        };
        // Nothing re-renders the menu on its own, so a change published while it is closed shows the
        // next time Steam draws it. That is the whole of the reapply story: the claim is on the type,
        // so every future render already runs through it.
      });
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      desired = { items: [], hidden: [] };
      descendCache.clear();
      panelCache.clear();
      const released = releaseMember(memo, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "navigation panel release failed";
        return { ok: false, error: lastError };
      }
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!memo,
      claimed: memberClaimed(memo, "type", claimKeys),
      // Everything above can be true while the panel shows exactly what Valve shipped, because
      // insertion depends on the tree Steam rendered. This is the part that says what happened.
      entries: observed,
      items: desired.items.length,
      hidden: desired.hidden.length,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("navigationPanel", createNavigationPanel());
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
    const patchId = "steam-ui.network";
    const availability = {
      marker: "__steamUiOwnedGetter",
      original: "__steamUiOriginalGetterDescriptor",
    };
    const scan = {
      marker: "__steamUiOwnedNetworkScan",
      original: "__steamUiOriginalNetworkScan",
    };
    let target = null;
    let lastError = "";
    let scanWrapped = false;
    let originalStart = null;
    let originalStop = null;
    let unsubscribe = null;
    let syntheticKeys = [];
    const store = () => {
      try {
        // Steam publishes this singleton after its own module initialization. Requiring the
        // module before its chunk arrives leaves empty exports cached for the whole session.
        return window.SystemNetworkStore ?? null;
      } catch {
        return null;
      }
    };
    const removeNetworkState = (refresh) => {
      const instance = store();
      if (instance) {
        const keys = new Set(syntheticKeys);
        // Compatibility cleanup for the retired standalone indicator, which used this exact
        // bounded id range but could not hand its closure-owned key list to the new gate.
        const deviceId = instance.m_WirelessDevice?.id;
        if (deviceId !== undefined) {
          for (let index = 0; index < 24; index += 1) keys.add(`${deviceId}:${990001 + index}`);
        }
        for (const key of keys) instance.m_mapNetworkAccessPoints?.delete(key);
        instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
        instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
      }
      syntheticKeys = [];
      if (refresh) {
        try {
          window.SteamClient?.System?.Network?.ForceRefresh?.();
        } catch {}
      }
    };
    // One resident owner now reveals AND feeds the network surface. The previous standalone
    // indicator installed a second script against this same store, with its own version sentinel
    // and retry timer; bridge state gives the generation-aware gate the same verified connected AP
    // for the header. Scan lifetime remains an observation of Steam's page, not an invented
    // connection protocol: its argument order has not been read from the client.
    const onState = (state) => {
      const instance = store();
      const networks = Array.isArray(state?.networks) ? state.networks.slice(0, 24) : [];
      if (!instance || !instance.m_WirelessDevice) {
        lastError = "network store has no wireless device";
        return;
      }
      if (networks.length === 0) {
        removeNetworkState(true);
        lastError = "";
        return;
      }
      try {
        const device = JSON.parse(JSON.stringify(instance.m_WirelessDevice));
        if (!device.wireless) device.wireless = { aps: [], esecurity_supported: 0 };
        const accessPoints = networks.map((network, index) => ({
          id: 990001 + index,
          esecurity: network.secured ? 16 : 0,
          estrength: Math.max(1, Math.min(4, Number(network.strength) || 1)),
          ssid: String(network.ssid || ""),
          is_active: network.connected === true,
          is_autoconnect: network.connected === true,
          is_hidden: false,
        }));
        const keys = accessPoints.map((accessPoint) => `${device.id}:${accessPoint.id}`);
        for (const key of syntheticKeys) {
          if (!keys.includes(key)) instance.m_mapNetworkAccessPoints.delete(key);
        }
        for (const key of keys) instance.m_mapNetworkAccessPoints.delete(key);
        device.estate = networks.some((network) => network.connected === true) ? 5 : device.estate;
        device.wireless.aps = accessPoints;
        accessPoints.forEach((accessPoint) => {
          instance.SetDeviceInfo(device, accessPoint.id);
          const entry = instance.m_mapNetworkAccessPoints.get(`${device.id}:${accessPoint.id}`);
          if (entry) entry.MarkAsNotPresent = () => {};
        });
        instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
        instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
        syntheticKeys = keys;
        lastError = "";
      } catch (error) {
        lastError = String(error);
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
      //
      // Marked as ours for the same reason the namespaces are: the compatibility probe checks that
      // the getter currently reads false, and a successful override makes it read true. Left
      // unmarked, the patch reads its own success as "the client already reports this available,
      // stand aside", declares itself incompatible, and tears down — taking the network list with
      // it. The claim primitive is what keeps that from being re-derived here.
      const proto = Object.getPrototypeOf(instance);
      const claim = claimAccessor(proto, property, availability, () => true);
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      target = proto;
      lastError = "";
      wrapScanning();
      unsubscribe = subscribe(patchId, onState);
      return { ok: true, installed: true, available: instance[property] === true };
    };
    // Steam's own UI calls these when its network page opens and closes, so they are exactly the
    // signal for when a scan is worth running. The host's radio manager is otherwise driven by the host's
    // own panel, and a list refreshed only then would be stale on Steam's page — which is worse
    // than an empty one, because the user picks a network that is gone and the join fails silently.
    //
    // Both originals are always called through: this observes the lifetime, it does not take it
    // over, so a client that grows a working backend keeps behaving exactly as before.
    const wrapScanning = () => {
      const net = window.SteamClient?.System?.Network;
      if (!net || scanWrapped) return;
      const wrap = (name, command) => {
        // Checked before claiming, not inside the factory: a client without this method is one this
        // gate leaves alone entirely, and claiming would mark and reassign something that is not a
        // method at all.
        const current = net[name];
        const existing = claimed(current, scan) ? storedOriginal(current, scan) : current;
        if (typeof existing !== "function") return null;
        let inner = null;
        const claim = claimMember(net, name, scan, (original) => {
          inner = original;
          return function (...args) {
            // A scan request that cannot reach the host must not stop Steam's own call. Promise
            // rejection is handled explicitly; a try/catch only sees synchronous construction.
            void request(patchId, command, null).catch(() => {});
            return inner.apply(this, args);
          };
        });
        return claim.ok ? inner : null;
      };
      originalStart = wrap("StartScanningForNetworks", "startScan");
      originalStop = wrap("StopScanningForNetworks", "stopScan");
      scanWrapped = !!(originalStart || originalStop);
    };
    const unwrapScanning = () => {
      const net = window.SteamClient?.System?.Network;
      if (!net || !scanWrapped) return;
      releaseMember(net, "StartScanningForNetworks", scan);
      releaseMember(net, "StopScanningForNetworks", scan);
      originalStart = null;
      originalStop = null;
      scanWrapped = false;
    };
    const remove = () => {
      unwrapScanning();
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      removeNetworkState(true);
      if (!target) return { ok: true, absent: true };
      const released = releaseAccessor(target, property, availability);
      if (!released.ok) {
        lastError = released.error ?? "network availability release failed";
        return { ok: false, error: lastError };
      }
      target = null;
      return { ok: true, removed: true };
    };
    const status = () => {
      const instance = store();
      return {
        ok: true,
        installed: !!target,
        available: instance ? instance[property] === true : false,
        // Reported because the row can be on while the list is empty: Steam's Windows backend
        // never populates wireless.aps, so an access point count of zero here means the host has not
        // supplied one, not that the machine cannot see any networks.
        accessPoints: Array.isArray(instance?.accessPoints) ? instance.accessPoints.length : -1,
        hasWirelessDevice: instance?.hasWirelessDevice === true,
        scanWrapped,
        lastError,
      };
    };
    return { install, remove, status };
  }
  registerGate("network", createNetworkGate());
  // Custom pages inside Steam's Game Mode UI.
  //
  // Mapped against the live client on 2026-09-10, and cross-read against decky-loader's RouterHook
  // (b4b8be3) as evidence for the approach:
  //
  //   <memo>            source carries "Settings.Root()"; the router
  //     fd              Steam's own switch: computedMatch + TopLevelTransition, 31 route children
  //       <Route ...>   one per page, children of fd rather than rendered output
  //
  // `fd` is not react-router's Switch. Its source shows the selection rule: it walks `children`, takes
  // the FIRST valid element whose `path` matches, and clones it with `location` and `computedMatch`.
  // Two things follow, and both are in the API rather than hidden:
  //
  //   - appending is safe for a path Steam does not have, and overriding one of Steam's requires
  //     going in front of it, so a page declares which it wants;
  //   - routes are plain elements passed as `children`, so registering a page is a list operation on
  //     props. No descent into rendered output is needed, unlike the navigation panel, where entries
  //     do not exist until the root renders.
  //
  // The Route component is Steam's own, resolved from the module that carries "router-backstack",
  // never react-router's. That is what gives a custom page native back-navigation: Steam's Route
  // registers the match with the back stack, so B and the back gesture pop the page the way they pop
  // /settings. Using react-router's Route renders the same content and silently loses that.
  function createPageHost() {
    const patchId = "steam-ui.pages";
    const claimKeys = {
      marker: "__steamUiPageHostClaimed",
      original: "__steamUiPageHostOriginal",
    };
    // The router, unique on this pair. "Settings.Root()" alone matches six modules and
    // "TopLevelTransition" is the switch's own; together they name exactly one.
    const RouterTokens = ["Settings.Root()", "TopLevelTransition"];
    const BackstackToken = "router-backstack";
    // decky-loader's fingerprint for Steam's back-stack Route, confirmed against this client: the
    // export whose body threads the match's path into routePath.
    const RoutePattern = /routePath:.\.match\?\.path./u;
    // A path every build of the client has and no consumer would register, used to recognise the
    // route list among the router's children.
    const KnownRoute = "/library/home";
    const MaximumPages = 32;
    const MaximumDescent = 8;
    // The router sits about a hundred levels down the live tree, so the bound is generous; it exists
    // to stop a cyclic or pathological tree, not to limit a legitimate search.
    const MaximumNodesVisited = 60000;
    let runtime;
    let react;
    let RouteComponent = null;
    let memo = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let pages = [];
    let lastOutcome = "never rendered";
    let observedRoutes = [];
    const descendCache = new Map();
    // One registered page. The content is described by the host rather than supplied as a component:
    // a consumer's React lives in its own process, not in this asset, so what crosses the bridge is
    // data. A page renders its title and asks the host for its body, which is the same shape the
    // Quick Access rows already use.
    const renderPage = (page) =>
      react.createElement(
        "div",
        {
          className: "steam-ui-page",
          role: "region",
          "aria-label": page.title,
        },
        react.createElement("h1", null, page.title),
        react.createElement("div", { id: `steam-ui-page-body-${page.id}` }),
      );
    const buildRoute = (page) =>
      react.createElement(
        RouteComponent,
        { path: page.path, key: `steam-ui-page-${page.id}` },
        renderPage(page),
      );
    // Whether an array of elements is the router's route list.
    const isRouteList = (value) =>
      Array.isArray(value) &&
      value.length > 2 &&
      value.length < 512 &&
      value.some((item) => react.isValidElement(item) && item.props?.path === KnownRoute);
    // Inserts the registered pages into the route list.
    //
    // Overrides go in front of Steam's own routes and additions behind them, because the switch takes
    // the first match. Both keep their relative order, so two overrides of the same path resolve in
    // registration order rather than arbitrarily.
    const applyPages = (routes) => {
      observedRoutes = routes
        .filter((route) => react.isValidElement(route) && typeof route.props?.path === "string")
        .map((route) => route.props.path);
      const wanted = pages.slice(0, MaximumPages);
      if (!wanted.length) {
        lastOutcome = `routes=${routes.length} pages=0`;
        return routes;
      }
      const overrides = wanted.filter((page) => page.override === true).map(buildRoute);
      const additions = wanted.filter((page) => page.override !== true).map(buildRoute);
      lastOutcome = `routes=${routes.length} overrides=${overrides.length} additions=${additions.length}`;
      return [...overrides, ...routes, ...additions];
    };
    // Finds the route list in the router's returned element tree and replaces it.
    //
    // The list is found by content — the array holding a route for a path the client always has —
    // rather than by an index chain into props. decky-loader's gamepad path indexes
    // children.props.children[0].props.children, which is exactly the kind of selector that breaks on
    // a client update with no diagnostic; its own desktop path searches by /library/home instead, and
    // that is the half worth following.
    const replaceRouteList = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      const children = element.props?.children;
      if (isRouteList(children)) {
        return react.cloneElement(element, { children: applyPages(children) });
      }
      const kids = react.Children.toArray(children);
      if (!kids.length) return element;
      let changed = false;
      const next = [];
      for (const kid of kids) {
        const replacement = replaceRouteList(kid, depth + 1);
        changed ||= replacement !== kid;
        next.push(replacement);
      }
      return changed ? react.cloneElement(element, {}, ...next) : element;
    };
    const descend = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      const replaced = replaceRouteList(element, 0);
      if (replaced !== element) return replaced;
      const type = element.type;
      if (typeof type === "function" && !type.prototype?.isReactComponent) {
        let wrapper = descendCache.get(type);
        if (!wrapper) {
          wrapper = function SteamUiPageDescend(props) {
            return descend(type(props), 0);
          };
          descendCache.set(type, wrapper);
        }
        return react.createElement(
          wrapper,
          element.key === null ? element.props : { ...element.props, key: element.key },
        );
      }
      return element;
    };
    const resolve = () => {
      runtime = getWebpackRuntime("pages");
      const reactFactory = runtime.findUnique([
        "react.transitional.element",
        "useState",
        "cloneElement",
        "createElement",
      ]);
      if (!reactFactory) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      react = runtime(reactFactory[0]);
      const backstack = runtime.findUnique([BackstackToken]);
      if (!backstack) {
        lastError = "router-backstack module was not a unique match";
        return false;
      }
      const backstackExports = runtime(backstack[0]);
      const routes = Object.keys(backstackExports).filter(
        (name) =>
          typeof backstackExports[name] === "function" &&
          RoutePattern.test(String(backstackExports[name])),
      );
      if (routes.length !== 1) {
        lastError = `Steam's Route export was ${routes.length ? "ambiguous" : "absent"}`;
        return false;
      }
      RouteComponent = backstackExports[routes[0]];
      // The router module is confirmed to exist and to be unique, but it exports nothing that
      // reaches the router: the memo is built locally inside the module. Verified against the live
      // client on 2026-09-10 — every export of that module was inspected and none is a memo whose
      // type carries the marker. So the handle comes from the rendered tree instead, which is also
      // where decky-loader gets it. Checking the module anyway keeps the failure specific: "Steam
      // moved the router" and "the tree has not been built yet" are different problems.
      if (!runtime.findUnique([RouterTokens[0], RouterTokens[1]])) {
        lastError = "router module was not a unique match";
        return false;
      }
      memo = findRouterMemo();
      if (!memo) {
        lastError = "router was not found in the rendered tree";
        return false;
      }
      return true;
    };
    // Finds the router's memo through SharedJSContext's own React root.
    //
    // SharedJSContext holds the tree that every Steam window renders from, which is why a claim made
    // here reaches the Big Picture window and the menu window alike. The search is bounded in both
    // nodes visited and depth so a pathological tree cannot hang the injection, and it matches on the
    // component's source rather than on a path through the tree.
    const findRouterMemo = () => {
      const host = document.getElementById("root");
      if (!host) return null;
      const key = Object.keys(host).find((name) => name.startsWith("__reactContainer$"));
      if (!key) return null;
      const seen = new Set();
      let visited = 0;
      const walk = (node) => {
        if (!node || seen.has(node) || visited > MaximumNodesVisited) return null;
        seen.add(node);
        visited++;
        if (
          typeof node.type === "function" &&
          String(node.type).includes(RouterTokens[0]) &&
          node.elementType &&
          typeof node.elementType === "object" &&
          node.elementType.type === node.type
        ) {
          return node.elementType;
        }
        return walk(node.child) || walk(node.sibling);
      };
      return walk(host[key]);
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      try {
        if (!resolve()) return { ok: false, error: lastError };
      } catch (error) {
        lastError = "page host resolution failed: " + String(error);
        return { ok: false, error: lastError };
      }
      const claim = claimMember(memo, "type", claimKeys, (original) => {
        if (typeof original !== "function") return original;
        return function SteamUiPageRouter(props) {
          return descend(original(props), 0);
        };
      });
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        const declared = Array.isArray(state?.pages) ? state.pages : [];
        pages = declared
          .filter(
            (page) =>
              page &&
              typeof page.id === "string" &&
              typeof page.title === "string" &&
              typeof page.path === "string" &&
              // A path has to be absolute or Steam's matcher never sees it, and a page that claims
              // every route would black out the client.
              page.path.startsWith("/") &&
              page.path !== "/",
          )
          .slice(0, MaximumPages);
      });
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      pages = [];
      descendCache.clear();
      const released = releaseMember(memo, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "page host release failed";
        return { ok: false, error: lastError };
      }
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!memo,
      routeResolved: !!RouteComponent,
      claimed: memberClaimed(memo, "type", claimKeys),
      pages: pages.length,
      // What the last render actually saw. Everything above can be true while no page is reachable,
      // because insertion depends on finding the route list in the tree Steam rendered.
      routeCount: observedRoutes.length,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("pages", createPageHost());
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
    const patchId = "steam-ui.performance";
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    const store = () => window.SystemPerfStore ?? null;
    // The message class is never named here — it is taken from an instance the store builds, so
    // this stays correct across minification and client updates. An object argument is still
    // accepted because that is what a caller other than the store would pass, and an
    // undecodable one is forwarded as-is so the host logs a readable rejection instead of nothing.
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
        // game's own profile is the one being edited. "No game" is 769 — the Steam client's own
        // pseudo-app, the id Valve's components compare against — never "0".
        target.m_msgState.current_game_id = state.currentGameId ?? "769";
        target.m_msgState.active_profile_game_id = state.activeProfileGameId ?? "769";
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
      if (!store()) {
        lastError = "SystemPerfStore unavailable";
        return { ok: false, error: lastError };
      }
      // Every setter builds a protobuf delta and hands it to UpdateSettings, so that one method is
      // where all of them arrive. The delta is decoded on the host's side rather than here, because the
      // message shapes belong to the client and this half only forwards.
      const buildApi = () => ({
        // Decode first, always. SystemPerfStore's setters all end in
        // `UpdateSettings(request.serializeBase64String())`, so what arrives here is a BASE64
        // STRING, not the message — live-verified 2026-08-30 by round-tripping a request built by
        // the store itself. Forwarding it verbatim made the host's reader reject every write as
        // "carried no delta object", which is why no control on the Performance tab did anything:
        // the overlay-level selector snapped back to off, the frame cap never took, VRR never
        // toggled. Decoding through the message's OWN deserializeBinary keeps the wire format the
        // client's business; toObject() then emits snake_case field names, which is what the host reads.
        UpdateSettings: (payload) =>
          request(patchId, "updateSettings", { delta: decodeSettingsUpdate(payload) }, 0),
        RegisterForStateChanges: () => ({ unregister: () => {} }),
        RegisterForDiagnosticInfoChanges: () => ({ unregister: () => {} }),
      });
      // Stand aside for a real backend, reclaim one of our own — the primitive's rules, and it marks
      // the namespace before publishing it so nothing can observe an unmarked one. An orphaned Perf
      // namespace is worse than an orphaned audio one: it leaves SystemPerfStore holding half-written
      // state, which renders Valve's controls with no values behind them.
      const supplied = supplyNamespace(system, "Perf", ownedMarker, buildApi);
      if (!supplied.ok) {
        lastError = supplied.error;
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
          // rendering nothing rather than keeping the host's last answer.
          target.m_msgState.limits = undefined;
          target.m_msgState.settings = undefined;
          target.m_msgState.current_game_id = undefined;
          target.m_msgState.active_profile_game_id = undefined;
        } catch (error) {
          lastError = String(error);
        }
      }
      // Marker-checked, which this path was not: it deleted whatever was at System.Perf, so a real
      // backend appearing under a still-installed gate would have been removed by the host's own cleanup.
      const withdrawn = withdrawNamespace(window.SteamClient?.System, "Perf", ownedMarker);
      if (!withdrawn.ok) {
        lastError = withdrawn.error ?? "perf namespace withdrawal failed";
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
  registerGate("perf", createPerfNamespace());
  // Steam's own storage device manager, revived on Windows.
  //
  // Big Picture ships a complete SteamOS storage UI — drives, block devices, format, adopt, eject,
  // trim — and on Windows it never appears. Mapped against the live client on 2026-09-10: the whole
  // surface hangs off one question. Its hooks call
  //
  //   StorageDeviceManager.IsServiceAvailable#1
  //
  // through the WebUI service transport, and every other query is `enabled:` on that answer. The
  // Windows client has no service behind it, so the answer never arrives and the UI stays inert.
  //
  // The transport is where this is claimable. Each generated client resolves
  // `GetDefaultTransport().SendMsg(name, request, responseType, options)`, and `SendMsg` lives on the
  // transport prototype as a writable, configurable property. Claiming it on the *instance* scopes
  // the change to the one live transport and lets removal delete the own property so the prototype
  // method shows through again, untouched.
  //
  // Everything not addressed to StorageDeviceManager is forwarded to the original synchronously and
  // unexamined. This carries all of Steam's service traffic, so the filter is a name prefix checked
  // first and nothing else happens on that path.
  //
  // The service vocabulary, read from the client's own message classes:
  //
  //   IsServiceAvailable, GetState, StateChanged, Eject, Adopt, Format, Unmount, TrimAll
  //   CStorageDeviceManagerDrive        id, is_formattable, is_unformatted
  //   CStorageDeviceManagerBlockDevice  block_device_id, drive_id, mount_paths, has_steam_library
  //   CStorageDeviceManagerState        drives, block_devices, is_adopt_supported,
  //                                     is_unmount_supported, is_trim_supported, is_trim_running
  function createStorageService() {
    const patchId = "steam-ui.storage";
    const claimKeys = {
      marker: "__steamUiStorageClaimed",
      original: "__steamUiStorageOriginal",
    };
    const ServicePrefix = "StorageDeviceManager.";
    const TransportToken = "GetDefaultTransport";
    const ServiceToken = "StorageDeviceManager.IsServiceAvailable#1";
    // Claiming the transport is not enough, and this is the part that was wrong: Steam asks each of
    // these questions exactly once. Both queries are registered with `staleTime: 1/0`, and the state
    // query is `enabled:` on the availability answer, so the client's whole storage UI hangs off one
    // cached boolean.
    //
    // That answer is already cached by the time this gate can install. The availability query runs
    // when the first component using it mounts, which happens while the library is building itself --
    // before Steam's UI exists as a patch target at all. It goes to the real transport, the Windows
    // client has no service behind it, and the rejection is cached forever. From then on nothing asks
    // again: the drive menu's Eject and Format entries are gated on `is_unmount_supported` and
    // `is_adopt_supported` from a state query that is disabled, so they are simply absent, and no
    // StorageDeviceManager call is ever made for this gate to answer. Observed exactly that way on
    // the September 2026 beta: gate installed, resolved and claimed, 13 unrelated calls forwarded,
    // zero answered.
    //
    // Steam's own store solves this the same way when the service state changes -- it invalidates
    // both keys through the shared query client -- so this does what the client does, with the key
    // names read off the client's own module.
    const QueryClientTokens = ["ReactQueryDevtools", "offlineFirst"];
    const StorageQueryScope = "SystemStorageService";
    const AvailabilityQueryKey = [StorageQueryScope, "IsServiceAvailable"];
    const StateQueryKey = [StorageQueryScope, "State"];
    // A machine with more drives than this is not a handheld, and the state is rendered as rows.
    const MaximumDrives = 32;
    let runtime;
    let transport = null;
    let queryClient = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let invalidated = 0;
    // What was last published, as the shape Steam would read. A publication that says the same thing
    // must not invalidate: the host republishes on its own poll, and invalidating each time would put
    // a GetState and a re-render on every tick for storage that has not changed.
    let stateSignature = "";
    // What the host says the machine's storage looks like. Empty until it publishes, and an empty
    // state is still answered: "no removable drives" is a truthful answer and the page renders it,
    // where refusing to answer leaves Steam's spinner up forever.
    let state = {
      drives: [],
      block_devices: [],
      is_adopt_supported: false,
      is_unmount_supported: false,
      is_trim_supported: false,
      is_trim_running: false,
    };
    let answered = 0;
    let forwarded = 0;
    let lastMethod = "";
    let lastPayload = "";
    let lastRequest = "";
    /// One of Steam's uint32 identifiers, whatever JavaScript type it arrived as. Zero for anything
    /// that is not a usable identifier, which is the wire's own "not named".
    const asId = (value) => {
      const parsed = typeof value === "string" ? Number(value) : value;
      return typeof parsed === "number" && Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
    };
    // Every route a generated message exposes a field through, tried in order. Steam's messages
    // define prototype accessors named after the declared field (that is what the `prototype.x ||
    // Sg(M())` guard at construction installs), `toObject()` may spell the key either way, and a
    // plain object hands the field back untouched. Reading only toObject() under the declared
    // spelling found nothing, so every action forwarded zero for both identifiers and the host
    // refused it as naming neither a volume nor a drive — the exact line the log printed for
    // Steam's eject. The identifier being asked for is the one this gate published, echoed back;
    // losing it here is losing our own number.
    const readId = (request, fields, declared, accessor) => {
      // The generated prototype accessor is a function named after the declared field (that is
      // what `Sg` installs: `proto[field] = () => getField(this, n)`), so on the body it has to be
      // called, not read -- reading it hands back the function, which is not an identifier.
      const body = typeof request?.Body === "function" ? request.Body() : request;
      const candidates = [
        fields?.[declared],
        fields?.[accessor],
        typeof body?.[declared] === "function" ? body[declared]() : body?.[declared],
      ];
      for (const value of candidates) {
        const id = asId(value);
        if (id > 0) return id;
      }
      return 0;
    };
    // The shape of the last action request, for the status probe: whether it was a message or a
    // plain object, what toObject() yielded, and which own and prototype names it carried. This is
    // what turns "forwarded zero" from a guess about the encoder into a fact about it.
    const describeRequest = (request, fields) => {
      try {
        const proto = request ? Object.getPrototypeOf(request) : null;
        return JSON.stringify({
          type: request?.constructor?.name ?? typeof request,
          hasToObject: typeof request?.toObject === "function",
          fieldKeys: fields && typeof fields === "object" ? Object.keys(fields).slice(0, 12) : [],
          ownKeys: request && typeof request === "object" ? Object.keys(request).slice(0, 12) : [],
          protoKeys: proto ? Object.getOwnPropertyNames(proto).slice(0, 24) : [],
        });
      } catch (error) {
        return "describe failed: " + String(error);
      }
    };
    // Steam's callers only ever ask a response two things, so the response is duck-typed rather than
    // built as a protobuf. Constructing a real Message would mean owning the wire format, which is
    // the client's business and not something this should mirror.
    // k_EResultOK. The action methods do not read the body at all: they return
    // `(await ...).GetEResult()` and the caller compares it against OK, so a response without that
    // method throws "GetEResult is not a function" inside an async click handler with nothing
    // attached to it. The button then does precisely nothing, with no error anywhere — which is what
    // eject did until this was found, while the read paths worked because the hooks use BSuccess and
    // Body instead.
    const ResultOk = 1;
    const ok = (body) =>
      Promise.resolve({ BSuccess: () => true, Body: () => body, GetEResult: () => ResultOk });
    const failed = (reason) =>
      Promise.resolve({
        BSuccess: () => false,
        Body: () => ({}),
        // 2 is k_EResultFail: a refusal has to be a result the caller can compare, not an absent
        // method that throws where the comparison would have been.
        GetEResult: () => 2,
        GetErrorMessage: () => reason,
      });
    // The request arrives already encoded. Steam's encoder yields a Message, which answers toObject(),
    // so the fields are readable without decoding bytes; anything that does not is treated as empty
    // rather than guessed at.
    // The request SendMsg receives is not the message. Steam's encoder (`I8`) wraps the generated
    // message in an envelope -- `P.InitFromObject(T, fields)` -- that carries a header beside it, and
    // the fields live on `Body()`. Reading toObject() off the envelope produced no fields at all,
    // so every action forwarded zero for both identifiers and was refused as naming neither a
    // volume nor a drive: the number being lost was the one this gate had published and Steam was
    // handing straight back. The body's toObject() (Steam's own `BT`) keys by the declared field
    // name, so block_device_id comes back spelled exactly as the message declares it.
    const readRequest = (request) => {
      try {
        const body = typeof request?.Body === "function" ? request.Body() : request;
        const fields = typeof body?.toObject === "function" ? body.toObject() : body;
        return fields && typeof fields === "object" ? fields : {};
      } catch {
        return {};
      }
    };
    const handle = (name, request) => {
      lastMethod = name;
      answered++;
      const method = name.slice(ServicePrefix.length).split("#")[0];
      const fields = readRequest(request);
      switch (method) {
        case "IsServiceAvailable":
          return ok({ is_available: () => true });
        case "GetState":
          return ok({ toObject: () => ({ state }) });
        // Every action is the host's to perform: this half owns no storage operation, which is what
        // keeps Windows formatting and ejecting in one place rather than two.
        case "Adopt":
        case "Unmount":
        case "Eject":
        case "Format":
        case "TrimAll": {
          const command = method.toLowerCase();
          // Steam's identifiers are uint32 on the wire, and Unmount names the volume while the
          // drive-level actions name the drive. Zero means "not named": the client numbers these
          // from one, so it is unambiguous and the host refuses rather than guessing.
          //
          // Numbers are coerced rather than type-tested. The encoder hands these back through
          // toObject(), which is not obliged to return the same JavaScript type it was given, and a
          // strict typeof test turned an identifier that arrived as a string into zero — which the
          // host then refuses as "named neither a volume nor a drive", silently, because a refusal
          // that never reaches a backend logs nothing.
          // Adopt is Steam's SteamOS "make this drive a library": its Format Drive modal sends
          // Adopt, not Format, with the name the user typed and a validate flag. Both travel, so the
          // host can tell a register-only adopt from an erase-and-register one and label the result.
          const body = typeof request?.Body === "function" ? request.Body() : request;
          const readText = (name) => {
            const value =
              fields?.[name] ?? (typeof body?.[name] === "function" ? body[name]() : undefined);
            return typeof value === "string" ? value.slice(0, 64) : "";
          };
          const readFlag = (name) => {
            const value =
              fields?.[name] ?? (typeof body?.[name] === "function" ? body[name]() : undefined);
            return value === true;
          };
          const payload = {
            driveId: readId(request, fields, "drive_id", "driveId"),
            blockDeviceId: readId(request, fields, "block_device_id", "blockDeviceId"),
            label: readText("label"),
            validate: readFlag("validate"),
          };
          lastPayload = JSON.stringify(payload);
          lastRequest = describeRequest(request, fields);
          request0(command, payload);
          return ok({ toObject: () => ({}) });
        }
        default:
          return failed(`unhandled storage method ${method}`);
      }
    };
    // Fire-and-forget: Steam's UI does not wait on the action's own response, it waits for the state
    // to change. Reporting the outcome is the host's job through the next publication.
    const request0 = (command, payload) => {
      try {
        request(patchId, command, payload).catch(() => {});
      } catch {
        // An unallowlisted command must not take the transport down with it.
      }
    };
    const resolve = () => {
      runtime = getWebpackRuntime("storage");
      // The service module names the method this whole surface is gated on.
      if (!runtime.findUnique([ServiceToken])) {
        lastError = "storage service module was not a unique match";
        return false;
      }
      // The transport provider: exactly one module exports a function returning an object with
      // GetDefaultTransport.
      const ids = runtime.findUnique([TransportToken, "m_transport"]);
      if (!ids) {
        lastError = "transport provider was not a unique match";
        return false;
      }
      const exports = runtime(ids[0]);
      const keys = Object.keys(exports).filter((name) => typeof exports[name] === "function");
      for (const key of keys) {
        try {
          const provider = exports[key]();
          const candidate = provider?.GetDefaultTransport?.();
          if (candidate && typeof candidate.SendMsg === "function") {
            transport = candidate;
            return true;
          }
        } catch {
          // Not the provider; keep looking.
        }
      }
      lastError = "no export yielded a transport";
      return false;
    };
    // The one shared query client, found by the module that builds it rather than by a name: it is
    // constructed once beside the provider and the devtools element, and exported as a plain object.
    // Duck-typed on invalidateQueries for the same reason the transport is duck-typed on SendMsg --
    // the export names are minified and change between builds, the shape does not.
    const resolveQueryClient = () => {
      const ids = runtime.findUnique(QueryClientTokens);
      if (!ids) return null;
      const exports = runtime(ids[0]);
      for (const key of Object.keys(exports)) {
        const candidate = exports[key];
        if (candidate && typeof candidate.invalidateQueries === "function") {
          return candidate;
        }
      }
      return null;
    };
    // Never fatal. A gate that answers Steam's questions is still strictly better than one that does
    // not, and the alternative to a missed invalidation is refusing to install at all.
    const invalidate = (queryKey) => {
      if (!queryClient) return;
      try {
        queryClient.invalidateQueries({ queryKey });
        invalidated++;
      } catch (error) {
        lastError = "invalidate failed: " + String(error);
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      try {
        if (!resolve()) return { ok: false, error: lastError };
      } catch (error) {
        lastError = "storage transport resolution failed: " + String(error);
        return { ok: false, error: lastError };
      }
      const claim = claimMember(transport, "SendMsg", claimKeys, (original) => {
        if (typeof original !== "function") return original;
        return function SteamUiStorageSendMsg(name, request, response, options) {
          // Prefix first and nothing else on the pass-through path: this method carries every
          // service call Steam makes.
          if (typeof name === "string" && name.startsWith(ServicePrefix)) {
            return handle(name, request);
          }
          forwarded++;
          return original.call(this, name, request, response, options);
        };
      });
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      queryClient = resolveQueryClient();
      unsubscribe = subscribe(patchId, (published) => {
        if (!published || typeof published !== "object") return;
        const drives = Array.isArray(published.drives) ? published.drives : [];
        const devices = Array.isArray(published.blockDevices) ? published.blockDevices : [];
        // Every field Steam declares, not only the ones an action needs. The client formats what it
        // is given without checking it got anything: a drive with no size_bytes renders "NaN B of
        // NaN B", and one with no adopt_stage renders a spinner forever, because undefined compares
        // unequal to the idle stage. Both were observed on the live page before this.
        state = {
          drives: drives.slice(0, MaximumDrives).map((drive) => ({
            id: Number(drive?.id ?? 0),
            model: String(drive?.model ?? ""),
            vendor: String(drive?.vendor ?? ""),
            serial: "",
            is_ejectable: drive?.ejectable === true,
            size_bytes: String(drive?.sizeBytes ?? 0),
            media_type: 0,
            is_unformatted: drive?.unformatted === true,
            // 1, not 0. Steam's adopt stage is a seven-value enum whose first member is Invalid and
            // whose second is the idle one the drive icon is gated on: `adopt_stage != 1` renders a
            // spinner. Publishing 0 spun the row forever, which looked exactly like omitting the
            // field and was diagnosed twice as that before the enum was read off the client.
            adopt_stage: 1,
            is_formattable: drive?.formattable === true,
            is_media_available: drive?.mediaAvailable !== false,
          })),
          block_devices: devices.slice(0, MaximumDrives).map((device) => ({
            id: Number(device?.id ?? 0),
            drive_id: Number(device?.driveId ?? 0),
            path: String(device?.friendlyPath ?? ""),
            friendly_path: String(device?.friendlyPath ?? ""),
            label: String(device?.label ?? ""),
            size_bytes: String(device?.sizeBytes ?? 0),
            is_formattable: false,
            is_read_only: false,
            is_root_device: false,
            content_type: 0,
            filesystem_type: 0,
            mount_paths: Array.isArray(device?.mountPaths) ? device.mountPaths.map(String) : [],
            is_unmounting: false,
            has_steam_library: device?.hasSteamLibrary === true,
          })),
          is_adopt_supported: published.adoptSupported === true,
          is_unmount_supported: published.unmountSupported === true,
          is_trim_supported: published.trimSupported === true,
          is_trim_running: published.trimRunning === true,
        };
        const signature = JSON.stringify(state);
        if (signature === stateSignature) return;
        stateSignature = signature;
        invalidate(StateQueryKey);
      });
      // Availability first: the state query stays disabled until that answer changes, so invalidating
      // the state key alone would drop the refetch on the floor.
      invalidate(AvailabilityQueryKey);
      invalidate(StateQueryKey);
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      installed = false;
      if (unsubscribe) {
        unsubscribe();
        unsubscribe = null;
      }
      const released = releaseMember(transport, "SendMsg", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "storage transport release failed";
        return { ok: false, error: lastError };
      }
      // Leaving the answers cached would leave Steam's pages offering Eject and Format against a
      // service that is no longer claimed, and the first press would reach a transport with nothing
      // behind it. Asking again puts the client back on its own answer, which is "unavailable".
      invalidate(AvailabilityQueryKey);
      invalidate(StateQueryKey);
      stateSignature = "";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!transport,
      claimed: memberClaimed(transport, "SendMsg", claimKeys),
      // Whether the client's cached answers were dropped. Zero here with answered also zero is the
      // signature of the failure this exists for: claimed, but Steam never asks.
      queryClient: !!queryClient,
      invalidated,
      drives: state.drives.length,
      blockDevices: state.block_devices.length,
      // Everything above can be true while the page shows nothing, because Steam only asks once its
      // own route is open. These say whether it ever asked.
      answered,
      forwarded,
      lastMethod,
      // What the last action actually forwarded. An identifier that arrived in an unexpected shape
      // is the difference between a press the host refused and a press it never saw.
      lastPayload,
      lastRequest,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("storage", createStorageService());
  function createNativeComponentHost() {
    const registrations = new Map();
    const listeners = new Set();
    let runtime;
    let controlRuntime;
    let autoTdpControl;
    let frameLimitControl;
    let controllerControl;
    let powerProfileControl;
    let hybridCoreControl;
    let powerPresetControl;
    let resolutionControl;
    let vrrControl;
    let deviceControlsControl;
    // Valve's profile header and its per-game profile toggle. On the current client they are TWO
    // exports of the perf-components module — re-probed 2026-09-02 after the header rendered with
    // no way to enable a profile: the toggle's token resolves uniquely on its own, so each mounts
    // as its own row under the one valveProfileHeader kind. And Valve's reset button. All are
    // additive: the host built none of them.
    let valveProfileHeaderControl;
    let valveProfileToggleControl;
    let valveResetControl;
    let valveRefreshRateControl;
    let valveOverlayLevelControl;
    let powerLimitControl;
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
    let lastPatchError = "";
    // One entry per wrapped tab, because "the perf panel appended fine" and "Quick Settings never
    // rendered" are different facts that a single field could only report as one.
    const appendDiagnostics = {
      perf: null,
      quickSettings: null,
    };
    // Why each control did or did not draw. A control that renders null leaves no trace anywhere:
    // the row is built and appended, the panel simply has one fewer child, and every other signal
    // still reports success. This is the difference between "the host did not add it" and "the host added
    // it and the device had nothing to show".
    const renderOutcomes = {};
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
      autoTdp: Object.freeze({
        patchId: "steam-ui.auto-tdp",
        command: "setAutoTdp",
      }),
      // Two commands, because this is SteamOS's unified row: one slider that is the frame cap while
      // a cap is set and the refresh rate once it is switched off.
      frameLimit: Object.freeze({
        patchId: "steam-ui.frame-limit",
        command: "setFrameLimit",
        refreshCommand: "setRefreshRate",
      }),
      controllerTarget: Object.freeze({
        patchId: "steam-ui.controller-target",
        command: "setControllerTarget",
      }),
      powerProfile: Object.freeze({
        patchId: "steam-ui.power-profile",
        command: "setPowerProfile",
      }),
      hybridCores: Object.freeze({
        patchId: "steam-ui.hybrid-cores",
        command: "setHybridCores",
      }),
      powerPreset: Object.freeze({
        patchId: "steam-ui.power-preset",
        acCommand: "setAcPowerPreset",
        batteryCommand: "setBatteryPowerPreset",
      }),
      // Hand-built for the same reason resolution is: Valve ships a component, and its gate is a
      // namespace this client does not have. See createVrrControl.
      vrr: Object.freeze({
        patchId: "steam-ui.variable-refresh",
        command: "setVariableRefreshRate",
      }),
      // Hand-built, unlike the frame limit and VRR rows. SteamOS drives resolution through
      // gamescope and this client ships no component for it, so there is nothing to mount.
      resolution: Object.freeze({
        patchId: "steam-ui.resolution",
        command: "setResolution",
      }),
      deviceControls: Object.freeze({
        patchId: "steam-ui.device-controls",
        chargeCommand: "setChargeLimit",
        brightnessCommand: "setLightingBrightness",
        colorCommand: "setLightingColor",
      }),
      // Valve's own components. They carry no command because they never call the host directly: they
      // read SystemPerfStore and write through SteamClient.System.Perf.UpdateSettings, which is the
      // perf patch's vocabulary, not theirs. They still need an entry here — install() refuses any
      // kind that is not a declared definition.
      valveProfileHeader: Object.freeze({
        patchId: "steam-ui.valve-profile-header",
        command: "",
      }),
      valveReset: Object.freeze({
        patchId: "steam-ui.valve-reset",
        command: "",
      }),
      // Valve's own refresh-rate row, mounted into Quick Settings per S14. It reads
      // limits.display_refresh_manual_hz_* from SystemPerfStore, which the projection supplies only
      // under FrameLimitOnly — the strategy gate is the state, not a check here.
      valveRefreshRate: Object.freeze({
        patchId: "steam-ui.valve-refresh-rate",
        command: "",
      }),
      // Valve's performance-overlay selector replaces the retired hand-rolled imitation.
      valveOverlayLevel: Object.freeze({
        patchId: "steam-ui.valve-overlay-level",
        command: "",
      }),
      powerLimit: Object.freeze({
        patchId: "steam-ui.power-limit",
        primaryCommand: "setPrimaryLimit",
        boostCommand: "setBoostLimit",
        modeCommand: "setUnifiedMode",
      }),
    });
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
    const uniqueFactory = (requiredTokens) => runtime.findUnique(requiredTokens);
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
      // Valve's read-only label/value row, from the Field module rather than the fields module: it
      // is what a figure the panel only reports — the profile actually in effect — is supposed to
      // look like. Without it that line was a bare div with none of Steam's type, spacing or
      // separator, which is exactly how it read. `#Field_MoreInfo_Action` occurs once in the whole
      // client bundle, so the module is unambiguous, and only this export draws LabelFieldValue.
      const labelFieldFactory = uniqueFactory([
        "#Field_MoreInfo_Action",
        "spacingBetweenLabelAndChild",
      ]);
      const labelField = labelFieldFactory
        ? uniqueFunction(runtime(labelFieldFactory[0]), [
            "LabelFieldValue",
            "spacingBetweenLabelAndChild",
          ])
        : null;
      const section = uniqueFunction(layout, ["PanelSectionTitle", "spinner"]);
      const row = uniqueObject(
        layout,
        (value) => value.$$typeof && typeof value.render === "function",
      );
      const localize = uniqueFunction(localization, ["LocalizeString(e)", "void 0===r?e"]);
      if (!slider || !dropdown || !section || !row || !localize) return null;
      // The toggle and the label field are deliberately not in that guard. They arrived after the
      // other four, so a client where either cannot be found still gets every control that does not
      // need one, rather than losing the whole native surface.
      // The icon renderer is built once per control runtime and closes over Steam's React, so a row
      // asks for a glyph by name and never touches element construction itself.
      const icon = createIconRenderer(react);
      return { react, slider, dropdown, toggle, labelField, section, row, localize, icon };
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
    const normalizeControllerState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (!Array.isArray(value.targets) || value.targets.length > 8) return null;
      const targets = [];
      const ids = new Set();
      for (const item of value.targets) {
        if (!item || typeof item !== "object") return null;
        const id = normalizeText(item.id);
        const label = normalizeText(item.label);
        // Uppercase is allowed because the ids the host actually sends are PascalCase —
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
      // Only what a row actually reads. This validator once also demanded readbackQuality,
      // policyLayer and adapterAvailability — enums no component consumed and, after the review
      // simplification deleted their only publisher, no state carried: every frame-limit
      // delivery was rejected and the row silently vanished from the QAM (device-observed
      // 2026-09-02, the first dogfooding find).
      const progress = validEnum(value.progress, [
        "idle",
        "queued",
        "applying",
        // A write the host accepted and stored but has not made yet, because what it applies to
        // is not addressable right now — WSGM reports it when Steam has named a running game
        // whose executable Windows has not exposed, so the cap is saved against the game rather
        // than sprayed onto the global profile. It was missing from this list, and a settled
        // outcome the host can legitimately report was therefore rejected as malformed: adjusting
        // the frame-limit slider while a game was starting deleted the row the user had just
        // touched (Claw, 2026-09-04). Not busy — the value is stored, and the row stays live.
        "deferred",
        "succeeded-verified",
        "applied-unverified",
        "rejected",
        "timed-out",
        "indeterminate",
        "failed",
        "external-change",
      ]);
      if (!progress) return null;
      return Object.freeze({
        available: value.available,
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
    const normalizeDeviceRange = (value) => {
      if (value === null || value === undefined) return null;
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      const minimum = Number(value.minimum);
      const maximum = Number(value.maximum);
      const step = Number(value.step);
      const desired = value.desired === null ? null : Number(value.desired);
      const observed = value.observed === null ? null : Number(value.observed);
      if (
        !Number.isInteger(minimum) ||
        !Number.isInteger(maximum) ||
        !Number.isInteger(step) ||
        minimum < 0 ||
        maximum > 100 ||
        minimum >= maximum ||
        step < 1 ||
        step > maximum - minimum ||
        (desired !== null &&
          (!Number.isInteger(desired) ||
            desired < minimum ||
            desired > maximum ||
            (desired - minimum) % step !== 0)) ||
        (observed !== null &&
          (!Number.isInteger(observed) ||
            observed < minimum ||
            observed > maximum ||
            (observed - minimum) % step !== 0))
      )
        return null;
      return Object.freeze({
        available: value.available,
        minimum,
        maximum,
        step,
        desired,
        observed,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      });
    };
    const normalizeDeviceControlsState = (value) => {
      if (!value || typeof value !== "object" || !Array.isArray(value.lightingZones)) return null;
      const chargeLimit = normalizeDeviceRange(value.chargeLimit);
      const lightingBrightness = normalizeDeviceRange(value.lightingBrightness);
      const lightingZones = [];
      const ids = new Set();
      for (const zone of value.lightingZones.slice(0, 16)) {
        if (!zone || typeof zone !== "object") return null;
        const id = normalizeText(zone.id);
        const label = normalizeText(zone.label);
        const desiredColor = zone.desiredColor === null ? null : Number(zone.desiredColor);
        const observedColor = zone.observedColor === null ? null : Number(zone.observedColor);
        if (
          id.length > 64 ||
          !id.trim() ||
          !label ||
          ids.has(id) ||
          (desiredColor !== null &&
            (!Number.isInteger(desiredColor) || desiredColor < 0 || desiredColor > 0xffffff)) ||
          (observedColor !== null &&
            (!Number.isInteger(observedColor) || observedColor < 0 || observedColor > 0xffffff))
        )
          return null;
        ids.add(id);
        lightingZones.push(
          Object.freeze({
            id,
            label,
            available: zone.available === true,
            desiredColor,
            observedColor,
            progress: normalizeText(zone.progress),
            statusText: normalizeText(zone.statusText),
          }),
        );
      }
      return Object.freeze({
        chargeLimit,
        lightingBrightness,
        lightingZones: Object.freeze(lightingZones),
      });
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
      // A cap only has to be something the limiter could hold. It is deliberately NOT required to
      // sit between the bookends: a host that raised its floor, or a limiter written behind the
      // host's back, would otherwise publish a state that deleted the whole row — and this row is
      // the only place the user could have corrected the value. Observed on a Claw (2026-09-03),
      // where a 12 FPS cap under a floor of 30 took the Quick Access slider away entirely and left
      // no way to put it back. The bookends stretch to reach the value instead.
      const capUnusable = (fps) =>
        fps !== null && (!Number.isInteger(fps) || fps < 0 || fps > 1000);
      if (
        (minimumFps !== null &&
          maximumFps !== null &&
          (!Number.isInteger(minimumFps) ||
            !Number.isInteger(maximumFps) ||
            minimumFps < 0 ||
            maximumFps < minimumFps ||
            maximumFps > 1000)) ||
        capUnusable(desiredFps) ||
        capUnusable(observedFps) ||
        (common.available && minimumFps === null)
      )
        return null;
      // Zero is OFF and is deliberately outside the slider's range, which starts at a cap worth
      // playing at, so it is the one value that never stretches a bookend.
      let lowestFps = minimumFps;
      let highestFps = maximumFps;
      if (lowestFps !== null && highestFps !== null) {
        for (const fps of [desiredFps, observedFps]) {
          if (fps === null || fps <= 0) continue;
          if (fps < lowestFps) lowestFps = fps;
          if (fps > highestFps) highestFps = fps;
        }
      }
      // Cap to refresh rate, for the "(60 Hz)" half of the label. Absent under the uncoupled
      // strategy, where a cap moves no display mode and there is nothing to name.
      const refreshForCap = new Map();
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
      const refreshRates = [];
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
        minimumFps: lowestFps,
        maximumFps: highestFps,
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
    const useSemanticState = (controlRuntime, kind, normalize) => {
      const definition = definitions[kind];
      const [state, setState] = controlRuntime.react.useState(null);
      controlRuntime.react.useEffect(
        () =>
          subscribe(definition.patchId, (value) => {
            const normalized = normalize(value);
            // A state that arrives and fails validation is not the same as one that never
            // arrived, and both used to end as a null the control returned on. The controller row
            // was invisible for exactly this reason: the host sends PascalCase target ids and the
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
          if (typeof next === "number" && Number.isFinite(next) && next !== observed) commit(next);
        },
      };
    };
    /// Coalesces expensive device-persistent writes while preserving the last value.
    /// A colour is edited through three sliders; committing each component separately can queue
    /// stale intermediate colours behind a firmware write-rate limit. The last edit replaces the
    /// pending one, and unmount flushes it so closing QAM cannot lose the user's final colour.
    const useTrailingCommit = (controlRuntime, delayMilliseconds, commit) => {
      const pending = controlRuntime.react.useRef(null);
      const timer = controlRuntime.react.useRef(null);
      const commitRef = controlRuntime.react.useRef(commit);
      commitRef.current = commit;
      const flush = () => {
        if (timer.current !== null) {
          globalThis.clearTimeout(timer.current);
          timer.current = null;
        }
        const value = pending.current;
        pending.current = null;
        if (value !== null) commitRef.current(value);
      };
      controlRuntime.react.useEffect(
        () => () => {
          flush();
        },
        [],
      );
      return (value) => {
        pending.current = value;
        if (timer.current !== null) globalThis.clearTimeout(timer.current);
        timer.current = globalThis.setTimeout(flush, delayMilliseconds);
      };
    };
    // Steam's localizer returns the token itself when it has no string for it, which is truthy and
    // would render "#QuickAccess_..." as a label. Live-verified 2026-08-29: a known token localizes,
    // an unknown one comes straight back.
    //
    // EVERY label goes through this, not only the host-invented ones. With the rows finally
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
    // The fallback still earns its place, for the labels the host invents and Valve has no string for
    // (AutoTDP, the display-resolution row). Those pass no token at all rather than a plausible
    // one: a token that does not exist makes Steam log an unresolved string on every render and
    // still shows the English text.
    // Steam's localizer does not return a string. It returns a React element wrapping one, so
    // `typeof text === "string"` was false for every token and every the host label fell back to its
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
    // The host's own variable-refresh switch. Valve ships one, and it cannot be used: its component is
    // gated on a react-query over SteamClient.System.DisplayManager, whose GetState this client
    // does not define — the query never succeeds and the component returns null before it reads a
    // single field the host publishes (live-probed 2026-08-30). The device capability behind this row
    // is the one already verified on the reference unit through IGCL Arc Sync.
    const createVrrControl = (controlRuntime) =>
      function SteamUiVrrControl() {
        const state = useSemanticState(controlRuntime, "vrr", normalizeVrrState);
        if (!state) return note("vrr", "no state");
        if (!state.available)
          return note("vrr", "unavailable: " + (state.statusText || "no reason"));
        if (!controlRuntime.toggle) return note("vrr", "Steam ToggleField was not resolved");
        renderOutcomes.vrr = "rendered";
        const definition = definitions.vrr;
        return controlRuntime.react.createElement(controlRuntime.toggle, {
          // Valve's own token for the row, so the label matches the client's language even though
          // the component behind it is the host's.
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_EnableVRR",
            "Variable refresh rate",
          ),
          icon: controlRuntime.icon("pulse"),
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
      function SteamUiAutoTdpControl() {
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
          // The host's own control; Valve has no string for it, so no token is passed.
          label: "Automatic TDP",
          icon: controlRuntime.icon("auto"),
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
    const normalizePowerProfileState = (value) => {
      if (
        !value ||
        typeof value.available !== "boolean" ||
        !Array.isArray(value.options) ||
        value.options.length > 64
      )
        return null;
      const ids = new Set();
      const options = [];
      for (const item of value.options) {
        const label = normalizeText(item?.label);
        if (
          !item ||
          typeof item.id !== "string" ||
          !/^[A-Za-z0-9._-]{1,64}$/.test(item.id) ||
          !label.trim() ||
          ids.has(item.id)
        )
          return null;
        ids.add(item.id);
        options.push({ id: item.id, label });
      }
      return {
        available: value.available,
        options,
        current: normalizeText(value.current),
        statusText: normalizeText(value.statusText),
      };
    };
    const createPowerProfileControl = (controlRuntime) =>
      function SteamUiPowerProfileControl() {
        const kind = "powerProfile";
        const state = useSemanticState(controlRuntime, kind, normalizePowerProfileState);
        const [pending, setPending] = controlRuntime.react.useState(false);
        if (!state) return note(kind, "no state");
        const options = state.options.map((option) => ({ data: option.id, label: option.label }));
        const definition = definitions[kind];
        renderOutcomes[kind] = "rendered";
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: "Windows power profile",
          icon: controlRuntime.icon("power"),
          rgOptions: options,
          selectedOption: options.some((option) => option.data === state.current)
            ? state.current
            : undefined,
          disabled: pending || !state.available || options.length < 2,
          description: state.statusText || undefined,
          layout: "below",
          onChange: (option) => {
            if (
              pending ||
              !state.available ||
              !option ||
              option.data === state.current ||
              !options.some((candidate) => candidate.data === option.data)
            )
              return;
            setPending(true);
            void request(
              definition.patchId,
              definition.command,
              { target: option.data },
              nextActionGeneration(definition.patchId),
            )
              .catch(() => {})
              .finally(() => setPending(false));
          },
        });
      };
    // The same dropdown as the row above, published from the same state shape. It is written out
    // rather than shared with it because each control's glyph is read from the literal at its own
    // icon() call: a factory taking the name as an argument makes both rows invisible to the
    // ownership check that proves every glyph is placed exactly once.
    const createHybridCoreControl = (controlRuntime) =>
      function SteamUiHybridCoreControl() {
        const kind = "hybridCores";
        const state = useSemanticState(controlRuntime, kind, normalizePowerProfileState);
        const [pending, setPending] = controlRuntime.react.useState(false);
        if (!state) return note(kind, "no state");
        const options = state.options.map((option) => ({ data: option.id, label: option.label }));
        const definition = definitions[kind];
        renderOutcomes[kind] = "rendered";
        return controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: "Processor cores",
          icon: controlRuntime.icon("cores"),
          rgOptions: options,
          selectedOption: options.some((option) => option.data === state.current)
            ? state.current
            : undefined,
          disabled: pending || !state.available || options.length < 2,
          description: state.statusText || undefined,
          layout: "below",
          onChange: (option) => {
            if (
              pending ||
              !state.available ||
              !option ||
              option.data === state.current ||
              !options.some((candidate) => candidate.data === option.data)
            )
              return;
            setPending(true);
            void request(
              definition.patchId,
              definition.command,
              { target: option.data },
              nextActionGeneration(definition.patchId),
            )
              .catch(() => {})
              .finally(() => setPending(false));
          },
        });
      };
    const normalizePowerPresetState = (value) => {
      const state = normalizePowerProfileState(value);
      if (!state || typeof value.ac !== "string" || typeof value.battery !== "string") return null;
      const valid = (id) => id === "" || state.options.some((option) => option.id === id);
      if (
        !valid(value.ac) ||
        !valid(value.battery) ||
        (state.options.some((option) => option.id === "custom") &&
          value.ac !== "custom" &&
          value.battery !== "custom")
      )
        return null;
      return {
        ...state,
        ac: value.ac,
        battery: value.battery,
        scope: normalizeText(value.scope),
        unsetLabel: normalizeText(value.unsetLabel),
      };
    };
    const createPowerPresetControl = (controlRuntime) =>
      function SteamUiPowerAssignments() {
        const state = useSemanticState(controlRuntime, "powerPreset", normalizePowerPresetState);
        const [pending, setPending] = controlRuntime.react.useState(false);
        if (!state || !state.options.length) return note("powerPreset", "no state");
        const options = [
          { data: "", label: state.unsetLabel || "Manual selection" },
          ...state.options.map((option) => ({ data: option.id, label: option.label })),
        ];
        const definition = definitions.powerPreset;
        const assignment = (label, iconName, selected, command, description) =>
          controlRuntime.react.createElement(controlRuntime.dropdown, {
            label,
            icon: controlRuntime.icon(iconName),
            layout: "below",
            description,
            rgOptions: options.filter(
              (option) => option.data !== "custom" || selected === "custom",
            ),
            selectedOption: selected,
            disabled: pending || !state.available,
            onChange: (option) => {
              if (
                pending ||
                !state.available ||
                !option ||
                option.data === "custom" ||
                !options.some((item) => item.data === option.data)
              )
                return;
              setPending(true);
              void request(
                definition.patchId,
                command,
                { target: option.data || null },
                nextActionGeneration(definition.patchId),
              )
                .catch(() => {})
                .finally(() => setPending(false));
            },
          });
        renderOutcomes.powerPreset = "rendered";
        // What is in effect, and why. The scope and the status belong to that one fact, so they are
        // its description rather than two more unlabelled lines: every other row in this host puts
        // its status there, and three stacked bare divs were the one place the panel stopped
        // looking like Steam.
        const detail = [state.scope, state.statusText].filter(Boolean).join(" · ");
        const active = !state.current
          ? null
          : controlRuntime.labelField
            ? controlRuntime.react.createElement(
                controlRuntime.labelField,
                {
                  label: "Active profile",
                  icon: controlRuntime.icon("check"),
                  description: detail || undefined,
                },
                state.current,
              )
            : note("powerPresetActive", "Steam LabelField was not resolved");
        // A refusal has to stay visible when there is no active profile to hang it on. Readback can
        // fail with `available: false`, a reason, and no current profile at all, and on a client
        // where LabelField could not be resolved there is no row here either; both left two
        // disabled dropdowns and no explanation for why.
        const orphaned = active || !detail ? undefined : detail;
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          active,
          assignment("When plugged in", "plug", state.ac, definition.acCommand, orphaned),
          assignment("On battery", "battery", state.battery, definition.batteryCommand),
        );
      };
    const createControllerControl = (controlRuntime) =>
      function SteamUiControllerTargetControl() {
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
          icon: controlRuntime.icon("swap"),
          rgOptions: options,
          selectedOption: selected,
          onChange: setTarget,
          disabled: isBusy(state.progress) || options.length < 2,
          description: (state.statusText || "") + restart || undefined,
          layout: "below",
        });
      };
    const createResolutionControl = (controlRuntime) =>
      function SteamUiResolutionControl() {
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
          icon: controlRuntime.icon("aspect"),
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
      function SteamUiFrameLimitControl() {
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
              icon: controlRuntime.icon("infinity"),
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
          icon: controlRuntime.icon("frameRate"),
          // SliderField would otherwise put the glyph beside the track, which is where Valve keeps
          // the Quick Settings brightness icon on a slider that has no label at all. These sliders
          // are labelled, and the icon belongs with the label so every row in a section lines its
          // glyph up in one column.
          iconLocation: "front",
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
    const rgbToHsv = (color) => {
      const red = ((color >> 16) & 0xff) / 255;
      const green = ((color >> 8) & 0xff) / 255;
      const blue = (color & 0xff) / 255;
      const maximum = Math.max(red, green, blue);
      const minimum = Math.min(red, green, blue);
      const delta = maximum - minimum;
      let hue = 0;
      if (delta > 0) {
        if (maximum === red) hue = 60 * (((green - blue) / delta) % 6);
        else if (maximum === green) hue = 60 * ((blue - red) / delta + 2);
        else hue = 60 * ((red - green) / delta + 4);
      }
      if (hue < 0) hue += 360;
      return {
        hue: Math.round(hue),
        saturation: maximum === 0 ? 0 : Math.round((delta / maximum) * 100),
        brightness: Math.round(maximum * 100),
      };
    };
    const hsvToRgb = (hue, saturation, brightness) => {
      const h = ((Number(hue) % 360) + 360) % 360;
      const s = Math.min(100, Math.max(0, Number(saturation))) / 100;
      const v = Math.min(100, Math.max(0, Number(brightness))) / 100;
      const chroma = v * s;
      const x = chroma * (1 - Math.abs(((h / 60) % 2) - 1));
      const m = v - chroma;
      let red = 0;
      let green = 0;
      let blue = 0;
      if (h < 60) [red, green] = [chroma, x];
      else if (h < 120) [red, green] = [x, chroma];
      else if (h < 180) [green, blue] = [chroma, x];
      else if (h < 240) [green, blue] = [x, chroma];
      else if (h < 300) [red, blue] = [x, chroma];
      else [red, blue] = [chroma, x];
      return (
        (Math.round((red + m) * 255) << 16) |
        (Math.round((green + m) * 255) << 8) |
        Math.round((blue + m) * 255)
      );
    };
    const rgbCss = (color) => `#${Number(color).toString(16).padStart(6, "0")}`;
    const normalizePowerLimitRange = (value) => {
      if (!value || typeof value !== "object") return null;
      const {
        minimumWatts: min,
        maximumWatts: max,
        stepWatts: step,
        observedWatts: observed,
      } = value;
      if (
        ![min, max, step].every(Number.isInteger) ||
        min < 1 ||
        max > 200 ||
        min >= max ||
        step < 1 ||
        step > max - min ||
        !Number.isInteger(observed) ||
        observed < min ||
        observed > max ||
        (observed - min) % step !== 0
      )
        return null;
      return {
        available: value.available === true,
        min,
        max,
        step,
        observed,
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
      };
    };
    const normalizePowerLimitState = (value) =>
      value && typeof value === "object"
        ? {
            sustained: normalizePowerLimitRange(value.sustained),
            boost: normalizePowerLimitRange(value.boost),
            unified: value.unified === true,
            canSelectMode: value.canSelectMode === true,
          }
        : null;
    const createPowerLimitControl = (controlRuntime) =>
      function SteamUiPowerLimits() {
        const state = useSemanticState(controlRuntime, "powerLimit", normalizePowerLimitState);
        const definition = definitions.powerLimit;
        const sustainedEcho = useEchoedValue(controlRuntime, state?.sustained?.observed ?? null);
        const boostEcho = useEchoedValue(controlRuntime, state?.boost?.observed ?? null);
        const pending = controlRuntime.react.useRef(false);
        const [sending, setSending] = controlRuntime.react.useState(false);
        const [error, setError] = controlRuntime.react.useState("");
        if (!state) return note("powerLimit", "no state");
        const busy = sending || isBusy(state.sustained?.progress) || isBusy(state.boost?.progress);
        const rows = [];
        if (state.canSelectMode && controlRuntime.toggle) {
          rows.push(
            controlRuntime.react.createElement(controlRuntime.toggle, {
              key: "mode",
              label: "Unified TDP",
              checked: state.unified,
              controlled: true,
              disabled: busy,
              description: error || "Coordinate sustained and boost limits with one target.",
              onChange: (unified) => {
                if (
                  pending.current ||
                  busy ||
                  typeof unified !== "boolean" ||
                  unified === state.unified
                )
                  return;
                pending.current = true;
                setSending(true);
                setError("");
                void request(
                  definition.patchId,
                  definition.modeCommand,
                  { unified },
                  nextActionGeneration(definition.patchId),
                )
                  .catch((reason) => setError(normalizeText(String(reason))))
                  .finally(() => {
                    pending.current = false;
                    setSending(false);
                  });
              },
            }),
          );
        }
        for (const [key, label, iconName, range, echo, command] of [
          [
            "pl1",
            state.unified ? "TDP" : "Sustained power (PL1)",
            "bolt",
            state.sustained,
            sustainedEcho,
            definition.primaryCommand,
          ],
          ["pl2", "Boost power (PL2)", "boost", state.boost, boostEcho, definition.boostCommand],
        ]) {
          if (!range || (state.unified && key === "pl2")) continue;
          const commit = (watts) => {
            if (
              pending.current ||
              busy ||
              !range.available ||
              !Number.isInteger(watts) ||
              watts < range.min ||
              watts > range.max ||
              (watts - range.min) % range.step !== 0 ||
              watts === range.observed
            )
              return;
            pending.current = true;
            setSending(true);
            setError("");
            void request(
              definition.patchId,
              command,
              { watts },
              nextActionGeneration(definition.patchId),
            )
              .catch((reason) => setError(normalizeText(String(reason))))
              .finally(() => {
                pending.current = false;
                setSending(false);
              });
          };
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key },
              controlRuntime.react.createElement(controlRuntime.slider, {
                label,
                icon: controlRuntime.icon(iconName),
                iconLocation: "front",
                min: range.min,
                max: range.max,
                step: range.step,
                value: echo.value,
                valueSuffix: " W",
                showValue: true,
                showBookendLabels: true,
                disabled: busy || !range.available,
                description:
                  error ||
                  (state.unified
                    ? `Sustained ${state.sustained?.observed ?? "?"} W · Boost ${state.boost?.observed ?? "?"} W`
                    : range.statusText) ||
                  undefined,
                onChange: echo.onChange,
                onChangeComplete: (next) => echo.onChangeComplete(next, commit),
              }),
            ),
          );
        }
        note("powerLimit", `rendered ${rows.length} row(s)`);
        return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, ...rows);
      };
    const createDeviceControlsControl = (controlRuntime) =>
      function SteamUiDeviceControls() {
        const state = useSemanticState(
          controlRuntime,
          "deviceControls",
          normalizeDeviceControlsState,
        );
        const definition = definitions.deviceControls;
        const send = (command, payload) =>
          void request(
            definition.patchId,
            command,
            payload,
            nextActionGeneration(definition.patchId),
          ).catch(() => {});
        const queueColorCommit = useTrailingCommit(controlRuntime, 350, ({ zone, color }) =>
          send(definition.colorCommand, { zone, color }),
        );
        const [selectedZone, setSelectedZone] = controlRuntime.react.useState("");
        const [editingColor, setEditingColor] = controlRuntime.react.useState(false);
        const chargeValue = state?.chargeLimit
          ? (state.chargeLimit.observed ?? state.chargeLimit.desired)
          : null;
        const brightnessValue = state?.lightingBrightness
          ? (state.lightingBrightness.observed ?? state.lightingBrightness.desired)
          : null;
        const zones = state?.lightingZones?.filter((zone) => zone.available) ?? [];
        const zone = zones.find((candidate) => candidate.id === selectedZone) ?? zones[0] ?? null;
        const color = zone ? (zone.observedColor ?? zone.desiredColor) : null;
        const hsv = color === null ? null : rgbToHsv(color);
        const chargeEcho = useEchoedValue(controlRuntime, chargeValue);
        const brightnessEcho = useEchoedValue(controlRuntime, brightnessValue);
        const hueEcho = useEchoedValue(controlRuntime, hsv?.hue ?? null);
        const saturationEcho = useEchoedValue(controlRuntime, hsv?.saturation ?? null);
        const colorBrightnessEcho = useEchoedValue(controlRuntime, hsv?.brightness ?? null);
        if (!state) return note("deviceControls", "no state");
        const rows = [];
        const appendSlider = (key, properties) => {
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key },
              controlRuntime.react.createElement(controlRuntime.slider, properties),
            ),
          );
        };
        if (state.chargeLimit?.available && chargeEcho.value !== null) {
          const range = state.chargeLimit;
          appendSlider("steam-ui-charge-limit", {
            label: "Battery charge limit",
            icon: controlRuntime.icon("percent"),
            iconLocation: "front",
            min: range.minimum,
            max: range.maximum,
            step: range.step,
            value: chargeEcho.value,
            valueSuffix: "%",
            showValue: true,
            showBookendLabels: true,
            disabled: isBusy(range.progress),
            description: range.statusText || undefined,
            onChange: chargeEcho.onChange,
            onChangeComplete: (next) =>
              chargeEcho.onChangeComplete(next, (percent) =>
                send(definition.chargeCommand, { percent }),
              ),
          });
        }
        const chargingRows = rows.splice(0);
        if (state.lightingBrightness?.available && brightnessEcho.value !== null) {
          const range = state.lightingBrightness;
          appendSlider("steam-ui-lighting-brightness", {
            label: "Lighting brightness",
            icon: controlRuntime.icon("bulb"),
            iconLocation: "front",
            min: range.minimum,
            max: range.maximum,
            step: range.step,
            value: brightnessEcho.value,
            valueSuffix: "%",
            showValue: true,
            showBookendLabels: true,
            disabled: isBusy(range.progress),
            description: range.statusText || undefined,
            onChange: brightnessEcho.onChange,
            onChangeComplete: (next) =>
              brightnessEcho.onChangeComplete(next, (percent) =>
                send(definition.brightnessCommand, { percent }),
              ),
          });
        }
        if (zone && hsv && controlRuntime.toggle) {
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key: "steam-ui-lighting-edit" },
              controlRuntime.react.createElement(controlRuntime.toggle, {
                label: "Edit color",
                icon: controlRuntime.icon("pencil"),
                checked: editingColor,
                controlled: true,
                onChange: setEditingColor,
              }),
            ),
          );
        }
        if (zone && hsv && controlRuntime.toggle && editingColor) {
          const options = zones.map((candidate) => ({
            data: candidate.id,
            label: candidate.label,
          }));
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key: "steam-ui-lighting-zone" },
              controlRuntime.react.createElement(controlRuntime.dropdown, {
                label: "Lighting zone",
                icon: controlRuntime.icon("zones"),
                rgOptions: options,
                selectedOption: zone.id,
                onChange: (option) => {
                  if (option && zones.some((candidate) => candidate.id === option.data)) {
                    setSelectedZone(option.data);
                  }
                },
                disabled: options.length < 2,
                description: zone.statusText || undefined,
                layout: "below",
              }),
            ),
          );
          const stagedColor = hsvToRgb(
            hueEcho.value ?? hsv.hue,
            saturationEcho.value ?? hsv.saturation,
            colorBrightnessEcho.value ?? hsv.brightness,
          );
          rows.push(
            controlRuntime.react.createElement(
              controlRuntime.row,
              { key: "steam-ui-lighting-preview" },
              controlRuntime.react.createElement("div", {
                title: rgbCss(stagedColor),
                style: {
                  background: rgbCss(stagedColor),
                  border: "1px solid rgba(255,255,255,.7)",
                  borderRadius: "4px",
                  height: "32px",
                  width: "100%",
                },
              }),
            ),
          );
          const commitColor = (hue, saturation, brightness) =>
            queueColorCommit({
              zone: zone.id,
              color: hsvToRgb(hue, saturation, brightness),
            });
          appendSlider("steam-ui-lighting-hue", {
            label: localizeOr(controlRuntime, "#ColorPicker_Hue", "Hue"),
            icon: controlRuntime.icon("rainbow"),
            iconLocation: "front",
            min: 0,
            max: 360,
            step: 1,
            value: hueEcho.value,
            valueSuffix: "°",
            showValue: true,
            disabled: isBusy(zone.progress),
            trackStyleOverride: {
              background: "linear-gradient(to right,#f00,#ff0,#0f0,#0ff,#00f,#f0f,#f00)",
              "--left-track-color": "transparent",
            },
            onChange: hueEcho.onChange,
            onChangeComplete: (next) =>
              hueEcho.onChangeComplete(next, (hue) =>
                commitColor(
                  hue,
                  saturationEcho.value ?? hsv.saturation,
                  colorBrightnessEcho.value ?? hsv.brightness,
                ),
              ),
          });
          appendSlider("steam-ui-lighting-saturation", {
            label: localizeOr(controlRuntime, "#ColorPicker_Saturation", "Saturation"),
            icon: controlRuntime.icon("droplet"),
            iconLocation: "front",
            min: 0,
            max: 100,
            step: 1,
            value: saturationEcho.value,
            valueSuffix: "%",
            showValue: true,
            disabled: isBusy(zone.progress),
            onChange: saturationEcho.onChange,
            onChangeComplete: (next) =>
              saturationEcho.onChangeComplete(next, (saturation) =>
                commitColor(
                  hueEcho.value ?? hsv.hue,
                  saturation,
                  colorBrightnessEcho.value ?? hsv.brightness,
                ),
              ),
          });
          appendSlider("steam-ui-lighting-color-brightness", {
            label: localizeOr(controlRuntime, "#ColorPicker_Brightness", "Brightness"),
            icon: controlRuntime.icon("contrast"),
            iconLocation: "front",
            min: 0,
            max: 100,
            step: 1,
            value: colorBrightnessEcho.value,
            valueSuffix: "%",
            showValue: true,
            disabled: isBusy(zone.progress),
            onChange: colorBrightnessEcho.onChange,
            onChangeComplete: (next) =>
              colorBrightnessEcho.onChangeComplete(next, (brightness) =>
                commitColor(
                  hueEcho.value ?? hsv.hue,
                  saturationEcho.value ?? hsv.saturation,
                  brightness,
                ),
              ),
          });
        }
        if (!rows.length && !chargingRows.length)
          return note("deviceControls", "no compatible charge or lighting rows");
        renderOutcomes.deviceControls = `rendered ${rows.length + chargingRows.length} row(s)`;
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          chargingRows.length
            ? controlRuntime.react.createElement(
                controlRuntime.section,
                { title: sectionTitle(controlRuntime, "Charging"), key: "charging" },
                ...chargingRows,
              )
            : null,
          rows.length
            ? controlRuntime.react.createElement(
                controlRuntime.section,
                { title: sectionTitle(controlRuntime, "RGB lighting"), key: "lighting" },
                ...rows,
              )
            : null,
        );
      };
    // Steam's own FPS counter rows, which the host replaces with its RTSS-driven overlay. Identified by
    // localising the same tokens Steam did rather than by CSS class or visible text: the classes
    // are hashed per client build and the text changes with the user's language, while the token is
    // the one thing that is neither.
    const NativeFpsTokens = [
      "#QuickAccess_Tab_Perf_FPS_Corner",
      "#QuickAccess_Tab_Perf_FPS_Contrast",
    ];
    let filteredNative = null;
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
      const type = element.type;
      if (typeof type === "function" && !type.prototype?.isReactComponent) {
        // A plain function component: render it through a wrapper so its output is filtered too.
        // Class components, memo and forwardRef objects are left alone — they cannot be called
        // directly, and wrapping them would change identity for refs.
        let wrapper = descendCache.get(type);
        if (!wrapper) {
          wrapper = function SteamUiDescend(props) {
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
      const next = [];
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
      const inner = tree && tree.type;
      if (typeof inner !== "function") return tree;
      const labels = NativeFpsTokens.map((token) => textOf(controlRuntime.localize(token))).filter(
        (text) => typeof text === "string" && text.length > 0 && text[0] !== "#",
      );
      if (!labels.length) return tree;
      if (!filteredNative || filteredNative.inner !== inner) {
        filteredNative = {
          inner,
          component: function SteamUiFilteredPerformance(props) {
            lastHidden = 0;
            const filtered = hideNativeRows(controlRuntime, inner(props), labels, 0);
            if (appendDiagnostics.perf) {
              appendDiagnostics.perf.nativeRowsHidden = lastHidden;
            }
            return filtered;
          },
        };
      }
      return controlRuntime.react.createElement(filteredNative.component, tree.props);
    };
    // Valve's own rows take no props, so the only way to put a glyph on one is to render it here and
    // clone what it returned. Calling the component as a plain function makes its hooks this
    // wrapper's hooks, which is safe because the call is unconditional, and is what the native-row
    // filter above already does.
    //
    // Only the overlay-level row is wrapped. It returns Valve's own slider wrapper, which spreads
    // every prop it does not recognize into SliderField and on into Field, so `icon` arrives where
    // a row's icon belongs. The other Valve rows cannot take one this way: the per-game toggle
    // returns a Fragment, which drops any prop but `key`; the reset row is a button rather than a
    // field; and the profile header already draws the game's own capsule art as its icon.
    // The icon is built by the caller rather than named here, so the glyph gate sees this placement
    // as the same `icon("name")` shape as every other one instead of needing a rule of its own.
    const withIcon = (controlRuntime, component, icon) =>
      function SteamUiValveRowWithIcon() {
        const rendered = component({});
        // Only a component element can carry the prop onward. Valve's row is a function that spreads
        // what it does not recognize into SliderField, and that is the whole reason this works; a
        // future build returning a Fragment or a host element would take the icon nowhere and warn
        // on every render instead, so those are handed back untouched.
        return icon &&
          controlRuntime.react.isValidElement(rendered) &&
          typeof rendered.type === "function"
          ? controlRuntime.react.cloneElement(rendered, { icon, iconLocation: "front" })
          : rendered;
      };
    // The glyph beside each section header, keyed by the header text so every placement — the
    // Performance groups, the Quick Settings Display group and the device sections — reads from one
    // table instead of carrying its icon at its own call site. PanelSection renders whatever `title`
    // is inside its own text element, so an element is as valid there as a string; the row of icon
    // and text is laid out here rather than left to Valve's header CSS, which only sizes an svg that
    // is its DIRECT child and would leave a nested one at its intrinsic size.
    // No header shares a glyph with a row beneath it, and no two rows share one either: the panel
    // is scanned by shape before it is read, so a repeated glyph says two controls are the same
    // control.
    const SectionIcons = Object.freeze({
      "Profile scope": "profile",
      "Power profiles": "sliders",
      "Display and frame rate": "timer",
      "Power limits": "gauge",
      Controller: "controller",
      Reset: "reset",
      Display: "display",
      Charging: "batteryCharging",
      "RGB lighting": "colors",
    });
    // 18px is the size Valve's own header rule gives a section icon, against a 16px header. A
    // section with no glyph of its own keeps the plain string, so the header is never wrapped in
    // markup that buys it nothing.
    // Hoisted, because a header is rebuilt on every render of the panel root and a fresh style
    // object each time would hand React new props for a div that never changes.
    const SectionTitleStyle = Object.freeze({
      display: "flex",
      alignItems: "center",
      gap: "8px",
    });
    const sectionTitle = (controlRuntime, title) => {
      const icon = controlRuntime.icon(SectionIcons[title], 18);
      return icon
        ? controlRuntime.react.createElement("div", { style: SectionTitleStyle }, icon, title)
        : title;
    };
    const appendControls = (controlRuntime, tree, placement = "perf") => {
      // Rendered React elements from Steam's own untyped runtime.
      const controls = [];
      const groups = new Map();
      const groupFor = (kind) =>
        ({
          valveProfileHeader: "Profile scope",
          powerPreset: "Power profiles",
          powerProfile: "Power profiles",
          hybridCores: "Power profiles",
          valveOverlayLevel: "Display and frame rate",
          frameLimit: "Display and frame rate",
          vrr: "Display and frame rate",
          powerLimit: "Power limits",
          autoTdp: "Power limits",
          controllerTarget: "Controller",
          valveReset: "Reset",
        })[kind] || "Display";
      // Registration, component and placement share one table. The group order below determines
      // section placement; this table determines the order of controls within each group.
      const rows = [
        ["valveProfileHeader", "steam-ui-valve-profile-header", valveProfileHeaderControl, "perf"],
        ["valveProfileHeader", "steam-ui-valve-profile-toggle", valveProfileToggleControl, "perf"],
        ["valveOverlayLevel", "steam-ui-valve-overlay-level", valveOverlayLevelControl, "perf"],
        ["frameLimit", "steam-ui-frame-limit", frameLimitControl, "perf"],
        ["powerProfile", "steam-ui-power-profile", powerProfileControl, "perf"],
        ["hybridCores", "steam-ui-hybrid-cores", hybridCoreControl, "perf"],
        ["powerPreset", "steam-ui-power-preset", powerPresetControl, "perf"],
        ["vrr", "steam-ui-vrr", vrrControl, "perf"],
        ["powerLimit", "steam-ui-power-limits", powerLimitControl, "perf"],
        ["autoTdp", "steam-ui-auto-tdp", autoTdpControl, "perf"],
        ["resolution", "steam-ui-resolution", resolutionControl, "quickSettings"],
        [
          "valveRefreshRate",
          "steam-ui-valve-refresh-rate",
          valveRefreshRateControl,
          "quickSettings",
        ],
        ["controllerTarget", "steam-ui-controller-target", controllerControl, "perf"],
        ["valveReset", "steam-ui-valve-reset", valveResetControl, "perf"],
      ];
      for (const [kind, key, component, rowPlacement] of rows) {
        if (rowPlacement !== placement || !registrations.has(kind) || !component) continue;
        const element = controlRuntime.react.createElement(
          controlRuntime.row,
          { key },
          controlRuntime.react.createElement(component),
        );
        controls.push(element);
        const group = groupFor(kind);
        if (!groups.has(group)) groups.set(group, []);
        groups.get(group).push(element);
      }
      if (
        placement === "quickSettings" &&
        registrations.has("deviceControls") &&
        deviceControlsControl
      ) {
        // Device controls render their own Charging and RGB sections after Valve's common settings.
        controls.push(
          controlRuntime.react.createElement(deviceControlsControl, {
            key: "steam-ui-device-controls",
          }),
        );
      }
      if (!controls.length) {
        appendDiagnostics[placement] = { controls: 0, inserted: false, ownSection: false };
        return tree;
      }
      // Quick Settings keeps Valve's common controls intact. The native-row filtering
      // below is about Steam's FPS counter rows on the PERFORMANCE panel; running it against a
      // different tab's tree would be hiding rows this code has never even looked at.
      if (placement === "quickSettings") {
        const section = groups.has("Display")
          ? controlRuntime.react.createElement(
              controlRuntime.section,
              {
                key: "steam-ui-quick-settings-section",
                title: sectionTitle(controlRuntime, "Display"),
              },
              ...(groups.get("Display") || []),
            )
          : null;
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
          registrations.has("deviceControls") && deviceControlsControl
            ? controlRuntime.react.createElement(deviceControlsControl, {
                key: "steam-ui-device-controls",
              })
            : null,
        );
      }
      // The host's rows go into titled PanelSections, appended after whatever the native
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
        controlRuntime.react.Fragment,
        null,
        ...[
          "Profile scope",
          "Power profiles",
          "Display and frame rate",
          "Power limits",
          "Controller",
          "Reset",
        ]
          .filter((title) => groups.has(title))
          .map((title) =>
            controlRuntime.react.createElement(
              controlRuntime.section,
              { key: title, title: sectionTitle(controlRuntime, title) },
              ...groups.get(title),
            ),
          ),
      );
      // Shape of what Steam's performance root returned, so the rows it renders can be identified
      // without guessing. Needed to suppress Steam's own FPS counter rows in favour of the host's
      // RTSS overlay: their DOM classes are hashed per client build and unusable as selectors.
      const describe = (element, depth) => {
        if (!controlRuntime.react.isValidElement(element)) return typeof element;
        const t = element.type;
        const name = typeof t === "string" ? t : t?.displayName || t?.name || "anonymous";
        const kids = controlRuntime.react.Children.toArray(element.props?.children);
        return depth >= 2 || !kids.length
          ? name
          : { [name]: kids.map((k) => describe(k, depth + 1)) };
      };
      // Steam's FPS rows are suppressed only on this path, which runs when the host has rows of its own
      // to put in their place. Hiding them and then rendering nothing would leave the user neither.
      const native = withNativeRowsHidden(controlRuntime, tree);
      appendDiagnostics.perf = {
        controls: controls.length,
        inserted: true,
        ownSection: true,
        tree: JSON.stringify(describe(tree, 0)).slice(0, 600),
        nativeFiltered: native !== tree,
      };
      return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, native, own);
    };
    // Resolve every dependency before changing React or registering a component.
    const resolveControls = () => {
      runtime = getWebpackRuntime("native-components");
      const performanceFactory = uniqueFactory([
        "#QuickAccess_Tab_Perf_Common_Settings",
        "#QuickAccess_Tab_Perf_BatteryTimeRemaining",
        "TS.ON_FRAME",
      ]);
      controlRuntime = createControlRuntime();
      if (!performanceFactory) {
        lastPatchError = "performance panel factory was not a unique match";
        return false;
      }
      if (!controlRuntime) {
        lastPatchError = "React, fields, layout or localization runtime was not a unique match";
        return false;
      }
      performanceRoot = uniqueFunction(runtime(performanceFactory[0]), ["TS.ON_FRAME", "return"]);
      if (!performanceRoot) {
        lastPatchError = "performance panel root was not a unique match";
        return false;
      }
      autoTdpControl = createAutoTdpControl(controlRuntime);
      frameLimitControl = createFrameLimitControl(controlRuntime);
      controllerControl = createControllerControl(controlRuntime);
      powerProfileControl = createPowerProfileControl(controlRuntime);
      hybridCoreControl = createHybridCoreControl(controlRuntime);
      powerPresetControl = createPowerPresetControl(controlRuntime);
      resolutionControl = createResolutionControl(controlRuntime);
      vrrControl = createVrrControl(controlRuntime);
      deviceControlsControl = createDeviceControlsControl(controlRuntime);
      powerLimitControl = createPowerLimitControl(controlRuntime);
      // Selected by the localization token it draws, never by a minified export name: the names are
      // right for today's build and are not guaranteed for the next. Live-probed 2026-08-30 that
      // this token matches exactly one export of the components module.
      const perfComponents = uniqueFactory([
        "#QuickAccess_Tab_Perf_EnableVRR",
        "#QuickAccess_Tab_Perf_LimitFrameRate",
      ]);
      const perfExports = perfComponents ? runtime(perfComponents[0]) : null;
      valveProfileHeaderControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_GameSpecificSettings"])
        : null;
      // The toggle reads current_game_id for availability, current==active for its checked state,
      // and writes through SetGameSpecificProfileEnabled — all state the host already backs. Without
      // this row nothing in the tab can enable a per-game profile.
      valveProfileToggleControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ToggleGameSettings"])
        : null;
      valveResetControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_ResetToDefault"])
        : null;
      valveRefreshRateControl = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_RefreshRate"])
        : null;
      const valveOverlayLevel = perfExports
        ? uniqueFunction(perfExports, ["#QuickAccess_Tab_Perf_Overlay_Level"])
        : null;
      valveOverlayLevelControl = valveOverlayLevel
        ? withIcon(controlRuntime, valveOverlayLevel, controlRuntime.icon("layers"))
        : null;
      return true;
    };
    const ensurePatched = () => {
      if (
        controlRuntime &&
        performanceRoot &&
        patchedUseMemo &&
        controlRuntime.react.useMemo === patchedUseMemo
      )
        return true;
      try {
        if (!resolveControls()) return false;
      } catch {
        lastPatchError = "native component runtime resolution failed";
        return false;
      }
      function SteamUiPerformanceRoot(props) {
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
      // It is matched by its own source instead, on two Valve strings the host's gates never touch: the
      // Other-section title and the reorder-controllers button. Deliberately NOT the brightness
      // title, because that is the surface the host's own gate reveals, and a selector must not be
      // entangled with a thing this code changes.
      const wrappers = [
        {
          match: (type) => type === performanceRoot,
          component: () => SteamUiPerformanceRoot,
          fallbackKey: "steam-ui-performance-root",
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
              wrapped = function SteamUiQuickSettingsRoot(props) {
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
          fallbackKey: "steam-ui-quick-settings-root",
        },
      ];
      patchedUseMemo = function SteamUiUseMemo(factory, dependencies) {
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
      if (controlRuntime.react.useMemo !== patchedUseMemo) {
        lastPatchError = "React useMemo wrapper could not be installed";
        return false;
      }
      lastPatchError = "";
      return true;
    };
    const install = (kind) => {
      if (disposedHost || !Object.hasOwn(definitions, kind))
        return { ok: false, error: "component is not allowlisted" };
      if (!ensurePatched())
        return {
          ok: false,
          error: lastPatchError || "native performance root is incompatible",
        };
      registrations.set(kind, definitions[kind].patchId);
      notify();
      return { ok: true, kind, registered: true, hostVersion: 1 };
    };
    const remove = (kind) => {
      if (!Object.hasOwn(definitions, kind)) return { ok: true, absent: true };
      registrations.delete(kind);
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
      lastError: lastPatchError,
    });
    const disposeHostResources = () => {
      disposedHost = true;
      registrations.clear();
      notify();
      listeners.clear();
      if (controlRuntime && originalUseMemo && controlRuntime.react.useMemo === patchedUseMemo)
        controlRuntime.react.useMemo = originalUseMemo;
    };
    return { install, remove, status, dispose: disposeHostResources };
  }
  registerGate("nativeComponents", createNativeComponentHost());
  // The last fragment in the bundle, and the only thing in it.
  //
  // bridge.ts opens the IIFE and every other fragment is concatenated into it, so the value the
  // injected script evaluates to has to be returned AFTER the last of them — a gate registers with a
  // top-level call, and a return placed before those calls makes every one of them unreachable. That
  // is not a hypothetical: it shipped, and it published a bridge whose registry was empty while the
  // bootstrap patch still verified, so every gate reported "bridge unavailable" with nothing in the
  // log naming why.
  //
  // Keeping the return here rather than in the builder's epilogue string keeps the result shape the
  // bridge's own business; the builder only has to emit this file last and close the IIFE.
  return installResult;
})();
