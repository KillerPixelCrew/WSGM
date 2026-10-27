# Handheld contributions

Add native handheld support to [LibHandheld](../external/libhandheld/README.md), which WSGM links
directly. Device support is not a plugin: there is no device manifest, package, Device SDK assembly
or device-package installation step.

## Existing implementations

The library contains 113 exact definitions across the supported handheld families, with device
artwork supplied by 107 glyph profiles. Detection, model facts, protocols and native services live
under the library's `Families` and internal implementation areas. Consult each family's current
source, provenance and dated observations before treating a behavior as hardware-tested.

## Adding a device

1. Identify the exact machine, board/product selectors, physical interfaces and firmware variants.
   Keep hardware observations separate from source-derived protocol facts.
2. Implement the family transport and exact definition in LibHandheld. Share an existing native
   service when its protocol is actually the same. Unknown decoders remain explicit research.
3. Keep detection and construction free of hardware acquisition. Native start owns access; commands
   use the existing semantic API and typed outcomes.
4. Supply authored control artwork, attribution and parser/encoder fixtures. Cover acquisition,
   handoff, suspend/resume, stop, restoration and terminal cleanup.
5. Keep profiles, AutoTDP policy, HidHide, virtual targets, Steam integration and presentation in
   WSGM. Build/deploy through the established application path for attended checks.
6. Commit and push the library before recording its gitlink in WSGM.

[Device Lab](../src/WSGM.DeviceLab) collects attended evidence and produces library contribution
source and fixtures. It does not create a device plugin. Remote testers diagnose through ordinary
WSGM logs; they are not required to run developer probes.

## Compatibility and lifetime

Use [LibHandheld's API](../external/libhandheld/API.md) and its native contracts. One
`HandheldDevice` serializes its services and commands; the host borrows its observer. Stop restores
native state. Dispose releases resources and joins outstanding work. Cancellation is cooperative and
never proves an uncertain write was undone or permits automatic replay.

The common Plugin SDK remains available for independent integrations, currently API 5 and package
version 0.4.0. Historical `WSGM.Device.Sdk` type namespaces inside that assembly provide shared
capability/lifecycle helpers; they do not restore the retired device extension point.
