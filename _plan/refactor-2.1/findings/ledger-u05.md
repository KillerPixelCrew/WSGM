# Lifecycle audit unit B (ledger U05-LFB) findings

Scope: the 30 saved ledger rows U05-LFB-001 to U05-LFB-030 from the first Claude audit (`reports/U05-LFB.review.md`, bodies in `claude-findings-raw.json`). They cover the Shell session lifecycle: `ShellSession` and its partials, `SessionModes` and the Game Mode entry transaction, splash, tray, `MessageWindow`, update monitor, startup-app watcher, return recovery, config reload, shutdown, and their tests. Every body survived, so there is no missing-bodies section. Codex A01 and A02 recorded the unit as "assigned scope retained, not ready" and raised no A0x-F ids for it.

Baseline is `master` 1329813f. The session domain review (`_plan/refactor-2.1/review/session.md`) and its verification re-checked 28 of the 30 rows against that baseline and cite them as "covered". Those rows get a short section naming the covering finding and its batch. Two rows are not fully covered by any current finding and were re-checked here against the code: U05-LFB-012 (UpdateMonitor) is still valid and gets a full write-up, and so does the uncovered remainder of U05-LFB-024 (StartupAppWatcher and UpdateMonitor have no tests and no batch).

Severity below is the current one: the covering domain finding's severity after verification, with the ledger's original severity noted when it differs. Counts: 0 critical, 4 high, 7 medium, 14 low, 4 nit, and 1 no-change (U05-LFB-016, the tray relay integrity row, dropped by maintainer decision as security theater). Partial no-change parts are noted inside U05-LFB-013 and U05-LFB-024.

Plan v2 batches that resolve this area: B039 (config, plus U05-LFB-012), B042 (config), B111, B114, B115, B116, B124, B136, B140, B141 (session), with related parts in B040, B076, B077, B085, B086, B088, B091, B123, B125, B146, and B068 (no UnsupportedSchema outcome, per the newer-config decision). B140 is decided: D1, safety-first ordered shutdown steps under one deadline instead of the B3 percentage cutoffs the ledger rows were accepted under. B178 has nothing left to reconcile for this unit once this file is applied.

## High

### U05-LFB-002: Runtime config reads turn a malformed or unreadable config.json into live defaults

- **Severity:** high (SESSION-017; ledger said medium)
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:104-157`, `src/WSGM/Shell/ShellSession.Modes.cs:605, 751-754, 858, 874`, `src/WSGM/Core/ConfigStore.cs:122-139`.
- **Coverage:** confirmed by SESSION-017 (and the consumer instances CONFIG-003, CONFIG-004) for a corrupt or unreadable file. The verifier notes the "transient sharing violation" trigger is plausible rather than shown; the defect itself (any load exception becomes live defaults plus quarantine) is confirmed for a corrupt file. The newer-schema trigger is settled by maintainer decision (DECISIONS.md, config from a newer WSGM): such a file loads best effort as today, keeping what this build understands through B038's tolerant enum converter and metadata walker, so it reads as Loaded. There is no UnsupportedSchema outcome and no read-only mode.
- **Plan v2:** B039 (typed read outcomes Loaded, Absent, Corrupt and Unreadable; boot manifest projected only from Loaded or Absent); B116 (the reloader keeps the last good snapshot on Corrupt or Unreadable). B068 drops UnsupportedSchema and the strict-write refusal for a newer schema per the same decision.
- **Related:** SESSION-017, CONFIG-003, CONFIG-004, U04A-LFA-002, PV10-002, PV11-002.

### U05-LFB-003: Startup has no per-service failure isolation, so an optional surface failure ends the session

- **Severity:** high (SESSION-011, verifier raised from medium; ledger said medium)
- **Where:** `src/WSGM/Shell/ShellSession.cs:330-332, 347-456, 908-940`; `src/WSGM/Shell/ShellSession.Modes.cs:62-70`; `src/WSGM/Shell/SettingsActivation.cs:21-36`.
- **Coverage:** confirmed by SESSION-011. The verifier adds that `coordinatorAdopted = true` is set before `StartOnUiThread()`, so a throwing owner constructor also skips coordinator disposal and the forced `Shutdown(1)` runs no cleanup, leaving a HidHide cloak on. STEAMHOST (C25, verify notes) shows a host constructor throw is one concrete trigger.
- **Plan v2:** B124 (per-owner `TryStart(name, essential, action)` with the message window and desktop host essential; a tray failure in direct boot returns to resident Desktop). The cleanup-on-startup-failure half lands in B006 (SESSION-V-001).
- **Related:** SESSION-011, SESSION-V-001, PV11-003, STEAMHOST-017.

### U05-LFB-004: ShellSession is a 4,894-line partial-class aggregate that combines composition, policy and state machines

- **Severity:** high (SESSION-010; ledger said medium)
- **Where:** `src/WSGM/Shell/ShellSession*.cs` (all partials).
- **Coverage:** confirmed by SESSION-010; the decomposition is plan v2 section 4 and the session batches.
- **Plan v2:** B124 (transitions and composition extracted), with B116 (power queue, config reloader), B136 (Steam UI coordinator), B140 (shutdown) and B141 (remaining dissolution) completing it.
- **Related:** SESSION-010, U05-LFB-005.

### U05-LFB-005: The session drives process-wide mutable statics and singletons, so lifetimes and tests cannot be isolated

- **Severity:** high (SESSION-010; ledger said medium)
- **Where:** `src/WSGM/Shell/ShellSession.cs:225-226, 599, 655`; `src/WSGM/Shell/ShellSession.Shutdown.cs:654`; `src/WSGM/Core/SteamInputShim.cs:111-227`; `src/WSGM/Settings/SettingsPluginActions.cs:17-37`; `src/WSGM/Core/SteamUiReadiness.cs:29-35`; `src/WSGM/Shell/LibraryBadges.cs`; `src/WSGM/Shell/GameModeReturnRecovery.cs:13`.
- **Coverage:** confirmed by SESSION-010 and split per static: SETTINGS-010 (`SettingsPluginActions`), INPUT-010 (`SteamInputShim`), INPUT-012 (`SteamInputBlocker`, same shape), STEAMHOST-012 (`SteamUiReadiness`, `SteamUiTransportSession` driving, `LibraryBadges`, `LibraryTabManager` statics), SESSION-027/CONFIG-024 (`GameModeReturnRecovery.Gate`).
- **Plan v2:** B124 (session side), B123 (SETTINGS-010), B076 (INPUT-010), B077 (INPUT-012), B136 (STEAMHOST-012), B042 (return recovery instance).
- **Related:** SESSION-010, SETTINGS-010, INPUT-010, INPUT-012, STEAMHOST-012, PV11-005.

## Medium

### U05-LFB-001: Shutdown does not join in-flight device/common-plugin power transitions or reconcile tasks

- **Severity:** medium
- **Where:** `src/WSGM/Shell/ShellSession.Power.cs:165-253` (`QueueDevicePowerTransition`, `_devicePowerWork`); `src/WSGM/Shell/ShellSession.Shutdown.cs:139-191`.
- **Coverage:** confirmed by SESSION-019, which extends the scope to every other fire-and-forget task in the session. Plan v2 replaces the ledger's accepted B3 remedy (percentage cutoffs, 500 ms preliminary drain) with owner admission close plus joining only the power queue, startup task, transition and gate loop under one deadline.
- **Plan v2:** B140 (decided: D1, safety-first ordered steps under one deadline; the B3 percentage cutoffs and preliminary drain are dropped). Related parts: B040 (CONFIG-018, unbounded fan-out disposal), B146 (SDK-004, active-time waits).
- **Related:** SESSION-019, SESSION-033, CONFIG-018, SDK-004, PV11-001.

### U05-LFB-006: Session transition and power state machines are tested only through extracted pure predicates

- **Severity:** medium
- **Where:** `tests/WSGM.Tests/Shell/SessionModesTests.cs:8-59`, `tests/WSGM.Tests/Shell/SystemPowerTransitionTests.cs:20-70` versus `src/WSGM/Shell/SessionModes.cs`, `src/WSGM/Shell/ShellSession.Power.cs`.
- **Coverage:** confirmed by SESSION-060 (and DEVICE-014 for the device coordinator). The behavioural tests are spread over the batches that create the seams.
- **Plan v2:** B115 (entry and return over the real `DesktopReturnSequence`), B116 (`SessionPowerQueue`: coalescing, opposite edge cancels, stale suspend dropped, stop joins), B140 (shutdown order), B088 (DEVICE-014).
- **Related:** SESSION-060, DEVICE-014, U05-LFB-024.

### U05-LFB-015: The tray host is treated as retired even when DestroyWindow(Shell_TrayWnd) failed

- **Severity:** medium (SESSION-045; ledger said low)
- **Where:** `src/WSGM/Shell/TrayHost.cs:63-100`; `src/WSGM/Shell/ShellSession.Shutdown.cs:274-276, 759-784`.
- **Coverage:** confirmed by SESSION-045 (retire returns `!IsWindow(_trayHwnd)`, UI-thread affinity).
- **Plan v2:** B114.
- **Related:** SESSION-045, PV11-014.

### U05-LFB-018: Config reload applies about 30 steps with no per-step isolation

- **Severity:** medium (SESSION-018; ledger said low)
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:110-157`.
- **Coverage:** confirmed by SESSION-018: one explicit ordered list with a `TryApply(name, action)` helper, no subscriber registry.
- **Plan v2:** B116.
- **Related:** SESSION-018, U05-LFB-002, U05-LFB-028.

### U05-LFB-019: Session fields are mutated across threads without synchronization

- **Severity:** medium (SESSION-013; ledger said low)
- **Where:** `src/WSGM/Shell/ShellSession.Actions.cs:110-118`; `src/WSGM/Shell/ShellSession.cs:829-835`; `src/WSGM/Shell/ShellSession.Modes.cs:820-833`.
- **Coverage:** confirmed by SESSION-013 (only the reloader replaces `_config`; writers commit to the store). The pending-return field is SESSION-016; device-side instances are DEVICE-011.
- **Plan v2:** B124 (SESSION-013 and the CONFIG-040 session part), B115 (SESSION-016), B091 (DEVICE-011), B039 (CONFIG-040 store part).
- **Related:** SESSION-013, SESSION-016, CONFIG-040, DEVICE-011.

### U05-LFB-020: During shutdown the Steam UI host outlives the device and GPU owners it calls, and the gate wait is unbounded

- **Severity:** medium (SESSION-020; ledger said low)
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:139-191, 271-297, 355-375`.
- **Coverage:** confirmed by SESSION-020 and STEAMHOST-001: close the host's command admission at T0 (the existing no-CEF prefix of `DisableAsync`), keep retraction and disposal after device cleanup, bound the gate wait by the deadline.
- **Plan v2:** B136 (admission close and coordinator), B140 (step order and deadline).
- **Related:** SESSION-020, STEAMHOST-001.

### U05-LFB-021: GameModeEntryTransactionTests' fake re-implements the compensation it asserts, and some paths are missing

- **Severity:** medium (SESSION-059; ledger said low)
- **Where:** `tests/WSGM.Tests/Shell/GameModeEntryTransactionTests.cs:125-127, 152, 387-405, 420-435`.
- **Coverage:** confirmed by SESSION-059: tests run the real `DesktopReturnSequence` behind a fake `IDesktopReturnBackend`.
- **Plan v2:** B115.
- **Related:** SESSION-059.

## Low

### U05-LFB-007: A Game Mode entry refused before any change leaves the Steam monitor paused on the desktop

- **Severity:** low
- **Where:** `src/WSGM/Shell/GameModeEntryTransaction.cs:143-147`; `src/WSGM/Shell/SessionModes.cs:466, 492-508`.
- **Coverage:** confirmed by SESSION-024 (a refusal before change unpauses the monitor).
- **Plan v2:** B115.
- **Related:** SESSION-024.

### U05-LFB-008: A desktop request racing the end of Game Mode entry can be lost and later fire spuriously

- **Severity:** low
- **Where:** `src/WSGM/Shell/SessionModes.cs:322-327, 467-468, 492-508`.
- **Coverage:** confirmed by SESSION-025 (one atomic entry-state field, no extra flags).
- **Plan v2:** B115.
- **Related:** SESSION-025.

### U05-LFB-009: KickTabBootSync can cancel an already-disposed source, and completed sources are never disposed

- **Severity:** low
- **Where:** `src/WSGM/Shell/ShellSession.SteamUi.cs:316-353`.
- **Coverage:** confirmed by SESSION-021 and STEAMHOST-011 (the tab boot sync becomes one tracked worker with a reschedulable signal).
- **Plan v2:** B136.
- **Related:** SESSION-021, STEAMHOST-011.

### U05-LFB-010: Boot splash timeout starts at Show, contradicting SplashPolicy and the boot doc, and the takeover can exceed it

- **Severity:** low
- **Where:** `src/WSGM/Shell/BootSplash.cs:52-58, 109`; `src/WSGM/Shell/SplashPolicy.cs:11-23`; `src/WSGM/Shell/ShellSession.Modes.cs:83`.
- **Coverage:** confirmed by SESSION-057 (timeout armed at the Steam request).
- **Plan v2:** B141.
- **Related:** SESSION-057, U05-LFB-011.

### U05-LFB-011: Opening the overlay dismisses a Game Mode entry splash while the entry continues uncovered

- **Severity:** low
- **Where:** `src/WSGM/Shell/ShellSession.Modes.cs:84, 535-558`; `src/WSGM/Overlay/OverlayController.cs:486`.
- **Coverage:** confirmed by SESSION-057 and overlay plan check C10 (keep the `OverlayShown` event; the entry-splash dismissal rule becomes explicit on the session side).
- **Plan v2:** B141.
- **Related:** SESSION-057, U05-LFB-010.

### U05-LFB-012: UpdateMonitor can stop silently for the session, and its Dispose blocks the UI thread and hides faults

- **Severity:** low
- **Where:** `src/WSGM/Shell/UpdateMonitor.cs:31-44` (`Dispose`), `:46-67` (`RunAsync`); `src/WSGM/Core/UpdateChecker.cs:116-148` (`CheckAsync`), `:225-257` (`ParseLatestRelease`); `src/WSGM/Shell/ShellSession.cs:374` (construction); `src/WSGM/Shell/ShellSession.Shutdown.cs:708-712` (disposal on the UI thread).
- **Problem:** still valid at 1329813f; no domain finding covers it (winsvc.md only defers to this row). `CheckAsync` catches a fixed list (`HttpRequestException`, `TaskCanceledException`, `JsonException`, `InvalidDataException`, `IOException`, `UnauthorizedAccessException`). Anything else escapes into `RunAsync`, which catches only caller cancellation, so `_loop` faults and the daily check stops for the rest of the session. Concrete triggers exist in the parser: `tag.GetString()` and `html.GetString()` throw `InvalidOperationException` when GitHub (or a proxy) returns a non-string `tag_name` or `html_url`, and `Text(asset, ...)` throws the same when an `assets` element is not an object; `PluginPackageCatalog.DiscoverInstalled` and `Warnings` can throw other types. Nothing logs it: `_loop` stays referenced, so `TaskScheduler.UnobservedTaskException` (Program.cs:205) never fires, and at shutdown `Dispose` waits synchronously on the UI thread for up to 2 s and then swallows the `AggregateException` with the comment "Cancelled", which hides the real fault. It then disposes `_stop` while the loop may still be running.
- **Best solution:** contain failures at the checker, where the documented contract already says "malformed answers leave the previous state in place", and remove the monitor's wait-and-swallow disposal.
  1. `UpdateChecker.ParseLatestRelease`: replace the throwing reads with type checks. Read `tag_name` and `html_url` through the existing `Text` helper (string kind only; `Text(root, "html_url") ?? ""`), and skip any `assets` element whose `ValueKind` is not `Object` before calling `Text` (`TryGetProperty` throws on a non-object). A malformed release then returns null like every other unusable answer. These are type checks, not limits.
  2. `UpdateChecker.CheckAsync`: replace the type list with one filter, `catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)`, keep `Log.Info` but write `ex.GetType().Name + ": " + ex.Message` so an unexpected type is identifiable (no stack trace: an offline handheld hits this daily), and return `ReadState()`. The check never faults the loop again, and the Settings manual check (`src/WSGM/Settings/SettingsViewModel.Updates.cs:112`, which has no catch of its own and passes `CancellationToken.None`) gets the same containment.
  3. `UpdateMonitor.Dispose`: reduce it to `_stop.Cancel()`. Delete the `Wait(TimeSpan)`, the `AggregateException` catch with its misleading "Cancelled" comment, and the `_stop.Dispose()` (a token source without a timer holds nothing to release, and disposing it while the loop may still observe the token is the race being removed). The loop ends on its own within moments because `HttpClient` and `Task.Delay` honour the token; plan v2 B140 joins only the power queue, startup task, transition and gate loop, so this loop is not one to join. The `_updates?.Dispose()` shutdown step in `ShellSession.Shutdown.cs` stays as it is; it now returns at once instead of blocking the UI thread.
  This beats "wrap the loop body in try/catch and keep looping" and beats an `IAsyncDisposable` with an awaited join: the fault originates in a checker whose contract already promises not to throw for bad answers, and the fix removes mechanism from the monitor instead of adding a shutdown step or retry state.
- **Tests:** in `tests/WSGM.Tests/Core/UpdateCheckerTests.cs` add `LatestRelease_TreatsWronglyTypedFieldsAsNoRelease` (numeric `tag_name` returns null; numeric `html_url` with valid assets returns the release with an empty page; a string element inside `assets` is skipped and the valid assets still produce the release). No loop test with injected delays; the monitor's only remaining logic is cancel. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~UpdateChecker"`.
- **Plan v2:** B039 (it already edits `src/WSGM/Core/UpdateChecker.cs` and fixes the same catch-filter pattern in CONFIG-V-001); add `src/WSGM/Shell/UpdateMonitor.cs` to its file list, `U05-LFB-012` to its Resolves line and `FullyQualifiedName~UpdateChecker` to its test filter. No dependency on B140 or D1, since the shutdown code does not change.
- **Related:** U05-LFB-024 (no tests), CONFIG-V-001 (same catch-filter pattern elsewhere), SESSION-019 (which tasks shutdown joins).

### U05-LFB-013: Native window procedures and the power-mode callback have no exception guard at the native boundary

- **Severity:** low
- **Where:** `src/WSGM/Interop/MessageWindow.cs:530-627`; `src/WSGM/Interop/DisplayChangeWindow.cs:82-96`; `src/WSGM/Interop/EffectivePowerModeNotification.cs:88-98`.
- **Coverage:** the substantive instance is `EffectivePowerModeNotification.OnChanged`, which invokes the managed delegate synchronously inside `[UnmanagedCallersOnly]` (DEVICE-017, WINSVC-V-003): guard it with try/catch in the callback. The window procedures only compare messages and `Dispatcher.UIThread.Post` the handlers (re-read at 1329813f), so WINSVC-045 records their guard as cosmetic no-change; SESSION-044 still adds a cheap guard in `MessageWindow.WndProc` within B114. Either outcome is acceptable; the guard must only log and fall through to `DefWindowProcW`.
- **Plan v2:** B091 (power-mode callback, DEVICE-017 and WINSVC-V-003); B114 (MessageWindow guard, SESSION-044).
- **Related:** DEVICE-017, WINSVC-V-003, WINSVC-045, SESSION-044, PV11-012.

### U05-LFB-014: MessageWindow is a shared process singleton whose Dispose destroys it for every holder

- **Severity:** low (SESSION-043, verifier lowered from medium; ledger said low)
- **Where:** `src/WSGM/Interop/MessageWindow.cs:86-108, 189-203`; `src/WSGM/Shell/ShellSession.Shutdown.cs:598-609, 752-756`.
- **Coverage:** confirmed by SESSION-043 and WINSVC-017. The verifier found no observable fault today (`Dispose` zeroes `_volumeNotify`, so the drive manager's later deregistration is a no-op); the ownership fix stands: one instance owned by the composition root, consumers take it by constructor, `Create()` removed, disposed last.
- **Plan v2:** B114 (ownership); B140 step 11 (MessageWindow last).
- **Related:** SESSION-043, WINSVC-017, PV11-013.

### U05-LFB-017: GameModeReturnRecovery reports "complete" while deliberately leaving its record in place

- **Severity:** low
- **Where:** `src/WSGM/Shell/GameModeReturnRecovery.cs:13, 28-50, 91-103`; `src/WSGM/Shell/ShellSession.cs:266-273`; `src/WSGM/Shell/ShellSession.Shutdown.cs:846-872`.
- **Coverage:** confirmed by SESSION-027 and CONFIG-024: instance `DesktopReturnRecovery`, `RestoreAsync` never clears, explicit `ClearIfUnchanged(fingerprint)` at each site.
- **Plan v2:** B042.
- **Related:** SESSION-027, CONFIG-024, PV11-005, PV11-016.

### U05-LFB-024: Several standalone session components in scope have no tests at all

- **Severity:** low
- **Where:** `src/WSGM/Shell/StartupAppWatcher.cs:156-299` (relaunch state machine, `WatchState`); `src/WSGM/Shell/UpdateMonitor.cs`; `src/WSGM/Shell/TrayHost.cs:400-515`; `src/WSGM/Shell/BootSplash.cs:118-245`; `src/WSGM/Shell/GameModeReturnRecovery.cs`; `src/WSGM/Shell/ShellDisplaySignals.cs`; `src/WSGM/Interop/MessageWindow.cs:530-627`; `src/WSGM/Shell/SessionActivation.cs`; construction at `src/WSGM/Shell/ShellSession.Modes.cs:141`.
- **Problem:** re-checked at 1329813f: `tests/` still has no reference to `StartupAppWatcher`, `UpdateMonitor`, `GameModeReturnRecovery`, `ShellDisplaySignals` or `MessageWindow`; `TrayHost` appears only through `TrayProtocolTests`, `BootSplash` only through style and config tests, `SessionActivation` has a UI test. SESSION-060 lists most of these, but batches.json resolves it only in B115 (entry compensation over the real `DesktopReturnSequence`); its return-recovery tests are already in B042's spec, but its tray and splash test parts are in no batch spec and are assigned below to B114 and B141. Nothing covers `StartupAppWatcher` or `UpdateMonitor`, and no batch lists either file. The watcher's crash-loop relaunch, 30 s cooldown, "seen alive once" rule, unknown-probe handling and generation invalidation are protected only by manual testing; a regression either spams relaunches of Handheld Companion-style tools or never relaunches them.
- **Best solution:** extract the watcher's decisions into pure methods on the existing `WatchState`, with no new seams on the watcher itself.
  0. `WatchState` is a `private sealed class` nested in the watcher today; make it `internal` (it stays nested, so tests use `StartupAppWatcher.WatchState`; WSGM.Tests already has `InternalsVisibleTo`) and move `RelaunchDelay` and `RelaunchCooldown` onto it as `internal static readonly` fields so the methods below and the tests share one copy.
  1. Move the body of `Apply`'s per-probe step into `WatchState.Observe(bool isAlive, DateTime utcNow, out TimeSpan delay, out long revision)`, returning true when a relaunch must be scheduled. It does exactly what lines 180-206 do today: update `ExitUnresolved` and `WasAlive`, cancel a pending relaunch when the app is alive again (revision bump), and compute `max(RelaunchDelay, LastRelaunchUtc + RelaunchCooldown - utcNow)`.
  2. Move the first half of `Relaunch` into `WatchState.TryConsume(long revision, bool? isAlive)`: revision check, clear `RelaunchPending`, set `ExitUnresolved = isAlive is null`, return true only for `isAlive == false`.
  3. `StartupAppWatcher` keeps the `DispatcherTimer`, `WindowFinder.ReadRunningPaths`, `Task.Delay`, the generation and suppression checks, config re-resolution and `AppLauncher.Start`, passing `DateTime.UtcNow`. Behaviour and log lines are unchanged.
  This is cheaper than the ledger's direction (inject a clock, a process probe and a launcher into the watcher), which adds three constructor seams and still needs a dispatcher in tests; the state methods are testable as plain objects. `UpdateMonitor` gets no test of its own: after U05-LFB-012 its only logic is cancel, and the failure containment is tested on `UpdateChecker`. `ShellDisplaySignals` stays untested: `ShellDisplayPresence` is a one-line forwarder and `ShellDisplayChangeSignal` is a 20-line adapter whose fallback is a plain `Task.Delay`; a test would pin framework behaviour (the same objection as SESSION-061).
- **Tests:** new `tests/WSGM.Tests/Shell/StartupAppWatcherStateTests.cs`: dead on first sight never schedules; alive then dead schedules after 5 s; an exit inside the 30 s cooldown schedules for the remaining cooldown; alive again while pending invalidates the revision so `TryConsume` returns false; an unknown liveness at relaunch time sets `ExitUnresolved` and the next dead poll schedules again; `TryConsume` with a stale revision does nothing. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~StartupAppWatcher"`. B114 and B141 must also add the tray click encoder (double-click detection, version-specific notify packing) and splash dismiss/timeout tests that SESSION-060 lists; their filters (`Tray`, `Splash`) already match.
- **Plan v2:** B124 (it moves the watcher's construction out of `ShellSession.Modes.cs`; add `src/WSGM/Shell/StartupAppWatcher.cs` and the new test to its file list and the `StartupAppWatcher` filter to its test line). The `UpdateMonitor` part is U05-LFB-012 in B039. B114 and B141: add the tray and splash tests named above to their specs.
- **Related:** SESSION-060, U05-LFB-006, U05-LFB-010, U05-LFB-012, U05-LFB-017, SESSION-061.

### U05-LFB-025: Dead conditionals, redundant assignments and stale comments in the session wiring

- **Severity:** low (SESSION-014; ledger said nit)
- **Where:** `src/WSGM/Shell/ShellSession.cs:313, 712-714, 823, 1057, 1072, 1087-1089`; `src/WSGM/Shell/ShellSession.Shutdown.cs:76, 678-682`.
- **Coverage:** confirmed by SESSION-014; the Steam UI shadow-field self-assignments are also in STEAMHOST-003 (one switches record) and the dead overlay-test conditionals in STEAMHOST-005 (backends built at the composition root).
- **Plan v2:** B124 (SESSION-014); B085 (STEAMHOST-003 part); B086 (STEAMHOST-005 part).
- **Related:** SESSION-014, STEAMHOST-003, STEAMHOST-005, PV11-020.

### U05-LFB-027: Desktop actions build their own PluginActionSequence and swallow every cancellation

- **Severity:** low (SESSION-029; ledger said nit)
- **Where:** `src/WSGM/Shell/ShellSession.Modes.cs:560-563, 579-587, 605, 630-648, 753`.
- **Coverage:** confirmed by SESSION-029 (reuse the `ActionSequence()` helper, no `SessionActionRunner`). The dead `ContinueWith(OnlyOnFaulted)` beside it is SESSION-015; the lenient re-reads are handled by B039's read outcomes.
- **Plan v2:** B115 (SESSION-029); B124 (SESSION-015).
- **Related:** SESSION-029, SESSION-015, SESSION-017.

### U05-LFB-028: SessionModes receives configuration only through OverlayController

- **Severity:** low (SESSION-026; ledger said nit)
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:152`; `src/WSGM/Overlay/OverlayController.cs:383-385`; `src/WSGM/Shell/SessionModes.cs:221-224`.
- **Coverage:** confirmed by SESSION-026 (the reloader applies modes config directly) and OVERLAY-011 (the overlay drops the forward).
- **Plan v2:** B116; B125 (OVERLAY-011).
- **Related:** SESSION-026, OVERLAY-011, U05-LFB-018.

### U05-LFB-029: ShutdownAsync's reentrancy guard is a non-atomic check-then-set with a second entry path

- **Severity:** low (SESSION-031; ledger said nit)
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:29-39`; `src/WSGM/Shell/ShellSession.cs:237-243`.
- **Coverage:** confirmed by SESSION-031 (`_shutdown ??= RunShutdownAsync()` on the UI thread) and SESSION-008 (delete the unused `DisposeAsync` with its own budget).
- **Plan v2:** B111.
- **Related:** SESSION-031, SESSION-008, PV11-024.

## Nit

### U05-LFB-022: ShellAnchorDisposalTests pins .NET pipe behavior, not WSGM's guard, and sits outside the guard's area

- **Severity:** nit (SESSION-061; ledger said low)
- **Where:** `tests/WSGM.Tests/Shell/ShellAnchorDisposalTests.cs:23-55`; guard at `src/WSGM/Core/ExplorerShellAnchor.cs:370`.
- **Coverage:** confirmed by SESSION-061; the premise test is deleted.
- **Plan v2:** B141.
- **Related:** SESSION-061.

### U05-LFB-023: Splash decode tests re-implement TryLoadBitmap's decision instead of exercising it

- **Severity:** nit (SESSION-058; ledger said low)
- **Where:** `tests/WSGM.Tests/Shell/SplashStyleTests.cs:346-356, 431-441`; `src/WSGM/Shell/BootSplashWindow.axaml.cs:651-697`.
- **Coverage:** confirmed by SESSION-058: the pure decode math moves to one static helper that the tests call. Its byte and dimension bounds stay (decided: D2 accepts the plan v2 list, which names the splash zip bounds).
- **Plan v2:** B141.
- **Related:** SESSION-058.

### U05-LFB-026: SessionModes has an orphaned XML summary stacked on an unrelated field

- **Severity:** nit
- **Where:** `src/WSGM/Shell/SessionModes.cs:60-69`.
- **Coverage:** confirmed by SESSION-028.
- **Plan v2:** B141.
- **Related:** SESSION-028.

### U05-LFB-030: Small test-hygiene nits: constant pinning, a misplaced test, and a pending Task.Delay per hint

- **Severity:** nit
- **Where:** `tests/WSGM.Tests/Shell/SystemPowerTransitionTests.cs:71-80`; `tests/WSGM.Tests/Shell/GameWindowReturnTests.cs:120-135`.
- **Coverage:** confirmed by SESSION-062, which adds further getter and constant tests and the process-global shutdown reason leak in `ApplicationShutdownTests`.
- **Plan v2:** B141 (getter and constant tests deleted); the shutdown-reason tests move with `ApplicationRuntime` in B111.
- **Related:** SESSION-062.

## Refuted or no-change

- **U05-LFB-016** (tray clicks relay callback messages from High-IL WSGM to any HWND a Medium-IL registrant named; `src/WSGM/Shell/TrayHost.cs:199-200, 307-376, 400-515`, covered by SESSION-048, PV11-015): Dropped by maintainer decision (security theater, DECISIONS.md). Today's relay of application-defined messages stays unchanged, with no sender or integrity check and no added remarks block or `docs/elevation.md` note about the integrity risk. B114 keeps only its functional work (one owned MessageWindow, verified tray retirement) and the tray tests named in U05-LFB-024. The row has no functional part.

The other 29 U05-LFB rows are still valid at 1329813f and appear above as sections. The partial no-change parts are recorded in the sections that own them: the window-procedure guard in U05-LFB-013 (cosmetic per WINSVC-045, since both procedures only post) and the `ShellDisplaySignals` and `UpdateMonitor` loop tests in U05-LFB-024 (trivial adapters; the failure containment is tested on `UpdateChecker`). The `IAsyncDisposable` join first proposed for `UpdateMonitor` in U05-LFB-012 was dropped: plan v2 B140 does not join that loop, so `Dispose` only cancels.
