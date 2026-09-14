# WSGM Device Plugin: MSI Claw 8 AI+ A2VM

This is the device plugin that teaches [WSGM](https://github.com/KillerPixelCrew/WSGM) an MSI Claw 8
AI+ A2VM: power and charge limits, fan behaviour, lighting, the controller and its motion sensors,
the OEM buttons, variable refresh, Intel Endurance Gaming and its target, the prebuilt shader
download, the shared GPU memory percentage, driver frame presentation, and the physical glyphs Steam
shows. The display capabilities only get published when the driver actually answers for them.

It is also the **reference implementation of the
[WSGM Device SDK](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.Device.Sdk)**: the
plugin to read and copy from when writing one for another handheld. That is why it is MIT. A
reference nobody may copy is not a reference.

## Power profiles

The plugin declares four profiles for WSGM's Device page and Steam's QAM:

| Preset              | PL1/PL2 | Windows mode     | EC scenario on AC |
| ------------------- | ------- | ---------------- | ----------------- |
| Super Battery       | 8/9 W   | Better Battery   | Eco               |
| Balanced            | 17/18 W | Balanced         | Green             |
| Extreme Performance | 30/31 W | Best Performance | Sport             |
| Full Power          | 37/37 W | Best Performance | Sport             |

All four use Comfort on battery. WSGM selects the scenario before applying the watt limits and the
Windows mode, then derives Custom whenever an observed target stops matching. Presets do not change
CPU boost, Endurance Gaming, the fan controls or the Windows power plan directly, and what the
scenario itself does in firmware is up to the device.

The sustained descriptor names its boost companion so runtime commands can move both together.
AutoTDP sends one watt target, and the plugin applies it to PL1 and PL2 in the order that keeps PL1
<= PL2, then verifies or rolls back the whole pair. Independent manual commands behave as before.
Host shutdown restores the original sustained and boost values separately.

Full Power is a WSGM addition at the device's supported maximum. The other presets and the scenario
mapping follow `ClawA1M.PowerProfileManager_Applied`, inherited by `ClawA2VM`, in the local Handheld
Companion reference at revision `5c94abca83f8711ff5620906871b31a41c76bf05`. HC's battery
`ShiftType.None` maps to mode 0 with the active bits set, which means Comfort (`0xC0`) rather than
SHIFT disabled.

The plugin verifies the exact scenario byte, publishes the resulting watt pair, and at cleanup
restores the first original scenario before its original watt pair. Post-command observation is
cancelled on quiesce and bounded by the command deadline plus a two-second publication budget. If a
scenario publication fails, the plugin reports it as uncertain rather than claiming it rolled
anything back; the recovery journal still owns restoring temporary state. Fake-transport tests cover
these paths, and I have not re-run the AC and battery scenarios on hardware since.

## What a plugin actually does

WSGM owns the session and the UI, the plugin owns the hardware. It publishes semantic capabilities,
things like "a TDP limit between these bounds", "a fan curve", "a lighting zone", and WSGM renders
them and routes user intent back as commands. WSGM never touches the device.

This one is a worked example of the parts that are easy to get wrong:

| File                              | What it demonstrates                                                              |
| --------------------------------- | --------------------------------------------------------------------------------- |
| `Claw8A2VmPlugin.cs`              | the lifecycle: detect, start, command, settings, stop                             |
| `ClawCapabilities.cs`             | publishing capabilities and reporting refusals honestly                           |
| `MsiWmiPlatform.cs`               | the vendor WMI surface behind power and fans                                      |
| `WindowsHidTransports.cs`         | HID transports for OEM controls and lighting                                      |
| `WindowsMotionSource.cs`          | legacy Sensor API IMU polling, freshness, and zero-rate offset correction         |
| `LegacyPhysicalMotionSensors.cs`  | the exact Intel ISS/LSM6DSO COM identity, fields, interval ownership and cleanup  |
| `ArcSyncTransport.cs`             | variable refresh through Intel's Graphics Control Library                         |
| `Intel3dFeatureTransport.cs`      | the pinned IGCL 3D-feature ABI behind Endurance Gaming and prebuilt shaders       |
| `IntelGraphicsMemoryTransport.cs` | driver settings that are registry values rather than API calls: GPU memory, VSync |
| `ClawRecoveryJournal.cs`          | leaving the device safe when a cycle ends badly                                   |

## Motion

Motion acquisition runs on one dedicated worker with a cancellable 2 ms wait after each synchronous
sensor read. It no longer schedules a shared thread-pool continuation every 2 ms or tries to catch
up after a slow read. The bounded publication channel, the sensor counter checks, calibration and
resampling are all unchanged. CPU and gyro responsiveness after that scheduling change still want a
manual check.

Motion shutdown waits up to two seconds and honours caller cancellation. If a worker is still
running, the session keeps its sensor until both workers finish and refuses another start. Cleanup
failures stay visible. Truncated power and fan responses fail before decoding, and unknown fan modes
are rejected before the transport is touched. These paths are covered with fakes rather than a
hardware pass.

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

The right OEM button also emits a malformed Windows-key chord: an orphan `G UP` for a short press,
or `Tab UP` for a long press. The plugin suppresses those sequences while its OEM service is active,
including on the Windows desktop. It also intercepts Win+G on key-down, the way HC does, before Game
Bar can activate, and that includes an ordinary keyboard Win+G even with Ctrl, Alt or Shift held.
Normal Win+Tab, modified orphan-up sequences, injected input, volume keys and unknown sequences all
pass through.

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

Every register, report layout and WMI method was established on real hardware. `PROVENANCE.md`
records the hardware revision it was confirmed against. A different Claw model is a different
device, and detection does not pretend otherwise: both detection and startup require the exact
manufacturer, the `MS-1T52` baseboard and the `1T52.1` SKU. Startup repeats that check from SMBIOS
and returns before it queries MSI's EC-backed WMI provider, the controller inventory, HID endpoints
or power state on any other machine.

That is the honest constraint of this whole category. None of it can be derived from a datasheet, so
none of it should be trusted on a machine it was not confirmed on.

## Building

Run these from the WSGM repository root. The SDK is shared source under `src/WSGM.Device.Sdk`, and
device projects are built and reviewed together.

```powershell
dotnet build src/WSGM.Device.Msi.Claw8A2Vm/WSGM.Device.Msi.Claw8A2Vm.csproj
dotnet test tests/WSGM.Device.Msi.Claw8A2Vm.Tests/WSGM.Device.Msi.Claw8A2Vm.Tests.csproj
```

The tests are unattended and need no hardware. They drive the plugin through `PluginTestKit` against
fake transports, which is the only kind of test that belongs in CI, since the real behaviour is only
ever proven on the device.

The fake-hardware publication test writes `claw-ui-publication.json` beside its test assembly, for
WSGM's Device-page visual fixture. Refresh that host fixture after changing descriptors.

## Packaging

```powershell
./eng/pack-claw.ps1
```

This publishes framework-dependent, since WSGM loads the plugin into its own process which already
has the runtime, validates the assembled package with Device Lab from the same checkout, then packs
the `.wsgmpkg`. The WSGM commit records the SDK, plugin and validator source that were used
together.

## Installing

WSGM ships this package as its device component, but only setup's **MSI Claw 8 AI+ A2VM** mode, or a
Custom install that selects it, puts it on disk. That mode is also the only one that enables Device
Integration on a fresh install. A package copied into the slot on a Minimal or Desktop install shows
a banner on the overlay's Device page naming what is missing.

To install a build of your own, see
[the authoring guide](https://github.com/KillerPixelCrew/WSGM/blob/master/docs/device-plugin-authoring.md).
In short: expand the `.wsgmpkg` into a fresh directory and hand it to
`WSGM.exe --install-device-plugin`, which validates it again before replacing the protected slot.

The package puts its controls in the SDK's shared Power, RGB and Info sections, so WSGM can combine
them with its own controls and assign the declared presets separately for AC and battery.

## Licence

MIT, see `LICENSE`. A plugin links only the MIT SDK and never WSGM, so nothing here obliges a
derived plugin to any particular licence. Third-party notices are in
`src/WSGM.Device.Msi.Claw8A2Vm/THIRD_PARTY_NOTICES.md`: the glyph artwork is MIT from
`handheld-controller-glyphs`, and no Intel code is redistributed.
