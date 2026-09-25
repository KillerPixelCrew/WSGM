# WSGM Device Plugin: ASUS ROG Ally family

This is the device plugin that teaches WSGM the four ASUS ROG Ally handhelds: power limits and the
ASUS performance modes, fan curves, the charge limit, Aura lighting on the stick rings, the
controller with its OEM buttons, rumble and motion, and the physical glyphs Steam shows.

It was built blind. Nobody working on it had an Ally, so every register, report and axis comes from
the Handheld Companion 1.3.1.6 Patreon build and from HHD, and none of it has been run on the
hardware yet. `PROVENANCE.md` cites the source of every fact and lists what a Device Lab report has
to confirm. Everything that differs between the models is one row in `AllyModels.cs`, so a report
corrects data rather than code.

| Model           | Board        | Definition ID |
| --------------- | ------------ | ------------- |
| ROG Ally        | RC71L        | `rc71l`       |
| ROG Ally X      | RC72LA/RC72L | `rc72la`      |
| ROG Xbox Ally   | RC73YA       | `rc73ya`      |
| ROG Xbox Ally X | RC73XA       | `rc73xa`      |

Detection matches the SMBIOS baseboard manufacturer `ASUSTeK COMPUTER INC.` and one of those
baseboard products exactly. Any other ASUS machine, and any other maker's board with the same name,
is declined. Startup reads the identity again from the registry copy of SMBIOS and refuses to start
if it no longer matches the detected model.

## What each model gets

Built blind on all four, awaiting lab evidence. "HC" and "HHD" name the reference each part follows.

| Capability                  | Ally | Ally X | Xbox Ally | Xbox Ally X | Transport and reference                                        |
| --------------------------- | ---- | ------ | --------- | ----------- | -------------------------------------------------------------- |
| Sustained power (SPL)       | 5-30 | 5-30   | 5-35      | 5-35        | ATKACPI DEVS 0x001200A3 (HC)                                   |
| Boost power (SPPT and FPPT) | yes  | yes    | yes       | yes         | ATKACPI 0x001200A0 and 0x001200C1 written together (HC)        |
| Performance mode            | yes  | yes    | yes       | yes         | ATKACPI 0x00120075 Silent/Performance/Turbo (HC, HHD)          |
| Power presets               | 10/15/25 | 13/17/25 | 13/17/25 | 13/17/25 | HC's three profiles, mode first, then watts               |
| Fan curve                   | yes  | yes    | yes       | yes         | ATKACPI CPU/GPU (+ mid when present) curves (HC)               |
| Fan readings                | yes  | yes    | yes       | yes         | ATKACPI 0x00110013/0x00110014 (HC)                             |
| Charge limit                | yes  | yes    | yes       | yes         | ATKACPI 0x00120057 (HC)                                        |
| Aura lighting               | yes  | yes    | yes       | yes         | HID report 0x5D (HC); Xbox models also put the lamp array in autonomous mode (HHD) |
| Controller                  | yes  | yes    | yes       | yes         | XInput (HC); Windows.Gaming.Input if the pad has no XInput slot |
| Rumble                      | yes  | yes    | yes       | yes         | XInput (HC)                                                    |
| Motion                      | yes  | yes    | yes       | yes         | WinRT sensors, legacy Sensor API fallback (HC), HC axis maps   |
| Front OEM buttons           | Command Center, Armoury Crate | same | Armoury Crate, Library, Xbox | same | Vendor report 0x5A codes (HHD); F21/F22 on the Xbox models (lab) |
| M1 and M2                   | yes  | yes    | yes       | yes         | HHD's controller tables make them F18/F17 (HHD, lab)           |
| Glyphs                      | rog-ally | rog-ally | rog-xbox-ally | rog-xbox-ally | handheld-controller-glyphs                            |

Nothing HC or HHD implements for these models is left out. Two things they do are deliberately not
reproduced: HHD's mouse mode on the Armoury Crate hold (that press is published as a long press
instead) and HHD's five-second re-send of the controller tables (an uncertain write is never
retried).

## How the parts behave

**Power.** The sustained limit writes SPL; the boost limit writes SPPT and FPPT to one value, as HC's
short limit does. Writes are ordered so that SPL <= SPPT <= FPPT holds after each one, spaced 100 ms
apart as HHD does. A paired command from AutoTDP moves all three to the one target. Each command is
verified by reading the three limits back through DSTS; if the firmware does not report them the
result is unverified rather than a failure. The first write of a cycle journals the original mode
and limits, and stop restores the mode first (a mode change resets the limits), then the limits.

**Fans.** A curve is eight points from 20 to 110 °C with non-falling duties, clamped to 99 % as HC
does, written to every fan. "Automatic" writes back the curves captured before the first change, or
HC's default tables when nothing was captured. Whether a DSTS read shows the curve in force is not
known, so a readback that differs is reported unverified. The original curves are journalled and
restored on stop.

**Charge limit.** 40-100 %, verified by readback, and never reverted: it is a user setting.

**Lighting.** Brightness in four steps, solid colour per stick ring, breathing between the two ring
colours, colour cycle and rainbow at three speeds. Aura cannot be read back, so every lighting
command is reported as applied but unverified, and WSGM's lighting restore owns reapplying it.

**Controller.** When WSGM manages the controller, the plugin finds the XInput slot whose capabilities
report VID 0B05 and one of the Ally product IDs, publishes the XUSB, GIP and XInput HID nodes for
WSGM to hide, and writes HHD's controller tables so that M1 and M2 send F18 and F17. It then reads
the pad at 125 Hz, merges the OEM buttons and the IMU into each sample, and drives rumble through
XInput. Release zeroes the motors, stops reading, and writes the factory M1/M2 tables back. Those
tables cannot be read, so a release that touched them is reported as unverified even when every
write was acknowledged.

**OEM buttons.** Front buttons come from the vendor collection's 0x5A reports as HHD decodes them:
0xA6 is the guide, 0x38 and 0x93 the QAM, and 0xA7 a long press of the right button. They are
latched into the controller sample for 150 ms and published as OEM events. The Xbox models also get
F21/F22 from a low-level keyboard hook, which is how the lab saw those buttons arrive, and their Xbox
button maps to the QAM as in HHD. M1 and M2 are claimed by the same hook, only while the controller
tables are applied.

**Motion.** WinRT gyrometer and accelerometer at their minimum report interval, or the legacy Sensor
API's standard motion fields when WinRT has none. HC's axis map is applied once, then the Claw
plugin's stationary gyro-offset correction and frame resampler.

## Needs lab evidence

All of it, in the order the Device Lab report should cover it; `PROVENANCE.md` has the full list.
The parts most likely to need correcting:

- The Xbox models' controller route. The RC73XA lab run saw neither an XInput slot nor a HID gamepad
  collection, so the Windows.Gaming.Input fallback and the device nodes to hide are unproven there.
- Whether the Xbox models send 0x5A vendor codes at all once the tables are written, or only the
  F21/F22 keys the lab saw.
- The rear buttons' key codes and physical labels per side.
- Motion axis signs, especially the Xbox models, where HC's gyro and accelerometer signs differ.
- Whether DSTS reads back the power limits, the performance mode and the fan curves.
- The Xbox Ally's watt envelope: HC says up to 35 W, HHD 20 W.
- Aura on the Xbox models, the speed byte order, and the lamp-array step.

Bring-up evidence comes from the Device Lab tester wizard (`wsgm-device`). A returned
`.wsgmlab` report is summarized with `wsgm-device report <file.wsgmlab>`; fold what it shows into
`AllyModels.cs` and `PROVENANCE.md`.

## Building

```powershell
dotnet build src/WSGM.Device.Asus.RogAlly/WSGM.Device.Asus.RogAlly.csproj
dotnet test tests/WSGM.Device.Asus.RogAlly.Tests/WSGM.Device.Asus.RogAlly.Tests.csproj
```

The tests need no hardware: they drive the plugin through fake transports. Packaging follows the
Claw plugin: `./eng/pack-device.ps1 -Source src/WSGM.Device.Asus.RogAlly -RequireGlyphs`. The curated
bundle entry keeps the package out of setup (`plugins/curated/wsgm.device.asus.rog-ally.json`,
`"bundle": false`) until a lab report has been reviewed.

## Licence

MIT, see `LICENSE`. The package links only the MIT Device SDK. No HC or HHD code is included; see
`THIRD_PARTY_NOTICES.md` for the glyph artwork.
