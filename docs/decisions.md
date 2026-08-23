# Standing decisions and accepted trade-offs

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

## Gotchas

- The installer (`installer\WSGM.iss`) is now **`PrivilegesRequired=admin`** (the machine service
  demands it) while the app stays per-user — deliberate single-user-device design: `{localappdata}`
  and HKCU belong to the elevating account, documented in the .iss header. Update/uninstall ordering
  is load-bearing: `[Code]` stops the **logon service first** (`sc stop` — a live watchdog would see
  the killed WSGM and start explorer mid-update, flipping the restart into desktop mode; also frees
  the Program Files binary, including an abandoned preview's — same service name), then signals
  `Local\WSGM.ExitForUpdate` (one SetEvent releases every instance, including elevated ones), waits
  bounded on the shell mutex, taskkill fallback — and restarts WSGM in its previous mode
  (shell-mutex check taken _before_ killing → `--shell`, else settings). `[Run]` order: `--setup`
  (per-user files, migrate off any legacy shell registration, Xbox-FSE guard, boot manifest) then
  `WSGM.LogonService.exe --install` (create-or-reconfigure + failure actions + start).
  `[UninstallRun]` order: service `--uninstall` (stop+delete) → `--unregister-shell` (legacy no-op
  on service installs) → `--uninstall-restore`, all before files are deleted; `[UninstallDelete]`
  also removes `{autopf}\WSGM` and `{commonappdata}\WSGM`. Interactive upgrades return `True` from
  Inno's `NeedRestart`; silent upgrades must return `False`, because `/VERYSILENT` otherwise reboots
  automatically unless its caller supplied `/NORESTART`.
- The direct HKCU Winlogon shell replacement was **retired** (2026-08): running the session without
  Explorer ever initializing broke touch features, and the Explorer-first service boot is the
  device-verified fix. `ShellRegistration.Install` is legacy-only (no caller); `Uninstall`, the
  snapshot fields in config.json, auto mode, and `--unregister-shell` remain for migrating installed
  devices — do not remove them while shell-registered installs exist in the field, and do not
  re-register WSGM as the shell from any new code path.
- Elevated processes started by WSGM inherit elevation — that inheritance is the point of
  self-elevation: an elevated WSGM yields an elevated **Steam**, which is what lets Steam Input
  reach elevated windows and the Steam Overlay inject into elevated games (UIPI blocks both
  otherwise); WSGM's own overlay/edge swipes over elevated windows ride the same chain. The flip
  side: an **elevated explorer breaks UWP** (touch keyboard, store apps) — that's what invariant 5
  protects.
- **Accepted security posture (do not "fix" by contorting the design):** the whole class of
  same-user medium→high findings is accepted by design. WSGM is a per-user shell that deliberately
  self-elevates and runs user-configured inputs — the boot manifest (`boot.json`), live
  `config.json` reload / auto-relaunch, `SelfElevation` launching configured home/startup apps, the
  HKCU-sourced Steam path, the co-located helper DLL (`WSGM.VolumeControl.dll`) and the install→run
  handoff all live in `%LOCALAPPDATA%`, which same-user medium-IL code already owns. UAC is not a
  Microsoft security boundary, so a same-user attacker who can pre-plant those inputs is out of
  scope. Do NOT relocate the install, add per-action UAC prompts, code-sign-gate DLL loads, or
  otherwise trade the design/UX to close these — a reviewer re-raising them should be answered
  "accepted by design," not patched. What IS worth doing (and already done) is _transparent_
  hardening that costs nothing: absolute `System32` paths for launched system tools
  (`ConsoleTool.System32`), reparse-point/ no-follow/no-overwrite checks before elevated file ops in
  user-writable dirs, DACL-scoping named kernel objects to the current user, and bounds/decode-size
  checks on parsed untrusted input.
- **The update-exit event (`Local\WSGM.ExitForUpdate`) is an accepted same-user capability, and it
  is neither availability-only nor harmless.** `Core\UpdateExitWatcher` creates it with an explicit
  DACL granting this user's SID and `BUILTIN\Administrators` `EVENT_MODIFY_STATE | SYNCHRONIZE`
  (`0x00100002`) plus a medium mandatory label, so any same-user medium process — not only the
  elevated setup — can signal it. Two consequences are accepted, and a review must state them
  correctly rather than downgrading them:
  - **It is not availability-only.** The callback (`Program.Main`'s `UpdateExitWatcher.Start`
    handler) runs `Steam.StopForUpdate()`, which activates `steam://exit` through
    `AppLauncher.StartProtocol` → `Process.Start(… UseShellExecute = true)` (`Core\AppLauncher.cs`).
    WSGM is normally elevated in game mode, so the shell resolves that protocol's **HKCU** handler
    with WSGM's token. The HKCU hijack itself is already accepted above (same-user medium→high,
    `%LOCALAPPDATA%`/HKCU are the attacker's own store); what the event adds is only _timing
    control_ over when the pre-planted handler runs. Do not describe this path as a mere shutdown
    request.
  - **It does leave the session desktop-less.** The graceful path ends with `lifetime.Shutdown()`
    and therefore **exit code 0**, while the logon service starts its Explorer fallback only on a
    dirty exit (`WSGM.LogonService\SessionLauncher.Watch`:
    `dirtyExit = !exitKnown || waitResult != WAIT_OBJECT_0 || exitCode != 0`). A signal delivered
    outside a real update thus ends the shell with no Explorer behind it until the user signs out.
    This is not a new capability — same-user medium code can already loop `taskkill` on the
    unelevated Explorer — but the doc must not claim the fallback covers it.
  - **The hardening that is kept is the cheap kind, and the grant must not be narrowed.** The medium
    label keeps low-IL/sandboxed code from signalling at all, and the watcher's `ResetEvent` at
    start drops a stale signal so a relaunched instance does not shut itself straight back down. Do
    **not** tighten the DACL: the unelevated Settings instance needs `EVENT_MODIFY_STATE` for that
    reset and for the `OpenEventW` fallback, and "one `SetEvent` releases every instance" is the
    contract `StopRunningInstances` in `installer\WSGM.iss` depends on. The event name and the
    `0x00100002` mask are a **cross-version** contract — during an upgrade the object is created by
    the OLD build and the new installer only opens it by name — so drifting either one silently
    breaks graceful update shutdown and leaves the injected Steam Input payload mapped.
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
  `src\WSGM\Licenses\`. `FluentAvaloniaUI` 3.0.2 and an explicit `Avalonia.Controls.ColorPicker`
  12.1.1 pin keep the controls on the same Avalonia line.
