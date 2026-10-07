# Unify imports, emulators and storage availability

**The combined feature is feasible as one library subsystem**, extending WSGM's existing importer,
Steam toolkit and launch helpers. Build the full ROM importer and Emulator Manager together so they
produce authoritative content paths and stable emulator references for Issue 200. Keep Steam
shortcuts and artwork intact when storage or an emulator disappears, then resolve their current
paths at launch. Prefer a reversible toolkit availability projection over altering Steam's native
installed state. One shared record model avoids deriving ROM paths from arbitrary launch text. This
is a recommended implementation, not delivered functionality.
([#200](https://github.com/KillerPixelCrew/WSGM/issues/200),
[#47](https://github.com/KillerPixelCrew/WSGM/issues/47),
[#203](https://github.com/KillerPixelCrew/WSGM/issues/203))

Research snapshot: 6 October 2026; WSGM `57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`, toolkit
`d8902b71c92a162f1221df4ebc8f1e642603290d`, WindowsDeviceControl
`79dae8bceca9e987a495df02eb3e6b490fe0870a`. Evidence is inspected source, offline Steam bundle
parsing and documented APIs/release metadata. No build, test, deployment, executable download,
application launch or live Steam mutation occurred.

## Existing ownership is reusable; ROM support is missing

**#47 is closed, but the current source does not contain its full ROM importer.** Its documentation
explicitly excludes ROM folders. Folder import supports `.exe`, `.lnk` and `.url`; this cannot
substitute for ROM configuration and emulator integration.
([game-library.md](D:/Coding/WSGM/docs/game-library.md:67),
[#47 requirements](https://github.com/KillerPixelCrew/WSGM/issues/47))

| Area             | Observed existing source                                                                              | Combined work still required                                                                                          |
| ---------------- | ----------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| Import/sync      | Source adapters, review/apply, exact-field conflicts, in-place AppId updates, collections and artwork | ROM/manual sources, parser profiles, authoritative paths, moved-title relink; EA adapter alongside existing launchers |
| Storage          | Volume-GUID helpers, volume/resume events, card watcher suspension                                    | Generic content availability, mounted-folder resolution, mount-only events and retained offline records               |
| Steam            | Shared grid/Home badge ownership and carousel integration                                             | Per-title unavailable state, mounted refresh, details/Play/context actions                                            |
| Emulators/launch | Download/atomic-write patterns and packaged/follow/input helpers                                      | Four release providers, definitions, installations, transactions and managed-record launch resolution                 |

Extend `Core/Library` and its existing atomic store; use focused `Core/Emulators` policy and Shell
services, composed by `ShellSession`. Toolkit owns Steam rendering. The helper reads a fresh shared
DTO snapshot without writing the resident store. No second importer, badge transport or plugin
framework is needed. Existing shortcut updates already preserve AppId rather than remove/re-add.
([SteamShortcutWriter.cs](D:/Coding/WSGM/src/WSGM/Core/Library/SteamShortcutWriter.cs:68))

| Shared record    | Proposed minimum fields                                                                                                   |
| ---------------- | ------------------------------------------------------------------------------------------------------------------------- |
| Logical source   | Stable SourceId, kind/name, system/parser profile, scan rules, preferred InstallationId                                   |
| Physical binding | Expected local volume GUID plus relative root, or configured UNC root; last mount is a hint                               |
| Title            | ManagedId, source/content key, primary ROM and required companions, SystemId, per-title emulator/profile overrides        |
| Emulator         | DefinitionId/revision, InstallationId, managed/external ownership, active/previous version and persistent data paths      |
| Steam mapping    | Active Steam account plus confirmed unsigned AppId; exact written target/options/start directory                          |
| Availability     | Unknown/available/unavailable; media/content/emulator/dependency/access reason, generation and last successful validation |

In Game Mode, configure system/root/profile, recursion, extensions/exclusions and compatible
installed or installable emulators. Provide manual titles, per-system defaults and per-title
exceptions. Scan into the existing dry-run preview: show title, backing files, location, launch
command, duplicate/conflict choices and artwork before applying. Descriptor/playlist profiles
produce one title with declared companions; archives are accepted only when that emulator supports
them. Preserve Xbox/package routes and explicit input-mode choices.
([#47](https://github.com/KillerPixelCrew/WSGM/issues/47),
[SRM parser/configuration model](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/src/models/user-configuration.model.ts#L17-L61))

Route ROM artwork to system-aware Screenscraper identification first, PC sources to SteamGridDB;
retain manual provider/match selection. Fall back to the other provider only when the first has no
match, never on error or quota refusal. Add live quota accounting and durable enrichment/application
progress, pausing on 430/431 and resuming unfinished work after restart. Repeated scans preserve
overrides, selected art, user edits and collection membership.
([Screenscraper API](https://www.screenscraper.fr/webapi2.php))

## Resolve content at launch and refresh Steam reactively

Use **volume GUID identity on the same Windows installation**, resolving its current usable drive or
mounted-folder path for emulator arguments. GUID persistence is not a cross-machine guarantee;
filenames, labels, disk numbers and filesystem serials cannot authorize blind rebinding. Preserve
existing source IDs when letters change: current folder IDs originate from absolute-path hashes, so
removing/re-adding under another letter needs explicit migration. Legacy offline records remain
pending until their authoritative source can be verified.
([Microsoft volume naming](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-volume),
[Mount Manager](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/supporting-mount-manager-requests-in-a-storage-class-driver),
[current mount paths](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamesforvolumenamew))

Reconcile remembered sources at startup, then use volume/resume notifications and one owned
mount-name notification for letter/folder-only changes. Coalesce topology events, invalidate stale
generations and check only affected sources. Missing volumes invalidate their titles without per-ROM
I/O. Reachable roots use source watchers and indexed path/prefix checks; overflow triggers bounded
reconciliation. Events/watchers lead, with occasional low-frequency affected-source validation and
no constant per-title polling. Respect eject suspension, batch results/persistence and isolate UNC
checks with bounded concurrency/backoff. Do not hash libraries on insertion or repeatedly scan
sleeping disks.
([Mount-change notification](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/mountmgr/ni-mountmgr-ioctl_mountmgr_change_notify),
[watcher limitations](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesystemwatcher?view=net-10.0))

This research did not establish a supported Steam-client API for marking non-Steam shortcuts
unavailable; inspected offline code shows installed state alone is not a universal launch gate.
([Offline Steam bundle](<C:/Program Files (x86)/Steam/steamui/chunk~2dcc5aaf7.js:1>)) Current WSGM
Home treats shortcuts as installed and separately excludes disconnected AppIds. Therefore **keep
unavailable managed shortcuts visible**, using a small typed availability map consumed by the
existing tile/badge owner; retain Home positions, grey artwork, explain the location/reason and
offer Recheck. Refresh mounted tiles/details reactively while preserving focus and normal/Big Art
layouts. Do not alter `app_type`, shortcut identity or the global installed store. Native filter
behavior remains a live proof gap.
([home-carousel.ts](D:/Coding/WSGM/external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/home-carousel.ts:128))

A CEF Play/context/RunGame guard improves UX but cannot cover protocol or external launches.
**Recommend `WSGM.PackagedLaunch --managed <ManagedId>` as the stable shortcut target**, allocating
the record before Steam creation and reconciling uncertain adds without duplication. Add a direct
child-launch mode preserving Steam environment, overlay/input policy and running/Stop lifetime;
today's detached follow route is not equivalent. This mode is proposed, not implemented or
compatibility-proven. Recheck expected storage, ROM/companions, emulator and dependencies
immediately before launch; provide helper-local refusal such as "Insert SD Card - Retro, then
Recheck" when CEF/resident WSGM is absent. Catch the final launch failure because unplug can still
race validation. Render explicit path tokens into argument arrays; never rewrite arbitrary legacy
arguments.
([Launch helper constraints](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM.PackagedLaunch/AGENTS.md),
[File.Exists limitations](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-10.0))

## Emulator updates switch versions while preserving ownership

Implement **GitHub, Forgejo, Gitea and Scoop** normalization with configurable hosts/subpaths,
pagination, channels, release notes and deterministic OS/architecture/asset selection. GitHub latest
excludes prereleases. Scoop contributes allowlisted manifest metadata, never executable
installation/checkver scripts. Curated, independently updateable definitions specify supported
systems, layout, dependencies, launch/data paths and reviewed validation/migration operations. Keep
upstream/manifest hash verification distinct from a locally computed receipt digest.
([GitHub releases](https://docs.github.com/en/rest/releases/releases),
[Forgejo API](https://forgejo.org/docs/latest/user/api/usage/),
[Gitea releases](https://docs.gitea.com/api/operations/repo-list-releases/),
[Scoop manifests](https://github.com/ScoopInstaller/Scoop/wiki/App-Manifests))

PCSX2 is a useful first real package: documented `-datapath`, bounded version/config probes and
batch ROM launch. DuckStation supplies a Scoop/ZIP case; PCSX2/mGBA `.7z` needs an extractor
dependency. DuckStation/mGBA data-path contracts still require verification. **No real official
Windows emulator release source was verified on Forgejo or Gitea**; real provider scenarios remain
required, beyond adapter fixtures. ([PCSX2 CLI](https://pcsx2.net/docs/advanced/cli/),
[DuckStation upstream](https://raw.githubusercontent.com/stenzek/duckstation/master/README.md),
[mGBA downloads](https://mgba.io/downloads.html))

Download to bounded private staging, verify supplied hashes/signatures and extract with
containment/link/size checks. Validate expected layout and run reviewed noninteractive
version/config checks where supported before accepting the active version in its atomic receipt;
roll back on failure. Retain previous binaries and configuration snapshots for
interrupted/failed-update recovery; rollback must never overwrite newer saves. Separate program,
config, saves, cache and firmware. Permit only locally supplied or vendor-authorized redistributable
firmware, validate through reviewed checks and preserve it separately from binary updates. Serialize
launcher admission and activation across processes; while an emulator runs, stage updates but defer
activation/removal, checking actual executable paths including manual external starts.
([#203](https://github.com/KillerPixelCrew/WSGM/issues/203))

Detect known external installations or accept a selected executable, validate compatible/version
state and grant use only. Migration is explicit preview/copy; external files/data stay unowned.
Manager controls include install, latest/version/source/channel/notes, update, skip version, repair,
per-system preference and removal with dependent-title explanation. Removal preserves ROM records
and user data, yielding `EmulatorUnavailable` until replacement/reinstall. Automatic update policy
can follow later as #203 permits; #118's plugin repository work is not a prerequisite.
([#203](https://github.com/KillerPixelCrew/WSGM/issues/203),
[#118](https://github.com/KillerPixelCrew/WSGM/issues/118))

## Six increments deliver the complete combined scope

These are implementation increments, not separate reductions of the requested outcome.

| Order | Work and user-visible acceptance                                                                                                                                                          |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1     | Shared schema/migration and managed resolver: original shortcut identity survives remount; friendly launch refusal works without CEF.                                                     |
| 2     | All provider adapters/definitions and version transaction: real GitHub/Scoop/Forgejo/Gitea install paths, firmware/dependencies, update/repair/rollback/remove preserve data and choices. |
| 3     | Full ROM/manual profiles plus EA completion: controller/touch configure, preview, exclusions, managed/external selection, conflict-aware sync and real launch.                            |
| 4     | Artwork completion: selected Screenscraper art applies; quota exhaustion pauses and restart resumes without losing completed choices.                                                     |
| 5     | Storage availability and toolkit projection: offline records stay visible, location/reason updates promptly, correct remount restores launch without Steam restart.                       |
| 6     | Build/deploy and attended acceptance: full importer/manager/storage scenarios, then focused tests under the repository's manual-first rules.                                              |

## Completion requires real launches and media transitions

First prove one retained shortcut changes available/unavailable/available in grid and Home, normal
and Big Art layouts, with focus retained and details Play/context/protocol launches correctly
handled. Then prove physical removal/reinsertion, absent startup, letter-only/folder-only remount,
resume, wrong-card identity, file deletion/access failures and large-library event handling without
excessive I/O. Build/fixtures cannot establish these results.

Full delivery also requires real ROM imports and emulator launches, unknown-add recovery, preserved
Steam edits/artwork/collections, all four provider scenarios, update/launch races including manually
opened emulators, interrupted rollback and save/config preservation. This research establishes a
concrete route; those runtime, package and hardware proofs remain outstanding.
