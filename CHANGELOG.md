# WSGM changelog

## 2.3.0 (2026-10-13)

Changes since the last published release, **2.1.0**, including the unreleased 2.2.0 work. This
release expands native handheld integration, fixes Steam UI lifetimes and slider layout, and
improves game launching, performance monitoring and installation.

### Before you upgrade

- **The Emulator Manager and ROM Importer are still works in progress.**
- Run the offline setup to update in place. It preserves settings and installation choices. Steam
  closes during the update.
- Handheld and graphics integration use the built-in libraries. Retired first-party device and
  graphics plugin packages are cleaned up during migration.
- Third-party common plugins need compatible rebuilds for **Plugin API 5**, using **WSGM.Plugin.Sdk
  0.4.0**. The separate Device SDK and device package extension point are retired.
- Device controls depend on the exact model, firmware and available native providers. Device
  Integration off stops device control and AutoTDP while independent features keep working.

### Handheld support and input

- Moved device detection, protocols and physical input into LibHandheld, replacing separate device
  packages. Profiles, AutoTDP, HidHide, virtual controllers and button policy remain in WSGM.
- Reconciled native support for **112 exact device definitions**, with device-specific artwork
  supplied through **106 glyph profiles**. Families include AYANEO/SuiPlay, AYN, GPD, Legion Go, MSI
  Claw, ONEXPLAYER/AOKZOE, ROG Ally, Valve and Zotac.
- Added independent AMD fast PPT alongside sustained and slow limits, including presets and custom
  AC/DC profiles.
- Exposed supported processor clocks, AMD curve and thermal settings, Intel undervolting and
  automatic clock restoration through the existing profiles and controls.
- Corrected gyro/accelerometer axis transforms, Legion and AYANEO rear-button ordering, and Legion
  touch timing and long-press behavior. Stale motion from previous controller sessions is discarded.
- Updated AYANEO KUN lighting and FLIP secondary-display packets, ONEX rear lighting, GPD WIN4
  automatic fan control and Zotac UMA control. XG Mobile restores automatic fan control when
  released.
- Improved Steam Deck haptic packets and output ownership, with independent charge, GPU and fabric
  measurements.
- Added Claw Battery Master control and corrected GPD WIN5 Type-C power-source reporting.
- Writable controls remain available when firmware cannot read back a setting. Accepted values are
  published separately from measured telemetry; uncertain writes are not automatically retried.
- Claw Steam and Quick Access buttons work through wrapped-game input leases. Explicit button
  assignments take precedence.

### Steam UI and game launching

- **Change Artwork stays in the native game menu across reactive updates.** Extensions use the
  menu's native action slot instead of depending on its first render.
- **Corrected slider and dropdown overflow in Quick Access.** Shared cards use the space supplied by
  Steam while preserving Valve's track, handle and notch sizing. The overlay-level row is present in
  the Performance panel.
- Fixed CEF `Promise was collected` failures when the script had already run. Synchronous results
  return directly; actual promises retain their results until completion or timeout. An unanswered
  evaluation does not replay its producer.
- Prepared CEF activation before startup applications and added bounded recovery when Steam's debug
  port remains closed. Foreign listeners are refused.
- Kept game creation inside the Steam launch wrapper after de-elevation, preserving the process
  boundary used by Steam's overlay.
- Corrected native Emulator Manager navigation and retained selection and editor state across
  updates.

### Graphics, display and audio

- Moved Intel, AMD and NVIDIA driver backends into LibGPUDriverInteract and made their integration
  built in. Graphics controls no longer require separate first-party GPU plugin packages.
- Preserved enabled-by-default graphics behavior when legacy configuration has no entry, while
  respecting an explicit disabled choice. Corrected Claw Intel GPU discovery.
- Added driver-declared GPU display preferences to Desktop and Game Mode display layouts, keyed to
  physical monitor identity. Connected but Windows-disabled displays remain configurable.
- Corrected display activation and recovery ordering, including late capture of original GPU
  settings before changes.
- Preserved audio endpoint, format and spatial-sound choices through discovery and live updates.
- Reduced redundant graphics publications and rejected stale profile or capability updates.

### Performance overlay and emulators

- Restored timely RTSS overlay publication and game detection. Steam lifetime notifications and
  foreground observation feed one application identity and profile.
- Improved RTSS startup, shared-memory framing and overlay ownership. Frame-limit and overlay
  controls remain independent of Device Integration.
- Managed emulators now use portable storage, with shared BIOS/firmware links and retained saves and
  configuration across updates.
- Protected portable emulator conversions from missing or disconnected data sources, collisions and
  unsafe aliases. Existing saves and configuration are preserved before redirects change.

### Installer

- The published installer is **offline**. It bundles the selected runtime prerequisites, including
  WebView2, USB/IP, HidHide and the pinned RTSS installer archive.
- Added an actual file-owner scan before replacement, rollback and cleanup. Setup identifies and
  terminates verified file holders, protects critical processes and reports the file and owner when
  it cannot proceed.
- Improved rollback, prerequisite acquisition and legacy package cleanup. Bundled glyph notices
  cover all nine device families.

### Developer integrations

- The common Plugin SDK contains shared capability, input and glyph contracts in one assembly.
  Independent integrations such as IR and Steam frontend plugins retain their extension points.
- Device Lab produces LibHandheld contribution source and fixtures instead of device packages.

### Validation and known limits

- Release compilation, native payload checks, relevant device/host regressions and affected UI
  rendering checks passed. The CEF completion path passed **31 focused tests** and live string,
  object and fulfilled-promise evaluations.
- Stable and Beta source contracts were checked separately. Current Beta was inspected live; Stable
  lacks the optional native Screensaver settings section.
- The Game Library and packaged-game overlay routes remain experimental. This release does not claim
  physical validation of every device definition or verified anti-cheat compatibility.

Feel free to report any issue found.

[Support development on Ko-fi](https://ko-fi.com/nightstorm1000)
