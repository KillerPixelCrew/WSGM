# Device integration

WSGM selects one exact handheld through [LibHandheld](../external/libhandheld/README.md). The
library owns detection, native protocols, physical input, motion, haptics and hardware restoration.
WSGM owns profiles, AutoTDP, HidHide, virtual controllers, OEM actions and both UI surfaces. The
current mechanism is in [built-in handheld runtime](device-runtime.md).

The Atlas
[OEM input and software control reference](../external/libhandheld/src/LibHandheld/Families/Acer/OEM_INPUT_AND_DISABLE.md)
records each physical source and report, the automatic rear-mode switch, and the tested separation
of the USB OEM keyboard from I2C volume input. It also lists the exact services, tasks and package
identities disabled on the maintainer's machine, activation-block checks, recovery and the opt-in
local maintenance guard in `eng/atlas-oem-disabled-state.ps1`. Service shutdown alone does not block
the Game Bar widget. WSGM's Full-mode manager takeover now covers the recorded services/tasks, the
exact Predator Sense package families and only the attributed controller keyboard leaf. Takeover
records original states before writing, rechecks at startup and every five minutes while accepted,
and restores WSGM-owned changes on uninstall. Setup and Settings report when Windows must restart.
The earlier local guard is separate from these install-lifecycle records; existing disabled settings
are preserved as user-owned state.

## Configuration and availability

Device Integration is an application switch. Off means no native device lifecycle, controller
target, device writes or AutoTDP. Windows controls, RTSS, built-in GPU drivers and independent
common plugins remain available through their own owners.

The library detects from a captured machine identity and returns one exact definition or null.
Detection does not acquire hardware. Unknown identities remain passive. Runtime start reports
missing native providers, permissions or driver dependencies without pretending that a selector is
working hardware support. Setup installs the detected model's declared prerequisites.

## Shared controls and profiles

Overlay and Steam consume the same native descriptors, requested values, observations and command
results through DeviceCoordinator. Descriptor bounds and options determine available controls.
Writable controls do not depend on readback; an accepted setting is published separately from
measured telemetry. Uncertain writes are not automatically retried.

Visibility is decided when a service first starts, never at runtime, so nothing appears and then
vanishes when touched. The processor service reads what it can and writes curve offsets and
undervolts to 0. A definite firmware refusal during that start, the AMD SMU's "unknown command"
(PawnIO Win32 87) or "failed" (31), leaves the setting out. A refusal of a later write is reported
as unsupported, keeps the control on screen and is recorded in `unsupported-settings.json` in the
device state folder, bound to the processor and BIOS, so the next start leaves it out. A wake
re-acquires the service but repeats neither the settle nor the decision. Busy (170), prerequisite
(1359) and timeout (121) answers are not definite and change nothing. A slider without a known value
stays visible as "Unknown".

[Profiles](profiles.md) resolves Global and per-application preferences. Power presets and AC/DC
assignments use the serialized power lane of the coordinator's `DevicePowerPolicy`. Native firmware
controls and WSGM's software fan/charge controllers have explicit ownership, so they do not fight
each other.

Each fan's mode says who drives it: Firmware (the firmware's own automatic mode), Software (WSGM's
fan curve) or Manual (the duty slider); other firmware modes such as full speed stay beside them.
Software is a WSGM choice that never reaches the firmware. The curve runs only on fans set to
Software, starting from the Default preset until a curve is saved, and those fans read Software even
though the firmware reports custom while the curve writes duty. The duty slider appears only in
Manual and the curve editor only while a fan uses Software, on the Overlay and in Steam alike. A
restore never replays a saved duty or curve for a fan in another mode. The curve follows the hotter
of the device's own temperature and RTSS's readings, averaged over the latest three seconds. It
recomputes its target only after a 2 °C change, except beyond its end points, and steps the duty by
at most 10 points up or 3 points down per second, so one spiky reading and sensor jitter do not move
the fans and a load spike spins up briskly and down slowly. The Quiet, Default and Aggressive
presets are HandheldCompanion's shapes (`FanCurvePresetTable.HandheldCompanion`) unless the matched
LibHandheld definition carries measured duties in `HandheldDefinition.FanCurvePresets`, together
with its own smoothing. `DeviceCoordinator.FanPresets` resolves that table once when the definition
is detected, through `HandheldUiProjection.FanPresets`, and the software curve, Overlay, Steam
settings and Quick Access all read that one value. Today only the Atlas 8 carries its own: its
package temperature swings ten or twenty degrees within a second or two at idle, so there the curve
reads a ten-second average, waits for 3 °C and ramps 5 points up or 2 down per second.

## Controllers and OEM buttons

LibHandheld produces canonical physical input. ControllerManager owns HidHide, VIIPER targets,
forwarding and UI capture. Steam Input leases keep capture and wrapped-game ownership separate from
native device acquisition. Explicit OEM action assignments override defaults; Guide and Quick Access
actions remain available through wrapper leases.

The Xbox Ally Armoury Crate button declares the companion-application default and opens WSGM without
a recorded keyboard shortcut. OEM release edges still reach the host during suspend, stop and fault
cleanup, after admission of new presses closes.

The virtual target's output and calibration previews share one physical haptic sink. Cleanup
neutralizes forwarding and ends output before physical controller release and native stop.

## The USB/IP driver is replaced on a boot of its own

Setup installs or upgrades USB/IP in a separate boot before WSGM attaches a virtual controller. The
initial run disables the logon service, schedules `/finishdrivers` through RunOnce and requests a
restart. The resumed run installs the driver and restores autostart even if driver installation
fails. A quiet initial run returns code 6. Runtime controls never install this kernel driver.

WSGM adds the installed USB/IP directory to its own process PATH when `usbip.exe` is not already
reachable. This covers installers that no longer add a machine-wide PATH entry. See
[setup](setup.md) for the installation and recovery sequence.

## Claw OEM chord suppression also runs on Desktop

LibHandheld's Claw OEM service keeps its chord hook active in Desktop and Game Mode. It consumes the
known firmware G/Tab sequences, tracks repeats and releases, and sends the synthetic Windows-key
release using the full x64 INPUT layout and extended-key flag. Modified shortcuts, injected input
and unknown sequences pass through. WSGM routes the resulting OEM actions through its existing
policy; the hook is part of the native device lifetime, not a plugin.

## WSGM owns the cloak

Controller management enables HidHide's cloak and adds only WSGM's application and device entries.
Normal shutdown, handoff, update, uninstall and disabling controller management remove those entries
and release the cloak. Sleep and fault recovery temporarily retain the physical hide while ownership
is reacquired, with a bounded recovery window. Entries belonging to other owners are never removed.
Controller forwarding and virtual-target release finish before physical ownership is released.

A definition whose pad Steam drives natively (`HandheldDefinition.NativeSteamController`, the Steam
Deck LCD and OLED) is not taken while the resolved target is that pad's own kind.
`DeviceCoordinator.OwnsPhysicalController` decides it from the management switch, the definition and
the target resolved for the running application. Leaving the native target runs the ordinary
handoff: the selection leaves `Native`, the handheld takes the pad, and its publication hides it and
brings the virtual target up. Returning releases the pad first, then reports `Native`. In `Native`
the manager creates no target and hides nothing, and both surfaces show the physical pad as the live
target with the emulated targets still on offer.

A family publishes every node of its pad that Windows knows, present or not, across all product IDs
the pad enumerates under (`HidDevices.KnownControllerNodes`). A pad that changes mode re-enumerates
under another product ID: the Claw's firmware returns to XInput in every sleep, and the Legion Go
switches on request. Hiding only the present nodes left the other mode visible to Steam from the
moment the pad reappeared until it was taken again.

## Glyphs, diagnostics and contributions

The failed F901 auxiliary hide set was withdrawn after a keyboard regression. Controller ownership
now selects native F90A DInput mode, reads its gamepad collection and drives the verified report05
rumble output. The original mode is journaled and restored. Alongside gamepad/XInput nodes, the
separately tested F90A companions `MI_00/COL02` (FFF0:0040 command) and `MI_01/COL03` (000C:0001
consumer) are selected by exact report shape and physical-port correlation. The maintainer confirmed
working keyboard input and one Steam Deck controller in Steam with this DInput-specific set.
Keyboard `MI_01/COL01` and mouse `MI_01/COL02` stay excluded. Steam-specific filtering is not used.
Physical DInput button mapping and firmware-keyboard hold behavior still require attended
acceptance.

Selecting an Atlas firmware mode now also publishes the selected profile's EC limit values.
Previously a stock-mode command cleared sustained/boost readouts indefinitely. A missing profile
getter still permits the mode command; it leaves the numeric observation unknown.

The Acer Predator Atlas 8 PA08-I51 uses LibHandheld's EC power profiles, WMI fan and battery
controls, Windows motion and controller RAM lighting. The same descriptors supply Overlay and Steam
controls for performance mode, manual power, both fans, charge protection and lighting. Controller
acquisition selects the native rear-button reporting mode and restores the original mode on release;
users do not need to configure rear assignments in Predator Sense first. Fan originals without
readable behavior return to firmware Auto with unverified restoration. Atlas fan duty is an absolute
share of full speed, about 70 RPM per percent, from 10% upwards because slower commands stall the
fans. The firmware honours a speed command absolutely only for a few seconds after a behavior write,
so LibHandheld re-arms manual mode every second while WSGM drives a fan; a failed re-arm is resolved
by readback and dropped, never repeated blindly. Lighting changes retain the other profile fields
and never write ROM. Shared ring color and eight measured segment colors use the same backend; the
unassigned ninth wire slot is retained. Optional Intel voltage settings use the signed PawnIO
IntelMSR module. Exact on-device results and remaining acceptance are in the
[Acer provenance](../external/libhandheld/src/LibHandheld/Families/Acer/PROVENANCE.md).

113 exact definitions select authored artwork through 107 glyph profiles. WSGM imports those
outlines and supplies the corresponding Overlay and Steam presentation. Family notices ship with the
application and setup.

Diagnostics expose native model/family identity, access, lifecycle, controller ownership and command
outcomes. Historical log labels containing `package` refer to a native family, not an installed
device plugin. Source coverage is distinct from physical validation on each model.

[Handheld contributions](handheld-contributions.md) describes implementing protocols, exact
definitions and fixtures. Attended hardware evidence is recorded in the family provenance.
Independent integrations such as IR use the [common plugin host](plugin-system.md), which refuses
the retired `wsgm.device` category.

Atlas 8 power range and load findings are recorded in the LibHandheld
[power-limit investigation](../external/libhandheld/src/LibHandheld/Families/Acer/POWER_LIMIT_TESTS.md).
The supported runtime offers 8..40 W sustained and up to 65 W boost, in 1 W steps, with boost at
least sustained. The maxima are firmware-advertised; the 8 W floor is below the advertised 10 W and
was confirmed by the maintainer on the device. Wider stored values are experimental; the OCCT
observation above 45 W does not establish their enforcement or a sustained maximum.

Atlas 8
[motion and controller-mode evidence](../external/libhandheld/src/LibHandheld/Families/Acer/MOTION_TESTS.md)
confirms Windows gyro delivery at 100 Hz during movement. DInput removes XInput and exposes 125 Hz
gamepad reports, but the tested motion fields remain fixed; firmware tilt-to-stick mapping does not
establish a raw controller gyro. The runtime continues using the Windows source.
