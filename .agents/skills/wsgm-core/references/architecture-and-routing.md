# Architecture and routing

## Process topology

WSGM is a self-contained managed-JIT `net10.0-windows` x64 CoreCLR application. It loads the sole
installed device plugin in-process through a collectible `AssemblyLoadContext`. NativeAOT and the
old out-of-process DeviceHost/IPC design are retired; do not resurrect them from historical plans.

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
```

The installer, logon service, launcher, resident UI, native Steam Input shim, and reusable/device
submodules are different authority and lifetime boundaries. `BootManifest` is the bounded untrusted
same-user projection the service consumes. WSGM starts with a linked user token; an unlinked token
is retained for Explorer recovery. The watchdog owns the process handle, waits for the shell-anchor
grace, may launch Explorer once after dirty/unknown exit, and never relaunches WSGM. Do not collapse
these boundaries for convenience or reject service boot merely because Explorer exists initially.

## Application directories

| Area                                  | Owns                                                                                                             | Does not own                                                |
| ------------------------------------- | ---------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------- |
| `src/WSGM/Program.cs`, `App.axaml.cs` | mode selection, pre-UI one-shots, Avalonia/application composition                                               | feature policy or live resource ownership                   |
| `src/WSGM/Core`                       | nonvisual product policy, config, recovery, package policy, Steam/RTSS helpers, persistent-state decisions       | view presentation or session-wide manager sprawl            |
| `src/WSGM/Shell`                      | Explorer/session transitions, live managers, integration reconciliation, destructive storage workflows, teardown | reusable SDK/library contracts or view-owned policy         |
| `src/WSGM/Settings`                   | settings view models/pages, validation, save intent and handoffs                                                 | long-lived hardware/Steam/input acquisition                 |
| `src/WSGM/Overlay`                    | quick-access views, navigation, projections, user intent                                                         | device protocols, config authority, global manager lifetime |
| `src/WSGM/Input`                      | canonical gamepad flow, UI capture, target encoders and output routing                                           | physical device protocol or HidHide policy inside a plugin  |
| `src/WSGM/Interop`                    | narrow native declarations/adapters                                                                              | product decisions or presentation                           |
| `src/WSGM/Controls`, `Themes`         | reusable Avalonia presentation primitives and styling                                                            | service orchestration                                       |
| `src/WSGM.Launch`                     | launch wrapper, de-elevation, input-lease containment                                                            | resident shell services                                     |
| `src/WSGM.LogonService`               | minimal logon trigger/watchdog contract                                                                          | user feature behavior                                       |

`ShellSession` is composition root, not permission to implement every feature in one file. A service
with independent state/resource ownership should remain a focused manager, rooted and ordered by the
session.

## External ownership

| Concern                                            | Repository/path                      |
| -------------------------------------------------- | ------------------------------------ |
| Semantic device-plugin contract                    | `external/WSGM.Device.Sdk`           |
| Hardware authoring/evidence tool                   | `external/WSGM.DeviceLab`            |
| MSI Claw device behavior                           | `external/WSGM.Device.Msi.Claw8A2Vm` |
| Reusable Steam CEF transport/patch/surfaces        | `external/steam-ui-toolkit`          |
| Reusable Windows radio/audio/brightness primitives | `external/windows-device-control`    |
| Native Steam Input shim/lease                      | `native/SteamInput`                  |

WSGM owns policy, orchestration, session state, and adapters. Do not mirror child source into the
main project. A cross-repository change is committed and pushed leaf first, then each parent gitlink
is advanced to an already published commit.

## Documentation router

Start at `docs/README.md`; then use:

| Task                                                      | Primary documents                                                 | Skill                                                     |
| --------------------------------------------------------- | ----------------------------------------------------------------- | --------------------------------------------------------- |
| boot, Explorer, desktop/game transition, update/uninstall | `boot-and-shell.md`, `elevation.md`                               | `wsgm-core`                                               |
| overlay, gamepad navigation, touch, UI                    | `overlay-and-input.md`, `ui.md`                                   | `wsgm-core`                                               |
| config/product decisions                                  | `decisions.md`, relevant mechanism doc                            | `wsgm-core`                                               |
| RTSS, frametimes, AutoTDP                                 | `rtss.md`                                                         | `wsgm-core`                                               |
| display, power, wake locks                                | `power-and-display.md`                                            | `wsgm-core`                                               |
| Wi-Fi, Bluetooth, audio                                   | `radios.md` and windows-device-control docs                       | `wsgm-core` plus child guidance when library code changes |
| SD cards                                                  | `sd-cards.md`                                                     | `wsgm-core`; live formatting remains explicitly attended  |
| Steam CEF/QAM patches                                     | `steam-cef-system.md`, `steam-cef.md`, toolkit reference          | Steam CEF skills                                          |
| device host and SDK contract                              | `device-integration.md`, `device-plugin-system.md`, SDK reference | `wsgm-device-sdk`                                         |
| new hardware discovery                                    | Device Lab README, device plan/provenance                         | `wsgm-device-lab`                                         |

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
