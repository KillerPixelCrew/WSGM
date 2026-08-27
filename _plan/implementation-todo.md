# WSGM 2.0 end-to-end implementation TODO

Status: execution backlog  
Branch: `2.0`  
Design baseline commit: `38b18cddeecacf2313030b530693353471e93495`  
Initial reference device: MSI Claw 8 AI+ A2VM (`MS-1T52`)

## Purpose and use

This is the ordered implementation backlog for all requirements in:

- [`2.0-design.md`](./2.0-design.md)
- [`device-plugin-system-and-tooling.md`](./device-plugin-system-and-tooling.md)
- [`claw-8-a2vm-plugin.md`](./claw-8-a2vm-plugin.md)
- [`controller-glyph-integration.md`](./controller-glyph-integration.md)

It is intentionally finer-grained than the milestone lists in those documents. A checkbox is
complete only when its implementation, automated coverage, diagnostics, documentation, and
applicable live or hardware evidence are complete. A compiling spike, successful fixture replay,
successful hardware trial, trusted package, and retail-approved feature are separate states and must
never be collapsed.

Baseline reality at the design commit:

- Branch `2.0` differs from `master` only by the four source plans; no 2.0 production code exists
  yet.
- The solution contains the NativeAOT `WSGM`, `WSGM.Launch`, and `WSGM.LogonService` projects plus
  the existing xUnit project; DeviceHost, SDK, Device Lab, plugin, HIDMaestro, HidHide, RTSS, and
  glyph catalog projects are all new work.
- Reusable foundations include the current one-shot validated Steam CEF path, SDL gamepad service,
  owner-scoped Steam Input lease, `ShellSession`, `ConfigStore`, `GlyphIcon`, and existing overlay.
- The current shell lifetime is not an awaited hardware-cleanup boundary, standalone Settings
  processes may coexist, and `--overlay-test` must remain hardware-free; authority and shutdown are
  explicit blocking decisions below.
- Existing shell/session and Steam Input behavior is device-verified and must be preserved while the
  new device lifecycle remains asynchronous and independent from Desktop/Game Mode transitions.

Tags used below:

- **DECISION**: freeze a contract or policy before dependent implementation.
- **SOFTWARE**: can be completed and tested without the reference handheld.
- **LIVE-STEAM**: requires probing a current Steam client; do this early with the existing CEF
  harness.
- **HARDWARE**: requires explicit MSI Claw A2VM validation.
- **DESTRUCTIVE-RISK**: bounded hardware mutation; only the named Device Lab trial path may perform
  it.
- **LEGAL**: licensing, redistribution, signing, or provenance gate.
- **RELEASE-GATE**: all child items must pass before the next phase or retail release.

Unless a task says otherwise, every implementation slice must:

- Follow the root and nearest `AGENTS.md` ownership rules.
- Keep the main WSGM executable NativeAOT-safe and free of reflection-dependent/plugin runtime code.
- Keep blocking process, CEF, file, and device work off the Avalonia UI thread.
- Use explicit cancellation, bounded retries, contextual `Log` diagnostics, and deterministic
  cleanup.
- Add isolated tests without touching the user's real `%LOCALAPPDATA%\WSGM` state.
- Keep device-only, Steam-live, shell-takeover, and destructive flows out of unattended tests.
- Run `./eng/verify.ps1`, then `./build.ps1` and the required installer handoff for completed
  implementation work on the Windows development machine.

## Non-negotiable product invariants

- [ ] **INV-001** Keep Game Mode, Big Picture, shell/session transitions, storage, artwork,
      launch-fix, and the existing overlay usable with Device Integration off.
- [ ] **INV-002** Make Device Integration optional and controller management its optional child;
      prevent or normalize the invalid `Device Integration off + controller management on` state.
- [ ] **INV-003** Start device integration asynchronously with WSGM and keep one device cycle across
      Desktop Mode, Game Mode, games, Steam restarts, and shell transitions.
- [ ] **INV-004** Treat only WSGM exit and the Settings Device Integration master toggle as normal
      device-cycle terminal triggers.
- [ ] **INV-005** Treat DeviceHost failure as an in-cycle fault with bounded recovery/quarantine,
      never as clean deactivation or external-manager handoff.
- [ ] **INV-006** Let plugins own every hardware transport, protocol, layout, safety policy, write,
      readback, rollback, and recovery mechanism; keep WSGM at semantic contracts and orchestration.
- [ ] **INV-007** Do not add a generic WSGM AMD/Intel power backend, EC/PawnIO service, raw
      WMI/HID/IOCTL/ACPI/MMIO/MSR/serial proxy, or shared privileged hardware broker.
- [ ] **INV-008** Run one selected plugin package per unelevated DeviceHost process; describe this
      as crash/dependency isolation, not a malware sandbox.
- [ ] **INV-009** Never let a device plugin supply XAML, HTML, CSS, JavaScript, SVG, URLs, arbitrary
      artwork, arbitrary commands, or an arbitrary privileged operation.
- [ ] **INV-010** Keep all active handheld controls in the overlay; keep only WSGM ownership,
      startup, integration, logging, and update policy in Settings.
- [ ] **INV-011** Never start, stop, kill, reconfigure, or silently race Handheld Companion, MSI
      Center M, or another external manager.
- [ ] **INV-012** Keep the Steam Input lease infrastructure permanently available for unmanaged,
      degraded, recovery, and per-game launch paths.
- [ ] **INV-013** Permit only OEM controls to be reassigned; do not grow a general controller,
      keyboard-macro, gyro, or touch remapper.
- [ ] **INV-014** Keep physical handheld presentation, virtual target, Steam Input binding, and
      in-game prompt identity as four independent concepts.
- [ ] **INV-015** Restore Valve's native Steam UI with narrow patches before considering replacement
      UI; never globally spoof SteamOS or Steam Deck identity.
- [ ] **INV-016** Make device, controller, CEF, RTSS, and glyph failures degrade only their feature
      and never block WSGM startup, the desktop overlay, or a mode transition.
- [ ] **INV-017** Prefer event-driven state; document, bound, cancel, dispose, and measure every
      repeating loop on the Claw.
- [ ] **INV-018** Make all ownership changes reversible and remove only WSGM-owned HidHide, CEF,
      virtual-device, style, hook, file, and configuration state.
- [ ] **INV-019** Fail closed for hardware writes and uncertain device/profile identity; fail open
      to usable input/native Steam UI when that is the safer user outcome.
- [ ] **INV-020** Keep runtime asset, plugin, and dependency updates offline/explicit; never
      download glyph assets or install/repair plugin dependencies during plugin execution.

## Critical path and phase gates

| Order | Gate                    | Can proceed when                                                                                        |
| ----: | ----------------------- | ------------------------------------------------------------------------------------------------------- |
|     0 | Baseline normalization  | All blocking decisions in `P0` are recorded and contradictions have one authoritative answer            |
|     1 | Contract freeze         | Semantic, lifecycle, package, IPC, input/output, evidence, and patch contracts pass compatibility tests |
|     2 | Safe bring-up           | Device Lab can inventory, match, probe reads, scaffold, and run one separately authorized bounded trial |
|     3 | Phase 0 proof + A2VM M0 | Hardware questions/writes and mandatory HID/QAM/RTSS/glyph experiments have reviewed evidence           |
|     4 | Device runtime          | One DeviceHost generation survives the full WSGM run and cleanly hands hardware back                    |
|     5 | Controller runtime      | HIDMaestro, HidHide, UI capture, output routing, and fallback pass the complete target matrix           |
|     6 | Steam UI                | Persistent patch host, native QAM, RTSS, and glyph tiers are asynchronous, isolated, and verified       |
|     7 | Final overlay           | Capability-driven navigation and the complete Device destination pass handheld UX validation            |
|     8 | Retail release          | Packaging, coexistence, recovery, performance, legal, installer, and update gates all pass              |

Device Lab contracts and read-only work can proceed in parallel with controller/CEF experiments
after the shared schemas are frozen. The production Claw write capabilities cannot start before
their M0 trial evidence. The final information architecture cannot be locked before capability,
controller, QAM, and RTSS contracts stabilize.

## P0 — normalize the baseline before implementation

### P0.1 Resolve cross-document behavior and sequencing

- [ ] **P0-001 · DECISION** Record one authoritative controller activation transaction, including
      whether the virtual target is created before HidHide is applied or the physical device is
      hidden before target creation; define rollback for every intermediate state.
- [ ] **P0-002 · DECISION** Decide whether HidHide is mandatory, default-on, or configurable
      whenever WSGM controller management is active; update every dependent acceptance case
      consistently.
- [ ] **P0-003 · DECISION** Split the early provisional capability-control surface required by Claw
      M1/M2 from the design's final Phase 4 overlay redesign; define what may ship temporarily and
      what is removed.
- [ ] **P0-004 · DECISION** Define which OEM sources and actions survive when controller management
      is off, especially M1/M2 if DirectInput acquisition is released to an external manager.
- [ ] **P0-005 · DECISION** Preserve the physical handheld profile when only WSGM virtual-controller
      management is off, then define presentation/input-prompt authority when an external or
      unmanaged controller is active so that controller is never mislabeled as the handheld.
- [ ] **P0-006 · DECISION** Define how the Win+G suppressor satisfies elevated-foreground
      acceptance: prove the medium-integrity path, approve the fixed-operation helper, or narrow the
      release claim.
- [ ] **P0-007 · DECISION** Define how genuinely new developer-authored mutation trials are
      proposed, reviewed, hash-pinned, installed, and promoted before `probe run` may execute them.
- [ ] **P0-008 · DECISION** Resolve native QAM first-milestone scope for performance profile and
      performance data: interactive control, read-only projection, or explicitly deferred.
- [ ] **P0-009 · DECISION** Decide whether RTSS frame-limit/performance-overlay controls remain
      usable with Device Integration off and, if so, which non-Device surface owns them.
- [ ] **P0-010 · DECISION** Define selected-plugin cardinality and tie-breaking across multiple
      physical handhelds, multiple matching packages, detachable devices, and stale devices.
- [ ] **P0-011 · DECISION** Define how immutable capability descriptors are replaced when firmware,
      AC/DC state, endpoint generation, or availability changes their ranges or operations.
- [ ] **P0-012 · DECISION** Keep captured hardware state restoration-only and outside desired-state
      precedence; separately freeze global defaults, AC/DC policy, selected hardware profile,
      per-application override, and active UI request, plus persistent-versus-temporary restore
      rules.
- [ ] **P0-013 · DECISION** Define the safest full-deactivation timeout topology, follow-up retries,
      warning state, recovery-journal entry, and next-start reconciliation.
- [ ] **P0-014 · DECISION** Define plugin update/remove/defer UX while its process-long device cycle
      is active; do not silently restart the host as an update mechanism.
- [ ] **P0-015 · DECISION** Define quarantine behavior: retained desired state, fallback input,
      visible Device page state, manual retry, cooldown, and reset conditions.
- [ ] **P0-016 · DECISION** Define exact global/per-application controller-target matching,
      precedence, process identity, unknown-app fallback, and when a live target switch occurs.
- [ ] **P0-017 · DECISION** Define glyph selection precedence and persistence for `Automatic`,
      `Native Steam glyphs`, and a reviewed manual diagnostic override.
- [ ] **P0-018 · DECISION** Define the testable threshold for declaring a Valve component genuinely
      removed before authorizing a WSGM-rendered replacement.
- [ ] **P0-019 · DECISION** Define how audited plugin dependencies are installed, repaired, updated,
      removed, and verified without granting runtime installation authority to a plugin.
- [ ] **P0-020 · DECISION** Define the release fallback if HIDMaestro misses a mandatory gate: delay
      controller management, ship Device Integration without it, or revisit the backend decision.
- [ ] **P0-021 · DECISION** Define the first WSGM 2.0 release boundary and explicitly classify
      DualSense and validated optional Claw capabilities as later work where applicable.
- [ ] **P0-022 · DECISION** Separate Device diagnostics from general WSGM diagnostics/logging with
      an explicit field/action ownership list.
- [ ] **P0-023 · DECISION** Freeze the allowlisted OEM action vocabulary and which actions are legal
      for front buttons versus rear controls on targets without paddles.
- [ ] **P0-024 · DECISION** Define make-before-break source-switch state transfer, held-control
      suppression, neutralization, and failure behavior between canonical and SDL input.
- [ ] **P0-025 · DECISION** Define how WSGM knows the restored physical controller is available for
      an external owner without assuming that Handheld Companion actually acquired it.

### P0.2 Freeze implementation boundaries and repository layout

- [ ] **P0-026 · SOFTWARE** Choose names and locations for the NativeAOT-safe semantic contracts,
      JIT DeviceHost, Device Plugin SDK, Device Lab GUI, `wsgm-device` CLI, probe host, reference
      plugin, catalog, fixtures, and generated projects.
- [ ] **P0-027 · SOFTWARE** Add the selected projects to `WSGM.slnx` with dependency direction tests
      that prevent WSGM Core from referencing plugin/runtime hardware assemblies.
- [ ] **P0-028 · SOFTWARE** Add directory-level `AGENTS.md` files for new ownership boundaries,
      including the explicit exception that the Claw keyboard suppressor lives in its plugin host
      and not in WSGM's general `Input` module.
- [ ] **P0-029 · SOFTWARE** Add build configurations that keep WSGM NativeAOT while allowing the
      DeviceHost, Device Lab, SDK tooling, WMI, and WinRT sensor code to remain JIT-capable.
- [ ] **P0-030 · SOFTWARE** Add solution/build checks that no plugin, analyzer, generator, WMI
      library, WinRT sensor library, or reflection-heavy tooling is copied into or loaded by the
      WSGM process.
- [ ] **P0-031 · SOFTWARE** Define generated-artifact directories and gitignore rules; never
      hand-copy native/driver binaries into generated staging directories.
- [ ] **P0-032 · SOFTWARE** Define test projects for contracts, host supervision, Device Lab,
      generators, Claw fixtures, HIDMaestro adapter, Steam UI patches, glyph import, and UI view
      models.
- [ ] **P0-033 · SOFTWARE** Add CI jobs/caches for the new .NET, TypeScript, generator,
      native-driver, and fixture validation steps, each with timeouts and minimal permissions.
- [ ] **P0-034 · SOFTWARE** Add a repository script that validates no generated test/probe path
      points at the real `%LOCALAPPDATA%\WSGM` directory.
- [ ] **P0-035 · SOFTWARE** Document safe local commands and explicitly prohibit unattended
      `--shell`, `--boot`, plugin lifecycle, hardware mutation, and Device Lab trial execution.

### P0.3 Legal, trust, dependency, and release policy gates

- [ ] **P0-036 · LEGAL** Audit the current WSGM license, contributor ownership, and ability to
      relicense; record the final license decision before copying any incompatible implementation
      code.
- [ ] **P0-037 · LEGAL** Classify each Handheld Companion, Linux `hid-msi`, HHD, ClawTweaks, VIIPER,
      HIDMaestro, usbip-win2, HidHide, MSI provider, RTSS, and glyph input as fact, behavioral
      reference, copied code, dependency, binary, or independently captured evidence.
- [ ] **P0-038 · LEGAL** Audit HIDMaestro MIT and usbip-win2 BSD-2-Clause packaging, driver, notice,
      and redistribution requirements.
- [ ] **P0-039 · LEGAL** Audit Handheld Controller Glyphs commit
      `46792aadf3b104efec1c5240ba414d2c0bf84127`, its MIT notice, and credited source-artwork
      provenance.
- [ ] **P0-040 · LEGAL** Establish whether and how the official MSI WMI provider may be detected,
      installed, and redistributed; keep capabilities unavailable until this is resolved.
- [ ] **P0-041 · LEGAL** Define license/provenance metadata required in catalogs, evidence locks,
      generated projects, packages, installer entries, and third-party notices.
- [ ] **P0-042 · DECISION** Freeze package trust tiers (`WSGM-reviewed`, `Signed external`,
      `Sideloaded community`, `Developer`) and their install, enable, warning, update, revocation,
      and promotion behavior.
- [ ] **P0-043 · DECISION** Freeze publisher identity, signature verification, key rotation,
      downgrade, revocation, package rollback, and compromised-publisher handling.
- [ ] **P0-044 · DECISION** Freeze the reviewed privileged-helper policy, fixed-operation review
      checklist, signer/hash requirements, protected install location, ACLs, and uninstall behavior.
- [ ] **P0-045 · DECISION** Define the audited dependency catalog fields: version, hash, signer,
      license, architecture, install owner, health check, ACL, upgrade, rollback, and removal
      behavior.
- [ ] **P0-046 · RELEASE-GATE** Review the full accepted security posture against
      `docs/decisions.md`; document concrete trust boundaries without replacing WSGM's deliberate
      elevation, injection, native-code, or shell mechanisms with generic policy advice.

### P0.4 Runtime authority, shutdown, and remaining boundary decisions

- [ ] **P0-047 · DECISION** Define the one authoritative per-user/session owner of the device cycle
      when shell, standalone Settings, and other WSGM processes may coexist; specify discovery,
      election, connection, takeover prevention, and what “WSGM exits” means across those processes.
- [ ] **P0-048 · DECISION** Preserve `--settings` and `--overlay-test` safety: decide which mode may
      connect read-only to an existing device owner, and prove neither mode independently acquires
      or mutates hardware, launches apps, changes HidHide, or starts a duplicate DeviceHost.
- [ ] **P0-049 · DECISION** Freeze bounded asynchronous shutdown ownership and deadlines for normal
      exit, update exit, uninstall, logoff, service/session stop, crash, and forced timeout before
      any production hardware write; define exactly what the installer may do after graceful cleanup
      fails.
- [ ] **P0-050 · LEGAL** Prohibit redistribution of any proprietary OEM DLL, provider, driver,
      helper, firmware, or asset without documented rights; model externally installed prerequisites
      separately from redistributable reviewed components.
- [ ] **P0-051 · DECISION** Define the user-visible OEM2 behavior when Steam is absent, starting,
      outside Big Picture, on Desktop, or has an unhealthy QAM fingerprint: no-op, WSGM overlay
      fallback, or another single immediate bounded action, with no queued replay or duplicate
      transition.

## P1 — semantic contracts and protocol foundation

### P1.1 Package, identity, device-definition, and module contracts

- [ ] **P1-001 · SOFTWARE** Define the versioned `plugin.wsgm.json` schema with stable package ID,
      API range, publisher, executable entry, devices, resources, risks, dependencies,
      implementation modules, and declared capabilities.
- [ ] **P1-002 · SOFTWARE** Define bounded string/list/object sizes and reject duplicate IDs,
      unknown critical fields, path traversal, invalid versions, and unsupported schema versions.
- [ ] **P1-003 · SOFTWARE** Define exact normalized identity fields for SMBIOS manufacturer,
      product, board, revision, CPU family, BIOS/EC/MCU firmware, PnP topology, descriptors, and
      report shapes.
- [ ] **P1-004 · SOFTWARE** Define required, excluded, optional, and weighted identity observations;
      preserve marketing names as weak display evidence only.
- [ ] **P1-005 · SOFTWARE** Define one logical handheld and its resource/endpoint graph, including
      detachable endpoints and topology/device generations.
- [ ] **P1-006 · SOFTWARE** Define device definitions as exact identity/firmware gates plus pinned
      composition; prohibit policy inheritance from a monolithic older-device class.
- [ ] **P1-007 · SOFTWARE** Define implementation-module metadata for transport, protocol, layout,
      policy, capability, dependencies, conflicts, safety, recovery, evidence, and license
      provenance.
- [ ] **P1-008 · SOFTWARE** Enforce that reusing a transport/protocol cannot import another model's
      ranges, offsets, persistence assumptions, or firmware policy.
- [ ] **P1-009 · SOFTWARE** Add manifest/schema fixtures for valid, malformed, oversized, unknown,
      forward-compatible, and incompatible packages.
- [ ] **P1-010 · SOFTWARE** Add source-generated serialization and deterministic canonicalization
      for all NativeAOT-visible package/identity contracts.

### P1.2 Capability descriptors, state, commands, and desired-state projection

- [ ] **P1-011 · SOFTWARE** Define stable capability and instance IDs plus semantic roles for power,
      scenario, fan, charge, lighting, telemetry, controller, motion, output, OEM, generic toggle,
      range, choice, action, and read-only values.
- [ ] **P1-012 · SOFTWARE** Define WSGM-owned localized display-schema keys plus a length-bounded,
      escaped, untrusted plain-text fallback for reviewed device-specific names; plugins may never
      supply markup, formatting, localization resources, or executable presentation content.
- [ ] **P1-013 · SOFTWARE** Define immutable descriptor fields for read/write/action support, min,
      max, step, unit, AC/DC availability, mutual exclusion, persistence, and activation,
      re-enumeration, restart, or reboot requirements.
- [ ] **P1-014 · SOFTWARE** Define descriptor generation/replacement and consumer invalidation rules
      from the result of `P0-011`.
- [ ] **P1-015 · SOFTWARE** Define live capability availability, command progress, observed/applied
      value, state quality, observation time, host generation, device generation, and structured
      reason.
- [ ] **P1-016 · SOFTWARE** Implement exact hardware-state qualities: `Unknown`, `Observed`,
      `Verified`, `Stale`, and `Faulted`.
- [ ] **P1-017 · SOFTWARE** Implement exact command outcomes: `Accepted`, `AppliedUnverified`,
      `AppliedVerified`, `Rejected`, `TimedOut`, and `Indeterminate`.
- [ ] **P1-018 · SOFTWARE** Define command IDs, idempotency keys, expected descriptor/device
      generation, deadline, cancellation, validation error, readback evidence, and rollback result.
- [ ] **P1-019 · SOFTWARE** Define structured unavailable/degraded/conflict/prerequisite/unsupported
      reason taxonomy with safe user text and diagnostic detail.
- [ ] **P1-020 · SOFTWARE** Implement WSGM's projection of authoritative desired value, profile
      source, pending request, UI progress, and last observed plugin state.
- [ ] **P1-021 · SOFTWARE** Define per-capability freshness policy and ensure disconnect, generation
      change, or expiry marks state stale and disables affected commands.
- [ ] **P1-022 · SOFTWARE** Ensure a successful IPC reply is never presented as verified hardware
      readback unless the plugin explicitly provides qualifying evidence.
- [ ] **P1-023 · SOFTWARE** Require the plugin to revalidate identity, firmware, ownership, range,
      relationship, and current state on every hardware command.
- [ ] **P1-024 · SOFTWARE** Add exhaustive serialization, version negotiation, stale-state,
      out-of-order delta, duplicate command, timeout, cancellation, and indeterminate-result tests.

### P1.3 Lifecycle, resource ownership, recovery, and diagnostics contracts

- [ ] **P1-025 · SOFTWARE** Define lifecycle states and messages for detect, activate, capability
      publication, suspend, resume, deactivate, release, fault, restart, and quarantine.
- [ ] **P1-026 · SOFTWARE** Define per-resource states so controller, power, fan, lighting, motion,
      OEM, and telemetry can become active/passive/degraded independently.
- [ ] **P1-027 · SOFTWARE** Define resource-lease acquisition, conflict, ordering, cancellation,
      release, and experiment-lease contracts without exposing raw transports over production IPC.
- [ ] **P1-028 · SOFTWARE** Define snapshot and recovery-journal schema with identity, firmware,
      host/device generation, original state, planned mutation, applied mutation, cleanup status,
      and atomic sequence number.
- [ ] **P1-029 · SOFTWARE** Define journal location, ACL, atomic replace, corruption handling,
      compatibility migration, retention, and startup reconciliation.
- [ ] **P1-030 · SOFTWARE** Define controller two-phase handoff messages for neutralized, physical
      acquisition stopped, original mode restored, topology verified/unverified, and WSGM cleanup
      done.
- [ ] **P1-031 · SOFTWARE** Define suspend/lock deadlines and ensure no long firmware operation can
      begin once quiescence starts.
- [ ] **P1-032 · SOFTWARE** Define hotplug/re-enumeration continuation by container identity and
      invalidate every handle/state from the previous generation.
- [ ] **P1-033 · SOFTWARE** Define crash restart/backoff/quarantine parameters and manual recovery
      messages from `P0-015`.
- [ ] **P1-034 · SOFTWARE** Define versioned read-only DeviceHost diagnostics and a bounded,
      plugin-owned diagnostic-session contract for Device Lab.
- [ ] **P1-035 · SOFTWARE** Define sanitized logging fields for package, host, device, resource,
      operation, generation, duration, queue depth, timeout, and result.
- [ ] **P1-036 · SOFTWARE** Add lifecycle model tests for full WSGM run, Desktop/Game transitions,
      controller-only disable, full disable, crash, restart, quarantine, suspend, hotplug, and
      timeout.

### P1.4 Canonical controller, OEM, output, and UI-input contracts

- [ ] **P1-037 · SOFTWARE** Define canonical standard buttons, D-pad, sticks, triggers, rear
      paddles, gyro, accelerometer, touchpads, touch contacts, and stick-touch state without
      assuming a target.
- [ ] **P1-038 · SOFTWARE** Define neutral state, sequence, timestamp, device generation,
      report-loss, discontinuity, calibration, and sample-quality fields.
- [ ] **P1-039 · SOFTWARE** Define logical OEM controls as a separate channel with stable ID,
      required display-name metadata under `P1-012`, type, source generation, timestamp,
      deduplication ID, and allowed routing class.
- [ ] **P1-040 · SOFTWARE** Define virtual-output/haptic return state separately, including target
      generation, motor/channel semantics, stop, rate, and unsupported-degradation behavior.
- [ ] **P1-041 · SOFTWARE** Define stable physical device identities and topology data WSGM needs
      for HidHide without exposing plugin raw hardware operations.
- [ ] **P1-042 · SOFTWARE** Define the WSGM-owned controller backend interface so plugins never call
      HIDMaestro and a future backend can replace it.
- [ ] **P1-043 · SOFTWARE** Define `IUiGamepadSource` semantics for full state, edges, repeat,
      chords, source health, source generation, held-state suppression, and duplicate filtering.
- [ ] **P1-044 · SOFTWARE** Define reference-counted local UI capture ownership, nested/handover
      surfaces, neutralization, release boundary, and failure behavior.
- [ ] **P1-045 · SOFTWARE** Define output-router ownership and mandatory zero-output triggers for UI
      capture, target removal, game exit, suspend, disconnect, plugin disable, and fault.
- [ ] **P1-046 · SOFTWARE** Add pure mapping/normalization tests for richest state, unsupported
      fields, target consumption, OEM mutual exclusion, neutralization, and no synthetic gyro
      mappings.

### P1.5 IPC and process boundary

- [ ] **P1-047 · DECISION** Freeze the bounded binary wire format, protocol-version negotiation,
      compatibility window, schema fingerprints, and unknown-message behavior.
- [ ] **P1-048 · SOFTWARE** Implement per-session named-pipe naming, current-user SID ACL, endpoint
      authentication material, handshake, package identity binding, and replay resistance.
- [ ] **P1-049 · SOFTWARE** Implement request IDs, responses, notifications, cancellation,
      deadlines, idempotency, bounded payloads, and backpressure on the control plane.
- [ ] **P1-050 · SOFTWARE** Implement a fixed binary shared-memory state page or bounded ring buffer
      for high-rate controller/IMU data with sequence counters, generation, event signal, overflow,
      and reader recovery.
- [ ] **P1-051 · SOFTWARE** Measure pipe versus return-ring cost and choose a bounded rumble/output
      channel without perceptible latency.
- [ ] **P1-052 · SOFTWARE** Reject generic execute, shell, file, WMI, HID, EC, IOCTL, script, path,
      helper, and raw-buffer operations at the protocol/schema boundary.
- [ ] **P1-053 · SOFTWARE** Add fuzz/property tests for malformed lengths, oversized fields, unknown
      versions, truncation, reordering, stale generations, producer death, slow readers, and
      cancellation.
- [ ] **P1-054 · SOFTWARE** Prove the shared contract library and WSGM client survive NativeAOT
      publish without COM, runtime reflection, dynamic loading, or non-blittable native interop.

### P1.6 Contract freeze gate

- [ ] **P1-055 · RELEASE-GATE** Review every public contract with meaningful XML documentation and
      executable compatibility tests.
- [ ] **P1-056 · RELEASE-GATE** Verify the contract exposes semantic capabilities only and contains
      no device-specific address, MSI method, raw buffer, or privileged operation.
- [ ] **P1-057 · RELEASE-GATE** Version and freeze the first runtime contract targeted by
      DeviceHost, scaffold, validator, packer, Claw plugin, WSGM client, and fixtures.
- [ ] **P1-058 · RELEASE-GATE** Record backward/forward compatibility, deprecation, and extension
      policy for package, capability, state, capture, catalog, evidence, fixture, and patch schemas.

## P2 — Device Lab, known implementations, evidence, and scaffolding

### P2.1 D0 schemas and developer surfaces

- [ ] **P2-001 · SOFTWARE** Define the versioned known-implementation catalog schema for identity,
      candidate predicates, firmware, endpoint roles, transport, protocol, layout, capabilities,
      safety, probes, recovery, evidence, dependencies, conflicts, and licensing.
- [ ] **P2-002 · SOFTWARE** Define three independent candidate outputs: reuse rank, a separately
      derived candidate evidence grade, and write eligibility; never derive one from similarity or
      automatically promote evidence grade into write eligibility.
- [ ] **P2-003 · SOFTWARE** Define exact claim states (`Candidate`, `Correlated`, `Corroborated`,
      `HardwareVerified`, `RetailApproved`, `Rejected`) separately from candidate evidence grade,
      provenance, package trust, and runtime write eligibility.
- [ ] **P2-004 · SOFTWARE** Define compatibility execution, observation, mutation, cleanup, and
      derived verdict enums exactly as described in the tooling design.
- [ ] **P2-005 · SOFTWARE** Define private capture, sanitized `.wsgmcap`, observe-only recipe,
      stream event, analysis result, claim ledger, redaction, blob, and hash schemas.
- [ ] **P2-006 · SOFTWARE** Require each event to carry source/step IDs, local/global sequence, QPC
      receipt time, optional source time, clock segment, device generation, payload length, exact
      payload bytes where permitted, and loss/discontinuity/timeout/access state.
- [ ] **P2-007 · SOFTWARE** Require each claim to carry a stable claim ID, scope,
      transport/endpoint, selector, offset, mask, width, endian, scale, unit, range, meaning,
      evidence, counterexamples, repetition, restoration, analyzer, provenance, limitations, and
      supersession.
- [ ] **P2-008 · SOFTWARE** Define deterministic `evidence.lock.json` canonicalization that pins
      accepted claim and module versions and requires a semantic diff for any constant change.
- [ ] **P2-009 · SOFTWARE** Define plain reviewable fixture directories, fixture metadata, expected
      semantic outputs, simulator-only replay, and explicit prohibition on hardware writes.
- [ ] **P2-010 · SOFTWARE** Define scaffold input/output schema, generator version, negotiated
      runtime API, module locks, evidence locks, and generated-file ownership markers.
- [ ] **P2-011 · SOFTWARE** Create the Device Plugin SDK with contracts, host adapter, templates,
      analyzers, generator support, fixture helpers, and TestKit.
- [ ] **P2-012 · SOFTWARE** Create the `wsgm-device` CLI command router with consistent structured
      output, exit codes, cancellation, explicit output directory, and no implicit live config
      access.
- [ ] **P2-013 · SOFTWARE** Create the Device Lab GUI shell with Hardware Owner and Plugin Developer
      modes sharing the same command/application layer as the CLI.
- [ ] **P2-014 · SOFTWARE** Create a disposable probe-host process whose typed profile-scoped APIs
      are unavailable in production DeviceHost IPC.
- [ ] **P2-015 · SOFTWARE** Add schema compatibility, deterministic serialization, migration,
      malformed/oversized input, and cross-version golden tests.

### P2.2 Stage 0 preflight and safety firewall

- [ ] **P2-016 · SOFTWARE** Implement `wsgm-device doctor` for environment, output path,
      architecture, required Windows APIs, permissions, runtime, and developer-mode health.
- [ ] **P2-017 · SOFTWARE** Require an explicit output or temporary directory and reject the live
      WSGM data directory, repository root, broad home paths, and unsafe overwrite targets.
- [ ] **P2-018 · SOFTWARE** Inspect current Device Integration, active DeviceHost generation, and
      per-resource ownership before any read session or trial.
- [ ] **P2-019 · SOFTWARE** Inspect AC/battery and thermal prerequisites and expose catalog-specific
      reasons a probe/trial is blocked.
- [ ] **P2-020 · SOFTWARE** Detect relevant OEM tools, services, drivers, providers, DLLs, helpers,
      tasks, available event sources and access, and existing resource conflicts without treating
      process presence alone as ownership.
- [ ] **P2-021 · SOFTWARE** Determine whether an observation or trial needs elevation or a reviewed
      helper before opening a device resource.
- [ ] **P2-022 · SOFTWARE** Define a distinct experiment lease and require orderly release by the
      production plugin before a direct Device Lab trial can acquire that resource.
- [ ] **P2-023 · SOFTWARE** When production DeviceHost owns a resource, request only a bounded
      read-only plugin diagnostic session; never stop, recreate, activate, or deactivate the
      process-long device cycle and never receive a raw transport.
- [ ] **P2-024 · SOFTWARE** Prevent Device Lab from silently disabling Device Integration, racing
      the production plugin, or treating a resource-name/process-name match as authorization.
- [ ] **P2-025 · SOFTWARE** Add tests proving imported captures, recipes, manifests, plugins,
      evidence locks, and acceptance manifests cannot authorize mutation.

### P2.3 Stage 1 automatic inventory

- [ ] **P2-026 · SOFTWARE** Implement normalized SMBIOS manufacturer, product, model, baseboard,
      revision, BIOS, EC, and firmware inventory.
- [ ] **P2-027 · SOFTWARE** Implement CPU/GPU family and exact identity inventory used only for
      matching catalog predicates.
- [ ] **P2-028 · SOFTWARE** Implement full PnP/container topology capture with interface arrival and
      removal generations.
- [ ] **P2-029 · SOFTWARE** Implement USB/HID VID, PID, MI, `bcdDevice`, usage, caps, descriptor
      hashes, report descriptors, input/output/feature report lengths, and endpoint roles.
- [ ] **P2-030 · SOFTWARE** Implement WMI namespace, class, instance, event, method-signature,
      qualifier, provider-version, and buffer-shape inventory without invoking unknown methods.
- [ ] **P2-031 · SOFTWARE** Implement COM endpoint and passive framing-candidate inventory without
      transmitting unknown serial data.
- [ ] **P2-032 · SOFTWARE** Implement WinRT/controller sensor inventory with device association,
      supported intervals, units, and current accessibility.
- [ ] **P2-033 · SOFTWARE** Implement XInput, DirectInput, SDL, Raw Input, and raw-HID views without
      starting a candidate plugin lifecycle.
- [ ] **P2-034 · SOFTWARE** Inventory native DLL name, version, architecture, hash, signer, and
      exports without loading or invoking unknown exports.
- [ ] **P2-035 · SOFTWARE** Inventory relevant processes, services, tasks, loaded providers,
      exclusive access, and demonstrated ownership conflicts.
- [ ] **P2-036 · SOFTWARE** Persist unique identifiers only in the private capture and replace them
      with stable session-local tokens in every shareable view.
- [ ] **P2-037 · SOFTWARE** Add disconnected, access-denied, multi-sensor, detachable, malformed
      descriptor, and topology-change inventory fixtures.

### P2.4 Stage 2 deterministic candidate matching

- [ ] **P2-038 · SOFTWARE** Normalize one inventory into independent transport, protocol, layout,
      policy, and capability observations.
- [ ] **P2-039 · SOFTWARE** Apply hard constraints before scoring and reject wrong report length,
      excluded firmware, absent required WMI method, CPU mismatch, descriptor mismatch, or missing
      endpoint.
- [ ] **P2-040 · SOFTWARE** Produce a human-readable explanation for every hard rejection and every
      positive/negative weighted observation.
- [ ] **P2-041 · SOFTWARE** Rank remaining modules independently by reusable unit and show precisely
      what each would reuse.
- [ ] **P2-042 · SOFTWARE** List device-specific values that must not be inherited from every
      candidate, especially ranges, offsets, tables, persistence, and recovery policy.
- [ ] **P2-043 · SOFTWARE** Select the next safest discriminating read probe for ambiguous
      candidates without opening a device handle during offline matching.
- [ ] **P2-044 · SOFTWARE** Make candidate output deterministic across input ordering and prove that
      a high rank may remain read-only/inconclusive.
- [ ] **P2-045 · SOFTWARE** Add negative matching cases for A1M `MS-1T41`, 7-inch A2VM `MS-1T42`,
      unrelated MSI PCs, spoofed VID/PID, wrong firmware, missing provider, and altered report
      shapes.

### P2.5 Stage 3 passive capture and correlation

- [ ] **P2-046 · SOFTWARE** Implement guided operator markers for button press/release, axes,
      six-face motion, attach/detach, and one externally performed OEM-utility setting change.
- [ ] **P2-047 · SOFTWARE** Build one QPC-aligned timeline for PnP, raw HID, device-identified Raw
      Input, low-level hook observation, WMI events/activity, XInput, DirectInput, SDL, sensors,
      serial, plugin operations, optional telemetry, and operator markers.
- [ ] **P2-048 · SOFTWARE** Segment clocks across suspend/resume and device generations; preserve
      loss, discontinuity, late-event, and access-denied evidence.
- [ ] **P2-049 · SOFTWARE** Implement baseline/action/release comparisons and correlation scoring
      that labels correlation as evidence rather than causality.
- [ ] **P2-050 · SOFTWARE** Keep raw observations alongside every derived interpretation and link a
      selected derived value back to its supporting raw event IDs.
- [ ] **P2-051 · SOFTWARE** Explicitly display platform limitations: user-mode HID output blindness,
      bounded/lossy USB ETW, incomplete WMI Activity, non-universal ETW, hook device ambiguity,
      unavailable generic ACPI/EC/SMBus/I2C, non-atomic snapshots, and non-causal timing.
- [ ] **P2-052 · SOFTWARE** Add tests for event loss, reordered lanes, clock reset, duplicated
      operator markers, unrelated keyboard activity, and false-correlation resistance.

### P2.6 Stage 4 reviewed read probes

- [ ] **P2-053 · SOFTWARE** Define named/versioned read-probe metadata with exact family/endpoint
      gate, hash, rate, timeout, expected response structure, cross-check, and evidence output.
- [ ] **P2-054 · SOFTWARE** Allow automatic execution only for WSGM-reviewed, locally installed,
      hash-pinned probe code matched to the exact family and endpoint.
- [ ] **P2-055 · SOFTWARE** Require an explicit Developer Mode action for signed-external,
      sideloaded, or developer probes even when they claim to be read-only.
- [ ] **P2-056 · SOFTWARE** Run each probe in a disposable bounded host; never activate an older
      plugin's normal lifecycle to test compatibility.
- [ ] **P2-057 · SOFTWARE** Validate response type, length, status, range, timing, repetitions, and
      independent cross-check; reject a merely nonempty response.
- [ ] **P2-058 · SOFTWARE** Implement safe version/status/current-value probe families for WMI,
      known HID feature reads, allowlisted EC reads, controller mode/profile, fan RPM, charge state,
      and native library version/exports.
- [ ] **P2-059 · SOFTWARE** Rate-limit and deadline-bound even cataloged getter/register reads;
      never infer that a read is safe merely because it does not request a setter.
- [ ] **P2-060 · SOFTWARE** Add probe-host crash, hang, access-denied, disconnect, malformed
      response, and hash-mismatch tests.

### P2.7 Stage 5 single bounded mutation path

- [ ] **P2-061 · DESTRUCTIVE-RISK** Implement `wsgm-device probe run <probe-id>` as the only Device
      Lab mutation entry point; reject unattended execution, CI, `--yes`, nesting, and bulk
      `test all`.
- [ ] **P2-062 · DESTRUCTIVE-RISK** Accept only a locally installed WSGM-reviewed trial ID and exact
      hash; never execute mutation instructions from imported files or a plugin package.
- [ ] **P2-063 · DESTRUCTIVE-RISK** Require local interactive review of exact board/firmware gates,
      actions, maximum writes, effect, lease, rollback, emergency action, timeout, retry, and
      cooldown.
- [ ] **P2-064 · DESTRUCTIVE-RISK** Expire authorization when preflight, device generation, module
      version, trial hash, target resource, or expected original state changes.
- [ ] **P2-065 · DESTRUCTIVE-RISK** Durably record original state before mutation and reconcile a
      process death between snapshot, write, observation, rollback, and verification.
- [ ] **P2-066 · DESTRUCTIVE-RISK** Acquire only the named resource and prohibit one trial from
      combining power, fan, rumble, RGB, controller mode, or another capability.
- [ ] **P2-067 · DESTRUCTIVE-RISK** Require independent observation/readback and classify execution,
      observation, mutation, and cleanup separately.
- [ ] **P2-068 · DESTRUCTIVE-RISK** Quarantine only the affected resource after failed/unverified
      restoration and block write-capable generation from that evidence.
- [ ] **P2-069 · DESTRUCTIVE-RISK** Implement the one-step temporary power-pair trial with exact
      pair restore and verified readback.
- [ ] **P2-070 · DESTRUCTIVE-RISK** Implement the one-fan current-or-higher safe-duty trial with RPM
      observation and firmware-mode restoration.
- [ ] **P2-071 · DESTRUCTIVE-RISK** Implement the low-amplitude rumble trial with an independent
      guaranteed zero-output emergency path.
- [ ] **P2-072 · DESTRUCTIVE-RISK** Implement the one-zone low-brightness RGB trial only for an
      exact profile already proven volatile.
- [ ] **P2-073 · DESTRUCTIVE-RISK** Implement controller-mode trial continuation across PnP
      re-enumeration and restore original mode/PID by container identity.
- [ ] **P2-074 · DESTRUCTIVE-RISK** Exclude EEPROM/ROM/UEFI writes, firmware flashing,
      provider/registry repair, driver restart/install, charge persistence, blind bus scans, unknown
      IOCTL/HID/ACPI/MMIO/MSR/raw port, physical memory, test certificates, and test-signing from
      all Device Lab mutation authority and `probe run` until a separately reviewed future pathway
      exists.
- [ ] **P2-075 · SOFTWARE** Add a simulator/fault harness for cancellation or death after every
      transactional step and prove the recorded result never overstates cleanup.

### P2.8 Stage 6 evidence assessment and Stage 7 scaffold generation

- [ ] **P2-076 · SOFTWARE** Derive only `Compatible`, `Incompatible`, `Inconclusive`, `Blocked`, or
      `Quarantined` from the independent assessment dimensions.
- [ ] **P2-077 · SOFTWARE** Preserve capability/resource independence so one failed probe does not
      invalidate unrelated implementations.
- [ ] **P2-078 · SOFTWARE** Implement `wsgm-device plugin scaffold --from <capture>` using only
      exact evidence-supported, version-pinned modules.
- [ ] **P2-079 · SOFTWARE** Generate `plugin.wsgm.json`, `evidence.lock.json`, README, bring-up
      report, exact detector, resource graph, module composition, capability registrations,
      lifecycle/per-resource lease skeleton, and module-required recovery-journal fields.
- [ ] **P2-080 · SOFTWARE** Generate positive/negative detection, unknown-firmware rejection,
      endpoint binding, capture replay, capability snapshot, unavailable-reason, and semantic
      command-intent tests.
- [ ] **P2-081 · SOFTWARE** Generate verified controller/button/sensor parsing only when the
      evidence qualifies; omit or explicitly mark unverified capabilities unavailable.
- [ ] **P2-082 · SOFTWARE** Never generate another model's power limits, fan conversion, RGB
      offsets, persistent writes, charge policy, firmware sync, unknown low-level access, untested
      rollback, or placeholder setters from similarity.
- [ ] **P2-083 · SOFTWARE** Mark generated output `Scaffolded`/Developer, not `Supported`, and
      ensure generation grants no package trust, privilege, hardware verification, or retail
      approval.
- [ ] **P2-084 · SOFTWARE** Keep `.g.cs` and handwritten files separate; regeneration must not
      overwrite developer code or silently accept changed golden output.
- [ ] **P2-085 · SOFTWARE** Require explicit semantic review for fixture changes and allow a
      firmware resweep to downgrade/disable a previously compatible module.
- [ ] **P2-086 · SOFTWARE** Compile every generated scaffold from a clean directory and run all
      offline fixtures immediately.

### P2.9 CLI completion, hardware validation, and packaging semantics

- [ ] **P2-087 · SOFTWARE** Implement `inventory`, `candidates`, `probe known --read-only`,
      `capture run`, `inspect`, `diff`, `correlate`, `fixture extract`, `validate offline`,
      `validate hardware`, and `pack` with the exact mutation boundaries in the design.
- [ ] **P2-088 · SOFTWARE** Make `capture run` execute only observe-only recipes and record the
      inert recipe in the bundle.
- [ ] **P2-089 · SOFTWARE** Make `validate hardware` accept only the target package under
      development plus a reviewed acceptance manifest; never activate an older candidate package.
- [ ] **P2-090 · SOFTWARE** When hardware evidence is missing, make validation emit named required
      trials and remain incomplete without invoking them.
- [ ] **P2-091 · SOFTWARE** Gate a later full plugin lifecycle test behind explicit WSGM Developer
      Mode and proof that every activation-time mutation has passed a named reviewed trial.
- [ ] **P2-092 · SOFTWARE** Show package identity, risk declarations, and verified activation
      operations before a developer enables full lifecycle testing.
- [ ] **P2-093 · SOFTWARE** Ensure `validate` and `pack` explicitly report that they do not grant
      trust, privilege, hardware verification, or retail support.
- [ ] **P2-094 · SOFTWARE** Implement deterministic package validation for schema, evidence,
      provenance, dependency, signer, generated/handwritten boundaries, and runtime API version.

### P2.10 Privacy, export, and unknown-gap workbench

- [ ] **P2-095 · SOFTWARE** Emit deterministic `.wsgmcap` bundles containing `manifest.json`,
      `recipe.json`, `inventory.json`, `streams/*.ndjson`, `analysis/*.ndjson`, `claims.json`,
      `blobs/*`, `redaction.json`, and `hashes.sha256`.
- [ ] **P2-096 · SOFTWARE** Redact user/computer names, SIDs, profile paths, command lines, serials,
      stable instance/container IDs, network/Bluetooth identifiers, Steam IDs, volume IDs, window
      titles, and unrelated keyboard input by default.
- [ ] **P2-097 · SOFTWARE** Exclude and mark quarantined any ETL/pcapng or opaque blob that cannot
      be safely rewritten; upload nothing automatically.
- [ ] **P2-098 · SOFTWARE** Add deterministic hash verification, corruption handling, size/decode
      budgets, and malicious-bundle tests.
- [ ] **P2-099 · SOFTWARE** Build PnP/endpoint explorer, multi-lane timeline, HID report matrix,
      bit/byte differential view, and WMI schema/method/event/activity browser.
- [ ] **P2-100 · SOFTWARE** Build serial framing/timing, raw-HID/XInput/DirectInput/SDL comparison,
      six-face IMU analysis, and baseline/action/release comparison.
- [ ] **P2-101 · SOFTWARE** Build integer, signed, endian, mask, scale, offset, counter, noise,
      checksum, and CRC hypothesis tools without turning a hypothesis into executable authority.
- [ ] **P2-102 · SOFTWARE** Add cross-device/cross-firmware comparison and privacy-reviewed,
      LLM-friendly summaries that link to evidence but cannot authorize a trial.

### P2.11 D1–D5 completion gates

- [ ] **P2-103 · RELEASE-GATE** Prove a new handheld receives complete safe inventory with no
      mutation, explained reusable candidates, and hard-mismatch rejection before ranking.
- [ ] **P2-104 · RELEASE-GATE** Prove only dedicated probe entry points execute, every mutation is
      explicit/bounded/observed/restored, and failed restoration quarantines exactly one resource.
- [ ] **P2-105 · RELEASE-GATE** Prove the generated project compiles, passes deterministic fixtures,
      fails closed for unknown firmware, and traces every constant/module to evidence and
      provenance.
- [ ] **P2-106 · RELEASE-GATE** Freeze SDK, catalog, capture, evidence, fixture, and scaffold
      compatibility policy and publish contributor templates.
- [ ] **P2-107 · RELEASE-GATE** Document source submission, hardware evidence, review, package
      trust, privileged review, and retail-promotion workflows.

### P2.12 Product workflows and boundary-completion gate

- [ ] **P2-108 · SOFTWARE** Let Device Lab recommend a glyph profile only from exact board/product
      evidence and label the recommendation unverified until full artwork, side, label, and
      logical-control validation passes.
- [ ] **P2-109 · SOFTWARE** Leave the generated plugin profile ID unset when exact visual/logical
      verification is absent and add fixtures proving family similarity or marketing name cannot
      auto-select artwork.
- [ ] **P2-110 · SOFTWARE** Implement the Hardware Owner GUI flow: select detected handheld, review
      observation scope, run the safe sweep, follow labeled prompts, review restoration/privacy, and
      export a sanitized capture without requiring SDK or protocol knowledge.
- [ ] **P2-111 · SOFTWARE** Implement the Plugin Developer GUI flow: compare candidates/evidence,
      inspect endpoints and analysis views, compose modules, scaffold/regenerate, replay fixtures,
      assess live acceptance, and validate packaging/trust readiness.
- [ ] **P2-112 · SOFTWARE** Enforce that normal WSGM detection and production DeviceHost consume
      only installed package device definitions, never the developer known-implementation catalog,
      probe metadata, candidate engine, or trial authority; add dependency and negative runtime
      tests.
- [ ] **P2-113 · SOFTWARE** Generate the accepted/rejected/unresolved candidate report plus explicit
      publisher, dependency, risk, evidence, and licensing metadata in every scaffold.
- [ ] **P2-114 · SOFTWARE** Retain selected catalog/module versions, evidence hashes, tested
      devices/firmware, notices, native-dependency hashes, expected signers, and required recovery
      fields in the generated/package review record.
- [ ] **P2-115 · SOFTWARE** Implement re-sweep/regeneration as an explicit diff workflow that
      updates inventory/evidence locks, re-runs compatibility, adds fixtures, can downgrade/disable
      modules, preserves handwritten files, and requires semantic acceptance of every golden change.
- [ ] **P2-116 · SOFTWARE** Store private working captures physically separately from sanitized
      exports and require an export preview/redaction report before a shareable bundle is written.
- [ ] **P2-117 · SOFTWARE** Reject any unreviewed-package manifest that requests WSGM-provisioned
      elevation, driver, service, task, helper, dependency, or extension of a reviewed
      helper/profile; keep independently installed privileged prerequisites outside package trust.
- [ ] **P2-118 · SOFTWARE** Keep cloud telemetry, automatic upload, remote hardware control, an
      automatic reverse-engineering oracle, and a universal hardware scripting language outside
      Device Lab, the SDK, DeviceHost, production device integration, and all plugin paths; prove no
      remote device-command channel exists.
- [ ] **P2-119 · SOFTWARE** When a generated definition selects a glyph profile, retain its profile
      ID, upstream revision, asset hashes, and visual-verification status in the private/shareable
      evidence bundle and `evidence.lock.json`; reference the centrally reviewed/attributed WSGM
      glyph catalog without copying it into the plugin package.
- [ ] **P2-120 · RELEASE-GATE** Pass both GUI workflows, runtime/catalog separation, regeneration,
      unreviewed-privilege, private/export separation, non-goal, and complete scaffold-record tests.

## P3 — MSI Claw A2VM M0 hardware characterization

Every task in this phase targets the exact reference board `MS-1T52`. Reference-source agreement is
not a substitute for an A2VM capture. No write-capable production module may proceed until its named
trial has verified both the effect and exact restoration.

### P3.1 Bootstrap the MSI catalog and exact scaffold

- [ ] **P3-001 · SOFTWARE** Register separately versioned candidates for MSI named-method WMI, Claw
      MCU framing, XInput source, DirectInput source/rumble, A2VM power policy, fan layout, RGB
      layout, Windows sensor source, MSI OEM events, and exact-device Win+G suppression.
- [ ] **P3-002 · SOFTWARE** Record provenance and current evidence grade for every method ID,
      address, report byte, range, transform, event code, and timeout candidate.
- [ ] **P3-003 · SOFTWARE** Hard-reject A1M `MS-1T41` power policy and treat `MS-1T42` as a separate
      definition even when MSI transports match.
- [ ] **P3-004 · SOFTWARE** Bootstrap only the reviewed MSI inventory and dedicated read-probe code
      required to assess candidates; do not activate HC or an older plugin.
- [ ] **P3-005 · SOFTWARE** Generate the initial `wsgm.device.msi.claw-8-a2vm` manifest, exact
      detector, resource graph, risk declarations, dependency record, evidence lock, and fail-closed
      scaffold.
- [ ] **P3-006 · SOFTWARE** Compile and replay the initial scaffold with all unverified write
      capabilities omitted or explicitly unavailable.

### P3.2 Exact identity, firmware, topology, and prerequisite capture

- [ ] **P3-007 · HARDWARE** Capture normalized SMBIOS manufacturer, product/board `MS-1T52`, board
      revision, marketing name, CPU/GPU, BIOS, EC, controller/MCU firmware, and controller
      `bcdDevice`.
- [ ] **P3-008 · HARDWARE** Capture the complete PnP/container graph and interface/report
      descriptors in every already-safe XInput and DirectInput state.
- [ ] **P3-009 · HARDWARE** Confirm VID `0x0DB0`, PID `0x1901` XInput, PID `0x1902` DirectInput,
      report lengths, usages, and stable physical container association.
- [ ] **P3-010 · HARDWARE** Determine the actual meaning and safe read-mode behavior of PIDs
      `0x1903` and `0x1904`; keep them diagnostics-only until resolved.
- [ ] **P3-011 · HARDWARE** Enumerate `root\WMI`, `MSI_ACPI`, `MSI_Event`, provider version, named
      methods, signatures, qualifiers, instance paths, and 32-byte buffer shapes.
- [ ] **P3-012 · HARDWARE** Verify the provider instance by board/interface version instead of
      hardcoding `ACPI\PNP0C14\0_0`.
- [ ] **P3-013 · HARDWARE** Capture the exact Windows gyro/accelerometer device IDs, PnP source,
      units, supported intervals, maximum stable rate, and resume identity.
- [ ] **P3-014 · HARDWARE** Capture relevant Handheld Companion, MSI Center M, service, driver,
      provider, HidHide, and controller-interface ownership state without terminating anything.
- [ ] **P3-015 · HARDWARE** Produce the first private capture, sanitized `.wsgmcap`, golden identity
      fixture, hashes, and evidence claims.

### P3.3 MSI WMI read-path characterization

- [ ] **P3-016 · HARDWARE** Validate fixed 32-byte input/output behavior and nonzero success status
      through the Windows named provider rather than assuming Linux ACPI behavior is identical.
- [ ] **P3-017 · HARDWARE** Probe and cross-check `Get_Data`, `Get_Fan`, `Get_AP`, and
      fan-temperature reads with the cataloged method/subfeature shapes only.
- [ ] **P3-018 · HARDWARE** Measure safe per-call deadlines, late responses, serialization needs,
      transient errors, provider hangs, and cancellation behavior.
- [ ] **P3-019 · HARDWARE** Confirm that provider absence/corruption returns a prerequisite failure
      without registry repair, DLL copying, ACPI restart, or Game Mode delay.
- [ ] **P3-020 · HARDWARE** Capture sanitized golden WMI responses for success, status failure,
      malformed length, timeout, access denial, and provider disappearance.

### P3.4 Power and scenario evidence

- [ ] **P3-021 · HARDWARE** Verify PL1/SPL read at address `0x50` and the complete little-endian
      32-bit watt field on `MS-1T52`.
- [ ] **P3-022 · HARDWARE** Verify PL2/SPPT read at address `0x51` and confirm PL3/FPPT address
      `0x52` remains unexposed/unwritten.
- [ ] **P3-023 · HARDWARE** Capture current PL1/PL2 and MSI scenario before/after one MSI Center M
      change to establish independently observable reference behavior.
- [ ] **P3-024 · HARDWARE** Map Comfort `0xC0`, Green `0xC1`, Eco `0xC2`, User `0xC3`, and Sport
      `0xC4` at scenario address `0xD2`; confirm that later Manual is absent on A2VM.
- [ ] **P3-025 · HARDWARE** Map every scenario's PL1/PL2 ceilings and readback on AC and battery.
- [ ] **P3-026 · HARDWARE** Determine exact write ordering when raising, lowering, and setting
      `PL1 == PL2`; confirm relationship enforcement and rejection behavior.
- [ ] **P3-027 · DESTRUCTIVE-RISK** Run the named one-step PL1/PL2 trial with exact pair snapshot,
      relationship-safe ordering, independent readback, exact restore, and verified cleanup.
- [ ] **P3-028 · HARDWARE** Confirm or revise planned desired profiles: Battery `8/9`, Balanced
      `17/18`, Performance `30/31`, and Performance + boost `30/37` watts.
- [ ] **P3-029 · HARDWARE** Produce fixtures for endpoints `8/30` PL1, `8/37` PL2, equality, invalid
      pairs, partial write, mismatched readback, AC/DC change, and rollback.

### P3.5 Fan layout, mode, safety, and telemetry evidence

- [ ] **P3-030 · HARDWARE** Read full left/right duty and temperature buffers before and after one
      MSI Center M curve change; resolve the six-versus-eight/11-to-8 discrepancy.
- [ ] **P3-031 · HARDWARE** Establish exact Windows byte offsets, units, endian, channel order, and
      read-modify-write preservation requirements for both fan channels.
- [ ] **P3-032 · HARDWARE** Resolve custom-enable conflict between `Set_AP` subfeature 1 byte 1 bit
      7 and HC's `Set_Data` address `0xD4` path.
- [ ] **P3-033 · HARDWARE** Verify or reject the candidate full-speed `0x98` bit 7 path and
      establish a separately observable full-speed state.
- [ ] **P3-034 · HARDWARE** Verify fan RPM channel order, big-endian divisor, candidate
      `RPM = 480000 / value`, and zero-as-stopped behavior.
- [ ] **P3-035 · HARDWARE** Confirm that fan curve temperature reads are curve points and identify a
      separate standard live-temperature source or explicitly omit live temperature.
- [ ] **P3-036 · HARDWARE** Capture the exact factory left/right tables instead of assuming the
      observed `0/0`, `50/40`, `60/49`, `70/58`, `80/67`, `88/75` curve is universal.
- [ ] **P3-037 · HARDWARE** Determine validated duty range and define the first-release
      hottest-point floor while preserving an explicit 100% emergency Full speed path.
- [ ] **P3-038 · DESTRUCTIVE-RISK** Run one named current-or-higher fan trial with independent RPM,
      table/flag readback, firmware-mode release, and exact two-channel restoration.
- [ ] **P3-039 · HARDWARE** Prove autonomous firmware safety or a plugin-owned watchdog/lease that
      restores automatic control if the host disappears.
- [ ] **P3-040 · HARDWARE** Produce fixtures for monotonic validation, one-channel failure,
      readback/status timeout, Custom/Full speed flags, stopped tach, host death, and restoration.

### P3.6 MCU framing, controller mode, and profile evidence

- [ ] **P3-041 · HARDWARE** Verify 64-byte MCU reports, output prefix `0F 00 00 3C`, output report
      ID `0x0F`, and input report ID `0x10`.
- [ ] **P3-042 · HARDWARE** Verify read/ACK `0x04/0x05`, generic ACK `0x06`, write profile `0x21`,
      ROM sync `0x22`, switch mode `0x24`, read mode/ACK `0x26/0x27`, and reset `0x28` semantics.
- [ ] **P3-043 · HARDWARE** Measure Windows profile ACK latency/retry behavior from the 25 ms
      starting hypothesis; capture late/orphan/stale ACKs.
- [ ] **P3-044 · HARDWARE** Confirm XInput payload `0x01`, DirectInput `0x02`, Desktop `0x04`, and
      exact PID/descriptor/container transitions.
- [ ] **P3-045 · HARDWARE** Verify switch/reset completion from old-interface disappearance and
      expected same-container return rather than an ordinary in-place ACK.
- [ ] **P3-046 · DESTRUCTIVE-RISK** Run the named controller-mode trial, continue across PnP
      re-enumeration, invalidate old handles, and restore original mode/PID with verified topology.
- [ ] **P3-047 · HARDWARE** Read current M1/M2 profile addresses/payloads for firmware `0x0211`,
      `0x0217`, and `0x0219` where available; resolve `0x0211` HC DInput `0x007A/0x011F` versus
      XInput `0x007B/0x0120`, `0x0217/0x0219` HC DInput `0x00BA/0x0163` versus XInput and Linux
      conflict `0x00BB/0x0164`, and payload `[01,00]` versus `[01,00,00,12,00]` as probe candidates
      only, never defaults.
- [ ] **P3-048 · HARDWARE** Determine whether M1/M2 repair is needed at all; keep it out of ordinary
      activation and record any qualifying repair as a later explicit persistent operation.
- [ ] **P3-049 · HARDWARE** Establish whether ROM sync is ever required for a real
      controller-profile change and how to verify it after re-enumeration without repeated writes.
- [ ] **P3-050 · HARDWARE** Produce request/ACK, late ACK, mode change, topology timeout, rollback,
      unknown firmware, and profile-read fixtures.

### P3.7 Physical controller and simultaneous-input evidence

- [ ] **P3-051 · HARDWARE** Verify DirectInput buttons 0–3 X/A/B/Y, 4–5 shoulders, 6–7 digital
      triggers, 8–9 View/Menu, 10–11 stick clicks, and 15–16 M1/M2.
- [ ] **P3-052 · HARDWARE** Verify left X/Y, right Z/Rotation Z, and trigger Rotation X/Y centers,
      extrema, dead zones, signs, scaling, and digital/analog duplication.
- [ ] **P3-053 · HARDWARE** Capture guide behavior, D-pad diagonals, multi-button rollover, report
      loss, and every safe simultaneous combination.
- [ ] **P3-054 · HARDWARE** Explicitly test stick movement plus M1/M2 and simultaneous rear buttons;
      do not accept HC parity given the reported concurrency limitation.
- [ ] **P3-055 · HARDWARE** Confirm the Claw has no touchpads/stick-touch and ensure canonical
      fields remain unsupported/neutral rather than synthesized.
- [ ] **P3-056 · HARDWARE** Produce neutral, press/release, axes, trigger, guide, rear-button,
      rollover, malformed, dropped, disconnect, and re-enumeration fixtures.

### P3.8 Rumble evidence

- [ ] **P3-057 · HARDWARE** Verify the 11-byte DirectInput output candidate
      `05 01 00 00 <weak> <strong> 00 00 00 00 00` without padding it to MCU length by assumption.
- [ ] **P3-058 · HARDWARE** Determine weak/strong motor order, scale, min/max, required HID API
      length/padding, rate tolerance, coalescing, and simultaneous input behavior.
- [ ] **P3-059 · DESTRUCTIVE-RISK** Run the named low-amplitude left/right/both rumble trial and
      verify automatic zero output on normal completion, cancellation, timeout, and host death.
- [ ] **P3-060 · HARDWARE** Verify XInput fallback vibration separately and reject the A1M-only 100
      ms binary workaround.
- [ ] **P3-061 · HARDWARE** Produce min/max/combined, high-rate, duplicate, stop, disconnect,
      target-removal, suspend, and output-fault fixtures.

### P3.9 Motion evidence

- [ ] **P3-062 · HARDWARE** Bind the exact gyrometer/accelerometer source instead of accepting an
      unrelated system-default sensor.
- [ ] **P3-063 · HARDWARE** Validate event-driven `ReadingChanged`, nearest supported interval,
      timestamp translation, rate stability, jitter, loss, sleep/resume, and disappearance.
- [ ] **P3-064 · HARDWARE** Validate gyro matrix `+X,+Y,-Z` by labeled physical rotations.
- [ ] **P3-065 · HARDWARE** Validate accelerometer mapping `X=-source X`, `Y=-source Z`,
      `Z=+source Y` by six-face orientation.
- [ ] **P3-066 · HARDWARE** Measure stationary gyro bias, accelerometer zero/scale, raw noise, and
      the minimum bounded smoothing justified as sensor correction.
- [ ] **P3-067 · HARDWARE** Define calibration invalidation for sensor identity and relevant
      firmware changes; capture raw diagnostic and corrected fixture streams.

### P3.10 OEM events and firmware chord evidence

- [ ] **P3-068 · HARDWARE** Capture WMI event low byte `41` for OEM1 and `88` for OEM2 across tap,
      hold, repeat, double press, boot, lock, sleep, and supported firmware.
- [ ] **P3-069 · HARDWARE** Capture exact Raw Input identity and make/break sequence from
      `ACPI\MSNB1001`, plus low-level-hook Win/G/Tab scan codes, flags, repeats, and timestamps.
- [ ] **P3-070 · HARDWARE** Determine whether the long OEM2 action emits Win+Tab or a distinct WMI
      event; do not implement Tab suppression before confirmation.
- [ ] **P3-071 · HARDWARE** Determine whether WMI `88` reliably precedes G-down across all supported
      BIOS, cold boot, sleep, and repeat cases; record whether a future correlation window is
      viable.
- [ ] **P3-072 · HARDWARE** Verify WMI/Raw Input event deduplication produces exactly one logical
      OEM2 event and never trusts the device-ambiguous hook as an action source.
- [ ] **P3-073 · HARDWARE** Threat-model and test medium-integrity hook behavior over elevated
      foreground applications; resolve `P0-006` with evidence.
- [ ] **P3-074 · DESTRUCTIVE-RISK** Run the named keyboard-suppression simulation with tagged input,
      every accepted-prefix count, emergency key-up reconciliation, and no stuck modifier.
- [ ] **P3-075 · HARDWARE** Validate that Win alone, Win+other keys, Ctrl/Alt/Shift interleavings,
      Alt+Tab, volume up/down/mute, OEM1, M1, and M2 remain unaffected.
- [ ] **P3-076 · HARDWARE** Produce WMI/Raw Input/hook/dedup fixtures with unrelated typed input
      removed and lifecycle state resets represented.

### P3.11 RGB and physical artwork evidence

- [ ] **P3-077 · HARDWARE** Verify exact RGB profile base `0x01FA` for firmware `0x0211` and
      `0x024A` for `0x0217`/`0x0219`; keep all other firmware read-only.
- [ ] **P3-078 · HARDWARE** Confirm physical order of all nine zones using one low-brightness zone
      at a time before assigning user-facing names.
- [ ] **P3-079 · HARDWARE** Verify profile framing from `0F 00 00 3C 21`, nine RGB triplets, bounded
      frame count, effect frames, brightness, and candidate speed encoding `20 - requestedSpeed`.
- [ ] **P3-080 · HARDWARE** Capture one MSI Center M lighting change and compare profile readback,
      ACK timing, persistence, re-enumeration, and power-loss behavior.
- [ ] **P3-081 · DESTRUCTIVE-RISK** Run the named one-zone volatile RGB trial only after exact
      firmware/profile proof; restore and verify the original state.
- [ ] **P3-082 · HARDWARE** Determine whether a safe volatile apply exists; if not, define one
      clearly labeled, coalesced persistent commit and its wear/recovery constraints.
- [ ] **P3-083 · HARDWARE** Verify Off, Solid, grouped ring/button colors, brightness 0–100, and
      each candidate Breathe/Chroma/Rainbow/Frostfire/speed mode before enabling it.
- [ ] **P3-084 · HARDWARE** Verify the upstream `msi.claw` full/left/right artwork, MSI Center/QAM
      sides, M1/M2 sides and orientation, and absent trackpads/additional paddles against the A2VM.
- [ ] **P3-085 · HARDWARE** Create and review `msi.claw-a2vm` instead of accepting a misleading
      near-match if any artwork or logical alias fails.
- [ ] **P3-086 · HARDWARE** Produce RGB fixtures and a signed visual-acceptance record with photos
      or equivalent evidence retained privately and sanitized claims exported.

### P3.12 Optional capability evidence

- [ ] **P3-087 · HARDWARE** Probe charge-threshold address `0xD7` only through a reviewed read
      recipe; keep charge persistence outside the required milestone.
- [ ] **P3-088 · HARDWARE** Validate exact read/write, AC/DC, MSI Center, suspend, and restoration
      before promoting charge or a standalone scenario selector.
- [ ] **P3-089 · SOFTWARE** Classify unready optional capabilities as later work without blocking
      TDP, fans, lighting, controller, rumble, motion, or OEM milestones.

### P3.13 M0 exit gate

- [ ] **P3-090 · RELEASE-GATE** Close all 12 named A2VM hardware questions with scoped evidence or
      explicitly disable the dependent capability/firmware combination.
- [ ] **P3-091 · RELEASE-GATE** Verify no planned M1–M4 write uses an unknown layout, nearest
      firmware, unverified range, unverified readback, or unverified restore path.
- [ ] **P3-092 · RELEASE-GATE** Regenerate the exact module composition, manifest capabilities,
      detector, evidence lock, fixtures, and scaffold from the qualified evidence.
- [ ] **P3-093 · RELEASE-GATE** Review the sanitized capture, privacy report, golden fixtures,
      provenance, rejected claims, and quarantined resources before production implementation
      begins.

### P3.14 Design Phase 0 cross-subsystem risk experiments

These experiments execute before production `P4`/`P5`, even though their hardened implementations
belong to later backlog phases.

- [ ] **P3-094 · SOFTWARE** Build a minimal isolated HIDMaestro adapter experiment behind the
      proposed WSGM backend interface using canonical fixture input; do not couple it to the Claw
      plugin ABI or treat it as production lifecycle code.
- [ ] **P3-095 · HARDWARE** Validate the Steam Deck Composite experiment in Steam, including
      recognition, standard/rear/motion fields, neutral unsupported fields, enumeration, output, and
      cleanup.
- [ ] **P3-096 · HARDWARE** Validate the Xbox 360 experiment through native XInput and
      representative older software, including enumeration/player slot, physical output return, and
      cleanup.
- [ ] **P3-097 · HARDWARE** Validate the DualShock 4 experiment through official PlayStation Remote
      Play and representative software, including supported motion/output and cleanup.
- [ ] **P3-098 · HARDWARE** Measure idle/active CPU, power, report latency/jitter/loss, memory,
      handles, and high-rate behavior for all three target paths on the Claw; record any unexplained
      composite overhead as a blocking risk.
- [ ] **P3-099 · LIVE-STEAM** Probe current native QAM performance components, stores, actions,
      Windows/device gates, controller navigation, and removal threshold before production patch
      work.
- [ ] **P3-100 · SOFTWARE** Consume the pinned/audited glyph baseline to prototype the semantic
      catalog and deterministic asset lock without loading upstream CSS at runtime.
- [ ] **P3-101 · HARDWARE** Compare the upstream `msi.claw` profile with A2VM full/left/right art,
      front-button sides, M1/M2 sides, labels, absent controls, and handheld scaling.
- [ ] **P3-102 · LIVE-STEAM** Prototype bounded bootstrap-delivered blob/data glyph assets plus
      unique positive probes for URL, structural, inline-SVG, and capability-hiding tiers in exact
      Steam Input routes.
- [ ] **P3-103 · SOFTWARE · LIVE-STEAM** Prototype RTSS discovery, state, frame-limit,
      overlay-level, readback, external-change, missing/restart behavior, and QAM bridge shape with
      no production persistence claim.
- [ ] **P3-104 · SOFTWARE** Feed experiment results, incompatibilities, performance measurements,
      numeric bounds, dependency/install findings, and fallback decisions back into `P0`/`P1`
      contracts before production implementations consume them.
- [ ] **P3-105 · RELEASE-GATE** Block production device/controller/Steam work until all mandatory
      design Phase 0 experiments have a reviewed result, explicit fallback, and no unresolved
      critical safety, feasibility, licensing, or performance risk.

## P4 — production device platform and DeviceHost

### P4.1 Authoritative device-cycle owner and process topology

- [ ] **P4-001 · SOFTWARE** Implement the authoritative per-user/interactive-session device
      coordinator selected in `P0-047`; prevent shell, Settings, preview, update, and stale
      processes from each starting their own host.
- [ ] **P4-002 · SOFTWARE** Add authenticated coordinator discovery and generation-aware client
      attachment for the shell overlay and standalone Settings ownership controls.
- [ ] **P4-003 · SOFTWARE** Define coordinator lifetime and intentional shutdown across
      shell-to-desktop transition, Settings close, WSGM update, session logoff, and the explicit
      master toggle.
- [ ] **P4-004 · SOFTWARE** Make `--overlay-test` use injected/simulated capability state only and
      make `--settings` mutate hardware ownership solely through the authoritative coordinator.
- [ ] **P4-005 · SOFTWARE** Detect and quarantine duplicate/stale WSGM coordinator or DeviceHost
      generations without killing unrelated external applications.
- [ ] **P4-006 · SOFTWARE** Add multi-process, stale-owner, crash-during-election, user-switch,
      duplicate-session, Settings-only, shell, and safe-preview tests.

### P4.2 Plugin discovery, package policy, and selection

- [ ] **P4-007 · SOFTWARE** Discover packages only from administrator/user locations approved by the
      frozen trust policy; use deterministic paths and reject traversal, links, mutable entry paths,
      and unsupported architectures.
- [ ] **P4-008 · SOFTWARE** Validate schema, runtime API range, package integrity,
      publisher/signature, dependency declarations, risk declarations, and trust state before
      detection or activation.
- [ ] **P4-009 · SOFTWARE** Perform read-only device-definition matching and apply the exact
      multi-device/package tie-breaking policy from `P0-010`.
- [ ] **P4-010 · SOFTWARE** Select at most one package for one logical handheld and show every
      rejected, ambiguous, disabled, quarantined, or missing-prerequisite candidate.
- [ ] **P4-011 · SOFTWARE** Block/defer package update, removal, replacement, and module change
      while that package's device cycle is active.
- [ ] **P4-012 · SOFTWARE** Implement atomic staged package update, previous-version rollback,
      signer continuity, compatibility check, and explicit activation only after the device cycle
      stops.
- [ ] **P4-013 · SOFTWARE** Add package tampering, revoked signer, downgrade, incompatible API,
      ambiguous match, missing dependency, and failed-start rollback tests.

### P4.3 DeviceHost executable and supervision

- [ ] **P4-014 · SOFTWARE** Build `WSGM.DeviceHost.exe` as a JIT-capable, unelevated, per-user,
      per-session host that loads exactly one validated package.
- [ ] **P4-015 · SOFTWARE** Establish deterministic DLL/native dependency resolution scoped to the
      package; remove current-directory and uncontrolled search-order ambiguity.
- [ ] **P4-016 · SOFTWARE** Start DeviceHost with the authenticated named-pipe handshake, package
      identity, runtime version, session identity, cancellation token, and fresh host generation.
- [ ] **P4-017 · SOFTWARE** Place DeviceHost in a kill-on-coordinator-close job and enforce the
      frozen memory, CPU, handle, process-child, and shutdown bounds.
- [ ] **P4-018 · SOFTWARE** Pass no WSGM secret beyond session authentication and no unrelated
      device handle, elevated token, raw broker, or helper authority.
- [ ] **P4-019 · SOFTWARE** Load the package, negotiate lifecycle/capability versions, root all
      cancellation/disposal owners, and reject a package that cannot complete the bounded handshake.
- [ ] **P4-020 · SOFTWARE** Supervise liveness, exit reason, timeout, protocol fault,
      CPU/memory/handle breach, and crash loop with bounded restart/backoff/quarantine.
- [ ] **P4-021 · SOFTWARE** Keep the same logical device cycle and desired state across an
      unexpected host restart; never label that restart a clean external-manager handoff.
- [ ] **P4-022 · SOFTWARE** Expose explicit manual retry/reset after quarantine without clearing
      unresolved hardware journal entries or silently re-enabling writes.
- [ ] **P4-023 · SOFTWARE** Add host crash/hang/overuse, child-process attempt, bad DLL search,
      handshake replay, malformed IPC, forced kill, restart, backoff, and quarantine tests.

### P4.4 Capability router, command routing, and state propagation

- [ ] **P4-024 · SOFTWARE** Implement the WSGM capability router from plugin descriptors/state to
      desired-state policy, overlay clients, Steam QAM clients, and diagnostics.
- [ ] **P4-025 · SOFTWARE** Validate descriptor/state/delta shapes, API versions, generations,
      sequence ordering, bounds, and semantic consistency before projection.
- [ ] **P4-026 · SOFTWARE** Store requested/desired values in WSGM, never treat the plugin's
      transient applied intent as the durable user preference, and never treat requested as
      observed.
- [ ] **P4-027 · SOFTWARE** Implement command serialization per capability/resource, cancellation,
      idempotency, timeout, late-result reconciliation, command progress, and truthful final status.
- [ ] **P4-028 · SOFTWARE** Expire observations by policy, mark affected state stale on disconnect
      or device-generation change, and disable commands until fresh state arrives.
- [ ] **P4-029 · SOFTWARE** Keep partial capability failures independent and preserve healthy
      power/fan/lighting/controller/motion/OEM/telemetry resources.
- [ ] **P4-030 · SOFTWARE** Publish UI-observable state on the UI dispatcher without performing
      blocking device/pipe work there.
- [ ] **P4-031 · SOFTWARE** Add state projection tests for stale ranges, changed descriptors,
      requested/applied/readback divergence, late success after timeout, rollback, and host
      replacement.

### P4.5 Desired-state, profiles, configuration, and migration

- [ ] **P4-032 · SOFTWARE** Add Device Integration and controller-management ownership settings to
      `AppConfig` with source-generated JSON, normalization, old-config defaults, and invalid-state
      repair.
- [ ] **P4-033 · SOFTWARE** Decide whether the disabled child preference is remembered for a future
      master re-enable and encode that result consistently in normalization and UI.
- [ ] **P4-034 · SOFTWARE** Add plugin selection/trust/update policy, controller global target,
      glyph selection mode, diagnostic level, and startup ownership defaults to the appropriate WSGM
      config; initialize the first-release managed target to Steam Deck Composite with an explicit
      unavailable-backend fallback.
- [ ] **P4-035 · SOFTWARE** Create a stable-device-identity desired-profile store for TDP policy,
      fan curve, lighting, OEM actions, calibration, and per-application overrides.
- [ ] **P4-036 · SOFTWARE** Keep observed hardware state, command progress, high-rate input,
      telemetry, and DeviceHost generations out of persisted `AppConfig`.
- [ ] **P4-037 · SOFTWARE** Debounce/coalesce profile writes and do not route every slider/color
      preview tick through `ConfigStore`'s whole-file cross-process transaction.
- [ ] **P4-038 · SOFTWARE** Implement the precedence frozen in `P0-012`, including return from a
      per-application override to global desired state.
- [ ] **P4-039 · SOFTWARE** Reconcile fresh resume/hotplug observations against persisted desired
      state once; do not poll-and-rewrite or replay startup mutations on mode transitions.
- [ ] **P4-040 · SOFTWARE** Add legacy-config, save failure, concurrent Settings/shell update,
      stale-open-window, crash, profile debounce, and per-application precedence tests.

### P4.6 Process-long lifecycle and resource ownership

- [ ] **P4-041 · SOFTWARE** Start discovery/activation asynchronously whenever the authoritative
      WSGM coordinator starts with Device Integration enabled, including Desktop Mode with Steam
      absent.
- [ ] **P4-042 · SOFTWARE** Publish `Detected`, `Passive`, `Activating`, `Active`, `Degraded`,
      `Suspended`, `Deactivating`, and `Disabled` truthfully from per-resource state.
- [ ] **P4-043 · SOFTWARE** Preserve the active host and valid resource handles across Desktop/Game
      transitions, games, Steam restart, QAM reconnect, and individual capability degradation.
- [ ] **P4-044 · SOFTWARE** Feed only session-state/profile-selection notifications across mode
      transitions; never use them to reset hardware or recreate the host/virtual target.
- [ ] **P4-045 · SOFTWARE** Implement suspend/lock quiescence: reject/stop new writes, cancel calls,
      stop output, quiesce input/IMU, reset hooks, close volatile handles, and meet the deadline.
- [ ] **P4-046 · SOFTWARE** Implement resume/unlock rediscovery by container, fresh
      identity/firmware/provider gates, new generation, state reads, and one desired-state
      reconciliation.
- [ ] **P4-047 · SOFTWARE** Implement hotplug/re-enumeration with exact endpoint invalidation and no
      fixed sleeps; await concrete PnP/interface/ACK events under bounded deadlines.
- [ ] **P4-048 · SOFTWARE** Detect resource-specific conflicts from actual access/writes/ownership
      and keep unrelated capabilities active; process/service presence remains diagnostic only.
- [ ] **P4-049 · SOFTWARE** Refuse runtime dependency repair, provider copying, registry edits, ACPI
      restart, driver/certificate install, service kill, or arbitrary installer execution.
- [ ] **P4-050 · SOFTWARE** Add repeated Desktop/Game, Steam absent/restart, game start/stop,
      partial failure, hotplug, suspend, lock, user switch, and dependency-loss tests.

### P4.7 Controller-only and full deactivation transactions

This phase owns the coordinator transaction against stable controller/HidHide/plugin contracts and
deterministic fakes. `P6` supplies the concrete HIDMaestro/HidHide/input integration and the live
end-to-end proof; it must not implement a second competing coordinator state machine.

- [ ] **P4-051 · SOFTWARE** On full disable, reject new semantic commands and establish the selected
      keyboard/touch and SDL/Steam-lease fallback for any open WSGM surface.
- [ ] **P4-052 · SOFTWARE** Neutralize the virtual target while retaining WSGM-owned HidHide entries
      before asking the plugin to release physical acquisition.
- [ ] **P4-053 · SOFTWARE** Require plugin acknowledgment of stopped input/output readers, closed
      handles, restored original controller mode, and verified topology before WSGM input cleanup.
- [ ] **P4-054 · SOFTWARE** Require plugin restoration/release of every other owned hardware
      resource, closed transports/subscriptions, and clean or preserved recovery journal.
- [ ] **P4-055 · SOFTWARE** After acknowledgment, remove the virtual target and only WSGM-owned
      HidHide entries, then dispose DeviceHost and end the device-cycle/resource ownership; keep the
      WSGM coordinator available for same-run re-enable unless WSGM itself is exiting.
- [ ] **P4-056 · SOFTWARE** On full-deactivation timeout, honor the master toggle: preserve
      keyboard/touch access, remove the virtual target and only WSGM-owned HidHide deltas, dispose
      DeviceHost/end the cycle, preserve the journal/recovery instructions, and report
      unverified—not factory/clean—restoration.
- [ ] **P4-057 · SOFTWARE** If fallback cannot be established during an open surface, keep
      keyboard/touch usable, warn, prevent held-input leakage, and still honor full disable.
- [ ] **P4-058 · SOFTWARE** Implement controller-management-only handoff with the same controller
      ordering while leaving DeviceHost and every permitted non-controller resource alive; on
      timeout, remove target/owned HidHide, quarantine only controller ownership, and preserve
      recovery evidence.
- [ ] **P4-059 · SOFTWARE** Keep HidHide untouched when controller management is off and preserve
      external HidHide application/device entries entry-for-entry in every rollback.
- [ ] **P4-060 · SOFTWARE** Add cancellation/failure injection after every deactivation step,
      fallback failure, plugin hang, topology mismatch, host death, and Settings close test.

### P4.8 Recovery journal and startup reconciliation

- [ ] **P4-061 · SOFTWARE** Atomically journal before and after every ownership-changing hardware
      operation, including exact original snapshot and evidence-qualified recovery method.
- [ ] **P4-062 · SOFTWARE** On startup/host restart, compare journal identity/firmware/generation to
      current observations before any recovery action.
- [ ] **P4-063 · SOFTWARE** Automatically restore only an evidence-qualified, currently safe,
      independently verifiable operation; otherwise offer a clear manual recovery path.
- [ ] **P4-064 · SOFTWARE** Never blindly retry an `Indeterminate` persistent operation or
      substitute a hardcoded factory value when the original snapshot is absent/unreadable.
- [ ] **P4-065 · SOFTWARE** Surface unresolved journal items, last attempted recovery, current live
      comparison, and safe next action in Device diagnostics.
- [ ] **P4-066 · SOFTWARE** Add corrupt/truncated/old journal, identity mismatch, partial atomic
      write, process death at every mutation step, and downgrade tests.

### P4.9 Provisional first-party surfaces and diagnostics

- [ ] **P4-067 · SOFTWARE** Build a modular provisional Device surface for design Phase 1 bring-up
      that is available in Desktop and Game Mode and can be adopted, not rewritten, by design Phase
      4 navigation.
- [ ] **P4-068 · SOFTWARE** Render capability controls from WSGM-owned semantic schemas and
      hide/disable unavailable operations without loading plugin UI code.
- [ ] **P4-069 · SOFTWARE** Keep the Device destination absent when Device Integration is off and
      preserve the lightweight existing overlay behavior.
- [ ] **P4-070 · SOFTWARE** Add per-resource identity, owner, capability health, desired/observed
      state, conflict, dependency, freshness, host/device generation, quarantine, and recovery
      status.
- [ ] **P4-071 · SOFTWARE** Add versioned sanitized diagnostic capture/export and never show raw
      unique paths, secrets, memory, or high-rate samples by default.
- [ ] **P4-072 · SOFTWARE** Extend remote-device logs with stable device-cycle, host, resource,
      command, transition, and fallback lines while preserving existing input/Steam/Explorer
      diagnostics.

### P4.10 Intermediate device-platform contract gate

- [ ] **P4-073 · RELEASE-GATE** With Device Integration off, prove no host, hook, device handle,
      virtual target, HidHide change, fixed poll, optional dependency requirement, or hardware write
      exists.
- [ ] **P4-074 · RELEASE-GATE** With integration on and Steam absent, prove one authoritative host
      starts asynchronously and the provisional Device surface becomes usable per capability.
- [ ] **P4-075 · RELEASE-GATE** Prove repeated Desktop/Game and Steam transitions retain one device
      cycle/host generation and do not replay startup hardware writes.
- [ ] **P4-076 · RELEASE-GATE** Prove conflict, capability failure, host crash, restart, quarantine,
      suspend, resume, hotplug, and timeout preserve the shell and unaffected resources.
- [ ] **P4-077 · RELEASE-GATE** Prove with contract/fake fault injection that controller-only and
      full handoff preserve external state and end in verified restoration or an honestly recorded
      unverified handoff; this intermediate gate does not close the device platform until `P4-094`
      and the concrete/live proofs in `P6`/`P10` pass.
- [ ] **P4-078 · SOFTWARE** Run signed-external, sideloaded, and Developer packages only at ordinary
      user integrity and never provision elevation, driver, service, task, helper, dependency, or
      privileged installation on their behalf.
- [ ] **P4-079 · SOFTWARE** Prevent an unreviewed package from selecting, extending, parameterizing,
      or inheriting authority from a WSGM-reviewed helper, privileged profile, or dependency grant;
      externally privileged prerequisites remain independently installed and reviewed.
- [ ] **P4-080 · RELEASE-GATE** Add manifest, install, launch, runtime, helper-confusion,
      trust-promotion, and downgrade tests proving unreviewed packages cannot gain reviewed
      privilege.

### P4.11 WSGM-owned OEM action router

- [ ] **P4-081 · SOFTWARE** Implement one WSGM-owned router from canonical logical OEM events to the
      frozen allowlisted WSGM/system action vocabulary; plugins publish events and never dispatch or
      configure user actions themselves.
- [ ] **P4-082 · SOFTWARE** Provide verified defaults of Control Center/OEM1 to the WSGM overlay and
      Quick Access/OEM2 to Steam's native QAM, with M1/M2 routing governed by target capability and
      the OEM-button exception; each accepted OEM1 edge opens or closes the overlay exactly once in
      Desktop/Game Mode with correct focus/local-capture acquisition and release.
- [ ] **P4-083 · SOFTWARE** Persist per-device OEM assignments as desired profile state, validate
      control type/action compatibility, and reject standard-controller remapping, macros, scripts,
      executables, arbitrary keys, and generic Steam navigation.
- [ ] **P4-084 · SOFTWARE** Deduplicate by logical event ID, physical source generation, edge, and
      action generation; the low-level keyboard suppressor may corroborate/suppress a chord but can
      never originate an OEM event or action.
- [ ] **P4-085 · SOFTWARE** Dispatch actions asynchronously through typed allowlisted services and
      isolate timeout/unavailable/failure from the plugin, input, device lifecycle, and other OEM
      controls.
- [ ] **P4-086 · SOFTWARE** Implement the unavailable/Desktop/Steam-starting/QAM-incompatible OEM2
      behavior frozen in `P0-051` without queue replay or fallback producing a second action.
- [ ] **P4-087 · SOFTWARE** Reset held/repeat/dedup state on activation, source replacement,
      suspend, lock, profile change, controller handoff, host fault, and WSGM shutdown.
- [ ] **P4-088 · RELEASE-GATE** Add defaults/configuration, duplicate-source, held/repeat, stale
      generation, action failure, unavailable QAM, lifecycle reset, OEM1 open-once/close-once,
      focus/capture, Desktop/Game, no-gameplay-leak, and exact-one-dispatch tests.

### P4.12 Generic-device conformance and same-run re-enable

- [ ] **P4-089 · SOFTWARE** Add a second synthetic non-MSI plugin/device with a materially different
      capability set and prove coordinator, router, desired-state policy, diagnostics, and
      provisional UI operate entirely through semantic contracts.
- [ ] **P4-090 · SOFTWARE** Add static dependency/constant checks that WSGM core, coordinator,
      router, and UI contain no `MS-1T52`, MSI method, HID report, firmware-address, fan-table, or
      Claw policy branches.
- [ ] **P4-091 · SOFTWARE** Implement Device Integration off-to-on in the same WSGM run as a fresh
      asynchronous device cycle with new generation, package/dependency/identity/journal recheck,
      and no stale desired/observed state reuse.
- [ ] **P4-092 · SOFTWARE** Implement controller management off-to-on while DeviceHost and permitted
      non-controller resources stay alive, using the single P4/P6 activation transaction and fresh
      physical/target/HidHide generations.
- [ ] **P4-093 · SOFTWARE** Define and enforce re-enable blocking/recovery after unverified handoff,
      unresolved journal, quarantined resource, incompatible package update, or missing fallback.
- [ ] **P4-094 · RELEASE-GATE** Pass repeated off/on/off/on, concurrent Settings clients, late
      prior-cycle completion, unresolved recovery, partial resource, and synthetic-plugin
      conformance tests.

## P5 — production MSI Claw 8 AI+ A2VM plugin

Production work in this phase consumes the evidence locks from `P3`. A capability stays absent until
its exact identity, range, transaction, readback, and restoration evidence meet the required grade.

### P5.1 Package, exact detection, and capability gates

- [ ] **P5-001 · SOFTWARE** Create the A2VM plugin from the deterministic Device Lab scaffold and
      preserve generated, handwritten, evidence, fixture, and packaged files as separate layers.
- [ ] **P5-002 · SOFTWARE** Pin every reused implementation module and catalog entry by stable ID,
      version, evidence grade, license/provenance record, and compatible firmware range.
- [ ] **P5-003 · SOFTWARE** Implement base positive detection from exact normalized MSI identity and
      `MS-1T52`; keep board/BIOS/EC/MCU, controller topology, provider, and sensor observations as
      independent secondary capability gates rather than prerequisites for detecting the handheld.
- [ ] **P5-004 · SOFTWARE** Add positive base-detection fixtures with controller
      disabled/offline/re-enumerating, provider absent, and sensor absent, plus negative fixtures
      for similar models; gate altered endpoint/report/provider/firmware cases only at their
      affected capabilities.
- [ ] **P5-005 · SOFTWARE** Re-evaluate identity after resume, controller-mode re-enumeration,
      hotplug, provider restart, and host restart; increment the device generation before accepting
      state.
- [ ] **P5-006 · SOFTWARE** Gate power, fan, RGB, controller, rumble, motion, and OEM capabilities
      independently so one missing prerequisite never disables unrelated verified resources.
- [ ] **P5-007 · SOFTWARE** On unknown controller `bcdDevice`, retain base detection, safe standard
      input/read-only diagnostics, and independently verified WMI power/fan capabilities while
      withholding controller-profile/RGB address and other firmware-layout-dependent writes; gate
      unknown BIOS/EC/provider evidence separately per affected WMI capability.
- [ ] **P5-008 · SOFTWARE** Declare all dependencies and conflicts in package metadata without
      installing, repairing, copying, registering, starting, stopping, or killing any of them at
      runtime.
- [ ] **P5-009 · SOFTWARE** Implement sanitized package diagnostics for matched evidence, rejected
      gates, provider health, firmware, endpoint generation, and per-capability availability.
- [ ] **P5-010 · SOFTWARE** Replay the complete M0 evidence bundle through the production plugin and
      prove generated constants agree with the evidence lock rather than duplicated handwritten
      values.
- [ ] **P5-011 · RELEASE-GATE** Require exact-device positive/negative fixtures and a fresh
      read-only hardware report before any production mutation capability may be enabled.

### P5.2 Serialized MSI WMI transport

- [ ] **P5-012 · SOFTWARE** Implement one plugin-owned FIFO owning all MSI WMI reads and writes for
      the verified provider namespace/class/method and exact 32-byte input/output contract.
- [ ] **P5-013 · SOFTWARE** Validate provider status, returned length, method ID, evidence-required
      command echo only where that method actually carries one, and capability-specific payload
      bounds before publishing a result.
- [ ] **P5-014 · SOFTWARE** Add cancellation, one-operation deadlines, bounded evidence-justified
      retries, late-result rejection, and a resource-specific circuit breaker.
- [ ] **P5-015 · SOFTWARE** Keep the WMI serializer off the UI and high-rate input paths and expose
      queue depth, wait time, call time, timeout, retry, and breaker state in diagnostics.
- [ ] **P5-016 · SOFTWARE** Prevent a queued stale-generation or canceled write from reaching the
      provider after suspend, deactivation, firmware change, or resource ownership loss.
- [ ] **P5-017 · SOFTWARE** Distinguish transport failure, provider rejection, malformed response,
      applied-unverified, readback mismatch, rollback failure, and indeterminate outcome.
- [ ] **P5-018 · SOFTWARE** Implement test doubles for success, delay, timeout, cancellation,
      corrupt status/length, reordered completion, provider disappearance, and host termination.
- [ ] **P5-019 · HARDWARE** Verify Windows provider behavior independently for every M0 read method;
      do not infer its framing or status behavior solely from Linux documentation.
- [ ] **P5-020 · HARDWARE · DESTRUCTIVE-RISK** Re-run each approved one-write WMI trial through the
      production transport, verify readback and rollback, and attach the resulting evidence ledger.
- [ ] **P5-021 · SOFTWARE** Quarantine only the affected WMI resource after breaker trip or
      indeterminate mutation and require fresh identity plus safe reconciliation before writes
      resume.
- [ ] **P5-022 · SOFTWARE** Dispose WMI subscriptions and provider objects within the lifecycle
      deadline and prove no call or callback survives host deactivation.
- [ ] **P5-023 · RELEASE-GATE** Pass malformed-response, timeout, cancellation, circuit-breaker,
      generation, partial-write, rollback, and repeated-open/close tests.

### P5.3 M1 lifecycle, ownership, and coexistence

- [ ] **P5-024 · SOFTWARE** Implement plugin lifecycle states and per-resource ownership using the
      shared state machine; never substitute a plugin-private lifecycle vocabulary on IPC.
- [ ] **P5-025 · SOFTWARE** Keep activation read-only until exact device, firmware, dependency,
      endpoint, conflict, and evidence gates have completed for each resource.
- [ ] **P5-026 · SOFTWARE** Snapshot original hardware values immediately before the first owned
      mutation and journal their evidence-qualified restoration operations atomically.
- [ ] **P5-027 · SOFTWARE** Acquire only the resource required by an enabled capability and leave
      power, fans, RGB, controller, sensors, and OEM events independently passive or active.
- [ ] **P5-028 · SOFTWARE** Detect actual handle/write/subscription conflicts and expose likely MSI
      Center M or Handheld Companion involvement as a diagnostic hint, not proof of ownership.
- [ ] **P5-029 · SOFTWARE** Never stop, kill, reconfigure, rewrite, or normalize either external
      manager; stay passive or degrade only the genuinely contested resource.
- [ ] **P5-030 · SOFTWARE** On suspend/lock, stop new writes, drain/cancel transport work,
      neutralize output, release volatile handles/subscriptions, reset the hook, and acknowledge
      within deadline.
- [ ] **P5-031 · SOFTWARE** On resume/unlock, rediscover endpoints and firmware, create a fresh
      generation, reacquire resources, read state, and reconcile desired state once.
- [ ] **P5-032 · SOFTWARE** On full deactivation, stop input/output/motion/OEM sources, restore the
      exact captured state in safe order, verify it, close transports, and acknowledge each
      resource.
- [ ] **P5-033 · SOFTWARE** On controller-only deactivation, restore physical controller mode and
      release its acquisition while keeping DeviceHost, power/fans/RGB, motion acquisition, the
      front OEM event path, and suppression alive; apply `P0-004` only to M1/M2/rear-source
      availability.
- [ ] **P5-034 · SOFTWARE** Preserve unresolved journal data and return an honest unverified handoff
      when any restoration cannot be proven; never claim factory state from an assumed constant.
- [ ] **P5-035 · RELEASE-GATE** Pass 100 activation/deactivation cycles, 100 suspend/resume cycles,
      conflict injection, forced-host termination, and external-manager handoff on the reference
      unit.

### P5.4 OEM events and firmware-chord suppression

- [ ] **P5-036 · SOFTWARE** Bind OEM WMI events and physical Raw Input/DirectInput sources to the
      exact A2VM container and publish canonical `OEM1`, `OEM2`, `M1`, and `M2` events separately.
- [ ] **P5-037 · SOFTWARE** Timestamp sources on one monotonic clock and deduplicate one physical
      action observed through multiple transports without merging legitimate repeated presses.
- [ ] **P5-038 · SOFTWARE** Keep OEM event production independent from WSGM's allowlisted action
      mapping; reject any plugin request for a generic macro, executable, script, or key sequence.
- [ ] **P5-039 · SOFTWARE** Publish required stable ID, bounded display-name metadata, type, source,
      and verified physical semantics for each OEM control; leave defaults, user mapping, and action
      dispatch exclusively to the WSGM router in `P4.11`.
- [ ] **P5-040 · SOFTWARE** Add the plugin-scoped low-level keyboard state machine for the verified
      firmware-generated right-front `Win+G` chord as suppression-only, only after the exact logical
      OEM2 WMI/Raw source is healthy and suppression ownership is uncontested.
- [ ] **P5-041 · SOFTWARE** Track preexisting physical modifier/key state and suppress only the
      exact system-wide key sequence without claiming source identity or consuming Win alone,
      volume, Alt+Tab, or other Win shortcuts.
- [ ] **P5-042 · SOFTWARE** Tag injected fallback/release events, ignore the plugin's own injection,
      handle partial `SendInput`, and emit exact releases without leaving a stuck key.
- [ ] **P5-043 · SOFTWARE** Reset the suppressor on activation, deactivation, suspend, lock,
      source-generation replacement, focus anomalies, host fault, and process exit.
- [ ] **P5-044 · SOFTWARE** Emit exactly one logical OEM2 event for a confirmed WMI/Raw physical
      press; the suppressor hook can never originate an OEM event/action, and only the WSGM router
      may turn the event into one QAM or fallback action.
- [ ] **P5-045 · SOFTWARE** Keep the version-one limitation explicit: the physical `Win+G` chord is
      suppressed system-wide while owned; do not claim source-specific kernel filtering.
- [ ] **P5-046 · HARDWARE** Capture press/release timing, autorepeat, rollover, M1/M2 behavior, and
      competing Win-key combinations under Desktop, Game Mode, UAC-safe, and supported elevated
      cases.
- [ ] **P5-047 · HARDWARE** Add `Win+Tab` suppression only if a fresh evidence lock proves that
      exact firmware behavior and its isolated state machine passes the same safety matrix.
- [ ] **P5-048 · SOFTWARE** Add deterministic state-machine tests for every key order, lost release,
      repeat, partial injection, duplicate source, generation reset, and lifecycle transition.
- [ ] **P5-049 · RELEASE-GATE** Prove OEM1/OEM2/M1/M2 correctness, no unrelated-key regression, no
      stuck modifiers, exactly one canonical event per physical action, and the elevated behavior
      frozen in `P0-006`; QAM/overlay action acceptance belongs to `P4`/`P7`.

### P5.5 M2 power, scenario, and TDP policy

- [ ] **P5-050 · SOFTWARE** Implement separate semantic PL1 and PL2 descriptors from the verified
      A2VM ranges, steps, relationship, AC/DC availability, firmware gate, and persistence evidence.
- [ ] **P5-051 · SOFTWARE** Model MSI shift/scenario state explicitly and prevent an unrelated
      scenario transition from silently rewriting user-selected power or fan policy.
- [ ] **P5-052 · SOFTWARE** Validate every request against current descriptor, AC/DC state,
      thermal/scenario constraints, PL1/PL2 relationship, and current device generation inside the
      plugin.
- [ ] **P5-053 · SOFTWARE** Capture current PL1, PL2, and linked scenario state immediately before
      an apply transaction; write in the evidence-approved order and read back every step.
- [ ] **P5-054 · SOFTWARE** Roll back the exact captured values in safe reverse order on rejection,
      mismatch, cancellation, timeout, disconnect, or partial success.
- [ ] **P5-055 · SOFTWARE** Return applied-verified only when all relevant readbacks match; report
      rollback and indeterminate state truthfully when they do not.
- [ ] **P5-056 · SOFTWARE** Implement only the final P3-approved presets—candidate Battery `8/9`,
      Balanced `17/18`, Performance `30/31`, and Performance+boost `30/37`—over the same
      PL1/PL2/scenario command path; do not invent an `Off` hardware profile.
- [ ] **P5-057 · SOFTWARE** Apply AC/DC and per-application profile transitions once through the
      shared precedence engine, with debouncing and no continuous hardware rewrite loop.
- [ ] **P5-058 · SOFTWARE** On resume or external change, refresh observed power state and reconcile
      policy without treating device state as the persisted desired value.
- [ ] **P5-059 · SOFTWARE** Add boundary, relationship, ordering, AC/DC change, partial-write,
      rollback, stale generation, and external-change fixture tests.
- [ ] **P5-060 · HARDWARE · DESTRUCTIVE-RISK** Validate every supported PL1/PL2 boundary, ordering,
      readback, rollback, AC/DC transition, and scenario interaction using approved trials.
- [ ] **P5-061 · HARDWARE** Run sustained safe-load checks for each retail profile and record
      temperature, throttling, stability, power, and restoration evidence without claiming an
      overclock.
- [ ] **P5-062 · RELEASE-GATE** Ship no power setter until the full supported firmware/range/profile
      matrix is verified and failed/interrupted transactions restore or quarantine safely.

### P5.6 M2 fan control and telemetry

- [ ] **P5-063 · SOFTWARE** Freeze the verified fan-table layout and custom/full-speed command path;
      do not implement either six- or eight-point proposals until `P3` resolves the conflict.
- [ ] **P5-064 · SOFTWARE** Publish independent left/right fan capabilities with immutable curve
      layout, units, bounds, monotonic constraints, mode, availability, and release behavior.
- [ ] **P5-065 · SOFTWARE** Keep curve control temperature fields distinct from measured
      CPU/GPU/board telemetry and label unavailable live sensors as unknown rather than reusing
      curve points.
- [ ] **P5-066 · SOFTWARE** Convert verified tachometer data to RPM with documented units and
      freshness, plausibility, stale, disconnected, and zero-speed semantics.
- [ ] **P5-067 · SOFTWARE** Validate point count, evidence-dependent editable/fixed temperature
      axes, monotonic temperatures and duties, duty bounds, safety minimums, left/right
      completeness, generation, and current mode before applying a curve.
- [ ] **P5-068 · SOFTWARE** Snapshot both fan channels and mode, then apply the two-channel change
      as one user-visible transaction with ordered writes and readback after each step.
- [ ] **P5-069 · SOFTWARE** On any failure, roll back both channels and prior mode; never leave a
      half-applied left/right pair reported as success.
- [ ] **P5-070 · SOFTWARE** Implement firmware/default release as an explicit verified operation and
      distinguish it from a guessed default curve.
- [ ] **P5-071 · SOFTWARE** Implement full-speed as a clearly indicated bounded mode with explicit
      exit/restore behavior, not an unlabelled curve mutation.
- [ ] **P5-072 · SOFTWARE** Coalesce UI previews, apply only on the explicit semantic command, let
      firmware execute the curve with no high-frequency software PWM loop, and reduce/stop WMI
      telemetry when overlay, QAM, diagnostics, or capture has no consumer.
- [ ] **P5-073 · SOFTWARE** Reject unsafe or stale curve writes after AC/DC, scenario, firmware,
      ownership, or generation changes and refresh the descriptor/state first.
- [ ] **P5-074 · SOFTWARE** Add mode/curve/RPM fixture tests for bounds, monotonicity, two-channel
      atomicity, partial write, rollback, release, full-speed exit, stale telemetry, and
      cancellation.
- [ ] **P5-075 · HARDWARE · DESTRUCTIVE-RISK** Validate each curve point/channel/mode with approved
      trials, physical fan response, tachometer readback, cooldown, and exact restoration.
- [ ] **P5-076 · HARDWARE** Run failure injection and a 100-cycle fan-curve/resume soak while
      recording thermals, RPM, timeouts, WMI queueing, rollback, and post-test firmware state.
- [ ] **P5-077 · SOFTWARE** Surface fan safety, current owner, desired/applied/readback curve, mode,
      RPM freshness, rollback, and quarantine independently per channel.
- [ ] **P5-078 · RELEASE-GATE** Require verified two-fan atomic behavior and safe firmware release
      on every supported firmware before enabling the retail fan editor.

### P5.7 MCU/vendor HID transport and controller-mode changes

- [ ] **P5-079 · SOFTWARE** Implement one serialized plugin-owned MCU/vendor HID transport using the
      exact verified 64-byte request, response, report IDs, endpoint, and ACK contract.
- [ ] **P5-080 · SOFTWARE** Match responses by response type, host transaction ordering, device
      generation, report shape, and only an address field that response type actually carries; do
      not invent a device sequence field and reject malformed, late, orphaned, or cross-generation
      data.
- [ ] **P5-081 · SOFTWARE** Drain stale input before each generic ACK operation, allow exactly one
      command in flight, verify completion by the relevant profile/state readback, and add
      cancellation, deadline, evidence-bounded retry, diagnostics, and an independent circuit
      breaker.
- [ ] **P5-082 · SOFTWARE** Invalidate all controller/MCU handles when a mode/reset command begins
      and wait for concrete PnP disappearance/reappearance rather than a fixed sleep.
- [ ] **P5-083 · SOFTWARE** Rebind only the same logical controller container with verified endpoint
      shape and a new generation before completing the transaction.
- [ ] **P5-084 · SOFTWARE** Snapshot and journal the original controller mode before the first mode
      change and restore that exact mode during handoff or recovery.
- [ ] **P5-085 · SOFTWARE** Keep activation free of ROM/profile writes and any reset not required
      for an explicit user-approved operation; serialize any separately approved ROM sync against
      late/orphan ACKs and verify the persisted state afterward.
- [ ] **P5-086 · SOFTWARE** Add fixture tests for ACK ordering, malformed length, late ACK, PnP
      loss, wrong container, timeout, cancellation, host death, and successful rebind.
- [ ] **P5-087 · HARDWARE · DESTRUCTIVE-RISK** Verify each supported controller mode transition,
      enumeration event, ACK, topology, cancellation boundary, and original-mode restoration.
- [ ] **P5-088 · HARDWARE** Run 100 mode switches with intermittent suspend, host restart, and
      target loss; verify no stale handle, duplicate controller, stuck output, or lost restore
      snapshot.
- [ ] **P5-089 · RELEASE-GATE** Enable controller-mode writes only for firmware/topologies whose
      transition and restoration evidence pass the complete generation-aware matrix.

### P5.8 M3 physical controller and canonical input

- [ ] **P5-090 · SOFTWARE** Bind the physical DirectInput source to the exact A2VM container and
      verified report/mapping; never choose the first controller by enumeration order.
- [ ] **P5-091 · SOFTWARE** Translate buttons, D-pad, sticks, triggers, Guide, M1/M2, OEM controls,
      and touch surfaces only when physically present and evidence-verified.
- [ ] **P5-092 · SOFTWARE** Normalize axes with captured signedness, center, deadzones, ranges, and
      trigger semantics while retaining raw diagnostics for bounded bring-up.
- [ ] **P5-093 · SOFTWARE** Publish monotonic timestamp, packet sequence, device generation,
      connection/source health, and complete canonical snapshots on the high-rate data plane.
- [ ] **P5-094 · SOFTWARE** Coalesce only superseded axis state; preserve ordered button edges and
      report explicit overflow/loss rather than silently manufacturing state.
- [ ] **P5-095 · SOFTWARE** Prove simultaneous button/axis/OEM rollover and keep M1/M2 separate from
      front OEM controls and standard Guide/View/Menu aliases.
- [ ] **P5-096 · SOFTWARE** Stop publication and send a terminal neutral/disconnected state on
      deactivation, suspend, generation change, read failure, or host exit.
- [ ] **P5-097 · SOFTWARE** Keep physical acquisition conditional on controller management and the
      exact survival policy frozen by `P0-004`; avoid racing an external controller manager.
- [ ] **P5-098 · SOFTWARE** Add fixture replay for every verified input, edge ordering, rollover,
      disconnect, reconnect, overflow, generation change, and malformed report.
- [ ] **P5-099 · HARDWARE** Validate latency, jitter, ranges, centers, diagonals, trigger
      independence, Guide, M1/M2, OEM controls, and simultaneous input against the reference unit.
- [ ] **P5-100 · HARDWARE** Verify physical acquisition/release and original-mode restoration with
      Steam, Desktop, games, HC absent/present/passive, suspend, and controller re-enumeration.
- [ ] **P5-101 · RELEASE-GATE** Publish the A2VM canonical source only after the full mapping and
      lifecycle fixture/hardware matrices are locked and reproducible.

### P5.9 M3 rumble output

- [ ] **P5-102 · SOFTWARE** Implement the exact verified DirectInput rumble report length, IDs,
      motor order, byte scale, update rate, endpoint generation, and stop frame without writing any
      persistent motor-intensity profile address during normal rumble.
- [ ] **P5-103 · SOFTWARE** Translate canonical low/high-frequency motor commands with saturation,
      coalescing, rate limiting, cancellation, and no unbounded output queue.
- [ ] **P5-104 · SOFTWARE** Require current physical ownership and generation for output and reject
      stale target feedback after controller replacement or mode change.
- [ ] **P5-105 · SOFTWARE** Send and verify the stop operation on zero command, target removal, game
      exit, source loss, suspend, deactivation, host fault, cancellation, and timeout.
- [ ] **P5-106 · SOFTWARE** Quarantine rumble independently after an indeterminate output or failed
      stop while preserving controller input and other capabilities.
- [ ] **P5-107 · SOFTWARE** Add motor-order, scaling, clamp, coalescing, queue, rate, generation,
      stop-path, malformed-feedback, and failure-injection tests.
- [ ] **P5-108 · HARDWARE · DESTRUCTIVE-RISK** Verify both motors, full scale, minimum perceptible
      scale, sustained rate, stop latency, interruption paths, and physical temperature/safety.
- [ ] **P5-109 · RELEASE-GATE** Prove no rumble survives any ownership or lifecycle boundary and no
      output fault takes down canonical input.

### P5.10 M3 motion sensing and calibration

- [ ] **P5-110 · SOFTWARE** Bind the exact verified Windows gyro and accelerometer identities to the
      A2VM device; refuse family-name or first-sensor matching.
- [ ] **P5-111 · SOFTWARE** Implement event-driven sampling with sensor timestamps, QPC correlation,
      units, sample sequence, generation, freshness, and explicit lost-sample reporting.
- [ ] **P5-112 · SOFTWARE** Apply evidence-verified gyro and accelerometer orientation matrices into
      one documented canonical right-handed coordinate system.
- [ ] **P5-113 · SOFTWARE** Keep calibration parameters scoped to exact stable sensor identity and
      firmware; invalidate them after identity or orientation evidence changes.
- [ ] **P5-114 · SOFTWARE** Implement bounded stationary calibration with quality thresholds,
      cancellation, progress, outlier rejection, no arbitrary raw sensor writes, and safe
      persistence.
- [ ] **P5-115 · SOFTWARE** Publish verified canonical motion whenever the plugin sensor resource is
      healthy, independent from selected virtual target; WSGM forwards it only to compatible targets
      and never synthesizes a fictitious Xbox gyro mapping.
- [ ] **P5-116 · SOFTWARE** Keep sensor acquisition, overlay axes, diagnostics, and calibration
      alive when only controller management is off; stop subscriptions on suspend/lock, full Device
      Integration deactivation, host fault, or sensor identity change.
- [ ] **P5-117 · SOFTWARE** Add orientation, units, timestamp, freshness, calibration, invalidation,
      disconnect, sample-loss, and target-support fixture tests.
- [ ] **P5-118 · HARDWARE** Validate six orientations, positive axes, stationary bias, motion
      timing, suspend/resume, controller mode changes, and sensor identity on the reference unit.
- [ ] **P5-119 · HARDWARE** Measure controller-plus-motion CPU, allocations, wakeups, latency, loss,
      and one-hour stability against the Claw gameplay budget.
- [ ] **P5-120 · RELEASE-GATE** Enable the plugin motion capability only with reproducible sensor
      identity/orientation/calibration evidence; selected-target compatibility gates forwarding, not
      motion acquisition, diagnostics, calibration, or capability health.

### P5.11 M4 RGB lighting

- [ ] **P5-121 · SOFTWARE** Gate RGB on the exact evidence-approved firmware whitelist, endpoint,
      profile base, zone count/order, command framing, readback, and persistence class.
- [ ] **P5-122 · SOFTWARE** Publish the verified nine physical zones, supported effects, colors,
      speed, brightness, frame count, and units without inventing unsupported semantic controls.
- [ ] **P5-123 · SOFTWARE** Keep the UI preview entirely local until an explicit coalesced Apply;
      never write one firmware frame for every pointer or color-picker movement.
- [ ] **P5-124 · SOFTWARE** Validate effect-specific required fields, zone completeness, bounds,
      frame count, firmware, generation, and current resource ownership inside the plugin.
- [ ] **P5-125 · SOFTWARE** Snapshot the exact current lighting state and persistence class, apply
      in evidence-approved order, read back, and roll back on any partial result.
- [ ] **P5-126 · SOFTWARE** Prefer verified volatile application; when only a persistent commit is
      available, label it, rate-limit it, journal it, and require explicit user confirmation.
- [ ] **P5-127 · SOFTWARE** Keep lighting available independently of controller management and keep
      its failure/quarantine isolated from controller, power, fan, and OEM resources.
- [ ] **P5-128 · SOFTWARE** Add fixture tests for every effect, zone order, bounds, coalescing,
      readback, partial write, rollback, generation, firmware gate, and persistence policy.
- [ ] **P5-129 · HARDWARE · DESTRUCTIVE-RISK** Map and visually verify all nine zones, colors,
      effects, speed, brightness, frame order, readback, rollback, and restoration.
- [ ] **P5-130 · HARDWARE** Measure preview/apply latency, sustained effect behavior, firmware write
      count, resume, host fault, and repeated-apply stability.
- [ ] **P5-131 · LEGAL** Verify that all first-party lighting names/icons/assets and any referenced
      MSI protocol facts have complete provenance and notices.
- [ ] **P5-132 · RELEASE-GATE** Enable RGB only on whitelisted firmware after exact visual mapping,
      readback, rollback, persistence, and wear-risk acceptance pass.

### P5.12 Optional capabilities and rare persistent repair

- [ ] **P5-133 · DECISION** Promote charge limit, battery conservation, cooler boost, extra thermal
      sensors, or another secondary capability only after first-release scope and evidence review.
- [ ] **P5-134 · HARDWARE** Characterize any promoted optional capability using the full Device Lab
      identity, bounds, effect, readback, rollback, persistence, and firmware-gate workflow.
- [ ] **P5-135 · SOFTWARE** Implement each promoted capability as an independent semantic resource;
      do not hide model-specific raw registers behind a generic settings surface.
- [ ] **P5-136 · HARDWARE · DESTRUCTIVE-RISK** Keep rear-button profile-memory repair diagnostic and
      manual unless the reference unit proves a broken state that cannot be safely restored
      otherwise.
- [ ] **P5-137 · SOFTWARE** If repair is justified, require exact prior snapshot, reviewed trial,
      explicit confirmation, write-count bound, readback, rollback, journal, and recovery
      instructions.
- [ ] **P5-138 · SOFTWARE** Never perform a persistent profile repair on activation, normal profile
      selection, controller target switch, resume, or host restart.
- [ ] **P5-139 · LEGAL** Add source/protocol provenance and dependency review for each optional or
      persistent operation before package promotion.
- [ ] **P5-140 · RELEASE-GATE** Keep every unpromoted or evidence-incomplete optional capability
      absent from retail descriptors, UI, defaults, and marketing claims.

### P5.13 M5 plugin diagnostics, performance, and release gate

- [ ] **P5-141 · SOFTWARE** Emit structured per-resource logs for device/host generation, identity,
      operation, desired/observed/readback state, ownership, timeout, rollback, and recovery result.
- [ ] **P5-142 · SOFTWARE** Redact serials, unique paths, raw capture payloads, secrets, calibration
      samples, and user data from default logs and support bundles.
- [ ] **P5-143 · SOFTWARE** Add bounded diagnostic commands for identity, dependencies, descriptors,
      current state, queue/circuit health, ownership, and journal status; add no raw write console.
- [ ] **P5-144 · SOFTWARE** Measure each repeating source's cadence, CPU, allocations, handles,
      wakeups, queue depth, and disposal; replace unnecessary polling with events.
- [ ] **P5-145 · HARDWARE** Prove DeviceHost plus idle Claw plugin remains under 0.5% CPU on the
      reference device with bounded memory/handles and no unexplained steady wakeup loop.
- [ ] **P5-146 · HARDWARE** Prove active controller plus motion remains under 2% CPU during gameplay
      and publish latency, jitter, sample loss, queue depth, allocation, and power results.
- [ ] **P5-147 · HARDWARE** Run one-hour idle and gameplay soaks, 100 suspend/resume cycles, 100
      mode switches, repeated Steam restart, hibernate, user switch, and forced-termination
      recovery.
- [ ] **P5-148 · HARDWARE** Run the complete positive/negative detection, power/fan,
      controller/target, UI-input, rumble/motion, OEM/chord, lighting, coexistence, and performance
      acceptance matrices.
- [ ] **P5-149 · SOFTWARE** Produce a hardware-verification manifest referencing immutable evidence
      artifacts without granting runtime mutation authority or upgrading trust by itself.
- [ ] **P5-150 · SOFTWARE** Pack the exact generated/handwritten/evidence/dependency composition,
      validate it offline, and prove install/update/rollback/uninstall against simulated host
      lifecycles.
- [ ] **P5-151 · LEGAL** Complete plugin notices, protocol-fact provenance, dependency licenses,
      firmware limitations, safety warnings, and external-manager coexistence documentation.
- [ ] **P5-152 · SOFTWARE** Document user-visible recovery for missing provider, unsupported
      firmware, conflicted resource, failed handoff, quarantined write, and interrupted update.
- [ ] **P5-153 · RELEASE-GATE** Require every shipped descriptor operation to trace to exact
      evidence, implementation, fixture tests, reference-hardware proof, diagnostics, and
      restoration procedure.
- [ ] **P5-154 · RELEASE-GATE** Prove one resource can fail, quarantine, or be externally owned
      while the shell, overlay, host, and every unrelated verified capability remain healthy.
- [ ] **P5-155 · RELEASE-GATE** Mark the A2VM package hardware-verified and integration-ready only
      after `P5-174`, M0 through M5, package validation, legal/provenance, performance, coexistence,
      and recovery pass; retail signing/installer/update/downgrade approval remains a final `P10`
      gate.

### P5.14 Protocol and acceptance audit completion

- [ ] **P5-156 · SOFTWARE · HARDWARE** Record and verify MSI WMI provider GUID
      `ABBC0F6E-8EA1-11d1-00A0-C90629100000`, instance `0`, and low-level method pairs `0D/0E`,
      `11/12`, `19/1A`, and `1B/1C` against the actual Windows named MOF methods before use.
- [ ] **P5-157 · SOFTWARE** Keep named-method/low-level pair selection evidence-locked per operation
      and reject provider/GUID/instance/method-shape mismatch without falling back by numerical
      proximity.
- [ ] **P5-158 · SOFTWARE** Bind the physical XInput source to the exact A2VM container and
      implement verified standard controls, ranges, packet/disconnect semantics, and no unavailable
      M1/M2 claims.
- [ ] **P5-159 · SOFTWARE** Keep the captured original XInput mode when it satisfies requested
      controls; switch to DirectInput only when verified M1/M2 ownership is required, with concrete
      PnP rebind and exact original-mode restore.
- [ ] **P5-160 · SOFTWARE** Implement normal physical XInput vibration fallback with canonical motor
      mapping, generation, rate/stop behavior, and no persistent motor-intensity profile writes.
- [ ] **P5-161 · HARDWARE** Validate DirectInput and XInput physical capture plus rumble
      independently across activation, simultaneous input/output, mode change, suspend, disconnect,
      handoff, and every stop boundary.
- [ ] **P5-162 · SOFTWARE** Run the low-level keyboard hook on a dedicated message-loop thread with
      a preallocated bounded event queue; perform no IPC, WMI, HID, UI, synchronous logging,
      blocking, busy-spin, or allocation-heavy work in the callback.
- [ ] **P5-163 · SOFTWARE** Build the exact ordered `SendInput` suppression batch with reserved
      `VK 0xFF` dummy down/up and required Windows-key ups—never F24—and suppress the physical chord
      only after the full batch is accepted.
- [ ] **P5-164 · SOFTWARE** Account for every accepted `SendInput` prefix, issue only its targeted
      cleanup on partial injection, tag/ignore self-injection, and fail open without stuck keys or a
      mismatched synthetic release.
- [ ] **P5-165 · SOFTWARE** Reset/reinstall the hook state across desktop, session, sign-in/out,
      helper, host, suspend, lock, and generation transitions; never intercept secure-desktop input.
- [ ] **P5-166 · SOFTWARE** Never disable `i8042prt`, the ACPI keyboard path, either volume key, or
      install an unsigned/source-specific kernel keyboard filter in the first release; when HC's
      Win+G blocker owns suppression, do not install a second blocker.
- [ ] **P5-167 · SOFTWARE** Keep hook callback time below `LowLevelHooksTimeout` with measured
      worst-case latency and add zero-allocation/queue-overflow/reentrancy/fail-open tests.
- [ ] **P5-168 · SOFTWARE** Treat HC “Ambilight” as grouped/dual-zone color only and never describe
      it as screen-reactive lighting without new hardware evidence.
- [ ] **P5-169 · SOFTWARE** If original lighting cannot be read, make activation and UI preview
      hardware-inert and permit no temporary Apply claim; an explicit persistent user commit becomes
      desired state and must not be described as restorable to an unknown original.
- [ ] **P5-170 · HARDWARE** Run named OEM suppression cells for Game Bar
      installed/enabled/disabled/absent; Desktop/windowed/borderless/exclusive fullscreen; external
      keyboards and both Windows keys; and another key arriving mid-transaction.
- [ ] **P5-171 · HARDWARE** Run tagged/untagged injection by other apps, hook reentrancy, every
      accepted-prefix count, cold boot, sign-out/in, helper restart, secure desktop, and lifecycle
      reset while checking for no Game Bar, Start, or Task View flash.
- [ ] **P5-172 · SOFTWARE** Expose bounded plugin diagnostics for physical-handle generation,
      report-path open/close, re-enumeration, and source continuity so the concrete `P6` UI-capture
      matrix can prove surfaces cause no hardware churn.
- [ ] **P5-173 · HARDWARE** Prove exact base detection and healthy unrelated resources when the
      controller is disabled, offline, re-enumerating, the provider is absent, or sensors are
      absent.
- [ ] **P5-174 · RELEASE-GATE** Close the exact WMI, physical XInput, rumble, hook, RGB-state,
      controller-offline, and named OEM acceptance matrices before M5 integration-ready status.

## P6 — HIDMaestro controller backend, HidHide, and WSGM input arbitration

### P6.1 Dependency acquisition, packaging, and backend boundary

- [ ] **P6-001 · LEGAL** Pin the reviewed HIDMaestro and usbip-win2 revisions and record license,
      source, build, binary hash, signer, driver/service identity, dependencies, and notices.
- [ ] **P6-002 · SOFTWARE** Reproduce audited HIDMaestro/usbip-win2 builds from source in an
      isolated pipeline and compare the packaged hashes and signatures to the reviewed outputs.
- [ ] **P6-003 · SOFTWARE** Define separate conditional installer components for the standard
      UMDF2/shared-memory backend, Steam Deck composite usbip path, HidHide, and
      architecture-specific files.
- [ ] **P6-004 · SOFTWARE** Implement install, health-check, repair, update, rollback, and removal
      in the trusted installer/component manager, never in DeviceHost or a device plugin.
- [ ] **P6-005 · SOFTWARE** Keep installed-but-disabled drivers/services inert and prove Device
      Integration off does not require, start, open, configure, or communicate with them.
- [ ] **P6-006 · SOFTWARE** Wrap HIDMaestro behind a WSGM-owned `IHidBackend`; keep all device
      plugins unaware of HIDMaestro profiles, driver protocol, virtual instance IDs, and
      installation details.
- [ ] **P6-007 · SOFTWARE** Define backend discovery, version/capability negotiation, target
      create/neutralize/remove, state publish, output subscription, health, cancellation, and
      diagnostics.
- [ ] **P6-008 · SOFTWARE** Add a fake backend that models enumeration, target loss, delayed output,
      failures, and generation changes for all coordinator and UI-input tests.
- [ ] **P6-009 · SOFTWARE** Isolate backend calls from the UI thread and DeviceHost control pipe;
      use bounded queues and make target generation explicit in every state/output operation.
- [ ] **P6-010 · SOFTWARE** Detect incompatible/missing backend components without looping repairs;
      degrade controller management while leaving device integration and SDL fallback usable.
- [ ] **P6-011 · SOFTWARE** Add static package/installer tests for hashes, signer, ACL, locations,
      service/driver names, dependency conditions, update ordering, and uninstall coverage.
- [ ] **P6-012 · RELEASE-GATE** Require a reproducible licensed build and reversible conditional
      install/upgrade/removal before testing a physical virtual target.

### P6.2 Canonical-to-target translation

- [ ] **P6-013 · SOFTWARE** Consume the single richest canonical handheld-state contract frozen in
      `P1-037`/`P1-038` and implement target-side validation/adaptation without redefining physical
      semantics in the HID backend.
- [ ] **P6-014 · SOFTWARE** Keep OEM controls and output/haptic events on separate channels from
      ordinary game input and prohibit target translation from inventing a generic remapper.
- [ ] **P6-015 · SOFTWARE** Validate canonical snapshot generation, sequence, timestamp, ranges,
      capability presence, and source health before forwarding it to any target.
- [ ] **P6-016 · SOFTWARE** Implement Steam Deck Composite translation for standard controls, four
      rear controls, native motion, touch/stick-touch only when physically present, and neutral
      defaults.
- [ ] **P6-017 · SOFTWARE** Implement Xbox 360 translation for only native XInput controls; omit
      motion, touch, rear controls, and any gyro-to-stick/mouse synthesis.
- [ ] **P6-018 · SOFTWARE** Implement DualShock 4 translation for standard controls and supported
      native motion with explicit unsupported-field behavior.
- [ ] **P6-019 · SOFTWARE** Keep DualSense absent from the initial retail selector unless `P0-021`
      explicitly promotes it and its independent driver/application matrix passes.
- [ ] **P6-020 · SOFTWARE** Treat physical calibration, dead-zone, range normalization, and
      orientation as plugin-owned canonicalization; perform only target-specific unit/field encoding
      here while preserving semantic meanings and prohibiting arbitrary remapping.
- [ ] **P6-021 · SOFTWARE** Route M1/M2 either to verified target rear controls or one allowlisted
      OEM action on targets without them, mutually exclusively and according to `P0-023`.
- [ ] **P6-022 · SOFTWARE** Publish a complete neutral state before first live state, after data
      loss, during local UI capture, and before removal; do not infer neutral from absence of
      packets.
- [ ] **P6-023 · SOFTWARE** Add golden translation fixtures for every control, capability omission,
      neutral state, range edge, timestamp/generation fault, M1/M2 route, and target type.
- [ ] **P6-024 · RELEASE-GATE** Require bit/field-level target fixtures and no unsupported
      capability synthesis before live enumeration tests.

### P6.3 One-target lifecycle and selection policy

- [ ] **P6-025 · SOFTWARE** Enforce at most one active WSGM virtual controller across every backend,
      process, restart, application override, and failure path.
- [ ] **P6-026 · SOFTWARE** Implement global default and per-application target selection using the
      identity/precedence policy frozen in `P0-016`.
- [ ] **P6-027 · SOFTWARE** Resolve the selected target through one service shared by profiles,
      overlay, native QAM, diagnostics, and launch detection.
- [ ] **P6-028 · SOFTWARE** Model `Absent`, `Creating`, `Neutral`, `Active`, `Replacing`, `Faulted`,
      and `Removing` with target generation, backend health, instance identity, and truthful
      progress.
- [ ] **P6-029 · SOFTWARE** Create a target neutral, verify its exact enumeration and backend
      generation, then enable live routing only through the activation transaction frozen in
      `P0-001`.
- [ ] **P6-030 · SOFTWARE** Replace a target by neutralizing and removing the old generation before
      creating the new one; prevent simultaneous old/new virtual input.
- [ ] **P6-031 · SOFTWARE** Wait for concrete enumeration/removal events under deadlines and never
      use a fixed sleep as proof that an application can bind the replacement.
- [ ] **P6-032 · SOFTWARE** Surface when the current application may require restart after target
      replacement and never claim that player slot/application rebinding is preserved without
      evidence.
- [ ] **P6-033 · SOFTWARE** On target creation/replacement failure, keep the physical source safely
      owned or hand it back through the chosen fallback transaction; never expose duplicate input.
- [ ] **P6-034 · SOFTWARE** Recover from backend/target loss with a new generation, held-input
      suppression, output stop, bounded retry, and fallback or quarantine.
- [ ] **P6-035 · SOFTWARE** Add fake-backend tests for concurrent requests, stale completions,
      create/remove timeout, replacement failure, process restart, and per-application precedence.
- [ ] **P6-036 · HARDWARE** Record enumeration/removal latency, instance identity, player slot,
      Steam/game/application binding, duplicate-input windows, and restart requirement for every
      target.

### P6.4 Target-specific live acceptance

- [ ] **P6-037 · HARDWARE** Prove Steam recognizes the composite target as a Valve Steam Deck
      controller without globally spoofing the host OS or unrelated device identity.
- [ ] **P6-038 · HARDWARE** Validate all Steam Deck target standard controls, A2VM rear controls,
      native motion, neutral unsupported touch/stick-touch fields, and Steam Input configuration
      routes.
- [ ] **P6-039 · HARDWARE** Validate Xbox 360 through native XInput and representative older games,
      programs, emulators, player-slot transitions, and physical rumble return.
- [ ] **P6-040 · HARDWARE** Validate DualShock 4 through the official PlayStation Remote Play
      client, representative software, native motion where supported, and physical rumble return.
- [ ] **P6-041 · HARDWARE** Verify target switching during Desktop, Game Mode, game start/exit,
      application override, Steam restart, suspend/resume, and physical-source re-enumeration.
- [ ] **P6-042 · HARDWARE** Compare high-report-rate CPU, latency, jitter, loss, memory, handles,
      and power for standard UMDF and Steam Deck composite usbip paths.
- [ ] **P6-043 · HARDWARE** Investigate and explain any recurrence of the observed 4–6%
      composite-target overhead before retail approval; do not normalize unexplained overhead as
      expected.
- [ ] **P6-044 · RELEASE-GATE** Approve each target independently; one target failure must not block
      a verified fallback target or Device Integration without controller management.

### P6.5 Output and haptic routing

- [ ] **P6-045 · SOFTWARE** Implement one WSGM output router from backend target-generation events
      to the current plugin-owned physical output sink; plugins never subscribe to HIDMaestro
      directly.
- [ ] **P6-046 · SOFTWARE** Validate output target generation, source target, supported
      effect/motor, range, timestamp, physical sink generation, and current resource ownership.
- [ ] **P6-047 · SOFTWARE** Translate supported target output into canonical low/high motor commands
      and explicitly degrade unsupported rich haptics to the verified Claw capability.
- [ ] **P6-048 · SOFTWARE** Bound, coalesce, rate-limit, cancel, and measure output queues without
      adding perceptible latency or allowing stale output to cross a target/source replacement.
- [ ] **P6-049 · SOFTWARE** Issue stop on local UI capture, target remove/fault, game exit, source
      loss, suspend, controller handoff, backend fault, router fault, and WSGM shutdown.
- [ ] **P6-050 · SOFTWARE** Report output active/stopped/faulted/indeterminate separately from input
      health and quarantine output without taking down the controller source.
- [ ] **P6-051 · SOFTWARE** Add fake target/plugin tests for motor mapping, unsupported output,
      stale generation, queue pressure, coalescing, every stop boundary, and sink failure.
- [ ] **P6-052 · HARDWARE** Verify end-to-end Steam Deck/Xbox/DS4 output direction, scale, latency,
      coalescing, sustained load, automatic stop, and recovery on the Claw.

### P6.6 HidHide owned-delta model

- [ ] **P6-053 · SOFTWARE** Implement a HidHide adapter that reads application/device entries,
      active state, service/driver health, and exact instance identities without normalizing them.
- [ ] **P6-054 · SOFTWARE** Persist an ownership ledger for only the DeviceHost allowlist and
      physical-instance deltas WSGM actually adds, including preexisting state and target device
      generation.
- [ ] **P6-055 · SOFTWARE** Compare-before-write every HidHide mutation and refuse cleanup when the
      current entry no longer equals the exact WSGM-owned delta.
- [ ] **P6-056 · SOFTWARE** Leave preexisting and externally added application/device entries,
      ordering, and global active state entry-for-entry untouched through activation and rollback.
- [ ] **P6-057 · SOFTWARE** Leave HidHide entirely unread/unmodified when controller management is
      off except an explicit diagnostics health check authorized by the user.
- [ ] **P6-058 · SOFTWARE** Apply and verify WSGM deltas only within the controller transaction
      frozen in `P0-001`; prove DeviceHost retains physical access before live target routing
      begins.
- [ ] **P6-059 · SOFTWARE** On cleanup, wait for plugin physical-handle closure and original-mode
      restoration, remove only WSGM-owned deltas, and verify physical visibility/topology.
- [ ] **P6-060 · SOFTWARE** Preserve the ledger and expose recovery steps when service loss, ACL
      failure, restart, timeout, external edit, or forced termination makes cleanup unverified.
- [ ] **P6-061 · SOFTWARE** Reconcile a crash ledger only after exact current device/application
      comparison; never delete a similarly named external entry or a different device generation.
- [ ] **P6-062 · SOFTWARE** Add full-state snapshot tests with preexisting HC/external entries,
      concurrent edits, duplicates, reorder, driver loss, partial apply, rollback, and crash
      recovery.
- [ ] **P6-063 · HARDWARE** Validate hide/unhide, DeviceHost access, application visibility,
      external entries, driver restart, forced process termination, update, and uninstall on the
      reference unit.
- [ ] **P6-064 · RELEASE-GATE** Require byte/entry-equivalent restoration of every external HidHide
      state across the complete fault matrix.

### P6.7 Controller activation, replacement, and handoff transaction

This section integrates the concrete backend, HidHide, source, and output adapters into the one
coordinator state machine owned by `P4.7`; it does not create a second handoff policy.

- [ ] **P6-065 · SOFTWARE** Bind concrete physical, HidHide, target, route, fallback, and
      verification effects to the coordinator-owned `P4.7` state machine and the order frozen in
      `P0-001`.
- [ ] **P6-066 · SOFTWARE** Acquire and validate the exact physical source, snapshot its mode/state,
      establish neutral target/hide prerequisites in the frozen order, and block live routing until
      safe.
- [ ] **P6-067 · SOFTWARE** Verify exactly one usable application-visible controller and exactly one
      DeviceHost-readable physical source before declaring managed input active.
- [ ] **P6-068 · SOFTWARE** Journal each ownership-changing boundary so process death can
      distinguish unapplied, applied, verified, rolling back, and indeterminate state.
- [ ] **P6-069 · SOFTWARE** Roll back in the exact safe order after failure at every boundary and
      remove only the target/HidHide/mode changes owned by that transaction.
- [ ] **P6-070 · SOFTWARE** For target replacement, keep the physical source controlled and neutral,
      stop output, remove old target, create/verify new target, suppress held input, then resume
      routing.
- [ ] **P6-071 · SOFTWARE** For controller-management-off, establish SDL/Steam-lease fallback first,
      neutralize and retain the target plus WSGM HidHide state, require plugin stop/original-mode
      restoration/physical release, then remove the target and only WSGM-owned HidHide deltas.
- [ ] **P6-072 · SOFTWARE** For full Device Integration off, perform the same controller handoff and
      then restore/release every other plugin resource and stop the device cycle.
- [ ] **P6-073 · SOFTWARE** On timeout, honor the user's toggle, preserve keyboard/touch fallback
      and recovery evidence, remove the virtual target and only WSGM-owned HidHide deltas, then
      either dispose DeviceHost for full disable or quarantine only controller ownership while
      non-controller resources continue; report unverified handoff and never silently reacquire.
- [ ] **P6-074 · SOFTWARE** Add deterministic failure injection before/after every transition,
      cancellation, concurrent toggle, target loss, plugin/driver death, and fallback failure.
- [ ] **P6-075 · HARDWARE** Execute the full transaction/fault matrix under Steam, games, HC states,
      suspend, mode re-enumeration, driver restart, updater exit, and forced WSGM termination.
- [ ] **P6-076 · RELEASE-GATE** Prove no tested boundary yields duplicate gameplay input, an exposed
      physical-plus-virtual pair, stuck output, lost external config, or a false clean-handoff
      claim.

### P6.8 `IUiGamepadSource` and existing-navigation parity

- [ ] **P6-077 · SOFTWARE** Extract `IUiGamepadSource` beneath `GamepadService` before changing any
      overlay/taskbar/Settings consumer; retain SDL as the first implementation.
- [ ] **P6-078 · SOFTWARE** Preserve `SdlGamepads` as the single SDL event-pump owner and keep
      current connect/disconnect, active-pad, repeat, chord, and disposal behavior under the new
      interface.
- [ ] **P6-079 · SOFTWARE** Preserve `GamepadNavigation`'s 250 ms SDL-versus-Steam-key suppression
      and every owner-scoped `SteamInputBlocker` handoff while establishing parity.
- [ ] **P6-080 · SOFTWARE** Add contract tests that replay identical SDL samples through the old and
      refactored paths and compare button edges, repeats, focus moves, chords, and active-pad
      selection.
- [ ] **P6-081 · SOFTWARE** Implement a managed canonical source from the high-rate data plane with
      bounded buffering, edge preservation, loss diagnostics, generation, and UI-dispatch
      projection.
- [ ] **P6-082 · SOFTWARE** Ignore matching physical and WSGM virtual SDL devices while the managed
      canonical source is healthy so one physical press cannot produce duplicate navigation.
- [ ] **P6-083 · SOFTWARE** Keep unmatched external SDL controllers available or excluded according
      to the source-selection policy frozen in `P0-005`, never by broad vendor-name filtering.
- [ ] **P6-084 · SOFTWARE** Base source health on actual physical ownership, fresh canonical state,
      and current generation; active fan/TDP/RGB management alone is insufficient.
- [ ] **P6-085 · SOFTWARE** Keep keyboard and touch navigation operational independent of source
      selection, Steam lease availability, and all device/backend faults.
- [ ] **P6-086 · SOFTWARE** Add source contract tests for malformed/lost packets, edge overflow,
      stale state, disconnect/reconnect, duplicate SDL devices, and dispatcher disposal.

### P6.9 Reference-counted local WSGM UI capture

- [ ] **P6-087 · SOFTWARE** Implement one reference-counted local capture service with owner IDs for
      overlay, taskbar, Settings controller navigation, modals, and future WSGM-owned surfaces.
- [ ] **P6-088 · SOFTWARE** Acquire capture before a surface accepts controller input and keep it
      until that owner's final close/disposal, including overlapping/nested surfaces.
- [ ] **P6-089 · SOFTWARE** On first claim, continue physical reads, stop output, publish one
      neutral target state, stop gameplay forwarding, and snapshot controls already held.
- [ ] **P6-090 · SOFTWARE** Suppress every pre-held control until full release so the opening
      chord/button cannot activate the first focused control.
- [ ] **P6-091 · SOFTWARE** Route edge, repeat, chord, and full-state events through the existing
      navigation semantics while capture is active.
- [ ] **P6-092 · SOFTWARE** After the final claim closes, keep the target neutral until all controls
      used by WSGM are released, then resume from a clean current-state boundary.
- [ ] **P6-093 · SOFTWARE** Keep Steam's native QAM outside local capture so it continues receiving
      the virtual controller and does not acquire the Steam Input block lease.
- [ ] **P6-094 · SOFTWARE** Make local capture an in-process routing lease only: no Steam hook,
      device rescan, HID revocation, HidHide edit, layout change, or target enumeration.
- [ ] **P6-095 · SOFTWARE** Force neutralization and owner cleanup on window fault, process
      shutdown, target/source change, suspend, controller handoff, or owner disposal.
- [ ] **P6-096 · SOFTWARE** Add overlapping-owner, open/close chord, held/repeat/analog, nested
      modal, abrupt close, target loss, source switch, and no-gameplay-leak tests.

### P6.10 Managed/SDL source switching and Steam lease fallback

- [ ] **P6-097 · SOFTWARE** Implement one source arbiter with explicit managed-ready,
      managed-capture, acquiring-fallback, SDL-ready, degraded, and keyboard/touch-only states.
- [ ] **P6-098 · SOFTWARE** When managed input becomes ready mid-surface, establish fresh canonical
      state, suppress held controls, begin local capture, then release only the surface Steam lease.
- [ ] **P6-099 · SOFTWARE** When managed input fails mid-surface, neutralize target, acquire the
      surface Steam lease, establish SDL input, suppress held controls, then release local capture.
- [ ] **P6-100 · SOFTWARE** Implement the make-before-break transfer and exact failure behavior
      frozen in `P0-024`, with generation tokens preventing late completion from reversing the
      chosen source.
- [ ] **P6-101 · SOFTWARE** If SDL or the Steam lease cannot be established, preserve
      keyboard/touch, keep the virtual target neutral while a WSGM focus-taking surface remains
      open, warn clearly, and resume gameplay only after a verified clean release boundary with no
      held canonical input leak.
- [ ] **P6-102 · SOFTWARE** Acquire no overlay/taskbar Steam lease while a healthy managed source
      and local capture are active; retain all existing lease code and observability for fallback.
- [ ] **P6-103 · SOFTWARE** Keep per-game launch leases independent from surface claims and preserve
      them regardless of managed-source availability or selected virtual target.
- [ ] **P6-104 · SOFTWARE** Preserve owner-scoped lease transfer between Settings/overlay/taskbar
      and avoid uninstalling or globally disabling the Steam Input shim as a mode transition.
- [ ] **P6-105 · SOFTWARE** Add transition tests for every source/lease/capture state, simultaneous
      surface owners, failures, timeouts, held controls, late packets, and app shutdown.
- [ ] **P6-106 · HARDWARE** Validate mid-surface managed-to-SDL and SDL-to-managed transitions under
      Steam, games, target loss, DeviceHost crash, controller toggle, and full integration toggle.

### P6.11 Controller-integration release gate

- [ ] **P6-107 · HARDWARE** Prove managed overlay, taskbar, and Settings navigation use physical
      canonical input without acquiring a surface Steam lease.
- [ ] **P6-108 · HARDWARE** Prove local capture makes the virtual target neutral until every UI-used
      control is released and leaks no navigation input into a game or Steam behind WSGM.
- [ ] **P6-109 · HARDWARE** Prove native QAM remains controller-navigable while no WSGM local
      capture or surface Steam lease is claimed for it.
- [ ] **P6-110 · HARDWARE** Prove fallback and recovery preserve navigation without duplicate
      presses, an input gap beyond the frozen bound, stuck controls, or broken keyboard/touch
      access.
- [ ] **P6-111 · HARDWARE** Run repeated enumerate/remove, suspend/resume, target switching, game
      start/exit, forced WSGM/host termination, driver failure, and HC coexistence tests.
- [ ] **P6-112 · HARDWARE** Record component-level idle/active CPU, memory, handles, wakeups,
      latency, jitter, loss, output cost, and composite-backend overhead on the reference Claw.
- [ ] **P6-113 · SOFTWARE** Document installed components, target limitations, application restart
      behavior, fallback, HidHide ownership, recovery, and how to disable controller management
      safely.
- [ ] **P6-114 · RELEASE-GATE** Mark the base controller integration candidate ready when all three
      initial targets, reversible HidHide, output routing, local capture, fallback, performance, and
      recovery pass; final surface-churn proof remains in `P6-115`/`P6-116` and retail installer
      approval remains in `P10`.

### P6.12 Managed-surface hardware-churn completion

- [ ] **P6-115 · HARDWARE** Using the `P5-172` diagnostics, prove opening/closing managed overlay,
      taskbar, Settings, and nested/modal surfaces causes zero Steam HID-handle revocation,
      controller rescan, physical re-enumeration, layout change, or duplicate report-path churn.
- [ ] **P6-116 · RELEASE-GATE** Mark controller integration complete only after repeated
      overlapping-surface, source-switch, target, Steam, game, and lifecycle churn tests preserve
      one physical acquisition/generation except at an intentional ownership transition.

## P7 — persistent Steam UI host, native QAM, and RTSS

### P7.1 Live Steam discovery and replacement thresholds

- [ ] **P7-001 · LIVE-STEAM** Record the current Windows Steam build, channels, launch flags,
      remote-debugging topology, target types, URLs, origins, execution contexts, and recreation
      behavior.
- [ ] **P7-002 · LIVE-STEAM** Inventory every candidate native QAM component/store/action for TDP,
      performance profile/data, frame limit, performance-overlay level, and controller target.
- [ ] **P7-003 · LIVE-STEAM** Record the exact OS/device/capability gates hiding each component and
      prove which narrow gate can be changed without activating unrelated SteamOS behavior.
- [ ] **P7-004 · LIVE-STEAM** Probe native controller navigation, localization, accessibility,
      animation, focus, sizing, route transitions, and action plumbing before any modification.
- [ ] **P7-005 · DECISION** For every candidate, apply the component-removal threshold from `P0-018`
      and record native restoration, read-only projection, deferred, or replacement status.
- [ ] **P7-006 · LIVE-STEAM** Capture sanitized structural fingerprints and fixtures without copying
      user content, credentials, store/community data, or unstable class names as the sole
      identifier.
- [ ] **P7-007 · LIVE-STEAM** Use the existing CEF harness to measure connect/probe/evaluate cost,
      target replacement, navigation, Steam restart, and steamwebhelper restart behavior.
- [ ] **P7-008 · SOFTWARE** Store Steam-build evidence and fixture provenance separately from
      runtime selectors and prohibit a fixture from declaring a live build compatible by itself.
- [ ] **P7-009 · RELEASE-GATE** Freeze the initial native-QAM component matrix and exact live probes
      before implementing state mutation or a replacement surface.

### P7.2 Persistent CDP transport and target lifecycle

- [ ] **P7-010 · SOFTWARE** Refactor the validated loopback/port-owner/WebSocket checks from
      `SteamCef` into one reusable persistent Steam UI transport without weakening them.
- [ ] **P7-011 · SOFTWARE** Discover candidate targets, validate process
      ownership/origin/type/shape, and attach only to allowlisted Steam controller-oriented
      contexts.
- [ ] **P7-012 · SOFTWARE** Maintain explicit browser, target, session, frame, execution-context,
      and document generations so no response or command crosses a replacement boundary.
- [ ] **P7-013 · SOFTWARE** Implement bounded CDP request IDs, responses, notifications,
      cancellation, deadlines, malformed-message rejection, and late/orphan response handling.
- [ ] **P7-014 · SOFTWARE** Reconnect asynchronously after Steam/steamwebhelper restart, target
      loss, route navigation, frame replacement, or JavaScript-context replacement.
- [ ] **P7-015 · SOFTWARE** Back off and expose retryable health after connection failure; never
      make WSGM startup or Desktop/Game Mode transitions await CEF readiness.
- [ ] **P7-016 · SOFTWARE** Use a single connection/session owner with reference-counted patch
      subscriptions and deterministic cancellation/disposal on Steam or WSGM exit.
- [ ] **P7-017 · SOFTWARE** Detect competing debugger/tool behavior and coexist without taking over,
      disconnecting, rewriting, or assuming ownership of another tool's CDP sessions.
- [ ] **P7-018 · SOFTWARE** Bound message bytes, outstanding requests, attach attempts, reconnect
      rate, evaluation time, log volume, and per-context resources.
- [ ] **P7-019 · SOFTWARE** Add fake-CDP tests for malformed frames, ID reuse, timeout,
      cancellation, navigation, context loss, target churn, Steam restart, backoff, and concurrent
      patches.
- [ ] **P7-020 · LIVE-STEAM** Validate persistent attach/reconnect/dispose through repeated Game
      Mode navigation and Steam/steamwebhelper restarts on supported Steam builds.

### P7.3 Versioned bootstrap and narrow bridge

- [ ] **P7-021 · SOFTWARE** Define an idempotent versioned bootstrap under one collision-resistant
      WSGM namespace with explicit document/context ownership and compatibility handshake.
- [ ] **P7-022 · SOFTWARE** Evaluate CDP Runtime bindings first and document why any additional
      local transport is necessary before adding one.
- [ ] **P7-023 · SOFTWARE** Implement versioned request, response, notification, cancellation,
      timeout, and generation envelopes between injected code and the WSGM host.
- [ ] **P7-024 · SOFTWARE** Expose only typed allowlisted state subscriptions and commands for the
      specific active patches; provide no generic execute, device, plugin, shell, filesystem, or RPC
      API.
- [ ] **P7-025 · SOFTWARE** Authorize every bridge command by patch ID, schema version,
      context/document generation, capability, bounds, and current service availability.
- [ ] **P7-026 · SOFTWARE** Keep hardware raw values and privileged operations behind WSGM semantic
      services; injected code receives only sanitized UI state and command results.
- [ ] **P7-027 · SOFTWARE** Remove bindings/listeners/bootstrap state owned by an obsolete
      generation and ensure context destruction is a valid cleanup boundary.
- [ ] **P7-028 · SOFTWARE** Add replay, confused-deputy, oversized payload, malformed type, stale
      context, command-flood, cancellation, and namespace-collision tests.
- [ ] **P7-029 · LIVE-STEAM** Validate binding availability and behavior across current Steam CSP,
      route navigation, iframe/context shapes, reload, and steamwebhelper restart.
- [ ] **P7-030 · RELEASE-GATE** Approve no local listener unless Runtime bindings fail a documented
      requirement and an authenticated random-capability bounded replacement passes security review.

### P7.4 Patch registry, health, and existing-patch migration

- [ ] **P7-031 · SOFTWARE** Define `ISteamUiPatch` with stable ID/version, probe, apply, verify,
      remove, fingerprint, diagnostics, recovery/reapply, resource bounds, and kill switch.
- [ ] **P7-032 · SOFTWARE** Implement a registry/scheduler that serializes conflicting DOM/store
      work while keeping independent patches healthy, cancellable, and separately observable.
- [ ] **P7-033 · SOFTWARE** Distinguish absent target, incompatible build, probe mismatch, applied,
      verified, degraded, disabled, remove-failed, and retrying for each patch.
- [ ] **P7-034 · SOFTWARE** Require probe and positive unique fingerprint before apply, resulting
      state verification after apply, and owned-resource verification after remove.
- [ ] **P7-035 · SOFTWARE** Never treat injected code/style presence as proof that a native
      component, store, action, selector, or asset mapping is functionally compatible.
- [ ] **P7-036 · SOFTWARE** Implement independent per-patch and global emergency kill switches that
      restore/remove only WSGM-owned resources without editing Steam installation files.
- [ ] **P7-037 · SOFTWARE** Convert the existing Wi-Fi store-data restoration into the first patch
      and compare behavior/diagnostics to the current live-verified one-shot implementation.
- [ ] **P7-038 · SOFTWARE** Migrate existing tabs, badges, downloads, launch-configuration, and
      other eligible CEF evaluations one at a time with a compatibility fixture and rollback path.
- [ ] **P7-039 · SOFTWARE** Preserve the existing one-shot path as a bounded fallback during each
      migration and remove it only after live parity and reconnect coverage pass.
- [ ] **P7-040 · SOFTWARE** Add patch-isolation tests proving one probe/apply/verify/remove failure,
      timeout, exception, or broken fixture cannot disable or corrupt another patch.
- [ ] **P7-041 · LIVE-STEAM** Validate every migrated patch across startup-before/after-Steam,
      navigation, context replacement, Steam restart, incompatible fixture, disable, and WSGM exit.
- [ ] **P7-042 · RELEASE-GATE** Retire independent string-evaluation loops only after
      persistent-host parity, removal, kill-switch, and live recovery are proven per patch.

### P7.5 Embedded TypeScript/React asset pipeline

- [ ] **P7-043 · SOFTWARE** Create a repository-owned TypeScript/React source project for injected
      UI modules with locked dependencies, reproducible install, lint, typecheck, tests, and bundle
      build.
- [ ] **P7-044 · SOFTWARE** Embed hash-locked minified bundles and source revision metadata into
      WSGM; do not fetch runtime code, styles, maps, or dependencies from the network.
- [ ] **P7-045 · SOFTWARE** Enforce a CSP-compatible no-eval build where feasible and document every
      required dynamic-code behavior with its scope and live evidence.
- [ ] **P7-046 · SOFTWARE** Generate strongly typed AOT-safe C# bridge schemas from one versioned
      contract or validate hand-maintained parity in both directions.
- [ ] **P7-047 · SOFTWARE** Prevent embedded UI code from accessing arbitrary origins, files,
      clipboard, shell commands, raw devices, plugin APIs, or broader WSGM state.
- [ ] **P7-048 · SOFTWARE** Add deterministic bundle hash, size-budget, dependency-license,
      forbidden-API, schema-compatibility, and generated-output drift checks to verification.
- [ ] **P7-049 · SOFTWARE** Keep patch source and generated bundle changes atomic and make a
      source-bundle mismatch fail the build.
- [ ] **P7-050 · RELEASE-GATE** Require reproducible assets, complete notices, bounded bundle size,
      and no runtime download path before injection is enabled by default.

### P7.6 Native QAM restoration patches

- [ ] **P7-051 · LIVE-STEAM** Implement an independently versioned probe/fingerprint fixture for
      each approved native TDP, profile/data, frame-limit, overlay-level, and controller-target
      component.
- [ ] **P7-052 · SOFTWARE** Remove only the exact approved Windows/capability gate per component and
      leave unrelated Linux storage, update, shutdown, power, and device behavior unchanged.
- [ ] **P7-053 · SOFTWARE** Supply each native component with its missing typed state and action
      contract through the narrow bridge; preserve Valve rendering and interaction code.
- [ ] **P7-054 · SOFTWARE** Bind QAM TDP/current value to the same desired/observed plugin service
      used by the overlay and expose command progress, rejection, timeout, and readback honestly.
- [ ] **P7-055 · SOFTWARE** Bind performance profile/data only to the scope approved by `P0-008` and
      do not invent a QAM-only profile state or telemetry source.
- [ ] **P7-056 · SOFTWARE** Bind frame limit and performance-overlay level to the shared RTSS
      service, including unavailable/degraded state and current per-game/global policy.
- [ ] **P7-057 · SOFTWARE** Bind controller target to the single target-selection service and
      surface replacement progress, fault, fallback, and possible application-restart warning.
- [ ] **P7-058 · SOFTWARE** Preserve native localization, accessibility, focus order, controller
      navigation, animation, scale, layout, Back behavior, and QAM open/close behavior.
- [ ] **P7-059 · SOFTWARE** Keep patch injection/reconnect independent from the process-long plugin;
      opening, closing, failing, or reinjecting QAM never starts/stops/restarts DeviceHost.
- [ ] **P7-060 · SOFTWARE** If a native component is genuinely absent, keep its replacement behind a
      separate approved decision, patch ID, kill switch, and parity/maintenance acceptance matrix.
- [ ] **P7-061 · SOFTWARE** Remove only WSGM-owned gates/bindings/resources on disable or
      incompatibility and leave the native QAM usable without a Steam restart.
- [ ] **P7-062 · SOFTWARE** Add fixture tests for each component's probe/apply/verify/remove,
      state/command flow, progress/error behavior, reconnect, and independent kill switch.

### P7.7 RTSS adapter and performance state

- [ ] **P7-063 · LEGAL** Audit the supported RTSS installation/API/profile integration,
      redistribution assumptions, license, process/IPC boundary, and user prerequisite
      documentation.
- [ ] **P7-064 · SOFTWARE** Implement bounded RTSS discovery by verified install/API identity and
      version; do not bind to an arbitrary similarly named process or writable file.
- [ ] **P7-065 · SOFTWARE** Define one shared RTSS service for availability, version, target
      process, frame limit, overlay level, telemetry health, command progress, and diagnostics.
- [ ] **P7-066 · SOFTWARE** Define global/per-application persistence and precedence for frame limit
      and overlay level according to `P0-009` and the shared application-identity policy.
- [ ] **P7-067 · SOFTWARE** Validate frame-limit and overlay-level bounds, units, supported modes,
      target application, generation, and current RTSS health before applying.
- [ ] **P7-068 · SOFTWARE** Advertise verified readback only for RTSS operations with a proven
      query; otherwise return applied-unverified, and always distinguish requested, observed,
      rejected, timeout, and external change.
- [ ] **P7-069 · SOFTWARE** Subscribe or poll only at a measured bounded cadence where no event API
      exists; cancel/dispose promptly and expose cost in performance diagnostics.
- [ ] **P7-070 · SOFTWARE** Treat RTSS absence, restart, version mismatch, profile contention, and
      command failure as an isolated unavailable/degraded feature.
- [ ] **P7-071 · SOFTWARE** According to `P0-009`, keep RTSS independent from or subordinate to the
      Device Integration master toggle consistently in overlay, QAM, startup, and persistence.
- [ ] **P7-072 · SOFTWARE** Add fake adapter tests for discovery, version gates, external edits,
      per-app transitions, timeout, restart, stale state, bounds, and persistence.
- [ ] **P7-073 · LIVE-STEAM** Validate RTSS state/action binding in native QAM across game
      launch/exit, focus changes, Steam restart, RTSS restart, missing RTSS, and external RTSS
      edits.
- [ ] **P7-074 · RELEASE-GATE** Publish no RTSS control until support scope, persistence,
      coexistence, readback truthfulness, failure isolation, and measured overhead are documented.

### P7.8 Shared overlay/QAM state synchronization

- [ ] **P7-075 · SOFTWARE** Make overlay and QAM clients observe the same immutable descriptor,
      desired state, observed state, freshness, progress, and command result streams.
- [ ] **P7-076 · SOFTWARE** Route each TDP/profile/frame-limit/overlay-level/target command through
      one semantic implementation with one serialization and precedence policy.
- [ ] **P7-077 · SOFTWARE** Include origin/correlation IDs only for echo suppression and
      diagnostics; never maintain separate last-writer state per surface.
- [ ] **P7-078 · SOFTWARE** Propagate a successful, rejected, timed-out, rolled-back, externally
      changed, or stale value immediately and consistently to both surfaces.
- [ ] **P7-079 · SOFTWARE** Handle simultaneous overlay/QAM commands deterministically by capability
      serialization and display queued/in-progress/final state without optimistic lies.
- [ ] **P7-080 · SOFTWARE** On QAM context loss, keep shared services and overlay state alive; on
      DeviceHost loss, keep RTSS controls and native QAM shell independently usable.
- [ ] **P7-081 · SOFTWARE** Add multi-client tests for simultaneous commands, late responses,
      reconnect snapshots, external changes, stale state, and partial subsystem failure.
- [ ] **P7-082 · LIVE-STEAM** Verify bidirectional state appears within the approved latency bound
      and survives repeated QAM route/context replacement without duplicated commands.

### P7.9 OEM2 QAM interaction

- [ ] **P7-083 · SOFTWARE** Implement one allowlisted `SteamUiHost.ToggleQuickAccess` command with
      no generic navigation/evaluation authority exposed to the plugin.
- [ ] **P7-084 · SOFTWARE** Accept the command only from the canonical OEM action router while the
      exact current Steam target/action fingerprint is healthy.
- [ ] **P7-085 · SOFTWARE** Deduplicate by physical OEM event/action generation so one press cannot
      issue multiple toggles when WMI/Raw observations overlap or a reconnect/command retry occurs;
      the suppression hook is never an OEM event/action source.
- [ ] **P7-086 · SOFTWARE** Preserve Steam's native focus/controller ownership and treat missing or
      incompatible QAM as a retryable feature warning, never a plugin lifecycle fault.
- [ ] **P7-087 · LIVE-STEAM** Validate press-to-open, press-to-close, game focus return, repeated
      taps, held/repeat input, Steam restart, context replacement, and no duplicate toggle.
- [ ] **P7-088 · RELEASE-GATE** Require exactly one native QAM transition per accepted OEM2 press
      and no effect on unrelated Steam routes or Windows shortcuts.

### P7.10 Base Steam UI and RTSS gate

- [ ] **P7-089 · LIVE-STEAM** Test WSGM-before-Steam, WSGM-after-Steam, Steam restart, webhelper
      restart, Big Picture navigation, game launch/exit, suspend/resume, and WSGM disable/exit.
- [ ] **P7-090 · LIVE-STEAM** Verify store, community, browser, downloads, games, desktop Chromium,
      unrelated QAM components, and non-target Steam contexts remain unchanged.
- [ ] **P7-091 · LIVE-STEAM** Break one fixture/selector/binding intentionally and prove its patch
      disables independently while native UI and all other patches remain healthy.
- [ ] **P7-092 · SOFTWARE** Measure initial connection/injection, reinjection, bridge traffic,
      steady CPU/memory/handles, RTSS cadence, retries, and resource cleanup.
- [ ] **P7-093 · SOFTWARE** Expose sanitized Steam build, target/session generation, patch versions,
      fingerprints, per-patch health, last failure, RTSS health, and kill switches in diagnostics.
- [ ] **P7-094 · SOFTWARE** Document supported Steam channels/builds, RTSS prerequisites, failure
      fallbacks, native-first maintenance workflow, and how to disable each patch.
- [ ] **P7-095 · RELEASE-GATE** Prove Big Picture launch/foreground is issued immediately and never
      awaits CEF/QAM/RTSS discovery/restoration; no patch delay may recreate the old long
      Back-to-Game-Mode transition, and every failure preserves usable native Steam UI.
- [ ] **P7-096 · RELEASE-GATE** Approve the base native-QAM candidate only after live component,
      shared-state, OEM2, focus/navigation, compatibility, isolation, performance, and removal
      matrices pass; final approval also requires the `P7-105` performance-service gate.

### P7.11 Shared performance telemetry and profile projection

- [ ] **P7-097 · SOFTWARE** Define versioned performance metric descriptors/state with stable metric
      ID, source, unit, range, timestamp, freshness, quality, generation, and unavailable reason.
- [ ] **P7-098 · SOFTWARE** Implement verified RTSS frame/performance metrics and plugin-provided
      hardware telemetry adapters independently; never relabel fan-curve points or guessed values as
      live CPU/GPU/device telemetry.
- [ ] **P7-099 · SOFTWARE** Merge approved metrics through one WSGM performance service with
      explicit source precedence, no duplicate conflicting metric IDs, bounded history, and stale
      expiry.
- [ ] **P7-100 · SOFTWARE** Make metric acquisition/subscription consumer-aware and reduce or stop
      expensive WMI/RTSS sampling when neither overlay, QAM, diagnostics, nor capture needs it.
- [ ] **P7-101 · SOFTWARE** Implement performance-profile and performance-data projection only to
      the interactive/read-only/deferred scope frozen in `P0-008`; keep deferred controls absent
      rather than simulated.
- [ ] **P7-102 · SOFTWARE** Bind overlay and QAM to the same metric/profile snapshots and preserve
      partial availability when RTSS or one hardware telemetry source is missing.
- [ ] **P7-103 · SOFTWARE** Add source conflict, units/ranges, freshness, cadence, consumer
      attach/detach, stale generation, missing RTSS/plugin, external change, and partial metric
      tests.
- [ ] **P7-104 · LIVE-STEAM** Validate the approved performance metrics/profile state through QAM
      navigation, game launch/exit, source restart, Steam restart, suspend/resume, and unavailable
      sources without blocking or stale display.
- [ ] **P7-105 · RELEASE-GATE** Meet numeric sampling/propagation/CPU/memory budgets and ship only
      metrics and profile interactions whose source, units, freshness, and behavior are verified.

## P8 — physical handheld glyph catalog and rendering

### P8.1 Upstream pin, provenance, and immutable inventory

- [ ] **P8-001 · LEGAL** Fetch exactly Handheld Controller Glyphs commit
      `46792aadf3b104efec1c5240ba414d2c0bf84127` through a reviewed build-time import.
- [ ] **P8-002 · LEGAL** Verify upstream repository identity, commit, top-level MIT license, every
      credited artwork/theme source, and any asset-specific attribution or restriction.
- [ ] **P8-003 · DECISION** Choose a re-syncable pinned snapshot or deterministic lock-manifest
      import and freeze repository paths for source, generated catalog, CEF assets, Avalonia assets,
      and notices.
- [ ] **P8-004 · SOFTWARE** Generate a lock manifest containing upstream revision, reviewed
      `theme.json` version `v2.1`, source/output path, media type, byte count, dimensions/view box,
      hash, provenance, and conversion action.
- [ ] **P8-005 · SOFTWARE** Inventory every upstream profile, CSS variable, `theme.json` mapping,
      full/left/right controller image, individual glyph, selector rule, and credited source.
- [ ] **P8-006 · SOFTWARE** Reject unexpected scripts, executables, symlinks, paths, media types,
      oversized files, external URLs, hash mismatches, or license changes during import.
- [ ] **P8-007 · SOFTWARE** Treat upstream CSS as build input only and prohibit it from controlling
      WSGM Avalonia or Steam CEF directly at runtime.
- [ ] **P8-008 · SOFTWARE** Add deterministic import and inventory tests that produce byte-identical
      lock/catalog outputs and no unreviewed network or runtime dependency.
- [ ] **P8-009 · LEGAL** Generate third-party notices and an asset-provenance report from the lock
      manifest and fail packaging when any included output lacks a complete chain.
- [ ] **P8-010 · RELEASE-GATE** Permit no glyph asset into a runtime bundle until pin, hash,
      provenance, license, bounds, and intended semantic use are reviewed.

### P8.2 WSGM semantic physical-profile catalog

- [ ] **P8-011 · SOFTWARE** Define a versioned WSGM-owned semantic profile schema independent from
      plugin ABI, virtual-target types, Steam selectors, and upstream CSS implementation details.
- [ ] **P8-012 · SOFTWARE** Give every profile a stable ID, display name, exact verified device
      mappings, revision, verification status, full/left/right art, controls, aliases, capabilities,
      provenance, and per-asset lock-manifest hash/format reference.
- [ ] **P8-013 · SOFTWARE** Define semantic control IDs for
      face/D-pad/stick/shoulder/trigger/Guide/View/Menu/QAM/rear/OEM/touch/trackpad controls plus
      explicit M1/M2 and logical-to-physical aliases, without binding them to one target report
      layout.
- [ ] **P8-014 · SOFTWARE** Represent physically absent trackpads, stick touch, rear buttons, and
      other controls explicitly so hiding is an exact capability decision, not a CSS-family guess;
      artwork availability must never enable, disable, or otherwise author hardware capabilities.
- [ ] **P8-015 · SOFTWARE** Convert reviewed `theme.json` and device CSS knowledge into catalog
      data; never let a runtime plugin contribute CSS, selectors, SVG, artwork, URL, or filesystem
      path.
- [ ] **P8-016 · SOFTWARE** Let an exact device definition advertise only a reviewed semantic
      profile ID and ignore/report unknown, unverified, incompatible, or path-like IDs.
- [ ] **P8-017 · SOFTWARE** Implement selection modes `Automatic`, `Native Steam glyphs`, and
      reviewed manual diagnostic override using the precedence frozen in `P0-017`.
- [ ] **P8-018 · SOFTWARE** In Automatic, activate only an exact verified device-to-profile mapping;
      otherwise retain native Valve and existing generic WSGM glyphs.
- [ ] **P8-019 · SOFTWARE** Keep selected physical presentation stable across Steam Deck/Xbox/DS4
      target changes; changing presentation must never change the HIDMaestro target, Steam Input
      mapping, SDL/XInput identity, device enumeration, or game-rendered prompts.
- [ ] **P8-020 · SOFTWARE** Implement the controller-management-off and external-controller identity
      behavior frozen in `P0-005` without falsely labeling an unmanaged source as the handheld.
- [ ] **P8-021 · SOFTWARE** Add schema/selection tests for versioning, exact/unknown/unverified IDs,
      absent controls, target changes, external controllers, Device Integration off, reverse
      identity isolation, and the one-way hardware-capability-to-presentation authority boundary.
- [ ] **P8-022 · RELEASE-GATE** Freeze catalog/profile/selection contracts and representative
      fixtures before building Steam selector rules or first-party profile UI.

### P8.3 A2VM profile verification and conditional fork

- [ ] **P8-023 · HARDWARE** Compare upstream `msi.claw` full-controller proportions and every
      visible control against A2VM photographs and the physical reference unit.
- [ ] **P8-024 · HARDWARE** Verify left/right images, MSI Center and QAM front-button sides, M1/M2
      rear-button sides, labels, orientation, every valid control, and physically absent
      trackpads/additional rear controls without hiding a valid control.
- [ ] **P8-025 · HARDWARE** Verify View/Menu/Guide/QAM aliases against captured logical OEM events
      and present the right-front firmware `Win+G` control as QAM, not Windows or Xbox Guide.
- [ ] **P8-026 · HARDWARE** Validate the catalog/profile preview at 100%, 125%, 150%, and handheld
      display scaling for blur, clipping, contrast, physical-layout comprehension, and a
      photograph/physical-unit comparison before the live Steam patch exists.
- [ ] **P8-027 · DECISION** Accept `msi.claw` only if all exact A2VM checks pass; otherwise create a
      separately reviewed `msi.claw-a2vm` rather than inheriting a misleading near-match.
- [ ] **P8-028 · SOFTWARE** Lock accepted A2VM artwork, mappings, aliases, verification evidence,
      and device-definition link in one atomic catalog update.
- [ ] **P8-029 · SOFTWARE** Add render/selection fixtures for the accepted A2VM profile and negative
      fixtures proving MSI Claw A8 and other MSI family profiles are not auto-selected.
- [ ] **P8-030 · RELEASE-GATE** Do not enable A2VM Automatic selection until visual, logical,
      provenance, scaling, and exact-identity acceptance all pass.

### P8.4 Build-time SVG inspection and Avalonia-safe assets

- [ ] **P8-031 · SOFTWARE** Build an importer that parses every approved SVG at build time and
      records element types, paths, transforms, fills/strokes, masks, clipping, filters, text,
      links, and bounds.
- [ ] **P8-032 · SOFTWARE** For each asset, choose a reviewed safe output: normalized supported
      geometry or deterministic rasterization at named WSGM sizes.
- [ ] **P8-033 · SOFTWARE** Reject active content, external references, scripts, foreign objects,
      unsupported transforms/filters, malformed geometry, excessive complexity, and unsafe
      dimensions.
- [ ] **P8-034 · SOFTWARE** Generate AOT-safe metadata/assets that require no reflection-heavy
      general SVG dependency and no arbitrary runtime SVG parsing.
- [ ] **P8-035 · SOFTWARE** Preserve view box, aspect ratio, transparent bounds, crisp target sizes,
      tintability where intended, and light/dark/high-contrast behavior.
- [ ] **P8-036 · SOFTWARE** Hash-link every generated asset to its source lock entry, importer
      version, conversion choice, and render settings.
- [ ] **P8-037 · SOFTWARE** Add deterministic golden image/snapshot tests at all supported scales,
      themes, DPI values, and disabled/selected/focused states.
- [ ] **P8-038 · SOFTWARE** Add size/complexity budgets and fail verification on nondeterministic
      render output, missing source link, unexpected alpha bounds, or catalog/output drift.
- [ ] **P8-039 · RELEASE-GATE** Visually review every changed generated asset before updating its
      profile verification state.

### P8.5 First-party Avalonia glyph service and surfaces

Execute this surface work only after the `P8.6`–`P8.9` Steam selector, asset-delivery, and
coexistence contracts demonstrate a viable bounded path; the shared catalog/importer work above may
proceed in parallel.

- [ ] **P8-040 · SOFTWARE** Extend the existing AOT-safe `GlyphIcon`/`GlyphStyle` path with a
      catalog-backed physical-device glyph service; retain all Kenney generic fallbacks.
- [ ] **P8-041 · SOFTWARE** Resolve glyphs by semantic control/profile/theme/scale with bounded
      caches, deterministic fallback, no plugin paths, and no runtime network access.
- [ ] **P8-042 · SOFTWARE** Provide full/left/right controller images, individual control glyphs,
      capability presence, aliases, upstream revision, and profile health to view models.
- [ ] **P8-043 · SOFTWARE** Add the Device > Controller physical-profile preview and selection UI,
      keeping it out of general Settings.
- [ ] **P8-044 · SOFTWARE** Add catalog glyphs to controller overview, live input test, OEM
      assignment rows, navigation hints, and diagnostics without coupling them to virtual target
      identity.
- [ ] **P8-045 · SOFTWARE** Preserve generic Xbox/PlayStation/Nintendo prompts whenever no exact
      physical profile exists or a semantic control lacks reviewed artwork.
- [ ] **P8-046 · SOFTWARE** Show unknown/unverified profile, upstream revision, exact-device
      mapping, conversion type, and fallback reason in sanitized diagnostics.
- [ ] **P8-047 · SOFTWARE** Bound cache bytes and release device-specific images after
      profile/lifecycle change; measure load, render, memory, and disposal cost.
- [ ] **P8-048 · SOFTWARE** Add view-model/control tests for selection, fallback, missing controls,
      target independence, theme/DPI changes, profile change, and Device Integration off.
- [ ] **P8-049 · HARDWARE** Validate controller comprehension and all A2VM glyph uses by controller,
      touch, and keyboard on the handheld display.
- [ ] **P8-050 · RELEASE-GATE** Require accessible, scalable, deterministic first-party rendering
      and correct generic fallback before enabling device-specific prompts by default.

### P8.6 Steam glyph patch identity, scope, and fingerprints

- [ ] **P8-051 · SOFTWARE** Implement independent `SteamInputHandheldGlyphPatch` identity/version,
      catalog version, selector version, kill switch, diagnostics, and removal lifecycle.
- [ ] **P8-052 · LIVE-STEAM** Capture current Windows Steam Input configuration/layout routes and
      relevant Big Picture/QAM/Main Menu prompt contexts for supported Steam builds.
- [ ] **P8-053 · LIVE-STEAM** Capture sanitized fingerprints for stable glyph URLs, controller-image
      containers, exact inline SVG shapes, and exact capability-control sets.
- [ ] **P8-054 · SOFTWARE** Allow injection only in positively identified controller-oriented Steam
      contexts; exclude store, community, browser, games, unrelated QAM/pages, and desktop Chromium.
- [ ] **P8-055 · SOFTWARE** Define unique positive probe and result verification per route/tier; a
      stylesheet or matched class fragment alone never proves compatibility.
- [ ] **P8-056 · SOFTWARE** Keep catalog, device mapping, Steam selector, and patch-host versions
      independently diagnosable and updatable.
- [ ] **P8-057 · SOFTWARE** Add Steam-build fixtures for known-compatible, missing, ambiguous, and
      drifted route/selector/component shapes without treating fixtures as live compatibility proof.
- [ ] **P8-058 · LIVE-STEAM** Verify exact target context ownership and no modification outside the
      approved Steam controller routes before adding asset delivery.

### P8.7 Independently healthy Steam mapping tiers

- [ ] **P8-059 · SOFTWARE** Implement stable `/steaminputglyphs/...` resource mapping as its own
      tier using only WSGM catalog semantic mappings and hash-locked assets.
- [ ] **P8-060 · SOFTWARE** Implement structural full/left/right controller-image replacement as an
      independent tier with unique container/route verification.
- [ ] **P8-061 · SOFTWARE** Implement individual inline Valve SVG replacement only for exact unique
      path/component-shape matches; disable one ambiguous mapping rather than guessing.
- [ ] **P8-062 · SOFTWARE** Implement capability hiding only after exact expected control-set and
      semantic-element identification; leave uncertain or valid controls visible.
- [ ] **P8-063 · SOFTWARE** Keep probe/apply/verify/remove/health/kill-switch state separate for all
      four tiers and for each exact mapping within fragile tiers.
- [ ] **P8-064 · SOFTWARE** Verify resulting image/style/reference and capability visibility, not
      just selector count or script completion.
- [ ] **P8-065 · SOFTWARE** Preserve native controller navigation, focus, localization,
      accessibility, animation, layout, and scaling in every active tier.
- [ ] **P8-066 · SOFTWARE** Add fixtures that break each tier/mapping independently and prove
      healthy glyph tiers and all non-glyph CEF patches remain active.
- [ ] **P8-067 · LIVE-STEAM** Validate every tier across all approved controller layout, binding,
      diagram, Big Picture, QAM, and relevant prompt routes with expected/result cases for every
      exposed face, D-pad, stick, shoulder, trigger, Guide, View, Menu, QAM, rear, touch, and
      trackpad glyph, plus the A2VM photograph/physical-unit comparison for every rendered
      diagram/label.
- [ ] **P8-068 · RELEASE-GATE** Enable only tiers with live positive uniqueness/result verification;
      all others fail closed to native Valve rendering.

### P8.8 Bounded CEF asset delivery and cleanup

- [ ] **P8-069 · DECISION** Freeze numeric per-file, dimension, per-profile, per-context,
      total-byte, style, observer, mutation-work, initial-injection/reinjection latency, steady
      CPU/memory, cleanup, and leak budgets.
- [ ] **P8-070 · LIVE-STEAM** Prototype context-local blob URLs and bounded data URLs delivered
      through the versioned `P7.3` WSGM bootstrap over the authenticated CDP session and test
      current Steam CSP/lifecycle behavior without ad-hoc evaluation paths.
- [ ] **P8-071 · SOFTWARE** Load only assets required by the selected reviewed profile and current
      healthy route tiers from WSGM's embedded hash-locked catalog.
- [ ] **P8-072 · SOFTWARE** Validate profile ID, catalog entry, source hash, media type, dimensions,
      bytes, total budget, and selector-template identity before delivery.
- [ ] **P8-073 · SOFTWARE** Create one WSGM namespace marker and one identifiable owned style
      element per CEF context with WSGM-owned semantic variables/rules only.
- [ ] **P8-074 · SOFTWARE** Track each context-local URL, style, marker, listener, and observer by
      document/context generation and make apply/remove idempotent.
- [ ] **P8-075 · SOFTWARE** Revoke URLs, disconnect observers, remove listeners, and remove only the
      WSGM style/marker on profile change, patch disable, navigation, replacement, or WSGM exit.
- [ ] **P8-076 · SOFTWARE** Treat context destruction as a valid cleanup boundary while dropping all
      host-side references so repeated recreation cannot leak memory or resource counts.
- [ ] **P8-077 · SOFTWARE** Scope any necessary mutation observer to the smallest verified
      controller subtree and prohibit frame-rate polling or continuous whole-document rescans.
- [ ] **P8-078 · SOFTWARE** Never emulate CSS Loader's `/themes_custom/...` route or expose a
      general unauthenticated local file/asset server.
- [ ] **P8-079 · SOFTWARE** If blob/data delivery fails, implement a fallback only after `P7-030`,
      with authentication, random capability, narrow asset paths, rotation/lifetime, and strict
      bounds.
- [ ] **P8-080 · SOFTWARE** Add budget, hash, path traversal, unknown profile, context replacement,
      partial apply, cleanup, leak, observer, and fallback-route security tests.
- [ ] **P8-081 · LIVE-STEAM** Validate delivery/removal across CSP, navigation, repeated context
      recreation, Steam restart, profile switching, patch disable, and WSGM exit.

### P8.9 CSS Loader/Decky coexistence

- [ ] **P8-082 · LIVE-STEAM** Capture positive fingerprints for the same Handheld Controller Glyphs
      theme when installed through CSS Loader/Decky, including stylesheet and resolved asset paths.
- [ ] **P8-083 · SOFTWARE** Detect only that verified competing glyph theme; do not label unrelated
      Steam themes or other injected tools as conflicts by process/name alone.
- [ ] **P8-084 · SOFTWARE** On conflict, keep WSGM glyph injection inactive, report the owner/theme,
      and leave every external stylesheet, URL, observer, marker, and asset untouched.
- [ ] **P8-085 · SOFTWARE** Re-probe after the competing theme is removed and activate only on a
      clean compatible context; never rewrite or remove the external theme to force WSGM ownership.
- [ ] **P8-086 · SOFTWARE** Keep Wi-Fi, native QAM, RTSS, and every other WSGM patch independent
      from the glyph conflict state.
- [ ] **P8-087 · SOFTWARE** Add no-theme, same-theme, unrelated-theme, ambiguous-theme, mid-session
      add/remove, navigation, and context-replacement fixtures.
- [ ] **P8-088 · LIVE-STEAM** Validate coexistence with current CSS Loader/Decky behavior and prove
      external resources are byte/identity-equivalent after WSGM apply/remove attempts.

### P8.10 Glyph lifecycle, diagnostics, and performance

- [ ] **P8-089 · SOFTWARE** Keep selected physical profile in long-lived Device Integration state
      and every CEF document/style/URL/observer as transient patch-owned state.
- [ ] **P8-090 · SOFTWARE** Reapply desired presentation after device activation/profile change,
      WSGM-before/after-Steam, Steam/webhelper restart, route change, or context replacement.
- [ ] **P8-091 · SOFTWARE** Retain the selected profile across device suspend and Windows resume
      while recreating only invalid context-local resources.
- [ ] **P8-092 · SOFTWARE** On Device Integration off, remove WSGM physical presentation and restore
      native Steam plus generic first-party fallbacks.
- [ ] **P8-093 · SOFTWARE** On virtual-controller-management off, retain the verified physical
      handheld presentation; apply `P0-005` only when arbitrating an active external/unmanaged input
      source and never tie presentation to the selected HIDMaestro target.
- [ ] **P8-094 · SOFTWARE** Report selection, exact-device mapping, upstream revision,
      asset/catalog/selector/patch versions, per-tier health, fingerprint, fallback, and theme
      conflict.
- [ ] **P8-095 · SOFTWARE** Measure initial import/load/injection, route reinjection, steady
      CPU/memory, observer work, context resources, asset bytes, and cleanup after repeated
      recreation.
- [ ] **P8-096 · SOFTWARE** Enforce zero steady-state polling when the relevant DOM is unchanged and
      prove only selected-profile assets reach each context.
- [ ] **P8-097 · LIVE-STEAM** Test Automatic/Native/manual switching, target switching, external
      controller use, controller-management off with the physical theme retained, Device Integration
      off, reverse target/input/enumeration isolation, and native restoration.
- [ ] **P8-098 · LIVE-STEAM** Verify game prompts, Steam Input bindings, device enumeration, store,
      community, browser, SDL/XInput identity, HIDMaestro target, hardware capability availability,
      and unrelated Steam contexts remain unchanged by glyph/profile selection.
- [ ] **P8-099 · RELEASE-GATE** Require no stale style/URL/observer, no cross-context injection,
      native fallback on every failure, independent patch health, and compliance with the numeric
      injection, reinjection, CPU/memory, observer, byte, cleanup, and leak budgets across the
      lifecycle matrix.

### P8.11 Reviewed catalog update workflow and completion gate

- [ ] **P8-100 · SOFTWARE** Implement an explicit import command that requires a named upstream
      commit and produces added/removed/changed profile, artwork, CSS mapping, provenance, and hash
      reports.
- [ ] **P8-101 · SOFTWARE** Regenerate semantic catalog, lock manifest, CEF assets, Avalonia-safe
      assets, notices, fixtures, and snapshots in one reviewable operation.
- [ ] **P8-102 · SOFTWARE** Require catalog schema tests, hash tests, importer tests, rendering
      tests, CEF fixtures, per-route fixtures, and affected-device selection tests on every update.
- [ ] **P8-103 · HARDWARE** Visually reaccept every affected exact device/profile; automatically
      mark mappings unverified until that device evidence is attached.
- [ ] **P8-104 · SOFTWARE** Commit source revision, lock data, catalog, generated assets, notices,
      fixtures, and verification changes atomically; reject partial or hand-edited generated output.
- [ ] **P8-105 · SOFTWARE** Provide no runtime downloader, silent tracking of upstream `main`,
      script execution, or plugin-supplied asset update path.
- [ ] **P8-106 · LEGAL** Re-run license/provenance and notice review for every changed source or
      asset.
- [ ] **P8-107 · RELEASE-GATE** Ship glyph integration only when catalog, A2VM identity, Avalonia,
      Steam tiers, coexistence, lifecycle, numeric performance/resource budgets, security,
      attribution, and update workflow pass.

## P9 — final overlay and Settings information architecture

### P9.1 Stabilization gate and handheld UX specification

- [ ] **P9-001 · RELEASE-GATE** Start the final overhaul only after capability descriptors/state,
      controller targets/input capture, native QAM, RTSS, glyphs, and provisional Device clients
      stabilize.
- [ ] **P9-002 · SOFTWARE** Inventory every current Session, Tools, Power, modal, taskbar,
      diagnostic, launch, storage, artwork, wake-lock, and system action before moving any UI.
- [ ] **P9-003 · SOFTWARE** Map each current action/state to exactly one proposed Home, Steam,
      Device, or System destination and flag genuine duplication versus shared-service projection.
- [ ] **P9-004 · DECISION** Produce handheld wireframes and freeze rail width, labels, ordering,
      responsive breakpoints, compact behavior, page density, and nested-page presentation.
- [ ] **P9-005 · DECISION** Freeze Home/Steam/System subnavigation and what remains in the taskbar,
      preventing Tools from reappearing as an unowned catch-all.
- [ ] **P9-006 · DECISION** Freeze Device section ordering, capability visibility, empty/degraded
      behavior, command progress, warnings, confirmations, and diagnostic disclosure.
- [ ] **P9-007 · HARDWARE** Validate wireframes at handheld DPI/scaling with controller, touch, and
      keyboard before implementation locks high-churn XAML.
- [ ] **P9-008 · SOFTWARE** Record explicit UX acceptance cases for first focus, Back/B, nested
      tools, scrolling, command failure, stale state, source switching, and surface reopen.
- [ ] **P9-009 · RELEASE-GATE** Approve the information architecture and acceptance fixtures without
      moving device controls into general Settings or duplicating backend logic in view models.

### P9.2 Navigation shell and page boundaries

- [ ] **P9-010 · SOFTWARE** Replace the monolithic visibility-switched overlay body with a
      navigation shell, destination descriptors, independent pages/view models, and explicit
      lifetime ownership.
- [ ] **P9-011 · SOFTWARE** Keep `OverlayController` as orchestration only and move destination/page
      state into focused clients without changing shell/session manager ownership.
- [ ] **P9-012 · SOFTWARE** Implement capability-driven destination/section visibility and remove
      the Device destination entirely when Device Integration is off.
- [ ] **P9-013 · SOFTWARE** Freeze an explicit eager/lazy policy per page, instantiate every
      declared lazy page only on first navigation, and deterministically dispose subscriptions,
      timers, images, input-test streams, and commands at its defined lifetime boundary.
- [ ] **P9-014 · SOFTWARE** Implement a bounded navigation stack for nested editors/tools with
      stable destination/page IDs and no retained stale device-generation objects.
- [ ] **P9-015 · SOFTWARE** Implement correct Back/B priority across popup, dialog, nested page,
      destination root, and overlay close without double actions.
- [ ] **P9-016 · SOFTWARE** Restore focused semantic control and scroll position on Back/reopen only
      when it still exists and is enabled; otherwise choose the deterministic nearest safe focus.
- [ ] **P9-017 · SOFTWARE** Preserve controller-first focus rings, directional geometry, repeats,
      page/section transitions, and held-input suppression through `UiInputArbiter`.
- [ ] **P9-018 · SOFTWARE** Support touch pointer, mouse, keyboard, screen-reader semantics, and
      controller concurrently without input-specific duplicate command paths.
- [ ] **P9-019 · SOFTWARE** Implement responsive rail/content behavior without clipping, unreachable
      controls, off-screen dialogs, or focus moving into hidden content.
- [ ] **P9-020 · SOFTWARE** Add navigation-shell tests for lazy creation/disposal, dynamic
      visibility, stack bounds, focus/scroll restoration, Back priority, resizing, and input-source
      switches.

### P9.3 Shared presentation components

- [ ] **P9-021 · SOFTWARE** Create reusable descriptor-driven control rows for toggle, enum, bounded
      range, action, progress, read-only state, unsupported, stale, faulted, and externally owned
      states.
- [ ] **P9-022 · SOFTWARE** Show desired, applied/readback, progress, rollback, freshness, and fault
      distinctly where they can diverge; never optimistically label a request as current hardware
      state.
- [ ] **P9-023 · SOFTWARE** Add shared warning/status presentation for identity, firmware,
      dependency, conflict, ownership, quarantine, recovery, restart-required, and unverified
      handoff.
- [ ] **P9-024 · SOFTWARE** Add consistent confirmation for safety-significant, persistent,
      full-speed, controller-replacement, calibration, recovery, and disable operations.
- [ ] **P9-025 · SOFTWARE** Implement reusable curve, color/effect preview, controller input test,
      rumble test, motion axes, glyph preview, and diagnostics summary components.
- [ ] **P9-026 · SOFTWARE** Keep preview state local until explicit Apply/Revert and reconcile
      cleanly when descriptors, device generation, desired state, or ownership change mid-edit.
- [ ] **P9-027 · SOFTWARE** Cancel or disable commands when state is stale/unavailable and surface
      the shared semantic reason rather than guessing from control-specific exceptions.
- [ ] **P9-028 · SOFTWARE** Localize labels, units, validation, progress, warnings, and recovery
      text; do not expose plugin-provided display markup or arbitrary strings as trusted UI.
- [ ] **P9-029 · SOFTWARE** Apply accessible names, help text, contrast, focus visuals, minimum
      touch targets, motion reduction, and scale-safe layout to every reusable component.
- [ ] **P9-030 · SOFTWARE** Add view-model/control tests for all semantic states, descriptor
      changes, mid-edit invalidation, cancellation, failure, accessibility metadata, and
      localization expansion.

### P9.4 Home destination

- [ ] **P9-031 · SOFTWARE** Move Steam/session status, Desktop/Game Mode transitions, active
      warnings, and immediate session actions into Home according to the frozen map.
- [ ] **P9-032 · SOFTWARE** Keep session transition commands bound to existing `ShellSession`
      services and preserve asynchronous device/CEF/RTSS startup invariants.
- [ ] **P9-033 · SOFTWARE** Show device/CEF/controller/RTSS warnings as summaries with navigation to
      their owning destination, not duplicated control implementations.
- [ ] **P9-034 · SOFTWARE** Preserve current disabled/busy/error semantics for mode transitions,
      Steam absence, Explorer readiness, and launch/recovery states.
- [ ] **P9-035 · SOFTWARE** Add parity tests for every migrated Session action and current warning,
      plus focus order, Back behavior, and dynamic availability.

### P9.5 Steam destination

- [ ] **P9-036 · SOFTWARE** Move library tabs, storage, artwork, per-game launch configuration, and
      other approved Steam-owned tools into focused Steam pages without changing their service
      owners.
- [ ] **P9-037 · SOFTWARE** Preserve current library/storage/artwork selection, progress, error,
      cancellation, cache, and safe-file behavior during view-model extraction.
- [ ] **P9-038 · SOFTWARE** Preserve per-game Steam Input lease options independently from managed
      UI input and virtual-target selection.
- [ ] **P9-039 · SOFTWARE** Keep native-QAM patch health/compatibility diagnostic links available
      without moving active handheld controls out of Device.
- [ ] **P9-040 · SOFTWARE** Add current-feature parity, large-library/storage, cancellation, page
      disposal, navigation, and no-regression tests for every migrated Steam tool.

### P9.6 System destination

- [ ] **P9-041 · SOFTWARE** Move power state/actions, wake locks, Task Manager, and approved system
      actions into System according to the frozen map.
- [ ] **P9-042 · SOFTWARE** Preserve confirmation, privilege, cancellation, failure, and
      lock-screen/shell safety behavior of existing system actions.
- [ ] **P9-043 · SOFTWARE** Keep RTSS controls in the destination selected by `P0-009` and avoid
      duplicating a separate frame-limit/overlay-level controller in System.
- [ ] **P9-044 · SOFTWARE** Keep device TDP/fan/power-profile controls out of System even when they
      share the word “power”; their semantic owner remains Device.
- [ ] **P9-045 · SOFTWARE** Add parity, privilege/failure, confirmation, dynamic availability,
      focus/navigation, and page-disposal tests for every migrated System action.

### P9.7 Device overview and profiles

- [ ] **P9-046 · SOFTWARE** Build Overview from shared state: exact identity, package/trust,
      lifecycle, owner, capability health, desired profile, conflicts, fan RPM, and validated live
      temperatures.
- [ ] **P9-047 · SOFTWARE** Never label curve-temperature points as live telemetry and show
      unknown/stale/faulted quality for every value whose validated source is absent.
- [ ] **P9-048 · SOFTWARE** Show partial capability health and recovery actions per resource rather
      than collapsing the entire device into one green/red status.
- [ ] **P9-049 · SOFTWARE** Build Profiles for global hardware profile selection, AC/DC behavior,
      and per-application overrides using the single precedence/persistence service.
- [ ] **P9-050 · SOFTWARE** Show current desired profile, active override, observed divergence,
      transition progress, and return-to-global behavior.
- [ ] **P9-051 · SOFTWARE** Add overview/profile tests for no device, detecting, passive, active,
      degraded, external ownership, stale data, overrides, rollback, and quarantine.

### P9.8 Device power and thermals

- [ ] **P9-052 · SOFTWARE** Build PL1/TDP and PL2 controls from current descriptors with units,
      bounds, relationship, AC/DC/scenario availability, desired/readback, and progress.
- [ ] **P9-053 · SOFTWARE** Build fan mode, independent left/right RPM, dual-curve editor, firmware
      release, and clearly indicated full-speed override from semantic capabilities.
- [ ] **P9-054 · SOFTWARE** Validate local curve completeness/monotonicity for feedback while
      relying on the plugin as the final safety authority.
- [ ] **P9-055 · SOFTWARE** Keep curve/color-like previews local, use explicit Apply/Revert,
      coalesce commands, and handle descriptor/generation changes mid-edit.
- [ ] **P9-056 · SOFTWARE** Surface partial apply, readback mismatch, rollback, quarantine, and safe
      recovery without offering an unreviewed raw retry.
- [ ] **P9-057 · SOFTWARE** Add descriptor boundary, AC/DC change, two-channel editor, apply/revert,
      progress/failure, stale, resize, controller, touch, and accessibility tests.

### P9.9 Device controller and motion

- [ ] **P9-058 · SOFTWARE** Build target selection, physical/virtual status, generation, backend,
      HidHide ownership, fallback source, and restart-required state from shared services.
- [ ] **P9-059 · SOFTWARE** Add a live canonical input test with semantic A2VM glyphs, raw
      diagnostics only behind opt-in, loss/freshness state, and bounded rendering cadence.
- [ ] **P9-060 · SOFTWARE** Add short left/right/both rumble tests with explicit automatic stop,
      visible countdown/progress, cancellation, and output-fault reporting.
- [ ] **P9-061 · SOFTWARE** Add physical glyph selection/preview, upstream revision, exact-device
      verification, target independence, Steam patch health, and native fallback state.
- [ ] **P9-062 · SOFTWARE** Build Motion with exact sensor identity, target support, rate/freshness,
      bounded live axes, bias/quality, calibration progress, result, and invalidation warning.
- [ ] **P9-063 · SOFTWARE** Disable unsupported target motion without offering gyro remapping and
      keep calibration separate from game-input semantic remapping.
- [ ] **P9-064 · SOFTWARE** Add target-change, app-override, HidHide, source fallback,
      input-capture, rumble-timeout, motion, calibration, glyph, and lifecycle view-model tests.

### P9.10 Device OEM controls and lighting/features

- [ ] **P9-065 · SOFTWARE** Build OEM1/OEM2/M1/M2 rows with physical glyph, source/availability,
      current routing, verified default, and only the allowlisted action vocabulary.
- [ ] **P9-066 · SOFTWARE** Show mutually exclusive rear-control versus OEM-action routing and never
      expose standard face/stick/trigger remapping or arbitrary macros.
- [ ] **P9-067 · SOFTWARE** Show firmware `Win+G` suppression state, system-wide version-one
      limitation, elevated behavior, last fault, and no misleading source-specific claim.
- [ ] **P9-068 · SOFTWARE** Build Lighting from verified effect/zone descriptors with local preview,
      groups/zones, color, brightness, speed, explicit Apply/Revert, and persistence warning.
- [ ] **P9-069 · SOFTWARE** Render promoted optional device features only from verified semantic
      capabilities and keep missing/deferred features absent rather than as dead controls.
- [ ] **P9-070 · SOFTWARE** Add OEM routing, exact-one action, unsupported action, suppression
      fault, lighting validation/preview/apply, persistent warning, and capability-change tests.

### P9.11 Device diagnostics and recovery

- [ ] **P9-071 · SOFTWARE** Build Device diagnostics for exact firmware/descriptors/endpoints,
      package/trust/dependencies, host/device generations, transports, and per-resource ownership.
- [ ] **P9-072 · SOFTWARE** Show desired/observed/readback state, freshness, last transaction,
      timeout/rollback, circuit breaker/quarantine, journal item, and safe next action.
- [ ] **P9-073 · SOFTWARE** Add controller backend/target/HidHide/source/capture/output and
      CEF/glyph/QAM/RTSS summaries with navigation to their detailed owning diagnostics.
- [ ] **P9-074 · SOFTWARE** Offer only reviewed semantic retry/reset/recovery/export operations;
      expose no raw WMI/HID/IOCTL/register, arbitrary trial, script, shell, or plugin command UI.
- [ ] **P9-075 · SOFTWARE** Add versioned sanitized trace export with opt-in high-rate/raw evidence,
      explicit preview, redaction report, destination, cancellation, and no automatic upload.
- [ ] **P9-076 · SOFTWARE** Keep general WSGM logs/startup/update diagnostics in Settings according
      to `P0-022`; do not duplicate device recovery controls there.
- [ ] **P9-077 · SOFTWARE** Add diagnostics tests for every state/quality/fault, redaction, export,
      unresolved journal, unsupported recovery, no-device, and changing generation.

### P9.12 Settings ownership-only changes

- [ ] **P9-078 · SOFTWARE** Add Device Integration master and controller-management child toggles to
      WSGM Settings with normalized dependencies, current owner/status, and async command progress.
- [ ] **P9-079 · SOFTWARE** Add plugin trust/update, startup behavior, diagnostics/logging level,
      and general glyph/CEF policy only where they configure WSGM ownership rather than hardware
      state.
- [ ] **P9-080 · SOFTWARE** Keep TDP, PL2, fan, charge, target, OEM, lighting, profile, calibration,
      active device/plugin, frame-limit, and RTSS active controls out of Settings.
- [ ] **P9-081 · SOFTWARE** Let standalone Settings connect safely to the authoritative coordinator
      as frozen in `P0-047`/`P0-048`; never create a second host or make window close end the device
      cycle.
- [ ] **P9-082 · SOFTWARE** Show deactivation progress, fallback state, timeout, unverified handoff,
      recovery guidance, and retained disabled state when the master toggle changes.
- [ ] **P9-083 · SOFTWARE** Save configuration through normalized cross-process transactions and
      keep live hardware/profile preview state out of the coarse settings file.
- [ ] **P9-084 · SOFTWARE** Add multi-Settings-process, stale window, concurrent shell update,
      toggle dependency, timeout, close-mid-deactivation, normalization, and migration tests.

### P9.13 Base overlay lifecycle, performance, and acceptance

- [ ] **P9-085 · SOFTWARE** Keep every page a client of long-lived shared services and ensure
      overlay open/close/navigation never starts, stops, owns, or restarts device, target, RTSS, or
      CEF lifecycles.
- [ ] **P9-086 · SOFTWARE** Acquire/release local input capture or SDL/Steam surface leases exactly
      once per owning surface and dispose claims on every close/fault/process-exit path.
- [ ] **P9-087 · SOFTWARE** Bound live input/motion/telemetry rendering cadence and subscriptions;
      pause invisible previews without changing backend acquisition policy.
- [ ] **P9-088 · SOFTWARE** Measure first open, destination switch, nested navigation, large
      catalog, curve/color rendering, live input, memory, allocations, UI-thread stalls, and
      disposal.
- [ ] **P9-089 · SOFTWARE** Add snapshot tests at supported themes, resolutions, 100/125/150% and
      handheld scaling, long localization, high contrast, missing capabilities, and all fault
      states.
- [ ] **P9-090 · HARDWARE** Validate the complete overlay in Desktop and Game Mode with controller,
      touch, keyboard, target/source switches, Steam absent/restart, suspend, and degraded
      subsystems.
- [ ] **P9-091 · HARDWARE** Validate every Home/Steam/Device/System route, nested editor, Back/B,
      focus/scroll restoration, opening chord suppression, and no input leak to a running game.
- [ ] **P9-092 · SOFTWARE** Compare every pre-overhaul action/state against the inventory and close
      regressions or record intentional removals with migration guidance.
- [ ] **P9-093 · RELEASE-GATE** Prove Device is complete, QAM is only a quick projection, Settings
      remains ownership-only, and no UI surface duplicates a hardware/RTSS/CEF/controller service.
- [ ] **P9-094 · RELEASE-GATE** Approve the base final-overlay candidate only after functional
      parity, handheld UX, accessibility, lifecycle, failure isolation, input capture, performance,
      and visual QA pass; information-architecture completion also requires `P9-101`.

### P9.14 Final surface-coverage checks

- [ ] **P9-095 · SOFTWARE** Add shared RTSS frame-limit and performance-overlay-level rows to the
      Device overlay when integration is active and to the non-Device surface frozen in `P0-009`
      when they remain available with integration off; both use the same `P7` service.
- [ ] **P9-096 · SOFTWARE** Add the approved performance profile and metric projection from `P7.11`
      with per-metric source/freshness/quality and no UI-owned sampling or derived backend.
- [ ] **P9-097 · SOFTWARE** Add descriptor-driven charge-limit/battery-conservation controls to the
      appropriate Device section only when the active exact plugin advertises a verified capability;
      keep them absent otherwise.
- [ ] **P9-098 · SOFTWARE** Inventory and preserve all existing Settings-owned startup/auto-launch,
      session behavior, hotkeys/swipes, overlay appearance, CEF enablement, updates/logging, Home
      app, and general preference workflows during Settings refactoring.
- [ ] **P9-099 · SOFTWARE** Add parity/migration tests for every existing Settings field, default,
      normalization, save transaction, cross-process refresh, validation, and user-visible status.
- [ ] **P9-100 · SOFTWARE** Reuse/migrate the `P8-043` physical-profile selector/preview and its
      view model in the final Device > Controller page rather than creating a second selector or
      state.
- [ ] **P9-101 · RELEASE-GATE** Close RTSS/performance, conditional charge, existing Settings
      parity, and provisional-surface reuse before declaring the information architecture complete.

## P10 — integration, packaging, verification, and retail release

### P10.1 Branch integration and repository governance

- [ ] **P10-001 · SOFTWARE** Rebase or merge current `master` into `2.0` before each major phase and
      resolve plans/contracts early; do not leave the four-doc branch to diverge until release.
- [ ] **P10-002 · SOFTWARE** Implement work as small vertical commits that pair contract, production
      slice, tests, diagnostics, and documentation without early mass edits to hot overlay/settings
      files.
- [ ] **P10-003 · SOFTWARE** Keep requirement/task IDs in commit and pull-request descriptions and
      update this checklist only with evidence links, not estimates or compile-only claims.
- [ ] **P10-004 · SOFTWARE** Add a generated traceability report mapping every shipped
      capability/operation to design requirement, task, contract, code owner, tests, evidence, and
      documentation.
- [ ] **P10-005 · SOFTWARE** Validate all new directories have correct scoped ownership rules and
      `CLAUDE.md` symlink convention before merging their first implementation.
- [ ] **P10-006 · SOFTWARE** Keep generated files deterministic, reviewable, marked, and separate
      from handwritten code; fail verification on drift or uncommitted regeneration.
- [ ] **P10-007 · SOFTWARE** Keep live Steam/hardware evidence out of ordinary unit-test authority
      and store only sanitized, consented, provenance-locked fixtures in the repository.
- [ ] **P10-008 · RELEASE-GATE** Require contract compatibility and traceability checks on every
      phase integration branch before starting the next dependent phase.

### P10.2 Build graph and isolated staging

- [ ] **P10-009 · SOFTWARE** Update `WSGM.slnx` and restore/build ordering for contracts, SDK,
      DeviceHost, Device Lab, CLI, generators, plugins, TypeScript assets, tests, and existing
      projects.
- [ ] **P10-010 · SOFTWARE** Preserve NativeAOT for WSGM, Launch, and LogonService while producing
      explicitly JIT-capable DeviceHost/Device Lab/tool outputs.
- [ ] **P10-011 · SOFTWARE** Split publish staging by executable/component/package so the current
      flat `*.dll` installer glob cannot mix plugin, WMI, WinRT, driver, tool, or host dependencies
      into WSGM.
- [ ] **P10-012 · SOFTWARE** Give DeviceHost and each plugin deterministic package-local
      managed/native dependency resolution without current-directory or global probing.
- [ ] **P10-013 · SOFTWARE** Produce architecture/configuration-specific manifests and hashes for
      app, host, plugin, driver, helper, catalog, glyph, and TypeScript outputs.
- [ ] **P10-014 · SOFTWARE** Add build assertions that WSGM's publish tree contains no dynamic
      plugin, WMI, WinRT sensor, Device Lab, driver SDK, or reflection-only tooling assembly.
- [ ] **P10-015 · SOFTWARE** Add build assertions that plugin/package staging contains no secret,
      source capture, developer signing key, local path, unredacted evidence, or unrelated WSGM
      binary.
- [ ] **P10-016 · SOFTWARE** Integrate npm lock/install/build for Steam assets and
      importer/generator drift checks into `eng/verify.ps1` with caches, timeouts, and
      offline/reproducible release behavior.
- [ ] **P10-017 · SOFTWARE** Update `build.ps1` to build/publish all selected components in
      dependency order, stop on warning/error, and emit a signed/hashable release manifest.
- [ ] **P10-018 · SOFTWARE** Build cleanly from a fresh checkout with only documented prerequisites
      and prove a second build is reproducible apart from approved signing/timestamp fields.
- [ ] **P10-019 · RELEASE-GATE** Pass repository formatting, analyzers, generated drift, Release
      build, tests, NativeAOT publish, TypeScript, native components, staging, and manifest
      verification.

### P10.3 Installer components and dependency state

- [ ] **P10-020 · SOFTWARE** Extend the Inno installer with separately selectable/conditional core,
      DeviceHost, Device Lab/CLI, reviewed plugin, glyph, HIDMaestro, usbip-win2, HidHide, and
      helper items.
- [ ] **P10-021 · SOFTWARE** Install each component only to its approved protected/user location
      with exact ACL, owner, signer/hash, architecture, service/driver registration, and rollback
      metadata.
- [ ] **P10-022 · SOFTWARE** Keep optional drivers/providers absent when controller/device features
      do not need them and keep Device Integration off fully functional without them.
- [ ] **P10-023 · SOFTWARE** Detect compatible preexisting dependencies and preserve externally
      owned installations/configuration rather than silently replacing or adopting them.
- [ ] **P10-024 · SOFTWARE** Show explicit prerequisite, license, privilege, restart, risk, source,
      and selected-feature information before installing any optional driver/helper/provider.
- [ ] **P10-025 · SOFTWARE** Implement health checks and guided repair in the trusted component
      manager with exact version/signature/hash/ACL checks and no plugin runtime authority.
- [ ] **P10-026 · SOFTWARE** Keep missing/corrupt optional dependency failure capability-specific
      and ensure installation/repair can never block first WSGM startup or Game Mode entry.
- [ ] **P10-027 · SOFTWARE** Add silent/default install policy that does not opt users into hardware
      control or high-risk optional components without the approved product decision.
- [ ] **P10-028 · SOFTWARE** Add clean install, upgrade, repair, component add/remove, incompatible
      preinstall, wrong architecture, bad signature/hash, denied ACL, rollback, and reboot tests.
- [ ] **P10-029 · HARDWARE** Validate the retail installer on a clean supported Windows Claw image
      and an upgraded existing WSGM install without modifying unrelated MSI/HC/HidHide state.

### P10.4 Graceful shutdown, update, rollback, downgrade, and uninstall

- [ ] **P10-030 · SOFTWARE** Implement one bounded async shutdown coordinator rooted by the
      authoritative process and invoked by normal desktop-lifetime exit, update exit, logoff, and
      stop.
- [ ] **P10-031 · SOFTWARE** Make `ShellSession`/device composition explicitly disposable and await
      rejection of new commands, UI fallback, target neutralization, plugin restoration, host stop,
      and logs.
- [ ] **P10-032 · SOFTWARE** Integrate `UpdateExitWatcher` with the shutdown coordinator and return
      an explicit clean, unverified, timed-out, or failed handoff result to the updater/installer.
- [ ] **P10-033 · SOFTWARE** Make installer replacement wait for the frozen graceful deadline and
      use only the exact bounded fallback approved in `P0-049`; preserve journal/recovery state on
      failure.
- [ ] **P10-034 · SOFTWARE** Stage core/plugin/component updates atomically, verify
      signatures/hashes/compatibility, and defer active-plugin replacement until the device cycle is
      intentionally stopped.
- [ ] **P10-035 · SOFTWARE** Implement previous-version rollback for core, host, plugin, schema,
      driver/helper, glyph catalog, and Steam patch assets without loading a half-updated
      composition.
- [ ] **P10-036 · SOFTWARE** Define WSGM downgrade compatibility for config, desired profiles,
      package ABI, evidence/catalog, recovery journal, HidHide ledger, and driver/component
      versions.
- [ ] **P10-037 · SOFTWARE** Before uninstall, establish usable input, deactivate device resources,
      restore original controller mode, remove target, remove only WSGM HidHide state, and verify
      handoff.
- [ ] **P10-038 · SOFTWARE** Preserve or explicitly offer removal of user profiles/logs/evidence
      while never deleting an unresolved recovery journal or external dependency/config without
      informed choice.
- [ ] **P10-039 · SOFTWARE** Remove only WSGM-owned driver/service/helper/task/package/glyph/CEF
      state and leave MSI Center, HC, external HidHide entries, Steam files, and external RTSS
      profiles unchanged.
- [ ] **P10-040 · SOFTWARE** Add failure injection at every exit/update/install/uninstall boundary,
      process death, locked file, restart-required, rollback failure, and unresolved journal.
- [ ] **P10-041 · HARDWARE** Validate normal exit, update exit, timeout fallback, rollback,
      downgrade, uninstall, reinstall, and interrupted installer recovery on the reference unit.
- [ ] **P10-042 · RELEASE-GATE** Approve updates/uninstall only when hardware ends verified-restored
      or the operation stops with retained recovery evidence and clear manual instructions.

### P10.5 Security and trust review

- [ ] **P10-043 · SOFTWARE** Threat-model package discovery, signature/trust, DeviceHost launch/IPC,
      high-rate memory, dependencies, helpers, Device Lab, captures, CEF bridge/assets, and update
      paths.
- [ ] **P10-044 · SOFTWARE** Prove the main WSGM process exposes no generic raw hardware broker and
      a plugin cannot request arbitrary WMI/HID/IOCTL/ACPI/MMIO/MSR/serial/registry/file/shell
      authority.
- [ ] **P10-045 · SOFTWARE** Prove DeviceHost runs unelevated with current-user/session pipe ACLs,
      authenticated launch, bounded messages, no inheritable unrelated handles, and job containment.
- [ ] **P10-046 · SOFTWARE** Fuzz IPC/control/data schemas, manifest/catalog/evidence parsing,
      capture import, generator input, CEF bridge payloads, asset import, and recovery journals.
- [ ] **P10-047 · SOFTWARE** Validate package traversal/symlink/native search attacks, tampered
      assets, signer rotation/revocation, downgrade, replay, stale generation, and dependency
      substitution.
- [ ] **P10-048 · SOFTWARE** Review any fixed-operation helper against exact
      board/firmware/operation/range/rate/length/ownership/signer/hash gates and same-user caller
      safety.
- [ ] **P10-049 · SOFTWARE** Prove runtime code cannot install/repair drivers, providers, registry,
      certificates, services, tasks, dependencies, or run arbitrary installer commands.
- [ ] **P10-050 · SOFTWARE** Prove CEF code/assets cannot cross approved Steam contexts, access raw
      device/plugin/file/shell authority, or open an unauthenticated general local endpoint.
- [ ] **P10-051 · SOFTWARE** Review default logs/support bundles for secrets, serials, unique paths,
      raw memory, captures, high-rate samples, account/game data, and user content.
- [ ] **P10-052 · SOFTWARE** Document that per-plugin process isolation reduces crash/dependency
      blast radius but is not a security sandbox for deliberately malicious same-user code.
- [ ] **P10-053 · RELEASE-GATE** Close all high/critical threat findings and explicitly accept,
      document, or defer lower risks before signing a retail build.

### P10.6 Automated compatibility and fault test suites

- [ ] **P10-054 · SOFTWARE** Add schema golden/round-trip/backward/forward/unknown-field tests for
      package, capabilities, state, commands, IPC, catalog, evidence, captures, journals, and
      diagnostics.
- [ ] **P10-055 · SOFTWARE** Add host/coordinator model tests for election, launch, handshake,
      lifecycle, generations, liveness, backoff, quarantine, shutdown, and stale clients.
- [ ] **P10-056 · SOFTWARE** Add resource model tests for partial ownership, desired/observed state,
      freshness, commands, cancellation, rollback, indeterminate outcomes, conflict, and recovery.
- [ ] **P10-057 · SOFTWARE** Add Device Lab deterministic
      inventory/matching/capture/read/trial/scaffold/regeneration/pack/privacy tests with mutation
      barred from CI.
- [ ] **P10-058 · SOFTWARE** Add Claw fixture suites for exact identity, WMI/MCU, power/fans,
      physical input, mode, rumble, motion, OEM/hook, lighting, optional gates, and every malformed
      response.
- [ ] **P10-059 · SOFTWARE** Add controller suites for target translation/lifecycle/output, HidHide
      deltas, activation/handoff, local capture, source arbitration, lease fallback, and held input.
- [ ] **P10-060 · SOFTWARE** Add Steam suites for persistent CDP, bridge, patch isolation, native
      QAM, RTSS, glyph tiers/assets/coexistence, fixtures, context churn, and cleanup.
- [ ] **P10-061 · SOFTWARE** Add overlay/Settings suites for shared state, navigation, every
      page/semantic state, accessibility, scaling, source switching, disposal, and hard UI boundary.
- [ ] **P10-062 · SOFTWARE** Build a deterministic fault-injection scheduler that fails before/after
      each transactional boundary and checks invariants, owned-state cleanup, and honest
      diagnostics.
- [ ] **P10-063 · SOFTWARE** Run malformed/fuzz/property tests under strict time/memory/handle
      bounds and retain minimal reproducible failures as sanitized fixtures.
- [ ] **P10-064 · SOFTWARE** Add process-level tests with fake devices/backends/CDP/RTSS for crash,
      hang, forced kill, duplicate process, update exit, reconnect, fallback, and quarantine.
- [ ] **P10-065 · SOFTWARE** Keep all ordinary test paths isolated from real WSGM config, Steam,
      drivers, HidHide, hardware, shell takeover, and Device Lab mutation.
- [ ] **P10-066 · RELEASE-GATE** Require deterministic automated coverage of every contract, state,
      command, fault boundary, cleanup owner, and negative identity before manual release testing.

### P10.7 Reference-hardware, Steam, coexistence, and soak matrix

- [ ] **P10-067 · HARDWARE** Run exact detection positives and negatives across supported/unknown
      BIOS, EC/MCU, provider, controller modes/endpoints, sensor states, and similar MSI hardware
      where available.
- [ ] **P10-068 · HARDWARE · DESTRUCTIVE-RISK** Run the complete approved
      power/fan/mode/rumble/RGB/persistent-operation matrix with immutable evidence, restoration,
      cooldown, and manual emergency plan.
- [ ] **P10-069 · HARDWARE** Run all controller targets, HidHide, input capture, source fallback,
      output, motion, OEM, hook, target/app overrides, and duplicate-input cases.
- [ ] **P10-070 · LIVE-STEAM** Run all CEF/QAM/RTSS/glyph components on each supported Steam
      channel/build, including incompatible fingerprints and native restoration.
- [ ] **P10-071 · HARDWARE** Run WSGM-before/after-Steam, Steam absent/restart, game start/exit,
      Desktop/Game transitions, Explorer/shell recovery, suspend, hibernate, lock, unlock, and user
      switch.
- [ ] **P10-072 · HARDWARE** Run HC absent/installed/passive/active where safe, MSI Center states,
      external HidHide changes, CSS Loader glyph theme, RTSS edits, and competing debugger/tool
      cases.
- [ ] **P10-073 · HARDWARE** Run device/controller master-toggle, partial conflict, dependency loss,
      host/backend/driver/provider death, timeout, quarantine, recovery, update, rollback, and
      uninstall.
- [ ] **P10-074 · HARDWARE** Run one-hour idle/gameplay soaks, 100 device cycles where safe, 100
      suspend/resume fan cycles, 100 controller-mode/target switches, and repeated CEF context
      churn.
- [ ] **P10-075 · HARDWARE** Verify external MSI/HC/HidHide/Steam/RTSS configuration and captured
      original hardware state are preserved entry/value-for-entry/value after every relevant test.
- [ ] **P10-076 · SOFTWARE** Attach Windows/Steam/firmware/package/build identity, exact steps,
      expected/observed outcomes, sanitized logs, measurements, cleanup, and reviewer to each manual
      result.
- [ ] **P10-077 · RELEASE-GATE** Close every mandatory manual matrix cell as pass or an explicit
      documented release exclusion; never infer one firmware/Steam build from another.

### P10.8 Performance, responsiveness, and resource budgets

- [ ] **P10-078 · DECISION** Freeze numeric budgets for startup added latency,
      coordinator/host/plugin, controller/motion, backend targets, UI, CDP injection, glyphs, RTSS,
      memory, handles, and wakeups.
- [ ] **P10-079 · SOFTWARE** Build a repeatable reference-device benchmark harness that records
      per-component CPU, power, threads, wakeups, allocations, queue depth, loss, latency, and
      working set.
- [ ] **P10-080 · SOFTWARE** Measure Device Integration off as the baseline and prove no optional
      host/hook/driver communication/poll/capture/asset cost remains active.
- [ ] **P10-081 · HARDWARE** Enforce under 0.5% idle DeviceHost plus Claw plugin CPU and under 2%
      controller-plus-motion gameplay CPU, plus the frozen memory/handle/wakeup bounds.
- [ ] **P10-082 · HARDWARE** Measure HIDMaestro standard/composite target cost and investigate any
      unexplained 4–6% regression versus direct/current baselines.
- [ ] **P10-083 · LIVE-STEAM** Measure CEF discovery/attach/injection/reinjection, per-patch work,
      bridge traffic, glyph resources/observers, RTSS cadence, and context cleanup independently.
- [ ] **P10-084 · HARDWARE** Measure overlay first-open/page navigation/live previews,
      input-to-focus, physical-to-virtual input, output return, QAM command propagation, and target
      replacement latency.
- [ ] **P10-085 · SOFTWARE** Detect thread/handle/subscription/URL/style/memory/queue growth across
      soaks and fail acceptance on unexplained monotonic growth.
- [ ] **P10-086 · SOFTWARE** Document every retained repeating loop with reason, idle/active
      cadence, cancellation owner, measured cost, and event-driven alternative analysis.
- [ ] **P10-087 · RELEASE-GATE** Meet all mandatory budgets without hiding cost in another process
      or accepting unexplained regressions; publish the benchmark configuration and results.

### P10.9 Documentation and contributor/community workflow

- [ ] **P10-088 · SOFTWARE** Update architecture documentation with process topology, trust
      boundary, contracts, data/control paths, lifecycle, recovery, UI ownership, and optional
      dependencies.
- [ ] **P10-089 · SOFTWARE** Publish supported-device/firmware/Windows/Steam/RTSS/target matrices
      and distinguish experimental, scaffolded, hardware-verified, reviewed, and retail-approved
      states.
- [ ] **P10-090 · SOFTWARE** Publish user setup for Device Integration, controller management,
      dependencies, targets, glyphs, QAM/RTSS, profiles, coexistence, disable, and uninstall.
- [ ] **P10-091 · SOFTWARE** Publish recovery instructions for missing provider, unsupported
      firmware, conflict, source/backend failure, unverified handoff, quarantine, interrupted
      update, and journal.
- [ ] **P10-092 · SOFTWARE** Publish the hard UI boundary and explain why handheld controls live in
      Device while ownership/startup/trust/logging policy lives in Settings.
- [ ] **P10-093 · SOFTWARE** Publish Device Plugin
      SDK/API/versioning/package/trust/dependency/capability/lifecycle/recovery/diagnostic
      documentation with complete safe examples.
- [ ] **P10-094 · SOFTWARE** Publish Device Lab Hardware Owner and Plugin Developer guides, CLI
      workflow, privacy/redaction/export, trial review, evidence grades, regeneration, validation,
      and pack.
- [ ] **P10-095 · SOFTWARE** Publish known-implementation catalog contribution/review, protocol-fact
      provenance, firmware evidence, generated-versus-handwritten, fixture, and promotion
      requirements.
- [ ] **P10-096 · SOFTWARE** Publish glyph import/update/provenance/device-verification/selector
      fixture workflow and native Steam fallback/coexistence troubleshooting.
- [ ] **P10-097 · SOFTWARE** Publish CEF patch development/compatibility/kill-switch and
      RTSS/native-QAM maintenance workflows without encouraging arbitrary injection.
- [ ] **P10-098 · SOFTWARE** Publish safe developer commands and warnings for live Steam, drivers,
      shell takeover, services, DeviceHost, Device Lab trials, and physical hardware validation.
- [ ] **P10-099 · LEGAL** Ship final license, third-party notices, dependency/asset inventory,
      source/offer obligations, signer/hash information, and protocol/artwork provenance.
- [ ] **P10-100 · SOFTWARE** Review every user/developer document against the final shipped build
      and remove stale proposed behavior, unsupported claims, or unverified compatibility.

### P10.10 Release candidate, rollout, and definition of done

- [ ] **P10-101 · SOFTWARE** Freeze contract/package/catalog/patch versions and generate migrations,
      compatibility ranges, release manifest, checksums, signatures, and rollback artifacts.
- [ ] **P10-102 · SOFTWARE** Produce a clean signed installer and component/package artifacts from
      the reviewed commit, then verify them on a clean machine against the release manifest.
- [ ] **P10-103 · SOFTWARE** Run `./eng/verify.ps1`, `./build.ps1`, installer verification,
      generated drift, license/notices, threat, automated, manual, and benchmark gates on the
      release commit.
- [ ] **P10-104 · SOFTWARE** Exercise each master/per-patch kill switch and the safe Device
      Integration-off baseline using the exact release artifacts.
- [ ] **P10-105 · SOFTWARE** Define staged rollout, package/plugin revocation, bad-update
      containment, support evidence, known limitations, and rollback decision owners.
- [ ] **P10-106 · HARDWARE** Run final smoke on the retail-supported A2VM firmware/Windows/Steam
      matrix after signing and installer packaging; do not rely only on pre-package builds.
- [ ] **P10-107 · RELEASE-GATE** Require every shipped operation to have exact identity/range,
      truthful state/result, cancellation, diagnostics, failure isolation, restoration, and
      evidence.
- [ ] **P10-108 · RELEASE-GATE** Require Device Integration off to match the lightweight baseline
      and require every optional dependency/host/hook/patch/target to be absent or inert.
- [ ] **P10-109 · RELEASE-GATE** Require clean or honestly unverified handoff on
      exit/disable/update/uninstall and no modification of externally owned hardware/software state.
- [ ] **P10-110 · RELEASE-GATE** Require complete tests, live/hardware matrices, performance, legal,
      security, accessibility, documentation, installer, update, downgrade, rollback, and recovery.
- [ ] **P10-111 · RELEASE-GATE** Mark WSGM 2.0 complete only when every applicable `INV`, `P0`–`P10`
      checkbox is closed with reviewable evidence or explicitly removed from the release scope by
      decision.

## Design-to-backlog traceability

| Design source                                                               | Primary backlog coverage                           |
| --------------------------------------------------------------------------- | -------------------------------------------------- |
| `2.0-design.md` product boundaries, UI boundary, lifecycle, shared services | `INV-001`–`INV-020`, `P0`, `P1`, `P4`, `P9`, `P10` |
| `2.0-design.md` plugin/capability pillar                                    | `P1`, `P4`, `P5`, `P10`                            |
| `2.0-design.md` HIDMaestro/controller pillar                                | `P0`, `P1.4`, `P5.8`–`P5.10`, `P6`, `P10`          |
| `2.0-design.md` native QAM/CEF/RTSS pillar                                  | `P0`, `P1.5`, `P7`, `P10`                          |
| `2.0-design.md` final overlay pillar                                        | `P4.9`, `P9`, `P10`                                |
| `device-plugin-system-and-tooling.md` semantic/runtime/package model        | `P0`, `P1`, `P4`, `P10`                            |
| `device-plugin-system-and-tooling.md` catalog and D0–D5 workflow            | `P2`, `P3`, `P10.6`, `P10.9`                       |
| `device-plugin-system-and-tooling.md` evidence/privacy/safety/licensing     | `P0.3`, `P2`, `P3`, `P10`                          |
| `claw-8-a2vm-plugin.md` M0 characterization                                 | `P3`                                               |
| `claw-8-a2vm-plugin.md` M1–M5 production and acceptance                     | `P5`, `P6`, `P7.6`, `P9.7`–`P9.11`, `P10`          |
| `controller-glyph-integration.md` catalog/provenance                        | `P0.3`, `P8.1`–`P8.4`, `P8.11`, `P10`              |
| `controller-glyph-integration.md` Steam/Avalonia/lifecycle/acceptance       | `P7`, `P8.5`–`P8.10`, `P9`, `P10`                  |

## Completion evidence required for each checked item

When a task is checked, append or link all applicable evidence in its implementation change:

- Contract/schema version and compatibility result.
- Code and generated-output review.
- Deterministic automated test names/results.
- Sanitized live Steam or reference-hardware evidence ID when tagged.
- Performance/resource measurements when the slice owns a repeating or high-rate path.
- Failure-injection, rollback, cleanup, and recovery result.
- Security, license, provenance, dependency, or signer review when applicable.
- Updated user, developer, diagnostics, installer, and recovery documentation.
