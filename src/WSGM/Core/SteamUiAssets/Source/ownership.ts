// Ownership claims: the one primitive every gate needs and every gate used to hand-roll.
//
// A gate changes something the client owns. Three things then have to be true, and getting any of
// them wrong has cost a device session:
//
//   1. It can recognise its OWN work. A probe that cannot tell "already ours" from "someone else's"
//      either refuses forever or overwrites a value that was never ours to change. Worse, a probe
//      that requires the pre-patch condition its own apply invalidates tears the patch down every
//      poll — the self-incompatibility teardown loop, paid for three times (the audio namespace,
//      the network getter, the brightness flag, whose row flickered on a ~25-second cycle).
//   2. It can hand back EXACTLY what was there. Keeping the original only in the installing
//      closure restores `undefined` to a bridge replaced in place, and Steam's `?? true` hooks then
//      keep a row visible after removal.
//   3. Both facts survive a separate CDP evaluation. Probes run in their own call, so the marker
//      must be a string key on the object — a Symbol from this scope is not reachable from there.
//
// Every claim below therefore writes two non-enumerable fields: a marker saying this is ours, and
// the original it displaced. Callers supply their own key names so no existing marker changes
// meaning; a renamed key would orphan the marker a previous build left on a running client.

type ClaimKeys = {
  // Set to true on the claimed object. "Is this ours?"
  readonly marker: string;
  // Holds what was displaced. "What do we hand back?"
  readonly original: string;
};

type ClaimOutcome = { ok: true; reclaimed: boolean } | { ok: false; error: string };

const defineHidden = (host: object, key: string, value: unknown) => {
  Object.defineProperty(host, key, {
    value,
    configurable: true,
    enumerable: false,
    writable: false,
  });
};

const claimed = (host: unknown, keys: ClaimKeys) =>
  !!host && (host as Record<string, unknown>)[keys.marker] === true;

// Claims a plain data field — a flag or value the client set, that a gate replaces.
//
// `absent` is what the field reads as when nothing has claimed it. It is required rather than
// inferred: reclaiming a previous bridge's work has to restore what THAT bridge displaced, and when
// the stored original is missing the only honest answer is the value the client would have had.
const claimValue = (
  host: Record<string, unknown> | null,
  field: string,
  keys: ClaimKeys,
  next: unknown,
  absent: unknown,
): ClaimOutcome => {
  if (!host || !(field in host)) {
    return { ok: false, error: "claim target unavailable" };
  }
  const reclaimed = claimed(host, keys);
  // Already at the target value and NOT marked means the client did this itself. Refusing is
  // correct: there is nothing to add, and restoring later would hand back a value we invented.
  if (!reclaimed && host[field] === next) {
    return { ok: false, error: "already set by the client" };
  }
  try {
    const original = reclaimed
      ? Object.hasOwn(host, keys.original)
        ? host[keys.original]
        : absent
      : host[field];
    host[field] = next;
    defineHidden(host, keys.marker, true);
    defineHidden(host, keys.original, original);
    return { ok: true, reclaimed };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
};

// Hands a claimed field back. Releasing something never claimed is success, not an error: a gate
// that failed halfway must be able to unwind without knowing how far it got.
const releaseValue = (
  host: Record<string, unknown> | null,
  field: string,
  keys: ClaimKeys,
): { ok: boolean; error?: string } => {
  if (!host || !claimed(host, keys)) return { ok: true };
  try {
    host[field] = host[keys.original];
    delete host[keys.marker];
    delete host[keys.original];
    return { ok: true };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
};

// Claims a member — a method a gate overlays, or a namespace it supplies where the client has
// none. The marker goes on the REPLACEMENT rather than the host, so `status` can ask the live
// object whether what is installed is ours without consulting any closure.
const claimMember = (
  host: Record<string, unknown> | null,
  member: string,
  keys: ClaimKeys,
  replacement: (original: unknown) => unknown,
): ClaimOutcome => {
  if (!host) {
    return { ok: false, error: "claim host unavailable" };
  }
  const current = host[member];
  const reclaimed = claimed(current, keys);
  try {
    const original = reclaimed
      ? (current as Record<string, unknown>)[keys.original]
      : current;
    const next = replacement(original) as Record<string, unknown>;
    // Functions as well as objects: every member claim so far replaces a METHOD, and `typeof` a
    // function is "function", not "object". Excluding it left the replacement unmarked, so the
    // release found nothing of ours and handed nothing back — the overlay outlived its own
    // removal.
    if (!next || (typeof next !== "object" && typeof next !== "function")) {
      return { ok: false, error: "claim replacement cannot carry its marker" };
    }
    defineHidden(next, keys.marker, true);
    defineHidden(next, keys.original, original);
    host[member] = next;
    return { ok: true, reclaimed };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
};

// Hands a claimed member back to whatever it displaced. A member that was absent before the claim
// is deleted rather than set to undefined, so `member in host` reads as it did.
const releaseMember = (
  host: Record<string, unknown> | null,
  member: string,
  keys: ClaimKeys,
): { ok: boolean; error?: string } => {
  if (!host) return { ok: true };
  const current = host[member];
  if (!claimed(current, keys)) return { ok: true };
  try {
    const original = (current as Record<string, unknown>)[keys.original];
    if (original === undefined) {
      delete host[member];
    } else {
      host[member] = original;
    }
    return { ok: true };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
};

const memberClaimed = (
  host: Record<string, unknown> | null | undefined,
  member: string,
  keys: ClaimKeys,
) => claimed(host?.[member], keys);

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
const supplyNamespace = (
  host: Record<string, unknown> | null,
  name: string,
  marker: string,
  factory: () => object,
): ClaimOutcome => {
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
const withdrawNamespace = (
  host: Record<string, unknown> | null | undefined,
  name: string,
  marker: string,
): { ok: boolean; error?: string } => {
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
const claimAccessor = (
  host: object | null,
  property: string,
  keys: ClaimKeys,
  getter: () => unknown,
): ClaimOutcome => {
  if (!host) {
    return { ok: false, error: "claim host unavailable" };
  }
  const descriptor = Object.getOwnPropertyDescriptor(host, property);
  if (!descriptor || descriptor.configurable !== true) {
    return { ok: false, error: "property is not configurable" };
  }
  try {
    const reclaimed = claimed(descriptor.get, keys);
    const original = reclaimed
      ? (descriptor.get as unknown as Record<string, unknown>)[keys.original]
      : descriptor;
    defineHidden(getter, keys.marker, true);
    defineHidden(getter, keys.original, original);
    Object.defineProperty(host, property, { get: getter, configurable: true });
    return { ok: true, reclaimed };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
};

// Restores the descriptor a claimed accessor displaced.
const releaseAccessor = (
  host: object | null,
  property: string,
  keys: ClaimKeys,
): { ok: boolean; error?: string } => {
  if (!host) return { ok: true };
  try {
    const descriptor = Object.getOwnPropertyDescriptor(host, property);
    if (!claimed(descriptor?.get, keys)) return { ok: true };
    const original = (descriptor!.get as unknown as Record<string, unknown>)[keys.original];
    if (original) {
      Object.defineProperty(host, property, original as PropertyDescriptor);
    }
    return { ok: true };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
};
