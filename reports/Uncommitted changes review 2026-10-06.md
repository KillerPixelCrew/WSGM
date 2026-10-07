# Uncommitted changes review, 2026-10-06

This report combines five reviews of the uncommitted working tree on `master`: a correctness review
(`/code-review xhigh`) and four cleanup reviews (reuse, simplification, efficiency, altitude). Each
issue appears once, with overlapping reports merged. Tags show which reviews raised it: **CR**
code-review, **Ru** reuse, **Si** simplification, **Ef** efficiency, **Al** altitude.

Scope:

- About 4,800 lines of changes to tracked files under `src`, `tests` and `eng`.
- About 7,000 lines in 21 new files.
- The uncommitted changes inside `external/steam-ui-toolkit`.
- `NativeQamBootstrap.js` is generated, so its `.ts` sources were reviewed instead.

Nothing was edited, built or run. All tests are deferred.

## 1. Bugs

1. `src/WSGM.PackagedLaunch/Session/ManagedContentLaunch.cs:171`: the catch filter misses
   `InvalidDataException`. That exception is thrown for a missing or incomplete record, a newer
   store version, a path that escapes its root, and a command over 32767 characters. The helper then
   crashes with no message box and no log line. (CR, Al)
2. The emulator code and the monitor also let `InvalidDataException` and `JsonException` escape.
   (CR, Al)
   - `src/WSGM/Core/Emulators/EmulatorManager.cs:46`: the importer is lost for the whole session.
   - `src/WSGM/Shell/GameLibraryService.cs:1656`: every scan fails, Epic and GOG included.
   - `src/WSGM/Shell/ManagedShortcutMonitor.cs:397`: the monitor dies silently.
3. `src/WSGM/Shell/GameLibraryService.cs:1860`: `ContentChanged` ignores Program, WorkingDirectory,
   RawArguments, LaunchKind, Follow\* and RequiredPaths, so the helper keeps launching the old
   command after an edit. It also forces a pointless Steam Update when only WSGM's own record
   changed. (CR, Si, Al)
4. `src/WSGM/Core/Library/LibrarySource.cs:109`: `manageContent` defaults to true, so every launcher
   title becomes a `--managed` launch. (CR, Si, Ef, Al)
   - This contradicts docs/game-library.md: "Existing packaged and launcher-follow routes retain
     their own mechanisms."
   - Existing launcher shortcuts get planned as a preselected Update.
   - `src/WSGM/Core/Library/ImportPlan.cs:411/414` means uninstalled launcher games are never
     offered for removal ("Content is unavailable").
   - Availability gets published for all of them, which force-enables the badge patch even with the
     LibraryBadge switch off.
5. `src/WSGM.PackagedLaunch/Session/ManagedContentLaunch.cs:230`: `CreateProcessW` from an asInvoker
   helper has no elevation path. Folder imports with a `requireAdministrator` manifest fail with
   ERROR_ELEVATION_REQUIRED and can no longer launch. (CR)
6. Screenscraper ROM matching fails instead of falling back. (CR, Ru)
   - `src/WSGM/Core/Artwork/ArtworkProviderImplementations.cs:274`: the name-search fallback in
     `SearchRomAsync` is unreachable, because `FetchAsync` throws on the 404 for an unknown ROM.
   - The new `FindMatchAsync` overload (`src/WSGM/Core/Artwork/ArtworkProviders.cs:324`) drops the
     argument validation and per-provider failure handling of the existing overload. A provider
     failure fails the whole match, a KO request is spent and SteamGridDB is never asked.
7. `src/WSGM/Shell/GameLibraryService.cs:776`: folder ids are now random GUIDs. Removing a folder
   and adding it back gives it a new id, the orphaned records keep claiming the old AppIds, and
   every game is planned as Add, so every Steam shortcut is duplicated. `FolderId` (documented as
   "stable for the path") is now dead. (CR, Ru, Si, Al)
8. `src/WSGM/Shell/GameLibraryService.cs:255`: `CancelAsync` also calls `_emulators?.Cancel()`. The
   importer's Cancel aborts an emulator install, and the emulator page's "Stop operation" aborts a
   library apply partway through its Steam writes. (CR)
9. `src/WSGM/Shell/SteamUiSessionHost.cs:1344`: `OnLibraryBadgesChanged` calls `SetPatchStates`
   without `_switchGate`, from whatever thread raised the event, and never calls
   `SetGlobalEnabled(true)`. It can re-enable patches a switch change just disabled, and the badge
   patch never runs when no other patch is wanted. (CR)
10. `src/WSGM/Core/Emulators/EmulatorManager.cs:326`: each install, update or repair activates into
    a new `versions/<hash>-<guid>` folder and old ones are never pruned. Repeated RetroArch updates
    fill a handheld's disk. (CR)
11. `src/WSGM/Core/Emulators/EmulatorManager.cs:508`: `DiscoverExternalAsync` runs on every refresh
    and re-registers an external install the user forgot. (CR)
12. `src/WSGM/Shell/GameLibraryArtwork.cs:334`: the persistent artwork cache is not keyed by the
    provider signature, and `ArtworkSearch.ResetCache` doesn't clear it. An empty result cached
    before an API key was added replays for 30 days. (CR, Al)
13. `src/WSGM/Shell/GameLibraryArtwork.cs:595`: the hard-coded `PauseReason("screenscraper")` labels
    other providers' failures as a Screenscraper quota pause and marks them QuotaPaused. (CR, Ru,
    Si, Al)
14. `src/WSGM/Shell/GameLibraryService.Roms.cs:103` and the overlay's `SplitRomValues`: ROM
    extensions are stored verbatim, with no leading dot or lowercasing. Typing "chd iso" matches
    nothing, and `GameLibraryRules` then drops them on reload. (CR)
15. `src/WSGM/Shell/GameLibraryService.Roms.cs:418`: `RecheckAvailabilityAsync` resolves the
    emulator only when an explicit installation id is set. It disagrees with the scan, the monitor
    and the helper, so the badge flips on each pass. (CR)
16. `external/steam-ui-toolkit/.../gates/library-badge.ts`, `libraryForOverview`:
    `shortcut?.location || library!.name` throws a TypeError inside Steam's tile render when
    Location is empty. (CR)
17. `src/WSGM/Core/Library/Sources/EaLibrarySource.cs`: one malformed `installerdata.xml`
    (XmlException) or an unreadable root (raw `Directory.EnumerateDirectories`) fails the whole
    source. It should use `LibraryFiles.Directories`. (CR, Ru)
18. `RepairAsync` is the same as `UpdateAsync`, so it installs a version the user skipped.
    `InstallAsync` on a second channel silently takes over the existing managed installation. (CR,
    Si)
19. The Buildbot release id includes the nightly core-index hash, so stable RetroArch offers an
    "update" almost daily and "Skip this version" lasts about a day. (CR)
20. `InstallCoresAsync` doesn't cancel the remaining core downloads after the first failure. (CR)
21. `CleanupEntry(false)` resets the plan to Skip and drops a pending Update until the next rescan.
    (CR)
22. `setPreferredEmulator` refuses a blank installation id, so a system default can't be cleared.
    (CR)
23. `ConfiguredPrerequisites` stores the user's source path, not the copied destination, so the
    monitor watches folders like Downloads. (CR)
24. `src/WSGM/Core/SteamUiAssets/Source/library-import.ts:1419`: the sidebar changed from
    `source.installed && source.enabled` to `checked: source.enabled`, so uninstalled launchers show
    as ticked. (CR, Al)
25. `src/WSGM/Shell/GameLibraryService.Roms.cs:179,315`: removing a ROM or manual source drops the
    `Guard()` and the `DisabledSources` cleanup that `RemoveFolderAsync`
    (`GameLibraryService.cs:810`) has. (Ru)
26. System ids disagree: `RomProfiles.All` (`src/WSGM/Core/Library/RomLibrarySource.cs:101-122`)
    uses `megadrive`, while `emulators.json` RetroArch and the `EmulatorPackages.cs:319-339` alias
    table use `genesis`. (Ru)
27. Argument limits disagree. The TS page and the overlay cap the argument count at 32
    (`library-import.ts:551`, `GameLibraryView.Arguments.cs:47`) and the C# reader at 256. The page
    allows 2048 characters per argument, but `SteamLibraryImportSurface.Managed.cs:106-113` refuses
    strings over 512, so valid arguments are rejected. (Si, Al)
28. `ManagedShortcutMonitor.Resolve` (`ManagedShortcutMonitor.cs:919-955`) uses a plain
    `StartsWith(root)` escape check, which differs from `ManagedContentStorage.ResolvePath` (the
    root-equal case). (Ru, Si)
29. `RomLibrarySource.Walk` (`RomLibrarySource.cs:311-337`) throws on the first access-denied
    subfolder; `LibraryFiles.List` (`Sources/LibraryFiles.cs:112`) skips them. (Ru)
30. `GameLibraryArtwork.SaveCacheAsync` (`GameLibraryArtwork.cs:693-734`): the fixed `.tmp` name can
    race, and the file isn't cleaned up on failure. `AtomicFile.WriteAsync`
    (`src/Shared/Boot/AtomicFile.cs:92`) handles both. (Ru)
31. The toolkit's `SteamApps.SetShortcutNameAsync` (`Client/SteamApps.cs:269`) fails unless a
    2-second readback confirms the name, which breaks the "never gate a write on readback" rule. Its
    sibling `SetShortcutLaunchAsync` writes and trusts. (Ru, Si)
32. `src/WSGM/Core/Library/ImportPlan.cs:497` compares start folders with `SameProgram`, while
    `ShortcutRoute` uses `SameFolder`. (Si)
33. The test `EmulatorToolIsIndependentOfLibraryAndOpensDedicatedPage` doesn't match how the host
    wires the manager, which is only created inside `TryStart("game library")`, the QAM row only
    when `libraryImport` is non-null (`SteamUiSessionHost.cs:217`), and the tile only when the
    library is attached (`OverlayWindow.Sources.cs:166`). (Al)

## 2. Altitude and design

1. The "validate content, then resolve the emulator" check exists six times, and the messages and
   the RetroArch rule already differ. Replace them with one
   `ManagedContentStorage.Check(record, EmulatorStore)`. (all five reviews)
   - `ManagedContentStorage.Validate` (`src/Shared/Library/ManagedContentModels.cs:325`)
   - `ManagedShortcutMonitor.Check`/`Resolve` (`ManagedShortcutMonitor.cs:919-1032`)
   - the scan at `GameLibraryService.cs:1681-1717`
   - `GameLibraryService.Roms.cs:262-275` and `:412-432`
   - `RomLibrarySource.cs:268-294`
   - `ManagedContentLaunch.cs:66,118`
2. `ManagedShortcutMonitor` is 1069 lines of mechanism. (Si, Al, Ef, Ru)
   - It has eight triggers, a topology epoch, signatures, a 400 ms debounce, a 5-minute poll, a
     network probe pool with a 2.5 s deadline and a 2-probe cap, and a `MaximumWatchRoots = 8` cap.
   - It runs per-path FileSystemWatchers, a watcher on its own `library-import.json`, and
     `MountNameWatcher` (`src/WSGM/Interop/MountNameWatcher.cs`), which duplicates the existing
     `MessageWindow` volume notifications, blocks a LongRunning thread and re-declares `CreateFileW`
     with magic numbers.
   - It forces the Suspend/Resume hooks in `RemovableDriveManager`, `SdFormatManager` and
     `ShellSession.cs:246-254`.
   - Replacement: on a volume change, resume, library change or explicit recheck, call
     `GameLibraryService.RecheckAvailabilityAsync("")` and publish badges from the store.
3. The monitor re-parses `library-import.json` from disk through `ManagedContentStorage.ReadEntries`
   although `ImportStateStore` already holds it in memory. The hard-coded `number > 2`
   (`ManagedContentModels.cs:189`) duplicates `ImportState.CurrentVersion`
   (`ImportStateStore.cs:16`), and that version bump is compatibility code on unreleased 2.0. (Ru,
   Si, Ef, Al)
4. `LastSuccessfulValidationUtc` (`ManagedContentModels.cs:119-120`, `ImportStateStore.cs:120-140`)
   exists only to rewrite itself. Together with the poll, it rewrites the whole import file every 5
   minutes, which wakes the monitor again. (Si, Ef, Al)
5. `src/WSGM/Core/Library/ShortcutRoute.cs:133-146, 231-236` stamps `ManagedId` on every route. The
   `Runs` recursion with `ManagedId = ""` is a compatibility path, and `RouteOf` always returns the
   first route. Make "managed" a single route kind with the launch plan in the record. (Al)
6. `ImportPlan.cs:642-653`: the new Conflict pass loops over titles × shortcuts × routes and calls
   `TryCompose` inside the innermost loop. Decide once whether StartDirectory is part of identity.
   (Ef, Al)
7. `ImportPlan.cs:456`: `ContentIdentity` handles ROMs only. Each single ROM or manual entry is a
   whole source (`GameLibraryConfig.cs:89-92`, `RomLibrarySource.cs:407`) with its own Steam
   collection (`CollectionGroup`, `GameLibraryService.cs:2335`), although the docs say ROMs group by
   system. (Al)
8. Each managed title is first saved as an AppId-0 record and confirmed on the next scan
   (`GameLibraryService.cs:1639-1663` and `:2084-2096`). That duplicates `ConfirmedUtc` and causes
   2N durable writes plus one save per confirmed record. (Si, Ef, Al)
9. The managed record is built four different ways (`ShortcutFolderSource.cs:194-208`,
   `RomLibrarySource.cs:424-432`, `LibrarySource.cs:109-117`), and `PrepareManagedContent`
   (`GameLibraryService.Roms.cs:14-45`) mutates it in place. The scan
   (`GameLibraryService.cs:1676-1712`) then patches Location, volume and Program after the fact.
   (Ru, Al)
10. `Sources()` (`GameLibraryService.cs:1522-1540`) backfills `VolumeId`, which is a migration, and
    reads the store on every call. It runs under `_gate` and partly on the UI thread, and the
    `ShortcutFolderSource` constructor calls `ResolvePath` (`ShortcutFolderSource.cs:92`). (Si, Ef,
    Al)
11. Discriminators are untyped strings. (Al, Si)
    - Source kind is parsed from the id prefix (`GameLibraryService.cs:2876`), and both UIs branch
      on it (`GameLibraryView.cs:126`, `library-import.ts:1419,1428,1443`).
    - SourceKind, LaunchKind and ProfileId are free strings compared across generic code.
    - `GameLibraryService.cs:1688` compares a route id with LaunchKind.
12. `RomLibrarySource` (`RomLibrarySource.cs:165, 243-301`) applies `ImportChoice`s and resolves
    emulators and previews during discovery. That is a layering inversion. (Al)
13. Per-emulator knowledge is scattered. (Ru, Si, Al)
    - There are `switch`es on emulator id in six places: `EmulatorPrerequisites.Configure`/
      `Describe`, `EmulatorStorage.MissingPrerequisites` (`EmulatorModels.cs:434-479`),
      `ManagedShortcutMonitor.InstallationPaths`, `EmulatorValidation.Probe`,
      `EmulatorManager.RegisterExternalAsync`/`NativeData`.
    - The `"retroarch"` literal appears about 25 times in 10 files, C# and TS.
    - `emulators.json` isn't really data-driven: `DataMode` is a 1:1 alias of Id,
      `RequiresVisualCpp` is ignored in favour of `DefinitionId is not "retroarch"`, and
      `WorkingDirectory` and `UpdateBehavior` each allow a single value.
    - Pick one: a static C# table like `RomProfiles.All`, or a catalogue that really carries the
      per-emulator data.
14. External installs strip `-datapath`, `-u` and `-c` by string match
    (`EmulatorManager.cs:461-479`). The per-id flags in `EmulatorValidation` re-encode the
    `{data}`/`{config}` tokens. Split the definition into launch and data arguments. (Si, Al)
15. The generic release providers hide per-emulator hacks. (Si, Al, CR)
    - `{compiler}` hard-coded to msvc/clang, Eden-only host scraping, and prerelease decided by
      channel name (`EmulatorReleases.cs:202-243`).
    - Buildbot is RetroArch-only, and `EmulatorPackages.cs:213` hard-codes the base URL instead of
      `source.BaseUrl`.
    - The GitHub, Gitea, Tag and 5-name checksum-discovery paths are unused, as is the Buildbot
      `"x86"` branch.
16. Hardening layers that conflict with the no-security-theater decision of 2026-10-03. (Si, Al)
    - Receipt path validation (`EmulatorModels.cs:326-348`) and journal path validation.
    - `AssertNoReparse` and the reparse scan in `DeleteOwned` (`EmulatorPackages.cs:134-168`), and
      the archive link, encryption and attribute checks.
    - The PE machine check (`EmulatorManager.cs:862-897`).
    - A manual HTTPS redirect loop with a host allowlist (`EmulatorReleases.cs:19-79`).
    - A cross-process store mutex, `RequireStopped` and an admission mutex, all for the same
      purpose.
    - `SafeFiles` link refusal, the HTTPS re-check of notes URLs, and the EA DTD and size limits.
17. Arbitrary caps, against the no-arbitrary-limits rule. (Si, Al)
    - Catalogue: 128 definitions, 32 launch arguments, 8 validation arguments, a 63-character id.
    - Packages: 2048 cores, 100,000 entries, 8 GiB expanded, 2 GiB downloaded.
    - Downloads: 8 redirects, 10 pages, 8 MiB metadata.
    - Validation: 20 s and 64 KiB of output.
    - Files: 64 MiB for the emulator store and the artwork cache.
    - `GameLibraryRules.cs:54`: extensions under 20 characters are silently dropped.
    - UI: 32/256/512 argument limits, core page size 48, `MaximumWatchRoots` 8.
18. The activation journal, `previous-config.bin` and the `activation.json` refusal
    (`EmulatorManager.cs:368-424, 777-838`, `EmulatorModels.cs:385-389`) exist only to roll back a
    crash mid-activation. Deleting unreferenced version folders at startup and re-running the
    idempotent RetroArch configuration would do. (Si)
19. The emulator manager is built into the Game Library. (Al, Ef)
    - It adds 14 methods to `IGameLibraryBackend` (`GameLibraryState.cs:230-298`), puts the full
      snapshot in `GameLibraryState`, and runs as a mode of the import page
      (`library-import.ts:1122`) and a second `GameLibraryView` (`OverlayWindow.axaml:359`).
    - It is created inside `TryStart("game library")`.
    - Every status tick republishes the whole library state, all entries and all cores, to Steam.
20. Policy is duplicated between C# and TS: availability labels and the unavailable set
    (`GameLibraryRows.cs:35-51`, `library-import.ts:43-45`, matched by enum-name strings),
    compatible installations and cores (`GameLibraryView.Roms.cs:229-246`,
    `library-import.ts:504-513`), and the "retroarch OR Systems.Contains" rule in five copies
    (`EmulatorModels.cs:373`, `EmulatorManager.cs:181`, `GameLibraryService.Roms.cs:264`,
    `GameLibraryView.Roms.cs:231`, `library-import.ts:506`). The host should publish them, as it
    already does for `ActionLabel` and `LaunchLabel`. (Ru, Si, Al)
21. Artwork search leaks Screenscraper specifics into generic code. (Al, Si)
    - `FindMatchAsync` gains Screenscraper-only parameters and a `provider is ScreenscraperProvider`
      check (`ArtworkProviders.cs:335`).
    - The provider is chosen by `SourceKind == "rom"` (`GameLibraryService.cs:1945-1949`), and
      `PauseReason` uses `OfType<ScreenscraperProvider>`.
    - `ArtworkDownload` (`ArtworkDownload.cs:41-53`) gains a `responseObserved` callback so one
      provider can throw from inside it.
    - The old string overloads are reached only through a default interface method and the test
      fake.
22. Screenscraper quotas are mirrored locally beside the existing `ArtworkRequestGate`
    (`ArtworkProviderImplementations.cs:174-338, 601-696`). (Si, Al, Ef)
    - Daily and KO counters, a per-minute queue, and a Romance Standard Time rollover.
    - `_maximumThreads` is only checked for being 0 or less.
    - The 430/431 message arms (`:556-565`) are now unreachable.
    - `ArtworkQuotaException`'s `ProviderId`, `StatusCode` and `DailyExhausted` are never read, and
      it needs an extra `catch { throw; }`.
23. The persistent artwork cache (`GameLibraryArtwork.cs:215-259, 333-348, 693-735`) is a second
    cache with its own 30-day TTL, 5-second write throttle, semaphore and 64 MiB cap, and it
    re-spells `Title.Matches`. (Si, Al)
24. Managed availability is folded into the card-library badge publication. (Si, Al, CR)
    - A static `RecheckRequested` event (`LibraryBadges.cs:48-61`) links the toolkit backend to the
      monitor.
    - "switch OR shortcuts exist" special cases appear twice (`SteamUiSessionHost.cs:1008,1528`),
      and `Libraries = []` is stripped when the switch is off.
    - `SteamLibraryBadgeSurface.RecheckAsync` is a default interface method with one implementer.
    - The `HomeCarousel.cs:40-44` managed filter is a no-op, because shortcut ids never appear in
      Steam library folders.
25. `library-badge.ts` `refreshMounted` (`:126-130, 370`) re-runs the full fiber-tree walk (`adopt`,
    up to 60,000 nodes) on every publication and rerenders every tile. (Ef, Al, Ru, Si)
    - It also changes `adoptMountedType` (`gate-helpers.ts:730`) for all callers.
    - The `observers` set plus `useReducer` (`:476-485`) re-implements `createLocalStore`.
    - The `publishedState` dedupe (`:123, 367-368`) is redundant with the host's.
    - A second `reading.shortcuts` lookup sits next to `libraryForOverview` (`:260`).
26. The title rename flow carries a lot of state. (Si, Ru, Al)
    - `TitleRenamePending`, `SteamNameAtScan`, the `steamNames` map and a stale-name refusal
      (`GameLibraryService.cs:2036-2058`).
    - `SteamShortcutWriter.RenameAsync` (`SteamShortcutWriter.cs:85-107`) takes its delegate per
      call, threaded through a new `GameLibraryService` constructor parameter
      (`ShellSession.cs:970`), instead of in the writer's constructor beside add, setLaunch and
      remove.
    - The toolkit adds a readback poll (section 1, item 31).
27. Overlay. (Al, Si, CR)
    - `OverlayNavigation.cs:315-318`: Back special-cases the emulator page and skips FocusRail,
      against the Overlay AGENTS.md rule.
    - The emulator tile's visibility is set imperatively (`OverlayWindow.Sources.cs:166`), plus a
      `HasService` flag (`GameLibraryView.cs:32`). A ViewModel `ShowEmulators` flag would match the
      other tools.
    - `GameLibraryView.Tools.cs:172-173`: the card Tag encodes availability and thumb to force a
      remount. Copying Opacity (and IsEnabled) in `ServiceSubView.Reconcile` is the real fix.
28. `eng/build-steam-assets.mjs:56-71`: a second Prettier pass, with the `spawnSync` block
    copy-pasted, patches output that isn't idempotent. Fix the source construct, or exclude the
    generated asset from Prettier and let the byte drift check be the gate. At minimum, use one
    `format(input)` helper. (all five reviews)
29. Toolkit. (Si, Al, CR)
    - Whole-file 4-to-2-space reformatting of `gate-helpers.ts`, `ui-kit.ts` and `library-badge.ts`
      (about 1,000 to 1,900 lines) breaks the toolkit's `.editorconfig` (`indent_size = 4`) and its
      AGENTS.md ("Avoid unrelated formatting"). The real changes underneath are the moved
      `fibers.push`, the optional `onBack` on `renderSteamUiLevel`, and `actionsAlwaysVisible`.
    - `SteamExtensionsTabSurface.ActionsAlwaysVisible` is a contract flag for one row.
    - A generic modal sheet and list editor (`library-import.ts:186-190, 529-551`) live in WSGM's TS
      instead of `ui-kit.ts`.
30. `ExistingShortcut.StartDirectory`/`Name` (`ImportPlan.cs:254-256`) are nullable only for tests,
    which forces a three-way check in three comparisons. (Si)
31. The `--managed` command is composed in `CommandShortcut.TryCompose` (`ShortcutRoute.cs:133-146`)
    and parsed by hand in `src/WSGM.PackagedLaunch/Program.cs:46`, bypassing the shared
    `PackagedLaunchCommand` composer, parser and `CommandLineRefusal`
    (`src/Shared/Launch/PackagedLaunchCommand.cs:116`). It hard-codes 32767
    (`ManagedContentLaunch.cs:221`) instead of `WindowsCommandLineLimit`, and the 32-hex id format
    is validated in two places. (Ru)

## 3. Reuse

1. `ManagedDirectProcess` and `ManagedDirectNative` (`ManagedContentLaunch.cs:196-263`,
   `src/WSGM.PackagedLaunch/Interop/ManagedDirectNative.cs`) copy
   `WSGM.Launch.SuspendedProcess.Start`/`BuildEnvironment` (`src/WSGM.Launch/SuspendedProcess.cs`).
   (Ru, Al)
   - `CloseHandle` duplicates `Win32Common.CloseHandle` (`src/Shared/Process/Win32Common.cs:12`).
   - The process structs duplicate `ParentProcessStart`
     (`src/Shared/Process/ParentProcessStart.cs`).
   - They use old-style `DllImport` where the project uses `LibraryImport`.
   - Move `SuspendedProcess` into `src/Shared/Process` and link it.
2. `ManagedContentLaunch.cs:21` hard-codes `%LOCALAPPDATA%\WSGM` instead of using `UserDataContext`
   (`src/WSGM/Core/UserDataContext.cs:13`), and `ImportStateStore.UserRoot` infers it from a file
   path. (Ru, Al)
3. `ImportStateStore.UpdateAvailability` (`ImportStateStore.cs:111-152`) re-implements the private
   `Mutate` (`:349`). (Ru)
4. `ScreenscraperProvider.SearchRomAsync` (`ArtworkProviderImplementations.cs:269-302`) re-parses
   `jeuRecherche` like `SearchGamesAsync` (`:226-267`) but drops the trim, the empty-id check and
   the string-id handling. `SearchGamesAsync` could take an optional system id. (Ru)
5. `EmulatorReleases.Text`/`Flag`/`ParseChecksum`/`Assets` (`EmulatorReleases.cs:359-382`) and
   `EmulatorPackages.DownloadAsync` (`EmulatorPackages.cs:19-60`) duplicate `UpdateChecker`
   (`src/WSGM/Core/UpdateChecker.cs:165-325`) and `JsonRead.OptionalString`
   (`src/WSGM/Core/JsonRead.cs:115`). (Ru)
6. `EmulatorManager.PeArchitecture` (`EmulatorManager.cs:870-897`) hand-parses PE headers; the
   codebase already uses `PEReader` (`src/WSGM/Core/PluginPackageFile.cs:275`). (Ru)
7. `EmulatorValidation.Probe` and `ReadBoundedAsync` re-implement `ConsoleTool.RunAsync`
   (`src/WSGM/Core/ConsoleTool.cs:52-104`), which is documented as the one home for that.
   `ConsoleTool` needs working-directory and environment parameters. (Ru)
8. `EmulatorPackages.Crc32`/`BuildCrcTable` (`EmulatorPackages.cs:349-381`) hand-roll CRC32, the
   third copy in the repo (also `src/WSGM.Device.Sdk/Glyphs/GlyphAssetValidation.cs:525`);
   `System.IO.Hashing.Crc32` would replace both. (Ru)
9. `ManagedContentStorage.NativeStorage` (`ManagedContentModels.cs:371-385`) re-declares
   `GetVolumeNameForVolumeMountPointW` (already in `src/WSGM/Interop/NativeStorage.cs:811`), and the
   nested class name shadows `WSGM.Interop.NativeStorage`. Shared declarations belong in
   `src/Shared/Interop/Kernel32.cs`. (Ru)
10. Command-line quoting is inconsistent: `RomLibrarySource.cs:276-277` uses
    `WindowsCommandLine.Quote` where sources are documented to use `LaunchArguments.Join`/`Quote`
    (`LibraryFiles.cs:350,373`), and `ShortcutRoute.cs:141-143` mixes two quoters in one command.
    (Ru)
11. INI reading and editing is hand-written in five places (`EmulatorPrerequisites.SetIni`,
    `EmulatorStorage.IniPath`, `EmulatorPackages.Info`, `EmulatorManager.ConfigureRetroArch`) while
    `LibraryFiles.IniValue` (`LibraryFiles.cs:223`) exists; it would need to move to `src/Shared`.
    (Ru)
12. The mutex-acquire pattern (wait, treat `AbandonedMutexException` as owned, release) is written
    three times (`EmulatorManager.cs:589,656`, `ManagedContentLaunch.cs:39-55`) while
    `ConfigStore.Acquire` and `WindowsPolicyOperation` exist, and `Local\WSGM.EmulatorStore` is not
    derived from `UserDataContext`. (Ru)
13. `RomLibrarySource.ContentId` (`RomLibrarySource.cs:192`) is the same algorithm as
    `GameLibraryService.EntryId` (`GameLibraryService.cs:2559`) with 16 bytes instead of 12, and it
    lives in `RomLibrarySource` although folder, manual and launcher sources all use it. Move it to
    `ManagedContentStorage`. (Ru, Si, Al)
14. HTTPS checks exist in four copies: `GameLibraryService.Roms.cs:597-598`,
    `GameLibraryArtwork.IsHttps` (`:688`), `JsonRead.HttpsUrl` (`:71`) and `EmulatorNetwork.Https`
    (`EmulatorReleases.cs:33`). (Ru)
15. The path containment check `StartsWith(root + separator)` is hand-written about 12 times
    (`EmulatorPackages.cs:88,150,307`, `EmulatorManager.cs:298,638,796`,
    `ManagedShortcutMonitor.cs:735,898,949` and others) while `PluginPackageManager.IsInside`
    (`:285`) and `RtssDiscovery.IsUnder` (`:286`) exist. `AssertNoReparse` duplicates
    `SoundPackLibrary.CheckPath` (`src/WSGM/Core/Sounds/SoundPackLibrary.cs:450-473`). (Ru)
16. Overlay duplication. (Ru)
    - The save methods (`GameLibraryView.Arguments.cs:64`, `GameLibraryView.Manual.cs:84`,
      `GameLibraryView.Roms.cs:153`) each hand-roll "run in background, check generation, toast,
      navigate" instead of `ServiceSubView.RunCommandAsync` (`ServiceSubView.cs:244`).
    - `SplitRomValues` (`GameLibraryView.Roms.cs:223`) duplicates the extension parsing in
      `GameLibraryView.Tools.cs:81`.
    - The Release notes row and offer lookup are copied between `GameLibraryView.Emulators.cs:74-83`
      and `:149-163`.
17. `SteamLibraryImportSurface.Managed.cs`: `TryReadRomSource`/`TryReadManualSource` (`:76-86`) are
    no-op wrappers, there are nine hand-written readers plus nine single-use record structs
    (`:138-277`), and the generic reader uses reflection-based options instead of
    `GameLibraryJsonContext`. (Ru, Si)
18. TS (`library-import.ts`): these pieces should use existing helpers or one shared helper. (Ru,
    Si)
    - `ImportRomRenameBody` (`:515`) duplicates `showSteamUiPrompt` (`ui-kit.ts:722`), and the core
      "Load more" button (`:797`) duplicates `renderSteamUiMore` (`ui-kit.ts:288`).
    - Yes/No dropdowns (`:636, 638, 687`) are used where `importUi.toggleField` fits, as the file
      already does at `:239`.
    - `fact` (`:743`) duplicates the `wsgm-import-fact` row markup (`:411-418`).
    - The state subscription is copied three times (`:287-289, 599-602, 701-704`) and should be one
      `useImportState()` hook.
    - `String(failure?.message ?? failure)` is repeated about nine times, and the extension
      splitting `/[\s,;]+/` twice (`:444, 641`).
19. `SteamLibraryBadgeSurface.TryReadRecheck` (`Surfaces/SteamLibraryBadgeSurface.cs:208`) reads a
    `long` where every other reader uses `TryGetUInt32`, and `SteamShortcutAvailability.AppId` is
    `long` while every other shortcut id is `uint`. (Ru)
20. `RomSourceConfig` (`RomLibrarySource.cs:22-26`) and `ShortcutFolderConfig`
    (`GameLibraryConfig.cs:34-36`) re-spell `ManagedContentPath` as
    `Path`/`VolumeId`/`RelativeRoot`, and `RomSourceConfig.ResolveRoot` duplicates
    `ShortcutFolderSource.cs:92-96`. (Ru, Si)
21. Emulator preference resolution is re-coded three times in the monitor
    (`ManagedShortcutMonitor.cs:496,581,976`) with a different case comparison and fallback than
    `ManagedContentStorage.ResolveEmulatorPreference` (`ManagedContentModels.cs:246`). (Ru)
22. The monitor's `InstallationPaths` (`ManagedShortcutMonitor.cs:596-602`) is a third copy of each
    emulator's folder layout. (Si)

## 4. Simplification and dead code

- **Dead code:**
  - `GameLibraryService.FolderId` (`:2551`), `EmulatorManager.SetPreferredEmulatorAsync`
    (`:200-202`), and `UpdateDefinitionsAsync` (`:224-246`) with the `emulator-definitions.json`
    override it feeds (`EmulatorCatalog.cs:62-67`).
  - `RomSystemProfile.DefaultEmulatorId` (filled 19 times, never read),
    `ShortcutFolderConfig.Name`/`LibraryBacked` (no writer), the single-entry `UpdateAvailability`
    overload (`ImportStateStore.cs:107-108`), and the monitor's RomSources fallback
    (`ManagedShortcutMonitor.cs:657-669`) along with its `_settings` dependency.
  - `RequiresVisualCpp` and about 15 write-only fields: `PreviousExecutablePath`, `SourceUrl`,
    `Sha256`, `ExpectedSha256`, `UpstreamCrc32`, `InstalledUtc`, `EmulatorOffer.Provider`,
    `EmulatorDefinition.Provider`/`DataMode`/`ExecutableNames`/`LaunchArguments`/`Architectures`,
    `EmulatorResolvedLaunch.RequiredFiles`/`GateName`.
  - `EmulatorInstallation.RequiredFiles` is never assigned but is copied five times
    (`EmulatorModels.cs:401`, `EmulatorManager.cs:902`, `ManagedShortcutMonitor.cs:467,608,1002`).
  - The Buildbot x86 branch and the DuckStation `repository`/`tag` keys in `emulators.json`.
- **Constant parameters:**
  - `RemoveAsync(preserveData)` (`EmulatorManager.cs:131-159`) is always true and runs through seven
    layers.
  - The `emulatorRoute` delegate (`SteamExtensionsTabBackend.cs:43`) always returns
    `SteamLibraryImportSurface.EmulatorRoute`.
  - `Placeholder(..., record = null)` (`GameLibraryService.cs:2917`) has one caller, which always
    passes it.
- **Derivable state:**
  - `_busy` (`EmulatorManager.cs:30`) is always set with `_active`.
  - The `Entry` fields `TitleName`, `EmulatorOverride`, `CoreOverride`, `Cleanup` and
    `ArgumentsOverride` (`GameLibraryService.cs:2990-2999`) mirror `ImportChoice`.
  - `UsesRawArguments` (`ManagedContentModels.cs:107-111`) is `SourceKind != "rom"`.
  - `PreviewCommand`/`PreviewWorkingDirectory` are persisted only for notes.
  - `RomSourceConfig.ProfileId` is derivable from `SystemId`, and its two dropdowns
    (`library-import.ts:631`, `GameLibraryView.Roms.cs:75-81`) only ever offer one option.
  - `EmulatorSnapshot` and `EmulatorStore` overlap (`EmulatorModels.cs:228-263`).
  - `QuotaPaused` duplicates "Failed with Detail == reason".
- **Redundant work:**
  - `RequireStopped` runs before `Admission` and again inside it
    (`EmulatorManager.cs:137-144, 208-212`), and the same `Mutate` appears in both branches of
    `RemoveAsync` (`:146, 155`).
  - The helper's `finalCheck` (`ManagedContentLaunch.cs:117-128`) and record re-read.
  - `Validate` resolves `BackingPath` twice (`ManagedContentModels.cs:329-330`).
  - Null guards on JSON the app wrote itself (`ManagedContentModels.cs:214-220`), a dead
    `!OperatingSystem.IsWindows()` branch (`:267`), the `RepairAsync` alias
    (`EmulatorManager.cs:124`) with its own backend method, command, fake and row, and the deep
    `Copy()` (`:899-909`) on every `GetSnapshot`.
- **Needless wrappers:**
  - `SetCleanupAsync` → `CleanupEntry` (`GameLibraryService.Roms.cs:354-402`).
  - The fake-async `ApplyAvailabilityAsync` (`:445`).
  - `SteamShortcutWriter.RenameAsync`, which also ignores the cancellation token after its first
    check.
  - `ImportArgumentsBody` (`library-import.ts:529`) running in two modes.
  - The save-sources tail (`TryUpdateSettings` → lock → `ScanOrQueue` → `Notify` → `Applied`)
    repeated four times (`GameLibraryService.Roms.cs:156-171, 182-193, 219-234, 318-330`), and the
    `{rom}` check twice (`:97-101, 372-376`) with different messages.
- **Repeated native calls:** `AddFolder` calls `CapturePath(full, true)` twice
  (`GameLibraryService.cs:778-779`).

## 5. Efficiency

1. `ManagedContentStorage.ResolvePath` (`ManagedContentModels.cs:292-298`) allocates a 32K-character
   buffer plus a full string on each call, about 256 KB per validated title once `Validate` resolves
   twice. A 3,000-ROM scan churns roughly 750 MB.
2. `RomLibrarySource` (`:260-274`) repeats per ROM work that is the same for the whole source:
   - `CapturePath(parent)`.
   - `Validate`, right after the walk enumerated the file.
   - `EmulatorStorage.Resolve`: INI read, BIOS folder listing, vcruntime checks and a SHA-256 for
     `GateName`. The scan merge (`GameLibraryService.cs:1696-1704`) does it all again, so a
     5,000-ROM PS2 library means about 10,000 PCSX2.ini reads and BIOS listings per scan.
3. `RomLibrarySource.Walk` calls `File.GetAttributes` on every entry through nested iterators;
   `EnumerationOptions` with `AttributesToSkip` would give the attributes with the listing.
4. The monitor:
   - wakes on every `library.Changed` (`ManagedShortcutMonitor.cs:69, 185-188, 303-313`), which
     fires per artwork title, and rebuilds snapshots, signatures and the published list each time;
   - stats each installation's executable, core and required files again for every entry
     (`:957-1003`);
   - calls `new DriveInfo(root).DriveType` for every path (`:624-641, 707`).
5. Every emulator operation, including "ignore version" and "set preferred", triggers a full
   `RecheckAvailabilityAsync("")` (`GameLibraryService.Roms.cs:520`), and the monitor runs another
   full pass on the same change.
6. Every emulator status tick republishes the full library (`GameLibraryService.Roms.cs:485-498`),
   about 250 times per RetroArch install. `BuildState` (`GameLibraryService.cs:2912-2913`) takes the
   snapshot twice, rebuilds `RomProfiles.ForInstallations`, and serializes every core on each
   revision.
7. Artwork cache:
   - It is read synchronously on the UI thread at session start (`ShellSession.cs:926`,
     `GameLibraryArtwork.cs:237-260`), up to 64 MB.
   - It is rewritten in full every 5 seconds during a run, then after every title once the queue
     empties, with a new `JsonSerializerOptions` on each save, blocking progress on file I/O.
   - It is never pruned.
8. The `EmulatorManager` constructor (`:41-44`) parses the catalogue and reads the store
   synchronously on the UI thread (`ShellSession.cs:924`).
9. The release refresh (`EmulatorManager.cs:79-99`) runs about 15 to 25 HTTPS round trips one after
   another.
10. The libretro `.index` file is fetched up to three times (`EmulatorReleases.cs:327`,
    `EmulatorPackages.cs:189, 277-281`).
11. Per-core work during a RetroArch install:
    - a recursive search of `info/` (`EmulatorPackages.cs:249-250`);
    - the alias dictionary rebuilt (`:319-339`);
    - the DLL read twice (`:239, 247, 252-261`), with a byte-at-a-time CRC loop.
12. Launch work:
    - Each launch parses the whole import file twice (`ManagedContentModels.cs:230-235`) and
      `emulators.json` up to three times, and runs `Validate` twice.
    - Waits are polled: the job every 200 ms (`ManagedContentLaunch.cs:154-157`), the admission
      mutex in 250 ms steps (`EmulatorManager.cs:589-606`) and `WaitForExit` in 100 ms steps
      (`EmulatorValidation.cs:66`).
13. `RomSize` (`GameLibraryService.Roms.cs:47-62`) does volume resolution and file I/O under `_gate`
    for every ROM on every `ResetArtwork` (`GameLibraryService.cs:1948`), which has five call sites.
14. `ShortcutFolderSource` (`:200-206`) makes four `CapturePath` calls per file, and launcher
    sources (`LibrarySource.cs:100-108`) make about three kernel calls per game.
15. `FrenchDate` (`ArtworkProviderImplementations.cs:336-337`) calls
    `TimeZoneInfo.FindSystemTimeZoneById` on every request reservation, quota read and pause check.
16. TS:
    - `ImportRomSourceBody` and `ImportEmulatorSetupBody` (`library-import.ts:600, 702`) re-render
      on every publication, and `ImportRomLaunchControls` re-filters and re-sorts the cores each
      time (`:507, 559`).
    - The core search re-lowercases the search text inside the filter callback (`:784`).
    - `importUnavailable`/`importAvailability` (`:43-44`) allocate a literal on every call, three
      times per card.
17. The hidden emulator `GameLibraryView` subscribes to `service.Changed` immediately
    (`OverlayWindow.Sources.cs:165`) and posts a dispatcher callback for every change burst.
18. Emulator discovery and the running-process check repeat their work.
    - `DiscoverExternalAsync` (`EmulatorManager.cs:508`) re-reads and validates `emulators.json`
      once per definition.
    - `RequireStopped` (`:620-626`) enumerates every `*.exe` under the install root, retained
      versions included, twice per operation.
19. Scan bookkeeping: each unconfirmed record does a linear shortcut search and its own durable
    `_store.Save` (`GameLibraryService.cs:1640-1652`), and each added title gets an extra pre-record
    save (`:2086`), so importing N titles writes the growing file 2N times.
20. The Conflict pass in `ImportPlan.cs:642-652` composes routes inside the shortcut loop, about
    600,000 compositions for 2,000 ROMs against 300 shortcuts.
21. `build-steam-assets.mjs` spawns a second full Prettier process on every asset build and check.

## 6. Repository rules and style

- **Em dashes in user-facing text:** `ManagedContentLaunch.cs:190` (the "WSGM — Content unavailable"
  caption) and `library-badge.ts:518` (`${name} — Unavailable`).
- **Readback gating:** section 1, item 31.
- **Toolkit formatting churn:** section 2, item 29.
- **Compatibility code in greenfield 2.0:** the store version bump and its `number > 2` check, the
  `ShortcutRoute.Runs` recursion, and the `Sources()` `VolumeId` backfill.

## Decisions needed before fixing

- Is managed content for launcher titles (section 1, item 4) intended?
- Should `ManagedShortcutMonitor` be reduced to plain rechecks on volume, resume, library change and
  explicit request?
- Should the emulator hardening layers and the arbitrary caps be removed?
- Should the emulator manager be split out of the Game Library backend, state, page and view?

Recommended order: apply sections 1 (excluding item 4), 3, 4 and 6 directly, and settle section 2
and the efficiency items that depend on it first.
