// Probe: the nested limits / global / per-app message field sets — the exact contract a
// WSGM-supplied perf backend would have to fill. Read-only: builds messages in JS only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_nested_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const chainNames = (obj) => {
    const names = new Set();
    let p = Object.getPrototypeOf(obj);
    let depth = 0;
    while (p && depth < 3) {
      for (const n of Object.getOwnPropertyNames(p)) names.add(n);
      p = Object.getPrototypeOf(p);
      depth++;
    }
    return [...names].filter(
      (n) =>
        !/^(constructor|toObject|serializeBinary|serializeBase64String|getClassName|getJsPbMessageId|syncMapFields_|toArray|toString|getExtension|setExtension|cloneMessage|clone)$/.test(
          n,
        ),
    );
  };
  const grab = (label, msg) => {
    try {
      out[label + "_class"] = msg.getClassName ? msg.getClassName() : "?";
      out[label] = chainNames(msg);
    } catch (e) {
      out[label + "_err"] = String(e);
    }
  };
  try {
    const m = runtime("28013");
    const state = new m.cI();
    grab("limits", state.limits(true));
    const settings = new m.SW();
    grab("global", settings.global(true));
    grab("perApp", settings.per_app(true));
  } catch (e) {
    out.err = String(e);
  }
  return JSON.stringify(out);
})();
