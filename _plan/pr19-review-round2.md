# PR #19 review, round 2 — verified findings

Produced 2026-08-30 by a 37-agent review workflow: 11 agents re-verified every one of the 122
Codex inline comments against current HEAD (the branch had moved ~40 commits past what Codex
reviewed), 13 agents freshly reviewed the whole 520-file `master...2.0` diff in area buckets, and
13 adversarial verifiers re-traced every fresh finding and killed the false positives. Raw agent
results live in the session workflow journal; the still-present Codex items keep their GitHub
comment ids so the full original text is one click away on the PR.

**Counts.** Codex comments: 59 already fixed at HEAD, **58 still present** (9 high / 33 medium /
16 low), 4 invalid, 1 needs-hardware. Fresh review: 151 findings, **144 confirmed** (5 high / 51
medium / 88 low), 6 refuted, 1 uncertain. The two sets have **zero overlap** — complementary
coverage, not double counting.

Severity vocabulary is the repository's (blocker/high/medium/low). Line anchors are HEAD at the
time of review and will drift; the summaries are written to be findable by content.

## High severity — fix before release

### Still-present Codex findings

- **[3887865291]** `plugins/…/WindowsHidTransports.cs` — controller-reader faults after the first
  sample are never observed; input silently stops while ownership still reads "plugin".
- **[3889089518]** `src/WSGM.DeviceHost/DeviceHostSession.cs` — controller management runs outside
  the lifecycle/terminal gating.
- **[3889089538]** `src/WSGM.DeviceLab/Application/DeviceLabApplication.cs` — attended activation
  gated on imported identity, not live identity.
- **[3889089486]** `src/WSGM.DeviceLab/Cli/DeviceLabCli.cs` — Ctrl+C kills `test hardware` without
  the attended cleanup.
- **[3889089484]** `src/WSGM.DeviceLab/Gui/MainWindow.cs` — window close never awaits attended
  cleanup.
- **[3889089516]** `src/WSGM/App.axaml.cs` — failed startup/cleanup exits 0, defeating the
  watchdog and the crash-loop breaker.
- **[3887865289]** `src/WSGM/Shell/DeviceCoordinator.cs` — host-exit cleanup skips make-safe; the
  restart cycle faults on a stale target.
- **[3889089537]** `src/WSGM/Shell/SessionModes.cs` — Explorer is not restored when the game-mode
  commit throws.
- **[3889178982]** `src/WSGM/Shell/ShellSession.cs` — RTSS policy persistence erases per-game
  TDP/VRR/switch fields (see also the fresh `PersistPerformancePolicyAsync` finding below — two
  independent confirmations of the same data-loss family).

### Confirmed fresh findings

- `src/WSGM/Core/SteamUiBridge.cs:395` — `SteamUiBridgeHost.DisposeAsync` throws
  `OperationCanceledException` on every orderly shutdown with Steam reachable, aborting the
  `ShellSession` teardown lambda before the transport disposal and desktop restore.
- `src/WSGM/Shell/ControllerManager.cs:438` — **the UI-capture mechanism has no production
  caller**: `ClaimUiAsync`/`ReleaseUi` are invoked only by tests, so in managed-controller mode
  every press inside the overlay/taskbar still reaches the virtual pad the game reads, and six of
  the eight documented zero-output triggers are never raised (D14's mechanism is unwired). Found
  independently by two buckets.
- `src/WSGM/Shell/ControllerManager.cs:251` — `StartAsync` always creates a fresh virtual target,
  but the plugin republishes physical identities on resume while the previous target exists —
  controller management faults on every suspend/resume cycle.
- `src/WSGM/Settings/PluginSettingsRowViewModel.cs:185` — Choice plugin settings broken both
  directions: the row round-trips `TextValue` while resolver and save use `ChoiceValue`, so a
  stored choice never displays and a new pick saves as all-null.

### Uncertain (needs hardware)

- `src/WSGM/Input/SteamDeckNeptuneReport.cs:97` — digital trigger bits set on any non-zero
  analogue value, while `GlyphInputTestMap` in the same changeset documents that real triggers
  rest slightly off zero (it uses a 0.2 threshold). Likely permanent phantom trigger-press on
  hardware; verify on the device.
- **[3889089507]** `plugins/…/WindowsHidTransports.cs` — rumble report possibly not padded to the
  HID output report length; unchanged at HEAD, needs the device to settle.

## Still-present Codex findings — medium

One line each; the GitHub comment carries the full text, re-verified at HEAD.

- [3889089492] `build.ps1` — stale VIIPER staging ships when the build is skipped
  (dup family: [3889654561]).
- [3889178975] `plugins/…/Claw8A2VmPlugin.cs` — Arc Sync restore failure not propagated into the
  stop result.
- [3889654534] `plugins/…/ClawCapabilities.cs` — no lighting rollback when cancellation fires
  after the write.
- [3889089528] `plugins/…/ClawResources.cs` — haptic duplicate state committed before the
  physical write.
- [3890193957] `plugins/…/ClawResources.cs` — release restores the acquisition-time snapshot, not
  pre-mutation state.
- [3889654544] `src/WSGM.Device.Sdk/Settings/PluginSettingsManifest.cs` — setting defaults not
  validated against declared bounds.
- [3890193958] `src/WSGM.DeviceHost/DeviceHostSession.cs` — operation faults discarded; host
  exits 0 as Clean (also found fresh, below).
- [3889089523] `src/WSGM.DeviceLab/Gui/MainWindow.cs` — plugin test workflows run on the Avalonia
  UI thread.
- [3889089497] `src/WSGM.DeviceLab/Probes/ReadProbeWorkerSupervisor.cs` — read-probe worker not
  killed on caller cancellation (dup: [3889281083]).
- [3889089541] `src/WSGM.DeviceLab/Scaffolding/ScaffoldFromCaptureWorkflow.cs` — scaffold
  silently keys the plugin to an arbitrary first USB endpoint.
- [3889281077] `src/WSGM.DeviceLab/Testing/AttendedPluginAction.cs` — availability check rejects
  the Claw's Choice/Action capability shapes.
- [3889089483] `src/WSGM.DeviceLab/Testing/PluginTestWorkflow.cs` — Device Lab load context
  throws on OS-native DLL imports.
- [3889178977] `src/WSGM/Core/ConfigStore.cs` — `FrameLimitStrategy` not in the enum-repair pass;
  one unknown name quarantines the config (also found fresh).
- [3890193944] `src/WSGM/Core/ConfigStore.cs` — cached plugin declarations unrepaired; unknown
  enum quarantines the config (also found fresh).
- [3889089503] `src/WSGM/Core/SteamGlyphPresentation.cs` — aliased logical controls counted
  absent, hiding rows that have generated artwork.
- [3889654551] `src/WSGM/Settings/Pages/PluginSettingsPage.axaml.cs` — profile capability IDs do
  not match the Claw plugin's.
- [3889281070] `src/WSGM/Shell/AutoTdpService.cs` — re-enable races the previous generation's
  fire-and-forget stop (also found fresh).
- [3889654522] `src/WSGM/Shell/ControllerManager.cs` — replacement failure drops HidHide without
  the physical release.
- [3889654527] `src/WSGM/Shell/ControllerManager.cs` — target replacement not serialized with
  sample routing.
- [3887865294] `src/WSGM/Shell/DeviceCapabilityRouter.cs` — `ReconcileResult` immediately removes
  the timed-out command entry (also found fresh: late-result reconciliation is dead).
- [3890193942] `src/WSGM/Shell/DeviceCoordinator.cs` — lexical capability order breaks paired
  PL1/PL2 decreases.
- [3890193948] `src/WSGM/Shell/DeviceCoordinator.cs` — unacknowledged controller-disable leaves
  plugin policy mismatched.
- [3890193949] `src/WSGM/Shell/DeviceCoordinator.cs` — concurrent profile reconciliations can
  interleave stale writes.
- [3889654532] `src/WSGM/Shell/DeviceHostClient.cs` — pending samples must be rechecked after the
  tail dispatch (also found fresh, one level down).
- [3890193952] `src/WSGM/Shell/PluginSettingsCoordinator.cs` — plugin-settings deliveries not
  serialized as complete sets.
- [3887865288] `src/WSGM/Shell/RunningApplicationTarget.cs` — transient shortcut
  profile-resolution failures never retried.
- [3889178985] `src/WSGM/Shell/RunningApplicationTarget.cs` — RTSS targets not resolved for
  ordinary Steam games.
- [3889178987] `src/WSGM/Shell/ShellSession.cs` — per-game profile enable switch ignored for RTSS
  overrides.
- [3889654554] `src/WSGM/Shell/ShellSession.cs` — profile scope selected without matching the
  active plugin.
- [3890193954] `src/WSGM/Shell/ShellSession.cs` — OEM actions backed only by permanent failure
  stubs.
- [3889089533] `src/WSGM/Shell/SteamUiSessionHost.cs` — semantic requests not cancelled on a
  Steam generation change.

## Still-present Codex findings — low

- [3889089521] `installer/WSGM.iss` — no restart offered after a fresh-install USB/IP driver
  install (also found fresh with detail).
- [3889089543] `plugins/…/ClawInput.cs` — OEM button latch shares one expiry for both buttons.
- [3889654538] `plugins/…/ClawResources.cs` — controller-source stop failure swallowed; release
  still claims verified.
- [3889089512] `src/WSGM.Device.Sdk/Ipc/CanonicalSampleCodec.cs` — `SensorTimestamp` not
  round-tripped through the ring codec.
- [3889281075] `src/WSGM.Device.Sdk/Ipc/DeviceFrameStream.cs` — `DisposeAsync` disposes the write
  gate under active writers (also found fresh).
- [3889089526] `src/WSGM.DeviceHost/PluginHostAdapter.cs` — descriptor-generation
  check-then-exchange race.
- [3889178979] `src/WSGM/Core/ConfigStore.cs` — explicit-null `RtssProfileName` NREs `Normalize`,
  quarantining the config (also found fresh).
- [3889178981] `src/WSGM/Core/RefreshRatePairingService.cs` — failed refresh restore discards the
  snapshot and the shutdown result (also found fresh).
- [3889089514] `src/WSGM/Core/RtssFrametimeReader.cs` — 32-bit tick wrap discards valid samples.
- [3889089505] `src/WSGM/Core/RtssNativeAdapter.cs` — full identity/signature work repeated every
  2 s poll (also found fresh, promoted to medium there).
- [3890193940] `src/WSGM/Shell/AutoTdpService.cs` — restore baseline cleared before restoration
  confirmed.
- [3889089532] `src/WSGM/Shell/DeviceCapabilityRouter.cs` — `DisposeAsync` disposes command gates
  under in-flight `ExecuteAsync`.
- [3889281080] `src/WSGM/Shell/DeviceCoordinator.cs` — null device definition silently preserves
  the previous cycle's identity.
- [3889178989] `src/WSGM/Shell/HidHideOwnership.cs` — volume identity lost when matching HidHide
  applications (see also the fresh cleanup-notation finding).
- [3889178988] `src/WSGM/Shell/ShellSession.cs` — device suspend state committed before the
  transition succeeds.
- [3889089496] `src/WSGM/WSGM.csproj` — `libviiper.dll` ships regardless of controller component
  selection.

## Confirmed fresh findings — medium

Grouped by bucket; each verified by an adversarial second pass at HEAD.

**core-cef**
- `PersistentSteamUiTransport.cs:306` — no CDP domain is ever enabled on the MainWindow/
  QuickAccess channels, so generation-bump notifications are unreachable for them and the glyph
  patch stays Verified across an in-place document replacement.
- `SteamUiSessionHost.cs:279` — `OnGenerationChanged` queues resynchronization only for
  SharedJsContext; a MainWindow reconnect parks the glyph patch in Retrying forever.
- `SteamUiSessionHost.cs:486` — `SetPatchStates` toggles only 8 of 17 registered patch ids; with
  native QAM off and glyphs on, the other nine churn Applying→Degraded against a removed bridge.

**core-other**
- `ConfigStore.cs:564` — `NormalizePerformance` NREs on explicit-null `RtssProfileName`.
- `ConfigStore.cs:126` — enum-repair omits `Performance.FrameLimitStrategy`.
- `ConfigStore.cs:145` — enum-repair omits the cached PluginSettings declaration.
  (All three: one bad name/null discards the entire config file, snapshots included.)

**core-perf**
- `PerformanceProfiles.cs:40` — a complete second per-app profile resolution system with zero
  production callers, whose documented `UsePerGameProfile` semantics contradict the live
  `PerformancePolicyResolver` path.
- `PerformanceService.cs:353` — turning a per-app profile off leaves the saved RTSS per-app
  snapshot governing the game.
- `PerformanceService.cs:691` — a global edit while an app snapshot is in force verifies only the
  global profile, then flags the unchanged snapshot as an external change.
- `RtssNativeAdapter.cs:172` — every Read/Apply re-runs full discovery including two
  WinVerifyTrust revocation checks; one poll tick does it twice, one command four times.
- `ShellSession.cs:2569` — `PersistPerformancePolicyAsync` rebuilds `Performance.Applications`
  from a policy modeling only FrameLimit/OverlayLevel, deleting `TdpWatts`,
  `VariableRefreshRate`, `UsePerGameProfile` from every per-app entry.

**devicehost-plugin**
- `Claw8A2VmPlugin.cs:254` — the post-verify state refresh runs unguarded inside
  `ExecuteCommandAsync`; a transient read failure kills the session or rewrites a verified
  success as Indeterminate.
- `Claw8A2VmPlugin.cs:2150` — the `Indeterminate()` factory hard-codes
  `Rollback = RestoredUnverified`, fabricating a restore that never happened.
- `Claw8A2VmPlugin.cs:509` — a failed ArcSync/VRR restore at stop is discarded;
  `PluginStopStatus.Clean` is reported with the panel left in an unchosen state.
- `WindowsHidTransports.cs:297` — a mid-cycle fault of the 125 Hz reader task is unobserved until
  stop: no trace, no service-state transition.
- `DeviceHostSession.cs:262` — a faulted operation task is swallowed with no diagnostic; the host
  exits 0.

**devicelab**
- `DeviceLabCli.cs:61` — community-plugin exceptions outside the fixed catch list crash the CLI,
  discarding the structured report of a run that may have touched hardware.
- `DeviceLabCli.cs:442` — unknown options silently ignored everywhere except `test hardware`; a
  typo'd `--shareable` produces an unredacted inventory the user believes sanitized.
- `KnownMsiClaw.cs:187` — the fan-rpm probe demands byte-identical RPM across reads and across a
  1 s repetition, which a live tachometer routinely fails.
- `ReadProbeWorkerSupervisor.cs:228` — the supervisor's kill deadline equals the worker's own
  budget, so the graceful deadline-exceeded path is unreachable.

**input-overlay**
- `GamepadNavigation.cs:356` — CurveEditor's keyboard/gamepad editing is unreachable in Settings:
  the navigation tunnel consumes direction keys for every focused control except
  TextBox/Slider/ComboBox before `CurveEditor.OnKeyDown` runs.
- `ManagedControllerRouter.cs:541` — a validator-forced neutralization discards the failure
  reason and logs nothing; a game-visible input drop is undiagnosable remotely.
- `UiCapture.cs:143` — six of eight documented mandatory zero-output triggers are never raised.
- `ViiperControllerBackend.cs:190` — a lost target drops the handle without `DeviceRemove`,
  leaking the VIIPER device object and its feedback callback; removal then "succeeds" from
  bookkeeping alone.

**js-assets**
- `NativeQamBootstrap.ts:1386` — audio device removals never reach the running store: the delete
  loop runs after `known = seen`, so its filter is always false.
- `NativeQamBootstrap.ts:1531` — audio `remove()` deletes the namespace but never undoes the
  live-store feeding; the section stays visible with controls that throw.
- `NativeQamBootstrap.ts:872` — the brightness reclaim path never captures `originalValue`, so
  `remove()` and the failed-install rollback write `undefined` into the flag, which the hook's
  `?? true` reads as available.
- `NativeQamBootstrap.ts:1141` — the network gate ignores its own `__wsgmOwnedGetter` marker on
  install, so a replaced bridge saves the previous bridge's `() => true` as "original".
- `NativeQamBootstrap.ts:1160` — the scan wrappers are unmarked; each bridge replacement stacks a
  dead wrapper and restore resurrects the wrong one.
- `NativeQamBootstrap.ts:1006` — Bluetooth `replace()` captures whatever sits on `RF` with no
  ownership marker; same stacking failure.
- `tools/WsgmLibTest/probe-perf-classes.js:30` — two retained probes construct every export of
  module 28013 in a loop — the exact pattern the root hard rule bans. Delete them.
- `tools/WsgmLibTest/qam-harness.mjs:148` — the harness `respond()` envelope omits
  `contextGeneration`/`documentGeneration`/`patchId`/`command`, so `deliver()` rejects every
  response while the harness logs it as answered.

**sdk**
- `ImmutableGlyphPackageDirectorySource.cs:63` — IO/ACL failure and a reparse-point profiles dir
  return an empty list indistinguishable from "no glyphs": zero profiles AND zero errors.

**settings-interop**
- `PluginSettingsPage.axaml:59` — renaming a device profile never sets the dirty flag; a
  rename-only edit is silently discarded at save.
- `PluginSettingsPage.axaml:70` — the lighting ColorPicker is never initialized from the stored
  colour; any touch overwrites it with an unrelated value.
- `SettingsViewModel.cs:1534` — `ShouldWriteDisplayProfiles` compares against the
  construction-time config, never updated after save; every later Automatic-mode save re-seeds
  runtime-owned profiles from stale rows.
- `SettingsViewModel.cs:420` — `LoadPluginSettings` takes the first cached declaration, and
  superseded scopes are never cleared: after a plugin replacement, Settings edits the OLD
  plugin's settings.

**shell-device**
- `AutoTdpService.cs:148` — `Apply(false)` tears down asynchronously without blocking a following
  `Apply(true)`; a fast off→on races restore against new ticks.
- `DeviceCapabilityRouter.cs:510` — timeout registration and `ReconcileResult` cancel each other;
  late-result reconciliation is dead code.
- `DeviceCoordinator.cs:1119` — `_automaticRestartAttempts` never resets after a successful
  restart; the budget exhausts over process lifetime instead of per fault episode.
- `DeviceCoordinator.cs:411` — a timed-out resume the host actually completed leaves the routers
  one generation behind for the rest of the cycle.
- `DeviceHostClient.cs:626` — dispose-time reader wait swallows only four exception types; an
  `InvalidDataException`-faulted reader makes `DisposeAsync` throw and blocks the automatic
  restart designed for protocol faults.

**shell-session**
- `NativeQamAudioService.cs:172` — the audio adapter mutates the UI-thread-owned `AudioManager`
  from bridge/threadpool threads (the radio path beside it marshals; this one does not).
- `ShellSession.cs:340` — the running-application monitor injects JS and polls Steam every 2 s
  for the whole session, bypassing the `Cef.Enabled` master switch that gates everything else.
- `WindowsHidHideAdapter.cs:165` — allowlist entries are written in DOS notation while the repo's
  own device observation records NT-path entries as the working form; the DeviceHost entry likely
  grants nothing.

**tests**
- `CaptureBundleReader` (the documented untrusted-input boundary for shared `.wsgmcap`) has zero
  rejection-path tests.
- `SteamUiSessionHost` (1,676 lines behind every native-QAM surface) has zero coverage; the sole
  obstacle is the constructor demanding the concrete transport where one interface member is
  used.
- `DeviceIntegrationOffTests.cs:111` — `AutoTdpFollowsTheMasterSwitch…` is a vacuous tautology
  masking a real divergence: the startup path applies AutoTDP without the master switch while the
  reload path applies the conjunction.

**build-native**
- `eng/build-viiper.ps1:27` — the `-Validate` gate (the VIIPER Deck tests covering WSGM's three
  patches) is invoked by nothing; the docstring claims the opposite.
- `WSGM.csproj:75` — two publish items map to `LICENSE.txt` and the VIIPER-staged copy silently
  wins; the shipped `{app}\LICENSE.txt` is not the repository LICENSE.
- `controller-components.lock.json:45` — HidHide is pinned but no build/installer/runtime step
  installs it; the ticked component can never become available on a machine without it.

## Confirmed fresh findings — low (88)

Kept terse deliberately; every one is a bounded, demonstrable defect confirmed at HEAD.

**core-cef (5)** — four dead patch classes + two orphan allowlist entries + dead QuickAccess
channel (`NativeQamComponentPatches.cs:59`); eleven identical `RequiredCounts` arrays + three
`EvaluateAsync` copies (`:14`); release-then-resubscribe race leaves a subscribed channel with no
reconnect loop (`PersistentSteamUiTransport.cs:60`); unreachable-target diagnostic names the wrong
target (`SteamInputGlyphStylePatch.cs:113`); the pinned asset hash is CRLF working-tree bytes with
no `.gitattributes` rule, breaking non-autocrlf checkouts (`SteamUiAssetCatalog.cs:18`).

**core-other (6)** — post-budget cleanup faults unobserved (`ApplicationShutdown.cs:140`); JSON
`null` config silently becomes defaults with nothing preserved (`ConfigStore.cs:44`); a raw NUL
byte makes `ConfigStore.cs` a "binary" to grep (`:513`); `ProfileMissing` is an unproducible
outcome (`PhysicalGlyphCatalog.cs:20`); async scheduled-task launch leaks its XML on budget close
(`UnelevatedLauncher.cs:503`); two parallel de-elevation handoff implementations with diverging
cleanup (`:392`).

**core-perf (8)** — `_settling` survives resets/context changes (`AutoTdp.cs:363`); unread
parameter (`:290`); doc block on the wrong method (`DisplayProfiles.cs:213`); command log names
the app profile for global writes (`PerformanceService.cs:1120`); `NormalizePolicy` silently drops
entries ConfigStore preserved (`:1240`); RTSS launcher holds the adapter gate through its 10 s
settle (`:924`); dispose races in-flight `SetAsync` (`:592`); `Restore` clears the captured
original before the apply is known good — both pairing and resolution services
(`RefreshRatePairingService.cs:171`); bare log calls on the 2 s poll path violate the
`Log.Change` rule (`RtssDiscovery.cs:409`).

**devicehost-plugin (4)** — rethrow-only catch (`Claw8A2VmPlugin.cs:1678`); the 2 s write-budget
guard exists three times (`:1853`); `FanTable.Copy()` and `ChargeLimitAddress` unreferenced
(`ClawHardware.cs:58`); `ReadModeAsync` and its 27-line HID exchange have no caller
(`WindowsHidTransports.cs:109`).

**devicelab (10)** — timed-out/unavailable sentinel collisions make captures unexportable
(`PassiveCapture.cs:334`); dead capture/marker/analyzer APIs (`:179`); `probe-read --run` parses
the inventory three times (`DeviceLabCli.cs:127`); the probed-WMI-class list exists twice
(`DeviceLabInventoryWorkflow.cs:65`); undisposed `ManagementObject`s
(`WindowsInventoryCollector.cs:302`); failed USB/graphics queries indistinguishable from empty
hardware (`:200`); case-sensitive manifest lookup on a case-insensitive filesystem
(`DeviceLabPackageSnapshot.cs:136`); inconsistent issue-code casing between verbs
(`GlyphPackageImportWorkflow.cs:90`); the central safety pure-logic surfaces have no unit tests
(`OutputPathPolicy.cs:127`); WMI-layer failures misclassified as `WorkerCrashed`
(`ReadProbeProfiles.cs:90`).

**input-overlay (8)** — curve editor's bounded refusals are silent (`CurveEditor.cs:174`);
`IControllerBackend`/`IUiGamepadSource` are zero-consumer public seams beside the real internal
pair (`ControllerBackend.cs:120`); ~470 lines of test-only fakes compiled into the production
assembly (`ManagedControllerBackend.cs:85`); `OutputRouting.ShouldDeliver` uncalled and
`RequiresStop` constant-false (`OutputRouting.cs:10`); `SourceArbitration.Decide` and friends
uncalled — `UiInputRouter` reimplements the policy inline (`SourceSwitch.cs:56`); source switches
unlogged (`UiInputRouter.cs:127`); `SubmitCanonicalSample` stole `AcquireSteamInputLease`'s doc
comment (`OverlayController.cs:480`); glyph input-test observation leaks when the Device
destination disappears while on the Glyphs page (`OverlayWindow.axaml.cs:960`).

**js-assets (7)** — bridge `dispose()` never stops the Manager gate's interval or settings
registration (`NativeQamBootstrap.ts:158`); `wrapScanning`'s fire-and-forget has no rejection
handler (`:1163`); `nativeRowsHidden` reports the previous render's count (`:2946`); four retired
component kinds fully implemented but never installed (`:2215`); two per-patchId
action-generation allocators for one mechanism (`:1731` + `:71`); `ensurePatched` collapses a
dozen failure points into one unnamed error (`:3110`); harness `remove()`/`status()` cover five of
six gates — the Manager overlay and its 1 Hz interval survive `remove`
(`tools/WsgmLibTest/qam-harness.mjs:237`).

**sdk (7)** — SVG view box never compared to the lock's declared box; `DimensionMismatch`
unreachable for SVG (`GlyphAssetValidation.cs:112`); XML reading stops at `MaxSvgPaths` leaving
the rest unchecked (`:154`); `MaxSvgCommands` enforced nowhere (`GlyphProfile.cs:65`);
`DeviceFrameStream.DisposeAsync` disposes the write gate under writers (`:129`);
`SharedStateRing.Open` skips `Create`'s validation — failure mode is out-of-bounds pointer access
(`:117`); `UnknownMember` detection substring-matches an exception message that is empty under
`UseSystemResourceKeys` (`PluginManifestReader.cs:364`); `PluginStopReason` duplicates
`DeviceStopReason` member-for-member, bridged by a throwing hand switch (`PluginContracts.cs:252`).

**settings-interop (4)** — the WinEvent callback resolves the process inline despite its own
remark forbidding it (`ForegroundWindowWatcher.cs:116`); `TryReadBrightness` returns the DC byte
while the comment says AC (`NativeBacklight.cs:65`); file-identity P/Invokes declared in
triplicate across `NativePackageSource`/`NativePathIdentity`/`NativeShellProcess`
(`NativePackageSource.cs:252`); orphaned docs from the removed `InstallCommand`
(`SettingsViewModel.cs:230`).

**shell-device (10)** — three private copies of the fire-and-forget observer
(`ControllerManager.cs:772`); unkeyed warn at publish rate on generation desync
(`DeviceCapabilityRouter.cs:433`); `_availability` grows without bound (`:469`); snapshots posted
outside the gate can arrive out of order (`:619`); `ScheduleStartFault` drops the exception
unlogged on three guard paths (`DeviceCoordinator.cs:1074`); silent no-op suspend/resume when
`_client` is null (`:382`); `DispatchPendingTail` reopens the lost-notification race one level
down (`DeviceHostClient.cs:496`); environment serialized to a Win32 block then parsed back
(`DeviceHostProcess.cs:228`); `_held` written and never read (`DeviceOemActionRouter.cs:219`);
the retry-refresh timer and its UI branch are dead (`DeviceOverlayBridge.cs:986`).

**shell-session (6)** — HidHide cleanup omits the notation bridging its own matcher was given
(`HidHideOwnership.cs:740`); `StartSteamDesktop` silent on a Steam-less machine
(`SessionModes.cs:325`); `KickTabBootSync` disposes the CTS a worker may still be reading
(`ShellSession.cs:1068`); network-indicator enable silently defers in desktop mode (`:1642`);
overlay-level cycling reimplemented beside its owner (`:1774`); `KickDownloadSort` duplicates the
tab manager's entire readiness-poll scaffold (`:1091`).

**tests (4)** — transport seam constructor untested (reconnect/backoff/refcount/generation all
uncovered) (`PersistentSteamUiTransport.cs:35`); four test-only fakes in the production assembly
(`ManagedControllerBackend.cs:85`); `DeviceCapabilityRouter`'s ~700-line stateful core untested
(`:29`); `MemoryPackageSource` duplicated three times across test projects
(`SteamGlyphCssTests.cs:246`); wall-clock race with 150 ms slack (`SteamUiHostTests.cs:240`).

**build-native (7)** — build.ps1's preflight duplicates build-viiper's probe with nothing tying
them (`build.ps1:52`); pinned commits hardcoded a second time
(`checkout-controller-dependency-sources.ps1:13`); `-SkipPrettier` skips provisioning but not the
asset check that needs it (`verify.ps1:45`); fresh-install driver path never offers the required
reboot (`WSGM.iss:357`); slot-rollback rename result discarded and unlogged (`:818`); the
watchdog's skip decision unlogged in the grace window (`SessionLauncher.cs:236`); notices file
points at a license file that does not exist (`THIRD-PARTY-NOTICES.txt:12`).

## Not carried

**Refuted by the adversarial pass (6)** — recorded so they are not re-found: the CEF master
switch and the two-stack duplication findings (both owned by Q16 by design), the
`SelectHardwareProfileAsync` identity guard, the slot-gate dispose ordering, the diagnostics catch
filter, and the glyph render-cache scale bucket. **Invalid Codex claims (4)**: 3889089480 (the
VIIPER header is tracked upstream), 3886589505 (the glyph service is state-free; policy is
resolved in Shell), 3890157969 (CodeQL false positive — the harness value is JSON-encoded),
3887865286 (the discontinuity claim misread the coalescing contract).

## Coverage

Every bucket returned a coverage statement (in the workflow journal): core-cef 11, core-perf 14,
core-other 9, shell-device 20, shell-session 9, input-overlay 15, settings-interop 9, sdk 8,
devicehost-plugin 9, devicelab 14, tests 8, build-native 10, js-assets 15 findings. Attended
device-only paths (shell takeover, live hardware mutation, real Steam input) were reviewed as
source only; their gates stay in the tracker.
