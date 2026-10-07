# Full ROM importer, managed shortcut metadata and authoritative launch integration

Research snapshot: 2026-10-06, `D:\Coding\WSGM`, master at
`57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`. Read-only source and GitHub issue inspection. No
compilation, tests, deployment, Steam CEF evaluation, UI interaction, or game launch was performed.
Recommendations below are proposals, not implemented behavior.

## What already exists, and what does Issue 200 actually depend on?

### Takeaway

WSGM already has the importer ownership record, shared source interface, live shortcut backend, and
in-place update mechanism needed to keep Steam identity. The combined requested work must add the
complete ROM importer and connect it to #203's Emulator Manager as well as #200's availability
tracking; current source explicitly lacks ROM sources.

### Cited Findings

- Issue 200 requires persistent Steam identity, source kind, backing content, logical storage
  identity, expected volume, availability and last successful validation; unavailable entries stay
  visible and recover without Steam restart. It explicitly separates creation/sync owned by #47 from
  runtime availability. — [Issue 200](https://github.com/KillerPixelCrew/WSGM/issues/200)
- Issue 47 is currently closed, but its actual acceptance includes ROM source configuration/import,
  folder sync and at least one launcher. A closed tracker item alone is not evidence that each
  current source capability exists. — [Issue 47](https://github.com/KillerPixelCrew/WSGM/issues/47)
- Current configured sources are Xbox, Epic, GOG, Ubisoft, Battle.net, itch, Amazon, Prism and
  ATLauncher; configured shortcut folders are appended separately. —
  [ShellSession.cs:923-960](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/ShellSession.cs#L923-L960),
  [GameLibraryService.cs:1489-1493](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L1489-L1493)
- Folder source configuration permits only `.lnk`, `.url` and `.exe`. The discovered title key is
  relative to the configured folder, which is a useful identity independent of drive letter provided
  its source ID remains unchanged. Existing creation derives that ID from a hash of the absolute
  folder path, so removing/re-adding the folder under a different letter would change the ID;
  migration must preserve its persisted ID and new ROM sources should allocate IDs independent of
  root spelling. —
  [GameLibraryConfig.cs:17-37](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/GameLibraryConfig.cs#L17-L37),
  [ShortcutFolderSource.cs:167-174](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/Sources/ShortcutFolderSource.cs#L167-L174),
  [GameLibraryService.cs:2375-2381](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L2375-L2381)
- Folder `.lnk` import resolves the link's program, arguments and working directory; direct routes
  require an existing executable. Its discovery-time check is against the program, with no distinct
  ROM-content field. —
  [ShortcutFolderSource.cs:205-242](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/Sources/ShortcutFolderSource.cs#L205-L242)
- `DiscoveredGame` carries source/key/name/install path and routes; `ShortcutRoute` carries
  executable/start directory/raw launch options and optional follow directory/marker. Neither shape
  carries storage identity, source kind, declared required content, availability or path-binding
  roles. —
  [LibrarySource.cs:45-80](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/LibrarySource.cs#L45-L80),
  [ShortcutRoute.cs:16-55](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ShortcutRoute.cs#L16-L55)
- `ImportedEntry` already persists `Source`, `Key`, `AppId`, `Name`, exact Steam target/options,
  input mode/route, import/confirmation timestamps, artwork and profile ownership. It does not
  persist the original start directory, source type, backing file, volume identity or availability.
  —
  [ImportPlan.cs:40-89](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportPlan.cs#L40-L89)
- `ImportStateStore` stores version 1 data in `%LOCALAPPDATA%\WSGM\library-import.json`, serializes
  mutations, caches its read state, protects unreadable files and writes with durable atomic
  replacement. Existing unknown newer versions refuse mutation. —
  [ImportStateStore.cs:13-28](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportStateStore.cs#L13-L28),
  [ImportStateStore.cs:90-109](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportStateStore.cs#L90-L109),
  [ImportStateStore.cs:294-356](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportStateStore.cs#L294-L356),
  [ImportStateStore.cs:534-550](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportStateStore.cs#L534-L550),
  [UserDataContext.cs:13-17](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/UserDataContext.cs#L13-L17)
- `SteamShortcutWriter.UpdateAsync` changes the existing AppId, including start directory, and
  explicitly avoids remove/re-add because that loses identity and artwork. Shell supplies toolkit
  `SetShortcutLaunchAsync` to do this. —
  [SteamShortcutWriter.cs:68-85](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/SteamShortcutWriter.cs#L68-L85),
  [ShellSession.cs:940-947](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/ShellSession.cs#L940-L947)
- Existing live reads retain AppId/target/options but discard the toolkit's start directory.
  Ownership checks compare the remembered exact target/options, protecting user edits and older
  helper locations. —
  [ShellSession.GameLibrary.cs:61-94](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/ShellSession.GameLibrary.cs#L61-L94),
  [ImportPlan.cs:416-429](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportPlan.cs#L416-L429)
- Unread/disabled/failed sources are protected from cleanup. A missing configured folder or
  uninstalled launcher is currently `Gone`; missing discovered titles produce an unticked, manually
  selectable Remove action rather than availability state. —
  [GameLibraryService.cs:1605-1632](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L1605-L1632),
  [ImportPlan.cs:355-394](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportPlan.cs#L355-L394)
- Collections already retain their actual Steam collection ID and prior WSGM-added AppIds, altering
  only owned membership. Membership groups by source. Retaining an unavailable ImportedEntry
  naturally retains membership; removing it would remove membership at the next sync. —
  [ImportStateStore.cs:31-49](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportStateStore.cs#L31-L49),
  [GameLibraryService.cs:2070-2163](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L2070-L2163)
- Current documentation explicitly says ROM folders arrive with the emulator installer and are not
  part of this Game Library pass. —
  [game-library.md:67-72](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/docs/game-library.md#L67-L72),
  [game-library.md:353-356](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/docs/game-library.md#L353-L356)

### Inferences

- The user's clarified scope requires the real ROM importer now, alongside #200 and full Emulator
  Manager #203. An emulator executable check or synthetic ROM fixture cannot fill this missing
  source capability. Build the source and launch metadata together so the runtime guard receives
  authoritative ROM paths from creation.
- Distinguish a source temporarily unreachable from a source intentionally removed from
  configuration. Media removal must not become sync cleanup. Explicit user cleanup remains possible;
  temporary absence must retain ImportedEntry, AppId, artwork, profile and collection membership.
- A Start Menu/desktop link imported through a folder is not inherently library-backed. The
  library-backed opt-in must say which actual target/content matters; removing the source `.lnk`
  should not mark a still-installed target unavailable unless that source file is explicitly the
  required content.

### Full #47 requirements: existing capability and required completion

Every row below derives from the actual
[Issue 47 requirements](https://github.com/KillerPixelCrew/WSGM/issues/47). Current capability
claims are limited to inspected source, not a live integration pass.

| Requirement                                                                                                                                          | Current source evidence                                                                                                                                                                                                                                                                                                | Work needed for combined scope                                                                                                                                                                                                                                                                                                                                    |
| ---------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| ROM, launcher, folder and manually defined sources                                                                                                   | `ShellSession.cs:923-960` supplies nine launcher adapters and ShortcutFolderSource. `GameLibraryConfig.cs:17-37` supports `.exe/.lnk/.url`, not ROM formats.                                                                                                                                                           | Add configured ROM sources and an explicit manual-source definition flow through ILibrarySource; retain existing launcher/folder adapters.                                                                                                                                                                                                                        |
| Source/parser definitions, scan, dry-run preview, selection/apply, manage/rescan                                                                     | Existing GameLibraryService has Scan, Toggle/Select, Apply, Exclude/Include, Details, source switches and folder add/remove (`:213-494`, `:537-810`).                                                                                                                                                                  | Extend both controller/touch surfaces for system, parser/profile, ROM root, recursion/extensions/exclusions, compatible emulator selection/install, per-title exceptions and preview of the resolved command/content/storage. Reuse the review/apply stage as dry run.                                                                                            |
| Emulator construction separate from Steam layer; per-system/per-title overrides; several emulators per platform                                      | ShortcutRoute separates command description from Steam write; no ROM/emulator profile exists.                                                                                                                                                                                                                          | RomLibrarySource emits title/content metadata; Emulator Manager resolves definition/installation; a ROM launch builder renders typed template tokens; SteamShortcutWriter receives fixed managed-helper fields. Store per-system preferred emulator and per-title override independently from Steam fields.                                                       |
| Archives, extensions and exclusion rules                                                                                                             | Current folder extension filtering and reparse/hidden/system skip exist (`ShortcutFolderSource.cs:155-201`), with no ROM archive profile.                                                                                                                                                                              | Add per-profile accepted extensions, recursion, include/exclude patterns and supported launch form. Archive accepted directly only when the selected emulator profile supports it; unsupported archives appear with a useful preview refusal rather than silently launching or extracting at every scan.                                                          |
| Repeated scans dedupe; update moved/removed titles; preserve edits; safe cleanup                                                                     | Source+Key dedupe, exact field conflict checks, live confirmation and AppId update exist (`ImportPlan.cs:314-350`, `:416-429`, `SteamShortcutWriter.cs:68-85`).                                                                                                                                                        | Record stable IDs and source-relative ROM paths. Identity survives mount and emulator-version changes. Confirm moved files through explicit user relink or reliable fingerprint; do not infer a move from matching basename. Disconnected/unread sources remain unavailable; explicit removal/cleanup is distinct.                                                |
| Artwork provider routing and selection                                                                                                               | Existing GameLibraryArtwork previews/fetches candidates, respects user match and delegates to shared ArtworkSearch. Automatic order is currently SteamGridDB first for every title (`GameLibraryArtwork.cs:58-87`, `:147-156`).                                                                                        | Source-kind-aware routing: ROM system/content to Screenscraper first, PC/launcher/folder to SteamGridDB; fallback only on successful empty result/no media, preserve failure as failure and manual provider/match choice.                                                                                                                                         |
| ROM identification and Screenscraper metadata                                                                                                        | Current Screenscraper search is `jeuRecherche.php?recherche=<name>` without system filter; details use game ID (`ArtworkProviderImplementations.cs:201-257`).                                                                                                                                                          | Add system-aware ROM query carrying Screenscraper system ID, original filename/size and optional cached fingerprint. Store provider title ID/platform/region without changing source-content identity.                                                                                                                                                            |
| Bulk pacing and durable resume; live quota; stop on 430/431                                                                                          | Existing gates have fixed concurrency, bounded session cache and 30-second failure memory. Screenscraper fixed one request; quota HTTP codes become a string exception, and no live quota fields or durable progress appear (`ArtworkRequestGate.cs:30-124`, `ArtworkProviderImplementations.cs:156-163`, `:460-498`). | Parse a typed account quota snapshot on every relevant response; gate all screenscraper lookups and media downloads by current limits. Persist per-title enrichment/application cursor and selected/cached assets. Pause that provider batch on 430/431 until user action or verified reset, preserve done work, and resume unfinished titles after WSGM restart. |
| Xbox packaged runtime classification, #48 route preservation, explicit input-mode choice                                                             | Current XboxLibrarySource/XboxRuntimeClassifier and PackagedLauncherShortcut are wired (`ShellSession.cs:925-928`).                                                                                                                                                                                                    | Reuse those routes; adding ROM management must not replace package launch identity with a direct executable or silently enable Steam injection for controller-only. This scope does not redesign #48 mechanisms.                                                                                                                                                  |
| Third-party launchers including named Epic/GOG/EA/Ubisoft/Battle.net and extensibility                                                               | Source interface and Epic/GOG/Ubisoft/Battle.net plus additional launchers exist; no EA adapter in current construction (`ShellSession.cs:923-936`).                                                                                                                                                                   | Preserve implemented adapters, add EA metadata adapter to account for that explicit named requirement; adapter completeness requires its own discover/launch acceptance.                                                                                                                                                                                          |
| Done when: full Game Mode ROM source, folder sync, launcher import, no duplicates, selectable applied Screenscraper art, no external desktop utility | Source implements much of the shared pipeline but documentation excludes ROM (`game-library.md:353-356`).                                                                                                                                                                                                              | Full real ROM flow and external/managed emulator integration must be implemented and exercised, not accepted from #47's CLOSED state.                                                                                                                                                                                                                             |

### Primary-source research supporting the smallest ROM design

- Upstream SRM supports Glob, Glob-regex and Manual ROM parsers; its configuration separates ROM
  directory, executable, start-in, parser inputs, executable arguments and title modifiers. WSGM can
  reproduce useful capabilities with its own profile model rather than embedding SRM's Electron
  app/Steam-file writes. —
  [SRM README](https://github.com/SteamGridDB/steam-rom-manager/blob/master/README.md),
  [reference snapshot configuration](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/src/models/user-configuration.model.ts#L17-L61)
- Upstream SRM has explicit file/ROM/executable/start-directory variable concepts and user title
  transformations; this supports a small declared path-token template rather than replacing strings
  in arbitrary launch commands. Its repository carries GPLv3; reuse/copy decisions must retain
  applicable provenance, but bundling it is not needed for this architecture. —
  [SRM parser variables](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/src/lang/en-US/markdown/parser-variables.md),
  [SRM license](https://github.com/SteamGridDB/steam-rom-manager/blob/master/LICENSE)
- Screenscraper's API supports system-filtered title search, ROM lookup by
  filename/size/hash/system, and returns thread/minute/daily/negative-result quota counters; 430/431
  identify daily scrape/unrecognized-ROM exhaustion. —
  [Screenscraper API documentation](https://www.screenscraper.fr/webapi2.php)

Recommended ROM shapes and scan semantics (proposal):

- `RomSourceConfig`: independent SourceId, friendly name/LocationId, volume-relative root, SystemId,
  ParserProfileId, recursion/include/exclude rules, default emulator selection and enabled flag. Add
  an advanced manually defined title/source route for hand-curated exceptions; preserve this in the
  same preview/apply pipeline.
- `RomParserProfile`: supported systems, accepted file extensions/archives, file-versus-folder
  launch form, optional title capture/normalisation rules, and declared companion-file conventions.
  Start with plain extension/glob matching plus optional title regex, not SRM's entire nested
  variable language. Present original filename, normalised title and system in preview; users can
  override title/artwork identity without rewriting the path key.
- `RomContentRecord`: stable ManagedId/Source+content key, primary source-relative ROM or
  descriptor, companion PathRefs, original filename/size, optional cached CRC/hash, system, explicit
  emulator selection override and chosen launch-profile override. Check lightweight presence for
  availability; never rehash an entire SD card on insertion. Compute identification fingerprints
  only during import/enrichment/relink when useful and cache by file size/write timestamp.
- Multi-file formats need correct entry-point handling: e.g. descriptor/playlist is one launchable
  title; companion tracks are required dependencies and should not each create another shortcut. #47
  does not explicitly demand a broad multi-disc manager. Support declared descriptor/playlist sets
  in accepted profiles, and a manual primary/companion override for ambiguous sets; defer automatic
  filename-based disc merging unless separately required. Do not silently treat any `.bin` as an
  independently launchable game.
- Within one source, use primary relative path/native supplied key, never normalised display title
  or emulator ID, for dedupe. Across overlapping configured sources, identify exact same
  storage/content and show duplicate-source conflict/adoption choices; two distinct volumes with
  equal filenames remain distinct titles. A rename is a proposed move until its
  user-confirmed/fingerprint identity resolves; keep old unavailable record/AppId and update its
  path on approved relink instead of delete/re-add.
- On a successful mounted/readable scan, truly removed entries become missing-content review items
  with explicit cleanup. On unavailable storage or failed enumeration, retain all records and prior
  intent/artwork. Rescans and availability updates must not reset emulator overrides, title
  mappings, exclusion choices, user artwork or hand-edited Steam fields.

Emulator Manager integration contract for #203 (installation internals owned by its separate
research lane): retain `EmulatorDefinitionId`, `InstallationId`, `Managed/External` ownership,
SystemId and per-title optional override in LaunchSpec. Catalog lookup answers compatible
definitions/installations and update state;
`ResolveLaunch(installationId, systemId, launchProfileOverride)` supplies the active executable,
working directory, required dependency checks and typed argument template. Managed upgrades switch
the installation's active version while preserving InstallationId, ManagedId and AppId; no versioned
archive path enters Steam. External usable installations are selectable and remain outside WSGM
update ownership. Preferred-system changes affect titles following that preference while per-title
explicit choices remain; preview the affected titles. Removing a depended-on emulator leaves ROM
records intact with `EmulatorUnavailable` and lets the user choose/install a replacement. —
[Issue 203](https://github.com/KillerPixelCrew/WSGM/issues/203)

### Gaps

- Current source was verified, but neither importer UI nor Steam identity retention was exercised
  live. Issue 47's closed state is not promoted to a current full-ROM integration or live-page pass.
- No per-Steam-account namespace appears in inspected import record/store shapes. Include the active
  Steam account in the AppId mapping before projecting onto a different account's library; Windows
  user identity alone does not identify the active Steam library.

## What durable metadata and identity should be added?

### Takeaway

Extend the existing import record instead of reconstructing content from Steam launch text. A
durable record ID must exist before Steam assigns AppId; a fixed helper target and
`--managed <recordId>` command let volume-relative metadata change while Steam shortcut identity
remains stable.

### Cited Findings

- Existing folder source keys are source-relative and stable, whereas Steam command routes currently
  contain absolute executable/start-directory/follow paths. —
  [ShortcutFolderSource.cs:167-174](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/Sources/ShortcutFolderSource.cs#L167-L174),
  [ShortcutRoute.cs:125-181](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ShortcutRoute.cs#L125-L181)
- Currently the saved record is constructed only after the Steam add/update request and then
  persisted before artwork/profile work. Unknown adds are not retried. —
  [GameLibraryService.cs:1924-1952](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L1924-L1952),
  [GameLibraryService.cs:1978-1995](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L1978-L1995)
- Existing product rules already require volume GUID addressing and contentId identity for tracked
  libraries, with identity revalidation before acting on stale scan results. —
  [Shell/AGENTS.md:34-42](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/AGENTS.md#L34-L42)

### Inferences

Proposed minimal record additions, owned by `Core/Library` and serialized by the existing
`ImportStateStore`:

| Field                                                   | Purpose                                                                                                                                                                                                                                                                 |
| ------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ManagedId`                                             | GUID allocated once before Steam creation, retained across moves/remounts. Helper references this, never AppId or drive letter.                                                                                                                                         |
| `SteamAccountId` plus existing `AppId`                  | Account-bound confirmed Steam identity. No guessed hash or shortcuts.vdf identity.                                                                                                                                                                                      |
| Existing `Source` and `Key`; `SourceKind`               | Stable configured source and its stable relative/native content key; explicit `Rom`, `Folder`, `Portable`, `Launcher` or `Manual`.                                                                                                                                      |
| `AvailabilityPolicy`                                    | `UserShortcut` or `LibraryBacked`. Ordinary unmanaged/user shortcuts retain their existing behavior.                                                                                                                                                                    |
| `LocationId`, `LocationName`, `StorageIdentity`         | Source/library reference plus persisted friendly expected location and expected volume GUID/contentId/serial when available. Persist display label so absence does not erase it.                                                                                        |
| `BackingContent` and `RequiredPaths`                    | Explicit main ROM/executable/content directory PathRef, with extra required files such as emulator executable. ROM and emulator are separate dependencies.                                                                                                              |
| `LaunchSpec`                                            | Original route kind, program PathRef or stable emulator definition/installation reference, original working-directory PathRef, original verbatim arguments and structured bindings/template supplied by its authoritative adapter; existing mode/input policy retained. |
| `Availability`, `Reason`, `LastSuccessfulValidationUtc` | Persisted observation for startup presentation, revalidated before launch; observation is not authority to skip fresh checks.                                                                                                                                           |

`PathRef` should be a small explicit shape: fixed absolute path or
`(StorageIdentity/LocationId, relativePath)`, plus last known display path. Resolve a correct
matched volume/root first, then combine relative paths; reject escape outside that root. Program and
backing content may be on different volumes. A ROM on SD card can launch through an emulator on the
internal disk. Volume GUID is the lookup/validation identity; choose a deterministic current usable
drive or mounted-folder path for the launch if the emulator cannot accept volume-GUID syntax, and
verify it still names that volume immediately before launching. Do not assume every emulator accepts
`\\?\Volume{...}\` paths.

Creation sequence solves AppId circularity: allocate ManagedId and save pending record with AppId
zero before AddShortcut; Steam sees the installed helper target and stable `--managed <ManagedId>`
options; after confirmation bind AppId into that record. An unanswered add retains the pending ID
for reconciliation, without another blind add. On restart locate that exact managed command token
through the existing live shortcut read. Preserve the existing unknown-write and user-edit
safeguards. Do not delete the pending record merely because no immediate AppId was returned.

The original Steam `Target`, `LaunchOptions` and start directory remain the comparison snapshot of
what WSGM wrote. The original game/emulator executable/options/start directory live in LaunchSpec
separately. Migration must read and retain the toolkit's current `ShortcutStartDir`; current
ExistingShortcut drops it. Existing records lack enough information to manufacture an authoritative
ROM path. Upgrade eligible direct/folder entries from current source data only after exact ownership
agreement; unresolved legacy routes stay explicit rather than guessing.

Argument rebinding must be structured. Future ROM adapters supply a template such as literal
`--fullscreen`, explicit ContentPath token, and literal system options; render each declared path
token with the canonical Windows argument quoting. Preserve literal arguments and their boundaries.
Existing arbitrary verbatim `.lnk` options stay verbatim, with no searching/replacing of drive
letters or basenames. If they contain an unknown content path, a user/source must explicitly declare
its binding before relocation can be promised. Store original raw options for audit/preservation
even for structured templates.

Keep single-writer ownership in the resident service. The helper reads a fresh atomic snapshot
independently and never mutates the cached ImportStateStore. Add a small shared read-only
DTO/parser/resolve seam compiled into app and helper, following the existing shared-command source
pattern. Do not reference the entire WSGM application from the helper. Bump the state schema
version, deep-copy any newly mutable nested metadata, and batch observation persistence so
last-validation updates do not rewrite the whole library for every individual file check.

### Gaps

- Exact stable Windows volume/API discovery is another research track. The required metadata
  contract here permits that track's volume GUID/contentId/serial provider without implementing
  another media watcher.
- If an existing shortcut has hand-edited launch fields, Issue 200 must show the conflict and
  require a clear adoption/reconfiguration choice; automatic wrapping would violate current
  preservation rules.

## How should launch guarding, late resolution and ownership work end to end?

### Takeaway

A Steam card patch can improve presentation but cannot authoritatively guard desktop shortcuts or
`steam://` launches. The actual Steam target must be an availability-aware helper that resolves the
durable record and rechecks required content at launch, including when WSGM's UI or CEF is
unavailable.

### Cited Findings

- Non-Steam shortcuts ignore an exe-replacing `%command%` launch option; existing code therefore
  puts the wrapper in Target. A launch-options-only guard is insufficient. —
  [LaunchWrapperCommand.cs:53-59](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/LaunchWrapperCommand.cs#L53-L59)
- `WSGM.Launch` requires a concrete command and behavior flag, captures split command
  arguments/environment, and either invokes the native lease wrapper or starts the passed
  executable. It currently has no managed record ID or storage resolver. —
  [WSGM.Launch/CommandLine.cs:41-85](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Launch/CommandLine.cs#L41-L85),
  [WSGM.Launch/Program.cs:109-143](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Launch/Program.cs#L109-L143),
  [WSGM.Launch/LaunchPayload.cs:13-46](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Launch/LaunchPayload.cs#L13-L46)
- The existing packaged helper receives AUMID, or fixed `--follow` program/directory/marker
  arguments. FollowSession starts the request's fixed program and supervises the game; it does not
  resolve import metadata. —
  [WSGM.PackagedLaunch/Program.cs:43-101](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.PackagedLaunch/Program.cs#L43-L101),
  [FollowSession.cs:38-57](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.PackagedLaunch/Session/FollowSession.cs#L38-L57)
- The packaged helper deliberately starts followed launchers outside Steam's tree, while ordinary
  direct routes currently let Steam start the executable. These are different runtime routes;
  sharing an availability guard must preserve that distinction. —
  [FollowSession.cs:11-28](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.PackagedLaunch/Session/FollowSession.cs#L11-L28),
  [ShortcutRoute.cs:131-144](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ShortcutRoute.cs#L131-L144)
- `ShellSession` already owns GameLibraryService construction, starts it, and includes it in
  teardown. Startup currently detects sources and publishes state, rather than performing
  availability reconciliation of every imported content record. —
  [ShellSession.cs:922-964](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/ShellSession.cs#L922-L964),
  [ShellSession.cs:1092](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/ShellSession.cs#L1092),
  [GameLibraryService.cs:1222-1241](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Shell/GameLibraryService.cs#L1222-L1241)
- The launch guides require WSGM.Launch to remain small and independent of desktop startup;
  PackagedLaunch is the imported shortcut helper, with injection/input mode constraints and shared
  command sources. —
  [WSGM.Launch/AGENTS.md](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Launch/AGENTS.md),
  [WSGM.PackagedLaunch/AGENTS.md](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.PackagedLaunch/AGENTS.md)

### Inferences

Recommended compact implementation sequence:

1. Agree the small shared SystemId/emulator installation/path/template contract with #203, extend
   Core/Library metadata/schema and allocate ManagedId before Steam creation. Preserve existing
   eligible folder IDs/AppIds through migration and retain original start directory/options.
2. Implement the full RomLibrarySource/parser/profile/manual-source flow, compatible
   managed/external emulator selection, per-system defaults and per-title overrides through the
   existing controller/touch preview/apply UI. Account for EA source completion alongside retained
   existing launcher/folder adapters.
3. Extend the existing artwork stage with source-kind routing, system/ROM identification, live
   Screenscraper quota and persistent enrichment/application progress; keep manual
   selection/provider override and pause/resume useful without blocking import of already identified
   titles.
4. Add `WSGM.PackagedLaunch --managed <id>` as the common imported-content entry point, using a
   small shared parser/resolver. Resolve the active emulator version and current ROM volume mount at
   launch. Dispatch to existing packaged/follow strategy or a direct child-launch path that keeps
   Steam environment and process-tree lifetime. This direct path must be added and validated;
   today's detached follow mode would change ROM overlay/input semantics. Preserve explicit
   de-elevation/input lease/injection policy and do not redesign #48.
5. Make runtime availability reconcile retained records separately from importer/artwork scans.
   ShellSession owns lifetime; Core owns policy/state, existing media manager supplies events,
   toolkit owns presentation. Missing media never automatically removes entries. Join
   availability/badges to confirmed AppId and provide helper-local friendly refusal/recheck outside
   CEF.
6. Build/deploy as the later implementation request authorizes; validate the full
   importer/manager/storage/manual matrix. Follow repository manual-first rules and run focused
   tests only when authorized or after maintainer manual acceptance.

Launch-time flow: load a fresh record, validate shape and original strategy, resolve the selected
emulator's active installation, identify expected content volume/root, resolve all declared PathRefs
to current usable mounts, check backing ROM/game content and required emulator/program, and check
mount identity immediately before starting. Retain typed `StorageUnavailable`, `ContentMissing`,
`EmulatorUnavailable`, `AccessDenied/Unreadable` reasons so UX states the actual remedy. If metadata
is absent/unreadable, the expected storage does not match, required content cannot be read or
CreateProcess fails because media disappeared, refuse cleanly; no basename search, alternative
volume guess or automatic launch retry. Cached Available is never sufficient.

Friendly refusal example:
`This game is on SD Card — Retro, which is currently unavailable. Insert that card, then choose Recheck.`
If the volume is present but the declared ROM was removed, say content is missing from that location
and offer recheck/source management. If the ROM exists but emulator does not, name the emulator
dependency instead. Keep error details in the launcher log and do not surface a raw Windows
missing-file exception. A small native fallback dialog with Recheck/Close keeps this functional when
WSGM/CEF is absent; resident UI can render the same result when reachable. Optional source-manager
navigation must not be necessary for refusal or launch resolution.

TOCTOU scope: identity revalidation and volume-relative addressing prevent launching a different
card at a reused letter. Removal can still occur between validation and executable/ROM open, and an
emulator may open its ROM after process creation. Catch the launch boundary failure and publish
unavailable; do not promise that prechecking makes removal atomic. A hot unplug after a game has
started is a game/media-loss scenario, not proof the prelaunch guard could prevent it. No
speculative filesystem-handle framework is necessary for this feature.

Acceptance matrix for the complete feature:

- Import portable game from configured source, retain AppId/artwork/controller profile/collection
  across removal/reinsertion and changed-letter remount; ensure title remains visible and
  availability/badge update without Steam restart.
- Complete real ROM source: configure/import from Game Mode, choose managed or external emulator,
  preserve per-system/per-title choices, preview/exclude/apply, rescan without duplicates and apply
  selected Screenscraper art. Emulator remains installed while ROM media is absent: Steam library
  and desktop/steam:// launch refuse before starting emulator. Reinsert correct card under new
  letter and render declared ROM argument from the new root with original options/start directory
  preserved.
- Two cards have identical filenames and even identical relative folder layouts; only the recorded
  storage matches. Reused old letter by the wrong card must not launch its file.
- Startup absent, insertion after startup, removal during reconciliation, resume with changed media,
  CEF unavailable, resident WSGM absent, shutdown/cancellation and stale worker results.
- Files with spaces/quotes, exact raw legacy `.lnk` options, explicit ROM token templates,
  program/content on separate volumes, missing emulator versus missing ROM, present volume with
  missing file, unreadable path versus confirmed missing.
- Unknown Steam add followed by restart reconciles one ManagedId/AppId without duplicate; persisted
  state damaged/newer version refuses safely; no helper writes race resident cache.
- Ordinary user/unmanaged shortcuts remain unaffected; hand-edited managed commands are conflicted
  instead of overwritten; intentional cleanup remains distinct from temporary unavailability.
- Large source event targets only its relevant records; no artwork search/full importer scan
  required for availability refresh; batch persistence and no filesystem work on UI thread.
- Actual direct/emulator Steam overlay, controller layout, running/Stop lifetime and focus require
  live testing because adding the helper changes the launch process tree.
- Full ROM parser rules for recursion/extensions/exclusions, direct-supported archives and
  descriptor/companion sets; duplicate overlapping sources, manual route/title overrides, moved file
  relink and actual removal versus absent storage.
- Large bulk Screenscraper import near known daily/negative-result quota, 429 throttling, 430/431
  stop, restart/resume without redoing successful lookups/images or losing chosen matches; automatic
  ROM-first routing and manual provider override.
- Emulator update preserves ROM ManagedId/AppId/Steam art/collections and user emulator choice;
  missing/removed emulator keeps ROM library recoverable. External installation is not silently
  updated. Full #203 provider/install/update/rollback/data-preservation acceptance belongs to its
  research lane.

### Gaps

- Native dialog presentation and controller interaction need implementation proof; no suitable
  general launch-refusal UI IPC was established in this inspection. Do not make a new broad IPC
  framework a prerequisite; the helper-local fallback is sufficient for the minimum friendly
  refusal.
- The exact Steam semantic installed-state/card/badge primitive belongs to the separate CEF research
  track, and media events/volume discovery to the separate Windows storage track.
- No live runtime, large-library I/O measurement, launcher identity retention, direct-child
  overlay/input behavior or hardware swap acceptance was executed by this research.
