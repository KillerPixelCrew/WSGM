# WSGM — Windows Steam Game Mode

WSGM reconstructs the SteamOS Game Mode experience on Windows 11 — on gaming
handhelds, gaming PCs, and DIY Steam Machines / living-room builds alike. It
replaces the Windows shell **for your user account only** and boots straight into Steam
Big Picture.
A touch/hotkey/controller overlay lets you hop to the desktop, back to game mode, switch between running apps, and
power the device down.


## ⚠ Recovery — read this FIRST

WSGM replaces your shell. If anything goes wrong you will see a black screen instead of
a desktop. **You can always recover:**

1. Press **Ctrl+Alt+Del** (this always works — it belongs to Windows, not the shell).
   On a handheld without a keyboard, attach a USB/Bluetooth keyboard.
2. Choose **Task Manager** → **Run new task**.
3. Type either:
   - `explorer.exe` — brings the desktop back for this session, or
   - `%LOCALAPPDATA%\WSGM\bin\WSGM.exe --restore-shell` — permanently restores your
     previous shell and starts the desktop.
4. If WSGM crashes repeatedly at logon, it disarms itself automatically (3 starts within
   2 minutes → restores your previous shell and starts explorer).

## Why

I know there is Windows 11's FSE, but it has a very specific Problem.
Windows 11's Xbox Full Screen Experience does not deliver controller input to elevated
processes.

Steam must run elevated if you want Steam Input to keep working while elevated windows have focus (UIPI). 
That means if you want to update a Driver in the Device Manager for example, Steam's Desktop Layout stops working the moment Device Manager is in focus.
Older Games that need to run Elevated also fail out on Steam Input as a Result.
The last Issue was that if Steam was started Elevated through Native FSE, it refused Controller Input from Virtual Controllers as made by Handheld Companion or ClawTweaks. Which I can only attribute to some, excuse my language, UWP Style Sandboxing Bullshit. Most likely the same shit that stops Steam Input from Working on XBOX Games.

WSGM gives you a fullscreen boot-to-Steam experience without FSE, so both things work at once.

## How it works

- Registers itself as your **per-user shell** (`HKCU\...\Winlogon\Shell`). 
  A well established method especially in Enterprise Environments for Kiosks or ThinClients, so this is by no way a hacky solution and probably exists for longer than I am alive.
- At logon it starts your **startup apps** (each optionally elevated — e.g. Handheld
  Companion), then **Steam Big Picture**. The **First app delay** setting adds an
  optional wait before the first startup app launches (useful when drivers or
  services need a moment after logon); the apps themselves start staggered.
  Steam's location comes from the registry;
  if Steam isn't running yet, the `steam://open/bigpicture` protocol boots it.
- **Quick access panel** (swipe in from the bottom or right screen edge, press the
  configurable hotkey — default Ctrl+Alt+Home — a recorded controller chord, or
  automatically when Steam exits): a right-side, Steam-QAM-style panel that leaves the
  game visible. From it you can focus or start Steam, return to the desktop and back to
  game mode, **exit Big Picture** while staying in game mode (handy for adding a library),
  **cycle through your running programs**, close
  Steam, open settings. Fully controller-navigable.
  Tapping anywhere outside the panel dismisses it.
- **Return to Desktop** exits Big Picture, pauses Steam monitoring, and starts `explorer.exe` 
   **Back to Game Mode** ends explorer and brings Big Picture back, restarting Steam if it closed meanwhile. WSGM stays resident the whole time.

## Install

**Prerequisite: Steam.** WSGM is Steam-exclusive and the setup refuses to install
when Steam is missing — install Steam, sign in once, then run the setup. Everything
else is self-contained (native binary: no .NET runtime, no VC++ redistributable);
Windows 11 is required.

Download and run **`WSGM-Setup-<version>.exe`** — a normal installer wizard
(per-user, no administrator rights, no UAC). It installs to `%LOCALAPPDATA%\WSGM\bin`,
adds a Start Menu entry, and shows up in Settings → Apps. Then:

1. Open WSGM — Steam is detected automatically. Add startup apps from the suggestions
   (Handheld Companion and friends are detected too).
2. Click **Install as shell** (this is the explicit, separate step that changes your shell).
3. Sign out, sign back in.

Upgrading: just run the newer setup. It closes a running WSGM first (it is most likely
your active shell) and restarts it afterwards in the mode it was in.

Uninstall: Windows Settings → Apps → WSGM. The uninstaller stops a running WSGM,
restores your previous Windows shell and puts machine settings it changed (UAC
prompt behavior, lock-on-wake) back from their snapshots — all **before** removing
any files. (`WSGM.exe --restore-shell` also works from anywhere, any time.)

Portable use: the standalone `WSGM.exe` + `.dll` files also run from any folder —
on first run they offer to install, or you can keep running portable.

Building a release yourself: `.\build.ps1` (needs the .NET 9 SDK, VS C++ build tools,
and Inno Setup 6) → `publish\WSGM-Setup-<version>.exe`.

## Command line

| Flag | Effect |
|---|---|
| *(none)* | Auto: shell mode if registered as shell and no desktop is running, else settings |
| `--shell` | Force shell mode |
| `--settings` | Force settings window |
| `--restore-shell` | Restore previous shell registration, start explorer, exit (recovery; needs no working GUI) |
| `--unregister-shell` | Restore previous shell registration and exit — quiet: no explorer start, no UI (used by the uninstaller) |
| `--uninstall-restore` | Restore machine settings (UAC, lock-on-wake) from their snapshots and exit (used by the uninstaller) |
| `--install` | Headless: install/update app files only |
| `--uninstall-app` | Headless: restore shell if ours, remove shortcut/registration/files |
| `--setup` | Headless: install app + register as shell (for scripts) |
| `--overlay-test` | Show the overlay without shell mode (development/testing) |
| `--set-uac-silent` / `--restore-uac` | Elevated one-shot: silence/restore the UAC prompt for admins (WSGM relaunches itself elevated to run these) |
| `--disable-lock-on-wake` / `--restore-lock-on-wake` | Elevated one-shot: disable/restore the sign-in requirement on wake |

## AI usage disclaimer

Large parts of WSGM are written with AI assistance, directed and
reviewed by a human. Changes are tested on real handheld hardware before release.

## License

MIT. Controller button glyphs from CC0 prompt packs (see `src/WSGM/Assets/Glyphs/CREDITS.md`).
