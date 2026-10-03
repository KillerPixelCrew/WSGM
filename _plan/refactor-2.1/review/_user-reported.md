# Maintainer-reported bugs

## USER-001 (high): RTSS overlay only appears after the QAM was opened once

Reported by the maintainer on 2026-10-03: in a game the RTSS overlay does not show until the Quick Access Menu has been opened once.

### Cause

`PerformanceService` (src/WSGM/Core/PerformanceService.cs) does three things only inside `RefreshAsync`: start RTSS when it is installed but not running (`RtssLauncher.TryStartAsync`, line 375), read back state, and write the desired values again on drift (`ApplyEffectiveDesiredAsync("drift-repair")`, line 382). `RefreshAsync` runs from `PollAsync` (line 594), and `PollAsync` waits while `_observers.Count == 0` (line 599). The only other caller is the resume refresh in `ShellSession.Power.cs:151`.

There are two observers:

- `SteamUiSessionHost.UpdatePerformanceObservation` (SteamUiSessionHost.cs:1574). It takes the lease only when the frame-limit row or `SteamPerformanceSurface.OverlayLevelRow` patch is `Verified`. Those `SteamQuickAccessRowPatch` verifications require Steam's performance root (`performanceRoot`, `nativeFields`, `nativeLayout` in `CommonRequiredCounts`), which exists only after the QAM performance panel has rendered.
- The WSGM overlay's performance page (`OverlayWindow.Sources.cs:361`).

At startup the profile fan-out (`ShellSession.Performance.cs:349`) calls `ApplyProfilesAsync`, which writes the overlay level once. If RTSS is not running yet, `ApplyOneAsync` gets a probe that is not `Ready` and returns `Rejected` (line 449). After that nothing retries. Nothing starts RTSS either, because the launcher only runs from the gated poll. Opening the QAM mounts the rows, verifies the patch and takes the lease. The poll then starts RTSS and drift repair writes the level. `_osd.SetLevel` runs, and the overlay appears.

The same gap covers a user who closes RTSS mid-session: it is restarted only while a UI is observing.

### Maintainer decision (2026-10-03): RTSS starts with WSGM and WSGM keeps it alive at all times

This is binding and replaces the on-demand launch described in docs/rtss.md "WSGM starts RTSS":

- **Start with WSGM.** When `PerformanceService` starts with RTSS integration enabled (`Performance.Enabled`, or overlay-test's simulated adapter), it probes immediately. If it gets `NotRunning`, it starts the verified RTSS executable right away. It does not wait for the first poll, an observer or a profile write. Switching RTSS integration on at runtime does the same.
- **Keep it alive.** Mirror HC's `RTSSPlatform.Start` and `Process_Exited` with `KeepAlive`: hold the running RTSS process (the one WSGM started, or the one discovery found) and restart it when it exits. The unconditional poll (below) is the backstop for an exit the hook missed and for an RTSS that was never running.
- **Remove the 30 s `RtssLauncher.RestartCooldown`.** HC has none, and the maintainer wants RTSS up at all times. Keep the existing safety rules: start only the executable discovery verified, and only on a `NotRunning` probe, so a second instance is never started. The 10 s settle stays, so a start is confirmed by the next probe and not re-fired on top of itself.
- **Integration off means no launch, and WSGM never kills RTSS.** RTSS stays detached and outlives WSGM, as today. Only a user who switched RTSS integration off in Settings opts out of the launch.
- Update docs/rtss.md "WSGM starts RTSS", its log-line table (the cooldown line goes) and docs/decisions.md.

### Best solution (mirror Handheld Companion)

HC's `RTSSPlatform` (_ref/HandheldCompanion/.../RTSSPlatform.cs) starts RTSS in `Start()`, restarts it on `Process.Exited`, and runs its 2 s `Watchdog_Elapsed` unconditionally. The watchdog compares the target FPS with the profile, rewrites it when it differs, and re-enables OSD. It is never tied to a UI being open.

Do the same here and remove mechanism rather than add it:

1. Remove the observation gate from `PerformanceService`: delete `ObservationGate _observers`, `AcquireObservation`, `ObserverCount`, and the `_observers.WaitAsync` branch in `PollAsync`. The poll runs for the service's whole lifetime. When `Enabled` is false it only probes, never writes; `Drifted()` already returns false when disabled, and `RtssLauncher.ShouldStart` already refuses when disabled.
2. Delete the lease plumbing that existed only for this gate: `UpdatePerformanceObservation`, `ReleasePerformanceObservation`, `_performanceObservation` and `_observationGate` in `SteamUiSessionHost`, `PerformanceOverlayBridge.AcquireObservation` (line 160), and `_performanceObservation` in `OverlayWindow.Sources.cs`. Check `RunningApplicationTarget.cs:570/652` separately: that is a different monitor's lease, not RTSS.
3. Keep the 5 s poll interval and the state-change dedup in `RaiseStateChanged` (no UI churn per poll). The probe has no per-sample allocation concern; it is a slow-path poll, like HC's watchdog.
4. Do not add a "retry rejected write" path. Once the poll runs unconditionally, the existing drift repair writes the desired level when RTSS becomes `Ready`. The rejected write was refused before dispatch, so this is not a retry of an uncertain write.

Tests (tests/WSGM.Tests, filter `FullyQualifiedName~PerformanceService`):

- A fake adapter reports `NotRunning`, then `Ready`, with no observer. The launcher fake is invoked, and after `Ready` the overlay level and frame limit are written once by drift repair.
- Disabled service: the poll probes but never writes or starts RTSS.
- Remove or rewrite the tests that assert polling stops without observers.

### Second contributing defect: the overlay level waits for a per-game executable it does not need

The maintainer suspected Game/Global profile switching. The switching itself is not gated on the QAM: `RunningApplicationCoordinator` takes its monitor lease in its constructor (RunningApplicationCoordinator.cs:49) and holds it for the session. A game start does reach `PerformanceService.ApplyProfilesAsync` as an `application-transition`, and that is the write that should turn the overlay on. It fails in two ways:

1. RTSS is not running. The write is refused, as described above.
2. The target is known but its executable is not. Steam store titles first publish with no executable: the log shows "executable profile unavailable, global RTSS policy remains active". `ApplyOneAsync` then returns `Deferred` for every control (PerformanceService.cs:527), including `OverlayLevel`. The overlay level does not need a per-app RTSS profile: the level is WSGM's own renderer state (`RtssOsdRenderer.SetLevel`), and its presentation gate falls back to the global profile (`OverlayActivationProfiles` with an empty name). The deferral contradicts the log line, which says the global policy remains active. If the foreground pairing never resolves the executable, a per-game overlay level never applies. Opening the QAM moves the foreground to Steam and back, which hands `RunningApplicationTarget.ReportForeground` a fresh foreground event, resolves the executable and triggers the transition write. That is another way "open the QAM once" makes the overlay appear.

Best solution: in `ApplyOneAsync`, defer only `FrameLimit` (the one control written into an RTSS profile file) when the executable is unknown. Let `OverlayLevel` proceed with `EffectiveRtssProfile` returning the global profile, as the log line already promises. Once the executable resolves, the transition write re-runs and adds the per-app `EnableOSD` repair. Test: target with `RtssProfileName == null`, desired overlay level 2. The adapter receives an `OverlayLevel` apply on the global profile, and `FrameLimit` is still `Deferred`.

Log lines that tell the two causes apart after a reproduction: `RTSS is installed but not running; starting it` appearing only after the QAM was opened means cause 1. `rtss.command.OverlayLevel ... Deferred` at game start means cause 2. `RTSS OSD level N` marks the moment the overlay turned on.

Manual check: with RTSS not running, start WSGM, launch a game without opening the QAM. RTSS starts within one poll plus its 10 s settle, and the overlay appears at the saved level.
