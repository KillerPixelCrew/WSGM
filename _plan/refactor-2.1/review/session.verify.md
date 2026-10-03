# Session domain: adversarial verification of session.md

Verifier scope: the same files as session.md, baseline HEAD `1329813f`, read-only. Framework claims were checked against
the pinned Avalonia 12.1.2 binaries (`~/.nuget/packages/avalonia/12.1.2/lib/net10.0/Avalonia.Controls.dll` and
`avalonia.win32/12.1.2/.../Avalonia.Win32.dll`, decompiled with ilspycmd into the scratchpad) and against this machine's
own `%LOCALAPPDATA%\WSGM\wsgm.log` (read only).

The most important result is a defect that the review, the plan and the prior ledger all missed (SESSION-V-001). Several
review findings and two plan-claim verdicts rest on the opposite assumption, so they are refuted or corrected below.

## Framework facts the review did not check

1. `ClassicDesktopStyleApplicationLifetime.Shutdown(int)` in 12.1.2 is
   `DoShutdown(new ShutdownRequestedEventArgs(), isProgrammatic: true, force: true, exitCode)`. With `force: true`,
   `DoShutdown` skips `ShutdownRequested?.Invoke` entirely, closes windows ignoring cancel, raises `Exit` and calls
   `Dispatcher.UIThread.InvokeShutdown()`. Only `TryShutdown(int)` and the platform event raise `ShutdownRequested`.
   The XML doc in the package says the same: `ShutdownRequested` is "Raised by the platform when an application shutdown
   is requested".
2. `Avalonia.Win32.Win32Platform.WndProc` raises its `ShutdownRequested` on message 17 (`WM_QUERYENDSESSION`) and returns
   0 when the handler cancels. Its window is created with parent 0 (a hidden top-level window, not `HWND_MESSAGE`), so it
   does receive the broadcast. The lifetime subscribes to it in `AfterInit` and calls `DoShutdown(e, isProgrammatic:
   false)`, which raises `App.OnShutdownRequested`.

## Refuted

- **SESSION-002** (medium). "A startup failure ends with exit code 0." `ObserveSessionStartupAsync` calls
  `desktop.Shutdown(1)` (`App.axaml.cs:94`), which is forced: `OnShutdownRequested` never runs, so `:143` never
  overwrites the code. The process exits 1 and the logon-service watchdog treats it as a dirty exit
  (`SessionLauncher.cs:241`). The real defect on this path is that no session cleanup runs at all (SESSION-V-001). Note:
  the claim becomes true the moment V-001 is fixed by routing exits through `ShutdownRequested`, so the sticky
  startup-failure exit code must land in the same batch as that fix.
- **SESSION-003** (medium). "A Settings process publishes `ExitForUpdate.Completed` as Clean." The Settings process's
  watcher runs `RunInstallerExitRequest`, whose `ShutdownLifetime()` is the forced `Shutdown()`, so
  `App.OnShutdownRequested` and `UpdateExitWatcher.ReportHandoff` never run there. No false completion is published.
  Residual (nit): a Settings or overlay-test process also runs `Steam.StopForUpdate`, a duplicate `steam://exit`.
- **SESSION-012** (medium). The failure scenario is inverted. In a failed or degraded return where Explorer's taskbar is
  absent, `IsDesktopShellRunning()` is false and the OEM toggle calls `EnterDesktopMode()` (`ShellSession.cs:1010-1017`),
  which retries the return because `_desktopReturnComplete` stays false; it does not start a Game Mode entry. When the
  taskbar is up the session is on the desktop and entry is correct. `EnterGameMode` already consults `_inGameMode`
  through `IsGameMode` (`SessionModes.cs:451`). The Core guide makes `IsDesktopShellRunning` the authority for "is the
  desktop up". The only valid part is per-call cost, which is U04B-LFA-010 (SESSION-035). Do not adopt A3 as a
  defect fix.
- **Plan claim C21** (review verdict "inaccurate"). Today a startup failure does exit 1 (see SESSION-002). The plan
  sentence is accurate about the exit code; what is missing today is the cleanup before it.

## Corrected

- **SESSION-007**: PLAUSIBLE/low becomes CONFIRMED/high. Avalonia raises `ShutdownRequested` from
  `WM_QUERYENDSESSION` (fact 2). `App.OnShutdownRequested` sets `Cancel = true` for a live session (`App.axaml.cs:118`),
  so WSGM answers `WM_QUERYENDSESSION` with FALSE and blocks sign-out, restart and shutdown until Windows' blocker
  screen or the user overrides it. This includes WSGM's own overlay Shut down/Restart, which runs `shutdown /s /t 0`
  without `/f` (`external/windows-device-control/.../WindowsPower.Actions.cs:64`). The cleanup then runs with reason
  `Normal` (`Consume()` at `:109`), so it sends `steam://close/bigpicture` and restores Explorer through the anchor
  during session end (`ShellSession.Shutdown.cs:798-881`), which the SessionEnd rule forbids. A later
  `WTS_SESSION_LOGOFF` is dropped by `OnSessionEnding`'s `_disposed` check (`:18`). This is the only path on which
  `ShellSession.ShutdownAsync` runs today; `wsgm.log` 2026-09-30 07:53:34 shows a partial cleanup (touch, display-state
  and volume deregistration) followed by nothing, consistent with an OS end-session kill. Recommendation: treat a
  platform `ShutdownRequested` as SessionEnd; do not cancel it for logoff/shutdown (or register a
  `ShutdownBlockReasonCreate` reason while the 5 s SessionEnd cleanup runs), and never restore Explorer on it.
- **SESSION-011**: medium becomes high, and the consequence is different. `coordinatorAdopted = true` is set before
  `StartOnUiThread()` (`ShellSession.cs:330-332`), so when any owner constructor throws, the startup `finally` does not
  dispose the coordinator, and the forced `Shutdown(1)` runs no session cleanup. A device session that already hid the
  pad (HidHide cloak on) is left as is.
- **Plan claim C12** (review "partially"). Direct game-mode boot with a tray failure does not "fail startup and exit
  cleanly"; it exits 1 with no cleanup at all (forced shutdown). For a service boot the watchdog restores Explorer; a
  manual `--shell` has no watchdog.
- **Plan claim C9** (review "inaccurate"). The plan does not claim `SessionActionRunner` exists; it proposes it. The right
  verdict is "redundant proposed type": reuse `PluginActionSequence` (the review's recommendation stands).
- **Plan claims C6/C19** (review "accurate"). Accurate but incomplete: `ApplicationShutdownRequest.ShutdownLifetime()`
  calls the forced `Shutdown()`, so the "reason re-read before each step" target is moot until V-001 is fixed.
- **SESSION-043**: medium becomes low. `MessageWindow.Dispose` already unregisters volume notifications and zeroes
  `_volumeNotify` (`MessageWindow.cs:87-108`, `:439-455`), so the drive manager's later deregistration is a no-op. The
  singleton-shared ownership is real but has no observable shutdown fault today.
- **SESSION-046**: partly wrong. The common Panic path is `Program.cs:239-242` (the dispatcher crashed and
  `StartWithClassicDesktopLifetime` threw), which runs on the STA UI thread, so `DestroyWindow` works there. Only the
  `AppDomain.UnhandledException` path from a pool thread is cross-thread. Consequence for batch B3: "Panic posts the
  retire" cannot work on the main Panic path because the dispatcher has already stopped; destroy synchronously when on
  the UI thread.
- **SESSION-030**: "no behavior change" is not accurate. Moving `_audio`, `_radios`, `_themes`, `_drives` and similar
  disposals from the pool to the dispatcher (as recommended) changes the thread they run on. The change is correct, but
  it is a behavior change and needs the shutdown manual row.

## Confirmed (ids only)

SESSION-001, 004, 005, 006, 008, 009, 010, 013, 014, 015, 016, 017, 018, 019, 020, 021, 022, 023, 024, 025, 026, 027,
028, 029, 031, 032, 033, 034, 035, 036, 037, 038, 039, 040, 041, 042, 044, 045, 047, 048, 049, 050, 051, 052, 053, 054,
055, 056, 057, 058, 059, 060, 061, 062, 063.

Plan-claim verdicts confirmed as written: C1-C5, C7, C8, C10, C11, C13-C18, C20, C22-C36 (C15, C16, C31, C32, C33
confirmed as simplification objections, which is a maintainer decision).

Notes on confirmations: every medium/high item above was re-read at the cited lines. SESSION-017's trigger "transient
sharing violation" is plausible rather than shown (a mutex timeout degrades to a read, `ConfigStore.cs:1380-1398`); the
defect itself (any load exception becomes live defaults plus `PreserveCorruptFile`) is confirmed for a corrupt or
newer-schema file. SESSION-054 stays PLAUSIBLE (see batch problem 6).

## Missed findings

**SESSION-V-001 (critical). Every programmatic exit skips the whole session cleanup.**
Files: `src/WSGM/Core/ApplicationShutdown.cs:57-64` (`ShutdownLifetime` calls `lifetime.Shutdown()`),
`src/WSGM/Program.cs:502-519` (restore-shell, update, uninstall), `src/WSGM/Shell/ShellSession.cs:925` (Desktop tray
"Exit WSGM"), `src/WSGM/Shell/ShellSession.Shutdown.cs:16-26` (`WTS_SESSION_LOGOFF`), `src/WSGM/App.axaml.cs:50,94,99-146`.
All four exit sources call the forced `Shutdown()` (fact 1), so `App.OnShutdownRequested` and therefore
`ShellSession.ShutdownAsync` never run. Skipped on every one of those exits: AutoTDP restore, controller release and
HidHide cloak off (`HidHideOwnership.cs:108-112` says only leaving turns it off), device/common/GPU plugin stop, tray
retire verification, Explorer restore, refresh/resolution restore, chord template restore, and the installer handoff.
Process exit then drops the in-process state; the anchor restores Explorer after owner loss, and Program still
releases the Steam Input lease and display scale (`Program.cs:221-237`).
Evidence: `ReportHandoff` logs `Installer shutdown handoff completed` unconditionally for Update (`UpdateExitWatcher.cs:103`),
and `wsgm.log` never contains it. The two update exits on 2026-10-02 (19:57:33 and 20:11:11) log "Exit requested by
installer (update)" and the Steam pre-stop, then nothing; setup's next line comes 27 s later in both cases, which is
the full 44 x 0.5 s grace plus margin (`WindowsSetup.cs:255-276`), so setup recorded TimedOut and used its force
fallback. The `RunInstallerExitRequest` tests (`ApplicationShutdownTests.cs:233-266`) pass because their fake
`shutdownLifetime` stands in for a call that, in production, never reaches the handler.
User impact: "Exit WSGM" from the tray and `--restore-shell` (the documented escape hatch) leave a managed controller
hidden with the HidHide cloak on, violating "never strand users on exit"; update cleanup never runs inside its budget;
the installer always waits out its grace period.
Recommendation (simplest, no new mechanism): make `ShutdownLifetime` call `lifetime.TryShutdown()`. That raises
`ShutdownRequested`; the existing handler already sets `Cancel` before its first await, runs the bounded cleanup and ends
with the forced `Shutdown(code)`, and `_shutdownInProgress` already absorbs repeats. Land it with the sticky
startup-failure exit code (otherwise SESSION-002 becomes real) and with the SESSION-007 handling, because the same
handler then serves both programmatic and OS requests. In the refactor, `ApplicationRuntime.RequestShutdown(reason)`
should run the session shutdown itself and only then call the forced lifetime shutdown, instead of depending on the
event. Test: a fake lifetime whose `Shutdown` does not call back must still see session cleanup run once. Manual row:
an update logs the handoff line with `outcome=Clean` and setup reports Completed; tray Exit on a device session shows the
pad again.

**SESSION-V-002 (high). An exception in any UI-thread callback kills the resident shell through Panic, without device
cleanup.**
Files: `src/WSGM/Program.cs:203-209,239-242,735-778`, `src/WSGM/Shell/DesktopTray.cs:44-49`.
There is no `Dispatcher.UIThread.UnhandledException` handler. An exception from an input handler, a posted lambda or a
native-menu click unwinds `Dispatcher.MainLoop`, `StartWithClassicDesktopLifetime` throws, and `Panic` runs. Panic
restores the shell registration, Explorer, display scale and the Steam Input lease, but not AutoTDP, the controller or
the HidHide cloak. Evidence: `wsgm.log` 2026-09-28 20:35:46, `PANIC (Avalonia lifetime crashed)` with the stack
`DesktopTray.OpenSettings` <- `DesktopTray.Add` click handler <- `NativeMenuItem` click <- `Dispatcher.MainLoop`: a
`SettingsViewModel` constructor bug ended the resident session. `SettingsActivation` already guards the same call
(`SettingsActivation.cs:91-98`); the tray menu does not. The review's SESSION-044 covers only `MessageWindow.WndProc`.
Recommendation: guard the tray menu actions the way `SettingsActivation` does, and add one Shell-mode
`Dispatcher.UIThread.UnhandledException` handler that logs and requests the normal runtime shutdown (after V-001 is
fixed) rather than letting the loop die. Panic should also run the device owner's bounded best-effort release where the
process can still do it, or the plan should state that the next start reconciles the HidHide ledger.

**SESSION-V-003 (medium). The plan and the review design the new runtime around the broken event.**
Files: `refactor-plan.md:59` ("App forwards lifetime events"), session.md section 4 (`ApplicationShutdownRequest.ShutdownLifetime`
-> injected `Action<int> shutdownLifetime`; "App ... forwards `ShutdownRequested` to the runtime"), planning-corrections
B3 (all cutoffs assume the shutdown starts).
Every shutdown improvement in R1, B3 and SESSION-B7 is unreachable today, because the cleanup they reorder does not
run for any programmatic exit. Recommendation: state in the plan that the runtime owns the exit sequence (cleanup, then
forced lifetime exit) and that `ShutdownRequested` is only the OS end-session input, mapped to SessionEnd. Promote the
one-line V-001 fix to an admitted small batch now, ahead of the refactor, since it is a release defect in 2.0.x.

**SESSION-V-004 (low, PLAUSIBLE). `Local\WSGM.Activate` uses default security.**
File: `src/WSGM/Program.cs:178-185`. The resident shell creates the activation event with the elevated token's default
DACL, which `UpdateExitWatcher` documents as unreachable from a non-elevated same-user instance (`UpdateExitWatcher.cs:11-17`).
A Start-menu `--shell --activate` launch whose self-elevation is declined continues non-elevated
(`SelfElevation.cs:76-81`) and its `new EventWaitHandle(..., SessionActivation.EventName)` would throw
`UnauthorizedAccessException` out of `MainAsync` before the unhandled-exception handlers are attached.
Recommendation: create it with the same SDDL helper as the exit events (name unchanged), or open it with
`EVENT_MODIFY_STATE` only in the activating instance.

**SESSION-V-005 (low). `OnSessionEnding` cannot escalate a shutdown that is already running.**
File: `src/WSGM/Shell/ShellSession.Shutdown.cs:16-26`. When a normal shutdown is running (`_disposed` already true),
`WTS_SESSION_LOGOFF` returns early, so SessionEnd never reaches the running cleanup and it may still start Explorer.
This is the concrete instance behind SESSION-032 inside the session class, not only in `App`. Recommendation: record
the reason with the runtime unconditionally; let the running shutdown read it before the Explorer step.

## Batch problems

1. **SESSION-B1** keeps the forwarding design (runtime fed by `ShutdownRequested`, lifetime exit injected as
   `Action<int>`), which preserves V-001. Its listed tests ("repeated shutdown same task", "startup failure exit 1 after
   clean cleanup") would pass with fakes and still ship a process that never cleans up. B1 must make the runtime drive
   cleanup before the forced exit and add the fake-lifetime test from V-001.
2. **Ordering.** V-001, the sticky exit code (SESSION-002 as it becomes real) and SESSION-007 are one change and a
   release defect; they should not wait for SESSION-B7, which depends on B6, the Device domain's admission close and the
   Steam UI host. Split the trigger fix out as an early small batch; B7 then only reorders steps.
3. **SESSION-B1 "verify Avalonia's session-end path (A7) and map it if needed"** is now answered: it is needed
   (SESSION-007 corrected). Make the mapping and the no-cancel/ShutdownBlockReason decision explicit in B1 rather than
   conditional.
4. **SESSION-B1 "installer watchers and handoff only in Shell mode"** rests on refuted SESSION-003. Keep it only as
   "Settings mode does not run the Steam pre-stop" or drop it.
5. **SESSION-B3 "Panic posts the retire"** cannot run on the main Panic path because the dispatcher has stopped (SESSION-046
   correction). Destroy synchronously when Panic runs on the UI thread; skip otherwise.
6. **SESSION-B4** changes `Steam.IsRunning` semantics for protocol decisions on a PLAUSIBLE finding (SESSION-054). With
   `steam.exe`-only liveness, `LaunchDesktop`/`LaunchBigPicture` take the `ColdStart` path while a `steamwebhelper` may
   still be alive, and `ColdStart` runs `SteamInputShim.Reconcile`, which the Core guide permits only during a proven
   cold start. Require log evidence of the lingering-helper case before changing it, and keep `ColdStart`'s shim step
   gated on both process names.
7. **SESSION-B6 A3 (mode authority replaces live probes)** should be dropped as a defect fix (SESSION-012 refuted). If
   kept as a performance change, the query must still answer from `IsDesktopShellRunning` semantics (taskbar owned by
   explorer.exe), not from `_inGameMode`.
8. **SESSION-B7 tests** use fake owners and a fake clock but never the real trigger. Add one test at the runtime/App seam
   that proves a tray Exit, an update request and a restore-shell request each reach `ShellSession.ShutdownAsync`
   exactly once, and one that proves an OS end-session request is handled as SessionEnd with zero Explorer calls.
9. **Manual matrix (M01)** needs explicit rows the review's list lacks: tray "Exit WSGM" on a device session (pad visible
   again, cloak off), update handoff (`Installer shutdown handoff completed ... outcome=Clean` in wsgm.log, setup
   Completed without the full grace wait), restore-shell from Game Mode on a device session, and sign-out/shutdown from the
   overlay power menu without a blocker screen.
