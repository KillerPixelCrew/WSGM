# Game library, sources, import, artwork and launch command findings

Scope: `Shell/GameLibraryService.cs`, `Shell/GameLibraryArtwork.cs`, `Shell/SteamArtworkBrowserSource.cs`, `Shell/ArtworkStateStore.cs`, `Shell/RunningApplicationTarget.cs`, `Shell/ShellSession.GameLibrary.cs` and the library composition in `ShellSession.cs`, `Shell/SteamLibraryImportSurface.cs`, everything under `Core/Library/**` and `Core/Artwork/**`, `Core/LaunchWrapperCommand.cs`, `Core/PackagedLaunchCommand.cs` (also compiled into `WSGM.PackagedLaunch`), `Core/LibraryFilter.cs`, `Core/SteamLibraryVdf.cs`, `Interop/ShellLink.cs` and `Interop/NativePackageSource.cs`. Baseline `master` 1329813f.

Sources: the domain review (LIBRARY-001..040), its adversarial verification (corrections plus the missed LIBRARY-V-001..007), the completeness critic and plan v2. Where plan v2 changed a recommendation, plan v2 wins; the maintainer's decisions of 2026-10-03 (`../DECISIONS.md`) override both. Line numbers in the review were often wrong; the anchors below were rechecked against the code, but implementers anchor by symbol.

Counts (after verifier and solution-checker corrections): 1 high, 9 medium, 22 low, 11 nit as sections; 4 ids are no-change (LIBRARY-033 moved there as dropped security hardening). The solution checker added no new id: the AUMID cap it found is folded into LIBRARY-032, and Ubisoft's `MiniYaml.MaximumDepth` (64) is a recursion guard below which no field discovery reads ever sits (the deepest read is `root.start_game.<mode>.executables[0].path.relative`), so it drops nothing that matters and stays. The review's plan-claim checks C1..C22 are not findings; their substance is folded in (C9 into the test filter, C14 into LIBRARY-035, C16/C18 into LIBRARY-008, C21 into LIBRARY-025).

Plan v2 batches for this area: B014 (golden tests), B015 (correctness), B102 (artwork providers), B103 (artwork browser), B104 (pure extractions), B105 (workers, port, threading, lifetime), B106 (one title per shortcut, D5 decided), B107 (sources and helpers). Other domains carry five items: B037 (state roots), B038 (config caps), B039 (sidecar read rules), B053 (toolkit outcomes), B059 (picker), B097 (storage), B173 (linked sources, D3 decided: Device Lab becomes GPL).

"The library filter" below means:

```
dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"
```

Batches that move code without a visible change (B104, B105) also run `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~GameLibrary"`.

## High

### LIBRARY-001: Imported titles pin their RTSS per-app profile to WSGM.PackagedLaunch.exe

- **Severity:** high
- **Where:** `src/WSGM/Shell/RunningApplicationTarget.cs:539-547` (`SteamRunningAppPairing.NormalizeShortcutTarget`), `:399-410` (`Project`), `:195-200` (`ApplyForeground`); `src/WSGM/Core/Library/PackagedLauncherShortcut.cs:23` (`ExecutableName`); `src/WSGM/Core/LaunchWrapperCommand.cs:53` (`HelperFileName`); `src/WSGM/Core/Library/ShortcutRoute.cs:197-199` (follow routes); `tests/WSGM.Tests/Shell/RunningApplicationTargetTests.cs:134-147`.
- **Problem:** `NormalizeShortcutTarget` treats a shortcut target as untruthful only when the file name starts with `WSGM.Launch`. Every imported Xbox title and every followed launcher route targets `WSGM.PackagedLaunch.exe`, which does not match. `Project` then reports the Steam state as `Active` with `RtssProfileName = "WSGM.PackagedLaunch.exe"`, and `ApplyForeground` never revisits an `Active` state, so the real game process never takes the pairing. Per-game RTSS limits and profiles silently target the launcher for the whole import feature. With RTSS now always running (USER-001), every imported title is affected.
- **Best solution:** Make `LaunchWrapperCommand.HelperFileName` `internal const`. In `NormalizeShortcutTarget`, replace the `StartsWith("WSGM.Launch")` test with an exact, case-insensitive file-name comparison against both constants (`LaunchWrapperCommand.HelperFileName`, `PackagedLauncherShortcut.ExecutableName`), through one private `IsWsgmHelper(string fileName)` in the same class. A helper target then returns the existing "not a truthful RTSS application profile" diagnostic, the title stays `IdentityOnly`, and the existing shortcut foreground fill (`RunningApplicationTarget.cs:232-242`) pairs the real game. No new heuristic. Exact names beat the prefix because the prefix is the bug: it matched by accident for one helper and missed the other.
- **Tests:** add `...\WSGM.PackagedLaunch.exe` (and a quoted variant) to the `UntruthfulShortcutTargetsNeverBecomeRtssProfiles` theory; add a projection case where a Steam shortcut targeting PackagedLaunch stays `IdentityOnly` and the foreground fill pairs the game executable. Filter `FullyQualifiedName~RunningApplicationTarget|FullyQualifiedName~ForegroundApplicationFilter`, then the library filter. Attended check: M01-30 plus new M01-43 (RTSS overlay at game start).
- **Plan v2:** B015 step 1.
- **Related:** LIBRARY-027 (name part lands here), USER-001 / B005 (RTSS always running). New, no ledger id.

## Medium

### LIBRARY-002: Two sources can adopt the same Steam shortcut

- **Severity:** medium
- **Where:** `src/WSGM/Core/Library/ImportPlan.cs:312-315` (`claimed` built once from records), `:481-491` (packaged adoption through `context.Unclaimed`), `:550-556` (command-route adoption), `:612-614` (`Context.Unclaimed` computed once in the constructor), `:343-383` (removal of a source's records); `src/WSGM/Shell/GameLibraryService.cs:2036-2038` (`Revalidate` accepts both).
- **Problem:** `Context.Unclaimed` is fixed before the discovery loop, so a shortcut adopted by one title is still offered to the next. A GOG game and a `.lnk` to the same exe in a shortcuts folder both plan `Adopt` of one AppId; both records name it, and removing either source later deletes the shortcut the other still claims.
- **Best solution:** Decided D5: the second title is offered as a second game shortcut, its own Add that creates its own Steam shortcut (no new row type, never two records on one AppId). It is offered unticked, so making the second entry is the user's choice. In `ImportPlan.Build`: keep `claimed` as a `HashSet<uint>` owned by `Context`, add `Context.Claim(uint appId)`, and turn `Unclaimed` into a computed filter (`existing.Where(s => !claimed.Contains(s.AppId))`). After each `Describe`/`DescribeCommand` result with `Action == Adopt`, call `context.Claim(entry.AppId)`; first title in discovery order wins. In both describe paths, after the unclaimed lookup finds nothing, check whether a claimed shortcut would have matched (same `PackagedLauncherShortcut.Owns` or `CommandShortcut.RouteOf` predicate over `existing` minus `Unclaimed`); if so return `Add` with `preselect: false` and a new reason constant, for example "Another imported title already uses Steam's entry for this game; adding it makes a separate entry." That applies equally to shortcuts claimed by an existing record, which today come out as a preselected Add. The final reason wording is the maintainer's.
- **Tests:** `ImportPlanTests`: two discovered titles whose routes match one existing shortcut give exactly one `Adopt` and one unselected `Add` with the new reason; a title whose route matches a shortcut another record owns gets an unselected `Add`; order decides the winner. Filter `FullyQualifiedName~ImportPlan`, then the library filter.
- **Plan v2:** B106; decided D5: offered as its own Add that creates its own Steam shortcut.
- **Related:** LIBRARY-V-001 (same rule on the apply side, must land together); LIBRARY-007 (B105 lands first).

### LIBRARY-003: Import records silently dropped by arbitrary length caps and then overwritten

- **Severity:** medium
- **Where:** `src/WSGM/Core/Library/ImportStateStore.cs:374-438` (`Sanitize`), `:293-302` (`Mutate` writes the sanitized state back), `:378` (stale comment); `src/WSGM/Core/Library/Sources/ShortcutFolderSource.cs:169` (relative-path keys); `tests/WSGM.Tests/Core/ImportStateStoreTests.cs:34-48` (`AnOversizedOrUnknownRecordIsDropped`).
- **Problem:** `Sanitize` drops entries with `Source.Length > 32` or `Key.Length > 512`, and entries with an unknown `Mode`, without a log line. The next `Mutate` persists the loss. This file is the only memory of which shortcuts are WSGM's; shortcut-folder keys are relative paths that can exceed 512 characters, so such titles lose ownership permanently. Violates no-arbitrary-limits.
- **Best solution:** In `Sanitize`, change the entry predicate to `Source.Length: > 0`, `Key.Length: > 0` (identity must be non-empty) plus the existing non-null checks and known `Mode`. Log every dropped entry, choice and collection with its reason (`Log.Warn("Library import record <source>/<key> dropped: <reason>")`); a load is not a high-rate path. Keep the `Version > CurrentVersion` refusal and the parse quarantine. Replace the comment at `:378` (LIBRARY-031). Records already dropped cannot be restored; the next scan's Adopt recovers packaged and exact-route titles.
- **Tests:** rewrite `AnOversizedOrUnknownRecordIsDropped` into two cases: a 2,000-character key and a 64-character source survive a write/reload round trip; an unknown mode and a blank key are still dropped. Filter `FullyQualifiedName~ImportStateStore`.
- **Plan v2:** B015 step 2.
- **Related:** LIBRARY-031; config sidecar rules B039 (the store's existing quarantine already matches them).

### LIBRARY-004: An uncertain shortcut update can strand the title in Conflict

- **Severity:** medium (verifier replaced the recommendation)
- **Where:** `src/WSGM/Shell/ShellSession.cs:810-818` (update delegate reduces `SetShortcutLaunchAsync` to `.Succeeded`); `src/WSGM/Core/Library/SteamShortcutWriter.cs:83-85` (`UpdateAsync` reports "Steam did not accept"); `src/WSGM/Shell/GameLibraryService.cs:1820-1834` (`ApplyCoreAsync`, unconfirmed branch keeps the old record); `src/WSGM/Core/Library/ImportPlan.cs:465-468, 526-529` (`OwnsRecorded` false gives `Conflict`); toolkit `SteamApps.cs:268-273` (three sequential setter calls).
- **Problem:** A timed-out eval after dispatch returns `Reachable=false` and becomes a plain refusal. The run keeps the old record and stops. If the write did land, the next scan sees live fields that differ from the record and plans `Conflict` ("changed by hand"), which is neither selectable nor editable. The update script also runs three calls in sequence, so a failure part-way leaves a new Target with old arguments, which ends in the same place. Recovery today is deleting the shortcut in Steam by hand.
- **Best solution:** In `ApplyCoreAsync`, in the `!result.Confirmed` branch for any non-Add action, re-read the shortcut once through the existing `_readShortcut` seam before stopping. If it returns a shortcut whose `Target` and `LaunchOptions` equal the composed `fields` exactly (the same comparison `OwnsRecorded` uses), save `saved` with `ConfirmedUtc` set to now; otherwise keep the old record. In both cases call `Fail(...)` and stop the run exactly as today, with no retry of the write. A cancelled or failing re-read counts as "no match". `OwnsRecorded` stays an exact comparison; the earlier idea of treating "equals what WSGM would compose now" as owned is dropped because it blurs the exact-ownership rule (`ImportStateStore.cs:63-66`). The partial-script case (new Target, old arguments) still ends in Conflict and stays recoverable by deleting the shortcut; it is not worth more mechanism. In B053 the toolkit's typed outcome replaces the bool: `Applied` is confirmed, `Rejected` and `Unknown` both go through this same branch (critic conflict 5: the outcome is `Unknown`, not `DispatchedUnknown`).
- **Tests:** `GameLibraryServiceTests`: an unconfirmed update whose re-read matches the composed fields records them and the next scan plans `Skip`; an unconfirmed update whose re-read shows the old fields keeps the old record; in both the run stops with "nothing was retried" and the writer was called once. Filter `FullyQualifiedName~GameLibraryService|FullyQualifiedName~SteamShortcutWriter`.
- **Plan v2:** B015 step 5 (re-read rule); B053 (typed outcome mapping). D9 does not reach this: it removes readback machinery from device writes, and this is one read of Steam's own shortcut through a seam the library already has, with no retry and no waiting state.
- **Related:** U02B-SUTC-008/012 (toolkit client outcome), TOOLKITCS-B3, LIBRARY-V-005 (the Add counterpart), critic conflict 5.

### LIBRARY-005: Library command exceptions quarantine the whole Steam module

- **Severity:** medium
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:571` (`SetSourceEnabledAsync`), `:626` (`SetCollectionsAsync`), `:732` (`AddFolderAsync`), `:788` (`RemoveFolderAsync`), `:462-499` (`OpenArtworkAsync`); `src/WSGM/Shell/ShellSession.Config.cs:31-42` (`CommitWsgmSetting`); `src/WSGM/Core/ConfigStore.cs` (`Mutate`, mutex acquire throws `TimeoutException`); `src/WSGM/Shell/ArtworkStateStore.cs:101-111` (`Read` dereferences `link.ProviderId.Length`, `filter.Tab.Length`); toolkit `SteamUiModuleRuntime.RespondAsync/FailModule`.
- **Problem:** The four settings commands call `_updateSettings`, which throws on an unreadable config, a parse failure or a busy config mutex. `OpenArtworkAsync` reaches `ArtworkStateStore.FindGame`, whose `Read` throws `NullReferenceException` on a JSON null in `ProviderId`, `GameId` or `Tab`. Any exception escaping a command makes the toolkit call `FailModule`, and every later Game Library command is refused for the session. Overlay calls are only logged.
- **Best solution:** Add one private helper in `GameLibraryService`, `SteamUiCommandResult? CommitSettings(Action<GameLibraryConfig> change)`, which calls `_updateSettings` and catches `IOException`, `UnauthorizedAccessException`, `JsonException` and `TimeoutException` (after B039 this becomes the single `ConfigUnavailableException`), logs the exception and returns `new SteamUiCommandResult(false, "WSGM's settings could not be saved, so nothing was changed.")`. The four commands return that refusal before touching any state. In `ArtworkStateStore.Read`, rewrite the two `Where` predicates as property patterns (`link is { AppId: not 0, ProviderId.Length: > 0, GameId.Length: > 0, Name: not null }`, `filter is { Tab.Length: > 0 }`), which are null-safe. That fixes the cause rather than wrapping `OpenArtworkAsync` in a catch-all, which would hide real bugs. Add `ArtworkStateStore.cs` to B015's file list; B039 later adds the read-outcome rules on top.
- **Tests:** `GameLibraryServiceTests`: a throwing `updateSettings` delegate makes each of the four commands return a refusal, leaves the published state unchanged and throws nothing. New `ArtworkStateStoreTests`: an `artwork.json` with `"ProviderId": null` and `"Tab": null` loads with those rows dropped. Filter `FullyQualifiedName~GameLibraryService|FullyQualifiedName~ArtworkStateStore`.
- **Plan v2:** B015 step 3.
- **Related:** CONFIG-V-001 and CONFIG-V-005 (B039: the strict-path exception type and null-tolerant sidecar checks), LIBRARY-013.

### LIBRARY-006: Disk IO and config commits run on the caller's thread and under the service lock

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/ServiceSubView.cs:227-239` (`Run` invokes the backend synchronously on the UI thread); `src/WSGM/Shell/GameLibraryService.cs:1199-1206, 1257-1264` (`EditEntry`/`EditMany` save choices under `_gate`); `src/WSGM/Core/Library/ImportStateStore.cs:451-455` (durable write); `src/WSGM/Shell/SteamArtworkBrowserSource.cs:139-153` (`SelectTabAsync` calls `Managed` under `_gate`), `:1144-1192` (`Managed`, `DataUrl`), and `BroadcastArtwork`.
- **Problem:** Overlay commands run the backend's synchronous prefix on the Avalonia thread, so durable `library-import.json` writes, config commits and `Directory.Exists` probes block the UI. In the artwork browser, `Managed` scans every Steam account's grid folder and base64-reads up to five files of up to 16 MiB while holding `_gate`, on every tab switch, load completion, outcome publish and once per open context.
- **Best solution:** Two parts, no new ordering mechanism. (1) Library (B105): keep choice persistence under `_gate`, so edit order stays write order (moving it outside, as the review first proposed, lets two surfaces' edits reach `SaveChoice` in the opposite order). In `ServiceSubView`, start the backend call on a worker in both places that call a backend: `Run` (`var result = await Task.Run(() => operation(CancellationToken.None));`) and `ConfirmCommand`'s `CommitAsync` (`await Task.Run(() => command(CancellationToken.None))`). The `await` resumes on the UI context, so the existing `Toast`, `Back()` and navigation-generation check stay on the UI thread unchanged. The five subclasses' backends (`GameLibraryView` → `GameLibraryService`, `ArtworkView` → `SteamArtworkBrowserSource`, `ThemesView`, `SoundsView`, `AnimationsView` → their services) are already called from bridge request threads by the Steam surfaces, so none needs the UI thread; confirm that by reading each before landing. (2) Artwork browser (B103): compute `Managed(appId)` before taking `_gate` (read `appId` in a short lock first, then compute, then assign `ManagedSlots` under the lock only if `_state?.AppId` still equals that `appId`; managed slots depend only on the app id and the disk, so no generation check is needed). Do the same in `BroadcastArtwork` and the outcome publishers. `DataUrl` takes the MIME type from `SteamArtwork.ImageFormat` on the bytes and the size bound from `ArtworkDownload.MaximumBytes` (LIBRARY-016), and logs an oversized file instead of returning null silently. File URLs or thumbnails would be a UI change and are not proposed.
- **Tests:** B103 `SteamArtworkBrowserSourceTests` with a temp grid folder: select tab while another thread holds a `Changed` subscriber that reads state does not block; managed slots for an `.ico` and a JPEG get the right MIME. B105: a ServiceSubView test (or a `GameLibraryServiceTests` case) that the backend of both `Run` and `ConfirmCommand` runs off the calling thread. Library filter plus the UI capture filter.
- **Plan v2:** B103 (artwork half), B105 (library half, with library.verify batch problem 5).
- **Related:** LIBRARY-016, LIBRARY-V-002, LIBRARY-020.

### LIBRARY-008: Fixed 5 s blocking dispose and untracked background tasks

- **Severity:** medium (verifier narrowed one sub-claim)
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:51` (`DisposeWait`), `:217-253` (`Dispose`: `running.Wait(DisposeWait)`, then `_shutdown.Dispose()`), `:641-660` (collection sync started by `SetCollectionsAsync` reads `_shutdown.Token`), `:1122-1138` (`Start` fire-and-forget), `:2370-2410` (`Run`, `_ = previous`); `src/WSGM/Shell/ShellSession.Shutdown.cs:504` (call site).
- **Problem:** `Dispose` blocks the async session shutdown for up to 5 s, the whole SessionEnd budget. The collection sync task is untracked and reads `_shutdown.Token` after `_shutdown.Dispose()`, which throws `ObjectDisposedException`. A superseded scan keeps running only until its next cancellation check (`DetectSources` and `_readPrograms` take no token); it is read-only, so that part is harmless.
- **Best solution:** Follow the owner contract of plan v2 section 2 and critic conflict 1 (`CloseAdmission()` plus `StopAsync(Deadline)`, the SDK `Deadline` type `CommonPluginManager.StopAsync` already takes). Replace `Dispose` with `CloseAdmission()` (synchronous, idempotent: sets `_disposed` under `_gate` so `Guard()` answers `ShuttingDown`, unsubscribes `OnArtworkChanged`, cancels `_shutdown`) and `Task StopAsync(Deadline deadline)` (calls `CloseAdmission`, then with `using var stop = deadline.CreateCancellationSource();` awaits `_running.WaitAsync(stop.Token)`, then `_collectionSync.WaitAsync(stop.Token)`; acquiring the semaphore proves no collection sync is mid-write, and it is never released because admission is closed; an `OperationCanceledException` from `stop` is caught and the method returns). On deadline it returns and leaves work running: an apply mid-write still records its result because `ImportStateStore` is not disposed. The final stop never disposes `_shutdown` or `_work`, which removes the `ObjectDisposedException` class entirely; `Begin` keeps disposing the `_work` it replaces, because that unregisters the linked source from `_shutdown` and the running task already holds its token. `Start`'s detection is read-only and already checks `_disposed` under the lock, so it needs no tracking. Delete `DisposeWait` and `_ = previous`. This tracks exactly the two things that write (the serialized apply and the collection sync) without a task set. In `ShellSession.Shutdown.cs`, replace `_libraryImport?.Dispose()` with `await _libraryImport.StopAsync(Deadline.At(deadline)).ConfigureAwait(false)` (the `ShutdownAsync` parameter, as the common-plugin step does). Do not pass `_shutdownCancellation.Token`: it is cancelled at the top of `ShutdownAsync`, so the apply's record would never be waited for. B140 later moves the call into its ordered step list.
- **Tests:** `GameLibraryServiceTests`: `StopAsync` during an apply blocked inside the writer returns at the deadline, and when the writer later completes, the record is still saved; `StopAsync` with an ample deadline waits for that write and returns after the record is saved; `SetCollectionsAsync(true)` immediately followed by `StopAsync` throws nothing; commands after `CloseAdmission` answer `ShuttingDown`. Filter `FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B105 (plan text calls it `DisposeAsync(deadline)`; use the `CloseAdmission`/`StopAsync` names of section 2). Session wiring finishes in B140 (decided D1: safety-first ordered steps under one deadline).
- **Related:** critic conflict 1, plan claims C16/C18, LIBRARY-019 (`_ = previous`), LIBRARY-012 (same CTS rule in the browser).

### LIBRARY-010: Artwork providers are process globals and the artwork page is untestable

- **Severity:** medium
- **Where:** `src/WSGM/Core/Artwork/ArtworkProviders.cs:243-244` (`ArtworkSearch.Providers`); `src/WSGM/Core/Artwork/SteamGridDb.cs:107-109` (static gate and `HttpClient`); `src/WSGM/Core/Artwork/ArtworkProviderImplementations.cs:65-114, 173, 190` (`SteamGridDbProvider` adapter, Screenscraper static gate and client); `src/WSGM/Core/Artwork/ArtworkDownload.cs:30`; `src/WSGM/Shell/SteamArtworkBrowserSource.cs` (static calls into `ArtworkSearch`, `SteamGridDb`, `SteamArtwork`, `SteamApps`, `SteamLibraryData`, `OverlayLibraryLookup`; no tests).
- **Problem:** Provider HTTP clients and request gates are static, tests share them (cross-test cache leakage), SteamGridDB has a static class plus a thin adapter that duplicates every method, and the artwork browser cannot be tested without live providers.
- **Best solution:** The smaller form from library.verify batch problem 8, adopted by plan v2. Provider classes keep their shape. `SteamGridDbProvider` and `ScreenscraperProvider` take `HttpMessageHandler? handler = null` and `ArtworkRequestGate? gate = null` constructor arguments; null means today's values, so production composition is unchanged. Fold the static `SteamGridDb` class into `SteamGridDbProvider` (search, assets, official assets, key resolution, cache reset become instance members; `IsTransient`, `RetryAfter` and `ImageExtension` stay `internal static` on it); `SgdbAsset`, `SgdbGame`, `SgdbOfficialAsset` and `SteamGridDbException` move with it. `ArtworkSearch` stays static but each search method gets an internal overload taking `IReadOnlyList<IArtworkProvider>`; the public methods pass `Providers`. The browser calls the static class directly in `LoadOfficialAssetsAsync` (`SteamGridDb.ResolveKey`, `GetOfficialAssetsForGameAsync`, `GetOfficialAssetsForSteamAppAsync`), so folding the class edits `SteamArtworkBrowserSource.cs` in B102 as well (add it to B102's file list): `SteamArtworkBrowserSource` takes `IReadOnlyList<IArtworkProvider>` (default `ArtworkSearch.Providers`) as an internal constructor argument, passes it to the `ArtworkSearch` overloads, and reaches the official-asset calls through the `SteamGridDbProvider` in that list (`providers.OfType<SteamGridDbProvider>().FirstOrDefault()`, none means no official assets, as an empty key does today). For B103's temp grid folder, `SteamArtwork.FindCustomArtFile` gets an internal overload taking the Steam root directory (the public one passes `Path.GetDirectoryName(Steam.ExePath)`), and the browser takes a `Func<string?>` Steam root (default the real one) that its `Managed` lookup passes through; that seam lands in B103 with the tests that use it. `ArtworkDownload`'s static client stays: no test needs it. `ArtworkRequestGate.Background()` stays. This beats the review's full instance `ArtworkProviders`/`SteamArtworkWriter` conversion (about 1,300 lines) because only the test seams are needed.
- **Tests:** replace `ArtworkProviderTests.BothProvidersAreDeclaredAndFindableByTheirOwnIds` with parsing tests for SteamGridDB search, assets and official assets and Screenscraper search and media over a fake `HttpMessageHandler`; existing gate and retry tests use per-test gate instances. Filter `FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb`, then the library filter.
- **Plan v2:** B102 (add `src/WSGM/Shell/SteamArtworkBrowserSource.cs` to its file list for the provider argument and the three official-asset call sites); the Steam-root seam lands in B103.
- **Related:** PV11-005 (process statics), LIBRARY-011, LIBRARY-015, LIBRARY-016, LIBRARY-037, LIBRARY-039.

### LIBRARY-V-001: An apply can make two titles own one Steam shortcut, with no adoption involved

- **Severity:** medium (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:2009-2019` (`Revalidate`, Add branch searches all of `existing`), `:1840-1841` (this run's writes are folded into `existing`); `src/WSGM/Core/Library/Sources/ShortcutFolderSource.cs:225-243` (`Direct` composes the same Target and arguments); `src/WSGM/Core/Library/ImportPlan.cs:368-382`.
- **Problem:** The Add branch of `Revalidate` turns an Add into an Update of any existing shortcut that `ImportPlan.Owns` accepts, including one another title's record already claims, bypassing the claimed rule `ImportPlan.Build` applies. A GOG title and a Start-menu `.lnk` to the same exe both show as preselected "New". Applying adds the shortcut for the first, folds it into `existing`, then rewrites it for the second: the second title's start directory overwrites the first's and both records name one AppId. Removing either source later deletes the shortcut the other still claims. This needs no user action, so it is more likely than LIBRARY-002.
- **Best solution:** Give `Revalidate` the set of AppIds claimed by other titles' records. In `ApplyCoreAsync`, compute it per entry from the `records` dictionary the loop already keeps current (`records.Where(r => !ImportPlan.Identity.Equals(r.Key, identity) && r.Value.AppId > 0).Select(r => r.Value.AppId)`), and in the Add branch search only `existing` shortcuts not in that set. The second title then stays an Add and gets its own shortcut, which is D5's answer (its own entry, never two records on one AppId). No new state.
- **Tests:** `GameLibraryServiceTests`: a GOG title and a folder `.lnk` composing the same command, both selected, end with two shortcuts and two records on different AppIds; the first title's start directory is unchanged. Filter `FullyQualifiedName~GameLibraryService|FullyQualifiedName~ImportPlan`.
- **Plan v2:** B106; decided D5: the second title gets its own Add and its own Steam shortcut (the method lives in `GameLibraryApply` after B105).
- **Related:** LIBRARY-002.

### LIBRARY-V-002: Every config.json reload resets the open artwork pages and wipes all provider caches

- **Severity:** medium (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/SteamArtworkBrowserSource.cs:650-677` (`ConfigurationChanged`); `src/WSGM/Shell/ShellSession.Config.cs:144` (called on every reload, on the UI thread); `src/WSGM/Shell/GameLibraryArtwork.cs:110-115, 285-312` (`Signature` rule the browser does not use).
- **Problem:** Any WSGM setting commit (a library source toggle, theme, sound pack, Quick Access change) reloads config. Each reload calls `ArtworkSearch.ResetCaches()` once per open context plus once for the parent, dropping SteamGridDB and Screenscraper answers and remembered failures, which spends Screenscraper's daily allowance again. It also calls `OpenAsync` for the open app, discarding the user's tab, page, filter results, game search and selection, and that reopen runs on the dispatcher, including `ArtworkStateStore` reads and `Managed()`.
- **Best solution:** Only the parent handles reloads. Add `internal string ProviderSignature()` to `ArtworkConfig` (credentials: the four values `GameLibraryArtwork.Signature` joins today; switch `ArtworkSearchProviders.Signature` to call it) and `TabSignature()` (`DefaultTab`, `TabOrder`, the six `Show*` flags). The parent keeps the two last-seen strings, initialised from `_readConfiguration()` in its constructor (otherwise the first reload always looks like a change); children never read them. On `ConfigurationChanged`: if neither changed, return. If the provider signature changed, call `ArtworkSearch.ResetCaches()` once. If either changed, reopen the parent and every child whose `_state` is non-null (its app id read under that context's own `_gate`) with `_ = Task.Run(() => context.OpenAsync(appId, context._shutdown.Token))`, off the dispatcher. Children no longer recurse into `ConfigurationChanged`. Two strings of state are justified by the concrete defect; it reuses the existing signature rule instead of inventing one.
- **Tests:** `SteamArtworkBrowserSourceTests`: a reload with an unchanged artwork section keeps the active tab, page and candidates and does not reset caches; a changed API key resets caches once and reopens; a changed tab order reopens without a cache reset. Filter `FullyQualifiedName~SteamArtworkBrowserSource|FullyQualifiedName~GameLibraryArtwork`.
- **Plan v2:** B103.
- **Related:** LIBRARY-012 (repeated reset), LIBRARY-006 (dispatcher IO).

## Low

### LIBRARY-007: Store records are shared mutable objects across the store boundary

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Core/Library/ImportStateStore.cs:104-110` (`Entries()` returns cached objects), `:304-313` (shallow `Copy`), `:292` (documented invariant); `src/WSGM/Shell/GameLibraryService.cs:1795-1796` (artwork-only path uses the store's own record), `:1852, :1869, :1873` (mutations after save).
- **Problem:** `ApplyCoreAsync` mutates objects already inside the store's `_state` (`OwnsProfile`, `ArtworkApplied`). That breaks "a write that fails leaves the remembered state as the file still has it", and a later unrelated write persists the unsaved mutation. The persisted values are facts about Steam that already happened, so no wrong state reaches disk today; it is an invariant breach.
- **Best solution:** Copy at the store boundary, without changing `ImportedEntry` into a record (a record would switch `Equals` to value semantics on a type held in dictionaries and lists). Add `internal ImportedEntry Copy() => (ImportedEntry)MemberwiseClone();` (all members are strings, ints and bools, so a shallow clone is a full copy). `Entries()` returns copies, and `Save`/`SaveApplied` store a copy of their argument. `ApplyCoreAsync` keeps working on its own object.
- **Tests:** `ImportStateStoreTests`: mutating an entry returned by `Entries()` or passed to `Save` does not change what a second `Entries()` returns; a `Save` whose write fails leaves `Entries()` equal to the file. Filter `FullyQualifiedName~ImportStateStore|FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B105 (moved out of the correctness batch, library.verify batch problem 4).
- **Related:** none.

### LIBRARY-009: Library writes the live config object from a non-UI thread

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Shell/ShellSession.cs:820-835` (`updateSettings` assigns `_config.GameLibrary = persisted.GameLibrary`; `settings: () => _config.GameLibrary` and the `DefaultMode`/`ImportUnroutable` readers); `src/WSGM/Shell/GameLibraryService.cs` (`_settings()` call sites).
- **Problem:** The commit callback writes the shared `_config` from whichever thread ran the command, while config reload replaces `_config` on the UI thread. With two commits from two surfaces and a reload between them, the assignment can land on the old instance or overwrite the reload's section.
- **Best solution:** The service owns a `GameLibraryConfig` snapshot field, read and replaced under `_gate`. The constructor takes the initial section and a commit delegate `Func<Action<GameLibraryConfig>, GameLibraryConfig>` that returns the persisted section (`change => CommitWsgmSetting(c => change(c.GameLibrary), false).GameLibrary`); after a successful commit the service adopts the returned section. `ConfigurationChanged(GameLibraryConfig section)` replaces the snapshot on reload. The separate `DefaultMode` and `ImportUnroutable` delegates are deleted (read from the snapshot). Delete the `_config.GameLibrary =` assignment and its comment. This is part of collapsing the 18 constructor parameters into a Steam port plus settings in B105.
- **Tests:** `GameLibraryServiceTests`: after `SetSourceEnabledAsync`, the next scan uses the committed section without any reload; a `ConfigurationChanged` section replaces it. Filter `FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B105.
- **Related:** config reloader B116, plan L46/L64.

### LIBRARY-011: Artwork page stops paging early

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamArtworkBrowserSource.cs:870-873, 955` (`HasMore = candidates.Length == 50 && mapped.Count > 0` after `ImageHeader.IsWithinLimits` filtering); `src/WSGM/Core/Artwork/ArtworkProviders.cs:199` (`ArtworkSearchResult`).
- **Problem:** `HasMore` is computed after the size filter and URL de-duplication, so any page where one candidate is filtered out ends paging. 50 is SteamGridDB's page size hard-coded in the consumer, and results are merged across providers.
- **Best solution:** The provider says whether it has more. The filtered query methods (the only ones left after LIBRARY-015) return `ArtworkPage(IReadOnlyList<ArtworkCandidate> Candidates, bool HasMore)`. `SteamGridDbProvider` sets `HasMore = rawPage.Count == PageSize` (its own constant, before any filtering); Screenscraper returns `HasMore = false`. `ArtworkSearchResult` gains `bool HasMore` (true when any provider's page had more). The browser sets `HasMore = fetched.HasMore`. Done in B102 so the provider seam changes once.
- **Tests:** browser or `ArtworkProviderTests`: a full SteamGridDB page with one oversized candidate still reports `HasMore`; a short page does not; Screenscraper never does. Filter `FullyQualifiedName~Artwork`.
- **Plan v2:** B102 (moved from the correctness batch, library.verify batch problem 1).
- **Related:** LIBRARY-010, LIBRARY-015.

### LIBRARY-012: Artwork browser background work and disposal

- **Severity:** low
- **Where:** `src/WSGM/Shell/SteamArtworkBrowserSource.cs:81-112` (`Dispose` cancels and disposes `_load` and `_shutdown`), fire-and-forget starts at `:158, 184, 210, 242, 273, 306, 330, 373, 381, 434, 453, 471, 565, 675`, `:134` (`SelectTabAsync` links the request token).
- **Problem:** Any call or still-running task that reads `_shutdown.Token` after `Dispose` throws `ObjectDisposedException`. Only `SelectTabAsync` links the bridge request token into its load; the runtime disposes rather than cancels request sources, so that link does nothing useful and makes the token policy inconsistent.
- **Best solution:** Remove mechanism, no tracked task set and no disposed flag at each entry (the tasks only publish page state). In `Dispose`, cancel `_shutdown` and `_load` and set `_state = null` under `_gate`, but delete `_load?.Dispose()` and `_shutdown.Dispose()`; a cancelled, undisposed CTS is safe to read from any later call, and newly linked loads start cancelled. With `_state` null, every command already refuses through its existing "No artwork page is open" / "no longer current" branch, a late load drops its result at its existing `_state is null` check, and a parent's `ConfigurationChanged` finds no app to reopen. `SelectTabAsync` and `OpenAsync` keep disposing the `_load` they replace. Every load links only `_shutdown.Token` (drop the request token in `SelectTabAsync`), the same rule the critic gives `ThemeBrowseSession` (CRIT-006). The once-per-context cache reset is fixed by LIBRARY-V-002.
- **Tests:** `SteamArtworkBrowserSourceTests`: dispose during a load, then `SelectTabAsync`, `LoadMoreAsync` and `ConfigurationChanged` throw nothing and publish nothing new. Filter `FullyQualifiedName~SteamArtworkBrowserSource`.
- **Plan v2:** B103 (library.verify batch problem 6).
- **Related:** CRIT-006, LIBRARY-008, LIBRARY-V-002.

### LIBRARY-013: ArtworkStateStore loses saved links on a bad read

- **Severity:** low (verifier replaced the recommendation)
- **Where:** `src/WSGM/Shell/ArtworkStateStore.cs:79-112` (`Read` caches empty state after an IO or parse failure), `:123-142` (`Write` overwrites).
- **Problem:** An unreadable or unparsable `artwork.json` is logged and replaced by empty state; the next `SaveFilter`/`SaveGame` overwrites the file and the user's saved matches and filters are gone.
- **Best solution:** Copy `ImportStateStore`'s two existing rules, nothing more: an IO failure is not cached and the save that follows refuses (logs and writes nothing); a file that fails to parse is moved aside as `artwork.json.corrupt-<utc>` before an empty state replaces it. No four-outcome read model and no shared persistence helper. Config B039 owns this as part of its sidecar rules (critic conflict 16), together with the null-tolerant shape checks from LIBRARY-005.
- **Tests:** `ArtworkStateStoreTests`: a locked file makes `SaveGame` write nothing and leaves the file intact; a corrupt file is set aside, not overwritten. Filter `FullyQualifiedName~ArtworkStateStore`.
- **Plan v2:** B039.
- **Related:** critic conflict 16, CONFIG-V-005, LIBRARY-005.

### LIBRARY-014: State file roots come from the static log directory

- **Severity:** low
- **Where:** `src/WSGM/Core/Library/ImportStateStore.cs:90`, `src/WSGM/Shell/ArtworkStateStore.cs:14`.
- **Problem:** Both stores derive their path from the static `Log.Directory`, so tests and composition cannot give them a root.
- **Best solution:** Both constructors take the root directory from `UserDataContext` (file names unchanged, so no migration). Folded into config B037, which deletes `Log.Directory` at every site.
- **Tests:** existing store tests already use temp paths; run `FullyQualifiedName~ImportStateStore|FullyQualifiedName~ArtworkStateStore` with B037's filter.
- **Plan v2:** B037.
- **Related:** U04A-LFA-004, critic conflicts 16 and 26 (library B7 folds into config).

### LIBRARY-016: Slot vocabulary scattered across files

- **Severity:** low
- **Where:** `src/WSGM/Core/Artwork/SteamGridDb.cs:20-36` (`ArtworkAsset` enum); `src/WSGM/Core/Artwork/ArtworkAssetNames.cs`; `src/WSGM/Shell/GameLibraryArtwork.cs:175-178` (`Assets`); `src/WSGM/Core/Library/XboxLibrarySource.cs:214-218` (`SelectArtwork` order); `src/WSGM/Shell/SteamArtworkBrowserSource.cs:16-24` (`AllTabs` labels), `:1144-1154` (`Managed`), `:1172-1184` (`DataUrl`: MIME by extension, own 16 MiB literal).
- **Problem:** Slot ids, labels and order are repeated in four places; `DataUrl` guesses MIME from the extension although the rule is that bytes decide (`SteamArtwork.ImageFormat`), and it re-declares the 16 MiB bound.
- **Best solution:** Move the `ArtworkAsset` enum into `ArtworkAsset.cs` beside `ArtworkAssetNames`, values unchanged (they are Steam's eAssetType and persisted in `ArtworkPick.Asset`). Add to `ArtworkAssetNames` one ordered table `Ordered` of `(ArtworkAsset Asset, string Id, string Label)` in display order (Grid "Capsule", Wide "Wide Capsule", Hero, Logo, Icon). `GameLibraryArtwork.Assets`, `AllTabs` (plus the "manage" tab) and `Managed` derive from it. `XboxLibrarySource.SelectArtwork` keeps its own literal: its Grid, Hero, Logo, Wide, Icon order is the order its discovered artwork is emitted, not a display order, and changing it is not this finding's business. `DataUrl` reads the bytes, maps `ImageFormat` to MIME and uses `ArtworkDownload.MaximumBytes` (a D2 bound).
- **Tests:** `GameLibraryArtworkTests` unchanged and green; a browser test that the tab list and labels equal today's. Filter `FullyQualifiedName~Artwork|FullyQualifiedName~Xbox`.
- **Plan v2:** B102 (table and enum move); the `DataUrl` part lands in B103 with LIBRARY-006.
- **Related:** LIBRARY-V-003, LIBRARY-V-004.

### LIBRARY-017: Disabled-source ids longer than 32 characters are dropped

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs:727` (`NormalizeGameLibrary`: `id is { Length: > 0 and <= 32 }`).
- **Problem:** An arbitrary cap. A future source id over 32 characters would be dropped from `DisabledSources` at load and silently re-enabled.
- **Best solution:** Non-empty check only (`id is { Length: > 0 }`), in the normalizer's new home beside its section.
- **Tests:** a configuration fixture with a 40-character disabled id round-trips. Use B038's filter.
- **Plan v2:** B038 (config rules and limit removal).
- **Related:** none.

### LIBRARY-018: An oversized or unreadable launcher file reads as "absent"

- **Severity:** low
- **Where:** `src/WSGM/Core/Library/Sources/LibraryFiles.cs:21, 42-61` (`ReadBytes` returns null for missing, unreadable and over 16 MiB alike); `src/WSGM/Core/Library/Sources/BattleNetLibrarySource.cs:239, 376-381` (`product.db`); `src/WSGM/Core/Library/Sources/UbisoftLibrarySource.cs:47, 61, 132-135`; `src/WSGM/Shell/GameLibraryService.cs:1466-1473` (scan turns a thrown source failure into "unread, nothing offered for removal").
- **Problem:** Battle.net's `product.db` over 16 MiB, or one that cannot be opened, contributes nothing while the source still counts as read, so titles only it knows disappear and their imports are offered for removal (unticked).
- **Best solution:** `ReadBytes` returns null only when the file or its folder does not exist (`FileNotFoundException`, `DirectoryNotFoundException`, or `File.Exists` false). Any other read failure and an exceeded bound throw an `IOException` naming the file, which propagates out of `DiscoverAsync`; the scan already reports "<launcher> could not be read" and offers nothing of that source for removal. Drop the `(int)stream.Length` capacity hint. The 16 MiB default (`LibraryFiles.DefaultMaximumBytes`) and Ubisoft's 64 MiB cache bound (`MaximumCacheBytes`) stay as D2 refusals: the accepted list keeps the 16 MiB, 64 MiB cache and 4 MiB JSON bounds, reported, never absent. Ubisoft's own "could not be read" message for a missing cache is unchanged.
- **Tests:** `BattleNetLibrarySourceTests`: an oversized `product.db` (seam returning a throwing reader) makes discovery throw; a missing one yields only the uninstall-list titles. `GameLibraryServiceTests`: a source that throws offers none of its records for removal. Filter `FullyQualifiedName~LauncherSource|FullyQualifiedName~BattleNet|FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B107; decided D2: both bounds are on the accepted list.
- **Related:** none.

### LIBRARY-020: Change notifications run inside the service lock

- **Severity:** low
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:2467-2478` (`Publish` invokes `Changed`), 18 `Publish()` call sites inside `lock (_gate)`, `:1662-1687` (`ResetArtwork` calls `_artwork.Reset` under the lock), `:2355-2364` (`OnArtworkChanged` re-enters `_gate`); `src/WSGM/Shell/GameLibraryArtwork.cs:275-276` (`Reset` raises its own `Changed`).
- **Problem:** Subscribers run while `_gate` is held, so a subscriber that takes another lock can deadlock against a thread waiting on `_gate`. `ResetArtwork` nests: the artwork stage raises `Changed`, `OnArtworkChanged` re-enters the lock and publishes a second revision.
- **Best solution:** Split publication. `Publish()` only does `Interlocked.Increment(ref _revision)`; a new `Notify()` invokes `Changed` with today's catch. Every method calls `Notify()` once after its `lock` block when it published. `OnArtworkChanged` takes no lock: `if (!Volatile.Read(ref _disposed)) { Publish(); Notify(); }`. `ResetArtwork` builds its request list under the lock and calls `_artwork.Reset(...)` after leaving it. `GameLibraryArtwork` is unchanged; an extra revision is harmless because subscribers re-read the latest state.
- **Tests:** `GameLibraryServiceTests`: a `Changed` subscriber that starts a service command on another thread (for example `ArtworkOptionsAsync` for a listed entry, which takes `_gate`) and waits for it with a short timeout completes in time, which fails if `Changed` is raised under `_gate`. No test-only probe on the service. Filter `FullyQualifiedName~GameLibraryService|FullyQualifiedName~GameLibraryArtwork`.
- **Plan v2:** B105.
- **Related:** LIBRARY-006.

### LIBRARY-021: Store catalog client creates a client per lookup and lets odd failures escape

- **Severity:** low
- **Where:** `src/WSGM/Core/Library/StoreCatalogClient.cs:366-382` (`DefaultFetchAsync` creates an `HttpClient` per call), `:157-162` (catch covers only `HttpRequestException` and `TaskCanceledException`); `src/WSGM/Core/Library/XboxLibrarySource.cs:104-106`.
- **Problem:** A new `HttpClient` (and handler) per lookup opens and tears down a connection for every Xbox title in a scan; any exception from the fetch other than `HttpRequestException` and `TaskCanceledException` (an `IOException` from the response stream, an `InvalidOperationException` from the client, anything a future fetch path throws) escapes `LookUpAsync` and fails the whole Xbox source, hiding every Xbox title. `Parse` already catches `JsonException`, and an over-4-MiB body surfaces as `HttpRequestException` today, so those two are not the escape route.
- **Best solution:** Make `DefaultFetchAsync` an instance method over one private `HttpClient` field, created in the constructor only when no `fetch` seam is supplied (keep the 20 s timeout and the 4 MiB response bound, a D2 JSON bound). Widen the lookup catch to every exception when `!cancellationToken.IsCancellationRequested` (excluding `OutOfMemoryException`): log and return null, uncached, so that title simply has no Store data this scan.
- **Tests:** `StoreCatalogClientTests`: a fetch that throws `InvalidOperationException` yields null and a later lookup tries again; caller cancellation still throws. Filter `FullyQualifiedName~StoreCatalog|FullyQualifiedName~XboxLibrarySource`.
- **Plan v2:** B107.
- **Related:** LIBRARY-V-004 (same file).

### LIBRARY-022: Running-application monitor lifecycle and an executable-name cap

- **Severity:** low
- **Where:** `src/WSGM/Shell/RunningApplicationTarget.cs:614` (constructor starts `_loop`), `:617-624` (unsynchronized `_disposed`), `:700-712` (`ReportForeground` reads `_profile` without `_stateGate`), `:300` (`name.Length is 0 or > 128`); `src/WSGM/Shell/ShellSession.cs:606`.
- **Problem:** The loop starts in the constructor, contrary to the inert-construction rule; `_profile`/`_profileAppId` are written on the loop and read on the hook thread without the lock; `_disposed` is racy; executable names over 128 characters are refused, an arbitrary cap.
- **Best solution:** Add `internal void Start()` that assigns `_loop = Task.Run(ObserveLoopAsync)`; `ShellSession` calls it right after construction (`DisposeAsync` awaits `_loop` only if started). `ReportForeground` takes a snapshot of `_profile` under `_stateGate`. `_disposed` becomes an `int` set with `Interlocked.Exchange` in `DisposeAsync` and read with `Volatile.Read`. Change the name check to `name.Length is 0` (type check only).
- **Tests:** `RunningApplicationTargetTests`: the monitor does not poll the probe before `Start`; a 200-character executable name is accepted. Filter `FullyQualifiedName~RunningApplication`.
- **Plan v2:** B107.
- **Related:** plan claim C16.

### LIBRARY-023: SteamLibraryVdf mixes file IO into a pure parser

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamLibraryVdf.cs:500-540` (`TryReadMarkerContentId`, `TryReadMarker`: `File.Exists` plus `File.ReadAllText`, `IOException` escapes by design); callers `src/WSGM/Shell/CardVolumeMonitor.cs:421, 549`, `src/WSGM/Shell/LibraryPolicy.cs:140`, `src/WSGM/Shell/LibraryTabManager.cs:467, 517`.
- **Problem:** The otherwise pure VDF parser reads the marker file itself. `TryReadMarkerContentId` has no caller. No behaviour is wrong; this is a seam for B097's storage tests.
- **Best solution:** Delete the dead `TryReadMarkerContentId`. Split `TryReadMarker` into a pure `SteamLibraryVdf.TryParseMarker(string text, out string? contentId, out string label)` (today's body after the read) and one file-reading helper on the storage side, `SteamLibraryMarker.TryRead(string libraryPath, out string? contentId, out string label)` in `src/WSGM/Shell` (`File.Exists`, `File.ReadAllText`, then `TryParseMarker`; IO failures still propagate because the callers' policies differ). The five call sites switch to it unchanged otherwise. The marker read is not moved into the SD format owner: its callers are the card monitor, library policy and library tabs, not `SdFormatManager`.
- **Tests:** new `SteamLibraryVdfTests` (matched by B097's `FullyQualifiedName~SteamLibraryVdf` filter): `TryParseMarker` over marker text with one and with two content-id blocks picks the right label; the rest per B097 (storage filters).
- **Plan v2:** B097.
- **Related:** WINSVC-007.

### LIBRARY-024: Library tab filter uses the ambient Steam transport

- **Severity:** low
- **Where:** `src/WSGM/Core/LibraryFilter.cs:629-686` (`EvaluateAsync` through `SteamUiTransportSession.EvaluateAsync`, 12 s budget), `:213-238` (`FilterNode.Clone`).
- **Problem:** The filter evaluation is one of the ambient-client consumers that the toolkit redesign deletes.
- **Best solution:** `EvaluateAsync` takes the explicit `SteamClient` composed in `ShellSession` and keeps its 12 s budget as a local constant. `FilterNode.Clone` stays as is: making `FilterNode` a record would change equality on a config type for a nit.
- **Tests:** `LibraryFilterTests` with the B053 fake client. Filter `FullyQualifiedName~LibraryFilter`.
- **Plan v2:** B053.
- **Related:** plan claim C12 (consumer manifest).

### LIBRARY-025: NativePackageSource lives in the wrong project

- **Severity:** low
- **Where:** `src/WSGM/Interop/NativePackageSource.cs`; `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj:34-35` (linked compile); `src/WSGM.DeviceLab/Packaging/DeviceLabPackageSnapshot.cs:19-63` (only consumer).
- **Problem:** WSGM compiles a file it never uses, and Device Lab (MIT) links a GPL file.
- **Best solution:** Decided D3: Device Lab is relicensed as GPL in B173, which settles the licence half; the file is neither relicensed nor duplicated and keeps its GPL header. The remaining cleanup is a move: the file goes into `WSGM.DeviceLab` (its only consumer, `Packaging/DeviceLabPackageSnapshot.cs`) and the linked `<Compile Include>` at `WSGM.DeviceLab.csproj:34-35` is deleted, so WSGM stops compiling a file it never uses. BUILD-B6's alternative of moving it to `src/Shared/Interop` is rejected (critic conflict 22).
- **Tests:** Device Lab build plus its packaging tests per B173.
- **Plan v2:** B173; decided D3: Device Lab becomes GPL, the file only moves.
- **Related:** A02-F022, LABCORE-039, critic conflict 22.

### LIBRARY-027: Duplicate sibling-executable resolution and helper-name literals

- **Severity:** low
- **Where:** `src/WSGM/Core/Library/PackagedLauncherShortcut.cs:32-45` (`ResolveLauncher`); `src/WSGM/Core/LaunchWrapperCommand.cs:73-77` (`HelperPathForCurrentDeployment`); `src/WSGM/Shell/RunningApplicationTarget.cs:541` (`"WSGM.Launch"` literal).
- **Problem:** Two copies derive the install folder from `Environment.ProcessPath` with an `Installer.InstallDir` fallback, with different empty-string handling (`??` versus `IsNullOrEmpty`); a third place hard-codes a helper name, which caused LIBRARY-001.
- **Best solution:** Name part (B015): LIBRARY-001's `IsWsgmHelper` over the two constants. Path part (B107): one `internal static string SiblingExecutable(string fileName)` in `Installer` (it already owns `InstallDir`): `Path.Combine(string.IsNullOrEmpty(dir) ? Installer.InstallDir : dir, fileName)` where `dir = Path.GetDirectoryName(Environment.ProcessPath)`. `ResolveLauncher` keeps its `File.Exists` and catch on top; `HelperPathForCurrentDeployment` returns it directly. Public names are kept.
- **Tests:** `PackagedLauncherShortcutTests` and `LaunchWrapperCommandTests` unchanged and green; golden tests (LIBRARY-030) stay green. Library filter.
- **Plan v2:** B015 (names), B107 (path helper).
- **Related:** LIBRARY-001.

### LIBRARY-030: Composed launch strings are persisted ownership evidence

- **Severity:** low (risk)
- **Where:** quoting helpers `src/WSGM/Core/Library/ShortcutRoute.cs:291-296` (`CommandShortcut.Quote`), `src/WSGM/Core/Library/Sources/LibraryFiles.cs:335-394` (`LaunchArguments.Quote`), `SteamCustomLaunchCommand.Quote` (`:82-85`), `src/WSGM/Core/PackagedLaunchCommand.cs:708-734`; ownership comparison `ImportPlan.OwnsRecorded :413-418`, `Recompose :569-586`.
- **Problem:** Records store the exact composed Target and LaunchOptions and ownership is exact comparison. Any change in composed output turns every imported title into a preselected Update ("What this title's shortcut should run has changed"). This is the main regression risk of every library refactor batch.
- **Best solution:** Do not consolidate the four quoting helpers. Add `tests/WSGM.Tests/Core/Library/ComposedShortcutGoldenTests.cs` that asserts the exact `ShortcutFields` strings produced today for each route shape: Xbox packaged in both modes with and without multiplayer acknowledgement, Epic launcher and direct, GOG direct and Galaxy, Ubisoft, Battle.net launcher and classic, Amazon both orders, itch, Prism, ATLauncher, folder `.exe`, `.lnk` and `.url`, follow with dir/marker, and the drive-root refusal. Test-only, lands before any other library batch, and must stay green through B015 to B107.
- **Tests:** the new file. Filter `FullyQualifiedName~ComposedShortcutGolden|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand`.
- **Plan v2:** B014.
- **Related:** LIBRARY-032 (the 2048 change cannot change a composed string).

### LIBRARY-035: UNC policy is inconsistent between consumers

- **Severity:** low
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:687-692` (`AddFolderAsync` refuses `\\`); `src/WSGM/Shell/SteamArtworkBrowserSource.cs:284-291` (local artwork file refuses `\\`); `src/WSGM/Core/ConfigStore.cs:737-741` (`NormalizeGameLibrary` accepts a hand-edited UNC folder).
- **Problem:** The two picker consumers refuse UNC paths after selection but accept mapped network drive letters; the old plan (B2, M01-32) expected UNC library folders and artwork files to be accepted, which is not today's workflow.
- **Best solution:** Keep both refusals unchanged (workflow identical). The picker batch keeps reachable UNC working in the picker itself and states that library folders and artwork files still refuse UNC; M01-31/32 are written to match. Document in the library docs that mapped drives are accepted. The hand-edit path in the normalizer stays as is: no new check.
- **Tests:** B105's `AddFolderAsync` cases cover "UNC refused, mapped path accepted"; B059 fixtures for the picker.
- **Plan v2:** B059 (and section 5 matrix rewrite of M01-31).
- **Related:** plan claim C14, critic conflict 7.

### LIBRARY-036: Test quality and gaps

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Core/ArtworkProviderTests.cs:73-80` (static-list assertion); `tests/WSGM.Tests/Core/ImportStateStoreTests.cs:34-48` (pins caps); `tests/WSGM.Tests/Shell/GameLibraryServiceTests.cs` (missing behaviour cases); no tests for `SteamArtworkBrowserSource`, `SteamArtwork` apply/clear/icon, `ArtworkStateStore`, `UninstallEntries`, `Protobuf`, `LaunchArguments`, `LibraryFiles`, provider response parsing.
- **Problem:** Getter-style tests pin arbitrary values, and the apply loop's safety rules (record kept after a sent write, unconfirmed add recorded, stale revalidation) have no direct tests, although B105 moves that code.
- **Best solution:** Add before or with B105: Stop during apply after a write was sent keeps the record; an unconfirmed add with an AppId is recorded unconfirmed; a stale revalidation leaves the title alone with "changed since the scan"; `AddFolderAsync`/`RemoveFolderAsync` (UNC refused, mapped path accepted, duplicate refused, relative path refused per LIBRARY-V-007); a failing store write mid-apply stops and reports; stop during a run (LIBRARY-008); command exceptions refused (LIBRARY-005). The provider-parsing tests replace the static-list test in B102, the cap test is rewritten in B015, browser tests come with B103, and `UninstallEntries.FindProgram`, `LaunchArguments` and `Protobuf` unit tests come with B107.
- **Tests:** `FullyQualifiedName~GameLibraryService` for the B105 cases, then the library filter.
- **Plan v2:** B105 (service cases); pieces in B015, B102, B103, B107.
- **Related:** LIBRARY-003, -004, -005, -008, -010, -013.

### LIBRARY-V-003: The Manage tab misreports which icons are custom

- **Severity:** low (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Core/Artwork/SteamArtwork.cs:35` (`GridExtensions` lacks `ico`), `:255-290` (`FindCustomArtFile`), `:362` (`ApplyIconAsync` writes `{appId}_icon.{format}` with `ico` possible), `:380-387` (`ClearIconAsync` for shortcuts only clears the Steam setting); `src/WSGM/Shell/SteamArtworkBrowserSource.cs:1181` (unreachable `.ico` MIME branch).
- **Problem:** A shortcut icon written as `.ico` is never found, so the slot shows as not custom or shows an older `_icon.png`. A shortcut icon reset leaves the file, so the slot still reads as custom. For a store title, any `appcache/librarycache/{id}_icon.jpg` counts as custom, but Steam's own cache keeps that file.
- **Best solution:** One slot-to-file rule in `SteamArtwork`: `internal static string GridStem(uint appId, ArtworkAsset asset)` (today's `p`, `_hero`, `_logo`, `_icon` suffixes) and `GridExtensions = ["png", "jpg", "jpeg", "webp", "ico"]`, used by `ApplyIconAsync`'s shortcut path, by `FindCustomArtFile` and by the icon clear (the other slots are written by Steam itself through `SetCustomArtworkAsync` under the same names). A shortcut icon reset, once Steam accepted `ClearShortcutIconAsync`, deletes `{stem}.{ext}` for those extensions in the active account's grid folder only (the files WSGM's naming produces); a refused clear deletes nothing, so the shortcut never points at a missing file. A delete that fails is logged and the reset still reports Steam's answer. The store-title case is unchanged until attended evidence shows how Steam's cache looks (same footing as LIBRARY-034).
- **Tests:** `SteamArtworkBrowserSourceTests` or a new `SteamArtworkTests` over a temp Steam root: an `_icon.ico` is found and shown as `image/vnd.microsoft.icon`; after a shortcut reset the slot reads as not custom. Filter `FullyQualifiedName~SteamArtwork`.
- **Plan v2:** B103.
- **Related:** LIBRARY-016, LIBRARY-034.

### LIBRARY-V-004: Store catalog images are filtered by URL suffix, against the "bytes decide" rule

- **Severity:** low (plausible; needs one piece of evidence)
- **Where:** `src/WSGM/Core/Library/StoreCatalogClient.cs:328-335`; `tests/WSGM.Tests/Core/StoreCatalogClientTests.cs:128-139` (`AnImageWithNoFormatTheApplyPathKnowsIsNotOffered`).
- **Problem:** The comment says the apply path names the file by its suffix, which stopped being true when `SteamArtwork.ImageFormat` began reading the format from the bytes. Display-catalog image URIs are commonly extensionless; if they are, every Xbox title loses its Store artwork.
- **Best solution:** First confirm with one real catalog answer or a `wsgm.log` line (attended evidence, plan v2 section 4). If URIs are extensionless, delete the suffix filter (keep the HTTPS-only check) and its comment, and invert the test to assert an extensionless HTTPS image is offered. If they carry suffixes, change only the comment.
- **Tests:** the inverted `StoreCatalogClientTests` case. Filter `FullyQualifiedName~StoreCatalog`.
- **Plan v2:** B107, conditional on the attended evidence.
- **Related:** LIBRARY-021, LIBRARY-016.

### LIBRARY-V-005: An unreachable add is reported as "nothing was created"

- **Severity:** low (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/ShellSession.GameLibrary.cs:97-107` (`AddShortcutAsync`).
- **Problem:** The add is sent with `CancellationToken.None` after dispatch; an eval that times out comes back `Reachable=false`, so the shortcut may exist. The message "Steam is not reachable, so nothing was created." is false, and the stop reason misleads diagnosis from `wsgm.log`. Recovery already works (the next scan offers `Adopt`).
- **Best solution:** With the B053 typed outcome, map `Unknown` to `new ShortcutWriteResult(0, false, "Steam did not answer, so the shortcut may not have been created. Scan again before retrying.")`, `NotSent` to today's "nothing was created" message, `Rejected` to the toolkit's error. The run stops with no retry and no record (there is no AppId), exactly as today.
- **Tests:** mapping-table test over the B053 fake client: `Unknown` gives the new message and no record; the apply stops after it without calling the writer again. Filter `FullyQualifiedName~GameLibraryService|FullyQualifiedName~SteamShortcutWriter`.
- **Plan v2:** B053.
- **Related:** LIBRARY-004, U02B-SUTC-008/012, critic conflict 5.

## Nit

### LIBRARY-015: Dead provider overloads and a redundant adapter layer

- **Severity:** nit (verifier lowered from low)
- **Where:** `src/WSGM/Core/Artwork/ArtworkProviders.cs:135-161` (unfiltered and filtered overloads with default-interface forwarding), `:396-401, 426-436` (`ArtworkSearch` overloads without a query, tests only); `src/WSGM/Core/Artwork/SteamGridDb.cs:164-193`; `src/WSGM/Core/Artwork/ArtworkProviderImplementations.cs:65-114`.
- **Problem:** Two shapes per operation and a forwarding layer; Screenscraper implements only the unfiltered shape, so page N re-returns page 0. The browser's URL de-duplication drops the repeats, so nothing visible happens.
- **Best solution:** One query-taking method per operation on `IArtworkProvider` (returning `ArtworkPage`, LIBRARY-011); delete the unfiltered overloads, the forwarding default bodies and the query-less `ArtworkSearch` overloads. Screenscraper implements the query shape and returns an empty page with `HasMore = false` for page > 0. Tests switch to the query overloads.
- **Tests:** `ArtworkProviderTests` updated; Screenscraper page 1 is empty. Filter `FullyQualifiedName~Artwork`.
- **Plan v2:** B102.
- **Related:** LIBRARY-010, LIBRARY-011, LIBRARY-028.

### LIBRARY-019: A superseded scan still prunes choices

- **Severity:** nit (verifier lowered from low)
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:1476-1546` (`PruneChoices` at `:1541` before the generation check at `:1550`), `:2410` (`_ = previous`).
- **Problem:** A scan replaced by a rescan still prunes choices before checking its generation. Its read was complete, so the dropped choices were really unlisted; no wrong outcome, just wasted work in the wrong order.
- **Best solution:** Move the existing `generation == _generation && !token.IsCancellationRequested` check (taken under `_gate`) to just before `PruneChoices`, returning early when stale. Delete `_ = previous` in `Run`.
- **Tests:** `GameLibraryServiceTests`: a scan superseded after its read does not call `PruneChoices` (store probe). Filter `FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B015 step 4.
- **Related:** LIBRARY-008, LIBRARY-028.

### LIBRARY-026: LaunchWrapperCommand.StopRunningHelpers stops nothing

- **Severity:** nit
- **Where:** `src/WSGM/Core/LaunchWrapperCommand.cs:344-372`; only caller `src/WSGM/Core/Steam.cs:441`.
- **Problem:** The method only logs running helpers, and process enumeration sits in a command-formatting class.
- **Best solution:** Move it into `Steam` as `private static void LogActiveLaunchHelpers(string reason)`, unchanged body, using `LaunchWrapperCommand.HelperFileName` (made `internal` by LIBRARY-001).
- **Tests:** build; `FullyQualifiedName~LaunchWrapperCommand`.
- **Plan v2:** B107.
- **Related:** LIBRARY-001.

### LIBRARY-028: Dead code

- **Severity:** nit
- **Where:** `src/WSGM/Core/Library/ImportPlan.cs:425-429` (`Matches`); `src/WSGM/Shell/GameLibraryState.cs:287-297` (`GameLibrarySteamTarget` and its three statics); `src/WSGM/Core/Library/ShortcutRoute.cs:127-132` (`CommandShortcut.Compose`, tests only); `src/WSGM/Shell/GameLibraryService.cs:2410` (`_ = previous`).
- **Problem:** No production callers.
- **Best solution:** Delete them; tests that used `Compose` use `TryCompose`. `_ = previous` goes in B015 (LIBRARY-019), the unfiltered overloads in B102 (LIBRARY-015), the rest in B104.
- **Tests:** library filter, UI capture filter.
- **Plan v2:** B104.
- **Related:** LIBRARY-015, LIBRARY-019.

### LIBRARY-031: Stale comments

- **Severity:** nit
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:729-730` ("a full list cannot be overfilled"; there is no cap); `src/WSGM/Core/Library/ImportStateStore.cs:378` ("The newest rows are kept past the bound"; there is no bound).
- **Problem:** Comments describe caps that do not exist.
- **Best solution:** Delete the first; replace the second with a line stating that only rows failing a type check are dropped, and each is logged (LIBRARY-003).
- **Tests:** none.
- **Plan v2:** B015 step 8.
- **Related:** LIBRARY-003.

### LIBRARY-032: Other arbitrary limits

- **Severity:** nit
- **Where:** `src/WSGM/Core/PackagedLaunchCommand.cs:134` (`MaximumArgumentsLength = 2048`), `:192, :397-400` (compose and refusal), `:652` (the launcher's own parse check; the file is linked into `WSGM.PackagedLaunch`), `:131, :685` (`MaximumAumidLength = 512` in `ValidAumid`, added by the solution checker); `src/WSGM/Core/Library/XboxManifest.cs:24, 35` and `src/WSGM/Core/Library/MicrosoftGameConfig.cs:47, 62` (`MaximumBytes = 1 MiB` compared with `xml.Length`); tests `PackagedLaunchCommandTests.cs:184`, `XboxManifestTests`, `MicrosoftGameConfigTests`.
- **Problem:** 2048 refuses valid follow routes far below the Windows command-line limit; the 512-character AUMID cap is the same kind of product cap (Windows AUMIDs stay far shorter, so it protects nothing). The manifest checks compare characters with a byte-named 1 MiB constant and treat a larger manifest as unreadable, dropping the title, although the whole text is already in memory (`XboxPackages.ReadPackageFile` reads it with `File.ReadAllText`).
- **Best solution:** Replace `MaximumArgumentsLength` with `WindowsCommandLineLimit = 32767` (a real OS bound, not a product cap) checked against the whole composed command line (quoted target plus arguments) in compose and refusal, with the refusal text naming the Windows limit. Delete the parse-side check at `:652`: its input already arrived through a command line Windows accepted. Delete `MaximumAumidLength = 512` (`:131`) and its comparison in `ValidAumid` (`:685`) for the same reason; the shape checks (one `!` with text on both sides, no quote, no NUL) stay. Delete both manifest `MaximumBytes` constants and their length comparisons; a manifest is now unreadable only when it is null, blank or does not parse. This deviates from plan v2's "rename to characters": decided D2 accepts exactly its list, which does not hold this bound, so it is removed. Build `WSGM.PackagedLaunch` too.
- **Tests:** `PackagedLaunchCommandTests`: 3,000-character arguments are accepted, a command line over 32,767 is refused, a 600-character well-formed AUMID is valid; `XboxManifestTests`/`MicrosoftGameConfigTests`: drop the oversize case, add a large valid manifest that parses. Golden tests unchanged. Filter `FullyQualifiedName~PackagedLaunch|FullyQualifiedName~XboxManifest|FullyQualifiedName~MicrosoftGameConfig`, plus `dotnet build src/WSGM.PackagedLaunch -c Release`.
- **Plan v2:** B107; decided D2: not on the accepted list, so removed.
- **Related:** LIBRARY-030.

### LIBRARY-037: Remembered provider failures are never pruned

- **Severity:** nit
- **Where:** `src/WSGM/Core/Artwork/ArtworkRequestGate.cs:37, 93-99, 156-164`.
- **Problem:** Expired failure entries are removed only when the same key is asked again, so the dictionary grows over a long session.
- **Best solution:** When recording a new failure, first remove every entry whose expiry has passed. No count cap.
- **Tests:** `ArtworkRequestGateTests`: after expiry, recording another key removes the expired one. Filter `FullyQualifiedName~ArtworkRequestGate`.
- **Plan v2:** B102.
- **Related:** none.

### LIBRARY-038: Small duplications and a missing notification

- **Severity:** nit (verifier refuted one of three parts)
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:947-981, 1053-1074` (`ArtworkOptionsAsync` and `ReadArtworkOptions` repeat one body); `src/WSGM/Shell/SteamArtworkBrowserSource.cs:68-79` (`CancelBrowsing` changes state without raising `Changed`).
- **Problem:** Duplicated options logic; `CancelBrowsing` bumps the revision but no subscriber hears it, so the page keeps showing "Loading".
- **Best solution:** `ArtworkOptionsAsync` calls `ReadArtworkOptions` (one private body) in B104. `CancelBrowsing` raises `Changed` after leaving `_gate` when it changed `_state` (B103). The third part, "`ApplyAsync` assigns `_launcher` before refusing", is refuted: the assignment publishes launcher presence, true either way; leave it.
- **Tests:** browser test that `CancelBrowsing` raises `Changed` once; `GameLibraryServiceTests` unchanged. Filter `FullyQualifiedName~SteamArtworkBrowserSource|FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B103 (`CancelBrowsing`), B104 (options body).
- **Related:** none.

### LIBRARY-039: Public types in the application assembly

- **Severity:** nit
- **Where:** `ArtworkAssetNames`, `SteamGridDb`, `SgdbAsset`, `ArtworkSearch`, `AmazonInstall`, `ItchCave`, `UbisoftInstall`, `ImportStateStore` and others in `src/WSGM/Core/Artwork` and `src/WSGM/Core/Library`.
- **Problem:** `public` types in the WSGM executable widen the surface for no consumer.
- **Best solution:** Make each `internal` while its file is touched. Before changing one, confirm every referencing project has `InternalsVisibleTo` (WSGM.Tests does; check WSGM.UiTests and tools).
- **Tests:** build of the solution's test projects.
- **Plan v2:** B102 (where touched; the same rule applies in later library batches).
- **Related:** none.

### LIBRARY-V-006: TryReadIdAnd ignores its maximum parameter

- **Severity:** nit (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/SteamLibraryImportSurface.cs:142-150`, callers `:170, 217, 229, 243, 256` (pass 32 or 8).
- **Problem:** A dead parameter that suggests a cap nobody enforces.
- **Best solution:** Delete the `maximum` parameter and the argument at the five call sites. Do not reinstate the caps; type checks stay.
- **Tests:** build; `FullyQualifiedName~SteamLibraryImport` if present, else the library filter.
- **Plan v2:** B015 step 6.
- **Related:** none.

### LIBRARY-V-007: AddFolderAsync resolves a relative path against the working directory

- **Severity:** nit (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/GameLibraryService.cs:676-692` (`Path.GetFullPath(path)` before `Path.IsPathFullyQualified`).
- **Problem:** `Path.GetFullPath("Games")` yields an absolute path under WSGM's current directory, which then passes the fully-qualified check. Pickers always send absolute paths, so only a malformed page payload reaches it.
- **Best solution:** Check `Path.IsPathFullyQualified(path)` on the raw input first and refuse with the existing "Choose a folder on this machine." text; then normalize as today.
- **Tests:** `GameLibraryServiceTests`: `AddFolderAsync("Games")` is refused. Filter `FullyQualifiedName~GameLibraryService`.
- **Plan v2:** B015 step 7.
- **Related:** LIBRARY-036.

## Refuted or no-change

- **LIBRARY-029** (low): no change. The `LibraryDisk` record rewriting nine source constructors is about 1,200 lines of seam churn with no defect behind it; plan v2 dropped it.
- **LIBRARY-034** (nit, plausible): no change until attended evidence of Steam's store-title icon cache (`{appId}_icon.jpg` may be the name Steam requires); recorded as attended evidence in plan v2 section 4.
- **LIBRARY-033** (nit): Dropped by maintainer decision (security theater, DECISIONS.md). The `DefaultDllImportSearchPaths` attribute on the `msi.dll` imports in `Interop/ShellLink.cs` only guards against a same-user DLL plant; the three 32K-character buffers per link stay, as they are MAX_PATH-safe and not per sample. Nothing functional remains.
- **LIBRARY-040** (info): no change. SteamGridDB key and Screenscraper password stay plaintext in `config.json` and migrate unchanged.
