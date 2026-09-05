# WSGM documentation

These documents explain how WSGM works and, more importantly, why it works the way it does. They
hold the rules that must not be broken and the findings that only showed up on real hardware or
against a live Steam client. The code itself is the reference for everything else.

Each doc opens with a short lead saying what it covers. Findings carry a date only when a device
verification anchors them. If you change behaviour that a finding describes, re-verify on the device
before you trust the change.

## Start here

| Read                   | When you want to understand                                                                           |
| ---------------------- | ----------------------------------------------------------------------------------------------------- |
| `boot-and-shell.md`    | how WSGM boots, takes over from Explorer, switches modes, and how the installer stops and restarts it |
| `elevation.md`         | why WSGM runs elevated, how it de-elevates, and the per-game launch wrapper                           |
| `steam-input.md`       | how the overlay takes the controller away from Steam and gives it back                                |
| `overlay-and-input.md` | the quick access sheet, gamepad navigation, touch edge swipes                                         |
| `ui.md`                | Avalonia styling rules and the splash engine                                                          |
| `decisions.md`         | standing product decisions in one page                                                                |

## Steam

| Read                                             | When you want to understand                                                                                                               |
| ------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `steam-cef.md`                                   | the findings behind driving Steam's Chromium front-end: libraries, tabs, badges, launch options, download sorting, revived Steam surfaces |
| `steam-cef-system.md`                            | the mechanism end to end: Steam discovery, the transport gate, the session host, patches, the native Quick Access Menu                    |
| `steam-cef-startup-audit.md`                     | the 2026-09-05 login failure, module-loading audit, corrections and remaining live checks                                                 |
| `..\external\steam-ui-toolkit\docs\reference.md` | the toolkit the mechanism is built on                                                                                                     |
| `sd-cards.md`                                    | the card manager and format flow                                                                                                          |

## Hardware

| Read                                            | When you want to understand                                                                                                         |
| ----------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| `device-integration.md`                         | why the device plugin runtime is shaped as it is; controller management; authored profiles; HidHide findings                        |
| `device-plugin-system.md`                       | the runtime mechanism: package slot, validation, load, cycle, publications, commands, glyphs                                        |
| `device-plugin-authoring.md`                    | writing, testing, packing and installing a plugin                                                                                   |
| `device-security.md`                            | the one-page boundary checklist                                                                                                     |
| `..\external\WSGM.Device.Sdk\docs\reference.md` | the public SDK contract                                                                                                             |
| `rtss.md`                                       | RivaTuner Statistics Server: frame limit, on-screen display, frametimes, AutoTDP                                                    |
| `power-and-display.md`                          | display profiles, screen-off mute, keep-awake, refresh rates, variable refresh                                                      |
| `radios.md`                                     | what WSGM decides about Wi-Fi, Bluetooth and audio; the library that owns the Windows calls is `..\external\windows-device-control` |

## Writing conventions

- Lead with what the doc covers. Sections by topic, one finding per heading.
- A finding states the claim, the reason, and the rule. It does not tell the story of how it was
  found.
- A fact has one home. Other docs point to it rather than restating it.
- Name a file only when the reader has to open it. Use a table for paths, limits and log lines.
- Keep the diagnostic log lines exact. They are how a pasted log gets read.
