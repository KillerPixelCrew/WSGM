# ROG Ally X implementation references

This is source inspection, not hardware validation. The maintainer has no Ally X available. HHD is primary, especially
for buttons; Handheld Companion (HC) is a cross-reference for Windows transports and device controls. No reference code
is imported into the MIT scaffold.

## HHD baseline

Repository: https://github.com/hhd-dev/hhd Local checkout: `_ref/hhd` Inspected revision:
`5b49c5d904257e042a704ade958fac0ba57af4b1` (2026-09-14).

Paths below are relative to that checkout.

| Area                     | Source                                     | Findings                                                                                                                                                                                                        |
|--------------------------|--------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Detection                | `src/hhd/device/rog_ally/__init__.py`      | Product-name substring `ROG Ally X RC72LA`; separate Xbox Ally variants. This is not WSGM's required exact identity predicate.                                                                                  |
| Interfaces               | `src/hhd/device/rog_ally/base.py`          | ASUS VID `0x0B05`, Ally X PID `0x1B4C`; vendor usage page `0xFF31`, usage `0x0080`. Gamepad input comes from Linux evdev, not a raw input-report decoder.                                                       |
| Vendor buttons           | `AllyHidraw.produce` in `base.py`          | Report `0x5A`: `0xA6` emits mode, `0x38`/`0x93` emits keyboard/QAM, `0xA7` toggles mouse mode, and `0xA8` is ignored. Mode/QAM releases are synthesized after 150 ms.                                           |
| Rear buttons             | `base.py`, `const.py`                      | `REMAP_M1M2_F17F18` configures rear buttons; the device keyboard path maps F17/F18 to `extra_l1`/`extra_r1`. This requires a configuration write, not just passive listening.                                   |
| QAM timing               | `src/hhd/controller/base.py`               | `keyboard_no_release` handles a missing hold event using a 40 ms tap deadline. This is policy above the raw vendor events.                                                                                      |
| Controller configuration | `src/hhd/device/rog_ally/const.py`         | 64-byte commands, report `0x5A`, group `0xD1`; game mode 1 and mouse mode 3. Mode setup also writes mappings, turbo reset, vibration strength, and stick/trigger deadzones.                                     |
| Rumble                   | `AllyXHidraw.consume` in `base.py`         | Output bytes `0D 0F 00 00 weak strong FF 00 EB`, magnitudes scaled to 0–100. This path handles main rumble only; physical response remains unmeasured.                                                          |
| RGB                      | `hid.py`, `const.py`                       | Four zones; solid, breathing/pulse, dual color, rainbow and spiral modes, brightness and boot/charging flags. Commands use HID output writes through `Device.write`, not feature writes despite constant names. |
| Motion                   | `base.py`                                  | Linux IIO `CombinedImu`, explicit axis mapping and gyro scale override. Windows sensor transport, units and basis still need independent confirmation.                                                          |
| Power routing            | `src/adjustor/hhd.py`                      | ASUS product match selects `AsusDriverPlugin` before the unified fallback. Do not mistake unified ASUS profile metadata for the active Ally X backend.                                                          |
| Power and fans           | `src/adjustor/drivers/asus/__init__.py`    | Linux `asus-nb-wmi` thermal policy and SPL/SPPT/FPPT attributes; two eight-point curves via `asus_custom_fan_curve` hwmon. No Windows transport is provided here.                                               |
| Power metadata           | `src/adjustor/core/const.py`               | Ally X quiet/balanced/performance values 13/17/30 W, performance on DC 25 W. These are reference values, not validated WSGM limits.                                                                             |
| Battery                  | `src/adjustor/drivers/battery/__init__.py` | Generic Linux charge-limit/bypass attribute discovery. Presence must be checked; this does not establish Ally X Windows bypass support.                                                                         |

### Behavior that needs a different WSGM implementation

- `AllyHidraw` reapplies configuration after five seconds. WSGM must not automatically retry an uncertain hardware
  write.
- The readiness helper is disabled, and mode setup does not independently verify readback.
- `AllyHidraw` inherits a close method that closes the HID handle without restoring the previous configuration. WSGM
  needs original-state capture, restoration and topology verification.
- The evdev trigger-map comments disagree with the active mapping: the dictionary maps RT to `ABS_Z`
  and LT to `ABS_RZ`. Neither Linux event codes nor comments establish raw Windows offsets.
- Vendor pulse events and synthetic releases must not be presented as physical held-state samples. Keep OEM actions
  separate from canonical controller state and retain host-owned action policy.
- HHD's Linux device hiding and virtual outputs are not plugin responsibilities in WSGM.

HHD's root license is LGPL-2.1. Inspect applicable file notices before any source reuse; the existing MIT scaffold
contains no HHD implementation.

## Handheld Companion cross-reference

Repository: https://github.com/Valkirie/HandheldCompanion Local checkout: `_ref/HandheldCompanion`
Inspected revision: `1d85da30861f700868e48ae8f498a5c455896f7c` (2026-09-12).

Paths below are relative to that checkout.

| Area                      | Source                                                     | Comparison and implication                                                                                                                                                                                                                                        |
|---------------------------|------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Model specialization      | `HandheldCompanion/Devices/ASUS/ROGAllyX.cs`               | Inherits `ROGAlly`; overrides profile triplets to 13/17/25 W. HHD distinguishes 30 W AC performance and 25 W DC, so these are different profile policies.                                                                                                         |
| Vendor buttons            | `HandheldCompanion/Devices/ASUS/ROGAlly.cs`, `HandleEvent` | `0xA6` is Command Center, `0x38` Armoury Crate, `0x93` Library. HC treats `0xA7`/`0xA8` as hold/release; HHD toggles mouse mode on `0xA7`, ignores `0xA8`, and merges `0x93` with its QAM event. Preserve these as alternative interpretations, with HHD primary. |
| Rear buttons              | `ROGAlly.cs`, `ConfigureController` and OEM chords         | Same command prefix and rear mapping bytes as HHD. HC labels M1 as F18 and M2 as F17 and comments that held inputs repeat. HHD maps F17 to left and F18 to right. Verify physical labels, repeat handling and release behavior before assigning WSGM controls.    |
| HID transport             | `ROGAlly.cs`, `ConfigureController`                        | Uses `WriteFeatureData(..., 64)` for controller configuration, whereas HHD uses HID output writes. Similar bytes do not prove interchangeable Windows report APIs.                                                                                                |
| Lighting                  | `ROGAlly.cs`                                               | Uses report `0x5D` for Aura, including feature-write brightness and output-write colors. HHD's active constants use `0x5A`. Keep interface/report selection explicit and unvalidated.                                                                             |
| Windows control transport | `HandheldCompanion/Devices/ASUS/AsusACPI.cs`               | Opens `\\.\ATKACPI`, IOCTL `0x0022240C`, with DSTS/DEVS operations. This is a Windows driver transport lead, not a verified dependency or firmware contract for WSGM.                                                                                             |
| Power identifiers         | `AsusACPI.cs`                                              | SPL `0x001200A3`, SPPT `0x001200A0`, FPPT `0x001200C1`, performance mode `0x00120075`. HC's short-limit setter writes both SPPT and FPPT; HHD has three independently addressed limits.                                                                           |
| Fans and charging         | `AsusACPI.cs`                                              | CPU/GPU curve identifiers `0x00110024`/`0x00110025`, battery limit `0x00120057`; fan-curve getter also exists. Use readback only after its semantics and support are established.                                                                                 |
| Cleanup                   | `ROGAlly.cs`, `Close`                                      | Rewrites a hard-coded default controller configuration and releases logical OEM buttons. This does not restore the user's captured original mappings. HHD's close does not restore mappings either.                                                               |
| Motion                    | `HandheldCompanion/Resources/Devices/ROGAllyX.json`        | Declares axis swaps and signs for HC's motion pipeline. Do not combine these with HHD's transform; their sensor and output coordinate conventions must first be reconciled.                                                                                       |

HC's root license is CC BY-NC-SA 4.0. Its source is retained only in the ignored reference checkout; no HC code or
assets are included in the MIT package.

## Next implementation slices

1. Define reference-derived, hardware-free vendor event and command codecs with explicit report API and length
   requirements. Test short reports, duplicate events and held/repeating rear buttons.
2. Establish Windows interface identity and exclusive ownership boundaries. HC supplies transport leads; HHD supplies
   primary behavior. Neither supplies a validated WSGM hardware fixture.
3. Implement read-only identity and control queries before any mutation. Keep unknown firmware and unsupported controls
   passive.
4. Add original-state capture and verified restoration before remapping rear buttons or changing controller mode. WSGM
   retains action policy, virtual targets and HidHide ownership.
5. Enable hardware behavior only after an attended inventory and the relevant device scenarios.

No imported daemon, installer, driver, probe or hardware command was executed during this review.
