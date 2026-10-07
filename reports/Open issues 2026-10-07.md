# Open issues, 2026-10-07

This is everything still open after Codex's second fix pass. Codex ticked all 80 items in
[Open issues 2026-10-06](Open%20issues%202026-10-06.md). Five verifiers then traced each item
through the current uncommitted tree without trusting the "Completed" notes.

The solution builds in Debug with 0 warnings and 0 errors. `build-steam-assets.mjs --check` reports
the asset current, with SHA-256 `DC6A66F5…A93B`, the same hash Codex recorded. No tests were run and
nothing was edited.

| Result                    | Items                                                                                                    |
| ------------------------- | -------------------------------------------------------------------------------------------------------- |
| Fixed                     | 67 of 80                                                                                                 |
| Partial                   | 13: items 7, 33, 40, 45, 46, 56, 75, 76, 77 and 79, plus small leftovers in 23, 25 and 31                |
| New issues from this pass | about 20, including two real regressions (items 1 and 2) and two launch/monitor problems (items 3 and 4) |

Codex's notes overstate items 56, 75, 77 and 79. The "matching toolkit override" behind item 76 has
no effect (item 14).

## Bugs and regressions

1. **Badges go stale after a scan.** (new, regression)
   - **Where:** `GameLibraryService.cs:1811,1844-1846`.
   - **Effect:** the scan writes availability to the store but never calls
     `LibraryBadges.UpdateShortcuts`. The only caller is `ApplyAvailability`
     (`GameLibraryService.Roms.cs:447`), and it used to run through the post-scan monitor recheck
     that the fix for old item 20 removed. A ROM deleted or restored on an always-mounted drive
     shows correctly in the review after a rescan, but the Steam badge keeps the old state until a
     volume event, resume or Apply.
   - **Fix:** publish badges from the scan's availability update.
2. **A confirmed Add is persisted too late.** (new, regression)
   - **Where:** `GameLibraryService.cs:2182-2217`.
   - **Effect:** the `_store.Save(saved)` that ran right after Steam confirmed the write was
     removed. The record is now written only in the `finally`, after `WriteControllerTargetAsync`
     and `ApplyImagesAsync`, and the artwork can wait a long time behind Screenscraper pacing. If
     WSGM is killed in that window, the next scan adopts the shortcut as an orphan, but
     `OwnsProfile` is lost and the controller profile WSGM wrote is never released.
   - **Fix:** restore the immediate save after a confirmed write.
3. **A failed emulator-store load stops all availability rechecks.** (new)
   - **Where:** `ManagedShortcutMonitor.cs:61-66`, `EmulatorManager.cs:92-109`.
   - **Effect:** `Request` drops everything while `_emulators.Initialized` is false. `LoadLocal`
     sets the flag only after a successful read and raises nothing on failure. If `emulators.json`
     can't be read at startup, volume, resume and Apply rechecks stop for every content kind,
     including folders and launchers. Only a successful emulator Refresh lifts it; a successful
     `Mutate` doesn't.
   - **Fix:** gate only the ROM emulator resolution on initialization, not the whole recheck. Set
     the flag on any successful store read or write.
4. **Launches are refused during brief emulator writes.** (new)
   - **Where:** `ManagedContentLaunch.cs:29` takes the gate with `TimeSpan.Zero`.
   - **Effect:** any short hold of the gate refuses the launch with "An emulator operation is in
     progress". That includes a Refresh saving offers, Ignore version, Set preferred, external
     registration, startup recovery, the activation probe, the `InstallationsChanged` and
     `ChoicesChanged` handlers that run inside the mutex, and a second title launched at the same
     moment. Folder and manual titles that use no emulator are refused too.
   - **Fix:** wait a short while, or take the gate only for emulator content and only against
     long-running operations.
5. **WSGM's own shortcut can be treated as user-edited.** (new, low)
   - **Where:** `GameLibraryService.cs:2149-2156`.
   - **Effect:** the unconfirmed-Update reconcile checks only Target and LaunchOptions
     (`CommandShortcut.Same`), then saves the composed StartDirectory. `OwnsRecorded` now requires
     the start directory to match (`ImportPlan.cs:516-521`). If Steam applied the command but not
     the start directory, the next scan treats WSGM's own shortcut as user-edited.
   - **Fix:** include StartDirectory in the reconcile comparison.
6. **Disabling one ROM source freezes the shared system collection.** (item 7, partial)
   - **Where:** `GameLibraryService.cs:2278-2299`; groups are per system (`:2370-2375`).
   - **Effect:** with two ROM sources for the same system, disabling one freezes the `rom-system:`
     collection for the enabled one too, so its new imports are never added.
   - **Fix:** freeze only membership owned by the disabled source.
7. **A re-enabled source's titles can stay failed.** (new, low)
   - **Where:** `GameLibraryArtwork.cs:275-287,308-328`.
   - **Effect:** `ConfigurationChanged` restarts only active titles and records the new signature.
     If a source's title failed or had no provider, and an API key was added or Screenscraper
     enabled while the source was disabled, re-enabling the source doesn't requeue it.
8. **The artwork title list is never pruned.** (new)
   - **Where:** `GameLibraryArtwork.Reset` (`:268-292`).
   - **Effect:** it no longer prunes `_titles`. Titles that drop out of the plan stay in memory and
     keep answering `Options`/`StatusOf`. This came from the fix for old item 13, which
     over-reached.
   - **Fix:** prune by the configured sources, not by the current request set.

## Design leftovers

9. **RetroArch knowledge is still in code.** (item 45, partial)
   - In `EmulatorPackages.cs`:
     - the Libretro bundle folder names "info", "assets", "autoconfig", "database-rdb" and
       "database-cursors" (`:99,250`);
     - the core-database alias table (`:19-39`);
     - special cases for `mgba_libretro`, `gambatte_libretro` and `sameboy_libretro` (`:360-363`);
     - the firmware `system` folder (`:351`);
     - "RetroArch:" progress strings (`:137,252,315`).
   - UI text chosen by `HasCores` ("Installs RetroArch…": `library-import.ts:1329-1333`,
     `EmulatorManagerView.cs:121-122`).
   - The "RetroArch also supports {core}, {data} and {config}" note in the arguments editor
     (`library-import.ts:657`, `GameLibraryView.Arguments.cs:25`).
10. **Buildbot still has hard-coded layout.** (item 46, leftover)
    - Its stable/nightly folder layout is keyed on `Repository == "stable"`, and its errors are
      worded for Libretro (`EmulatorReleases.cs:246-297`).
11. **Leftover guards.** (item 33, leftover)
    - An asset-name "unsafe filename" check that the repair path skips
      (`EmulatorPackages.cs:46-50`).
    - `EmulatorCatalog.Parse` validates its own bundled file with an id regex, file-name-only
      executables and a provider whitelist (`EmulatorCatalog.cs:83-101`).
    - `RecordDefect`'s per-field null checks run on every read, check and compare.
12. **Two messages for one refusal.** (item 40, leftover)
    - "Choose an installed emulator." (`GameLibraryService.Roms.cs:167`) and Resolve's "The selected
      emulator is not installed…" (`EmulatorModels.cs:476-478`).
13. **The offer lookup is still written twice.** (item 56, partial; Codex's note overstates it)
    - **Where:** the `FirstOrDefault` offer lookup at `EmulatorManagerView.cs:113-115` and
      `:184-186`.

## Formatting and repository hygiene

14. **The toolkit Prettier override does nothing.** (new)
    - **Where:** `.prettierrc.json:8-14`.
    - **Effect:** `.prettierignore:43` excludes `external/`, so the override never applies. It also
      contradicts the ignore file's own note that submodules own their formatting.
15. **`library-badge.ts` was reformatted wholesale.** (item 75, partial)
    - **Effect:** the doubled spaces are gone, but the whole file was rewritten in Prettier style,
      so churn on unchanged lines grew. Examples: every `{ok: true}` became `{ ok: true }`,
      `ClassMapTokens`/`RequiredClasses` were split across lines, and `.catch(() => {})` was
      rewritten.
    - **Fix:** revert to the file's HEAD style, and keep only the semantic changes.
16. **`ui-kit.ts` was re-indented wholesale.** (item 76, partial)
    - **Effect:** it is consistent now, but by re-indenting the whole file: 1003 lines changed, 225
      of them non-whitespace. That breaks the toolkit AGENTS.md rule "Avoid unrelated formatting".
17. **Line endings.** (item 77, partial; new)
    - 18 tracked toolkit files show as modified purely from line-ending rewrites.
    - Untracked toolkit files are LF: `SteamUiCefDiagnostics.cs` and
      `SteamUiCefDiagnosticsTests.cs`.
    - `library-import.ts` is CRLF in the working tree, while `.gitattributes` pins it to `eol=lf`
      because the asset hash depends on its bytes.
    - Mixed endings in `WSGM.csproj`, `WSGM.Launch.csproj`, `WSGM.PackagedLaunch.csproj`,
      `WSGM.Tests.csproj` and `OverlayWindow.axaml`.
    - About 40 untracked `.cs` files under `src/` and `tests/` are LF against `.editorconfig` CRLF.
      Git normalizes them on commit, but the Rider cleanup in `eng/verify.ps1` may report a diff.
18. **Doc comments in `EmulatorModels.cs` are still garbled.** (item 79, partial)
    - Glued fragments at lines 92, 110, 125, 137, 143, 161, 260, 263, 270 and 282, for example
      "DefinitionStable identifier…" and "ExpectedLocally computed…".
    - Boilerplate at lines 165, 254, 267, 292, 308 and 327, all reading "X used by emulator
      management and fresh managed launch resolution", which is wrong for `EmulatorOffer`.
19. **Stale doc comments.** (new)
    - `ManagedContentModels.cs:557` mentions "preview diagnostics".
    - `ManagedContentModels.cs:147` says LaunchKind "uses the existing followed-game strategy".
    - `WSGM.Launch/Program.cs:597` lost the comment explaining error 740.
20. **Stray double blank lines.** (new)
    - `RemovableDriveManager.cs:137-138`, `SdFormatManager.cs:144-145`,
      `GameLibraryService.Roms.cs:130-131`.
21. **Emulator page notes are unstyled.** (new)
    - **Where:** `library-import.ts:1165,1332` use `wsgm-import-note`, but no CSS defines that class
      any more.

## Dead and test-only code from this pass

22. **`ImportStateStore.EntriesIfLoaded()` has no caller.** (new)
    - **Where:** `ImportStateStore.cs:155`.
23. **`EmulatorService._choicesRevision` is write-only.** (new)
    - **Where:** `EmulatorService.cs:61,81,241`; `ChoicesRevision` is never read.
24. **An unreachable path in `UpdateAvailability`.** (new)
    - **Where:** `ImportStateStore.cs:106-120,137`.
    - **Effect:** the `expectedContents = null` path is never taken, and `changed |= transition` is
      always true where it sits.
25. **An unused fallback.** (new)
    - **Where:** `GameLibraryArtwork.cs:473-474`; the `?? title.Request.PreferredProviderId`
      fallback is never reached.
26. **Optional parameters and default members used only by tests.** (new; the pattern old item 71
    removed)
    - The four optional parameters on `GameLibraryArtworkRequest` (`GameLibraryArtwork.cs:72-75`).
    - `preferredProviderId = "steamgriddb"` on `FindMatchAsync` (`ArtworkProviders.cs:299`).
    - The default `PauseReason` implementations (`GameLibraryArtwork.cs:86-89`,
      `ArtworkProviders.cs:120`).
27. **Small redundancies.** (new)
    - `ShortcutFolderSource.cs:192,217`: `content?.LaunchKind` is null-safe for a value that is
      always set.
    - `ShortcutRoute.cs:242-246`: `route.IsManaged ||` is redundant.
    - `build-steam-assets.mjs`: the `formatAsset` wrapper is a leftover indirection.
    - `EmulatorNetwork.Https` parses the URL twice (`EmulatorReleases.cs:44-52`).
    - `EmulatorDefinition.Prerequisites` copies `DataPolicy.Prerequisites`.

## Minor efficiency leftovers

28. **Rechecks copy too much.** (item 31, leftover)
    - `RecheckAvailabilityAsync` copies every entry even for a single-id recheck, and
      `RecheckShortcutAsync` copies all of them twice.
29. **Launcher captures aren't cached per volume.** (item 25, leftover)
    - Each launcher game still makes one `CapturePath` call (two volume API calls).
30. **The uncached `ResolvePath` still allocates.** (item 23, leftover)
    - It still allocates strings: `new string(mounts).Split().OrderBy()` and a `new string` per
      candidate. That path isn't hot.
31. **A discovery failure message is hidden.** (new, cosmetic)
    - `DiscoverExternalAsync`'s failure status is overwritten straight away by "Emulator versions
      checked." (`EmulatorManager.cs:206`).

## Outside this review

- The toolkit working tree now carries changes this review never covered: `SteamUiCefDiagnostics`,
  `PersistentSteamUiTransport`, `SteamUiCdpConnection`, `SteamUiModuleResolver`,
  `module-resolver.ts`, several `eng/check-*.mjs` and gate files, and their tests. They look like a
  separate task sharing the same submodule checkout. Keep them apart when committing.
- Codex reports building `WSGM-Setup-2.1.0.exe` and deploying to the live machine. Neither was
  verified here.
