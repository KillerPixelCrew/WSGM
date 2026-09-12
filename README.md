<p align="center">
  <img src="docs/banner.svg" alt="WSGM — Windows Steam Game Mode" width="810">
</p>

WSGM reconstructs the SteamOS Game Mode experience on Windows 11 — on gaming handhelds, gaming PCs,
and DIY Steam Machines alike. Sign in, land directly in Steam Big Picture, control everything with
the pad and the touchscreen, and only see the desktop when you ask for it. Explorer stays your
Windows shell the whole time.

## Features

- **Boot to Big Picture** — a logon service starts WSGM at sign-in, into Game Mode behind a splash
  screen or into a resident desktop session; switching modes is one press, any time.
- **Desktop Mode** — the same WSGM, with Explorer as the shell: it waits in the notification area,
  starts the windowed Steam client itself, keeps plugins, overlay, hotkeys and performance services
  running, and enters Game Mode from the icon or the overlay. Built for gaming PCs and Steam
  Machines; see [Game Mode and Desktop Mode](#game-mode-and-desktop-mode).
- **Quick access sheet** — one controller- and touch-driven surface that slides down from the top
  edge and leaves the game visible below: a home tab of rows and plugin widgets you pin yourself,
  session control, Steam and device tools, power actions, your open programs, tray icons,
  Wi-Fi/Bluetooth state, battery, and a clock. Left and right edges stay Steam's own menus, exactly
  like SteamOS.
- **Steam's own Quick Access Menu, revived** — Steam ships its Performance, audio, Bluetooth and
  network menus on Windows with nothing behind them. WSGM answers them, so TDP sliders, frame limit,
  per-game performance profiles, brightness, volume, Bluetooth and Wi-Fi work inside Steam's own UI,
  as they do on a Steam Deck. Steam's Storage and Screensaver settings pages get backends too.
- **Frame limit, overlay and AutoTDP** — through your own RivaTuner Statistics Server install: a
  frame limit paired with the rates your display actually accepts, on-screen display levels, and an
  AutoTDP that steers the power limit from measured frametimes.
- **Device plugins** — one MIT-licensed Device SDK, one installed package at a time. The MSI Claw 8
  AI+ A2VM package is the reference: power and charge limits, fan behaviour, RGB lighting, the
  controller and its motion sensors, OEM buttons that open Steam's menus, variable refresh, Intel
  Endurance Gaming, GPU memory share and frame presentation, plus a virtual controller. Write one
  for another handheld with [Device Lab](src/WSGM.DeviceLab/README.md) and the
  [authoring guide](docs/device-plugin-authoring.md).
- **Device power profiles** — pick a plugin-defined TDP, firmware scenario and Windows mode preset
  from the Device page or Steam QAM Performance. The Claw offers Super Battery, Balanced, Extreme
  Performance and Full Power; independent changes save Custom for the active AC or battery source,
  and switching back restores those custom values. QAM has separate sustained (PL1) and boost (PL2)
  sliders that follow device readback when a profile changes.
- **AC and battery profiles** — save global defaults and per-game overrides under Device > Power.
  Shared Power, RGB, Controller and Info pages combine Windows and device controls, including
  Windows power schemes.
- **Display and power from the sheet** — brightness, resolution and refresh rate, the Intel
  P-core/E-core preference, keep the display awake while a game runs, mute while the screen is off
  during downloads, display-off timeouts, and a report of what woke the machine from standby.
- **Game Mode display layouts** — Default drops every display to 100% scaling so DPI-unaware games
  render 1:1; Custom applies a display arrangement you captured from your desktop, optionally after
  waiting for a TV to appear and after running plugin actions such as switching an HDMI input.
- **Common plugins** — packages beyond the device slot, explicitly enabled. The first is an IR
  plugin for the XIAO IR Mate that learns and sends remote codes over USB or Wi-Fi and drives an
  HDMI switch or TV as part of entering Game Mode. It is under development; see its
  [README](src/WSGM.Plugin.Ir/README.md).
- **Wi-Fi & Bluetooth** — join networks and pair controllers/headsets without leaving game mode
  (Windows' own flyouts can't open there).
- **Audio** — volume and output-device switching from the sheet, plus an on-screen indicator for
  hardware volume keys.
- **Safe Eject** — remove SD cards and USB drives cleanly from the sheet.
- **Library tabs** — build custom tabs for Steam's library from filters (installed, tags, playtime,
  size, title patterns, …), reorder the whole tab strip, and hide Steam's built-in tabs.
- **SD card & external drive libraries** — every removable Steam library gets its own tab that
  remembers its games while ejected; rename, hide, or forget cards from a controller-driven manager,
  and a badge on every library tile and game page names the library a game is on, green while it is
  installed.
- **Connected-library Home** — Big Picture Home's carousel lists every game on the libraries
  attached right now, last played first, and drops a card's games when the card comes out.
- **Drive formatting** — format a card or drive into a ready-to-use Steam library in one guided
  flow, keeping its exact drive letter, from the sheet or from Steam's own Storage page; register
  any folder or network share with the running Steam client, no restart.
- **Artwork** — browse and apply capsule/hero/logo art from SteamGridDB and Screenscraper.fr for any
  game, including non-Steam shortcuts, without leaving game mode.
- **A working Wi-Fi icon** — Big Picture's header shows your real network and signal strength on
  Windows (Steam never feeds it there; WSGM does).
- **Steam Input everywhere** — WSGM starts Steam itself, elevated, so Steam Input keeps working over
  elevated windows and games. Windows' own Steam startup entries would undo that, so Quick Setup
  asks to take them over; nothing is deleted, and uninstall puts back exactly what WSGM changed.
- **Steam Input Lease** — the first tool to take the controller out of the running Steam client's
  hands **dynamically**: Steam is asked to let go of the pad and gets it back the moment it's needed
  again — no restart, no drivers, no config changes, no Steam file touched; Steam just sees a brief
  unplug. It's what lets WSGM's own panels read the controller while they're open.
- **Free the controller for emulators & SDL3 apps** — Steam's desktop layout normally swallows the
  pad from every other program. The lease blocks Steam Input for a single title, so emulators and
  SDL3 applications read the real controller directly — and Steam takes it back the moment the game
  exits. The same wrapper de-elevates titles that refuse to run elevated, and can do both at once.
- **Per-game launch fixes, applied for you** — open the panel on a game and pick the fix; WSGM
  writes it straight into the running Steam client. No pasting, no restart, and it gets the awkward
  non-Steam-shortcut setup right by itself. One button puts everything back.
- **Make it yours** — a fully configurable boot splash (text, spinner, logo, background, shareable
  presets) and an accent colour every surface follows.
- **Fails open** — if anything goes wrong, WSGM keeps or restores the desktop rather than leaving a
  black screen, and a crash-loop breaker disarms game mode by itself.

## Demo

The quick access sidebar, and switching between game mode and the desktop:

https://github.com/user-attachments/assets/4e422b98-cf27-4f17-aa46-b8c956ce7275

The 1.x game-mode taskbar (2.0 merges it into the quick access sheet):

https://github.com/user-attachments/assets/c90e6354-5d05-46c5-9866-d5f8a647cbcb

## Game Mode and Desktop Mode

Starting with Windows and taking the screen over are separate choices. Settings > System has **Start
WSGM at sign-in** and **Start in** (Game or Desktop); the install mode seeds them, and Quick Setup
confirms them on first run.

Game Mode ends Explorer and lands in Big Picture behind the splash. Desktop Mode is a complete
resident session: plugins, overlay, hotkey, controller chord and performance services all run, WSGM
starts the windowed Steam client itself and keeps it running, and Explorer stays the shell. The WSGM
notification icon opens the Overlay, Settings or Game Mode and offers Exit WSGM. Start WSGM again
from the Start Menu; launching it while running opens the existing session. Setup also offers an
optional Desktop shortcut.

Settings > Display configures what entering Game Mode does. Default adjusts scaling only. Custom
applies a saved display layout, optionally after waiting for a display and running plugin actions.
Snapshot captures the desktop as it is arranged now, and every display WSGM has seen stays editable
afterwards, including one that is currently unplugged. Plugin actions can also run when leaving Game
Mode and at desktop startup and wake, which is how external HDMI and input routing works without
putting device protocols in WSGM itself. The wait for a display has no time limit, for a TV that
only appears once an HDMI switch selects this PC. Entering Game Mode is one cancellable transaction:
until Explorer leaves, Cancel on the splash puts the desktop back exactly as it was. See
[session automation](docs/plugin-system.md#session-automation) and
[Game Mode display layouts](docs/power-and-display.md#game-mode-display-layouts).

## ⚠ Recovery — read this FIRST

Game mode ends Explorer while it runs, so if something goes wrong you can end up looking at a screen
with no desktop on it. **You can always recover:**

1. Press **Ctrl+Alt+Del** (this always works — it belongs to Windows, not to WSGM). On a handheld
   without a keyboard, attach a USB/Bluetooth keyboard.
2. Choose **Task Manager** → **Run new task**.
3. Type either:
   - `explorer.exe` — brings the desktop back for this session, or
   - `%LOCALAPPDATA%\WSGM\bin\WSGM.exe --restore-shell` — turns **off** the sign-in start and starts
     the desktop, so the next sign-in is an ordinary Windows one.

Safety nets also run on their own: the boot takeover keeps the desktop if it can't end Explorer
cleanly, the service starts Explorer if WSGM crashes without one, and three failed game-mode starts
within two minutes disarm game mode automatically.

## Why not Windows' own fullscreen experience?

Windows 11's Xbox Full Screen Experience doesn't deliver controller input to elevated processes —
and Steam must run elevated if you want Steam Input to keep working while an elevated window has
focus, or in games that require elevation. Under FSE, an elevated Steam additionally refuses input
from virtual controllers (Handheld Companion and friends). WSGM gives you boot-to-Steam without FSE,
so all of it works at once.

## Compatibility

- **Handheld Companion** — works, and is heavily used on WSGM's own development devices. Tested
  against all of its controller types.
- **CSSLoader Desktop** — works, with caution: themes restyle the same Steam UI that WSGM's
  library-tab engine patches, so a theme that touches the library's tab strip can break the injected
  tabs.
- **Custom (non-Steam) shortcuts are set up differently** — WSGM handles this for you, but it is
  worth knowing why the two look different in Steam. A normal Steam title takes the wrapper in its
  **Launch Options** (`"…\WSGM.Launch.exe" --deelevate -- %command%`). A **non-Steam shortcut**
  cannot: Steam quietly ignores an exe-replacement launch option there and runs the original target
  anyway (it even mangles the command line — the wrapper never starts). So for a shortcut the
  wrapper goes in the **Target** field and the real program moves into **Launch Arguments**. With
  the Steam integration turned off, the Tools tab copies the command and you apply it by hand — in
  that case the shortcut layout above is on you.

## How it works

The full technical deep-dive — the logon service, the Explorer takeover, Desktop Mode, the Steam
Input Lease, the Steam CEF bridge behind the library features, device plugins, elevation and
recovery — lives in the wiki:
**[How it Works](https://github.com/KillerPixelCrew/WSGM/wiki/How-it-Works)**. The in-repo
[docs](docs/README.md) carry the exact log lines, budgets and dates it summarizes.

## Install

**Prerequisites:** Steam (installed and signed in once — the setup refuses to run without it) and
**Windows 11 x64**. Everything else is self-contained: no .NET runtime, no redistributables.

1. Download and run **`WSGM-Setup-<version>.exe`** from the
   [latest release](https://github.com/KillerPixelCrew/WSGM/releases/latest). It asks for
   administrator rights once, to register the logon service.
2. Pick an install mode:
   - **Minimal** — boots into Game Mode. Nothing device-specific; the right choice on any handheld
     or PC WSGM has no package for.
   - **MSI Claw 8 AI+ A2VM** — Game Mode plus the device integration and the virtual controller, for
     that exact handheld. Offers the USB/IP and HidHide driver step, which needs a reboot.
   - **Desktop first** — WSGM starts with Windows and waits in the notification area; Game Mode is
     one press away whenever you want it.

   The mode sets where the first run starts and whether the device integration is on. Everything it
   chooses is a normal setting afterwards, and re-running setup to repair or upgrade never changes
   what you set. **Custom** picks components by hand and leaves the settings at their defaults.

3. Open WSGM — Steam is detected automatically; add startup apps from the suggestions (Handheld
   Companion and friends are detected too). Quick Setup shows your mode's answers for you to
   confirm, and lists any Run entries, Startup shortcuts or scheduled tasks Windows uses to start
   Steam. WSGM has to start Steam itself for Steam Input to work over elevated windows, so Continue
   waits until you let it disable those; Skip leaves them alone and turns the takeover off.

**Upgrading:** run the newer setup. **Uninstall:** Windows Settings → Apps → WSGM — it restores
every machine setting it changed and removes its files.

Building from source: `.\build.ps1` (needs the .NET SDK, Rust with the MSVC toolchain, Go, Git, a
cgo-capable GCC, and Inno Setup 6) → `publish\WSGM-Setup-<version>.exe`.

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

Copyright (C) 2026 NightHammer1000.

WSGM is free software: you can redistribute it and/or modify it under the terms of the **GNU General
Public License as published by the Free Software Foundation, either version 3 of the License, or (at
your option) any later version** ([full text](LICENSE)).

WSGM is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public
License for more details.

The Plugin SDK, Device SDK, Device Lab, Claw reference plugin, and Handheld Companion scaffold,
including their test projects, retain their MIT licenses under `src` and `tests`, so external
packages can implement the contracts. See [device project layout](docs/device-projects.md) for paths
and build commands.

Bundled third-party components keep their own licenses; their notices ship beside the executable and
with the installer.
