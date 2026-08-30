// Probe: the exact accessor names on the perf state message and its nested limits/settings
// messages, so the C# projection fills the fields Valve's own controls read rather than names
// guessed from the minified hook sources. Read-only: constructs messages, writes nothing.
(() => {
  let runtime;
  window.webpackChunksteamui.push([
    ["wsgm_probe_accessors_" + Date.now()],
    {},
    (r) => {
      runtime = r;
    },
  ]);
  const out = {};

  // Accessors live on the prototype; toObject() only emits fields that are set, which is why an
  // empty message looked field-less in the earlier probe.
  const accessors = (ctor) => {
    try {
      return Object.getOwnPropertyNames(ctor.prototype).filter(
        (name) => name !== "constructor" && !name.startsWith("set_") &&
          !["toObject", "serializeBinary", "serializeBase64String", "getClassName"].includes(name),
      );
    } catch (e) {
      return ["ERR " + e];
    }
  };

  try {
    const m = runtime("28013");
    out.classes = {};
    for (const key of Object.keys(m)) {
      const value = m[key];
      if (typeof value !== "function") continue;
      let className = null;
      try {
        className = new value().getClassName?.() ?? null;
      } catch (e) {
        out["ctorError_" + key] = String(e);
        continue;
      }
      if (!className) continue;
      out.classes[className] = { export: key, fields: accessors(value) };
    }
  } catch (e) {
    out.moduleError = String(e);
  }

  return JSON.stringify(out);
})();
