# WSGM documentation

These documents explain how WSGM works and, more importantly, why it works the way it does. They
hold the rules that must not be broken, and the things that only turned up on real hardware or
against a live Steam client. For everything else, the code is the reference.

The reusable [Avalonia live backdrop library](../src/Avalonia.LiveBackdrop/README.md) records the
issue #183 compositor spike, API and integration limits. Its separate Avalonia sample and the
integrated Overlay were both checked on the Claw with Windows Transparency Effects disabled.

Each doc opens with a short lead saying what it covers. If you change behaviour one of these
findings describes, check it again on the device before trusting the change.

## Start here

| Read                   | When you want to understand                                                                   |
| ---------------------- | --------------------------------------------------------------------------------------------- |
| `boot-and-shell.md`    | boot, Explorer takeover, Desktop Mode, Game Mode entry, Steam autostart takeover, setup       |
| `elevation.md`         | why WSGM runs elevated, how it de-elevates, and the per-game launch wrappers                  |
| `steam-input.md`       | how the overlay takes the controller from Steam and gives it back, and the OEM button handoff |
| `overlay-and-input.md` | the quick access sheet, gamepad navigation, touch edge swipes                                 |
| `overlay-surfaces.md`  | in-window utility panels, credentials, keyboard and power-menu ownership                      |
| `ui.md`                | Avalonia styling, headless UI tests and the splash engine                                     |
| `logging.md`           | what wsgm.log must and must not contain                                                       |
| `decisions.md`         | standing product decisions in one page                                                        |
| `profiles.md`          | Global and per-game profiles: what they hold, how values fall back, Steam's toggle and reset  |
| `plugin-system.md`     | common plugin contracts, widgets, session automation and the Game Mode entry transaction      |

## Steam

[Steam overlay and input across launchers](steam-launcher-handoff.md) records the attended evidence
behind runtime classification, the UWP and packaged Win32 launch routes, and the checks for keeping
overlay and input consistent through launcher replacements and foreground changes. What was built on
that evidence is in `packaged-game-launcher.md`.

| Read                                             | When you want to understand                                                                                                               |
| ------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `steam-cef.md`                                   | what came out of driving Steam's front end: libraries, tabs, badges, launch options, download sorting, revived surfaces, the 2026-09 beta |
| `steam-cef-system.md`                            | the mechanism end to end: Steam discovery, the transport gate, the session host, patches, the native Quick Access Menu                    |
| `steam-cef-startup-audit.md`                     | the 2026-09-05 login failure, the module-loading audit, the corrections and what is still to check live                                   |
| `..\external\steam-ui-toolkit\docs\reference.md` | the toolkit the mechanism is built on                                                                                                     |
| `packaged-game-launcher.md`                      | how an imported Xbox, UWP or MSIX game gets Steam's overlay and Steam Input, and the two routes that work                                 |
| `game-library.md`                                | bringing other launchers' games into Steam from the overlay or Steam: the pipeline, its parts, and what a second run does                 |
| `wsgm-in-steam.md`                               | WSGM's row in Steam's main menu and the settings page it opens: what is on it, how a change is saved, and what is drawn with what         |
| `sd-cards.md`                                    | the card manager and the format flow                                                                                                      |

## Hardware

| Read                                       | When you want to understand                                                                                                      |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| `device-integration.md`                    | why the device plugin runtime is shaped the way it is, controller management, authored profiles, HidHide                         |
| `device-plugin-system.md`                  | the runtime mechanism: package files, validation, load, cycle, publications, commands, glyphs                                    |
| `device-plugin-authoring.md`               | writing, testing, packing and installing a plugin                                                                                |
| `device-security.md`                       | the one-page boundary checklist                                                                                                  |
| `..\src\WSGM.Device.Sdk\docs\reference.md` | the public SDK contract                                                                                                          |
| `rtss.md`                                  | RivaTuner Statistics Server: frame limit, on-screen display, frametimes, AutoTDP                                                 |
| `power-and-display.md`                     | Game Mode display layouts, Windows power schemes, core preference, screen-off mute, keep-awake, standby wake, refresh rates, VRR |
| `radios.md`                                | what WSGM decides about Wi-Fi, Bluetooth and audio. The Windows calls themselves live in `..\external\windows-device-control`    |
| `perf/README.md`                           | what WSGM costs at idle and in game on the Claw, the scenarios and budgets, and how a run is recorded with `tools\PerfLab`       |

## How to write these

- Lead with what the doc covers. Sections by topic, one finding per heading.
- A finding states the claim, the reason and the rule. It does not tell the story of how it was
  found.
- A fact has one home. Other docs point at it rather than restating it.
- Name a file only when the reader has to open it. Use a table for paths, limits and log lines.
- Keep the diagnostic log lines exact. They are how a pasted log gets read.

Device project layout, shared SDK builds and import revisions are in
[device-projects.md](device-projects.md).
