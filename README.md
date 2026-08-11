<p align="center">
  <img src="docs/banner.svg" alt="WSGM — Windows Steam Game Mode" width="810">
</p>

WSGM reconstructs the SteamOS Game Mode experience on Windows 11 — on gaming handhelds, gaming PCs,
and DIY Steam Machines alike. Sign in, land directly in Steam Big Picture, control everything with
the pad and the touchscreen, and only see the desktop when you ask for it. Explorer stays your
Windows shell the whole time.

## Features

- **Boot to Big Picture** — a logon service starts game mode at sign-in behind a splash screen;
  switching to the desktop and back is one press, any time.
- **Quick access panel** — a controller- and touch-driven sidebar: session control, tools, and power
  actions, docked so the game stays visible.
- **Game-mode taskbar** — open windows, tray icons, Wi-Fi/Bluetooth state, battery, and a clock.
- **Wi-Fi & Bluetooth** — join networks and pair controllers/headsets without leaving game mode
  (Windows' own flyouts can't open there).
- **Audio** — volume and output-device switching from the taskbar, plus an on-screen indicator for
  hardware volume keys.
- **Safe Eject** — remove SD cards and USB drives cleanly from the taskbar.
- **Library tabs** — build custom tabs for Steam's library from filters (installed, tags, playtime,
  size, title patterns, …), reorder the whole tab strip, and hide Steam's built-in tabs.
- **SD card & external drive libraries** — every removable Steam library gets its own tab that
  remembers its games while ejected; rename, hide, or forget cards from a controller-driven manager,
  and an "On: card" badge shows where the game you're viewing lives.
- **Drive formatting** — format a card or drive into a ready-to-use Steam library in one guided
  flow, keeping its exact drive letter; register any folder or network share with the running Steam
  client, no restart.
- **SteamGridDB artwork** — browse and apply capsule/hero/logo art for any game, including non-Steam
  shortcuts, without leaving game mode.
- **A working Wi-Fi icon** — Big Picture's header shows your real network and signal strength on
  Windows (Steam never feeds it there; WSGM does).
- **Steam Input everywhere** — Steam runs elevated so Steam Input keeps working over elevated
  windows and games; the **Steam Input Lease** hands the controller to WSGM's panels only while
  they're open, and per-game helpers block Steam Input or de-elevate a single title via launch
  options copied from the Tools tab.
- **Make it yours** — a fully configurable boot splash (text, spinner, logo, background, shareable
  presets) and an accent colour every surface follows.
- **Fails open** — if anything goes wrong, WSGM keeps or restores the desktop rather than leaving a
  black screen, and a crash-loop breaker disarms game mode by itself.

## Demo

The quick access sidebar, and switching between game mode and the desktop:

https://github.com/user-attachments/assets/4e422b98-cf27-4f17-aa46-b8c956ce7275

The game-mode taskbar:

https://github.com/user-attachments/assets/c90e6354-5d05-46c5-9866-d5f8a647cbcb

## ⚠ Recovery — read this FIRST

Game mode ends Explorer while it runs, so if something goes wrong you can end up looking at a screen
with no desktop on it. **You can always recover:**

1. Press **Ctrl+Alt+Del** (this always works — it belongs to Windows, not to WSGM). On a handheld
   without a keyboard, attach a USB/Bluetooth keyboard.
2. Choose **Task Manager** → **Run new task**.
3. Type either:
   - `explorer.exe` — brings the desktop back for this session, or
   - `%LOCALAPPDATA%\WSGM\bin\WSGM.exe --restore-shell` — turns **off** game mode at sign-in and
     starts the desktop, so the next sign-in is an ordinary Windows one.

Safety nets also run on their own: the boot takeover keeps the desktop if it can't end Explorer
cleanly, the service starts Explorer if WSGM crashes without one, and three failed game-mode starts
within two minutes disarm game mode automatically.

## Why not Windows' own fullscreen experience?

Windows 11's Xbox Full Screen Experience doesn't deliver controller input to elevated processes —
and Steam must run elevated if you want Steam Input to keep working while an elevated window has
focus, or in games that require elevation. Under FSE, an elevated Steam additionally refuses input
from virtual controllers (Handheld Companion and friends). WSGM gives you boot-to-Steam without FSE,
so all of it works at once.

## How it works

The full technical deep-dive — the logon service, the Explorer takeover, the Steam Input Lease, the
Steam CEF bridge behind the library features, elevation and recovery — lives in the wiki:
**[How it Works](https://github.com/NightHammer1000/WSGM/wiki/How-it-Works)**.

## Install

**Prerequisites:** Steam (installed and signed in once — the setup refuses to run without it) and
**Windows 11 x64**. Everything else is self-contained: no .NET runtime, no redistributables.

1. Download and run **`WSGM-Setup-<version>.exe`** from the
   [latest release](https://github.com/NightHammer1000/WSGM/releases/latest). It asks for
   administrator rights once, to register the logon service.
2. Open WSGM — Steam is detected automatically; add startup apps from the suggestions (Handheld
   Companion and friends are detected too).
3. Leave **Start game mode at sign-in** on, **Save changes**, sign out and back in.

**Upgrading:** run the newer setup. **Uninstall:** Windows Settings → Apps → WSGM — it restores
every machine setting it changed and removes its files.

Building from source: `.\build.ps1` (needs the .NET SDK, VS C++ build tools, a Rust toolchain and
Inno Setup 6) → `publish\WSGM-Setup-<version>.exe`.

## Credits

The library features are Windows reimplementations of approaches from Decky Loader plugins on
SteamOS: [TabMaster](https://github.com/Tormak9970/TabMaster) (filter tabs, tab-strip control),
[MicroSDeck](https://github.com/CEbbinghaus/MicroSDeck) (per-card libraries), and
[decky-steamgriddb](https://github.com/SteamGridDB/decky-steamgriddb) (artwork flow). The Steam
Input Lease's blocking model was informed by SpecialK's ValvePlug. Controller button glyphs come
from CC0 prompt packs (see `src/WSGM/Assets/Glyphs/CREDITS.md`).

## AI usage disclaimer

Large parts of WSGM are written with AI assistance, directed and reviewed by a human. Changes are
tested on real handheld hardware before release.

## License

MIT.
