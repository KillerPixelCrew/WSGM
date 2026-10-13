# WSGM changelog

## 2.3.0 (2026-10-13)

The main change in **WSGM 2.3.0** is the replacement of the Device SDK and separate hardware plugin
packages with **[LibHandheld](https://github.com/KillerPixelCrew/LibHandheld)** and
**[LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract)**. Handheld and
GPU integration are now built into WSGM.

**The Device SDK is deprecated and has been removed from WSGM.** Device plugin packages are no
longer supported. New handheld support belongs in LibHandheld; native graphics driver support
belongs in LibGPUDriverInteract. The common Plugin SDK remains available for independent
integrations.

**The only tested handhelds remain the MSI Claw 8 A2VM and ROG Xbox Ally X.** The larger device
catalog describes implemented definitions and source coverage. It does not mean those devices have
been tested on physical hardware.

**The Emulator Manager and ROM Importer are still WIP.** The Game Library and packaged-game overlay
routes also remain experimental.

This changelog covers changes since the last published release, **2.1.0**, including the unreleased
2.2.0 work.

### From Device SDK to native libraries

- **[LibHandheld](https://github.com/KillerPixelCrew/LibHandheld)** owns exact device detection,
  model protocols, physical input, motion, haptics and native hardware restoration. WSGM keeps
  profiles, AutoTDP, HidHide, virtual controllers, OEM action policy and presentation.
- **[LibGPUDriverInteract](https://github.com/KillerPixelCrew/LibGPUDriverInteract)** owns the
  Intel, AMD and NVIDIA driver backends. WSGM keeps graphics profiles, integration switches and
  controls in Overlay and Steam. Separate first-party GPU plugin packages are retired.
- Device support ships with WSGM and updates through the application installer. There is no device
  package installation or device plugin lifecycle.
- The native catalog contains **112 exact device definitions** and **106 glyph profiles** across
  AYANEO/SuiPlay, AYN, GPD, Legion Go, MSI Claw, ONEXPLAYER/AOKZOE, ROG Ally, Valve and Zotac.
  Hardware testing remains limited to the two handhelds named above.
- Device Lab now produces LibHandheld contribution source and fixtures instead of device packages.

### Upgrade notes

- Run the **offline installer** to update in place. Settings and installation choices are preserved.
  Steam closes during the update, and retired first-party hardware packages are cleaned up during
  migration.
- Third-party common plugins need compatible rebuilds for **Plugin API 5 / WSGM.Plugin.Sdk 0.4.0**.
  Shared types moved into the common SDK assembly; this does not restore the deprecated Device SDK
  extension point.
- Device Integration off stops the native handheld lifecycle, controller management, device writes
  and AutoTDP. Built-in GPU integration, Windows controls, RTSS and independent common plugins
  retain their own owners.

### Handheld controls and input

- Added independent AMD fast PPT, supported processor clocks, AMD curve and thermal controls, Intel
  undervolting and clock restoration through the existing controls and profiles.
- Corrected motion transforms, rear-button ordering, Legion touch timing and stale samples across
  controller sessions.
- Updated supported lighting, fan, charge and UMA protocols, including Claw Battery Master, GPD WIN4
  fan control and WIN5 Type-C power reporting, Steam Deck haptic output, and XG Mobile fan
  restoration.
- Writable controls stay available when firmware cannot read a setting back. Accepted values and
  measured telemetry remain separate; uncertain writes are not automatically retried.
- Claw Steam and Quick Access buttons work through wrapped-game input leases. Explicit button
  assignments take precedence.

### Steam UI and launching

- Fixed **Change Artwork** disappearing from the native game menu during reactive updates.
- Corrected Quick Access slider and dropdown overflow while preserving native track, handle and
  notch sizing. Restored the overlay-level row in the Performance panel.
- Fixed CEF promise collection failures and result ownership without replaying an evaluation that
  may already have run.
- Prepared CEF activation before startup applications and improved bounded recovery of Steam's debug
  listener.
- Kept game creation inside the Steam launch wrapper after de-elevation, preserving Steam overlay
  ownership.
- Improved Emulator Manager navigation and retained editor state across updates. Emulator management
  and ROM importing remain WIP.

### Graphics, performance and storage

- Preserved enabled-by-default GPU integration for legacy configuration and corrected Claw Intel GPU
  discovery.
- Added driver-declared GPU display preferences to Desktop and Game Mode layouts, including
  connected but Windows-disabled displays. Improved display recovery and audio choice preservation.
- Improved RTSS overlay publication, game detection, startup and shared-memory ownership. Frame
  limits and the performance overlay remain independent of Device Integration.
- Moved managed emulators to portable storage with shared BIOS/firmware links and retained saves and
  configuration. Conversion checks protect against disconnected sources, collisions and unsafe
  aliases.

### Installer

- Only the **offline installer** is published. It includes the selected prerequisites, including
  WebView2, USB/IP, HidHide and the pinned RTSS installer archive.
- Added file-owner scanning before replacement, rollback and cleanup. Setup terminates verified file
  holders, protects critical processes and reports the blocked file and owner when it cannot
  proceed.
- Improved rollback, prerequisite handling and legacy package cleanup.

Automated checks and Steam UI inspection do not expand the tested handheld list. Device capabilities
still depend on the exact model, firmware and available native providers. Anti-cheat compatibility
is not verified.

Feel free to report any issue found.

[Support development on Ko-fi](https://ko-fi.com/nightstorm1000)
