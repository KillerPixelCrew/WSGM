# WSGM.Device.Sdk

What a plugin author links against: one lifecycle, capability publication and commands, canonical
input/output, glyph data, diagnostics, a small TestKit, and packaging validation.

**This assembly is linked into the plugin, so everything in it runs with the plugin's authority.**
That single fact decides what may live here.

- **Static data, not injection.** The SDK defines a glyph profile, control map, and assets.
  Authoring/scaffolding and previews belong to Device Lab; Steam selectors, CDP calls, and patch
  apply/verify belong to WSGM. This is an ownership boundary — it keeps the Steam surface in one
  place — and not a security control. A plugin is an assembly DeviceHost loads and runs, so it can
  already reach anything the user can; do not add sanitizing, allowlisting, or re-serializing here
  in the belief that it constrains a plugin. Asset handling checks integrity (hash, bounds,
  well-formedness) and passes the author's bytes through.
- Plugins never call HIDMaestro, never own WSGM's Steam Input lease, and never touch HidHide. The SDK
  offers no API that looks like one; canonical input goes out, canonical output comes back.
- The SDK is not a hardware-implementation surface. There is no WSGM-owned AMD/Intel power backend,
  EC/PawnIO service, or raw WMI/HID/IOCTL/ACPI/MMIO/MSR/serial proxy; the plugin directly owns its
  device services and package-local dependencies.
- Do not add implementation modules, generic resource leases/coordinators, evidence IDs/locks,
  source generators, or policy projections. Add a public abstraction only when the Claw plugin and
  a materially different plugin both require it.
- Helpers must make the safe path the easy one: a capability that cannot revalidate identity,
  firmware, range, and current state on every command should be awkward to express.
- Dependencies are declared, never installed or repaired at runtime. Do not add an SDK helper that
  copies a provider DLL, edits a registry path, restarts a device, or runs an installer.
