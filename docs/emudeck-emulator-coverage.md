# EmuDeck emulator coverage

Source snapshot: 2026-10-21. This is a catalogue/source comparison, not a claim that all downloads,
emulator gameplay, firmware installation, Steam shortcuts or ARM hardware were exercised.

- [EmuDeck Windows install definitions](https://github.com/EmuDeck/emudeck-we/tree/037460efc88c1ab70256ab0833f8f10e7f2904b2/functions/EmuScripts),
  tree `037460efc88c1ab70256ab0833f8f10e7f2904b2`.
- [EmuDeck Linux install definitions](https://github.com/dragoonDorise/EmuDeck/tree/dddb30229adbbd54d5ff7283cfd70e11c4322c26/functions/EmuScripts),
  tree `dddb30229adbbd54d5ff7283cfd70e11c4322c26`.
- WSGM started with RetroArch, DuckStation, RPCS3, PCSX2, Eden and Dolphin. The catalogue now has 28
  definitions, adding the 22 Windows-capable engines below. Install/update/repair/remove and both
  frontends consume the same catalogue and existing manager.

## Every standalone entry

Names in the first column correspond to EmuDeck `emuDeck*.ps1` / `emuDeck*.sh` definitions. Windows
packages are native x64 unless the architecture column says otherwise. x86 Model 2 runs under
Windows x64 compatibility; it is not mislabeled as a native x64 binary.

| EmuDeck entry           | WSGM disposition                                                       | Windows release source                                                                                                                                                                                                      | Portable data and local setup                                                                                                                                                                     |
| ----------------------- | ---------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| RetroArch               | Existing `retroarch`, all published Windows cores                      | [Libretro Buildbot](https://buildbot.libretro.com/)                                                                                                                                                                         | `retroarch.cfg`, existing managed core/system/save paths                                                                                                                                          |
| DuckStation             | Existing `duckstation`                                                 | Existing Scoop manifests / DuckStation upstream                                                                                                                                                                             | Existing portable marker, BIOS and memory-card policy                                                                                                                                             |
| RPCS3                   | Existing `rpcs3`                                                       | Existing RPCS3 Windows release repositories                                                                                                                                                                                 | Existing portable directory and PS3 firmware installer                                                                                                                                            |
| RPCS3Legacy             | Alias/legacy Linux package route, covered by `rpcs3`                   | Same engine; no invented separate Windows legacy distribution                                                                                                                                                               | Existing RPCS3 data contract                                                                                                                                                                      |
| PCSX2QT                 | Existing `pcsx2`                                                       | Existing PCSX2 stable/development manifests                                                                                                                                                                                 | Existing PS2 BIOS and portable data policy                                                                                                                                                        |
| Dolphin                 | Existing `dolphin`                                                     | Existing Dolphin release/development endpoints                                                                                                                                                                              | Existing User directory and console-system policy                                                                                                                                                 |
| Eden                    | Existing `eden`                                                        | Existing Eden stable/nightly definitions                                                                                                                                                                                    | Existing user/NAND/keys policy                                                                                                                                                                    |
| mGBA / MGBA             | Added `mgba`                                                           | [mgba-emu/mgba](https://github.com/mgba-emu/mgba/releases), `mGBA-*-win64.7z`                                                                                                                                               | `portable.ini`; managed saves/states/screenshots; optional dumped boot ROMs                                                                                                                       |
| melonDS / MelonDS       | Added `melonds`, x64 + ARM64                                           | [melonDS-emu/melonDS](https://github.com/melonDS-emu/melonDS/releases), Windows architecture ZIPs                                                                                                                           | Adjacent `melonDS.toml`, Instance0 save/state paths; built-in DS system data, optional local DS/DSi dumps                                                                                         |
| PPSSPP                  | Added `ppsspp`, x64 + ARM64                                            | [hrydgard/ppsspp](https://github.com/hrydgard/ppsspp/releases), Windows x64/ARM64 ZIPs                                                                                                                                      | Adjacent `memstick`; refuse installer-created `installed.txt`; HLE needs no BIOS                                                                                                                  |
| Flycast                 | Added `flycast`                                                        | [flyinghead/flycast](https://github.com/flyinghead/flycast/releases), `flycast-win64-*.zip`                                                                                                                                 | Adjacent `emu.cfg` and `data` including VMUs; optional Dreamcast BIOS, game-specific arcade BIOS                                                                                                  |
| Cemu                    | Added `cemu`                                                           | [cemu-project/Cemu](https://github.com/cemu-project/Cemu/releases), Windows x64 ZIP                                                                                                                                         | Native `portable` directory, including configuration/cache/MLC; local keys/account data only                                                                                                      |
| Azahar                  | Added `azahar`                                                         | [azahar-emu/azahar](https://github.com/azahar-emu/azahar/releases), MSVC Windows ZIP                                                                                                                                        | Native `user`; decrypted game dumps, local system data; CIA installation stays in Azahar                                                                                                          |
| Vita3K                  | Added `vita3k`, x64 + ARM64                                            | [Vita3K/Vita3K](https://github.com/Vita3K/Vita3K/releases), continuous Windows ZIPs                                                                                                                                         | Native `portable`, filesystem under `fs`; local PSP2UPDAT.PUP through `--firmware`; import installed app `eboot.bin`                                                                              |
| Xemu                    | Added `xemu`, x64 + ARM64                                              | [xemu-project/xemu](https://github.com/xemu-project/xemu/releases), versioned Windows ZIPs excluding debug/PDB/compatibility aliases                                                                                        | Adjacent TOML; local MCPX, flash BIOS and QCOW2 hard disk; own EEPROM; XISO input                                                                                                                 |
| Xenia                   | Added `xenia` (Canary)                                                 | [xenia-canary releases](https://github.com/xenia-canary/xenia-canary-releases/releases), Windows ZIP                                                                                                                        | `portable.txt`; preserve content/config/storage; ISO or XEX input                                                                                                                                 |
| XeniaNative             | Same engine, Linux-native packaging route                              | Windows counterpart is `xenia` above; Linux AppImage is not offered as a Windows binary                                                                                                                                     | Separate Linux package is unnecessary on Windows                                                                                                                                                  |
| MAME                    | Added `mame`, x64 + ARM64                                              | [mamedev/mame](https://github.com/mamedev/mame/releases), architecture SFX + SHA256SUMS                                                                                                                                     | Read embedded 7-Zip data without executing the SFX; driver stem plus ROM-directory path; local matching BIOS/device sets                                                                          |
| SuperModel / Supermodel | Added `supermodel`                                                     | [trzy/Supermodel](https://github.com/trzy/Supermodel/releases), Windows ZIP                                                                                                                                                 | Native adjacent Config/NVRAM/Saves paths; Model 3 ZIP launch                                                                                                                                      |
| Model2                  | Added `model2`, x86                                                    | [Exact EmuDeck distribution declaration](https://github.com/EmuDeck/emudeck-we/blob/037460efc88c1ab70256ab0833f8f10e7f2904b2/functions/EmuScripts/emuDeckModel2.ps1)                                                        | Legacy 1.1a archive from the EmuDeck-declared PhoenixInteractiveNL mirror; preserve EMULATOR.INI/CFG/NVDATA; add ROM search folder without replacing existing entries, launch driver stem         |
| BigPEmu                 | Added `bigpemu`, x64 + ARM64                                           | [Publisher download page](https://www.richwhitehouse.com/jaguar/index.php?content=download), architecture-specific versioned ZIP                                                                                            | `UserData` with `-localdata`; image must precede options; GUI uses `*null -localdata`; Jaguar and Jaguar CD                                                                                       |
| ares                    | Added `ares`, x64 + ARM64                                              | [ares-emulator/ares](https://github.com/ares-emulator/ares/releases), Windows architecture ZIPs                                                                                                                             | Adjacent `settings.bml`; initialize native Paths for saves/screenshots/firmware/debugging                                                                                                         |
| RMG                     | Added `rmg`                                                            | [Rosalie241/RMG](https://github.com/Rosalie241/RMG/releases), portable Windows ZIP                                                                                                                                          | `portable.txt`, native Config/Save/Game/Save/State paths; optional local 64DD IPL                                                                                                                 |
| Primehack               | Added `primehack`                                                      | [shiiion/dolphin](https://github.com/shiiion/dolphin/releases), PrimeHack ZIP                                                                                                                                               | Native User directory and portable.txt; independent Dolphin-compatible data and launch policies                                                                                                   |
| ScummVM                 | Added `scummvm`                                                        | [Publisher downloads](https://www.scummvm.org/downloads/), versioned x86_64 ZIP + SHA-256                                                                                                                                   | Explicit config/save arguments; marker `.scummvm` in each game folder; native auto-detection; bundled engine data                                                                                 |
| ShadPS4                 | Added `shadps4`                                                        | [shadps4-emu/shadPS4](https://github.com/shadps4-emu/shadPS4/releases), Windows SDL core ZIP                                                                                                                                | Native user directory; built-in Big Picture browser via `-b --same-process`; direct decrypted eboot launch and local system modules. Installs the working core, not a launcher without its engine |
| Citron                  | Added `citron` using current Citron Neo continuation                   | [citron-neo/emulator](https://github.com/citron-neo/emulator/releases), MSVC stable ZIP; [citron-neo/CI](https://github.com/citron-neo/CI/releases), clang-cl nightly ZIP                                                   | Native user/keys/NAND; separate local firmware and decryption keys                                                                                                                                |
| Ryujinx                 | Added `ryujinx` using Ryubing continuation                             | [Stable manifest](https://github.com/ScoopInstaller/Games/blob/master/bucket/ryujinx.json), [Canary manifest](https://github.com/ScoopInstaller/Games/blob/master/bucket/ryujinx-canary.json), hashed official Ryubing URLs | Native portable/system keys and bis/system/Contents/registered firmware; stable x64, Canary x64 + ARM64. Stable ARM64 is explicitly refused by its architecture metadata                          |
| Suyu                    | Added `suyu` using maintained Windows continuation                     | [suyu-emu/suyu-main](https://github.com/suyu-emu/suyu-main/releases), GUI Windows x86_64 ZIP, excluding libretro ZIP                                                                                                        | Native user/keys/NAND; original legacy AppImage route is not used on Windows                                                                                                                      |
| Yuzu                    | Original upstream discontinued; no invented original-upstream download | Existing Eden plus Citron Neo and Suyu cover active Windows descendants                                                                                                                                                     | Separate original Yuzu entry is not an installable source; user-owned dumps/keys/firmware only                                                                                                    |
| ARMSX2                  | Added armsx2, ARM64 only                                               | [Actual upstream Windows ARM64 nightly](https://github.com/ARMSX2/ARMSX2/releases), ARMSX2-nightly-20261021-65fd14b916-Windows-arm64.zip                                                                                    | Current upstream has Windows ARM64 despite the older EmuDeck Linux-only selection. Native ARMSX2.exe, portable.txt, -portable, inis/PCSX2.ini, local PS2 BIOS and separate saves                  |

`emuDeckTemplate.ps1` is an installer template, `RetroArch_maincfg.sh` is configuration, and EmuDeck
ToolScripts (ES-DE, Pegasus, Steam ROM Manager, migration, download helpers, save sync, store,
homebrew installers) are not emulator engines. Historical Citra/Lime3DS names are superseded by
Azahar; their still-published libretro builds remain core choices below.

## Libretro cores and aliases

The Windows snapshot explicitly requests 110 unique core DLL names. WSGM's existing all-core
installer reads the current Windows Buildbot index and core-info metadata rather than limiting the
install to EmuDeck's chosen subset. These names are accounted for as cores, not duplicate standalone
application entries. A removed upstream core cannot be invented or downloaded from an unrelated
mirror; a core is launchable only when its actual Windows DLL and metadata exist.

```text
81, a5200, atari800, blastem, bluemsx, bsnes, bsnes_hd_beta, bsnes_mercury_accuracy,
cap32, chailove, citra, citra2018, crocods, desmume, desmume2015, dolphin,
dosbox_core, dosbox_pure, dosbox_svn, fbalpha2012, fbalpha2012_cps1,
fbalpha2012_cps2, fbalpha2012_cps3, fbalpha2012_neogeo, fbneo, fceumm, flycast,
fmsx, freechaf, freeintv, frodo, fuse, gambatte, gearboy, gearsystem,
genesis_plus_gx, genesis_plus_gx_wide, gpsp, gw, handy, hatari, kronos, lutro,
mame, mame2000, mame2003_plus, mame2010, mednafen_lynx, mednafen_ngp,
mednafen_pce, mednafen_pce_fast, mednafen_pcfx, mednafen_psx, mednafen_psx_hw,
mednafen_saturn, mednafen_supafaust, mednafen_supergrafx, mednafen_vb,
mednafen_wswan, melonds, melondsds, mesen, mesen-s, mgba, mu, mupen64plus_next,
nekop2, neocd, nestopia, np2kai, nxengine, o2em, opera, parallel_n64,
pcsx_rearmed, pcsx2, picodrive, pokemini, ppsspp, prboom, prosystem, puae,
px68k, quasi88, quicknes, race, sameboy, scummvm, smsplus, snes9x,
snes9x2010, squirreljme, stella, stella2014, swanstation, tgbdual, theodore,
uzem, vba_next, vbam, vecx, vice_x128, vice_x64, vice_x64sc, vice_xscpu64,
vice_xvic, virtualjaguar, x1, yabasanshiro, yabause
```

Linux-specific `.so` core selections use the same core-info driven mechanism when an actual Windows
DLL is published; the Linux binary itself is never installed. No BIOS, game ROM, encryption key,
account dump or proprietary firmware is fetched by this expansion.

## Installation and update contracts

Existing manager staging, archive path validation, executable selection, download verification,
atomic activation/rollback, package caching, stopped-process admission and data-copy preservation
remain the owners. MAME SFX payloads are extracted as bytes into an owned temporary archive and then
pass through the same archive reader; the downloaded EXE is never run. EXE repair packages are
retained just like ZIP/7z packages. GitHub mutable continuous tags include asset ID/update identity
so new binaries are discoverable even when a release tag is reused. Stable channels skip prerelease
entries; continuous/nightly channels retain their actual upstream behavior.

New user-directory engines bind native data directories to the existing retained data root. Engines
which write next to the executable carry those user files through the existing preservation path;
configuration references to a retired owned data root are rebased with a backup. Explicit external
paths remain explicit. Existing data collisions retain backups. External registration requires the
native portable marker/layout and never migrates an unrelated installed AppData profile implicitly.

Every package validates its PE architecture and native portable binding. Version/help checks are
used for reviewed noninteractive CLIs. GUI-only entries explicitly select `{portable-layout}`: that
checks the Windows executable and real portable layout without opening a visible emulator. It is
structural validation, not a claim that the application started or rendered a game.

ROM importer choices now include 3DS, Wii U, Xbox, Xbox 360, installed Vita/PS4 applications,
Jaguar/CD, arcade, NAOMI, Atomiswave, Model 2/3 and ScummVM. BIOS requirements remain local actions;
console-specific dispatch prevents a Switch firmware ZIP from being passed to Vita's installer. The
definition page and installed detail on both Overlay and Steam offer **Install required runtimes**
before a managed installation needs them. Both invoke the same manager operation, progress and
cancellation path. Visual C++ acquisition uses Microsoft's documented
[`aka.ms/vc14/vc_redist.<architecture>.exe` permalinks](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist).
Model 2 uses the exact
[Microsoft DirectX Web Installer](https://www.microsoft.com/en-us/download/details.aspx?id=35)
download link published on that page. Windows Authenticode chain/revocation checks and the Microsoft
Corporation signer are verified while holding the downloaded file against replacement. Only this
explicit action requests elevation. Microsoft's documented VC++ `/install /quiet /norestart` options
and the `/Q` quiet option declared in the actual signed `dxwebsetup.exe` usage resource keep
progress in WSGM without an external wizard. Available dependencies skip installer dispatch. No
downloaded script is evaluated and no runtime installer was run during development. Cancelled or
timed-out waiting never terminates setup or automatically retries an uncertain dispatch.

Installation checks real runtime availability before the emulator startup probe and explains which
runtime action to take, preserving any previous emulator version/data. After native setup exits, the
manager checks the actual runtime DLLs and refreshes installed prerequisite state. Model 2 checks
its observed x86 `d3dx9_42.dll` and `xinput1_3.dll` imports; it does not mistake the DirectX core
included in modern Windows for those legacy components. A native setup restart request is reported
explicitly. Runtime setup remains an attended acceptance case for the coordinator/human.

Native-layout evidence:
[Cemu Windows DeterminePaths](https://github.com/cemu-project/Cemu/blob/main/src/gui/wxgui/CemuApp.cpp),
[Vita portable root and fs](https://github.com/Vita3K/Vita3K/blob/master/vita3k/app/src/app_init.cpp),
[Vita command line](https://github.com/Vita3K/Vita3K/blob/master/vita3k/config/src/config.cpp),
[mGBA portable.ini](https://github.com/mgba-emu/mgba/blob/master/src/core/config.c),
[melonDS configuration](https://github.com/melonDS-emu/melonDS/blob/master/src/frontend/qt_sdl/Config.cpp),
[PPSSPP memory stick](https://github.com/hrydgard/ppsspp/blob/master/Windows/main.cpp),
[RMG directories](https://github.com/Rosalie241/RMG/blob/master/Source/RMG-Core/Directories.cpp),
[ares Paths](https://github.com/ares-emulator/ares/blob/master/desktop-ui/settings/settings.cpp),
[shadPS4 command line](https://github.com/shadps4-emu/shadPS4/blob/main/src/main.cpp),
[BigPEmu command line](https://www.richwhitehouse.com/jaguar/index.php?content=userman).

## Artwork and remaining acceptance

27 of the 28 definitions have verified SteamGridDB game IDs. Seven were established through public
game pages; the remainder were verified against the site's own anonymous public search response,
using the `/api/public/search/autocomplete?term=` interface found in its first-party web bundle. For
example,
[RPCS3 public search](https://www.steamgriddb.com/api/public/search/autocomplete?term=RPCS3) returns
the verified `RPCS3 (Emulator)` game record. This is public metadata, with no API key or account
session.
[EmuDeck's emulator SRM parser](https://github.com/EmuDeck/emudeck-we/blob/037460efc88c1ab70256ab0833f8f10e7f2904b2/configs/steam-rom-manager/userData/parsers/emudeck/1_emulators.json)
uses name queries and contains no hardcoded game-ID table.

| Definition  | Verified SteamGridDB record                                                                    |
| ----------- | ---------------------------------------------------------------------------------------------- |
| retroarch   | [RetroArch 5245768](https://www.steamgriddb.com/game/5245768)                                  |
| duckstation | [DuckStation (Emulator) 5267970](https://www.steamgriddb.com/game/5267970)                     |
| pcsx2       | [PCSX2 (Emulator) 35439](https://www.steamgriddb.com/game/35439)                               |
| dolphin     | [Dolphin (Emulator) 34346](https://www.steamgriddb.com/game/34346)                             |
| rpcs3       | [RPCS3 (Emulator) 5245879](https://www.steamgriddb.com/game/5245879)                           |
| eden        | [Eden (Emulator) 5488518](https://www.steamgriddb.com/game/5488518)                            |
| mgba        | [mGBA (Emulator) 5246941](https://www.steamgriddb.com/game/5246941)                            |
| melonds     | [melonDS (Emulator) 5246939](https://www.steamgriddb.com/game/5246939)                         |
| ppsspp      | [PPSSPP (Emulator) 36515](https://www.steamgriddb.com/game/36515)                              |
| flycast     | [flycast (Emulator) 5266194](https://www.steamgriddb.com/game/5266194)                         |
| cemu        | [Cemu (Emulator) 36024](https://www.steamgriddb.com/game/36024)                                |
| azahar      | [Azahar (Emulator) 5480678](https://www.steamgriddb.com/game/5480678)                          |
| vita3k      | [Vita3K (Emulator) 5265565](https://www.steamgriddb.com/game/5265565)                          |
| xemu        | [xemu (Emulator) 5277917](https://www.steamgriddb.com/game/5277917)                            |
| xenia       | [Xenia (Emulator) 5245877](https://www.steamgriddb.com/game/5245877)                           |
| mame        | [MAME (Emulator) 5250066](https://www.steamgriddb.com/game/5250066)                            |
| supermodel  | [Supermodel (Emulator) 5367324](https://www.steamgriddb.com/game/5367324)                      |
| model2      | [Model 2 (emulator) 5316480](https://www.steamgriddb.com/game/5316480)                         |
| bigpemu     | [BigPEmu (Emulator) 5431829](https://www.steamgriddb.com/game/5431829)                         |
| ares        | [ares (Emulator) 5362691](https://www.steamgriddb.com/game/5362691)                            |
| rmg         | [Rosalie's Mupen GUI (Emulator) 5369404](https://www.steamgriddb.com/game/5369404)             |
| primehack   | [PrimeHack 5260937](https://www.steamgriddb.com/game/5260937)                                  |
| scummvm     | [ScummVM 5248828](https://www.steamgriddb.com/game/5248828)                                    |
| shadps4     | [shadPS4 (Emulator) 5458313](https://www.steamgriddb.com/game/5458313)                         |
| citron      | [Citron (Emulator) 5471414](https://www.steamgriddb.com/game/5471414)                          |
| ryujinx     | [Ryujinx (Emulator) 5258324](https://www.steamgriddb.com/game/5258324)                         |
| suyu        | [Suyu (emulator) 5446361](https://www.steamgriddb.com/game/5446361)                            |
| armsx2      | No record returned by the exact first-party public search for ARMSX2; ordinary lookup fallback |

Citron Neo uses the verified Citron parent record; no distinct Neo record was returned. Xenia Canary
and Ryubing likewise use their engine's record. Searches explicitly distinguished the emulator
records from unrelated games named Eden, Xenia or Ares, and the separate Steam-release BigPEmu
record. No IDs were inferred from grid/collection IDs. The shared artwork helper retains normal name
lookup for a future definition without an ID and uses the existing paced provider and static-content
filters. No API key means no artwork lookup and does not prevent creating the shortcut; downloaded
image validation remains with the existing artwork application path.

This worker did not build, run tests, generate frontend assets, deploy, launch emulators, install
firmware, write Steam, commit or push. The coordinator owns the combined validation/delivery batch.
The original Model 2 server returned 521 in this research environment; its configured distribution
is the exact EmuDeck-declared mirror. That 529235-byte archive was downloaded and inspected without
running either emulator executable: it contains both native x86 executables, EMULATOR.INI, scripts
and README.TXT. The shipped README confirms driver-name launch; its INI documents a ten-directory
ROM search limit. Its reviewed SHA-256 is pinned:
`338c579a2ce9a9661cc6036999a76b19241140dadce42599a8c9e5fbe990fac3`. Ryubing primary URLs could not
be fetched here, so its maintained manifests and SHA-256 metadata were reviewed without claiming
download success. Legacy Suyu hosting errors were not treated as proof that the maintained Windows
continuation is absent. Other archive downloads, GUI startup, save/update/repair round trips, BIOS
compatibility, ROM gameplay, artwork and Steam/runtime acceptance remain unverified until that
batch/manual use.
