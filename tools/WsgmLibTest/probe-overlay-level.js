// Prints the frame-limit row component in full, to see how it turns the option list into slider
// notches and whether a long list is rendered as labels or as a plain range. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([
    [Symbol("wsgm-framelimit-probe")],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  if (!runtime) return "no runtime";
  const mod = runtime("83571");
  const hits = {};
  for (const key of Object.keys(mod)) {
    let src = "";
    try {
      src = String(mod[key]);
    } catch {
      continue;
    }
    if (src.includes("LimitFrameRate") || src.includes("FramerateLimit")) hits[key] = src;
  }
  return JSON.stringify(hits, null, 1);
})();
