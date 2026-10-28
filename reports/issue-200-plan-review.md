# Claude review of the issue #200 implementation plan

Reviewer: Claude Code, model `claude-opus-5-5`. Session: `c9270e5c-6dc8-499c-91a6-fc413c1ca0d3`.
Date: 2026-10-28. Read-only review against the initial plan draft and repository source.

The original findings below refer to the initial draft's line numbers. See the
[plan's review resolution](../docs/plans/removable-shortcut-availability.md#review) for accepted
corrections and maintainer-directed exceptions. This report does not establish implementation, live
UI or hardware acceptance.

---

# Review: removable non-Steam content plan

**Verdict: changes required.** The direction is right, and most of it reuses the correct owners.
Several points can't be implemented as written against the current code, though, and a few of them
could lead to deleting the wrong shortcut or a duplicate identity. None of the fixes needs a new
framework.

## Code inspected

- Guides: root `AGENTS.md`, `.agents/skills/wsgm/SKILL.md`, and the nearer `src/WSGM`,
  `src/WSGM/Core`, `src/WSGM/Shell` and `src/WSGM.PackagedLaunch` guides.
- `src/Shared/Library/ManagedContentModels.cs` (all of it), `src/Shared/Library/UserDataContext.cs`,
  and the shared-source links in `WSGM.csproj` and `WSGM.PackagedLaunch.csproj`.
- `src/WSGM/Core/Library/ImportStateStore.cs` (all of it), `ImportPlan.cs` (`ImportedEntry`,
  `ImportChoice`, plan/removal logic 360–552), `GameLibraryConfig.cs`,
  `Sources/ShortcutFolderSource.cs` (all of it), `SteamShortcutWriter.cs`, and the
  `RomLibrarySource.cs` record construction.
- `src/WSGM/Shell/GameLibraryService.cs` (apply/remove 1971–2060) and `GameLibraryService.Roms.cs`
  (recheck/apply-availability/badge publication 240–500).
- `ManagedShortcutMonitor.cs` (all of it), `ShellSession.GameLibrary.cs` (shortcut read/add),
  `LibraryBadges.cs` (all of it), the `SteamUiSessionHost.cs` badge wiring, and grep-level checks of
  `EmulatorShortcutSync.cs` and `CardVolumeMonitor.cs`.
- `src/WSGM/Interop/ShellLink.cs` (resolve flags and fields), and
  `src/WSGM.PackagedLaunch/Session/ManagedContentLaunch.cs` (all of it).
- In the toolkit: `SteamLibraryBadgeSurface.cs` (contract) and
  `SteamUiAssets/Source/gates/library-badge.ts` (state parsing and the details stat).
- `SteamArtwork.cs`, for the existing `ReadAccountIdAsync` use.
- `docs/decisions.md:281-288` and `docs/sd-cards.md` (marker naming).

I did not read the Overlay views in depth. I only counted their availability and recheck usage.

## Findings by severity

### High (actual defects)

**H1. Steam account scoping doesn't exist today. Migration as written would guess, and a "confirmed
absence" read could be from the wrong account.**

- **Plan:** lines 97–102, 180–181, step 3 (line 299).
- **Code:**
  - `ImportStateStore` lives in the per-Windows-user root (`UserDataContext.ForCurrentUser`,
    `%LOCALAPPDATA%\WSGM`).
  - Neither `ImportedEntry` nor `ImportedCollection` has an account field. The only account read in
    the app is `SteamArtwork.cs:376` (`steam.Apps.ReadAccountIdAsync`).
  - `ShellSession.GameLibrary.cs:114-118` treats "Steam answered without details" as "no shortcut
    with that id any more".
  - `GameLibraryService.cs:2024-2037` then forgets the record when `live is null`.
- **Problem:** If Steam is signed in to account B, account A's shortcut reads as absent. The plan's
  "independent read confirms absence" would clear the record, and the automatic cleanup lane makes
  this happen without the user asking.
- **Minimal fix:**
  - Add a recorded account id to the entry and to `ImportedCollection`, since Steam collections are
    per account too.
  - Assign the account to a legacy record only when an exact `OwnsRecorded` match is observed in the
    active account. Until then, keep it "account unknown" and never let it count as confirmation.
  - Read the active account (reuse `ReadAccountIdAsync`) before and after any cleanup read or write.
    If the account changed, abandon.
  - A shortcut that is absent in an account other than the recorded one is not absence.

**H2. Content IDs are derived from device-local source IDs, so a "stable content ID" in a portable
manifest isn't portable.**

- **Plan:** lines 65, 278, 286–288; table row "Second WSGM device".
- **Code:** `ManagedContentStorage.ContentId(sourceId, key)` hashes the configured source id:
  - `folder:<token>` (`GameLibraryConfig.cs:22`),
  - the ROM source `Id`, or
  - `"manual"` plus the entry id (`RomLibrarySource.cs:434,444,651`).
- **Problem:** Device B, or device A's own ROM source pointed at the same card, will compute a
  different id for the same file. The result is duplicate manifest entries, or a merge the plan
  forbids. The plan also never says which `ILibrarySource` turns a manifest into `DiscoveredGame`s
  on device B.
- **Minimal fix:**
  - Treat the manifest entry id as authoritative once the entry exists. A device importing from a
    manifest reuses that id as its `ContentRecord.Id`, and that id goes into `--managed <id>`.
  - Dedupe manifest entries by (library UUID, volume-relative backing path, system id or program+raw
    arguments). This mirrors `ImportPlan.ContentIdentity`, but without the per-machine volume GUID.
  - Name the one small manifest-backed source that offers a manifest's entries in Library Manager.

**H3. Rebinding a known library UUID to a new volume GUID is unspecified, and the UUID is authored
by the card.**

- **Plan:** lines 77–81, step 5 (line 301).
- **Code:** Each `ManagedContentPath.VolumeId` is a local volume GUID. Volume GUIDs are assigned per
  machine and change when a card is copied to new media. `ManagedContentLaunch` runs whatever
  `Check` resolves on that volume.
- **Problem:** If WSGM rebinds automatically whenever a volume claims a known UUID, any card that
  copies `.wsgm/library.json` and plants an exe at the same relative path takes over the existing
  Steam shortcuts. The plan only says "observed and reconciled", which names no action and no owner.
- **Minimal fix:**
  - Accepting a new volume binding for a known library should be an explicit user confirmation in
    Library Manager, on both surfaces. After confirmation, WSGM rewrites the `VolumeId` on that
    library's records.
  - Until then, the titles stay "card absent".
  - The launch helper should keep resolving only the local `VolumeId`, and should never read card
    manifests. That keeps step 5 small and removes untrusted parsing from the helper.

**H4. Location and other new fields take part in `SameLaunch`, so the migration would make every
legacy record look changed.**

- **Plan:** "labelled with the expected library" (line 17), migration step 2 (lines 278–279).
- **Code:**
  - `ManagedContentStorage.SameLaunch` compares `Location` (`ManagedContentModels.cs:599`).
  - `ImportStateStore.UpdateAvailability` and `SaveApplied`, and
    `GameLibraryService.Roms.cs:426-429`, gate on `SameLaunch`.
  - For `.lnk` titles, `Location` is currently the shortcut folder's display name
    (`ShortcutFolderSource.cs:196-197`, `DisplayName`), for example "Game Shortcuts" rather than the
    card. ROM records use the source or manual label.
- **Problem:** Relabelling records, or adding a library UUID field that `SameLaunch` compares, turns
  every migrated record into a launch change. That produces Update plans and skipped availability
  updates.
- **Minimal fix:** Keep the library name and UUID on the device-index library record and join them
  at projection time. Alternatively, exclude display-only fields from `SameLaunch`. State which
  approach is used and add it to the migration test list.

**H5. Upgrading would automatically prune titles that the current rule deliberately kept.**

- **Plan:** migration step 3 (lines 280–282) together with the table row "Manifest entry removed and
  its cached backing files confirmed missing → prune".
- **Code:** `ImportPlan.cs:456-462` currently keeps missing ROM, Folder and Manual titles.
  `docs/decisions.md:287` says missing content stays visible.
- **Problem:** If the legacy manifest is initialized from _verified_ records, every currently
  missing title is pruned on the first reconciliation after upgrade.
- **Fix:** This may be what the maintainer wants, but the plan has to state it as an intended
  first-run transition with its own attended scenario. It also has to define what "controlled
  migration operation" means: an explicit user action, or automatic at startup. The two give very
  different blast radii.

### Medium (actual defects or omissions)

**M1. "Participating volume" is undefined, and the system volume is a problem.**

- **Plan:** lines 55–58 and 289–291.
- **Problem:** A folder import on `C:` targeting `C:` games would put `.wsgm` at the root of `C:`.
  That needs elevation and is unwanted.
- **Fix:**
  - Participate only volumes that are not the system volume, and that have a volume GUID and a local
    filesystem. Reuse the removable and hotplug classification from `docs/sd-cards.md` if you want
    "card only".
  - Say explicitly that `C:` titles keep the current keep-until-cleanup contract.
  - Separately, a read-only or unwritable volume must not block import. Either import local-only (no
    manifest) or refuse clearly. Lines 177 and 169–170 currently imply a regression for
    write-protected cards.

**M2. The checks query through drive letters, which is a letter-reuse race.**

- **Plan:** line 83 applies the volume GUID path to manifest I/O only.
- **Code:** `ResolvePath` returns a drive-letter mount, and `ManagedContentCheckContext.Mounts`
  caches it for the whole batch (`ManagedContentModels.cs:375-379`). `Validate` maps
  `FileNotFound`/`DirectoryNotFound` to `ContentMissing`.
- **Problem:** If the card is swapped during a batch, another card at the same letter yields
  "missing" for every title.
- **Fix:** Run the existence queries for the deletion decision against `\\?\Volume{GUID}\relative`.
  Then step 4's revalidation is a backstop rather than the only guard. The Shell guide already
  requires this ("never validate on a letter and then write to it").

**M3. A missing configured source root would be read as a mass deletion.**

- **Plan:** lines 148–151.
- **Problem:** Renaming `ROMS` makes every ROM title "confirmed missing" under a readable ancestor.
- **Fix:** Classify a missing `SourceRoot` as _source unavailable_. This reuses
  `ImportSourceState.Gone`; it is not relinking. Only a missing title under a present root counts as
  deletion. This stays within the maintainer's rule that moving a game deletes its entry.

**M4. Manifest disagreement has no terminal rule.**

- **Plan:** table row line 140, versus line 104 ("card owns the portable inventory").
- **Problem:** "Keep cached entry and report disagreement" stays unresolved forever. Nothing in the
  plan lets a user remove a title from the inventory while the files remain. A lower card revision
  than the last accepted one (restored backup, older device) is also unhandled.
- **Fix:** Pick one rule. The simplest consistent one: the card wins. A title removed from the
  manifest is forgotten locally, and its owned shortcut is removed, only when the files are
  confirmed missing. Otherwise the entry is re-published to the manifest from the index, as a
  repair. A revision rollback triggers a fresh check and is never pruned on its own.

**M5. Edited, account-mismatched or Steam-deleted shortcuts have no terminal state.**

- **Plan:** table row line 144, lines 187–189.
- **Code:** `OwnsRecorded` compares Target, LaunchOptions and StartDirectory, but not Name
  (`ImportPlan.cs:546-552`).
- **Problem:** With the manifest already pruned, an edited shortcut leaves a permanent "cleanup
  pending" row.
- **Fix:**
  - On a confirmed deletion with an edited shortcut, release WSGM's ownership record and leave the
    user's shortcut in place. Or surface it once through the existing `ImportAction.Conflict`
    explicit cleanup.
  - State that a rename alone is not an edit.
  - If a shortcut was deleted in Steam while its files are present, forget the local mapping, keep
    the manifest entry, and let Library Manager offer it as Add.

**M6. Not every kind is excluded from automatic pruning.**

- **Plan:** line 291 names Xbox and launcher only.
- **Code:**
  - `EmulatorShortcutSync` writes `SourceKind.Emulator` records into the same store
    (`EmulatorShortcutSync.cs:164-240`).
  - Folder `.url` titles are `LaunchKind.Native` with `BackingPath` set to the `.url` file itself
    (`ShortcutFolderSource.cs:206-210`).
- **Fix:**
  - Prune only `Rom`, `Folder`+`Direct` and `Manual` records that belong to a participating library.
  - A deleted `.url` must not count as deleted game content, by the same reasoning as plan line 153.
  - `EmulatorShortcutSync` is also a separate writer of store records and Steam shortcuts. The
    "single serialized lane" must say whether it is included or why it is safe outside.

**M7. Steam's launch affordance is only visual today.**

- **Plan:** lines 261–265.
- **Code:** `library-badge.ts:546-584` renders a stat with "Unavailable" and Recheck, and leaves
  Play alone. The badge module and details patch are both gated by `_switches.LibraryBadge`
  (`SteamUiSessionHost.cs:1113,1705`). The contract is
  `SteamShortcutAvailability(AppId, bool Available, Location, Reason)`.
- **Fix:**
  - State that enforcement is the helper refusal and the presentation is reason text. The "check
    failed" and "pending" states then fit the existing bool + reason.
  - Make the shortcut-availability part of the badge publication ignore the badge switch, or add a
    separate always-on switch for it.
  - Don't promise disabling Play unless you scope that patch.

**M8. Running-title deferral has no named source.**

- **Plan:** line 161.
- **Fix:** Name the owner. `RunningApplicationMonitor`/`SteamRunningAppPairing` in
  `Shell/RunningApplicationTarget.cs` is the likely one. If running state is unknown, defer.

**M9. Library naming would compete with the card marker.**

- **Plan:** lines 63 and 94.
- **Code:** The Shell guide and `docs/sd-cards.md:17-48` make the Steam `libraryfolder.vdf` marker
  label the card's only name. `LibraryBadges.Build` names cards from `CardLibraries`.
- **Problem:** A separate manifest "friendly name" lets the same card show two names. The test card
  D: already has a `SteamLibrary`.
- **Fix:** Derive the managed library's display name from the marker label when one exists, falling
  back to the volume label. Do not add a third editable name.

### Low (actual, minor)

- **L1. Arguments aren't stored verbatim.** `ShortcutFolderSource.Direct` stores `arguments.Trim()`
  (line 287), which contradicts "verbatim" (plan lines 69 and 231). Keep the trim and say so, or
  remove it.
- **L2. The working directory's role in classification is unstated.** In `Check`, a missing program
  or working directory yields `EmulatorUnavailable` (`ManagedContentModels.cs:549-556`). The plan
  should say that a missing working directory is a dependency failure, not a deletion. Note also
  that `ShellLink` expands environment variables in Start in (`ShellLink.cs:114`), so
  `%USERPROFILE%`-style directories become local `C:` dependencies.
- **L3. Scan and reconciliation must not become two deletion paths.** The plan says nothing about
  `ImportPlan`'s removal branch. Keep scans non-destructive (the current `Skip` at
  `ImportPlan.cs:456-462`) so deletion happens only in reconciliation.
- **L4. Rollback after the schema bump.** `ImportState.CurrentVersion` is tied to
  `ManagedContentStorage.FormatVersion`. An older build refuses a newer file
  (`ImportStateStore.cs:477-481`, `ManagedContentModels.cs:281-285`). "Preserve previous local
  state" should mean a concrete one-time backup of the v1 file before the first v2 write. Also,
  `Sanitize` silently persists dropped rows, so new fields need defaults that load under
  `EntryDefect`/`RecordDefect`.
- **L5. Manifest parsing hardening.** Cap the manifest size, and reject reparse points in
  manifest-derived path segments. Card-authored relative paths through a junction can make a "not
  found" result describe a different volume. `ShortcutFolderSource.Walk` already skips reparse
  points; mirror that rule.
- **L6. Lines 165–167, cross-process serialization.** The helper doesn't write
  `library-import.json`; it only reads it, under `EmulatorStorage.TryAcquireGate`. Cross-process
  writer serialization is therefore moot unless the plan adds helper writes. Drop that clause, or
  name the actual second writer.

## Optional improvements (not defects)

- The separate "successful validation timestamp" (lines 95 and 209) has no consumer in the plan.
  Drop it unless the UI shows it.
- Reuse `CardVolumeMonitor.SettleDelay` and `Kick` for arrival settling instead of a new delay.
- `ManagedEntriesChanged` (fired by pruning) re-requests a full recheck through
  `ManagedShortcutMonitor.Request`. It's harmless, but a generation check avoids the extra pass.
- Name the existing cleanup helpers explicitly: `ReleaseControllerTargetAsync(appId, OwnsProfile)`
  (`GameLibraryService.cs:2035`) and the `_collectionSync` collection sync.

## Acceptance gaps

Add these attended scenarios:

- First run after upgrade with already-missing legacy titles (H5).
- Steam account switch while a card is attached and while cleanup is pending (H1).
- A card copied to new media, or the same card on a second PC: binding confirmation (H2, H3).
- ROM root folder renamed (M3).
- Write-protected card import (M1).
- Badges switch off while a title is unavailable (M7).
- The details page open while its title is auto-removed.
- An edited or renamed shortcut on a confirmed deletion (M5).
- Deleted `.url` and deleted emulator-interface shortcut (M6).

The validation section otherwise follows the budget: manual-first, one build, `dev-deploy`, no full
gate. Note that a toolkit TS change needs the toolkit asset rebuild and a submodule push before the
gitlink.

---

# Focused re-review: revised `docs/plans/removable-shortcut-availability.md`

**Verdict: changes required. They are small and local, not a redesign.** All five high-severity
findings from the first review are resolved in intent. The maintainer's decisions are reflected
correctly:

- Renamed or deleted roots are pruned once absence is confirmed.
- New volume bindings are accepted through the existing review/Apply.
- Card inventory is account-independent, while Steam mappings are account-scoped.
- The scoped CEF primary-action, presentation and filter work is required, independent of the badge
  switch and backed by the launch helper. The current badge/stat does not count toward it.

Three concrete gaps remain. In each, the plan relies on store or workflow behaviour that the current
code doesn't provide.

I read the whole revised plan and re-checked `ImportPlan.cs` (`Removal`, `EditedByHand`), the
Conflict handling in `GameLibraryService.cs`, and the `(Source, Key)` keying in
`ImportStateStore.cs`, which I had already inspected.

## Resolution of the first review

| First finding                                      | Status                                                                                                                                                                                         |
| -------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| H1 account scoping                                 | Resolved in policy (lines 123–129, 166–167, 183). The data shape is still unresolved: see R1.                                                                                                  |
| H2 portable content IDs                            | Resolved (lines 87–95): the manifest ID is authoritative, matching is by recipe, and `ManifestLibrarySource` feeds the existing pipeline.                                                      |
| H3 rebinding                                       | Resolved (lines 97–103, 340–342): review/Apply only, and the helper never parses manifests.                                                                                                    |
| H4 `SameLaunch` and labels                         | Resolved (lines 131–134).                                                                                                                                                                      |
| H5 first-run pruning                               | Resolved (migration step 3, lines 383–391; scenario line 447).                                                                                                                                 |
| M1 participating volumes and write-protected cards | Resolved (lines 61–65, 446).                                                                                                                                                                   |
| M2 volume GUID queries                             | Resolved (lines 105–110, 158–159).                                                                                                                                                             |
| M3 missing root                                    | Correctly left out, per the maintainer (lines 191–195, 504–507).                                                                                                                               |
| M4 manifest disagreement and rollback              | Resolved (lines 178, 197–201). One wording contradiction remains: see R4.                                                                                                                      |
| M5 edited and Steam-deleted shortcuts              | The Steam-deleted case and rename-only are resolved (lines 213, 242–246). The edited case relies on a workflow that doesn't exist: see R2.                                                     |
| M6 exclusions                                      | Resolved (lines 143–147, 398–400).                                                                                                                                                             |
| M7 Steam presentation                              | Superseded by the required CEF scope (lines 323–338).                                                                                                                                          |
| M8 running titles                                  | Resolved (lines 210–213).                                                                                                                                                                      |
| M9 naming                                          | Resolved (lines 69–72).                                                                                                                                                                        |
| L1–L6                                              | Resolved (argument trim, working directory as a dependency, scans kept non-destructive, v1 backup and default audit, 32 MiB/100k parse bounds with reparse-point rejection, helper read-only). |

## Remaining findings

**R1. Account-scoped mappings conflict with the store's `(Source, Key)` identity. This is an
implementation blocker.**

- **Plan:** Device index (lines 120, 123–129), Interrupted operations (lines 244–246).
- **Code:** Every `ImportStateStore` mutation keys on `(Source, Key)`. `Replace` removes the
  existing record, and `Forget` removes the entry _and its choices_. `ImportPlan` and
  `ApplyCoreAsync` build `records` dictionaries on the same identity.
- **Problem:** A second account's import of the same title would overwrite the first account's
  mapping. Forgetting account B's mapping (line 245) would delete account A's mapping and the shared
  per-title choices. A scan in account B would also see A's AppId as "no longer in Steam"
  (`ImportPlan.cs:470-477`).
- **Minimal fix:** State the data shape:
  - Each entry keeps one content record and one set of choices, plus a small list of per-account
    mappings (account id, AppId, recorded Target/LaunchOptions/StartDirectory, confirmed and pending
    fields).
  - `Forget`, `Save` and `SaveApplied` act on the active account's mapping only.
  - The content record is dropped only when the inventory prune happens or when no mapping and no
    manifest entry remain.
  - Planning and apply see only the active account's mappings.
  - The helper still looks up content by ID only.

  Alternatively, include the account in the entry identity and say how choices are shared. Either
  works, but the plan has to pick one.

**R2. The "existing Conflict workflow" can't settle ownership.**

- **Plan:** table row line 182; lines 213–215; scenario line 454.
- **Code:**
  - A removal Conflict is created as `Removal(record, ImportAction.Conflict, EditedByHand, false)`.
    It is not selectable, with the reason "left alone".
  - `SetCleanupAsync` refuses Conflict entries.
  - `GameLibraryService.cs:1890` and `:3092` exclude Conflict from cleanup and editing.
  - Lines 247–249 also keep scans non-destructive for missing managed entries, so a pruned title
    would never reach that branch anyway.
- **Problem:** After content deletion, an edited shortcut leaves a permanent local record with no
  action that resolves it.
- **Minimal fix (no new workflow):** When the manifest entry is pruned and the live shortcut's
  command no longer matches, release WSGM's local mapping for that account and leave the user's
  shortcut untouched in Steam. This is a local-only change, consistent with "never touched without
  confirmation", and it needs no extra UI. Update line 182 and scenario line 454 to match.

**R3. A registered library whose manifest is lost has no recovery path.**

- **Plan:** migration step 3 (lines 390–391), table row line 181, lines 200–201.
- **Problem:** The plan forbids bootstrapping from cache and forbids treating the library as empty.
  That leaves a deleted or corrupt `.wsgm/library.json` as a permanent error state. The UI rules
  require recovery states.
- **Minimal fix:** Reuse the step-3 initialization, but trigger it explicitly. Library Manager's
  existing review/Apply offers to re-publish the manifest from the known local records for that
  library UUID and volume binding. This is the same mechanism as the new-binding acceptance, so it
  is not a wizard. After that, the next check makes the deletion decisions as usual. Add one
  scenario row.

**R4. Repair contradicts "no recreation" (wording).**

- **Plan:** line 239–240 says a changed revision must not lead to "recreation of records another
  writer removed". Lines 178 and 198–199 require exactly that recreation when the files still exist.
- **Fix:** Qualify line 240: "except the file-present repair of previously managed records described
  above". The behaviour itself is consistent, because entries are removed only after confirmed
  absence.

**R5. Filter semantics are undefined (acceptance definition, low).**

- **Plan:** lines 326 and 459 ("intended filter semantics").
- **Fix:** State the intent so the scenario is testable. For example: an unavailable managed
  shortcut is treated as not installed by installed-only filters, and as installed again once
  available.

## Optional (not required)

- Legacy exact-account binding (line 128) can only observe the active account. If both accounts hold
  an identical WSGM shortcut, the first observation wins and the other stays account-unknown. That
  is safe, because it is non-destructive. A one-line note would do.

## Remaining validation gaps

These are beyond the scenarios already listed. All of them still need a live run.

- Two Steam accounts holding mappings for the same title, plus a removal in one account (R1).
- An edited shortcut whose content was deleted: local ownership released, shortcut untouched (R2).
- Manifest deleted or corrupted on a registered card, then recovered through review/Apply (R3).
- The CEF availability work in the live client:
  - the primary-action replacement and its controller activation,
  - installed-only filters,
  - restore after a renderer reload or feature removal,
  - the details page while its title is removed.
- Confirmation that no narrow native installed-state primitive exists, as lines 367–371 already
  require. The offline search is not proof either way.

Nothing here claims implementation or live acceptance. Builds and offline checks can't establish
storage, launch, Steam UI or controller behaviour.
