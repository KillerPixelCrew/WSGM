# Overlay Mockup Command Deck Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the standalone overlay mockup as a low-radius, Steam Big Picture-adjacent WSGM
command deck while preserving its existing simulated interactions.

**Architecture:** Keep behavior and state in the existing partial `MockupWindow` class. Centralize
visual policy in `MockupApp.axaml`, then use role-specific control factories and structural
composition in the existing partial files. No production project, service, navigation algorithm, or
package topology changes.

**Tech Stack:** .NET 10, Avalonia 12.1.2, FluentAvalonia 3.1.0, Avalonia.Labs.Panels 12.0.2.

**Spec:** `docs/superpowers/specs/2026-09-20-overlay-mockup-command-deck-design.md`

## Global Constraints

- Change only `tools/OverlayMockup` and its README.
- Retain fullscreen glass, opaque fallback, all simulated-only boundaries, existing packages, and
  all existing navigation semantics.
- Preserve the 1/3 rail / 2/3 controls split, 48-DIP rail rows, 4-DIP rail gaps, and independent
  scrolling.
- A selects a rail section, Right moves into controls, and B/Escape returns to the rail.
- Use centralized mockup resources for configurable accent semantics; never add literal orange to
  individual controls.
- Keep interactive targets at least 44 DIP wherever touch activates them. Editor focus remains on
  the actual editor.
- Use 4-DIP interactive radii and a 6-DIP maximum for grouped, utility, power, and keyboard
  surfaces.
- Do not run automated test suites before the maintainer completes the attended mockup review.
  Compilation, formatting, and visual export are allowed before it.

## Review Focus

- Header at 980 DIP: all required utility clusters, battery/time, and Close remain visible and
  touchable.
- Controller state: selected section remains visually distinct when focus moves into a control,
  utility, app item, or tray item.
- Opaque fallback: all planes remain distinguishable with blur disabled, not only the root
  background.
- Focus return: dismissing an anchored utility, centred power surface, or keyboard returns to its
  invoker.
- App/tray overflow: horizontal application overflow does not move or replace the focused item when
  the sample collection changes.

---

### Task 1: Establish the command-deck resource and component system

**Files:**

- Modify: `tools/OverlayMockup/MockupApp.axaml:7-103`
- Modify: `tools/OverlayMockup/MockupWindow.Controls.cs:14-234`
- Modify: `tools/OverlayMockup/MockupWindow.Icons.cs:154-187`

**Interfaces:**

- Consumes: existing `ActionButton`, `Card`, `Tile`, `Range`, `Picker`, `Toggle`, `Stat`,
  `IconLabel`, and `IconButton` factories.
- Produces: centralized semantic visual resources and role-specific control factories used by shell,
  pages, and surfaces.

- [ ] Replace literal palette resources with semantic canvas, structural rail, controls plane,
      group, hover, divider, primary text, secondary text, muted text, status, glass, opaque,
      accent, and on-accent resources. Keep the preview default accent centralized.
- [ ] Define resource-backed 4-DIP and 6-DIP geometry, the 4/8/12/16/24 spacing scale, 44–48 DIP
      touch targets, and a permanent 2-DIP focus border.
- [ ] Replace generic `.card`, `.tile`, and `.nav` styling with role selectors for destinations,
      section rows, groups, setting rows, stat blocks, primary actions, utilities, app items, tray
      items, banners, power surfaces, and keyboard surfaces.
- [ ] Implement the state matrix in the spec: neutral rest/hover/pressed, persistent selected rules,
      visible controller focus, selected-plus-focused, and disabled styling. Ensure actual Fluent
      editors receive focus-within treatment.
- [ ] Refactor reusable factories so a functional group, setting row, stat block, primary action,
      and utility control each request their role explicitly instead of inheriting one rounded-card
      treatment.
- [ ] Change stroke icons to inherit or receive centralized neutral/accent-aware foreground instead
      of `#FF9D3D`.
- [ ] Format the touched C# and AXAML through the repository formatter authority. Build the mockup
      warning-clean:

```powershell
dotnet build tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -warnaserror
```

- [ ] Commit the task as `refactor(mockup): add command deck visual roles`.

### Task 2: Recompose permanent shell chrome

**Files:**

- Modify: `tools/OverlayMockup/MockupWindow.cs:108-261`
- Modify: `tools/OverlayMockup/MockupWindow.Controls.cs:63-129`
- Modify: `tools/OverlayMockup/MockupWindow.Icons.cs:10-187`

**Interfaces:**

- Consumes: role styles and role-specific factories from Task 1; existing `ShowStatus`,
  `ShowKeyboard`, `CloseDeferred`, `CycleDestination`, and `Navigate` methods.
- Produces: a clustered top bar and one anchored bottom rail without changing event handlers or
  window lifecycle.

- [ ] Rebuild `BuildHeader` as a left context cluster and the ordered right clusters:
      Network/Bluetooth, Volume/Brightness, conditional Eject, Keyboard, Battery/Time, Close. Use
      neutral dividers and shared cluster regions; add a stable battery indicator beside the
      existing clock/date.
- [ ] Preserve existing status actions and add no external behavior. Keep Close as the final
      isolated control and retain the documented deferred close path.
- [ ] Use destination role styles for the LT/RT strip. Preserve button order, wrap behavior,
      controller focus target selection, and horizontal navigation.
- [ ] Replace `BuildDock` plus the separate body footer with one anchored bottom rail. Its upper row
      contains focus-stable app-switcher items and a separated tray region; its lower edge holds
      current feedback and controller hints.
- [ ] Keep the current sample Steam/Desktop items but add mock-only tray icon items and a persistent
      neutral active-app marker. Use a scrollable app region so tray width and focus geometry remain
      stable.
- [ ] Budget and inspect the header at the 980-DIP floor before changing any target dimensions.
      Reduce context width/gaps rather than utility targets.
- [ ] Build warning-clean with the Task 1 command and capture the Quick access page in opaque
      windowed mode:

```powershell
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --page "Quick access" --capture obj/previews/quick-access-command-deck.png
```

- [ ] Commit the task as `feat(mockup): group system chrome`.

### Task 3: Restyle the workspace and in-window surfaces

**Files:**

- Modify: `tools/OverlayMockup/MockupWindow.Pages.cs:15-324`
- Modify: `tools/OverlayMockup/MockupWindow.Surfaces.cs:12-164`
- Modify: `tools/OverlayMockup/MockupWindow.cs:291-297`

**Interfaces:**

- Consumes: Tasks 1–2 role factories/resources; existing `SplitPage`, `ShowDetail`, `ShowStatus`,
  `ShowPowerMenu`, `ShowKeyboard`, and `_sectionDetail` focus lifecycle.
- Produces: a visually coherent workspace, contextual utility panels, centred power surface,
  full-width keyboard, and complete opaque fallback.

- [ ] Preserve `SplitPage` proportions and independent scroll viewers, but replace card-wrapped
      rail/canvas with one structural rail plane, neutral gutter, and quiet controls plane. Keep
      48-DIP sections and their 4-DIP spacing.
- [ ] Style selected rail rows with a leading accent rule and restrained accent fill; preserve
      selected state while focus moves into controls.
- [ ] Change every destination from generic nested cards to functional groups containing setting
      rows, compact nonfocusable stats, and real trailing controls. Remove decorative gradients and
      excessive duplicate headings.
- [ ] Rewrite Quick access, utility headings, and power/keyboard copy as concise task labels. Retain
      only one persistent sample-data notice and necessary safety/preview explanations.
- [ ] Present normal utilities in an in-window panel aligned beneath and near the invoking header
      cluster, clamped to viewport bounds. Preserve `ShowDetail` focus-return behavior. Keep power
      centred with `Keep playing` default focus, and keyboard full-width with no more than 6-DIP top
      corners.
- [ ] Centralize glass/opaque variants so every surface preserves hierarchy in `ApplyGlass`, rather
      than changing only the root tint.
- [ ] Generate and inspect opaque device, tools, power-menu, and keyboard exports:

```powershell
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --page Device --capture obj/previews/device-command-deck.png
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --page Tools --capture obj/previews/tools-command-deck.png
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --power-menu --capture obj/previews/power-menu-command-deck.png
dotnet run --project tools/OverlayMockup/WSGM.OverlayMockup.csproj -c Release -- --windowed --opaque --keyboard --capture obj/previews/keyboard-command-deck.png
```

- [ ] Commit the task as `feat(mockup): restyle command deck workspace`.

### Task 4: Reconcile documentation and complete attended review handoff

**Files:**

- Modify: `tools/OverlayMockup/README.md:1-96`

**Interfaces:**

- Consumes: completed presentation, navigation, and export behavior from Tasks 1–3.
- Produces: accurate user-facing mockup guidance and the attended-review checklist.

- [ ] Replace the charcoal/orange, translucent-card, orange-divider, session-card, and separate-dock
      descriptions with the command-deck visual language, clustered top bar, unified bottom app/tray
      rail, low-radius role system, and centralized accent semantics.
- [ ] Preserve standalone boundary guarantees, controller map, opaque fallback explanation, launch
      commands, visual-export commands, and manual-first validation rule.
- [ ] Add the attended review sequence from the spec: native glass, opaque fallback, 980 × 640 fit,
      top/bottom chrome reachability, controller focus/return semantics, touch states, power safe
      default, and keyboard behavior.
- [ ] Run formatting and guidance checks allowed before attended review:

```powershell
npm run format
git diff --check
```

- [ ] Commit the task as `docs(mockup): document command deck review`.

## Final validation and handoff

- [ ] Run the warning-clean Release build and all named opaque exports from Tasks 1–3.
- [ ] Ask the maintainer to complete the attended mockup review; do not run automated test suites
      before that report.
- [ ] After the maintainer reports the attended result, run the narrowest relevant checks or
      investigate reported findings. The full repository gate remains deferred because this
      standalone mockup is not production work.
- [ ] Confirm only mockup files, the spec, the plan, and intended documentation are staged; preserve
      existing user changes to `README.md` and `eng/*.sh`.
