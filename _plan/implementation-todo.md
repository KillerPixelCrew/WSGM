# WSGM 2.0 simplification and delivery plan

Status: structural architectural simplification complete in source and build on 2026-08-29; focused 2.0
feature work and attended acceptance continue below

Branch: `2.0`

Purpose: simplify the implementation without removing any fixed 2.0 feature

This is the only progress tracker. The other `_plan` files describe architecture, decisions,
requirements, exact Claw hardware facts, and glyph behavior; they are not parallel checklists.

## Non-negotiable outcome

The overhaul preserves:

- A real public Device SDK and community Device Plugins.
- Full MSI Claw 8 AI+ A2VM support.
- Device Lab GUI and CLI workflows.
- Steam Deck Composite, Xbox 360, and DualShock 4.
- Global and per-application controller targets.
- HidHide, managed WSGM UI input, output routing, and Steam Input fallback.
- Persistent Steam CEF, native QAM, RTSS, and shared performance state.
- Frametime-driven AutoTDP.
- Plugin-owned physical glyphs in Steam and WSGM.
- Home/Steam/Device/System overlay redesign.
- Safe update, uninstall, handoff, recovery, and release validation.

No simplification item may satisfy itself by deleting, disabling, or indefinitely deferring one of
these outcomes.

## What is being simplified

At the reset checkpoint, the branch had added roughly 68,000 C# lines and thirteen projects relative
to `master` while remaining far from hardware/release acceptance. The largest excess was not the
feature code; it was the framework built around a hypothetical multi-plugin ecosystem:

- Plugin ranking, selection, enablement, trust tiers, side-by-side versions, promotion, signer
  rotation/revocation, staged rollback, and quarantine.
- Separate Contracts, SDK, Host, ProbeHost, Device Lab Core, CLI, GUI, generators, and their test
  projects.
- A 147-line plugin manifest duplicating runtime hardware policy.
- Generic implementation-module composition, resource coordinators, public recovery DTOs, evidence
  IDs/locks, and generated scaffolding architecture.
- General capture/evidence/promotion schemas and a multi-stage mutation-authorization system.
- Generated requirement/code/test traceability and build gates enforcing the bureaucracy itself.
- Multiple policy/projection layers between a feature and the one UI that consumes it.

The replacement is a direct product architecture:

```text
WSGM.exe
  ├─ DeviceRuntime ── DeviceHost.exe ── one installed plugin ── hardware
  ├─ ControllerManager ── HIDMaestro + HidHide ── Deck / X360 / DS4
  ├─ PerformanceManager ── RTSS + AutoTDP
  ├─ SteamUiHost ── native QAM + physical glyphs
  └─ Overlay and Settings consume those same services

WSGM.Device.Sdk
  one AOT-safe public semantic API shared by WSGM, DeviceHost, plugins, and Device Lab
```

## Hard one-plugin rule

- [x] Count plugin package roots before manifest validation, device matching, elevation, splash,
      Explorer exit, Avalonia initialization, `ShellSession`, DeviceHost, HidHide, or virtual-target
      creation.
- [x] Zero package roots starts normal core WSGM with Device Integration unavailable.
- [x] One package root is the only candidate. Validate it; if it is broken or does not match this
      machine, keep core WSGM usable and show the device error.
- [x] Two or more package roots refuse every normal WSGM UI/shell startup and list each package name
      and absolute path. Do not rank, select, disable, or prefer one.
- [x] Recovery/setup/update/uninstall, `--restore-shell`, and a dedicated plugin-removal maintenance
      command bypass the refusal without starting device code.
- [x] `--overlay-test` remains simulated and independent of production plugins.
- [x] A developer plugin occupies the same single logical slot; there is no simultaneous installed
      release plugin plus developer plugin.
- [x] Stage updates outside every discovery path and atomically replace the sole slot.
- [x] Ensure the installer can place at most one plugin in the slot. Installing a future Legion Go
      or other plugin replaces the Claw package rather than accumulating beside it.
- [x] Add focused tests for 0, 1 valid, 1 malformed, 1 nonmatching, 2 valid, valid+malformed, update
      staging, and recovery-bypass cases.

This invariant deletes the need for package selection, preferred plugin configuration, matching
scores, trust/version ranking, multiple hosts, cross-plugin resources, and plugin-update deferral.

## Target project structure

### Keep

- `src/WSGM`, `src/WSGM.Launch`, and `src/WSGM.LogonService`.
- One AOT-safe `src/WSGM.Device.Sdk` public API.
- One `src/WSGM.DeviceHost` process.
- One `src/WSGM.DeviceLab` application with GUI and CLI modes.
- Independent `plugins/*` projects, beginning with the Claw.
- Existing native components and third-party controller source boundaries where they are required.

### Collapse or remove

- [x] Merge `WSGM.Device.Contracts` into the public AOT-safe `WSGM.Device.Sdk`; keep only types that
      are part of the plugin/wire/capability/input/glyph contract.
- [x] Move WSGM-only UI capture, controller backend, projection, source arbitration, and policy
      types out of the public SDK and back into WSGM.
- [x] Fold Device Lab Core/CLI/GUI into one application with internal services and one executable
      mode switch. Preserve both user experiences.
- [x] Remove `WSGM.Device.ProbeHost`; run explicit developer/plugin test operations through a
      clearly named DeviceHost tooling mode or directly in Device Lab where no isolation is needed.
- [x] Merge the five new device-platform test projects into one focused `WSGM.Device.Tests` unless
      a genuine runtime/toolchain boundary requires a separate project.
- [x] Remove solution/build-order assertions that only enforce the discarded project diagram:
      delete `eng/assert-build-graph.ps1` and its `eng/verify.ps1` hook; in
      `eng/check-agent-guidance.ps1` keep the load-bearing `CLAUDE.md` symlink check while the
      per-layer ownership-guidance enforcement shrinks with the scoped `AGENTS.md` files.
- [x] Delete the empty `catalog/` directory; it holds only `AGENTS.md`/`CLAUDE.md` and no data.
- [x] Keep the AOT isolation check, normal solution dependency order, and component staging checks
      that catch real binary contamination.
- [x] Reduce scoped `AGENTS.md` files to directories with genuinely different ownership or safety
      rules; keep every `CLAUDE.md` symlink correct.

Project count is not a score. A project remains separate only for NativeAOT, executable lifetime,
packaging, or public plugin ownership—not because an abstract layer can be named.

## Simplification phases

### S0 — Preserve and stabilize the checkpoint and shell baseline

- [x] Review the current uncommitted shutdown/update/uninstall changes as one coherent slice.
- [x] Review the current physical-glyph selection and Steam route/tier changes as a separate slice.
- [x] Preserve `SteamUiAssets/Source/NativeQamBootstrap.ts` as the TypeScript source seed.
- [x] Preserve the unrelated user-owned
      `native/SteamInput/crates/steam-input-recovery/src/lib.rs` edit without combining it.
- [x] Record the current build/test state honestly before structural movement; do not infer runtime
      completion from source or fixtures. Two gaps are already verified: the Steam Input glyph
      delivery stack is inert (`SteamUiSessionHost.ApplyGlyphDeliveryProfile` has zero callers, so
      tier enablement never leaves Disabled), and Device Lab mutation trials are unreachable from
      the CLI/GUI facade (`DeviceLabApplication` exposes no trial operation). S5/S9 must wire these
      features, not merely preserve them.
      Automated checkpoint on 2026-08-28: `eng/verify.ps1 -Fix` passes 1,687 managed tests and both
      native suites; `build.ps1` completes the NativeAOT publish and installer; the copied installer
      on `Z:\` matches the local SHA-256. No live Steam, shell-takeover, or attended hardware
      acceptance is claimed by that checkpoint.
- [x] Keep the completed rewrite as one coherent, source-reviewed vertical change; run the complete
      verification/build handoff before its requested final commit and exclude the unrelated Steam
      Input recovery edit.

#### S0.1 — Restore normal Explorer process semantics

Affected-device comparison on 2026-08-28 isolated a release-blocking desktop handoff defect. After
a complete WSGM boot and later desktop transition, the Explorer launched through scheduled-task
de-elevation is medium-integrity but belongs to an `<Unnamed Job>`. Mod Organizer 2 inherits that
job and receives Win32 error 5 when it requests `CREATE_BREAKAWAY_FROM_JOB` for a game process.
Cancelling at the startup splash before WSGM ends the original Explorer preserves the normal jobless
shell and the same MO2 launch works. Launching the game through Steam in Game Mode also works. Medium
integrity alone is therefore not a successful desktop handoff.

The required normal result is the initialized taskbar-owning Explorer for the current interactive
session, running at medium integrity and not associated with a job. Process Explorer's top-level
tree position is useful diagnostic evidence, but parent-tree appearance is not itself the contract.

- [x] Add narrow native job-membership inspection at the `Interop` boundary and record the current
      WSGM process, the original taskbar-owning Explorer, every launch owner, and the resulting
      Explorer before choosing a route. Treat a failed membership query as unknown, never jobless.
      Do not add `CREATE_BREAKAWAY_FROM_JOB` to WSGM's launch: that flag itself fails with access
      denied when the creator's job disallows breakaway.
- [x] Couple creation of a session-owned, disposable shell-parent context to the orderly Explorer
      exit operation. Immediately before each exit, resolve the `Shell_TrayWnd` owner rather than an
      arbitrary `explorer.exe`; verify its canonical Windows image path, current session, medium
      integrity, and jobless state; then retain a handle with `PROCESS_CREATE_PROCESS` and build the
      matching user environment for the replacement. If this capture fails while the original shell
      still exists, abort takeover and preserve desktop mode instead of making the transition
      irreversible.
- [x] Implement the narrow launch mechanism: start
      `%WINDIR%\explorer.exe` with `CreateProcessW`, `STARTUPINFOEX`, and
      `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` set to the captured canonical Explorer. The designated
      parent, not a copied token alone, supplies both the medium token and job association.
- [ ] Prove on the supported Windows 11 builds whether the retained handle remains a valid
      process-creation parent after the old Explorer exits; the API documentation does not document
      that lifetime. The implemented anchor is the normal path until this attended proof exists.
- [x] If a terminated Explorer cannot remain the designated parent reliably, start a minimal
      fixed-purpose medium/jobless shell anchor through that parent before the orderly exit. Give it
      only an authenticated per-session command to start the fixed Windows Explorer path; never
      accept caller-provided executables or arguments. Keep the anchor/session resource owned and
      disposed by `ShellSession`. It must preserve an already-valid shell, restore Explorer after
      abnormal WSGM loss once the old shell is gone, self-terminate on owner/session exit, and allow
      the next run to identify and clean up only stale WSGM-owned anchors. Use `CloseHandle`,
      `DeleteProcThreadAttributeList` plus allocation release, and `DestroyEnvironmentBlock` for
      their respective resources.
- [x] Do not substitute `CreateProcessWithTokenW` or Logon Service `CreateProcessAsUserW` merely
      because either supplies a medium user token: the child still inherits job association from
      its actual/designated creator. Either path is eligible only if its launch owner is separately
      verified jobless and the resulting shell passes the same postcondition.
- [x] Keep Explorer's use of `CreateExplorerShellUnelevatedTask`/`UnelevatedLauncher` only as a
      last-resort fail-open recovery route when no verified jobless launch owner is available. A
      scheduler-launched, job-bound Explorer restores a usable desktop but is a logged degraded
      outcome and must not satisfy normal transition success. Leave `WSGM.Launch`, Steam, and other
      one-shot de-elevation users outside this fix.
- [x] Add a transition-only asynchronous Explorer-launch API consumed by `SessionModes` and boot
      cancellation after the irreversible exit point; do not change the intentionally non-waiting
      `Panic` recovery or blocking `--restore-shell`/crash-loop recovery contracts. Replace the
      normal transition's fire-and-forget five-second elevation repair, and do not finish that
      transition until the resulting taskbar owner—not merely the PID returned at process
      creation—is initialized, belongs to the current session, has the canonical image path, is
      medium integrity, and is jobless. Adopt an already-valid Explorer instead of replacing it
      during an early splash cancellation.
- [x] Handle an installed upgrade that starts beside an Explorer contaminated by an older WSGM
      scheduled-task launch. Never end that shell without a verified jobless parent/anchor. If no
      independent verified repair owner exists, fail open, keep desktop mode, and present one
      explicit sign-out/reboot requirement; after the fresh logon shell appears, capture it before
      allowing the next takeover.
- [x] Preserve the device-verified orderly Explorer exit and one-per-session initialization timing.
      On timeout or launch failure, retain the existing fail-open priority of returning a desktop,
      but report whether the outcome is normal, degraded scheduler recovery, or failed. Log route,
      source and result PID/session, integrity, job membership, readiness, elapsed time, fallback,
      and Win32 error so a remote report identifies the containment failure directly.
- [x] Add isolated tests for shell-parent acceptance by image/session/integrity/job/query result;
      capture-before-exit ordering; existing-shell adoption; returned-PID versus taskbar-owner
      verification; wrong-session, elevated, job-bound, not-ready, timeout, fallback, and
      cancellation results; old-build contaminated-shell refusal; anchor owner/session loss and
      stale cleanup; and exact native-resource cleanup. Tests must use fakes/pure result evaluation
      and must never stop or start the live Explorer.
- [x] Update `docs/boot-and-shell.md` and `docs/elevation.md` in the implementation change. Record
      that scheduled-task de-elevation is valid for ordinary one-shot apps but cannot produce the
      normal shell process semantics required by Explorer-hosted launchers.
- [ ] Validate on the affected device: splash cancellation both before and after Explorer exit,
      repeated Game Mode-to-desktop transitions, crash/restore recovery, taskbar/tray/UWP/touch
      behavior, and MO2 launching a game from Explorer without access denied. Confirm the normal
      restored Explorer has no Process Explorer Job tab and record the original Explorer, WSGM,
      selected parent/anchor, and result job states; retain Steam/Game Mode launch as an unchanged
      regression case and identify any scheduler fallback as deliberately degraded. Also install
      over an older job-bound desktop, prove takeover is safely refused, then sign out/reboot once
      and prove the next complete cycle produces the canonical jobless Explorer.

### S1 — Remove planning and governance machinery

- [x] Remove `docs/2.0-traceability.manifest.json`, generated `docs/2.0-traceability.md`,
      `eng/update-traceability.ps1`, and its `eng/verify.ps1` hook.
- [x] Remove requirement IDs from commit/review expectations and delete the temporary legacy-ID
      appendix from `implementation-requirements.md` after the hook is gone.
- [x] Remove evidence-lock, claim-promotion, trust-promotion, and generated-operation artifacts that
      no runtime/developer feature consumes.
- [x] Update root/nested `AGENTS.md`, `docs/device-integration.md`, and `docs/device-security.md` so
      they describe the lean one-plugin architecture rather than reimposing the retired system.
- [x] Retain ordinary license notices, exact Claw hardware facts, diagnostic fixtures, and comments
      beside safety-critical code.
- [x] Keep one architecture document, one decision record, one requirements document, and this
      tracker; do not create a second exhaustive backlog.

### S2 — Implement the one protected plugin slot

- [x] Replace trust-tier roots and candidate ranking in `Core/DevicePackagePolicy.cs` with one
      protected installed slot and the hard cardinality result.
- [x] Replace side-by-side immutable version staging in `Core/DevicePackageStager.cs` with one
      temporary sibling plus atomic replacement.
- [x] Remove package selection, preferred package, staged apply, selected-device cardinality,
      version rollback selection, and quarantine policy from `Shell/DeviceCoordinator.cs`.
- [x] Remove trust-tier privilege branching/de-elevation from `Shell/DeviceHostProcess.cs`; normal
      installed plugins run as explicit trusted hardware code with WSGM's required authority.
- [x] Shrink `plugin.wsgm.json` to ID, name, version, API version, entry assembly, and entry type.
- [x] Move hardware matching and capability/dependency publication into plugin code.
- [x] Delete publisher grants, signature rotation/revocation, risk declarations, module catalogs,
      runtime per-file hash ledgers, and disabled-plugin configuration: `DevicePluginTrustTier` and
      `IDevicePackageSignatureVerifier`/`WindowsDevicePackageSignatureVerifier` in
      `Core/DevicePackagePolicy.cs`, `eng/prepare-reviewed-packages.ps1` and its `build.ps1` call,
      the manifest schema's `RiskDeclaration`/`DependencyDeclaration`/`ModuleReference`/
      `PackageProvenance` types, and the Claw package's `evidence.lock.json`; reduce its
      `PROVENANCE.md` to the D25 source revision beside the retained `THIRD_PARTY_NOTICES.md`.
- [x] Keep path containment, protected-directory enforcement, package-local dependency loading, and
      a clear install warning because they prevent concrete broken/elevation paths.

### S3 — Collapse and freeze the thin SDK

- [x] Define one exact integer API version and one public `IDevicePlugin` lifecycle:
      detect/start/stop/suspend/resume/diagnostics.
- [x] Retain only practical capability descriptors/state/commands/results, canonical controller and
      motion samples, haptic output, OEM events, glyph package/control map, and publication sink.
- [x] Collapse Contracts and SDK implementation/tests while keeping the resulting assembly AOT-safe.
- [x] Remove `ImplementationModule`, module-composition validators, evidence IDs/locks,
      `PluginResourceCoordinator`, generic resource leases, generated authoring models, and
      WSGM-only policy types from the public API.
- [x] Replace the generator/analyzer architecture with a checked-in minimal plugin template, sample
      plugin, small TestKit, manifest validator, and pack command.
- [x] Document the entire plugin author path on one page: implement, run, test, pack, install.
- [x] Validate the API against the real Claw plugin and at least one materially different synthetic
      plugin; add an abstraction only when one of those consumers needs it.
- [x] Apply an adopt-or-delete rule to the roughly thirty Contracts types whose only consumers are
      their own tests: either the runtime becomes their one real consumer (`SourceArbitration`/
      `UiCaptureState`/`ZeroOutputTrigger` in the S7 UI-capture work; `DeviceCycleTransitions`/
      `LifecycleTrigger` replacing `DeviceCoordinator`'s hand-rolled transitions) or the unused type
      and its tests are deleted together (`LeaseArbitration`, `Lifecycle/ResourceLease.cs`,
      `ModuleCompositionValidator`, `CommandDeduplicator`, `CommandAdmission`, `DeviceContinuity`,
      and `CorruptionResponse`). `OutputRouting` and the zero-consumer `IControllerBackend`/
      `IUiGamepadSource` in `Input/ControllerBackend.cs` may disappear only as abstractions after S7
      provides their required controller-output and managed-UI-input behavior through the direct
      `ControllerManager` path. Those S7 contracts are the only deliberate zero-consumer holdovers;
      the obsolete architecture/test-only contracts were deleted with their tests.
- [x] Move the entirely test-only `Sdk/Authoring` folder (`GlyphProfileBuilder`,
      `IPluginObservationAnalyzer<,>`, `PluginProjectTemplate`, `PluginSourceGenerationRequest`)
      into Device Lab or delete it, and decide where `Sdk/Testing` (TestKit) ships so the merged
      SDK assembly referenced by NativeAOT WSGM stays AOT-clean; `eng/check-aot-isolation.ps1`
      remains the proof.
- [x] Collapse the three stacked glyph import layers in `Contracts/Glyphs`
      (`GlyphProfileReader`/`GlyphProfileValidator`, `GlyphProfileImporter`,
      `GlyphPackageImporter`) into one loader with one error list.
- [x] Fold the misnamed `tests/WSGM.Device.Sdk.Generators.Tests` into the merged device test
      project; no Roslyn generator exists anywhere in `src/` — generation is string templating.
- [x] Consolidate the device-platform source-generated JSON contexts (`DeviceWireJsonContext`,
      `DeviceContractsJsonContext`, `GlyphProfileJsonContext`, `RecoveryJournalJsonContext`,
      `DeviceCoordinatorDiagnosticsJsonContext`, `DevicePackageJsonContext`,
      `DeviceLabJsonContext`, `DeviceLabCompactJsonContext`) down to one per remaining device
      assembly; unrelated established WSGM mechanism contexts remain separately owned.

### S4 — Slim DeviceHost and runtime coordination

- [x] Reduce DeviceHost to one package load, one lifecycle, one pipe connection, one shared input
      ring, bounded shutdown, and package-local dependency resolution.
- [x] Retain the measured fixed shared controller/motion ring (42 ns/sample in the current benchmark)
      and keep it single-purpose.
- [x] Replace min/max protocol negotiation and schema fingerprints with exact API/wire equality;
      delete `Contracts/Ipc/ProtocolNegotiation.cs` with it.
- [x] Use one cycle generation where stale reconnect rejection is required; remove duplicate
      host/device/context generations that do not prevent a demonstrated stale action
      (`Contracts/Lifecycle/DeviceGeneration.cs` plus `DeviceCoordinator`'s separate host and
      device generations).
- [x] Remove generic command idempotency caches (`CommandDeduplicator`, `CommandAdmission`). Never
      automatically retry an uncertain hardware write; return the uncertainty to the owning
      plugin/service.
- [x] Reduce host restart policy to one or two bounded retries, then fault Device Integration for the
      run with manual retry and clear diagnostics. Keep exactly one policy — today
      `RestartPolicy.Evaluate` and `DeviceCoordinator`'s own `_hostFaults` window coexist.
- [x] Keep the kill-on-close job and shutdown timeout; remove periodic enterprise resource policing
      (the `DeviceHostClient` handle/working-set watchdog).
- [x] Keep the `--settings` owner-request pipe but drop its bespoke third framing: reuse the host
      pipe's `WireFormat` and one JSON context in `Shell/DeviceCoordinatorDiagnostics.cs`.
- [x] Reduce `DeviceCoordinator` to composition/lifecycle, `DeviceCapabilityRouter` to direct
      capability state/command routing, and `DeviceOverlayBridge` to view-ready projections.
- [x] Ensure `ShellSession` owns and disposes each retained resident service exactly once.

### S5 — Simplify Device Lab without cutting workflows

- [x] Preserve doctor, inventory, capture, inspect/compare/correlate, fixture extraction, scaffold,
      glyph import, local plugin run, validate/test, and pack in the combined Device Lab app.
- [x] Keep the Hardware Owner GUI flow and Plugin Developer GUI flow as thin views over the same
      operations; do not require every obscure CLI switch to have a separate screen.
- [x] Use one current capture schema and one obvious redaction pass; remove compatibility families,
      evidence promotion, claim ledgers, and immutable evidence locks.
- [x] Replace the versioned implementation-module catalog and weighted matching with straightforward
      known-device fingerprints and explained exact mismatches; fold
      `BuiltInKnownImplementationCatalog`'s five entries (all one `ms-1t52` family) into that data.
- [x] Replace generated source architecture with checked-in templates and token replacement.
- [x] Run read/test operations through ordinary compiled code. Replace trial hashes, receipts,
      authorization snapshots, experiment leases, and mutation fault frameworks — the
      `DeviceLab.Core/Trials` folder with `BoundedMutationTrialRunner`, its five zero-implementor
      trial-transport interfaces, `MutationTrialFaultHarness`, and `MutationTrialJournal` — with
      one explicit attended action plus plugin-owned snapshot/readback/restore wired through
      `DeviceLabApplication` and both front ends.
- [x] Delete lab machinery with no production caller unless a preserved workflow adopts it:
      `ClaimStatePolicy`, the `Evidence` promotion/lock types, `DeviceLabSchemaReader<T>`,
      `DevicePluginScaffoldRegeneration`/`DevicePluginScaffoldVerifier`, `ReadProbeSelector`, and
      `ResourceCompatibility`.
- [x] When absorbing ProbeHost, flatten `ReadProbeProfiles.cs` — eight marker interfaces (two with
      zero implementors) over six concrete probes — into plain probe classes in one registry.
- [x] Settle one CLI verb set: `doctor`, `inventory`, `candidates`, `probe-read`, `capture`,
      `inspect`, `compare`, `correlate`, `fixture`, `scaffold`, `glyph`, `validate`, `test`, and
      `pack`; update the root `AGENTS.md` safe-command table in the same change so documentation and
      tool never disagree.
- [x] Retain privacy redaction, explicit output paths, and the prohibition on live WSGM data because
      those prevent actual data loss/leaks.
- [x] Ship Device Lab as an optional developer-tools component; normal WSGM runtime never depends on
      it.

### S6 — Simplify the Claw plugin around hardware services

- [x] Preserve every exact identity, WMI address, fan layout, MCU command, topology rule, input map,
      rumble byte, gyro limit, OEM event/chord rule, RGB address/zone, and unresolved hardware item
      in `claw-8-a2vm-plugin.md`.
- [x] Replace generic `IPluginResource` objects and public resource-state machinery with direct
      power, fan, controller, motion, OEM, and RGB services publishing independent availability;
      retire `Sdk/Lifecycle/PluginResourceCoordinator.cs` and the eight `ClawResourceBase`
      wrappers in `ClawResources.cs` with it.
- [x] Replace nested capability/command/lease/registry gates with one plugin command entry that
      validates current identity, ownership, range, and device state immediately before the write.
- [x] Keep one serializer per actual vendor transport, not per abstract capability layer.
- [x] Replace host-owned plus plugin-owned recovery journals with one small plugin-owned record of
      temporary state the plugin actually changed and could not restore; today
      `Contracts/Lifecycle/RecoveryJournal.cs`, `DeviceHost/RecoveryJournalStore.cs`, and the
      plugin's `ClawRecoveryJournal.cs` coexist.
- [x] Keep persistent RGB desired state separate from temporary restoration.
- [ ] Complete remaining power/fan/charge, controller, gyro, OEM suppression, rumble, RGB effect,
      glyph, suspend/resume, hotplug, conflict, and handoff hardware work.
- [ ] Verify that Device Integration off leaves no Claw activity and another manager can take over
      without WSGM killing or reconfiguring it.

### S7 — Complete controller management directly

- [ ] Finish a technically acceptable virtual-controller backend. Scope is fixed: nothing here may be
      cut. **Backend decided 2026-08-29 — VIIPER, not HIDMaestro.** Full evidence in
      `third_party/controller/README.md`. VIIPER's `device/steamdeck` natively carries the whole
      Neptune frame including all four rear controls and stick touch (bit map agreed exactly by
      VIIPER, HandheldCompanion, and `hhd`: L5 15, R5 16, L4 41, R4 42, pad touch 19/20, stick touch
      46/47), and it rides `usbip-win2`'s already-pinned signed kernel driver, so the missing-fields
      and driver-reproducibility gates both disappear.
      - The three fixes merged into `Valkirie/VIIPER` are carried onto corando98's `viiper-controller`
        branch, tracked as a patch in `third_party/controller/viiper/`: PR #4 (SDL3 `ucLength` 64) was
        already present; PR #3 (stick-Y clamp) and PR #2 (placeholder endpoints must stay pending) are
        applied, #2 adapted to this branch's `device.BlockUntilDeadline`. The patch also repairs a
        stale quaternion assertion the branch left failing, so the package has a green baseline.
        Built and tested with Go 1.27.0 on the reference Claw: `go build ./...` succeeds tree-wide and
        `go test ./device/steamdeck/...` passes. `xboxelite2`, `xboxgip`, and `internal/server/api`
        fail before any WSGM patch and are the accepted baseline.
      - **The one real cost is CPU and it must be fixed, not accepted.** VIIPER driving a virtual Deck
        in HandheldCompanion measured a constant 6–8%. Mechanism identified: the Deck does not declare
        `NaksWhenIdle`, so all three streaming endpoints take the keepalive path and replay the last
        report every `bInterval` forever — and two of them (keyboard, mouse) are descriptor
        placeholders carrying nothing, ~200 wasted completions/second, which PR #2 removes. Whether
        the controller endpoint should NAK when idle needs measurement against a real Steam claim, not
        an assumption: a real Deck appears to stream continuously.
      Superseded HIDMaestro analysis, kept because it stays accurate and the component remains pinned
      as the alternative:
      - **Rear controls close without any upstream change.** `HMButton` already carries four paddles;
        only the profile's 64-bit mask names two of them. The missing positions are sourced from
        `hhd`'s virtual Steam Deck (the implementation HIDMaestro's own profile cites): L5 at bit 15,
        R5 at 16, **L4 at 41, R4 at 42**, cross-checked against three positions the two projects
        already agree on. WSGM ships its own profile naming all four and loads it with
        `LoadProfilesFromDirectory`. A profile is data; shipping data is not forking HIDMaestro.
      - **Stick touch does not close that way.** `HMGamepadState` has no capacitive stick-touch field
        and `hhd` does not emulate one, so there is no bit to name. The Steam Deck target must
        declare that truthfully instead of letting `VirtualTargetProfile.Consume` pass a control the
        backend then drops silently. Not a Claw blocker — it has no capacitive sticks.
      - **The remaining gate is installation, not capability.** v1.7.0 is UMDF2 user-mode with a
        locally trusted self-signed certificate and no `testsigning`, so the kernel-driver and
        EV-cert concerns are gone. Driver and certificate installation still must not happen at
        runtime (INV-020); it belongs to the installer as an explicit, user-approved, elevated step
        that verifies the locked component identity first.
      Implemented since: `Input/ViiperControllerBackend.cs` replaces the HIDMaestro stub as the
      production `IHidBackend`, over `Interop/NativeViiper.cs` — a flat blittable C ABI the NativeAOT
      executable binds directly, so no helper process is needed. `Input/SteamDeckNeptuneReport.cs`
      packs the 64-byte frame, and the rumble return path is wired through VIIPER's feedback callback.
      `eng/build-viiper.ps1` checks out the pinned revision, applies WSGM's patches, runs the Deck
      tests, builds `libviiper.dll`, and stages it; `WSGM.csproj` ships it beside the executable.
      The binding is verified against the real library on the reference Claw, not merely compiled:
      init, bus create, device add, fast handle, a 64-byte frame submit, remove, and shutdown all
      succeed. Licensing is settled — both projects are GPL-3.0.
      Remaining: `viiper_device_attach` needs usbip-win2 installed, so the installer work below is
      the next step; then measure idle CPU with and without `VIIPER_NAK_IDLE` against a Steam client
      that has actually claimed the device, and open `DeviceFeatureAvailability.ControllerManagement`.
- [x] Define every control the virtual targets can express in the canonical model, once, rather than
      extending it each time a plugin needs a button. The API version is an exact integer match
      across WSGM, DeviceHost, Device Lab, and every installed plugin, so a later addition is a
      breaking rebuild for every plugin that exists — and the target set is fixed and its control
      surface knowable today. `CanonicalButtons` gained trackpad touch and click, and quick access;
      `CanonicalControllerSample` gained the two touch contacts with position and force plus both
      stick forces. `CanonicalSampleCodec` is version 2 at 128 bytes.
      `SteamDeckNeptuneReport.Supported` shows the Deck target now drops nothing the model defines,
      and `VirtualTargetProfile.Consume` strips the new controls for targets that lack them rather
      than remapping them.
- [x] Keep one `ControllerManager` owning Steam Deck Composite, Xbox 360, DualShock 4, target
      replacement, output, HidHide, local UI capture, and fallback. `Shell/ControllerManager.cs` is
      that owner: selection, target lifetime and replacement, owned-delta HidHide, reference-counted
      UI capture with held-control suppression, zero-output triggers, the make-safe handoff, and the
      truthful `UiInputSource` fallback projection. It is the real consumer S3 held `UiCaptureState`,
      `SourceArbitration`, `OutputRouting`, and `ManagedControllerRouter` open for. It is constructed
      and owned by `DeviceCoordinator`, which feeds it canonical samples, starts it from the plugin's
      own physical-identity publication, and routes both teardown paths through its make-safe
      sequence. Production behaviour still waits on a usable backend (first item).
- [x] Use one existing/merged running-application monitor to resolve both per-app controller target
      and performance/profile identity. `ControllerManager.ApplyRunningApplicationAsync` consumes the
      same `RunningApplicationTargetSnapshot` as `RunningApplicationPerformanceCoordinator`; no second
      monitor exists.
- [x] Store controller selection directly as global default plus executable override; remove generic
      desired-state/selection projections that have no second policy. `ControllerTargets` moved from
      the per-device `DeviceDesiredProfile` up beside `ControllerTarget` on `DeviceIntegrationConfig`,
      and `ControllerTargetSelection.Resolve` is the whole policy. The five-layer
      `DeviceDesiredStateResolver` stays for semantic capabilities, which genuinely vary by power
      state and profile.
- [x] Preserve owned-delta HidHide and the two-phase make-safe handoff because they prevent real
      controller loss/duplicate input.
- [x] Unify the two handoff sequencing vocabularies — `Shell/ControllerReleaseOrdering.cs`
      (`ControllerReleaseOrder`, ten states) and `Contracts/Lifecycle/ControllerHandoff.cs`
      (`ControllerHandoffStep`) — into the one make-safe sequence `ControllerManager` owns.
      `ControllerReleaseOrdering.cs` is deleted; `Shell/ControllerMakeSafe.cs` states the sequence in
      the wire vocabulary and keeps the two orderings that matter (no target removal before the
      physical release concludes, no HidHide removal before the target is gone) as explicit guards.
- [ ] Finish managed overlay/taskbar/Settings navigation, held-control suppression, target
      neutralization, and make-before-break SDL/Steam-lease fallback. Held-control suppression,
      target neutralization, and the source projection are implemented and tested in
      `ControllerManager`; the overlay/taskbar/Settings surfaces still consume SDL directly and are
      not yet switched onto `UiSampleReceived`/`ClaimUiAsync`.
- [x] Complete physical rumble/haptic return and zero-output cleanup. The plugin now publishes
      `HapticCapabilities` alongside its physical identities, so `Shell/DeviceHostHapticSink.cs`
      reports what the device can actually drive rather than guessing; the Claw declares its two
      motors, no trigger haptics, and the 250 fps its own 4 ms write gate allows. Ownership is
      withdrawn the moment the plugin stops publishing, an unowned sink clamps every channel to
      silence, and stopping sends an explicit silent frame because the plugin latches its last rumble
      values. Zero-output triggers and neutralize-on-capture were already in `ControllerManager`.
      Felt rumble on the reference unit stays in the attended item below.
- [ ] Run all target, per-app, slot, duplicate-input, suspend/resume, host-fault, and external-owner
      acceptance on the reference unit.

### S8 — Complete Steam UI, RTSS, and AutoTDP directly

- [ ] Keep one persistent `SteamUiHost` connection/reconnect owner and one TypeScript bootstrap.
- [ ] Collapse class/version/tier machinery where a direct component-local probe/apply/remove/health
      implementation is sufficient; keep failures independent and native Valve fallback intact.
- [x] Finish the deterministic TypeScript build with pinned dependencies and one drift/hash check,
      without introducing a general asset-manifest governance system. `eng/build-steam-assets.mjs`
      compiles the source with pinned `typescript`, strips everything above the `@wsgm-bundle-start`
      marker, formats the result with the repository's pinned Prettier so it is byte-stable across
      machines, and writes both the asset and its catalog hash. `--check` rebuilds into memory and
      compares, so neither a source edit that was never compiled nor a hand edit of the generated
      file can ship; `eng/verify.ps1` runs it. Type-stripping only — no bundler, no minifier — so the
      shipped asset stays reviewable beside the page it is injected into.
      The seed had never been compiled and did not type-check. Fixing it found four genuine
      correlated-null defects in the frame-limit validator, where `maximumFps` was compared against
      without establishing it was non-null; the generated asset is otherwise identical to the
      hand-maintained one apart from stripped blank lines. Implicit `any` stays permitted at the one
      boundary that reaches Steam's minified React internals, which have no types to borrow.
- [x] Hoist the identical private CDP evaluate/`ok`-parse helper duplicated in
      `NativeQamComponentPatches.cs`, `SteamInputHandheldGlyphPatch.cs`, and
      `SteamInputGlyphDeliveryPatches.cs` into one shared helper beside `SteamUiPatchManager`.
      `Core/SteamUiPatchEvaluation.cs` now owns the parse, the `ok` check, `IsOne`, and the bounded
      diagnostic. The three copies had drifted: two discarded the page's own answer and reported only
      the caller's fallback text whenever the returned shape carried no `error` string, which is
      exactly the case a remote log needs. All patches now read the page's answer.
- [ ] Retain existing CEF library/card/Wi-Fi/download behavior while completing native TDP, frame
      limit, RTSS overlay level, performance/profile, controller-target, and AutoTDP projections.
- [x] Add a user-owned toggle that launches the complete Steam client unelevated, not only
      individual games or helper processes. Keep the current integrity-matched launch as the
      default, apply the choice consistently to cold start and restart paths, and log the selected
      Steam launch integrity for remote diagnosis. `AppConfig.SteamLaunchUnelevated` (default off,
      Settings → Steam) routes the cold start through `UnelevatedLauncher` when WSGM is elevated;
      both the cold start and the auto-relaunch pass through `SessionModes.StartBigPicture`, so the
      choice cannot apply to one and not the other. Every launch logs
      `Steam launch integrity: …`, including a requested de-elevation that was unavailable.
      Live acceptance stays in the S8 validation item below.
- [ ] Keep RTSS controls working when Device Integration is off.
- [x] Reduce performance state to the verified current frametime/metrics required by overlay, QAM,
      diagnostics, and AutoTDP; do not build a general metrics platform. `Core/RtssFrametimeReader.cs`
      reads exactly one thing — the per-application mean frametime — from RTSS's own shared memory.
      The `RTSSSharedMemoryV2` layout was confirmed against a live RTSS 2.21 on the reference Claw on
      2026-08-29 rather than copied from a header; the offsets and the tick-based mean are recorded in
      `docs/rtss.md`.
- [x] Implement one deterministic AutoTDP controller: fast rise on sustained misses, settled
      one-step descent, last-good restore, cap/menu handling, transient heavy-scene recovery,
      per-app/context learning, manual pause, one in-flight write, and exact stop restore.
      `Core/AutoTdp.cs` is the pure policy and `Shell/AutoTdpService.cs` the binding that decides
      nothing: renderer selection against the shared running-application identity, the
      `PowerSustainedLimit` capability and its plugin-published range, one write at a time, and
      restoration on stop/disable/dispose. `DeviceIntegration.AutoTdpEnabled` (Settings → Device
      ownership) is the user switch.
- [x] Build replay around real trace shapes and add sophistication only when a recorded trace defeats
      the simple controller. `AutoTdpReplay` runs a recorded trace through the controller with no
      device involved; the 20 controller tests are written as trace shapes (sustained miss, settled
      descent, rejected probe, capped menu, transient heavy scene, telemetry gap, context change).
- [ ] Validate live Steam context churn, focus/navigation, RTSS external edits/restart, AutoTDP
      games/menus/scenes/suspend/manual override, performance, and cleanup. Partly done on the
      reference Claw on 2026-08-29: the shipping `RtssFrametimeReader` opens the live
      `RTSSSharedMemoryV2` mapping from its own process and correctly reports no samples with nothing
      hooked, and the layout is now an executable specification. Still needs a rendering game for a
      real frametime read, and the whole AutoTDP loop needs power writes, which are hardware mutation
      and stay attended.

### S9 — Complete physical glyphs as static plugin data

- [ ] Keep the plugin-owned manifest, artwork, logical control map, source revision, and required
      license notice.
- [ ] Prefer fixed reviewed PNG assets for runtime presentation where they can replace the custom
      thousand-line SVG parser without losing quality; retain narrowly normalized SVG only where
      needed.
- [ ] Reduce validation to path locality, known IDs, format, dimensions, size, and references.
- [ ] Keep WSGM-owned Steam selectors, context-local delivery, cleanup, CSS Loader coexistence, and
      native fallback.
- [ ] Finish Automatic/Native/manual selection, graphical preview, input test, OEM rows, and
      navigation hints from one shared physical map.
- [ ] Finish stable resource mappings, controller diagrams, exact inline mappings, and supported
      capability hiding without a generic patch-tier framework: replace the
      `SteamInputGlyphTierPatch` abstract base (six abstract members) and its four subclasses with
      direct per-group patches. **The mechanism is settled: physical glyphs are CSS, exactly as
      CSSLoader's Handheld Controller Glyphs theme already does it.** That theme is checked out at
      `_ref/handheld-controller-glyphs` (it covers the MSI Claw) and the loader at
      `_ref/SDH-CssLoader`; `docs/steam-cef.md` records the details. Nothing patches Steam's data
      model and nothing needs a new framework:
      - Glyph replacement is `img[src="/steaminputglyphs/<name>.svg"] { content: url(<asset>) }`,
        keyed by the stable Valve basename, with several Valve names mapping onto one control.
      - Inline Valve SVG is the same for glyphs Valve draws as `<svg><path d="…">` rather than an
        `<img>`: match `:has(svg path[d="…"])`, hide the inner `svg`, paint the asset as a
        background. Keyed by the `d` attribute.
      - Capability hiding is `display: none` on the row carrying the absent control's glyph, wrapped
        in an `@container style(--hiding-enabled: 1)` query so it stays switchable.
      - The device half is only custom properties — `themes/msi/claw.css` is seventeen lines of
        `--button-*-image` — which is the shape the plugin-owned glyph package should produce.
      Done: the four JS mapping-namespace tiers are deleted and replaced by `Core/SteamGlyphCss.cs`
      (the emitter) plus `Core/SteamInputGlyphStylePatch.cs` (one owned `<style>` appended to
      `document.head`, carrying WSGM's `wsgm-glyph-style` class, removed only by that class and never
      touching a `.css-loader-style` node). `SteamUiSessionHost` registers one glyph patch with one
      switch instead of four. The ownership split is enforced by construction: **WSGM owns the
      method** — Valve resource names, selectors, stylesheet shape, injection — and **the plugin owns
      the glyphs**, with every emitted image coming from its imported profile as a data URI. WSGM
      ships no handheld artwork and no per-device stylesheet.
      Live-verified on the reference Claw on 2026-08-29 against the running client: both
      build-coupled classes are present in this Steam build, the full install/verify/remove cycle
      runs, all five emitted rule shapes parse (including both `:has()` selectors carrying the long
      Steam-logo `d` attribute), the controller-image custom property resolves, and removal leaves no
      owned node and touches no `.css-loader-style` node. That run also settled the probe design: the
      classes match zero live elements unless a controller settings view is open, so compatibility is
      read from the parsed stylesheets rather than from the DOM.
      Remaining: visual acceptance with a real plugin profile on a controller settings screen
      (artwork, orientation, scale), and re-verifying the two class names after a Steam client
      update — the probe already fails closed when either disappears.
- [x] Wire the dead activation path: `SteamUiSessionHost.ApplyGlyphDeliveryProfile` has zero
      callers, so tier enablement never leaves Disabled and every delivery patch is inert. Drive it
      from `DeviceCoordinator` when the active glyph profile loads or changes.
      `ShellSession.ApplyGlyphConfig` now applies both halves — the route selector and the resolved
      profile — from the coordinator's `PhysicalGlyphSelectionSnapshot`, driven by the new
      `DeviceCoordinator.PhysicalGlyphProfilesChanged` event and by config reload. Only the two
      live-approved tiers are requested; the inline-SVG and capability-hiding tiers stay fail-closed
      until their fingerprints exist, so enabling them would produce a permanent patch failure rather
      than a feature.
- [x] Merge `SteamInputHandheldGlyphPatch` — a full probe/apply/verify/remove patch that installs
      only a route-predicate object — into the delivery patch that consumes it. **Deleted rather than
      merged: once delivery became CSS, nothing consumed it.** The stylesheet is matched by Steam's
      own selectors and never reads a WSGM `window` object, so the selector patch was injecting a
      property into SharedJSContext that nothing read, on every session, with its own build-coupled
      probe to keep working. Removing it drops injected surface, a cleanup path, and a compatibility
      gate. `ApplyGlyphSelector` and `ApplyGlyphDeliveryProfile` collapse into one `ApplyGlyphs`,
      because there is now one thing to install.
- [x] Replace the `"wsgm.glyph.selection"` pseudo-capability that `DeviceOverlayBridge` synthesizes
      and special-cases in `InvokeAsync` with a direct overlay command to
      `DeviceCoordinator.CyclePhysicalGlyphSelectionAsync`, keeping one dispatch path for real
      capabilities.
- [ ] Visually accept the A2VM profile, OEM sides, and M1-left/M2-right orientation at supported
      scales.

### S10 — Finish the overlay without duplicating services

- [ ] Complete Home, Steam, Device, and System navigation with Back/focus/scroll restoration.
- [ ] Migrate every existing Home/Steam/System action to its page without reimplementing its service.
- [ ] Complete Device Overview, Profiles, Power/Thermals, Controller/Motion, OEM,
      Lighting/Features, Glyph Preview/Input Test, and Diagnostics/Recovery.
- [ ] Bind every overlay and QAM control to the same direct runtime service.
- [ ] Keep Settings limited to startup/integration/controller ownership/logging/update configuration
      and owner-process requests.
- [ ] Validate controller, touch, keyboard, scaling, accessibility, themes, cancellation, disposal,
      and responsiveness on the handheld.

### S11 — Simplify build, installer, tests, and release

- [x] Make `WSGM.slnx`, `eng/verify.ps1`, and `build.ps1` reflect the collapsed projects and one
      compatible component composition.
- [x] Keep NativeAOT publish proof and prevent JIT/plugin/tool dependencies from leaking beside
      `WSGM.exe`.
- [x] Stage only App, DeviceHost, the one plugin, optional Device Lab, and required controller
      dependencies; remove package catalogs and side-by-side versions.
- [ ] Install everything VIIPER needs, as an explicit user-approved elevated installer step — never
      from the running shell (INV-020). Detail in `third_party/controller/viiper/README.md`.
      Done: `WSGM.iss` declares a `controller` component and ships `libviiper.dll` with its notices
      and header, every entry `skipifsourcedoesntexist` because `build.ps1` skips the library loudly
      when the release machine lacks a Go toolchain or C compiler.
      Remaining: installing the **usbip-win2 driver**, which is the one step between here and a
      working virtual controller — `viiper_device_attach` needs the VHCI device it provides.
      `eng/acquire-controller-dependencies.ps1` already downloads the pinned installer and verifies
      its hash and Authenticode signature; nothing yet stages it into `publish/` or runs it from
      setup. It must run only for the `controller` component, re-verify the locked identity first,
      and leave a declined or failed install as a machine that runs WSGM normally with controller
      management unavailable.
- [x] Standardize setup and managed maintenance on the fixed `.staging`/`.previous` siblings,
      reconcile the prior `.installed.previous` name, and serialize stop/recheck/publication with
      the exact global package-slot and hardware-owner objects so no DeviceHost can race replacement
      or uninstall. Preserve the initially observed runtime mode across setup refusal/retry/cancel
      through an installer-tagged service restart; suppress rollback-shell hardware admission when
      prior DeviceHost exit is unverified and hand the global marker to that restored process without
      an unreserved gap.
- [x] Keep legal notices for shipped code/assets and remove provenance/promotion metadata not needed
      by licensing; retire `eng/write-artifact-manifests.ps1`'s per-component hash manifests unless
      a retained staging check consumes them — the final installer SHA-256 handoff is enough.
- [x] Simplify tests toward known parsing, bounds, lifecycle, cleanup, host/crash, controller,
      Steam/RTSS/glyph, AutoTDP replay, UI parity, and regressions.
- [x] Remove exhaustive schema compatibility, trust-tier, evidence, ranking, promotion, and
      theoretical fault-matrix tests with the code they served.
- [x] Finish one shutdown coordinator and one installer result channel for normal/update/logoff/
      uninstall, preserving the current uncommitted work where correct. Known defects to fix in
      that review: the outer Update budget in `ApplicationShutdownCoordinator.BudgetFor` (10 s)
      equals `DeactivationBudget.Update.Total` with CEF/RTSS teardown still to run after it,
      guaranteeing `TimedOut`; `ApplicationShutdownRequest` precedence is enum-declaration-order
      max-wins, so a logoff downgrades an installer-requested Update to the 5 s SessionEnd path;
      and Uninstall reuses `DeviceDeactivationReason.IntegrationDisabled`.
- [x] Pass one outer shutdown deadline down instead of parallel budget tables; delete
      `DeactivationBudget` from `Contracts/Lifecycle/RestartPolicy.cs` once the deadline flows.
- [x] Simplify the installer result channel: `UpdateExitWatcher` creates eight per-outcome named
      events and `WSGM.iss` decodes them, yet all four outcomes fall through to the same
      unconditional force-stop. Either branch installer behavior on the outcome or reduce to one
      completion event plus the logged compact result; keep the D22
      clean/unverified/timed-out/failed report either way.
- [x] Deduplicate the request-plus-`Shutdown()` block in `Program.RequestInstallerExit` and
      `ShellSession.OnSessionEnding`; drop the dead `_messageWindow ?? MessageWindow.Create()`
      branches; re-evaluate the two-holder `SessionNotificationLease` refcount against simply
      registering session notifications for the `MessageWindow`'s lifetime.
- [x] Retarget tests that freeze constants (exact budget seconds, event-name suffix strings) at the
      behavior they exist to protect.
- [ ] Validate atomic one-plugin update, rollback by reinstall, uninstall, external-state
      preservation, and recovery-first bypass.
- [ ] Run the focused automated suite, NativeAOT build, live Steam matrix, attended Claw/controller/
      AutoTDP/glyph/transition tests, and meaningful soak/performance checks.
- [x] Run `eng/verify.ps1 -Fix`, `build.ps1`, copy the newest installer to `Z:\`, and verify matching
      SHA-256 hashes for the final handoff.

## Current feature state to preserve through simplification

| Area | Existing useful work | Still required |
| --- | --- | --- |
| SDK/Host | One public AOT-safe SDK, exact wire contract, one-package host lifecycle, shared ring, focused tests | Feature-specific controller/glyph consumers and attended fault/recovery acceptance |
| Device Lab | One GUI/CLI app preserving inventory, capture, probes, scaffolding, testing, glyph import, validation, and packing | Attended reference-device acceptance for the explicit hardware action |
| Claw | Direct hardware services, one command gate, one small plugin journal, exact WMI/HID facts and fixtures | Remaining hardware features plus attended acceptance/restoration |
| Controller | `ControllerManager`, the unified make-safe sequence, a canonical model covering every control the targets express, and a VIIPER backend verified against the real library | Installing usbip-win2 so `device_attach` works, idle-CPU measurement, overlay consumption of the managed UI source, attended acceptance |
| Steam/QAM | Persistent CDP, bootstrap, semantic QAM foundations, one shared patch-evaluation helper, a deterministic TypeScript build with a drift gate, user-owned unelevated client launch | Direct component implementation, the AutoTDP projection, live matrix |
| RTSS | Discovery/profile/control plus a device-verified shared-memory frametime reader with the layout as an executable specification | QAM/overlay binding of the frametime state, and a read from an actually rendering game |
| AutoTDP | One pure controller, trace replay, the session binding, and the user switch | A QAM/overlay surface, and live games/menus/scenes/suspend/manual-override acceptance |
| Glyphs | Plugin-owned artwork delivered as CSS through one owned stylesheet, live-verified against the running client | Static package simplification, preview and input test, visual acceptance with a real profile |
| Overlay | Navigation and partial Device projections | Complete destinations and handheld acceptance |
| Shutdown | One deadline-driven coordinator and one installer completion channel | Live update/logoff/uninstall recovery acceptance |
| Shell/Desktop | Explorer-first takeover plus a session-owned jobless shell anchor and verified-result transition API | Attended Process Explorer/MO2/repeated-transition acceptance |

## Immediate queue

Only this list drives the next implementation work:

- [x] **Q01 — Stabilize the uncommitted checkpoint** into separately reviewed shutdown, glyph/Steam,
      and TypeScript slices while preserving the unrelated Steam Input recovery edit.
- [x] **Q02 — Restore normal Explorer process semantics** through a captured canonical shell
      parent or live jobless anchor, await the complete shell result, and add focused tests,
      diagnostics, and docs. The attended Process Explorer/MO2 matrix remains the explicit S0.1
      release gate because shell takeover must not be run unattended.
- [x] **Q03 — Remove generated traceability/governance** and update repository instructions/docs to
      the lean architecture.
- [x] **Q04 — Implement the one-plugin startup invariant** and protected atomic slot before further
      package/runtime work.
- [x] **Q05 — Collapse Contracts/SDK and shrink the manifest/API** against the real Claw consumer.
- [x] **Q06 — Slim DeviceHost/Coordinator and merge Device Lab projects** without dropping GUI/CLI
      workflows.
- [x] **Q07 — Simplify the Claw plugin internals** around direct services and the small plugin-owned
      recovery record. Remaining capability completion and attended hardware acceptance stay in S6.
- [ ] **Q08 — Finish controller targets, per-app selection, UI capture, output, HidHide, and fallback.**
      The one `ControllerManager`, its `DeviceCoordinator` wiring, the unified make-safe sequence,
      direct global-plus-override target selection, and the wire-published haptic return path are
      complete with focused tests, diagnostics, and docs. **The backend is no longer blocked**: it is
      VIIPER, implemented, and verified against the real library — see S7. What remains is installing
      usbip-win2 so `device_attach` works, the idle-CPU measurement, switching the overlay surfaces
      onto the managed UI source, and the attended reference-device acceptance in S7.
- [ ] **Q09 — Finish Steam/QAM/RTSS, the full-client unelevated Steam launch toggle, and implement
      the direct AutoTDP controller/replay.** The unelevated Steam toggle, the shared patch-evaluation
      helper, the device-verified RTSS frametime reader, the pure AutoTDP controller with trace
      replay, its session binding and user switch, and the deterministic TypeScript build with its
      drift gate are done. What remains is the direct per-component QAM implementation, an AutoTDP
      surface in QAM or the overlay, and the live Steam/RTSS/AutoTDP matrix.
- [ ] **Q10 — Finish static plugin glyph delivery and all WSGM/Steam consumers.** Delivery works and
      is live-verified: the plugin's profile becomes one WSGM-owned stylesheet, matching CSSLoader's
      mechanism so both can run at once. The four JS tier patches and the selector patch that nothing
      consumed are gone. What remains is the static package simplification, the preview and
      input-test surfaces, and visual acceptance with a real profile on a controller settings screen.
- [ ] **Q11 — Finish the overlay, shutdown/installer, and focused release validation.**

A checked architectural queue item has its code, focused tests, diagnostics, and documentation
complete. Attended/live gates remain explicit and unchecked in the owning phase until they run on
the reference device; they are release evidence, not a reason to leave finished source architecture
open. Do not add hundreds of sub-gates; add a concrete bug or missing outcome to the owning item.

## Simplification completion criteria

The structural overhaul is complete when:

- Every fixed feature still has a direct owner and implementation path.
- Normal startup has exactly the 0/1/2+ plugin behavior above.
- A community developer can understand the public SDK and package format quickly.
- WSGM, DeviceHost, Device Lab, and the Claw plugin no longer carry multi-plugin/trust/evidence/
  promotion machinery.
- UI actions reach one service without parallel policy/projection stacks.
- Every normal desktop handoff yields an initialized, medium-integrity, current-session Explorer
  that is not associated with a job; scheduled-task Explorer launch is recovery-only and reported
  as degraded.
- The solution/build/installer contain only boundaries justified by AOT, process lifetime, public
  SDK ownership, or packaging.
- Safety remains concentrated around exact hardware writes, input recovery, CEF fail-open, and
  cleanup—not spread across enterprise governance abstractions.

The current source/build composition meets those structural criteria. Full 2.0 completion remains
separate: the feature set must still pass its focused automated, live Steam, and attended
reference-device validation and produce the release installer artifact.
