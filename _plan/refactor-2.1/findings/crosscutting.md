# Cross-cutting and uncovered code findings

Scope: everything no domain review owned. That is the maintainer-reported USER-001, the completeness
critic's CRIT findings, the 27 cross-domain conflicts between domain reports (CONFLICT-NN, each with
the resolution plan v2 adopted), the plan and requirement items no domain addressed (GAP-NN), and a
fresh line-by-line review of the uncovered application sources the critic listed (UNCOVERED-NNN).
Baseline is `master` 1329813f. Sources: `_plan/refactor-2.1/review/_critic.md`,
`_plan/refactor-2.1/review/_user-reported.md`, `_plan/refactor-2.1/refactor-plan-v2.md` (batch ids
`B001`...`B179`, decisions `D1`...`D14`).

Files reviewed in full for the UNCOVERED section: `src/WSGM/Core/Sounds/SoundPackLibrary.cs`,
`Core/Sounds/SoundsConfig.cs`, `Shell/SoundPackService.cs`, `Core/SteamAutostart.cs`,
`Core/SteamAutostartTakeover.cs`, `Core/SteamAutostartService.cs`, `Core/KnownStartupApps.cs`,
`Core/WindowsPolicyOperation.cs`, `Core/DesktopAppProcessBackend.cs`,
`Core/DeviceProfileValidation.cs`, `Core/ApplicationProfileRules.cs`, `Core/Credits.cs`,
`Core/DevicePrerequisites.cs`, `Core/DisplayOperatingPoint.cs`, `Core/DisplayTimeoutPolicy.cs`,
`Core/ExceptionList.cs`, `Core/FanCurvePresets.cs` (checked against HC `IDevice.fanPresets`:
identical), `Core/FileCleanup.cs`, `Core/ForegroundApplicationFilter.cs`,
`Core/ManualTdpProfile.cs`, `Core/ModernStandbyPolicy.cs`, `Core/NativeQamPerfProjection.cs`,
`Core/ObservableObject.cs`, `Core/PerApplicationPowerPolicy.cs`, `Core/PerformanceModels.cs`,
`Core/RelayCommand.cs`, `Core/SteamGlyphPresentation.cs`, `Shell/AnimationBrowseSession.cs`,
`Shell/ApplicationProfileSyncBuilder.cs`, `Shell/CommonPluginDependencyPlan.cs`,
`Shell/GameModeCardServicePolicy.cs`, `Shell/GameWindowReturn.cs`, `Shell/IChangeSource.cs`,
`Shell/IExtensionsTabSection.cs`, `Shell/NativeQamCpuBoostService.cs`,
`Shell/NativeQamHybridCoreService.cs`, `Shell/OverlayToolSessions.cs`,
`Shell/SimulatedGraphicsOverlaySource.cs`, `Shell/SplashPolicy.cs`, `Shell/SplashStyle.cs`,
`Shell/SteamExtensionsTabBackend.cs`, `Shell/SteamGameContextMenuBackend.cs`,
`Shell/SteamPowerMenuBackend.cs`, `Shell/ThemeBrowseSession.cs`,
`Shell/VolumeIndicatorWindow.axaml.cs`, `Interop/OverlayMediaNative.cs`, plus `Interop/LastInput.cs`
and `Interop/TouchKeyboard.cs` (named by the critic as reviewed by nobody) and the four tool files
(`eng/checkout-controller-dependency-sources.ps1`, `eng/extract-hc-devices.ps1`,
`tools/WsgmLibTest/capture-steam-window.ps1`, `tools/PerfLab/WSGM.PerfLab.csproj`).

Counts: 58 findings need work. Critical 0, high 9, medium 23, low 20, nit 6. One more id
(CONFLICT-06) is no-change by maintainer decision and listed at the end. By kind, across all 59 ids:
USER 1, CRIT 6, CONFLICT 27, GAP 15, UNCOVERED 10.

Maintainer decisions (`_plan/refactor-2.1/DECISIONS.md`, 2026-10-03) applied here: D1 is decided for
B140 (CONFLICT-01); D2 settles the sound-pack zip-bomb bound (CRIT-002, UNCOVERED-004); D3 makes
Device Lab GPL, so CONFLICT-22 moves `NativePackageSource` without any interop relicensing; D9
removes readback-based unresolved entries and re-arm rules for every non-Claw package and host path
(CONFLICT-19, UNCOVERED-010); D14 and all security hardening are dropped, which reduces GAP-03,
GAP-04 and GAP-10 to their functional parts and moves CONFLICT-06 to no-change; plugin Steam UI
modules register when the plugin becomes ready (CONFLICT-05); RTSS starts with WSGM and is kept
alive, as USER-001 already says.

Plan v2 batches that carry this area: B005 (USER-001), B016 and B017 (CRIT-001, CRIT-003), B038
(CRIT-005), B112 (CRIT-004), B138 (CRIT-002, CRIT-006), the never-strand group B006, B007, B009, the
install batches B024, B025 and B027 to B031 (B026 is removed), B068, B178 and B179 (coverage,
ledger, frozen check), and the batches named per conflict below. Implementers anchor every edit by
symbol; cited line numbers are from 1329813f and may drift.

## High

### CONFLICT-01: Shutdown model, percentage cutoffs versus ordered steps under one deadline

- **Severity:** high
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs`,
  `src/WSGM/Shell/ShellSession.Composition.cs`, `src/WSGM/App.axaml.cs`,
  `src/WSGM/Core/ApplicationShutdown.cs` (becomes `ApplicationRuntime.cs`),
  `src/WSGM/Shell/DeviceCoordinator.cs`, `src/WSGM/Shell/SteamUiSessionHost.cs`; reports session
  R1/R2 against device, config B4, steamhost B5, winsvc B10, toolkitcs A5, input change 2.
- **Problem:** The session review replaces the Codex B3 percentage cutoffs (30/50/70/90 %), the 500
  ms preliminary drain and the `SessionLifetime` five-state machine with a fixed safety-first step
  list under one deadline. Five other reports still build on the B3 phases or on a `SessionLifetime`
  step registry. Implementing both shapes would rewrite the shutdown and every owner's stop API
  twice, and the cutoff design orders code that today is not even reachable (SESSION-V-001).
- **Best solution:** Adopt session R1/R2 (decision D1). Every owner exposes a synchronous
  `CloseAdmission()` called at T0 and `StopAsync(Deadline)` that awaits at most the deadline and
  retains anything still running. No phases, no registry, no per-owner budgets. B140 implements the
  order: (0) record reason and sticky SessionEnd, close admission everywhere; (1) power queue tail
  and startup task; (2) AutoTDP restore; (3) device shutdown with controller release and HidHide
  show; (4) common plugins and GPU; (5) transition, boot worker, gate loop; (6) tray retire and
  verify; (7) Explorer restore only when the reason allows and the tray is gone; (8) Steam host
  retraction and transport; (9) UI-thread feature owners; (10) native providers; (11) MessageWindow
  last. One `RunStepAsync(name, ui, step)` helper replaces the repeated 600-line block. It beats the
  cutoff design because nothing unrelated is awaited before the safety steps, so the cutoffs fix no
  defect.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionShutdown|FullyQualifiedName~ApplicationRuntime"`
  with the B140 cases (each exit reaches the session shutdown once, OS end-session has zero Explorer
  calls, a hung transition does not delay device safety, repeated shutdown returns the same task).
- **Plan v2:** B140 (decided: D1, safety-first ordered steps under one deadline; the B3 cutoffs and
  preliminary drain are dropped), with owners' `CloseAdmission`/`StopAsync` landing in B040, B083,
  B136; base fix B006.
- **Related:** SESSION-019, SESSION-030, SESSION-033, SESSION-V-001, CONFLICT-02, GAP-09.

### CONFLICT-03: Controller release ownership and its unbounded wait

- **Severity:** high
- **Where:** `src/WSGM/Shell/ControllerManager.cs` (`ReleaseAsync`, `DisposeAsync`),
  `src/WSGM/Shell/DeviceCoordinator.cs` (four release call sites, symbol-anchored near 1236, 1412,
  1619, 1710), `src/WSGM/Shell/DevicePluginRuntime.cs`.
- **Problem:** DEVICE-B3 wants a bounded device shutdown, but `ControllerManager.ReleaseAsync` has
  no deadline (device.verify 3). INPUT-B1 changes it to `ReleaseAsync(Deadline)` but does not list
  the four `DeviceCoordinator` call sites it breaks (input.verify), and DEVICE-B7 needs a
  `ControllerManager` built at the root (INPUT-B3). Landing in the wrong order either leaves
  shutdown unbounded or breaks the build, and the release path is where the HidHide show
  (never-strand) lives.
- **Best solution:** INPUT-B1 owns the signature and the four call sites and lands first (B009):
  `ReleaseAsync` takes the caller's deadline and attempts every step, with the HidHide show and
  `SetState` in `finally` unless a fault restart keeps the pad hidden. INPUT-B3's constructor change
  follows (B074), then DEVICE-B3 (B083) and DEVICE-B7 (B092).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ControllerManager|FullyQualifiedName~HidHideOwnership|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B009, B074, B083, B092.
- **Related:** INPUT-001, INPUT-008, DEVICE-003, DEVICE-004, GAP-09.

### CONFLICT-19: DeviceRecoveryJournal edited by three batches, including a latch that loses controls

- **Severity:** high
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs`; admitted Codex batch A02_02,
  SDK-B1, PACKAGES-B2.
- **Problem:** A02_02, SDK-B1 and PACKAGES-B2 all edit the journal. sdk.verify and packages.verify
  V-006 reject A02_02's save-failure latch: one transient file lock would turn into the loss of
  every journalled control. The device review also wants DEVICE-001 (an unverified stop blocks every
  restart) fixed before A02_02. A02-F008 (Codex High) is contradicted by the admitted batch.
- **Best solution:** B008 fixes DEVICE-001 first. B010 then applies A02_02 steps 1 and 4 only
  (`LoadAsync` treats only `FileNotFoundException` as absent; undefined statuses refused before IO)
  plus SDK-B1 without its extra lock; the post-gate re-check and the latch are dropped, A02-F008
  becomes no-change. PACKAGES-B2 runs inside the Device API 12 group (B143 after B142), shaped by
  D9: readback-based recovery states exist only for the Claw, the one vendor whose state can be read
  back. The Claw keeps the PACKAGES-B2 rules (unresolved `RestoredUnverified`/`RestoreFailed`
  entries are kept and never written automatically, an explicit command re-arms, and the Claw
  controller entry re-reads the mode and restores at every start). The Ally, and any other non-Claw
  package, has no unresolved or uncertain entry and no re-arm rule: a release writes the recorded
  state and removes the entry once the writes dispatched, publishing them as written; a write that
  failed to dispatch leaves the entry `Pending`, so the next release writes it like any pending
  entry (nothing reached the device, so this is not a retry of an uncertain write). In code that
  means `AllyServices` power and fan release and `AllyControllerService` release stop deriving
  `RestoredVerified`/`RestoredUnverified` or `ReleasedUnverified` from a readback,
  `RogAllyPlugin.Recovery` stops recording `RestoreFailed` from a readback result, the
  `RestoredUnverified or RestoreFailed` keep branch in `AllyRecoveryJournal` goes, and the "enabling
  controller management re-arms the Ally controller entry" rule is not added. The SDK journal keeps
  its statuses for the Claw; PACKAGES-001 in `packages.md` carries the package-level steps.
- **Tests:**
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Services"`;
  for B143 the `DeviceRecoveryJournalTests` and package filters listed in the batch, with these Ally
  cases: a release whose writes dispatched removes the entry and reads nothing back; a write that
  throws before dispatch leaves the entry `Pending` and the next release writes it; no Ally path
  ever records `RestoredUnverified` or `RestoreFailed`.
- **Plan v2:** B008, B010, B143 (decided: D4 approved, each guidance diff shown with its batch; D9
  no readback machinery outside the Claw, so the Ally controller-entry re-arm and every non-Claw
  unresolved entry are removed).
- **Related:** A02-F005, A02-F007, A02-F008, A02-F010, SDK-002, SDK-003, PACKAGES-001, DEVICE-001.

### CONFLICT-21: Publication model for submodule changes

- **Severity:** high
- **Where:** `external/windows-device-control`, `external/steam-ui-toolkit` and their parent
  consumers; build R2, wdc.verify 1 (I01/I02 phases), toolkitjs B5a/B5b.
- **Problem:** build R2 publishes per batch (child commit and push, then the parent gitlink with
  consumer edits), as CLAUDE.md requires. wdc.verify binds the planner's I01/I02 integration phases,
  and toolkitjs B5a/B5b leave the parent "on the old gitlink". But the parent uses
  `ProjectReference` into the child working tree, so the parent build breaks as soon as B5a lands
  without its consumers.
- **Best solution:** Per-batch publication, because CLAUDE.md wins over a planner addition. Each
  library API batch carries its consumers in the same parent commit: TOOLKITCS-B3 with library B5,
  the steamhost explicit-client edits and overlay B8's outcome mapping (B053); TOOLKITJS B5a and B5b
  merge (B057); WDC-B7 rebuilds the NVIDIA plugin (B069). Child-local validation runs in every child
  batch (GAP-07).
- **Tests:** each child batch's filter on both WDC frameworks or the toolkit suite plus
  `npm run prelude:claims`, and the parent build
  `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true`.
- **Plan v2:** execution protocol items 7 and 8; B053, B057, B069; full child validation in B062,
  B072, B179.
- **Related:** GAP-01, GAP-07.

### CONFLICT-23: Settings save edited by six batches

- **Severity:** high
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs`, new `SettingsSaveMerge.cs`,
  `src/WSGM/Core/Profiles/ProfileEdits.cs`.
- **Problem:** config B1/B2b/B4 and settings B1/B3/B7 all edit the save path. Config C3 routes
  profile writes through `ProfileEdits` while settings B1 rebuilds the merge. SETTINGS-001 is live
  data loss (a Settings save reverts Steam-side choices, including the recovery flag
  `Animations.SteamSetAside`), so ordering decides whether the data-loss fix lands on stable code.
- **Best solution:** SETTINGS-B1 merged with B2's shared-field table first (B013):
  `SettingsSaveMerge.Apply(fresh, request, splash)` starts from the fresh strict load and copies
  only Settings-owned fields, shared fields live in one `WsgmSharedSettings` table written only when
  edited, and the merge calls `ProfileEdits.RemoveFanCurveReferences` on the fresh scope. Config
  then edits the new merge (B039 read outcomes, B040 profile ownership).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~WsgmSteamSettings"`
  and the UiTests Settings filter in B013.
- **Plan v2:** B013, then B039, B040, B117, B133.
- **Related:** SETTINGS-001, SETTINGS-V-001, SETTINGS-V-002, CONFIG-022.

### GAP-01: The WindowsDeviceControl child holds an uncommitted W02_01 edit

- **Severity:** high
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs`,
  `external/windows-device-control/tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs`.
- **Problem:** The parent `git status` reads clean because the gitlink is unchanged, but the child
  working tree carries the W02_01 edit uncommitted. Any WDC batch started on top of it builds on an
  unrecorded change and would sweep it into an unrelated commit.
- **Best solution:** B001 first: normalize the new lines to CRLF (WDC-012), build both TFMs, run the
  three W02_01 tests, commit the two files in the child on `main`, push, then record the gitlink in
  the parent with a pathspec commit. Every later WDC batch starts from that commit.
- **Tests:**
  `dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~EdidIdsFollowOnlyTheValidityBit|FullyQualifiedName~ValidZeroEdidIdsArePreserved|FullyQualifiedName~ExistingPathIdentitySurvivesEdidPopulation"`.
- **Plan v2:** B001.
- **Related:** WDC-012, A01-F001, CONFLICT-20.

### GAP-03: The install domain had no batches, and its report is truncated

- **Severity:** high
- **Where:** `src/WSGM.LogonService/**`, `src/WSGM.Setup/**`, `src/WSGM.Install/**`,
  `src/WSGM.Launch/**`, `src/WSGM/Core/UnelevatedLauncher.cs`;
  `_plan/refactor-2.1/review/install.md` stops inside INSTALL-010.
- **Problem:** Two high findings (INSTALL-001: the SYSTEM logon service starts `boot.json`'s
  user-writable `ExePath` elevated; INSTALL-002: elevated setup runs payloads staged in the user's
  temp), INSTALL-003 (`%ProgramData%\WSGM` default DACL), INSTALL-008 (task XML staged in a
  user-writable folder) and INSTALL-V-003 (uninstall with `App\WSGM.exe` missing deletes the only
  HidHide ledger) had no batch. The bodies of INSTALL-004, 011 to 014, 018, 019, 021 to 025, 028 to
  031 and 034 to 046 were never written, and INSTALL-V-001 (single-file host extracts natives to the
  user's temp before setup code runs) is plausible only.
- **Best solution:** The batches plan v2 built from `install.verify.md`, reduced to their functional
  parts because the maintainer dropped all security hardening (DECISIONS.md): B007 (ledger kept when
  App is missing, Close starts WSGM only after success); B024 (read-only closure writing one
  disposition per unwritten id and appending any fix as a batch before B030, with security-only
  findings recorded as no-change by that decision); B025 (clean service stop only: the
  INSTALL-007/A02-F019 stop flag checked under the gate before `TryLaunch`; the elevated branch
  keeps launching `boot.json`'s `ExePath`, and the logon service keeps its single-file publish);
  B027 (only functional `UpdateChecker` work, the U04A-LFA-010 download fixes with the U04A-LFA-011
  `.partial` cleanup, both owned by `ledger-u04.md`; no DACL change and no download move); B028
  (answers applied last, truthful partial-change report); B029 (only U04B-LFA-012: one `/Delete`
  after the dispatch budget closes; the task XML stays where it is staged today); B030 (identity
  refusal in `Detect`, exact component match); B031 (cross-process names in one linked file, without
  the INSTALL-010 broker constants). B026 is removed. INSTALL-001, -002, -003, -008, -010 and
  INSTALL-V-001 are no-change by maintainer decision (security theater, DECISIONS.md), recorded in
  `install.md`. Decided items are not reopened (INSTALL-005 refusal, INSTALL-007 stop flag).
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup"`
  and the per-batch filters (`UnelevatedLauncher`, `Launch`, `Logon`, `UpdateChecker`); matrix rows
  M01-48, M01-49 (without its "starts only the installed WSGM" clause), M01-50.
- **Plan v2:** B007, B024, B025, B027 to B031 as reduced; B026 removed; decided: D14 dropped as
  security theater, so INSTALL-V-001 gets no VM check and no change.
- **Related:** INSTALL-001, INSTALL-002, INSTALL-003, INSTALL-008, INSTALL-V-001, INSTALL-V-003,
  GAP-04, GAP-09.

### GAP-09: Never-strand acceptance was spread over four domains

- **Severity:** high
- **Where:** `src/WSGM/Core/ApplicationShutdown.cs`, `src/WSGM/App.axaml.cs`,
  `src/WSGM/Shell/ControllerManager.cs`, `src/WSGM/Shell/HidHideOwnership.cs`,
  `src/WSGM/Shell/DeviceCoordinator.cs`, `src/WSGM.Setup/Engine/SetupEngine.cs`.
- **Problem:** The rule "HidHide cloak on at start, off on every exit, uninstall and upgrade path"
  was covered by INPUT-B1 (show on every leave path), DEVICE-005 (stale ledger at start with
  integration off), INSTALL-V-003 (uninstall with App missing) and session B7 (cloak off on every
  exit), with no single acceptance list. Worse, no programmatic exit runs the session cleanup today
  (SESSION-V-001: tray Exit, `--restore-shell`, update and uninstall call Avalonia's forced
  `Shutdown()`), so the cloak-off never runs on those paths.
- **Best solution:** One early never-strand group with one acceptance list: B006 makes every exit
  run the session cleanup once and maps the OS end-session to SessionEnd; B009 makes the release
  attempt every step with the cloak-off written first and unconditionally (HC
  `RestoreAllControllersForUninstall` order), turns the cloak off even with an unreadable ledger,
  and shows the physical pad once at start when integration is off and the ledger is non-empty; B007
  keeps the ledger when `App\WSGM.exe` is missing. Matrix rows M01-44 (tray Exit shows the pad),
  M01-46 (`--restore-shell` from Game Mode), M01-48 (uninstall with App missing), M01-12 extended
  (crash ledger then a start with integration off).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ApplicationShutdown|FullyQualifiedName~ModeSelection|FullyQualifiedName~ControllerManager|FullyQualifiedName~HidHideOwnership|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceIntegrationOff|FullyQualifiedName~Setup"`.
- **Plan v2:** B006, B007, B009; B140 keeps the order afterwards.
- **Related:** SESSION-V-001, INPUT-001, INPUT-002, INPUT-V-003, DEVICE-005, INSTALL-V-003,
  CONFLICT-01, CONFLICT-03.

### USER-001: RTSS overlay only appears after the QAM was opened once

- **Severity:** high (maintainer-reported 2026-10-03; binding decision)
- **Where:** `src/WSGM/Core/PerformanceService.cs` (`_observers` ObservationGate field,
  `ObserverCount`, `AcquireObservation`, `RefreshAsync` with `RtssLauncher.TryStartAsync` and
  `ApplyEffectiveDesiredAsync("drift-repair")`, `PollAsync` waiting while `_observers.Count == 0`,
  `ApplyOneAsync` returning `Rejected` and `Deferred`), `src/WSGM/Core/RtssLauncher.cs`
  (`RestartCooldown`, `StartDetachedAsync`), `src/WSGM/Shell/SteamUiSessionHost.cs`
  (`UpdatePerformanceObservation`, `ReleasePerformanceObservation`, `_performanceObservation`,
  `_observationGate`), `src/WSGM/Shell/PerformanceOverlayBridge.cs` (`AcquireObservation`),
  `src/WSGM/Overlay/OverlayWindow.Sources.cs` (`_performanceObservation`),
  `src/WSGM/Shell/ShellSession.Performance.cs` (profile fan-out),
  `src/WSGM/Shell/ShellSession.Power.cs` (resume refresh), `src/WSGM/Core/PerformanceModels.cs`,
  `docs/rtss.md`, `docs/decisions.md`.
- **Problem:** In a game the RTSS overlay does not show until the Quick Access Menu has been opened
  once. Two causes.
  1. `PerformanceService` starts RTSS when it is installed but not running, reads back state and
     repairs drift only inside `RefreshAsync`. `RefreshAsync` runs from `PollAsync`, which waits
     while there are no observers; the only other caller is the resume refresh. The two observers
     are `SteamUiSessionHost.UpdatePerformanceObservation`, which takes the lease only once the
     frame-limit row or the `SteamPerformanceSurface.OverlayLevelRow` patch is `Verified` (that
     verification needs Steam's performance root, which exists only after the QAM performance panel
     rendered), and the overlay's performance page. At startup the profile fan-out calls
     `ApplyProfilesAsync`, which writes the overlay level once; with RTSS not running,
     `ApplyOneAsync` gets a probe that is not `Ready` and returns `Rejected`. Nothing retries and
     nothing starts RTSS, because the launcher only runs from the gated poll. Opening the QAM
     verifies the patch, takes the lease, the poll starts RTSS, drift repair writes the level,
     `_osd.SetLevel` runs and the overlay appears. A user who closes RTSS mid-session gets it back
     only while a UI observes.
  2. The overlay level waits for a per-game executable it does not need. Steam store titles first
     publish with no executable (log: "executable profile unavailable, global RTSS policy remains
     active"), and `ApplyOneAsync` then returns `Deferred` for every control, including
     `OverlayLevel`. The level is WSGM's own renderer state (`RtssOsdRenderer.SetLevel`) whose
     presentation gate falls back to the global profile, so the deferral contradicts the log line.
     Opening the QAM moves the foreground to Steam and back,
     `RunningApplicationTarget.ReportForeground` resolves the executable and the transition write
     runs, which is the second way "open the QAM once" fixes it. Game/Global profile switching
     itself is not gated: `RunningApplicationCoordinator` holds its monitor lease for the session.
- **Maintainer decision (2026-10-03), binding, replaces the on-demand launch in docs/rtss.md "WSGM
  starts RTSS":**
  - Start with WSGM. When `PerformanceService` starts with RTSS integration enabled
    (`Performance.Enabled`, or overlay-test's simulated adapter), it probes immediately and starts
    the verified RTSS executable on `NotRunning`, without waiting for a poll, an observer or a
    profile write. Switching RTSS integration on at runtime does the same.
  - Keep it alive. Mirror HC's `RTSSPlatform.Start` and `Process_Exited` with `KeepAlive`: hold the
    running RTSS process (started by WSGM or found by discovery) and restart it when it exits. The
    unconditional poll is the backstop for a missed exit and for an RTSS that was never running.
  - Remove the 30 s `RtssLauncher.RestartCooldown`. HC has none. Keep the safety rules: start only
    the executable discovery verified, only on a `NotRunning` probe, so a second instance is never
    started. The 10 s settle stays so a start is confirmed by the next probe and not re-fired on top
    of itself.
  - Integration off means no launch, and WSGM never kills RTSS. RTSS stays detached and outlives
    WSGM. Only a user who switched RTSS integration off opts out.
  - Update docs/rtss.md "WSGM starts RTSS" and its log-line table (the cooldown line goes) and
    docs/decisions.md.
- **Best solution:** Mirror HC and remove mechanism.
  1. Delete the `_observers` gate from `PerformanceService`: the field, `AcquireObservation`,
     `ObserverCount`, the `_observers.Signal()` in `DisposeAsync` and the `WaitAsync` branch in
     `PollAsync`. The poll's first iteration runs `RefreshAsync` immediately, which is the
     start-with-WSGM probe; it then runs every 5 s for the service lifetime. Disabled means probe
     only: `Drifted()` already returns false and `RtssLauncher.ShouldStart` already refuses when
     disabled. On the enabled edge in `ApplyProfilesAsync` (false to true), call the same
     `KickRefresh()` as the exit handler (step 2) instead of waiting for the next tick; do not await
     `RefreshAsync` there, because its 10 s settle would hold up the profile fan-out. Give the
     constructor an optional `RtssLauncher? launcher` parameter (default `new RtssLauncher()`) so
     tests inject a fake start and exit-watch port. Keep the `ObservationGate` type:
     `RunningApplicationTarget` still uses it.
  2. `RtssLauncher`: delete `RestartCooldown` and the time provider; turn `_lastAttemptTicks` into a
     single in-flight flag (set by the existing compare-exchange, cleared after the 10 s settle and
     immediately when the start fails or throws) so the poll, the enable edge and an exit restart
     can never start two copies. Hold the process the same way whether WSGM started it or found it:
     discovery already matches exactly one process to the verified executable
     (`RtssDiscovery.Probe`, `processes[0]`), so add an optional `int? ProcessId` to `RtssProbe`,
     set it on that success path (the adapter's `probe with { Availability = Ready }` carries it
     through) and leave `StartDetachedAsync` disposing its `Process` as today. Add
     `RtssLauncher.Watch(RtssProbe probe, Action exited)`: on a `Ready` probe whose `ProcessId`
     differs from the held one, dispose the old watch and watch the new PID through an injected
     `Func<int, Action, IDisposable?>` port whose default opens the process with
     `Process.GetProcessById`, sets `EnableRaisingEvents` and subscribes `Exited` (an open that
     fails is logged once and nothing is held; the poll is the backstop). Do not use the limited
     image-name query: it arrives only in B100, which depends on B005, and discovery has already
     verified the PID. `PerformanceService.RefreshAsync` calls `Watch` after a `Ready` probe; the
     exit callback clears the held watch and calls one `KickRefresh()` that starts a single tracked
     `RefreshAsync` when none is running (probe, then start only on `NotRunning`, so a disabled
     integration only probes). `DisposeAsync` disposes the watch and awaits the tracked refresh
     within its existing budget. The overlay-test simulated probe has no executable and no process
     id, so it never launches or holds anything. Never kill RTSS.
  3. Delete the lease plumbing: `UpdatePerformanceObservation`, `ReleasePerformanceObservation`,
     `_performanceObservation` and `_observationGate` in `SteamUiSessionHost`, and
     `_performanceObservation` in `OverlayWindow.Sources.cs`. Correction found while consolidating:
     `PerformanceOverlayBridge.AcquireObservation` also starts `RefreshCpuBoostAsync()` so the
     overlay's CPU boost row shows what Windows holds when the source attaches. Do not lose that:
     replace the method with `void RefreshWindowsState()` that only runs that refresh, and call it
     where the overlay used to acquire the lease. Leave `RunningApplicationTarget`'s monitor lease
     alone.
  4. Keep the 5 s interval and the state-change dedup in `RaiseStateChanged` (no UI churn per poll);
     the probe is a slow path like HC's watchdog.
  5. In `ApplyOneAsync`, defer only `FrameLimit` (the one control written into an RTSS profile file)
     when the executable is unknown. `OverlayLevel` applies with `EffectiveRtssProfile` returning
     the global profile, as the log line promises; when the executable resolves, the transition
     write adds the per-app `EnableOSD` repair. The read side must change with it, or step 6 does
     nothing for a store title: `RefreshInsideGateAsync`'s pending-executable branch sets
     `Observed = PerformanceValues.Empty`, and `Drifted()` skips a control without an observed
     value, so after RTSS reaches `Ready` the overlay level is never repaired until the executable
     resolves. Replace that branch's empty readback with the same
     `_adapter.ReadAsync(EffectiveRtssProfile(target, optedIn), ...)` the known-target branch makes
     (it already resolves to the global profile) and pass `UpdateReadback` the result with
     `FrameLimit` set to null. The overlay level then drifts and is repaired like any other value,
     while the deferred frame limit stays unobserved and is not drift-written and deferred again on
     every poll. Update the `PerformanceApplicationTarget` and `PerformanceState` doc comments
     (UNCOVERED-007).
  6. Add no retry path. Once the poll is unconditional, drift repair writes the desired level when
     RTSS reaches `Ready`; the rejected write was refused before dispatch, so this is not a retry of
     an uncertain write. Log lines that separate the causes after a reproduction: "RTSS is installed
     but not running; starting it" appearing only after the QAM opened means cause 1;
     `rtss.command.OverlayLevel ... Deferred` at game start means cause 2; "RTSS OSD level N" marks
     when the overlay turned on.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PerformanceService|FullyQualifiedName~PerformanceOverlayBridge|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~RtssLauncher|FullyQualifiedName~RtssDiscovery"`
  (B005's own filter names only `PerformanceService`; use this one). Add: a fake adapter reports
  `NotRunning` then `Ready` with no observer; the launcher fake is invoked and after `Ready` the
  overlay level and frame limit are written once by drift repair. The same with a target whose
  `RtssProfileName` is null: after `Ready` the overlay level is written by drift repair and no
  `FrameLimit` command or drift warning repeats on later polls. A disabled service probes but never
  writes or starts. Switching integration on calls the start without waiting a poll interval. A fake
  exit callback triggers one refresh and one start, and a concurrent poll does not start a second
  copy. Target with `RtssProfileName == null` and desired overlay level 2: the adapter receives an
  `OverlayLevel` apply on the global profile and `FrameLimit` stays `Deferred`. `RtssDiscovery`
  returns the matched process id on its success probe. Rewrite the `RtssLauncherTests` that use
  `RestartCooldown` (a failed start no longer blocks the next attempt; a start in flight blocks a
  second one), `PerformanceServiceTests` (around line 229), the four `PerformanceOverlayBridgeTests`
  that use `AcquireObservation` (assert `RefreshWindowsState` refreshes CPU boost instead) and the
  `SteamUiSessionHostTests` lines asserting `ObserverCount` (around 423); delete tests asserting
  that polling stops without observers. Manual: M01-43 (RTSS not running, start WSGM, launch a Steam
  store game without opening the QAM: RTSS starts within one poll plus its settle and the overlay
  appears at the saved level; close RTSS mid-session and it comes back).
- **Plan v2:** B005 (first live-defect batch; B100 depends on it). Add `src/WSGM/Core/RtssModels.cs`
  and `src/WSGM/Core/RtssDiscovery.cs` (the `ProcessId` on the probe) and the `RtssLauncherTests`
  and `SteamUiSessionHostTests` files to B005's file list.
- **Related:** GAP-02 (later batches must not reintroduce the plumbing), UNCOVERED-007, WINSVC-031,
  WINSVC-032 (B100), DEVICE-033.

## Medium

### CONFLICT-02: CloseAdmission is consumed but never provided, under two names

- **Severity:** medium
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs`, `src/WSGM/Shell/SteamUiSessionHost.cs`,
  `src/WSGM/Shell/ShellSession.Shutdown.cs`.
- **Problem:** Session B7 calls `DeviceCoordinator.CloseCommandAdmission`, steamhost calls its own
  method `CloseAdmission()`, and no device batch adds either. B140 would not compile against the
  owners it orders.
- **Best solution:** One name, `CloseAdmission()`, synchronous and called at T0, on every owner.
  DEVICE-B3 adds it to `DeviceCoordinator` together with `StopAsync(Deadline)` (B083); the Steam
  host's is the existing no-CEF prefix of `DisableAsync` (B136); `ProfileService`/`ProfileFanOut`
  get `Close()` (B040).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~SteamUiCoordinator|FullyQualifiedName~ProfileFanOut"`.
- **Plan v2:** B083, B136, B040, consumed by B140.
- **Related:** CONFLICT-01, DEVICE-004.

### CONFLICT-04: NativeQamSemanticServices dependency cycle

- **Severity:** medium
- **Where:** `src/WSGM/Shell/NativeQamSemanticServices.cs` (split into
  `src/WSGM/Shell/NativeQam/*.cs`).
- **Problem:** steamhost B4 waits on device ports, while DEVICE-B6/B8 wait on steamhost's member
  moves and both edit the same file. As written the order is a cycle.
- **Best solution:** Run steamhost B4 first against the concrete `DeviceCoordinator` (it allows
  that), with no interim ports. The device batches then edit the split files.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQam|FullyQualifiedName~CapabilityProjection|FullyQualifiedName~DeviceOverlayBridge"`.
- **Plan v2:** B087 before B088, B089, B091.
- **Related:** STEAMHOST-021, STEAMHOST-V-008.

### CONFLICT-05: Toolkit contract the Steam host expects versus the one the toolkit keeps

- **Severity:** medium
- **Where:** `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiPatchManager.cs`,
  `SteamUiModuleRuntime.cs`, `Client/*.cs`; `src/WSGM/Shell/SteamUiSessionHost.cs`; library and
  overlay outcome mapping.
- **Problem:** steamhost B7 expects a patch `DependsOn` graph, a quarantine reset API, dynamic
  module (un)registration and an `ExistingWindowsPaths` policy type; toolkitcs R3/R4/R8 remove all
  four. Library B5 and overlay B8 map `DispatchedUnknown`, which the toolkit's four outcomes do not
  have.
- **Best solution:** Adopt toolkitcs for three of the four: the manager applies its own bridge first
  and removes it last on every path (no `DependsOn` graph), quarantine lives for the runtime
  instance (reset on WSGM restart, no reset API), and write outcomes are `NotSent`, `Unknown`,
  `Rejected`, `Applied`; consumers map `Unknown` (stop, keep the record, no retry). Drop steamhost
  B7 steps 2 (`DependsOn`) and 5 (`ExistingWindowsPaths` policy type). The fourth follows the
  maintainer's answer instead of toolkitcs: plugin Steam UI modules register dynamically when the
  plugin becomes ready (steamhost B7 step 3 stays). Toolkit side: `SteamUiModuleRuntime` gains
  module add and remove on a live runtime, `SteamUiPatchManager` gains `Unregister(id)` that
  retracts the patch from Steam and drops its quarantine, and the bridge's allowed-command set
  follows the module set instead of being fixed at `SteamUiBridgeHost` construction. Host side:
  `CommonPluginSteamUiSource` raises a change when an admitted plugin becomes ready, stops or
  restarts; `SteamUiSessionHost` registers that plugin's modules and patches on ready and
  unregisters them on stop, so no handler keeps pointing into a retired instance. This replaces the
  one-shot `ReadModules()` snapshot in the host constructor and the
  `modules.AddRange(_pluginModules)` in `CreateModules`.
- **Tests:** toolkit `SteamUiPatchManagerTests|SteamClientTests|SteamUiModuleRuntime` filters (a
  module added to a running runtime is served, a removed module's patch is retracted and its
  commands refused);
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~CommonPluginSteamUi|FullyQualifiedName~GameLibrary|FullyQualifiedName~LaunchFix"`
  with a plugin that becomes ready after host start (its module registers) and then stops (its patch
  is retracted and its commands refused).
- **Plan v2:** B053, B054 (adds module add and remove, patch unregistration and the live allowlist),
  B086 (registers plugin modules on readiness in place of "read at session start", decided by the
  maintainer).
- **Related:** TOOLKITCS-025, TOOLKITCS-044, STEAMHOST-006, LIBRARY-004, OVERLAY-010.

### CONFLICT-07: File picker paging, spool and worker caps

- **Severity:** medium
- **Where:** `external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamFilePickerSurface.cs`,
  `SteamUiAssets/Source/file-picker.ts`; manual row M01-31.
- **Problem:** Codex plan B2 (200-entry pages, spool, four workers,
  `ProviderBusy`/`ProviderCapacity`), M01-31/32, toolkitcs R8, toolkitjs R2 (paging with the kit's
  More control) and steamhost B7 (policy type) all differ. `file-picker.ts` renders the whole list
  today and never calls `renderSteamUiMore`, so paging is a visible UI change, and the page cap
  would be an arbitrary limit.
- **Best solution:** Enumerate and sort on a worker with the caller's cancellation, reject stale
  tickets, normalize `\\?\` and `\\?\UNC\` paths, refuse device namespaces, render the whole list as
  today, bounded only by the bridge request timeout. No pages, spool, worker caps or policy type.
  Rewrite M01-31 ("the whole list renders as today; a stale listing never replaces the current one;
  explicit selection, cancel or unmount settles once"). Library folders keep refusing UNC.
- **Tests:**
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamFilePicker"`
  with the 450-entry fixture, stale listing, cancel and unmount, extended-path and device-namespace
  cases.
- **Plan v2:** B059; matrix change in plan v2 section 5.
- **Related:** TOOLKITCS-055, TOOLKITJS-021, TOOLKITJS-V-008, LIBRARY-035.

### CONFLICT-09: WindowsDeviceControl shape, static facades versus per-instance services

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio*.cs`,
  `CoreAudio*.cs`; `src/WSGM/Shell/RadioManager.cs`, `AudioManager.cs`.
- **Problem:** WDC proposes static facades returning disposable watch registrations; winsvc B8,
  config B5/B7 and Codex plan L121 assume per-instance `AudioService`/`DisplayService`/radio
  services. Both cannot be built.
- **Best solution:** Adopt WDC. `Start*Watch` returns an owned `IDisposable` per registration (no
  hub), disposing one is the stop, internal ports exist only where orchestration needs a test.
  `RadioManager` consumes registrations and deletes `_feedWork`, `QueueFeedWork` and
  `_bluetoothWatchGeneration`. Config B7 has no instance dependency.
- **Tests:** `WatchRegistrationTests|CoreAudioTests` on net8 and net10;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager|FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~NativeQamBluetooth"`.
- **Plan v2:** B063, then B064 to B071.
- **Related:** WINSVC-022, WDC-014, A01-F003, A01-F005.

### CONFLICT-10: Two owners for display modes

- **Severity:** medium
- **Where:** `src/WSGM/Core/DisplayProfiles.cs`, `DisplayResolutionService.cs`,
  `RefreshRatePairingService.cs`, `EdidModes.cs`, `src/WSGM/Shell/NativeQamResolutionService.cs`.
- **Problem:** config B7 (`PrimaryDisplayModes`) and winsvc B6 (`TransientDisplayMode`) rewrite or
  delete the same files; winsvc.verify rejects B6 as a workflow change.
- **Best solution:** One owner, winsvc, with WINSVC-012 only: give the harness an operating-point
  fake and delete the `"test"` key and null-operating-point production branches. Rename
  `DisplayProfiles` and delete `EdidModes` in favour of WDC `DisplayEdid` only if WDC exposes the
  advertised rates after B069; otherwise fix the stale doc only. Drop config B7.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~FrameLimitPairing|FullyQualifiedName~RefreshRatePairing|FullyQualifiedName~DisplayResolutionService"`.
- **Plan v2:** B098.
- **Related:** WINSVC-011 (refuted), WINSVC-012, WINSVC-027, CONFIG-028, CONFIG-029.

### CONFLICT-13: Windows power statics claimed by two batches

- **Severity:** medium
- **Where:** `src/WSGM/Core/PowerSchemes.cs`, `CpuBoost.cs`, `HybridCores.cs`,
  `WindowsPowerModes.cs`, `PowerTimeouts.cs`; `src/WSGM/Shell/NativeQamCpuBoostService.cs`,
  `NativeQamHybridCoreService.cs`, `ApplicationPerformanceReconciler.cs`.
- **Problem:** WINSVC-B2 (`PowerPolicyLane`, deletes the four statics) and DEVICE-B6
  (`CpuBoostReconciler` with "static Windows adapters passed from the composition root") target the
  same statics, and the two NativeQam services are owned by neither.
- **Best solution:** WINSVC-B2 owns the statics. The lock over the machine-global active scheme
  moves into the one injected `PowerSchemes` instance; `CpuBoost`, `HybridCores`,
  `WindowsPowerModes` and `PowerTimeouts` take that instance; no `PowerPolicyLane` type. CRIT-003
  lands first with the readback fixes. DEVICE-B6 consumes the instances.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~PowerTimeout|FullyQualifiedName~DisplayTimeout"`
  and the UiTests `HybridCoreView|DevicePageCapture` filter.
- **Plan v2:** B017, B090, B091.
- **Related:** CRIT-003, WINSVC-009, WINSVC-010, WINSVC-030, DEVICE-040.

### CONFLICT-14: MessageWindow owned by two batches with different shapes

- **Severity:** medium
- **Where:** `src/WSGM/Interop/MessageWindow.cs`, `src/WSGM/Shell/DisplayChangeWindow.cs`,
  `src/WSGM/Interop/EffectivePowerModeNotification.cs`.
- **Problem:** SESSION-B3 wants no new registration objects; WINSVC-B10 wants reference-counted
  `IDisposable` claims, which winsvc.verify rejects.
- **Best solution:** One batch, SESSION-B3: one `MessageWindow` instance owned by the composition
  root with UI-thread affinity, a WndProc exception guard, consumers take it by constructor,
  provider disposal ahead of window destruction, disposed last. WINSVC-B10 shrinks to
  `DisplayChangeWindow` owner-only construction (in the same batch) and the
  `EffectivePowerModeNotification` callback guard (B091).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Tray|FullyQualifiedName~MessageWindow|FullyQualifiedName~WindowFinder"`.
- **Plan v2:** B114, B091.
- **Related:** SESSION-043, SESSION-044, WINSVC-017, WINSVC-045, WINSVC-V-003.

### CONFLICT-16: State-file read outcome implemented five times

- **Severity:** medium
- **Where:** `src/WSGM/Core/ConfigStore.cs`, `src/WSGM/Core/Library/ImportStateStore.cs`,
  `src/WSGM/Shell/ArtworkStateStore.cs`, `src/WSGM/Shell/QuickAccessFolds.cs`,
  `src/Shared/Gpu/DriverStateFile.cs`, `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs`,
  `src/WSGM.DeviceLab/Wizard/LabMachineState.cs`.
- **Problem:** config B2b, library B1/B3, gpuir B1, SDK A02_02 and labcore each define a read
  outcome; library B7 and config B2a both thread roots into the same two stores. Duplicate types and
  double edits.
- **Best solution:** config B2b owns every WSGM sidecar with one rule (missing file or directory is
  Absent, a parse failure is Corrupt, any other IO is Unreadable and never written over); library B1
  keeps only the `Sanitize` cap removal, library B3 drops its `ArtworkStateStore` step, library B7
  folds into config B2a. Plugins and Lab apply the same stated semantics in their own assemblies; no
  shared new type.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~StateFile|FullyQualifiedName~ArtworkState|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~ImportState"`;
  GPU, SDK and Lab filters in their batches.
- **Plan v2:** B037, B039, B015, B103, B150, B010, B158.
- **Related:** LIBRARY-013, LIBRARY-014, GPUIR-003, LABCORE-001, A02-F007.

### CONFLICT-18: Clock and trace ownership

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Lifecycle/ActiveClock.cs`, `PluginTrace`,
  `src/WSGM/Shell/DeviceCapabilityRouter.cs`, `src/WSGM/Shell/DevicePluginRuntime.cs`.
- **Problem:** SDK keeps the process `ActiveClock` and `PluginTrace`; PACKAGES-B5 assumes an owned
  clock and a `PluginTraceSink` instance; device and gpuir B2 wait on an "F02 clock". The device's
  across-sleep command timeout (device.md:210) has no owner.
- **Best solution:** Adopt SDK. `ActiveClockCore(Func<long>)` behind the static facade, due sources
  cancelled with `CancelAsync` and faults observed, so plugin continuations never run on the clock
  thread; one `IPluginHost` trace channel for common plugins. PACKAGES-B5 shrinks to the API bump
  and the `DisposeAsync` contract line. The command timeout uses the SDK active-time `Deadline`
  instead of a wall timer so sleep does not consume it; the device keeps an injected
  `Func<DateTimeOffset>` as final.
- **Tests:**
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Lifecycle"`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCapabilityRouter|FullyQualifiedName~DevicePluginRuntime"`.
- **Plan v2:** B043, B082, B052, B142; PACKAGES-024 recorded no-change.
- **Related:** SDK-001, DEVICE-028, A02-F003, A02-F006, PACKAGES-024, PACKAGES-026.

### CONFLICT-20: Admitted Codex batches against the reviews

- **Severity:** medium
- **Where:** `batches/T01_01.md`, `A02_01.md` to `A02_04.md`, `W02_01`, `W02_02`.
- **Problem:** Seven admitted batches predate the reviews. T01_01 edits code the client redesign
  deletes; A02_01 grows CLI detection nobody needs; A02_02 carries the rejected latch; W02_02 adds
  two interfaces and a process wrapper; W02_01 is done but uncommitted.
- **Best solution:** T01_01 is not landed, its tests move into B053. A02_01 is replaced by B045
  (steps 1, 2, 4 plus the package manifest length check). A02_02 runs trimmed in B010. A02_03 lands
  with R4 in B004. A02_04 lands unchanged in B003. W02_01 is committed in B001. W02_02 becomes the
  single `IPowerActionApi` port in B002.
- **Tests:** each batch's filter as listed in plan v2.
- **Plan v2:** B001, B002, B003, B004, B010, B045, B053.
- **Related:** A01-F001, A01-F002, A02-F001, A02-F012, A02-F015, A02-F016, CONFLICT-19, GAP-01.

### CONFLICT-25: Overlay activation sources edited by three batches

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayController.cs`, `OverlayController.Gestures.cs`,
  `TouchSwipeMonitor.cs`, `src/WSGM/Core/HotkeyService.cs`, new
  `src/WSGM/Overlay/OverlayActivation.cs`.
- **Problem:** SESSION-B3 waits on overlay's `HotkeyService`/`OverlayController` constructor change,
  WINSVC-B10 edits `HotkeyService` and `OverlayController`, OVERLAY-B3 moves activation out of the
  controller.
- **Best solution:** OVERLAY-B3 owns hotkey, chord and swipe construction in `OverlayActivation`;
  the in-session Settings preview composes no activation; `--overlay-test` keeps all three because
  they are its only reopen path. SESSION-B3 follows. WINSVC-B10 does not touch these files.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayActivation|FullyQualifiedName~QuickAccessSheet"`
  and the UiTests overlay filter.
- **Plan v2:** B113 before B114.
- **Related:** OVERLAY-002, SESSION-043.

### CONFLICT-26: Timing of the instance config store (config B2a)

- **Severity:** medium
- **Where:** `src/WSGM/Core/UserDataContext.cs` (new), `ConfigStore.cs`, `Log.cs`, all `ConfigStore`
  consumers and the 27 `Log.Directory` sites.
- **Problem:** B2a threads the instance store through nearly every domain's constructors, and
  library B7, steamhost B6, settings B7, overlay C2 and SDK-B8 wait for it. Run late, every
  constructor is rewritten twice.
- **Best solution:** Run B2a early, mechanical and behaviour-neutral, before config B1 and before
  structural domain batches: `UserDataContext(Root, ConfigMutexName)` with `ForCurrentUser()` only,
  `ConfigStore` an instance with today's semantics, `Log.Init(name, root)` and `Log.Directory`
  deleted, tests on a temp root with a unique mutex.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~BootManifest|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~SetupAnswers|FullyQualifiedName~ImportState|FullyQualifiedName~ArtworkState"`.
- **Plan v2:** B037 (Phase D head, no dependency), before B038.
- **Related:** CONFIG-011, LIBRARY-014, BUILD-001, CONFLICT-17.

### CONFLICT-27: Steam autostart restore edited by three domains

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamAutostartService.cs`, `src/WSGM/Core/OtherManagers.cs`,
  `src/WSGM.Setup` answers path.
- **Problem:** config B2b (strict `RestoreAll`), WINSVC-B3 (takeover instance, strict restore) and
  install.verify V-004 (apply only on a false-to-true change) all edit the same two files.
- **Best solution:** WINSVC-B3 owns `SteamAutostartService` and `OtherManagers` behaviour: CRIT-001
  first (B016), then the `OtherManagerTakeover` instance and strict `RestoreAll` returning 1 without
  writing on an unreadable config (B095). config B2b supplies only the strict read and classifies
  Steam autostart as fail-closed (B039). Setup applies the takeover only when the answer flips from
  false to true (INSTALL-V-004, which plan v2 placed in the setup-answers batch).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart|FullyQualifiedName~OtherManagers|FullyQualifiedName~Autostart|FullyQualifiedName~Setup"`.
- **Plan v2:** B016, B039, B095, B028.
- **Related:** CRIT-001, WINSVC-013, U04B-LFA-003, U04B-LFA-004, INSTALL-V-004, UNCOVERED-002,
  UNCOVERED-006.

### CRIT-001: Steam autostart takeover disables entries it could not record

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamAutostartService.cs:178-202` (`RecordDisabled`),
  `src/WSGM/Core/SteamAutostartTakeover.cs:77-103` (`Disable`); compare
  `src/WSGM/Core/OtherManagers.cs:478-488` (`Record`).
- **Problem:** `RecordDisabled` catches and logs a failed `ConfigStore.Mutate`.
  `SteamAutostartTakeover.Disable` therefore goes on to disable the scheduled task or write the
  disabled approval bytes with no recorded original. Uninstall's `RestoreAll` then has nothing to
  restore and Steam's own autostart stays off for good. `OtherManagers.Record` throws instead and is
  correct; the plan claimed "Disable records before each change" for both.
- **Best solution:** Delete the `try/catch` in `RecordDisabled` so the exception propagates.
  `Disable`'s per-item `try` already catches it, logs "disabling ... failed", adds the source to
  `Pending` and skips the write, because `record(entry)` is called before
  `SetTaskEnabled`/`WriteApproval`. No new state.
- **Tests:** in `SteamAutostartTests`, a throwing record delegate leaves the task enabled (no
  `SetTaskEnabled(false)` call on the fake) and the approval bytes untouched, and the source lands
  in `Pending`. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`.
- **Plan v2:** B016.
- **Related:** U04B-LFA-003, CONFLICT-27.

### CRIT-002: Sound packs drop and refuse valid content through arbitrary caps

- **Severity:** medium
- **Where:** `src/WSGM/Core/Sounds/SoundPackLibrary.cs:172-177` (1 MiB per-asset skip counted as
  "missing"), `:190-194` (16 MB "playback budget" throws for the whole pack), `:221-224` (512-entry
  install refusal), `:229` (64 MiB expanded-bytes check);
  `src/WSGM/Shell/SoundPackService.cs:201-204` (64 MB ZIP refusal), `:332-339` (`Load` first
  publishes an empty override set).
- **Problem:** An asset over 1 MiB silently plays Steam's default; a pack past 16 MB loses every
  override; an archive with more than 512 entries or over 64 MB is refused. The Steam delivery is
  already chunked (toolkit `DeliverAsync` parts), so none of these caps protects a transport.
  Separately, `Load()` publishes an empty override set with a new revision before rebuilding, so
  every refresh, select or preview briefly retracts the active pack in Steam.
- **Best solution:** In `BuildOverrides`, keep the existence and extension (MIME) checks and drop
  the size test and the `total` budget; a zero-byte file stays skipped as unsupported. In `Install`,
  delete the entry-count check and keep only the zip-bomb guard as the D2 exception: expanded bytes
  (keep the existing 64 MiB expanded total as the D2 byte bound, refusing, never truncating),
  symlink attribute and containment checks. In `ImportAsync`, delete the 64 MB file-size check and
  keep `IsPathFullyQualified` and `File.Exists` (the expanded-bytes guard protects extraction); its
  refusal text loses only the size clause ("Choose an existing ZIP archive."), the one string change
  removing the cap requires. The large state still reaches Steam whole: the toolkit already delivers
  in 256 K parts, and its 32 M-character envelope cap is removed by B056, which lands before B138.
  In `Load`, build the new set first and publish once; publish the empty set only on the failure
  path (a `catch` around the rebuild that publishes empty overrides and rethrows), which keeps the
  "retract stale bytes" intent. The stored selection (`SoundsConfig.Selected`) is unchanged, so no
  migration.
- **Tests:** `SoundPackLibraryTests`: a 2 MiB asset is delivered; a pack totalling 20 MB delivers
  all resources; a 600-entry archive installs; a symlink entry and an entry escaping the stage are
  still refused; an archive whose declared expanded size passes the D2 bound is refused.
  `SoundPackServiceTests`: a refresh publishes one revision, never an empty set first; a failing
  rebuild publishes the empty set. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SoundPack"`. Manual
  M01-28 with a pack holding an asset over 1 MiB.
- **Plan v2:** B138 (decided: D2 accepts exactly the plan v2 bound list, which holds this zip-bomb
  bound; every other cap goes).
- **Related:** UNCOVERED-004, UNCOVERED-005, GAP-05, M01-28.

### CRIT-003: Hybrid core selection is gated on readback and latched after a failure

- **Severity:** medium
- **Where:** `src/WSGM/Shell/NativeQamHybridCoreService.cs:26-69` (`SetHybridCoresAsync`), `:71-108`
  (`ReadAsync` clears and sets `_requiresRead`).
- **Problem:** A selection is refused when a fresh `cores.Read()` fails or reports unsupported, and
  after any failed write `_requiresRead` refuses every later explicit selection ("Windows state must
  be refreshed before another selection") until a publication read succeeds. A new user action is
  the allowed retry, so this gates a control on readback.
- **Best solution:** The same shape winsvc settled for `NativeQamPowerProfileService` (WINSVC-010):
  validate against the published option list, write, and let the next publication read Windows. Add
  one `HashSet<HybridCoreMode> _offered` set by `ReadAsync` (the published options; empty when
  unsupported or when the read failed, which matches the row Steam was shown) and refuse a mode
  outside it with the existing "no longer offered" text. Delete the pre-write `cores.Read()`, the
  `_requiresRead` field, its check, both assignments and the "Windows state must be refreshed before
  another selection" message. Call `cores.Apply(mode, token)`; with B017's `HybridCores.Apply` no
  longer throwing on a readback mismatch (it logs it), a write Windows accepted returns success. Add
  no cached "written mode" field: the class remarks say why nothing is cached between reads
  (activating a power scheme can carry a different preference), and `ReadAsync` already reads
  Windows on every publication, which is an accurate Windows read, not a device readback. A write
  that throws returns its failure once with `_status`; the next explicit selection is attempted
  normally. Never retry automatically.
- **Tests:** in `HybridCoreTests`, replace
  `AnUnconfirmedSteamWriteBlocksTheNextOneUntilTheStateIsReRead` with: a throwing `Apply` followed
  by a second selection calls `Apply` again; a fake that reads back another mode after the write
  still returns success; a selection makes no `Read` call before the write; an id outside the
  published options is refused without a write. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~HybridCore"`.
- **Plan v2:** B017 (its "validates against the published option list" is the `_offered` set above;
  "publishes the written value" is met by the next Windows read, with no cached value).
- **Related:** WINSVC-010 (same latch in `NativeQamPowerProfileService`, resolved there),
  CONFLICT-13.

### GAP-02: Later batches still cite the observation plumbing USER-001 deletes

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs`;
  `_plan/refactor-2.1/review/steamhost.verify.md:221` (batch problem 3), cited by B085.
- **Problem:** USER-001 was in no domain batch, and steamhost.verify batch problem 3 tells
  STEAMHOST-B2 to preserve `ReleasePerformanceObservation` as a synchronous edge effect of
  `Apply(false)`. Plan v2 runs B005 first and B085 cites that verify section as part of its spec
  ("the cited verify section wins"), so an implementer could reintroduce the lease. Device B8,
  winsvc B9 and overlay B10 also edit the files B005 changes.
- **Best solution:** Add one line to B085's steps: "`ReleasePerformanceObservation` and
  `UpdatePerformanceObservation` no longer exist after B005; `Apply(false)` keeps only
  `CancelAllInflight`." B091 (which edits `PerformanceOverlayBridge.cs` and
  `OverlayWindow.Sources.cs`) keeps `RefreshWindowsState` from USER-001 step 3. B100 already depends
  on B005.
- **Tests:** B085's `SteamUiSessionHost|SteamUiSurfaceSwitches` filter; a grep in the post-batch
  review that `PerformanceObservation` does not reappear.
- **Plan v2:** B005, B085, B091, B100.
- **Related:** USER-001, STEAMHOST-003, STEAMHOST-V-010.

### GAP-04: U04B closure and the unowned launch-adjacent files

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamAutostart.cs`, `SteamAutostartTakeover.cs`, `KnownStartupApps.cs`,
  `WindowsPolicyOperation.cs`, `DesktopAppProcessBackend.cs`; ledger U04B-LFA-013 to 049 (bodies
  missing).
- **Problem:** No report owned these files, 37 U04B ledger bodies were never written, and
  U04B-LFA-003 (CRIT-001) and U04B-LFA-012 (leaked `WSGM_StartUnelevated_*` tasks) had no
  disposition.
- **Best solution:** B024 re-reviews the five files with the install projects and writes one
  disposition per U04B-LFA-013..049 id. Assigned fixes: U04B-LFA-001 B112, U04B-LFA-012 B029 (one
  `/Delete` after the dispatch budget closes, no persistent record), U04B-LFA-003 B016, U04B-LFA-004
  B095. U04B-LFA-002 (task XML staged in a user-writable folder) is no-change by maintainer decision
  (security theater, DECISIONS.md), recorded in `ledger-u04.md`. This consolidation line-read the
  five files: `KnownStartupApps` and `WindowsPolicyOperation` have no defect; the autostart findings
  are CRIT-001 and UNCOVERED-002, 003, 006, 010; `DesktopAppProcessBackend` is CRIT-004.
- **Tests:** none for B024 (read-only); the fix batches' filters.
- **Plan v2:** B024, B016, B029, B095, B112.
- **Related:** U04B-LFA-001..004, U04B-LFA-012, U04B-LFA-013..049, GAP-03.

### GAP-06: Frozen-foundation check (requirement 12) had no implementation

- **Severity:** medium
- **Where:** `*.csproj`, `Directory.Build.props`, `.gitmodules`, `external/viiper` gitlink,
  `.github/dependabot.yml`.
- **Problem:** Requirement 12 freezes Avalonia, FluentAvaloniaUI and VIIPER including "their
  dependency source/versions and the VIIPER gitlink". BUILD-R4 only added a Dependabot ignore. The
  critic's proposed diff misses `.gitmodules`, where a VIIPER source or branch change would land.
- **Best solution:** At the final gate run
  `git diff 1329813f -- '*.csproj' Directory.Build.props .gitmodules external/viiper` and fail on
  any Avalonia, FluentAvaloniaUI, Avalonia.Labs, ColorPicker or VIIPER version, source or gitlink
  change. Versions live in the project files (there is no `Directory.Packages.props`), and git's
  `*.csproj` pathspec matches the 36 tracked project files at every depth. The vendored
  `external/LoadingIndicators.Avalonia` has no project file (its sources compile into WSGM), so it
  adds no package reference to check; `global.json` pins only the SDK and is outside the freeze. No
  Dependabot edit (BUILD-004 no-change), no new tool.
- **Tests:** the command itself in B179.
- **Plan v2:** B179 (add `.gitmodules` to its pathspec).
- **Related:** BUILD-004.

### GAP-07: Independent child validation per batch (requirement 16)

- **Severity:** medium
- **Where:** `external/windows-device-control/tests`, `external/steam-ui-toolkit/tests`, toolkit
  Node checks.
- **Problem:** With per-batch publication replacing the I01/I02 phases (CONFLICT-21), nothing
  guaranteed that every WDC and toolkit batch runs child-local build and tests on both WDC
  frameworks and the toolkit Node suite.
- **Best solution:** Execution protocol item 8: every child batch runs the child build and its
  filtered tests (WDC on net8 and, from B046, net10; toolkit with `npm run prelude:claims`), and the
  full suites run at B062 (toolkit), B072 (WDC) and B179 (both). B072 adds a minimal child solution
  so the child builds standalone.
- **Tests:** as stated per batch; full WDC suite on net8 and net10; full toolkit .NET and Node
  suites.
- **Plan v2:** protocol item 8; B046, B062, B072, B179.
- **Related:** CONFLICT-21, WDC-020.

### GAP-10: Medium ledger rows no report referenced

- **Severity:** medium
- **Where:** toolkit client and gates, `BootManifestTests`, Steam autostart.
- **Problem:** U02A-SUTC-001 (one-shot session turns a JavaScript exception into success),
  U02A-SUTC-004 (bridge open to any SharedJSContext script), U03A-SUTS-004 (performance gate accepts
  an undecodable update as an empty delta), U03A-SUTS-006 (no emitted-asset checks for the audio,
  performance and network gates), U04A-LFA-008 (BootManifestTests re-implement the projection) and
  U04B-LFA-003 had no owner.
- **Best solution:** U02A-SUTC-001 is fixed by B053 (an answered JavaScript error is `Rejected`);
  U03A-SUTS-004 and 006 in B051 (refuse an undecodable update; new `check-audio`,
  `check-performance`, `check-network` fixtures); U04A-LFA-008 in B039 (tests use the production
  projection); U04B-LFA-003 in B016. U02A-SUTC-004 is no-change by maintainer decision (security
  theater, DECISIONS.md): the bridge stays open to SharedJSContext scripts as today and B177 writes
  no trust-boundary note for it.
- **Tests:** the named batches' filters.
- **Plan v2:** B053, B051, B039, B016 (plan v2 Appendix B); B177 drops its U02A-SUTC-004 clause.
- **Related:** CONFLICT-06, CRIT-001.

### GAP-11: Manual matrix rows not mapped to the refined batches

- **Severity:** medium
- **Where:** `_plan/refactor-2.1/manual-acceptance-matrix.md`.
- **Problem:** Nobody reviewed the matrix against the refined batches. No row covered USER-001.
  M01-31 hard-codes "multiple 200 entry pages", a UI that does not exist. Rows were missing for tray
  Exit on a device session, the update handoff log line, restore-shell from Game Mode, sign-out
  without a blocker, and uninstall with App missing. Even after plan v2 section 5, rows 01, 02, 04
  to 11, 13 to 16, 18, 23, 24, 26 to 30, 32 to 34, 36, 38, 40 and 41 are named by no batch, so a
  manual failure has no owner to route to.
- **Best solution:** Keep the matrix the maintainer's and apply plan v2 section 5: new M01-43 to
  M01-50, M01-31 rewritten, M01-03, 12, 17, 19, 21, 22 extended; extend M01-28 with a sound pack
  holding an asset over 1 MiB (CRIT-002). B178 adds one table to the plan appendix mapping every M01
  row to the batches whose behaviour it exercises, so a failure goes to its owner with the narrow
  test rerun. No new rows beyond section 5.
- **Tests:** none (documentation); the matrix runs with the maintainer after B179.
- **Plan v2:** section 5; B178; B179 hand-off.
- **Related:** USER-001, CONFLICT-07, GAP-09, CRIT-002.

## Low

### CONFLICT-08: Fold-id migration with three owners

- **Severity:** low
- **Where:** `src/WSGM/Shell/QuickAccessFolds.cs`, toolkit `qam-*.ts`,
  `src/WSGM/Core/ConfigRepair.cs` (Migrate).
- **Problem:** config B3 says steamhost owns a fold-id migration, steamhost B7 says config owns it,
  toolkitjs R1 says none is needed. A migration nobody needs is mechanism.
- **Best solution:** No migration. The published section ids stay today's strings, carried as opaque
  ids in the layout publication. Remove it from plan L103, config B3 and steamhost B7.
- **Tests:** B057's `QuickAccessFolds|NativeQam` filters and B068's migration fixture (folds
  round-trip unchanged).
- **Plan v2:** B057 (R1), B068 (no fold-id step); STEAMHOST-045 recorded no-change.
- **Related:** STEAMHOST-045.

### CONFLICT-11: Intel display identity through WDC

- **Severity:** low
- **Where:** `src/WSGM.Plugin.IntelGpu` CCD query, WDC display topology.
- **Problem:** GPUIR-B5 routes Intel display identity through WDC; WDC C36 rules the duplication
  accepted.
- **Best solution:** Skip GPUIR-B5; Intel keeps its own CCD query.
- **Tests:** none.
- **Plan v2:** GPUIR-027 recorded as accepted duplication; B153 does not touch it.
- **Related:** GPUIR-027, U01-040.

### CONFLICT-12: BluetoothDeviceCatalog moved to WDC or not

- **Severity:** low
- **Where:** `src/WSGM/Shell/BluetoothDeviceCatalog.cs`,
  `external/windows-device-control/.../WindowsRadio.Bluetooth.cs`.
- **Problem:** winsvc B8 deletes `BluetoothDeviceCatalog` as "moved to WDC", while WDC moves only
  container normalization. Deleting it would lose WSGM product policy.
- **Best solution:** One container normalizer in WDC; `BluetoothDeviceCatalog` and its product
  policy stay in WSGM (a library holds no WSGM policy, requirement 7).
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BluetoothDeviceCatalog|FullyQualifiedName~RadioManager"`
  and the WDC pairing tests.
- **Plan v2:** B065.
- **Related:** WDC-005, WINSVC-029.

### CONFLICT-15: Steam storage bridge cache versus StorageInventory

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamStorageBridge.cs`, new `src/WSGM/Shell/StorageInventory.cs`.
- **Problem:** STEAMHOST-B1 adds a lock plus a cached projection with a revision; WINSVC-B4 replaces
  the same enumerations with a `StorageInventory` projection. Two caches for one fact.
- **Best solution:** No revision cache in steamhost: the bridge projects an immutable snapshot on
  the UI thread when either collection changes, describes only ejectable or formattable letters and
  skips network drives. WINSVC-B4 owns the inventory, and the bridge snapshot then projects from it.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~RemovableDrive|FullyQualifiedName~SdFormat"`.
- **Plan v2:** B084, B096.
- **Related:** STEAMHOST-007, STEAMHOST-008, WINSVC-004.

### CONFLICT-17: UserDataContext user identity

- **Severity:** low
- **Where:** `src/WSGM/Core/UserDataContext.cs`; Codex plan L97/L185; setup `Detect`.
- **Problem:** config waits for install to supply `ForInteractiveUser`; install.verify replaces the
  INSTALL-005 `TargetUser` plumbing with a refusal in `Detect`. Identity plumbing nobody provides.
- **Best solution:** `UserDataContext` is the root plus the mutex name, `ForCurrentUser()` only.
  Setup refuses when the session's interactive user differs from the process user (B030). Delete
  "user identity" from plan L97.
- **Tests:** B037 configuration filter; B030 `Setup` filter.
- **Plan v2:** B037, B030.
- **Related:** INSTALL-005, CONFLICT-26, UNCOVERED-003.

### CONFLICT-22: Linked sources moved to src/Shared too early

- **Severity:** low
- **Where:** `src/WSGM/Core/NativePackageSource.cs` (linked), `ScheduledTaskXml`, `src/Shared/*`,
  `WSGM.DeviceLab.csproj`.
- **Problem:** BUILD-B6 moves `NativePackageSource` to `src/Shared/Interop` while library says it
  belongs in Device Lab; BUILD-B6 also moves `ScheduledTaskXml`, which INSTALL-008 would have
  changed (INSTALL-008 is now dropped by maintainer decision, so it stays as it is).
- **Best solution:** `NativePackageSource` moves from `src/WSGM/Interop` into WSGM.DeviceLab (today
  a `<Compile Include>` link in `WSGM.DeviceLab.csproj`, used only by
  `Packaging/DeviceLabPackageSnapshot.cs`). D3 makes Device Lab GPL, so the move needs no licence
  change to the file, and the other GPL interop files Device Lab links (`Kernel32`,
  `NativePathIdentity`, `NativeHidHide`) are neither relicensed, duplicated nor moved to an MIT
  `src/Shared/Interop`; they stay linked. Device Lab's licence file, csproj metadata, README, AGENTS
  and the notices change to GPL instead; the SDKs stay MIT. BUILD-B6 runs after the install and
  session batches and moves only files whose sharing survived them.
- **Tests:** `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries|FullyQualifiedName~Launch|FullyQualifiedName~Logon|FullyQualifiedName~PackagedLaunch"`;
  `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~DeviceLabPackaging"`
  for the moved source.
- **Plan v2:** B173, after B029, B031, B141 (decided: D3, Device Lab relicensed as GPL; no interop
  file relicensing or duplication).
- **Related:** LIBRARY-025, BUILD-019, LABCORE-039, A02-F022.

### CONFLICT-24: Steam Input lease port in the overlay

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamInputBlocker.cs`, `src/WSGM/Overlay/OverlayController*.cs`,
  `src/WSGM/Program.cs`.
- **Problem:** OVERLAY-B4 adds an `ISteamInputLease` port while INPUT-B4 makes the lease an instance
  Program owns, so Panic still reaches the live lease. A mirroring port is mechanism.
- **Best solution:** The overlay takes the input owner's concrete `SteamInputBlocker` instance
  created by Program; no port.
- **Tests:**
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamInputBlockerTests|FullyQualifiedName~SteamInputShimTests"`.
- **Plan v2:** B077.
- **Related:** INPUT-012.

### CRIT-005: Authored fan curves refused above 64 points

- **Severity:** low
- **Where:** `src/WSGM/Core/DeviceProfileValidation.cs:37-38` (`MaximumPoints = 64`), `:72-76`;
  `src/WSGM/Shell/DeviceCapabilityRouter.cs` (`CurveIsValid`, near 1356).
- **Problem:** An authored curve with more points than 64 is refused although the device declares no
  such limit. Correction to the critic: the router no longer has a matching limit; `CurveIsValid`
  refuses only an empty curve. The two checks therefore disagree today: a 65-point curve passes
  `ExecuteCapabilityAsync` but is refused as an authored profile, and the comment "matching the
  device router's own limit" is false.
- **Best solution:** Delete `MaximumPoints`, refuse only an empty curve, update the `PointCount` doc
  ("The profile carries no points"), and keep only the descriptor's declared bounds and the
  ascending-input rule, identical to `CurveIsValid`.
- **Tests:** a 100-point ascending curve inside bounds validates as `None`; an empty curve stays
  `PointCount`. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceProfile"`.
- **Plan v2:** B038.
- **Related:** DEVICE-013.

### CRIT-006: Browse sessions link background reads to the request token

- **Severity:** low (critic: note only)
- **Where:** `src/WSGM/Shell/ThemeBrowseSession.cs:111-128` (`OpenAsync`), `:276-293` (`Fetch`);
  toolkit `SteamUiModuleRuntime.cs:178-179`.
- **Problem:** `OpenAsync` and `Fetch` start fire-and-forget reads linked to both `_lifetime` and
  the per-request token. It is benign today only because the runtime disposes, not cancels, the
  request CTS. If request tokens are ever cancelled on completion (B049 moves handlers off the
  pump), the read is cancelled as soon as the command returns `Applied`, and the cancellation path
  returns without clearing `_loading`/`_detailLoading`, leaving a stuck spinner.
- **Best solution:** Link `_queryWork` and `_detailWork` only to `_lifetime.Token`; the request
  token governs only the synchronous part. Navigation still cancels the previous work through
  `_queryWork?.Cancel()`. No new mechanism.
- **Tests:** no `ThemeBrowseSession` unit test exists today; add
  `tests/WSGM.Tests/Shell/ThemeBrowseSessionTests.cs` with a `ThemeStoreClient` over a stub
  `HttpMessageHandler` (as `ThemeInstallerTests` builds it): cancel the request token right after
  `BrowseAsync`/`OpenAsync` return, the read completes and `Loading` clears; a second browse still
  cancels the first read. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ThemeBrowseSession|FullyQualifiedName~ThemeService"`
  (B138's `Themes` filter does not match a test in the Shell namespace).
- **Plan v2:** B138.
- **Related:** TOOLKITCS-V-002.

### GAP-05: Sound choice migration and sound-pack ownership

- **Severity:** low
- **Where:** `src/WSGM/Shell/SoundPackService.cs`, `src/WSGM/Core/Sounds/*`; Codex plan L103 ("sound
  choices remain usable").
- **Problem:** No domain owned the sound-pack service, and the sound-choice part of the migration
  requirement had no owner.
- **Best solution:** No migration is needed: `SoundsConfig.Selected` is the installed folder id,
  unchanged by the refactor, and B068 leaves theme, sound and movie state in place while its 2.0
  fixture round trip proves every section survives. B138 owns the service and library (CRIT-002,
  UNCOVERED-004, UNCOVERED-005).
- **Tests:** B068's `Migration|Configuration` filter; B138's `SoundPack` filter.
- **Plan v2:** B068, B138.
- **Related:** CRIT-002.

### GAP-08: Toolkit JavaScript API inventory (requirement 6)

- **Severity:** low
- **Where:** `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/**`,
  `external/steam-ui-toolkit/docs/reference.md`.
- **Problem:** toolkitcs inventoried 229 public C# types and WDC its API, but nobody inventoried the
  exported JavaScript surface (kit functions, gate factories, fragment entry points WSGM fragments
  consume) with retain, internalize or remove decisions.
- **Best solution:** The toolkit reference gains the exported script API inventory with a decision
  per export, written after the surface batches so it describes the final state.
- **Tests:** `npm run format:check`; child `npm run prelude:claims`.
- **Plan v2:** B177 (TOOLKITJS-028).
- **Related:** TOOLKITJS-028.

### GAP-12: Ledger ids referenced only through ranges, and 43 unreferenced Low/Nit ids

- **Severity:** low
- **Where:** `claude-findings-disposition.json`, `audit/A02/findings.json`,
  `audit/A02S01/findings.json`.
- **Problem:** A02-F016 and A02S01-F003, F004, F006, F009, F010, F011, F013, F014 were referenced
  only inside ranges, and 43 Low/Nit ledger ids (U02A-SUTC-015 ... U04B-LFA-012) by no report.
- **Best solution:** B178 gives each id an individual disposition: the batch that edits the cited
  code, or a written no-change reason. U02B-SUTC-007 is already fixed by B053; A02-F016 by B003.
- **Tests:** none (read-only).
- **Plan v2:** B178.
- **Related:** plan v2 Appendix B.

### GAP-13: 159 never-named test files not audited

- **Severity:** low
- **Where:** `tests/WSGM.Tests/**` (for example `AutoTdpControllerTests`, `SplashAssetsTests`,
  `SplashThemeTests`, `SteamAutostartTests`, `SteamGlyphCssTests`, `ProfileEditsTests`,
  `ProfileResolverTests`, 44 Shell and 58 Core files).
- **Problem:** The getter-only and copied-predicate audit the plan asks for (L38) was not done on
  these files. A live-state sweep found only `RegistryValueSnapshotTests` (disposable
  `HKCU\Software\WSGM.Tests`) and `ThemePathsTests` (`mklink /J` inside temp), both safe.
- **Best solution:** B178 audits them and files any real fix as an appended batch before B179;
  getter-only tests are deleted where the owning batch touches the file.
- **Tests:** none (read-only audit).
- **Plan v2:** B178.
- **Related:** GAP-14.

### GAP-14: Production files only grep-swept or reviewed by nobody

- **Severity:** low
- **Where:** the 21 `src/WSGM/Core` and 18 `src/WSGM/Shell` files in critic table 1.1;
  `Interop/LastInput.cs`, `Interop/TouchKeyboard.cs`; winsvc's light sweep (`HotkeyService`,
  `KeyboardService`, `VolumeFeedback`, `UpdateChecker`, `ForegroundWindowWatcher`, `WindowIcons`,
  `VariableRefreshCapabilities`, `VolumeIndicator`, `AudioPlaybackChoices`, `UacSettings`,
  `WakeLockStatus`, `ModernStandbyDiagnostics`, `RegistryValueSnapshot`, `UpdateExitWatcher`);
  `Core/AutoTdp.cs`, `AutoTdpTrace.cs` (structure only).
- **Problem:** Coverage records would otherwise claim a review that did not happen.
- **Best solution:** This consolidation line-read every file in critic table 1.1 plus `LastInput`
  and `TouchKeyboard`; findings are the UNCOVERED sections below, and the rest has no defect. B178
  records them as line-read (this file), keeps the winsvc light-swept list as "swept, not
  line-read", and records AutoTDP as "structure reviewed" because no retuning is allowed.
  `SimulatedGraphicsOverlaySource` is overlay-test only and must stay inert through the overlay
  batches.
- **Tests:** none.
- **Plan v2:** B178.
- **Related:** GAP-13.

### GAP-15: Lab side-file migration flagged as over-engineering

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Wizard/LabMachineState.cs` and the Lab record files.
- **Problem:** labcore addition 2 proposed a one-time migration of the Lab side files into a
  `LabMachineChanges` record. A developer tool needs no migration layer, and the migration would be
  new mechanism.
- **Best solution:** Read the existing records where they are through one path helper; no fold, no
  migration. `LabMachineState.Read` throws for anything but a missing file so `Update` never
  overwrites what it could not read.
- **Tests:**
  `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabMachineState|FullyQualifiedName~LabRecovery"`.
- **Plan v2:** B158.
- **Related:** LABCORE-001, LABCORE-010.

### UNCOVERED-001: The Steam performance state sends fps_limit 0 after the cap is switched off

- **Severity:** low (plausible; Steam's reaction to a 0 value not observed live)
- **Where:** `src/WSGM/Core/NativeQamPerfProjection.cs:91-94`
  (`frameLimit = values.FrameLimit ?? HighestOption(...)`), comment at `:134-139`;
  `src/WSGM/Shell/NativeQamSemanticServices.cs` (`FrameLimitEnabled when !change.AsFlag` writes 0,
  near line 433).
- **Problem:** Switching the frame limit off in Steam's panel writes `FrameLimit = 0`, which is
  persisted and becomes `Desired.FrameLimit = 0`. `Project` substitutes the highest notch only for
  `null`, so it publishes `fps_limit = 0` and `fps_limit_external = 0`, although 0 is filtered out
  of `fps_limit_options` and the projection's own comment says the value is "the highest offered
  notch when no cap is set, never 0" (the pairing rule that crashed the Performance tab on
  2026-08-30). `EnableFrameLimitWatts` already treats 0 like unset. The existing test covers only
  `null`.
- **Best solution:**
  `int? frameLimit = frameLimitOptions is not null ? values.FrameLimit is > 0 ? values.FrameLimit : HighestOption(support.FrameLimitOptions) : null;`.
  `IsFpsLimitEnabled` stays `values.FrameLimit is > 0`, so a disabled cap reads as off at the
  highest notch, exactly like an unset one.
- **Tests:** add `ADisabledFrameLimitSitsAtTheHighestCapAndReadsAsOff` beside
  `AnUnsetFrameLimitSitsAtTheHighestCapAndReadsAsOff` in `NativeQamPerfProjectionTests` with
  `new PerformanceValues(0, 1)`, and `[InlineData(0, false)]` on `TheCapAndItsEnabledFlagAgree`.
  Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQamPerfProjection"`.
- **Plan v2:** B084.
- **Related:** STEAMHOST-V-008.

### UNCOVERED-002: Steam autostart restore drops a task record it could not read

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamAutostartTakeover.cs:143-156` (`Restore`, scheduled-task branch);
  `src/WSGM/Core/AutostartSystem.cs:117-131` (`IsTaskEnabled` returns
  `ReadTaskEnabled(...) ?? true`).
- **Problem:** `IsTaskEnabled` reports `true` when the task cannot be read (a `schtasks /Query`
  timeout or a non-zero exit). `Restore` reads that as "someone turned it back on", adds the record
  to `restored` and the caller removes it from config without enabling anything. A transient query
  failure during uninstall's `RestoreAll` leaves Steam's logon task disabled for good, with no
  record left to restore it.
- **Best solution:** Remove the pre-read. `Restore` calls `SetTaskEnabled(entry.Location, true)`
  directly: enabling an already enabled task is a no-op, so "the user's later decision wins" still
  holds. Success drops the record; failure keeps it and logs, so `RestoreAll` returns 1 and the
  record survives for a later run. A deleted task then stays recorded and is reported as not
  restored, which is the truthful outcome.
- **Tests:** in `SteamAutostartTests`, a fake whose `IsTaskEnabled` would throw or report unreadable
  still gets `SetTaskEnabled(true)`; a failing `SetTaskEnabled` keeps the record. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`.
- **Plan v2:** B180.
- **Related:** CRIT-001, CONFLICT-27, U04B-LFA-013..049 (B024 closure).

### UNCOVERED-003: Elevated one-shots record recovery state in the elevating account's config

- **Severity:** low (plausible; only under over-the-shoulder UAC)
- **Where:** `src/WSGM/Core/SteamAutostartService.cs` (`RunElevatedDisable` via
  `--disable-steam-autostart`, `RecordDisabled`), `src/WSGM/Core/UacSettings.cs` (`ApplyDirect`),
  `src/WSGM/Core/LockScreenSettings.cs` (`ApplyDirect`).
- **Problem:** These run in a self-elevated WSGM instance and write the pre-change Windows state
  into `ConfigStore` of the process user. When a standard user elevates with an administrator's
  credentials, that is the administrator's `config.json`: the user's own config has no record, and
  the user's uninstall or restore cannot put Steam autostart, UAC prompts or sign-in-on-wake back.
  Setup already gets an identity refusal for the same situation (INSTALL-005, B030); these helpers
  do not.
- **Best solution:** Reuse B030's check (session interactive user from `WTSQuerySessionInformation`
  compared with the process user) at the start of the three elevated one-shots. WSGM cannot
  reference WSGM.Setup, so B030 writes that one helper in `src/WSGM.Install` (referenced by both
  WSGM and WSGM.Setup) instead of inside `SetupEngine.Detect`, and both call it; when they differ,
  write nothing to Windows or config and return failure, so the caller shows its existing failure
  text. No `TargetUser` plumbing, consistent with CONFLICT-17.
- **Tests:** with an injected identity pair that differs, each one-shot returns failure and calls
  neither the Windows writer nor the config mutation. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart|FullyQualifiedName~UacSettings|FullyQualifiedName~LockScreen"`.
- **Plan v2:** B030.
- **Related:** INSTALL-005, CONFLICT-17, GAP-04.

### UNCOVERED-004: Sound-pack byte and length caps outside D2 that CRIT-002 missed

- **Severity:** low
- **Where:** `src/WSGM/Core/Sounds/SoundPackLibrary.cs:58-61` (manifest over 256 KiB refused),
  `:113-116` (`.wsgm-store-id` over 256 bytes refused), `:130` (pack id over 160 characters
  refused).
- **Problem:** Three more caps refuse content. D2 lists only "the sound-pack zip-bomb guard on
  expanded bytes" for this subsystem, and plan v2 removes every other cap. A legitimate `pack.json`
  with many mappings over 256 KiB shows the pack as broken. The store-id file is written by WSGM
  itself, and the folder id is validated for safety by the invalid-character, `.`/`..` and
  containment checks, so the length tests protect nothing.
- **Best solution:** Delete the three checks. Keep the JSON shape checks (they are type checks),
  `GetInvalidFileNameChars`, the `.`/`..` refusal, `CheckPath` containment and reparse-point
  refusal.
- **Tests:** a 300 KiB valid manifest reads; a 200-character folder id with valid characters reads;
  `..` and a path escaping the root are still refused. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SoundPack"`.
- **Plan v2:** B138.
- **Related:** CRIT-002.

### UNCOVERED-005: Deleting the selected sound pack clears the selection before the delete can fail

- **Severity:** low
- **Where:** `src/WSGM/Shell/SoundPackService.cs:174-195` (`DeleteAsync`).
- **Problem:** When the pack being deleted is selected, `DeleteAsync` saves an empty selection,
  publishes empty overrides and raises `Changed` before `_library.Delete(id)`. If the delete then
  fails (a reparse point inside the pack, a locked file), the command reports failure but the pack
  is still installed while the user's choice is gone and Steam has fallen back to its default
  sounds. A failed command should change nothing, the same rule B138 applies to content saves.
- **Best solution:** Reorder: `StopPreview()`, `_library.Delete(id)`, then, only if the deleted id
  was selected, `_saveSelected("")` and the `Selected` state update, then `Load()`. The overrides
  are data URIs, so nothing in Steam holds the files and deleting first is safe.
- **Tests:** a library fake whose `Delete` throws leaves `Selected` and the saved selection
  unchanged; a successful delete of the selected pack clears both. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SoundPack"`.
- **Plan v2:** B138.
- **Related:** CRIT-002.

## Nit

### CRIT-004: DesktopAppProcessBackend reads identities through MainModule

- **Severity:** nit (critic rated low to medium; reduced after checking the reachable paths)
- **Where:** `src/WSGM/Core/DesktopAppProcessBackend.cs:30-31` (`Capture`), `:81-87` (`StopAsync`
  identity check); caller `src/WSGM/Core/DesktopAppLifecycle.cs` (`StopAsync`, `RestoreAsync`).
- **Problem:** `Capture` and the PID-reuse check read `Process.MainModule`, which needs
  `PROCESS_VM_READ`. From a medium-integrity WSGM an elevated integration throws `Win32Exception`
  before the intended "Cannot restore elevated ... from this session" message, so the log shows an
  access-denied text instead of the reason. The outcome is the same either way:
  `DesktopAppLifecycle.StopAsync` catches the capture failure and preserves Explorer, which is the
  safe result. `Capture` is per rule, so nothing fails "for every rule". The rest of the critic's
  item is not a defect (see Refuted or no-change). `Kill(true)` applies only to the listed
  integrations, never Explorer, so the Explorer rule holds.
- **Best solution:** Read the path with
  `NativeShellProcess.TryGetImagePath(checked((uint)process.Id))` in `Capture` and in the
  `StopAsync` identity check, the primitive B112 already moves the Explorer probe onto
  (SESSION-035). A null path keeps today's throw ("Cannot capture {rule}'s executable."); do not
  skip the process, because a capture that silently omits a running integration would let
  `StopAsync`'s final `Capture(rule).Count == 0` check pass and retire Explorer under an app WSGM
  did not stop. No other change to the backend.
- **Tests:** `DesktopAppLifecycleTests` stay as they are (the backend is the live adapter); compile
  and the existing filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Explorer|FullyQualifiedName~DesktopApp"`.
  Attended check inside B112's restore-shell pass: with an elevated listed integration and a medium
  WSGM, the log names the integrity reason and Explorer is preserved.
- **Plan v2:** B138.
- **Related:** U04B-LFA-010, SESSION-035, GAP-04.

### UNCOVERED-006: Steam autostart Apply matches elevated results by kind and name only

- **Severity:** nit
- **Where:** `src/WSGM/Core/SteamAutostartService.cs:66-80` (`Apply` after `RunElevatedAction`).
- **Problem:** After the elevated pass, the result is rebuilt by matching `remaining` sources on
  `Kind` and `Name` only. A per-user `HKCU Run "Steam"` that stayed pending makes a successfully
  disabled machine-scope `HKLM Run "Steam"` report as still needing elevation, so `Complete` is
  false and Settings or setup shows a source as left enabled that is off.
- **Best solution:** Compare whole sources: `SteamAutostartSource` is a record, and both lists come
  from the same scanner with `Enabled = true`, so replace the two
  `remaining.Any(other => other.Kind == source.Kind && other.Name == source.Name)` predicates with
  `remaining.Contains(source)`. That matches on kind, scope, location, name and Wow64 with no new
  helper.
- **Tests:** `Apply`'s elevated branch runs a real UAC action, so move the two-list rebuild into an
  `internal static` function of the result and the rescanned list (the same expression, no new
  state) and test that: two same-name sources in different scopes, the user one still enabled and
  the machine one gone from the rescan, puts the machine one in `Disabled`. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`.
- **Plan v2:** B016.
- **Related:** CRIT-001, CONFLICT-27.

### UNCOVERED-007: Performance model comments still say RTSS writes wait for the executable

- **Severity:** nit
- **Where:** `src/WSGM/Core/PerformanceModels.cs:25-35` (`PerformanceApplicationTarget` remarks),
  `:83-97` (`PerformanceState` remarks).
- **Problem:** The remarks say "RTSS writes wait for that enrichment" and that `Observed` is empty
  while "the target's executable is still unknown". After USER-001 only `FrameLimit` waits;
  `OverlayLevel` applies on the global profile.
- **Best solution:** Reword both remarks in the same commit as USER-001 step 5: only the frame
  limit, written into a per-application RTSS profile file, waits for the executable; the overlay
  level applies through the global profile meanwhile. `Observed` is empty while RTSS is unavailable,
  and while the executable is unknown it carries only the overlay level read from the global profile
  (its frame limit stays null).
- **Tests:** none (comments).
- **Plan v2:** B005.
- **Related:** USER-001.

### UNCOVERED-008: OverlayMediaNative duplicates two NativeMethods declarations

- **Severity:** nit
- **Where:** `src/WSGM/Interop/OverlayMediaNative.cs` (whole file);
  `src/WSGM/Interop/NativeMethods.cs:345-354` (`CreateWindowExW`, `DestroyWindow`); caller
  `src/WSGM/Overlay/OverlayMediaPreview.cs:96, :117`.
- **Problem:** The file re-declares `CreateWindowExW` and `DestroyWindow` with the same signatures
  `NativeMethods` already has, a second home for the same P/Invoke.
- **Best solution:** Delete `OverlayMediaNative.cs` and call
  `NativeMethods.CreateWindowExW(0, "STATIC", "WSGM media", 0x4e000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0)`
  and `NativeMethods.DestroyWindow(control.Handle)`.
- **Tests:** build;
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"`
  to confirm the media preview still constructs.
- **Plan v2:** B132.
- **Related:** none.

### UNCOVERED-009: Orphaned doc block in SteamGlyphPresentation

- **Severity:** nit
- **Where:** `src/WSGM/Core/SteamGlyphPresentation.cs:64-79`.
- **Problem:** The summary and remarks describing "Valve's glyph resource names, mapped to the
  physical control each one depicts" sit on `SoftPullResourceMap` together with that field's own
  summary, so the field carries two `<summary>` elements and `StableResourceMap` (line 85), which
  the text describes, carries none.
- **Best solution:** Move the first summary and remarks block onto `StableResourceMap`.
- **Tests:** build (documentation file generation stays warning-free).
- **Plan v2:** B139.
- **Related:** none.

### UNCOVERED-010: Steam autostart task disable is reported pending when the confirming query fails

- **Severity:** nit
- **Where:** `src/WSGM/Core/SteamAutostartTakeover.cs:81` (`Disable`, scheduled-task branch:
  `!system.SetTaskEnabled(source.Location, false) || system.IsTaskEnabled(source.Location)`);
  `src/WSGM/Core/AutostartSystem.cs:117-120` (`IsTaskEnabled` returns
  `ReadTaskEnabled(...) ?? true`).
- **Problem:** After `schtasks /Change /DISABLE` succeeded, the confirming `IsTaskEnabled` read
  turns any query failure (the 30 s `schtasks /Query` timeout, a non-zero exit) into "still
  enabled". The source is reported pending, so Settings or setup says Steam's logon task was left
  enabled when it is off, and the record stays `Pending`. This gates success on a readback, and it
  is the same `?? true` misreading UNCOVERED-002 removes from `Restore`. The registry-approval
  branch does the same: after `WriteApproval` it reads the value back and leaves the source pending
  (record `Pending = true`) when the readback still means enabled, which D9 rules out on host paths
  too.
- **Best solution:** Drop `|| system.IsTaskEnabled(source.Location)`: the `schtasks` exit code is
  the write's outcome, as it already is for `Restore` after UNCOVERED-002. In the registry-approval
  branch, delete the post-write `ReadApproval` and its `ApprovalMeansEnabled` pending branch: hold
  `DisabledApproval()` in a local (it embeds a fresh filetime, so it must be built once), write that
  array and set `entry.WrittenApproval` to its Base64. `Restore` still recognises WSGM's own change
  by comparing the current bytes with `WrittenApproval`, which now holds exactly what was written. A
  write that throws keeps today's catch (logged, source pending).
- **Tests:** in `SteamAutostartTakeoverTests`, a fake whose `SetTaskEnabled(false)` succeeds while
  `IsTaskEnabled` keeps reporting true lands the source in `Disabled` with `Pending = false` in the
  record; a fake whose `ReadApproval` after the write still reports enabled lands the source in
  `Disabled` with `WrittenApproval` equal to the written bytes and no post-write read;
  `ARefusedTaskWriteStaysPending` stays. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`.
- **Plan v2:** B016.
- **Related:** UNCOVERED-002, CRIT-001, CONFLICT-27.

## Refuted or no-change

One whole id is no-change by maintainer decision; every other CRIT, USER, CONFLICT, GAP and
UNCOVERED id has a section above. Where a conflict's adopted resolution is "change nothing"
(CONFLICT-08, CONFLICT-11), the section records it.

- **CONFLICT-06** (bridge identity with host id and nonce; toolkitjs R17/B7 add `hostId`, a
  per-bootstrap nonce and a foreign-bridge refusal, toolkitcs R6 drops them; `bridge.ts`,
  `SteamUiBridgeIdentity.cs`, `SteamUiBridge.cs`): Dropped by maintainer decision (security theater,
  DECISIONS.md). No `hostId`, nonce, foreign refusal, sender or ownership check is added, and B177
  writes no trust-boundary note (U02A-SUTC-004 and U02A-SUTC-016 go the same way). The bridge keeps
  today's asset hash, schema and generations, and replacing an older build's bridge stays the update
  path, so a restarted WSGM reconnects as today. No functional part remains.

Parts of findings dropped by maintainer decision (the rest of each id stands in its section):

- GAP-03, security parts: B025's `InstallLayout.AppExe`-only elevated launch and single-file change,
  B026 whole, B027's `%ProgramData%\WSGM` DACL and download move, B029's admin-only XML folder,
  B031's INSTALL-010 broker constants and the D14 VM check. Dropped by maintainer decision (security
  theater, DECISIONS.md).
- GAP-04, U04B-LFA-002 (task XML staged in a user-writable folder): dropped by maintainer decision
  (security theater, DECISIONS.md). U04B-LFA-012's task cleanup stays in B029.
- GAP-10, U02A-SUTC-004 (bridge open to any SharedJSContext script, trust boundary undocumented):
  dropped by maintainer decision (security theater, DECISIONS.md).
- CONFLICT-05, "plugin Steam UI modules are read at session start": replaced by the maintainer's
  answer that they register when the plugin becomes ready.
- CONFLICT-19, "for Ally tables, enabling controller management is the user action that re-arms" and
  every non-Claw unresolved or uncertain recovery entry: removed by D9 (no readback machinery for
  any vendor except the Claw).

Parts of findings refuted by the solution check (the rest of each id stands in its section):

- CRIT-004, declined UAC escaping `RestartAsync`: not reachable. `Capture` records an instance as
  `Elevated` only when WSGM itself runs at high integrity (it throws "Cannot restore elevated ...
  from this session" otherwise), and a `runas` start from an already elevated process shows no
  consent prompt, so there is nothing to decline. A `Win32Exception` from that start is caught by
  `DesktopAppLifecycle.RestoreAsync` and logged. B112 drops the clause.
- CRIT-004, poll without cancellation: the `Task.Delay(100)` loop in `RestartAsync` already ends at
  the caller's deadline, capped at 5 s, and `IDesktopAppBackend.RestartAsync` takes no token. Adding
  one would be mechanism with no defect behind it. B112 drops the clause.
- CRIT-004, "skip an unreadable process": rejected as a remedy. Skipping would let
  `DesktopAppLifecycle.StopAsync`'s final `Capture(rule).Count == 0` check pass while the
  integration still runs, retiring Explorer under it. Today's throw, which preserves Explorer, is
  kept.
- USER-001, "the limited image-name query from B100" for holding a discovered RTSS: B100 depends on
  B005, so B005 cannot use it, and discovery already verified the process id. The section now
  carries that id on the probe.
- CRIT-003, "publish the written mode as the current value": no cached value is added.
  `NativeQamHybridCoreService` reads Windows on every publication by design (a power-scheme switch
  can replace the preference), which WINSVC-010 also settled for `NativeQamPowerProfileService`.

Files line-read for this report with no finding (not ids, listed so B178 can record them):
`Core/ApplicationProfileRules.cs`, `Core/Credits.cs`, `Core/DevicePrerequisites.cs`,
`Core/DisplayOperatingPoint.cs`, `Core/DisplayTimeoutPolicy.cs`, `Core/ExceptionList.cs`,
`Core/FanCurvePresets.cs` (matches HC `IDevice.fanPresets`), `Core/FileCleanup.cs`,
`Core/ForegroundApplicationFilter.cs`, `Core/KnownStartupApps.cs`, `Core/ManualTdpProfile.cs`,
`Core/ModernStandbyPolicy.cs` (the three-attempt bound is a loop bound, not a content cap),
`Core/ObservableObject.cs`, `Core/PerApplicationPowerPolicy.cs`, `Core/RelayCommand.cs`,
`Core/Sounds/SoundsConfig.cs`, `Core/WindowsPolicyOperation.cs`, `Shell/AnimationBrowseSession.cs`,
`Shell/ApplicationProfileSyncBuilder.cs`, `Shell/CommonPluginDependencyPlan.cs`,
`Shell/GameModeCardServicePolicy.cs`, `Shell/GameWindowReturn.cs`, `Shell/IChangeSource.cs`,
`Shell/IExtensionsTabSection.cs`, `Shell/NativeQamCpuBoostService.cs` (its readback wording is
covered by B017), `Shell/OverlayToolSessions.cs`, `Shell/SimulatedGraphicsOverlaySource.cs`,
`Shell/SplashPolicy.cs`, `Shell/SplashStyle.cs`, `Shell/SteamExtensionsTabBackend.cs`,
`Shell/SteamGameContextMenuBackend.cs`, `Shell/SteamPowerMenuBackend.cs`,
`Shell/VolumeIndicatorWindow.axaml.cs`, `Interop/LastInput.cs`, `Interop/TouchKeyboard.cs`,
`eng/checkout-controller-dependency-sources.ps1`, `eng/extract-hc-devices.ps1`,
`tools/WsgmLibTest/capture-steam-window.ps1` (attended tool), `tools/PerfLab/WSGM.PerfLab.csproj`
(compiled in the gate by B174, BUILD-003).
