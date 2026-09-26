# RTSS integration

WSGM uses RivaTuner Statistics Server (RTSS) for the frame limit, the performance overlay and the
frametimes that drive AutoTDP. This doc covers the boundary with RTSS, how the running application
is identified and which profile is written, the overlay levels, starting RTSS, the shared-memory
frametime reader and the AutoTDP policy. The Steam QAM projection of these values is in
`docs\steam-cef.md`; refresh-rate pairing for the frame limit is in `docs\power-and-display.md`.

## Boundary

WSGM treats RTSS as an optional external application and ships none of the RTSS SDK, headers, DLLs,
installers, profiles or licence text. Using the profile API of the installed RTSS is the accepted
boundary; compatibility, truthful readback and coexistence remain ordinary engineering gates.

Setup's `rtss` switch, which Full turns on, is the one exception to "never installs": when it is on
and no RTSS is registered, setup downloads one pinned Guru3D build (`RtssInstaller`: 7.3.7, the file
and SHA-256 winget's `Guru3D.RTSS` manifest pins), refuses it unless the hash matches, and runs its
installer silently. The installer is signed by Micro-Star International, like the reference
installation. A failed download or install is reported on the progress page, is not retried, and
does not stop setup. WSGM never repairs, updates or removes RTSS, and uninstall leaves it in place
because other programs use it too. The switch itself is `Performance.Enabled`.

RTSS state is independent of Device Integration. One session-owned `PerformanceService` feeds both
the WSGM overlay and the native Steam QAM; neither owns a separate RTSS adapter. RTSS absence or
failure disables only the performance controls and never blocks startup or a mode transition.

`RtssDiscovery` accepts exactly one machine-wide RTSS 7.3-or-newer registration whose publisher,
protected Program Files location, `RTSS.exe` product/version identity, required profile-API PE
exports and running process path all agree. It reads the DLL export table as data rather than
loading the DLL; a process merely named `RTSS` is never sufficient. `RtssNativeAdapter` then loads
only that architecture-matched, signed `RTSSHooks.dll`/`RTSSHooks64.dll` by absolute path and uses
the documented profile functions (`LoadProfile`, `SaveProfile`, `GetProfileProperty`,
`SetProfileProperty`, `UpdateProfiles`): set `FramerateLimit`, save the selected profile, ask
running applications to reload, read the property back. The reference installation is 7.3.7 under
`C:\Program Files (x86)\RivaTuner Statistics Server`, with `RTSS.exe` and `RTSSHooks.dll`
Authenticode-signed by the MSI bundle publisher (Claw, 2026-08-28).

## Application identity and profile writes

### Exclude the ClawLab cursor refresh helper

The ClawLab helper owns a desktop refresh surface, so game frame limiting must not target it. On the
reference Claw (2026-09-13), a cursor-activity trace attributed about 76% of the helper's sampled
CPU to RTSS's hook under `DxgiPresenter.Present`, including repeated performance-counter reads. The
global RTSS limit was 119 FPS while the helper paced at 120 Hz. These are sample shares, not a
whole-machine CPU measurement or proof that ClawLab itself busy-waits.

The maintainer's local RTSS profile for `ClawLab-Cursor-Refresh-Helper.exe` sets Application
detection level to None (`[Hooking] EnableHooking=0`) and `[Framerate] Limit=0`. The helper was
restarted through its existing limited-user scheduled task. Global RTSS settings and ClawLab's
VRR/LFC configuration were left intact. This is an installation-specific exclusion, not an automatic
WSGM profile write. The RTSS API read back detection level 0 and frame limit 0. A subsequent
25-second active capture contained no RTSS hook work in the helper's sampled CPU stacks, although
the DLL remained mapped. Its startup warm-up differs from the earlier intermittent mouse workload,
so those captures do not establish an equivalent-workload percentage reduction.

### Shared application identity

`RunningApplicationMonitor` is the only detector; `RunningApplicationCoordinator` hands its one
answer to `ProfileService`, which RTSS, the device and controller policy all resolve from, and QAM
and the overlay read those rather than observing Steam or foreground windows again. Identity comes
from Steam lifetime notifications and foreground-window observation. A Steam AppID wins when exactly
one game is running. More than one running AppID is ambiguous and uses global policy, because
foreground focus is not allowed to guess which game should be edited. A usable foreground executable
fills Steam's missing store-app profile or identifies an application outside Steam.

The performance contract takes its desired frame limit and overlay level from the profile store,
each resolved on its own from the game's profile, then Global (`docs\profiles.md`);
adapter-published frame-limit and overlay-level bounds; one serialized command path with
origin/correlation diagnostics; distinct requested, applying, deferred, verified,
applied-unverified, rejected, timed-out, indeterminate, failed and externally-changed outcomes;
process-generation checks before readback, so an RTSS restart makes an in-flight result
indeterminate; and polling only while a UI client holds an observation lease, bounded to 250 ms
through 30 s (5 s by default). Commands and application transitions still read back immediately; the
slower background check reduces profile reloads and discovery while Steam keeps an observation lease
open. External changes and RTSS availability are detected on that background cadence.

### Configurable application profiles

The overlay header always exposes Global / Per-application and a profile-manager button. The manager
creates, renames and deletes profiles without requiring their applications to run. Each profile can
bind up to 32 exact executable names, compared without case. Duplicate bindings across profiles are
refused, including disabled profiles. A conflicting hand-edited configuration resolves to Global
rather than selecting an arbitrary profile. Existing profiles without explicit process rules retain
their canonical application binding; adding rules replaces that binding.

The editor saves a profile's name, executables and switch, not its values. Values are set on their
own overlay and Quick Access rows while the profile is active, and a profile holds only those; the
rest comes from Global. Config reload reapplies the current application's profile even when no
process transition occurred.

Scope changes, resets and editor saves persist before the new snapshot is published. Global turns
the matching profile off without deleting its values, so turning it back on restores them. Delete
removes the profile. The header rejects a selection captured for a different application, and
persistence failures leave the previous profile in force.

### The foreground fill for a store title is proof-gated by its install folder

A bare foreground name once made `WindowsTerminal.exe` HITMAN 3's sticky frame-limit target for a
whole run (Claw, 2026-09-02). Steam's `strInstallFolder` is resolved from the same AppDetails read
as the shortcut target, and only a foreground process whose image path lies inside that folder may
become the game's RTSS profile. The pairing survives alt-tab; a different validated executable from
the same folder takes it over (a launcher handing off to the game).

### Per-application profiles are written only on opt-in or when RTSS already has one

Saving an absent RTSS profile creates it. Applying effective values on every application transition
sprayed a profile onto every executable that ever took focus, filling the RTSS profile list with
terminals and installers (Claw, 2026-09-02). A per-application write now happens only when the user
opted the application in or RTSS already carries that profile, whose explicit values would otherwise
override the global write. Everything else goes to the global profile.

### A game Steam has named but Windows has not exposed is deferred

Preferences persist against the AppID and report `Deferred` instead of being misapplied to the
global profile; they apply when foreground enrichment arrives.

### Two proofs pair a foreground process with a Steam AppID, and RTSS is the second

A bare foreground name is never enough — that is the `WindowsTerminal.exe` rule above. Either of two
proofs is:

1. **Steam's install folder.** The process runs from inside `strInstallFolder`. Covers every title
   Steam installed, and costs nothing, so it is checked first.
2. **RTSS is rendering it.** The process appears in the `RTSSSharedMemoryV2` application table as
   currently delivering frames, matched on process id — the same table `RtssFrametimeReader` already
   parses for AutoTDP, read through a second reader of its own because that class is not
   thread-safe.

The second exists because Steam can name a running AppID and know nothing else about it. Skyrim SE
launched through Mod Organizer reports `strInstallFolder ""`, `strLaunchOptions ""`,
`iInstallFolder -1` and `bHasAnyLocalContent false`: the title runs, Steam sees the AppID through
the steam_api handshake, and there is no folder to prove anything against. Enabling the
per-application profile created WSGM policy that could never reach RTSS — every write reported
`Deferred` against a foreground executable that would never be accepted (Claw, 2026-09-04).

`GetLaunchOptionsForApp` is not a third source. It returns
`{nIndex, strDescription, strGameName, eType, VR flags}` and names no executable for **any** title,
installed or not — checked against HITMAN, Death Stranding and Metal Gear Solid on the reference
Claw. It is the launch-picker display list.

The RTSS proof is also the more meaningful one here: an RTSS profile for a process RTSS is not
rendering does nothing at all, so this admits exactly the processes the feature can act on. During
that Skyrim run the foreground passed through `ModOrganizer.exe`, `GameBar.exe`, `rustdesk.exe`,
`RTSS.exe` and `waterfox.exe`; none is hooked, so none could take the pairing. A process id of zero
means "could not be read" and never matches.

### Every poll cross-checks the readback against what WSGM asked for

An RTSS profile is a file its own UI, another overlay tool or a game's installer can rewrite, and
none of them announce it. Nothing but the readback proves a profile still says what WSGM wrote, so
`PerformanceService.DriftNeedsRepair` compares the two on every poll and re-applies the effective
desired values through the ordinary `ApplyEffectiveDesiredAsync` path when they disagree.

Three rules keep that from becoming a write loop:

- Only a `Verified` readback counts. An unreadable property is not a mismatch, and treating it as
  one would rewrite the profile on every poll.
- Only a control WSGM actually has a desired value for. A user who has set no frame limit is never
  fought over one.
- **Once per disagreement.** A writer that takes the profile back between polls is a fight WSGM
  cannot win and must not join, so a second consecutive disagreement about the same desired values
  is reported and then left alone until the values change, the readback agrees, or the user sets the
  value again by hand.

| Line                                                | Meaning                                 |
| --------------------------------------------------- | --------------------------------------- |
| `RTSS drifted from what WSGM set (…); re-applying.` | First disagreement; one repair follows. |
| `RTSS still disagrees after a repair (…)`           | Another writer owns it; WSGM stopped.   |
| `RTSS holds the values WSGM set again.`             | The episode ended.                      |

The command outcome line names its origin (`overlay`, `native-qam`, `application-transition`,
`policy-reload`, `drift-repair`) for the same reason: a value nobody meant to set is otherwise
unattributable, and placing that 12 FPS cap took a whole evening because the log could not say which
surface had written it.

## OSD levels

The overlay control exposes Steam's five selector notches. Levels 1–3 are fixed WSGM-rendered
presets with HandheldCompanion's structure (`Core\RtssOsd.cs`); level 4 is HC's Custom level, one
row per widget with order and per-widget detail from the Settings Integration page
(`PerformanceConfig.OsdCustom*`); 0 renders nothing. The level lives in WSGM's renderer, whose live
state is the verified readback. On the wire it is Valve's `EGraphicsPerfOverlayLevel`, which is not
the notch order; `SteamOverlayLevelWire` (toolkit, `SteamPerformanceSurface.cs`) translates at the
QAM boundary in both directions, and everything behind it speaks notches.

| Notch | Rendered                      | Wire value               |
| ----- | ----------------------------- | ------------------------ |
| 0     | nothing                       | Hidden = 0               |
| 1     | Minimal (FPS)                 | Basic = 1                |
| 2     | Extended (one combined row)   | Medium = 2               |
| 3     | Full (one row per subject)    | Full = 3                 |
| 4     | Custom plus live power status | Minimal = 4 (added last) |

Those notch names are what the overlay's Performance overlay row offers, as a dropdown built from
the levels the adapter actually publishes. It used to be a cycling button reading "On" for every one
of 1 to 4, which made four different overlays indistinguishable in the one place they are chosen.
The frame limit beside it is a slider, zero reading "Off": the preset ladder it cycled through could
not reach a rate the ladder did not contain. Both write through
`PerformanceOverlayBridge.SetValueAsync`, which refuses a value the adapter does not accept rather
than sending it. `CyclePerformanceOverlayLevel` still cycles for the OEM button; there is
deliberately no frame-limit equivalent, because stepping a range this size one notch at a time is
not something a button can usefully do.

### The overlay slider and the Quick Access row bookend the same way

Both ask `FrameLimitPairing.FrameLimitRange` — 30 FPS up to the highest rate the display accepted,
capped at 280 because the slider has to stay crossable on a thumbstick. They did not: the overlay
ran over RTSS's own 0-1000 instead, so a stray thumbstick on the Device page set a 12 FPS cap that
RTSS honoured and the Quick Access row could not represent. That row's injected half validates the
state it is handed against its own bookends, so it discarded the whole thing and the frame-limit
slider disappeared from the Quick Access Menu entirely (Claw, 2026-09-03).

Both halves of that changed. The overlay's slider now spans the panel's range, and because it has no
separate off switch the way SteamOS's row does, it keeps zero and treats everything under the floor
as zero — `DescriptorRange.OffBelow`, applied to the committed value and to the label the user reads
while dragging, so the two cannot disagree. On the toolkit side a cap outside the bookends now
stretches them rather than invalidating the row: the row is where the user would have corrected the
value, so deleting it is the one response that cannot be recovered from.

Nonzero levels are drawn into one claimed RTSS OSD slot. `RtssOsdSlots` is a C# port of
RTSSSharedMemoryNET's claim/update/release protocol, the library HandheldCompanion ships (vendoring
its C++/CLI fork was declined). Offsets were verified against RTSS 2.21 on the Claw: OSD array at
+96, eight slots, slot 0 reserved for RTSS, owner `WSGM` at entry+256, text in `szOSDEx` at
entry+512 for 2.7+, the 2.14+ busy flag taken interlocked around text writes, `dwOSDFrame` bumped
per update. Releasing zeroes the whole entry so it returns to the pool; an RTSS restart is survived
by reopening the mapping and re-claiming on the next tick.

Content templates (`RtssOsdContent`) are HC's `Overlay/Strategy` structures; `<FR>`/`<FT>` are
RTSS's own framerate tags. Sensor values come from RTSS's own LibreHardwareMonitor provider,
`LHMDataProvider.exe`, which publishes the sensor tree as XML in the `LHMDPSharedMemory` mapping
under the `Global\Access_LHMDPSharedMemory` mutex. `RtssLhmSensors` selects values with HC's
sensor-name rules (`CPU Total`, `CPU Package` power/temperature, `D3D 3D`, `GPU Power`,
dedicated-beats-shared GPU memory). WSGM starts the provider with `-i` when the mapping is absent;
it deduplicates itself and is the process the Overlay Editor spawns. Samples are cached at HC's
one-second cadence; kernel counters fill what the provider does not publish, and the battery stays
kernel-fed because the provider ships with its battery section disabled. An entry whose metric has
no source does not render.

The slot is OSD data, not a window: it is visible only inside a rendering process RTSS has hooked
and whose RTSS profile permits OSD (HandheldCompanion creates its OSD only from RTSS's `Hooked`
notification). "OSD slot claimed and updating" proves half the feature; the profile gate must also
be open.

Levels 2–4 also show the current sustained `TDP` limit from the device capability readback. While
AutoTDP is switched on and has an accepted current wattage, that controller value takes precedence
until device readback catches up. A separate `AUTO TDP` entry shows the controller's current watts
and a short live activity such as `HOLDING`, `RAISING`, `LOWERING`, `TESTING`, `RESTORING`, or
`SETTLING`. It appears only while AutoTDP is actively controlling the limit, so idle, paused,
startup, unavailable, and disabled states do not leave a misleading row behind. The session pushes
this projection only when device or AutoTDP state changes; the 100 ms renderer does not poll the
device capability router.

### EnableOSD is a one-way gate

Every nonzero apply sets `EnableOSD=1` in the global and current-executable profiles before
publishing the slot, and later application transitions repair each profile as it becomes current.
Level 0 only clears WSGM's slot and never writes `EnableOSD=0`, because that would disable the
user's other RTSS feeders too; a build that did so turned off every overlay on the device (Claw,
2026-09-01). The next day's report found `EnableOSD` off globally and in every inspected profile
until repaired by hand; a read after that repair showed WSGM's nonempty slot plus `ShellHost.exe`
and game entries, but cannot establish the pre-repair cause and is not end-to-end evidence.

### EnableStat leftovers are not cleared

The orange statistics/frametime display seen after that deployment is a separate RTSS-owned surface:
the shared-memory inventory showed only WSGM's slot plus an empty Overlay Editor slot, while RTSS's
`Global` profile and several application profiles still had `EnableStat=1`. Early WSGM builds wrote
that property; current WSGM cannot tell those leftovers from a user's intentional settings, so the
level selector does not clear `EnableStat`. Cleaning the affected profiles is an explicit
maintenance choice.

## WSGM starts RTSS

RTSS is normally launched by its own tray entry, which does not run before WSGM on a service boot,
so a machine with RTSS installed still came up with performance controls unavailable. `RtssLauncher`
starts it under three rules:

- Only the executable discovery already verified. It never resolves a path itself and never takes
  one from configuration, so it cannot be pointed at another program.
- Only on a NotRunning probe, so a second copy of the single-instance program is never started.
- A 30 s cooldown between attempts, not once per session. RTSS's window has no close-to-tray, so one
  accidental X used to end the frame limit, the OSD and AutoTDP's frametimes for the rest of the
  session; a later NotRunning probe past the cooldown starts it again. The cooldown also keeps an
  RTSS that exits immediately from being relaunched on every poll.

| Line                                                                                                   | Meaning                   |
| ------------------------------------------------------------------------------------------------------ | ------------------------- |
| `RTSS is installed but not running; starting it: <path>`                                               | An attempt.               |
| `RTSS did not start; performance controls stay unavailable until the next attempt after the cooldown.` | Start returned false.     |
| `Starting RTSS failed: …`                                                                              | Start threw. Never fatal. |

## Frametime reader

`RtssFrametimeReader` is the only thing WSGM takes from RTSS that the profile API cannot answer. It
opens the `RTSSSharedMemoryV2` mapping read-only and walks the application array the header
describes. The layout was confirmed against a live RTSS 2.21 (`dwVersion 0x00020015`) on the Claw,
2026-08-29, not copied from a header.

| Field                 | Value                                            |
| --------------------- | ------------------------------------------------ |
| Application array     | 256 entries, entry size 12416                    |
| `dwProcessID`         | entry + 0                                        |
| `szName[260]`         | entry + 4                                        |
| `dwFlags`             | entry + 264                                      |
| `dwTime0` / `dwTime1` | entry + 268 / + 272, `GetTickCount` milliseconds |
| `dwFrames`            | entry + 276                                      |
| `dwFrameTime`         | entry + 280, unit not live-verified              |

A 1 fps application reported `dwTime1 - dwTime0 = 2000` over `dwFrames = 2`, the 1000 ms mean WSGM
uses. Entries RTSS has not updated for two seconds are treated as not rendering: RTSS leaves an
entry behind after an application stops drawing, and staleness is the only way to tell.

The read is defensive throughout: the array is sized from the header, every offset is bounds-checked
against the mapped capacity, tick counters are compared on their low 32 bits so a 49.7-day wrap
cannot produce a huge age, and an absent, truncated or unexpected-version mapping yields no samples.
RTSS running elevated while WSGM is not is one of those cases, not an error. The parsing sits behind
an `IRtssRegion` seam so `RtssFrametimeReaderTests` can exercise the layout, including the measured
1 fps case, because the live path only produces data while RTSS has a rendering application hooked.

Verification status: the layout, tick base and frames/interval mean are device-verified as above.
The shipping reader opened the live mapping from its own process (signature `RTSS`,
`dwVersion 0x00020015`, 5,578,752 bytes) and returned no samples while RTSS had nothing hooked,
which confirms an empty result is not masking a failed open. A frametime read from an actually
rendering game has not been performed yet; RTSS creates an entry only once a hooked 3D application
draws, so that step remains attended.

## AutoTDP policy

`AutoTdpController` (`Core\AutoTdp.cs`) holds the whole control policy and is pure: every input is
an argument, every decision a return value. `AutoTdpTraceReplay` in `tests\WSGM.Tests` runs a
recorded trace through it with no device involved; an oscillation reported from a handheld is
reproduced by replaying its trace. `docs\autotdp-controller.md` is the full description, including
the evidence behind each rule. In short:

- Nothing is learned. No floor survives a probe, a context or a session, and control starts from the
  limit the hardware reports. The floors this replaced held a handheld at 24 W through 25 minutes of
  capped 60 FPS play (Claw, 2026-09-26).
- A window counts as a miss above 1.05x its deadline and as headroom at or below 0.92x, or anywhere
  in the 0.97x to 1.05x band a limiter holds a healthy game in. Zero tolerance would raise power on
  every capped game, because a cap is enforced by sleeping.
- A window whose last frame, ratio or length says a present hiatus happened is severe, and so is a
  tick whose gap since the last present has passed 15 target frames. Severe evidence quarantines the
  controller: a probe in flight is restored unjudged, a raise chain is dropped, and three ordinary
  windows are required before anything is judged again.
- Three consecutive missed windows raise one step. Each step is then judged over three windows
  against the ratio it started from, and a chain ends after three steps delivery did not answer.
- Ten seconds of settled window time probe one step down. A probe tolerates one late window, fails
  on two of the last three, and a failure that correlates with power doubles the wait before the
  next probe at that limit, to a minute. The step is always offered again.
- Utilization never commands a wattage. It defers a raise whose windows were all late on an idle
  GPU, separates a loading stall from real work, counts a step as answered, and downgrades a probe
  failure to inconclusive. Every one of those is skipped when no sensor provider is publishing.
- Dwells are sums of fresh window time, so a repeated RTSS read, a missed tick or a slow write
  cannot shorten one. Every write is followed by two settling windows and two seconds.
- A manual power change pauses control until AutoTDP is switched off and on again. Taking the limit
  back from a user who just moved the slider is the most confusing thing this feature could do.
- The context is the application and its deadline together, so changing the frame cap starts over
  rather than judging the new deadline on the old one's windows.

The deadline comes only from verified, active RTSS frame-limit readback. A desired cap, an
unverified write and a default 60 Hz target cannot substitute for an active limiter. The service's
shared availability result drives both QAM and Overlay and guards the coordinator's enable command.
Without a limiter the controls are disabled with `Requires frame-rate limit.` Turning the limiter
off stops control and restores the previous power limit; its performance-state event also clears the
AutoTDP setting. Temporary missing readback suspends runtime control without replacing saved intent.
A verified limiter makes the controls available again.

`AutoTdpService` owns runtime admission and binds the deterministic controller. It picks the
renderer matching the running application (declining rather than guessing when several render with
no identity), finds the `PowerSustainedLimit` capability and takes its range from the plugin,
permits one power write at a time, and restores the limit it took over from on stop, disable and
disposal. Every prerequisite is optional and rechecked each second; no RTSS, no plugin, no power
capability or no rendering application means AutoTDP holds.

When the descriptor declares `PairedPowerLimitId`, AutoTDP requires current readback for both limits
and dispatches `ApplyPowerPair` through the same coordinator. The plugin owns the hardware
relationship, ordering and rollback. Only verified paired results advance control. Both original
values are captured before the first write; release restores the sustained pair and then the
original boost value, including unequal manual limits. Restoration across a device-cycle change is
refused. No automatic target is persisted into profile configuration.

The service also supplies runtime ownership to the shared power-preset projection. Both QAM and
Overlay show Custom while AutoTDP owns power, even if a momentary readback matches a named preset,
both as the active profile and in the assignment dropdown for the source in use. None of that is
saved: the assignment loop never turns AutoTDP's readings into a Custom assignment. A manual or
assigned power change cancels pending automatic dispatch and updates the restoration target;
disabling AutoTDP cannot restore an older value over that newer intent. Editing the boost companion
pauses the pair without saving observed sustained wattage as a new primary preference.

## AutoTDP trace

Settings, System, **AutoTDP trace** records what the controller saw and did, for diagnosing a
control problem such as issue 181. While it is on, each AutoTDP control generation writes one CSV to
`%LOCALAPPDATA%\WSGM\autotdp-traces\autotdp-<local time>-<trace id>.csv`. The setting applies on the
next config reload; switching it off closes the current file.

Recording does not change a decision. `AutoTdpService` fills a row with the values it already
computes and `AutoTdpTraceRecorder` queues it to one background writer, so a manual change on the UI
thread never waits for the disk. A file that cannot be created is logged and that generation goes
untraced; AutoTDP carries on.

Rows:

- `tick`: one per controller window, whether or not the controller was reached. Early exits (no
  power capability, no renderer) keep their status and detail with the controller columns empty.
- `enabled`, `disabled`, `application`, `manual-pause`, `resume`: generation and ownership events.
  The `disabled` row carries the release write and its outcome.

Column groups, in file order:

- Identity and timing: schema version, row, event, monotonic `elapsed_ms`, `wall_utc`, the measured
  `tick_interval_ms`, trace id, WSGM version and the installed device package.
- Context: application id, executable, process id, running generation, context key and target.
- Windows and device: AC state, battery percent, effective power mode, active scheme, the plugin's
  limit range and step, the primary and paired capability ids, their observed values, quality and
  cycle generation.
- RTSS: renderer count, how the sample was selected, raw `dwTime0`/`dwTime1`, window length, frames,
  mean frametime and FPS, raw `dwFrameTime`, sample age, whether the window repeats the previous
  read (`rtss_window_repeat`) and the gap between consecutive windows.
- Controller evidence: whether the window started or re-based the controller and the limit it
  started from (`start_w`: the observed value, else the last written value, else the ceiling on a
  device without readback), how the window was classified and its ratio, the gap since the last
  present and whether that is a hiatus, the control phase, believed and last-good limits, the miss
  streak, the dwell and the dwell this operating point currently needs, the raise baseline and how
  many steps went unanswered, probe and settling counters, consecutive severe windows, which
  utilization rule last changed an outcome, and a probe id that follows one probe from start to
  acceptance or rejection.
- Decision: action, reason token, requested limit and delta, time since the last write-requiring
  decision and since the last dispatched write.
- Application: whether a write was dispatched, its outcome, readback, whether AutoTDP counted it as
  applied, its duration, a note for skipped or failed writes, and the observed limits afterwards.
- Sensors: CPU and GPU load, CPU and GPU power and battery power. The control path reads these
  before its decision and the row records that same sample, so the GPU load beside a decision is the
  one the decision used.

Numbers use invariant round-trip formatting and an empty cell means the value was unavailable, never
zero. Columns are read by name, so the replay tolerates a file with more or fewer of them; the
schema version says which policy wrote it, and version 1 files were written by the learned-floor
controller that version 2 replaced.

`AutoTdpTraceReplay` in `tests\WSGM.Tests\Builders` replays a trace file through a controller. It
starts at the first window that started the controller and feeds back only raw inputs — the window,
the deadline, the device bounds and the sensor sample — so a file recorded under one policy can
drive another. Where the file also holds a recorded decision it pairs it with the replayed one.
`tests\WSGM.Tests\Fixtures\AutoTdp` holds hand-authored traces of the shapes that matter, with their
provenance in a README beside them.

## Remaining live work

- Validate the production adapter with a disposable test profile rather than an existing user
  profile: record the exact RTSS profile name derived from WSGM's application identity, property
  ranges and units, query fidelity, concurrent external-edit behaviour, RTSS restart behaviour, and
  whether a failed save can be rolled back without deleting an external profile.
- Perform a frametime read from a rendering game.
- Decide whether to clean the `EnableStat=1` leftovers from the affected RTSS profiles.
