# RTSS integration

WSGM uses RivaTuner Statistics Server (RTSS) for the frame limit, the performance overlay and the
frametimes that drive AutoTDP. This doc covers the boundary with RTSS, how the running application
is identified and which profile is written, the overlay levels, starting RTSS and the shared-memory
frametime reader. The AutoTDP controller built on it is in [AutoTDP](autotdp-controller.md); the
Steam QAM projection of these values is in [the Steam CEF system](steam-cef-system.md); refresh-rate
pairing for the frame limit is in [power and display](power-and-display.md).

## Boundary

WSGM treats RTSS as an optional external application and ships none of the RTSS SDK, headers, DLLs,
installers, profiles or licence text. Using the profile API of the installed RTSS is the accepted
boundary; compatibility, truthful readback and coexistence remain ordinary engineering gates.

Setup's `rtss` switch, which Full turns on, is the one exception to "never installs". When it is on
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

The ClawLab helper owns a desktop refresh surface, so game frame limiting must not target it. A
cursor-activity trace on the reference Claw (2026-09-13) attributed about 76% of the helper's
sampled CPU to RTSS's hook under `DxgiPresenter.Present`, including repeated performance-counter
reads, while the global RTSS limit was 119 FPS and the helper paced at 120 Hz. These are sample
shares, not a whole-machine CPU measurement or proof that ClawLab itself busy-waits.

The maintainer's local RTSS profile for `ClawLab-Cursor-Refresh-Helper.exe` sets Application
detection level to None (`[Hooking] EnableHooking=0`) and `[Framerate] Limit=0`. The helper was
restarted through its existing limited-user scheduled task; global RTSS settings and ClawLab's
VRR/LFC configuration were left intact. This is an installation-specific exclusion, not an automatic
WSGM profile write. The RTSS API read back detection level 0 and frame limit 0, and a subsequent
25-second active capture contained no RTSS hook work in the helper's sampled CPU stacks, although
the DLL remained mapped. Its startup warm-up differs from the earlier intermittent mouse workload,
so those captures do not establish an equivalent-workload percentage reduction.

### Shared application identity

`RunningApplicationMonitor` is the only detector. `RunningApplicationCoordinator` hands its one
answer to `ProfileService`, which RTSS, the device and controller policy all resolve from; QAM and
the overlay read those rather than observing Steam or foreground windows again. Identity comes from
Steam lifetime notifications and foreground-window observation. A Steam AppID wins when exactly one
game is running. More than one running AppID is ambiguous and uses global policy, because foreground
focus is not allowed to guess which game should be edited. A usable foreground executable fills
Steam's missing store-app profile or identifies an application outside Steam.

The performance contract takes its desired frame limit and overlay level from the profile store,
each resolved on its own from the game's profile, then Global ([profiles](profiles.md)). It also
takes adapter-published frame-limit and overlay-level bounds; one serialized command path with
origin/correlation diagnostics; distinct requested, applying, deferred, applied, rejected, failed
and externally-changed outcomes; a process-generation check before each write, so a command
addressed to an RTSS that restarted fails rather than landing on the new process; and polling for
the service lifetime, bounded to 250 ms through 30 s (5 s by default). A write publishes the written
value as observed, and the next poll reads it back. External changes and RTSS availability are
detected on that cadence, even when neither performance UI has been opened.

### Configurable application profiles

The overlay header always exposes Global / Per-application and a profile-manager button. The manager
creates, renames and deletes profiles without requiring their applications to run. Each profile can
bind any number of exact executable names, compared without case. Duplicate bindings across profiles
are refused, including disabled profiles. A conflicting hand-edited configuration resolves to Global
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
become the game's RTSS profile. The pairing survives alt-tab, and a different validated executable
from the same folder takes it over, as when a launcher hands off to the game.

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

A bare foreground name is never enough; that is the `WindowsTerminal.exe` rule above. Either of two
proofs is:

1. **Steam's install folder.** The process runs from inside `strInstallFolder`. Covers every title
   Steam installed, and costs nothing, so it is checked first.
2. **RTSS is rendering it.** The process appears in the `RTSSSharedMemoryV2` application table as
   currently delivering frames, matched on process id. This is the same table `RtssFrametimeReader`
   already parses for AutoTDP, read through a second reader of its own because that class is not
   thread-safe.

The second exists because Steam can name a running AppID and know nothing else about it. Skyrim SE
launched through Mod Organizer reports `strInstallFolder ""`, `strLaunchOptions ""`,
`iInstallFolder -1` and `bHasAnyLocalContent false`: the title runs, Steam sees the AppID through
the steam_api handshake, and there is no folder to prove anything against. Enabling the
per-application profile created WSGM policy that could never reach RTSS, because every write
reported `Deferred` against a foreground executable that would never be accepted (Claw, 2026-09-04).

`GetLaunchOptionsForApp` is not a third source. It returns
`{nIndex, strDescription, strGameName, eType, VR flags}` and names no executable for **any** title,
installed or not, checked against HITMAN, Death Stranding and Metal Gear Solid on the reference
Claw. It is the launch-picker display list.

The RTSS proof is also the more meaningful one: an RTSS profile for a process RTSS is not rendering
does nothing at all, so this admits exactly the processes the feature can act on. During that Skyrim
run the foreground passed through `ModOrganizer.exe`, `GameBar.exe`, `rustdesk.exe`, `RTSS.exe` and
`waterfox.exe`; none is hooked, so none could take the pairing. A process id of zero means "could
not be read" and never matches.

### Every poll cross-checks the readback against what WSGM asked for

An RTSS profile is a file its own UI, another overlay tool or a game's installer can rewrite, and
none of them announce it. WSGM follows HC's RTSS watchdog (`RTSSPlatform.Watchdog_Elapsed`):
`PerformanceService` compares the readback with the effective desired values on every poll and
writes the desired values again through the ordinary `ApplyEffectiveDesiredAsync` path whenever they
differ. A control without a desired value or without a readback is left alone, so a user who has set
no frame limit is never fought over one. Each repair logs
`RTSS drifted from what WSGM set (…); writing it again.`

The command outcome line names its origin (`overlay`, `native-qam`, `application-transition`,
`policy-reload`, `drift-repair`) for the same reason: a value nobody meant to set is otherwise
unattributable, and placing that 12 FPS cap took a whole evening because the log could not say which
surface had written it.

## OSD levels

The overlay control exposes Steam's five selector notches. Levels 1–3 are fixed WSGM-rendered
presets with HandheldCompanion's structure (`Core\RtssOsd.cs`); level 4 is HC's Custom level, one
row per widget with order and per-widget detail from the Settings Integration page
(`PerformanceConfig.OsdCustom*`); 0 renders nothing. The level lives in WSGM's renderer, whose live
state is the observed value. On the wire it is Valve's `EGraphicsPerfOverlayLevel`, which is not the
notch order; `SteamOverlayLevelWire` (toolkit, `SteamPerformanceSurface.cs`) translates at the QAM
boundary in both directions, and everything behind it speaks notches.

| Notch | Rendered                      | Wire value               |
| ----- | ----------------------------- | ------------------------ |
| 0     | nothing                       | Hidden = 0               |
| 1     | Minimal (FPS)                 | Basic = 1                |
| 2     | Extended (one combined row)   | Medium = 2               |
| 3     | Full (one row per subject)    | Full = 3                 |
| 4     | Custom plus live power status | Minimal = 4 (added last) |

Those notch names are what the overlay's Performance overlay row offers, as a dropdown built from
the levels the adapter actually publishes. A cycling button that read "On" for every one of 1 to 4
made four different overlays indistinguishable in the one place they are chosen. The frame limit
beside it is a slider, zero reading "Off", because the preset ladder it used to cycle through could
not reach a rate the ladder did not contain. Both write through
`PerformanceOverlayBridge.SetValueAsync`, which refuses a value the adapter does not accept rather
than sending it. `CyclePerformanceOverlayLevel` still cycles for the OEM button. There is
deliberately no frame-limit equivalent, because stepping a range this size one notch at a time is
not something a button can usefully do.

### The overlay slider and the Quick Access row bookend the same way

Both ask `FrameLimitPairing.FrameLimitRange`: 30 FPS up to the highest rate the display accepted,
capped at 280 because the slider has to stay crossable on a thumbstick. The overlay used to run over
RTSS's own 0-1000 instead, so a stray thumbstick on the Device page set a 12 FPS cap that RTSS
honoured and the Quick Access row could not represent. That row's injected half validates the state
it is handed against its own bookends, so it discarded the whole thing and the frame-limit slider
disappeared from the Quick Access Menu entirely (Claw, 2026-09-03).

Both halves changed. The overlay's slider spans the panel's range, and because it has no separate
off switch the way SteamOS's row does, it keeps zero and treats everything under the floor as zero.
`DescriptorRange.OffBelow` applies to the committed value and to the label the user reads while
dragging, so the two cannot disagree. On the toolkit side a cap outside the bookends stretches them
rather than invalidating the row: the row is where the user would have corrected the value, so
deleting it is the one response that cannot be recovered from.

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
until repaired by hand. A read after that repair showed WSGM's nonempty slot plus `ShellHost.exe`
and game entries, but cannot establish the pre-repair cause and is not end-to-end evidence.

### EnableStat leftovers are not cleared

The orange statistics/frametime display seen after that deployment is a separate RTSS-owned surface:
the shared-memory inventory showed only WSGM's slot plus an empty Overlay Editor slot, while RTSS's
`Global` profile and several application profiles still had `EnableStat=1`. Early WSGM builds wrote
that property. Current WSGM cannot tell those leftovers from a user's intentional settings, so the
level selector does not clear `EnableStat`, and cleaning the affected profiles is an explicit
maintenance choice.

## WSGM starts RTSS

With RTSS integration enabled, WSGM probes at service startup and starts the verified installation
when it is not running. Switching integration on does the same immediately. The verified process is
watched, whether WSGM started it or found it already running, and its exit triggers a fresh probe
and restart. The unconditional 5 s poll is the backstop for a missed exit notification.

- Only the executable discovery already verified. The launcher never resolves a path itself or takes
  one from configuration.
- Only on a NotRunning probe. One in-flight guard spans launch and the 10 s initialization settle,
  so concurrent refreshes cannot start another copy. A failed start releases the guard immediately.
- Integration off means probes without launches or writes. WSGM never kills RTSS; its detached
  process continues running after WSGM exits.

The overlay level applies through the global RTSS profile while a running game's executable is
unknown. Only its frame limit waits for executable enrichment. Readback observes the global overlay
level during that wait, so the poll can restore it when RTSS becomes ready.

| Line                                                                                | Meaning                                           |
| ----------------------------------------------------------------------------------- | ------------------------------------------------- |
| `RTSS is installed but not running; starting it: <path>`                            | An attempt.                                       |
| `RTSS did not start; performance controls stay unavailable until the next attempt.` | Start returned false.                             |
| `Starting RTSS failed: …`                                                           | Start threw. Never fatal.                         |
| `RTSS exit watch unavailable for process <pid>: <reason>`                           | Exit subscription failed; polling remains active. |

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

The read is defensive throughout. The array is sized from the header, every offset is bounds-checked
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

## AutoTDP

RTSS is what makes AutoTDP possible: the frametime reader above supplies the windows the controller
judges, and the observed frame limit supplies the deadline: the readback, or the value WSGM wrote
until the next poll reads it. A desired cap or a default 60 Hz target cannot substitute for an
active limiter. Without one the controls are disabled with `Requires frame-rate limit.`. Turning the
limiter off stops control and restores the previous power limit but leaves the AutoTDP setting
alone, because the limit is per application and switching to a window without one and back is the
ordinary case (Claw, 2026-09-27). The controller, the service that admits it and the trace it
records are in [AutoTDP](autotdp-controller.md).

## Remaining live work

- Validate the production adapter with a disposable test profile rather than an existing user
  profile: record the exact RTSS profile name derived from WSGM's application identity, property
  ranges and units, query fidelity, concurrent external-edit behaviour, RTSS restart behaviour, and
  whether a failed save can be rolled back without deleting an external profile.
- Perform a frametime read from a rendering game.
- Decide whether to clean the `EnableStat=1` leftovers from the affected RTSS profiles.

## Source routes

| Boundary                                           | Source                                                                                    |
| -------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| Registration, file identity and compatible exports | [RtssDiscovery](../src/WSGM/Core/RtssDiscovery.cs)                                        |
| Starting the installed application                 | [RtssLauncher](../src/WSGM/Core/RtssLauncher.cs)                                          |
| Profile API and installed native-library lifetime  | [RtssNativeAdapter](../src/WSGM/Core/RtssNativeAdapter.cs)                                |
| Typed status, target and adapter contract          | [RtssModels](../src/WSGM/Core/RtssModels.cs)                                              |
| Shared-memory frametime snapshots                  | [RtssFrametimeReader](../src/WSGM/Core/RtssFrametimeReader.cs)                            |
| Performance overlay composition                    | [RtssOsd](../src/WSGM/Core/RtssOsd.cs)                                                    |
| Application/profile reconciliation                 | [ApplicationPerformanceReconciler](../src/WSGM/Shell/ApplicationPerformanceReconciler.cs) |
| Session construction and disposal                  | [ShellSession.Performance](../src/WSGM/Shell/ShellSession.Performance.cs)                 |

RTSS observations feed both performance presentation and AutoTDP. Their readback can update
effective state and diagnose an external edit, but cannot become a new saved preference.
Installation is a separate setup action; reading these files or building their fake-backed tests is
not a live RTSS acceptance run.
