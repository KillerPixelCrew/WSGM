// Reads the device class's Update/getDeviceVolume/setDeviceVolume so the exact shape that fills
// m_mapVolumes is taken from the client rather than guessed. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([[Symbol("wsgm-audio-update-probe")], {}, (r) => { runtime = r; }]);
  if (!runtime) return "no runtime";
  const store = runtime("1409")?.F5;
  const device = store?.m_mapAudioDevices?.get(1);
  if (!device) return "no device 1";
  const proto = Object.getPrototypeOf(device);
  const out = {};
  for (const name of ["Update", "getDeviceVolume", "setDeviceVolume", "OnVolumeUpdated"]) {
    try { out[name] = String(proto[name]).slice(0, 700); } catch (e) { out[name] = "ERR " + e; }
  }
  try { out.registerOrUpdate = String(Object.getPrototypeOf(store).RegisterOrUpdateDevice).slice(0, 600); } catch {}
  try { out.onVolumeChanged = String(Object.getPrototypeOf(store).OnAudioDeviceVolumeChanged).slice(0, 600); } catch {}
  return JSON.stringify(out, null, 1);
})();
