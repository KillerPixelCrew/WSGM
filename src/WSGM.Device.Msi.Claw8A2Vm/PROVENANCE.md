# MSI Claw 8 AI+ A2VM plugin provenance

Source revision: `HW-2026-09-03`

On 2026-09-08, the existing ordered power-pair transport was connected to the SDK's optional
coordinated command. AutoTDP uses equal PL1/PL2 targets within the existing 8-37 W bounds. New
fake-transport tests cover raising, lowering and failed-readback rollback. This is software
validation of the existing transport, not a new attended hardware pass.

The 2026-09-05 keyboard comparison against HandheldCompanion revision
`5c94abca83f8711ff5620906871b31a41c76bf05`, `Helpers/FirmwareWorkarounds.cs`, found that synthetic
Win releases also need the extended-key flag. The plugin now supplies it and follows HC's Win+G
key-down interception, including normal keyboard Win+G with modifiers, as requested by the
maintainer after continued desktop failures. The existing measured G/Tab orphan-up path remains.
Sequence tests cover repeats, release order and failure without input injection. These software
corrections are not a new attended suppression pass.

Power-preset data was checked on 2026-09-05 against HandheldCompanion commit
`5c94abca83f8711ff5620906871b31a41c76bf05`: `Devices/MSI/ClawA2VM.cs` supplies the 8/8/9, 17/17/18
and 30/30/31 W overrides; `ClawA1M.cs` and `Properties/Resources.resx` supply the names and Windows
modes. WSGM's sustained/slow pair maps these to PL1/PL2 8/9, 17/18 and 30/31 W. AC firmware targets
follow HC's Eco/Green/Sport choices, with Comfort on battery. Full Power is a WSGM addition using
37/37 W, Best Performance, and Sport on AC or Comfort on battery. These are independently
implemented through the SDK. HC's CPU boost and Intel Endurance settings are outside this shortcut.
This is source evidence and fake-transport validation, not a new attended hardware measurement.
Existing power transport limits and rollback behavior are unchanged.

The package is first-party code licensed under the MIT License. It is the reference implementation
of the WSGM Device SDK — the plugin other device plugins are expected to be read against and copied
from — which is why it is permissive rather than carrying WSGM's own GPL-3.0-or-later. A plugin
links only `WSGM.Device.Sdk`, never WSGM, so nothing here obliges a derived plugin to any licence.

Required third-party notices ship in `THIRD_PARTY_NOTICES.md`.

## Hardware knowledge in this package

Every register, report layout and WMI method here was established by observation on a physical MSI
Claw 8 AI+ A2VM, not from vendor documentation. Two consequences:

- The revision above identifies the hardware generation the behaviour was confirmed against. A
  different Claw model is a different device and is not claimed to be supported by detection.
- `ArcSyncTransport` binds Intel's Graphics Control Library dynamically, through `ControlLib.dll` as
  it already ships in `System32` with the Intel driver. No Intel code is redistributed here; the
  blittable structures mirror the published `igcl_api.h` layouts so the driver's own size checks
  pass, and a layout regression test pins them.
- `EnduranceGamingTransport` reaches the same library for Intel Endurance Gaming, through
  `ctlGetSet3DFeature` with `CTL_3D_FEATURE_ENDURANCE_GAMING` (feature 1) and
  `CTL_PROPERTY_VALUE_TYPE_CUSTOM` (5) carrying a `ctl_endurance_gaming_t`. Confirmed on the
  reference unit on 2026-09-10, unelevated: `ctlInit` reported supported version `0x10001` (IGCL
  1.1), one adapter enumerated, and the feature read back `EGControl = OFF`, `EGMode = PERFORMANCE`.
  The managed `ctl_3d_feature_getset_t` mirror measured 56 bytes and `ctl_endurance_gaming_t` 8,
  both pinned by a layout test. A second concurrent IGCL session alongside the Arc Sync one was
  confirmed to succeed rather than assumed, which is why the two transports each own their own
  handle. Support is decided by a successful read rather than by walking
  `ctlGetSupported3DCapabilities`; that capability array is not marshalled at all. **Only the read
  is device-verified.** A write and its read-back have not been exercised on the unit, so the
  applied path remains a source-and-layout claim awaiting an attended Device Lab run.
- The same transport reads `CTL_3D_FEATURE_PREBUILT_SHADER_DOWNLOAD` (feature 18), which Intel
  documents as carrying generic bool fields, so its value rides in the property union with
  `CTL_PROPERTY_VALUE_TYPE_BOOL` (0) rather than through `pCustomValue`. Confirmed on the reference
  unit on 2026-09-10: the feature answered and read back enabled. `CTL_3D_FEATURE_GAMING_FLIP_MODES`
  (9) and `CTL_3D_FEATURE_LOW_LATENCY` (16) also answered on the same probe and are recorded here
  only as observed-present; neither is implemented, and neither is a driver-level VSync toggle. As
  with Endurance Gaming, **only the read is device-verified** for shader download; no value was
  applied to the unit.
- `IntelGraphicsMemoryTransport` drives Intel's Shared GPU Memory Override, which is not in IGCL at
  all: `ControlLib.dll` has four memory entry points and every one is a get. The driver reads
  `GpuSystemMemoryPinninglimit`, a percentage under the display adapter's `GMM` key, when it sets up
  its memory manager, which is why the change needs a restart. Confirmed on the reference unit on
  2026-09-10 by driving Intel Graphics Software and watching what moved. At rest the value read 57,
  Intel documents 57% as the default, `ullTotalPhys` was 33,866,657,792 bytes, and the adapter
  reported 19,327,352,832 — 57.07% of it, tying the value to the feature. Setting the panel to 44%
  wrote 44 into exactly that value; pressing reset wrote 57 back rather than deleting it, so the
  default is the literal 57 and there is no "changed" flag to look for. Neither change touched
  anything else: no other value under the adapter, nothing under `HKLM\SOFTWARE\Intel` or
  `HKCU\SOFTWARE\Intel`, and nothing in ProgramData. The one other artifact was Intel Graphics
  Software's own DPAPI-encrypted per-user settings blob, which the driver never reads.
  `qwMemorySize` stayed at the old percentage across the change, which is the reboot requirement
  showing itself. **The write is device-verified, the effect is not**: nothing here observed the
  split actually change after a restart. The offered range is 13-87 percent, taken from what Intel
  Graphics Software shows on this machine rather than derived; Intel publishes the default and a 10
  GB system-memory requirement but no formula for the bounds.
- Driver-level VSync is `CTL_3D_FEATURE_GAMING_FLIP_MODES` (feature 9), not a VSync toggle: Intel's
  `igcl_api.h` has no `CTL_3D_FEATURE_VSYNC` at all. Reading `ctlGetSupported3DCapabilities` on the
  reference unit on 2026-09-10 returned twelve supported features, with feature 9 enum-typed and a
  supported mask of `0x2d` — application default, VSync on, Smooth Sync and capped FPS. VSync
  **off** is deliberately not offered, because leaving it off is what the application default
  already means. The capability element layout is not asserted: the stride is derived from the data
  by finding the only one that yields that many distinct in-range feature ids, measured as 72 bytes
  here. **IGCL cannot read or write the current mode.** `ctlGetSet3DFeature` answers feature 9 with
  an enable byte and a value of zero regardless of what is set, and a write returns
  `CTL_RESULT_SUCCESS` and changes nothing observable — not its own getter, not the stored value,
  not the per-application entries — tested both unelevated and elevated, with Intel Graphics
  Software and `IntelGraphicsSoftwareService` running. What does hold the mode is
  `<adapter>\3DKeys\Global_AsyncFlipMode`, carrying Intel's own `ctl_gaming_flip_mode_flag_t` bits;
  an untouched machine reads 1, which is `APPLICATION_DEFAULT`. So the capability asks IGCL which
  modes exist and reads and writes the value in the driver's own store. Confirmed elevated on the
  reference unit: 1, 4, 8 and 32 each wrote and read back exactly, and the original restored. **The
  write requires elevation** — the key is under `HKLM\SYSTEM` — which WSGM has as a shell
  replacement; unelevated every write fails cleanly and is reported as failed. The same elevation
  requirement applies to the shared-memory split above, whose write was never exercised on the unit.
  **Whether the driver acts on either value without a restart is not established.**
- Intel's IO/sensor driver exposes the STMicroelectronics LSM6DSO `Physical Accelerometer` and
  `Physical Gyrometer` through the legacy Sensor API as custom sensor type
  `e83af229-8640-4d18-a213-e22675ebb2c3` on the `VID_8087&PID_0AC2` HID collection. Their live
  values are `VT_R4` fields 7, 8, and 9 under property-set `b14c764f-07cf-41e8-9d82-ebe3d0776a6f`,
  in g and degrees/second respectively. Field 34 is the gyrometer's opaque `VT_UI4` hardware-report
  counter: it advances for stationary samples too. The gyrometer advertises a 10 ms minimum report
  interval (100 Hz); the accelerometer advertises 2 ms, but its synchronous `GetData` can still wait
  about 200 ms for a changed report at rest. Combined reads therefore acquire acceleration first and
  the gyrometer last so a delayed accelerometer cannot make the published gyro report and timestamp
  stale. The application-axis transform for both die-aligned sensors is `(raw X, raw Z, -raw Y)`;
  the Steam Deck encoder reverses that once when filling the controller's raw IMU slots. WinRT does
  not project the accelerometer and its gyrometer event path suppresses unchanged reports.
