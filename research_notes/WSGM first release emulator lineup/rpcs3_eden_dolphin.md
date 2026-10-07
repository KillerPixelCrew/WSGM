# RPCS3, Eden and Dolphin: first-release package and launch definitions

Research-only snapshot, 2026-10-06 Europe/Berlin. Workspace baseline
`57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`. Metadata/source files were read, but no emulator
archive, executable, ROM, firmware or key was downloaded or executed. No build, tests, deployment,
live Steam or installed-app actions. Current canonical Scoop Games commit:
`c9f4e2f74c75285b111beb0839a7eb729f16fada`; current SRM commit:
`bd66e5f4ef1eb0b4855bbd216063f547f1468368`; EmuDeck Windows commit:
`9d459968b2f9c135bb88853548b54109eb763898`.

## RPCS3: source, data preservation and actual launch template

### Takeaway

Use RPCS3's official Windows binary release repositories, with architecture-aware `.7z` selection
and upstream SHA-256, or the exact pinned Scoop package as a metadata source. RPCS3 is rolling
release; it does not have a genuine stable channel. Its source supports separate persistent
config/data through `RPCS3_CONFIG_DIR`, avoiding user data living inside disposable version folders.

### Cited Findings

- Canonical Scoop currently specifies `0.0.43-20234`, x64 asset
  `rpcs3-v0.0.43-20234-532b6520_win64_msvc.7z`, hash
  `96e8504988090facbb4c222093acbdaed4d203cb77e0a3451a0b77dad5044f0d`; ARM64 asset in
  `RPCS3/rpcs3-binaries-win-arm64` is `rpcs3-v0.0.43-20234-532b6520_win64_aarch64_clang.7z`, hash
  `0248c22ce8a591d0acb8a84e06934818fe23fc9e81c1c08e6cab934a6c345a86`. Expected executable is
  `rpcs3.exe`, no `extract_dir`. —
  [Pinned Scoop RPCS3](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/rpcs3.json)
- Live official GitHub x64 release metadata agrees with Scoop and provides both the archive asset
  digest `sha256:96e850...` and a `.7z.sha256` sidecar. Binary release tag is
  `build-532b6520b4d04e12632c249f34a6fc41f23b1fa6`, while human version is `0.0.43-20234`; compare
  the package version/build identity rather than assuming SemVer tag. —
  [Official x64 release API](https://api.github.com/repos/RPCS3/rpcs3-binaries-win/releases/latest),
  [Official binary repository](https://github.com/RPCS3/rpcs3-binaries-win)
- RPCS3's main source release page explicitly calls its tagged versions landmarks rather than stable
  builds and directs users to official current downloads. —
  [RPCS3 source releases](https://github.com/RPCS3/rpcs3/releases)
- RPCS3's own updater uses
  `https://update.rpcs3.net/?api=v3&c=<commit>&os_type=<os>&os_arch=<arch>&os_version=<version>`,
  reads OS-specific `checksum`, and verifies downloaded bytes with SHA-256. A request using the
  current commit returned only current/latest build summary; an all-zero unknown commit returned
  `return_code:-1`. Thus official download/update metadata is not interchangeable with the simpler
  GitHub binary release API. —
  [Pinned updater source:116,198,246,578](https://github.com/RPCS3/rpcs3/blob/532b6520b4d04e12632c249f34a6fc41f23b1fa6/rpcs3/rpcs3qt/update_manager.cpp)
- SRM current presets use `--no-gui "${filePath}"` for ISO, extracted ISO and installed PKG.
  Extracted games match `${title}/PS3_GAME/USRDIR/@(eboot.bin|EBOOT.BIN)`; installed PKG matches
  `${title}/USRDIR/@(eboot.bin|EBOOT.BIN)`. These preserve the actual EBOOT path instead of treating
  a game folder's every file as a separate title. —
  [Pinned SRM PS3 presets](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation%203.json)
- Official source registers `--no-gui`, `--fullscreen` usable with no-gui, `--config <path>` and
  `--input-config <name>`; positional boot target is required in no-gui mode. Firmware installation
  is a separate `--installfw` mode and cannot be mixed blindly into no-gui game launch. —
  [Pinned RPCS3 command parser:811-839,1361-1371](https://github.com/RPCS3/rpcs3/blob/532b6520b4d04e12632c249f34a6fc41f23b1fa6/rpcs3/rpcs3.cpp#L811-L839)
- Windows `fs::get_config_dir` first checks `RPCS3_CONFIG_DIR`; otherwise resolves the executable
  location. Scoop persists virtual flash/HDD/USB, `config.yml`, `games.yml`, `config`, `GuiConfigs`,
  firmware, patches, captures/cache and icons. —
  [Pinned Windows path source:2518-2570](https://github.com/RPCS3/rpcs3/blob/532b6520b4d04e12632c249f34a6fc41f23b1fa6/Utilities/File.cpp#L2518-L2570),
  [Pinned Scoop persistence](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/rpcs3.json)
- EmuDeck's actual Windows script downloads from `RPCS3/rpcs3-binaries-win`, uses
  `Emulators/RPCS3/rpcs3.exe`, copies config/VFS files, redirects VFS to Emulation storage, and
  links `dev_hdd0/home/00000001/savedata` and trophy data into its saves directory. Its launcher
  forwards args but also pulls its own backend Git repository. —
  [Pinned Windows install/config](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckRPCS3.ps1#L1-L29),
  [Windows save links:81-89](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckRPCS3.ps1#L81-L89),
  [Windows launcher](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/tools/launchers/rpcs3.ps1)

### Inferences

- Definition: `id=rpcs3`, system `ps3`, channel `rolling`, provider `GitHubReleases`, repository
  selected by architecture; deterministic filename matcher `*_win64_msvc.7z` versus
  `*_win64_aarch64_clang.7z`. Installation validates `rpcs3.exe`, preserves all user data, and
  records exact asset URL/version/digest.
- Start with SRM's exact `--no-gui {ContentPath}` typed template. Fullscreen is a reviewed optional
  system/title flag, not a reason to rewrite every existing shortcut. Save original working
  directory and set it to active emulator program directory for new installs.
- Set `RPCS3_CONFIG_DIR` only in the managed child's environment to a persistent installation data
  root; preserve emulator-generated config/VFS and patches across versions. Mark user saves and
  firmware as persistent, shader/cache as separately clearable. Do not copy EmuDeck's Git pulls,
  scripts, forced config reset or save-link migrations.
- PS3 firmware is a required separately supplied/officially sourced prerequisite, not Scoop
  dependency execution. Games needing their own licensing data require user-provided legitimate
  files. WSGM needs to distinguish emulator installed from firmware/game launch ready.

### Gaps

- API/source and filename/digest verification do not prove archive layout or actual x64/ARM64 game
  launch. RPCS3_CONFIG_DIR behavior, overlays/controller/input and save preservation need later
  attended validation.
- Official updater API's older-commit payload was not obtained; using the official binary GitHub
  source avoids making that unknown a v1 prerequisite.

## Eden: the exact official Forgejo project, channels and Windows compatibility

### Takeaway

The requested Eden is officially at `eden-emu.dev` and `git.eden-emu.dev/eden-emu/eden`, with real
Forgejo-style release APIs. Stable v0.2.1 is available through canonical Scoop metadata with
SHA-256; nightly discovery requires release-body links because its live API has no asset
attachments. EmuDeck Windows's Eden download function is stale and must not supply the source
definition.

### Cited Findings

- Official downloads link Stable to `git.eden-emu.dev/eden-emu/eden/releases` and Nightly to
  `git.eden-emu.dev/eden-ci/nightly/releases`; official footer identifies Forgejo and the canonical
  source repository. This resolves a real Forgejo source-discovery example, not an install/runtime
  pass or a Gitea example. — [Official Eden downloads](https://eden-emu.dev/downloads/),
  [Official Eden repository](https://git.eden-emu.dev/eden-emu/eden)
- Live `https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases/latest` returns stable
  `v0.2.1`, non-prerelease, and Windows ZIP links on `stable.eden-emu.dev`. It exposes amd64 clang
  PGO/GCC/MSVC variants, ARM64 clang variants and optimized ROG Ally variants; returned attachment
  sizes are zero and digests absent. —
  [Official stable release API](https://git.eden-emu.dev/api/v1/repos/eden-emu/eden/releases/latest)
- Canonical Scoop stable is `0.2.1`; x64 `Eden-Windows-v0.2.1-amd64-clang-pgo.zip` SHA-256 is
  `6c1b53ce325170a026cc0f87098380027dc6170d94ba95f913aab7596fd097cb`; ARM64
  `Eden-Windows-v0.2.1-arm64-clang-pgo.zip` is
  `ed03e04606d06d8efa63f239775e19a3b5539227c042f133c3170234c9fd277a`. Expected exe is `eden.exe`,
  persisted portable directory `user`; no explicit extract_dir. —
  [Pinned Scoop Eden](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/eden.json)
- Live nightly API tag is `v1791243079.10bcd2d849`, published 2026-10-06, no attached assets. Body
  contains explicit ZIP links on `nightly.eden-emu.dev` for x64 MSVC, x86-64-v3 GCC/clang PGO, Zen4
  optimized and ARM64; it states optimized ROG Ally variants require Zen4 and are incompatible with
  Intel. —
  [Official nightly release API](https://git.eden-emu.dev/api/v1/repos/eden-ci/nightly/releases/latest)
- SRM currently uses `--fullscreen "${filePath}"` and `.nca/.nro/.nso/.nsp/.xci` variants. Official
  current source accepts `-f/--fullscreen`, `-g/--game`, positional filename and `-u/--user` as a
  user-profile selector, not a data-directory argument. —
  [Pinned SRM Switch preset](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%20Switch.json),
  [Official parser at current commit:27-36,55-63,95-110,141-145](https://git.eden-emu.dev/eden-emu/eden/src/commit/10bcd2d849843b146a79c61a21df615e1bdcfcde/src/core/launch_params.cpp)
- Official quickstart says Windows needs Microsoft C++ runtime, firmware/keys/game dumps from the
  user's console, and supports fully portable mode by creating `user` beside the extracted program.
  Current Windows path source prefers portable user over `%APPDATA%/eden`, with config, keys, NAND,
  SDMC and shader directories. —
  [Official quickstart](https://git.eden-emu.dev/eden-emu/eden/src/commit/10bcd2d849843b146a79c61a21df615e1bdcfcde/docs/user/QuickStart.md),
  [Official paths:106-175](https://git.eden-emu.dev/eden-emu/eden/src/commit/10bcd2d849843b146a79c61a21df615e1bdcfcde/src/common/fs/path_util.cpp)
- EmuDeck Windows Eden currently tries GitHub `eden-emu/eden-mainline`, a `.7z`/`eden-windows-msvc`
  layout and an old early-access `.org` API; these contradict official current source/CDN/ZIP names.
  Its save/config logic still demonstrates a portable `user` directory with NAND/config/keys and
  savedata links. Its Windows SRM wrapper uses `-f -g <file>`, which is consistent with official
  current argument aliases. —
  [Pinned Windows Eden function:1-70,149-193](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckEden.ps1),
  [Pinned Windows parser](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/configs/steam-rom-manager/userData/parsers/emudeck/nintendo_switch-eden.json)

### Inferences

- Definition: `id=eden`, system `switch`, official `ForgejoReleases` provider with explicit server
  `https://git.eden-emu.dev`; stable repository `eden-emu/eden`, nightly `eden-ci/nightly`. Exact
  host/repository stays in definition, not a GitHub-specific adapter. Scoop can supply stable
  package metadata/hash without invoking Scoop.
- Use x64 generic PGO only after checking its CPU baseline; do not pick a file named ROG Ally merely
  because WSGM runs on a handheld. Variant choice must include architecture and minimum ISA. MSVC is
  a useful generic/manual compatibility fallback. Never silently substitute another Switch emulator
  for requested Eden.
- Launch from managed active program directory with SRM's typed `--fullscreen {ContentPath}`.
  Preserve a stable persistent `user` directory across staged program updates; keep NAND, keys,
  firmware, controller mappings, config and saves out of disposable version folders. Copy/import
  external user data only through an explicit action, preserving originals. Scoop's destructive
  external AppData migration is not behavior to copy.
- Stable official API provides no verified payload digest; matching pinned Scoop hash is available
  for the exact stable asset. Nightly requires the source adapter to parse trusted release-body URLs
  and apply deterministic variant rules, with HTTPS/host restrictions and transparent lack of an
  upstream checksum if none is supplied. No hashes from stable/Scoop may be transplanted onto
  nightly assets.

### Gaps

- Nightly payload checksums/signatures were not located in inspected API/body; zero attachment size
  is not the actual download size. Direct official raw source requests returned 403; official
  contents API was used to read and verify the cited pinned source.
- Portable path behavior and ZIP root layout are source/manifest evidence; no archives were
  inspected and no installed/runtime integration was tested. Whether every listed SRM extension is
  directly launchable depends on actual title content and user prerequisites.

## Dolphin: official update feeds, Scoop lag and explicit persistent user directory

### Takeaway

Dolphin has a useful official JSON feed for release and development channels, including
architecture-specific download URLs. Current Scoop is older than the official feed;
source+channel+version+hash must remain one coherent package choice. Dolphin's own `--user` path
option gives the simplest durable data location across managed program updates.

### Cited Findings

- Canonical Scoop currently packages release `2606`: x64 `dolphin-2606-x64.7z`, hash
  `c6ea821c820cdc5d52d9f4d315fdca629d51a1b9f4f7e671948b905c085d7f59`, extract directory
  `Dolphin-x64`; ARM64 `dolphin-2606-ARM64.7z`, hash
  `ab5df5a8bc88e820714afbd7f78de6a36ed23a98c141b1e41ca12d7a08c5201e`, extract directory
  `Dolphin-ARM64`. Expected executables are `Dolphin.exe` and optional tooling `DolphinTool.exe`,
  portable marker `portable.txt`, persistent `User`. —
  [Pinned Scoop Dolphin](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/dolphin.json)
- Live official `/update/latest/beta` returns release `2609` from 2026-09-24 and
  `/update/latest/dev` returns `2609-61` from 2026-10-05. Both list Windows x64 and arm64 `.7z`
  assets. The `hash` fields are 40-character source Git revisions
  (`f84df02055ab9610feec48e65648cac5a3c098fa`, `f87e849a0afb3092aa0e30d363e730a106c65fe6`), not
  SHA-256 of the archives; artifacts contain URL/system but no payload checksum. —
  [Official release JSON](https://dolphin-emu.org/update/latest/beta),
  [Official development JSON](https://dolphin-emu.org/update/latest/dev)
- Official download page describes releases versus development builds and requires Microsoft Visual
  C++ 2022 runtime for Windows. The historical feed name `beta` currently supplies numbered
  releases, so UI should label its user-facing channel Release. —
  [Official downloads](https://dolphin-emu.org/download/)
- SRM standalone GameCube and Wii presets use `-b -e "${filePath}"`. Official parser registers
  `-b/--batch` (no main UI, requires exec/NAND target), `-e/--exec`, `-u/--user <path>`, and
  `-C/--config` override. —
  [Pinned SRM GameCube](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%20GameCube.json),
  [Pinned SRM Wii](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%20Wii.json),
  [Official command parser:83-116](https://github.com/dolphin-emu/dolphin/blob/f87e849a0afb3092aa0e30d363e730a106c65fe6/Source/Core/UICommon/CommandLineParse.cpp#L83-L116)
- Official Windows path precedence includes custom path, local `portable.txt`/registry settings,
  legacy Documents folder and `%APPDATA%/Dolphin Emulator`; portable uses program-local User. Under
  User, source derives GC/Wii, Config, GameSettings, StateSaves and Cache/shader locations. —
  [Official user-directory selection:304-399](https://github.com/dolphin-emu/dolphin/blob/f87e849a0afb3092aa0e30d363e730a106c65fe6/Source/Core/UICommon/UICommon.cpp#L304-L399),
  [Official derived paths:843-879](https://github.com/dolphin-emu/dolphin/blob/f87e849a0afb3092aa0e30d363e730a106c65fe6/Source/Core/Common/FileUtil.cpp#L843-L879)
- Actual EmuDeck Windows function creates `Dolphin-x64/portable.txt`, configures `User/Config`, and
  links User/GC, User/Wii and User/StateSaves to separate save locations. The Windows launcher calls
  actual Dolphin.exe through PowerShell and backend initialization; these are Windows-specific
  evidence, distinct from Linux Flatpak/AppImage wrappers. —
  [Pinned Windows Dolphin install/config/save links:4-75](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckDolphin.ps1#L4-L75),
  [Pinned Windows launcher](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/tools/launchers/dolphin.ps1)

### Inferences

- Definition: `id=dolphin`, systems `gamecube,wii`, provider either exact Scoop metadata package or
  a small Dolphin JSON feed adapter; channels `release` and `development`. Choose correct
  system/architecture URL from feed rather than fabricating paths or treating GitHub source release
  tags as Windows binaries.
- Starting from SRM, template is `-b -e {ContentPath}` with reviewed `-u {InstallationDataPath}`
  added for managed installs. That explicit persistent data path avoids linking a User directory
  into every version folder. Preserve Config/GameSettings/GC/Wii/memory cards/StateSaves and user
  texture/profile data; expose cache clearing separately. Do not overwrite all controls with
  EmuDeck's profile bundle.
- Release 2609 must never be checked against Scoop 2606's hash. A definition choosing Scoop uses
  exactly its specified URL/version/hash; one choosing official feed uses its URL/revision/channel
  plus only independently verified matching payload digest if supplied. Record a locally computed
  digest as observed integrity, never call the feed's Git revision a download checksum.
- GameCube BIOS/Wii menu/content require optional legitimately supplied data depending on desired
  behavior, distinct from core emulator binary installation. No BIOS/game/firmware retrieval was
  performed here.

### Gaps

- No archive payload checksum is returned by inspected Dolphin feeds. Availability of a separately
  verifiable official checksum/signature for latest packages remains unconfirmed; manifest hashes
  cover only the manifest's exact older package.
- Archive root layout for latest 2609/2609-61 is not physically inspected. No real
  title/gamepad/overlay/Steam lifetime, profile preservation or update rollback test occurred.

Common implementation direction: these are reviewed definition facts and narrowly required provider
differences, not instructions to execute remote scripts. Keep emulator InstallationId stable,
resolve active program and persistent data root at launch, render source-owned ROM path tokens after
correct-volume resolution, and retain ManagedId/AppId/artwork after emulator updates or removal.
Managed update refuses while emulator runs and removal preserves data by default. The real Eden
Forgejo source proves discovery feasibility; Gitea's separate real-source/runtime acceptance gap
remains.
