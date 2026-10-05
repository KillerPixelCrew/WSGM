# Boot, shell takeover and session transitions

How WSGM gets from Windows sign-in to Steam Big Picture with Explorer gone, and how it moves between
game and desktop mode afterwards. The Steam-side cold-start hang and the CEF transport gate are in
[the Steam CEF system](steam-cef-system.md); elevation and the per-game launch wrapper in
[elevation](elevation.md); what setup installs, asks and restarts in [setup](setup.md); why WSGM is
elevated at all in [decisions](decisions.md).

## Process modes

The Start Menu shortcut runs `WSGM.exe --shell --activate`, and the installer can put the same
shortcut on the Desktop. With Explorer running, that starts the resident Desktop session. A repeat
launch signals the existing mutex owner to open the Overlay, including requests queued during
startup. No arguments still opens Settings. Setup creates, updates and removes the shortcuts.

Two independent settings decide what a sign-in produces: `StartAtSignIn` and `StartMode` (`Desktop`
or `Game`). Starting with Windows and taking the screen over are separate choices, so a desktop PC
can have the first without the second. `BootManifestWriter` projects the pair into `GameModeBoot`
and `DesktopResident`, both false when the sign-in start is off.

Desktop Mode is a complete resident session, not a reduced agent. It keeps the plugins, overlay,
hotkey, chord, application monitor, performance services, permitted Steam integration, card services
and config watching. It starts the windowed Steam client itself after the input-desktop barrier, so
Steam inherits WSGM's integrity rather than the user's own autostart; the takeover of Windows' own
Steam startup entries that keeps it that way is in [setup](setup.md#the-steam-autostart-takeover).
Explorer stays the shell: no takeover, replacement tray host, Game display posture, startup-app
sequence or Big Picture request.

Desktop Mode shows a WSGM notification icon with Open WSGM, Enter Game Mode, Settings and Exit WSGM.
Primary activation opens the Overlay; Settings focuses its existing window. The icon is hidden in
Game Mode and disposed during shutdown, and it is separate from Game Mode's `TrayHost`. Exit is the
ordinary coordinated shutdown: integrations and runtime resources retire, Explorer is restored, and
the interactive process ends. The logon service stays installed for the next sign-in; Exit neither
uninstalls it nor changes the configured next-logon preference.

`Program.DecideMode` picks one mode from the command line. WSGM never registers as the Windows
shell, so no arguments means Settings.

| Flag                           | Mode                                  |
| ------------------------------ | ------------------------------------- |
| `--boot`                       | service-launched takeover at logon    |
| `--shell`                      | resident shell session                |
| `--shell --desktop-resident`   | resident Desktop session, no takeover |
| `--settings` (or no arguments) | Settings window                       |
| `--overlay-test`               | overlay without a shell session       |

Settings mode first asks an existing resident session to open its Settings window. An accepted
request exits the launcher before Avalonia starts; otherwise Settings runs standalone. The resident
activation endpoint accepts only the fixed, payload-free Settings request, including from desktop
shortcuts at medium integrity, which keeps Settings on the resident's controller input owner without
starting or elevating a second shell session.

Only shell mode holds the single-instance mutex `Local\WSGM.Shell`, and the installer keys its
restart decision off it. A crash-loop breaker counts shell starts: three inside two minutes disarm
the sign-in start (`GameModeBoot=false` and `DesktopResident=false` in boot.json, `StartAtSignIn`
off, shell snapshot restored, Explorer started if none runs). The exit of a session that started
resets the counter, whatever its cleanup outcome; otherwise two update restarts plus a sign-in
inside two minutes read as a loop. A failed startup does not reset it. `--restore-shell` disarms it
the same way. It first signals `Local\WSGM.ExitForRestoreShell` and waits up to 45 seconds for a
resident shell to exit, because that shell still owns a Shell_TrayWnd that must never coexist with
Explorer's. The resident's normal shutdown restores Explorer, and the recovery process starts
Explorer only when the desktop shell is still missing. A resident that left without stopping its
Explorer anchor leaves the restore to that anchor, so the recovery process first waits up to 15
seconds for the desktop instead of racing it. Both paths leave `StartMode` alone, so re-enabling in
Settings restores the chosen mode.

`Panic()` is the in-process, best-effort recovery: restore the shell snapshot, destroy the tray
host, hand recovery to the verified shell anchor when one exists, otherwise start Explorer if none
is running. The logon service's watchdog is the robust outer layer.

Shell mode also watches `config.json` (FileSystemWatcher, 500 ms debounce, then
`OverlayController.ApplyConfig`). A reload replaces the config object wholesale, so runtime state
lives on controllers, never in the config.

## Logon service and boot flow

`WSGM.LogonService` is a SYSTEM service on the raw SCM API with `SERVICE_ACCEPT_SESSIONCHANGE`. It
reacts to `WTS_SESSION_LOGON` only; console connect is ignored so a fast-user switch keeps whatever
is running. A startup sweep catches autologons that beat the auto-start service; a session logged on
less than 60 s ago counts as fresh.

The service reads the per-user boot manifest `%LOCALAPPDATA%\WSGM\boot.json`, which WSGM projects
from config.json on `--setup`, on every Settings save and on every shell start. It treats the
manifest as untrusted and only ever launches the named executable as that user, through
`CreateProcessAsUserW`. When the manifest asks for elevation it uses the user's linked elevated
token, which is legal under the service's SeTcbPrivilege and raises no UAC prompt.

The service logs to `%ProgramData%\WSGM\wsgm-service.log`, because SYSTEM must not write user
directories. WSGM's own `Run mode: Shell (service boot, elevated=…, session N)` line keeps wsgm.log
the primary surface.

### The service fires before Winlogon starts Explorer

Device-verified (2026-08-07). `--boot` therefore runs the takeover unconditionally, and the
readiness poll is what waits for Explorer to appear. Gating the takeover on "is Explorer running" at
start once left Explorer alive behind Big Picture, next to WSGM's tray host.

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

The splash's "Switch to desktop" button is a recovery owned by `ShellSession`. During steps 2 and 3
it cancels those waits. Once Explorer's orderly exit has been requested it cannot be undone, so the
button skips every game-mode side effect and completes an ordinary desktop transition, which starts
Explorer again. It does not go through the `SessionModes` transition gate the takeover already
holds, and it must not let Big Picture start afterwards.

### The watchdog waits for the anchor before starting Explorer itself

The service keeps the launched pid. On a dirty exit with an active session and no Explorer, it gives
the session-owned shell anchor five seconds to restore a normal medium, jobless Explorer. Only if no
shell appeared does it start Explorer itself, with the unlinked token because Explorer must stay
unelevated, once per logon, and it never relaunches WSGM. The grace keeps the anchor and the
watchdog from creating competing shells; the watchdog remains the outer fallback when the anchor is
absent or broken.

## How Explorer is ended

### Desktop integrations leave before Explorer

`Core\DesktopAppLifecycle.cs` holds the hardcoded integration list. Each rule names exact primary
processes, an exit command or hidden event-window class, restart arguments and console/editor
policy. `DesktopAppProcessBackend` supplies the Windows operations; `ExplorerDesktopHost` owns the
captured instances under its transition gate. Add future Explorer-hooking applications to this list
rather than adding another boot or mode-switch branch.

Immediately before Explorer's exit, WSGM captures the listed processes in its own Windows session,
with their executable paths, PIDs, start times and elevation. Nothing is launched merely because it
is installed. Both the boot takeover and resident Game Mode entry use this path. An unreadable
process, a failed exit or a respawning integration refuses takeover, and a partial failure restores
the affected apps while retaining Explorer. Captured identity is checked again before a process is
stopped; services and unrelated processes are never selected by a substring match.

| Integration      | Exit                                                                                                         | Desktop return                                        |
| ---------------- | ------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------- |
| DisplayFusion    | Its sibling `DisplayFusionCommand.exe -closeall`, then wait for exit                                         | Captured `DisplayFusion.exe`                          |
| Wallpaper Engine | Post `WM_CLOSE` to its PID-owned `WPEEventWindow`, then wait for exit                                        | Same executable with `-silent`                        |
| LittleBigMouse   | Close an open Avalonia editor first, allowing its save prompt; then terminate the captured UI and hook trees | Captured hook without a console, then the captured UI |

DisplayFusion's
[command-line guide](https://www.displayfusion.com/HelpGuide/DisplayFusionCommandLineTool/)
documents its full-exit command. Wallpaper Engine's documented `-control stop` only stops wallpaper
playback, so it is not used as proof of process exit. LittleBigMouse's installed 5.6.0 source
(`48f7ef83b8c87d6bdec06d560fa88a4d87bd0d27`) shows that its UI restarts a dead hook, so stopping the
hook alone cannot keep it inactive; the rule covers the current Avalonia UI and Hook process names.
These are implementation references, not a live transition pass.

Normal desktop restoration, failed-entry recovery and coordinated WSGM exit restart the remembered
applications only after Explorer is verified usable, and the restarts preserve the captured
elevation:

- Normal apps use the existing unelevated launcher with their installation directory.
- Previously elevated GUI apps use ShellExecute `runas`.
- Console-free helpers use direct process creation with `CreateNoWindow` from the elevated host,
  which respects compatibility elevation flags that reject direct creation with error 740 even from
  the elevated resident.

All of them use the original executable paths, a bounded wait and a running-instance check. The
startup-app sequence and the auto-relaunch watcher suppress listed integrations while the desktop is
suspended. An uncertain launch is not dispatched again, and Windows sign-out or shutdown does not
restart them. Ownership lives in memory for the resident session; the independent shell-anchor and
watchdog crash recovery restores Explorer only.

### Explorer is asked to exit through its own "Exit Explorer" command

`ExitExplorerAndWait` (`Core\ExplorerControl.cs`) posts `0x5B4` (`WM_USER+436`, the
Ctrl+Shift-taskbar "Exit Explorer" command) to Explorer's pid-verified `Shell_TrayWnd`. That
intentional shutdown is the only exit Winlogon's AutoRestartShell does not respawn. Tried and
disproven (2026-08-07): plain `Process.Kill`, which Winlogon respawns, and Restart Manager
`RmShutdown`, which wedged a freshly logged-on Explorer for about 30 s with error 351 and then
respawned it.

The taskbar's exact process handle is retained before the command is posted. File Explorer processes
that do not own the desktop do not block takeover. A new taskbar owner is a replacement shell, and
WSGM gives it one orderly attempt within the same deadline.

Explorer is never terminated: a killed shell process is what Winlogon's AutoRestartShell answers
with a respawn (2026-08-08). After both `Shell_TrayWnd` and `GetShellWindow` disappear, the original
process finishes on its own. If it is still running after 3 s, its remaining windows are sent
`WM_CLOSE`, the first step of Task Manager's End task, and nothing further. Success requires 1.5 s
of stable shell absence, long enough to catch a respawn inside the exit step. A retired process that
still owns no shell after 10 s does not hold Game Mode back: it has no taskbar, and the tray host
checks for Explorer's desktop shell, not for any `explorer.exe`. On refusal or timeout, the shared
desktop-return sequence restores the layout, shell and captured integrations before optional leave
actions.

The process outlives its windows because any shell extension can hold a reference on explorer.exe
through `SHGetInstanceExplorer`, and the process ends only when the last one is released. When a
retired shell is still running after 3 s, the log names the modules it loaded from outside the
Windows directory, so the extension holding it can be identified without a probe on the device.

The retired process's exit code is read once it exits. Winlogon's AutoRestartShell relaunches a
shell that stopped unexpectedly and leaves a clean exit alone, and "Exit Explorer" exits with 0. A
non-zero code therefore predicts a respawn: entry then watches for the replacement taskbar for 8 s
(device logs put it at about 3 s) and gives it the one orderly attempt, instead of racing the tray
host against it.

A new Explorer started beside a lingering retired one comes up unresponsive (2026-09-13): Explorer
takes the `ExplorerIsShellMutex` on start and polls `GetShellWindow` for 3 s to decide whether it is
the shell or a folder window, so a shell still winding down confuses it. Desktop return therefore
waits for a retired process that Game Mode entry left running, asking its windows to close again, up
to the transition deadline less an 8 s launch reserve, before it starts Explorer. The same day's fix
released (killed) that process after 2 s instead; an Xbox Ally X, whose orderly exit takes longer,
then fought a Winlogon respawn on every entry and failed to create the Game Mode tray (2026-09-25),
so the release was removed again.

Folder windows have run in their own explorer.exe since Windows 10 1903, so a process count never
answers "is the desktop up". Every mode decision (tray host, overlay swipes and scale, the mode
toggle, resume at shell start, Settings) asks `ExplorerControl.IsDesktopShellRunning`, which
requires a current-session `Shell_TrayWnd` owned by the canonical explorer.exe.

## How Explorer is restored

### A pre-captured medium, jobless anchor starts Explorer on a normal desktop transition

Explorer started by the de-elevating scheduled task inherits the Task Scheduler's job, and desktop
launchers such as Mod Organizer 2 then fail `CREATE_BREAKAWAY_FROM_JOB` with error 5 (see
[elevation](elevation.md)). So immediately before each orderly exit WSGM resolves the current
`Shell_TrayWnd` owner. The normal parent route accepts it only if `GetShellWindow` names the same
owner, its image is `%WINDIR%\explorer.exe`, it is in the current session, at medium integrity and
able to supply a jobless child. Capturing this launch parent needs no idle UI thread and no separate
stability wait, because display changes and desktop hooks can briefly delay Explorer messages;
responsive windows and stable ownership are still required when the restored desktop is verified.
WSGM keeps that process as the `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` and starts one fixed-purpose
medium, jobless anchor under it before the old shell exits (`Core\ExplorerShellAnchor.cs`, installed
as the same payload under the image name `WSGM.ShellAnchor.exe`).

The anchor accepts one authenticated per-session `start` command for the fixed Explorer path. WSGM
owns the child handle, bounds every pipe operation, stops only that owned process on failed setup,
and disposes or replaces the anchor together with the shell session. Capture, restore, replacement
and disposal are serialized in the session owner, and disposal closes admission before waiting for a
running operation. A named per-session stop event (`Local\WSGM.ShellAnchor.Stop.…`) lets a new run
retire only a stale anchor.

Owner loss is judged strictly. Pipe EOF alone is not owner loss: the anchor keeps the recovery role
until the retained owner process exits or the stop event is signalled. A faulted owner wait is not a
settlement either: the anchor keeps serving the pipe, retries a liveness observation, and otherwise
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
produced happens to be jobless; its deadline rules are in [elevation](elevation.md). A job-bound
taskbar is never ended without a verified repair owner. For a canonical, ready, current-session
medium Explorer that is already in a job, WSGM duplicates that shell's primary token and creates the
fixed anchor through `CreateProcessWithTokenW`, without using the job-bound shell as the process
parent. The anchor must still pass the image, session and medium-integrity checks before Explorer
receives an exit request. It must also be jobless unless the Explorer it replaces was job-bound:
some OEM images, such as the Xbox Ally X's, start Explorer in a job at logon, and the anchor created
from it lands in a job too (2026-09-25 tester log, every boot). Such an anchor is no worse than the
desktop the user already had, so takeover proceeds, the anchor is logged as degraded, and the
restored Explorer is reported as a degraded desktop. An unknown job membership is still refused, and
failed creation or verification preserves the existing desktop.

### Shutdown keeps the anchor alive until the desktop is verified

Application shutdown rejects new mode and Steam-launch commands and waits for the in-flight
transition and boot worker under one outer deadline. Device cleanup runs before that wait. The
process runtime owns one cleanup attempt before forcing the Avalonia lifetime to exit. Tray Exit,
restore-shell, update, uninstall and startup failure use that owner. Startup failure keeps exit code

1. OS session end runs the five-second cleanup without cancelling Windows' request, starting
   Explorer or closing Big Picture. Session end during another exit tightens its deadline and
   suppresses desktop restoration before dispatch.

The anchor stays alive if the deadline or the desktop verification fails, so owner-loss recovery
still has a launch path that is jobless whenever the original shell was. Before retiring the anchor,
normal disposal verifies or restores a usable desktop; logoff retires it without launching. Logs
record source and result pid, both shell-surface owners, route, session, integrity, job state,
readiness, elapsed time, dispatched state and the Win32 query errors.

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

`Shell\SteamMonitor` polls `steam;steamwebhelper` every 5 s. Its `Paused` flag means a transition is
in flight, so nothing reacts to Steam while the session owns it. A deliberate "Close Steam" sets
`SessionModes.SteamClosedByUser` instead, because a desktop session watches Steam continuously and
would otherwise start it straight back up; any request that wants Steam running again clears it.

`Shell\SteamExitPolicy` decides what an observed exit means. Game mode needs Steam on screen, so it
relaunches Big Picture or shows the overlay, the only surface left. A desktop session has Explorer,
so it either starts the windowed client again or does nothing, and it never interrupts the user with
the overlay. `SessionModes` applies the policy for the monitor it was given, so it runs whether or
not an overlay exists; it decides again when the 10 s delay ends, and a session that is shutting
down relaunches nothing. For the show-overlay case it raises `SteamExitShowOverlayRequested`, which
the session's overlay controller answers.

Desktop mode: pause the monitor, close Big Picture, restore the layout the Game Mode session owed
the desktop, start Explorer through the anchor, run the configured leave actions, then resume
monitoring and supply the windowed client. The layout goes back before Explorer does, as the scaling
restore always has, because Explorer sizes its taskbar and desktop icons to whatever the displays
say when it starts.

Session plugin actions log their start and completion outcome without dumping their arguments. The
IR plugin opens and identifies a fresh Wi-Fi connection for each explicit action, because the
endpoint closes idle clients after two minutes. An uncertain transmission is never retried.

Game mode from the desktop is one cancellable transaction, `Shell\GameModeEntryTransaction.cs`. The
splash offers Cancel before Explorer exit and Switch to desktop afterwards. A desktop request is
retained while an exit or layout operation settles, never discarded, because entry owns the
transition gate. The layout, splash arming and UI commit are awaited, so a UI exception enters
recovery rather than escaping from a posted callback.

Normal return and every failed entry use `Shell\DesktopReturnSequence.cs`: leave Big Picture,
restore the desktop layout, retire the game tray, restore and verify Explorer, clear a successfully
restored layout record, then run optional leave actions. Each phase catches its own failure, and a
failed layout remains recorded. A failed desktop return never redirects the user back into Game
Mode. The splash closes when the desktop is ready, before slow IR actions finish, and repeated
completed returns do not replay the leave list.

Leaving Big Picture is verified, not fired and forgotten. The phase retracts the injected Steam UI
and closes the transport first
([Steam CEF system](steam-cef-system.md#retract-before-either-big-picture-mode-change)), sends
`steam://close/bigpicture`, then waits for Steam's Big Picture window to disappear. If it does not,
WSGM posts `WM_CLOSE` to that window directly and logs its handle and `IsHungAppWindow`. On
2026-09-26 the close request reached a Steam whose CEF renderer had stopped answering thirteen
seconds earlier, so nothing consumed it, and the rest of the return rebuilt the desktop underneath a
Big Picture window that was still up.

Shell readiness requires matching taskbar/desktop owners and responsive windows; surviving processes
or window handles alone are insufficient. Responsiveness alone is the one check that cannot condemn
a desktop. When the restore budget runs out and the canonical Explorer owns both surfaces and passes
every identity check but has not answered the 500 ms liveness probe, the result is `Degraded`
(`timeout-unresponsive-shell`), not `Failed`. `Failed` abandons the whole return: game mode already
retired, no desktop, no Steam, monitor paused. On 2026-09-26 that is what a shell blocked behind
Steam's hung window produced. A genuine failure now logs
`Desktop return abandoned: Explorer was not restored` once, as an error.

Big Picture is requested after the exit and after the layout, which reverses the old order. That
order was a latency optimisation, worth having when Steam was not already running. In a resident
desktop session Steam is already up, the splash covers the whole transaction, and a Big Picture
window created before the layout would be built on the wrong display at the wrong scaling. The
occlusion rules below are unchanged: detection is armed at the request, the fade starts on window
detection, and nothing re-activates Steam afterwards. The logon boot is stricter still: Steam starts
only after Explorer is gone.

The entry order, the display wait that has no deadline, and what compensation runs when are in
[common plugin contracts](plugin-system.md#game-mode-entry).

### The CEF transport stays closed until the Big Picture window exists

A cold-starting Steam that meets WSGM's patches never creates its window. So the transport stays
closed in game mode until the process-owned Big Picture window exists, and a transition that
requests Big Picture first retracts WSGM's injected UI state
(`ShellSession.PrepareSteamUiForBigPictureAsync`, bounded to 5 s) before it sends
`steam://open/bigpicture`. Automatic boot CEF mutations wait for the window too; card detection
starts immediately and defers only the live Steam change. The evidence, the healthy log shape and
the gate itself are in [the Steam CEF system](steam-cef-system.md#3-the-transport-gate).

## Big Picture occlusion and the splash

### Entry must arm detection before requesting Steam

The entry splash starts unarmed so waiting for a switched-off TV has no deadline. After the display
layout is applied, the transaction awaits `ArmSteamDetectionAsync` on the UI dispatcher before
requesting Big Picture, which enables both window detection and the 120-second Steam timeout.

The 2026-09-13 desktop log showed the missing handoff: Explorer exited cleanly and Steam's Big
Picture window was recognized at 14:32:35, but the unarmed cover stayed until the user chose Desktop
at 14:38:26, because `ArmSteamDetection` had no caller. Regression tests now require arming after
the display wait and before the Steam request, including waiting for dispatcher completion.

### Big Picture suspends rendering while occluded

Big Picture's CEF UI stops rendering while fully occluded, as it does under a game, so an intro
video that initializes under an opaque fullscreen cover stays black even after the cover leaves. The
boot splash therefore begins its fade immediately on Big Picture window detection, on a 250 ms poll
whose window probe runs on the thread pool, one at a time; the first fade tick drops the layered
alpha below 255, which lifts the occlusion. Never hold an opaque cover over a live Big Picture
window. A no-activate splash was tried and did not change the symptom.

A `steam://open/bigpicture` re-activation while the intro plays kills the video (the former
splash-to-Big-Picture "focus handoff"). After the splash closes, do not touch Steam; it takes the
foreground itself.

### The detection poll must not throw

The splash poll calls `WindowFinder.FindWindow`, whose `FindProcessIds` reads `Process.SessionId`
per candidate behind a blanket `catch`. Narrowing that catch to
`InvalidOperationException`/`Win32Exception` let another exception type escape the poll: Big Picture
was never detected, the splash never faded, and its cover sat over the live window as a black intro
on every boot (device, two reboots, 2026-08-12). Keep the catch blanket, and do not add an
unthrottled log call inside it: at 4 Hz across Steam's helper processes that alone fills the capped
log. On any poll that feeds splash dismissal or takeover progress, a swallowed exception is the
lesser failure, so prefer a throttled one-shot warning over a narrower catch.

## Open apps strip and tray host

The former bottom taskbar lives inside the quick access sheet. Switchable windows
(`WindowFinder.ListSwitchableWindows`) form a horizontally scrolling chip strip along the sheet's
bottom. Tray icons share that bottom rail, and `OverlayWindow.ComputeTrayMaxWidth` limits their
share to keep the app strip usable. Wi-Fi, Bluetooth, audio, brightness, keyboard and eject
utilities, plus battery and clock from `Shell\SystemStatus`, remain in the fixed header. Utility
targets keep their size while secondary status labels collapse at narrow logical widths. Chip
refreshes reconcile in place, because a wholesale rebuild destroys the focused button under the
gamepad cursor. The radio controls open the in-window `RadioManager` panel and never invoke
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

## Desktop start at sign-in

A Desktop start projects `DesktopResident`, and the service launches `--shell --desktop-resident`
with its usual user-token and elevation policy. That mode stays on Desktop even before Explorer
appears and runs no takeover, startup apps or Game Mode display posture. `GameModeBoot` takes
precedence when both flags are true. Old manifests omit `DesktopResident` and keep their previous
behavior. Crash-loop disabling clears both automatic launch choices.

Desktop startup and wake plugin actions are coalesced and serialized with mode changes, and are
suppressed while in or entering Game Mode; `--overlay-test` installs none of these hooks. The action
lists, their editor and the Game Mode entry order are in
[plugin-system.md](plugin-system.md#session-automation).

## Recorded desktop state recovery

Source cleanup on 2026-10-01 made the saved Game Mode return layout and audio snapshot reachable
from normal startup, explicit entry admission, coordinated exit, panic recovery and the early
`--restore-shell` escape. Explorer recovery runs independently of optional display/audio success.
The early escape attempts those optional restores after making Explorer usable, without requiring
logging or Avalonia initialization.

A failed restore keeps its snapshot. A timeout does not prove that a synchronous display call
stopped, so its owner and serialization gate remain until the call actually finishes. Startup stays
on the desktop and suppresses boot takeover when recovery is pending. A later explicit entry must
settle recovery before capturing a new return snapshot. Successful restoration clears only the
matching record after Explorer is verified. Shutdown isolates each UI cleanup so one failed resource
cannot skip another or prevent an Explorer attempt.

The logon service's grace check asks a fixed pre-UI probe whether the actual Explorer desktop shell
exists. An Explorer folder process alone is insufficient. A stalled probe is terminated through its
owned process handle. The existing crash-loop breaker remains deliberate recovery policy.

These are implementation changes, not a new live shell or hardware pass.
