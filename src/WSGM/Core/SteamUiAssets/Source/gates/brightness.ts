// Not availability-only, despite the founding comment that said Steam's own backend works on
// Windows. It does not — device-disproved 2026-08-30: SetBrightness is a native stub and
// RegisterForBrightnessChanges never fires, so the store's observable sits at its constructed 1
// and the revealed slider moves nothing. WSGM is the backend: the gate forwards the slider's
// writes over the bridge and feeds the store's observable from the published state, both through
// the same \\.\LCD interface the host owns.
function createBrightnessGate() {
  const patchId = "wsgm.steam-display.brightness";
  const field = "is_display_brightness_available";
  // A string key on the settings message, because the probe reads it from a separate CDP
  // evaluation where nothing from this scope is reachable. Without it this gate ran the
  // self-incompatibility teardown loop the audio namespace already paid for: the probe required
  // the flag to be hidden, a successful apply made it visible, and the patch manager tore down
  // its own work every poll — the row flickered in and out on a ~25-second cycle on the device.
  const revealedMarker = "__wsgmBrightnessRevealed";
  const originalValueField = "__wsgmOriginalBrightnessAvailability";
  const ownedSetterMarker = "__wsgmOwnedSetBrightness";
  const originalSetterField = "__wsgmOriginalSetBrightness";
  let originalValue: unknown;
  let installed = false;
  let lastError = "";
  let unsubscribe: (() => void) | null = null;
  let lastPercent: number | null = null;

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
    if (!Number.isInteger(percent) || percent < 0 || percent > 100) return;
    // Same rule as the volume: write only when WSGM's OWN reading moved, so a publish that
    // merely restates the level never fights a drag the store is already ahead on.
    if (percent === lastPercent) return;
    lastPercent = percent;
    try {
      const observable = displayStore()?.m_flDisplayBrightness;
      if (observable?.Set && Math.abs((observable.m_currentValue ?? -1) - percent / 100) > 0.004) {
        observable.Set(percent / 100);
      }
    } catch (error) {
      lastError = "brightness state apply failed: " + String(error);
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

    const existing = display.SetBrightness;
    const original =
      existing[ownedSetterMarker] === true ? existing[originalSetterField] : existing;
    const overlaid = (flBrightness) => {
      const percent = Math.round(Math.min(1, Math.max(0, Number(flBrightness) || 0)) * 100);
      // Remembered as ours so the echo of this very write coming back as state does not Set the
      // observable again underneath the drag.
      lastPercent = percent;
      return request(patchId, "setBrightness", { percent }).catch(() => {});
    };
    Object.defineProperty(overlaid, ownedSetterMarker, {
      value: true,
      configurable: true,
      enumerable: false,
    });
    Object.defineProperty(overlaid, originalSetterField, {
      value: original,
      configurable: true,
      enumerable: false,
    });
    try {
      display.SetBrightness = overlaid;
    } catch (error) {
      lastError = String(error);
      return false;
    }

    return true;
  };

  const restoreSetter = () => {
    try {
      const display = window.SteamClient?.System?.Display;
      const current = display?.SetBrightness;
      if (current?.[ownedSetterMarker] === true) {
        display.SetBrightness = current[originalSetterField];
      }
    } catch (error) {
      lastError = String(error);
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
    // the flag would mean restoring a value that was never ours to change. Available AND MARKED
    // is different: that is this gate's own earlier reveal, surviving a bridge replaced in
    // place, and refusing it is the teardown trap.
    if (message[field] === true && message[revealedMarker] !== true) {
      lastError = "brightness already available";
      return { ok: false, error: lastError };
    }

    try {
      // Reclaiming a previous bridge's reveal must also recover what that bridge replaced.
      // Keeping the value only in the old closure restored `undefined`; Steam's `?? true`
      // availability hook then kept the row visible after removal.
      originalValue =
        message[revealedMarker] === true && Object.hasOwn(message, originalValueField)
          ? message[originalValueField]
          : message[revealedMarker] === true
            ? false
            : message[field];

      message[field] = true;
      Object.defineProperty(message, revealedMarker, {
        value: true,
        configurable: true,
        enumerable: false,
        writable: false,
      });
      Object.defineProperty(message, originalValueField, {
        value: originalValue,
        configurable: true,
        enumerable: false,
        writable: false,
      });
    } catch (error) {
      lastError = String(error);
      return { ok: false, error: lastError };
    }

    if (!overrideSetter()) {
      // Revealing a slider whose writes go into the stub is the broken state this gate shipped
      // with; the reveal is undone rather than left half-working.
      try {
        message[field] = originalValue;
        delete message[revealedMarker];
        delete message[originalValueField];
      } catch {}
      return { ok: false, error: lastError };
    }

    installed = true;
    lastError = "";
    unsubscribe = subscribe(patchId, onState);
    return { ok: true, installed: true, available: message[field] === true };
  };

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    const message = settings();
    installed = false;
    if (unsubscribe) {
      unsubscribe();
      unsubscribe = null;
    }

    restoreSetter();
    if (!message) return { ok: true, removed: true, storeGone: true };
    try {
      message[field] = originalValue;
      delete message[revealedMarker];
      delete message[originalValueField];
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
      setterOwned: window.SteamClient?.System?.Display?.SetBrightness?.[ownedSetterMarker] === true,
      lastPercent,
      observable: displayStore()?.m_flDisplayBrightness?.m_currentValue ?? null,
      lastError,
    };
  };

  return { install, remove, status };
}
