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
the Game Bar widget. This local administration policy is not yet a WSGM Settings/runtime feature.

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
assignments use the coordinator's serialized power lane. Native firmware controls and WSGM's
software fan/charge controllers have explicit ownership, so they do not fight each other.

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

The Acer Predator Atlas 8 PA08-I51 uses LibHandheld's EC power profiles, WMI fan and battery
controls, Windows motion and controller RAM lighting. The same descriptors supply Overlay and Steam
controls for performance mode, manual power, both fans, charge protection and lighting. Controller
acquisition selects the native rear-button reporting mode and restores the original mode on release;
users do not need to configure rear assignments in Predator Sense first. Fan originals without
readable behavior return to firmware Auto with unverified restoration. Lighting changes retain the
other profile fields and never write ROM. Shared ring color and eight measured segment colors use
the same backend; the unassigned ninth wire slot is retained. The optional KX processor helper can
be blocked by Windows security without disabling the native EC path. Exact on-device results and
remaining acceptance are in the
[Acer provenance](../external/libhandheld/src/LibHandheld/Families/Acer/PROVENANCE.md).

113 exact definitions select authored artwork through 107 glyph profiles. WSGM imports those
outlines and supplies the corresponding Overlay and Steam presentation. Family notices ship with the
application and setup.

Diagnostics expose native model/family identity, access, lifecycle, controller ownership and command
outcomes. Historical log labels containing `package` refer to a native family, not an installed
device plugin. Source coverage is distinct from physical validation on each model.

[Handheld contributions](handheld-contributions.md) describes implementing protocols, exact
definitions and fixtures. Device Lab is an attended evidence/contribution tool, not a production
driver or a device-package generator. Independent integrations such as IR use the
[common plugin host](plugin-system.md), which refuses the retired `wsgm.device` category.

Atlas 8 power range and load findings are recorded in the LibHandheld
[power-limit investigation](../external/libhandheld/src/LibHandheld/Families/Acer/POWER_LIMIT_TESTS.md).
The supported runtime keeps firmware-advertised 10..40 W sustained and up to 65 W boost, in 1 W
steps, with boost at least sustained. Wider stored values are experimental; the OCCT observation
above 45 W does not establish their enforcement or a sustained maximum.
