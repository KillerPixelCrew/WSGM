# WSGM documentation

These documents explain how WSGM works and, more importantly, why it works the way it does. They
hold the runtime contracts, ownership rules, and the things that only turned up on real hardware or
against a live Steam client. C# member contracts live in the XML comments beside their declarations;
the source and generated XML documentation remain the detailed API reference.

Each doc opens with a short lead saying what it covers. Dated findings are evidence from one machine
and one build: if you change behaviour one of them describes, check it again on the device before
trusting the change. Product decisions live in [decisions.md](decisions.md); the implementation
tracker is `_plan\implementation-todo.md`.

## Start with the code

| Read                                                                                     | When you want to understand                                                                             |
| ---------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------- |
| [architecture.md](architecture.md)                                                       | the whole path from process startup through frontend actions, backend owners and shutdown               |
| [source-map.md](source-map.md)                                                           | every first-party project, application layer, shared source area, tool and external boundary            |
| [configuration.md](configuration.md)                                                     | configuration fields, strict writes, recovery copies, schema migration, boot projection and live reload |
| [development.md](development.md)                                                         | prerequisites, solution/project layout, generated assets, build and verification commands               |
| [How it Works wiki](https://github.com/KillerPixelCrew/WSGM/wiki/How-it-Works)           | the technical walkthrough across the application and its libraries                                      |
| [Command-line wiki](https://github.com/KillerPixelCrew/WSGM/wiki/Command-Line-Reference) | supported process modes, launcher commands and maintenance entry points                                 |

Reusable libraries are pinned submodules, with their own source contracts and documentation:
[SteamUiToolkit](../external/steam-ui-toolkit/README.md) and
[WindowsDeviceControl](../external/windows-device-control/README.md). WSGM's guides describe the
product policy around those APIs; each library describes its own mechanics, outcomes and lifetimes.

The device/GPU extraction is on `chore/device-integration-rework`. Its private repositories are
[LibHandheld](../external/libhandheld/README.md) and
[LibGPUDriverInteract](../external/libgpu-driver-interact/README.md); see the
[rework plan](../_plan/device-integration-rework.md). LibGPUDriverInteract is directly linked into
WSGM; LibHandheld is still the inventory/planning stage.

## Implementation skills

The delivered [WSGM skill](../.agents/skills/wsgm/SKILL.md) is the entry point for architecture,
placement, reuse, maintainability and frontend parity. It routes work to
[Core](../.agents/skills/wsgm-core/SKILL.md),
[Steam CEF implementation](../.agents/skills/wsgm-steam-cef-toolkit/SKILL.md),
[Steam CEF debugging](../.agents/skills/wsgm-steam-cef-debugging/SKILL.md),
[Device SDK](../.agents/skills/wsgm-device-sdk/SKILL.md) and
[Device Lab](../.agents/skills/wsgm-device-lab/SKILL.md). The Steam guide includes the
[reusable-elements catalog](../.agents/skills/wsgm-steam-cef-toolkit/references/reusable-elements.md)
and
[styling/overlay parity guide](../.agents/skills/wsgm-steam-cef-toolkit/references/ui-and-overlay-parity.md).
`.claude/skills` links to the same canonical files under `.agents/skills`.

## The product

| Read                                         | When you want to understand                                                                                           |
| -------------------------------------------- | --------------------------------------------------------------------------------------------------------------------- |
| [decisions.md](decisions.md)                 | the standing product decisions, grouped by area, each pointing at the doc with the mechanism                          |
| [setup.md](setup.md)                         | what the installer puts where, what a fresh install asks, the update and uninstall order, the two takeovers           |
| [boot-and-shell.md](boot-and-shell.md)       | the sign-in start, how Explorer is ended and restored, desktop and game transitions, the splash and the tray host     |
| [elevation.md](elevation.md)                 | why WSGM runs elevated, how it de-elevates, the per-game launch wrapper and Steam's launch integrity                  |
| [profiles.md](profiles.md)                   | Global and per-game profiles: what they hold, how values fall back, Steam's toggle and reset                          |
| [power-and-display.md](power-and-display.md) | display layouts, Windows power schemes, core preference and boost, screen-off mute, keep-awake, standby, refresh, VRR |
| [logging.md](logging.md)                     | what wsgm.log must and must not contain                                                                               |
| [perf/README.md](perf/README.md)             | what WSGM costs at idle and in game, where the cost goes, the budgets, and how a run is recorded                      |

## The overlay and Settings

| Read                                         | When you want to understand                                                                                        |
| -------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| [overlay-and-input.md](overlay-and-input.md) | the quick access sheet, its in-window keyboard, utility and power surfaces, gamepad navigation, edge swipes        |
| [ui.md](ui.md)                               | Avalonia styling and tokens, layout floors, the headless UI tests and their baselines, the splash engine           |
| [steam-input.md](steam-input.md)             | how the overlay takes the controller from Steam and gives it back, the OEM handoff, guide chord edits              |
| [radios.md](radios.md)                       | what WSGM decides about Wi-Fi, Bluetooth and audio; the Windows calls live in `..\external\windows-device-control` |

## Steam

| Read                                                                       | When you want to understand                                                                                                                |
| -------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| [steam-cef-system.md](steam-cef-system.md)                                 | the mechanism end to end: Steam discovery, the transport gate, the session host, patches, the native Quick Access Menu, WSGM's own pages   |
| [steam-cef.md](steam-cef.md)                                               | the dated findings and disproven approaches behind it: client updates, the login failure, libraries, tabs, badges, launch options, sorting |
| [SteamUiToolkit reference](../external/steam-ui-toolkit/docs/reference.md) | the toolkit the mechanism is built on                                                                                                      |
| [steam-sounds.md](steam-sounds.md)                                         | Steam UI sound packs: Audio Loader compatibility, previews, discovery, reversible playback overrides, the attended acceptance matrix       |
| [game-library.md](game-library.md)                                         | bringing other launchers' games into Steam from the overlay or Steam: the pipeline, its parts, and what a second run does                  |
| [packaged-game-launcher.md](packaged-game-launcher.md)                     | how an imported Xbox, UWP or MSIX game gets Steam's overlay, the follow mode for other launchers, and the attended evidence                |
| [sd-cards.md](sd-cards.md)                                                 | the card manager and the format flow                                                                                                       |
| [rtss.md](rtss.md)                                                         | RivaTuner Statistics Server: frame limit, on-screen display, application identity, the frametime reader                                    |
| [autotdp-controller.md](autotdp-controller.md)                             | the AutoTDP controller: window classes, states, cadence, probe backoff, the service and the trace                                          |

## Devices and plugins

Intel, NVIDIA and AMD driver engines are provided by the directly linked
[LibGPUDriverInteract](../external/libgpu-driver-interact/README.md) library. WSGM owns their
profiles, integration switches and UI through its graphics adapter and coordinator.

| Read                                                             | When you want to understand                                                                                                    |
| ---------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------ |
| [plugin-system.md](plugin-system.md)                             | common plugin contracts, widgets, Steam placements, session automation and the Game Mode entry transaction                     |
| [device-integration.md](device-integration.md)                   | why the device plugin runtime is shaped the way it is, controller management, authored profiles, HidHide, the device findings  |
| [device-plugin-system.md](device-plugin-system.md)               | the runtime mechanism: package files, validation, load, cycle, publications, commands, glyphs, Device Lab, the Claw as example |
| [device-plugin-authoring.md](device-plugin-authoring.md)         | the device projects in this repository, and writing, testing, packing and installing a plugin                                  |
| [Device SDK reference](../src/WSGM.Device.Sdk/docs/reference.md) | the public SDK contract                                                                                                        |

The [Device SDK reference](../src/WSGM.Device.Sdk/docs/reference.md) and
[Plugin SDK guide](../src/WSGM.Plugin.Sdk/README.md) document the public contracts beside their
declarations.

## How to write these

- Lead with what the doc covers. Sections by topic, one finding per heading.
- A finding states the claim, the reason and the rule. It does not tell the story of how it was
  found. A dated finding keeps its date and its machine.
- A fact has one home. Other docs link to it rather than restating it.
- When a contract changes, update its source XML or TypeScript comment, the owning mechanism guide,
  and the wiki section that summarizes it. Keep historical findings dated instead of presenting them
  as current validation. Generated assets and vendored code retain their upstream documentation.
- Name a file only when the reader has to open it. Use a table for paths, limits and log lines.
- Keep the diagnostic log lines exact. They are how a pasted log gets read.
- Link between docs with relative Markdown links, not `docs\` paths, so a rename shows up as a
  broken link rather than a stale name.
