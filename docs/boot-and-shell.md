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

The Start Menu shortcut runs `WSGM.exe --shell --activate`; the installer optionally creates the
same shortcut on the Desktop. With Explorer running, this starts the resident Desktop session. A
repeat launch signals the existing mutex owner to open the Overlay, including requests queued during
startup. No arguments still open Settings. Shortcuts are updated and removed by Inno Setup.

Two independent settings decide what a sign-in produces: `StartAtSignIn` and `StartMode` (`Desktop`
or `Game`). Starting with Windows and taking the screen over are separate choices, so a desktop PC
can have the first without the second. `BootManifestWriter` projects the pair into `GameModeBoot`
and `DesktopResident`; both are false when the sign-in start is off. The retired
`GameModeBootEnabled` switch, and the residency that used to follow from enabled route automation,
are migrated by `Core\ConfigMigrations` on load: game-mode boot becomes a Game start, route
automation without it becomes a Desktop start, and neither leaves the sign-in alone.

Desktop Mode is a complete resident session, not a reduced agent. It keeps the plugins, overlay,
hotkey, chord, application monitor, performance services, permitted Steam integration, card services
and config watching, and it starts the windowed Steam client itself after the input-desktop barrier,
so Steam inherits WSGM's integrity rather than the user's own autostart. Explorer stays the shell:
no takeover, replacement tray host, Game display posture, startup-app sequence or Big Picture
request.

Desktop Mode shows a WSGM notification icon with Open WSGM, Enter Game Mode, Settings and Exit WSGM.
Primary activation opens the Overlay; Settings focuses its existing window. The icon is hidden in
Game Mode and disposed during shutdown. Exit uses the ordinary coordinated application shutdown:
integrations and runtime resources retire, Explorer is restored, and the interactive process ends.
The installed logon service remains available for the next sign-in; Exit does not uninstall it or
change the configured next-logon preference. This icon is separate from Game Mode's `TrayHost`.

### Steam autostart takeover

WSGM starts Steam so the client inherits WSGM's integrity. A Steam that Windows started first takes
that away without saying so, so `Core\SteamAutostart` looks for the places Windows would start it:
`Run` values in HKCU and HKLM including the 32-bit view, shortcuts in either Startup folder resolved
through `Interop\ShellLink`, and scheduled tasks with a logon trigger. Tasks are read as
language-neutral XML from `schtasks /Query /XML`, because the table output is localized. Matching
compares the resolved executable against Steam's own path, and an unquoted command is resolved the
way Windows resolves it, by successive prefixes rather than the first space.

`Core\SteamAutostartTakeover` disables an entry the way Task Manager's Startup tab does, by writing
Windows' own `StartupApproved` bytes, or by disabling the task. Nothing is deleted. The previous
state is recorded in `SteamAutostartDisabled` before the write, so an interrupted takeover is still
undoable, and the write is confirmed by a readback; an unconfirmed one stays pending. Restore only
undoes an entry that still carries WSGM's own value, so a decision the user made afterwards always
wins. HKLM and task changes need elevation and go through the `--disable-steam-autostart` one-shot,
which rescans and takes no name from its command line. A sign-in never prompts: an unelevated
re-check disables user-scope entries and warns about the rest. `--restore-steam-autostart` runs from
the elevated uninstall restore.

Quick Setup (revision 2) asks the two sign-in choices and lists what it found. The panel opens
before its read-only startup scan runs on a worker, so the window stays responsive while Task
Scheduler answers. Continue waits for the scan; a failed scan offers Try again and leaves Skip
available. Closing or skipping the panel discards a late scan result. With entries present, Continue
stays disabled until the takeover is allowed; Skip means off for all of it, as it does for the Steam
integrations. The takeover itself runs after the save, outside the config lock, because it may
prompt. Settings > System shows the state and offers "Take over again". That command also scans and
applies on a worker, and disables itself until the operation finishes.

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
shortcuts at medium integrity. This keeps Settings on the resident's controller input owner without
starting or elevating a second shell session.

Only shell mode holds the single-instance mutex `Local\WSGM.Shell`; the installer keys its restart
decision off it. A crash-loop breaker counts shell starts: three inside two minutes disarms the
sign-in start (`GameModeBoot=false` and `DesktopResident=false` in boot.json, `StartAtSignIn` off,
shell snapshot restored, Explorer started if none runs). `--restore-shell` disarms it the same way.
Both leave `StartMode` alone, so re-enabling in Settings restores the chosen mode. A clean exit
resets the counter, otherwise two update restarts plus a sign-in inside two minutes read as a loop.

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

### Desktop integrations leave before Explorer

`Core\DesktopAppLifecycle.cs` holds the hardcoded integration list. Each rule names exact primary
processes, an exit command or hidden event-window class, restart arguments and console/editor
policy. `DesktopAppProcessBackend` supplies the Windows operations; `ExplorerDesktopHost` owns the
captured instances under its transition gate. Add future Explorer-hooking applications to this list
rather than adding another boot or mode-switch branch.

Immediately before Explorer's exit, WSGM captures listed processes in its own Windows session,
including their executable paths, PIDs, start times and elevation. Nothing is launched merely
because it is installed. Both boot takeover and resident Game Mode entry use this path. An
unreadable process, failed exit or respawning integration refuses takeover. A partial failure
restores the affected apps while retaining Explorer. Captured identity is checked again before
stopping a process; services and unrelated processes are not selected by a substring match.

| Integration      | Exit                                                                                                         | Desktop return                                        |
| ---------------- | ------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------- |
| DisplayFusion    | Its sibling `DisplayFusionCommand.exe -closeall`, then wait for exit                                         | Captured `DisplayFusion.exe`                          |
| Wallpaper Engine | Post `WM_CLOSE` to its PID-owned `WPEEventWindow`, then wait for exit                                        | Same executable with `-silent`                        |
| LittleBigMouse   | Close an open Avalonia editor first, allowing its save prompt; then terminate the captured UI and hook trees | Captured hook without a console, then the captured UI |

DisplayFusion's
[command-line guide](https://www.displayfusion.com/HelpGuide/DisplayFusionCommandLineTool/)
documents its full-exit command. Wallpaper Engine's documented `-control stop` only stops wallpaper
playback, so it is not used as proof of process exit. LittleBigMouse's installed 5.6.0 source
(`48f7ef83b8c87d6bdec06d560fa88a4d87bd0d27`) shows that its UI automatically restarts a dead hook,
so stopping the hook alone cannot keep it inactive. These are implementation references, not a live
transition pass. The LittleBigMouse rule covers the current Avalonia UI and Hook process names.

Normal desktop restoration, failed-entry recovery and coordinated WSGM exit restart remembered
applications only after Explorer is verified usable. Restarts preserve the captured elevation:
normal apps use the existing unelevated launcher with their installation directory, and previously
elevated GUI apps use ShellExecute `runas`. Console-free helpers use direct process creation with
`CreateNoWindow` from the elevated host. This respects compatibility elevation flags that can reject
direct process creation with error 740 even from the elevated resident. Both use original executable
paths, a bounded wait and a running-instance check. WSGM's startup-app sequence and auto-relaunch
watcher suppress listed integrations while the desktop is suspended. An uncertain launch is not
dispatched again. Windows sign-out/shutdown does not restart them. Ownership is in memory for the
resident session; the independent shell-anchor/watchdog crash recovery restores Explorer only.

### Explorer is asked to exit through its own "Exit Explorer" command

`ExitExplorerAndWait` (`Core\ExplorerControl.cs`) posts `0x5B4` (`WM_USER+436`, the
Ctrl+Shift-taskbar "Exit Explorer" command) to Explorer's pid-verified `Shell_TrayWnd`. That
intentional shutdown is the only exit Winlogon's AutoRestartShell does not respawn. Tried and
disproven (2026-08-07): plain `Process.Kill`, which Winlogon respawns, and Restart Manager
`RmShutdown`, which wedged a freshly logged-on Explorer for about 30 s with error 351 and then
respawned it.

The taskbar's exact process handle is retained before posting the command. File Explorer processes
that do not own the desktop do not block takeover. A new taskbar owner is a replacement shell; WSGM
gives it one orderly attempt within the same deadline.

After both `Shell_TrayWnd` and `GetShellWindow` disappear, the original process gets two seconds to
finish. If it still holds the shell singleton, WSGM terminates that retained process only, without
its children. An active or replacement desktop is never force-closed by this path. Success requires
500 ms of stable shell absence after exit. On refusal or timeout, the shared desktop-return sequence
restores the layout, shell and captured integrations before optional leave actions.

This replaces the older wait-only policy after the attended 2026-09-13 failure: Explorer removed its
taskbar but stayed alive, and a new Explorer then created unresponsive shell windows. Keeping that
retired process alive stranded recovery. The maintainer requested the transition redesign; process
existence is no longer treated as proof of a usable desktop.

## How Explorer is restored

### A pre-captured medium, jobless anchor starts Explorer on a normal desktop transition

Explorer started by the de-elevating scheduled task inherits the Task Scheduler's job, and desktop
launchers such as Mod Organizer 2 then fail `CREATE_BREAKAWAY_FROM_JOB` with error 5 (see
`docs\elevation.md`). So immediately before each orderly exit WSGM resolves the current
`Shell_TrayWnd` owner. The normal parent route accepts it only if `GetShellWindow` names the same
owner, its image is `%WINDIR%\explorer.exe`, it is in the current session, at medium integrity and
able to supply a jobless child. WSGM keeps that process as the
`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` and starts one fixed-purpose medium, jobless anchor under it
before the old shell exits (`Core\ExplorerShellAnchor.cs`; installed as the same payload under the
image name `WSGM.ShellAnchor.exe`).

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
job-bound taskbar is never ended without a verified repair owner. For a canonical, ready,
current-session medium Explorer, WSGM can duplicate that shell's primary token and create the fixed
anchor through `CreateProcessWithTokenW`, without using the job-bound shell as the process parent.
The anchor must still pass the same image, session, medium-integrity and jobless checks before
Explorer receives an exit request. Failed creation or verification preserves the existing desktop.

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

`Shell\SteamMonitor` polls `steam;steamwebhelper` every 5 s. Its `Paused` flag means a transition is
in flight, so nothing reacts to Steam while the session owns it. A deliberate "Close Steam" sets
`SessionModes.SteamClosedByUser` instead, because a desktop session watches Steam continuously and
would otherwise start it straight back up. Any request that wants Steam running again clears it.

`Shell\SteamExitPolicy` decides what an observed exit means. Game mode needs Steam on screen, so it
relaunches Big Picture or shows the overlay, which is the only surface left. A desktop session has
Explorer, so it either starts the windowed client again or does nothing; it never interrupts the
user with the overlay.

Desktop mode: pause the monitor, close Big Picture, restore the layout the Game Mode session owed
the desktop, start Explorer through the anchor, run the configured leave actions, then resume
monitoring and supply the windowed client. The layout goes back before Explorer does, as the scaling
restore always has, because Explorer sizes its taskbar and desktop icons to whatever the displays
say when it starts.

Session plugin actions log their start and completion outcome without dumping their arguments. The
IR plugin opens and identifies a fresh Wi-Fi connection for each explicit action because the
endpoint closes idle clients after two minutes. An uncertain transmission is never retried.

Game mode from the desktop is one cancellable transaction, `Shell\GameModeEntryTransaction.cs`. The
splash offers Cancel before Explorer exit and Switch to desktop afterwards. A desktop request is
retained while an exit or layout operation settles; it is never discarded because entry owns the
transition gate. The layout, splash arming and UI commit are awaited, so a UI exception enters
recovery rather than escaping from a posted callback.

Normal return and every failed entry use `Shell\DesktopReturnSequence.cs`: leave Big Picture,
restore the desktop layout, retire the game tray, restore and verify Explorer, clear a successfully
restored layout record, then run optional leave actions. Each phase catches its own failure. A
failed layout remains recorded. A failed desktop return never redirects the user back into Game
Mode. Shell readiness requires matching taskbar/desktop owners and responsive windows; surviving
processes or window handles alone are insufficient. The splash closes when the desktop is ready,
before slow IR actions finish. Repeated completed returns do not replay the leave list.

Big Picture is requested after the exit and after the layout, which reverses the old order. That
order was a latency optimisation, worth having when Steam was not already running. In a resident
desktop session Steam is already up, the splash covers the whole transaction, and a Big Picture
window created before the layout would be built on the wrong display at the wrong scaling. The
occlusion rules below are unchanged: detection is armed at the request, the fade starts on window
detection, and nothing re-activates Steam afterwards. The logon boot is stricter still: Steam starts
only after Explorer is gone.

The entry order, the display wait that has no deadline, and what compensation runs when are in
`docs\plugin-system.md`, "Game Mode entry".

### The CEF transport stays closed until the Big Picture window exists

A cold-starting Steam that meets WSGM's patches never creates its window. So the transport stays
closed in game mode until the process-owned Big Picture window exists, and a transition that
requests Big Picture first retracts WSGM's injected UI state
(`ShellSession.PrepareSteamUiForBigPictureAsync`, bounded to 5 s) before it sends
`steam://open/bigpicture`. Automatic boot CEF mutations wait for the window too; card detection
starts immediately and defers only the live Steam change. The evidence, the healthy log shape and
the gate itself are in `docs\steam-cef-system.md`, "The transport gate".

## Big Picture occlusion and the splash

### Entry must arm detection before requesting Steam

The entry splash starts unarmed so waiting for a switched-off TV has no deadline. After the display
layout is applied, the transaction awaits `ArmSteamDetectionAsync` on the UI dispatcher before
requesting Big Picture. This enables both window detection and the 120-second Steam timeout.

The 2026-09-13 desktop log showed the missing handoff: Explorer exited cleanly and Steam's Big
Picture window was recognized at 14:32:35, but the unarmed cover stayed until the user chose Desktop
at 14:38:26. `ArmSteamDetection` previously had no caller. Regression tests now require arming after
the display wait and before the Steam request, including waiting for dispatcher completion.

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

### Install modes

Setup offers three modes plus Custom, named for the machine they suit rather than for the components
they carry:

| Mode                | Components               | A fresh install starts       |
| ------------------- | ------------------------ | ---------------------------- |
| Minimal (default)   | core                     | Game Mode, integration off   |
| MSI Claw 8 AI+ A2VM | core, device, controller | Game Mode, integration on    |
| Desktop first       | core                     | Desktop session, off         |
| Custom              | chosen by hand           | the configuration's defaults |

A mode decides two separate things, and they must stay separate. Which bytes install is Inno's
`[Types]`/`[Components]`. What the first run of those bytes does is passed to `--setup` as
`--profile=` and applied by `Core\InstallProfile.cs`, **only when the machine has no config.json**.
Re-running setup is how people repair and upgrade, so a mode that rewrote the start mode and the
integration switch each time would silently undo Settings; changing an installed machine's mode
means changing it in Settings, where it is visible. Custom names no mode on purpose: the user picked
components rather than an intent.

The Claw mode is the one that switches Device Integration on, because naming a device in setup is
the explicit choice the integration otherwise waits for, and installing the package without it would
read as a broken install. The package still refuses any machine whose SMBIOS identity does not match
it, so the mode cannot make a non-Claw pretend to be one. Chosen from Custom instead, the same bytes
install and stay inert until Settings enables them.

A mode seeds Quick Setup's answers rather than replacing them: the panel still appears on first run
so the start choices are confirmed and the Steam autostart takeover is consented to rather than
assumed.

### A device package on an install that has no room for it

The protected package slot is a directory an administrator can copy into, so a device package can
arrive on a Minimal or Desktop install long after setup ran. That combination is otherwise silent:
the package loads, controller management reports itself unavailable, and nothing says why.

`Core\DevicePrerequisites` answers it by looking at the machine rather than at the package, because
a device manifest declares no prerequisites: is a package in the slot, is Device Integration on, is
`libviiper.dll` beside WSGM, does the HidHide control device answer. When something is missing, the
overlay's Device page carries a banner naming it — the overlay because it is the surface a person
actually opens, and the Device page because that is where someone whose device is not working goes.

The banner offers **Enable Device Integration** and nothing else, and the split is not cosmetic.
Device Integration is WSGM's own setting. The virtual controller needs a kernel driver, and INV-020
forbids the runtime from installing one whatever its provenance: the USB/IP install restarts every
USB 3.0 hub, which drops the built-in controller, the touch digitiser and the keyboard. Underneath a
running Game Mode that leaves a person with no input and no way back, so it happens only while setup
is on screen. For that half the banner says to re-run setup and warns that a reboot follows.

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
