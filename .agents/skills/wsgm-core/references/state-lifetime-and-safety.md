# State, lifetime, and safety

## Program modes and recovery

Read the top of `src/WSGM/Program.cs` before changing argument handling. Modes are `Shell`,
`Settings`, and `OverlayTest`. `--shell`/`--boot` win, then `--settings`, then `--overlay-test`; no
arguments deliberately open Settings. Shell-only elevation, mutex, and crash-loop handling occur
after mode and package-cardinality decisions. Mixed `--shell --overlay-test` is real shell startup.

Early operation order is also a safety property:

- the fixed-purpose Explorer anchor and early restore/unregister paths run before logging,
  configuration, Avalonia, GPU initialization, package discovery, or normal services;
- maintenance and other one-shots retain their current deliberate position and authority;
- only the normal selected mode reaches application composition;
- exact overlay-test mode skips package/plugin, display, and autonomous Steam startup, but opening
  the surface can still acquire live overlay input/capture and a Steam Input lease. It is not a
  fully inert process.

Do not make restore-shell depend on the component whose failure it is meant to recover. Normal
session transitions restore through `ExplorerDesktopHost` and its pre-captured medium, jobless
anchor. Early `--restore-shell` runs before normal composition and calls
`ExplorerControl.StartExplorerAndVerify`, including its scheduled-task de-elevation repair when
needed.

Explorer exit starts with its orderly `0x05B4` command. The only bounded kill exceptions are an
original Explorer remnant after taskbar acknowledgement plus the full eight-second linger grace, and
terminal elevation repair of an elevated Explorer before scheduled-task restart. Never kill a
replacement Explorer or substitute generic process killing for orderly exit/recovery.

During service-boot takeover, the splash's Desktop action sets sticky `BootTakeoverCancellation`,
pauses the Steam monitor, and lets the boot worker release the `SessionModes` transition gate before
desktop restoration. Calling `EnterDesktopMode` directly while takeover owns that gate recreates the
deadlock/race.

## Configuration ownership

`Core/ConfigStore.cs` owns `%LOCALAPPDATA%\WSGM\config.json` and its cross-process lock.

- Normal load can defend with defaults; a mutation uses the strict load path. If existing config is
  unreadable, abort rather than writing defaults over recovery snapshots.
- Writes are serialized and atomically replace state. A read/modify/write mutation starts from fresh
  state under the lock.
- Settings transactions that couple config and promoted assets hold the established lock across the
  transaction, not across unrelated long copies.
- A live reload replaces the `AppConfig` object. Managers receive/project new values; they do not
  retain nested references into the previous object.
- Durable preference belongs in config. Handles, debounce generations, in-flight operations,
  ownership claims, and observed live values belong in the manager that owns them.

The current reload route is a useful model: `ShellSession.WatchStartupAppsAndConfig` creates the
session-owned watchers during startup; `WatchConfig` loads on a worker and posts the detached
result. The dispatcher callback rejects `_disposed` or stale `_configReloadGeneration` immediately
before replacing `_config`, closing the older-result race. It then calls existing owners such as
`StartupAppWatcher.Apply`, `OverlayController.ApplyConfig`, and `SessionModes.ApplyConfig`; reload
does not construct another session or manager. Do not re-enter startup/composition from an apply
path. `WatchStartupAppsAndConfig` is not independently idempotent today, so a duplicate call can
overwrite live watcher references without disposing the old owners.

Test unreadable current state, recovery snapshots, concurrent mutations, reload replacement, and
save failure. Never point tests at the user's real profile.

## One owner and an explicit state machine

For every long-lived service identify:

1. who constructs it;
2. when admission opens;
3. which thread owns UI-observable state;
4. which cancellation token ends background work;
5. which dependencies must still exist during restoration;
6. how repeated stop/dispose behaves;
7. what evidence distinguishes clean, unverified, and failed cleanup.

Root session services in `ShellSession` fields so native registrations, watchers, and callbacks are
not collected or orphaned. Views subscribe to projections and send intent; they never acquire
machine resources. Static process-wide hooks/sinks require explicit reset and test serialization.

Some resources are intentionally scoped to a mode rather than the whole process. `TrayHost`, for
example, is created for a game-mode span and retired before Explorer returns. Re-entry must not
replace the field with `null` or a second object while an earlier static/native owner remains live.

Close command/input admission before tearing down dependencies and accumulate failures so one
exception does not skip later cleanup. Preserve this established dependency order:

1. cancel work and restore AutoTDP before shutting down `DeviceCoordinator`;
2. await session-transition, boot, and Steam transport-gate work;
3. retire WSGM's tray before Explorer recovery, and retain the shell anchor if desktop verification
   fails;
4. dispose the Steam UI host before detaching/disposing its transport;
5. dispose audio/radio owners after their consumers;
6. destroy `MessageWindow` last among services registered against that window; it is not necessarily
   the final unrelated cleanup in the process.

Verify phase-level exception isolation rather than assuming a failure list proves continuation. The
current `ShellSession.ShutdownAsync` has a broad final `try` around transition waits, tray
retirement, Explorer recovery, and later service cleanup; an unexpected early exception can skip
later phases. Safety-critical restoration needs independent guards or a guaranteed recovery
`finally`, with failures accumulated only after every required phase ran. Also inspect
`EnterGameModeSurfaces` on duplicate entry: assigning the result of a refused `TrayHost.Create()`
can lose the reference to the still-live tray owner.

## Threads and hot paths

- Avalonia-bound state and collections change on `Dispatcher.UIThread`.
- Cold acquisition, package loading, Windows enumeration, hardware/network calls, and blocking waits
  stay off the UI thread.
- Background callbacks catch ordinary failures at the native boundary; no exception crosses into a
  native callback.
- Every background loop observes cancellation and has bounded I/O/deadlines. Avoid fire-and-forget
  unless a named owner observes its completion and failure.
- Controller, sensor, frametime, and telemetry paths avoid per-sample allocation, locks spanning
  I/O, and per-sample logs. Use latest-wins/coalescing when intermediate state has no semantic
  value.
- For worker-to-overlay collections, follow the current app-switcher shape: one in-flight flag,
  background enumeration, a detached snapshot, dispatcher publication guarded by `_disposed` and
  captured/current view-model identity, then `AppSwitcherViewModel.Reconcile` in place. Never mutate
  a bound `ObservableCollection` from the worker or replace surviving entries and lose focus.
- Use `Log.Change` for polled transition state. Default Info/Warn/Error must retain the evidence
  needed for a pasted `%LOCALAPPDATA%\WSGM\wsgm.log`; Debug is supplemental.

## External state and uncertainty

Before a persistent or destructive operation, re-open/re-read exact target identity and validate
bounds at the last responsible moment. A successful API return without readback is not verified.
Timeout or cancellation after dispatch can mean the operation happened; surface uncertainty,
reconcile current state, or require a new explicit user action instead of automatically retrying.

Preserve every external owner's state:

- remove only WSGM's Steam patch/member/namespace, HidHide entry, input lease, config value, display
  change, mount/library entry, or temporary file;
- capture originals before the first mutation and keep them until restoration is proven;
- make disable and repeated cleanup idempotent;
- fail open when WSGM cannot prove it still owns a change.

Device Integration off is not a degraded plugin state. `ShellSession` still creates the session
`DeviceCoordinator`, which reserves `Global\WSGM.DeviceOwner`, but it starts no plugin lifecycle,
controller target, hardware write, or AutoTDP. Code independent of a package must still work, and
turning integration off does not make package maintenance ownership available.

## Live-machine boundary

Do not use automated tests to run shell/boot mode, alter Explorer, install/remove a plugin, deploy a
developer package, mutate Steam CEF, edit HidHide, create a controller target, write hardware,
format/eject storage, install services/drivers, or touch the user's config. Use fakes, temp roots,
captured fixtures, and policy extraction.

The following require explicit maintainer direction and a recovery path even when useful:

- `--shell`, `--boot`, `--restore-shell`, `--unregister-shell`, setup/uninstall and package
  maintenance modes;
- UAC/lock changes, Steam Input shim maintenance, and the live radio probe;
- `eng/dev-deploy.ps1` and release/install scripts;
- Device Lab capture/read probes/hardware actions;
- live Steam helpers under `tools/WsgmLibTest`;
- real SD-card format/eject, radio, display, controller, sensor, fan, power, lighting, or firmware
  checks.

Shutdown behavior is reason-specific: `SessionEnd` must not launch Explorer, and update shutdown
must not send the normal Big Picture exit operation. If WSGM's tray retirement is unverified, do not
start Explorer beside a possible live `Shell_TrayWnd`; retain the recovery anchor instead.
