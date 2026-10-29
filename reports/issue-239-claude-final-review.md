# PR #239 final corrective review (Claude)

## Scope and method

- Base `7f076ee7`. I reviewed the uncommitted working tree in 22 files, including the root's late
  compile correction, which moved the `RemovedFromSteam` filter into `ReadManagedEntries` and left
  `GameLibraryService.Roms.cs` unchanged. I also reviewed `docs/game-library.md`,
  `tests/WSGM.UiTests/Visual/PreviewTools.cs`, the regenerated `NativeQamBootstrap.js` and the
  `external/steam-ui-toolkit` working diff (`library-badge.ts`, `check-library.mjs`,
  `docs/reference.md`).
- I read the original review (`reports/issue-239-claude-review.md`), the three worker reports and
  the callers that the new behavior depends on: `ShellSession.ReadShortcutsAsync`,
  `SteamCollections.SyncScript`, `SteamLibraryData.CollectionsExpression`, both emulator shortcut
  UIs, `ImportStateStore.Project`/`Replace`/`SaveCollection`, `ImportPlan.Describe*`,
  `BulkSelectable` and `ManagedShortcutMonitor`.
- This was a static review only. I did not build, test, render, generate assets, format, deploy or
  touch Steam or the card. No finding has a runtime reproduction.

Severity: High = likely user-visible breakage or a background change to Steam state the user did not
request. Medium = a real defect under a plausible trigger. Low = an edge case, a UX issue,
accounting or performance.

## Manual Steam shortcut deletion: verdict

Ordinary titles are correct for the main path. `ReadShortcutsAsync` refuses partial reads, checks
the account before and after, and only then calls `ObserveShortcutPresence`
(`ShellSession.GameLibrary.cs:186-189`). That call only touches confirmed (`ConfirmedUtc`),
non-pending-deletion mappings of the captured account (`ImportStateStore.cs:171-197`). Unknown and
other-account receipts are untouched. Uncertain adds (`ConfirmedUtc` empty) and dispatched deletions
are skipped. The plan then offers an unticked `DeletedFromSteam` Add with `ReplacedAppId > 0`, which
`BulkSelectable` (`GameLibraryService.cs:3272`) also excludes from Select all. Apply builds a fresh
`ImportedEntry`, so `RemovedFromSteam` resets to false, and `Replace` keeps the other accounts'
mappings. I found no background path that re-adds an ordinary shortcut.

The requirement is **not** fully met for emulator shortcuts (F2, F3) or for WSGM collections (F1).
The new documentation sentence "Startup, scans and storage reconciliation cannot grant that request
automatically" is therefore not yet true (F2, F3).

## Findings

### High

**F1. Background reconcile now syncs collections every 5 minutes, re-adding removed members and
recreating deleted WSGM collections**

- Where: `GameLibraryService.Storage.cs:1012-1076`. The early return from `CleanRemovedContentAsync`
  when no `InventoryRemoved` entry exists is gone, so every reconcile ends in `SyncCollectionsAsync`
  (`:1075`). The callers are the monitor's 5-minute full check, every hotplug observation, per-title
  and per-library Recheck, and storage-only Apply. `SyncCollectionsAsync`
  (`GameLibraryService.cs:2338-2450`) calls `_syncCollection` for every group with `want` set to
  every imported AppId. `SteamCollections.SyncScript`
  (`external/steam-ui-toolkit/.../SteamCollections.cs:112-119`) creates a new collection when the
  recorded id is no longer in `userCollections` and adds every `want` id that the collection lacks.
- Trigger: with "Create collections" on, the user deletes a WSGM collection or removes a title from
  it in Steam, then waits up to 5 minutes, plugs in a card, or presses Recheck on any title.
- Consequence: WSGM silently recreates the collection or puts the title back, with no user action.
  This is the collection version of the background recreation the maintainer asked to prevent.
  Before this change it happened only after a user Apply, or a cleanup that was actually pending. An
  unbound legacy collection group whose id is absent (M9's "deleted" case) is also recreated in the
  background as a new active-account collection.
- Fix: restore the guard so background reconcile calls `SyncCollectionsAsync` only when it pruned or
  cleaned something, and only with `takeBack` (removals). Additions and creation remain Apply and
  settings-toggle actions. If deliberate collection edits must be respected even on Apply, record
  observed membership and treat a missing collection id or removed member as user intent, the way
  `RemovedFromSteam` does for shortcuts.

### Medium

**F2. Changing an emulator shortcut's launch mode recreates a shortcut the user deleted in Steam**

- Where: `EmulatorService.cs:252-269` calls `RequestExplicitCreate` whenever `enabled` is true. Both
  surfaces send `enabled: true` for a mode change: `emulators.ts:99-101`
  (`enabled: entry.steamShortcutEnabled !== false`) and `EmulatorManagerView.cs:307-308`
  (`installation.SteamShortcutEnabled`).
- Trigger: the shortcut is enabled. The user deletes it in Steam, then changes "Launch mode" in
  Emulator Manager on either surface.
- Consequence: `EmulatorShortcutSync.cs:241-251` skips the deletion guard because `explicitCreate`
  is true, and the shortcut is added again. A property edit is not a request to recreate, and the
  documentation says only explicitly enabling it can do that.
- Fix: request explicit creation only on an off-to-on transition. Compare against the installation
  state before `SetSteamShortcutAsync`, or split the command into enable and mode operations.

**F3. A stale explicit-create flag can recreate a shortcut deleted later**

- Where: `EmulatorShortcutSync.cs:55-65` adds `(account, installationId)`, and `:183-187` removes it
  only when the `wanted` loop reaches that installation in the same account.
- Trigger: the user enables the shortcut (the flag is set), but that pass never reaches the
  installation. Examples: `!_manager.Initialized || !_ready()` (`:100-104`), a `_readLibrary`
  failure, an account mismatch (`:137-140`), or an earlier installation in the loop that throws
  (unconfirmed creation `:254-257`, unsupported mode `:209-212`, missing helper `:218-221`,
  `RequireOwned` conflict, update not confirmed `:303-306`, `CommandShortcut.TryCompose` refusal).
  The flag is also keyed to the account active at click time and is never consumed while another
  account is active. The user then deletes the shortcut in Steam (if it already existed), or
  switches back to the original account and deletes it there.
- Consequence: the next background sync that reaches the installation sees `explicitCreate = true`
  and recreates a shortcut the user deleted after giving the original intent. This breaks "manual
  deletion never causes background recreation".
- Fix: consume or clear every flag at the end of each pass, whatever the outcome. Alternatively,
  store a request timestamp and honor it only if the shortcut's deletion was observed before the
  request.

**F4. Steam is now read on every reconcile, so storage checks fail while Steam is unavailable**

- Where: `GameLibraryService.Storage.cs:1015-1023` reads `_readLibrary` unconditionally. A failure
  adds "Steam shortcut state could not be checked" to `problems`, which becomes `_storageError`
  (`:978-983`) and a failed `SteamUiCommandResult`. `ReadShortcutsAsync` can wait up to
  `LibraryReadyBudget` (60 s, `LibraryTabManager.cs:53`) while Steam's library loads.
- Trigger: Steam is not running or not ready, for example in the Windows desktop or during startup.
  Then a card hotplug, Overlay "Recheck all storage", a title Recheck, or the 5-minute check runs.
- Consequence: a storage recheck that inspected the card successfully reports failure. The Overlay
  shows a storage error, and `wsgm.log` gets `library.availability.error` on every monitor tick. A
  hotplug reconcile can hold `_storageLane` for up to 60 s while Steam loads. Before this change,
  Steam was read only when cleanup was pending.
- Fix: read Steam in reconcile only when a cleanup receipt is in scope or F1's narrowed collection
  sync needs it. Otherwise rely on the next ordinary read (scan, Apply, emulator sync) for
  `ObserveShortcutPresence`. If the read stays, report its failure as a separate "Steam state
  pending" note, not as a storage check failure.

**F5. Legacy receipts with `AppId == 0` or an edited live shortcut are blocked permanently with
misleading text**

- Where: `ImportPlan.cs:442-452` (`unresolvedHere` is true whenever a legacy mapping has
  `AppId == 0` or its AppId exists in Steam). Apply repeats the guard at
  `GameLibraryService.cs:2058-2066`. The emulator equivalent at `EmulatorShortcutSync.cs:188-194`
  blocks even with `explicitCreate`.
- Trigger: a v1 record whose add was never confirmed and has no unique exact match in this account,
  or whose shortcut the user edited in v1, so `BindLegacyAccount` cannot bind it.
- Consequence: the title shows "Waiting for an exact Steam account observation before changing this
  title." on every scan, which is false because the account was observed. Neither surface offers a
  recovery action. v1 offered these as unticked Add (`NeverConfirmed`) or as a hand-edit conflict.
  The emulator status keeps the "legacy shortcut ownership could not be confirmed" error on every
  sync, and turning it off and on does not clear it.
- Fix: for `AppId == 0` with no match in a confirmed full read, offer the unticked `NeverConfirmed`
  Add (not bulk-selectable) and keep the legacy receipt. For an edited live shortcut, show the
  existing `EditedByHand` conflict text. Give the emulator path the same explicit escape.

**F6. A deliberately deleted emulator shortcut leaves a permanent error and asks for an impossible
action**

- Where: `EmulatorShortcutSync.cs:241-251` adds "deleted in Steam; enable its shortcut in Emulator
  Manager to add it again", and `:343-346` throws, so `_status` and `Log.Warn` repeat on every sync.
- Trigger: the user deletes an enabled emulator shortcut in Steam.
- Consequence: Emulator Manager shows a pending-change failure indefinitely, after an intentional
  user action. The toggle already reads "On". Choosing "On" again does not send a change on either
  surface, because both use choice controls keyed on the current value. So the instruction cannot be
  followed directly, and the user has to find Off then On.
- Fix: show deletion as a neutral state ("Removed from Steam. Turn Off and On to add it again", or a
  dedicated "Add to Steam again" action that calls `RequestExplicitCreate`), and do not throw for
  it.

### Low

**F7. Storage-only Apply accounting is wrong, and dropped staged actions vanish without a message**

- Where: `GameLibraryService.cs:2007-2027` captures `storageActionCount` before `RefreshStorage`.
  The new drop logic in `GameLibraryService.Storage.cs:220-229` removes actions without adding a
  problem. `ApplyStorageActionsAsync` also appends the reconcile error (`:730-735`) to the same
  list. With selected titles, `applied = storageActionCount` (`GameLibraryService.cs:2051`) counts
  failed actions.
- Consequence: "Applied N storage change(s)" can count a dropped or failed action as applied, or
  subtract a reconcile problem that is not an action. An action dropped at Apply time, for example
  after a card becomes briefly unreadable, disappears with no explanation.
- Fix: count successes inside `ApplyStorageActionsAsync`, return them with the problems, and add a
  problem for each action that `RefreshStorage` drops.

**F8. A per-title Recheck clears the whole library's launch guard and replaces library-wide errors**

- Where: `GameLibraryService.Storage.cs:909-925` sets `RepairRequired = false` and
  `MigrationPending = false` after a title-scoped pass. `:978-983` replaces `_storageError` with
  that title's problems only. `PublishImportedContent`'s unchanged branch (`:~557-560`) also clears
  `RepairRequired` and adopts the card's revision without reconciling.
- Consequence: this contradicts the L21 disposition ("only a successful bounded reconciliation
  clears the guard"). One title's Recheck or Update re-enables launches for every cached recipe of
  that library, without comparing them to the manifest, and hides errors from the last full check.
- Fix: clear `RepairRequired` and advance `ManifestRevision` only on a full-library reconcile, and
  scope `_storageError` updates to the requested library or title.

**F9. A failed migration write leaves a self-created manifest that can never be migrated**

- Where: `GameLibraryService.Storage.cs:290-305` and `:337-341`. The first migration publishes a new
  manifest, then `SaveLibrary` runs. If `SaveLibrary` fails (an `ImportStateException` propagates),
  or the process exits in between, the next refresh sees `Present && existing is null` and takes the
  `continue` branch forever.
- Consequence: the legacy titles never migrate, and the card shows an unknown library needing "Use
  this card". Accepting it rebinds nothing, because legacy entries have an empty `LibraryId`
  (`:657-660`). Its manifest then lists the same titles as separate portable entries.
- Fix: when an unknown manifest's id and entries exactly equal the legacy group's content ids (the
  device's own interrupted publication), adopt it. Otherwise keep refusing foreign manifests.

**F10. Collections read reports an empty success before Steam's collection store loads**

- Where: `SteamLibraryData.cs:47-52` maps a missing `userCollections` to `[]` with `ok: true`.
  Callers: `ReadLibraryCollectionsAsync` (`ShellSession.GameLibrary.cs:18-42`), then
  `BindLegacyCollections`.
- Consequence: if collections are not loaded when the read runs, legacy groups stay unbound and
  `SyncCollectionsAsync` creates a second collection for each group. The guard at
  `ImportStateStore.cs:209-213` then prevents the legacy id from ever binding, so the duplicate is
  permanent. `_readLibrary`'s readiness wait makes this unlikely, but it does not prove the store is
  loaded.
- Fix: return a failure when `collectionStore.userCollections` is undefined (toolkit), or check
  readiness in `ReadLibraryCollectionsAsync`.

**F11. A scan swallows state-index write failures from legacy collection binding**

- Where: `GameLibraryService.cs:1671`. `StorageFailure` includes `ImportStateException`
  (`GameLibraryService.Storage.cs:65-66`).
- Consequence: an index write failure becomes a scan note, which conflicts with the L20 disposition
  that state-index failures stay fatal. Exclude `ImportStateException` as the storage catches do.

**F12. Extra mount enumeration per title**

- Where: `ManagedContentModels.cs:582` and `:748-749` call `ResolvePath(...)` without the check
  context, after a context-backed resolve.
- Consequence: an extra `GetVolumePathNamesForVolumeNameW` call and mount probe for every title in
  every availability pass. Pass `context` through. The mapping it validates is the same.

**F13. The toolkit Installed view cache depends on Steam array identity**

- Where: `library-badge.ts` `installedView` keys `all()` on
  `[collection.allApps, type-games.allApps, reading]` and `visibleOf()` on the visible arrays.
- Consequence: if Steam mutates any of these arrays in place, or changes an overview's
  `local_per_client_data.installed` (read by `availableManaged`) without replacing an array,
  Installed shows a stale list until another key changes. MobX observable arrays keep their identity
  when mutated. The worker report lists this as unchecked.
- Needed: a live check that installing or uninstalling a Steam game, and hiding or unhiding an app,
  updates the gamepad Installed tab while managed shortcuts exist.

## Original findings: current status

| #   | Status                        | Evidence / note                                                                                                                                                                                                                                                               |
| --- | ----------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| H1  | Fixed                         | GUID evidence for portable records only, mount-letter launch paths after mount-to-volume verification (`ManagedContentModels.cs:550-582`, `:711-750`). See F12.                                                                                                               |
| H2  | Partly fixed                  | Absent legacy AppIds now get an unticked Add, and emulator sync is per installation. `AppId == 0` and edited receipts stay blocked with no recovery (F5).                                                                                                                     |
| M1  | Fixed                         | v1 mapping defects drop the row with a warning (`ImportStateStore.cs:821-831`). v2 still throws.                                                                                                                                                                              |
| M2  | Fixed                         | Unchanged recipe skips publish (`Storage.cs:547-560`). Per-title catch in Apply (`GameLibraryService.cs:2128-2140`).                                                                                                                                                          |
| M3  | Fixed                         | `CancellationToken.None` after dispatch (`Storage.cs:1059`).                                                                                                                                                                                                                  |
| M4  | Fixed                         | Invalid staged actions dropped, per-action problems, snapshot under `_gate`. Accounting gap: F7.                                                                                                                                                                              |
| M5  | Fixed                         | Acceptance rebinds every cached library record. Per-entry `FromManaged` failures are isolated.                                                                                                                                                                                |
| M6  | Fixed                         | Skips known libraries without `MigrationPending`, and skips writes when nothing migrated. Interrupted first migration: F9.                                                                                                                                                    |
| M7  | Fixed                         | Reparse check limited to dependencies on the backing volume.                                                                                                                                                                                                                  |
| M8  | Fixed                         | `SyncCollectionsAsync` catches `InvalidOperationException`/IO/JSON.                                                                                                                                                                                                           |
| M9  | Fixed                         | Binding by exact id. Deleted legacy collections are recreated, now also in the background (F1).                                                                                                                                                                               |
| M10 | Fixed for fields              | `CheckKnownFields` covers every manifest object type and raises the no-repair "Update WSGM" path. A new `SourceKind` enum value in v1 would still be a JSON/validation error that offers Repair. That is acceptable if the docs state that a schema addition bumps `Version`. |
| L1  | Fixed                         | `Storage.cs:152`.                                                                                                                                                                                                                                                             |
| L2  | Fixed                         | Normalized in `CapturePath`, `BindPath`, `SameLaunch` and `SameAs`.                                                                                                                                                                                                           |
| L3  | Fixed for legacy fixed drives | Non-portable records use `ResolvePath`.                                                                                                                                                                                                                                       |
| L4  | Fixed                         | 32-hex entry id validation (`StorageLibraryStorage.cs:374`).                                                                                                                                                                                                                  |
| L5  | Fixed                         | Foreign existing manifest requires binding review (`Storage.cs:525-529`).                                                                                                                                                                                                     |
| L6  | Partly fixed                  | Pruning and cleanup are scoped. Collection sync and Steam read remain global (F1, F4), and library guard/error state is overwritten (F8).                                                                                                                                     |
| L7  | Not addressed                 | `_storageLane` is still held across a whole scan/apply. No disposition is recorded.                                                                                                                                                                                           |
| L8  | Fixed                         | `AppId == 0` cleanup receipts are kept and reported.                                                                                                                                                                                                                          |
| L9  | Fixed                         | Initialization check before consuming the flag. Recovery may wait for the 5-minute timeout because `Wake` does not release before initialization. Acceptable.                                                                                                                 |
| L10 | Fixed                         | Second observation logged.                                                                                                                                                                                                                                                    |
| L11 | Fixed                         | `ConfirmReadableContentParent(..., portable)`.                                                                                                                                                                                                                                |
| L12 | Fixed, with live gap          | See F13.                                                                                                                                                                                                                                                                      |
| L13 | Fixed                         | `ManagedAvailabilityWanted`.                                                                                                                                                                                                                                                  |
| L14 | Not addressed                 | Projection still yields `AppId = 0` while the account is unknown. No disposition is recorded.                                                                                                                                                                                 |
| L15 | Fixed (unrendered)            | Pending title state on both surfaces, Steam "Recheck all storage", and paged title rows in the Steam storage sheet. The bundle contains the new code.                                                                                                                         |
| L16 | Fixed                         | `[Obsolete(error: true)]`. No two-argument callers remain.                                                                                                                                                                                                                    |
| L17 | Not addressed                 | `SaveInventories` still throws for the whole batch (`ImportStateStore.cs:285-288`). No disposition is recorded.                                                                                                                                                               |
| L18 | Not addressed                 | `eng/steam-cef-mcp.mjs` is unchanged. No disposition is recorded.                                                                                                                                                                                                             |
| L19 | Fixed                         | `EmulatorManager.cs:101`.                                                                                                                                                                                                                                                     |
| L20 | Rejected, justified           | Index failures stay fatal. F11 is an exception that remains.                                                                                                                                                                                                                  |
| L21 | Rejected, justified           | Conservative guard kept. F8 undermines it from the other direction.                                                                                                                                                                                                           |

## Areas checked with no finding

- `ObserveShortcutPresence` account scoping, unknown/legacy receipt preservation, the skip of
  uncertain-write and pending-deletion mappings, and execution inside `_shortcutOperations` after
  both account reads.
- Ordinary importer re-add: `Replace` preserves other mappings, the legacy-unresolved Add projection
  is `Unconfirmed` and not bulk-selectable, and `RequireEntryAccount` refuses unresolved
  projections.
- Legacy v1 migration drop, mapped back through `Sanitize`/`EntryDefect` "no source or key".
- `CheckKnownFields` coverage of `StorageLibraryManifest`, `StorageLibraryContent` and
  `StorageLibraryPath`, including arrays and nullable path members.
- `RemovedFromSteam` persisted through `ImportAccountMapping`, its `SameAs`, `From` and `Project`,
  the badge and collection filters, and `RecheckShortcutAsync`.
- Steam `ImportDetailsBody` pending logic (`state.loading` exists), shared `showImportDetails`,
  Overlay `RenderEntryPending` and the matching preview scenario.
- `TryReadRecheck` accepts an empty id, so Steam "Recheck all storage" reaches
  `RecheckAvailabilityAsync("")`.

## Validation gaps

- No unit tests cover `ObserveShortcutPresence`, `RemovedFromSteam` in plans or emulator sync,
  explicit-create consumption, the v1 row drop, `CheckKnownFields`, unchanged-recipe publication,
  scoped reconcile or staged-action dropping. The only test change adapts `Forget`.
- Nothing here was compiled, run or rendered by me. The root reports a Release batch in progress.
- Live gaps: manual Steam deletion with WSGM running (ordinary and emulator), collection behavior
  after F1 is addressed, the Steam storage sheet with many titles and Load more focus, the Installed
  tab under F13, and card hotplug with Steam closed (F4).
