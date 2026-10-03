# Domain review: WSGM-side Steam UI host and semantic services

Reviewer: Claude Opus 5.5, read-only, baseline `1329813f` (clean `master`). Scope read in full:
`Shell/SteamUiSessionHost.cs` (1,617), `Shell/NativeQamSemanticServices.cs` (1,657), `Shell/ShellSession.SteamUi.cs`
(675), `Shell/SteamUiReadiness.cs` (184, the current stand-in for the planned `SteamUiCoordinator`, which does not
exist yet), `Shell/WsgmSteamSettingsService.cs` (867), `Shell/CommonPluginSteamUiSource.cs` (476),
`Shell/SteamThemesSurface.cs` (442), `Shell/SteamStorageBridge.cs` (637), `Shell/LibraryTabManager.cs` (1,208),
`Shell/ThemeService.cs` (1,336), `Shell/AnimationService.cs` (1,074), `Shell/LibraryBadges.cs` (100), `Core/SteamGlyphCss.cs`,
`Core/SteamDownloadSort.cs`, `Core/SteamUiAssetCatalog.cs`, `Core/Themes/**` (12), `Core/Animations/**` (6), `Core/Splash*.cs`
(3, the static/limit parts), `Core/SteamUiAssets/Source/**` (10, page fragments skimmed for structure, gates read in full), the
relevant parts of `ShellSession.cs`/`.Config.cs`/`.Shutdown.cs`/`.Performance.cs`, the toolkit runtime publication loop, and the
tests: `SteamUiSessionHostTests`, `NativeQamSemanticServicesTests`, `WsgmSteamSettingsServiceTests`,
`CommonPluginSteamUiSourceTests`, `SteamStorageBridgeTests`, `ThemeServiceTests`, `AnimationServiceTests`,
`SteamDownloadSortPatchTests`, `SteamUiAssetTests`, `SteamUiTransportGateTests`, `CardNameAuthorityTests`, and the
Themes/Animations/Splash Core tests (sampled).

Ledger status: the audit units that cover this domain (`U08A-STA`, `U08B-STA`, `U09-STB`) are "unstarted Claude part; 0 saved
IDs" (`audit-coverage.md` L22-24). A01/A02/A02S01 hold no semantic finding for these files (structural signals only). So most
findings below are NEW. Where a prior ledger entry exists, I cite it and confirmed it against the current code.

---

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "`SteamUiSessionHost` is 1,617 lines, holds roughly 30 backend dependencies and duplicates module quarantine/retraction policy" (refactor-plan L45) | accurate | 1,617 lines; 28 constructor parameters (`SteamUiSessionHost.cs:224-252`), about 45 fields (L24-162). The quarantine mirror is `_failedPatchGate`/`_failedPatchIds` (L64-68, L1466-1491) | none |
| C2 | "`NativeQamSemanticServices.cs` holds 1,657 lines of projections/adapters" (L45) | accurate | 1,657 lines, 7 types | Add that `NativeQamUi.OverrideId` and the TDP projection have non-Steam consumers (STEAMHOST-021), so the split must move them out of the QAM namespace, not only cut the file up |
| C3 | "WSGM policy leaks into toolkit `components.ts`, one 2,490-line closure" (L45) | accurate (toolkit side; U03B-SUTS-002/003) | WSGM supplies no labels, sections or FPS policy today. The host declares only the row modules (`SteamUiSessionHost.cs:1063-1108`) | The WSGM side needs a new publication of today's labels, section ids/titles/icons/order and the explicit Valve-FPS suppression flag, which C12 covers |
| C4 | "`ShellSession.Shutdown.cs` stops device/GPU providers before the Steam UI host" (L42) | accurate | AutoTDP is disposed at `ShellSession.Shutdown.cs:117-131`, the device coordinator at L139-162, common plugins at L166, GPU at L180. `_steamUi.DisposeAsync()` follows only at L355-370 (U05-LFB-020) | Close Steam host admission at T0 synchronously (C9) |
| C5 | "toolkit client calls consult ambient `SteamUiTransportSession`" (L47) | accurate | WSGM drives it at `ShellSession.cs:599` (Attach), `ShellSession.SteamUi.cs:83` (SetEnabled), `LibraryTabManager.cs:228` (EvaluateAsync) and `ShellSession.cs:759-760` (StartupMovie). Also through `SteamLibraryTabs`, `LibraryFilter.EvaluateAsync` and `SteamLibraryFolders` | List these consumers in api-integration (C29) |
| C6 | `SteamUiCoordinator` row: "WSGM readiness/master gate and one generation host" (L68) | partially (target only) | No such type exists. The responsibilities live in `ShellSession.SteamUi.cs` (gate, master switch, BP transitions, tab boot sync, card services) and the static `SteamUiReadiness.cs` | Rename "one generation host" to "one session-scoped Steam UI host per transport". Toolkit patch-manager generations stay inside the toolkit |
| C7 | `SteamUiSessionHost`: "Generation-scoped module/bridge composition" (L69) | inaccurate (as design) | The host is session-scoped today. It is created once (`ShellSession.cs:1042`), and one bridge/manager/runtime survives every SharedJSContext generation (`OnGenerationChanged` L865-898) | Drop "generation-scoped". Re-creating the host per generation would add a lifecycle with no defect behind it, which breaks the simplify rule. Keep one host per transport |
| C8 | "toolkit owns patch dependencies and module quarantine evidence" (L69, L135) | accurate (direction) | WSGM workarounds: two-pass `DisableAsync` (L857-862, U02A-SUTC-002) and the quarantine mirror (U02A-SUTC-034) | Add the WSGM consumer obligation: `SteamDownloadSortPatch` and the glyph/chord/caps gates must declare `DependsOn` the bridge patch (STEAMHOST-018) |
| C9 | Shutdown order "close surface/command admission ... remove generation host patches before closing transport" (L91, planning-corrections B3 "Close all ordinary admission immediately at shutdown T0 ... UI/Steam commands") | partially | Patch removal already precedes transport disposal (`Shutdown.cs:355-391`). Missing pieces: the host has no synchronous admission close, and `_steamUi.DisposeAsync` runs after device/GPU disposal | Add `SteamUiSessionHost.CloseAdmission()` (runtime command/publication off, `CancelAllInflight`, no CEF round trip) at T0. Patch retraction then runs in the feature phase under the outer deadline (STEAMHOST-001) |
| C10 | "UI snapshots are detached values with revisions" (L115) | partially | Revisions exist for themes, animations, library import, sounds and the folds publication (`SteamUiSessionHost.cs:1227-1251`). Storage, network, audio, Bluetooth, TDP, perf and device controls have none and are re-read on every publication round (`SteamUiModuleRuntime.cs:290-318`) | Give the storage projection a cached revision (STEAMHOST-008). The cheap in-memory projections may stay revisionless; do not add revisions where reads are cheap |
| C11 | "All long-running work is tracked and joined" (L115) | inaccurate (current state) | Nine untracked fire-and-forget sites (STEAMHOST-010) | Keep as the objective. The batches in section 5 name each site |
| C12 | "Host publications provide labels, sections/icons/order, stable fold IDs, selectable options and explicit Valve-FPS suppression. WSGM supplies today's exact data" (L141) | accurate (requirement) | `QuickAccessFolds` persists English titles (U03B-SUTS-008) | WSGM must add the host publication and migrate the old title keys to stable section ids in `quick-access-folds.json`. That is a requirements.md item 11 migration, owned by the config domain |
| C13 | "Route length remains unbounded as current tested behavior" (L141) | contradicted by WSGM code | `CommonPluginSteamUiSource.Admissible` refuses plugin page paths longer than 256 (`CommonPluginSteamUiSource.cs:256`), and its comment still talks about a "cap"/"quota" (L243-245) | Remove the 256 bound and the stale comment, keeping the shape checks (STEAMHOST-026) |
| C14 | "Refusals ... produce bounded existing status/description messages" (L141) | ambiguous | WSGM refusal texts are full sentences (e.g. `SteamStorageBridge.cs:114-131`, `NativeQamSemanticServices.cs:527-541`) | State that "bounded" means transport byte-safety only. Host refusal text is never truncated (no-arbitrary-limits) |
| C15 | File picker: "WSGM explicitly selects `ExistingWindowsPaths`" (planning-corrections B2) | accurate (requirement), consumer not yet changed | `SteamFilePickerSurface.Module(HostSteamUiEnabled)` takes no policy (`SteamUiSessionHost.cs:1192-1195`) | Add the explicit policy argument at the host when T03 lands (batch STEAMHOST-B7) |
| C16 | "Unavailable/null badge state draws nothing; do not invent Internal before publication" (L143) | accurate | WSGM seeds a first reading before the host exists (`ShellSession.cs:1030-1040`) and supplies its own "Internal" label (`LibraryBadges.cs` `Build`) | none on the WSGM side; the toolkit default changes |
| C17 | "Runtime exposes quarantine/state/reason and owns corresponding manager retraction; WSGM stops mirroring `_failedPatchIds`" (L135) | accurate | `SteamUiSessionHost.cs:1462-1491`, and `Quarantined()` is consulted in `SetPatchStates`/`OnThemesChanged` (L1456, L1532) | none |
| C18 | "The manager coalesces generation/sync notifications; host policy supplies enablement" (L135) | accurate, and it enables a deletion | The host runs its own signal/loop (`_synchronizeSignal`, `_signalPending`, `SynchronizeLoopAsync`, L134-135, L900-972) | Once the manager schedules its own synchronization, delete the host loop. Post-sync reconciliation (sound status, screensaver report, WSGM menu readiness, perf observation) then hangs off one manager "patch states changed" notification (STEAMHOST-B7) |
| C19 | S01 ownership: "`SteamUiSessionHost*`, `SteamUiReadiness*`, `NativeQam*`, `WsgmSteamSettingsService*`, `CommonPluginSteamUiSource*`, `CommonPluginActions*`, `LibraryBadges*`; Core/Steam* client adapters" (pre-AM01 task-briefs L780) | partially | Missing: `ShellSession.SteamUi.cs` (the actual gate/master code), `SteamStorageBridge.cs`, the WSGM `Steam*Surface.cs` files in Shell, `SteamPanelFoldsBackend.cs`, `Core/SteamUiAssetCatalog.cs`, `Core/SteamUiAssets/Source/*.ts`, `eng/build-steam-assets.mjs` | Add them |
| C20 | S01 step 2 "remove mirrored failed patch set/two-pass bridge workaround" | accurate | L857-862, L1466-1491 | Depends on toolkit T01; sequenced in B7 |
| C21 | S02 "Replace LibraryTabManager static task chain and badge singleton with borrowed session instances" | accurate | `LibraryTabManager.cs:35, 73-76`; `LibraryBadges.cs` static `_current`, `_revision`, `Changed` | Also move the overlay callers and `SdFormatManager`'s use of `LibraryTabManager.MutateConfigAsync` (STEAMHOST-013) |
| C22 | S02 "Give content browser/download/preview/catalog/render projection separate owned lifetimes and injected filesystem/network/client/sink ports" | over-engineering | `ThemeService` and `AnimationService` already take injected loader/library, client, config read/write and Steam directory (`ThemeService.cs:94-108`, `AnimationService.cs:107-126`). The real defects are lock discipline and untracked work (STEAMHOST-009/010/030) | Do not split each service into five owners. Keep one service per feature, fix lock scope and tracking, and share one small background-work slot (STEAMHOST-029) |
| C23 | S02 "Preserve CSSLoader/AudioLoader formats, boot movie choice originals, authored glyph bytes and guide chord ownership" | accurate | `InstalledTheme.ConfigFileName` (`config_USER.json`), `AnimationOverrides` markers, `SteamGlyphCss` | Add: persist Steam's startup-movie choice from a partial set-aside reply (U02B-SUTC-011 consumer side, STEAMHOST-016) |
| C24 | api-integration L8 toolkit 0.2.0 consumer list | partially | Missing: `Core/SteamDownloadSort.cs` (JS in C#), `SteamStorageBridge`, `CommonPluginSteamUiSource`, `ShellSession.SteamUi.cs`, `ShellSession.cs:594-607` (transport/running-apps probe), `CardAcfWatcher.cs:246-276` and `CardVolumeMonitor.cs:331` (readiness), `SteamGraphicsService.cs:274` (`NativeQamUi.ValidInteger`) | Add them |
| C25 | Plan L83 "Optional ... Steam surfaces ... degrade independently" | inaccurate (current state) | A host constructor throw (e.g. the asset hash mismatch in `SteamUiAssetCatalog.LoadNativeQamBootstrap`) escapes `WireSessionEvents` (`ShellSession.cs:1042`) and ends the session (U05-LFB-003) | Keep as the requirement; STEAMHOST-017 removes one throw source |
| C26 | T01_01 batch (toolkit `CefEvalResult.FromTransport`) "all in-repo consumers remain unchanged" | accurate for this domain | `LibraryTabManager.cs:228-235` already treats `Reachable && Value != "true"` as not done | none |
| C27 | audit-coverage: U08A/U08B/U09 unstarted, "required new source closure" | accurate | — | This report is the source closure for the scoped files; the coverage ledger should cite STEAMHOST ids |

---

## 2. Findings

Severity scale: critical > high > medium > low > nit. "NEW" means no ledger or audit entry covers it.

### STEAMHOST-001 (high): the Steam UI host outlives the owners it calls during shutdown, with no T0 admission close

- **Where:** `ShellSession.Shutdown.cs:117-191` against `:355-370`; `SteamUiSessionHost.cs:437-569`.
- **Problem:** AutoTDP, the device coordinator, common plugins and GPU are disposed first. Steam's QAM can still dispatch `setPrimaryLimit`, `setAutoTdp` or `setControllerTarget` into `DeviceCoordinatorNativeQam*` services whose coordinator is shut down. `_cefMasterGate.WaitAsync()` has no token. The host has no synchronous way to stop answering. Even `DisposeAsync` first awaits `DisableAsync`, which does CEF round trips, and only then disposes the runtime.
- **Coverage:** U05-LFB-020. Plan B3 covers admission in general but names no host entry point.
- **Fix:** add `CloseAdmission()`, which sets runtime commands and publications off and calls `CancelAllInflight` with no CEF traffic, and call it at T0. Retract patches in the feature phase under the outer deadline. Order host disposal and the projection unsubscriptions before device/GPU disposal, or after admission is closed. Never await the master gate without the shutdown token.

### STEAMHOST-002 (medium): feature-switch writes race the synchronization loop's retraction

- **Where:** `SteamUiSessionHost.cs:589-610`, `:939-961`, `:1498-1534`.
- **Problem:** the loop evaluates `BootstrapWanted()` as false and then calls `SetPatchStates(false,false)`. An `Apply(true)` on the UI thread between those two steps runs `SetPatchStates(true,true)` and queues a pass. The loop's `SetPatchStates(false,false)` then overwrites it. The next pass sees `BootstrapWanted()==true` but never calls `SetPatchStates`, so native QAM is "enabled" with every row patch disabled until the next toggle. `SetPatchStates` is called from the UI thread, the loop and `DisableAsync` with no ordering.
- **Coverage:** NEW.
- **Fix:** delete the loop's else-branch `SetPatchStates`. Compute patch enablement once per synchronization pass from one immutable switches snapshot (see 003), so the last pass always reflects the last switches.

### STEAMHOST-003 (medium): thirteen volatile switch flags and nine near-identical `Apply*` methods, mirrored by session shadow fields

- **Where:** host `SteamUiSessionHost.cs:141-162`, `:572-830`; session `ShellSession.SteamUi.cs:34-63`, `:359-441`, `:607-635`; reload `ShellSession.Config.cs:124-140`.
- **Problem:** each host `Apply*` repeats the same "equal, set, global-enable, SetPatchStates, QueueSynchronization, QueuePublication" sequence. The session keeps its own `_downloadSortEnabled`, `_libraryBadgeEnabled`, `_homeCarouselEnabled`, `_carouselShowUninstalled`, `_screensaverTimeoutsEnabled` and `_wifiIndicatorEnabled` with a second set of equality guards and the redundant self-assignments (U05-LFB-025). The reload applies Steam UI in nine order-dependent calls.
- **Coverage:** NEW (the self-assignment part is U05-LFB-025).
- **Fix:** one `SteamUiSurfaceSwitches` record (NativeQuickAccess, HostSurfaces, SurfaceObservation, NetworkIndicator, DownloadSort, LibraryBadge, HomeCarousel + ShowUninstalled, ScreensaverRows, Glyphs + profile + nativeArtwork). It is built from `AppConfig` by one pure function, and the host gets one `Apply(SteamUiSurfaceSwitches)` that stores it with `Volatile.Write` and queues a pass. This deletes the session shadow fields and the eight `Apply*` methods. `_glyphDeliveryEnabled`, `_graphicsReady` and `_wsgmSettingsReady` stay host-derived state.

### STEAMHOST-004 (medium): the quarantine mirror and the two-pass disable are toolkit workarounds

- **Where:** `SteamUiSessionHost.cs:64-68`, `:832-863`, `:1462-1491`.
- **Coverage:** U02A-SUTC-034 and U02A-SUTC-002, confirmed.
- **Fix:** after T01 (runtime-owned quarantine plus reverse-topological removal on every path), delete `_failedPatchGate`, `_failedPatchIds`, `Quarantined` and `OnModuleFailed`'s retraction loop, and make `DisableAsync` a single pass.

### STEAMHOST-005 (medium): the host constructs about 25 concrete backends, including ambient native and config access

- **Where:** `SteamUiSessionHost.cs:109-110` (`ConfigStore.Mutate`, `PowerSchemes.Windows`), `:78` (`HybridCores.Windows`), `:284` (`new QuickAccessFolds()`, a file store under `Log.Directory`), `:290-316`, `:321-327` (`SteamUiAssetCatalog`), `:224-252` (28 optional parameters whose null means overlay-test, but the host is never built in overlay-test, `ShellSession.cs:1028-1031`).
- **Coverage:** plan S01 step 1 ("typed SteamFeatureSet backend bundles"); the dead overlay-test conditionals are U05-LFB-025.
- **Fix:** see section 4. Build the backends at the composition root. Required backends become non-null and the overlay-test null semantics go away; optional ones stay optional only where a session can lack them (device integration off, no GPU, no common plugins, CPU boost unavailable).

### STEAMHOST-006 (medium, latent): plugin Steam UI modules are read once at host construction

- **Where:** `SteamUiSessionHost.cs:261-262`, `:1268-1271`; `CommonPluginSteamUiSource.cs:210-223`.
- **Problem:** `ReadModules()` snapshots the admitted, ready plugins when the host is built. A plugin that becomes Ready later, or restarts with a new generation, never gets its modules registered. Modules captured from a retired registration stay registered with handlers into the old instance. `_pluginPatchIds` is likewise frozen.
- **Coverage:** NEW. Latent today: no in-repo plugin implements `IPluginSteamUi`, but the MIT SDK invites outside packages to.
- **Fix:** let `CommonPluginSteamUiSource` own a dynamic module set, registering and unregistering a plugin's modules on its admission edges through the toolkit module set (which needs a register/unregister API from T01). Alternatively, if dynamic module registration is not wanted, state that plugin modules are read at session start, refuse modules from plugins not ready by then, and document it. Recommendation: dynamic, because restarts are normal.

### STEAMHOST-007 (medium): `SteamStorageBridge` id maps are mutated and read across threads without a lock

- **Where:** `SteamStorageBridge.cs:32-37`, `:366-378` (`DriveId`/`DeviceId` add during `ReadState` on the publication loop), `:583-601` (`ResolveDrive`/`ResolveDevice`/`FindTarget` on command threads), `_loggedProjection` at L45/L462-468.
- **Problem:** concurrent `Dictionary` writes and reads can corrupt the maps or throw. A throw in `ReadState` quarantines the storage module for the rest of the session.
- **Coverage:** NEW.
- **Fix:** one lock around the maps, or build an immutable id table per projection and resolve commands against the last published table.

### STEAMHOST-008 (medium): `SteamStorageBridge.ReadState` does synchronous disk and file I/O on every publication round

- **Where:** `SteamStorageBridge.cs:296-356`, `:498-518`, `:543-570`, `:612-623`; `SteamUiSessionHost.cs:1322-1328` (the comment there says reading "only projects"); `WindowsStorage.DescribeVolumes` (`DriveInfo.GetDrives()` + `IsReady` + `TotalSize` for every drive, network drives included).
- **Problem:** with removable storage present, every round re-enumerates volumes, re-reads `libraryfolders.vdf` per device and reads each card marker. A round is raised by any surface change, including AutoTDP status ticks and performance state. Reads are sequential in the toolkit loop (`SteamUiModuleRuntime.cs:290-318`), so a stalled network drive blocks every QAM publication.
- **Coverage:** NEW.
- **Fix:** compute the projection on the bridge's existing drive/format change signals (`OnDrivesChanged`) off the publication thread, cache it with a revision, and pass `revision:` to the module. `ReadState` then returns the cached value. This also fixes the misleading comment.

### STEAMHOST-009 (medium): `AnimationService` writes configuration while holding its state lock

- **Where:** `AnimationService.cs:974-992` (`ChangeConfigLocked` calls `_writeConfig` under `_sync`); callers at L419, L474, L486, L542 (in `Start()` on the UI thread), L794, L893, L900. `_writeConfig` is `CommitWsgmSetting` (`ShellSession.Config.cs:31-43`), which takes the cross-process config mutex (2 s timeout) and writes the file.
- **Problem:** `ReadState`/`ReadExtensionsItem` on the publication and overlay UI threads block behind cross-process I/O.
- **Coverage:** NEW.
- **Fix:** compute the change under the lock, release it, write, then publish. `ThemeService.ChangeConfig` already writes outside its lock. Unify the save-failure semantics (029).

### STEAMHOST-010 (medium): untracked fire-and-forget work is not joined at shutdown

- **Where:**
  - `ShellSession.SteamUi.cs:466` and `:515`: `ApplyCefMasterSwitch` enable/disable via `Task.Run`. An exception from `ApplySteamUiTransportGate` in the `finally` goes unobserved.
  - `:240`: `RestoreSteamUiAfterBigPictureAsync` (only observed).
  - `:327`: tab boot sync.
  - `WsgmSteamSettingsService.cs:735`: `ApplyLatestSteamInputAsync`, which may elevate during shutdown.
  - `LibraryTabManager.cs:417, 670, 681, 710, 767, 817`: `SyncQuietlyAsync`/`PushTabOrderAsync`, which write `config.json`.
  - `ThemeService.cs:355, 384, 406, 707, 784, 808-809, 941, 1006`.
  - `AnimationService.cs:281, 322, 601, 828, 1022`.
  - `SteamUiSessionHost.cs:874`: `sounds.RefreshAsync`.
- **Coverage:** plan objective L115 ("All long-running work is tracked and joined"). The site list is NEW.
- **Fix:** each owner keeps one tracked task (or a small set) under its own cancellation and awaits it in `DisposeAsync`. Owners: the coordinator for master/transition/boot sync, the tab service for syncs and order pushes, the content services for store work. Never queue new work after admission closes.

### STEAMHOST-011 (medium): `KickTabBootSync` can cancel a disposed source, and completed sources leak

- **Where:** `ShellSession.SteamUi.cs:316-353`.
- **Coverage:** U05-LFB-009, confirmed: the previous task's `finally` disposes the old source between the store at L325 and `previous.Cancel()` at L326.
- **Fix:** move this into the tab service (B6) as one tracked worker with a single reschedulable token. The "superseded" semantics do not need per-trigger `CancellationTokenSource` swaps.

### STEAMHOST-012 (medium): process-global mutable state in this domain

- **Where:**
  - `SteamUiReadiness` static `_nextReady`/`_ready` (`SteamUiReadiness.cs:29-35`); tests must reset it (`SteamUiTransportGateTests.cs:7-16`).
  - `LibraryTabManager` statics `Gate`, `_tabOrderWrites`, `_tabOrderPush` (L35, L73-76).
  - `LibraryBadges` static `_current`, `_revision`, `Changed`, with a static event subscription from the host (`SteamUiSessionHost.cs:349`, `:511`).
  - `SteamUiTransportSession.SetEnabled`/`Attach` driven by the session (`ShellSession.SteamUi.cs:83`, `ShellSession.cs:599`).
  - `SplashTheme._openImportSessions` (`SplashTheme.cs:43-51`).
- **Coverage:** U05-LFB-005 (part), plan S02. Confirmed.
- **Fix:** instances owned by the coordinator and the tab service (section 4). `SplashTheme` sessions become an instance owned by the Settings window host (Settings domain).

### STEAMHOST-013 (medium): views and another manager call the `LibraryTabManager` static API directly

- **Where:** `Overlay/CardManagerView.cs:48-177`, `Overlay/LibraryTabsView.cs:64-366`, `Shell/SdFormatManager.cs:509` (uses `LibraryTabManager.MutateConfigAsync`, a generic config helper living in the tab manager), `CardAcfWatcher.cs:266-276`.
- **Coverage:** NEW. The plan's "no global service lookup/store acquisition in views" principle applies.
- **Fix:** an injected `ILibraryTabs`/`ICardLibraries` command interface for the views. `SdFormatManager` uses the config store transaction port directly (config domain).

### STEAMHOST-014 (medium): every library-tab sync rewrites `config.json` and triggers a full session reload

- **Where:** `LibraryTabManager.cs:140-161` (`MutateConfigAsync` with `MergeDiscovery` + `KnownNativeTabs` union, always), `:320-335` (`ListCardsAsync` writes too); `ConfigStore.cs:1194-1201` (`Mutate` always saves).
- **Problem:** each card notification, boot sync, builder change or opening of the card manager writes the user preferences file. The file watcher then reloads about 30 consumers (`ShellSession.Config.cs:104-157`).
- **Coverage:** NEW. The observed caches inside `AppConfig` are U04A-LFA-007.
- **Fix:** write only when the merge changed something (compare before saving inside the transaction). The config domain's instance store should skip unchanged saves generally. Whether `KnownNativeTabs`/`CardLibraries.AppIds` stay in `config.json` is the config domain's decision.

### STEAMHOST-015 (medium): uninstall leaves Steam-side state that WSGM owns

- **Where:**
  - The boot-movie override `config\uioverrides\movies\bigpicture_startup.webm`, its `.wsgm` marker and the user's original moved to `.wsgm-original` (`AnimationOverrides.cs:79-185`).
  - Steam's own Startup Movie choice, set aside and kept only in WSGM's `config.json` (`AnimationService.cs:855-917`, `AnimationsConfig.SteamSetAside`).
  - The `steamui\themes_custom` junction re-pointed to `%LOCALAPPDATA%\WSGM\themes`, which also replaces a CSS Loader link (`ThemePaths.cs:68-112`).
  - No reference to any of these in setup, uninstall or `--restore-shell` (grep over `src`).
- **Coverage:** NEW. In the spirit of "Never strand users ... on every exit/uninstall/upgrade path".
- **Fix:** proposed as an uninstall step. Remove WSGM's override and restore `.wsgm-original` (pure file operation); recreate or remove the junction. Steam's set-aside choice can only be given back through live Steam UI, so record it in an uninstall note. This changes setup behaviour, so it needs a maintainer decision (section 6).

### STEAMHOST-016 (medium): a partial startup-movie set-aside loses the user's original choice

- **Where:** toolkit `SteamStartupMovie.cs:65-67, 103-110, 142-146`; consumer `AnimationService.cs:880-887` ignores `result.Choice` when `!Accepted`.
- **Coverage:** U02B-SUTC-011, confirmed.
- **Fix:** after the toolkit returns the original in every post-first-write reply, WSGM persists `SteamSetAside` whenever a choice is present, `Accepted` or not, and records the error.

### STEAMHOST-017 (low): the runtime asset hash check can abort session startup, and the hash constant is redundant mechanism

- **Where:** `SteamUiAssetCatalog.cs` (`NativeQamBootstrapSha256` constant, throw on mismatch); `SteamUiSessionHost.cs:321-326`; the builder rewrites C# source (`eng/build-steam-assets.mjs:221-275`).
- **Coverage:** NEW; the startup isolation part is U05-LFB-003.
- **Fix:** keep `steam-assets:check` (the generated `.js` is the reviewed artifact and diffs on its own). Compute the SHA-256 at load for the injected-asset identity, then delete the constant, the runtime compare-and-throw and the builder's C# rewrite. If host construction can still throw, contain it so Steam surfaces degrade independently (plan L83).

### STEAMHOST-018 (low): download sort is 220 lines of JavaScript in a C# raw string, outside the asset pipeline

- **Where:** `SteamDownloadSort.cs:20-246` (`window.__wsgm` global, its own focusable/enum scans); `InstallExpression` re-runs `SteamUiModuleResolver.CreateExpression` on every apply (U02A-SUTC-005). `SteamDownloadSortPatch` (L300-384) needs the bridge's `elements` gate but declares no dependency, so it relies on the host enabling the bridge at the same time.
- **Coverage:** NEW (the resolver part is U02A-SUTC-005).
- **Fix:** a `Source/download-sort.ts` fragment registered with `registerGate`, using the bridge's resolver and the `elements` gate. The C# patch becomes the standard gate patch, and `DependsOn` names the bridge once T01 adds it. The resident `__wsgm` namespace goes away.

### STEAMHOST-019 (low): WSGM-owned surfaces use the toolkit's `steam-ui.` patch-id prefix

- **Where:** `steam-ui.themes`, `steam-ui.animations`, `steam-ui.artwork-browser`, `steam-ui.wsgm-graphics`, `steam-ui.library-import`, `steam-ui.wsgm-settings` (Shell `Steam*Surface.cs`, matching TS constants), while `wsgm.download-sort`, `wsgm.chord-reset`, `wsgm.controller-caps` and `wsgm.steam-input.glyph-style` use `wsgm.`.
- **Problem:** this conflicts with the plan's host-reserved prefixes (L139).
- **Coverage:** NEW.
- **Fix:** rename to `wsgm.*` in C# and TS together when T03 reserves the prefix. The ids are not persisted, so users see no change.

### STEAMHOST-020 (low): inconsistent visibility in the executable

- **Where:** `SteamThemesSurface.cs` (public static class, public records, public `ISteamThemesBackend`), the other WSGM surfaces, `LibraryTabManager` (public static) and `LibraryTabSyncResult`, all public in an exe whose neighbours are internal.
- **Coverage:** NEW.
- **Fix:** make them internal (`InternalsVisibleTo` already serves the tests).

### STEAMHOST-021 (low): non-Steam consumers depend on QAM helper types

- **Where:** `NativeQamUi.OverrideId`/`DeviceOverrideId` are used by `Overlay/OverlayWindow.Sources.cs:72` and `DeviceOverlayBridge.cs:1222-1228`; `NativeQamUi.ValidInteger` by `SteamGraphicsService.cs:274`; `DeviceCoordinatorNativeQamTdpService.Project` and `NativeQamTdpState` by the RTSS OSD (`ShellSession.Performance.cs:183-190`).
- **Problem:** `NativeQamTdpState` only exists to be mapped by `ToRange` (`NativeQamSemanticServices.cs:815-820`), and its doc ("overlay and AutoTDP read") is stale.
- **Coverage:** NEW.
- **Fix:** move the profile override marker and the integer-on-descriptor check to the profile/capability projection (`CapabilityProjection` or the Device domain's `PowerLimitProjection`). The QAM services then map device projections to toolkit states directly, and `NativeQamTdpState` is deleted.

### STEAMHOST-022 (low): redundant subscriptions and an unnecessary UI-thread hop

- **Where:** the TDP, device-controls and AutoTDP services each subscribe to `Capabilities.Changed` (`NativeQamSemanticServices.cs:736-737`, `:982`, `:1290-1302`). `OnAutoTdpStatusChanged` posts to the Avalonia dispatcher (L1432-1435) even though `QueueStatePublication` is thread-safe (`SteamUiModuleRuntime.QueuePublication`).
- **Coverage:** NEW.
- **Fix:** projections become stateless functions over a device port. The host (or one `DeviceNativeQamProjection`) subscribes once and queues publication, and the dispatcher hop is deleted, which also removes an Avalonia dependency from a projection. `NativeQamUi.RunAsync` stays for the radio/audio managers whose collections are UI-thread-owned.

### STEAMHOST-023 (nit): duplicated validation and constants

- **Where:** `(value - min) % step` checks in `NativeQamUi.ValidInteger` (L59-67), `SetLimitAsync` (L835-845) and `SetIntegerAsync` (L1076-1084); the 5-second `CommandTimeout` twice (L713, L973); `SetRefreshRateAsync` (L250-263) duplicating the `RefreshRateHz` branch (L461-466).
- **Coverage:** NEW.
- **Fix:** one helper and one constant.

### STEAMHOST-024 (low): misleading command results

- **Where:** `ApplyProfileToggleAsync` returns "no identifiable application is running" when `SetGameEnabledAsync` returns false for any reason (`NativeQamSemanticServices.cs:533-541`). `ResetProfileAsync` reports success with no profile owner (L504-513). `SetUnifiedModeAsync` always reports success (L783-794).
- **Coverage:** NEW.
- **Fix:** distinct texts. Make `Profiles` required (the session always has one), and return the actual outcome of `SetManualTdpModeAsync` if it has one.

### STEAMHOST-025 (low): `WsgmSteamSettingsService` caps ordered choices at 64, and its default branch is untyped

- **Where:** `WsgmSteamSettingsService.cs:616` (`GetArrayLength() > 64` refuses), `:633-635` (the default passes any JSON kind), `:805-827` (hand-rolled `JsonText`/`JsonNumber`).
- **Problem:** the 64 cap violates no-arbitrary-limits.
- **Coverage:** NEW.
- **Fix:** validate that every item is a declared choice (a type/shape check), and drop the 64. Type-check the remaining kinds as `CommonPluginSteamUiSource.TryReadValue` does. Use `JsonSerializer.SerializeToElement`.

### STEAMHOST-026 (low): `CommonPluginSteamUiSource` route bound, stale comment, reads that mutate, inconsistent lookups

- **Where:** `Admissible` bounds paths at 256 (L256) against plan L141, with a stale "cap/quota" comment (L243-245). `Refresh()` mutates subscriptions and ids inside every `Read*` (L131, 171, 202, 235). `ReadExtensionsTab` uses the indexer (L145) while `ReadSettings` uses `TryGetValue` (L189-192). `Deadline.After(TimeSpan.FromSeconds(10))` uses the ambient clock (L112, L293), which will change with the SDK clock.
- **Coverage:** NEW.
- **Fix:** remove the bound and the comment. Refresh on change notifications only; reads take the lock and project. Use one value lookup. Take the clock from the plugin context.

### STEAMHOST-027 (low): ownership inversion of `CommonPluginSteamUiSource`

- **Where:** the session creates it inline in the host's argument list (`ShellSession.cs:1074-1076`), but the host disposes it (`SteamUiSessionHost.cs:527-531`). The session's `_pluginSteamUi.Changed += wsgmSettings.Refresh` (`ShellSession.cs:1094-1098`) is never unsubscribed. `_steamPowerMenu` and `_steamGraphics` are also assigned inside the argument list (`ShellSession.cs:1084-1092`).
- **Coverage:** NEW.
- **Fix:** whoever creates an object disposes it. The composition root creates it and registers its disposal in the session lifetime.

### STEAMHOST-028 (low): `DisposeAsync` is not idempotent under concurrency

- **Where:** `SteamUiSessionHost.cs:437-445`.
- **Problem:** a volatile check-then-act, with `_disposed` set only after `DisableAsync`, so events keep flowing into the runtime during disable.
- **Coverage:** NEW.
- **Fix:** `Interlocked` once-only dispose task returned to every caller, the pattern plan L81 requires.

### STEAMHOST-029 (low): the theme and animation services duplicate one mechanism with divergent failure semantics

- **Where:** `ThemeService.cs:743-761`, `:926-983`; `AnimationService.cs:974-1050`.
- **Problem:** both carry their own `_busy`/`_notice`/`_error`/`_revision` plus `StartWorkAsync`/`Publish`/`Refuse`. On a save failure, `ThemeService.ChangeConfig` refuses and keeps state. `AnimationService.ChangeConfigLocked` keeps the unsaved choice in memory and still copies it to Steam's override (L419-425, L794-797).
- **Coverage:** NEW.
- **Fix:** one small internal `ContentWorkSlot` (busy flag, notice/error, one tracked task, revision bump), shared by both. One rule: an unsaved choice is refused and nothing is applied.

### STEAMHOST-030 (low): `ThemeService` lock and thread discipline

- **Where:** `_loader.SaveConfig`/`Delete`/`Load` run file I/O under `_sync` (L491-623, L998-1006). `Start()` loads every theme synchronously on the UI thread during session start (`ShellSession.cs:730-736`, `ThemeService.cs:764-810`). `_config` is read, modified and written outside the lock from the UI and bridge threads (L703-704, L756-758, L813-823). `InstalledTheme.Enable`/`Disable` drop `SaveConfig` errors (`InstalledTheme.cs` Enable/Disable).
- **Coverage:** NEW.
- **Fix:** run `Start` as tracked background work with an explicit "loading" state (same UI). Report save errors. Swap `_config` under the lock.

### STEAMHOST-031 (low): arbitrary limits and a silent drop in the theme loader and installer

- **Where:** `ThemeInstaller.cs:53-57`, `:190-208`; `ThemeLoader` `ParseThemes` duplicate-suffix loop.
- **Problem:** `depth > 8` is arbitrary, since the `local` name set already prevents cycles. The journal's `Names.Count > 256` refusal can leave recovery permanently pending after a legitimate package with many roots. A theme whose name already has five duplicates is dropped with no `ThemeLoadError`.
- **Coverage:** NEW.
- **Fix:** a visited set by store id instead of the depth cap. Drop the count bound (keep the 128 KiB byte bound, a real I/O safety bound). Record a load error for an unresolvable duplicate (CSS Loader parity on the suffix count is fine).

### STEAMHOST-032 (nit): `ThemePaths.CreateLink` can leave `cmd mklink` running

- **Where:** `ThemePaths.cs:126-157`.
- **Problem:** `WaitForExit(10000)` timing out leaves the child running and unreported.
- **Coverage:** NEW.
- **Fix:** kill the child on timeout and log it, or create the junction through the existing native helper if one exists.

### STEAMHOST-033 (low): WSGM gates hand-roll method wrapping

- **Where:** `chord-reset.ts:28-70`, `controller-caps.ts:118-143`.
- **Problem:** neither uses the toolkit's claim/release helpers. If another wrapper sits on top, unhook silently forgets ownership while `remove()` reports `removed`; the wrapper stays in the chain as a pass-through.
- **Coverage:** NEW; an analog of U02A-SUTC-025 and U03B-SUTS-001.
- **Fix:** use the toolkit `ownership.ts` member claim (release first, forget after), exposed to fragments by T03.

### STEAMHOST-034 (low): duplicated patterns in the TS page fragments

- **Where:** `themes.ts` and `animations.ts` (act helper, tabs, card, detail, browse, page shell); `artwork-browser.ts:8-12` and `library-import.ts:11-31` (module-level `ui` plus listener sets so modals can read state).
- **Coverage:** NEW.
- **Fix:** one fragment-local helper (WSGM `Source/page-kit.ts`) or a toolkit `pageStateChannel()` for modal state, with identical rendering output. Verify with the emitted-asset checks.

### STEAMHOST-035 (nit): write-only state

- **Where:** `LibraryBadgeBackend.BigArt` (`LibraryBadges.cs`) and `HomeCarouselBackend.Last` (`HomeCarousel.cs:55-75`) are never read; their equality guard duplicates `Log.Change` deduplication.
- **Coverage:** NEW.
- **Fix:** stateless log-only handlers.

### STEAMHOST-036 (nit): the sound integration status strings are duplicated four times

- **Where:** `SteamUiSessionHost.cs:627-629`, `:871-873`, `:926-931`; `ShellSession.cs:743-746`.
- **Coverage:** NEW.
- **Fix:** let `SoundPackService` derive the text from (host on, patch state, last failure).

### STEAMHOST-037 (nit): dead term in the synchronization loop

- **Where:** `SteamUiSessionHost.cs:956-958`.
- **Problem:** `_downloadSortEnabled ||` can never be true in the `!BootstrapWanted()` branch, because `BootstrapWanted` includes it.
- **Coverage:** NEW. Removed with 002.

### STEAMHOST-038 (nit): small inaccuracies

- **Where:**
  - The comment "Steam's game menu. Declared unconditionally" sits above the CPU boost block (`SteamUiSessionHost.cs:1142-1149`).
  - `Quarantined` makes a redundant `Count > 0` check (L1489).
  - `EnableFrameLimitWatts` returns fps (`NativeQamSemanticServices.cs:377`).
  - `ApplyVariableRefreshRate { get; set; }` beside `init` siblings (L205).
  - `ThemeService` returns the literal "Invalid State" as a preset name (L887).
- **Coverage:** NEW.

### STEAMHOST-039 (nit): duplicate applies in the Big Picture restore

- **Where:** `RestoreSteamUiAfterBigPictureAsync` calls `ApplyHostSteamUi` and then `ApplySteamUiSurfacePreferences`, which calls it again (`ShellSession.SteamUi.cs:257-261`, `:361`).
- **Coverage:** U05-LFB-025 family. Removed with 003.

### STEAMHOST-040 (nit): two readiness notions

- **Where:** `SteamUiReadiness.IsReady` (a live `Steam.IsRunning && IsBigPictureVisible`) is used by `CardAcfWatcher.cs:246`, `CardVolumeMonitor.cs:331` and `ShellSession.cs:679`, while `RunWhenReadyAsync` follows the gate's observed edge (`SteamUiReadiness.cs:44`, `:93-125`).
- **Coverage:** NEW.
- **Fix:** consumers read the coordinator's gate state. The 1 s window poll stays inside the coordinator as the one sampling point.

### STEAMHOST-041 (medium, test isolation): storage tests enumerate real disks

- **Where:** `SteamStorageBridgeTests.cs:36-55` and `SteamUiSessionHostTests.cs:215-236` construct a real `RemovableDriveManager` and `SdFormatManager`. The bridge constructor calls `_formats.Refresh()` (`SteamStorageBridge.cs:76`), which starts `NativeStorage.MountedVolumes()`/`ListDiskInterfaces()` against real hardware and posts to the Avalonia dispatcher (`SdFormatManager.cs:171-196`, `:202-221`).
- **Problem:** this contradicts the test class's own remark ("never touches real storage") and `tests/WSGM.Tests/AGENTS.md`.
- **Coverage:** NEW.
- **Fix:** inject drive/format ports (fake lists). The bridge stops triggering enumeration in its constructor; the session triggers the first refresh.

### STEAMHOST-042 (low, test quality): behaviour coverage gaps

- **Untested:**
  - `LibraryTabManager.SyncAllDetailedAsync`, the `SaveTabOrder` chain and rename orchestration. Only the pure `MergeDiscovery`/`TrySetMarkerLabel` are covered (`CardNameAuthorityTests.cs`).
  - The master switch, BP transitions and transport gate in `ShellSession.SteamUi.cs` (U05-LFB-006).
  - Host dispose/shutdown ordering and storage commands.
- **Shape of what exists:** `SteamUiAssetTests` are source-token checks. The plan accepts these only as secondary guards (L145); keep them secondary.
- **Coverage:** U05-LFB-006 plus NEW.
- **Fix:** add real-owner orchestration tests with a fake transport and fake ports, listed per batch.

### STEAMHOST-043 (nit, test placement)

- **Where:** the `LibraryTabManager` tests sit in `CardNameAuthorityTests`. `AnimationServiceTests` borrows `ThemeStoreClientTests.StubHandler`, which belongs in `Fakes`. `SteamUiTransportGateTests` lives in `Core/` for a Shell type.
- **Coverage:** analog of U04A-LFA-034.

### STEAMHOST-044 (low): `SteamUiText.Of` is a trivial toolkit public helper

- **Where:** 14 call sites in `NativeQamSemanticServices.cs`.
- **Coverage:** U02B-SUTC-032.
- **Fix:** toolkit surfaces normalize null/blank at serialization, after which the calls are deleted rather than moved.

### STEAMHOST-045 (low): fold ids are persisted English titles

- **Where:** `QuickAccessFolds.cs:26-30`; `SteamPanelFoldsBackend` is constructed in the host (L284).
- **Coverage:** U03B-SUTS-008.
- **Fix:** migration in the config domain, plus the WSGM-supplied stable ids (C12).

### STEAMHOST-046 (low): card services sit in the Steam UI partial

- **Where:** `ApplyCardServices` (`ShellSession.SteamUi.cs:551-598`, the ACF watcher, volume monitor, `MessageWindow.Create()` and drive/format watcher wiring) is card/storage ownership placed in the Steam UI file.
- **Coverage:** NEW (`MessageWindow` is U05-LFB-014).
- **Fix:** move it to the storage/card owner in the session decomposition (lifecycle/storage domain). The coordinator only exposes "CEF master enabled" and "ready" to it.

### STEAMHOST-047 (low): the reload applies Steam UI with order-dependent, split semantics

- **Where:** `ShellSession.Config.cs:120-140`.
- **Problem:** `ApplyCefMasterSwitch` runs asynchronously while `ApplyHostSteamUi` and the other surfaces apply immediately. Native QAM `Apply(false)` is never called when the master switch goes off; it relies on `DisableAsync` in the background retraction. The QAM rows can be re-applied transiently before that retraction runs.
- **Coverage:** NEW.
- **Fix:** with 003 and the coordinator, reload calls `coordinator.ApplyConfig(SteamUiConfig)` once. The coordinator orders retract, then close, or open, then apply under its gate.

### Reviewed with no change needed

`SteamGlyphCss` (pure, ownership split clear; build-coupled class names are documented). `AnimationOverrides` (marker/ownership logic is careful). `ThemeTargets`. `ThemeInject`. `ThemeStoreClient` (its 64 MiB and 8 MiB caps are byte-safety bounds, allowed). `SplashTheme` zip caps (byte-safety bounds). The `SteamUiReadiness.TransportShouldBeOpen` pure policy and its tests. `wsgm-settings.ts`/`wsgm-graphics.ts` (rejections do reach `.catch`: `bridge.ts:87-117` rejects). The `SteamPanelFoldsBackend` wiring. The library rename re-read of the marker is a file-integrity check, not a device readback gate, so the no-readback rule does not apply.

---

## 3. Plan refinements

### Additions

1. **S01 ownership:** add `ShellSession.SteamUi.cs`, `SteamStorageBridge.cs`, the WSGM `Steam*Surface.cs` files in Shell, `SteamPanelFoldsBackend.cs`, `Core/SteamUiAssetCatalog.cs`, `Core/SteamDownloadSort.cs`, `Core/SteamUiAssets/Source/*.ts` and `eng/build-steam-assets.mjs`. Add the consumer sites in C24.
2. **Host admission close:** add `SteamUiSessionHost.CloseAdmission()` as a named T0 step in the B3 shutdown plan, ahead of any device/GPU retirement (STEAMHOST-001).
3. **Switches record:** add `SteamUiSurfaceSwitches` with a single `Apply` (STEAMHOST-002/003). This is the shape the plan's "explicit SteamFeatureSet" should take for enablement.
4. **Storage bridge:** add a lock and a cached projection with revision (STEAMHOST-007/008), plus fake-port tests (STEAMHOST-041).
5. **Uninstall:** add an uninstall/upgrade cleanup item for Steam-side state (STEAMHOST-015), pending the maintainer's decision.
6. **Plugin modules:** register and unregister them dynamically (STEAMHOST-006), with a toolkit module-set API dependency on T01.
7. **Startup movie:** persist Steam's choice from partial set-aside replies (STEAMHOST-016).
8. **Library tab sync:** write only on change (STEAMHOST-014).
9. **Download sort:** move it into a TS fragment (STEAMHOST-018), and rename WSGM patch ids to `wsgm.*` (STEAMHOST-019).

### Changes

1. **C6/C7:** "one generation host" / "Generation-scoped" becomes "one session-scoped host per transport; generations stay inside the toolkit manager".
2. **C14:** "bounded ... messages" means transport byte safety only; WSGM refusal text is never truncated.
3. **C18:** when the manager schedules its own synchronization, the host's signal/loop is deleted (not ported). Post-sync reconciliation runs off one manager notification.
4. **The S01 "typed SteamFeatureSet backend bundles":** use one plain `SteamUiBackends` record of already-built backends plus a static `WsgmSteamModuleCatalog.Create(backends, switchesAccessor)`. Do not introduce per-group interfaces or a registry.

### Removals (over-engineering under the simplify and no-arbitrary-limits rules)

1. **S02 step 1**, five owners per content feature (C22). Simpler shape: one service per feature with correct lock scope, one tracked work slot, and the existing injected loader/client/config.
2. **Host per generation** (C7). Simpler shape: keep the existing single host.
3. **Arbitrary caps in WSGM code**: plugin route 256 (STEAMHOST-026), ordered-choice 64 (STEAMHOST-025), theme dependency depth 8 and journal names 256 (STEAMHOST-031). The replacement is a type/shape check or a visited set.
4. **Runtime asset hash constant** and the builder's C# rewrite (STEAMHOST-017). The replacement is a hash computed at load plus the existing `--check`.
5. **The quarantine mirror, two-pass disable, sync loop and AutoTDP dispatcher hop** (STEAMHOST-004/018/022): delete rather than port.
6. **Per-trigger `CancellationTokenSource` swapping** for the tab boot sync (STEAMHOST-011). The replacement is one tracked worker with a reschedulable signal.

---

## 4. Target design

### Owners

| Owner (file) | Responsibility | Construction / dependencies |
| --- | --- | --- |
| `SteamUiCoordinator` (`Shell/SteamUiCoordinator.cs`, new) | The one transport instance (`PersistentSteamUiTransport`). The gate decision and 1 s window sample. The master switch with retract-then-close / open-then-apply under one gate. BP enter/exit holds. Readiness edge (`WhenReadyAsync`, `RunWhenReadyAsync`). `ApplyConfig(SteamUiConfig)`. `CloseAdmission()`. `DisposeAsync()` joins every tracked task. Exposes `IsOpen`/`Changed` to card services | Built by the composition root with the transport factory, `SteamUiSessionHost`, `LibraryTabService`, a clock and a `ISteamWindowProbe` (`Steam.IsRunning`/`IsBigPictureVisible`). No statics |
| `SteamUiSessionHost` (existing, slimmed) | Bridge, patch manager and runtime. `Apply(SteamUiSurfaceSwitches)`. Patch enablement derived per pass. Post-sync reconciliation (WSGM menu readiness, screensaver report, sound status, perf observation). `CloseAdmission`, `DisableAsync`, `DisposeAsync` | ctor `(ISteamUiTransport, WsgmSteamModules modules, SteamUiHostOptions)`. No backend construction |
| `WsgmSteamModuleCatalog` (`Shell/WsgmSteamModuleCatalog.cs`, new, static) | Today's `CreateModules` and `ReadPages`, as a pure builder from `SteamUiBackends` and a switches accessor; also returns the change sources to subscribe | static, pure |
| `SteamUiBackends` (record in the same file) | Every semantic backend, already constructed | Built by the composition root factory `SteamUiBackends.Create(...)` in `ShellSession` (later the session composition) |
| `PerformanceNativeQamAdapter` (`Shell/NativeQam/PerformanceNativeQamAdapter.cs`) | Today's `PerformanceServiceNativeQamAdapter` | `PerformanceService`, `ProfileService` (required) and the support/refresh/VRR delegates |
| `PowerLimitNativeQamService` | TDP sliders and unified mode | Device power-limit port (from the Device domain) |
| `DeviceControlsNativeQamService` | Charge and lighting | Device capability port |
| `AutoTdpNativeQamService` | AutoTDP switch | Device + AutoTDP ports |
| `ControllerTargetNativeQamService` | Controller target | Controller port |
| `NativeQamText` (static) | `ProgressText`, `StatusText`, `CommandResult`, `RunOnUiAsync` (renamed `RunAsync`) | — |
| `CapabilityProjection` (existing, Device/Profile domain) | Gains `OverrideId`, `DeviceOverrideId`, `ValidInteger`, and the power-limit projection that today is `DeviceCoordinatorNativeQamTdpService.Project` | Shared by overlay, OSD, Steam graphics and QAM |
| `LibraryTabService` (`Shell/LibraryTabService.cs`, replaces static `LibraryTabManager`) | Scan, merge, tab build, sync, order-push chain, card commands, rename. One tracked worker | config store transaction port, Steam client (explicit after T02), volume scanner port, `LibraryBadgeState` |
| `LibraryBadgeState` (`Shell/LibraryBadges.cs`, instance) | Current badge reading and revision, `Changed` | Owned by `LibraryTabService`, read by the host catalog |
| `SteamStorageBridge` (existing) | Same API, plus a lock, a cached projection and a revision | Drive/format ports |
| `ThemeService`/`AnimationService` (existing) | Same API. Lock scope fixed. `ContentWorkSlot` shared helper. Tracked tasks joined in `DisposeAsync` | unchanged injection |
| `CommonPluginSteamUiSource` (existing) | Dynamic plugin module (un)registration. Reads do not mutate. Owned and disposed by the composition root | `CommonPluginManager`, `PluginHost`, toolkit module-set API (T01) |

### Old symbol to new owner: `ShellSession.SteamUi.cs` (dissolved)

| Old symbol | New owner |
| --- | --- |
| `SteamUiHeldReason` const | `SteamUiCoordinator` |
| `_cefMasterGate` | `SteamUiCoordinator._gate` |
| `_transportGateSignal`, `_transportGateWork` | `SteamUiCoordinator` (tracked gate task) |
| `_bigPictureExitPending`, `_gameModeCefTransitionPending`, `_cefMasterEnabled` | `SteamUiCoordinator` state |
| `_carouselShowUninstalled`, `_downloadSortEnabled`, `_homeCarouselEnabled`, `_libraryBadgeEnabled`, `_screensaverTimeoutsEnabled`, `_wifiIndicatorEnabled` | deleted; replaced by `SteamUiSurfaceSwitches` computed from config |
| `_tabBootSyncCancellation` | deleted; `LibraryTabService` tracked worker |
| `ApplySteamUiTransportGate`, `RunSteamUiTransportGateAsync`, `RequestSteamUiTransportGateCheck` | `SteamUiCoordinator.Decide`/gate loop/`RequestCheck` |
| `PrepareSteamUiForBigPictureAsync`, `PrepareSteamUiForDesktopAsync`, `RetractSteamUiAsync` | `SteamUiCoordinator.PrepareForBigPictureAsync`/`PrepareForDesktopAsync`/`RetractAsync` |
| `ReleaseSteamUiBigPictureHold`, `RestoreSteamUiAfterBigPictureAsync` | `SteamUiCoordinator.ReleaseHold` (one tracked restore) |
| `KickTabBootSync`, `RunTabBootSyncAsync` | `LibraryTabService.RequestBootSync` |
| `ApplySteamUiSurfacePreferences`, `ApplyDownloadSort`, `ApplyLibraryBadge`, `ApplyHomeCarousel`, `ApplyScreensaverTimeouts`, `ApplyNetworkIndicator` | deleted; `SteamUiCoordinator.ApplyConfig` → `host.Apply(switches)` |
| `ApplyCefMasterSwitch` | `SteamUiCoordinator.ApplyConfig` (master edge) |
| `ApplyCardServices` | session card/storage owner (storage domain); reads `coordinator.IsMasterEnabled` |
| `GlyphsEnabled`, `ApplyGlyphConfig`, `OnPhysicalGlyphProfilesChanged` | `SteamUiSurfaceSwitches.From(config, glyphSelection)`; the session forwards profile changes to `coordinator.ApplyConfig` |

### Old symbol to new owner: `NativeQamSemanticServices.cs` (split)

| Old symbol | New owner |
| --- | --- |
| `NativeQamTdpState` | deleted; the device power-limit projection record (Device domain) |
| `NativeQamUi.OverrideId`/`DeviceOverrideId`/`ValidInteger` | `CapabilityProjection` |
| `NativeQamUi.ProgressText`/`StatusText`/`CommandResult`/`RunAsync` | `NativeQamText` |
| `PerformanceServiceNativeQamAdapter` (+ `ProjectFrameLimit`, private helpers) | `PerformanceNativeQamAdapter` |
| `DeviceCoordinatorNativeQamTdpService` (+ `ProjectPowerLimits`, `Project`, `TdpProjection`, `OutcomeText`) | `PowerLimitNativeQamService`; `Project` moves to the Device power-limit projection |
| `DeviceCoordinatorNativeQamDeviceControlsService` | `DeviceControlsNativeQamService` |
| `DeviceCoordinatorNativeQamAutoTdpService` (+ static `Project`) | `AutoTdpNativeQamService` |
| `DeviceCoordinatorNativeQamControllerTargetService` (+ `UnavailableDetail`, `Project`, `TryParseTarget`) | `ControllerTargetNativeQamService` |

### Old symbol to new owner: `SteamUiReadiness.cs` (static to instance)

| Old symbol | New owner |
| --- | --- |
| `TransportGatePollInterval` | `SteamUiCoordinator` const |
| `TransportShouldBeOpen` | kept static pure, as `SteamUiGatePolicy.ShouldOpen` (tests keep their cases) |
| `IsReady` | `ISteamWindowProbe` in the coordinator |
| `Observe`, `WhenReadyAsync`, `RunWhenReadyAsync`, `NextReadyAsync`, `_nextReady`, `_ready`, `Sync` | `SteamUiCoordinator` instance members |

### Old symbol to new owner: `LibraryTabManager.cs` and `LibraryBadges.cs` (static to instance)

| Old symbol | New owner |
| --- | --- |
| `Gate`, `_tabOrderWrites`, `_tabOrderPush` | `LibraryTabService` instance fields (one worker) |
| `SyncAllAsync`, `SyncAllDetailedAsync`, `SyncOnBootAsync`, `ListCardsAsync`, `RenameCardAsync`, `SetCardEnabledAsync`, `SetCardHiddenAsync`, `ForgetCardAsync`, `SaveTabOrder`, `SaveCustomTabsAsync`, `BuildTabOrder`, `PresentCardContentIds` | `LibraryTabService` (instance; `BuildTabOrder` and `MergeDiscovery` stay static pure) |
| `MutateConfigAsync` | deleted; callers use the config store transaction port (config domain) |
| `TrySetMarkerLabel`, `FindMountedVolume`, `PushLabelToSteamAsync`, `TrySetVolumeLabel`, `ScanLibraries`, `ExternalVolumeLetters`, `ResolveName`, `ReadAcfAppIds`, `CardResolver`, `LibraryReadyProbe`, `DefaultNativeTabs` | `LibraryTabService` (the volume operations behind a scanner port) |
| `CardView`, `TabOrderEntry`, `Discovered`, `MountedVolume`, `LibraryTabSyncResult` | kept as nested/internal records in the new file |
| `LibraryBadges.Current`/`Update`/`Changed`/`Build` | `LibraryBadgeState` instance (`Build` stays static pure) |

### Public/internal API changes and the consumers that must change

| API change | Consumers |
| --- | --- |
| `SteamUiSessionHost` ctor and the eight `Apply*` collapse to `Apply(SteamUiSurfaceSwitches)` + `CloseAdmission()` | `ShellSession.cs:1042-1110`, `ShellSession.Config.cs:120-140`, `ShellSession.SteamUi.cs` (dissolved), `SteamUiSessionHostTests` |
| `SteamUiReadiness` static removed | `ShellSession.cs:679, 762`, `CardAcfWatcher.cs:246-276`, `CardVolumeMonitor.cs:331`, `AnimationService.cs` (doc ref), `SteamUiTransportGateTests` |
| `LibraryTabManager` static removed | `Overlay/CardManagerView.cs`, `Overlay/LibraryTabsView.cs`, `SdFormatManager.cs:509`, `CardAcfWatcher.cs`, `ShellSession.cs:1033`, `CardNameAuthorityTests` |
| `LibraryBadges` static removed | `ShellSession.cs:1033`, the host, `LibraryBadgesTests` |
| `NativeQamUi`/`DeviceCoordinatorNativeQam*` renamed/moved | `NativeQamAudioService`, `NativeQamBluetoothService`, `NativeQamNetworkService`, `DeviceOverlayBridge.cs:1222-1228`, `OverlayWindow.Sources.cs:72`, `SteamGraphicsService.cs:274`, `ShellSession.Performance.cs:183`, `AutoTdpServiceTests.cs:829`, `NativeQamSemanticServicesTests`, `NativeQamPerformanceAdapterTests` |
| Patch-id rename `steam-ui.*` → `wsgm.*` (WSGM surfaces) | Shell `Steam*Surface.cs`, `Source/*.ts`, the generated asset and hash, tests asserting ids (`SteamUiSessionHostTests`, `SteamUiAssetTests`, `CommonPluginSteamUiSource.HostOwnedPatches`) |
| `SteamDownloadSort` resident JS moves to `Source/download-sort.ts` | `SteamDownloadSort.cs`, `SteamDownloadSortPatchTests`, `eng/build-steam-assets.mjs` (fragment discovery is automatic) |
| `SteamUiAssetCatalog.NativeQamBootstrapSha256` removed (hash computed at load) | `SteamUiSessionHost`, `eng/build-steam-assets.mjs`, `SteamUiAssetTests` |

### Files

- **Split:** `NativeQamSemanticServices.cs` into 5 files plus helpers.
- **Dissolved:** `ShellSession.SteamUi.cs`.
- **Replaced:** `SteamUiReadiness.cs` → `SteamUiCoordinator.cs` + `SteamUiGatePolicy`; `LibraryTabManager.cs` → `LibraryTabService.cs`.
- **New:** `WsgmSteamModuleCatalog.cs`; `Source/download-sort.ts`; optionally `Source/page-kit.ts`.
- **No folder move:** renaming namespaces for about 35 Steam files is churn without a defect behind it, so new files sit in `Shell/` (the NativeQam split may use `Shell/NativeQam/` with namespace `WSGM.Shell`).

---

## 5. Implementation batches

Each batch keeps the build green and runs only its filter (the full gate is for the final Z04).

### STEAMHOST-B1: local defect fixes, no API change (about 650 lines)

- **Files:** `SteamStorageBridge.cs`, `SteamUiSessionHost.cs` (loop else-branch, dead term, misplaced comment), `AnimationService.cs` (config write outside lock), `CommonPluginSteamUiSource.cs` (route bound, comment, lookup), `WsgmSteamSettingsService.cs` (64 cap, typed default, `SerializeToElement`), `NativeQamSemanticServices.cs` (result texts, required profiles, `AutoTdp` hop), `SteamStorageBridgeTests.cs`, `SteamUiSessionHostTests.cs` (storage fakes).
- **Steps:**
  1. Add the storage lock and cached projection with revision, computed on change signals; stop enumerating in the constructor.
  2. Delete the loop's else-branch `SetPatchStates` and compute patch states at the start of every pass from the current switches.
  3. Move `ChangeConfigLocked` I/O out of the lock.
  4. Remove the route and choice caps, replacing them with type/shape validation.
  5. Fix the misleading results.
  6. Delete the dispatcher `Post`.
- **Depends on:** none.
- **Tests:** storage projection with fake drive/format lists (no real `NativeStorage`); concurrent command-vs-publication on the id table; host `Apply(true)` racing a disabled-pass leaves rows enabled (fake transport; signal ordering through the existing `SessionHostTransport` fake); animation `ReadState` not blocked while a slow `writeConfig` fake runs; plugin route >256 admitted; ordered choice validated by declared set.
- **Filter:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~AnimationService|FullyQualifiedName~CommonPluginSteamUiSource|FullyQualifiedName~WsgmSteamSettings|FullyQualifiedName~NativeQam"`

### STEAMHOST-B2: one switches snapshot (about 700 lines)

- **Files:** `SteamUiSessionHost.cs`, `ShellSession.SteamUi.cs`, `ShellSession.Config.cs`, `ShellSession.cs`, new `SteamUiSurfaceSwitches` (in the host file), tests.
- **Steps:**
  1. Add the record and its pure `From(AppConfig, glyph selection, profile)`.
  2. Replace the eight `Apply*` with `Apply(switches)`.
  3. Delete the session shadow fields and guards, and route reload, BP restore and the master-enable post through one call.
  4. Keep the derived `_glyphDeliveryEnabled`/ready flags in the host.
- **Depends on:** B1.
- **Tests:** a switches table drives the expected patch enablement over the registry, for every surface, including QAM off with download sort on, carousel preference only (publication without retract) and screensaver without a timeout owner.
- **Filter:** `--filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~SteamUiSurfaceSwitches"`

### STEAMHOST-B3: backends built outside the host (about 1,200 lines)

- **Files:** `SteamUiSessionHost.cs`, new `WsgmSteamModuleCatalog.cs` (+ `SteamUiBackends`), `ShellSession.cs:1028-1110`, `CommonPluginSteamUiSource.cs` (ownership), `HomeCarousel.cs`/`LibraryBadges.cs` (stateless backends), tests.
- **Steps:**
  1. Move `CreateModules`, `ReadPages` and the change-source subscriptions to the catalog.
  2. Have the composition root build every backend, including the power profile, hybrid core, folds store and brightness fallback.
  3. Make required backends non-null and delete the overlay-test null comments.
  4. Change the host ctor to `(transport, modules, options)`.
  5. The composition root disposes what it created; unsubscribe `wsgmSettings.Refresh`.
  6. Contain a host construction failure so Steam surfaces degrade alone.
- **Depends on:** B2. The device types stay concrete until the Device domain publishes ports.
- **Tests:** the catalog declares exactly today's modules per backend presence (snapshot of patch ids and command vocabulary); a host construction failure leaves the session usable (fake throwing asset loader); the folds store is the injected one.
- **Filter:** `--filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~WsgmSteamModuleCatalog"`

### STEAMHOST-B4: NativeQam split and projection moves (about 1,000 lines)

- **Files:** `NativeQamSemanticServices.cs` → `Shell/NativeQam/*.cs`; `CapabilityProjection.cs`; `DeviceOverlayBridge.cs`; `OverlayWindow.Sources.cs`; `SteamGraphicsService.cs`; `ShellSession.Performance.cs`; `NativeQamAudioService.cs`, `NativeQamBluetoothService.cs`, `NativeQamNetworkService.cs`; tests (`NativeQamSemanticServicesTests` split per type, `NativeQamPerformanceAdapterTests`, `AutoTdpServiceTests:829`).
- **Steps:**
  1. Split per the table in section 4.
  2. Move the profile and power-limit projections.
  3. Delete `NativeQamTdpState` and `ToRange`.
  4. Use one capability subscription in the catalog.
  5. Deduplicate the range checks and timeout constant.
  6. Replace `SteamUiText.Of` call sites with local normalization only if T03 has not yet removed the helper.
- **Depends on:** B3. Port interfaces come from the Device domain (D01/D02 `DeviceCoordinator` facade: capability snapshot/changed/execute, profiles, AutoTDP and controller target). Until then the services take the coordinator.
- **Tests:** keep and port the existing projection tests, plus OSD power status reading the moved projection.
- **Filter:** `--filter "FullyQualifiedName~NativeQam|FullyQualifiedName~CapabilityProjection|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceOverlayBridge"`

### STEAMHOST-B5: SteamUiCoordinator (about 1,300 lines)

- **Files:** new `SteamUiCoordinator.cs` + `SteamUiGatePolicy`; delete `ShellSession.SteamUi.cs` and the static `SteamUiReadiness.cs`; `ShellSession.cs`, `.Config.cs`, `.Shutdown.cs`, `.Modes` hooks; `CardAcfWatcher.cs`, `CardVolumeMonitor.cs`, `KeepAwake` readiness lambda; `SteamUiSessionHost.cs` (`CloseAdmission`, `Interlocked` dispose); tests (`SteamUiTransportGateTests` → `SteamUiCoordinatorTests`).
- **Steps:**
  1. Move the gate, master switch, transitions and readiness into the instance coordinator, with one gate and every task tracked.
  2. Add `ApplyConfig(SteamUiConfig)`.
  3. Call `CloseAdmission()` at shutdown T0 and order host disposal before device/GPU retirement, or after admission is closed, under the deadline.
  4. Move `ApplyCardServices` to the storage owner (or leave it in the session if that domain has not landed, reading `coordinator.IsMasterEnabled`).
- **Depends on:** B2 and B3. The lifecycle domain supplies the `SessionLifetime` step registration and deadline (H02); an interim call from `ShellSession.Shutdown` is acceptable.
- **Tests (real coordinator, fake transport/window probe/clock):** master off retracts before close; on during retraction; BP request hold then release then restore; BP close hold; game-mode cold start waits for the window; `RunWhenReady` attempt-per-edge; shutdown closes admission before device disposal (call-order recorder); repeated shutdown; no CEF call after transport close.
- **Filter:** `--filter "FullyQualifiedName~SteamUiCoordinator|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~ShellAnchorDisposal"`

### STEAMHOST-B6: library tabs and badges as instances (about 1,250 lines)

- **Files:** `LibraryTabManager.cs` → `LibraryTabService.cs`; `LibraryBadges.cs`; `Overlay/CardManagerView.cs`, `Overlay/LibraryTabsView.cs`; `SdFormatManager.cs`; `CardAcfWatcher.cs`; `ShellSession.cs`; tests (`CardNameAuthorityTests` → `LibraryTabServiceTests`, `LibraryBadgesTests`).
- **Steps:**
  1. Convert to an instance with config transaction, scanner and Steam client ports.
  2. Use one tracked worker for boot, card and builder syncs and order pushes (STEAMHOST-010/011).
  3. Write only on change (STEAMHOST-014).
  4. Pass the views an `ILibraryTabs` interface.
  5. Remove `MutateConfigAsync`.
- **Depends on:** B5 for readiness. The config domain supplies the instance store and a changed-detection save; toolkit T02 supplies the explicit client (the interim uses the current ambient path through one adapter).
- **Tests:** sync with a fake scanner/client merges once and writes only on change; the order-push chain keeps press order and the last push wins; cancellation on desktop mode; rename orchestration steps with fake volume ports (marker first, no Steam write when the marker write fails).
- **Filter:** `--filter "FullyQualifiedName~LibraryTab|FullyQualifiedName~LibraryBadges|FullyQualifiedName~CardNameAuthority"`

### STEAMHOST-B7: toolkit-dependent deletions and host publications (about 1,400 lines)

- **Files:** `SteamUiSessionHost.cs`, `WsgmSteamModuleCatalog.cs`, `CommonPluginSteamUiSource.cs`, `SteamDownloadSort.cs`, `AnimationService.cs`, `QuickAccessFolds`/`SteamPanelFoldsBackend` (with config-domain migration), the file picker registration, `LibraryTabService`/`AnimationService` client injection, tests.
- **Steps:**
  1. Delete the quarantine mirror, the two-pass disable and the host sync loop if the manager schedules its own sync.
  2. Declare `DependsOn` (bridge) for download sort and the WSGM gates.
  3. Register and unregister plugin modules dynamically.
  4. Publish today's QAM labels, section ids/titles/icons/order and FPS suppression through the new T03 host data, together with the fold-id migration.
  5. Pass `ExistingWindowsPaths` to the file picker.
  6. Persist the startup-movie choice from partial replies.
  7. Use the explicit `SteamClient` everywhere.
- **Depends on:** toolkit T01/T02/T03 published (I01), plus the config domain migration (fold ids, `SchemaVersion`).
- **Tests:** a module failure retracts its patches through the runtime with no host code; disable removes gates before the bridge in one pass; the published host data equals today's literals (golden); fold-title migration fixture; plugin module added after host start is registered and retired on stop; partial set-aside persists.
- **Filter:** `--filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~CommonPluginSteamUi|FullyQualifiedName~AnimationService|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~SteamPanelFolds"`, plus `npm run steam-assets:build`, `steam-assets:check` and `steam-assets:claims`.

### STEAMHOST-B8: content services (about 900 lines)

- **Files:** `ThemeService.cs`, `AnimationService.cs`, new `ContentWorkSlot` (internal, Shell), `ThemeInstaller.cs`, `ThemeLoader.cs`, `ThemePaths.cs`, `InstalledTheme.cs`, `ShellSession.cs` (`Start` off the UI thread); uninstall cleanup in setup if approved (section 6, Q1); tests.
- **Steps:** apply STEAMHOST-009/010/029/030/031/032 and 015 (if approved).
- **Depends on:** B1. Setup domain for the uninstall step.
- **Tests:** save failure refuses and applies nothing for both services; a background install is joined on dispose; the dependency cycle ends by visited set; a sixth duplicate theme is reported; mklink timeout reported (fake process port).
- **Filter:** `--filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService|FullyQualifiedName~Themes|FullyQualifiedName~Animations"`

### STEAMHOST-B9: asset pipeline cleanup (about 1,100 lines)

- **Files:** `SteamDownloadSort.cs` → `Source/download-sort.ts`; `SteamUiAssetCatalog.cs`; `eng/build-steam-assets.mjs`; WSGM patch-id rename across Shell `Steam*Surface.cs` and `Source/*.ts`; `chord-reset.ts`, `controller-caps.ts` (toolkit claim helpers); optional `Source/page-kit.ts` dedup; regenerated asset; tests (`SteamDownloadSortPatchTests`, `SteamUiAssetTests`, host id assertions).
- **Depends on:** T03 (reserved prefixes, exported ownership helpers, emitted-fragment markers); B7 for `DependsOn`.
- **Tests:** emitted-asset fixtures for download-sort install/remove/report and gate claim release with a foreign wrapper on top; hash computed at load equals the bridge identity.
- **Filter:** `--filter "FullyQualifiedName~SteamDownloadSort|FullyQualifiedName~SteamUiAsset|FullyQualifiedName~SteamUiSessionHost"`, plus the three `steam-assets` npm checks.

### Order and other-domain needs

B1 → B2 → B3 → (B4 ∥ B8) → B5 → B6 → B7 → B9.

| Needed from | What |
| --- | --- |
| Device domain | Capability/power-limit/AutoTDP/controller ports (B4) |
| Lifecycle | `SessionLifetime` shutdown steps and deadlines (B5) |
| Config | Instance store, transaction port, changed-only save, fold-id migration (B6, B7) |
| Toolkit | T01 runtime quarantine/`DependsOn`/self-scheduled sync/module (un)registration; T02 explicit client; T03 host publication data, picker policy, reserved prefixes, ownership helpers (B7, B9) |
| Setup | Uninstall cleanup (B8, if approved) |

---

## 6. Risks and open questions

### Risks

- **B5** moves the BP-transition holds, which carry device-diagnosed history (`ShellSession.SteamUi.cs:129-175`). Keep the exact retract-then-close / open-then-apply order and port every existing remark. Manual M01 Steam UI rows on the Claw and the Xbox Ally X tester build are required.
- **B7/B9** change emitted JS. Use golden host-data fixtures to prove identical labels and order. A live check stays a manual matrix item.
- **STEAMHOST-002 fix:** deriving patch states per pass changes when patches flip, from immediately on `Apply` to at the next pass. That is observable only as timing within one synchronization.

### Open questions for the maintainer

1. **(STEAMHOST-015) Uninstall cleanup of Steam-side state.** Should uninstall and upgrade remove WSGM's boot-movie override and restore `.wsgm-original`, and remove or restore the `steamui\themes_custom` junction? Steam's set-aside Startup Movie choice can only be returned through live Steam UI. Should it be left with a note, or given back at WSGM exit when no WSGM movie is chosen? Recommendation: restore the files at uninstall and leave the Steam choice with a note in the uninstall log.
2. **(STEAMHOST-006) Plugin Steam UI modules.** Registered dynamically on plugin readiness (recommended), or explicitly read once at session start and documented as such?
