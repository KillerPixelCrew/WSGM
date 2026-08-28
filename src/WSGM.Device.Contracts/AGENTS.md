# WSGM.Device.Contracts

The semantic boundary between WSGM and every device plugin: capability descriptors, capability
state, command envelopes, lifecycle messages, canonical controller/OEM/output state, the IPC wire
format, and the glyph profile schema with its validator and SVG normalizer.

**This is the only new project the NativeAOT `WSGM` executable may reference.** Everything here is
compiled into WSGM's AOT image, so its constraints are WSGM's constraints.

- NativeAOT rules apply in full: source-generated JSON (`[JsonSerializable]`), no runtime reflection,
  no `Assembly.Load`, no `dynamic`, no managed COM, no reflection-dependent package. `IsAotCompatible`
  and the trim/single-file analyzers are on — a warning here fails the release build, so do not
  suppress one to make a type serialize.
- **Semantic capabilities only.** A device-specific address, WMI method name, HID report offset, EC
  register, raw buffer, or privileged operation appearing in this assembly is a design failure, not a
  detail: WSGM must be able to express "sustained power limit, 8–30 W, step 1" without knowing how
  any device implements it.
- Reject at the schema boundary, not in the consumer: generic execute, shell, file, WMI, HID, EC,
  IOCTL, script, path, helper, and raw-buffer operations must be unrepresentable in the wire types.
- Contract changes are versioned and negotiated. Both sides of a version window must round-trip, and
  compatibility tests are part of the change, not a follow-up.
- Bound every string, list, and payload. These types decode input from a plugin process; unbounded
  fields are a decode budget waiting to be exhausted.

## Glyph profile schema (`P0-052`)

Artwork and the control map are supplied by the plugin package; WSGM supplies the schema, the
validator, and the normalizer that live here. Two rules are load-bearing:

- **Plugin bytes never reach a surface.** SVG is parsed against an element/attribute allowlist and
  **re-serialized from our own model** before any CEF context or Avalonia control sees it. Passing
  the original bytes through — even after validating them — defeats the entire boundary.
- Assets resolve by content hash from the package manifest. A profile ID, asset name, or plugin
  string must never become a filesystem path or URL.

Selectors, CDP calls, and patch apply/verify belong to the Steam UI host in `src\WSGM\`, never here
and never in the SDK.
