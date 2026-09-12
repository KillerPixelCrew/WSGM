# WSGM Device Plugin — MSI Claw 8 AI+ A2VM

The device plugin that teaches [WSGM](https://github.com/KillerPixelCrew/WSGM) an MSI Claw 8 AI+
A2VM: power and charge limits, fan behaviour, lighting, the controller and its motion sensors, the
OEM buttons, variable refresh, Intel Endurance Gaming and its target, the prebuilt shader download,
the shared GPU memory percentage, driver frame presentation, and the physical glyphs shown in Steam.
The display capabilities are published only when the driver answers for them.

The plugin also declares four power profiles for WSGM's Device page and Steam QAM: Super Battery
(8/9 W, Better Battery), Balanced (17/18 W, balanced Windows mode), and Extreme Performance (30/31
W, Best Performance), plus Full Power (37/37 W, Best Performance). The watt pair is PL1/PL2. Their
EC scenarios on AC are Eco, Green, Sport and Sport respectively; all four use Comfort on battery.
WSGM selects the scenario before applying watt limits and Windows mode, then derives Custom whenever
any observed target no longer matches. Presets do not directly change CPU boost, Intel Endurance
Gaming, fan controls, or the Windows power plan. Firmware effects of the scenario itself remain
device-dependent.

The sustained descriptor declares its boost companion for coordinated runtime commands. AutoTDP
requests one watt target; the plugin applies that value to both PL1 and PL2, orders the writes to
preserve PL1 <= PL2, and verifies or rolls back the complete pair. Independent manual commands keep
their existing behavior. Host shutdown restores the original sustained and boost values separately.

Full Power is a WSGM addition using the device's supported maximum watt limits. The other presets
and scenario mapping follow `ClawA1M.PowerProfileManager_Applied`, inherited by `ClawA2VM`, in the
local Handheld Companion reference at revision `5c94abca83f8711ff5620906871b31a41c76bf05`. HC's
battery `ShiftType.None` maps to mode 0 with the active bits set, which means Comfort (`0xC0`), not
disabled SHIFT. The plugin verifies the exact scenario byte, publishes the resulting watt pair, and
restores the first original scenario before its original watt pair at cleanup. Post-command
observation is cancelled on quiesce and bounded by the command deadline and a two-second publication
budget. A scenario publication failure reports uncertainty without claiming a rollback was
attempted; the recovery journal still owns restoration of temporary state. Fake-transport tests
cover these paths; no new live AC/battery scenario pass is claimed.

It is also **the reference implementation of the
[WSGM Device SDK](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.Device.Sdk)** — the
plugin to read, and copy from, when writing one for another handheld. That is why it is MIT: a
reference nobody may copy is not a reference.

Motion shutdown waits up to two seconds and honors caller cancellation. If a worker is still
running, the session keeps its sensor until both workers finish and refuses another start. Cleanup
failures remain visible. Truncated power/fan responses fail before decoding; unknown fan modes are
rejected before transport access. These paths are covered with fakes, without a new hardware pass.

## What a plugin actually does

WSGM owns the session and the UI; the plugin owns the hardware. It publishes _semantic capabilities_
— "a TDP limit between these bounds", "a fan curve", "a lighting zone" — and WSGM renders them and
routes user intent back as commands. WSGM never touches the device.

This one is a worked example of the parts that are easy to get wrong:

| File                              | What it demonstrates                                                               |
| --------------------------------- | ---------------------------------------------------------------------------------- |
| `Claw8A2VmPlugin.cs`              | the lifecycle: detect, start, command, settings, stop                              |
| `ClawCapabilities.cs`             | publishing capabilities and reporting refusals honestly                            |
| `MsiWmiPlatform.cs`               | the vendor WMI surface behind power and fans                                       |
| `WindowsHidTransports.cs`         | HID transports for OEM controls and lighting                                       |
| `WindowsMotionSource.cs`          | physical legacy-Sensor-API IMU polling, freshness, and zero-rate offset correction |
| `LegacyPhysicalMotionSensors.cs`  | exact Intel ISS/LSM6DSO COM identity, fields, interval ownership, and cleanup      |
| `ArcSyncTransport.cs`             | variable refresh through Intel's Graphics Control Library                          |
| `Intel3dFeatureTransport.cs`      | the pinned IGCL 3D-feature ABI behind Endurance Gaming and prebuilt shaders        |
| `IntelGraphicsMemoryTransport.cs` | driver settings that are registry values rather than API calls: GPU memory, VSync  |
| `ClawRecoveryJournal.cs`          | leaving the device safe when a cycle ends badly                                    |

Motion writes no per-report file. Nothing here may log at the 100 Hz sensor cadence: a CSV of every
report cost roughly 10 MB per five minutes of play, which is not a diagnostic anyone should leave on
a handheld's SSD. The ordinary WSGM log receives transitions only — the measured offset, a read
failure and its recovery — never per-report data. Investigations that genuinely need the raw stream
add the capture temporarily and remove it with the finding, as the offset below was.

### This part's gyroscope has a zero-rate offset, and only the plugin can remove it

Intel's ISS stack publishes the LSM6DSO's rates uncorrected. Two eight-minute stationary captures on
the reference unit, hours apart, both measured the same offset in sensor space:

|                         | X     | Y     | Z     |
| ----------------------- | ----- | ----- | ----- |
| offset (degrees/second) | +0.75 | −0.37 | −0.14 |
| noise, 1σ               | 0.13  | 0.07  | 0.06  |

It is a hardware offset, not a mapping or gravity error: it is identical while the device lies flat
and while it is held tilted, and identical across sessions. Nothing downstream removes it. A Steam
Deck's own gyroscope arrives offset-free, so Steam integrates whatever a Deck target reports — here
that was a permanent 12-count pitch and −6-count yaw, drifting the view along one fixed diagonal
forever. `StationaryGyroBiasCalibrator` therefore measures the offset from ~2 s rest windows and
subtracts it. Every threshold in it comes from the table above and from the same captures: peak
stationary spans of 1.47 degrees/second and 0.023 g over 200 reports. Re-deriving them means
re-capturing, which is the deliberate cost of not shipping the capture. Correction is subtraction
only — a deadband would trade the drift for a dead zone around rest, which is a worse artifact than
the noise it hides.

One case is worth understanding before changing this. A steady yaw is the single motion no
acceleration gate can separate from rest — a constant-radius turn holds both the rate and the
acceleration vector still — so a device powered on aboard a moving vehicle measures that turn as its
offset. No software fixes that without an external heading reference. What the design does instead
is make it temporary: a run of later windows that agree with each other but not with the stored
value replaces it. Clamping refinement to a maximum step, which looks like the safe choice, is the
one thing that must not be done here — every honest window after the turn stops is further away than
such a clamp allows, so the wrong offset would outlive the entire device cycle.

### OEM keyboard side effects

The right OEM button also emits a malformed Windows-key chord: an orphan `G UP` for a short press,
or `Tab UP` for a long press. The plugin suppresses those measured sequences while its OEM service
is active, including on the Windows desktop. It also intercepts Win+G on key-down, as HC does,
before Game Bar can activate. This includes ordinary keyboard Win+G, even with Ctrl/Alt/Shift held.
Normal Win+Tab, modified orphan-up sequences, injected input, volume keys and unknown sequences pass
through.

The synthetic Win-key release uses the full 40-byte Windows x64 `INPUT` record. A keyboard-only
union incorrectly reduced it to 32 bytes, so Windows rejected the release and the hook passed the
firmware chord through. Layout and sequence tests cover the correction without installing a hook or
sending input to the live desktop. No new device observation is claimed by those tests.

The Win release also carries `KEYEVENTF_EXTENDEDKEY`, as in HC's `Helpers/FirmwareWorkarounds.cs` at
revision `5c94abca83f8711ff5620906871b31a41c76bf05`. The earlier orphan-up-only matcher missed HC's
key-down interception. The plugin now consumes the initial G down, repeats and G up, including when
Win is released first. A failed synthetic release fails open without retrying on held-key repeats.
The measured orphan-up handling remains for both G and Tab. These corrections have software tests;
desktop suppression still needs an attended validation on the updated installed package.

## Hardware knowledge is observed, not documented

Every register, report layout and WMI method here was established on a physical device.
`PROVENANCE.md` records the hardware revision it was confirmed against. A different Claw model is a
different device, and detection does not claim it. Both detection and startup require the exact
manufacturer, `MS-1T52` baseboard and `1T52.1` SKU. Startup repeats that check from SMBIOS and
returns before it queries MSI's EC-backed WMI provider, controller inventory, HID endpoints or power
state on any other machine.

That is the honest constraint of this whole category: nothing here can be derived from a datasheet,
so nothing here should be trusted on a machine it was not confirmed on.

## Building

Run these commands from the WSGM repository root. The SDK is shared source under
`src/WSGM.Device.Sdk`; device projects are built and reviewed together.

```powershell
dotnet build src/WSGM.Device.Msi.Claw8A2Vm/WSGM.Device.Msi.Claw8A2Vm.csproj
dotnet test tests/WSGM.Device.Msi.Claw8A2Vm.Tests/WSGM.Device.Msi.Claw8A2Vm.Tests.csproj
```

The tests are unattended and hardware-free. They drive the plugin through `PluginTestKit` against
fake transports, which is the only kind of test that belongs in CI — the real behaviour is only ever
proven on the device.

## Packaging

```powershell
./eng/pack-claw.ps1
```

This publishes framework-dependent (WSGM loads the plugin into its own process, which already has
the runtime), validates the assembled package with Device Lab from the same checkout, then packs the
`.wsgmpkg`. The WSGM commit records the SDK, plugin, and validator source used together.

## Installing

WSGM ships this package as its device component, but only setup's **MSI Claw 8 AI+ A2VM** mode, or a
Custom install that selects it, puts it on disk; that mode is also the only one that enables Device
Integration on a fresh install. A package copied into the slot on a Minimal or Desktop install shows
a banner on the overlay's Device page naming what is missing. To install a build of your own, see
[the authoring guide](https://github.com/KillerPixelCrew/WSGM/blob/master/docs/device-plugin-authoring.md)
— in short, expand the `.wsgmpkg` into a fresh directory and hand it to
`WSGM.exe --install-device-plugin`, which validates it again before replacing the protected slot.

## Licence

MIT. See `LICENSE`. A plugin links only the MIT SDK, never WSGM, so nothing here obliges a derived
plugin to any particular licence. Third-party notices are in
`src/WSGM.Device.Msi.Claw8A2Vm/THIRD_PARTY_NOTICES.md` — the glyph artwork is MIT from
`handheld-controller-glyphs`, and no Intel code is redistributed.

The package places its controls in the SDK shared Power, RGB and Info sections. WSGM can combine
them with its own controls and assign the declared presets separately for AC and battery power.

The fake-hardware publication test writes claw-ui-publication.json beside its test assembly for
WSGM's complete Device-page visual fixture. Refresh that host fixture after changing descriptors.
