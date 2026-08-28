# WSGM.Device.Sdk

What a plugin author links against: the host adapter, lifecycle and resource-lease scaffolding,
capability registration, the glyph control-map builder and asset packaging helpers, fixture helpers,
and the TestKit.

**This assembly is linked into the plugin, so everything in it runs with the plugin's authority.**
That single fact decides what may live here.

- **Authoring, not injecting.** The SDK may help an author *declare* a glyph profile, control map, and
  assets, validate them locally, and preview them against a simulated Steam layout. It must never
  contain Steam selectors, CDP calls, or patch apply/verify — putting the injector here would let an
  unreviewed package reach the user's authenticated Steam CEF context, which is exactly the reach
  `P0-052` removes.
- Plugins never call HIDMaestro, never own WSGM's Steam Input lease, and never touch HidHide. The SDK
  offers no API that looks like one; canonical input goes out, canonical output comes back.
- The SDK is not a hardware-implementation surface. There is no WSGM-owned AMD/Intel power backend,
  EC/PawnIO service, or raw WMI/HID/IOCTL/ACPI/MMIO/MSR/serial proxy — reusable hardware code is a
  version-pinned implementation module that a plugin links and owns, not a service the SDK provides.
- Helpers must make the safe path the easy one: a capability that cannot revalidate identity,
  firmware, range, and current state on every command should be awkward to express.
- Dependencies are declared, never installed or repaired at runtime. Do not add an SDK helper that
  copies a provider DLL, edits a registry path, restarts a device, or runs an installer.
