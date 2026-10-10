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
    // The allow map is fixed at install, so a bridge reused across a vocabulary change would refuse
    // every command a module added since.
    prior.vocabularyRevision === config.vocabularyRevision &&
    prior.contextGeneration === config.contextGeneration &&
    prior.documentGeneration === config.documentGeneration &&
    // A prior bridge that can still hand out gates is one this build can stand aside for. Asking
    // for a specific gate by name would tie the reuse check to whichever surfaces the consumer
    // happens to have.
    typeof prior.gate === "function"
  ) {
    return JSON.stringify({ ok: true, reused: true, version: prior.version });
  }
  // A prior bridge unwinds every gate it registered while their closures still hold what they
  // displaced; see dispose below. It names the gates that could not unwind, and the install result
  // carries them so the host can log them: nothing else would ever show that a gate stayed behind.
  const priorDispose =
    typeof prior?.dispose === "function" ? prior.dispose("generation replaced") : null;
  const priorDisposeFailures =
    Array.isArray(priorDispose) && priorDispose.length ? priorDispose.map(String) : undefined;
  const pending = new Map();
  const subscribers = new Map();
  const latestStates = new Map();
  // Why the host could not deliver a patch's state, until a state arrives again. A surface shows
  // it: the state it holds is the last one it was given, and without this it would pass for
  // current.
  const refusalSubscribers = new Map();
  const latestRefusals = new Map();
  // Deliveries being reassembled from parts, by delivery id. The host sends a large response and a
  // large state publication independently, so their parts can interleave; one shared slot made each
  // cancel the other. A set cut short is dropped rather than delivered half, and one the host
  // abandoned dies with the document.
  const assembling = new Map();
  let nextSequence = 0;
  let disposed = false;
  // A successful capture is shared; failed cold-start captures remain retryable.
  let webpackResolver;
  /**
   * Returns this bridge's shared webpack resolver without evaluating unknown modules.
   * @param scope Diagnostic label used only on the first successful capture.
   * @returns The cached resolver; an unavailable runtime throws and can be retried later.
   */
  const getWebpackRuntime = (scope) => (webpackResolver ??= createSteamUiModuleResolver(scope));
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
  // Every request requires a positive generation, including automatic service notifications.
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
  /**
   * Sends one allowlisted semantic request in the current bridge generation.
   * @param patchId Registered command namespace.
   * @param command Allowlisted command name.
   * @param payload JSON-serializable payload; undefined becomes null.
   * @param requestedGeneration Optional positive action generation; otherwise a new one is allocated.
   * @returns A promise for the backend payload; rejects on refusal, overload, timeout, disposal or send failure.
   */
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
  /**
   * Subscribes to a patch state with synchronous cached replay and isolated callback errors.
   * @param patchId Registered publication identity.
   * @param callback Receives each complete delivered state, including cached state if available.
   * @returns An unsubscribe callback; after disposal a valid subscription returns a no-op.
   */
  const subscribe = (patchId, callback) => {
    if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
      throw new Error("subscription not allowlisted");
    if (disposed) return () => false;
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
  /**
   * Subscribes to publication failures and their recovery with synchronous cached replay.
   * @param patchId Registered publication identity.
   * @param callback Receives a refusal reason, or null when a later state succeeds.
   * @returns An unsubscribe callback; invalid identities or callbacks throw.
   */
  const subscribeRefusal = (patchId, callback) => {
    if (!Object.hasOwn(config.allowed, patchId) || typeof callback !== "function")
      throw new Error("subscription not allowlisted");
    if (disposed) return () => false;
    let set = refusalSubscribers.get(patchId);
    if (!set) refusalSubscribers.set(patchId, (set = new Set()));
    set.add(callback);
    if (latestRefusals.has(patchId)) {
      try {
        callback(latestRefusals.get(patchId));
      } catch {}
    }
    return () => set.delete(callback);
  };
  const reportRefusal = (patchId, reason) => {
    if (reason === null ? !latestRefusals.has(patchId) : latestRefusals.get(patchId) === reason)
      return;
    if (reason === null) latestRefusals.delete(patchId);
    else latestRefusals.set(patchId, reason);
    for (const callback of [...(refusalSubscribers.get(patchId) ?? [])]) {
      try {
        callback(reason);
      } catch {}
    }
  };
  const deliver = (envelope) => {
    if (
      disposed ||
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
      reportRefusal(envelope.patchId, null);
      const set = subscribers.get(envelope.patchId);
      if (!set) return true;
      for (const callback of [...set]) {
        try {
          callback(envelope.payload);
        } catch {}
      }
      return true;
    }
    if (envelope.type === "refused") {
      if (!Object.hasOwn(config.allowed, envelope.patchId) || typeof envelope.reason !== "string")
        return false;
      reportRefusal(envelope.patchId, envelope.reason);
      return true;
    }
    return false;
  };
  // One part of an envelope too large for a single evaluation. A delivery's parts arrive in order,
  // each acknowledged before the next is sent, though another delivery's parts may come between
  // them; the last one delivers the reassembled envelope.
  const deliverPart = (part) => {
    if (
      disposed ||
      !part ||
      part.contextGeneration !== config.contextGeneration ||
      part.documentGeneration !== config.documentGeneration ||
      !Number.isSafeInteger(part.id) ||
      !Number.isSafeInteger(part.count) ||
      part.count < 2 ||
      !Number.isSafeInteger(part.index) ||
      part.index < 0 ||
      part.index >= part.count ||
      typeof part.text !== "string"
    )
      return false;
    if (part.index === 0) assembling.set(part.id, { count: part.count, parts: [] });
    const entry = assembling.get(part.id);
    if (!entry || entry.count !== part.count || entry.parts.length !== part.index) {
      // Only this delivery is dropped; another one being reassembled is untouched.
      assembling.delete(part.id);
      return false;
    }
    entry.parts.push(part.text);
    if (entry.parts.length < entry.count) return true;
    const text = entry.parts.join("");
    assembling.delete(part.id);
    try {
      return deliver(JSON.parse(text));
    } catch {
      return false;
    }
  };
  // Returns the names of the gates that could not unwind, so the bridge replacing this one can
  // report them.
  const dispose = (reason) => {
    if (disposed) return [];
    disposed = true;
    const failures = [];
    // Resident gates own callbacks, service overlays and timers outside the bridge namespace.
    // Removing only the component host left the Manager gate polling every second after the bridge
    // that answered it had gone away, and left the other service wrappers calling dead closures.
    //
    // Every registered gate, not a list: a gate this file does not know about is exactly the case
    // a list gets wrong, and it is the normal case once a consumer adds one.
    for (const [name, gate] of gates) {
      const owned = gate;
      // Both, where present. `remove` unwinds what the gate installed in the client; `dispose`
      // releases what it holds inside this bridge, and the component host has only the latter.
      // A throw or an `{ok: false}` from either names the gate.
      let failed = false;
      for (const step of [owned.remove, owned.dispose]) {
        if (typeof step !== "function") continue;
        try {
          const result = step.call(owned);
          if (result && typeof result === "object" && result.ok === false) failed = true;
        } catch {
          failed = true;
        }
      }
      if (failed) failures.push(name);
    }
    gates.clear();
    for (const item of pending.values()) {
      clearTimeout(item.timer);
      item.reject(new Error(reason || "Steam UI bridge disposed"));
    }
    pending.clear();
    subscribers.clear();
    latestStates.clear();
    refusalSubscribers.clear();
    latestRefusals.clear();
    assembling.clear();
    actionGenerations.clear();
    webpackResolver = undefined;
    return failures;
  };
  // Stamped on every namespace the host defines on SteamClient, so a later probe can tell OUR namespace
  // from a real backend. Without it the two are indistinguishable and the compatibility check reads
  // its own successful install as "a native backend exists", refuses, and tears the patch down —
  // which is exactly what left this client with an empty audio page and a crashing Performance tab.
  //
  // A string key rather than a Symbol: it has to survive being read back from a probe evaluated in
  // a separate CDP call, where a Symbol from this scope is not reachable.
  const ownedMarker = "__steamUiOwnedNamespace";
  // Gates register themselves rather than being named here. The bridge used to construct each one
  // by name and publish it under a fixed property, which meant this file had to list every surface
  // its consumer happened to have — the one thing a reusable bridge cannot do.
  //
  // Registration is a top-level statement in each fragment, so it runs after this file and before
  // anything asks for a gate. It also inherits the reuse check for free: when this file returns
  // early because an identical bridge is already installed, the whole IIFE returns and no fragment
  // registers over it.
  const gates = new Map();
  /**
   * Registers one gate implementation in this asset's shared bridge.
   * @param name Gate identity used by host patch expressions.
   * @param gate Gate lifecycle object retained until bridge disposal.
   */
  const registerGate = (name, gate) => {
    gates.set(name, gate);
  };
  const bridge = Object.freeze({
    version: config.version,
    assetHash: config.assetHash,
    vocabularyRevision: config.vocabularyRevision,
    contextGeneration: config.contextGeneration,
    documentGeneration: config.documentGeneration,
    request,
    subscribe,
    subscribeRefusal,
    deliver,
    deliverPart,
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
  installResult = JSON.stringify({
    ok: true,
    reused: false,
    version: config.version,
    priorDisposeFailures,
  });
  // @fragment ownership.ts
  // Durable ownership claims shared across CDP evaluations. Restore exact descriptors and inherited
  // membership; never stack wrappers, replace a real backend or spoof global platform identity.
  const defineHidden = (host, key, value) => {
    Object.defineProperty(host, key, {
      value,
      configurable: true,
      enumerable: false,
      writable: false,
    });
  };
  const claimed = (host, keys) => !!host && host[keys.marker] === true;
  // What a claim stored as the displaced original.
  const storedOriginal = (host, keys) => {
    const record = host;
    return Object.hasOwn(record, keys.original) ? record[keys.original] : undefined;
  };
  // Removes a claim's markers, so the next probe does not read a released claim as one.
  const dropClaimKeys = (host, keys) => {
    delete host[keys.marker];
    delete host[keys.original];
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
    value.kind === "steam-ui-property-snapshot-v1" &&
    typeof value.hadOwn === "boolean";
  // What a claimed member displaced, or the value itself when it is not ours. For code that has to
  // recognise a component by its source while the gate may already hold it: a probe or a re-resolve
  // that tests the live value sees the wrapper, and a wrapper carries none of the original's tokens.
  const unclaimedValue = (value, keys) => {
    if (!claimed(value, keys)) return value;
    const stored = storedOriginal(value, keys);
    return isPropertySnapshot(stored) ? stored.value : stored;
  };
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
  /**
   * Claims a field while preserving the exact underlying property across bridge replacements.
   * @param host Object containing the field, or null when unavailable.
   * @param field Existing field to replace.
   * @param keys Stable marker/original property names shared by later evaluations.
   * @param next Desired value; an unowned field already equal to it is refused.
   * @returns Claim status and reclaim flag, or a diagnostic; failed installation attempts rollback.
   */
  const claimValue = (host, field, keys, next) => {
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
      const original = reclaimed ? storedOriginal(host, keys) : fieldBefore;
      installDataValue(host, field, next);
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
  /**
   * Restores a claimed field through its saved descriptor or original accessor setter.
   * @param host Claimed object, or null.
   * @param field Field named at installation.
   * @param keys The same durable keys used to claim the field.
   * @returns Success for an absent claim; otherwise restoration status and any diagnostic.
   */
  const releaseValue = (host, field, keys) => {
    if (!host || !claimed(host, keys)) return { ok: true };
    try {
      // A claim whose stored original is not a snapshot restores nothing; saying so keeps the
      // caller from forgetting a claim that is still in place.
      const original = storedOriginal(host, keys);
      if (!isPropertySnapshot(original)) return { ok: false, error: "stored original invalid" };
      restoreProperty(host, field, original);
      dropClaimKeys(host, keys);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  /**
   * Replaces a member with a marked object/function without stacking prior toolkit wrappers.
   * @param host Object whose member is replaced, or null.
   * @param member Member to claim.
   * @param keys Durable marker/original keys on the replacement.
   * @param replacement Builds the replacement from the underlying original value.
   * @returns Claim status; a replacement that cannot carry its marker is refused.
   */
  const claimMember = (host, member, keys, replacement) => {
    if (!host) {
      return { ok: false, error: "claim host unavailable" };
    }
    const current = host[member];
    const reclaimed = claimed(current, keys);
    try {
      const original = reclaimed ? storedOriginal(current, keys) : captureProperty(host, member);
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
  /**
   * Restores only the currently marked member, including its prior inherited/absent state.
   * @param host Object carrying the replacement, or null.
   * @param member Claimed member name.
   * @param keys Marker/original keys from installation.
   * @returns Restoration status; an absent or no-longer-owned member is already released.
   */
  const releaseMember = (host, member, keys) => {
    if (!host) return { ok: true };
    const current = host[member];
    if (!claimed(current, keys)) return { ok: true };
    try {
      restoreProperty(host, member, storedOriginal(current, keys));
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  /**
   * Checks the live member for this claim marker.
   * @param host Object to inspect, or null/undefined.
   * @param member Member name.
   * @param keys Claim marker/original keys.
   * @returns Whether the current member is marked as owned.
   */
  const memberClaimed = (host, member, keys) => claimed(host?.[member], keys);
  /**
   * Supplies an absent namespace or reclaims an orphaned toolkit namespace.
   * @param host Namespace parent, or null.
   * @param name Property receiving the namespace.
   * @param marker Durable ownership marker.
   * @param factory Builds the supplied object; no real unmarked backend is replaced.
   * @returns Claim/reclaim status or the refusal reason.
   */
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
  /**
   * Deletes only the namespace carrying the supplied marker.
   * @param host Namespace parent, or null/undefined.
   * @param name Namespace property name.
   * @param marker Marker used when supplying the namespace.
   * @returns Removal status; missing or foreign namespaces are left intact.
   */
  const withdrawNamespace = (host, name, marker) => {
    if (!host || !claimed(host[name], { marker, original: marker })) return { ok: true };
    try {
      delete host[name];
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  /**
   * Claims a configurable own accessor while retaining its complete original descriptor.
   * @param host Object that defines the accessor, not an inheriting instance.
   * @param property Own property to replace.
   * @param keys Durable marker/original keys carried by the getter.
   * @param getter Replacement getter.
   * @returns Claim/reclaim status; unavailable or non-configurable properties are refused.
   */
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
  /**
   * Restores the descriptor saved by a still-owned accessor claim.
   * @param host Object defining the accessor, or null.
   * @param property Claimed property.
   * @param keys Marker/original keys from installation.
   * @returns Restoration status; missing saved originals remain failures.
   */
  const releaseAccessor = (host, property, keys) => {
    if (!host) return { ok: true };
    try {
      const descriptor = Object.getOwnPropertyDescriptor(host, property);
      if (!claimed(descriptor?.get, keys)) return { ok: true };
      const original = storedOriginal(descriptor.get, keys);
      // Our getter with nothing to hand back is still installed: not a success.
      if (!original) return { ok: false, error: "accessor original missing" };
      Object.defineProperty(host, property, original);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  };
  // One claim on a set of function members that several surfaces transform: taken with the first
  // transform and released with the last, because two wrappers on one member would each hand back the
  // other's wrapper or the original from under it on removal. `wrap` builds the replacement around the
  // displaced original and reads the live transforms at call time. The claim's marker and original
  // live on the wrapper, so a bridge replaced in place reclaims rather than wraps its predecessor.
  //
  // The wrappers sit on paths Steam calls for every element and every memo, so they read the
  // transforms as a plain array kept in registration order, rebuilt only when one is added or
  // withdrawn, and loop over it by index: a Map iterator per call was garbage on every render.
  const createSharedClaim = (keys, members, unavailable, uninstallable, wrap) => {
    const transforms = new Map();
    let active = Object.freeze([]);
    const refresh = () => {
      active = Object.freeze([...transforms.values()]);
    };
    const activeTransforms = () => active;
    let wrappers = null;
    const holds = (host) => {
      const current = wrappers;
      return !!current && members.every((member) => host[member] === current[member]);
    };
    const intercept = (host, name, transform) => {
      if (!host || members.some((member) => typeof host[member] !== "function")) {
        return { ok: false, error: unavailable };
      }
      transforms.set(name, transform);
      refresh();
      if (holds(host)) return { ok: true };
      const installed = {};
      for (const member of members) {
        const claim = claimMember(host, member, keys, (original) =>
          wrap(original, activeTransforms),
        );
        if (!claim.ok || !memberClaimed(host, member, keys)) {
          for (const done of Object.keys(installed)) releaseMember(host, done, keys);
          transforms.delete(name);
          refresh();
          return { ok: false, error: claim.ok ? uninstallable : claim.error };
        }
        installed[member] = host[member];
      }
      wrappers = installed;
      return { ok: true };
    };
    // Withdraws one transform, and hands the members back once none is left. Without a host the
    // wrappers stay installed, so that is reported and the caller releases again with one.
    const release = (host, name) => {
      transforms.delete(name);
      refresh();
      if (transforms.size) return { ok: true };
      if (!host) return wrappers ? { ok: false, error: "host unavailable" } : { ok: true };
      for (const member of members) {
        const released = releaseMember(host, member, keys);
        if (!released.ok) return released;
      }
      wrappers = null;
      return { ok: true };
    };
    const intercepted = (host, name) => !!host && transforms.has(name) && holds(host);
    return { intercept, release, intercepted };
  };
  // Intercepts what React.useMemo returns, for every surface that needs to see an array Steam builds
  // through it: the Quick Access tab list, the Settings page list. React has one useMemo, so this is
  // one shared claim. Transforms run in registration order, each seeing the result of the one before,
  // and one that throws leaves the value as it found it.
  const memoClaim = createSharedClaim(
    { marker: "__steamUiOwnedUseMemo", original: "__steamUiOriginalUseMemo" },
    ["useMemo"],
    "React useMemo unavailable",
    "React useMemo wrapper could not be installed",
    (original, transforms) =>
      function SteamUiUseMemo(factory, dependencies) {
        let value = original(factory, dependencies);
        const list = transforms();
        for (let index = 0; index < list.length; index++) {
          try {
            value = list[index](value);
          } catch {
            // A failing transform leaves what it was given.
          }
        }
        return value;
      },
  );
  /**
   * Registers a named transform on the shared React.useMemo claim; throwing transforms preserve the previous result.
   * @param host Steam React or JSX runtime object.
   * @param name Unique transform identity for later release.
   * @param transform Transformation callback; it must not recursively call the claimed wrappers.
   * @returns Installation status; transforms run in registration order.
   */
  const interceptMemo = memoClaim.intercept;
  /**
   * Withdraws one transform and restores the claimed functions when the last transform leaves.
   * @param host Original React/JSX host; required while a wrapper remains installed.
   * @param name Registered transform identity.
   * @returns Cleanup status; unavailable hosts with remaining wrappers are reported as failures.
   */
  const releaseMemo = memoClaim.release;
  /**
   * Checks that a named transform and all corresponding live wrappers are still owned.
   * @param host React/JSX host to inspect.
   * @param name Transform identity.
   * @returns Whether both registration and wrapper ownership hold.
   */
  const memoIntercepted = memoClaim.intercepted;
  const elementClaim = createSharedClaim(
    { marker: "__steamUiOwnedElements", original: "__steamUiOriginalElements" },
    ["jsx", "jsxs"],
    "JSX runtime unavailable",
    "JSX runtime wrapper could not be installed",
    (original, transforms) =>
      function SteamUiElement(type, props, key) {
        const list = transforms();
        for (let index = 0; index < list.length; index++) {
          try {
            const replaced = list[index](original, type, props, key);
            if (replaced !== undefined) return replaced;
          } catch {
            // A failing transform leaves the element to the runtime.
          }
        }
        return original.apply(this, arguments);
      },
  );
  /**
   * Registers a named JSX transform on the shared jsx/jsxs claims; the first defined result wins.
   * @param host Steam React or JSX runtime object.
   * @param name Unique transform identity for later release.
   * @param transform Transformation callback; it must not recursively call the claimed wrappers.
   * @returns Installation status; throwing transforms are skipped.
   */
  const interceptElements = elementClaim.intercept;
  /**
   * Withdraws one transform and restores the claimed functions when the last transform leaves.
   * @param host Original React/JSX host; required while a wrapper remains installed.
   * @param name Registered transform identity.
   * @returns Cleanup status; unavailable hosts with remaining wrappers are reported as failures.
   */
  const releaseElements = elementClaim.release;
  /**
   * Checks that a named transform and all corresponding live wrappers are still owned.
   * @param host React/JSX host to inspect.
   * @param name Transform identity.
   * @returns Whether both registration and wrapper ownership hold.
   */
  const elementsIntercepted = elementClaim.intercepted;
  // @fragment rpc.ts
  /**
   * Creates the success envelope expected by Steam service callers.
   * @param body Response body returned by Body().toObject().
   * @returns An object exposing BSuccess/BFailed/GetEResult/Body.
   */
  const transportReply = (body) => ({
    BSuccess: () => true,
    BFailed: () => false,
    GetEResult: () => 1,
    Body: () => ({ ...body, toObject: () => body }),
  });
  /**
   * Creates a refused Steam service response with EResult 2.
   * @param body Failure response body.
   * @returns The same transport shape as transportReply, marked failed.
   */
  const transportFailure = (body) => ({
    ...transportReply(body),
    BSuccess: () => false,
    BFailed: () => true,
    GetEResult: () => 2,
  });
  // Replacing a stub is only half the job: react-query still holds the answer the stub gave, so the
  // UI keeps rendering the refusal until the query that cached it is invalidated.
  //
  // The client has one query client, built by the module that provides it with its default options
  // and mounts the devtools beside it. Client builds renumber modules and rename exports, so it is
  // found by that provider's source and by the shape of the client.
  //
  // Failure is swallowed on purpose. A client whose query layer moved keeps the stale answer and the
  // row simply does not update — which is a degraded surface, not a broken one, and never a reason to
  // tear down a gate that is otherwise working.
  const QueryClientTokens = ["ReactQueryDevtools", "offlineFirst"];
  const isQueryClient = (value) =>
    typeof value?.invalidateQueries === "function" && typeof value?.getQueryState === "function";
  /**
   * Resolves Steam's query client without exposing factory or export identities to callers.
   * @param req Shared module resolver.
   * @returns The uniquely shaped query client, or null on discovery/load failure.
   */
  const resolveQueryClient = (req) => {
    try {
      return req?.exported(QueryClientTokens, isQueryClient) ?? null;
    } catch {
      return null;
    }
  };
  /**
   * Invalidates a Steam query after a supplied service reply changes.
   * @param req Shared module resolver.
   * @param queryKey Query key to invalidate.
   */
  const invalidateQuery = (req, queryKey) => {
    try {
      resolveQueryClient(req)?.invalidateQueries({ queryKey });
    } catch {
      // Intentionally ignored; see above.
    }
  };
  // @fragment file-picker.ts
  // Controller-accessible file selection; the host owns directory enumeration.
  const SteamFilePickerPatchId = "steam-ui.file-picker";
  /**
   * Opens a contained two-pane file picker using native Steam controls.
   * @param ui Steam's resolved React, buttons, focus and modal components.
   * @param options Title, folder/file mode, extension filters and optional starting path.
   * @returns The accepted path or null; cancellation and late listings cannot settle it twice.
   */
  const showSteamFilePicker = (ui, options = {}) =>
    new Promise((resolve) => {
      const react = ui?.react;
      const mode = options.mode === "file" ? "file" : "folder";
      const extensions = Array.isArray(options.extensions) ? options.extensions : [];
      const title = options.title ?? (mode === "folder" ? "Choose a folder" : "Choose a file");
      let settled = false;
      const settle = (value) => {
        if (!settled) {
          settled = true;
          resolve(value);
        }
      };
      function Picker({ close }) {
        const h = react.createElement;
        const [places, setPlaces] = react.useState([]);
        const [listing, setListing] = react.useState(null);
        const [error, setError] = react.useState("");
        const [loading, setLoading] = react.useState(false);
        const requested = react.useRef(0);
        const finish = (path) => {
          if (settled) return;
          settle(path);
          close();
        };
        const open = (path) => {
          const ticket = ++requested.current;
          setLoading(true);
          setError("");
          void request(SteamFilePickerPatchId, "listFolder", {
            path,
            extensions: mode === "folder" ? [] : extensions.length ? extensions : [".*"],
          }).then(
            (answer) => {
              if (ticket !== requested.current) return;
              setLoading(false);
              if (answer) {
                setListing(answer);
                setError(answer.error ?? "");
              }
            },
            (failure) => {
              if (ticket !== requested.current) return;
              setLoading(false);
              setError(String(failure?.message ?? failure));
            },
          );
        };
        react.useEffect(() => {
          void request(SteamFilePickerPatchId, "listPlaces", {}).then(
            (answer) => {
              if (requested.current < 0) return;
              const found = answer?.places ?? [];
              setPlaces(found);
              const first = options.start || found[0]?.path;
              if (first && requested.current === 0) open(first);
            },
            (failure) => {
              if (requested.current >= 0) setError(String(failure?.message ?? failure));
            },
          );
          return () => {
            requested.current = -1;
            settle(null);
          };
        }, []);
        const current = listing?.path ?? "";
        const up = () => listing?.parent && open(listing.parent);
        const useCurrent = () => {
          if (mode === "folder" && current && !listing?.error && !loading) finish(current);
        };
        const entries = listing?.entries ?? [];
        return h(
          ui.focusable,
          {
            className: "steam-ui-kit-picker",
            "flow-children": "column",
            onCancelButton: () => finish(null),
            onSecondaryButton: useCurrent,
            onSecondaryActionDescription: mode === "folder" ? "Use this folder" : undefined,
            onOptionsButton: up,
            onOptionsActionDescription: "Up one level",
          },
          steamUiKitStyle(react),
          h(
            ui.focusable,
            { className: "steam-ui-kit-picker-actions", "flow-children": "row" },
            h(ui.dialogButton, { disabled: !listing?.parent || loading, onClick: up }, "Up"),
            h(
              "div",
              { className: "steam-ui-kit-picker-path", role: "status" },
              loading ? (current || "Places") + " (loading)" : current,
            ),
          ),
          h(
            "div",
            { className: "steam-ui-kit-picker-body" },
            h(
              ui.focusable,
              { className: "steam-ui-kit-picker-places", "flow-children": "column" },
              h("div", { className: "steam-ui-kit-picker-heading" }, "Places"),
              ...places.map((place) =>
                renderSteamUiSelectRow(ui, {
                  key: place.path,
                  title: place.name,
                  detail: place.detail,
                  selected: current === place.path,
                  onClick: () => open(place.path),
                }),
              ),
            ),
            h(
              ui.focusable,
              { className: "steam-ui-kit-picker-files", "flow-children": "column" },
              h("div", { className: "steam-ui-kit-picker-heading" }, "Files and folders"),
              ...entries.map((entry) =>
                renderSteamUiSelectRow(ui, {
                  key: entry.path,
                  title: entry.name,
                  status: h(
                    "span",
                    { className: "steam-ui-kit-muted" },
                    entry.folder ? "Folder" : "File",
                  ),
                  onClick: () => (entry.folder ? open(entry.path) : finish(entry.path)),
                }),
              ),
              entries.length === 0 && !loading && !error
                ? h("p", {}, "This folder is empty.")
                : null,
            ),
          ),
          error ? h("p", { className: "steam-ui-kit-sheet-error", role: "alert" }, error) : null,
          h(
            ui.focusable,
            { className: "steam-ui-kit-picker-actions", "flow-children": "row" },
            h(ui.dialogButton, { onClick: () => finish(null) }, "Cancel"),
            mode === "folder"
              ? h(
                  ui.dialogButtonPrimary ?? ui.dialogButton,
                  {
                    onClick: useCurrent,
                    disabled: !current || !!listing?.error || loading,
                  },
                  "Use this folder",
                )
              : null,
          ),
        );
      }
      if (
        !showSteamModal(ui, {
          title,
          render: (close) => react.createElement(Picker, { close }),
          onCancel: () => settle(null),
        })
      )
        settle(null);
    });
  // @fragment gate-helpers.ts
  // What the gates that walk Steam's React output have in common, and the lifecycle steps every gate
  // repeats.
  //
  // Constants and functions only. Nothing here runs while the bundle is evaluated, and gates call it
  // only once the whole bundle has run, so this fragment's place in the discovered order does not
  // matter. Fingerprints for a module more than one surface resolves live here once, so two gates
  // cannot drift onto different spellings of the same module.
  // Steam's React module, by the four names only it carries together.
  const ReactTokens = ["react.transitional.element", "useState", "cloneElement", "createElement"];
  // The module holding Steam's SliderField, DropDownField and ToggleField.
  const FieldTokens = ["DialogSlider_Container", "DropDownField", "SliderField"];
  // DropDownField within that module, by the markers of its own body.
  const DropdownMarkers = ["contextMenuPositionOptions", "childrenContainerWidth", "menuLabel"];
  // Steam's library item class map, by three class names only it carries together: the library
  // capsule is styled by it and the library badge reads its tile classes from it.
  const SteamLibraryClassTokens = [
    'ControllerSupportIcon:"',
    'LibraryItemIcons:"',
    'LibraryItemBox:"',
  ];
  // The JSX runtime module: `jsx` and `jsxs` beside React's element marker.
  const JsxRuntimeTokens = ["react.transitional.element", ".jsx", ".jsxs"];
  // The client settings store: the store class's own getter and its deferred-settings set.
  const SettingsTokens = ["get clientSettings()", "m_setDeferredSettings"];
  // mobx-react-lite's own startup check, present once in the client.
  const ObserverTokens = ["mobx-react-lite requires React with Hooks support"];
  // The localization module.
  const LocalizationTokens = [
    "Attempting to localize token",
    "Unable to find localization token",
    "LocalizeString",
  ];
  // Steam's React exports, or null when the module is not a unique match.
  const resolveReact = (runtime) => {
    const factory = runtime.findUnique(ReactTokens);
    return factory ? runtime(factory[0]) : null;
  };
  // Steam's Panel joins its navigation graph and maps onActivate to mouse and gamepad OK.
  // Generic button forwardRefs have identical closure bodies, so their source cannot identify them.
  const NativeFocusableTokens = ["focusableIfEmpty", "onActivate", '"Panel"'];
  const resolveNativeFocusable = (runtime) =>
    runtime.exported([...NativeFocusableTokens], (value) => {
      if (typeof value !== "function") return false;
      const source = String(value);
      return ["onActivate", "onCancel", "focusableIfEmpty", "focusClassName"].every((token) =>
        source.includes(token),
      );
    });
  // Native Steam controls shared by plugin pages and toolkit-owned surfaces. Component export names
  // are minified and change between client builds, so every control is selected from a uniquely
  // fingerprinted provider by its own behavior. Consumers must treat a null optional control as an
  // unavailable capability rather than replacing it with an imitation.
  const uniqueSteamExport = (exports, predicate) => {
    const matches = new Set();
    for (const name of Object.keys(exports ?? {})) {
      try {
        const value = exports[name];
        if (predicate(value)) matches.add(value);
      } catch {
        // An export whose getter or shape test throws is not the requested component.
      }
    }
    return matches.size === 1 ? [...matches][0] : null;
  };
  const sourceOfSteamComponent = (value) => {
    if (typeof value === "function") return String(value);
    return typeof value?.render === "function" ? String(value.render) : "";
  };
  // The class names a component's source writes in its string literals, whether its author passed
  // them to the class-name helper one by one or as one string. The quotes and the separators between
  // the arguments are the minifier's; the names are the author's.
  const steamClassNamesOf = (source) => {
    const names = new Set();
    for (const literal of source.matchAll(/"([^"\\]*)"|'([^'\\]*)'/gu)) {
      for (const name of (literal[1] ?? literal[2]).split(/\s+/u)) {
        if (name) names.add(name);
      }
    }
    return names;
  };
  // One of Steam's dialog buttons by the classes it draws with: DialogButton and _DialogLayout, and
  // the variant (Secondary, Primary, Small) that tells the buttons apart.
  const isSteamDialogButton = (value, variant) => {
    const names = steamClassNamesOf(sourceOfSteamComponent(value));
    return names.has("DialogButton") && names.has("_DialogLayout") && names.has(variant);
  };
  const optionalSteamExport = (runtime, tokens, predicate) => {
    try {
      return runtime.exported(tokens, predicate);
    } catch {
      return null;
    }
  };
  /**
   * Resolves React and native field components for row renderers.
   * @param runtime Shared module resolver.
   * @returns Available field components, or null when foundational module evidence is absent.
   */
  const resolveSteamFieldComponents = (runtime) => {
    const react = resolveReact(runtime);
    const fieldsFactory = runtime.findUnique(FieldTokens);
    if (!react || !fieldsFactory) return null;
    const fields = runtime(fieldsFactory[0]);
    const sliderField = uniqueSteamExport(fields, (value) => {
      if (typeof value !== "function") return false;
      const source = String(value);
      return ["onChangeComplete", "notchCount", "valueSuffix", "explainerTitle"].every((token) =>
        source.includes(token),
      );
    });
    const dropdown = uniqueSteamExport(fields, (value) => {
      if (typeof value !== "function") return false;
      const source = String(value);
      return DropdownMarkers.every((token) => source.includes(token));
    });
    // The bare dropdown that DropDownField wraps in a labelled row: a toolbar wants the button on
    // its own. Chosen the way decky-frontend-lib chooses it, by the two prototype members only it
    // declares, tested by name so no getter runs. Wanted, not required: a page that lacks it draws
    // the labelled field.
    const dropdownControl = uniqueSteamExport(
      fields,
      (value) =>
        typeof value === "function" &&
        !!value.prototype &&
        "SetSelectedOption" in value.prototype &&
        "BuildMenu" in value.prototype,
    );
    const toggleField = uniqueSteamExport(fields, (value) => {
      const source = sourceOfSteamComponent(value);
      return source.includes("OnToggleChange") && source.includes("this.Toggle()");
    });
    const dialogButton = uniqueSteamExport(fields, (value) =>
      isSteamDialogButton(value, "Secondary"),
    );
    const dialogButtonPrimary = uniqueSteamExport(fields, (value) =>
      isSteamDialogButton(value, "Primary"),
    );
    // The class that DEFINES the validators, not one that merely inherits them. A class extending
    // TextField answers `typeof validateUrl === "function"` through its prototype chain, and the
    // 2026-09-24 client exports such a subclass beside the base: two fits, no unique match, no
    // textField, and every settings page that needs one went Degraded. Own properties name the base.
    const textField = uniqueSteamExport(
      fields,
      (value) =>
        typeof value === "function" &&
        Object.prototype.hasOwnProperty.call(value, "validateUrl") &&
        Object.prototype.hasOwnProperty.call(value, "validateEmail") &&
        typeof value.validateUrl === "function" &&
        typeof value.validateEmail === "function",
    );
    return {
      react,
      sliderField,
      dropdown,
      dropdownControl,
      toggleField,
      dialogButton,
      dialogButtonPrimary,
      textField,
    };
  };
  /**
   * Resolves native page controls using shared module fingerprints and export shapes.
   * @param runtime Shared module resolver; matching factory loads may throw.
   * @returns The available component collection, or null when base fields cannot resolve.
   */
  const resolveSteamUiComponents = (runtime) => {
    const fields = resolveSteamFieldComponents(runtime);
    if (!fields) return null;
    const focusable = resolveNativeFocusable(runtime);
    // The tabbed page is the module's one export that wraps a function component (a memo, here an
    // observer), the rest being plain functions and a context; two such exports are refused.
    const tabsFactory = runtime.findUnique([".TabRowTabs", "activeTab:"]);
    const tabs = tabsFactory
      ? uniqueSteamExport(runtime(tabsFactory[0]), (value) => typeof value?.type === "function")
      : null;
    const modalRoot = optionalSteamExport(
      runtime,
      ["Either closeModal or onCancel should be passed to GenericDialog. Classes: "],
      (value) =>
        typeof value === "function" &&
        String(value).includes("Either closeModal or onCancel should be passed to GenericDialog"),
    );
    const showModalRaw = optionalSteamExport(
      runtime,
      ["props.bDisableBackgroundDismiss"],
      (value) =>
        typeof value === "function" &&
        String(value).includes("props.bDisableBackgroundDismiss") &&
        !value?.prototype?.Cancel,
    );
    const showModal = showModalRaw
      ? (modal, parent = window, props = {}) =>
          showModalRaw(
            modal,
            parent,
            props.strTitle ?? "",
            { bHideMainWindowForPopouts: false, ...props },
            undefined,
            { bHideActions: props.bHideActionIcons },
          )
      : null;
    // Steam's own checkbox, the DialogCheckbox its dialogs tick options with. It lives in its own
    // module beside the toggle's base class and takes the same props: label, description, checked,
    // onChange, disabled. Chosen by what its author wrote - the class name it draws and the two
    // methods decky-frontend-lib also picks it by - never by how the minifier joined them. Wanted,
    // not required: a page that needs it says so, and `steamCheckbox` falls back to the toggle.
    const checkbox = optionalSteamExport(
      runtime,
      ["DialogCheckbox_Container"],
      (value) =>
        typeof value === "function" &&
        !!value.prototype &&
        "SetChecked" in value.prototype &&
        "Toggle" in value.prototype &&
        String(value).includes('"DialogCheckbox"'),
    );
    return {
      ...fields,
      focusable,
      tabs,
      modalRoot,
      showModal,
      checkbox,
    };
  };
  // Valve's panel pieces, the ones every Quick Access tab is built from: PanelSection, which draws a
  // titled section, and PanelSectionRow, which lays one control out inside it. Both come from the one
  // layout module that names them together; null when either is not a unique match there.
  const PanelLayoutTokens = ["PanelSectionTitle", "PanelSectionRow", "spinner"];
  /**
   * Resolves both native Quick Access section and row components.
   * @param runtime Shared module resolver.
   * @returns The pair, or null unless both exports match uniquely.
   */
  const resolveSteamPanelComponents = (runtime) => {
    const factory = runtime.findUnique(PanelLayoutTokens);
    if (!factory) return null;
    const layout = runtime(factory[0]);
    const section = uniqueSteamExport(layout, (value) => {
      if (typeof value !== "function") return false;
      const source = String(value);
      return source.includes("PanelSectionTitle") && source.includes("spinner");
    });
    const row = uniqueSteamExport(
      layout,
      (value) =>
        !!value &&
        typeof value === "object" &&
        !!value.$$typeof &&
        typeof value.render === "function",
    );
    return section && row ? { section, row } : null;
  };
  // The folds of the Quick Access tabs' sections, one mechanism for all of them. The host publishes
  // the sections the user opened under `SteamFoldsPatchId`, so every section starts folded, and a
  // heading asks for a change with `setFolded`. A fold is shown at once: the override holds until
  // the host's next publication agrees with it, so a host that keeps folds has the last word, and a
  // host without the module leaves them to last the session. Ids are the host's to keep and the
  // gate's to name: a Performance or Quick Settings section by its title, an Extensions tab item as
  // `extensions:<item>`, a switch's settings under it as `extensions:<item>:<key>`.
  const SteamFoldsPatchId = "steam-ui.panel-folds";
  const createSteamFolds = () => {
    const overrides = new Map();
    return {
      // The host's list, as a set of the open ids, or null for a state that is not one.
      normalize(value) {
        if (!value || typeof value !== "object" || !Array.isArray(value.open)) return null;
        const open = new Set(value.open.filter((id) => typeof id === "string" && id.length > 0));
        for (const [id, folded] of overrides) {
          if (open.has(id) === !folded) overrides.delete(id);
        }
        return open;
      },
      isFolded: (open, id) => (overrides.has(id) ? overrides.get(id) : !(open && open.has(id))),
      // `changed` redraws the root that asked, at once and again if the host refuses.
      setFolded(id, folded, changed) {
        overrides.set(id, folded);
        changed();
        void request(SteamFoldsPatchId, "setFolded", { id, folded }).catch(() => {});
      },
    };
  };
  /**
   * Selects a native checkbox with a toggle fallback.
   * @param ui Steam's resolved React and native control components.
   * @returns The checkbox/toggle component, or null when neither exists.
   */
  const steamCheckbox = (ui) => ui?.checkbox ?? ui?.toggleField ?? null;
  /**
   * Draws a native toolbar dropdown, falling back to a labelled field.
   * @param ui Steam's resolved React and native control components.
   * @param props Native dropdown props, including options, current selection, label and change handler.
   * @returns The dropdown element.
   */
  const renderSteamDropdown = (ui, props) =>
    ui.dropdownControl
      ? ui.react.createElement(ui.dropdownControl, {
          rgOptions: props.rgOptions,
          selectedOption: props.selectedOption,
          onChange: props.onChange,
          disabled: props.disabled,
          menuLabel: props.label,
        })
      : ui.react.createElement(ui.dropdown, {
          label: props.label,
          rgOptions: props.rgOptions,
          selectedOption: props.selectedOption,
          onChange: props.onChange,
          disabled: props.disabled,
          layout: "below",
        });
  /**
   * Opens a native modal and supplies a close callback to its content.
   * @param ui Steam's resolved React and native control components.
   * @param options Optional title/class, content renderer and cancellation callback.
   * @returns True when shown, false when modal components are unavailable.
   */
  const showSteamModal = (ui, options) => {
    if (!ui?.showModal || !ui?.modalRoot) return false;
    const react = ui.react;
    const title = options.title ?? "";
    function SteamModal(props) {
      const close = props?.closeModal ?? (() => {});
      const cancel = () => {
        options.onCancel?.();
        close();
      };
      return react.createElement(
        ui.modalRoot,
        { className: options.className, onCancel: cancel, closeModal: cancel, strTitle: title },
        options.render(close),
      );
    }
    ui.showModal(react.createElement(SteamModal, {}), window, { strTitle: title });
    return true;
  };
  // Steam's gamepad button codes, as a Focusable's onButtonDown reports them in event.detail.button.
  const SteamGamepadButton = Object.freeze({ TriggerLeft: 7, TriggerRight: 8 });
  /**
   * Creates a handler that consumes LT/RT and leaves other buttons to Steam.
   * @param step Receives -1 for LT and +1 for RT.
   * @returns A Steam Focusable onButtonDown handler.
   */
  const onSteamTriggers = (step) => (event) => {
    const button = event?.detail?.button;
    if (button !== SteamGamepadButton.TriggerLeft && button !== SteamGamepadButton.TriggerRight)
      return;
    event?.stopPropagation?.();
    step(button === SteamGamepadButton.TriggerRight ? 1 : -1);
  };
  // Closes whichever side panel is open, so a route followed from inside one is not rendered behind
  // it. Valve's own main-window instance owns the operation; SteamNativeSurfaceCommands drives the
  // same MenuStore.CloseSideMenus for the keyboard overlay. Reports whether the panel is now closed,
  // which for a caller that was never in a panel is trivially true.
  const closeSteamSideMenus = () => {
    const menus = window.SteamUIStore?.WindowStore?.MainWindowInstance?.MenuStore;
    if (typeof menus?.CloseSideMenus !== "function") return false;
    try {
      menus.CloseSideMenus();
      return true;
    } catch (_) {
      return false;
    }
  };
  // Whether an array of elements is a router's route list: found by content, an array of more than two
  // children holding a route for a path every client has. However many routes the client has.
  const isSteamRouteList = (react, value, knownRoute) =>
    Array.isArray(value) &&
    value.length > 2 &&
    value.some((item) => react.isValidElement(item) && item.props?.path === knownRoute);
  // An absolute route other than the root.
  // Absolute, not the root, and free of control characters, as the C# side refuses them too. No length
  // limit: a long route is still a route.
  const isNavigableRoute = (route) =>
    typeof route === "string" &&
    route.startsWith("/") &&
    route !== "/" &&
    !/[\u0000-\u001f\u007f]/u.test(route);
  /**
   * Pushes an absolute non-root route through the existing Steam history.
   * @param route Route without control characters; no length bound is imposed.
   * @returns False for an invalid route or missing history; true after push. History exceptions propagate.
   */
  const navigateSteamRoute = (route) => {
    if (!isNavigableRoute(route)) {
      return false;
    }
    const history = window.tempNavStore?.m_history;
    if (!history || typeof history.push !== "function") return false;
    history.push(route);
    return true;
  };
  // Whether a source calls `call` and every such call passes a single argument. Counting the
  // arguments reads what the author wrote, however the minifier then names and spells them.
  const callsWithOneArgument = (source, call) => {
    let found = false;
    for (let at = source.indexOf(call); at >= 0; at = source.indexOf(call, at + call.length)) {
      let depth = 0;
      for (let index = at + call.length; index < source.length; index++) {
        const char = source[index];
        if (char === "(" || char === "[" || char === "{") {
          depth++;
        } else if (char === ")" || char === "]" || char === "}") {
          if (depth === 0) break;
          depth--;
        } else if (char === "," && depth === 0) {
          return false;
        }
      }
      found = true;
    }
    return found;
  };
  // Valve's localize-with-fallback, chosen by what its source does: it hands LocalizeString the token
  // and nothing else, and builds no elements. Its siblings in the module differ in exactly that: the
  // quiet variant and the presence test pass LocalizeString a second argument, and the formatting
  // variant builds elements around the string. An earlier match on how the minifier spelled the
  // comparisons broke with the September 2026 beta.
  const isLocalizer = (source) =>
    callsWithOneArgument(source, ".LocalizeString(") && !source.includes("createElement");
  // Valve's localize-with-fallback from the localization module, by its shape (isLocalizer), or null
  // when the module or the function is not a unique match. When the minifier broke the older
  // name-based match, every Quick Access row refused with "React, fields, layout or localization
  // runtime was not a unique match".
  const resolveSteamLocalizer = (runtime) => {
    const localization = runtime.findUnique([...LocalizationTokens]);
    if (!localization) return null;
    return uniqueSteamExport(runtime(localization[0]), (value) => {
      if (typeof value !== "function") return false;
      const source = String(value);
      return !source.startsWith("class") && isLocalizer(source);
    });
  };
  // Steam's string for a token, or the fallback when the localizer is absent or has no string.
  const localizedOr = (localize, token, fallback) => {
    try {
      const text = localize?.(token);
      if (typeof text === "string" && text && text !== token) return text;
    } catch {
      // The fallback stands in for a localizer that did not answer.
    }
    return fallback;
  };
  // The text a label carries, or null. A label is sometimes a plain string and sometimes what Steam's
  // localizer returns, which is a React element wrapping the string rather than the string itself.
  const textOf = (value) => {
    if (typeof value === "string") return value;
    return value && typeof value === "object" && typeof value.props?.children === "string"
      ? value.props.children
      : null;
  };
  // State a gate keeps outside Steam's stores, read by its components through React's
  // useSyncExternalStore. `changed` advances the revision and tells every subscriber; a listener that
  // throws does not stop the others.
  const createLocalStore = () => {
    let revision = 0;
    const listeners = new Set();
    return {
      changed() {
        revision += 1;
        for (const listener of [...listeners]) {
          try {
            listener();
          } catch {}
        }
      },
      subscribe(listener) {
        listeners.add(listener);
        return () => listeners.delete(listener);
      },
      revision: () => revision,
    };
  };
  // mobx-react-lite's useObserver, found by its shape in the module that carries the startup check,
  // or null. Wanted by the surfaces that use it, never required.
  const findUseObserver = (runtime) => {
    const observer = runtime.findUnique(ObserverTokens);
    if (!observer) return null;
    const exports = runtime(observer[0]);
    const hooks = Object.keys(exports).filter((name) => {
      const value = exports[name];
      return (
        typeof value === "function" && value.length === 2 && String(value).includes('"observed"')
      );
    });
    return hooks.length === 1 ? exports[hooks[0]] : null;
  };
  // A webpack class map, unwrapped when the module is an ES default export.
  const classMapOf = (exported) => (exported && exported.__esModule ? exported.default : exported);
  // Steam's accent blue, which a row the host marks draws its description in. The Quick Access rows
  // and the settings pages share it, so a marked value reads the same on both.
  const SteamAccentColor = "#1a9fff";
  // A description, as one span in the accent colour when the host marks the row, and as its plain
  // text otherwise. The words are the host's; an empty unmarked description draws nothing.
  const steamAccentDescription = (react, text, accent) =>
    accent
      ? react.createElement("span", { style: { color: SteamAccentColor } }, text)
      : text || undefined;
  /**
   * Builds props that retain an existing React element's key.
   * @param element Element whose identity is preserved.
   * @param props Replacement props; defaults to the element's current props.
   * @returns Original props for a null key; otherwise a shallow props copy containing the key.
   */
  const keyed = (element, props = element.props) =>
    element.key === null ? props : { ...props, key: element.key };
  // A portal is not an element: isValidElement answers false, and its children sit on the portal
  // itself rather than under props. Steam's Quick Access menu draws its whole body through one into
  // the popup window, so a descent that treats a portal as a leaf never reaches the tab list beneath
  // it (2026-09-24). React reads a portal by `$$typeof`, `children` and `containerInfo`, so a shallow
  // copy with mapped children is a portal to it.
  const PortalType = Symbol.for("react.portal");
  // What React marks a memo with, the one way a memo object is told apart from other exports.
  const ReactMemoType = Symbol.for("react.memo");
  const isPortal = (value) => !!value && typeof value === "object" && value.$$typeof === PortalType;
  // Maps a child list; answers the new list, or null when no child changed. A child mapped to null
  // is dropped. Shared by element and portal mapping so the two cannot drift on those rules.
  const mapEach = (react, children, map) => {
    const kids = react.Children.toArray(children);
    if (!kids.length) return null;
    let changed = false;
    const next = [];
    for (const kid of kids) {
      const replacement = map(kid);
      changed ||= replacement !== kid;
      if (replacement !== null) next.push(replacement);
    }
    return changed ? next : null;
  };
  const mapPortalChildren = (react, portal, map) => {
    const next = mapEach(react, portal.children, map);
    return next ? { ...portal, children: next } : portal;
  };
  // Maps an element's children and clones it only when one changed. An element with no children is
  // returned as it is.
  const mapChildren = (react, element, map) => {
    const next = mapEach(react, element.props?.children, map);
    return next ? react.cloneElement(element, {}, ...next) : element;
  };
  // Renders a plain function component through a wrapper, so what it returns can be changed as well:
  // a component's children do not exist until it renders. The wrapper `wrap` builds is cached against
  // the component, because a fresh type on every render would remount the subtree. Class components,
  // memo and forwardRef objects are left alone, since they cannot be called directly and wrapping
  // them would change identity for refs; for those, and for anything that is not an element of a
  // function type, this answers null.
  // The wrapper built for a type, once: a fresh identity on every render would remount the subtree.
  const cachedWrapper = (cache, type, wrap) => {
    let wrapper = cache.get(type);
    if (!wrapper) {
      wrapper = wrap(type);
      cache.set(type, wrapper);
    }
    return wrapper;
  };
  const descendInto = (react, element, cache, wrap) => {
    const type = element.type;
    if (typeof type !== "function" || type.prototype?.isReactComponent) return null;
    return react.createElement(cachedWrapper(cache, type, wrap), keyed(element));
  };
  // Runs a gate's resolution. A throw is handed to `failed` to record under the gate's own wording; a
  // resolution that answers false has already recorded why.
  const attemptResolution = (resolve, failed) => {
    try {
      return resolve();
    } catch (error) {
      failed(error);
      return false;
    }
  };
  /**
   * Invokes an optional subscription disposer; cleanup exceptions propagate.
   * @param unsubscribe Current disposer, or null.
   * @returns Null, suitable for assigning back to the owner's subscription field.
   */
  const endSubscription = (unsubscribe) => {
    unsubscribe?.();
    return null;
  };
  // React's mounted trees, for the gates that find a module-local component by where it is drawn.
  //
  // One root fiber per container React attached to under the document: the `#root` host and any
  // other body child that carries a container key. SharedJSContext keeps a second, empty container
  // beside `#root` on the September 2026 client. Each container names the fiber root React created;
  // the root's `current` is the tree on screen, and after the first commit that is not always the
  // fiber the container key was written with. A supplied document lets a gate read a known Steam
  // popup's roots without changing the default SharedJSContext lookup.
  const reactRootFibers = (doc = typeof document === "undefined" ? null : document) => {
    // A page always has a document; an emitted-asset check may not, and then nothing is mounted.
    if (!doc) return [];
    const hosts = [];
    const root = doc.getElementById("root");
    if (root) hosts.push(root);
    for (const child of Array.from(doc.body?.children ?? [])) {
      if (child !== root) hosts.push(child);
    }
    const roots = [];
    for (const host of hosts) {
      const key = Object.keys(host).find((name) => name.startsWith("__reactContainer$"));
      if (!key) continue;
      const fiber = host[key];
      roots.push(fiber?.stateNode?.current ?? fiber);
    }
    return roots;
  };
  // Walks mounted fibers breadth-first over the child and sibling links, bounded, until `visit`
  // answers true. Breadth-first because a router sits near the top of its tree, and a depth-first
  // walk can spend the whole bound inside the first large subtree — a mounted library grid — before
  // it gets there. Answers how many fibers were seen and whether the walk stopped on a match.
  const walkFibers = (roots, bound, visit) => {
    const queue = roots.slice();
    let visited = 0;
    for (let head = 0; head < queue.length && visited < bound; head++) {
      const node = queue[head];
      if (!node) continue;
      visited++;
      if (visit(node) === true) return { visited, stopped: true };
      queue.push(node.child, node.sibling);
    }
    return { visited, stopped: false };
  };
  // The mounted fibers drawing one component: those whose element type is the memo or function that
  // was claimed. The walk covers the tree on screen, so each mounted instance answers once.
  const mountedFibersOf = (roots, elementType, bound) => {
    const fibers = [];
    walkFibers(roots, bound, (fiber) => {
      if (fiber.elementType === elementType) fibers.push(fiber);
    });
    return fibers;
  };
  // Where a fiber's real props are kept while an adoption render is forced; see adoptMountedType.
  const AdoptedPropsKey = "__steamUiAdoptedProps";
  const MaximumAncestors = 64;
  // Asks the nearest class component above a fiber to render again. Steam's router switch is a
  // class, so a claimed page under it re-renders the way a navigation renders it. `forceUpdate` is
  // React's public API for exactly this. Answers whether an instance was found and asked.
  const requestRender = (fiber) => {
    let node = fiber.return;
    for (let depth = 0; node && depth < MaximumAncestors; depth++) {
      const instance = node.stateNode;
      if (instance?.isReactComponent && typeof instance.forceUpdate === "function") {
        try {
          instance.forceUpdate();
          return true;
        } catch {
          return false;
        }
      }
      node = node.return;
    }
    return false;
  };
  // The three writes that bring a claim to a fiber already on screen, each to a plain field:
  //   - `type` on the fiber and its alternate, so the next render calls the replacement. The
  //     replacement must add no hooks of its own: the fiber keeps the hook list the original built,
  //     and React refuses a render that ends with more hooks than the last.
  //   - `memoizedProps` swapped for an object that shallow-compares unequal to the real props, so
  //     React's memo bail-out cannot skip the render. React writes the real props back when it
  //     renders; until then they stay reachable under AdoptedPropsKey.
  //   - a render requested from the nearest class ancestor (requestRender), so it happens now.
  const invalidateFiberProps = (fiber) => {
    for (const side of [fiber, fiber.alternate]) {
      if (side) side.memoizedProps = { [AdoptedPropsKey]: side.memoizedProps };
    }
  };
  const retargetFiber = (fiber, type) => {
    for (const side of [fiber, fiber.alternate]) {
      if (side) side.type = type;
    }
  };
  // Whether a fiber has a parent link on either side; a fiber sitting directly under a React root
  // never had one, so only one that had a parent and lost it has been detached.
  const fiberAttached = (fiber) => !!(fiber.return || fiber.alternate?.return);
  // Brings a claim on a component's `type` to the instances already on screen.
  //
  // A claim on a memo's `type` reaches the next mount only: when React mounts a memo it resolves the
  // function once and caches it on the fiber as `type`, and every later render of that fiber reads
  // the cache, not the memo. Big Picture mounts its router, Home, the Quick Access view and the menu
  // at boot and keeps them, so a claim alone is inert until the user happens to remount one; the
  // carousel (2026-09-22) and every page gate (2026-09-24) shipped that way. Answers the fibers
  // adopted and whether a render was requested; without a class ancestor the adoption still holds
  // and takes effect on the instance's next render.
  const adoptMountedType = (roots, elementType, replacement, bound) => {
    const fibers = [];
    let scheduled = false;
    for (const fiber of mountedFibersOf(roots, elementType, bound)) {
      if (fiber.type === replacement) continue;
      retargetFiber(fiber, replacement);
      invalidateFiberProps(fiber);
      fibers.push(fiber);
      scheduled = requestRender(fiber) || scheduled;
    }
    return { fibers, adopted: fibers.length, scheduled };
  };
  // The node bound mounted trees are walked under. The router sits about a hundred levels down the
  // live tree and the popups are shallower, so this is generous; it exists to stop a cyclic or
  // pathological tree, not to limit a legitimate search.
  const MaximumMountedNodes = 60000;
  // Whether a publication carries something the wrappers have not drawn yet. Publications repeat
  // the same state every round, several times a second while the host has anything to say, and a
  // gate that re-rendered on each one asked the router's class ancestor to render again each time.
  // That render re-runs every route under it: Steam's controller configurator restarts its edit
  // session on each render of its route and threw away the user's bindings every few seconds
  // (2026-09-26). A wrapper reads its state from a closure, so the only publications that need a
  // render are the ones that changed it.
  const publicationChanged = (previous, next) => JSON.stringify(previous) !== JSON.stringify(next);
  // One claimed component's mounted instances, for the life of a gate's install.
  //
  // Adoption walks the tree once and keeps the fibers it adopted. Everything after that is over that
  // list rather than the tree: a publication asks them to render again (the wrapper reads the gate's
  // state from a closure, so the props have not changed and a memo with equal props bails out exactly
  // as it did before adoption); status counts them; release hands them back. Publications arrive
  // several times a second while a user browses artwork and status is read on every verify, so a
  // tree walk on either was a full 60000-node pass on the UI thread for a number that never changed.
  // A fiber React has since unmounted is dropped when next seen; the claim on the memo's `type`
  // reaches any instance mounted after adoption on its own.
  const createMountedAdoption = (bound = MaximumMountedNodes) => {
    // Each adopted fiber with whether it had a parent when adopted; see fiberAttached.
    let entries = [];
    let replacement = null;
    let scheduled = false;
    const live = () => {
      entries = entries.filter(({ fiber, hadParent }) => !hadParent || fiberAttached(fiber));
      return entries.map(({ fiber }) => fiber);
    };
    return {
      adopt: (elementType, wrapper) => {
        replacement = wrapper;
        const result = adoptMountedType(reactRootFibers(), elementType, wrapper, bound);
        entries = result.fibers.map((fiber) => ({ fiber, hadParent: fiberAttached(fiber) }));
        scheduled = result.scheduled;
        return result;
      },
      rerender: () => {
        let asked = 0;
        for (const fiber of live()) {
          invalidateFiberProps(fiber);
          if (requestRender(fiber)) asked++;
        }
        return asked;
      },
      release: (original) => {
        for (const { fiber } of entries) {
          if (fiber.type === replacement) retargetFiber(fiber, original);
        }
        entries = [];
        replacement = null;
        scheduled = false;
      },
      // `stale` is an adopted instance still drawing something other than the wrapper: a render
      // that has not happened yet, or a type React reset underneath us.
      status: () => {
        const fibers = live();
        return {
          adopted: fibers.length,
          scheduled,
          stale: fibers.filter((fiber) => fiber.type !== replacement).length,
        };
      },
    };
  };
  // Whether every token is in a function's source. The source is taken once per function: a walk
  // over a mounted tree meets the same few component types thousands of times.
  const sourceTexts = new WeakMap();
  const sourceMatches = (fn, tokens) => {
    if (typeof fn !== "function") return false;
    let source = sourceTexts.get(fn);
    if (source === undefined) {
      source = String(fn);
      sourceTexts.set(fn, source);
    }
    return tokens.every((token) => source.includes(token));
  };
  // The mounted instances of a component that has no public handle at all, kept by the fiber.
  //
  // Steam's main-menu popup host is such a component: a module-local function the popup mounts
  // directly under a React root, exported nowhere, with the menu's memo export absent from that
  // render path entirely. The only handle is the mounted fiber, which decky-loader's tabs hook adopts
  // the same way: each fiber whose source carries the tokens has its `type` swapped for the wrapper
  // `wrapFor` builds for its original. `adopt` walks only while no adopted host is still mounted, so
  // running it on every publication catches a host Steam recreated without paying a tree walk for
  // the one it did not.
  const createSourceAdoption = (tokens, wrapFor, bound = MaximumMountedNodes) => {
    // Each adopted fiber's original, the wrapper it was given and whether it had a parent when
    // adopted; see fiberAttached.
    const adopted = new Map();
    const prune = () => {
      for (const [fiber, { hadParent }] of [...adopted]) {
        if (hadParent && !fiberAttached(fiber)) adopted.delete(fiber);
      }
    };
    return {
      adopt: () => {
        prune();
        if (adopted.size > 0) return 0;
        let count = 0;
        walkFibers(reactRootFibers(), bound, (fiber) => {
          const type = fiber.type;
          if (adopted.has(fiber) || !sourceMatches(type, tokens)) return false;
          const wrapper = wrapFor(type);
          adopted.set(fiber, { original: type, wrapper, hadParent: fiberAttached(fiber) });
          retargetFiber(fiber, wrapper);
          invalidateFiberProps(fiber);
          requestRender(fiber);
          count++;
          return false;
        });
        return count;
      },
      release: () => {
        // Only a fiber still drawing our wrapper is handed back; a type something else set since
        // is not ours to overwrite.
        for (const [fiber, { original, wrapper }] of adopted) {
          if (fiber.type === wrapper) retargetFiber(fiber, original);
        }
        adopted.clear();
      },
      count: () => {
        prune();
        return adopted.size;
      },
    };
  };
  // Hands adopted instances back to the function the claim displaced. No render is requested: the
  // original draws again whenever the page next renders, and a wrapper left on screen until then
  // passes Steam's tree through once its gate is removed.
  const releaseMountedType = (roots, elementType, replacement, original, bound) => {
    let released = 0;
    for (const fiber of mountedFibersOf(roots, elementType, bound)) {
      if (fiber.type !== replacement) continue;
      retargetFiber(fiber, original);
      released++;
    }
    return released;
  };
  // @fragment icons.ts
  // Original 24×24 glyphs for native Steam rows; filled with currentColor and sized by the host.
  // Each row has a distinct shape so controller navigation retains recognizable landmarks.
  /** Named glyph shapes expressed as React SVG tags and attributes. */
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
    // Fast-forward chevrons for the processor boost row: the question is how hard the cores may
    // run past their base clock, which is a speed, not a power state or a kind of core.
    turbo: [["path", { d: "M3.5 5.2 11.6 12l-8.1 6.8V5.2Zm8.9 0L20.5 12l-8.1 6.8V5.2Z" }]],
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
    // -- Audio ----------------------------------------------------------------------------------
    audio: [["path", { d: "M3 12a9 9 0 0 1 18 0v7h-4v-8h2a7 7 0 0 0-14 0h2v8H3Z" }]],
    audioChannels: [
      [
        "path",
        {
          d: "M2 4h7v16H2ZM15 4h7v16h-7ZM3.5 14a2 2 0 1 0 4 0 2 2 0 1 0-4 0ZM16.5 14a2 2 0 1 0 4 0 2 2 0 1 0-4 0Z",
          fillRule: "evenodd",
        },
      ],
    ],
    audioEncoding: [
      ["rect", { x: 2, y: 7, width: 3, height: 10 }],
      ["rect", { x: 7, y: 3, width: 3, height: 18 }],
      ["rect", { x: 12, y: 9, width: 3, height: 6 }],
      ["rect", { x: 17, y: 5, width: 3, height: 14 }],
    ],
    audioSpatial: [
      ["circle", { cx: 12, cy: 12, r: 3 }],
      ["path", { d: "M2 2h6v2H4v4H2ZM16 2h6v6h-2V4h-4ZM2 16h2v4h4v2H2ZM20 16h2v6h-6v-2h4Z" }],
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
    // -- Quick Access tabs ----------------------------------------------------------------------
    // The Extensions tab: a puzzle piece, the shape that means "something added in". Its own
    // drawing rather than the power plug it used to borrow from the "When plugged in" row.
    extensions: [
      [
        "path",
        {
          d:
            "M9 3.5a2.5 2.5 0 0 1 5 0V5h4a1 1 0 0 1 1 1v4h-1.5a2.5 2.5 0 0 0 0 5H19v4a1 1 0 0 1-1 1h-4v-1.5" +
            "a2.5 2.5 0 0 0-5 0V20H5a1 1 0 0 1-1-1v-4h1.5a2.5 2.5 0 0 0 0-5H4V6a1 1 0 0 1 1-1h4V3.5Z",
        },
      ],
    ],
    // -- Extensions tab section headers ---------------------------------------------------------
    // The pair a collapsible section's header shows: pointing down while the section is open,
    // right while it is folded. Two states of one control, so they are the one place a shape
    // repeats, and they are drawn nowhere else.
    sectionOpen: [["path", { d: "M12 16.4 4.6 9l1.8-1.8L12 12.8l5.6-5.6L19.4 9 12 16.4Z" }]],
    sectionClosed: [["path", { d: "M9 4.6 16.4 12 9 19.4 7.2 17.6l5.6-5.6-5.6-5.6L9 4.6Z" }]],
  });
  /**
   * Creates a decorative icon renderer with a cache owned by the supplied React runtime.
   * @param react Steam's React runtime; elements must be rendered by this same instance.
   * @returns A (name, size = 20) renderer; unknown icon names return null.
   */
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
  // Only SVG path commands and numbers are accepted; publications cannot inject markup.
  const SteamGlyphPattern = /^[MmLlHhVvCcSsQqTtAaZz0-9.,\-\s]+$/u;
  const steamGlyphCaches = new WeakMap();
  /**
   * Renders host-supplied SVG path data as a decorative current-color glyph.
   * @param react Steam's React runtime, which owns cached elements.
   * @param d SVG path commands and numbers on a 24×24 grid; markup is rejected.
   * @returns A cached or new SVG element, or null when the path fails validation.
   */
  const renderSteamGlyph = (react, d) => {
    if (typeof d !== "string" || !SteamGlyphPattern.test(d)) return null;
    let steamGlyphCache = steamGlyphCaches.get(react);
    if (!steamGlyphCache) {
      steamGlyphCache = new Map();
      steamGlyphCaches.set(react, steamGlyphCache);
    }
    const cached = steamGlyphCache.get(d);
    if (cached) return cached;
    const element = react.createElement(
      "svg",
      {
        xmlns: "http://www.w3.org/2000/svg",
        viewBox: "0 0 24 24",
        fill: "none",
        "aria-hidden": true,
        focusable: false,
      },
      react.createElement("path", { d, fill: "currentColor", fillRule: "evenodd" }),
    );
    if (steamGlyphCache.size < 32) steamGlyphCache.set(d, element);
    return element;
  };
  // @fragment library-capsule.ts
  // Native library styling for host-owned titles that have no Steam app overview.
  const SteamLibraryClassNames = [
    "LibraryItemBox",
    "Portrait",
    "Landscape",
    "PortraitImage",
    "LibraryItemBoxShine",
    "LibraryItemOverlayOuterArea",
    "LibraryItemOverlayInnerArea",
  ];
  /**
   * Resolves all native CSS classes needed for a host-rendered library capsule.
   * @param runtime Shared resolver; loading a matched factory may throw.
   * @returns Mapped capsule classes, or null when the complete class contract is unavailable.
   */
  const resolveSteamLibraryClasses = (runtime) => {
    const factory = runtime.findUnique([...SteamLibraryClassTokens]);
    if (!factory) return null;
    const map = classMapOf(runtime(factory[0]));
    if (!map) return null;
    for (const name of SteamLibraryClassNames) {
      if (typeof map[name] !== "string" || !map[name]) return null;
    }
    return {
      box: map.LibraryItemBox,
      portrait: map.Portrait,
      landscape: map.Landscape,
      image: map.PortraitImage,
      shine: map.LibraryItemBoxShine,
      overlayOuter: map.LibraryItemOverlayOuterArea,
      overlayInner: map.LibraryItemOverlayInnerArea,
    };
  };
  // The shape of each Steam artwork type, as a CSS aspect ratio.
  const SteamCapsuleAspects = {
    grid: "2 / 3",
    wide: "460 / 215",
    hero: "1920 / 620",
    logo: "16 / 9",
    icon: "1 / 1",
  };
  /**
   * Creates a reusable capsule component; retain its identity across renders to preserve focus.
   * @param ui Steam's resolved React and native control components.
   * @param classes Complete class map from resolveSteamLibraryClasses.
   * @returns A component accepting asset/image/placeholder/width/dimmed/overlay/caption/focus props.
   */
  const createSteamCapsule = (ui, classes) => {
    const react = ui.react;
    return function SteamCapsule(props) {
      const asset = props.asset ?? "grid";
      const portrait = asset === "grid";
      const boxClass = `${classes.box} ${portrait ? classes.portrait : classes.landscape}`;
      const children = [];
      if (props.image) {
        children.push(
          react.createElement("img", {
            key: "image",
            className: classes.image,
            src: props.image,
            loading: "lazy",
            draggable: false,
            style: {
              width: "100%",
              height: "100%",
              objectFit: asset === "logo" || asset === "icon" ? "contain" : "cover",
              display: "block",
            },
          }),
        );
      } else {
        children.push(
          react.createElement(
            "div",
            {
              key: "placeholder",
              style: {
                position: "absolute",
                inset: 0,
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                padding: "10px",
                textAlign: "center",
                fontSize: "14px",
                opacity: 0.7,
              },
            },
            props.placeholder ?? "",
          ),
        );
      }
      if (portrait) {
        children.push(
          react.createElement("div", {
            key: "shine",
            className: `${classes.shine} ${classes.portrait}`,
          }),
        );
      }
      children.push(
        react.createElement(
          "div",
          { key: "overlay", className: classes.overlayOuter },
          react.createElement("div", { className: classes.overlayInner }, props.overlay ?? null),
        ),
      );
      if (props.caption) {
        children.push(
          react.createElement(
            "div",
            {
              key: "caption",
              style: {
                position: "absolute",
                left: 0,
                right: 0,
                bottom: 0,
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                gap: "8px",
                padding: "4px 6px",
                fontSize: "12px",
                background: "rgba(14, 20, 27, 0.82)",
                pointerEvents: "none",
              },
            },
            props.caption,
          ),
        );
      }
      return react.createElement(
        ui.focusable,
        {
          ...(props.focus ?? {}),
          className: boxClass,
          style: {
            position: "relative",
            width: `${props.width ?? 160}px`,
            aspectRatio: SteamCapsuleAspects[asset] ?? SteamCapsuleAspects.grid,
            height: "auto",
            overflow: "hidden",
            opacity: props.dimmed ? 0.5 : 1,
            background: props.image ? undefined : "rgba(255, 255, 255, 0.06)",
          },
        },
        ...children,
      );
    };
  };
  // @fragment module-resolver.ts
  /**
   * Captures Steam's webpack runtime and exposes unique source-fingerprint resolution.
   * @param scope Diagnostic chunk-label suffix for this resolver capture.
   * @returns A checked module reader with count/findUnique/resolve/exported helpers.
   * @throws When the runtime is absent, a fingerprint is invalid or a requested match/load fails.
   */
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
    // Standalone probes and the bridge inspect the same runtime. Repeating every fingerprint's
    // text search in each probe stalls Steam's UI thread during a synchronization pass.
    const cacheKey = Symbol.for("SteamUiToolkit.moduleResolverCache.v1");
    let caches = Reflect.get(window, cacheKey);
    if (!caches) {
      caches = new WeakMap();
      Object.defineProperty(window, cacheKey, { value: caches, configurable: true });
    }
    let cache = caches.get(runtime);
    if (!cache) {
      cache = { sources: new WeakMap(), ids: [], factories: [], matches: new Map() };
      caches.set(runtime, cache);
    }
    // A factory's source is stable by function identity, including across separate evaluations.
    const sources = cache.sources;
    const sourceOf = (factory) => {
      let source = sources.get(factory);
      if (source === undefined) {
        source = Function.prototype.toString.call(factory);
        sources.set(factory, source);
      }
      return source;
    };
    // No memory of a failure. Steam's loader records a module before running its factory and never
    // re-runs one that threw, so a later call returns whatever exports that factory set, the object
    // Steam's own code now uses, and the shape tests below accept or refuse it. Remembering the
    // failure made a module that threw once during a cold start unusable for the bridge's life.
    const requirePresent = (id) => {
      if (typeof id !== "string" || typeof runtime.m[id] !== "function")
        throw new Error(`Steam module absent: ${id}`);
      try {
        return runtime(id);
      } catch (error) {
        throw new Error(`Steam module resolution failed: ${id}: ${String(error)}`);
      }
    };
    const matches = (tokens) => {
      if (
        !Array.isArray(tokens) ||
        tokens.length < 1 ||
        !tokens.every((token) => typeof token === "string" && token.length > 0)
      )
        throw new Error("Steam module fingerprint invalid");
      // Steam registers chunks after startup and can replace a factory without changing the
      // module count. Check every id and factory before trusting a cached unique match.
      const ids = Object.keys(runtime.m);
      if (
        ids.length !== cache.ids.length ||
        !ids.every(
          (id, index) => id === cache.ids[index] && runtime.m[id] === cache.factories[index],
        )
      ) {
        cache.ids = ids;
        cache.factories = ids.map((id) => runtime.m[id]);
        cache.matches.clear();
      }
      const key = JSON.stringify(tokens);
      const remembered = cache.matches.get(key);
      if (remembered) return remembered;
      const found = ids.filter((id) => {
        const factory = runtime.m[id];
        if (typeof factory !== "function") return false;
        const source = sourceOf(factory);
        return tokens.every((token) => source.includes(token));
      });
      cache.matches.set(key, found);
      return found;
    };
    /** Counts matching factories without executing them; invalid tokens throw. */
    requirePresent.count = (tokens) => matches(tokens).length;
    /** Returns the unique factory id/source pair, or null on absence or ambiguity. */
    requirePresent.findUnique = (tokens) => {
      const ids = matches(tokens);
      return ids.length === 1 ? [ids[0], sourceOf(runtime.m[ids[0]])] : null;
    };
    /** Loads one uniquely matched module; invalid, absent, ambiguous or failed loads throw. */
    requirePresent.resolve = (tokens) => {
      const ids = matches(tokens);
      if (ids.length !== 1)
        throw new Error(
          `Steam module ${ids.length ? "ambiguous" : "absent"}: ${tokens.join(", ")}`,
        );
      return requirePresent(ids[0]);
    };
    /**
     * Selects one distinct export of a uniquely fingerprinted module.
     * @param tokens Nonempty source tokens that must all match one factory.
     * @param predicate Shape predicate; throwing getters and predicates are ignored.
     * @returns The unique matching value; aliases count once. Absence or ambiguity throws.
     */
    requirePresent.exported = (tokens, predicate) => {
      if (typeof predicate !== "function") throw new Error("Steam export predicate invalid");
      const exports = requirePresent.resolve(tokens);
      const fits = new Set();
      for (const name of Object.keys(exports ?? {})) {
        try {
          const value = exports[name];
          if (predicate(value)) fits.add(value);
        } catch {
          // An export whose getter or shape test throws is not the one being looked for.
        }
      }
      if (fits.size !== 1)
        throw new Error(`Steam export ${fits.size ? "ambiguous" : "absent"}: ${tokens.join(", ")}`);
      return [...fits][0];
    };
    // Storage's installed transport module exports a zero-argument function whose entire body
    // returns its closed-over singleton. It has no author tokens of its own. Keep this strict
    // structural selection here so the standalone probe and the gate inspect the same shape.
    // Only the already uniquely fingerprinted module is loaded; no provider accessor is called.
    requirePresent.storageProvider = () => {
      const ids = matches(["GetDefaultTransport", "m_transport"]);
      if (ids.length !== 1) return { transportModule: ids.length, provider: 0, accessor: null };
      const exports = requirePresent(ids[0]);
      const fits = new Set();
      const accessorSource =
        /^function\s+[A-Za-z_$][\w$]*\s*\(\s*\)\s*\{\s*return[ \t]+[A-Za-z_$][\w$]*\s*;?\s*\}$/;
      const bindingSource = /^\(\s*\)\s*=>\s*[A-Za-z_$][\w$]*\s*$/;
      for (const descriptor of Object.values(Object.getOwnPropertyDescriptors(exports ?? {}))) {
        let value;
        if (Object.prototype.hasOwnProperty.call(descriptor, "value")) {
          value = descriptor.value;
        } else {
          // Webpack's export bindings are getters. Read only the evidenced pure identifier
          // return, never a getter that calls something or performs other work.
          if (typeof descriptor.get !== "function" || !bindingSource.test(sourceOf(descriptor.get)))
            continue;
          value = Reflect.apply(descriptor.get, exports, []);
        }
        if (typeof value === "function" && accessorSource.test(sourceOf(value))) fits.add(value);
      }
      return {
        transportModule: ids.length,
        provider: fits.size,
        accessor: fits.size === 1 ? [...fits][0] : null,
      };
    };
    return requirePresent;
  }
  // @steam-ui-module-resolver-end
  // @fragment page-gate.ts
  /**
   * Registers a renderer and generation-bound gate for one host page.
   * @param definition Stable renderer/gate/patch identities, component requirements and lifecycle callbacks.
   * @returns Live page accessors; removal clears UI and state while the React instance may remain available.
   */
  function registerSteamPage(definition) {
    let installed = false;
    let ui = null;
    let react = null;
    let state = null;
    let refusal = null;
    let lastError = "";
    let unsubscribe = null;
    let unsubscribeRefusal = null;
    const listeners = new Set();
    const notify = () => {
      for (const listener of [...listeners]) {
        try {
          listener();
        } catch {}
      }
    };
    const context = {
      react: () => react,
      ui: () => ui,
      state: () => state,
      refusal: () => refusal,
    };
    const resolve = () => {
      const runtime = getWebpackRuntime(definition.template);
      const resolved = definition.components(runtime);
      const missing = definition.required.filter((name) => !resolved?.[name]);
      if (missing.length) {
        lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
        return false;
      }
      const refused = definition.prepare?.(resolved, runtime) ?? null;
      if (refused) {
        lastError = refused;
        return false;
      }
      ui = resolved;
      react = resolved.react;
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      if (!attemptResolution(resolve, (error) => (lastError = String(error)))) {
        ui = null;
        notify();
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(definition.patchId, (next) => {
        state = next;
        notify();
      });
      unsubscribeRefusal = subscribeRefusal(definition.patchId, (reason) => {
        refusal = reason;
        notify();
      });
      notify();
      return { ok: true, installed: true };
    };
    // A mounted page draws nothing from now on, rather than the controls it last had.
    const remove = () => {
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      unsubscribeRefusal = endSubscription(unsubscribeRefusal);
      state = null;
      refusal = null;
      ui = null;
      definition.release?.();
      notify();
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!ui,
      subscribed: !!unsubscribe,
      refused: refusal,
      lastError,
      ...(definition.status?.() ?? {}),
    });
    // One component for the life of the asset. The page host draws it on every router render, and a
    // component declared inside the renderer would be a new type each time: React would remount the
    // page and drop its state and the controller's focus.
    function SteamPageFrame(props) {
      const [, setRevision] = react.useState(0);
      react.useEffect(() => {
        const listener = () => setRevision((value) => value + 1);
        listeners.add(listener);
        return () => listeners.delete(listener);
      }, []);
      // After the hooks, so a render that finds the gate removed calls the same ones.
      if (!ui) {
        return react.createElement(
          "div",
          {
            role: "status",
            style: {
              marginTop: "var(--basicui-header-height, 40px)",
              padding: "24px 48px",
              opacity: 0.8,
            },
          },
          lastError || "Loading…",
        );
      }
      // The root must take focus so Back reaches page navigation before the router stack.
      return ui.focusable
        ? react.createElement(
            ui.focusable,
            { className: "steam-ui-page-root", autoFocus: true, style: { height: "100%" } },
            react.createElement(definition.Page, { context, page: props.page }),
          )
        : react.createElement(definition.Page, { context, page: props.page });
    }
    // React comes from the page host before the gate has supplied it; Steam has one.
    registerSteamPageRenderer(definition.template, (hostReact, page) => {
      react ??= hostReact;
      return react.createElement(SteamPageFrame, { page });
    });
    registerGate(definition.gate, { install, remove, status });
    return context;
  }
  // @fragment plugin-frontends.ts
  // @fragment plugin-frontends
  const pluginFrontendEntries = new Map();
  const pluginFrontendSlots = new Map();
  const pluginFrontendListeners = new Set();
  const pluginFrontendChanged = () => {
    for (const listener of pluginFrontendListeners) listener();
  };
  /**
   * Observes contribution changes in this bridge generation.
   * @param listener Callback invoked after registrations change.
   * @returns An unsubscribe callback; there is no cached replay.
   */
  const subscribePluginFrontends = (listener) => {
    pluginFrontendListeners.add(listener);
    return () => pluginFrontendListeners.delete(listener);
  };
  /**
   * Reads slots for currently active frontend contributions.
   * @param kind Contribution category, such as menu or tab.
   * @returns A new array excluding failed or closed owners.
   */
  const pluginFrontendItems = (kind) =>
    [...pluginFrontendSlots.values()].filter(
      (slot) => slot.kind === kind && !slot.entry.failed && !slot.entry.closed,
    );
  /**
   * Renders currently active frontend contributions of one kind.
   * @param kind Contribution category.
   * @param react Steam's React runtime.
   * @param props Additional props supplied by the containing surface.
   * @returns An array of owner-bound React elements.
   */
  const pluginFrontendElements = (kind, react, props = {}) =>
    pluginFrontendItems(kind).map((slot) => slot.element(react, props));
  /**
   * Owns trusted plugin frontend scripts, contributions and failure cleanup for this bridge.
   * @returns Probe/load/status/unload controls; unloading awaits owned cleanup, while remove starts all unloads without awaiting.
   */
  function createPluginFrontends() {
    const documents = () => {
      const docs = new Set([document]);
      for (const popup of window.g_PopupManager?.GetPopups?.() ?? []) {
        if (popup?.m_popup?.document) docs.add(popup.m_popup.document);
      }
      return [...docs];
    };
    const reactRuntime = () => resolveReact(getWebpackRuntime("plugin-frontends"));
    const report = (entry, module, error) => {
      if (entry.failed || entry.closed) return;
      const reason = String(error?.stack ?? error).slice(0, 4096);
      // Shut down every sibling synchronously before reporting to the host.
      for (const sibling of pluginFrontendEntries.values()) {
        if (sibling.owner !== entry.owner) continue;
        sibling.failed = `${module}: ${reason}`;
        void cleanup(sibling);
      }
      void request(entry.id, "failure", { module, reason }).catch(() => {});
    };
    const guard = (entry, module, callback) =>
      function (...args) {
        if (entry.failed || entry.closed) return;
        try {
          const result = callback.apply(this, args);
          if (result && typeof result.then === "function") {
            return Promise.resolve(result).catch((error) => {
              report(entry, module, error);
            });
          }
          return result;
        } catch (error) {
          report(entry, module, error);
          return undefined;
        }
      };
    const cleanup = async (entry) => {
      entry.stopDocuments?.();
      entry.stopDocuments = null;
      for (const [key, slot] of pluginFrontendSlots)
        if (slot.entry === entry) pluginFrontendSlots.delete(key);
      pluginFrontendChanged();
      let failure = "";
      while (entry.cleanups.length) {
        try {
          const work = entry.cleanups.pop()();
          if (work && typeof work.then === "function") {
            let timer;
            try {
              await Promise.race([
                work,
                new Promise((_, reject) => {
                  timer = setTimeout(() => reject(new Error("module teardown timed out")), 2000);
                }),
              ]);
            } finally {
              clearTimeout(timer);
            }
          }
        } catch (error) {
          failure ||= String(error);
        }
      }
      if (failure) entry.cleanupFailure = failure;
    };
    const boundary = (react, entry, module, render) => {
      class ModuleBoundary extends react.Component {
        state = { failed: false };
        stop;
        componentDidMount() {
          this.stop = subscribePluginFrontends(guard(entry, module, () => this.forceUpdate()));
        }
        componentWillUnmount() {
          this.stop?.();
        }
        static getDerivedStateFromError() {
          return { failed: true };
        }
        componentDidCatch(error) {
          report(entry, module, error);
        }
        render() {
          return this.state.failed || entry.failed || entry.closed ? null : this.props.children;
        }
      }
      const Render = guard(entry, module, (props) => render(react, props));
      return (props) =>
        react.createElement(ModuleBoundary, null, react.createElement(Render, props));
    };
    const ready = async (entry, module, check) => {
      const until = Date.now() + 5000;
      let last;
      while (!entry.closed && !entry.failed) {
        last = check();
        if (last === true || last?.ok === true) return;
        if (Date.now() >= until)
          throw new Error(`${module}: readiness failed: ${last?.error ?? "gate never ready"}`);
        await new Promise((resolve) => setTimeout(resolve, 50));
      }
      throw new Error(`${module}: module stopped during readiness`);
    };
    const load = async (spec) => {
      const prior = pluginFrontendEntries.get(spec.id);
      if (prior) return status(spec.id);
      const entry = {
        ...spec,
        failed: "",
        closed: false,
        cleanups: [],
        pending: [],
        verifiers: [],
        contributionIds: new Set(),
        styles: new Set(),
        ready: false,
      };
      pluginFrontendEntries.set(spec.id, entry);
      const sourceUrl = `steam-ui-plugin://${encodeURIComponent(spec.owner)}/${encodeURIComponent(spec.module)}.js`;
      const knownWindows = new Set();
      const asyncError = (event) => {
        const origin = `${event.filename ?? ""}\n${event.error?.stack ?? ""}\n${event.reason?.stack ?? ""}`;
        if (!origin.includes(sourceUrl)) return;
        event.preventDefault?.();
        report(entry, spec.module, event.error ?? event.reason ?? event.message);
      };
      const refreshDocuments = () => {
        if (entry.closed || entry.failed) return;
        for (const doc of documents()) {
          const target = doc.defaultView;
          if (target && !knownWindows.has(target)) {
            knownWindows.add(target);
            target.addEventListener("error", asyncError);
            target.addEventListener("unhandledrejection", asyncError);
          }
          if (entry.style && ![...entry.styles].some((style) => style.ownerDocument === doc)) {
            const style = doc.createElement("style");
            style.dataset.steamUiPlugin = spec.id;
            style.textContent = entry.style;
            (doc.head ?? doc.documentElement).appendChild(style);
            entry.styles.add(style);
          }
        }
      };
      refreshDocuments();
      const interval = setInterval(refreshDocuments, 500);
      entry.stopDocuments = () => {
        clearInterval(interval);
        for (const target of knownWindows) {
          target.removeEventListener("error", asyncError);
          target.removeEventListener("unhandledrejection", asyncError);
        }
        for (const style of entry.styles) style.remove();
        entry.styles.clear();
      };
      const register = (kind, id, value, render, gate, gateKind) => {
        const module = `${spec.module}/${id}`;
        const key = `${spec.id}/${id}`;
        if (!id || entry.contributionIds.has(id))
          throw new Error(`Invalid or duplicate module: ${module}`);
        entry.contributionIds.add(id);
        if (kind === "page" && (typeof value?.path !== "string" || !value.path.startsWith("/")))
          throw new Error(`${module}: page path must be absolute`);
        if (render != null && typeof render !== "function")
          throw new Error(`${module}: renderer must be a function`);
        const work = (async () => {
          await ready(entry, module, () => bridge.gate(gate)?.install(gateKind));
          const react = reactRuntime();
          if (!react) throw new Error("Steam React runtime unavailable");
          const Component = render ? boundary(react, entry, module, render) : null;
          const slot = {
            kind,
            id: key,
            entry,
            value: { ...value, id: key },
            element: (_react, props) =>
              Component ? react.createElement(Component, { ...props, key }) : null,
          };
          pluginFrontendSlots.set(key, slot);
          entry.verifiers.push({
            module,
            check: () => {
              const state = bridge.gate(gate)?.status(gateKind);
              return state?.installed === true || state?.registered === true;
            },
          });
          if (kind === "page") {
            steamPageRenderers.set(key, (_react, page) => slot.element(react, { page }));
            entry.cleanups.push(() => steamPageRenderers.delete(key));
          }
          entry.cleanups.push(() => {
            pluginFrontendSlots.delete(key);
            pluginFrontendChanged();
          });
          pluginFrontendChanged();
          return () => {
            pluginFrontendSlots.delete(key);
            pluginFrontendChanged();
          };
        })().catch((error) => {
          report(entry, module, error);
        });
        entry.pending.push(work);
        return work;
      };
      const api = {
        /**
         * Owner module identity used for backend commands and subscriptions.
         */
        id: spec.id,
        /**
         * Diagnostic module name used in frontend failure reports.
         */
        module: spec.module,
        /**
         * Steam's existing React runtime; frontends must not load a second copy.
         */
        react: reactRuntime(),
        /**
         * Shared generation-bound bridge; trusted frontend code is not sandboxed.
         */
        bridge,
        /**
         * Returns the shared module resolver; unavailable Steam runtime throws.
         */
        resolveModules: getWebpackRuntime,
        /**
         * Resolves native settings controls; returns null when required evidence is absent.
         */
        resolveComponents: () =>
          resolveSteamSettingsComponents(getWebpackRuntime("plugin-frontends")),
        /**
         * Native section, header and empty-state helpers for contributed surfaces.
         */
        uiKit: {
          section: renderSteamUiGroup,
          header: renderSteamUiHeader,
          note: renderSteamUiEmpty,
        },
        /**
         * Wraps a callback so synchronous throws and rejected promises fail the owning plugin.
         * @param callback Callback to invoke with the original receiver and arguments.
         * @returns A guarded callback; failure triggers cleanup of this owner and sibling modules.
         */
        guard: (callback) => guard(entry, spec.module, callback),
        /**
         * Registers owner cleanup, invoked in reverse order during unload or failure.
         * @param callback Cleanup callback; an asynchronous result receives a two-second wait budget.
         */
        onDispose: (callback) => entry.cleanups.push(callback),
        /**
         * Invokes an allowlisted backend method through this module.
         * @param method Semantic backend method name.
         * @param payload JSON arguments; defaults to null.
         * @returns The bridge result promise; refusal and timeout reject.
         */
        call: (method, payload = null) => request(spec.id, "invoke", { method, payload }),
        /**
         * Observes backend state with cached replay and owner failure handling.
         * @param callback Receives complete state publications.
         * @returns An unsubscribe callback, also retained for automatic owner cleanup.
         */
        subscribe: (callback) => {
          const stop = subscribe(spec.id, guard(entry, spec.module, callback));
          entry.cleanups.push(stop);
          return stop;
        },
        /**
         * Waits for a frontend readiness predicate, checking every 50 ms for up to five seconds.
         * @param check Returns true or an object with ok:true when ready.
         * @returns A promise rejecting on timeout, predicate failure or owner shutdown.
         */
        ready: (check) => ready(entry, spec.module, check),
        /**
         * Contributes a routed page; duplicate ids and nonabsolute paths are rejected.
         * @param id Contribution id unique within this module.
         * @param page Page descriptor with an absolute path.
         * @param render Renderer wrapped in the owning plugin error boundary.
         * @returns A registration promise; asynchronous failure marks the owner failed and starts cleanup.
         */
        registerPage: (id, page, render) => register("page", id, page, render, "pages"),
        /**
         * Contributes a native navigation row, with an optional guarded activation callback.
         * @param id Contribution id unique within this module.
         * @param item Menu descriptor and optional onActivate callback.
         * @returns A registration promise; owner status reports asynchronous failure.
         */
        registerMenuEntry: (id, item) =>
          register(
            "menu",
            id,
            {
              ...item,
              frontendAction: item.onActivate
                ? guard(entry, `${spec.module}/${id}`, item.onActivate)
                : undefined,
            },
            null,
            "navigationPanel",
          ),
        /**
         * Contributes a rendered Quick Access tab.
         * @param id Contribution id unique within this module.
         * @param tab Tab descriptor, including its title.
         * @param render Renderer wrapped in the owning plugin error boundary.
         * @returns A registration promise; owner status reports asynchronous failure.
         */
        registerQuickAccessTab: (id, tab, render) =>
          register("tab", id, tab, render, "extensionsTab"),
        /**
         * Contributes a row through the native component host.
         * @param id Contribution id unique within this module.
         * @param placement quickSettings selects Quick Settings; every other value selects Performance.
         * @param render Renderer wrapped in the owning plugin error boundary.
         * @returns A registration promise; owner status reports asynchronous failure.
         */
        registerQuickAccessRow: (id, placement, render) =>
          register(
            placement === "quickSettings" ? "quickSettings" : "perf",
            id,
            {},
            render,
            "nativeComponents",
            "settingsSections",
          ),
        /**
         * Contributes a library-card addition receiving the current surface props.
         * @param id Contribution id unique within this module.
         * @param render Renderer wrapped in the owning plugin error boundary.
         * @returns A registration promise; owner status reports asynchronous failure.
         */
        registerLibraryAddition: (id, render) =>
          register("library", id, {}, render, "libraryBadge"),
        /**
         * Contributes a game-details addition receiving the current surface props.
         * @param id Contribution id unique within this module.
         * @param render Renderer wrapped in the owning plugin error boundary.
         * @returns A registration promise; owner status reports asynchronous failure.
         */
        registerGamePageAddition: (id, render) =>
          register("gamePage", id, {}, render, "libraryDetails"),
        /**
         * Appends owner CSS to the main and reachable popup documents; unload removes the owned nodes.
         * @param css CSS text evaluated with the same trust as the frontend script.
         */
        addStyle: (css) => {
          entry.style = `${entry.style ?? ""}\n${css}`;
          for (const style of entry.styles) style.textContent = entry.style;
          refreshDocuments();
        },
        /**
         * Probes, applies and verifies a plugin-owned patch, retaining its remover for cleanup.
         * @param id Diagnostic patch suffix within this module.
         * @param patch Object providing probe, apply, verify and remove callbacks.
         * @returns A promise; asynchronous failure marks the owner failed rather than escaping.
         */
        registerPatch: (id, patch) => {
          const module = `${spec.module}/${id}`;
          const work = (async () => {
            await ready(entry, module, () => patch.probe());
            entry.cleanups.push(() => patch.remove());
            await patch.apply();
            await ready(entry, module, () => patch.verify());
            entry.verifiers.push({ module, check: () => patch.verify() });
          })().catch((error) => report(entry, module, error));
          entry.pending.push(work);
          return work;
        },
        /**
         * Schedules an owner-guarded callback and cancels it on cleanup.
         * @param callback Callback whose failure fails the owner.
         * @param milliseconds Delay passed to the browser timer.
         * @returns The browser timeout handle.
         */
        setTimeout: (callback, milliseconds) => {
          const timer = setTimeout(guard(entry, spec.module, callback), milliseconds);
          entry.cleanups.push(() => clearTimeout(timer));
          return timer;
        },
        /**
         * Schedules a repeating owner-guarded callback and cancels it on cleanup.
         * @param callback Callback whose failure fails the owner.
         * @param milliseconds Interval passed to the browser timer.
         * @returns The browser interval handle.
         */
        setInterval: (callback, milliseconds) => {
          const timer = setInterval(guard(entry, spec.module, callback), milliseconds);
          entry.cleanups.push(() => clearInterval(timer));
          return timer;
        },
        /**
         * Registers a guarded event listener and removes the same callback/options on cleanup.
         * @param target EventTarget receiving the listener.
         * @param type Browser event name.
         * @param callback Listener whose failure fails the owner.
         * @param options Listener options reused when removing it.
         */
        addEventListener: (target, type, callback, options) => {
          const wrapped = guard(entry, spec.module, callback);
          target.addEventListener(type, wrapped, options);
          entry.cleanups.push(() => target.removeEventListener(type, wrapped, options));
        },
      };
      try {
        const execute = new Function("api", `${spec.script}\n//# sourceURL=${sourceUrl}`);
        const dispose = await execute(api);
        if (typeof dispose === "function") entry.cleanups.push(dispose);
        await Promise.all(entry.pending);
        if (entry.failed || entry.closed) {
          await cleanup(entry);
          return status(spec.id);
        }
        entry.ready = true;
        return { ok: true };
      } catch (error) {
        report(entry, spec.module, error);
        return status(spec.id);
      }
    };
    const status = async (id) => {
      const entry = pluginFrontendEntries.get(id);
      if (entry?.ready && !entry.failed && !entry.closed) {
        for (const verifier of entry.verifiers) {
          try {
            const result = await verifier.check();
            if (result !== true && result?.ok !== true) {
              report(entry, verifier.module, "module no longer passes verification");
              break;
            }
          } catch (error) {
            report(entry, verifier.module, error);
            break;
          }
        }
      }
      return {
        ok: !!entry?.ready && !entry.failed && !entry.closed,
        error: entry?.failed || (entry ? "frontend not ready" : "frontend absent"),
      };
    };
    const unload = async (id) => {
      const entry = pluginFrontendEntries.get(id);
      if (!entry) return { ok: true };
      entry.closed = true;
      await cleanup(entry);
      if (entry.cleanupFailure) return { ok: false, error: entry.cleanupFailure };
      pluginFrontendEntries.delete(id);
      return { ok: true };
    };
    return {
      probe: () => ({ ok: !!reactRuntime() }),
      load,
      status,
      unload,
      remove: () => {
        for (const id of pluginFrontendEntries.keys()) void unload(id);
        return { ok: true };
      },
    };
  }
  registerGate("pluginFrontends", createPluginFrontends());
  // @fragment settings.ts
  // Host settings use Steam's native routed sidebar and fields. Missing component contracts refuse
  // installation; descriptors and command policy belong to the host.
  const SteamRoutedPagesTokens = ["disableRouteReporting"];
  // The generic confirm modal, by three props only its module names together.
  const SteamConfirmModalTokens = ["strMiddleButtonText", "bProgressDialog", "bAlertDialog"];
  /**
   * Resolves native Settings controls using unique authored-source and export-shape matches.
   * @param runtime The shared module resolver; matched factory loads can throw.
   * @returns The component collection, or null when required module evidence is absent.
   */
  const resolveSteamSettingsComponents = (runtime) => {
    const ui = resolveSteamUiComponents(runtime);
    const fieldsFactory = runtime.findUnique(FieldTokens);
    if (!ui || !fieldsFactory) return null;
    const fields = runtime(fieldsFactory[0]);
    const settingsSection = uniqueSteamExport(fields, (value) =>
      sourceOfSteamComponent(value).includes('"DialogSettingsSection"'),
    );
    // The name/value field sets `inlineWrap` to "shift-children-below" and makes the row focusable.
    // The field layout it draws through reads the same props, but takes a second argument beside
    // them; a component takes its props alone.
    const valueField = uniqueSteamExport(fields, (value) => {
      if (typeof value !== "function" || value.length !== 1) return false;
      const source = String(value);
      return ["inlineWrap", '"shift-children-below"', "focusable"].every((token) =>
        source.includes(token),
      );
    });
    const smallButton = uniqueSteamExport(fields, (value) => isSteamDialogButton(value, "Small"));
    const routedPages = optionalSteamExport(
      runtime,
      [...SteamRoutedPagesTokens],
      (value) =>
        typeof value === "function" &&
        String(value).includes("disableRouteReporting") &&
        String(value).includes("pages"),
    );
    const confirmModal = optionalSteamExport(runtime, [...SteamConfirmModalTokens], (value) => {
      const source = sourceOfSteamComponent(value);
      return SteamConfirmModalTokens.every((token) => source.includes(token));
    });
    return { ...ui, settingsSection, valueField, smallButton, routedPages, confirmModal };
  };
  /**
   * Component names required to draw every supported host Settings row.
   */
  const SteamSettingsRequired = [
    "react",
    "focusable",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "dialogButton",
    "smallButton",
    "valueField",
    "settingsSection",
    "routedPages",
    "confirmModal",
    "showModal",
  ];
  // Asks before a change the host marked as needing it, in Steam's own confirm modal. Cancelling
  // sends nothing, so the row keeps showing what the host last published.
  const confirmSteamSetting = (ui, confirmation, proceed) => {
    const h = ui.react.createElement;
    ui.showModal(
      h(ui.confirmModal, {
        strTitle: confirmation.title,
        strDescription: confirmation.description,
        strOKButtonText: confirmation.confirmLabel,
        bDestructiveWarning: confirmation.destructive !== false,
        onOK: proceed,
        onCancel: () => {},
      }),
      window,
      { strTitle: confirmation.title },
    );
  };
  // A colour as hue, saturation, lightness and alpha, read from the hex, rgb(a) and hsl(a) forms a
  // theme's colour takes (comma or space separated), and written back as hsla() the way CSSLoader's
  // colour picker writes it. Anything else, a named colour or a variable, answers null, and the row
  // keeps it as editable text rather than opening it as some other colour.
  const clampSteamColorPart = (value, max) => Math.min(max, Math.max(0, value));
  // An alpha written as a fraction or as a percentage, or 1 when the colour gives none.
  const steamColorAlpha = (value, percent) =>
    value === undefined ? 1 : clampSteamColorPart(Number(value) / (percent ? 100 : 1), 1);
  // Red, green and blue from 0 to 1 as hue, saturation and lightness.
  const steamColorFromRgb = (r, g, b, a) => {
    const max = Math.max(r, g, b);
    const min = Math.min(r, g, b);
    const l = (max + min) / 2;
    let h = 0;
    let s = 0;
    if (max !== min) {
      const d = max - min;
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
      else if (max === g) h = (b - r) / d + 2;
      else h = (r - g) / d + 4;
      h *= 60;
    }
    return {
      h: Math.round(h),
      s: Math.round(s * 100),
      l: Math.round(l * 100),
      a: Math.round(a * 100) / 100,
    };
  };
  const SteamColorSeparator = String.raw`\s*(?:,\s*|\s+)`;
  const SteamColorAlpha = String.raw`(?:\s*(?:,|\/)\s*([\d.]+)(%?))?\s*`;
  const SteamHslPattern = new RegExp(
    String.raw`^hsla?\(\s*([\d.]+)(?:deg)?${SteamColorSeparator}([\d.]+)%${SteamColorSeparator}([\d.]+)%` +
      String.raw`${SteamColorAlpha}\)$`,
    "iu",
  );
  const SteamRgbPattern = new RegExp(
    String.raw`^rgba?\(\s*([\d.]+)(%?)${SteamColorSeparator}([\d.]+)(%?)${SteamColorSeparator}([\d.]+)(%?)` +
      String.raw`${SteamColorAlpha}\)$`,
    "iu",
  );
  const parseSteamColor = (text) => {
    const value = String(text ?? "").trim();
    const hsl = SteamHslPattern.exec(value);
    if (hsl) {
      return {
        h: clampSteamColorPart(Number(hsl[1]), 360),
        s: clampSteamColorPart(Number(hsl[2]), 100),
        l: clampSteamColorPart(Number(hsl[3]), 100),
        a: steamColorAlpha(hsl[4], hsl[5]),
      };
    }
    const rgb = SteamRgbPattern.exec(value);
    if (rgb) {
      const channel = (number, percent) =>
        clampSteamColorPart(Number(number) / (percent ? 100 : 255), 1);
      return steamColorFromRgb(
        channel(rgb[1], rgb[2]),
        channel(rgb[3], rgb[4]),
        channel(rgb[5], rgb[6]),
        steamColorAlpha(rgb[7], rgb[8]),
      );
    }
    const hex = /^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/iu.exec(value);
    if (!hex) return null;
    let digits = hex[1];
    if (digits.length <= 4) digits = [...digits].map((digit) => digit + digit).join("");
    return steamColorFromRgb(
      parseInt(digits.slice(0, 2), 16) / 255,
      parseInt(digits.slice(2, 4), 16) / 255,
      parseInt(digits.slice(4, 6), 16) / 255,
      digits.length === 8 ? parseInt(digits.slice(6, 8), 16) / 255 : 1,
    );
  };
  const formatSteamColor = (color) => `hsla(${color.h}, ${color.s}%, ${color.l}%, ${color.a})`;
  /**
   * Opens a staged color editor; only Save dispatches the selected color.
   * @param ui Steam's resolved React and native control components.
   * @param title Modal title.
   * @param current Parsed hue/saturation/lightness/alpha value.
   * @param send Receives the saved hsla() color; cancellation never calls it.
   * @param alpha Whether opacity is editable; false fixes an opaque color.
   * @returns Whether the native modal could be shown.
   */
  const showSteamColorEditor = (ui, title, current, send, alpha = true) => {
    const react = ui.react;
    const h = react.createElement;
    function SteamColorEditor(props) {
      const [color, setColor] = react.useState(alpha ? current : { ...current, a: 1 });
      const slider = (label, key, max, step = 1) =>
        h(ui.sliderField, {
          key,
          label,
          value: color[key],
          min: 0,
          max,
          step,
          showValue: true,
          onChange: (value) => setColor({ ...color, [key]: value }),
        });
      return h(
        "div",
        { className: "steam-ui-color-editor" },
        h("div", {
          className: "steam-ui-color-preview",
          style: {
            height: "48px",
            borderRadius: "4px",
            marginBottom: "12px",
            background: formatSteamColor(color),
            border: "1px solid rgba(255,255,255,0.3)",
          },
        }),
        slider("Hue", "h", 360),
        slider("Saturation", "s", 100),
        slider("Lightness", "l", 100),
        alpha ? slider("Opacity", "a", 1, 0.01) : null,
        h(
          ui.focusable,
          {
            "flow-children": "row",
            style: { display: "flex", justifyContent: "flex-end", gap: "8px" },
          },
          h(ui.dialogButton, { onClick: () => props.close() }, "Cancel"),
          h(
            ui.dialogButtonPrimary ?? ui.dialogButton,
            {
              onClick: () => {
                send(formatSteamColor(color));
                props.close();
              },
            },
            "Save",
          ),
        ),
      );
    }
    return showSteamModal(ui, {
      title,
      className: "steam-ui-color-modal",
      render: (close) => h(SteamColorEditor, { close }),
    });
  };
  // A row the host marks draws its description in Steam's accent blue, the way a marked Quick Access
  // row does. The words are the host's; the toolkit adds none and offers no control to unmark it.
  const steamSettingDescription = (ui, row) =>
    steamAccentDescription(ui.react, row.description, row.accent === true);
  /**
   * Renders one typed host setting with native fields and shared refusal/draft handling.
   * @param ui Steam's resolved React and native control components.
   * @param row Published SteamSettingsRow descriptor.
   * @param draft Locally staged value, or undefined to use the published value.
   * @param change Handles edits and whether they should be committed.
   * @param action Handles action rows.
   * @returns The row element, or null for an unsupported row kind.
   */
  const renderSteamSettingRow = (ui, row, draft, change, action) => {
    const h = ui.react.createElement;
    const key = `steam-setting-${row.key}`;
    const common = {
      label: row.label,
      description: steamSettingDescription(ui, row),
      disabled: !!row.disabled,
    };
    const send = (value) => {
      const confirmation = row.confirm;
      if (confirmation && value === confirmation.when) {
        confirmSteamSetting(ui, confirmation, () => change(row, value));
      } else {
        change(row, value);
      }
    };
    switch (row.kind) {
      case "boolean":
        return h(ui.toggleField, {
          key,
          ...common,
          controlled: true,
          checked: draft !== undefined ? draft : !!row.checked,
          onChange: (value) => send(!!value),
        });
      case "choice":
        return h(ui.dropdown, {
          key,
          ...common,
          rgOptions: (row.choices ?? []).map((choice) => ({
            data: choice.value,
            label: choice.label,
          })),
          selectedOption: draft !== undefined ? draft : row.text,
          onChange: (option) => send(option?.data),
          // A row may ask for the control under its label rather than beside it, which is
          // how a dropdown fits a narrow panel.
          layout: row.layout === "below" ? "below" : undefined,
        });
      case "range": {
        // A range with labels is one of them by index: Steam's slider names each notch and the
        // value is the notch, not a number worth printing beside the track.
        const labels = Array.isArray(row.labels) && row.labels.length > 1 ? row.labels : null;
        return h(ui.sliderField, {
          key,
          ...common,
          value: draft !== undefined ? draft : (row.number ?? 0),
          min: labels ? 0 : (row.minimum ?? 0),
          max: labels ? labels.length - 1 : (row.maximum ?? 100),
          step: labels ? 1 : (row.step ?? 1),
          showValue: !labels,
          valueSuffix: row.suffix ?? undefined,
          notchCount: labels ? labels.length : undefined,
          notchLabels: labels
            ? labels.map((label, notchIndex) => ({ notchIndex, label: String(label) }))
            : undefined,
          notchTicksVisible: labels ? true : undefined,
          // Every step while the slider moves only redraws it; the value is sent once, when it
          // settles, so a sweep across the range is one write rather than dozens.
          onChange: (value) => change(row, value, false),
          onChangeComplete: (value) => send(value),
        });
      }
      case "color": {
        // A colour is shown as its swatch and its text, and edited in a modal of Steam's sliders,
        // the way CSSLoader's colour picker edits a theme's colour. Where this client has no
        // modal, or the value is not a colour the sliders can hold, the text itself is editable,
        // so the value is never out of reach and never replaced by a colour it was not. Text
        // being typed stays a text field until it is sent.
        const current = String(draft !== undefined ? draft : (row.text ?? ""));
        const parsed = draft === undefined ? parseSteamColor(current) : null;
        if (!ui.showModal || !ui.modalRoot || !parsed) {
          return h(ui.textField, {
            key,
            ...common,
            value: current,
            onChange: (event) => change(row, event?.target?.value ?? "", false),
            onBlur: () => {
              if (draft !== undefined && draft !== row.text) send(draft);
            },
          });
        }
        return h(ui.valueField, {
          key,
          name: row.label,
          description: steamSettingDescription(ui, row),
          focusable: false,
          value: h(
            ui.focusable,
            {
              "flow-children": "row",
              style: { display: "flex", alignItems: "center", gap: "8px" },
            },
            renderSteamUiSwatch(ui.react, current),
            h("span", null, current),
            h(
              ui.smallButton,
              {
                disabled: !!row.disabled,
                onClick: () =>
                  showSteamColorEditor(ui, row.label, parsed, send, row.colorAlpha !== false),
              },
              "Edit",
            ),
          ),
        });
      }
      case "text":
      case "secret": {
        const secret = row.kind === "secret";
        return h(ui.textField, {
          key,
          ...common,
          // A secret is never published, so its box starts empty and says only whether one is set.
          value: draft !== undefined ? draft : secret ? "" : (row.text ?? ""),
          type: secret ? "password" : "text",
          placeholder: secret ? row.text : undefined,
          maxLength: row.maximumLength ?? undefined,
          onChange: (event) => change(row, event?.target?.value ?? "", false),
          // Sent only once typed into. A secret's box starts empty, so an empty draft is the user
          // clearing it, which is a change like any other; an untouched box sends nothing.
          onBlur: () => {
            if (draft === undefined || (!secret && draft === row.text)) {
              return;
            }
            send(draft);
          },
        });
      }
      case "order": {
        const values = draft !== undefined ? draft : (row.order ?? []);
        const labelOf = (value) =>
          row.choices?.find((choice) => choice.value === value)?.label ?? value;
        const move = (index, offset) => {
          const next = values.slice();
          const [moved] = next.splice(index, 1);
          next.splice(index + offset, 0, moved);
          send(next);
        };
        return h(
          ui.react.Fragment,
          { key },
          h(ui.valueField, {
            name: row.label,
            value: null,
            description: steamSettingDescription(ui, row),
            focusable: false,
          }),
          ...values.map((value, index) =>
            h(ui.valueField, {
              key: `${key}-${value}`,
              name: labelOf(value),
              indentLevel: 1,
              focusable: false,
              value: h(
                ui.focusable,
                { "flow-children": "row" },
                h(
                  ui.smallButton,
                  { disabled: row.disabled || index === 0, onClick: () => move(index, -1) },
                  "Move up",
                ),
                h(
                  ui.smallButton,
                  {
                    disabled: row.disabled || index === values.length - 1,
                    onClick: () => move(index, 1),
                  },
                  "Move down",
                ),
              ),
            }),
          ),
        );
      }
      case "action":
        return h(ui.valueField, {
          key,
          name: row.label,
          description: steamSettingDescription(ui, row),
          focusable: false,
          value: h(
            ui.dialogButton,
            { disabled: !!row.disabled, onClick: () => action(row) },
            row.buttonLabel ?? row.label,
          ),
        });
      case "note":
        return h(ui.valueField, {
          key,
          name: row.label,
          value: row.text ?? "",
          description: steamSettingDescription(ui, row),
        });
      default:
        // A kind this build does not know is shown as its label and nothing else, never as a
        // control that would send a value the host did not describe.
        return h(ui.valueField, {
          key,
          name: row.label,
          value: "",
          description: steamSettingDescription(ui, row),
        });
    }
  };
  // What a row was published with, as text, so a list compares by its entries.
  const steamSettingPublished = (row) =>
    JSON.stringify(
      row.kind === "boolean"
        ? !!row.checked
        : row.kind === "range"
          ? (row.number ?? null)
          : row.kind === "order"
            ? (row.order ?? null)
            : (row.text ?? null),
    );
  /**
   * Tracks per-row edits and refusals without letting older requests settle newer drafts.
   * @param react Steam's React instance used by the consuming root.
   * @param revision Published revision; a change releases committed drafts to the new state.
   * @returns change(send), draft(row) and row(row) helpers for this mounted component.
   */
  const useSteamSettingDrafts = (react, revision) => {
    const [state, setState] = react.useState({ drafts: {}, refusals: {} });
    react.useEffect(
      () =>
        setState((previous) => {
          const drafts = {};
          for (const [key, draft] of Object.entries(previous.drafts)) {
            if (!draft.committed) drafts[key] = draft;
          }
          return { ...previous, drafts };
        }),
      [revision],
    );
    // Only the draft that was sent is settled; one the user has replaced since stays as it is.
    const settle = (key, sent, refusal) =>
      setState((previous) => {
        if (previous.drafts[key] !== sent) return previous;
        const drafts = { ...previous.drafts };
        if (refusal === null) {
          drafts[key] = { ...sent, committed: true };
          return { ...previous, drafts };
        }
        delete drafts[key];
        return {
          drafts,
          refusals: { ...previous.refusals, [key]: { text: refusal, base: sent.base } },
        };
      });
    const change =
      (send) =>
      (row, value, commit = true) => {
        const key = row.key;
        const draft = { value, base: steamSettingPublished(row), committed: false };
        setState((previous) => {
          const refusals = { ...previous.refusals };
          delete refusals[key];
          return { drafts: { ...previous.drafts, [key]: draft }, refusals };
        });
        if (!commit) return;
        let answer;
        try {
          answer = send(row, value);
        } catch (error) {
          answer = Promise.reject(error);
        }
        if (typeof answer?.then !== "function") {
          settle(key, draft, null);
          return;
        }
        answer.then(
          () => settle(key, draft, null),
          (reason) => settle(key, draft, String(reason?.message ?? reason)),
        );
      };
    const draft = (row) => {
      const entry = state.drafts[row.key];
      return entry && entry.base === steamSettingPublished(row) ? entry.value : undefined;
    };
    const shown = (row) => {
      const refusal = state.refusals[row.key];
      return refusal && refusal.base === steamSettingPublished(row)
        ? { ...row, description: refusal.text }
        : row;
    };
    return { change, draft, row: shown };
  };
  // The whole page: Steam's routed sidebar, one page per host page, each a list of Steam sections.
  // `route` is the page's own registered route; each page sits below it, so Steam's router keeps
  // the sidebar's selection in the address and the page's registration covers all of them.
  // `onChange` and `onAction` answer the request they made, so a refusal can be shown on its row.
  function SteamSettingsView(props) {
    const { ui, route, pages, revision, onChange, onAction } = props;
    const react = ui.react;
    const h = react.createElement;
    const drafts = useSteamSettingDrafts(react, revision);
    const change = drafts.change((row, value) => onChange(row, value));
    const run = drafts.change((row) => onAction(row));
    const action = (row) => run(row, true);
    return h(ui.routedPages, {
      pages: (pages ?? []).map((page) => ({
        title: page.title,
        route: `${route}/${page.id}`,
        icon: page.glyph ? renderSteamGlyph(react, page.glyph) : undefined,
        content: h(
          react.Fragment,
          null,
          ...(page.sections ?? []).map((section, index) =>
            h(
              ui.settingsSection,
              { key: `${page.id}-${index}`, label: section.title ?? undefined },
              ...(section.rows ?? []).map((row) =>
                renderSteamSettingRow(ui, drafts.row(row), drafts.draft(row), change, action),
              ),
            ),
          ),
        ),
      })),
    });
  }
  /**
   * Creates a routed native Settings view over host-owned descriptors.
   * @param ui Steam's resolved React and native control components.
   * @param props Base route, pages, publication revision and setting/action callbacks.
   * @returns The settings view; the caller owns state persistence and command validation.
   */
  const renderSteamSettings = (ui, props) =>
    ui.react.createElement(SteamSettingsView, { ui, ...props });
  // @fragment ui-kit.ts
  // Shared elements around Steam's native fields. Use Steam React/Focusable and render the kit
  // stylesheet in each consuming document. CSS selectors preserve Valve layout and focus behavior.
  const SteamUiKitStyles = `
.steam-ui-kit-page{margin-top:var(--basicui-header-height,40px);height:calc(100% - var(--basicui-header-height,40px));display:flex;flex-direction:column;background:var(--gpSystemDarkestGrey,#0e141b);color:#dcdedf}
.steam-ui-kit-pane{display:flex;flex-direction:column;gap:14px;padding:12px 4px 72px}
.steam-ui-kit-sheet{display:flex;flex-direction:column;gap:6px;width:100%;min-width:0;max-height:min(70vh,calc(100vh - 160px));overflow-y:auto;overflow-x:hidden;overscroll-behavior:contain;scrollbar-gutter:stable}
.steam-ui-kit-sheet>*{min-width:0;max-width:100%;box-sizing:border-box;flex-shrink:0}
.steam-ui-kit-sheet .DialogInput,.steam-ui-kit-sheet .DialogInputWrapper,.steam-ui-kit-sheet .DialogButton{min-width:0;max-width:100%;box-sizing:border-box}
.steam-ui-kit-sheet-note{font-size:14px;color:#8b929a;line-height:1.45;margin:0 0 12px}
.steam-ui-kit-sheet-error{color:#ff6d6d;font-size:14px}
.steam-ui-kit-sheet-actions{padding-top:18px}
.steam-ui-kit-page div[class*="gamepadtabbedpage_TabHeaderRowWrapper"]{background:#1b2838}
.steam-ui-kit-page-banner{margin:8px 48px 0}
.steam-ui-kit-page h3{margin:6px 0 0;font-size:15px;font-weight:700;color:#fff}
.steam-ui-kit-page p{margin:0;font-size:14px;line-height:1.5;color:#c6d4df;max-width:700px;white-space:pre-wrap}
.steam-ui-kit-detail{display:flex;gap:32px;padding:12px 4px 72px}
.steam-ui-kit-detail-main{flex:1;min-width:0;display:flex;flex-direction:column;gap:10px}
.steam-ui-kit-detail-aside{width:300px;flex:0 0 auto;display:flex;flex-direction:column;gap:14px}
.steam-ui-kit-detail-heading{display:flex;align-items:baseline;gap:12px}
.steam-ui-kit-detail-heading h2{margin:0;font-size:30px;font-weight:700;color:#fff}
.steam-ui-kit-detail-heading span{font-size:16px;font-weight:700;color:#fff}
.steam-ui-kit-header{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 10px;margin:0 -10px;border-radius:2px;outline:2px solid transparent}
.steam-ui-kit-header.gpfocus,.steam-ui-kit-header:hover{background:rgba(255,255,255,.08)}
.steam-ui-kit-header.plain:hover{background:transparent}
.steam-ui-kit-header.sub{padding:6px 10px}
.steam-ui-kit-header-icon{display:flex;flex:0 0 auto;color:rgba(255,255,255,.8)}
.steam-ui-kit-header-icon svg{width:18px;height:18px}
.steam-ui-kit-header-text{min-width:0;flex:1 1 auto}
.steam-ui-kit-header-title{font-size:12px;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:rgba(255,255,255,.6);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-header.open .steam-ui-kit-header-title{color:#fff}
.steam-ui-kit-header.sub .steam-ui-kit-header-title{font-size:13px;font-weight:600;letter-spacing:0;text-transform:none;color:rgba(255,255,255,.75)}
.steam-ui-kit-header.sub.open .steam-ui-kit-header-title{color:#fff}
.steam-ui-kit-header-detail{font-size:12px;color:rgba(255,255,255,.55);margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-header-caret{flex:0 0 auto;color:rgba(255,255,255,.7);display:flex}
.steam-ui-kit-group,.steam-ui-kit-blocks > div:not(:empty),.steam-ui-kit-valve > div:not(:empty){border-radius:4px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.07);padding:4px 12px 8px!important;margin:0 12px 10px!important;box-sizing:border-box}
.steam-ui-kit-group.plain{padding:8px 12px!important}
.steam-ui-kit-group.hidden{display:none}
.steam-ui-kit-more{display:flex;justify-content:center;padding:8px 0 24px}
.steam-ui-kit-more .DialogButton{width:50%!important}
.steam-ui-kit-group.closed .steam-ui-kit-group-body{display:none}
.steam-ui-kit-valve > div:not(:empty){padding-top:8px!important}
.steam-ui-kit-valve > div:not(:empty) > div:first-child{font-size:12px!important;font-weight:700;letter-spacing:.09em;text-transform:uppercase;color:rgba(255,255,255,.6)!important;padding:4px 0 8px!important}
.steam-ui-kit-group-body > div > :first-child,.steam-ui-kit-blocks > div > div > :first-child,.steam-ui-kit-valve > div > div > :first-child{--field-negative-horizontal-margin:0px}
.steam-ui-kit-group-body div,.steam-ui-kit-blocks div,.steam-ui-kit-valve div,.steam-ui-kit-battery div{min-width:0!important}
.steam-ui-kit-group-body button.DialogButton,.steam-ui-kit-blocks button.DialogButton,.steam-ui-kit-valve button.DialogButton{min-width:0!important;box-sizing:border-box!important}
.steam-ui-kit-battery{width:calc(100% - 32px);box-sizing:border-box;margin:0 16px 6px;padding-bottom:8px;border-bottom:1px solid rgba(255,255,255,.08);--field-negative-horizontal-margin:0px}
.steam-ui-kit-battery > *{margin-inline:0!important;padding-inline:0!important;width:100%!important}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)){display:flex;flex-wrap:nowrap;align-items:center;gap:8px;height:24px!important}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(1){width:24px!important;height:24px!important;margin:0!important;transform:scale(.6);transform-origin:center}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(1) > div{height:24px!important;align-items:center}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(2){font-size:14px!important;font-weight:600;height:auto!important;line-height:24px}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(3){margin-left:auto!important;height:auto!important;flex-direction:row!important;align-items:baseline;gap:6px}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(3) > :first-child{font-size:13px!important;font-weight:600;height:auto!important}
.steam-ui-kit-battery div:has(> :nth-child(2):nth-last-child(2):not(:empty)) > :nth-child(3) > :last-child{font-size:10px!important;height:auto!important}
.steam-ui-kit-actions{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.steam-ui-kit-actions button.DialogButton{display:block!important;width:auto!important;min-width:0!important;height:36px!important;line-height:36px!important;padding:0 10px!important;box-sizing:border-box!important;font-size:13px;text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-actions button.DialogButton:last-child:nth-child(odd){grid-column:1 / -1}
.steam-ui-kit-actions .DialogButton.steam-ui-kit-wide{grid-column:1 / -1}
.steam-ui-kit-nested{margin-left:2px;padding-left:12px;border-left:2px solid rgba(255,255,255,.12);box-sizing:border-box}
.steam-ui-kit-nested .DialogToggle_Label{font-size:14px}
.steam-ui-kit-nested .DialogToggle_Description{font-size:12px}
.steam-ui-kit-highlight{color:#fca904}
.steam-ui-kit-swatch{width:20px;height:20px;border-radius:3px;border:1px solid rgba(255,255,255,.3);flex:0 0 auto}
.steam-ui-kit-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:14px}
.steam-ui-kit-card{display:flex;flex-direction:column;border-radius:4px;overflow:hidden;background:#ACB2C924;outline:2px solid transparent;transition:outline-color 150ms,background 150ms}
.steam-ui-kit-card.gpfocus,.steam-ui-kit-card:hover{background:#ACB2C947;outline-color:#fff}
.steam-ui-kit-card-shot{position:relative;aspect-ratio:16 / 10;background:#10151c;overflow:hidden}
.steam-ui-kit-card-shot img{width:100%;height:100%;object-fit:cover;display:block}
.steam-ui-kit-card-stats{position:absolute;left:0;right:0;bottom:0;display:flex;gap:12px;padding:6px 8px;font-size:12px;color:#fff;background:linear-gradient(180deg,transparent,rgba(0,0,0,.75))}
.steam-ui-kit-card-stats span{display:inline-flex;align-items:center;gap:4px;min-width:0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-card-stats svg{width:13px;height:13px;flex:0 0 auto}
.steam-ui-kit-badge{position:absolute;top:6px;right:6px;padding:2px 8px;border-radius:12px;font-size:11px;font-weight:700;background:#5cb85c;color:#000}
.steam-ui-kit-badge.warn{background:#fca904}
.steam-ui-kit-card-title{font-size:15px;font-weight:600;color:#fff;padding:8px 10px 0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-card-meta{font-size:11px;color:rgba(255,255,255,.55);padding:2px 10px}
.steam-ui-kit-card-meta:last-child{padding-bottom:10px}
.steam-ui-kit-empty{padding:12px;text-align:center;color:#b8bcbf;font-size:14px}
.steam-ui-kit-empty.error{color:#ff6d6d}
.steam-ui-kit-banner{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:10px 14px;border-radius:2px;background:rgba(26,159,255,.18);font-size:14px;color:#dcdedf}
.steam-ui-kit-banner.error{background:rgba(194,70,62,.25)}
.steam-ui-kit-toolbar{display:flex;align-items:flex-end;gap:12px;flex-wrap:nowrap}
.steam-ui-kit-toolbar .DialogButton{width:auto;min-width:auto;height:40px;padding:0 16px;white-space:nowrap}
.steam-ui-kit-tool{display:flex;flex-direction:column;flex:0 0 auto}
.steam-ui-kit-tool .DialogLabel{font-size:12px;margin-bottom:4px}
.steam-ui-kit-tool .DialogDropDown_CurrentDisplay{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.steam-ui-kit-tool.grow{flex:1 1 auto;min-width:160px}
.steam-ui-kit-tool.grow .DialogInputLabelGroup,.steam-ui-kit-tool.grow .DialogInput_Wrapper{margin:0}
.steam-ui-kit-chips{display:flex;flex-wrap:wrap;gap:8px}
.steam-ui-kit-chips .DialogButton{width:auto;min-width:auto;height:32px;padding:0 12px;font-size:13px}
.steam-ui-kit-box{background:rgba(27,40,56,.9);border-radius:4px;padding:16px;display:flex;flex-direction:column;gap:10px}
.steam-ui-kit-box-title{display:flex;align-items:center;gap:6px;font-size:16px;font-weight:600;color:#fff}
.steam-ui-kit-box-title svg{width:18px;height:18px}
.steam-ui-kit-box p{margin:0;line-height:1.45}
.steam-ui-kit-box,.steam-ui-kit-group,.steam-ui-kit-group-body,.steam-ui-kit-pane,.steam-ui-kit-detail-aside{min-width:0;max-width:100%;box-sizing:border-box}
.steam-ui-kit-box>*{min-width:0;max-width:100%;box-sizing:border-box}
.steam-ui-kit-box .DialogButton,.steam-ui-kit-sheet .DialogButton{min-width:0!important;max-width:100%;box-sizing:border-box!important;white-space:normal;overflow-wrap:anywhere}
.steam-ui-kit-choice{display:flex;flex-direction:column;gap:6px;min-width:0;max-width:100%;width:100%;box-sizing:border-box}
.steam-ui-kit-choice-label{font-size:14px;color:#dcdedf}
.steam-ui-kit-choice .DialogDropDown{min-width:0!important;max-width:100%!important;width:100%!important;box-sizing:border-box!important;margin:0}
.steam-ui-kit-choice div{min-width:0!important;max-width:100%!important;box-sizing:border-box!important}
.steam-ui-kit-choice .DialogButton{width:100%!important;min-width:0!important;max-width:100%!important;box-sizing:border-box!important}
.steam-ui-kit-choice .DialogDropDown_CurrentDisplay{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.steam-ui-kit-choice>div{width:100%!important;min-width:0;max-width:100%;box-sizing:border-box;--field-negative-horizontal-margin:0px}
.steam-ui-kit-select-row{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 12px;width:100%;min-width:0;text-align:left;box-sizing:border-box}
.steam-ui-kit-select-row-title{min-width:0;overflow-wrap:anywhere}
.steam-ui-kit-select-row-detail{grid-column:1 / -1;min-width:0;font-size:13px;color:#8b929a;white-space:normal;overflow-wrap:anywhere}
.steam-ui-kit-picker{display:flex;flex-direction:column;gap:12px;min-width:0;width:min(900px,100%);max-width:100%;box-sizing:border-box}
.steam-ui-kit-picker-body{display:grid;grid-template-columns:minmax(140px,1fr) minmax(0,2fr);gap:16px;min-height:0;height:min(360px,48vh)}
.steam-ui-kit-picker-places,.steam-ui-kit-picker-files{min-width:0;min-height:0;overflow-y:auto;overflow-x:hidden;display:flex;flex-direction:column;gap:6px;background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.12);border-radius:4px;padding:8px;box-sizing:border-box}
.steam-ui-kit-picker-heading{padding:4px 8px 8px;border-bottom:1px solid #3d4450;font-size:12px;font-weight:600;color:#8b929a;text-transform:uppercase;letter-spacing:.06em}
.steam-ui-kit-picker .steam-ui-kit-select-row{padding:4px 0;border-bottom:1px solid rgba(255,255,255,.14)}
.steam-ui-kit-picker .DialogButton{min-width:0!important;max-width:100%;width:100%;box-sizing:border-box!important;white-space:normal;margin:0}
.steam-ui-kit-picker-path{font-size:14px;color:#b8bcbf;overflow-wrap:anywhere}
.steam-ui-kit-picker-actions{display:flex;gap:8px;justify-content:flex-end;flex-wrap:wrap}
.steam-ui-kit-picker-actions .DialogButton{width:auto}
.steam-ui-kit-muted{color:rgb(124,142,163);font-size:13px}
.steam-ui-kit-gallery{display:flex;gap:12px}
.steam-ui-kit-thumbs{display:flex;flex-direction:column;gap:8px}
.steam-ui-kit-thumb{width:96px;aspect-ratio:16 / 10;border-radius:3px;overflow:hidden;opacity:.6;outline:2px solid transparent}
.steam-ui-kit-thumb.current,.steam-ui-kit-thumb.gpfocus{opacity:1;outline-color:#fff}
.steam-ui-kit-thumb img{width:100%;height:100%;object-fit:cover;display:block}
.steam-ui-kit-hero{position:relative;width:556px;max-width:100%;aspect-ratio:16 / 10;border-radius:4px;overflow:hidden;background:#10151c}
.steam-ui-kit-hero img{width:100%;height:100%;object-fit:cover;display:block}
.steam-ui-kit-hero-empty{display:flex;align-items:center;justify-content:center;height:100%;color:#8b929a}
.steam-ui-kit-hero-count{position:absolute;right:10px;bottom:10px;padding:3px 8px;border-radius:2px;background:rgba(0,0,0,.7);font-size:12px;color:#fff}
.steam-ui-kit-hero.video{aspect-ratio:16 / 9}
.steam-ui-kit-hero video{width:100%;height:100%;object-fit:contain;display:block}
.steam-ui-kit-hero.video img{object-fit:contain}
.steam-ui-kit-modal-body{display:flex;flex-direction:column;gap:12px}
.steam-ui-kit-modal-body p{margin:0}
.steam-ui-kit-modal-actions{display:flex;justify-content:flex-end;gap:8px;margin-top:8px}
`;
  // One stylesheet element and one icon renderer per React, so a root re-rendering on every host
  // publication hands React the same style element and the same caret elements each time rather
  // than fresh ones to diff.
  const steamUiKitStyles = new WeakMap();
  const steamUiKitIcons = new WeakMap();
  /**
   * Returns the cached kit stylesheet element; render it once in each consuming document root.
   * @param react Steam's React instance used by the consuming root.
   * @returns The style element shared by callers using this React instance.
   */
  const steamUiKitStyle = (react) => {
    let element = steamUiKitStyles.get(react);
    if (!element) {
      element = react.createElement("style", { key: "steam-ui-kit" }, SteamUiKitStyles);
      steamUiKitStyles.set(react, element);
    }
    return element;
  };
  const steamUiKitIcon = (react) => {
    let icon = steamUiKitIcons.get(react);
    if (!icon) {
      icon = createIconRenderer(react);
      steamUiKitIcons.set(react, icon);
    }
    return icon;
  };
  /**
   * Fits a native dropdown to a box, toolbar or table cell without a second settings-row label.
   * @param ui Resolved native controls; a full field with below layout is the fallback.
   * @param props Accessible label, optional visible label and native option/selection/change props.
   * @returns A contained dropdown; its popup remains owned by Steam.
   */
  const renderSteamUiChoice = (ui, props) => {
    const h = ui.react.createElement;
    const bare = !!ui.dropdownControl;
    return h(
      "div",
      { className: "steam-ui-kit-choice", "aria-label": props.label },
      bare && props.showLabel !== false
        ? h("div", { className: "steam-ui-kit-choice-label" }, props.label)
        : null,
      h(ui.dropdownControl ?? ui.dropdown, {
        label: bare || props.showLabel !== false ? props.label : "",
        menuLabel: props.label,
        layout: "below",
        rgOptions: props.rgOptions,
        selectedOption: props.selectedOption,
        disabled: !!props.disabled,
        onChange: props.onChange,
        "aria-label": props.label,
      }),
    );
  };
  /**
   * Arranges a selectable item as a title, trailing status and separate supporting line.
   * @param ui Resolved native Steam button.
   * @param props Stable key, label/detail/status, selected state and one activation callback.
   * @returns One native focus target with structured content instead of concatenated button text.
   */
  const renderSteamUiSelectRow = (ui, props) => {
    const h = ui.react.createElement;
    return h(
      ui.dialogButton,
      { key: props.key, "aria-pressed": !!props.selected, onClick: props.onClick },
      h(
        "div",
        { className: "steam-ui-kit-select-row" },
        h("span", { className: "steam-ui-kit-select-row-title" }, props.title),
        props.status ?? null,
        props.detail
          ? h("span", { className: "steam-ui-kit-select-row-detail" }, props.detail)
          : null,
      ),
    );
  };
  /**
   * Creates a section heading with optional controller-accessible folding.
   * @param ui Steam's resolved React and native control components.
   * @param props Title, optional icon/detail, fold state and toggle callback; sub selects nested styling.
   * @returns A heading element; without onToggle it is not a focusable fold control.
   */
  const renderSteamUiHeader = (ui, props) => {
    const h = ui.react.createElement;
    const folds = typeof props.onToggle === "function";
    const collapsed = folds && !!props.collapsed;
    const icon = steamUiKitIcon(ui.react);
    const children = [
      props.icon ? h("div", { className: "steam-ui-kit-header-icon" }, props.icon) : null,
      h(
        "div",
        { className: "steam-ui-kit-header-text" },
        h("div", { className: "steam-ui-kit-header-title" }, props.title),
        props.detail ? h("div", { className: "steam-ui-kit-header-detail" }, props.detail) : null,
      ),
      folds
        ? h(
            "div",
            { className: "steam-ui-kit-header-caret" },
            collapsed ? icon("sectionClosed", 18) : icon("sectionOpen", 18),
          )
        : null,
    ].filter((child) => child !== null);
    const className = `steam-ui-kit-header${collapsed ? "" : " open"}${folds ? "" : " plain"}${props.sub ? " sub" : ""}`;
    return folds
      ? h(
          ui.focusable ?? "div",
          {
            className,
            onActivate: props.onToggle,
            onOKActionDescription: collapsed ? "Expand" : "Collapse",
          },
          ...children,
        )
      : h("div", { className }, ...children);
  };
  /**
   * Creates a section whose hidden or folded descendants remain mounted but cannot receive focus.
   * @param ui Steam's resolved React and native control components.
   * @param props Section identity, optional title/icon/detail and fold/visibility controls.
   * @param children Rows retained while the section is folded.
   * @returns The grouped React element; the caller owns fold state.
   */
  const renderSteamUiGroup = (ui, props, ...children) => {
    const h = ui.react.createElement;
    const folds = typeof props.onToggle === "function";
    const collapsed = folds && !!props.collapsed;
    const className = [
      "steam-ui-kit-group",
      props.title ? "" : "plain",
      collapsed ? "closed" : "",
      props.hidden ? "hidden" : "",
    ]
      .filter((name) => name)
      .join(" ");
    // Steam's Focusable where the client has one, so navigation can be switched off for what is not
    // drawn; a plain element otherwise, where there is no gamepad navigation to switch off.
    const box = (unreachable, boxProps, ...kids) =>
      ui.focusable
        ? h(ui.focusable, { ...boxProps, childFocusDisabled: unreachable }, ...kids)
        : h("div", boxProps, ...kids);
    return box(
      !!props.hidden,
      { key: props.key, className },
      props.title
        ? renderSteamUiHeader(ui, {
            title: props.title,
            icon: props.icon,
            detail: props.detail,
            collapsed,
            onToggle: props.onToggle,
          })
        : null,
      box(collapsed, { className: "steam-ui-kit-group-body" }, ...children),
    );
  };
  /**
   * Creates a controller-navigable grid of native action buttons.
   * @param ui Steam's resolved React and native control components.
   * @param actions Stable ids, visible labels and activation callbacks in display order.
   * @returns A two-column grid; long labels span both columns.
   */
  const renderSteamUiActions = (ui, actions) => {
    const h = ui.react.createElement;
    return h(
      ui.focusable,
      { "flow-children": "row", className: "steam-ui-kit-actions" },
      ...actions.map((action) =>
        h(
          action.primary ? (ui.dialogButtonPrimary ?? ui.dialogButton) : ui.dialogButton,
          {
            key: action.id,
            className: action.label.length > 18 ? "steam-ui-kit-wide" : undefined,
            onClick: action.onClick,
            disabled: !!action.disabled,
          },
          action.label,
        ),
      ),
    );
  };
  // A scrollable Steam modal body. The host supplies its fields and action handlers; the kit owns
  // the common spacing, error line and action row instead of each page carrying another sheet.
  const renderSteamUiSheet = (ui, props, ...children) => {
    const h = ui.react.createElement;
    return h(
      ui.focusable ?? "div",
      {
        className: ["steam-ui-kit-sheet", props.className].filter(Boolean).join(" "),
        "flow-children": "column",
      },
      steamUiKitStyle(ui.react),
      props.note ? h("p", { className: "steam-ui-kit-sheet-note" }, props.note) : null,
      ...children,
      props.error ? h("p", { className: "steam-ui-kit-sheet-error" }, props.error) : null,
      props.actions?.length
        ? h(
            "div",
            { className: "steam-ui-kit-sheet-actions" },
            renderSteamUiActions(ui, props.actions),
          )
        : null,
    );
  };
  // A controlled list of text values: one Steam text field and remove action per row, plus add and
  // optional reset. The caller owns the draft and validation, including any application limits.
  const renderSteamUiStringList = (ui, props) => {
    const h = ui.react.createElement;
    return h(
      ui.focusable ?? "div",
      { "flow-children": "column" },
      ...props.values.map((value, index) =>
        h(
          ui.focusable ?? "div",
          { key: index, "flow-children": "column" },
          h(ui.textField, {
            label: props.label?.(index) ?? `Value ${index + 1}`,
            value,
            onChange: (event) =>
              props.onChange(
                props.values.map((item, position) =>
                  position === index ? (event?.target?.value ?? "") : item,
                ),
              ),
          }),
          h(
            ui.dialogButton,
            {
              onClick: () =>
                props.onChange(props.values.filter((_item, position) => position !== index)),
            },
            props.removeLabel?.(index) ?? `Remove value ${index + 1}`,
          ),
        ),
      ),
      h(
        ui.dialogButton,
        { onClick: () => props.onChange([...props.values, ""]) },
        props.addLabel ?? "Add value",
      ),
      props.resetLabel
        ? h(ui.dialogButton, { onClick: () => props.onChange([]) }, props.resetLabel)
        : null,
    );
  };
  /**
   * Creates the next-page control for a bounded list of rendered items.
   * @param ui Steam's resolved React and native control components.
   * @param props Optional label/disabled state and the callback requesting more items.
   * @returns A centered native button; it does not fetch or append items itself.
   */
  const renderSteamUiMore = (ui, props) => {
    const h = ui.react.createElement;
    return h(
      "div",
      { className: "steam-ui-kit-more" },
      h(
        ui.dialogButton,
        { onClick: props.onClick, disabled: !!props.disabled },
        props.label ?? "Load More",
      ),
    );
  };
  /**
   * Creates a page column that acquires controller focus when mounted.
   * @param ui Steam's resolved React and native control components.
   * @param props Optional React key and additional class name.
   * @param children Toolbar, list or other page content in navigation order.
   * @returns The focusable pane, or a plain container when Focusable is unavailable.
   */
  const renderSteamUiPane = (ui, props, ...children) => {
    const h = ui.react.createElement;
    const className = ["steam-ui-kit-pane", props.className].filter(Boolean).join(" ");
    return ui.focusable
      ? h(
          ui.focusable,
          { key: props.key, className, autoFocus: true, "flow-children": "column" },
          ...children,
        )
      : h("div", { key: props.key, className }, ...children);
  };
  /**
   * Creates a nested page level that handles Back before Steam leaves the route.
   * @param ui Steam's resolved React and native control components.
   * @param props Optional class and callback returning to the previous level.
   * @param children Content of the nested level.
   * @returns A focusable level with initial focus and the supplied Back action.
   */
  const renderSteamUiLevel = (ui, props, ...children) =>
    ui.react.createElement(
      ui.focusable,
      {
        className: props.className,
        autoFocus: true,
        onCancelButton: props.onBack,
        onCancelActionDescription: "Back",
      },
      ...children,
    );
  /**
   * Creates a noninteractive color preview.
   * @param react Steam's React instance used by the consuming root.
   * @param color CSS color used as the swatch background.
   * @returns The swatch element.
   */
  const renderSteamUiSwatch = (react, color) =>
    react.createElement("div", { className: "steam-ui-kit-swatch", style: { background: color } });
  /**
   * Creates one activatable gallery card using Steam controller focus.
   * @param ui Steam's resolved React and native control components.
   * @param props Optional image/stats/badge, title, metadata and activation callback.
   * @returns A single focus target containing the card content.
   */
  const renderSteamUiCard = (ui, props) => {
    const h = ui.react.createElement;
    return h(
      ui.focusable,
      {
        key: props.key,
        className: "steam-ui-kit-card",
        onActivate: props.onActivate,
        onOKActionDescription: "Open",
      },
      h(
        "div",
        { className: "steam-ui-kit-card-shot" },
        props.image ? h("img", { src: props.image, alt: "", loading: "lazy" }) : null,
        props.stats?.length
          ? h(
              "div",
              { className: "steam-ui-kit-card-stats" },
              ...props.stats.map((stat, index) =>
                h("span", { key: index }, stat.glyph ?? null, stat.text),
              ),
            )
          : null,
        props.badge
          ? h(
              "div",
              { className: `steam-ui-kit-badge${props.badge.warn ? " warn" : ""}` },
              props.badge.text,
            )
          : null,
      ),
      h("div", { className: "steam-ui-kit-card-title" }, props.title),
      ...(props.meta ?? []).map((line, index) =>
        h("div", { key: index, className: "steam-ui-kit-card-meta" }, line),
      ),
    );
  };
  /**
   * Arranges rendered cards in a controller-navigable grid.
   * @param ui Steam's resolved React and native control components.
   * @param cards Already-created card elements in display order.
   * @returns The grid element; callers bound the number of mounted cards.
   */
  const renderSteamUiGrid = (ui, cards) =>
    ui.react.createElement(
      ui.focusable,
      { className: "steam-ui-kit-grid", "flow-children": "grid" },
      ...cards,
    );
  /**
   * Creates an empty-list explanation or error message.
   * @param react Steam's React instance used by the consuming root.
   * @param text Visible reason or empty-state text.
   * @param error Whether to use error styling.
   * @returns The message element.
   */
  const renderSteamUiEmpty = (react, text, error = false) =>
    react.createElement("div", { className: `steam-ui-kit-empty${error ? " error" : ""}` }, text);
  /**
   * Creates a dismissible notice or error.
   * @param ui Steam's resolved React and native control components.
   * @param props Message, error flag and callback that clears the owning state.
   * @returns The banner; dismissal is delegated to the caller.
   */
  const renderSteamUiBanner = (ui, props) => {
    const h = ui.react.createElement;
    return h(
      "div",
      { className: `steam-ui-kit-banner${props.error ? " error" : ""}` },
      h("span", null, props.text),
      h(ui.smallButton ?? ui.dialogButton, { onClick: props.onDismiss }, "Dismiss"),
    );
  };
  /**
   * Groups existing controls in a horizontal controller-navigation row.
   * @param ui Steam's resolved React and native control components.
   * @param tools Rendered toolbar items, normally created with renderSteamUiTool.
   * @returns The focusable toolbar element.
   */
  const renderSteamUiToolbar = (ui, ...tools) =>
    ui.react.createElement(
      ui.focusable,
      { className: "steam-ui-kit-toolbar", "flow-children": "row" },
      ...tools,
    );
  /**
   * Labels a control within a toolbar.
   * @param ui Steam's resolved React and native control components.
   * @param label Visible label, or null to omit it.
   * @param control Rendered native control.
   * @param grow Whether this item occupies remaining toolbar space.
   * @returns The labelled toolbar item.
   */
  const renderSteamUiTool = (ui, label, control, grow = false) => {
    const h = ui.react.createElement;
    return h(
      "div",
      { className: `steam-ui-kit-tool${grow ? " grow" : ""}` },
      label ? h("span", { className: "DialogLabel" }, label) : null,
      control,
    );
  };
  /**
   * Creates a wrapping row of native buttons.
   * @param ui Steam's resolved React and native control components.
   * @param chips Labels, click handlers and optional controller action descriptions.
   * @returns The controller-navigable chip row.
   */
  const renderSteamUiChips = (ui, chips) => {
    const h = ui.react.createElement;
    return h(
      ui.focusable,
      { "flow-children": "row", className: "steam-ui-kit-chips" },
      ...chips.map((chip, index) =>
        h(
          ui.dialogButton,
          {
            key: `${index}:${chip.label}`,
            onClick: chip.onClick,
            onOKActionDescription: chip.description,
          },
          chip.label,
        ),
      ),
    );
  };
  /**
   * Groups detail content under an optional heading.
   * @param react Steam's React instance used by the consuming root.
   * @param title Heading content; a false-like value omits the heading.
   * @param children Content rendered below the heading.
   * @returns The detail box element.
   */
  const renderSteamUiBox = (react, title, ...children) =>
    react.createElement(
      "div",
      { className: "steam-ui-kit-box" },
      title ? react.createElement("div", { className: "steam-ui-kit-box-title" }, title) : null,
      ...children,
    );
  /**
   * Creates a selected image with controller-selectable thumbnails.
   * @param ui Steam's resolved React and native control components.
   * @param props Image URLs, selected index, selection callback and optional empty text.
   * @returns The gallery; the displayed index is clamped without changing caller state.
   */
  const renderSteamUiGallery = (ui, props) => {
    const h = ui.react.createElement;
    const images = props.images ?? [];
    const index = Math.min(Math.max(0, props.index), Math.max(0, images.length - 1));
    const shown = images[index];
    return h(
      "div",
      { className: "steam-ui-kit-gallery" },
      images.length > 1
        ? h(
            ui.focusable,
            { className: "steam-ui-kit-thumbs", "flow-children": "column" },
            ...images.map((url, at) =>
              h(
                ui.focusable,
                {
                  key: `${at}:${url}`,
                  className: `steam-ui-kit-thumb${at === index ? " current" : ""}`,
                  onActivate: () => props.onSelect(at),
                  onFocus: () => props.onSelect(at),
                },
                h("img", { src: url, alt: "" }),
              ),
            ),
          )
        : null,
      h(
        "div",
        { className: "steam-ui-kit-hero" },
        shown
          ? h("img", { src: shown, alt: "" })
          : h("div", { className: "steam-ui-kit-hero-empty" }, props.empty ?? "No image"),
        images.length > 1
          ? h("div", { className: "steam-ui-kit-hero-count" }, `${index + 1}/${images.length}`)
          : null,
      ),
    );
  };
  /**
   * Creates a muted looping preview, a poster fallback or empty-state text.
   * @param react Steam's React instance used by the consuming root.
   * @param props Optional media URL, poster URL and empty-state message.
   * @returns The preview element; unmounting removes its video element.
   */
  const renderSteamUiVideo = (react, props) =>
    react.createElement(
      "div",
      { className: "steam-ui-kit-hero video" },
      props.src
        ? react.createElement("video", {
            src: props.src,
            poster: props.poster ?? undefined,
            autoPlay: true,
            loop: true,
            muted: true,
            playsInline: true,
          })
        : props.poster
          ? react.createElement("img", { src: props.poster, alt: "" })
          : react.createElement(
              "div",
              { className: "steam-ui-kit-hero-empty" },
              props.empty ?? "No preview",
            ),
    );
  /**
   * Shared SVG path data for store-page metadata glyphs.
   */
  const SteamUiGlyphs = Object.freeze({
    download: "M11 3h2v9.2l3.6-3.6 1.4 1.4-6 6-6-6 1.4-1.4L11 12.2zM4 19h16v2H4z",
    star: "M12 2.5l2.9 6 6.6.9-4.8 4.6 1.2 6.5L12 17.4 6.1 20.5l1.2-6.5L2.5 9.4l6.6-.9z",
    heart:
      "M12 21s-7-4.6-9.3-9.1C1 8.5 3.2 5 6.7 5c2 0 3.4 1 4.3 2.3C12 6 13.4 5 15.3 5c3.5 0 5.7 3.5 4 6.9C19 16.4 12 21 12 21z",
    target:
      "M12 3a9 9 0 1 1 0 18 9 9 0 0 1 0-18zm0 2a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm0 3a4 4 0 1 1 0 8 4 4 0 0 1 0-8zm0 2a2 2 0 1 0 0 4 2 2 0 0 0 0-4z",
  });
  /**
   * Creates an SVG from the kit glyph vocabulary.
   * @param react Steam's React instance used by the consuming root.
   * @param name Key from SteamUiGlyphs.
   * @returns The rendered glyph.
   */
  const renderSteamUiGlyph = (react, name) => renderSteamGlyph(react, SteamUiGlyphs[name]);
  /**
   * Component names a tabbed page must resolve before installation.
   */
  const SteamUiTabbedPageRequired = Object.freeze([
    "react",
    "focusable",
    "toggleField",
    "dropdown",
    "sliderField",
    "textField",
    "dialogButton",
    "dialogButtonPrimary",
    "smallButton",
    "valueField",
    "settingsSection",
    "tabs",
    "modalRoot",
    "showModal",
  ]);
  /**
   * Creates a native tabbed host page and mounts only its active content.
   * @param ui Steam's resolved React and native control components.
   * @param props Page identity/label, optional CSS/banner, tabs, active id and tab callbacks.
   * @returns The page with kit styles; an unknown active id selects the first tab.
   */
  const renderSteamUiTabbedPage = (ui, props) => {
    const h = ui.react.createElement;
    const active = props.tabs.some((tab) => tab.id === props.active)
      ? props.active
      : (props.tabs[0]?.id ?? "");
    return h(
      "div",
      { id: props.id, className: "steam-ui-kit-page", "aria-label": props.label },
      steamUiKitStyle(ui.react),
      props.style ? h("style", null, props.style) : null,
      props.banner?.text
        ? h("div", { className: "steam-ui-kit-page-banner" }, renderSteamUiBanner(ui, props.banner))
        : null,
      h(ui.tabs, {
        autoFocusContents: true,
        activeTab: active,
        onShowTab: props.onTab,
        tabs: props.tabs.map((tab) => ({
          id: tab.id,
          title: tab.title,
          content: tab.id === active ? props.content(tab.id) : null,
        })),
      }),
    );
  };
  /**
   * Creates an item detail view with its own controller Back action.
   * @param ui Steam's resolved React and native control components.
   * @param props Heading/badge/media, primary and aside content, and the Back callback.
   * @returns The focused detail layout.
   */
  const renderSteamUiDetail = (ui, props) => {
    const h = ui.react.createElement;
    return h(
      ui.focusable,
      {
        className: "steam-ui-kit-detail",
        autoFocus: true,
        onCancelButton: props.onBack,
        onCancelActionDescription: "Back",
      },
      h(
        "div",
        { className: "steam-ui-kit-detail-main" },
        props.media ?? null,
        h(
          "div",
          { className: "steam-ui-kit-detail-heading" },
          h("h2", null, props.title),
          props.badge ? h("span", null, props.badge) : null,
        ),
        ...props.main,
      ),
      h(
        "div",
        { className: "steam-ui-kit-detail-aside" },
        ...props.aside,
        h(ui.dialogButton, { onClick: props.onBack }, "Back"),
      ),
    );
  };
  /**
   * Opens a native confirmation; Cancel and Back invoke no action.
   * @param ui Steam's resolved React and native control components.
   * @param props Title, explanation, confirm label and callback invoked on confirmation.
   * @returns Whether the modal could be shown; a thrown confirmation callback is not swallowed.
   */
  const showSteamUiConfirm = (ui, props) => {
    const h = ui.react.createElement;
    return showSteamModal(ui, {
      title: props.title,
      className: "steam-ui-kit-modal",
      render: (close) =>
        h(
          "div",
          { className: "steam-ui-kit-modal-body" },
          h("p", null, props.text),
          h(
            ui.focusable,
            { "flow-children": "row", className: "steam-ui-kit-modal-actions" },
            h(ui.dialogButton, { onClick: close }, "Cancel"),
            h(
              ui.dialogButtonPrimary ?? ui.dialogButton,
              {
                onClick: () => {
                  props.onConfirm();
                  close();
                },
              },
              props.confirmLabel,
            ),
          ),
        ),
    });
  };
  // Asks for a line of text: a sentence, Steam's text field and two buttons. An empty answer is not
  // sent.
  function SteamUiPromptBody(props) {
    const ui = props.ui;
    const react = ui.react;
    const h = react.createElement;
    const [value, setValue] = react.useState(props.initial ?? "");
    return h(
      "div",
      { className: "steam-ui-kit-modal-body" },
      props.text ? h("p", null, props.text) : null,
      h(ui.textField, {
        label: props.label,
        value,
        onChange: (event) => setValue(event?.target?.value ?? ""),
      }),
      h(
        ui.focusable,
        { "flow-children": "row", className: "steam-ui-kit-modal-actions" },
        h(ui.dialogButton, { onClick: props.close }, "Cancel"),
        h(
          ui.dialogButtonPrimary ?? ui.dialogButton,
          {
            onClick: () => {
              if (!String(value).trim()) return;
              props.onConfirm(String(value).trim());
              props.close();
            },
          },
          props.confirmLabel,
        ),
      ),
    );
  }
  /**
   * Opens a native text prompt that submits only a trimmed nonempty value.
   * @param ui Steam's resolved React and native control components.
   * @param props Title, optional explanation/initial value, input label and confirmation callback.
   * @returns Whether the modal could be shown; cancellation submits nothing.
   */
  const showSteamUiPrompt = (ui, props) =>
    showSteamModal(ui, {
      title: props.title,
      className: "steam-ui-kit-modal",
      render: (close) => ui.react.createElement(SteamUiPromptBody, { ...props, ui, close }),
    });
  // @fragment gates/audio.ts
  /**
   * Adapts Steam audio RPCs to host state and commands without claiming a real native audio backend.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
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
    // OnAudioDeviceVolumeChanged. INPUT IS ZERO — read out of the client's own enum (Input=0,
    // Output=1) on 2026-08-30, after assuming the opposite: with the values swapped the
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
    //
    // Found by what it is: the one audio-store module, and the one export on it carrying the store's
    // availability flag and ingestion method, never by a module id: the September 2026 beta
    // renumbered the module the store was first verified in and the probe refused the gate.
    const AudioStoreTokens = ["SteamClient.System.Audio", "RegisterForDeviceAdded", "m_bAvailable"];
    const isAudioStore = (value) =>
      !!value &&
      typeof value === "object" &&
      "m_bAvailable" in value &&
      typeof value.RegisterOrUpdateDevice === "function";
    // One store for the gate's life: the store is a singleton and every publication asks for it. The
    // bridge shares one resolver, so a lookup that failed is simply tried again next time.
    let cachedStore = null;
    const liveStore = () => {
      if (cachedStore) return cachedStore;
      try {
        cachedStore = getWebpackRuntime("audio-store").exported(AudioStoreTokens, isAudioStore);
      } catch {
        return null;
      }
      return cachedStore;
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
      // Released first: a failed withdrawal leaves the gate installed with everything it knows, so
      // the next remove retries it instead of answering absent over a namespace still in place.
      const withdrawn = withdrawNamespace(window.SteamClient?.System, "Audio", ownedMarker);
      if (!withdrawn.ok) {
        lastError = withdrawn.error ?? "audio namespace withdrawal failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      for (const slot of Object.keys(callbacks)) callbacks[slot] = null;
      const store = liveStore();
      if (store) {
        try {
          for (const id of known) store.m_mapAudioDevices?.delete(id);
          // Only what a publication displaced; a store that was never fed keeps its own values.
          if (originalStoreState) {
            store.m_bAvailable = originalStoreState.available;
            store.m_activeOutputDeviceId = originalStoreState.output;
            store.m_activeInputDeviceId = originalStoreState.input;
          }
        } catch (error) {
          lastError = "audio store cleanup failed: " + String(error);
        }
      }
      known = [];
      lastFlOutputVolume = null;
      lastFlInputVolume = null;
      originalStoreState = null;
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
  // @fragment gates/bluetooth.ts
  /**
   * Supplies Steam Bluetooth service responses and notifications from host publications.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createBluetoothService() {
    const patchId = "steam-ui.bluetooth";
    const queryKey = ["BluetoothManagerService", "State"];
    const methodKeys = {
      marker: "__steamUiOwnedBluetoothService",
      original: "__steamUiOriginalBluetoothServiceMethod",
    };
    const replaced = new Set();
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // Steam's own device and adapter shapes, which are not ours to describe: the store reads them
    // and the host only carries them through from the state it was given.
    let latest = { is_service_available: false, adapters: [], devices: [] };
    // Resolved once, at install. Every state push invalidates through the same resolver, and removal
    // hands the methods back on the stub they were claimed on.
    let req = null;
    let stub = null;
    const serviceStub = (req) => {
      try {
        return req.exported(
          ["BluetoothManager.GetState#1"],
          (value) =>
            !!value &&
            typeof value === "object" &&
            typeof value.GetState === "function" &&
            typeof value.Pair === "function",
        );
      } catch {
        return null;
      }
    };
    const invalidate = () => invalidateQuery(req, queryKey);
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
      invalidate();
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      req = getWebpackRuntime("bluetooth-service");
      stub = serviceStub(req);
      if (!stub || typeof stub.GetState !== "function") {
        lastError = "BluetoothManagerService stub unavailable";
        return { ok: false, error: lastError };
      }
      const forward = (command) => (payload) =>
        request(patchId, command, payload ?? null).then(
          () => {
            lastError = "";
            return transportReply({ success: true });
          },
          (error) => {
            lastError = String(error);
            return transportFailure({ success: false, error: lastError });
          },
        );
      // A member claim per method, so the stub's own method is what removal hands back and a bridge
      // replaced in place reclaims its predecessor's overlay instead of wrapping it.
      const replace = (name, replacement) => {
        const claim = claimMember(stub, name, methodKeys, () => replacement);
        if (!claim.ok) throw new Error(claim.error);
        replaced.add(name);
      };
      try {
        replace("GetState", () => Promise.resolve(transportReply(latest)));
        replace("GetDeviceDetails", (payload) => {
          const id = payload?.device ?? payload?.id;
          const device = latest.devices.find((entry) => entry.id === id) ?? null;
          return Promise.resolve(transportReply({ device }));
        });
        replace("GetAdapterDetails", () =>
          Promise.resolve(transportReply({ adapter: latest.adapters[0] ?? null })),
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
        for (const name of replaced) releaseMember(stub, name, methodKeys);
        replaced.clear();
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, onState);
      invalidate();
      return { ok: true, installed: true, replaced: replaced.size };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      // Every method is released before the gate forgets it is installed, so a failed release is
      // retried by the next remove rather than left in Steam behind an "absent" answer.
      for (const name of replaced) {
        const released = releaseMember(stub, name, methodKeys);
        if (!released.ok) {
          lastError = released.error ?? "Bluetooth service method release failed";
          return { ok: false, error: lastError };
        }
        replaced.delete(name);
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      latest = { is_service_available: false, adapters: [], devices: [] };
      invalidate();
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      replaced: replaced.size,
      available: latest.is_service_available,
      devices: latest.devices.length,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("bluetooth", createBluetoothService());
  // @fragment gates/brightness.ts
  /**
   * Binds Steam brightness controls to host display state and brightness commands.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
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
    // The display settings store by what it is: the one module holding the brightness observable,
    // and the one exported class on it with a singleton Get() whose body declares that observable.
    // Client builds renumber the module (the September 2026 beta did), so no id is kept.
    const DisplayStoreTokens = ["m_flDisplayBrightness", "is_display_brightness_available"];
    const isDisplayStoreClass = (value) =>
      typeof value === "function" &&
      typeof value.Get === "function" &&
      String(value).includes("m_flDisplayBrightness");
    // One store for the gate's life: the store is a singleton and every publication and status read
    // asks for it. The bridge shares one resolver, so a lookup that failed is simply tried again.
    let cachedStore = null;
    const displayStore = () => {
      if (cachedStore) return cachedStore;
      try {
        const runtime = getWebpackRuntime("brightness-store");
        cachedStore = runtime.exported(DisplayStoreTokens, isDisplayStoreClass).Get() ?? null;
      } catch {
        return null;
      }
      return cachedStore;
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
      return released.ok;
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
      const claim = claimValue(message, field, availability, true);
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
      // Both claims are released before anything is forgotten: a failed release leaves the gate
      // installed, so the next remove retries what is left (a released claim answers ok again).
      if (!restoreSetter()) return { ok: false, error: lastError };
      const message = settings();
      if (message) {
        const released = releaseValue(message, field, availability);
        if (!released.ok) {
          lastError = released.error ?? "brightness release failed";
          return { ok: false, error: lastError };
        }
      }
      installed = false;
      ++requestVersion;
      pendingWrite = false;
      unsubscribe = endSubscription(unsubscribe);
      return message ? { ok: true, removed: true } : { ok: true, removed: true, storeGone: true };
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
  // @fragment gates/elements.ts
  /**
   * Exposes the shared JSX transform claim to scripts outside this bundle.
   * @returns Named transform registration, removal and ownership checks; registrations belong to this bridge.
   */
  function createElementsGate() {
    // The bridge's shared resolver, so no chunk is pushed on every registration and check.
    const runtime = () => getWebpackRuntime("elements").resolve([...JsxRuntimeTokens]);
    const validName = (name) => typeof name === "string" && name.length > 0;
    const register = (name, transform) => {
      if (!validName(name) || typeof transform !== "function") {
        return { ok: false, error: "invalid element transform" };
      }
      try {
        return interceptElements(runtime(), name, transform);
      } catch (error) {
        return { ok: false, error: String(error) };
      }
    };
    const unregister = (name) => {
      if (!validName(name)) return { ok: false, error: "invalid element transform name" };
      try {
        return releaseElements(runtime(), name);
      } catch (error) {
        return { ok: false, error: String(error) };
      }
    };
    const registered = (name) => {
      try {
        return validName(name) && elementsIntercepted(runtime(), name);
      } catch {
        return false;
      }
    };
    return { register, unregister, registered };
  }
  registerGate("elements", createElementsGate());
  // @fragment gates/extensions-tab.ts
  /**
   * Contributes the host Extensions tab through the shared Quick Access memo claim.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createExtensionsTab() {
    const patchId = "steam-ui.extensions-tab";
    const claimKeys = {
      marker: "__steamUiExtensionsTabClaimed",
      original: "__steamUiExtensionsTabOriginal",
    };
    const QamToken = "QuickAccessMenuBrowserView";
    // The tab's identity in Steam's strip, a number like Valve's own (Notifications 0, Friends 3,
    // Settings 4, Perf 5, Help 6, Music 7): the strip's activeTab is compared to it. Clear of Valve's
    // and of decky-loader's 999 so the two can coexist.
    const ExtensionsTabId = 1010;
    const ExtensionsTabTitle = "Extensions";
    // How the tab is both drawn and selected, in two steps that are each necessary:
    //   - it is pushed into Valve's own tab array in place, as decky-loader does, which is what the
    //     strip and the content draw from;
    //   - the component rendering those two validates the store's active tab against a list it built
    //     itself and falls back to the first entry when ours is absent from it, so whenever Steam's
    //     store names our tab, the strip and the content are handed it as the active one directly.
    // The store is read by the names Valve gives it; null when it is not where Valve keeps it today.
    const activeQuickAccessTab = () => {
      try {
        return window.SteamUIStore?.ActiveWindowInstance?.MenuStore?.GetQuickAccessTab?.() ?? null;
      } catch {
        return null;
      }
    };
    const withOurTabActive = (element) =>
      (activeQuickAccessTab() === ExtensionsTabId ||
        pluginFrontendItems("tab").some((slot) => slot.tabKey === activeQuickAccessTab())) &&
      element.props.activeTab !== activeQuickAccessTab()
        ? react.cloneElement(element, { activeTab: activeQuickAccessTab() })
        : element;
    // Element depth within one render pass, reset at every wrapped component. Measured on the
    // 2026-09-24 client at nineteen component-typed levels from the component carrying
    // onFocusNavDeactivated to the element holding the tab list.
    const MaximumDescent = 32;
    // What the tab draws with, every piece Steam's own: the panel's PanelSection and PanelSectionRow,
    // its DialogButton, and the settings fields `renderSteamSettingRow` draws a setting with. All of
    // them are required. A client missing one refuses the tab, like every surface here, rather than
    // drawing an imitation that looks and navigates unlike the tabs beside it.
    const ExtensionsTabRequired = [
      "react",
      "focusable",
      "dialogButton",
      "toggleField",
      "dropdown",
      "sliderField",
      "textField",
      "smallButton",
      "valueField",
    ];
    let runtime;
    let react;
    let ui = null;
    let panel = null;
    let icon = null;
    let memo = null;
    let installed = false;
    let unsubscribe = null;
    let unsubscribeFolds = null;
    let unsubscribePlugins = null;
    let nextPluginTabKey = 1100;
    const pluginTabLists = new Set();
    let desired = { items: [], revision: 0 };
    // The folds, shared with the Performance and Quick Settings groups: the host publishes the
    // sections the user opened, and every section and every switch's settings start folded.
    const folds = createSteamFolds();
    let openSections = null;
    let lastOutcome = "never rendered";
    let lastError = "";
    const descenderCache = new Map();
    const mounted = createMountedAdoption();
    // Valve's tab array the tab was last pushed into, so removal can take it out again.
    let insertedInto = null;
    // Only what the renderer needs to draw a row: the right types, and text where a label goes. No
    // counts and no lengths: the publication is delivered in parts however large it is, and a cap here
    // only ever threw real content away. A theme set with 160 settings made the whole Themes section
    // vanish. Anything malformed drops alone, never the item it sits in.
    const text = (value) => typeof value === "string" && value.length > 0;
    const optionalText = (value) =>
      value === undefined || value === null || typeof value === "string";
    const optionalFlag = (value) =>
      value === undefined || value === null || typeof value === "boolean";
    const textList = (value) =>
      Array.isArray(value) && value.every((entry) => typeof entry === "string");
    const validAction = (action) => action && text(action.id) && text(action.label);
    const validSetting = (setting) =>
      setting &&
      text(setting.key) &&
      text(setting.label) &&
      ["boolean", "number", "text", "secret", "order", "color"].includes(setting.kind) &&
      optionalText(setting.description) &&
      optionalText(setting.parent) &&
      optionalFlag(setting.highlight) &&
      (setting.choices === undefined || setting.choices === null || textList(setting.choices)) &&
      (setting.choiceLabels === undefined ||
        setting.choiceLabels === null ||
        (textList(setting.choiceLabels) &&
          Array.isArray(setting.choices) &&
          setting.choiceLabels.length === setting.choices.length));
    // An item keeps whatever of it can be drawn. Only one without an identity and a name is dropped,
    // or one with a revision the configure command refuses, since every row in it would be dead.
    const usableItem = (item) =>
      item &&
      text(item.id) &&
      typeof item.name === "string" &&
      typeof item.version === "string" &&
      typeof item.status === "string" &&
      Number.isSafeInteger(item.configurationRevision ?? 0) &&
      (item.configurationRevision ?? 0) >= 0;
    const drawable = (item) => ({
      ...item,
      actions: Array.isArray(item.actions) ? item.actions.filter(validAction) : [],
      settings: Array.isArray(item.settings) ? item.settings.filter(validSetting) : [],
      detail: typeof item.detail === "string" ? item.detail : undefined,
    });
    // An element whose own props carry the tab list, with our tab in it; null for any other element.
    // Steam's tab view is private, so the list is matched by content rather than by a path into the
    // tree. The strip and the content each carry the same array, so the second visit finds the tab
    // already present.
    const insertTab = (element, visible) => {
      const tabs = element.props?.tabs;
      if (!Array.isArray(tabs)) return null;
      if (!tabs.some((tab) => tab?.steamUiExtensionsTab || [4, 5, 6].includes(tab?.key)))
        return null;
      pluginTabLists.add(tabs);
      for (let index = tabs.length - 1; index >= 0; index--)
        if (tabs[index]?.steamUiPluginTab) tabs.splice(index, 1);
      for (const slot of pluginFrontendItems("tab")) {
        slot.tabKey ??= nextPluginTabKey++;
        tabs.push({
          key: slot.tabKey,
          title: react.createElement("div", null, slot.value.title),
          strTitle: slot.value.title,
          tab: slot.value.icon ?? icon("extensions", 22),
          steamUiPluginTab: slot.id,
          initialVisibility: !!visible,
          panel: slot.element(react, {}),
        });
      }
      const existing = tabs.filter((tab) => tab && tab.steamUiExtensionsTab === true);
      if (existing.length > 1) {
        lastOutcome = `tabs=${tabs.length} extensions=ambiguous`;
        return element;
      }
      if (existing.length === 0) {
        // Valve's tabs carry both: the element the header draws and the string it is named by. The
        // element is Valve's own title element with our text in it, so the tab's heading is drawn
        // the size and colour Valve's are, under a class this code never names; a client whose
        // title is not a plain element gets a plain div.
        const sample = tabs.find(
          (tab) => tab && react.isValidElement(tab.title) && typeof tab.title.type === "string",
        );
        tabs.push({
          key: ExtensionsTabId,
          title: sample
            ? react.cloneElement(sample.title, { key: undefined }, ExtensionsTabTitle)
            : react.createElement("div", null, ExtensionsTabTitle),
          strTitle: ExtensionsTabTitle,
          tab: icon("extensions", 22),
          steamUiExtensionsTab: true,
          initialVisibility: !!visible,
          panel: react.createElement(ExtensionsTabPanel, { key: "steam-ui.extensions-panel" }),
        });
        insertedInto = tabs;
      }
      lastOutcome = `tabs=${tabs.length} extensions=${existing.length ? "present" : "added"}`;
      return withOurTabActive(element);
    };
    // A published setting as the settings renderer's row, so a setting here is drawn by the same code,
    // and looks the same, as one on a host's settings page. Null for a setting no row can show.
    const settingRow = (item, setting) => {
      const key = `${item.id}:${setting.key}`;
      // A highlighted description is drawn in the kit's accent: "Update available", for one.
      const description =
        setting.description && setting.highlight
          ? react.createElement(
              "span",
              { className: "steam-ui-kit-highlight" },
              setting.description,
            )
          : (setting.description ?? undefined);
      // A choice is sent back by its value and shown by its label, when the host gave one.
      const labels = Array.isArray(setting.choiceLabels) ? setting.choiceLabels : setting.choices;
      const choices = Array.isArray(setting.choices)
        ? setting.choices.map((choice, index) => ({
            value: choice,
            label: labels?.[index] ?? choice,
          }))
        : null;
      switch (setting.kind) {
        case "boolean":
          return {
            key,
            label: setting.label,
            description,
            kind: "boolean",
            checked: !!setting.booleanValue,
          };
        case "order": {
          if (!choices) return null;
          const saved = String(setting.textValue ?? "")
            .split(",")
            .filter((choice) => setting.choices.includes(choice));
          return {
            key,
            label: setting.label,
            description,
            kind: "order",
            choices,
            order: [...new Set([...saved, ...setting.choices])],
          };
        }
        case "number":
          // A number with choices is one of them by index: a slider stepping through the choices,
          // each named on its notch, the way CSSLoader draws a theme's slider patch.
          if (choices && choices.length > 1) {
            return {
              key,
              label: setting.label,
              description,
              kind: "range",
              number: setting.numberValue ?? 0,
              minimum: 0,
              maximum: choices.length - 1,
              labels,
            };
          }
          return Number.isFinite(setting.minimum) && Number.isFinite(setting.maximum)
            ? {
                key,
                label: setting.label,
                description,
                kind: "range",
                number: setting.numberValue ?? setting.minimum,
                minimum: setting.minimum,
                maximum: setting.maximum,
              }
            : {
                key,
                label: setting.label,
                description,
                kind: "text",
                text: String(setting.numberValue ?? ""),
              };
        case "secret":
          // A secret's current value is never published, so its box starts empty.
          return { key, label: setting.label, description, kind: "secret" };
        case "color":
          return {
            key,
            label: setting.label,
            description,
            kind: "color",
            text: setting.textValue ?? "",
          };
        default:
          return choices
            ? {
                key,
                label: setting.label,
                description,
                kind: "choice",
                choices,
                text: setting.textValue ?? "",
                // Below its label, as CSSLoader draws a patch: the panel is too narrow for a
                // dropdown beside one.
                layout: "below",
              }
            : {
                key,
                label: setting.label,
                description,
                kind: "text",
                text: setting.textValue ?? "",
              };
      }
    };
    // What a row's value means to the host: an order is sent as its comma-joined list, and a number
    // typed into a box as a number. Undefined when the value is not one the setting can take.
    const settingValue = (setting, value) => {
      if (setting.kind === "order") return Array.isArray(value) ? value.join(",") : undefined;
      if (setting.kind === "number") {
        const number = Number(value);
        return Number.isFinite(number) ? number : undefined;
      }
      return value;
    };
    function ExtensionsTabPanel() {
      const [, setRevision] = react.useState(0);
      // The settings page's draft keeping. A draft belongs to the value its row was published with,
      // so a newer configuration that changes the row replaces it, one that changes another row
      // keeps it, and a refused save drops it and says why on the row.
      const drafts = useSteamSettingDrafts(
        react,
        desired.items.map((item) => item.configurationRevision ?? 0).join(","),
      );
      const redraw = () => setRevision((value) => value + 1);
      react.useEffect(() => subscribe(patchId, redraw), []);
      const h = react.createElement;
      const items = desired.items;
      const activate = (id) => {
        void request(patchId, "activate", { id }).then(
          (answer) => {
            // An action may answer with a page to open. The panel is closed first: this tab is
            // rendered inside the Quick Access flyout, so navigating with it open leaves the page
            // behind the panel, which on a controller is indistinguishable from a dead button.
            if (answer?.route && closeSteamSideMenus()) navigateSteamRoute(answer.route);
          },
          () => {
            // The host's refusal is already logged and the row remains truthful on the next state
            // publication. A rejected click must not tear down the whole Quick Access panel.
          },
        );
      };
      // The row renderer's change: a committed value is sent against the configuration it was made
      // against. A value the setting cannot take is refused here rather than sent to be refused.
      const change = (item, setting) =>
        drafts.change((_row, value) => {
          const sent = settingValue(setting, value);
          if (sent === undefined)
            return Promise.reject(new Error("Not a value this setting can take"));
          return request(patchId, "configure", {
            id: item.id,
            key: setting.key,
            value: sent,
            revision: item.configurationRevision ?? 0,
          });
        });
      const draftOf = (item, setting) => {
        const row = settingRow(item, setting);
        return row ? drafts.draft(row) : undefined;
      };
      const settingControl = (item, setting) => {
        const row = settingRow(item, setting);
        if (!row) return null;
        return renderSteamSettingRow(
          ui,
          drafts.row(row),
          drafts.draft(row),
          change(item, setting),
          () => {},
        );
      };
      // Whether a switch is on, as the user last set it or as the host published it.
      const switchOn = (item, setting) => {
        const draft = draftOf(item, setting);
        return draft !== undefined ? !!draft : !!setting.booleanValue;
      };
      const settingLine = (item, setting) => {
        const control = settingControl(item, setting);
        if (!control) return null;
        return h(
          panel.row,
          { key: `setting-${setting.key}` },
          setting.parent ? h("div", { className: "steam-ui-kit-nested" }, control) : control,
        );
      };
      const detailOf = (item) =>
        [item.version, item.status, item.detail].filter((part) => !!part).join(" · ");
      const isFolded = (id) => folds.isFolded(openSections, id);
      const foldHeading = (id, props) =>
        renderSteamUiHeader(ui, {
          ...props,
          collapsed: isFolded(id),
          onToggle: () => folds.setFolded(id, !isFolded(id), redraw),
        });
      // A section is headed by a focusable row rather than the section's own title: the title Steam
      // draws cannot take focus, and a controller has to be able to land on the fold. It is drawn as
      // a title with a caret, not as a button, so a folded section reads as a heading.
      const header = (item) =>
        h(
          panel.row,
          { key: "header" },
          foldHeading(`extensions:${item.id}`, { title: item.name, detail: detailOf(item) }),
        );
      // Actions in the kit's grid: two short labels side by side, a long one across the row, rather
      // than every action being a full-width bar of its own.
      const actionsRow = (item) =>
        (item.actions ?? []).length
          ? h(
              panel.row,
              { key: "actions" },
              renderSteamUiActions(
                ui,
                item.actions.map((action) => ({
                  id: action.id,
                  label: action.label,
                  onClick: () => activate(action.id),
                })),
              ),
            )
          : null;
      // One line per setting, in the order published. A setting with a parent is drawn under that
      // switch, only while it is on, and a switch's settings fold under a small heading of their own
      // that names how many there are; a parent that is not a switch on the item hides the setting,
      // since nothing could open it.
      const settingLines = (item) => {
        const settings = item.settings ?? [];
        const children = new Map();
        for (const setting of settings) {
          if (!setting.parent) continue;
          if (!children.has(setting.parent)) children.set(setting.parent, []);
          children.get(setting.parent).push(setting);
        }
        return settings.flatMap((setting) => {
          if (setting.parent) return [];
          const line = settingLine(item, setting);
          const under = setting.kind === "boolean" ? (children.get(setting.key) ?? []) : [];
          if (!under.length || !switchOn(item, setting)) return [line];
          const id = `extensions:${item.id}:${setting.key}`;
          const heading = h(
            panel.row,
            { key: `fold-${setting.key}` },
            h(
              "div",
              { className: "steam-ui-kit-nested" },
              foldHeading(id, {
                title: under.length === 1 ? "1 setting" : `${under.length} settings`,
                sub: true,
              }),
            ),
          );
          return [
            line,
            heading,
            ...(isFolded(id) ? [] : under.map((child) => settingLine(item, child))),
          ];
        });
      };
      // One PanelSection per extension, one PanelSectionRow per line in it, the way Valve's own tabs
      // and decky's plugin list lay theirs out, each drawn as a kit block so the sections read as
      // the groups on the Performance and Quick Settings tabs do. Steam titles the tab itself, so the
      // panel adds no heading of its own.
      const sections = items.map((item) =>
        h(
          panel.section,
          { key: item.id },
          header(item),
          actionsRow(item),
          ...(isFolded(`extensions:${item.id}`) ? [] : settingLines(item)),
        ),
      );
      return h(
        "div",
        { className: "steam-ui-extensions-tab steam-ui-kit-blocks" },
        steamUiKitStyle(react),
        sections.length
          ? sections
          : h(
              panel.section,
              { key: "empty" },
              h(panel.row, null, "No Steam UI extensions are installed."),
            ),
      );
    }
    const tabDescender = (type) =>
      function SteamUiExtensionsTabDescend(props) {
        return descend(type(props), 0, props?.visible);
      };
    // One traversal: the tab list stops it, a function component is entered through a wrapper that
    // keeps descending, and anything else — a context provider, a host element, the portal the
    // menu's body is drawn through — is descended through its children.
    const descend = (element, depth, visible) => {
      if (depth > MaximumDescent) return element;
      if (isPortal(element)) {
        return mapPortalChildren(react, element, (kid) => descend(kid, depth + 1, visible));
      }
      if (!react.isValidElement(element)) return element;
      return (
        insertTab(element, visible) ??
        descendInto(react, element, descenderCache, tabDescender) ??
        mapChildren(react, element, (kid) => descend(kid, depth + 1, visible))
      );
    };
    const resolve = () => {
      runtime = getWebpackRuntime("extensions-tab");
      ui = resolveSteamSettingsComponents(runtime);
      panel = resolveSteamPanelComponents(runtime);
      const missing = ExtensionsTabRequired.filter((name) => !ui?.[name]);
      if (!panel) missing.push("panel section and row");
      if (missing.length) {
        lastError = `Native Steam components unavailable: ${missing.join(", ")}`;
        ui = null;
        panel = null;
        return false;
      }
      react = ui.react;
      icon = createIconRenderer(react);
      const qam = runtime.findUnique([QamToken]);
      if (!qam) {
        lastError = "Quick Access module was not a unique match";
        return false;
      }
      const exports = runtime(qam[0]);
      // Through the gate's own claim, or a re-resolve while the claim is held finds no memo.
      const candidates = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return (
          value &&
          typeof value === "object" &&
          sourceMatches(unclaimedValue(value.type, claimKeys), [QamToken])
        );
      });
      if (candidates.length !== 1) {
        lastError = `Quick Access memo was ${candidates.length ? "ambiguous" : "absent"}`;
        return false;
      }
      memo = exports[candidates[0]];
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      if (
        !attemptResolution(
          resolve,
          (error) => (lastError = "Extensions tab resolution failed: " + String(error)),
        )
      ) {
        return { ok: false, error: lastError };
      }
      const claim = claimMember(memo, "type", claimKeys, (original) => {
        if (typeof original !== "function") return original;
        return function SteamUiExtensionsTabRoot(props) {
          return descend(original(props), 0, props?.visible);
        };
      });
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      // The claim reaches the next mount only, and the Quick Access view is mounted at boot and kept.
      mounted.adopt(memo, memo.type);
      unsubscribe = subscribe(patchId, (state) => {
        const items = Array.isArray(state?.items)
          ? state.items.filter(usableItem).map(drawable)
          : [];
        const next = {
          items,
          revision: Number.isSafeInteger(state?.revision) ? state.revision : 0,
        };
        // The wrapper reads `desired` from its closure, so a publication changes nothing React can
        // see on its own, and an unchanged one needs no render at all.
        if (!publicationChanged(desired, next)) return;
        desired = next;
        mounted.rerender();
      });
      // The folds arrive on their own publication and redraw the tab the same way.
      unsubscribeFolds = subscribe(SteamFoldsPatchId, (state) => {
        openSections = folds.normalize(state);
        mounted.rerender();
      });
      unsubscribePlugins = subscribePluginFrontends(() => {
        for (const tabs of pluginTabLists)
          for (let index = tabs.length - 1; index >= 0; index--)
            if (
              tabs[index]?.steamUiPluginTab &&
              !pluginFrontendItems("tab").some((slot) => slot.id === tabs[index].steamUiPluginTab)
            )
              tabs.splice(index, 1);
        mounted.rerender();
      });
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    // Ownership is given up before the gate forgets it owns anything: a failed release otherwise
    // leaves the claim live while every later remove() answers `absent` and never retries it.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseMember(memo, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "Extensions tab release failed";
        return { ok: false, error: lastError };
      }
      mounted.release(memo.type);
      // Out of Valve's array again: removal restores exactly what was displaced.
      if (insertedInto) {
        const at = insertedInto.findIndex((tab) => tab && tab.steamUiExtensionsTab === true);
        if (at >= 0) insertedInto.splice(at, 1);
        insertedInto = null;
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      unsubscribeFolds = endSubscription(unsubscribeFolds);
      unsubscribePlugins = endSubscription(unsubscribePlugins);
      for (const tabs of pluginTabLists)
        for (let index = tabs.length - 1; index >= 0; index--)
          if (tabs[index]?.steamUiPluginTab) tabs.splice(index, 1);
      pluginTabLists.clear();
      desired = { items: [], revision: 0 };
      descenderCache.clear();
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!memo,
      nativeComponentsResolved: !!ui && !!panel,
      claimed: memberClaimed(memo, "type", claimKeys),
      items: desired.items.length,
      revision: desired.revision,
      // Whether the claim reached the views already on screen; a claim that adopted nothing is inert.
      mounted: mounted.status(),
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("extensionsTab", createExtensionsTab());
  // @fragment gates/game-context-menu.ts
  /**
   * Adds host actions to Steam game context menus while preserving native entries.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createGameContextMenu() {
    const patchId = "steam-ui.game-context-menu";
    const renderClaimKeys = {
      marker: "__steamUiGameContextMenuRenderClaimed",
      original: "__steamUiGameContextMenuRenderOriginal",
    };
    const MenuTokens = ["GetTargetApps", "BuildManageSubmenu", "GetPrimaryActionMenuItem"];
    // How deep into Steam's own menu tree the item list is looked for; it bounds the walk, not the items.
    const MaximumDepth = 10;
    let runtime;
    let react;
    let menuComponent = null;
    let jsxRuntime;
    let installed = false;
    let unsubscribe = null;
    let desired = { items: [], revision: 0 };
    let lastOutcome = "never rendered";
    let lastError = "";
    const validItem = (item) =>
      item &&
      typeof item.id === "string" &&
      item.id.length > 0 &&
      typeof item.label === "string" &&
      item.label.length > 0;
    const appIdFor = (instance) => {
      try {
        const apps = instance?.GetTargetApps?.();
        const appId = Array.isArray(apps) && apps.length === 1 ? apps[0]?.appid : null;
        return Number.isSafeInteger(appId) && appId > 0 ? appId : null;
      } catch (error) {
        lastError = "Game menu app lookup failed: " + String(error);
        return null;
      }
    };
    // Item components are module-private. A menu already rendered at least one when it reached this
    // wrapper, so borrow its actual type rather than resolving unrelated exports by a CSS name.
    const findItemType = (element, depth = 0) => {
      if (depth > MaximumDepth || !react.isValidElement(element)) return null;
      if (typeof element.props?.onSelected === "function" && element.type) return element.type;
      for (const child of react.Children.toArray(element.props?.children)) {
        const found = findItemType(child, depth + 1);
        if (found) return found;
      }
      return null;
    };
    const containsPropertiesAction = (element, depth = 0) => {
      if (depth > MaximumDepth || !react.isValidElement(element)) return false;
      if (
        typeof element.props?.onSelected === "function" &&
        String(element.props.onSelected).includes("AppProperties")
      ) {
        return true;
      }
      return react.Children.toArray(element.props?.children).some((child) =>
        containsPropertiesAction(child, depth + 1),
      );
    };
    const insertItems = (root, appId) => {
      if (!react.isValidElement(root) || desired.items.length === 0) return root;
      const itemType = findItemType(root);
      if (!itemType) {
        lastOutcome = "menu item type absent";
        return root;
      }
      const children = react.Children.toArray(root.props?.children);
      if (children.length === 0) {
        lastOutcome = "menu root had no children";
        return root;
      }
      const ownItems = desired.items.map((item) =>
        react.createElement(
          itemType,
          {
            key: `steam-ui-game-context-menu-${item.id}`,
            onSelected: () => {
              void request(
                patchId,
                "activate",
                { appId, id: item.id },
                nextActionGeneration(patchId),
              ).then(
                (answer) => {
                  if (answer?.route) navigateSteamRoute(answer.route);
                },
                () => {
                  // A refusal stays host-authoritative and must not make Steam's menu fail.
                },
              );
            },
          },
          item.label,
        ),
      );
      const beforeProperties = children.findIndex((child) => containsPropertiesAction(child));
      const index = beforeProperties >= 0 ? beforeProperties : children.length;
      children.splice(index, 0, ...ownItems);
      lastOutcome = `app=${appId} commands=${ownItems.length} ${beforeProperties >= 0 ? "before-properties" : "appended"}`;
      return react.cloneElement(root, undefined, children);
    };
    const resolve = () => {
      runtime = getWebpackRuntime("game-context-menu");
      react = resolveReact(runtime);
      if (!react) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      const menu = runtime.findUnique(MenuTokens);
      if (!menu) {
        lastError = "Game context menu module was not a unique match";
        return false;
      }
      jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
      if (!jsxRuntime) {
        lastError = "JSX runtime was not a unique match";
        return false;
      }
      return true;
    };
    const claimMenuRender = (candidate) => {
      if (
        !candidate ||
        !candidate.prototype ||
        typeof candidate.prototype.render !== "function" ||
        menuComponent === candidate
      ) {
        return;
      }
      if (menuComponent) {
        lastError = "Game context menu component changed while claimed";
        return;
      }
      const claim = claimMember(candidate.prototype, "render", renderClaimKeys, (original) => {
        if (typeof original !== "function") return original;
        return function SteamUiGameContextMenuRender(...args) {
          const root = original.apply(this, args);
          const appId = appIdFor(this);
          return appId === null ? root : insertItems(root, appId);
        };
      });
      if (!claim.ok) {
        lastError = "Game context menu render claim failed: " + claim.error;
        return;
      }
      menuComponent = candidate;
      lastError = "";
    };
    // SharedJSContext owns React but has no visible DOM. Observe the existing shared JSX claim:
    // the private class passes through it before its first render, so that same opening gets rows.
    // Every element Steam creates passes through that claim, so the transform is withdrawn as soon as
    // the class is held: from then on the render claim does the work.
    const captureMenu = (_create, candidate) => {
      if (menuComponent || typeof candidate !== "function") return;
      const prototype = candidate.prototype;
      if (
        prototype &&
        typeof prototype.render === "function" &&
        MenuTokens.every((name) => typeof prototype[name] === "function")
      ) {
        claimMenuRender(candidate);
        if (menuComponent) {
          const released = releaseElements(jsxRuntime, patchId);
          if (!released.ok) lastError = released.error ?? "Game context menu JSX release failed";
        }
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      if (
        !attemptResolution(
          resolve,
          (error) => (lastError = "Game context menu resolution failed: " + String(error)),
        )
      ) {
        return { ok: false, error: lastError };
      }
      const claim = interceptElements(jsxRuntime, patchId, captureMenu);
      if (!claim.ok) {
        lastError = claim.error ?? "Game context menu JSX interception failed";
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        const items = Array.isArray(state?.items) ? state.items.filter(validItem) : [];
        desired = { items, revision: Number.isSafeInteger(state?.revision) ? state.revision : 0 };
      });
      return { ok: true, installed: true, observing: true };
    };
    // Both claims go back before the gate forgets it holds them. Clearing `installed` and
    // `menuComponent` first would answer `absent` on every later remove() while the render claim and
    // the JSX interception were still live, with nothing left that names what to release.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const releasedElements = releaseElements(jsxRuntime, patchId);
      const releasedRender = releaseMember(menuComponent?.prototype, "render", renderClaimKeys);
      if (!releasedRender.ok || !releasedElements.ok) {
        lastError =
          releasedRender.error ?? releasedElements.error ?? "Game context menu release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      desired = { items: [], revision: 0 };
      menuComponent = null;
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!runtime && !!react,
      // Watching for the menu class, or holding it once it was seen.
      observing:
        installed &&
        (memberClaimed(menuComponent?.prototype, "render", renderClaimKeys) ||
          elementsIntercepted(jsxRuntime, patchId)),
      menuClaimed: memberClaimed(menuComponent?.prototype, "render", renderClaimKeys),
      items: desired.items.length,
      revision: desired.revision,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("gameContextMenu", createGameContextMenu());
  // @fragment gates/home-carousel.ts
  /**
   * Replaces only the matched home-carousel collection using shared memo ownership.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createHomeCarousel() {
    const patchId = "steam-ui.home-carousel";
    const claimKeys = {
      marker: "__steamUiHomeCarouselClaimed",
      original: "__steamUiHomeCarouselOriginal",
    };
    const HomeTokens = ["HomeTabsActive", "HomeActiveTab"];
    const CarouselTokens = ["#Showcase_RecentGames", "RecentGamesContainer"];
    const KnownRoute = "/library/home";
    // Valve's collection ids, the string values of its own enum.
    const InstalledCollection = "local-install";
    const PurchasedCollection = "recent-purchased";
    const OwnedCollection = "my-games";
    const MaximumDescent = 12;
    // Fiber reads are cheap and the walk runs once per install; the bound stops a cyclic or runaway
    // tree, not a legitimate search through a window with a whole library mounted.
    const MaximumNodesVisited = 250000;
    const ContainerClass = "steam-ui-home-carousel";
    let runtime;
    let react;
    let home = null;
    let useObserver = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    // The host's instruction, replaced whole on each publication.
    let policy = { includeUninstalled: false, disconnected: new Set() };
    const local = createLocalStore();
    let lastOutcome = "never rendered";
    let lastReport = "";
    // The Homes on screen at install, kept so status counts them without walking the tree.
    const mounted = createMountedAdoption(MaximumNodesVisited);
    let cached = null;
    const carouselChecks = new WeakMap();
    const carouselCache = new Map();
    const recentGamesCache = new Map();
    const collectionStore = () => window.collectionStore;
    const appStore = () => window.appStore;
    const collection = (id) => {
      try {
        return collectionStore()?.GetCollection(id) ?? null;
      } catch {
        return null;
      }
    };
    // One empty list for every absent input, so an absent input keys the list the same each render.
    const NoApps = Object.freeze([]);
    const appsOf = (source) => {
      try {
        const apps = source?.visibleApps;
        return Array.isArray(apps) ? apps : NoApps;
      } catch {
        return NoApps;
      }
    };
    const overviewOf = (appid) => {
      try {
        return appStore()?.GetAppOverviewByAppID(appid) ?? null;
      } catch {
        return null;
      }
    };
    const call = (overview, name) =>
      typeof overview?.[name] === "function" ? overview[name]() === true : false;
    const lastPlayed = (overview) =>
      Math.max(overview.rt_last_time_locally_played || 0, overview.rt_last_time_played || 0);
    // Steam's own installed test for its local-games collection. A game on a card that is not in the
    // reader reads false here, which is the same fact the library badge greys on.
    const isInstalled = (overview) =>
      overview.local_per_client_data?.installed === true || call(overview, "BIsShortcut");
    // The same exclusions Steam's own list applies, plus the host's disconnected libraries.
    const eligible = (overview) =>
      !!overview &&
      Number.isInteger(overview.appid) &&
      overview.appid > 0 &&
      !policy.disconnected.has(overview.appid) &&
      !call(overview, "BIsMusicAlbum") &&
      !(call(overview, "BIsApplicationOrTool") && !(overview.minutes_playtime_forever > 0));
    // The carousel's inputs: each collection's apps, read inside Steam's observer so a recomputed
    // collection re-renders the carousel. The arrays, not the collections, are what the list is keyed
    // on: a collection object never changes identity, so an install or uninstall left the list as it
    // was until Steam's own recent list happened to change.
    const readInputs = () => ({
      installed: appsOf(collection(InstalledCollection)),
      purchased: appsOf(collection(PurchasedCollection)),
      owned: policy.includeUninstalled ? appsOf(collection(OwnedCollection)) : NoApps,
    });
    // Builds the list, or returns the last one when none of its inputs changed.
    //
    // Order:
    //   1. Steam's own running-game prefix, when it has one: the running game and its separator.
    //   2. The most recently played installed game, pinned first, as Steam pins it.
    //   3. Installed games by last played, merged with unplayed recent purchases by purchase time,
    //      so a new purchase sits among the games played around when it was bought.
    //   4. Installed games never played, newest install first.
    //   5. With the host's permission, owned games that are not installed.
    // Ties fall back to app id, so the order is deterministic.
    const listFor = (steamGames, inputs) => {
      const key = [local.revision(), steamGames, inputs.installed, inputs.purchased, inputs.owned];
      if (
        cached &&
        cached.key.length === key.length &&
        cached.key.every((value, index) => value === key[index])
      ) {
        return cached;
      }
      const prefix = steamGames.length > 1 && steamGames[1] === 0 ? [steamGames[0], 0] : [];
      const placed = new Set(prefix);
      const pool = new Map();
      for (const overview of inputs.installed) {
        if (eligible(overview) && isInstalled(overview)) pool.set(overview.appid, overview);
      }
      // Steam's own list carries played shortcuts, which its installed collection leaves out.
      for (const appid of steamGames) {
        if (!appid || placed.has(appid) || pool.has(appid)) continue;
        const overview = overviewOf(appid);
        if (eligible(overview) && isInstalled(overview)) pool.set(appid, overview);
      }
      const purchases = new Map();
      for (const overview of inputs.purchased) {
        if (eligible(overview) && lastPlayed(overview) === 0)
          purchases.set(overview.appid, overview);
      }
      const timed = [];
      const unplayed = [];
      for (const [appid, overview] of pool) {
        if (placed.has(appid) || purchases.has(appid)) continue;
        const time = lastPlayed(overview);
        if (time > 0) timed.push({ appid, time, purchase: false });
        else unplayed.push({ appid, time: overview.rt_last_time_played_or_installed || 0 });
      }
      for (const [appid, overview] of purchases) {
        if (placed.has(appid)) continue;
        timed.push({ appid, time: overview.rt_purchased_time || 0, purchase: true });
      }
      timed.sort((left, right) => right.time - left.time || left.appid - right.appid);
      const pinned = timed.findIndex((entry) => !entry.purchase);
      if (pinned > 0) timed.unshift(timed.splice(pinned, 1)[0]);
      unplayed.sort((left, right) => right.time - left.time || left.appid - right.appid);
      const uninstalled = [];
      if (policy.includeUninstalled) {
        for (const overview of inputs.owned) {
          if (!eligible(overview) || isInstalled(overview)) continue;
          if (
            placed.has(overview.appid) ||
            pool.has(overview.appid) ||
            purchases.has(overview.appid)
          ) {
            continue;
          }
          uninstalled.push({
            appid: overview.appid,
            time: lastPlayed(overview),
            bought: overview.rt_purchased_time || 0,
          });
        }
        uninstalled.sort(
          (left, right) =>
            right.time - left.time || right.bought - left.bought || left.appid - right.appid,
        );
      }
      let list = [
        ...prefix,
        ...timed.map((entry) => entry.appid),
        ...unplayed.map((entry) => entry.appid),
        ...uninstalled.map((entry) => entry.appid),
      ];
      // Nothing on the attached libraries is still an answer, but an empty carousel is not one the
      // page can draw: Steam's box carousel renders nothing for an empty list. Steam's own list stands
      // in rather than leaving Home blank.
      const fellBack = list.length === prefix.length && steamGames.length > prefix.length;
      if (fellBack) list = steamGames.slice();
      // Grey means not installed, whichever section put the game there: an unplayed purchase that is
      // still downloading reads the same as an owned game that was never installed.
      const grey = fellBack
        ? []
        : list.filter((appid) => appid > 0 && !pool.has(appid) && !isInstalledId(appid));
      const css = grey.length
        ? `${grey.map((appid) => `.${ContainerClass} [data-id="${appid}"] img`).join(",")}` +
          "{filter:grayscale(1);opacity:.55}"
        : "";
      const counts = {
        items: list.length,
        purchases: timed.filter((entry) => entry.purchase).length,
        installed: timed.filter((entry) => !entry.purchase).length + unplayed.length,
        uninstalled: uninstalled.length,
        excluded: policy.disconnected.size,
        tracking: !!useObserver,
        fallback: fellBack,
      };
      cached = { key, list, window: steamGames.length, css, counts };
      return cached;
    };
    const isInstalledId = (appid) => {
      const overview = overviewOf(appid);
      return !!overview && isInstalled(overview);
    };
    // Tells the host what the carousel holds, once per change. This is what the host's log reads,
    // so the carousel can be checked without attaching a debugger to Steam.
    const report = (counts) => {
      const text = JSON.stringify(counts);
      if (text === lastReport) return;
      lastReport = text;
      request(patchId, "report", counts).catch(() => {});
    };
    // Replaces the carousel's list in what the carousel component rendered: the background and the
    // box carousel both receive `games`, told apart by the one prop each takes that the other does
    // not. The box carousel's own component is wrapped so its overscan can be bounded.
    const retarget = (tree, inputs) => {
      // Asserted rather than annotated: assigned inside the walk below, which control-flow narrowing
      // cannot see, so a plain `= null` would leave it typed as never after the walk.
      let result = null;
      let background = 0;
      let carousels = 0;
      const replace = (element, depth) => {
        if (depth > MaximumDescent || !react.isValidElement(element)) return element;
        const props = element.props;
        if (props && Array.isArray(props.games)) {
          result ??= listFor(props.games, inputs);
          if (props.refOnItemFocus !== undefined) {
            background++;
            return react.cloneElement(element, { games: result.list });
          }
          if (typeof props.onItemFocus === "function" || "showFeaturedItem" in props) {
            carousels++;
            return react.createElement(
              recentGamesFor(element.type),
              keyed(element, { ...props, games: result.list }),
            );
          }
        }
        return mapChildren(react, element, (kid) => replace(kid, depth + 1));
      };
      const output = replace(tree, 0);
      lastOutcome = result
        ? `background=${background} carousel=${carousels} items=${result.list.length}`
        : "no games list in the carousel's output";
      return output;
    };
    // The box carousel's own function component, rendered here so its overscan stays what Steam's own
    // list gives it, inside a container that carries the grey rules. The container is always present,
    // so toggling the rules never changes the tree's shape and remounts the carousel.
    //
    // Steam sets the overscan to its list's length, at most 20, so every tile of its own carousel is
    // mounted and loading from the start. Given the whole library, that length would mount every
    // game at once; the component's default of 3 mounted only the tiles in view, so each image
    // started loading when its tile scrolled in, and again when it came back, which is slow wherever
    // the art is not in Steam's local cache (2026-09-29, 246 games on a handheld). Steam's own length
    // keeps the first tiles loading at once and the rest loading ahead of focus.
    const recentGamesFor = (type) => {
      if (typeof type !== "function" || type.prototype?.isReactComponent) return type;
      let wrapped = recentGamesCache.get(type);
      if (wrapped) return wrapped;
      wrapped = function SteamUiRecentGames(props) {
        const rendered = type(props);
        const bounded =
          react.isValidElement(rendered) && typeof rendered.props?.overscan === "number"
            ? react.cloneElement(rendered, { overscan: cached?.window || undefined })
            : rendered;
        return react.createElement(
          "div",
          { className: ContainerClass, style: { display: "contents" } },
          react.createElement("style", { key: "steam-ui-home-carousel-grey" }, cached?.css ?? ""),
          bounded,
        );
      };
      recentGamesCache.set(type, wrapped);
      return wrapped;
    };
    const isCarousel = (type) => {
      if (!type || typeof type !== "object") return false;
      let known = carouselChecks.get(type);
      if (known === undefined) {
        const inner = type.$$typeof === ReactMemoType ? type.type : null;
        const source = typeof inner === "function" ? String(inner) : "";
        known = CarouselTokens.every((token) => source.includes(token));
        carouselChecks.set(type, known);
      }
      return known;
    };
    // The carousel memo, replaced by a memo of our own with the same comparison, so the component
    // keeps the render behavior Steam gave it.
    const carouselFor = (type) => {
      let wrapped = carouselCache.get(type);
      if (wrapped) return wrapped;
      const inner = type.type;
      const tracked = useObserver;
      const Carousel = function SteamUiHomeCarousel(props) {
        react.useSyncExternalStore(local.subscribe, local.revision);
        const inputs = tracked ? tracked(readInputs, "SteamUiHomeCarousel") : readInputs();
        const tree = inner(props);
        const output = installed ? retarget(tree, inputs) : tree;
        // Reported after the render rather than from it; `report` drops a repeat of the same counts.
        const counts = installed ? cached?.counts : null;
        react.useEffect(() => {
          if (counts) report(counts);
        }, [counts]);
        return output;
      };
      wrapped = react.memo(Carousel, type.compare ?? undefined);
      carouselCache.set(type, wrapped);
      return wrapped;
    };
    // Walks what Home rendered, by props alone, to the carousel element.
    const decorate = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      if (isCarousel(element.type)) {
        return react.createElement(carouselFor(element.type), keyed(element, element.props));
      }
      return mapChildren(react, element, (kid) => decorate(kid, depth + 1));
    };
    // Home by its own source, or a Home an earlier injection already claimed: the claim replaces
    // `type`, so requiring the source alone would make a successful apply fail its next resolution.
    const isHome = (type) =>
      !!type &&
      typeof type === "object" &&
      type.$$typeof === ReactMemoType &&
      sourceMatches(unclaimedValue(type.type, claimKeys), HomeTokens);
    // Home from the router's route list. The list is found by content — the array holding a route for
    // /library/home — and the page element under that route names the Home memo. Bounded and
    // read-only; the memo is one object whichever window renders it, so claiming it reaches them all.
    // Until Big Picture has built its tree there is no route list to find, and the patch manager
    // probes again. What the walk saw is kept for `status` and the refusal, so a miss on a new
    // client says which assumption failed without anyone attaching to Steam.
    let lastSearch = { roots: 0, visited: 0, homeRoutes: 0, page: "" };
    const findHome = () => {
      let found = null;
      const roots = reactRootFibers();
      const search = { roots: roots.length, visited: 0, homeRoutes: 0, page: "" };
      const walk = walkFibers(roots, MaximumNodesVisited, (node) => {
        // A Fragment's fiber holds its children array as the props themselves.
        const props = node.memoizedProps;
        const children = Array.isArray(props) ? props : props?.children;
        if (!isSteamRouteList(react, children, KnownRoute)) return;
        const route = children.find(
          (child) => react.isValidElement(child) && child.props?.path === KnownRoute,
        );
        if (!route) return;
        search.homeRoutes++;
        const type = route.props?.children?.type;
        search.page = !type
          ? "none"
          : typeof type === "function"
            ? "function"
            : String(type.$$typeof);
        if (isHome(type)) found = type;
        return !!found;
      });
      search.visited = walk.visited;
      lastSearch = search;
      return found;
    };
    const resolve = () => {
      runtime = getWebpackRuntime("home-carousel");
      const resolvedReact = resolveReact(runtime);
      if (!resolvedReact) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      react = resolvedReact;
      if (
        typeof react.useSyncExternalStore !== "function" ||
        typeof react.useEffect !== "function" ||
        typeof react.memo !== "function"
      ) {
        lastError = "React runtime lacks useSyncExternalStore, useEffect or memo";
        return false;
      }
      if (!runtime.findUnique(["HomeTabsActive", CarouselTokens[0]])) {
        lastError = "Home module was not a unique match";
        return false;
      }
      if (
        typeof collectionStore()?.GetCollection !== "function" ||
        typeof appStore()?.GetAppOverviewByAppID !== "function"
      ) {
        lastError = "collection or app store is unavailable";
        return false;
      }
      // Wanted, not required: without it the carousel still follows the host and Steam's own list,
      // and an install elsewhere shows on its next render. `status.tracking` says which.
      useObserver = findUseObserver(runtime);
      home = findHome();
      if (!home) {
        lastError =
          `Home was not found in the router's route list (roots=${lastSearch.roots} ` +
          `visited=${lastSearch.visited} homeRoutes=${lastSearch.homeRoutes} page=${lastSearch.page || "-"})`;
        return false;
      }
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "home carousel resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
      // Home renders through this memo wherever the router draws it. The wrapper adds no hooks, so
      // a Home already on screen can be adopted into it without remounting.
      const claim = claimMember(home, "type", claimKeys, (original) => {
        if (typeof original !== "function") return original;
        return function SteamUiHome(props) {
          const tree = original(props);
          return installed ? decorate(tree, 0) : tree;
        };
      });
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      const { adopted } = mounted.adopt(home, home.type);
      unsubscribe = subscribe(patchId, (published) => {
        const ids = Array.isArray(published?.disconnectedAppIds)
          ? published.disconnectedAppIds
          : [];
        const disconnected = new Set();
        for (const appid of ids) {
          if (Number.isInteger(appid) && appid > 0) disconnected.add(appid);
        }
        policy = {
          includeUninstalled: published?.includeUninstalled === true,
          disconnected,
        };
        local.changed();
      });
      return { ok: true, installed: true, reclaimed: claim.reclaimed, adopted };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      // Released before anything is forgotten, so a failed release stays installed and the next
      // remove retries it.
      const wrapper = home?.type;
      const released = releaseMember(home, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "home carousel release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      // A carousel on screen re-renders and hands back Steam's own list and overscan.
      policy = { includeUninstalled: false, disconnected: new Set() };
      local.changed();
      cached = null;
      lastReport = "";
      carouselCache.clear();
      recentGamesCache.clear();
      // Adopted Homes draw the original again on their next render; the memo already does.
      releaseMountedType(reactRootFibers(), home, wrapper, home.type, MaximumNodesVisited);
      mounted.release(home.type);
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!home,
      claimed: memberClaimed(home, "type", claimKeys),
      tracking: !!useObserver,
      includeUninstalled: policy.includeUninstalled,
      disconnected: policy.disconnected.size,
      counts: cached?.counts ?? null,
      search: lastSearch,
      // Homes on screen at install, and how many of them still draw something other than the
      // claim: an adoption whose render is pending, or a type React reset underneath us.
      mounted: mounted.status(),
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("homeCarousel", createHomeCarousel());
  // @fragment gates/library-badge.ts
  // A library badge on every library tile: the name of the Steam library that holds the game, drawn
  // beside Valve's own Steam Input badge in the tile's icon row.
  //
  // Mapped against the September 2026 client beta on 2026-09-11. One module carries the library tile
  // and everything it draws:
  //
  //   tile  an exported React.memo (mobx observer): the app tile, props { app, bFeatured, context, … }
  //         rendered by Home's carousel, the library grid and three other callers, all through the
  //         export, and keyed in Steam's focus tree as `appportrait_<appid>`
  //     Focusable, navKey "appportrait_<appid>"
  //       hover wrapper
  //         div.LibraryItemOverlayOuterArea > div.LibraryItemOverlayInnerArea > div.LibraryBottomItems
  //           div.LibraryItemIcons     the icon row: justify-content space-between
  //             badge                  exported: the Steam Input badge, props { overview }   <- anchor
  //
  // The badge is exported but the tile calls it by its module-local name, so claiming the badge export
  // changes nothing the tile draws. The claim is on the tile memo's `type` instead, and the badge is
  // found in what the tile RENDERS by element type — identity with the export, never a class name or
  // a minified name — and replaced by a row of two: this badge, then Valve's. The tile is drawn
  // through the export by every caller, so one claim reaches the carousel and the grid alike, and
  // the badge inherits the row's own visibility: Valve shows the icon row on the focused tile only,
  // and this badge appears and disappears with it.
  //
  // The badge names the library and says whether the game is installed, by colour: green when the
  // game is installed, grey when it is not — which is what a card being disconnected amounts to. The
  // text is the library's name alone, and the host names every library. A game that no published
  // library holds gets no badge, because there is no library to name.
  //
  // Big Art Mode is Steam's own `library_home_big_art` client setting, read from the settings store
  // the Home component itself reads it from. The badge is tile-relative and does not care, but a
  // consumer may: the gate reports the mode to the host through `homeLayout` when it first resolves
  // and whenever a tile render sees it change, and carries it in `status` for verification.
  // The host's published libraries, read once for every gate that names them, indexed by app id. A
  // malformed entry is skipped rather than failing the whole reading.
  const readLibraryBadgeState = (state) => {
    const libraries = new Map();
    const shortcuts = new Map();
    const published = Array.isArray(state?.libraries) ? state.libraries : [];
    let count = 0;
    for (const entry of published) {
      if (!entry || typeof entry.name !== "string" || !Array.isArray(entry.appIds)) continue;
      count++;
      const library = {
        name: entry.name,
        connected: entry.connected === true,
      };
      for (const appid of entry.appIds) {
        if (typeof appid !== "number" || !Number.isInteger(appid) || appid <= 0) continue;
        libraries.set(appid, library);
      }
    }
    for (const entry of Array.isArray(state?.shortcuts) ? state.shortcuts : []) {
      if (
        !Number.isInteger(entry?.appId) ||
        entry.appId <= 0 ||
        entry.appId > 0xffffffff ||
        typeof entry.available !== "boolean" ||
        typeof entry.location !== "string" ||
        typeof entry.reason !== "string"
      )
        continue;
      shortcuts.set(entry.appId, entry);
    }
    return { libraries, shortcuts, count };
  };
  // The library to name for one app overview, or null when there is none. Steam's own installed flag is
  // the authority on installed; a disconnected card's games are not installed by Steam's reckoning. The
  // published connection stands in only where the overview cannot say. A game that no published library
  // holds has no library to name.
  const libraryForOverview = (overview, reading) => {
    const appid = typeof overview?.appid === "number" ? overview.appid : null;
    if (appid === null) return null;
    const library = reading.libraries.get(appid);
    const shortcut = reading.shortcuts.get(appid);
    const installed =
      typeof overview.installed === "boolean" ? overview.installed : (library?.connected ?? false);
    if (!library && !shortcut) return null;
    return {
      appId: appid,
      name: shortcut?.location || library?.name || "Game storage",
      installed: shortcut?.available ?? installed,
      reason: shortcut?.available === false ? shortcut.reason : "",
      managed: !!shortcut,
    };
  };
  // A mounted tile or stat subscribes to its app alone. Publications update only the apps whose
  // visible badge changed, without walking Steam's fibers or repainting the rest of the library.
  const createLibraryBadgeReading = () => {
    let current = readLibraryBadgeState(null);
    const stores = new Map();
    const storeFor = (appId) => {
      let store = stores.get(appId);
      if (!store) {
        store = createLocalStore();
        stores.set(appId, store);
      }
      return store;
    };
    return {
      read: () => current,
      storeFor,
      publish: (state) => {
        const previous = current;
        current = readLibraryBadgeState(state);
        for (const [appId, store] of stores) {
          const before = libraryForOverview({ appid: appId }, previous);
          const after = libraryForOverview({ appid: appId }, current);
          if (
            before?.name !== after?.name ||
            before?.installed !== after?.installed ||
            before?.reason !== after?.reason ||
            before?.managed !== after?.managed
          ) {
            store.changed();
          }
        }
        return current;
      },
      changed: () => {
        for (const store of stores.values()) store.changed();
      },
    };
  };
  /**
   * Adds host library badges using shared element transforms and published library identity.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createLibraryBadge() {
    const patchId = "steam-ui.library-badge";
    const claimKeys = {
      marker: "__steamUiLibraryBadgeClaimed",
      original: "__steamUiLibraryBadgeOriginal",
    };
    // The module that owns the tile. `appportrait_` is the tile's own focus key and occurs in exactly
    // one module; `ControllerSupportIcon` also names the stylesheet module, so the pair is what is
    // unique. Neither is a localized string or a generated class.
    const TileTokens = ["ControllerSupportIcon", "appportrait_"];
    // The tile stylesheet's class map: Valve's own names for the icon row and the Steam Input badge,
    // mapped to whatever hashes this build emitted. Read by name, so the hashes are never written
    // down here. The badge's visibility comes from Valve's rules on the badge class — hidden until
    // the tile is focused or hovered — and the row wearing that class inherits them.
    const ClassMapTokens = SteamLibraryClassTokens;
    const BigArtSetting = "library_home_big_art";
    const MaximumDescent = 12;
    let runtime;
    let react;
    let tile = null;
    let badge = null;
    let settings = null;
    let classes = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let reportedBigArt = null;
    // The host's published libraries, replaced whole on each publication and indexed by app id.
    const local = createLibraryBadgeReading();
    let reading = local.read();
    const mounted = createMountedAdoption();
    // What the last tile render actually did, because a claimed tile can render exactly what Valve
    // shipped when the badge anchor is not in its tree. Kept as counts and the reading that render
    // saw, so a render does no string work; status builds the text.
    let outcome = "never rendered";
    let renderedReading = reading;
    let placed = 0;
    let unanchored = 0;
    const tileCache = new Map();
    const readBigArt = () => {
      try {
        const value = settings?.clientSettings?.[BigArtSetting];
        return typeof value === "boolean" ? value : null;
      } catch {
        return null;
      }
    };
    // Tells the host once per change, never once per render: the setting is read on every tile
    // render, which is how a toggle is noticed without a subscription into Valve's store.
    const reportBigArt = () => {
      const current = readBigArt();
      if (current === null || current === reportedBigArt) return;
      reportedBigArt = current;
      request(patchId, "homeLayout", { bigArt: current }).catch(() => {});
    };
    const badgeStyle = (installedNow) => ({
      display: "inline-block",
      maxWidth: "180px",
      overflow: "hidden",
      textOverflow: "ellipsis",
      whiteSpace: "nowrap",
      padding: "2px 8px",
      borderRadius: "4px",
      fontSize: "13px",
      lineHeight: "17px",
      fontWeight: 600,
      letterSpacing: "0.2px",
      color: "#f2f4f5",
      background: installedNow ? "rgba(76, 160, 54, 0.92)" : "rgba(110, 115, 120, 0.85)",
    });
    // The badge for one tile, or null when there is no library to name.
    const renderBadge = (overview) => {
      // Grey when not installed, which is exactly what a disconnected card amounts to.
      const library = libraryForOverview(overview, reading);
      if (!library) return null;
      return react.createElement(
        "span",
        {
          key: "steam-ui-library-badge",
          className: "steam-ui-library-badge",
          style: badgeStyle(library.installed),
          title: library.reason || library.name,
          "aria-label": library.name,
        },
        library.name,
      );
    };
    // Replaces Valve's badge element with a right-aligned row of ours and Valve's. The icon row is
    // `space-between` with Valve's badge pushed to its end by an auto margin, so a bare sibling would
    // land at the row's far left; one flex box holding both keeps this badge immediately left of the
    // icon wherever the row puts it.
    //
    // The box wears two of Valve's own classes. The badge class carries the visibility rule — opacity
    // zero until the tile is focused or hovered — and the end-of-row margin, so the pair appears and
    // disappears with Valve's icon instead of sitting on every tile; its size, padding and pill
    // background are overridden inline because they are drawn for a 34-pixel glyph. The row class
    // keeps Valve's icon a direct child of a row, which is what its pill background is written
    // against. Without the class map the box is plain and always visible, and status says so.
    const withBadge = (element) => {
      const ours = renderBadge(element.props?.overview);
      const additions = pluginFrontendElements("library", react, {
        overview: element.props?.overview,
      });
      if (!ours && !additions.length) return element;
      const style = {
        display: "flex",
        alignItems: "center",
        gap: "8px",
        marginInlineStart: "auto",
      };
      let className;
      if (classes) {
        className = `${classes.row} ${classes.icon}`;
        Object.assign(style, {
          justifyContent: "flex-end",
          width: "auto",
          maxWidth: "none",
          maxHeight: "none",
          padding: 0,
          borderRadius: 0,
          backgroundColor: "transparent",
        });
      }
      return react.createElement(
        "div",
        { key: "steam-ui-library-badge-row", className, style },
        ours,
        ...additions,
        element,
      );
    };
    // Descends the rendered tree by props alone. The tile's whole icon row is host elements and
    // fragments below the Focusable, so nothing has to be rendered to reach the anchor; function
    // components on the way are left untouched, which keeps every identity Valve's reconciler holds.
    const decorate = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      if (element.type === badge) {
        placed++;
        return withBadge(element);
      }
      return mapChildren(react, element, (kid) => decorate(kid, depth + 1));
    };
    // Wraps the tile's observer so its OUTPUT can be changed. Cached against the original: a fresh
    // identity on every claim would remount every tile React reconciles.
    const wrapTile = (original) => {
      let wrapped = tileCache.get(original);
      if (wrapped) return wrapped;
      wrapped = function SteamUiLibraryTile(props, secondArgument) {
        const tree = original.call(this, props, secondArgument);
        reportBigArt();
        return installed
          ? react.createElement(LiveLibraryTile, {
              tree,
              overview: props?.app ?? props?.overview,
            })
          : tree;
      };
      tileCache.set(original, wrapped);
      return wrapped;
    };
    function LiveLibraryTile(props) {
      const appId = props.overview?.appid ?? 0;
      const store = local.storeFor(appId);
      react.useSyncExternalStore(store.subscribe, store.revision, store.revision);
      if (!installed) return props.tree;
      reading = local.read();
      const before = placed;
      let result = decorate(props.tree, 0);
      const library = libraryForOverview(props.overview, reading);
      if (library?.managed && !library.installed && react.isValidElement(result)) {
        result = react.cloneElement(result, {
          style: { ...result.props.style, filter: "grayscale(1)", opacity: 0.58 },
          title: library.reason || library.name,
        });
      }
      if (placed === before) unanchored++;
      outcome = "rendered";
      renderedReading = reading;
      return result;
    }
    const resolve = () => {
      runtime = getWebpackRuntime("library-badge");
      const resolvedReact = resolveReact(runtime);
      if (!resolvedReact) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      react = resolvedReact;
      if (typeof react.useSyncExternalStore !== "function") {
        lastError = "React runtime lacks useSyncExternalStore";
        return false;
      }
      const tileFactory = runtime.findUnique([...TileTokens]);
      if (!tileFactory) {
        lastError = "library tile module was not a unique match";
        return false;
      }
      const exports = runtime(tileFactory[0]);
      // The tile is the module's one memo export; the badge is the one function export that draws
      // the controller-support icon. Both are chosen by what they are, never by their minified names.
      const tiles = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return value && typeof value === "object" && value.$$typeof === ReactMemoType;
      });
      if (tiles.length !== 1) {
        lastError = `library tile export was ${tiles.length ? "ambiguous" : "absent"}`;
        return false;
      }
      const badges = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return typeof value === "function" && String(value).includes(TileTokens[0]);
      });
      if (badges.length !== 1) {
        lastError = `Steam Input badge export was ${badges.length ? "ambiguous" : "absent"}`;
        return false;
      }
      tile = exports[tiles[0]];
      badge = exports[badges[0]];
      // The class map is wanted, not required: without it the badge still draws, on every tile
      // rather than the focused one, and `status.classesResolved` says so. Read as the tile reads
      // it — the module's export, unwrapped if it is an ES default.
      classes = null;
      const classMapFactory = runtime.findUnique([...ClassMapTokens]);
      if (classMapFactory) {
        const exported = runtime(classMapFactory[0]);
        const map = classMapOf(exported);
        const row = map?.LibraryItemIcons;
        const icon = map?.ControllerSupportIcon;
        if (typeof row === "string" && row && typeof icon === "string" && icon) {
          classes = { row, icon };
        }
      }
      // The settings store is wanted, not required: without it the badge still draws and
      // `status.bigArt` says null rather than guessing.
      settings = null;
      const settingsFactory = runtime.findUnique([...SettingsTokens]);
      if (settingsFactory) {
        const stores = runtime(settingsFactory[0]);
        const candidates = Object.keys(stores).filter((name) => {
          const value = stores[name];
          return value && typeof value === "object" && typeof value.clientSettings === "object";
        });
        if (candidates.length === 1) settings = stores[candidates[0]];
      }
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "library badge resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
      // Every caller draws the tile through the same memo, so claiming its `type` reaches the
      // carousel and the grid without patching a single caller.
      const claim = claimMember(tile, "type", claimKeys, (original) => {
        if (typeof original !== "function") return original;
        return wrapTile(original);
      });
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      mounted.adopt(tile, tile.type);
      lastError = "";
      reportedBigArt = null;
      reportBigArt();
      unsubscribe = subscribe(patchId, (state) => {
        reading = local.publish(state);
      });
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      // Released before anything is forgotten, so a failed release stays installed and the next
      // remove retries it.
      const replacement = tile.type;
      const original = unclaimedValue(replacement, claimKeys);
      const released = releaseMember(tile, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "library badge release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      mounted.release(original);
      // New mounts inherit the claimed memo without adoption. Restore those owned fibers once,
      // on removal; ordinary publications never traverse Steam's component tree.
      for (const fiber of mountedFibersOf(reactRootFibers(), tile, MaximumMountedNodes)) {
        if (fiber.type !== replacement) continue;
        retargetFiber(fiber, original);
        invalidateFiberProps(fiber);
        requestRender(fiber);
      }
      unsubscribe = endSubscription(unsubscribe);
      reading = local.publish(null);
      local.changed();
      tileCache.clear();
      outcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!tile && !!badge,
      claimed: memberClaimed(tile, "type", claimKeys),
      settingsResolved: !!settings,
      classesResolved: !!classes,
      bigArt: readBigArt(),
      libraries: reading.count,
      apps: reading.libraries.size,
      lastOutcome:
        outcome === "rendered"
          ? `placed=${placed} unanchored=${unanchored} libraries=${renderedReading.count} apps=${renderedReading.libraries.size}`
          : outcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("libraryBadge", createLibraryBadge());
  /**
   * Adds host metadata and frontend contributions to the matched game details page.
   * @returns Install/remove controls and diagnostics for the reversible details-page claim.
   */
  function createLibraryDetails() {
    const publicationId = "steam-ui.library-badge";
    const TransformName = "libraryDetails";
    const ClassMapTokens = ['GameStatsSection:"', 'PlayBarDetailLabel:"', 'LastPlayedInfo:"'];
    const RequiredClasses = [
      "GameStatsSection",
      "GameStat",
      "GameStatRight",
      "PlayBarLabel",
      "PlayBarDetailLabel",
    ];
    const LabelToken = "#Settings_Page_Library";
    const StatKey = "steam-ui-library-details";
    let runtime;
    let react = null;
    let jsxRuntime = null;
    let ui = null;
    let localize = null;
    let classes = null;
    let installed = false;
    let lastError = "";
    let lastOutcome = "never rendered";
    let placed = 0;
    let without = 0;
    let unsubscribe = null;
    const local = createLibraryBadgeReading();
    let reading = local.read();
    function LiveLibraryStat(props) {
      const store = local.storeFor(props.overview?.appid ?? 0);
      react.useSyncExternalStore(store.subscribe, store.revision, store.revision);
      if (!installed) return null;
      reading = local.read();
      const library = libraryForOverview(props.overview, reading);
      return library ? renderStat(library) : null;
    }
    const label = () => localizedOr(localize, LabelToken, "Library");
    const overviewIn = (children) => {
      for (const child of children) {
        const overview = child?.props?.overview;
        if (overview && typeof overview.appid === "number") return overview;
      }
      return null;
    };
    const renderStat = (library) =>
      react.createElement(
        "div",
        { key: StatKey, className: classes.stat },
        react.createElement(
          "div",
          { className: classes.right },
          react.createElement("div", { className: classes.label }, label()),
          react.createElement(
            "div",
            {
              className: classes.value,
              title: library.reason || library.name,
              style: library.installed ? undefined : { opacity: 0.55, whiteSpace: "normal" },
            },
            library.installed || !library.managed ? library.name : `${library.name}: Unavailable`,
            library.reason
              ? react.createElement("div", { style: { fontSize: "12px" } }, library.reason)
              : null,
            !library.installed && library.managed && ui?.dialogButton
              ? react.createElement(
                  ui.dialogButton,
                  {
                    onClick: () =>
                      request(publicationId, "recheck", {
                        appId: library.appId,
                      }).catch(() => {}),
                  },
                  "Recheck",
                )
              : null,
          ),
        ),
      );
    const transform = (create, type, props, key) => {
      if (type !== "div" || !installed || !classes || props?.className !== classes.section)
        return undefined;
      const children = Array.isArray(props.children) ? props.children : [props.children];
      if (children.some((child) => child?.key === StatKey)) return undefined;
      const overview = overviewIn(children);
      const library = libraryForOverview(overview, reading);
      const additions = pluginFrontendElements("gamePage", react, { overview });
      if (!library) {
        without++;
      } else {
        placed++;
      }
      lastOutcome = `placed=${placed} without=${without} libraries=${reading.count} apps=${reading.libraries.size}`;
      if (!overview && !additions.length) return undefined;
      return create(
        type,
        {
          ...props,
          children: [
            ...children,
            ...(overview ? [react.createElement(LiveLibraryStat, { key: StatKey, overview })] : []),
            ...additions,
          ],
        },
        key,
      );
    };
    const resolve = () => {
      runtime = getWebpackRuntime("library-details");
      react = resolveReact(runtime);
      if (!react || typeof react.useSyncExternalStore !== "function") {
        lastError = "React unavailable or lacks useSyncExternalStore";
        return false;
      }
      jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
      if (typeof jsxRuntime?.jsx !== "function" || typeof jsxRuntime?.jsxs !== "function") {
        lastError = "JSX runtime lacks jsx or jsxs";
        return false;
      }
      // Valve's names for the play bar's classes, mapped to whatever this build emitted. Read by
      // name, never written down.
      const exported = runtime.resolve([...ClassMapTokens]);
      const map = classMapOf(exported);
      if (!map || RequiredClasses.some((name) => typeof map[name] !== "string" || !map[name])) {
        lastError = "the play bar class map lacks a stat class";
        return false;
      }
      const join = (...names) =>
        names
          .map((name) => map[name])
          .filter((value) => typeof value === "string" && value)
          .join(" ");
      classes = {
        section: map.GameStatsSection,
        stat: join("GameStat", "LastPlayed"),
        right: join("GameStatRight"),
        label: join("PlayBarLabel"),
        value: join("PlayBarDetailLabel", "LastPlayedInfo"),
      };
      // Wanted, not required: without it the label is the English word.
      localize = resolveSteamLocalizer(runtime);
      const components = resolveSteamUiComponents(runtime);
      ui = components ? { ...components, react } : null;
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "library details resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
      installed = true;
      const claim = interceptElements(jsxRuntime, TransformName, transform);
      if (!claim.ok) {
        installed = false;
        lastError = claim.error ?? "the JSX runtime could not be intercepted";
        return { ok: false, error: lastError };
      }
      lastError = "";
      unsubscribe = subscribe(publicationId, (state) => {
        reading = local.publish(state);
      });
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseElements(jsxRuntime, TransformName);
      if (!released.ok) {
        lastError = released.error ?? "library details release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      reading = local.publish(null);
      local.changed();
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!react && !!jsxRuntime && !!classes,
      claimed: elementsIntercepted(jsxRuntime, TransformName),
      localized: !!localize,
      libraries: reading.count,
      apps: reading.libraries.size,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("libraryDetails", createLibraryDetails());
  // @fragment gates/native-settings.ts
  /**
   * Contributes host settings pages to Steam using reversible member and render claims.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createNativeSettings() {
    const patchId = "steam-ui.native-settings";
    const RootOriginal = "__steamUiNativeSettingsRootOriginal";
    const DescriptorTokens = [
      "#Settings_Page_Display",
      "#Settings_Page_Power",
      "#Settings_Page_Audio",
      "#Settings_Page_Controller",
    ];
    const RootTokens = ["#Settings_Title", "SettingsModal", "SettingsTitleBar"];
    const NativePages = {
      display: "Display",
      power: "Power",
      audio: "Audio",
      controller: "Controller",
    };
    const RowKinds = new Set(["boolean", "choice", "range", "text", "color", "action", "note"]);
    let ui = null;
    let react = null;
    let jsxRuntime = null;
    let nativeRoot = null;
    let installed = false;
    let lastError = "";
    let lastOutcome = "never rendered";
    let unsubscribe = null;
    let state = { pages: [], revision: 0 };
    let mapCache = new WeakMap();
    const local = createLocalStore();
    const roots = new Map();
    const rendered = new Set();
    // Bounded, whole-state validation: duplicate page/row identities are unsafe and an unsupported
    // row must not become a control that sends a value the host did not describe.
    const normalize = (value) => {
      if (value === null) return { pages: [], revision: 0 };
      if (
        !Array.isArray(value?.pages) ||
        value.pages.length > 4 ||
        !Number.isSafeInteger(value.revision)
      )
        return null;
      const pageIds = new Set();
      const rowIds = new Set();
      for (const page of value.pages) {
        if (
          !Object.hasOwn(NativePages, page?.id) ||
          pageIds.has(page.id) ||
          !Array.isArray(page.sections)
        )
          return null;
        if (page.sections.length > 64) return null;
        pageIds.add(page.id);
        for (const section of page.sections) {
          if (!Array.isArray(section?.rows) || section.rows.length > 256) return null;
          for (const row of section.rows) {
            if (
              typeof row?.key !== "string" ||
              !row.key.trim() ||
              row.key.length > 1024 ||
              rowIds.has(row.key) ||
              typeof row.label !== "string" ||
              !RowKinds.has(row.kind)
            )
              return null;
            if (
              row.kind === "choice" &&
              (!Array.isArray(row.choices) ||
                row.choices.some(
                  (choice) => typeof choice?.value !== "string" || typeof choice.label !== "string",
                ))
            )
              return null;
            if (
              row.kind === "range" &&
              (!Number.isFinite(row.number) ||
                !Number.isFinite(row.minimum) ||
                !Number.isFinite(row.maximum) ||
                !Number.isFinite(row.step) ||
                row.step <= 0 ||
                row.minimum > row.maximum)
            )
              return null;
            rowIds.add(row.key);
          }
        }
      }
      return {
        pages: value.pages.filter((page) => page.sections.some((section) => section.rows.length)),
        revision: value.revision,
      };
    };
    const send = (row, value) => {
      const current = state.pages
        .flatMap((page) => page.sections.flatMap((section) => section.rows))
        .find((candidate) => candidate.key === row.key);
      if (!installed || !current || current.disabled)
        return Promise.reject(new Error("This setting is unavailable."));
      return request(patchId, "set", { key: row.key, value }, nextActionGeneration(patchId));
    };
    function SteamUiNativeSettingsSections({ id }) {
      react.useSyncExternalStore(local.subscribe, local.revision);
      react.useEffect(() => {
        if (id !== "controller") return;
        return () => {
          // Stop is an idempotent host action even after its rows retract. Reload/disconnect also
          // stop through the host and the output owner, and every preview has a bounded timer.
          void request(patchId, "set", { key: "rumble.stop", value: true }).catch(() => {});
        };
      }, [id]);
      const drafts = useSteamSettingDrafts(react, state.revision);
      const change = drafts.change(send);
      const action = (row) => change(row, true);
      const page = installed ? state.pages.find((candidate) => candidate.id === id) : null;
      if (!page) return null;
      rendered.add(id);
      lastOutcome = `rendered ${id}: ${page.sections.length} section(s)`;
      return react.createElement(
        react.Fragment,
        null,
        steamUiKitStyle(react),
        ...page.sections
          .filter((section) => section.rows.length)
          .map((section, index) =>
            react.createElement(
              ui.settingsSection,
              {
                key: `steam-ui-native-${id}-${section.id ?? index}`,
                label: section.title ?? undefined,
              },
              ...section.rows.map((row) =>
                renderSteamSettingRow(ui, drafts.row(row), drafts.draft(row), change, action),
              ),
            ),
          ),
      );
    }
    // Match Steam's semantic descriptor keys and the native element handles already in its map.
    // No native component is replaced: the route, label, glyph and content remain Steam's own.
    const transformDescriptors = (value) => {
      if (!installed || !value || Array.isArray(value) || typeof value !== "object") return value;
      if (
        !Object.values(NativePages).every((key) => {
          const page = value[key];
          return (
            page &&
            typeof page.route === "string" &&
            page.route.startsWith("/") &&
            typeof page.visible === "boolean" &&
            react.isValidElement(page.content)
          );
        })
      )
        return value;
      const cached = mapCache.get(value);
      if (cached) return cached;
      const next = { ...value };
      const hostPower = state.pages.some((page) => page.id === "power");
      for (const [id, key] of Object.entries(NativePages)) {
        const original = value[key];
        next[key] = {
          ...original,
          // The peers share Power's native services-initialized gate. Reveal only the battery
          // condition, never a page whose native services are still unavailable.
          visible:
            id === "power" && hostPower
              ? original.visible ||
                (value.Display.visible && value.Audio.visible && value.Controller.visible)
              : original.visible,
          content: react.createElement(
            react.Fragment,
            null,
            original.content,
            react.createElement(SteamUiNativeSettingsSections, {
              id,
              key: `steam-ui-native-${id}`,
            }),
          ),
        };
      }
      mapCache.set(value, next);
      lastOutcome = `mounted four native slots; ${state.pages.length} page(s) published`;
      return next;
    };
    // Same hooks as the original. A mounted root may be adopted without changing its hook order.
    function SteamUiNativeSettingsRoot(props) {
      return nativeRoot(props);
    }
    // Only host Power presence needs the native root to render. Other capability changes update the
    // always-mounted slots, including a Controller slot that was previously empty. Values update inside the
    // separate subscribed sections. A root already mounted when the gate installs is adopted, and
    // the shared JSX transform covers future mounts. Durable original markers reclaim a previous
    // bridge's adopted roots without stacking wrappers.
    const nativeReactRoots = () => {
      const documents = new Set();
      if (typeof document !== "undefined") documents.add(document);
      // The same known popup handles the theme gate reads. A navigating popup may refuse its document.
      try {
        const manager = window.g_PopupManager;
        if (typeof manager?.GetPopups === "function") {
          let visited = 0;
          for (const popup of manager.GetPopups() ?? []) {
            if (visited++ >= 256) break;
            try {
              const doc = popup?.m_popup?.document;
              if (doc) documents.add(doc);
            } catch {}
          }
        }
      } catch {}
      const found = new Set();
      for (const doc of documents) {
        try {
          for (const fiber of reactRootFibers(doc)) found.add(fiber);
        } catch {}
      }
      return [...found];
    };
    const refreshRoots = (remove = false) => {
      for (const [fiber, hadParent] of roots) {
        if (hadParent && !fiberAttached(fiber)) roots.delete(fiber);
      }
      // One traversal and one total node budget across every deduplicated document root.
      walkFibers(nativeReactRoots(), MaximumMountedNodes, (fiber) => {
        const type = fiber.type;
        if (
          type !== nativeRoot &&
          type !== SteamUiNativeSettingsRoot &&
          type?.[RootOriginal] !== nativeRoot
        )
          return;
        roots.set(fiber, fiberAttached(fiber));
      });
      for (const [fiber] of roots) {
        if (remove && fiber.type !== SteamUiNativeSettingsRoot) continue;
        retargetFiber(fiber, remove ? nativeRoot : SteamUiNativeSettingsRoot);
        invalidateFiberProps(fiber);
        requestRender(fiber);
      }
      if (remove) roots.clear();
    };
    const resolve = () => {
      const runtime = getWebpackRuntime("native-settings");
      if (!runtime.findUnique([...DescriptorTokens])) {
        lastError = "native Settings descriptors were not a unique match";
        return false;
      }
      ui = resolveSteamSettingsComponents(runtime);
      react = ui?.react;
      const required = [
        "react",
        "settingsSection",
        "toggleField",
        "dropdown",
        "sliderField",
        "textField",
        "valueField",
        "dialogButton",
        "smallButton",
        "showModal",
        "modalRoot",
        "confirmModal",
      ];
      if (
        !ui ||
        required.some((key) => !ui[key]) ||
        typeof react.useSyncExternalStore !== "function"
      ) {
        lastError = "native Settings fields or subscription hooks are unavailable";
        return false;
      }
      nativeRoot = runtime.exported([...RootTokens], (value) =>
        sourceMatches(value, ["#Settings_Title", "show-icon", '"Settings"']),
      );
      jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
      if (
        !nativeRoot ||
        typeof jsxRuntime?.jsx !== "function" ||
        typeof jsxRuntime?.jsxs !== "function"
      ) {
        lastError = "native Settings root or JSX runtime was not a unique match";
        return false;
      }
      Object.defineProperty(SteamUiNativeSettingsRoot, RootOriginal, {
        value: nativeRoot,
        configurable: true,
      });
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      if (
        !attemptResolution(resolve, (error) => {
          lastError = "native Settings resolution failed: " + String(error);
        })
      )
        return { ok: false, error: lastError };
      const memo = interceptMemo(react, patchId, transformDescriptors);
      if (!memo.ok) return { ok: false, error: memo.error };
      const elements = interceptElements(jsxRuntime, patchId, (create, type, props, key) =>
        type === nativeRoot ? create(SteamUiNativeSettingsRoot, props, key) : undefined,
      );
      if (!elements.ok) {
        releaseMemo(react, patchId);
        return { ok: false, error: elements.error };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (published) => {
        const next = normalize(published);
        if (!next) {
          lastError = "native Settings publication is invalid";
          const hadPower = state.pages.some((page) => page.id === "power");
          state = { pages: [], revision: state.revision };
          rendered.clear();
          if (hadPower) {
            mapCache = new WeakMap();
            refreshRoots();
          }
          local.changed();
          lastOutcome = "invalid publication retracted native additions";
          return;
        }
        lastError = "";
        if (!publicationChanged(state, next)) return;
        const powerPresenceChanged =
          state.pages.some((page) => page.id === "power") !==
          next.pages.some((page) => page.id === "power");
        state = next;
        if (powerPresenceChanged) {
          mapCache = new WeakMap();
          rendered.clear();
          refreshRoots();
        }
        local.changed();
      });
      refreshRoots();
      return { ok: true, installed: true, adopted: roots.size };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const elements = releaseElements(jsxRuntime, patchId);
      if (!elements.ok) return { ok: false, error: elements.error };
      const memo = releaseMemo(react, patchId);
      if (!memo.ok) return { ok: false, error: memo.error };
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      state = { pages: [], revision: 0 };
      local.changed();
      refreshRoots(true);
      mapCache = new WeakMap();
      rendered.clear();
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!ui && !!nativeRoot && !!jsxRuntime,
      claimed: memoIntercepted(react, patchId) && elementsIntercepted(jsxRuntime, patchId),
      claimsRemaining: memoIntercepted(react, patchId) || elementsIntercepted(jsxRuntime, patchId),
      ownedRoots: [...roots.keys()].filter((fiber) => fiber.type === SteamUiNativeSettingsRoot)
        .length,
      pages: state.pages.map((page) => page.id),
      renderedPages: [...rendered],
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("nativeSettings", createNativeSettings());
  // @fragment gates/navigation.ts
  /**
   * Contributes navigation entries using native route/action rows and a reversible memo claim.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
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
    const MaximumDescent = 12;
    let runtime;
    let react;
    let icon;
    let memo = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let unsubscribePlugins = null;
    // What the last render actually saw and did. Everything else can report success while the panel
    // shows exactly what Valve shipped, because insertion depends on the tree Steam rendered.
    let observed = [];
    let lastOutcome = "never rendered";
    // Published items refused for a route that is not one. Counted where they are refused, because
    // they never reach a render, and a row the host asked for must not vanish without a trace.
    let rejectedRoutes = 0;
    // The host's desired additions and hidden entries, replaced whole on each publication.
    let desired = { items: [], hidden: [] };
    const descendCache = new Map();
    const panelCache = new Map();
    // A rendered entry's identity. `route` is the descriptor's own destination and the anchor an
    // "insert after Library" is written against; the React key is Valve's descriptor key and is what
    // survives when an entry has no route at all, such as the power button.
    const identify = (element) => {
      const route = typeof element?.props?.route === "string" ? element.props.route : null;
      // Children.toArray prefixes keys with their nested array path, e.g. .0:$power.
      const rawKey = typeof element?.key === "string" ? element.key : "";
      const key = rawKey.slice(rawKey.lastIndexOf("$") + 1);
      return { key, route, label: textOf(element?.props?.label) ?? "" };
    };
    const matchesAnchor = (element, anchor) => {
      if (typeof anchor !== "string" || !anchor) return false;
      const identity = identify(element);
      return identity.route === anchor || identity.key === anchor;
    };
    // Valve's own entry components, taken from the entries this render already holds. Both are local
    // to the menu module, so a rendered sibling is the only place they can be had - and drawing an
    // added row with them is what makes it Steam's row rather than a copy of one: the same focus
    // bar, active dot, icon box and label, and the same gamepad activation. `onGamepadFocus` is the
    // panel's own handler, which clears the focused running app the way every native entry does.
    const nativeEntries = (children) => {
      let route = null;
      let action = null;
      let onGamepadFocus;
      for (const child of children) {
        if (!react.isValidElement(child) || typeof child.type !== "function") continue;
        const props = child.props ?? {};
        if (!route && typeof props.route === "string") {
          route = child.type;
        } else if (
          !action &&
          typeof props.action === "function" &&
          !("route" in props) &&
          !("app" in props) &&
          !("stream" in props)
        ) {
          action = child.type;
        }
        if (!onGamepadFocus && typeof props.onGamepadFocus === "function") {
          onGamepadFocus = props.onGamepadFocus;
        }
      }
      return { route, action, onGamepadFocus };
    };
    // The row's glyph: the host's own path data, or a toolkit glyph by name, or none.
    const iconOf = (item) =>
      (item.glyph ? renderSteamGlyph(react, item.glyph) : null) ??
      (item.icon && icon ? icon(item.icon) : null);
    const activate = (id) => {
      void request(patchId, "activate", { id }, nextActionGeneration(patchId)).then(
        (answer) => {
          // An action may answer with a page to open. The menu is a side panel, so it is
          // closed first; a page opened behind it is, on a controller, a dead button.
          if (answer?.route && closeSteamSideMenus()) navigateSteamRoute(answer.route);
        },
        () => {
          // The host's refusal is already logged; a rejected press must not break the menu.
        },
      );
    };
    // One added entry, drawn by Valve's own component. An entry with a route uses the route entry,
    // which matches the route for its active state and navigates with Valve's own action exactly as
    // Library does; the route is held to the bounds navigateSteamRoute applies, and is only followed
    // when the user selects the row. Anything else uses the action entry and asks the host.
    // Without the component it needs there is no row: an imitation would be a control that looks
    // like Steam's and behaves like something else, so it is counted instead.
    const renderItem = (item, native) => {
      const common = {
        key: `steam-ui-nav-${item.id}`,
        label: item.label,
        icon: iconOf(item),
        onGamepadFocus: native.onGamepadFocus,
      };
      if (item.route && isNavigableRoute(item.route) && native.route) {
        return react.createElement(native.route, {
          ...common,
          route: item.route,
          active: "if-within-route",
        });
      }
      if (!item.route && native.action) {
        return react.createElement(native.action, {
          ...common,
          action: item.frontendAction ?? (() => activate(item.id)),
        });
      }
      return null;
    };
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
      const pending = [...desired.items, ...pluginFrontendItems("menu").map((slot) => slot.value)];
      // From every child, hidden ones included: hiding Power must not cost the action entry.
      const native = nativeEntries(children);
      const placed = new Set();
      const result = [];
      let unrendered = 0;
      const place = (item) => {
        placed.add(item.id);
        const row = renderItem(item, native);
        if (row) result.push(row);
        else unrendered++;
      };
      for (const item of pending) {
        if (item.position === "start") place(item);
      }
      for (const child of kept) {
        for (const item of pending) {
          if (!placed.has(item.id) && matchesAnchor(child, item.before)) place(item);
        }
        result.push(child);
        for (const item of pending) {
          if (!placed.has(item.id) && matchesAnchor(child, item.after)) place(item);
        }
      }
      // Anything left over goes at the end, including an entry whose anchor is not in this panel.
      // Dropping it would be the silent-control failure the guidance forbids: the caller asked for a
      // row and would have no way to tell that Steam simply does not have the item it named.
      let orphaned = 0;
      for (const item of pending) {
        if (placed.has(item.id)) continue;
        if (item.before || item.after) orphaned++;
        place(item);
      }
      lastOutcome =
        `entries=${observed.length} hidden=${hidden} added=${pending.length - unrendered} ` +
        `orphaned=${orphaned} unrendered=${unrendered}`;
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
        if (!children.length) {
          lastOutcome = `panel had ${children.length} children; left alone`;
          return tree;
        }
        return react.cloneElement(tree, {}, ...applyEntries(children));
      };
      panelCache.set(original, wrapped);
      return wrapped;
    };
    const isPanelRoot = (type) => sourceMatches(type, PanelRootTokens);
    // Descends the rendered tree to the panel root. Function components on the way down are replaced
    // by wrappers that render the original and keep descending (descendInto); anything else is
    // descended through its children.
    const navigationDescender = (type) =>
      function SteamUiNavigationDescend(props) {
        return descend(type(props), 0);
      };
    const descend = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      if (isPanelRoot(element.type)) {
        return react.createElement(wrapPanelRoot(element.type), keyed(element));
      }
      return (
        descendInto(react, element, descendCache, navigationDescender) ??
        mapChildren(react, element, (kid) => descend(kid, depth + 1))
      );
    };
    const resolve = () => {
      runtime = getWebpackRuntime("navigation-panel");
      const resolvedReact = resolveReact(runtime);
      if (!resolvedReact) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      react = resolvedReact;
      icon = createIconRenderer(react);
      const menuFactory = runtime.findUnique([PanelRootTokens[0], OuterToken]);
      if (!menuFactory) {
        lastError = "main menu module was not a unique match";
        return false;
      }
      // The one export whose memo renders the outer container. Selected by what its component draws,
      // never by its minified export name: those are right for today's build and nothing more.
      const exports = runtime(menuFactory[0]);
      // Through the gate's own claim, or a re-resolve while the claim is held finds no memo.
      const candidates = Object.keys(exports).filter((name) => {
        const value = exports[name];
        return (
          value &&
          typeof value === "object" &&
          sourceMatches(unclaimedValue(value.type, claimKeys), [OuterToken])
        );
      });
      if (candidates.length !== 1) {
        lastError = `main menu export was ${candidates.length ? "ambiguous" : "absent"}`;
        return false;
      }
      memo = exports[candidates[0]];
      return true;
    };
    const mounted = createMountedAdoption();
    // The popup's menu host, which has no public handle: a module-local function the popup mounts
    // directly under a React root, exported nowhere, with the memo this gate claims absent from its
    // render path. Recognised by three prop names its author destructures and adopted by the fiber,
    // as decky-loader adopts the Quick Access view. It persists while the menu is closed and
    // re-renders when `open` flips, so an adoption shows on the next open.
    const MenuHostTokens = ["MainNavMenuContainer", "onFocusNavDeactivated", "popup:"];
    const hosts = createSourceAdoption(MenuHostTokens, (type) =>
      cachedWrapper(descendCache, type, navigationDescender),
    );
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "navigation panel resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
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
      // The claim reaches the next mount only; what is already on screen is adopted.
      mounted.adopt(memo, memo.type);
      hosts.adopt();
      unsubscribe = subscribe(patchId, (state) => {
        const items = Array.isArray(state?.items) ? state.items : [];
        const hidden = Array.isArray(state?.hidden) ? state.hidden : [];
        const named = items.filter(
          (item) => item && typeof item.id === "string" && typeof item.label === "string",
        );
        const routable = named.filter((item) => item.route == null || isNavigableRoute(item.route));
        rejectedRoutes = named.length - routable.length;
        const next = {
          items: routable,
          hidden: hidden.filter((value) => typeof value === "string"),
        };
        // A host Steam has recreated since install is adopted here; it sits under a React root
        // with no class above it, so its entries show when the menu next opens.
        hosts.adopt();
        // The wrappers read `desired` from their closure, so a publication changes nothing React
        // can see on its own, and an unchanged one needs no render at all.
        if (!publicationChanged(desired, next)) return;
        desired = next;
        mounted.rerender();
      });
      unsubscribePlugins = subscribePluginFrontends(() => mounted.rerender());
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      // Released before anything is forgotten, so a failed release stays installed and the next
      // remove retries it.
      const released = releaseMember(memo, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "navigation panel release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      unsubscribePlugins = endSubscription(unsubscribePlugins);
      desired = { items: [], hidden: [] };
      descendCache.clear();
      panelCache.clear();
      mounted.release(memo.type);
      hosts.release();
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
      // Whether the claim reached the panels already on screen, and how many popup menu hosts are
      // adopted by source, the path the memo claim never reaches. Nothing adopted is inert.
      mounted: { ...mounted.status(), hosts: hosts.count() },
      rejectedRoutes,
      hidden: desired.hidden.length,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("navigationPanel", createNavigationPanel());
  // @fragment gates/network.ts
  /**
   * Supplies Steam networking RPCs and scan notifications from host publications.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
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
    let unsubscribe = null;
    let syntheticKeys = [];
    // Steam publishes this singleton after its own module initialization. Requiring the module
    // before its chunk arrives leaves empty exports cached for the whole session.
    const store = () => window.SystemNetworkStore ?? null;
    const removeNetworkState = (refresh) => {
      const instance = store();
      if (instance) {
        for (const key of syntheticKeys) instance.m_mapNetworkAccessPoints?.delete(key);
        // Recomputed only through the store's own methods. A client without them, or one that
        // throws, keeps the flags as they are and reports it; it never blocks the rest.
        if (
          typeof instance.IsAnyDeviceConnected === "function" &&
          typeof instance.IsAnyDeviceConnecting === "function"
        ) {
          try {
            instance.m_bIsConnectedToANetwork = instance.IsAnyDeviceConnected();
            instance.m_bIsConnectingToANetwork = instance.IsAnyDeviceConnecting();
          } catch (error) {
            lastError = "network store refresh failed: " + String(error);
          }
        }
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
      const networks = Array.isArray(state?.networks) ? state.networks : [];
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
        for (const key of [...syntheticKeys, ...keys])
          instance.m_mapNetworkAccessPoints.delete(key);
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
        if (typeof existing !== "function") return false;
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
        return claim.ok;
      };
      const started = wrap("StartScanningForNetworks", "startScan");
      const stopped = wrap("StopScanningForNetworks", "stopScan");
      scanWrapped = started || stopped;
    };
    const remove = () => {
      if (!target && !scanWrapped) return { ok: true, absent: true };
      // Every claim goes back before anything is forgotten, so a failed release leaves the gate as
      // it was and the next remove retries what is left (a released claim answers ok again).
      if (scanWrapped) {
        const net = window.SteamClient?.System?.Network ?? null;
        for (const name of ["StartScanningForNetworks", "StopScanningForNetworks"]) {
          const released = releaseMember(net, name, scan);
          if (!released.ok) {
            lastError = released.error ?? "network scan release failed";
            return { ok: false, error: lastError };
          }
        }
      }
      if (target) {
        const released = releaseAccessor(target, property, availability);
        if (!released.ok) {
          lastError = released.error ?? "network availability release failed";
          return { ok: false, error: lastError };
        }
      }
      scanWrapped = false;
      target = null;
      unsubscribe = endSubscription(unsubscribe);
      removeNetworkState(true);
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
  // @fragment gates/pages.ts
  // Custom pages inside Steam's Game Mode UI.
  //
  // Mapped against the live client on 2026-09-10, and cross-read against decky-loader's RouterHook
  // (b4b8be3) as evidence for the approach:
  //
  //   <memo>            source carries "Settings.Root()"; the router
  //     switch          Steam's own route switch: computedMatch + TopLevelTransition, 31 route children
  //       <Route ...>   one per page, children of the switch rather than rendered output
  //
  // The switch is Steam's own, not react-router's Switch. Its source shows the selection rule: it
  // walks `children`, takes the FIRST valid element whose `path` matches, and clones it with
  // `location` and `computedMatch`. Two things follow, and both are in the API rather than hidden:
  //
  //   - appending is safe for a path Steam does not have, and overriding one of Steam's requires
  //     going in front of it, so a page declares which it wants;
  //   - routes are plain elements passed as `children`, so registering a page is a list operation on
  //     props. No descent into rendered output is needed, unlike the navigation panel, where entries
  //     do not exist until the root renders.
  //
  // The Route component is Steam's own, taken off the route list Steam already rendered, never
  // react-router's. That is what gives a custom page native back-navigation: Steam's Route registers
  // the match with the back stack, so B and the back gesture pop the page the way they pop /settings.
  // Using react-router's Route renders the same content and silently loses that.
  const steamPageRenderers = new Map();
  /**
   * Registers a renderer for one page template in this bridge generation.
   * @param template Stable template key used by host page publications.
   * @param render Renderer receiving Steam's React runtime and the published page descriptor.
   */
  const registerSteamPageRenderer = (template, render) => {
    if (!template || template === "default" || steamPageRenderers.has(template)) {
      throw new Error(`Steam page renderer '${template}' is invalid or already registered.`);
    }
    steamPageRenderers.set(template, render);
  };
  /**
   * Installs host-owned routes and renders registered page templates inside Steam's router.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createPageHost() {
    const patchId = "steam-ui.pages";
    const claimKeys = {
      marker: "__steamUiPageHostClaimed",
      original: "__steamUiPageHostOriginal",
    };
    // The router, unique on this pair. "Settings.Root()" alone matches six modules and
    // "TopLevelTransition" is the switch's own; together they name exactly one.
    const RouterTokens = ["Settings.Root()", "TopLevelTransition"];
    // What Steam's back-stack Route reads, in its author's words: the JSX prop it fills in and the
    // optional member access it fills it from. Used to verify the Route borrowed from the route list,
    // never to find one; a fingerprint names what an author typed, not how a minifier spelled it.
    const BackstackRouteMarkers = ["routePath:", ".match?.path"];
    // A path every build of the client has and no consumer would register, used to recognise the
    // route list among the router's children.
    const KnownRoute = "/library/home";
    const MaximumDescent = 8;
    const PageKeyPrefix = "steam-ui-page-";
    let runtime;
    let react;
    // Steam's own Route, taken off the `/library/home` element in the route list Steam is rendering:
    // the component itself rather than a description of it, so no client build can rename it away.
    let borrowedRoute = null;
    let routeVerified = false;
    let memo = null;
    let routeSwitchFiber = null;
    let routeSwitchWrapper = null;
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    let unsubscribePlugins = null;
    let hostPages = [];
    const mounted = createMountedAdoption();
    let pages = [];
    let lastOutcome = "never rendered";
    let observedRoutes = [];
    const descendCache = new Map();
    // One registered page's body. The content is described by the host rather than supplied as a
    // component: a consumer's React lives in its own process, so what crosses the bridge is data,
    // and a renderer registered under the page's template draws it.
    //
    // The renderer runs here, when Steam draws the page, not when the route is built. Routes are
    // built the moment pages are published, which on a cold start is before the gate a renderer
    // needs has resolved; calling it then either baked a null child into the route or threw inside
    // Steam's router render, whose error boundary replaces the whole client. A renderer that throws
    // now costs its own page, falls open to the heading, and names its template.
    function SteamUiPageBody({ page }) {
      const renderer = steamPageRenderers.get(page.template);
      if (renderer) {
        try {
          return renderer(react, page);
        } catch (error) {
          lastError = `page renderer '${page.template}' threw: ${String(error)}`;
        }
      }
      return react.createElement(
        "div",
        { className: "steam-ui-page", role: "region", "aria-label": page.title },
        react.createElement("h1", null, page.title),
        react.createElement("div", { id: `steam-ui-page-body-${page.id}` }),
      );
    }
    const buildRoute = (page) =>
      react.createElement(
        borrowedRoute,
        { path: page.path, key: `${PageKeyPrefix}${page.id}` },
        react.createElement(SteamUiPageBody, { page }),
      );
    // Whether an array of elements is the router's route list.
    const isRouteList = (value) => isSteamRouteList(react, value, KnownRoute);
    // Inserts the registered pages into the route list.
    //
    // Overrides go in front of Steam's own routes and additions behind them, because the switch takes
    // the first match. Both keep their relative order, so two overrides of the same path resolve in
    // registration order rather than arbitrarily.
    const applyPages = (routes) => {
      // Idempotent: the route list is reached twice per render, once in the router's output and once
      // by the mounted switch, and a page inserted by the first pass must not be inserted again.
      const own = routes.filter(
        (route) => typeof route?.key === "string" && route.key.startsWith(PageKeyPrefix),
      );
      const steam = routes.filter((route) => !own.includes(route));
      observedRoutes = steam
        .filter((route) => react.isValidElement(route) && typeof route.props?.path === "string")
        .map((route) => route.props.path);
      // A host element would be a string type and cannot be built with; anything else is what Steam
      // renders that route with, verified against the Route's own markers for the status only.
      const known = steam.find(
        (route) => react.isValidElement(route) && route.props?.path === KnownRoute,
      );
      if (known && typeof known.type !== "string") {
        borrowedRoute = known.type;
        routeVerified = sourceMatches(known.type, BackstackRouteMarkers);
      }
      const wanted = pages;
      if (!wanted.length) {
        lastOutcome = `routes=${steam.length} pages=0`;
        return routes;
      }
      // Loud rather than empty: a surface that silently draws nothing is a defect.
      if (!borrowedRoute) {
        lastOutcome = `routes=${steam.length} pages=${wanted.length} route=unavailable`;
        return routes;
      }
      const overrides = wanted.filter((page) => page.override === true).map(buildRoute);
      const additions = wanted.filter((page) => page.override !== true).map(buildRoute);
      lastOutcome = `routes=${steam.length} overrides=${overrides.length} additions=${additions.length}`;
      return [...overrides, ...steam, ...additions];
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
      return mapChildren(react, element, (kid) => replaceRouteList(kid, depth + 1));
    };
    const pageDescender = (type) =>
      function SteamUiPageDescend(props) {
        return descend(type(props), 0);
      };
    const descend = (element, depth) => {
      if (depth > MaximumDescent || !react.isValidElement(element)) return element;
      const replaced = replaceRouteList(element, 0);
      if (replaced !== element) return replaced;
      return descendInto(react, element, descendCache, pageDescender) ?? element;
    };
    const resolve = () => {
      runtime = getWebpackRuntime("pages");
      const resolvedReact = resolveReact(runtime);
      if (!resolvedReact) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      react = resolvedReact;
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
    //
    // Breadth-first over the child and sibling links, as the Home carousel walks the same tree. A
    // recursive walk nests a frame for every sibling, so a long sibling chain could exhaust the stack
    // before the node bound was ever reached.
    const findRouterMemo = () => {
      let found = null;
      walkFibers(reactRootFibers(), MaximumMountedNodes, (node) => {
        const elementType = node.elementType;
        if (!elementType || typeof elementType !== "object") return false;
        // Through the gate's own claim, or a re-resolve while the claim is held finds no router.
        if (!sourceMatches(unclaimedValue(elementType.type, claimKeys), [RouterTokens[0]])) {
          return false;
        }
        found = elementType;
        return true;
      });
      return found;
    };
    const findRouteSwitchFiber = () => {
      let found = null;
      walkFibers(reactRootFibers(), MaximumMountedNodes, (node) => {
        const original = node.type?.__steamUiPageSwitchOriginal ?? node.type;
        if (!sourceMatches(original, ["computedMatch", "TopLevelTransition"])) return false;
        found = node;
        return true;
      });
      return found;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "page host resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
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
      routeSwitchFiber = findRouteSwitchFiber();
      if (!routeSwitchFiber) {
        const rolledBack = releaseMember(memo, "type", claimKeys);
        lastError = rolledBack.ok
          ? "Steam's mounted route switch was not found"
          : "Steam's mounted route switch was not found, and the router claim could not be released: " +
            (rolledBack.error ?? "unknown");
        return { ok: false, error: lastError };
      }
      const currentSwitch = routeSwitchFiber.type;
      const originalSwitch = currentSwitch?.__steamUiPageSwitchOriginal ?? currentSwitch;
      routeSwitchWrapper = function SteamUiPageSwitch(props) {
        const children = react.Children.toArray(props?.children);
        return originalSwitch({
          ...props,
          children: isRouteList(children) ? applyPages(children) : children,
        });
      };
      Object.defineProperty(routeSwitchWrapper, "__steamUiPageSwitchOriginal", {
        value: originalSwitch,
      });
      routeSwitchFiber.type = routeSwitchWrapper;
      if (routeSwitchFiber.alternate) routeSwitchFiber.alternate.type = routeSwitchWrapper;
      // The claim reaches the next mount only; the router already on screen is adopted, or the claim
      // is correct and inert until the user happens to remount it. See createMountedAdoption.
      mounted.adopt(memo, memo.type);
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        const declared = Array.isArray(state?.pages) ? state.pages : [];
        const next = declared.filter(
          (page) =>
            page &&
            typeof page.id === "string" &&
            typeof page.title === "string" &&
            typeof page.path === "string" &&
            // A path has to be absolute or Steam's matcher never sees it, and a page that claims
            // every route would black out the client.
            page.path.startsWith("/") &&
            page.path !== "/",
        );
        // The wrappers read `pages` from their closure, so a publication changes nothing React can
        // see on its own. Only a changed list earns a render: the class above the router is the one
        // asked, and its render re-runs every route, the configurator's edit session included.
        if (!publicationChanged(pages, next)) return;
        hostPages = next;
        pages = [
          ...hostPages,
          ...pluginFrontendItems("page").map((slot) => ({ ...slot.value, template: slot.id })),
        ];
        mounted.rerender();
      });
      unsubscribePlugins = subscribePluginFrontends(() => {
        pages = [
          ...hostPages,
          ...pluginFrontendItems("page").map((slot) => ({ ...slot.value, template: slot.id })),
        ];
        mounted.rerender();
      });
      return { ok: true, installed: true, reclaimed: claim.reclaimed };
    };
    // Every owned mutation goes back before the gate forgets it owns anything. Clearing `installed`
    // ahead of the fallible release left both wrappers running while each later remove() answered
    // `absent`, so a failed cleanup could never be retried.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseMember(memo, "type", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "page host release failed";
        return { ok: false, error: lastError };
      }
      mounted.release(memo.type);
      if (routeSwitchFiber && routeSwitchWrapper) {
        const originalSwitch = routeSwitchWrapper.__steamUiPageSwitchOriginal;
        if (routeSwitchFiber.type === routeSwitchWrapper) routeSwitchFiber.type = originalSwitch;
        if (routeSwitchFiber.alternate?.type === routeSwitchWrapper) {
          routeSwitchFiber.alternate.type = originalSwitch;
        }
      }
      routeSwitchFiber = null;
      routeSwitchWrapper = null;
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      unsubscribePlugins = endSubscription(unsubscribePlugins);
      hostPages = [];
      pages = [];
      // Borrowed from a render that is about to be undone, so it is not carried into the next install.
      borrowedRoute = null;
      routeVerified = false;
      descendCache.clear();
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!memo,
      routeResolved: !!borrowedRoute,
      // "borrowed (unverified)" is a Route whose source lacks the back-stack markers: it draws, and
      // back navigation may be the thing it lost.
      routeSource: borrowedRoute ? (routeVerified ? "borrowed" : "borrowed (unverified)") : "none",
      claimed: memberClaimed(memo, "type", claimKeys),
      pages: pages.length,
      // Whether the claim reached the routers already on screen, and whether one is still drawing
      // something else: the difference between "claimed" and "actually running".
      mounted: mounted.status(),
      // What the last render actually saw. Everything above can be true while no page is reachable,
      // because insertion depends on finding the route list in the tree Steam rendered.
      routeCount: observedRoutes.length,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("pages", createPageHost());
  // @fragment gates/performance.ts
  /**
   * Projects host performance state into Steam performance RPCs and semantic write commands.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createPerfNamespace() {
    const patchId = "steam-ui.performance";
    let installed = false;
    let lastError = "";
    let unsubscribe = null;
    const store = () => window.SystemPerfStore ?? null;
    // The message class is never named here — it is taken from an instance the store builds, so
    // this stays correct across minification and client updates. An object argument is still
    // accepted because that is what a caller other than the store would pass. Anything that cannot
    // be decoded answers null, and the update is refused rather than sent as an empty delta that the
    // host would accept as "nothing changed".
    const decodeSettingsUpdate = (payload) => {
      if (typeof payload !== "string") {
        const decoded = payload?.toObject?.() ?? payload;
        if (decoded && typeof decoded === "object" && !Array.isArray(decoded)) return decoded;
        lastError = "settings update could not be decoded: not a message";
        return null;
      }
      try {
        const constructor = store()?.CreateSettingsUpdateRequest?.()?.constructor;
        if (typeof constructor?.deserializeBinary !== "function") {
          lastError = "settings update could not be decoded: no deserializeBinary";
          return null;
        }
        const binary = atob(payload);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index += 1) {
          bytes[index] = binary.charCodeAt(index);
        }
        const decoded = constructor.deserializeBinary(bytes).toObject();
        if (decoded && typeof decoded === "object" && !Array.isArray(decoded)) return decoded;
        lastError = "settings update could not be decoded: not a message";
        return null;
      } catch (error) {
        lastError = "settings update could not be decoded: " + String(error);
        return null;
      }
    };
    // The four fields the gate writes, as Steam's store held them before the first publication:
    // presence and value, so removal hands back exactly that rather than an invented empty state.
    const DisplacedFields = ["limits", "settings", "current_game_id", "active_profile_game_id"];
    let displaced = null;
    let displacedTarget = null;
    const onState = (state) => {
      if (!installed || !state) return;
      const target = store();
      if (!target || !target.m_msgState) return;
      try {
        if (!displaced) {
          displacedTarget = target.m_msgState;
          displaced = DisplacedFields.map((field) => ({
            field,
            present: Object.hasOwn(displacedTarget, field),
            value: displacedTarget[field],
          }));
        }
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
        UpdateSettings: (payload) => {
          const delta = decodeSettingsUpdate(payload);
          if (delta === null) return Promise.reject(new Error(lastError));
          return request(patchId, "updateSettings", { delta }, 0);
        },
        RegisterForStateChanges: () => ({
          unregister: () => {},
        }),
        RegisterForDiagnosticInfoChanges: () => ({
          unregister: () => {},
        }),
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
      // Marker-checked, which this path was not: it deleted whatever was at System.Perf, so a real
      // backend appearing under a still-installed gate would have been removed by the host's own
      // cleanup. Released first, so a failed withdrawal leaves the gate installed and the next
      // remove retries it.
      const withdrawn = withdrawNamespace(window.SteamClient?.System, "Perf", ownedMarker);
      if (!withdrawn.ok) {
        lastError = withdrawn.error ?? "perf namespace withdrawal failed";
        return { ok: false, error: lastError };
      }
      // Back to what the store held before the first publication, so every control returns to
      // rendering what it did before the host answered rather than keeping the host's last answer.
      if (displacedTarget && displaced) {
        try {
          for (const { field, present, value } of displaced) {
            if (present) displacedTarget[field] = value;
            else delete displacedTarget[field];
          }
        } catch (error) {
          lastError = "perf state restoration failed: " + String(error);
          return { ok: false, error: lastError };
        }
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      displaced = null;
      displacedTarget = null;
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
  // @fragment gates/power-menu.ts
  /**
   * Projects host power-menu additions and visibility into Steam's existing menu.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createPowerMenu() {
    const patchId = "steam-ui.power-menu";
    const PowerTokens = new Set(["#Sleep", "#Quit_Sleep", "#Shutdown", "#Quit_Shutdown"]);
    const LabelToken = "#SwitchToDesktop";
    const EntryKey = "steam-ui-power-menu-desktop";
    const MaximumDepth = 4;
    let runtime;
    let react = null;
    let jsxRuntime = null;
    let localize = null;
    let installed = false;
    let visible = false;
    let unsubscribe = null;
    // What the first power menu rendered, kept for the session: the menu's own type, which makes
    // every later element a single identity test, the item and separator types, and the label.
    let menuType = null;
    let itemType = null;
    let separatorType = null;
    let label = "";
    let lastOutcome = "never rendered";
    let lastError = "";
    const isPowerEntry = (child) =>
      react.isValidElement(child) && PowerTokens.has(child.props?.strDisplayNameLocToken);
    // Steam's plain menu item: selectable, labelled by its children, not one of the confirming
    // entries that carry a localization token instead. Found among what the menu rendered, inside
    // the fragments Valve groups its sections in.
    const findItemType = (children, depth = 0) => {
      if (depth > MaximumDepth || !Array.isArray(children)) return null;
      for (const child of children) {
        if (!react.isValidElement(child)) continue;
        const props = child.props ?? {};
        if (child.type === react.Fragment) {
          const found = findItemType(props.children, depth + 1);
          if (found) return found;
        } else if (
          typeof props.onSelected === "function" &&
          props.strDisplayNameLocToken === undefined &&
          typeof props.children === "string"
        ) {
          return child.type;
        }
      }
      return null;
    };
    // Valve's separator opens each of its fragment sections and is the one element there with no
    // props at all. Wanted, not required: without it the entry is drawn unseparated.
    const findSeparator = (children) => {
      for (const child of children) {
        if (!react.isValidElement(child) || child.type !== react.Fragment) continue;
        const inner = child.props?.children;
        const first = Array.isArray(inner) ? inner[0] : inner;
        if (
          react.isValidElement(first) &&
          first.type !== react.Fragment &&
          typeof first.type !== "string" &&
          Object.keys(first.props ?? {}).length === 0
        ) {
          return first.type;
        }
      }
      return null;
    };
    const activate = () => {
      void request(patchId, "switchToDesktop", {}, nextActionGeneration(patchId)).catch(() => {
        // A refusal stays host-authoritative and must not make Steam's menu fail.
      });
    };
    const entryProps = { tone: "destructive", onSelected: activate };
    // Recognises the power menu the first time it renders and remembers what it drew with.
    const learn = (type, children) => {
      if (!children.some(isPowerEntry)) return false;
      const item = findItemType(children);
      if (!item) {
        lastOutcome = "menu item type absent";
        return false;
      }
      menuType = type;
      itemType = item;
      separatorType = findSeparator(children);
      label = localizedOr(localize, LabelToken, "Switch to Desktop");
      return true;
    };
    const transform = (create, type, props, key) => {
      if (!visible || (menuType !== null && type !== menuType)) return undefined;
      const children = props?.children;
      if (!Array.isArray(children)) return undefined;
      if (menuType === null) {
        if (typeof props.onCancel !== "function" || typeof props.label !== "string")
          return undefined;
        if (!learn(type, children)) return undefined;
      } else if (!children.some(isPowerEntry)) {
        // Valve's menu component draws other menus too.
        return undefined;
      }
      const section = react.createElement(
        react.Fragment,
        { key: EntryKey },
        separatorType ? react.createElement(separatorType) : null,
        react.createElement(itemType, entryProps, label),
      );
      lastOutcome = `appended${separatorType ? "" : " without separator"}`;
      return create(type, { ...props, children: [...children, section] }, key);
    };
    const resolve = () => {
      runtime = getWebpackRuntime("power-menu");
      react = resolveReact(runtime);
      if (!react) {
        lastError = "React runtime was not a unique match";
        return false;
      }
      if (!runtime.findUnique(["#Quit_Shutdown", LabelToken])) {
        lastError = "Power menu module was not a unique match";
        return false;
      }
      jsxRuntime = runtime.resolve([...JsxRuntimeTokens]);
      if (!jsxRuntime) {
        lastError = "JSX runtime was not a unique match";
        return false;
      }
      // Wanted, not required: without it the label is the English string.
      localize = resolveSteamLocalizer(runtime);
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      if (
        !attemptResolution(
          resolve,
          (error) => (lastError = "Power menu resolution failed: " + String(error)),
        )
      ) {
        return { ok: false, error: lastError };
      }
      const claim = interceptElements(jsxRuntime, patchId, transform);
      if (!claim.ok) {
        lastError = claim.error ?? "the JSX runtime could not be intercepted";
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        visible = state?.visible === true;
      });
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseElements(jsxRuntime, patchId);
      if (!released.ok) {
        lastError = released.error ?? "Power menu release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      visible = false;
      menuType = itemType = separatorType = null;
      label = "";
      lastOutcome = "removed";
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!react && !!jsxRuntime,
      claimed: elementsIntercepted(jsxRuntime, patchId),
      localized: !!localize,
      recognised: menuType !== null,
      visible,
      lastOutcome,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("powerMenu", createPowerMenu());
  // @fragment gates/screensaver.ts
  /**
   * Projects the host screensaver setting into the matched Steam settings surface.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createScreensaverSettings() {
    const patchId = "steam-ui.screensaver";
    const MemoName = "screensaverSettings";
    const RouteTokens = ["GameAPIOSK:", "/gameapiosk"];
    const SectionTokens = ['"#Settings_Customization_Screensaver"', "ForceScreensaver"];
    const PluggedInSetting = "system_idle_screensaver_ac_sec";
    const BatterySetting = "system_idle_screensaver_battery_sec";
    // Steam's settings can arrive after the gate installs. The first report is retried on this
    // bounded schedule rather than waiting for someone to open the page.
    const ReportAttempts = 60;
    const ReportIntervalMilliseconds = 2000;
    let runtime;
    let react = null;
    let dropdown = null;
    let settings = null;
    let useObserver = null;
    let route = "";
    let installed = false;
    let lastError = "";
    let lastOutcome = "never rendered";
    let lastReport = "";
    let unsubscribe = null;
    let reportTimer = null;
    // The host's rows, replaced whole on each publication.
    let rows = [];
    const pending = new Set();
    const local = createLocalStore();
    const pageCache = new Map();
    const sectionCache = new Map();
    const listCache = new WeakMap();
    const text = (value) => (typeof value === "string" ? value : "");
    const seconds = (value) => (Number.isInteger(value) && value >= 0 ? value : null);
    // Validated rather than trusted: a malformed option list renders a dropdown whose entries select
    // nothing. A state that fails is dropped whole and the outcome says so.
    const normalize = (value) => {
      if (!value || typeof value !== "object" || !Array.isArray(value.rows)) return null;
      const ids = new Set();
      const next = [];
      for (const row of value.rows) {
        if (!row || typeof row !== "object") return null;
        const id = text(row.id);
        const current = seconds(row.seconds);
        if (
          !/^[a-z][a-z0-9-]*$/u.test(id) ||
          ids.has(id) ||
          current === null ||
          !Array.isArray(row.options)
        )
          return null;
        const options = [];
        for (const option of row.options) {
          const optionSeconds = seconds(option?.seconds);
          const label = text(option?.label);
          if (optionSeconds === null || !label) return null;
          options.push({ data: optionSeconds, label });
        }
        ids.add(id);
        next.push({
          id,
          label: text(row.label),
          description: text(row.description),
          seconds: current,
          options,
          available: row.available === true,
        });
      }
      return next;
    };
    // Steam's own screensaver timeouts from its client settings, and whether Steam believes the
    // machine has a battery (the test its Power page is shown on). Null until the settings arrive.
    const readScreensaver = () => {
      const values = settings?.clientSettings;
      const pluggedIn = seconds(values?.[PluggedInSetting]);
      if (pluggedIn === null) return null;
      return {
        acSeconds: pluggedIn,
        batterySeconds: seconds(values?.[BatterySetting]),
        battery: window.SystemPowerStore?.batteryState?.bHasBattery === true,
      };
    };
    // Once per change, and again whenever the page opens, because that is when the host's reading of
    // its own timeouts is worth refreshing.
    const report = (reading, force) => {
      if (!installed || !reading) return false;
      const signature = JSON.stringify(reading);
      if (!force && signature === lastReport) return true;
      lastReport = signature;
      request(patchId, "report", reading).catch((error) => {
        lastError = "screensaver report failed: " + String(error);
      });
      return true;
    };
    const reportWhenReady = (attempt) => {
      reportTimer = null;
      if (!installed || report(readScreensaver(), false) || attempt >= ReportAttempts) return;
      reportTimer = setTimeout(() => reportWhenReady(attempt + 1), ReportIntervalMilliseconds);
    };
    const select = (row, value) => {
      const chosen = seconds(value);
      if (!installed || chosen === null || chosen === row.seconds || pending.has(row.id)) return;
      pending.add(row.id);
      local.changed();
      request(patchId, "setTimeout", { row: row.id, seconds: chosen })
        .catch((error) => {
          lastError = "timeout change failed: " + String(error);
        })
        .finally(() => {
          pending.delete(row.id);
          local.changed();
        });
    };
    function SteamUiScreensaverTimeouts() {
      react.useSyncExternalStore(local.subscribe, local.revision);
      const reading = useObserver
        ? useObserver(readScreensaver, "SteamUiScreensaverTimeouts")
        : readScreensaver();
      const signature = reading ? JSON.stringify(reading) : "";
      react.useEffect(() => {
        report(readScreensaver(), true);
      }, []);
      react.useEffect(() => {
        report(readScreensaver(), false);
      }, [signature]);
      if (!installed) return null;
      if (!rows.length) {
        lastOutcome = "no rows published";
        return null;
      }
      lastOutcome = `rendered ${rows.length} row(s)`;
      return react.createElement(
        react.Fragment,
        null,
        ...rows.map((row) =>
          react.createElement(dropdown, {
            key: `steam-ui-timeout-${row.id}`,
            label: row.label,
            description: row.description || undefined,
            rgOptions: row.options,
            selectedOption: row.seconds,
            disabled: !row.available || pending.has(row.id),
            controlled: true,
            onChange: (option) => select(row, option?.data),
          }),
        ),
      );
    }
    const childrenOf = (element) => {
      const children = element.props?.children;
      return Array.isArray(children) ? children : children === undefined ? [] : [children];
    };
    const isSection = (type) => {
      if (typeof type !== "function") return false;
      const source = String(type);
      return SectionTokens.every((token) => source.includes(token));
    };
    const sectionFor = (original) => {
      let wrapped = sectionCache.get(original);
      if (wrapped) return wrapped;
      wrapped = function SteamUiScreensaverSection(props) {
        const section = original(props);
        if (!installed || !react.isValidElement(section)) return section;
        return react.cloneElement(
          section,
          undefined,
          ...childrenOf(section),
          react.createElement(SteamUiScreensaverTimeouts, { key: "steam-ui-screensaver-timeouts" }),
        );
      };
      sectionCache.set(original, wrapped);
      return wrapped;
    };
    const pageFor = (original) => {
      let wrapped = pageCache.get(original);
      if (wrapped) return wrapped;
      wrapped = function SteamUiCustomizationPage(props) {
        const tree = original(props);
        if (!installed || !react.isValidElement(tree)) return tree;
        let found = 0;
        const children = childrenOf(tree).map((child) => {
          if (!react.isValidElement(child) || !isSection(child.type)) return child;
          found += 1;
          return react.createElement(sectionFor(child.type), keyed(child));
        });
        if (found !== 1) {
          lastOutcome = found
            ? "the screensaver section was not unique on the page"
            : "the screensaver section was not found on the page";
          return tree;
        }
        return react.cloneElement(tree, undefined, ...children);
      };
      pageCache.set(original, wrapped);
      return wrapped;
    };
    // The page list, with the customization page's content wrapped. The same input list always maps
    // to the same output list, so memo consumers downstream see a stable identity.
    const transformPages = (value) => {
      if (!installed || !Array.isArray(value) || !value.length) return value;
      const first = value[0];
      if (!first || typeof first !== "object" || !("route" in first) || !("content" in first))
        return value;
      const cached = listCache.get(value);
      if (cached) return cached;
      let index = -1;
      for (let at = 0; at < value.length; at++) {
        const item = value[at];
        if (
          item &&
          typeof item === "object" &&
          item.route === route &&
          react.isValidElement(item.content)
        ) {
          if (index >= 0) return value;
          index = at;
        }
      }
      if (index < 0 || typeof value[index].content.type !== "function") return value;
      const item = value[index];
      const next = value.slice();
      next[index] = {
        ...item,
        content: react.createElement(pageFor(item.content.type), keyed(item.content)),
      };
      listCache.set(value, next);
      return next;
    };
    const resolve = () => {
      runtime = getWebpackRuntime("screensaver-settings");
      const fields = resolveSteamFieldComponents(runtime);
      react = fields?.react ?? null;
      if (!react) {
        lastError = "React unavailable";
        return false;
      }
      if (
        typeof react.useSyncExternalStore !== "function" ||
        typeof react.useEffect !== "function"
      ) {
        lastError = "React runtime lacks useSyncExternalStore or useEffect";
        return false;
      }
      if (!fields?.dropdown) {
        lastError = "the dropdown field was not a unique match";
        return false;
      }
      dropdown = fields.dropdown;
      const pages = runtime.exported(
        [...RouteTokens],
        (value) => typeof value?.Settings?.Customization === "function",
      );
      route = pages.Settings.Customization();
      if (typeof route !== "string" || !route.startsWith("/")) {
        lastError = "the customization settings route is unavailable";
        return false;
      }
      if (!runtime.findUnique([...SectionTokens])) {
        lastError = "the screensaver section module was not a unique match";
        return false;
      }
      settings = runtime.exported(
        [...SettingsTokens],
        (value) => !!value && typeof value === "object" && typeof value.clientSettings === "object",
      );
      // Wanted, not required: without it the rows still follow the host, and a change to Steam's
      // timeout reaches the host on the section's next render. `status.tracking` says which.
      useObserver = findUseObserver(runtime);
      return true;
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "screensaver settings resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
      installed = true;
      const intercepted = interceptMemo(react, MemoName, transformPages);
      if (!intercepted.ok) {
        installed = false;
        lastError = intercepted.error ?? "React useMemo could not be intercepted";
        return { ok: false, error: lastError };
      }
      lastError = "";
      unsubscribe = subscribe(patchId, (published) => {
        const next = normalize(published);
        if (!next) {
          lastOutcome = "state received but rejected by validation";
          return;
        }
        rows = next;
        local.changed();
      });
      reportWhenReady(0);
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      // Released first: a failed release keeps the gate installed with its rows, so the next
      // remove retries it instead of answering absent over a claim still in place.
      const released = releaseMemo(react, MemoName);
      if (!released.ok) {
        lastError = released.error ?? "React useMemo could not be released";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      if (reportTimer) {
        clearTimeout(reportTimer);
        reportTimer = null;
      }
      // An open page re-renders without the rows; its wrappers pass Steam's own tree through.
      rows = [];
      pending.clear();
      lastReport = "";
      local.changed();
      pageCache.clear();
      sectionCache.clear();
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      resolved: !!react && !!dropdown && !!route,
      claimed: memoIntercepted(react, MemoName),
      route,
      settings: !!settings,
      tracking: !!useObserver,
      rows: rows.length,
      lastOutcome,
      lastReport,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("screensaver", createScreensaverSettings());
  // @fragment gates/sound-overrides.ts
  /**
   * Overrides selected Steam UI sounds and restores the displaced sound handlers on removal.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createSoundOverrides() {
    const patchId = "steam-ui.sound-overrides";
    const keys = { marker: "__steamUiSoundsClaimed", original: "__steamUiSoundsOriginal" };
    let manager = null;
    let installed = false;
    let unsubscribe = null;
    let sounds = new Map();
    let generation = 0;
    let lastError = "";
    let loading = false;
    const report = (revision) => {
      if (!installed) return;
      void request(patchId, "status", {
        revision,
        loading,
        resources: sounds.size,
        error: lastError ? lastError.slice(0, 256) : null,
      }).catch(() => {});
    };
    const resolve = () => {
      try {
        const runtime = getWebpackRuntime("sound-overrides");
        const store = runtime.exported(
          ["m_GamepadUIAudioStore", "m_bHomeAndQuickAccessButtonsEnabled"],
          (value) => !!value?.GamepadUIAudio?.AudioPlaybackManager,
        );
        const candidate = store.GamepadUIAudio.AudioPlaybackManager;
        return typeof candidate.PlayAudioURLWithRepeats === "function" ? candidate : null;
      } catch {
        return null;
      }
    };
    const reconcile = async (state) => {
      const current = ++generation;
      // Retract before decoding: stale or corrupt assets never displace working stock audio.
      sounds = new Map();
      lastError = "";
      loading = true;
      const revision = Number(state?.revision ?? 0);
      report(revision);
      if (!state?.sounds || typeof state.sounds !== "object") {
        loading = false;
        report(revision);
        return;
      }
      const entries = Object.entries(state.sounds);
      if (!entries.length) {
        loading = false;
        report(revision);
        return;
      }
      let context = null;
      const next = new Map();
      // A refused entry leaves the others loading and says which one it was.
      const reject = (name) => {
        if (current === generation && installed) lastError = `Rejected sound: ${name}`;
      };
      try {
        context = new AudioContext();
        for (const [name, value] of entries) {
          if (!/^[a-zA-Z0-9_.-]+\.(wav|mp3|m4a|ogg)$/u.test(name) || !Array.isArray(value)) {
            reject(name);
            continue;
          }
          const valid = [];
          for (const url of value) {
            if (current !== generation || !installed) return;
            if (
              typeof url !== "string" ||
              !/^data:audio\/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]+$/u.test(url)
            ) {
              reject(name);
              continue;
            }
            try {
              const bytes = await (await fetch(url)).arrayBuffer();
              await context.decodeAudioData(bytes);
              valid.push(url);
            } catch {
              if (current === generation && installed) lastError = `Unreadable sound: ${name}`;
            }
          }
          if (valid.length) next.set(name, valid);
        }
        if (current === generation && installed) sounds = next;
      } catch (error) {
        if (current === generation && installed) lastError = String(error);
      } finally {
        if (context) await context.close().catch(() => {});
        if (current === generation && installed) {
          loading = false;
          report(revision);
        }
      }
    };
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      manager = resolve();
      if (!manager) return { ok: false, error: "Gamepad audio manager unavailable" };
      const result = claimMember(
        manager,
        "PlayAudioURLWithRepeats",
        keys,
        (original) =>
          function (url, ...args) {
            // Stock URLs can be relative or absolute. Unknown directories are never remapped.
            let options;
            if (typeof url === "string") {
              try {
                const path = url.startsWith("/sounds/")
                  ? url.split(/[?#]/u, 1)[0]
                  : new URL(url).pathname;
                if (path.startsWith("/sounds/") && !path.slice(8).includes("/"))
                  options = sounds.get(path.slice(8));
              } catch {}
            }
            const chosen = options?.length
              ? options[Math.floor(Math.random() * options.length)]
              : url;
            return original.call(this, chosen, ...args);
          },
      );
      if (!result.ok) return result;
      installed = true;
      unsubscribe = subscribe(patchId, (state) => {
        void reconcile(state);
      });
      return { ok: true, installed: true };
    };
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      // Released first: a failed release keeps the gate installed with its sounds, so the next remove
      // retries it.
      const result = releaseMember(manager, "PlayAudioURLWithRepeats", keys);
      if (!result.ok) {
        lastError = result.error ?? "sound override release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      ++generation;
      loading = false;
      sounds.clear();
      unsubscribe = endSubscription(unsubscribe);
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      claimed: memberClaimed(manager, "PlayAudioURLWithRepeats", keys),
      resources: sounds.size,
      loading,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("soundOverrides", createSoundOverrides());
  // @fragment gates/storage.ts
  /**
   * Supplies Steam storage RPCs while retaining unrelated native transport behavior.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced.
   */
  function createStorageService() {
    const patchId = "steam-ui.storage";
    const claimKeys = {
      marker: "__steamUiStorageClaimed",
      original: "__steamUiStorageOriginal",
    };
    const ServicePrefix = "StorageDeviceManager.";
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
    const StorageQueryScope = "SystemStorageService";
    const AvailabilityQueryKey = [StorageQueryScope, "IsServiceAvailable"];
    const StateQueryKey = [StorageQueryScope, "State"];
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
    // One of Steam's uint32 identifiers. Zero for anything that is not one, which is the wire's own
    // "not named": the client numbers these from one, so the host can refuse rather than guess.
    const asId = (value) => {
      const parsed = typeof value === "string" ? Number(value) : value;
      return typeof parsed === "number" && Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
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
      Promise.resolve({ ...transportFailure({}), GetErrorMessage: () => reason });
    // The request arrives already encoded. Steam's encoder yields a Message, which answers toObject(),
    // so the fields are readable without decoding bytes; anything that does not is treated as empty
    // rather than guessed at.
    // The request SendMsg receives is an envelope, not the message: Steam's encoder wraps the
    // generated message with a header, and the fields live on `Body()`. The body's toObject() keys
    // by the declared field name, so block_device_id comes back spelled as the message declares it.
    // Anything that does not fit that shape is read as empty rather than guessed at.
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
          // Unmount names the volume, the drive-level actions name the drive, and Adopt is Steam's
          // "make this drive a library" — its Format Drive modal sends Adopt, not Format, with the
          // typed name and a validate flag. All of it travels; the host decides what each means.
          const payload = {
            driveId: asId(fields.drive_id),
            blockDeviceId: asId(fields.block_device_id),
            // The host checks the label against the format it writes.
            label: typeof fields.label === "string" ? fields.label : "",
            validate: fields.validate === true,
          };
          lastPayload = JSON.stringify(payload);
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
      const selection = runtime.storageProvider();
      if (selection.transportModule !== 1) {
        lastError =
          "transport provider module was not a unique match: " + selection.transportModule;
        return false;
      }
      if (selection.provider !== 1) {
        lastError = "transport provider export not identified: " + selection.provider;
        return false;
      }
      // Call only the uniquely identified singleton accessor, once. A changed return shape is
      // a refusal, never a reason to try another export.
      const provider = selection.accessor();
      if (typeof provider?.GetDefaultTransport !== "function") {
        lastError = "transport provider has no GetDefaultTransport";
        return false;
      }
      const candidate = provider.GetDefaultTransport();
      if (!candidate || typeof candidate.SendMsg !== "function") {
        lastError = "transport provider yielded no SendMsg transport";
        return false;
      }
      transport = candidate;
      return true;
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
      const resolved = attemptResolution(resolve, (error) => {
        lastError = "storage transport resolution failed: " + String(error);
      });
      if (!resolved) return { ok: false, error: lastError };
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
      // The one shared query client, found by the module that builds it and by its shape (rpc.ts):
      // the export names are minified and change between builds, the shape does not.
      queryClient = resolveQueryClient(runtime);
      unsubscribe = subscribe(patchId, (published) => {
        if (!published || typeof published !== "object") return;
        const drives = Array.isArray(published.drives) ? published.drives : [];
        const devices = Array.isArray(published.blockDevices) ? published.blockDevices : [];
        // Every field Steam declares, not only the ones an action needs. The client formats what it
        // is given without checking it got anything: a drive with no size_bytes renders "NaN B of
        // NaN B", and one with no adopt_stage renders a spinner forever, because undefined compares
        // unequal to the idle stage. Both were observed on the live page before this.
        state = {
          drives: drives.map((drive) => ({
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
          block_devices: devices.map((device) => ({
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
      // Released before the gate forgets it: the wrapper sits on the transport every service call
      // takes, so a failed release must stay installed and be retried by the next remove.
      const released = releaseMember(transport, "SendMsg", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "storage transport release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
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
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("storage", createStorageService());
  // @fragment gates/theme-styles.ts
  /**
   * Reconciles host CSS into owned style elements across eligible Steam documents.
   * @returns Install/remove controls and diagnostics; remove must release this gate before its bridge is replaced. The windows accessor reports currently reachable documents and owned nodes.
   */
  function createThemeStyles() {
    const patchId = "steam-ui.theme-styles";
    // Every node this gate appends carries the class, and only nodes with it are ever removed.
    // CSSLoader's own is `css-loader-style`, which it bulk-removes; a different class is what lets
    // the two run beside each other.
    const OwnedClass = "steam-ui-theme-style";
    const IdPrefix = "steam-ui-theme-";
    const HashKey = "steamUiHash";
    // React's HostPortal fiber tag, the one whose stateNode carries the container it renders into.
    const HostPortalTag = 4;
    let installed = false;
    let unsubscribe = null;
    // The popup manager's registration for windows Steam creates, or null when it offers none.
    let popupsWatched = null;
    let desired = {
      styles: [],
      signature: "",
      revision: 0,
    };
    let lastOutcome = "never reconciled";
    let lastError = "";
    let windowsSeen = 0;
    let documentsStyled = 0;
    let nodesInstalled = 0;
    // Compiled title patterns, once each: a pattern that does not compile matches nothing. Held for
    // the current publication only, so a theme the host turned off leaves nothing behind.
    const patterns = new Map();
    // Types only, and an id a node can carry. However many themes are on and however large their CSS,
    // every one is installed.
    const validStyle = (style) =>
      !!style &&
      typeof style.id === "string" &&
      /^[A-Za-z0-9_.:-]+$/u.test(style.id) &&
      typeof style.css === "string" &&
      typeof style.hash === "string" &&
      style.hash.length > 0 &&
      Array.isArray(style.targets) &&
      style.targets.length > 0 &&
      style.targets.every((target) => typeof target === "string" && target.length > 0);
    // Steam's popup manager, by the name Valve publishes it under; null when it is not where Valve
    // keeps it today.
    const popupManager = () => {
      try {
        const manager = window.g_PopupManager;
        return manager && typeof manager.GetPopups === "function" ? manager : null;
      } catch {
        return null;
      }
    };
    // What a target is compared against, read fresh each pass because a window navigates.
    const factsOf = (doc, name) => {
      const win = doc.defaultView;
      const classes = [
        ...Array.from(doc.documentElement?.classList ?? []),
        ...Array.from(doc.body?.classList ?? []),
        ...Array.from(doc.head?.classList ?? []),
      ].map(String);
      return {
        doc,
        name: String(name || win?.name || ""),
        title: String(doc.title ?? ""),
        url: String(win?.location?.href ?? doc.location?.href ?? ""),
        classes,
      };
    };
    // Every window Steam is rendering into, each document once: the popups the manager lists, then
    // the containers of every portal in the mounted trees. SharedJSContext's own document is not a
    // window anyone looks at and is left out.
    const steamDocuments = () => {
      const found = new Map();
      const consider = (doc, name = "") => {
        try {
          if (!doc || doc === document || !doc.head || found.has(doc)) return;
          found.set(doc, factsOf(doc, name));
        } catch {
          // A window mid-navigation can refuse every read; it is looked at again next pass.
        }
      };
      const manager = popupManager();
      if (manager) {
        let popups = [];
        try {
          popups = Array.from(manager.GetPopups() ?? []);
        } catch {
          popups = [];
        }
        for (const popup of popups) {
          try {
            consider(popup?.m_popup?.document, String(popup?.m_strName ?? ""));
          } catch {
            // As above.
          }
        }
      }
      walkFibers(reactRootFibers(), MaximumMountedNodes, (fiber) => {
        if (fiber.tag !== HostPortalTag) return false;
        const container = fiber.stateNode?.containerInfo;
        if (!container) return false;
        consider(container.nodeType === 9 ? container : container.ownerDocument);
        return false;
      });
      windowsSeen = found.size;
      return [...found.values()];
    };
    // CSSLoader's compare(): `~text~` is a URL substring, `!name` a class on the document's root
    // elements, anything else a whole-title regular expression. The expression is also tried against
    // the window's name, which is what the title is on the Deck and what stays stable on Windows.
    const matchesTarget = (target, facts) => {
      if (target.length > 2 && target.startsWith("~") && target.endsWith("~")) {
        return facts.url.includes(target.slice(1, -1));
      }
      if (target.startsWith("!")) {
        return facts.classes.includes(target.slice(1));
      }
      let pattern = patterns.get(target);
      if (pattern === undefined) {
        try {
          pattern = new RegExp(`^(${target})$`, "u");
        } catch {
          pattern = null;
        }
        patterns.set(target, pattern);
      }
      return (
        !!pattern &&
        (pattern.test(facts.title) || (facts.name.length > 0 && pattern.test(facts.name)))
      );
    };
    const ownedNodes = (doc) => {
      try {
        return Array.from(doc.head.querySelectorAll("style." + OwnedClass));
      } catch {
        return [];
      }
    };
    // One document brought in step with the publication: the blocks whose targets name it, in the
    // order published, each exactly once. A head already holding that list, block for block and hash
    // for hash, is left alone; anything else is rebuilt, because order is part of what a theme means.
    // What a document wants is a function of the publication and the document's facts, both of
    // which rarely change between reconciliations (a publication, a new window, a window's load), so
    // the match is kept per document until either does.
    const wantedByDocument = new WeakMap();
    const wantedFor = (facts) => {
      const key = `${desired.signature}\u0000${facts.name}\u0000${facts.title}\u0000${facts.url}\u0000${facts.classes.join(" ")}`;
      const cached = wantedByDocument.get(facts.doc);
      if (cached && cached.key === key) return cached.wanted;
      const wanted = desired.styles.filter((style) =>
        style.targets.some((target) => matchesTarget(target, facts)),
      );
      wantedByDocument.set(facts.doc, { key, wanted });
      return wanted;
    };
    const reconcileDocument = (facts) => {
      const wanted = wantedFor(facts);
      const owned = ownedNodes(facts.doc);
      const same =
        owned.length === wanted.length &&
        owned.every(
          (node, index) =>
            node.id === IdPrefix + wanted[index].id &&
            node.dataset?.[HashKey] === wanted[index].hash,
        );
      if (!same) {
        for (const node of owned) node.remove();
        for (const style of wanted) {
          const node = facts.doc.createElement("style");
          node.id = IdPrefix + style.id;
          node.className = OwnedClass;
          node.dataset[HashKey] = style.hash;
          node.textContent = style.css;
          facts.doc.head.append(node);
        }
      }
      return wanted.length;
    };
    const reconcile = () => {
      if (!installed) return;
      // With nothing published and nothing installed there is no window to bring in step, and the
      // walk over every mounted fiber that finds the windows is not worth doing.
      if (desired.styles.length === 0 && nodesInstalled === 0) {
        lastOutcome = "idle: no styles";
        return;
      }
      try {
        let styled = 0;
        let nodes = 0;
        for (const facts of steamDocuments()) {
          const count = reconcileDocument(facts);
          if (count > 0) styled++;
          nodes += count;
        }
        documentsStyled = styled;
        nodesInstalled = nodes;
        lastOutcome = `windows=${windowsSeen} documents=${styled} nodes=${nodes} styles=${desired.styles.length}`;
        lastError = "";
      } catch (error) {
        lastError = "theme reconciliation failed: " + String(error);
      }
    };
    const clearAll = () => {
      let removed = 0;
      for (const facts of steamDocuments()) {
        for (const node of ownedNodes(facts.doc)) {
          try {
            node.remove();
            removed++;
          } catch {
            // A node whose document is gone has nothing left to remove.
          }
        }
      }
      documentsStyled = 0;
      nodesInstalled = 0;
      return removed;
    };
    // What a publication changes, without stringifying megabytes of CSS on every round: a block's
    // identity and hash, its targets, and the order.
    const signatureOf = (styles) =>
      styles.map((style) => `${style.id}#${style.hash}@${style.targets.join("|")}`).join(";");
    const install = () => {
      if (installed) return { ok: true, alreadyInstalled: true };
      if (!popupManager() && reactRootFibers().length === 0) {
        lastError = "neither Steam's popup manager nor a mounted React tree was found";
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        const styles = Array.isArray(state?.styles) ? state.styles.filter(validStyle) : [];
        const revision = Number.isSafeInteger(state?.revision) ? state.revision : 0;
        const signature = signatureOf(styles);
        if (signature === desired.signature && revision === desired.revision) return;
        if (signature !== desired.signature) patterns.clear();
        desired = { styles, signature, revision };
        reconcile();
      });
      // A window Steam creates later is styled when Steam announces it, and again once it has loaded,
      // since a popup's document can still be the blank one when the callback runs.
      try {
        const manager = popupManager();
        if (manager && typeof manager.AddPopupCreatedCallback === "function") {
          popupsWatched = manager.AddPopupCreatedCallback((popup) => {
            reconcile();
            try {
              (popup?.m_popup ?? popup?.window)?.addEventListener?.("load", reconcile, {
                once: true,
              });
            } catch {
              // A window that refuses the listener was styled above; the next publication covers it.
            }
          });
        }
      } catch {
        popupsWatched = null;
      }
      reconcile();
      return { ok: true, installed: true };
    };
    // Every owned node in every window goes before the gate forgets it holds anything, so a window
    // keeps no theme once the host has retracted it, and Steam's own styling is what remains.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      try {
        popupsWatched?.Unregister?.();
      } catch {
        // The manager went with its window; nothing is left to call back.
      }
      popupsWatched = null;
      unsubscribe = endSubscription(unsubscribe);
      const removed = clearAll();
      desired = { styles: [], signature: "", revision: 0 };
      patterns.clear();
      installed = false;
      lastOutcome = `removed ${removed}`;
      return { ok: true, removed: true, nodes: removed };
    };
    // The windows and what they are matched by, for a host's diagnostics.
    const windows = () =>
      steamDocuments().map((facts) => ({
        name: facts.name,
        title: facts.title,
        url: facts.url,
        nodes: ownedNodes(facts.doc).length,
      }));
    const status = () => ({
      ok: true,
      installed,
      resolved: !!popupManager() || reactRootFibers().length > 0,
      windows: windowsSeen,
      watchingPopups: !!popupsWatched,
      documents: documentsStyled,
      nodes: nodesInstalled,
      styles: desired.styles.length,
      revision: desired.revision,
      lastOutcome,
      lastError,
    });
    return { install, remove, status, windows };
  }
  registerGate("themeStyles", createThemeStyles());
  // @fragment components.ts
  /**
   * Owns host Quick Access controls and their shared Steam render claims.
   * @returns Per-kind install/remove/status operations and a dispose operation that releases subscriptions and claims.
   */
  function createNativeComponentHost() {
    let unsubscribePlugins = null;
    const registrations = new Map();
    const listeners = new Set();
    let runtime;
    let controlRuntime;
    let autoTdpControl;
    let frameLimitControl;
    let controllerControl;
    let powerProfileControl;
    let hybridCoreControl;
    let cpuBoostControl;
    let powerPresetControl;
    let resolutionControl;
    let audioFormatControl;
    let settingsSectionsControl;
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
    // This host's name on the shared useMemo claim (ownership.ts).
    const MemoName = "quickAccessTabs";
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
    // What a folded section says on its heading's detail line. A row with a value worth a glance
    // leaves it here as it renders, and the section joins its rows'. Rows stay mounted while their
    // section is folded, so the line stays current.
    const summaries = {};
    const summarize = (kind, text) => {
      summaries[kind] = typeof text === "string" ? text : "";
    };
    // Which of the host's own rows drew something on their last render. A section header exists for
    // the rows under it, so a section whose rows all returned null is only a title: with no device
    // coordinator, Power limits and Controller were exactly that. Valve's rows report nothing here
    // and count as drawn.
    const drawnKinds = new Set();
    let layoutQueued = false;
    const setDrawn = (kind, drawn) => {
      if (drawnKinds.has(kind) === drawn) return;
      if (drawn) drawnKinds.add(kind);
      else drawnKinds.delete(kind);
      // Recorded while a row renders, so the panel roots hear about it afterwards instead of being
      // updated from inside another component's render.
      if (layoutQueued) return;
      layoutQueued = true;
      queueMicrotask(() => {
        layoutQueued = false;
        notify();
      });
    };
    const drew = (kind, outcome = "rendered") => {
      setDrawn(kind, true);
      renderOutcomes[kind] = outcome;
    };
    const note = (kind, reason) => {
      setDrawn(kind, false);
      delete summaries[kind];
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
      cpuBoost: Object.freeze({
        patchId: "steam-ui.cpu-boost",
        command: "setCpuBoost",
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
      settingsSections: Object.freeze({ patchId: "steam-ui.settings-sections", command: "set" }),
      audioFormat: Object.freeze({
        patchId: "steam-ui.audio-format",
        formatCommand: "setFormat",
        spatialCommand: "setSpatial",
      }),
      deviceControls: Object.freeze({
        patchId: "steam-ui.device-controls",
        chargeCommand: "setChargeLimit",
        brightnessCommand: "setLightingBrightness",
        colorCommand: "setLightingColor",
      }),
      // Which of the panel's own sections are folded, kept by the host so Steam rebuilding a tab
      // does not open them again. Not a row: the state is read by the panel roots. A host without
      // the module still gets folding sections; they last the session.
      panelFolds: Object.freeze({
        patchId: SteamFoldsPatchId,
        command: "setFolded",
      }),
      // The host's Quick Access layout: sections, headings, glyphs, folds and the accent label. Not a
      // row either, and it takes no command. Without it each tab draws its rows in one untitled group.
      quickAccessLayout: Object.freeze({
        patchId: "steam-ui.quick-access-layout",
        command: "",
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
    // Every row command carries a fresh action generation, so its echo can be matched to the write.
    const sendCommand = (definition, command, payload) =>
      request(definition.patchId, command, payload, nextActionGeneration(definition.patchId));
    // A controlled switch's change: a boolean that differs from what the device reports is sent, and
    // its refusal goes to `refuse` for the row's description; the next change clears it ("").
    const toggleCommand = (definition, state, refuse) => (enabled) => {
      if (typeof enabled !== "boolean" || enabled === state.enabled) return;
      refuse("");
      void sendCommand(definition, definition.command, { enabled }).catch((reason) =>
        refuse(refusalText(reason)),
      );
    };
    // The one function export carrying every token. Through the shared matcher, so an export Steam
    // aliases under two names counts once and a getter that throws counts as no match.
    // The sections' folds, the mechanism every Quick Access tab shares (gate-helpers.ts).
    const panelFolds = createSteamFolds();
    const normalizePanelFoldsState = (value) => panelFolds.normalize(value);
    const isFolded = (open, id) => panelFolds.isFolded(open, id);
    const setFolded = (id, folded) => panelFolds.setFolded(id, folded, notify);
    const uniqueFunction = (exports, requiredTokens) =>
      uniqueSteamExport(
        exports,
        (value) =>
          typeof value === "function" &&
          requiredTokens.every((token) => String(value).includes(token)),
      );
    const createControlRuntime = () => {
      const controls = resolveSteamFieldComponents(runtime);
      const panel = resolveSteamPanelComponents(runtime);
      if (!controls || !panel) return null;
      const react = controls.react;
      const slider = controls.sliderField;
      const dropdown = controls.dropdown;
      // Steam's own ToggleField, from the same module as the slider and dropdown above. Selected by
      // the two markers of its class body rather than by its export name, which is minified and
      // changes with every client build. Live-verified 2026-08-29: exactly one export matches, and
      // the provider that names the module's fields lists that same class as ToggleField.
      const toggle = controls.toggleField;
      // Valve's read-only label/value row, from the Field module rather than the fields module: it
      // is what a figure the panel only reports — the profile actually in effect — is supposed to
      // look like. Without it that line was a bare div with none of Steam's type, spacing or
      // separator, which is exactly how it read. `#Field_MoreInfo_Action` occurs once in the whole
      // client bundle, so the module is unambiguous, and only this export draws LabelFieldValue.
      const labelFieldFactory = runtime.findUnique([
        "#Field_MoreInfo_Action",
        "spacingBetweenLabelAndChild",
      ]);
      const labelField = labelFieldFactory
        ? uniqueFunction(runtime(labelFieldFactory[0]), [
            "LabelFieldValue",
            "spacingBetweenLabelAndChild",
          ])
        : null;
      const { section, row } = panel;
      // Valve's localize-with-fallback; every row's label needs it.
      const localize = resolveSteamLocalizer(runtime);
      if (!slider || !dropdown || !localize) return null;
      // The toggle and the label field are deliberately not in that guard. They arrived after the
      // other four, so a client where either cannot be found still gets every control that does not
      // need one, rather than losing the whole native surface.
      // The icon renderer is built once per control runtime and closes over Steam's React, so a row
      // asks for a glyph by name and never touches element construction itself.
      const icon = createIconRenderer(react);
      // Steam's Focusable, for the kit's folding section headings. Not in the guard either: without
      // it a heading is a plain div and the sections simply do not fold, so a client where it is
      // not a unique match keeps every row.
      let focusable = null;
      try {
        focusable = resolveNativeFocusable(runtime);
      } catch {
        focusable = null;
      }
      return {
        react,
        slider,
        dropdown,
        toggle,
        labelField,
        section,
        row,
        localize,
        icon,
        focusable,
      };
    };
    const normalizeText = (value) => (typeof value === "string" ? value : "");
    // A row the host marks says so in its own description, in Steam's accent colour, led by the label the
    // host's layout publishes (steam-ui.quick-access-layout). The toolkit holds no word of its own for it.
    const accentDescription = (controlRuntime, accent, text) => {
      const label = accent
        ? normalizeText(acceptedStates.get("quickAccessLayout")?.accentLabel)
        : "";
      return steamAccentDescription(
        controlRuntime.react,
        [label, text].filter(Boolean).join(" · "),
        accent === true,
      );
    };
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
        accent: value.accent === true,
      });
    };
    const normalizeAutoTdpState = (value) => {
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      if (typeof value.enabled !== "boolean" || typeof value.controlling !== "boolean") return null;
      // The watts figure is only ever a display detail beside the switch, so a value that is not a
      // positive whole number is dropped rather than rejecting the whole state and taking the switch
      // away with it.
      const watts =
        typeof value.watts === "number" && Number.isInteger(value.watts) && value.watts >= 1
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
      if (!Array.isArray(value.targets)) return null;
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
        if (!/^[A-Za-z0-9._-]+$/.test(id) || !label || ids.has(id)) return null;
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
        accent: value.accent === true,
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
        // is not addressable right now — a host reports it when Steam has named a running game
        // whose executable Windows has not exposed, so the cap is saved against the game rather
        // than sprayed onto the global profile. It was missing from this list, and a settled
        // outcome the host can legitimately report was therefore rejected as malformed: adjusting
        // the frame-limit slider while a game was starting deleted the row the user had just
        // touched (2026-09-04). Not busy — the value is stored, and the row stays live.
        "deferred",
        "applied",
        "rejected",
        "failed",
        "external-change",
      ]);
      if (!progress) return null;
      return Object.freeze({
        available: value.available,
        progress,
        fault: normalizeText(value.fault),
        statusText: normalizeText(value.statusText),
        accent: value.accent === true,
      });
    };
    // Validated rather than trusted, like every other semantic state: this arrives over the bridge
    // and a malformed option list would render a dropdown whose entries select nothing.
    const normalizeResolutionState = (value) => {
      if (!value || typeof value !== "object") return null;
      const options = Array.isArray(value.options)
        ? value.options.filter(
            (option) => typeof option === "string" && /^[1-9][0-9]*x[1-9][0-9]*$/.test(option),
          )
        : [];
      return {
        available: value.available === true,
        options,
        current: typeof value.current === "string" ? value.current : "",
        statusText: typeof value.statusText === "string" ? value.statusText : "",
      };
    };
    const normalizeAudioFormatState = (value) => {
      if (!value || typeof value !== "object") return null;
      const options = (items) => {
        const values = [];
        if (!Array.isArray(items)) return values;
        for (const item of items) {
          if (!item || typeof item !== "object") continue;
          const id = normalizeText(item.id);
          const label = normalizeText(item.label);
          if (id && label) values.push(Object.freeze({ id, label }));
        }
        return values;
      };
      const channelOptions = options(value.channelOptions);
      const currentChannels = normalizeText(value.currentChannels);
      const formatOptions = options(value.formatOptions);
      const spatialOptions = options(value.spatialOptions);
      const distinct = (items) => new Set(items.map((item) => item.id)).size === items.length;
      if (!distinct(channelOptions) || !distinct(formatOptions) || !distinct(spatialOptions))
        return null;
      const currentFormat = normalizeText(value.currentFormat);
      const currentSpatial = normalizeText(value.currentSpatial);
      if (
        (currentChannels && !channelOptions.some((item) => item.id === currentChannels)) ||
        (currentFormat && !formatOptions.some((item) => item.id === currentFormat)) ||
        (currentSpatial && !spatialOptions.some((item) => item.id === currentSpatial))
      )
        return null;
      return Object.freeze({
        available: value.available === true,
        channelOptions: Object.freeze(channelOptions),
        currentChannels,
        formatOptions: Object.freeze(formatOptions),
        currentFormat,
        spatialOptions: Object.freeze(spatialOptions),
        currentSpatial,
        statusText: normalizeText(value.statusText),
      });
    };
    // A host's reading for a range row, clamped into the range, or null when it is not a number.
    const clampReading = (value, minimum, maximum) =>
      typeof value === "number" && Number.isFinite(value)
        ? Math.min(maximum, Math.max(minimum, value))
        : null;
    const normalizeDeviceRange = (value) => {
      if (value === null || value === undefined) return null;
      if (!value || typeof value !== "object" || typeof value.available !== "boolean") return null;
      const minimum = Number(value.minimum);
      const maximum = Number(value.maximum);
      const step = Number(value.step);
      // The descriptor decides whether the slider draws; the readings only where it sits, clamped
      // into the range, with an off-step one shown as is until the user moves it.
      if (
        !Number.isInteger(minimum) ||
        !Number.isInteger(maximum) ||
        !Number.isInteger(step) ||
        minimum < 0 ||
        maximum > 100 ||
        minimum >= maximum ||
        step < 1 ||
        step > maximum - minimum
      )
        return null;
      return Object.freeze({
        available: value.available,
        minimum,
        maximum,
        step,
        desired: clampReading(value.desired, minimum, maximum),
        observed: clampReading(value.observed, minimum, maximum),
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
        accent: value.accent === true,
      });
    };
    const normalizeDeviceControlsState = (value) => {
      if (!value || typeof value !== "object" || !Array.isArray(value.lightingZones)) return null;
      const chargeLimit = normalizeDeviceRange(value.chargeLimit);
      const lightingBrightness = normalizeDeviceRange(value.lightingBrightness);
      const lightingZones = [];
      const ids = new Set();
      for (const zone of value.lightingZones) {
        if (!zone || typeof zone !== "object") return null;
        const id = normalizeText(zone.id);
        const label = normalizeText(zone.label);
        const desiredColor = zone.desiredColor === null ? null : Number(zone.desiredColor);
        const observedColor = zone.observedColor === null ? null : Number(zone.observedColor);
        if (
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
            accent: zone.accent === true,
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
      // the only place the user could have corrected the value. Observed on a handheld (2026-09-03),
      // where a 12 FPS cap under a floor of 30 took the Quick Access slider away entirely and left
      // no way to put it back. The bookends stretch to reach the value instead.
      const capUnusable = (fps) => fps !== null && (!Number.isInteger(fps) || fps < 0);
      if (
        (minimumFps !== null &&
          maximumFps !== null &&
          (!Number.isInteger(minimumFps) ||
            !Number.isInteger(maximumFps) ||
            minimumFps < 0 ||
            maximumFps < minimumFps)) ||
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
    // The last state each kind accepted. A row mounted again when the panel reopens starts from it,
    // so it draws on its first render rather than reporting nothing until the replay arrives, which
    // would flash its section out of layout and back.
    const acceptedStates = new Map();
    const useSemanticState = (controlRuntime, kind, normalize) => {
      const definition = definitions[kind];
      const [state, setState] = controlRuntime.react.useState(
        () => acceptedStates.get(kind) ?? null,
      );
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
            acceptedStates.set(kind, normalized);
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
    // rendering on the reference device, "#QuickAccess_Tab_Perf_FramerateLimit" and
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
    // is what should be handed to the field; only the "#" test needs the text inside it (textOf).
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
        const [refusal, setRefusal] = controlRuntime.react.useState("");
        if (!state) return note("vrr", "no state");
        if (!state.available)
          return note("vrr", "unavailable: " + (state.statusText || "no reason"));
        if (!controlRuntime.toggle) return note("vrr", "Steam ToggleField was not resolved");
        drew("vrr");
        summarize("vrr", state.enabled ? "VRR on" : "VRR off");
        const definition = definitions.vrr;
        const toggle = controlRuntime.react.createElement(controlRuntime.toggle, {
          // Valve's own token for the row, so the label matches the client's language even though
          // the component behind it is the host's.
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Perf_EnableVRR",
            "Variable refresh rate",
          ),
          icon: controlRuntime.icon("pulse"),
          description: refusal || accentDescription(controlRuntime, state.accent, state.statusText),
          checked: state.enabled,
          // Controlled: the switch shows what the device reports, so a write the panel refuses
          // leaves it where the hardware actually is rather than where it was clicked.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: toggleCommand(definition, state, setRefusal),
        });
        return toggle;
      };
    const createAutoTdpControl = (controlRuntime) =>
      function SteamUiAutoTdpControl() {
        const state = useSemanticState(controlRuntime, "autoTdp", normalizeAutoTdpState);
        const [refusal, setRefusal] = controlRuntime.react.useState("");
        if (!state) return note("autoTdp", "no state");
        if (!state.available)
          return note("autoTdp", "unavailable: " + (state.statusText || "no reason"));
        // Deliberately outside createControlRuntime's guard, so a client whose ToggleField cannot
        // be located loses only this row. That silence is exactly what needed a name.
        if (!controlRuntime.toggle) return note("autoTdp", "Steam ToggleField was not resolved");
        drew("autoTdp");
        summarize(
          "autoTdp",
          !state.enabled
            ? ""
            : state.controlling && state.watts !== null
              ? `Auto TDP holding ${state.watts} W`
              : "Auto TDP on",
        );
        const definition = definitions.autoTdp;
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
          description: refusal || description || undefined,
          checked: state.enabled,
          // Controlled, so the switch shows the stored setting rather than its own click. A command
          // that does not land leaves the switch where the setting actually is instead of showing a
          // change that did not happen.
          controlled: true,
          disabled: isBusy(state.progress),
          onChange: toggleCommand(definition, state, setRefusal),
        });
      };
    const normalizePowerProfileState = (value) => {
      if (!value || typeof value.available !== "boolean" || !Array.isArray(value.options))
        return null;
      const ids = new Set();
      const options = [];
      for (const item of value.options) {
        const label = normalizeText(item?.label);
        if (
          !item ||
          typeof item.id !== "string" ||
          !/^[A-Za-z0-9._-]+$/.test(item.id) ||
          !label.trim() ||
          ids.has(item.id)
        )
          return null;
        ids.add(item.id);
        // An option the host marks unselectable names a state the user cannot pick, such as values
        // that match none of the presets: it is listed only where it is the current value, and never
        // sent.
        options.push({ id: item.id, label, selectable: item.selectable !== false });
      }
      return {
        available: value.available,
        options,
        current: normalizeText(value.current),
        statusText: normalizeText(value.statusText),
      };
    };
    // A refused write's reason, whole, for the row's description.
    const refusalText = (reason) => normalizeText(String(reason?.message ?? reason));
    // One dropdown write: pending while it is in flight, and its refusal handed to `refuse` for the
    // row's description rather than swallowed. The next write clears it ("") as it starts.
    const sendPending = (setPending, refuse, sent) => {
      setPending(true);
      refuse("");
      void sent.catch((reason) => refuse(refusalText(reason))).finally(() => setPending(false));
    };
    // The Windows power profile, processor core and CPU boost rows: one dropdown over the same state
    // shape, differing in kind, label, glyph and how a state describes itself (the boost row carries
    // the per-game marker). No options is nothing to choose, so the reason goes to renderOutcomes
    // rather than onto an empty, disabled dropdown. The component keeps the name it is created under,
    // and the glyph is built by the caller so each row's `icon("…")` stays a literal the glyph
    // ownership check can read.
    const createChoiceControl = (
      controlRuntime,
      kind,
      label,
      name,
      icon,
      normalize = normalizePowerProfileState,
      describe = (state) => state.statusText || undefined,
    ) =>
      ({
        [name]: function () {
          const state = useSemanticState(controlRuntime, kind, normalize);
          const [pending, setPending] = controlRuntime.react.useState(false);
          const [refusal, setRefusal] = controlRuntime.react.useState("");
          if (!state) return note(kind, "no state");
          if (!state.options.length)
            return note(kind, "no options: " + (state.statusText || "no reason"));
          const options = state.options
            .filter((option) => option.selectable || option.id === state.current)
            .map((option) => ({ data: option.id, label: option.label }));
          const selectable = (id) =>
            state.options.some((option) => option.id === id && option.selectable);
          const definition = definitions[kind];
          drew(kind);
          summarize(kind, options.find((option) => option.data === state.current)?.label ?? "");
          return controlRuntime.react.createElement(controlRuntime.dropdown, {
            label,
            icon: icon(),
            rgOptions: options,
            selectedOption: options.some((option) => option.data === state.current)
              ? state.current
              : undefined,
            disabled: pending || !state.available || options.length < 2,
            description: refusal || describe(state),
            layout: "below",
            onChange: (option) => {
              if (
                pending ||
                !state.available ||
                !option ||
                option.data === state.current ||
                !selectable(option.data)
              )
                return;
              sendPending(
                setPending,
                setRefusal,
                sendCommand(definition, definition.command, { target: option.data }),
              );
            },
          });
        },
      })[name];
    const createPowerProfileControl = (controlRuntime) =>
      createChoiceControl(
        controlRuntime,
        "powerProfile",
        "Windows power profile",
        "SteamUiPowerProfileControl",
        () => controlRuntime.icon("power"),
      );
    // A processor with one kind of core publishes no options, and has nothing to show here.
    const createHybridCoreControl = (controlRuntime) =>
      createChoiceControl(
        controlRuntime,
        "hybridCores",
        "Processor cores",
        "SteamUiHybridCoreControl",
        () => controlRuntime.icon("cores"),
      );
    // The power-profile shape plus the per-game marker, which the boost row's description carries.
    const normalizeCpuBoostState = (value) => {
      const state = normalizePowerProfileState(value);
      return state ? { ...state, accent: value.accent === true } : null;
    };
    const createCpuBoostControl = (controlRuntime) =>
      createChoiceControl(
        controlRuntime,
        "cpuBoost",
        "CPU boost mode",
        "SteamUiCpuBoostControl",
        () => controlRuntime.icon("turbo"),
        normalizeCpuBoostState,
        (state) => accentDescription(controlRuntime, state.accent, state.statusText),
      );
    const normalizePowerPresetState = (value) => {
      const state = normalizePowerProfileState(value);
      if (!state || typeof value.ac !== "string" || typeof value.battery !== "string") return null;
      const valid = (id) => id === "" || state.options.some((option) => option.id === id);
      if (!valid(value.ac) || !valid(value.battery)) return null;
      return {
        ...state,
        ac: value.ac,
        battery: value.battery,
        scope: normalizeText(value.scope),
        unsetLabel: normalizeText(value.unsetLabel),
        acAccent: value.acAccent === true,
        batteryAccent: value.batteryAccent === true,
      };
    };
    const createPowerPresetControl = (controlRuntime) =>
      function SteamUiPowerAssignments() {
        const state = useSemanticState(controlRuntime, "powerPreset", normalizePowerPresetState);
        const [pending, setPending] = controlRuntime.react.useState(false);
        // A refusal belongs to the assignment whose write it answered.
        const [refusal, setRefusal] = controlRuntime.react.useState(null);
        if (!state || !state.options.length) return note("powerPreset", "no state");
        const options = [
          { data: "", label: state.unsetLabel || "Manual selection", selectable: true },
          ...state.options.map((option) => ({
            data: option.id,
            label: option.label,
            selectable: option.selectable,
          })),
        ];
        const definition = definitions.powerPreset;
        // The unset entry is the way back to Global for a game's own assignment, so the override needs
        // only its marker here, not a second control.
        const assignment = (label, iconName, selected, command, accent, description) =>
          controlRuntime.react.createElement(controlRuntime.dropdown, {
            label,
            icon: controlRuntime.icon(iconName),
            layout: "below",
            description:
              refusal && refusal.command === command
                ? refusal.text
                : accentDescription(controlRuntime, accent, description),
            rgOptions: options
              .filter((option) => option.selectable || option.data === selected)
              .map((option) => ({ data: option.data, label: option.label })),
            selectedOption: selected,
            disabled: pending || !state.available,
            onChange: (option) => {
              if (
                pending ||
                !state.available ||
                !option ||
                !options.some((item) => item.data === option.data && item.selectable)
              )
                return;
              sendPending(
                setPending,
                (text) => setRefusal(text ? { command, text } : null),
                sendCommand(definition, command, { target: option.data || null }),
              );
            },
          });
        drew("powerPreset");
        // The two assignments, named; an unset one says nothing.
        const assigned = (label, id) =>
          id ? `${label} ${options.find((option) => option.data === id)?.label ?? id}` : "";
        summarize(
          "powerPreset",
          [assigned("Plugged in", state.ac), assigned("Battery", state.battery)]
            .filter(Boolean)
            .join(" · ") || state.current,
        );
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
          assignment(
            "When plugged in",
            "plug",
            state.ac,
            definition.acCommand,
            state.acAccent,
            orphaned,
          ),
          assignment(
            "On battery",
            "battery",
            state.battery,
            definition.batteryCommand,
            state.batteryAccent,
          ),
        );
      };
    const createControllerControl = (controlRuntime) =>
      function SteamUiControllerTargetControl() {
        const state = useSemanticState(
          controlRuntime,
          "controllerTarget",
          normalizeControllerState,
        );
        const [refusal, setRefusal] = controlRuntime.react.useState("");
        if (!state) return note("controllerTarget", "no state");
        if (!state.available)
          return note("controllerTarget", "unavailable: " + (state.statusText || "no reason"));
        const options = state.targets
          .filter((target) => target.available)
          .map((target) => ({ data: target.id, label: target.label }));
        // What the host reports, or nothing when neither the observed nor the selected target is one
        // of the available ones: the dropdown still draws, with no selection, so the user can pick.
        const reported = state.observedTarget || state.selectedTarget;
        const selected = options.some((option) => option.data === reported) ? reported : undefined;
        if (!options.length) return note("controllerTarget", "no available targets");
        drew("controllerTarget");
        summarize(
          "controllerTarget",
          options.find((option) => option.data === selected)?.label ?? "",
        );
        const definition = definitions.controllerTarget;
        const setTarget = (option) => {
          if (!option || !options.some((candidate) => candidate.data === option.data)) return;
          setRefusal("");
          void sendCommand(definition, definition.command, { target: option.data }).catch(
            (reason) => setRefusal(refusalText(reason)),
          );
        };
        const dropdown = controlRuntime.react.createElement(controlRuntime.dropdown, {
          label: localizeOr(
            controlRuntime,
            "#QuickAccess_Tab_Settings_Section_Controller_Title",
            "Controller",
          ),
          icon: controlRuntime.icon("swap"),
          rgOptions: options,
          selectedOption: selected,
          onChange: setTarget,
          // One target is still a choice while none is shown as selected.
          disabled: isBusy(state.progress) || (options.length < 2 && selected !== undefined),
          description: refusal || accentDescription(controlRuntime, state.accent, state.statusText),
          layout: "below",
        });
        return dropdown;
      };
    const createResolutionControl = (controlRuntime) =>
      function SteamUiResolutionControl() {
        const state = useSemanticState(controlRuntime, "resolution", normalizeResolutionState);
        const [refusal, setRefusal] = controlRuntime.react.useState("");
        if (!state) return note("resolution", "no state");
        if (!state.available)
          return note("resolution", "unavailable: " + (state.statusText || "no reason"));
        if (state.options.length < 2)
          return note("resolution", `only ${state.options.length} option(s)`);
        drew("resolution");
        summarize("resolution", state.options.includes(state.current) ? state.current : "");
        const definition = definitions.resolution;
        const options = state.options.map((option) => ({ data: option, label: option }));
        const setResolution = (option) => {
          // Checked against the offered list before sending. The row cannot be the only thing
          // standing between a stray value and a mode change, but it should not be the source of
          // one either.
          if (!option || !state.options.includes(option.data)) return;
          // "target" rather than "value": that is the payload shape every dropdown here uses, and
          // the host's reader rejects an object carrying anything else.
          setRefusal("");
          void sendCommand(definition, definition.command, { target: option.data }).catch(
            (reason) => setRefusal(refusalText(reason)),
          );
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
          description: refusal || state.statusText || undefined,
          layout: "below",
        });
      };
    // Typed host settings reuse the same native fields as a routed settings page.
    const createSettingsSectionsControl = (controlRuntime, ui) =>
      function SteamUiSettingsSectionsControl() {
        const state = useSemanticState(controlRuntime, "settingsSections", (value) =>
          value && Array.isArray(value.pages) && Number.isSafeInteger(value.revision)
            ? value
            : null,
        );
        const folds = useSemanticState(controlRuntime, "panelFolds", normalizePanelFoldsState);
        const react = controlRuntime.react;
        // The settings page's own draft keeping: a refused write shows why on its row, and a
        // publication that changes another row keeps what is being typed here.
        const drafts = useSteamSettingDrafts(react, state?.revision);
        if (!state) return note("settingsSections", "no state");
        if (!ui?.valueField || !ui?.smallButton)
          return note(
            "settingsSections",
            "Steam's settings value field or small button was not resolved",
          );
        const change = drafts.change((row, value) =>
          sendCommand(definitions.settingsSections, "set", { key: row.key, value }),
        );
        const action = (row) => change(row, true);
        return react.createElement(
          react.Fragment,
          null,
          ...state.pages.map((page) => {
            const key = "settings." + page.id;
            return renderSteamUiGroup(
              controlRuntime,
              {
                key,
                title: page.title,
                collapsed: isFolded(folds, key),
                onToggle: () => setFolded(key, !isFolded(folds, key)),
              },
              ...(page.sections ?? []).map((section, index) =>
                renderSteamUiGroup(
                  controlRuntime,
                  {
                    key: key + "." + (section.id ?? index),
                    title: section.title || undefined,
                  },
                  ...(section.rows ?? []).map((row) =>
                    react.createElement(
                      controlRuntime.row,
                      { key: row.key },
                      renderSteamSettingRow(
                        ui,
                        drafts.row({ ...row, layout: "below" }),
                        drafts.draft(row),
                        change,
                        action,
                      ),
                    ),
                  ),
                ),
              ),
            );
          }),
        );
      };
    const createAudioFormatControl = (controlRuntime) =>
      function SteamUiAudioFormatControl() {
        const state = useSemanticState(controlRuntime, "audioFormat", normalizeAudioFormatState);
        const [pending, setPending] = controlRuntime.react.useState(false);
        // A refusal belongs to the dropdown whose write it answered; two share a command.
        const [refusal, setRefusal] = controlRuntime.react.useState(null);
        if (!state) return note("audioFormat", "no state");
        if (!state.available)
          return note("audioFormat", "unavailable: " + (state.statusText || "no reason"));
        const definition = definitions.audioFormat;
        const dropdown = (label, choices, current, command, icon) => {
          if (choices.length === 0) return null;
          const options = choices.map((choice) => ({ data: choice.id, label: choice.label }));
          return controlRuntime.react.createElement(controlRuntime.dropdown, {
            label,
            icon: controlRuntime.icon(icon),
            rgOptions: options,
            selectedOption: current || undefined,
            disabled: pending || choices.length < 2,
            description:
              (refusal?.label === label ? refusal.text : "") || state.statusText || undefined,
            layout: "below",
            onChange: (option) => {
              if (
                pending ||
                !option ||
                option.data === current ||
                !options.some((choice) => choice.data === option.data)
              )
                return;
              sendPending(
                setPending,
                (text) => setRefusal(text ? { label, text } : null),
                sendCommand(definition, command, { target: option.data }),
              );
            },
          });
        };
        const channels = dropdown(
          "Channels",
          state.channelOptions,
          state.currentChannels,
          definition.formatCommand,
          "audioChannels",
        );
        const format = dropdown(
          "Format",
          state.formatOptions,
          state.currentFormat,
          definition.formatCommand,
          "audioEncoding",
        );
        const spatial = dropdown(
          "Spatial sound",
          state.spatialOptions,
          state.currentSpatial,
          definition.spatialCommand,
          "audioSpatial",
        );
        if (!channels && !format && !spatial) return note("audioFormat", "fewer than two choices");
        drew("audioFormat");
        summarize(
          "audioFormat",
          [
            state.channelOptions.find((choice) => choice.id === state.currentChannels)?.label,
            state.formatOptions.find((choice) => choice.id === state.currentFormat)?.label,
            state.spatialOptions.find((choice) => choice.id === state.currentSpatial)?.label,
          ]
            .filter(Boolean)
            .join(" · "),
        );
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          channels,
          format,
          spatial,
        );
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
        const [refusal, setRefusal] = controlRuntime.react.useState("");
        if (!state) return note("frameLimit", "no state");
        if (!state.available)
          return note("frameLimit", "unavailable: " + (state.statusText || "no reason"));
        // No observed or desired cap still draws the row: the slider sits where an unset cap does and
        // hides its number until the user moves it.
        drew("frameLimit");
        const definition = definitions.frameLimit;
        // A refused write shows why under the slider until the next write.
        const send = (command, nextValue) => {
          setRefusal("");
          void sendCommand(definition, command, { value: nextValue }).catch((reason) =>
            setRefusal(refusalText(reason)),
          );
        };
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
        // Limit slider. With no cap chosen yet it sits at the highest one, because no limit means
        // the most the display can run, so switching the limit on costs nothing until it is moved.
        const capped = state.limitEnabled && echoed.value > 0;
        const cappedValue = echoed.value > 0 ? echoed.value : (state.maximumFps ?? 0);
        // Recomputed every render, which is what makes it track a value still being dragged.
        const pairedHz = state.refreshForCap.get(cappedValue);
        // The row's second mode. With the cap off the slider IS the refresh rate — the whole reason
        // SteamOS merged the two rows is that they are one decision: the frame cap and the rate it
        // is presented at are the same frametime question, and vsync is what makes the pacing hold.
        // Switching the cap off does not leave a dead control behind, it hands the same slider over
        // to the rate.
        const refreshMode = !capped && state.refreshRates.length > 0;
        const sliderValue = refreshMode ? (refreshEchoed.value ?? 0) : cappedValue;
        summarize(
          "frameLimit",
          capped
            ? `${cappedValue} fps cap`
            : refreshMode
              ? `${state.refreshRates[refreshEchoed.value ?? 0] ?? "?"} Hz`
              : "No frame limit",
        );
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
          showValue: !refreshMode && echoed.value !== null,
          showBookendLabels: !refreshMode,
          disabled: isBusy(state.progress),
          description:
            refusal ||
            accentDescription(controlRuntime, state.accent, state.fault || state.statusText),
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
      // A valid descriptor draws the slider. The reading is a display detail: none leaves the slider
      // at its minimum with no number, one outside the range is shown at the nearer end, and one off
      // the step is shown as is until the user moves it.
      if (
        ![min, max, step].every(Number.isInteger) ||
        min < 1 ||
        min >= max ||
        step < 1 ||
        step > max - min
      )
        return null;
      return {
        available: value.available === true,
        min,
        max,
        step,
        observed: clampReading(observed, min, max),
        progress: normalizeText(value.progress),
        statusText: normalizeText(value.statusText),
        accent: value.accent === true,
      };
    };
    const normalizePowerLimitState = (value) =>
      value && typeof value === "object"
        ? {
            sustained: normalizePowerLimitRange(value.sustained),
            boost: normalizePowerLimitRange(value.boost),
            unified: value.unified === true,
            canSelectMode: value.canSelectMode === true,
            modeAccent: value.modeAccent === true,
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
              description: accentDescription(
                controlRuntime,
                state.modeAccent,
                error || "Coordinate sustained and boost limits with one target.",
              ),
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
                void sendCommand(definition, definition.modeCommand, { unified })
                  .catch((reason) => setError(normalizeText(reason?.message ?? String(reason))))
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
            void sendCommand(definition, command, { watts })
              .catch((reason) => setError(normalizeText(reason?.message ?? String(reason))))
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
                // No reading sits at the minimum with no number, and sends nothing until moved.
                value: echo.value ?? range.min,
                valueSuffix: " W",
                showValue: echo.value !== null,
                showBookendLabels: true,
                disabled: busy || !range.available,
                description: accentDescription(
                  controlRuntime,
                  range.accent,
                  error ||
                    (state.unified
                      ? `Sustained ${state.sustained?.observed ?? "?"} W · Boost ${state.boost?.observed ?? "?"} W`
                      : range.statusText),
                ),
                onChange: echo.onChange,
                onChangeComplete: (next) => echo.onChangeComplete(next, commit),
              }),
            ),
          );
        }
        if (!rows.length) return note("powerLimit", "no usable power limit");
        drew("powerLimit", `rendered ${rows.length} row(s)`);
        summarize(
          "powerLimit",
          state.unified
            ? `${state.sustained?.observed ?? "?"} W`
            : `${state.sustained?.observed ?? "?"} W sustained · ${state.boost?.observed ?? "?"} W boost`,
        );
        return controlRuntime.react.createElement(controlRuntime.react.Fragment, null, ...rows);
      };
    const createDeviceControlsControl = (controlRuntime) =>
      // `sections` is the Quick Settings sections of the host's layout, or null without one.
      function SteamUiDeviceControls({ sections }) {
        const state = useSemanticState(
          controlRuntime,
          "deviceControls",
          normalizeDeviceControlsState,
        );
        const definition = definitions.deviceControls;
        // A refusal belongs to the row whose write it answered, and shows there until the next write.
        const [refusal, setRefusal] = controlRuntime.react.useState(null);
        const refusalFor = (command) =>
          refusal && refusal.command === command ? refusal.text : "";
        const send = (command, payload) => {
          setRefusal(null);
          void sendCommand(definition, command, payload).catch((reason) =>
            setRefusal({ command, text: refusalText(reason) }),
          );
        };
        const queueColorCommit = useTrailingCommit(controlRuntime, 350, ({ zone, color }) =>
          send(definition.colorCommand, { zone, color }),
        );
        const [selectedZone, setSelectedZone] = controlRuntime.react.useState("");
        const [editingColor, setEditingColor] = controlRuntime.react.useState(false);
        const folds = useSemanticState(controlRuntime, "panelFolds", normalizePanelFoldsState);
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
        // A range with no reading still draws, at its minimum with no number, and sends nothing until
        // the user moves it.
        if (state.chargeLimit?.available) {
          const range = state.chargeLimit;
          appendSlider("steam-ui-charge-limit", {
            label: "Battery charge limit",
            icon: controlRuntime.icon("percent"),
            iconLocation: "front",
            min: range.minimum,
            max: range.maximum,
            step: range.step,
            value: chargeEcho.value ?? range.minimum,
            valueSuffix: "%",
            showValue: chargeEcho.value !== null,
            showBookendLabels: true,
            disabled: isBusy(range.progress),
            description:
              refusalFor(definition.chargeCommand) ||
              accentDescription(controlRuntime, range.accent, range.statusText),
            onChange: chargeEcho.onChange,
            onChangeComplete: (next) =>
              chargeEcho.onChangeComplete(next, (percent) =>
                send(definition.chargeCommand, { percent }),
              ),
          });
        }
        const chargingRows = rows.splice(0);
        if (state.lightingBrightness?.available) {
          const range = state.lightingBrightness;
          appendSlider("steam-ui-lighting-brightness", {
            label: "Lighting brightness",
            icon: controlRuntime.icon("bulb"),
            iconLocation: "front",
            min: range.minimum,
            max: range.maximum,
            step: range.step,
            value: brightnessEcho.value ?? range.minimum,
            valueSuffix: "%",
            showValue: brightnessEcho.value !== null,
            showBookendLabels: true,
            disabled: isBusy(range.progress),
            description:
              refusalFor(definition.brightnessCommand) ||
              accentDescription(controlRuntime, range.accent, range.statusText),
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
                // Which zones the host marks, after its accent label, so it shows without opening the
                // editor, or why the last colour was refused. Plain text, not accented.
                description:
                  refusalFor(definition.colorCommand) ||
                  (zones.some((candidate) => candidate.accent)
                    ? [
                        normalizeText(acceptedStates.get("quickAccessLayout")?.accentLabel),
                        zones
                          .filter((candidate) => candidate.accent)
                          .map((candidate) => candidate.label)
                          .join(", "),
                      ]
                        .filter(Boolean)
                        .join(" · ")
                    : undefined),
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
                description: accentDescription(controlRuntime, zone.accent, zone.statusText),
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
        drew("deviceControls", `rendered ${rows.length + chargingRows.length} row(s)`);
        // Two groups, two detail lines, each under the kind its section names: the charge limit, and
        // the lighting's brightness and zone.
        summarize("charging", chargeValue === null ? "" : `Limit ${chargeValue}%`);
        summarize(
          "lighting",
          [brightnessValue === null ? "" : `${brightnessValue}%`, zone ? zone.label : ""]
            .filter(Boolean)
            .join(" · "),
        );
        // Each group draws in the layout's section that names its kind, or untitled without a layout.
        // A layout that names neither leaves the group out.
        const group = (kind, groupRows) => {
          const section = Array.isArray(sections)
            ? sections.find((candidate) => candidate.kinds.includes(kind))
            : untitledSection(kind, [kind]);
          return groupRows.length && section
            ? hostSection(controlRuntime, section, true, groupRows, folds)
            : null;
        };
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          group("charging", chargingRows),
          group("lighting", rows),
        );
      };
    // Steam's own FPS counter rows, which a host whose limiter overlay replaces them hides. Identified by
    // localising the same tokens Steam did rather than by CSS class or visible text: the classes
    // are hashed per client build and the text changes with the user's language, while the token is
    // the one thing that is neither.
    const NativeFpsTokens = [
      "#QuickAccess_Tab_Perf_FPS_Corner",
      "#QuickAccess_Tab_Perf_FPS_Contrast",
    ];
    let filteredNative = null;
    // Localized once the runtime answers for at least one token. An empty answer is asked again on
    // the next render, because the localization table can arrive after the panel first draws.
    let nativeFpsLabels = null;
    // Rows hidden by each render that filters: the root and every component wrapper on the way down
    // count their own, because a wrapper's output exists only once React renders it, after the root
    // has returned. The diagnostic is their sum, written after each of those renders.
    const hiddenByComponent = new Map();
    const publishHidden = (component, hidden) => {
      hiddenByComponent.set(component, hidden);
      if (!appendDiagnostics.perf) return;
      let total = 0;
      for (const count of hiddenByComponent.values()) total += count;
      appendDiagnostics.perf.nativeRowsHidden = total;
    };
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
    const hideNativeRows = (controlRuntime, element, labels, depth, counted) => {
      if (depth > 12 || !controlRuntime.react.isValidElement(element)) return element;
      // Compared as text on both sides: a label is sometimes a localiser element and sometimes a
      // plain string, and matching the raw prop found nothing at all.
      const label = textOf(element.props && element.props.label);
      if (label !== null && labels.includes(label)) {
        counted.hidden++;
        return null;
      }
      // A plain function component renders through a wrapper so its output is filtered too; any
      // other element is filtered through its children, dropping the rows that matched.
      return (
        descendInto(
          controlRuntime.react,
          element,
          descendCache,
          (type) =>
            function SteamUiDescend(props) {
              const own = { hidden: 0 };
              const output = hideNativeRows(controlRuntime, type(props), labels, 0, own);
              publishHidden(type, own.hidden);
              return output;
            },
        ) ??
        mapChildren(controlRuntime.react, element, (kid) =>
          hideNativeRows(controlRuntime, kid, labels, depth + 1, counted),
        )
      );
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
      if (nativeFpsLabels?.runtime !== controlRuntime) {
        const localized = NativeFpsTokens.map((token) =>
          textOf(controlRuntime.localize(token)),
        ).filter((text) => typeof text === "string" && text.length > 0 && text[0] !== "#");
        if (!localized.length) return tree;
        nativeFpsLabels = { runtime: controlRuntime, labels: localized };
      }
      const labels = nativeFpsLabels.labels;
      if (!filteredNative || filteredNative.inner !== inner) {
        hiddenByComponent.clear();
        filteredNative = {
          inner,
          component: function SteamUiFilteredPerformance(props) {
            const own = { hidden: 0 };
            const filtered = hideNativeRows(controlRuntime, inner(props), labels, 0, own);
            publishHidden(inner, own.hidden);
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
    // The host's Quick Access layout (steam-ui.quick-access-layout): each tab's sections, their
    // headings, glyphs and folds, and the row kinds drawn under each. The toolkit holds none of its
    // own. A section without an id or a kind list is skipped rather than costing the whole layout.
    const normalizeQuickAccessSections = (value) => {
      const sections = [];
      if (!Array.isArray(value)) return Object.freeze(sections);
      const ids = new Set();
      for (const item of value) {
        const id = normalizeText(item?.id);
        if (!id || ids.has(id) || !Array.isArray(item.kinds)) continue;
        ids.add(id);
        sections.push(
          Object.freeze({
            id,
            title: normalizeText(item.title),
            icon: normalizeText(item.icon),
            folds: item.folds === true,
            kinds: Object.freeze(item.kinds.filter((kind) => typeof kind === "string" && kind)),
          }),
        );
      }
      return Object.freeze(sections);
    };
    const normalizeQuickAccessLayout = (value) =>
      value && typeof value === "object"
        ? Object.freeze({
            performance: normalizeQuickAccessSections(value.performance),
            performanceEnd: normalizeQuickAccessSections(value.performanceEnd),
            quickSettings: normalizeQuickAccessSections(value.quickSettings),
            quickSettingsEnd: normalizeQuickAccessSections(value.quickSettingsEnd),
            hideValveFpsRows: value.hideValveFpsRows === true,
            accentLabel: normalizeText(value.accentLabel),
          })
        : null;
    // Without a layout a tab's rows draw in one untitled group that does not fold.
    const untitledSection = (id, kinds) =>
      Object.freeze({ id, title: "", icon: "", folds: false, kinds: Object.freeze(kinds) });
    // 18px is the size Valve's own header rule gives a section icon, against a 16px header. The glyph
    // is named by the host; a name the kit does not draw leaves the heading without one.
    const sectionIcon = (controlRuntime, name) => controlRuntime.icon(name, 18);
    // What a folded section's heading reports: the summaries of its rows, in the row table's order,
    // then those a row leaves under a kind of the section's own, which is how the device rows report
    // their charging and lighting sections.
    const sectionSummary = (section) =>
      [
        ...new Set([
          ...controlRows.map((row) => row[0]).filter((kind) => section.kinds.includes(kind)),
          ...section.kinds,
        ]),
      ]
        .map((kind) => summaries[kind])
        .filter(Boolean)
        .join(" · ");
    // A section is a kit group: a heading with the section's glyph, its title and, folded, what its
    // rows report, over the rows. A section with no title has no heading, and one the host does not
    // fold stays open. A section whose rows all draw nothing stays mounted, so those rows keep their
    // subscriptions and can bring it back when state arrives; it is only taken out of layout. The
    // fold is kept under the section's id. `folds` is the host's published open list, or null.
    const hostSection = (controlRuntime, section, shown, rows, folds) =>
      !section.title
        ? renderSteamUiGroup(controlRuntime, { key: section.id, hidden: !shown }, ...rows)
        : renderSteamUiGroup(
            controlRuntime,
            {
              key: section.id,
              title: section.title,
              icon: sectionIcon(controlRuntime, section.icon),
              detail: sectionSummary(section) || undefined,
              hidden: !shown,
              ...(section.folds
                ? {
                    collapsed: isFolded(folds, section.id),
                    onToggle: () => setFolded(section.id, !isFolded(folds, section.id)),
                  }
                : {}),
            },
            ...rows,
          );
    // Built once the controls resolve, rather than on every render of the panel.
    let controlRows = [];
    // Shape of what Steam's performance root returned, so the rows it renders can be identified
    // without guessing. Needed to suppress Steam's own FPS counter rows in favour of the host's own
    // overlay: their DOM classes are hashed per client build and unusable as selectors.
    const describe = (controlRuntime, element, depth) => {
      if (!controlRuntime.react.isValidElement(element)) return typeof element;
      const t = element.type;
      const name = typeof t === "string" ? t : t?.displayName || t?.name || "anonymous";
      const kids = controlRuntime.react.Children.toArray(element.props?.children);
      return depth >= 2 || !kids.length
        ? name
        : { [name]: kids.map((k) => describe(controlRuntime, k, depth + 1)) };
    };
    // `layout` is the host's published layout, or null.
    const appendControls = (
      controlRuntime,
      tree,
      placement = "perf",
      folds = null,
      layout = null,
    ) => {
      // Rendered React elements from Steam's own untyped runtime, with the kind each draws.
      const rows = [];
      // Kinds with at least one row that drew. Valve's components report nothing, so theirs count.
      const drawn = new Set();
      for (const [kind, key, component, rowPlacement] of controlRows) {
        if (rowPlacement !== placement || !registrations.has(kind) || !component) continue;
        rows.push([
          kind,
          controlRuntime.react.createElement(
            controlRuntime.row,
            { key },
            controlRuntime.react.createElement(component),
          ),
        ]);
        if (kind.startsWith("valve") || drawnKinds.has(kind)) drawn.add(kind);
      }
      const leading = layout
        ? placement === "perf"
          ? layout.performance
          : layout.quickSettings
        : [
            untitledSection(
              "steam-ui-" + placement,
              rows.map(([kind]) => kind),
            ),
          ];
      const trailing = layout
        ? placement === "perf"
          ? layout.performanceEnd
          : layout.quickSettingsEnd
        : [];
      // A section draws the rows of its kinds in the row table's order. One with none of them is left
      // out, and a kind no section names is not drawn while a layout is published.
      const sections = (list) =>
        list.flatMap((section) => {
          const own = rows
            .filter(([kind]) => section.kinds.includes(kind))
            .map(([, element]) => element);
          return own.length
            ? [
                hostSection(
                  controlRuntime,
                  section,
                  section.kinds.some((kind) => drawn.has(kind)),
                  own,
                  folds,
                ),
              ]
            : [];
        });
      const deviceControls =
        placement === "quickSettings" &&
        registrations.has("deviceControls") &&
        deviceControlsControl
          ? controlRuntime.react.createElement(deviceControlsControl, {
              key: "steam-ui-device-controls",
              sections: layout ? [...layout.quickSettings, ...layout.quickSettingsEnd] : null,
            })
          : null;
      const settingsSections =
        placement === "perf" && registrations.has("settingsSections") && settingsSectionsControl
          ? controlRuntime.react.createElement(settingsSectionsControl, {
              key: "steam-ui-settings-sections",
            })
          : null;
      const pluginRows = pluginFrontendElements(placement, controlRuntime.react);
      if (!rows.length && !deviceControls && !settingsSections && !pluginRows.length) {
        appendDiagnostics[placement] = { controls: 0, inserted: false, ownSection: false };
        return tree;
      }
      const controls = rows.length + (deviceControls ? 1 : 0) + (settingsSections ? 1 : 0);
      // Quick Settings keeps Valve's common controls intact. The native-row filtering
      // below is about Steam's FPS counter rows on the PERFORMANCE panel; running it against a
      // different tab's tree would be hiding rows this code has never even looked at.
      if (placement === "quickSettings") {
        appendDiagnostics[placement] = {
          controls,
          inserted: true,
          ownSection: true,
        };
        // The host's sections lead the tab rather than trailing it: brightness and the shortcut
        // toggles read below them naturally, and a dropdown at the bottom of a scrolling tab is
        // the control a user finds last. Valve's own sections between are drawn as kit blocks too,
        // so the tab reads as one column of groups. The closing sections and the device controls'
        // own follow Valve's.
        return controlRuntime.react.createElement(
          controlRuntime.react.Fragment,
          null,
          steamUiKitStyle(controlRuntime.react),
          ...sections(leading),
          controlRuntime.react.createElement(
            "div",
            { key: "steam-ui-valve-sections", className: "steam-ui-kit-valve" },
            tree,
          ),
          ...sections(trailing),
          deviceControls,
          ...pluginRows,
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
      // children — measured on the reference device as depthReached 0, sectionSeen false, with the
      // section component itself resolved and all five rows built. It failed silently, which is
      // why an empty Quick Access panel survived so long: every other signal said success.
      //
      // Appending a section instead depends on nothing about Steam's internal tree shape, so it
      // cannot be broken by a Steam UI change or by the fields Windows hides.
      const own = controlRuntime.react.createElement(
        controlRuntime.react.Fragment,
        null,
        ...sections(leading),
      );
      // Steam's FPS rows are hidden only when the host's layout asks, for a host whose own overlay
      // replaces them; otherwise Valve's tree is untouched. What remains of it is the battery line,
      // which the kit draws small under its class.
      const native = controlRuntime.react.createElement(
        "div",
        { key: "steam-ui-native-performance", className: "steam-ui-kit-battery" },
        layout?.hideValveFpsRows === true ? withNativeRowsHidden(controlRuntime, tree) : tree,
      );
      // Described when status asks rather than on every render of the panel.
      let description;
      appendDiagnostics.perf = {
        controls,
        inserted: true,
        ownSection: true,
        get tree() {
          return (description ??= JSON.stringify(describe(controlRuntime, tree, 0)));
        },
        nativeFiltered: native.props.children !== tree,
      };
      return controlRuntime.react.createElement(
        controlRuntime.react.Fragment,
        null,
        steamUiKitStyle(controlRuntime.react),
        native,
        own,
        settingsSections,
        ...pluginRows,
        // The closing sections follow every other one, including dynamically published host
        // settings sections.
        ...sections(trailing),
      );
    };
    // Resolve every dependency before changing React or registering a component.
    const resolveControls = () => {
      runtime = getWebpackRuntime("native-components");
      const performanceFactory = runtime.findUnique([
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
      cpuBoostControl = createCpuBoostControl(controlRuntime);
      powerPresetControl = createPowerPresetControl(controlRuntime);
      resolutionControl = createResolutionControl(controlRuntime);
      audioFormatControl = createAudioFormatControl(controlRuntime);
      let settingsRuntime = null;
      try {
        settingsRuntime = resolveSteamSettingsComponents(runtime);
      } catch {
        // Host settings need more native components than the independent QAM controls do.
      }
      settingsSectionsControl = createSettingsSectionsControl(controlRuntime, settingsRuntime);
      vrrControl = createVrrControl(controlRuntime);
      deviceControlsControl = createDeviceControlsControl(controlRuntime);
      powerLimitControl = createPowerLimitControl(controlRuntime);
      // Selected by the localization token it draws, never by a minified export name: the names are
      // right for today's build and are not guaranteed for the next. Live-probed 2026-08-30 that
      // this token matches exactly one export of the components module.
      const perfComponents = runtime.findUnique([
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
      // Registration, component and placement share one table. The host's layout decides the
      // sections; this table determines the order of controls within each section.
      controlRows = [
        ["valveProfileHeader", "steam-ui-valve-profile-header", valveProfileHeaderControl, "perf"],
        ["valveProfileHeader", "steam-ui-valve-profile-toggle", valveProfileToggleControl, "perf"],
        ["valveOverlayLevel", "steam-ui-valve-overlay-level", valveOverlayLevelControl, "perf"],
        ["frameLimit", "steam-ui-frame-limit", frameLimitControl, "perf"],
        ["powerProfile", "steam-ui-power-profile", powerProfileControl, "perf"],
        ["hybridCores", "steam-ui-hybrid-cores", hybridCoreControl, "perf"],
        ["cpuBoost", "steam-ui-cpu-boost", cpuBoostControl, "perf"],
        ["powerPreset", "steam-ui-power-preset", powerPresetControl, "perf"],
        ["vrr", "steam-ui-vrr", vrrControl, "perf"],
        ["powerLimit", "steam-ui-power-limits", powerLimitControl, "perf"],
        ["autoTdp", "steam-ui-auto-tdp", autoTdpControl, "perf"],
        ["resolution", "steam-ui-resolution", resolutionControl, "quickSettings"],
        ["audioFormat", "steam-ui-audio-format", audioFormatControl, "quickSettings"],
        [
          "valveRefreshRate",
          "steam-ui-valve-refresh-rate",
          valveRefreshRateControl,
          "quickSettings",
        ],
        ["controllerTarget", "steam-ui-controller-target", controllerControl, "perf"],
        ["valveReset", "steam-ui-valve-reset", valveResetControl, "perf"],
      ];
      return true;
    };
    const ensurePatched = () => {
      if (controlRuntime && performanceRoot && memoIntercepted(controlRuntime.react, MemoName))
        return true;
      try {
        if (!resolveControls()) return false;
      } catch (error) {
        lastPatchError = "native component runtime resolution failed: " + String(error);
        return false;
      }
      function SteamUiPerformanceRoot(props) {
        const [, setRevision] = controlRuntime.react.useState(0);
        controlRuntime.react.useEffect(
          () => subscribeHost(() => setRevision((value) => value + 1)),
          [],
        );
        const folds = useSemanticState(controlRuntime, "panelFolds", normalizePanelFoldsState);
        const layout = useSemanticState(
          controlRuntime,
          "quickAccessLayout",
          normalizeQuickAccessLayout,
        );
        return appendControls(controlRuntime, performanceRoot(props), "perf", folds, layout);
      }
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
          match: (type) =>
            type !== performanceRoot &&
            sourceMatches(type, [
              "#QuickAccess_Tab_Settings_Section_Other_Title",
              "#QuickAccess_ReorderControllers_Button",
            ]),
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
                const folds = useSemanticState(
                  controlRuntime,
                  "panelFolds",
                  normalizePanelFoldsState,
                );
                const layout = useSemanticState(
                  controlRuntime,
                  "quickAccessLayout",
                  normalizeQuickAccessLayout,
                );
                return appendControls(
                  controlRuntime,
                  original(props),
                  "quickSettings",
                  folds,
                  layout,
                );
              };
              quickSettingsWrapCache.set(original, wrapped);
            }
            return wrapped;
          },
          fallbackKey: "steam-ui-quick-settings-root",
        },
      ];
      // The tab array passes through the one useMemo claim every surface shares (ownership.ts).
      const transformTabs = (value) => {
        // Every useMemo result in the client passes through here. A tab list holds tab objects, so an
        // empty array, or one that starts with a string or number, is answered before any filtering.
        if (!Array.isArray(value) || !value.length) return value;
        if (typeof value[0] === "string" || typeof value[0] === "number") return value;
        // Many memoized arrays hold objects too; only one with a tab's panel element is worth copying.
        let tabs = false;
        for (let index = 0; index < value.length && !tabs; index++) {
          const item = value[index];
          tabs =
            !!item && typeof item === "object" && controlRuntime.react.isValidElement(item.panel);
        }
        if (!tabs) return value;
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
      const intercepted = interceptMemo(controlRuntime.react, MemoName, transformTabs);
      if (!intercepted.ok) {
        lastPatchError = intercepted.error || "React useMemo wrapper could not be installed";
        return false;
      }
      lastPatchError = "";
      return true;
    };
    const install = (kind) => {
      unsubscribePlugins ??= subscribePluginFrontends(() => notify());
      if (disposedHost) return { ok: false, error: "component host disposed" };
      if (!Object.hasOwn(definitions, kind))
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
      if (!registrations.size && controlRuntime) {
        releaseMemo(controlRuntime.react, MemoName);
      }
      return { ok: true, kind, registered: false };
    };
    const status = (kind) => ({
      ok: Object.hasOwn(definitions, kind),
      kind,
      registered: registrations.has(kind),
      hostVersion: 1,
      performanceRootWrapped: !!controlRuntime && memoIntercepted(controlRuntime.react, MemoName),
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
      unsubscribePlugins?.();
      disposedHost = true;
      registrations.clear();
      notify();
      listeners.clear();
      if (controlRuntime) releaseMemo(controlRuntime.react, MemoName);
    };
    return { install, remove, status, dispose: disposeHostResources };
  }
  registerGate("nativeComponents", createNativeComponentHost());
  // @fragment consumer/animations.ts
  // Boot-animation frontend; host services own downloads, selection and override files.
  // Windows does not drive Steam suspend movies, so this page offers boot movies only.
  const AnimationsPatchId = "wsgm.animations";
  let animationsUi = null;
  const animationsAct = wsgmPageAct(AnimationsPatchId);
  const animationsTabs = [
    { id: "browse", title: "Browse" },
    { id: "library", title: "Library" },
    { id: "settings", title: "Settings" },
  ];
  // A card in the grid: the still, likes and downloads, a badge once the library holds it or it
  // plays at boot, and the author and date under the name.
  const animationsCard = (ui, item, selected, open) => {
    const react = ui.react;
    return renderSteamUiCard(ui, {
      key: item.id,
      image: item.thumbnailUrl,
      stats: item.custom
        ? []
        : [
            { glyph: renderSteamUiGlyph(react, "heart"), text: String(item.likes ?? 0) },
            { glyph: renderSteamUiGlyph(react, "download"), text: String(item.downloads ?? 0) },
          ],
      badge:
        item.id === selected
          ? { text: "Plays at boot", warn: true }
          : item.downloaded
            ? { text: item.custom ? "Your file" : "In library" }
            : null,
      title: item.name,
      meta: [
        item.custom ? "Your file" : item.updated || "",
        item.author ? `By ${item.author}` : "",
      ],
      onActivate: () => open(item.id),
    });
  };
  function AnimationsDetail({ state }) {
    const ui = animationsUi;
    const react = ui.react;
    const h = react.createElement;
    const item = state.detail;
    const playing = state.selected === item.id;
    const back = () => void animationsAct("closeDetail");
    return renderSteamUiDetail(ui, {
      title: item.name,
      media: renderSteamUiVideo(react, {
        src: item.previewUrl,
        poster: item.thumbnailUrl,
        empty: item.custom ? "Your file has no preview here" : "No preview",
      }),
      main: [
        h(
          "div",
          { className: "steam-ui-kit-muted" },
          [item.author ? `By ${item.author}` : "", item.updated].filter(Boolean).join(" · "),
        ),
        h("h3", null, "Description"),
        h(
          "p",
          { className: item.description ? "" : "steam-ui-kit-muted" },
          item.description || "No description provided.",
        ),
      ],
      aside: [
        item.downloaded
          ? renderSteamUiBox(
              react,
              playing ? "Plays at boot" : "In the library",
              h(
                ui.dialogButtonPrimary,
                { disabled: playing, onClick: () => void animationsAct("select", { id: item.id }) },
                playing ? "Big Picture starts with this" : "Start Big Picture with this",
              ),
              h(
                "div",
                { className: "steam-ui-kit-muted" },
                "Steam reads the movie when it starts, so a change shows at the next Steam start.",
              ),
            )
          : renderSteamUiBox(
              react,
              `Download ${item.name}`,
              h(
                "div",
                { className: "steam-ui-kit-muted" },
                `${item.downloads ?? 0} downloads · ${item.likes ?? 0} likes`,
              ),
              h(
                ui.dialogButtonPrimary,
                {
                  disabled: !!state.busy,
                  onClick: () => void animationsAct("download", { id: item.id }),
                },
                state.busy ? "Working…" : "Download",
              ),
              h(
                "div",
                { className: "steam-ui-kit-muted" },
                "Into WSGM's library; choose it there afterwards.",
              ),
            ),
        item.downloaded
          ? h(
              ui.dialogButton,
              {
                onClick: () =>
                  showSteamUiConfirm(ui, {
                    title: "Remove movie",
                    text: `Remove ${item.name} from the library? If it plays at boot, Steam's own movie plays again.`,
                    confirmLabel: "Remove",
                    onConfirm: () => void animationsAct("delete", { id: item.id }).then(back),
                  }),
              },
              "Remove from library",
            )
          : null,
      ],
      onBack: back,
    });
  }
  function AnimationsBrowse({ state }) {
    const ui = animationsUi;
    const react = ui.react;
    const h = react.createElement;
    const browse = state.browse ?? {};
    const sorts = browse.sorts ?? [];
    const [search, setSearch] = react.useState(browse.search ?? "");
    react.useEffect(() => setSearch(browse.search ?? ""), [browse.search]);
    // The first look at the repository is the page's own: nothing is fetched until someone opens the tab.
    react.useEffect(() => {
      if (!browse.loading && !browse.error && !browse.total) {
        void animationsAct("browse", { sort: browse.sort ?? "", search: browse.search ?? "" });
      }
    }, []);
    const ask = (changes) =>
      void animationsAct("browse", { sort: browse.sort ?? "", search, ...changes });
    if (state.detail) return h(AnimationsDetail, { state });
    const items = browse.items ?? [];
    const open = (id) => void animationsAct("open", { id });
    return renderSteamUiPane(
      ui,
      {},
      renderSteamUiToolbar(
        ui,
        renderSteamUiTool(
          ui,
          "Sort",
          renderSteamDropdown(ui, {
            label: "Sort",
            rgOptions: sorts.map((sort) => ({ data: sort.id, label: sort.label })),
            selectedOption: browse.sort,
            onChange: (option) => ask({ sort: option?.data }),
          }),
        ),
        renderSteamUiTool(
          ui,
          null,
          h(ui.textField, {
            label: "Search",
            value: search,
            onChange: (event) => setSearch(event?.target?.value ?? ""),
            onBlur: () => {
              if (search !== (browse.search ?? "")) ask({ search });
            },
          }),
          true,
        ),
        h(
          ui.dialogButton,
          { disabled: !!browse.loading, onClick: () => void animationsAct("refresh") },
          "Refresh",
        ),
      ),
      browse.error ? renderSteamUiEmpty(react, browse.error, true) : null,
      renderSteamUiGrid(
        ui,
        items.map((item) => animationsCard(ui, item, state.selected, open)),
      ),
      // A page at a time: the repository lists thousands, and a card for each stalls Steam.
      items.length < (browse.matched ?? 0)
        ? renderSteamUiMore(ui, {
            label: `Load More (${items.length} of ${browse.matched})`,
            onClick: () => void animationsAct("more"),
          })
        : null,
      browse.loading
        ? renderSteamUiEmpty(react, "Asking the repository…")
        : items.length === 0 && !browse.error
          ? renderSteamUiEmpty(
              react,
              browse.total ? "Nothing matched." : "The repository has not answered yet.",
            )
          : null,
    );
  }
  function AnimationsLibrary({ state }) {
    const ui = animationsUi;
    const react = ui.react;
    const h = react.createElement;
    if (state.detail) return h(AnimationsDetail, { state });
    const library = state.library ?? [];
    const open = (id) => void animationsAct("open", { id });
    const addFile = () =>
      void showSteamFilePicker(ui, {
        title: "Choose a WebM movie",
        mode: "file",
        extensions: [".webm"],
      }).then((chosen) => chosen && animationsAct("addFile", { path: chosen }));
    const choice = renderSteamSettingRow(
      ui,
      {
        key: "boot",
        kind: "choice",
        label: "Boot movie",
        description: "What Big Picture starts with.",
        text: state.selected ?? "",
        choices: [
          { value: "", label: state.stockName ?? "" },
          ...library.map((item) => ({ value: item.id, label: item.name })),
        ],
      },
      undefined,
      (_row, value, commit = true) => {
        if (commit) void animationsAct("select", { id: String(value) });
      },
      () => {},
    );
    return renderSteamUiPane(
      ui,
      {},
      renderSteamUiToolbar(
        ui,
        h(
          ui.dialogButton,
          {
            disabled: !!state.busy || library.length === 0,
            onClick: () => void animationsAct("shuffle"),
          },
          "Shuffle",
        ),
        h(ui.dialogButton, { disabled: !!state.busy, onClick: addFile }, "Add a video file…"),
      ),
      h(ui.settingsSection, { label: "Boot" }, choice),
      state.settings?.restartNeeded
        ? renderSteamUiEmpty(
            react,
            "The boot movie changed since Steam started. Restart Steam to see it.",
          )
        : null,
      library.length === 0
        ? renderSteamUiEmpty(
            react,
            "Nothing in the library yet. Download a movie under Browse, or add a WebM file.",
          )
        : renderSteamUiGrid(
            ui,
            library.map((item) => animationsCard(ui, item, state.selected, open)),
          ),
    );
  }
  function AnimationsSettings({ state }) {
    const ui = animationsUi;
    const h = ui.react.createElement;
    const settings = state.settings ?? {};
    const rows = [
      {
        key: "shuffleOnStart",
        kind: "boolean",
        label: "Shuffle on start",
        description:
          "Picks the boot movie anew from the library each time WSGM starts, before Steam does.",
        checked: !!settings.shuffleOnStart,
      },
      {
        key: "bootVolume",
        kind: "range",
        label: "Volume",
        description:
          "How loud the boot movie plays, against its file. Steam plays a movie other than its own twice at once, so 100% takes that back. Works on movies whose sound is Opus.",
        number: settings.bootVolume ?? 100,
        minimum: 0,
        maximum: 100,
        step: 5,
        suffix: "%",
      },
      {
        key: "note",
        kind: "note",
        label: "How it works",
        text: "The chosen movie is copied to the file Steam asks for under its uioverrides folder; Steam reads it when it starts. While one of these movies is chosen, Steam's own Startup Movie choice is set aside, and it comes back when you choose Steam's own here.",
      },
      { key: "library", kind: "note", label: "Library folder", text: settings.libraryPath ?? "" },
      {
        key: "overrides",
        kind: "note",
        label: "Steam's override folder",
        text: settings.overridesPath ?? "Steam is not installed",
      },
    ];
    return renderSteamUiPane(
      ui,
      {},
      h(
        ui.settingsSection,
        { label: "Boot animation" },
        ...rows.map((row) =>
          renderSteamSettingRow(
            ui,
            row,
            undefined,
            (changed, value, commit = true) => {
              if (commit && changed.key === "shuffleOnStart")
                void animationsAct("setShuffleOnStart", { value: !!value });
              if (commit && changed.key === "bootVolume")
                void animationsAct("setBootVolume", { value: Math.round(Number(value)) });
            },
            () => {},
          ),
        ),
      ),
    );
  }
  /**
   * Renders animation browsing, the installed library and settings from host state.
   * @param context Registered page accessors for Steam components, latest state and publication refusal.
   * @returns The page React tree, including loading or refusal state when data is unavailable.
   */
  function AnimationsPage({ context }) {
    const react = context.react();
    const h = react.createElement;
    const ui = context.ui();
    animationsUi = ui;
    const state = context.state();
    if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading boot movies…");
    return renderSteamUiTabbedPage(ui, {
      id: "wsgm-animations",
      label: "Boot animation",
      style: animationsStyles,
      tabs: animationsTabs,
      ...wsgmPageFrame(animationsAct, state),
      content: (id) => {
        switch (id) {
          case "library":
            return h(AnimationsLibrary, { state });
          case "settings":
            return h(AnimationsSettings, { state });
          default:
            return h(AnimationsBrowse, { state });
        }
      },
    });
  }
  // The page's own layout: where the kit's elements go, not how they look.
  const animationsStyles = `
#wsgm-animations .steam-ui-kit-tool:not(.grow) { width: 200px; }
`;
  const animationsPage = registerSteamPage({
    template: "animations",
    gate: "animations",
    patchId: AnimationsPatchId,
    components: resolveSteamSettingsComponents,
    required: SteamUiTabbedPageRequired,
    status: () => ({ tab: animationsPage.state()?.activeTab ?? "" }),
    Page: AnimationsPage,
  });
  // @fragment consumer/artwork-browser.ts
  // Artwork frontend; host services own searches and file writes, and the toolkit owns native UI primitives.
  const ArtworkBrowserPatchId = "wsgm.artwork-browser";
  // The resolved components and the latest state, for the modals: a modal is drawn outside the page's
  // tree, so it reads them here and hears about new state through the listeners the page notifies.
  let artworkUi = null;
  let artworkDesired = null;
  const artworkListeners = new Set();
  const artworkFilterOptions = (tab) => ({
    styles:
      tab === "logo"
        ? ["official", "white", "black", "custom"]
        : tab === "icon"
          ? ["official", "custom"]
          : tab === "hero"
            ? ["alternate", "blurred", "material"]
            : ["alternate", "white_logo", "no_logo", "blurred", "material"],
    dimensions:
      tab === "grid"
        ? ["600x900", "342x482", "660x930", "512x512", "1024x1024"]
        : tab === "wide"
          ? ["460x215", "920x430", "512x512", "1024x1024"]
          : tab === "hero"
            ? ["1920x620", "3840x1240", "1600x650"]
            : tab === "icon"
              ? ["1024", "512", "310", "256", "192", "128", "96", "64", "48", "32", "16"]
              : [],
    mimes:
      tab === "logo"
        ? ["image/png", "image/webp"]
        : tab === "icon"
          ? ["image/png", "image/vnd.microsoft.icon"]
          : ["image/png", "image/jpeg", "image/webp"],
  });
  const readableFilter = (value) =>
    value.replace("image/", "").replaceAll("_", " ").replace("x", "×");
  /**
   * Sends one artwork action to the shared host service.
   * @param command Allowlisted artwork command.
   * @param payload JSON action arguments.
   * @returns The backend result promise; callers own refusal handling.
   */
  const sendArtworkCommand = (command, payload = {}) =>
    request(ArtworkBrowserPatchId, command, payload);
  /**
   * Opens the controller-accessible file picker and sends the chosen path to the host.
   * @param tab Artwork slot; icon additionally permits ICO files.
   * @param failed Receives an applyLocal refusal; cancellation sends no command.
   */
  const chooseLocalArtwork = (tab, failed) => {
    void showSteamFilePicker(artworkUi, {
      title: "Choose an image",
      mode: "file",
      extensions:
        tab === "icon"
          ? [".png", ".jpg", ".jpeg", ".webp", ".ico"]
          : [".png", ".jpg", ".jpeg", ".webp"],
    }).then((path) => {
      if (!path) return;
      void sendArtworkCommand("applyLocal", { tab, path }).catch((error) =>
        failed(String(error?.message || error)),
      );
    });
  };
  /**
   * Opens a page-styled modal through Steam's native modal host.
   * @param className Page-specific layout classes.
   * @param render Body renderer receiving the close callback.
   * @returns True if a modal was opened, or false when required native components are unavailable.
   */
  const showArtworkModal = (className, render) =>
    showSteamModal(artworkUi, {
      title: "SteamGridDB",
      className: `sgdb-modal ${className}`,
      render,
    });
  function ArtworkDetailsModal({ asset, label, closeModal }) {
    const h = artworkUi.react.createElement;
    const PrimaryButton = artworkUi.dialogButtonPrimary;
    const Focusable = artworkUi.focusable;
    return h(
      "div",
      {},
      h(
        "div",
        { className: `sgdb-modal-details-wrapper${asset.width > asset.height ? " wide" : ""}` },
        h(
          Focusable,
          {
            className: "image-wrap modal-image",
            onActivate: () => {
              void sendArtworkCommand("apply", { id: asset.id });
              closeModal?.();
            },
            onOKActionDescription: `Apply ${label}`,
          },
          h("img", { src: asset.imageUrl, alt: "" }),
        ),
        h(
          "div",
          { className: "info" },
          h(
            PrimaryButton,
            {
              onClick: () => {
                void sendArtworkCommand("apply", { id: asset.id });
                closeModal?.();
              },
              onOKActionDescription: `Apply ${label}`,
            },
            `Apply ${label}`,
          ),
          h(
            "span",
            { className: "meta" },
            [asset.format, asset.style, asset.width > 0 ? `${asset.width}×${asset.height}` : null]
              .filter(Boolean)
              .join(" • "),
          ),
          asset.author
            ? h(Focusable, { className: "author" }, h("span", null, asset.author))
            : null,
          asset.notes ? h("p", { className: "notes" }, asset.notes) : null,
        ),
      ),
    );
  }
  function ArtworkOfficialModal({ assets, label, closeModal }) {
    const h = artworkUi.react.createElement;
    const PrimaryButton = artworkUi.dialogButtonPrimary;
    return h(
      "div",
      {},
      h("h2", null, `Official ${label}`),
      ...assets.map((asset) =>
        h(
          "div",
          { className: "official-steam-asset", key: asset.id },
          h("img", { src: asset.imageUrl, alt: asset.label || `Official ${label}` }),
          h(
            "div",
            { className: "official-steam-asset-action" },
            h("span", null, asset.label),
            h(
              PrimaryButton,
              {
                onClick: () => {
                  void sendArtworkCommand("applyOfficial", { id: asset.id });
                  closeModal?.();
                },
              },
              `Apply ${label}`,
            ),
          ),
        ),
      ),
    );
  }
  function ArtworkFilterModal({ tab, label, initialFilter, closeModal }) {
    const react = artworkUi.react;
    const h = react.createElement;
    const Button = artworkUi.dialogButton;
    const PrimaryButton = artworkUi.dialogButtonPrimary;
    const ToggleField = artworkUi.toggleField;
    const TextField = artworkUi.textField;
    const Focusable = artworkUi.focusable;
    const options = artworkFilterOptions(tab);
    const [filter, setFilter] = react.useState({ ...initialFilter });
    const [searchTerm, setSearchTerm] = react.useState(artworkDesired?.selectedGame || "");
    const [matches, setMatches] = react.useState(artworkDesired?.gameMatches || []);
    react.useEffect(() => {
      const listener = (state) =>
        setMatches(Array.isArray(state?.gameMatches) ? state.gameMatches : []);
      artworkListeners.add(listener);
      return () => artworkListeners.delete(listener);
    }, []);
    const multiSelect = (title, name, values) =>
      h(
        "div",
        { className: "sgdb-filter-field" },
        h("label", null, title),
        h(
          Focusable,
          { className: "sgdb-filter-options", "flow-children": "row" },
          ...values.map((value) => {
            const selected = (filter[name] || []).includes(value);
            return h(
              Button,
              {
                key: value,
                className: selected ? "sgdb-filter-selected" : "",
                onClick: () =>
                  setFilter({
                    ...filter,
                    [name]: selected
                      ? filter[name].filter((item) => item !== value)
                      : [...(filter[name] || []), value],
                  }),
              },
              `${selected ? "✓ " : ""}${readableFilter(value)}`,
            );
          }),
        ),
      );
    const toggle = (name, labelText) =>
      h(ToggleField, {
        key: name,
        label: labelText,
        checked: !!filter[name],
        onChange: (checked) => {
          const next = { ...filter, [name]: checked };
          if ((name === "static" || name === "animated") && !next.static && !next.animated) {
            next[name === "static" ? "animated" : "static"] = true;
          }
          setFilter(next);
        },
      });
    return h(
      "div",
      {},
      h("h2", null, `${label} Filter`),
      h(
        "div",
        { className: "sgdb-filter-game" },
        TextField
          ? h(TextField, {
              label: "Game",
              value: searchTerm,
              placeholder: artworkDesired?.appName || "Search for a game",
              onChange: (event) => setSearchTerm(event.currentTarget.value),
            })
          : null,
        h(
          Button,
          {
            onClick: () =>
              void sendArtworkCommand("searchGames", {
                term: searchTerm || artworkDesired?.appName || "Steam",
              }),
          },
          "Search",
        ),
        artworkDesired?.selectedGame
          ? h(
              Button,
              { onClick: () => void sendArtworkCommand("selectGame", { id: null }) },
              "Use Steam game",
            )
          : null,
      ),
      matches.length
        ? h(
            Focusable,
            { className: "sgdb-game-matches" },
            ...matches.map((match) =>
              h(
                Button,
                {
                  key: match.id,
                  onClick: () => void sendArtworkCommand("selectGame", { id: match.id }),
                },
                `${match.name} · ${match.provider}`,
              ),
            ),
          )
        : null,
      options.dimensions.length
        ? multiSelect("Dimensions", "dimensions", options.dimensions)
        : null,
      multiSelect("Styles", "styles", options.styles),
      multiSelect("File Types", "mimes", options.mimes),
      h("h3", null, "Types"),
      toggle("animated", "Animated"),
      toggle("static", "Static"),
      h("h3", null, "Tags"),
      toggle("adult", "Adult Content"),
      toggle("humor", "Humor"),
      toggle("epilepsy", "Epilepsy"),
      toggle("untagged", "Untagged"),
      h(
        Focusable,
        { className: "sgdb-modal-actions", "flow-children": "row" },
        h(Button, { onClick: closeModal }, "Cancel"),
        h(
          PrimaryButton,
          {
            onClick: () => {
              void sendArtworkCommand("setFilter", filter);
              closeModal?.();
            },
          },
          "Apply Filters",
        ),
      ),
    );
  }
  function ArtworkLogoModal({ closeModal }) {
    const react = artworkUi.react;
    const h = react.createElement;
    const Button = artworkUi.dialogButton;
    const PrimaryButton = artworkUi.dialogButtonPrimary;
    const SliderField = artworkUi.sliderField;
    const Focusable = artworkUi.focusable;
    const [position, setPosition] = react.useState({ anchor: "BottomLeft", width: 50, height: 50 });
    const anchors = [
      "TopLeft",
      "TopCenter",
      "TopRight",
      "CenterLeft",
      "CenterCenter",
      "CenterRight",
      "BottomLeft",
      "BottomCenter",
      "BottomRight",
    ];
    return h(
      "div",
      {},
      h("h2", null, "Adjust Logo Position"),
      h(
        "div",
        { className: "sgdb-logo-preview" },
        h("div", { className: `sgdb-logo-sample anchor-${position.anchor}` }, "GAME LOGO"),
      ),
      h(
        Focusable,
        { className: "sgdb-logo-anchors", "flow-children": "row" },
        ...anchors.map((anchor) =>
          h(
            Button,
            {
              key: anchor,
              className: position.anchor === anchor ? "sgdb-filter-selected" : "",
              onClick: () => setPosition({ ...position, anchor }),
            },
            anchor.replace(/([A-Z])/g, " $1").trim(),
          ),
        ),
      ),
      h(SliderField, {
        label: "Logo width",
        value: position.width,
        min: 5,
        max: 100,
        step: 1,
        showValue: true,
        valueSuffix: "%",
        onChange: (width) => setPosition({ ...position, width }),
      }),
      h(SliderField, {
        label: "Logo height",
        value: position.height,
        min: 5,
        max: 100,
        step: 1,
        showValue: true,
        valueSuffix: "%",
        onChange: (height) => setPosition({ ...position, height }),
      }),
      h(
        Focusable,
        { className: "sgdb-modal-actions", "flow-children": "row" },
        h(Button, { onClick: closeModal }, "Cancel"),
        h(
          PrimaryButton,
          {
            onClick: () => {
              void sendArtworkCommand("saveLogoPosition", position);
              closeModal?.();
            },
          },
          "Save",
        ),
      ),
    );
  }
  /**
   * Renders artwork selection and coordinates the modals subscribed to its host snapshot.
   * @param context Registered page accessors for Steam components, latest state and publication refusal.
   * @returns The page React tree, including loading or refusal state when data is unavailable.
   */
  function ArtworkBrowserPage({ context }) {
    const react = context.react();
    const h = react.createElement;
    const [actionError, setActionError] = react.useState("");
    const [cardSize, setCardSize] = react.useState(170);
    const state = context.state();
    artworkUi = context.ui();
    artworkDesired = state;
    // An open filter modal lists the matches the host publishes after its search.
    react.useEffect(() => {
      for (const listener of [...artworkListeners]) listener(state);
    }, [state]);
    // After the hooks, so a render before the first publication calls the same ones as one after.
    if (!state)
      return h("div", { className: "sgdb-loading" }, context.refusal() ?? "Loading artwork…");
    const Focusable = artworkUi.focusable;
    const Button = artworkUi.dialogButton;
    const SliderField = artworkUi.sliderField;
    const Tabs = artworkUi.tabs;
    const activate = (command, payload = {}) =>
      sendArtworkCommand(command, payload).catch((error) => {
        setActionError(String(error?.message || error));
        return undefined;
      });
    const tabs = Array.isArray(state.tabs) ? state.tabs : [];
    const assets = Array.isArray(state.assets) ? state.assets : [];
    const officialAssets = Array.isArray(state.officialAssets) ? state.officialAssets : [];
    const managed = Array.isArray(state.managedSlots) ? state.managedSlots : [];
    const active = tabs.find((tab) => tab.id === state.activeTab) || tabs[0];
    const openFilters = () =>
      showArtworkModal("sgdb-modal-filters", (close) =>
        h(ArtworkFilterModal, {
          tab: state.activeTab,
          label: active?.label || "Artwork",
          initialFilter: state.filter,
          closeModal: close,
        }),
      );
    const openDetails = (asset) =>
      showArtworkModal("sgdb-modal-details", (close) =>
        h(ArtworkDetailsModal, { asset, label: active?.label || "artwork", closeModal: close }),
      );
    const openLogo = () =>
      showArtworkModal("sgdb-modal-logo", (close) => h(ArtworkLogoModal, { closeModal: close }));
    const assetCard = (asset) =>
      h(
        "div",
        { className: "asset-box-wrap", key: asset.id },
        h(
          Focusable,
          {
            className: `image-wrap type-${state.activeTab}`,
            style: {
              paddingBottom: `${asset.width === asset.height ? 100 : (asset.height / asset.width) * 100}%`,
            },
            onActivate: () => activate("apply", { id: asset.id }),
            onSecondaryButton: openFilters,
            onMenuButton: () => openDetails(asset),
            onContextMenu: (event) => {
              event.preventDefault();
              openDetails(asset);
            },
            onOKActionDescription: `Apply ${active?.label || "artwork"}`,
            onSecondaryActionDescription: "Filter",
            onMenuActionDescription: "Details",
          },
          h(
            "div",
            { className: "thumb" },
            h("img", { src: asset.thumbnailUrl || asset.imageUrl, alt: "", loading: "lazy" }),
          ),
          asset.animated || asset.nsfw || asset.humor || asset.epilepsy
            ? h(
                "ul",
                { className: "chips" },
                asset.animated ? h("li", { className: "chip animated" }, "Animated") : null,
                asset.nsfw ? h("li", { className: "chip nsfw" }, "Adult") : null,
                asset.humor ? h("li", { className: "chip humor" }, "Humor") : null,
                asset.epilepsy ? h("li", { className: "chip epilepsy" }, "Epilepsy") : null,
              )
            : null,
        ),
        asset.author ? h("div", { className: "author" }, asset.author) : null,
      );
    const assetContent = h(
      "div",
      { className: "tabcontents-wrap" },
      state.loading
        ? h("div", { className: "spinnyboi" }, h("img", { src: "/images/steam_spinner.png" }))
        : null,
      h(
        Focusable,
        { className: "sgdb-asset-toolbar", "flow-children": "row" },
        h(
          Focusable,
          { className: "filter-buttons", "flow-children": "row" },
          h(Button, { noFocusRing: true, onClick: openFilters }, "Filter"),
          officialAssets.length
            ? h(
                Button,
                {
                  noFocusRing: true,
                  onClick: () =>
                    showArtworkModal("sgdb-modal-official-assets", (close) =>
                      h(ArtworkOfficialModal, {
                        assets: officialAssets,
                        label: active?.label || "Artwork",
                        closeModal: close,
                      }),
                    ),
                },
                `Official ${active?.label || "Artwork"}`,
              )
            : null,
          h(
            Button,
            {
              noFocusRing: true,
              onClick: () => chooseLocalArtwork(state.activeTab, setActionError),
            },
            "Browse Local",
          ),
          state.activeTab === "logo"
            ? h(Button, { noFocusRing: true, onClick: openLogo }, "Adjust Logo Position")
            : null,
        ),
        h(SliderField, {
          className: "size-slider",
          value: cardSize,
          min: 100,
          max: 260,
          step: 5,
          layout: "below",
          bottomSeparator: "none",
          onChange: setCardSize,
        }),
      ),
      state.selectedGame || state.filter?.styles?.length || state.filter?.dimensions?.length
        ? h(
            Button,
            { className: "sgdb-results-state", onClick: openFilters },
            state.selectedGame
              ? `Results for ${state.selectedGame}`
              : "Some assets may be hidden due to filter",
          )
        : null,
      state.error || state.notice || actionError
        ? h(
            "div",
            { className: `sgdb-status${state.error || actionError ? " error" : ""}` },
            state.error || actionError || state.notice,
          )
        : null,
      h(
        Focusable,
        {
          id: "images-container",
          style: { "--asset-size": `${cardSize}px` },
        },
        ...assets.map(assetCard),
      ),
      !state.loading && assets.length === 0 && !state.error
        ? h("div", { className: "sgdb-empty" }, "No Results Found.")
        : null,
      state.hasMore
        ? h(
            "div",
            { className: "sgdb-load-more" },
            h(Button, { onClick: () => activate("loadMore") }, "Load More"),
          )
        : null,
    );
    const manageContent = h(
      Focusable,
      { id: "local-images-container" },
      ...managed.map((slot) =>
        h(
          "div",
          { className: `asset-wrap asset-wrap-${slot.id}`, key: slot.id },
          h("div", { className: "asset-label" }, `Current ${slot.label}`),
          h(
            Focusable,
            { className: "manage-asset", focusWithinClassName: "is-focused" },
            h(
              "div",
              { className: "asset" },
              slot.imageUrl
                ? h("img", { className: "asset-img", src: slot.imageUrl, alt: "" })
                : h("span", null, slot.hasCustomArtwork ? "Custom artwork" : "Steam default"),
            ),
            h(
              Focusable,
              { className: "action-overlay", "flow-children": "row" },
              h(Button, { onClick: () => activate("clear", { tab: slot.id }) }, "Clear"),
              h(Button, { onClick: () => chooseLocalArtwork(slot.id, setActionError) }, "Browse"),
              slot.id !== "icon"
                ? h(
                    Button,
                    { onClick: () => activate("applyInvisible", { tab: slot.id }) },
                    "Invisible",
                  )
                : null,
            ),
          ),
        ),
      ),
      h(
        Focusable,
        { className: "manage-actions", "flow-children": "row" },
        h(Button, { onClick: openLogo }, "Adjust Logo Position"),
        h(Button, { onClick: () => activate("resetLogoPosition") }, "Reset Logo Position"),
      ),
    );
    const nativeTabs = tabs.map((tab) => ({
      id: tab.id,
      title: tab.label,
      content: tab.manage ? manageContent : tab.id === state.activeTab ? assetContent : null,
      footer: tab.manage
        ? undefined
        : {
            onSecondaryActionDescription: "Filter",
            onSecondaryButton: openFilters,
          },
    }));
    return h(
      "div",
      { id: "sgdb-wrap", "aria-label": `Artwork for ${state.appName}` },
      h("style", null, artworkBrowserStyles),
      h(Tabs, {
        autoFocusContents: true,
        activeTab: state.activeTab,
        onShowTab: (tab) => activate("selectTab", { tab }),
        tabs: nativeTabs,
      }),
    );
  }
  const artworkBrowserStyles = `
#sgdb-wrap{--asset-size:170px;margin-top:var(--basicui-header-height,40px);height:calc(100% - var(--basicui-header-height,40px));background:var(--gpSystemDarkestGrey,#0e141b);color:#fff}
#sgdb-wrap div[class*="gamepadtabbedpage_TabHeaderRowWrapper"]{background:#1b2838}
#sgdb-wrap .tabcontents-wrap{display:flex;height:100%;width:100%;flex-direction:column}
#sgdb-wrap .spinnyboi{display:flex;align-items:center;justify-content:center;position:fixed;inset:0;z-index:10008;background:#0e141b}
#sgdb-wrap .spinnyboi img{transform:scale(.75)}
#sgdb-wrap .sgdb-asset-toolbar{display:flex;width:100%;gap:var(--gpSpace-Gap,.6em)}
#sgdb-wrap .filter-buttons{align-items:center;display:flex;gap:.5em}
#sgdb-wrap .filter-buttons>button{min-width:auto;flex:1;white-space:nowrap}
#sgdb-wrap .size-slider{flex:1;padding:.5em 1em;justify-content:center}
#sgdb-wrap #images-container{display:grid;padding-top:1em;padding-bottom:var(--gamepadui-current-footer-height);justify-content:space-evenly;grid-auto-flow:dense;row-gap:1em;column-gap:.65em;grid-template-columns:repeat(auto-fill,minmax(min(var(--asset-size),100%),var(--asset-size)))}
#sgdb-wrap .asset-box-wrap{display:flex;align-items:center;flex-wrap:wrap;position:relative}
#sgdb-wrap .image-wrap{background:url('/images/defaultappimage.png') center/cover;position:relative;width:100%;margin-top:auto;outline:2px solid transparent;transition:outline-color 200ms}
#sgdb-wrap .image-wrap.gpfocus,#sgdb-wrap .image-wrap:hover{z-index:10005;outline-color:rgba(255,255,255,.5)}
#sgdb-wrap .image-wrap.type-logo{padding-bottom:0!important;height:185px}
#sgdb-wrap .image-wrap.type-logo>.thumb,#sgdb-wrap .image-wrap.type-icon>.thumb{background:url('/images/defaultappimage.png') center/cover}
#sgdb-wrap .image-wrap>.thumb{position:absolute;inset:0}
#sgdb-wrap .image-wrap>.thumb img{position:absolute;inset:0;max-height:100%;max-width:100%;width:100%;height:auto;margin:0 auto}
#sgdb-wrap .image-wrap.type-logo>.thumb img,#sgdb-wrap .image-wrap.type-icon>.thumb img{position:static;width:auto;height:100%;object-fit:contain}
#sgdb-wrap .author{font-size:.65em;padding-top:.15em;overflow:hidden;text-shadow:0 1px 1px #000;white-space:nowrap;text-overflow:ellipsis}
#sgdb-wrap .chips{margin:0;padding:0;list-style:none;display:flex;flex-direction:column;position:absolute;right:-.5em;top:0;font-size:.5em;font-weight:bold;text-transform:uppercase;z-index:-1}
#sgdb-wrap .chip{display:flex;align-items:center;justify-content:center;padding:.3em .8em;min-height:2em;border-radius:0 5px 5px 0;transition:transform 300ms cubic-bezier(.33,1,.68,1)}
#sgdb-wrap .chip.animated{background:#e2a256}.chip.nsfw{background:#e5344c}.chip.humor{background:#eec314;color:#434343}.chip.epilepsy{background:#735f9f}
#sgdb-wrap .image-wrap.gpfocus .chip,#sgdb-wrap .image-wrap:hover .chip{transform:translateX(calc(100% - .5em - 1px));box-shadow:1px 2px 3px #0004}
#sgdb-wrap .sgdb-results-state{margin:1em 0 0;min-width:auto}.sgdb-status,.sgdb-empty{padding:1em;text-align:center}.sgdb-status.error{color:#ff6b6b}.sgdb-load-more{display:flex;justify-content:center;padding:1em 0 3em}
#sgdb-wrap #local-images-container{display:grid;grid-template-columns:30% 1fr;gap:1em;margin-bottom:2em}
#sgdb-wrap .asset-label{color:#fff;font-weight:500;letter-spacing:1px;text-transform:uppercase;line-height:20px;margin-bottom:.5em}
#sgdb-wrap .manage-asset{position:relative}.manage-asset .asset{display:flex;min-height:100px;overflow:hidden;background:url('/images/defaultappimage.png') center/cover;align-items:center;justify-content:center}.manage-asset .asset-img{display:block;width:100%;max-height:260px;object-fit:contain}
#sgdb-wrap .action-overlay{display:none;position:absolute;gap:.25em;right:.5em;bottom:.5em;z-index:2}.manage-asset.is-focused .action-overlay,.manage-asset:hover .action-overlay{display:flex}.manage-actions{grid-column:span 2;display:flex;gap:.5em}
.sgdb-modal h2,.sgdb-modal h3{color:#fff}.sgdb-modal-details-wrapper{display:flex;gap:1em}.sgdb-modal-details-wrapper.wide{flex-direction:column}.sgdb-modal-details .modal-image{flex:1}.sgdb-modal-details .modal-image img{display:block;max-width:100%;max-height:55vh;margin:auto}.sgdb-modal-details .info{display:flex;flex-direction:column;flex:1;gap:.5em}.sgdb-modal-details .meta{text-transform:capitalize;opacity:.5;font-size:.8em;text-align:right}.sgdb-modal-details .author{margin-top:1em;font-weight:bold}.sgdb-modal-details .notes{max-width:300px;word-break:break-word}
.sgdb-modal-official-assets .official-steam-asset{margin:0 auto 1em}.sgdb-modal-official-assets img{display:block;max-width:100%;max-height:55vh;margin:auto}.official-steam-asset-action{display:flex;align-items:center;justify-content:space-between;gap:1em;margin-top:.5em}
.sgdb-filter-game{display:flex;align-items:end;gap:.5em}.sgdb-filter-game>div:first-child{flex:1}.sgdb-filter-field{margin-top:1em}.sgdb-filter-field>label{display:block;margin-bottom:.4em;font-weight:600}.sgdb-filter-options,.sgdb-game-matches{display:flex;flex-wrap:wrap;gap:.4em}.sgdb-filter-options>button,.sgdb-game-matches>button{min-width:auto}.sgdb-filter-selected{box-shadow:inset 0 0 0 2px var(--gpStoreLightestGrey,#fff)}.sgdb-modal-actions{display:flex;justify-content:flex-end;gap:.5em;margin-top:1em}
.sgdb-logo-preview{height:250px;position:relative;background:#1b2838;overflow:hidden}.sgdb-logo-sample{position:absolute;padding:10px;font-size:28px;font-weight:bold}.anchor-TopLeft{left:0;top:0}.anchor-TopCenter{left:50%;top:0;transform:translateX(-50%)}.anchor-TopRight{right:0;top:0}.anchor-CenterLeft{left:0;top:50%;transform:translateY(-50%)}.anchor-CenterCenter{left:50%;top:50%;transform:translate(-50%,-50%)}.anchor-CenterRight{right:0;top:50%;transform:translateY(-50%)}.anchor-BottomLeft{left:0;bottom:0}.anchor-BottomCenter{left:50%;bottom:0;transform:translateX(-50%)}.anchor-BottomRight{right:0;bottom:0}.sgdb-logo-anchors{display:grid;grid-template-columns:repeat(3,1fr);gap:.4em;margin:1em 0}.sgdb-logo-anchors>button{min-width:auto}
`;
  const artworkBrowserPage = registerSteamPage({
    template: "artwork-browser",
    gate: "artworkBrowser",
    patchId: ArtworkBrowserPatchId,
    components: resolveSteamUiComponents,
    required: [
      "react",
      "focusable",
      "sliderField",
      "toggleField",
      "dialogButton",
      "dialogButtonPrimary",
      "tabs",
      "modalRoot",
      "showModal",
    ],
    // A modal left open when the gate goes keeps drawing with Steam's components, which stay valid, so
    // it never throws inside Steam's modal layer; it only loses the state.
    release: () => {
      artworkDesired = null;
      for (const listener of [...artworkListeners]) listener(null);
    },
    status: () => ({ appId: artworkBrowserPage.state()?.appId ?? 0 }),
    Page: ArtworkBrowserPage,
  });
  // @fragment consumer/chord-reset.ts
  /**
   * Observes chord-layout default resets while the host reports an active template mirror.
   * @returns Install/remove controls and diagnostics; a failed release retains ownership for a later removal attempt.
   */
  function createWsgmChordReset() {
    const patchId = "wsgm.chord-reset";
    const ChordAppId = 443510;
    const member = "SetSelectedConfigForApp";
    const claimKeys = {
      marker: "__wsgmChordResetClaimed",
      original: "__wsgmChordResetOriginal",
    };
    let installed = false;
    let active = false;
    let unsubscribe = null;
    let lastError = "";
    let resets = 0;
    const input = () => globalThis.SteamClient?.Input ?? null;
    const install = () => {
      if (installed) return { ok: true, installed: true };
      const target = input();
      if (!target || typeof target[member] !== "function") {
        lastError = "SteamClient.Input.SetSelectedConfigForApp is absent";
        return { ok: false, error: lastError };
      }
      const claim = claimMember(
        target,
        member,
        claimKeys,
        (original) =>
          function (appId, controllerIndex, url, ...rest) {
            if (
              active &&
              Number(appId) === ChordAppId &&
              typeof url === "string" &&
              url.startsWith("default://")
            ) {
              resets++;
              request(patchId, "reset", null).catch(() => {});
            }
            return original.apply(this, [appId, controllerIndex, url, ...rest]);
          },
      );
      if (!claim.ok) {
        lastError = claim.error;
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        active = !!state?.active;
      });
      return { ok: true, installed: true };
    };
    // The member is released before the gate forgets it is installed, so a failed release is retried
    // by the next remove rather than left in Steam behind an "absent" answer.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseMember(input(), member, claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "chord reset release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      active = false;
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      hooked: memberClaimed(input(), member, claimKeys),
      active,
      subscribed: !!unsubscribe,
      resets,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("wsgmChordReset", createWsgmChordReset());
  // @fragment consumer/controller-caps.ts
  /**
   * Masks host-selected capability bits only for the matching VID/PID in Steam UI controller-list responses.
   * @returns Install/remove controls and diagnostics; a failed release retains ownership for a later removal attempt.
   */
  function createWsgmControllerCaps() {
    const patchId = "wsgm.controller-caps";
    const ServiceTokens = ["SteamInputManager.GetControllerList#1", "GetControllerListHandler"];
    const isService = (value) =>
      !!value &&
      typeof value === "object" &&
      typeof value.GetControllerList === "function" &&
      typeof value.RegisterForNotifyControllerListChanged === "function";
    // These stores cache controller lists indefinitely; invalidate the query before refreshing them.
    const ListQueryKey = ["ControllerList"];
    const StoreFingerprints = [
      [
        ["GetControllerBySerial", "m_unboundControllerList"],
        (value) =>
          typeof value?.GetControllerBySerial === "function" &&
          typeof value?.DoControllerListQuery === "function",
      ],
      [
        ["m_pendingEditingConfiguration", "EnsureEditingConfiguration"],
        (value) =>
          typeof value?.EnsureEditingConfiguration === "function" &&
          typeof value?.DoControllerListQuery === "function",
      ],
    ];
    // A member claim, so a bridge replaced without its dispose (a JS context reload) reclaims the
    // wrapper it left instead of wrapping it again, and removal hands back the displaced function.
    const claimKeys = {
      marker: "__wsgmControllerCapsClaimed",
      original: "__wsgmControllerCapsOriginal",
    };
    let installed = false;
    let service = null;
    let unsubscribe = null;
    let resolver = null;
    let lastError = "";
    let mask = 0n;
    let vendorId = 0;
    let productId = 0;
    let masked = 0;
    let refreshed = 0;
    const applyState = (state) => {
      try {
        mask = BigInt(state?.mask ?? 0);
      } catch {
        mask = 0n;
      }
      vendorId = Number(state?.vendorId ?? 0);
      productId = Number(state?.productId ?? 0);
    };
    const maskList = (list) => {
      if (mask === 0n || !list || typeof list !== "object" || !Array.isArray(list.controllers))
        return list;
      for (const controller of list.controllers) {
        if (
          !controller ||
          Number(controller.vendor_id) !== vendorId ||
          Number(controller.product_id) !== productId
        )
          continue;
        let caps;
        try {
          caps = BigInt(controller.capabilities ?? 0);
        } catch {
          continue;
        }
        const cleared = caps & ~mask;
        if (cleared !== caps) {
          controller.capabilities = cleared.toString();
          masked++;
        }
      }
      return list;
    };
    // The response object is Steam's protobuf wrapper: Body() is the message, toObject() the plain
    // shape every mapper reads. Both are replaced on the instance only, so the wrapper's own type stays
    // as it was.
    const maskResponse = (response) => {
      if (
        !response ||
        typeof response.Body !== "function" ||
        typeof response.BSuccess !== "function"
      )
        return response;
      try {
        if (!response.BSuccess()) return response;
        const body = response.Body();
        if (!body || typeof body.toObject !== "function") return response;
        const toObject = body.toObject.bind(body);
        body.toObject = () => maskList(toObject());
        response.Body = () => body;
      } catch {
        // A response shaped differently than expected is handed on as it is.
      }
      return response;
    };
    const hook = () => {
      try {
        resolver ??= getWebpackRuntime("controller-caps");
        service = resolver.exported(ServiceTokens, isService);
      } catch (error) {
        lastError = String(error);
        return false;
      }
      const claim = claimMember(
        service,
        "GetControllerList",
        claimKeys,
        (original) =>
          function (...args) {
            const result = original.apply(this, args);
            return result && typeof result.then === "function"
              ? result.then(maskResponse)
              : maskResponse(result);
          },
      );
      if (!claim.ok) {
        lastError = claim.error;
        service = null;
        return false;
      }
      return true;
    };
    const refresh = () => {
      if (!resolver) return;
      invalidateQuery(resolver, ListQueryKey);
      for (const [tokens, predicate] of StoreFingerprints) {
        try {
          const store = resolver.exported(tokens, predicate);
          const pending = store.DoControllerListQuery();
          if (pending && typeof pending.catch === "function") pending.catch(() => {});
          refreshed++;
        } catch {
          // A store that is absent or has moved keeps its list until Steam's own notification.
        }
      }
    };
    const install = () => {
      if (installed) return { ok: true, installed: true };
      if (!hook()) return { ok: false, error: lastError };
      installed = true;
      lastError = "";
      unsubscribe = subscribe(patchId, (state) => {
        const before = `${mask}/${vendorId}/${productId}`;
        applyState(state);
        if (`${mask}/${vendorId}/${productId}` !== before) refresh();
      });
      refresh();
      return { ok: true, installed: true };
    };
    // The member is released before the gate forgets it is installed, so a failed release is retried
    // by the next remove rather than left in Steam behind an "absent" answer.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseMember(service, "GetControllerList", claimKeys);
      if (!released.ok) {
        lastError = released.error ?? "controller list release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      unsubscribe = endSubscription(unsubscribe);
      service = null;
      mask = 0n;
      refresh();
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      hooked: memberClaimed(service, "GetControllerList", claimKeys),
      subscribed: !!unsubscribe,
      mask: mask.toString(),
      vendorId,
      productId,
      masked,
      refreshed,
      lastError,
    });
    return { install, remove, status };
  }
  registerGate("wsgmControllerCaps", createWsgmControllerCaps());
  // @fragment consumer/download-sort.ts
  /**
   * Adds host-defined download ordering through reversible Steam render and method claims.
   * @returns Install/remove controls and diagnostics; a failed release retains ownership for a later removal attempt.
   */
  function createWsgmDownloadSort() {
    const patchId = "wsgm.download-sort";
    const transformName = "wsgm.download-sort";
    const sectionToken = "#Downloads_Section_Current";
    const ReactExportTokens = [
      "react.transitional.element",
      "useState",
      "cloneElement",
      "createElement",
    ];
    const FocusableTokens = [
      '"flow-children"',
      "onActivate:",
      "focusClassName",
      "focusWithinClassName",
    ];
    const DownloadProgressTokens = [
      "k_EAppUpdateProgress_Preallocating=",
      "k_EAppUpdateProgress_Download=",
    ];
    let installed = false;
    let runtime = null;
    let react = null;
    let focusable = null;
    let downloadIndex;
    let state = { key: null, dir: 1, busy: false };
    let lastError = "";
    const modules = () => getWebpackRuntime("download-sort");
    const sourceOf = (value) => {
      try {
        const fn =
          typeof value === "function" ? value : value && value.render ? value.render : null;
        return fn ? Function.prototype.toString.call(fn) : "";
      } catch {
        return "";
      }
    };
    const scan = () => {
      if (react && focusable && downloadIndex !== undefined) return;
      const resolve = modules().resolve;
      const found = resolve(ReactExportTokens);
      if (!found || !found.createElement || !found.useMemo || !found.version)
        throw new Error("React exports unavailable");
      const focusables = Object.values(resolve(FocusableTokens)).filter((value) => {
        const source = sourceOf(value);
        return (
          typeof value === "function" &&
          source.length < 1500 &&
          source.indexOf("class") !== 0 &&
          FocusableTokens.every((token) => source.includes(token))
        );
      });
      if (focusables.length !== 1) throw new Error("Focusable export is absent or ambiguous");
      const enums = Object.values(resolve(DownloadProgressTokens)).filter(
        (value) =>
          value &&
          typeof value === "object" &&
          Number.isInteger(value.k_EAppUpdateProgress_Download),
      );
      if (enums.length !== 1) throw new Error("Download progress enum is absent or ambiguous");
      react = found;
      focusable = focusables[0];
      downloadIndex = enums[0].k_EAppUpdateProgress_Download;
    };
    // Bytes LEFT to download, not the total: the queue is about what is still coming down the wire.
    // Returns -1 for "Steam has not planned this app yet" (every bytes_total still 0, which is what a
    // freshly restarted client reports for a queued-but-not-started app) so those can be parked
    // instead of being ranked as the smallest download.
    const bytesLeft = (transfer) => {
      let total = 0;
      let done = 0;
      for (const info of transfer.update_type_info || []) {
        const progress = info.progress && info.progress[downloadIndex];
        if (!progress) continue;
        total += progress.bytes_total || 0;
        done += progress.bytes_in_progress || 0;
      }
      if (total <= 0) return -1;
      return Math.max(0, total - done);
    };
    const nameOf = (transfer) => {
      const overview = window.appStore && window.appStore.GetAppOverviewByAppID(transfer.appid);
      return (
        overview && overview.display_name ? overview.display_name : String(transfer.appid)
      ).toLocaleLowerCase();
    };
    const kindOf = (transfer) => (transfer.buildid === 0 ? 0 : 1);
    // Direction is applied INSIDE each comparator: items with an unknown size must stay at the end in
    // both directions, which an outer sign flip cannot express.
    const keys = [
      { id: "name", label: "NAME", cmp: (a, b, dir) => dir * nameOf(a).localeCompare(nameOf(b)) },
      {
        id: "size",
        label: "SIZE",
        cmp: (a, b, dir) => {
          const x = bytesLeft(a);
          const y = bytesLeft(b);
          if (x < 0 && y < 0) return nameOf(a).localeCompare(nameOf(b));
          if (x < 0) return 1;
          if (y < 0) return -1;
          return dir * (x - y) || nameOf(a).localeCompare(nameOf(b));
        },
      },
      {
        id: "type",
        label: "TYPE",
        cmp: (a, b, dir) => dir * (kindOf(a) - kindOf(b)) || nameOf(a).localeCompare(nameOf(b)),
      },
    ];
    // The whole pending list, not just the running queue: scheduled and unqueued entries are part of
    // what the user sees on the page, so they are sorted in with everything else. Assigning them a
    // queue index is what dragging them into the queue does in Steam's own UI.
    const pendingTransfers = () => {
      const store = window.downloadsStore;
      if (!store) return [];
      const seen = {};
      const out = [];
      const add = (list) => {
        for (const transfer of list || []) {
          if (!transfer || transfer.completed) continue;
          if (seen[transfer.appid]) continue;
          seen[transfer.appid] = 1;
          out.push(transfer);
        }
      };
      add(store.QueuedTransfers);
      add(store.UnqueuedTransfers);
      add(store.ScheduledTransfers);
      // Stable starting point: queued entries keep their order, everything unqueued follows in the
      // order Steam listed it.
      return out.sort((a, b) => {
        const x = a.queue_index < 0 ? 1e9 : a.queue_index;
        const y = b.queue_index < 0 ? 1e9 : b.queue_index;
        return x - y;
      });
    };
    // The page has no log that reaches wsgm.log, so a run's refusals go to the host as the patch's one
    // command. Only a bridge replaced mid-run loses the report, and then the console is the one place
    // left to say so.
    const report = (refusal) => {
      const say = (error) => {
        console.warn(
          "WSGM download sort: Steam refused " +
            refusal.refused +
            " of " +
            refusal.total +
            " queue positions (first: " +
            refusal.first +
            "); the report did not reach WSGM: " +
            String((error && error.message) || error),
        );
      };
      try {
        request(patchId, "refused", refusal).catch(say);
      } catch (error) {
        say(error);
      }
    };
    // A best-effort repaint of the queue's storage-keyed list sections after the order changed under
    // them. The walk recurses on child and sibling alike, so depth counts every earlier sibling as
    // well; maxDepth keeps that recursion far inside the engine's stack. The download page has few
    // such sections, and maxUpdates stops the walk from repainting every storage-keyed component
    // elsewhere in the popup.
    const rerender = () => {
      const maxDepth = 500;
      const maxUpdates = 12;
      try {
        const manager = window.g_PopupManager;
        if (!manager) return;
        for (const popup of Array.from(manager.GetPopups())) {
          const popupDocument = popup.m_popup && popup.m_popup.document;
          if (!popupDocument) continue;
          const row = popupDocument.querySelector("[data-rbd-draggable-id]");
          if (!row) continue;
          const fiberKey = Object.keys(row).filter((key) => key.indexOf("__reactFiber$") === 0)[0];
          if (!fiberKey) continue;
          let fiber = row[fiberKey];
          while (fiber.return) fiber = fiber.return;
          let seen = 0;
          const visit = (node, depth) => {
            if (!node || depth > maxDepth || seen > maxUpdates) return;
            const type = node.type;
            if (
              node.stateNode &&
              typeof type === "function" &&
              type.prototype &&
              type.prototype.GetStorageKey
            ) {
              try {
                node.stateNode.forceUpdate();
                seen++;
              } catch {
                // A section that cannot repaint now repaints on Steam's own next render.
              }
            }
            visit(node.child, depth + 1);
            visit(node.sibling, depth + 1);
          };
          visit(fiber, 0);
        }
      } catch {
        // Best effort: the order is already applied, and the page catches up on its own.
      }
    };
    const applySort = (keyId) => {
      const current = state;
      if (current.busy) return;
      current.dir = current.key === keyId ? -current.dir : 1;
      current.key = keyId;
      current.busy = true;
      rerender();
      const items = pendingTransfers();
      if (items.length < 2) {
        current.busy = false;
        rerender();
        return;
      }
      // Always renumber from 0: the list includes unqueued and scheduled entries whose queue_index is
      // -1, so seeding from items[0] could hand SetQueueIndex a negative index.
      const start = 0;
      const definition = keys.filter((key) => key.id === keyId)[0];
      const sorted = items.slice().sort((a, b) => definition.cmp(a, b, current.dir));
      // One SetQueueIndex per pace, as on the device pass (docs/steam-cef.md §12): a fifty-entry
      // re-queue takes about 6 s with the buttons dimmed. A rejected index does not stop the run; the
      // run ends by telling WSGM how many Steam refused.
      const paceMs = 120;
      let index = 0;
      let failed = 0;
      let firstError = "";
      const fail = (error) => {
        failed++;
        if (!firstError) firstError = String((error && error.message) || error);
      };
      const step = () => {
        if (index >= sorted.length) {
          current.busy = false;
          rerender();
          if (failed) report({ refused: failed, total: sorted.length, first: firstError });
          return;
        }
        try {
          const moved = window.SteamClient.Downloads.SetQueueIndex(
            sorted[index].appid,
            start + index,
            window.downloadsStore.CurrentViewingRemoteClientID,
          );
          if (moved && typeof moved.catch === "function") moved.catch(fail);
        } catch (error) {
          fail(error);
        }
        index++;
        setTimeout(step, paceMs);
      };
      step();
    };
    const sortBar = () => {
      const current = state;
      const children = [
        react.createElement(
          "span",
          {
            key: "cap",
            style: {
              fontSize: "11px",
              letterSpacing: ".5px",
              color: "#8ba6b8",
              marginRight: "2px",
            },
          },
          "SORT:",
        ),
      ];
      keys.forEach((key) => {
        const on = current.key === key.id;
        children.push(
          react.createElement(
            focusable,
            {
              key: key.id,
              onActivate: () => applySort(key.id),
              style: {
                font: "inherit",
                fontSize: "11px",
                letterSpacing: ".5px",
                lineHeight: "1",
                padding: "5px 9px",
                border: "1px solid " + (on ? "rgba(103,193,245,.55)" : "rgba(255,255,255,.18)"),
                borderRadius: "2px",
                background: on ? "rgba(103,193,245,.20)" : "rgba(255,255,255,.07)",
                color: on ? "#67c1f5" : "#c6d4df",
                cursor: "pointer",
                opacity: current.busy ? 0.5 : 1,
              },
            },
            key.label + (on ? (current.dir > 0 ? " ↑" : " ↓") : ""),
          ),
        );
      });
      return react.createElement(
        focusable,
        {
          key: "wsgm-sort",
          "flow-children": "row",
          style: {
            display: "flex",
            alignItems: "center",
            gap: "6px",
            flex: "0 0 auto",
            paddingLeft: "12px",
          },
        },
        children,
      );
    };
    // One element transform on the shared JSX-runtime claim: the queue header, and nothing else, comes
    // back inside a row with the sort bar. Undefined leaves every other element to the runtime.
    const transform = (create, type, props, key) => {
      if (!(
        props &&
        props.sectionTitle === sectionToken &&
        props.count !== undefined &&
        props.labelId !== undefined
      )) {
        return undefined;
      }
      const header = create(
        type,
        { ...props, style: { ...props.style, flex: "1 1 auto", minWidth: 0 } },
        key,
      );
      // paddingRight matches the header's own 16px gutter so the bar lines up with the right edge of
      // the rows, not the window edge.
      return react.createElement(
        "div",
        {
          style: {
            display: "flex",
            alignItems: "center",
            width: "100%",
            paddingRight: "16px",
            boxSizing: "border-box",
          },
        },
        header,
        sortBar(),
      );
    };
    const install = () => {
      if (installed) return { ok: true, installed: true };
      try {
        scan();
        runtime = modules().resolve([...JsxRuntimeTokens]);
      } catch (error) {
        lastError = String((error && error.stack) || error);
        return { ok: false, error: lastError };
      }
      const registered = interceptElements(runtime, transformName, transform);
      if (!registered.ok) {
        lastError = registered.error || "element transform refused";
        return { ok: false, error: lastError };
      }
      installed = true;
      lastError = "";
      rerender();
      return { ok: true, installed: true };
    };
    // The transform is released before the gate forgets it is installed, so a failed release is
    // retried by the next remove rather than left in Steam behind an "absent" answer.
    const remove = () => {
      if (!installed) return { ok: true, absent: true };
      const released = releaseElements(runtime, transformName);
      if (!released.ok) {
        lastError = released.error || "download sort release failed";
        return { ok: false, error: lastError };
      }
      installed = false;
      runtime = null;
      state = { key: null, dir: 1, busy: false };
      rerender();
      return { ok: true, removed: true };
    };
    const status = () => ({
      ok: true,
      installed,
      registered: !!runtime && elementsIntercepted(runtime, transformName),
      sorting: state.busy,
      lastError,
    });
    return { install, remove, status, applySort };
  }
  registerGate("wsgmDownloadSort", createWsgmDownloadSort());
  // @fragment consumer/emulators.ts
  // Emulator management presentation. All package, BIOS and preference writes belong to EmulatorService.
  const emulatorStyles = `
#wsgm-import.wsgm-emulators .wsgm-import-head { padding-bottom:12px; }
#wsgm-import.wsgm-emulators .wsgm-import-body { display:block; overflow:auto; padding-bottom:56px; }
.wsgm-emu-content { display:flex; flex-direction:column; gap:14px; min-width:0; }
.wsgm-emu-content h2,.wsgm-emu-content p { margin:0; }
.wsgm-emu-tabs { display:flex; gap:8px; flex-wrap:wrap; border-bottom:1px solid #3d4450; padding-bottom:12px; }
.wsgm-emu-tabs .DialogButton { width:auto; min-width:0; height:36px; }
.wsgm-emu-tabs [aria-pressed=true] { background:var(--gpSystemBlue,#1a9fff); color:white; }
.wsgm-emu-toolbar { display:flex; gap:12px; align-items:flex-end; flex-wrap:wrap; }
.wsgm-emu-toolbar .DialogInputLabelGroup { margin:0; }
.wsgm-emu-toolbar .DialogButton { width:auto; min-width:0; }
.wsgm-emu-search { flex:1; min-width:180px; }
.wsgm-emu-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(220px,1fr)); gap:18px; }
.wsgm-emu-grid .steam-ui-kit-card { width:auto; }
.wsgm-emu-mark { position:absolute; inset:0; display:flex; align-items:center; justify-content:center; font-size:28px; font-weight:700; color:#fff; }
.wsgm-emu-split { display:grid; grid-template-columns:minmax(0,2fr) minmax(240px,1fr); gap:24px; }
.wsgm-emu-column { display:flex; flex-direction:column; gap:14px; min-width:0; }
.wsgm-emu-actions { display:flex; gap:10px; flex-wrap:wrap; }
.wsgm-emu-actions .DialogButton { width:auto; min-width:0; }
.wsgm-emu-fact { display:flex; flex-direction:column; gap:4px; font-size:14px; overflow-wrap:anywhere; }
.wsgm-emu-fact > span:first-child { color:#8b929a; font-size:12px; }
.wsgm-emu-file { display:flex; justify-content:space-between; gap:14px; padding:8px 0; border-top:1px solid #3d4450; overflow-wrap:anywhere; }
.wsgm-emu-badge { align-self:center; white-space:nowrap; font-size:12px; font-weight:600; color:#b8bcbf; }
.wsgm-emu-badge[data-status=Missing], .wsgm-emu-badge[data-status=Partial] { color:#ffc66d; }
.wsgm-emu-badge[data-status="Wrong file"] { color:#ff6d6d; }
.wsgm-emu-badge[data-status=Verified] { color:#a3dca3; }
.wsgm-emu-core { display:grid; grid-template-columns:minmax(160px,1fr) minmax(100px,1fr) auto; gap:16px; align-items:center; padding:10px 12px; }
.wsgm-emu-core.gpfocus { background:#fff; color:#0e141b; }
.wsgm-emu-default { display:grid; grid-template-columns:180px minmax(180px,1fr) minmax(180px,1fr); align-items:center; gap:14px; padding:8px 0; }
@media(max-width:1000px) { .wsgm-emu-default { grid-template-columns:130px minmax(120px,1fr) minmax(120px,1fr); } }
@media(max-width:750px) { .wsgm-emu-split { grid-template-columns:1fr; } .wsgm-emu-default { grid-template-columns:1fr; } }
`;
  function ImportEmulatorPage({ state, status, onBack }) {
    const react = importUi.react;
    const h = react.createElement;
    const progress = useEmulatorProgress();
    const [tab, setTab] = react.useState("installed");
    const [route, setRoute] = react.useState({ kind: "list", id: "" });
    const [channel, setChannel] = react.useState("");
    const [search, setSearch] = react.useState("");
    const [coreSearch, setCoreSearch] = react.useState("");
    const [coreFilter, setCoreFilter] = react.useState("all");
    const [biosId, setBiosId] = react.useState("");
    const [biosReturn, setBiosReturn] = react.useState(null);
    const manager = state.emulators ?? {
      definitions: [],
      installations: [],
      offers: [],
      systemPreferences: [],
    };
    const bios = state.bios ?? { folder: "", checked: false, systems: [] };
    const installed = manager.installations ?? [];
    const available = manager.definitions.filter(
      (item) => !installed.some((entry) => entry.definitionId === item.id),
    );
    const selectTab = (value) => {
      setBiosReturn(null);
      setTab(value);
      setRoute({ kind: "list", id: "" });
    };
    const back = () =>
      route.kind === "cores"
        ? setRoute({ kind: "installation", id: route.id })
        : route.kind !== "list"
          ? setRoute({ kind: "list", id: "" })
          : biosReturn
            ? (setTab(biosReturn.tab), setRoute(biosReturn.route), setBiosReturn(null))
            : onBack?.();
    const button = (label, run, idle = true, primary = false) =>
      h(
        primary ? importUi.dialogButtonPrimary : importUi.dialogButton,
        { disabled: idle && progress.busy, onClick: run },
        label,
      );
    // These rows wrap on handheld widths; Steam must use their geometry on both axes.
    const actions = (...children) =>
      h(
        importUi.focusable,
        { className: "wsgm-emu-actions", "flow-children": "grid" },
        ...children,
      );
    const fact = (label, value) =>
      h("div", { className: "wsgm-emu-fact" }, h("span", {}, label), h("span", {}, value || "—"));
    const box = (label, ...children) => renderSteamUiBox(react, label, ...children);
    const badge = (value) =>
      h("span", { className: "wsgm-emu-badge", "data-status": value }, value);
    const offerFor = (entry) =>
      manager.offers.find(
        (item) =>
          item.definitionId === entry.definitionId &&
          item.channel === entry.channel &&
          item.architecture === entry.architecture,
      );
    const labelFor = (entry) => (state.installed ?? []).find((item) => item.id === entry.id);
    const systemName = (id) => (state.romSystems ?? []).find((item) => item.id === id)?.name ?? id;
    const matches = (text, value) => value.toLowerCase().includes(text.toLowerCase());
    const notes = (offer) =>
      offer?.notesUrl
        ? button(
            "Release notes",
            () =>
              void emulatorAct("openEmulatorReleaseNotes", {
                definitionId: offer.definitionId,
                channel: offer.channel,
                architecture: offer.architecture,
              }),
            false,
          )
        : null;
    const pick = (title, mode, command, extra = {}) =>
      void showSteamFilePicker(importUi, { title, mode }).then(
        (path) => path && void emulatorAct(command, { path, ...extra }),
      );
    const setup = (installationId, name) =>
      showSteamModal(importUi, {
        title: name + " setup",
        render: (close) => h(ImportEmulatorSetupBody, { installationId, close }),
      });
    const toBios = () => {
      setBiosReturn({ tab, route });
      setTab("bios");
      setRoute({ kind: "list", id: "" });
    };
    const canBack = route.kind !== "list" || !!biosReturn || typeof onBack === "function";
    const tabs = h(
      importUi.focusable,
      { className: "wsgm-emu-tabs", "flow-children": "grid" },
      ...[
        ["installed", `Installed ${installed.length}`],
        ["available", `Available ${available.length}`],
        ["bios", "BIOS & firmware"],
        ["defaults", "System defaults"],
      ].map(([id, label]) =>
        h(
          importUi.dialogButton,
          { key: id, "aria-pressed": tab === id, onClick: () => selectTab(id) },
          label,
        ),
      ),
    );
    let content;
    if (route.kind === "installation" || route.kind === "cores") {
      const entry = installed.find((item) => item.id === route.id);
      if (!entry) {
        content = h(
          "p",
          {},
          "That installation is no longer registered. Go Back to the installed list.",
        );
      } else {
        const offer = offerFor(entry);
        const installationId = entry.id;
        const definition = manager.definitions.find((item) => item.id === entry.definitionId);
        const remove = () =>
          showSteamUiConfirm(importUi, {
            title: entry.managed ? "Remove emulator?" : "Forget external installation?",
            confirmLabel: entry.managed ? "Remove" : "Forget",
            text:
              ((state.dependencies ?? []).find((item) => item.installationId === installationId)
                ?.summary ?? "Titles using this installation will need another emulator.") +
              " ROMs, saves, settings and firmware stay.",
            onConfirm: () =>
              void request(EmulatorPatchId, "removeEmulator", { installationId }).then(
                () => setRoute({ kind: "list", id: "" }),
                (error) => emulatorReport({ text: importError(error), error: true }),
              ),
          });
        const maintenance = box(
          "Maintenance",
          entry.managed
            ? button(
                "Repair installation",
                () => void emulatorAct("repairEmulator", { installationId }),
              )
            : null,
          button(entry.managed ? "Remove emulator…" : "Forget external installation…", remove),
          h("p", { className: "steam-ui-kit-muted" }, "ROMs, saves, settings and firmware stay."),
        );
        const systemFiles = box(
          "System files",
          ...(entry.missingRequirements ?? []).map((text) => h("p", { key: text }, text)),
          fact("BIOS folder", bios.folder),
          button("Open BIOS & firmware", toBios, false),
          definition?.prerequisites?.length
            ? button("Emulator-specific setup…", () => setup(installationId, entry.name), false)
            : null,
        );
        if (route.kind === "cores") {
          const cores = entry.cores ?? [];
          const needFiles = (core) =>
            (state.coreStatus ?? []).find(
              (item) => item.installationId === installationId && item.coreId === core.id,
            )?.missing ?? false;
          const found = cores.filter((core) =>
            matches(
              coreSearch,
              `${core.name} ${core.id} ${(core.systems ?? []).map(systemName).join(" ")}`,
            ),
          );
          const visible = (coreFilter === "all" ? cores : found)
            .filter((core) => coreFilter !== "files" || needFiles(core))
            .filter((core) => coreFilter !== "metadata" || core.metadataMissing);
          content = h(
            importUi.focusable,
            { className: "wsgm-emu-split", "flow-children": "grid" },
            h(
              importUi.focusable,
              { className: "wsgm-emu-column", "flow-children": "column" },
              h("h2", {}, `${entry.name} · Installed cores ${cores.length}`),
              h(importUi.textField, {
                label: "Search cores or systems",
                value: coreSearch,
                onChange: (event) => {
                  setCoreSearch(event?.target?.value ?? "");
                  setCoreFilter("matches");
                },
              }),
              actions(
                ...[
                  ["all", `All ${cores.length}`],
                  ["matches", `Matches ${found.length}`],
                  ["files", `Need files ${cores.filter(needFiles).length}`],
                  [
                    "metadata",
                    `No metadata ${cores.filter((core) => core.metadataMissing).length}`,
                  ],
                ].map(([id, label]) =>
                  h(
                    importUi.dialogButton,
                    {
                      key: id,
                      "aria-pressed": coreFilter === id,
                      onClick: () => setCoreFilter(id),
                    },
                    label,
                  ),
                ),
              ),
              ...visible.map((core) =>
                h(
                  importUi.focusable,
                  {
                    key: core.id,
                    className: "wsgm-emu-core",
                    onActivate: () =>
                      showSteamModal(importUi, {
                        title: core.name,
                        render: (close) =>
                          renderSteamUiSheet(
                            importUi,
                            { actions: [{ id: "close", label: "Close", onClick: close }] },
                            fact("Core", core.path),
                            fact("Systems", (core.systems ?? []).map(systemName).join(", ")),
                            fact("File types", (core.extensions ?? []).join(", ")),
                            fact(
                              "Metadata",
                              core.metadataMissing
                                ? "Unavailable; choose the system manually"
                                : "Supplied by the core metadata",
                            ),
                            ...(core.requiredFiles ?? []).map((path) => fact("Required", path)),
                          ),
                      }),
                  },
                  h("span", {}, core.name),
                  h("span", {}, (core.systems ?? []).map(systemName).join(", ")),
                  badge(
                    core.metadataMissing ? "No metadata" : needFiles(core) ? "Need files" : "Ready",
                  ),
                ),
              ),
            ),
            h(
              importUi.focusable,
              { className: "wsgm-emu-column", "flow-children": "column" },
              box(
                "RetroArch",
                fact("Installed", `${entry.version} · ${entry.channel} · ${cores.length} cores`),
                entry.managed
                  ? button(
                      "Update installed cores",
                      () => void emulatorAct("updateEmulator", { installationId }),
                    )
                  : null,
                notes(offer),
              ),
              systemFiles,
              maintenance,
            ),
          );
        } else {
          content = h(
            importUi.focusable,
            { className: "wsgm-emu-split", "flow-children": "grid" },
            h(
              importUi.focusable,
              { className: "wsgm-emu-column", "flow-children": "column" },
              h("h2", {}, entry.name),
              fact(
                "Installed",
                `${entry.version} · ${entry.channel} · ${entry.managed ? "Managed" : "External"}`,
              ),
              fact("Systems", (entry.systems ?? []).map(systemName).join(" · ")),
              systemFiles,
              box(
                "Installation",
                fact("Source", entry.source),
                fact("Verification", entry.integrity),
                fact("Architecture", entry.architecture),
                fact("Executable", entry.executablePath),
                fact("Data", entry.dataPath),
              ),
              entry.cores?.length
                ? button(
                    `Installed cores (${entry.cores.length})`,
                    () => setRoute({ kind: "cores", id: installationId }),
                    false,
                  )
                : null,
            ),
            h(
              importUi.focusable,
              { className: "wsgm-emu-column", "flow-children": "column" },
              box(
                offer?.releaseId && offer.releaseId !== entry.releaseId
                  ? "Update available"
                  : "Release",
                fact("Latest", offer?.error || offer?.version || "Check for updates"),
                offer?.releaseId && offer.releaseId !== entry.releaseId && entry.managed
                  ? button(
                      `Update to ${offer.version}`,
                      () => void emulatorAct("updateEmulator", { installationId }),
                      true,
                      true,
                    )
                  : null,
                offer?.releaseId && offer.releaseId !== entry.releaseId
                  ? button(
                      "Skip this version",
                      () =>
                        void emulatorAct("ignoreEmulatorVersion", {
                          installationId,
                          releaseId: offer.releaseId,
                        }),
                    )
                  : null,
                offer?.releaseId === entry.ignoredReleaseId
                  ? h("p", {}, "You skipped this release. You can still install it now.")
                  : null,
                notes(offer),
                h(
                  "p",
                  { className: "steam-ui-kit-muted" },
                  "Settings, saves and supplied firmware are preserved.",
                ),
              ),
              maintenance,
            ),
          );
        }
      }
    } else if (route.kind === "definition") {
      const definition = manager.definitions.find((item) => item.id === route.id);
      if (!definition) content = h("p", {}, "That emulator is no longer listed.");
      else {
        const offer = manager.offers.find(
          (item) =>
            item.definitionId === definition.id &&
            item.channel === channel &&
            item.architecture === state.architecture,
        );
        content = h(
          importUi.focusable,
          { className: "wsgm-emu-split", "flow-children": "grid" },
          h(
            importUi.focusable,
            { className: "wsgm-emu-column", "flow-children": "column" },
            h("h2", {}, definition.name),
            fact("Source", definition.source),
            fact("Systems", definition.systems.map(systemName).join(" · ")),
            box(
              "Set up from your BIOS folder",
              fact("BIOS folder", bios.folder),
              ...definition.prerequisites.map((rule) => fact(rule.name, rule.description)),
              button("Open BIOS & firmware", toBios, false),
            ),
          ),
          h(
            importUi.focusable,
            { className: "wsgm-emu-column", "flow-children": "column" },
            box(
              "Install",
              renderSteamUiChoice(importUi, {
                label: "Release channel",
                rgOptions: definition.channels.map((value) => ({ data: value, label: value })),
                selectedOption: channel,
                onChange: (option) => option && setChannel(option.data),
              }),
              fact(
                "Latest",
                offer?.error || offer?.version || "Check for updates to read releases",
              ),
              definition.dataPolicy?.hasCores
                ? h("p", {}, "Installs the complete published Windows core catalogue.")
                : null,
              button(
                `Install ${channel}`,
                () => void emulatorAct("installEmulator", { definitionId: definition.id, channel }),
                true,
                true,
              ),
              notes(offer),
              button("Use an existing installation…", () =>
                pick("Choose emulator executable", "file", "useExternalEmulator", {
                  definitionId: definition.id,
                }),
              ),
            ),
            box(
              "Already installed?",
              h(
                "p",
                {},
                "Register your executable. WSGM will not own its updates or remove its files.",
              ),
            ),
          ),
        );
      }
    } else if (tab === "defaults") {
      const systems = state.romSystems ?? [];
      const groups = {};
      for (const system of systems) {
        const group =
          system.group ||
          (/^ps/.test(system.id)
            ? "Sony"
            : ["megadrive", "mastersystem", "gamegear", "saturn", "dreamcast"].includes(system.id)
              ? "Sega"
              : [
                    "nes",
                    "snes",
                    "gb",
                    "gbc",
                    "gba",
                    "n64",
                    "nds",
                    "gamecube",
                    "wii",
                    "switch",
                  ].includes(system.id)
                ? "Nintendo"
                : "Other systems");
        (groups[group] ??= []).push(system);
      }
      content = h(
        importUi.focusable,
        { className: "wsgm-emu-column", "flow-children": "column" },
        h(
          "p",
          {},
          "New ROM libraries start with these choices. Existing libraries and title overrides keep their own selections.",
        ),
        ...Object.entries(groups).map(([name, items]) =>
          box(
            name,
            ...items.map((system) => {
              const choices =
                (state.choices ?? []).find((item) => item.systemId === system.id)?.installations ??
                [];
              const preference = manager.systemPreferences.find(
                (item) => item.systemId === system.id,
              );
              const selected = choices.find((item) => item.id === preference?.installationId);
              return h(
                importUi.focusable,
                { key: system.id, className: "wsgm-emu-default", "flow-children": "grid" },
                h("span", {}, system.name),
                renderSteamUiChoice(importUi, {
                  label: `${system.name} emulator`,
                  showLabel: false,
                  rgOptions: [
                    { data: "", label: "No default" },
                    ...choices.map((item) => ({ data: item.id, label: item.label })),
                  ],
                  selectedOption: preference?.installationId ?? "",
                  disabled: progress.busy,
                  onChange: (option) =>
                    option &&
                    void emulatorAct("setPreferredEmulator", {
                      systemId: system.id,
                      installationId: option.data,
                      coreId: choices.find((item) => item.id === option.data)?.defaultCoreId ?? "",
                    }),
                }),
                selected?.requiresCore
                  ? renderSteamUiChoice(importUi, {
                      label: `${system.name} core`,
                      showLabel: false,
                      rgOptions: selected.cores.map((item) => ({
                        data: item.id,
                        label: item.label,
                      })),
                      selectedOption: preference?.coreId || selected.defaultCoreId,
                      disabled: progress.busy,
                      onChange: (option) =>
                        option &&
                        void emulatorAct("setPreferredEmulator", {
                          systemId: system.id,
                          installationId: selected.id,
                          coreId: option.data,
                        }),
                    })
                  : h("span", { className: "steam-ui-kit-muted" }, selected ? "Standalone" : "—"),
              );
            }),
          ),
        ),
      );
    } else if (tab === "bios") {
      const selected = bios.systems.find((item) => item.id === biosId) ?? bios.systems[0];
      content = h(
        importUi.focusable,
        { className: "wsgm-emu-column", "flow-children": "column" },
        h(
          importUi.focusable,
          { className: "wsgm-emu-toolbar", "flow-children": "grid" },
          fact("BIOS folder", bios.folder),
          button("Change folder…", () => pick("Choose BIOS folder", "folder", "setBiosFolder")),
          button("Add files…", () =>
            pick("Choose local BIOS files", "file", "addBiosFiles", { systemId: "" }),
          ),
          button("Add folder…", () =>
            pick("Choose local BIOS folder", "folder", "addBiosFiles", { systemId: "" }),
          ),
          button("Verify files", () => void emulatorAct("verifyBios")),
        ),
        h(
          "p",
          { className: "steam-ui-kit-muted" },
          "EmuDeck layout · MD5 checked against the bundled retrobios list. Files without a checksum are marked Present.",
        ),
        !bios.checked ? h("p", {}, "Choose Verify files to check your BIOS folder.") : null,
        h(
          importUi.focusable,
          { className: "wsgm-emu-split", "flow-children": "grid" },
          h(
            importUi.focusable,
            { className: "wsgm-emu-column", "flow-children": "column" },
            box(
              "Systems your emulators run",
              ...bios.systems.map((system) =>
                renderSteamUiSelectRow(importUi, {
                  key: system.id,
                  title: system.name,
                  status: badge(system.status),
                  selected: selected?.id === system.id,
                  detail: system.emulators.length
                    ? "Used by " + system.emulators.join(" · ")
                    : "Emulator not installed",
                  onClick: () => setBiosId(system.id),
                }),
              ),
            ),
          ),
          selected
            ? h(
                importUi.focusable,
                { className: "wsgm-emu-column", "flow-children": "column" },
                h("h2", {}, selected.name),
                box(
                  "Files in the BIOS folder",
                  ...(selected.files ?? [])
                    .filter((file) => selected.id !== "ps2" || file.status !== "Missing")
                    .map((file) =>
                      h(
                        "div",
                        {
                          key: file.path,
                          className: "wsgm-emu-file",
                          title: file.md5 ? `MD5 ${file.md5}` : undefined,
                        },
                        h("span", {}, file.path + (file.optional ? " · Optional" : "")),
                        badge(file.status),
                      ),
                    ),
                  selected.id === "ps2" && selected.files.every((file) => file.status === "Missing")
                    ? h(
                        "p",
                        {},
                        "Add a supported PS2 BIOS dump. Regional alternatives are accepted by MD5.",
                      )
                    : null,
                ),
                box(
                  "Emulator links",
                  ...selected.links.map((link) => h("p", { key: link }, link)),
                  selected.links.length
                    ? null
                    : h("p", {}, "Use Relink emulators to apply this BIOS folder."),
                ),
                actions(
                  button("Add files…", () =>
                    pick("Choose " + selected.name + " files", "file", "addBiosFiles", {
                      systemId: selected.id,
                    }),
                  ),
                  button(
                    "Relink emulators",
                    () => void emulatorAct("relinkBios", { systemId: selected.id }),
                  ),
                ),
                ...installed
                  .filter(
                    (entry) =>
                      entry.systems.includes(selected.id) &&
                      entry.dataPolicy.prerequisites.some((rule) => rule.nativeInstaller),
                  )
                  .map((entry) =>
                    button(
                      "Install firmware for " + entry.name,
                      () => setup(entry.id, entry.name),
                      false,
                    ),
                  ),
              )
            : null,
        ),
      );
    } else {
      const entries = tab === "installed" ? installed : available;
      const visible = entries.filter((entry) =>
        matches(search, `${entry.name} ${(entry.systems ?? []).map(systemName).join(" ")}`),
      );
      content = h(
        importUi.focusable,
        { className: "wsgm-emu-column", "flow-children": "column" },
        h(
          importUi.focusable,
          { className: "wsgm-emu-toolbar", "flow-children": "grid" },
          h(
            "div",
            { className: "wsgm-emu-search" },
            h(importUi.textField, {
              label: "Search emulators or systems",
              value: search,
              onChange: (event) => setSearch(event?.target?.value ?? ""),
            }),
          ),
          h(
            "span",
            { className: "steam-ui-kit-muted" },
            `${installed.length} installed · ${(state.installed ?? []).filter((item) => item.updateAvailable).length} updates · ${installed.filter((item) => item.missingRequirements.length).length} need setup`,
          ),
        ),
        h(
          importUi.focusable,
          { className: "wsgm-emu-grid", "flow-children": "grid" },
          ...visible.map((entry) => {
            const label = labelFor(entry);
            const card = renderSteamUiCard(importUi, {
              key: entry.id,
              title: entry.name,
              stats:
                tab === "installed"
                  ? [
                      { text: entry.version },
                      { text: entry.channel },
                      {
                        text: entry.cores?.length
                          ? `${entry.cores.length} cores`
                          : entry.architecture,
                      },
                    ]
                  : [],
              badge:
                label?.updateAvailable || entry.missingRequirements?.length
                  ? { text: label?.badge ?? "Needs setup", warn: true }
                  : null,
              meta:
                tab === "installed"
                  ? [
                      label?.detail ?? entry.version,
                      label?.badge ?? (entry.managed ? "Not checked" : "External"),
                    ]
                  : [(entry.systems ?? []).map(systemName).join(" · "), "Not installed"],
              onActivate: () => {
                setRoute({
                  kind: tab === "installed" ? "installation" : "definition",
                  id: entry.id,
                });
                setChannel(entry.channels?.[0] ?? "");
              },
            });
            // The shared card owns Steam focus. Add a text mark to its empty image surface.
            return react.cloneElement(
              card,
              {},
              ...react.Children.toArray(card.props.children).map((child, index) =>
                index === 0
                  ? react.cloneElement(
                      child,
                      {},
                      h(
                        "div",
                        { className: "wsgm-emu-mark" },
                        entry.name.replace(" (all cores)", ""),
                      ),
                      ...react.Children.toArray(child.props.children),
                    )
                  : child,
              ),
            );
          }),
        ),
        visible.length
          ? null
          : h(
              "p",
              {},
              entries.length
                ? "No emulators match your search."
                : tab === "installed"
                  ? "No emulators installed. Choose Available to install or register an emulator."
                  : "All supported emulators are installed.",
            ),
        tab === "installed" && available.length
          ? h(
              "p",
              { className: "steam-ui-kit-muted" },
              "Not installed: " +
                available.map((entry) => entry.name).join(" · ") +
                ". Find them under Available.",
            )
          : null,
      );
    }
    // A route removes its old focus target. Mount the new native navigation subtree once per
    // route, so Steam enters it; ordinary state/progress updates retain the same focus owner.
    if (content.type === importUi.focusable) {
      content = react.cloneElement(content, {
        key: `${tab}:${route.kind}:${route.id}`,
        autoFocus: true,
      });
    }
    return renderSteamUiLevel(
      importUi,
      { className: "wsgm-emu-content", onBack: canBack ? back : undefined },
      h("style", {}, emulatorStyles),
      steamUiKitStyle(react),
      tabs,
      status,
      h(
        importUi.focusable,
        {
          className: "wsgm-emu-toolbar",
          "flow-children": "grid",
          onOptionsButton: () => !progress.busy && void emulatorAct("refreshEmulators"),
          onOptionsActionDescription: "Check for updates",
        },
        canBack ? button("Back", back, false) : null,
        button("Check for updates", () => void emulatorAct("refreshEmulators")),
        progress.busy ? button("Stop operation", () => void emulatorAct("cancel"), false) : null,
        progress.status ? h("span", { role: "status" }, progress.status) : null,
      ),
      content,
    );
  }
  // @fragment consumer/library-import.ts
  // Library-import frontend; shared host services own discovery, review decisions and writes.
  const LibraryImportPatchId = "wsgm.library-import";
  const EmulatorPatchId = "wsgm.emulators";
  const EmulatorProgressId = "wsgm.emulators.progress";
  const importError = (failure) => String(failure?.message ?? failure);
  const importSplitValues = (value, separator = /[\s,;]+/) =>
    value
      .split(separator)
      .map((item) => item.trim())
      .filter(Boolean);
  const importExtensions = (value) =>
    importSplitValues(value).map((item) =>
      item.startsWith(".") ? item.toLowerCase() : "." + item.toLowerCase(),
    );
  const importFact = (label, value, key) =>
    importUi.react.createElement(
      "div",
      { key, className: "wsgm-import-fact" },
      importUi.react.createElement("span", {}, label),
      importUi.react.createElement("span", {}, value || "—"),
    );
  // Resolved once the gate holds; the modals are drawn outside the page's tree and read them here.
  let importUi = null;
  let ImportCapsule = null;
  let ImportCardType = null;
  // Modals hear about new state and page messages through these; the page is their only notifier.
  const importStateStore = createLocalStore();
  const importEditorStore = createLocalStore();
  let importEditorState = null;
  let importRomSystems = [];
  let importRomState = { choices: [], systemPreferences: [] };
  const updateImportEditorState = () => {
    importEditorState = {
      loading: !!importLatest?.loading,
      romSystems: importRomSystems,
      emulators: importRomState,
    };
    importEditorStore.changed();
  };
  const receiveRomChoices = (state) => {
    importRomSystems = state?.romSystems ?? [];
    importRomState = {
      choices: state?.choices ?? [],
      systemPreferences: state?.emulators?.systemPreferences ?? [],
    };
    updateImportEditorState();
  };
  const emulatorStateStore = createLocalStore();
  const emulatorProgressStore = createLocalStore();
  let emulatorProgress = { busy: false, status: "" };
  const importReporters = new Set();
  const emulatorReporters = new Set();
  let importLatest = null;
  const importReport = (message) => {
    for (const reporter of [...importReporters]) reporter(message);
  };
  const emulatorReport = (message) => {
    for (const reporter of [...emulatorReporters]) reporter(message);
  };
  /**
   * Sends an import command and presents request rejection through the page reporters.
   * @param command Allowlisted import command.
   * @param payload JSON action arguments.
   * @returns The backend result, or undefined after reporting a rejection; clears the previous message first.
   */
  const importAct = (command, payload = {}) => {
    importReport(null);
    return request(LibraryImportPatchId, command, payload).catch((error) => {
      importReport({ text: importError(error), error: true });
      return undefined;
    });
  };
  const useToolState = (store, read) => {
    const react = importUi.react;
    react.useSyncExternalStore(store.subscribe, store.revision, store.revision);
    return read();
  };
  const useImportState = () => useToolState(importStateStore, () => importLatest);
  const useImportEditorState = () =>
    useToolState(importEditorStore, () => importEditorState ?? importLatest);
  const useEmulatorState = () => useToolState(emulatorStateStore, () => emulatorPage.state());
  const useEmulatorProgress = () => useToolState(emulatorProgressStore, () => emulatorProgress);
  const emulatorAct = (command, payload = {}) => {
    emulatorReport(null);
    return request(EmulatorPatchId, command, payload).catch((error) => {
      emulatorReport({ text: importError(error), error: true });
      return undefined;
    });
  };
  const importAssets = [
    {
      id: "grid",
      label: "Portrait capsule",
      short: "Portrait",
      card: 150,
      cell: 70,
      option: 124,
      thumb: [36, 54],
    },
    {
      id: "wide",
      label: "Wide capsule",
      short: "Wide",
      card: 300,
      cell: 224,
      option: 280,
      thumb: [60, 28],
    },
    {
      id: "hero",
      label: "Hero",
      short: "Hero",
      card: 300,
      cell: 300,
      option: 280,
      thumb: [60, 28],
    },
    {
      id: "logo",
      label: "Logo",
      short: "Logo",
      card: 220,
      cell: 160,
      option: 200,
      thumb: [60, 28],
    },
    { id: "icon", label: "Icon", short: "Icon", card: 150, cell: 64, option: 124, thumb: [36, 36] },
  ];
  const importAsset = (id) => importAssets.find((asset) => asset.id === id) ?? importAssets[0];
  const importUnavailable = (entry) => !!entry.unavailable;
  const importAvailability = (entry) => entry.availabilityLabel ?? "Not checked yet";
  // The tabs, by the group the host puts each title in. Membership is the host's, so the overlay and
  // this page cannot disagree about what "needs attention" means.
  const importTabs = [
    { id: "all", title: "All", group: "" },
    { id: "new", title: "New", group: "new" },
    { id: "imported", title: "Imported", group: "imported" },
    { id: "attention", title: "Needs attention", group: "attention" },
    { id: "excluded", title: "Left out", group: "excluded" },
  ];
  // Badge colours by action. The words are the host's; only the colours are this page's.
  const importBadgeTones = {
    Add: { tone: "#1a9fff", text: "#ffffff" },
    Adopt: { tone: "#1a9fff", text: "#ffffff" },
    Update: { tone: "#d9a441", text: "#1a1206" },
    Artwork: { tone: "#d9a441", text: "#1a1206" },
    Remove: { tone: "#c2463e", text: "#ffffff" },
    Conflict: { tone: "#c2463e", text: "#ffffff" },
    Skip: { tone: "#3d4450", text: "#dcdedf" },
  };
  const importExcludedTone = { tone: "rgba(14,20,27,0.85)", text: "#b8bcbf" };
  // The page's glyphs, as path data Steam's own convention draws: filled with the text colour, holes
  // cut with the even-odd rule, sized by the page's CSS.
  const importGlyphs = {
    check: "M9.6 15.6 5.4 11.4 4 12.8l5.6 5.6L20 8l-1.4-1.4z",
    controller:
      "M7.5 7h9A5.5 5.5 0 0 1 22 12.5v1.9a3.1 3.1 0 0 1-5.5 2L14.8 14.5H9.2L7.5 16.4A3.1 3.1 0 0 1 2 14.4v-1.9A5.5 5.5 0 0 1 7.5 7zM6.3 9.8v1.5H4.8v1.6h1.5v1.5h1.6v-1.5h1.5v-1.6H7.9V9.8zM15.5 10.2h1.6v1.6h-1.6zM17.6 12.3h1.6v1.6h-1.6z",
    overlay:
      "M3 4h18a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1h-7v2h3v2H7v-2h3v-2H3a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1zm1 5v7h16V9z",
    launcher: "M4 11h11.2l-4.6-4.6L12 5l7 7-7 7-1.4-1.4 4.6-4.6H4z",
    chevron: "M9 5.6 10.4 4.2l7.8 7.8-7.8 7.8L9 18.4l6.4-6.4z",
  };
  const importGlyph = (react, name) => renderSteamGlyph(react, importGlyphs[name]);
  // The page's own layout over Steam's components. Steam's stable class names (DialogCheckbox,
  // DialogLabel, DialogInput, DialogButton) are what the compacting rules below hang on; the hashed
  // ones are never named.
  const importStyles = `
#wsgm-import { margin-top: var(--basicui-header-height, 40px); height: calc(100% - var(--basicui-header-height, 40px));
  display: flex; flex-direction: column; background: var(--gpSystemDarkestGrey, #0e141b); color: #dcdedf; }
#wsgm-import .wsgm-import-head { display: flex; align-items: baseline; gap: 14px; padding: 24px 48px 0; flex-shrink: 0; }
#wsgm-import .wsgm-import-head h1 { margin: 0; font-size: 30px; font-weight: 700; color: #fff; letter-spacing: 0.2px; }
#wsgm-import .wsgm-import-crumb { font-size: 30px; font-weight: 700; color: #8b929a; }
#wsgm-import .wsgm-import-crumb svg { width: 18px; height: 18px; margin: 0 -2px 2px 0; vertical-align: middle; }
#wsgm-import .wsgm-import-muted { color: #8b929a; font-size: 14px; }
#wsgm-import .wsgm-import-body { flex: 1; min-height: 0; display: flex; gap: 32px; padding: 16px 48px 0; }
#wsgm-import .wsgm-import-sidebar { width: 260px; flex: 0 0 auto; overflow-y: auto; min-height: 0;
  padding-bottom: 72px; display: flex; flex-direction: column; gap: 2px; }
#wsgm-import .wsgm-import-eyebrow { font-size: 13px; font-weight: 700; letter-spacing: 1.6px; text-transform: uppercase;
  color: #dcdedf; padding: 0 10px 8px; }
#wsgm-import .wsgm-import-sidebar .wsgm-import-custom { padding-top: 16px; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container { display: flex; flex-direction: row; flex-wrap: nowrap;
  align-items: center; gap: 12px; min-height: 38px; margin: 0; padding: 0 10px; border-radius: 2px; box-sizing: border-box; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container > .DialogCheckbox { flex: 0 0 auto; margin: 0; }
#wsgm-import .wsgm-import-source .DialogCheckbox_Container > div:empty { display: none; }
#wsgm-import .wsgm-import-source .DialogToggle_Label { flex: 1; min-width: 0; font-size: 15px; line-height: 1.2;
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis; margin: 0; padding: 0; }
#wsgm-import .wsgm-import-source .DialogToggle_Description { flex: 0 0 auto; margin: 0; padding: 0;
  font-size: 13px; color: #8b929a; white-space: nowrap; }
#wsgm-import .wsgm-import-source[data-missing="true"] .DialogCheckbox_Container { opacity: 0.6; }
#wsgm-import .wsgm-import-sidebar > .DialogButton { margin: 8px 0 0; width: auto; min-width: auto; height: 36px; }
#wsgm-import .wsgm-import-main { flex: 1; min-width: 0; min-height: 0; display: flex; flex-direction: column; }
#wsgm-import .wsgm-import-pane { display: flex; flex-direction: column; gap: 14px; padding: 12px 4px 72px; }
#wsgm-import .wsgm-import-bar { display: flex; align-items: center; gap: 10px; flex-wrap: nowrap; }
#wsgm-import .wsgm-import-bar .DialogButton { width: auto; min-width: auto; height: 40px; padding: 0 16px;
  white-space: nowrap; box-sizing: border-box; }
#wsgm-import .wsgm-import-spacer { flex: 1; }
#wsgm-import .wsgm-import-tool { flex: 0 0 auto; }
#wsgm-import .wsgm-import-tool .DialogButton { width: 100%; justify-content: space-between; }
#wsgm-import .wsgm-import-tool .DialogDropDown_CurrentDisplay { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-search { flex: 0 0 auto; width: 150px; }
#wsgm-import .wsgm-import-search .DialogLabel { display: none; }
#wsgm-import .wsgm-import-search .DialogInputLabelGroup, #wsgm-import .wsgm-import-search .DialogInput_Wrapper { margin: 0; }
#wsgm-import .wsgm-import-search .DialogInput { width: 100%; height: 40px; box-sizing: border-box; padding: 0 12px; }
#wsgm-import .wsgm-import-status { display: flex; flex-direction: column; gap: 4px; font-size: 14px; color: #b8bcbf; }
#wsgm-import .wsgm-import-status:empty { display: none; }
#wsgm-import .wsgm-import-status .wsgm-import-error { color: #ff6d6d; }
#wsgm-import .wsgm-import-group { display: flex; flex-direction: column; gap: 14px; margin-bottom: 8px; }
#wsgm-import .wsgm-import-grouphead { display: flex; align-items: baseline; gap: 14px; }
#wsgm-import .wsgm-import-grouphead .wsgm-import-eyebrow { padding: 0; }
#wsgm-import .wsgm-import-grid { display: flex; flex-wrap: wrap; gap: 22px 14px; padding: 4px; }
#wsgm-import .wsgm-import-card { display: flex; flex-direction: column; gap: 8px; }
#wsgm-import .wsgm-import-card[data-unavailable="true"] img { filter: grayscale(1); }
#wsgm-import .wsgm-import-name { display: flex; align-items: center; gap: 6px; font-size: 13px; color: #b8bcbf; }
#wsgm-import .wsgm-import-name svg { flex: 0 0 auto; width: 16px; height: 16px; }
#wsgm-import .wsgm-import-name span { min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-launch { font-size: 12px; color: #8b929a; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-badge { position: absolute; top: 8px; right: 8px; padding: 3px 8px; border-radius: 2px;
  font-size: 11px; font-weight: 700; letter-spacing: 0.4px; text-transform: uppercase; pointer-events: none; }
#wsgm-import .wsgm-import-check { position: absolute; top: 8px; left: 8px; width: 24px; height: 24px; border-radius: 50%;
  background: #1a9fff; color: #ffffff; display: flex; align-items: center; justify-content: center; pointer-events: none;
  box-shadow: 0 1px 4px rgba(0,0,0,0.5); }
#wsgm-import .wsgm-import-check svg { width: 14px; height: 14px; }
#wsgm-import .wsgm-import-rows { display: flex; flex-direction: column; gap: 10px; }
#wsgm-import .wsgm-import-row { display: flex; align-items: center; gap: 14px; padding: 6px 0; }
#wsgm-import .wsgm-import-rowname { width: 170px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 4px; }
#wsgm-import .wsgm-import-rowname span:first-child { font-size: 15px; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-import .wsgm-import-colhead { display: flex; gap: 14px; align-items: flex-end; padding: 6px 0 8px; border-bottom: 1px solid #23262e; }
#wsgm-import .wsgm-import-colhead > * { flex: 0 0 auto; }
#wsgm-import .wsgm-import-col { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
#wsgm-import .wsgm-import-col .wsgm-import-eyebrow { padding: 0; font-size: 12px; }
#wsgm-import .wsgm-import-col .DialogButton { width: auto; min-width: auto; height: 24px; padding: 0 8px; font-size: 11px; }
#wsgm-import .wsgm-import-split { display: flex; gap: 36px; flex: 1; min-height: 0; }
#wsgm-import .wsgm-import-side { width: 260px; flex: 0 0 auto; display: flex; flex-direction: column; gap: 12px; }
#wsgm-import .wsgm-import-side .DialogButton { width: auto; min-width: auto; height: 40px; }
#wsgm-import .wsgm-import-side .DialogLabel { display: none; }
#wsgm-import .wsgm-import-side .DialogInputLabelGroup, #wsgm-import .wsgm-import-side .DialogInput_Wrapper { margin: 0; }
#wsgm-import .wsgm-import-side .DialogInput { width: 100%; height: 40px; box-sizing: border-box; padding: 0 12px; }
#wsgm-import .wsgm-import-chosen { display: flex; align-items: center; gap: 12px; padding: 8px; border-radius: 2px; }
#wsgm-import .wsgm-import-chosen[data-on="true"] { background: #23262e; }
#wsgm-import .wsgm-import-chosen img, #wsgm-import .wsgm-import-chosen .wsgm-import-thumb { flex: 0 0 auto; border-radius: 2px;
  object-fit: cover; background: rgba(255,255,255,0.06); }
#wsgm-import .wsgm-import-chosen div { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
#wsgm-import .wsgm-import-chosen div span:first-child { font-size: 14px; color: #fff; }
#wsgm-import .wsgm-import-chosen div span:last-child { font-size: 12px; color: #8b929a; }
#wsgm-import .wsgm-import-rule { height: 1px; background: #23262e; margin: 6px 0; }
#wsgm-import .wsgm-import-matches { display: flex; flex-direction: column; gap: 4px; }
#wsgm-import .wsgm-import-matches .wsgm-import-eyebrow { padding: 8px 0 2px; }
.steam-ui-kit-sheet .wsgm-import-sheethead { display: flex; align-items: center; gap: 16px; margin-bottom: 8px; }
.steam-ui-kit-sheet .wsgm-import-sheethead img, .steam-ui-kit-sheet .wsgm-import-sheethead .wsgm-import-thumb { width: 60px; height: 90px;
  border-radius: 2px; object-fit: cover; background: rgba(255,255,255,0.06); flex: 0 0 auto; }
.steam-ui-kit-sheet .wsgm-import-sheethead div { display: flex; flex-direction: column; gap: 6px; }
.steam-ui-kit-sheet .wsgm-import-sheethead span:last-child { font-size: 14px; color: #8b929a; }
.steam-ui-kit-sheet .wsgm-import-fact { display: flex; align-items: flex-start; justify-content: space-between; gap: 24px;
  padding: 12px 0; border-top: 1px solid #2f343c; }
.steam-ui-kit-sheet .wsgm-import-fact > span:first-child { font-size: 15px; color: #dcdedf; flex-shrink: 0; }
.steam-ui-kit-sheet .wsgm-import-fact > span:last-child { font-size: 14px; color: #8b929a; text-align: right; line-height: 1.4;
  max-width: 420px; word-break: break-word; }
.steam-ui-kit-sheet .wsgm-import-muted { font-size: 13px; color: #8b929a; }
.steam-ui-kit-sheet .wsgm-import-spacer { flex: 1; }
.steam-ui-kit-sheet .wsgm-import-path { max-width: 280px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.wsgm-import-risk p { margin: 0 0 12px; line-height: 1.45; }
`;
  // A card's caption: which image of how many, and from whom.
  const importSlotCaption = (slot) => {
    if (!slot) return null;
    switch (slot.kind) {
      case "keep":
        return "Keeps current";
      case "none":
        return slot.count ? `None · ${slot.count} found` : "No image";
      case "loading":
        return "Finding images…";
      default:
        // A pick that is no longer among the candidates has no position to show.
        return slot.index > 0 ? `${slot.provider} ${slot.index}/${slot.count}` : slot.provider;
    }
  };
  // Why a title has no images, when the host said: a provider that failed or none that is set up.
  const importArtworkLine = (entry) =>
    entry.artworkStatus === "failed" || entry.artworkStatus === "unavailable"
      ? entry.artworkDetail || "The artwork providers could not be asked."
      : entry.artworkStatus === "notFound"
        ? "No provider had images for this. Fix the match to search for the right game."
        : "";
  // The triggers cycle a slot's image in place, the way SRM's arrows do.
  const importCycle = (id, asset) =>
    onSteamTriggers((delta) => void importAct("cycleArtwork", { id, asset, delta }));
  // The ban-risk acknowledgement. Deliberately a modal with its own checkbox rather than a switch on
  // the card: the user is accepting a risk to their account, and that should not be one press away
  // from a grid they are moving through.
  function ImportRiskBody({ entry, close }) {
    const react = importUi.react;
    const h = react.createElement;
    const [checked, setChecked] = react.useState(false);
    return renderSteamUiSheet(
      importUi,
      {
        className: "wsgm-import-risk",
        note: `${entry.name} is marked as a multiplayer title. The Steam overlay route loads Steam's overlay into the running game. Anti-cheat compatibility has not been established for any title, and some anti-cheat systems treat that as tampering. Controller-only support injects nothing and is the safer choice for a multiplayer game.`,
        actions: [
          { id: "cancel", label: "Keep controller only", onClick: close },
          {
            id: "save",
            label: "Use the Steam overlay",
            primary: true,
            disabled: !checked,
            onClick: () => {
              if (!checked) return;
              void importAct("setMode", {
                id: entry.id,
                mode: "SteamIntegration",
                acknowledged: true,
              });
              close();
            },
          },
        ],
      },
      h(importUi.toggleField, {
        label: "I understand this may risk a ban on this title",
        checked,
        controlled: true,
        onChange: (value) => setChecked(!!value),
      }),
    );
  }
  const showImportRisk = (entry) =>
    showSteamModal(importUi, {
      title: entry.name,
      render: (close) => importUi.react.createElement(ImportRiskBody, { entry, close }),
    });
  const cycleImportLaunch = (entry) =>
    void importAct("cycleLaunch", { id: entry.id }).then((answer) => {
      if (answer?.acknowledge) showImportRisk(entry);
    });
  function ImportDetailsBody({ id, close, openTitle }) {
    const react = importUi.react;
    const h = react.createElement;
    const state = useImportState();
    const [details, setDetails] = react.useState(null);
    react.useEffect(() => {
      let current = true;
      void importAct("details", { id }).then((answer) => current && answer && setDetails(answer));
      return () => {
        current = false;
      };
    }, [id]);
    const entry = (state?.entries ?? []).find((candidate) => candidate.id === id);
    if (!entry) {
      return renderSteamUiSheet(importUi, {
        note: "This title is no longer listed.",
        actions: [{ id: "close", label: "Close", onClick: close }],
      });
    }
    const launchControl = !entry.editable
      ? null
      : entry.packaged
        ? h(importUi.dropdown, {
            label: "Launch mode",
            description: entry.requiresAcknowledgement
              ? "Multiplayer title. The Steam overlay route loads Steam into the game and needs you to accept " +
                "the ban risk first."
              : details?.launchEvidence,
            rgOptions: [
              { data: "ControllerOnly", label: "Controller only" },
              ...(entry.canUseSteamIntegration
                ? [{ data: "SteamIntegration", label: "Steam overlay" }]
                : []),
            ],
            selectedOption: entry.mode,
            onChange: (option) => {
              const mode = option?.data;
              if (!mode || mode === entry.mode) return;
              if (
                mode === "SteamIntegration" &&
                entry.requiresAcknowledgement &&
                !entry.acknowledged
              ) {
                showImportRisk(entry);
                return;
              }
              void importAct("setMode", { id: entry.id, mode, acknowledged: entry.acknowledged });
            },
          })
        : entry.routes.length > 1
          ? h(importUi.dropdown, {
              label: "Launch route",
              description: details?.launchEvidence,
              rgOptions: entry.routes.map((route) => ({ data: route.id, label: route.label })),
              selectedOption: entry.route,
              onChange: (option) => {
                if (option?.data && option.data !== entry.route) {
                  void importAct("setRoute", { id: entry.id, route: option.data });
                }
              },
            })
          : null;
    const artwork = (entry.artwork ?? [])
      .map((slot) => `${importAsset(slot.asset).short}: ${importSlotCaption(slot) ?? "none"}`)
      .join(" · ");
    const facts = [
      [
        "Launch route",
        details ? `${entry.launchLabel}. ${details.launchEvidence}` : entry.launchLabel,
      ],
      ["Multiplayer", details ? `${details.multiplayer}. ${details.multiplayerEvidence}` : "…"],
      ["Saving would", `${entry.actionLabel}: ${entry.reason}`],
      ["Artwork", artwork || importArtworkLine(entry) || "—"],
      [
        "Matched to",
        entry.matchName ? `${entry.matchName}${entry.matchFixed ? " (fixed)" : ""}` : "—",
      ],
      ["Installed at", details?.installPath || "unknown"],
      ...(entry.managedId
        ? [
            ["Location", entry.location || entry.contentPath],
            ["Availability", importAvailability(entry)],
          ]
        : []),
      ["Identity", details?.identity ?? "…"],
      ...(details?.notes ?? []).map((note) => ["Note", note]),
    ];
    const grid = (entry.artwork ?? []).find((slot) => slot.asset === "grid");
    const actions = [
      entry.managedId
        ? {
            label: "Recheck availability",
            run: () => void importAct("recheckAvailability", { id: entry.id }),
          }
        : null,
      entry.systemId
        ? {
            label: "Rename title…",
            run: () =>
              showSteamUiPrompt(importUi, {
                title: "Title",
                label: "Title",
                initial: entry.name,
                confirmLabel: "Save title",
                onConfirm: (name) => void importAct("setRomTitle", { id: entry.id, name }),
              }),
          }
        : null,
      entry.systemId
        ? {
            label: "Use library or system default",
            run: () =>
              void importAct("setRomEmulator", { id: entry.id, installationId: "", coreId: "" }),
          }
        : null,
      entry.systemId
        ? {
            label: "Launch arguments…",
            run: () =>
              showSteamModal(importUi, {
                title: "Launch arguments",
                render: (done) =>
                  h(ImportArgumentsBody, {
                    tokens: entry.arguments ?? [],
                    onSave: (values) =>
                      request(LibraryImportPatchId, "setRomArguments", {
                        id: entry.id,
                        arguments: values,
                      }),
                    close: done,
                  }),
              }),
          }
        : null,
      entry.managedId && entry.appId > 0 && entry.action !== "Conflict"
        ? entry.action === "Remove"
          ? {
              label: "Keep this shortcut",
              run: () => void importAct("setCleanup", { id: entry.id, enabled: false }),
            }
          : {
              label: "Stage shortcut removal…",
              run: () =>
                showSteamUiConfirm(importUi, {
                  title: "Stage shortcut removal?",
                  text: "The next apply will remove this shortcut from Steam. ROMs and saves are preserved.",
                  confirmLabel: "Stage removal",
                  onConfirm: () => {
                    close();
                    void importAct("setCleanup", { id: entry.id, enabled: true });
                  },
                }),
            }
        : null,
      entry.editable
        ? { label: "Choose artwork…", run: () => (close(), openTitle(entry, "grid")) }
        : null,
      entry.appId > 0 && entry.action !== "Remove"
        ? {
            label: "Open Steam's artwork page…",
            run: () => {
              close();
              void importAct("openArtwork", { id: entry.id }).then((answer) => {
                if (answer?.route) navigateSteamRoute(answer.route);
              });
            },
          }
        : null,
      entry.excluded
        ? { label: "Offer again", run: () => void importAct("include", { id: entry.id }) }
        : entry.action === "Add" || entry.action === "Adopt"
          ? {
              label: "Don't import",
              run: () => (void importAct("exclude", { id: entry.id }), close()),
            }
          : null,
    ].filter((action) => action !== null);
    return renderSteamUiSheet(
      importUi,
      {
        actions: [
          ...actions.map((action) => ({
            id: action.label,
            label: action.label,
            onClick: action.run,
          })),
          { id: "close", label: "Close", onClick: close },
        ],
      },
      h(
        "div",
        { className: "wsgm-import-sheethead" },
        grid?.thumb
          ? h("img", { src: grid.thumb, alt: "", draggable: false })
          : h("span", { className: "wsgm-import-thumb" }),
        h(
          "div",
          {},
          h("h2", { style: { margin: 0 } }, entry.name),
          h(
            "span",
            {},
            `${entry.source} · ${entry.actionLabel} · ${entry.appId > 0 ? "in Steam" : "not in Steam yet"}`,
          ),
        ),
      ),
      launchControl,
      entry.systemId && entry.editable
        ? h(ImportRomLaunchControls, {
            state,
            systemId: entry.systemId,
            installationId: entry.emulatorInstallationId,
            coreId: entry.coreId,
            choose: (installationId, coreId) =>
              void importAct("setRomEmulator", { id: entry.id, installationId, coreId }),
          })
        : null,
      ...facts.map(([term, value], index) => importFact(term, value, `${term}${index}`)),
    );
  }
  // The mockup's "Add a shortcuts folder" sheet: the folder, whether its subfolders are read too,
  // and which file types it offers.
  function ImportFolderBody({ close }) {
    const react = importUi.react;
    const h = react.createElement;
    const [path, setPath] = react.useState("");
    const [subfolders, setSubfolders] = react.useState(true);
    const [types, setTypes] = react.useState(".lnk .url .exe");
    const [error, setError] = react.useState("");
    const choose = () =>
      void showSteamFilePicker(importUi, {
        title: "Choose the shortcuts folder",
        mode: "folder",
      }).then((chosen) => chosen && setPath(chosen));
    const extensions = importExtensions(types);
    const add = () => {
      setError("");
      void request(LibraryImportPatchId, "addFolder", {
        path,
        includeSubfolders: subfolders,
        extensions,
      }).then(
        () => close(),
        (failure) => setError(importError(failure)),
      );
    };
    return renderSteamUiSheet(
      importUi,
      {
        note: "Every shortcut in the folder becomes a title. The folder appears in the sidebar and is scanned with the launchers.",
        error,
        actions: [
          { id: "cancel", label: "Cancel", onClick: close },
          {
            id: "save",
            label: "Add source",
            primary: true,
            disabled: !path || extensions.length === 0,
            onClick: add,
          },
        ],
      },
      h(
        importUi.focusable,
        { className: "wsgm-import-fact", "flow-children": "row" },
        h(
          "div",
          { style: { display: "flex", flexDirection: "column", gap: "4px" } },
          h("span", {}, "Folder"),
          h("span", { className: "wsgm-import-muted" }, "Pick a drive and folder."),
        ),
        h(
          importUi.dialogButton,
          { onClick: choose, style: { width: "280px" } },
          h("span", { className: "wsgm-import-path" }, path || "Choose…"),
        ),
      ),
      h(importUi.toggleField, {
        label: "Include subfolders",
        description: "Scan folders inside it too.",
        checked: subfolders,
        onChange: (value) => setSubfolders(value),
      }),
      importUi.textField
        ? h(importUi.textField, {
            label: "File types",
            description: "Separated by spaces.",
            value: types,
            onChange: (event) => setTypes(event?.target?.value ?? ""),
          })
        : null,
    );
  }
  const importSystemChoices = (state, systemId) =>
    (state?.choices ?? importRomState.choices).find((item) => item.systemId === systemId)
      ?.installations ?? [];
  function ImportArgumentsBody({ tokens, onSave, close }) {
    const react = importUi.react;
    const [values, setValues] = react.useState([...(tokens ?? [])]);
    const [error, setError] = react.useState("");
    const save = () =>
      Promise.resolve()
        .then(() => onSave(values.filter((value) => value.trim().length > 0)))
        .then(close, (failure) => setError(importError(failure)));
    return renderSteamUiSheet(
      importUi,
      {
        note: "Each row is one argument, including spaces inside it. Use {rom} for the game path. RetroArch also supports {core}, {data} and {config}. Leave the list empty to use the library or emulator default.",
        error,
        actions: [
          { id: "cancel", label: "Cancel", onClick: close },
          { id: "save", label: "Save arguments", primary: true, onClick: () => void save() },
        ],
      },
      renderSteamUiStringList(importUi, {
        values,
        onChange: setValues,
        label: (index) => `Argument ${index + 1}`,
        addLabel: "Add argument",
        removeLabel: (index) => `Remove argument ${index + 1}`,
        resetLabel: "Use default arguments",
      }),
    );
  }
  function ImportRomLaunchControls({ state, systemId, installationId, coreId, choose }) {
    const h = importUi.react.createElement;
    useImportEditorState();
    const installations = importSystemChoices(state, systemId);
    const selected = installations.find((installation) => installation.id === installationId);
    const options = [
      { data: "", label: "Choose an installed emulator" },
      ...installations.map((installation) => ({
        data: installation.id,
        label: installation.label,
      })),
      ...(installationId && !selected
        ? [{ data: installationId, label: "Selected emulator is unavailable" }]
        : []),
    ];
    const cores = selected?.cores ?? [];
    return h(
      importUi.react.Fragment,
      {},
      h(importUi.dropdown, {
        label: "Emulator",
        rgOptions: options,
        selectedOption: installationId,
        onChange: (option) => {
          if (!option) return;
          const installation = installations.find((item) => item.id === option.data);
          choose(option.data, installation?.defaultCoreId ?? "");
        },
      }),
      selected?.requiresCore
        ? h(importUi.dropdown, {
            label: "Core",
            rgOptions: [
              { data: "", label: "Choose a core" },
              ...cores.map((core) => ({ data: core.id, label: core.label })),
              ...(coreId && !cores.some((core) => core.id === coreId)
                ? [{ data: coreId, label: "Selected core is unavailable" }]
                : []),
            ],
            selectedOption: coreId,
            onChange: (option) => option && choose(installationId, option.data),
          })
        : null,
    );
  }
  function ImportRomSourceBody({ sourceId, singleFile = false, close }) {
    const react = importUi.react;
    const h = react.createElement;
    const state = useImportEditorState();
    const [draft, setDraft] = react.useState(() => {
      const manual = singleFile
        ? (importLatest?.manualSources ?? []).find((item) => item.id === sourceId)
        : null;
      const source = (importLatest?.romSources ?? []).find((item) => item.id === sourceId);
      const system =
        state?.romSystems?.find((item) => item.id === manual?.systemId) ?? state?.romSystems?.[0];
      const preferred = importRomState.systemPreferences.find(
        (item) => item.systemId === system?.id,
      );
      if (manual?.romPath)
        return {
          id: manual.id,
          name: manual.name,
          root: { ...manual.romPath, directory: false },
          systemId: manual.systemId,
          emulatorInstallationId: manual.emulatorInstallationId,
          coreId: manual.coreId,
          includeSubfolders: false,
          titleCleanup: true,
          extensions: [],
          exclusions: [],
          arguments: [...(manual.romArguments ?? [])],
        };
      if (source)
        return {
          ...source,
          root: { ...source.root },
          extensions: [...source.extensions],
          exclusions: [...source.exclusions],
          arguments: [...source.arguments],
        };
      return {
        id: "",
        name: singleFile ? "ROM" : "ROM library",
        root: { absolutePath: "", volumeId: "", relativePath: "", directory: !singleFile },
        systemId: system?.id ?? "",
        emulatorInstallationId: preferred?.installationId ?? "",
        coreId: preferred?.coreId ?? "",
        includeSubfolders: !singleFile,
        titleCleanup: true,
        extensions: system?.extensions ?? [],
        exclusions: [],
        arguments: [],
      };
    });
    const [error, setError] = react.useState("");
    const change = (fields) => setDraft((current) => ({ ...current, ...fields }));
    const systems = state?.romSystems ?? [];
    const field = (label, value, onChange) =>
      h(importUi.textField, {
        label,
        value,
        onChange: (event) => onChange(event?.target?.value ?? ""),
      });
    const choosePath = () =>
      void showSteamFilePicker(importUi, {
        title: singleFile ? "Choose a ROM" : "Choose the ROM library",
        mode: singleFile ? "file" : "folder",
      }).then(
        (chosen) =>
          chosen &&
          change({
            root: { absolutePath: chosen, volumeId: "", relativePath: "", directory: !singleFile },
          }),
      );
    const save = () => {
      setError("");
      void request(LibraryImportPatchId, "addRomSource", { source: draft }).then(close, (failure) =>
        setError(importError(failure)),
      );
    };
    const remove = () =>
      showSteamUiConfirm(importUi, {
        title: singleFile ? "Remove this ROM source?" : "Remove this ROM library?",
        text: "Stops scanning this source. ROMs and imported shortcuts stay.",
        confirmLabel: "Remove source",
        onConfirm: () =>
          void request(
            LibraryImportPatchId,
            singleFile ? "removeManualSource" : "removeRomSource",
            {
              id: draft.id,
            },
          ).then(close, (failure) => setError(importError(failure))),
      });
    const actions = [
      ...(draft.id
        ? [{ id: "remove", label: "Remove source…", disabled: state?.loading, onClick: remove }]
        : []),
      { id: "cancel", label: "Cancel", onClick: close },
      {
        id: "save",
        label: "Save and preview",
        primary: true,
        disabled:
          state?.loading ||
          !draft.root.absolutePath ||
          !draft.systemId ||
          !draft.emulatorInstallationId ||
          (importSystemChoices(state, draft.systemId).find(
            (item) => item.id === draft.emulatorInstallationId,
          )?.requiresCore &&
            !draft.coreId),
        onClick: save,
      },
    ];
    return renderSteamUiSheet(
      importUi,
      {
        note: "Review the titles before saving to Steam. ROM files stay in their current location.",
        error,
        actions,
      },
      field(singleFile ? "Title" : "Library name", draft.name, (name) => change({ name })),
      h(
        importUi.dialogButton,
        { onClick: choosePath },
        draft.root.absolutePath || (singleFile ? "Choose ROM…" : "Choose folder…"),
      ),
      h(importUi.dropdown, {
        label: "System",
        rgOptions: systems.map((system) => ({ data: system.id, label: system.name })),
        selectedOption: draft.systemId,
        onChange: (option) => {
          const system = systems.find((item) => item.id === option?.data);
          const preferred = importRomState.systemPreferences.find(
            (item) => item.systemId === system?.id,
          );
          if (system)
            change({
              systemId: system.id,
              extensions: system.extensions,
              emulatorInstallationId: preferred?.installationId ?? "",
              coreId: preferred?.coreId ?? "",
            });
        },
      }),
      h(ImportRomLaunchControls, {
        state,
        systemId: draft.systemId,
        installationId: draft.emulatorInstallationId,
        coreId: draft.coreId,
        choose: (emulatorInstallationId, coreId) => change({ emulatorInstallationId, coreId }),
      }),
      ...(!singleFile
        ? [
            h(importUi.toggleField, {
              label: "Include subfolders",
              checked: draft.includeSubfolders,
              onChange: (value) => change({ includeSubfolders: value }),
            }),
            h(importUi.toggleField, {
              label: "Clean title names",
              checked: draft.titleCleanup,
              onChange: (value) => change({ titleCleanup: value }),
            }),
            field("File extensions, separated by spaces", draft.extensions.join(" "), (value) =>
              change({ extensions: importExtensions(value) }),
            ),
            field("Excluded paths, separated by semicolons", draft.exclusions.join("; "), (value) =>
              change({ exclusions: importSplitValues(value, /;/) }),
            ),
          ]
        : []),
      h(
        importUi.dialogButton,
        {
          onClick: () =>
            showSteamModal(importUi, {
              title: "Launch arguments",
              render: (done) =>
                h(ImportArgumentsBody, {
                  tokens: draft.arguments,
                  onSave: (args) => change({ arguments: args }),
                  close: done,
                }),
            }),
        },
        draft.arguments.length
          ? `Launch arguments (${draft.arguments.length})…`
          : "Launch arguments: emulator default…",
      ),
    );
  }
  function ImportManualSourceBody({ sourceId, close }) {
    const react = importUi.react;
    const h = react.createElement;
    const [draft, setDraft] = react.useState(() => {
      const source = (importLatest?.manualSources ?? []).find((item) => item.id === sourceId);
      return {
        id: source?.id ?? "",
        name: source?.name ?? "",
        target: source?.target ?? "",
        arguments: source?.arguments ?? "",
        workingDirectory: source?.workingDirectory ?? "",
        contentPath: source?.contentPath ?? "",
        location: source?.location ?? "",
        libraryBacked: source?.libraryBacked ?? true,
      };
    });
    const [error, setError] = react.useState("");
    const change = (fields) => setDraft((current) => ({ ...current, ...fields }));
    const field = (label, key) =>
      h(importUi.textField, {
        label,
        value: draft[key],
        onChange: (event) => change({ [key]: event?.target?.value ?? "" }),
      });
    const pick = (key, title, mode) =>
      void showSteamFilePicker(importUi, { title, mode }).then(
        (chosen) => chosen && change({ [key]: chosen }),
      );
    const save = () =>
      void request(LibraryImportPatchId, "addManualSource", { source: draft }).then(
        () => {
          close();
        },
        (failure) => setError(importError(failure)),
      );
    const remove = () =>
      showSteamUiConfirm(importUi, {
        title: "Remove this manual source?",
        text: "Stops scanning this source. The imported shortcut and backing files stay.",
        confirmLabel: "Remove source",
        onConfirm: () =>
          void request(LibraryImportPatchId, "removeManualSource", { id: draft.id }).then(
            close,
            (failure) => setError(importError(failure)),
          ),
      });
    return renderSteamUiSheet(
      importUi,
      {
        note: "Preview this command before saving it to Steam.",
        error,
        actions: [
          ...(draft.id ? [{ id: "remove", label: "Remove source…", onClick: remove }] : []),
          { id: "cancel", label: "Cancel", onClick: close },
          {
            id: "save",
            label: "Add and preview",
            primary: true,
            disabled: !draft.name || !draft.target,
            onClick: save,
          },
        ],
      },
      field("Title", "name"),
      h(
        importUi.dialogButton,
        { onClick: () => pick("target", "Choose executable", "file") },
        draft.target || "Choose executable…",
      ),
      field("Arguments", "arguments"),
      h(
        importUi.dialogButton,
        { onClick: () => pick("workingDirectory", "Choose working folder", "folder") },
        draft.workingDirectory || "Choose working folder…",
      ),
      h(
        importUi.dialogButton,
        { onClick: () => pick("contentPath", "Choose backing game file", "file") },
        draft.contentPath || "Choose backing game file…",
      ),
      field("Location name", "location"),
      h(importUi.toggleField, {
        label: "Track backing storage",
        checked: draft.libraryBacked,
        onChange: (value) => change({ libraryBacked: value }),
      }),
    );
  }
  function ImportEmulatorSetupBody({ installationId, close }) {
    const react = importUi.react;
    const h = react.createElement;
    const state = useEmulatorState();
    const progress = useEmulatorProgress();
    const [error, setError] = react.useState("");
    const installation = (state?.emulators?.installations ?? []).find(
      (item) => item.id === installationId,
    );
    const definition = (state?.emulators?.definitions ?? []).find(
      (item) => item.id === installation?.definitionId,
    );
    const choose = (prerequisite, mode) =>
      void showSteamFilePicker(importUi, {
        title: `Choose ${prerequisite.name}`,
        mode,
        extensions: prerequisite.extensions,
      }).then(
        (path) =>
          path &&
          void request(EmulatorPatchId, "configureEmulatorPrerequisite", {
            installationId,
            path,
            kind: prerequisite.kind,
          }).then(
            () => setError(""),
            (failure) => setError(importError(failure)),
          ),
      );
    return renderSteamUiSheet(
      importUi,
      {
        note: "Choose BIOS, firmware or system files you already have.",
        error,
        actions: [
          {
            id: "recheck",
            label: "Recheck setup",
            disabled: progress.busy,
            onClick: () => void emulatorAct("refreshEmulators"),
          },
          ...(progress.busy
            ? [{ id: "stop", label: "Stop operation", onClick: () => void emulatorAct("cancel") }]
            : []),
          { id: "close", label: "Close", onClick: close },
        ],
      },
      progress.status ? h("p", { role: "status" }, progress.status) : null,
      ...(definition?.prerequisites ?? []).map((prerequisite) =>
        h(
          importUi.focusable,
          { key: prerequisite.kind, className: "wsgm-import-pane", "flow-children": "column" },
          h("h3", {}, prerequisite.name),
          h("p", {}, prerequisite.description),
          installation?.configuredPrerequisites?.[prerequisite.kind]
            ? h("p", {}, "Configured: " + installation.configuredPrerequisites[prerequisite.kind])
            : null,
          h(
            importUi.dialogButton,
            { disabled: progress.busy, onClick: () => choose(prerequisite, "file") },
            "Choose file…",
          ),
          prerequisite.allowDirectory
            ? h(
                importUi.dialogButton,
                { disabled: progress.busy, onClick: () => choose(prerequisite, "folder") },
                "Choose folder…",
              )
            : null,
        ),
      ),
    );
  }
  function EmulatorCommandButton({ label, run, requiresIdle = true }) {
    const progress = useEmulatorProgress();
    return importUi.react.createElement(
      importUi.dialogButton,
      { disabled: requiresIdle && progress.busy, onClick: run },
      label,
    );
  }
  function EmulatorProgressToolbar({ back, canGoBack }) {
    const h = importUi.react.createElement;
    const progress = useEmulatorProgress();
    return h(
      importUi.react.Fragment,
      {},
      progress.status ? h("p", { role: "status" }, progress.status) : null,
      h(
        importUi.focusable,
        { className: "wsgm-import-bar", "flow-children": "row" },
        canGoBack
          ? h(EmulatorCommandButton, { label: "Back", run: back, requiresIdle: false })
          : null,
        h(EmulatorCommandButton, {
          label: "Check for updates",
          run: () => void emulatorAct("refreshEmulators"),
        }),
        progress.busy
          ? h(EmulatorCommandButton, {
              label: "Stop operation",
              run: () => void emulatorAct("cancel"),
              requiresIdle: false,
            })
          : null,
      ),
    );
  }
  // One poster in the grid. Memoized on what it draws: after a scan a publication changes a few
  // titles, and redrawing every card for it was what made the D-pad lag on a large library.
  function ImportCard({ entry, slot, asset, actions, returning }) {
    const react = importUi.react;
    const h = react.createElement;
    const tone = entry.excluded
      ? importExcludedTone
      : (importBadgeTones[entry.action] ?? importBadgeTones.Skip);
    const width = importAsset(asset).card;
    const launchGlyph = entry.packaged
      ? importGlyph(react, entry.mode === "SteamIntegration" ? "overlay" : "controller")
      : entry.follows
        ? importGlyph(react, "launcher")
        : null;
    const canCycle =
      entry.editable && (entry.packaged ? entry.canUseSteamIntegration : entry.routes.length > 1);
    return h(
      "div",
      {
        className: "wsgm-import-card",
        "data-unavailable": String(importUnavailable(entry)),
        style: { width: `${width}px` },
      },
      h(ImportCapsule, {
        asset,
        width,
        image: slot?.thumb ?? "",
        placeholder: entry.name,
        dimmed: entry.excluded || importUnavailable(entry),
        overlay: [
          entry.selected
            ? h(
                "span",
                { key: "check", className: "wsgm-import-check" },
                importGlyph(react, "check"),
              )
            : null,
          h(
            "span",
            {
              key: "badge",
              className: "wsgm-import-badge",
              style: { background: tone.tone, color: tone.text },
            },
            entry.actionLabel,
          ),
        ],
        caption: importSlotCaption(slot),
        focus: {
          // Back from a title's artwork lands on the card it was opened from.
          autoFocus: returning,
          // A card that cannot be ticked says why rather than doing nothing.
          onActivate: () =>
            entry.selectable
              ? actions.toggle(entry)
              : importReport({ text: entry.reason, error: false }),
          onOKActionDescription: entry.selectable
            ? entry.selected
              ? "Deselect"
              : "Select"
            : "Why not",
          onSecondaryButton: canCycle ? () => cycleImportLaunch(entry) : undefined,
          onSecondaryActionDescription: canCycle
            ? entry.packaged
              ? "Launch mode"
              : "Launch route"
            : undefined,
          onOptionsButton: entry.editable ? () => actions.openTitle(entry, asset) : undefined,
          onOptionsActionDescription: entry.editable ? "Title artwork" : undefined,
          onMenuButton: () => actions.openDetails(entry),
          onMenuActionDescription: "Details",
          onContextMenu: (event) => {
            event?.preventDefault?.();
            actions.openDetails(entry);
          },
          onButtonDown: entry.editable ? importCycle(entry.id, asset) : undefined,
        },
      }),
      h("div", { className: "wsgm-import-name" }, launchGlyph, h("span", {}, entry.name)),
      h("div", { className: "wsgm-import-launch" }, entry.launchLabel),
      importUnavailable(entry)
        ? h("div", { className: "wsgm-import-launch" }, importAvailability(entry))
        : null,
    );
  }
  // What a card draws, so the memo redraws it only when that changed.
  const importCardKey = (entry, slot) =>
    [
      entry.id,
      entry.name,
      entry.selected,
      entry.selectable,
      entry.excluded,
      entry.editable,
      entry.action,
      entry.actionLabel,
      entry.reason,
      entry.launchLabel,
      entry.availability,
      entry.follows,
      entry.packaged,
      entry.mode,
      entry.canUseSteamIntegration,
      entry.routes?.length ?? 0,
      slot?.kind,
      slot?.thumb,
      slot?.provider,
      slot?.index,
      slot?.count,
    ].join("\u001f");
  const importCardType = (react) =>
    (ImportCardType ??= react.memo(
      ImportCard,
      (before, after) => before.drawn === after.drawn && before.asset === after.asset,
    ));
  // One title's artwork: what is chosen for each type, every candidate for the shown type grouped by
  // provider, and the match to fix when the providers found the wrong game.
  function ImportTitleArtwork({ entry, asset, onAsset, onBack, status }) {
    const react = importUi.react;
    const h = react.createElement;
    const ui = importUi;
    const [answer, setAnswer] = react.useState(null);
    const [search, setSearch] = react.useState(entry.matchName || entry.name);
    const [matches, setMatches] = react.useState(null);
    const [searching, setSearching] = react.useState(false);
    const slot = (entry.artwork ?? []).find((candidate) => candidate.asset === asset);
    const slotKey = `${slot?.kind}:${slot?.index}:${slot?.count}:${entry.artworkStatus}`;
    react.useEffect(() => {
      let live = true;
      void importAct("artworkOptions", { id: entry.id, asset }).then((next) => {
        if (live) setAnswer(next ?? { asset, options: [], failed: true });
      });
      return () => {
        live = false;
      };
    }, [entry.id, asset, slotKey]);
    const options = answer?.asset === asset ? (answer.options ?? []) : [];
    const providers = options.reduce((groups, option) => {
      const group = groups.find((candidate) => candidate.name === option.provider);
      if (group) group.items.push(option);
      else groups.push({ name: option.provider, items: [option] });
      return groups;
    }, []);
    const width = importAsset(asset).option;
    const empty = answer?.failed
      ? "The images could not be listed."
      : answer?.status === "loading" || answer?.status === "pending" || !answer
        ? "Finding images…"
        : importArtworkLine(entry) ||
          "No images were found for this. Fix the match to search for the right game.";
    const content = h(
      "div",
      { className: "wsgm-import-pane" },
      status,
      answer?.detail && options.length
        ? h("div", { className: "wsgm-import-muted" }, answer.detail)
        : null,
      options.length === 0 ? h("div", { className: "wsgm-import-muted" }, empty) : null,
      ...providers.map((group, groupIndex) =>
        h(
          "div",
          { key: group.name, className: "wsgm-import-group" },
          h(
            "div",
            { className: "wsgm-import-grouphead" },
            h("span", { className: "wsgm-import-eyebrow" }, group.name),
            h(
              "span",
              { className: "wsgm-import-muted" },
              `${group.items.length} image${group.items.length === 1 ? "" : "s"}`,
            ),
          ),
          h(
            ui.focusable,
            { className: "wsgm-import-grid", "flow-children": "grid" },
            ...group.items.map((option, index) =>
              h(ImportCapsule, {
                key: option.url,
                asset,
                width,
                image: option.thumb,
                placeholder: option.provider,
                overlay:
                  answer?.selected > 0 && options[answer.selected - 1]?.url === option.url
                    ? h("span", { className: "wsgm-import-check" }, importGlyph(react, "check"))
                    : null,
                caption: option.width ? `${option.width} × ${option.height}` : null,
                focus: {
                  autoFocus: groupIndex === 0 && index === 0,
                  onActivate: () =>
                    void importAct("pickArtwork", { id: entry.id, asset, url: option.url }),
                  onOKActionDescription: "Use this",
                  onOptionsButton: () => void importAct("clearArtwork", { id: entry.id, asset }),
                  onOptionsActionDescription: "Use none",
                  onButtonDown: importCycle(entry.id, asset),
                },
              }),
            ),
          ),
        ),
      ),
    );
    // What each type would get, the shown type highlighted, with a small preview of the image.
    const chosenRows = importAssets.map((type) => {
      const chosen = (entry.artwork ?? []).find((candidate) => candidate.asset === type.id);
      const size = { width: `${type.thumb[0]}px`, height: `${type.thumb[1]}px` };
      return h(
        "div",
        { key: type.id, className: "wsgm-import-chosen", "data-on": String(type.id === asset) },
        chosen?.thumb
          ? h("img", { src: chosen.thumb, alt: "", loading: "lazy", draggable: false, style: size })
          : h("span", { className: "wsgm-import-thumb", style: size }),
        h("div", {}, h("span", {}, type.label), h("span", {}, importSlotCaption(chosen) ?? "—")),
      );
    });
    // Fixing a match searches every provider at once and lists them together, the way the artwork
    // page does; the automatic match still asks SteamGridDB first.
    const runSearch = () => {
      setSearching(true);
      void importAct("searchMatch", { id: entry.id, query: search }).then((next) => {
        setSearching(false);
        setMatches(next?.matches ?? []);
      });
    };
    const byProvider = (matches ?? []).reduce((groups, match) => {
      const group = groups.find((candidate) => candidate.name === match.providerName);
      if (group) group.items.push(match);
      else groups.push({ name: match.providerName, items: [match] });
      return groups;
    }, []);
    const side = h(
      ui.focusable,
      { className: "wsgm-import-side", "flow-children": "column" },
      h("div", { className: "wsgm-import-eyebrow" }, "Chosen for this title"),
      ...chosenRows,
      h("div", { className: "wsgm-import-rule" }),
      h(
        "div",
        { className: "wsgm-import-muted", style: { lineHeight: 1.45 } },
        entry.matchName
          ? `Matched as “${entry.matchName}”. Wrong game?`
          : "Not matched to a game yet.",
      ),
      ui.textField
        ? h(ui.textField, {
            value: search,
            placeholder: "Search for the right game",
            onChange: (event) => setSearch(event?.target?.value ?? ""),
          })
        : null,
      h(
        ui.dialogButton,
        { disabled: searching, onClick: runSearch },
        searching ? "Searching…" : "Fix match",
      ),
      entry.matchFixed
        ? h(
            ui.dialogButton,
            {
              onClick: () =>
                void importAct("setMatch", { id: entry.id, provider: "", gameId: "", name: "" }),
            },
            "Use the automatic match",
          )
        : null,
      h(
        "div",
        { className: "wsgm-import-matches" },
        matches && matches.length === 0
          ? h("div", { className: "wsgm-import-muted" }, "No provider knows that name.")
          : null,
        ...byProvider.flatMap((group) => [
          h("div", { key: `head:${group.name}`, className: "wsgm-import-eyebrow" }, group.name),
          ...group.items.map((match) =>
            h(
              ui.dialogButton,
              {
                key: `${match.provider}:${match.id}`,
                onClick: () => {
                  setMatches(null);
                  void importAct("setMatch", {
                    id: entry.id,
                    provider: match.provider,
                    gameId: match.id,
                    name: match.name,
                  });
                },
              },
              `${match.name}${match.exact ? "" : " ?"}`,
            ),
          ),
        ]),
      ),
      h(ui.dialogButton, { onClick: onBack }, "Back"),
    );
    return renderSteamUiLevel(
      ui,
      { className: "wsgm-import-main", onBack },
      h(
        "div",
        { className: "wsgm-import-split" },
        side,
        h(
          "div",
          { className: "wsgm-import-main" },
          h(ui.tabs, {
            autoFocusContents: true,
            activeTab: asset,
            onShowTab: (next) => onAsset(next),
            tabs: importAssets.map((type) => ({
              id: type.id,
              title: type.label,
              content: type.id === asset ? content : null,
            })),
          }),
        ),
      ),
    );
  }
  /**
   * Renders shared import review state and dispatches host commands for every persistent action.
   * @param context Registered page accessors for Steam components, latest state and publication refusal.
   * @param page Identifies the importer or standalone emulator manager route.
   * @returns The page React tree, including loading or refusal state when data is unavailable.
   */
  function LibraryImportPage({ context, page }) {
    const react = context.react();
    const h = react.createElement;
    const ui = context.ui();
    importUi = ui;
    const state = context.state() ?? {};
    importLatest = state;
    const [view, setView] = react.useState({
      name: "grid",
      title: "",
      asset: "grid",
      from: "grid",
    });
    const [tab, setTab] = react.useState("all");
    const [asset, setAsset] = react.useState("grid");
    const [query, setQuery] = react.useState("");
    const [fillFrom, setFillFrom] = react.useState("");
    const [message, setMessage] = react.useState(null);
    const [returnTo, setReturnTo] = react.useState("");
    react.useEffect(() => {
      importReporters.add(setMessage);
      return () => {
        importReporters.delete(setMessage);
      };
    }, []);
    react.useEffect(() => {
      importStateStore.changed();
      if (importEditorState?.loading !== !!state.loading) updateImportEditorState();
    }, [state]);
    react.useEffect(() => subscribe(EmulatorPatchId, receiveRomChoices), []);
    const entries = state.entries ?? [];
    const sources = state.sources ?? [];
    const busy = !!state.loading;
    const titleEntry =
      view.name === "title" ? entries.find((entry) => entry.id === view.title) : null;
    // A title that is no longer listed, or can no longer be changed, takes the page back where it came
    // from rather than leaving an artwork view nothing can act on.
    react.useEffect(() => {
      if (view.name === "title" && (!titleEntry || !titleEntry.editable)) {
        setView({ name: view.from, title: "", asset: "grid", from: "grid" });
      }
    }, [view.name, titleEntry?.id, titleEntry?.editable]);
    // Stable for the life of the page, so a memoized card's handlers never go stale.
    const actions = react.useMemo(
      () => ({
        toggle: (entry) => void importAct("toggleEntry", { id: entry.id }),
        openTitle: (entry, type) => {
          setReturnTo(entry.id);
          setView((current) => ({
            name: "title",
            title: entry.id,
            asset: type,
            from: current.name === "title" ? current.from : current.name,
          }));
        },
        openDetails: (entry) =>
          showSteamModal(importUi, {
            title: entry.name,
            render: (close) =>
              importUi.react.createElement(ImportDetailsBody, {
                id: entry.id,
                close,
                openTitle: actions.openTitle,
              }),
          }),
      }),
      [],
    );
    // Every message stays visible: a refusal does not hide what the host says, and the other way round.
    const refusal = context.refusal();
    const statusLines = [
      refusal ? { text: `The library could not be shown in full: ${refusal}`, error: true } : null,
      message,
      state.error ? { text: state.error, error: true } : null,
      state.phase === "applying"
        ? { text: `Saving ${state.progress ?? 0} of ${state.progressTotal ?? 0}…`, error: false }
        : state.notice
          ? { text: state.notice, error: false }
          : null,
      state.launcherDetail ? { text: state.launcherDetail, error: false } : null,
    ].filter((line) => line !== null);
    const status = h(
      "div",
      { className: "wsgm-import-status", role: "status" },
      ...statusLines.map((line, index) =>
        h(
          "div",
          { key: index, className: line.error ? "wsgm-import-error" : undefined },
          line.text,
        ),
      ),
    );
    const saveCount = state.selectedCount ?? 0;
    const saveButton = h(
      ui.dialogButtonPrimary,
      { disabled: !state.selectedCount || busy, onClick: () => void importAct("apply") },
      state.phase === "applying"
        ? `Saving ${state.progress ?? 0}/${state.progressTotal ?? 0}…`
        : !state.selectedCount
          ? "Save to Steam"
          : `Save to Steam (${saveCount})`,
    );
    let body;
    let header;
    const crumb = (text) =>
      h("span", { className: "wsgm-import-crumb" }, text, " ", importGlyph(react, "chevron"));
    if (view.name === "title" && titleEntry) {
      header = h(
        "div",
        { className: "wsgm-import-head" },
        crumb("Game Library"),
        h("h1", {}, titleEntry.name),
        h(
          "span",
          { className: "wsgm-import-muted" },
          "Choose artwork · applied when you save to Steam",
        ),
      );
      body = h(ImportTitleArtwork, {
        key: titleEntry.id,
        entry: titleEntry,
        asset: view.asset,
        status,
        onAsset: (next) => setView({ ...view, asset: next }),
        onBack: () => setView({ name: view.from, title: "", asset: "grid", from: "grid" }),
      });
    } else if (view.name === "all") {
      // Every selected title that can take artwork: the review's search does not apply here, so what
      // Fill and Reset change is exactly the list on screen.
      const rows = entries.filter((entry) => entry.selected && entry.editable);
      const preference = fillFrom || state.artworkPreference || "Catalog";
      const fill = (onlyEmpty, type) =>
        void importAct("fillArtwork", { preference, onlyEmpty, asset: type });
      header = h(
        "div",
        { className: "wsgm-import-head" },
        crumb("Game Library"),
        h("h1", {}, "All artwork"),
        h(
          "span",
          { className: "wsgm-import-muted" },
          `${rows.length} selected title${rows.length === 1 ? "" : "s"} · applied when you save to Steam`,
        ),
      );
      const back = () => setView({ name: "grid", title: "", asset: "grid", from: "grid" });
      body = renderSteamUiLevel(
        ui,
        { className: "wsgm-import-main", onBack: back },
        h(
          "div",
          { className: "wsgm-import-pane" },
          h(
            ui.focusable,
            { className: "wsgm-import-bar", "flow-children": "row" },
            h(
              "div",
              { className: "wsgm-import-tool", style: { width: "290px" } },
              renderSteamDropdown(ui, {
                label: "Fill every title from",
                rgOptions: [
                  { data: "Catalog", label: "Fill from the launcher first" },
                  { data: "Providers", label: "Fill from SteamGridDB first" },
                ],
                selectedOption: preference,
                onChange: (option) => option?.data && setFillFrom(option.data),
              }),
            ),
            h(ui.dialogButton, { onClick: () => fill(false, "") }, "Fill all"),
            h(ui.dialogButton, { onClick: () => fill(true, "") }, "Fill empty slots"),
            h(ui.dialogButton, { onClick: () => void importAct("resetArtwork") }, "Reset all"),
            h("div", { className: "wsgm-import-spacer" }),
            h(ui.dialogButton, { onClick: back }, "Back"),
            saveButton,
          ),
          status,
          h(
            ui.focusable,
            { className: "wsgm-import-colhead", "flow-children": "row" },
            h("div", { style: { width: "170px" }, className: "wsgm-import-eyebrow" }, "Title"),
            ...importAssets.map((type) =>
              h(
                "div",
                { key: type.id, className: "wsgm-import-col", style: { width: `${type.cell}px` } },
                h("span", { className: "wsgm-import-eyebrow" }, type.short),
                h(ui.dialogButton, { onClick: () => fill(false, type.id) }, "Fill"),
              ),
            ),
          ),
          rows.length === 0
            ? h(
                "div",
                { className: "wsgm-import-muted" },
                "Select titles in the review to dress them here.",
              )
            : null,
          h(
            ui.focusable,
            { className: "wsgm-import-rows", "flow-children": "column" },
            ...rows.map((entry, rowIndex) =>
              h(
                ui.focusable,
                { key: entry.id, className: "wsgm-import-row", "flow-children": "row" },
                h(
                  "div",
                  { className: "wsgm-import-rowname" },
                  h("span", {}, entry.name),
                  h("span", { className: "wsgm-import-muted" }, entry.source),
                ),
                ...importAssets.map((type, columnIndex) => {
                  const slot = (entry.artwork ?? []).find(
                    (candidate) => candidate.asset === type.id,
                  );
                  return h(ImportCapsule, {
                    key: type.id,
                    asset: type.id,
                    width: type.cell,
                    image: slot?.thumb ?? "",
                    placeholder:
                      slot?.kind === "loading" ? "…" : slot?.kind === "keep" ? "Current" : "None",
                    caption: type.id === "icon" ? null : importSlotCaption(slot),
                    focus: {
                      autoFocus: rowIndex === 0 && columnIndex === 0,
                      onActivate: () => actions.openTitle(entry, type.id),
                      onOKActionDescription: "All options",
                      onOptionsButton: () =>
                        void importAct("clearArtwork", { id: entry.id, asset: type.id }),
                      onOptionsActionDescription: "Clear",
                      onMenuButton: () => actions.openDetails(entry),
                      onMenuActionDescription: "Details",
                      onButtonDown: importCycle(entry.id, type.id),
                    },
                  });
                }),
              ),
            ),
          ),
        ),
      );
    } else {
      // The review. One pass over the entries for the search, the tab counts and the source groups;
      // only the shown tab's cards are built.
      const search = query.trim().toLowerCase();
      const counts = { all: 0 };
      const active = importTabs.find((candidate) => candidate.id === tab) ?? importTabs[0];
      const groups = new Map();
      for (const entry of entries) {
        if (search && !String(entry.name).toLowerCase().includes(search)) continue;
        counts.all++;
        counts[entry.group] = (counts[entry.group] ?? 0) + 1;
        if (active.group && entry.group !== active.group) continue;
        const key = String(entry.sourceId).toLowerCase();
        const items = groups.get(key);
        if (items) items.push(entry);
        else groups.set(key, [entry]);
      }
      const Card = importCardType(react);
      const shown = sources
        .map((source) => ({ source, items: groups.get(String(source.id).toLowerCase()) ?? [] }))
        .concat(
          [...groups.entries()]
            .filter(([key]) => !sources.some((source) => String(source.id).toLowerCase() === key))
            .map(([key, items]) => ({ source: { id: key, name: items[0]?.source ?? key }, items })),
        )
        .filter((group) => group.items.length);
      const grid = [
        shown.length === 0
          ? h(
              "div",
              { className: "wsgm-import-muted" },
              entries.length ? "Nothing here." : "No games listed yet.",
            )
          : null,
        ...shown.map((group) =>
          h(
            "div",
            { key: group.source.id, className: "wsgm-import-group" },
            h(
              "div",
              { className: "wsgm-import-grouphead" },
              h("span", { className: "wsgm-import-eyebrow" }, group.source.name),
              h(
                "span",
                { className: "wsgm-import-muted" },
                `${group.items.length} title${group.items.length === 1 ? "" : "s"} · ` +
                  `${group.items.filter((entry) => entry.selected).length} selected`,
              ),
            ),
            h(
              ui.focusable,
              { className: "wsgm-import-grid", "flow-children": "grid" },
              ...group.items.map((entry) => {
                const slot = (entry.artwork ?? []).find((candidate) => candidate.asset === asset);
                return h(Card, {
                  key: entry.id,
                  entry,
                  slot,
                  asset,
                  actions,
                  returning: entry.id === returnTo,
                  drawn: importCardKey(entry, slot) + (entry.id === returnTo ? "\u001freturn" : ""),
                });
              }),
            ),
          ),
        ),
      ];
      // The sources, each ticked with Steam's own checkbox. One that is not installed cannot be
      // ticked. The right-hand text is what the last scan found in it, or why it cannot be scanned.
      const Check = steamCheckbox(ui);
      const sourceRow = (source) =>
        h(
          "div",
          {
            key: source.id,
            className: "wsgm-import-source",
            "data-missing": String(!source.installed),
          },
          h(Check, {
            label: source.name,
            description: !source.installed
              ? source.detail || "Not found"
              : source.count >= 0
                ? String(source.count)
                : source.detail,
            checked: source.checked,
            controlled: true,
            disabled: !source.canToggle || busy,
            bottomSeparator: "none",
            onChange: (value) =>
              void importAct("setSourceEnabled", { id: source.id, enabled: !!value }),
          }),
        );
      const sidebar = h(
        ui.focusable,
        { className: "wsgm-import-sidebar", "flow-children": "column" },
        h("div", { className: "wsgm-import-eyebrow" }, "Sources"),
        ...sources
          .filter(
            (source) =>
              source.kind !== "Folder" && source.kind !== "Rom" && source.kind !== "Manual",
          )
          .map(sourceRow),
        h("div", { className: "wsgm-import-eyebrow wsgm-import-custom" }, "Custom"),
        ...sources
          .filter((source) => source.kind === "Folder")
          .map((source) =>
            h(
              ui.focusable,
              {
                key: `${source.id}-wrap`,
                onOptionsButton: () => void importAct("removeFolder", { id: source.id }),
                onOptionsActionDescription: "Remove folder",
              },
              sourceRow(source),
            ),
          ),
        ...sources
          .filter((source) => source.kind === "Rom")
          .map((source) =>
            h(
              ui.focusable,
              { key: `${source.id}-custom`, "flow-children": "column" },
              sourceRow(source),
              h(
                ui.dialogButton,
                {
                  disabled: busy,
                  onClick: () =>
                    showSteamModal(ui, {
                      title: `Configure ${source.name}`,
                      render: (close) =>
                        h(source.kind === "Rom" ? ImportRomSourceBody : ImportManualSourceBody, {
                          sourceId: source.id,
                          close,
                        }),
                    }),
                },
                "Configure source…",
              ),
            ),
          ),
        ...sources.filter((source) => source.kind === "Manual").map(sourceRow),
        ...(state.manualSources ?? []).map((source) =>
          h(
            ui.dialogButton,
            {
              key: source.id,
              disabled: busy,
              onClick: () =>
                showSteamModal(ui, {
                  title: `Configure ${source.name}`,
                  render: (close) =>
                    h(source.romPath ? ImportRomSourceBody : ImportManualSourceBody, {
                      sourceId: source.id,
                      singleFile: !!source.romPath,
                      close,
                    }),
                }),
            },
            `Configure ${source.name}…`,
          ),
        ),
        h(
          ui.dialogButton,
          {
            disabled: busy,
            onClick: () =>
              showSteamModal(ui, {
                title: "Add a ROM library",
                render: (close) => h(ImportRomSourceBody, { close }),
              }),
          },
          "Add ROM library…",
        ),
        h(
          ui.dialogButton,
          {
            disabled: busy,
            onClick: () =>
              showSteamModal(ui, {
                title: "Add a ROM",
                render: (close) => h(ImportRomSourceBody, { singleFile: true, close }),
              }),
          },
          "Add a ROM…",
        ),
        h(
          ui.dialogButton,
          {
            disabled: busy,
            onClick: () =>
              showSteamModal(ui, {
                title: "Add a manual shortcut",
                render: (close) => h(ImportManualSourceBody, { close }),
              }),
          },
          "Add manual shortcut…",
        ),
        h(
          ui.dialogButton,
          {
            disabled: busy,
            onClick: () =>
              showSteamModal(ui, {
                title: "Add a shortcuts folder",
                render: (close) => h(ImportFolderBody, { close }),
              }),
          },
          "Add folder…",
        ),
        h("div", { className: "wsgm-import-eyebrow wsgm-import-custom" }, "Steam"),
        h(
          "div",
          { className: "wsgm-import-source" },
          h(Check, {
            label: "Collections",
            description: "One per launcher and folder",
            checked: !!state.createCollections,
            controlled: true,
            bottomSeparator: "none",
            onChange: (value) => void importAct("setCollections", { enabled: !!value }),
          }),
        ),
      );
      const anySelected = entries.some(
        (entry) =>
          entry.selected &&
          (!active.group || entry.group === active.group) &&
          (!search || String(entry.name).toLowerCase().includes(search)),
      );
      const toolbar = h(
        ui.focusable,
        { className: "wsgm-import-bar", "flow-children": "row" },
        h(
          "div",
          { className: "wsgm-import-tool", style: { width: "200px" } },
          renderSteamDropdown(ui, {
            label: "Artwork shown",
            rgOptions: importAssets.map((type) => ({ data: type.id, label: type.label })),
            selectedOption: asset,
            onChange: (option) => option?.data && setAsset(option.data),
          }),
        ),
        ui.textField
          ? h(
              "div",
              { className: "wsgm-import-search" },
              h(ui.textField, {
                value: query,
                placeholder: "Search titles",
                onChange: (event) => setQuery(event?.target?.value ?? ""),
              }),
            )
          : null,
        h("div", { className: "wsgm-import-spacer" }),
        h(
          ui.dialogButton,
          {
            disabled: !state.selectedCount,
            onClick: () => setView({ name: "all", title: "", asset: "grid", from: "grid" }),
          },
          "All artwork",
        ),
        busy
          ? h(ui.dialogButton, { onClick: () => void importAct("cancel") }, "Stop")
          : h(ui.dialogButton, { onClick: () => void importAct("scan") }, "Scan"),
        // What this tab and search show: "Select all" on the New tab never reaches another tab's titles.
        h(
          ui.dialogButton,
          {
            disabled: counts.all === 0 || busy,
            onClick: () =>
              void importAct("select", {
                group: active.group,
                query: search,
                selected: !anySelected,
              }),
          },
          anySelected ? "Clear" : "Select all",
        ),
        saveButton,
      );
      const installed = sources.filter((source) => source.installed).length;
      header = h(
        "div",
        { className: "wsgm-import-head" },
        h("h1", {}, "Game Library"),
        h(
          "span",
          { className: "wsgm-import-muted" },
          entries.length
            ? `${installed} source${installed === 1 ? "" : "s"} found · ${entries.length} title${entries.length === 1 ? "" : "s"} · ` +
                `${state.selectedCount ?? 0} selected`
            : busy
              ? "Scanning…"
              : "Scan to find games in your launchers.",
        ),
      );
      body = [
        h(react.Fragment, { key: "sidebar" }, sidebar),
        h(
          "div",
          { key: "review", className: "wsgm-import-main" },
          h(ui.tabs, {
            autoFocusContents: true,
            activeTab: active.id,
            onShowTab: (next) => setTab(next),
            tabs: importTabs.map((candidate) => ({
              id: candidate.id,
              title: `${candidate.title} ${counts[candidate.group || "all"] ?? 0}`,
              content:
                candidate.id === active.id
                  ? h("div", { className: "wsgm-import-pane" }, toolbar, status, ...grid)
                  : null,
            })),
          }),
        ),
      ];
    }
    return h(
      "div",
      { id: "wsgm-import", "aria-label": "Game Library" },
      h("style", null, importStyles),
      header,
      h("div", { className: "wsgm-import-body" }, ...(Array.isArray(body) ? body : [body])),
    );
  }
  const libraryImportPage = registerSteamPage({
    template: "library-import",
    gate: "libraryImport",
    patchId: LibraryImportPatchId,
    components: resolveSteamUiComponents,
    // Only what this page actually renders. Steam's checkbox and bare dropdown are wanted, not
    // required: the sidebar falls back to Steam's toggle and the toolbar to its labelled field.
    required: [
      "react",
      "focusable",
      "toggleField",
      "dropdown",
      "textField",
      "dialogButton",
      "dialogButtonPrimary",
      "tabs",
      "modalRoot",
      "showModal",
    ],
    prepare: (ui, runtime) => {
      const classes = resolveSteamLibraryClasses(runtime);
      if (!classes) return "Native Steam components unavailable: library classes";
      ImportCapsule = createSteamCapsule(ui, classes);
      ImportCardType = null;
      return null;
    },
    status: () => ({
      checkbox: !!libraryImportPage.ui()?.checkbox,
      dropdownControl: !!libraryImportPage.ui()?.dropdownControl,
      entries: libraryImportPage.state()?.entries?.length ?? 0,
    }),
    Page: LibraryImportPage,
  });
  function EmulatorManagerPage({ context }) {
    const react = context.react();
    const h = react.createElement;
    importUi = context.ui();
    const state = context.state() ?? {
      emulators: { definitions: [], installations: [], offers: [], systemPreferences: [] },
      romSystems: [],
    };
    react.useEffect(() => {
      emulatorStateStore.changed();
      receiveRomChoices(state);
    }, [state]);
    react.useEffect(
      () =>
        subscribe(EmulatorProgressId, (progress) => {
          emulatorProgress = progress ?? { busy: false, status: "" };
          emulatorProgressStore.changed();
        }),
      [],
    );
    const [message, setMessage] = react.useState(null);
    react.useEffect(() => {
      emulatorReporters.add(setMessage);
      return () => emulatorReporters.delete(setMessage);
    }, []);
    const status = message
      ? h("p", { className: message.error ? "wsgm-import-error" : undefined }, message.text)
      : null;
    return h(
      "div",
      { id: "wsgm-import", className: "wsgm-import wsgm-emulators" },
      h("style", null, importStyles),
      h("div", { className: "wsgm-import-head" }, h("h1", {}, "Emulator Manager")),
      h("div", { className: "wsgm-import-body" }, h(ImportEmulatorPage, { state, status })),
    );
  }
  const emulatorPage = registerSteamPage({
    template: "emulators",
    gate: "emulatorManager",
    patchId: EmulatorPatchId,
    components: resolveSteamUiComponents,
    required: [
      "react",
      "focusable",
      "toggleField",
      "dropdown",
      "textField",
      "dialogButton",
      "dialogButtonPrimary",
      "modalRoot",
      "showModal",
    ],
    Page: EmulatorManagerPage,
  });
  // @fragment consumer/library-tabs.ts
  // WSGM's library transform shares the toolkit's React claim. It owns no dispatcher property.
  const libraryTabsClaim = (() => {
    const name = "wsgm.library-tabs";
    let react = null;
    return {
      install(host, transform) {
        if (react && react !== host) {
          const released = releaseMemo(react, name);
          if (!released.ok) return released;
        }
        const installed = interceptMemo(host, name, transform);
        if (installed.ok) react = host;
        return installed;
      },
      status() {
        return { installed: !!react && memoIntercepted(react, name) };
      },
      remove() {
        const released = releaseMemo(react, name);
        if (released.ok) react = null;
        return released;
      },
    };
  })();
  registerGate("wsgmLibraryTabs", libraryTabsClaim);
  // @fragment consumer/page-kit.ts
  /**
   * Creates a command sender for pages whose error banner comes from host state.
   * @param patchId Registered WSGM page command identity.
   * @returns A sender resolving to the backend result, or undefined on rejection; the page relies on host publication for error display.
   */
  function wsgmPageAct(patchId) {
    return (command, payload = {}) => request(patchId, command, payload).catch(() => undefined);
  }
  /**
   * Projects published page navigation and notices into the shared tabbed frame.
   * @param act Page sender accepting setTab and dismiss commands.
   * @param state Published activeTab, error and notice fields.
   * @returns Frame props; errors take precedence over notices and no message yields a null banner.
   */
  function wsgmPageFrame(act, state) {
    const banner = state.error || state.notice;
    return {
      active: state.activeTab,
      onTab: (tab) => void act("setTab", { tab }),
      banner: banner
        ? { text: banner, error: !!state.error, onDismiss: () => void act("dismiss") }
        : null,
    };
  }
  // @fragment consumer/themes.ts
  // Theme frontend; shared host services own catalogue, profiles, settings and CSS deployment.
  const ThemesPatchId = "wsgm.themes";
  let themesUi = null;
  const themesAct = wsgmPageAct(ThemesPatchId);
  const themesTabs = [
    { id: "browse", title: "Browse" },
    { id: "installed", title: "Installed" },
    { id: "profiles", title: "Profiles" },
    { id: "settings", title: "Settings" },
  ];
  /**
   * Dispatches a committed settings row to the host command encoded by its key.
   * @param row Published row whose NUL-separated key identifies the command and target.
   * @param value Edited value, converted according to the row kind.
   * @param commit False for draft updates, which are not sent to the host.
   */
  const themesRowChange = (row, value, commit = true) => {
    if (!commit) return;
    const [kind, theme, patch, component] = String(row.key).split("\u0000");
    switch (kind) {
      case "theme":
        void themesAct("setEnabled", { name: theme, enabled: !!value });
        break;
      case "patch":
        void themesAct("setPatch", { theme, patch, value: String(value) });
        break;
      case "checkbox":
        void themesAct("setPatch", { theme, patch, value: value ? "Yes" : "No" });
        break;
      case "slider":
        void themesAct("setPatch", {
          theme,
          patch,
          value: String(row.labels?.[Number(value)] ?? ""),
        });
        break;
      case "component":
        void themesAct("setComponent", { theme, patch, component, value: String(value) });
        break;
      case "profile":
        void themesAct("setProfile", { name: String(value) });
        break;
      case "setting":
        void themesAct("setSetting", { key: theme, value });
        break;
      default:
        break;
    }
  };
  const themesKey = (...parts) => parts.join("\u0000");
  /**
   * Builds settings descriptors from one installed theme snapshot.
   * @param theme Host-published theme and its patch/component choices.
   * @returns Rows headed by the enable switch; disabled themes omit patch controls.
   */
  const themesRowsOf = (theme) => {
    const rows = [];
    const description =
      theme.status === "outdated"
        ? `Update available (${theme.latestVersion}) · ${theme.author}`
        : theme.author
          ? `${theme.version} · ${theme.author}`
          : theme.version;
    rows.push({
      key: themesKey("theme", theme.name),
      kind: "boolean",
      label: theme.displayName,
      description,
      checked: !!theme.enabled,
    });
    if (!theme.enabled) return rows;
    for (const patch of theme.patches ?? []) {
      switch (patch.type) {
        case "checkbox":
          rows.push({
            key: themesKey("checkbox", theme.name, patch.name),
            kind: "boolean",
            label: patch.name,
            checked: patch.value === "Yes",
            nested: true,
          });
          break;
        case "slider":
          rows.push({
            key: themesKey("slider", theme.name, patch.name),
            kind: "range",
            label: patch.name,
            number: Math.max(0, (patch.options ?? []).indexOf(patch.value)),
            labels: patch.options,
            nested: true,
          });
          break;
        case "none":
          rows.push({
            key: themesKey("none", theme.name, patch.name),
            kind: "note",
            label: patch.name,
            text: "",
            nested: true,
          });
          break;
        default:
          rows.push({
            key: themesKey("patch", theme.name, patch.name),
            kind: "choice",
            label: patch.name,
            text: patch.value,
            choices: (patch.options ?? []).map((option) => ({ value: option, label: option })),
            nested: true,
          });
          break;
      }
      for (const component of (patch.components ?? []).filter(
        (component) => component.on === patch.value,
      )) {
        rows.push({
          key: themesKey("component", theme.name, patch.name, component.name),
          kind: component.type === "color-picker" ? "color" : "text",
          label: component.name,
          text: component.value,
          nested: true,
        });
      }
    }
    return rows;
  };
  // A card in the store's grid: the kit's card with the theme's screenshot, its counts and target,
  // an Installed or Update badge, and its version and author.
  const themesCard = (ui, item, open) => {
    const react = ui.react;
    const badge =
      item.localStatus === "installed"
        ? { text: "Installed" }
        : item.localStatus === "outdated"
          ? { text: "Update", warn: true }
          : null;
    return renderSteamUiCard(ui, {
      key: item.id,
      image: item.imageUrl,
      stats: [
        { glyph: renderSteamUiGlyph(react, "download"), text: String(item.downloads ?? 0) },
        { glyph: renderSteamUiGlyph(react, "star"), text: String(item.stars ?? 0) },
        ...(item.target ? [{ glyph: renderSteamUiGlyph(react, "target"), text: item.target }] : []),
      ],
      badge,
      title: item.displayName,
      meta: [
        item.updated ? `${item.version} - Last Updated ${item.updated}` : item.version,
        item.author ? `By ${item.author}` : "",
      ],
      onActivate: () => open(item.id),
    });
  };
  function ThemesDetail({ detail, busy }) {
    const ui = themesUi;
    const react = ui.react;
    const h = react.createElement;
    const [focusedImage, setFocusedImage] = react.useState(0);
    const item = detail.item;
    const installLabel =
      item.localStatus === "outdated"
        ? "Update"
        : item.localStatus === "installed"
          ? "Reinstall"
          : "Install";
    return renderSteamUiDetail(ui, {
      title: item.displayName,
      badge: item.version,
      media: renderSteamUiGallery(ui, {
        images: detail.imageUrls ?? [],
        index: focusedImage,
        onSelect: setFocusedImage,
        empty: "No screenshot",
      }),
      main: [
        h(
          "div",
          { className: "steam-ui-kit-muted" },
          item.author ? `By ${item.author}` : "",
          item.updated ? ` · Last Updated ${item.updated}` : "",
        ),
        h("h3", null, "Description"),
        h(
          "p",
          { className: detail.description ? "" : "steam-ui-kit-muted" },
          detail.loading
            ? "Loading…"
            : detail.error
              ? detail.error
              : detail.description || "No description provided.",
        ),
        item.targets?.length
          ? h(
              react.Fragment,
              null,
              h("h3", null, "Targets"),
              renderSteamUiChips(
                ui,
                item.targets.map((target) => ({
                  label: target,
                  description: `View Other "${target}" Themes`,
                  onClick: () =>
                    void themesAct("browse", { filter: target, order: "", search: "" }).then(() =>
                      themesAct("closeDetail"),
                    ),
                })),
              ),
            )
          : null,
        detail.dependencies?.length
          ? h(
              react.Fragment,
              null,
              h("h3", null, "Requires"),
              h(
                "div",
                { className: "steam-ui-kit-muted" },
                detail.dependencies
                  .map(
                    (dependency) =>
                      `${dependency.displayName}${dependency.installed ? "" : " (not installed)"}`,
                  )
                  .join(", "),
              ),
            )
          : null,
      ],
      aside: [
        renderSteamUiBox(
          react,
          h(react.Fragment, null, renderSteamUiGlyph(react, "star"), ` ${item.stars ?? 0} Stars`),
          h(
            "div",
            { className: "steam-ui-kit-muted" },
            "Starring needs a DeckThemes account, which WSGM does not sign in to.",
          ),
        ),
        renderSteamUiBox(
          react,
          `${installLabel} ${item.displayName}`,
          h("div", { className: "steam-ui-kit-muted" }, `${item.downloads ?? 0} Downloads`),
          h(
            ui.dialogButtonPrimary,
            {
              disabled: !!busy || !!detail.loading,
              onClick: () => void themesAct("install", { id: item.id }),
            },
            busy ? "Working…" : installLabel,
          ),
          h(
            "div",
            { className: "steam-ui-kit-muted" },
            "Downloads into WSGM's themes folder, with every theme it needs. Turn it on under Installed.",
          ),
        ),
      ],
      onBack: () => void themesAct("closeDetail"),
    });
  }
  function ThemesBrowse({ state }) {
    const ui = themesUi;
    const react = ui.react;
    const h = react.createElement;
    const browse = state.browse ?? {};
    const [search, setSearch] = react.useState(browse.search ?? "");
    react.useEffect(() => setSearch(browse.search ?? ""), [browse.search]);
    // The first look at the store is the page's own: nothing is fetched until someone opens the tab.
    react.useEffect(() => {
      if (!browse.loading && !browse.error && (browse.items ?? []).length === 0 && !browse.page) {
        void themesAct("browse", {
          filter: browse.filter ?? "All",
          order: browse.order ?? "",
          search: browse.search ?? "",
        });
      }
    }, []);
    const ask = (changes) =>
      void themesAct("browse", {
        filter: browse.filter ?? "All",
        order: browse.order ?? "",
        search,
        ...changes,
      });
    if (state.detail) return h(ThemesDetail, { detail: state.detail, busy: state.busy });
    const filters = browse.filters ?? {};
    const total = Object.values(filters).reduce((sum, count) => sum + Number(count || 0), 0);
    const filterOptions = [
      {
        data: "All",
        label: h(
          "div",
          { className: "wsgm-themes-filter" },
          h("span", null, "All"),
          h("b", null, total ? String(total) : ""),
        ),
      },
      ...Object.keys(filters)
        .filter((name) => Number(filters[name]) > 0)
        .map((name) => ({
          data: name,
          label: h(
            "div",
            { className: "wsgm-themes-filter" },
            h("span", null, name),
            h("b", null, String(filters[name])),
          ),
        })),
    ];
    const orderOptions = (browse.orders ?? []).map((order) => ({ data: order, label: order }));
    const items = browse.items ?? [];
    const open = (id) => void themesAct("open", { id });
    return renderSteamUiPane(
      ui,
      {},
      renderSteamUiToolbar(
        ui,
        renderSteamUiTool(
          ui,
          "Sort",
          renderSteamDropdown(ui, {
            label: "Sort",
            rgOptions: orderOptions,
            selectedOption: browse.order,
            onChange: (option) => ask({ order: option?.data }),
          }),
        ),
        renderSteamUiTool(
          ui,
          "Filter",
          renderSteamDropdown(ui, {
            label: "Filter",
            rgOptions: filterOptions,
            selectedOption: browse.filter ?? "All",
            onChange: (option) => ask({ filter: option?.data }),
          }),
        ),
        renderSteamUiTool(
          ui,
          null,
          h(ui.textField, {
            label: "Search",
            value: search,
            onChange: (event) => setSearch(event?.target?.value ?? ""),
            onBlur: () => {
              if (search !== (browse.search ?? "")) ask({ search });
            },
          }),
          true,
        ),
        h(ui.dialogButton, { onClick: () => ask({}) }, "Refresh"),
      ),
      browse.error ? renderSteamUiEmpty(react, browse.error, true) : null,
      renderSteamUiGrid(
        ui,
        items.map((item) => themesCard(ui, item, open)),
      ),
      browse.loading
        ? renderSteamUiEmpty(react, "Asking the store…")
        : items.length === 0 && !browse.error
          ? renderSteamUiEmpty(react, "Nothing matched.")
          : null,
      items.length < (browse.total ?? 0) && !browse.loading
        ? renderSteamUiMore(ui, { onClick: () => void themesAct("loadMore") })
        : null,
    );
  }
  function ThemesInstalled({ state }) {
    const ui = themesUi;
    const react = ui.react;
    const h = react.createElement;
    const themes = state.themes ?? [];
    return renderSteamUiPane(
      ui,
      {},
      renderSteamUiToolbar(
        ui,
        h(
          ui.dialogButton,
          { disabled: !!state.busy, onClick: () => void themesAct("refresh") },
          "Refresh",
        ),
        state.updates > 0
          ? h(
              ui.dialogButton,
              { disabled: !!state.busy, onClick: () => void themesAct("updateAll") },
              `Update All Themes (${state.updates})`,
            )
          : null,
      ),
      themes.length === 0
        ? renderSteamUiEmpty(react, "You have no themes installed. Get started under Browse.")
        : null,
      ...themes.map((theme) =>
        h(
          ui.settingsSection,
          { key: theme.name, label: undefined },
          ...themesRowsOf(theme).map((row) => {
            const control = renderSteamSettingRow(ui, row, undefined, themesRowChange, () => {});
            return row.nested
              ? h("div", { key: row.key, className: "steam-ui-kit-nested" }, control)
              : control;
          }),
          h(
            "div",
            { className: "wsgm-themes-manage" },
            renderSteamUiChips(ui, [
              ...(theme.status === "outdated"
                ? [
                    {
                      label: `Update to ${theme.latestVersion}`,
                      onClick: () => {
                        if (!state.busy) void themesAct("update", { name: theme.name });
                      },
                    },
                  ]
                : []),
              {
                label: theme.hidden ? "Show in Quick Access" : "Hide from Quick Access",
                onClick: () =>
                  void themesAct("setHidden", { name: theme.name, hidden: !theme.hidden }),
              },
              {
                label: "Delete",
                onClick: () =>
                  showSteamUiConfirm(ui, {
                    title: "Delete Theme",
                    text: `Are you sure you want to delete ${theme.displayName}?`,
                    confirmLabel: "Delete",
                    onConfirm: () => void themesAct("delete", { name: theme.name }),
                  }),
              },
            ]),
          ),
        ),
      ),
      (state.errors ?? []).length
        ? h(
            ui.settingsSection,
            { label: "Errors" },
            ...state.errors.map((error) =>
              h(
                "div",
                { key: error.folder, className: "wsgm-themes-error" },
                h("b", null, error.folder),
                h("span", null, error.error),
              ),
            ),
          )
        : null,
    );
  }
  function ThemesProfiles({ state }) {
    const ui = themesUi;
    const react = ui.react;
    const h = react.createElement;
    const presets = state.presets ?? [];
    const enabledCount = (state.themes ?? []).filter((theme) => theme.enabled).length;
    const NewProfile = "\u0000new";
    const choices = [
      ...(state.selectedPreset === "Invalid State"
        ? [{ value: "Invalid State", label: "Invalid State" }]
        : []),
      { value: "", label: "None" },
      ...presets.map((preset) => ({ value: preset.name, label: preset.displayName })),
      { value: NewProfile, label: "New Profile" },
    ];
    // A profile is named in a modal, as CSS Loader names one: the enabled themes and their settings
    // under one name.
    const change = (row, value, commit = true) => {
      if (!commit) return;
      if (value === NewProfile) {
        showSteamUiPrompt(ui, {
          title: "Create Profile",
          text: `This profile will combine all ${enabledCount} themes you currently have enabled. Enabling or disabling it will toggle them all at once.`,
          label: "Profile Name",
          confirmLabel: "Create",
          onConfirm: (name) => void themesAct("createProfile", { name }),
        });
        return;
      }
      themesRowChange(row, value);
    };
    return renderSteamUiPane(
      ui,
      {},
      h(
        ui.settingsSection,
        { label: "Profiles" },
        renderSteamSettingRow(
          ui,
          {
            key: themesKey("profile"),
            kind: "choice",
            label: "Selected Profile",
            description:
              "A profile turns a set of themes on with their settings, and off again together.",
            text: state.selectedPreset ?? "",
            choices,
          },
          undefined,
          change,
          () => {},
        ),
        ...presets.map((preset) =>
          h(
            "div",
            { key: preset.name, className: "wsgm-themes-profile" },
            h("span", null, preset.displayName),
            h("span", { className: "steam-ui-kit-muted" }, (preset.dependencies ?? []).join(", ")),
            renderSteamUiChips(ui, [
              {
                label: "Delete",
                onClick: () =>
                  showSteamUiConfirm(ui, {
                    title: "Delete Profile",
                    text: `Delete the profile ${preset.displayName}?`,
                    confirmLabel: "Delete",
                    onConfirm: () => void themesAct("delete", { name: preset.name }),
                  }),
              },
            ]),
          ),
        ),
      ),
    );
  }
  function ThemesSettings({ state }) {
    const ui = themesUi;
    const settings = state.settings ?? {};
    const rows = [
      {
        key: themesKey("setting", "enabled"),
        kind: "boolean",
        label: "Install themes into Steam",
        description: "Off leaves Steam's own styling and keeps every theme as it is.",
        checked: !!settings.enabled,
      },
      {
        key: themesKey("setting", "translationsBranch"),
        kind: "choice",
        label: "Class translations",
        description:
          "Steam renames its style classes with every client build; DeckThemes publishes the table that maps themes onto the current names.",
        text: settings.translationsBranch ?? "auto",
        choices: [
          {
            value: "auto",
            label: settings.steamBeta ? "Auto-Detect (beta)" : "Auto-Detect (stable)",
          },
          { value: "stable", label: "Force Stable" },
          { value: "beta", label: "Force Beta" },
        ],
      },
      {
        key: "translations",
        kind: "note",
        label: "Translations",
        text: settings.translations
          ? `${settings.translations} names${settings.translationsFetched ? `, fetched ${settings.translationsFetched}` : ""}`
          : "Not fetched yet",
      },
      { key: "path", kind: "note", label: "Themes folder", text: settings.themesPath ?? "" },
      { key: "link", kind: "note", label: "Steam's themes_custom", text: settings.steamLink ?? "" },
    ];
    const h = ui.react.createElement;
    return renderSteamUiPane(
      ui,
      {},
      h(
        ui.settingsSection,
        { label: "Themes" },
        ...rows.map((row) => renderSteamSettingRow(ui, row, undefined, themesRowChange, () => {})),
      ),
    );
  }
  /**
   * Renders theme browsing, installation, profiles and settings from host state.
   * @param context Registered page accessors for Steam components, latest state and publication refusal.
   * @returns The page React tree, including loading or refusal state when data is unavailable.
   */
  function ThemesPage({ context }) {
    const react = context.react();
    const h = react.createElement;
    const ui = context.ui();
    themesUi = ui;
    const state = context.state();
    if (!state) return renderSteamUiEmpty(react, context.refusal() ?? "Loading themes…");
    return renderSteamUiTabbedPage(ui, {
      id: "wsgm-themes",
      label: "Themes",
      style: themesStyles,
      tabs: themesTabs,
      ...wsgmPageFrame(themesAct, state),
      content: (id) => {
        switch (id) {
          case "installed":
            return h(ThemesInstalled, { state });
          case "profiles":
            return h(ThemesProfiles, { state });
          case "settings":
            return h(ThemesSettings, { state });
          default:
            return h(ThemesBrowse, { state });
        }
      },
    });
  }
  // The page's own layout: where the kit's elements go, not how they look.
  const themesStyles = `
#wsgm-themes .steam-ui-kit-tool:not(.grow) { width: 240px; }
#wsgm-themes .wsgm-themes-filter { display: flex; justify-content: space-between; width: 100%; gap: 12px; }
#wsgm-themes .steam-ui-kit-box-title svg { color: #ffd166; }
#wsgm-themes .wsgm-themes-manage { padding: 6px 0 12px; }
#wsgm-themes .wsgm-themes-profile { display: flex; align-items: center; gap: 12px; padding: 8px 0; }
#wsgm-themes .wsgm-themes-profile > span:first-child { font-size: 15px; color: #fff; }
#wsgm-themes .wsgm-themes-profile > .steam-ui-kit-muted { flex: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
#wsgm-themes .wsgm-themes-error { display: flex; flex-direction: column; gap: 2px; padding: 8px 10px; margin: 4px 0; border-radius: 2px; background: #f002; }
`;
  const themesPage = registerSteamPage({
    template: "themes",
    gate: "themes",
    patchId: ThemesPatchId,
    components: resolveSteamSettingsComponents,
    required: SteamUiTabbedPageRequired,
    status: () => ({ tab: themesPage.state()?.activeTab ?? "" }),
    Page: ThemesPage,
  });
  // @fragment consumer/wsgm-settings.ts
  // WSGM settings frontend; host descriptors and commands are shared with the overlay.
  const WsgmSettingsPatchId = "wsgm.settings";
  const WsgmSettingsRoute = "/wsgm/settings";
  /**
   * Renders host settings sections with the shared native settings renderer.
   * @param context Registered page accessors for Steam components, latest state and publication refusal.
   * @returns The page React tree, including loading or refusal state when data is unavailable.
   */
  function WsgmSettingsPage({ context }) {
    const state = context.state() ?? {};
    return renderSteamSettings(context.ui(), {
      route: WsgmSettingsRoute,
      pages: state.pages ?? [],
      revision: state.revision ?? 0,
      // The request is the answer: the renderer drops a refused row's draft and shows why on that row.
      onChange: (row, value) => request(WsgmSettingsPatchId, "set", { key: row.key, value }),
      // No row on this page is an action.
      onAction: () => {},
    });
  }
  const wsgmSettingsPage = registerSteamPage({
    template: "wsgm-settings",
    gate: "wsgmSettings",
    patchId: WsgmSettingsPatchId,
    components: resolveSteamSettingsComponents,
    required: SteamSettingsRequired,
    status: () => ({ pages: wsgmSettingsPage.state()?.pages?.length ?? 0 }),
    Page: WsgmSettingsPage,
  });
  // @fragment epilogue.ts
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
