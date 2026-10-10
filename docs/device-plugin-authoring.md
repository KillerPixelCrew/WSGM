# Handheld device contributions

Handheld support is implemented in [LibHandheld](../external/libhandheld/README.md), directly linked
into WSGM. There is no separate Device SDK assembly, device plugin project, manifest, package or
device-package installation step. The common [Plugin SDK](../src/WSGM.Plugin.Sdk/README.md) remains
for independent integrations such as IR.

## Existing implementations

`external/libhandheld/src/LibHandheld/Families/MsiClaw` and `Families/RogAlly` contain the migrated
native engines and model facts: five Claw definitions and four Ally definitions. Their original
behavior, protocol facts, glyph bytes and notices are retained. Migration does not constitute a new
hardware pass. Consult each family's provenance and existing dated evidence before treating a model
or feature as observed on hardware.

The full target remains every HC/HHD-supported device. The inventory's 201 source records are not
201 unique physical models and do not imply implemented coverage. HC is the primary Windows source,
HHD is secondary and OpenGamepadUI is tertiary. Ambiguous board, firmware and CPU variants require
explicit resolution before they share an implementation.

## Adding a device

1. Identify the exact machine and physical controller interfaces. Record board/product selectors,
   firmware versions and protocol provenance in the library inventory and family evidence.
2. Implement the native family transport and exact model facts in LibHandheld. Share helpers only
   for concrete common protocols. Keep profiles, AutoTDP policy, HidHide, virtual-controller
   routing, Steam integration and UI in WSGM.
3. Register the supported definition and exact identity matching. Detection and construction must
   not acquire hardware. Unknown input decoders remain explicitly unimplemented research; a selector
   or scaffold is not device support.
4. Supply parser/encoder fixtures and regression sources, with separate records of attended hardware
   observations. Cover start, controller handoff, suspend/resume, restoration and terminal cleanup.
5. Build and deploy through WSGM for manual acceptance. Automated tests follow the maintainer's
   manual-first validation policy. Commit and push the library before updating WSGM's gitlink.

[Device Lab](../src/WSGM.DeviceLab) remains the attended evidence tool. Its contribution scaffolding
creates LibHandheld source, fixtures and provenance, rather than a plugin project or manifest.
Remote testers use ordinary WSGM logs; do not require them to run developer probes.

## API and ownership

The public [API](../external/libhandheld/API.md) uses `HandheldDevice`, typed contracts and a
borrowed `IHandheldObserver`. WSGM's `HandheldAdapter` converts those contracts into the
coordinator's existing projections; `HandheldDeviceRuntime` owns the direct lifecycle. Existing
recovery state remains under `DeviceState/<legacy family id>`.

Cancellation requests cooperation and does not prove that a hardware write stopped. Await actual
native completion before releasing ownership. Stop restores native state; disposal joins remaining
operations and releases resources without substituting for restoration. An uncertain write is not
automatically retried.

## Common plugin compatibility

Common plugins now use Plugin API 5, exact range 5..5, with SDK package version 0.4.0. Shared
capability/lifecycle contracts retain historical `WSGM.Device.Sdk` namespaces inside the single
`WSGM.Plugin.Sdk` assembly. Their assembly identity changed, so plugins must be rebuilt. These
namespaces and legacy diagnostic version records do not restore a separate hardware SDK or a
device-package loader.
