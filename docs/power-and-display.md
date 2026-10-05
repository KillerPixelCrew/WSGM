# Display profiles, power and wake locks

What WSGM does with the display and the power state of a handheld: Windows power schemes and
processor policy, device power presets and their assignments, Game Mode display layouts and audio
profiles, muting during screen-off downloads, the keep-awake wake lock, refresh-rate pairing for the
frame limit, and variable refresh over IGCL. The established display and wake-lock paths were
verified on the reference MSI Claw. Boot and shell transitions are in
[boot and shell](boot-and-shell.md); the frame limit itself in [RTSS](rtss.md).

Windows Device Control owns the first reusable CCD display-profile primitives. `DisplayTopology`
captures active paths in Windows priority order, identifies monitors primarily by device-interface
path with EDID manufacturer/product fallback, and skips a display whose name cannot be read rather
than failing the rest. Friendly names and GDI `DISPLAY1` numbering are presentation metadata;
adapter LUID and target ID are current route coordinates and are refreshed after hotplug.
Enumeration is read-only. The library has no wait of its own: WSGM's `Shell\DisplayArrivalWaiter.cs`
is the one display wait, settling on two equal `DisplayLayouts.Observe` fingerprints.
`DisplayLayouts` holds the editable form: a `DisplayLayout` names each display's placement, mode,
scaling and advanced colour state by value. `Validate` rematches each saved target to the current
topology and asks Windows to validate without changing anything. `Apply` snapshots rollback state,
writes once and takes Windows' acceptance as the result without reading it back; a refused
application gets one rollback to the snapshot, scaling and colour included. A failed rollback
reports its own status, never a claim that the prior desktop was restored. Every display write
(layout, mode, scaling, HDR) shares one gate in the library, and its results are codes and native
statuses; `Core\DisplayText.cs` words them for Settings, the overlay and the log. A layout output
with rotation 0 keeps the display's current rotation. The design uses DisplayMagician as behavioral
reference while the MIT library implementation comes from documented Windows CCD contracts rather
than copied GPL source.

## Windows power schemes

Windows Device Control owns power actions, source/battery queries, scheme/mode APIs, wake requests,
request-list decoding, wake sign-in policy and native notification registration. WSGM owns user
intent, policy serialization, logging, window message dispatch and persisted recovery snapshots.
Lifecycle requests do not block the UI; successful shutdown dispatch is not a claim that shutdown
completed. Wake-policy capture must succeed before any mutation. Restoration failures retain the
saved snapshot.

Disabling Device Integration releases plugin-only pages. Shared Power stays open, keeping the
Windows power-profile picker reachable.

The Core policy in `PowerSchemes` consumes Windows Device Control's `WindowsPower` API for installed
schemes and the active GUID. The library owns `powrprof`, native buffers, policy values and
power-mode overlays. GUIDs identify schemes; localized friendly names are display text only. An
empty name falls back to the GUID. Enumeration failures are surfaced rather than returning a partial
list. The existing idle-timeout controls share its active-scheme reader.

### No automatic display hold for running applications

WSGM does not hold a DISPLAY power request because an application is running. Applications keep the
display awake themselves, at least through audio output. An automatic hold tied to the
running-application snapshot became permanent on the desktop, where any foreground application is
Active so the frame limit can apply. The Power tab's manual Keep Awake is the only display hold WSGM
takes.

### Sleeping again after an unexplained wake

`Core\ModernStandbyPolicy` decides, and `Shell\ModernStandbyGuard` owns the subscriptions, the timer
and the attempt count. Off by default and switched on from Settings > System > Power: it decides on
its own to suspend the machine, so it is the user's to enable.

WSGM changes no power settings and arms no wake sources for it. The machine wakes normally and the
guard only decides whether to put it back, so nothing global is left altered and nothing needs
restoring if the process dies mid-session. That shape comes from the #27 investigation: Winhanced's
`SleepCoordinator` imports `IsSystemResumeAutomatic`, `SetSuspendState` and the power-notification
registrations and writes no scheme values at all, unlike Handheld Companion's Enhanced Sleep, which
writes eight settings into the active scheme and only restores them when its own toggle is turned
back off.

`ModernStandby.WasLastResumeUnattended` is the whole basis: Windows says whether a person woke the
machine. A wake it attributes to the user is never undone. Beyond that the guard refuses on a lit
display, on any input since the wake, inside a settle period, and after three unattended wakes in a
row; only a wake a person causes resets that count, so a wake source that keeps firing cannot hold
the machine in a loop. Only this session's display may report the screen dark, as for the download
mute, and the guard registers the display notifications itself rather than relying on the mute
feature having done so. Desktop wake actions likewise run only on a resume a person caused.

The display gate is the one that does not depend on Windows counting a device as input. A gamepad
does not advance the last-input time, the same fact behind the idle-timeout bug in #69, so a player
holding a controller reads as idle by that measure alone. Suspending a machine under someone's hands
is the one failure this feature must never produce, and a dark screen is the evidence that nobody is
looking. Anything the guard cannot read leaves the machine awake: staying on is recoverable by the
user, suspending on an unestablished state is not. The bounded attempt count exists for the same
reason: a machine waking for a cause WSGM cannot see must not be suspended in a loop the user cannot
escape.

#### What the settings page reports

`Core\ModernStandbyDiagnostics` reads Windows' own account for the row under the toggle. It reports
three things and refuses a fourth:

- whether the machine does S0 low-power idle at all, so a machine that cannot use the feature is
  told plainly instead of being offered it;
- how long the last standby lasted and how long the machine has been awake since;
- which devices are currently **allowed** to wake it, named as Windows names them.

It never names what woke the machine, because Windows exposes no documented call that says. The
attribution is reported exactly as far as it goes: whether the resume was put down to a person.
Anything more would be a guess printed as a diagnosis.

The armed-device list is the actionable half. Measured on the reference handheld on 2026-09-10: two
of three wake-capable devices were armed, the Intel Wi-Fi 7 BE201 and the USB4 root router, after a
22.7-hour standby that Windows attributed to a person. On a handheld that list is usually the answer
to "why did it come back on in my bag", and `ModernStandby.TrySetWakeArmed` could act on it. WSGM
deliberately does not, because disarming a wake source is a global change that outlives the process,
which is exactly what this feature's design avoids.

**Not measured:** no battery-drain comparison has been run. The re-suspend behaviour and the
diagnostics are implemented and testable; whether they add up to less drain over a night in a bag is
an attended measurement nobody has done yet.

### Processor core preference

`Core\HybridCores` offers the same class of control for a hybrid CPU, on the Device Power page
beside the energy plan and as a second dropdown on Steam's Performance tab. Both surfaces drive the
one policy, so a change from either is the same write and the next read on the other reports it.
Neither caches: activating a power scheme can carry a different preference with it.

WSGM writes only Windows' thread scheduling policy for ordinary and short-running threads, which
Windows itself names: performant processors, prefer performant, efficient, prefer efficient,
automatic. It reads and restores the heterogeneous-policy value beside them but never chooses one,
because `powercfg /qh` enumerates that setting as "use heterogeneous policy 0..4" with no published
meaning, and an undocumented write cannot be verified against what it claims to do. Handheld
Companion assigns meanings to those values; that is an assumption, not a Windows contract.

A mode is one value applied to both thread settings and to both power sources. A stored pair that
disagrees with itself, or carries a value WSGM does not offer, reads back as no mode at all rather
than the nearest one: something else set it, and naming a WSGM mode there would claim WSGM did.
Processor policy takes effect on scheme activation, so the writes and activation happen together
under one gate. Both unrelated heterogeneous-policy values are captured before either write; no
confirming read follows. The control is hidden on a CPU with one efficiency class.

A manual selection calls `PowerSetActiveScheme` once and publishes the written GUID when Windows
accepts it. No read follows the write, and a failed write never triggers a retry or rollback.
Windows remains authoritative through independent refreshes, including subsequent changes made by
Settings or OEM tools. Native failures retain their error codes.

Overlay → Device → Power offers a Windows power-profile dropdown, Apply and Refresh inside the
Windows energy plan card, shown only once Windows has been read and enumerates more than one plan; a
single plan is nothing to choose, and the card and its pin stay hidden. It stays available with
Device Integration off. Choosing an entry stages it; only Apply writes Windows. The current scheme
is read when the sheet opens, when Device is selected and on Refresh. Apply publishes its written
selection. Duplicate names include their GUIDs. An unknown active scheme leaves the picker
unselected; an empty or failed read disables Apply. A failed write keeps the published options
available for another explicit attempt. QAM validates against its published option list and has no
refresh-required write latch. Its first publication after an accepted write uses that value; later
independent refreshes read Windows. Explicit display-timeout selections also write without a prior
read and publish the accepted value. Preview mode allows reads only. Native calls and persistence
run off the UI thread, and closing the overlay discards late UI updates. Idle-timeout selectors
refresh after the active scheme is read.

The last accepted manual selection is saved as `LastSelectedPowerSchemeId`, a GUID in Core config.
It is a reference, not an instruction to reapply at startup, config reload or a session transition.
A failed save reports that Windows applied the scheme but WSGM could not save the reference; it does
not undo or repeat the write. Timeout edits and scheme selection share one mutation gate so an
idle-timeout edit cannot reactivate a stale scheme during selection.

Synthetic tests cover the backend, selection workflow, persistence and Core-only Device navigation.
No live power settings were changed for validation. No session-mode or per-application scheme policy
is installed, and the selector has no device-plugin dependency. WSGM Settings configures WSGM
itself; this Windows control belongs in the overlay and Steam QAM.

Steam QAM → Performance offers a Windows power profile dropdown, built on Valve's dropdown field,
when Windows enumerates more than one plan; with one plan the row publishes no options and is not
drawn. Selecting an entry applies it immediately through the same Core backend and saves the
verified GUID. Each publication reads the active GUID. The installed scheme list is cached for one
minute and invalidated after a selection, so schemes created or renamed outside WSGM appear on the
bounded refresh while routine publications avoid repeating the native enumeration. The backend
rejects unknown or removed GUIDs and requires a fresh read after an uncertain write. The row shows
failures and disables input while its request is pending. The toolkit owns row placement and command
validation; WSGM owns Windows access.

### Processor boost mode

`Core\CpuBoost` is Handheld Companion's "CPU boost mode" as a per-game profile value, ported because
some games run better with boost off (Doom: The Dark Ages) and others with it on (Sonic Frontiers).
It writes Windows' `PERFBOOSTMODE` processor setting exactly as HC does
(`PerformanceManager.RequestPerfBoostMode`, `PowerScheme.WritePowerCfg`): HC's five choices,
Disabled, Enabled, Aggressive, Efficient enabled and Efficient aggressive, as values 0 to 4; the
setting revealed in Windows' own power options; plugged-in and battery written to the same value on
the active scheme; and the scheme re-activated so the policy takes effect. The value is read back
afterwards only for a warning, as `RequestPerfBoostMode` does, and the written mode is what WSGM
publishes. A scheme whose read is refused offers nothing. Windows' modes 5 and 6 are shown as "not
set by WSGM".

The value is a `ProfileValues.CpuBoost` layer like the frame limit: a game profile that sets it wins
while that game runs, Global applies otherwise, and an unset layer leaves Windows alone.
`ApplicationPerformanceReconciler` carries it on every running-application change, with or without a
device plugin, and restores the mode it found before its first write once no layer prefers one. The
overlay's Performance section (Device > Power, present with RTSS and device integration off) and
Steam's Performance tab (`steam-ui.cpu-boost`) show and set the same value through that one carrier;
both mark a game override and offer the way back to Global.

## Device power presets

Steam QAM → Performance offers AC and battery profile assignments when the plugin declares presets.
The Claw A2VM supplies:

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

Applying a preset:

- Changing the assignment for the active power source applies it immediately, serialized with manual
  power, scenario and AutoTDP writes.
- The order is the firmware scenario first, then a read of the resulting watt pair, then PL2 before
  PL1 when raising and PL1 before PL2 when lowering, then the Windows mode, read back last.
- Each device write must be applied, verified or not, before the next step; a rejected or uncertain
  write stops the preset. Device and descriptor generations and the power source are checked between
  steps; an unknown power source blocks scenario presets, and a source change stops the remaining
  writes without retry.
- The manual TDP funnel pauses AutoTDP and records the underlying values through their existing
  owners. Preset scenario commands are not persisted as desired values.
- The plugin journals the exact original scenario and watt pair and restores the scenario first,
  then the pair, when releasing its temporary state.

Reading the current preset:

- Both UIs derive it from observed PL1, PL2, the firmware scenario for the current power source, and
  the effective Windows mode. A mismatch, including an external Windows mode change or a resumed
  AutoTDP adjustment, shows Custom.
- After a successful assignment, a complete current reading that differs from it replaces that
  source's assignment with Custom, including PL1, PL2, Windows mode and the firmware scenario when
  the preset includes it. The other source remains unchanged.
- The open overlay refreshes once per second; QAM refreshes with its regular state publication.
  Missing or stale observations disable selection instead of guessing a preset.
- Disabling Device Integration removes the preset choices and leaves the Windows scheme picker.

A failure can leave some underlying values changed. WSGM reports that partial result, stops, and
does not retry or roll back across Windows and device controls. The plugin's existing per-command
power rollback remains intact. Preview surfaces cannot apply presets, and closing the overlay
cancels remaining work and prevents late UI updates. Validation uses fake device/Windows backends
and emitted dropdown fixtures; it does not change live power settings or a running Steam client.

## AC and battery assignments

Device → Power and Steam QAM → Performance provide **When plugged in** and **On battery** profile
assignments and a read-only active-profile status. There is no separate active-profile selector.
Background reads do not block assignment selection or overwrite an open dropdown. Global assignments
are the defaults; with the per-game profile on, an assignment made for the running game overrides
Global for that source only, and an unset one inherits Global ([profiles](profiles.md)). References
include the plugin ID so changing device packages cannot silently apply another package's similarly
named preset.

When an assignment is applied:

- The session applies an assignment once on source, application, assignment or device-cycle changes.
  Nothing polls: the coordinator reconciles when the message window reports an AC/DC switch
  (`GUID_ACDC_POWER_SOURCE`), when Windows reports an effective power mode change, on every profile
  snapshot, on each device-cycle state change and once the cycle's restore pass has finished, and
  when the observed values or availability of PL1, PL2 or the firmware scenario change. The power
  control trigger applies a preset whose controls arrive late and, with the power mode trigger,
  adopts an out-of-band watt or Windows mode change as Custom.
- Every preset checks the selected power source before each device or Windows write, including
  presets without firmware targets. A source change stops the remaining steps.
- Unknown power sources and unavailable device observations defer application.
- A failed or uncertain write is recorded before dispatch and never retried by polling; explicitly
  saving an assignment permits another attempt.
- Automatic application pauses AutoTDP without overwriting the saved manual watt limit.
- Editing an inactive assignment never reapplies the active preset. Windows power-plan selection
  remains independent of these device preset assignments.

When an assignment is saved:

- Assignment saves reject changes to the application, plugin, device cycle, enabled state, power
  source or performance configuration during the read. The coordinator checks the assignment scope
  again under its transition gate before persistence.
- Saved plugin and preset IDs are trimmed before validation.

Custom values:

- Custom values persist per source and are restored on the next source, application or device-cycle
  transition through the same validated, ordered write path as named presets.
- Manual changes to an inherited assignment create a per-game Custom override without changing the
  global default. Further changes update that source's Custom values; unchanged observations do not
  save or write hardware.
- Missing, stale or uncertain readings and partially failed applications never become saved Custom
  profiles.
- Each assignment dropdown displays Custom when it is saved for that source. The dropdown for the
  source in use also shows Custom while AutoTDP owns power. That is display only: AutoTDP's readings
  are never saved as Custom, and the saved assignment shows again once AutoTDP is off or paused by a
  manual change.
- Custom is a reading; selecting a named preset replaces that source's Custom values.

## Game Mode display layouts

`AppConfig.GameModeLaunch` says what entering and leaving Game Mode do to displays. It has two
kinds, not four modes:

- **Default** is the scaling posture in `Core\DisplayScale.cs`: capture every display's scaling,
  drop them all to 100% so DPI-unaware games render 1:1, and restore on the way back. Nothing else
  about the desktop changes. This is what the four retired modes collapse into, including the old
  Off, which left a handheld running desktop scaling inside Big Picture.
- **Custom** applies a saved `DisplayLayout`: which displays are on, which is primary, where each
  sits, its resolution, refresh rate, scaling and advanced-colour state. Settings edits these values
  directly. Copy current desktop is an optional starting point, not a prerequisite.

A layout is keyed by `DisplayTargetIdentity`, so it survives GDI renumbering and a hotplug.
`WindowsDeviceControl.DisplayLayouts` owns the writing: validate, capture the rollback set, apply
once, and one rollback only when Windows refuses the apply, never a retry. `DisplayLayouts.Describe`
is the pure rule set (at least one display, exactly one at 0,0, no duplicates, no overlaps, all
connected, scaling within range); Settings and configuration normalization both use it, so a layout
that could never describe a desktop is refused before it reaches a display.

Displays WSGM has seen are remembered in `GameModeLaunch.KnownDisplays`, along with the modes,
advanced-colour support and scaling range each reported while it was active. That is what lets a TV
behind an HDMI switch be configured while it is unplugged, which the reference machine requires: the
TV exposes no EDID until the switch selects this PC. Settings > Display shows one row per remembered
display whether or not it is connected, badges the absent ones, and offers Forget to prune the
catalog. Discovery runs on a worker and refreshes rows in place without discarding either draft. New
custom layouts start from the observed arrangement; Game Mode starts at 100% scaling.

Connected displays disabled in Windows are queried through their monitor interface's EDID, including
DisplayID detailed timings for high-refresh and ultrawide modes. Those advertised candidates are
merged with remembered driver modes, so refreshing an inactive screen does not discard its broader
saved list. The normal layout apply still validates the requested arrangement. Discovery never
enables a screen to obtain its modes. A read-only check on Windows build 26200 on 2026-09-13
returned 3840x2160 at 165 Hz for the disabled G7, 2560x1440 at 165 Hz for the disabled X32, and
5120x1440 at 240 Hz for the disabled G93SC.

The Game Mode and Desktop selectors choose independent drafts. A numbered arrangement supports
selection and drag positioning; the inspector separates resolution, refresh rate, scale, HDR,
primary selection and placement. It stays editable when a display is disabled. Disabled values
remain in the open draft; the saved runtime layout contains enabled outputs. Undo restores one edit
or copy operation. Saving stages the next transition and never changes the current desktop. Invalid
enabled layouts block saving. Disconnected displays use their remembered modes. Resolution and
refresh choices keep the same item list while a selection changes; rebuilding that list during the
ComboBox commit reset the chosen value. UI regressions select through the actual pickers, including
while the source is disabled in Windows.

CCD discovery bounds possible routes separately from the number of monitors. A read-only check on
2026-09-13 found 284 possible routes, 568 mode records and three active displays on the desktop,
exceeding the former 256-route bound. The library now admits up to 4,096 routes and 8,192 modes; the
updated observation returned Odyssey G7, HP X32 and Odyssey G93SC. No layout was applied.

Choosing a primary display normalizes the whole arrangement so that display sits at 0,0, which is
where Windows puts it. That one rule is corrected rather than reported, because making a user do the
subtraction themselves is only a way to fail it. Everything else is `DisplayLayouts.Describe`, so
the editor refuses exactly what the apply would.

The scaling snapshot recovery is unchanged: a surviving snapshot never authorizes lowering a newly
docked display that is absent from it, and panic, uninstall and shell repair restore it. The layout
a running Game Mode session owes the desktop is separate and lives in
`AppConfig.GameModeLaunchRecovery`, which the runtime owns and Settings never writes.

HDR uses DisplayConfig advanced-color get/set against the path target. A persisted flag is neither
shown nor applied when the target reports no advanced-colour support.

## Game Mode audio profiles

Each Game Mode launch configuration carries an optional audio preference for Game Mode and another
for Desktop, beside the layouts they already own. Every value in one is independently optional:
default playback endpoint, default recording endpoint, playback volume, mute, playback device format
and spatial sound format. An endpoint is stored by its Core Audio ID with the friendly name it last
had, so a profile keeps naming a device that is currently off or unplugged. Saving a profile never
changes Windows audio.

`Shell\AudioProfileService.cs` performs every Core Audio read and write off the dispatcher and
serializes them behind one gate, so the overlay, Steam Quick Access and a mode transition cannot
interleave writes. Apply order is render endpoint, capture endpoint, volume and mute, device format,
then spatial format; the format and spatial writes target the endpoint the profile selected, after
it has been selected. Volume and mute go through whatever is default at the time, so they are
skipped and reported when the profile named a playback endpoint that did not become the default: the
alternative is changing an unrelated device in the same breath as reporting that the configured one
was left alone. A missing endpoint, an unsupported format, an HRESULT failure or a spatial refusal
leaves that one value unchanged, reports the reason and does not fail the transition. There is no
automatic retry write: the next explicit action or mode switch is the retry.

When a configured endpoint is absent, one application waits up to 3 seconds for Core Audio's
endpoint notification to bring it, which is what lets an HDMI audio endpoint appear as the TV wakes.
The wait wakes on each arrival or state change rather than polling. The budget is for the whole
application rather than per direction, so two absent endpoints cannot hold a desktop return for
twice the wait. After it expires the endpoint is logged as unavailable and left alone.

A custom Game Mode entry captures the desktop audio snapshot wherever it captures the return layout,
and persists both in `AppConfig.GameModeLaunchRecovery` before Explorer leaves. That happens even
when the launch sets no Game Mode audio, because the return falls back to that snapshot whenever the
Desktop profile carries no audio preference of its own. Crash recovery restores from the same
persisted snapshot and clears it only after the return path has finished. The return restores the
layout before the audio, so an HDMI endpoint is back before anything is asked to select it.

Settings edits the profiles and never probes or writes, and never reads Core Audio on the
dispatcher: the endpoint and capability reads run on a worker behind a generation guard, so a wedged
audio driver delays the lists filling in rather than the Settings window opening. A saved endpoint
the machine is not currently reporting stays in the list under its saved name and stays selected,
and a saved format or spatial value that the selected endpoint does not report right now is held as
latent draft state rather than cleared, so an unrelated save cannot silently delete it. Choosing a
different endpoint does clear both, because a format belongs to the endpoint that reported it.

The Overlay Audio panel and the Steam Quick Access row are live controls on the current playback
endpoint, not profile editors. Each read is tagged with the endpoint it described and with the
refresh that produced it, so a read that lands after the default output has already changed is
discarded instead of describing the wrong device, and a selection made against a superseded read is
refused rather than applied to whatever is default now. A command is one explicit write followed by
an observed refresh.

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

### The mute claim keeps the endpoint it muted

The service captures the endpoint ID before muting and restores that same endpoint, even if Windows
changes the default playback device while the screen is dark. It clears the claim only after the
endpoint accepts the unmute write. A failed attempt retains the endpoint ID and is retried on the
existing 2 s timer while the claim is outstanding. Restore does not require a volume read before or
after the write.

### Input while muted restores

While muted, the same timer compares `GetLastInputInfo` against a baseline taken at mute time
(wrap-safe signed tick compare). Keyboard, mouse or touch input means a lit screen, so the mute is
undone even if the display-on notification never arrives.

### Any display state other than off restores

Only state 0 establishes the dark half of the mute condition. Every other value restores, dimmed and
any value Windows adds later, because an unrecognised state must not keep a device silent.

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

`Core\WakeLock.cs` adapts Windows Device Control's `WindowsPowerRequest`, which owns the native
handle and reason buffer. A system request blocks standby while held. The display still times out,
but Wi-Fi and Steam keep running, which is what lets downloads survive "screen off" on a Modern
Standby handheld. Downloads during real Modern Standby sleep are impossible for a Win32 application
(DAM suspends every desktop process, no opt-out), so keep-awake is the whole feature, the same model
as SteamOS "Display-Off Downloads". Windows limits it: indefinite on AC; on battery the request is
force-terminated about 5 min after the sleep timeout expires; the power button always wins.
Historical Claw evidence from 2026-08-12 covered the download hold across screen-off, the
then-current manual cycle, indicator and idle-timeout rows. It does not validate the redesigned
selectors introduced for #114.

There are two independent holds, each its own request so `powercfg /requests` attributes them.

The manual hold is a Power > Wake selector with session lifetime that survives mode switches.
Automatic leaves the manual hold off; Prevent standby holds a system request; Keep screen on also
holds a separate `DisplayRequired` request. Browsing the dropdown stages a choice until it closes.
Readback preserves an open draft and reconciles the resulting held state after the selection,
including partial or refused native changes. Acquisitions precede releases, and failed requests are
not retried automatically.

The automatic download hold (`Shell\KeepAwakeService.cs`, toolkit `SteamDownloadActivity`) polls
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
yellow standby-blocked, red display-pinned, grey unknown, WakeWatch's colour vocabulary on purpose.
`Core\WakeLockStatus.cs` maps the list to a state plus a collapsed holder summary; WSGM's own pid
colours the state but is excluded from the summary.

A "What's keeping this awake" row opens `Overlay\WakeLockHoldersView.cs`, a Power-tab sub-view
listing every requester, deduplicated on (label, detail, reason) so thirty identical Steam requests
read as `steam.exe ×30`, sorted by count then name, with caller kind, pid, path and reason on the
second line. Unlike the summary it does not hide WSGM's own request: the list must not omit an
answer. An unelevated read shows "couldn't read", never an empty all-clear. It belongs to the Power
tab rather than Tools, and it is opened from inside the Wake category, so leaving it restores the
Wake page. Back from the primary page focuses its selected section rail, then returns home.

Windows Device Control's `PowerRequestList` calls the undocumented
`NtPowerInformation(GetPowerRequestList = 45)` on ntdll directly, because the documented wrapper
rejects the class; it needs elevation, and denied yields grey. The version-dependent layout is
decoded by bounds-checked readers ported from WakeWatch's `power.rs` (MIT, same author). Any
structural surprise yields grey, never a false all-clear. The list is polled at 1.5 s only while the
panel is open.

### Idle-timeout rows

The two screen-off rows never turn the display off before Steam's Big Picture screensaver may start.
They select values through `Shell\DisplayTimeouts.cs`, the owner Steam's Screensaver settings rows
share, which excludes presets below Steam's reported screensaver timeout and names that bound in the
row's description. The bound and how it is enforced are in
[the Steam CEF system](steam-cef-system.md#screensaver-settings).

Four selectors (screen-off and standby, each for battery and plugged-in) offer 1, 3, 5, 10, 15, 30,
60 min and never, preserving a current custom value. A popup stages browsing until it closes;
confirmed choices enter an ordered background queue and re-read the active scheme before one write
through `Core\PowerTimeouts.cs`, using Windows Device Control's policy-value API. Parsing
`powercfg /q` was rejected: its output is localized, the same trap as netstat. The rows are a
convenience over the active scheme, deliberately not snapshotted or restored.

Every machine-wide power change, from scheme selection and these timeouts to processor boost, core
placement and the power mode, takes the lock of the session's one `Core\PowerSchemes.cs` instance.
`Core\WindowsPowerPolicy.cs` builds that instance and the owners that share it, and the session
hands them to the overlay, Steam's Quick Access and the device presets, so a scheme switch cannot
land between another owner's read of the active scheme and its write.

### Log lines

| Line                                                                          | Meaning                                               |
| ----------------------------------------------------------------------------- | ----------------------------------------------------- |
| `Keep awake: download hold acquired (…)` / `released (…)`                     | The automatic hold changed, with the snapshot detail. |
| `Keep awake: download hold released (disabled in settings).`                  | Config apply dropped an engaged hold.                 |
| `Keep awake: manual mode Off\|Standby\|StandbyAndDisplay (quick access).`     | The resulting manual hold state.                      |
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
that keeps the persisted preference unchanged. It does not prove that a process exit or crash
restores the active timing. WSGM explicitly restores the original rate on normal release; Windows
reconstructs display state on reboot.

Consequences encoded in `Core\FrameLimitPairing.cs`, `Core\PrimaryDisplayModes.cs` and
`Core\RefreshRatePairingService.cs`:

- Enumeration alone cannot tell an advertised mode from a synthesized one, so the native-modes
  strategy needs the EDID. Without it that strategy would silently equal full frame doubling.
- Rates are enumerated and then tested; a driver may refuse one it enumerated. `CDS_TEST` changes
  nothing and is safe while a game runs.
- Discovery is cached for the primary target, resolution and colour depth because each candidate
  costs a driver round trip. A changed operating point invalidates it, and stale discovery results
  are discarded. Original rates are captured and restored per target.
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

The graphics driver drives the panel's adaptive sync, so the transport belongs to the Intel graphics
package (`src\WSGM.Plugin.IntelGpu`, `wsgm.gpu.intel`), which moved out of the Claw device package
on 2026-09-29. WSGM only projects the capability.

Four facts that cost real time to establish:

- Both enumerations are two-call: ask for the count with a null buffer, then fetch. Passing a buffer
  straight away returns nothing.
- A display is recognised by which output answers, never by index. The reference unit enumerates
  twelve display outputs of which one is real; the other eleven return `CTL_RESULT_ERROR_KMD_CALL`.
  An external display when docked is a different output with its own controls; the built-in panel
  carries the variable refresh role that Valve's Performance row uses.
- IGCL's `bool` is one byte. A managed `bool` is four and would shift every float after it.
- Every call passes its own `sizeof` in a `Size` field and the driver refuses a mismatch. That
  refusal is indistinguishable from "this machine has no variable refresh", so a layout drift would
  remove the feature silently. The sizes are 36 / 24 / 28 and are pinned by a test.

Turning the profile `OFF` collapses the reported range to 120/120, a second confirmation independent
of the profile enum. That is why this capability reports a verified read-back rather than an
applied-unverified one.

## Driver-level VSync is a presentation mode, not a toggle

Intel has no `CTL_3D_FEATURE_VSYNC`. What it has is `CTL_3D_FEATURE_GAMING_FLIP_MODES`, a flag set
whose members are presentation modes, and the reference driver offers four of them: application
default, VSync on, Smooth Sync and capped FPS. That is why the capability is a choice rather than a
boolean, and why the offered set comes from the driver's own supported mask rather than a fixed
list.

The mode goes through IGCL like every other 3D feature, globally and per game. The Claw's first
implementation wrote `<adapter>DKeys\Global_AsyncFlipMode` instead, after a 2026-09-10 probe
concluded that IGCL could not set the mode: it read "an enable byte and a value of zero" and a write
changed nothing. The header explains that result. An enum value is one `uint32 EnableType` at the
start of the value union, so reading it as a flag followed by a value sees the low byte of the mode
as the flag and a permanent zero after it, and writing the mode into the second slot leaves the real
field untouched. The registry path worked, but the driver reads it only at start, so a change needed
a restart. The maintainer preferred direct driver control for that reason on 2026-09-29.

Evidence for the IGCL path, 2026-09-29, on an Alder Lake UHD laptop (`8086:4688`, driver
32.0.101.7088): the driver reports the feature as per-application capable and changeable live
(`LIVE_CHANGE`), and per-application writes of other enum features stored and read back exactly,
landing in the same `3DKeys` store as `<exe>_<Name>` values. That legacy driver refuses every flip
mode itself, so **setting the mode through IGCL is not yet verified on hardware**; it waits for a
Claw run.

## The GPU memory share is a driver setting, not an IGCL call

Intel's Shared GPU Memory Override decides how much system memory the integrated GPU may use. It is
not in IGCL: `ControlLib.dll` has four memory entry points and every one of them is a get. The
driver reads a percentage from `GpuSystemMemoryPinninglimit` under the display adapter's `GMM` key
when it initialises its memory manager, which is the whole reason the change only takes effect after
a restart.

Established on the reference unit on 2026-09-10 by driving Intel Graphics Software and watching what
moved, rather than by reading its exports:

- At rest the value read 57, which is Intel's documented default. Total physical memory was
  33,866,657,792 bytes and the adapter reported 19,327,352,832, which is 57.07% of it. That
  agreement is what ties the registry value to the feature.
- Setting the panel to 44% wrote 44 into exactly that value and nothing else. Not another value
  under the adapter, nothing under `HKLM\SOFTWARE\Intel` or `HKCU\SOFTWARE\Intel`, nothing in
  ProgramData. The only other artifact was Intel Graphics Software's own DPAPI-encrypted per-user
  settings blob, which the driver never reads.
- Pressing reset wrote 57 back rather than deleting the value. There is therefore no separate "has
  been changed" flag, and none is needed: the default is the literal 57. WSGM reports an absent
  value as 57 too, so an untouched machine still shows the control instead of hiding it.
- `qwMemorySize` did not move across either change. That is the restart requirement showing itself,
  and it is why a fresh write and the size the adapter reports legitimately disagree until reboot.

The adapter is matched rather than hard-coded: its index is `0001` on the reference unit and the
class also holds an unreadable `0000` beside it, so the search skips what it cannot read and refuses
when more than one candidate remains. The offered range is 13-87 percent, which is what Intel
Graphics Software shows on this machine. Intel publishes the default and a 10 GB system-memory
requirement but no formula for the bounds, so they are taken from the shipping control.

The setting is a persistent user choice like the charge limit, not a resource the plugin borrows. It
is not journalled and not restored on stop: putting it back would silently undo what the user asked
for. It is the one Intel setting still written to the registry, because IGCL has no call for it, and
the only one the Intel package publishes as global-only with a restart timing. Only the write is
verified; the split itself is not.
