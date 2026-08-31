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
  const patchId = "wsgm.steam-network.gate";
  const getterMarker = "__wsgmOwnedGetter";
  const originalGetterField = "__wsgmOriginalGetterDescriptor";
  const scanMarker = "__wsgmOwnedNetworkScan";
  const originalScanField = "__wsgmOriginalNetworkScan";
  let original: PropertyDescriptor | undefined;
  let target: object | null = null;
  let lastError = "";
  let scanWrapped = false;
  let originalStart: ((...args: unknown[]) => unknown) | null = null;
  let originalStop: ((...args: unknown[]) => unknown) | null = null;
  let unsubscribe: (() => void) | null = null;
  let syntheticKeys: string[] = [];

  const store = () => {
    try {
      const req = getWebpackRuntime("network-store");
      return req?.("77347")?.OQ?.Get() ?? null;
    } catch {
      return null;
    }
  };

  const removeNetworkState = (refresh: boolean) => {
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
    const proto = Object.getPrototypeOf(instance);
    const descriptor = Object.getOwnPropertyDescriptor(proto, property);
    if (!descriptor || descriptor.configurable !== true) {
      lastError = "network availability getter is not configurable";
      return { ok: false, error: lastError };
    }

    try {
      // Marked as ours for the same reason the namespaces are: the compatibility probe checks
      // that the getter currently reads false, and a successful override makes it read true. Left
      // unmarked, the patch reads its own success as "the client already reports this available,
      // stand aside", declares itself incompatible, and tears down — taking the network list with
      // it.
      const owned = () => true;
      const originalDescriptor =
        descriptor.get?.[getterMarker] === true ? descriptor.get[originalGetterField] : descriptor;
      Object.defineProperty(owned, getterMarker, {
        value: true,
        configurable: true,
        enumerable: false,
      });
      Object.defineProperty(owned, originalGetterField, {
        value: originalDescriptor,
        configurable: true,
        enumerable: false,
      });
      Object.defineProperty(proto, property, { get: owned, configurable: true });
      original = originalDescriptor;
    } catch (error) {
      lastError = String(error);
      return { ok: false, error: lastError };
    }
    target = proto;
    lastError = "";
    wrapScanning();
    unsubscribe = subscribe(patchId, onState);
    return { ok: true, installed: true, available: instance[property] === true };
  };

  // Steam's own UI calls these when its network page opens and closes, so they are exactly the
  // signal for when a scan is worth running. WSGM's radio manager is otherwise driven by WSGM's
  // own panel, and a list refreshed only then would be stale on Steam's page — which is worse
  // than an empty one, because the user picks a network that is gone and the join fails silently.
  //
  // Both originals are always called through: this observes the lifetime, it does not take it
  // over, so a client that grows a working backend keeps behaving exactly as before.
  const wrapScanning = () => {
    const net = window.SteamClient?.System?.Network;
    if (!net || scanWrapped) return;
    const wrap = (name: string, command: string) => {
      const current = net[name];
      const inner = current?.[scanMarker] === true ? current[originalScanField] : current;
      if (typeof inner !== "function") return null;
      const wrapped = function (this: unknown, ...args: unknown[]) {
        // A scan request that cannot reach WSGM must not stop Steam's own call. Promise
        // rejection is handled explicitly; a try/catch only sees synchronous construction.
        void request(patchId, command, null).catch(() => {});

        return inner.apply(this, args);
      };
      Object.defineProperty(wrapped, scanMarker, {
        value: true,
        configurable: true,
        enumerable: false,
      });
      Object.defineProperty(wrapped, originalScanField, {
        value: inner,
        configurable: true,
        enumerable: false,
      });
      net[name] = wrapped;
      return inner;
    };

    originalStart = wrap("StartScanningForNetworks", "startScan");
    originalStop = wrap("StopScanningForNetworks", "stopScan");
    scanWrapped = !!(originalStart || originalStop);
  };

  const unwrapScanning = () => {
    const net = window.SteamClient?.System?.Network;
    if (!net || !scanWrapped) return;
    if (net.StartScanningForNetworks?.[scanMarker] === true && originalStart) {
      net.StartScanningForNetworks = originalStart;
    }
    if (net.StopScanningForNetworks?.[scanMarker] === true && originalStop) {
      net.StopScanningForNetworks = originalStop;
    }
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
    if (!target || !original) return { ok: true, absent: true };
    try {
      const current = Object.getOwnPropertyDescriptor(target, property);
      if (current?.get?.[getterMarker] === true) {
        Object.defineProperty(target, property, original);
      }
    } catch (error) {
      lastError = String(error);
      return { ok: false, error: lastError };
    }

    target = null;
    original = undefined;
    return { ok: true, removed: true };
  };

  const status = () => {
    const instance = store();
    return {
      ok: true,
      installed: !!target,
      available: instance ? instance[property] === true : false,
      // Reported because the row can be on while the list is empty: Steam's Windows backend
      // never populates wireless.aps, so an access point count of zero here means WSGM has not
      // supplied one, not that the machine cannot see any networks.
      accessPoints: Array.isArray(instance?.accessPoints) ? instance.accessPoints.length : -1,
      hasWirelessDevice: instance?.hasWirelessDevice === true,
      scanWrapped,
      lastError,
    };
  };

  return { install, remove, status };
}
