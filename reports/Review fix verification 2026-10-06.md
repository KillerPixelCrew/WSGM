# Review fix verification, 2026-10-06

This checks Codex's claim, in
[Uncommitted review fixes 2026-10-06](Uncommitted%20review%20fixes%202026-10-06.md), that every
finding in [the combined review](Uncommitted%20changes%20review%202026-10-06.md) is fixed. Six
verifiers each traced their section's findings through the current uncommitted tree. Item numbers
refer to the combined review.

The solution builds in Debug with 0 errors and no warnings, and
`node eng/build-steam-assets.mjs --check` reports the asset current. No tests were run, and nothing
was edited.

**Verdict: the claim does not hold.** All of section 1's bugs are fixed apart from small leftovers,
and so are most of the efficiency items. But about 45 original findings are still partial, not
fixed, or kept without a valid reason. The rework also introduced about 25 new issues, four of them
real bugs.

## New bugs introduced by the fixes

1. **Apply pre-saves every Add before any Steam write.** See `GameLibraryService.cs:2084-2104` and
   `ImportStateStore.cs:222-245` (`SaveMany` replaces existing records).
   - If a run fails, is cancelled, or an entry fails `Revalidate`, the titles it never reached keep
     AppId 0 and no `ConfirmedUtc`. The next scan shows them as unticked and Unconfirmed.
   - A "deleted from Steam" Add loses its old AppId, OwnsProfile and ArtworkApplied, so its
     controller override can no longer be released.
2. **ROM titles can be marked Unavailable at startup, and it sticks.** See
   `EmulatorManager.cs:34,44,63,105`, `EmulatorService.cs:186-194`, `ManagedShortcutMonitor.cs:46`
   and `ShellSession.cs:395,991`.
   - The manager initializes in the background, while the monitor's first recheck can run against
     the still-empty snapshot. It then records EmulatorUnavailable for every ROM and publishes the
     badges.
   - Finishing initialization raises only `Changed`, not `InstallationsChanged`, so nothing
     rechecks.
   - If the first store read fails once, the store stays empty for the session. Launch still works,
     because the helper reads from disk.
3. **One unsupported external emulator breaks every Refresh.** `EmulatorManager.cs:622-623` misses
   `InvalidDataException` and `BadImageFormatException`, which an ARM64 or x86 `retroarch.exe` found
   on a known path throws (`:541-545,954`). Every Refresh then fails, and discovery stops early.
4. **The "Launch command" note prints `System.String[]`.** At `GameLibraryService.cs:1783-1788` a
   `string[]` is concatenated into the note. It should use `LaunchArguments.Join`.

## Other new issues

5. **Process start resolves programs differently.** `ContainedProcessStart.cs:60` now passes
   `FileName` as `lpApplicationName`, where the old `SuspendedProcess` passed null. WSGM.Launch
   loses the PATH search and the implied `.exe` for targets that aren't fully qualified.
6. **Thread safety.** `EmulatorManager.cs:62-74` and `:117-118` write `_state`, `_status`,
   `_catalog` and `_definitionViews` outside `_stateLock`, while `GetSnapshot` reads them under the
   lock. `_initialized` is not volatile.
7. **Emulator file activity triggers full rechecks.** `ManagedShortcutMonitor.cs:137-153,174-183`
   filters only `Changed` events. Created, Deleted and Renamed events in the recursively watched
   program and data folders each start a full recheck: save states, screenshots and logs during
   play. This contradicts the claim in Codex's summary.
   - An install also triggers a second pass through the `AtomicFile` temp-file renames, including
     the 12 INI writes in `ConfigureRetroArch`.
8. **A recursive watcher on every launcher game's install folder.** Every launcher game now gets a
   Native content record (`LibrarySource.cs:140-145`), so the monitor watches each one recursively,
   with no debounce (`ManagedShortcutMonitor.cs:111-127`). A running game's logs or cache writes
   cause repeated full rechecks.
9. **A full recheck right after every scan.** Each scan ends with `NotifyManagedEntriesChanged`
   (`GameLibraryService.cs:1924`), so the monitor rechecks titles the scan merge has just checked.
10. **A paused artwork provider blocks fallback.** It throws at `ArtworkProviders.cs:328-331`. While
    Screenscraper is paused, ROM titles never fall back to SteamGridDB.
11. **A generation counter in the shared artwork gate.**
    `ArtworkRequestGate.cs:45-49,99-123, 143,177-191,214,223,283` adds `_generation`, an
    `AsyncLocal` request generation and "settings changed" refusals. That is the kind of generation
    mechanism the maintainer rules rule out, and SteamGridDB goes through it too.
12. **The artwork cache gained bookkeeping.**
    - `GameLibraryArtwork.cs:221-238,829-866` adds `_cacheRevision`, `_savedCacheRevision`,
      `_cacheSaving`, a save loop and two 5-second throttles.
    - Only IO and access errors are caught (`:887`), so any other exception leaves `_cacheSaving`
      stuck at true.
    - `PruneCache` (`:428-429`) drops the entries of a disabled source, so re-enabling it re-fetches
      against the Screenscraper quota.
13. **A migration on greenfield 2.0.** `RegroupManualSources` (`ImportStateStore.cs:248-281`) runs
    at the start of every scan (`GameLibraryService.cs:1645`), with `Id`/`ManagedId` re-stamping at
    `:1768-1772`.
14. **New dead code.**
    - `ManagedLaunchKind.Follow`, `FollowDirectory`, `FollowMarker` and the launcher's Follow branch
      can never be reached (`ManagedContentModels.cs:62-63,166-170`,
      `ManagedContentLaunch.cs:72-83`, `GameLibraryService.Roms.cs:361-367`).
    - `ImportStateStore.UserRoot` (`:104`) has no callers.
    - `PreviewCommand` and `PreviewWorkingDirectory` are no longer written but are still read.
    - `ArtworkSearch.ResetCaches` has no callers, which makes the `CachesReset` path dead.
15. **Collection sync no longer recognises unticked ROM systems.** `unticked.Contains(group)`
    (`GameLibraryService.cs:2405`) never matches a `rom-system:` group, so disabling a ROM source no
    longer leaves its collection alone.
16. **Nightly RetroArch repair misses its retained archive.** `EmulatorManager.cs:342` looks the
    archive up by the URL's file name, but it was saved under `Asset.Name`
    (`EmulatorPackages.cs: 58-59,78-93`). Repair downloads it again, and fails once Buildbot has
    pruned that nightly.
17. **Existing imports show once as a preselected Update.** `SameLaunch(null, ...)` is false
    (`GameLibraryService.cs:1970`). Applying it writes only the record. This is low impact.
18. **The TS argument list editor adds an empty row the host refuses.** The editor adds a blank row
    (`ui-kit.ts:339`), but `TryReadStrings` refuses blank strings
    (`SteamLibraryImportSurface.Managed.cs:110-122`). Saving with an unfilled row gives "The launch
    arguments are invalid."
19. **Leftover `staging-*` folders.** A crash can leave them behind, and `PruneVersions` never
    sweeps them.
20. **Efficiency.**
    - `EmulatorPackages.Info` (`:375-392`) re-parses the whole file once per key, which is quadratic
      per core.
    - `ConfigureRetroArch` does 12 read-and-rewrite passes (`EmulatorManager.cs:682-687`).
    - `Mutate` serializes installations and preferences twice just to detect a change (`:758-764`).
    - Records that follow the system preference are deep-copied twice per check
      (`ManagedContentModels.cs:333,509`).
    - `UpdateEmulatorDependencies` (`GameLibraryService.Roms.cs:544-547`) deep-copies every ROM
      record to read one id.
21. **Conventions.**
    - The new shared files (`src/Shared/Library/*`, `ContainedProcessStart.cs`) lack the "Shared
      between ..." header.
    - Existing headers are now stale: `Kernel32.cs`, `WindowsCommandLine.cs` and `AtomicFile.cs`
      don't list PackagedLaunch, and `ParentProcessStart.cs` and `Win32Common.cs` don't list
      WSGM.Launch.
    - `VerifiedDownload.cs` sits in `Core/Emulators`, but `UpdateChecker` uses it.
    - `JsonRead` now depends on `ArtworkUrls`.
    - The doc comment at `EmulatorModels.cs:378` is garbled.
22. **Toolkit formatting.**
    - `library-badge.ts` has double spaces (`:218`, `:501`) and odd line breaks (`:238`, `:493-495`,
      `:504-505`).
    - The new `ui-kit.ts` helpers mix 4-space and 2-space indentation in a 2-space file.
    - Several files are LF against the `.editorconfig` CRLF.
23. **`build-steam-assets.mjs` loops instead of fixing the cause.** It reformats until the output
    stops changing, with a retry limit (`:43-53`), and always formats the bundle at least twice. The
    non-idempotent source is not fixed.

## Original findings still open

### Section 1, bugs

All 33 are fixed, with the residuals listed under new issues 12, 18 and 19. Item 5 (elevation) is
fixed with RunAsInvoker, which matches the existing WSGM.Launch policy. Codex's justification for it
holds.

### Section 2, altitude and design

- **1, leftover:** `SetRomEmulatorAsync` (`Roms.cs:171-183`) and `SetPreferredAsync`
  (`EmulatorManager.cs:268-277`) repeat `Resolve`'s system and core checks, with three different
  messages.
- **2, partial:** the monitor is down to 275 lines. Recursive watchers on source and backing roots
  and on emulator folders remain, and so do the `Suspend` hooks in `RemovableDriveManager.cs:507`
  and `SdFormatManager.cs:526`.
- **3, partial:** the duplicate version constant is gone, but the version bump from 1 to 2
  (`ManagedContentModels.cs:227`) remains on unreleased 2.0. It is not listed as kept.
- **4, kept without a valid reason:** `LastSuccessfulValidationUtc` is written
  (`ImportStateStore.cs:139-143`) and required by `RecordDefect` (`ManagedContentModels.cs:634`),
  but nothing reads it.
- **5, leftover:** the managed branch of `Runs` is identical to the direct branch
  (`ShortcutRoute.cs:242-252`).
- **6, leftover:** `OwnsRecorded` skips the StartDirectory check when the recorded value is empty
  (`ImportPlan.cs:517`), which is a compatibility allowance.
- **7, partial:** a single ROM is still a whole source (`RomLibrarySource.cs:22,254`), and
  `ContentIdentity` still covers ROMs only (`ImportPlan.cs:474-485`).
- **8, partial:** the provisional AppId-0 record and the confirmation pass
  (`GameLibraryService.cs:1690-1714`) remain, although adoption by the exact `--managed <id>`
  command already recovers such shortcuts. See new bug 1.
- **9, partial:** `PrepareManagedContent` still changes the record in place and runs twice per Add
  (`GameLibraryService.cs:2089,2210`). The scan still patches Location, volume, Program and Id
  (`:1741-1772`).
- **11, leftover:** there are three vocabularies for one idea: `LibrarySourceKind`,
  `ManagedContentKind` and the `GameLibrarySourceKinds` strings. Id-prefix checks remain in
  `GameLibraryRules.cs:45,67,83`.
- **13, partial:** RetroArch knowledge is still in code, keyed on `HasCores`:
  - `ConfigureRetroArch`.
  - `{config}` is hard-wired to `retroarch.cfg` in two copies (`EmulatorModels.cs:560`,
    `EmulatorValidation.cs:29`).
  - A `"retroarch"` default (`RomLibrarySource.cs:191`).
  - `Output.Contains(definition.Id)` (`EmulatorValidation.cs:42`).
- **15, partial:** Buildbot is still RetroArch-only (`EmulatorReleases.cs:288-328`). The `"Gitea"`
  alias and the `Prerelease` option are unused, and the Scoop provider has a hidden github.com
  special case (`:153-177`).
- **16, mostly kept without a valid reason.** The archive escape check, the `DeleteOwned` prefix
  check and the expected-volume binding are justified. These remain against the no-security-theater
  decision:
  - receipt path validation on every `ReadStore` (`EmulatorModels.cs:440-461`)
  - `AssertNoReparse`'s parent-folder walk (`EmulatorPackages.cs:209-219`). It refuses every install
    when `%LOCALAPPDATA%` or Documents is behind a junction or OneDrive.
  - the reparse scan in `DeleteOwned` (`:230-239`)
  - archive checks beyond escape (`:156-205`)
  - the PE machine check (`EmulatorManager.cs:386,938-955`)
  - three mutual-exclusion layers: store mutex, admission gate and `RequireStopped`
  - `SafeFiles` and the `DataPath` reparse check (`EmulatorPrerequisites.cs:31,102-123`)
  - HTTPS re-checks (`EmulatorService.cs:175`, `EmulatorReleases.cs:67`)
- **21, leftover:** the generic request record still has a field named `ScreenscraperSystemId`
  (`GameLibraryArtwork.cs:78`).
- **23, kept without a valid reason:** the persistent artwork cache keeps its 30-day lifetime and
  now has more bookkeeping (new issue 12). No decision records it.
- **26, partial:** the `steamAtScan` map, the stale-name refusal
  (`GameLibraryService.cs: 2168-2177`) and `TitleRenamePending` remain.
- **28, partial:** see new issue 23.
- **29, partial:** most WSGM sheets still pass `{}` to `renderSteamUiSheet` and hand-roll their own
  note, error and action-bar markup and CSS. See new issue 22 for the formatting.

### Section 3, reuse

- **1, leftover:** `WSGM.Launch/Program.cs:597-609` keeps its own RunAsInvoker code instead of
  calling `ContainedProcessStart.UseCallerIntegrity`.
- **2, partial:** `ImportStateStore.UserRoot` still infers the root from a path, and it is now
  unused.
- **8, kept, reasonable:** the Device SDK keeps its own CRC32, which avoids a package dependency in
  the MIT SDK.
- **11, leftover:** see new issue 20.
- **12, partial:** the mutex wait/abandoned/release pattern is still written three times
  (`EmulatorManager.cs:735-775`, `EmulatorModels.cs:601-626,628-655`).
- **15, not fixed:** hand-written `StartsWith(root + separator)` containment checks remain at 10
  sites:
  - `EmulatorManager.cs:375,714,895`
  - `EmulatorPackages.cs:146,164,224,401`
  - `EmulatorPrerequisites.cs:59`
  - `EmulatorModels.cs:451`
  - `ManagedContentModels.cs:440-443`

  `AssertNoReparse` also still duplicates `SoundPackLibrary.CheckPath`.

- **16, partial:**
  - Extension splitting is still duplicated in the overlay (`GameLibraryView.Roms.cs:93-94`,
    `GameLibraryView.Tools.cs:81-82`).
  - The Release notes row is still copied in `EmulatorManagerView.cs:101-110` and `:176-190`, and
    the first lookup ignores architecture.
- **17, leftover:** `RomSourceConfig` and `ManualShortcutConfig` are reachable only through
  `GameLibraryState`, not registered in `GameLibraryJsonContext` directly.
- **20, not fixed:**
  - The configs still spell out `Path`/`VolumeId`/`RelativeRoot`.
  - The `ManagedContentPath` construction is duplicated (`RomLibrarySource.cs:65-71`,
    `ShortcutFolderSource.cs:167-171`).
  - The same-location comparison is written three times (`GameLibraryService.Roms.cs:71-80`,
    `GameLibraryService.cs:774-778,791-795`).
- **21, partial:** `GameLibraryService.Roms.cs:535-538` re-codes the preference fallback without
  `NormalizeSystemId`, and its `ToDictionary` throws on duplicates. `SetPreferredAsync` compares ids
  exactly (`EmulatorManager.cs:283`), while clearing a preference normalizes them (`:260`).

### Section 4, simplification and dead code

- **Dead code still present:**
  - `EmulatorCatalog.Load` still reads `emulator-definitions.json` on init and on every Refresh,
    although nothing writes it.
  - `RomSystemProfile.DefaultEmulatorId`.
  - `ShortcutFolderConfig.Name` and `LibraryBacked`.
  - `UpstreamCrc32` and `InstalledUtc`.
  - `EmulatorOffer.Provider`.
  - The `EmulatorDefinition` view's `Provider`, `Architectures`, `ExecutableNames` and
    `LaunchArguments`.
- **Constant parameters:** the `emulatorRoute` delegate (`SteamExtensionsTabBackend.cs:60`) and
  `Placeholder(..., record = null)` (`GameLibraryService.cs:3048`).
- **Derivable state:** `UsesRawArguments` still equals `SourceKind != Rom`. `PreviewCommand` is now
  fully dead.
- **Redundant work:**
  - The launch helper's final `Check` and re-read (`ManagedContentLaunch.cs:54-70`).
  - The uncached double `ResolvePath` in `Validate` (`ManagedContentModels.cs:457-458`).
  - The dead `!OperatingSystem.IsWindows()` branch (`:348`).
- **Kept, acceptable:**
  - `RecordDefect` (the friendly invalid-data refusal).
  - `RepairAsync`, which now reinstalls the exact version.
  - `RenameAsync`, whose token behaviour is documented.

### Section 5, efficiency

- **2, partial:** `CapturePath(parent)` still runs per ROM (`RomLibrarySource.cs:311`), and
  `new FileInfo(file).Length` re-reads a length the listing already has (`:324`).
- **6, partial:** every library revision, including each artwork title, still serializes the full
  `emulatorChoices` (`GameLibraryService.cs:3043-3045`).
- **7b, partial:** the whole cache file is still rewritten every 5 seconds during a run.
- **12, worse than before:**
  - Each launch parses the import file 3 times and `emulators.json` 3 times, and runs `Check` twice
    (`ManagedContentLaunch.cs:24,36,54`).
  - The job is still polled every 200 ms (`:101-104`).
- **14, partial:**
  - `ShortcutFolderSource` calls `CapturePath(root)` for every file (`:209,219`).
  - Launcher sources still call `Directory.Exists` plus `CapturePath` per game.
- **19, partial:**
  - Each applied title still makes 2 to 3 full durable writes
    (`GameLibraryService.cs:2294,2327, 2333`).
  - The search for unconfirmed records is still linear (`:1691`).
- **21, partial:** see new issue 23.

### Section 6, rules and style

- **Em dash:** `ManagedContentLaunch.cs:182` still has "WSGM — Content unavailable". The badge one
  is fixed.
- **Version bump:** see section 2, item 3.
- **Toolkit formatting:** the whole-file reformatting is gone, but new churn was added (new issue
  22).
- **Fixed:** readback gating, the `Runs` recursion and the `Sources()` backfill.

## Codex's "kept" justifications

| Kept item                                                         | Holds?                                                                  |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------- |
| RunAsInvoker instead of elevation                                 | Yes, it matches WSGM.Launch.                                            |
| Expected-volume binding, archive escape, owned-path delete prefix | Yes.                                                                    |
| Durable record after an uncertain Steam add that has an AppId     | Yes.                                                                    |
| AppId-0 pre-records before every Add                              | No. They are redundant with `--managed` adoption, and caused new bug 1. |
| `LastSuccessfulValidationUtc`                                     | No. Nothing reads it.                                                   |
| The other hardening layers (section 2, item 16)                   | No.                                                                     |

## Unrelated

A leftover agent worktree from an older commit exists at
`.claude/worktrees/agent-a1a3754e7bef61c5c`. It predates this work.
