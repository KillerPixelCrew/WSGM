// Fully unwrap every jsx/jsxs wrapper this prototype installed, then re-render.
(() => {
  try {
    const W = window.__wsgmQS;
    if (!W || !W.req) return "nothing to unpatch";
    function unwrap(fn) {
      let n = 0;
      while (fn && fn.__wsgmOrig) {
        fn = fn.__wsgmOrig;
        n++;
      }
      return [fn, n];
    }
    let restored = 0;
    for (const id of Object.keys(W.req.m)) {
      let e;
      try {
        e = W.req(id);
      } catch (x) {
        continue;
      }
      if (!e || typeof e !== "object") continue;
      for (const k of ["jsx", "jsxs"]) {
        try {
          if (typeof e[k] === "function" && e[k].__wsgmOrig) {
            const [orig, depth] = unwrap(e[k]);
            e[k] = orig;
            restored += depth;
          }
        } catch (x) {}
      }
    }
    W.patchedRuntimes = null;
    if (W.refresh) W.refresh();
    return JSON.stringify({ unwrapped: restored });
  } catch (e) {
    return "THREW: " + String((e && e.stack) || e);
  }
})();
