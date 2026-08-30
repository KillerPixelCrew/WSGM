// Pins Steam's audio direction enum: which direction value the QAM volume slider passes to
// setDeviceVolume, and which key the output volume actually sits under. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([[Symbol("wsgm-direction-probe")], {}, (r) => { runtime = r; }]);
  if (!runtime || !runtime.m) return "no runtime";
  const out = { enumCandidates: [], sliderCallers: [] };
  for (const [id, factory] of Object.entries(runtime.m)) {
    const src = String(factory);
    // Enum shapes: assignments like Output=..., AllOutput=..., next to Input=...
    if (/AllOutput/.test(src) || /EAudioDirection/.test(src)) {
      const hits = [...new Set(src.match(/\w*(?:Input|Output)\w*\]=\d+|\d+\]="\w*(?:Input|Output)\w*"|(?:Input|Output)\w*[:=]\s*\d+/g) ?? [])].slice(0, 12);
      if (hits.length) out.enumCandidates.push({ id, hits });
    }
    // Callers of setDeviceVolume with a literal direction.
    const calls = [...new Set(src.match(/setDeviceVolume\([^)]{0,60}\)/g) ?? [])];
    if (calls.length) out.sliderCallers.push({ id, calls: calls.slice(0, 6) });
  }
  // And the ground truth: which KEY holds the value in the live map after Steam itself wrote one.
  const store = runtime("1409")?.F5;
  const volumes = [];
  try {
    store?.m_mapAudioDevices?.forEach((d, id) => {
      const map = {};
      d.m_mapVolumes?.forEach((v, k) => (map[k] = v));
      volumes.push({ id, map });
    });
  } catch (e) { volumes.push("ERR " + e); }
  out.liveVolumes = volumes;
  return JSON.stringify(out, null, 1);
})();
