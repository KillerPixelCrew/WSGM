# Standing decisions and accepted trade-offs

Product-level decisions the other docs assume. Each entry says what was decided and why, and points
to the doc that holds the mechanism. Nothing here is a how-to; when a decision and a mechanism doc
disagree, fix the mechanism doc.

**The installer is admin; the app stays per-user.** `installer\WSGM.iss` is
`PrivilegesRequired=admin` because the machine service demands it, while `{localappdata}` and HKCU
belong to the elevating account. This is a single-user-device design. The update and uninstall
ordering, the exit events and the `NeedRestart` rule live in `docs\boot-and-shell.md`, "Install,
update and uninstall".

**The HKCU Winlogon shell replacement is retired.** Running the session without Explorer ever
initializing broke touch features; the Explorer-first service boot is the device-verified fix
(2026-08). 2.0 deleted the registration path, with no install code and no auto mode.
`ShellRegistration.Uninstall`, the snapshot fields in config.json and `--unregister-shell` remain
only as an install's own recovery and the uninstaller's restore. Do not re-register WSGM as the
shell from any new code path.

**Processes WSGM starts inherit its elevation, and that is the point.** An elevated WSGM yields an
elevated Steam, which lets Steam Input reach elevated windows and the Steam Overlay inject into
elevated games; UIPI blocks both otherwise. WSGM's own overlay and edge swipes over elevated windows
ride the same chain. The cost is that an elevated Explorer breaks UWP (touch keyboard, store apps),
which is what de-elevation protects. See `docs\elevation.md`.

**Per-user inputs stay per-user.** The boot manifest, live configuration, HKCU Steam registration
and the install-to-run handoff stay in the user's profile even though WSGM elevates. Keep validation
that improves correctness without changing that model: absolute system-tool paths, no-follow and
no-overwrite file operations, correctly scoped kernel objects, bounded external-data parsers. Do not
add publisher tiers or per-action prompts for inputs the same user already controls.

**The update and uninstall exit events are a cross-version contract.** The names
`Local\WSGM.ExitForUpdate` and `Local\WSGM.ExitForUninstall`, their access grant, label and
stale-signal reset must stay compatible with older running builds. Update asks Steam to exit
normally; a Steam client or launch wrapper that remains is a setup refusal, never a process kill.
Details in `docs\boot-and-shell.md`.

**Windows owns device posture and automatic touch-keyboard policy.** Game and desktop mode never
capture or write `ConvertibleSlateMode` or `TouchKeyboardTapInvoke`.

**The volume OSD never interrupts an exclusive game.** The physical volume command is always applied
in game mode. The indicator is non-activating and click-through, and is suppressed only for a
confirmed `QUNS_RUNNING_D3D_FULL_SCREEN` from `SHQueryUserNotificationState`, or an absent or locked
session. `QUNS_BUSY` stays allowed: Steam Big Picture and borderless fullscreen report it.

**One config file, one lock.** Config lives at `%LOCALAPPDATA%\WSGM\config.json`
(`Core\ConfigStore`, System.Text.Json source generation; a new scalar property needs no context
change). The registry snapshots inside it belong to the install lifecycle and feature code never
clobbers them. `ConfigStore.AcquireLock()` is the cross-process scope: the Settings save transaction
holds it across the config write and the splash-asset promotion, while the multi-megabyte image
copies happen outside it (sidecars are per-transaction unique). Nested acquisition on one thread is
free; do not reintroduce stacked 2 s timeouts.

**WSGM is not a controller remapper.** OEM buttons are bound in plugin code, and the closed
`OemAction` vocabulary has no authoring UI on purpose. Every handheld on the market today maps
cleanly onto a Steam Deck controller with no buttons or functions left over, so a rebinding surface
would answer a problem no supported device has while making WSGM responsible for input policy that
belongs to Steam. See `docs\device-plugin-system.md`, "OEM controls".

**A device control the user moves is remembered.** A `User` capability write the device accepted is
stored as the desired value of the layer that press means — the running application's when a game is
running, the global default otherwise. The sustained power limit and variable refresh are the two
exceptions, stored under `Performance` because that owner also decides how each is released when an
application closes; one value never gets two homes. Mechanism in `docs\device-plugin-system.md`,
§11.

**Toolchain pins.** .NET 10 and Avalonia 12.1.1. `LoadingIndicators.Avalonia` is vendored under
`third_party\LoadingIndicators.Avalonia` and built from source, because its published Avalonia 11
package has precompiled XAML that fails on Avalonia 12; its Unlicense text ships from
`src\WSGM\Licenses\`. `FluentAvaloniaUI` 3.0.2 and an explicit `Avalonia.Controls.ColorPicker`
12.1.1 pin keep the controls on the same Avalonia line.
