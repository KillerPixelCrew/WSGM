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
   first `AcquireLease`, so the payload is entirely inert until WSGM asks for a block. Never move
   hook installation back into `DllMain`/`server_thread`. The observable check: with the proxy
   loaded and no lease taken, `steam.exe` maps only `XInput1_4.dll` (ours plus the real one, pulled
   lazily by a forwarder); `xinput1_3/1_2/1_1/9_1_0` appearing means hooks are installed. Keep the
   native `steam_input_gate.dll` and `steam_input_lease_ffi.dll` beside WSGM.exe, and preserve the
   `Steam Input lease acquired/released` logs for device diagnosis. **That deferral was NOT the
   whole cure. `DllMain` must `record_self_module(instance)` before anything else, and that single
   store is what actually stopped the cold-boot hang (device-verified 2026-08-20: Steam hung on
   EVERY boot with the 1.5.0 proxy deployed, Task Manager required, gone when the shim was disabled;
   0 hangs in 10 boots after).** The mechanism: `proxy::is_self` fails closed while the payload's
   own handle is unknown, and the handle used to be recorded only on the server thread — which
   cannot run until the loader lock is released. During that window `load_system32_module` performed
   a full `LoadLibraryExW` of the real System32 XInput, rejected the result as possibly-us, and
   returned null **without caching**, so `real_module`/`real_export` repeated the entire load on
   every call while SDL probed four user indices and retried. That is a burst of full loader
   transactions on Steam's own startup thread, contending the very lock the server thread needed to
   end the window; which side won was a race, and losing it wedged Steam. **It is a LIVELOCK, not a
   deadlock, and three device observations say so:** it hung on every cold boot yet a warm second
   start worked (the module is in the standby list by then, so each repeated load is fast enough
   that the server thread wins the gap); and holding the stick UP through startup also made it come
   up (device-observed 2026-08-20) — HID traffic satisfies Steam's controller enumeration by a path
   that is not the failing XInput probe, the retry burst stops, and the loader lock frees for long
   enough. Anything that perturbs that loop from outside lets the window close. The `HINSTANCE` the
   loader hands `DllMain` IS the image base, so recording it there costs nothing and adds no loader
   call. Never move the record back to the server thread, and never let the identity guard be the
   only thing deciding whether the real module gets cached. Two latent defects in that path are
   still unfixed and are worth closing if it is ever touched: a rejected `load_system32_module`
   leaks the module reference (no `FreeLibrary`), and a failed resolve is retried unboundedly with
   no cache. For reference, the known-good comparable — **ValvePlug** — pins itself in `DllMain` and
   resolves the real System32 XInput eagerly on its own init thread, i.e. it never has this window
   at all. **Owner claims outlive a failed native acquire by design:** `AcquireFor` registers the
   focused surface before it attempts injection, so every deactivate/close path must call
   `ReleaseFor` even when Steam was unavailable and `IsApplied` stayed false. During the
   overlay-to-Settings handoff, Settings registers first and the deferred overlay close removes the
   overlay owner; abandoning either name leaves the controller blocked after the visible surface is
   gone (device-observed 2026-08-15). Settings ignores the transient deactivation caused by that
   still-closing 150 ms overlay, then resumes normal focus-based ownership after the overlay
   acknowledges the handoff; releasing during the overlap drops and re-revokes the controller
   (device-observed 2026-08-12). **Live-verified end to end (2026-08-12, dev box,
   `steam-input-lease.exe`, real Steam Controller connected):** acquire took the pad from Steam
   (`tracked HID handles` 1 → 0, `handles revoked by last transition` = 1), Steam had rediscovered
   it within 700 ms of release (0 → 1), and an explicit `--rescan` moved Steam's scan counter 14
   → 16. **Measured cost:** cold inject + acquire
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
