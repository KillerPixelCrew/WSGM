# Boot, shell takeover and session transitions

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

**Process modes** (`Program.DecideMode`): `--shell` / `--boot` (service-launched takeover) /
`--settings` / `--overlay-test`; the legacy auto mode (shell iff registered as shell and no desktop
alive) survives only for the migration window — new installs never register as shell, so no-args =
settings. Shell mode: single-instance mutex `Local\WSGM.Shell` (held only in shell mode — the
installer keys off it), crash-loop breaker (3 shell starts in 2 min → **disarm the service boot**:
boot.json `GameModeBoot=false` + config flag off + legacy shell unregister + explorer if none),
`Panic()` = legacy shell unregister (self-guarding no-op on service installs), destroy tray host,
start explorer if none is running. The logon service's watchdog is the robust outer recovery layer;
Panic is in-process best effort.

**Logon service + boot flow** (`src\WSGM.LogonService`, `Core\BootManifest.cs`,
`Core\BootManifestWriter.cs`, `Shell\ExplorerReadiness.cs`): the SYSTEM service (raw SCM +
`SERVICE_ACCEPT_SESSIONCHANGE`, NativeAOT, no ServiceBase) reacts to `WTS_SESSION_LOGON` only (not
console connect — fast-user-switch keeps what runs), reads the per-user
`%LOCALAPPDATA%\WSGM\boot.json` **boot manifest** (projected from config.json by WSGM on `--setup`,
settings saves, and every shell start; the service treats it as untrusted and only ever launches the
named exe AS THAT USER), and launches `WSGM.exe --boot` via `CreateProcessAsUserW` — with the user's
elevated **linked token** when the manifest says so (legal under the service's SeTcbPrivilege; no
UAC prompt at logon). A startup catch-up sweep covers autologons that beat the auto-start service
(fresh = logged on < 60 s). The service watchdog holds the launched pid: dirty exit + active
session + no explorer → start explorer with the UNLINKED token (explorer must stay unelevated), once
per logon, never relaunching WSGM. Service log: `%ProgramData%\WSGM\wsgm-service.log` (SYSTEM must
not write user dirs); WSGM's own `Run mode: Shell (service boot, elevated=…, session N)` line keeps
wsgm.log the primary surface.

**The service fires BEFORE Winlogon starts explorer** (device-verified 2026-08-07), so `--boot` runs
the takeover unconditionally — never gate it on `IsRunningInSession()` at start (that exact gate
once left explorer alive behind Big Picture next to WSGM's tray host); the readiness poll is what
waits for explorer to appear at all. The `--boot` takeover (`ShellSession.StartBootTakeover`):
splash FIRST (covers the booting desktop; re-covers itself on display change because posture applies
later) → **input-desktop barrier** (`Core\InputDesktop` polls `OpenInputDesktop` for winsta0\Default
— WTS_SESSION_LOGON fires while LogonUI still owns the screen, and WTS_SESSION_DESKTOP_READY is
never delivered on the Claw; without this gate Steam audio leaks behind the Welcome screen) →
`ExplorerReadiness` — `GetShellWindow()` + explorer's `Shell_TrayWnd`, then `ExplorerLogonSettleMs`
settle (default 5000 ms), 60 s hard cap, and **invariant-7 acceleration** (BP window appears under
the opaque cover → take over immediately) → `ExplorerControl.ExitExplorerAndWait(30 s)` → posture →
TrayHost → startup apps (skipping ones explorer's autostart already launched) → Steam, strictly
AFTER explorer is gone. The splash's **Switch to desktop** is a recovery/quickswitch owned by
`ShellSession`: while the service takeover is active it cancels the input-desktop/readiness waits
before Explorer shutdown; if Explorer's irreversible orderly-exit request already began, it skips
every game-mode side effect and completes the ordinary desktop transition, which starts Explorer
again. It must never compete through `SessionModes`' already-held transition gate or allow Big
Picture to start afterward.

**How Explorer is ended — device-settled, do not change the mechanism:** `ExitExplorerAndWait` posts
`0x5B4` (WM_USER+436, explorer's own Ctrl+Shift-taskbar "Exit Explorer" command) to explorer's
pid-verified `Shell_TrayWnd`. That intentional shutdown is the ONLY way Winlogon's AutoRestartShell
does not respawn the shell. PID-snapshot semantics: any explorer pid not in the initial snapshot is
a Winlogon replacement → cancel and **fail open** (preserve desktop mode, warn
`Couldn't exit Windows Explorer safely`); a replacement is NEVER killed (fighting AutoRestartShell
loops) — instead the orderly exit is retried ONCE against the respawned shell, which is a freshly
started explorer that honors it within seconds, and both attempts share ONE deadline (a fresh full
budget for the retry let a caller asking for 15 s sit in the transition for more than twice that).
Lingering snapshotted pids are terminated only after explorer destroyed its taskbar (a shell
extension can hold the process open — device-observed) **and only after a `LingerGrace` (8 s) window
in which the remnant is given the chance to leave on its own** — killing it mid-shutdown is itself
what Winlogon respawns (device-observed 2026-08-08 as "game mode needs two tries"; a clean run had
the remnant exit ~830 ms after the taskbar went). That grace is never shortened to fit the remaining
budget: a remnant that did not get the full window is left alone and the exit fails open. Success
requires 500 ms of stable absence. Two mechanisms are device-DISPROVEN (2026-08-07): plain
`Process.Kill` (Winlogon respawns) and Restart Manager `RmShutdown` (wedged a freshly logged-on
explorer ~30 s, error 351, then respawn). The full working-era implementation is preserved in the
Codex transcript `~\.codex\sessions\2026\08\06\rollout-2026-08-06T23-57-41-*.jsonl` (L567/L1167).

**Shell session** (`Shell\ShellSession`): launches startup apps (optional `StartupDelayMs` wait
before the first one — the "First app delay" setting — then staggered, optionally elevated), then
Steam Big Picture. WSGM self-elevates when a startup app or Steam requires matching integrity, and
watches `config.json` (FileSystemWatcher, 500 ms debounce → `OverlayController.ApplyConfig`; runtime
state must live on controllers, not in `_config`, because reloads replace it wholesale).
`Shell\SteamMonitor` polls `steam;steamwebhelper` every 5 s; its `Paused` flag is how desktop mode
and "Close Steam" suppress auto-relaunch/overlay-pop reactions.

**Steam integration** (`Core\Steam.cs`, `Core\SteamInputBlocker.cs`, `Shell\SessionModes.cs`,
`Overlay\OverlayController.cs`): everything is protocol URLs — start/focus =
`steam://open/bigpicture` (boots Steam if needed, UIPI-proof), leave BP =
`steam://close/bigpicture`, quit = `steam://exit`. Desktop mode = pause monitor + close BP + start
explorer (de-elevated if WSGM is elevated — `Core\UnelevatedLauncher.cs` via scheduled task). A
runtime desktop-to-game switch requests Big Picture FIRST while keeping the monitor paused, then
runs `ExplorerControl.ExitExplorerAndWait` off the UI thread. That overlaps Steam's UI startup with
Explorer's bounded linger/retry instead of presenting the safety wait before Big Picture appears.
Only after Explorer is verifiably gone does the UI thread apply game posture, recreate game-mode
services/the tray host, and resume monitoring. If Explorer refuses to exit, the transition sends
`steam://close/bigpicture` and preserves desktop mode. The direct logon boot remains stricter: Steam
starts only after Explorer is gone. `SessionModes.TransitionInProgress` serializes transitions (the
overlay ignores mode clicks while one runs). The game/desktop mode transitions and the shared Steam
start+warning flow live in `Shell\SessionModes` (session coordinator, used by both `ShellSession`
boot and the overlay's buttons); `OverlayController` stays the UI owner (lease lifecycle, overlay
window) and surfaces `SessionModes.SteamStartFailed` warnings.

**The strongest current evidence for the recurring Steam startup hang is boot-context CEF mutation,
not the resident input shim** (device-observed repeatedly 2026-08-22). WSGM's direct-boot Steam
start could wedge while a manual start with the same deployed shim succeeded. Failed boot PID
12064's native trace shows the proxy forwarding table ready and rediscovery complete in 2 ms, the
control pipe listening, and zero bootstrap fallback calls — the same shape as successful starts.
WSGM then drove the still-headless CEF session and began replacing the card library before
`WindowFinder` ever observed Big Picture; that window never appeared. Automatic boot CEF mutations
now require the process-owned Big Picture window, while card detection starts immediately and defers
only the live Steam change. Device re-verification of that boundary is still required. Keep the
per-process trace (`%LOCALAPPDATA%\WSGM\steam-input-gate-<pid>.log`) as the control for future
reports; do not resume proxy-timing changes unless a failing trace differs.

7. **Big Picture's UI (steamwebhelper/CEF) suspends rendering while fully occluded** — a BP intro
   video that initializes under an opaque fullscreen cover stays black even after the cover leaves
   (same behavior BP shows under a game). The boot splash therefore begins its fade **immediately**
   on BP-window detection (the first fade tick drops the layered alpha below 255, which lifts the
   occlusion) with a tight 250 ms detection poll; never hold an opaque cover over a live BP window.
   Additionally, a `steam://open/bigpicture` re-activation while the intro plays kills the video
   (the removed splash→BP "focus handoff") — after the splash closes, do not touch Steam; it takes
   the foreground itself. A no-activate splash was tried and did not affect the symptom. **The
   detection path is boot-critical and must never throw** (regressed 2026-08-12, caught on the
   device across two reboots): the splash's 250 ms poll calls `WindowFinder.FindWindow` →
   `FindProcessIds`, which reads `Process.SessionId` per candidate. That read sits behind a
   deliberately BLANKET `catch` — an audit "fix" narrowed it to
   `InvalidOperationException`/`Win32Exception`, so any other type propagated out of the poll, BP
   was never detected, the splash never faded, and its opaque cover sat over a live BP window: black
   intro video, every boot. Do not narrow it, and do not add an unthrottled `Log` call inside it
   either — at 4 Hz across Steam's several helper processes that alone fills the capped log. The
   general rule: on any poll that feeds splash dismissal or takeover progress, a swallowed exception
   is the lesser failure. Prefer a throttled one-shot warning over a narrower catch.

**Taskbar + tray host** (`Overlay\TaskbarWindow/TaskbarViewModel`, `Core\TrayProtocol.cs`,
`Shell\TrayHost.cs`, `Shell\SystemStatus.cs`): bottom-edge swipe in game mode opens a **full-width
three-zone bar** — left WSGM button (opens quick access through the existing handover), centre the
switchable windows (`WindowFinder.ListSwitchableWindows`) in a horizontally scrolling strip, right
the tray icons (also bounded/scrolling) plus Wi-Fi/Bluetooth buttons, battery and clock from
`SystemStatus`. Columns are `Auto,*,Auto` ON PURPOSE: the old `*,Auto,*` let a large tile count push
the home button and the whole status cluster off a 1280-wide screen. Tiles keep FIXED sizes at every
count. The buttons open the `RadioManager`-backed radio panel; they must never invoke `ms-settings:`
(the immersive shell cannot activate it without Explorer in the session). The right edge stays quick
access, and `OverlayController` owns BOTH surfaces (shared Steam Input lease released only when both
are closed, mutual exclusion with restore-target handover, same 150 ms deferred close and
ghost-click WndProc hook). Tile refreshes reconcile IN PLACE — a wholesale rebuild would destroy the
focused button under the gamepad cursor. `TrayHost` registers a window class literally named
`Shell_TrayWnd` (that's how `Shell_NotifyIcon` finds a tray; game mode has no explorer, so without
it closed-to-tray apps lose their icons) and parses the WM_COPYDATA wire format in the pure,
unit-tested `TrayProtocol` (32-bit handle fields on every architecture). Two hard rules: (a) **never
coexist with explorer's taskbar** — the host is destroyed on `SessionModes.DesktopModeStarting`
(before `StartExplorer`) and recreated on `GameModeEntered`; (b) the **UIPI gate**: WSGM is usually
elevated, and unelevated apps' `Shell_NotifyIcon` WM_COPYDATA is silently dropped by UIPI unless
`ChangeWindowMessageFilterEx(WM_COPYDATA, MSGFLT_ALLOW)` is applied to the tray window — no shipped
replacement shell runs elevated, so this gate is WSGM-specific and its device verification status
must be tracked via the `Tray host created (… WM_COPYDATA filter …)` / `Tray icon Added/Rejected`
log lines.

A third rule guards the OUTBOUND side: (c) **a registered callback message is relayed only when it
lies in `WM_USER..0xFFFF`** — `TrayProtocol.IsRelayableCallback`, enforced in `TrayHost.SendClick`
immediately after the existing "registered no callback message" drop and before the `IsWindow`
check. The tray wire is attacker-reachable by design: the UIPI allowance in (b) exists precisely so
a Medium-IL process can push WM_COPYDATA into WSGM's High-IL `Shell_TrayWnd`, the sender's callback
HWND is taken verbatim off that wire, and the relay itself (`SendNotifyMessageW`) travels outbound
from High IL, where UIPI restricts nothing. Without the bound a Medium-IL process could register an
icon naming an ELEVATED window with `uCallbackMessage` = `WM_CLOSE` or `WM_SYSCOMMAND` and have the
tray deliver it on the next click. Three parts of the shape are deliberate:

- **The filter is on the message, never on the target's integrity level.** WSGM itself launches
  Handheld Companion / RTSS / MSI Afterburner elevated (`Core\KnownStartupApps.cs`), and `TrayHost`
  documents WinForms-hosted tray menus as device-verified consumers — an IL-based filter would kill
  tray clicks for exactly the apps the handheld depends on.
- **Registration is untouched.** `TrayProtocol.TryParse`, `TrayIconTable.Apply` and the WM_COPYDATA
  return value are byte-identical; a rejected NIM_ADD reads to shell32 as failure and well-behaved
  apps then re-add in a loop. An icon with a non-relayable callback still registers, still renders,
  and still shows its tooltip — only its click is dropped, and the drop is logged ONCE per tray host
  (a per-click `Log.Warn` would push the boot/takeover/lease lines out of the capped log, which is
  the same rule that keeps tray logging to Added/Removed).
- **The lower bound is `WM_USER` (0x0400), not `WM_APP`.** WinForms `NotifyIcon` registers WM_USER +
  1024 and Qt's `QSystemTrayIcon` uses WM_APP + 101; a tighter bound would silently break real
  applications.

Two honest caveats belong with this, because the guard is a mitigation and not a fix. First, **no
supported Win32 API identifies the sender of a WM_COPYDATA**, so WSGM cannot authenticate the
registering process at all — bounding the message value is the whole of the defence. The related
reading that WM_COPYDATA's `wParam` (which WSGM's WndProc ignores; it forwards only `lParam`) would
carry the same untrusted handle anyway comes from ReactOS/Wine sources, NOT from anything verified
against live shell32 — treat it as informed inference, not as established behaviour. Second, the
residual is NOT closed: an attacker who registers an icon can still make WSGM deliver ANY message in
`WM_USER..0xFFFF` to any window it names, including the `RegisterWindowMessage` range 0xC000..0xFFFF
that real applications use for private IPC, and on a version-0/3 icon the accompanying `wParam` is
the wire `uid` — a fully attacker-chosen 32-bit value. The values stay bounded (`Notify` composes
them), so there is no pointer primitive, but a targeted private-IPC message remains reachable.
