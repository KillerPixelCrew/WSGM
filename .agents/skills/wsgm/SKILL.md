---
name: wsgm
description:
  Plan, implement, review or document work anywhere in WSGM. Start here for project architecture,
  source ownership, reusable building blocks, UI parity, maintenance rules and routing to the Core,
  Steam CEF, Device SDK or Device Lab skill. Use before deciding where a feature belongs or adding a
  new service, control, project or abstraction.
---

# WSGM

Build a Windows gaming session that remains understandable and recoverable. Put each behavior in its
existing owner, reuse the project's contracts and controls, and make the same feature available in
the overlay and Steam Big Picture. The maintainer's instructions take precedence over this guide.

## Start from the current checkout

1. Resolve the repository with `git rev-parse --show-toplevel`. Read the root and nearest
   `AGENTS.md`, inspect `git status --short --branch` and `git submodule status --recursive`, and
   preserve unrelated work.
2. Read [architecture](../../../docs/architecture.md) for the runtime flow and
   [source map](../../../docs/source-map.md) for every project and source area. `WSGM.slnx` and
   tracked source are authoritative; `_plan`, `_ref` and dated observations are context.
3. Read [product decisions](../../../docs/decisions.md) and the owning guide linked by
   [docs/README.md](../../../docs/README.md). Inspect the actual producer, state owner, effect and
   consumers before changing them. A historical hardware pass is not new validation.
4. Choose the specialist below, then describe the smallest complete change through both user
   surfaces, its failure behavior and cleanup. Do not begin by creating a new abstraction.

## Choose the owner and specialist

| Work                                                                                    | Home and boundary                                                                                                                            | Skill                                                                                                                                                                      |
| --------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Process modes, configuration, resident services, session transitions or shutdown        | `src/WSGM/Program.cs`, `Core/` and `Shell/`; `App.axaml.cs` composes the UI lifetime                                                         | [WSGM Core](../wsgm-core/SKILL.md)                                                                                                                                         |
| Overlay, Settings, navigation, styling or ordinary application input                    | `Overlay/`, `Settings/`, `Controls/`, `Themes/`, `Input/`; views express intent through existing owners                                      | [WSGM Core](../wsgm-core/SKILL.md), then [UI and overlay parity](../wsgm-steam-cef-toolkit/references/ui-and-overlay-parity.md) when Steam is involved                     |
| Add or change a Steam page, QAM row, native setting, patch, bridge contract or frontend | Reusable mechanics in `external/steam-ui-toolkit`; WSGM policy/adapters in `Shell/`, WSGM-only feature logic/frontend fragments in `Core/`   | [Steam CEF toolkit](../wsgm-steam-cef-toolkit/SKILL.md)                                                                                                                    |
| Diagnose a Steam frontend or attachment failure                                         | Start with logs and the failed lifecycle boundary; use current-run Steam startup evidence before any live debugger tool                      | [Steam CEF debugging](../wsgm-steam-cef-debugging/SKILL.md)                                                                                                                |
| Device lifecycle, capabilities, controller reports or package authoring                 | `WSGM.Device.Sdk` contracts, `Shell/Device*` host policy, machine behavior in `WSGM.Device.*`                                                | [Device SDK](../wsgm-device-sdk/SKILL.md)                                                                                                                                  |
| Unknown hardware, OEM protocols, controller/motion/haptics evidence                     | `WSGM.DeviceLab`; its knowledge and reports are evidence, not runtime hardware drivers                                                       | [Device Lab](../wsgm-device-lab/SKILL.md)                                                                                                                                  |
| Common plugin, session action or GPU driver feature                                     | `WSGM.Plugin.Sdk`, `Shell/CommonPlugin*`, `Shell/GpuCoordinator` and `WSGM.Plugin.*`; independent of the singleton device slot               | [Plugin system](../../../docs/plugin-system.md), [Plugin SDK reference](../../../src/WSGM.Plugin.Sdk/docs/reference.md), and Core/Device SDK as appropriate                |
| Reusable Windows audio, radios, brightness, display or power primitive                  | `external/windows-device-control`; the caller owns UI, user policy and persistent recovery                                                   | [Windows library](../../../external/windows-device-control/docs/README.md)                                                                                                 |
| Installation, sign-in service or game wrappers                                          | `WSGM.Setup`, `WSGM.Install`, `WSGM.LogonService`, `WSGM.Launch`, `WSGM.PackagedLaunch`; preserve their distinct token and process lifetimes | [Development](../../../docs/development.md), [setup](../../../docs/setup.md), [boot](../../../docs/boot-and-shell.md), [launcher](../../../docs/packaged-game-launcher.md) |

The product is a .NET 10 Windows application with an Avalonia frontend and a Steam CEF frontend.
`Program.Main` selects the mode and handles early recovery; `App` constructs one resident
`ShellSession` or standalone Settings. The session composes live owners, admits plugins and
coordinates mode changes and shutdown. The logon service consumes the small `boot.json` contract,
not the full application configuration. Installed plugins run in-process and are trusted code.

## Place code at the boundary it owns

- **Core**: nonvisual product policy, configuration, persistence, validation and recovery
  primitives. **Shell**: live managers, session orchestration, policy application and integration
  adapters. A coordinator can compose focused owners without accumulating every feature's
  implementation.
- **Overlay and Settings**: render current state, collect user intent, validate editor input and
  hand the operation to its owner. Settings configures WSGM; Windows or external-state controls
  belong on the overlay's relevant page and Steam's Quick Access, subject to the recorded product
  exceptions.
- **Controls and Themes**: reusable presentation, styles and tokens. **Input**: canonical reports,
  navigation/capture and virtual target routing. **Interop**: narrow ABI declarations and native
  handles, without product decisions.
- **Device package**: its machine's protocol and semantic capabilities. **Device SDK**: contracts
  another package can implement. The host owns profiles, controller targeting, HidHide and policy.
  Device Integration off runs no device lifecycle, controller target, device writes or AutoTDP;
  independent common/GPU plugins and Windows features remain usable.
- **Reusable submodule**: behavior useful to another consumer, with no WSGM configuration or UI
  dependency. SteamUiToolkit owns transport/bridge/patch/surface mechanics; WindowsDeviceControl
  owns Windows primitives. Keep WSGM adapters in WSGM instead of copying library code.
- **Shared source**: `src/Shared` contains explicitly linked contracts used by multiple binaries.
  Check every consuming `.csproj` before changing one. Native payload directories and generated
  frontend bundles are build outputs, not alternate implementation homes.

## Reuse the existing building blocks

Use [the source map](../../../docs/source-map.md) to find each feature's current owner. For common
needs, start here:

| Need                                  | Existing element and rule                                                                                                                                                                                                                                                    |
| ------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Persist preferences or recovery state | `ConfigStore.Transaction` / `Update`, generated `ConfigJsonContext`, normalization and migration. Read fresh state under the writer lock; refuse corrupt/unreadable state; keep transactions on their acquiring thread. See [configuration](../../../docs/configuration.md). |
| Apply Global/per-game state           | `ProfileService`, its resolver, existing profile fan-out and capability owners. Do not write a second inheritance algorithm in a view.                                                                                                                                       |
| Own long-running work                 | A focused session-owned manager with explicit start/admission/cancellation/disposal; join the existing `ApplicationRuntime` shutdown path. Do not start anonymous process-long tasks from a control.                                                                         |
| Apply hardware or driver settings     | Existing device/GPU capability routers, typed outcomes and write admission, or the narrow Windows library API. Preserve partial/uncertain outcomes; never blindly repeat a potentially completed write.                                                                      |
| Share frontend actions and state      | One backend and detached projections for Avalonia and Steam. Use toolkit module/surface builders, bridge vocabulary and existing host registration. See [reusable Steam elements](../wsgm-steam-cef-toolkit/references/reusable-elements.md).                                |
| Build UI                              | Established view/row renderers, descriptor controls, tokens, native Steam controls and navigation patterns. Follow [UI guidance](../../../docs/ui.md) and [surface parity/style guide](../wsgm-steam-cef-toolkit/references/ui-and-overlay-parity.md).                       |
| Input ownership                       | Existing process Steam Input blocker, named Hold/Drop claims, `OverlayController`, focused Settings ownership and controller coordinator. Do not acquire a separate lease from a feature control.                                                                            |
| Files, network and diagnostics        | Existing bounded HTTP/download gates, format/path validation, atomic publication and owning logger. Find the matching feature's helper before adding another generic utility. Log transitions, not high-rate samples or credentials.                                         |

## Keep changes simple and maintainable

Trace one complete flow: user intent or observation, validation, one state owner, one effect
adapter, result, both projections, persistence when needed, and cleanup. Extend that flow before
adding another manager, event bus, cache, registry or transport.

Prefer small named functions, explicit data and ordinary control flow. Extract a shared helper when
there is an actual shared contract or duplicated behavior, not to anticipate imagined consumers.
Keep a useful abstraction at the smallest responsible boundary. Avoid compatibility layers, generic
frameworks, pass-through wrappers and parallel state stores unless the task demonstrates a need.

One fact has one owner. Share policy, validators, identity, capability availability and command
results; keep surface rendering separate. Do not make a device plugin know about overlay layout, a
Windows primitive know about `AppConfig`, or a React component own hardware lifetime. Comment the
reason, contract, ownership and non-obvious failure behavior; use clear names for the ordinary
steps. Public C# XML documentation and the frontend contracts are part of the implementation.

## Deliver the same functionality in both interfaces

Every feature added to Steam Big Picture must also be available in the WSGM overlay. Reuse the same
backend, capabilities, options, actions, state and failure meanings. Use the established layout and
navigation of each surface; matching functionality does not require copying Steam's DOM into
Avalonia. Include loading/unavailable states, disabled integrations, cancellation and disposal in
the design for both surfaces. A Steam-only implementation is unfinished unless the maintainer
explicitly narrows the task.

Follow [UI and overlay parity](../wsgm-steam-cef-toolkit/references/ui-and-overlay-parity.md) for
the concrete controls, styling and review checklist. Add a shared feature owner or extend the
existing one first, then the two projections. Do not duplicate side effects to make the second
interface appear complete.

## Preserve recovery and live-session boundaries

Keep restore-shell usable before configuration, logging, Avalonia and GPU startup. Explorer is the
registered shell and is asked to exit orderly; never terminate it. Preserve the current recovery
records, shutdown reason and ownership of other applications' state.

Before any attended CEF debugging connection or live tool call, confirm from the current run's Steam
logs that Steam and Big Picture have fully started. A reachable endpoint, visible window or old log
entry is insufficient. Early attachment can hang the entire Steam UI and require Steam to be
force-closed. If readiness is unclear, do not connect; this rule does not authorize force-closing
Steam. Read the [debugging preflight](../wsgm-steam-cef-debugging/SKILL.md). The production
runtime's window-based gate is documented separately and does not satisfy this attended-debugging
requirement.

Live shell/boot changes, installation, plugin maintenance, Device Lab writes, Steam mutation and
release builds require the task's explicit scope. Documentation or inspection alone does not grant
those operations.

## Finish the complete change

Update the source contracts, owning mechanism documentation and relevant skills; keep the
[How it Works wiki](https://github.com/KillerPixelCrew/WSGM/wiki/How-it-Works) consistent when the
architecture or behavior it describes changes. Add a new project's entry to the source map only when
the project is actually part of the tracked topology.

Follow the root manual-first validation policy: compilation, formatting, asset drift and guidance
checks may run first; automated tests and test-bearing gates wait for the maintainer's manual pass
unless requested sooner. Choose checks that exercise the actual changed boundary, and report
unavailable tools and deferred hardware evidence plainly. Do not claim an offline check proves a
Steam patch rendered or a hardware write worked.

Use the existing generators for generated assets. Inspect the final diff for unrelated changes.
Commit and push a changed submodule before recording its gitlink in WSGM. Follow the root's default
branch workflow; do not create a task branch, duplicate checkout or PR unless requested.
