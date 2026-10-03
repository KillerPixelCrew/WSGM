# Session findings

Scope: process startup and exit (`Program.cs`, `App.axaml.cs`, `Core/ApplicationShutdown.cs`, `Core/UpdateExitWatcher.cs`), the `ShellSession` partial aggregate (nine files), Game Mode transitions (`SessionModes`, `GameModeEntryTransaction`, `GameModeEntryServices`, `DesktopReturnSequence`, `GameModeReturnRecovery`), Explorer control and recovery (`ExplorerControl`, `ExplorerDesktopHost`, `ExplorerShellAnchor`, `NativeShellProcess`), native windows and tray (`MessageWindow`, `TrayHost`, `TrayProtocol`, `DesktopTray`, `SessionActivation`, `SettingsActivation`), `WindowFinder`, `Steam`, the boot splash, and their tests. Baseline `1329813f`. Line numbers are from the review; anchor every edit by symbol.

Sources merged: `_plan/refactor-2.1/review/session.md`, its adversarial verification `session.verify.md` (refutations, severity corrections and the five missed findings SESSION-V-001 to V-005), the critic (conflicts 1, 2, 14, 25 and CRIT-004, folded into the solutions below) and plan v2, which wins where it simplified a recommendation. The maintainer's answers in `DECISIONS.md` (2026-10-03) override all of them: D1 shutdown design, best-effort config loading without a read-only mode or `UnsupportedSchema` outcome, today's tray relay kept, and every security-hardening item dropped.

Counts: 64 findings written as sections (critical 1, high 5, medium 16, low 30, nit 12), including SESSION-C-001 added by the solution check, and 5 ids in the refuted or no-change list (SESSION-003, SESSION-012, SESSION-040, SESSION-048, SESSION-054).

Plan v2 batches covering this area: B006 (exit cleanup, first), B031 (protocol names), B039 and B042 (config-owned parts), B111, B112, B114, B115, B116, B124, B136 (Steam UI host parts), B140 (shutdown order, decided: D1, safety-first ordered steps under one deadline) and B141. B113 (overlay activation) must land before B114. Manual rows that prove the area: M01-44 (tray Exit on a device session shows the pad, cloak off), M01-45 (update handoff line with `outcome=Clean`), M01-46 (`--restore-shell` from Game Mode on a device session), M01-47 (sign-out, restart, shutdown from the overlay with no blocker screen and no Explorer start).

Ground rules that apply to every solution here: Explorer is only asked to exit with `0x5B4` and `WM_CLOSE` and never killed; HidHide cloak goes off on every exit path; no new phase engines, registries or generation counters; no caps that drop content; no automatic retry of an uncertain write.

---

## Critical

### SESSION-V-001: Every programmatic exit skips the whole session cleanup

- **Severity:** critical (missed by the review, found by the verifier).
- **Where:** `src/WSGM/Core/ApplicationShutdown.cs:57-64` (`ApplicationShutdownRequest.ShutdownLifetime`); callers `src/WSGM/Program.cs:502-519` (`RequestRestoreShellExit`, `RequestInstallerExit`), `src/WSGM/Shell/ShellSession.cs:925` (Desktop tray "Exit WSGM"), `src/WSGM/Shell/ShellSession.Shutdown.cs:16-26` (`OnSessionEnding`); handler `src/WSGM/App.axaml.cs:50,94,99-146`.
- **Problem:** `ShutdownLifetime` calls `IClassicDesktopStyleApplicationLifetime.Shutdown()`. In Avalonia 12.1.2 that is `DoShutdown(..., force: true)`, which never raises `ShutdownRequested` (confirmed by decompiling the pinned package). So `App.OnShutdownRequested` and `ShellSession.ShutdownAsync` never run for tray Exit, `--restore-shell`, update, uninstall or `WTS_SESSION_LOGOFF`. Skipped on all of them: AutoTDP restore, controller release and HidHide cloak-off, device, common and GPU plugin stop, tray retirement, Explorer restore, refresh and resolution restore, chord template restore, and the installer handoff. `wsgm.log` never contains `Installer shutdown handoff completed`; setup waits out its full 22 s grace on every update and uses its force fallback. A user who picks "Exit WSGM" on a device session is left with the physical pad hidden.
- **Best solution:** The exit sequence stops depending on the Avalonia event. In B006, add one small instance type in `Core/ApplicationShutdown.cs`, named `ApplicationRuntime` from the start so B111 moves and extends it instead of renaming it. App creates it in `OnFrameworkInitializationCompleted` and passes the session to it. It holds `Task? _exit`, `bool _startupFailed` and `bool _osEnding`, and takes three delegates so tests need no Avalonia: `Func<ApplicationShutdownReason, DateTimeOffset, ValueTask>? sessionShutdown` (null when the process has no session), `Action<int> forcedExit` (production: `desktop.Shutdown(code)`) and `Action<ApplicationShutdownReason, ApplicationShutdownOutcome> reportHandoff`.
  - `RequestExit()` runs on the UI thread and does `_exit ??= RunAsync()`. `RunAsync` reads the reason, awaits `ApplicationShutdownCoordinator.ShutdownAsync(deadline => sessionShutdown(reason, deadline), reason)`, reports the handoff once, computes `code = _startupFailed ? 1 : ExitCodeFor(outcome)`, and then calls `forcedExit(code)`, unless the OS end-session path owns the exit (SESSION-007). `RunAsync` keeps the UI synchronization context (no `ConfigureAwait(false)` on its own awaits), so `forcedExit` and the handoff report run on the UI thread.
  - When there is no session (Settings, overlay-test before start), `RunAsync` reports the handoff for the current reason with `Clean` and calls `forcedExit(0)` (SESSION-C-001). This cannot complete a running shell's handoff early: setup only accepts completion once the shell mutex `Local\WSGM.Shell` is gone (`WindowsSetup.RequestExit`), and that mutex lives until the shell process exits.
  - `ApplicationShutdownRequest.ShutdownLifetime()` is deleted. All five sources (tray Exit, restore-shell, update, uninstall, `OnSessionEnding`) call `ApplicationShutdownRequest.Request(reason)` and then the runtime's `RequestExit()`, reached through `App` (`((App)Application.Current!).Runtime`). That is the one static door that remains until B111 passes the runtime explicitly.
  - `App.OnShutdownRequested` keeps only the OS end-session role (SESSION-007). `_shutdownInProgress`, `_sessionStopped` and `_shutdownOutcome` collapse into the runtime's `_exit` task.

  This beats the verifier's one-line `TryShutdown()` alternative. With `TryShutdown()`, programmatic exits would still round-trip through `ShutdownRequested`, and the handler could not tell a logoff (where `IsOSShutdown` is false) from a tray Exit. Driving the exit directly makes every `ShutdownRequested` mean "the OS is ending the session". Land SESSION-002 (sticky exit code), SESSION-007, SESSION-V-005 and SESSION-V-002 in the same batch, because they all touch this sequence.
- **Tests:** In `ApplicationShutdownTests`, use a fake `forcedExit` that never calls back. Tray Exit, update and restore-shell requests must each run `sessionShutdown` exactly once and then `forcedExit` once. Repeated requests return the same task. With a null session an update request reports `Update`/`Clean` once and the exit code is 0. Update reports `Clean` once. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ApplicationShutdown|FullyQualifiedName~ModeSelection"`. Manual: M01-44, M01-45, M01-46.
- **Plan v2:** B006 (fix); B111 (moves into `Core/ApplicationRuntime.cs`); B140 (seam tests).
- **Related:** SESSION-V-003, SESSION-002, SESSION-007, SESSION-V-005, SESSION-032, SESSION-009, SESSION-011, SESSION-C-001, U04A-LFA-013; never-strand group with B009 (controller release).

## High

### SESSION-007: WSGM vetoes sign-out and shutdown, then cleans up as Normal and restores Explorer

- **Severity:** high (verifier raised it from low/PLAUSIBLE to CONFIRMED/high).
- **Where:** `src/WSGM/App.axaml.cs:99-146` (`OnShutdownRequested`, `eventArgs.Cancel = true` at `:118`, `Consume()` at `:109`); `src/WSGM/Shell/ShellSession.Shutdown.cs:16-26` and the Explorer and Big Picture steps at `:798-881`; Avalonia `Win32Platform.WndProc` (message 17) and `ClassicDesktopStyleApplicationLifetime.DoShutdown`.
- **Problem:** Avalonia raises `ShutdownRequested` from `WM_QUERYENDSESSION` (`IsOSShutdown = lParam == 0`) and returns 0 when the handler cancels. WSGM always cancels for a live session, so it answers FALSE and blocks sign-out, restart and shutdown. That includes its own overlay Shut down and Restart, which run `shutdown /s /t 0` without `/f`. The cleanup then runs with reason Normal: it sends `steam://close/bigpicture` and restores Explorer during session end, which the SessionEnd rule forbids. A later `WTS_SESSION_LOGOFF` is dropped by the `_disposed` check.
- **Best solution:** After V-001, every `ShutdownRequested` that reaches App comes from the OS, so the handler does the following:
  1. Call `ApplicationShutdownRequest.Request(SessionEnd)`, which sets the sticky SessionEnd flag (SESSION-V-005).
  2. Call `runtime.RequestOsSessionEnd()`, which sets `_osEnding` and returns `RequestExit()`. This starts the bounded 5 s SessionEnd cleanup, or joins the one already running (whose outer deadline SESSION-032 tightens to 5 s).
  3. Run a nested dispatcher frame (`Dispatcher.UIThread.PushFrame(frame)`, with `frame.Continue = false` set when the exit task completes) so UI-marshalled cleanup steps keep running.
  4. Return with `e.Cancel` left false. Avalonia then finishes its own `DoShutdown`: it closes windows, raises `Exit` and stops the dispatcher.

  The runtime does not call the forced `Shutdown(code)` when `_osEnding` is set, because `DoShutdown` is already in progress. The exit code at OS session end is not observed, so no `Exit` handler is needed. The session reads the sticky flag and skips the Big Picture close and the Explorer restore steps. Nothing else changes.

  A sign-out or restart that another application vetoes after WSGM answered TRUE can still be cancelled by the user, and WSGM has then already exited. So on SessionEnd the shutdown must not stop the shell anchor: `RestoreDesktopBeforeShutdownAsync` returns false for SessionEnd (today it returns true, which makes the shutdown send the anchor its `stop` through `_desktopHost.DisposeAsync()`). The host is then dropped without disposal, exactly as on the unverified-tray branch, and the anchor's existing owner-loss recovery (`RecoverAfterOwnerLossAsync`, which already checks `IsSessionActive` and an existing shell surface) starts Explorer only when the session survives. No new mechanism; this removes one disposal on one branch.

  Do not use `ShutdownBlockReasonCreate` plus cancel: cancelling is the veto that produces the blocker screen. The existing 5 s SessionEnd budget matches the time Windows gives `WM_QUERYENDSESSION` before it shows that screen, so no new budget is added. Note for B119: no window may cancel `Closing` when `CloseReason` is `OSShutdown`, because a remaining window makes Avalonia set `e.Cancel` itself. No window cancels today.
- **Tests:** An OS end-session request runs the session shutdown once with reason SessionEnd and makes zero Explorer and zero Big Picture-close calls; use a fake session that records step calls. A SessionEnd arriving during a running Normal shutdown makes the running cleanup skip Explorer. A SessionEnd shutdown with a desktop host does not dispose it (the anchor is kept). Filter: `FullyQualifiedName~ApplicationShutdown`. Manual: M01-47 (sign-out, restart and shutdown from the overlay power menu, no blocker screen, no Explorer start); plus one attended Game Mode sign-out with an unsaved Notepad document, cancelled on the Windows blocker screen: the desktop comes back through the anchor.
- **Plan v2:** B006.
- **Related:** SESSION-V-001, SESSION-V-005, SESSION-032, U04A-LFA-020.

### SESSION-010: ShellSession is a 4,894-line partial aggregate that drives process statics

- **Severity:** high.
- **Where:** `src/WSGM/Shell/ShellSession*.cs` (nine files, about 75 fields); process statics driven at `ShellSession.cs:225-226,599` (`SteamUiTransportSession.SetEnabled/Attach`, `SteamInputShim.SetEnabled`), `SettingsPluginActions.Publish/Withdraw`, `SteamUiReadiness`, `LibraryBadges`, `GameModeReturnRecovery.Gate`.
- **Problem:** Every lifecycle, transition, reload, power and shutdown concern lives in one class whose partial files share mutable fields. No part can be tested without the whole session, and process statics keep state that outlives or races the session.
- **Best solution:** `ShellSession` becomes the composition root only: `ShellSession.cs` (fields, ctor, start), `ShellSession.Composition.cs` (owner construction and event wiring) and `ShellSession.Shutdown.cs` (the B140 step list). Concerns move to the owners in the review's section 4 table:
  - `SessionTransitions` (B124) gets the contents of `ShellSession.Modes.cs`.
  - `SessionPowerQueue` and `SessionConfigReloader` (B116) get `.Power.cs` and `.Config.cs`.
  - `SessionCommands` (B124) gets `.Actions.cs`.
  - `SteamUiCoordinator` (B136) gets `.SteamUi.cs`.
  - `PerformanceSessionWiring` and the library owners (B141) get `.Performance.cs` and `.GameLibrary.cs`.

  Each process static becomes an instance in its own domain batch (Steam UI readiness in B136, the shim in B076, recovery in B042). There is no DI container, no `SessionLifetime` state machine and no owner registry (critic conflict 1, D1). The session keeps `_startupTask`, one shutdown task and an admission flag.
- **Tests:** `SessionStartupTests` (an optional owner throwing leaves the session running) and the per-owner suites named in each batch. Filter for B124: `FullyQualifiedName~SessionTransitions|FullyQualifiedName~SessionStartup|FullyQualifiedName~BootTakeover|FullyQualifiedName~ExplorerReadiness`.
- **Plan v2:** B124 (owning batch), with the dissolution spread over B116, B136 and B141.
- **Related:** U05-LFB-004, U05-LFB-005; SESSION-011, SESSION-013.

### SESSION-011: No per-owner start isolation; a throwing constructor leaves the device runtime alive and uncleaned

- **Severity:** high (verifier raised it from medium and corrected the consequence).
- **Where:** `src/WSGM/Shell/ShellSession.cs:330-332` (`coordinatorAdopted = true` before `StartOnUiThread()`), `:347-456` (`StartOnUiThread`); throwing owners include `SettingsActivation` ctor (`SettingsActivation.cs:21-36`), `DesktopTray` asset load, `AudioManager.Start`, `SteamUiSessionHost` construction; `App.axaml.cs:91-95`.
- **Problem:** Any exception among about 50 owner constructions ends startup. Because the coordinator is adopted before `StartOnUiThread`, startup's `finally` does not dispose it. The forced `Shutdown(1)` then runs no session cleanup (V-001), so a device session that already hid the pad stays hidden. A direct Game Mode boot with a tray failure exits 1 with no cleanup instead of falling back to the desktop.
- **Best solution:** Two parts.
  1. In B006, the V-001 fix routes a startup failure through the runtime: `ObserveSessionStartupAsync` sets `_startupFailed` and calls `RequestExit()`. `ShellSession.ShutdownAsync` then disposes the adopted `_deviceCoordinator` and everything else that was built.
  2. In B124, the composition uses one helper, `TryStart(string name, bool essential, Action start)`. It logs and continues for optional owners (activation receivers, tray menu assets, audio, Steam UI host, feature services). It rethrows only for essentials: the message window and the desktop host. A tray creation failure on a direct Game Mode boot calls the existing "resume preserved desktop" path (`BeginDesktopModeAfterBootFailure`) and the session stays resident on the desktop.

  Keep `coordinatorAdopted` as is, since cleanup now owns the adopted coordinator. Do not add a registry or a state machine.
- **Tests:** `SessionStartupTests`: an optional owner throwing leaves the session running and logs once. An essential owner throwing fails startup, runs the cleanup once and exits 1. A tray create failure in direct boot ends on the resident desktop. Filter as in B124.
- **Plan v2:** B006 (cleanup on startup failure), B124 (isolation).
- **Related:** U05-LFB-003, PV11-003; SESSION-V-001, SESSION-002; plan claims C12, C13.

### SESSION-017: Config reload turns any load failure into live defaults

- **Severity:** high.
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:109` calling `ConfigStore.Load` (`src/WSGM/Core/ConfigStore.cs:122-139`, which returns `new AppConfig()` on any exception and calls `PreserveCorruptFile`).
- **Problem:** A corrupt, newer-schema or unreadable `config.json` seen by the watcher reload becomes a default config applied live. Device Integration switches off (AutoTDP stops and restores, device config applies "disabled"), all CEF surfaces retract and the master switch drops. A readable file can also be quarantined. The verifier confirmed this for corrupt and newer-schema files; the transient sharing-violation trigger is plausible, because a mutex timeout degrades to a read.
- **Best solution:** The config domain's B039 gives `ConfigStore.Read()` an outcome: Loaded, Absent, Corrupt or Unreadable, where only Loaded and Absent carry a config. There is no `UnsupportedSchema` outcome and no read-only mode (DECISIONS.md): a file written by a newer WSGM loads best effort as Loaded, keeping what this build understands. B039's tolerant enum converter turns unknown enum names into the undefined value instead of failing the parse, and unknown members are ignored, so such a file no longer falls to defaults. The reload path (moved into `SessionConfigReloader` by B116) applies a new snapshot only on Loaded, or on Absent, which maps to defaults exactly as a first start does. On Corrupt or Unreadable it keeps the last good snapshot, applies nothing and logs the outcome once per change.
  - Quarantine happens only for Corrupt, and once per distinct content (B039). Unreadable never quarantines.
  - Until B039 lands, B116's interim rule applies: any exception from a strict read keeps the last good snapshot.
- **Tests:** `SessionConfigReloaderTests`: an unreadable read keeps the snapshot and applies nothing; a corrupt read keeps the snapshot; a file with an unknown enum name and an unknown member (a newer WSGM's file) is applied as Loaded with the known values. B039's store tests cover quarantine once per content. Filters: B039 `FullyQualifiedName~Configuration|...` as listed in the batch; B116 `FullyQualifiedName~SessionConfigReloader`.
- **Plan v2:** B039 (store outcome), B116 (reloader).
- **Related:** U05-LFB-002; CONFIG-001 to CONFIG-012 group in B039; SESSION-018.

### SESSION-V-002: An exception in any UI-thread callback kills the resident shell through Panic, without device cleanup

- **Severity:** high (missed by the review, found by the verifier).
- **Where:** `src/WSGM/Program.cs:203-209,239-242,735-778` (no `Dispatcher.UIThread.UnhandledException` handler; Panic); `src/WSGM/Shell/DesktopTray.cs:44-49` (`Add` click handler without a guard).
- **Problem:** An exception from an input handler, a posted lambda or a native menu click unwinds `Dispatcher.MainLoop`. `StartWithClassicDesktopLifetime` then throws and `Panic` runs. Panic restores the shell registration, Explorer, display scale and the Steam Input lease, but not AutoTDP, the controller or the HidHide cloak. `wsgm.log` 2026-09-28 20:35:46 shows a `SettingsViewModel` constructor bug, reached through `DesktopTray.OpenSettings`, ending the resident session this way.
- **Best solution:** Three small changes in B006.
  1. `DesktopTray.Add` wraps the action: `item.Click += (_, _) => { try { action(); } catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Error($"Tray menu '{title}' failed", ex); } }`. This is the same guard `SettingsActivation` uses.
  2. In Shell mode, App subscribes once to `Dispatcher.UIThread.UnhandledException`. It logs the exception, sets `e.Handled = true` and calls `runtime.RequestExit()` with reason Normal, so the session ends through the full cleanup instead of dying in Panic.
  3. Panic is an exit path, so it turns the cloak off. In Shell mode, when the ledger file `Path.Combine(Log.Directory, "hidhide-ownership.json")` exists (WSGM hid something; the same rule B009 uses at start), it runs `Task.Run(() => new HidHideOwnership(new NativeHidHideControl(), new FileHidHideOwnershipStore(path)).ShowAsync(cts.Token)).Wait(ApplicationShutdownCoordinator.BudgetFor(SessionEnd))` inside a catch-all, as `RestoreHidHideForUninstallAsync` builds it. `Task.Run` keeps the wait off whatever synchronization context the dead dispatcher left on the main thread. Panic runs after the dispatcher stopped and must not marshal, so the async device stack is not used. This step is not in B006's text; add it there, because the never-strand rule requires cloak-off on every exit path. The next start reconciles anything else, and B009 shows the pad when no cycle runs.
- **Tests:** A UI-thread exception raised through a fake dispatcher seam requests the exit once and does not rethrow. A tray menu action that throws is logged and swallowed. Filter: `FullyQualifiedName~ApplicationShutdown`. Manual: open Settings from the tray with a forced constructor failure on a test build; the session stays up, or exits through cleanup with the pad visible.
- **Plan v2:** B006.
- **Related:** SESSION-044 (MessageWindow `WndProc` guard), SESSION-046 (Panic tray destroy), B009 (controller release), INPUT-V-004/DEVICE-005 (stale-ledger show at start).

## Medium

### SESSION-001: Startup flags are public mutable statics

- **Severity:** medium.
- **Where:** `src/WSGM/Program.cs:42-52` (`Mode`, `ServiceBoot`, `DesktopResident`), read by `App.axaml.cs:51-58` and `Panic` (`Program.cs:738,774`).
- **Problem:** Any code can read or change process mode flags, and tests cannot construct `App` or `ShellSession` with chosen options.
- **Best solution:** Add `StartupOptions` (`src/WSGM/StartupOptions.cs`) as an immutable record with `Mode`, `ServiceBoot`, `DesktopResident`, `Activate` and `Verbose`, and `static StartupOptions Parse(string[] args)`. `DecideMode`, `ArgumentValue`, `IsServiceBoot` and `HasVerboseFlag` move there. `Program` parses once and passes the options to `BuildAvaloniaApp`, then to `App(AppConfig, StartupOptions, ApplicationRuntime)` and `ShellSession`. Panic reads a private static copy that `Program` stores once, since Panic has no other way in. Delete the three public statics. The one-shots move to `StartupCommands` verbatim and in today's order. The early restore-shell path still runs before config, logging and Avalonia.
- **Tests:** `StartupOptionsTests` (today's `ModeSelectionTests` moved: precedence of `--shell`, service boot, `--desktop-resident`, `--activate`). Filter: `FullyQualifiedName~StartupOptions|FullyQualifiedName~ModeSelection`.
- **Plan v2:** B111.
- **Related:** U04A-LFA-017.

### SESSION-002: A startup failure must keep exit code 1 once the exit path runs the handler

- **Severity:** medium, latent. The verifier refuted the claim as a description of today: the forced `Shutdown(1)` skips the handler, so the process does exit 1. Plan v2 keeps it because the V-001 fix makes it real.
- **Where:** `src/WSGM/App.axaml.cs:94` (`desktop.Shutdown(1)`), `:141-144` (`Shutdown(ExitCodeFor(outcome))`), `:110-115` (second handoff report); `src/WSGM/Program.cs:226-237` (unconditional `CrashLoopBreaker.Reset()`).
- **Problem:** Once startup failure goes through the cleanup (V-001), a clean cleanup would overwrite exit code 1 with 0. The logon-service watchdog would then see a clean exit, and a shell that fails every sign-in would never trip the crash-loop breaker. Program also resets the breaker on every lifetime return in Shell mode, failed starts included.
- **Best solution:** In B006, the runtime keeps `_startupFailed` and computes the exit code as `_startupFailed ? 1 : ExitCodeFor(outcome)`. The handoff is reported exactly once, inside `RunAsync`. In B111, `Program` calls `CrashLoopBreaker.Reset()` only when startup did not fail (`!runtime.StartupFailed`), whatever the cleanup outcome. This deliberately narrows plan v2's "started and exit 0": an update whose 10 s cleanup times out exits 1 but is not a crash, and today's comment at the reset ("two update restarts plus a sign-in inside 2 minutes read as a crash loop", device-observed) is exactly the case an exit-code rule would bring back. `CrashLoopBreaker` becomes an instance over a directory and a clock in `Core/CrashLoopBreaker.cs`.
- **Tests:** B006: a startup failure followed by a clean cleanup exits 1 and reports the handoff once. B111: `CrashLoopBreakerTests` on a temp directory with a fake clock (the third start within 2 minutes loops; no reset after a startup failure; reset after a started session whose cleanup timed out). Filters: B006 `FullyQualifiedName~ApplicationShutdown`; B111 `FullyQualifiedName~CrashLoopBreaker|FullyQualifiedName~ApplicationRuntime`.
- **Plan v2:** B006 (sticky code), B111 (breaker reset rule).
- **Related:** U04A-LFA-013; plan claims C8, C21, C22; SESSION-V-001.

### SESSION-013: Session fields are written from worker threads without synchronization

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/ShellSession.Actions.cs:110-118` (`EnableDeviceIntegrationAsync` sets `_config.DeviceIntegration.Enabled` inside `Task.Run`); `ShellSession.cs:829-835` (library `updateSettings` writes `_config.GameLibrary` from the caller's thread); `ShellSession.Modes.cs:820-833` (`_pendingReturnLayout` written on the pool).
- **Problem:** The live `AppConfig` instance that UI-thread consumers read is mutated concurrently, and the in-place write is then superseded by the next reload, so state can flicker or be lost.
- **Best solution:** Every in-place mutation of the live snapshot happens on the UI thread; the store write stays where it is.
  - `EnableDeviceIntegrationAsync` keeps the store write on the pool (`ConfigStore.Update(c => { c.DeviceIntegration.Enabled = true; return true; })` after B039) and then sets the live flag with `await Dispatcher.UIThread.InvokeAsync(() => _config.DeviceIntegration.Enabled = true)`. The overlay prerequisites banner reads `_config.DeviceIntegration.Enabled` right after the click, so leaving the flag to the debounced reload would change what the user sees.
  - The library `updateSettings` callback keeps its read-your-write swap. `GameLibraryService` reads its sources back right after writing them (comment at `ShellSession.cs:829-831`), so waiting for the reload would break it. The swap assigns a fresh `GameLibraryConfig` read back from the store, a single reference write, not a field mutation; keep it and keep the comment.
  - `_pendingReturnLayout` is deleted (SESSION-016).

  This also covers the session part of CONFIG-040. No new snapshot type.
- **Tests:** In `SessionStartupTests` or the owner tests, enabling integration from a worker writes the store first and then sets the live flag on the (fake) UI dispatcher. Filter as B124.
- **Plan v2:** B124.
- **Related:** U05-LFB-019, CONFIG-040, SESSION-016.

### SESSION-018: About 30 reload apply steps run in one callback with no per-step isolation

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:110-157`.
- **Problem:** A throw in any apply step skips every later step and escapes to the dispatcher. After V-002 that path ends the session; before it, the shell dies in Panic.
- **Best solution:** `SessionConfigReloader` (new, `Shell/SessionConfigReloader.cs`) owns the watcher, the debounce (today's debounce generation only) and the outcome handling (SESSION-017). It calls one explicit ordered method `ShellSession.ApplyConfig(AppConfig)`, which runs each step through `TryApply(string name, Action step)`; `TryApply` logs a failure with the step name and continues. The step order stays exactly as today, plus `_modes.ApplyConfig` (SESSION-026). It also applies verbosity (with B041), routes profiles to `ProfileService.ReloadAsync` and keeps the `ApplyCommonPluginConfigAsync` task (CONFIG-046). A late watcher event after shutdown cannot recreate the timer, because the reloader refuses once its admission is closed (CONFIG-V-006). There is no subscriber registry.
- **Tests:** `SessionConfigReloaderTests`: a throwing step leaves later steps running; a stale debounce generation is dropped; no apply after close. Filter: `FullyQualifiedName~SessionConfigReloader`.
- **Plan v2:** B116.
- **Related:** U05-LFB-018, CONFIG-046, CONFIG-V-006, SESSION-017, SESSION-026.

### SESSION-019: Shutdown does not join the device power queue; about 15 fire-and-forget sites have no owner

- **Severity:** medium.
- **Where:** `_devicePowerWork` chain (`src/WSGM/Shell/ShellSession.Power.cs:208-209`), never referenced in `ShellSession.Shutdown.cs`. Unowned work: `NotifyPluginModeAsync` (4 sites), `Task.Run(OtherManagers.ReapplyAtStart)` (`ShellSession.cs:398`), `_sounds.RefreshAsync` (`:742`), reload `ApplyCommonPluginConfigAsync` (`ShellSession.Config.cs:211`), `ApplyDeviceConfigAndTargetAsync` (`:222`), `ApplyPerformanceConfig` (`:267`), two `Task.Run` in `ApplyCefMasterSwitch` (`ShellSession.SteamUi.cs:466,515`), `ApplySteamInputManagement` (`ShellSession.Config.cs:63`), `KickTabBootSync`, `RestoreSteamUiAfterBigPictureAsync`, `QueueDesktopActions`, `ReleaseAbandonedPackageExemptions`, `ObserveUiCaptureClaimAsync`, the `RepairAfterResume` RTSS refresh, and `ExplorerControl.StartExplorer`'s `Task.Run(VerifyAndRepairElevation)`.
- **Problem:** A lock or suspend transition in flight can still command the device while shutdown disposes it. The other sites can call owners that are being torn down.
- **Best solution:** Use admission rather than tracking every task (critic conflict 1, D1). Every owner exposes a synchronous `CloseAdmission()`, the single name everywhere (critic conflict 2), and refuses new calls afterwards, so late fire-and-forget work fails fast. The shutdown joins only four tasks:
  - the power queue tail (`SessionPowerQueue.WhenIdleAsync`, B116);
  - the startup task (SESSION-033);
  - the transition and boot worker;
  - the transport gate loop.

  Each join is bounded by the remaining deadline. `VerifyAndRepairElevation` is deleted by the launcher change (SESSION-037), and the tab boot sync becomes a tracked worker (SESSION-021). Do not add a generic task tracker.
- **Tests:** `SessionShutdownTests`: a pending power transition completes or is refused before the device step; owner calls after `CloseAdmission` are refused; a hung unrelated transition does not delay device safety. Filter: `FullyQualifiedName~SessionShutdown|FullyQualifiedName~ApplicationRuntime`.
- **Plan v2:** B140 (decided: D1, safety-first ordered steps under one deadline); the power queue extraction is in B116, the device `CloseAdmission` in B083.
- **Related:** U05-LFB-001, PV11-001, U04B-LFA-006; plan claims C4, C16, C32.

### SESSION-020: The Steam UI host outlives the device and GPU owners at shutdown; the master-gate wait is unbounded

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:139-191` (device and GPU stop) versus `:355-371` (Steam UI host), `:355` (`_cefMasterGate.WaitAsync()` without a timeout).
- **Problem:** Steam's QAM can call bridge commands into the host after the device coordinator, AutoTDP and GPU services it references are shut down. A wedged retraction holds the gate until transport timeouts expire.
- **Best solution:** In B136, `SteamUiCoordinator` and `SteamUiSessionHost` get `CloseAdmission()`. That is only the existing no-CEF prefix of `DisableAsync` (clear the switch flags, cancel in-flight requests), called at step 0 of the shutdown. Patch retraction and host disposal stay where they are today, after device cleanup. The gate wait takes the shutdown deadline: `await _gate.WaitAsync(remaining)`. If it times out, record a shutdown failure, skip the retraction and continue to transport disposal.
- **Tests:** `SteamUiCoordinatorTests`: admission closes before device disposal; no CEF call after transport close; a held gate does not block shutdown past the deadline. Filter: `FullyQualifiedName~SteamUiCoordinator|FullyQualifiedName~SteamUiSessionHost`.
- **Plan v2:** B136, with ordering in B140.
- **Related:** U05-LFB-020, STEAMHOST-001; plan claim C17.

### SESSION-022: Three interfaces and two forwarding adapters wrap one entry transaction

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/GameModeEntryServices.cs:17-88` (`IGameModeEntryServices`, 14 members), `:95-228` (`SessionModesEntryBackend`, a forwarder with null fallbacks); `src/WSGM/Shell/GameModeEntryTransaction.cs:36-110` (`IGameModeEntryBackend`, 15 members); `src/WSGM/Shell/DesktopReturnSequence.cs:8-17` (`IDesktopReturnBackend`) and `SessionModes.cs:826-927` (`DesktopReturnBackend`).
- **Problem:** Every entry call passes through two adapters and default interface methods. The null fallbacks ("This session cannot change displays", `Task.Run(DisplayLayouts.Observe)`) are unreachable, because entry only runs when `_desktopHost` exists, and that is exactly when the services are set (`ShellSession.cs:641-649`, `SessionModes.cs:444-449`).
- **Best solution:** Delete `IGameModeEntryServices` and `SessionModesEntryBackend`. Add `Shell/SessionEntryBackend.cs`, one class that implements `IGameModeEntryBackend` and `IDesktopReturnBackend`. It takes the 14 members of today's `ShellGameModeEntryServices` (`ShellSession.Modes.cs`) plus `PrepareExplorerExitAsync`, `ExitExplorerAndWaitAsync`, `ReturnToDesktopAsync`, `RequestBigPictureAsync`, `CommitGameModeAsync` and `ApplyDefaultPostureAsync`. Remove the default interface implementations (`RestorePendingReturnAsync` in both interfaces) and implement them once. Delete the null fallbacks. `GameModeEntryTransaction` and `DesktopReturnSequence` keep their logic unchanged.
- **Tests:** `GameModeEntryTransactionTests` run over the real `DesktopReturnSequence` behind a fake `IDesktopReturnBackend` (SESSION-059). Filter: `FullyQualifiedName~GameModeEntryTransaction|FullyQualifiedName~SessionModes|FullyQualifiedName~DesktopReturnSequence`.
- **Plan v2:** B115.
- **Related:** SESSION-059, SESSION-063; plan claim C34.

### SESSION-030: ShellSession.ShutdownAsync is 600 lines of repeated try/catch/finally blocks and disposes UI-affine owners on the pool

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:92-611`; the thread hop happens after `await _profileFanOut.DisposeAsync().ConfigureAwait(false)` (`:96`).
- **Problem:** Each step repeats `try { ... } catch (Exception ex) when (...) { RecordShutdownFailure(...) } finally { _x = null; }`. After the first `ConfigureAwait(false)`, `_steamGraphics`, `_chordMirror`, `_audio`, `_radios`, `_themes` and `_drives` (with its MessageWindow registration) are disposed off the UI thread, while `DisposeUiOwnedSessionResources` is marshalled.
- **Best solution:** One private helper, `Task RunStepAsync(string name, bool ui, Func<TimeSpan, ValueTask> step)`. It computes the remaining time to the deadline, runs the step on the dispatcher when `ui` is true (`Dispatcher.UIThread.InvokeAsync`) and elsewhere otherwise, catches and records failures with the step name, and never skips a later step. `ShutdownAsync` becomes the fixed list from B140:
  0. Record the reason and sticky SessionEnd; call every `CloseAdmission()`.
  1. Join the power queue tail and the startup task.
  2. AutoTDP restore.
  3. Device shutdown.
  4. Common plugins and GPU.
  5. Transition, boot worker and gate loop.
  6. Tray retire and verify.
  7. Explorer restore only when the reason allows and the tray is gone.
  8. Steam host retraction and transport.
  9. Feature owners on the UI thread.
  10. Native providers.
  11. MessageWindow last.

  This removes about 350 lines. It is a behaviour change, because UI-affine disposals move to the dispatcher (verifier correction), so it needs the shutdown manual rows.
- **Tests:** `SessionShutdownTests` with fake owners and a fake clock: order; every step attempted after an earlier failure; UI-tagged steps run on the fake dispatcher; repeated shutdown returns the same task. Filter: `FullyQualifiedName~SessionShutdown`.
- **Plan v2:** B140 (decided: D1, safety-first ordered steps under one deadline).
- **Related:** plan claims C14, C15; SESSION-019, SESSION-033, SESSION-043.

### SESSION-034: Explorer exit, restore and anchor orchestration has no seams and no behavioural tests

- **Severity:** medium.
- **Where:** `src/WSGM/Core/ExplorerControl.cs` (static over `NativeMethods` and `Process`); `src/WSGM/Core/ExplorerDesktopHost.cs:21,132,408` (constructs `DesktopAppLifecycle`, `ExplorerShellAnchor.StartAsync` and `UnelevatedLauncher` directly).
- **Problem:** The code that decides whether the user gets a desktop back cannot be tested without a live Explorer.
- **Best solution:** One internal port, `IExplorerNative`: find the taskbar or shell window, get the window owner pid, post a message, close a process's windows, wait for process exit and read its exit code. Image identity goes through `NativeShellProcess.TryGetImagePath`. `ExplorerDesktopHost` takes `(IExplorerNative, Func<anchor start> anchorFactory, ExplorerLauncher, DesktopAppLifecycle, int sessionId)`. The exit loop and retired-shell state move into the host (SESSION-038). `ExplorerControl` splits into `ExplorerShellProbe` (SESSION-035) and `ExplorerLauncher` (SESSION-037). The production native implementation is a thin wrapper over today's calls, with no behaviour change.
- **Tests:** `ExplorerDesktopHostTests` with a fake native:
  - clean exit;
  - unclean exit waits for the respawn;
  - a replacement shell is asked to exit once;
  - a lingering retired shell is asked to close and is never terminated;
  - anchor Dispatched, NotDispatched and Unknown;
  - the scheduler fallback is refused when a surface appeared;
  - disposal refuses new work.

  Filter: `FullyQualifiedName~Explorer|FullyQualifiedName~DesktopAppProcess`.
- **Plan v2:** B112.
- **Related:** U04B-LFA-008; SESSION-035 to SESSION-042 (SESSION-040 is no-change).

### SESSION-035: IsDesktopShellRunning uses Process.MainModule

- **Severity:** medium.
- **Where:** `src/WSGM/Core/ExplorerControl.cs:82` (`IsDesktopShellRunning`), `:222` (`ExitExplorerAndWait`); `src/WSGM/Interop/NativeShellProcess.cs:65-81` (`TryGetImagePath` already exists); critic CRIT-004: `src/WSGM/Core/DesktopAppProcessBackend.cs:31,83`.
- **Problem:** `MainModule` throws for a higher-integrity Explorer when WSGM runs at medium integrity. The probe then reports "no desktop", so `TrayHost.Create` competes with a live taskbar. It also enumerates modules on every UI-thread call (overlay swipes, layout, Settings). CRIT-004 applies the same class of defect to `DesktopAppProcessBackend`: from a medium WSGM, an elevated integration throws `Win32Exception` and fails the whole capture for every rule.
- **Best solution:** Add `Core/ExplorerShellProbe.cs`, a static over `NativeShellProcess`. `IsDesktopShellRunning()` finds `Shell_TrayWnd`, reads its owner pid, checks the current session (`IsCurrentSessionWindow`) and compares `NativeShellProcess.TryGetImagePath(pid)` against `ExplorerPath`, ignoring case. A limited query (`PROCESS_QUERY_LIMITED_INFORMATION`) works across integrity levels. It stays the authority for "is the desktop up" (SESSION-012 refuted). `ExplorerPath` and `IsCurrentSessionWindow` move with it; the existing callers (`Program`, `TrayHost.Create`, `ShellSession`, `GameModeReturnRecovery`, `SettingsViewModel.System`, `OverlayController` and its partials) change only the type name. `DesktopAppProcessBackend` uses the same image-path query for capture and PID-reuse identity. Its elevated `RestartAsync` catches a declined-UAC `Win32Exception` as a refusal, and its poll honours the caller's cancellation token (CRIT-004). `Kill(true)` there applies only to the listed integrations, never Explorer.
- **Tests:** `ExplorerShellProbe` over a fake image query (Explorer path matches, foreign owner, query failure means false). `DesktopAppProcessBackendTests` (an elevated process is captured by path, a declined UAC is a refusal, cancellation stops the poll). Filter: `FullyQualifiedName~Explorer|FullyQualifiedName~DesktopAppProcess`.
- **Plan v2:** B112.
- **Related:** U04B-LFA-010, CRIT-004; plan claim C28.

### SESSION-037: Terminal recovery paths use a second Explorer launch policy that can start a competing shell

- **Severity:** medium.
- **Where:** `src/WSGM/Core/ExplorerControl.cs:91-190` (`StartExplorer`, `StartExplorerAndVerify`, `StartExplorerCore`, `VerifyAndRepairElevation`); callers `src/WSGM/Program.cs:286` (restore-shell), `:488` (crash-loop disarm), `:764` (Panic); the session path is `src/WSGM/Core/ExplorerDesktopHost.cs:327-442`.
- **Problem:** Terminal recovery starts Explorer with ShellExecute from a possibly elevated process. A fire-and-forget or blocking loop then verifies the elevation, asks an elevated Explorer to exit, restarts it via the scheduler, and otherwise starts it elevated. The bool scheduler wrapper treats Unknown as failure, so a second shell can start. The session path already uses anchor, then scheduler, with a tri-state result.
- **Best solution:** Add `Core/ExplorerLauncher.cs` with one policy, used by both `ExplorerDesktopHost` and the terminal paths:
  - Not elevated: start `explorer.exe` directly.
  - Elevated: dispatch through the de-elevation scheduler (`UnelevatedLauncher`, made tri-state by U04B-LFA-001 in the same batch).
  - Dispatched: wait for the shell probe up to today's timeout.
  - NotDispatched: start Explorer directly, accept the elevated shell as the last resort and log it, as today.
  - Unknown: start nothing else and only wait for the probe.

  Delete `VerifyAndRepairElevation` and the async `StartExplorer` variant. The repair loop only existed because elevated ShellExecute came first; with the scheduler first, an elevated Explorer only appears when the scheduler is unavailable, and then the repair cannot work either. Nothing terminates Explorer. Gate acceptance on one attended restore-shell and crash-loop pass on the notebook and the Claw (R7).
- **Tests:** `ExplorerLauncherTests` with a fake scheduler and starter: not elevated starts directly; Dispatched starts nothing else; NotDispatched starts directly once; Unknown starts nothing. Filter: `FullyQualifiedName~Explorer`. Manual: M01-46 and the crash-loop disarm row.
- **Plan v2:** B112.
- **Related:** U04B-LFA-001, U04B-LFA-006, SESSION-055, SESSION-019; review R7.

### SESSION-045: TrayHost reports retirement even when DestroyWindow failed

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/TrayHost.cs:80-100` (`Dispose`); `src/WSGM/Shell/ShellSession.Shutdown.cs:274-297,777-782`.
- **Problem:** A failed `DestroyWindow(Shell_TrayWnd)` is only logged, `_trayHwnd` is zeroed, and the session treats the tray as retired. It then restores Explorer while WSGM's taskbar window still exists, which is the coexistence the tray contract forbids.
- **Best solution:** Replace `Dispose()` with `bool Retire()`. It must run on the thread that created the window (`Dispatcher.UIThread.CheckAccess()`; off-thread it returns false without touching the window). It calls `DestroyWindow`, then returns `!IsWindow(hwnd)` and keeps `_trayHwnd` when the window still exists. The shutdown step 6 uses this bool as `trayRetired`. When it is false, the session keeps the anchor and skips its own Explorer restore, and the anchor restores Explorer after the process exits (today's `desktopVerified` branch, `ShellSession.Shutdown.cs:283-297`). `DestroyActive` becomes the Panic helper in SESSION-046.
- **Tests:** A `TrayHost` retire test through a fake window API (destroy fails, so false and the handle is kept). `SessionShutdownTests`: an unverified tray keeps the anchor and makes zero Explorer calls. Filter: `FullyQualifiedName~Tray|FullyQualifiedName~SessionShutdown`.
- **Plan v2:** B114 (Retire), B140 (use in the step list).
- **Related:** U05-LFB-015, PV11-014; plan claim C24; SESSION-046.

### SESSION-056: Steam is a 474-line static mixing discovery, process control, input shortcuts and update stop

- **Severity:** medium.
- **Where:** `src/WSGM/Core/Steam.cs`; consumers `SessionModes.cs` (10 uses), `BootSplash.cs:151`, `SteamMonitor.cs:80`, the boot takeover, and about 20 discovery-only consumers listed in the review's section 4.
- **Problem:** Transitions, the splash and the monitor reach Steam only through process-table statics, so none of them can be tested without a live Steam.
- **Best solution:** Split by role:
  - `SteamInstallation` (static, pure discovery): `ExePath`, `InstallDirectory`, `LibraryFoldersConfigPath`, `IsInstalled`, `RequiresElevatedShell`, `ResolveExePath`, the compatibility-layer checks and `TryReadLibraryFolders`.
  - `SteamSessionControl : ISteamSessionControl` (instance, injected into `SessionModes`, `SteamMonitor`, `BootSplash` and `SessionTransitions`): `IsRunning`, `IsBigPictureVisible`, `FindBigPictureWindow`, `LaunchBigPicture`, `LaunchDesktop`, `ColdStart`, `StopForUpdate`, `CurrentSessionProcesses`, plus `SessionModes.ExitBigPicture`, `ExitBigPictureAndSettleAsync` and `WaitForBigPictureToCloseAsync`.
  - `BigPictureShortcuts` (static): `TrySend`, `ShortcutVirtualKey` and the enum.

  `IsRunning` keeps today's semantics, counting `steam.exe` and `steamwebhelper`. `ColdStart`'s shim step stays gated on both names (SESSION-054 needs log evidence first). Discovery-only consumers change their type name only.
- **Tests:** `SteamSessionControlTests` over a fake process query (both names count as running; `ColdStart` runs the shim only when neither runs). Filter: `FullyQualifiedName~SteamSessionControl|FullyQualifiedName~SessionModes`.
- **Plan v2:** B115.
- **Related:** SESSION-054, SESSION-055; review R-table row A6.

### SESSION-059: The entry transaction tests use a fake that does the sequence's work

- **Severity:** medium.
- **Where:** `tests/WSGM.Tests/Shell/GameModeEntryTransactionTests.cs:420-434` (the fake `ReturnToDesktopAsync` applies the layout, runs leave actions and clears the record), assertions at `:125-127,152`.
- **Problem:** The tests assert behaviour that the fake implements, so a regression in `DesktopReturnSequence` would pass.
- **Best solution:** The tests construct the real `DesktopReturnSequence` behind a fake `IDesktopReturnBackend` that only records calls, through `SessionEntryBackend` or a minimal test implementation of `IGameModeEntryBackend` whose `ReturnToDesktopAsync` delegates to the real sequence. The assertions move to what the real sequence did: layout applied, leave actions compensated, record cleared once through `ClearIfUnchanged`.
- **Tests:** Rewrite these cases as described. Filter: `FullyQualifiedName~GameModeEntryTransaction|FullyQualifiedName~DesktopReturnSequence`.
- **Plan v2:** B115.
- **Related:** U05-LFB-021, SESSION-022.

### SESSION-060: Transition and power state machines are tested only through pure predicates

- **Severity:** medium.
- **Where:** `tests/WSGM.Tests/Shell/SessionModesTests.cs` (four trivial cases), `SystemPowerTransitionTests` (only `IsStaleSuspend`). No tests exist for `ShutdownAsync` order, `QueueDevicePowerTransition`, reload, `TrayHost`, `MessageWindow`, `SteamMonitor`, `BootSplash`, `GameModeReturnRecovery`, `SessionActivation` or `SettingsActivation`.
- **Problem:** The orchestration that decides whether the user ends on a desktop with a working controller has no regression net.
- **Best solution:** Each extraction batch adds tests over its owner with fakes, temp directories and disposable names. None touches Explorer, Steam, the tray or the real config.
  - B115 `SessionModesTests`: a desktop request during entry is honoured once; a refusal unpauses the monitor; shutdown refuses new transitions.
  - B116 `SessionPowerQueueTests`: lock plus suspend coalesce; an opposite queued edge cancels; a faulted cycle is repaired on resume; a stale suspend is dropped; stop refuses and joins.
  - B124 `SessionTransitionsTests`.
  - B140 `SessionShutdownTests`.
  - B141 `BootSplashTests`.
- **Tests:** As listed, with filters from each batch.
- **Plan v2:** B115 (owning id); B116, B124, B140, B141 add their parts.
- **Related:** U05-LFB-006, U05-LFB-024.

### SESSION-V-003: The plan and review designed the new runtime around the event that never fires

- **Severity:** medium (missed by the review, found by the verifier).
- **Where:** `refactor-plan.md:59` ("App forwards lifetime events"); session.md section 4 (`ApplicationShutdownRequest.ShutdownLifetime` became an injected `Action<int>`; "App forwards `ShutdownRequested` to the runtime"); the planning-corrections B3 cutoffs.
- **Problem:** Every shutdown improvement (R1, B3, SESSION-B7) reorders cleanup that no programmatic exit reaches today. Tests with a fake lifetime that calls back would pass while production never cleans up.
- **Best solution:** Make the design rule explicit and enforce it in code. `ApplicationRuntime` owns the exit sequence: session cleanup first, then the forced lifetime exit. `ShutdownRequested` is only the OS end-session input, mapped to SessionEnd (SESSION-007). The one-line trigger fix ships first and on its own (B006), ahead of the refactor. B111 then moves it into `Core/ApplicationRuntime.cs` and B140 only reorders steps. Every runtime test uses a fake `forcedExit` that never calls back into the handler.
- **Tests:** The B006 fake-lifetime tests (see SESSION-V-001). B140 adds the seam tests: tray Exit, update and restore-shell each reach `ShellSession.ShutdownAsync` exactly once; an OS end-session request is SessionEnd with zero Explorer calls.
- **Plan v2:** B006, with B111 and B140 building on it.
- **Related:** SESSION-V-001, SESSION-007; plan claims C6, C19; critic conflict 1.

## Low

### SESSION-004: The update pre-stop blocks the UI thread for up to 5 s

- **Severity:** low.
- **Where:** `src/WSGM/Program.cs:511-519` (`RequestInstallerExit` posts `RunInstallerExitRequest(..., Steam.StopForUpdate, ...)` to the dispatcher); `src/WSGM/Core/Steam.cs:382-447` (`StopForUpdate` sleeps in 250 ms steps, then stops launch wrappers).
- **Problem:** While the pre-stop sleeps on the UI thread, tray `WM_COPYDATA`, session-change and power messages and the boot splash are not pumped. The residual of the refuted SESSION-003 is folded in here: a Settings or overlay-test process also wakes on the manual-reset update event and runs `Steam.StopForUpdate`, a duplicate `steam://exit`.
- **Best solution:** `RequestInstallerExit` runs the pre-stop synchronously on the watcher's callback thread inside `try/finally`. The `finally` posts `Request(reason)` and `runtime.RequestExit()` to the dispatcher, so a failed pre-stop can never prevent cleanup. That keeps today's contract in `RunInstallerExitRequest`. Only Shell mode passes `Steam.StopForUpdate` (later `SteamSessionControl.StopForUpdate`). Other modes pass a no-op and simply exit on the event without a handoff. Uninstall still never stops Steam.
- **Tests:** `RunInstallerExitRequest` ordering tests stay. Add: a throwing pre-stop still requests the exit; the Settings-mode wiring passes no pre-stop. Filter: `FullyQualifiedName~ApplicationRuntime|FullyQualifiedName~ApplicationShutdown`.
- **Plan v2:** B111.
- **Related:** SESSION-003 (refuted, residual here), SESSION-V-001.

### SESSION-005: --restore-shell waits up to 45 s for every WSGM process, Settings included

- **Severity:** low.
- **Where:** `src/WSGM/Core/UpdateExitWatcher.cs:174-205` (`RequestResidentShellExit` polls `WindowFinder.FindProcessIds("WSGM")` until only itself remains); `src/WSGM/Program.cs:282` (45 s).
- **Problem:** A separate Settings or `--overlay-test` process does not watch the restore event, so the user's desktop recovery waits the full 45 s.
- **Best solution:** After signalling, poll at the same 200 ms until the shell mutex `Local\WSGM.Shell` no longer exists. That is the same test setup uses in `WindowsSetup.ShellRunning` (`Mutex.TryOpenExisting`). An `UnauthorizedAccessException` from an elevated owner counts as "still running". The 45 s timeout and the call order stay unchanged. The name comes from the linked `SessionProtocolNames` (B031). No completion event or new mechanism is needed.
- **Tests:** The poll predicate as a small function taking a `Func<bool> shellAlive` (returns at once when absent; times out when held). Filter: `FullyQualifiedName~StartupCommands|FullyQualifiedName~UpdateExitWatcher`.
- **Plan v2:** B111.
- **Related:** U04A-LFA-015, PV10-010, SESSION-047.

### SESSION-006: ApplyLogVerbosity guards a read that cannot throw

- **Severity:** low.
- **Where:** `src/WSGM/Program.cs:685-705` (`ApplyLogVerbosity`).
- **Problem:** The `try/catch` and its "fell back" warning are dead mechanism.
- **Best solution:** Delete the `try/catch`. Compute the verbosity from `StartupOptions.Verbose` and `config` and call `Log.SetVerbosity` once.
- **Tests:** None needed; build only.
- **Plan v2:** B111.
- **Related:** U04A-LFA-018.

### SESSION-009: MainAsync and the shutdown handler have no behavioural tests

- **Severity:** low.
- **Where:** `tests/WSGM.Tests/App/ModeSelectionTests.cs`, `tests/WSGM.Tests/Core/ApplicationShutdownTests.cs:233-266` (only `DecideMode`, `IsServiceBoot` and `RunInstallerExitRequest` are covered).
- **Problem:** The existing `RunInstallerExitRequest` tests passed while production never reached the cleanup (V-001), because their fake stands in for the call that fails.
- **Best solution:** B006's runtime takes `sessionShutdown`, `forcedExit` and `reportHandoff` delegates, so the exit sequence is testable without Avalonia. Add the tests listed under SESSION-V-001 and SESSION-007. B111 moves them to `ApplicationRuntimeTests` with an instance runtime, so no test mutates the process-global reason.
- **Tests:** As in SESSION-V-001. Filter: `FullyQualifiedName~ApplicationShutdown|FullyQualifiedName~ModeSelection`.
- **Plan v2:** B006.
- **Related:** U04A-LFA-006, PV10-005, SESSION-062.

### SESSION-014: Dead or redundant wiring in ShellSession

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ShellSession.cs:313` (`_commonPluginStartup is not null`, always true there); `:712,714` (`_drives.CardWatcher = _cardAcfWatcher` assigns null before card services exist); `:1057,1072,1087` (`_overlayTestOnly ? null : _resolutions` inside a `!_overlayTestOnly` block); `ShellSession.Shutdown.cs:76` and `:678-682` (`_overlay` disposed twice).
- **Problem:** Dead branches mislead readers and the double dispose hides real ordering.
- **Best solution:** Delete the always-true check. Assign `CardWatcher` where card services are created (or let the storage owner take it by constructor after B136). Replace the conditional with `_resolutions`. Keep only the first `_overlay` dispose (admission close at step 0) and delete the second block.
- **Tests:** Build; B124 filter.
- **Plan v2:** B124.
- **Related:** U05-LFB-025, PV11-020.

### SESSION-021: KickTabBootSync can cancel a disposed source and leaks completed ones

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ShellSession.SteamUi.cs:316-353` (`KickTabBootSync`, `RunTabBootSyncAsync`).
- **Problem:** The previous run's `finally` (on the pool) can dispose the source between the UI thread's swap and its `Cancel()`, which then throws `ObjectDisposedException`. A source whose run finished while still current is cancelled later but never disposed.
- **Best solution:** In B136, the tab boot sync becomes one worker owned by `SteamUiCoordinator`. It is started once, waits on a reschedulable signal (a `SemaphoreSlim(0, 1)` released by `Kick`; a release when already signalled throws `SemaphoreFullException`, which `Kick` catches and ignores), runs the sync when signalled, and stops on the coordinator's own token at shutdown. There is no CTS per kick, so there is nothing to swap or dispose, and the coordinator joins the worker in its stop.
- **Tests:** `SteamUiCoordinatorTests`: two kicks while running cause one more pass; a kick after stop is ignored; stop joins the worker. Filter: `FullyQualifiedName~SteamUiCoordinator`.
- **Plan v2:** B136.
- **Related:** U05-LFB-009, STEAMHOST-011.

### SESSION-023: Failed desktop recovery throws across the handlers and shows the wrong, duplicated warning

- **Severity:** low.
- **Where:** `src/WSGM/Shell/GameModeEntryTransaction.cs:129-138` (`RecoverAsync`), `:271-281` (catch handlers), `:313-322` (`RecoverDesktopAsync` throws `InvalidOperationException(ExplorerDesktopPendingWarning)`); `src/WSGM/Shell/SessionModes.cs:378-386,480-491`.
- **Problem:** When called from the catch handlers, the throw escapes `RunAsync` and `SessionModes` shows `ExplorerExitFailedWarning`. From the normal path it is rewrapped as "Game Mode entry failed: ...". `ReturnToDesktopAsync` has already shown `ExplorerDesktopPendingWarning` itself.
- **Best solution:** `RecoverDesktopAsync` and `RecoverAsync` return `Task<bool>` and never throw for a failed restore. Each call site returns its normal result (Failed, Cancelled or DesktopPreserved) with `Warning: null` when recovery failed, because the return path already showed the pending-desktop warning once. The catch-all in `SessionModes` stays for real bugs only. No new result field.
- **Tests:** `GameModeEntryTransactionTests`: a failed recovery from the cancel handler returns Cancelled without a warning and without throwing; the user sees exactly one pending-desktop warning. Filter: `FullyQualifiedName~GameModeEntryTransaction`.
- **Plan v2:** B115.
- **Related:** SESSION-022.

### SESSION-024: A refusal before any change leaves the Steam monitor paused

- **Severity:** low.
- **Where:** `src/WSGM/Shell/GameModeEntryTransaction.cs:143-147` (pending record not restorable returns Failed without recovery); `src/WSGM/Shell/SessionModes.cs:466` (`_monitor.Paused = true`), `:492-508`.
- **Problem:** Only `ReturnToDesktopAsync` unpauses the monitor, so after this refusal `SteamMonitor` suppresses `SteamStarted` and `SteamExited` on the desktop.
- **Best solution:** Unpause exactly at the one refusal that skips the desktop return. Every other non-Entered path in `GameModeEntryTransaction.RunAsync` goes through `RecoverAsync`, and `SessionModes.ReturnToDesktopAsync` already sets `_monitor.Paused = !restored` there; that pause must stay when the desktop could not be restored, so a blanket unpause in the worker's `finally` would be wrong. The backend's `RestorePendingReturnAsync` (in `SessionEntryBackend` after SESSION-022) posts `_monitor.Paused = false` to the UI thread before it returns false. No new result field and no new state.
- **Tests:** `SessionModesTests` or `GameModeEntryTransactionTests`: a pending-record refusal leaves the monitor unpaused; a failed recovery after an Explorer exit leaves it paused. Filter: `FullyQualifiedName~SessionModes|FullyQualifiedName~GameModeEntryTransaction`.
- **Plan v2:** B115.
- **Related:** U05-LFB-007.

### SESSION-025: A desktop request racing the end of entry can be lost or fire later

- **Severity:** low.
- **Where:** `src/WSGM/Shell/SessionModes.cs:322-327` (UI reads `_entryCancellation`), `:492-508` (the worker nulls it and resets `_desktopRequested`).
- **Problem:** `_entryCancellation` is a plain field written by the worker and read on the UI thread. A request can be dropped, or left set so the next successful entry immediately posts `EnterDesktopMode`.
- **Best solution:** Make both fields UI-thread-only instead of adding a type. `EnterGameMode` (UI thread) sets `_entryCancellation` and resets `_desktopRequested = 0`. The worker's `finally` keeps `EndTransition()` and the settled callbacks, then posts one UI-thread callback that clears `_entryCancellation`, disposes the CTS, reads and resets `_desktopRequested`, and calls `EnterDesktopMode()` when the flag was set and the entry ended Entered. The desktop request path, the splash Cancel button and `RequestShutdown` (shutdown step 0) all call `CancelGameModeEntry` on the UI thread, so they see either the live CTS (and the posted callback honours the flag) or null (and the request starts its own transition). Both stay plain fields; `Interlocked` on `_desktopRequested` goes. No new class, lock or phase enum.
- **Tests:** `SessionModesTests`: a desktop request during entry is honoured exactly once; a request after the attempt ends does not leak into the next entry. Filter: `FullyQualifiedName~SessionModes`.
- **Plan v2:** B115.
- **Related:** U05-LFB-008.

### SESSION-026: SessionModes receives configuration only through the overlay controller

- **Severity:** low.
- **Where:** `src/WSGM/Overlay/OverlayController.cs:385` (`_modes.ApplyConfig`).
- **Problem:** Mode settings depend on the overlay's reload path and lifetime.
- **Best solution:** The session's ordered `ApplyConfig` (SESSION-018) calls `_modes.ApplyConfig(config)` directly as one `TryApply` step, in today's position relative to the overlay apply. Delete the forwarding line in `OverlayController.ApplyConfig`.
- **Tests:** `SessionConfigReloaderTests` include the modes step. Filter: `FullyQualifiedName~SessionConfigReloader`.
- **Plan v2:** B116.
- **Related:** U05-LFB-028, plan claim C11.

### SESSION-027: GameModeReturnRecovery reports completion while leaving the record and clears it two ways

- **Severity:** low.
- **Where:** `src/WSGM/Shell/GameModeReturnRecovery.cs:28-50,92-101,145-167`; `src/WSGM/Shell/ShellSession.Shutdown.cs:846-872`.
- **Problem:** The class is a static with a static `SemaphoreSlim` gate and hard-wired `ConfigStore`/`DisplayLayouts`. It reports complete when Explorer is not up but keeps the record. Shutdown fingerprints and clears it a second way with identical code. `RestorePendingAsync` returns on cancellation while the gated work continues.
- **Best solution:** Follow the config domain's B042. Rename to `DesktopReturnRecovery(store, applyLayout, audio)` as an instance. `RestoreAsync` returns `{Restored, Pending, Unreadable}` and never clears. `ClearIfUnchanged(fingerprint)` is called explicitly at every site that relied on implicit clearing: the Game Mode entry path (including a non-Custom launch without GameAudio), restore-shell, Panic and shutdown. Use a source-generated fingerprint. Cancellation stops waiting without a second clear.
- **Tests:** Layout success plus audio failure keeps the record; clear only when unchanged; cancellation stops without a second clear; the entry path clears once. Filter: `FullyQualifiedName~DesktopReturnRecovery|FullyQualifiedName~AudioProfileService|FullyQualifiedName~GameModeEntryTransaction`.
- **Plan v2:** B042.
- **Related:** U05-LFB-017, CONFIG-024 to CONFIG-026, SESSION-016.

### SESSION-029: Desktop actions build their own sequence, swallow cancellation and re-read config leniently

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ShellSession.Modes.cs:630-648` (`RunDesktopActionsAsync`), `:605` and `:753` (`ReadLaunch` and `RunDesktopActionsAsync` call the lenient `ConfigStore.Load`).
- **Problem:** Shutdown cancellation is swallowed like any other failure, and a lenient read can run desktop actions from defaults after a bad read.
- **Best solution:** `SessionTransitions` holds one `PluginActionSequence` (no new `SessionActionRunner`, plan claim C9) used for entry and desktop actions. It catches `OperationCanceledException` only when the session token is cancelled, and logs other failures per step as today. Launch and desktop-action settings come from the session's current snapshot (the reloader's last good config), not from a fresh lenient load.
- **Tests:** `SessionTransitionsTests` or `SessionModesTests`: desktop actions stop on shutdown cancellation; actions use the snapshot. Filter: `FullyQualifiedName~SessionModes|FullyQualifiedName~SessionTransitions`.
- **Plan v2:** B115.
- **Related:** U05-LFB-027, SESSION-015.

### SESSION-031: The shutdown entry is a non-atomic check-then-set, with a second entry path

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:33-39` (`if (_disposed) return; _disposed = true;`); `ShellSession.cs:237-243` (`DisposeAsync`, SESSION-008).
- **Problem:** Two concurrent callers can both pass the check, and the unused `DisposeAsync` builds its own budget.
- **Best solution:** `ShutdownAsync` becomes `Task ShutdownAsync(...) => _shutdown ??= RunShutdownAsync(...)`, called only on the UI thread (the runtime posts there), so the null-coalescing assignment is safe without a lock. `_disposed` stays only as a read-only "stopping" admission check for callbacks and is set as the first line of `RunShutdownAsync`. `DisposeAsync` is deleted.
- **Tests:** `ApplicationRuntimeTests` or `SessionShutdownTests`: repeated shutdown returns the same task. Filter: `FullyQualifiedName~ApplicationRuntime|FullyQualifiedName~SessionShutdown`.
- **Plan v2:** B111.
- **Related:** U05-LFB-029, PV11-024, SESSION-008.

### SESSION-032: A reason escalation during a running shutdown is dropped

- **Severity:** low.
- **Where:** `src/WSGM/App.axaml.cs:103-109` (a request while `_shutdownInProgress` is only cancelled; the reason is consumed once).
- **Problem:** An Update or SessionEnd arriving after a Normal shutdown started never reaches the running cleanup. That cleanup may then restore Explorer during session end, or skip the installer handoff.
- **Best solution:** `ApplicationShutdownRequest.Request` keeps its priority rule and, in addition, sets a sticky `SessionEnding` flag on any SessionEnd request. `Consume()` becomes a non-destructive `Current` read. The running session reads `Current` and `SessionEnding` right before the Big Picture exit step and the Explorer step, and skips both when the session is ending. An escalation only tightens the outer deadline: the coordinator waits on a `CancellationTokenSource`, and escalation calls `CancelAfter(remainingForNewReason)` only when that is earlier. The handoff reports the final reason. B111 moves these fields into the runtime instance.
- **Tests:** Normal then SessionEnd mid-shutdown: Explorer is skipped and the deadline tightens. Normal then Update: the handoff reports Update once. A later Normal never extends the deadline. Filter: `FullyQualifiedName~ApplicationShutdown`.
- **Plan v2:** B006.
- **Related:** U04A-LFA-020, PV10-017, SESSION-V-005, SESSION-007.

### SESSION-033: Shutdown first awaits the startup task without a bound

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:47-60`; startup holds `GameModeReturnRecovery` (own 15 s budget, checks cancellation only between operations, `GameModeReturnRecovery.cs:145-167`) and `DeviceCoordinator.TryStartAsync`.
- **Problem:** With the 5 s SessionEnd budget, the outer deadline can expire before any cleanup runs.
- **Best solution:** Step 1 of B140 cancels `_shutdownCancellation` (already done at the top) and then awaits `Task.WhenAny(_startupTask, Task.Delay(remaining))`. If startup has not finished, record it as a shutdown failure and continue with the safety steps. An unadopted coordinator is still disposed by startup's own `finally`, and an adopted one by step 3.
- **Tests:** `SessionShutdownTests`: a startup task that never completes does not delay the device step past the deadline. Filter: `FullyQualifiedName~SessionShutdown`.
- **Plan v2:** B140 (decided: D1, safety-first ordered steps under one deadline).
- **Related:** SESSION-019, SESSION-030.

### SESSION-036: ExitExplorerAndWait can throw despite its bool contract

- **Severity:** low.
- **Where:** `src/WSGM/Core/ExplorerControl.cs:219` (`Process.GetProcessById`), `:222` (`MainModule`); the async wrapper catches it at `ExplorerDesktopHost.cs:251-258`.
- **Problem:** Callers that trust the bool can be surprised by an exception when Explorer exits between lookups.
- **Best solution:** In the moved exit loop (SESSION-038), process lookup goes through `IExplorerNative`. A process that is gone counts as exited (the `ArgumentException` from `GetProcessById` maps to "exited"), and image identity uses the probe from SESSION-035. Any other unexpected exception is logged and returns false ("not confirmed"), which already triggers desktop recovery. The wrapper's catch becomes unnecessary and is deleted.
- **Tests:** `ExplorerDesktopHostTests`: a pid that vanishes mid-loop counts as exited; a native failure returns false without throwing. Filter: `FullyQualifiedName~Explorer`.
- **Plan v2:** B112.
- **Related:** U04B-LFA-011.

### SESSION-038: Retired-shell state is a process static guarded by a lock held for up to 30 s of Thread.Sleep polling

- **Severity:** low.
- **Where:** `src/WSGM/Core/ExplorerControl.cs:23-25` (`_retired`, `ExitGate`), `:200-303` (`ExitExplorerAndWait`), `:311-338` (`WaitForRetiredShell`).
- **Problem:** `WaitForRetiredShell` during a desktop return blocks behind a concurrent exit loop, and `_retired` is shared across `ExplorerDesktopHost` instances.
- **Best solution:** Move `_retired`, `RememberRetired`, `ExitedUncleanly`, `ThirdPartyModules`, `CloseWindowsOf`, `WaitForShellAbsence` and the exit loop into `ExplorerDesktopHost` as instance members. They are already serialized by its `_operationGate`. Delete the static `ExitGate`. Replace `Thread.Sleep` polling with `await Task.Delay(interval, cancellationToken)` inside the host's async operations, keeping today's intervals and timeouts. Exit is still only `0x5B4` and `WM_CLOSE`.
- **Tests:** `ExplorerDesktopHostTests`: a desktop return during an exit waits on the host gate, not a static lock; two hosts do not share retired state. Filter: `FullyQualifiedName~Explorer`.
- **Plan v2:** B112.
- **Related:** SESSION-034.

### SESSION-039: StopFailedChildAsync leaks handles and masks the original exception when the writer flush throws

- **Severity:** low.
- **Where:** `src/WSGM/Core/ExplorerShellAnchor.cs:735-755` (unguarded `writer.DisposeAsync()` at `:747-754`).
- **Problem:** If disposing the writer throws (broken pipe; the writer has `AutoFlush` and flushes on dispose), the reader, pipe and child process handle are never released. Because `StartAsync` calls this helper from inside its `catch (Exception ex)` before mapping `ex` to an `ExplorerShellAnchorStartResult`, the IO exception escapes `StartAsync` instead of the mapped start failure.
- **Best solution:** `StopFailedChildAsync` never throws. It keeps today's order (terminate and wait for the child, then writer, reader, pipe, process) and wraps each disposal in its own `try/catch` that logs the failure with the resource name. The caller's `catch` then maps the original exception to its result unchanged, and the invalid-handshake path returns its result as today. No rethrow, no new state.
- **Tests:** An anchor start failure whose writer dispose throws: the pipe and process are still disposed and `StartAsync` returns the mapped failure result instead of throwing. Filter: `FullyQualifiedName~ShellAnchor|FullyQualifiedName~Explorer`.
- **Plan v2:** B112.
- **Related:** U04B-LFA-005, plan claim C27, SESSION-061.

### SESSION-042: High-integrity Explorer is refused for takeover without documentation

- **Severity:** low.
- **Where:** `src/WSGM/Core/ExplorerShellPolicy.cs:71-76`.
- **Problem:** The refusal is correct (WSGM must not take over a shell it cannot manage from medium integrity), but users and maintainers cannot see why the takeover is classified as failed.
- **Best solution:** Keep the refusal unchanged. Add a remarks block on the policy and a short paragraph in `docs/elevation.md` ("A High-integrity Explorer is never taken over; Game Mode entry reports the desktop as preserved"). The existing log line names the reason.
- **Tests:** The existing `ExplorerShellPolicyTests` stay. Filter: `FullyQualifiedName~ExplorerShellPolicy`.
- **Plan v2:** B112.
- **Related:** U04B-LFA-007, plan claim C29.

### SESSION-043: MessageWindow is a shared process singleton that one holder destroys for all

- **Severity:** low (verifier lowered it from medium: `MessageWindow.Dispose` already unregisters volume notifications, so the later drive-manager deregistration is a no-op; the ownership defect is real but has no observable shutdown fault today).
- **Where:** `src/WSGM/Interop/MessageWindow.cs:87-108` (`Dispose`), `:189-203` (`Create()`); "creators" `ShellSession.cs:468`, `ShellSession.SteamUi.cs:584` (`CardVolumeMonitor`), `CardAcfWatcher.cs:90`, `RemovableDriveManager.cs:156`, `OverlayController.cs:215`; the disposal order in `ShellSession.Shutdown.cs:598-609,752-756`.
- **Problem:** Every consumer "creates" the same instance, and the session's dispose destroys it for all, from whichever thread shutdown is on.
- **Best solution:** Remove `Create()` and add a public constructor. It verifies the UI thread (`Dispatcher.UIThread.VerifyAccess()`), creates the message-only window, registers session, suspend/resume and power-source notifications, and sets the static `_instance` dispatch slot, throwing if one already exists (one window per process). `Dispose` clears the slot. The composition root (`ShellSession`, or the overlay-test and preview compositions for their own) constructs it once and passes it by constructor to `CardVolumeMonitor`, `CardAcfWatcher`, `RemovableDriveManager` and the overlay activation owner (after B113, which owns hotkey construction, critic conflict 25). Providers are disposed before the window (winsvc C8), and the window is disposed last on the UI thread (B140 step 11). `DisplayChangeWindow` also gets owner-only construction (WINSVC-017). There is no `GWLP_USERDATA`/`GCHandle` and no reference-counted claim objects (critic conflict 14).
- **Tests:** Build plus a `MessageWindowTests` case only for pure decode helpers extracted from `WndProc` (SESSION-044). Filter: `FullyQualifiedName~MessageWindow|FullyQualifiedName~Tray`.
- **Plan v2:** B114 (after B112 and B113).
- **Related:** U05-LFB-014, PV11-013, WINSVC-017; plan claims C18, C31; review R4.

### SESSION-044: No exception guard in MessageWindow.WndProc

- **Severity:** low.
- **Where:** `src/WSGM/Interop/MessageWindow.cs:530-627` (`[UnmanagedCallersOnly] WndProc`; `Marshal.PtrToStructure` and posted lambdas at the native boundary).
- **Problem:** An exception at an `UnmanagedCallersOnly` boundary terminates the process without cleanup.
- **Best solution:** Wrap the body in `try { ... } catch (Exception ex) { Log.Error("MessageWindow message failed", ex); }` and return `DefWindowProcW(hwnd, msg, wParam, lParam)` after a failure, as `TrayHost.WndProc` does (`TrayHost.cs:233-249`). Extract the struct decoding (`WM_POWERBROADCAST` setting, `WM_DEVICECHANGE` volume) into pure static helpers so they can be tested. The `DisplayChangeWindow` guard is no-change (WINSVC-045: it only posts). The `EffectivePowerModeNotification` guard belongs to B091.
- **Tests:** `MessageWindowTests` for the decode helpers (valid and short buffers). Filter: `FullyQualifiedName~MessageWindow`.
- **Plan v2:** B114.
- **Related:** U05-LFB-013, PV11-012, WINSVC-045, SESSION-V-002.

### SESSION-046: Panic destroys the tray cross-thread and can start Explorer while WSGM's taskbar is alive

- **Severity:** low (the verifier corrected the scope: the common Panic path, `Program.cs:239-242`, runs on the STA UI thread where `DestroyWindow` works; only the `AppDomain.UnhandledException` path from a pool thread is cross-thread).
- **Where:** `src/WSGM/Program.cs:735-778` (`Panic`, `TrayHost.DestroyActive()` at `:744-751`, Explorer start at `:753-765`).
- **Problem:** From a pool thread, `DestroyWindow` fails and `_instance` is read unsynchronized. Panic then starts Explorer next to a live `Shell_TrayWnd` for the few milliseconds before the process dies.
- **Best solution:** Panic calls a static `TrayHost.RetireActiveOnThisThread()`. It destroys the window synchronously only when the current thread is the window's thread (store the creating thread id in `TrayHost` at creation and compare with `Environment.CurrentManagedThreadId`), and otherwise does nothing. It cannot post, because the dispatcher has already stopped on the main Panic path. The Explorer start logic stays as today: it is delegated to the verified anchor when one exists, and otherwise uses the launcher from SESSION-037. The brief coexistence on the pool-thread path is accepted because the process terminates right after the handler. Explorer is never killed.
- **Tests:** A unit test of the thread-match predicate. Filter: `FullyQualifiedName~Tray`.
- **Plan v2:** B114.
- **Related:** SESSION-045, SESSION-V-002, SESSION-037.

### SESSION-049: TrayProtocol rejects any NOTIFYICONDATA with cbSize above 968

- **Severity:** low.
- **Where:** `src/WSGM/Core/TrayProtocol.cs:130-142` (`cbSize < MinimumNidSize || cbSize > 968 || nid.Length < (int)cbSize`).
- **Problem:** A valid registration from a Windows build that extends NOTIFYICONDATA is dropped, although the parser reads only the v3 region. This is a length cap that drops content.
- **Best solution:** Accept `cbSize >= MinimumNidSize` (952) with `nid.Length >= cbSize`, delete the `> 968` bound, and update the comment. The parser keeps reading only the v3 fields.
- **Tests:** `TrayProtocolTests`: cbSize 972 accepted; 951 rejected; a buffer shorter than cbSize rejected. Filter: `FullyQualifiedName~TrayProtocol`.
- **Plan v2:** B114.
- **Related:** no-arbitrary-limits rule.

### SESSION-051: WindowFinder's warn-once set is mutated from concurrent poll threads

- **Severity:** low.
- **Where:** `src/WSGM/Core/WindowFinder.cs:27` (`WarnedSessionIdNames`, a static `HashSet<string>`), `:102` (`Add` inside the catch-all).
- **Problem:** The splash probe, Steam monitor, Big Picture close waits and boot takeover poll call it concurrently. A concurrent `Add` can corrupt the set and throw from the poll that "must not throw".
- **Best solution:** Guard the `Add` with a static `Lock` (the same pattern as the existing `IncludeGate`): `bool first; lock (WarnedGate) { first = WarnedSessionIdNames.Add(plain); } if (first) Log.Warn(...)`. No per-sample work changes.
- **Tests:** None beyond build. Optionally, a parallel call test that never throws. Filter: `FullyQualifiedName~WindowFinder`.
- **Plan v2:** B114.
- **Related:** none.

### SESSION-055: Steam.ColdStart falls back to a competing launch after an Unknown scheduler result

- **Severity:** low.
- **Where:** `src/WSGM/Core/Steam.cs:295-308` (`ColdStart` uses the bool scheduler wrapper and falls back to `AppLauncher.Start`).
- **Problem:** When the de-elevation dispatch outcome is unknown, Steam may already be starting, and the same-integrity fallback starts a second Steam.
- **Best solution:** `ColdStart` uses the tri-state `UnelevatedLauncher` result (U04B-LFA-001). Dispatched means done. NotDispatched falls back to today's same-integrity `AppLauncher.Start`. Unknown starts nothing more and leaves Steam detection to the existing monitor and splash. `SteamInputShim.Reconcile` stays gated on both process names being absent (SESSION-054).
- **Tests:** `SteamSessionControlTests` or `SteamTests` with a fake launcher: Unknown makes no second start; NotDispatched starts once. Filter: `FullyQualifiedName~Explorer|FullyQualifiedName~SteamSessionControl`.
- **Plan v2:** B112.
- **Related:** U04B-LFA-001, SESSION-037, SESSION-054.

### SESSION-057: Splash timeout armed at Show; opening the overlay dismisses an entry splash

- **Severity:** low.
- **Where:** `src/WSGM/Shell/BootSplash.cs:52-58,109` (timeout armed at `Show`); `src/WSGM/Shell/ShellSession.Modes.cs:84` (`OverlayShown += () => _splash?.Dismiss(...)`), `:535-558` (`EnsureEntrySplash`, `_holdingEntrySplash`).
- **Problem:**
  - On a service boot, the input-desktop wait, readiness wait, Explorer exit and start delays all run before Big Picture is requested, so the 120 s cover can time out mid-takeover. `_splash is not null` then still reports Big Picture as covered.
  - Opening the overlay dismisses a Game Mode entry splash. `_holdingEntrySplash` stays true, status updates go nowhere, and the Cancel button is gone.
- **Best solution:**
  - The mechanism already exists: `BootSplash(config, action, armed)` and `ArmSteamDetection()`, which the entry splash uses (`ShellSession.Modes.cs:765`). The boot cover is constructed with `armed: false` (`ShellSession.Modes.cs:83` today passes the default `true`), and `ArmSteamDetection()` is called where Big Picture is actually requested: the boot launch sequence and the takeover. No new method. That matches `SplashPolicy` and the boot doc. "Covered" is read from the splash's own visible state, not from `_splash is not null`.
  - The `OverlayShown` handler dismisses the splash only when it is the boot cover (`!_holdingEntrySplash`). An entry splash stays up.
- **Tests:** `BootSplashTests` or `SplashPolicy` tests: an unarmed splash never times out; the timeout counts from `ArmSteamDetection`, not from `Show`. `SessionTransitionsTests`: an overlay open during entry keeps the entry splash. Filter: `FullyQualifiedName~Splash|FullyQualifiedName~SessionTransitions`.
- **Plan v2:** B141.
- **Related:** U05-LFB-010, U05-LFB-011, plan claim C35.

### SESSION-063: SessionModes gets eight collaborators through settable properties and has a preview-only constructor

- **Severity:** low.
- **Where:** `src/WSGM/Shell/SessionModes.cs:142-171` (`PrepareSteamUiForBigPictureAsync`, `PrepareSteamUiForDesktopAsync`, `SteamUiBigPictureRequestSettled`, `GameModeEntryServices`, `GameModeEntrySettled`, `DesktopReady`, `IsGameMode`), plus public events; preview ctor used by `src/WSGM/Settings/SettingsWindow.axaml.cs:259`.
- **Problem:** A half-wired `SessionModes` can run with nulls, and nothing shows which hooks are required.
- **Best solution:** One constructor, `SessionModes(SessionModeHooks? hooks, ISteamSessionControl steam, ...)`. `SessionModeHooks` is a record holding the seven delegates and the `SessionEntryBackend`. A null `hooks` means preview: every transition method returns at once, which replaces the preview-only ctor. Delete the settable properties. The events stay as they are because the overlay subscribes to them.
- **Tests:** `SessionModesTests`: preview mode refuses transitions; a constructed host uses the injected hooks. Filter: `FullyQualifiedName~SessionModes`.
- **Plan v2:** B115.
- **Related:** SESSION-022, SESSION-056.

### SESSION-V-004: Local\WSGM.Activate is created with the elevated token's default DACL

- **Severity:** low, PLAUSIBLE (missed by the review, found by the verifier).
- **Where:** `src/WSGM/Program.cs:178-185` (`new EventWaitHandle(false, EventResetMode.AutoReset, SessionActivation.EventName)`); `src/WSGM/Shell/SessionActivation.cs:15-18`; the SDDL helper is `UpdateExitWatcher.BuildEventSddl` with `CreateOrOpenEvent` (`UpdateExitWatcher.cs:71-74,264-300`).
- **Problem:** An elevated resident shell creates the event so that a non-elevated same-user instance cannot open it. A Start-menu `--shell --activate` launch whose self-elevation is declined continues non-elevated, and its `EventWaitHandle` constructor throws `UnauthorizedAccessException` out of `MainAsync` before the unhandled-exception handlers are attached.
- **Best solution:** Create or open `Local\WSGM.Activate` through the same helper the exit events use. `UpdateExitWatcher.CreateOrOpenEvent(eventName, operation, userSid, clearStaleSignal)` is private and always creates a manual-reset event (`CreateEventW(..., true, false, ...)`); make it internal and add a `bool manualReset` parameter (the exit events pass `true`), then call it with `manualReset: false, clearStaleSignal: false`. It already applies `BuildEventSddl` (user SID and Administrators get `SYNCHRONIZE | EVENT_MODIFY_STATE`, medium label) and already falls back to `OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE)` on `ERROR_ACCESS_DENIED`. Wrap the handle in an `EventWaitHandle` via `SafeWaitHandle`. The name stays unchanged and comes from `SessionProtocolNames` after B031. If opening still fails (an older resident with the default DACL), log once and continue without activation instead of throwing; activation is a convenience. `SessionActivation` opens the existing handle in the resident process. This is a functional fix (a crash at start and an activation that never arrives), not hardening: it widens access for the same user, so the DECISIONS.md security drop does not remove it.
- **Tests:** A unit test that the activation SDDL equals the exit-event SDDL for a given SID. Filter: `FullyQualifiedName~Tray|FullyQualifiedName~UpdateExitWatcher`. Manual: a non-elevated `--shell --activate` with a resident elevated shell opens the overlay.
- **Plan v2:** B114.
- **Related:** SESSION-047, SESSION-050.

### SESSION-V-005: OnSessionEnding cannot escalate a shutdown that is already running

- **Severity:** low (missed by the review, found by the verifier).
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:16-26` (`if (_disposed) return;` before `Request(SessionEnd)`).
- **Problem:** When a Normal shutdown is running, `WTS_SESSION_LOGOFF` returns early. SessionEnd never reaches the running cleanup, which may still start Explorer and close Big Picture during sign-out.
- **Best solution:** Remove the `_disposed` early return. `OnSessionEnding` always calls `ApplicationShutdownRequest.Request(SessionEnd)`, which sets the sticky flag (SESSION-032), and then `runtime.RequestExit()`, which joins the running task. The running cleanup reads the flag before the Big Picture and Explorer steps. Log the "session is ending" line only once.
- **Tests:** A running Normal shutdown followed by a session-end notification skips Explorer and closes Big Picture zero times. Filter: `FullyQualifiedName~ApplicationShutdown`.
- **Plan v2:** B006.
- **Related:** SESSION-032, SESSION-007, U04A-LFA-020.

### SESSION-C-001: An update with only Settings open waits out setup's full 22 s grace

- **Severity:** low (found by the solution check).
- **Where:** `src/WSGM/Core/UpdateExitWatcher.cs` `Start` (creates the `ExitForUpdate.Completed` and `ExitForUninstall.Completed` events in every mode) and `ReportHandoff`; `src/WSGM/App.axaml.cs` (`OnShutdownRequested`, never raised for the forced exit); `src/WSGM.Setup/Engine/WindowsSetup.cs` `RequestExit` (`completion` opened before the request, loop `!ShellRunning() && (completion is null || completed)`), `UpdateGraceIterations = 44`.
- **Problem:** A Settings process creates the `.Completed` event but never sets it: its watcher exits through the forced `Shutdown()`, and after B006 SESSION-V-001 as first written it would still skip the report for a process with no session. Setup opens the event, so `completion` is not null; with no shell running the loop still waits for `completed`, which never comes. Every update or uninstall started while only Settings is open waits 44 × 0.5 s, logs `TimedOut` and goes to the force-stop fallback.
- **Best solution:** In B006, every process that exits for Update or Uninstall reports the handoff once at exit. The runtime's no-session branch reports `Clean` for the current reason before `forcedExit(0)` (written into SESSION-V-001). A Settings report cannot complete a running shell's handoff early, because setup also requires the shell mutex `Local\WSGM.Shell` to be gone and the shell holds it until its process exits. No setup change, no new event.
- **Tests:** `ApplicationShutdownTests` (B006) or `ApplicationRuntimeTests` (B111): a runtime with no session reports `Update`/`Clean` once and exits 0; a Normal exit with no session reports nothing. Filter: `FullyQualifiedName~ApplicationShutdown`. Manual: M01-45 variant with only Settings open: the setup log shows `Shutdown handoff on Local\WSGM.ExitForUpdate: Completed` without the 22 s wait.
- **Plan v2:** B006 (add to its Resolves list).
- **Related:** SESSION-003, SESSION-V-001, SESSION-004.

## Nit

### SESSION-008: ShellSession.DisposeAsync is an unused second shutdown entry

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/ShellSession.cs:237-243`.
- **Problem:** It builds its own Normal budget and is called by nothing, but it suggests a second way to stop the session.
- **Best solution:** Delete `DisposeAsync`, and `IAsyncDisposable` from the class declaration if it is declared only for it. The runtime's single shutdown task is the only entry.
- **Tests:** Build only.
- **Plan v2:** B111.
- **Related:** U05-LFB-029, SESSION-031.

### SESSION-015: QueueDesktopActions attaches a dead fault continuation

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/ShellSession.Modes.cs:579-587` (`ContinueWith(OnlyOnFaulted)` rethrowing on the dispatcher), `:642-648` (`RunDesktopActionsAsync` catches everything).
- **Problem:** The continuation can never run, yet it reads as if desktop action faults crash the dispatcher.
- **Best solution:** Delete the continuation. The task stays observed by the action admission and is refused after `CloseAdmission`.
- **Tests:** Build; B124 filter.
- **Plan v2:** B124.
- **Related:** U05-LFB-027, SESSION-029.

### SESSION-016: The pending return layout has two sources of truth

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/ShellSession.Modes.cs:820-833` (memory `_pendingReturnLayout` set before the durable write, on the pool), `:857-859` (memory preferred on return).
- **Problem:** Memory and record can disagree after a failed write, and the field is written cross-thread.
- **Best solution:** Delete `_pendingReturnLayout`. `PersistPendingReturnAsync` writes only the durable record, and the return reads it through `DesktopReturnRecovery` (B042).
- **Tests:** `GameModeEntryTransactionTests`: the return uses the persisted layout. Filter: `FullyQualifiedName~GameModeEntryTransaction|FullyQualifiedName~DesktopReturnRecovery`.
- **Plan v2:** B115.
- **Related:** U05-LFB-019, SESSION-013, SESSION-027.

### SESSION-028: An orphaned XML summary is stacked on SteamUiPrepareTimeout

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/SessionModes.cs:60-69`.
- **Problem:** Two summaries describe one member; one belongs to nothing.
- **Best solution:** Delete the orphaned summary.
- **Tests:** Build only.
- **Plan v2:** B141.
- **Related:** U05-LFB-026.

### SESSION-041: Unused sync-over-async Dispose wrappers and a 16-entry cap on diagnostic module lists

- **Severity:** nit.
- **Where:** `src/WSGM/Core/ExplorerDesktopHost.cs:67-70` and `src/WSGM/Core/ExplorerShellAnchor.cs:128-131` (sync `Dispose()` blocking on async); `src/WSGM/Core/ExplorerControl.cs:391-413` (`ThirdPartyModules` stops at 16 names and appends "..."); event names at `ExplorerShellAnchor.cs:766-769`.
- **Problem:** The sync wrappers invite deadlocks and are unused. The module cap drops diagnostic content (no-arbitrary-limits).
- **Best solution:** Delete both sync `Dispose()` methods and drop `IDisposable` where only `DisposeAsync` remains in use. `ThirdPartyModules` (moved into the host by SESSION-038) lists every third-party module. Keep the stop event names that embed the session id unchanged, for cross-version compatibility.
- **Tests:** Build; `FullyQualifiedName~Explorer`.
- **Plan v2:** B112.
- **Related:** SESSION-038.

### SESSION-047: Cross-project protocol names are duplicated literals

- **Severity:** nit.
- **Where:** `Local\WSGM.ShellAnchor.RecoverySettled` (`src/WSGM/Core/ExplorerShellAnchor.cs:22`, `src/WSGM.Setup/Engine/WindowsSetup.cs:50`); `Local\WSGM.ExitForUpdate`/`ExitForUninstall` (`src/WSGM/Core/UpdateExitWatcher.cs:34,37`, `WindowsSetup.cs:46-47`); `Local\WSGM.Shell` (`Program.cs:710`, `WindowsSetup.cs:48`); `Global\WSGM.DeviceOwner` (four copies including Device Lab).
- **Problem:** A rename on one side silently breaks the update or uninstall handoff between versions.
- **Best solution:** Add one linked source file, `src/Shared/Process/SessionProtocolNames.cs`, with the shell mutex, activation event, exit events and their `.Completed` names, the restore-shell event, the anchor settled event and the device owner name. Values are unchanged. Every project references the constants. `SetupShutdownContractTests` pins each value as a literal.
- **Tests:** `SetupShutdownContractTests`. Filter: `FullyQualifiedName~Setup|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~Launch`.
- **Plan v2:** B031.
- **Related:** INSTALL-027, plan claim C25, SESSION-005, SESSION-V-004.

### SESSION-050: SessionActivation's disposal flag is not volatile

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/SessionActivation.cs:13,21,31-32` (`_disposed` plain field; `Unregister(null)` does not wait for an in-flight callback).
- **Problem:** The effect is benign, because the posted lambda checks the flag on the UI thread, but the read is formally unsynchronized.
- **Best solution:** Use `Volatile.Write(ref _disposed, true)` in `Dispose` and `Volatile.Read` in the posted lambda. Keep `Unregister(null)`.
- **Tests:** Build only.
- **Plan v2:** B114.
- **Related:** SESSION-V-004.

### SESSION-052: Poll paths allocate per window and truncate switcher titles at 255 characters

- **Severity:** nit.
- **Where:** `src/WSGM/Core/WindowFinder.cs:263,337` (`char[256]` per enumerated window in `EnumWindowsProc` and `ListWindowsProc`); `src/WSGM/Interop/NativeShellProcess.cs:150-162` (64 KB buffer per `QueryImagePath`) inside the 200 ms restore polling (`ExplorerDesktopHost.cs:528-579`).
- **Problem:** Allocation per window and per poll, and switcher titles longer than 255 characters are cut (an arbitrary limit).
- **Best solution:** Read the class name into `stackalloc char[256]` (class names are limited to 256 by Windows) through a `char*` overload of `RealGetWindowClassW`, and compare the span with `MemoryExtensions.Equals(span, state.WindowClass, StringComparison.OrdinalIgnoreCase)` so no string is built per window. For titles, call `GetWindowTextLengthW` first, use `stackalloc` when the length is at most 256 and `ArrayPool<char>.Shared` otherwise, so no title is truncated. `QueryImagePath` rents its buffer from `ArrayPool<char>.Shared` and returns it in `finally`.
- **Tests:** `WindowFinderTests`: a title over 255 characters is returned whole (through a seam on the text reader). Filter: `FullyQualifiedName~WindowFinder`.
- **Plan v2:** B114.
- **Related:** no-arbitrary-limits rule.

### SESSION-053: Stale WindowFinder doc, an over-visible TerminateProcess and duplicated native structs

- **Severity:** nit.
- **Where:** `src/WSGM/Core/WindowFinder.cs:12-15` (doc says "the home app's main window"); `src/WSGM/Interop/NativeShellProcess.cs:243-245` (`internal TerminateProcess`); `StartupInfo`/`ProcessInformation` declared in both `NativeShellProcess.cs:341-371` and `src/WSGM/Interop/ParentProcessStart.cs:276-313`.
- **Problem:** The doc misleads. Any code can reach a terminate call that only the owned anchor child may use. Two struct copies can drift.
- **Best solution:** Rewrite the summary to "session id, process queries, switcher listing and focus". Move the `TerminateProcess` `LibraryImport` into `NativeShellChildProcess` (same file, made `partial`) as a `private static partial` method. Its only caller is `NativeShellChildProcess.TryTerminate` (the owned anchor child), so `private` inside `NativeShellProcess` would not compile; Explorer is never terminated. The two private `StartupInfo` and `ProcessInformation` copies are field-for-field identical: keep one `internal` declaration in `NativeShellProcess.cs` (top level in `WSGM.Interop`), used by `NativeShellProcess.TokenLaunch.cs` and by `ParentProcessStart` (whose `StartupInfoEx` embeds it), and delete the `ParentProcessStart` copy.
- **Tests:** Build only.
- **Plan v2:** B114.
- **Related:** none.

### SESSION-058: Splash decode policy is 300 lines of comments with tests that re-implement the decoder

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/BootSplashWindow.axaml.cs:36-109,385-505`; tests re-implement `TryLoadBitmap`.
- **Problem:** The policy is hard to read and the tests do not exercise the production code. The decode budgets downscale rather than drop. `ImageHeader.TryReadBoundedSize` refusing oversized declared dimensions is a safety bound on the D2 list (splash zip bounds), which the maintainer accepted as written.
- **Best solution:** Move the pure math (target size, downscale factor, header bounds) into a static `SplashDecode` class next to the window. Keep every bound and value. Condense the comments to the rule and its reason. The tests call `SplashDecode` directly instead of copying it.
- **Tests:** Splash decode tests over the real helper. Filter: `FullyQualifiedName~Splash`.
- **Plan v2:** B141 (decided: D2 accepts exactly the plan v2 list, which keeps the splash bounds).
- **Related:** U05-LFB-023.

### SESSION-061: ShellAnchorDisposalTests pins .NET pipe behaviour, not the guard

- **Severity:** nit.
- **Where:** `tests/WSGM.Tests/Shell/ShellAnchorDisposalTests.cs`.
- **Problem:** The premise test checks framework behaviour and would not catch a regression in the anchor guard.
- **Best solution:** Delete the premise test. The guard is covered by the anchor start-failure test from SESSION-039.
- **Tests:** Filter: `FullyQualifiedName~ShellAnchor`.
- **Plan v2:** B141.
- **Related:** U05-LFB-022, SESSION-039.

### SESSION-062: Getter and constant tests, and tests that leak the process-global shutdown reason

- **Severity:** nit.
- **Where:** `tests/WSGM.Tests/Core/ApplicationShutdownTests.cs:20-31` (`BudgetsMatchFrozenShutdownAndUpdatePreStopDeadlines`), `:91-110` (`Request`/`Consume` mutate the global reason); `SystemPowerTransitionTests.cs:132-140` (`EveryResumeCodeWindowsCanSendIsADistinctValue`); `WindowFinderTests.cs:35` (`WindowSnapshotRetainsItsPositionalRecordContract`); `DeviceIntegrationOffTests.cs:177-189` (asserts a property it just set).
- **Problem:** These tests prove nothing about behaviour, and the reason tests leak state across tests.
- **Best solution:** Delete the four getter and constant tests. The setup contract test (B031) pins the cross-process budgets that matter. The reason tests move to `ApplicationRuntimeTests` over a runtime instance (B111), so no global state remains.
- **Tests:** Filters: `FullyQualifiedName~ApplicationRuntime|FullyQualifiedName~SystemPowerTransition|FullyQualifiedName~WindowFinder|FullyQualifiedName~DeviceIntegrationOff`.
- **Plan v2:** B141.
- **Related:** U05-LFB-030, SESSION-009.

---

## Refuted or no-change

- **SESSION-003:** Refuted. A Settings process's watcher ends through the forced `Shutdown()`, so it never reports `ExitForUpdate.Completed`. A completion from a Settings process could not mislead setup anyway: `WindowsSetup.RequestExit` only accepts it once the shell mutex is gone. The opposite gap, a Settings-only update waiting the full grace because nothing reports, is SESSION-C-001; after B006 the no-session exit reports `Clean`. The duplicate Steam pre-stop residual is handled in SESSION-004 (B111).
- **SESSION-012:** Refuted. The failure scenario was inverted: with no taskbar, the OEM toggle retries the desktop return and does not start a Game Mode entry. `IsDesktopShellRunning` stays the authority for "is the desktop up", and no mode-authority query is added (review A3 dropped). The per-call cost is fixed by SESSION-035.
- **SESSION-040:** No change (the hold is the documented rule). `ExplorerDesktopOutcome.Failed` means "no verified usable taskbar was produced" (`ExplorerDesktopHost.cs:702-703`), so the desktop is still suspended, and `docs/boot-and-shell.md` (desktop integrations) says the startup-app sequence suppresses listed integrations while the desktop is suspended. The only consumer, the launch sequence (`ShellSession.Modes.cs:681-685`), runs once per process at boot (`ShellSession.cs:455` and the takeover in `ShellSession.Modes.cs:282`), so a restore that fails later in the same session never reaches it; at boot after a takeover the suppression is the intended behaviour. Clearing the flag while the stopped apps stay unrestored would let the launch sequence start an integration (another controller manager) beside WSGM's device control during Game Mode. `_desktopApps` keeps the stop list, so the next Normal or Degraded restore restarts them and clears the flag. B112 adds only a one-line remark on `_desktopAppsSuspended` saying it is held until a verified restore; drop SESSION-040 from B112's Resolves list.
- **SESSION-048:** No change. Dropped by maintainer decision (security theater, DECISIONS.md): keep today's tray relay unchanged. The finding only concerned a medium-integrity registrant naming an elevated window as its callback target; there is no functional defect, so no code change, remarks block or `docs/elevation.md` paragraph is added. Drop SESSION-048 from B114's Resolves list.
- **SESSION-054:** No change. It is plausible only: `Steam.IsRunning` keeps counting `steam.exe` and `steamwebhelper` until a `wsgm.log` shows a lingering helper (plan v2 attended evidence). `ColdStart`'s shim step stays gated on both names.
