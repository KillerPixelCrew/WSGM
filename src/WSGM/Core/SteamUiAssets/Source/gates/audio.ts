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
  let originalStoreState: {
    available: boolean;
    output: number;
    input: number;
  } | null = null;
  // Steam's audio identities are NUMBERS: the live store keeps m_activeOutputDeviceId as a
  // uint32 with 0xFFFFFFFF for none (read off the running client, 2026-08-30). WSGM's endpoint
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
  // sliders bind — omit them and every slider renders a grey bar over undefined. WSGM's volume is
  // system-wide, so every device carries the same value; Windows' default endpoint is the one the
  // user actually hears, and a per-device number WSGM cannot move would be an invented control.
  const toDevice = (entry, flVolume) => ({
    id: numberFor(entry.id),
    sName: entry.name,
    bHasOutput: entry.hasOutput === true,
    bHasInput: entry.hasInput === true,
    // WSGM reports ONE volume: the default OUTPUT endpoint's. Copying it into the input field
    // made Steam's microphone slider show the speaker volume and made the two move together,
    // because both were the same number written twice. A capture endpoint's own volume needs a
    // backend WSGM does not have yet, and until it does the honest answer is no value rather
    // than the wrong one.
    flOutputVolume: entry.hasOutput === true ? flVolume : undefined,
    flInputVolume: undefined,
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

  // The one volume WSGM tracks, as the 0..1 float Steam's sliders use.
  const flVolumeOf = (state) => Math.min(1, Math.max(0, (Number(state?.volumePercent) || 0) / 100));

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
    const removed = known.filter((id) => !seen.includes(id));

    // Removals first: a device that has gone must leave the store before a re-read of the device
    // list can describe the set as complete, or the picker keeps an endpoint that is not there.
    for (const id of removed) {
      if (callbacks.deviceRemoved) callbacks.deviceRemoved(id as never);
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
      if (volumeChanged && device.hasOutput === true && callbacks.deviceVolumeChanged) {
        const id = numberFor(device.id);
        callbacks.deviceVolumeChanged(
          id as never,
          AudioDirection.Output as never,
          flVolume as never,
        );
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
        // The OUTPUT direction only, and only on a device that has one. This is the single volume
        // WSGM observes; writing it to the input direction as well is what put the speaker volume
        // on the microphone slider and made the two move as one.
        for (const direction of device.hasOutput === true ? [AudioDirection.Output] : []) {
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
    originalStoreState = null;
    try {
      if (window.SteamClient?.System?.Audio?.[ownedMarker] === true) {
        delete window.SteamClient.System.Audio;
      }
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
