# WSGM Device Plugin: ASUS ROG Ally family

This is the device plugin that teaches WSGM the four ASUS ROG Ally handhelds: power limits and the
ASUS performance modes, fan curves, the charge limit, Aura lighting on the stick rings, the
controller with its OEM buttons, rumble and motion, and the physical glyphs Steam shows.

It was built without an Ally on the desk. Every register, report and axis comes from the Handheld
Companion 1.3.1.6 Patreon build, which runs on these devices under Windows, with HHD as a
cross-check; none of it has been run through WSGM on the hardware yet. A Device Lab run confirms
what HC already establishes rather than discovering it. `PROVENANCE.md` cites the source of every
fact. Everything that differs between the models is one row in `AllyModels.cs`, so a correction
changes data rather than code.

| Model           | Board        | Definition ID |
| --------------- | ------------ | ------------- |
| ROG Ally        | RC71L        | `rc71l`       |
| ROG Ally X      | RC72LA/RC72L | `rc72la`      |
| ROG Xbox Ally   | RC73YA       | `rc73ya`      |
| ROG Xbox Ally X | RC73XA       | `rc73xa`      |

Detection matches the SMBIOS baseboard manufacturer `ASUSTeK COMPUTER INC.` and one of those
baseboard products as whole strings, ignoring case and surrounding spaces. RC72L is admitted on the
strength of HHD's product-name match, not an observed board. Any other ASUS machine, and any other
maker's board with the same name, is declined. Startup reads the identity again from the registry
copy of SMBIOS and refuses to start if it no longer matches the detected model.

## What each model gets

The same on all four unless a column says otherwise. "HC" and "HHD" name the reference each part
follows.

| Capability                  | Ally                                   | Ally X   | Xbox Ally                    | Xbox Ally X   | Transport and reference                                                             |
| --------------------------- | -------------------------------------- | -------- | ---------------------------- | ------------- | ----------------------------------------------------------------------------------- |
| Sustained power (SPL)       | 5-30                                   | 5-30     | 5-35                         | 5-35          | ATKACPI DEVS 0x001200A3 (HC)                                                        |
| Boost power (SPPT and FPPT) | yes                                    | yes      | yes                          | yes           | ATKACPI 0x001200A0 and 0x001200C1 written together (HC)                             |
| Performance mode            | yes                                    | yes      | yes                          | yes           | ATKACPI 0x00120075 Silent/Performance/Turbo (HC, HHD)                               |
| Power presets               | 10/15/25                               | 13/17/25 | 13/17/25                     | 13/17/25      | HC's three profiles, mode first, then watts                                         |
| Fan curve                   | yes                                    | yes      | yes                          | yes           | ATKACPI CPU/GPU (+ mid when present) curves (HC)                                    |
| Fan readings                | yes                                    | yes      | yes                          | yes           | ATKACPI 0x00110013/0x00110014 (HC)                                                  |
| Charge limit                | yes                                    | yes      | yes                          | yes           | ATKACPI 0x00120057 (HC)                                                             |
| Aura lighting               | yes                                    | yes      | yes                          | yes           | HID report 0x5D (HC); Xbox models also put the lamp array in autonomous mode (HHD)  |
| Controller                  | yes                                    | yes      | yes                          | yes           | XInput, XUSB 0B05:1ABE/1B4C (HC); Windows.Gaming.Input only if no slot is found     |
| Rumble                      | yes                                    | yes      | yes                          | yes           | XInput (HC)                                                                         |
| Motion                      | yes                                    | yes      | yes                          | yes           | WinRT sensors, legacy Sensor API fallback (HC), HC axis maps                        |
| Front OEM buttons           | Command Center, Armoury Crate, Library | same     | Armoury Crate, Library, Xbox | same          | Vendor report 0x5A codes 0xA6/0x38/0x93 (HC); also F21/F22 on the Xbox models (lab) |
| M1 and M2                   | yes                                    | yes      | yes                          | yes           | HC's controller tables make them F18/F17 (HC, lab)                                  |
| Glyphs                      | rog-ally                               | rog-ally | rog-xbox-ally                | rog-xbox-ally | handheld-controller-glyphs                                                          |

HHD's mouse mode on the Armoury Crate hold and its five-second re-send of controller tables are not
reproduced. An uncertain write is never retried.

## How the parts behave

**Power.** The sustained limit writes SPL; the boost limit writes SPPT and FPPT to one value, as
HC's short limit does. Writes are ordered so that SPL <= SPPT <= FPPT holds after each one, spaced
100 ms apart as HHD does. A paired command from AutoTDP moves all three to the one target. Each
command is verified by reading the three limits back through DSTS. If the original mode or any limit
is unreadable, the write is refused. The first write of a cycle journals the original mode and
limits; stop restores the mode first (a mode change resets the limits), then the limits.

**Fans.** A curve is eight points from 20 to 110 °C with non-falling duties, clamped to 99 % as HC
does, written to every fan. "Custom" waits for the next curve command without changing hardware.
"Automatic" writes back the curves captured before the first change. If any present fan's original
curve cannot be read, the write is refused. A write is verified only when every present fan reads
back the requested curve. An unverified restore remains in the recovery record and is not retried
automatically; the fans stay usable, and the next fan command re-arms the captured original for the
following release. Power restores work the same way.

**Charge limit.** 40-100 %, verified by readback, and never reverted: it is a user setting.

**Lighting.** Brightness in four steps, solid colour per stick ring, breathing between the two ring
colours, colour cycle and rainbow at three speeds. One colour goes out as HC's all-zone message; two
different ring colours use HC's per-zone path, which always runs at the slow speed. Aura cannot be
read back, so every lighting command is reported as applied but unverified, and WSGM's lighting
restore owns reapplying it.

**Controller.** Every Ally, the Xbox models included, exposes its pad as an XUSB device with VID
0B05 and product ID 1ABE or 1B4C, which HC reads through XInput as an `XboxAdaptiveController`. When
WSGM manages the controller, the plugin finds the XInput slot whose capabilities report that VID and
one of those product IDs, publishes the XUSB, GIP and XInput HID nodes for WSGM to hide, and writes
the controller tables so that M1 and M2 send F18 and F17. It then reads the pad at 125 Hz, merges
the OEM buttons and the IMU into each sample, and drives rumble through XInput. HC has no other
route; only if no slot is found does the plugin try Windows.Gaming.Input, which has no guide button.
A pad with no node to hide is left alone, because Steam would otherwise see it beside the virtual
one. Release zeroes the motors, stops reading, and writes the factory M1/M2 tables back. Those
tables cannot be read, so a release that touched them is reported as unverified even when every
write was acknowledged; a release with nothing acquired is clean. A reader that faults is released
and acquired again on resume or when controller management is turned back on.

**OEM buttons.** Front buttons come from the vendor collection's 0x5A reports, on every model as in
HC. HC treats 0x93 as Library and 0xA7/0xA8 as M2 press/release; 0xA6 and 0x38 map to each model's
front controls, and none reports a long press. A release-less press is latched into the controller
sample for 150 ms. The Xbox models also get F21/F22 from a low-level keyboard hook, which is how the
lab saw those buttons arrive, and on the XInput route their Xbox button maps to the QAM as in HHD.
M1 and M2 are claimed by the same hook, only while the controller tables are applied; the hook is
installed only while it has a key to claim. A button that reports on both the vendor collection and
the keyboard is counted once. If the vendor reader stops, the fault is reported and the reader
restarts on resume.

**Motion.** WinRT gyrometer and accelerometer at their minimum report interval, or the legacy Sensor
API's standard motion fields when WinRT has none. HC's axis map is applied once, then the Claw
plugin's stationary gyro-offset correction and frame resampler.

## What HC establishes and a lab run confirms

HC answers each open question on every model, since its Xbox classes inherit the Ally ones:

- Controller route: XInput. HC matches the XUSB device 0B05:1ABE/1B4C on every Ally and reads it as
  an `XboxAdaptiveController` (`ControllerManager.cs:2236-2243`). The 0.2.1 RC73XA run that saw no
  XInput slot and no gamepad collection used a tool version without a HidHide allowance, and a
  cloaked pad looks exactly like that.
- Front buttons: vendor codes 0xA6, 0x38 and 0x93 on every model (`ROGAlly.cs:53-83, 485-505`). The
  F21/F22 keys the RC73XA run saw are handled as well, and a press on both paths counts once.
- Rear buttons: HC's table makes M1 send F18 and M2 send F17 (`ROGAlly.cs:163-168, 263-282`); the
  RC73XA run agrees.
- Motion: HC's per-model axis matrices, including the Xbox models' differing gyro signs
  (`Resources/Devices/*.json`).
- Aura: report 0x5D with HC's speed bytes on every model. For Windows Dynamic Lighting, HC turns
  `HKCU\Software\Microsoft\Lighting\AmbientLightingEnabled` off while it runs and restores it on
  stop (`DynamicLightingManager.cs:117-205`); this plugin sends HHD's lamp-array step instead.

The one thing HC cannot settle is readback: HC never reads the power limits, and its fan-curve
getter is never called. The plugin refuses a power or fan write whose original it cannot read and
journal, so a model whose DSTS does not report them keeps those writes refused until that is known.

Bring-up evidence comes from the Device Lab tester wizard (`wsgm-device`). A returned `.wsgmlab`
report is summarized with `wsgm-device report <file.wsgmlab>`; fold what it shows into
`AllyModels.cs` and `PROVENANCE.md`.

## Building

```powershell
dotnet build src/WSGM.Device.Asus.RogAlly/WSGM.Device.Asus.RogAlly.csproj
dotnet test tests/WSGM.Device.Asus.RogAlly.Tests/WSGM.Device.Asus.RogAlly.Tests.csproj
```

The tests need no hardware: they drive the plugin through fake transports. Packaging follows the
Claw plugin: `./eng/pack-device.ps1 -Source src/WSGM.Device.Asus.RogAlly -RequireGlyphs`. The
curated bundle entry keeps the package out of setup
(`plugins/curated/wsgm.device.asus.rog-ally.json`, `"bundle": false`) until a lab report has been
reviewed.

## Licence

MIT, see `LICENSE`. The package links only the MIT Device SDK. No HC or HHD code is included; see
`THIRD_PARTY_NOTICES.md` for the glyph artwork.
