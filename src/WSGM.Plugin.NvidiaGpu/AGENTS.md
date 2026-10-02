# NVIDIA Graphics package

This MIT common `wsgm.gpu` package owns NVIDIA settings and output controls. The maintainer's
NoVidiaApp is the driver-settings/profile behavioral reference. ColorControl is the display bit
depth, color, dithering and HDR10+ reference. Official NVAPI headers decide documented layouts,
entry points and enum values. Read README.md and PROVENANCE.md first.

- Load only the installed driver's System32 `nvapi64.dll`; do not redistribute it.
- DRS is authoritative for global/native application values. Change one setting at a time, never
  restore/delete entire profiles, and preserve external edits and prior explicit values.
- The journal records durable intent before SaveSettings. An unconfirmed write is reconciled by
  readback and never blindly repeated. Profiles sharing executables/native profiles cannot request
  conflicting values for one setting.
- Enumerate NVIDIA-connected active display IDs. Hybrid systems may have no NVIDIA-driven panel.
  Revalidate topology before writes; map Windows display identity through the driver, not numbering.
- Check complete color combinations through IS_SUPPORTED_COLOR and preserve every unedited field.
- Read all controls before startup support probes and publish only usable controls. Return raw output state,
  preserving disabled dithering and color policy. Probe DRS through its setter: save identical explicit values,
  but discard staged inherited/default values with LoadSettings instead of creating overrides. Never probe
  action buttons or retry failed/uncertain probes on polling/reconnect. Later user failures keep the layout stable.
- Private dithering and HDR output entry points come from ColorControl; isolate their binding and
  document exact IDs, layouts and limitations. Query HDR10+ Gaming support independently of video.
- All native operations share the serialized lane and stay off the UI thread. Never free the
  library under a call. Driver loss retracts controls and reopens later; ordinary row failures do
  not discard unrelated settings.
- Keep Windows HDR state intact when using ColorControl's HDR10+ -> HDR10 workaround. No registry
  dithering writes, forced driver restart or duplicate generic Windows HDR controls.
- Follow the root manual-first validation policy. Do not claim a hardware pass from compilation
  or mocked fixtures.
