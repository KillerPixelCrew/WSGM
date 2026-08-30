// Reads module 96555's display-info query: its key, its fetch, and what it currently answers.
// Read-only.
(() => {
  const chunk = window.webpackChunksteamui;
  if (!chunk) return "no webpackChunksteamui";
  let runtime = null;
  chunk.push([[Symbol("wsgm-vrr-query2-probe")], {}, (r) => { runtime = r; }]);
  if (!runtime || !runtime.m) return "no runtime";
  const src = String(runtime.m["96555"]);
  const out = { length: src.length, queryKeys: [...new Set(src.match(/queryKey:[^,}]{0,160}/g) ?? [])] };
  const at = src.indexOf("function y()");
  out.around = src.slice(Math.max(0, at - 1800), at + 200);
  return JSON.stringify(out, null, 1);
})();
