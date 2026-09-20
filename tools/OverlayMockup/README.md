# Overlay concept for issue 114

A runnable C# / Avalonia exploration of
[issue 114](https://github.com/KillerPixelCrew/WSGM/issues/114). The existing overlay supplies the
content inventory only. This concept is a low-radius graphite command deck with smoky charcoal
glass, tonal planes, hairline dividers and a centrally owned WSGM accent. It keeps horizontal LT/RT
navigation, section icons and a unified bottom app/tray rail. A clustered top bar contains the
current profile/context, live local time/date, network and Bluetooth, audio, brightness, eject,
keyboard, battery and close controls.

## Run

From the repository root:

```powershell
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release
```

It opens fullscreen. Press **F11** for a resizable window, or append `-- --windowed` to start there.
The supported preview floor is 980 × 640 DIPs. Workspace content scrolls independently between the
fixed top bar and bottom app/tray rail. **Tab / Shift+Tab** move focus, **Enter / Space** operate
controls, and **Esc** backs out of a surface, returns to Quick access, then closes. The top-right
**×** closes the sheet.

Every destination uses the same one-third section rail and two-thirds controls plane. The rail is a
continuous structural surface, while functional groups in the controls plane use restrained tonal
separation instead of nested cards. Section buttons are consistently 48 DIPs high with 4-DIP gaps on
every tab, align to the top, and scroll independently from the controls. Interactive controls use
4-DIP radii; groups and utility surfaces use at most 6 DIPs. The accent is reserved for the current
destination, selected section, visible controller focus, confirmed values and primary actions.
Neutral text and icons remain neutral at rest.

The top bar keeps the WSGM context cluster on the left and orders the right-side utilities as
network/Bluetooth, volume/brightness, conditional eject, keyboard, battery/time and isolated Close.
The bottom rail combines a horizontally scrollable app switcher with a persistent active-app marker,
a separated tray region, and a subordinate feedback/controller-hints edge.

The top-left **◐** preview options' **Glass / solid** button compares live Avalonia AcrylicBlur/Blur
with the opaque fallback. The glass tint is 75% opaque. Windows decides whether native blur is
available; this tool does not capture the desktop or simulate blur using a frozen image. The
fallback is an intentional graphite hierarchy: canvas, rail, controls, groups and surfaces remain
distinct when blur is disabled, rather than only changing the root tint. Battery-saver behavior
needs an attended hardware check.

## Explore

- **Quick access:** brightness, volume, night light and compact sample readings in functional
  groups, with one persistent sample-data notice.
- **Device:** a one-third section rail and two-thirds controls plane. Icon buttons select power,
  fans, charging, Windows power, display, performance, controller, lighting and device info. Each
  pane scrolls independently. Up/down moves through sections; A selects and right enters its
  controls. B / Esc returns from controls to the section list. The nested fan curve stays in the
  controls pane, with Back / B restoring Fans. LT / RT still changes horizontal destinations. The
  integration switch leaves device-independent Windows controls available. Claw power and charging
  ranges follow `tests/WSGM.UiTests/Fixtures/claw-ui-publication.json`.
- **Steam and Tools:** library, launch settings, performance, display, storage and independent
  plugin content from the current overlay, in a new composition.
- **Power:** wake/idle options and a centred menu with safe initial focus on **Keep playing**. The
  power surface retains controller ownership while open and returns focus to its invoker when
  dismissed. `--power-menu` opens directly into this surface. Every power entry only reports a
  simulated action.
- **Top bar:** clustered in-window connection, audio, brightness and storage surfaces, plus a
  full-width typing keyboard. Utility surfaces open below and near their source cluster, stay within
  the viewport, retain controller ownership, and return focus to the invoking control. The keyboard
  edits its own text field, never another application's input.

**LT / RT** change destinations with wrap; **Page Up / Page Down** are the keyboard equivalents. A
read-only XInput poller handles D-pad movement, A selection and B back while this window is active.
All tabs use Up/Down for sections, A to select, Right to enter controls and B / Esc to return to the
selected section. Selected sections are remembered per destination. Held triggers switch once until
released. Left/Right adjust a focused slider or selector. Utilities, the power menu and keyboard own
controller focus while open; B / Esc dismisses them and restores focus to the invoker. Physical
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
editor. CardButton is not used; shared palette values are linked while the mockup owns its accent
semantics. Xaml.Behaviors.Avalonia is optional in the issue and is not included: interactions
currently live in C#.

Production gamepad/Steam input routing, raw touch/swipe recognition, synthesized-mouse filtering,
lease and capture handling, power-button events, SDK layout hints, descriptor-driven shortest-column
layout and telemetry reconciliation remain production work under issue 114. Preview navigation here
does not establish production controller or live-game acceptance. The mockup preserves a 150 ms
close delay.

## Attended review

Review the standalone mockup manually before any automated suite:

1. Check fullscreen native glass with live content behind it, then toggle **Glass / solid** and
   confirm the opaque fallback keeps the canvas, rail, controls, groups and open surfaces legible.
2. Check the 980 × 640 DIP floor and normal fullscreen fit. Confirm every top-bar cluster, Close,
   the bottom app/tray rail and its overflow remain reachable without reducing touch targets.
3. Check mouse and touch rest, hover, press and focus states. Confirm selected destinations and
   sections remain visible when focus moves into a control, utility, app item or tray item.
4. Check controller visible focus, rail Up/Down, A selection, Right-to-controls, B / Esc return,
   LT/RT destination switching, utility focus return, the power menu's safe **Keep playing**
   default, and keyboard navigation and editing.

The mockup remains simulated-only throughout this review: no action may change Windows, hardware,
Steam, configuration or another application's input.

## Build and visual exports

```powershell
dotnet build tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -warnaserror
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --page Device --capture device.png
```

The other named opaque exports use the same command shape:

```powershell
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --page Tools --capture tools.png
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --power-menu --capture power-menu.png
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --keyboard --capture keyboard.png
```

`--capture` exports the Avalonia content and closes. Create the output directory first. These PNGs
show layout only; native DWM blur is composed outside the visual tree and cannot appear in this
export. `--opaque` makes the exported background deterministic. `--keyboard` and `--power-menu`
select those surfaces for export. Review the running glass window for the actual translucency.

Release compilation and visual exports are permitted before manual testing. Automated suites and the
production gate remain deferred under the repository's manual-first policy.
