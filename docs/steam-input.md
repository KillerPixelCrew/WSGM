# The Steam Input lease

WSGM's side of the Steam Input lease: why the overlay needs it, how the gate DLL reaches Steam, who
owns the lease while surfaces open and close, and what to look at when it fails on a device. The
native library (proxy DLL, pipe protocol, hooks, controller recovery) is documented in
`native\SteamInput\README.md` and is not repeated here.

Related:

- `native\SteamInput\README.md` — the gate DLL, ABI, hook coverage and recovery internals.
- `docs\steam-cef-system.md` — the Steam cold-start hang and the transport gate that fixed it.

## Why the lease exists

### Steam Input's desktop profile swallows the controller

When Steam Input's desktop profile activates it takes the controller from every API, system-wide:
XInput, DirectInput and raw HID. WSGM's overlay takes focus like Game Bar does, which mutes the game
while the sheet is open, and that is only acceptable because of the lease: the gate blocks
controller access inside `steam.exe`, so SDL in WSGM reads the pad directly while Steam's active
layout is left untouched.

The lease is scoped to a focused surface: acquired before the overlay or Settings opens, released
after the last one closes. It is an open named-pipe connection, so Windows drops it after a WSGM
crash. A normal release asks Steam to rediscover its controllers.

## How it is delivered

The gate is a proxy DLL that Steam loads itself; WSGM never injects. The library's `allow_injection`
defaults to false and `SteamInputBlocker` sets it explicitly, so this is a property of the code.
`Core\SteamInputShim.cs` copies `steam_input_gate.dll` into Steam's install directory as
`XInput1_4.dll` (ValvePlug proves that vector loads), or as `dinput8.dll` when that name is taken.
Steam maps it on its next cold start.

Three facts about the live client make that safe. Nothing in `steam.exe` hardens the search order
(no `SetDefaultDllDirectories` or `AddDllDirectory`; the lone `SetDllDirectoryA` in `SteamUI.dll`
cannot displace the application directory). Neither name is a KnownDLL. Nothing in Steam's directory
statically imports XInput or DirectInput, so a missing export degrades a `GetProcAddress` to NULL
instead of failing a load.

Three rules in the deployer are load-bearing:

- Never overwrite a file whose ownership signature (the `WsgmSteamInputGateProxy` export) does not
  prove it is ours. ValvePlug and Special K claim the same file names.
- Never `File.Move(..., overwrite: true)` on the park or restore path. `REPLACE_EXISTING` fails
  against a mapped image, which is why disabling parks the file as `.dlld` instead of deleting it.
- Inside the gate, resolve the real system module by its full System32 path. The loader keys modules
  by base name, so a bare-name load would hand the gate its own image to detour.

`steam_input_gate.dll` and `steam_input_lease_ffi.dll` ship beside `WSGM.exe`.

### Hooks are installed on the first lease, never at load

MinHook's `MH_ApplyQueued` suspends every thread in the process to patch. As a proxy the DLL is
mapped during Steam's own startup, and suspending threads under the loader lock hung Steam on the
first cold boot after an install: unkillable by `steam://exit` or a process-tree kill, with the warm
second start working (Claw, 2026-08-19). Hook installation and the recovery warm-up therefore wait
for the first `AcquireLease`. Keep them out of `DllMain` and the server thread.

The observable check: with no lease taken, Steam has loaded our image plus the real vector module.
`xinput1_3`, `xinput1_2`, `xinput1_1` or `xinput9_1_0` appearing means the first lease has installed
hooks.

## Bootstrap blocking and the startup hang

Every proxy export starts blocked. Until the gate's worker has cached the complete forwarding table,
an XInput or DirectInput call returns its disconnected fallback without allocating, resolving an
export or entering the loader. This is separate from the lease count: a bootstrap block has no
surface owner, installs no HID hooks and is not a client lease.

The history is short. The 1.5.0 proxy hung Steam on every Claw cold boot. Recording the module
handle first in `DllMain` closed a livelock in which every XInput call reloaded the real DLL and
cached nothing while SDL probed four user indices; that build passed ten consecutive boots (Claw,
2026-08-20). The hang recurred, bootstrap blocking was added, and it recurred again. The next failed
boot's trace showed the gate finishing forwarding and rediscovery in 2 ms, reaching
`control pipe listening` and serving zero bootstrap fallbacks, identical to a good boot. The proxy
was cleared as the cause; the hang belonged to CEF touching Steam's front-end before any Big Picture
window existed. Its home is `docs\steam-cef-system.md`, section "The transport gate". Do not label
proxy initialization timing as the root cause again without a failing trace that differs.

### Export ordinals come from one `.def` file

The same inspection found that rustc's automatic cdylib ordinals had placed `DirectInput8Create` at
XInput ordinal 104 and `DllRegisterServer` at 109, so a dynamic lookup of either undocumented XInput
ordinal would call an incompatible signature. `build.rs` now writes one authoritative `.def`: named
XInput exports match the System32 ordinals, 100/101/102/103 and 108 stay NONAME, 104 and 109 stay
empty, and the `dinput8.dll` fallback lives at 200-205. `eng\build-steam-input-lease.ps1 -Validate`
inspects the finished PE and fails the build if that contract drifts.

## Diagnostics

Every mapped gate writes a per-process startup trace. `DllMain` and the proxy exports only update
atomics; the worker writes the file after the loader lock is released, so tracing cannot add file
I/O or locks to the path being diagnosed. Per-pid names keep a failed direct-boot trace intact when
Steam is later started by hand for comparison.

| Where                                                                     | What                                                                               |
| ------------------------------------------------------------------------- | ---------------------------------------------------------------------------------- |
| `%LOCALAPPDATA%\WSGM\steam-input-gate-<steam-pid>.log`                    | the trace; the cold-start launcher writes the expected path into `wsgm.log`        |
| file missing                                                              | the worker never reached its first phase, or the profile directory was unavailable |
| last line `forwarding initialization started`                             | the stall is inside loading the real module                                        |
| `control pipe listening`                                                  | gate initialization finished                                                       |
| `Steam Input lease acquired via ...` / `Steam Input lease released (...)` | the WSGM-side events in `wsgm.log`; keep them for device reports                   |

The gate's control pipe carries the DACL `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<token owner>)`: full
access for System, administrators and the token owner, so a read-only open cannot consume a pipe
instance and its worker. The owner comes from `GetTokenInformation(TokenOwner)` because
`CREATOR OWNER` is not expanded in a directly applied DACL. If token lookup or SDDL conversion
fails, the pipe uses the Windows default descriptor so blocking stays available; the trace says
which descriptor was used.

## Owner claims and the Settings handoff

Several surfaces can need the one process-wide lease at once, so each focused surface registers a
named owner claim in `SteamInputBlocker` and the lease is released when the last owner lets go.
`AcquireFor` registers the owner before it attempts the native acquire. Every deactivate and close
path must therefore call `ReleaseFor`, even when Steam was unavailable and `IsApplied` stayed false.

In the overlay-to-Settings handoff, Settings registers its owner first and the deferred overlay
close removes the overlay's. Abandoning either name leaves the controller blocked after the visible
surface is gone (device-observed, 2026-08-15). Settings ignores the transient deactivation caused by
the overlay's 150 ms deferred close and resumes focus-based ownership once the overlay acknowledges
the handoff; releasing during that overlap drops and re-revokes the controller (device-observed,
2026-08-12).

## Measured cost

Verified end to end with `steam-input-lease.exe` and a real Steam Controller (dev box, 2026-08-12):
acquire took the pad from Steam (`tracked HID handles` 1 → 0, `handles revoked by last transition` =
1), Steam rediscovered it within 700 ms of release, and `--rescan` moved Steam's scan counter 14
→ 16.

| Operation                                                | Cost                               |
| -------------------------------------------------------- | ---------------------------------- |
| cold inject + acquire + release (injection era, one-off) | 492 ms, dominated by the injection |
| warm acquire + release, with or without a pad            | 41-42 ms                           |
| one pipe reply (`--status`), ten consecutive calls       | 12-16 ms                           |

Recovery layout discovery runs once during gate warm-up and is cached; pipe replies do not repeat
the cross-process scan. The first acquire must still report the internal-recovery capability, and
the pinned gate stays mapped until Steam restarts.
