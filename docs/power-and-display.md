# Display profiles, power and wake locks

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

**Display profiles** (`Core\DisplayScale.cs`, `Core\DisplayProfiles.cs`): display management has
four mutually exclusive modes: Off, legacy DPI-only, automatic profiles, and fixed profiles.
Profiles are keyed by stable monitor device identity (with the current GDI source name retained for
Win32 application) and contain resolution, refresh rate, DPI, and — only when the active target
reports advanced-color support — an HDR flag for both Desktop and Game mode. Automatic mode captures
only at a Desktop/Game transition (never continuously, or an exclusive-fullscreen game's temporary
mode would become the saved preference), then restores the last values for the mode being entered.
Fixed mode applies the values edited in Settings. DPI-only retains the crash-safe saved-scale
recovery path. A surviving DPI-only snapshot never authorizes lowering a newly docked display absent
from that snapshot. Panic/uninstall recovery applies the last known Desktop profile without
capturing the possibly half-torn-down current mode, and restores any pending legacy DPI snapshot
even when display management has since been switched Off. Automatic snapshots are runtime-owned;
Settings preserves a newer capture made while its window was open. HDR uses DisplayConfig
advanced-color get/set packets against the path TARGET; never show or apply the flag merely because
it was persisted when the currently active target reports no HDR support.

**Mute during screen-off downloads** (`Shell\DisplayOffMuteService.cs`, `Shell\KeepAwakeService.cs`,
`Interop\MessageWindow.cs`, config `MuteWhileDisplayOff`, default OFF, Settings → System → POWER —
display notification device-verified on the MSI Claw 2026-08-13; download-aware policy implemented
2026-08-22, device re-verification required): the companion to keep-awake, which deliberately lets
the display time out while downloads continue — and Steam plays a sound on every finished download,
into a dark room. The condition is the exact conjunction **setting enabled + this session's display
off + Steam actively downloading**. Screen-off alone never mutes. An active download arriving while
the display is already dark mutes then; display wake restores immediately; the first usable inactive
Steam snapshot starts a 10 s restore grace, and a new active snapshot cancels it. A transient CEF
failure preserves the last usable activity answer rather than inventing a completion, while a
confirmed dead Steam process is inactive. The display signal is
`RegisterPowerSettingNotification(hwnd, GUID_SESSION_DISPLAY_STATUS, DEVICE_NOTIFY_WINDOW_HANDLE)`
on the existing process message-only window → `WM_POWERBROADCAST` / `PBT_POWERSETTINGCHANGE`,
payload a DWORD `MONITOR_DISPLAY_STATE` (0 off, 1 on, 2 dimmed). Microsoft documents

**`GUID_SESSION_DISPLAY_STATUS` as the one interactive user-mode apps must use** —
`GUID_CONSOLE_DISPLAY_STATE` is for services/kernel-mode and `GUID_MONITOR_POWER_ON` is the
superseded legacy setting; do not "simplify" to either. Dimmed is NOT treated as off (the screen is
still lit in front of the user). The open question was whether the notification fires at all when
the Claw's screen times out under Modern Standby; it does (device-verified 2026-08-13). The
`Display state: off/on` and `Mute on display off: …` log lines are the whole remote test surface, so
preserve them. Only a mute WSGM applied itself is undone (a user who muted on purpose stays muted),
and the service restores on `ProcessExit` so a normal exit while the screen is dark cannot strand
the device muted; a hard kill still can, which is why the toggle defaults off. Muting goes through
the native helper's APPCOMMAND

**toggle** (`WsgmVolumeCommand(8)`) after reading the current state — there is no absolute set-mute
export, so never call it without checking `WsgmVolumeGet` first.

**The wake side listens on every signal Windows has, because there is no way to ASK.** No user-mode
API reports current display power state (`GetDevicePowerState` explicitly excludes displays), so a
notification is the only mechanism, and WSGM registers all three display power settings plus session
unlock on the same message window: `GUID_SESSION_DISPLAY_STATUS` (primary),
`GUID_CONSOLE_DISPLAY_STATE`, the superseded `GUID_MONITOR_POWER_ON`, and `WM_WTSSESSION_CHANGE` /
`WTS_SESSION_UNLOCK`. **The asymmetry is the safety rule** (`DisplayMuteDecider.MayReportDark`):
only the session setting may report the screen going DARK — console state describes whichever
session owns the console, so acting on its "off" would mute the wrong session after a fast user
switch — while **every** source may report it coming back. The
`Display state: … (via Session | Console | LegacyMonitor)` tag is what makes a missed wake
diagnosable from a pasted log; the extra registrations are not a substitute for the documented one
and must not replace it. Note the blind spot in the `GetLastInputInfo` net below: it does not see
gamepads or the power button, so a user who wakes with the power button and then navigates by
controller (HandheldCompanion blocks controller wake by design) depends entirely on the
notifications.

**Coming back must not hang on any one notification** (reported 2026-08-19: a mute applied during a
screen-off download never came back; `Core\DisplayMuteDecider.cs` now owns the pure display mapping
and download/display reconciliation). Three rules make the restore path robust and none of them may
be simplified away: the "we muted this" claim is cleared only after a **confirmed** unmute — the
default endpoint is re-enumerated when the display wakes, and the old code cleared the flag _before_
attempting the read/toggle, so one transient `GetDefaultAudioEndpoint` failure stranded the mute
permanently with nothing left to retry; a failed attempt is retried on a 2 s timer that runs
**only** while the claim is outstanding; and while muted that timer also watches `GetLastInputInfo`
against a baseline taken at mute time (wrap-safe signed tick compare), because keyboard/mouse/touch
input means a lit screen, so the mute is undone even if the display-on notification never arrives.
Restore direction is fail-safe and deliberately asymmetric with mute: only state 0 establishes the
dark half of the mute condition; **every other value restores** — dimmed and any value Windows may
add later — since an unrecognised state must never be the reason a device stays silent. The added
`Mute on display off: user input while muted, …` line joins the remote test surface.

**Keep-awake wake lock** (`Core\WakeLock.cs`, `Core\SteamDownloads.cs`, `Core\KeepAwakeDecider.cs`,
`Shell\KeepAwakeService.cs` — device-verified on the MSI Claw 2026-08-12, including the download
hold across screen-off, the manual cycle, the indicator dot, and the idle-timeout rows): a Windows
power request (`PowerCreateRequest` + `PowerRequestSystemRequired`) that blocks standby entry while
held — the display still times out dark, but Wi-Fi and Steam keep running, which is what makes
downloads survive "screen off" on a Modern-Standby handheld. Research-settled (2026-08-12):
downloads during REAL Modern Standby sleep are impossible for a Win32 app (DAM suspends every
desktop process, no opt-out), so keep-awake is the whole feature — the same model Valve ships as
SteamOS "Display-Off Downloads". Windows-documented limits: indefinite on AC; on battery the OS
force-terminates the request ~5 min after the sleep timeout expires, and the power button always
wins. Two independent holds, each its own request so `powercfg /requests` attributes them: a
**manual toggle** (quick-access Power tab, session-lifetime, survives mode switches) and an
**automatic download hold** — `KeepAwakeService` polls
`SteamClient.Downloads.RegisterForDownloadOverview` over the CEF bridge every 30 s (one-shot
subscribe/unsubscribe; fires immediately with a snapshot, live-verified; active =
`update_state != "None" && !paused`, and the Windows client's active state string is `Downloading`,
NOT decky's Linux-documented `Updating`). Release is debounced (`KeepAwakeDecider`, 2 consecutive
inactive polls) so queue gaps don't flap the hold; unreachable polls count as inactive for that
wake-lock debounce so a dead Steam cannot pin the device awake. The separate activity answer
consumed by display muting is stricter: an unreachable live client preserves the prior answer, and
only a usable idle snapshot or dead process ends activity. `CefConfig.DownloadKeepAwake` (default
on, Settings row on the Integration tab, gated by the CEF master switch AND off in `--overlay-test`,
whose safe-mode contract excludes autonomous Steam traffic) gates only the automatic hold. The
shared poll remains active while either that hold or download-aware muting consumes it. Hold
transitions and the config apply share one gate — a disable landing mid-poll must not lose to the
stale sample, or the hold sticks for the session. The manual side is a **three-state cycle** (Off →
Standby lock → Standby+Display lock → Off), holding a separate DisplayRequired request for the third
state — acquired-before-released so a step never has a lock gap. Preserve the
`Keep awake: … hold acquired/released / manual mode …` log lines — they are the remote test surface.
The row also carries a **WakeWatch-style indicator dot** (the maintainer's WakeWatch tray tool,
deliberately the same color vocabulary): green free / yellow standby-blocked / red display-pinned /
grey unknown, computed from the system-wide power-request list — A **"What's keeping this awake"**
row below it opens the Power tab's own in-place sub-view (`Overlay\WakeLockHoldersView.cs`, grouped
by `Core\WakeLockHolders.cs`) listing every requester — WakeWatch's right-click detail,
reimplemented: dedupe on (label, detail, reason) so thirty identical Steam requests read as
`steam.exe ×30`, sorted by count then name, with the caller kind, pid, path and reason string on the
second line. It is the first sub-view that belongs to the **Power** tab rather than Tools, so
`LeaveWakeLockSubView` restores `PanelPower`, and it appears in `AnySubView`, `DefaultFocusTarget`,
`TryCancelSubView`, the tab-switch teardown and the `Activated` reset like every other one. Unlike
the summary line it deliberately does NOT hide WSGM's own request: the row above already explains
WSGM's holds, but the full list is answering "what is holding this awake" and must not omit an
answer. An unelevated read yields "couldn't read", never an empty all-clear.
`Interop\PowerRequestList.cs` calls the undocumented `NtPowerInformation(GetPowerRequestList=45)`
against ntdll directly (the documented wrapper rejects the class; needs elevation, denied → grey),
decodes the version-dependent layout through bounds-checked readers ported from WakeWatch's
`power.rs` (MIT, same author) — any structural surprise must yield grey "unknown", NEVER a false
all-clear — and `Core\WakeLockStatus.cs` maps entries to state + a collapsed holder summary (WSGM's
own pid colors the state but is excluded from the summary). Polled at 1.5 s only while the panel is
open. The Power tab also hosts four **idle-timeout rows** (screen-off / standby × battery /
plugged-in) that cycle presets via `Core\PowerTimeouts.cs` — the flat powrprof value-index API, NOT
`powercfg /q` parsing (localized output, same trap as netstat); these are a user-facing convenience
over the active scheme, deliberately not snapshotted/restored state.
