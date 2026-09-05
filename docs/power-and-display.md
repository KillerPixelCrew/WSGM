# Display profiles, power and wake locks

What WSGM does with the display and the power state of a handheld: display profiles and HDR, muting
during screen-off downloads, the keep-awake wake lock, refresh-rate pairing for the frame limit, and
variable refresh over IGCL. The established display and wake-lock paths were verified on the
reference MSI Claw. Boot and shell transitions are in `docs\boot-and-shell.md`; the frame limit
itself in `docs\rtss.md`.

## Windows power schemes

Disabling Device Integration releases plugin-only pages. Shared Power stays open, keeping the
Windows power-profile picker reachable.

The Core backend in `PowerSchemes` enumerates installed schemes and reads the active GUID through
`powrprof`. GUIDs identify schemes; localized friendly names are display text only. An empty name
falls back to the GUID. Enumeration failures are surfaced rather than returning a partial list. The
existing idle-timeout controls share its active-scheme reader.

A manual selection calls `PowerSetActiveScheme` once, then verifies the GUID with
`PowerGetActiveScheme`. A failed write, failed readback or different active GUID is not success and
does not trigger another write or rollback. Windows remains authoritative, including subsequent
changes made by Settings or OEM tools. Native failures retain their error codes.

Overlay → Device → Power offers a Windows power-profile dropdown, Apply and Refresh inside the
Windows energy plan card. It stays available with Device Integration off. Choosing an entry stages
it; only Apply writes Windows. The current scheme is read when the sheet opens, when Device is
selected, after Apply and on Refresh. Duplicate names include their GUIDs. An unknown active scheme
leaves the picker unselected; an empty or failed read disables Apply. An unconfirmed write requires
Refresh before another attempt. Preview mode allows reads only. Native calls and persistence run off
the UI thread, and closing the overlay discards late UI updates. Idle-timeout badges refresh after
the active scheme is read.

The last verified manual selection is saved as `LastSelectedPowerSchemeId`, a GUID in Core config.
It is a reference, not an instruction to reapply at startup, config reload or a session transition.
A failed save reports that Windows applied the scheme but WSGM could not save the reference; it does
not undo or repeat the write. Timeout edits and scheme selection share one mutation gate so an
idle-timeout edit cannot reactivate a stale scheme during selection.

Synthetic tests cover the backend, selection workflow, persistence and Core-only Device navigation.
No live power settings were changed for validation. No session-mode or per-application scheme policy
is installed, and the selector has no device-plugin dependency. WSGM Settings configures WSGM
itself; this Windows control belongs in the overlay and Steam QAM.

Steam QAM → Performance offers a Windows power profile dropdown, built on Valve's dropdown field.
Selecting an entry applies it immediately through the same Core backend and saves the verified GUID.
Each publication reads Windows; the backend rejects unknown or removed GUIDs and requires a fresh
read after an uncertain write. The row shows failures and disables input while its request is
pending. The toolkit owns row placement and command validation; WSGM owns Windows access.

## Device power presets

Steam QAM → Performance offers a Device power profile dropdown when the plugin declares presets. The
Claw A2VM supplies:

| Preset              | PL1 / PL2 | Windows power mode | EC scenario on AC | EC scenario on battery |
| ------------------- | --------- | ------------------ | ----------------- | ---------------------- |
| Super Battery       | 8 / 9 W   | Better Battery     | Eco               | Comfort                |
| Balanced            | 17 / 18 W | Balanced           | Green             | Comfort                |
| Extreme Performance | 30 / 31 W | Best Performance   | Sport             | Comfort                |
| Full Power          | 37 / 37 W | Best Performance   | Sport             | Comfort                |

Full Power uses the Claw plugin's supported maximum of 37 W for both limits. The other three presets
are the A2VM values from the local `_ref/HandheldCompanion` source. `ClawA2VM` overrides the watt
pairs and inherits `ClawA1M.PowerProfileManager_Applied` for the scenario selection. HC's battery
`ShiftType.None` becomes active Comfort (`0xC0`). WSGM uses the same mapping through optional
plugin-authored AC/battery scenario targets; the host contains no MSI register knowledge. A Windows
mode is the performance/efficiency overlay on a power plan, separate from the scheme selector above.
CPU boost, Intel Endurance Gaming and fan controls remain independent. The exact firmware effects of
each EC scenario still require attended AC/battery measurements.

Selecting a preset applies immediately. WSGM serializes it with manual power, scenario and AutoTDP
writes. It selects the firmware scenario first, reads the resulting watt pair, raises PL2 before PL1
when necessary, and lowers PL1 before PL2. Each device write must report verified success before the
next step; Windows mode is applied last and read back. Device and descriptor generations and power
source are checked between steps; an unknown power source blocks scenario presets, and a source
change stops remaining writes without retry. The manual TDP funnel pauses AutoTDP and records the
underlying values using their existing owners. Manual selection does not create an automatic
assignment. Preset scenario commands are not persisted as desired values. The plugin journals the
exact original scenario and watt pair and restores the scenario first, then the pair, when releasing
its temporary state.

Both UIs derive the current preset from observed PL1, PL2, firmware scenario for the current power
source, and effective Windows mode. A mismatch, including an external Windows mode change or a
resumed AutoTDP adjustment, shows Custom. Custom is a reading, not an action. The open overlay
refreshes once per second; QAM refreshes with its regular state publication. Missing or stale
observations disable selection instead of guessing a preset. Disabling Device Integration removes
the preset choices and leaves the Windows scheme picker.

A failure can leave some underlying values changed. WSGM reports that partial result, stops, and
does not retry or roll back across Windows and device controls. The plugin's existing per-command
power rollback remains intact. Preview surfaces cannot apply presets, and closing the overlay
cancels remaining work and prevents late UI updates. Validation uses fake device/Windows backends
and emitted dropdown fixtures; it does not change live power settings or a running Steam client.

## AC and battery assignments

Device → Power provides **When plugged in** and **On battery** profile assignments and a read-only
active-profile status. There is no separate active-profile selector. Background reads do not block
assignment selection or overwrite an open dropdown. Global assignments are the defaults; enabling
the existing per-game profile switch exposes overrides for the running game. An unset per-game value
inherits its global assignment. References include the plugin ID so changing device packages cannot
silently apply another package's similarly named preset.

The session applies an assignment once on source, application, assignment or device-cycle changes.
Unknown power sources and unavailable device observations defer application. A failed or uncertain
write is recorded before dispatch and never retried by polling; explicitly saving an assignment
permits another attempt. Manual changes remain in place until the next transition. Automatic
application pauses AutoTDP without overwriting the saved manual watt limit. Windows power-plan
selection remains independent of these device preset assignments.

## Display profiles

Display management (`Core\DisplayScale.cs`, `Core\DisplayProfiles.cs`) has four mutually exclusive
modes: Off, legacy DPI-only, automatic profiles and fixed profiles. A profile is keyed by the stable
monitor device identity (the current GDI source name is retained for the Win32 calls) and holds
resolution, refresh rate, DPI and an HDR flag for Desktop and for Game mode. The HDR flag exists
only when the active target reports advanced-color support.

- Automatic mode captures only at a Desktop/Game transition and restores the last values for the
  mode being entered. Capturing continuously would make an exclusive-fullscreen game's temporary
  mode the saved preference.
- Fixed mode applies the values edited in Settings.
- DPI-only keeps the crash-safe saved-scale recovery path. A surviving DPI-only snapshot never
  authorizes lowering a newly docked display that is absent from it.
- Panic and uninstall recovery apply the last known Desktop profile without capturing the possibly
  half-torn-down current mode, and restore a pending legacy DPI snapshot even when display
  management has since been switched Off.
- Automatic snapshots are runtime-owned; Settings preserves a newer capture made while its window
  was open.
- HDR uses DisplayConfig advanced-color get/set against the path target. A persisted flag is neither
  shown nor applied when the active target reports no HDR support.

## Mute during screen-off downloads

Keep-awake (below) lets the display time out while downloads continue, and Steam then plays a sound
for every finished download into a dark room. `Shell\DisplayOffMuteService.cs` mutes for exactly
that case. Config `MuteWhileDisplayOff`, default off, Settings → System → Power.

The condition is the conjunction of three facts: the setting is enabled, this session's display is
off, and Steam is actively downloading. Screen-off alone never mutes. A download that starts while
the display is dark mutes then. Display wake restores immediately. The first usable idle Steam
snapshot starts a 10 s restore grace; a new active snapshot cancels it. A transient CEF failure
keeps the last usable activity answer rather than inventing a completion; a confirmed dead Steam
process counts as inactive.

Only a mute WSGM applied itself is undone, so a user who muted on purpose stays muted. The Core
Audio edge reads the endpoint's mute before claiming, then writes an absolute
`IAudioEndpointVolume.SetMute` value, which avoids a read/toggle race during recovery. The service
restores on `ProcessExit`; a hard kill can still strand the device muted, which is why the toggle
defaults to off.

### The display signal is GUID_SESSION_DISPLAY_STATUS

`RegisterPowerSettingNotification(hwnd, GUID_SESSION_DISPLAY_STATUS, DEVICE_NOTIFY_WINDOW_HANDLE)`
on the process message-only window (`Interop\MessageWindow.cs`) delivers `WM_POWERBROADCAST` /
`PBT_POWERSETTINGCHANGE` with a DWORD `MONITOR_DISPLAY_STATE`: 0 off, 1 on, 2 dimmed. Microsoft
documents the session setting as the one for interactive user-mode applications;
`GUID_CONSOLE_DISPLAY_STATE` is for services and kernel mode and `GUID_MONITOR_POWER_ON` is the
superseded legacy setting. Dimmed is not off: the screen is still lit in front of the user. The
notification does fire when the Claw's screen times out under Modern Standby (Claw, 2026-08-13).

### Every wake source is registered; only the session source may report dark

No user-mode API reports display power (`GetDevicePowerState` excludes displays), so notifications
are the only mechanism, and WSGM registers all three display settings plus `WM_WTSSESSION_CHANGE` /
`WTS_SESSION_UNLOCK` on the same window. `DisplayMuteDecider.MayReportDark` enforces the asymmetry:
only the session setting may say the screen went dark, because console state describes whichever
session owns the console and would mute the wrong session after a fast user switch. Every source may
report the screen coming back. The extra registrations do not replace the documented one.

The `GetLastInputInfo` net below does not see gamepads or the power button. A user who wakes with
the power button and navigates by controller (HandheldCompanion blocks controller wake by design)
depends entirely on the notifications.

### The mute claim clears only after a confirmed unmute

A mute applied during a screen-off download once never came back. The code cleared the "we muted
this" claim before attempting the unmute, so one transient `GetDefaultAudioEndpoint` failure while
re-enumerating the endpoint on wake stranded the mute with nothing left to retry. The claim now
clears only after a confirmed unmute, and a failed attempt is retried on a 2 s timer that runs only
while the claim is outstanding.

### Input while muted restores

While muted, the same timer compares `GetLastInputInfo` against a baseline taken at mute time
(wrap-safe signed tick compare). Keyboard, mouse or touch input means a lit screen, so the mute is
undone even if the display-on notification never arrives.

### Any display state other than off restores

Only state 0 establishes the dark half of the mute condition. Every other value restores — dimmed,
and any value Windows adds later — because an unrecognised state must not keep a device silent.

### Log lines

These are the remote test surface; keep their shape.

| Line                                                                                                                              | Meaning                                                                                  |
| --------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| `Display state: off\|on\|dimmed\|unknown (n) (via Session\|Console\|LegacyMonitor).`                                              | Every notification with its source. A missed wake is diagnosed from which sources spoke. |
| `Mute on display off: Steam downloads active\|inactive.`                                                                          | Activity answer changed.                                                                 |
| `Mute on display off: screen dark without an active Steam download, leaving audio unchanged.`                                     | The no-op branch.                                                                        |
| `Mute on display off: muted.` / `already muted, leaving it alone.` / `unmuted.`                                                   | Claim taken, declined, released.                                                         |
| `Mute on display off: downloads inactive, waiting 10 s before unmute.` / `downloads remained inactive for 10 s, restoring audio.` | Restore grace started, elapsed.                                                          |
| `Mute on display off: user input while muted, restoring …`                                                                        | The input net fired.                                                                     |

## Keep-awake wake lock

`Core\WakeLock.cs` holds a Windows power request (`PowerCreateRequest` +
`PowerRequestSystemRequired`) that blocks standby while held. The display still times out, but Wi-Fi
and Steam keep running, which is what lets downloads survive "screen off" on a Modern Standby
handheld. Downloads during real Modern Standby sleep are impossible for a Win32 application (DAM
suspends every desktop process, no opt-out), so keep-awake is the whole feature — the same model as
SteamOS "Display-Off Downloads". Windows limits it: indefinite on AC; on battery the request is
force-terminated about 5 min after the sleep timeout expires; the power button always wins. Verified
on the Claw, 2026-08-12, including the download hold across screen-off, the manual cycle, the
indicator and the idle-timeout rows.

There are two independent holds, each its own request so `powercfg /requests` attributes them.

The manual hold is a quick-access Power tab toggle with session lifetime that survives mode
switches. It cycles Off → Standby lock → Standby+Display lock → Off; the third state holds a
separate `DisplayRequired` request. Each step acquires before it releases, so there is never a lock
gap.

The automatic download hold (`Shell\KeepAwakeService.cs`, `Core\SteamDownloads.cs`) polls
`SteamClient.Downloads.RegisterForDownloadOverview` over the CEF bridge every 30 s as a one-shot
subscribe/unsubscribe; it fires immediately with a snapshot (live Steam client). Active means
`update_state != "None" && !paused`; the Windows client's active string is `Downloading`, not the
`Updating` decky documents for Linux. Release is debounced by two consecutive inactive polls
(`KeepAwakeService.NextDownloadHold`) so queue gaps do not flap the hold, and an unreachable poll
counts as inactive so a dead Steam cannot pin the device awake. The activity answer consumed by
muting is stricter: an unreachable client preserves the prior answer, and only a usable idle
snapshot or a dead process ends activity.

`CefConfig.DownloadKeepAwake` (default on, Settings Integration tab) gates only the automatic hold.
It is also off when the CEF master switch is off and in `--overlay-test`, whose safe-mode contract
excludes autonomous Steam traffic. The poll stays active while either the hold or muting consumes
it. Hold transitions and the config apply share one gate; a disable landing mid-poll must not lose
to the stale sample, or the hold sticks for the session.

### Indicator and holders view

The Power tab row shows an indicator computed from the system-wide power-request list: green free,
yellow standby-blocked, red display-pinned, grey unknown — WakeWatch's colour vocabulary on purpose.
`Core\WakeLockStatus.cs` maps the list to a state plus a collapsed holder summary; WSGM's own pid
colours the state but is excluded from the summary.

A "What's keeping this awake" row opens `Overlay\WakeLockHoldersView.cs`, a Power-tab sub-view
listing every requester, deduplicated on (label, detail, reason) so thirty identical Steam requests
read as `steam.exe ×30`, sorted by count then name, with caller kind, pid, path and reason on the
second line. Unlike the summary it does not hide WSGM's own request: the list must not omit an
answer. An unelevated read shows "couldn't read", never an empty all-clear. It is the first sub-view
belonging to the Power tab rather than Tools, so leaving it restores `PanelPower`.

`Interop\PowerRequestList.cs` calls the undocumented `NtPowerInformation(GetPowerRequestList = 45)`
on ntdll directly, because the documented wrapper rejects the class; it needs elevation, and denied
yields grey. The version-dependent layout is decoded by bounds-checked readers ported from
WakeWatch's `power.rs` (MIT, same author). Any structural surprise yields grey, never a false
all-clear. The list is polled at 1.5 s only while the panel is open.

### Idle-timeout rows

Four rows (screen-off and standby, each for battery and plugged-in) cycle presets of 1, 3, 5, 10,
15, 30, 60 min and never through `Core\PowerTimeouts.cs`, using the powrprof value-index API.
Parsing `powercfg /q` was rejected: its output is localized, the same trap as netstat. The rows are
a convenience over the active scheme, deliberately not snapshotted or restored.

### Log lines

| Line                                                                          | Meaning                                               |
| ----------------------------------------------------------------------------- | ----------------------------------------------------- |
| `Keep awake: download hold acquired (…)` / `released (…)`                     | The automatic hold changed, with the snapshot detail. |
| `Keep awake: download hold released (disabled in settings).`                  | Config apply dropped an engaged hold.                 |
| `Keep awake: manual mode Off\|Standby\|StandbyAndDisplay (quick access).`     | A step of the manual cycle.                           |
| `Steam downloads: active\|inactive (…)`                                       | The activity answer consumed by muting changed.       |
| `Keep awake: PowerCreateRequest\|PowerSetRequest\|PowerClearRequest failed …` | The Windows request itself failed.                    |

## Refresh rates: what a panel advertises is not what a driver accepts

Verified on the reference MSI Claw 8 AI+ A2VM, 2026-08-30. The two lists differ, and every
frame-limit strategy depends on the difference.

`EnumDisplaySettings` reports 30/48/60/75/100/120 Hz at 1920x1200, and
`ChangeDisplaySettingsEx(CDS_TEST)` accepts all six. The panel's EDID advertises only 60 and 120:
two detailed timings, 315.50 MHz and 157.75 MHz over a 2080x1264 total. The other four exist because
the panel declares a 30-120 Hz adaptive-sync range in its display-range-limits descriptor and the
driver synthesizes timings inside it. Arc Sync independently reports the same 30-120 band.

The synthesized modes are real: applying 48 Hz moved DWM's `rateRefresh` from 119.999 to 47.997 and
back. Windows Settings kept showing 120 throughout, because the change was applied without
`CDS_UPDATEREGISTRY` and Settings reads the persisted configuration. That is exactly the property
that makes a game-scoped refresh change safe: exit, a crash or a reboot all restore the user's own
configuration with WSGM doing nothing.

Consequences encoded in `Core\FrameLimitPairing.cs`, `Core\EdidModes.cs` and
`Core\RefreshRatePairingService.cs`:

- Enumeration alone cannot tell an advertised mode from a synthesized one, so the native-modes
  strategy needs the EDID. Without it that strategy would silently equal full frame doubling.
- Rates are enumerated and then tested; a driver may refuse one it enumerated. `CDS_TEST` changes
  nothing and is safe while a game runs.
- Discovery is cached because each candidate costs a driver round trip.
- The frame-doubling strategy prefers the lowest mode at least twice the cap (30 FPS at 60 Hz, 60 at
  120). A 1:1 cadence keeps adaptive sync's low-framerate compensation out of reach, and a 30 Hz
  panel visibly flickers. Where no doubled multiple exists, and under native modes always, pairing
  takes the lowest exact multiple, since refresh rate is a power cost.
- A cap with no exact multiple leaves the refresh rate alone. Forcing a near-miss mode adds judder
  rather than removing it.
- A mode change is not free: an exclusive-fullscreen title can hitch, minimize or drop out across
  one. Cap-only is therefore the default wherever variable refresh already covers the range.

## Variable refresh over IGCL

Verified on the same unit and date, unelevated. `ControlLib.dll` ships with the Intel driver and is
already in `System32`; IGCL initialises at v1.1. The internal panel reports
`IsIntelArcSyncSupported` across 30-120 Hz with the profile at `EXCELLENT`. Writing `OFF` and
restoring the saved parameter struct both succeed, and the read-back confirms each.

The panel belongs to the device, so the transport belongs to the plugin
(`external\WSGM.Device.Msi.Claw8A2Vm\src\WSGM.Device.Msi.Claw8A2Vm\ArcSyncTransport.cs`). WSGM only
projects the capability.

Four facts that cost real time to establish:

- Both enumerations are two-call: ask for the count with a null buffer, then fetch. Passing a buffer
  straight away returns nothing.
- The panel is chosen by which output answers, never by index. The reference unit enumerates twelve
  display outputs of which one is real; the other eleven return `CTL_RESULT_ERROR_KMD_CALL`. An
  external display when docked is a different output.
- IGCL's `bool` is one byte. A managed `bool` is four and would shift every float after it.
- Every call passes its own `sizeof` in a `Size` field and the driver refuses a mismatch. That
  refusal is indistinguishable from "this machine has no variable refresh", so a layout drift would
  remove the feature silently. The sizes are 36 / 24 / 28 and are pinned by a test.

Turning the profile `OFF` collapses the reported range to 120/120, a second confirmation independent
of the profile enum. That is why this capability reports a verified read-back rather than an
applied-unverified one.
