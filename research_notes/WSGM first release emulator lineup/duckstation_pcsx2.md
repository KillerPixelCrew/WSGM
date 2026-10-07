# First-release DuckStation and PCSX2 definitions

Research snapshot: 6 October 2026, WSGM `57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`. Sources were
read as text/JSON only. No ROM, BIOS, emulator archive or executable was downloaded; no scripts,
applications, tests, builds or installations were run. The earlier combined research remains
unchanged. This note narrows the agreed lineup to these two emulators; other lanes own RetroArch,
RPCS3, Eden and Dolphin.

## Which exact package sources, channels and hashes should v1 consume?

### Takeaway

Use curated Scoop Games metadata with official GitHub asset cross-checks for both. The canonical
repository is now `ScoopInstaller/Games`; former `Calinou/scoop-games` redirects there. Actual
channel manifests are `duckstation`, `duckstation-preview`, `pcsx2`, and `pcsx2-dev`, not an
invented `pcsx2-nightly` manifest.

### Cited Findings

- Live GitHub contents/commit API reads resolved canonical Games identity and current revision
  `c9f4e2f74c75285b111beb0839a7eb729f16fada`. The following pinned files are the precise metadata
  sources:
  [duckstation.json](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/duckstation.json),
  [duckstation-preview.json](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/duckstation-preview.json),
  [pcsx2.json](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/pcsx2.json),
  [pcsx2-dev.json](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/pcsx2-dev.json).
  `pcsx2-nightly.json` returned 404; the canonical contents listing contained no DuckStation UWP
  manifest. [Games bucket API](https://api.github.com/repos/ScoopInstaller/Games/contents/bucket).
- DuckStation's current ordinary Windows release publishes x64, ARM64 and separate legacy x64-SSE2
  ZIPs. Normal x64 and ARM64 hashes match today's Scoop metadata. These are standalone Qt
  applications, not UWP/AppX packages. Latest release ID is `404547563`, published 2026-10-06
  09:49:07 UTC; preview ID is `404461956`, published 08:03:44 UTC.
  [Latest release API](https://api.github.com/repos/stenzek/duckstation/releases/tags/latest),
  [preview API](https://api.github.com/repos/stenzek/duckstation/releases/tags/preview),
  [upstream Windows instructions](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/README.md#L75).
- PCSX2 stable `v2.8.2` offers the x64 Qt 7z, a symbols 7z and an installer. Its portable asset hash
  matches Scoop stable. Today's development/nightly manifest is `2.9.103`, selecting `v2.9.103` x64
  Qt 7z with a hash matching the official API read in the earlier research.
  [Stable API](https://api.github.com/repos/PCSX2/pcsx2/releases/latest),
  [nightly release](https://github.com/PCSX2/pcsx2/releases/tag/v2.9.103),
  [official stable/nightly description](https://pcsx2.net/docs/setup/running/).

Exact metadata snapshot; SHA-256 values are upstream/manifest expectations, not hashes measured from
downloaded files:

| Definition/channel         | Display version     | Official portable asset URL                                                                              | Expected SHA-256                                                   |
| -------------------------- | ------------------- | -------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------ |
| DuckStation latest, x64    | `20261006-g697599c` | `https://github.com/stenzek/duckstation/releases/download/latest/duckstation-windows-x64-release.zip`    | `4813f22823e6aa262c46a4a516f10b01e1e2ddf6fcea82763e359ef9ac812a9d` |
| DuckStation latest, ARM64  | `20261006-g697599c` | `https://github.com/stenzek/duckstation/releases/download/latest/duckstation-windows-arm64-release.zip`  | `6f1515d962e470f48ff87a710aaea67197932fa8c0331064753f52c65fd35ea1` |
| DuckStation preview, x64   | `20261006-g697599c` | `https://github.com/stenzek/duckstation/releases/download/preview/duckstation-windows-x64-release.zip`   | `d1b6561bf252e07607687fd3dd14816662672ce8f7c794c2042f18fa0210b1a2` |
| DuckStation preview, ARM64 | `20261006-g697599c` | `https://github.com/stenzek/duckstation/releases/download/preview/duckstation-windows-arm64-release.zip` | `7ee4fb95cb092a8acd72c104b355b9b66632b0787453a354e81b485ab8d0329d` |
| PCSX2 stable, x64          | `2.8.2`             | `https://github.com/PCSX2/pcsx2/releases/download/v2.8.2/pcsx2-v2.8.2-windows-x64-Qt.7z`                 | `7dfc829ca1994cc1045ac49f05e39b6cf968b72e6a374c40e05c2a2b4ac200b4` |
| PCSX2 nightly, x64         | `2.9.103`           | `https://github.com/PCSX2/pcsx2/releases/download/v2.9.103/pcsx2-v2.9.103-windows-x64-Qt.7z`             | `a4a876ee82364c9078cf3ce0ea7b1fd7d42fd0614a5d27768268dc9e690d2c0b` |

Sources for the table: the four pinned manifests and official API/release links above. The stable
DuckStation Scoop ARM64 URL spells `ARM64`, while the actual official asset name is lower-case
`arm64`; use the exact selected API asset URL when cross-checking, and do not silently fabricate a
case-normalized URL. No HTTP archive fetch was made to establish whether the differently cased URL
redirects.

### Inferences

- Recommended source policy: Scoop metadata determines the curated offer/channel/version/hash,
  official metadata confirms owner, exact asset name, size and digest. If source metadata disagrees
  or a rolling asset changes before Scoop updates, reject/defer with a clear stale-metadata result;
  never bypass the manifest hash.
- PCSX2 channel mapping is stable=`games/pcsx2`, nightly=`games/pcsx2-dev`. Official prerelease list
  can cross-check nightly and its digest; do not copy the manifest's fragile `per_page=1` assumption
  as a generic nightly algorithm.
- DuckStation channel names should be `latest`/`preview`, matching upstream, rather than claiming a
  semantic stable/nightly policy absent from these sources. Its rolling releases can replace assets.
  Today's latest and preview display versions are identical but their archive hashes differ:
  update/skip identity needs source+channel+release/asset revision or digest, not only a version
  string.
- Keep definition IDs `duckstation` and `pcsx2` stable across channels/versions. Allocate a distinct
  stable InstallationId for a selected managed/external install; changing channel need not change
  imported shortcut identity. Multiple independently configured installations may have different
  InstallationIds.

### Gaps

- No archive was extracted. Expected exe/resource/DLL layout must be checked during implementation.
  DuckStation docs expressly state the ZIP has no enclosing root directory. PCSX2's current manifest
  has no top-level `extract_dir`, while its unused stable `autoupdate.extract_dir` says
  `PCSX2 $version`; do not interpret autoupdate-only metadata as the current archive root. Official
  instructions show extracting into a chosen folder and `pcsx2-qt.exe` there.
  [DuckStation layout](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/README.md#L89),
  [PCSX2 layout](https://pcsx2.net/docs/setup/running/).
- PCSX2's verified Windows offers here are x64; do not advertise a native Windows ARM64 package.
  DuckStation ARM64 asset selection is verified metadata, not runtime proof on ARM64 WSGM.

## What launch behavior do SRM and EmuDeck establish, and what does upstream confirm?

### Takeaway

Start with SRM's standalone parser presets, adapt them to WSGM typed arguments and fresh managed
path resolution, and verify flags against official CLI/source. EmuDeck Windows is valuable setup
evidence, but its PowerShell wrappers and bespoke renames are not the WSGM launch contract.

### Cited Findings

- SRM commit `bd66e5f4ef1eb0b4855bbd216063f547f1468368` preset `Sony PlayStation - DuckStation`,
  version 20, uses `-batch -fullscreen "${filePath}"`; StartIn is empty. Its glob accepts
  cue/chd/ecm/iso/m3u/mds/pbp with upper/lower-case variants. The Flatpak variant is a separate
  Linux target.
  [SRM PS1 preset:1](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation.json#L1).
- SRM `Sony PlayStation 2 - PCSX2`, version 20, uses `"${filePath}" -batch -fullscreen -nogui`;
  StartIn is empty. Its standalone glob accepts bin/chd/cso/dump/gz/img/iso/mdf/nrg. Do not borrow
  the separate RetroArch PCSX2 glob's archive/playlist support as standalone capability.
  [SRM PS2 preset:1](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation%202.json#L1).
- DuckStation current official parser implements `-batch`, `-fullscreen`, `-nogui`, `-bigpicture`,
  `-version` and `--`. It rejects unknown dash options with a dialog; inspected parser has no
  `-userdir` or `-settings`. `-version` prints version then returns false, and current main converts
  that result to `EXIT_FAILURE`: a probe cannot universally require exit code 0.
  [DuckStation qthost.cpp:3325](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/duckstation-qt/qthost.cpp#L3325),
  [parser:3366](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/duckstation-qt/qthost.cpp#L3366),
  [main:3615](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/duckstation-qt/qthost.cpp#L3615).
- PCSX2 documents `-datapath <path>` for all application data; `-portable` overrides it. It
  documents `-fullscreen`, `-batch`, `-nogui`, `--`, `-version` and `-testconfig`.
  [Official CLI](https://pcsx2.net/docs/advanced/cli/).
- EmuDeck Windows source snapshot `9d459968b2f9c135bb88853548b54109eb763898` uses normal standalone
  executables. Its PCSX2 installation renames upstream `pcsx2-qt.exe` to `pcsx2-qtx64.exe`; the
  wrappers point to their own fixed AppData EmuDeck locations and perform a Git pull/cloud-wrapper
  setup. WSGM should retain the upstream filename and direct typed launch.
  [PCSX2 setup:1](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckPCSX2QT.ps1#L1),
  [DuckStation wrapper:1](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/tools/launchers/duckstation.ps1#L1),
  [PCSX2 wrapper:1](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/tools/launchers/pcsx2.ps1#L1).
- EmuDeck Windows SRM configurations use cmd/PowerShell launchers, PS1 explicit `-fullscreen`, and
  PS2 a ROM argument; PS2 config sets `StartFullscreen=true`. These are Windows examples, unlike
  EmuDeck `.sh` or SRM Flatpak wrappers.
  [PS1 Windows SRM config:9](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/configs/steam-rom-manager/userData/parsers/emudeck/sony_psx-duckstation.json#L9),
  [PS2 config:6](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/configs/steam-rom-manager/userData/parsers/emudeck/sony_ps2-pcsx2.json#L6),
  [PCSX2 INI:7](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/configs/PCSX2/inis/PCSX2.ini#L7).

### Inferences

Proposed reviewed launch definitions, rendered as individual `ProcessStartInfo.ArgumentList` items:

| Emulator               | Resolved executable                                          | Typed argument sequence                                                                          | Working directory   |
| ---------------------- | ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------ | ------------------- |
| DuckStation normal x64 | active program root + `duckstation-qt-x64-ReleaseLTCG.exe`   | `-batch`, `-fullscreen`, `--`, resolved primary ROM/playlist                                     | active program root |
| DuckStation ARM64      | active program root + `duckstation-qt-ARM64-ReleaseLTCG.exe` | same                                                                                             | active program root |
| PCSX2 x64              | active program root + `pcsx2-qt.exe`                         | `-datapath`, persistent data root, `-batch`, `-fullscreen`, `-nogui`, `--`, resolved primary ROM | active program root |

This preserves SRM's game-facing flags while adopting upstream's end-of-options protection and data
override. WSGM shortcuts retain ManagedId/InstallationId, not these versioned executable paths. A
controller-friendly “Configure emulator” action can use the official big-picture/setup entry
separately from ROM launching.

### Gaps

- Probe exit/output behavior remains a runtime check. DuckStation source establishes why exit 0 is
  an unsafe generic assumption, not that the shipped binary probe has been run. Controller binding,
  Steam running/Stop lifetime, Unicode/space/dash filenames, descriptors/companions and multi-disc
  playlists need actual imported-shortcut acceptance later.

## How do config, saves, dependencies and update ownership stay safe?

### Takeaway

PCSX2 can use a WSGM-owned persistent data root through its documented datapath option.
DuckStation's smallest default is native Documents/LocalAppData data across version activation,
explicitly recorded as shared/unowned where pre-existing. Reproducing portable Scoop/EmuDeck data
behavior must be an explicit fully handled alternative.

### Cited Findings

- DuckStation executable directory becomes DataRoot when either `portable.txt` OR `settings.ini` is
  there. Otherwise current Windows code first reuses existing known Documents\DuckStation, then
  selects actual `FOLDERID_LocalAppData\DuckStation`. A nearby comment says AppData, but executed
  code and current README use LocalAppData; follow the code. `settings.ini` is under DataRoot.
  [core.cpp:141](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/core/core.cpp#L141),
  [settings path:259](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/core/core.cpp#L259).
- Games manifests identify DuckStation persistent BIOS/cache/cheats/covers/dumps/per-game
  settings/input profiles/memory cards/save states/screenshots/shaders/textures/settings.ini. PCSX2
  stable/dev preserve BIOS, configs, memory cards, states, profiles, cheats/textures and other data,
  with different lists between channels. Their scripts migrate Documents data and delete the old
  folder; WSGM must not execute that behavior.
  [DuckStation manifest](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/duckstation.json),
  [PCSX2 manifests](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/pcsx2-dev.json).
- EmuDeck's Windows setup creates portable markers and save links from emulator-relative
  memory-card/state directories to Emulation saves. Its config templates also have folder/BIOS
  settings and controller mappings. This is useful path evidence, not proof that those helpers
  preserve data through WSGM transactions.
  [DuckStation setup:8](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckDuckStation.ps1#L8),
  [PCSX2 setup:12](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckPCSX2QT.ps1#L12),
  [DuckStation folders:202](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/configs/DuckStation/settings.ini#L202),
  [PCSX2 folders:19](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/configs/PCSX2/inis/PCSX2.ini#L19).
- Both require appropriate Microsoft Visual C++ runtime; PCSX2 specifically requires latest x64
  runtime. EmuDeck's inspected script asks Winget for x86, so do not copy that dependency choice.
  PCSX2 video capture additionally needs version-compatible FFmpeg libraries, but these are optional
  for normal emulation. Both require locally supplied console BIOS.
  [PCSX2 runtime/capture](https://pcsx2.net/docs/setup/running/),
  [DuckStation upstream requirements](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/README.md#L75),
  [PCSX2 BIOS instructions](https://pcsx2.net/docs/setup/bios/).
- Normal DuckStation x64 requires SSE4.1; upstream publishes a distinct SSE2 build and its hardware
  checks distinguish those. PCSX2 published requirements also name x86-64 SSE4.1.
  [DuckStation checks:577](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/core/core.cpp#L577),
  [PCSX2 requirements](https://pcsx2.net/docs/setup/requirements/).

### Inferences

- Managed PCSX2: use persistent `Data/<InstallationId>` through `-datapath`, with no portable marker
  or `-portable`. Keep full user data, not only a possibly stale manifest persistence allowlist.
  Separate cache/log cleanup from save/card/config ownership. Explicit migration copies accepted old
  config/data without deleting the source.
- Managed DuckStation default: install only program files into immutable version roots, ensure
  archives do not introduce a portable marker/settings.ini accidentally, and record the discovered
  native data path. Updating binaries leaves that data alone. Existing data remains shared/external
  and must never be deleted on managed removal. If users want two isolated DuckStation
  installations, current verified CLI cannot supply an independent datadir; portable mode needs its
  own reviewed design and acceptance instead of inventing `-userdir`.
- Disable emulator self-update checks only through reviewed settings keys for WSGM-owned configs, or
  explicit user consent for shared configs. Do not overwrite controller/graphics/game settings with
  EmuDeck templates. Neither source's arbitrary migration/install/uninstall scripts nor its shell
  wrappers run in WSGM.
- Downloads install into `<managed-root>/<InstallationId>/versions/<release-identity>`, with atomic
  active/previous metadata switching and preserved persistent paths. Serialize activation with
  launch admission; stage while running, defer activation/removal until no managed image is running.
  Retain previous binaries and config snapshots for rollback; never restore old memory-card/save
  files over newer gameplay saves.
- Definition validation checks expected exe/resources, architecture/CPU/runtime readiness, supplied
  archive hash, and approved bounded version/config probes. Removal warns about dependent ROM
  systems and preserves import IDs, ROMs and all data by default. No BIOS download is part of either
  definition.

### Gaps

- This is source/package metadata research, not startup, import, gameplay or save-survival proof.
  Later acceptance must install both real packages, launch existing stable shortcuts, update
  channels/versions, prove failed-download and failed-activation rollback, defer running-process
  updates, and verify settings/cards/save states/controller profiles survive. Native shared
  DuckStation data needs an explicit ownership display and configuration-migration rollback policy
  before delivery.
