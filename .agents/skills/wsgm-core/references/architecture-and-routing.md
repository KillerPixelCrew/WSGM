# Architecture and routing

## Process topology

WSGM is a self-contained managed-JIT `net10.0-windows10.0.19041.0` x64 CoreCLR application.
Supporting projects that need no WinRT/WindowsDeviceControl API use `net10.0-windows`. With Device
Integration enabled, WSGM creates the exact detected LibHandheld definition using one captured
identity snapshot. NativeAOT and the old out-of-process DeviceHost/IPC design are retired; do not
resurrect them from historical plans.

The current high-level flow is:

```text
WSGM.LogonService / explicit command
  -> Program.Main mode and pre-UI one-shots
  -> Avalonia application composition
  -> ShellSession for the resident shell mode
       -> one owner per live manager/integration
       -> Settings/Overlay projections and intent
       -> ordered restoration and shutdown

WSGM.Launch
  -> de-elevated/per-game launch and Steam Input lease containment

WSGM.PackagedLaunch
  -> activation, supervision and overlay routes for an imported packaged game
  -> or injection-free following of a game started by another launcher

WSGM.Setup + WSGM.Install
  -> hardware/package offers, user answers, file transaction and component installation
```

The installer, logon service, launchers, resident UI, native Steam Input shim, reusable submodules
and built-in hardware libraries are different authority and lifetime boundaries. `BootManifest` is
the untrusted same-user projection the service consumes. WSGM starts with the interactive user
token, using its linked elevated token only when requested by the manifest and available; the
unlinked token is retained for Explorer recovery. The watchdog owns the process handle, waits for
the shell-anchor grace, may launch Explorer once after dirty/unknown exit, and never relaunches
WSGM. Do not collapse these boundaries for convenience or reject service boot merely because
Explorer exists initially.

## Application directories

| Area                                                       | Owns                                                                                                             | Does not own                                                |
| ---------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------- |
| `src/WSGM/Program.cs`, `StartupOptions.cs`, `App.axaml.cs` | mode precedence, pre-UI one-shots, process Steam Input owner and Avalonia/application composition                | feature-specific orchestration                              |
| `src/WSGM/Core`                                            | nonvisual product policy, config, recovery, package policy, Steam/RTSS helpers, persistent-state decisions       | view presentation or session-wide manager sprawl            |
| `src/WSGM/Shell`                                           | Explorer/session transitions, live managers, integration reconciliation, destructive storage workflows, teardown | reusable SDK/library contracts or view-owned policy         |
| `src/WSGM/Settings`                                        | settings view models/pages, validation, save intent and handoffs                                                 | long-lived hardware/Steam/input acquisition                 |
| `src/WSGM/Overlay`                                         | quick-access views, navigation, projections, user intent                                                         | device protocols, config authority, global manager lifetime |
| `src/WSGM/Input`                                           | canonical gamepad flow, UI capture, target encoders and output routing                                           | physical device protocol or HidHide policy inside a plugin  |
| `src/WSGM/Interop`                                         | narrow native declarations/adapters                                                                              | product decisions or presentation                           |
| `src/WSGM/Controls`, `Themes`                              | reusable Avalonia presentation primitives and styling                                                            | service orchestration                                       |
| `src/WSGM.Launch`                                          | launch wrapper, de-elevation, input-lease containment                                                            | resident shell services                                     |
| `src/WSGM.PackagedLaunch`                                  | packaged-game activation, supervision, containment and the two overlay routes                                    | library discovery, shortcut writing or WSGM policy          |
| `src/WSGM.LogonService`                                    | minimal logon trigger/watchdog contract                                                                          | user feature behavior                                       |
| `src/WSGM.Setup`, `src/WSGM.Install`                       | setup UI/engine, shared bundle/offers/layout, file rollback and component installation                           | normal-session policy or live runtime feature ownership     |
| `src/Shared`                                               | source-linked cross-executable contracts and native/process primitives                                           | an independently deployed service or assembly               |

`ShellSession` is composition root, not permission to implement every feature in one file. A service
with independent state/resource ownership should remain a focused manager, rooted and ordered by the
session.

## Existing components to reuse

- Configuration: `Core/ConfigStore.cs` owns strict reads, the cross-process lock and atomic writes.
  Use the page's owned-field save path and `Core/WsgmSharedSettings.cs` when Settings and Steam edit
  the same field. External work happens after the config lock is released.
- Bounded network reads: `Core/BoundedHttp.cs` checks body size and read-stall timeouts. Reuse it
  for an HTTP body; carry caller cancellation and an explicit size limit rather than introducing an
  unbounded stream copy or retry loop.
- Launching: use `Core/AppLauncher.cs` for configured application/protocol activation and
  `Core/UnelevatedLauncher.cs` for the existing scheduled-task route. `src/Shared/Process` contains
  command-line, scheduled-task XML, parent-start, session-name and log primitives compiled by their
  declared consumers. Check the project includes before changing a shared source file. Game-tree
  containment stays with `WSGM.Launch` or `WSGM.PackagedLaunch`; Explorer recovery stays with
  `ExplorerDesktopHost` and its anchor rather than a generic process-start helper.
- Lifetime: compose the feature in its existing session manager, close admission through the
  session's cancellation path and join it through `ShellSession.Shutdown.cs` and
  `Core/ApplicationShutdown.cs`. Reuse that one shutdown request/task and its deadline; do not add
  an independent exit loop or dispose dependencies while work still uses them.
- Views: reuse `Controls` components such as `ActionButton`, `CollapsibleSection`, `TabStrip`,
  `CurveEditor` and `OnScreenKeyboard` when their interaction matches the feature. Reuse the current
  view model and navigation route before creating a second editor. Controls report intent and keep
  focus/automation behavior; their feature owner handles persistence and side effects.
- Styles: `Themes/Palette.axaml`, `Typography.axaml`, `Shared.axaml` and the owning control theme
  provide shared tokens. `CommandDeck.axaml` owns overlay `Deck*` tokens. Use the existing theme and
  focus treatment rather than page-local color, spacing or control copies.

Prefer a focused helper beside its real consumers over a new framework or catch-all utility file.
Extract only behavior that actually shares a contract; keep policy in its established owner. New
Steam/CEF features also need the same feature/workflow in the WSGM Overlay, with one state and
command owner. Use the specialized Steam CEF skill for the Steam component catalog, visual rules and
parity review.

## Project ownership

| Concern                                                  | Repository/path                                                             |
| -------------------------------------------------------- | --------------------------------------------------------------------------- |
| Semantic device-plugin contract                          | `external/libhandheld/src/LibHandheld/Contracts`                            |
| Common plugin contract, instances and extension surfaces | `src/WSGM.Plugin.Sdk`                                                       |
| Hardware authoring/evidence tool                         | `src/WSGM.DeviceLab`                                                        |
| Handheld Companion scaffold (unfinished)                 | `src/WSGM.Device.HandheldCompanion`                                         |
| MSI Claw device behavior                                 | `external/libhandheld/src/LibHandheld/Families/MsiClaw`                     |
| ROG Ally device behavior and recorded evidence status    | `external/libhandheld/src/LibHandheld/Families/RogAlly`                     |
| Built-in graphics driver engines                         | `external/libgpu-driver-interact`; WSGM adapter and coordinator in `Shell/` |
| IR session actions and firmware                          | `src/WSGM.Plugin.Ir`                                                        |
| Reusable live-backdrop rendering                         | `src/Avalonia.LiveBackdrop`                                                 |
| Reusable Steam CEF transport/patch/surfaces              | `external/steam-ui-toolkit`                                                 |
| Reusable Windows radio/audio/brightness/power primitives | `external/windows-device-control`                                           |
| Native Steam Input shim/lease                            | `external/steam-input-lease`                                                |
| VIIPER virtual controller library                        | `external/viiper`                                                           |
| Vendored LoadingIndicators.Avalonia source               | `external/LoadingIndicators.Avalonia`                                       |
| Controller dependency lock, licences, and VIIPER notes   | `external/controller`                                                       |

WSGM owns policy, orchestration, session state, and adapters. Device projects share this repository
and one SDK project reference. Keep them separate assemblies. Only the reusable libraries, Steam
Input, and VIIPER remain submodules; publish a child change before advancing its WSGM gitlink.

## Documentation router

Start at `docs/README.md`; then use:

| Task                                                      | Primary documents                                                         | Skill                                                     |
| --------------------------------------------------------- | ------------------------------------------------------------------------- | --------------------------------------------------------- |
| boot, Explorer, desktop/game transition, update/uninstall | `boot-and-shell.md`, `elevation.md`, `setup.md`                           | `wsgm-core`                                               |
| topology, build, generated output                         | `architecture.md`, `source-map.md`, `development.md`                      | `wsgm-core`                                               |
| imported/followed game lifetime                           | `game-library.md`, `packaged-game-launcher.md`                            | `wsgm-core`                                               |
| overlay, gamepad navigation, touch, UI                    | `overlay-and-input.md`, `ui.md`                                           | `wsgm-core`                                               |
| config/product decisions                                  | `configuration.md`, `profiles.md`, `decisions.md`, relevant mechanism doc | `wsgm-core`                                               |
| RTSS, frametimes, AutoTDP                                 | `rtss.md`, `autotdp-controller.md`                                        | `wsgm-core`                                               |
| display, power, wake locks                                | `power-and-display.md`                                                    | `wsgm-core`                                               |
| Wi-Fi, Bluetooth, audio                                   | `radios.md` and windows-device-control docs                               | `wsgm-core` plus child guidance when library code changes |
| SD cards                                                  | `sd-cards.md`                                                             | `wsgm-core`; live formatting remains explicitly attended  |
| Steam CEF/QAM patches                                     | `steam-cef-system.md`, `steam-cef.md`, toolkit reference                  | Steam CEF skills                                          |
| device host and SDK contract                              | `device-integration.md`, `device-plugin-system.md`, SDK reference         | `wsgm-device-sdk`                                         |
| new hardware discovery                                    | Device Lab README, device plan/provenance                                 | `wsgm-device-lab`                                         |

`_plan/implementation-todo.md` is the progress tracker. Requirements and dated findings are not a
second progress counter. Reconfirm drift-prone hardware/Steam facts before changing behavior.

## Standing decisions that shape architecture

- WSGM no longer registers itself as the HKCU Winlogon shell. Explorer-first service boot is the
  established path; recovery remnants exist to undo old installs, not as a new activation route.
- The resident process is elevated intentionally so Steam Input/overlay reach elevated games;
  desktop transitions must restore a normal unelevated Explorer.
- Per-user config and inputs remain per-user even though the process is elevated.
- `Local\WSGM.ExitForUpdate` and `Local\WSGM.ExitForUninstall` are cross-version contracts.
- There is one config file and one cross-process config lock.
- WSGM is not a controller remapper; Steam owns general remapping and WSGM's OEM action vocabulary
  remains closed.
- AutoTDP control is frametime-first. Utilization is explanatory telemetry, not its control signal
  or a persistent power floor.
