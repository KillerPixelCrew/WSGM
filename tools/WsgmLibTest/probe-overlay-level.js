// Prints module 74514's refresh-rate hook in full, to see how the external-display gate combines
// with the manual-refresh availability flag. Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([
    [Symbol("wsgm-refresh-probe")],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  if (!runtime) return "no runtime";
  return String(runtime("74514").zn);
})();
