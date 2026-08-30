// Probe: name every message class in the perf module and walk the prototype chain for
// real field accessors, so the per-app settings contract is exact. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_classes_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = { classes: {} };
  const chainNames = (obj) => {
    const names = new Set();
    let p = obj;
    let depth = 0;
    while (p && depth < 6) {
      for (const n of Object.getOwnPropertyNames(p)) names.add(n);
      p = Object.getPrototypeOf(p);
      depth++;
    }
    return [...names];
  };
  try {
    const m = runtime("28013");
    for (const k of Object.keys(m)) {
      const v = m[k];
      if (typeof v !== "function" || !v.prototype) continue;
      try {
        const inst = new v();
        const name = inst.getClassName ? inst.getClassName() : "?";
        out.classes[k] = name;
        if (/Perf/.test(String(name))) {
          out["fields_" + name] = chainNames(Object.getPrototypeOf(inst)).filter(
            (n) =>
              !/^(constructor|toObject|serializeBinary|serializeBase64String|getClassName)$/.test(
                n,
              ),
          );
        }
      } catch (e) {
        out.classes[k] = "ERR " + e;
      }
    }
  } catch (e) {
    out.err = String(e);
  }
  return JSON.stringify(out);
})();
