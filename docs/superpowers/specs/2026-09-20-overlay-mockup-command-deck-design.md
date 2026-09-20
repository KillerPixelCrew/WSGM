# Overlay Mockup Command Deck Design

## Purpose

Refine `tools/OverlayMockup` into a distinctive fullscreen WSGM concept that feels native beside
Steam Big Picture without copying Steam’s visual assets or blue accent. This is a mockup-only visual
and UX exploration for issue 114. It does not change production overlay behavior, hardware behavior,
Steam input, settings, or packaging.

## Goals

- Preserve the existing fullscreen glass canvas, destination model, and simulated-only boundary.
- Preserve the 1/3 section rail and 2/3 controls canvas on every destination.
- Replace the generic rounded-card dashboard treatment with a low-radius graphite command-deck
  language.
- Make controller focus, touch targets, and persistent selection obvious and mutually consistent.
- Make the top bar a deliberate system-control surface and the bottom bar a stable app/tray rail.
- Represent the configured WSGM accent centrally, with orange only as the preview default.

## Non-goals

- No production code or production-theme changes under `src/WSGM`.
- No new dependencies, WebView, motion library, snapshot blur, device services, configuration
  persistence, hardware actions, Steam leases, or system actions.
- No redesign of destinations, the approved two-pane layout, or the existing simulated control
  inventory.
- No automated test project added solely for this mockup. Build, export, and attended review remain
  its validation path.

## Interaction invariants

- The window stays fullscreen by default and supports the documented windowed preview and opaque
  fallback.
- LT/RT, and Page Up/Page Down, cycle destinations with wrap.
- Section selections persist independently per destination.
- The rail retains 48-DIP rows and 4-DIP spacing. Up/Down moves within the rail; A selects its
  section; Right moves focus into the displayed controls; B or Escape returns from controls to the
  selected rail item.
- In-window utilities, power menu, and keyboard retain controller ownership while open. B/Escape
  closes the active surface and restores focus to its invoking control.
- The keyboard remains full-width and types only into its own mockup field.
- Simulated state remains in memory. No control produces external side effects.

## Visual language

### Foundation

The visual reference is Steam Big Picture’s dark layered clarity: confident destination hierarchy,
high-contrast type, and controller-obvious selection. WSGM stays distinct through precise alignment,
technical restraint, and its configurable accent.

The glass canvas uses a smoky charcoal tint. The opaque fallback retains the exact surface hierarchy
in graphite, so it reads as intentional when Windows disables blur.

Use these geometry and spacing rules:

| Element                            | Rule                                     |
| ---------------------------------- | ---------------------------------------- |
| Structural top and bottom bars     | Square outside edges                     |
| Interactive controls               | 4 DIP radius                             |
| Group, utility, and power surfaces | 6 DIP maximum radius                     |
| Keyboard                           | Full-width; 6 DIP maximum top corners    |
| Section rail rows                  | 48 DIP high; 4 DIP gaps                  |
| Touch utilities                    | 44–48 DIP minimum target; 20–22 DIP icon |
| Spacing                            | 4 / 8 / 12 / 16 / 24 DIP only            |

Use tonal planes, hairline dividers, and typography rather than gradients, nested cards, decorative
orange icons, or pill geometry. Keep Inter and the existing WSGM stroke-icon vocabulary. Copy is
direct and task-based; remove inspirational session headings and repeated “preview only” notes,
retaining one persistent sample-data notice and contextual safety explanations.

### Color and state

Accent is a mockup-owned centralized resource whose default can be WSGM orange. It means only
current destination, selected section, visible controller focus, active/confirmed value, and
intentional primary action. Inactive icons, dividers, labels, and static controls stay neutral.

Warnings, errors, and destructive actions use named semantic colors independent of the configured
accent. Primary controls use an on-accent foreground selected for contrast rather than assuming
black text works with every configured color.

| State                 | Treatment                                                  |
| --------------------- | ---------------------------------------------------------- |
| Resting               | Neutral text/icon and quiet or transparent fill            |
| Pointer hover         | Neutral contrast increase only                             |
| Pressed               | Brief tonal contrast change, with no scale or layout shift |
| Selected destination  | Accent underline/rule plus stronger label                  |
| Selected rail section | Leading accent rule plus restrained accent field           |
| Controller focus      | Stable 2-DIP accent edge plus contrasting inset fill       |
| Selected and focused  | Both the persistent selection rule and focus treatment     |
| Disabled              | Muted neutral treatment, skipped by controller navigation  |

Focus border space exists at rest so focus never moves geometry. Actual editors, including subparts
of number and combo editors, own their focus treatment; a decorative outer row must never become a
contradictory focus stop.

### Shared component roles

Replace the generic card/tile language with mockup-level role styles for:

- destination button;
- section-rail row;
- group container;
- setting row with a real editor;
- compact stat block;
- primary action;
- status utility;
- app-switcher item;
- tray item;
- banner;
- centred power surface;
- full-width keyboard surface.

Group surfaces distinguish functional areas once. Rows inside groups use hairlines or tonal
separation, not individual cards. Read-only statistics remain compact and nonfocusable; range,
selector, toggle, and number controls receive full-width rows with real controls in the trailing
position. At the 980-DIP floor, range controls may use a second internal line rather than
compressing the actual editor below touch usability.

## Layout

### Workspace

The split stays proportional after its neutral gutter: one-third rail, two-thirds controls. Both
panes keep independent scrolling. The rail is one continuous structural plane, not a stack of cards.
The controls canvas is one quiet plane containing distinct functional groups. Repeated headings
inside one group are removed.

### Top bar

The permanent top bar has one left context cluster and ordered right utility clusters:

1. WSGM mark and current profile/context selector.
2. Network and Bluetooth.
3. Volume and brightness.
4. Safely eject when available.
5. On-screen keyboard.
6. Battery and time.
7. Close.

Clusters use thin dividers and shared background regions. Interactive utilities are square or
rectangular touch targets, not pills. Battery and time are stable status readouts. Close is
separated as the final action, never permanently styled as a danger action, and remains the
fullscreen sheet’s touch-close path.

Opening an interactive utility displays one in-window utility surface aligned below and near the
source cluster, clamped to the viewport. It captures controller focus and returns focus to its
invoker on B/Escape. The power surface remains centred, and the keyboard remains full-width at the
bottom.

The top bar must fit at 980 DIP without reducing touch targets. Compress spacing and the context
selector before reducing utility targets or hiding Close.

### Bottom bar

The bottom bar becomes one anchored rail with an upper applications/tray row and a quiet lower
feedback/hints edge.

- Left and centre: open-window switcher. It has a persistent active-window marker, stable identity
  while sample items refresh, and horizontally scrollable overflow.
- Right: tray icon area, structurally separated from the window switcher.
- Lower edge: the existing feedback and controller hints, visually subordinate without becoming a
  third detached footer.

## Implementation boundaries

The work is limited to `tools/OverlayMockup`:

- `MockupApp.axaml` owns shared semantic colors, glass/opaque variants, radii, spacing, control
  states, and all role styles.
- `MockupWindow.Controls.cs` owns reusable component factories for the defined roles and editor/stat
  composition.
- `MockupWindow.cs` owns structural shell composition, top-bar clusters, bottom rail, and
  centralized glass/fallback application.
- `MockupWindow.Pages.cs` retains routes and content inventory while changing page composition away
  from generic cards, especially Quick Access and the split panes.
- `MockupWindow.Surfaces.cs` owns utility, power, and keyboard surface presentation.
- `MockupWindow.Icons.cs` uses centralized neutral/accent-aware icon presentation instead of literal
  orange strokes.
- `MockupWindow.Navigation.cs` must retain the existing navigation semantics and focus restore
  rules.
- `README.md` documents the revised visual language, chrome composition, fallback behavior, and
  unchanged build/export/manual-review commands.

## Validation

Before any test suite, perform the repository-required attended review of the standalone mockup:

- fullscreen native glass with live content behind it;
- opaque fallback;
- 980 × 640 DIP floor and normal fullscreen fit;
- top-bar cluster reachability and Close isolation;
- bottom app/tray rail overflow;
- mouse/touch hover, press, and focus behavior;
- controller visible focus, rail navigation, Right-to-controls, B return, LT/RT switching, utility
  focus return, power safe default, and keyboard navigation.

Then run the documented warning-clean Release build and named opaque visual exports. Inspect the
generated PNGs and transparency sidecars. Automated production UI tests and the full repository gate
remain out of scope unless the mockup is later promoted into production work.
