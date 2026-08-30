(() => {
  const out = {};
  out.bridge = Object.keys(window).filter((k) => k.indexOf("__wsgmSteamUi") === 0);
  const s = window.SteamClient && window.SteamClient.System;
  out.audio = !!(s && s.Audio);
  out.perf = !!(s && s.Perf);
  return JSON.stringify(out);
})();
