# WSGM release-prep review findings

Review of the WSGM tree at `release-prep` (7ab9e4f, identical to `master`) and the four
KillerPixelCrew submodules at their recorded gitlinks.

Severity is about user impact, not effort. Findings marked **[verified]** were confirmed by reading
the code directly during this review. Unmarked findings come from focused sub-reviews with file and
line evidence but were not independently re-read.

Progress: 28 of 70 findings marked done. Each finding carries its status and fixing commit in
brackets; submodule commits are in that submodule.

## Scope and state notes

- The working tree and `release-prep` are clean and equal to `origin/master`, so there is no pull
  request diff. This is a whole-tree release readiness review.
- Submodule pins reviewed: `steam-input-lease` 1dd699b (v0.1.0), `steam-ui-toolkit` 3e08ccd,
  `windows-device-control` 0a8efad, `viiper` 4d2bd52. All four worktrees are clean and match their
  gitlinks.
- `steam-input-lease`, `steam-ui-toolkit` and `windows-device-control` pins are contained in their
  remote default branches. `viiper`'s pin is not (see R2).
- Two candidate findings were investigated and **discarded as incorrect**:
  - `--restore-shell` overwriting recovery snapshots with defaults. `PreserveCorruptFile` only
    copies the bad file aside, so `config.json` still exists, `LoadCurrentDocument` throws and
    `Mutate` aborts, exactly as its contract requires.
  - `Application.DoEvents()` in the Ally X Lab worker being a dead call. `InputSources` registers a
    shell-hook window and power-setting notifications, so it is the worker's only message pump.
- Empty submodule worktrees seen mid-review were an artifact of an interrupted
  `git submodule update --init --recursive` in this session, not a repository defect. Restored.

## 1. Overlay, shell and session (src/WSGM)

**[DONE 90617c4] O1. `src/WSGM/Overlay/OverlayController.cs:955` - high - a failed `Show()` wedges
Quick Access for the rest of the session and leaks the Steam Input lease.** [verified] The `catch`
at 960 releases only the UI-surface claim and rethrows. `_overlay` (939), `_navigation` (940),
`KeyboardService.Handler` (950) and `_gamepad.Start()` (951) stay live, and the lease taken by
`AcquireSteamInputLease()` (856) is released only in `OnOverlayClosed`, which needs a `Closed` event
that never fires for a window that failed to show. Every later `ShowOverlay()` takes the
`_overlay is not null` branch at 858 and reactivates a never-shown window. Scenario: a monitor
hot-unplug or GPU/compositor failure makes one `Show()` throw, and the user cannot open Quick Access
again for the whole game-mode session while Steam Input stays blocked, so the controller stays
missing in Steam.

**[DONE b956a76] O2. `src/WSGM/Shell/ShellSession.cs:2650` - high - one oversized `try` in
`ShutdownAsync` skips display restore and manager disposal.** [verified] Steps 2559-2719 share a
single `try` whose `catch` at 2721 only collects the exception, unlike the individually guarded
steps above it (2552, 2584, 2593). A throw from `_performance.DisposeAsync()` (2653),
`_steamUi.DisposeAsync()` (2636) or `_runningApplications.DisposeAsync()` (2625) therefore skips the
refresh-rate restore (2662), the resolution restore (2675) and disposal of `_audio` (2686),
`_radios` (2692), `_steamStorage` (2701) and `_drives` (2707). This contradicts the invariant stated
in the method's own comment at 2443. Scenario: RTSS dies during exit and the user lands back on the
desktop stuck at the game's 48 Hz and resolution, and on an `Update` shutdown the audio, radio and
drive managers still hold endpoints and device notifications while the installer replaces files.

**O3. `src/WSGM/Program.cs:94` - medium - `--restore-shell` starts Explorer unconditionally.**
[verified] `StartExplorerCore` (`Core/ExplorerControl.cs:29-62`) has no `IsRunningInSession()` guard
even though `StartExplorer`'s own summary at `ExplorerControl.cs:14` says "when it is not already
running", and the sibling crash-loop path does guard it (`Program.cs:352`). Explorer is a
per-session singleton, so a second start opens a folder window instead of becoming the shell.
Scenario: a user runs `--restore-shell` from a normal desktop to disarm the sign-in start and gets a
stray File Explorer window plus the blocking elevation verification at `ExplorerControl.cs:50`.

**O4. `src/WSGM/Program.cs:78` - medium - `--restore-shell` does not coordinate with a live WSGM
shell.** It never takes or checks `Local\WSGM.Shell` (unlike shell mode at 317) and never signals
the resident session. Scenario: a user escapes a wedged session by running
`WSGM.exe --restore-shell` from a second process; Explorer's taskbar comes up while the resident
shell still owns its `Shell_TrayWnd`, which `src/WSGM/Shell/AGENTS.md` forbids, and the resident
session's shell registration and `StartAtSignIn` change underneath it with no notification.

**O5. `src/WSGM/Shell/ShellSession.cs:3317` - medium - `_tabBootSyncCancellation` is read off the UI
thread while the UI thread replaces it.** The boot worker (comment at 3296 confirms it is off the UI
thread) can capture a source that `GameModeEntered` (1115) or `SteamStarted` (1125) cancels
immediately after. `RunTabBootSyncAsync` (1568) is fire-and-forget with no `catch`. Scenario: Steam
finishes starting just as game-mode entry settles, the boot-time injection is cancelled before it
injects, and the WSGM library tabs are simply absent with nothing logged.

**O6. `src/WSGM/Core/Log.cs:335` - medium - `Thread.Sleep(15)` retried up to three times inside the
process-wide `lock (Gate)` (311).** Shell and Settings are separate processes appending to the same
`wsgm.log`, so a sharing violation blocks the calling thread for up to 45 ms while holding the lock
and queues every other thread's lines behind it. Scenario: UI-thread `Log.Info` calls on the
dispatcher, for example `OverlayController.cs:932` during sheet open, stall the Avalonia dispatcher
tens of milliseconds at a time, which is visible overlay stutter; verbose logging scales the
exposure.

**O7. `src/WSGM/Shell/CardVolumeMonitor.cs:547` - low - `Dispose` tears notifications off a
process-wide singleton window it does not own.** `MessageWindow.Create()`
(`MessageWindow.cs:115-120`) returns a shared singleton, and `ApplyCardServices(false)` disposes the
monitor on every desktop trip (2240), deregistering `GUID_DEVINTERFACE_VOLUME` from the shared
window each time. Harmless today because the card monitor is the only consumer; any second
subscriber to `MessageWindow.VolumeChanged` would silently stop receiving notifications.

**O8. `src/WSGM/Core/DevicePackageSlotGate.cs:115` - low - `DisposeAsync` awaits
`_releaseCompleted.Task` with no timeout while the owner thread sits in an unbounded
`_releaseRequested.Wait()` (150), and `_releaseRequested` is never disposed.** Not a live hang today
because the owner's `finally` always signals, but the disposing thread holds
`Global\WSGM.DevicePackageSlot`, and any future path that cannot reach the `Set()` makes every other
WSGM startup refuse with exit code 2 (`Program.cs:257-262`).

## 2. Steam UI toolkit (external/steam-ui-toolkit)

**[DONE toolkit a3b2942] S1.
`external/steam-ui-toolkit/src/SteamUiToolkit/PersistentSteamUiTransport.cs:702` - high - the event
pumps run on the Avalonia UI thread, not a background pump thread.** [verified] `PumpAsync` does
`await foreach (... reader.ReadAllAsync())` with no `ConfigureAwait(false)`, and both pumps start in
the constructor (82, 86). WSGM constructs the transport at `src/WSGM/Shell/ShellSession.cs:740`,
reached from `StartOnUiThread` (509 -> 527), so the pump captures Avalonia's
`SynchronizationContext` and every `NotificationReceived`/`GenerationChanged` handler is posted to
the UI thread, contradicting the assumption in the comment at 543. Scenario: while the UI thread is
busy with overlay animation or a dispatcher-marshalled device call, generation snapshots stop being
delivered, so the patch manager never learns the SharedJSContext document was replaced and Quick
Access rows and library badges stay missing after a Big Picture navigation.
`SteamUiCdpConnection.cs:351` has the identical omission.

**[DONE toolkit c510147] S2.
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiPatchManager.cs:959` - high - teardown can hang
for minutes.** [verified] `DisposeAsync` awaits `_schedulerGate.WaitAsync()` with no timeout and no
token, and the pass it may wait behind is the fire-and-forget one at 373 which calls
`SynchronizeAsync()` at 385 with `CancellationToken.None`. `CancelActivePatchOperations()` cancels
only the running patch; the loop at 962 then removes each remaining registered patch under its own
`OperationTimeout` CTS (966), serially. Scenario: with a connected but unresponsive steamwebhelper
(a Steam update or hung renderer), `SteamUiSessionHost.DisposeAsync` stalls for minutes and leaving
game mode hangs with the shell mid-restore and the user on no desktop.

**[DONE toolkit 451058c] S3. `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiBridge.cs:511` -
high - `DispatchRequestsAsync` also lacks `ConfigureAwait(false)` and starts in the constructor
(244).** `SteamUiBridgeHost` is built inside the `SteamUiSessionHost` constructor
(`SteamUiSessionHost.cs:185`) on the same UI-thread path, so every `RequestReceived` invocation
(537) begins on the Avalonia UI thread. Scenario: a handler that blocks before its first real await,
such as a `DeviceCoordinator` capability write or a `powrprof` call, freezes WSGM's own overlay and
tray; dragging Steam's TDP slider stutters or hangs the UI for the duration of the hardware write.
`SteamUiModuleRuntime` gets this right with `Task.Run` at 60, so the bridge is the outlier.

**[DONE toolkit b014233] S4. `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiBridge.cs:557` -
medium - a pure transport reconnect leaves `_ready == true` while the binding is dead.**
`OnGenerationChanged` invalidates readiness only when `ExecutionContext` or `Document` moved, but a
dropped socket bumps only `Session`/`Target`. `Runtime.addBinding` was registered on the dead
session, so no `Runtime.bindingCalled` can arrive. Scenario: `IsReady` lies, `PublishStateAsync`
keeps succeeding because it is evaluate-based, WSGM keeps the RTSS observation lease alive on that
basis (`SteamUiSessionHost.cs:830`), and every user press in Steam's QAM is silently lost until the
injected side self-times-out; if the bridge patch is disabled at that moment it never recovers.

**[DONE toolkit b68cdb8] S5.
`external/steam-ui-toolkit/src/SteamUiToolkit/PersistentSteamUiTransport.cs:33` - medium - one
`DropOldest` channel carries both idempotent snapshots and non-idempotent RPC frames.**
`RaiseNotificationReceived` (692) discards `TryWrite`'s result. Dropping an old generation snapshot
is harmless; dropping a `Runtime.bindingCalled` frame is a permanently lost user action. Combined
with S1, back-pressure from a busy dispatcher discards user actions instead of delaying them.
Scenario: a TDP or frame-limit change in Steam's QAM does nothing and the control snaps back after 5
s with no log line.

**[DONE toolkit b1e4cad] S6.
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiModuleRuntime.cs:124` - medium - `_inflight` is
keyed by `request.Sequence` alone, but sequences restart at 1 per bridge generation**
(`SteamUiBridgeAuthorizer.Reset` zeroes `_lastRequestSequence`). If a handler from the previous
document has not yet observed cancellation, the new document's request #1 collides, `TryAdd` fails,
and the method returns without answering and without logging. Scenario: navigate out of and back
into Big Picture, press the first WSGM row, and nothing happens. Key by (ContextGeneration,
DocumentGeneration, Sequence).

**[DONE toolkit 6160ef0] S7.
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiCdpConnection.cs:416` - medium - one oversized
or non-JSON frame tears down the whole CDP socket.** `Runtime.enable` is issued for generation
tracking, so all of Steam's console chatter arrives here; a `params` payload over the 1 MiB bound
throws `InvalidDataException` out of `ProcessMessage`, which `ReadLoopAsync` (301, catch at 307)
treats as terminal, even though `OnNotification` would have dropped that frame unread at 549.
Scenario: a game or Steam component logs a large object to the console, the socket dies, the
1/4/16/30 s backoff starts, the Session generation bumps and every patch's `Verified` state is
invalidated, so Quick Access rows, badges and the glyph stylesheet all vanish and slowly return. The
unguarded `JsonDocument.Parse` at 366 gives any non-JSON frame the same power.

**[DONE toolkit 457eacf] S8.
`external/steam-ui-toolkit/src/SteamUiToolkit/PersistentSteamUiTransport.cs:344` - medium -
`ReconnectLoopAsync` resets `attempt = 0` on any successful connect with no minimum-uptime
requirement.** A 50 ms session resets the backoff like a ten-minute one. Scenario: a CEF that
accepts the WebSocket and immediately drops it, normal during a Steam update or crash-restart loop,
pins the loop at a fixed 1 s retry indefinitely, each iteration re-running `GetExtendedTcpTable`,
two loopback HTTP probes and a handshake per role; on a handheld that is a continuous 1 Hz CPU and
battery cost plus a Session-generation bump per cycle that re-runs the full apply/verify pass over
every patch.

**[DONE toolkit c90e389] S9.
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiPatchManager.cs:232` - medium - the manager
subscribes to transport events in its constructor, then mutates unsynchronized dictionaries in
`Register`.** `_patches.Add` (254) and `_resourceGates.TryAdd` (257) run with no lock while
`OnGenerationChanged` (826) and `SynchronizeAsync` (416) enumerate `_patches.Values` from the pump
thread. Scenario: Steam raises `Page.frameNavigated` while the host is still registering patches at
startup; the enumeration throws `InvalidOperationException`, `PumpAsync` swallows it (715-718), and
that generation change is lost so patches keep a stale `Verified` snapshot and never reapply.

**[DONE toolkit 9d47829] S10.
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiTransportSession.cs:51` - medium - the static
`_enabled` is written before the lock and can be left permanently inconsistent.** `SetEnabled`
writes the field then takes `Gate` and calls `_transport?.SetEnabled(...)` (54), which throws
`ObjectDisposedException` if the attached transport was disposed without `Detach`. Scenario:
shutdown disposes the transport, a settings toggle then calls `SetEnabled(false)`, the exception
propagates while `_enabled` has already flipped, and every later `EvaluateAsync` reports "Steam CEF
integration disabled in settings" for the rest of the process; `Attach` only copies `_enabled` (76)
and cannot restore it.

## 3. VIIPER (external/viiper)

ABI note, stated as a cleared negative: all 18 exports in `libviiper.h:104-122` match the `//export`
set in `clib/main.go`/`clib/fastpath.go` one-for-one, and every declaration in
`src/WSGM/Interop/NativeViiper.cs:26-74` is correct in name, arity, type and convention. String
ownership is correct (`viiper_last_error` malloc paired with `viiper_free_string`), no structs cross
the boundary, and device IDs start at 1 so WSGM's `_deviceId == 0` sentinel is sound. No ABI defect.

**[DONE viiper 4601137] V1. `external/viiper/clib/main.go:345` - high - `viiper_init` blocks forever
when the listener fails to bind.** [verified] `ListenAndServe` returns the bind error at
`internal/server/usb/server.go:318-321`, before `close(s.ready)` at 324, and the goroutine only logs
it (`clib/main.go:338-342`). The unbounded `<-server.Ready()` at 345 therefore never completes,
while holding `mu`. Scenario: `127.0.0.1:0` fails to bind (ephemeral port exhaustion, a filtering
LSP, a locked-down policy); `NativeViiper.Init` at `src/WSGM/Input/ViiperControllerBackend.cs:361`
never returns, so `DiscoverAsync` hangs holding `_gate` with its token unusable, the careful
`DllNotFound`/`BadImageFormat` fail-closed handling is bypassed, and the device coordinator and
shutdown deadlock instead of reporting "backend unavailable".

**[PARTIAL viiper e7e220a] V2. `external/viiper/clib/main.go:612` - high - `viiper_device_attach`
uses `context.Background()` for both the IOCTL and the `usbip.exe` fallback while holding the global
`mu`.** `attachViaCommand` (`internal/server/api/autoattach_windows.go:204-214`) runs
`exec.CommandContext(ctx, "usbip", ...).CombinedOutput()` with a non-cancellable context. Scenario:
the usbip-win2 driver is wedged or a `usbip.exe` on PATH hangs, so `viiper_device_attach` never
returns and `mu` is held forever, blocking `viiper_device_remove`, `viiper_device_set_input` and
`viiper_shutdown`. The managed call is made under the backend's semaphore
(`ViiperControllerBackend.cs:160`) and a blocked P/Invoke ignores cancellation, so `DisposeAsync`
(312) waits on `_gate` forever and WSGM has to be killed.

**V3. `external/viiper/clib/main.go:373` - medium - `viiper_shutdown` does not drain in-flight
feedback callbacks, unlike `viiper_device_remove` which waits `feedback.inflight.Wait()`
(649-655).** A callback past the `inflight.Add(1)` point (1051) is still executing when shutdown
returns. `ViiperControllerBackend.cs:339-341` then frees the `GCHandle` the callback's `userData`
points at, relying on a comment that claims shutdown joins the native server lifetime, which the
library does not guarantee. Scenario: the legal sequence init, device_add, set_feedback_callback,
shutdown leaves `OnFeedback` calling `GCHandle.FromIntPtr` on a freed handle, an access violation
rather than a catchable exception. Related in the same file: `setError` is called after
`mu.Unlock()` at 633 and 643, racing the locked read in `viiper_last_error`.

**V4. `external/viiper/clib/main.go:613` - medium - every IOCTL attach error is treated as "nothing
happened".** `attachViaIOCTL` can fail after the driver already plugged the device in
(`autoattach_windows.go:189-191` rejects a successful `DeviceIoControl` whose `PortOutput` is <= 0).
Line 615 then re-attaches via `usbip.exe`. Scenario: two usbip attachments of one device, so two
identical controllers appear in Steam, and only one port is recorded (620-622) so
`viiper_device_remove` detaches one and the other stays enumerated after WSGM exits until a manual
`usbip detach` or reboot. This is the duplicate the fork's own comment at 491-504 removed from
`device_add`.

**V5. `external/viiper/clib/fastpath.go:69` - medium - the fast-path handle slice is only ever
appended to.** Nothing removes an entry on `viiper_device_remove`; only `viiper_shutdown` clears it
(39-43), and a retained `*deviceInfo` pins the whole device object (`clib/main.go:82`). Scenario:
WSGM opens a handle per target creation (`ViiperControllerBackend.cs:150`), so every controller
handoff permanently leaks a device object for the process lifetime. Second half of the same defect:
a stale handle still validates, so `viiper_device_set_input_fast` returns success for a removed
device and input silently goes nowhere; WSGM escapes this only because it zeroes `_fastHandle` in
`RemoveDeviceUnderGate` (667).

**V6. `external/viiper/internal/server/usb/server.go:334` - medium - the accept loop `continue`s on
any non-`ErrClosed` error with no backoff and no failure counter.** Scenario: handle or socket
exhaustion inside WSGM.exe makes `Accept` fail persistently, producing a tight infinite loop that
pins a CPU core and emits one `slog.Error` per iteration into `viiper_go_debug.log`, which
`clib/main.go:317-318` opens next to the executable with an ignored error, growing without bound
inside the install directory until the volume fills.

**V7. No `recover()` anywhere in the repository - medium - any Go panic aborts WSGM.exe with no
managed exception**, including all four server goroutines and every exported entry point;
`SafeNative` (`ViiperControllerBackend.cs:708-719`) cannot catch it. The reachable decode paths were
checked and are bounds-clean (`xbox360`, `dualshock4`, `steamdeck` `UnmarshalBinary` all
length-check up front). The only unguarded panic primitives are the `unsafe.Slice` calls at
`clib/fastpath.go:89` and `clib/main.go:733`, which WSGM does not currently trigger.

## 4. Steam Input lease (external/steam-input-lease)

**[DONE lease 79fc8e2] L1. `external/steam-input-lease/crates/steam-input-lease/src/lib.rs:548` -
high - a 20 ms probe decides "no resident payload" and then injects, with no check for an
already-mapped gate.** `inject_payload` never tests `remote_module(pid, ".../gate/XInput1_4.dll")`,
and the gate's server has no-pipe windows longer than 20 ms: on a transient creation failure it
sleeps 250 ms with no instance existing (`crates/steam-input-gate/src/lib.rs:2515`). Scenario: a
game's launch options still carry `--inject` from when Management was off, so a proxy gate is
resident as `XInput1_4.dll`; WSGM.Launch probes during that gap and injects `steam_input_gate.dll`,
so Steam maps two distinct images of the same code. Both start servers on the same pipe name
(2487-2497 uses `PIPE_UNLIMITED_INSTANCES` with no `FILE_FLAG_FIRST_PIPE_INSTANCE`), each with its
own `LEASE_COUNT` and its own MinHook instance re-detouring the same `ntdll` exports, so lease
counts disagree and a release served by one image leaves the other's detours blocking controllers.

**[DONE lease 3a414aa] L2. `external/steam-input-lease/crates/steam-input-gate/src/lib.rs:1975` -
high - every reply, including `QueryStatus`, triggers a full address-space sweep.** `response()`
calls `payload_capabilities()` (1757) which calls `resolve_discovery_deadline(false)` ->
`find_hid_thread` (1604): a `VirtualQuery`/`ReadProcessMemory` sweep of Steam's entire private
address space in 1 MiB chunks plus up to a 1.5 s live-object election, on the pipe worker before the
response is written. The attempt budget of 3 resets on every acquire and re-blocking transition
(2012-2013, 2075-2076). The host waits in a blocking `ReadFile` with no timeout
(`crates/steam-input-lease/src/lib.rs:1523`) and `Client::status()`'s 500 ms budget covers only pipe
connect (398). Scenario: on a Steam build whose `CHIDIOThread` is not resolvable,
`SteamInputClient.GetStatus()` blocks for multiple seconds per call on the calling thread while
`SteamInputBlocker` holds its `Sync` lock across acquire.

**L3. `external/steam-input-lease/crates/steam-input-lease-ffi/src/lib.rs:446` - medium -
`sil_lease_release` returns `SIL_ERROR` on a null `outcome` before `Box::from_raw`, so the lease is
not consumed.** Both `include/steam_input_lease.h:184-187` ("The lease is consumed even if this
function returns an error") and the function's own doc at 434-437 promise the opposite. Scenario: a
consumer of the public header passes `NULL` to ignore the handshake result; the `SilLease` box and
its pipe handle leak, and because the pipe never closes the block lease is held until the host
process exits, so Steam's controllers stay revoked with no way to release them.

**L4. `external/steam-input-lease/crates/steam-input-lease/src/lib.rs:621` - medium -
`Lease::release` runs a 5-10 s rescan inline when the payload lacks
`CAPABILITY_INTERNAL_RECOVERY`.** Two full resolves (an up-to-512 MB module snapshot at 945 and a
whole-process candidate scan at 1002), each followed by `thread::sleep(2200 ms)` (1289). The gate
drops that capability as soon as its resolver budget is spent (`steam-input-gate/src/lib.rs:1616`),
so this is the normal path on an unrecognised Steam build. Scenario: closing a WSGM overlay calls
`Release()` from `SteamInputBlocker.ReleaseCore`, which holds `Sync` for the whole duration, so the
next surface open blocks behind a ~10 s native scan.

**L5. `external/steam-input-lease/crates/steam-input-gate/src/lib.rs:1675` - medium - bytes in a
short read are never examined.** The scan correctly matches only `&buffer[..transferred]`, but then
advances `chunk_start` from the requested `chunk_end`, skipping `[transferred, chunk_end)`. The host
repeats the pattern at `crates/steam-input-lease/src/lib.rs:1051`. Scenario: a partially readable
region (a page decommitted mid-sweep) holds the live `CHIDIOThread`; it is skipped, candidates is
empty, and recovery fails closed, so blocking is lifted but Steam is never told to rediscover and
the pad stays missing.

**L6. `external/steam-input-lease/bindings/SteamInterop.Net/PlatformSupport.cs:3` - medium - a
vendored assembly-level attribute is applied to WSGM's own assembly.** [verified] The file contains
nothing but `[assembly: SupportedOSPlatform("windows10.0.17763")]`, and `src/WSGM/WSGM.csproj:49`
globs `bindings/SteamInterop.Net/*.cs` directly into WSGM (as does `WSGM.Launch.csproj:29`). WSGM
targets `net10.0-windows10.0.19041.0` while the binding project targets
`net8.0-windows10.0.17763.0`, so the whole WSGM assembly declares a 17763 floor and CA1416 will flag
its 19041-only calls, including the WinRT radio and Bluetooth paths that require 19041. Any new file
added to the binding is also silently compiled into the app. The rest of the glob was checked and is
clean: no other assembly attributes, no `Main`, no `InternalsVisibleTo`, no colliding type names,
and the glob is non-recursive so generated `obj/` sources are not pulled in.

**L7. `external/steam-input-lease/bindings/SteamInterop.Net/SteamInputClient.cs:40` - low -
`ConnectTimeout` of zero silently becomes the native 10 s default.**
`ConnectTimeoutMilliseconds = checked((uint)options.ConnectTimeout.TotalMilliseconds)` passes 0
through, and native treats 0 as "use the default" (`include/steam_input_lease.h:55`), which the
binding's own docs (`Models.cs:24`) never mention. Scenario: WSGM sets
`ConnectTimeout = TimeSpan.Zero` for a deliberately non-blocking availability probe and gets a
10-second blocking P/Invoke instead, on the UI thread if that is where the probe ran.
Sub-millisecond values truncate the same way.

## 5. Windows device control (external/windows-device-control)

**[DONE wdc 364bb1d] W1.
`external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WlanNative.cs:137` - high -
`WlanAvailableNetwork` is missing its trailing `DWORD dwReserved`, so every network after the first
is decoded at the wrong offset.** [verified] The managed struct ends at `internal uint Flags;` while
the native record ends `dwFlags; dwReserved;`, making it 628 bytes against `Marshal.SizeOf` = 624.
`ReadWlanList` takes `var stride = Marshal.SizeOf<TRecord>()` (37) and walks
`start + index * stride` (41), so record n is read 4n bytes early. Scenario: on a handheld seeing
five access points, `ListWifiNetworks` (`WindowsRadio.Wifi.cs:541`) returns the first SSID correctly
and then garbage, with SSID bytes sliced from the previous record's `dwFlags`, `SignalQuality` read
from `bSecurityEnabled` and `Connectable` read from a phy-type word, so the Wi-Fi list shows
mojibake names with wrong signal bars and `ChooseInterface` can mark a real network ambiguous and
refuse to join it.

**[DONE wdc 97654d6] W2.
`external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:74` - high -
`StopWifiWatch` unregisters the WLAN callback while holding the lock the callback itself takes, then
drops the delegate's last reference.** It takes `WifiWatchLock` (65) and calls
`watch?.Registration.Dispose()` (75) inside it; `Dispose` calls `WlanRegisterNotification(...)`
(247) then `_client.Dispose()` (249), while `OnWifiNotification` acquires the same lock as its first
act (82). `StopWifiWatchCore` has already set `_wifiWatch = null` (73), so nothing roots `_callback`
or its function-pointer stub (226) after `Dispose` returns; `GC.KeepAlive` at 250 covers only up to
that point. Scenario: a scan-complete notification is being delivered on the WLAN service thread
while the shell toggles Wi-Fi or shuts down, and radio teardown hangs or takes an access violation
instead of stopping cleanly.

**[DONE wdc 4cd2d0b] W3.
`external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Native.cs:81` - medium -
`Marshal.FinalReleaseComObject` on shared RCWs breaks the volume API's "never throws" contract.**
`WithDefaultVolume`'s `finally` (`CoreAudio.cs:460-461`) applies it to the `IMMDevice` and
`IAudioEndpointVolume` obtained from the process-wide cached enumerator (23-35), and the runtime
hands out one RCW per COM identity. Scenario: a hardware volume key going through `ApplyCommand`
while the QAM polls `GetVolume` share the RCW; the first to finish severs it and the second throws
`InvalidComObjectException`, which is not a `COMException` so the `catch (COMException)` at
`CoreAudio.cs:453` misses it and it escapes into the volume-button handler.

**[DONE wdc e1a967f] W4.
`external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:152` - medium -
`ConnectionVerdict.OnNotification` dereferences the notification pointer without the null check its
sibling has** (`OnWifiNotification` guards `data == 0` at 84). WLAN may deliver a notification with
a null `pData`; `PtrToStructure` then throws `ArgumentNullException`, which the blanket `catch` at
177 swallows, so `_ready` is never set. Scenario: the connect path (`WindowsRadio.Wifi.cs:268`)
waits the full 25 s `ConnectTimeout` and reports failure for a join that actually succeeded, so the
user sees "connection did not complete" on a network they are on.

**W5. `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:125` - low - the
same Win32 surfaces are implemented twice in one process.** `DisplayModes` declares
`EnumDisplaySettingsExW`/`ChangeDisplaySettingsExW` with its own explicit-layout `NativeMode`
(114-133) while `src/WSGM/Interop/NativeDisplay.cs:134-139` declares the identical pair with its own
`DevMode`, used by `src/WSGM/Core/DisplayProfiles.cs:98,226,349`: two independent mode-change paths,
two DEVMODE layouts, no shared gate. Likewise `WindowsStorage.DiskNumberFor`/`DiskNumber`
(`WindowsStorage.cs:110,138`) and `src/WSGM/Interop/NativeStorage.cs:53,246` both issue
`IOCTL_STORAGE_GET_DEVICE_NUMBER`, and WDC's `Interop.cs:54-64` duplicates
`src/WSGM/Interop/Kernel32.cs:21`. WSGM already consumes this library in 52 files, so the app-side
copies are redundant.

**[DONE wdc d579f32] W6.
`external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Native.cs:102` - high -
`PropVariant` is 16 bytes but native `PROPVARIANT` is 24 on x64, so every audio property read
overwrites 8 bytes of the caller's stack frame.** [verified] The struct is `LayoutKind.Explicit`
with no `Size`, carrying only `[FieldOffset(0)] ushort` and `[FieldOffset(8)] nint`. Native
`PROPVARIANT` is an 8-byte vt/reserved header plus a 16-byte union (the `DECIMAL` and counted-array
arms are 16 wide). The struct is blittable, so `IPropertyStore.GetValue` (215) writes 24 bytes into
the 16-byte stack local created at `ReadStringProperty:62`, and `PropVariantClear` (86) then zeroes
24 bytes over it. Scenario: every `CoreAudio.ListEndpoints` and `ListBluetoothAudioContainers` call
scribbles past `value` into the adjacent `store`/`key` slots in that frame, so the audio device list
returns a corrupted friendly name, or the process takes an access violation on `Release(store)` in
the same `finally`.

**[DONE wdc 6618280] W7.
`external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.Profiles.cs:47` - high - a
failed display-profile apply rolls back the live desktop but leaves the broken topology persisted.**
[verified] The apply at 36 uses `SdcApply | SaveToDatabase`, so the unconfirmed configuration is
written into the CCD database; the rollback at 47 uses `SdcApply` alone and therefore changes only
the live desktop. `DisplayLayouts.cs:304-305` does include `SaveToDatabase` on its rollback, so the
two paths disagree. Scenario: a user applies a saved profile on a dock, confirmation fails, the
screen visibly recovers, and then at the next sign-in or monitor hotplug Windows replays the
persisted broken topology, for example external-only on a panel that is no longer attached.

**[DONE wdc 4cd2d0b] W8.
`external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Bluetooth.cs:126` - medium -
second call site of the shared-RCW defect in W3.** `ForEachEndpoint` calls `Release(endpoint)` on
every enumerated `IMMDevice`, and RCWs are keyed by native IUnknown identity, so it finalizes the
same managed object another thread holds as its default endpoint. Scenario: the user presses a
volume key while the radio/audio panel refresh runs `ListBluetoothAudioContainers`; the enumeration
severs the default endpoint's RCW and the volume call throws `InvalidComObjectException`, which
escapes the `catch (COMException)` at `CoreAudio.cs:453` and leaves the library throwing where it
documents an HRESULT return.

**[DONE wdc a9794a1] W9.
`external/windows-device-control/src/WindowsDeviceControl/WaveOutFeedback.cs:119` - medium -
`Dispose` frees buffers that winmm may still own.** It discards the MMRESULT of `WaveOutReset`
(108), `WaveOutUnprepareHeader` (111) and `WaveOutClose` (114), then unconditionally `FreeHGlobal`s
the header and the PCM sample block. Scenario: a Bluetooth or USB headset disconnects while the
volume feedback tone is queued, so reset/unprepare/close return `MMSYSERR_NODRIVER` or
`WAVERR_STILLPLAYING` and the driver still holds those buffers; freeing them hands the driver freed
heap, producing heap corruption or a crash shortly after a headset disconnect during volume changes.

**[DONE wdc 1aa4267] W10.
`external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:98` - medium - `Read`
throws instead of returning null, defeating the caller's documented optional query.** `Find` ->
`DisplayTopology.CaptureActive()` -> `ReadTarget` throws `Win32Exception` when any active path's
`DisplayConfigGetDeviceInfo` target-name query fails, contradicting the "returns null when the
target is absent, ambiguous or unreadable" contract at 33.
`src/WSGM/Settings/SettingsViewModel.cs:73` calls
`DisplayModes.Read(target)?.Supported ?? DisplayEdid.ReadModes(target)` with no guard, under a
comment promising every query is optional. Scenario: one unreadable target such as an indirect,
Miracast or virtual display aborts the whole display-facts read, the EDID fallback never runs, and
the Settings display page shows no modes at all. `DisplayLayouts.Observe` (`DisplayLayouts.cs:145`)
catches per target for exactly this reason.

**[DONE wdc 5ccba70] W11.
`external/windows-device-control/src/WindowsDeviceControl/CoreAudio.cs:370` - medium -
`ReadDefaultEndpointId` hardcodes `DataFlow.Render` while `SetDefaultEndpoint` accepts capture ids
too.** Scenario: the user selects a microphone and the third role assignment fails (an
`IPolicyConfig` refusal or the device is removed); the rollback at 419 restores the previous
_render_ endpoint id, re-asserting the speakers while doing nothing about the capture roles already
changed. The API reports `RollbackHResult == 0` while leaving the capture default split across two
devices, the exact state the remarks at 277 claim to prevent.

**[DONE wdc efff362] W12.
`external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:234` - medium -
consumer callbacks are invoked unguarded on a WinRT `DeviceWatcher` thread, so a handler exception
terminates the process.** `watch.Callback(...)` at 234, 253, 271, 293 and 304 has no `try`/`catch`,
unlike the Wi-Fi path which swallows and documents it (`WindowsRadio.WifiWatch.cs:105`, doc at 25).
Reachable in WSGM: `RadioManager.cs:671` posts to `Dispatcher.UIThread` inside that callback, which
throws once the Avalonia dispatcher is shutting down. Scenario: a Bluetooth arrival landing during
application exit crashes WSGM instead of being dropped. The callback is also raised while holding
`BluetoothWatchLock`, the same lock `StopBluetoothWatch` takes, which is the W2 shape again.

**[DONE wdc 2501195] W13.
`external/windows-device-control/src/WindowsDeviceControl/ModernStandby.cs:174` - low -
`DevicePowerOpen`/`DevicePowerClose` wrap enumerations of a process-global list with no
synchronization.** `RestoreWakeDevices` (273) enumerates once and again per `TrySetWakeArmed` (224),
so the window is wide. Scenario: a concurrent `ModernStandbyDiagnostics.Read` closes the list under
the other thread, and `DevicePowerEnumDevices` either reports `ERROR_NO_MORE_ITEMS` at index 0, so
`CaptureWakeDevices` records an empty snapshot and the user's wake arming is silently never
restored, or fails with another code and throws mid-restore, leaving arming half applied.

## 6. Release, build and packaging

**[DONE be6f746] R1. `.github/workflows/ci.yml:71` - high - VIIPER is never built in CI, so a broken
pin is first discovered after a public tag exists.** CI runs only `eng/verify.ps1`, which builds
Steam Input Lease (`eng/verify.ps1:89`) but never VIIPER; `eng/build-viiper.ps1` is invoked only
from `build.ps1:45`. VIIPER is also invisible to the solution: `WSGM.slnx` has no viiper project and
`src/WSGM/WSGM.csproj:81` globs `Native\Viiper\*`, which silently yields zero items when the staging
directory is absent. Scenario: a viiper pin that cannot compile leaves CI fully green and fails
inside the tag-triggered release job at `.github/workflows/release.yml:80`.

**R2. `.gitmodules` / `external/viiper` gitlink - medium - the pinned VIIPER commit is not on the
remote's default branch and `git submodule update --init --recursive` fails on a fresh clone.**
[verified] The pin 4d2bd52 lives on `refs/heads/wsgm`; the remote default HEAD is
`viiper-controller` (024aef3a). `.gitmodules` records no `branch=` key for any submodule. Observed
directly in this review: `git submodule update --init --recursive` aborts with
`fatal: Unable to find current revision in submodule path 'external/viiper'`, and only an explicit
`git fetch origin wsgm` recovers it. Because the update aborts partway, it can also leave earlier
submodules with populated indexes and empty worktrees. Either record the branch in `.gitmodules` or
merge the pin into the default branch.

**R3. `src/WSGM/app.manifest:8` - medium - the SxS assembly identity says `1.0.0.0` while
`src/WSGM/WSGM.csproj:13` is `2.0.0`.** `build.ps1` stamps neither: it passes `/p:Version` only to
Launch and LogonService (69, 75) and `/DAppVersion` to Inno (105). Only
`.github/workflows/release.yml:68` rewrites the manifest. Scenario: a locally produced
`publish/WSGM-Setup-2.0.0.exe` ships a `WSGM.exe` whose manifest identity claims 1.0.0.0, so
installer name, file metadata and manifest identity disagree in exactly the artifact a maintainer
hand-builds.

**R4. `installer/WSGM.iss:13` - medium - `#define AppVersion "2.0.0"` is a second hardcoded version
with nothing enforcing it.** `eng/verify.ps1` has no check comparing it to the csproj, and
`release.yml:58` stamps only `WSGM.csproj` and `app.manifest`. Scenario: after a `v2.1.0` tag the
fallback still says 2.0.0, so a direct ISCC run emits `WSGM-Setup-2.0.0.exe` (39) containing 2.1.0
binaries and registers `AppVersion` 2.0.0 (27) in Add/Remove Programs.

**R5. `eng/assert-component-staging.ps1:58` - medium - the developer-path leak check cannot match
this repository's own path shape, and covers only part of the payload.** `$localPathPattern`
alternates `(?:Users|Coding|Repos?|Source|Worktrees?)[\\/]`, which requires `Source\`, but the real
root is `E:\SourceCode\`, so it never matches. The leak, secret and forbidden-extension scan also
runs only inside `Packages` (loop at 61, walk at 78); `App` and `Tools` get only `Assert-NoLinks`
(40-42) even though `installer/WSGM.iss:143` ships the whole `Tools` tree with `recursesubdirs`.
Scenario: an absolute developer path or a stray `.pdb` under `App`/`Tools` ships to users unflagged.

**R6. `src/WSGM/WSGM.csproj:81` - medium - `Native\Viiper\*` carries no `SkipNativeArtifacts`
condition**, unlike the SteamInputLease items at 57, 66 and 71, and the staging directory is cleaned
only by `eng/build-viiper.ps1:74`, which neither `eng/verify.ps1` nor `eng/dev-deploy.ps1` calls.
Scenario: a `libviiper.dll` left over from an earlier build is copied into every later publish, so a
dev deploy can run a native library older than the pinned submodule with no warning; `build.ps1:80`
only proves the file exists, never that it matches the pin.

**R7. `eng/check-no-live-data-paths.ps1:37` - medium - the live-data guard scans only `tests`.**
`$scanned = @("tests")` while the synopsis at 3 and 22-23 claims it covers "test, fixture, probe, or
tooling sources". `tools/AllyXLab/Session.cs:16` resolves `SpecialFolder.LocalApplicationData` and
writes `%LOCALAPPDATA%\WSGM.AllyXLab`, and `tools/UwpLaunchSpike/Options.cs:313` does the same for a
transcript; neither is scanned, yet `eng/verify.ps1:78` reports the guarantee as met.

**R8. `eng/build-viiper.ps1:13` - medium - nothing asserts the submodule is at its recorded gitlink
or clean before a release build.** The script builds "the submodule as it is checked out" and only
checks presence (37); `build.ps1` never verifies submodule cleanliness before publishing. Scenario:
a release installer is compiled from uncommitted local submodule edits and nothing in the artifact
or logs records that the shipped `libviiper.dll` corresponds to no pushed commit.

**R9. `eng/build-viiper.ps1:80` - medium - the staged VIIPER artifact cannot be traced back to its
commit.** `go build -buildmode=c-shared` runs with no `-trimpath`, no `-ldflags` and no version or
commit stamping, so the DLL carries no commit, no version resource and absolute build-machine paths,
while the repo's own `Makefile:42-43` stamps `VERSION`/`COMMIT` for the other library. The toolchain
is unpinned (`go.mod:3` has `go 1.26.2` and no `toolchain` directive; line 41 only checks that some
`go` exists, and 48-63 link with the first `gcc.exe` found under the winget tree). Lines 86-87 also
delete the header `go build` just generated and replace it with the tracked `libviiper.h`, which was
last modified 2026-07-31 while `clib/` changed through 2026-09-11. They agree at this commit, but
nothing enforces that, so a future export change would stage a header that lies about the ABI.

**R10. `installer/WSGM.iss:169` - low - the HidHide install step's exit code is never inspected.**
It uses `skipifdoesntexist` and Inno's `[Run]` ignores exit codes by default, unlike the USB/IP step
at 168 which reports through `PrepareUsbipInstallOutcome`/`ReportUsbipInstallOutcome` and a status
INI. Scenario: setup completes successfully with physical-controller isolation missing, surfacing
later as unexplained duplicate controllers rather than an install-time diagnostic.

**R11. `eng/stage-device-components.ps1:103` - low - the `$LASTEXITCODE` guard is inert.** It is
evaluated after `& $pluginPack @packArguments`, a PowerShell script invocation that does not set
`$LASTEXITCODE`, so the value read is stale from the preceding native `dotnet publish` inside
`Publish-DeviceLab` (`eng/device-lab-publish.ps1:52`). `eng/dev-deploy.ps1:192` repeats the pattern.
The guard works today only because `pack-device.ps1` happens to `throw`; any non-throwing failure
would let staging continue and let `assert-component-staging.ps1` judge a partially built package.

## 7. Ally X Lab tool (tools/AllyXLab)

This is the newest code in the tree (the last three commits) and is an attended developer tool, not
shipped product. It does write to live system state, which is why the items below matter.

**T1. `tools/AllyXLab/MainForm.Access.cs:32` - medium-high - the conflict step freezes the UI for up
to 8 seconds per manager, and the status text it sets provably never paints.** [verified]
`DeviceCheckAsync` runs on the UI thread (a Button.Click handler, resumed on the WinForms
`SynchronizationContext`). Line 32 sets `_status.Text = "Asking ... to close..."` and line 33 then
calls `Conflicts.Close`, which blocks in `process.WaitForExit(8000)` (`Conflicts.cs:101`). The paint
message cannot be pumped while the UI thread blocks. Scenario: with four closable Armoury Crate
processes the tester gets roughly 32 seconds of an apparently hung, blank window, the exact opposite
of the intended reassurance. `Conflicts.Running()` also enumerates every process on the UI thread.

**T2. `tools/AllyXLab/MainForm.Access.cs:47` - medium - the HidHide allowed-application list is a
read-modify-write across an unbounded user prompt, so a concurrent edit is silently dropped.**
[verified] `Read` happens at 47, the tester is then asked for consent (51-54) with no time bound,
and `TryAllow` (65) writes `state.Applications` captured before the prompt. The tool's own conflict
table lists `HidHideClient` as software that "edits the device hiding list while this tool reads it"
(`Conflicts.cs:37`). Scenario: the tester opens the HidHide Configuration Client during the prompt
and adds an application; the tool then writes its stale list plus itself, removing that entry, and
`Restore` later writes the even older list back.

**T3. `tools/AllyXLab/MainForm.Access.cs:50` - medium - under HidHide inverse mode the offered
remedy is semantically wrong, yet it is applied and reported as applied.** [verified] The code
detects `Inverse` and warns the tester that the list "means the opposite of usual", then performs
the same "add self to the list" write. In inverse mode that write does not grant this tool
visibility. Scenario: the tester consents, the controller stays hidden or more devices become
hidden, and the session log records the allowance as made.

**T4. `tools/AllyXLab/MainForm.Access.cs:65` - medium - the HidHide mutation is the only live-system
write in the tool with no durable recovery record.** [verified] Every other mutation goes through
`Checkpoint?.Invoke(...)` and requires a durable parent acknowledgement before the write
(`Worker.cs:135`, `Program.cs:92-99`). `TryAllow` writes immediately and records only a session
Observation. `RestoreHidHide` is reachable on normal and exception paths via `StartAsync`'s
`finally` -> `Finish()` (`MainForm.cs:279-283`, 399), but not if the process is killed or crashes.
Scenario: the tool adds itself, the tester force-closes it, and their HidHide allowed list
permanently contains a path to a portable exe they then delete, with nothing telling them.

**T5. `tools/AllyXLab/Motors.cs:83` - medium - motor routes are identified by live collection index,
so a disconnect between discovery and open drives the wrong controller.** [verified] `Discover`
numbers `wgi:` routes by position in `Gamepad.Gamepads` (49-51) and `xinput:` by slot; `Open`
re-reads the collection and only checks `index < Count` (83). Scenario: two pads are discovered, the
first is unplugged, the tester selects `wgi:1`, and the vibration write goes to a different device
than the one described in the prompt. `int.TryParse` also accepts a negative index, which would
throw rather than be refused.

**T6. `tools/AllyXLab/Worker.cs:52` - low - the tool version is hardcoded in five places.**
[verified] `0.3.1` appears in `Worker.cs:52` (the provenance record), `Session.cs:34`,
`MainForm.cs:27`, `WSGM.AllyXLab.csproj:19` and `Downloads/BUILD.json:44`, even though
`SourceSnapshot` assembly metadata is already plumbed through `Session.cs:31`. Scenario: bumping the
csproj alone silently mislabels the evidence ZIP and the provenance log that the whole tool exists
to produce.

**T7. `tools/AllyXLab/Downloads/BUILD.json` - low - the provenance manifest is unverifiable on a
Windows checkout and nothing validates it.** [verified] The committed exe's hash matches
`SHA256.txt` and `BUILD.json` exactly (confirmed). The 33 `sourceFiles` hashes, however, are
computed over LF-normalized content, so on this CRLF checkout every single one appears to drift;
they reproduce exactly once newlines are normalized. Unlike the Steam UI assets, which
`.gitattributes` pins to `eol=lf` precisely "so the hash is identical on autocrlf and non-autocrlf
checkouts", these paths have no such pin and no documented normalization rule, and no gate anywhere
validates `BUILD.json`. Scenario: a reviewer or tester tries to verify the manifest, finds total
drift, and cannot tell a real stale binary from a line-ending artifact.

**T8. `tools/AllyXLab/HidHideAccess.cs:116` - low - `NormalizePath` discards the volume, so paths on
different volumes compare equal.** [verified] `C:\Tools\AllyXLab.exe` and
`\Device\HarddiskVolume3\Tools\AllyXLab.exe` both reduce to `\Tools\AllyXLab.exe`. This is
deliberate and test-locked (`tests/WSGM.AllyXLab.Tests/SafetyTests.cs:122-126`) to match one file
written in two notations, but it is unsound across volumes. Scenario: HidHide already lists
`C:\Tools\AllyXLab.exe` from an earlier run and the tester now runs the portable exe from
`D:\Tools\AllyXLab.exe`; `Contains` reports it as already allowed, the consent prompt is skipped
entirely, and the whole capture silently records a hidden controller.

**T9. `tools/AllyXLab/RumbleCalibration.cs:142` - low - `finally { output.Zero(); }` can throw and
replace the original exception.** `XInputMotors.Set` throws `IOException` when XInput refuses
(`Motors.cs:122`), and `Zero()` routes through `Set(0, 0)`. Scenario: the tester presses Stop during
the 400 ms probe pulse and the pad disconnects at the same moment, so the clean
`OperationCanceledException` is replaced by an IO error and the session reports a confusing failure
instead of a cancellation. `Worker.cs:253` guards its own `Zero()` call this way; `Probe` does not.

**T10. `tools/AllyXLab/Motors.cs:68` - low - `SingleOrDefault` throws instead of producing the
intended message.** Both `Motors.Open` (68) and `Worker.cs:260` pair `SingleOrDefault` with a `??`
that can never run for the duplicate case, because `SingleOrDefault` throws on more than one match
rather than returning null. The prepared messages ("not in this inventory", "Select exactly one
inventoried endpoint") are unreachable for duplicated ids.

**T11. `tools/AllyXLab/Program.cs:57` - low - fire-and-forget worker tasks outlive the disposables
they capture.** The parent-exit watcher (57) and the stdin reader (58-74) both call
`cancel.Cancel()`, and the reader also calls `answers.TryAdd`, after `RunWorker` may have disposed
the `CancellationTokenSource`, `AutoResetEvent` and `BlockingCollection`. The resulting
`ObjectDisposedException` lands on a thread-pool thread with no observer. Low impact because the
worker process is exiting anyway.

**T12. `tools/AllyXLab/MainForm.Rumble.cs:9` - low - `Motors.Discover(Hid.Enumerate())` runs on the
UI thread.** It opens every HID collection on the machine, probes four XInput slots and touches
`Gamepad.Gamepads` before the section's first frame, so the window hitches on entry to the rumble
section.

**T13. `tools/AllyXLab/Conflicts.cs:57` - low - manager matching is a loose two-way substring test
with first-match-wins.** `name.Replace(" ","").Contains(match.Replace(" ",""))` means
`ArmouryCrateControlInterface` matches the earlier `ArmouryCrate` entry and is labelled and treated
as that product, and the `break` at 64 makes the outcome depend on table order. Scenario: the tester
is shown the wrong product name, and a process could inherit the wrong `Closable` flag.

**T14. `tools/AllyXLab/Downloads/AllyXLab.exe` - information, not a defect - the tracked binary has
cost the repository about 139 MB of history.** [verified] `README.md:16` states the binary is
committed beside its source "at the maintainer's request", so this is a recorded decision, not an
oversight. For planning only: five revisions of a ~55 MB self-contained single-file exe are the five
largest blobs in the repository and `.git` is now 139 MB, and every rebuild adds another ~55 MB
permanently. A release asset or Git LFS would remove that growth without changing how testers
download it.

## Themes worth acting on before release

1. **Two library event pumps and one bridge dispatcher run on the Avalonia UI thread** (S1, S3)
   because `ConfigureAwait(false)` is missing on pumps started from constructors that WSGM invokes
   on the dispatcher. This one root cause explains several separate stutter, freeze and lost-input
   symptoms, and `SteamUiModuleRuntime` already shows the correct pattern.
2. **Unbounded waits on paths that must always finish**: S2, V1, V2, L2, L4 and O8 all block with no
   timeout or with a token that cannot reach the blocking call, several of them while holding a
   global lock or mutex. These convert a degraded dependency into a hang, and two of them can leave
   the user with no desktop.
3. **Teardown that stops at the first failure** (O2) or leaves acquired resources held (O1) rather
   than completing every step. Both contradict invariants stated in their own comments.
4. **VIIPER is outside every automated gate** (R1, R2, R6, R8, R9): not in the solution, not built
   by CI, pinned to a commit that a standard recursive submodule update cannot fetch, staged without
   provenance, and consumed through a glob that silently accepts a stale DLL.
5. **Version and provenance facts are duplicated by hand** (R3, R4, T6, T7) with no check
   reconciling them, so drift ships silently in exactly the hand-built artifacts.
6. **Native interop needs a layout audit, not a spot fix** (W1, W6, W5): two structs are already
   provably wrong, one corrupting every Wi-Fi record after the first and one overwriting 8 bytes of
   the caller's stack on every audio property read, and the same surfaces are declared twice in one
   process with two different layouts. These are the findings most likely to present as
   unreproducible crashes in the field.
7. **Memory and process safety in the device library** (W6, W9, W3/W8, W12): a stack overwrite, a
   free of buffers the audio driver may still own, COM RCWs severed underneath other threads, and an
   unguarded callback on a native watcher thread that terminates the process during shutdown. None
   of these fail cleanly, and all four sit under ordinary user actions: opening the audio panel,
   pressing a volume key, disconnecting a headset, exiting the app.
8. **Two paths disagree with their own siblings about persistence and error handling** (W7, W10,
   W11, O3): in each case one implementation in the same repository already does it correctly, so
   these are consistency fixes with a known-good reference rather than open design questions.
