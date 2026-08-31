# RTSS integration

WSGM treats RivaTuner Statistics Server (RTSS) as an optional external application. WSGM does not
download, install, redistribute, repair, update, or remove RTSS, and it does not copy the RTSS SDK,
headers, DLLs, profiles, or license into its own output. Users who enable this integration must
install a supported RTSS release separately.

RTSS state is independent of Device Integration. One session-owned `PerformanceService` is intended
to feed the WSGM overlay and native Steam QAM; neither UI surface owns a separate RTSS adapter. RTSS
absence or failure disables only performance controls and never blocks WSGM startup or a shell mode
transition.

## Read-only evidence captured on the reference device

The 2026-08-28 inspection found the machine-wide uninstall registration at the exact `RTSS` key in
the 32-bit registry view. It reported `RivaTuner Statistics Server 7.3.7`, publisher `Unwinder`, and
an installer under `C:\Program Files (x86)\RivaTuner Statistics Server`. `RTSS.exe` reported product
identity `RTSS` and file version `7.3.5.28314`; the executable and `RTSSHooks.dll` had valid
Authenticode signatures from the MSI bundle publisher.

The installed SDK documents profile functions exported by the architecture-matched
`RTSSHooks.dll`/`RTSSHooks64.dll`: `LoadProfile`, `SaveProfile`, `GetProfileProperty`,
`SetProfileProperty`, and `UpdateProfiles`. Its own HotkeyHandler sample uses those functions to
load one profile, set `FramerateLimit`, save that same profile, and ask running applications to
reload profiles. The same SDK header documents `EnableStat` as the `Show own statistics` profile
property. This establishes supported controls worth prototyping; it does not by itself establish
safe concurrent profile ownership or application-name mapping.

The maintainer accepted redistribution-free use of the installed RTSS profile API for WSGM. WSGM
still ships none of the SDK, headers, DLLs, profiles, or license text and requires the user's own
RTSS installation. Technical compatibility, truthful readback, and coexistence remain normal
engineering gates rather than license blockers.

## Implemented support boundary

`RtssDiscovery` accepts only one machine-wide RTSS 7.3-or-newer registration whose publisher,
protected Program Files location, `RTSS.exe` product/version identity, required profile-API PE
exports, and running process path all agree. It reads the DLL export table as data instead of
loading the DLL. A process merely named `RTSS` is never sufficient. `RtssNativeAdapter` then loads
only that exact architecture-matched, signed DLL by absolute path. It uses the documented profile
API for `FramerateLimit` and `EnableStat`, saves the selected global/application profile, requests
running-profile reload, and reads the same properties back. The overlay control exposes exactly two
verified levels: `0` (off) and `1` (RTSS own statistics on). WSGM does not invent intermediate
levels or rewrite the user's RTSS overlay layout.

The shared performance contract already provides:

- global and per-application desired state with per-property application-to-global fallback;
- adapter-published frame-limit and overlay-level bounds instead of guessed numeric limits;
- one serialized command path with origin/correlation diagnostics;
- distinct requested, applying, verified, applied-unverified, rejected, timed-out, indeterminate,
  failed, and externally-changed outcomes;
- process-generation checks before readback, so an RTSS restart makes an in-flight result
  indeterminate;
- polling only while at least one UI client owns an observation lease, bounded to 250 ms through 30
  seconds (two seconds by default), with cancellation and disposal; and
- no dependency on the Device Integration master toggle.

## WSGM starts RTSS

WSGM depends on RTSS for the frame limit, the performance overlay and AutoTDP's frametimes, and on
a handheld nobody wants to leave game mode to start a background service by hand. RTSS is normally
launched by its own tray entry, which does not run before WSGM does on a service boot — so a
machine that has RTSS installed and working still came up with performance controls unavailable,
purely because of start order. `RtssLauncher` therefore starts it, under two rules:

- Only ever the executable discovery already verified: registered under a protected install root,
  signed, product name RTSS, version 7.3 or newer. It never resolves a path itself and never takes
  one from configuration, so it cannot be pointed at another program.
- One attempt per session. RTSS not appearing is a state worth reporting once, not something to
  retry into: a second copy of a single-instance program is at best wasted and at worst the
  "multiple processes match" case discovery already treats as degraded.

## Frametime-driven AutoTDP

`RtssFrametimeReader` is the only thing WSGM takes from RTSS that the profile API cannot answer. It
opens the `RTSSSharedMemoryV2` mapping read-only and walks the application array the header
describes.

The layout was confirmed against a live RTSS 2.21 (`dwVersion 0x00020015`) on the reference Claw on
2026-08-29, not copied from a header: entry size 12416, application array of 256 entries, and per
entry `dwProcessID` at +0, `szName[260]` at +4, `dwFlags` at +264, `dwTime0` at +268, `dwTime1` at
+272, `dwFrames` at +276. `dwTime0`/`dwTime1` are `GetTickCount` milliseconds; a 1 fps application
reported `dwTime1 - dwTime0 = 2000` over `dwFrames = 2`, which is the 1000 ms mean WSGM uses.
Entries RTSS has not updated for two seconds are treated as not rendering, because RTSS leaves an
entry behind after an application stops drawing and staleness is the only way to tell the two apart.

Everything about this read is defensive. The array is sized from the header rather than a constant,
every offset is bounds-checked against the mapped capacity, the 32-bit tick counters are compared on
their low 32 bits so a 49.7-day wrap cannot produce a huge age, and an absent, truncated, or
unexpected-version mapping simply yields no samples. RTSS running elevated while WSGM is not is one
of those cases and is not an error.

The parsing sits behind an `IRtssRegion` seam so the layout is exercised as an executable
specification (`RtssFrametimeReaderTests`), including the measured 1 fps case above. That seam earns
its place: the live path only produces data while RTSS happens to have a rendering application
hooked, so without it the parsing would be untestable in exactly the situation a test runs in.

**Verification status on the reference Claw, 2026-08-29.** The layout, the tick base, and the
frames/interval mean are device-verified as described above. The shipping reader was then run
against the live RTSS from its own process: it opens the mapping (signature `RTSS`,
`dwVersion 0x00020015`, 5,578,752 bytes) and returns no samples while RTSS has nothing hooked — the
correct answer, and confirmation that an empty result is not masking a failed open. **A frametime
read from an actually rendering game has not been performed yet**; RTSS only creates an application
entry once a hooked 3D application draws, so that step needs a game running and remains attended.

`AutoTdpController` holds the whole control policy and is pure: every input is an argument, every
decision is a return value, and the `AutoTdpReplay` harness in `tests\WSGM.Tests` runs a recorded
trace through it with no device involved. That is the regression harness for this feature — an
oscillation reported from a handheld is reproduced by replaying its trace. The policy itself:

- A window counts as a miss above 1.05x its deadline and as headroom at or below 0.92x. Zero
  tolerance would raise power on every healthy capped game, because a cap is enforced by sleeping.
- Three consecutive misses raise one step; eight consecutive comfortable windows probe one step
  down. The thresholds are deliberately asymmetric: raising costs battery and fixes stutter,
  lowering saves battery and risks stutter.
- A probe that produces a miss restores the previous limit and records it as that context's learned
  floor, so a later settled period cannot probe back into the same stutter. Without that the limit
  oscillates for as long as the game runs.
- A capped window that is not missing is treated as headroom, so a menu at the frame cap descends
  rather than driving power to maximum.
- Every write is followed by two settling windows, missing telemetry resets the streaks rather than
  being read as comfort, and a context change discards the evidence gathered for the previous one.
- A manual power change pauses control until AutoTDP is switched off and on again. Taking the limit
  back from a user who just moved the slider is the most confusing thing this feature could do.

`AutoTdpService` is the binding and decides nothing: it picks the renderer matching the running
application (declining rather than guessing when several render with no identity), finds the
`PowerSustainedLimit` capability and takes its range from the plugin, permits one power write at a
time, and restores the limit it took over from on stop, disable, and disposal. Every prerequisite is
optional and rechecked each second; no RTSS, no plugin, no power capability, or no rendering
application means AutoTDP holds.

The deadline is the applied RTSS frame limit when there is one, and 60 Hz otherwise — never the
panel maximum, because chasing an uncapped refresh rate would raise the limit for as long as the
game could absorb it.

## Remaining live compatibility work

Use a disposable test profile to validate the production adapter without editing an existing user
profile. Record the exact RTSS profile name derived from WSGM's application identity, property
ranges and units, query fidelity, concurrent external-edit behavior, RTSS restart behavior, and
whether a failed save can be rolled back without deleting an external profile.

The RTSS own-statistics control is deliberately binary. Do not equate Steam's numbered performance
overlay presets with `EnableOSD`, RTSS shared flags, or an OverlayEditor layout. Additional levels
require a separately verified reversible mapping and readback contract.
