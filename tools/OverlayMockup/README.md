# Overlay concept for issue 114

A runnable C# / Avalonia exploration of
[issue 114](https://github.com/KillerPixelCrew/WSGM/issues/114). The existing overlay supplies the
content inventory only. This concept uses a new compact dark glass canvas with WSGM's
charcoal/orange palette, horizontal LT/RT navigation, section icons and a bottom session dock. A
dedicated top bar contains the current profile, live local time/date and Wi-Fi, Bluetooth, audio,
brightness, eject, keyboard and close controls at the top right.

## Run

From the repository root:

```powershell
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release
```

It opens fullscreen. Press **F11** for a resizable window, or append `-- --windowed` to start there.
The supported preview floor is 980 × 640 DIPs. All content scrolls within the fixed navigation and
session dock. **Tab / Shift+Tab** move focus, **Enter / Space** operate controls, and **Esc** backs
out of a surface, returns to Quick access, then closes. The top-right **×** closes the sheet.

The top-left **◐** preview options' **Glass / solid** button compares live Avalonia AcrylicBlur/Blur
with the flat fallback. The glass tint is 68% opaque. Windows decides whether native blur is
available; this tool does not capture the desktop or simulate blur using a frozen image.
Battery-saver behavior needs an attended hardware check.

## Explore

- **Quick access:** a session card, brightness, volume, night light and compact sample readings.
- **Device:** real sliders, selectors, an AutoTDP expander above power limits, cooling, charging,
  Windows power plans, display, lighting and controller details. The preview options' integration
  switch demonstrates the device-independent Windows controls. Claw power and charging ranges follow
  the existing `tests/WSGM.UiTests/Fixtures/claw-ui-publication.json` inventory.
- **Steam and Tools:** library, launch settings, performance, display, storage and independent
  plugin content from the current overlay, in a new composition.
- **Power:** wake/idle options and a centred menu with safe initial focus on **Keep playing**.
  `--power-menu` opens directly into this surface. Every power entry only reports a simulated
  action.
- **Top bar:** in-window connection, audio, brightness and storage surfaces, plus a full-width
  typing keyboard. The keyboard edits its own text field, never another application's input.

**LT / RT** change destinations with wrap; **Page Up / Page Down** are the keyboard equivalents. A
read-only XInput poller handles D-pad movement, A selection and B back while this window is active.
Held triggers switch once until released. Left/right adjust a focused slider or selector. Physical
controller acceptance still needs the maintainer's manual test.

Controls retain in-memory values across pages. The top-bar selector switches between Global and a
sample game profile, with separate performance overrides; Windows brightness and audio remain
global. Shared brightness, volume and frame-limit controls project the same preview state. The
screenshot readings are deliberately static samples, not live telemetry or a complete Claw
publication renderer.

## Boundary

This standalone tool is outside `WSGM.slnx` and the installer. It has no reference to the production
application, plugins or Windows device-control library. It cannot change configuration, acquire
Steam leases, capture a controller, switch sessions, eject drives or write hardware. No production
overlay behavior changes with this mockup.

The prototype uses Avalonia 12.1.2, FluentAvalonia 3.1.0 and Avalonia.Labs.Panels 12.0.2. FlexPanel
wraps groups and tiles; FluentAvalonia supplies expanders, footer rows, information banners and
badges, plus NumberBox inputs paired with sliders. The footer-row template keeps focus on the actual
editor. CardButton is not used; only the production palette is linked. Xaml.Behaviors.Avalonia is
optional in the issue and is not included: interactions currently live in C#.

Production gamepad/Steam input routing, raw touch/swipe recognition, synthesized-mouse filtering,
lease and capture handling, power-button events, SDK layout hints, descriptor-driven shortest-column
layout and telemetry reconciliation remain production work under issue 114. Preview navigation here
does not establish production controller or live-game acceptance. The mockup preserves a 150 ms
close delay.

## Build and visual exports

```powershell
dotnet build tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -warnaserror
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --page Device --capture device.png
```

`--capture` exports the Avalonia content and closes. Create the output directory first. These PNGs
show layout only; native DWM blur is composed outside the visual tree and cannot appear in this
export. `--opaque` makes the exported background deterministic. `--keyboard` and `--power-menu`
select those surfaces for export. Review the running glass window for the actual translucency.

Release compilation and visual exports are permitted before manual testing. Automated suites and the
production gate remain deferred under the repository's manual-first policy.
