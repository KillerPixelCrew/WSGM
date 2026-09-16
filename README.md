<p align="center">
  <img src="docs/banner.svg" alt="WSGM, Windows Steam Game Mode" width="810">
</p>

WSGM rebuilds the SteamOS Game Mode experience on Windows 11, on gaming handhelds, gaming PCs and
DIY Steam Machines. You sign in, you land in Steam Big Picture, you drive everything with the pad
and the touchscreen, and you only see the desktop when you ask for it. Explorer stays your Windows
shell the whole time.

## What it does

**Boots straight to Big Picture.** A logon service starts WSGM at sign-in, either into Game Mode
behind a splash screen or into a resident desktop session. Switching between them is one press.

**Desktop Mode** is the same WSGM with Explorer as the shell. It sits in the notification area,
starts the windowed Steam client itself, keeps plugins, overlay, hotkeys and performance services
running, and enters Game Mode from the icon or the overlay. This is the mode for gaming PCs and
Steam Machines. See [Game Mode and Desktop Mode](#game-mode-and-desktop-mode).

**One quick access sheet** slides down from the top edge and leaves the game visible below it.
Controller and touch driven, with a home tab of actions, grouped sections and plugin widgets you pin
yourself, session control, Steam and device tools, power actions, your open programs, tray icons,
Wi-Fi and Bluetooth state, battery and a clock. The left and right edges stay Steam's own menus,
exactly like SteamOS.

**Steam's own Quick Access Menu, working.** Steam ships its Performance, audio, Bluetooth and
network menus on Windows with nothing behind them. WSGM answers them, so TDP sliders, frame limit,
per-game performance profiles, brightness, volume, Bluetooth and Wi-Fi all work inside Steam's own
UI the way they do on a Steam Deck. Steam's Storage and Screensaver settings pages get backends too.

**Frame limit, OSD and AutoTDP** through your own RivaTuner Statistics Server install: a frame limit
paired with the refresh rates your display actually accepts, on-screen display levels, and an
AutoTDP that steers the power limit from measured frametimes.

**Device plugins.** One MIT-licensed Device SDK, one installed package at a time. The MSI Claw 8 AI+
A2VM package is the reference: power and charge limits, fan behaviour, RGB lighting, the controller
and its motion sensors, OEM buttons that open Steam's menus, variable refresh, Intel Endurance
Gaming, GPU memory share, frame presentation, and a virtual controller. You can write one for
another handheld with [Device Lab](src/WSGM.DeviceLab/README.md) and the
[authoring guide](docs/device-plugin-authoring.md).

**Device power profiles.** Pick a plugin-defined TDP, firmware scenario and Windows mode preset from
the Device page or from Steam's QAM Performance tab. The Claw offers Super Battery, Balanced,
Extreme Performance and Full Power. Changing something by hand saves a Custom profile for whichever
source you are on, AC or battery, and switching back restores it. The QAM has separate sustained
(PL1) and boost (PL2) sliders that follow device readback when a profile changes.

**AC and battery profiles**, as global defaults and per-game overrides under Device > Power. Shared
Power, RGB, Controller and Info pages combine Windows and device controls, including Windows power
schemes.

**Display and power from the sheet:** brightness, resolution and refresh rate, the Intel P-core and
E-core preference, a manual keep-awake, muting while the screen is off during downloads, display-off
timeouts, and a report of what woke the machine from standby.

**Game Mode display layouts.** Default drops every display to 100% scaling so DPI-unaware games
render 1:1. Custom applies a layout you edit in Settings, optionally after waiting for a TV to show
up and after running plugin actions like switching an HDMI input.

**Common plugins** are packages beyond the device slot, each one explicitly enabled. The first is an
IR plugin for the XIAO IR Mate that learns and sends remote codes over USB or Wi-Fi and can drive an
HDMI switch or TV as part of entering Game Mode. It is still under development, see its
[README](src/WSGM.Plugin.Ir/README.md).

**Wi-Fi and Bluetooth** without leaving game mode, since Windows' own flyouts cannot open there.
Join networks, pair controllers and headsets.

**Audio:** volume and output-device switching from the sheet, plus an on-screen indicator for
hardware volume keys.

**Safe Eject** for SD cards and USB drives, from the sheet.

**Library tabs.** Build custom tabs for Steam's library from filters (installed, tags, playtime,
size, title patterns and so on), reorder the whole tab strip, and hide Steam's built-in tabs.

**SD card and external drive libraries.** Every removable Steam library gets its own tab that
remembers its games while the card is out. Rename, hide or forget cards from a controller-driven
manager, and a badge on every library tile and game page names the library a game is on, green while
it is installed.

**Connected-library Home.** Big Picture Home's carousel lists every game on the libraries attached
right now, last played first, and drops a card's games when the card comes out.

**Drive formatting.** Turn a card or drive into a ready-to-use Steam library in one guided flow,
keeping its exact drive letter, either from the sheet or from Steam's own Storage page. You can also
register any folder or network share with the running Steam client, no restart needed.

**Artwork.** Browse and apply capsule, hero and logo art from SteamGridDB and Screenscraper.fr for
any game, including non-Steam shortcuts, without leaving game mode.

**A working Wi-Fi icon.** Big Picture's header shows your real network and signal strength on
Windows. Steam never feeds it there, so WSGM does.

**Steam Input everywhere.** WSGM starts Steam itself, elevated, so Steam Input keeps working over
elevated windows and games. Windows' own Steam startup entries would undo that, so Quick Setup asks
to take them over. Nothing is deleted, and uninstall puts back exactly what WSGM changed.

**The Steam Input Lease** is the first tool that takes the controller out of the running Steam
client's hands _while it runs_. Steam is asked to let go of the pad and gets it back the moment it
needs it again: no restart, no drivers, no config changes and no Steam file touched. Steam just sees
a brief unplug. This is what lets WSGM's own panels read the controller while they are open.

**Free the controller for emulators and SDL3 apps.** Steam's desktop layout normally swallows the
pad from every other program. The lease blocks Steam Input for one title, so emulators and SDL3
applications read the real controller directly, and Steam takes it back the moment the game exits.
The same wrapper de-elevates titles that refuse to run elevated, and it can do both at once.

**Per-game launch fixes, applied for you.** Open the panel on a game and pick the fix; WSGM writes
it straight into the running Steam client. No pasting, no restart, and it gets the awkward non-Steam
shortcut setup right by itself. One button puts everything back.

**Make it yours** with a fully configurable boot splash (text, spinner, logo, background, shareable
presets) and an accent colour every surface follows.

**It fails open.** If something goes wrong, WSGM keeps or restores the desktop rather than leaving
you with a black screen, and a crash-loop breaker disarms game mode on its own.

## Demo

The quick access sidebar, and switching between game mode and the desktop:

https://github.com/user-attachments/assets/4e422b98-cf27-4f17-aa46-b8c956ce7275

The 1.x game-mode taskbar (2.0 merges it into the quick access sheet):

https://github.com/user-attachments/assets/c90e6354-5d05-46c5-9866-d5f8a647cbcb

## Game Mode and Desktop Mode

Starting with Windows and taking the screen over are two separate choices. Settings > System has
**Start WSGM at sign-in** and **Start in** (Game or Desktop). The install mode seeds them and Quick
Setup confirms them on first run.

Game Mode ends Explorer and lands in Big Picture behind the splash. Desktop Mode is a complete
resident session: plugins, overlay, hotkey, controller chord and performance services all run, WSGM
starts the windowed Steam client itself and keeps it running, and Explorer stays the shell. The WSGM
notification icon opens the overlay, Settings or Game Mode, and offers Exit WSGM. Start WSGM again
from the Start Menu; launching it while it is already running just opens the existing session. Setup
also offers an optional Desktop shortcut.

**WSGM Settings** has its own Start Menu shortcut, and its own Desktop shortcut when you pick
Desktop shortcuts. It opens Settings in the running resident session and shares its controller input
owner. With no resident session it opens standalone Settings without starting one.

Settings > Display configures what entering Game Mode actually does. Default adjusts scaling only.
Custom applies a saved display layout, optionally after waiting for a display and running plugin
actions. Game Mode and Desktop layouts are edited independently: drag screens around, choose a
primary display, and set resolution, refresh rate, scaling, HDR and exact positions. Copying the
current desktop is optional, refreshing the display list preserves unfinished edits, and Undo
reverses the last one. Remembered displays stay editable while unplugged. Saving applies the layout
on the next mode switch.

Plugin actions can also run when leaving Game Mode and at desktop startup and wake, which is how
external HDMI and input routing works without putting device protocols inside WSGM. The wait for a
display has no time limit, because a TV behind an HDMI switch only appears once the switch selects
this PC. Entering Game Mode is one cancellable transaction: until Explorer leaves, Cancel on the
splash puts the desktop back exactly as it was.

See [session automation](docs/plugin-system.md#session-automation) and
[Game Mode display layouts](docs/power-and-display.md#game-mode-display-layouts).

## ⚠ Recovery, read this first

Game mode ends Explorer while it runs, so if something goes wrong you can end up looking at a screen
with no desktop on it. **You can always get it back:**

1. Press **Ctrl+Alt+Del**. This always works, because it belongs to Windows and not to WSGM. On a
   handheld with no keyboard, plug in a USB or Bluetooth one.
2. Choose **Task Manager**, then **Run new task**.
3. Type one of:
   - `explorer.exe` to bring the desktop back for this session, or
   - `%LOCALAPPDATA%\WSGM\bin\WSGM.exe --restore-shell` to turn the sign-in start **off** and start
     the desktop, so the next sign-in is an ordinary Windows one.

There are also safety nets that run by themselves. The boot takeover keeps the desktop if it cannot
end Explorer cleanly, the service starts Explorer if WSGM crashes without one, and three failed
game-mode starts within two minutes disarm game mode automatically.

## Why not Windows' own fullscreen experience?

Windows 11's Xbox Full Screen Experience does not deliver controller input to elevated processes,
and Steam has to run elevated if you want Steam Input to keep working while an elevated window has
focus, or in games that require elevation. Under FSE, an elevated Steam also refuses input from
virtual controllers, which breaks Handheld Companion and anything like it. WSGM gives you
boot-to-Steam without FSE, so all of it works at the same time.

## Compatibility

**Handheld Companion** works, and gets heavy use on WSGM's own development devices. Tested against
all of its controller types.

**CSSLoader Desktop** works, with one caveat: themes restyle the same Steam UI that WSGM's
library-tab engine patches, so a theme that touches the library's tab strip can break the injected
tabs.

**Non-Steam shortcuts are set up differently.** WSGM handles this for you, but it is worth knowing
why the two look different in Steam. A normal Steam title takes the wrapper in its **Launch
Options** (`"…\WSGM.Launch.exe" --deelevate -- %command%`). A non-Steam shortcut cannot: Steam
quietly ignores an exe-replacement launch option there and runs the original target anyway, mangling
the command line in the process, so the wrapper never starts. For a shortcut, the wrapper goes in
the **Target** field and the real program moves into **Launch Arguments**. With the Steam
integration turned off, the Tools tab copies the command for you to apply by hand, and then the
shortcut layout is on you.

## How it works

The full technical write-up lives in the wiki:
**[How it Works](https://github.com/KillerPixelCrew/WSGM/wiki/How-it-Works)**. It covers the logon
service, the Explorer takeover, Desktop Mode, the Steam Input Lease, the Steam CEF bridge behind the
library features, device plugins, elevation and recovery. The in-repo [docs](docs/README.md) carry
the exact log lines, budgets and dates it summarizes.

## Install

**You need** Steam (installed and signed in at least once, setup refuses to run without it) and
**Windows 11 x64**. Everything else is self-contained: no .NET runtime, no redistributables.

1. Download and run **`WSGM-Setup-<version>.exe`** from the
   [latest release](https://github.com/KillerPixelCrew/WSGM/releases/latest). It asks for
   administrator rights once, to register the logon service.
2. Pick an install mode:
   - **Minimal** boots into Game Mode with nothing device-specific. The right choice on any handheld
     or PC WSGM has no package for.
   - **MSI Claw 8 AI+ A2VM** adds the device integration and the virtual controller, for that exact
     handheld. It offers the USB/IP and HidHide driver step, which needs a reboot.
   - **Desktop first** starts WSGM with Windows and waits in the notification area, with Game Mode
     one press away whenever you want it.

   The mode decides where the first run starts and whether the device integration is on. Everything
   it chooses is a normal setting afterwards, and re-running setup to repair or upgrade never
   changes what you set. **Custom** picks components by hand and leaves the settings at their
   defaults.

3. Open WSGM. Steam is detected automatically, and you can add startup apps from the suggestions,
   which detect Handheld Companion and friends too. Quick Setup shows your mode's answers for you to
   confirm, and lists any Run entries, Startup shortcuts or scheduled tasks Windows uses to start
   Steam. WSGM has to start Steam itself for Steam Input to work over elevated windows, so Continue
   waits until you let it disable those. Skip leaves them alone and turns the takeover off.

**Upgrading:** run the newer setup. **Uninstalling:** Windows Settings > Apps > WSGM. It restores
every machine setting it changed and removes its files.

Building from source: `.\build.ps1` (needs the .NET SDK, Rust with the MSVC toolchain, Go, Git, a
cgo-capable GCC, and Inno Setup 6), which produces `publish\WSGM-Setup-<version>.exe`.

## Credits

The library features are Windows reimplementations of approaches from Decky Loader plugins on
SteamOS: [TabMaster](https://github.com/Tormak9970/TabMaster) for filter tabs and tab-strip control,
[MicroSDeck](https://github.com/CEbbinghaus/MicroSDeck) for per-card libraries, and
[decky-steamgriddb](https://github.com/SteamGridDB/decky-steamgriddb) for the artwork flow. The
Steam Input Lease's blocking model was informed by SpecialK's ValvePlug. Controller button glyphs
come from CC0 prompt packs, see `src/WSGM/Assets/Glyphs/CREDITS.md`.

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

The Plugin SDK, Device SDK, Device Lab, Claw reference plugin, Handheld Companion scaffold and ROG
Ally X scaffold, including their test projects, keep their MIT licenses under `src` and `tests`, so
external packages can implement the contracts. See [device project layout](docs/device-projects.md)
for paths and build commands.

Bundled third-party components keep their own licenses; their notices ship beside the executable and
with the installer.
