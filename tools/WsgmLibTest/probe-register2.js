(() => {
  const b = window.__wsgmSteamUi_v1_28d7c54a;
  const out = { install: {} };
  for (const kind of ["tdp","autoTdp","frameLimit","overlayLevel","controllerTarget",
    "resolution","valveVrr","valveProfileHeader","valveReset","valveRefreshRate"]) {
    try { out.install[kind] = b.nativeComponents.install(kind).ok; }
    catch (e) { out.install[kind] = String(e); }
  }
  return JSON.stringify(out);
})();
