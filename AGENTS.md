# AGENTS.md

Source of truth for every coding agent working in this repository (Claude Code, Codex, others).

**The agent instructions ship with the repo, deliberately.** Every `AGENTS.md` and every `CLAUDE.md`
is tracked, so review tooling and a contributor's own AI read the same binding rules from the checked
-out tree rather than guessing. Do not add either back to `.gitignore`.

**Every `CLAUDE.md` here is a symlink (mode `120000`) to the `AGENTS.md` beside it** — including the
one at the repository root. That is load-bearing, not tidiness: Claude Code auto-loads `CLAUDE.md`,
so the symlink is what puts these rules into the agent's context. A `CLAUDE.md` that is a *regular
file* — typically one line reading `AGENTS.md`, left by a checkout without symlink support or by a
tool that rewrote it — silently drops that directory's guidance, and the agent then appears to ignore
rules it was never shown. If you find one, recreate it as a symlink (`New-Item -ItemType SymbolicLink
-Path CLAUDE.md -Target AGENTS.md`). Never replace one with a real file and never put content in a
`CLAUDE.md`; all guidance goes in `AGENTS.md`.

Public-facing text — the README, the wiki, release notes — must state a rule itself rather than cite
these files.

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

## Working in this repository (maintainer workflow)

The section above governs contributions arriving as PRs. Work done *inside* this repository for the
maintainer follows a deliberately simpler flow — do not import the PR ceremony into it:

- **Commit directly to `master`.** Do not create a feature branch, and do not offer to — the
  maintainer says so when a branch is wanted. Release tags land on master regardless, so a branch
  only adds a merge step.
- **Committing and pushing are separate, and both are asked for explicitly.** "Commit it" means
  commit and stop. Never push, tag, or publish on your own initiative.
- **Know what automation actually runs.** Codex reviews **pull requests only** — a push to `master`
  gets no review from it. What a push does trigger is `.github\workflows\ci.yml` (it fires on both
  `push` and `pull_request`) and GitHub's **CodeQL**, which is configured through GitHub's *default
  setup* and therefore has **no workflow file in this tree** — do not go looking for `codeql.yml` or
  add one. CodeQL is a security scanner, not a code review; neither substitutes for the other.
- Version numbers stay user-owned — see the `<Version>` rule under Build and packaging.
- Every completed implementation task ends with `./build.ps1` and the installer copied to `Z:\`
  (see "Dev environment reality").

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
license files into `src\WSGM\Native\SteamInputLease\`. `WSGM.csproj` copies the two DLLs and the
license files beside the AOT executable and the installer ships those; `steam-input-lease.exe` is
deliberately **not** shipped (see below). `build.ps1` calls it first; `eng\verify.ps1` calls it with
`-Validate`, which adds the library's own gates (`cargo clippy -- -D warnings`, `cargo test`). CI
therefore needs a Rust toolchain — it adds the clippy component and caches `target\`.

That staging directory is **generated and gitignored**; `native\SteamInput` is the tracked source.
Never hand-copy binaries into it. A Rust toolchain is now required to build WSGM at all.

`src\WSGM\SteamInterop\*.cs` are copies of `bindings\SteamInterop.Net\*.cs` **plus explicit
`using` directives** (neither WSGM nor `WSGM.Launch`, which links the same files, enables
`ImplicitUsings`) — diff, don't blind-copy. The Rust code
is deliberately not `cargo fmt` clean and has no fmt gate; do not reformat untouched code. Both
`native\SteamInput\` and the staging directory are in `.prettierignore` (the latter because
regenerating it would otherwise fail the next format check).

`steam-input-lease.exe` is a **development/diagnostic tool only** and is not installed. The
user-facing wrapper is `WSGM.Launch.exe`, which links the same binding mirror, takes the lease itself
via `--input-lease`, and carries the CLI's `--status`/`--rescan` diagnostics (invariant 5).

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
→ `ExplorerControl.ExitExplorerAndWait(30 s)` → posture → TrayHost → startup apps (skipping ones
explorer's autostart already launched) → Steam, strictly AFTER explorer is gone.

**How Explorer is ended — device-settled, do not change the mechanism:** `ExitExplorerAndWait`
posts `0x5B4` (WM_USER+436, explorer's own Ctrl+Shift-taskbar "Exit Explorer" command) to
explorer's pid-verified `Shell_TrayWnd`. That intentional shutdown is the ONLY way Winlogon's
AutoRestartShell does not respawn the shell. PID-snapshot semantics: any explorer pid not in the
initial snapshot is a Winlogon replacement → cancel and **fail open** (preserve desktop mode, warn
`Couldn't exit Windows Explorer safely`); a replacement is NEVER killed (fighting AutoRestartShell
loops) — instead the orderly exit is retried ONCE against the respawned shell, which is a freshly
started explorer that honors it within seconds, and both attempts share ONE deadline (a fresh full
budget for the retry let a caller asking for 15 s sit in the transition for more than twice that).
Lingering snapshotted pids are terminated only after explorer destroyed its taskbar (a
shell extension can hold the process open — device-observed) **and only after a `LingerGrace` (8 s)
window in which the remnant is given the chance to leave on its own** — killing it mid-shutdown is
itself what Winlogon respawns (device-observed 2026-08-08 as "game mode needs two tries"; a clean
run had the remnant exit ~830 ms after the taskbar went). That grace is never shortened to fit the
remaining budget: a remnant that did not get the full window is left alone and the exit fails open.
Success requires 500 ms of stable absence. Two mechanisms are device-DISPROVEN (2026-08-07): plain `Process.Kill` (Winlogon
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
so routing LB/RB there would double-advance the panel's tabs. The Tools tab hosts five in-place
sub-views (`PanelFormat`, `LibraryTabsView`, `CardManagerView`, `ArtworkView`, `LaunchWrapperView`);
adding one means extending `AnySubView`, `DefaultFocusTarget`, `TryCancelSubView`, the `Activated`
teardown, and an `Enter*`/`Leave*` pair — never a Popup/Flyout, which `GamepadNavigation` cannot reach.

**Text entry in the panel is a press-to-edit ROW, never a bare `TextBox`** (maintainer, on the
format name reading as broken). Every editable name — the tab editor, card rename, filter
patterns — is a `CardButton`/`Row` whose Description shows the current value and whose click
opens the peer keyboard window through `KeyboardService.Request`. A `TextBox` dropped into a
panel looks editable but is unusable on a controller: `GamepadNavigation` deliberately skips
TextBoxes so the Windows touch keyboard cannot pop, so focus never lands on it and nothing
types. The format library name was the last holdout and is now a row like the rest. When
`KeyboardService.Request` returns false there is no way to type at all — log it rather than
leaving a row that silently does nothing when pressed.

**Format SD Card lives inside the Card Manager**, not the Tools list (maintainer): formatting a
card and managing tracked cards are one subject. `CardManagerView` raises `FormatRequested`, the
overlay leaves that sub-view and enters `PanelFormat` (two Tools sub-views must never own the
surface at once), and Cancel/Back returns to the Card Manager via `LeaveFormatSubViewToOrigin`
— which rescans, so a card that was just formatted appears immediately. The two feature toggles
stay independent: `OverlayViewModel.ShowFormatInTools` (`ShowSdCard && !ShowCardManager`) brings
the Tools button back when the Card Manager is switched off, so `Cef.SdFormat` can never be on
with no way to reach it. `_formatReturnsToCards` is cleared in `LeaveFormatSubView` so the
`Activated` teardown cannot bounce a fresh summon back into the Card Manager.

**Input stack** (`Input\`): `SdlGamepads` is the process-wide SDL3 owner (single event pump — two
`GamepadService` instances exist when Settings is open; per-instance pumps would steal hotplug
events). UI-thread 16 ms `DispatcherTimer` poll → edge-triggered `ButtonPressed` (+ direction
auto-repeat) and full-state `StateChanged` (chords) → `GamepadNavigation` (focus movement through
tab order, synthesized Enter to activate, arrow-key mirror with 250 ms dedupe, skips TextBoxes so
the touch keyboard doesn't pop) and `GamepadChordWatcher`. `Overlay\TouchSwipeMonitor` observes the
raw HID digitizer (`RIDEV_INPUTSINK`, observation only) for four configurable edge swipes _and_
tap-outside-overlay dismissal. Bottom/right retain WSGM's taskbar/quick-access actions; left/top
always send Steam's installed-client keyboard mappings Ctrl+1 (Steam menu) / Ctrl+2 (Quick Access
Menu), including while a game is foreground — bringing Steam's menu over the game is their purpose.

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
   **Live-verified end to end (2026-08-12, dev box, `steam-input-lease.exe`, real Steam Controller
   connected):** acquire took the pad from Steam (`tracked HID handles` 1 → 0, `handles revoked by
   last transition` = 1), Steam had rediscovered it within 700 ms of release (0 → 1), and an
   explicit `--rescan` moved Steam's scan counter 14 → 16. **Measured cost:** cold inject + acquire
   + release **492 ms** (one-off; the injection dominates), warm acquire + release **41-42 ms** with
   and without a pad, and a single pipe reply (`--status`) **12-16 ms across ten consecutive calls**.
   A review finding claimed every pipe reply re-resolves the recovery layout inline, so an acquire
   can block on a full cross-process address-space sweep — that does NOT reproduce: the layout is
   resolved once on the gate's warm-up and cached, and a sweep would cost hundreds of ms per reply,
   not 14. Do not "fix" it; the proposed fix (answering `payload_capabilities()` only from
   already-resolved state) would additionally make the FIRST acquire report no internal recovery,
   sending `SteamInputBlocker.cs`'s acquire-time gate into a host-side sweep under `Sync` —
   strictly worse. The dev box's Steam is a usable rig for this: the gate stays mapped (pinned by
   design) until Steam restarts.
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
   `WSGM.Launch.exe` is the user-facing extension of the same mechanism for Steam games that
   reject elevation. It is the **single** launch wrapper: it replaced `WSGM.Deelevate.exe` and
   `steam-input-lease.exe`, which the installer now deletes on update, so anyone who had pasted one
   of the old commands must re-apply the fix (call this out in the release notes). Behaviours are selected by flag: `"...\WSGM.Launch.exe" [--deelevate] [--input-lease]
   -- %command%`, at least one required, the target command always after `--`. The elevated
   wrapper must remain alive for the target lifetime, preserve Steam's arguments/environment/CWD,
   and stop the target tree if Steam terminates the wrapper. Do not replace it with a fire-and-forget
   scheduled task or an Explorer-token shortcut. **The lease is the OUTER behaviour**: its gate
   injects into an elevated `steam.exe`, which a medium-integrity process cannot do, so it is
   acquired by the elevated parent *before* the de-elevation hand-off and released after the medium
   child reports the target's exit. **Both paths wait on a job object, not on the process they
   started** (`--input-lease` alone in the native wrapper, which starts the target suspended and
   assigns before resume; the de-elevated child in `WSGM.Launch\JobObject.cs`, which assigns right
   after `Process.Start`): a game behind a launcher exits its root process seconds in, and waiting
   on that alone released the lease mid-session and told Steam the game had stopped. The job is also
   what makes the stop-on-parent-exit path reach orphaned descendants. Lease failures fail **open** —
   log, tell the user, launch anyway — and so does an impossible de-elevation (UAC switched off
   leaves no limited token to hand out; the child tags that failure and the parent launches the game
   as-is). An error out of `run_wrapped` means only that the target NEVER STARTED, because that is
   what the caller does about it; a release handshake that fails after the game exited returns the
   exit code instead, or the wrapper would start a finished game a second time. It is still
   **reported**, through `WrappedRun.release` (ABI 3 added the `release` output to
   `sil_client_run_wrapped`, which previously discarded it): blocking is lifted either way, but a
   failed handshake means Steam was never asked to rediscover controllers, so `WSGM.Launch` writes
   `Steam Input lease released, but Steam controller recovery did not run …` to `launch.log` —
   the only surface that failure was ever diagnosable from.
   **Four device-verified invariants make it actually work when Steam is elevated (each was a
   separate real failure, 2026-08-12):** (a) it MUST be a
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
   `Core\SteamLaunchConfig.cs` writes (d) into the running client so the user never has to; see
   invariant 11.
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
   **The detection path is boot-critical and must never throw** (regressed 2026-08-12, caught on the
   device across two reboots): the splash's 250 ms poll calls
   `WindowFinder.FindWindow` → `FindProcessIds`, which reads `Process.SessionId` per candidate. That
   read sits behind a deliberately BLANKET `catch` — an audit "fix" narrowed it to
   `InvalidOperationException`/`Win32Exception`, so any other type propagated out of the poll, BP was
   never detected, the splash never faded, and its opaque cover sat over a live BP window: black
   intro video, every boot. Do not narrow it, and do not add an unthrottled `Log` call inside it
   either — at 4 Hz across Steam's several helper processes that alone fills the capped log.
   The general rule: on any poll that feeds splash dismissal or takeover progress, a swallowed
   exception is the lesser failure. Prefer a throttled one-shot warning over a narrower catch.
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
   `CurrentAppIdJs` resolves to **`{id,src}`**, not a bare number: it is ONE source string shared by
   the C# reader and the resident badge (so the center/visibility rules cannot drift between them),
   and `src` names the signal that matched — `focus` (the focused element's React fiber, tried first)
   or `hero image`. The badge's `curId()` unwraps `.id`. `Log` prints the signal, so a detection that
   silently shifts from one signal to the other is visible in a pasted `wsgm.log` instead of hiding
   behind a generic label. Bump `BadgeScriptVersion` whenever the resident script text changes, and
   re-probe both branches against a live Steam (`tools/WsgmLibTest`) before shipping a change here.
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
11. **Writing a game's launch configuration (`Core\SteamLaunchConfig.cs`, live-probed 2026-08-12).**
   The Tools tab's per-game launch fixes configure the RUNNING Steam client over SharedJSContext
   instead of copying a command for the user to paste; with `Cef.Enabled` off they fall back to the
   clipboard. Two APIs, because Steam treats the two kinds of entry differently: a real title takes
   `SteamClient.Apps.SetAppLaunchOptions(appid, str)`; a non-Steam shortcut takes
   `SetShortcutExe` + `SetShortcutLaunchOptions` (invariant 5d — a shortcut ignores an
   exe-replacement launch option). **Steam stores every one of these values VERBATIM** — it neither
   adds nor strips quotes and does not touch backslashes — and its own shortcut `Exe` is stored
   *quoted* with single backslashes (`"C:\Games\…\game.exe"`), so WSGM supplies the quotes itself.
   Never use decky's `JSON.stringify(path)` form: it doubles backslashes and is only correct on
   Linux. Reads go through `RegisterForAppDetails` wrapped in a promise with a timeout and
   `unregister()` on both paths (it is a subscription, not a getter, and it re-fires after a write);
   `GetLaunchOptionsForApp` is the launch-*menu* list, not the options string. Writes persist to
   `shortcuts.vdf`/`localconfig.vdf` immediately — **no Steam restart, and never hand-write those
   files**. `StartDir` is deliberately never written (the game's folder stays the CWD). A real
   title's **existing launch options are composed, never replaced** (`%command%` expands to the
   game's own command, so options the wrapper value overwrote would silently stop applying): plain
   options move after the placeholder, a user value that positions `%command%` itself keeps its
   prefix and suffix, and re-applying reads them back out with
   `LaunchWrapperCommand.OriginalLaunchOptions`. `%command%` is **real titles only** — a non-Steam
   shortcut ignores it (see 5d). Because
   configuring a shortcut destroys its original Target, the pre-change values are snapshotted into
   `AppConfig.LaunchWrappers` BEFORE the write — via `SteamLaunchConfig.OriginalsFrom`, which
   UNWRAPS an already-wrapped game (the command may have been pasted by hand, or the config reset)
   so the snapshot never records WSGM's own wrapper as the "original" and Remove cannot restore the
   wrapper itself — and re-applying an already-wrapped game keeps the
   first snapshot rather than recording WSGM's own values.

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
Settings is `Settings\SettingsWindow` + six always-alive `Settings\Pages\*` UserControls toggled by
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

12. **Download-queue sorting (`Core\SteamDownloadSort.cs`, live-verified 2026-08-12).** Name/Size/Type
   sort buttons injected into the header of Big Picture's "Up Next" download section, reordering the
   queue through Steam's own `SteamClient.Downloads.SetQueueIndex(appid, index, remoteClientId)`.
   Three findings are load-bearing and were each a real failure first:
   (a) **The buttons must be built from Steam's own `Focusable` component.** A plain DOM injection
   renders and clicks fine but is invisible to Big Picture's gamepad focus tree — device-confirmed
   ("its not navigateable with controller"). With `Focusable` the controller reaches them and the
   footer shows the select hint.
   (b) **The injection point is the JSX runtime**, not the component. The section header rebuilds its
   own `children` array after spreading rest props, so it can only be WRAPPED; and the download-list
   section is a **MobX observer** whose `render` is a NON-configurable, NON-writable own property on
   every instance, so it cannot be patched, deleted, or shadowed by a prototype accessor. Wrapping
   `jsx`/`jsxs` and intercepting the header element at creation is what is left; the hot-path cost is
   one reference comparison. Some runtime modules re-export the same binding, so a wrapper must be
   skipped if it already carries the guard property — wrapping a wrapper renders the bar twice.
   (c) **The `Focusable` lookup must stay tight.** Matching "flow-children" + "onActivate" also hits
   three chat/friends CLASS components and the registry hands a text-area component back FIRST — which
   rendered a textbox into the download header. Require a plain function under 1500 chars that
   destructures the quoted `"flow-children"` key together with `onActivate:` / `focusClassName` /
   `focusWithinClassName`; that leaves exactly one match. Note webpack's ES exports are ACCESSOR
   properties — a value-only scan (`getOwnPropertyDescriptor(...).value`) finds neither React nor
   `Focusable`, which is also why they cannot be located the way a plain object's members would be.
   **Scope is the ENTIRE pending list** (maintainer-directed): `QueuedTransfers` + `UnqueuedTransfers`
   + `ScheduledTransfers`, minus completed, renumbered from index 0. Index 0 is included — the item
   Steam is currently working on is part of the queue, and excluding it made a sort look broken;
   moving another app to index 0 only switches which one Steam works on, and per-app progress is
   retained. Including the scheduled entries **queues them** (their `queue_index` is -1 until a sort
   gives them one), which is exactly what dragging them into the queue does in Steam's own UI — so a
   sort empties the "Scheduled" section. **That is the point, not a side effect** (maintainer, on
   being offered the schedule-preserving alternative): when Wi-Fi drops mid-download Steam kicks the
   whole queue out to unqueued/scheduled, and one tap on a sort button is how fifty entries go back
   in. Do NOT "fix" this into sorting each section separately or preserving `deferred_time` — a
   reviewer re-raising it should be answered "deliberate, it is the bulk re-queue path". Never seed
   the renumbering from `items[0].queue_index`: with unqueued entries in the list that can be -1.
   The apply loop is one `SetQueueIndex` per item at 120 ms, so a fifty-entry re-queue takes ~6 s
   with the buttons dimmed; that pacing is deliberate and the list is deliberately not capped.
   **SIZE means bytes LEFT to download** (`bytes_total - bytes_in_progress`), not the total — the
   queue is about what is still coming down the wire. A freshly restarted client reports
   `bytes_total == 0` for queued-but-not-yet-planned apps; that is "unknown", NOT "smallest", and
   ranking it as zero is what made the first tap look like it did nothing while the second (reversed)
   tap looked correct — the reported "only works on the second tap" bug. Unknown-size items are
   parked at the END in BOTH directions, which is why each comparator takes the direction as an
   argument instead of the caller flipping the sign. WSGM never calls
   `EnableAllDownloads`, but a sort still **resumes a paused queue** (live-verified: paused →
   `Downloading`, even when the resulting order is unchanged) because Steam reacts to a
   `SetQueueIndex` at the head. That is accepted — it is what dragging an item to the top does in
   Steam's own UI — and must not be "fixed" by re-pausing afterwards. Displayed size is Steam's own formula: the sum
   of `progress[k_EAppUpdateProgress_Download].bytes_total` across every content type; taking the max
   over the progress array yields numbers that do not match the rows. `buildid == 0` = Install,
   otherwise Update. The queued section is identified by the locale-independent
   `#Downloads_Section_Current` title token plus a `count`+`labelId` shape check. Re-probe against a
   live Steam (`tools/WsgmLibTest/run-prod-sort.mjs`, which extracts the script verbatim from the C#)
   before shipping a change here.

**Turning a CEF feature off must RETRACT, not just stop pushing.** The injected tabs, badges,
synthetic Wi-Fi AP and download-sort buttons are resident in Steam's CEF session and survive until
Steam restarts. The master switch fails every evaluation closed — including WSGM's own `Disable*`
calls — so `ShellSession` owns it and awaits the retractions BEFORE closing the choke point
(`ApplyCefMasterSwitch`); a sub-toggle going off retracts through the same kill switches inside the
sync (`LibraryTabManager`), and the Wi-Fi indicator's and download sort's start gates are live fields,
not the boot-time `_config`, so their toggles apply without a re-logon. The overlay's per-feature
button visibility is recomputed on config reload as well, so a disabled feature loses its entry point
immediately.

**Mute while the screen is off** (`Shell\DisplayOffMuteService.cs`, `Interop\MessageWindow.cs`,
config `MuteWhileDisplayOff`, default OFF, Settings → System → POWER — device-verified on the MSI
Claw 2026-08-13): the companion to keep-awake, which deliberately lets the display time out while
downloads
continue — and Steam plays a sound on every finished download, into a dark room. The signal is
`RegisterPowerSettingNotification(hwnd, GUID_SESSION_DISPLAY_STATUS, DEVICE_NOTIFY_WINDOW_HANDLE)` on
the existing process message-only window → `WM_POWERBROADCAST` / `PBT_POWERSETTINGCHANGE`, payload a
DWORD `MONITOR_DISPLAY_STATE` (0 off, 1 on, 2 dimmed). Microsoft documents
**`GUID_SESSION_DISPLAY_STATUS` as the one interactive user-mode apps must use** —
`GUID_CONSOLE_DISPLAY_STATE` is for services/kernel-mode and `GUID_MONITOR_POWER_ON` is the
superseded legacy setting; do not "simplify" to either. Dimmed is NOT treated as off (the screen is
still lit in front of the user). The open question was whether the notification fires at all when
the Claw's screen times out under Modern Standby; it does (device-verified 2026-08-13). The
`Display state: off/on` and `Mute on display off: …` log lines are the whole remote test surface,
so preserve them. Only a mute
WSGM applied itself is undone (a user who muted on purpose stays muted), and the service restores on
`ProcessExit` so a normal exit while the screen is dark cannot strand the device muted; a hard kill
still can, which is why the toggle defaults off. Muting goes through the native helper's APPCOMMAND
**toggle** (`WsgmVolumeCommand(8)`) after reading the current state — there is no absolute set-mute
export, so never call it without checking `WsgmVolumeGet` first.

**Keep-awake wake lock** (`Core\WakeLock.cs`, `Core\SteamDownloads.cs`, `Core\KeepAwakeDecider.cs`,
`Shell\KeepAwakeService.cs` — device-verified on the MSI Claw 2026-08-12, including the download
hold across screen-off, the manual cycle, the indicator dot, and the idle-timeout rows): a Windows
power request (`PowerCreateRequest` +
`PowerRequestSystemRequired`) that blocks standby entry while held — the display still times out
dark, but Wi-Fi and Steam keep running, which is what makes downloads survive "screen off" on a
Modern-Standby handheld. Research-settled (2026-08-12): downloads during REAL Modern Standby sleep
are impossible for a Win32 app (DAM suspends every desktop process, no opt-out), so keep-awake is
the whole feature — the same model Valve ships as SteamOS "Display-Off Downloads". Windows-documented
limits: indefinite on AC; on battery the OS force-terminates the request ~5 min after the sleep
timeout expires, and the power button always wins. Two independent holds, each its own request so
`powercfg /requests` attributes them: a **manual toggle** (quick-access Power tab, session-lifetime,
survives mode switches) and an **automatic download hold** — `KeepAwakeService` polls
`SteamClient.Downloads.RegisterForDownloadOverview` over the CEF bridge every 30 s (one-shot
subscribe/unsubscribe; fires immediately with a snapshot, live-verified; active =
`update_state != "None" && !paused`, and the Windows client's active state string is `Downloading`,
NOT decky's Linux-documented `Updating`). Release is debounced (`KeepAwakeDecider`, 2 consecutive
inactive polls) so queue gaps don't flap the hold; unreachable polls count as inactive so a dead
Steam can't pin the device awake. `CefConfig.DownloadKeepAwake` (default on, Settings row
on the Integration tab, gated by the CEF master switch AND off in `--overlay-test`, whose safe-mode
contract excludes autonomous Steam traffic) gates only the automatic side. Hold transitions and the
config apply share one gate — a disable landing mid-poll must not lose to the stale sample, or the
hold sticks for the session. The
manual side is a **three-state cycle** (Off → Standby lock → Standby+Display lock → Off), holding a
separate DisplayRequired request for the third state — acquired-before-released so a step never has
a lock gap. Preserve the `Keep awake: … hold acquired/released / manual mode …` log lines — they are
the remote test surface. The row also carries a **WakeWatch-style indicator dot** (the maintainer's
WakeWatch tray tool, deliberately the same color vocabulary): green free / yellow standby-blocked /
red display-pinned / grey unknown, computed from the system-wide power-request list —
A **"What's keeping this awake"** row below it opens the Power tab's own in-place sub-view
(`Overlay\WakeLockHoldersView.cs`, grouped by `Core\WakeLockHolders.cs`) listing every requester —
WakeWatch's right-click detail, reimplemented: dedupe on (label, detail, reason) so thirty identical
Steam requests read as `steam.exe ×30`, sorted by count then name, with the caller kind, pid, path and
reason string on the second line. It is the first sub-view that belongs to the **Power** tab rather
than Tools, so `LeaveWakeLockSubView` restores `PanelPower`, and it appears in `AnySubView`,
`DefaultFocusTarget`, `TryCancelSubView`, the tab-switch teardown and the `Activated` reset like every
other one. Unlike the summary line it deliberately does NOT hide WSGM's own request: the row above
already explains WSGM's holds, but the full list is answering "what is holding this awake" and must
not omit an answer. An unelevated read yields "couldn't read", never an empty all-clear.
`Interop\PowerRequestList.cs` calls the undocumented `NtPowerInformation(GetPowerRequestList=45)`
against ntdll directly (the documented wrapper rejects the class; needs elevation, denied → grey),
decodes the version-dependent layout through bounds-checked readers ported from WakeWatch's
`power.rs` (MIT, same author) — any structural surprise must yield grey "unknown", NEVER a false
all-clear — and `Core\WakeLockStatus.cs` maps entries to state + a collapsed holder summary
(WSGM's own pid colors the state but is excluded from the summary). Polled at 1.5 s only while the
panel is open. The Power tab also hosts four **idle-timeout rows** (screen-off / standby × battery /
plugged-in) that cycle presets via `Core\PowerTimeouts.cs` — the flat powrprof value-index API, NOT
`powercfg /q` parsing (localized output, same trap as netstat); these are a user-facing convenience
over the active scheme, deliberately not snapshotted/restored state.

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
| `WSGM.Launch` | per-game wrapper: medium-integrity child lifetime, Steam Input lease | scheduled-task launcher, `SteamInterop` mirror | shell/session UI, launch-option writing |
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

Generic rules for the languages and tooling in this repo. They apply only where the WSGM-specific
guidance above does not say otherwise — on any conflict, the project sections win. Known overrides:
WSGM's `Log` is the only logging subsystem (no Serilog or Console output); the Rust code deliberately
has no `cargo fmt` gate; device-only flows are never automated no matter what coverage goals say;
version numbers are user-owned.

## Coding conventions

- `PascalCase` for public types and members, `camelCase` for parameters and locals, `_camelCase` for
  private fields — except where a language's own conventions differ (Rust `snake_case`).
- Write clear, descriptive commit messages in English.
- `.editorconfig` is authoritative for formatting; run `eng\verify.ps1` rather than hand-applying
  formatters, and never override its line endings or indentation with a generic convention.

## Design and error handling

- Classes for stateful services and domain entities; pure functions for stateless transformations,
  validation, and decision logic. Prefer immutable data (records, readonly structs) between
  components; mutate state only inside well-encapsulated owners.
- Constructor injection for wiring services; interfaces over concrete hierarchies where behavior
  varies; model finite state with enums and pure decision functions, not boolean combinations.
- Expected failures are values (Result/Option-style returns, meaningful nullability); exceptions are
  for truly exceptional conditions. Wrap third-party errors at module boundaries into
  domain-meaningful, logged, recoverable feature state.
- Never swallow exceptions silently — log with the operation and relevant state. In retry logic,
  retry only transient failures, with backoff and a maximum count. Document at API boundaries which
  errors a function can produce and what callers should do about them.

## Performance

- Profile and measure before optimizing; optimize the critical path first; benchmark before and
  after every optimization.
- Debounce user-input-driven operations; run I/O-bound work async/off-thread; cache expensive
  computations with a deliberate invalidation story.

## Testing

- Unit-test new logic immediately; one behavior per test; Arrange-Act-Assert; fast, isolated,
  deterministic; mock external dependencies through seams; cover edge cases and error paths, not
  just the happy path.
- When a bug is found, write the failing test that reproduces it first, then fix.
- Tests stay independent of each other and of machine state. Run the full suite (via
  `eng\verify.ps1`) before committing; never merge with failing tests, and fix flaky tests instead
  of exempting them.
- xUnit: `[Fact]` for single cases, `[Theory]` + `[InlineData]`/`[MemberData]` for parameterized;
  test names follow `MethodName_Scenario_ExpectedBehavior` and are the executable specification.

## C# / NativeAOT

- Use C# 12+ features (primary constructors, collection expressions, pattern matching), file-scoped
  namespaces, and meaningful nullable annotations. `var` only when the right-hand side makes the
  type obvious.
- `async`/`await` for all I/O — never `.Result`/`.Wait()`. Omit the `Async` suffix unless both
  variants exist. Prefix interfaces with `I`. Prefer LINQ for querying collections and
  `Span<T>`/`Memory<T>` where allocation-free buffers matter.
- NativeAOT discipline: source generators instead of reflection (`[JsonSerializable]` for JSON), no
  `dynamic`, no `Assembly.Load`, annotate unavoidable reflection with
  `DynamicallyAccessedMembersAttribute`. The AOT publish (`build.ps1`) is the compatibility proof
  for every dependency choice.

## Rust (`native\*`)

- Prefer borrowing (`&T`, `&str`, slices) over cloning; `Result<T, E>` + `?` for fallible
  operations and `Option<T>` for absence; typed errors (`thiserror`-style); no `.unwrap()` in
  library code — `.expect("reason")` only for true invariants; never panic across the C ABI.
- Iterators and combinators over manual loops; `#[derive]` the common traits; `#[must_use]` where
  discarding a result is a bug; document public items with `///` including `# Errors`/`# Safety`
  where applicable.
- The gates are `cargo clippy -- -D warnings` and `cargo test` (run via the `eng\build-*.ps1`
  scripts with `-Validate`). There is deliberately no fmt gate — do not reformat untouched code.
- Unit tests inline under `#[cfg(test)]`; integration tests in `tests/`.

## PowerShell (`eng\`, build scripts)

- Approved Verb-Noun names, `[CmdletBinding()]`, typed `param()` blocks with validation attributes;
  full cmdlet names, no aliases; `Join-Path` for path construction.
- `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` at the top; typed
  `try`/`catch`, never an empty `catch {}`; `Write-Error -ErrorRecord $_` to re-throw after logging.
- `[System.Collections.Generic.List[object]]` + `.Add()` instead of array `+=`; batch filesystem
  queries instead of per-item `Test-Path` loops; avoid `Invoke-Expression`.

## GitHub Actions

- Every job gets `timeout-minutes`; every step gets a `name:`; workflow-level `permissions:` kept
  minimal (default read-only, widen per job only as needed).
- Cache dependencies keyed on lockfile hashes with `restore-keys` fallback; prefer the setup
  actions' built-in caching; never cache ephemeral outputs (logs, test reports).
- Concurrency groups with `cancel-in-progress` for PR workflows; path filters to skip irrelevant
  runs; parallelize independent jobs and chain the rest with `needs:`.
- Keep third-party actions pinned and updated (Dependabot); never combine `pull_request_target`
  with a checkout of the PR head.

## Security

- Validate untrusted input with explicit bounds, size caps, and decode budgets — the splash-theme
  defense set above is the model.
- Never hardcode secrets; never concatenate untrusted input into command lines, queries, or
  injected script text (JSON-encode, as the CEF bridge does).
- Judge findings against WSGM's accepted security posture and trust model (see Gotchas and Agent
  Review Rules) before proposing a fix; treat high-severity dependency CVEs as release blockers.

## Collaboration

- Keep changesets cohesive; state their rationale and test plan; keep formatting-only work separate
  from behavior changes when practical. Reviews follow the Agent Review Rules above.
- Decompose features into small, independently verifiable slices; separate research spikes from
  implementation; record known debt as follow-up tasks instead of expanding scope.
- Keep this file current (see the header) and prune stale guidance — outdated instructions are
  worse than none.
