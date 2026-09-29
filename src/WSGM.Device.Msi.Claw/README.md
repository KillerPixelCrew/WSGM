# WSGM Device Plugin: MSI Claw

This is the device plugin that teaches [WSGM](https://github.com/KillerPixelCrew/WSGM) the MSI Claw
family: power and charge limits, fan behaviour, lighting, the controller and its motion sensors, the
OEM buttons, and the physical glyphs Steam shows. Intel graphics-driver controls such as variable
refresh, Endurance Gaming and the shared GPU memory split live in the `wsgm.gpu.intel` plugin.

## Models

| Model               | Board     | HC class  | Status                                  |
| ------------------- | --------- | --------- | --------------------------------------- |
| Claw A1M            | `MS-1T41` | ClawA1M   | built from HC 1.3.1.6, no hardware pass |
| Claw 7 AI+ A2VM     | `MS-1T42` | ClawA2VM  | built from HC 1.3.1.6, no hardware pass |
| Claw 8 AI+ A2VM     | `MS-1T52` | ClawA2VM  | reference unit, hardware-tested         |
| Claw A8 BZ2EM       | `MS-1T8K` | ClawBZ2EM | built from HC 1.3.1.6, no hardware pass |
| Claw 8 EX AI+ CG3EM | `MS-1T91` | ClawCG3EM | built from HC 1.3.1.6, no hardware pass |

Handheld Companion derives every Claw from `ClawA1M`, so the MSI_ACPI power, fan, charge and
scenario registers, the MCU mode switch and lighting profile, the `MSI_Event` OEM buttons and the
Win+G chord are one protocol across the family. What differs is in `ClawModels.cs`, one row per
model: the power ranges and presets, the CG3EM's User scenario byte (6 rather than 3), the BZ2EM's
extra boost register `0x52`, the motion sensors and axis signs, and the A1M's on/off rumble. The
lighting and paddle-mapping addresses follow HC's MCU firmware table (`0x01FA` for lighting on
revisions 0163 and 0211, `0x024A` elsewhere). Detection matches the baseboard manufacturer and
product, as HC does; the SKU, EC firmware, BIOS and MCU revision are logged at start and never gated
on.

Like HC, the plugin writes and trusts the write. A readback only upgrades a result to verified; a
mismatch leaves it unverified, publishes the written value and changes nothing on the device. Power,
fans, charge and lighting are offered even where the firmware reads back nothing useful, and power
is re-asserted every five seconds while the EC reports other limits, as HC's TDP watchdog does.

Only the Claw 8 AI+ A2VM's DirectInput report layout was measured, so only it is decoded at fixed
byte offsets. Every other model is decoded through its HID report descriptor
(`HidDescriptorGamepad`), the way HC reads every Claw through DirectInput, with HC's button indices
and the reference unit's paddle order. The A1M's rumble follows HC's `DClawController`: each motor
on at 193 or off, at most one write per 100 ms. The haptic capabilities it publishes say so (10
frames a second, 100 ms minimum pulse, no start floor), and the host paces output to them and never
holds back a stop. The curated record lists `MS-1T52` as the only tested board, so Setup and the
Plugins page call the package blind on the other four.

After a wake the pad comes back a few seconds late. When no controller is found at acquire, the
controller service reports Degraded and waits for it with the SDK's `DeviceReconnect`, taking it as
soon as it answers. A reader that stops is treated the same way: the pad went away, which is never a
device fault, so fans, TDP and the OEM buttons stay up.

The package id was `wsgm.device.msi.claw-8-a2vm` before it covered the family. Setup removes that
package when it installs this one, and the plugin moves a recovery journal left in the old id's
state folder into its own on first start, so a crash under 2.0.3 is still restored.

It is also the **reference implementation of the
[WSGM Device SDK](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.Device.Sdk)**: the
plugin to read and copy from when writing one for another handheld. That is why it is MIT. A
reference nobody may copy is not a reference.

The overlay descriptors mark sustained power as a primary control, keep its boost companion
adjacent, and mark numeric read-only telemetry compact. These layout hints change presentation only;
command bounds, transport sequencing and hardware verification are unchanged.

## Power profiles

The plugin declares four profiles for WSGM's Device page and Steam's QAM. Watts are PL1/PL2 per
model, from HC's TDP overrides after HC's clamp to `cTDP`, the range both limits keep. The CG3EM's
declared 15 W Better Battery therefore arrives as 20 W, as it does in HC. On the A8 every boost
write also goes to `0x52`, as HC's `set_short_limit` does, and a restore puts back the `0x52` value
read before the first write.

| Preset              | A1M     | A2VM (7 and 8) | A8 BZ2EM | 8 EX CG3EM | Windows mode     | EC scenario on AC |
| ------------------- | ------- | -------------- | -------- | ---------- | ---------------- | ----------------- |
| Super Battery       | 20/20 W | 8/9 W          | 15/15 W  | 20/20 W    | Better Battery   | Eco               |
| Balanced            | 30/30 W | 17/18 W        | 20/20 W  | 25/37 W    | Balanced         | Green             |
| Extreme Performance | 35/35 W | 30/31 W        | 28/28 W  | 30/37 W    | Best Performance | Sport             |
| Full Power          | 45/45 W | 37/37 W        | 35/35 W  | 37/37 W    | Best Performance | Sport             |
| Range               | 20-45 W | 8-37 W         | 15-35 W  | 20-37 W    |                  |                   |

All four use Comfort on battery. WSGM selects the scenario before applying the watt limits and the
Windows mode, then derives Custom whenever an observed target stops matching. Presets do not change
CPU boost, Endurance Gaming, the fan controls or the Windows power plan directly, and what the
scenario itself does in firmware is up to the device.

The sustained descriptor names its boost companion so runtime commands can move both together.
AutoTDP sends one watt target, and the plugin writes it to PL1 and then PL2, 200 ms apart, in HC's
order. Host shutdown writes the original sustained and boost values back.

Full Power is a WSGM addition at the device's supported maximum. The other presets and the scenario
mapping follow `ClawA1M.PowerProfileManager_Applied`, which every Claw inherits, in Handheld
Companion 1.3.1.6 (`_ref/HandheldCompanion`). HC's battery `ShiftType.None` maps to mode 0 with the
active bits set, which means Comfort (`0xC0`) rather than SHIFT disabled.

The plugin reads the SHIFT byte from `Get_AP` block 0 and writes it with HC's arithmetic, publishes
the resulting watt pair, and at cleanup writes the first original scenario before its original watt
pair. HC also applies Sport or Comfort on every profile change that has no preset scenario; WSGM has
no such event for manual or AutoTDP limits, so a scenario only changes when a preset or the user
picks one. Post-command observation is cancelled on quiesce and bounded by the command deadline plus
a two-second publication budget. If a scenario publication fails, the plugin reports it as uncertain
rather than claiming it rolled anything back; the recovery journal still owns restoring temporary
state. Fake-transport tests cover these paths, and I have not re-run the AC and battery scenarios on
hardware since.

## What a plugin actually does

WSGM owns the session and the UI, the plugin owns the hardware. It publishes semantic capabilities,
things like "a TDP limit between these bounds", "a fan curve", "a lighting zone", and WSGM renders
them and routes user intent back as commands. WSGM never touches the device.

This one is a worked example of the parts that are easy to get wrong:

| File                      | What it demonstrates                                                                        |
| ------------------------- | ------------------------------------------------------------------------------------------- |
| `ClawPlugin.cs`           | the lifecycle: detect, start, command, settings, stop                                       |
| `ClawModels.cs`           | every per-model fact, one row per Claw                                                      |
| `ClawCapabilities.cs`     | publishing capabilities and reporting refusals honestly                                     |
| `MsiWmiPlatform.cs`       | the vendor WMI surface behind power and fans                                                |
| `WindowsHidTransports.cs` | the MCU (mode switch, lighting, paddle mapping), the gamepad reader and rumble              |
| `WindowsMotionSource.cs`  | the IMU through the SDK's Sensor API stream: sensor order per model and the axis conversion |
| `HidDescriptorGamepad.cs` | the DirectInput pad through its HID descriptor, for the models without a measured layout    |
| `ClawRecoveryJournal.cs`  | leaving the device safe when a cycle ends badly                                             |

## Motion

The A2VM and CG3EM expose their IMU as Intel's custom "Physical" sensors, described below. The A1M
and A8 have no such declaration in HC, which reads them through WinRT's default gyrometer and
accelerometer. The plugin binds the same standard Sensor API sensors directly through the same COM
edge, events and polling fallback, with the report timestamp standing in for the hardware counter.
WinRT's projection would leave finalizable objects for every event. Each model applies HC's shared
axis swap and its own signs once. The COM edge, the event sink and the poll now live in the SDK
(`LegacyMotionSensors` and `LegacyMotionStream` in `WSGM.Device.Sdk.Windows`), shared with the Ally
plugin; `WindowsMotionSource.cs` only picks the sensors and converts the axes.

On the A2VM, motion reports arrive by Sensor API event: the SDK registers an `ISensorEvents` sink on
both sensors for `SENSOR_EVENT_DATA_UPDATED`, pairs each fresh gyrometer report (by hardware
counter) with the latest accelerometer report, and hands the reading straight to the SDK's
`MotionSampleBuilder` for the axis map and offset correction, with no queue in between. Polling the
sensors every 2 ms cost the plugin 12 % of WSGM's idle CPU and the Intel driver host 5 % of a core,
four polls in five returning the previous report (docs/perf). The 2 ms poll on a dedicated thread
remains only as the fallback when a sink cannot be registered, and the log says so when it falls
back. The gyrometer asks for its 10 ms driver minimum and the accelerometer for the gyrometer's
interval rather than its own 2 ms minimum. Read off the virtual Deck with a raw HID handle, the
accelerometer value changes about 80 times a second at either request, in alternating 8 and 16 ms
steps, so the slower request saves the callbacks and changes nothing Steam receives; at 2 ms the
extra callbacks also cost the gyrometer, whose distinct values fell from 125 to 76 a second
(docs/perf, 2026-09-26). Event delivery has not had a hardware pass yet: gyro responsiveness, drift
after a stop and start, and the driver host's CPU want a manual check.

The motion service runs with the device cycle: start and resume open the stream, suspend and stop
close it, and WSGM sends no signal about whether anything reads motion. The `MotionSampleBuilder`
and its zero-rate offset calibrator live on the source, not the stream, so a restart after a wake
carries the measured offset instead of drifting until the next rest window.

Motion shutdown waits up to two seconds for the stream to stop and honours caller cancellation.
Cleanup failures stay visible. Truncated power and fan responses fail before decoding, and unknown
fan modes are rejected before the transport is touched. These paths are covered with fakes rather
than a hardware pass.

Nothing here writes a per-report file, and nothing may log at the 100 Hz sensor cadence. A CSV of
every report cost roughly 10 MB per five minutes of play, which is not something to leave running on
a handheld's SSD. The ordinary WSGM log gets transitions only: the measured offset, a read failure
and its recovery. If an investigation genuinely needs the raw stream, add the capture temporarily
and remove it along with the finding, which is what I did for the offset below.

### The gyroscope has a zero-rate offset, and only the plugin can remove it

Intel's ISS stack publishes the LSM6DSO's rates uncorrected. Two eight-minute stationary captures on
the reference unit, hours apart, both measured the same offset in sensor space:

|                         | X     | Y     | Z     |
| ----------------------- | ----- | ----- | ----- |
| offset (degrees/second) | +0.75 | −0.37 | −0.14 |
| noise, 1σ               | 0.13  | 0.07  | 0.06  |

It is a hardware offset rather than a mapping or gravity error: identical with the device flat and
with it tilted, and identical across sessions. Nothing downstream removes it. A Steam Deck's own
gyroscope arrives offset-free, so Steam simply integrates whatever a Deck target reports, which here
meant a permanent 12-count pitch and −6-count yaw drifting the view along one fixed diagonal
forever.

`StationaryGyroBiasCalibrator` measures the offset from roughly 2 second rest windows and subtracts
it. Every threshold in it comes from the table above and from the same captures: peak stationary
spans of 1.47 degrees/second and 0.023 g over 200 reports. Re-deriving them means capturing again,
which is the deliberate cost of not shipping the capture. Correction is subtraction only, because a
deadband would trade the drift for a dead zone around rest, and that is a worse artifact than the
noise it hides.

One case is worth understanding before you change any of this. A steady yaw is the one motion no
acceleration gate can tell apart from rest, since a constant-radius turn holds both the rate and the
acceleration vector still. So a device powered on aboard a moving vehicle measures that turn as its
offset. No software fixes that without an external heading reference. What the design does instead
is make it temporary: a run of later windows that agree with each other but not with the stored
value replaces it. Clamping refinement to a maximum step looks like the safe choice and is the one
thing that must not be done here, because every honest window after the turn stops is further away
than such a clamp allows, so the wrong offset would outlive the whole device cycle.

## OEM keyboard side effects

The right OEM button also emits a Windows-key chord: Win+G for a short press, Win+Tab for a long
one, sometimes as an orphan key up. HC treats both as silenced chords that raise its QS button, and
so does the plugin, with or without `MSI_Event`: Win+G is intercepted on key-down before Game Bar
can activate (even with Ctrl, Alt or Shift held), unmodified Win+Tab before Task View opens, and
each raises QuickAccess, short or long. A QS from `MSI_Event` and one from the chord within 500 ms
count as one press. Like HC's, the hook cannot tell the button from a keyboard, so an attached
keyboard's Win+G and Win+Tab do the same. Modified Win+Tab, modified orphan-up sequences, injected
input, volume keys and unknown sequences pass through. Where `MSI_Event` is missing but MSI's
`msiapcfg.dll` is installed, the plugin repairs the class the way HC does.

The synthetic Win-key release uses the full 40-byte Windows x64 `INPUT` record. A keyboard-only
union cut that to 32 bytes, so Windows rejected the release and the hook passed the firmware chord
straight through. Layout and sequence tests cover the fix without installing a hook or sending input
to the live desktop.

The Win release also carries `KEYEVENTF_EXTENDEDKEY`, as in HC's `Helpers/FirmwareWorkarounds.cs` at
revision `5c94abca83f8711ff5620906871b31a41c76bf05`. My earlier orphan-up-only matcher missed HC's
key-down interception, so the plugin now consumes the initial G down, the repeats and the G up,
including when Win is released first. A failed synthetic release fails open and does not retry on
held-key repeats. The orphan-up handling stays for both G and Tab. All of this has software tests;
desktop suppression still wants an attended check on the updated installed package.

## Everything here came off a physical device

On the Claw 8 AI+ A2VM, every register, report layout and WMI method was established on real
hardware, and `PROVENANCE.md` records the revision it was confirmed against. The other four models
run the same code paths HC runs on them, from HC's source alone. Detection and startup require an
MSI baseboard in the model table; startup repeats that check from SMBIOS and returns before it
queries MSI's WMI provider, the controller inventory, HID endpoints or power state on any other
machine.

## Building

Run these from the WSGM repository root. The SDK is shared source under `src/WSGM.Device.Sdk`, and
device projects are built and reviewed together.

```powershell
dotnet build src/WSGM.Device.Msi.Claw/WSGM.Device.Msi.Claw.csproj
dotnet test tests/WSGM.Device.Msi.Claw.Tests/WSGM.Device.Msi.Claw.Tests.csproj
```

The tests are unattended and need no hardware. They drive the plugin through `PluginTestKit` against
fake transports, which is the only kind of test that belongs in CI, since the real behaviour is only
ever proven on the device.

The fake-hardware publication test writes `claw-ui-publication.json` beside its test assembly, for
WSGM's Device-page visual fixture. Refresh that host fixture after changing descriptors.

## Packaging

```powershell
./eng/pack-device.ps1 -Source src/WSGM.Device.Msi.Claw -RequireGlyphs
```

This publishes framework-dependent, since WSGM loads the plugin into its own process which already
has the runtime, validates the assembled package with Device Lab from the same checkout, then packs
the `.wsgmpkg`. The WSGM commit records the SDK, plugin and validator source that were used
together.

## Installing

WSGM ships this package as its device component, and setup installs it on a machine whose baseboard
matches one of the five Claws. That mode is also the only one that enables Device Integration on a
fresh install. A package copied into the Plugins folder on a Minimal or Desktop install shows a
banner on the overlay's Device page naming what is missing.

To install a build of your own, see
[the authoring guide](https://github.com/KillerPixelCrew/WSGM/blob/master/docs/device-plugin-authoring.md).
In short: close WSGM and copy the `.wsgmpkg` into `%ProgramFiles%\WSGM\Plugins`; WSGM validates it
again when it loads it.

The package puts its controls in the SDK's shared Power, RGB and Info sections, so WSGM can combine
them with its own controls and assign the declared presets separately for AC and battery.

## Licence

MIT, see `LICENSE`. A plugin links only the MIT SDK and never WSGM, so nothing here obliges a
derived plugin to any particular licence. Third-party notices are in
`src/WSGM.Device.Msi.Claw/THIRD_PARTY_NOTICES.md`: the control glyphs are PromptFont outlines under
the SIL Open Font License 1.1, and the controller images are MIT from `handheld-controller-glyphs`.
