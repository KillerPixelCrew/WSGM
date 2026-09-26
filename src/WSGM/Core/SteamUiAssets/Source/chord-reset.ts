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
// default selection while the host says the mirror is active. Removal puts the original function
// back, and only if the wrapper is still the one installed.
function createWsgmChordReset() {
  const patchId = "wsgm.chord-reset";
  const ChordAppId = 443510;

  let installed = false;
  let active = false;
  let hooked = false;
  let original: any = null;
  let unsubscribe: (() => void) | null = null;
  let lastError = "";
  let resets = 0;

  const input = () => (globalThis as any).SteamClient?.Input;

  const hook = () => {
    if (hooked) return true;
    const target = input();
    if (!target || typeof target.SetSelectedConfigForApp !== "function") {
      lastError = "SteamClient.Input.SetSelectedConfigForApp is absent";
      return false;
    }
    const wrapped = target.SetSelectedConfigForApp;
    const wrapper = function (this: any, appId, controllerIndex, url, ...rest) {
      if (
        active &&
        Number(appId) === ChordAppId &&
        typeof url === "string" &&
        url.startsWith("default://")
      ) {
        resets++;
        request(patchId, "reset", null).catch(() => {});
      }
      return wrapped.apply(this, [appId, controllerIndex, url, ...rest]);
    };
    (wrapper as any).__wsgmWrapped = wrapped;
    target.SetSelectedConfigForApp = wrapper;
    original = wrapped;
    hooked = true;
    return true;
  };

  const unhook = () => {
    if (!hooked) return;
    const target = input();
    if (target && target.SetSelectedConfigForApp?.__wsgmWrapped === original) {
      target.SetSelectedConfigForApp = original;
    }
    original = null;
    hooked = false;
  };

  const install = () => {
    if (installed) return { ok: true, installed: true };
    if (!hook()) return { ok: false, error: lastError };
    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, (state) => {
      active = !!state?.active;
    });
    return { ok: true, installed: true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    active = false;
    unhook();
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    hooked,
    active,
    subscribed: !!unsubscribe,
    resets,
    lastError,
  });

  return { install, remove, status };
}

registerGate("wsgmChordReset", createWsgmChordReset());
