# Built-in handheld runtime

WSGM runs one exact native handheld through [LibHandheld](../external/libhandheld/README.md). There
is no device plugin, package manifest, Device SDK assembly or collectible device load context.
Independent extensions use the [common plugin host](plugin-system.md).

## Ownership

| Owner                                       | Responsibility                                                                                               |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| LibHandheld                                 | Exact detection, family protocols, native services, physical input, motion, haptics and hardware restoration |
| `DeviceCoordinator`                         | One machine-wide owner, application policy, profiles, lifecycle admission and cleanup                        |
| `DevicePowerPolicy`                         | The power lane, manual TDP, presets, AC/DC assignments, per-application limits and AutoTDP                   |
| `DeviceCapabilityCommands`                  | The one command route: power-lane serialization, user-value persistence and desired-value restore            |
| `DeviceFanCurves`, `DeviceLightingProfiles` | Software fan curve and authored fan profiles; lighting profiles and lighting restore                         |
| `HandheldDeviceRuntime`                     | Borrowed observer and adaptation between native contracts and WSGM publications                              |
| `DeviceCapabilityRouter`                    | Validated descriptors, detached state, command admission and UI projections                                  |
| `ControllerManager`, `ControllerForwarding` | HidHide, VIIPER targets and controller handoff; sample forwarding, UI capture and synthetic presses          |
| `DeviceOemActionRouter`                     | Configured OEM actions and default Steam/Quick Access behavior                                               |

The machine-wide `Global\WSGM.DeviceOwner` marker prevents another WSGM session or setup from
starting a competing production hardware cycle. Device Integration off starts none of these device
services or AutoTDP; Windows controls, RTSS, built-in graphics and common plugins retain their own
owners.

## Detection and construction

`HandheldDevice.Definitions` contains 113 exact definitions. `Detect(identity)` returns one matching
definition or null. Identity matching does not acquire hardware. Unknown machines remain passive;
WSGM does not select a generic CPU/vendor fallback or rank installed packages.

Acer Predator Atlas 8 PA08-I51 initially supplies physical XInput/rumble and raw Task, Predator, M1
and M2 input. It requires the exact Acer system and PTL board identity and remains hardware
unverified. Its
[family provenance](../external/libhandheld/src/LibHandheld/Families/Acer/PROVENANCE.md) records the
published captures and the controls still awaiting implementation and hardware evidence.

`HandheldDevice.Create` constructs native services using the selected definition, the captured
identity and the caller's state directory. Native access is acquired during start. Missing drivers,
permissions or providers produce a native lifecycle outcome and diagnostic rather than fabricated
support. Setup installs the selected definition's declared prerequisites.

## Lifetime

`DeviceCoordinator` owns `HandheldDeviceRuntime` across Steam restarts, games and Desktop/Game Mode
transitions. The runtime observes `HandheldDevice` directly. The library serializes native commands
and lifecycle operations and admits them through its active state.

- Start acquires services and reports Passive, Active or Degraded.
- Suspend quiesces native services; resume reacquires the same selected model.
- Changing Controller Management hands physical input ownership over without creating a second
  device runtime.
- Stop restores native originals and reports Clean, Unverified or Failed.
- Dispose joins remaining operations and releases resources. It does not replace Stop's hardware
  restoration.

WSGM closes command admission and neutralizes/releases controller forwarding before native stop.
Cancellation requests cooperation; a timeout does not prove a write stopped or resources were
released. Fault restart policy remains with the coordinator and does not retry an uncertain command.
State and recovery live under the selected native family's state directory. Legacy device package
profiles and journals are not interpreted as a native hardware identity.

## Controls and profiles

`HandheldUiProjection` adapts library descriptors and observations for WSGM's existing capability
router. `HandheldDeviceRuntime` sends semantic operations to the native library; application policy
and vendor byte protocols do not cross that boundary.

Descriptor bounds, choices, instances, profile scope and apply timing determine the controls offered
by Overlay and Steam. They share one backend and current state. WSGM rejects stale host generations
before dispatch. Native writes return typed outcomes; accepted requested values remain separate from
temperature, fan and other measured telemetry. Missing readback never disables a declared writable
control, and uncertain writes are not automatically replayed.

Unreadable numeric controls display **Unknown** until a value is supplied by an observation, saved
preference or explicit edit. Opening or refreshing a slider does not write its minimum. **Reset
processor tuning**, available on both surfaces, clears that device's processor overrides from Global
and every game, including disabled games, before requesting native restoration. The row's caption is
the action's `CapabilityDisplay.Description` as the library publishes it; every action row shows its
published description while no failure or unavailable reason needs saying. Lighting, fan and power
preferences are preserved. Selecting **Automatic GPU clock** also clears saved fixed clocks. The
library restores documented automatic clock modes, neutral curve/undervolt offsets and captured
thermal originals. Where firmware provides no documented live restore and no original was readable,
the reset reports the affected controls and asks for a Windows restart; it never invents thermal
defaults or reports a minimum clock as automatic. Live device and game performance still require
testing on the affected handheld.

[Profiles](profiles.md) resolve Global and per-application values. AC/DC assignments and supported
power presets use the power lane of `DevicePowerPolicy` alongside manual power and AutoTDP writes.
Software fan and charge policy remain WSGM services; supported firmware curves and protection modes
are native library controls. Competing software owners yield when a native mode owns the operation.

## Physical input, OEM controls and haptics

LibHandheld supplies canonical physical reports and motion samples for the selected model. WSGM owns
virtual target selection, UI capture, Steam Input leases and forwarding. High-rate samples do not
allocate or log per report. Stale motion from an earlier acquisition is rejected before use.

OEM button identity belongs to the library; action assignment belongs to WSGM. Explicit assignments
take precedence. Steam Guide and Quick Access defaults remain available through wrapped-game input
leases without creating another hardware owner.

Virtual output, calibration previews and timed pulses share the physical haptic sink. Library
capabilities limit supported actuators and pulse forms. Cleanup ends previews and releases output
before physical ownership ends. [Overlay and input](overlay-and-input.md) and
[Steam Input](steam-input.md) describe the corresponding capture and lease contracts.

## Glyphs and UI

The exact definitions select authored device artwork through 107 glyph profiles. LibHandheld owns
device facts and packaged outlines; WSGM validates/imports them and adapts them for Avalonia and
Steam. Family attribution ships with setup. A glyph failure does not create a replacement hardware
provider or change control ownership.

Settings configures WSGM. Native device controls live in Overlay and Steam's existing surfaces,
using the same descriptors, semantic operations and desired state. Diagnostics report the selected
model/family, provider access, lifecycle, controller ownership and command outcomes. Some legacy log
labels still say `package`; they identify the native family, not a loaded plugin archive.

## Source and contributions

- [LibHandheld API](../external/libhandheld/API.md): native contracts and supported operations.
- [Handheld contributions](handheld-contributions.md): adding exact definitions, protocols and
  fixtures.
- [Device integration](device-integration.md): product settings and integration boundaries.
- `Shell/Device/DeviceCoordinator`, `HandheldDeviceRuntime` and `HandheldUiProjection`: application
  ownership and adaptation.

Source coverage and regression fixtures do not establish physical validation of every model.
