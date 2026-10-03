# Steam UI host and semantic services findings

Scope: the WSGM side of the Steam UI integration at baseline `1329813f`. That is `SteamUiSessionHost`,
`NativeQamSemanticServices`, `ShellSession.SteamUi.cs`, `SteamUiReadiness`, `WsgmSteamSettingsService`,
`CommonPluginSteamUiSource`, `SteamStorageBridge`, `LibraryTabManager`/`LibraryBadges`, `ThemeService`,
`AnimationService`, `Core/Themes/**`, `Core/Animations/**`, `SteamDownloadSort`, `SteamUiAssetCatalog`, the WSGM
`Source/*.ts` fragments and `eng/build-steam-assets.mjs`. Sources: `review/steamhost.md`, its adversarial verification
`review/steamhost.verify.md` (corrections applied; all ten missed findings included as full sections), `_critic.md`
(conflicts 1, 2, 4, 5, 8 and 15, USER-001 interplay, CRIT-002 and CRIT-006, which plan v2 implements in a steamhost batch)
and `refactor-plan-v2.md` (wins wherever it simplified a recommendation).

Counts by severity, after verifier corrections: critical 0, high 0, medium 17, low 31, nit 10 (58 sections, including
the critic's CRIT-002 and CRIT-006). One id is no-change. Nothing was refuted. Within a severity, sections run
STEAMHOST numeric ids, then STEAMHOST-V ids, then CRIT ids.

Plan v2 batches for this area: B018 (theme journal), B084 (local defect fixes), B085 (switches snapshot), B086
(backends outside the host), B087 (NativeQam split), B136 (`SteamUiCoordinator`), B137 (library tabs as instances), B138
(content services; D2 and D10 decided), B139 (asset pipeline). Toolkit batches B053, B054, B056 and B057 carry the WSGM
consumer edits for STEAMHOST-004, 016, 037, 044, V-005 and V-009. B032 removes the asset size cap (V-003). B005
(USER-001) runs first and deletes the host's performance-observation plumbing, so no solution below keeps
`ReleasePerformanceObservation` or `UpdatePerformanceObservation`.

Rules that shaped the solutions: plugin Steam UI modules register when their plugin becomes ready and leave when it
stops (maintainer decision in DECISIONS.md, which overrides critic conflict 5 on this point), and there is still no
`DependsOn` graph, quarantine API or picker policy type. Fold ids keep today's strings (conflict 8). The storage bridge
gets no revision cache (conflict 15). Shutdown is a fixed safety-first step list in which each owner exposes
`CloseAdmission()` (conflicts 1 and 2). steamhost B4 (B087) runs against the concrete `DeviceCoordinator` before the
device batches (conflict 4).

A solution-checker pass against `1329813f` corrected the solutions of STEAMHOST-001, 002, 005, 007, 008, 010, 011,
012, 015, 016, 017, 019, 025, 026, 028, 030, 032, 033, 035, 041, V-001, V-004, V-005, CRIT-002 and CRIT-006 where the
code disagreed with them. Two deliberately depart from plan v2 batch text: V-001 also drops the 128 KiB journal bound
(not in D2) and STEAMHOST-005 names a concrete host constructor instead of `SteamUiHostOptions`.

The maintainer's answers in `DECISIONS.md` are applied and win over plan v2 section 4: STEAMHOST-006 now registers
plugin modules when their plugin becomes ready, STEAMHOST-015 follows D10 (files restored and Steam's startup-movie
choice handed back at exit and uninstall), and the D1 and D2 gates are recorded as decided. No finding here rested on a
dropped security item or on readback machinery, so no section moved to the no-change list and the counts are unchanged.

---

## Medium

### STEAMHOST-001: Steam's QAM can still dispatch into disposed device owners during shutdown, and the host has no synchronous admission close

- **Severity:** medium (verifier lowered from high)
- **Where:** `src/WSGM/Shell/ShellSession.Shutdown.cs:108-191` (device, AutoTDP, plugins and GPU disposed first) against
  `:355-370` (`_steamUi.DisposeAsync()` and the untokened `_cefMasterGate.WaitAsync()`);
  `src/WSGM/Shell/SteamUiSessionHost.cs:437-445` (`DisposeAsync`), `:832-863` (`DisableAsync`, whose no-CEF prefix is
  `:839-851`), `:343-346` (runtime publish/request gates).
- **Problem:** shutdown disposes AutoTDP, the device coordinator, common plugins and GPU before the Steam host. In that
  window Steam can still send `setPrimaryLimit`, `setAutoTdp` or `setControllerTarget` into the
  `DeviceCoordinatorNativeQam*` services. The verifier showed this is not a hardware hazard. The command reaches disposed
  capabilities or gates (`DeviceCoordinator.cs:805-862`), fails with a disposed-object error inside the module runtime
  and writes nothing. What remains: failed commands that should have been refused, a master-gate wait with no token
  (bounded only by CEF evaluation timeouts), and no way to stop the host answering without CEF round trips, because
  `DisposeAsync` first awaits `DisableAsync`.
- **Best solution:** add `internal void CloseAdmission()` to `SteamUiSessionHost`. It is exactly the existing no-CEF
  prefix of `DisableAsync`: store all-off switches (the flags before B085, an all-off `SteamUiSurfaceSwitches` after
  it), turn the overlay-activation patch switch off and call `CancelAllInflightRequests()`. The runtime's publish and
  request gates already read those switches (`:343-346`), so every later command is refused with no new state.
  `DisableAsync` starts by calling `CloseAdmission()`. `SteamUiCoordinator.CloseAdmission()` (B136) forwards to the host,
  stops its own gate loop from starting new work and turns later `ApplyConfig`, master-switch and Big Picture requests
  into no-ops, so no late `host.Apply` (reload, Game Mode entry) can reopen what step 0 closed before step 8 runs.
  B140 calls it at step 0 (T0). Patch retraction and `DisposeAsync` stay after device cleanup, at B140 step 8, under
  the remaining deadline. Do not move host disposal ahead of device/GPU
  disposal: AutoTDP restore, controller release and the HidHide cloak-off (`Shutdown.cs:108-111`) must never wait behind
  CEF round trips (never-strand rule). Replace the untokened gate wait with a wait on the shutdown deadline token. After
  B005 there is no `ReleasePerformanceObservation` to call. Use the name `CloseAdmission` (critic conflict 2).
- **Tests:** with the fake transport, a QAM command after `CloseAdmission()` is refused and no CEF call is made. A
  call-order recorder shows admission closed before device disposal and retraction after it. A repeated shutdown is a
  no-op. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiCoordinator|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~ShellAnchorDisposal"`.
- **Plan v2:** B136 (the method and the coordinator forward). B140 makes the T0 call; decided: D1, safety-first ordered
  steps under one deadline, without the B3 percentage cutoffs or preliminary drain.
- **Related:** U05-LFB-020, SESSION-020, critic conflicts 1 and 2, USER-001 (B005), STEAMHOST-028.

### STEAMHOST-002: a feature-switch write can be overwritten by a stale retraction

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:589-610` (`Apply`), `:619-830` (the other `Apply*`), `:857-862`
  (`DisableAsync` tail), `:939-961` (sync loop else-branch), `:1498-1534` (`SetPatchStates`); toolkit
  `SteamUiPatchManager.cs:346-429` (last-writer-wins switches); ungated callers `ShellSession.cs:1149-1150` and
  `ShellSession.Config.cs:125-140`.
- **Problem:** three unordered writers set patch and global switches: the UI-thread `Apply*`, the sync loop and
  `DisableAsync`. Suppose the loop reads `BootstrapWanted()` as false and an `Apply(true)` then enables the patches. The
  loop's `SetPatchStates(false,false)` and `SetGlobalEnabledAsync(false)` overwrite that, and the next pass sees
  `BootstrapWanted()==true` without re-setting anything. Native QAM then reads as on while every row patch is disabled,
  until the next toggle. `DisableAsync`'s final `SetPatchStates(false,false)` plus `SetGlobalEnabledAsync(false)` do the
  same to an ungated `Apply*` that lands mid-retraction (Game Mode entry, config reload), and the host's equality guards
  then swallow the restore's re-apply.
- **Best solution:** B054 deletes the host sync loop (the manager runs its own loop). In B084, route every switch write
  through one private method, `ApplySwitchStates()`. It runs under a small `Lock _switchGate`, reads the flags once and
  sets everything from them: `SetPatchStates(BootstrapWanted(), _enabled)`, `SetGlyphDeliveryPatchStates()`, the
  overlay-activation patch and
  `_patches.SetGlobalEnabled(BootstrapWanted() || _glyphDeliveryEnabled || _surfaceObservationEnabled)`. Each `Apply*`
  writes its flag and calls `ApplySwitchStates()` inside the same lock, so flag write, derivation and switch set are one
  step and the last caller always leaves the switches matching the last flags. `OnThemesChanged` (`:1450-1459`) is a
  fourth writer (it sets the theme-style patch switch from the themes' own switch); it also just calls
  `ApplySwitchStates()`, whose `SetPatchStates` already derives `SteamThemeStyleSurface.PatchId` from
  `_themes is { Enabled: true }`.

  `DisableAsync` clears the flags under the lock, calls `ApplySwitchStates()` and awaits one `SynchronizeAsync`. An
  `Apply` that arrives after the clear wins, as it should. The two-pass "gates first, bridge last" disappears because the
  B054 manager removes its bridge last on every path. No hard-coded `SetPatchStates(false,false)` or
  `SetGlobalEnabledAsync(false)` remains. The lock covers only synchronous switch setters and never an await, so it
  cannot deadlock with the manager's scheduler gate. A snapshot without the lock would still leave a read-then-write
  window between two threads, so the lock is the minimum that closes it.
- **Tests:** in `SteamUiSessionHostTests` with the `SessionHostTransport` fake:
  - `Apply(true)` interleaved after `DisableAsync` has cleared the flags ends with QAM rows and the global switch
    enabled;
  - `Apply(false)` with download sort on keeps the bridge enabled and turns the QAM row patches off;
  - `GetPatchSnapshots()` matches the flags after every call.

  Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost"`.
- **Plan v2:** B084 (after B054). B085 turns the flags into one record.
- **Related:** STEAMHOST-003, 037, 047, V-010; TOOLKITCS-030.

### STEAMHOST-003: thirteen switch flags, eight near-identical `Apply*` methods and a second set of shadow fields in the session

- **Severity:** medium
- **Where:** host `src/WSGM/Shell/SteamUiSessionHost.cs:141-162`, `:572-830`; session
  `src/WSGM/Shell/ShellSession.SteamUi.cs:34-63`, `:359-441`, `:607-635`; reload `ShellSession.Config.cs:120-140`.
- **Problem:** every host `Apply*` repeats the same sequence: equality check, set, global enable, `SetPatchStates`,
  `QueueSynchronization`, `QueueStatePublication`. The session keeps its own copies (`_downloadSortEnabled`,
  `_libraryBadgeEnabled`, `_homeCarouselEnabled`, `_carouselShowUninstalled`, `_screensaverTimeoutsEnabled`,
  `_wifiIndicatorEnabled`) with a second set of equality guards and redundant self-assignments. Reload applies Steam UI
  through nine order-dependent calls. Every extra entry point is another overwrite source for STEAMHOST-002.
- **Best solution:** add `internal sealed record SteamUiSurfaceSwitches` with these fields:
  - `NativeQuickAccess`, `HostSurfaces`, `SurfaceObservation`;
  - `NetworkIndicator`, `DownloadSort`, `LibraryBadge`, `HomeCarousel`, `CarouselShowUninstalled`, `ScreensaverRows`;
  - `Glyphs`, `GlyphProfile`, `NativeGlyphArtwork`.

  It is built by one pure `static SteamUiSurfaceSwitches From(AppConfig config, bool cefMasterEnabled, ImportedGlyphProfile? glyphProfile, bool nativeArtwork)`.
  The host gets one `Apply(SteamUiSurfaceSwitches next)`. Under `_switchGate` it swaps the stored record, compares it
  with the previous one, runs the synchronous edge effects, calls `ApplySwitchStates()` (STEAMHOST-002) and queues
  publication when a published value changed. The edge effects stay synchronous on their edge (verify batch problem 3):
  - native QAM on to off calls `CancelAllInflightRequests()` at once;
  - screensaver rows on to off call `_displayTimeouts.ForgetSteam()`, and the host ignores `ScreensaverRows` when no
    timeout owner exists;
  - network indicator on to off while QAM is off calls `_network.PostStopScanning()`;
  - a `HostSurfaces` change sets the sound integration status text;
  - a change to `CarouselShowUninstalled` alone queues publication only, with no switch change;
  - a glyph input change runs `_glyphDeliveryState.Update(...)` and its log line.

  `ReleasePerformanceObservation` is no longer an edge effect because B005 deletes it. Delete from the session the six
  shadow fields, `ApplySteamUiSurfacePreferences`, `ApplyDownloadSort`, `ApplyLibraryBadge`, `ApplyHomeCarousel`,
  `ApplyScreensaverTimeouts`, `ApplyNetworkIndicator` and the self-assignments. Reload, Big Picture restore, Game Mode
  entry and master enable each call one session method that builds `From(...)` and calls `host.Apply`. After B136 that
  method is `SteamUiCoordinator.ApplyConfig`. `_glyphDeliveryEnabled`, `_graphicsReady` and `_wsgmSettingsReady` stay
  derived host state.
- **Tests:** new `SteamUiSurfaceSwitchesTests` covering `From` for every surface. A host table maps switches to the
  expected patch enablement over the registry, including QAM off with download sort on, a carousel preference-only
  change (publication, no retraction) and screensaver rows without a timeout owner. Another test checks that commands are
  refused immediately after `Apply` with QAM off. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~SteamUiSurfaceSwitches"`.
- **Plan v2:** B085.
- **Related:** U05-LFB-025, STEAMHOST-002, 039, 047, V-010, USER-001 (B005), critic section 2 item 1.

### STEAMHOST-004: the quarantine mirror and the two-pass disable are toolkit workarounds

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:64-68` (`_failedPatchGate`, `_failedPatchIds`), `:832-863` (two-pass
  disable), `:1462-1491` (`OnModuleFailed` retraction, `Quarantined`), and the `Quarantined()` checks in `SetPatchStates`
  and `OnThemesChanged` (`:1456`, `:1532`).
- **Problem:** WSGM mirrors toolkit quarantine state and retracts a failed module's patches itself. It also disables in
  two passes because the toolkit did not remove the bridge last. That is duplicated policy, and the two mirrors can
  disagree.
- **Best solution:** in the parent half of B054, once the manager faults a failing module's patches itself
  (quarantine kept for the runtime instance and reset on WSGM restart) and removes its own bridge last on every path:
  - delete `_failedPatchGate`, `_failedPatchIds`, `Quarantined`, the retraction loop in `OnModuleFailed`, the remount
    guard and both `Quarantined()` checks;
  - make `DisableAsync` a single pass (see STEAMHOST-002);
  - delete `SynchronizeLoopAsync`, `_synchronizeSignal` and `_signalPending`;
  - move the post-sync reconciliation (sound status, `ReconcileScreensaverReport`, `ReconcileWsgmSettingsMenu`,
    publication) into the handler for the manager's `Synchronized` event, which the manager raises asynchronously after
    each iteration.

  No `DependsOn` graph and no quarantine reset API (critic conflict 5).
- **Tests:** a module failure retracts its patches through the runtime with no host code. One disable pass removes gates
  before the bridge. The `Synchronized` handler updates sound status. Filters:
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamUiModule"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost"`.
- **Plan v2:** B054.
- **Related:** U02A-SUTC-034, U02A-SUTC-002, TOOLKITCS-025, TOOLKITCS-030, TOOLKITCS-044, STEAMHOST-037, critic
  conflict 5.

### STEAMHOST-005: the host constructs about 25 concrete backends, including ambient native and config access

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:224-252` (28 constructor parameters, 24 of them optional, whose null
  means overlay-test, although the host is never built in overlay-test, `ShellSession.cs:1028-1031`), `:78`
  (`HybridCores.Windows`), `:109-110` (`ConfigStore.Mutate`, `PowerSchemes.Windows`), `:284` (`new QuickAccessFolds()`,
  a file store under `Log.Directory`), `:290-316`, `:321-327` (`SteamUiAssetCatalog`).
- **Problem:** the host is both composition root and lifecycle owner. Tests cannot inject the ambient pieces. The null
  defaults encode a mode that never happens, so required backends look optional.
- **Best solution:** add `Shell/WsgmSteamModuleCatalog.cs` with a plain record `SteamUiBackends` holding every semantic
  backend, already built, and a static `WsgmSteamModuleCatalog.Create(SteamUiBackends backends, Func<SteamUiSurfaceSwitches> switches)`.
  `Create` holds today's `CreateModules` without the plugin modules, which the host adds and removes as plugins become
  ready or stop (STEAMHOST-006), plus `ReadPages`, and returns
  `WsgmSteamModules(IReadOnlyList<ISteamUiModule> Modules, IReadOnlyList<SteamPage> Pages, Func<Action, IDisposable> Subscribe)`.
  `Subscribe(queuePublication)` attaches the callback to every change source today's constructor wires to
  `QueueStatePublication`/`OnSemanticStateChanged` (`:285`, `:317`, `:348-431`) and returns one disposable that
  detaches them all. `ThemeService.Changed` stays a host subscription because it moves a patch switch (STEAMHOST-002).
  The composition root in `ShellSession` builds every backend, including the `DeviceCoordinatorNativeQam*` and
  `NativeQam*` wrappers the host constructs today, the power-scheme and hybrid-core accessors (whatever WINSVC-B2 makes
  of them, critic conflict 13), the `QuickAccessFolds` store, the brightness fallback and the config writer, and it
  disposes them after the host (STEAMHOST-027's rule: the creator disposes). The host constructor becomes
  `SteamUiSessionHost(ISteamUiTransport transport, SteamUiBackends backends, Func<CancellationToken, Task<bool>> toggleQuickAccess)`:
  it calls `Create`, builds asset, bridge, patch manager and runtime, and calls `Subscribe` last (V-007). Its edge
  effects read the backends they act on (network, Bluetooth, display timeouts, sounds, themes, WSGM settings) from
  `backends`. This replaces plan v2's unspecified `SteamUiHostOptions`.

  Required backends become non-null, and the overlay-test null comments are deleted. A backend stays nullable only where
  a session can really lack it: device integration off, no GPU plugin, no common plugins, CPU boost unavailable, no
  display-timeout owner. Use no per-group interfaces and no registry. The device types stay concrete until the device
  batches.
- **Tests:** the catalog declares exactly today's modules for each backend combination (a snapshot of patch ids and
  command vocabulary), and the folds store is the injected one. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~WsgmSteamModuleCatalog"`.
- **Plan v2:** B086.
- **Related:** U05-LFB-025 (dead overlay-test conditionals), STEAMHOST-027, V-007, critic conflict 13.

### STEAMHOST-007: `SteamStorageBridge` id maps are mutated and read across threads without a lock

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SteamStorageBridge.cs:32-37` (`_driveIds`, `_deviceIds`), `:366-378` (`DriveId`/`DeviceId`
  add entries during `ReadState` on the publication loop), `:583-601` (`ResolveDrive`, `ResolveDevice`, `FindTarget` on
  command threads), `:45`/`:462-468` (`_loggedProjection`).
- **Problem:** concurrent `Dictionary` writes and reads can corrupt the maps or throw. A throw inside `ReadState`
  quarantines the storage module for the rest of the session (`SteamUiModuleRuntime.FailModule`), so Steam's storage
  page goes dead until WSGM restarts.
- **Best solution:** fix together with STEAMHOST-V-006, with one owner thread and no lock. Subscribe to
  `_drives.Drives.CollectionChanged` and `_formats.Targets.CollectionChanged`; both fire on the UI thread. On either
  event, build an immutable `StorageSnapshot(FormatTargetEntry[] Targets, RemovableDriveEntry[] Drives, FrozenDictionary<string,uint> DriveIds, FrozenDictionary<string,uint> DeviceIds)`.
  The arrays hold the entry objects themselves, not copies of their fields: `RemovableDriveEntry.Letters` and
  `Ejected` change in place on the UI thread without a `CollectionChanged` (`RemovableDriveManager.cs:470-480`), and
  reading those single properties through the reference stays correct. The id assignment that lives in
  `DriveId`/`DeviceId` today moves into that builder, which numbers every target and entry in the collections. The
  session-stable source maps stay private and are only ever written on the UI thread. Publish the snapshot with
  `Volatile.Write`.

  `ReadState`, `ResolveDrive`, `ResolveDevice`, `FindTarget`, `FirstMountPath`, `EjectAsync` (`:168`) and `TrimAllAsync`
  (`:241`) read only `Volatile.Read(ref _snapshot)` and never touch the observable collections or the source maps.
  `ReadState` keeps reading `_drives.HasScanned` live: a first scan that finds nothing raises no `CollectionChanged`,
  so a scanned flag stored in the snapshot would keep answering null and strand Steam on a pulled card, the defect
  `ReadState`'s remarks describe. `_loggedProjection` is touched only by the publication loop, so it stays a plain
  field. Build an initial snapshot in the constructor; it runs on the UI thread at session composition
  (`ShellSession.cs:720`).
- **Tests:** with fake drive and format lists (STEAMHOST-041), commands resolving ids while the snapshot is rebuilt never
  throw, and an id stays stable across rebuilds. A first scan with no drives publishes an empty state, and an entry
  ejected in place drops out of the next `ReadState` with no collection change. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamStorageBridge"`.
- **Plan v2:** B084. B096 (WINSVC-B4) later makes the snapshot a projection of `StorageInventory`.
- **Related:** STEAMHOST-V-006, 008, 041, WINSVC-004, critic conflict 15.

### STEAMHOST-008: `SteamStorageBridge.ReadState` does synchronous disk I/O, including network drives, on every publication round

- **Severity:** medium
- **Where:** `src/WSGM/Shell/SteamStorageBridge.cs:296-356` (`ReadState`), `:318` and `:620`
  (`WindowsStorage.DescribeVolumes()`), `:498-518` (markers), `:543-570`, `:612-623`;
  `src/WSGM/Shell/SteamUiSessionHost.cs:1322-1328` (a comment saying the read "only projects");
  `external/windows-device-control/src/WindowsDeviceControl/WindowsStorage.cs:54-90` (`DriveInfo.GetDrives()`, then
  `IsReady`/`TotalSize` for every drive).
- **Problem:** whenever removable storage is present, every publication round re-enumerates every volume, re-reads
  `libraryfolders.vdf` per device and reads each card marker. Any surface change raises a round, including AutoTDP status
  ticks. The module runtime reads modules one after another (`SteamUiModuleRuntime.cs:290-318`), so a stalled network
  drive's `IsReady` blocks every QAM publication.
- **Best solution:** keep the per-round projection and make it cheap. Do not add a cached projection with a revision: it
  would go stale on format-target refresh, `AddLibraryAsync`, card renames and marker writes, trim start/stop and Steam's
  own `libraryfolders.vdf` edits, and B096 owns the storage inventory anyway (critic conflict 15).

  Replace both `WindowsStorage.DescribeVolumes()` calls with an `internal static DescribeLocalVolumes()` in the bridge,
  which the session passes in through STEAMHOST-041's `describeVolumes` seam. It walks
  `DriveInfo.GetDrives()` and skips every drive whose `DriveType` is not `Removable` or `Fixed` before touching
  `IsReady`, so network, optical and unrooted drives are never probed. It then builds the same public `StorageVolume`
  records with `WindowsStorage.DiskNumberFor(letter)`. `ReadState` takes one volume list per round and shares it with
  `BlockDevice`. Correct the host comment at `:1322-1328` to say the read performs local volume and marker reads.
- **Tests:** a fake drive and format list with no network entries projects the same rows as today. Pin the drive-type
  filter with a unit test over the pure selection helper; make the filter a static function of `DriveType` so it is
  testable without real drives. Filter: `--filter "FullyQualifiedName~SteamStorageBridge"`.
- **Plan v2:** B084.
- **Related:** STEAMHOST-007, V-006, WINSVC-004 (B096), critic conflict 15.

### STEAMHOST-009: `AnimationService` writes configuration while holding its state lock

- **Severity:** medium
- **Where:** `src/WSGM/Shell/AnimationService.cs:974-992` (`ChangeConfigLocked` calls `_writeConfig` under `_sync`);
  callers at `:419`, `:474`, `:486`, `:542` (`Start()`, UI thread), `:794`, `:893`, `:900`. `_writeConfig` is
  `CommitWsgmSetting` (`ShellSession.Config.cs:31-43`), which takes the cross-process config mutex (2 s timeout) and
  writes the file.
- **Problem:** `ReadState` and `ReadExtensionsItem` run on the publication loop and the overlay UI thread, and both wait
  behind cross-process mutex and file I/O whenever a choice is saved.
- **Best solution:** replace `ChangeConfigLocked` with `ChangeConfig(Action<AnimationsConfig> change)`, called outside
  `_sync`, which:
  1. takes a dedicated `Lock _configWrite`, so two writers cannot reach the file in a different order than memory;
  2. calls `_writeConfig(change)` and catches the same exception list;
  3. takes `_sync` only to clone, apply `change` and swap `_config`, and to set `_error` on failure.

  Lock order is always `_configWrite` then `_sync`, and `_configWrite` is never taken while `_sync` is held. Each caller
  splits its block: the `_sync` section that decides, then `ChangeConfig`, then a `_sync` section for the follow-up
  state. `Start()` keeps calling it synchronously, so the start-time override write still happens before Steam starts
  (`ShellSession.cs:748-750`); it is only off the state lock. Keep today's in-memory behaviour on a failed save here.
  STEAMHOST-029 changes it to "refuse and apply nothing" in B138.
- **Tests:** a slow `writeConfig` fake (blocks on a gate) does not block `ReadState`. Two concurrent `SetBootVolumeAsync`
  calls leave memory and the written value equal. Filter: `--filter "FullyQualifiedName~AnimationService"`.
- **Plan v2:** B084.
- **Related:** STEAMHOST-029, 030.

### STEAMHOST-010: fire-and-forget work in this domain is not joined at shutdown

- **Severity:** medium
- **Where:**
  - `ShellSession.SteamUi.cs:466`, `:515` (`ApplyCefMasterSwitch` enable/disable via `Task.Run`; an exception from
    `ApplySteamUiTransportGate` in the `finally` is unobserved), `:240` (`RestoreSteamUiAfterBigPictureAsync`), `:327`
    (tab boot sync);
  - `WsgmSteamSettingsService.cs:735` (`ApplyLatestSteamInputAsync`, which may elevate);
  - `LibraryTabManager.cs:417, 670, 681, 710, 767, 817` (`SyncQuietlyAsync`/`PushTabOrderAsync`, which write
    `config.json`);
  - `ThemeService.cs:355, 384, 406, 707, 784, 808-809, 941, 1006`;
  - `AnimationService.cs:281, 322, 601, 828, 1022`;
  - `SteamUiSessionHost.cs:874` (`sounds.RefreshAsync`).
- **Problem:** this work can outlive its owner. Config writes, an elevation prompt or a Steam call can start during or
  after shutdown, and exceptions go unobserved.
- **Best solution:** each owner keeps the one task it started, under its own lifetime token, and awaits it in its
  disposal. Owners never start new work after their `CloseAdmission`. Per owner:
  - **`SteamUiCoordinator` (B136):** master enable/disable, Big Picture restore and the gate loop run as tracked tasks
    under the coordinator's single gate, with exceptions logged inside the task.
  - **`LibraryTabService` (B137):** one worker task drains the boot, card and builder syncs and the order pushes in
    press order (STEAMHOST-011 describes the loop).
  - **`ThemeService`/`AnimationService` (B138):** these start several kinds of background work, not one: installs and
    copies (the `ContentWorkSlot` of STEAMHOST-029), the start-time junction task, the translations loop and update
    check (`ThemeService.cs:784`, `:808-809`, `:1006`), browse fetches (`:355`, `:384`, `:406`, `:707`) and the repository
    fetches and Steam-choice work in `AnimationService` (`:281`, `:322`, `:601`, `:828`). Each service keeps every task
    it starts in one `List<Task> _work` under `_sync` (completed ones are dropped when a new one is added). Disposal
    becomes `DisposeAsync`: it cancels `_shutdown`, then awaits `Task.WhenAll(_work)` within the shutdown deadline,
    swallowing `OperationCanceledException`; the session's disposal call sites change to `await`. Browse sessions link
    only to `_lifetime` (CRIT-006).
  - **`WsgmSteamSettingsService`:** store the last started `ApplyLatestSteamInputAsync` task in a field under `_gate`.
    Every run takes the latest pending value through `_steamInputGate`, so awaiting the last task is enough, and
    `DisposeAsync` awaits it within the shutdown deadline.
  - **`sounds.RefreshAsync`:** started by the host on a generation change (`SteamUiSessionHost.cs:874`) and by the
    composition root at startup (`ShellSession.cs:741`). `SoundPackService` keeps the last refresh task it started in
    one field under its `_sync`, and its disposal awaits it; neither caller discards a task it could not join.

  Add `WsgmSteamSettingsService.cs` to B137's file list. The sound refresh change lands in B138, whose file list already
  holds `SoundPackService.cs`.
- **Tests:** a background install and a pending order push are joined by dispose. A Steam Input apply queued before
  shutdown finishes before dispose returns. Filters: `--filter "FullyQualifiedName~LibraryTab|FullyQualifiedName~WsgmSteamSettings"`
  (B137), `--filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService"` (B138).
- **Plan v2:** B137 (resolving batch). Coordinator sites in B136, content sites in B138.
- **Related:** STEAMHOST-011, 029, CRIT-006, SESSION-021.

### STEAMHOST-011: `KickTabBootSync` can cancel a disposed source, and completed sources leak

- **Severity:** medium
- **Where:** `src/WSGM/Shell/ShellSession.SteamUi.cs:316-353`; triggers `ShellSession.cs:1153`, `:1166`,
  `ShellSession.SteamUi.cs:265`, `:499`; cancels `ShellSession.cs:1128` (desktop trip), `ShellSession.SteamUi.cs:514`
  (master off), `ShellSession.Shutdown.cs:88`.
- **Problem:** the previous run's `finally` disposes its `CancellationTokenSource` between the store at `:325` and
  `previous.Cancel()` at `:326`, so `Cancel()` can throw `ObjectDisposedException`. Sources replaced while their run is
  still pending are never disposed by anyone.
- **Best solution:** one tracked worker owns the source lifecycle and nothing else creates or disposes sources. Fields:
  `SemaphoreSlim _bootSignal = new(0, 1)`, `Lock _bootGate`, `bool _bootRequested`, `CancellationTokenSource? _bootPass`
  and `Task _bootWorker`.
  - **`RequestBootSync()`:** under `_bootGate`, set `_bootRequested`, cancel `_bootPass` if one is set (supersede),
    then release `_bootSignal` if its `CurrentCount` is 0.
  - **`CancelBootSync()`** (desktop trip, master off, shutdown): under `_bootGate`, clear `_bootRequested` and cancel
    `_bootPass`. A request that was signalled but not yet started is dropped with it, as today's cancel drops
    everything outstanding. That matters on master off: the transport is still open during the retraction, so a
    request that survived the cancel would re-inject the tabs mid-retraction, which is the hazard the cancel at
    `ShellSession.SteamUi.cs:509-514` exists for. The paths that want tabs again (master on, Game Mode entry, Steam
    restart, card volume change) call `RequestBootSync()` as they call `KickTabBootSync` today.
  - **Worker loop:**
    1. await the signal on the lifetime token;
    2. under `_bootGate`: if `_bootRequested` is false, go back to 1; otherwise clear it and create `_bootPass` linked to
       the lifetime;
    3. run `SyncOnBootAsync(_bootPass.Token)` and log supersede, cancel or failure as today;
    4. under `_bootGate`, take `_bootPass` into a local and set the field to null;
    5. dispose the local.

  Cancel and dispose are serialized by `_bootGate`, so a disposed source is never cancelled, and a cancel that lands
  between the signal and step 2 is seen through `_bootRequested`. B136 puts this worker in
  `SteamUiCoordinator` and passes the coordinator's `RunWhenReadyAsync` into the still-static
  `LibraryTabManager.SyncOnBootAsync` as a delegate. B137 moves the worker into `LibraryTabService` as
  `RequestBootSync`/`CancelBootSync`. There is no static forwarder at either step.
- **Tests:** rapid `RequestBootSync` calls run one sync after the last request and throw nothing. `CancelBootSync` stops a
  pass waiting for readiness, drops a request that was signalled but not yet started, and the next request runs.
  Dispose joins the worker. Filter:
  `--filter "FullyQualifiedName~SteamUiCoordinator"` (B136), then `--filter "FullyQualifiedName~LibraryTab"` (B137).
- **Plan v2:** B136, moved in B137.
- **Related:** U05-LFB-009, SESSION-021, STEAMHOST-010, verify batch problem 1.

### STEAMHOST-012: process-global mutable state in this domain

- **Severity:** medium
- **Where:**
  - `src/WSGM/Shell/SteamUiReadiness.cs:29-35` (static `_nextReady`/`_ready`; tests reset it in
    `SteamUiTransportGateTests.cs:7-16`);
  - `LibraryTabManager.cs:35, 73-76` (`Gate`, `_tabOrderWrites`, `_tabOrderPush`);
  - `LibraryBadges.cs` (static `_current`, `_revision`, `Changed`, with static subscriptions from the host at
    `SteamUiSessionHost.cs:349`, `:511`);
  - toolkit `SteamUiTransportSession.SetEnabled`/`Attach` driven by the session (`ShellSession.SteamUi.cs:83`,
    `ShellSession.cs:599`);
  - `SplashTheme._openImportSessions` (`SplashTheme.cs:43-51`).
- **Problem:** static state couples tests and makes the session's lifetime implicit, so a second session in one process
  or a test run sees leftovers.
- **Best solution:**
  - **Readiness statics:** become `SteamUiCoordinator` instance members (B136), with the pure decision kept as
    `SteamUiGatePolicy.ShouldOpen`.
  - **Tab and badge statics:** become `LibraryTabService` and `LibraryBadgeState` instances (B137, STEAMHOST-013).
  - **`SteamUiTransportSession`:** deleted by B053 in favour of the explicit `SteamClient`.
  - **`SplashTheme._openImportSessions`:** stays. It counts in-process Settings windows for a per-process staging folder,
    so its scope really is the process. Converting it fixes no defect and would only move the counter (simplify rule).
- **Tests:** `SteamUiTransportGateTests` becomes `SteamUiCoordinatorTests` with no static reset. `LibraryBadgesTests`
  constructs instances. Filters: B136 and B137 filters.
- **Plan v2:** B136 (readiness), B137 (tabs and badges), B053 (transport session). B136's file list names
  `src/WSGM/Core/SteamUiReadiness.cs (deleted)`; the file is `src/WSGM/Shell/SteamUiReadiness.cs`.
- **Related:** U05-LFB-005, STEAMHOST-013, 040.

### STEAMHOST-013: views and another manager call the static `LibraryTabManager` API directly

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/CardManagerView.cs:48-177`, `src/WSGM/Overlay/LibraryTabsView.cs:64-366`,
  `src/WSGM/Shell/SdFormatManager.cs:509` (`LibraryTabManager.MutateConfigAsync`, a generic config helper living in the
  tab manager), `src/WSGM/Shell/CardAcfWatcher.cs:266-276`, `ShellSession.cs:1033`.
- **Problem:** views reach a global service, and the static API blocks the instance conversion.
- **Best solution:** turn `LibraryTabManager` into an instance, `Shell/LibraryTabService.cs`, constructed by the
  composition root from the config store transaction port, a volume scanner port, the explicit `SteamClient` (B053) and
  `LibraryBadgeState`. The views receive an `ILibraryTabs` interface carrying exactly the methods they call today:
  `ListCardsAsync`, `RenameCardAsync`, `SetCardEnabledAsync`, `SetCardHiddenAsync`, `ForgetCardAsync`, `SaveTabOrder`,
  `SaveCustomTabsAsync`, `BuildTabOrder` and `SyncAllAsync`. Delete `MutateConfigAsync`; `SdFormatManager` uses the
  config store transaction directly. `BuildTabOrder` and `MergeDiscovery` stay static pure. The nested records
  (`CardView`, `TabOrderEntry`, `Discovered`, `MountedVolume`, `LibraryTabSyncResult`) stay internal in the new file.
- **Tests:** `CardNameAuthorityTests` move to `LibraryTabServiceTests` against the instance with fake ports. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LibraryTab|FullyQualifiedName~LibraryBadges|FullyQualifiedName~CardNameAuthority"`.
- **Plan v2:** B137 (waits for config B2a's instance store, critic conflict 26).
- **Related:** STEAMHOST-012, 014, 010.

### STEAMHOST-014: every library-tab sync rewrites `config.json` and triggers a full session reload

- **Severity:** medium
- **Where:** `src/WSGM/Shell/LibraryTabManager.cs:140-161` (`MergeDiscovery` plus the `KnownNativeTabs` union, always
  saved), `:320-335` (`ListCardsAsync` writes too), `:843` (`MutateConfigAsync`); `src/WSGM/Core/ConfigStore.cs:1194-1201`
  (`Mutate` always saves).
- **Problem:** each card notification, boot sync, builder change or opening of the card manager writes the user's
  preferences file. The file watcher then reloads about 30 consumers (`ShellSession.Config.cs:104-157`), which re-applies
  Steam surfaces and can start more syncs.
- **Best solution:** make `MergeDiscovery(AppConfig, List<Discovered>)` return `bool changed` (a card added, renamed,
  re-pathed or its `AppIds` changed). Make the `KnownNativeTabs` union a static `MergeNativeTabs(AppConfig, IReadOnlyList<NativeTab>)`
  that returns whether it added an entry or changed a title. The sync and `ListCardsAsync` transactions save only when
  either returns true, through the config domain's transaction that skips the save on `false`. `ListCardsAsync`
  projects the `CardView` list from the merged config whether or not it saved. Do not deep-compare whole configs: the
  merge functions know exactly what they changed.
- **Tests:** a sync over unchanged discovery writes nothing (the fake transaction counts saves). A new card writes once.
  Filter: B137 filter.
- **Plan v2:** B137.
- **Related:** U04A-LFA-007, STEAMHOST-013.

### STEAMHOST-015: uninstall leaves Steam-side state that WSGM owns

- **Severity:** medium
- **Where:** `src/WSGM/Core/Animations/AnimationOverrides.cs:79-185` (override
  `config\uioverrides\movies\bigpicture_startup.webm`, its `.wsgm` marker and the user's original moved to
  `.wsgm-original`); `src/WSGM/Shell/AnimationService.cs:855-917` and `AnimationsConfig.SteamSetAside` (Steam's own
  Startup Movie choice, kept only in WSGM's `config.json`); `src/WSGM/Core/Themes/ThemePaths.cs:68-112`
  (`steamui\themes_custom` re-pointed to `%LOCALAPPDATA%\WSGM\themes`, replacing a CSS Loader link). Nothing in setup,
  uninstall or `--restore-shell` references them.
- **Problem:** after uninstall Steam keeps playing WSGM's boot movie, the user's own override stays hidden as
  `.wsgm-original`, and `themes_custom` points into a folder the user may delete. This breaks the spirit of the
  never-strand rule for every uninstall and upgrade path.
- **Best solution (decided by D10: restore the files and also hand back Steam's startup-movie choice):** two halves.
  WSGM hands Steam's choice back while Steam UI is still reachable, and setup restores the files, which needs neither
  WSGM nor Steam.

  **Steam's choice, in WSGM.** Add `AnimationService.HandBackSteamChoiceAsync(bool uninstalling, Deadline deadline)`.
  Under `_sync` it reads `SteamSetAside` and whether a WSGM movie is selected. It does nothing when nothing is set aside,
  or when a WSGM movie is selected and `uninstalling` is false (the override keeps playing WSGM's movie with WSGM closed,
  so Steam's choice must stay aside). Otherwise it makes one call to the existing `access.Restore(...)`, which already
  refuses to overwrite a choice the user made anew in Steam, and handles the reply as `ReconcileSteamChoiceAsync`'s
  give-back branch does today: accepted clears `SteamSetAside`, anything else keeps it and logs one line. No retry. A
  kept set-aside is given back at the next ready edge by today's reconcile when no WSGM movie is selected, and is
  reported by setup otherwise.
  - Ordinary exit: B140's step 8 calls it with `uninstalling: false`, before the host retracts its patches and while
    the transport is open, under the remaining deadline. Only an unreachable Steam or a spent deadline skips it, and
    neither delays the safety steps, which run before step 8.
  - Uninstall: setup already raises `Local\WSGM.ExitForUninstall` before it closes Steam (`SetupEngine.StopWsgm`, then
    `CloseSteam`), and the session receives `ApplicationShutdownReason.Uninstall` (`ShellSession.Shutdown.cs:141-148`).
    Step 8 then calls it with `uninstalling: true`, so Steam's choice comes back whatever WSGM movie was selected. If
    the uninstall rolls back, the restarted WSGM sets it aside again at its next ready edge, as it does today.

  **Files, in setup.** Add an uninstall step to `WSGM.Setup` that only does file operations. The shared code goes in
  `src/WSGM.Install`, which both `WSGM` and `WSGM.Setup` reference: a new `static class SteamContentCleanup` with
  `RestoreBootMovie(string steamDirectory)` and `RemoveThemesLink(string steamDirectory, string themesRoot)`.
  `AnimationOverrides` calls the same marker helpers, so the rules exist once.
  1. If the override carries WSGM's `.wsgm` marker, delete it and the marker, then move `.wsgm-original` back when it
     exists. An unmarked override is the user's and is left alone.
  2. If `steamui\themes_custom` is a reparse point whose target is the uninstalling user's
     `%LOCALAPPDATA%\WSGM\themes`, delete the link itself (never its target). WSGM never recorded a CSS Loader target
     it replaced (`ThemePaths.EnsureSteamLink` only logs it), so nothing is recreated. Any other target or a real folder
     is left alone.
  3. Before the user's data is removed, read `SteamSetAside` from the uninstalling user's `config.json`. When it is
     still present (WSGM was not running, Steam was unreachable or the give-back failed), write one line to the
     uninstall log naming the movie to choose again in Steam's Startup Movie setting.

  The file step runs whether or not the user keeps data, because both files sit in Steam's folder, not WSGM's. Upgrade
  runs no uninstall step and its exit is an ordinary exit, because the new version takes the same state over.
- **Tests:** a `SteamContentCleanup` test over a temp Steam folder covers a WSGM-marked override restored with the
  original, an unmarked override left alone, a link to WSGM's themes folder removed with its target intact, and a link
  to another folder left alone. `AnimationServiceTests` with a fake `SteamStartupMovieAccess`: exit with no movie
  selected and a set-aside calls `Restore` once and clears it; exit with a movie selected calls nothing; uninstall with
  a movie selected calls `Restore`; an unreachable or refused give-back keeps `SteamSetAside`. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Animations|FullyQualifiedName~AnimationService|FullyQualifiedName~Themes|FullyQualifiedName~Setup"`.
- **Plan v2:** B138; decided: D10, restore the files and hand back Steam's startup-movie choice at exit (no WSGM movie
  chosen) and at uninstall. The exit and uninstall call lands with B140's step 8, and the setup half with the install
  batches' uninstall work.
- **Related:** INSTALL-V-003 (same uninstall path), never-strand group (B007).

### STEAMHOST-016: a partial startup-movie set-aside loses the user's original Steam choice

- **Severity:** medium
- **Where:** toolkit `Client/SteamStartupMovie.cs:65-67, 103-110, 142-146`; consumer
  `src/WSGM/Shell/AnimationService.cs:880-887` (ignores `result.Choice` when `!Accepted`).
- **Problem:** when the set-aside writes the first setting and then fails, Steam's original choice was already changed,
  but WSGM records nothing. The user's Startup Movie choice is lost for good.
- **Best solution:** the toolkit returns the original choice in every reply that follows a first write (TOOLKITCS-016,
  "partial outcomes keep ownership"). In `ReconcileSteamChoiceAsync`, on the set-aside path (`plays`), persist
  `SteamSetAside` whenever `result.Choice` is present, whether `Accepted` is true or not, then record the failure text as
  the page error. The restore path is unchanged: a failed give-back keeps the stored `SteamSetAside` (today the
  `!Accepted` branch writes nothing), because clearing it on a partial restore would lose the original. Persisting the
  original is bookkeeping, not a retry; nothing is written to Steam again automatically.
- **Tests:** a fake startup-movie client returns `Accepted=false` with a choice, and `SteamSetAside` is saved and the
  error shown. Filters: `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamStartupMovieTests"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Animation"`.
- **Plan v2:** B053.
- **Related:** U02B-SUTC-011, TOOLKITCS-016, STEAMHOST-V-005, SETTINGS-001 (a Settings save must not revert
  `SteamSetAside`).

### STEAMHOST-V-001: an invalid theme update journal stops the session from starting, on every start

- **Severity:** medium
- **Where:** `src/WSGM/Core/Themes/ThemeInstaller.cs:181-208` (`Recover`), `:161-167` (inner `Recover` in `Unpack`'s
  catch), `src/WSGM/Core/Themes/ThemeLoader.cs:64-74`, `src/WSGM/Shell/ThemeService.cs:764-778`, `:949-960`,
  `src/WSGM/Shell/ShellSession.cs:730-736`.
- **Problem:** `Recover` throws `InvalidDataException` when the journal is over 128 KiB, deserializes to null, has a bad
  id, names more than 256 entries or is inconsistent. `ThemeLoader.Load` catches only `IOException`,
  `UnauthorizedAccessException` and `JsonException`. The exception escapes `ThemeService.Start()`, which runs on the UI
  thread inside session startup, so the session fails. The marker file stays, so every later start fails the same way.

  The install path has a second escape. The inner `Recover(root)` in `Unpack`'s catch can throw the same exception past
  `StartWorkAsync`'s filter. `_busy` then stays true, and every theme operation answers "Another theme operation is still
  running" until restart.
- **Best solution:**
  - In `ThemeLoader.Load`, add `InvalidDataException` to the catch filter (no catch-all), so any recovery failure becomes
    the existing "Theme update recovery remains pending" load error and `Load` returns usable.
  - In `Unpack`'s catch, wrap the inner `Recover(root)` in a try with the same filter and append its message to the
    `ThemeStoreException`.
  - Remove the `journal.Names.Count > 256` check and the `stream.Length > 128 * 1024` check (no-arbitrary-limits). The
    journal is WSGM's own file, written by `Unpack`, not untrusted input, and D2 does not list a journal bound; with
    only the name cap gone, a journal past roughly two thousand names would still fail recovery on every start. B018's
    spec text says to keep the 128 KiB bound; this finding overrides that because D2 is "exactly this list".
  - Deserialize the journal through a source-generated JSON context.
- **Tests:** a garbage or inconsistent journal leaves `Load` usable with exactly one load error, and session start
  survives. A journal with more than 256 names, and one larger than 128 KiB, round-trips. A failing inner `Recover` still clears the busy slot. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Themes|FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService"`.
- **Plan v2:** B018.
- **Related:** CONFIG-V-002 (same defect), CONFIG-038, STEAMHOST-031 (journal cap part), V-002, U05-LFB-003.

### CRIT-002: sound packs drop and refuse valid content through arbitrary caps

- **Severity:** medium (critic finding, owned by this area in plan v2)
- **Where:** `src/WSGM/Core/Sounds/SoundPackLibrary.cs:173-177` (an asset over 1 MiB silently counts as "missing"),
  `:191-194` (the whole pack throws past a 16 MB "playback budget"), `:221` (install refuses more than 512 entries);
  `src/WSGM/Shell/SoundPackService.cs:332-339` (`Load()` first publishes an empty override set with a new revision),
  `:201` (`ImportAsync` refuses a ZIP over 64 MB).
- **Problem:** the delivery to Steam is already chunked (toolkit `DeliverAsync` parts), so none of these caps protects a
  transport. They silently drop sounds or refuse whole packs. Publishing an empty set first briefly retracts the pack on
  every refresh, select or preview.
- **Best solution:** delete the 1 MiB per-asset skip, the 16 MB budget with its `total` counter, the 512-entry count and
  the 64 MB ZIP size check. Keep only the zip-bomb guard: expanded-bytes bound (`SoundPackLibrary.cs:229`), symlink
  refusal and path containment, as the D2 exception that refuses and never truncates. The 256 KiB `pack.json` read bound
  (`:58`) stays under D2's manifest 256 KiB entry. In `SoundPackService.Load()`, delete the leading empty-set
  assignment and wrap the read and `BuildOverrides` in a `try` whose `catch` publishes the empty set with a new revision
  and rethrows, so a failed rebuild still retracts stale bytes ("Failure to rebuild content must retract stale bytes")
  and a successful one replaces the set in one publication.
- **Tests:** a pack with a 2 MiB asset delivers it. A pack over 16 MB total loads. An archive with 600 entries installs.
  An archive whose expanded size exceeds the guard is refused. A successful reload raises one publication, and a
  reload whose rebuild throws leaves the empty override set published. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SoundPack"`.
- **Plan v2:** B138; decided: D2 accepts exactly plan v2's byte-bound list, which names the sound-pack zip-bomb guard
  on expanded bytes, and every other cap goes.
- **Related:** critic section 1.2, M01-28.

---

## Low

### STEAMHOST-006: plugin Steam UI modules are read once at host construction

- **Severity:** low (verifier lowered from medium latent)
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:261-262`, `:1268-1271`; `src/WSGM/Shell/CommonPluginSteamUiSource.cs:210-223`
  (`ReadModules`); bridge allowlist fixed at construction (`SteamUiSessionHost.cs:321-326`).
- **Problem:** `ReadModules()` snapshots admitted, ready plugins once, after common-plugin startup
  (`ShellSession.cs:303-316`). A plugin enabled later gets no modules until the next session. If a plugin restarts, its
  captured modules keep handlers into the retired instance. This is latent, because no in-repo plugin implements
  `IPluginSteamUi`.
- **Best solution (maintainer decision: register plugin modules dynamically when the plugin becomes ready):** the module
  set gains one replace operation, and the plugin source tells the host when the plugin part of it changed.
  - **Toolkit (parent half of B054, beside the manager rework).** `SteamUiModuleRuntime.ReplaceModules(SteamUiModuleSet next)`
    swaps the set it routes commands and publications through under `_moduleGate`; it cancels the in-flight requests of
    every module that left and drops those modules from `_failedModules`, so a restarted plugin's modules start
    unfaulted (quarantine still lasts for the module instance, with no reset API). `SteamUiPatchManager` gets
    `Unregister(string patchId)`, which retracts the patch under its resource gate, then removes the entry; `Register`
    already works after the first synchronization. `SteamUiBridgeHost.SetAllowedCommands(vocabulary)` swaps the C#
    vocabulary copy and bumps a vocabulary revision that joins `assetHash` in the bootstrap configuration and in
    `bridge.ts`'s reuse check, so the next synchronization reinstalls the bridge with the new `allowed` map. That is the
    path a new asset hash takes today, and the manager re-applies every enabled gate after a bridge reinstall. The
    runtime's replace applies in order: register added patches, swap the bridge vocabulary and the module set, then
    unregister removed patches.
  - **WSGM.** `CommonPluginSteamUiSource.Refresh()` (which STEAMHOST-026 leaves running only from `OnChanged` and the
    constructor) also computes the plugin module list, keyed by registration and `Context.Generation`, with today's
    `ReadModules` filter, and raises a new `ModulesChanged` event after the lock when that list differs from the last
    one. A plugin that stops or restarts leaves the list, so the retired instance's handlers go with it. The host
    composes `new SteamUiModuleSet([.. catalogModules, .. pluginModules])` on each change and calls
    `_runtime.ReplaceModules`. A plugin module whose patch id or command collides with an earlier one is dropped with one
    log line, as host-owned ids are dropped today, so one package cannot take every surface down. `_pluginPatchIds`
    (`SteamUiSessionHost.cs:1529`) is recomputed from the same list, and `ApplySwitchStates()` (STEAMHOST-002) runs
    after the replace so added patches get their switch. `WsgmSteamModuleCatalog.Create` (STEAMHOST-005) returns only
    host modules.
  - Update the XML docs of `src/WSGM.Plugin.Sdk/PluginSteamUi.cs`, `src/WSGM.Plugin.Sdk/README.md` and
    `docs/plugin-system.md`: a plugin's Steam UI modules are registered when it becomes ready and removed when it stops.
    No SDK API change.
- **Tests:** toolkit: `ReplaceModules` routes a command to an added module, refuses one to a removed module, retracts
  the removed patch, and a re-added module starts unfaulted; a vocabulary change makes the next bootstrap a reinstall,
  not a reuse. WSGM: a plugin turning ready after session start raises `ModulesChanged` once and its command reaches it;
  a restarted plugin's old handler is gone; a colliding plugin patch is dropped and the other surfaces stay. Filters:
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamUiBridge"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPluginSteamUiSource|FullyQualifiedName~SteamUiSessionHost"`.
- **Plan v2:** B086 for the WSGM half, after the toolkit half in B054. This replaces plan v2's read-at-start
  documentation step.
- **Related:** critic conflict 5 (overridden on this point), verify batch problem 6, STEAMHOST-002, 005, 026.

### STEAMHOST-017: the runtime asset hash check can abort session startup, and the hash constant is redundant

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamUiAssetCatalog.cs:15-35` (`NativeQamBootstrapSha256` constant, compare-and-throw);
  `src/WSGM/Shell/SteamUiSessionHost.cs:321-326`; `eng/build-steam-assets.mjs:221-275` (the builder rewrites the C#
  constant).
- **Problem:** a mismatch throws out of the host constructor and ends the session. The constant duplicates
  `steam-assets:check`, which already reviews the generated `.js`, and the builder has to rewrite C# source to keep the
  constant in step.
- **Best solution:** make `SteamUiAssetCatalog.LoadNativeQamBootstrap()` return a `SteamUiInjectedAsset` built from the
  embedded source and `Convert.ToHexString(SHA256.HashData(bytes))` computed at load. That hash is the injected-asset
  identity the bridge uses. Keep the "embedded bootstrap is missing" throw, which means a broken build. Delete
  `NativeQamBootstrapSha256`, the compare-and-throw, and the builder's `hashPattern` rewrite and check
  (`eng/build-steam-assets.mjs:221-238`, `:275`), plus `catalogPath` if nothing else uses it. No test references the
  constant today (`SteamUiAssetTests` only calls `LoadNativeQamBootstrap()`), so no test is deleted. The hash stays
  load-bearing: the bridge compares it with the previously published one to replace a stale script after a WSGM update
  (`SteamUiInjectedAsset` remarks), and a hash of the embedded bytes serves that exactly. Host construction failure
  containment is B086's (V-007).
- **Tests:** the hash computed at load equals the SHA-256 of the embedded resource and is what the bridge receives.
  `npm run steam-assets:check` still passes. Filter:
  `--filter "FullyQualifiedName~SteamUiAsset|FullyQualifiedName~SteamUiSessionHost"` plus `npm run steam-assets:build`,
  `npm run steam-assets:check`, `npm run steam-assets:claims`.
- **Plan v2:** B139.
- **Related:** U05-LFB-003, STEAMHOST-V-007.

### STEAMHOST-018: download sort is 220 lines of JavaScript in a C# raw string, outside the asset pipeline

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamDownloadSort.cs:20-246` (resident `window.__wsgm` global with its own focusable and enum
  scans), `InstallExpression` re-running `SteamUiModuleResolver.CreateExpression` on every apply, `SteamDownloadSortPatch`
  (`:300-384`).
- **Problem:** this JS escapes the reviewed asset, its formatting and claims checks. It keeps a resident global namespace
  and re-resolves modules on every apply.
- **Best solution:** move the script into `src/WSGM/Core/SteamUiAssets/Source/download-sort.ts`, registered with
  `registerGate` and using the bridge's resolver and `elements` gate. `SteamDownloadSortPatch` becomes the standard gate
  patch, enabled and disabled like the other WSGM gates. The `__wsgm` namespace and the C# raw string are deleted. Do not
  add `DependsOn`: the B054 manager applies the bridge first and removes it last on every path (critic conflict 5).
  Fragment discovery in `eng/build-steam-assets.mjs` is automatic. B054 already removes the patch's `Version` field.
- **Tests:** emitted-asset fixtures for install, remove and the refused-position report, and `SteamDownloadSortPatchTests`
  ported to the gate. Filter: `--filter "FullyQualifiedName~SteamDownloadSort|FullyQualifiedName~SteamUiAsset"` plus the
  three `steam-assets` npm checks.
- **Plan v2:** B139 (after V-003's cap removal in B032).
- **Related:** U02A-SUTC-005, STEAMHOST-V-003, critic conflict 5.

### STEAMHOST-019: WSGM-owned surfaces use the toolkit's `steam-ui.` patch-id prefix

- **Severity:** low
- **Where:** `steam-ui.themes`, `steam-ui.animations`, `steam-ui.artwork-browser`, `steam-ui.wsgm-graphics`,
  `steam-ui.library-import`, `steam-ui.wsgm-settings` in Shell `Steam*Surface.cs` and the matching `Source/*.ts`
  constants, against `wsgm.download-sort`, `wsgm.chord-reset`, `wsgm.controller-caps`,
  `wsgm.steam-input.glyph-style`; `CommonPluginSteamUiSource.HostOwnedPatches`.
- **Problem:** the toolkit reserves its own prefix (B054, TOOLKITCS-038), and WSGM ids sitting under it can collide with
  toolkit surfaces.
- **Best solution:** rename the six ids to `wsgm.themes`, `wsgm.animations`, `wsgm.artwork-browser`, `wsgm.graphics`,
  `wsgm.library-import` and `wsgm.settings`, changing C#, TS and `HostOwnedPatches` together, plus the id table in
  `docs/steam-cef-system.md:653-658` and `:1064` and the key in `tools/WsgmLibTest/artwork-browser-fixture.json:13`.
  The ids are not persisted, so users see nothing.
- **Tests:** update id assertions in `SteamUiSessionHostTests`, `SteamUiAssetTests` and `CommonPluginSteamUiSourceTests`.
  Filter: B139 filter.
- **Plan v2:** B139.
- **Related:** TOOLKITCS-038.

### STEAMHOST-020: inconsistent visibility in the executable

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamThemesSurface.cs` (public static class, public records, public `ISteamThemesBackend`),
  the other WSGM `Steam*Surface.cs`, `LibraryTabManager` and `LibraryTabSyncResult`.
- **Problem:** public types in an exe whose neighbours are internal blur which types are a contract.
- **Best solution:** make them `internal`; `InternalsVisibleTo` already serves the tests. `LibraryTabManager` becomes the
  internal `LibraryTabService` in B137 anyway.
- **Tests:** a build only.
- **Plan v2:** B139.
- **Related:** none.

### STEAMHOST-021: non-Steam consumers depend on QAM helper types

- **Severity:** low (verifier corrected the claim about `NativeQamTdpState`)
- **Where:** `NativeQamUi.OverrideId`/`DeviceOverrideId` used by `src/WSGM/Overlay/OverlayWindow.Sources.cs:72` and
  `src/WSGM/Shell/DeviceOverlayBridge.cs:1222-1228`; `NativeQamUi.ValidInteger` used by `SteamGraphicsService.cs:274`;
  `DeviceCoordinatorNativeQamTdpService.Project` and `NativeQamTdpState` read by the RTSS OSD
  (`ShellSession.Performance.cs:183-190`, `ObservedWatts`/`DesiredWatts`).
- **Problem:** overlay, OSD and graphics code reaches into the Steam QAM namespace for profile and capability
  projections, and that blocks the `NativeQamSemanticServices` split.
- **Best solution:** move `OverrideId`, `DeviceOverrideId` and `ValidInteger` into `CapabilityProjection`. Move
  `DeviceCoordinatorNativeQamTdpService.Project` into a power-limit projection beside it, returning a record that keeps
  raw `ObservedWatts` and `DesiredWatts` (both nullable) plus the descriptor range. The OSD reads that record. The QAM
  power-limit service maps it to the toolkit slider state, and only that mapping applies the ceiling fallback (V-008).
  Delete `NativeQamTdpState` and `ToRange`. Split the rest of the file per the target table:
  - `PerformanceNativeQamAdapter`;
  - `PowerLimitNativeQamService`;
  - `DeviceControlsNativeQamService`;
  - `AutoTdpNativeQamService`;
  - `ControllerTargetNativeQamService`;
  - static `NativeQamText` (`ProgressText`, `StatusText`, `CommandResult`, `RunAsync`), in `Shell/NativeQam/` with
    namespace `WSGM.Shell`.

  Run it against the concrete `DeviceCoordinator` with no interim ports (critic conflict 4).
- **Tests:** port the existing projection tests per type and add "the OSD reads raw observed and desired watts". Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQam|FullyQualifiedName~CapabilityProjection|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceOverlayBridge"`.
- **Plan v2:** B087 (before DEVICE-B6/B8).
- **Related:** STEAMHOST-V-008, 022, 023, critic conflict 4.

### STEAMHOST-022: redundant capability subscriptions and an unnecessary UI-thread hop for AutoTDP status

- **Severity:** low
- **Where:** `src/WSGM/Shell/NativeQamSemanticServices.cs:736-737`, `:982`, `:1290-1302` (three services subscribe to
  `Capabilities.Changed`); `:1430-1434` (`OnAutoTdpStatusChanged` posts to the Avalonia dispatcher).
- **Problem:** three subscriptions do one job. The dispatcher hop delays publication behind UI work and ties a projection
  to Avalonia, although `QueueStatePublication` is thread-safe (`SteamUiModuleRuntime.QueuePublication`).
- **Best solution:** in B084, replace `Dispatcher.UIThread.Post(OnChanged)` with a direct `OnChanged()`. In B087, the
  catalog subscribes once to the coordinator's capability change and queues one publication, and the three per-service
  subscriptions are deleted. `NativeQamText.RunAsync` (today `NativeQamUi.RunAsync`) stays for the radio and audio
  managers, whose collections are UI-thread owned.
- **Tests:** an AutoTDP status change raised off the UI thread queues a publication without a dispatcher. One capability
  change raises one publication. Filter: `--filter "FullyQualifiedName~NativeQam"`.
- **Plan v2:** B084 (dispatcher hop), B087 (single subscription).
- **Related:** STEAMHOST-021.

### STEAMHOST-024: misleading command results

- **Severity:** low
- **Where:** `src/WSGM/Shell/NativeQamSemanticServices.cs:521-541` (`ApplyProfileToggleAsync` reports "no identifiable
  application is running" whenever `SetGameEnabledAsync` returns false), `:504-513` (`ResetProfileAsync` reports success
  with no profile owner), `:783-794` (`SetUnifiedModeAsync` always reports success); `:145` (`Profiles` is optional).
- **Problem:** `SetGameEnabledAsync` returns false when the running application changed under the request or the edit
  did not take. The user is told no application is running, which is false. `SetManualTdpModeAsync` throws
  `InvalidOperationException("Paired TDP is unavailable.")`, which becomes an unhandled command failure instead of a
  refusal.
- **Best solution:** make `PerformanceServiceNativeQamAdapter.Profiles` a required constructor argument, since the
  session always has one, and delete the null branches. In `ApplyProfileToggleAsync`, keep the existing null-target text
  and return "The per-application profile could not be changed." when `SetGameEnabledAsync` returns false.
  `ResetProfileAsync` keeps reporting success after `ResetAsync`, as its remark intends. `SetUnifiedModeAsync` catches
  `InvalidOperationException` from `SetManualTdpModeAsync` and returns a refusal carrying its message. There is still no
  readback gate: success means the write path ran. Update the constructor call sites in `SteamUiSessionHost` and
  `NativeQamPerformanceAdapterTests`.
- **Tests:** a toggle with a mismatched application gets the generic refusal text. Unified mode while unavailable is
  refused with the reason. Filter: `--filter "FullyQualifiedName~NativeQam"`.
- **Plan v2:** B084.
- **Related:** verify batch problem 4.

### STEAMHOST-025: `WsgmSteamSettingsService` caps ordered choices at 64

- **Severity:** low
- **Where:** `src/WSGM/Shell/WsgmSteamSettingsService.cs:616` (`GetArrayLength() > 64` refuses).
- **Problem:** the 64 cap violates the no-arbitrary-limits rule: a plugin declaring more than 64 ordered choices can
  never be reordered from Steam's settings page. The review also called the `default` branch (`:633-635`) untyped. That
  part is not a defect: the converted value goes through `_configurePlugin` to `CommonPluginSteamUiSource.ConfigureAsync`
  (`ShellSession.cs:781-783`), which refuses any JSON kind that does not match `setting.Kind` in `TryReadValue`
  (`CommonPluginSteamUiSource.cs:102-105`, `:403-420`) before anything reaches the plugin.
- **Best solution:** delete `value.GetArrayLength() > 64 ||` and keep the every-item-is-a-string check. Add no
  declared-choice check in WSGM: the permutation rule belongs to the plugin's own configuration path
  (`PluginSettingKind.OrderedChoices`), and a second copy here would be new policy. Leave the `default` branch and the
  `JsonText`/`JsonNumber` helpers as they are.
- **Tests:** an ordered list of 100 string items is passed through, a non-string item is refused, and a number sent to a
  boolean setting is still refused by the downstream `TryReadValue`. Filter:
  `--filter "FullyQualifiedName~WsgmSteamSettings|FullyQualifiedName~CommonPluginSteamUiSource"`.
- **Plan v2:** B084.
- **Related:** STEAMHOST-026.

### STEAMHOST-026: `CommonPluginSteamUiSource` route bound, stale comment, reads that mutate, inconsistent lookups

- **Severity:** low
- **Where:** `src/WSGM/Shell/CommonPluginSteamUiSource.cs:256` (`Path.Length: > 1 and <= 256`), `:243-245` ("cap" and
  "quota" comment), `:131, 171, 202, 235` (`Refresh()` inside every `Read*`), `:145` against `:189-192` (indexer against
  `TryGetValue`), `:112`, `:293` (`Deadline.After(TimeSpan.FromSeconds(10))`).
- **Problem:** the 256 bound refuses valid plugin routes. The comment describes a quota that does not exist. Reads mutate
  subscriptions and ids. The indexer can throw where the sibling read is safe.
- **Best solution:**
  - Drop `<= 256`, keeping `Length > 1`, the leading `/`, a non-empty `Template`, `Override: false` and the `default`
    refusal. Delete the "cap"/"quota" comment and the "longer than Steam's own bound" clause in `ReadPages`' remarks.
  - Call `Refresh()` only from `OnChanged` (already under `_gate`, raised by `CommonPluginManager.Changed` and
    `PluginHost.HealthChanged`) and once in the constructor under `_gate`, so the first read after construction is
    populated without a change event. `Read*`, `ConfigureAsync` and `ActivateAsync` then take the lock and project.
    `Refresh()` is also where STEAMHOST-006 detects a changed plugin module list and raises `ModulesChanged`.
  - Use `TryGetValue` in `ReadExtensionsTab`.
  - Keep `Deadline.After` on the process `ActiveClock`: the SDK keeps it (critic conflict 18), so there is no context
    clock to take.
- **Tests:** a 300-character route is admitted. Reads with no change event make no subscription change. Filter:
  `--filter "FullyQualifiedName~CommonPluginSteamUiSource"`.
- **Plan v2:** B084.
- **Related:** plan claim C13, critic conflict 18.

### STEAMHOST-027: ownership inversion of `CommonPluginSteamUiSource`

- **Severity:** low
- **Where:** `src/WSGM/Shell/ShellSession.cs:1074-1076` (created inline in the host's argument list),
  `src/WSGM/Shell/SteamUiSessionHost.cs:527-531` (the host disposes it), `ShellSession.cs:1094-1098`
  (`_pluginSteamUi.Changed += wsgmSettings.Refresh`, never unsubscribed), `:1084-1092` (`_steamPowerMenu` and
  `_steamGraphics` assigned inside the argument list).
- **Problem:** the creator and the disposer differ, a subscription leaks, and side-effecting assignments are hidden in an
  argument list.
- **Best solution:** the composition root creates `CommonPluginSteamUiSource`, `_steamPowerMenu` and `_steamGraphics` as
  separate statements, passes them in through `SteamUiBackends` and disposes them in the session's disposal, after the
  host. The host stops disposing it. The session unsubscribes `wsgmSettings.Refresh` in the same place.
- **Tests:** host dispose leaves the plugin source undisposed, and session disposal disposes it once. Filter: B086
  filter.
- **Plan v2:** B086.
- **Related:** STEAMHOST-005.

### STEAMHOST-028: `DisposeAsync` is not idempotent under concurrency

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:437-445`.
- **Problem:** a volatile check-then-act lets two callers both run `DisableAsync`. `_disposed` is set only after the
  disable, so events keep flowing into the runtime during it.
- **Best solution:** add `private readonly Lock _disposeGate = new();` and `private Task? _disposal;`, and make
  `DisposeAsync` take `_disposeGate`, run `_disposal ??= DisposeCoreAsync();` and return `new ValueTask(_disposal)`.
  Every caller gets the same task. Do not use `LazyInitializer.EnsureInitialized(ref _disposal, factory)`: under a race
  that overload may run the factory more than once and keep only one result, which would start two disposals. The lock
  is held only until `DisposeCoreAsync` reaches its first await; `CloseAdmission()` is synchronous and takes only
  `_switchGate`, so there is no lock-order issue. `DisposeCoreAsync` calls `CloseAdmission()` first (STEAMHOST-001), which stops new events
  reaching the runtime, then `DisableAsync`, then disposes runtime, bridge and transport subscriptions.
- **Tests:** two concurrent `DisposeAsync` calls run one disable (counted by the fake transport). Filter: B136 filter.
- **Plan v2:** B136.
- **Related:** STEAMHOST-001.

### STEAMHOST-029: theme and animation services duplicate one work mechanism with divergent save-failure semantics

- **Severity:** low (verifier narrowed the divergence claim)
- **Where:** `src/WSGM/Shell/ThemeService.cs:743-761`, `:926-983`; `src/WSGM/Shell/AnimationService.cs:974-1050`,
  `:419-427`, `:486-489`, `:794-797`, `:988-990`.
- **Problem:** both services carry their own `_busy`, `_notice`, `_error`, `_revision`, `StartWorkAsync`, `Publish` and
  `Refuse`. On a save failure, `ThemeService.ChangeConfig` refuses and keeps state. `AnimationService` keeps the unsaved
  value in memory every time. `DeleteAsync` applies the override only when saved, but `SetBootAsync` and
  `SetBootVolumeAsync` apply it to Steam's override regardless.
- **Best solution:** add `Shell/ContentWorkSlot.cs` (internal sealed) holding the busy flag, notice, error, one tracked
  task and the revision. It exposes `TryStart(Func<CancellationToken, Task<SteamUiCommandResult>> work)`, which clears
  busy in `finally` and records any exception's message as the error (V-002), plus `Publish`, `Refuse` and `JoinAsync`
  for disposal. Both services use it and delete their copies. One rule applies to both: a choice whose save failed is
  refused, memory keeps the previous value and nothing is applied to Steam's override.
  `AnimationService.ChangeConfig` (STEAMHOST-009) therefore swaps `_config` only on success, and `SetBootAsync`,
  `SetBootVolumeAsync` and `DeleteAsync` apply the override only when saved. The page looks the same: the same busy and
  error texts.
- **Tests:** for both services, a failing config writer refuses, memory is unchanged and no override is written. A
  background install is joined by dispose. Filter:
  `--filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService"`.
- **Plan v2:** B138.
- **Related:** STEAMHOST-009, 010, 030, V-002.

### STEAMHOST-030: `ThemeService` lock and thread discipline

- **Severity:** low
- **Where:** `src/WSGM/Shell/ThemeService.cs:491-623`, `:998-1006` (`_loader.SaveConfig`/`Delete`/`Load` under `_sync`),
  `:764-810` (`Start()` loads every theme synchronously on the UI thread, `ShellSession.cs:730-736`), `:703-704`,
  `:756-758`, `:813-823` (`_config` read, modified and written outside the lock from the UI and bridge threads);
  `src/WSGM/Core/Themes/InstalledTheme.cs` `Enable`/`Disable` (drop `SaveConfig` errors).
- **Problem:** file I/O under the state lock blocks publication reads. A slow theme folder delays session start on the
  UI thread (accepted, see the first bullet). `_config` races between threads. Theme state save errors vanish.
- **Best solution:**
  - Keep `ThemeService.Start()` synchronous at composition. It reads a local folder under `%LOCALAPPDATA%`, its slow
    part (the junction) already runs off-thread, and there is no "loading" notice to reuse: moving it to the background
    would publish an empty theme list and a busy page at every start, a visible change the UI rule forbids. Only take
    its loader calls out of `_sync` like the rest below.
  - Move loader file calls out of `_sync`: compute under the lock, do I/O outside it, publish under it.
  - Read and swap `_config` only under `_sync` (immutable clone swap, as `AnimationService` does).
  - `InstalledTheme.Enable`/`Disable` return the `SaveConfig` error and `ThemeService` reports it as the slot error.

  `AnimationService.Start` stays synchronous before Steam starts (`ShellSession.cs:748-750`).
- **Tests:** `ReadState` is not blocked while a slow loader fake runs. Start publishes the full list once, with no busy
  state. A save failure in `Enable` is shown. Filter: `--filter "FullyQualifiedName~ThemeService|FullyQualifiedName~Themes"`.
- **Plan v2:** B138.
- **Related:** STEAMHOST-029, verify batch problem 7.

### STEAMHOST-031: an arbitrary dependency depth cap and a silent duplicate drop in the theme installer and loader

- **Severity:** low. The verifier raised the journal-cap part to medium; it moved to STEAMHOST-V-001 and B018.
- **Where:** `src/WSGM/Core/Themes/ThemeInstaller.cs:51-57` (`depth > 8`), `:76-79` (recursion);
  `src/WSGM/Core/Themes/ThemeLoader.cs:490-505` (duplicate-suffix loop).
- **Problem:** `depth > 8` refuses a legitimate dependency chain; cycles are already prevented, because `local` gains
  each installed name before the recursion. A theme whose name already has five suffixed duplicates is dropped with no
  `ThemeLoadError`.
- **Best solution:** delete the `depth` parameter and the cap. Add a `HashSet<string> visitedIds`, keyed by store id and
  checked on entry (`if (!visitedIds.Add(id)) return;`). That covers a store answering two names for one id. In the
  loader, keep the five-suffix search (CSS Loader parity). When no free suffix exists, add
  `new ThemeLoadError(folder, "A theme with this name is already loaded.")` and log it instead of dropping silently. The
  journal's 256-name cap is removed in B018.
- **Tests:** a dependency chain of 12 installs. A cycle ends through the visited set. A sixth duplicate is reported as a
  load error. Filter: `--filter "FullyQualifiedName~Themes"`.
- **Plan v2:** B138 (journal part B018).
- **Related:** STEAMHOST-V-001, CONFIG-038.

### STEAMHOST-033: WSGM gates hand-roll method wrapping

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamUiAssets/Source/chord-reset.ts:28-70`,
  `src/WSGM/Core/SteamUiAssets/Source/controller-caps.ts:110-133`.
- **Problem:** neither gate uses the toolkit's ownership primitives, which are already in the same emitted asset
  (`eng/build-steam-assets.mjs:55-60` concatenates `ownership.ts` ahead of every fragment). Each keeps its own
  `hooked`/`original` closure state and a `__wsgmWrapped` property instead of `claimMember`'s marker and stored
  original. The marker is what lets a gate recognise its own earlier wrapper. A bridge that is replaced normally runs
  `prior.dispose` and unhooks (`bridge.ts:24-27`), but after a JS context reload the bridge, a window property, is gone
  without its dispose running while `SteamClient` keeps the old wrapper (`ownership.ts` `supplyNamespace` remarks). The
  next bridge's gate then finds that wrapper and, with closure state, wraps it a second time instead of reclaiming it.
  The review's further claim, that the toolkit helper makes `remove()` report failure when a foreign wrapper sits on
  top, is wrong: `releaseMember` answers ok when the member is no longer ours (`ownership.ts:257-271`), exactly like
  today's `unhook`, and nothing can unwrap from under a foreign wrapper.
- **Best solution:** replace each `hook`/`unhook` pair with `claimMember(target, member, keys, original => wrapper)` and
  `releaseMember(target, member, keys)` using one `ClaimKeys` pair per gate, and delete `hooked`, `original` and
  `__wsgmWrapped`. Keep the gates' behaviour and their remove order as TOOLKITJS-008 fixes it for the toolkit gates:
  release first, forget only after a successful release.
- **Tests:** emitted-asset fixtures: claim then remove restores the original; a second gate instance claiming the same
  member reuses the stored original (one wrapper in the chain, one reset request per call). Filter:
  `--filter "FullyQualifiedName~SteamUiAsset"` plus the three `steam-assets` npm checks.
- **Plan v2:** B139.
- **Related:** U02A-SUTC-025, U03B-SUTS-001, TOOLKITJS-008, TOOLKITJS-020.

### STEAMHOST-034: duplicated patterns in the TS page fragments

- **Severity:** low
- **Where:** `Source/themes.ts` and `Source/animations.ts` (act helper, tabs, card, detail, browse, page shell);
  `Source/artwork-browser.ts:8-12` and `Source/library-import.ts:11-31` (module-level `ui` plus listener sets so modals
  can read state).
- **Problem:** two copies of the same page helpers drift apart.
- **Best solution:** fold only the byte-identical helpers into one WSGM fragment, `Source/page-kit.ts` (act helper, tab
  strip, card, detail shell). Leave the modal-state pattern as it is unless the toolkit already exports a channel for it.
  The rendered output must be identical, which the emitted-asset checks verify. Skip any fold that would change markup.
- **Tests:** `npm run steam-assets:build`, `npm run steam-assets:check`, `npm run steam-assets:claims`; `SteamUiAssetTests`
  unchanged.
- **Plan v2:** B139.
- **Related:** none.

### STEAMHOST-041: storage tests enumerate real disks

- **Severity:** low (verifier lowered from medium)
- **Where:** `tests/WSGM.Tests/.../SteamStorageBridgeTests.cs:36-55`, `SteamUiSessionHostTests.cs:215-236` (real
  `RemovableDriveManager` and `SdFormatManager`); `src/WSGM/Shell/SteamStorageBridge.cs:76` (`_formats.Refresh()` in the
  constructor); `SdFormatManager.cs:171-221`.
- **Problem:** the tests start read-only `NativeStorage.ListDiskInterfaces()`/IOCTL queries against real hardware. That
  contradicts the class's own "never touches real storage" remark and `tests/WSGM.Tests/AGENTS.md`. Nothing is mutated.
- **Best solution:** two seams, both constructor parameters, and no change to how production composes the managers:
  - `SdFormatManager` gets an internal constructor taking `Func<List<FormatTarget>> readTargets`; the public
    parameterless one passes today's `ReadTargets`. Its `Apply(List<FormatTarget>)` becomes `internal`, like
    `RemovableDriveManager.Apply`, so tests feed format targets the way they already feed drives.
  - `SteamStorageBridge` takes `Func<IReadOnlyList<StorageVolume>> describeVolumes`; the session passes
    STEAMHOST-008's `DescribeLocalVolumes`.

  Tests keep the real `RemovableDriveManager` without calling `Start()` (its constructor touches no hardware) and feed
  it through `Apply`. The bridge constructor no longer calls `_formats.Refresh()`; the session calls it once after
  composition (`ShellSession.cs:720`), where the first refresh belongs. Changed constructors and call sites:
  `SdFormatManager` (new internal overload only), `SteamStorageBridge` (`ShellSession.cs:720`,
  `SteamStorageBridgeTests.cs:39`, `SteamUiSessionHostTests.cs:220`).
- **Tests:** the storage tests run with fakes only: every test `SdFormatManager` is built with a fake reader, no test
  calls `RemovableDriveManager.Start()`, and every bridge gets a fake volume list. Filter:
  `--filter "FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~SteamUiSessionHost"`.
- **Plan v2:** B084.
- **Related:** STEAMHOST-007, 008, V-006, verify batch problem 4.

### STEAMHOST-042: behaviour coverage gaps

- **Severity:** low
- **Where:** `LibraryTabManager.SyncAllDetailedAsync`, the `SaveTabOrder` chain and rename orchestration (only
  `MergeDiscovery`/`TrySetMarkerLabel` are covered, in `CardNameAuthorityTests.cs`); the master switch, Big Picture
  transitions and transport gate in `ShellSession.SteamUi.cs`; host dispose and shutdown ordering; storage commands.
  `SteamUiAssetTests` are source-token checks.
- **Problem:** the orchestration most likely to regress in the coordinator and tab-service moves has no real-owner
  tests.
- **Best solution:** add real-owner tests with fake transport, window probe, clock and ports in the batch that moves each
  owner:
  - **B136 coordinator:** master off retracts before close; Big Picture request hold, release, restore; cold start waits
    for the window; `RunWhenReady` attempts once per edge; admission closes before device disposal; no CEF call after
    the transport closes.
  - **B137 tab service:** sync writes only on change; the last order push wins in press order; cancellation on the
    desktop trip; rename writes the marker first and makes no Steam write when the marker write fails.
  - **B084 storage commands:** eject, format refusal when not allowed, and adopt.

  Keep the source-token asset tests secondary and add emitted-asset fixtures where B139 changes JS.
- **Tests:** as listed, each under its batch filter.
- **Plan v2:** B139 (resolving batch). The tests themselves land in B084, B136 and B137.
- **Related:** U05-LFB-006.

### STEAMHOST-044: `SteamUiText.Of` is a trivial toolkit public helper

- **Severity:** low
- **Where:** 14 call sites in `src/WSGM/Shell/NativeQamSemanticServices.cs` (`:103`, `:494`, `:656`, `:673`, `:687`,
  `:688`, `:946`, `:1139`, `:1382`, `:1395`, `:1406`, `:1423`, `:1576`, `:1619`); toolkit `Surfaces/SteamUiText.cs`.
- **Problem:** a one-line null-or-blank helper is public toolkit API, and WSGM wraps literals with it that can never be
  blank.
- **Best solution:** the toolkit deletes `SteamUiText` (TOOLKITCS-057) and its surfaces normalize null or blank text at
  serialization. In WSGM, drop the wrapper at every site whose argument is a literal or interpolation, and pass nullable
  values straight through, because the surface now normalizes them. Plan v2 counts three sites where the text is used
  before serialization (compared or concatenated). Those keep a local `string.IsNullOrWhiteSpace(x) ? string.Empty : x`.
- **Tests:** existing NativeQam projection tests (blank diagnostics still render as empty). Filter:
  `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true` and `--filter "FullyQualifiedName~NativeQam"`.
- **Plan v2:** B056.
- **Related:** U02B-SUTC-032, TOOLKITCS-057.

### STEAMHOST-046: card services sit in the Steam UI partial

- **Severity:** low
- **Where:** `src/WSGM/Shell/ShellSession.SteamUi.cs:551-598` (`ApplyCardServices`: ACF watcher, volume monitor,
  `MessageWindow.Create()`, drive/format watcher wiring).
- **Problem:** card and storage ownership lives in the Steam UI file, and `ShellSession.SteamUi.cs` is dissolved in
  B136.
- **Best solution:** move `ApplyCardServices` into the session's card/storage owner. If the storage domain's owner has
  not landed yet, it stays in `ShellSession.cs`. It reads `coordinator.IsMasterEnabled` and subscribes to the
  coordinator's `Changed` instead of the session's `_cefMasterEnabled`. `MessageWindow` creation follows SESSION-B3's
  single owner (critic conflict 14).
- **Tests:** none beyond the coordinator tests. The card watchers start and stop with the master switch. Filter: B136
  filter.
- **Plan v2:** B136.
- **Related:** U05-LFB-014, critic conflict 14.

### STEAMHOST-047: reload applies Steam UI with split, order-dependent semantics

- **Severity:** low (verifier: the "transient re-apply" claim is not shown; the real defect is the overwrite race in
  STEAMHOST-002)
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:120-140`.
- **Problem:** `ApplyCefMasterSwitch` runs asynchronously while `ApplyHostSteamUi` and the other surfaces apply
  immediately and outside the master gate. Native QAM `Apply(false)` is never called when the master switch goes off; the
  code relies on the background `DisableAsync`. An ungated apply during that retraction is one of STEAMHOST-002's
  overwrite sources.
- **Best solution:** reload calls one `SteamUiCoordinator.ApplyConfig(SteamUiConfig)`. The coordinator computes
  `SteamUiSurfaceSwitches` and, under its gate, orders the edges: on master off it retracts and then closes the
  transport; on master on it opens and then applies. Otherwise it calls `host.Apply(switches)`. Until B136, B085 already
  routes reload through the single session method.
- **Tests:** a reload with the master switch off then on, interleaved with a pending retraction, ends with the switches
  of the last config. Filter: B085 and B136 filters.
- **Plan v2:** B085.
- **Related:** STEAMHOST-002, 003, V-010.

### STEAMHOST-V-002: content work slots can stay busy forever

- **Severity:** low
- **Where:** `src/WSGM/Shell/ThemeService.cs:926-983`, `src/WSGM/Shell/AnimationService.cs:1007-1050`.
- **Problem:** both `StartWorkAsync` bodies catch a closed list of exception types and clear `_busy` only after it. Any
  other exception, such as V-001's `InvalidDataException` or an `ArgumentException` from a path, escapes the `Task.Run`
  unobserved and leaves `_busy == true`. The page then refuses all work for the rest of the session.
- **Best solution:** in both `StartWorkAsync` bodies, move the `_busy = false` and publication into a `finally`, and add
  a final `catch (Exception ex)` that records `ex.Message` as the slot error (no rethrow; it is the task's top level).
  This is bookkeeping, not a retry. B138 then folds both into `ContentWorkSlot` (STEAMHOST-029).
- **Tests:** a work delegate that throws `ArgumentException` leaves the page usable with the message shown. Filter:
  `--filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService"`.
- **Plan v2:** B018.
- **Related:** STEAMHOST-V-001, 029.

### STEAMHOST-V-003: arbitrary 768 KiB size cap on the generated Steam UI asset

- **Severity:** low
- **Where:** `eng/build-steam-assets.mjs:128-134` (`maximumAssetBytes`), `:257-264`.
- **Problem:** the bound is self-described as "a sanity bound ... not a limit anything downstream imposes" and has
  already been raised twice. The asset is 645,950 bytes (84 percent). B057 and B139 add JS, so the gate would fail a
  correct change.
- **Best solution:** delete `maximumAssetBytes` and the upper-bound test. Keep the non-empty, UTF-8, no-BOM and
  single-reviewed-file checks.
- **Tests:** `npm run steam-assets:check`.
- **Plan v2:** B032 (before B057 and B139).
- **Related:** TOOLKITJS-037, BUILD-V-001.

### STEAMHOST-V-004: 64 MB cap on importing the user's own boot movie

- **Severity:** low
- **Where:** `src/WSGM/Core/Animations/AnimationLibrary.cs:179-182` (reusing `AnimationRepoClient.MaximumMovieBytes`,
  `AnimationRepoClient.cs:29`); the download path `AnimationRepoClient.cs:167` (`BoundedHttp.CopyAsync`).
- **Problem:** a local `.webm` over 64 MB is refused with "larger than the 64 MB safety limit". The import is a
  `File.Copy` of the user's own file, so the cap protects nothing and drops user content.
- **Best solution:** delete the size check from the local import path. The repository download keeps its 64 MB bound on
  third-party input, which D2 lists as an accepted byte bound that refuses and never truncates. The local path no longer
  references `MaximumMovieBytes`. The overlay's preview of a local library movie carries the same check
  (`src/WSGM/Overlay/OverlayMediaPreview.cs:348-370`), so an imported movie over 64 MB would import and then fail to
  preview; delete that local-branch check in the same change (the overlay finding that reworks `OverlayMediaPreview`
  keeps it out of the local file copy it introduces). The remote preview download keeps the D2 bound.
- **Tests:** importing a 70 MB sparse temp file succeeds. The download refusal test is unchanged. Filter:
  `--filter "FullyQualifiedName~Animations"`.
- **Plan v2:** B138; decided: D2 keeps the 64 MB animation repository download bound and removes the local cap.
- **Related:** none.

### STEAMHOST-V-005: Steam's startup-movie set-aside is not attempted again when Steam's settings store is still loading

- **Severity:** low (verifier rated low-medium)
- **Where:** `src/WSGM/Shell/AnimationService.cs:855-887` (`ReconcileSteamChoiceAsync`); toolkit
  `Client/SteamStartupMovie.cs` (`ReadChoice` answers `ok:false, "Steam has not loaded its settings yet."`).
- **Problem:** the attempt runs at the transport's ready edge, when the Big Picture window is visible, but Steam's stores
  can lag that edge. A reachable refusal returns `true` ("done") because only `!Reachable` returns false. Nothing tries
  again until the next Steam start or choice change, so the user's Steam Startup Movie keeps overriding WSGM's and the
  page shows an error.
- **Best solution:** no WSGM retry machinery. The toolkit's `ReadChoice` prefix (`SteamStartupMovie.cs:58-67`) polls for
  `window.settingsStore?.clientSettings` and `GetClientSetting` every 250 ms for up to 5 s, inside the existing 10 s
  evaluation `Budget`, the way `LibraryTabManager.LibraryReadyProbe` waits for its stores, and only then answers the
  existing "Steam has not loaded its settings yet." error. Both scripts share the prefix, so restore gains the same
  wait. WSGM keeps its one attempt per ready edge and pairs it with
  STEAMHOST-016's persistence.
- **Tests:** toolkit `SteamStartupMovieTests`: a store that becomes available inside the wait succeeds. Filter:
  `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamStartupMovieTests"`.
- **Plan v2:** B053.
- **Related:** STEAMHOST-016.

### STEAMHOST-V-006: the storage bridge reads UI-owned collections off the UI thread

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamStorageBridge.cs:168`, `:241`, `:298-299`, `:600`; writers
  `SdFormatManager.Refresh` (posts `Apply` to the dispatcher, `SdFormatManager.cs:187`) and `RemovableDriveManager`.
- **Problem:** `_drives.Drives` and `_formats.Targets` are observable collections mutated on the UI thread and enumerated
  from the publication loop and command threads. An overlapping enumeration can throw, which on the publication path
  quarantines the storage module for the session.
- **Best solution:** the same change as STEAMHOST-007. The bridge builds an immutable snapshot of both collections and
  the id table on the UI thread on each `CollectionChanged`, and every off-thread reader uses only the snapshot. One owner
  thread, no lock.
- **Tests:** covered by STEAMHOST-007's tests (rebuild during resolve and publication, with no throw).
- **Plan v2:** B084.
- **Related:** STEAMHOST-007, WINSVC-004.

### STEAMHOST-V-007: host construction subscribes to session-owned events before its throwable steps

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:285`, `:308-313`, `:317` (subscriptions), then `:321-326` (asset load,
  which can throw) and `:340` (`_runtime` assigned).
- **Problem:** if construction throws after `:317`, the session-owned `NativeQamBrightnessService` keeps a
  `QueueStatePublication` handler on a half-built host whose `_runtime` is null, and the network service stays subscribed
  to the radio manager. Today the throw ends the session. Once B086 contains it, every brightness change would throw a
  `NullReferenceException`.
- **Best solution:** in the new constructor, build the asset, bridge, patch manager and runtime first. Subscribe to the
  change sources returned by `WsgmSteamModuleCatalog.Create` only as the constructor's last step, and unsubscribe them
  all in `DisposeCoreAsync`. In the composition root, construct the host inside a try: on failure, log, leave
  `_steamUi` null and continue (Steam surfaces degrade alone, plan L83). Nothing was subscribed, so nothing dangles.
- **Tests:** a throwing asset loader leaves the session usable, and a later brightness change raises nothing. Filter:
  `--filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~WsgmSteamModuleCatalog"`.
- **Plan v2:** B086.
- **Related:** U05-LFB-003, STEAMHOST-005, 017.

### STEAMHOST-V-008: the slider's ceiling fallback reaches the OSD as the reported wattage

- **Severity:** low
- **Where:** `src/WSGM/Shell/NativeQamSemanticServices.cs:902-905`
  (`observed = valid(observed) ?? desired ?? maximum`); `src/WSGM/Shell/ShellSession.Performance.cs:183-190`
  (`tdp.ObservedWatts ?? tdp.DesiredWatts`).
- **Problem:** `observed` is never null while the projection is available. On a device without readback (the Ally
  family) and with no desired value, the RTSS OSD shows the descriptor maximum as the current TDP, a value nobody wrote
  or read. This is not a readback gate, but the display is wrong.
- **Best solution:** the power-limit projection record (STEAMHOST-021) carries raw `ObservedWatts` and `DesiredWatts`,
  validated against the range but never filled from the ceiling. Only the QAM slider mapping applies
  `observed ?? desired ?? maximum`, so the slider still has a position on a device that cannot read its limits. With
  neither value, the OSD shows no TDP figure.
- **Tests:** with no observed and no desired value, the OSD projection has null watts and the slider sits at the
  maximum. Filter: B087 filter.
- **Plan v2:** B087 (B057 states the rule on the toolkit side).
- **Related:** STEAMHOST-021, no-readback rule.

### STEAMHOST-V-009: a 200 W ceiling of WSGM's own on power-limit descriptors

- **Severity:** low
- **Where:** `src/WSGM/Shell/NativeQamSemanticServices.cs:891-892` (`maximum > 200` marks the descriptor incompatible);
  the duplicated toolkit-side check.
- **Problem:** the device package's descriptor is the authority for its range, as device limits are in the reference.
  A package publishing a maximum above 200 W loses the TDP slider and, through `Current`, the AutoTDP switch, with an
  "incompatible" status. The check is a plausibility cap of WSGM's own, not a shape check.
- **Best solution:** delete `|| maximum > 200` here and its toolkit duplicate. Keep the shape checks: integer kind, watt
  unit, writable, `minimum >= 1`, `minimum < maximum`, `step >= 1`, `step <= maximum - minimum`.
- **Tests:** a 250 W descriptor projects an available slider. Filter: `--filter "FullyQualifiedName~NativeQam"` and the
  B057 toolkit filter.
- **Plan v2:** B057.
- **Related:** TOOLKITJS R11.

### CRIT-006: browse sessions link their loads to the per-request token

- **Severity:** low (critic note, owned by this area in plan v2)
- **Where:** `src/WSGM/Shell/ThemeBrowseSession.cs:118` (`OpenAsync`) and `:284` (`Fetch`), both
  `CreateLinkedTokenSource(_lifetime.Token, token)`; toolkit `SteamUiModuleRuntime.cs:178-179` (the request CTS is
  disposed, not cancelled). `AnimationBrowseSession` does not link the request token (its fetches run on
  `AnimationService._shutdown`), so it needs no change.
- **Problem:** the fire-and-forget reads are linked to the request token, and their cancellation path never clears
  `_loading`/`_detailLoading`. This is benign today, but any toolkit change that cancels request tokens on completion would
  leave a stuck spinner.
- **Best solution:** in both places create the per-read source from `_lifetime.Token` alone, dropping the request
  token. A newer fetch or open still cancels the previous one through `_queryWork`/`_detailWork`, as today. The request
  returns at once as today, and the read runs until it completes, is superseded or the session ends. No new state.
- **Tests:** completing or cancelling the originating request does not cancel the browse load, and disposal does.
  Filter: `--filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService|FullyQualifiedName~Browse"`.
- **Plan v2:** B138.
- **Related:** STEAMHOST-010.

---

## Nit

### STEAMHOST-023: duplicated range validation and timeout constants

- **Severity:** nit
- **Where:** `src/WSGM/Shell/NativeQamSemanticServices.cs:59-67` (`NativeQamUi.ValidInteger`), `:835-845`
  (`SetLimitAsync`), `:1076-1084` (`SetIntegerAsync`); the 5-second `CommandTimeout` at `:713` and `:973`;
  `SetRefreshRateAsync` (`:250-263`) duplicating the `RefreshRateHz` branch (`:461-466`).
- **Problem:** three copies of `(value - min) % step` checks and two timeout constants can drift apart.
- **Best solution:** one `CapabilityProjection.ValidInteger(value, min, max, step)` (moved in STEAMHOST-021) used by all
  three. One `NativeQamText.CommandTimeout`. `SetRefreshRateAsync` calls the shared `RefreshRateHz` path.
- **Tests:** existing NativeQam tests. Filter: B087 filter.
- **Plan v2:** B087.
- **Related:** STEAMHOST-021.

### STEAMHOST-032: `ThemePaths.CreateLink` can leave `cmd mklink` running

- **Severity:** nit
- **Where:** `src/WSGM/Core/Themes/ThemePaths.cs:122-157`.
- **Problem:** `WaitForExit(10000)` timing out leaves the child running, and nothing reports it.
- **Best solution:** extract the process part into
  `internal static bool RunJunctionCommand(ProcessStartInfo start, TimeSpan timeout)`. When `WaitForExit(timeout)`
  returns false, it calls `process.Kill(entireProcessTree: true)`, logs "Themes: mklink did not finish in time and was stopped." and returns
  false. `CreateLink` passes 10 s. There is no native junction helper in the repository, and adding one is not needed.
  This is simpler than plan v2's fake process port: the timeout parameter alone makes the path testable.
- **Tests:** `RunJunctionCommand` with a harmless `cmd.exe /c ping -n 30 127.0.0.1` and a 100 ms timeout returns false
  and the process has exited. Filter: `--filter "FullyQualifiedName~ThemePaths"`.
- **Plan v2:** B138.
- **Related:** none.

### STEAMHOST-035: write-only state in two Steam backends

- **Severity:** nit (verifier: tests read the properties; only `LibraryBadgeBackend` has the equality guard)
- **Where:** `src/WSGM/Shell/LibraryBadges.cs:83-99` (`LibraryBadgeBackend.BigArt` and its guard),
  `src/WSGM/Shell/HomeCarousel.cs:55-75` (`HomeCarouselBackend.Last`); tests `LibraryBadgesTests.cs:111-117`,
  `HomeCarouselTests.cs:51-53`.
- **Problem:** production never reads these properties. `BigArt`'s equality guard duplicates `Log.Change`'s per-key
  deduplication (`Log.cs:218-232`).
- **Best solution:** delete `BigArt`, its guard and `Last`. The handlers call `Log.Change` and return `Applied`. The two
  tests assert only that the report is accepted; "logged only on change" is `Log.Change`'s own behaviour, covered by its
  tests.
- **Tests:** the two tests trimmed as described. Filter: `--filter "FullyQualifiedName~LibraryBadges|FullyQualifiedName~HomeCarousel"`.
- **Plan v2:** B137. Its spec says "write-only test state stays test-only"; deleting the two properties is the simpler
  reading and leaves nothing test-only in production code.
- **Related:** none.

### STEAMHOST-036: the sound integration status strings appear four times

- **Severity:** nit
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:627-629`, `:871-873`, `:926-931`; `src/WSGM/Shell/ShellSession.cs:743-746`.
- **Problem:** four copies of the same three sentences, set from different places.
- **Best solution:** add `SoundPackService.SetHostState(bool hostOn, SteamUiPatchSnapshot? soundPatch)`, which derives
  the text: host off gives "Steam integration is off. The sound-pack selection is saved."; a verified patch gives
  "Steam sound override connected. Each replacement is checked before playback."; otherwise the patch failure or
  "Steam sound overrides are unavailable; stock sounds remain in use."; and before any synchronization, "Waiting for
  Steam sound integration.". The host's edge (STEAMHOST-003) and its `Synchronized` handler (STEAMHOST-004) call it; the
  session's literal goes. The strings stay identical.
- **Tests:** a table over (host on, patch state) gives the exact strings. Filter: `--filter "FullyQualifiedName~SoundPack|FullyQualifiedName~SteamUiSessionHost"`.
- **Plan v2:** B087.
- **Related:** STEAMHOST-003, 004.

### STEAMHOST-037: dead term in the synchronization loop

- **Severity:** nit
- **Where:** `src/WSGM/Shell/SteamUiSessionHost.cs:956-958`.
- **Problem:** `_downloadSortEnabled ||` can never be true in the `!BootstrapWanted()` branch, because `BootstrapWanted`
  includes it.
- **Best solution:** removed with the loop in B054 (STEAMHOST-004). The global switch is derived in `ApplySwitchStates()`
  (STEAMHOST-002).
- **Tests:** none.
- **Plan v2:** B054.
- **Related:** STEAMHOST-002, 004.

### STEAMHOST-038: small inaccuracies

- **Severity:** nit
- **Where:**
  - `src/WSGM/Shell/SteamUiSessionHost.cs:1142-1149`: the comment "Steam's game menu. Declared unconditionally" sits
    above the CPU boost block;
  - `:1489`: `Quarantined` makes a redundant `Count > 0` check;
  - `src/WSGM/Shell/NativeQamSemanticServices.cs:377`: `EnableFrameLimitWatts` returns fps;
  - `:205`: `ApplyVariableRefreshRate { get; set; }` sits beside `init` siblings;
  - `src/WSGM/Shell/ThemeService.cs:887`: the literal "Invalid State" is returned as a preset name.
- **Problem:** misleading names and comments, and an unexplained magic string.
- **Best solution:**
  - move the comment to the game-menu declaration;
  - the `Quarantined` part goes with B054's deletion;
  - rename to `EnableFrameLimitFps`, updating its callers;
  - make it `init`;
  - the "Invalid State" literal is the selected-preset text the page shows when more than one preset is enabled
    (CSS Loader's own wording), so the UI keeps it. Name it as a private constant, for example
    `MultiplePresetsSelected`, with a one-line comment saying it is CSS Loader's text.
- **Tests:** a build and the B084 filter.
- **Plan v2:** B084.
- **Related:** STEAMHOST-004.

### STEAMHOST-039: duplicate applies in the Big Picture restore

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ShellSession.SteamUi.cs:257-261`, `:361` (`RestoreSteamUiAfterBigPictureAsync` calls
  `ApplyHostSteamUi`, then `ApplySteamUiSurfacePreferences` calls it again).
- **Problem:** two applies in one restore, a second overwrite opportunity.
- **Best solution:** the coordinator's restore calls `ApplyConfig` once, keeping the retract-then-close and
  open-then-apply order and every existing remark from `ShellSession.SteamUi.cs:129-175`.
- **Tests:** a Big Picture request hold, then release, then restore gives one host `Apply`. Filter: B136 filter.
- **Plan v2:** B136.
- **Related:** U05-LFB-025, STEAMHOST-003.

### STEAMHOST-040: two notions of readiness

- **Severity:** nit
- **Where:** `src/WSGM/Shell/SteamUiReadiness.cs:44`, `:93-174` (the edge followed by `RunWhenReadyAsync`) against
  `IsReady` (a live `Steam.IsRunning && IsBigPictureVisible`) used by `CardAcfWatcher.cs:246`,
  `CardVolumeMonitor.cs:331` and `ShellSession.cs:679`.
- **Problem:** consumers sample the window directly and can disagree with the gate's observed edge.
- **Best solution:** `SteamUiCoordinator` exposes `IsReady` (the last observed gate state) and `Changed`. The card
  watchers and the KeepAwake lambda read that. The 1 s window sample stays inside the coordinator as the only sampling
  point, behind `ISteamWindowProbe`.
- **Tests:** coordinator tests with a fake window probe: `IsReady` follows the gate edge, not a direct probe call.
  Filter: B136 filter.
- **Plan v2:** B136.
- **Related:** STEAMHOST-012.

### STEAMHOST-043: test placement

- **Severity:** nit
- **Where:** the `LibraryTabManager` tests in `CardNameAuthorityTests`; `AnimationServiceTests` borrowing
  `ThemeStoreClientTests.StubHandler`; `SteamUiTransportGateTests` in `Core/` for a Shell type.
- **Problem:** tests are hard to find, and a stub is shared through another test class.
- **Best solution:** move `StubHandler` to `tests/WSGM.Tests/Fakes`. The tab tests land in `LibraryTabServiceTests`
  (B137), and the gate tests become `Shell/SteamUiCoordinatorTests` (B136). Only moves, no assertion changes.
- **Tests:** the moved tests pass under their filters.
- **Plan v2:** B139 (resolving batch). The moves happen with B136 and B137.
- **Related:** U04A-LFA-034.

### STEAMHOST-V-010: Game Mode entry applies the Steam surfaces twice, once outside the master gate

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ShellSession.cs:1142-1154`.
- **Problem:** `ReleaseSteamUiBigPictureHold()` starts the gated restore, which applies everything. The handler then
  calls `ApplyNetworkIndicator` and `ApplySteamUiSurfacePreferences` again synchronously, outside `_cefMasterGate`. That
  is one of STEAMHOST-002's overwrite sources.
- **Best solution:** delete the two direct calls. The restore's single `ApplyConfig` covers them (B085 single session
  method, later the coordinator).
- **Tests:** Game Mode entry causes one host `Apply`. Filter: B085 filter.
- **Plan v2:** B085.
- **Related:** STEAMHOST-002, 003, 047.

---

## Refuted or no-change

- **STEAMHOST-045** (fold ids are persisted English titles, `QuickAccessFolds.cs:26-30`): no change. The published
  section ids stay today's strings, so the stored fold keys already match and no fold-id migration is needed (plan v2
  Appendix A, critic conflict 8, TOOLKITJS R1).

No finding was refuted by the verifier.
