# Domain review: Game library, sources, import, artwork, launch commands

Reviewer: Claude Opus 5.5, read-only stage. Baseline `master` 1329813f (clean). No build, test, git mutation, Steam/CEF or live-machine action.

Scope read in full: `Shell/GameLibraryService.cs`, `Shell/GameLibraryArtwork.cs`, `Shell/SteamArtworkBrowserSource.cs`, `Shell/RunningApplicationTarget.cs`, all 10 files in `Core/Artwork/`, all 14 files in `Core/Library/`, all 13 in `Core/Library/Sources/` (Ubisoft's MiniYaml tail and AtLauncher's tail skimmed), `Core/SteamLibraryVdf.cs`, `Core/LibraryFilter.cs`, `Core/PackagedLaunchCommand.cs`, `Core/LaunchWrapperCommand.cs`, `Interop/NativePackageSource.cs`, `Interop/ShellLink.cs`. Boundary files read: `Shell/ShellSession.GameLibrary.cs`, the library/artwork composition in `ShellSession.cs:721-841`, shutdown `ShellSession.Shutdown.cs:510-582`, `Shell/ArtworkStateStore.cs`, `Shell/SteamArtworkBrowserSurface.cs`, `Shell/GameLibraryState.cs` (contracts), `ConfigStore.NormalizeGameLibrary/NormalizeArtwork`, `Overlay/ServiceSubView.Run`, toolkit `SteamUiModuleRuntime.RespondAsync/FailModule`, `SteamApps.WriteAsync`. Tests: test inventories of all domain test classes, the `GameLibraryServiceTests` harness, and the cap/getter tests cited below.

Prior coverage: `audit-coverage.md` marks U10A/U10B/U10C (this whole domain) as "unstarted Claude part; 0 saved IDs". The ledger has no library/artwork finding; the only related ledger IDs are U04A-LFA-004 (static `Log.Directory` root), PV11-005 (process-wide statics), U02A-SUTC-052 (LibraryImport without DefaultDllImportSearchPaths, toolkit), U02B-SUTC-008/012 (toolkit add-shortcut outcomes). Codex A01/A02/A02S01 findings contain nothing for these files. None of the seven admitted batches (W02_01..A02_04) touches this domain.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "`GameLibraryService` is 2,698 lines" (plan L44) | accurate | `GameLibraryService.cs` 2,698 lines | none |
| C2 | "with an existing useful injection seam" (L44) | accurate | ctor `GameLibraryService.cs:159-205`, 17 delegate/instance parameters; tests drive it with a real `ImportStateStore` on a temp path (`GameLibraryServiceTests.cs:917-1022`) | The seam is useful but over-wide: 17 positional delegates. Collapse to one Steam port plus settings (LIBRARY-B4b). |
| C3 | "one serialized apply" (L44) | accurate | `ApplyAsync` refuses while `Busy` (`:513`); every list edit refuses with `Frozen` during `Applying` (`Guard`, `:1161-1164`) | none |
| C4 | "Its lock contains scan state, choices, artwork, collection synchronization and presentation" (L44) | partially | Choices are saved to disk under `_gate` (`EditEntry :1199-1206`, `EditMany :1257-1264`); artwork options/status are read under `_gate`; presentation (`BuildState`) is built under `_gate`. Collection sync is NOT under `_gate`: it has its own `SemaphoreSlim _collectionSync` and runs outside the lock (`:1918-1983`). | Replace "collection synchronization" with "synchronous choice persistence (durable file writes) and config commits on the caller's thread". The real defect is disk IO under the lock and on the overlay UI thread (LIBRARY-006). |
| C5 | "The existing per-entry revalidation and stop-on-uncertain-write rules must survive extraction" (L44) | accurate | `Revalidate :2009-2039`, re-read before write `:1716-1727`, stop on unconfirmed `:1820-1834`, writer sends with `CancellationToken.None` after the pre-dispatch check (`SteamShortcutWriter.cs:181-220`) | Add: "uncertain" today collapses into "refused" (LIBRARY-004); the rule must survive with the toolkit's new DispatchedUnknown outcome. |
| C6 | Target row: `GameLibraryService` facade over `LibraryScanCoordinator`, `LibrarySelectionStore`, `LibraryApplyRunner`, `LibraryArtworkCoordinator`, `LibraryCollectionSync` (L70; G01 step 1) | partially | `GameLibraryArtwork` already is the artwork coordinator (`GameLibraryArtwork.cs:164`, own lock/queue/workers). `ImportStateStore` already is the selection store (`Choices/SaveChoice/PruneChoices`). Entries, phase and publish are one shared state that scan, edits and apply all mutate (`Settle :2679`, `_entries.Remove :1757`). | Over-split. Two of the five owners would duplicate existing owners, and splitting the shared review state across owners needs cross-owner locking. Keep `GameLibraryService` as the single state owner and extract stateless or self-contained pieces only (section 4). |
| C7 | "existing source implementations and import planner retained"; "Preserve ... existing source parsers" (L70, L75) | accurate | Sources are already injectable and fixture-tested (`tests/WSGM.Tests/Core/Library/*`, 10 files); `ImportPlan.Build` is pure (`ImportPlan.cs:284`) | Two planner bugs must still be fixed (LIBRARY-002, -004). |
| C8 | G01 step 4: "Serialized apply rereads owned shortcut before each write, persists acknowledged result even after cancel and stops on ambiguous/unknown outcomes; no bulk rollback" | stale (already true) | Re-read `:1719-1727`; record saved immediately after the write with no cancellable await in between (`:1836-1841`); artwork/override results saved in `finally` (`:1864-1871`); no rollback (`:33-37`) | Reword as "preserve and test"; missing tests are listed in LIBRARY-036. |
| C9 | G01 validation filter `FullyQualifiedName~GameLibrary\|~Import\|~SteamShortcut\|~SteamArtwork\|~LibrarySource` (pre-AM01 task-briefs L865) | inaccurate | Misses `PrismLauncherSourceTests`, `AtLauncherSourceTests`, `ShortcutFolderSourceTests`, `BattleNetProductDatabaseTests`, `CommandRouteTests`, `PackagedLauncherShortcutTests`, `XboxManifestTests`, `XboxRuntimeClassifierTests`, `StoreCatalogClientTests`, `ArtworkProviderTests`, `ArtworkRequestGateTests`, `SteamGridDbRetryTests`, `ArtworkConfigTests`, `RunningApplicationTargetTests`, `PackagedLaunchCommandTests`, `LaunchWrapperCommandTests` (all in namespace `WSGM.Tests.Core`/`Shell`, class names do not match) | Use the filter in section 5. |
| C10 | "Existing ... import records ... survive" migration; "imported records ... remain usable" (L28, L103) | accurate as requirement, at risk in code | `library-import.json` is versioned (`ImportState.CurrentVersion = 1`, newer refused `ImportStateStore.cs:343-348`), but `Sanitize` silently drops records by arbitrary length and the next write persists the loss (`:379-392`) | Fix LIBRARY-003 before any migration work touches this file; migration needs no transform (file path unchanged). |
| C11 | "file paths come from `Log.Directory`" (L46, ConfigStore) | accurate, incomplete | Same pattern in `ImportStateStore.cs:90` and `ArtworkStateStore.cs:14` (ledger U04A-LFA-004) | Include both stores in the `UserDataContext` consumer list. |
| C12 | Toolkit 0.2.0 consumers include "`GameLibraryService`, shortcut/artwork/collections, library tabs/badges" (api-integration L8; plan L133) | accurate, incomplete | Additional ambient-client consumers in this domain: `LibraryFilter.EvaluateAsync` (`SteamUiTransportSession.EvaluateAsync`, `LibraryFilter.cs:641`), `SteamArtwork` (all `SteamApps.*`), `SteamArtworkBrowserSource` (`SteamLibraryData.ListGamesAsync :754`, `OverlayLibraryLookup.ReadAsync :485`, `SteamApps.*Logo*`), `ShellSession.GameLibrary.cs` adapters, `SteamCollections.SyncAsync` (`ShellSession.cs:839`), `RunningApplicationMonitor` (`SteamRunningAppsProbe`) | Add these to the I01 call-site manifest. |
| C13 | "Write success publishes the written value as Observed even when readback is absent/mismatched" (L111) | accurate for add | Confirmed add with `Mismatch` records the written fields and reports the mismatch as a problem (`:1836-1845`) | none |
| C14 | B2 / plan L143 / M01-32: WSGM's `ExistingWindowsPaths` policy preserves "reachable UNC" workflows; "Existing paths remain selectable from Steam and Overlay" | partially inaccurate | The two picker consumers in this domain refuse UNC after selection: Game Library folders `GameLibraryService.cs:689` and artwork local file `SteamArtworkBrowserSource.cs:286`. Mapped network drive letters pass both checks. | Today's workflow for these two consumers is "UNC refused". Preserving behaviour means keeping both refusals; M01-32 must not expect a UNC library folder or UNC artwork image to be accepted. |
| C15 | "No bulk rollback of imported shortcuts" (L139) | accurate | `GameLibraryService.cs:33-37`, `Fail` stops without undo | none |
| C16 | "Every factory constructs inert objects. Explicit StartAsync ..." (L81) | partially | `GameLibraryService` is inert until `Start()` (accurate). `RunningApplicationMonitor` starts its loop in the constructor (`RunningApplicationTarget.cs:614`). `GameLibraryService.Start()` and `SetCollectionsAsync` spawn untracked tasks (`:1124`, `:641`). | Track and join those tasks; make `RunningApplicationMonitor` start explicitly. |
| C17 | "UI snapshots are detached values with revisions" (L115) | accurate already | `GameLibraryState` carries `_revision`, cached per revision (`ReadState :1082-1095`, `BuildState :2525`); entry ids stable (`EntryId :2212`) | No new mechanism needed here. |
| C18 | Shutdown ordering and single deadline (L89-93, B3) | partially; library blocks | `GameLibraryService.Dispose` blocks its caller up to a fixed 5 s (`DisposeWait`, `running.Wait :239`) inside the async session shutdown (`ShellSession.Shutdown.cs:513`); SessionEnd's whole budget is 5 s | Library disposal must take the caller's remaining deadline, not its own fixed wait (LIBRARY-008). |
| C19 | "No new launcher source, emulator installer" (L34) | accurate | 9 launcher sources + folders, unchanged | none |
| C20 | audit-coverage: U10A/U10B/U10C unstarted, 0 saved IDs | accurate | ledger has no IDs for this domain | This report supplies LIBRARY-001..040 as the closure for U10A-C. |
| C21 | audit-coverage U10A lists `src/WSGM/Interop/NativePackageSource.cs` as a library file | inaccurate | It is a plugin-package-source securing primitive; its only consumer is `WSGM.DeviceLab/Packaging/DeviceLabPackageSnapshot.cs:63` via a linked compile (`WSGM.DeviceLab.csproj:34`); WSGM itself never references it | Reassign to the Device Lab domain (LIBRARY-025). |
| C22 | Pre-AM01 G01 ownership "Shell/{GameLibraryService,GameLibraryArtwork,SteamArtworkBrowserSource}*; Core/Library/**, Import*, SteamShortcutWriter*, SteamArtwork*" | partially | Misses `Core/Artwork/**` providers, `Shell/ArtworkStateStore.cs`, `Shell/ShellSession.GameLibrary.cs` adapters and the library composition block in `ShellSession.cs`, `ConfigStore.NormalizeGameLibrary`, `Shell/RunningApplicationTarget.cs` (owns the Steam identity the library writes profiles under, `GameLibraryService.cs:2200`) | Use the batch file lists in section 5. |

## 2. Findings

Severity scale: critical / high / medium / low / nit. "NEW" means not in the ledger or Codex audits.

### LIBRARY-001 (high) Imported titles pin their RTSS per-app profile to WSGM.PackagedLaunch.exe
- `Shell/RunningApplicationTarget.cs:539-547`. `NormalizeShortcutTarget` treats a shortcut target as untruthful only when its name starts with `WSGM.Launch`. Every imported Xbox title and every followed launcher route has the target `WSGM.PackagedLaunch.exe` (`PackagedLauncherShortcut.Compose :72`, `CommandShortcut.TryCompose :418`), which does not match that prefix. `Project` then reports `Active` with `RtssProfileName = "WSGM.PackagedLaunch.exe"` (`:399-410`), and `ApplyForeground` never revisits an `Active` Steam state (`:195-200`), so the real game process never takes the pairing. Per-game RTSS limits/profiles silently target the launcher for the whole import feature. The test `UntruthfulShortcutTargetsNeverBecomeRtssProfiles` only lists `WSGM.Launch.exe` (`RunningApplicationTargetTests.cs:134-147`).
- NEW.
- Recommendation: one Core list of WSGM helper executable names (`WSGM.Launch.exe`, `WSGM.PackagedLaunch.exe`) taken from the existing constants `LaunchWrapperCommand.HelperFileName` and `PackagedLauncherShortcut.ExecutableName`; `NormalizeShortcutTarget` refuses any of them, which leaves the title `IdentityOnly` and lets the existing shortcut foreground fill (`:232-242`) pair the real game. No new heuristic. Add the PackagedLaunch target to the theory.

### LIBRARY-002 (medium) Two sources can adopt the same Steam shortcut
- `Core/Library/ImportPlan.cs:312-315, 481-491, 550-556, 612-614`. `Context.Unclaimed` is computed once before the loop; a shortcut adopted by one title is not marked claimed, so a second discovered title whose route composes to the same command (for example a GOG game and a `.lnk` to the same exe in a shortcuts folder) is also planned as `Adopt` of the same AppId. `Revalidate` accepts both (`GameLibraryService.cs:2036-2038`), both records name one AppId, and removing either source later deletes the shortcut the other still claims (`ImportPlan.cs:343-383`).
- NEW.
- Recommendation: add the AppId to the claimed set when a title adopts it (first in discovery order wins). The losing title is planned as a non-selectable Skip whose reason names the owner (see open question Q1). Test in `ImportPlanTests`.

### LIBRARY-003 (medium) Import records silently dropped by arbitrary length caps and then overwritten
- `Core/Library/ImportStateStore.cs:374-438`. `Sanitize` drops entries with `Source.Length > 32` or `Key.Length > 512`, entries with an unknown `Mode`, and collections by shape, with no log line; the next `Mutate` writes the sanitized state back (`:293-302`), so the loss becomes permanent. The file is "the only memory of which shortcuts are WSGM's" (`:74`). Shortcut-folder keys are relative paths (`ShortcutFolderSource.cs:169`) and can exceed 512 characters. The comment "The newest rows are kept past the bound" (`:378`) describes a bound that does not exist. `ImportStateStoreTests.AnOversizedOrUnknownRecordIsDropped` (`:34-48`) pins the caps.
- NEW. Violates the no-arbitrary-limits rule and the migration requirement (plan L28, L103).
- Recommendation: type checks only (non-null strings, non-empty identity, known mode); log each dropped row with its reason; keep `Version > Current` refusal. Change the test to assert a long key survives.

### LIBRARY-004 (medium) An uncertain shortcut update can strand the title in Conflict
- `Shell/ShellSession.cs:810-818` maps `SteamApps.SetShortcutLaunchAsync(...).Succeeded` to bool; the toolkit returns `Reachable=false` for an eval that timed out after dispatch (`SteamApps.cs:815-837`), so "maybe applied" becomes "Steam did not accept the new launch command" (`SteamShortcutWriter.cs:205-207`). The apply then keeps the old record (`GameLibraryService.cs:1820-1828`). If the write actually landed, the next scan sees live fields different from the record and plans `Conflict` "edited by hand" (`ImportPlan.cs:465-468, 526-529`), which is neither selectable nor editable (`Entry.Editable :2630`). The user cannot repair it from the UI.
- NEW (related toolkit IDs U02B-SUTC-008/012 cover the client outcome, not this consumer).
- Recommendation, without adding state or readback gating: a live shortcut that equals exactly what WSGM would compose now for the record's own title, mode and route counts as WSGM's (re-record as Skip). That makes both outcomes of an uncertain update self-heal on the next scan. When T02's typed write outcome lands, map `DispatchedUnknown` to "stop the run, no retry, keep the record", same as today's refusal.

### LIBRARY-005 (medium) Library command exceptions quarantine the whole Steam module
- `GameLibraryService.cs:571, 626, 732, 788` call `_updateSettings` → `CommitWsgmSetting` → `ConfigStore.Mutate` (`ShellSession.Config.cs:31-42`), which throws on an unreadable configuration (strict mutation rule). `OpenArtworkAsync` → `SteamArtworkBrowserSource.OpenAsync` → `ArtworkStateStore.FindGame`, whose `Read` dereferences `link.ProviderId.Length`/`filter.Tab.Length` without null checks (`ArtworkStateStore.cs:101-111`). Any exception escaping a command reaches `SteamUiModuleRuntime.RespondAsync`, which calls `FailModule` and quarantines the module for the session (`SteamUiModuleRuntime.cs:212-216, 440-455`).
- NEW.
- Recommendation: catch at the library command boundary (one private helper around the settings commit) and answer a refusal with the existing message style; make `ArtworkStateStore.Read` null-safe.

### LIBRARY-006 (medium) Disk IO and config commits run on the caller's thread and under the service lock
- Overlay commands run the backend synchronously on the UI thread (`Overlay/ServiceSubView.cs:227-239`, `operation(CancellationToken.None)`); bridge commands run on the request thread. `EditEntry`/`EditMany` do a durable `AtomicFile` write while holding `_gate` (`GameLibraryService.cs:1199-1206, 1257-1264`, `ImportStateStore.Write :451-455`); `SetSourceEnabledAsync`/`AddFolderAsync`/`RemoveFolderAsync`/`SetCollectionsAsync` commit config and probe `Directory.Exists` inline. `SteamArtworkBrowserSource.Managed` scans every Steam account's grid folder and base64-reads up to five 16 MiB files while holding `_gate` (`:139-153, 941-960, 1144-1192`), on every tab switch, load completion and outcome publish, and once per open context in `BroadcastArtwork`.
- NEW.
- Recommendation: keep the in-memory edit and publish under the lock; persist the choice after releasing it (the store has its own lock). In the artwork browser compute managed slots before entering `_gate` and assign the result. Prefer file URLs or thumbnails is a UI change and is not proposed.

### LIBRARY-007 (medium) Store records are shared mutable objects across the store boundary
- `ImportStateStore.Copy` is shallow (`:304-313`) and `Entries()` returns the cached objects (`:104-110`). `ApplyCoreAsync` mutates objects that are already inside the store's cached `_state`: `saved.OwnsProfile = false` (`GameLibraryService.cs:1852`), `saved.OwnsProfile = ownsProfile` (`:1869`), `saved.ArtworkApplied = ...` (`:1873`); on the artwork-only path `saved` is the store's own record (`:1795-1796`). This breaks the documented invariant "A write that fails leaves the remembered state as the file still has it" (`ImportStateStore.cs:292`), and a later unrelated write persists the unsaved mutation.
- NEW.
- Recommendation: make `ImportedEntry` an immutable `sealed record` with `init` setters (JSON shape unchanged with source generation) and use `with` at the three apply sites. No copy machinery.

### LIBRARY-008 (medium) Library lifetime: fixed 5 s blocking dispose and untracked background tasks
- `Dispose` blocks the caller with `running.Wait(DisposeWait)` (`GameLibraryService.cs:51, 236-244`) from the async session shutdown (`ShellSession.Shutdown.cs:513`), consuming up to the entire SessionEnd budget (B3). `Start()` (`:1124`) and the collection sync started by `SetCollectionsAsync` (`:641-660`) are untracked; the latter reads `_shutdown.Token` and can throw `ObjectDisposedException` after `_shutdown.Dispose()` (`:252`). `Run` stores `previous` and discards it (`:2372, 2410`), so a superseded scan keeps running beside its replacement.
- Partially covered by plan B3 (single deadline, retain running work); specifics NEW.
- Recommendation: `DisposeAsync(CancellationToken deadline)` that cancels, awaits the tracked run tasks until the caller's deadline, and leaves still-running work retained (the store is not disposed). Track the three task sources in the one `_running` chain. Delete `_ = previous`.

### LIBRARY-009 (medium) Library writes the live config object from a non-UI thread
- `ShellSession.cs:829-835`: `updateSettings` assigns `_config.GameLibrary = persisted.GameLibrary` on whichever thread ran the command (bridge or overlay), while configuration reload replaces `_config` on the UI thread. The assignment can land on the old instance or overwrite the reload's section. `settings: () => _config.GameLibrary` reads the same shared object under the service lock.
- Partially covered by plan L46/L64 (`SessionConfigReloader` slices).
- Recommendation: the service holds its own `GameLibraryConfig` snapshot; `updateSettings` returns the persisted section and the service adopts it; `ConfigurationChanged(GameLibraryConfig)` replaces it on reload. Remove the cross-thread field write.

### LIBRARY-010 (medium) Artwork providers are process globals and the artwork page is untestable
- `ArtworkSearch.Providers` static list (`ArtworkProviders.cs:243-244`); `SteamGridDb` static gate and `HttpClient` (`SteamGridDb.cs:107-109`); `ScreenscraperProvider` static gate and `HttpClient` (`ArtworkProviderImplementations.cs:173, 190`); `ArtworkDownload` static `HttpClient` (`ArtworkDownload.cs:30`); `ArtworkSearch.ResetCaches` mutates them all. `SteamArtworkBrowserSource` calls `ArtworkSearch`, `SteamGridDb`, `SteamArtwork`, `SteamApps`, `SteamLibraryData`, `OverlayLibraryLookup` statically and has zero tests. Tests share the static gates (cross-test cache leakage).
- PV11-005 covers process statics in general; artwork instances NEW.
- Recommendation: one `ArtworkProviders` instance built at composition and injected into `GameLibraryArtwork`, `SteamArtworkBrowserSource` and the artwork apply path; `SteamGridDbProvider` becomes the single SteamGridDB class (fold the static `SteamGridDb` into it, removing the "thin adapter" layer); gates and `HttpMessageHandler` are instance fields so tests can use a fake handler. `ArtworkRequestGate.Background()` stays (an `AsyncLocal` scope flag is the simplest priority signal).

### LIBRARY-011 (low) Artwork page stops paging early
- `SteamArtworkBrowserSource.cs:870-873, 955`. `HasMore = candidates.Length == 50 && mapped.Count > 0` is computed after `ImageHeader.IsWithinLimits` filtering, so a page where any candidate is filtered out ends paging; 50 is SteamGridDB's page size hard-coded in the consumer, and results are merged across providers.
- NEW.
- Recommendation: decide `HasMore` from the provider's own page count before filtering (return it from the provider query).

### LIBRARY-012 (low) Artwork browser background work is untracked
- `SteamArtworkBrowserSource.cs:158, 184, 210, 242, 273, 306, 330, 373, 381, 434, 453, 471, 565, 675`: every operation starts a fire-and-forget task. `Dispose` cancels and disposes `_load`/`_shutdown` while those tasks run (`:108-111`), and any later call reads `_shutdown.Token` → `ObjectDisposedException`. `ConfigurationChanged` resets the global caches once per open context (`:658-665`). Only `SelectTabAsync` links the bridge request token into the load (`:134`), so a page-side cancel aborts only that load; the other entry points do not.
- NEW.
- Recommendation: one tracked task set joined by the session's shutdown deadline; reset caches once from the parent; use the same token policy for every load (not the request token, since the command returns immediately).

### LIBRARY-013 (low) ArtworkStateStore loses saved links on a bad read; duplicate state-file mechanism
- `Shell/ArtworkStateStore.cs:79-112, 123-142`. An unreadable or unparsable `artwork.json` is logged and replaced by empty state; the next save overwrites the file (contrast `ImportStateStore.Quarantine`). Writes are non-durable. Two stores implement the same "small JSON state file" lifecycle with different failure policies (also `LaunchWrapperStore`).
- NEW (the `Log.Directory` root is U04A-LFA-004).
- Recommendation: give `ArtworkStateStore` the same read outcomes as the plan's config store (Loaded/Absent/Corrupt/Unreadable): corrupt is set aside, unreadable refuses the save. Reuse one small helper only if it removes the two copies; do not build a generic persistence layer.

### LIBRARY-014 (low) State file roots come from the static log directory
- `ImportStateStore.cs:90`, `ArtworkStateStore.cs:14`.
- Covered: U04A-LFA-004 / plan L46.
- Recommendation: take the root from the `UserDataContext` once the config domain provides it; file names unchanged, so no migration.

### LIBRARY-015 (low) Dead provider overloads and a redundant adapter layer
- `IArtworkProvider` declares unfiltered and filtered overloads with default-interface forwarding (`ArtworkProviders.cs:135-161`); `ArtworkSearch.GetAssetsForSteamAppAsync/GetAssetsForMatchAsync` without a query (`:396-401, 426-436`) are called only by tests (`ArtworkProviderTests.cs:87, 107`); `SteamGridDb` has both shapes (`SteamGridDb.cs:164-193`) and `SteamGridDbProvider` duplicates all four (`ArtworkProviderImplementations.cs:65-114`). `ScreenscraperProvider` implements only the unfiltered shape, so its "page N" silently re-returns page 0.
- NEW.
- Recommendation: one query-taking method per operation; delete the unfiltered overloads and the forwarding defaults. Screenscraper answers an empty result for page > 0 rather than repeating page 0.

### LIBRARY-016 (low) Slot vocabulary scattered across files
- `ArtworkAsset` is declared in `SteamGridDb.cs:20-36`; slot order and labels are repeated in `GameLibraryArtwork.Assets` (`:175-178`), `XboxLibrarySource.SelectArtwork` (`:597-601`), `SteamArtworkBrowserSource.AllTabs` (`:16-24`) and `Managed` (`:1144-1154`); `DataUrl` maps MIME by file extension (`:1177-1184`) although the rule is "bytes decide" (`SteamArtwork.ImageFormat :196-214`) and re-declares the 16 MiB constant (`:1172` vs `ArtworkDownload.MaximumBytes`).
- NEW.
- Recommendation: move the enum next to `ArtworkAssetNames` (one file `ArtworkAsset.cs`) carrying id, label and display order; use `ImageFormat` and `ArtworkDownload.MaximumBytes`.

### LIBRARY-017 (low) Disabled-source ids longer than 32 characters are dropped
- `Core/ConfigStore.cs:724-729` (`id is { Length: > 0 and <= 32 }`).
- NEW. Arbitrary cap; current ids fit, a future id would be silently re-enabled.
- Recommendation: non-empty check only. Coordinate with the config domain, which owns the normalizer move.

### LIBRARY-018 (low) An oversized launcher file reads as "absent"
- `Core/Library/Sources/LibraryFiles.cs:42-61` returns null for files over 16 MiB, the same as "cannot be read". Battle.net's `product.db` then contributes nothing (`BattleNetLibrarySource.cs:239, 376-381`), so titles only it knows disappear and their imports are offered for removal (unticked). Ubisoft already throws instead (`UbisoftLibrarySource.cs:132-135`).
- NEW.
- Recommendation: a read that exceeds the safety bound throws the source's "could not be read" failure, which the scan already turns into "unread, nothing offered for removal" (`GameLibraryService.cs:1466-1473`).

### LIBRARY-019 (low) A superseded scan still prunes choices
- `GameLibraryService.cs:1476-1546`: after a rescan supersedes this one, the old scan still reads the library and calls `PruneChoices` before its generation check at `:1550`.
- NEW.
- Recommendation: check generation and token before pruning.

### LIBRARY-020 (low) Change notifications run inside the service lock
- `Publish()` invokes `Changed` while `_gate` is held at every call site; `ResetArtwork` calls `GameLibraryArtwork.Reset`, which raises its own `Changed` → `OnArtworkChanged` re-enters `_gate` and publishes a second revision (`:1662-1687`, `GameLibraryArtwork.cs:275-276`).
- NEW.
- Recommendation: bump the revision under the lock and raise `Changed` after leaving it; drop the artwork stage's own `Raise()` from `Reset`/`Rematch` when called by the service.

### LIBRARY-021 (low) Store catalog client
- `StoreCatalogClient.cs:366-382` creates a new `HttpClient` per lookup; a non-HTTP exception from `_fetch` escapes and fails the whole Xbox source (`XboxLibrarySource.cs:487-489`).
- NEW.
- Recommendation: one instance-owned `HttpClient`; treat any non-cancellation lookup failure as "Store had nothing" for that title.

### LIBRARY-022 (low) Running-application monitor lifecycle and an executable-name cap
- `RunningApplicationTarget.cs:614` starts the loop in the constructor; `_profile`/`_profileAppId` are written on the loop and read by `ReportForeground` on the hook thread without the lock (`:710`); `_disposed` is unsynchronized (`:617-624`); `ValidatedGameExecutable` refuses names longer than 128 characters (`:300`).
- NEW.
- Recommendation: explicit `Start`; snapshot `_profile` under `_stateGate`; remove the 128 cap (type checks only).

### LIBRARY-023 (low) SteamLibraryVdf mixes file IO into a pure parser
- `Core/SteamLibraryVdf.cs:372-396` (`TryReadMarker` reads the file and lets `IOException` escape). This class belongs to the storage/SD-card domain (callers `SdFormatManager`, `CardVolumeMonitor`, `LibraryPolicy`, `LibraryTabManager`, `SteamStorageBridge`).
- NEW, cross-domain.
- Recommendation: storage domain owns it; split the marker read into the storage owner and keep the parser pure.

### LIBRARY-024 (low) Library tab filter uses the ambient Steam transport
- `Core/LibraryFilter.cs:629-686` (`SteamUiTransportSession.EvaluateAsync`, hard-coded 12 s budget); `FilterNode.Clone` copies 20 properties by hand (`:213-238`).
- Ambient transport covered by plan L133 (T02); rest NEW nit.
- Recommendation: take the explicit client in the T02 consumer batch; replace hand `Clone` with a record `with` if `FilterNode` becomes a record (wire names unchanged).

### LIBRARY-025 (low) NativePackageSource lives in the wrong project
- `Interop/NativePackageSource.cs` is compiled into WSGM but referenced only by Device Lab through a linked compile (`WSGM.DeviceLab.csproj:34-35`; `DeviceLabPackageSnapshot.cs:19-63`). No tests.
- NEW.
- Recommendation: move it into `WSGM.DeviceLab` (owner) and drop the link; Device Lab domain owns the batch. If WSGM's plugin installer is meant to adopt it, that is a separate decision.

### LIBRARY-026 (nit) LaunchWrapperCommand.StopRunningHelpers stops nothing
- `Core/LaunchWrapperCommand.cs:346-372` only logs active helper processes; called from the update path (`Steam.cs:441`). Process enumeration inside a command-formatting class.
- NEW.
- Recommendation: rename to what it does (`LogActiveHelpers`) and move it next to its only caller.

### LIBRARY-027 (low) Duplicate sibling-executable resolution and helper-name literals
- `PackagedLauncherShortcut.ResolveLauncher` (`:32-45`) and `LaunchWrapperCommand.HelperPathForCurrentDeployment` (`:73-77`) both derive the install folder from `Environment.ProcessPath` with an `Installer.InstallDir` fallback, with different failure handling; `RunningApplicationTarget.cs:541` hard-codes `"WSGM.Launch"`.
- NEW (root cause of LIBRARY-001).
- Recommendation: one helper for "sibling executable of this install" and the helper-name list from LIBRARY-001.

### LIBRARY-028 (nit) Dead code
- `ImportPlan.Matches` (`ImportPlan.cs:425-429`, no caller); `GameLibrarySteamTarget` and its three statics (`GameLibraryState.cs:287-297`, no caller); `CommandShortcut.Compose` (`ShortcutRoute.cs:348-353`, tests only); the unfiltered artwork overloads (LIBRARY-015); `_ = previous` (`GameLibraryService.cs:2410`).
- NEW.
- Recommendation: delete; tests use `TryCompose`.

### LIBRARY-029 (low) Source constructor scaffolding is copied nine times
- Each source re-declares 3-7 `Func` seams for the same disk operations and the same `Task.Run(() => Discover(...))` wrapper (for example `EpicLibrarySource.cs:33-79, 99-103`, `AmazonLibrarySource.cs:63-111`, `UbisoftLibrarySource.cs:49-95`, `PrismLauncherSource.cs:28-64`, `AtLauncherSource.cs:234-270`, `ShortcutFolderSource.cs:42-89`).
- NEW.
- Recommendation: one `LibraryDisk` record of the shared operations (file/dir exists, read text/bytes, list files/dirs/entries, resolve protocol, special folder), real by default, replaced whole in tests. Behaviour unchanged.

### LIBRARY-030 (low, risk) Composed launch strings are persisted ownership evidence
- Four quoting helpers with different rules: `CommandShortcut.Quote` (`ShortcutRoute.cs:512-517`), `LaunchArguments.Quote` (`LibraryFiles.cs:335-394`), `SteamCustomLaunchCommand.Quote` (`:82-85`), `PackagedLaunchCommand.Append/AppendQuoted` (`:708-734`). Records store the exact composed Target/LaunchOptions and ownership is exact comparison (`ImportPlan.OwnsRecorded :413-418`, `Recompose :569-586`). Any change in composed output turns every imported title into a preselected `Update` ("What this title's shortcut should run has changed").
- NEW.
- Recommendation: do not consolidate quoting. Add golden tests that pin the exact composed strings for each source/route shape before any refactor in this domain.

### LIBRARY-031 (nit) Stale comments
- `GameLibraryService.cs:729-730` "a full list cannot be overfilled" (there is no cap); `ImportStateStore.cs:378` "The newest rows are kept past the bound" (no bound).
- NEW. Recommendation: correct with LIBRARY-003.

### LIBRARY-032 (nit) Other arbitrary limits
- `PackagedLaunchCommand.MaximumArgumentsLength = 2048` refuses valid follow routes well under the 32,767-character Windows limit (`:131-134, 397-400`); `XboxManifest`/`MicrosoftGameConfig` compare `xml.Length` (characters) against a byte-named limit (`XboxManifest.cs:35`, `MicrosoftGameConfig.cs:317`).
- NEW.
- Recommendation: replace 2048 with the Windows command-line limit check (type/safety bound, not a product cap); rename the manifest constant to characters. Changing the 2048 refusal cannot change any composed string, so LIBRARY-030 is unaffected.

### LIBRARY-033 (nit) ShellLink native details
- `Interop/ShellLink.cs:97-110` allocates three 32K-char `StringBuilder`s per link; `msi.dll` imports (`:163-175`) have no `DefaultDllImportSearchPaths` (same class of issue as toolkit U02A-SUTC-052).
- Partially covered by U02A-SUTC-052 (toolkit only).
- Recommendation: `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` on the imports; smaller buffers are optional.

### LIBRARY-034 (nit, plausible) Store-title icon written under a .jpg name whatever its format
- `Core/Artwork/SteamArtwork.cs:370-377` writes `{appId}_icon.jpg` even when `ImageFormat` decoded png/webp/ico, contrary to the file's own "bytes decide" rule (`:191-195`). Steam may require that name.
- NEW, needs evidence before any change.
- Recommendation: leave as is; record the question for the next attended artwork test.

### LIBRARY-035 (low) UNC policy is inconsistent between consumers
- `GameLibraryService.cs:687-692` and `SteamArtworkBrowserSource.cs:284-291` refuse `\\` paths but accept mapped network drive letters; `ConfigStore.NormalizeGameLibrary` accepts a UNC folder from a hand edit (`:737-741`).
- NEW; conflicts with plan B2/M01-32 wording (C14).
- Recommendation: keep today's refusals (workflow unchanged); fix M01-32's expectation; document that mapped drives are accepted.

### LIBRARY-036 (low) Test quality and gaps
- Getter/pinning tests: `ArtworkProviderTests.BothProvidersAreDeclaredAndFindableByTheirOwnIds` asserts the static list (`:73-80`); `ImportStateStoreTests.AnOversizedOrUnknownRecordIsDropped` pins the arbitrary caps (`:34-48`).
- No tests: `SteamArtworkBrowserSource`, `SteamArtwork` apply/clear/icon flows, `ArtworkStateStore`, `UninstallEntries`, `Protobuf`, `LaunchArguments`, `LibraryFiles`, `NativePackageSource`, SteamGridDB/Screenscraper response parsing (only `IsTransient`/`RetryAfter`).
- `GameLibraryServiceTests` has no case for: Stop during apply after a write was sent (record kept), an unconfirmed add with an AppId (recorded unconfirmed), stale revalidation ("changed since the scan and was left alone"), `AddFolderAsync`/`RemoveFolderAsync`, a failing store write mid-apply, disposal during a run, command exceptions (LIBRARY-005).
- NEW.
- Recommendation: add the behavioural cases in the batches below; replace the static-list test with provider-parsing tests over a fake `HttpMessageHandler`.

### LIBRARY-037 (nit) Remembered provider failures are never pruned
- `ArtworkRequestGate.cs:37, 93-99, 156-164`: expired failure entries are removed only when the same key is asked again.
- NEW. Recommendation: drop expired entries when adding a new one. No cap.

### LIBRARY-038 (nit) Small duplications and gaps
- `ArtworkOptionsAsync` and `ReadArtworkOptions` repeat the same body (`GameLibraryService.cs:947-981, 1053-1074`); `CancelBrowsing` changes state without raising `Changed` (`SteamArtworkBrowserSource.cs:68-79`); `ApplyAsync` assigns `_launcher` before refusing (`:527`).
- NEW. Recommendation: share one private method; raise `Changed`; assign after the refusal check.

### LIBRARY-039 (nit) Public types in the application assembly
- `ArtworkAssetNames`, `SteamGridDb`, `SgdbAsset`, `ArtworkSearch`, `AmazonInstall`, `ItchCave`, `UbisoftInstall`, `ImportStateStore` etc. are `public` in the WSGM executable.
- NEW. Recommendation: `internal` while touching each file (InternalsVisibleTo already serves tests).

### LIBRARY-040 (nit, info) Credentials stored in plain config
- `ArtworkConfig.ScreenscraperUserPassword` and `SteamGridDbApiKey` are plaintext in `config.json` (`ArtworkConfig.cs:61-70`). Pre-existing; the migration must carry them unchanged (plan L28). No change proposed.

## 3. Plan refinements

Additions:
1. Add LIBRARY-001 (RTSS helper exclusion) and LIBRARY-002 (double adoption) as the first library batch. They are user-visible correctness bugs, independent of every other domain.
2. Before any code move in this domain, add golden tests pinning the composed shortcut strings (LIBRARY-030). This is the main regression risk of the whole library refactor.
3. Add to the I01 toolkit consumer manifest: `LibraryFilter.EvaluateAsync`, `SteamArtwork`, `SteamArtworkBrowserSource`, `ShellSession.GameLibrary.cs` adapters, `SteamCollections.SyncAsync`, `RunningApplicationMonitor`'s probe (C12). Map the typed write outcome as described in LIBRARY-004.
4. Add the library's command-boundary refusal rule (LIBRARY-005): no backend exception may reach `RespondAsync`.
5. Add both state stores to the `UserDataContext` consumer list (C11, LIBRARY-014) with "no file move, no migration".
6. Correct M01-32 and B2 wording: library folders and artwork local files keep refusing UNC (C14, LIBRARY-035).
7. Reassign `NativePackageSource` to Device Lab (C21, LIBRARY-025).
8. Replace the G01 test filter (C9) with the one in section 5.

Changes:
- Replace the five-owner split (C6) with: one state owner (`GameLibraryService`), two pure extractions (review entry rules, projection), three self-contained workers (scan reader, apply run, collection sync), and one Steam port. Artwork coordination stays `GameLibraryArtwork`; choice storage stays `ImportStateStore`.
- G01 step 4 becomes "preserve and test" (C8).
- Library disposal takes the caller's deadline (C18, LIBRARY-008); no fixed 5 s wait and no new shutdown phase.

Removals and over-engineering flags:
- `LibrarySelectionStore` and `LibraryArtworkCoordinator` as new types: remove (duplicates of `ImportStateStore` and `GameLibraryArtwork`).
- No generations, budgets or capacity counters are needed in this domain: the existing `_generation` already rejects stale scans/applies, the revisioned `GameLibraryState` already serves stale-publication rejection, and `ArtworkRequestGate` already paces providers. Do not add per-operation generations to `SteamArtworkBrowserSource`; it already has one (`_generation`).
- B2's picker mechanism (four-worker cap, per-root busy, `ProviderCapacity`, five-second request wait) protects UNC/stalled-provider browsing that the two library consumers then refuse anyway. From this domain's point of view the simpler shape is enough: enumerate on a worker, cancel on navigation, drop stale generations. Flagged for the toolkit/picker reviewer; no change requested here.
- No-arbitrary-limits: remove the caps in LIBRARY-003, -017, -022, -032; keep the 16 MiB image/64 MiB cache/4 MiB JSON safety bounds but make an exceeded bound a reported failure, never a silent "absent" (LIBRARY-018).

## 4. Target design

Owners (all in `src/WSGM`, namespace unchanged):

| Owner | Responsibility | State |
| --- | --- | --- |
| `GameLibraryService` (Shell) | Single owner of the review state (entries, phase, notes, error, progress, revision), command admission and publication | `_gate`, `_entries`, `_phase`, `_generation`, tracked `_running` |
| `LibraryReviewEntry` (Core/Library, new file) | The former nested `Entry` plus its pure rules: `Create` (choice overlay), `ChoiceOf`, `Refusal`, `Excludable`, `Placeholder`, `EntryId`, `FolderId` | none (instance data only) |
| `GameLibraryProjection` (Shell, new file) | Pure projection to `GameLibraryState`/`GameLibraryEntry`/details: `BuildState`, `Project`, `Slot`, `GroupOf`, `ActionLabel`, `ModeLabel`, `StatusName`, `Preferred`, `IndexOf`, `Pick`, `Options` (over an artwork reader) | none |
| `GameLibraryScan` (Shell, new file) | Detection and discovery read phase: `DetectSources`, source fan-out, library read, record read, `ImportPlan.Build`; returns a `LibraryScanResult` the service applies under its lock | none |
| `GameLibraryApply` (Shell, new file) | One apply run: `Revalidate`, `TryCompose`, writer calls, record saves, controller override, images; reports per-entry outcomes through a small callback interface (`Settled`, `Removed`, `Note`, `Progress`) | per-run locals only |
| `GameLibraryCollections` (Shell, new file) | Collection sync with its semaphore, `CollectionGroup` | `_sync` semaphore |
| `ILibrarySteam` (Shell, in `ShellSession.GameLibrary.cs`) | Port: read library, read shortcut, add, update, remove, apply images, open artwork, sync collection; implemented once by `SteamLibraryClient` over the toolkit client | none |
| `GameLibraryArtwork` | unchanged owner of candidate gathering | unchanged |
| `ImportStateStore` | unchanged owner of records/choices/collections; immutable `ImportedEntry` | unchanged |
| `ArtworkProviders` (Core/Artwork, replaces static `ArtworkSearch`) | Instance list of providers; search, match, gather, download, official assets, reset | provider instances |
| `SteamGridDbProvider` (Core/Artwork) | The single SteamGridDB client (folds static `SteamGridDb`) | instance gate, `HttpClient` |
| `ScreenscraperProvider` | instance gate and `HttpClient` | instance |
| `SteamArtworkWriter` (Core/Artwork, replaces static `SteamArtwork`) | Apply/clear/icon writes and custom-art lookup over the toolkit client and `ArtworkProviders` downloads | none |
| `LibraryDisk` (Core/Library/Sources, new record) | Shared disk/registry seam for sources | none |

Dissolved files and old-symbol → new-owner table:

| Old symbol (file) | New owner |
| --- | --- |
| `GameLibraryService.Entry` (nested class) | `LibraryReviewEntry` |
| `GameLibraryService.Create`, `ChoiceOf`, `Refusal`, `Excludable`, `Placeholder`, `EntryId`, `FolderId` | `LibraryReviewEntry` (static members) |
| `GameLibraryService.BuildState`, `Project`, `Slot`, `GroupOf` (both), `ActionLabel`, `ModeLabel`, `StatusName`, `Preferred`, `IndexOf`, `Pick`, `Options`, `Progress`, `SourceNames`, `NotEditable` | `GameLibraryProjection` (`ModeLabel` keeps `internal static`; callers updated) |
| `GameLibraryService.DetectSources`, `Record`, read half of `ScanCoreAsync`, `Sources` | `GameLibraryScan` (`Record` and state application stay in the service) |
| `GameLibraryService.ApplyCoreAsync`, `Revalidate`, `TryCompose`, `WriteControllerTargetAsync`, `ApplyImagesAsync`, `Images`, `ReleaseControllerTargetAsync`, `Identity` | `GameLibraryApply` |
| `GameLibraryService.SyncCollectionsAsync`, `CollectionGroup`, `_collectionSync` | `GameLibraryCollections` |
| `GameLibraryService.Dispose`, `DisposeWait` | `DisposeAsync(CancellationToken)` on the service; `DisposeWait` deleted |
| `GameLibraryService` ctor delegates `writer`, `readLibrary`, `readShortcut`, `applyArtwork`, `openArtwork`, `syncCollection` | `ILibrarySteam` |
| `GameLibraryService` ctor `settings`/`updateSettings` | `GameLibraryConfig` snapshot + `Func<Action<GameLibraryConfig>, GameLibraryConfig>` commit returning the persisted section |
| `ShellSession.GameLibrary.ReadShortcutsAsync`, `ReadShortcutAsync`, `AddShortcutAsync` | `SteamLibraryClient : ILibrarySteam` (same file) |
| `GameLibraryState.GameLibrarySteamTarget` | deleted |
| `ArtworkSearch` (static) | `ArtworkProviders` (instance) |
| `ArtworkSearchProviders` (GameLibraryArtwork.cs) | deleted; `ArtworkProviders` implements `IGameLibraryArtworkProviders` directly with a config reader |
| `SteamGridDb` (static class: `ResolveKey`, `KeyPageUrl`, `SearchGamesAsync`, `GetAssets*`, `GetOfficialAssets*`, `IsTransient`, `RetryAfter`, `ImageExtension`, `ResetCache`, `Gate`, `Http`) | `SteamGridDbProvider` (`IsTransient`/`RetryAfter`/`ImageExtension` stay `internal static` on it) |
| `ArtworkAsset` enum (SteamGridDb.cs) | `ArtworkAsset.cs` beside `ArtworkAssetNames` (values unchanged: they are Steam's eAssetType and persisted in `ArtworkPick.Asset`) |
| `SgdbAsset`, `SgdbGame`, `SgdbOfficialAsset`, `SteamGridDbException` | stay, moved with `SteamGridDbProvider` |
| `SteamArtwork` (static) | `SteamArtworkWriter` (instance); `ImageFormat` stays `internal static` |
| `IArtworkProvider` unfiltered overloads and default forwarding | deleted |
| `ImportPlan.Matches`, `CommandShortcut.Compose` | deleted |
| `LaunchWrapperCommand.StopRunningHelpers` | `Steam.LogActiveLaunchHelpers` (private, at its caller) |
| `PackagedLauncherShortcut.ResolveLauncher` / `LaunchWrapperCommand.HelperPathForCurrentDeployment` | both call one `InstallLayout`-style sibling-executable helper; public names kept |
| `Interop/NativePackageSource.cs` | `WSGM.DeviceLab` project (Device Lab domain) |

Public/internal API changes and every consumer that must change:
- `GameLibraryService` constructor and `Dispose` → `ShellSession.cs:790-841`, `ShellSession.Shutdown.cs:513-524`, `GameLibraryServiceTests.Harness`, `WSGM.UiTests/Fakes/OverlayToolsSources.cs` (fake implements the interface only; unaffected unless `IGameLibraryOverlaySource` changes, which it does not).
- `ArtworkSearch` → `ArtworkProviders`: `GameLibraryService.cs:1015, 1028`, `GameLibraryArtwork.cs:93-136`, `SteamArtworkBrowserSource.cs:665, 687, 695, 772, 799, 865-866, 1012-1030`, `SteamArtwork.cs:54, 90`, `ShellSession.cs:725, 792, 822`, `ArtworkProviderTests`, `GameLibraryArtworkTests`.
- `SteamArtwork` → `SteamArtworkWriter`: `SteamArtworkBrowserSource` (apply/clear/managed), `ShellSession.cs:822`, plus any overlay artwork caller (`Overlay/ArtworkView.cs` uses the browser source, not `SteamArtwork`).
- `ImportedEntry` becomes a record: `ImportStateStore`, `GameLibraryService.ApplyCoreAsync`, `ImportPlan` (reads only), `ImportStateStoreTests`, `ImportPlanTests`, `GameLibraryServiceTests` (object initializers keep compiling with `init`).
- `SteamArtworkBrowserSource` constructor gains `ArtworkProviders` and `SteamArtworkWriter` → `ShellSession.cs:725`.
- `LibraryDisk` → all nine source constructors and their test files in `tests/WSGM.Tests/Core/Library/`, `XboxLibrarySourceTests` unaffected (Xbox keeps its package seams).
- `RunningApplicationTarget` helper list → `RunningApplicationTargetTests`.

## 5. Implementation batches

Narrow test filter for every batch in this domain (replaces C9):

```
dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase"
```

(`~Library` also selects `LibraryFilterTests`, `LibraryBadgesTests`, `CardLibraryDecisionTests`; harmless.)

### LIBRARY-B0 Golden composition tests (prerequisite, test-only)
- Files: new `tests/WSGM.Tests/Core/Library/ComposedShortcutGoldenTests.cs`.
- Steps: for each source's route shapes (Xbox packaged both modes with/without multiplayer acknowledgement, Epic launcher+direct, GOG direct+Galaxy, Ubisoft, Battle.net launcher+classic, Amazon both orders, itch, Prism, ATLauncher, folder `.exe`/`.lnk`/`.url`, follow with dir/marker and drive-root refusal) assert the exact `ShortcutFields` strings produced today.
- Dependencies: none. Tests: the file itself. ~350 lines.

### LIBRARY-B1 Correctness fixes
- Files: `Shell/RunningApplicationTarget.cs`, `Core/LaunchWrapperCommand.cs`, `Core/Library/PackagedLauncherShortcut.cs`, `Core/Library/ImportPlan.cs`, `Core/Library/ImportStateStore.cs`, `Shell/GameLibraryService.cs` (apply `with`, command-boundary refusal, prune generation check, stale comments, `_launcher` order, duplicate options body), `Shell/ArtworkStateStore.cs` (null-safe read), `Core/ConfigStore.cs` (`DisabledSources` cap only), `Shell/SteamArtworkBrowserSource.cs` (`HasMore`), tests.
- Steps: (1) helper-name list and `NormalizeShortcutTarget` refusal (LIBRARY-001, -027 name part); (2) claim adopted AppIds in `ImportPlan.Build` (LIBRARY-002, decision Q1 default applied); (3) `ImportedEntry` → `sealed record` with `init`, apply sites use `with` (LIBRARY-007); (4) `Sanitize` type checks only, log drops (LIBRARY-003, -031); (5) refuse instead of throw at the four settings commands and `OpenArtworkAsync` (LIBRARY-005); (6) generation/token check before `PruneChoices`, delete `_ = previous` (LIBRARY-019); (7) remove the 32-char `DisabledSources` cap (LIBRARY-017); (8) `HasMore` from the unfiltered provider page (LIBRARY-011); (9) ownership tolerance for "equals what WSGM would compose now" in `OwnsRecorded` callers (LIBRARY-004 part 1).
- Dependencies: B0 green first. Config domain must agree to the one-line normalizer change (or take it into its own batch).
- Tests: `NormalizeShortcutTarget` with `WSGM.PackagedLaunch.exe` → untruthful; two sources one shortcut → one Adopt; long key survives reload; failed `SaveApplied` leaves store state equal to disk; `SetSourceEnabledAsync` with throwing commit → refusal, no exception; superseded scan does not prune; landed-but-unreported update re-records as Skip; update the cap test; artwork paging with one filtered candidate keeps `HasMore`.
- ~900 changed lines.

### LIBRARY-B2 Artwork providers as instances
- Files: `Core/Artwork/*` (merge `SteamGridDb.cs` into `ArtworkProviderImplementations.cs` or a `SteamGridDbProvider.cs`, new `ArtworkAsset.cs`, `ArtworkProviders.cs` instance), `Shell/GameLibraryArtwork.cs` (delete adapter), `Shell/ShellSession.cs` composition, `Core/Library/XboxLibrarySource.cs` (slot order from `ArtworkAsset`), tests.
- Steps: instance `ArtworkProviders` with injected `HttpMessageHandler`; fold static `SteamGridDb` into `SteamGridDbProvider`; delete unfiltered overloads and forwarding defaults (LIBRARY-015); Screenscraper returns empty for page > 0; move enum and labels (LIBRARY-016); prune expired failures on insert (LIBRARY-037); one `ResetCaches` call per configuration change; `internal` visibility while touching (LIBRARY-039). `SteamArtwork` keeps its static shape this batch but takes `ArtworkProviders` for downloads.
- Dependencies: none.
- Tests: replace `BothProvidersAreDeclared...` with parsing tests for SteamGridDB search/assets/official assets and Screenscraper search/media over a fake handler; existing gate/retry tests on instance gates; `GameLibraryArtworkTests` unchanged behaviour.
- ~1,300 changed lines.

### LIBRARY-B3 Artwork browser lifecycle and IO off the lock
- Files: `Shell/SteamArtworkBrowserSource.cs`, `Shell/ArtworkStateStore.cs`, `Core/Artwork/SteamArtwork.cs` → `SteamArtworkWriter`, `Shell/ShellSession.cs`, `ShellSession.Shutdown.cs`, new `tests/WSGM.Tests/Shell/SteamArtworkBrowserSourceTests.cs`.
- Steps: compute managed slots outside `_gate`, MIME from `ImageFormat`, shared 16 MiB constant (LIBRARY-006 artwork half, -016); tracked task set, `DisposeAsync(CancellationToken)`, no token access after dispose, one cache reset from the parent, uniform load token (LIBRARY-012); `CancelBrowsing` raises `Changed` (LIBRARY-038); `ArtworkStateStore` read outcomes (corrupt set aside, unreadable refuses save, durable write) (LIBRARY-013); `SteamArtwork` → instance writer injected into the browser source.
- Dependencies: B2. Shutdown domain's deadline token (until then pass the session's existing cancellation).
- Tests: open/select/load-more/stale-generation drop/dispose-during-load with fake providers and a temp grid folder; corrupt `artwork.json` is set aside, not overwritten.
- ~1,000 changed lines.

### LIBRARY-B4a GameLibraryService pure extractions
- Files: `Shell/GameLibraryService.cs`, new `Core/Library/LibraryReviewEntry.cs`, new `Shell/GameLibraryProjection.cs`, `Shell/GameLibraryState.cs` (delete `GameLibrarySteamTarget`), `Core/Library/ImportPlan.cs` (delete `Matches`), `Core/Library/ShortcutRoute.cs` (delete `Compose`), tests.
- Steps: move `Entry` and its rules; move projection; delete dead code (LIBRARY-028); share the options body (LIBRARY-038). No behaviour change.
- Dependencies: B1.
- Tests: projection unit tests over hand-built entries (group/slot/label rules) replacing nothing; full `GameLibraryServiceTests` unchanged and green.
- ~900 changed lines (mostly moves).

### LIBRARY-B4b GameLibraryService workers, port, threading and lifetime
- Files: `Shell/GameLibraryService.cs`, new `Shell/GameLibraryScan.cs`, `Shell/GameLibraryApply.cs`, `Shell/GameLibraryCollections.cs`, `Shell/ShellSession.GameLibrary.cs` (`ILibrarySteam`, `SteamLibraryClient`), `Shell/ShellSession.cs` (composition, remove `_config.GameLibrary =` write), `Shell/ShellSession.Config.cs` (`ConfigurationChanged(GameLibraryConfig)`), `Shell/ShellSession.Shutdown.cs`, `Core/Library/SteamShortcutWriter.cs` (built over the port), tests.
- Steps: extract scan/apply/collections; 17 ctor parameters → port + settings snapshot (LIBRARY-009); persist choices and commit settings outside `_gate`, raise `Changed` after the lock, drop the artwork stage's nested `Raise` on service-initiated resets (LIBRARY-006 library half, -020); track `Start` and collection tasks, `DisposeAsync(deadline)` (LIBRARY-008).
- Dependencies: B4a. Config domain's reload slice API if available (otherwise keep a `Func<GameLibraryConfig>` reader for this batch). Shutdown domain's deadline token.
- Tests: Stop after a sent write keeps the record; unconfirmed add with AppId recorded unconfirmed; stale revalidation leaves the title alone; `AddFolderAsync`/`RemoveFolderAsync` (UNC refused, mapped path accepted, duplicate refused); store write failure mid-apply stops and reports; `DisposeAsync` during a blocked apply returns at the deadline and the late completion still records; `Changed` raised outside the lock (subscriber that takes another lock does not deadlock).
- ~1,400 changed lines.

### LIBRARY-B5 Toolkit 0.2.0 consumers in this domain (part of I01)
- Files: `Shell/ShellSession.GameLibrary.cs` (`SteamLibraryClient`), `Core/Artwork/SteamArtworkWriter`, `Shell/SteamArtworkBrowserSource.cs`, `Core/LibraryFilter.cs`, `Shell/RunningApplicationTarget.cs` (probe), `Shell/ShellSession.cs` (collections), tests.
- Steps: take the explicit `SteamClient`; map add/update/remove typed outcomes: Applied → confirmed, Refused → refused, DispatchedUnknown → stop run, no retry, record kept (add with AppId → unconfirmed record) (LIBRARY-004 part 2); `LibraryFilter.EvaluateAsync` takes the client and keeps the 12 s budget as a parameter (LIBRARY-024).
- Dependencies: Steam UI toolkit domain T01/T02 published API and I01 hashes.
- Tests: `SteamLibraryClient` mapping table over a fake client; library apply with DispatchedUnknown stops without retry.
- ~700 changed lines.

### LIBRARY-B6 Source scaffolding, shared helpers and misplaced code
- Files: `Core/Library/Sources/*.cs`, `Core/Library/Sources/LibraryFiles.cs`, new `Core/Library/Sources/LibraryDisk.cs`, `Core/Library/StoreCatalogClient.cs`, `Core/LaunchWrapperCommand.cs`, `Core/Steam.cs`, `Core/Library/PackagedLauncherShortcut.cs`, `Core/PackagedLaunchCommand.cs`, `Interop/ShellLink.cs`, `Core/Library/XboxManifest.cs`, `Core/Library/MicrosoftGameConfig.cs`, `Shell/RunningApplicationTarget.cs`, `tests/WSGM.Tests/Core/Library/*`.
- Steps: `LibraryDisk` record (LIBRARY-029); oversize read is a failure (LIBRARY-018); one `HttpClient` and broader lookup failure handling in `StoreCatalogClient` (LIBRARY-021); move/rename `StopRunningHelpers` (LIBRARY-026); one sibling-executable helper (LIBRARY-027); command-line limit instead of 2048, character-named manifest constant (LIBRARY-032); `DefaultDllImportSearchPaths` on msi imports (LIBRARY-033); explicit `RunningApplicationMonitor.Start`, snapshot `_profile` under lock, drop the 128 cap (LIBRARY-022).
- Dependencies: B0 golden tests must stay green (composition unchanged). Device Lab domain takes `NativePackageSource` (LIBRARY-025) in its own batch.
- Tests: sources' existing fixtures through `LibraryDisk`; Battle.net oversized `product.db` → source failure, nothing offered for removal; `UninstallEntries.FindProgram`, `LaunchArguments` and `Protobuf` unit tests; monitor does not observe before `Start`.
- ~1,200 changed lines.

### LIBRARY-B7 State roots from UserDataContext
- Files: `Core/Library/ImportStateStore.cs`, `Shell/ArtworkStateStore.cs`, `Shell/ShellSession.cs`, tests.
- Steps: constructors take the root directory from `UserDataContext`; file names unchanged; tests already use temp paths.
- Dependencies: config domain's `UserDataContext` (F01, U04A-LFA-004).
- ~120 changed lines.

Order: B0 → B1 → (B2 → B3) and (B4a → B4b) in either order, serialized → B5 after toolkit I01 → B6 → B7 after config domain.

Tests deferred in this read-only stage: all of them; nothing was built or run.

## 6. Risks and open questions

Risks:
- Composed-string drift (LIBRARY-030) would silently turn every imported title into a preselected Update; B0 golden tests are the guard and must precede any move.
- LIBRARY-001's fix changes RTSS pairing for imported titles from "the launcher" to the existing foreground fill for shortcuts, which inherits that rule's known weakness (focus on a non-game window). It is still strictly better than pairing a process that never renders; the attended check belongs in M01-30.
- Records already dropped by the old caps are gone from disk; the fix cannot restore them (next scan offers Adopt, which recovers ownership for packaged and exact-route titles).
- B4b moves the apply loop; the per-entry re-read, record-before-anything-slower and no-rollback rules must be moved byte-for-byte and kept under the existing behavioural tests.

Open questions for the maintainer:
- Q1 (LIBRARY-002): when two sources resolve to the same existing Steam shortcut, should the second title show as a non-selectable "already imported from <source>" (default proposed, no duplicate Steam entry), or be offered as a fresh Add (a second Steam entry)?
