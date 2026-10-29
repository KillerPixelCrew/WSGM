# PR #239 independent review (Claude)

## Scope

- Base `a8921a0eabd6816ee4a30534a59542e03c9b7a31`, branch
  `NightHammer1000/removable-storage-nonsteam-shortcuts`, HEAD `7f076ee7` plus the uncommitted
  working-tree fixes in 8 files (`ManagedContentModels.cs`, `ImportStateStore.cs`,
  `StorageLibraryStorage.cs`, `EmulatorShortcutSync.cs`, `GameLibraryService.Storage.cs`,
  `GameLibraryService.cs`, `ShellSession.GameLibrary.cs`, `ShellSession.cs`).
- Toolkit delta `external/steam-ui-toolkit` `661199a7..a3f6a825` (library-badge gate,
  `SteamLibraryBadgeSurface`, tests, check script, docs). The WSGM bundle `NativeQamBootstrap.js`
  matches the toolkit source at HEAD.
- I read every changed source file in `src/`, `tests/`, `tools/` and the toolkit delta, together
  with the callers of the changed APIs (`ManagedContentLaunch`, `EmulatorManager`, `ImportPlan`,
  `SyncCollectionsAsync`, `ApplyCoreAsync`, `Run`/`Begin`, `ManagedShortcutMonitor`,
  `SteamUiSessionHost`, `LibraryBadges`). I also read the plan
  (`docs/plans/removable-shortcut-availability.md`) for intended behavior.
- Method: static reading only. I did not build, run tests, attach to CEF, or touch Steam, the card
  or config. Every finding comes from the code paths cited. None is backed by a runtime
  reproduction.

Severity: High = likely user-visible breakage or data/ownership loss on ordinary upgrade or use;
Medium = real defect under a plausible trigger; Low = edge case, perf, doc or UX.

## Five PR comments: current status

| #   | Comment                      | Status                                                 | Notes                                                                                                                                                                                                                                                                                                                                                                                                                          |
| --- | ---------------------------- | ------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 1   | Generated dot segments       | **Fixed, small residual**                              | `BindPath` and `FromManaged` now normalize (`ManagedContentModels.cs:273`, `StorageLibraryStorage.cs:362`), and `ResolveBoundPath` normalizes before validating. Remaining: `CapturePath` still stores `.` for a volume-root source, and legacy records persisted with `Games\.` / `.\game.gb` now compare unequal in `SameLaunch` (see L2).                                                                                   |
| 2   | Unbound legacy collections   | **Fixed, residuals**                                   | `SyncCollectionsAsync` now calls `BindLegacyCollections` and skips unresolved groups (`GameLibraryService.cs:2323-2380`). Binding requires the observed collection to contain every recorded AppId, so an edited or deleted legacy collection never binds and reports a permanent problem (M9). The new account and collection reads also throw exceptions that are not caught (M8).                                           |
| 3   | Unresolved emulator receipts | **Duplicate creation prevented, but it over-corrects** | `EmulatorShortcutSync.cs:130` now throws for the whole sync whenever any emulator receipt is unresolved. Receipts for shortcuts the user deleted, or that live in another account, can never resolve, so emulator shortcut sync is blocked permanently (H2).                                                                                                                                                                   |
| 4   | Transient manifest recovery  | **Fixed**                                              | `RecheckStorageChangesAsync` now also requests libraries whose previous status was `Check failed` or whose status changed (`GameLibraryService.Storage.cs:399-404`). The second settled observation therefore reconciles and clears `RepairRequired` (`:841-848`). This depends on literal status strings. A transient failure still persists `RepairRequired=true` immediately, which blocks the helper until recovery (L21). |
| 5   | Artwork-only manifest write  | **Fixed, residual**                                    | `GameLibraryService.cs:2109` guards on `!artworkOnly`. Non-artwork **Update** actions with unchanged content still republish the manifest and need a writable card (M2).                                                                                                                                                                                                                                                       |

## Confirmed findings

### High

**H1. Every volume-bound launch now runs through `\\?\Volume{GUID}\` paths, legacy fixed-drive
titles included**

- Where: `src/Shared/Library/ManagedContentModels.cs:549` (`Validate` → `primary` → `check.Path`)
  and `:701-702` (`Check` → `ProgramPath`/`WorkingDirectoryPath`). These are consumed by
  `src/WSGM.PackagedLaunch/Session/ManagedContentLaunch.cs:91-110` (process executable,
  `WorkingDirectory`, `{content}` substitution) and by
  `EmulatorStorage.Resolve(..., checkedContent.Path, ...)` for ROMs.
- Trigger: launch any managed ROM, folder or manual title whose paths carry a `VolumeId`.
  `CapturePath` assigns one to every local volume, so this covers C:/D: legacy records as well as
  removable storage.
- Consequence: the emulator receives `\\?\Volume{…}\roms\x.gb`. PC games start with argv[0] and the
  current directory as a volume GUID path. `cmd.exe` (bat/cmd shortcuts) cannot use such a current
  directory, many emulators and cores and MAX_PATH-era games mis-parse it, and saves or config
  resolved relative to the exe can land in unexpected places. This is a regression for titles that
  launched fine before the PR. The plan only asks for GUID paths for deletion-authorizing queries
  and manifest I/O (plan lines 106-107, 176).
- Fix: keep `ResolveBoundPath`/GUID only for evidence checks. For launch, return the mount-letter
  path from `ResolvePath` after confirming that its mount point maps to the expected volume GUID (as
  before), and run the reparse and containment checks on the GUID path.

**H2. Unresolvable legacy receipts permanently block titles and all emulator shortcut sync**

- Where: `ImportStateStore.cs:134-165` (`BindLegacyAccount` binds only an exact live match), `:688`
  (projection flag), `ImportPlan.cs:438-447` (Skip forever), `EmulatorShortcutSync.cs:130-133`
  (throws for the whole sync).
- Trigger: upgrade with v1 records whose shortcut is absent from the active account. Examples:
  titles the user deleted in Steam, which v1 deliberately kept as "You deleted this title's shortcut
  from Steam. Tick it to add it again" (`ImportPlan.cs:360`, `:693`, `:765`), or shortcuts that live
  in a different Steam account on a shared device.
- Consequence: those titles show "Waiting for an exact Steam account observation" on every scan and
  can never be re-added or cleaned up. Any such emulator receipt makes `ReconcileAsync` throw
  "Restore its recorded command and sync again" for every emulator, and the user cannot restore a
  deleted shortcut's command. Nothing in the UI resolves the state.
- Fix: in the active account, treat an unresolved legacy receipt whose AppId is absent from a
  confirmed full snapshot as "not imported here". Allow Add (with `ReplacedAppId`) while preserving
  the unresolved receipt for other accounts. For emulator sync, refuse only the specific
  installation whose recorded shortcut exists but was edited. Optionally add an explicit "forget
  legacy record" action on both surfaces.

### Medium

**M1. One malformed v1 row now bricks the whole device index instead of being dropped**

- Where: `ImportStateStore.cs:770-790` (v1 migration wraps every legacy row into a mapping) and
  `:959-986` (`EntryDefect` throws `ImportStateException` on any `MappingDefect`).
- Trigger: a v1 file containing a row the old `EntryDefect` dropped with a warning, such as an
  unknown/empty `Mode`, a JSON null `Target`/`Route`/`StartDirectory`, or an incomplete
  `PendingLaunch`.
- Consequence: `Read()` throws on every call, the file is not quarantined, and every Game Library
  scan, apply, recheck and emulator sync fails with "index was left unchanged". Before the PR only
  that row was lost.
- Fix: during v1 migration, apply the old per-row drop policy (log and skip) before building
  mappings. Reserve the throw for v2 receipts.

**M2. `PublishImportedContent` failures abort the whole Apply, and Updates with unchanged content
still require a card write**

- Where: `GameLibraryService.cs:2109-2112`. There is no per-entry catch around it inside the
  selected loop (`:2044-2276`). Callee: `GameLibraryService.Storage.cs:475-537`.
- Trigger: one selected title on a write-protected or unreadable card, or a manifest match conflict,
  in a batch with other titles. Or a mode/route-only Update of an existing card title on a locked
  card.
- Consequence: `Run` catches and fails the entire run with a raw message such as "The media is write
  protected.". Remaining selected titles, including ones on internal storage, are not applied. Every
  Update also bumps the manifest revision needlessly. The plan expects a clear Unlock/Retry import
  failure for that title, and existing imports to stay usable.
- Fix: publish only for Add, for `ContentChanged`, or when the entry is missing from the manifest.
  Catch `StorageFailure` per entry and record it as that entry's problem with Unlock/Retry wording.

**M3. Cleanup removal can be cancelled after dispatch, leaving a permanently stuck cleanup**

- Where: `GameLibraryService.Storage.cs:975-982` (`MarkDeletionDispatched`, then
  `writer.RemoveAsync(entry.AppId, token)`).
- Trigger: `token` is linked to `_storageObservation`, which every `Begin()` cancels
  (`GameLibraryService.cs:2896`). Starting a scan or apply, or shutdown, while the monitor is
  removing a pruned shortcut cancels the in-flight write.
- Consequence: the outcome is unknown and `DeletionDispatched=true` is persisted. Every later
  reconcile reports "Steam did not confirm a previous cleanup…" until the user deletes the shortcut
  manually.
- Fix: once dispatch is admitted, pass `CancellationToken.None` to the write (as
  `EmulatorShortcutSync` does for Add), or avoid cancelling the lane holder during a dispatched
  write.

**M4. Staged storage actions can become impossible to clear and block every Apply**

- Where: `GameLibraryService.Storage.cs:539-582` (stage), `:584-676` (apply throws), `:212-219`
  (refresh keeps `_storageActions`).
- Trigger: (a) stage "Use this card" for a library that is not yet known, then remove the card. Its
  row disappears from `_storagePublished`, so `StageStorageActionAsync(id, "")` returns "no longer
  listed" while the action stays in `_storageActions`. (b) Stage Repair, after which the manifest
  becomes readable again: `Repair` throws "This manifest is valid".
- Consequence: every Apply throws in `ApplyStorageActionsAsync` before any title is written, for the
  rest of the session. `_stagedStorage[id]` (`:599`) is also read outside the lock.
- Fix: in `RefreshStorage`, drop staged actions whose preconditions no longer hold (`CanRepair`,
  `CanAcceptBinding`, candidate present). Report a failed storage action as a problem instead of
  aborting the run.

**M5. Accepting a new binding strands titles missing from the manifest, and that breaks library
reconcile**

- Where: `GameLibraryService.Storage.cs:614-636` (entries without a manifest recipe are skipped; the
  library's `VolumeId` changes anyway), `:780-783` (`FromManaged` throws "backing content belongs to
  different storage").
- Trigger: accept a library on a new volume while some locally known titles of that library are
  missing from the card's manifest, for example because another device pruned them or the manifest
  was repaired.
- Consequence: those titles keep the old `VolumeId`. The helper refuses them ("binding needs
  review"), the UI says to review a binding that is no longer offered, and every reconcile of the
  library throws, so no pruning, manifest update or checkpoint ever happens for any title of that
  library.
- Fix: during acceptance, rebind every title with that `LibraryId` and let the next reconcile prune
  the missing ones. Also make reconcile handle a per-entry `FromManaged` failure without failing the
  whole library.

**M6. `MigrateStorage` rewrites the index on every refresh and overwrites the accepted revision
without validation**

- Where: `GameLibraryService.Storage.cs:250-327`.
- Trigger: an existing library record plus at least one legacy (`LibraryId` empty) portable entry on
  that volume that is absent from the manifest. Such an entry stays legacy forever, because only
  matching ids migrate.
- Consequence: every `RefreshStorage` re-reads the manifest, sets `ManifestRevision` to whatever the
  card says, and writes `library-import.json` durably twice (`SaveLibrary` with
  `MigrationPending=true`, then `false`). Refresh runs on every scan, apply, hotplug observation,
  recheck and the 5-minute check. Accepting the card's revision silently defeats the device's
  expected-revision checkpoint. `_notes` also gains "Library migration is pending" on each failing
  refresh (`:323`).
- Fix: migrate only when no library record exists or `MigrationPending` is set. Publish missing
  legacy entries through the normal revisioned publication. Skip the writes when nothing migrated.

**M7. Local-recipe dependencies are rejected for any reparse point, including off-card paths**

- Where: `ManagedContentModels.cs:703-722`. `RejectLibraryReparsePoints` runs on
  `Program`/`WorkingDirectory` whenever `LibraryId` is set, whatever volume the dependency is on.
- Trigger: a card title whose local recipe or working directory is on C: under a junction, a
  symlinked games folder, or a OneDrive or Cloud Files placeholder path (these carry
  `FileAttributes.ReparsePoint`).
- Consequence: the launch is refused as Unreadable ("contains a reparse point"). The plan only
  rejects reparse points in manifest-derived segments.
- Fix: apply the reparse check only when the dependency's `VolumeId` equals the library's bound
  volume.

**M8. Collection sync now throws exceptions the callers do not catch**

- Where: `GameLibraryService.cs:2321-2334` (`_readLibrary`, `CaptureAccount`, `_readCollections`,
  `RequireAccount` can throw `InvalidOperationException`). Only `ImportStateException` is caught
  (`:2431`).
- Trigger: Steam unreachable or an account change at the end of an apply, or toggling "Create
  collections" (`:657` fire-and-forget `Task.Run`).
- Consequence: an apply whose shortcut writes all succeeded is reported as failed and skips the Done
  bookkeeping. The toggle path ends in an unobserved task exception with no user-visible error.
- Fix: catch `InvalidOperationException` (and the other `StorageFailure` types) inside
  `SyncCollectionsAsync` and add them to `problems`.

**M9. Legacy collections that the user edited or deleted can never bind, and every apply reports a
problem**

- Where: `ImportStateStore.cs:169-193` (requires `collection.AppIds.All(observed.Contains)`),
  `GameLibraryService.cs:2377-2381`.
- Trigger: before the upgrade, the user removed one title from a WSGM collection or deleted the
  collection.
- Consequence: the group is skipped permanently with "legacy ownership could not be confirmed", and
  the user has no recovery action.
- Fix: bind on an exact collection id observed in the confirmed account (membership differences then
  reconcile normally). If the id is absent from a confirmed read, settle the legacy receipt so the
  group can be recreated.

**M10. Manifest forward compatibility: an additive v1 field triggers Repair, which overwrites the
newer manifest**

- Where: `StorageLibraryModels.cs:112` (`UnmappedMemberHandling.Disallow`),
  `GameLibraryService.Storage.cs:154-164` (`JsonException`/`InvalidDataException` sets
  `RepairRequired` and enables `CanRepair`), `StorageLibraryStorage.cs:76-140`.
- Trigger: a future WSGM adds an optional property while keeping `Version: 1`, and an older device
  reads the card.
- Consequence: the older device refuses every launch from that card (helper checks `RepairRequired`)
  and offers "Repair library". Repair replaces the manifest with only that device's known titles. A
  backup is kept, but other devices' entries and fields drop out of the live manifest.
- Fix: treat unknown members in a present, well-formed v1 file as "Update WSGM", the same as a
  version mismatch, and never offer repair for it. Alternatively, document that any schema addition
  must bump `Version`.

### Low

- **L1** `GameLibraryService.Storage.cs:146`: for an unknown card with an unsupported manifest
  version, `record.Content?.LibraryId == expected?.Id` is `null == null` for every entry without
  content. The row then shows the count of Xbox, launcher and other non-managed titles. Use
  `expected is null ? 0 : …`.
- **L2** Legacy records persisted with `.` segments (old `BindPath`) now differ in `SameLaunch` from
  freshly normalized discoveries. After upgrade, those titles show spurious `ContentChanged` Update
  rows. `CapturePath` (`ManagedContentModels.cs:~402`) still emits `.` for a volume root. Normalize
  on read/compare and in `CapturePath`.
- **L3** `ResolveLibraryPath` (`ManagedContentModels.cs:506`) rejects segments ending in a space or
  dot. Because `ResolveBoundPath` now covers fixed-drive legacy records too, files that previously
  launched (possible on exFAT/NTFS through other tools) now report Unreadable.
- **L4** Manifest entry ids are only checked for 1-128 characters (`StorageLibraryStorage.cs:329`),
  while `ComposeManagedTarget` requires 32 hex characters. A card-authored non-hex id then throws
  `ArgumentException` at plan/apply rather than failing validation with a clear message. Validate
  the id format in `Validate`.
- **L5** `PublishImportedContent` (`:487-529`): importing a locally discovered title on a card that
  carries a foreign, never-accepted manifest creates and accepts that library binding implicitly,
  bypassing the explicit "Use this card" review the plan asks for.
- **L6** Per-title Recheck (`RecheckShortcutAsync`, then `ReconcileStorageAsync(contentId)`)
  reconciles the title's whole library, including pruning other titles and Steam cleanup. That is
  surprising scope and cost for a single-title action from the Steam details page.
- **L7** `Run` (`GameLibraryService.cs:2833-2848`) holds `_storageLane` for the entire scan or
  apply, including artwork network work, so details-page Recheck and hotplug observations wait
  behind it.
- **L8** `CleanRemovedContentAsync` (`:948-986`): a pending-cleanup mapping with `AppId == 0`
  (unconfirmed creation) reads as absent and is forgotten, which can orphan a shortcut that was in
  fact created.
- **L9** `ManagedShortcutMonitor`: the 5-minute timeout path runs a full reconcile without the
  `_emulators.Initialized` gate that `Wake` applies. Early checks can therefore publish
  `EmulatorUnavailable` before the emulator store is ready.
- **L10** The monitor's second settled observation ignores its result
  (`ManagedShortcutMonitor.cs:~119`), so failures are never logged.
- **L11** For legacy fixed-drive records, `ConfirmReadableContentParent` runs the reparse check on
  parents. Missing content under a junction becomes Unreadable instead of ContentMissing.
- **L12** Toolkit `library-badge.ts`: `installedView` recomputes `all()` (a filter over every game)
  on every property access, and `apps` builds a new `Set` each time, so callers that loop can go
  quadratic on large libraries. Once any managed shortcut exists, every element carrying
  `collection` is wrapped, which changes element types and remounts library grids (lost
  focus/scroll). `count()` ignores the tools filter for added apps. The comment at
  `library-badge.ts:750` (and the bundle at `:9573`) still says the button "reads Recheck and sends
  `recheck`", but the code disables it. `classes.text` is unused.
- **L13** `SteamUiSessionHost.cs:747-750`: the availability gate (a claim on Steam's filter
  prototype) installs whenever Library Badges is on, even with no managed shortcuts, which widens
  the patch-risk surface.
- **L14** While the account is unknown, `Entries()` projects `AppId = 0`, so `UpdateShortcuts`
  publishes nothing and unavailable shortcuts briefly look launchable until the next account read.
- **L15** UI: the Overlay `RenderEntry` → `LeaveRemovedEntry` and Steam `ImportDetailsBody` close
  with "no longer listed" on any transient disappearance, for example while a rescan replaces
  entries. On parity, the Overlay has "Recheck all storage" but Steam has no equivalent. The Steam
  storage sheet lists 12 names as text, while the Overlay offers paged, openable title rows.
- **L16** `ImportStateStore.Forget(source, key)` is a public method that always throws. Remove it or
  mark it `[Obsolete(error: true)]`.
- **L17** `SaveInventories` (`ImportStateStore.cs:262-265`): one entry that reverses
  `InventoryRemoved` aborts the whole batch.
- **L18** `eng/steam-cef-mcp.mjs` imports `chrome-devtools-mcp`'s internal
  `build/src/third_party/index.js`. This is fragile on upgrade and unrelated to the PR's feature.
- **L19** `EmulatorManager.cs:98-115` now also iterates pending-cleanup entries. A disconnected card
  with a pending-cleanup ROM blocks RetroArch save conversion ("Reconnect the ROM media").
- **L20** `RefreshStorage` calls `SaveLibrary` inside its catch block (`:160`). An
  `ImportStateException` there escapes `RefreshStorage` and fails the whole scan, apply or
  reconcile.
- **L21** A transient FileNotFound or JSON error on the manifest during mount persists
  `RepairRequired=true` immediately. The helper then refuses launches from that library until a
  later reconcile clears it.

## Uncertainties (not confirmed)

- H1 severity per app: which emulators and games fail with GUID paths needs a live check, but
  cmd/bat launch and `{content}` consumers are known not to accept `\\?\` current directories.
- Whether `StorageVolumeFacts` record equality is stable across inventory reads (any array member
  would make `TopologyChanged` fire on every refresh).
- Whether `Run`'s lane hold interacts badly with Steam bridge command timeouts while a long scan
  runs.
- Whether Steam's gamepad Installed tab reads `apps`/`allApps` in hot loops (relevant to L12).

## No findings

- `ManagedContentLaunch` helper: reads only the local index, honors `InventoryRemoved`, binding and
  repair, and does not parse card data.
- `StorageLibraryStorage` parser, Publish and Repair mechanics: bounded size, duplicate-property
  rejection, revision checks, atomic durable writes and repair backup are sound apart from M10.
- `ImportAccountMapping`, `SteamShortcutSnapshot`, account capture and serialization through
  `_shortcutOperations`: no self-deadlock, since `AddShortcutAsync` does not re-enter the semaphore.
- `RemovableDriveManager.TopologyChanged`, `RunningApplicationMonitor.IsSteamAppRunningAsync`, the
  `stageStorageAction` payload validation in `SteamLibraryImportSurface`, the
  `LibraryBadges.HasManagedShortcuts` switch re-evaluation, and `SteamLibraryBadgeSurface` patch
  declarations and probe.
- `tests/WSGM.UiTests` fakes, `PreviewTools` and the `tools/OverlayPreview` storage-cases hook.

## Validation gaps

- No unit tests cover the v1 to v2 index migration, `BindLegacyAccount`/`BindLegacyCollections`, the
  manifest parser and Publish/Repair, `ReconcileStorageAsync` pruning and cleanup, or acceptBinding.
  The PR adds UI preview fakes only.
- The uncommitted fixes were not compiled or tested in this review.
- No live Steam, Big Picture, card hotplug or hardware evidence was examined. Toolkit gate behavior
  depends on the 2026-10-20 `sp.js` structure.
- Git warns that LF will be replaced by CRLF in the working copies of `ImportStateStore.cs` and
  `StorageLibraryStorage.cs`.
