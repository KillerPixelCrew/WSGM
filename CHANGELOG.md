# WSGM changelog

## 2.3.1 (2026-10-13)

This fixes OEM button handling broken by the move from device plugins to LibHandheld in 2.3.0.

## Before you upgrade

- **Just run the offline setup.** It updates in place and keeps your settings and installation
  choices. Steam closes during the update, that is normal.
- **Only the offline installer is published.** It includes the prerequisites, as in 2.3.0.

## Fixed

- **The Armoury Crate button no longer opening WSGM on the ROG Xbox Ally and Xbox Ally X.** The
  button events still arrived, but the migration left out the default action WSGM now reads. The
  button opens the overlay again without recording a keyboard shortcut. Explicit button assignments
  still take precedence.
- **OEM button releases being dropped during suspend, shutdown and fault cleanup.** Release events
  now reach the application after new presses are blocked, so held controls can be cleared.

## Testing

- 90 focused regression checks passed, including the actual Xbox Ally button metadata reaching
  WSGM's overlay action and OEM releases during cleanup.
- The full release build and native payload checks passed.
- The Xbox Ally hardware retest is still pending.

## Known issues

- **The Emulator Manager and ROM Importer are still WIP.**
- **The Game Library and packaged-game overlay routes remain experimental.** Anti-cheat
  compatibility of the overlay route is unverified.

Feel free to report any issue found.

## Support

If WSGM is useful to you, you can support its development on Ko-fi.

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/nightstorm1000)

[Full changelog since 2.3.0](https://github.com/KillerPixelCrew/WSGM/compare/2.3.0...2.3.1)

## Checksum

```
SHA-256 904495257497a3a6a667523cd5f291b3d5f90b02b01abed2edda7aecec523798  WSGM-Setup-2.3.1.exe
```

## 2.3.0 (2026-10-13)

Hardware support has moved out of device plugins and into
**[LibHandheld](https://github.com/KillerPixelCrew/LibHandheld)** and
**[LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract)**. The Device SDK
is deprecated and removed; handheld and graphics integration now ship as part of WSGM. This release
also fixes Change Artwork disappearing, broken Quick Access sliders, Steam overlay launching, RTSS
updates and setup failing because files are still in use.

These are the changes since **2.1.0**, the last published release, including the unreleased 2.2.0
work.

## Before you upgrade

- **Just run the offline setup.** It updates in place and keeps your settings and installation
  choices. Steam closes during the update, that is normal.
- **The Device SDK and device plugins are retired.** Setup removes the old first-party device and
  GPU packages. Their replacements are built into WSGM; there is no separate hardware package to
  install.
- **Third-party common plugins need compatible builds.** The common Plugin SDK is now **API 5,
  version 0.4.0**. Shared types moved into that assembly, so older plugin builds need rebuilding.
- Settings that depend on a particular model, firmware or native provider remain available only
  where that support exists.

## What's new

### LibHandheld replaces the Device SDK

**[LibHandheld](https://github.com/KillerPixelCrew/LibHandheld)** now handles exact device
detection, the device's own protocols, physical controller input, motion, haptics and hardware
restoration. WSGM keeps profiles, AutoTDP, HidHide, virtual controllers and what the OEM buttons do.
The controls still appear in the overlay and Steam through the same backend.

The library has **112 exact device definitions** and **106 glyph profiles**, covering
AYANEO/SuiPlay, AYN, GPD, Legion Go, MSI Claw, ONEXPLAYER/AOKZOE, ROG Ally, Valve and Zotac. **Only
the MSI Claw 8 A2VM and ROG Xbox Ally X have been tested on real hardware.** The other definitions
are implemented from reference source and await owners trying them.

New handheld support belongs in LibHandheld. Device Lab produces library contribution source and
fixtures instead of device plugins. Hardware support updates with WSGM.

### Built-in Intel, NVIDIA and AMD graphics

The graphics backends now come from
**[LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract)**. Separate
first-party GPU plugins are gone. WSGM keeps graphics profiles, integration switches and the
controls in the overlay and Steam.

Graphics integration still works with Device Integration off. Turning Device Integration off stops
handheld control, controller management and AutoTDP; Windows controls, RTSS and independent common
plugins keep working too. The common Plugin SDK remains available for integrations such as IR and
Steam frontend plugins.

### Handheld controls

- Independent AMD fast PPT alongside the sustained and slow limits, including presets and custom
  AC/DC profiles.
- Supported processor clocks, AMD curve and thermal controls, Intel undervolting and automatic clock
  restoration.
- Claw Battery Master and corrected GPD WIN5 Type-C power-source reporting.
- Updated supported lighting, fan and UMA protocols, including AYANEO KUN and FLIP, ONEX rear
  lighting, GPD WIN4 fan control and Zotac UMA. XG Mobile restores automatic fan control when
  released.
- Steam Deck haptic output and independent charge, GPU and fabric measurements.

### Display layouts and portable emulators

- GPU display preferences are part of Desktop and Game Mode layouts, tied to the physical monitor.
  Connected displays that Windows has disabled can still be configured.
- Managed emulators use portable storage, with shared BIOS/firmware links and retained saves and
  configuration across updates.
- Portable conversions check for missing or disconnected sources, collisions and unsafe aliases
  before changing redirects.

### Offline installer

Only the offline setup is published for this release. It includes the selected prerequisites,
including WebView2, USB/IP, HidHide and the pinned RTSS installer archive.

## Fixed

- **Change Artwork disappearing from the game menu.** Steam rebuilt the menu after WSGM added its
  entry and dropped it a few seconds later. The entry now uses the menu's native action slot and
  stays through those updates.
- **Broken sliders and dropdowns in Quick Access.** Shared cards fit the space Steam gives them
  while keeping the native slider track, handle and notches at their intended sizes. The
  overlay-level row is back in the Performance panel.
- **CEF commands failing with `Promise was collected`.** Results stay owned until completion or
  timeout. A command that may already have run is not sent again automatically.
- **Steam's debug connection not being ready at startup.** CEF activation happens before startup
  applications, with bounded recovery when Steam's debug listener remains closed.
- **Steam overlay launching after de-elevation.** The game is created inside the Steam launch
  wrapper, preserving the process boundary Steam uses for its overlay.
- **Claw Steam and Quick Access buttons during wrapped games.** Their default actions work through
  the wrapper's input lease; explicit button assignments still take precedence.
- **Profiles waiting for readback a device cannot provide.** Writable controls stay available,
  accepted settings are separate from measured telemetry, and uncertain writes are not retried
  automatically.
- **Motion and rear-button handling.** Corrected axis transforms, Legion and AYANEO rear-button
  ordering, Legion touch timing and samples left over from an earlier controller session.
- **RTSS overlay updates and game detection.** Steam notifications and foreground observation use
  one application identity and profile. Startup, shared-memory handling and overlay ownership are
  improved.
- **Claw Intel graphics discovery and legacy GPU settings.** Matching graphics integration stays
  enabled when an older configuration has no entry, while an explicit disabled choice is respected.
- **Display and audio recovery.** Corrected display activation order, capture of original GPU
  settings, and preservation of audio endpoint, format and spatial-sound choices during updates.
- **Emulator Manager navigation.** Selection and editor state survive updates.
- **Setup failing with files in use.** After normal shutdown, setup scans the files being replaced
  and terminates verified holders. Critical processes are protected, and a remaining failure names
  the blocked file and owner when available. Rollback and legacy package cleanup are improved too.

## Testing

- **The only tested handhelds remain the MSI Claw 8 A2VM and ROG Xbox Ally X.** The larger catalog
  is not a claim that every model has been tested on hardware.
- Steam Stable and Beta source contracts were checked separately, and the current Beta was inspected
  live.
- Release builds, native payload checks and relevant regression checks passed. These checks do not
  expand the hardware-tested device list.

## Known issues

- **The Emulator Manager and ROM Importer are still WIP.**
- **The Game Library and packaged-game overlay routes remain experimental.** Anti-cheat
  compatibility of the overlay route is unverified.

Feel free to report any issue found.

## Support

If WSGM is useful to you, you can support its development on Ko-fi.

[![Support me on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/nightstorm1000)

[Full changelog since 2.1.0](https://github.com/KillerPixelCrew/WSGM/compare/2.1.0...2.3.0)

## Checksum

```
SHA-256 cd4f5615bb3af8a93e32c3e9f87eb57458d66a9a895ff6424c05533d26d62a7d  WSGM-Setup-2.3.0.exe
```
