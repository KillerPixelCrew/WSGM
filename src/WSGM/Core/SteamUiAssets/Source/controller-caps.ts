// The virtual controller's capabilities, as Steam's UI sees them.
//
// Steam's controller pages decide what to draw from each controller's capability bits, which the
// client reports per controller type: a Steam Deck controller always carries ATTRIBCAP_TRACKPAD and
// ATTRIBCAP_CAPJOYSTICK, so WSGM's Steam Deck target puts trackpad and stick-touch settings in front
// of a handheld that has neither. The glyph stylesheet hides the rows it can anchor on a glyph, but
// the configurator's quick settings ("right trackpad behavior", its sensitivity and inversion) are
// plain labelled fields with nothing to anchor, and every such list grows with each client build.
//
// Every store reads the list through one generated RPC namespace, SteamInputManager.GetControllerList,
// and converts each entry's `capabilities` with BigInt. This wraps that one function and clears the
// bits the host names on the controller the host names (its vendor and product id), so the pages
// draw the handheld the device plugin describes. The native side and the layouts are untouched: the
// mask only changes what this UI process believes. After hooking, unhooking or a mask change, the
// three stores that cache the list are asked to query it again, the same call they make on Steam's
// own list-changed notification.
function createWsgmControllerCaps() {
  const patchId = "wsgm.controller-caps";
  const ServiceTokens = ["SteamInputManager.GetControllerList#1", "GetControllerListHandler"];
  const isService = (value) =>
    !!value &&
    typeof value === "object" &&
    typeof value.GetControllerList === "function" &&
    typeof value.RegisterForNotifyControllerListChanged === "function";
  // The stores that hold a copy of the list: the controller store, the configurator store and the
  // gamepad input store. Each is found by what it is; a store that has moved is skipped, not guessed.
  const StoreFingerprints: Array<[string[], (value: any) => boolean]> = [
    [
      ["GetControllerBySerial", "m_unboundControllerList"],
      (value) => typeof value?.GetControllerBySerial === "function" && typeof value?.DoControllerListQuery === "function",
    ],
    [
      ["m_pendingEditingConfiguration", "EnsureEditingConfiguration"],
      (value) =>
        typeof value?.EnsureEditingConfiguration === "function" && typeof value?.DoControllerListQuery === "function",
    ],
    [
      ["OnControllerListChanged", "activeButtons"],
      (value) =>
        typeof value?.OnControllerListChanged === "function" && typeof value?.DoControllerListQuery === "function",
    ],
  ];

  let installed = false;
  let hooked = false;
  let service: any = null;
  let original: any = null;
  let unsubscribe: (() => void) | null = null;
  let resolver: any = null;
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
    if (mask === 0n || !list || typeof list !== "object" || !Array.isArray(list.controllers)) return list;
    for (const controller of list.controllers) {
      if (!controller || Number(controller.vendor_id) !== vendorId || Number(controller.product_id) !== productId) continue;
      let caps: bigint;
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
    if (!response || typeof response.Body !== "function" || typeof response.BSuccess !== "function") return response;
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
    if (hooked) return true;
    try {
      resolver ??= getWebpackRuntime("controller-caps");
      service = resolver.exported(ServiceTokens, isService);
    } catch (error) {
      lastError = String(error);
      return false;
    }
    const wrapped = service.GetControllerList;
    const wrapper = function (this: any, ...args) {
      const result = wrapped.apply(this, args);
      return result && typeof result.then === "function" ? result.then(maskResponse) : maskResponse(result);
    };
    (wrapper as any).__wsgmWrapped = wrapped;
    service.GetControllerList = wrapper;
    original = wrapped;
    hooked = true;
    return true;
  };

  const unhook = () => {
    if (!hooked) return;
    if (service && service.GetControllerList?.__wsgmWrapped === original) {
      service.GetControllerList = original;
    }
    service = null;
    original = null;
    hooked = false;
  };

  const refresh = () => {
    if (!resolver) return;
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

  const remove = () => {
    if (!installed) return { ok: true, absent: true };
    installed = false;
    unsubscribe = endSubscription(unsubscribe);
    unhook();
    mask = 0n;
    refresh();
    return { ok: true, removed: true };
  };

  const status = () => ({
    ok: true,
    installed,
    hooked,
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
