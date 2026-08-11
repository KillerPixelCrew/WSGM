# AGENTS.md

Source of truth for every coding agent working in this repository (Claude Code, Codex, others).
This file is tracked so review tooling and contributors' agents can read it; CLAUDE.md stays
untracked and nothing in the repo, the README, or the wiki may reference it.

**Keep this file current at every implementation step.** When code, architecture, build tooling,
dependencies, device findings, or operating constraints change, update the relevant project-specific
guidance here in the same change before proceeding.

## Contributor instructions (PRs)

**PRs that ignore the conventions and architecture in this file will be refused.** If you are an
agent preparing a contribution, treat everything below as binding, not advisory:

- **Follow the documented architecture.** Respect the module ownership boundaries (`Core\`,
  `Shell\`, `Overlay\`, `Input\`, `Settings\`, `Interop\` — put code in the narrowest applicable
  module), the NativeAOT constraints (no COM interop, `LibraryImport` with blittable signatures,
  source-generated JSON, no reflection-dependent packages), and the established idioms of the file
  you are editing.
- **Do not "fix" device-verified mechanisms.** Sections marked device-verified or live-verified
  (the Steam CEF integration, injected JS, boot/takeover sequencing, input handling) encode
  behavior that only reveals its constraints on real hardware or against a live Steam client.
  Changing them without re-verification — even when the change looks like an obvious cleanup or
  hardening — is grounds for refusal on its own.
- **Match the code conventions**: existing naming, comment density, XML docs on public production
  APIs, and the formatting gates (`./eng/verify.ps1` must pass — it runs Prettier over the whole
  repo including `.github\` and Markdown, plus C# lint/format, build, and tests).
- **Fill out the PR template honestly**, including what hardware the change was tested on;
  "compiles" is not "works" in this codebase.

## What this is

WSGM ("Windows Steam Game Mode", formerly OpenFSE) reconstructs SteamOS Game Mode on Windows 11
gaming handhelds. **Explorer stays the registered Windows shell.** A SYSTEM logon service
(`src\WSGM.LogonService`, `WSGMLogonService`) launches WSGM's boot splash at sign-in to cover the
booting desktop; WSGM waits until Explorer finishes its logon prep (that one-per-session init is
what keeps touch features alive in game mode — device-verified), ends Explorer via its own orderly
Exit-Explorer path, and boots into Steam Big Picture with a controller/touch quick-access overlay. It
is **Steam-exclusive by design decision** — do not add multi-launcher support back; Steam is
auto-detected from the registry (`Core\Steam.cs`), never configured by path.

## Commands

```powershell
dotnet build src\WSGM\WSGM.csproj          # build (output is localized German: "0 Fehler" = success)
./eng/verify.ps1                             # Prettier + C# lint/format + Release build + unit tests + coverage
./eng/verify.ps1 -Fix                        # apply Prettier and C# lint/format fixes, then validate
.\build.ps1                                 # NativeAOT publish + Inno Setup installer → publish\WSGM-Setup-*.exe
                                            # (needs .NET 10 SDK, VS C++ build tools, Inno Setup 6)
src\WSGM\bin\...\WSGM.exe --settings        # safe to run locally: settings window only
src\WSGM\bin\...\WSGM.exe --overlay-test    # safe to run locally: overlay + activation surfaces, no apps started
```

## The Steam Input Lease library (`native\SteamInput`)

The Rust library that blocks Steam Input lives **in this repo** at `native\SteamInput` (Rust
workspace + C ABI + .NET binding). It is not a separate repository and WSGM is its only consumer, so
breaking its API is fine — change all layers together (Rust → `include\steam_input_lease.h` →
`bindings\SteamInterop.Net\` → `src\WSGM\SteamInterop\` → callers) and bump `sil_abi_version()`.

**It is built from source on every build.** `eng\build-steam-input-lease.ps1` compiles the workspace
and stages `steam_input_gate.dll`, `steam_input_lease_ffi.dll`, `steam-input-lease.exe`, and the two
license files into `src\WSGM\Native\SteamInputLease\`, which `WSGM.csproj` copies beside the AOT
executable and the installer ships. `build.ps1` calls it first; `eng\verify.ps1` calls it with
`-Validate`, which adds the library's own gates (`cargo clippy -- -D warnings`, `cargo test`). CI
therefore needs a Rust toolchain — it adds the clippy component and caches `target\`.

That staging directory is **generated and gitignored**; `native\SteamInput` is the tracked source.
Never hand-copy binaries into it. A Rust toolchain is now required to build WSGM at all.

`src\WSGM\SteamInterop\*.cs` are copies of `bindings\SteamInterop.Net\*.cs` **plus explicit
`using` directives** (WSGM does not enable `ImplicitUsings`) — diff, don't blind-copy. The Rust code
is deliberately not `cargo fmt` clean and has no fmt gate; do not reformat untouched code. Both
`native\SteamInput\` and the staging directory are in `.prettierignore` (the latter because
regenerating it would otherwise fail the next format check).

`steam-input-lease.exe` is also user-facing: Quick Access copies
`"...\steam-input-lease.exe" -- %command%` as a Steam launch option (the `--` is mandatory), the
Steam-Input twin of the de-elevation command.

## The radio helper library (`native\Radio`)

Wi-Fi, Bluetooth, pairing, and touch-keyboard support live in this in-repo Rust workspace. WSGM's
NativeAOT executable has managed COM interop disabled, so `WSGM.Radio.dll` owns the WinRT and Win32
calls behind a flat C ABI. `eng\build-radio.ps1` builds it from source on every verification and
release build, staging `WSGM.Radio.dll` and the user-facing `WSGM.RadioProbe.exe` in
`src\WSGM\Native\Radio\`; that directory is generated and must never be hand-populated. The probe
is the device diagnostic for shell-less/elevated radio control and the Wi-Fi location-consent gate.

## Required build handoff

For every completed implementation task on this machine, always run `./build.ps1` before handing it
off. After a successful build, copy the freshly produced `publish\WSGM-Setup-*.exe` installer to
`Z:\`. Use PowerShell to select the newest matching installer and overwrite the matching artifact
on `Z:\`:

```powershell
$setup = Get-ChildItem -LiteralPath .\publish -Filter 'WSGM-Setup-*.exe' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $setup) { throw 'WSGM installer was not produced.' }
Copy-Item -LiteralPath $setup.FullName -Destination 'Z:\' -Force
```

Automated tests live in `tests\WSGM.Tests` and run through `dotnet test WSGM.slnx`. They cover
pure/stateful logic, source-generated config serialization, and isolated per-test HKCU snapshot
round trips. The CI workflow also collects Cobertura and LCOV coverage under `TestResults`. **Never
run `--shell` or `--boot` on a dev machine** — both end explorer and take over the session; never
run `WSGM.LogonService.exe --install` locally either. `--restore-shell` is the recovery path
(restores any legacy shell registration, disarms the service boot, starts explorer) and must stay
bulletproof (it runs before logging/Avalonia init).

All public production APIs require meaningful XML documentation (`CS1573`/`CS1591` stay enabled and
the Release verification build treats warnings as errors). Test method names are the executable
specification and are exempt from that API-documentation rule. Do not use coverage percentage as a
reason to automate the device-only flows listed below; add isolated unit tests around their pure
state/serialization/decision logic and retain the manual device-verification boundary.

The live shell, Steam protocol, device input, display-DPI, explorer, UAC, and lock-screen flows
require the safe manual modes (`--settings` and `--overlay-test`) plus the device-verification
process below; they must never be triggered by unattended tests.

## Dev environment reality

- **Get hands dirty before theorizing (hard lesson, user-mandated).** When a live Steam is reachable
  over the CEF port, PROTOTYPE AGAINST IT immediately — don't write long feasibility essays or hedge
  about fragility from the armchair. The injected library-tabs work looked "too fragile / needs the
  React module registry we don't have," until a few live `Runtime.evaluate` probes proved the
  registry (`webpackChunksteamui`), React, and a working tab injection in minutes. Reality is cheaper
  to query than to reason about: run the probe, inject the script, watch the screen. Estimate cost by
  doing, not by imagining. (`tools/WsgmLibTest/` — `cdp-eval.mjs raw`, `run-file.mjs <file>` — is the
  live probe harness; Steam BPM on the dev box is a CEF test rig even though WSGM itself never runs
  there.)
- **No controller hardware locally.** Real testing happens on a user's MSI Claw via pasted logs from
  `%LOCALAPPDATA%\WSGM\wsgm.log`. Every input/focus feature must log enough to be diagnosed remotely
  (`Gamepad added:`, `Controller input:`, `Gamepad nav:`, `Steam Input lease acquired/released`,
  `Explorer is running unelevated/ELEVATED`). Preserve and extend these lines; they are the only
  test harness.
- NativeAOT (`PublishAot=true`, `BuiltInComInteropSupport=false`): P/Invoke via `LibraryImport` with
  blittable types only, **no COM interop**, no reflection-dependent packages. `ppy.SDL3-CS` is used
  precisely because it is plain-DllImport. The Rust radio helper and the native volume helper own
  the WinRT/COM calls behind flat C ABIs. AOT may be dropped if ever truly necessary (user-approved),
  but so far never needed.

## Architecture

**Process modes** (`Program.DecideMode`): `--shell` / `--boot` (service-launched takeover) /
`--settings` / `--overlay-test`; the legacy auto mode (shell iff registered as shell and no desktop
alive) survives only for the migration window — new installs never register as shell, so no-args =
settings. Shell mode: single-instance mutex `Local\WSGM.Shell` (held only in shell mode — the
installer keys off it), crash-loop breaker (3 shell starts in 2 min → **disarm the service boot**:
boot.json `GameModeBoot=false` + config flag off + legacy shell unregister + explorer if none),
`Panic()` = legacy shell unregister (self-guarding no-op on service installs), destroy tray host,
start explorer if none is running. The logon service's watchdog is the robust outer recovery layer;
Panic is in-process best effort.

**Logon service + boot flow** (`src\WSGM.LogonService`, `Core\BootManifest.cs`,
`Core\BootManifestWriter.cs`, `Shell\ExplorerReadiness.cs`): the SYSTEM service (raw SCM +
`SERVICE_ACCEPT_SESSIONCHANGE`, NativeAOT, no ServiceBase) reacts to `WTS_SESSION_LOGON` only (not
console connect — fast-user-switch keeps what runs), reads the per-user
`%LOCALAPPDATA%\WSGM\boot.json` **boot manifest** (projected from config.json by WSGM on `--setup`,
settings saves, and every shell start; the service treats it as untrusted and only ever launches
the named exe AS THAT USER), and launches `WSGM.exe --boot` via `CreateProcessAsUserW` — with the
user's elevated **linked token** when the manifest says so (legal under the service's
SeTcbPrivilege; no UAC prompt at logon). A startup catch-up sweep covers autologons that beat the
auto-start service (fresh = logged on < 60 s). The service watchdog holds the launched pid: dirty
exit + active session + no explorer → start explorer with the UNLINKED token (explorer must stay
unelevated), once per logon, never relaunching WSGM. Service log:
`%ProgramData%\WSGM\wsgm-service.log` (SYSTEM must not write user dirs); WSGM's own
`Run mode: Shell (service boot, elevated=…, session N)` line keeps wsgm.log the primary surface.
**The service fires BEFORE Winlogon starts explorer** (device-verified 2026-08-07), so `--boot`
runs the takeover unconditionally — never gate it on `IsRunningInSession()` at start (that exact
gate once left explorer alive behind Big Picture next to WSGM's tray host); the readiness poll is
what waits for explorer to appear at all.
The `--boot` takeover (`ShellSession.StartBootTakeover`): splash FIRST (covers the booting desktop;
re-covers itself on display change because posture applies later) → **input-desktop barrier**
(`Core\InputDesktop` polls `OpenInputDesktop` for winsta0\Default — WTS_SESSION_LOGON fires while
LogonUI still owns the screen, and WTS_SESSION_DESKTOP_READY is never delivered on the Claw; without
this gate Steam audio leaks behind the Welcome screen) → `ExplorerReadiness` — `GetShellWindow()` +
explorer's `Shell_TrayWnd`, then `ExplorerLogonSettleMs` settle (default 5000 ms), 60 s hard cap,
and **invariant-7 acceleration** (BP window appears under the opaque cover → take over immediately)
→ `ExplorerControl.ExitExplorerAndWait(5 s)` → posture → TrayHost → startup apps (skipping ones
explorer's autostart already launched) → Steam, strictly AFTER explorer is gone.

**How Explorer is ended — device-settled, do not change the mechanism:** `ExitExplorerAndWait`
posts `0x5B4` (WM_USER+436, explorer's own Ctrl+Shift-taskbar "Exit Explorer" command) to
explorer's pid-verified `Shell_TrayWnd`. That intentional shutdown is the ONLY way Winlogon's
AutoRestartShell does not respawn the shell. PID-snapshot semantics: any explorer pid not in the
initial snapshot is a Winlogon replacement → cancel and **fail open** (preserve desktop mode, warn
`Couldn't exit Windows Explorer safely`); a replacement is NEVER killed (fighting AutoRestartShell
loops). Lingering snapshotted pids are terminated only after explorer destroyed its taskbar (a
shell extension can hold the process open — device-observed) **and only after a `LingerGrace` (2 s)
window in which the remnant is given the chance to leave on its own** — killing it mid-shutdown is
itself what Winlogon respawns (device-observed 2026-08-08 as "game mode needs two tries"; a clean
run had the remnant exit ~830 ms after the taskbar went). Success requires 500 ms of stable
absence. Two mechanisms are device-DISPROVEN (2026-08-07): plain `Process.Kill` (Winlogon
respawns) and Restart Manager `RmShutdown` (wedged a freshly logged-on explorer ~30 s, error 351,
then respawn). The full working-era implementation is preserved in the Codex transcript
`~\.codex\sessions\2026\08\06\rollout-2026-08-06T23-57-41-*.jsonl` (L567/L1167).

**Shell session** (`Shell\ShellSession`): launches startup apps (optional `StartupDelayMs` wait
before the first one — the "First app delay" setting — then staggered, optionally elevated), then
Steam Big Picture. WSGM self-elevates when a startup app or Steam requires matching integrity, and
watches `config.json`
(FileSystemWatcher, 500 ms debounce → `OverlayController.ApplyConfig`; runtime state must live on
controllers, not in `_config`, because reloads replace it wholesale). `Shell\SteamMonitor` polls
`steam;steamwebhelper` every 5 s; its `Paused` flag is how desktop mode and "Close Steam" suppress
auto-relaunch/overlay-pop reactions.

**Quick access panel** (`Overlay\OverlayWindow`): a `TabStrip` over three always-alive panels —
Session / Tools / Power — LB/RB cycling with wrap (via `GamepadNavigation`'s optional
`tabPrevious`/`tabNext`), reopening on Session, focus landing on the first row after a switch, and
the warning `InfoBar` staying panel-level above the tabs. `DefaultFocusTarget` resolves to the ACTIVE
tab's first row (HomeAppButton is invisible on the other tabs). Note the taskbar's navigation
deliberately passes NO tab callbacks: during the 150 ms surface handover both navigations are alive,
so routing LB/RB there would double-advance the panel's tabs.

**Input stack** (`Input\`): `SdlGamepads` is the process-wide SDL3 owner (single event pump — two
`GamepadService` instances exist when Settings is open; per-instance pumps would steal hotplug
events). UI-thread 16 ms `DispatcherTimer` poll → edge-triggered `ButtonPressed` (+ direction
auto-repeat) and full-state `StateChanged` (chords) → `GamepadNavigation` (focus movement through
tab order, synthesized Enter to activate, arrow-key mirror with 100 ms dedupe, skips TextBoxes so
the touch keyboard doesn't pop) and `GamepadChordWatcher`. `Overlay\TouchSwipeMonitor` observes the
raw HID digitizer (`RIDEV_INPUTSINK`, observation only) for edge swipes _and_ tap-outside-overlay
dismissal.

**Steam integration** (`Core\Steam.cs`, `Core\SteamInputBlocker.cs`, `Shell\SessionModes.cs`,
`Overlay\OverlayController.cs`): everything is protocol URLs — start/focus =
`steam://open/bigpicture` (boots Steam if needed, UIPI-proof), leave BP =
`steam://close/bigpicture`, quit = `steam://exit`. Desktop mode = pause monitor + close BP + start
explorer (de-elevated if WSGM is elevated — `Core\UnelevatedLauncher.cs` via scheduled task); game
mode reverses it via `ExplorerControl.ExitExplorerAndWait` run OFF the UI thread (a synchronous
exit froze the overlay for its full duration) with the transition completing on the UI thread;
`SessionModes.TransitionInProgress` serializes transitions (the overlay ignores mode clicks while
one runs) and failure keeps desktop mode (fail open, never a half game mode). The game/desktop mode transitions and the shared Steam start+warning flow live in
`Shell\SessionModes` (session coordinator, used by both `ShellSession` boot and the overlay's
buttons); `OverlayController` stays the UI owner (lease lifecycle, overlay window) and surfaces
`SessionModes.SteamStartFailed` warnings.

## Device-verified invariants — do not regress these

1. **Steam Input's desktop profile swallows the controller from every API** (XInput/DInput/HID,
   system-wide) the moment it activates. The **only** reason the overlay may take focus
   (Game-Bar-style, which mutes the game while the panel is open) is the **Steam Input Lease**:
   its injected gate blocks controller access inside `steam.exe`, leaving SDL direct access for
   WSGM without changing Steam's active layout. The lease is **scoped to the overlay/taskbar
   lifetime** — acquired before each focused surface opens and released after the last one closes.
   It is an open named-pipe connection, so Windows releases it after a WSGM crash; normal release
   requests Steam controller rediscovery. Keep the native `steam_input_gate.dll` and
   `steam_input_lease_ffi.dll` beside WSGM.exe, and preserve the `Steam Input lease
   acquired/released` logs for device diagnosis.
2. **Never intercept mouse or keyboard globally** — raw-input _observation_ only (TouchSwipeMonitor
   pattern). The low-level keyboard hook in `KeyRecorder` exists only during explicit shortcut
   recording.
3. **Avalonia touch promotion bug** (root-caused in Avalonia source): Avalonia never marks touch raw
   events handled, so `WM_POINTER` reaches `DefWindowProc`, which synthesizes a delayed mouse click.
   Hence: `OverlayController.CloseOverlay` defers the actual `Close()` by 150 ms, and
   `OverlayWindow`'s WndProc hook eats `MI_WP_SIGNATURE`-tagged (touch-synthesized) mouse messages.
   Removing either brings back ghost clicks that press buttons in whatever sits under the panel.
4. **Avalonia's 3-arg `DispatcherTimer(interval, priority, callback)` ctor auto-starts the timer.**
   This once made `IsRunning` permanently true and silently broke every "start if not running"
   guard. Use the parameterless ctor + `Tick +=` + explicit `Start()` when `IsEnabled` is consulted.
5. **De-elevation:** the naive `TokenLinkedToken` → primary-token route fails (error 1346, needs
   `SeTcbPrivilege`); the working mechanism is a one-shot scheduled task (`InteractiveToken`, no
   RunLevel, task XML **must be UTF-16**, never ship `/NoUACCheck` — EDRs flag it). Win11 explorer
   usually de-elevates itself; `ExplorerControl` verifies 5 s after start and repairs once via the
   task. Modern Settings activation uses this same task to run a narrow WSGM one-shot at medium
   integrity before opening `ms-settings:`. The shell is normally elevated, and relying on
   `ShellExecute` directly only works while unelevated Explorer happens to broker the request;
   never start Explorer just to open Bluetooth or Wi-Fi Settings.
   `WSGM.Deelevate.exe` is the user-facing extension of the same mechanism for Steam games that
   reject elevation. Its copied launch option is `"...\WSGM.Deelevate.exe" %command%`; the elevated
   wrapper must remain alive for the target lifetime, preserve Steam's arguments/environment/CWD,
   and stop the target tree if Steam terminates the wrapper. Do not replace it with a fire-and-forget
   scheduled task or an Explorer-token shortcut. **Four device-verified invariants make it actually
   work when Steam is elevated (each was a separate real failure, 2026-08-12):** (a) it MUST be a
   **console** subsystem exe (`<OutputType>Exe</OutputType>`, shows a CLI window) — a windowless
   `WinExe` is treated by Steam as a game and gets Steam Input hooked into it, dying before it logs;
   (b) the elevated parent's IPC pipe MUST grant the **User SID** explicitly (`NamedPipeServerStreamAcl`
   + `WindowsIdentity.User`), NOT `PipeOptions.CurrentUserOnly` — an elevated server's CurrentUserOnly
   grants the token OWNER = `BUILTIN\Administrators`, deny-only in the child's filtered token, so the
   medium child's connect fails "Access is denied"; (c) the medium child launches the game with
   `__COMPAT_LAYER=RunAsInvoker` in its environment, or a target with a RUNASADMIN flag / admin manifest
   fails a medium `CreateProcess` with `ERROR_ELEVATION_REQUIRED` (740); (d) for a **non-Steam (custom)
   shortcut** Steam ignores an exe-replacement `%command%` launch option and runs the original target
   anyway — the wrapper goes in the shortcut's **Target**, the real program in **Launch Arguments**.
   Never reintroduce `CurrentUserOnly` on an elevated↔medium pipe, and never make the wrapper WinExe.
6. **Overlay dismissal refocuses only under strict gates** (intentional since b7234f8): on close,
   the overlay calls back the window that was foreground when it opened (`_restoreFocusTo`, captured
   in ShowOverlay) — exclusive-fullscreen games sit minimized after the panel took focus. The
   refocus fires **only** when no overlay action redirected focus (every focus-redirecting action,
   including Next-app cycling via `PickWindow`, sets `_suppressFocusRestore`) **and** only in game
   mode (no explorer in the session). That suppression is load-bearing for Next-app cycling, which
   depends on the switched-to window staying foreground. Tap-outside dismissal is raw-observation
   hit-testing, deliberately not dismiss-on-deactivate (cycling deactivates the panel while it must
   stay open).
7. **Big Picture's UI (steamwebhelper/CEF) suspends rendering while fully occluded** — a BP intro
   video that initializes under an opaque fullscreen cover stays black even after the cover leaves
   (same behavior BP shows under a game). The boot splash therefore begins its fade **immediately**
   on BP-window detection (the first fade tick drops the layered alpha below 255, which lifts the
   occlusion) with a tight 250 ms detection poll; never hold an opaque cover over a live BP window.
   Additionally, a `steam://open/bigpicture` re-activation while the intro plays kills the video
   (the removed splash→BP "focus handoff") — after the splash closes, do not touch Steam; it takes
   the foreground itself. A no-activate splash was tried and did not affect the symptom.
8. **Adding a library to a RUNNING Steam goes through Steam's own front-end, never its internals.**
   `Core\SteamCef.cs` drives Steam's CEF remote-debugging port (localhost:8080) → WebSocket
   `Runtime.evaluate` → `SteamClient.InstallFolder.AddInstallFolder("<path>")`, so Steam adds,
   persists, mounts and scans on its own thread with no restart. The port only opens when Steam
   starts with the `<SteamDir>\.cef-enable-remote-debugging` flag present, so
   `SteamCef.EnsureRemoteDebuggingEnabled()` writes it before `Steam.LaunchBigPicture` cold-starts
   Steam (game mode always has the port).
   **Security posture of the CEF port (accepted, reviewed — do not "fix" without reading this).**
   The port is unauthenticated (Steam's CEF has no auth — a platform limitation, not ours) but
   **loopback-only** (`127.0.0.1`), and driving Steam's front-end is the only way to build the
   live-add / library-tab / artwork features; every comparable tool (CSSLoader-Desktop, Millennium,
   Decky-on-Windows) uses the same flag and port. WSGM's own hardening against a **local squatter**:
   `SteamCef.IsSteamPortOwner()` refuses port 8080 unless the listening PID is `steamwebhelper`/`steam`
   (native TCP table, loopback listener preferred over a wildcard one), and the returned
   `webSocketDebuggerUrl` is rejected unless it is `ws`/`wss` + host `127.0.0.1`/`localhost` + port
   8080 — so a spoofed `/json/list` cannot redirect the CDP client (this is the answer to the Codex
   "unauthenticated DevTools" finding, which reviewed the pre-hardening commit `59fb357`; the checks
   landed in `4925494`). The residual is a loopback port any same-user process can drive — inherent,
   `medium`, not raised further. **Do NOT remove the `.cef-enable-remote-debugging` flag on uninstall
   (or anywhere):** it is shared Steam-wide state that CSSLoader-Desktop/Millennium also set and
   depend on, WSGM only writes it if absent and cannot know who created it, so deleting it would
   silently break a coexisting tool. This deletion was tried and deliberately reverted.
   **JSON-encode the path into the JS** (`JsonEncodedText`) —
   a raw path drops its backslashes and Steam rejects it as `NotWritableFolder`. Steam enforces one
   library per drive (`DriveAlreadyHasLibrary` = already present, not an error). Do NOT resurrect the
   in-process `CApplicationManager::AddLibraryFolder` call (removed from `steam_input_gate`): calling
   it from the injected thread clears+rebuilds the library array without Steam's lock and **destroys
   the library list** (device-verified: dropped D:/E:, persisted the loss to config). When Steam is
   closed (or the port is unreachable) `SdFormatManager` falls back to the
   `config\libraryfolders.vdf` splice, read on Steam's next start. Before a
   WSGM-format of a card that already has a library marker, WSGM reads that
   marker's `contentid`, removes the matching registered/live library first, and
   only then erases the disk; never identify the old library by its reused drive
   letter or path.
   `SteamCollections` remains only as the read/filter bridge and one-time cleanup for collection IDs
   created by pre-injection builds. New tabs never create collections. CEF unreachability must save
   the desired configuration but fail open with a retryable warning; it must not replace the last
   successfully injected definitions.
9. **Custom filter tabs are INJECTED into Steam's tab strip — not collections (device-verified).**
   Collections render under the "Collections" tab, never as top-strip tabs; that was the wrong model
   and is fully removed. `Core\SteamLibraryTabs.cs` injects a resident script into `SharedJSContext`
   that replicates TabMaster without Decky: push a chunk to **`window.webpackChunksteamui`** to
   capture `__webpack_require__`, iterate `req.m` to `findModule` React (module with
   `createElement`+`useMemo`+`version`) **loading each candidate via `req(id)` — the captured
   require's `req.c` cache is EMPTY (live-verified), so a cache-only exports scan can never find
   React; a review once made that "safer" swap and broke all tab injection until the next device
   test** — then **hijack the current dispatcher slot**
   `React.__CLIENT_INTERNALS_DO_NOT_USE_OR_WARN_USERS_THEY_CANNOT_UPGRADE.H` so every `useMemo` result
   is passed through `patchTabs`, which rewrites the library tab array (found by a tab with
   `id==='AllGames'`) to append WSGM tabs. Each tab is a **fake in-memory collection** (a plain object
   of app overviews) rendered by Steam's own grid (found by the `Library_FilteredByHeader` source
   marker) — no real Steam collection is ever created. WSGM only supplies
   `window.__wsgm.tabs = [{id,title,appids}]`, plus `tabOrder` (full strip order as tab keys —
   native ids like `AllGames` mixed with `wsgm-…` ids; unlisted tabs keep natural order after the
   listed ones) and `hiddenTabs` (native ids to omit — hiding IS omission from the returned array,
   exactly TabMaster's model, and the tab reappears untouched when unhidden). `patchTabs` also
   records `W.nativeTabs` (id+title of Steam's own tabs) which the sync persists into
   `AppConfig.KnownNativeTabs` so the order UI shows real localized titles; app-ids come from
   `Core\LibraryFilter.cs` (a persisted
   `FilterNode` tree → **pure JS predicate** over `appStore`, unit-tested in `LibraryFilterTests` —
   keep it Steam-free; SD-card membership is baked in from WSGM's own card model). Card tabs and genre
   tabs use the same injection. It is **reactive**: `LibraryTabManager.SyncAllAsync` re-injects after
   every builder change (no manual "sync" button); interactive reordering uses the cheap
   `SteamLibraryTabs.PushOrderAsync` (order + hidden set only, no filter re-evaluation), debounced
   from the Tab Order UI. The two things that shift on a major Steam UI
   update — the dispatcher slot name and the `Library_FilteredByHeader` marker — are the accepted
   fragility (kill switch `window.__wsgm.disableTabs()`; a Steam restart also recovers). The builder
   UI is `Overlay\LibraryTabsView.cs` (self-drawing sub-view like `PanelFormat`; extend `AnySubView`).
   Prototype any change against live Steam via `tools/WsgmLibTest` (`run-file.mjs tabs-prod.js`) BEFORE
   editing the C#.
10. **Steam-page bridge (the VISIBLE window, not SharedJSContext).** `Core\SteamPageBridge.cs` reads
   the current game and injects the "On: <card>" badge into the **visible** Big-Picture/library window
   (`SteamCef.EvaluateOnVisibleWindowAsync`) — SharedJSContext is HEADLESS (empty DOM, no images), it
   only holds the stores/React. The visible window is selected by shape, not localized title (a `page`
   whose url has `createflags` and lacks `openerid`/`browserviewpopup`). Current game = the appid of
   the **largest WIDE visible** `assets/<appid>/...` image (the hero banner) — device-verified robust
   across art naming (some games serve `library_hero`, others a hashed `assets/<id>/<hash>`; both put
   the appid in the path). Match by `width>=600 && width>height` so the portrait grid capsules are
   skipped and the badge CLEARS when leaving a game. NEVER match the `library_hero` filename alone —
   many games don't use it. The badge is a resident `MutationObserver` + fixed-position pill.
   Artwork apply (SteamGridDB feature) is the robust `SharedJSContext` API
   `SteamClient.Apps.Clear/SetCustomArtworkForApp(appid, base64, ext, assetType)` (grid=0/hero=1/
   logo=2/wide=3/icon=4; clear→~500ms→set; icons alone need FS writes) — data on SharedJSContext, DOM
   on the visible window, always.
   **Header Wi-Fi indicator (`Core\SteamNetworkIndicator.cs`, live-verified):** Big Picture's header
   Wi-Fi icon is empty on Windows because Steam's backend sends device reports with an empty
   `wireless.aps` list, so `SystemNetworkStore` (SharedJSContext) never sees a connected access
   point. WSGM injects a synthetic AP (real SSID + signal from `NativeRadio.WifiStatus`, polled by
   `Shell\NetworkIndicatorService.cs`) through the store's own `SetDeviceInfo` ingestion (plain
   protobuf-toObject shape; estate 5=Connected, estrength 0-4 = filled arcs). Residency: do NOT wrap
   `OnNetworkDevicesChanged` — the backend holds the bound callback registered at init and a property
   wrap never fires (verified); instead the synthetic AP instance gets a no-op `MarkAsNotPresent`,
   which pins it across the backend's periodic reports. Removal = delete the map entry +
   `SteamClient.System.Network.ForceRefresh()`; disabled on desktop transitions like tabs/badge.
   **CSSLoader-Desktop coexistence (device- + source-verified):** Steam's CEF allows concurrent CDP
   clients, and CSSLoader only appends/removes `<style>` in `document.head`. Namespace everything under
   `window.__wsgm`, give injected nodes a unique `wsgm-badge` class (never `css-loader-style`, which
   CSSLoader bulk-removes), never touch `document.head`, and never disable the debug flag or port.

**UI layer (rebuilt in 0.9.0 — read before touching any XAML).** All styling lives in `Themes\`
(`Palette.axaml` = the token set, `Typography.axaml`, `Shared.axaml`, plus `ControlThemes`/
`TabStripTheme`/`CardButtonTheme`); `App.axaml` is only includes. Rules: every colour comes from a
token — no hex literals in consumer XAML; the accent family (`HcAccentBrush`, `HcOnAccentBrush`,
`HcOnAccentCaptionBrush`) is consumed via **DynamicResource** because `Themes\AccentPalette.cs`
replaces it at runtime, everything else via StaticResource. One focus mechanism only:
`FocusAdorner={x:Null}` + a constant 2 px border that flips to the accent on `:focus` (Avalonia's
adorner is destroyed/rebuilt on every focus move and lost on activation blips). Shared controls in
`Controls\`: `TabStrip` (the LB/RB bumper tab bar used by BOTH the quick-access panel and Settings),
`CardButton`, `Icons` (stroke-style `StreamGeometry`; render them stroked with `Fill={x:Null}` —
filling collapses interior detail). `Core\RelayCommand.cs` is the hand-rolled AOT-safe ICommand.
Settings is `Settings\SettingsWindow` + five always-alive `Settings\Pages\*` UserControls toggled by
`IsVisible` (scroll positions survive switching), with recorder lifetime in
`Settings\ShortcutRecorders.cs`. **Layout floor: 1280x800 (Steam Deck), Settings min 1024x640** —
a page must fit without scrolling or it earns another tab.
_Gotcha:_ Avalonia's `Shape` scales a `Stretch=Uniform` geometry and then aligns it **top-left**
inside the element box (`CalculateSizeAndTransform` translates only by the geometry origin), so a
square box around a wide-and-short glyph parks it against the top. Give such a Path only its
dominant dimension and let the box hug the drawn content.

**Splash engine** (`Core\AppConfig.SplashConfig`, `Shell\SplashStyle/SplashPresets`,
`Core\SplashAssets/SplashTheme/ImageHeader`, `Shell\BootSplashWindow`): the splash is a pure
customization engine — text/caption with own colours+sizes, 12 spinner styles, background
colour/image/vignette, logo, per-element placement (anchor+padding, absolute X/Y, or attached to the
text block). Presets only prefill editable fields; **never key rendering off a preset**.
`.wsgmsplash` theme files are SHARED and therefore UNTRUSTED — the whole defense set must stay
intact: entry names must equal their own file name (a drive-relative `D:logo.png` is rooted, and
`Path.Combine` then discards the staging dir) plus a containment assert; per-entry and total
decompression caps enforced through a counted copy (central-directory sizes lie); image paths from
the JSON are ALWAYS replaced by what was actually extracted (a UNC path there makes Settings touch a
remote host when it thumbnails); `ImageHeader` gates declared pixel dimensions before any decode and
both logo and background decode under an output-area budget (byte caps bound only encoded size);
text/colour strings length-capped and every numeric field clamped in `ConfigStore.NormalizeSplash`,
the choke point BOTH config load and theme import pass through. Imports stage into a temp directory
owned (marker held `FileShare.None`) for the life of the Settings window, so a second window cannot
delete a first window's unsaved import. `SplashAssets` is a **two-phase transaction**: sidecars are
promoted only after `ConfigStore.Save` succeeds, a failed promotion reports a failed save and keeps
the previous path, and the picked path stays in the view model so a retry works.

**Test-harness rule (hard):** no test or throwaway probe may touch `%LOCALAPPDATA%\WSGM` — a probe
once destroyed the developer's real `config.json`. Use temp dirs and the seams: `SplashAssets`/
`SplashTheme` take explicit target directories, `SettingsViewModel` has an internal ctor taking an
`AppConfig`. Never call `ConfigStore.Save/Load` or the parameterless `SettingsViewModel` ctor from a
test. `Log` is uninitialized in tests (writes are no-ops) — keep it that way.

**Taskbar + tray host** (`Overlay\TaskbarWindow/TaskbarViewModel`, `Core\TrayProtocol.cs`,
`Shell\TrayHost.cs`, `Shell\SystemStatus.cs`): bottom-edge swipe in game mode opens a **full-width
three-zone bar** — left WSGM button (opens quick access through the existing handover), centre the
switchable windows (`WindowFinder.ListSwitchableWindows`) in a horizontally scrolling strip, right
the tray icons (also bounded/scrolling) plus Wi-Fi/Bluetooth buttons, battery and clock from
`SystemStatus`. Columns are `Auto,*,Auto` ON PURPOSE: the old `*,Auto,*` let a large tile count push
the home button and the whole status cluster off a 1280-wide screen. Tiles keep FIXED sizes at every
count. The buttons open the `RadioManager`-backed radio panel; they must never invoke `ms-settings:`
(the immersive shell cannot activate it without Explorer in the session). The right edge stays quick access, and `OverlayController` owns BOTH surfaces (shared Steam Input lease released only when
both are closed, mutual exclusion with restore-target handover, same 150 ms deferred close and
ghost-click WndProc hook). Tile refreshes reconcile IN PLACE — a wholesale rebuild would destroy the
focused button under the gamepad cursor. `TrayHost` registers a window class literally named
`Shell_TrayWnd` (that's how `Shell_NotifyIcon` finds a tray; game mode has no explorer, so without
it closed-to-tray apps lose their icons) and parses the WM_COPYDATA wire format in the pure,
unit-tested `TrayProtocol` (32-bit handle fields on every architecture). Two hard rules: (a) **never
coexist with explorer's taskbar** — the host is destroyed on `SessionModes.DesktopModeStarting`
(before `StartExplorer`) and recreated on `GameModeEntered`; (b) the **UIPI gate**: WSGM is usually
elevated, and unelevated apps' `Shell_NotifyIcon` WM_COPYDATA is silently dropped by UIPI unless
`ChangeWindowMessageFilterEx(WM_COPYDATA, MSGFLT_ALLOW)` is applied to the tray window — no shipped
replacement shell runs elevated, so this gate is WSGM-specific and its device verification status
must be tracked via the `Tray host created (… WM_COPYDATA filter …)` / `Tray icon Added/Rejected`
log lines.

## Gotchas

- The installer (`installer\WSGM.iss`) is now **`PrivilegesRequired=admin`** (the machine service
  demands it) while the app stays per-user — deliberate single-user-device design: `{localappdata}`
  and HKCU belong to the elevating account, documented in the .iss header. Update/uninstall
  ordering is load-bearing: `[Code]` stops the **logon service first** (`sc stop` — a live watchdog
  would see the killed WSGM and start explorer mid-update, flipping the restart into desktop mode;
  also frees the Program Files binary, including an abandoned preview's — same service name), then
  signals `Local\WSGM.ExitForUpdate` (one SetEvent releases every instance, including elevated
  ones), waits bounded on the shell mutex, taskkill fallback — and restarts WSGM in its previous
  mode (shell-mutex check taken _before_ killing → `--shell`, else settings). `[Run]` order:
  `--setup` (per-user files, migrate off any legacy shell registration, Xbox-FSE guard, boot
  manifest) then `WSGM.LogonService.exe --install` (create-or-reconfigure + failure actions +
  start). `[UninstallRun]` order: service `--uninstall` (stop+delete) → `--unregister-shell`
  (legacy no-op on service installs) → `--uninstall-restore`, all before files are deleted;
  `[UninstallDelete]` also removes `{autopf}\WSGM` and `{commonappdata}\WSGM`. Interactive upgrades
  return `True` from Inno's `NeedRestart`; silent upgrades must return `False`, because
  `/VERYSILENT` otherwise reboots automatically unless its caller supplied `/NORESTART`.
- The direct HKCU Winlogon shell replacement was **retired** (2026-08): running the session without
  Explorer ever initializing broke touch features, and the Explorer-first service boot is the
  device-verified fix. `ShellRegistration.Install` is legacy-only (no caller); `Uninstall`, the
  snapshot fields in config.json, auto mode, and `--unregister-shell` remain for migrating
  installed devices — do not remove them while shell-registered installs exist in the field, and do
  not re-register WSGM as the shell from any new code path.
- Elevated processes started by WSGM inherit elevation — that inheritance is the point of
  self-elevation: an elevated WSGM yields an elevated **Steam**, which is what lets Steam Input
  reach elevated windows and the Steam Overlay inject into elevated games (UIPI blocks both
  otherwise); WSGM's own overlay/edge swipes over elevated windows ride the same chain. The flip
  side: an **elevated explorer breaks UWP** (touch keyboard, store apps) — that's what invariant 5
  protects.
- **Accepted security posture (do not "fix" by contorting the design):** the whole class of
  same-user medium→high findings is accepted by design. WSGM is a per-user shell that deliberately
  self-elevates and runs user-configured inputs — the boot manifest (`boot.json`), live `config.json`
  reload / auto-relaunch, `SelfElevation` launching configured home/startup apps, the HKCU-sourced
  Steam path, the co-located helper DLL (`WSGM.VolumeControl.dll`) and the install→run handoff all
  live in `%LOCALAPPDATA%`, which same-user medium-IL code already owns. UAC is not a Microsoft
  security boundary, so a same-user attacker who can pre-plant those inputs is out of scope. Do NOT
  relocate the install, add per-action UAC prompts, code-sign-gate DLL loads, or otherwise trade the
  design/UX to close these — a reviewer re-raising them should be answered "accepted by design," not
  patched. What IS worth doing (and already done) is *transparent* hardening that costs nothing:
  absolute `System32` paths for launched system tools (`ConsoleTool.System32`), reparse-point/
  no-follow/no-overwrite checks before elevated file ops in user-writable dirs, DACL-scoping named
  kernel objects to the current user, and bounds/decode-size checks on parsed untrusted input.
- **Never manage Windows device posture or automatic touch-keyboard policy:** game/desktop mode must
  not capture or write `ConvertibleSlateMode` or `TouchKeyboardTapInvoke`. Windows owns both. The
  legacy config fields and `LegacyPostureCleanup.Restore` exist only to undo values changed by older
  builds; remove them only after that migration is no longer needed.
- **The volume OSD must never interrupt an exclusive game:** the physical volume command is always
  applied in game mode. The indicator is non-activating and click-through, and is suppressed only
  for `SHQueryUserNotificationState`'s confirmed `QUNS_RUNNING_D3D_FULL_SCREEN` (and absent/locked
  session). `QUNS_BUSY` must stay allowed: Steam Big Picture and borderless fullscreen report it.
- Config lives at `%LOCALAPPDATA%\WSGM\config.json` (`Core\ConfigStore`, System.Text.Json source-gen
  — new scalar props need no context changes). Registry snapshots inside it (previous
  shell/UAC/lock-screen values) belong to the install lifecycle; never clobber them from feature
  code. `ConfigStore.AcquireLock()` is the cross-process scope; `SaveMerged` holds it across the
  config write AND the splash-asset promotion, but the multi-megabyte image copies happen OUTSIDE it
  (sidecars are per-transaction unique, so staging cannot collide). Nested acquisition on the same
  thread is free — do not reintroduce stacked 2 s timeouts.
- The app targets .NET 10 and Avalonia 12.1.1. `LoadingIndicators.Avalonia` is vendored under
  `third_party\LoadingIndicators.Avalonia` and built from source because its published Avalonia 11
  package has precompiled XAML that fails on Avalonia 12; its Unlicense text ships from
  `src\WSGM\Licenses\`. `FluentAvaloniaUI` 3.0.2 and an explicit
  `Avalonia.Controls.ColorPicker` 12.1.1 pin keep the controls on the same Avalonia line.
- Version numbers are user-owned: never bump `<Version>` in `WSGM.csproj` on your OWN initiative
  (e.g. to disambiguate builds — use log content or timestamps for that). But a "tag the release"
  instruction IS the request to bump: set `<Version>` to that `vX.Y.Z` in the same commit BEFORE
  creating the tag, then tag. `release.yml` stamps the version from the tag on the CI runner only
  and never commits it back, so the tracked `<Version>` lags unless it is bumped here. `build.ps1`
  reads it and passes it to the installer, so that one line drives the local installer name and the
  app version.

# Implementation Architecture

Use this map when deciding where new code belongs. Keep direct dependencies narrow and communicate
across ownership boundaries through the named manager/coordinator; core/native layers must not depend
on Avalonia windows or controls.

| Area | Owns | May depend on | Must not own |
| --- | --- | --- | --- |
| `Program`, `App` | command-mode selection, recovery-first bootstrap, Avalonia lifetime | `Core`, `Shell`, `Settings` | feature policy or window behavior |
| `Core` | durable configuration, process/Steam/Explorer/elevation primitives, CEF bridge | `Interop`, BCL | Avalonia UI or session lifetime |
| `Shell` | game/desktop state machine and long-lived device/session managers | `Core`, `Interop`, `Overlay` facade | raw Win32 declarations or page-specific UI |
| `Overlay` | focused transient surfaces, focus restoration, activation handover | `Shell` coordinators, `Input`, `Controls` | Steam/Explorer transition implementation |
| `Input` | SDL ownership, gamepad navigation/chords, raw-input observation | SDL interop, Avalonia input primitives | global input interception or application policy |
| `Settings` | editing and committing user configuration | `Core`, shared controls/themes | live shell transition ownership |
| `Interop` | narrow Win32/native ABI boundary | BCL/native DLLs | application decisions or UI state |
| `Themes`, `Controls` | tokens, reusable presentation, AOT-safe commands | Avalonia | device/session/Steam policy |
| `WSGM.LogonService` | SYSTEM launch/watchdog boundary | shared boot manifest, Win32 | Avalonia or user-profile writes |
| `WSGM.Deelevate` | medium-integrity child-process lifetime | scheduled-task launcher | shell/session UI |
| `native\*` | OS APIs unavailable to NativeAOT WSGM | Rust/C++ and C ABI | managed business logic |

## Application lifetime and state flow

1. `Program.Main` handles recovery and one-shot commands before logging or Avalonia. It selects the
   mode, performs shell-only elevation/mutex/crash-loop protection, and guarantees lease recovery on
   normal and fatal exit.
2. `App.OnFrameworkInitializationCompleted` loads the initial configuration and creates exactly one
   root: Settings/Welcome for safe UI modes, or a resident `ShellSession` for shell/overlay-test.
3. `ShellSession` creates `SteamMonitor`, `SessionModes`, and `OverlayController` once. It is the
   composition root for the running session; event subscriptions and disposable process resources
   must be rooted there for their required lifetime.
4. `SessionModes` serializes game/desktop transitions and emits state events. `OverlayController`
   requests transitions and owns presentation; it must not duplicate transition policy.
5. `ConfigStore` loads a replaceable `AppConfig`. `ShellSession` debounces file changes and calls
   `OverlayController.ApplyConfig`; controllers retain their own runtime state because reload swaps
   the configuration object.
6. Native helpers stay behind `Interop`. Managers translate their errors into logged, recoverable
   feature state; a missing helper must never take down the shell.

## Concurrency, UI, and resource ownership

- Avalonia controls, windows, focus, and observable view state are UI-thread owned. Perform blocking
  Explorer, process, file, CEF, and device calls off-thread, then marshal only the result back to the
  dispatcher.
- `async void` is allowed only for framework event handlers. Library/manager operations return
  `Task`/`Task<T>` and must observe failures; do not use `.Wait()` or `.Result`.
- Long-lived callbacks require a field-rooted owner and explicit `Dispose`/unsubscribe path. This is
  mandatory for file watchers, timers, raw-input windows, gamepad services, tray hosts, and native
  callback handles.
- Use one named synchronization gate for each shared workflow (for example config save, library-tab
  synchronization, or session transition). Do not introduce nested timeouts around `ConfigStore`.
- A best-effort recovery path may catch exceptions only when it preserves a usable desktop/session;
  log contextual failures everywhere normal diagnosis is possible. Never silently swallow a normal
  feature failure.

## Repository code conventions

- `.editorconfig` is authoritative: UTF-8, CRLF, final newline, trimmed trailing whitespace, and
  four-space indentation for C#/PowerShell-style code; AXAML, XML, project, JSON, and workflow files
  use two spaces. Run `eng\verify.ps1` rather than hand-applying formatters with different settings.
- C# is file-scoped with explicit `using` directives in WSGM. Keep system usings first, nullable
  annotations meaningful, braces present, and use `var` only when the right-hand side makes the type
  obvious.
- Public production types and members need meaningful XML documentation. Keep methods small around
  a single policy/side effect, make pure decision helpers `internal` where tests need them, and give
  tests descriptive executable-specification names.
- Prefer records/readonly value types for immutable data and sealed classes for stateful managers.
  Model finite state with enums and pure decision functions instead of scattered boolean combinations.
- Use `Log.Info/Warn/Error` with the operation and device-relevant state. Do not add Console output,
  reflection-based logging, or a second logging subsystem.
- Put Win32 constants, handles, ownership rules, and native marshalling at the `Interop` boundary.
  Callers should receive managed values/results, not raw pointers or unchecked handles.
- Do not add packages that require runtime reflection, managed COM interop, or JIT-only behavior.
  Pin UI package versions deliberately; NativeAOT publish is the compatibility proof.

# Agent Review Rules

Use this section for every requested code review, pull-request review, changeset audit, or review
comment. The objective is complete, evidence-backed defect discovery—not stylistic preference or a
quick scan of the diff.

## Required review scope

1. Read every changed file and every changed hunk, including generated-input definitions, project
   files, build scripts, installer changes, tests, native ABI layers, and documentation that changes
   an operational contract.
2. Trace each changed behavior from every relevant entry point through its complete affected code path:
   caller, state/config input, async/thread boundary, side effect, error/recovery path, cleanup, and
   user-visible outcome. Review both normal and failure paths.
3. Follow all changed contracts across module boundaries. In this repository that explicitly includes
   C# ↔ native ABI, config.json ↔ boot manifest ↔ SYSTEM service, shell ↔ overlay ↔ Steam, installer
   ↔ running-session recovery, and theme/control ↔ every consuming AXAML surface.
4. Compare the changes against the root and nearest nested `AGENTS.md` rules, existing tests, and the
   device-verified invariants. Treat a violation as a finding even when the changed code appears to
   work in isolation.
5. Review tests as production code: verify they exercise the changed contract, isolate machine state,
   detect the regression they claim to cover, and do not automate device-only or destructive flows.

**Refuse approval of any PR or changeset that violates the documented architecture or code
conventions.** Report every such violation as `blocker` severity, request correction, and do not
approve until the implementation conforms or the project instruction itself is deliberately updated.
For every refusal, give concise but concrete remediation: name the violated rule and exact location,
explain why the current structure does not fit, and prescribe the smallest compliant move, split, API
boundary, ownership change, or test adjustment needed to resolve it.

## Project-aware security and risk review

- Review against WSGM's actual trust model and documented invariants, not generic least-privilege
  checklists. Elevated WSGM, elevated Steam, the SYSTEM logon service, scheduled-task de-elevation,
  native helper DLLs, Steam Input injection, registry/service management, and raw-input observation
  are deliberate product mechanisms. Their presence alone is never a finding.
- Flag an issue only when a change violates a stated boundary or introduces a concrete unsafe path—for
  example: a service launching an untrusted executable, an elevated Explorer, a broken token boundary,
  unchecked untrusted splash extraction, an ABI ownership error, or a recovery path that can strand a
  user without a desktop.
- Do not recommend removing elevation, avoiding native code/injection, adding consent dialogs,
  replacing scheduled tasks, or broadening sandboxing merely because those are conventional security
  defaults. Recommend an alternative only when it preserves the device-verified behavior and solves a
  demonstrated defect.
- Give operational correctness equal weight with security: regressions in Explorer recovery, Steam
  Input lease lifetime, touch/input behavior, Big Picture visibility, installer ordering, and remote
  device diagnosability are merge-blocking even when no conventional security category applies.

## Finding standard

- Report **every issue found**, regardless of severity. Do not omit a valid low-severity correctness,
  reliability, security, performance, compatibility, maintainability, or test-coverage defect merely
  because a more serious issue exists.
- Report only actionable defects with a concrete failure mode. Do not file nitpicks about formatting,
  naming, personal taste, hypothetical abstractions, or pre-existing unrelated code unless the
  changeset makes the problem materially worse.
- Each finding must state: severity, precise file and line, the triggering condition, evidence from
  the affected path, concrete impact, and the smallest safe correction. A review comment must stand
  alone without requiring the author to rediscover the reasoning.
- Severity communicates impact, not whether the finding is reported: `blocker` prevents safe merge or
  recovery; `high` risks data/session/security breakage; `medium` causes a real incorrect or degraded
  behavior; `low` is a bounded but demonstrable defect. Never inflate severity to win an argument.
- If evidence is insufficient, investigate the path or label it as a question/risk—not as a defect.
  Never invent runtime behavior, claim a test was run when it was not, or treat speculation as proof.

## Review output and closure

- Order findings by severity, then affected execution path. Lead with findings; put summaries after
  them. Include file/line anchors whenever the review surface supports them.
- If no defects are found, state `No findings` and list the code paths and failure modes actually
  reviewed, plus any residual device-only validation that could not be performed. Do not imply that
  unreviewed code is approved.
- Re-review changed fixes and all paths they affect. A finding is closed only when the correction,
  regression coverage, and relevant recovery/cleanup behavior have been checked.
- Keep review feedback separate from implementation changes unless the user explicitly requests the
  fixes. A review reports evidence first; it does not silently mutate the reviewed code.

# Shared Engineering Rules

## Coding Conventions

- Use `PascalCase` for public types and members, `camelCase` for parameters and locals, and
  `_camelCase` for private fields, except where a language's established conventions explicitly
  differ (for example, Rust's `snake_case`).
- Write clear, descriptive commit messages in English.
- Follow the repository-specific formatting rules above; do not override `.editorconfig` line endings
  or indentation with a generic convention.

## Design Patterns

- Use classes for stateful domain entities and services; use plain functions for stateless transformations and utilities.
- Prefer immutable data flows between components; mutate state only inside well-encapsulated objects.
- Choose the simplest paradigm for each task: OOP for modeling, FP for data pipelines, imperative for performance-critical loops.
- Encapsulate complex state and lifecycle in classes (repositories, services, controllers); keep business logic in pure functions.
- Use functional patterns (map, filter, reduce, composition) for data transformations instead of method chains on mutable collections.
- Apply dependency injection for wiring services together; prefer constructor injection for clarity and testability.
- Favor interfaces and protocols over concrete class hierarchies; combine with higher-order functions for flexible behavior customization.
- Organize code into layers: use classes for infrastructure and domain models, pure functions for business rules and validation logic.
- Organize application code in layers: Domain (entities/interfaces), Application (services/DTOs), Infrastructure (repositories/external), and Presentation.
- When modeling behavior variants, choose Strategy pattern (OOP) for stateful strategies or simple function parameters (FP) for stateless ones.
- Use functional error handling (Result/Either types) for recoverable operations; use exceptions only for truly exceptional, unrecoverable failures.
- Combine immutable value objects with mutable entity classes to balance safety and expressiveness in domain models.
- Apply the Adapter pattern with functional wrappers to integrate third-party libraries without polluting your core domain with external types.
- Prefer pure functions and immutable data structures; use composition and higher-order functions instead of imperative loops where practical, and isolate I/O at application boundaries.

## Performance

- Profile before optimizing — measure, don't guess. Premature optimization wastes time and adds complexity.
- Optimize the critical path first. 90% of performance comes from 10% of the code.
- Cache expensive computations and database queries — use appropriate TTLs and invalidation strategies based on data freshness requirements.
- Use lazy loading for non-critical resources and code paths.
- Debounce user-input-driven operations (search, resize, scroll).
- Prefer pagination or virtual scrolling for large data sets — never render 10,000 DOM nodes.
- Set performance budgets for bundle size, Time to Interactive, and API response times.
- Use async/concurrent processing for I/O-bound operations — don't block the main thread or event loop.
- Avoid N+1 query patterns — use batch loading, JOINs, or DataLoader patterns.
- Measure before and after every optimization with reproducible benchmarks.
- Use connection pooling for database and HTTP clients — creating connections is expensive.
- Compress API responses (gzip/brotli) and serve static assets from CDN with cache headers.

## Error Handling

- Use exceptions for truly exceptional conditions (infrastructure failures, OOM, programming bugs) and Result/Either/Option types for expected business failures (validation errors, not-found, conflicts).
- Define an error taxonomy: separate Recoverable (retry, fallback) from Fatal (crash, alert) from Expected (return to caller) errors.
- Wrap third-party exceptions at module boundaries — translate external errors into domain-specific error types that callers can pattern-match on.
- Attach structured context to all errors: operation name, relevant input IDs, timestamp, and correlation/trace ID for distributed tracing.
- Implement a centralized error handler middleware for cross-cutting concerns: structured logging, metrics emission, and user-friendly message formatting.
- Use typed error enums or discriminated unions over generic strings — this enables exhaustiveness checks at compile time and prevents unhandled error paths.
- Never swallow exceptions silently. At minimum log them; prefer propagating to a handler that can decide the correct recovery strategy.
- For async operations, ensure errors propagate correctly through promise chains — unhandled rejections should crash the process in production rather than silently failing.
- In retry logic, distinguish transient errors (network timeout, 503) from permanent errors (400, 404) — only retry transient failures with exponential backoff and a maximum retry count.
- Document error contracts at API boundaries: which errors each function can return and what callers should do about them.

## Testing

- Write unit tests for every new function or method immediately after implementation.
- Run the full unit test suite before committing — never push code with failing tests.
- Test one behavior per test case. Keep tests fast, isolated, and deterministic.
- Follow the Arrange-Act-Assert pattern: set up inputs, call the function, verify the output.
- Mock external dependencies (APIs, databases, file system) — unit tests validate your logic in isolation.
- Test edge cases: empty inputs, nulls, boundary values, error conditions — not just the happy path.
- Run unit tests after every code change during development for fast feedback.
- Aim for high coverage on business logic (80%+), but don't chase 100% — test behavior, not implementation details.
- Use test factories or builders to create consistent test data — avoid hardcoded inline objects.
- Keep tests independent — no test should depend on another test's state or execution order.
- When a bug is found, write a failing test first that reproduces it, then fix the code.
- Organize tests to mirror source structure: `src/utils/parse.ts` → `src/utils/parse.test.ts`.

### Coverage and CI

- Run the full test suite in CI on every push and pull request — never merge with failing tests.
- Set coverage thresholds for business-critical code (80%+ for core logic).
- Always run tests locally before pushing — CI is a safety net, not the first line of defense.
- Configure CI to run unit tests first (fast feedback), then integration, then E2E (test pyramid).
- Fail the build when coverage drops below the threshold — prevent gradual test debt accumulation.
- Track coverage trends over time — a declining coverage metric signals a process problem.
- Use test result caching and parallelization to keep CI feedback under 10 minutes.
- Require all tests to pass before merging PRs — no exceptions for "known flaky" tests, fix them instead.
- Measure coverage by business domain, not just overall percentage — 90% coverage that skips payments code is dangerous.
- Use mutation testing periodically to verify that your tests actually catch real bugs, not just execute code paths.
- Set up separate CI jobs for different test types: unit (every push), integration (every PR), E2E (pre-deploy).
- Generate and publish coverage reports as PR comments — make coverage visible in code review.
- Run performance/load tests in CI for critical paths — catch regressions before they reach production.
- Maintain a test health dashboard: track flaky test rate, average suite duration, and coverage by module.

# C# + NativeAOT Agent Rules

## Project Context
You are an expert C# developer working with NativeAOT runtime.

## Code Style & Structure
### C# Defaults
- Use C# 12+ features: primary constructors, collection expressions, pattern matching.
- Use `var` for local variables when the type is obvious from the right side — use explicit types when it improves readability.
- Use file-scoped namespaces (`namespace MyApp;`) to reduce nesting — available in C# 10+.
- Use `record` types for immutable data. Use `readonly` on structs and fields that shouldn't change.
- Prefer `async`/`await` for all I/O operations — never block with `.Result` or `.Wait()`.
- Use nullable reference types (`#nullable enable`). Annotate nullability explicitly.
- Use pattern matching (`is`, `switch` expressions) over type casting and `if`/`else` chains.
- Prefer LINQ methods (`.Where()`, `.Select()`, `.Any()`) over manual loops for querying collections.
- Use `Span<T>` and `Memory<T>` for high-performance buffer manipulation without allocations.
- Use `IAsyncEnumerable<T>` for streaming data from async sources.
- Prefer `ValueTask<T>` over `Task<T>` for frequently synchronous async methods.
- Use source generators over runtime reflection for serialization and DI.
- Use `global using` directives for commonly imported namespaces (System.Linq, etc.).
- Avoid `dynamic` — use generics or pattern matching for type-flexible code.

### NativeAOT Patterns
- Enable with `<PublishAot>true</PublishAot>` in csproj for ahead-of-time compilation.
- Replace reflection with source generators — use `[JsonSerializable]` for System.Text.Json serialization.
- Avoid `dynamic` keyword and `Assembly.Load()` — they are not supported in NativeAOT.
- Use `DynamicallyAccessedMembersAttribute` to annotate unavoidable reflection usage.
- Use minimal APIs instead of controllers — they avoid the reflection-heavy MVC pipeline.
- Use `IServiceCollection` dependency injection over dynamic object creation.
- Prefer compile-time configuration binding with source generators over reflection-based binding.
- Use `[LoggerMessage]` source generator for high-performance structured logging.
- Avoid `Enum.Parse<T>` in hot paths — use switch expressions or source-generated parsers.
- If a NuGet package doesn't support trimming, check for alternatives or pin it with `<TrimmerRootAssembly>`.
- If EF Core is needed, use compiled models and avoid dynamic query compilation.
- Test AOT builds in CI — runtime behavior can differ from JIT builds around initialization order.

### .NET Conventions
- Prefix interface names with 'I' (e.g., `IUserService`).
- Place `using` directives outside namespaces.
- Use exceptions for exceptional conditions; null checks for expected failures.
- Omit 'Async' suffix from method names unless providing both synchronous and asynchronous variants.

## Linting & Formatting
### dotnet-format + Roslyn
- Run `dotnet format` for code formatting. Configure in `.editorconfig` for project-wide style settings.
- Use Roslyn analyzers for static analysis: `dotnet_diagnostic.CAXXXX.severity = warning`.
- Run `dotnet format` in CI to enforce consistent code style — configure rules in `.editorconfig` at the solution root.
- Use `.editorconfig` for IDE-enforced formatting: indent style, naming conventions, code style preferences.
- Enable `EnforceCodeStyleInBuild` in `.csproj` to fail builds on style violations.
- Use `SonarAnalyzer.CSharp` or `Roslynator` NuGet packages for additional analysis rules.
- Run `dotnet format --verify-no-changes` in CI to reject unformatted code.
- Use `dotnet format analyzers` to fix analyzer-suggested code changes automatically.
- Configure severity levels in `.editorconfig`: `dotnet_style_prefer_is_null_check = true:suggestion`.
- Use `Directory.Build.props` for solution-wide analyzer configuration across multiple projects.
- Use `NoWarn` sparingly in `.csproj` — prefer fixing or explicitly suppressing with justification comments.

## Testing
### C# Testing
- Follow the naming convention: `MethodName_Scenario_ExpectedBehavior`.
- Use the Arrange-Act-Assert pattern. One assertion concept per test method.
- Use the `[Fact]` attribute for single tests and `[Theory]` with `[InlineData]` for parameterized tests in xUnit.
- Use shared fixtures for expensive setup (database, HTTP client) across tests in a class.
- Mock interfaces for dependency isolation — prefer mocking libraries with clean, fluent syntax.
- Use parameterized tests for data-driven scenarios with multiple input combinations.
- Use `Verify()` on mocks to assert that expected interactions occurred.
- If testing against a real database, use Testcontainers for isolated, disposable instances.
- Use test data generators for randomized but valid input — catches edge cases manual data misses.
- If tests share resources, control parallelism with collection attributes to prevent conflicts.
- If a test class grows beyond 10–15 tests, split into focused classes by behavior.

### xUnit
- Use `[Fact]` for single test cases and `[Theory]` with `[InlineData]` for parameterized tests. Use `Assert.Equal()`, `Assert.Throws<T>()`, and `Assert.Contains()`. One assertion focus per test method.
- Use `[ClassFixture<T>]` for shared expensive setup across tests in a class. Use `[CollectionFixture<T>]` for sharing across multiple test classes. Use `IAsyncLifetime` for async setup/teardown instead of constructor/Dispose. Mock dependencies with Moq or NSubstitute: `Mock<IService>().Setup(x => x.Method()).Returns(value)`.
- Use `[MemberData]` for complex test data from properties or methods. Use `FluentAssertions` for readable assertions: `result.Should().BeEquivalentTo(expected)`. Implement custom `IXunitSerializable` for complex theory data. Use `ITestOutputHelper` for test-scoped logging. Run in parallel by default — use `[Collection("Sequential")]` only when tests share state. Configure with `xunit.runner.json` for timeouts and parallelism.

## Libraries & Tools
### Entity Framework Core
- Configure entities in `IEntityTypeConfiguration<T>` classes, not in `OnModelCreating()` — keep DbContext clean and organized.
- Use `AsNoTracking()` for read-only queries — it skips change tracking and is significantly faster for SELECT operations.
- Use `Include()` and `ThenInclude()` for eager loading related entities — avoid N+1 queries by loading navigation properties upfront.
- Use `.Include()` and `.ThenInclude()` for eager loading. Avoid lazy loading — it causes N+1 queries.
- Use `SaveChangesAsync()` — the change tracker batches all modifications into a single transaction.
- Configure indexes, unique constraints, and relationships in `OnModelCreating()` using Fluent API.
- Use global query filters (`HasQueryFilter`) for soft deletes and multi-tenancy.
- Use `ExecuteUpdateAsync` / `ExecuteDeleteAsync` (EF 7+) for bulk operations without loading entities.
- Use `IDbContextFactory<T>` for creating short-lived contexts in background services and Blazor.
- Use split queries (`.AsSplitQuery()`) for queries with multiple collection includes to avoid cartesian explosion.

### Serilog
- Use structured log templates with named properties: `Log.Information("Order {OrderId} placed by {UserId}", orderId, userId)` — never use string interpolation (`$""`). Configure sinks in `Program.cs` with `WriteTo.Console()` and `WriteTo.File()`.
- Use `LogContext.PushProperty()` for correlation IDs and request-scoped data. Use enrichers (`Enrich.FromLogContext()`, `Enrich.WithMachineName()`) for automatic context. Set minimum level per sink: verbose to file, warning to console. Use `Serilog.AspNetCore` with `UseSerilogRequestLogging()` for HTTP request logs. Implement `ILogger<T>` injection via Microsoft DI integration.
- Use `Serilog.Expressions` for filtering and formatting in configuration. Use `WriteTo.Seq()` or `WriteTo.Elasticsearch()` for production log aggregation. Destructure objects with `@` operator: `Log.Information("Order {@Order}", order)`. Use `LogEventLevel.Fatal` + `CloseAndFlush()` in unhandled exception handlers. Configure with `appsettings.json` using `Serilog.Settings.Configuration` for environment-specific log levels without redeployment.

# Rust Agent Rules

## Project Context
You are an expert Rust developer. This project is a Library.

## Code Style & Structure
### Rust Defaults
- Embrace ownership and borrowing. Prefer borrowing (`&T`, `&mut T`) over cloning unless necessary.
- Use `Result<T, E>` for fallible operations and `Option<T>` for optional values — propagate errors with `?` operator instead of `unwrap()`.
- Use `impl Trait` in function signatures for return types and `&dyn Trait` for dynamic dispatch — prefer generics over trait objects when possible.
- Use `?` operator for error propagation. Define custom error types with `thiserror` or implement `std::error::Error`.
- Prefer iterators and combinators (`.map()`, `.filter()`, `.collect()`) over manual loops.
- Use `clippy` lints and fix all warnings. Run `cargo fmt` before every commit.
- Use `#[derive(...)]` for common traits: `Debug`, `Clone`, `PartialEq`, `Serialize`, `Deserialize`.
- Prefer `&str` over `String` in function parameters; return `String` when ownership transfer is needed.
- Use `Arc<T>` for shared ownership across threads, `Rc<T>` for single-threaded shared ownership.
- Use `#[must_use]` on functions whose return value should not be discarded.
- Organize modules with `mod.rs` or inline `mod` declarations. Re-export public types at crate root.
- Use `cfg` attributes and feature flags for conditional compilation.
- Avoid `.unwrap()` in library code — use `.expect("reason")` only when the invariant is truly guaranteed.

### Rust API Guidelines
- Use snake_case for functions, variables, and modules with descriptive names including auxiliary verbs (e.g., is_valid, has_error).
- Handle errors early using guard clauses, early returns, and the ? operator.
- Minimize allocations in hot paths; prefer zero-copy operations and static data where possible.
- Modularize code to avoid duplication, favoring iteration over repetition.
- Separate policy and metadata management from core storage for cleaner APIs.
- Prefer contiguous storage with index-based indirection over scattered pointers or dynamic structures.
- Design concurrency explicitly from the start (e.g., sharding or lock-free) rather than as an afterthought.
- Document all public items with `///` doc comments — include a `# Examples` section with a runnable `doctest` and `# Errors` / `# Panics` / `# Safety` sections where applicable.
- Implement structured logging with contextual fields for better observability.
- If an operation can fail, return `Result<T, E>` with a meaningful error type — never panic in library code.
- If a function takes ownership but doesn't need it, accept `&T` or `&mut T` instead — unnecessary ownership transfers make APIs harder to use.
- If a type implements `Display`, also implement `Error` if it represents a failure condition.
- Pre-allocate arenas or pools for frequent operations to ensure predictable performance.
- If a generic function's bounds are complex, use a `where` clause for readability over inline bounds.

## Linting & Formatting
### Rustfmt & Clippy
- Run `cargo fmt` before every commit. Configure in `rustfmt.toml` if needed.
- Run `cargo clippy` and fix all warnings — Clippy catches common mistakes and unidiomatic code.
- Run `cargo clippy -- -W clippy::all` for comprehensive linting and `cargo fmt` for formatting — add both to CI.
- Use `cargo clippy -- -D warnings` in CI to treat warnings as errors.
- Use `#[allow(clippy::lint_name)]` for intentional suppressions — always add a comment explaining why.
- Configure `rustfmt.toml` for team preferences: `max_width`, `use_field_init_shorthand`, `edition`.
- Run `cargo clippy --all-targets --all-features` to lint test code and feature-gated code too.
- Enable additional Clippy lint groups: `#![warn(clippy::pedantic)]` for stricter checks in libraries.
- Use `clippy::nursery` selectively for experimental but useful lints.
- Use `cargo fmt -- --check` in CI to reject unformatted code without modifying files.
- Use `cargo clippy --fix` for auto-fixing simple Clippy suggestions.

## Architecture
### Library Architecture
- Design a minimal, intuitive public API. Every exported symbol is a commitment — keep the surface area small.
- Follow semantic versioning strictly: breaking changes = major, new features = minor, bug fixes = patch.
- Write comprehensive documentation: README with quick start, API reference, migration guides between major versions.
- Ship both ESM and CJS (for JS/TS) or the idiomatic package format for your language. Support tree-shaking.
- Use the facade pattern: expose a clean public API that hides internal complexity. Internal modules should not be importable.
- Deprecate before removing. Mark APIs as deprecated for at least one major version before removal. Include migration path in deprecation message.
- Write examples for every public function. Examples serve as both documentation and regression tests.
- Minimize dependencies. Every dependency is a liability — it can break, have vulnerabilities, or conflict with user deps.
- Version your error types. Users may match on error kinds, so changing error variants is a breaking change.
- Support both sync and async patterns where applicable. Do not force async on users who do not need it.
- Provide TypeScript types (or equivalent type definitions) even if the library is written in plain JS. Types are documentation.
- Use feature flags or optional peer dependencies for heavy optional functionality. Keep the core lightweight.
- Write a CHANGELOG.md that explains what changed and why, not just a list of commits. Link to relevant issues.
- Run CI against multiple runtime versions (Node 18/20/22, Python 3.10/3.12, etc.) to ensure broad compatibility.
- Publish pre-release versions (alpha, beta, rc) for major changes. Let users test before committing.
- Monitor bundle size in CI. Alert on significant increases. Provide a size badge in the README.
- Write property-based tests for core algorithms. Edge cases in libraries affect all downstream users.

## Performance
### Rust Performance
- Use `&str` and `&[T]` (borrowed slices) to avoid unnecessary cloning and allocation.
- Compile with `--release` for optimized builds. Debug builds are 10-100x slower.
- Use `cargo bench` with Criterion.rs for benchmarks — compare against baselines to detect regressions across commits.
- Use iterators and combinators instead of indexed loops — they often optimize to the same assembly.
- Use `Vec::with_capacity(n)` when the final size is known to avoid reallocation.
- Use `Cow<str>` for functions that sometimes need to allocate and sometimes can borrow.
- Use `rayon` for data parallelism: `.par_iter()` for parallel map/filter/reduce.
- Use `criterion` for statistically rigorous benchmarking with warm-up and confidence intervals.
- Use `perf` or `flamegraph` crate for CPU profiling. Use `dhat` for heap allocation profiling.
- Use `SmallVec` or `ArrayVec` for collections that are usually small to avoid heap allocation.
- Use `#[inline]` on small, hot functions in libraries. Don't over-use — let the compiler decide for most code.
- Use `std::hint::black_box()` in benchmarks to prevent dead code elimination.
- Use `lto = true` and `codegen-units = 1` in `Cargo.toml` release profile for maximum optimization.

## Testing
### Rust Testing
- Use `#[cfg(test)]` module in each source file for unit tests. Use `assert_eq!`, `assert_ne!`, `assert!` macros. Put integration tests in `tests/` directory — each file is a separate test binary.
- Use `#[should_panic(expected = "message")]` for testing error conditions. Use `Result<(), Box<dyn Error>>` as test return type for `?` operator in tests. Use `cargo test -- --nocapture` to see stdout. Organize test helpers in `tests/common/mod.rs`. Use `#[ignore]` for slow tests, run with `cargo test -- --ignored`.

### Cargo Test
- Run all tests with `cargo test`.
- Run specific tests with `cargo test -- test_name`.
- Place unit tests inline in source modules using `#[cfg(test)] mod tests { use super::*; #[test] fn test_name() { ... } }`.
- Place integration tests in the `tests/` directory.
- Unit tests next to code enable fast iteration and access to private items via `super::*`.
- Organize `tests/` into subdirectories like `integration/`, `models/`, `backend/` for categorization.
- Integration tests run as separate binaries, testing full workflows.
- Unit tests compile with the library for focused, efficient testing.
- Descriptive test names allow precise filtering, e.g., `cargo test -- lora_initialization`.
- Test error cases explicitly, e.g., `assert!(matches!(result, Err(...)))`.
- Structure tests hierarchically: end-to-end in `tests/integration/`, component-specific in subdirs.
- Inline unit tests per module maintain locality for refactoring.

# PowerShell Agent Rules

## Project Context
You are an expert PowerShell developer. This project is a CLI.

## Code Style & Structure
### PowerShell Defaults
- Use approved Verb-Noun cmdlet names (e.g., `Get-Item`, `Set-Content`, `Invoke-RestMethod`) — run `Get-Verb` to see the full list of approved verbs.
- Use `PascalCase` for function names, parameter names, and variable names — PowerShell is case-insensitive but `$MyVariable` is clearer than `$myvariable`.
- Add `[CmdletBinding()]` to every advanced function to enable `-Verbose`, `-Debug`, `-WhatIf`, and `-ErrorAction` common parameters automatically.
- Use `param()` blocks with explicit `[Parameter()]` attributes and type constraints instead of positional argument parsing.
- Validate parameters with `[ValidateNotNullOrEmpty()]`, `[ValidateSet('a','b')]`, or `[ValidateRange(1,100)]` attributes rather than manual `if` checks inside the function body.
- Use `Write-Verbose` for diagnostic output and `Write-Output` (or just emit objects) for pipeline data — never use `Write-Host` in library functions as it bypasses the pipeline.
- Prefer objects over formatted strings as function output — return `[PSCustomObject]@{Name=$name; Value=$val}` so callers can pipe, filter, and format as needed.
- Use `#Requires -Version 5.1` or `#Requires -Modules Az` at the top of scripts to fail fast with a clear message on incompatible environments.
- Structure scripts as: `#Requires` statements, `param()` block, `begin{}`, `process{}`, `end{}` — use `process{}` when the function should accept pipeline input.
- Use `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` at the top of scripts to catch undefined variables and turn non-terminating errors into terminating ones.
- Avoid aliases (`ls`, `cd`, `gc`) in scripts — use full cmdlet names for clarity and portability across PowerShell editions (Windows PowerShell vs. PowerShell 7).
- Document functions with comment-based help (`<# .SYNOPSIS .DESCRIPTION .PARAMETER .EXAMPLE #>`) immediately before the `function` keyword so `Get-Help` displays it.
- Use `[System.IO.Path]::Combine()` or `Join-Path` for path construction — never string-concatenate paths with `+` or `"$dir\$file"`.

## Linting & Formatting
### PSScriptAnalyzer
- Run `Invoke-ScriptAnalyzer -Path ./src -Recurse` in CI and fail the pipeline on any `Error` or `Warning` severity finding.
- Fix `PSAvoidUsingCmdletAliases` violations by replacing aliases (`ls`, `gc`, `%`, `?`) with full cmdlet names (`Get-ChildItem`, `Get-Content`, `ForEach-Object`, `Where-Object`).
- Address `PSUseDeclaredVarsMoreThanAssignments` warnings by removing unused variables — they indicate dead code or logic errors.
- Use `Invoke-Formatter -ScriptDefinition $code -Settings CodeFormattingOTBS` (or `Allman`) to enforce consistent brace style automatically.
- Suppress individual rules inline only when absolutely necessary: `[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost','')]` with a justification comment.
- Configure a `.PSScriptAnalyzerSettings.psd1` file in the project root to pin rule severity levels and exclude generated or vendored files.
- Enable `PSUseConsistentIndentation` and `PSAlignAssignmentStatement` formatting rules to enforce 4-space indentation and aligned hashtable assignment.
- Run PSScriptAnalyzer with the `PSGallery` rule preset for module publishing: `Invoke-ScriptAnalyzer -Path . -Settings PSGallery` and resolve all findings before publishing.
- Add `PSAvoidUsingPlainTextForPassword` and `PSAvoidUsingConvertToSecureStringWithPlainText` to your required rules to catch hardcoded credential patterns.
- Use `Invoke-ScriptAnalyzer -Fix` (where supported) or `Invoke-Formatter` to auto-correct whitespace and brace placement without manual edits.
- Integrate PSScriptAnalyzer as a VS Code workspace setting: `"powershell.scriptAnalysis.settingsPath": ".PSScriptAnalyzerSettings.psd1"` for real-time feedback.
- Review `PSPossibleIncorrectComparisonWithNull` warnings carefully — always compare `$null` on the left side of `-eq` (`$null -eq $value`) to avoid type coercion surprises.

## Architecture
### CLI Architecture
- Structure CLI apps with a clear command → handler → output pipeline. Separate argument parsing from business logic.
- Use subcommands for complex CLIs. Each subcommand should have its own help text, flags, and validation.
- Exit with meaningful codes: 0 for success, 1 for general errors, 2 for usage errors. Document exit codes.
- Write to stdout for output data, stderr for logs/progress/errors. This enables piping and redirection.
- Implement a config hierarchy: CLI flags > env vars > config file > defaults. Use XDG directories for config files.
- Add --json or --output=json flag for machine-readable output. Human-readable by default, structured when piped.
- Validate all inputs early and fail fast with clear error messages that include the invalid value and expected format.
- Support --verbose/-v and --quiet/-q flags. Default output should be minimal but informative.
- Add shell completion scripts (bash, zsh, fish). Most CLI frameworks generate these automatically.
- Use progress bars for long operations. Detect TTY and suppress progress in non-interactive mode.
- Implement graceful shutdown: catch SIGINT/SIGTERM, clean up temp files, release locks, flush buffers.
- Use a plugin system for extensibility: allow users to add custom subcommands via a known directory or config.
- Add --dry-run flag for destructive operations. Show what would happen without making changes.
- Support stdin for input when no file argument is given. This enables piping from other commands.
- Include a self-update mechanism or version check that warns when a newer version is available.
- Write man pages or generate them from help text. Provide --help at every subcommand level.
- Test CLI integration by capturing stdout/stderr and asserting on output format and exit codes.

## Performance
### PowerShell Performance
- Use `[System.Collections.Generic.List[object]]` and `.Add()` instead of `+=` on arrays — array `+=` copies the entire array on each append, making it O(n²).
- Use `ForEach-Object` pipelines for large datasets that don't fit in memory; use `foreach ($x in $list)` loops for small in-memory collections — the statement form is faster.
- Filter early in the pipeline with `Where-Object` before `Select-Object` or `Sort-Object` to reduce the number of objects processed downstream.
- Use `[System.Text.StringBuilder]` for building large strings in loops instead of string concatenation with `+` — each `+` creates a new string object.
- Avoid `Invoke-Expression` — it is slow and dangerous; use `& $scriptBlock` or direct cmdlet calls instead.
- Measure code blocks with `Measure-Command { ... }` before optimizing — PowerShell overhead is often in object creation, not in the algorithm itself.
- Use `-Filter` parameters (e.g., `Get-ChildItem -Filter '*.log'`) instead of piping to `Where-Object` when the provider supports native filtering — it is orders of magnitude faster.
- Use `runspaces` (`[runspacefactory]::CreateRunspacePool`) for true parallel execution in performance-critical scripts instead of `Start-Job` which has high process-creation overhead.
- Cache `[regex]` objects outside of loops: `$re = [regex]'pattern'` and call `$re.IsMatch($s)` rather than using `-match` operator which recompiles the pattern on each iteration.
- Use `ConvertFrom-Json` with `-AsHashtable` (PowerShell 6+) instead of the default `[PSCustomObject]` output when you only need key lookup — hashtable access is O(1) vs. property reflection.
- Avoid repeated calls to `Get-Item` or `Test-Path` inside loops — batch filesystem queries into a single `Get-ChildItem` call and index results into a hashtable.
- Use `[System.IO.File]::ReadAllLines()` and `[System.IO.File]::WriteAllLines()` for large file I/O instead of `Get-Content` and `Set-Content` — they skip PowerShell's object pipeline overhead.

## Error Handling
### PowerShell Error Handling
- Set `$ErrorActionPreference = 'Stop'` at script scope to convert non-terminating errors into terminating errors that `try`/`catch` can intercept.
- Use `try { } catch [System.IO.IOException] { } catch { }` with typed catch blocks to handle specific error types before falling through to a generic handler.
- Always inspect `$_.Exception.Message` and `$_.ScriptStackTrace` in catch blocks — never silently swallow exceptions with an empty `catch {}`.
- Use `Write-Error -ErrorRecord $_` to re-throw a caught error after logging, preserving the original stack trace rather than creating a new error record.
- Use `-ErrorAction Stop` on individual cmdlet calls to make them terminating without changing the script-wide `$ErrorActionPreference`.
- Use `$Error[0]` only for debugging — in production code always capture errors through `try`/`catch` or the `-ErrorVariable` common parameter.
- Validate preconditions with `if (-not $condition) { throw [System.ArgumentException]::new('message', 'paramName') }` at the top of functions.
- Use `[System.Management.Automation.ErrorRecord]` constructors to create structured error records with a category, target object, and activity when throwing from advanced functions.
- Propagate errors from child runspaces by checking `$job.ChildJobs[0].JobStateInfo.Reason` and re-throwing in the parent scope after `Wait-Job`.
- Use `trap { Write-Error $_; break }` in legacy scripts for global error handling, but prefer `try`/`catch` in all new code — `trap` has confusing scope semantics.
- Log full error details with `$_ | ConvertTo-Json -Depth 3` to a file or structured log sink before re-throwing — this preserves the complete `ErrorRecord` for post-mortem analysis.
- Test error handling paths by calling functions with `-ErrorAction Stop` in Pester tests and using `Should -Throw -ExceptionType [System.IO.FileNotFoundException]`.

# GitHub Actions Agent Rules

## Project Context
You are an expert in GitHub Actions.

## Code Style & Structure
### GitHub Actions Workflow Conventions
- Descriptive workflow names: CI, Deploy to Production, Release, Security Scan — visible in Actions tab and PR checks
- File naming: kebab-case .yml files: ci.yml, deploy-staging.yml, codeql-analysis.yml
- Step naming: every step gets a name: for readability in logs and failure identification
- timeout-minutes on all jobs: 10 for lint/test, 30 for builds, 60 for E2E tests — prevent runaway costs
- workflow_dispatch with typed inputs for manual operations: environment, version, dry-run flag
- Use job dependency chain: needs: [lint, test] for build job; parallelize independent jobs
- Consistent trigger patterns: push to main, pull_request to main, schedule for nightly, workflow_dispatch for manual
- Environment variables: define at workflow level for shared values, job level for job-specific, step level for step-specific
- Use YAML anchors or env blocks at the top for shared configuration: node version, registry URLs
- Fail fast: set fail-fast: true on matrix jobs (default); false when you want all combinations to complete
- Cache aggressively: actions/cache for node_modules, pip, go modules — restore key with hash of lockfile
- Workflow names reflect purpose: "CI", "Deploy Production", "Nightly E2E", "Release"; shown in PR status checks
- File naming: kebab-case, descriptive: ci.yml, deploy-production.yml, release-please.yml, stale-issues.yml
- Every step has name:: required for log readability and failure identification in the GitHub UI
- timeout-minutes on all jobs: fast jobs 10-15min, builds 30min, E2E 60min; prevents billing surprises
- workflow_dispatch inputs: type (choice, string, boolean), description, required, default — self-documenting manual triggers
- Job DAG: independent jobs run parallel; use needs: for dependencies; jobs: lint → test → build → deploy
- Triggers: push + pull_request for CI, workflow_dispatch for manual, schedule for nightly, release for publishing
- Environment variable scoping: workflow env for constants, job env for job-specific, step env for step-only
- Shared config at top: env block with NODE_VERSION, REGISTRY, common paths; reduces duplication
- Matrix fail-fast: true for PR feedback (fail early); false for release validation (test all combinations)
- Caching: actions/cache with hash of lockfile as key; restore-keys for partial cache hits
- Use job outputs to pass data: steps.step-id.outputs.value → needs.job-id.outputs.value
- Consistent step ordering: checkout → setup runtime → restore cache → install deps → build → test → deploy
- Use concurrency groups: ${{ github.workflow }}-${{ github.ref }} with cancel-in-progress for PR workflows
- Organize workflows by trigger and purpose; avoid mega-workflows that do everything in one file
- Use path filters: on: push: paths: ['src/**', 'package.json'] to skip irrelevant workflow runs

# General Architecture and Collaboration Rules

## Architecture
### API Surface Design
- Keep the public API surface minimal. Every exported symbol is a maintenance commitment — if in doubt, keep it private.
- Design APIs that are impossible to misuse: required parameters cannot be omitted, invalid states cannot be represented, types enforce constraints.
- Use consistent naming conventions across the entire API: if you use create() in one module, do not use make() or new() in another.
- Make the common case easy and the advanced case possible. The 80% use case should require minimal code; power users get escape hatches.
- Use the builder pattern or option objects for functions with more than 3 parameters. Positional arguments become unreadable and error-prone.
- Design for composability: functions that accept and return the same types can be chained and combined. Prefer data-in, data-out over complex object graphs.
- Provide sensible defaults for every option. Users should be productive with zero configuration — customization is opt-in.
- Return specific types, not generic ones. createUser() returns User, not Object/any. Specific types enable IDE autocomplete and catch errors at compile time.
- Use method overloading or union types to handle multiple input formats: accept both a string ID and a full object where it makes sense.
- Make side effects explicit. Functions that read data should not write data. Functions that modify state should have names that indicate mutation (save, delete, update).
- Follow the principle of least surprise: API behavior should match what the name suggests. sort() should return a new array, sortInPlace() should mutate.
- Provide diagnostic information when APIs are misused: instead of a generic error, explain what was expected and what was received.
- Design for discoverability: group related functions in modules/namespaces that users can explore. A flat namespace with 100 exports is harder to learn than 5 modules with 20 each.
- Test your API by writing the consumer code first. If the calling code is awkward, the API design needs to change.
- Avoid boolean flags as function parameters — use named options or enum values. is_active: true is clearer than the 4th positional argument being true.
- Support both synchronous and asynchronous versions when the operation can be either. Do not force async on CPU-bound operations.
- Document every public function with parameter descriptions, return types, thrown exceptions, and at least one usage example.

### Backward Compatibility
- Follow semantic versioning strictly: breaking changes bump major, new features bump minor, bug fixes bump patch. Never introduce breaking changes in a minor version.
- Deprecate before removing: mark APIs as deprecated for at least one major version cycle before removal. Include the migration path in the deprecation message.
- Treat any change that could break a consumer as breaking: removing fields, changing return types, renaming exports, altering default behavior.
- Maintain a CHANGELOG.md that describes every change with the version, date, and category (Added, Changed, Deprecated, Removed, Fixed).
- Add runtime deprecation warnings that print once per call site: "Warning: foo() is deprecated, use bar() instead. Will be removed in v3.0."
- Write codemods or migration scripts for major version transitions. Automated migration reduces the friction of upgrading.
- Run backward-compatibility tests in CI: install the previous minor version's test suite and run it against the current code. If tests fail, you have a breaking change.
- Document all breaking changes in a dedicated MIGRATION.md for each major version. Include before/after code examples for every change.
- Use feature flags for gradual rollout of behavioral changes. Let users opt in to new behavior before it becomes the default in the next major version.
- Publish release candidates (1.0.0-rc.1) for major versions. Give consumers 2-4 weeks to test before the final release.
- Use API compatibility tools in CI: api-extractor (TypeScript), cargo-semver-checks (Rust), japicmp (Java) — automate detection of accidental breaking changes.
- Never change default values in a minor version — a consumer relying on the default gets different behavior without changing their code. This is a breaking change.
- Maintain TypeScript declaration files (.d.ts) or equivalent type stubs as part of the compatibility contract. Changing type signatures is a breaking change.
- Support multiple active major versions with security patches. Define a support policy: latest major gets features, previous major gets security fixes for 12 months.
- Track downstream breakage: monitor CI status of popular dependents (reverse dependencies) before releasing.
- Use opt-in flags for experimental features. Mark them clearly as unstable and exclude them from semver guarantees until promoted to stable.
- Test upgrade paths: a consumer on v1.x should be able to upgrade to v2.x by following the migration guide with no additional changes. Test this in CI.

### Library Documentation
- Write a README with: one-line description, installation command, minimal "getting started" example, and link to full API docs.
- Document every public function with parameter types, return types, thrown exceptions, and a runnable example.
- Include a getting-started example that takes the user from install to working code in under 2 minutes and 10 lines of code.
- Write migration guides for every major version. Include before/after code for every breaking change.
- Structure documentation in layers: README (quick start) -> Guide (tutorials, how-tos) -> API Reference (comprehensive details) -> Changelog (version history).
- Test all code examples in documentation by running them in CI. Untested examples rot — they break silently as the API evolves.
- Provide copy-pasteable code snippets. Every example should work as-is without modification, hidden imports, or assumed setup.
- Document error messages and what causes them. A user who encounters "InvalidConfigError" should find the explanation and fix in the docs.
- Include a FAQ or troubleshooting section covering the 5-10 most common issues reported in GitHub issues.
- Add badges to the README: build status, test coverage, bundle size, version, license. These signal project health at a glance.
- Generate API reference documentation from source code comments/docstrings. Keep narrative documentation (guides, tutorials) hand-written.
- Include architecture documentation for contributors: how the codebase is organized, key design decisions, and how to add new features.
- Provide examples for every common use case: basic usage, advanced configuration, error handling, integration with popular frameworks, and testing patterns.
- Maintain documentation in the same repo as the code. PRs that change API must update docs in the same PR — enforce this in the PR template.
- Version the documentation alongside releases. Users on v1.x should see v1.x docs, not the latest docs that reference v2.x features.
- Include performance characteristics in the docs: time complexity of key operations, memory usage for large inputs, and benchmarks.
- Add interactive examples (playground links, CodeSandbox, StackBlitz) where possible. Letting users experiment without installing reduces friction.

### Smart Contract Architecture
- Use OpenZeppelin or audited library contracts for standard patterns (ERC-20, ERC-721, AccessControl, ReentrancyGuard) — never re-implement security-critical primitives.
- Make contracts upgradeable only when necessary — use the proxy pattern (EIP-1967/UUPS) with explicit storage layout management. Use storage gaps (`uint256[50] private __gap`) in base contracts to allow future storage additions.
- Follow checks-effects-interactions pattern in every external function: validate all inputs and preconditions, update contract state, then make external calls. This prevents reentrancy attacks without relying solely on reentrancy guards.
- Minimize on-chain storage — store only essential state on-chain. Use events/logs for historical data and off-chain indexing (The Graph, custom indexers). Each storage slot costs 20,000 gas to initialize.
- Implement role-based access control (AccessControl) with separate roles: admin (governance), operator (day-to-day), upgrader (proxy upgrades), pauser (emergency). Use timelocks for sensitive admin operations.
- Design for gas optimization: pack storage variables into 32-byte slots, use calldata for read-only function parameters, batch operations, use unchecked blocks for safe arithmetic, and prefer mappings over arrays for lookups.
- Emit indexed events for every state change — they are the primary interface for frontends, indexers, and monitoring services to track contract activity.
- Implement emergency mechanisms: Pausable for halting operations during incidents, and circuit breakers with rate limiting for high-value operations.
- Write comprehensive NatSpec documentation (@notice, @dev, @param, @return) for all public/external functions — this becomes the contract's API documentation.
- Use the diamond pattern (EIP-2535) or modular contract architecture only for complex protocols that genuinely need it — prefer simpler proxy patterns for most use cases.
- Test with both unit tests (Foundry/Hardhat) and invariant/fuzz tests — smart contract bugs are irreversible and potentially catastrophic. Achieve 100% branch coverage before deployment.

### Desktop Architecture
- Use a single source of truth for application state with unidirectional data flow to prevent state desynchronization across windows.
- Use native OS conventions for menus, keyboard shortcuts, drag-and-drop, and accessibility — don't reinvent platform-standard interactions.
- Separate UI rendering from business logic using MVP, MVVM, or MVC — never embed domain logic in event handlers or widget callbacks. The UI layer should only bind data and forward user actions.
- Keep the main/UI thread responsive: offload file I/O, network calls, and CPU-intensive computation to background threads, worker processes, or async task queues. Use progress indicators for operations exceeding 200ms.
- Implement a platform abstraction layer (PAL) so core business logic is fully testable without a running GUI framework — mock the PAL in tests.
- Handle window lifecycle events explicitly: persist unsaved state on close, confirm destructive operations with dialogs, release file locks, and clean up temporary resources.
- Design for offline-first: cache data locally (SQLite, embedded DB) and implement sync-when-available with conflict resolution for remote data.
- Support undo/redo using a command pattern or immutable state snapshots — users expect to reverse actions in desktop applications.
- Handle multi-monitor, DPI scaling, and window positioning gracefully — persist and restore window geometry across sessions.
- Implement auto-update mechanisms with rollback support — desktop apps must update themselves since users won't reinstall manually.
- Use structured logging with rotation for desktop apps — logs are critical for debugging issues reported by end users running on diverse hardware.

# GitHub Actions Delivery Rules

## Performance
### CI/CD Pipeline Optimization
- Job parallelization: lint (1min) | test (3min) | e2e (5min) concurrent → total 5min instead of 9min sequential
- Path filters: on.push.paths avoid unnecessary runs; use paths-ignore for docs, README, LICENSE changes
- Runner tiers: 2-core for lint/test, 4-core for builds, 8/16-core for E2E suites; balance cost vs. speed
- Artifacts: upload build output once → download in deploy/test jobs; set retention-days: 1 for PR artifacts
- Step consolidation: one RUN with npm ci && npm run build vs. two steps; reduces ~5s overhead per step
- setup-* action caching: prefer built-in caching (actions/setup-node cache: npm) over manual actions/cache
- Matrix optimization: include only meaningful combinations; exclude known-broken combinations explicitly
- Conditional execution: skip deploy on PR, skip E2E on draft PR, skip lint on direct pushes to main
- Concurrency: cancel-in-progress: true for PR workflows (save money); false for main branch (ensure completion)
- Docker layer caching: cache-from: type=gha, cache-to: type=gha,mode=max in docker/build-push-action
- Self-hosted runners: pre-install tools, warm caches, use ephemeral runners for security isolation
- Workflow timing: add timestamps to step names or use actions/workflow-run-stats to identify bottlenecks
- Composite actions for repeated setup: checkout + cache + install as one action reduces workflow verbosity
- Use GitHub Actions cache metrics to monitor hit rates and identify cold cache scenarios
- Split E2E tests with matrix: shard test files across parallel jobs for linear speedup

### Caching & Workflow Optimization
- Cache build outputs (`.next/cache`, `dist/`, `.turbo/`) in addition to dependencies for faster CI.
- Set appropriate `SEGMENT_DOWNLOAD_TIMEOUT` for large caches to avoid timeout failures.
- Cache dependencies with a precise key: `key: ${{ runner.os }}-${{ hashFiles('**/lockfile') }}` — ensures cache invalidation on dependency changes.
- Use `restore-keys` for graceful fallback: `restore-keys: ${{ runner.os }}-node-` matches the most recent cache with that prefix.
- Prefer built-in caching in setup actions (`actions/setup-node@v4` with `cache: 'npm'`) — simpler configuration and automatic key management.
- Cache build artifacts (`.next/cache`, `target/`, `.turbo/`) separately from dependencies — different invalidation cadences.
- Use `actions/cache/save` and `actions/cache/restore` separately for fine-grained control over when saves happen (e.g., only on main branch).
- Monitor cache hit rates in workflow logs; adjust keys if miss rates are high. GitHub provides 10GB cache per repo.
- Use `hashFiles()` with glob patterns matching all relevant lockfiles: `hashFiles('**/pnpm-lock.yaml', '**/Cargo.lock')`.
- Avoid caching ephemeral data (logs, test reports) — cache only artifacts that are expensive to regenerate.

# General Security and Collaboration Rules

## Security
### Security Guidelines
- Validate and sanitize all user inputs from external sources.
- NEVER hardcode secrets (API keys, passwords) in the codebase. Use environment variables.
- Use parameterized queries for all database access — never concatenate user input into SQL, command strings, or template expressions.
- If you detect a hardcoded secret, stop immediately and prompt the user to remove it.
- Ensure code handles edge cases and failures gracefully, not just the happy path.
- If accepting file uploads, validate MIME type, size, and filename — never trust client-supplied content types.
- If using environment variables for secrets, ensure they're not logged, serialized, or exposed in error messages.
- If a route handles sensitive operations (password change, payment), require re-authentication or CSRF tokens.
- If rate limiting is needed, implement it at the API gateway or reverse proxy level — not in application code alone.
- Keep dependencies up to date and scan for known vulnerabilities in CI — treat high-severity CVEs as release blockers and patch them before deploying to production.

### GitHub Actions Security
- Pin actions by SHA: uses: actions/checkout@11bd71901... — prevent supply chain attacks from tag mutation
- Minimal GITHUB_TOKEN permissions: set at workflow level, override per job if needed; default to read-only
- Environment protection: required reviewers, wait timer, deployment branch restrictions for production
- Secrets storage: GitHub Secrets for CI values, OIDC for cloud provider authentication (no long-lived keys)
- Set concurrency groups to prevent parallel runs of sensitive workflows: concurrency: { group: deploy-prod }
- Pin all actions by full SHA (40 chars): uses: actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683
- Workflow-level permissions: permissions: { contents: read } — add specific permissions per job as needed
- Environment protection rules: required reviewers, wait timers, deployment branches, custom policies
- OIDC federation: aws-actions/configure-aws-credentials with role-to-assume — no AWS_ACCESS_KEY_ID stored
- Secrets hierarchy: repository secrets for repo-specific, organization secrets for shared, environment secrets for deploy
- Avoid pull_request_target + actions/checkout of PR head — attacker-controlled code runs with write permissions
- Dependabot/Renovate for action version updates: automatic PRs when new versions or security fixes are available
- Audit: review workflow_dispatch triggers, check for fork PR permissions, monitor billing for crypto mining
- Security scanning: actions/dependency-review-action on PRs, github/codeql-action for code analysis
- Use allow-list for self-hosted runners: restrict which repos can use them; avoid self-hosted for public repos
- Workflow isolation: use separate GitHub Apps or deploy keys per repository instead of PATs
- Set CODEOWNERS for .github/workflows/ — require review for all workflow changes
- Use step-level if: conditions to skip sensitive operations on fork PRs: if: github.event.pull_request.head.repo.full_name == github.repository

### Permissions & Privacy
- Request permissions just-in-time, not at app launch. Ask for camera permission when the user taps the camera button, not on first open.
- Always explain why you need a permission before requesting it. Use a pre-permission dialog: "We need camera access to scan QR codes."
- Handle permission denial gracefully. The feature should degrade, not crash. Offer an alternative path or explain how to grant permission in Settings.
- Request the minimum permission scope: "When in Use" location instead of "Always", read-only photo access instead of full library access.
- Check permission status before requesting. If already granted, proceed silently. If denied, show instructions to enable in system Settings with a deep link.
- Handle the "Don't ask again" state (Android) and the second denial (iOS). After permanent denial, the only recovery is directing users to Settings.
- Audit all permissions in the app manifest/Info.plist quarterly. Remove permissions you no longer use — unused permissions are a privacy risk and app store rejection risk.
- Provide clear privacy policy text for each permission. App stores require a purpose string (NSCameraUsageDescription on iOS) — make it user-friendly, not developer jargon.
- Track permission grant rates. If a permission is denied by >30% of users, reconsider whether you need it or improve the pre-permission explanation.
- Support graceful downgrade for optional permissions: the app works without location, but enables nearby search when granted. Core functionality must never depend on optional permissions.
- Handle permission changes during the app lifecycle: users can revoke permissions in Settings while the app is backgrounded. Check permissions when the app returns to the foreground.
- Use approximate location (coarse) when precise location is not needed. Approximate location is sufficient for weather, news, and city-level features.
- Implement permission-aware onboarding: if the app requires a permission to function (e.g., a camera app), explain the requirement on the onboarding screen.
- Never request all permissions at once during onboarding. This overwhelms users and increases denial rates. Request each permission at the point of relevance.
- Comply with platform data transparency requirements: iOS App Privacy labels, Android Data Safety sections, and GDPR data processing records.
- Test all permission flows: grant, deny, deny permanently, revoke while backgrounded, revoke and re-grant. Each path must produce a working (possibly degraded) experience.

## Context Management
### Memory Bank Pattern
- Maintain project context files that the agent reads at the start of every session.
- Keep an `activeContext.md` with current work focus, recent decisions, and open questions.
- Store project-specific decisions, architecture choices, and common patterns in a persistent memory file — update it when decisions change.
- Include `projectbrief.md` (goals, scope) and `techContext.md` (stack, architecture, conventions).
- Update context files after significant decisions or architectural changes — they are living documents.
- Store context files in a known location (`.cursor/rules/`, `.agent/`, project root) for easy discovery.
- Version-control all context files alongside the codebase — they evolve with the project.
- Periodically prune stale context — outdated information is worse than no information.
- Include dependency maps showing how major modules relate to each other.

### Rules & Context Files
- Store agent rules in `.cursor/rules/`, `AGENTS.md`, or `.github/copilot-instructions.md` as appropriate.
- Rules should be prescriptive and actionable — "Use X pattern" not "Consider using X".
- Keep context files under 200 lines — split into topic-specific files (debugging.md, patterns.md) linked from the main rules file.
- Keep rules project-specific — include coding standards, architecture conventions, and known patterns.
- Include negative rules ("Don't use X") to prevent common mistakes specific to your project.
- Keep rules concise — agents have limited context windows; verbose rules dilute important information.
- Reference real file paths and patterns from your codebase — concrete examples beat abstract guidance.
- Test your rules by observing agent behavior — iterate on rules that don't produce desired outcomes.
- Share rules across team members via version control — everyone's agent should follow the same conventions.

## Project Management
### Code Review Patterns
- Follow the dedicated **Agent Review Rules** above. Keep changesets cohesive, state their rationale and
  test plan, and keep formatting-only work separate from behavior changes when practical.

### Task Decomposition
- Break every feature into tasks that can be completed in 1–2 days maximum.
- Define acceptance criteria for every task before starting — know what "done" looks like.
- Each task should be completable in 1-4 hours — if a task takes longer, it needs further decomposition into smaller pieces.
- Order tasks by dependency — identify what must be done first and what can be parallelized.
- Separate research/spike tasks from implementation tasks — time-box exploration.
- Create follow-up tasks for known technical debt rather than expanding scope of current work.
- Decompose vertically (thin end-to-end slices) not horizontally (all backend, then all frontend).
- Include non-code tasks: documentation updates, monitoring setup, stakeholder communication.
- Use the INVEST criteria: Independent, Negotiable, Valuable, Estimable, Small, Testable.

## MCP Integration
### MCP Integration Patterns
- Enable only the MCP servers needed for the current task — too many servers overwhelm the context.
- Use environment variables for all MCP server credentials — never hardcode API keys or tokens.
- Test MCP tools individually before chaining them — verify each tool's output format and error behavior in isolation.
- Handle MCP server errors gracefully — network failures, rate limits, and auth errors should not crash.
- Version-control MCP server configuration (`mcp.json`, `.cursor/mcp.json`) for team consistency.
- Test MCP integrations independently before relying on them in complex workflows.
- Use project-level MCP configs for repo-specific servers, global configs for personal tools.
- Respect rate limits of underlying APIs — add delays between batch operations.
- Prefer official MCP servers from service providers over community forks for security and reliability.
