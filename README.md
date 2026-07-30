# OpenFSE

A "poor man's Full Screen Experience" for Windows 11 gaming handhelds. OpenFSE replaces the
Windows shell **for your user account only** and boots you straight into your launcher
(e.g. Steam Big Picture, optionally elevated) — no explorer, no Xbox FSE, no FSE input
sandboxing. A touch/hotkey/controller overlay lets you hop to the desktop, back to game
mode, and power the device down.

## ⚠ Recovery — read this FIRST

OpenFSE replaces your shell. If anything goes wrong you will see a black screen instead of
a desktop. **You can always recover:**

1. Press **Ctrl+Alt+Del** (this always works — it belongs to Windows, not the shell).
   On a handheld without a keyboard, attach a USB/Bluetooth keyboard.
2. Choose **Task Manager** → **Run new task**.
3. Type either:
   - `explorer.exe` — brings the desktop back for this session, or
   - `"C:\Program Files\OpenFSE\OpenFSE.exe" --restore-shell` — permanently restores your
     previous shell and starts the desktop (adjust the path to where you put OpenFSE.exe).
4. If OpenFSE crashes repeatedly at logon, it disarms itself automatically (3 starts within
   2 minutes → restores your previous shell and starts explorer).

Belt and braces: keep a second local admin account on the machine. OpenFSE only changes
`HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` for the user that
installed it — other accounts are untouched.

## Why

Windows 11's Xbox Full Screen Experience does not deliver controller input to elevated
processes. Steam must run elevated if you want Steam Input to keep working while elevated
windows have focus (UIPI). Outside FSE — on the plain desktop — elevated Steam receives
controller input just fine. OpenFSE gives you a fullscreen boot-to-Steam experience
without FSE, so both things work at once.

## How it works

- Registers itself as your **per-user shell** (`HKCU\...\Winlogon\Shell`). No admin needed,
  HKLM untouched, other users unaffected. Your previous Shell value (if any) is saved and
  restored on uninstall.
- At logon it starts your **startup apps** (each optionally elevated — e.g. Handheld
  Companion) and then your **home app** (e.g. `Steam.exe steam://open/bigpicture`,
  optionally elevated), then waits in the background.
- **Overlay** (swipe in from the bottom or right screen edge, press the configurable
  hotkey — default Ctrl+Alt+Home — or automatically when the home app exits):
  Return to Desktop / Back to Game Mode / Start home app / Sleep / Restart / Shutdown.
  Fully controller-navigable (D-pad/stick + A/B) with Xbox, PlayStation, or Nintendo
  button glyphs.
- **Return to Desktop** simply starts `explorer.exe`; **Back to Game Mode** ends it again.
  OpenFSE stays resident the whole time.

## Install

Download and run **`OpenFSE-Setup-<version>.exe`** — a normal installer wizard
(per-user, no administrator rights, no UAC). It installs to `%LOCALAPPDATA%\OpenFSE\bin`,
adds a Start Menu entry, and shows up in Settings → Apps. Then:

1. Open OpenFSE → pick your launcher from the list (Steam Big Picture, Playnite,
   BigBox, RetroBat, Kodi, Razer Cortex, Armoury Crate, One Game Launcher). Installed
   ones are detected automatically and everything technical — path, launch arguments,
   window matching — is filled in for you. Add startup apps from the suggestions
   (Handheld Companion and friends are detected too).
2. Click **Install as shell** (this is the explicit, separate step that changes your shell).
3. Sign out, sign back in.

Upgrading: just run the newer setup — settings and shell registration are kept, and it
can update even while OpenFSE is running as the shell.

Uninstall: Windows Settings → Apps → OpenFSE. The uninstaller restores your previous
Windows shell **before** removing any files. (`OpenFSE.exe --restore-shell` also works
from anywhere, any time.)

Portable use: the standalone `OpenFSE.exe` + `.dll` files also run from any folder —
on first run they offer to install, or you can keep running portable.

Building a release yourself: `.\build.ps1` (needs the .NET 9 SDK, VS C++ build tools,
and Inno Setup 6) → `publish\OpenFSE-Setup-<version>.exe`.

## Command line

| Flag | Effect |
|---|---|
| *(none)* | Auto: shell mode if registered as shell and no desktop is running, else settings |
| `--shell` | Force shell mode |
| `--settings` | Force settings window |
| `--restore-shell` | Restore previous shell registration, start explorer, exit (recovery; needs no working GUI) |
| `--install` | Headless: install/update app files only |
| `--uninstall-app` | Headless: restore shell if ours, remove shortcut/registration/files |
| `--setup` | Headless: install app + register as shell (for scripts) |
| `--overlay-test` | Show the overlay without shell mode (development/testing) |

## "Never show UAC prompts" (optional)

Settings has a checkbox that sets Windows' UAC slider to its lowest position
("Never notify"): `ConsentPromptBehaviorAdmin=0` + `PromptOnSecureDesktop=0` in
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`. That is what makes
elevated launches (elevated Steam, Handheld Companion) start silently at boot instead
of stopping on a consent dialog you'd have to tap through on a handheld.

- UAC itself stays **enabled** (`EnableLUA=1` is never touched) — turning that off
  would break Store/UWP apps and require a reboot.
- **Security trade-off**: it applies machine-wide to every administrator account. Any
  program that requests administrator rights then gets them without asking. Only use
  it on a personal device you trust.
- Ticking or unticking requires one administrator confirmation and takes effect
  immediately; the previous values are saved and restored exactly when you untick.

## "No lock screen after screen off / standby" (optional)

Second checkbox in Settings. It writes three things:

- `CONSOLELOCK` ("Require a password on wakeup") = 0 for AC and battery as a machine
  **policy** (`HKLM\SOFTWARE\Policies\Microsoft\Power\PowerSettings\
  {0e796bdb-100d-47d6-a2d5-f7d2daa51f51}` → `ACSettingIndex`/`DCSettingIndex`), which
  survives vendor software switching power plans — common on handhelds,
- the same value on **every** power plan on the machine via `powercfg` (not just the
  active one — Handheld Companion and similar tools switch plans constantly, and this
  setting is stored per plan), and
- `HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization` → `NoLockScreen` = 1,
  which removes the lock screen UI itself. **Windows 11 Home ignores this policy on
  some builds** — the wake behaviour above is the part that reliably matters.

On modern-standby devices `CONSOLELOCK` is hidden from the classic power UI but still
applies. Anyone who picks the device up can use it without signing in — fine for a
personal handheld, not for a shared or easily-lost machine. One administrator
confirmation to change; unticking restores all three previous values exactly.

## Notes & limitations

- Custom shells are a legacy but functional Windows mechanism; it is not officially
  supported by Microsoft on consumer SKUs. After a Windows feature update, verify the
  shell registration survived (OpenFSE's settings shows the status).
- True exclusive-fullscreen games can cover the edge-swipe strips; use the hotkey then.
- The overlay/hotkey cannot appear over the lock screen or UAC secure desktop (by design).
- Without explorer there are no taskbar, toasts, or system tray. OpenFSE shows its own
  errors in the overlay, and the settings window has a button to open the touch keyboard.

## License

MIT. Controller button glyphs from CC0 prompt packs (see `src/OpenFSE/Assets/Glyphs/CREDITS.md`).
