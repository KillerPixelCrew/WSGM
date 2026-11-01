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
  } as const;

  let installed = false;
  let active = false;
  let unsubscribe: (() => void) | null = null;
  let lastError = "";
  let resets = 0;

  const input = () => (globalThis as any).SteamClient?.Input ?? null;

  const install = () => {
    if (installed) return { ok: true, installed: true };
    const target = input();
    if (!target || typeof target[member] !== "function") {
      lastError = "SteamClient.Input.SetSelectedConfigForApp is absent";
      return { ok: false, error: lastError };
    }
    const claim = claimMember(target, member, claimKeys, (original: any) =>
      function (this: any, appId, controllerIndex, url, ...rest) {
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

  // The standard probe answer: the member this hook claims, answered from the claim while it holds.
  // Steam's modules must be loaded, as everything the hook reports to runs inside them.
  const probe = () => {
    if (installed) return { ok: true, outcome: "ready", installed: true, facts: { input: true } };
    try {
      getWebpackRuntime("chord-reset");
    } catch (error) {
      return { ok: false, outcome: "notReady", reason: `Steam modules unavailable: ${String(error)}` };
    }
    const present = typeof input()?.[member] === "function";
    return present
      ? { ok: true, outcome: "ready", facts: { input: true } }
      : {
          ok: false,
          outcome: "incompatible",
          reason: "SteamClient.Input.SetSelectedConfigForApp is absent",
          facts: { input: false },
        };
  };

  const status = () => {
    const hooked = memberClaimed(input(), member, claimKeys);
    return {
      ok: true,
      installed,
      hooked,
      active,
      subscribed: !!unsubscribe,
      // The standard status: the claim and the subscription are what this gate holds.
      held: installed && hooked && !!unsubscribe,
      claims: (hooked ? 1 : 0) + (unsubscribe ? 1 : 0),
      resets,
      lastError,
    };
  };

  return { install, remove, status, probe };
}

registerGate("wsgmChordReset", createWsgmChordReset());
