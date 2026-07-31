# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

WSGM ("Windows Steam Game Mode", formerly OpenFSE) reconstructs SteamOS Game Mode on Windows 11 gaming handhelds. It registers itself as the **per-user Windows shell** (`HKCU\...\Winlogon\Shell`), boots straight into Steam Big Picture, and provides a controller/touch quick-access overlay. It is **Steam-exclusive by design decision** — do not add multi-launcher support back; Steam is auto-detected from the registry (`Core\Steam.cs`), never configured by path.

## Commands

```powershell
dotnet build src\WSGM\WSGM.csproj          # build (output is localized German: "0 Fehler" = success)
.\build.ps1                                 # NativeAOT publish + Inno Setup installer → publish\WSGM-Setup-*.exe
                                            # (needs .NET 9 SDK, VS C++ build tools, Inno Setup 6)
src\WSGM\bin\...\WSGM.exe --settings        # safe to run locally: settings window only
src\WSGM\bin\...\WSGM.exe --overlay-test    # safe to run locally: overlay + activation surfaces, no apps started
```

There are no tests. **Never run `--shell` (or no-args when shell-registered) on a dev machine** — it kills explorer and takes over the session. `--restore-shell` is the recovery path and must stay bulletproof (it runs before logging/Avalonia init).

## Dev environment reality

- **No controller hardware locally.** Real testing happens on a user's MSI Claw via pasted logs from `%LOCALAPPDATA%\WSGM\wsgm.log`. Every input/focus feature must log enough to be diagnosed remotely (`Gamepad added:`, `Controller input:`, `Gamepad nav:`, `Steam Input pinned/released`, `Explorer is running unelevated/ELEVATED`). Preserve and extend these lines; they are the only test harness.
- NativeAOT (`PublishAot=true`, `BuiltInComInteropSupport=false`): P/Invoke via `LibraryImport` with blittable types only, **no COM interop**, no reflection-dependent packages. `ppy.SDL3-CS` is used precisely because it is plain-DllImport. AOT may be dropped if ever truly necessary (user-approved), but so far never needed.

## Architecture

**Process modes** (`Program.DecideMode`): `--shell` / `--settings` / `--overlay-test`, auto mode = shell iff registered as shell and no desktop alive. Shell mode: single-instance mutex `Local\WSGM.Shell` (held only in shell mode — the installer keys off it), crash-loop breaker (3 shell starts in 2 min → restore previous shell), `Panic()` restores the shell registration *before* starting explorer so Winlogon's AutoRestartShell can't resurrect WSGM next to it.

**Shell session** (`Shell\ShellSession`): launches startup apps (staggered, optionally elevated — this is the only thing self-elevation exists for), then Steam Big Picture, watches `config.json` (FileSystemWatcher, 500 ms debounce → `OverlayController.ApplyConfig`; runtime state must live on controllers, not in `_config`, because reloads replace it wholesale). `Shell\SteamMonitor` polls `steam;steamwebhelper` every 5 s; its `Paused` flag is how desktop mode and "Close Steam" suppress auto-relaunch/overlay-pop reactions.

**Input stack** (`Input\`): `SdlGamepads` is the process-wide SDL3 owner (single event pump — two `GamepadService` instances exist when Settings is open; per-instance pumps would steal hotplug events). UI-thread 16 ms `DispatcherTimer` poll → edge-triggered `ButtonPressed` (+ direction auto-repeat) and full-state `StateChanged` (chords) → `GamepadNavigation` (focus movement through tab order, synthesized Enter to activate, arrow-key mirror with 100 ms dedupe, skips TextBoxes so the touch keyboard doesn't pop) and `GamepadChordWatcher`. `Overlay\TouchSwipeMonitor` observes the raw HID digitizer (`RIDEV_INPUTSINK`, observation only) for edge swipes *and* tap-outside-overlay dismissal.

**Steam integration** (`Core\Steam.cs`, `Core\SteamInputPin.cs`, `Overlay\OverlayController.cs`): everything is protocol URLs — start/focus = `steam://open/bigpicture` (boots Steam if needed, UIPI-proof), leave BP = `steam://close/bigpicture`, quit = `steam://exit`. Desktop mode = pause monitor + close BP + start explorer (de-elevated if WSGM is elevated — `Core\UnelevatedLauncher.cs` via scheduled task); game mode reverses it.

## Device-verified invariants — do not regress these

1. **Steam Input's desktop profile swallows the controller from every API** (XInput/DInput/HID, system-wide) the moment it activates. The **only** reason the overlay may take focus (Game-Bar-style, which mutes the game while the panel is open) is the **Steam Input pin**: `steam://forceinputappid/480` forces a stock gamepad layout everywhere. `SteamInputPin` must stay applied whenever Steam runs, and `/0` release must fire on **every** exit/recovery path (normal shutdown, Panic, crash-loop, `--restore-shell`, `--unregister-shell` — recovery paths fire unconditionally because a fresh process can't know a crashed shell pinned). The pin lives inside Steam and survives our crashes.
2. **Never intercept mouse or keyboard globally** — raw-input *observation* only (TouchSwipeMonitor pattern). The low-level keyboard hook in `KeyRecorder` exists only during explicit shortcut recording.
3. **Avalonia touch promotion bug** (root-caused in Avalonia source): Avalonia never marks touch raw events handled, so `WM_POINTER` reaches `DefWindowProc`, which synthesizes a delayed mouse click. Hence: `OverlayController.CloseOverlay` defers the actual `Close()` by 150 ms, and `OverlayWindow`'s WndProc hook eats `MI_WP_SIGNATURE`-tagged (touch-synthesized) mouse messages. Removing either brings back ghost clicks that press buttons in whatever sits under the panel.
4. **Avalonia's 3-arg `DispatcherTimer(interval, priority, callback)` ctor auto-starts the timer.** This once made `IsRunning` permanently true and silently broke every "start if not running" guard. Use the parameterless ctor + `Tick +=` + explicit `Start()` when `IsEnabled` is consulted.
5. **De-elevation:** the naive `TokenLinkedToken` → primary-token route fails (error 1346, needs `SeTcbPrivilege`); the working mechanism is a one-shot scheduled task (`InteractiveToken`, no RunLevel, task XML **must be UTF-16**, never ship `/NoUACCheck` — EDRs flag it). Win11 explorer usually de-elevates itself; `ExplorerControl` verifies 5 s after start and repairs once via the task.
6. **Dismissing the overlay must not refocus anything** — Next-app cycling depends on the switched-to window staying foreground. Tap-outside dismissal is raw-observation hit-testing, deliberately not dismiss-on-deactivate (cycling deactivates the panel while it must stay open).

## Gotchas

- The installer (`installer\WSGM.iss` `[Code]` section) kills a running WSGM before updating and restarts it in its previous mode (shell-mutex check → `--shell`, else settings). Whether Winlogon resurrects the killed shell mid-copy is still unverified on device; the double-taskkill guard exists for that.
- Elevated processes started by WSGM inherit elevation (UIPI is why self-elevation exists at all); an **elevated explorer breaks UWP** (touch keyboard, store apps) — that's what invariant 5 protects.
- Config lives at `%LOCALAPPDATA%\WSGM\config.json` (`Core\ConfigStore`, System.Text.Json source-gen — new scalar props need no context changes). Registry snapshots inside it (previous shell/UAC/lock-screen values) belong to the install lifecycle; never clobber them from feature code.
