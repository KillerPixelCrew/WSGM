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
| Controller                  | yes                                    | yes      | yes                          | yes           | XInput, XUSB 0B05:1ABE/1B4C (HC)                                                    |
| Rumble                      | yes                                    | yes      | yes                          | yes           | XInput (HC)                                                                         |
| Motion                      | yes                                    | yes      | yes                          | yes           | WinRT sensors, legacy Sensor API fallback (HC), HC axis maps                        |
| Front OEM buttons           | Command Center, Armoury Crate, Library | same     | Armoury Crate, Library, Xbox | same          | Vendor report 0x5A codes 0xA6/0x38/0x93 (HC); also F21/F22 on the Xbox models (lab) |
| M1 and M2                   | yes                                    | yes      | yes                          | yes           | HC's controller tables make them F18/F17 (HC, lab)                                  |
| Glyphs                      | rog-ally                               | rog-ally | rog-xbox-ally                | rog-xbox-ally | handheld-controller-glyphs                                                          |

HHD's mouse mode on the Armoury Crate hold and its five-second re-send of controller tables are not
reproduced. An uncertain write is never retried.

## How the parts behave

**Power.** The sustained limit writes SPL; the boost limit writes SPPT and FPPT to one value, as
HC's short limit does. Writes go SPL, then SPPT and FPPT, as HC writes them, spaced 100 ms apart as
HHD does. Every power-limit command carries both values as WSGM decided them
(`DevicePowerPair.TryResolve` checks them), and a unified target from AutoTDP moves all three to it.
As in HC, an accepted write is trusted and published as written, without a readback. A write that
throws is indeterminate and performs no rollback. The first write of a cycle journals the original
mode and limits when DSTS reports them; stop restores the mode first (a mode change resets the
limits), then the limits. A firmware that does not report them is written anyway, without a restore
point.

**Fans.** A curve is eight points from 20 to 110 °C with non-falling duties, clamped to 99 % as HC
does, written to every fan. "Custom" waits for the next curve command without changing hardware.
"Automatic" writes back the curves captured before the first change. Accepted writes are published
as written without a readback. If the original curves cannot be read, the write goes ahead anyway
and stop replaces a custom curve with HC's factory tables instead of a captured original. Completed
restores clear the recovery entry; failed restores leave it Pending with no status write, and the
next start writes that original once. Power restores work the same way.

**Charge limit.** 40-100 %, published as written without a readback, and never reverted: it is a
user setting.

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
the OEM buttons and the IMU into each sample, and drives rumble through XInput. XInput is the only
route, as in HC. Discovery finds nothing unless the ASUS XInput slot and the pad's device nodes are
both present, because a pad with no node to hide would sit beside the virtual one in Steam. When
they are not, the controller reports Degraded and the plugin waits for them with the SDK's
`DeviceReconnect`, attaching as soon as both appear; after a wake they return a few seconds late and
not together, and giving up on the first look left the controller dead. Release is best effort, as
in HC: it zeroes the motors, stops reading and writes the factory M1/M2 tables back, and a write
that fails is traced. Those tables cannot be read, so an acknowledged write is all that can be
known.

**When the pad drops out.** The Xbox Ally X takes its pad and vendor collection off the bus about a
second before Windows reports a suspend and brings them back a few seconds after the wake. As in
HC's `Device_Removed` and `Device_Inserted`, that is not a fault: the reader sends one neutral
frame, the service goes Degraded and `DeviceReconnect` checks every half second, and when the pad is
back it writes the controller tables once and restarts the reader. If that fails the controller
faults until controller management is turned off and on; the tables are not written again. The pad's
identities are published again; the host keeps a virtual pad of the same kind, and HidHide keeps the
physical one hidden. A pad that is not back when a resume acquires it is taken the same way when it
appears. The vendor collection is reopened likewise. Every reader failure is treated as the pad
going away, never as a device fault, so fans, TDP and the OEM buttons stay up while it is gone.

**Button diagnostics.** Every OEM button edge is logged with the transport it arrived on and whether
it was taken or ignored as the other transport's echo.

**OEM buttons.** Front buttons come from the vendor collection's 0x5A reports, on every model as in
HC. HC treats 0x93 as Library and 0xA7/0xA8 as M2 press/release; 0xA6 and 0x38 map to each model's
front controls, and none reports a long press. A release-less press is latched into the controller
sample for HC's 200 ms key press delay by the SDK's `OemButtonLatch`. The Xbox models also get
F21/F22 from a low-level keyboard hook, which is how the lab saw those buttons arrive. On the Xbox
models the Xbox button is Steam's Guide, as HC reads it, Library is Steam's Quick Access, and
Armoury Crate carries no Steam button: it is published as the companion-application control, which
WSGM opens its overlay from unless the user assigns it otherwise. The classic models keep Armoury
Crate as the Guide and Command Center as Quick Access, since they have no Xbox button. A vendor code
the model does not map is traced once per cycle. M1 and M2 are claimed by the same hook, only while
the controller tables are applied; the hook is installed only while it has a key to claim. A button
that reports on both the vendor collection and the keyboard is counted once. If the vendor
collection is missing or its reader stops, the service reports Degraded and reopens the collection
when it is back.

**Motion.** WinRT gyrometer and accelerometer at their minimum report interval, or the legacy Sensor
API's standard motion fields through the SDK's `LegacyMotionStream` when WinRT has none. The SDK's
`MotionSampleBuilder` applies HC's axis map once and subtracts the measured zero-rate offset, and
its `GyroFrameResampler` spreads the readings over the controller frames. Motion streams for as long
as the cycle runs and stops only for suspend and stop; there is no host signal to stop it.

## What HC establishes and a lab run confirms

HC answers each open question on every model, since its Xbox classes inherit the Ally ones:

- Controller route: XInput. HC matches the XUSB device 0B05:1ABE/1B4C on every Ally and reads it as
  an `XboxAdaptiveController` (`ControllerManager.cs:2236-2243`). The 0.2.1 RC73XA run that saw no
  XInput slot and no gamepad collection used a tool version without a HidHide allowance, and a
  cloaked pad looks exactly like that.
- Front buttons: vendor codes 0xA6, 0x38 and 0x93 on every model (`ROGAlly.cs:53-83, 485-505`). The
  F21/F22 keys the RC73XA run saw are handled as well, and a press on both paths counts once.
- Rear buttons: with HC's table (`ROGAlly.cs:163-168`) the left button sends F17 and the right F18,
  as an RC73XA tester confirmed; without it the RC73XA firmware sends F18 left and F17 right, and
  the Xbox models watch those keys too.
- Motion: HC's per-model axis matrices, including the Xbox models' differing gyro signs
  (`Resources/Devices/*.json`).
- Aura: report 0x5D with HC's speed bytes on every model. For Windows Dynamic Lighting, HC turns
  `HKCU\Software\Microsoft\Lighting\AmbientLightingEnabled` off while it runs and restores it on
  stop (`DynamicLightingManager.cs:117-205`); this plugin sends HHD's lamp-array step instead.

The one thing HC cannot settle is readback: HC never reads the power limits, and its fan-curve
getter is never called. The plugin writes as HC does whether or not DSTS reports them, so readback
only decides whether a result is verified and whether stop has a captured original to restore. A lab
run shows which models report them.

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
curated entry includes the package in setup (`plugins/curated/wsgm.device.asus.rog-ally.json`,
`"bundle": true`) with validation marked `blind`. Setup matches its declared hardware rules;
inclusion is not a hardware acceptance claim. Follow the root manual-first validation policy before
running the automated tests above.

## Licence

MIT, see `LICENSE`. The package links only the MIT Device SDK. No HC or HHD code is included; see
`THIRD_PARTY_NOTICES.md` for the glyph artwork.

## Source map

[RogAllyPlugin](RogAllyPlugin.cs) creates the service cycle; its
[Surface](RogAllyPlugin.Surface.cs), [Commands](RogAllyPlugin.Commands.cs),
[Observation](RogAllyPlugin.Observation.cs) and [Recovery](RogAllyPlugin.Recovery.cs) partials own
semantic publication, dispatch and recovery ordering. [AllyModels](AllyModels.cs) contains every
per-model fact; [AllyIdentity](AllyIdentity.cs) matches it against live identity.

[AllyServices](AllyServices.cs) adapts the shared SDK service lifecycle;
[AllyAcpiCapabilities](AllyAcpiCapabilities.cs) implements power, fans and charge over
[AsusAcpi](AsusAcpi.cs). [AllyHid](AllyHid.cs) and [AllyProtocol](AllyProtocol.cs) own controller
and Aura messages; [AllyControllerService](AllyControllerService.cs) owns acquisition/reconnect and
release. [AllyInput](AllyInput.cs), [AllyOemServices](AllyOemServices.cs) and
[AllyMotion](AllyMotion.cs) produce canonical input. [AllyRecoveryJournal](AllyRecoveryJournal.cs)
binds temporary originals to the correct firmware. Follow [PROVENANCE.md](PROVENANCE.md) before
treating any source-derived mapping as measured hardware behavior.
