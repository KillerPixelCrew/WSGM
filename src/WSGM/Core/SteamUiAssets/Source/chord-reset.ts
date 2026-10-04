// The guide button chord layout's "reset to defaults", reported to the host.
//
// WSGM keeps Steam's last-resort chord template (`controller_base/chord_neptune.vdf`) equal to the
// user's autosaved layout, because Steam's editor reloads that template after every autosave for a
// Steam Deck type controller and threw the edits away (see SteamGuideChordMirror). With the template
// mirrored, the editor's reset loads the mirror instead of Valve's defaults. The editor resets by
// calling SteamClient.Input.SetSelectedConfigForApp(443510, controllerIndex, "default://…") from
// Steam's configurator store in this context, three seconds before it reloads, so the call is the
// place to tell the host to put Valve's file back in time.
//
// The wrapper forwards every call unchanged and only sends the command for the chord pseudo-app's
// default selection while the host says the mirror is active. It is a member claim, so a bridge
// replaced without its dispose (a JS context reload) reclaims the wrapper it left instead of wrapping
// it again, and removal hands back exactly the function it displaced.
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
