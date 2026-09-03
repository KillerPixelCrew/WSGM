# Boot, shell takeover and session transitions

How WSGM gets from Windows sign-in to Steam Big Picture with Explorer gone, how it moves between
game and desktop mode afterwards, and how the installer stops and restarts all of it. The Steam-side
cold-start hang and the CEF transport gate are not covered here; see `docs\steam-cef-system.md`.
Elevation and the per-game launch wrapper are in `docs\elevation.md`.

Related:

- `docs\elevation.md` — de-elevation, the scheduled-task route and its budget rules, `WSGM.Launch`
- `docs\steam-cef-system.md` — the transport gate and retract-before-Big-Picture
- `docs\decisions.md` — why WSGM is elevated at all, and what stays per-user

## Process modes

`Program.DecideMode` picks one mode from the command line. WSGM never registers as the Windows
shell, so no arguments means Settings.

| Flag                           | Mode                                            |
| ------------------------------ | ----------------------------------------------- |
| `--boot`                       | service-launched takeover at logon              |
| `--shell`                      | shell session started by hand (dev deploy only) |
| `--settings` (or no arguments) | Settings window                                 |
| `--overlay-test`               | overlay without a shell session                 |

Only shell mode holds the single-instance mutex `Local\WSGM.Shell`; the installer keys its restart
decision off it. A crash-loop breaker counts shell starts: three inside two minutes disarms the
service boot (`GameModeBoot=false` in boot.json, the config flag off, shell snapshot restored,
Explorer started if none runs). A clean exit resets the counter, otherwise two update restarts plus
a sign-in inside two minutes read as a loop.

`Panic()` is the in-process, best-effort recovery: restore the shell snapshot, destroy the tray
host, hand recovery to the verified shell anchor when one exists, otherwise start Explorer if none
is running. The logon service's watchdog is the robust outer layer.

Shell mode also watches `config.json` (FileSystemWatcher, 500 ms debounce, then
`OverlayController.ApplyConfig`). A reload replaces the config object wholesale, so runtime state
lives on controllers, never in the config.

## Logon service and boot flow

`WSGM.LogonService` is a SYSTEM service on the raw SCM API with `SERVICE_ACCEPT_SESSIONCHANGE`. It
reacts to `WTS_SESSION_LOGON` only; console connect is ignored so a fast-user switch keeps whatever
is running. A startup sweep catches autologons that beat the auto-start service (a session logged on
less than 60 s ago counts as fresh).

The service reads the per-user boot manifest `%LOCALAPPDATA%\WSGM\boot.json`, which WSGM projects
from config.json on `--setup`, on every Settings save and on every shell start. The service treats
the manifest as untrusted: it only ever launches the named executable as that user, through
`CreateProcessAsUserW`. When the manifest asks for elevation it uses the user's linked elevated
token, which is legal under the service's SeTcbPrivilege and raises no UAC prompt.

The service logs to `%ProgramData%\WSGM\wsgm-service.log`, because SYSTEM must not write user
directories. WSGM's own `Run mode: Shell (service boot, elevated=…, session N)` line keeps wsgm.log
the primary surface.

### The service fires before Winlogon starts Explorer

Device-verified (2026-08-07). `--boot` therefore runs the takeover unconditionally and the readiness
poll is what waits for Explorer to appear. Gating the takeover on "is Explorer running" at start
once left Explorer alive behind Big Picture, next to WSGM's tray host.

The takeover (`ShellSession.StartBootTakeover`) runs in this order:

1. Show the splash, so it covers the booting desktop. It re-covers itself on display change, because
   posture is applied later.
2. Wait for the input desktop. `Core\InputDesktop` polls `OpenInputDesktop` for `winsta0\Default`:
   `WTS_SESSION_LOGON` fires while LogonUI still owns the screen, and `WTS_SESSION_DESKTOP_READY` is
   never delivered on the Claw. Without this wait Steam audio leaks behind the Welcome screen.
3. Wait for Explorer readiness: `GetShellWindow()` plus Explorer's `Shell_TrayWnd`, then an
   `ExplorerLogonSettleMs` settle (default 5000 ms), 60 s hard cap. A Big Picture window appearing
   under the opaque cover ends the wait immediately (see "Big Picture suspends rendering while
   occluded").
4. `ExplorerControl.ExitExplorerAndWait`, 30 s budget.
5. Apply posture, create the tray host, start the startup apps, skipping any that Explorer's
   autostart already launched. An optional `StartupDelayMs` wait ("First app delay") precedes the
   first; the rest start staggered, optionally elevated.
6. Start Steam, strictly after Explorer is gone.

The splash's "Switch to desktop" button is a recovery owned by `ShellSession`. While the takeover is
still in steps 2-3 it cancels those waits. Once Explorer's orderly exit has been requested it cannot
be undone, so the button skips every game-mode side effect and completes an ordinary desktop
transition, which starts Explorer again. It does not go through the `SessionModes` transition gate
the takeover already holds, and it must not let Big Picture start afterwards.

### The watchdog waits for the anchor before starting Explorer itself

The service keeps the launched pid. On a dirty exit with an active session and no Explorer, it gives
the session-owned shell anchor five seconds to restore a normal medium, jobless Explorer. Only if no
shell appeared does it start Explorer itself, with the unlinked token (Explorer must stay
unelevated), once per logon, and it never relaunches WSGM. The grace keeps the anchor and the
watchdog from creating competing shells; the watchdog remains the outer fallback when the anchor is
absent or broken.

## How Explorer is ended

### Explorer is asked to exit through its own "Exit Explorer" command

`ExitExplorerAndWait` (`Core\ExplorerControl.cs`) posts `0x5B4` (`WM_USER+436`, the
Ctrl+Shift-taskbar "Exit Explorer" command) to Explorer's pid-verified `Shell_TrayWnd`. That
intentional shutdown is the only exit Winlogon's AutoRestartShell does not respawn. Tried and
disproven (2026-08-07): plain `Process.Kill`, which Winlogon respawns, and Restart Manager
`RmShutdown`, which wedged a freshly logged-on Explorer for about 30 s with error 351 and then
respawned it.

Explorer pids are snapshotted first. Any Explorer pid outside the snapshot is a Winlogon replacement
and is never killed; killing it fights AutoRestartShell in a loop. Instead the orderly exit is
retried once against the respawned shell, a fresh Explorer that honors it within seconds. Both
attempts share one deadline: a fresh budget for the retry let a 15 s caller sit in the transition
for more than twice that. If the replacement persists, the exit fails open: desktop mode is
preserved and the user sees `Couldn't exit Windows Explorer safely`.

### A lingering remnant gets a full grace window or is left alone

A shell extension can hold the Explorer process open after the taskbar is gone. Snapshotted pids
that linger are terminated only after the taskbar was destroyed and only after `LingerGrace` (8 s).
Killing a remnant mid-shutdown is itself what Winlogon respawns; on the device that showed up as
"game mode needs two tries" (2026-08-08). A clean run has the remnant leave about 830 ms after the
taskbar. The grace is never shortened to fit the remaining budget: a remnant that did not get the
full window is left alone and the exit fails open. Success requires 500 ms of stable absence.

## How Explorer is restored

### A pre-captured medium, jobless anchor starts Explorer on a normal desktop transition

Explorer started by the de-elevating scheduled task inherits the Task Scheduler's job, and desktop
launchers such as Mod Organizer 2 then fail `CREATE_BREAKAWAY_FROM_JOB` with error 5 (see
`docs\elevation.md`). So immediately before each orderly exit WSGM resolves the current
`Shell_TrayWnd` owner and accepts it only if `GetShellWindow` names the same owner, its image is
`%WINDIR%\explorer.exe`, it is in the current session, at medium integrity and not in a job. WSGM
keeps that process as the `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` and starts one fixed-purpose
medium, jobless anchor under it before the old shell exits (`Core\ExplorerShellAnchor.cs`; installed
as the same payload under the image name `WSGM.ShellAnchor.exe`).

The anchor accepts one authenticated per-session `start` command for the fixed Explorer path. WSGM
owns the child handle, bounds every pipe operation, stops only that owned process on failed setup,
and disposes or replaces the anchor together with the shell session. Capture, restore, replacement
and disposal are serialized in the session owner; disposal closes admission before waiting for a
running operation. A named per-session stop event (`Local\WSGM.ShellAnchor.Stop.…`) lets a new run
retire only a stale anchor.

Owner loss is judged strictly. Pipe EOF alone is not owner loss: the anchor keeps the recovery role
until the retained owner process exits or the stop event is signalled. A faulted owner wait is not a
settlement either; the anchor keeps serving the pipe, retries a liveness observation, and otherwise
waits for the explicit stop rather than start Explorer beside an owner it could not classify. On
abnormal WSGM loss it waits briefly for another recovery actor, preserves any existing shell
surface, checks that the session is still active, and only then restores Explorer.

### Success is the observed taskbar owner, not the created pid

The transition completes only when `GetShellWindow` and `Shell_TrayWnd` share one owner for a stable
500 ms and that owner again passes the image, session, integrity and job checks. The pid from
process creation is diagnostic only. An already-valid shell is adopted (the early splash-cancel
case). A canonical current-session medium Explorer with unknown or positive job membership is a
degraded desktop. A wrong-image, wrong-session, elevated, uninspectable, owner-mismatched or
unsettled taskbar is a failure. Once an anchor request was dispatched, or may have crossed the pipe,
WSGM never dispatches the scheduled task as a second creator and never recreates `TrayHost` while
that late shell may still publish `Shell_TrayWnd`.

The scheduled-task route (`Core\UnelevatedLauncher.cs`) is last-resort recovery when no anchor
request was dispatched. Its result is always reported as degraded, even when the Explorer it
produced happens to be jobless; its deadline rules are in `docs\elevation.md`. An older-build
job-bound taskbar is never ended without a verified repair owner: takeover stays in desktop mode and
the UI asks for one sign-out or reboot.

### Shutdown keeps the anchor alive until the desktop is verified

Application shutdown rejects new mode and Steam-launch commands and waits for the in-flight
transition and boot worker under one outer deadline. Device cleanup runs before that wait. The
anchor stays alive if the deadline or the desktop verification fails, so owner-loss recovery still
has a jobless launch path. Before retiring the anchor, normal disposal verifies or restores a usable
desktop; logoff retires it without launching. Logs record source and result pid, both shell-surface
owners, route, session, integrity, job state, readiness, elapsed time, dispatched state and the
Win32 query errors.

### A dead designated parent still reparents; token inheritance is unproven

Measured on Windows 11 25H2 build 26200.9168 (2026-08-29) with a throwaway `cmd.exe` as the parent:
after the parent exits, a retained handle still lets `CreateProcessW` with
`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` succeed, and the child's recorded parent is the dead process.
Three runs, each with a live-parent control. Not measured: whether the dead parent also supplies the
token and job association, which needs a parent at a different integrity level. Until that is
answered the anchor stays the normal path, and a `CreateProcessW` that merely succeeds is no
evidence about where the token came from.

### Attended device acceptance is still required

Isolated policy tests cover the anchor path and its refusal and fallback classifications. Splash
cancellation before and after the exit, repeated transitions, abnormal-loss recovery, Process
Explorer job inspection and the Mod Organizer 2 breakaway launch must still be exercised on the
reference Claw. Unattended tests must not start or stop the live shell.

## Desktop and game transitions

`Shell\SessionModes` owns both transitions and the shared Steam start-plus-warning flow; the
`ShellSession` boot and the overlay's buttons both call it. `OverlayController` stays the UI owner
and surfaces `SessionModes.SteamStartFailed`. `TransitionInProgress` serializes transitions, and the
overlay ignores mode clicks while one runs.

Steam is driven with protocol URLs, which are UIPI-proof:

| Action                                             | URL                        |
| -------------------------------------------------- | -------------------------- |
| start or focus Big Picture (boots Steam if needed) | `steam://open/bigpicture`  |
| leave Big Picture                                  | `steam://close/bigpicture` |
| quit Steam                                         | `steam://exit`             |

`Shell\SteamMonitor` polls `steam;steamwebhelper` every 5 s. Its `Paused` flag is how desktop mode
and "Close Steam" suppress auto-relaunch and overlay-pop reactions.

Desktop mode: pause the monitor, close Big Picture, start Explorer through the anchor. Game mode
from the desktop: request Big Picture first with the monitor still paused, then run
`ExitExplorerAndWait` off the UI thread, so Steam's UI startup overlaps Explorer's linger and retry
instead of showing the wait before Big Picture appears. Only when Explorer is verifiably gone does
the UI thread apply game posture, recreate the tray host and game-mode services, and resume
monitoring. If Explorer refuses to exit, the transition sends `steam://close/bigpicture` and keeps
desktop mode. If desktop restoration fails before any Explorer launch was dispatched, rollback
reopens Big Picture before recreating game-mode services; a dispatched or late shell suppresses that
recreation so there are never two taskbars. The logon boot is stricter: Steam starts only after
Explorer is gone.

### The CEF transport stays closed until the Big Picture window exists

A cold-starting Steam that meets WSGM's patches never creates its window. So the transport stays
closed in game mode until the process-owned Big Picture window exists, and a transition that
requests Big Picture first retracts WSGM's injected UI state
(`ShellSession.PrepareSteamUiForBigPictureAsync`, bounded to 5 s) before it sends
`steam://open/bigpicture`. Automatic boot CEF mutations wait for the window too; card detection
starts immediately and defers only the live Steam change. The evidence, the healthy log shape and
the gate itself are in `docs\steam-cef-system.md`, "The transport gate".

## Big Picture occlusion and the splash

### Big Picture suspends rendering while occluded

Big Picture's CEF UI stops rendering while fully occluded, as it does under a game. An intro video
that initializes under an opaque fullscreen cover stays black even after the cover leaves. The boot
splash therefore begins its fade immediately on Big Picture window detection, on a 250 ms poll; the
first fade tick drops the layered alpha below 255, which lifts the occlusion. Never hold an opaque
cover over a live Big Picture window. A no-activate splash was tried and did not change the symptom.

A `steam://open/bigpicture` re-activation while the intro plays kills the video (the former
splash-to-Big-Picture "focus handoff"). After the splash closes, do not touch Steam; it takes the
foreground itself.

### The detection poll must not throw

The splash poll calls `WindowFinder.FindWindow`, whose `FindProcessIds` reads `Process.SessionId`
per candidate behind a blanket `catch`. Narrowing that catch to
`InvalidOperationException`/`Win32Exception` let another exception type escape the poll: Big Picture
was never detected, the splash never faded, and its cover sat over the live window as a black intro
on every boot (device, two reboots, 2026-08-12). Keep the catch blanket, and do not add an
unthrottled log call inside it; at 4 Hz across Steam's helper processes that alone fills the capped
log. On any poll that feeds splash dismissal or takeover progress, a swallowed exception is the
lesser failure. Prefer a throttled one-shot warning over a narrower catch.

## Open apps strip and tray host

The former bottom taskbar lives inside the quick access sheet. Switchable windows
(`WindowFinder.ListSwitchableWindows`) form a horizontally scrolling chip strip along the sheet's
bottom. Tray icons, the Wi-Fi/Bluetooth/audio/eject pills, and battery and clock from
`Shell\SystemStatus` sit in the header. `OverlayWindow.ComputeTrayMaxWidth` budgets the tray so
icons cannot push the fixed pills off a 1280-wide screen; chips and pills keep fixed sizes at every
count. Chip refreshes reconcile in place, because a wholesale rebuild destroys the focused button
under the gamepad cursor. The pills open the `RadioManager` radio panel and never invoke
`ms-settings:`, which the immersive shell cannot activate without Explorer in the session.

### The tray host is a window class literally named `Shell_TrayWnd`

That is how `Shell_NotifyIcon` finds a tray; without it closed-to-tray apps lose their icons in game
mode (`Shell\TrayHost`). The WM_COPYDATA wire format is parsed in the pure, unit-tested
`Core\TrayProtocol` (32-bit handle fields on every architecture). Three rules govern it.

**The tray host never coexists with Explorer's taskbar.** It is destroyed on
`SessionModes.DesktopModeStarting`, before Explorer starts, and recreated on `GameModeEntered`.

**The UIPI gate.** WSGM is usually elevated, and UIPI silently drops an unelevated app's WM_COPYDATA
unless `ChangeWindowMessageFilterEx(WM_COPYDATA, MSGFLT_ALLOW)` is applied to the tray window. No
shipped replacement shell runs elevated, so this gate is WSGM-specific; its device verification
reads from the `Tray host created (… WM_COPYDATA filter …)` and `Tray icon Added/Rejected` log
lines.

**Only callback messages in `WM_USER..0xFFFF` are relayed.** `TrayProtocol.IsRelayableCallback`
applies that range in `TrayHost.SendClick`; system messages such as `WM_CLOSE` are never forwarded.
The check is on the message rather than the target's integrity, because elevated tray applications
still need clicks. An out-of-range callback still registers, so shell32 does not enter an add/reject
loop; only activation is dropped, logged once per host. `WM_USER` is the lower bound because
WinForms uses `WM_USER + 1024` and Qt an even higher `WM_APP` value.

## Install, update and uninstall

The installer (`installer\WSGM.iss`) is `PrivilegesRequired=admin` because the machine service
demands it, while the app stays per-user: `{localappdata}` and HKCU belong to the elevating account.
This is the single-user-device design.

### Order on update

1. Record whether the shell is running (mutex `Local\WSGM.Shell`), so WSGM can be restarted in the
   same mode afterwards. The temporary stopped state is never classified as the previous mode.
2. Stop the logon service (`sc stop WSGMLogonService`). A live watchdog would see the killed WSGM
   and start Explorer mid-update, flipping the restart into desktop mode. Stopping it also frees the
   Program Files binary, including an abandoned preview's, which uses the same service name.
3. Signal `Local\WSGM.ExitForUpdate`. One SetEvent releases every WSGM instance, elevated ones
   included. WSGM asks Steam and the launch wrappers to exit under a bounded 10 s pre-stop, then
   runs its own 10 s cleanup, because the mapped Steam Input payload must be replaceable. Setup
   waits for both plus handoff margin (44 half-second iterations) before force-stop. A failed Steam
   pre-stop still starts WSGM cleanup.
4. Force-stop fallback: `taskkill` only primary `WSGM.exe` images in the installer's Terminal
   Services session.
5. Retire the shell anchor. Restart Manager excludes `WSGM.ShellAnchor.exe`
   (`CloseApplicationsFilterExcludes`), so the anchor gets its owner-loss recovery window and is
   ended only after it publishes `Local\WSGM.ShellAnchor.RecoverySettled`, through the same
   current-session filter, while setup holds that event open so a new anchor cannot enter the
   image-name kill. Without the acknowledgement, setup defers the companion's replacement rather
   than kill the only remaining desktop-recovery owner; a silent update skips the locked file
   instead of taking the automatic reboot `restartreplace` would cause.
6. Refuse replacement while Steam or a launch wrapper (`WSGM.Launch`, plus the retired
   `WSGM.Deelevate` and `steam-input-lease` names) remains in the session. Setup never terminates
   either tree, and a failed inspection counts as blocked.
7. `[Run]`: `WSGM.exe --setup` (per-user files, migrate off any legacy shell registration, the
   Xbox-FSE guard, the boot manifest), then `WSGM.LogonService.exe --install`
   (create-or-reconfigure, failure actions, start), then the USB/IP driver if its task was selected,
   then WSGM in its previous mode (`--shell` or Settings).

A refusal, retry or cancellation before file mutation releases the device-package reservations and
restores the old service through its installer-tagged start in the recorded runtime mode.

### Uninstall

`Local\WSGM.ExitForUninstall` selects a fixed 20 s WSGM cleanup and does not stop Steam. Removing an
older build falls back to the update event. `[UninstallRun]` order: service `--uninstall` (stop and
delete), `--unregister-shell` (a no-op on service installs, kept as the legacy restore),
`--uninstall-restore`, all before files are deleted. `[UninstallDelete]` also removes
`{autopf}\WSGM` and `{commonappdata}\WSGM`. The uninstaller holds the same global package and owner
reservations through `[UninstallDelete]`; cancellation before mutation restores the service and the
prior runtime.

### The exit events are a cross-version contract

A newer installer must still release an older running build. The event names, their access grant
(user SID plus Administrators `EVENT_MODIFY_STATE | SYNCHRONIZE`, `0x00100002`), the medium
mandatory label and the startup reset therefore stay compatible (`Core\UpdateExitWatcher.cs`). The
unelevated Settings instance needs the same grant to wait and reset, so narrowing it breaks ordinary
update shutdown.

Session end is a separate path. The resident shell holds a shared `WTSRegisterSessionNotification`
lease; `WTS_SESSION_LOGOFF` requests the five-second session-end shutdown before Avalonia exits.
Display-mute owns its own lease for unlock recovery, so toggling that feature cannot deregister the
shell's logoff signal.

### NeedRestart follows the USB/IP driver only

`NeedRestart` is true only when the USB/IP driver task was selected and the driver either reported a
reboot or reported nothing (stay conservative when the bounded status file is missing). Ordinary
upgrades are not marked for reboot. Silent setup always returns `False`, because `/VERYSILENT` could
otherwise reboot automatically.
