# Ship six emulators with complete cores

**The first release must include RetroArch with all published Windows x64 cores, DuckStation, RPCS3,
PCSX2, Eden and Dolphin.** These are release requirements alongside the full ROM importer and
availability integration. RetroArch uses Libretro Buildbot; the others use curated Scoop metadata
cross-checked against official sources. Steam ROM Manager presets supply the starting launch
profiles, verified against each emulator's official parser before adaptation. EmuDeck Windows
supplies practical configuration/save-path evidence. This supplement specifies that lineup without
reducing the combined #47/#200/#203 scope.

Research snapshot: **6 October 2026**, WSGM `57a3dafdc7b4871eeb073d15ceb92dc5356abd6f`. Evidence is
source and release metadata; no emulator package, executable, ROM, firmware or key was downloaded,
and no build, test, install or launch occurred.

## Six definitions share one managed launcher

Commands below show verified SRM flags with readable resolved-path tokens. They are argument
sequences for the managed launcher, not versioned paths stored in Steam shortcuts.

| Emulator                  | Source and channels                                                                                                                                                                                                                                                                        | Verified Windows package                                     | SRM launch template                                                                                                                                                                 | Persistent-data direction                                                                                                                                                                                                      |
| ------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **RetroArch + all cores** | [Libretro Buildbot](https://buildbot.libretro.com/stable/1.22.2/windows/x86_64/), stable **1.22.2** / nightly; separate current core catalogue                                                                                                                                             | x64 frontend/core `.7z`; individual core DLL `.zip`          | `-L <CoreDLL> <ROM>` ([SRM N64 example](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%2064.json))           | Persistent config, saves, states, options/remaps and supplied system files; resolve active core/info/assets paths.                                                                                                             |
| **DuckStation**           | [Scoop Games](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/duckstation.json) + official GitHub; latest / preview                                                                                                                           | x64/ARM64 standalone Qt `.zip`; **20261006-g697599c**        | `-batch -fullscreen <ROM>` ([SRM](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation.json))            | Native [Documents/LocalAppData data](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/core/core.cpp#L141) retained across program updates; explicit shared/external ownership.         |
| **RPCS3**                 | [Scoop](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/rpcs3.json) + architecture-specific official binary GitHub repositories; rolling                                                                                                      | x64/ARM64 `.7z`; **0.0.43-20234**                            | `--no-gui <BootTarget>` ([SRM](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation%203.json))           | Child-only [RPCS3_CONFIG_DIR](https://github.com/RPCS3/rpcs3/blob/532b6520b4d04e12632c249f34a6fc41f23b1fa6/Utilities/File.cpp#L2518) points to persistent config/VFS, saves and supplied prerequisites.                        |
| **PCSX2**                 | [Scoop stable](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/pcsx2.json) / [pcsx2-dev](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/pcsx2-dev.json), official GitHub; **2.8.2 / 2.9.103**   | x64 Qt `.7z`; exclude symbols/installer                      | `<ROM> -batch -fullscreen -nogui` ([SRM](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation%202.json)) | Add verified `-datapath <DataRoot>`; keep cards, states, profiles/config outside versions.                                                                                                                                     |
| **Eden**                  | [Official Forgejo](https://eden-emu.dev/downloads/) + [Scoop stable](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/eden.json); **0.2.1** / nightly                                                                                          | x64/ARM64 `.zip`; select CPU-compatible variant              | `--fullscreen <ROM>` ([SRM](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%20Switch.json))                   | Preserve portable `user` with config/NAND/saves and user-supplied keys/firmware.                                                                                                                                               |
| **Dolphin**               | [Official release feed](https://dolphin-emu.org/update/latest/beta) **2609** / [development](https://dolphin-emu.org/update/latest/dev) **2609-61**; [Scoop](https://github.com/ScoopInstaller/Games/blob/c9f4e2f74c75285b111beb0839a7eb729f16fada/bucket/dolphin.json) currently **2606** | x64/ARM64 `.7z`; Scoop roots `Dolphin-x64` / `Dolphin-ARM64` | `-b -e <ROM>` ([SRM](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Nintendo%20GameCube.json))                        | Add verified `-u <DataRoot>` ([parser](https://github.com/dolphin-emu/dolphin/blob/f87e849a0afb3092aa0e30d363e730a106c65fe6/Source/Core/UICommon/CommandLineParse.cpp#L83)); preserve Config/GameSettings/GC/Wii/cards/states. |

PCSX2 and DuckStation support `--` before the ROM; place flags before it. **DuckStation has no
verified `-userdir` override**, and its current `-version` path returns failure after printing, so
validation needs definition-specific output/exit semantics. Latest/preview currently share a display
version but differ in hashes; update/skip identity includes channel and artifact revision.
([PCSX2 CLI](https://pcsx2.net/docs/advanced/cli/),
[DuckStation parser](https://github.com/stenzek/duckstation/blob/697599c47a646a6cfcc4d246018adf4904d55001/src/duckstation-qt/qthost.cpp#L3366))

RPCS3 profiles distinguish extracted-disc `PS3_GAME/USRDIR/EBOOT.BIN` and installed-title
`USRDIR/EBOOT.BIN`. **Raw PKG installation is a separate operation**, not an already installed game
launch. Eden's canonical Forgejo source is now verified, resolving the earlier discovery gap; its
nightly release uses body-linked downloads without located payload hashes. EmuDeck's Eden downloader
references stale upstream locations.
([RPCS3 presets](https://github.com/SteamGridDB/steam-rom-manager/blob/bd66e5f4ef1eb0b4855bbd216063f547f1468368/files/presets/Sony%20PlayStation%203.json),
[Eden nightly API](https://git.eden-emu.dev/api/v1/repos/eden-ci/nightly/releases/latest),
[EmuDeck Eden](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckEden.ps1))

## All cores means the complete published catalogue

Buildbot's x64 plain index lists **242 package filenames**, not 242 proven usable cores. **RetroArch
installation defaults to installing every published core package for the selected supported Windows
architecture; x64 is the verified v1 catalogue.** Extended metadata has malformed/missing rows,
including Holani aliases; several packages lack matching `.info`. Missing `.info` produces a visible
metadata state, never silent omission of the core package. Keep every entry accounted for and expose
missing requirements explicitly. A core bundle is only a download optimization after checking its
inventory and filling omissions. Install supporting core info/assets/databases; maintain system
mappings, per-system defaults and per-title CoreId overrides. EmuDeck's selected core list or an
updater button cannot satisfy this requirement. Native Windows ARM64 RetroArch was not found.
([Core indexes](https://buildbot.libretro.com/nightly/windows/x86_64/latest/),
[core-info tree](https://api.github.com/repos/libretro/libretro-core-info/git/trees/5a74858ab2f7a50cebb5a6330895bc38899531c0?recursive=1),
[EmuDeck Windows](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckRetroArch.ps1))

Add small Buildbot and Dolphin-feed adapters beside #203's GitHub/Forgejo/Gitea/Scoop providers. No
additional installer framework is needed. Verify supplied matching hashes; **Buildbot CRC32 and
Dolphin Git revisions are not publisher-authenticated payload SHA-256**. Never apply Scoop 2606's
hash to Dolphin 2609. Record locally computed digests separately.
([Buildbot parser](https://github.com/libretro/RetroArch/blob/a7999b032c567a39a5ce48674b79dd06677a406a/core_updater_list.c),
[Dolphin feed](https://dolphin-emu.org/update/latest/beta))

## Stable identity connects installation and media availability

Retain `ManagedId + RomPathRef + EmulatorInstallationId + optional CoreId`; resolve current mounts,
program/core versions and typed arguments immediately before launch. Preserve AppId, artwork and
collections through changed drive letters, missing media, emulator/core updates and
removal/reinstall. Keep ROM-media, content, emulator/core and prerequisite failures distinct.
Existing in-place shortcut updates already protect Steam identity.
([Issue 200](https://github.com/KillerPixelCrew/WSGM/issues/200),
[SteamShortcutWriter](https://github.com/KillerPixelCrew/WSGM/blob/57a3dafdc7b4871eeb073d15ceb92dc5356abd6f/src/WSGM/Core/Library/SteamShortcutWriter.cs#L68))

Use one staged installation/rollback path, preserve user data, and serialize launch admission with
activation. Stage while running; defer switching/removal. Consume Scoop metadata without its
scripts, shims or destructive migrations. EmuDeck Windows validates useful config/save conventions,
but its Git-pulling wrappers, executable renames and settings resets should not be replayed. Use
official matching runtime requirements; firmware/keys remain separately supplied prerequisites.
([Issue 203](https://github.com/KillerPixelCrew/WSGM/issues/203),
[EmuDeck Windows PCSX2](https://github.com/EmuDeck/emudeck-we/blob/9d459968b2f9c135bb88853548b54109eb763898/functions/EmuScripts/emuDeckPCSX2QT.ps1))

## Acceptance covers the entire first release

| Proof group           | Required result                                                                                                                                                                                                                                                   |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Complete delivery     | All six end-to-end manager/importer definition, install and launch flows; installed full x64 core catalogue inventory, aliases/gaps accounted for without silently skipped packages; compatible CPU/architecture/channel selection.                               |
| Actual launching      | Controller/touch configure/install/import; real content per emulator, PS3 installed/extracted profiles, representative core/system combinations; Steam overlay/input/running/Stop lifetime. Inventory proof remains separate from every-core compatibility proof. |
| Updates and ownership | Corrupt/hash-mismatched download, rollback/interruption, running-process deferral, restart; saves/config/cards/profiles survive; external installs remain unowned; dependent removal stays recoverable.                                                           |
| Availability          | Absent/reinserted/wrong media, changed letter/mount, missing core/emulator/prerequisite; retain AppId/artwork and refresh without Steam restart; protocol/helper launch refuses missing content.                                                                  |

Source discovery is established, including real Eden Forgejo hosting; package layout, runtime
integration and hardware acceptance remain untested. Gitea's separate real-package acceptance gap
remains. Implementation increments can be internal, but the first release cannot quietly omit an
emulator or replace all-core installation with a curated subset.
