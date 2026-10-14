# Device integration

WSGM selects one exact handheld through [LibHandheld](../external/libhandheld/README.md). The
library owns detection, native protocols, physical input, motion, haptics and hardware restoration.
WSGM owns profiles, AutoTDP, HidHide, virtual controllers, OEM actions and both UI surfaces. The
current mechanism is in [built-in handheld runtime](device-runtime.md).

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

## Glyphs, diagnostics and contributions

112 exact definitions select authored artwork through 106 glyph profiles. WSGM imports those
outlines and supplies the corresponding Overlay and Steam presentation. Family notices ship with the
application and setup.

Diagnostics expose native model/family identity, access, lifecycle, controller ownership and command
outcomes. Historical log labels containing `package` refer to a native family, not an installed
device plugin. Source coverage is distinct from physical validation on each model.

[Handheld contributions](handheld-contributions.md) describes implementing protocols, exact
definitions and fixtures. Device Lab is an attended evidence/contribution tool, not a production
driver or a device-package generator. Independent integrations such as IR use the
[common plugin host](plugin-system.md), which refuses the retired `wsgm.device` category.
