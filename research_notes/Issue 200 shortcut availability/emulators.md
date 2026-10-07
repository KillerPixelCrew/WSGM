# Emulator Manager research for #203 with #47 and #200

Research-only snapshot: 2026-10-06, WSGM master `57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`. No
production edits, executable downloads, installs, application launches, Steam mutation, builds or
tests. Requirements read in full from [#203](https://github.com/KillerPixelCrew/WSGM/issues/203) and
[#118](https://github.com/KillerPixelCrew/WSGM/issues/118).

## Which existing WSGM mechanisms can we reuse?

### Takeaway

Reuse small download, atomic-write, path-containment and journal patterns. WSGM does not currently
contain a general emulator repository/install/update service, and emulator packages should remain
separate from Plugin SDK packages.

### Cited Findings

- `UpdateChecker` uses the fixed WSGM GitHub latest-release endpoint, filters prereleases/drafts,
  requires a WSGM setup plus checksum sidecar, and starts the setup. It is application-specific
  policy, not a reusable release provider. Its streaming download writes `.partial`, hashes it,
  rejects mismatches and renames only after verification; this mechanism is useful to factor into a
  bounded downloader.
  [UpdateChecker.cs:120](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/UpdateChecker.cs#L120),
  [download:165](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/UpdateChecker.cs#L165),
  [release parser:228](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/UpdateChecker.cs#L228).
- `BoundedHttp.CopyAsync` provides cancellation, size bounds, progress and a read-stall timeout.
  `AtomicFile.Write` writes a sibling temporary file, flushes durable writes and then replaces the
  destination.
  [BoundedHttp.cs:49](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/BoundedHttp.cs#L49),
  [AtomicFile.cs:54](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/Shared/Boot/AtomicFile.cs#L54).
- `PluginPackageManager` installs a bundled file after checking its bundle hash, copies to
  `.incoming`, moves to a flat Plugins directory and requests restart. Removal is scoped to that
  directory and can defer a locked file. It does not supply hosted definitions, portable archive
  layouts, channels, emulator data handling or transactional multi-file updates.
  [PluginPackageManager.cs:240](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/PluginPackageManager.cs#L240).
- `SetupFileTransaction` has recover/begin/commit/rollback and a journal, but hardcodes App,
  Plugins, Packages, Setup and Bundle targets and setup registration. Reuse its recovery discipline;
  invoking the entire setup engine for emulator updates would also import unrelated machine/service
  policy.
  [SetupFileTransaction.cs:20](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Setup/Engine/SetupFileTransaction.cs#L20),
  [rollback:137](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Setup/Engine/SetupFileTransaction.cs#L137).
- Existing library policy includes shortcut folders, but no ROM-system or emulator records. Its
  import store already writes through `AtomicFile`; new durable emulator state should follow the
  same strict mutation pattern. `UserDataContext` supplies the current user's LocalAppData WSGM
  root.
  [GameLibraryConfig.cs:57](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/GameLibraryConfig.cs#L57),
  [ImportStateStore.cs:534](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/ImportStateStore.cs#L534),
  [UserDataContext.cs:13](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/UserDataContext.cs#L13).
- Launch creation already constructs an argument array using `ProcessStartInfo.ArgumentList`; ROM
  paths should become typed arguments, never shell commands.
  [WSGM.Launch Program.cs:557](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.Launch/Program.cs#L557).

### Inferences

- Proposed ownership: `Core/Emulators` owns definitions, provider adaptation, install receipts and
  the resolver contract; one focused Shell emulator service owns background operations and UI
  snapshots, composed alongside the Game Library. Importer and manager use the same records. Do not
  put policy in a view or expand `ShellSession` into the implementation.
- Extract common primitives only when this increment gives them two concrete callers. #118's larger
  signed plugin repository/hot-unload work is not a prerequisite for #203. A separately updateable
  curated definition JSON index can reuse HTTPS/hash/download patterns without loading plugin
  assemblies.
- Versioned program directories plus one atomic installation record are simpler than junction
  management or replacing running executables. Preserve previous version until the new one passes
  definition-specific validation; rollback updates the record.

### Gaps

- No existing generic signed repository/definition service was found in the inspected update/plugin
  paths. Archive handling inspected in WSGM is ZIP-based; WSGM.csproj contains no 7z reader
  dependency. mGBA/PCSX2 portable packages therefore require a reviewed extractor dependency, or
  another officially supported package format. This remains implementation work, not an existing
  capability.

## How should the four providers and initial emulator definitions work?

### Takeaway

Providers normalize release descriptions; curated emulator definitions decide platform support,
package selection, layout, launch and data preservation. Support all four requested providers, while
keeping actual emulator install proof distinct from API compatibility proof.

### Cited Findings

- GitHub's release-list API exposes tags, draft/prerelease status, assets and notes. `/latest`
  excludes prereleases, so nightly/channel discovery must use lists or definition-selected tags.
  Asset digests appear in API examples but cannot be assumed present.
  [GitHub Releases API](https://docs.github.com/en/rest/releases/releases).
- Gitea release listing uses `/api/v1/repos/{owner}/{repo}/releases` and exposes attached asset
  names, size, `browser_download_url`, notes and prerelease status. The documented attachment schema
  inspected does not promise an upstream checksum field.
  [Gitea release-list API](https://docs.gitea.com/api/operations/repo-list-releases/).
- Forgejo exposes instance-specific API documentation at `/api/swagger` and schema at
  `/swagger.v1.json`; it documents pagination and instance-specific limits. Host/base path belongs
  in a definition, not a github.com constant.
  [Forgejo API usage](https://forgejo.org/docs/latest/user/api/usage/). Codeberg uses Forgejo, but
  that alone says nothing about a particular emulator's Windows assets.
  [Codeberg launch announcement](https://blog.codeberg.org/codeberg-launches-forgejo.html).
- Scoop manifests support architecture sections (`32bit`, `64bit`, `arm64`), download/hash/extract
  metadata and executable hints. They also contain executable scripts and persistence instructions.
  Consume an allowlisted metadata subset; never run `pre_install`, `post_install`,
  installer/uninstaller, checkver scripts or shims.
  [Scoop manifest format](https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests),
  [autoupdate format](https://github.com/ScoopInstaller/Scoop/wiki/App-Manifest-Autoupdate).
- mGBA's official stable download is 0.10.5 with Windows x64 portable `.7z`; development builds are
  a separate buildbot/S3 source, not GitHub prereleases. Its Games-bucket manifest supplies the x64
  URL, checksum, extraction root and executable hint. A live GitHub metadata read found null digests
  for both Windows portable assets, so a computed receipt cannot be called upstream-verified.
  [mGBA downloads](https://mgba.io/downloads.html),
  [mGBA manifest](https://raw.githubusercontent.com/Calinou/scoop-games/master/bucket/mgba.json),
  [release API](https://api.github.com/repos/mgba-emu/mgba/releases/latest).
- PCSX2 offers a particularly useful first managed definition: documented `-datapath` separates all
  data from the binary, `-version` and `-testconfig` exit after checks, and
  `-fullscreen -batch -- <ROM>` has a documented launch contract. Live release metadata returned
  nightly `v2.9.103` with `pcsx2-v2.9.103-windows-x64-Qt.7z` and a SHA-256 digest; a symbols archive
  is also present and must be excluded. [PCSX2 CLI](https://pcsx2.net/docs/advanced/cli/),
  [PCSX2 releases](https://github.com/PCSX2/pcsx2/releases).
- DuckStation publishes Windows x64/ARM64 ZIP archives with no enclosing directory, a fixed Qt
  executable, and a Visual C++ runtime dependency. Its default user data lives in LocalAppData;
  older installations may use Documents. Portable mode places data beside the executable.
  [DuckStation upstream README](https://raw.githubusercontent.com/stenzek/duckstation/master/README.md).
- The current Games-bucket DuckStation manifest was verified with a direct raw read as
  `20261006-g697599c`, with x64/ARM64 URLs and hashes and a persistence list for BIOS, memory cards,
  settings, save states, controller profiles and caches. The web fetch's cached response was a year
  old; do not use its older version/hash as current.
  [DuckStation manifest](https://raw.githubusercontent.com/Calinou/scoop-games/master/bucket/duckstation.json).

### Inferences

- Proposed provider result: immutable source/repository identity, release identity/version, channel,
  publish timestamp, notes URL/body, asset identities/names/URLs/sizes and optional expected
  checksum/signature. GitHub and two explicitly named Forgejo/Gitea adapters may share
  parser/request code where confirmed; do not assume all server versions are identical. Preserve
  custom host and subpath, pagination, rate limits, cancellation and last successful metadata when
  offline.
- Deterministic selection means exact OS/architecture/channel plus definition-specific name/template
  rules, with an error on zero or multiple matches. Exclude source archives, symbols/debug and
  installers unless explicitly supported. Do not select the first `.zip`. Architecture includes
  runtime/CPU requirements; an ARM64 package's existence does not prove today's x64 WSGM itself runs
  natively on ARM64.
- Scoop provider first reads the current manifest at a pinned bucket revision and selects the
  architecture-specific URL/hash/extract fields. Record the bucket revision, manifest hash and
  upstream URL. `checkver/autoupdate` are optional declarative metadata; no generic remote script
  engine is needed. Upstream rolling `latest` URLs can change before the bucket hash does: fail the
  mismatch and refresh metadata, never bypass verification.
- Expected upstream/manifest checksum verification and locally computed archive digest are separate
  receipt fields. With no upstream hash/signature, record HTTPS source plus local digest and report
  that upstream integrity metadata was unavailable. A local digest alone cannot authenticate a
  download.
- Start with a small supported set: PCSX2 for GitHub stable/nightly, DuckStation for Scoop/ZIP and
  x64/ARM64 selection, and optionally mGBA as a second real system/backend definition. This covers
  diverse package behavior without creating an unverified catalog. Windows ARM64 launch proof
  requires appropriate hardware later.

### Gaps

- No currently maintained, official Windows emulator release source was verified on Forgejo or Gitea
  within this bounded research. An attempted Ryujinx-host URL was inaccessible and is not evidence.
  Keep both backend implementations and acceptance requirements; choose verified real upstream
  projects before claiming complete #203 delivery. Controlled API/package fixtures can prove adapter
  parsing/error behavior but do not replace the requested real emulator scenarios.
- DuckStation user-directory override and the full mGBA launch/data override contract need targeted
  source validation before writing their reviewed definitions. Preserve default external data as
  unowned if used, or configure a supported separate data directory. Do not blindly transplant Scoop
  persistence scripts.
- No archives were downloaded or extracted; package contents, dependencies and actual
  startup/gameplay remain unverified. Definition claims from docs/metadata need package and runtime
  checks at implementation time.

## What coherent model, lifecycle and acceptance cover all #203 requirements?

### Takeaway

The ROM importer should retain emulator installation identity independently of ROM content and
resolved versioned executable paths. A managed launch resolver makes updates/removal reversible and
gives #200 explicit emulator availability without changing shortcut identity.

### Cited Findings

- #203 requires four reusable providers, data-driven definitions, existing/external detection,
  preserved user data, firmware configuration, update/repair/removal, stable shortcuts, per-system
  choice and a complete validation matrix. Those are acceptance requirements, not optional catalog
  metadata. [Issue #203](https://github.com/KillerPixelCrew/WSGM/issues/203).
- #118 requests common download/integrity/staging/repository primitives, but its plugin signatures,
  SDK range and collectible-context upgrade contract concern plugin packages.
  [Issue #118](https://github.com/KillerPixelCrew/WSGM/issues/118).

### Inferences

Proposed records and boundaries:

| Record                   | Required contents                                                                                                                                                                                                                                                                                                                                                                                                                              |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Emulator definition      | Stable ID/name; supported system IDs; provider/base URI/repository; explicit channels/architectures; deterministic asset rules; package format/extract root; expected executable/dependencies/runtime requirements; install layout; typed launch arguments and per-system overrides; working directory; version detection; reviewed migration/validation hook IDs; program/config/save/cache/firmware path policy. No downloaded code/scripts. |
| Installation receipt     | Installation ID plus definition ID/revision; managed/external ownership; managed root or explicitly accepted external executable; source/channel/architecture/version/release/asset identity; expected hash and verification result; local archive digest; active/previous/staged versions; persistent data paths and their ownership; skipped version; last update result.                                                                    |
| ROM system configuration | Stable system/source ID; roots; preferred installation ID; compatible alternatives and per-system launch overrides. A preferred emulator is policy, not a baked versioned executable path.                                                                                                                                                                                                                                                     |
| Imported ROM record      | Existing library identity plus system ID, content root/relative ROM path or content references, selected installation ID and launch profile. #200 evaluates ROM content and emulator requirements separately; removing the emulator keeps this record recoverable.                                                                                                                                                                             |

Proposed lifecycle:

1. Detect managed receipts first, then reviewed known external locations/registry/App Paths/Start
   Menu hints or a user-selected path. Validate executable and version where supported; ambiguous
   results are offered for selection. External discovery grants use only. Never
   install/update/remove external files or claim unknown user data. Migration is an explicit
   preview-and-copy action that leaves the old installation available.
2. On system import, show compatible installed/available emulators, ownership and version/channel.
   Offer install directly in the controller/touch library flow; retain normal externally installed
   use. The same manager surface supplies source/notes, install/update/skip
   version/channel/preferred system/repair/remove. Firmware setup accepts locally supplied paths and
   definition-approved presence/hash validation; keep firmware outside normal binary updates and
   never find unauthorized download sources.
3. Download HTTPS assets to private staging with bounded sizes and progress. Enforce allowed
   redirect protocol, verify supplied upstream/Scoop hashes/signatures, reject mismatches, and
   record provenance. Extract into a new owned version root with path-traversal/reparse/link
   protections, size/file limits and expected layout validation. Never merge untrusted archives into
   live program or data roots.
4. Serialize launch admission and activation per installation across the launcher and manager. Check
   actual running executable paths under managed versions, not process names or only WSGM wrappers;
   an unreadable process state cannot establish safe activation. While running, downloading/staging
   may continue but activation/removal waits. Keep admission closed from final check through pointer
   commit so a new launch cannot race the switch. Existing external starts remain outside WSGM
   control, so retain old files rather than deleting a possibly mapped version.
5. Attach stable persistent user data via a verified emulator data-path option where possible. PCSX2
   `-datapath` is the clean first example. If an emulator requires beside-executable data, its
   reviewed definition must snapshot/migrate the exact data set before activation, and preserve user
   data backups across rollback. A failed new version may migrate configuration incompatibly, so
   binary-only rollback is insufficient: preserve prior configuration where migration applies and
   never roll back game saves by overwriting newer saves.
6. Validate expected files/layout before atomically recording `active + previous` in one receipt.
   Run only a definition-approved noninteractive bounded probe where documented (PCSX2
   `-version`/`-testconfig`), in the correct data context; an arbitrary GUI start is not a generic
   validation method. On failure restore previous activation/config snapshot. On restart recover a
   pending transaction from the receipt without mixing two versions. Retain the prior version until
   success is established; prune later after proving it is not in use.
7. Stable WSGM launcher arguments identify imported entry/installation, resolving current version
   and working directory at launch. Reuse existing de-elevation/input-lease/argument-array
   mechanisms. Import IDs, Steam shortcut IDs and ROM paths do not change just because an emulator
   updates. Removing a managed emulator shows dependent systems/count and offers
   replacement/reinstall; preserve ROM/import records and user data by default. Never delete
   external data. Repair uses the same verified install pipeline with the stored definition/source,
   not a separate destructive path.

Compact complete requirement coverage:

| Requirement area                          | Proposed completion and proof                                                                                                                                                                                                                                   |
| ----------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Full importer integration/definitions     | Shared system/install/ROM records; compatible choices; no mandatory manual executable browsing for curated definitions. Demonstrate controller-only configure, install, import and launch.                                                                      |
| Four providers/configurable hosts         | GitHub, Forgejo, Gitea and Scoop adapters with reusable normalization and deterministic architecture/channel matching. Real upstream install scenario per provider remains required.                                                                            |
| Integrity/install/update/rollback         | HTTPS and supplied hash/signature checks; local digest separately recorded; safe extraction/new version root; persisted activation journal. Inject corrupt/truncated download, checksum mismatch, failed extraction/probe, interrupted activation and rollback. |
| Data/dependencies/firmware                | Explicit program vs persistent vs cache vs optional firmware paths; runtime dependency readiness and user-owned firmware selection. Prove settings, mappings, cards, saves and per-game config survive update; assert remove preserves them.                    |
| Launch/update concurrency                 | Cross-process admission serialization and running-image identity checks; update while launched, manually opened emulator and startup race. Demonstrate deferred activation and no mapped-file replacement.                                                      |
| Existing/external/multiple emulators      | Known external detection/use; explicit migration; per-system preference and compatible alternatives. Prove external executable/data remain untouched on managed update/removal.                                                                                 |
| UI/update policy/removal                  | Versions/latest/state/provider/channel/notes; install/update/skip version/repair/remove, dependent warning/recovery. Automatic update policy can follow later because issue explicitly permits that.                                                            |
| Independent definition updates/#118 reuse | Curated versioned JSON catalog fetched independently with trusted integrity, validation and last known good cache; common primitives without plugin SDK/ALC dependency. Definition schema changes require compatibility gating.                                 |
| #200/stability/restart                    | Emulator unavailability produces a recoverable launch condition separate from ROM media loss. Reboot, update, repair and removal/reinstall leave the original imported shortcut identity working again.                                                         |

Smallest coherent implementation order, preserving the complete scope:

1. Define shared system/ROM/installation IDs and version resolver; wire external-use and stable
   shortcut launch into the full importer.
2. Add curated definition parsing and provider normalization for all four sources, with
   deterministic selection and metadata integrity rules.
3. Build one portable package transaction path, persistent data handling and launch/activation
   coordination; finish one real PCSX2 GitHub install/update/rollback through the UI.
4. Finish Scoop/ZIP DuckStation and verified real Forgejo/Gitea emulator definitions; then connect
   all manager actions, per-system choices, firmware and dependent shortcut recovery.
5. Complete the issue's failure/concurrency/restart/data-preservation matrix, build/deploy the
   resulting feature under the maintainer's manual-first policy, and distinguish compile/fixture
   proof from attended Game Mode and hardware acceptance.

### Gaps

- Storage lane must decide content identity and removable-root handling; launch lane must choose the
  shared resolver entry point. This note does not prescribe a second independent launcher/data
  store.
- Before claiming #203 complete, choose real Forgejo/Gitea upstream emulators, verify full package
  layouts and reviewed launch/data hooks, and execute actual
  importer/launch/update/save-preservation scenarios. This research does not claim those outcomes.
