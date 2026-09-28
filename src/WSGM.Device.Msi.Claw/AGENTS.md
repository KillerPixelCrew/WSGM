# MSI Claw plugin contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.Device.Msi.Claw/**`.

This project is the MIT-licensed reference plugin for the MSI Claw family: Claw A1M (`MS-1T41`), Claw 7 AI+ A2VM
(`MS-1T42`), Claw 8 AI+ A2VM (`MS-1T52`), Claw A8 BZ2EM (`MS-1T8K`) and Claw 8 EX AI+ CG3EM (`MS-1T91`). Only the
Claw 8 AI+ A2VM has hardware evidence; the other rows were built from Handheld Companion 1.3.1.6
(`_ref/HandheldCompanion`), which derives every Claw class from `ClawA1M` and shares its WMI, MCU, charge, fan and
lighting protocols. Read this project's `README.md`, `PROVENANCE.md`, the relevant tests, and the current
implementation before changing behavior. `PROVENANCE.md` records the reference unit's revision, IGCL and motion
evidence and cites each HC-derived model fact.

- HC 1.3.1.6 is the reference for the models without hardware evidence. A Device Lab observation on that model beats it.
- Every per-model fact lives in `ClawModels.cs`. Correct a model by changing its row, never by adding a model check
  elsewhere. A measured A2VM behaviour applies to another model only where HC shows the same code path.

## Build and packaging

From the repository root:

```powershell
dotnet build src/WSGM.Device.Msi.Claw/WSGM.Device.Msi.Claw.csproj --configuration Release
dotnet test tests/WSGM.Device.Msi.Claw.Tests/WSGM.Device.Msi.Claw.Tests.csproj --configuration Release
```

The plugin targets `net10.0-windows10.0.19041.0` and x64. `plugin.wsgm.json` is authoritative for package ID, plugin
version, API version, entry assembly, and entry type; a release tag must match it. When package verification is
requested, run
`./eng/pack-device.ps1 -Source src/WSGM.Device.Msi.Claw -RequireGlyphs`; it performs the framework-dependent
publish, glyph staging, offline Device Lab validation, and deterministic package creation. Do not tag, publish, or
release unless explicitly asked.

`src/WSGM.Device.Sdk` and `src/WSGM.DeviceLab` live in the same WSGM repository. The plugin references the one SDK
project, and packaging uses Device Lab from the same checkout. Deliver contract, tool, and plugin changes in one WSGM
commit.

## Exact device boundary

Match the machine as HC's `IDevice.GetCurrent` does: manufacturer `MICRO-STAR INTERNATIONAL CO., LTD.` and a
baseboard product in `ClawModels`. The SKU is recorded, never matched. Package ID `wsgm.device.msi.claw` and the
per-model definition IDs (`ms-1t41` ... `ms-1t91`) identify software records, not the machine. WMI-backed services need
only the MSI_ACPI provider; the EC firmware and `Get_WMI` interface version are recorded and bind the power and fan
journal, but gate nothing. MSI USB VID `0DB0` with the supported PIDs gates the controller. Never gate on a firmware
revision, EC or MCU (USB `bcdDevice`): MSI ships both through Windows Update and its updater, and the 0229 gate refused
controller ownership and lighting on every unit that moved to 0230. Record revisions for diagnostics; verify MCU
register layouts by reading the block back and checking its shape instead. The lighting profile address follows HC's
nearest-firmware table.

Detection must remain side-effect free. `StartAsync` and every mutation must revalidate live identity, firmware, service
availability, generation, deadline, range, and current state before access. Install `PluginTrace` before the first
hardware read. Report truthful outcomes; never convert an uncertain write or restore into success.

## Lifecycle and service behavior

- Serialize commands with observation so reads cannot race writes. Preserve whole-set publication and generation
  semantics.
- Keep periodic observation inside the host freshness window. A service read failure may degrade that service but must
  not kill the observation loop or unrelated services.
- Retract physical/OEM/descriptors if startup cannot establish the supported device. Treat OEM,
  power/charge/fans/telemetry, lighting, motion, controller, chord suppression, and optional display as independently
  degradable.
- Trace transitions, decisions, and keyed state changes only. Do not log every HID or motion report; cancellation is
  diagnostic, not an error.
- Stop and disposal must be bounded, idempotent, and honest about incomplete cleanup.

## Mutation invariants

All hardware protocols are bounded and allowlisted at their call sites. WMI calls are serialized, the transport admits
only `Get_*`/`Set_*` names, and callers must remain limited to the measured methods with exact 32-byte payloads and the
established timeout. Preserve unknown bytes and flags in stateful read-modify-write formats such as fan and lighting
payloads; power and charge deliberately use zero-filled command envelopes.

- Power: keep PL1/PL2 within the model's range (8-37 W on the A2VM) and PL1 <= PL2. Use ordered writes, exact readback, and rollback of the original
  pair. PL1 <= PL2 is a firmware invariant rather than a user preference, so a single-limit write that would break it
  carries the other limit with it: a boost ceiling below the current sustained limit pulls PL1 down, and a sustained
  limit above the current boost limit pushes PL2 up. The requested number is always applied as asked. Only a value
  outside the model's range is rejected. The BZ2EM also receives the boost value at EC `0x52`, blind, as HC writes it.
- Scenarios: presets map Super Battery/Balanced/Extreme Performance to Eco/Green/Sport on AC and Comfort on battery,
  following HC's ClawA1M handler that every Claw inherits. The User scenario is 3, or 6 on the CG3EM. Journal the exact original scenario byte with the watt
  pair. Select or restore the scenario before the pair, since firmware can reset power limits. Publish the resulting
  pair before reporting scenario success; inactive SHIFT must not be reported as an active preset. This mapping is
  source evidence, not an attended verification of its firmware effects.
- Charge: 60-100 percent is a persistent user setting held in the low seven bits of register `0xD7`; bit 7 is MSI's
  Battery Master flag and is carried through every write. A read outside 60-100 (a BIOS update resets it to 0) is
  published as unknown with the capability still writable, never as a fault. Verify writes and roll back
  failed/cancelled changes; do not restore a successful choice on normal stop.
- Fans: one six-point semantic curve applies atomically to both channels under one snapshot. Verify both readbacks and
  restore both originals on failure.
- Lighting: treat the 32-byte MCU profile as persistent state. Preserve unknown bytes, replicate the three logical zones
  as measured, keep the write-rate limit, verify the full profile, and exactly roll back failure or cancellation. Do not
  revert a successful user choice on normal stop.
- Controller mode: stop source/output first; journal the original mode; switch, wait for re-enumeration, and identify
  the same physical device through `DEVPKEY_Device_LocationPaths`. Restore and verify the original mode during cleanup.
  Never report an unverified device as restored.
- Power, fans, and controller mode are temporary. Capture the first original value in
  `temporary-state.v1.json` before mutation, publish the bounded journal atomically, restore power and fans only on
  the same EC firmware, restore controller mode on any MCU revision, retain failed entries for retry, and block unsafe
  mismatches.
- Optional VRR/display support remains capability-probed and cycle-scoped. Load the user's Intel control library
  dynamically; do not ship Intel binaries. Preserve tested IGCL ABI sizes, capture the original profile on acquire, and
  restore that exact profile during make-safe.

## Input and motion invariants

- Preserve the measured DirectInput report layout: byte 7 bit 4 is left/M1 and bit 3 is right/M2. Assert the two bits
  separately so a swapped mapping cannot pass. Preserve OEM key codes and the 120 ms latch used for reports without
  release events.
- Chord suppression belongs in this plugin. Intercept non-injected Win+G on key-down as HC does, including ordinary
  keyboard Win+G with modifiers. Consume repeats and G up after an accepted synthetic Win release, even if physical Win
  up arrives first. Do not retry a failed release on repeats. Also suppress the measured orphan `G`/`Tab` key-up while
  Win is down and Ctrl/Alt/Shift are not. Preserve normal Win+Tab and unknown input. The hook callback must remain
  bounded, allocation-light, and free of I/O and logging.
- Keep the x64 `INPUT` ABI at 40 bytes with its 32-byte union, including for keyboard-only injection. A smaller record
  makes `SendInput` reject the synthetic Win release and the hook pass the firmware chord through. Keep the layout and
  shortcut-preservation regression tests.
- Synthetic left/right Win events must carry `KEYEVENTF_EXTENDEDKEY`; dummy-key events must not. Source comparison and
  layout tests do not establish that desktop Game Bar suppression works.
- On the A2VM and CG3EM, bind only the measured legacy Sensor API accelerometer/gyrometer identities and fields. Reject
  duplicate gyrometer counters before reading the accelerometer, and keep the bounded drop-oldest channel. The A1M and
  BZ2EM use the default WinRT sensors, as HC does for a Claw without `WindowsGyrometerFields`.
- Apply the axis transform exactly once: HC's shared swap `(raw X, raw Z, raw Y)` times the model's signs, which
  gives `(raw X, raw Z, -raw Y)` on the A2VM.
- Rumble is proportional except on the A1M, where HC's `DClawController` drives each motor on or off at 193.
- Preserve the measurement-derived stationary gyro bias behavior: approximately 200-report windows, subtraction without
  deadband, rest gates, and agreement across three separated windows before distant-bias reacquisition. Preserve
  resampling and reset semantics; do not clamp away a valid distant correction.

## Glyphs, tests, and evidence

The glyph profile names each asset by `assetId`, which is also its file name under `glyphs/assets`. When artwork
changes, update the declared `viewBox` or pixel dimensions, the source revision, and the notice; keep the authored
artwork as upstream drew it rather than reformatting it. Keep all manifest assets packaged.

CI is software-only. Any claim about WMI, HID, Sensor API, controller re-enumeration, fan/lighting payloads, power
behavior, or display behavior requires an explicit attended Device Lab run on that model and a provenance update. Add focused regression tests for each changed identity gate, protocol byte, timeout, rollback, journal, mapping,
transform, calibration, ABI, and package invariant. Preserve repository `.editorconfig` conventions.
