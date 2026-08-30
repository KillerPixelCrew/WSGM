// Round-trips a settings-update request through serializeBase64String -> deserializeBinary ->
// toObject, to pin the exact shape WSGM's UpdateSettings receives. Builds a message and inspects it;
// never calls UpdateSettings, so nothing is applied.
(() => {
  const store = window.SystemPerfStore;
  if (!store) return "no SystemPerfStore";
  let request;
  try {
    request = store.CreateSettingsUpdateRequest();
  } catch (e) {
    return "CreateSettingsUpdateRequest threw: " + e;
  }
  try {
    request.settings_delta().global(true).set_perf_overlay_level(3);
    request.settings_delta().per_app(true).set_fps_limit_external(60);
  } catch (e) {
    return "building the delta threw: " + e;
  }
  const base64 = request.serializeBase64String();
  const ctor = request.constructor;
  const out = {
    base64,
    ctorName: ctor?.name,
    hasDeserializeBinary: typeof ctor?.deserializeBinary === "function",
    directToObject: request.toObject ? request.toObject() : "no toObject",
  };
  if (out.hasDeserializeBinary) {
    const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
    out.roundTripped = ctor.deserializeBinary(bytes).toObject();
  }
  return JSON.stringify(out, null, 1);
})();
