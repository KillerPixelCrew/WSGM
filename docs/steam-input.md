# The Steam Input lease

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

1. **Steam Input's desktop profile swallows the controller from every API** (XInput/DInput/HID,
   system-wide) the moment it activates. The **only** reason the overlay may take focus
   (Game-Bar-style, which mutes the game while the panel is open) is the **Steam Input Lease**: its
   gate blocks controller access inside `steam.exe`, leaving SDL direct access for WSGM without
   changing Steam's active layout. The lease is **scoped to the overlay/taskbar lifetime** —
   acquired before each focused surface opens and released after the last one closes. It is an open
   named-pipe connection, so Windows releases it after a WSGM crash; normal release requests Steam
   controller rediscovery. **Delivery is a proxy DLL, not injection (since the Steam Input
   Management work).** `WSGM.exe` NEVER injects — the C ABI's `allow_injection` defaults to false
   and `SteamInputBlocker` sets it explicitly, so this is a property of the code, not a promise. The
   payload is deployed by `Core\SteamInputShim.cs` into **Steam's own install directory** under a
   name Steam resolves through the default DLL search order (`XInput1_4.dll` first — ValvePlug
   proves that vector loads — then `dinput8.dll`), and Steam loads it itself. Verified on a live
   client: nothing in `steam.exe` hardens the search order (no `SetDefaultDllDirectories` /
   `AddDllDirectory` anywhere, statically or dynamically; the lone `SetDllDirectoryA` in
   `SteamUI.dll` cannot displace the application directory), neither name is a KnownDLL, and
   **nothing in Steam's directory statically imports XInput or DirectInput** — so a missing export
   degrades a `GetProcAddress` to NULL instead of failing a load. Three rules are load-bearing:
   never overwrite a file the ownership signature does not prove is ours (ValvePlug and Special K
   claim the same names); never `File.Move(..., overwrite: true)` on the park/restore path
   (`REPLACE_EXISTING` fails against a mapped image, which is why disabling parks to `.dlld` instead
   of deleting); and resolve the real system module by FULL System32 path inside the gate, because
   the loader keys loaded modules by BASE NAME and a bare-name load would hand the gate its own
   image to detour. **Hooks are installed on the FIRST LEASE, never at load (device-verified
   2026-08-19).** MinHook's `MH_ApplyQueued` suspends every thread in the process to patch safely.
   Under injection that ran against a fully started, quiescent Steam. As a proxy the DLL is mapped
   during Steam's OWN startup, and suspending threads while the loader lock is being taken
   constantly hung Steam on the first cold boot after an install — completely, unkillable by
   `steam://exit` or a process-tree kill, Task Manager required, with a second (warm) start working.
   `ensure_hooks_installed()` therefore defers `install_hooks()` and the recovery warm-up to the
   first `AcquireLease`. Never move hook installation back into `DllMain`/`server_thread`.

   **The proxy forwarders start BLOCKED and the worker releases them only after complete
   initialization (implemented 2026-08-22; DEVICE-DISPROVEN as a complete cure for the startup hang
   later that day).** This is deliberately separate from `LEASE_COUNT`: a bootstrap block has no
   surface owner and must not install the HID hooks or appear as a client lease. `DllMain` first
   records its `HINSTANCE`, pins the image before its worker can race SDL's `FreeLibrary`, and
   starts the worker. Until that worker finishes, every XInput or DirectInput proxy export returns
   its disconnected fallback without allocating, resolving an export, or entering the Windows
   loader. The worker identifies the deployed vector, loads the real module by full System32 path
   exactly once, caches the complete forwarding table, then publishes one release store and posts
   the ordinary `WM_DEVICECHANGE` rediscovery notification. A failed initialization is cached and
   remains blocked; no Steam call can retry it. A rejected self-load is balanced with `FreeLibrary`
   rather than leaking a module reference. This matches the startup property that made ValvePlug the
   useful control: it begins blocked, pins itself during process attach, and resolves the real
   module on its own initialization thread.

   The earlier identity fix was necessary but not sufficient. `DllMain` began recording its module
   before anything else after the 1.5.0 proxy hung Steam on every Claw cold boot; that build then
   passed 10 consecutive boots (device-verified 2026-08-20), but another XInput startup hang was
   observed on 2026-08-22. Before the identity store, `proxy::is_self` failed closed until the
   server thread ran, so every XInput call loaded the real DLL, rejected it as possibly-us, cached
   nothing, and repeated while SDL probed four user indices. The resulting loader-transaction storm
   starved the server thread that would end the window. Warm starts and holding the stick UP both
   broke the loop from outside, which identifies a livelock rather than a fixed lock cycle.
   Recording the handle closed that particular window; bootstrap blocking removes real-module
   loading from Steam's startup threads altogether, but the same hang subsequently recurred. The
   strongest discriminator was the launch context: it repeated when WSGM started Steam during direct
   boot, while starting Steam by hand with the same deployed shim succeeded. The next failed boot
   supplied decisive trace evidence: PID 12064 completed forwarding and rediscovery in 2 ms, reached
   `control pipe listening`, and served zero bootstrap fallbacks — equivalent to successful traces.
   The failed path instead mutated Steam's card library over CEF before any Big Picture window
   existed (see `docs\steam-cef.md`). Do not label proxy initialization timing as the root cause
   again without a failing trace that differs.

   Inspection after the recurrence also found that rustc's automatic cdylib export ordinals had
   silently placed `DirectInput8Create` at XInput ordinal 104 and `DllRegisterServer` at 109. A
   dynamic lookup of either undocumented XInput ordinal would therefore call an incompatible
   function signature. `build.rs` now supplies one authoritative `.def`: named XInput exports match
   the System32 ordinals, ordinal-only 100/101/102/103/108 remain NONAME, 104/109 remain empty, and
   the **retained** name-resolved `dinput8.dll` fallback lives at non-conflicting ordinals 200-205.
   `eng\build-steam-input-lease.ps1 -Validate` inspects the finished PE and fails if that contract
   drifts. The observable check with no lease is ours plus the real vector module loaded by the
   worker; `xinput1_3/1_2/1_1/9_1_0` appearing means the first lease installed hooks. Keep the
   native `steam_input_gate.dll` and `steam_input_lease_ffi.dll` beside WSGM.exe, and preserve the
   `Steam Input lease acquired/released` logs for device diagnosis.

   **Every mapped gate writes a per-process startup trace** to
   `%LOCALAPPDATA%\WSGM\steam-input-gate-<steam-pid>.log`; the cold-start launcher writes the exact
   expected path into `wsgm.log`. Each line is emitted only by the worker after loader-lock release.
   `DllMain` and the proxy exports record atomics only, so tracing cannot add file I/O or locks to
   the path being diagnosed. The trace distinguishes loader attach/self-record/pin/worker request,
   vector detection, forwarding begin/end, the number of startup calls that received the bootstrap
   fallback, device rediscovery, and control-pipe readiness. Per-pid names deliberately preserve the
   failed direct-boot trace after a later manual Steam start supplies the control comparison. A
   missing expected file means the gate worker never reached its first post-loader phase (or the
   profile directory was unavailable); a last line at `forwarding initialization started` localizes
   the stall inside that operation; `control pipe listening` proves gate initialization finished.

   **The gate's control pipe carries an explicit DACL (implemented 2026-08-23).** Every instance
   used to be created with a null `SECURITY_ATTRIBUTES`, i.e. with the default named-pipe
   descriptor - measured on the dev box as
   `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<token owner>)(A;;FR;;;WD)(A;;FR;;;AN)`. Those last two ACEs
   let any local principal open the pipe read-only, and read-only is enough to cost something:
   `ConnectNamedPipe` reports `ERROR_PIPE_CONNECTED`, and the server commits one pipe instance plus
   one worker thread blocked in a timeout-free `ReadFile` before a single request byte is read.
   Instances are `PIPE_UNLIMITED_INSTANCES`, so resources could be parked inside `steam.exe` without
   ever speaking the protocol. `server_thread` now builds
   `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<token owner>)` once, before the accept loop, and passes it
   to every `CreateNamedPipeW`. That is the measured default minus the two read-only ACEs, so it is
   a strict subset of what real clients already use: the host opens with
   `FILE_GENERIC_READ | FILE_GENERIC_WRITE`, which no `FR` ACE ever satisfied, and `FA` is granted
   rather than hand-picked `FILE_*` bits because `FILE_GENERIC_WRITE` on a pipe includes
   `FILE_CREATE_PIPE_INSTANCE`. The owner is resolved with `GetTokenInformation(TokenOwner)`, never
   substituted: `CO` is expanded only while an ACE is inherited, so it would match nothing in a
   directly applied DACL, and where owner and user differ - an elevated process whose owner is
   `BUILTIN\Administrators`, the behaviour `WSGM.Launch\AGENTS.md` records from the real device
   failure on 2026-08-12 - the USER form would WIDEN access instead of narrowing it. Functionality
   does not depend on which one the owner resolves to: an elevated client matches `BA`, and an
   unelevated client against an unelevated Steam matches the owner ACE, which is the user there.
   **NOT claimed: that this keeps a medium-IL client away from an ELEVATED Steam's gate.** Whether
   owner and user differ is a machine policy ("default owner for objects created by members of the
   Administrators group") that nobody has verified on the Claw for this pipe, and it is not relied
   on - the default descriptor carried the same owner ACE, so the new DACL is a strict subset of
   today either way and asserts no new security property. Construction FAILS OPEN: if the token
   lookup or the SDDL conversion fails, the pipe is created with a null descriptor exactly as
   before, because a gate that refuses to listen would break controller blocking outright. It
   happens once rather than inside the loop so a failure cannot be charged against
   `PIPE_CREATE_MAX_FAILURES` and retire the server for the life of that `steam.exe` (the image is
   pinned, so `DllMain` never runs again). The startup trace records
   `control pipe descriptor scoped=...; owner=...` so a device log shows which path was taken.
   Deliberately rejected: a HIGH mandatory label, `ImpersonateNamedPipeClient` (per-connection cost
   against a 12-16 ms reply budget), and any thread or instance cap - a cap converts wasted memory
   into lease DENIAL for the legitimate client, which is strictly worse. The same-user case is not a
   threat and was not fixed: driving the protocol needs write access, which only the owner ACE
   granted, and a same-user attacker already holds `TerminateProcess` and `WriteProcessMemory` on
   that process.

   **Owner claims outlive a failed native acquire by design:** `AcquireFor` registers the focused
   surface before it attempts injection, so every deactivate/close path must call `ReleaseFor` even
   when Steam was unavailable and `IsApplied` stayed false. During the overlay-to-Settings handoff,
   Settings registers first and the deferred overlay close removes the overlay owner; abandoning
   either name leaves the controller blocked after the visible surface is gone (device-observed
   2026-08-15). Settings ignores the transient deactivation caused by that still-closing 150 ms
   overlay, then resumes normal focus-based ownership after the overlay acknowledges the handoff;
   releasing during the overlap drops and re-revokes the controller (device-observed 2026-08-12).
   **Live-verified end to end (2026-08-12, dev box, `steam-input-lease.exe`, real Steam Controller
   connected):** acquire took the pad from Steam (`tracked HID handles` 1 → 0,
   `handles revoked by last transition` = 1), Steam had rediscovered it within 700 ms of release (0
   → 1), and an explicit `--rescan` moved Steam's scan counter 14 → 16. **Measured cost:** cold
   inject + acquire
   - release **492 ms** (one-off; the injection dominates), warm acquire + release **41-42 ms** with
     and without a pad, and a single pipe reply (`--status`) **12-16 ms across ten consecutive
     calls**. A review finding claimed every pipe reply re-resolves the recovery layout inline, so
     an acquire can block on a full cross-process address-space sweep — that does NOT reproduce: the
     layout is resolved once on the gate's warm-up and cached, and a sweep would cost hundreds of ms
     per reply, not 14. Do not "fix" it; the proposed fix (answering `payload_capabilities()` only
     from already-resolved state) would additionally make the FIRST acquire report no internal
     recovery, sending `SteamInputBlocker.cs`'s acquire-time gate into a host-side sweep under
     `Sync` — strictly worse. The dev box's Steam is a usable rig for this: the gate stays mapped
     (pinned by design) until Steam restarts.
