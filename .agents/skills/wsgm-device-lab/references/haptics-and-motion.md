# Haptics and motion

## Separate three layers

1. The virtual target reports protocol intent from Steam or a game.
2. WSGM decodes, generation-checks, coalesces, bounds, and schedules that intent.
3. The plugin maps semantic low/high/trigger channels to the physical actuators it actually owns.

Do not put a weak physical motor's floor into the Steam decoder, and do not flatten Steam's distinct
events into a binary on/off command. Unsupported plugin channels are dropped, never redistributed.

## Steam Deck/Neptune feedback decoded by WSGM

`src/WSGM/Input/ViiperControllerBackend.cs` currently recognizes:

| Command | Semantic decode                                                                                                                                            |
| ------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `0xEB`  | Continuous low/high rumble from little-endian 16-bit values at bytes 5-8, divided by `65535`. Values above `0x7FFF` are real; do not use a signed divisor. |
| `0xDC`  | Bounded symmetric event from byte 3: 0 stop, 1 half, otherwise full; nonzero stops after 150 ms.                                                           |
| `0xE2`  | Companion gain configuration; ignore as output so it does not cancel the event it configures.                                                              |
| `0xEA`  | Interaction haptic with small enum level at byte 4; `level / 3`, symmetric, stops after 35 ms.                                                             |
| `0x8F`  | Pulse strength `min(255, count*16 + report[9]) / 255`; duration `ceil(period*count/1000)` ms clamped to 1-5000 ms.                                         |

Known configuration chatter is ignored. An unknown command ID logs at most four bounded hex samples
so a future Steam protocol change remains discoverable without flooding the log.

`ManagedControllerRouter` admits a capacity-one latest frame, requires current target kind and
generation, rejects stale/invalid timestamps and nonfinite channels, applies the plugin's channel
support, and rate-limits to its declared `MaxFramesPerSecond`. It applies
`MinimumStartIntensity`/`MinimumPulse` only to bounded events. Continuous rumble passes through
unfloored so quiet scenes do not buzz. Every pulse schedules a route-generation-checked explicit
zero, and detachment waits for in-flight output.

## Measure physical motors

Use the attended `haptic-sweep` only with explicit operator direction and an already trusted plugin
that can identify and acquire the exact controller, publish a haptic sink, emit a known physical
output report, stop output, and restore the original topology. It calibrates known motors; it is not
a report-format discovery tool and never justifies brute-forcing output reports. Its
controller-driven phases measure:

1. Continuous descending strength, informational only.
2. Descending 30 ms ticks: weakest rendered bounded event becomes `MinimumStartIntensity`.
3. Full-strength shrinking pulses: shortest rendered pulse becomes `MinimumPulse`.

The stock fixed pulse and sweep drive low and high channels identically. They prove only the
symmetric path's perceptual floor and pulse length; they cannot prove independent motor routing,
trigger channels, amplitude resolution, or that the hardware is binary. Low-only, high-only, and
multiple-level trials require a reviewed Device Lab extension or a trusted plugin-specific attended
test.

Declare measured physics in `HapticCapabilities`. A voice-coil/LRA that renders every event keeps
zero defaults. Do not floor continuous rumble. Test explicit zero on target removal, game exit,
suspend, disconnect, integration/controller disable, output-router fault, and plugin stop.

The Claw's DirectInput physical report is:

```text
05 01 00 00 <weak/high-frequency> <strong/low-frequency> 00...
```

Both channels are real `0..255` amplitudes. The measured Claw ERM bounded-event floor is `56/255`
(about 0.22) and minimum pulse is 10 ms. Successful identical writes may coalesce; a failed write
must not enter that success cache, so a later explicit output can try again. Nonzero physical writes
are spaced by at least 4 ms in the current plugin.

## Generic motion discovery

1. Inventory Windows sensor, USB, HID, and driver associations. Try WinRT, but do not stop there: an
   Intel Sensor Hub can expose a custom legacy sensor WinRT will not project. WinRT absence is not
   proof that motion hardware is absent. Current Device Lab inventory calls WinRT `GetDefault` and
   records metadata; it does not sample motion.
2. With operator approval, inspect `tools/probe-legacy-sensors.ps1` before running it. This
   checked-in, reviewed enumerator is a specific exception to the exact compiled-profile rule: it
   enumerates legacy Sensor API objects and lower HID collections, reads only shared input, requests
   no sensor permission, configures nothing, and bounds its read timeout. It is still live hardware
   access; `-AllHid` is broad metadata enumeration, not an exact endpoint probe.
3. Record exact friendly/type identity, device path, HID usage, supported property keys, value type,
   units, report counter/timestamp, minimum/current interval, and behavior at rest and known axes.
4. Prove cadence and freshness independently. Polling the same cached Sensor API report faster does
   not create new samples; use a hardware counter/timestamp where available.
5. Establish the device-to-application basis from deliberate positive rotations/tilts. Apply that
   transform exactly once in the plugin. Target encoders perform their own application-to-wire
   transform exactly once.
6. Quantify stationary noise and zero-rate bias across multiple positions and runs before designing
   a calibrator. Bias subtraction is not deadbanding.
7. Keep raw high-rate capture temporary and bounded. Persist the finding in code/tests/docs, then
   remove the capture path. Production uses transition/freshness logs only.

The generic SDK permits finite gyro-only or accelerometer-only motion publication; omit the absent
half rather than synthesizing it. Requiring both physical sources to open is a measured Claw package
decision below, not a universal contract.

## Claw motion evidence, not a generic default

The reference A2VM uses the ST LSM6DSO behind Intel ISS `VID_8087&PID_0AC2`:

- Legacy exact names: `Physical Gyrometer` and `Physical Accelerometer`.
- Custom type GUID: `e83af229-8640-4d18-a213-e22675ebb2c3`.
- XYZ are `VT_R4` fields 7/8/9 in property set `b14c764f-07cf-41e8-9d82-ebe3d0776a6f`.
- Gyro units are degrees/second; acceleration units are g.
- Gyro field 34 is a `VT_UI4` hardware-report counter that advances even at rest. Publish only when
  it changes.
- Gyro minimum interval is 10 ms, accelerometer minimum is 2 ms; the plugin polls at 2 ms, requests
  each own minimum per cycle, and restores the old interval only if nobody else changed it.
- Both physical sources must match and open; do not synthesize a missing half.
- Sensor to application axes: `(X, Y, Z) -> (X, Z, -Y)`.

The stationary bias calibrator uses 200-report rest windows, per-axis gyro span at most 2 deg/s,
acceleration span at most 0.05 g, gravity magnitude 0.85-1.15 g, and candidate bias magnitude at
most 5 deg/s. It passes data unchanged until a valid rest window, subtracts the adopted bias without
a deadband, dampens nearby refinements, and replaces a distant estimate only after three agreeing
windows. A steady yaw cannot be distinguished by accelerometer gates, so this remains measured
heuristic behavior, not universal IMU theory.

## Target motion encoding

`src/WSGM/Input/SteamDeckNeptuneReport.cs` converts canonical application motion into Deck raw
slots:

- application -> Neptune axes `(X, -Z, Y)`;
- gyroscope scale 16 counts per degree/second;
- accelerometer scale 16384 counts per g;
- motion occupies bytes 24-35;
- quaternion bytes 36-43 remain zero. A frozen identity quaternion made Steam ignore the raw gyro;
  never synthesize orientation.

Triggers/pressure scale to signed full travel 32767, and signed axes clamp at -32767 to avoid the
decoder's `-32768` negation overflow. Xbox drops motion; DualShock 4 and Neptune have separate
tested encoders.

Key implementation/evidence paths:

- `tools/probe-legacy-sensors.ps1`
- `_plan/claw-8-a2vm-plugin.md`, Motion and Rumble sections
- Claw `LegacyPhysicalMotionSensors.cs`, `WindowsMotionSource.cs`, `ClawInput.cs`,
  `ClawResources.cs`
- WSGM `Input/ViiperControllerBackend.cs`, `ManagedControllerRouter.cs`,
  `SteamDeckNeptuneReport.cs`, `DualShock4Report.cs`
- `tests/WSGM.Tests/SteamDeckNeptuneReportTests.cs`, `ControllerDependencyAdapterTests.cs`,
  `ManagedControllerBackendTests.cs`

Hardware-free validation:

```powershell
dotnet test external/WSGM.Device.Msi.Claw8A2Vm/WSGM.Device.Msi.Claw8A2Vm.slnx --configuration Release
dotnet test tests/WSGM.Tests/WSGM.Tests.csproj --configuration Release --filter "FullyQualifiedName~SteamDeckNeptuneReportTests|FullyQualifiedName~ControllerDependencyAdapterTests|FullyQualifiedName~ManagedControllerBackendTests"
```
