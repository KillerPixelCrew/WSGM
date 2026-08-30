// Reads the store's volume-overlay suppression pair and the current per-device volume map, so the
// publish rule can be written against what the store actually holds. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([[Symbol("wsgm-audio-suppress-probe")], {}, (r) => { runtime = r; }]);
  if (!runtime) return "no runtime";
  const store = runtime("1409")?.F5;
  if (!store) return "no F5";
  const proto = Object.getPrototypeOf(store);
  const out = {};
  for (const name of ["SuppressVolumeOverlay", "UnSuppressVolumeOverlay", "OnVolumeButtonPressed"]) {
    try { out[name] = String(proto[name]).slice(0, 400); } catch (e) { out[name] = "ERR " + e; }
  }
  const volumes = [];
  try {
    store.m_mapAudioDevices?.forEach((d, id) => {
      volumes.push({ id, out: d.getDeviceVolume?.(0), in: d.getDeviceVolume?.(1) });
    });
  } catch (e) { volumes.push("ERR " + e); }
  out.volumes = volumes;
  return JSON.stringify(out, null, 1);
})();
