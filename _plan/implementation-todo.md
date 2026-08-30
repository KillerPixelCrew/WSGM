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
      **Half of it is answered, and the answer is yes.** Measured on Windows 11 25H2, build
      26200.9168, on 2026-08-29, without touching Explorer: the question is about the Win32
      attribute, so it was asked with a throwaway `cmd.exe` as designated parent and another as the
      child. With the designated parent already exited and only its handle retained, the handle
      stays signalled, `GetExitCodeProcess` still answers, `CreateProcessW` with
      `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` succeeds, and the child's recorded parent is the dead
      process rather than the caller. Reproduced across three runs, with a live-parent control in
      each proving the harness itself works.
      What that does **not** yet establish is the half the mechanism actually depends on: whether a
      dead parent still supplies the medium token and the job association, rather than only the
      recorded parent pid. Discriminating that needs a parent at a different integrity level from the
      caller, which needs an elevated run — so this stays open, now on a much narrower question, and
      the anchor stays the normal path until it is answered.
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
      Scoped down to what is actually missing. The plugin publishes twelve capabilities covering
      power (sustained, boost, scenario), fan (mode, curve, measured RPM), lighting (brightness,
      zone colour), rumble, motion source, controller source and temperature. Two named items have
      no capability behind them:
      - **Charge limit.** `ClawHardwareFacts.ChargeLimitAddress = 0xD7` is declared and unused, and
        the SDK already carries `CapabilityRole.ChargeLimit` and `DisplayKey.ChargeLimit`, so the
        descriptor is a few lines. What is missing is the one thing that must not be guessed: how
        that byte encodes the limit. MSI firmware commonly packs an enable bit with the percentage,
        and writing a wrong shape to a battery controller is not a mistake worth making from an
        assumption.
      - **RGB effect.** Lighting exposes brightness and zone colour but no effect or animation.
      Both are blocked on the same read, and the blocker is elevation rather than hardware:
      `MSI_ACPI` enumerates empty unelevated on this machine, so `Get_Data` cannot be called to see
      the current values. One elevated PowerShell, with the device idle, settles both:
      `$i = Get-CimInstance -Namespace root\WMI -ClassName MSI_ACPI | Where-Object Active;`
      `0x50,0x51,0xD2,0xD4,0xD7,0x98 | ForEach-Object { '{0:X2} {1}' -f $_, (Invoke-CimMethod -InputObject $i -MethodName Get_Data -Arguments @{Data=[byte]$_}).Data }`
      Reading is safe and changes nothing; the write path stays attended either way.
- [ ] Verify that Device Integration off leaves no Claw activity and another manager can take over
      without WSGM killing or reconfiguring it.
      The decidable half is now pinned by `DeviceIntegrationOffTests`. The master switch decides
      regardless of what the child preferences hold, so turning integration off cannot leave WSGM
      creating a virtual controller, hiding the physical one, or writing a power limit — while the
      child preference is still remembered, which is what makes the switch reversible without
      setting everything up again. The make-safe ordering is pinned in both directions: nothing is
      removed before the plugin has handed its devices back, and WSGM's HidHide entries outlive the
      virtual target, because removing them first would expose the physical controller alongside the
      virtual one and whatever takes over would see both at once.
      What is left is the observation itself: another manager driving the Claw with WSGM installed
      and integration off, and nothing of WSGM's moving. That needs the hardware and a second
      manager, so it stays attended.

### S7 — Complete controller management directly

- [x] Finish a technically acceptable virtual-controller backend. Scope is fixed: nothing here may be
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
      - **Measured, and it is not the cost it was treated as.** An attached idle virtual Deck costs
        **6.6% of one core — 0.82% of the machine** on the reference Claw's eight. Without PR #2 it is
        8.3% of a core, so that patch is worth carrying at about a fifth of the cost, but only paired
        interleaved runs show it: a single sample is inside the run-to-run spread. The inherited
        "6–8%" figure lands on the patched build's own per-core number, so it was measuring this same
        thing per-core all along. Submissions are not the cost; the keepalive replay is. Numbers and
        method in `third_party/controller/README.md`.
      - **The backend now works end to end against the real driver.** Every entry point the binding
        uses returns success, and the attach is real rather than nominal: while attached Windows
        enumerates `USB\VID_28DE&PID_1205` with the expected three interfaces, and teardown leaves no
        `VID_28DE` device present. Verified on the reference Claw, 2026-08-29, unelevated.
        Getting there needed a second WSGM patch. usbip-win2 0.9.7.8 appended a `serial` field to
        `plugin_hardware`, taking the attach IOCTL's structure from 1100 to 1116 bytes; the driver
        validates the size before acting and rejects the older shape VIIPER encodes with
        `ERROR_INSUFFICIENT_BUFFER`, so every attach failed. The `usbip.exe` fallback cannot cover
        for it either — the usbip-win2 installer leaves `%ProgramFiles%\USBip` off `PATH` entirely.
        `0002-attach-plugin-hardware-layouts.patch` declares the newer structure and tries both known
        sizes newest-first, so WSGM works on 0.9.7.7 and 0.9.7.8 alike. Full evidence, including the
        IOCTL issued directly at each size, is in `third_party/controller/viiper/README.md`.
      **The backend is finished.** Every piece of it ships: the library is built from the pinned
      revision on every release build, the driver installs from an explicitly ticked setup task, the
      availability gate is open, and the whole path is verified against real hardware rather than
      compiled. What is left is the attended acceptance matrix on the reference unit, which is its
      own item below.
      Superseded HIDMaestro analysis, kept because the comparison is what justifies the choice. It is
      no longer a locked component — nothing in a build downloads, stages or installs it — so the
      "pinned alternative" framing below is historical:
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
- [x] Finish managed overlay/taskbar/Settings navigation, held-control suppression, target
      neutralization, and make-before-break SDL/Steam-lease fallback. Held-control suppression,
      target neutralization, and the source projection were already in `ControllerManager`; the
      surfaces are now switched onto the managed stream.
      The whole coupling every navigation surface had to `GamepadService` was one event, so the seam
      is one interface. `Input/UiInputRouter` sits behind it and owns the swap: `CanonicalButtonSource`
      turns samples into the press edges navigation acts on — SDL reports a press once and a
      canonical stream reports a held button on every sample, so deriving edges centrally is what
      makes the two interchangeable — and `SourceArbitration` decides when to switch.
      SDL stays subscribed and running throughout rather than being stopped when the managed source
      takes over. It is what the fallback returns to, and a source that has been stopped cannot be
      shown healthy before the switch that needs it. The switch itself waits for the first managed
      sample: switching on "a managed source exists" rather than "it is delivering" leaves a gap
      where nothing delivers and the UI looks frozen.
      A control held across the switch emits neither a press nor a release, because the user made
      neither. It stays suppressed while the incoming source still reports it held, and clears once
      observed up — or on `SourceSwitch.HeldControlTimeout`, for controls the incoming source cannot
      see at all, which is not hypothetical: the managed source exposes rear paddles SDL never
      reports. Coming back after a fallback resets the held state, so the first press afterwards is
      not swallowed.
      This is what lets WSGM's own UI be driven by the controls SDL cannot see on a handheld — the
      rear paddles, Quick Access, and the trackpad clicks. The chord watcher deliberately stays on
      raw SDL: the chord is what opens the overlay, so it has to keep working when the managed
      source is not running.
      With controller management off — every current release — the router forwards SDL unchanged,
      so the device-verified path is untouched. Live handheld acceptance stays in the attended item
      below.
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

- [x] Keep one persistent `SteamUiHost` connection/reconnect owner and one TypeScript bootstrap.
      One of each, and the shape enforces it: `ShellSession` constructs the single
      `PersistentSteamUiTransport`, `SteamUiSessionHost` is the only thing that takes it and builds
      the one `SteamUiBridgeHost` over it, and `NativeQamBootstrapPatch` is registered once and is
      the only patch carrying a bootstrap. Every semantic control is a separate patch sharing that
      one bridge rather than a second connection, and `eng/build-steam-assets.mjs` compiles the one
      TypeScript source into the one shipped asset with a drift check.
- [x] Collapse class/version/tier machinery where a direct component-local probe/apply/remove/health
      implementation is sufficient; keep failures independent and native Valve fallback intact.
      What is left is exactly the direct implementation this asks for and nothing above it.
      `ISteamUiPatch` is probe, apply, verify, remove plus an id, a version and the resource key the
      patch serializes on — no compatibility class, no tier, no shared registry of what a patch is
      allowed to be. The four glyph mapping-namespace tiers that were the last of that machinery are
      gone, replaced by one stylesheet with one owned node.
      Both properties are structural rather than incidental. `SynchronizePatchAsync` runs each patch
      inside its own bounded phase and its own try, and every failure path — a throwing probe, an
      absent or non-unique target, a failed apply, a timeout — records that one patch's state and
      returns, so a patch that cannot apply on a given client build costs exactly that control.
      Valve fallback is what "returns" means: the patch never installed anything, so the surface it
      would have replaced is Steam's own, untouched.
      Six patches now exercise that independence for real, with their own probes and enable switches:
      bootstrap, TDP, AutoTDP, frame limit, overlay level, controller target, and the glyph
      stylesheet.
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
- [x] Retain existing CEF library/card/Wi-Fi/download behavior while completing native TDP, frame
      limit, RTSS overlay level, performance/profile, controller-target, and AutoTDP projections.
      All six projections exist and are fed by real services. TDP, frame limit and overlay level
      were already there; controller-target was written but wired to a hardwired unavailable
      service, and AutoTDP had no native surface at all. Both now read their one owner through the
      coordinator — `ControllerManager` and `DeviceIntegration.AutoTdpEnabled` — rather than
      synthesizing a pseudo-capability, so the QAM control, the overlay row and the Settings
      checkbox are the same setting reached three ways.
      Nothing on the CEF library, card, Wi-Fi or download paths was touched: every one of these is a
      separately registered `NativeQamComponentPatch` with its own structural probe and its own
      enable switch, so a client whose shape does not match loses that one control and keeps the
      rest, and the existing patches keep the ids, versions and fingerprints they had.
- [x] Add a user-owned toggle that launches the complete Steam client unelevated, not only
      individual games or helper processes. Keep the current integrity-matched launch as the
      default, apply the choice consistently to cold start and restart paths, and log the selected
      Steam launch integrity for remote diagnosis. `AppConfig.SteamLaunchUnelevated` (default off,
      Settings → Steam) routes the cold start through `UnelevatedLauncher` when WSGM is elevated;
      both the cold start and the auto-relaunch pass through `SessionModes.StartBigPicture`, so the
      choice cannot apply to one and not the other. Every launch logs
      `Steam launch integrity: …`, including a requested de-elevation that was unavailable.
      Live acceptance stays in the S8 validation item below.
- [x] Keep RTSS controls working when Device Integration is off. Nothing on the RTSS path was ever
      routed through the device platform, and `PerformanceIndependenceTests` now pins that rather
      than leaving it as an accident of the current wiring: the service, the overlay projection and
      the observation lease are constructed with no coordinator, plugin or capability in reach, so
      introducing a device dependency into that path stops the tests compiling. `ShellSession`
      constructs `PerformanceService` unconditionally, before and outside the device branch, and the
      only switch that empties the rows is the performance policy's own `Enabled`.
- [x] Reduce performance state to the verified current frametime/metrics required by overlay, QAM,
      diagnostics, and AutoTDP; do not build a general metrics platform. `Core/RtssFrametimeReader.cs`
      reads exactly one thing — the per-application mean frametime — from RTSS's own shared memory.
      The `RTSSSharedMemoryV2` layout was confirmed against a live RTSS 2.21 on the reference Claw on
      2026-08-29 rather than copied from a header; the offsets and the tick-based mean are recorded in
      `docs/rtss.md`.
- [x] Make the native QAM actually reach the user. Live-verified on the reference Claw on
      2026-08-29; every one of these was silent, and each was found by measuring the running Steam
      rather than by reading the code.
      - `appendControls` searched for Steam's `PanelSection` inside `performanceRoot(props)`, an
        UNRENDERED element whose `props.children` holds only what was passed in. Steam's section
        exists only after React renders it, so the walk ended on a childless root — measured as
        `depthReached 0, sectionSeen false` with all five rows built and the section component
        resolved. It would have failed the same way on SteamOS. WSGM now renders its own
        `PanelSection` and appends it, depending on nothing about Steam's internal tree shape.
      - The request context declared `PropertyNameCaseInsensitive = false` with no naming policy, so
        the source generator matched PascalCase against the bootstrap's camelCase envelope and
        NOTHING bound: `Version` arrived as 0 and every command was refused as a "schema version
        mismatch" with an empty patch id. Every native-QAM command had been rejected since the
        bridge was written, invisible because no row rendered to send one.
      - Steam's localiser returns a React element wrapping a string, not a string, so `localizeOr`
        fell back on every token and WSGM's rows rendered in English beside Steam's own in the
        user's language.
      - The sliders were controlled by the observed hardware value with a no-op `onChange`, so the
        handle snapped back on every render: dragging did nothing and one press moved one step.
      - `wsgm.native-qam.auto-tdp` was missing from the command allowlist that also gates
        subscriptions, so the AutoTDP row threw on every render.
      - Controller target ids were validated lowercase-only against `SteamDeckComposite`,
        `Xbox360`, `DualShock4`.
      - The bridge reused an installed bootstrap whenever the version and both Steam generations
        matched, none of which change when WSGM updates, so a new build kept running the previous
        build's injected script until Steam itself restarted. The asset's SHA-256 is now part of
        that identity.
- [x] Suppress Steam's own FPS counter rows in favour of WSGM's RTSS overlay. Matched by localising
      `#QuickAccess_Tab_Perf_FPS_Corner` and `_FPS_Contrast` — the DOM classes are hashed per client
      build and the visible text changes with the user's language, so the token is the only stable
      handle. The rows sit about ten levels inside the panel behind component elements, so the
      filter descends by wrapping each plain function component it meets, the mechanism Decky's
      `createReactTreePatcher` uses on this same panel. Dropped only on the path that also appends
      WSGM's rows, so the user never ends up with neither.
- [x] Give the plugin a periodic observation refresh. It published capability state at start, at
      resume and after a command and never again, so every readable capability expired against
      WSGM's thirty-second freshness policy half a minute into the cycle and stayed expired until
      the user changed something.
- [x] Correct PL1's ceiling to 37 W. `_plan/claw-8-a2vm-plugin.md` recorded "8–30 W; stock read
      30 W" for EC `0x50` — the stock value copied into the range field — and that reached both the
      capability descriptor and the write validator. HandheldCompanion's `ClawA2VM` declares
      `cTDP = { 8, 37 }` for the same board.
- [x] **Hide the sections the device does not have, and make absence the default.** Confirmed on
      the reference unit: no trackpad sections, no second back-button pair.
      Hiding now anchors on the section container `_1KA4m3xP2X5TGmO81UKYgL` and matches the Valve
      glyph anywhere inside it, so a control the device lacks takes its heading and its bindings
      with it. It previously anchored on a row and required the glyph to be an immediate
      grandchild, which described neither the trackpad sections nor the back-button rows as Steam
      builds them, and its `RowGlyphs` table named `shared_m1.svg`, `shared_l5.svg` and
      `sd_ltrackpad_swipe.svg` where the client draws `sd_l4.svg`, `sd_l5.svg` and
      `sd_ltrackpad_up/down/left/right.svg` — so every rule matched nothing.
      **A plugin now declares only what its device HAS.** Anything it does not name is hidden.
      Declaring absence explicitly was the previous model and it fails the way every
      allowlist-by-omission fails: the entry nobody remembered to add is the one that shows up on
      screen. The Claw profile lists no absent controls at all now.
      **Binding sub-rows are hidden too**, through the one build-independent hook in this whole
      surface: Steam spells its own input enum into each row's id —
      `modeid-7-input-unknown EControllerModeInput ( 55 )-binding-0` — where every other handle here
      is a class the build rehashes. The reference pairs that token with a hardcoded number per
      control; WSGM does not need to, because the row also carries the glyph for its own input, so
      `[id*="EControllerModeInput"]:has(img[src="<valve glyph>"])` identifies it just as precisely
      and works for any control on any device with no table to maintain. Measured on the reference
      Claw: 55 -> `sd_l5.svg`, 56 -> `sd_r5.svg`, 57 -> `sd_l4.svg`, 58 -> `sd_r4.svg`,
      51 -> `sd_button_view.svg`, 52 -> `sd_button_menu.svg`. Section rules alone could not reach
      these — on the binding editor the section selectors match nothing at all.
      Not adopted: the reference's `@container style(--hiding-enabled: 1)` gate, which makes hiding
      a user toggle rather than a policy. WSGM hides whenever a profile is active.
- [x] **Map the Claw's OEM buttons onto the virtual target's Steam and Quick Access buttons, in the
      plugin.** They are physical controller buttons and belong in the controller sample.
      `CanonicalButtons.Guide` and `CanonicalButtons.QuickAccess` both existed and
      `ClawInput.Decode` set neither, so the virtual Steam Deck had no Steam button and no QAM
      button — which is also why no glyph could appear for them: the controls did not exist as far
      as Steam was concerned.
      The firmware sends them as MSI WMI events (`0x29` OEM1, `0x58` OEM2 short, `0x2A` OEM2 long)
      with a press and no release, so `ClawOemButtonLatch` turns one event into a 120 ms
      press-and-release that the pad reader merges into the sample stream. One latch, shared by the
      OEM event source and the controller reader.
      **WSGM claims neither button.** `DeviceOemActionRouter` defaulted OEM1 to the WSGM overlay and
      let OEM2 fall through to WSGM's Device page whenever Steam's QAM did not answer, so on any
      machine where the native QAM was unreachable both of the device's buttons belonged to WSGM.
      The default is `Disabled`; the Settings hotkey assignment is the only thing that should put a
      WSGM surface on a hardware button. A first attempt at this added a WSGM action that
      synthesized Ctrl+1 into Steam — the same boundary violation wearing a different hat — and was
      reverted.
- [x] **Sweep the codebase for more pseudo-security like the glyph SVG sanitizer.** That one kept an
      allowlist of permitted SVG root attributes, elements, path attributes and colour forms, and
      shipped a canonical document re-serialized from what survived. It protected nothing: a plugin
      is a .NET assembly DeviceHost loads and runs, already holding WMI, HID and EC access, and
      free to open a socket to Steam's debug port and drive CEF itself. What it did do was refuse
      all twenty glyphs of the first real profile for carrying a `width` attribute, and destroy the
      controller illustration's `<g>` grouping. It is now a pass-through with an integrity check.
      The test for this class of code: name the attacker, and say what they cannot already do
      through a door that is standing open beside it. If that sentence cannot be written, the check
      is costing capability and buying nothing. WSGM is a game mode for Steam on a single-user
      handheld; the user is responsible for what they install.
      Candidates to review against that test: the splash-theme extraction defence set in
      `docs/ui.md`, the device-package validator's non-integrity checks, the Steam UI patch bounds,
      and anything else framed as protecting WSGM from a component that already runs as the user.
      Integrity checks are NOT the target — hash pinning, size bounds, CRCs and dimension
      agreement catch corruption and stay.
      **Four sites swept so far, all cleared, with the sentence the test demands:**
      - `Sdk\Glyphs\GlyphAssetValidation.cs` — already fixed; it now states in its own summary that
        it deliberately does not sanitize.
      - `Core\DevicePackagePolicy.IsX64ManagedAssembly` — not security. It refuses to *load* a
        wrong-architecture or non-managed entry assembly, which is failing early with a reason
        instead of late with a loader error. It claims to stop nobody.
      - `Core\SteamUiBridge` command allowlist — real, and the sentence writes itself: the attacker
        is any script running in Steam's CEF context, including another injector such as CSSLoader
        or Decky, and what it cannot otherwise do is reach WSGM's privileged device commands. This
        is the boundary that makes injected JS safe to run at all; do not remove it.
      - `Shell\DeviceHostClient.WriteTrace` bounds — the same rationale as `PlainText`: it prevents
        a malformed string corrupting a log line or hiding its own tail from whoever reads it. Not a
        privilege boundary and not claimed as one.
      **Four more swept, all cleared:**
      - `Shell\SdFormatManager.SanitizeLabel` — the label is interpolated into a generated diskpart
        script, so this is the repository's own "never concatenate untrusted input into a command
        line" rule, not a trust boundary. The thing it stops is a stray quote producing a malformed
        command against a physical disk. It does cost capability — no non-ASCII card names — but the
        diskpart constraint is real. Keep.
      - `Core\PersistentSteamUiTransport` and `Core\SteamUiPatchManager` target allowlists — these
        name which CDP target a patch may attach to. Not defence: attaching a QAM patch to a store
        page would simply be a bug. Keep as correctness.
      - `Shell\HidHideOwnership` allowlist — functional, not protective. It decides which processes
        still see a hidden controller, and its own comment records the incident where DeviceHost
        was allowlisted too late and the plugin enumerated nothing.
      - `Input\UiCapture` — the allowlisted host is the mechanism by which WSGM's own surfaces stay
        readable while excluded from ordinary capture. Removing it would break the feature, not
        loosen a boundary.
      **The splash-theme defence set is cleared, and it is the case that shows the test working.**
      `.wsgmsplash` files are *shared between users*, so the attacker is whoever authored a theme
      someone downloaded — not the user, and not a plugin. That author holds no other access at all,
      which is exactly the sentence the glyph sanitizer could not produce: there a plugin already had
      WMI, HID and EC access, so an SVG allowlist bought nothing. Here the same shape of check buys
      everything. Zip-slip out of the staging directory, decompression bombs against lying
      central-directory sizes, a UNC image path making Settings touch a remote host while
      thumbnailing, and decode bombs via declared pixel dimensions are all things a theme author
      could otherwise do and cannot. Keep the set intact.
      **Sweep complete.** Nine sites reviewed, none removed, two reclassified as do-not-remove
      boundaries (`SteamUiBridge`, splash extraction) that had been listed as removal candidates.
- [x] **Ship a physical glyph profile for the Claw 8 A2VM.** Done and confirmed on the reference
      unit: the Steam Input page shows the Claw's buttons and its own illustration. The package
      carries a profile for `ms-1t52` with twenty control glyphs, both split controller images and
      the controller illustration, from `_ref/handheld-controller-glyphs` (MIT, attributed, pinned
      to its upstream revision); release staging copies `glyphs/` and the offline validator runs
      over the staged bytes.
      Shipping it took seven separate faults, none of which the code reported, and every one found
      by probing the running client rather than by reading:
      - The stylesheet was installed into `SharedJsContext`, whose body measured 218 bytes. The page
        renders in the Big Picture window — 29,555 bytes, every glyph image — and CSS is per
        document, so half a megabyte of correct CSS applied and verified into a blank page. There
        was no role for that window; `MainWindow` now matches on window shape, because CDP reports
        the URL a target was CREATED with (`about:blank?…`) rather than the document address, and
        the title is localized.
      - Selection was hardwired to fail: `PhysicalGlyphSelectionSnapshot` passed
        `activeDeviceId: null, advertisedProfileId: null`. DeviceHost had always sent the matched
        definition and WSGM never read it, and nothing anywhere supplied an "advertised profile" —
        so the selector refused every profile whatever the catalog held, disabling all three glyph
        surfaces from one call site. That parameter is deleted rather than fed: naming the device is
        the whole discriminator.
      - The control map lacked the `sd_*` family the page actually draws, so shoulders, triggers and
        rear paddles kept Valve's artwork while the face buttons were correctly replaced.
      - The footer's Menu hint stayed blank because `display: none` on Steam's inline logo svg
        collapsed its flex container to 0x0 and the background had no area. `visibility: hidden`
        keeps the box.
      - Hiding matched nothing: it anchored on the wrong class and named `shared_l5.svg`,
        `shared_m1.svg` and `sd_ltrackpad_swipe.svg` where the client draws `sd_l5.svg`,
        `sd_l4.svg` and `sd_ltrackpad_up/down/left/right.svg`.
      - The controller illustration override installed, verified, and lost the cascade: Steam
        qualifies that div with a controller-type ancestor, so two classes beat one. It needed
        `!important`, which the live prototype had and the implementation dropped.
      - The transport refused the payload outright — a 96 KB cap on WSGM's own outgoing expression
        against ~500 KB of real artwork. Inbound framing bounds are kept; the send-side caps are
        gone, because WSGM decides what WSGM sends.
- [ ] **Replace WSGM's hand-rolled RTSS interop with `RTSSSharedMemoryNET`** — the library
      HandheldCompanion uses. There was never a decision to hand-roll this; it is an omission, and
      two of the three RTSS defects found on 2026-08-29 were in code the library already owns.
      What it replaces: `Core/RtssFrametimeReader.cs` (the `RTSSSharedMemoryV2` read) and
      `Core/RtssProfileApi.cs` (the `RTSSHooks64.dll` profile P/Invokes). Both are WSGM
      re-implementations of that library's whole subject.
      What it does **not** replace, so keep it: `Core/RtssDiscovery.cs`. Verifying that the
      registered installation is a genuine, signed, correctly-versioned RTSS under a protected root
      is WSGM's own trust question, not something the library answers — and it is where the
      expired-certificate bug actually lived.
      Two things to settle before committing to it, in this order:
      - **NativeAOT.** WSGM forbids reflection-dependent packages and the AOT publish is the
        compatibility proof. The library looks like plain P/Invoke over blittable structs, which
        would be fine, but that has to be proven by publishing rather than by reading.
      - **How to consume it.** It is not on NuGet; HandheldCompanion vendors the source. So this
        follows the `native/` precedent — a pinned revision in `third_party/` built from source with
        its licence retained — rather than a package reference. Record the pin the same way
        `third_party/controller/` does.
      Keep the seams that already exist. `IFrametimeSource` and `IRtssAdapter` are what let the
      overlay, QAM and AutoTDP stay unaware of any of this, and they are what make the swap a
      contained change rather than a rewrite: the library goes behind them, and
      `SimulatedRtssAdapter` keeps `--overlay-test` working with no RTSS at all.
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

- [x] Keep the plugin-owned manifest, artwork, logical control map, source revision, and required
      license notice. All five survive the CSS rewrite and are the only source of what reaches Steam:
      `SteamInputGlyphPresentation.Create` resolves the plugin's manifest, its control map, and its
      aliases into Valve resource mappings, and every image `SteamGlyphCss` emits is a hash-checked
      asset from that package. WSGM ships no handheld artwork.
- [x] Prefer fixed reviewed PNG assets for runtime presentation where they can replace the custom
      thousand-line SVG parser without losing quality; retain narrowly normalized SVG only where
      needed. **Resolved by evidence against the change: SVG is the right format for glyphs and PNG
      is not.** The reference theme WSGM now matches is 142 SVG assets to 56 PNG, and for the MSI
      Claw specifically every glyph is SVG while only the two controller half-images are PNG —
      because a glyph is drawn at many sizes across Steam's surfaces and a raster one is soft at all
      but the size it was authored for. The qualifier in this item is what decides it: PNG cannot
      replace these "without losing quality".
      The normalizer also is not a thousand lines — `GlyphSvgNormalizer` plus `GlyphPathData` is
      about 450 — and it earns them. The assets are untrusted plugin data that WSGM inlines as data
      URIs into Steam's own page, so stripping active content, external references, and unbounded
      geometry is the thing standing between a plugin package and Steam's document.
- [x] Reduce validation to path locality, known IDs, format, dimensions, size, and references. Already
      exactly that: `GlyphAssetImportCode` has four outcomes — malformed or format-mismatched payload,
      dimension mismatch, active content or external reference, and malformed or over-budget geometry
      — with path locality and known IDs enforced by `GlyphPackageLayout` and the manifest before an
      asset is read.
- [x] Keep WSGM-owned Steam selectors, context-local delivery, cleanup, CSS Loader coexistence, and
      native fallback. All five are properties of the CSS delivery and are live-verified: the Valve
      resource names and selectors are WSGM's and never a plugin's, the stylesheet is installed into
      the matched context only, removal takes just the nodes carrying WSGM's own marker class, a
      `.css-loader-style` node is never touched so both tools run at once, and a profile that
      supplies nothing — or a Steam build whose classes moved — leaves native Valve glyphs in place.
- [x] Finish Automatic/Native/manual selection, graphical preview, input test, OEM rows, and
      navigation hints from one shared physical map. All five read the same
      `PhysicalGlyphService.Resolve`, differing only in the surface they ask for, so there is one
      selection policy and one geometry cache behind every one of them.
      Selection cycles Automatic, Native Steam and manual on the Glyphs page. The preview and the
      input test are one surface — the same picture answering whether the artwork resolves and
      whether pressing a control reaches WSGM as the control the artwork claims. OEM rows arrive
      through the capability list on their own page.
      Navigation hints replace the written activation letter with the device's own button, on the
      `NavigationHint` surface, which the service refuses unless the input actually reaching WSGM is
      the managed handheld's. The letter stays in the markup and remains the fallback, so this can
      only ever add: a machine with no profile, or one where an Xbox pad is what is being held,
      keeps the letter — showing a Claw button to someone holding an Xbox pad would be worse than
      the letter it replaced. The hint asks for `FaceSouth` rather than "A", because the glyph
      vocabulary is positional and a device whose bottom face button is printed differently should
      get the button it actually has.
- [x] Finish stable resource mappings, controller diagrams, exact inline mappings, and supported
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
      All four halves are emitted: stable Valve-basename `content:` overrides, controller diagrams
      through `AppendControllerImages`, exact inline mappings through the `d`-keyed `:has()` rules,
      and capability hiding as `display: none` on the row carrying the absent control's glyph. The
      abstract base and its four subclasses are gone from the tree entirely.
      What is left is not implementation: visual acceptance with a real plugin profile on a
      controller settings screen (artwork, orientation, scale) is the attended item below, and
      re-verifying the two build-coupled class names after a Steam client update is ongoing
      maintenance the probe already fails closed on.
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

- [x] Complete Home, Steam, Device, and System navigation with Back/focus/scroll restoration.
      `OverlayNavigation` owns four destinations, a bounded eight-deep page stack that refuses a page
      belonging to another destination, and one Back decision — popup, dialog, nested page, home,
      close — in that order. `OverlayFocusMemory` keeps the semantic key and scroll offset per
      destination without retaining a control, and each nested route carries the focus key it was
      entered from, which `Pop` hands back.
      One real gap fixed: the fallback branch of `LeaveNestedPage` popped bare, where every other pop
      site restores focus from the returned key. It is reached by a nested page none of the named
      branches claims — one added later, or a sub-view flag out of step with the stack — and left the
      user at the top of the page they came back to.
      Tests now pin the invariants the completion rests on rather than only the behaviours that
      already had coverage: a pop hands back the key it was pushed with, a pop at a root is a no-op
      that leaves the stack readable, and every destination has a root page while every page in the
      enum is reachable from some destination — so a page added without being routed fails there
      rather than when a user navigates to it.
- [x] Migrate every existing Home/Steam/System action to its page without reimplementing its service.
      Every action lives on its destination panel with a stable semantic `Tag` that is what focus
      memory stores, and each handler calls the owning service rather than restating it — the shared
      performance projection is reparented between System and Device rather than duplicated, and its
      source keeps RTSS lifetime and state while the window only observes and invokes rows.
- [x] Complete Device Overview, Profiles, Power/Thermals, Controller/Motion, OEM,
      Lighting/Features, Glyph Preview/Input Test, and Diagnostics/Recovery. The eight sections now
      exist as navigable pages rather than headings in one scrolling list: `DeviceOverlaySection`
      splits the combined OEM-and-lighting section and adds Profiles and Glyphs,
      `Overlay/DeviceOverlaySectionPages.cs` builds the root menu from the snapshot, and the Device
      root renders one card per section that currently has something in it, carrying its row count
      and the most serious status inside it. A section a plugin publishes nothing for is absent
      rather than empty, and Back leaves a section page to its card.
      Controller and motion, and Diagnostics, now carry their own rows. The controller target is a
      cycling row reading `ControllerManager` through the coordinator — it names the target in
      effect, distinguishes ready from present, refuses to cycle into a backend that cannot come up,
      and tells a running game that a change reaches it only on the next launch. Diagnostics carries
      the one recovery action a faulted cycle has, with the cycle state beside it, and it is absent
      while there is nothing to recover rather than always present and inert.
      That retry used to be a synthesized `wsgm.device.retry` pseudo-capability with a branch inside
      the capability invoke path. It is now a direct row like AutoTDP and glyph selection, so the
      capability path has one meaning again and the Diagnostics count no longer includes a row no
      plugin published.
      Fixed while doing it: WSGM's own rows were not counted into their sections, so a section
      holding only one of them had a count of zero and was dropped from the menu — which made the row
      unreachable. That was already latent for AutoTDP on a device publishing no power capability,
      and would have been immediate for the controller target, since no plugin publishes one.
      Profiles now carries the selection row. The list of profiles is derived rather than declared —
      a named profile exists exactly when some capability stores a value under its name — so there
      is no catalog to keep in step with the values, and a profile is never offered while selecting
      it would change nothing. None is a position in the cycle rather than a separate control, so
      the button that applied a profile always gets back to unmodified defaults, and a selection
      naming a profile that no longer defines anything reads as NONE, which is what it now behaves
      as. Unlike recovery the row is always present: profiles have to be found before they can be
      used, so with none defined it says where to author one instead of vanishing.
      The identity-keyed profile lookup was duplicated in the coordinator and is now one property.
      Glyphs now carries the graphical preview and the live input test, which are one surface because
      they are the same picture answering the two questions a glyph profile can fail at: whether the
      plugin's artwork resolves at all, and whether pressing a control reaches WSGM as the control
      the artwork claims. Neither is answerable from a list of names.
      It needed no SVG library. The SDK's loader already normalizes the plugin's SVG into a path
      model and `PhysicalGlyphService` — written for this and left unwired pending the Steam gates —
      already turns that into Avalonia geometry, so `Controls/PhysicalGlyphImage` is a transform and
      a fill with no parser or decoder inside the NativeAOT executable.
      The input test reads `ControllerManager.PhysicalSampleObserved`, which is raised before routing
      and deliberately unfiltered: the stream the UI acts on has the controls the UI is using removed
      from it, which are exactly the ones someone checking a mapping needs to see. It is read-only
      and cannot change what is routed, so it is not a second input path. The stream is leased only
      while the page that draws it is showing, and the pressed set is compared before posting to the
      dispatcher, so a controller sitting still costs nothing.
      `Overlay/GlyphInputTestMap` is the one place the canonical button vocabulary meets the glyph
      one. They stay separate deliberately — a device can report a control it has no artwork for, and
      a profile can carry artwork for a control the plugin never reports — and that gap is precisely
      what the test makes visible.
- [x] Bind every overlay and QAM control to the same direct runtime service. AutoTDP now has a
      surface: the Device → Power and thermals page carries a row beside the power limit it moves,
      reporting what AutoTDP is actually doing rather than merely that it is on — controlling with
      its settled watts and the frametime against its deadline, paused by a manual change, waiting
      for a game, or unable to find a power limit. Toggling persists
      `DeviceIntegration.AutoTdpEnabled` through `DeviceCoordinator.ToggleAutoTdpAsync`, so the
      overlay switch and the Settings checkbox are the same setting reached two ways. It is a direct
      command like glyph selection, not a synthesized pseudo-capability, so the capability invoke
      keeps one dispatch path.
      The native QAM now carries the same state through the same owner. `NativeQamAutoTdpPatch`
      renders a Valve `ToggleField` directly beneath the power-limit slider — beside the thing it
      moves, so a user who sees the limit change on its own finds the explanation in the next row —
      and it reports the settled watts while controlling rather than only that it is on. The switch
      is `controlled`, so it shows the stored setting rather than its own click: a command that does
      not land leaves it where the setting actually is. Turning it on or off routes through
      `DeviceCoordinator.ToggleAutoTdpAsync`, the same method the overlay row and the Settings
      checkbox use, so there is one owner and no copy of the value anywhere.
      The controller-target control was already written in the injected script but was fed by a
      hardwired unavailable service. It now has a real one: `ControllerManager` projected through
      the coordinator, offering every target whenever management runs, distinguishing a target that
      is selected from one that is actually up, and telling a running game it needs a restart before
      a change reaches it.
      Two things were found by probing the live client rather than reasoning about it. Steam's field
      module does expose a `ToggleField`, and it is selectable by the same unique-marker rule the
      slider and dropdown already use. And Steam's localizer **returns the token itself** for a
      string it does not have — which is truthy, so the obvious `localize(token) || fallback` would
      have rendered a raw `#QuickAccess_…` as the label of a WSGM feature Valve has no token for.
      `localizeOr` treats a leading `#` as not-found, which also protects the existing Valve-token
      calls if one is ever retired.
      The binding is single-instance by construction, not by convention: `ShellSession` builds one
      `PerformanceService` and one `DeviceCoordinator` and hands the same two objects to both
      surfaces, so `PerformanceOverlayBridge` and `PerformanceServiceNativeQamAdapter` are two views
      of one service and cannot disagree.
      The QAM set is complete at the five controls that belong in Steam's own performance menu — TDP,
      AutoTDP, frame limit, overlay level, controller target. Glyph selection, hardware profile and
      cycle recovery deliberately have no QAM counterpart: they are WSGM's configuration and
      diagnostics, and injecting them into Valve's menu would put WSGM's own settings somewhere a
      user would reasonably expect Steam's.
- [x] Keep Settings limited to startup/integration/controller ownership/logging/update configuration
      and owner-process requests. Checked against what it can reach rather than against what its
      pages are called: every page edits stored configuration through `SettingsViewModel`, and the
      only live objects Settings touches are the two preview requests on the Quick access page,
      which build an `OverlayController` in `previewOnly` mode with no monitor to show the panel and
      the taskbar. That is the owner-process request the item allows, not a second control surface —
      nothing in Settings drives a session transition, a device cycle, or a running capability.
      One real wrinkle fixed rather than argued around: `SplashPresets` lived in `Shell` while its
      own summary said it exists for the Appearance page, and it depended on nothing but `Core`. It
      was Settings' only reason to reference `Shell` at all, so it moved to `Core` where it belongs.
      Settings now depends on `Core`, `Controls`, `Themes` and `Input`, plus the one preview request.
- [ ] Validate controller, touch, keyboard, scaling, accessibility, themes, cancellation, disposal,
      and responsiveness on the handheld.

### S11 — Simplify build, installer, tests, and release

- [x] Make `WSGM.slnx`, `eng/verify.ps1`, and `build.ps1` reflect the collapsed projects and one
      compatible component composition.
- [x] Keep NativeAOT publish proof and prevent JIT/plugin/tool dependencies from leaking beside
      `WSGM.exe`.
- [x] Stage only App, DeviceHost, the one plugin, optional Device Lab, and required controller
      dependencies; remove package catalogs and side-by-side versions.
- [x] Install everything VIIPER needs, as an explicit user-approved elevated installer step — never
      from the running shell (INV-020). Detail in `third_party/controller/viiper/README.md`.
      `WSGM.iss` ships `libviiper.dll` with its notices and header and the verified usbip-win2
      installer under the `controller` component, and a separately ticked task runs
      `Install-UsbipDriver.ps1` from `[Run]`, before setup restarts anything of WSGM's. The step
      re-verifies the pinned digest and signer on the user's disk, skips an install that is already
      present or newer, confirms `usbip2_ude` is registered afterwards rather than trusting the exit
      code, and is non-fatal in every failure mode — a machine without the driver runs WSGM normally
      with controller management unavailable. `eng/acquire-controller-dependencies.ps1` now reads
      the lock file instead of restating it, and `eng/assert-controller-pin.ps1` (wired into
      `verify.ps1`) fails the build if the identity the shipped script carries drifts from the
      reviewed one.
      Two findings that would each have shipped a broken step. The asset is an **Inno Setup**
      installer, so VIIPER's own `/S` is ignored and pops the interactive installer; and
      `System32\drivers\usbip2_ude.sys` **does not exist even on a working install** — it is a
      universal driver in the driver store, so the file test VIIPER's script falls back to reports
      "not installed" on a machine where it is. Detection is the `usbip2_ude` service key instead.
      The pin stays at 0.9.7.7 rather than the newer 0.9.7.8: usbip-win2 #180 and #181 are still
      open against 0.9.7.8, and #180 is a pool-corruption BSOD on *every* attach on Windows 11
      build 26200 — this machine's build. Neither reproducer is on WSGM's path, and 0.9.7.8 offers
      nothing WSGM needs, so there is no reason to take it.
      With that shipping, both conditions `DeviceFeatureAvailability.ControllerManagement` was
      waiting on are met and the gate is open. Whether the controller works on a given machine stays
      a runtime question with distinguishable answers in `ControllerManagerStatus`, not a constant.
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

### S12 — Per-application profiles by reactivating Steam's own performance UI

Full plan and the live-probe evidence behind every claim here: `_plan\qam-overhaul.md`. The
components already ship in the Windows client; only their backend is absent, so this replaces
hand-built rows with Valve's own and supplies `SteamClient.System.Perf`.

- [x] Add a VRR capability role and display key to the SDK so the plugin publishes it and WSGM only
      projects it. No device-specific detail enters the SDK. `CapabilityRole.VariableRefreshRate`
      pairs with `Boolean` in `DeviceCapabilityRouter`, and `DeviceOverlayBridge` places it in Power
      and thermals beside the frame-limit and power controls it interacts with.
- [x] Implement the Claw plugin's IGCL transport: dynamic `ControlLib.dll` load, Arc Sync capability
      detection, read, write, and restore of the saved parameter struct on make-safe.
      `ArcSyncTransport.cs` binds by function pointer and reports unsupported when the library is
      absent; `DisplayService` owns it for a cycle and restores on stop, outside the service loop,
      because nothing `StopServicesAsync` releases owns the display. The descriptor is published
      only when a capable panel answers, the write is reported verified only once the read-back
      agrees, and the struct sizes are pinned by a test because IGCL refuses a `Size` mismatch in a
      way indistinguishable from "no variable refresh here".
      **Attended validation remains**: no automated test drives the real driver.
- [x] Implement the Core per-application profile store keyed by app id, plus the three configurable
      frame-limit strategies — `FrameLimitOnly`, `NativeModes`, `FrameDoubling` — with runtime mode
      discovery validated by `ChangeDisplaySettingsEx(CDS_TEST)` and lowest-valid-multiple pairing.
      `Core\PerformanceProfiles.cs` resolves the global and per-game layers with each value falling
      back independently; `Core\FrameLimitPairing.cs` holds the strategies and derives the cap
      options from the discovered modes rather than a fixed ladder;
      `DisplayProfiles.EnumerateAcceptedRefreshRates` enumerates then `CDS_TEST`s every rate and
      logs both what was accepted and what was refused. `TryApplyTransientRefreshRate` omits
      `CDS_UPDATEREGISTRY` so exit, crash, and reboot self-heal, and stays separate from the
      display-profile path that persists on purpose. `FrameLimitOnly` is the default.
- [ ] Implement the `SteamClient.System.Perf` shim: build `CMsgSystemPerfState` through the client's
      own message classes, deliver it via the store's bound `OnStateChanged`, and route
      `UpdateSettings` deltas to the same services the overlay uses. One patch per mounted
      component, each with its own fingerprint, verification, removal, and kill switch. Never set
      `force_deck_perf_tab`; it is a persisted client setting that force-shows unbackable rows.
- [ ] Supply the `SteamOSService/State/Manager` RPC response as a second seam so Valve's own TDP row
      is reused rather than hand-built: `GetState()` → `is_tdp_limit_available`, `tdp_limit_min`,
      `tdp_limit_max`, plus the `is_charge_limit_available` and `charge_limit_min/max/default` the
      same service carries. Its own patch id, fingerprint, verification, removal, and kill switch,
      so a changed service shape loses that row and nothing else.
- [ ] Mount only the components WSGM can back, and retire the hand-built rows they replace. Keep GPU
      clock, scaling mode/filter/sharpness, half-rate shading, tearing, force composite, and Steam's
      own FPS overlay hidden.
- [ ] Project the same services onto the overlay, which stays the complete surface.
- [x] Amend D16 in `_plan\2.0-decisions.md` to record that supplying an absent
      `SteamClient.System.Perf` for a device WSGM can service is in scope and is not the forbidden
      global SteamOS/Deck spoof. D16 now carries the four-gate table and the rule that the forbidden
      thing is the global claim, never the local answer.
- [x] Record the device- and live-verified findings in `docs\steam-cef.md` and
      `docs\power-and-display.md`: the absent-backend seam, the three Steam performance backend
      families, Arc Sync read/write/restore, and driver-synthesized modes outside the EDID. Both
      written, including the four-gate table, the Wi-Fi trap that a probed access point is WSGM's own
      synthetic one, and the rule against `force_deck_perf_tab`.
- [ ] Validate on the reference device: the live Steam matrix with the panel mounted, per-game
      profile switching against a real game, each frame-limit strategy including an
      exclusive-fullscreen title across a mode change, VRR toggling with a rendering game, and
      recovery when Steam restarts underneath the shim.

### S13 — Plugin-driven settings page, sections, and profile authoring

Full plan: `_plan\plugin-driven-settings-page.md`. A plugin declares typed elements and sections;
WSGM draws, validates, stores, and localizes them. The plugin ships no UI. Settings holds plugin
settings and profile authoring only — device control stays in the overlay.

- [x] Add `CapabilityValueKind.Text` with `CustomLabel`'s exact treatment: declared maximum length,
      control characters and bidirectional overrides rejected, escaped at every sink, never a format
      string or localization key. The rule now lives once in `Capabilities\PlainText.cs` with
      `CustomLabel` refactored onto it, rather than in two copies. `GenericText` is its role, and a
      text descriptor must declare its own bound while nothing else may.
- [x] Add the plugin settings descriptor and manifest, separate from the capability manifest: stable
      id, value kind, `CapabilityDisplay`, default, section assignment, sort order, and per-kind
      bounds. `Sdk\Settings\PluginSettingsManifest.cs`. Curve-shaped settings are refused, because a
      curve is authored as a named profile with its own storage and would otherwise have two homes.
- [x] Add the section descriptor and its WSGM-owned display key with a bounded `Custom` title,
      mirroring `CapabilityDisplay` rather than inventing a looser rule. Bound the section and
      element counts, reject duplicate ids naming the offender, and break sort ties on declaration
      order. `Sdk\Settings\PluginSettingSection.cs`. An element naming an unknown section validates
      deliberately, so the renderer can fall it back rather than drop it; the fallback placement,
      the skipped empty section, and the sort decisions are the renderer's to log when it is built.
- [ ] Keep sections scoped to the settings page and `Generic*` capabilities. A semantic role keeps
      the home WSGM gives it, so a plugin cannot scatter power or fan controls into invented
      groupings and break the cross-device consistency `DisplayKey` exists to protect.
- [x] Store settings values in WSGM configuration through the source-generated JSON path, keyed by
      device definition id and plugin id, and revalidate against the current manifest on load.
      `Core\DeviceConfiguration.cs` and `ConfigStore.NormalizeDeviceIntegration` drop unmatchable
      scopes and duplicates so the file cannot grow forever; `Core\PluginSettingsResolver.cs`
      reconciles what is left against the live declaration, falling back to the declared default and
      reporting the stored value beside the declared bound so the rejection is diagnosable from a
      user's log. A setting whose kind changed between plugin versions is rejected rather than
      reinterpreted, and settings the manifest no longer declares come back as orphans.
- [x] Deliver settings to the plugin at start and on change over the existing wire contract. No new
      privileged channel: `SettingsManifest` and `SettingsValues` join the closed
      `DeviceMessageType` enumeration, with `IPluginHostAdapter.PublishSettingsManifestAsync` and a
      defaulted `IDevicePlugin.ApplySettingsAsync` so a plugin declaring nothing carries no empty
      override. Values travel as a complete set, and the manifest is validated at both ends so a
      refusal names the plugin where its author will read it and leaves the previous declaration
      standing.
- [ ] Render one WSGM-owned Settings page from the declared manifest, gamepad and touch navigable,
      on the shared controls and themes. Sections are focus groups with stable semantic keys so the
      existing per-destination focus and scroll restoration survives a refresh.
- [ ] Build a reusable curve editor in `Controls\`. None exists in the tree: `FanCurve` and
      `CapabilityValueKind.Curve` are declared, validated, and projected, and rendered by nothing.
- [ ] Add device-keyed RGB and fan profile authoring to Settings, revalidated against the live
      `FanCurve` descriptor before apply. Authoring only: `--settings` starts no DeviceHost, so the
      editor has no live temperature or RPM readout in the first cut.
- [ ] Let the overlay select those profiles globally or per application, extending the per-app
      profile store from S12 rather than adding a second per-app mechanism.
- [x] Record the Settings/overlay boundary as a numbered decision in `_plan\2.0-decisions.md`. Now
      D22b, including the discriminator that makes it not a judgement call: a setting configures
      behaviour and WSGM stores it, a capability writes hardware and the device holds it, and
      authoring a named profile writes no hardware so it belongs on the surface with a mouse.
- [x] Give the `WSGM.DeviceLab\Testing` synthetic plugin fixture a settings manifest so the page is
      exercised without hardware. Covers every value kind except curve, which the SDK refuses by
      design, and keeps one setting in an undeclared section so the fallback path is exercised.
- [ ] Validate on the reference device: the page rendered from the Claw plugin's real manifest,
      gamepad and touch navigation across sections, a curve authored in Settings then applied from
      the overlay, and behaviour after a plugin update narrows a range a stored value no longer
      satisfies.

### S14 — Revive Quick Settings, Internet, and Bluetooth

Full plan: `_plan\qam-quick-settings.md`. Same principle as S12, with a gate taxonomy that governs
all Steam UI revival: supply an absent JS namespace, supply an absent RPC response, override a
single Deck-only store getter — but never set the global `TS.IS_STEAMOS`, which is the D16 spoof.

- [ ] Read `Core\SteamNetworkIndicator.cs`, `Shell\NetworkIndicatorService.cs` and
      `Shell\RadioManager.cs` first. The backend is finished — `SetRadioAsync`, `ConnectAsync`,
      `DisconnectAsync`, `ForgetAsync`, `SetAudioConnectionAsync`, `UnpairAsync`,
      `RespondToPairing`, scanning and PIN prompts all exist — and part of this surface is already
      revived. Everything below is an adapter over that, not an implementation.
- [x] Override the `networkManagementAvailable` getter — it is literally `return TS.IS_STEAMOS`.
      `Core\SteamNetworkGatePatch.cs` plus the bootstrap's network gate: the prototype getter is
      replaced and restored rather than shadowed on the instance, the probe refuses a client that
      already reports network management available, and verification reports the access-point count
      so a revealed row over an empty list is not mistaken for success. Live-verified: descriptor
      configurable, override flips the value, restore puts it back, and the store reports a real
      wireless device throughout.
      Expect a Wi-Fi row and Internet page over an **empty** network list: Steam's Windows backend
      does push real device reports, but every one carries an empty `wireless.aps`, so it never
      enumerates networks. The single access point visible in a live probe is WSGM's own synthetic
      one from `SteamNetworkIndicator`, not Steam's.
- [ ] Feed the whole access-point list from `RadioManager` through the store's `SetDeviceInfo`
      ingestion path in the plain-object shape the protobuf decoder produces, and wire connect and
      forget to `ConnectAsync`/`ForgetAsync`. Two constraints are already device-verified and must
      not be rediscovered: replacing the store's report handler does not work, because the backend
      holds the bound callback registered at store init; and backend reports expire unknown entries
      through `MarkAsNotPresent()`, so injected entries need the same no-op pin
      `SteamNetworkIndicator` already uses. `SetWifiEnabled` exists natively, is untested, is a real
      radio mutation, and stays attended.
- [ ] Revive Bluetooth pair and connect in Steam directly by replacing the `BluetoothManagerService`
      stub methods on the plain object `RF` exported by module `60517`, routing them to the existing
      radio backend. The service round-trips on Windows — `GetState` succeeds and returns
      `is_service_available: false` with empty adapters and devices — so the transport and message
      shapes are present and only the backend is missing. Cover `GetState`, `GetAdapterDetails`,
      `GetDeviceDetails`, `NotifyStateChanged`, `SetDiscovering`, `SetLoginAdvertising`, `Pair`,
      `CancelPair`, `Forget`, `Connect`, `Disconnect`, `SetWakeAllowed`, `SetTrusted`, each
      returning the transport result shape (`BSuccess()` plus `Body().toObject()`). `*Handler` is a
      message descriptor, not a registration hook, so implementing the service is not an option.
      Land it after Wi-Fi so the narrower gate override is proven first.
- [ ] Audio has its own plan and its own phase: see `_plan\steam-settings-audio-revive.md` and S15.
- [ ] Reuse Valve's brightness row over `SteamClient.System.Display.SetBrightness`, which exists and
      whose availability flag defaults true. If it does not move the panel, fall back to the driver
      through IGCL `ctlGetBrightnessSetting`/`ctlSetBrightnessSetting`, which makes it a device
      transport and therefore plugin-owned beside Arc Sync.
- [ ] Back night mode with Windows Night Light; its Steam gate is `IN_GAMESCOPE` only.
- [ ] Add a resolution row, which exists nowhere in the tab because SteamOS drives it through
      gamescope, from the same runtime mode discovery the frame-limit strategies use.
- [ ] Mount the Performance tab's refresh-rate component here, shown only when the frame-limit
      strategy is `FrameLimitOnly`; under `NativeModes` or `FrameDoubling` the pairing policy owns
      the refresh and a second control would fight it.
- [ ] Leave the natively working rows alone: controller list with battery and Identify, reorder
      controllers, game recording, and display scaling over `SetUnderscanLevel`.
- [ ] Validate attended on the reference device: Wi-Fi enumerate/connect/forget/airplane, Bluetooth
      pair through forget including a controller over Bluetooth, audio device switching while a game
      runs, brightness across both paths, night mode, a resolution change with a game running, and
      recovery of every one when Steam restarts underneath the patches.

### S15 — Revive Steam's audio settings

Full plan: `_plan\steam-settings-audio-revive.md`. The backend already exists — `AudioManager`,
`NativeVolumeControl`, `native\VolumeControl` — so this is an adapter plus one new capability.

      **Nearly done.** `Shell\NativeQamAudioService.cs` projects `AudioManager` into Steam's
      device/volume shape through the same manager property the taskbar sets, so the two surfaces
      cannot disagree; the bootstrap supplies `SteamClient.System.Audio` and also writes the running
      store, because its `m_bAvailable` is computed once at construction and WSGM attaches to a
      client that is already up; `Core\NativeQamAudioPatch.cs` installs, verifies and removes it with
      its own resource key. Payload shapes were read off the store's own consumers, and a store
      driven by them reports available with both devices present and a dual-direction headset read
      correctly. The session now owns one `AudioManager` shared with the taskbar's status cluster,
      so the namespace can answer while the taskbar is closed, and the patch registers whenever that
      manager exists. **What remains is confirming on a live tab that the Audio section actually
      renders** — the section gate is `!IN_VR && bAvailable`, and satisfying a data gate is never
      proof that the render gate above it opened. Attended.
- [ ] Supply `SteamClient.System.Audio` over `Shell\AudioManager.cs`,
      `Interop\NativeVolumeControl.cs` and `Shell\VolumeButtonService.cs`. The cheapest gate in the
      project: the store's flag is literally `m_bAvailable = null != SteamClient.System.Audio`, so
      supplying the namespace is the whole of it. Implement `GetDevices`,
      `SetDefaultDeviceOverride`, `SetDeviceVolume`, and the `DeviceAdded`/`DeviceRemoved`/
      `DeviceVolumeChanged`/`VolumeButtonPressed`/`ServiceConnectionStateChanges` registrations.
      HDMI CEC reaches a different service and stays unsupported.
- [ ] Report no audio apps until the mixer exists: `SetAppVolume`, `RegisterForAppAdded` and
      `RegisterForAppRemoved` have no backend, and `Shell\VolumeAppCommands.cs` is media-key
      decoding, not a mixer. Steam's per-app mixer then lists nothing rather than misbehaving.
- [ ] Add WASAPI audio-session enumeration and per-session volume to `native\VolumeControl`, exposed
      through `AudioManager`. This serves the custom taskbar as much as Steam — one backend, two
      surfaces — so it is its own item, not folded into the Steam adapter.
- [ ] Close two unknowns before building speaker configuration, both needing multichannel hardware
      that is not currently available: whether a 5.1 or 7.1 configuration can be read and written at
      all (the reference Claw exposes one stereo Realtek endpoint, and `PhysicalSpeakers` is absent
      on all six persisted render endpoints), and whether a re-enumerated HDMI endpoint keeps a
      stable identity across a display change — which decides whether reapply-on-churn can key on
      the endpoint id or needs a fuzzier match.
- [ ] Implement speaker configuration through `IPolicyConfig::SetDeviceFormat`, which
      `native\VolumeControl\VolumeControl.cpp` **already declares** with the correct vtable ordering
      and already uses for `SetDefaultEndpoint`. Do not write
      `PKEY_AudioEndpoint_PhysicalSpeakers` or `PKEY_AudioEngine_DeviceFormat` through
      `IPropertyStore`: the store does open `STGM_READWRITE`, but Microsoft documents those as
      service-owned and read-only for clients, and device-format writes are reported not to take
      effect. The point of the feature is that Windows loses the configuration across display
      changes, so WSGM persists the choice per endpoint and reapplies it on endpoint churn, the same
      shape as display profiles.
- [ ] Replace the `CAudio_SetSpeakerConfiguration` stub (`sink_id`, `config` → `config`, `channels`,
      `sdescription`) and `CAudio_PlaySpeakerTestOnChannel` so Steam's own dropdown and per-channel
      speaker test drive it.
- [ ] Validate attended: device switching while a game runs, volume buttons, per-app volume against
      a real mixer, and — once multichannel hardware exists — 5.1 and 7.1 selection, the per-channel
      test, and a display change with the configuration restored afterwards.

## Verified review findings from PR #19

Codex left 51 inline findings on PR #19 across seven review passes (commits `75494c6` … `e7386e2`).
Every one was re-checked against this branch's HEAD; the five listed under "checked and not carried"
at the end are not defects here and are recorded so they are not re-litigated. Line anchors are the
current tree, not the reviewed commit.

**All 43 are fixed.** Each item below is checked in the sense this file defines: source, focused
tests where the behaviour is deterministically testable, diagnostics, and comments complete, with
`eng\verify.ps1`'s automated gates passing. Every entry keeps the defect it describes so the fix has
its reason attached. The device and Steam ones do not carry their own attended gates — they inherit
the existing ones in S7 (controller acceptance), S8 (live Steam/RTSS/AutoTDP matrix), S9 (glyph
visual acceptance) and S10, which stay unchecked until they run on the reference device. Three
changes touch paths that only reveal their constraints there and must be re-verified before release:
the persistent-lighting rollback, the gyroscope staleness bound, and the two Steam UI patch changes
(the glyph probe's selector requirement and removing an applied patch that failed verification).

### AutoTDP

- [x] **One AutoTDP worker per lifetime.** `AutoTdpService.Apply(false)` clears `_enabled` and starts
      `StopAsync`, but never cancels `RunAsync` — the loop only ends on `_shutdown`, which fires in
      `DisposeAsync`. `Apply(true)` then starts a second loop and overwrites `_worker`
      (`src\WSGM\Shell\AutoTdpService.cs:110-121`). Every off→on cycle adds another one-second timer,
      so the controller evaluates the same window several times, reaches raise/probe thresholds early
      and races its own restoration; an already-admitted tick can also write after `StopAsync`
      restored the prior limit. Keep one lifetime worker, or cancel and await the previous generation
      before restoring and before starting another.
- [x] **Stop controlling when a power write is not applied.** `WriteAsync` logs `result.Outcome` and
      does nothing else (`src\WSGM\Shell\AutoTdpService.cs:281-315`) even though
      `AutoTdpController` has already advanced its believed wattage, so `Rejected`, `TimedOut` and
      `Indeterminate` all leave later decisions resting on a limit that may never have reached
      hardware. `StopAsync` then publishes "the previous limit was restored" unconditionally
      (`:337`) — including when `_write.WaitAsync(0)` refused the restore write outright because a
      tick write was still in flight. Inspect the outcome, pause or degrade after an unverified
      write, and report restoration only after a successful result.
- [x] **Route manual power writes through `NoteManualChange`.** It has no production caller: the
      overlay path (`src\WSGM\Shell\DeviceOverlayBridge.cs:692`) and the native-QAM TDP setter both
      call `DeviceCoordinator.ExecuteCapabilityAsync` directly, and only
      `tests\WSGM.Tests\AutoTdpServiceTests.cs:118` invokes the hook. A user's successful manual PL1
      change is therefore ordinary telemetry and the next tick overwrites it, contradicting the
      documented permanent-until-resume override. Send user-originated primary-limit writes through
      one shared path that pauses control, and rebase `_restoreTo` to the accepted manual value so a
      later disable does not restore the pre-AutoTDP limit over the user's own choice.
- [x] **Restore AutoTDP before the device coordinator is retired.** `ShellSession` awaits
      `_deviceCoordinator.ShutdownAsync` at `src\WSGM\Shell\ShellSession.cs:1471` and only disposes
      `_autoTdp` at `:1538`. AutoTDP's write delegate is that coordinator's
      `ExecuteCapabilityAsync`, so on application exit, update, uninstall and session end the
      restoration is issued into an already-disconnected capability path and the handheld is left on
      the last automatically selected wattage. Dispose AutoTDP and verify its restore first.
- [x] **Publish AutoTDP status changes to the surfaces that render them.**
      `AutoTdpService.StatusChanged` has no subscriber anywhere in production; `AttachAutoTdpStatus`
      gives the coordinator a snapshot getter only (`src\WSGM\Shell\ShellSession.cs:231`), and both
      the overlay bridge and native QAM refresh on coordinator configuration/capability events.
      State, watts, frametime and detail therefore stay stale on both surfaces until an unrelated
      device event happens to arrive. Root a subscription in `ShellSession`, marshal it to the
      dispatcher, and unsubscribe during teardown.
- [x] **Set the requested AutoTDP state instead of toggling it.**
      `NativeQamSemanticServices.SetEnabledAsync` reads `Current`, returns early when it already
      matches, then calls the blind `DeviceCoordinator.ToggleAutoTdpAsync`
      (`src\WSGM\Shell\NativeQamSemanticServices.cs:625-646`, `DeviceCoordinator.cs:1495`). A change
      from another surface between the read and the gate acquisition inverts the newer value and
      still reports success. Add an idempotent coordinator setter that compares and sets while
      holding `_transitionGate`.

### Controller management

- [x] **Serialize sample publication with UI neutralization.** `ControllerManager.RouteAsync` decides
      `toUi` under `_stateGate` and then publishes outside it
      (`src\WSGM\Shell\ControllerManager.cs:344-376`). A surface claim racing that decision can write
      its neutral packet first and have this stale live sample written after it; every later sample
      goes only to the UI, so the game keeps that press until capture is released. Revalidate a
      capture generation immediately before publication, or share the gate.
- [x] **Close sample admission before make-safe neutralizes.** `MakeSafeUnderGateAsync` awaits
      `_router.NeutralizeAsync` and only afterwards sets `_zeroTriggers |= TargetRemoved` under
      `_stateGate` (`src\WSGM\Shell\ControllerManager.cs:522-536`). A sample arriving in that window
      sees no trigger and no capture, finds the router `Neutral`, re-activates the source and
      publishes a non-neutral report; the handoff then proceeds as if the target were quiet. Set the
      trigger first and drain already-admitted routes under the same gate.
- [x] **Do not record a failed virtual-target removal as successful.** When `_router.RemoveAsync`
      throws, the catch logs and `sequence.RecordTargetRemoved()` still runs
      (`src\WSGM\Shell\ControllerManager.cs:552-563`), and that method sets `_targetRemoved` without
      touching `_unverified` (`ControllerMakeSafeSequence.cs`), so `Complete()` can return
      `ReleasedVerified` while the virtual controller is still enumerated beside the newly exposed
      physical one. The same applies to a failed `NeutralizeAsync` before `RecordNeutralized()`.
      Keep continuing the sequence — that ordering is deliberate — but force the unverified result.
- [x] **Check the VIIPER removal status before forgetting the target.**
      `RemoveDeviceUnderGate` clears `_deviceId`/`_fastHandle` and then discards
      `viiper_device_remove`'s status through the `Func<int>` overload of `SafeNative`
      (`src\WSGM\Input\ViiperControllerBackend.cs:431-443`, `:465`), so a nonzero result is neither
      logged nor acted on. `WaitForRemovalAsync` reports success purely from the cleared managed
      state (`:214-220`), which lets a failed detach leave the old virtual controller enumerated
      while replacement and HidHide cleanup proceed. Use `Check`, log the status, and hold an
      unverified target state until removal is observed.
- [x] **Fault the target when VIIPER rejects an input frame.** `SubmitUnderGate` returns false on a
      nonzero `viiper_device_set_input_fast` (`src\WSGM\Input\ViiperControllerBackend.cs:420-429`)
      and `ManagedControllerRouter.RouteAsync` simply propagates that false
      (`src\WSGM\Input\ManagedControllerRouter.cs:525-532`): the target stays `Active`, nothing is
      logged, no `TargetLost` is raised, and the host keeps the last successful report — a held
      button included — while WSGM still reports controller management active. Treat a rejected
      submission as target loss, emit the diagnostic, and run make-safe.
- [x] **Recheck the haptic route after acquiring `_sinkGate`.** The output worker validates
      `_routeGeneration` under `_gate` and then awaits `_sinkGate` without rechecking
      (`src\WSGM\Input\ManagedControllerRouter.cs:314-330`), while `StopAsync` bumps that generation
      under `_gate` and takes `_sinkGate` separately (`:174-204`). If stop wins the gate, its silent
      frame is followed by this stale non-silent one and the plugin latches it, so vibration
      survives the neutralization. Recheck inside the gate or serialize invalidation, stop and apply
      under one admission mechanism.
- [x] **Close haptic admission before ownership is withdrawn.** `DeviceHostHapticSink.ApplyAsync`
      reads `IsOwned` (taking and releasing `_gate`) and then invokes the asynchronous DeviceHost
      write outside it (`src\WSGM\Shell\DeviceHostHapticSink.cs:91-95`), so a `Withdraw` from
      `DeviceCoordinator.Detach` can complete while an admitted frame is still in flight to a plugin
      that has already handed the controller back — and the plugin latches the last rumble values.
      Serialize admission and completion with withdrawal, or cancel and await admitted frames before
      reporting ownership withdrawn.
- [x] **Drain a sample that arrives while dispatch is running.**
      `DeviceHostClient.DispatchLatestSample` returns immediately when `_sampleDispatching` is
      already set (`src\WSGM\Shell\DeviceHostClient.cs:387-392`), and the auto-reset state event has
      already been consumed by that callback. If the first callback read the ring before the newer
      sample was written, that sample — typically the final button-release packet — waits for some
      later input, leaving the virtual controller on stale state. Record a pending notification or
      loop until the sequence stops advancing before clearing the gate.
- [x] **Tell the plugin when controller management is disabled.**
      `SetControllerManagementUnderGateAsync` runs make-safe and returns without ever sending
      `SetControllerManagementAsync(enabled: false)` (`src\WSGM\Shell\DeviceCoordinator.cs:1341-1353`).
      The Claw plugin keeps `ControllerService.Enabled = true`, so after a suspend/resume of the same
      host cycle `ResumeServicesAsync` reacquires and switches the physical controller against the
      persisted setting, with no WSGM target to receive it. Send the disable after the verified
      handoff.
- [x] **Transition capability consumers when controller management is enabled mid-cycle.**
      `DeviceHostSession.ControllerManagementAsync` calls
      `_adapter.SetCycleGeneration(request.CycleGeneration)`
      (`src\WSGM.DeviceHost\DeviceHostSession.cs:833`), which resets `_descriptorGeneration` to zero
      (`PluginHostAdapter.cs:245-254`). The plugin then acquires the controller and calls
      `PublishCapabilityStatesAsync`, which still stamps `_descriptorSet.Generation`
      (`plugins\WSGM.Device.Msi.Claw8A2Vm\Claw8A2VmPlugin.cs:1332`), so the adapter rejects the first
      state as stale and the enable request faults *after* hardware acquisition; WSGM's
      `DeviceCapabilityRouter` is also still attached at the old cycle generation, and an identical
      config reload does not retry. Republish descriptors for the new cycle and move the consumers
      onto it before accepting states.
- [x] **Run the HidHide readability check on the mid-cycle enable path too.** Cycle start now calls
      `EnsureHidHideReadableAsync` before `client.StartAsync`
      (`src\WSGM\Shell\DeviceCoordinator.cs:758`), but enabling controller management inside a
      running cycle goes straight to `client.SetControllerManagementAsync(enabled: true)` (`:1355`)
      and asks the plugin to discover an interface another application's HidHide allowlist may still
      be hiding from DeviceHost. Add the same pre-acquisition allowance and report existing HidHide
      blocking explicitly.
- [x] **Stop advertising controller targets the backend cannot create.** The overlay cycles all three
      (`src\WSGM\Shell\DeviceOverlayBridge.cs:802-804`), native QAM lists Xbox 360 and DualShock 4 as
      available (`src\WSGM\Shell\NativeQamSemanticServices.cs:847-848`) and Settings exposes the same
      indices, but `ViiperControllerBackend.Supported` is `[SteamDeckComposite]` and
      `CreateTargetAsync` throws for the other two (`src\WSGM\Input\ViiperControllerBackend.cs:42`,
      `:93-96`). Selecting an advertised target therefore leaves controller management
      `Unavailable`. Implement the two encoders (they are fixed 2.0 scope, see S7) or gate the
      selectable values on the backend's real capability until they exist.

### Managed UI input

- [x] **Marshal managed controller samples to the UI thread.** The subscription at
      `src\WSGM\Shell\ShellSession.cs:280` is raised from `DeviceHostClient`'s registered ThreadPool
      wait and runs synchronously through `ControllerManager.RouteAsync` →
      `OverlayController.SubmitCanonicalSample` → `UiInputRouter.Submit` →
      `GamepadNavigation.OnButtons`, which reads `_window.IsVisible` and mutates Avalonia focus,
      ComboBoxes and windows directly. With managed input active, any button press on a visible WSGM
      surface performs UI work off the dispatcher. Post the sample to `Dispatcher.UIThread` once
      before it enters the navigation pipeline.
- [x] **Suppress controls already held on the first managed sample.** `UiInputRouter.Submit` calls
      `BeginSwitch` before `_managed.Submit(sample)`, so the suppression mask is taken from a
      `_managed.Held` that is still zero (`src\WSGM\Input\UiInputRouter.cs:61-85`, `:114-140`). A
      button held while controller management comes online is delivered as a fresh press and can
      activate or dismiss whatever has focus. Initialize suppression from the translated first
      incoming sample and hold it until release.
- [x] **Fall back to SDL whenever controller management leaves Active.** `ShellSession` drives
      `ManagedInputLost()` from the device-cycle state only
      (`src\WSGM\Shell\ShellSession.cs:281-288`), but disabling controller management runs make-safe
      and leaves the cycle Active while the plugin stops publishing samples. `ControllerManager`
      raises `ControllerStatusChanged` (forwarded at `DeviceCoordinator.cs:1520`) and nothing
      subscribes, so `UiInputRouter` stays on the silent managed source and WSGM's surfaces stop
      responding to a controller that SDL can already see. Drive the fallback from every controller
      status other than Active.

### Device cycle and plugin

- [x] **Apply profile values when a hardware profile is selected.**
      `SelectHardwareProfileAsync` persists the choice and calls `UpdateCapabilityDesiredContext`
      (`src\WSGM\Shell\DeviceCoordinator.cs:1723`), which only republishes the router's desired-value
      projection (`DeviceCapabilityRouter.UpdateDesiredContext`); no `ExecuteAsync` runs for any
      affected capability and nothing else reconciles desired values onto hardware. The Profiles page
      reports the profile active and claims it overrides power/battery defaults while the device
      keeps its previous values. Reconcile the newly resolved values through the serialized command
      path, retaining per-capability failures.
- [x] **Wire device suspend and resume into the session.** `DeviceCoordinator.SuspendAsync` and
      `ResumeAsync` (`src\WSGM\Shell\DeviceCoordinator.cs:353`, `:376`) have no production callers,
      and `MessageWindow` raises only `SessionUnlocked`/`SessionEnding`
      (`src\WSGM\Interop\MessageWindow.cs:45-48`), of which `ShellSession` subscribes to the latter.
      The Claw's controller, motion, OEM and suppressor services therefore stay live across lock and
      system sleep and no fresh cycle generation is established afterwards. Subscribe the session
      root to lock/suspend and resume/unlock, observe both asynchronous calls, and unsubscribe during
      teardown.
- [x] **Degrade malformed WMI responses instead of faulting the whole cycle.**
      `MsiWmiPlatform` throws `InvalidDataException` for an invalid `Package_32` payload, a bad
      status or multiple active `MSI_ACPI` instances (`:158`, `:166`, `:171`, `:191`), but the
      recoverable filter at `plugins\WSGM.Device.Msi.Claw8A2Vm\MsiWmiPlatform.cs:249` catches only
      `ManagementException`, `IOException`, `UnauthorizedAccessException` and a foreign
      `OperationCanceledException` — `InvalidDataException` derives from `SystemException`, so it
      escapes `WindowsClawIdentityReader.ReadAsync`, fails plugin startup and takes down controller,
      motion and OEM services that never needed WMI. The comment in that branch already says a
      malformed response is meant to land there; include it.
- [x] **Expire stalled gyroscope samples before forwarding them.**
      `PublishControllerSampleAsync` attaches `_motion.Latest` to every controller sample
      unconditionally (`plugins\WSGM.Device.Msi.Claw8A2Vm\ClawResources.cs:889-891`). If the WinRT
      sensor stops raising `ReadingChanged` while DirectInput keeps reporting, the last non-zero
      angular velocity is replayed through the virtual Deck indefinitely. `SensorTimestamp` is
      already carried (`WindowsMotionSource.cs:109`) and unused here. Drop motion older than a
      bounded sensor interval, report the degraded motion service, and re-verify the combined
      controller path on the reference device.
- [x] **Roll back an unverified persistent lighting write.** When the MCU accepts the 32-byte profile
      but the readback differs, the command returns `Indeterminate` with
      `Rollback = RollbackResult.NotRequired` and no restore
      (`plugins\WSGM.Device.Msi.Claw8A2Vm\ClawCapabilities.cs:613-622`). That profile persists across
      reboot, so a partial or normalized write leaves an unintended profile permanently active while
      the UI reports failure. Retain the exact pre-write profile, restore and verify it on mismatch
      or a post-write exception, and report the resulting rollback status. Attended hardware
      verification before this is called done.

### Steam UI, RTSS and performance

- [x] **Require both glyph selector classes in the compatibility probe.** The probe returns
      `rowClass` and `logoClass` alongside `ok`
      (`src\WSGM\Core\SteamInputGlyphStylePatch.cs:80-121`), but `SteamUiPatchEvaluation.IsSuccessful`
      inspects only `ok` (`SteamUiPatchEvaluation.cs:76-88`), which is `!!document.head`. A Steam
      build that renamed either build-coupled class is still reported compatible and unique, so rules
      that can no longer match are installed instead of taking the documented native-rendering
      fallback. Parse the result and require both booleans. Live re-verification against a running
      client before this is called done.
- [x] **Give each Steam UI patch phase its own timeout.** `OperationTimeout` is documented as the
      maximum duration of *one* phase (`src\WSGM\Core\SteamUiPatchManager.cs:44`), but a single
      linked source spans probe, apply and verify (`:258-294`). A reachable but slow target that
      spends most of the budget probing has its otherwise in-budget apply or verification cancelled,
      and the patch drops to `Retrying`. Create a fresh linked timeout per phase.
- [x] **Remove a Steam UI patch after verification fails.** A successful `ApplyAsync` followed by a
      failed `VerifyAsync` only marks the patch `Degraded` (`src\WSGM\Core\SteamUiPatchManager.cs:290-295`);
      `RemoveAsync` is never attempted, so the unverified stylesheet or QAM bridge stays live instead
      of falling back to Valve's native UI, and later synchronization probes and reapplies over it.
      Attempt removal immediately, surface `RemoveFailed` when cleanup cannot be verified, and
      live-verify the apply/fail/remove sequence.
- [x] **Deliver glyph rules for absent-control-only profiles.**
      `SetGlyphDeliveryPatchStates` enables the stylesheet only when `StableResources` or
      `ControllerImages` are non-empty (`src\WSGM\Shell\SteamUiSessionHost.cs:307-311`), but
      `SteamGlyphCss.Build(..., hideAbsentControls: true)` emits real rules for a profile that only
      declares `AbsentControls` — a valid profile that hides trackpad or extra-paddle affordances
      while keeping Valve's artwork. Those controls stay visible. Include `AbsentControls.Count > 0`
      in the predicate (the patch already refuses an empty stylesheet).
- [x] **Reject a CEF connection completed after its last subscriber left.**
      `PersistentSteamUiTransport.ConnectAsync` assigns `channel.Connection`, sets `Ready` and calls
      `connection.Start()` inside `lock (channel.Sync)` without consulting `Subscribers`, `_disposed`
      or the cancelled reconnect generation (`src\WSGM\Core\PersistentSteamUiTransport.cs:230-270`),
      while `ReleaseAsync` and `DisposeAsync` only dispose whatever is stored at that moment
      (`:355-378`, `:420-445`). An in-flight connect therefore publishes a live socket and callbacks
      after its owner has gone. Revalidate ownership before assigning and dispose the stale wire.
- [x] **Move RTSS discovery off the UI-thread command path.**
      `RtssNativeAdapter.ProbeAsync` is fully synchronous — `RtssDiscovery.Probe()` then
      `Task.FromResult` (`src\WSGM\Core\RtssNativeAdapter.cs:25-61`) — and does registry, filesystem,
      signature, PE-export and process inspection. An overlay row's `Click` handler awaits
      `IPerformanceOverlaySource.InvokeAsync` on the UI thread
      (`src\WSGM\Overlay\OverlayWindow.axaml.cs:832-843`), and the uncontended
      `_adapterGate.WaitAsync` in `PerformanceService` completes synchronously
      (`PerformanceService.cs:343`), so the whole probe runs inline on the dispatcher. Run the
      adapter's blocking discovery and profile work off-thread at the service boundary.
- [x] **Recheck RTSS enablement after waiting for the adapter.** `PerformanceService` reads
      `_policy.Enabled` under `_stateGate` and then awaits `_adapterGate`
      (`src\WSGM\Core\PerformanceService.cs:324-343`) without rechecking. A Settings or config update
      that disables RTSS integration meanwhile takes no adapter gate of its own, so the queued
      command still persists and writes its value after the integration was switched off. Recheck
      after acquiring the gate, or route enablement changes through the same gate.
- [x] **Skip superseded running-application snapshots.**
      `RunningApplicationCoordinator.ApplyPendingAsync` takes one snapshot and then applies it to
      both consumers with no recheck of `_pending` between them
      (`src\WSGM\Shell\RunningApplicationCoordinator.cs:146-175`). A slow RTSS apply for application
      A can be followed by replacing the managed controller with A's per-application target after A
      has already exited and B was published, contradicting the class's latest-identity coalescing
      contract and disturbing controller enumeration during a launch. Track an apply generation or
      recheck `_pending` before each side effect.

### Settings, glyph presentation and SDK

- [x] **Preserve runtime-owned device values across a Settings save.** `SaveMerged` deliberately
      applies UI-owned fields over a fresh load, but `AutoTdpEnabled`, `ControllerTarget` and
      `GlyphSelection` are written unconditionally from the view model's construction-time snapshot
      (`src\WSGM\Settings\SettingsViewModel.cs:1177-1186`). In game mode the overlay and native QAM
      persist all three at runtime — `ToggleAutoTdpAsync`, the controller-target cycle and
      `CyclePhysicalGlyphSelectionAsync` — so saving any unrelated Settings field silently reverts
      whichever of them changed while the window was open, restarting or stopping hardware power
      control and reverting the active artwork and target policy. Track per-field local edits, or
      merge the freshly loaded runtime-owned values.
- [x] **Dispose raster glyphs when their control leaves the visual tree.** `PhysicalGlyphImage`
      disposes `_raster` only when the same control decodes a different PNG
      (`src\WSGM\Controls\PhysicalGlyphImage.cs:198-216`), while `RefreshDevicePanel` rebuilds the
      preview by clearing the visual tree and creating new instances. Each discarded raster-backed
      preview keeps its decoded native bitmap alive until finalization, so repeated capability-state
      refreshes in the resident shell accumulate native image memory. Release it on detach and when
      the plan stops being raster-backed.
- [x] **Repair the new device string enums before the second deserialize.**
      `ConfigJsonContext` sets `UseStringEnumConverter = true`
      (`src\WSGM\Core\AppConfig.cs:882`), so an unknown or hand-mistyped `ControllerTarget`,
      `GlyphSelection`, `DiagnosticLevel`, nested `ControllerTargets[].Target` or `OemAction` throws
      before `Normalize` can apply its `Enum.IsDefined` fallbacks. The `JsonException` recovery pass
      repairs only the older fields (`ConfigStore.cs:78-111`), so the retry throws too and `Load`
      moves the whole otherwise-valid file aside — including the registry recovery snapshots the
      preserve step exists for — and replaces every unrelated setting with defaults. Extend the
      repair pass to the device enums.
- [x] **Dispose mappings opened by `SharedStateRing.Open`.** `Open` constructs the ring with
      `ownsFile: false` (`src\WSGM.Device.Sdk\Ipc\SharedStateRing.cs:114-117`) and `Dispose` releases
      `_file` only for the `Create` path (`:241-244`), yet `MemoryMappedFile.OpenExisting` returns a
      handle the opener owns. Every open/dispose cycle leaks a section handle until the process
      exits, which SDK consumers that reopen rings in one process see first. Dispose it on both
      paths — the distinction governs mapping creation, not ownership of the wrapper.
- [x] **Let the glyph importer see an over-limit package.**
      `ImmutableGlyphPackageDirectorySource.EnumerateProfileIds` truncates with `.Take(32)`
      (`src\WSGM.Device.Sdk\Glyphs\ImmutableGlyphPackageDirectorySource.cs:56`), while
      `GlyphPackageImporter` detects the condition only through `discovered.Count > MaxProfiles`
      (`GlyphPackageImporter.cs:130`), which that truncation makes unreachable. A package with 33 or
      more profile manifests validates as conforming after silently dropping the extras. Return a
      sentinel past the limit, or let the importer truncate after recording the error.
- [x] **Reject sanitized fixture-name collisions.** `FixtureExtractionWorkflow` keys streams and
      analysis outputs by `SafeName(...)` (`src\WSGM.DeviceLab\Fixtures\FixtureExtractionWorkflow.cs:65`,
      `:78`), so two distinct source ids that normalize to the same name overwrite each other
      silently. The fixture then validates while omitting source data and expected results, and
      replay no longer represents the imported capture. Include a stable index or hash in generated
      names, or detect the collision and refuse extraction.

### Build and installer

- [x] **Restore the stopped runtime when post-install publication aborts.**
      `CurStepChanged(ssInstall)` sets `SetupInstallStarted := True`
      (`installer\WSGM.iss:831-834`), and `SetupShutdownApplied` is only cleared on the success path
      after `ReplaceDevicePluginSlot()` (`:838-840`). Any later failure — notably that procedure's
      `RaiseException` during `ssPostInstall` — reaches `DeinitializeSetup`, which skips
      `RestoreStoppedSetupRuntime()` because installation had started (`:1386-1392`), while the
      `[Run]` restart entries never executed. A failed update therefore leaves the previously running
      shell/Settings instance and the logon service stopped. Restore on every unsuccessful
      termination and suppress it only once publication has actually succeeded.
- [x] **Decide explicitly what to do about USB/IP versions above the pin.**
      `Install-UsbipDriver.ps1` treats `$installed -ge $RequiredVersion` as "already present" and
      exits 0 (`installer\Install-UsbipDriver.ps1:194-197`), so a machine carrying 0.9.7.8 keeps the
      build the same file says was excluded for open kernel-pool-corruption reports (`:54-56`) — and
      the patched VIIPER backend attaches to it happily. Either require the exact pin, or report the
      unreviewed driver and leave controller management unavailable; silently accepting it is the one
      option that hides the decision.
- [x] **Skip the optional VIIPER build when its toolchain is incomplete.** `build.ps1` gates the
      step on `Get-Command go` alone (`build.ps1:47-52`), but `eng\build-viiper.ps1` throws when
      `git` or a cgo-capable `gcc` is missing (`:53-75`), which under `$ErrorActionPreference =
      'Stop'` aborts the whole release build — contradicting the best-effort behavior the surrounding
      comment states. Include the C compiler and git in the optional prerequisite check, or catch and
      warn.

### Checked and not carried

- `src\WSGM\Core\SteamUiAssetCatalog.cs:18` — the pinned bootstrap hash matches the checked-in
  `NativeQamBootstrap.js` at HEAD (`981D696A…`); fixed since the reviewed commit.
- `src\WSGM\Shell\ControllerManager.cs:365` — `UiSampleReceived` now has its production subscriber at
  `src\WSGM\Shell\ShellSession.cs:280`, feeding `OverlayController.SubmitCanonicalSample`.
- `src\WSGM\Shell\DeviceCoordinator.cs:1433` — glyph identity is no longer passed as null;
  `PhysicalGlyphCatalog.SelectProfile` resolves against `_activeDeviceId`, set from the activation's
  `DeviceDefinitionId` (`DeviceCoordinator.cs:768`).
- `src\WSGM\Shell\DeviceCoordinator.cs:1360` — the cycle-start half of the HidHide finding is fixed
  (`:758`, before `client.StartAsync`); only the mid-cycle enable path remains, tracked above.
- `src\WSGM\Controls\PhysicalGlyphService.cs:132` — not accepted as an ownership violation. Both
  authorization inputs are resolved in Shell and passed in as booleans; the switch only maps a
  surface to which already-resolved input applies, which is presentation selection, not device or
  Steam policy.

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
      VIIPER, implemented, and verified end to end against the real library and the real driver — see
      S7. Everything this item listed as remaining is done: setup installs usbip-win2 from an
      explicitly ticked task, `device_attach` works and enumerates a real composite device, the idle
      cost is measured at 0.82% of the machine rather than the inherited per-core figure, and every
      navigation surface now runs on the managed UI source with SDL as a make-before-break fallback.
      **What remains is only the attended reference-device acceptance in S7** — targets, per-app
      selection, slots, duplicate input, suspend/resume, host fault and external owner, on hardware,
      with someone watching.
- [ ] **Q09 — Finish Steam/QAM/RTSS, the full-client unelevated Steam launch toggle, and implement
      the direct AutoTDP controller/replay.** The unelevated Steam toggle, the shared patch-evaluation
      helper, the device-verified RTSS frametime reader, the pure AutoTDP controller with trace
      replay, its session binding and user switch, and the deterministic TypeScript build with its
      drift gate are done. The per-component QAM implementation is done too — six independently
      probed patches, each losing only its own control on a client whose shape does not match — and
      AutoTDP has a surface in both places: a row on the overlay's Power and thermals page and a
      Valve `ToggleField` in the native QAM beneath the limit it moves, both moving the one stored
      setting through the one method. **What remains is the live Steam/RTSS/AutoTDP matrix**, which
      is attended.
- [ ] **Q10 — Finish static plugin glyph delivery and all WSGM/Steam consumers.** Delivery works and
      is live-verified: the plugin's profile becomes one WSGM-owned stylesheet, matching CSSLoader's
      mechanism so both can run at once. The four JS tier patches and the selector patch that nothing
      consumed are gone. The preview and input-test surfaces are built: the plugin's artwork is drawn
      with no SVG library, because the SDK normalizes it and the glyph service already turns that into
      Avalonia geometry, and the input test lights a control from the unfiltered physical sample.
      Navigation hints take the device's own button too. **What remains is visual acceptance with a
      real profile on a controller settings screen**, which is attended.
- [ ] **Q11 — Finish the overlay, shutdown/installer, and focused release validation.** The overlay
      is finished: four destinations with a bounded page stack, Back, and focus and scroll
      restoration; every Home/Steam/System action on its own page calling the owning service; all
      eight Device sections navigable, including the ones that needed more than a capability list —
      profile selection, the controller target, cycle recovery, and the glyph preview with its live
      input test. The installer carries the controller component and its user-approved driver step.
      **What remains is the release validation**, which is attended by definition, and the shutdown
      path, which is unchanged and already covered by its own items.
- [ ] **Q12 — Add per-application performance profiles by reactivating Steam's own performance UI.**
      Live probing on 2026-08-30 settled the whole approach: the SteamOS Performance tab ships in
      the Windows client and is not gated — `SteamClient.System.Perf` is simply absent, so the
      store's optional-chained registration no-ops and every control renders null. Supplying that
      one namespace turns Valve's own per-game profile toggle, profile header, frame-limit slider,
      overlay level, refresh rate, VRR, basic/advanced view and reset back on, with their localized
      explainers, and makes hiding free because availability is read from the `limits` WSGM
      supplies. VRR is proven on the reference unit through IGCL Arc Sync — read, write, verified
      read-back and exact restore, unelevated — and belongs to the Device Plugin under the standing
      boundary rule. Frame limiting ships as three user-configurable strategies:
      `FrameLimitOnly`, `NativeModes`, and full granular `FrameDoubling` over runtime-discovered
      modes, which are real: 48 Hz applied on a panel whose EDID lists only 60 and 120, with DWM
      reporting 47.997 Hz. See `_plan\qam-overhaul.md` and S12. **What remains is all of it** — no
      code has been written; the probes are throwaways plus the retained
      `tools\WsgmLibTest\probe-perf-*.js` evidence.
- [ ] **Q13 — Let a plugin declare its own settings page, in sections, and author profiles.** The
      declarative vocabulary is mostly already there and already wired — `GenericToggle`,
      `GenericChoice`, `GenericRange`, `GenericAction`, `GenericReadOnly`, `Color` and `Curve` are
      validated by `DeviceCapabilityRouter` and projected by `DeviceOverlayBridge`. Three things are
      missing: a text kind, a way to declare sections and assign elements to them, and a Settings
      surface. The boundary is fixed and is what shapes the work: a plugin **setting** configures
      plugin behaviour and is stored by WSGM, a **capability** writes hardware and stays in the
      overlay. Settings additionally gains RGB and fan profile authoring with a curve editor, which
      exists nowhere in the tree today even though `FanCurve` is declared and projected. See
      `_plan\plugin-driven-settings-page.md` and S13. **What remains is all of it** — no code has
      been written.
- [ ] **Q14 — Revive Steam's Quick Settings tab, Internet page, and Bluetooth.** Live probing on
      2026-08-30 produced the gate taxonomy that governs every Steam UI revival: supply an absent JS
      namespace, supply an absent RPC response, or override one Deck-only store getter — never the
      global `TS.IS_STEAMOS`, which is the D16 spoof. Wi-Fi is close to free: Steam's network
      subsystem already runs on Windows with a live wireless device and access points, and only
      `get networkManagementAvailable(){return TS.IS_STEAMOS}` hides it. Bluetooth rides the same
      SteamOS Manager RPC seam as TDP. Brightness, display scaling, controllers and game recording
      are natively backed; audio, night mode and resolution are backed by mechanisms WSGM already
      owns. See `_plan\qam-quick-settings.md` and S14. **What remains is all of it.**
- [ ] **Q15 — Revive Steam's audio settings.** The store's availability flag is literally
      `m_bAvailable = null != SteamClient.System.Audio`, so supplying that one namespace is the
      whole gate — the cheapest in the project — and it runs over `AudioManager` and
      `native\VolumeControl`, which already own devices, volume, mute and default-endpoint
      switching. Two additions: per-application volume, wanted by the custom taskbar as much as by
      Steam, so one WASAPI backend serves both; and speaker configuration through
      `IPolicyConfig::SetDeviceFormat`, which the helper already declares and already uses for
      `SetDefaultEndpoint`. That last one exists because Windows loses the configuration across
      display changes, so WSGM persists and reapplies it — but it is blocked on multichannel
      hardware to prove 5.1/7.1 at all, and on whether HDMI endpoint identity survives a display
      change. See `_plan\steam-settings-audio-revive.md` and S15. **What remains is all of it.**

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
