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

## Remaining live compatibility work

Use a disposable test profile to validate the production adapter without editing an existing user
profile. Record the exact RTSS profile name derived from WSGM's application identity, property
ranges and units, query fidelity, concurrent external-edit behavior, RTSS restart behavior, and
whether a failed save can be rolled back without deleting an external profile.

The RTSS own-statistics control is deliberately binary. Do not equate Steam's numbered performance
overlay presets with `EnableOSD`, RTSS shared flags, or an OverlayEditor layout. Additional levels
require a separately verified reversible mapping and readback contract.
