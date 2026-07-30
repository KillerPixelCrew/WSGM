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

1. Copy `OpenFSE.exe` to a **stable path** (e.g. `C:\Program Files\OpenFSE\`). Do not run
   it from Downloads and then move it — the shell registration points at the absolute path.
2. Run it → Settings opens. Configure your home app and startup apps.
3. Click **Install as shell**, sign out, sign back in.

Uninstall: run OpenFSE (settings open when a desktop is present) → **Uninstall**, or
`OpenFSE.exe --restore-shell` from anywhere.

## Command line

| Flag | Effect |
|---|---|
| *(none)* | Auto: shell mode if registered as shell and no desktop is running, else settings |
| `--shell` | Force shell mode |
| `--settings` | Force settings window |
| `--restore-shell` | Restore previous shell registration, start explorer, exit (recovery; needs no working GUI) |
| `--setup` | Headless install as shell (for scripts) |
| `--overlay-test` | Show the overlay without shell mode (development/testing) |

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
