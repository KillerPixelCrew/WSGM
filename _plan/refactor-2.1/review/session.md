# Session domain review: startup, lifetime, transitions, shutdown, Explorer/tray recovery

Reviewer scope: `src/WSGM/Program.cs`, `App.axaml(.cs)`, `Shell/ShellSession*.cs` (9 files, 4,894 lines),
`Shell/SessionModes.cs`, `Shell/GameModeEntryServices.cs`, `Shell/GameModeEntryTransaction.cs`,
`Shell/DesktopReturnSequence.cs`, `Shell/GameModeReturnRecovery.cs`, `Shell/BootTakeoverCancellation.cs`,
`Shell/ExplorerReadiness.cs`, `Shell/DesktopActionAdmission.cs`, `Shell/TrayHost.cs`, `Shell/DesktopTray.cs`,
`Shell/SessionActivation.cs`, `Shell/SettingsActivation.cs`, `Shell/SteamMonitor.cs`, `Shell/SteamExitPolicy.cs`,
`Shell/UiThread.cs`, `Shell/BootSplash.cs`, `Shell/BootSplashWindow.axaml(.cs)`, `Core/ApplicationShutdown.cs`,
`Core/Explorer{Control,DesktopHost,ShellAnchor,ShellPolicy,ExitPolicy}.cs`, `Core/TrayProtocol.cs`,
`Core/WindowFinder.cs`, `Core/Steam.cs`, `Core/UpdateExitWatcher.cs` (traced, crosses the boundary),
`Interop/MessageWindow.cs`, `Interop/NativeShellProcess*.cs`, `Interop/ParentProcessStart.cs`, and their tests
(`App/ModeSelectionTests`, `Core/ApplicationShutdownTests`, `Core/ExplorerShellPolicyTests`, `Core/TrayProtocolTests`,
`Core/WindowFinderTests`, `Core/UpdateExitWatcherTests`, `Shell/{SessionModes,GameModeEntryTransaction,
DesktopReturnSequence,BootTakeoverCancellation,ExplorerReadiness,DesktopActionAdmission,SteamExitPolicy,
SystemPowerTransition,DeviceIntegrationOff,ShellAnchorDisposal}Tests`). Baseline HEAD `1329813f`. Read-only.

Ledger coverage: this domain was audited by Claude units U04A-LFA, U04B-LFA and U05-LFB (plan tasks H01/H02). The Codex
audits (A01, A02, A02S01) and the seven admitted batches contain nothing in this domain. U04B-LFA-013..049 bodies are
missing; this review's NEW findings are the independent closure for the session/Explorer part of that gap.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "ShellSession spans nine files and 4,894 lines" (plan, Source diagnosis) | accurate | 196+303+108+896+363+282+883+675+1188 = 4,894 | none |
| C2 | "StartOnUiThread constructs approximately 40 services in one UI dispatch" | partially | `ShellSession.cs:347-456` plus the five `Start*` methods create about 50 owners (message/display windows, AutoTDP, performance stack, transport and gate loop, monitor, modes, keep-awake, audio, radio, drives, formats, storage, artwork, themes, sounds, animations, settings, library, overlay sources, tray icon, two activation receivers, Steam UI host and its backends, volume buttons, standby guard, update monitor, display mute, startup watcher, config watcher), and later tray/splash/card services | "about 50, created in one UI dispatch with no per-owner isolation" |
| C3 | "Shutdown stops device/GPU providers before the Steam UI host" | accurate | device `ShellSession.Shutdown.cs:139-164`, GPU `:180-191`, Steam UI host `:355-371` | none; see SESSION-020 for the simpler fix |
| C4 | "never joins `_devicePowerWork`" | accurate | chain built at `ShellSession.Power.cs:208-209`; no reference in `ShellSession.Shutdown.cs` | also unjoined: reload's `ApplyCommonPluginConfigAsync` (`ShellSession.Config.cs:211`), `ApplyDeviceConfigAndTargetAsync` (`:222`), `RestoreSteamUiAfterBigPictureAsync` (`ShellSession.SteamUi.cs:240`) |
| C5 | "Reload applies about 30 consumers in one callback after lenient `ConfigStore.Load`" | accurate | `ShellSession.Config.cs:110-157`; `ConfigStore.cs:122-139` returns `new AppConfig()` on any exception, including a transient sharing violation | none (U05-LFB-002, U05-LFB-018 confirmed) |
| C6 | "`ApplicationShutdownRequest` ties its process state to Avalonia" | accurate | static `_reason` `ApplicationShutdown.cs:30`; `ShutdownLifetime` reads `Application.Current` `:57-64` | none |
| C7 | `Program.Main` row: route early commands before normal bootstrap | accurate as preservation | anchor/probe/restore/unregister precede `Log.Init` (`Program.cs:74-99`) | keep exact order; one-shots stay after `Log.Init` as the guide requires |
| C8 | `ApplicationRuntime`: one startup/shutdown task, sticky startup failure | accurate target, current defect | startup failure is not sticky: `App.axaml.cs:94` calls `Shutdown(1)`, then `:143` calls `Shutdown(ExitCodeFor(outcome))`, which is 0 after a clean cleanup | see SESSION-002 |
| C9 | `SessionTransitions` uses "`SessionActionRunner`" | inaccurate | no such type exists; the action runner is `PluginActionSequence` + `PluginHostActionInvoker` (`ShellSession.Modes.cs:560-563`, `:630-631`) | reuse `PluginActionSequence`; do not add `SessionActionRunner` |
| C10 | `SessionPowerQueue`: ordered admission, join and cancellation, stale-suspend rule kept | accurate | `ShellSession.Power.cs:113-125`, `:165-253` | extract as-is plus one tracked tail task; no new states |
| C11 | `SessionConfigReloader`: "modes receive configuration directly" | accurate | today `SessionModes.ApplyConfig` is reached only through `OverlayController.cs:385` | U05-LFB-028 |
| C12 | Essentials: message window, desktop recovery host; tray window failure "returns to Desktop" | partially | true for a desktop-to-game transition (commit throws, `GameModeEntryTransaction.cs:267`, recovery runs) and for boot takeover (`ShellSession.Modes.cs:197-283`); direct game-mode boot instead fails startup and exits the process (`ShellSession.cs:450-451`, `ShellSession.Modes.cs:65-66`, `App.axaml.cs:91-95`) | direct boot must also fall back to resident Desktop |
| C13 | "Optional activation receivers ... degrade independently" | accurate target, current defect | `SettingsActivation` ctor throws (`SettingsActivation.cs:21-36`) inside `StartOverlay` and ends the session | U05-LFB-003 |
| C14 | Shutdown "named typed steps, fixed order, one budget, independent error collection" | accurate | independent collection already exists (`ShellSession.Shutdown.cs:46`, `RecordShutdownFailure`) | keep; collapse the 600 lines of try/catch/finally into one step helper (SESSION-030) |
| C15 | B3 phase cutoffs (preliminary min(500 ms,10%), 30/50/70/90%) | over-engineered | the concrete defects are C4, U05-LFB-020 and the pre-safety awaits; current order already runs device safety before transition waits (`ShellSession.Shutdown.cs:116-191` vs `:200-232`) | replace with safety-first ordering under the single deadline (section 3, R1) |
| C16 | Order "cancel/join reload, boot, power and transition work" before AutoTDP | partially | joining unrelated transition/boot work before safety steps is what B3 then has to time-box; only the power queue conflicts with device stop | join only the power queue before device stop; transitions after device/common/GPU, as today |
| C17 | "remove generation host patches before closing transport" | accurate | `ShellSession.Shutdown.cs:358-392` | keep; close host command admission at T0 (SESSION-020) |
| C18 | "destroy MessageWindow after its last subscription" | accurate defect | window disposed at `ShellSession.Shutdown.cs:752-756` (via `:264`), drive manager that registered volume notifications disposed later at `:598-609` on a pool thread | U05-LFB-014 |
| C19 | Reason "re-read before each not-yet-started step" | accurate target | reason consumed once (`App.axaml.cs:109`), later requests cancelled (`:103-107`) | U04A-LFA-020; implement as a field read, not a phase engine |
| C20 | Initial deadlines 15/10/5/20 s | accurate | `ApplicationShutdown.cs:90-99` | none |
| C21 | "Startup failure **remains** exit code 1 even after clean cleanup" | inaccurate as a description of today | `App.axaml.cs:141-144` overwrites 1 with 0 | SESSION-002 |
| C22 | "Only successfully started, clean exit 0 resets the crash-loop record" | accurate target, current defect | `Program.cs:236` resets unconditionally in Shell mode | U04A-LFA-013 |
| C23 | "Update never emits normal Big Picture exit" | accurate | `ShellSession.Shutdown.cs:821-824` | none |
| C24 | "Unverified tray retirement retains the anchor and forbids competing Explorer" | partially | anchor is retained when `desktopVerified` is false (`:283-297`), but `trayRetired` is true even when `DestroyWindow` failed (`TrayHost.cs:89-97`, `ShellSession.Shutdown.cs:777-782`) | U05-LFB-015 |
| C25 | Cross-version event/mutex/anchor names unchanged | accurate requirement | `Local\WSGM.Shell` `Program.cs:710`; `Local\WSGM.Activate` `SessionActivation.cs:13`; exit events `UpdateExitWatcher.cs:35-46`; anchor names `ExplorerShellAnchor.cs:22,766-769` duplicated as a literal in `WSGM.Setup/Engine/WindowsSetup.cs:50` | keep values; share one linked constant file (SESSION-047) |
| C26 | Sync/async scheduler launch tri-state; Unknown suppresses competing launches | accurate | `Steam.cs:296-308` falls back to `AppLauncher.Start` after a bool; `ExplorerControl.cs:178-184` falls back to an elevated start | U04B-LFA-001 |
| C27 | `StopFailedChildAsync` must release handles when writer flush fails | accurate | `ExplorerShellAnchor.cs:747-754` unguarded `writer.DisposeAsync()` | U04B-LFA-005 |
| C28 | Explorer observation uses limited-query image identity, not `MainModule` | accurate | `ExplorerControl.cs:82`, `:222`; `NativeShellProcess.Inspect` already exists | U04B-LFA-010 |
| C29 | High-integrity shell owners stay refused | accurate | `ExplorerShellPolicy.cs:71-76` | none |
| C30 | Tray relay "checks sender HWND/PID ... refuse relays into a higher-integrity target when sender identity cannot be established" | partially, risky | WM_COPYDATA delivered by shell32 carries no verifiable sender process; the identity can essentially never be established, so the rule degrades to "never relay to a High-IL target", which breaks clicking an elevated tray app (the doc names Handheld Companion menus, `TrayHost.cs:441-447`) | open question Q2 |
| C31 | Native windows use "HWND user data/owned callback registrations rather than replaceable `_instance`" | over-engineered | the defect is the shared `MessageWindow.Create()` singleton disposed by one holder (`MessageWindow.cs:189-203`, `:107`), not the static dispatch slot | one owned instance, UI-thread affinity, keep the static slot (R4) |
| C32 | "All long-running work is tracked and joined" | over-broad | about 15 fire-and-forget sites in this domain (listed in SESSION-019) | close owner admission; join only work that holds safety resources (R3) |
| C33 | `Every factory constructs inert objects. Explicit StartAsync` + 5-state `SessionLifetime` with owner registration | over-engineered and self-contradictory | the plan forbids a generic resource coordinator yet asks owners to "register in SessionLifetime"; current `_startupTask ??=` (`ShellSession.cs:247-251`) is already the single start task | two tasks (start, stop) and an admission flag; no registry (R2) |
| C34 | Preserve `GameModeEntryTransaction` | accurate | `GameModeEntryTransaction.cs` | keep it; collapse the three interfaces around it (SESSION-022) |
| C35 | H02 step 5: "splash arm-at-Steam-request/boot-only dismissal" | accurate | `BootSplash.cs:109` arms timeout at Show; `ShellSession.Modes.cs:84` dismisses any splash on overlay open | U05-LFB-010/011 |
| C36 | Steam UI host "holds roughly 30 backend dependencies" | accurate | 27 positional arguments at `ShellSession.cs:1042-1093` | Steam UI domain |

## 2. Findings

Severity scale: critical, high, medium, low, nit. "Covered" cites the ledger ID, confirmed against current code unless
stated. NEW findings are not in the ledger.

### Startup, runtime and process exit

**SESSION-001 (medium) covered U04A-LFA-017.** `Program.Mode`, `ServiceBoot`, `DesktopResident` are public mutable
statics (`Program.cs:42-52`) read by `App` (`App.axaml.cs:51-58`) and `Panic` (`Program.cs:738,774`). Confirmed.
Recommendation: immutable `StartupOptions` passed to `App` and the runtime.

**SESSION-002 (medium) NEW, related U04A-LFA-013.** A startup failure ends with exit code 0. `ObserveSessionStartupAsync`
calls `desktop.Shutdown(1)` (`App.axaml.cs:94`); `OnShutdownRequested` cancels, runs cleanup and then calls
`desktop.Shutdown(ExitCodeFor(outcome))` (`:141-144`), which is 0 when cleanup was clean. The re-entered handler then
reports the installer handoff a second time (`:110-115`, nit). Program then resets the crash-loop record because its reset
is unconditional (`Program.cs:226-237`). A shell that fails startup every sign-in therefore never trips the breaker.
Recommendation: `ApplicationRuntime` keeps a sticky `StartupFailed`; exit code is `max(startupFailed ? 1 : 0,
ExitCodeFor(outcome))`; the crash-loop reset runs only when started and exit code is 0; report the handoff once.

**SESSION-003 (medium) NEW, PLAUSIBLE.** `UpdateExitWatcher.Start` runs in every mode (`Program.cs:211-214`). The exit
events are manual-reset (`UpdateExitWatcher.cs` `CreateEventW(..., true, false, ...)`), so a Settings or overlay-test
process also wakes, runs `Steam.StopForUpdate` and, having no session, publishes `ExitForUpdate.Completed` as Clean at
once (`App.axaml.cs:110-115`). Setup latches completion with `completed |=` (`WSGM.Setup/Engine/WindowsSetup.cs:255-276`),
so a Settings window open during an update reports `Completed` for a shell that later times out. Recommendation: start
the update/uninstall watchers and publish completion only in Shell mode; Settings mode exits on the same event without
Steam pre-stop or handoff.

**SESSION-004 (low) NEW.** The update pre-stop blocks the UI thread. `RequestInstallerExit` posts
`RunInstallerExitRequest(..., Steam.StopForUpdate, ...)` to the dispatcher (`Program.cs:511-519`); `StopForUpdate`
sleeps in 250 ms steps for up to 5 s and then stops launch wrappers (`Steam.cs:382-447`). Meanwhile tray WM_COPYDATA,
session-change and power messages and the boot splash are not pumped. Recommendation: run the pre-stop on the watcher
thread, then post only the shutdown request.

**SESSION-005 (low) covered U04A-LFA-015, PV10-010.** `--restore-shell` waits up to 45 s for every WSGM process, Settings
included (`UpdateExitWatcher.cs` `RequestResidentShellExit`, `Program.cs:282`). Confirmed. Recommendation: wait on the
shell mutex `Local\WSGM.Shell` instead of process names.

**SESSION-006 (low) covered U04A-LFA-018.** `ApplyLogVerbosity` guards a property read that cannot throw
(`Program.cs:694-702`). Confirmed. Remove the try/catch.

**SESSION-007 (low) NEW, PLAUSIBLE, needs a framework check.** `App.OnShutdownRequested` always sets `Cancel = true`
for a live session (`App.axaml.cs:118`). If Avalonia 12's Win32 lifetime raises `ShutdownRequested` from
`WM_QUERYENDSESSION`, a logoff arrives with reason Normal (only the later `WTS_SESSION_LOGOFF` sets SessionEnd,
`ShellSession.Shutdown.cs:16-26`), so the Normal path can exit Big Picture and restore Explorer during sign-out, and the
cancel can surface Windows' "an app is preventing sign-out" screen. Recommendation: verify against the pinned Avalonia
source; if confirmed, map a session-ending shutdown request to SessionEnd before cleanup.

**SESSION-008 (nit) NEW.** `ShellSession.DisposeAsync` (`ShellSession.cs:237-243`) builds its own Normal budget and is
called by nothing; it is the "second entry path" of U05-LFB-029. Delete it.

**SESSION-009 (low) covered U04A-LFA-006, PV10-005.** `MainAsync` and `App.OnShutdownRequested` have no behavioral tests;
only `DecideMode`, `IsServiceBoot` and `RunInstallerExitRequest` are tested (`App/ModeSelectionTests.cs`,
`Core/ApplicationShutdownTests.cs:233-266`). Confirmed.

### Session composition and ownership

**SESSION-010 (high) covered U05-LFB-004, U05-LFB-005.** `ShellSession` is a 4,894-line partial aggregate with about 75
fields and drives process statics (`SteamUiTransportSession.SetEnabled/Attach` `ShellSession.cs:225,599`,
`SteamInputShim.SetEnabled` `:226`, `SettingsPluginActions.Publish/Withdraw`, `SteamUiReadiness`, `LibraryBadges`,
`GameModeReturnRecovery.Gate`). Confirmed. Recommendation in section 4.

**SESSION-011 (medium) covered U05-LFB-003, PV11-003.** No per-owner failure isolation: any throw in `StartOnUiThread`
(`ShellSession.cs:347-456`) ends the process. Confirmed for `SettingsActivation`, `DesktopTray` asset load,
`AudioManager.Start`, `SteamUiSessionHost` construction.

**SESSION-012 (medium) NEW.** Two sources of truth for the current mode. The session tracks `_inGameMode`
(`ShellSession.cs:129`, `SetInGameMode` `ShellSession.Modes.cs:445-449`), but the OEM desktop/game toggle
(`ShellSession.cs:1010`), overlay swipes and layout (`OverlayController.cs:508,699,748,924,1120`,
`OverlayController.Gestures.cs:53`, `.Power.cs:184`, `.SteamExit.cs:62`) and Settings (`SettingsViewModel.System.cs:276`)
probe `ExplorerControl.IsDesktopShellRunning()` live. They disagree during transitions and after a failed or degraded
return (Explorer unresponsive, `_inGameMode` false): the OEM toggle then starts a Game Mode entry from a session that
believes it is on the desktop. Each probe also opens processes and enumerates modules on the UI thread (U04B-LFA-010).
Recommendation: one `SessionMode` query owned by `SessionTransitions` (Game, Desktop, Transitioning); the live probe stays
only inside Explorer exit/restore and the tray-create guard.

**SESSION-013 (medium) covered U05-LFB-019.** Session fields written from worker threads without synchronization, e.g.
`EnableDeviceIntegrationAsync` mutates the live `_config` from `Task.Run` (`ShellSession.Actions.cs:110-118`), library
`updateSettings` writes `_config.GameLibrary` from the caller thread (`ShellSession.cs:829-835`),
`ShellGameModeEntryServices.PersistPendingReturnAsync` writes `_pendingReturnLayout` from the pool
(`ShellSession.Modes.cs:820-833`). Confirmed. Recommendation: only the reloader replaces `_config`; writers commit to the
store and let the reload publish.

**SESSION-014 (low) covered U05-LFB-025, PV11-020.** Dead or redundant wiring: `_commonPluginStartup is not null` is
always true (`ShellSession.cs:313`); `_drives.CardWatcher = _cardAcfWatcher` assigns null before card services exist
(`:712`, `:714`); `_overlayTestOnly ? null : _resolutions` inside a `!_overlayTestOnly` block (`:1057`, `:1072`,
`:1087`); `_overlay` disposed twice (`ShellSession.Shutdown.cs:76` and `:678-682`). Confirmed.

**SESSION-015 (nit) NEW.** `QueueDesktopActions` attaches a `ContinueWith(OnlyOnFaulted)` that rethrows on the dispatcher
(`ShellSession.Modes.cs:579-587`), but `RunDesktopActionsAsync` catches every exception (`:642-648`). The continuation is
dead mechanism. Remove it (related U05-LFB-027).

**SESSION-016 (nit) NEW.** Pending-return layout has two sources of truth: memory `_pendingReturnLayout` is set before the
durable write (`ShellSession.Modes.cs:824-831`) and preferred over the record on return (`:857-859`). Read the record
only.

### Configuration reload

**SESSION-017 (high) covered U05-LFB-002.** Reload turns any load failure into live defaults
(`ShellSession.Config.cs:109` -> `ConfigStore.cs:122-139`). A transient lock during a Settings save switches Device
Integration off (AutoTDP stops and restores, device config applies "disabled"), retracts all CEF surfaces and drops the
master switch, and `PreserveCorruptFile` quarantines a readable file. Confirmed. Recommendation: reload keeps the last
good snapshot on any non-Loaded outcome (depends on the Config domain's read outcome).

**SESSION-018 (medium) covered U05-LFB-018.** About 30 apply steps in one dispatcher callback with no per-step isolation
(`ShellSession.Config.cs:110-157`); a throw skips the rest and escapes to the dispatcher. Confirmed. Recommendation: one
explicit ordered method with a `TryApply(name, action)` helper; no subscriber registry.

### Power queue and untracked work

**SESSION-019 (medium) covered U05-LFB-001, PV11-001; scope extended NEW.** Shutdown does not join `_devicePowerWork`
(C4). Additional unowned work in this domain: `NotifyPluginModeAsync` (4 sites), `Task.Run(OtherManagers.ReapplyAtStart)`
(`ShellSession.cs:398`), `_sounds.RefreshAsync` (`:742`), reload `ApplyCommonPluginConfigAsync`
(`ShellSession.Config.cs:211`), `ApplyDeviceConfigAndTargetAsync` (`:222`), `ApplyPerformanceConfig` (`:267`), two
`Task.Run` in `ApplyCefMasterSwitch` (`ShellSession.SteamUi.cs:466,515`), `ApplySteamInputManagement`
(`ShellSession.Config.cs:63`), `KickTabBootSync`, `RestoreSteamUiAfterBigPictureAsync`, `QueueDesktopActions`,
`ReleaseAbandonedPackageExemptions`, `ObserveUiCaptureClaimAsync`, `RepairAfterResume` RTSS refresh,
`ExplorerControl.StartExplorer`'s `Task.Run(VerifyAndRepairElevation)` (U04B-LFA-006). Recommendation: owners refuse
calls once stopping (admission), so late fire-and-forget work fails fast; join only the power queue, the boot worker, the
transition and the transport gate loop.

**SESSION-020 (medium) covered U05-LFB-020.** The Steam UI host and its bridge commands outlive the device and GPU owners
during shutdown, and `_cefMasterGate.WaitAsync()` is unbounded (`ShellSession.Shutdown.cs:355`). Confirmed.
Recommendation: close the host's command admission at T0 (one call), dispose it later in today's position; bound the
gate wait by the deadline.

**SESSION-021 (low) covered U05-LFB-009.** `KickTabBootSync` cancels a source that `RunTabBootSyncAsync`'s `finally` may
already have disposed, and the last source is disposed only at shutdown (`ShellSession.SteamUi.cs:316-353`). Confirmed.

### Transitions

**SESSION-022 (medium) NEW.** Three interfaces and two forwarding adapters wrap one transaction:
`IGameModeEntryServices` (14 members, `GameModeEntryServices.cs:17-88`), `IGameModeEntryBackend` (15 members,
`GameModeEntryTransaction.cs:36-110`), `SessionModesEntryBackend` (pure forwarder with null fallbacks,
`GameModeEntryServices.cs:95-228`), `IDesktopReturnBackend` + `SessionModes.DesktopReturnBackend`
(`DesktopReturnSequence.cs:8-17`, `SessionModes.cs:826-927`). The null fallbacks ("This session cannot change displays",
`Task.Run(DisplayLayouts.Observe)`) are unreachable: entry only runs when `_desktopHost` exists, which is exactly when
`GameModeEntryServices` is set (`ShellSession.cs:641-649`, `SessionModes.cs:444-449`). Default interface methods
(`RestorePendingReturnAsync` in both interfaces) add a third layer. Recommendation: one `IGameModeEntryBackend`
implemented by one session-side class; `DesktopReturnSequence` keeps its small interface implemented by the same class;
delete `IGameModeEntryServices` and `SessionModesEntryBackend`.

**SESSION-023 (low) NEW.** Failed recovery produces the wrong, duplicated warning. `RecoverDesktopAsync` throws
`InvalidOperationException(ExplorerDesktopPendingWarning)` (`GameModeEntryTransaction.cs:313-322`). Called from the
`catch (OperationCanceledException)` or `catch (Exception)` handlers (`:271-281`) the throw escapes `RunAsync`, and
`SessionModes.EnterGameMode` shows `ExplorerExitFailedWarning` instead (`SessionModes.cs:486-491`); from the normal path
it is caught and rewrapped as "Game Mode entry failed: Windows Explorer did not finish starting". `ReturnToDesktopAsync`
has already shown `ExplorerDesktopPendingWarning` itself (`SessionModes.cs:378-386`). Recommendation: `RecoverAsync`
returns bool; the result carries `DesktopPending` and no exception crosses the handlers.

**SESSION-024 (low) covered U05-LFB-007.** A pre-change refusal (pending record not restorable) returns Failed without
recovery and leaves the Steam monitor paused (`GameModeEntryTransaction.cs:143-147`, `SessionModes.cs:466`). Confirmed.

**SESSION-025 (low) covered U05-LFB-008.** `_entryCancellation` is a plain field written by the worker and read on the UI
thread; `_desktopRequested` can be lost or fire later (`SessionModes.cs:322-327`, `:492-508`). Confirmed.
Recommendation: one atomic entry-state field, no extra flags.

**SESSION-026 (low) covered U05-LFB-028.** `SessionModes` config arrives only through `OverlayController.ApplyConfig`
(`OverlayController.cs:385`). Confirmed.

**SESSION-027 (low) covered U05-LFB-017.** `GameModeReturnRecovery` reports complete while it leaves the record when
Explorer is not up (`GameModeReturnRecovery.cs:92-101`); shutdown then fingerprints and clears it a second way
(`ShellSession.Shutdown.cs:846-872`, `GameModeReturnRecovery.cs:28-42`). The two clear blocks are identical. The class
is a static with a static `SemaphoreSlim` Gate and `ConfigStore`/`DisplayLayouts` hard-wired; `RestorePendingAsync`
returns on cancellation while the gated work continues (`:44-50`). Recommendation: instance owner returning
`{Restored, Pending, Unreadable}`; one clear path keyed by the fingerprint the restore read.

**SESSION-028 (nit) covered U05-LFB-026.** Orphaned summary stacked on `SteamUiPrepareTimeout`
(`SessionModes.cs:60-69`).

**SESSION-029 (low) covered U05-LFB-027.** Desktop actions build their own `PluginActionSequence` and swallow every
cancellation (`ShellSession.Modes.cs:630-648`); `ShellGameModeEntryServices.ReadLaunch` and `RunDesktopActionsAsync`
re-read config with the lenient loader (`:605`, `:753`). Confirmed.

**SESSION-063 (low) NEW.** `SessionModes` gets eight collaborators by settable properties after construction
(`PrepareSteamUiForBigPictureAsync`, `PrepareSteamUiForDesktopAsync`, `SteamUiBigPictureRequestSettled`,
`GameModeEntryServices`, `GameModeEntrySettled`, `DesktopReady`, `IsGameMode`, `SessionModes.cs:142-171`) plus public
events, and a preview-only public ctor used by `SettingsWindow.axaml.cs:259`. Recommendation: constructor-inject one
hooks object; a null host means preview.

### Shutdown

**SESSION-030 (medium) NEW.** `ShellSession.ShutdownAsync` is 600 lines of repeated
`try { ... } catch (Exception ex) when (...) { RecordShutdownFailure(...) } finally { _x = null; }` blocks
(`ShellSession.Shutdown.cs:92-611`) and mixes threads: after `await _profileFanOut.DisposeAsync().ConfigureAwait(false)`
(`:96`) the rest runs on the pool, so UI-affine disposals (`_steamGraphics`, `_chordMirror`, `_audio`, `_radios`,
`_themes`, `_drives` with its MessageWindow registration) run off the UI thread while `DisposeUiOwnedSessionResources`
is marshalled. Recommendation: an ordered list of named steps executed by one `RunStepAsync(name, step)` helper; steps
tagged UI run through the dispatcher. Removes about 350 lines with no behavior change.

**SESSION-031 (low) covered U05-LFB-029, PV11-024.** `_disposed` check-then-set is not atomic and `DisposeAsync` is a
second entry (`ShellSession.Shutdown.cs:33-39`, SESSION-008). Confirmed. Recommendation: `_shutdown ??= RunShutdownAsync()`
on the UI thread.

**SESSION-032 (low) covered U04A-LFA-020, PV10-017.** Reason escalation during shutdown is dropped (`App.axaml.cs:103-107`).

**SESSION-033 (low) NEW.** The shutdown awaits `_startupTask` first (`ShellSession.Shutdown.cs:47-60`). Startup holds
`GameModeReturnRecovery` with its own 15 s budget and `DeviceCoordinator.TryStartAsync`; with a 5 s SessionEnd budget
the outer deadline can expire before any cleanup. Both honour `_shutdownCancellation`, but the recovery restore only
checks cancellation between operations (`GameModeReturnRecovery.cs:145-167`). Recommendation: cancel, then await startup
bounded by the deadline; an unadopted coordinator is still disposed by startup's own `finally`.

### Explorer

**SESSION-034 (medium) covered U04B-LFA-008.** Explorer exit/restore/anchor orchestration has no seams and no behavioral
tests. Confirmed: `ExplorerControl` is static over `NativeMethods`/`Process`; `ExplorerDesktopHost` constructs
`DesktopAppLifecycle`, `ExplorerShellAnchor.StartAsync`, `UnelevatedLauncher` directly (`ExplorerDesktopHost.cs:21,132,408`).

**SESSION-035 (medium) covered U04B-LFA-010.** `IsDesktopShellRunning` uses `Process.MainModule` (`ExplorerControl.cs:82`),
which throws for a higher-integrity Explorer when WSGM is medium (then "no desktop", so `TrayHost.Create` would compete),
and enumerates modules on every UI call. `ExitExplorerAndWait` repeats it (`:222`). `NativeShellProcess.TryGetImagePath`
(`NativeShellProcess.cs:65-81`) already exists. Confirmed.

**SESSION-036 (low) covered U04B-LFA-011.** `ExitExplorerAndWait` can throw (`Process.GetProcessById` `ExplorerControl.cs:219`,
`MainModule` `:222`) despite its bool contract. Confirmed; the async wrapper catches it (`ExplorerDesktopHost.cs:251-258`).

**SESSION-037 (medium) NEW, related U04B-LFA-001/006.** Two Explorer launch policies. Terminal recovery (`--restore-shell`,
crash-loop disarm, Panic: `Program.cs:286,488,764`) uses `ExplorerControl.StartExplorer*`: ShellExecute from a possibly
elevated process, then a fire-and-forget or blocking "verify elevation, ask it to exit, restart via scheduler, else start
elevated" loop (`ExplorerControl.cs:91-190`). The session uses `ExplorerDesktopHost`'s anchor -> scheduler with tri-state
dispatch (`ExplorerDesktopHost.cs:327-442`). The first path ignores Unknown and can start a second shell. Recommendation:
one `ExplorerLauncher` with the tri-state scheduler; terminal paths, when elevated, go straight to the scheduler and only
on NotDispatched start Explorer directly. Keeps the "only 0x5B4 and WM_CLOSE" rule (no termination anywhere). Needs a
live check (R7).

**SESSION-038 (low) NEW.** Retired-shell state is a process static guarded by a static lock held for up to 30 s of
`Thread.Sleep` polling (`ExplorerControl.cs:23-25,200-303,311-338`). `WaitForRetiredShell` during a desktop return blocks
behind a concurrent exit loop, and `_retired` is shared across `ExplorerDesktopHost` instances. Recommendation: move
`_retired` and the exit loop into the `ExplorerDesktopHost` instance, already serialized by `_operationGate`.

**SESSION-039 (low) covered U04B-LFA-005.** `StopFailedChildAsync` leaks pipe/process handles and masks the original
exception when the writer flush throws (`ExplorerShellAnchor.cs:735-755`). Confirmed.

**SESSION-040 (low) NEW.** A failed `RestoreDesktopAsync` leaves `_desktopAppsSuspended = 1`
(`ExplorerDesktopHost.cs:310-319`), so startup apps matched by `DesktopAppLifecycle` stay suppressed
(`ShellSession.Modes.cs:681-685`) until a later successful restore. Recommendation: clear suspension when the stop is
undone, not only on success; or document the deliberate hold.

**SESSION-041 (nit) NEW.** `ExplorerDesktopHost.Dispose()` and `ExplorerShellAnchor.Dispose()` are sync-over-async
(`ExplorerDesktopHost.cs:67-70`, `ExplorerShellAnchor.cs:128-131`) and unused; `ThirdPartyModules` truncates the module
list at 16 entries with "..." (`ExplorerControl.cs:409-413`), an arbitrary cap on diagnostic content; stop event names
embed the session id inside the already session-scoped `Local\` namespace (`ExplorerShellAnchor.cs:766-769`; keep for
cross-version).

**SESSION-042 (low) covered U04B-LFA-007.** High-integrity Explorer is refused for takeover and classified failed;
undocumented. Confirmed (`ExplorerShellPolicy.cs:71-76`).

### Native windows and tray

**SESSION-043 (medium) covered U05-LFB-014, PV11-013.** `MessageWindow.Create()` returns a shared process singleton
(`MessageWindow.cs:189-203`) that `ShellSession`, `CardVolumeMonitor` (`ShellSession.SteamUi.cs:584`), `CardAcfWatcher`
(`CardAcfWatcher.cs:90`), `RemovableDriveManager` (`:156`) and `OverlayController` (`OverlayController.cs:215`) each
"create"; the session's `Dispose` destroys it for all (`MessageWindow.cs:87-108`) before the drive manager unregisters.
Confirmed.

**SESSION-044 (low) covered U05-LFB-013, PV11-012.** No exception guard in `MessageWindow.WndProc`
(`MessageWindow.cs:530-627`); `Marshal.PtrToStructure` and the posted lambdas run at the native boundary. Confirmed.
TrayHost has one (`TrayHost.cs:233-249`); `SettingsActivation.WindowProc` posts and returns (`:174-201`).

**SESSION-045 (medium) covered U05-LFB-015, PV11-014.** `TrayHost.Dispose` logs a failed `DestroyWindow` and still
reports retirement (`TrayHost.cs:89-97`); the session then restores Explorer (`ShellSession.Shutdown.cs:274-276`).
Confirmed. Recommendation: `Dispose` returns whether `IsWindow(_trayHwnd)` is false; enforce UI-thread affinity.

**SESSION-046 (low) NEW.** `Panic` calls `TrayHost.DestroyActive()` from whatever thread raised `UnhandledException`
(`Program.cs:744-751`); `DestroyWindow` fails cross-thread and `_instance` is read unsynchronized. When no anchor exists,
Panic then starts Explorer while WSGM's `Shell_TrayWnd` is still alive (`:753-765`). Short-lived because the process is
dying, but it is the coexistence the tray contract forbids. Recommendation: post the destroy to the dispatcher with a
short wait, or start Explorer only through the anchor/owner-loss path; keep "never kill Explorer".

**SESSION-047 (nit) NEW.** Cross-project protocol literals are duplicated: `Local\WSGM.ShellAnchor.RecoverySettled`
(`ExplorerShellAnchor.cs:22`, `WSGM.Setup/Engine/WindowsSetup.cs:50`), `Local\WSGM.ExitForUpdate`/`Uninstall`
(`UpdateExitWatcher.cs:35,38`, `WindowsSetup.cs:46-47`). Recommendation: one linked `SessionProtocolNames.cs`, values
unchanged.

**SESSION-048 (low) covered U05-LFB-016, PV11-015.** Tray clicks relay app-defined messages from High-IL WSGM to any HWND
the registrant named (`TrayHost.cs:400-495`). Confirmed. The plan's remedy needs a decision (Q2).

**SESSION-049 (low) NEW.** `TrayProtocol.TryParse` rejects any `cbSize > 968` (`TrayProtocol.cs:138`), dropping a valid
registration from a Windows build that extends NOTIFYICONDATA, although the parser reads only the v3 region. This is a
length cap that drops content. Recommendation: accept `cbSize >= 952` with `nid.Length >= cbSize`.

**SESSION-050 (nit) NEW.** `SessionActivation.Dispose` sets a non-volatile `_disposed` and unregisters with
`Unregister(null)`, which does not wait for an in-flight callback (`SessionActivation.cs:94-99`); the posted lambda checks
`_disposed` on the UI thread, so the effect is benign. Keep, but make `_disposed` volatile.

### WindowFinder, NativeShellProcess, Steam process control

**SESSION-051 (low) NEW.** `WindowFinder.WarnedSessionIdNames` is a static `HashSet` mutated from concurrent pool threads
(splash probe, Steam monitor, Big Picture close waits, boot takeover poll; `WindowFinder.cs:27,102`). Concurrent `Add`
can corrupt it and throw from the poll that "must not throw". Recommendation: lock it (or `ConcurrentDictionary`).

**SESSION-052 (nit) NEW.** Poll paths allocate per window: `EnumWindowsProc` and `ListWindowsProc` allocate `char[256]`
per enumerated window (`WindowFinder.cs:263,337`), and `NativeShellProcess.QueryImagePath` allocates a 64 KB buffer per
call (`NativeShellProcess.cs:150-162`) inside 200 ms restore polling (`ExplorerDesktopHost.cs:528-579`). Switcher titles
longer than 255 chars are truncated (arbitrary limit). Recommendation: `stackalloc`/pooled buffers;
`GetWindowTextLengthW` for titles.

**SESSION-053 (nit) NEW.** `WindowFinder`'s doc says it finds "the home app's main window" (`WindowFinder.cs:12-15`); it
is now session id, process queries, switcher listing and focus. `NativeShellProcess.TerminateProcess` is `internal`
(`NativeShellProcess.cs:243-245`), reachable from any code although only the owned anchor child may be terminated; make it
private. `StartupInfo`/`ProcessInformation` are declared twice (`NativeShellProcess.cs:341-371`,
`ParentProcessStart.cs:276-313`); `TokenLaunch` can use the shared ones.

**SESSION-054 (low) NEW, PLAUSIBLE.** `Steam.IsRunning` counts `steamwebhelper` (`Steam.cs:32,108`) although the class
documents that protocol callers must count only `steam.exe` (`:34-39`). `SessionModes.ExitBigPicture` (`SessionModes.cs:566`)
and `ExitBigPictureAndSettleAsync` (`:584`) send `steam://close/bigpicture` while only a lingering webhelper exists, which
starts Steam; `LaunchDesktop` reports "already running" (`Steam.cs:266-269`) and `LaunchBigPicture` skips the cold-start
argument path (`:248-253`). Recommendation: protocol decisions use the main process; the liveness monitor keeps both.

**SESSION-055 (low) related U04B-LFA-001.** `Steam.ColdStart` uses the bool scheduler wrapper and falls back to a
same-integrity launch (`Steam.cs:295-308`), a competing Steam start after Unknown. Confirmed.

**SESSION-056 (medium) NEW.** `Steam` is a static mixing installation discovery, process control, input shortcuts and
update stop (`Steam.cs`, 474 lines) and is the only port `SessionModes`, `BootSplash`, `SteamMonitor` and the boot
takeover have to Steam (`SessionModes.cs` 10 uses). Transitions cannot be tested without a live process table.
Recommendation in section 4 (instance `SteamSessionControl` for process control; installation discovery stays static).

### Splash

**SESSION-057 (low) covered U05-LFB-010/011.** Splash timeout armed at `Show` (`BootSplash.cs:52-58,109`); overlay open
dismisses an entry splash (`ShellSession.Modes.cs:84`). Confirmed.

**SESSION-058 (nit) covered U05-LFB-023.** Splash decode policy is 300 lines of comments and three budgets
(`BootSplashWindow.axaml.cs:36-109,385-505`); tests re-implement `TryLoadBitmap`. These are memory-safety bounds that
downscale rather than drop, so they do not violate the no-arbitrary-limits rule; `ImageHeader.TryReadBoundedSize`
refusing oversized declared dimensions does drop the element, and is a safety boundary. Keep, move the pure math to a
`SplashDecode` static, trim comments.

### Tests

**SESSION-059 (medium) covered U05-LFB-021.** `GameModeEntryTransactionTests`' fake `ReturnToDesktopAsync` applies the
layout, runs leave actions and clears the record itself (`GameModeEntryTransactionTests.cs:420-434`), then tests assert
those calls (`:125-127`, `:152`). Confirmed. Recommendation: tests run the real `DesktopReturnSequence` behind a fake
`IDesktopReturnBackend`.

**SESSION-060 (medium) covered U05-LFB-006, U05-LFB-024.** Transition and power state machines are tested only through
pure predicates (`SessionModesTests` 4 trivial cases; `SystemPowerTransitionTests` tests `IsStaleSuspend` only); no test
for `ShellSession.ShutdownAsync` order, `QueueDevicePowerTransition`, reload, `TrayHost`, `MessageWindow`, `SteamMonitor`,
`BootSplash`, `GameModeReturnRecovery`, `SessionActivation`, `SettingsActivation`.

**SESSION-061 (nit) covered U05-LFB-022.** `ShellAnchorDisposalTests` pins .NET pipe behavior, not the guard.

**SESSION-062 (nit) covered U05-LFB-030; NEW instances.** Getter/constant tests:
`ApplicationShutdownTests.BudgetsMatchFrozenShutdownAndUpdatePreStopDeadlines` (`:20-31`),
`SystemPowerTransitionTests.EveryResumeCodeWindowsCanSendIsADistinctValue` (`:132-140`),
`WindowFinderTests.WindowSnapshotRetainsItsPositionalRecordContract` (`:35`),
`DeviceIntegrationOffTests.TurningTheMasterOffKeepsTheChildPreferenceForNextTime` asserts a property it just set
(`:177-189`). `ApplicationShutdownTests` `Request`/`Consume` tests mutate the process-global reason (`:91-110`) and leak
state across tests. Recommendation: delete getter tests; the reason tests move to an `ApplicationRuntime` instance.

## 3. Plan refinements

Additions:

- A1. Fix SESSION-002 (sticky startup failure exit code and single handoff report) in the runtime batch; it is a real
  crash-loop defect the plan describes as already true.
- A2. Shell-mode-only installer watchers and completion (SESSION-003); pre-stop off the UI thread (SESSION-004).
- A3. One mode authority (SESSION-012); every consumer of `IsDesktopShellRunning` outside Explorer exit/restore and
  tray create switches to it.
- A4. Collapse the entry interfaces (SESSION-022) and make recovery result-based (SESSION-023).
- A5. One Explorer launcher with tri-state dispatch for terminal recovery paths too (SESSION-037); `_retired` becomes host
  instance state (SESSION-038).
- A6. Steam process control as an injected instance port; fix `IsRunning` semantics for protocol decisions (SESSION-054,
  SESSION-056).
- A7. Verify SESSION-007 against the pinned Avalonia 12 source before writing the shutdown batch.
- A8. `TrayProtocol` cbSize upper bound removed (SESSION-049); thread-safe `WindowFinder` warn set (SESSION-051).

Changes:

- R1. Replace B3's percentage cutoffs and preliminary drain with safety-first ordering under the one deadline. Order:
  (0) atomically record reason and sticky SessionEnd, close admission everywhere (UI intents, Steam host commands,
  reload, power queue, mode requests, profile fan-out), cancel the session token; (1) await the power queue tail and the
  startup task, each bounded by the deadline; (2) AutoTDP restore; (3) device shutdown (controller neutral/release,
  HidHide, plugin stop: the device owner handles its own busy lanes); (4) common plugins, GPU; (5) await transition, boot
  worker and gate loop; (6) tray retire and verify; (7) Explorer restore when reason allows and tray is gone; (8) Steam
  host and transport; (9) feature owners; (10) native providers; (11) MessageWindow last. Each step gets "remaining time
  to the deadline". The anchor already restores Explorer after an overrun (`ShellSession.Shutdown.cs:108-111`,
  `ExplorerShellAnchor.cs:634-665`), so no reservation for the desktop is needed. The B3 busy-owner table is honoured
  inside the owners (DeviceCoordinator's command serializer, GPU driver lane), not by a session phase engine. This removes
  four cutoffs, the 500 ms/10% drain, recomputation on escalation and "overdue phase" handling. Escalation then only
  tightens the deadline and sets sticky SessionEnd.
- R2. `SessionLifetime`: drop the 5-state machine and owner registration. `ShellSession` keeps `Task? _startup`,
  `Task? _shutdown` and a `_stopping` admission flag; shutdown is the fixed typed list from R1. "Inert factories" only
  where construction has side effects that a test must avoid (SteamMonitor timer, AudioManager, KeepAwake), not as a
  blanket rule.
- R3. "All long-running work tracked and joined" becomes: owners refuse calls once stopping; the session joins the power
  queue, boot worker, transition and transport gate loop only (SESSION-019).
- R4. Native windows: one `MessageWindow` instance owned by the process composition root and passed to consumers;
  `Create()` removed; UI-thread affinity enforced; static dispatch slot kept (one window per process); `WndProc`
  exception guard. No `GWLP_USERDATA`/`GCHandle` mechanism.
- R5. `SessionConfigReloader` is an explicit ordered method with a `TryApply(name, action)` helper and last-good
  retention; no subscriber registry, no generation beyond today's debounce generation.
- R6. `SessionTransitions` reuses `PluginActionSequence`; drop `SessionActionRunner` (C9).
- R7. The Explorer launcher unification (A5) changes how terminal recovery starts Explorer; gate it on one attended
  restore-shell and crash-loop pass on the notebook and the Claw (M01 rows).

Removals:

- `ShellSession.DisposeAsync`, `IGameModeEntryServices`, `SessionModesEntryBackend`, the `QueueDesktopActions`
  continuation, `ExplorerDesktopHost.Dispose()`/`ExplorerShellAnchor.Dispose()` sync wrappers, `ApplicationShutdownRequest`
  static, `Program.Mode/ServiceBoot/DesktopResident`, `ShellAnchorDisposalTests` premise test, getter tests (SESSION-062).

Plan mechanisms flagged as over-engineering (simplify rule) and their replacements:

| Mechanism | Why it is not justified | Simpler shape |
| --- | --- | --- |
| B3 phase cutoffs 30/50/70/90% and min(500 ms,10%) drain, with recomputation on escalation | No defect needs per-phase budgets once nothing unrelated is awaited before safety steps; the anchor covers Explorer after an overrun | R1 safety-first order, one deadline |
| `SessionLifetime` Created/Starting/Running/Stopping/Stopped + owner registration | Duplicates `_startupTask ??=`; registration is the generic coordinator the plan forbids | R2 two tasks and a flag |
| "Track and join all long-running work" | 15+ sites; joining them adds bookkeeping without safety value | R3 admission at owners, join four tasks |
| HWND user data / owned callback registrations | The defect is the shared singleton, not the static slot | R4 one owned instance |
| Tray relay sender-identity verification | Sender identity is not obtainable; would remove a working workflow | Q2 |
| Ordered reload "subscribers" | A registry where a method suffices | R5 |
| `SessionActionRunner` | Duplicates `PluginActionSequence` | R6 |

No-arbitrary-limits: SESSION-049 (tray cbSize cap), SESSION-052 (switcher title truncation), SESSION-041
(`ThirdPartyModules` 16 cap) are existing caps to remove. The plan's 200-entry picker pages and IR bounds are outside this
domain.

## 4. Target design

Owners (all in `src/WSGM` unless noted; no new project):

| Owner | Responsibility | Construction |
| --- | --- | --- |
| `Program` (`Program.cs`) | STA `Main`, early route, mode decision, mutex | static entry only |
| `StartupOptions` (`StartupOptions.cs`) | immutable parsed flags: Mode, ServiceBoot, DesktopResident, Activate, Verbose; `Decide`, `ArgumentValue` | `StartupOptions.Parse(args)` |
| `StartupCommands` (`StartupCommands.cs`) | restore-shell, unregister-shell, uninstall-restore, setup, export answers, elevated one-shots, crash-loop disarm, display-scale restore | static, ports passed as arguments where tests need them |
| `CrashLoopBreaker` (`Core/CrashLoopBreaker.cs`) | start record, loop detection, reset; instance over a directory and clock | `new CrashLoopBreaker(dir, clock)` |
| `ApplicationRuntime` (`Core/ApplicationRuntime.cs`) | reason with priority and sticky SessionEnd, one shutdown task, deadline, outcome, sticky startup failure, exit code, installer handoff once | created in `Program`, passed to `App` and the watcher callbacks |
| `App` (`App.axaml.cs`) | Avalonia glue only: creates session/settings window, forwards `ShutdownRequested` to the runtime | ctor `(AppConfig, StartupOptions, ApplicationRuntime)` |
| `ShellSession` (`Shell/ShellSession.cs`, `ShellSession.Composition.cs`, `ShellSession.Shutdown.cs`) | composition root: constructs owners, wires events, typed shutdown list (R1) | ctor `(AppConfig, StartupOptions, ApplicationRuntime, MessageWindow?)` |
| `SessionTransitions` (`Shell/SessionTransitions.cs`) | boot takeover, desktop resume, launch sequence, entry splash, desktop/wake actions, mode authority (Game/Desktop/Transitioning) | ctor with `SessionModes`, `ExplorerDesktopHost`, `ISteamSessionControl`, `PluginActionSequence`, splash factory |
| `SessionEntryBackend` (`Shell/SessionEntryBackend.cs`) | the one `IGameModeEntryBackend` and `IDesktopReturnBackend` implementation | owned by `SessionTransitions` |
| `SessionModes` (`Shell/SessionModes.cs`) | transition admission and the two transition workers; Steam start/focus/close | ctor-injected hooks; null host = preview |
| `SessionPowerQueue` (`Shell/SessionPowerQueue.cs`) | lock/unlock/suspend/resume edge queue, stale-suspend rule, tracked tail, `WhenIdleAsync` | ctor `(Func<DeviceCoordinator?>, CommonPluginManager?, clock, CancellationToken)` |
| `SessionConfigReloader` (`Shell/SessionConfigReloader.cs`) | watcher, debounce, load with outcome, last-good, ordered `TryApply` | ctor `(path, load, apply, dispatch)` |
| `GameModeReturnRecovery` (instance) | pending desktop return restore with tri-state outcome and one clear path | ctor with config store, display apply, audio factory |
| `ExplorerShellProbe` (`Core/ExplorerShellProbe.cs`) | `IsDesktopShellRunning` via limited image query; current-session check | static over `NativeShellProcess` |
| `ExplorerDesktopHost` (instance, ports) | prepare/exit/restore; owns retired-shell handle and exit loop; anchor/scheduler fallback | ctor `(IExplorerNative, anchor factory, IExplorerLauncher, DesktopAppLifecycle, sessionId)` |
| `ExplorerLauncher` (`Core/ExplorerLauncher.cs`) | one start policy, tri-state scheduler (U04B-LFA-001) | used by host and terminal recovery |
| `MessageWindow` | one instance per process, owned by the composition root, disposed last | `new MessageWindow()` on the UI thread |
| `TrayHost` | instance; `Retire()` returns verified bool | `TrayHost.TryCreate()` |
| `SteamInstallation` (static) | ExePath, InstallDirectory, library folders, RequiresElevatedShell | unchanged static (pure discovery) |
| `SteamSessionControl : ISteamSessionControl` | IsClientRunning (steam.exe), IsAnyRunning, Big Picture window, launch BP/desktop, exit BP, close, focus, StopForUpdate | instance, injected |
| `BigPictureShortcuts` (static) | `TrySend`, `ShortcutVirtualKey`, `BigPictureShortcut` enum | static |

Old symbol -> new owner, per dissolved or split file:

`Program.cs` (split):

| Old | New |
| --- | --- |
| `RunMode` | `StartupOptions.cs` (unchanged enum) |
| `Program.Mode`, `ServiceBoot`, `DesktopResident` | `StartupOptions` properties (statics deleted) |
| `UninstallHidHideUnverifiedExitCode` | `StartupCommands` |
| `_shellMutex`, `AcquireShellMutex` | `Program` (private, unchanged name `Local\WSGM.Shell`) |
| `Main`, `MainAsync` routing | `Program` |
| `RestoreShellAsync`, `RunOneShot`, `ApplySteamInputShim`, `RemoveSteamInputShim`, `RestoreSteamChordTemplate`, `RunSetup`, `ExportSetupAnswers`, `RestoreHidHideForUninstallAsync`, `DisarmCrashLoop`, `RestoreDisplayScalesBestEffort` | `StartupCommands` |
| `RequestRestoreShellExit`, `RequestInstallerExit`, `RunInstallerExitRequest` | `ApplicationRuntime` (pre-stop on watcher thread) |
| `DecideMode`, `ArgumentValue`, `IsServiceBoot`, `HasVerboseFlag` | `StartupOptions` |
| `ApplyLogVerbosity` | `Program` (try/catch removed) |
| `Panic` | `ApplicationRuntime.Panic` (reads options, posts tray destroy) |
| `BuildAvaloniaApp` | `Program` |
| `CrashLoopBreaker` (`MarkerPath`, `RecordStart`, `IsLooping`, `Reset`) | `Core/CrashLoopBreaker.cs` instance |
| unhandled/unobserved handlers, `UpdateExitWatcher.Start` call | `Program` (Shell mode only for installer watchers) |

`Core/ApplicationShutdown.cs` (dissolved):

| Old | New |
| --- | --- |
| `ApplicationShutdownReason`, `ApplicationShutdownOutcome` | `Core/ApplicationRuntime.cs` (unchanged values) |
| `ApplicationShutdownRequest._reason`, `Request`, `Consume`, `PriorityFor` | `ApplicationRuntime.RequestShutdown`, `Reason`, private priority |
| `ApplicationShutdownRequest.ShutdownLifetime` | `ApplicationRuntime` via injected `Action<int> shutdownLifetime` from `App` |
| `ApplicationShutdownCoordinator.ExitCodeFor`, `BudgetFor`, both `ShutdownAsync`, `ReportTimeout`, `ObserveLateCleanup`, `ObserveAsync` | `ApplicationRuntime` (clock/delay ports kept) |

`App.axaml.cs`: `_session`, `_sessionStopped`, `_shutdownInProgress`, `_shutdownOutcome`, `ObserveSessionStartupAsync`,
`OnShutdownRequested` -> `ApplicationRuntime` (App keeps `_session` rooting and a 5-line forwarding handler).

`Shell/ShellSession.Power.cs` (dissolved):

| Old | New |
| --- | --- |
| `SpuriousSuspendWindow`, `LongAgo`, `_devicePowerGate`, `_devicePowerWork`, `_deviceSuspended`, `_latestPowerTransition`, `_systemResumeTimestamp`, `_systemResumeWallTicks`, `IsStaleSuspend`, `QueueDevicePowerTransition`, `ApplyDevicePowerTransitionAsync`, `ApplyCommonPluginPowerAsync`, `PowerTransition` | `SessionPowerQueue` |
| `OnSessionLocked`, `OnSessionUnlocked`, `OnSystemSuspending`, `OnSystemResumed` | `SessionPowerQueue` handlers subscribed by `ShellSession` |
| `ResumeWasUnattended` + `QueueDesktopActions(false)` on resume | `SessionTransitions.OnResume` |
| `_lastResumeRepairTick`, `RepairAfterResume` | performance wiring (`PerformanceSessionWiring`, Performance domain) invoked on resume |
| `OnPowerSourceChanged` | direct subscription `DeviceCoordinator.OnPowerSourceChanged` in composition |

`Shell/ShellSession.Config.cs` (dissolved):

| Old | New |
| --- | --- |
| `_configDebounceGate`, `_configDebounce`, `_configReloadGeneration`, `_configWatcher`, `WatchConfig` (Reload, Debounce, Error re-arm) | `SessionConfigReloader` |
| reload apply body | `ShellSession.ApplyConfig(AppConfig)` ordered `TryApply` list, now including `_modes.ApplyConfig` |
| `CommitWsgmSetting` | Config domain commit helper (`ConfigStore` instance) |
| `ApplySteamInputManagement` | `SteamInputManagement` (Core) |
| `AutoKeepAwakeEnabled`, `DownloadMonitoringEnabled` | `KeepAwakeService` static policy |
| `ApplyDeviceConfig`, `ApplyDeviceConfigAndTargetAsync`, `ApplyCommonPluginConfigAsync` | composition apply step; tasks tracked by owners' admission |
| `ApplyPerformanceConfig` | Performance domain wiring |
| `MutateProfilesAsync`, `MutateSimulatedProfilesAsync` | Profile domain store port |

`Shell/ShellSession.Modes.cs` (dissolved):

| Old | New |
| --- | --- |
| `InputDesktopWait`, `WaitForInputDesktopAsync` | `SessionTransitions` |
| `_desktopActionAdmission`, `_displayActionGate`, `QueueDesktopActions`, `RunDesktopActionsAsync`, `ActionSequence` | `SessionTransitions` (continuation removed) |
| `_bootTakeover`, `_bootWork`, `_tookOverFromExplorer`, `StartBootTakeover`, `RunBootTakeoverAsync`, `BootTakeoverResult` | `SessionTransitions` |
| `_gameModeEntryActive`, `_holdingEntrySplash`, `EnsureEntrySplash`, `ShowBootSplashIfEnabled`, `SwitchToDesktopFromSplash`, `BeginDesktopModeFromSplash`, `ResumePreservedDesktopAfterBootFailure`, `BeginDesktopModeAfterBootFailure` | `SessionTransitions` (splash ownership) |
| `NotifyPluginModeAsync`, `EnterGameModeSurfaces`, `SetInGameMode` | `SessionTransitions` (mode authority) |
| `StartDesktopSteamAsync`, `RunLaunchSequenceAsync`, `LaunchAppsAsync`, `IsAppAlreadyRunning`, `WatchStartupAppsAndConfig` | `SessionTransitions` |
| `SwitchToDesktopFromSteamAsync` | `SessionTransitions` (Steam power menu backend calls it) |
| `CreateArrivalWaiter`, `_pendingReturnLayout`, `ShellGameModeEntryServices` (all 14 members) | `SessionEntryBackend` (memory copy of pending layout removed) |

`Shell/GameModeEntryServices.cs` (dissolved): `IGameModeEntryServices` deleted; `SessionModesEntryBackend` members
`PrepareExplorerExitAsync`, `ExitExplorerAndWaitAsync`, `ReturnToDesktopAsync`, `RequestBigPictureAsync`,
`CommitGameModeAsync`, `ApplyDefaultPostureAsync` -> `SessionEntryBackend`; null fallbacks deleted.

`Shell/ShellSession.SteamUi.cs` (dissolved, owned by the Steam UI domain): transport gate, master switch, Big Picture
holds, tab boot sync, surface preferences, glyphs -> `SteamUiCoordinator`; `ApplyCardServices` -> card services owner
(storage domain). This domain only moves the calls and keeps the existing order.

`Shell/ShellSession.Performance.cs` (dissolved, Performance/AutoTDP domains): `OnForegroundApplicationChanged`,
`ApplyManualRefreshRate`, `ReadNativeQamPerfSupport`, `ReadPairedRefreshRates`, `ApplyRunningApplicationTargetAsync`,
OSD power status members, `AutoTdpActivity`, `TargetFrametimeMs`, `OnPerformanceStateForPairing`,
`ApplyRefreshPairing`, `_pairedFrameLimit`, `_pairedOperatingPoint`, `PerformanceEnabled`, `LiveCapabilityPublishers`,
`StartProfileFanOut` -> `PerformanceSessionWiring`; `ShouldRunAutoTdp` -> `DeviceIntegrationConfig.RunsAutoTdp`.

`Shell/ShellSession.Actions.cs` (dissolved): `ShowOnScreenKeyboardAsync`, `ToggleSteamSurfaceAsync`,
`SelectReplayTarget`, `RunUiActionAsync`, `CyclePerformanceOverlayLevelAsync`, `CyclePerformanceProfileAsync`,
`ObserveUiCaptureClaimAsync` -> `SessionCommands` (`Shell/SessionCommands.cs`); `ReadDevicePrerequisiteState`,
`EnableDeviceIntegrationAsync` -> `DevicePrerequisiteSource`; `ReadPluginActionOptions` -> `CommonPluginOverlaySource`.

`Shell/ShellSession.GameLibrary.cs` (dissolved, Library domain): `ReleaseAbandonedPackageExemptions` ->
`PackagedLauncherShortcut`; `ReadShortcutsAsync`, `ReadShortcutAsync`, `AddShortcutAsync` -> `SteamShortcutAccess`.

`Core/ExplorerControl.cs` (split):

| Old | New |
| --- | --- |
| `ExplorerPath`, `IsDesktopShellRunning`, `IsCurrentSessionWindow` | `ExplorerShellProbe` (limited image query) |
| `ExitExplorerMessage`, `WmClose`, `_retired`, `ExitGate`, `ExitExplorerAndWait`, `WaitForRetiredShell`, `RememberRetired`, `ExitedUncleanly`, `ThirdPartyModules`, `CloseWindowsOf`, `WaitForShellAbsence`, `IsWindowOwnedByProcess` | `ExplorerDesktopHost` (instance, through `IExplorerNative`) |
| `ElevationCheckTimeout`, `StartExplorer`, `StartExplorerAndVerify`, `StartExplorerCore`, `VerifyAndRepairElevation` | `ExplorerLauncher` |

`Core/Steam.cs` (split): `ProcessNames`, `MainProcessName`, `BigPictureWindowClass`, URLs, `UpdateGracefulExitBudget`,
`UpdateStopBudget`, `IsRunning`, `IsBigPictureVisible`, `FindBigPictureWindow`, `LaunchBigPicture`, `LaunchDesktop`,
`ColdStart`, `StopForUpdate` (both), `CurrentSessionProcesses` -> `SteamSessionControl`; `ExePath`, `InstallDirectory`,
`LibraryFoldersConfigPath`, `IsInstalled`, `RequiresElevatedShell`, `ResolveExePath`,
`CompatibilityLayerRequiresElevation`, `HasRunAsAdminCompatibilityLayer`, `TryReadLibraryFolders` ->
`SteamInstallation`; `BigPictureShortcut`, `TrySendBigPictureShortcut`, `ShortcutVirtualKey` -> `BigPictureShortcuts`;
`SessionModes.ExitBigPicture`, `ExitBigPictureAndSettleAsync`, `WaitForBigPictureToCloseAsync` move onto
`SteamSessionControl`.

Unchanged files kept as owners: `GameModeEntryTransaction.cs` (result-based recovery), `DesktopReturnSequence.cs`,
`BootTakeoverCancellation.cs`, `ExplorerReadiness.cs`, `DesktopActionAdmission.cs`, `SteamExitPolicy.cs`,
`ExplorerShellPolicy.cs`, `ExplorerExitPolicy.cs`, `ExplorerShellAnchor.cs` (handle fix), `TrayProtocol.cs`,
`NativeShellProcess*.cs`, `ParentProcessStart.cs`, `BootSplash*.cs`, `DesktopTray.cs`, `SessionActivation.cs`,
`SettingsActivation.cs`, `SteamMonitor.cs` (takes `ISteamSessionControl`), `UiThread.cs`.

Public API changes and every consumer that must change:

| Change | Consumers |
| --- | --- |
| `Program.Mode/ServiceBoot/DesktopResident` removed | `App.axaml.cs:51-58`; Panic |
| `ApplicationShutdownRequest`/`Coordinator` -> `ApplicationRuntime` | `Program.cs:502-519`, `App.axaml.cs`, `ShellSession.Shutdown.cs:24-25`, `ShellSession.cs:239-242,925`, `UpdateExitWatcher.cs` (`ApplicationShutdownReason` only, unchanged), `tests/.../ApplicationShutdownTests.cs` |
| `App` ctor gains options and runtime | `Program.BuildAvaloniaApp` |
| `ShellSession` ctor signature | `App.axaml.cs:57,64` |
| `SessionModes` ctor-injected hooks, `ExitBigPicture` static removed | `ShellSession`, `OverlayController.cs:212,281,385,727-768`, `.Apps.cs:67`, `.SteamExit.cs:42-65`, `SettingsWindow.axaml.cs:259`, `ShellSession.Shutdown.cs:823`, `SessionModesTests`, `GameModeEntryTransactionTests` |
| `ExplorerControl` split | `Program.cs:81,286,484-488,753-764`; `OverlayController.cs:508,699,748,924,1120`, `.Gestures.cs:53`, `.Power.cs:184`, `.SteamExit.cs:62`; `SettingsViewModel.System.cs:276`; `TrayHost.cs:122`; `GameModeReturnRecovery.cs:92`; `ShellSession.cs:275,417,1010`; `ExplorerShellAnchor.cs:684`; `ExplorerDesktopHost.cs` |
| Mode authority query replaces live probes (A3) | overlay and OEM sites above except `TrayHost.Create` and Explorer internals |
| `MessageWindow.Create()` removed | `ShellSession.cs:468`, `ShellSession.SteamUi.cs:584`, `CardAcfWatcher.cs:90`, `RemovableDriveManager.cs:156`, `OverlayController.cs:215`; the Settings-process test overlay creates its own instance |
| `TrayHost.DestroyActive` -> runtime-posted retire; `Dispose` -> `Retire()` | `Program.cs:746`, `ShellSession.Modes.cs:65`, `ShellSession.cs:1121`, `ShellSession.Shutdown.cs:779` |
| `Steam` split | `SessionModes.cs` (10), `ShellSession*.cs` (6), `BootSplash.cs:151`, `SteamMonitor.cs:80`, `OverlayController.Gestures.cs`, `SdFormatManager.cs` (12, installation only), `LibraryTabManager.cs`, `SteamInputShim.cs`, `SteamUiReadiness.cs`, `CardVolumeMonitor.cs`, `SteamAutostartService.cs`, `SteamArtwork.cs`, `SteamStorageBridge.cs`, `SettingsViewModel.Steam.cs`, `SteamGuideChordMirror.cs`, `SelfElevation.cs`, `BootManifestWriter.cs`, `Program.cs:365,516`; most only need `SteamInstallation` |
| Protocol name constants linked file | `ExplorerShellAnchor.cs`, `UpdateExitWatcher.cs`, `SessionActivation.cs`, `WSGM.Setup/Engine/WindowsSetup.cs` (values unchanged) |

## 5. Implementation batches

Each batch builds green and runs its narrow filter
(`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "<filter>"`). All tests use fakes, temp directories and
disposable names; none touch Explorer, Steam, the tray or the real config.

**SESSION-B1 Startup options, runtime and exit codes** (about 1,000 lines)
Files: `Program.cs`, new `StartupOptions.cs`, `StartupCommands.cs`, `Core/CrashLoopBreaker.cs`,
`Core/ApplicationRuntime.cs`; delete `Core/ApplicationShutdown.cs`; `App.axaml.cs`; `Shell/ShellSession.cs` (ctor,
`DisposeAsync` removal), `ShellSession.Shutdown.cs` (session-end request); tests.
Steps: parse `StartupOptions` once; move one-shots verbatim keeping order; runtime owns reason (priority, sticky
SessionEnd, escalation tightens the deadline), one shutdown task, sticky startup failure, exit code, single handoff;
crash-loop reset only when started and exit 0; installer watchers and handoff only in Shell mode; pre-stop on the
watcher thread; verify Avalonia's session-end path (A7) and map it to SessionEnd if needed.
Depends on: nothing (uses current `ConfigStore`).
Tests: `StartupOptionsTests` (precedence, today's `ModeSelectionTests` moved), `ApplicationRuntimeTests` (escalation
Normal->Update->SessionEnd sticky, repeated shutdown same task, startup failure exit 1 after clean cleanup, handoff once,
deadline tightening never extends), `CrashLoopBreakerTests` (temp dir, fake clock).
Filter: `FullyQualifiedName~StartupOptions|FullyQualifiedName~ApplicationRuntime|FullyQualifiedName~CrashLoopBreaker|FullyQualifiedName~ModeSelection`.

**SESSION-B2 Explorer primitives behind ports** (about 1,300 lines)
Files: `Core/ExplorerControl.cs` -> `ExplorerShellProbe.cs`, `ExplorerLauncher.cs`; `ExplorerDesktopHost.cs`,
`ExplorerShellAnchor.cs`, `Interop/NativeShellProcess*.cs`; consumers of `IsDesktopShellRunning`; tests.
Steps: probe via `NativeShellProcess.TryGetImagePath` (no `MainModule`); exit loop and `_retired` into the host behind
`IExplorerNative` (find taskbar/shell window, owner pid, post message, close windows, process exit/exit code); never-throw
exit contract; `StopFailedChildAsync` disposes each resource independently; `ExplorerLauncher` with tri-state scheduler
used by host and terminal paths; `TerminateProcess` private; pooled image buffer; clear desktop-app suspension on failure
(or document).
Depends on: Install/elevation domain for the tri-state `UnelevatedLauncher` (U04B-LFA-001/002). If not landed, keep the
bool and add the tri-state in that domain's batch.
Tests: `ExplorerDesktopHostTests` with fake native (clean exit, unclean exit waits for respawn, replacement shell once,
lingering retired shell asked to close and never terminated, anchor Dispatched/NotDispatched/Unknown, scheduler fallback
refused when a surface appeared, disposal refuses), `ExplorerLauncherTests`, anchor start failure releases handles.
Filter: `FullyQualifiedName~Explorer`.

**SESSION-B3 Native windows and tray** (about 800 lines)
Files: `Interop/MessageWindow.cs`, `Shell/TrayHost.cs`, `Core/TrayProtocol.cs`, `Shell/SettingsActivation.cs`,
`Shell/SessionActivation.cs`, `Core/WindowFinder.cs`, consumers listed for `MessageWindow.Create()`, `Program.cs` Panic,
new linked `SessionProtocolNames.cs`; tests.
Steps: one owned `MessageWindow` (UI thread, disposed last), consumers take it by ctor; `WndProc` exception guards;
`TrayHost.Retire()` verifies `IsWindow`; Panic posts the retire; cbSize cap removed; warn set locked; stack buffers.
Depends on: Overlay domain for the `OverlayController`/`HotkeyService` ctor change.
Tests: `TrayProtocolTests` (cbSize 972 accepted), `MessageWindowTests` only for pure dispatch decode helpers extracted
from `WndProc`, `WindowFinderTests` getter test removed.
Filter: `FullyQualifiedName~Tray|FullyQualifiedName~MessageWindow|FullyQualifiedName~WindowFinder`.

**SESSION-B4 Transition backend collapse and Steam process port** (about 1,400 lines)
Files: `Shell/GameModeEntryServices.cs` (delete), new `Shell/SessionEntryBackend.cs`, `GameModeEntryTransaction.cs`,
`SessionModes.cs`, `DesktopReturnSequence.cs`, `GameModeReturnRecovery.cs`, `Core/Steam.cs` -> `SteamInstallation.cs`,
`SteamSessionControl.cs`, `BigPictureShortcuts.cs`; `SteamMonitor.cs`, `BootSplash.cs`; consumers; tests.
Steps: one `IGameModeEntryBackend`; result-based recovery (no throw from handlers); atomic entry state; refusal before
change unpauses the monitor; `SessionModes` ctor hooks; `GameModeReturnRecovery` instance with outcome; protocol
decisions use `steam.exe` only; fake-free compensation in tests.
Depends on: Config domain only if its store instance has landed; otherwise inject `Func<GameModeLaunchRecovery>` reads.
Tests: `GameModeEntryTransactionTests` rewritten over real `DesktopReturnSequence`, `SessionModesTests` (desktop request
during entry honoured once, refusal unpauses, shutdown refuses), `GameModeReturnRecoveryTests` (temp config, partial
restore keeps record), `SteamSessionControlTests` (main-process vs helper semantics via fake process query).
Filter: `FullyQualifiedName~GameModeEntryTransaction|FullyQualifiedName~SessionModes|FullyQualifiedName~DesktopReturnSequence|FullyQualifiedName~GameModeReturnRecovery|FullyQualifiedName~SteamSessionControl`.

**SESSION-B5 Power queue and config reloader** (about 900 lines)
Files: new `Shell/SessionPowerQueue.cs`, `Shell/SessionConfigReloader.cs`; delete `ShellSession.Power.cs`,
`ShellSession.Config.cs` (members per section 4); `ShellSession.cs`; tests.
Steps: extract verbatim; tail task exposed as `WhenIdleAsync`; admission flag; reloader keeps last good on any
non-Loaded outcome, ordered `TryApply` per step, applies `_modes.ApplyConfig` directly.
Depends on: Config domain read outcome (`Loaded/Absent/Corrupt/Unreadable/UnsupportedSchema`). Interim: treat any
exception from a strict read as "keep last good".
Tests: `SessionPowerQueueTests` (lock+suspend coalesce, opposite queued edge cancels, faulted cycle repaired on resume,
stale suspend dropped, stop refuses and joins), `SessionConfigReloaderTests` (unreadable keeps snapshot, one step throws
and later steps still run, stale generation dropped).
Filter: `FullyQualifiedName~SessionPowerQueue|FullyQualifiedName~SessionConfigReloader|FullyQualifiedName~SystemPowerTransition`.

**SESSION-B6 Session transitions extraction and startup isolation** (about 1,400 lines)
Files: new `Shell/SessionTransitions.cs`; delete `ShellSession.Modes.cs`; `ShellSession.cs` (split into
`ShellSession.cs` + `ShellSession.Composition.cs`); `ShellSession.Actions.cs` -> `SessionCommands.cs`; mode-authority
consumers (overlay, OEM); tests.
Steps: move boot takeover, launch sequence, desktop resume, splash and desktop actions; mode authority query;
per-owner `TryStart(name, essential, action)` in composition (essential: message window, desktop host; tray failure in
direct boot returns to resident Desktop); remove dead wiring and the dead continuation.
Depends on: Overlay domain (consumes mode query); B3, B4, B5.
Tests: `SessionTransitionsTests` with fake host/Steam/splash (takeover refused -> preserved desktop, uncertain exit ->
desktop restore, splash desktop request during takeover, tray create failure in direct boot -> resident Desktop),
`SessionStartupTests` (optional owner throws, session continues).
Filter: `FullyQualifiedName~SessionTransitions|FullyQualifiedName~SessionStartup|FullyQualifiedName~BootTakeover|FullyQualifiedName~ExplorerReadiness`.

**SESSION-B7 Shutdown as ordered steps** (about 1,000 lines)
Files: `ShellSession.Shutdown.cs`, `ShellSession.Composition.cs`, `App.axaml.cs` (forwarding only); tests.
Steps: R1 order; one `RunStepAsync(name, ui, step)` helper; admission close at T0 across owners; Steam host command
admission closed at T0; power queue and startup joined first, bounded; MessageWindow last; reason read per step;
SessionEnd never restores Explorer.
Depends on: Device domain (`DeviceCoordinator.CloseAdmission` and lane-aware `ShutdownAsync`), Steam UI domain (host
`CloseCommandAdmission`), common plugins/GPU stop accepting the deadline (exists).
Tests: `SessionShutdownTests` with fake owners and a fake clock: order, every step attempted after an earlier failure,
SessionEnd makes zero Explorer calls, escalation mid-shutdown tightens deadline and skips Explorer, a hung unrelated
transition does not delay device safety, unverified tray keeps the anchor, repeated shutdown returns the same task.
Filter: `FullyQualifiedName~SessionShutdown|FullyQualifiedName~ApplicationRuntime`.

**SESSION-B8 Remaining partial dissolution, tests and docs** (about 900 lines)
Files: `ShellSession.SteamUi.cs`, `ShellSession.Performance.cs`, `ShellSession.GameLibrary.cs` moved to their domains'
owners (calls only), `BootSplash*.cs` (arm at Steam request, entry-splash dismissal rule, `SplashDecode` static),
`ShellAnchorDisposalTests`, getter tests, `docs/boot-and-shell.md`, `docs/elevation.md`.
Depends on: Steam UI domain `SteamUiCoordinator`, Performance domain wiring, Library domain shortcut access; B6.
Tests: `BootSplashTests` (timeout armed at Steam request only), splash decode tests over the real helper.
Filter: `FullyQualifiedName~Splash|FullyQualifiedName~ShellAnchor`.

Order: B1, B2, B3 independent of each other; B4 after B2; B5 independent; B6 after B3-B5; B7 after B6 and the Device and
Steam UI admission changes; B8 last. Manual matrix rows touched: boot takeover, desktop/game transitions, restore-shell,
crash-loop disarm, update/uninstall handoff, sign-out, sleep/wake (M01).

## 6. Risks and open questions

Risks:

- Moving Explorer and tray code is the highest-impact change for "never strand users". Every batch keeps today's step
  order unless the finding is the order itself, and B2/B7 need the attended restore/transition rows before acceptance.
- SESSION-007 (session end arriving as Normal) must be verified in Avalonia 12 source before B1; the fix differs if the
  framework already distinguishes it.
- Terminal Explorer launch unification (SESSION-037) changes integrity handling on the restore-shell and crash-loop
  paths; validate on the notebook and the Claw.

Open questions for the maintainer:

- Q1. planning-corrections B3 is binding. Accept replacing its percentage cutoffs and preliminary drain with
  safety-first ordering under the single deadline (R1)? The anchor already recovers Explorer after an overrun.
- Q2. Tray relay to higher-integrity windows (U05-LFB-016): the plan's rule would stop relaying clicks to elevated tray
  apps such as an elevated Handheld Companion, because the sender can never be identified. Keep today's relay
  (application-defined messages only) and document the risk, or accept losing clicks on elevated tray icons in Game Mode?
