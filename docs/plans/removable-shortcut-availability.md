# Removable non-Steam content: implementation plan

This plan completes [issue #200](https://github.com/KillerPixelCrew/WSGM/issues/200) through the
existing Game Library. It covers ROMs, portable PC games, Windows shortcut-folder imports and
explicitly library-backed manual commands.

Status: proposed implementation, revised after a full Claude review. No feature code, deployment or
media changes have been made for this plan. Review findings and their disposition are recorded below
and in the linked review report.

## Agreed behavior

- Keep two ledgers: a device index remembers known libraries and their local Steam shortcuts; a
  manifest on the storage describes its managed content.
- Compare the ledgers with actual files on every reconciliation. Neither ledger alone establishes
  availability.
- Disconnected storage keeps its titles visible, unavailable and labelled with the expected library.
  Reinsertion restores available titles without a Steam restart.
- When the correct storage is mounted and readable, confirmed missing backing files mean the title
  is gone. Remove its manifest entry, cached title and owned Steam shortcut automatically. No
  additional confirmation for this agreed behavior.
- Moving a game counts as deleting its old entry. The user can import it again with Library Manager.
  No locate, relink, filename matching or content hashing workflow.
- Read failures, a missing/unreadable manifest, ambiguous identity and missing emulator
  installations do not establish deleted game content.
- Ordinary user shortcuts that are not library-backed keep their existing behavior. Existing Xbox,
  packaged-game and third-party launcher ownership is preserved.

## Current implementation and gaps

The existing implementation already supplies these parts:

| Owner                                                                    | Reuse                                                                                                |
| ------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------- |
| `src/WSGM/Core/Library/ImportStateStore.cs`                              | Device-owned `library-import.json`, detached records, atomic writes and import ownership             |
| `src/Shared/Library/ManagedContentModels.cs`                             | Stable content IDs, volume-bound paths, required companion files, mount resolution and shared checks |
| `src/WSGM/Shell/GameLibraryService.cs` and `.Roms.cs`                    | Discovery, import planning/apply, availability publication and explicit recheck                      |
| `src/WSGM/Shell/ManagedShortcutMonitor.cs`                               | Session-owned startup, volume-change, resume and metadata-change reconciliation                      |
| `src/WSGM/Core/Library/Sources/ShortcutFolderSource.cs`                  | `.lnk` target, arguments and working directory adoption; direct managed launch route                 |
| `src/WSGM/Interop/ShellLink.cs`                                          | Windows shortcut resolution without searching for moved files or rewriting shortcuts                 |
| `src/WSGM.PackagedLaunch/Session/ManagedContentLaunch.cs`                | Launch-time content validation, friendly refusal and explicit Retry                                  |
| `src/WSGM/Shell/LibraryBadges.cs` and toolkit `SteamLibraryBadgeSurface` | Shared availability/location publication, card styling and details-page recheck                      |
| `src/WSGM/Overlay/GameLibraryView.Roms.cs`                               | Existing location, availability and recheck controls                                                 |

Today the authoritative content records are local only. Runtime checks can report missing files, but
do not implement the agreed automatic pruning. Import planning explicitly retains missing
ROM/folder/manual entries. Volume events request a full stored-content check; there is no periodic
recovery in `ManagedShortcutMonitor`. Card greying and location presentation currently belong to the
Library Badges feature. Last successful content validation is not recorded separately.

## Ledger layout and ownership

### Storage manifest

Use one versioned `.wsgm/library.json` at the root of each participating volume. It covers multiple
ROM roots or PC-game folders on that volume. This avoids putting a ledger in the shortcuts folder
when the games it references live on another disk. Create it through import/apply, never merely
because an unknown card was inserted. Leave Steam's `SteamLibrary`, `libraryfolder.vdf`, ACF files
and registrations alone.

For this increment, a participating volume is a local, non-system volume classified as removable or
hot-pluggable by the existing storage inventory. Do not write a new manifest at the root of C: or
broaden the existing disk classification. Fixed-only and network sources retain their current local
import/cleanup contract. New card imports require a writable manifest; a write-protected card gives
a clear import failure with an Unlock/Retry explanation, while existing imports remain checkable.

The manifest contains:

- Schema version, library UUID, cached display name and a monotonically increasing manifest
  revision. Follow the card's own Steam library marker label when present, otherwise its volume
  label; do not introduce another editable card name. Source names such as Retro remain distinct
  grouping metadata.
- Stable content IDs, title names, source type and enough source metadata to offer the titles in
  Library Manager on another device.
- Volume-relative backing paths and declared required companion paths.
- Portable launch intent: ROM system, portable executable/working-directory paths, PC-game arguments
  with the existing outer-whitespace trim and appropriate source grouping. Preserve argument quoting
  and internal spacing.

Do not put Steam account IDs, Steam AppIds, host emulator executable paths, credentials, account
tokens or device-specific input preferences on the card. Resolve ROM emulator/core selection from
the device's existing system preferences. Commands with dependencies outside the content volume
retain their local recipe in the device index; another device must configure/import that recipe
before launch. An environment-expanded Start in path such as `%USERPROFILE%` is a local dependency,
not a portable path to copy to another PC.

Once a manifest entry exists, its content ID is authoritative: reuse it in the local content record
and `--managed` arguments. Never regenerate it from another device's configured source ID. On
migration preserve the existing managed ID; for new records allocate an ID once when publishing the
manifest. Match a source scan to an existing entry using library UUID plus relative backing path and
launch recipe: ROM system for ROMs; relative program/working directory and arguments for PC
commands. Distinct command recipes may legitimately point to the same executable. Multiple matches
are a conflict, not a guessed merge. A small `ManifestLibrarySource` adapter under `Core/Library`
offers a registered card's entries as `DiscoveredGame` records using those existing IDs. It feeds
the current review/apply pipeline; it does not own another importer.

Library UUID is the durable logical identity. Windows volume GUID is a local mount binding, not an
assumed universal identity on every Windows machine. A known UUID on a new volume binding is offered
through Library Manager's existing review/Apply flow on both surfaces. Only that explicit action
accepts the new binding; insertion alone leaves the existing titles unavailable. A first import on
another PC likewise records its own local binding. No separate relink wizard is needed. Two attached
volumes claiming the same UUID are ambiguous: use neither as an automatic replacement, and perform
no pruning against them.

Read/write the manifest and perform deletion-authorizing file queries through the captured volume
GUID path, not a cached drive letter. Revalidate the manifest UUID and expected revision immediately
before publication. Reject escaping relative paths and reparse points in manifest-derived path
segments, including the manifest directory/file; do not allow card data to redirect queries or
writes elsewhere. Bound parser input to 32 MiB and 100,000 entries in schema v1 and report an
explicit limit error rather than partially accepting a larger file.

### Device index

Extend `ImportStateStore` rather than introducing a second device database. Retain the existing
per-user file location and shared launcher lookup contract. Add known library records with:

- Library UUID, cached friendly name, expected/local volume binding and last mount.
- Last accepted manifest revision and successful validation timestamp.
- Cached content metadata, including all information needed to label absent titles.
- Local Steam account ownership and per-title shortcut AppIds/recorded commands.
- Explicit pending cleanup state where Steam or a manifest write has not settled.

Choose a concrete device data shape: one title entry retains `(Source, Key)`, its content record and
shared per-title choices, plus a small `SteamMappings` list. Each mapping stores account ID, AppId,
recorded Target/arguments/StartDirectory, route/mode, confirmation and pending-write/cleanup fields,
artwork receipt and profile ownership. Migrate today's flattened `ImportedEntry` fields into this
list. `Save`, `SaveApplied` and mapping removal operate on one account's mapping, never replace the
whole title or delete its shared choices. Planning/apply materialize only the active account's
mappings; collections are keyed by account plus group. Keep unresolved legacy mappings separately
identifiable until an exact match binds them. An identical legacy shortcut observed in a second
account may be adopted explicitly there; it must not overwrite the first mapping.

Inventory pruning removes the active content from the library, but retains any pending per-account
cleanup receipts until each is settled in that account. These receipts cannot be launched or treated
as present inventory. Drop a title's shared record only after inventory removal and settlement of
its remaining receipts, or when no manifest entry and no mapping remain. Forgetting a user-deleted
shortcut removes only that mapping. The helper continues looking up accepted content by ID without
consulting mappings or the active Steam account.

Keep local shortcut mappings and `ImportedCollection` ownership scoped to the Steam account that
created/adopted them. The portable inventory is account-independent; Steam's local shortcut lists
are stored per account. Reuse `SteamApps.ReadAccountIdAsync` to capture the active account before
and after live reads/writes; a changed/unknown account invalidates the result. Absence in account B
cannot settle a mapping belonging to A. A cached AppId alone never authorizes a deletion. Bind
legacy mappings to an account only after an exact `OwnsRecorded` match in that account; keep
unmatched records account-unknown and non-destructive.

Join the library's cached display name into projections and launch refusal text. Exclude
display-only labels and validation observations from `SameLaunch` so a card rename does not become a
Steam command edit. Apply the new library identity as an explicit metadata migration, retaining
existing commands and IDs; only actual launch recipe changes produce Update plans.

There is one authority per fact: the card owns the portable library inventory; the device owns its
Steam mappings and local choices; current filesystem observations determine availability and
confirmed deletions. Manifest revision is a consistency check, not a substitute for checking files.

## Reconciliation and automatic cleanup

Extend the existing service/monitor flow. Keep a single serialized lane for import, manifest
publication and automatic shortcut cleanup. Only participating-library ROM, direct folder and
library-backed direct manual records enter that lane's automatic pruning policy. Exclude
`SourceKind.Emulator`, launcher-native, packaged and folder protocol `.url` routes.
`EmulatorShortcutSync` retains its lifecycle but shares the session's Steam shortcut-write
serialization/settle boundary; its records are never selected by content cleanup. Availability
observations run on workers and carry a generation so removal, rescan or shutdown invalidates stale
results. Reuse the existing shutdown/admission boundaries.

For each requested library:

1. Resolve its current volume and read the complete manifest. Validate schema, identity, duplicate
   IDs, path containment and the snapshot revision. A future schema or incomplete read preserves
   cached entries and reports an error.
2. Check every recorded backing path and declared companion file for that library, even if both
   ledgers agree. Use file metadata queries, not content reads. Reuse volume resolution and shared
   prerequisite observations within the batch. Query `\\?\Volume{GUID}\relative-path` for pruning
   decisions so a drive-letter swap cannot turn another card's paths into evidence. Host
   dependencies are checked separately from the title's backing content.
3. Classify the result before applying anything, using the table below.
4. Revalidate volume/library identity and revision before committing confirmed removals. Abandon the
   result if storage disappeared or the generation changed.
5. Persist the pruned manifest atomically, retain a local cleanup record, then remove only
   confirmed-owned Steam shortcuts through the existing writer. Clear the local record only after
   deletion or an independent read in its recorded account confirms absence. Recheck account
   identity across each operation.
6. Publish the resulting index to Overlay and Steam. Log state transitions and one batch summary,
   including checked/available/missing/unreadable/pending counts.

| Observation                                                                | Outcome                                                                                                                      |
| -------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| Expected card absent, not ready or replaced by another card                | Keep cached titles; mark storage unavailable; no deletion                                                                    |
| Correct manifest and required content present                              | Keep IDs, artwork and collections; mark available                                                                            |
| Correct readable card; backing file or declared companion confirmed absent | Prune title and owned shortcut automatically; never delete remaining game files                                              |
| File access denied, I/O error or uncertain media state                     | Keep title; show check failure; no pruning                                                                                   |
| Emulator, core, BIOS or host-side launcher dependency missing              | Keep title; show the dependency failure; do not classify game content as deleted                                             |
| Manifest entry removed, but cached backing files still exist               | Repair the valid writable manifest from the previously managed cached record after a fresh file check; do not infer deletion |
| Manifest entry removed and its cached backing files confirmed missing      | Prune the cached title and owned shortcut                                                                                    |
| New manifest entry or newly discovered file                                | Offer through Library Manager; no automatic creation/execution of shortcuts                                                  |
| Manifest absent, invalid, truncated, unsupported or unreadable             | Preserve device index; report the problem; never substitute an empty manifest                                                |
| Steam shortcut command edited by the user, and content confirmed deleted   | Leave the shortcut untouched and release only its local mapping/cleanup receipt; do not retain an unresolvable pending row   |
| Steam account ownership unknown or another account active                  | Preserve the mapping pending an exact-account observation; no deletion or absence confirmation                               |

Use APIs that distinguish not-found from access/I/O errors. `File.Exists` returning false cannot
alone authorize deletion. For missing files, recheck the required volume and readable parent/source
context before pruning. If the game directory itself was deleted, a readable existing ancestor on
the confirmed volume still allows an actual not-found result to establish deletion. An unreadable
dependency volume keeps the title pending rather than deleting it.

This also applies when the whole configured ROM/game root was renamed or deleted: if the correct
volume and an existing ancestor are readable, the old titles are gone and are pruned. A missing
source root is not a special exemption. This is the maintainer's intended move/delete behavior;
Library Manager can import the new root. A missing working directory, in contrast, is a launch
dependency failure unless that directory is itself the declared backing content.

A manifest revision rollback triggers a complete fresh check, never deletion based on the revision
alone. For a valid manifest missing previously cached entries whose files still exist, repair those
entries after identity/revision revalidation. This repair uses only previously managed records; it
never recursively discovers or imports new files. A missing/corrupt manifest remains an error, not a
repairable empty inventory. A read-only manifest leaves the repair pending.

Missing a source `.lnk` alone does not mean its target game was deleted. For PC-game shortcut
imports, the executable is the backing content. The same applies when the shortcuts folder itself is
offline but the target volume is still present.

Automatic cleanup removes the title from WSGM-owned collection membership and releases only
import-owned profile state. It never erases ROMs, PC-game files, saves, user profiles or unrelated
artwork. Reuse existing cleanup helpers and their ownership rules, specifically
`ReleaseControllerTargetAsync(appId, OwnsProfile)` and the existing collection-sync path. Defer
running-title cleanup using the session's `RunningApplicationMonitor`/`SteamRunningAppPairing` state
from `Shell/RunningApplicationTarget.cs`. Unknown running state also defers cleanup; do not
terminate the game. A rename alone does not break `OwnsRecorded`. On confirmed content deletion,
leave a command-edited shortcut untouched and release only its local mapping/receipt. Do not run
Steam collection, artwork or profile mutations for that edited shortcut, and do not repeatedly
attempt deletion in the background. The current Conflict rows cannot settle ownership; do not assume
a nonexistent cleanup action. For content that still exists, keep the existing import conflict
behavior unchanged.

## Interrupted operations and recovery

Use existing atomic publication and local import records, with small explicit pending fields rather
than a general transaction engine. All index/manifest writes are host-owned. The helper only reads
the atomically published local index; do not add helper writes or a second cross-process writer
protocol. Serialize host writers and preserve the helper's existing emulator admission gate.

- An import writes its durable content/manifest intent before creating a Steam shortcut. A failed
  Steam creation retains the intent for explicit reconciliation; it never blindly retries a possibly
  completed creation.
- A confirmed file deletion commits the card's pruned inventory before forgetting its local mapping.
  A crash between card and device updates is recoverable by comparing cached paths with the next
  valid manifest and filesystem snapshot.
- If the manifest cannot be written, retain the cached title as unavailable/pending and report the
  error. Do not claim that cleanup completed or erase its local ownership record. A read-only card
  can be checked; changes require it writable.
- If Steam is unavailable, persist pending cleanup and drain it once the existing Steam readiness
  gate permits writes. No offline `shortcuts.vdf` fallback.
- After a timeout or lost response, independently read the live shortcut before any subsequent
  write. Never retry an uncertain mutation on a timer.
- If a missing file reappears before cleanup has committed, discard the stale deletion decision.
  Once removed, reimport is the route back.
- A changed manifest revision causes a fresh read/check, not last-writer-wins merge. Recreate
  omitted records only through the file-present repair of previously managed records described
  above, never just because the device cache differs.

Deleting a shortcut explicitly in Steam must not cause insertion to recreate it. Removing only a
local shortcut is a local integration choice, not deletion of the card's files or portable
inventory. After a read in the correct account confirms the shortcut is absent while content is
present, forget that account's local AppId mapping; keep the manifest title and let Library Manager
offer Add. Fresh imports keep the existing review/apply workflow on both interfaces. Keep
scans/plans non-destructive for missing managed entries: replace the current permanent-retention
rule with deferral to reconciliation, rather than adding a second automatic deletion path inside
`ImportPlan`.

## Event handling and scale

- Reconcile known libraries at startup, on resume, explicit Recheck, source/import changes and
  storage arrival/removal/remount.
- Reuse the established card-monitor settling approach for notification bursts and wait briefly for
  arrival to settle. A bounded read-only follow-up may observe a volume that was not ready on its
  first event.
- Compare current mounts with the previous snapshot to target changed volumes and records that
  depend on them. The current notification supplies a boolean, not a volume ID; reuse mount
  observations rather than assuming it identifies a card.
- Validate every recorded file in each requested library. Startup, resume and an explicit full
  Recheck cover all known mounted libraries.
- Add a low-frequency recovery pass, initially every five minutes, for missed notifications and
  manual file edits. Check only known mounted libraries; never recursively scan Z:, absent cards or
  every game directory on that timer.
- Discovery of new or moved files remains a Library Manager scan. Do not run repeated recursive
  discovery or watchers across ROM trees.
- Batch persistence/publication. Unchanged checks do not rewrite the manifest or publish Steam
  state. Coalesce validation timestamps with normal local writes or a bounded checkpoint rather than
  forcing a full index write per file.
- Cancellation prevents new work and late publication. Diagnostics contain no raw authored argument
  strings, credentials or per-file success spam.

Persist last-successful validation per library for diagnostics and checkpoint it with normal local
updates. Do not introduce per-title validation history or a new screen for timestamps. Distinguish
content-only changes from structural metadata events so cleanup does not immediately schedule a
redundant full-library pass.

## Shortcut-folder and portable PC-game workflow

Retain the current user flow: add a folder of Windows shortcuts, scan, review and apply. `.lnk`
imports adopt the original executable, arguments and working directory. Empty working directory
falls back to the executable's directory. Keep the current exclusions for broken/non-executable
targets, Steam games and installer/uninstaller tools.

Bind the imported title to the executable's storage, not the `.lnk` directory. For example,
`C:\Game Shortcuts\Game.lnk` pointing to `D:\Games\Game\game.exe` belongs in D:'s manifest. Capture
the working directory separately, since it may differ from the executable folder or live on another
volume. Several shortcuts on C: can target different cards; partition their records by actual
backing volume rather than treating the source folder as one card.

On remount, resolve the executable and working directory by volume identity and relative path.
Retain the existing trim of leading/trailing argument whitespace; otherwise keep strings unchanged,
including quoting and internal spacing. Do not guess which argument substring is a file path.
Absolute drive-letter paths inside authored arguments therefore remain the user's responsibility;
relative arguments work through the adopted working directory. Existing explicit content tokens keep
their supported substitution behavior.

Preserve the `--managed` content ID and current helper route, rather than rewriting Steam's shortcut
on each drive-letter change. Keep the original import identity and title name; do not silently merge
same-name executables on different cards. Support the directly executable PC-game route here;
launcher protocol `.url` handoffs retain their existing native route and are not advertised as
portable launch recipes.

## Both interfaces and launch behavior

Use the same backend state/actions for Overlay and Steam:

- Available: normal card and launch behavior, expected library name.
- Card absent: title remains, unavailable styling and clear location/reason.
- Check failed: retained title with a readable failure and existing Recheck action.
- Cleanup pending: visible explanation until the queued removal settles.
- Confirmed deletion: title disappears automatically when cleanup succeeds. Show a concise
  reconciliation result, without introducing a recovery wizard.
- Unknown card manifest: Library Manager can review/import its contents; insertion alone cannot
  authorize commands or local shortcuts from untrusted media.
- Lost/corrupt manifest on a known card: Library Manager's existing review/Apply offers Repair
  library from its retained known inventory, after confirming the exact existing volume binding.
  Preserve corrupt bytes in a backup, rebuild the known UUID/records atomically, then run normal
  file reconciliation. This is an explicit repair action through the existing flow, never an
  automatic empty-file replacement, and it introduces no locate/relink wizard.

Reuse current native Steam controls and Overlay rows, with retained selection, scroll and focus
during updates. If the selected title disappears, select a logical remaining entry and keep Back
reachable. Check empty libraries, long names and failures at the Overlay's 980 x 640 DIP floor and
larger sizes.

Keep availability presentation and enforcement independent of the optional location-badge display.
The required CEF feature includes unavailable card styling, details-page state, a native-style
unavailable/recheck primary action replacing the ordinary Play affordance for an unavailable managed
shortcut, and shared availability semantics for relevant installed-only library filters. Restore the
exact normal behavior on availability, feature removal or renderer replacement. Keep focus and
controller activation intact. Never present a Steam Install/download action for content that only
needs its card reconnected.

The filter contract is explicit: managed unavailable shortcuts remain in All Games and Non-Steam
views, are excluded from installed-only views, and return to those installed-only views once
availability is restored. Available managed shortcuts participate as installed. User-defined
collections keep their membership while a card is disconnected; ordinary user shortcuts and
Steam-owned games are unaffected.

Extend the existing toolkit surface and scoped hooks; its current greying/stat alone is incomplete.
Separate its decorative badge preference from managed-shortcut availability publication while the
Steam integration master is enabled. Prefer a scoped, observable per-shortcut availability
projection consumed by the relevant cards/actions/filters. Do not globally mutate Valve's install
store, intercept all `RunGame` calls or alter Steam-owned games. If safely projecting a semantic
installed state is not supported by the inspected client, implement the affected rendering,
primary-action and filtering paths explicitly through the toolkit's owned patch lifecycle. These
paths are required delivery, not a CSS-only fallback labelled done.

The launch helper reads only the host-accepted local recipe/binding and validates the current volume
and required files again. It does not parse or trust card manifests and cannot adopt a replacement
volume on launch. Missing storage gives the existing friendly location/recheck refusal, never a raw
missing-executable error. It must not launch a same-name file on a replacement card. Launching is
not a place to mutate Steam or perform broad discovery; notify/queue normal reconciliation through
an existing host boundary only if one is already available. Otherwise the next event/periodic
reconciliation handles cleanup; do not add a new IPC service merely for this notification.

### Native uninstalled state: offline investigation

The maintainer specifically requested this check before implementing the CEF state. Offline
inspection on 2026-10-28 found no shortcut-specific installed-state setter in the installed UI's
literal `SteamClient.Apps` calls. The observed APIs edit shortcut target/name/arguments/start
directory and remove shortcuts. The overview's `installed` getter forwards
`most_available_per_client_data.installed`, whose state comes from the client. Existing toolkit code
overlays WSGM availability for card greying and a details stat; it does not change Play or native
installed filtering.

The inspected `steamui/chunk~2dcc5aaf7.js` had SHA-256
`72EC01478CC8C0F1F6DF79D61BBB25DFCA2EA87703309B6C94604771CBABFB45`. No Steam code was executed and
no CEF connection was made. For corroborating public source,
[Steam ROM Manager's writer](https://github.com/SteamGridDB/steam-rom-manager/blob/master/src/lib/vdf-manager.ts)
maintains shortcuts per user and writes command metadata, and the
[Decky SteamClient Apps contract](https://github.com/SteamDeckHomebrew/decky-frontend-lib/blob/main/src/globals/steam-client/App.ts)
exposes shortcut creation/removal without a shortcut-uninstalled setter. The latter is an incomplete
reverse-engineered contract, not proof of API absence.

Planning conclusion: there is no verified native persistent toggle to rely on; budget and deliver
the CEF availability feature above. At implementation, inspect the installed client shapes and
validate one disposable managed shortcut in a maintainer-directed live session. If a narrow native
primitive is actually proven, prefer it and record that evidence; a native-looking field or absent
API name in a static search alone proves neither mutation support nor complete UI behavior.

## Compatibility and migration

1. Add versioned manifest/index fields with strict shape and path validation. Check every `.csproj`
   linking the shared content models and keep the independent helper able to read the new local
   schema. Before the first v2 write, save one immutable backup of the v1 `library-import.json`; an
   older build cannot read v2. Give new fields safe migration defaults and audit `Copy`, `Sanitize`,
   `EntryDefect`, `RecordDefect`, generated serializers and every save path so no legacy rows are
   silently dropped and no new fields disappear during an ordinary update.
2. Migrate existing managed entries in place. Keep content IDs, Steam AppIds, artwork, collections
   and per-game choices; no bulk remove-and-recreate.
3. Migration runs automatically during the first settled reconciliation of an eligible legacy card.
   Require its exact pre-existing volume binding and readable storage; preserve present and missing
   legacy entries in the initial manifest so the first real check makes the deletion decision. Bind
   legacy Steam mappings only by the exact-account match described above. Already-missing content on
   that mounted card is intentionally pruned on this first successful reconciliation, subject to the
   same ownership and read/write checks as later deletions. This explicitly supersedes the previous
   keep-until-cleanup rule. No second permission round or migration wizard. Initialize only
   genuinely unmigrated legacy records; a manifest lost after a library was registered is an error,
   never an invitation to bootstrap it again from cache.
4. For an absent legacy card, retain its local metadata and wait for its expected volume before
   binding/writing the manifest. No path-only adoption of a different card at the old letter.
   Migration failures stay visible and non-destructive.
5. Card manifests do not automatically import on a second device. Library Manager offers their
   content for review, maps local emulators and creates local Steam mappings only when applied. Do
   not execute card-authored commands while reading.
6. Fixed-only and non-volume-backed sources retain the existing local contract and
   keep-until-cleanup behavior. Do not apply automatic card pruning to Xbox, launcher-native,
   emulator-interface or protocol-link records. Unreadable or unwritable migration leaves existing
   titles retained and explains why it is pending; a failure never advances the schema/library
   migration marker.

## Implementation sequence and responsible files

| Step                            | Work and completion condition                                                                                                                                                                                                                                                    |
| ------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1. Shared contracts and storage | Extend shared local content references and `ImportStateStore`; add a focused host-only manifest reader/writer under `Core/Library`. Parse strictly, preserve IDs and validate volume-relative paths. Keep card parsing out of the helper.                                        |
| 2. Import/apply integration     | Extend `GameLibraryService`, ROM/manual adapters and `ShortcutFolderSource`, and add the small manifest source adapter. Partition content by backing library, reuse manifest IDs and publish card inventory through existing apply. Preserve `.lnk` fields and ownership checks. |
| 3. Migration                    | Back up v1 and migrate automatically at the first safe legacy-card reconciliation without Steam recreation. Preserve all new fields across copy/save/sanitize paths. Keep absent/unreadable/read-only media pending and account-unknown mappings protected.                      |
| 4. Reconciliation               | Extend `ManagedShortcutMonitor` and existing service worker flow with targeted checks, settled arrivals, periodic recovery, confirmed deletion and pending cleanup. Reuse `SteamShortcutWriter` and existing profile/collection cleanup.                                         |
| 5. Launch guard                 | Update shared local lookup and `ManagedContentLaunch` for host-accepted library bindings, current mounts, local dependency resolution and actionable refusal. Preserve process/input lifetime.                                                                                   |
| 6. Both UI surfaces             | Complete shared projections, Overlay states and Steam card/details/primary-action/filter behavior, with toolkit-owned reversible hooks. Keep availability independent of badge preference and review focus/Back behavior. Regenerate affected toolkit and WSGM Steam assets.     |
| 7. Delivery and acceptance      | Build once for the coherent change, deploy when directed, run the attended scenarios below, then use the maintainer's manual report to select focused automated checks.                                                                                                          |

Keep reusable Steam mechanics in `external/steam-ui-toolkit`, WSGM policy in WSGM. No new project,
generic synchronization framework, alternate database or duplicate storage inventory service. Read
applicable nearer guides before implementation.

## Validation and evidence

The planning pass found D: labelled `SDCard12`, approximately 367 GiB, with an existing
`SteamLibrary` directory; `Z:\ROMS` was accessible. These are observations, not permanent
identities. Before any copy, reread volume identity and free space. Copy a few non-BIOS GB/GBA/SNES
games and one PSX CHD into a dedicated test root, preserving source files and existing card content.
Keep total sample size below 2 GiB. Record copied names/size and the card's current identity.

Add a small portable test executable and real Windows `.lnk` fixtures with arguments and a distinct
working directory; place the shortcuts folder on C: and target the card. Use owned fixtures, never
delete original games or source ROMs for a test. Delete/move only explicitly recorded disposable
copies during attended validation.

| Scenario                                                                        | Required observation                                                                                                                              |
| ------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| Initial ROM and PC shortcut import                                              | Correct names, executable, arguments, working directory, library UUID and local Steam mappings                                                    |
| Remove/reinsert card while Steam runs                                           | Titles remain unavailable while absent, then recover with the same AppIds, artwork and collections                                                |
| Startup without card; insertion afterwards                                      | Cached library is named correctly and insertion restores availability without restart                                                             |
| Changed drive letter                                                            | Correct card's executable, working directory and ROM paths resolve; no shortcut recreation                                                        |
| Sleep/resume with card changed                                                  | Fresh reconciliation, no stale deletion or replacement-card launch                                                                                |
| Delete/move a copied ROM or executable                                          | Correct attached card causes automatic manifest/index/owned-shortcut removal; later scan can import moved files                                   |
| Rename/delete the configured ROM/game root                                      | On the correct readable card, old title entries are pruned; new root requires Library Manager import                                              |
| Delete a required disc track                                                    | Title is pruned; unrelated tracks, saves and files remain                                                                                         |
| Delete a `.lnk` but retain its executable                                       | Existing imported game remains; source-link absence does not delete real content                                                                  |
| Shortcut folder on C:, games on two cards                                       | Each title follows its target volume; absence of one card does not affect the other                                                               |
| Read-only card, access error, corrupt/missing manifest or unplug during a check | No false deletion, actionable retained/pending state and safe recovery                                                                            |
| First import on a write-protected card                                          | Clear import refusal; no half-created Steam shortcut or silent local-only inventory                                                               |
| Upgrade with already-missing legacy titles                                      | One v1 backup, safe automatic initialization, intended pruning on the attached card, unchanged IDs for retained titles                            |
| Manifest revision rollback or omitted entry whose content still exists          | Fresh file checks; repair only known managed records; no deletion based on revision or omission alone                                             |
| Missing local emulator/core/BIOS                                                | Dependency refusal, no content/shortcut pruning                                                                                                   |
| Same filenames on different cards; duplicate library UUIDs                      | No cross-card substitution, merge or automatic deletion on ambiguous identity                                                                     |
| Steam disconnected, restart during cleanup or lost write response               | Durable pending work; independent read before mutation; eventual account-correct cleanup                                                          |
| User-edited shortcut or different Steam account                                 | Exact ownership enforced; unrelated shortcut preserved                                                                                            |
| Steam account switches during a check/write or while cleanup is pending         | No absence confirmation or mutation attributed to the wrong account; mapping retained for the correct account                                     |
| Two accounts import the same title; one mapping is removed                      | Both mappings coexist; removing one preserves the other, the card title and shared per-title choices                                              |
| Rename-only shortcut versus command-edited shortcut on deleted content          | Rename alone preserves ownership; command-edited shortcut remains untouched and its local mapping is released without a permanent pending row     |
| Deleted protocol `.url` or emulator-interface shortcut                          | Excluded from automatic card-content pruning                                                                                                      |
| Second WSGM device/account                                                      | Card offers reviewable inventory, local mappings stay local, no automatic execution                                                               |
| Known UUID on a new local volume or copied media                                | Existing binding not silently replaced; review/Apply required; duplicate attached UUIDs remain ambiguous                                          |
| Known card loses/corrupts its manifest, then Repair library is applied          | Exact binding checked, original bytes preserved where present, inventory rebuilt from known records, normal file checks resume                    |
| Normal Steam library, Big Art Mode, details and badges disabled                 | Consistent unavailable presentation, unavailable/recheck primary action and usable focus/navigation                                               |
| All Games, Non-Steam, installed-only filters and Steam-owned titles             | Unavailable managed titles remain in general views but leave installed-only views; reinsertion restores them; other titles retain native behavior |
| Details page open when its title is removed                                     | Page exits or reaches a clear removed state; Back and focus remain usable                                                                         |
| Overlay compact/larger, populated/empty/error                                   | Personally inspected renders; no clipping, lost Back or disruptive refresh                                                                        |
| Large library                                                                   | One metadata pass per requested library, shared mount/prerequisite queries, no recursive timer scan or per-file persistence/logging               |

Follow the repository's manual-first validation budget. Initially run one useful warning-free
Release build of affected application/helper dependencies and changed asset checks. Use
`eng/dev-deploy.ps1` for an authorized deployment. Before any live CEF connection, establish
completed Steam and Big Picture startup from this run's Steam logs. Physical removal/reinsertion
remains attended.

After the maintainer's manual report, add/run focused tests for manifest parsing, identity/path
containment, migration, reconciliation/deletion decisions, interrupted cleanup, account ownership
and `.lnk` imports. Use temp roots and fake Steam/storage adapters; never mutate live media in
automated tests. Render changed Overlay cases, inspect the images and update only named baselines at
the permitted stage. Inspect live Steam screenshots before claiming Steam visual acceptance. A build
cannot establish storage, launch, controller or visual acceptance. Do not run the full gate or
whole-project cleanup without its separately required reason/request.

Update `docs/game-library.md`, `docs/sd-cards.md`, relevant mechanism/SDK contracts and
`docs/decisions.md` when implemented, explicitly superseding the old rule that missing managed
content always remains until manual cleanup. Link this plan from the Game Library documentation as
proposed work. Continue the existing authorized task branch; push changed submodules before the
parent gitlinks and record the actual build/artifact identity and remaining live acceptance once.

## Review

The maintainer requested a full Claude review after the initial plan. Claude Code
(`claude-opus-5-5`, session `c9270e5c-6dc8-499c-91a6-fc413c1ca0d3`) inspected the draft and the
source owners listed in its [complete review](../../reports/issue-200-plan-review.md) on 2026-10-28.
Its initial verdict was changes required. That review was read-only and did not establish live or
implementation acceptance.

Revisions address the five high-severity findings: exact-account ownership and legacy adoption,
authoritative portable content IDs plus a manifest source adapter, explicit review/Apply for a new
local volume binding, display labels excluded from launch-intent comparisons, and an explicit
first-run migration/deletion policy.

The medium/low findings are addressed by defining participating volumes and read-only behavior;
checking through volume GUID paths; repairing known manifest omissions after file checks; specifying
conflict and user-deleted-shortcut handling; excluding emulator/protocol routes; naming
running-session and cleanup owners; following existing card naming; retaining current argument
trimming; keeping the helper read-only; defining the v1 backup and parser/path bounds; and
separating scan planning from the automatic reconciliation lane.

One recommendation was not adopted: treating a renamed/missing configured root as source-unavailable
forever. The maintainer explicitly chose deletion for moved content. This plan therefore prunes old
entries after confirming absence through the correct readable volume and requires reimport at the
new root, with no relink workflow. Media/read failures still preserve entries.

The maintainer subsequently requested investigation of native uninstalled shortcut state. The
offline findings and the complete required CEF work are now explicit in the UI section. The public
per-account storage evidence was checked separately after the maintainer questioned account scoping;
account-independent card content and account-local Steam mappings remain separate facts.

Claude's focused second pass confirmed all five original high-severity findings were resolved in
intent, and identified remaining local gaps. The plan now chooses the per-title list of account
mappings explicitly, releases local ownership of an edited shortcut after confirmed content deletion
without touching Steam, supplies Repair library through review/Apply for a lost manifest, qualifies
file-present manifest repair, and defines exact installed-only filter semantics. The second pass is
preserved in the review report. These final corrections have been checked against the cited source;
no implementation or live acceptance is claimed.
