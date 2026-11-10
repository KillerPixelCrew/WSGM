---
name: wsgm-device-sdk
description:
  Implement, review or diagnose built-in LibHandheld contracts, family protocols and WSGM handheld
  host integration, including attended hardware evidence recorded in family provenance.
---

# WSGM handheld integration

Handhelds are built-in LibHandheld definitions. Installed device packages and the separate device
SDK are retired. Common plugin contracts remain in `src/WSGM.Plugin.Sdk`; GPU driver control is
built into `external/libgpu-driver-interact`. Keep their independent enablement and lifetimes.

Read the library-owned [LibHandheld skill](../libhandheld/SKILL.md) for device command outcomes,
power ownership, multi-output dispatch and recovery. Keep those rules in the library rather than
duplicating them in host adapters.

## Owners and contract

- `external/libhandheld/src/LibHandheld/Contracts` owns public semantic handheld values.
- `Families/` owns exact model facts, protocols and provenance. Read its nearest `AGENTS.md` before
  editing Claw or Ally code. Preserve every invariant from those former package guides.
- WSGM `Shell/Device/HandheldDevice*`, `DeviceCoordinator` and `Input/` own application policy,
  profiles, physical-controller routing, virtual targets and UI projection. Family code does not
  know UI layout.
- `HandheldDevice.Detect(identity)` is pure and exact. It returns no definition for unsupported
  hardware. Never add a broad CPU/manufacturer fallback or open native handles during detection.
- `Create(definition, identity, stateDirectory, diagnostics)` uses the chosen definition and
  captured identity. Do not recollect WMI identity during start. Install per-instance diagnostics
  before acquisition and release it with the owning lifetime.
- Definition metadata includes actual declared roles, controller availability, hardware dependencies
  and hardware verification. Preserve untested-model caution; only measured hardware earns a pass.

## Runtime requirements

Integration off runs no handheld lifecycle, controller target, hardware writes or AutoTDP. Windows
features, explicitly enabled common plugins and built-in GPU controls remain independent. Both Steam
Big Picture and the overlay use the same owner, actions, state and capabilities.

Serialize writes with observation. Cancellation before dispatch is rejected; failure after dispatch
is uncertain. Never retry uncertain writes, gate controls on successful readback, or hide writable
support because a read failed. Publish an acknowledged written value as observed. Preserve exact
model/firmware recovery bindings in the shared permissive journal. Family IDs and state paths are
fresh; legacy device profiles and journals are not migrated. Dispose only releases handles; bounded
stop/suspend owns restoration and reports incomplete cleanup honestly.

High-rate input and telemetry allocate nothing and log nothing per sample. Disconnect and every
lifecycle transition release held controller, keyboard and mouse edges. Model-specific controller
tables are never automatically re-sent. Do not add ASUS ATKACPI INIT or WDOG IDs. Preserve Claw 0x50
then 0x51 power writes 200 ms apart and its measured firmware chord state machine.

The system power button is a `Power` placement OEM control, never a remappable one. Publish it only
from a source that reads both edges and only while that source reads; WSGM takes Windows' button
handling over exactly while it is published. A model whose button Windows reports with a press edge
only (the Claw 8's ACPI fixed-feature button) publishes none. A family opts into the shared Intel
HID Event Filter reader by adding `IntelPdcPowerButtonService` to its services, after attended
evidence in its provenance.

Read [source contribution](references/authoring-and-packaging.md) and
[host integration](references/host-and-debugging.md) for the remaining boundary rules. Follow the
root manual-first validation policy. No static fixture, source review or build establishes hardware
acceptance, and no live test is authorized merely by applying this skill.
