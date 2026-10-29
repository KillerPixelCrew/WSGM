# PR 239 round-two review (uncommitted fixes)

Base: `00ab2b380ba34ce24a0113ca18a7b19897133ff4`. Scope: every uncommitted change in the worktree
(the six source files plus `tests/WSGM.Tests/Core/ImportStateStoreTests.cs`, which the coordinator
added during this review) and the callers those changes affect. This was a read-only review. I ran
no builds, tests, formatters, Steam, CEF or deployment. Line numbers refer to the current working
tree.

## Summary

| ID  | Severity | Area               | Short claim                                                                                                                         |
| --- | -------- | ------------------ | ----------------------------------------------------------------------------------------------------------------------------------- |
| H1  | High     | Artwork settlement | Settled picks dropped from `Entry.Picks` are then erased from the shared choice on the next choice save                             |
| M1  | Medium   | Artwork / Stop     | An uncancellable artwork batch can outlast the shutdown deadline before the confirmed title's only durable receipt write            |
| M2  | Medium   | Emulator intents   | An explicit recreate is still lost when its pass is not admitted (Steam not ready) or throws before reaching it                     |
| M3  | Medium   | Storage manifest   | A missing manifest recipe now always wins over a present local recipe, so a disagreement removes inventory and queues Steam cleanup |
| L1  | Low      | Artwork settlement | Adopting a different AppId keeps the old shortcut's settlement                                                                      |
| L2  | Low      | Artwork settlement | Failed slots are cleared in-session but come back after a restart (inconsistent; not an uncertain retry)                            |
| L3  | Low      | Artwork settlement | Deliberately re-picking the already-settled URL is silently filtered on the next scan                                               |
| L4  | Low      | Storage manifest   | `replacesMissing` branch and the `!manifestIds.Contains` test are now dead code                                                     |
| I1  | Info     | Concurrency        | `entry.Picks` is read outside `_gate` after the await in `ApplyImagesAsync`                                                         |
| I2  | Info     | JSON / compat      | Strict null rejection and downgrade behaviour of `SettledArtwork`                                                                   |
| I3  | Info     | Tests              | The new tests cover only the store round trip, none of the behaviour in H1 to M3                                                    |

## Assessment of the three GitHub comments

**4412880199 (stale cached path deletes a valid manifest entry): resolved for the reported
trigger.** `GameLibraryService.Storage.cs:868-872` skips any cached record whose content ID is in
the manifest. The manifest pass alone decides those IDs. When another device moves a title's backing
path while keeping its ID, the present manifest recipe is no longer deleted because a stale local
path is missing. The library scan already adopts the manifest recipe through
`ManifestLibrarySource.DiscoverAsync` (`ToManaged(..., localRecipe)`), so the stale local recipe
shows up as a content change rather than a deletion. Residual: M3 (the reverse disagreement is now
destructive) and L4 (dead code).

**4412880217 (new explicit recreate request erased by the prior pass): resolved for requests queued
during an active pass.** `EmulatorShortcutSync.cs:102-107` swaps `_explicitRecreates` into a
pass-local set when the pass wakes. The unconditional `finally` clear is gone, and `ReconcileAsync`
consumes only from the local set (`:191`). A request made while a pass runs stays in
`_explicitRecreates`, and `Request()` has already released `_wake`, so the next pass sees it.
Residual: M2 is the same failure class at the other two exits of a pass, and it predates this fix.

**4412880231 (artwork choices replay across restarts): resolved for the applying account, but it
introduces H1.** Settlement is now per account (`ImportAccountMapping.SettledArtwork`), persisted
and validated. `Copy`, `From`, `Project` and `SameAs` deep-copy and compare it, `SaveApplied` no
longer short-circuits when only settlement changed, and another account's projection has an empty
map, so shared choices stay pending there. `Create` (`GameLibraryService.cs:1952-1959`) leaves out
picks whose exact URL this account already settled on the same AppId, so `ArtworkPending` no longer
pulls those titles into an unrelated Apply after a restart. `ApplyImagesAsync` (`:2633-2642`)
settles only the successful pick slots, in order. A Steam `Unknown` outcome is reported as
`Succeeded` by `SteamArtwork.Interpret`, so it is settled and never replayed, which fits the
no-uncertain-retry rule. The defect: removing settled picks from `Entry.Picks` also removes them
from every later `ChoiceOf`, which erases the shared choice (H1). L1 to L3 are smaller gaps.

## Findings

### H1 (High): the next choice save erases settled picks from the shared device-wide choice

- **Where:** `src/WSGM/Shell/GameLibraryService.cs:1952-1959` (new filter in `Create`), combined
  with `ChoiceOf` at `:1497-1515` (`Artwork = [.. entry.Picks.Values]`) and its writers `EditEntry`
  → `_store.SaveChoice(ChoiceOf(entry))` at `:1357` and `EditMany` → `_store.SaveChoices(...)` at
  `:1419-1423`.
- **Trigger:** In account A, pick Hero=U for a title and Apply. Hero is settled for A. Restart WSGM,
  or rescan, so `Create` runs again. A's `Entry.Picks` no longer contains Hero. Now make any
  remembered edit to that title: pick or cycle another slot (`EditArtwork`), `FillArtworkAsync`,
  `SetMatchAsync`, title name, emulator or core override, mode, route, exclude, cleanup or
  arguments.
- **Consequence:** `ChoiceOf` writes `Artwork` without Hero. `SaveChoice` replaces the device-wide
  choice, so Hero=U is gone for every account. Account B (signed in later, never applied) silently
  loses the user's choice. This is exactly the "preserve shared choices for other accounts" part of
  comment 4412880231. Account A also loses it. If A's user later deletes the shortcut in Steam and
  re-adds it through WSGM (`Add`, `ReplacedAppId`), the choice is no longer there to reapply.
  `Entry.Settle` (`:3316`, `Picks.Clear()`) already had the same effect within one session before
  this change. The fix extends it across restarts and rescans, which is how this state is normally
  reached.
- **Suggested fix:** Keep settled picks on the entry, apart from the pending ones. For example,
  `Create` could put the filtered picks into an `Entry.SettledPicks` dictionary instead of dropping
  them. `ChoiceOf` would then emit `SettledPicks` merged with `Picks`, with pending picks taking
  precedence per slot, and `Settle` would move each settled slot into `SettledPicks` instead of
  discarding it. `ArtworkPending` keeps using `Picks` only. Decide explicitly whether
  `ResetArtworkAsync` should also clear `SettledPicks`. Today it returns `false` when `Picks` is
  empty and leaves them persisted, which is reasonable. Add a service-level test: settle for account
  A, reload, edit another field, then assert that the persisted choice still contains the settled
  pick and that account B still has it pending.

### M1 (Medium): Stop and shutdown can no longer bound a title's durable receipt write

- **Where:** `src/WSGM/Shell/GameLibraryService.cs:2633-2635`
  (`_applyArtwork(appId, images, CancellationToken.None)`). The confirmed title's only durable write
  is the `finally` → `_store.SaveApplied(saved)` at `:2295-2303`. Since the confirmed path skips the
  save at `:2264-2266`, nothing is persisted between the confirmed `AddAsync`/`UpdateAsync` and that
  `finally`. The callee is `SteamArtwork.ApplyManyFromUrlsAsync`
  (`src/WSGM/Core/Artwork/SteamArtwork.cs:97-136`).
- **Trigger:** An Apply has just confirmed a shortcut and starts its artwork batch. The downloads
  wait behind a provider's `ArtworkRequestGate` (Screenscraper capacity 1, SteamGridDB 4). The wait
  is not cancellable with `None`, see `EnterAsync` at `ArtworkRequestGate.cs:275`. Or the image host
  is slow: the `ArtworkDownload` HttpClient timeout is 30 s per request, followed by serial Steam
  client writes. During this the user presses Stop or WSGM shuts down. `StopAsync` waits for
  `_running` only until the shutdown deadline (`:1192-1214`) and then returns.
- **Consequence:** Stop no longer takes effect for up to several tens of seconds. On shutdown the
  process can end before `SaveApplied` runs. The shortcut Steam confirmed then has no account
  receipt, and the controller target written by `WriteControllerTargetAsync` is unrecorded, so
  `OwnsProfile` is lost. Artwork already applied is not settled either. The next scan offers the
  shortcut as `Adopt` (packaged and command routes both adopt unclaimed owned shortcuts, see
  `ImportPlan.cs:697-708` and `:778`), so I see no duplicate. Profile ownership, `ArtworkApplied`
  and settlement are lost, though. Before the change, cancellation aborted the batch quickly and the
  `finally` saved the receipt.
- **Suggested fix:** Persist the confirmed receipt (`SaveApplied(saved)`) before decoration starts,
  then save again with `OwnsProfile`, `ArtworkApplied` and settlement afterwards. Pass the run token
  to the download phase and use `CancellationToken.None` only for each Steam write once it has been
  dispatched. Alternatively, have `ApplyManyFromUrlsAsync` return a per-slot "Stopped" failure
  instead of throwing, so completed slots still settle and unstarted ones stay pending.

### M2 (Medium): an explicit emulator recreate is lost when its pass is not admitted or fails early

- **Where:** `src/WSGM/Shell/EmulatorShortcutSync.cs:102-112` (the requests are swapped out, then
  `continue` when `!_manager.Initialized || !_ready()`) and `:117-124` (an exception from
  `ReconcileAsync` discards the pass-local set).
- **Trigger, case A:** While Steam or Big Picture is not ready yet, for example during boot, the
  user turns a manually deleted emulator shortcut Off and then On. The Off pass is not admitted, so
  the receipt is not forgotten. The On request is swapped out and dropped by the not-ready
  `continue`. When Steam becomes ready, the next pass finds the receipt with `AppId > 0`,
  `ConfirmedUtc`, no live shortcut and `explicitCreate == false` (`:245`), and reports "removed from
  Steam. Turn its shortcut Off and On to add it again."
- **Trigger, case B:** A pass consumes the requests and then throws before it reaches that
  installation. Examples are `_writer()` returning null ("Steam is unavailable. Shortcut changes are
  pending."), `_readLibrary` failing, the account changing (`:146-148`), or another emulator
  throwing "creation is unconfirmed" in the removal loop (`:171-174`) or in the wanted loop before
  this one.
- **Consequence:** The toggle reports success but the shortcut stays absent until the user toggles
  it again. The status even says changes are "pending". This predates the round-two fix (both exits
  cleared the set before), but it is the same lost-intent defect that comment 4412880217 describes,
  and the new capture sits exactly at these exits.
- **Suggested fix:** At the not-ready `continue` and in the `catch`, re-add the requests that are
  still in the pass-local set under `_gate`. `requests.Remove` has already taken out every request
  this pass consumed. This does not retry anything uncertain: an add is persisted before dispatch
  (`:278-281`), and an unconfirmed add throws on later passes (`:257-260`), so a restored request
  cannot cause a duplicate. Keep the account key, so a request made under another account still
  expires.

### M3 (Medium): a manifest/local disagreement now removes inventory and queues Steam cleanup

- **Where:** `src/WSGM/Shell/GameLibraryService.Storage.cs:849-863` and `:866-872`, with the effect
  at `:916-940`.
- **Trigger:** The manifest recipe for content ID X is `Missing` on this volume, while this device's
  cached recipe for X (same ID, same library) is `Present`. Examples: another device rewrote X's
  recipe to a new path whose files have not been copied yet, or its `RequiredPaths` name a
  dependency this card lacks, while this device's recipe resolves.
- **Consequence:** The manifest pass adds X to `removed`, and the new skip means the cached recipe
  can no longer veto that. Reconciliation publishes the manifest without X, marks the local
  inventory `InventoryRemoved`/`ContentMissing`, and `SaveInventories` queues Steam shortcut
  deletion for every account (`ImportStateStore` sets `PendingDeletion` on all mappings). Before the
  change, the present local recipe replaced the manifest entry. That was the mirror-image bug,
  because it overwrote the other device's newer recipe. Neither outcome is safe when the two recipes
  disagree. Today the result is an automatic destructive action on the manifest's word while files
  this device can launch are present.
- **Suggested fix:** When a manifest ID is `Missing` but a cached recipe for the same ID on this
  library is `Present`, treat it as a conflict. Set `failed = true`, add a problem such as "This
  device's copy of X differs from the card's library. Review it.", and neither delete nor rewrite
  the entry. Removal stays automatic only when the manifest recipe and every cached recipe for the
  ID are missing. If manifest authority is the intended product rule, write that down in
  `docs/decisions.md` and delete the dead branch (L4).

### L1 (Low): adopting a different AppId keeps the previous shortcut's settlement

- **Where:** `src/WSGM/Shell/GameLibraryService.cs:2217-2218` (the reset is keyed on
  `current.Action == ImportAction.Add`).
- **Trigger:** The record for account A has `AppId` X, which is no longer live. The scan finds an
  owned orphan Y and plans `Adopt` (`ImportPlan.cs:697-708`). `Create` correctly keeps the picks
  pending because `record.AppId != planned.AppId`. On Apply, one slot (say Hero) fails on Y.
- **Consequence:** `saved.SettledArtwork` was copied from X's receipt, so if X had Hero=U settled
  and the pick is still U, Hero is recorded as settled for Y although Y never received it. After a
  restart it is filtered out and never applied.
- **Suggested fix:** Reset settlement whenever `record is null || record.AppId != result.AppId`,
  instead of keying on `Action`. That covers both `Add` and `Adopt`.

### L2 (Low): failed slots behave differently before and after a restart

- **Where:** `Entry.Settle` (`GameLibraryService.cs:3301-3320`, `Picks.Clear()`), together with the
  new pending-on-failure settlement at `:2636-2642`.
- **Trigger:** Apply a title whose Hero download or Steam write fails (`NotSent`, rejected, or a
  failed download). `Unknown` counts as success, so it is excluded here.
- **Consequence:** In the same session the pick disappears, the title is no longer pending, and the
  problem text says "Pick others and save again". After a restart the failed pick reappears as
  pending, the title becomes `BulkSelectable`, and a later "select all" Apply attempts it again. No
  rule is broken, because the failed write definitely did not happen and only a user Apply resends
  it. The state the user sees still depends on whether WSGM restarted.
- **Suggested fix:** In `Settle`, remove only the slots that settled and keep the failed picks, so
  the title stays visibly pending in the same session, or decide that failures settle as well.
  Either way, make the two paths agree.

### L3 (Low): deliberately re-picking the settled URL is silently filtered

- **Where:** `GameLibraryService.cs:1955-1958`.
- **Trigger:** After an apply, the user changes the artwork by hand in Steam. Then, in WSGM, they
  pick the same image again to restore it. A rescan or restart happens before they press Apply.
- **Consequence:** The saved choice's URL equals the settled URL, so `Create` drops it. The user's
  explicit restore request disappears without feedback.
- **Suggested fix:** Give an explicit pick a revision or timestamp (for example
  `ArtworkPick.Picked`) and compare settlement on that rather than on the URL alone. Or, when an
  edit re-picks a settled URL, mark that settlement stale in the account receipt.

### L4 (Low): dead code left in the cached-recipe pass

- **Where:** `GameLibraryService.Storage.cs:882-892`.
- **Detail:** Every cached ID that reaches this point is absent from `manifestIds`, so
  `removed.Remove(portable.Id)` (`replacesMissing`) can only succeed for a duplicate cached ID, and
  `!manifestIds.Contains(portable.Id)` is always true. The `manifest.Entries.RemoveAll` replace
  branch cannot run.
- **Suggested fix:** Simplify to "present and not in the manifest: add it", or rework it as part of
  M3.

### I1 (Info): `entry.Picks` is read outside the lock

- **Where:** `GameLibraryService.cs:2638`.
- **Detail:** `Images(entry, created)` runs under `_gate`, but the post-await
  `entry.Picks.ContainsKey(...)` does not. `Guard()` refuses edits while `_phase is Phase.Applying`,
  so I found no live race. Capturing the set of pick assets together with `images` under the lock
  would make this independent of that guard.

### I2 (Info): JSON validation and compatibility of `SettledArtwork`

- A missing property deserializes to the `[]` initializer, so v2 files written before this change
  and v1 legacy rows (`ImportStateStore.cs:803-830`) load fine.
- `"SettledArtwork": null`, a null value, or an undefined enum key (STJ accepts numeric key strings
  such as `"99"`) makes `MappingDefect` return a defect. `EntryDefect` then throws
  `ImportStateException` for the whole index (`:1016-1022`). That is consistent with the existing
  strict receipt policy, which is stated as intended. Duplicate JSON keys are last-wins, which is
  harmless.
- `ImportStateJsonContext` does not disallow unmapped members. An older WSGM therefore loads the new
  file but drops `SettledArtwork` when it rewrites a receipt, and after upgrading again those
  choices replay once. This is acceptable for prerelease builds and only noted here.
- `SameAs` compares the dictionaries exactly, so `SaveApplied` persists a settlement-only change. I
  confirmed this against the early-return at `ImportStateStore.cs:505-516`.

### I3 (Info): test coverage of the round-two changes

- The two new tests in `tests/WSGM.Tests/Core/ImportStateStoreTests.cs:12-56` check the store round
  trip, per-account projection and the deep copy. Their entries rely on the `""` defaults of
  `ImportedEntry` strings (`ImportPlan.cs:59-94`), which satisfies `MappingDefect`.
- The first test's assertion that the shared choice remains does not cover H1, because nothing in it
  saves a choice after settlement.
- No tests cover: the `Create` filter and the AppId reset, `ChoiceOf` preserving settled picks (H1),
  partial-slot settlement and cancellation in `ApplyImagesAsync` (M1, L2), the emulator requests
  made during a pass or a non-admitted pass (comment 2, M2), and the manifest-ID skip and
  disagreement in `ReconcileStorageAsync` (comment 1, M3). The coordinator says the tests will be
  compiled and not run, per the manual-first policy.

## Checked and found correct

- `ImportAccountMapping.Copy`, `From`, `Project`, `ImportedEntry.Copy` and `ImportStateStore.Copy`
  (through the `mapping.Copy()` calls in `Replace`, `SaveInventories` and `BindLegacyAccount`) all
  deep-copy `SettledArtwork`. `ImportedEntry.SettledArtwork` is `[JsonIgnore]`, so it never leaks
  into the shared row, and another account's projection gets an empty map.
- `BindLegacyAccount` rebinds a legacy mapping in place. Legacy mappings carry no settlement, so the
  AppId rebind cannot inherit a stale one.
- The unconfirmed-update reconcile (`:2240`) carries the record's settlement but applies no artwork,
  which is correct. The unconfirmed-add save (`:2258`) carries an empty map, also correct.
- Default (non-pick) slots on a new shortcut are never settled (`entry.Picks.ContainsKey` check), so
  defaults never pose as choices.
- Explicit clears (`Url == ""`) settle as `""` and are compared exactly. `Unknown` Steam outcomes
  settle and are never replayed.
- `EmulatorShortcutSync` entries (`wsgm-emulators`) are excluded from the library service
  (`GameLibraryService.cs:1676`), and their `Copy()` carries an always-empty map.
- No line-ending problems: none of the changed files contains CRLF lines, and the git warnings come
  only from `core.autocrlf=true`.
