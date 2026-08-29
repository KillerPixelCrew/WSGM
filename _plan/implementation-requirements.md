# WSGM 2.0 lean requirements

Status: authoritative feature and behavior requirements, rewritten 2026-08-28

## Purpose

This file states what the completed product must do. It is not a method-by-method implementation
checklist, progress counter, compliance dossier, or generated traceability source.

Progress and simplification work live in [`implementation-todo.md`](./implementation-todo.md).
Architecture lives in [`2.0-design.md`](./2.0-design.md), decisions in
[`2.0-decisions.md`](./2.0-decisions.md), and exact Claw hardware facts in
[`claw-8-a2vm-plugin.md`](./claw-8-a2vm-plugin.md).

## Product invariants

These are observable properties of the shipped product.

- **INV-001 — Core independence.** Device Integration off leaves Game Mode, Big Picture,
  shell/session transitions, library/storage/artwork/launch tools, overlay, and Steam Input lease
  behavior usable.
- **INV-002 — Optional ownership.** Device Integration is optional; controller management is its
  optional child and cannot remain on while the parent is off.
- **INV-003 — One lifecycle.** Enabled device integration starts asynchronously and remains active
  across Desktop/Game Mode, games, Steam restarts, and shell transitions.
- **INV-004 — Two intentional stop triggers.** Only the owning WSGM process exiting or the user
  turning Device Integration off intentionally ends the device cycle.
- **INV-005 — Faults are not handoffs.** A DeviceHost crash is a recoverable fault in the same
  cycle, not clean deactivation or permission for another manager to race the hardware.
- **INV-006 — Plugin hardware ownership.** The plugin owns exact identity, transports, ranges,
  writes, readback, rollback, restoration, and physical input/output.
- **INV-007 — No generic hardware broker.** WSGM exposes no generic privileged raw
  WMI/HID/EC/IOCTL/ACPI/MMIO/MSR/serial service.
- **INV-008 — One plugin.** Zero installed plugins is valid, one is loadable, and more than one
  refuses normal startup before matching or loading.
- **INV-009 — Static plugin presentation.** Plugins may supply bounded static glyph artwork and a
  declarative physical map, never runtime UI/Steam code, selectors, URLs, or commands.
- **INV-010 — Hard UI boundary.** Active handheld controls live in the overlay; Settings contains
  WSGM ownership/startup/integration/logging/update policy.
- **INV-011 — External ownership is respected.** WSGM does not kill, reconfigure, or silently race
  Handheld Companion, MSI Center, or another external manager.
- **INV-012 — Steam Input fallback remains.** The existing lease path continues for unmanaged,
  degraded, recovery, and per-game launch use.
- **INV-013 — OEM mapping only.** WSGM does not become a general controller/gyro/touch/macro
  remapper; only logical OEM controls use the fixed action vocabulary.
- **INV-014 — Identities remain separate.** Physical presentation, virtual target, Steam Input
  binding, and game-rendered prompts are independent.
- **INV-015 — Native-first Steam.** WSGM restores Valve components narrowly and never globally
  spoofs SteamOS/Deck identity.
- **INV-016 — Feature-local failure.** Device, controller, CEF, RTSS, AutoTDP, and glyph failure
  degrades that feature without blocking WSGM startup or a mode transition.
- **INV-017 — Measured resident work.** Repeating work is cancellable, bounded, and justified by a
  current feature; event-driven behavior is preferred.
- **INV-018 — Owned cleanup.** WSGM and plugins remove/restore only state they own or explicitly
  captured.
- **INV-019 — Correct failure direction.** Unknown hardware identity/ranges fail closed for writes;
  uncertain input or Steam UI fails open to usable physical input/native Valve UI.
- **INV-020 — Explicit installation.** WSGM does not download plugin code/assets or silently
  install/repair drivers, providers, services, tasks, helpers, or certificates at runtime.

## Core and startup requirements

- Recovery-first commands execute before plugin-cardinality validation and remain usable after a
  broken installation.
- Normal startup inventories the effective installed-or-recovery slot once under the machine-wide
  package gate. It does not match hardware until the count is zero or one.
- A multiple-plugin error names every package/path and exits before shell takeover, `ShellSession`,
  DeviceHost, HidHide, virtual targets, or hardware access.
- Zero plugins never blocks core WSGM. If Device Integration was configured on, the UI explains that
  no plugin is installed and normalizes runtime state safely.
- Device startup, Steam discovery, CEF patches, RTSS, telemetry, glyphs, and AutoTDP never delay
  launching/foregrounding Big Picture or completing a Desktop/Game transition.

## Device SDK and packaging requirements

- The SDK exposes one understandable plugin lifecycle, semantic capability registration,
  canonical controller/motion/OEM input, physical output, diagnostics, a small TestKit, and bounded
  manifest/glyph validation. Device Lab owns scaffolding and archive packaging.
- The manifest contains stable package ID/name/version, exact API version, entry assembly, and entry
  type. It does not duplicate hardware policy or act as a permission language.
- A normal plugin is installed through explicit user action into the sole administrator-protected
  plugin slot. Code loaded elevated is never read from a user-writable discovery directory.
- Setup, uninstall, and CLI maintenance reserve the exact machine-wide package and hardware-owner
  objects across host recheck and file mutation; an unverified live DeviceHost refuses mutation.
- API/wire incompatibility produces one clear error; the single integer version must match exactly.
  Internal components installed together do not promise an independent forward/backward
  compatibility matrix.
- Plugin dependencies remain package-local where possible. OEM-installed providers/drivers remain
  explicit prerequisites; runtime code does not repair them.
- The SDK sample demonstrates partial capability availability, cancellation, stop/restoration,
  canonical input/output, and diagnostics without imposing an internal module hierarchy.
- Another developer can build, locally run, validate, pack, install, and diagnose a plugin from the
  public documentation without modifying WSGM.

## DeviceHost requirements

- WSGM creates one DeviceHost for the sole plugin and owns it through a kill-on-close job.
- DeviceHost loads package-local managed/native dependencies deterministically and loads no second
  package.
- One current-session named pipe carries lifecycle, commands, state, low-rate events, and
  diagnostics with bounded frames, exact version, request IDs, cancellation, and a simple launch
  token.
- High-rate controller/motion input uses the existing fixed binary shared ring, kept single-purpose
  and verified with end-to-end latency/loss measurements on the reference unit.
- DeviceHost is described and tested as crash/dependency isolation, not a malicious-code sandbox.
- Start failure or repeated crash leaves core WSGM usable and reports the plugin error.
- Stop has one deadline and returns clean, unverified, timed-out, or failed restoration without
  nested timeout state machines.

## Device Lab requirements

Device Lab retains both guided GUI and repeatable CLI access to:

- Machine/firmware/PnP/HID/WMI/controller/sensor/dependency inventory.
- Exact known-device comparison with explained match/mismatch.
- Passive timestamped capture, operator markers, timelines, and correlation.
- Compiled known read probes with response validation.
- One explicitly selected attended semantic action at a time: capability value, haptic pulse, or
  controller management/release. Every path captures applicable original state, verifies the
  requested outcome, and verifies restore/release before reporting success.
- Minimal plugin scaffolding from confirmed facts.
- Fixture extraction/replay, local plugin run, validation, and packaging.
- Sanitized export that removes user/machine/network/account identifiers and uploads nothing.

Device Lab does not require package promotion, evidence grades, immutable claim IDs, a general
recipe language, a separately versioned implementation catalog, or a separate ProbeHost. Imported
captures are data and never executable authority.

The attended hardware action atomically reserves the same machine-wide owner marker as production
before plugin loading and holds the unowned handle through plugin cleanup and disposal.

## Capability and profile requirements

- Numeric, choice, toggle, action, curve, metric, input, output, and glyph capability shapes cover
  the UI without plugin-supplied UI code.
- Descriptors include stable ID/role, display metadata, supported operations, ranges/steps/units or
  choices, and current availability.
- State includes current value, observation time/freshness, pending command, and an ordinary
  unavailable/error reason.
- WSGM validates UI shape; the plugin revalidates current hardware identity, ownership, range, and
  sequencing for every write.
- Profiles store semantic desired values and per-application overrides, not raw vendor packets.
- Manual changes, profiles, and AutoTDP share one direct precedence path so they cannot fight.

## MSI Claw A2VM requirements

The first-party plugin implements all behavior and exact facts in
[`claw-8-a2vm-plugin.md`](./claw-8-a2vm-plugin.md), including:

- Exact `MS-1T52`/SKU/firmware/provider/controller detection and capability-specific gating.
- Serialized 32-byte MSI WMI transactions.
- PL1 8–30 W, PL2 8–37 W, `PL2 >= PL1`, safe ordering, pair readback, and rollback.
- MSI scenario handling and per-application semantic profiles.
- Two physical fans with Automatic, Custom firmware curve, Full Speed, RPM, and live temperature.
- Exact full-buffer read-modify-write with direct percentage duties and mechanical settling behavior.
- Verified MCU framing, endpoint discovery, XInput/DirectInput mode switching, and physical USB
  location continuation.
- Full physical input including M1-left/M2-right, canonical normalization, and corrupt-first-state
  guard.
- Genuine dual-motor 0–255 rumble with zero-output cleanup.
- Three-axis gyroscope at no more than 100 Hz, calibration/orientation, and no synthesized
  accelerometer.
- OEM1/OEM2 short/long events and narrow orphan-up firmware chord suppression without harming
  ordinary Win+G/Win+Tab, modifiers, volume keys, or the ACPI keyboard.
- Three logical RGB zones over the verified `0x024A` profile block, coalesced explicit persistent
  applies, readback, and supported effects only after validation.
- Diagnostics, suspend/resume, resource-specific conflicts, compact recovery, and clean HC handoff.

Unknown or optional charge/RGB-effect/PID behavior remains unavailable until hardware validation;
that is honest feature gating, not removal of the supporting capability.

## Controller requirements

- HIDMaestro remains behind one WSGM backend interface and must be technically capable of the
  required targets/fields before release.
- Steam Deck Composite, Xbox 360, and DualShock 4 all pass live target-specific acceptance.
- WSGM stores a global target plus per-application override and exposes selection in Device and
  native QAM.
- Exactly one virtual target is active. Replacement neutralizes/removes the old target before the
  new one becomes the gameplay source, with a clear application-restart warning where needed.
- Physical output returns to the plugin and always stops on target/fault/suspend/disable.
- WSGM tracks and removes only its HidHide entries.
- Managed plugin input navigates WSGM overlay/taskbar/Settings directly.
- Local UI capture suppresses held controls, neutralizes the virtual target, and releases only after
  UI-used controls return neutral.
- SDL plus Steam Input lease fallback switches make-before-break when managed input becomes
  unavailable.
- Controller disable and full device stop return to usable physical or fallback input before
  removing WSGM's hiding/target state.

## Steam CEF and native-QAM requirements

- One persistent Steam UI owner discovers the correct Big Picture targets, reconnects after
  `steamwebhelper`/Steam restart, and handles context replacement.
- Existing Wi-Fi/library/card/download features migrate without regression.
- Built-in patches use direct probe/apply/verify/remove behavior and fail independently.
- Native-first restoration exposes TDP, RTSS frame limit, performance-overlay level, supported
  profile/metrics, controller target, and supported AutoTDP controls.
- No global SteamOS/Deck spoof or generic evaluation/plugin/device bridge exists.
- OEM2 opens native QAM when healthy and otherwise opens the WSGM Device page once.
- Steam patch failure leaves native Valve UI and never delays Game Mode entry.

## RTSS and performance requirements

- RTSS discovery/control survives RTSS absence, restart, external profile edits, and readback
  mismatch without blocking WSGM.
- Frame limit and performance-overlay level remain available independently of Device Integration.
- One performance service combines verified RTSS frametimes/metrics and optional plugin telemetry
  with timestamps and freshness.
- Overlay, QAM, diagnostics, and AutoTDP consume the same current performance snapshot.
- Sampling cadence is consumer-aware and bounded; stale data is not displayed or used for power
  decisions.

## AutoTDP requirements

- One session-owned service consumes verified foreground frametimes and the current primary-power
  capability.
- The first objective is target frametime reliability; the second is the lowest sustainable power
  and fewest unnecessary writes.
- Sustained deadline misses raise power promptly. Stable operation permits one-step downward probes.
- A harmful downward probe immediately restores the last known good value and backs off another
  attempt.
- Loading/focus loss/discontinuous/stale/post-write-unsettled windows do not drive normal decisions.
- Capped/menu states may descend while preserving observed cadence; they do not trigger maximum
  power merely for sitting below the configured FPS target.
- Abrupt heavier scenes recover without turning that scene into a permanent global floor.
- At maximum power with a missed target, state reports temporarily unreachable rather than
  accumulating demand.
- Accepted per-device/per-application/context learning persists; live windows, transient scene
  state, and failed probes do not.
- Manual TDP pauses AutoTDP, one power write may be in flight, and disabling/stopping restores the
  underlying manual/profile value exactly once.
- Deterministic replay covers stable, capped, menu, loading, abrupt/heavy, noisy, thermally drifting,
  and source-restart traces before hardware enablement.
- Overlay and QAM show enabled/paused/calibrating/tracking/probing/recovering/unreachable/faulted
  state, current power, target, and actionable error.
- CPU/GPU utilization is never used as the control explanation or policy input.

## Glyph requirements

- The active plugin supplies a static profile, controller artwork, control assets, presence map,
  physical labels, source revision, and required license notice.
- WSGM validates paths, formats, sizes, dimensions, IDs, and references for reliability, then
  normalizes/renders the active package.
- Automatic exact-device selection, Native Steam fallback, and manual diagnostic selection exist.
- WSGM owns all Steam routes/selectors/asset delivery and all Avalonia adaptation.
- Steam Input diagrams and relevant Gamepad UI prompts match the physical device without changing
  virtual target, bindings, enumeration, or game-rendered prompts.
- WSGM preview, input test, OEM rows, and navigation hints use the same physical map.
- Route/context churn, Steam restart, profile change, disable, and exit leave no stale styles,
  observers, or URLs.
- CSS Loader/Decky conflict is detected and left externally owned.
- The A2VM profile receives physical visual acceptance, especially OEM sides and M1-left/M2-right.

## Overlay and Settings requirements

- Top-level destinations are Home, Steam, Device, and System.
- Device includes Overview, Profiles, Power/Thermals, Controller/Motion, OEM Controls,
  Lighting/Features, Glyph Preview/Input Test, and Diagnostics/Recovery.
- Existing Home/Steam/System actions migrate rather than being reimplemented behind duplicate
  services.
- QAM and overlay are synchronized views of the same commands/state.
- Controller, touch, keyboard, Back behavior, focus/scroll restoration, scaling, accessibility,
  themes, cancellation, and error presentation work on the handheld display.
- Settings owns only WSGM integration/startup/controller-ownership/logging/update configuration and
  can request changes from the current owner process.

## Shutdown, update, installer, and uninstall requirements

- One shutdown coordinator owns normal, update, logoff, stop, and uninstall requests.
- It rejects new commands, establishes usable input, stops AutoTDP/output, restores plugin temporary
  state, removes WSGM target/HidHide state, stops CEF/RTSS, and flushes logs within one outer deadline.
- Update/installer receives one clean/unverified/timed-out/failed result and uses a bounded fallback.
- Plugin/runtime update is atomic and never exposes two discoverable plugin directories.
- Uninstall removes only WSGM-owned service/task/target/HidHide/plugin/CEF/configuration state and
  preserves MSI Center, HC, external HidHide, Steam, RTSS, and user data unless explicitly chosen.
- A failed graceful uninstall may remove WSGM's target/HidHide ownership but never invent hardware
  restoration after the plugin is unavailable.
- Core-only installation remains possible and Device Integration defaults to off.

## Required validation

### Automated

- Build/NativeAOT and dependency-boundary checks.
- Focused parsing, bounds, lifecycle, ownership, cleanup, and regression tests.
- Host/pipe/package/cardinality/crash integration tests.
- Claw fixtures for exact transports and malformed responses.
- Controller target/input/output/HidHide/source-switch tests.
- Steam/RTSS/glyph fixtures for reconnect, mismatch, cleanup, and native fallback.
- AutoTDP deterministic replay and state-machine tests.
- Overlay navigation/view-model tests for shared state and existing-feature parity.

### Live and hardware

- Exact Claw detection and negative identity checks.
- Power/fan/RGB/controller/rumble/motion/OEM operations with restoration.
- All controller targets, per-application changes, UI capture, HidHide, and fallback.
- Steam QAM/RTSS/glyph behavior on supported current Steam builds.
- AutoTDP calibration/adaptation across representative games, caps, menus, scene changes,
  AC/battery, suspend/resume, manual overrides, and long runs.
- Desktop/Game transitions, Steam restart, suspend/hibernate/lock, host crash, Device Integration
  toggle, HC handoff, update, rollback, uninstall, and reinstall.
- Idle/gameplay CPU, memory, handles, wakeups, latency, and monotonic resource growth.

The manual matrix should cover meaningful state boundaries and regressions. It does not require 100
repetitions of every operation or a compliance record for every theoretical combination unless a
specific instability makes that repetition useful.

## Not required

- Generated capability-to-requirement-to-code-to-test traceability.
- Requirement IDs in commits and pull requests.
- One project/test project/`AGENTS.md` per conceptual layer.
- Package trust promotion, signer rotation/revocation, or an internal marketplace.
- A general evidence ledger and immutable constant-to-claim database.
- Forward/backward golden tests for every atomically installed internal DTO.
- Universal parser fuzzing and a fault scheduler for every theoretical boundary.
- Security claims against malicious administrator-installed plugins.
