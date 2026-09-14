# WSGM documentation

These documents explain how WSGM works and, more importantly, why it works the way it does. They
hold the rules that must not be broken, and the things that only turned up on real hardware or
against a live Steam client. For everything else, the code is the reference.

Each doc opens with a short lead saying what it covers. If you change behaviour one of these
findings describes, check it again on the device before trusting the change.

## Start here

| Read                   | When you want to understand                                                                     |
| ---------------------- | ----------------------------------------------------------------------------------------------- |
| `boot-and-shell.md`    | boot, Explorer takeover, Desktop Mode, Game Mode entry, Steam autostart takeover, install modes |
| `elevation.md`         | why WSGM runs elevated, how it de-elevates, and the per-game launch wrapper                     |
| `steam-input.md`       | how the overlay takes the controller from Steam and gives it back, and the OEM button handoff   |
| `overlay-and-input.md` | the quick access sheet, gamepad navigation, touch edge swipes                                   |
| `ui.md`                | Avalonia styling, headless UI tests and the splash engine                                       |
| `logging.md`           | what wsgm.log must and must not contain                                                         |
| `decisions.md`         | standing product decisions in one page                                                          |
| `plugin-system.md`     | common plugin contracts, widgets, session automation and the Game Mode entry transaction        |

## Steam

[Steam overlay and input across launchers](steam-launcher-handoff.md) records runtime
classification, the UWP and packaged Win32 launch routes that work, and the checks for keeping
overlay and input consistent through launcher replacements and foreground changes.

| Read                                             | When you want to understand                                                                                                               |
| ------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `steam-cef.md`                                   | what came out of driving Steam's front end: libraries, tabs, badges, launch options, download sorting, revived surfaces, the 2026-09 beta |
| `steam-cef-system.md`                            | the mechanism end to end: Steam discovery, the transport gate, the session host, patches, the native Quick Access Menu                    |
| `steam-cef-startup-audit.md`                     | the 2026-09-05 login failure, the module-loading audit, the corrections and what is still to check live                                   |
| `..\external\steam-ui-toolkit\docs\reference.md` | the toolkit the mechanism is built on                                                                                                     |
| `sd-cards.md`                                    | the card manager and the format flow                                                                                                      |

## Hardware

| Read                                       | When you want to understand                                                                                                      |
| ------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------- |
| `device-integration.md`                    | why the device plugin runtime is shaped the way it is, controller management, authored profiles, HidHide                         |
| `device-plugin-system.md`                  | the runtime mechanism: package slot, validation, load, cycle, publications, commands, glyphs                                     |
| `device-plugin-authoring.md`               | writing, testing, packing and installing a plugin                                                                                |
| `device-security.md`                       | the one-page boundary checklist                                                                                                  |
| `..\src\WSGM.Device.Sdk\docs\reference.md` | the public SDK contract                                                                                                          |
| `rtss.md`                                  | RivaTuner Statistics Server: frame limit, on-screen display, frametimes, AutoTDP                                                 |
| `power-and-display.md`                     | Game Mode display layouts, Windows power schemes, core preference, screen-off mute, keep-awake, standby wake, refresh rates, VRR |
| `radios.md`                                | what WSGM decides about Wi-Fi, Bluetooth and audio. The Windows calls themselves live in `..\external\windows-device-control`    |

## How to write these

- Lead with what the doc covers. Sections by topic, one finding per heading.
- A finding states the claim, the reason and the rule. It does not tell the story of how it was
  found.
- A fact has one home. Other docs point at it rather than restating it.
- Name a file only when the reader has to open it. Use a table for paths, limits and log lines.
- Keep the diagnostic log lines exact. They are how a pasted log gets read.

Device project layout, shared SDK builds and import revisions are in
[device-projects.md](device-projects.md).
