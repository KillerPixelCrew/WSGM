# RetroArch first release: complete Windows core catalogue from Libretro Buildbot

## Which upstream packages and architectures cover the first release?

### Takeaway

Use Libretro Buildbot directly, with stable RetroArch and the complete published Windows x64 core
catalogue installed. The catalogue is larger than a curated default list; installing only an updater
or EmuDeck's selected cores would not satisfy the human's requirement.

### Cited Findings

- Verified 6 October 2026: the official downloads page identifies stable **1.22.2**, with Windows
  32-bit and 64-bit desktop downloads. It explains that Steam RetroArch has restricted core
  distribution and lacks the daily core updater. Use the desktop Buildbot package. —
  [Official downloads](https://www.retroarch.com/?page=platforms)
- Stable x64 directory publishes `RetroArch.7z`, `RetroArch-Win64-setup.exe` and separate
  `RetroArch_cores.7z`; the listing dates these packages 20 November 2025. —
  [Stable x64 packages](https://buildbot.libretro.com/stable/1.22.2/windows/x86_64/)
- Nightly x64 publishes date-named frontend archives, a moving `RetroArch.7z`, `RetroArch_cores.7z`
  and `RetroArch_update.7z`. The inspected moving packages were dated 5 October 2026. —
  [Nightly x64 packages](https://buildbot.libretro.com/nightly/windows/x86_64/)
- The separate `latest/` core directory publishes individual DLL ZIP packages. Direct metadata
  retrieval found **242 filenames in `.index`**. `.index-extended` had 240 well-formed DLL ZIP rows
  and two malformed `.zip` rows; the extended list lacked CRC/date records for `holani.dll.zip` and
  `holani_retro.dll.zip`, which remain present in the plain index. These are catalogue artifacts,
  not a claim of 242 distinct working emulator cores. —
  [Core directory](https://buildbot.libretro.com/nightly/windows/x86_64/latest/);
  [Plain index](https://buildbot.libretro.com/nightly/windows/x86_64/latest/.index);
  [Extended index](https://buildbot.libretro.com/nightly/windows/x86_64/latest/.index-extended)
- Official stable and nightly Windows directories expose `x86` and `x86_64`; no native Windows ARM64
  Buildbot package was found. —
  [Stable architectures](https://buildbot.libretro.com/stable/1.22.2/windows/);
  [Nightly architectures](https://buildbot.libretro.com/nightly/windows/)

### Inferences

- Add one small **Libretro Buildbot** provider alongside #203's GitHub/Forgejo/Gitea/Scoop adapters:
  allowlisted host/root, numeric stable-directory discovery, date-named nightly frontend discovery,
  plain and extended core-index parsing, and fixed frontend-asset endpoints. This is a provider
  extension, not a new installer framework.
- Use the separate core bundle as a bulk-download optimization only after verifying its actual
  inventory against the selected catalogue; fill every missing entry with its individual ZIP.
  Without archive inspection, the stable bundle cannot be assumed to cover today's entire nightly
  catalogue.
- Keep frontend channel and core catalogue revision explicit. A stable frontend with current cores
  is a reasonable default proposal, but newly published cores may require a newer frontend; validate
  that contract rather than asserting universal compatibility. Offer nightly frontend deliberately
  when needed.
- Preserve every published target-architecture entry and report aliases, missing metadata, failed
  packages and unavailable cores individually. Do not silently replace “all cores” with the
  known-good default selection.

### Gaps

- No executable/archive was downloaded or inspected. Bundle contents, extraction layout and all-core
  DLL/runtime compatibility remain unverified.
- Native Windows ARM64 availability is not established. Running x64 binaries under Windows emulation
  would be a separate compatibility route, not native ARM64 coverage.
- The upstream index contains legacy aliases and metadata holes. A maintained mapping is needed
  before claiming every published artifact represents a distinct selectable core.

## How should complete core installation, updates, metadata and data ownership work?

### Takeaway

Treat cores and accompanying `.info` metadata as managed components, with persistent configuration,
saves and user-supplied system files outside replaceable versions. Upstream CRC32 helps detect core
changes, but does not establish publisher-authenticated cryptographic integrity.

### Cited Findings

- Buildbot exposes frontend `info.zip`, `assets.zip`, `autoconfig.zip`, database RDB/cursor
  archives, cheats, overlays and shader archives. These endpoints are moving packages; `.info` and
  databases are required for useful automatic identification rather than just copying DLLs. —
  [Frontend asset catalogue](https://buildbot.libretro.com/assets/frontend/);
  [Playlist scan requirements](https://docs.libretro.com/guides/roms-playlists-thumbnails/)
- RetroArch fetches `.index-extended`; its parser expects `[date] [crc] [filename]`. The updater
  compares that CRC with a CRC32 computed from the local core file. —
  [Pinned parser](https://github.com/libretro/RetroArch/blob/a7999b032c567a39a5ce48674b79dd06677a406a/core_updater_list.c);
  [Pinned updater](https://github.com/libretro/RetroArch/blob/a7999b032c567a39a5ce48674b79dd06677a406a/tasks/task_core_updater.c)
- The measured extended-index snapshot had HTTP Last-Modified `Tue, 06 Oct 2026 13:37:36 GMT`;
  locally computed SHA-256 of its bytes was
  `0ec72b4c5b5c4e9933a31f878047e6e46118751d820572f86a709c1955819713`. This is a local catalogue
  receipt, **not** an upstream release signature or publisher-supplied SHA-256. —
  [Inspected extended index](https://buildbot.libretro.com/nightly/windows/x86_64/latest/.index-extended)
- `.info` supplies display/core names, extensions, `systemid`, `database`, firmware paths/optional
  flags, fullpath/no-content/subsystem and hardware-render information. Example mGBA supports
  GB/GBC/GBA and optional BIOS; `pcsx2_libretro` is displayed as LRPS2, requires a BIOS directory
  and explicitly says directory presence cannot prove BIOS correctness. —
  [Pinned mGBA info](https://github.com/libretro/libretro-core-info/blob/5a74858ab2f7a50cebb5a6330895bc38899531c0/mgba_libretro.info);
  [Pinned LRPS2 info](https://github.com/libretro/libretro-core-info/blob/5a74858ab2f7a50cebb5a6330895bc38899531c0/pcsx2_libretro.info)
- Comparing the well-formed extended catalogue with pinned core-info tree found missing `.info`
  entries for `boom3_xp_libretro`, `playdiaemu_libretro`, `rust_dos_libretro`, `fbalpha_libretro`
  and `rpcs3_libretro`; legacy Holani aliases lack extended-index rows as above. —
  [Pinned core-info tree](https://api.github.com/repos/libretro/libretro-core-info/git/trees/5a74858ab2f7a50cebb5a6330895bc38899531c0?recursive=1);
  [Extended catalogue](https://buildbot.libretro.com/nightly/windows/x86_64/latest/.index-extended)
- Config provides distinct paths for core DLLs, core info, system files, assets, autoconfig,
  playlists, savefiles, states, screenshots and core options. RetroArch may rewrite its config on
  exit. —
  [Pinned skeleton configuration](https://github.com/libretro/RetroArch/blob/a7999b032c567a39a5ce48674b79dd06677a406a/retroarch.cfg)

### Inferences

- Minimum core receipt: `CoreId` (stable filename stem, preserve case), architecture/platform,
  catalogue URL/revision/index digest, entry date, upstream CRC32 if present, ZIP URL, local ZIP
  SHA-256, local DLL SHA-256, metadata revision, installation status and missing-requirements
  reason. Mark hash provenance explicitly (`upstream-crc32`, `local-sha256`, or separately verified
  upstream signature/hash).
- Snapshot metadata first; stage/extract the complete catalogue safely, compare extracted DLL CRC32
  where upstream supplies one, compute local SHA-256 receipts, and re-read catalogue before
  activation to catch moving-target races. Never label a local SHA as upstream verification. Retain
  a cached receipt/archive for rollback; no immutable archived individual-core endpoint was
  established.
- One install transaction should account for all entries and identify failures while preserving the
  active version. Updates should keep the previous frontend/core set and activate only when
  RetroArch is stopped. Defer launch/activation cross-process under the manager's existing
  transaction design.
- Explicit stable user-data paths prevent updates/rollback/removal from deleting saves, states,
  options, remaps or supplied firmware. Configure core/info/assets paths to the resolved active
  version at launch; keep mutable data persistent. Route `system_directory` to user-supplied BIOS
  plus reviewed freely distributable core assets. The official asset server must not be treated as
  blanket redistribution permission for arbitrary firmware.
- System mapping should use reviewed core-info `systemid`/database relationships, with per-system
  default core and per-title override. Extensions alone cannot resolve shared `.bin`, `.iso`,
  `.zip`, or multicomponent media. Missing metadata needs an explicit maintained mapping/manual
  choice.

### Gaps

- No signed manifest or strong publisher checksum was observed in the inspected Buildbot directory
  listings/indexes. This does not prove none exists elsewhere.
- Metadata does not fully declare every core's runtime/assets/firmware requirements. Installing all
  cores does not make all systems playable without those requirements and valid content.

## Which Steam ROM Manager and EmuDeck conventions should WSGM adopt?

### Takeaway

Use Steam ROM Manager presets as launch/profile input, cross-check flags against RetroArch, and use
EmuDeck's Windows source as practical path/configuration evidence. WSGM should resolve its own
active executable/core/content paths, keeping stable identities independent of package versions and
drive letters.

### Cited Findings

- Pinned SRM Windows branches use `${retroarchpath}` for the executable,
  `cores\\<core>_libretro.dll` via the OS-aware argument template, and quoted `${filePath}` for
  content. Representative mappings: Nintendo 64 → `mupen64plus_next_libretro`; SNES →
  `mednafen_snes_libretro` or `bsnes2014_accuracy_libretro`; PlayStation →
  `mednafen_psx_hw_libretro` or `pcsx_rearmed_libretro`. The presets' `startInDirectory` is blank. —
  [N64 presets](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%2064.json);
  [SNES presets](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%20SNES.json);
  [PS1 presets](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation.json)
- Exact SRM N64 template is
  `-L ${os:win|cores|${os:mac|${racores}|${os:linux|${racores}}}}${/}mupen64plus_next_libretro.${os:win|dll|${os:mac|dylib|${os:linux|so}}} "${filePath}"`.
  Windows resolves to `-L cores\\mupen64plus_next_libretro.dll "<ROM>"`. —
  [Pinned N64 preset](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%2064.json)
- Official CLI source documents `-L/--libretro`, `-c/--config`, `--appendconfig` and
  `-f/--fullscreen`. Current master marks `--save`/`--savestate` deprecated in favor of
  append-config directory settings; this master behavior must not be assumed identical to older
  stable binaries without checking them. —
  [Pinned RetroArch CLI source](https://github.com/libretro/RetroArch/blob/a7999b032c567a39a5ce48674b79dd06677a406a/retroarch.c)
- EmuDeck's Windows script downloads RetroArch from `$url_ra`, fetches a **hardcoded selected core
  list** individually, points system files to `$biosPath`, and separates saves/states through links.
  Its RetroArch update/storage/migration functions contain `NYI`; it is a useful configuration
  reference, not a complete manager implementation. —
  [Pinned EmuDeck Windows RetroArch module](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckRetroArch.ps1)
- EmuDeck's Windows launcher resolves `%APPDATA%/emudeck/Emulators/RetroArch/retroarch.exe`, quotes
  received arguments and calls its shared launcher helper. It also performs a Git pull and invokes
  cloud helper initialization; those side effects are EmuDeck ownership and should not become WSGM
  launcher behavior. —
  [Pinned Windows launcher](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/tools/launchers/retroarch.ps1)

### Inferences

- WSGM's argument-array equivalent should be
  `-L <resolved absolute active-core DLL> -c <persistent user config> --appendconfig <launch-specific path config> -f <resolved current ROM or M3U>`,
  with a stable manager-selected working directory. Absolute core paths avoid reliance on SRM's
  blank working-directory convention and keep cores outside changing ROM storage.
- WSGM owns `InstallationId`, `CoreId`, source/volume-relative content paths and persistent config;
  importer profile expansion supplies typed tokens. The managed launcher resolves current paths
  immediately before launch. Do not paste SRM's template language into stored executable commands or
  infer core identity later from arbitrary command text.
- Preserve a human-selected core even when a new default appears. Distinguish standalone
  PCSX2/Dolphin/RPCS3 installations from similarly named Libretro cores; they have different
  identities, capabilities and update ownership.
- EmuDeck's Linux Flatpak paths and wrappers are Linux-specific. Prefer its Windows module and
  explicit paths; adapt reviewed config values rather than copying cloud/auto-update scripts or all
  SteamOS settings.

### Gaps

- Exact SRM/EmuDeck argument behavior and configuration persistence with current Windows stable
  RetroArch have not been executed. No real ROM, core or Steam launch was tested.
- This is researched scope and implementation guidance only. No source change, executable download,
  build, installation, application launch or live Steam mutation occurred.
