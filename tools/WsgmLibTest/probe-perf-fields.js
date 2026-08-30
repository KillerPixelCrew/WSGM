// Probe: instantiate the perf state / settings-update messages and read their real field
// names, so the payload contract WSGM would have to fill is exact. Read-only.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_fields_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};
  const inspect = (label, ctor) => {
    try {
      const inst = new ctor();
      out[label + "_own"] = Object.getOwnPropertyNames(inst).slice(0, 200);
      out[label + "_obj"] = inst.toObject ? inst.toObject() : null;
      out[label + "_class"] = inst.getClassName ? inst.getClassName() : null;
    } catch (e) {
      out[label + "_err"] = String(e);
    }
  };
  try {
    const m = runtime("28013");
    inspect("state", m.cI);
    inspect("update", m.TR);
    inspect("diag", m.bm);
    // Walk one level into the state message to reach limits / settings.
    try {
      const s = new m.cI();
      const keys = Object.getOwnPropertyNames(s);
      out.stateChildren = {};
      for (const k of keys) {
        const v = s[k];
        if (v && typeof v === "object") {
          out.stateChildren[k] = Object.getOwnPropertyNames(v).slice(0, 200);
        }
      }
    } catch (e) {
      out.walkErr = String(e);
    }
  } catch (e) {
    out.err = String(e);
  }
  return JSON.stringify(out);
})();
