# Open issues, 2026-10-06

This tracks the 80 findings identified after the previous fix pass. Completion notes below record
the applied corrections; validation and remaining manual acceptance are recorded at the end. The
original evidence behind each item is in
[Review fix verification 2026-10-06](Review%20fix%20verification%202026-10-06.md), and the original
findings are in [the combined review](Uncommitted%20changes%20review%202026-10-06.md).

Origin tags: **new** means the fix pass introduced it. **R1.x** to **R6** point to sections 1 to 6
of the combined review.

Checkmarks record applied source corrections. Each completed item includes its change; combined
build, deployment and manual acceptance are tracked separately below.

## Bugs

1. [x] **Apply records every Add before any Steam write.** (new)
   - **Where:** `GameLibraryService.cs:2084-2104`, `ImportStateStore.cs:222-245` (`SaveMany`
     replaces existing records).
   - **Effect:** a failed or cancelled run, or an entry that fails `Revalidate`, leaves the titles
     it never reached at AppId 0 with no `ConfirmedUtc`. The next scan shows them unticked and
     Unconfirmed. A "deleted from Steam" Add loses its old AppId, OwnsProfile and ArtworkApplied, so
     its controller override can't be released.
   - **Fix:** save the provisional record only immediately before that entry's `AddAsync`, or drop
     it (item 37).
   - **Completed:** Removed upfront provisional Add records; failed, cancelled and unreached entries
     retain their existing ownership metadata.
2. [x] **ROM titles can be marked Unavailable at startup, and it sticks.** (new)
   - **Where:** `EmulatorManager.cs:34,44,63,105`, `EmulatorService.cs:186-194`,
     `ManagedShortcutMonitor.cs:46`, `ShellSession.cs:395,991`.
   - **Effect:** the monitor's first recheck can run against the still-empty emulator snapshot and
     record EmulatorUnavailable for every ROM. Finishing initialization raises only `Changed`, so
     nothing rechecks. If the first store read fails, the store stays empty for the session.
   - **Fix:** raise `InstallationsChanged` when initialization completes, and don't recheck before
     it. Retry the load on the next refresh.
   - **Completed:** Initialization publishes semantic installation changes and exposes Initialized;
     the monitor waits for readiness, and Refresh retries a failed store load.
3. [x] **One unsupported external emulator breaks every Refresh.** (new)
   - **Where:** `EmulatorManager.cs:622-623`; the exceptions come from `:541-545,954`.
   - **Effect:** `InvalidDataException` and `BadImageFormatException` from an ARM64 or x86
     `retroarch.exe` aren't caught. Every Refresh fails, and discovery stops early.
   - **Fix:** catch them per candidate and skip that candidate.
   - **Completed:** External discovery catches unsupported architecture/image data per candidate and
     continues scanning.
4. [x] **The "Launch command" note prints `System.String[]`.** (new)
   - **Where:** `GameLibraryService.cs:1783-1788`.
   - **Fix:** use `LaunchArguments.Join(launch.Arguments)`.
   - **Completed:** Launch previews join typed arguments with shared LaunchArguments.Join.
5. [x] **WSGM.Launch no longer searches PATH.** (new)
   - **Where:** `ContainedProcessStart.cs:60` passes `FileName` as `lpApplicationName`, where the
     old `SuspendedProcess` passed null.
   - **Effect:** a target that isn't fully qualified loses the PATH search and the implied `.exe`,
     and resolves against WSGM.Launch's current folder instead.
   - **Fix:** pass null as before, or only pass it when the path is fully qualified.
   - **Completed:** Shared CreateProcess passes a null application name, restoring Windows
     executable search and implied .exe. Added an unqualified-target regression; combined validation
     pending.
6. [x] **Emulator manager state is written outside its lock.** (new)
   - **Where:** `EmulatorManager.cs:62-74,117-118`.
   - **Effect:** `_state`, `_status`, `_catalog` and `_definitionViews` are written outside
     `_stateLock` but read under it in `GetSnapshot`. `_initialized` is not volatile.
   - **Fix:** assign under `_stateLock`.
   - **Completed:** Initialization, catalogue, status and receipt snapshot assignments now use the
     state lock.
7. [x] **Collection sync ignores unticked ROM systems.** (new)
   - **Where:** `GameLibraryService.cs:2405`.
   - **Effect:** `unticked.Contains(group)` never matches a `rom-system:` group, so disabling a ROM
     source no longer leaves its collection alone.
   - **Fix:** map the source to its system groups before the check.
   - **Completed:** Disabled sources map to their ROM system collection groups, including authored
     single-ROM entries and retained imported records. Added collection regression.
8. [x] **Nightly RetroArch repair misses its retained archive.** (new)
   - **Where:** `EmulatorManager.cs:342`; the archive is saved under `Asset.Name` in
     `EmulatorPackages.cs:58-59,78-93`.
   - **Effect:** repair looks the archive up by the URL's file name, so it downloads again and fails
     once Buildbot prunes that nightly.
   - **Fix:** look it up by the stored asset name.
   - **Completed:** Installation receipts retain PackageName; exact-version repair locates the
     retained archive by that name.
9. [x] **A blank argument row is refused.** (new)
   - **Where:** the list editor adds a blank row (`ui-kit.ts:339`), and
     `SteamLibraryImportSurface.Managed.cs:110-122` refuses blank strings.
   - **Effect:** saving with an unfilled row gives "The launch arguments are invalid."
   - **Fix:** drop blank rows before sending, or accept and ignore them on the host.
   - **Completed:** Steam and overlay argument editors drop blank rows; the host accepts and ignores
     unfilled rows.
10. [x] **The artwork cache save can stop for good.** (new)
    - **Where:** `GameLibraryArtwork.cs:887`.
    - **Effect:** only IO and access errors are caught, so any other exception leaves `_cacheSaving`
      true and saving never runs again.
    - **Fix:** reset it in a `finally`.
    - **Completed:** Removed the secondary disk candidate cache and its saving flag, eliminating the
      stuck-save path.
11. [x] **A paused artwork provider blocks fallback.** (new)
    - **Where:** `ArtworkProviders.cs:328-331`.
    - **Effect:** while Screenscraper is paused, ROM titles never fall back to SteamGridDB.
    - **Fix:** skip the paused provider and continue. Report the pause only when no provider
      answered.
    - **Completed:** Queries and asset gathering continue after paused providers. A pause is
      reported only when no provider answers; a successful empty answer counts.
12. [x] **Emulator preferences compare system ids inconsistently.** (R3.21)
    - **Where:** `GameLibraryService.Roms.cs:535-538`, `EmulatorManager.cs:260,283`.
    - **Effect:** the preference lookup's `ToDictionary` throws on duplicate ids and doesn't use
      `NormalizeSystemId`. `SetPreferredAsync` compares ids exactly, while clearing normalizes them.
    - **Fix:** use `ManagedContentStorage.ResolveEmulatorPreference` and normalize everywhere.
    - **Completed:** Preference selection reuses canonical PreferredSystem/ResolveEmulatorPreference
      and normalized system identities. Duplicate alias preferences no longer throw.
13. [x] **Re-enabling a source re-fetches its artwork.** (new)
    - **Where:** `GameLibraryArtwork.cs:428-429`.
    - **Effect:** `PruneCache` drops the entries of titles that aren't in the current request, so a
      disabled source's artwork is fetched again on re-enable, against the Screenscraper quota.
    - **Fix:** prune by the configured sources, or by age only.
    - **Completed:** The in-memory artwork stage retains known images for disabled sources and
      retries only active titles after settings changes.
14. [x] **Emulator installs fail on redirected profile folders.** (R2.16)
    - **Where:** `AssertNoReparse` walks every parent folder (`EmulatorPackages.cs:209-219`).
    - **Effect:** every install or BIOS setup is refused when `%LOCALAPPDATA%` or Documents sits
      behind a junction or a OneDrive placeholder.
    - **Fix:** remove it (see item 33).
    - **Completed:** Removed parent reparse refusal from installation and prerequisite paths,
      allowing redirected profile folders.
15. [x] **Crash leftovers are never cleaned.** (new)
    - **Where:** `PruneVersions` in `EmulatorManager.cs:885-904`.
    - **Effect:** `staging-*` folders left by a crash are never swept.
    - **Fix:** sweep them on startup.
    - **Completed:** Startup cleanup sweeps abandoned staging directories and unused owned
      installation roots.
16. [x] **Release notes ignore architecture.** (R3.16)
    - **Where:** `EmulatorManagerView.cs:101-110`.
    - **Effect:** the first offer lookup ignores architecture, so it can show the wrong offer's
      notes.
    - **Completed:** Both surfaces choose release notes by definition, channel and architecture.
      Steam payloads carry architecture; failed offers retain the native architecture too.
17. [x] **Existing imports show once as a preselected Update.** (new, low)
    - **Where:** `GameLibraryService.cs:1970`, where `SameLaunch(null, ...)` is false.
    - **Effect:** applying it writes only the record. This is acceptable under greenfield 2.0, but
      the preselection is noise.
    - **Completed:** Missing legacy content metadata alone no longer preselects an Update.

## Performance and behaviour

18. [x] **Emulator file activity triggers full rechecks.** (new; Codex's summary claims otherwise)
    - **Where:** `ManagedShortcutMonitor.cs:137-153,174-183,201-207`.
    - **Effect:** only `Changed` events are filtered. Created, Deleted and Renamed events in the
      recursively watched emulator program and data folders each start a full recheck: save states,
      screenshots and logs during play. An install triggers a second pass through the `AtomicFile`
      temp-file renames and the 12 INI writes in `ConfigureRetroArch`.
    - **Fix:** stop watching emulator folders, and react to `InstallationsChanged` only.
    - **Completed:** Removed every emulator filesystem watcher. Semantic InstallationsChanged now
      requests checks.
19. [x] **A recursive watcher on every launcher game's install folder.** (new)
    - **Where:** `LibrarySource.cs:140-145`, `ManagedShortcutMonitor.cs:111-127`.
    - **Effect:** every launcher game gets a Native content record and its own watcher, with no
      debounce. A running game's writes cause repeated full rechecks.
    - **Fix:** don't watch launcher content; rely on volume events and rescans.
    - **Completed:** Removed every launcher content filesystem watcher. Availability reacts to
      volume events, resume and explicit requests.
20. [x] **A full recheck right after every scan.** (new)
    - **Where:** `GameLibraryService.cs:1924`.
    - **Effect:** `NotifyManagedEntriesChanged` makes the monitor recheck titles the scan merge has
      just checked.
    - **Fix:** notify only when records changed outside a scan.
    - **Completed:** Scan completion updates dependency projections without requesting a redundant
      full availability pass.
21. [x] **Launch-time work got worse.** (R5.12)
    - **Where:** `ManagedContentLaunch.cs:24,36,54-70`.
    - **Effect:** each launch parses the import file 3 times and `emulators.json` 3 times, and runs
      `Check` twice, including the final re-check and re-read.
    - **Fix:** read once inside the gate.
    - **Completed:** Helper reads each store once and checks once inside the per-user admission
      gate, starts the contained child, then releases admission before waiting.
22. [x] **The launch helper polls its job.** (R5.12)
    - **Where:** `ManagedContentLaunch.cs:101-104`.
    - **Effect:** the job is polled every 200 ms.
    - **Fix:** wait on the job's completion port for `ACTIVE_PROCESS_ZERO`.
    - **Completed:** Managed game lifetime waits on the job completion port's ACTIVE_PROCESS_ZERO
      notification instead of polling.
23. [x] **Volume paths are resolved twice and allocate heavily.** (R4, R5.1)
    - **Where:** `ManagedContentModels.cs:457-458` resolves `BackingPath` twice without the mount
      cache. Every uncached `ResolvePath` allocates a 32K-character array plus a 32K-character
      string (`:383,405`).
    - **Fix:** reuse `primary`, and size the buffer from `required`.
    - **Completed:** Reused the already resolved primary path and allocated mount buffers from the
      size Windows reports.
24. [x] **ROM discovery repeats per-file work.** (R5.2)
    - **Where:** `RomLibrarySource.cs:311` runs `CapturePath(parent)` for every ROM, and `:324`
      re-reads the length the listing already has.
    - **Completed:** ROM scans reuse captured parent bindings and file lengths already returned by
      directory listing.
25. [x] **Folder and launcher sources repeat path captures.** (R5.14)
    - **Where:** `ShortcutFolderSource.cs:209,219` calls `CapturePath(root)` for every file.
      Launcher sources call `Directory.Exists` plus `CapturePath` for every game.
    - **Completed:** Folder scans reuse the root binding; launcher backing metadata avoids a
      redundant existence query before capture.
26. [x] **Every library revision carries the full emulator choices.** (R5.6)
    - **Where:** `GameLibraryService.cs:3043-3045`.
    - **Effect:** every revision, including each artwork title, serializes the full
      `emulatorChoices` with all installations and cores.
    - **Fix:** publish them only when emulators change.
    - **Completed:** Removed emulator systems/choices from every library DTO revision. Editors read
      cached backend state or subscribe to the independent emulator publication.
27. [x] **The artwork cache is rewritten in full every 5 seconds during a run.** (R5.7)
    - **Completed:** Removed full candidate-cache rewrites, periodic save timers and their disk
      serialization.
28. [x] **Applying writes the store several times per title.** (R5.19)
    - **Where:** each applied title makes 2 to 3 full durable writes
      (`GameLibraryService.cs:2294, 2327,2333`), and the search for unconfirmed records is linear
      (`:1691`).
    - **Completed:** Normal confirmed titles use one final SaveApplied write. Immediate durable
      writes remain for uncertain Adds with AppId and reconciled failed updates only.
29. [x] **INI handling is wasteful.** (new)
    - **Where:** `EmulatorPackages.Info` (`:375-392`) re-parses the whole file once per key, which
      is quadratic for every core. `ConfigureRetroArch` (`EmulatorManager.cs:682-687`) does 12
      separate read-and-rewrite passes.
    - **Fix:** parse once, and write all keys in one pass.
    - **Completed:** Core metadata is parsed once; declarative configuration keys are applied in a
      single INI edit/write.
30. [x] **Change detection costs extra serialization.** (new)
    - **Where:** `Mutate` serializes installations and preferences twice just to detect a change
      (`EmulatorManager.cs:758-764`).
    - **Completed:** Semantic receipt/preference change detection uses record sequences instead of
      serializing before and after.
31. [x] **Records are deep-copied needlessly.** (new)
    - **Where:** records that follow the preference are deep-copied twice per check
      (`ManagedContentModels.cs:333,509`). `UpdateEmulatorDependencies`
      (`GameLibraryService.Roms.cs:544-547`) deep-copies every ROM record to read one id.
    - **Completed:** Preference checks avoid duplicate copies; dependency counts use a locked scalar
      store query and cached canonical preference IDs rather than copied records.
32. [x] **The asset build loops instead of fixing the cause.** (R2.28, new)
    - **Where:** `eng/build-steam-assets.mjs:43-53`.
    - **Effect:** it formats until stable with a retry limit, always at least twice.
    - **Fix:** correct the non-idempotent source construct, or exclude the generated asset from
      Prettier and let the drift check gate it.
    - **Completed:** Asset builder formats once; generated JavaScript is excluded from blanket
      Prettier and remains owned by the asset drift check.

## Design and altitude

33. [x] **Hardening layers remain, against the no-security-theater decision.** (R2.16)
    - Receipt path validation on every `ReadStore`, including in the launcher
      (`EmulatorModels.cs:440-461`).
    - `AssertNoReparse` (`EmulatorPackages.cs:209-219`) and the reparse scan in `DeleteOwned`
      (`:230-239`).
    - Archive checks beyond path escape: links, `.`, `..`, `:`, duplicates and size or truncation
      (`:156-205`).
    - The PE machine check (`EmulatorManager.cs:386,938-955`).
    - Three mutual-exclusion layers for one purpose: the store mutex (`:737`), the admission gate
      (`:692`, `EmulatorModels.cs:601-655`) and `RequireStopped` (`:697`).
    - `SafeFiles` link refusal and the `DataPath` reparse check
      (`EmulatorPrerequisites.cs:31, 102-123`).
    - HTTPS re-checks on release-notes URLs and the final response URL (`EmulatorService.cs:175`,
      `EmulatorReleases.cs:67`).
    - Keep: the archive escape check, the `DeleteOwned` prefix check and the expected-volume
      binding. receipt/reparse/archive-overcheck/managed-PE/extra-mutex/running-image/redirect-HTTPS
      layers. Retained archive escape, owned-delete bounds and expected-volume binding.
    - **Completed:** Removed the listed extra hardening layers and retained the three accepted
      bounds. Corrected archive reader selection: ZIP/non-solid and sequential 7z/solid entries
      share one extraction function. All3 archive regressions pass, including normalized/duplicate
      ZIP entries, escape refusal and multi-file7z.
34. [x] **A generation counter in the shared artwork gate.** (new)
    - **Where:** `ArtworkRequestGate.cs:45-49,99-123,143,177-191,214,223,283`.
    - **Effect:** `_generation`, an `AsyncLocal` request generation and "settings changed" refusals,
      which the maintainer rules exclude. SteamGridDB goes through this gate too.
    - **Completed:** Removed request generations, their AsyncLocal scope and settings-changed
      refusals; retained existing background request priority.
35. [x] **The persistent artwork cache grew instead of shrinking.** (R2.23, new)
    - **Where:** `GameLibraryArtwork.cs:212,221-238,829-866`.
    - **Effect:** it keeps a 30-day lifetime, revision bookkeeping, a save loop and two 5-second
      throttles, and no decision records it.
    - **Fix:** remove it, or record a decision and reduce it to one write path.
    - **Completed:** Removed the secondary persistent artwork candidate cache, expiry/revision
      bookkeeping and save loop. Provider caches and actual applied-artwork receipts remain.
36. [x] **Migrations on unreleased 2.0.** (R2.3, R6, new)
    - `RegroupManualSources` (`ImportStateStore.cs:248-281`) runs at the start of every scan
      (`GameLibraryService.cs:1645`), with `Id`/`ManagedId` re-stamping at `:1768-1772`.
    - The store format version bump from 1 to 2 (`ManagedContentModels.cs:227`,
      `ImportStateStore.cs:16`).
    - `OwnsRecorded` skips the StartDirectory check when the recorded value is empty
      (`ImportPlan.cs:517`).
    - **Completed:** Removed manual regrouping, identity re-stamping, format migration and the
      empty-start-directory ownership exception. The unreleased format remains version1.
37. [x] **The provisional AppId-0 record is redundant.** (R2.8)
    - **Where:** it and its confirmation pass (`GameLibraryService.cs:1690-1714`) remain alongside
      `ConfirmedUtc`.
    - **Effect:** adoption by the exact `--managed <id>` command (`ImportPlan.cs:647-651`) already
      recovers such shortcuts.
    - **Fix:** remove both. Keep the durable record after an uncertain add that has an AppId.
    - **Completed:** Removed AppId-0 provisional records and the confirmation pass. Exact
      managed-command adoption and uncertain Add-with-AppId receipts remain.
38. [x] **`LastSuccessfulValidationUtc` is write-only.** (R2.4)
    - **Where:** written at `ImportStateStore.cs:139-143` and required by `RecordDefect`
      (`ManagedContentModels.cs:634`). Nothing reads it.
    - **Completed:** Removed the write-only successful-validation timestamp and its record-validity
      requirement.
39. [x] **The monitor still holds watchers that need suspend hooks.** (R2.2)
    - **Where:** recursive watchers on source and backing roots and on emulator folders
      (`ManagedShortcutMonitor.cs:111-153,169-185`). The suspend hooks remain in
      `RemovableDriveManager.cs:507` and `SdFormatManager.cs:526`.
    - **Fix:** recheck on volume events, resume and explicit request only. The hooks then go.
    - **Completed:** Removed monitor filesystem watchers, suspension leases, manager properties and
      disk-workflow suspension wiring.
40. [x] **Resolve's checks are repeated.** (R2.1)
    - **Where:** `SetRomEmulatorAsync` (`GameLibraryService.Roms.cs:171-183`) and
      `SetPreferredAsync` (`EmulatorManager.cs:268-277`) repeat `EmulatorStorage.Resolve`'s system
      and core checks, with three different messages.
    - **Completed:** ROM choice and system-default validation share
      EmulatorStorage.ValidateSelection with runtime Resolve, using one set of selection messages.
41. [x] **Managed records are still built and patched in several places.** (R2.9)
    - **Where:** `PrepareManagedContent` changes the record in place and runs twice per Add
      (`GameLibraryService.cs:2089,2210`). The scan patches Location, volume, Program,
      WorkingDirectory and Id afterwards (`:1741-1772`).
    - **Fix:** build the full record once at discovery.
    - **Completed:** Discovery creates complete managed records once through shared factories;
      removed PrepareManagedContent, duplicate Add preparation and scan-time field patching.
42. [x] **Single ROMs are still whole sources.** (R2.7)
    - **Where:** `RomSourceConfig.Path` may be a file (`RomLibrarySource.cs:22,254`), and
      `ContentIdentity` covers ROMs only (`ImportPlan.cs:474-485`).
    - **Completed:** Single ROMs persist as authored entries and use leaf ReadSingle/shared
      CreateRom parsing. Configured ROM scanners are directory-only. Content identity covers ROM and
      command-backed imports.
43. [x] **The managed and direct `Runs` branches are identical.** (R2.5)
    - **Where:** `ShortcutRoute.cs:242-252`. Merge them.
    - **Completed:** Merged identical managed/direct route matching branches.
44. [x] **Three vocabularies for source kind.** (R2.11)
    - **Where:** the `LibrarySourceKind` and `ManagedContentKind` enums plus the
      `GameLibrarySourceKinds` strings (`GameLibraryState.cs:191`). Id-prefix checks remain in
      `GameLibraryRules.cs:45,67,83`.
    - **Completed:** One shared LibrarySourceKind enum replaces both enums and UI string constants;
      normalization no longer infers kinds from ID prefixes.
45. [x] **RetroArch knowledge is still in code.** (R2.13)
    - `ConfigureRetroArch` is keyed on `HasCores` (`EmulatorManager.cs:664-685`).
    - `{config}` is hard-wired to `retroarch.cfg` in two token expanders (`EmulatorModels.cs:560`,
      `EmulatorValidation.cs:29`).
    - There is a `"retroarch"` default (`RomLibrarySource.cs:191`).
    - Validation matches `Output.Contains(definition.Id)` (`EmulatorValidation.cs:42`).
    - **Completed:** Configuration filenames/paths and probe output are catalog data. Removed
      hard-coded RetroArch defaults and token filenames.
46. [x] **Release providers keep per-emulator quirks.** (R2.15)
    - Buildbot is RetroArch-only (`EmulatorReleases.cs:288,306,312,328`).
    - The `"Gitea"` alias (`:107`) and the `Prerelease` option (`:218`) are unused.
    - Scoop has a hidden github.com special case (`:153-177`).
    - **Completed:** Buildbot layout/package names are metadata; removed unused Gitea/Prerelease
      options and Scoop's hidden GitHub branch.
47. [x] **The title rename keeps its state.** (R2.26)
    - **Where:** the `steamAtScan` map, the stale-name refusal (`GameLibraryService.cs:2168-2177`)
      and `TitleRenamePending` remain.
    - **Completed:** Removed the scan-name map, stale-name refusal and TitleRenamePending state;
      explicit title changes derive the required rename from existing choices/receipts.
48. [x] **WSGM sheets don't use the kit sheet.** (R2.29)
    - **Where:** most of them pass `{}` to `renderSteamUiSheet` and hand-roll their own note, error
      and action-bar markup and CSS (`library-import.ts:210-213`).
    - **Completed:** Every Steam sheet uses kit note/error/actions props; removed duplicate generic
      note/error/footer styling.
49. [x] **Screenscraper naming in a generic record.** (R2.21)
    - **Where:** the generic artwork request record has a field named `ScreenscraperSystemId`
      (`GameLibraryArtwork.cs:78`).
    - **Completed:** Renamed the generic artwork request field to PlatformId; provider-specific
      system identities remain in provider profiles.
50. [x] **Helpers in the wrong place.** (new)
    - `VerifiedDownload.cs` sits in `Core/Emulators` although `UpdateChecker` uses it; it belongs in
      `Core`.
    - `JsonRead.HttpsUrl` now depends on `Artwork.ArtworkUrls`.
    - **Completed:** Moved VerifiedDownload to Core and moved neutral URL validation into
      Core.HttpUrls; JsonRead no longer depends on artwork.

## Reuse

51. [x] **Path containment checks are hand-written at 10 sites.** (R3.15)
    - **Where:** `EmulatorManager.cs:375,714,895`; `EmulatorPackages.cs:146,164,224,401`;
      `EmulatorPrerequisites.cs:59`; `EmulatorModels.cs:451`; `ManagedContentModels.cs:440-443`.
    - `AssertNoReparse` also duplicates `SoundPackLibrary.CheckPath`.
    - **Fix:** one shared `IsUnder(root, path)` helper.
    - **Completed:** Remaining containment checks use Shared/Library/StoragePaths.IsUnder; removed
      reparse helper duplication. Added directory-boundary, traversal and volume-root regressions.
52. [x] **The mutex pattern is written three times.** (R3.12)
    - **Where:** the wait, abandoned-counts-as-owned, release pattern at
      `EmulatorManager.cs:735-775` and `EmulatorModels.cs:601-626,628-655`.
    - **Completed:** Both admission APIs share NamedMutexLease's wait, abandoned-owner and disposal
      implementation. One per-user gate is shared with launch admission.
53. [x] **Config path fields don't use the shared path type.** (R3.20)
    - `RomSourceConfig` and `ShortcutFolderConfig` still spell out `Path`/`VolumeId`/`RelativeRoot`.
    - The `ManagedContentPath` construction is duplicated (`RomLibrarySource.cs:65-71`,
      `ShortcutFolderSource.cs:167-171`).
    - The same-location comparison is written three times (`GameLibraryService.Roms.cs:71-80`,
      `GameLibraryService.cs:774-778,791-795`).
    - **Completed:** ROM/folder configs use ManagedContentPath Root and shared location/binding
      helpers. Both editors preserve or recapture those bindings.
54. [x] **WSGM.Launch keeps its own RunAsInvoker code.** (R3.1)
    - **Where:** `WSGM.Launch/Program.cs:597-609`.
    - **Fix:** call `ContainedProcessStart.UseCallerIntegrity`.
    - **Completed:** The medium child now calls the shared ContainedProcessStart.UseCallerIntegrity
      implementation.
55. [x] **Extension splitting is duplicated in the overlay.** (R3.16)
    - **Where:** `GameLibraryView.Roms.cs:93-94` and `GameLibraryView.Tools.cs:81-82`.
    - **Completed:** Both overlay source editors use one ParseExtensions implementation with
      consistent trimming and normalization.
56. [x] **The Release notes row is copied.** (R3.16)
    - **Where:** `EmulatorManagerView.cs:101-110` and `:176-190`.
    - **Completed:** Definition and installed-emulator pages share AddReleaseNotes and the
      architecture-aware offer selection.
57. [x] **Config types aren't registered directly in the JSON context.** (R3.17)
    - **Where:** `RomSourceConfig` and `ManualShortcutConfig` are reachable only through
      `GameLibraryState`, not registered in `GameLibraryJsonContext`.
    - **Completed:** Registered ROM and authored-command configs directly in the source-generated
      library JSON context.
58. [x] **The launch helper re-implements `LaunchArguments.Join`.** (R3.10, low)
    - **Where:** `ManagedContentLaunch.cs:151-156`.
    - **Fix:** link the joiner into src/Shared. launch previews and validation; removed the
      duplicate class/joining path.
    - **Completed:** LaunchArguments is linked into all3 consumers; contained startup, previews and
      validation share its joiner. The4 launcher regressions pass, including unqualified executable
      search.
59. [x] **Older duplicates outside this change.** (R3.1, R3.14; pre-existing, low)
    - Private `CloseHandle` in `WSGM.Launch/Elevation.cs:76` and `JobObject.cs:138`, although
      `Win32Common` is now linked there.
    - HTTPS checks in `ArtworkDownload.cs:43`, `SteamGridDb.cs:559`, `ImportStateStore.cs:656`,
      `UpdateChecker.cs:229` and `StoreCatalogClient.cs:332` instead of `ArtworkUrls.IsHttps`.
    - **Completed:** Launch token/job cleanup uses shared Win32Common.CloseHandle. Artwork, import
      picks, release assets and store-catalog URL checks use neutral Core.HttpUrls.IsHttps.

## Dead and derivable code

60. [x] **The managed follow path can never run.** (new)
    - **Where:** `ManagedLaunchKind.Follow`, `FollowDirectory`, `FollowMarker` and the launcher's
      Follow branch (`ManagedContentModels.cs:62-63,166-170`, `ManagedContentLaunch.cs:72-83`,
      `GameLibraryService.Roms.cs:361-367`).
    - **Completed:** Removed unreachable managed Follow strategy, metadata and launch branch;
      existing native follow support remains.
61. [x] **`ImportStateStore.UserRoot` is unused.** (new)
    - **Where:** `ImportStateStore.cs:104`.
    - **Completed:** Removed unused ImportStateStore.UserRoot.
62. [x] **`PreviewCommand` and `PreviewWorkingDirectory` are dead.** (R4)
    - **Where:** they are never written, but are still read (`GameLibraryService.Roms.cs:380-383`)
      and null-checked (`ManagedContentModels.cs:635`).
    - **Completed:** Removed unread/unwritten managed preview fields and their rendering/validation
      paths.
63. [x] **The artwork reset path is dead.** (new)
    - **Where:** `ArtworkSearch.ResetCaches` has no callers, so the `CachesReset` →
      `GameLibraryArtwork.ResetCache` path is dead (`ArtworkProviders.cs:356`,
      `GameLibraryArtwork.cs:249,437`).
    - **Completed:** Removed the dead global artwork-reset event chain. Retained provider cache
      reset used by the artwork browser.
64. [x] **`emulator-definitions.json` is still read, but nothing writes it.** (R4)
    - **Where:** `EmulatorCatalog.Load` (`EmulatorCatalog.cs:64-74`) reads it on init and on every
      Refresh.
    - **Completed:** Catalogue loading uses the embedded bundled resource. Removed the unused
      external override read and its copied setup payload file.
65. [x] **`RomSystemProfile.DefaultEmulatorId` is never read.** (R4)
    - **Where:** `RomLibrarySource.cs:119`, filled at `:142-165`.
    - **Completed:** Removed unused profile DefaultEmulatorId and constructor data.
66. [x] **`ShortcutFolderConfig.Name` and `LibraryBacked` have no product writer.** (R4)
    - **Where:** `GameLibraryConfig.cs:29,32`.
    - **Completed:** Removed unwritten folder Name/LibraryBacked fields; folder names derive from
      their root and folder imports use their declared tracked-content policy.
67. [x] **`UpstreamCrc32` and `InstalledUtc` are write-only.** (R4)
    - **Where:** `EmulatorModels.cs:76,170`.
    - **Completed:** Removed write-only core UpstreamCrc32 and installation InstalledUtc receipt
      fields; download verification remains.
68. [x] **`EmulatorOffer.Provider` is never read.** (R4)
    - **Where:** written at `EmulatorManager.cs:138`.
    - **Completed:** Removed the unread EmulatorOffer.Provider field and its writers.
69. [x] **The shared `EmulatorDefinition` view has unread fields.** (R4)
    - **Where:** `Provider`, `Architectures`, `ExecutableNames` and `LaunchArguments`
      (`EmulatorCatalog.cs:48-52`).
    - **Completed:** Removed unread fields from the shared EmulatorDefinition view; backend package
      definitions retain fields needed for installation.
70. [x] **The `emulatorRoute` delegate always returns a constant.** (R4)
    - **Where:** `SteamExtensionsTabBackend.cs:60`, `SteamUiSessionHost.cs:221`.
    - **Completed:** Replaced the constant route delegate with a session availability flag and
      SteamEmulatorSurface.Route.
71. [x] **`Placeholder`'s record parameter is optional for no reason.** (R4)
    - **Where:** `Placeholder(..., record = null)` at `GameLibraryService.cs:3048` has one caller,
      which always passes it.
    - **Completed:** Placeholder's record argument is now required at its single call site.
72. [x] **`UsesRawArguments` is derivable.** (R4)
    - **Where:** `ManagedContentModels.cs:146`; it always equals `SourceKind != Rom`.
    - **Completed:** Removed stored UsesRawArguments; ROM/direct argument behavior follows the
      source/launch policy.
73. [x] **A dead `!OperatingSystem.IsWindows()` branch.** (R4)
    - **Where:** `ManagedContentModels.cs:348`.
    - **Completed:** Removed the unreachable non-Windows path from this Windows-only shared code.

## Style and conventions

74. [x] **An em dash in user-facing text.** (R6)
    - **Where:** `ManagedContentLaunch.cs:182`, "WSGM — Content unavailable".
    - **Completed:** Replaced the launch refusal dialog's em dash with plain punctuation.
75. [x] **Formatting churn in `library-badge.ts`.** (new)
    - **Where:** double spaces at `:218` and `:501` (`let jsxRuntime:  any  =  null;`, an unchanged
      line rewritten), and odd line breaks at `:238`, `:493-495` and `:504-505`.
    - **Completed:** Reformatted the badge source to remove doubled spaces and the listed awkward
      breaks.
76. [x] **Mixed indentation in `ui-kit.ts`.** (new)
    - **Where:** the new helpers (`:291-342`) mix 4-space and 2-space indentation and unspaced
      braces in a 2-space file. The toolkit `.editorconfig` asks for 4.
    - **Completed:** Toolkit ui-kit and badge sources now use consistent four-space formatting; the
      root formatter has a matching toolkit override.
77. [x] **LF line endings in the toolkit.** (new)
    - **Where:** several toolkit files are LF against `.editorconfig` `end_of_line = crlf`.
    - **Completed:** Normalized changed toolkit TypeScript, checker and C# source/test files to CRLF
      without altering unrelated C# logic.
78. [x] **Shared-file headers are missing or stale.** (new)
    - New shared files lack the "Shared between ... (linked as a source file)" header:
      `src/Shared/Library/{EmulatorModels,IniFile,ManagedContentModels,UserDataContext}.cs` and
      `src/Shared/Process/ContainedProcessStart.cs`.
    - Stale headers: `Kernel32.cs`, `WindowsCommandLine.cs` and `AtomicFile.cs` don't list
      PackagedLaunch, and `ParentProcessStart.cs` and `Win32Common.cs` don't list WSGM.Launch.
    - **Completed:** Added linked-source consumer headers to the new shared library/process files
      and corrected all five listed stale headers.
79. [x] **A garbled doc comment.** (new)
    - **Where:** `EmulatorModels.cs:378`, "new used by emulator management...".
    - **Completed:** Rewrote the garbled shared emulator-storage documentation comment.

## Housekeeping

80. [x] **A leftover agent worktree.** It is at `.claude/worktrees/agent-a1a3754e7bef61c5c`, on an
        older commit, and predates this work. Remove it if it isn't needed.
    - **Completed:** Verified the old worktree had no tracked changes or unique commits, backed up
      its ignored local settings, and removed it with git worktree remove. Its path and registration
      are gone.

## Accepted, no action

- RunAsInvoker for managed launches instead of an elevation path.
- The archive escape check, the `DeleteOwned` prefix check and the expected-volume binding.
- A durable record after an uncertain Steam add that has an AppId.
- The Device SDK keeping its own CRC32, so the MIT SDK takes no package dependency.
- `RecordDefect`'s friendly invalid-data refusal.
- `RepairAsync` reinstalling the exact recorded version.
- `RenameAsync`'s documented token behaviour.
- Two `.index` reads per install, the second deliberately checking whether the catalogue changed.

## Validation for this fix pass

All 80 numbered items are implemented and checked off.

- Release solution compilation passed with zero warnings and errors. Scoped Rider cleanup,
  formatting and Steam asset drift checks ran.
- The first focused application run passed 260 of 261 cases and exposed the ZIP-reader bug. It was
  fixed, then all three archive cases passed, including the added multi-file 7z case. The four
  launcher cases also passed after the shared joiner correction. This establishes 262 passing
  application cases across the initial and affected follow-up runs.
- All 14 AMD native contract cases passed. All 18 emitted Steam UI checks and module discovery
  passed. Steam asset SHA-256: `DC6A66F54E5DB1245049E880C92EC1D36D596C9B7CE69BB4961B6E94C38DA93B`.
- The canonical setup build passed, rebuilding all six bundled plugins with zero outdated packages.
  Package manifest hashes, copies inside the installer payload and rebuilt plugin DLLs match.
- Final installer: `C:\Users\N1GHT\ownCloud\Persönlich\WSGM-Setup-2.1.0.exe`, version `2.1.0.1644`,
  404,504,343 bytes. SHA-256: `11DEF6FE908D298CA1BF4F549AE2AB2C0C31D4338D26AF7AD0CD5194A54BBA4F`.
- The final application was deployed after closing and restarting WSGM and Steam as authorized. The
  five checked application/helper/toolkit components match the final published bytes. Both
  `wsgm.emulators` and `wsgm.library-import` reported Applied and Verified at 23:56:12 in
  `wsgm.log`.
- Source base: parent `bf31ab561d57f7e80e42fdc2e34e045b99574cc1`, toolkit
  `689afb3e08a440b8f9e6ed39ca54d6dcf430edc5`. No commits, staging, pushes or issue closures were
  performed for this task; both indexes are empty and the changes remain uncommitted.

Full test/coverage gates and UI baseline refreshes remain deferred under the repository's
manual-first policy. Live acceptance of the new overlay/CEF flows, real emulator installs and full
core downloads, and Ally X tester confirmation remain separate from these automated results.
