# Steam design system

This is the visual reference for every WSGM page, Quick Access row and dialog inside Steam Big
Picture. It records Big Picture's native values, the toolkit UI kit that WSGM draws around them and
the elements proposed for the kit. Use it when designing, building or reviewing Steam UI. The
[WSGM Steam design system](https://claude.ai/artifact/Ds954YTgjcqM1RZMUgLBDB) shows each token and
component as a live preview. The sibling
[Overlay design system](../../wsgm-ui/references/overlay-design-system.md) covers the Avalonia side.

Sources, read on 2026-11-01: Big Picture values from the installed client's `steamui/css/sp.css`
(2026-10-14 build, the gamepad stylesheet `library.js` loads; Steam defines no `--gp*` values on
disk, so these are its literals and `:root` scales); kit values from `SteamUiKitStyles` in
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/ui-kit.ts` (toolkit caa72ec); the
approved direction from `_mockup/game-tools/project/SteamGame.dc.html` and `SteamInstalled.dc.html`.
Where a mockup differs from Steam, Steam wins. The "WSGM Steam" design system artifact carries the
same content with live previews.

## Native first, kit for the rest

Steam's own field is the default wherever one fits. Resolve it with `resolveSteamUiComponents`
(`gate-helpers.ts`) and draw with the page's `ui`: `ui.focusable` (Focusable), `ui.toggleField`
(ToggleField), `ui.dropdown` (DropDownField) or the bare `ui.dropdownControl`, `ui.sliderField`
(SliderField), `ui.textField` (TextField), `ui.dialogButton`, `ui.dialogButtonPrimary` and
`ui.smallButton` (DialogButton), `ui.valueField`, `ui.settingsSection`, `ui.tabs` through the
`registerSteamPage` frame, and `ui.showModal`, `ui.modalRoot` and `ui.confirmModal` for dialogs.

A new element is allowed whenever the design calls for it, even beside an existing native field, as
long as it is Steam-styled:

- Steam's palette and type, the 2px `radius-control` corner;
- the `gpfocus` focus treatment Steam sets on its Focusable;
- controller navigation through `ui.focusable` with `flow-children`, `onActivate`,
  `onOKActionDescription` and, for a level, `onCancelButton`;
- built once in `ui-kit.ts` as a `renderSteamUi*` function with flat `steam-ui-kit-*` rules, covered
  by `eng/check-ui-kit.mjs`, listed in the toolkit reference and in this file.

Never draw one-off HTML for a control in a WSGM page. Never imitate a native field that exists, name
a hashed Steam class, or copy the Overlay's `Deck*` or `Hc*` palette into Steam. A page's own
`style` string holds layout glue under its root id only. When that glue grows a look of its own,
move it into the kit.

## Color tokens

One theme: Big Picture's dark one. Ratios are WCAG contrast on the named ground.

### Native (sp.css)

| Token                  | Value                   | Source and use                                                  |
| ---------------------- | ----------------------- | --------------------------------------------------------------- |
| `surface-page`         | `#0e141b`               | Page, QAM, modal, main menu ground (`--color-greyneutral-2`)    |
| `surface-control`      | `#23262e`               | Field fill, text input, menu item, dark 1px lines, modal border |
| `field-fill-focus`     | `#3d4450`               | Focused or hovered row, selected menu item                      |
| `surface-side`         | `#171d25`               | `--color-greyneutral-4`; the proposed side panel                |
| `button-fill`          | `hsla(0,0%,100%,.15)`   | DialogButton at rest, toggle off, slider track, selected tab    |
| `button-fill-hover`    | `hsla(0,0%,100%,.2)`    | DialogButton hover, 150ms ease-in                               |
| `button-fill-focus`    | `#ffffff`               | Focused button, dropdown, tab, input, menu item                 |
| `button-fill-disabled` | `#131418`               | Disabled DialogButton, drawn at 0.4 opacity                     |
| `text-on-light`        | `#23262e`               | Label on a white focus fill (inputs and menu items: `#0e141b`)  |
| `accent`               | `#1a9fff`               | Focused Primary, toggle on, slider fill, badges; 6.6:1 as text  |
| `accent-hover`         | `rgba(26,160,255,.486)` | Primary hover                                                   |
| `link`                 | alias of `accent`       | Gamepad links                                                   |
| `line-separator`       | `hsla(0,0%,100%,.1)`    | 1px rule under a field row, QAM tab divider                     |
| `overlay-dim`          | `rgba(0,0,0,.85)`       | Behind a modal, with `backdrop-filter: blur(3px)`               |
| `menu-dim`             | `rgba(0,0,0,.4)`        | Behind a dropdown's context menu                                |
| `footer-fill`          | `rgba(0,0,0,.5)`        | Hint bar and scrolled tab header, with `blur(100px)`            |
| `success`              | `#59bf40`               | Positive menu focus, Deck Verified; 7.9:1                       |
| `warning`              | `#ffc82c`               | Warning text, Deck Playable                                     |
| `destructive`          | `#de3618`               | Focused Destructive button; white on it 4.5:1; as text 4.1:1    |
| `text-strong`          | `#ffffff`               | Titles, focused labels, card titles                             |
| `text-body`            | `#dcdedf`               | FieldLabel, button labels, body; 13.7:1                         |
| `text-secondary`       | `#b8bcbf`               | FieldDescription, menu items; 9.7:1                             |
| `text-muted`           | `#8b929a`               | Values, tab counts, notes; 5.9:1, 5.4:1 on `surface-side`       |
| `text-disabled`        | `#67707b`               | Disabled and placeholder text only; 3.7:1                       |

Two of Valve's pairs miss 4.5:1 and stay as Steam draws them: white on `accent` (a focused Primary
button, 2.8:1) and `destructive` as small text (4.1:1). A kit element never uses either; kit error
text is `danger-text`.

### Kit (`ui-kit.ts`)

| Token                 | Value                    | Use                                                    |
| --------------------- | ------------------------ | ------------------------------------------------------ |
| `text-description`    | `#c6d4df`                | Page paragraphs, progress outcome; 12.2:1              |
| `text-heading`        | `rgba(255,255,255,.6)`   | Section title while folded (Steam's QAM title uses .7) |
| `text-sub`            | `rgba(255,255,255,.75)`  | Sub heading                                            |
| `text-detail`         | `rgba(255,255,255,.55)`  | Heading detail line, card meta, page header detail     |
| `text-byline`         | `rgb(124,142,163)`       | Byline, acquire facts, `steam-ui-kit-muted`; 5.5:1     |
| `text-on-badge`       | `#000000`                | Badge text                                             |
| `surface-tabs`        | `#1b2838`                | Tab header row on a kit page                           |
| `surface-media`       | `#10151c`                | Behind card shots and the gallery                      |
| `surface-box`         | `rgba(27,40,56,.9)`      | Detail aside box                                       |
| `group-fill`          | `rgba(255,255,255,.045)` | Group block, wrapped Valve sections, progress box      |
| `group-border`        | `rgba(255,255,255,.07)`  | Group and progress border                              |
| `focus-fill`          | `rgba(255,255,255,.08)`  | Focused or hovered section heading                     |
| `card-fill`           | `#acb2c924`              | Card at rest                                           |
| `card-fill-focus`     | `#acb2c947`              | Focused or hovered card                                |
| `focus-ring`          | `#ffffff`                | 2px outline on a focused card or thumbnail             |
| `line-nested`         | `rgba(255,255,255,.12)`  | Nested rows' left rule                                 |
| `line-dashed`         | `rgba(255,255,255,.2)`   | Large empty panel border                               |
| `empty-fill`          | `rgba(255,255,255,.025)` | Large empty panel fill                                 |
| `track-fill`          | `rgba(255,255,255,.12)`  | Progress and meter track                               |
| `info-fill`           | `rgba(26,159,255,.18)`   | Notice banner                                          |
| `info-fill-soft`      | `rgba(26,159,255,.14)`   | Notice status line                                     |
| `danger-fill`         | `rgba(194,70,62,.25)`    | Error banner and status line                           |
| `danger-text`         | `#ff6d6d`                | Error text everywhere in the kit; 6.8:1                |
| `danger-text-soft`    | `#ffd8d4`                | Error status line text                                 |
| `badge-success`       | `#5cb85c`                | Success card badge                                     |
| `badge-warn`          | `#fca904`                | Warn card badge, `steam-ui-kit-highlight`              |
| `placeholder-from/to` | `#26374a` / `#10151c`    | Card placeholder gradient (135deg)                     |
| `success-text-soft`   | `#a3dca3`                | Page-local "In use" text in card-manager               |
| `surface-hero`        | `#1b232d`                | Hero band ground under the library art                 |
| `hero-scrim`          | `#0e141b` at .1 → .92    | Gradient over hero art that keeps the title legible    |
| `line-subtle`         | `#23262e`                | Hero rule, side-panel hairline                         |
| `tag-fill`            | `#2a3039`                | Neutral and info tags, a step to come                  |
| `success-fill`        | `#1e3324`                | Success tag and banner, a step done                    |
| `success-text`        | `#b9f0c6`                | Text on `success-fill`; 10.5:1                         |
| `update-fill`         | `#3b3020`                | Update tag                                             |
| `update-text`         | `#ffd28c`                | Text on `update-fill`; 9.1:1                           |
| `danger-tag-fill`     | `#3b2326`                | Danger tag: `danger-fill` made opaque over the page    |
| `focus-detail`        | `#3d4450`                | Select-row detail on a focused (white) row; 10:1       |
| `focus-error`         | `#b3261e`                | Select-row error on a focused row; 6.5:1               |

Every tag fill is opaque, so a tag reads the same on the page and inside a white focused row.

## Type

Steam's face is Motiva Sans, a proprietary font the client loads from `/custom_fonts/`. It is not
redistributable. Steam's stack is `"Motiva Sans", "Twemoji", "Noto Sans", Helvetica, sans-serif`;
the monospace face is Roboto Mono. Inside Steam, inherit the client's font and never load another.
Outside Steam (mockups, previews) the stack falls back to Noto Sans.

| Style                  | Size / line | Weight | Source and use                                    |
| ---------------------- | ----------- | ------ | ------------------------------------------------- |
| `bp-body`              | 16 / 20     | 400    | Steam base: field labels, button labels, inputs   |
| `bp-field-description` | 12 / 16     | 400    | FieldDescription, 4px under the label             |
| `bp-caps`              | 12 / 22     | 700    | Tabs, hint bar; uppercase, 0.5px                  |
| `bp-qam-section`       | 16 / 22     | 600    | QAM section title; uppercase, 0.5px, white 70%    |
| `bp-subheading`        | 18 / 22     | 400    | Dialog sub header, dropdown menu header           |
| `bp-heading`           | 22 / 28     | 700    | Modal title, QAM title, Settings page title       |
| `bp-heading-large`     | 28 / 34     | 700    | Steam large heading                               |
| `bp-notch`             | 10 / 10     | 700    | Slider notch labels; uppercase, 0.5px             |
| `kit-section-title`    | 12 / 1.4    | 700    | `renderSteamUiHeader`; uppercase, 0.09em          |
| `kit-sub-title`        | 13 / 1.4    | 600    | Sub heading                                       |
| `kit-page-title`       | 28 / 1.2    | 700    | Page header h1                                    |
| `kit-detail-title`     | 30 / 1.2    | 700    | Detail heading h2                                 |
| `kit-modal-title`      | 24 / 1.25   | 700    | Kit modal title                                   |
| `kit-empty-title`      | 22 / 1.25   | 700    | Large empty panel title                           |
| `kit-box-title`        | 16 / 1.3    | 600    | Aside box title                                   |
| `kit-card-title`       | 15 / 1.3    | 600    | Card title, progress phase                        |
| `kit-body`             | 14 / 1.5    | 400    | Page text, banners, status lines, empty text      |
| `kit-caption`          | 13 / 1.4    | 400    | Select-row detail, progress count, chips, byline  |
| `kit-meta`             | 11 / 1.4    | 400    | Card meta; badges at 700                          |
| `page-hero`            | 34 / 1.15   | 700    | Hero title                                        |
| `item-heading`         | 24 / 1.2    | 700    | Item heading, such as a tool's name               |
| `eyebrow`              | 11 / 1.4    | 700    | Hero eyebrow, side-panel labels; uppercase, .08em |
| `tag`                  | 11 / 22     | 700    | Tags; uppercase words, 0.06em                     |

## Spacing, radius, elevation and size

Spacing is Steam's `:root` scale `space-1` to `space-9`: 4, 8, 12, 16, 24, 32, 40, 48, 64px. The kit
adds `gap-list` 6px (list rows, status lines, Steam's gap between fields), `gap-group` 10px (below a
group), `gap-pane` 14px (pane, collection, card grid), `gap-content` 20px and `pane-bottom` 72px
(clears Steam's footer). A field row has 12px block padding and a 12px column gap. Steam's page
gutter is 2.8vw, 4vw at 1500px and wider.

| Radius           | Value  | Use                                                          |
| ---------------- | ------ | ------------------------------------------------------------ |
| `radius-none`    | 0      | Gamepad text inputs                                          |
| `radius-control` | 2px    | Field, DialogButton, banner, status line, heading focus, tag |
| `radius-track`   | 3px    | Slider track, meter, swatch, thumbnail                       |
| `radius-group`   | 4px    | Group, card, box, progress, gallery, side panel              |
| `radius-empty`   | 6px    | Large empty panel, footer legend item                        |
| `radius-badge`   | 12px   | Card badge                                                   |
| `radius-toggle`  | 16px   | Toggle track                                                 |
| `radius-tab`     | 64px   | Header tab pill                                              |
| `radius-full`    | 9999px | Toggle knob, slider handle                                   |

Big Picture is flat; shadows mark interaction. `shadow-knob` `0 0 5px rgba(0,0,0,.35)` (toggle knob,
slider handle), `shadow-button-focus` `0 4px 4px rgba(0,0,0,.25)` (focused button in a field),
`shadow-tab-focus` `0 3px 6px rgba(0,0,0,.32)`, `shadow-header` `0 4px 8px rgba(0,0,0,.5)` once a
page scrolls, `shadow-capsule` `0 4px 10px rgba(0,0,0,.25)`. Steam's elevations are `2px 2px 4px`
and `5px 5px 6px` at `rgba(0,0,0,.25)`. The gamepad context menu uses
`drop-shadow(0 0 8px rgba(0,0,0,.5))`.

Motion: focus moves with `cubic-bezier(.17,.45,.14,.83)` over 0.32s, a focused row settles its fill
over 0.5s, toggles slide in 0.2s, modals and menus slide in over 0.5s with
`cubic-bezier(.16,.86,.43,.99)`, kit cards and headings change in 150ms, the kit progress bar in
200ms.

Sizes: header 40px; footer at least 40px; DialogButton about 40px; field control minimum 250px
(270px in QAM); QAM visible width 348px with 300px content and a 48px tab strip; modal 660px; wide
modal cap 1000px; bounded content 960px; detail aside 300px; card column minimum 240px; detail
stacks below 760px; side panel 340px; hero 168px (132px without a purpose line); select-row picture
120px at the header capsule's 460:215.

## Focus and selection

Steam marks the focused Focusable with the `gpfocus` class and has no global rule: each control
styles its own. Buttons, dropdowns, tabs and inputs turn white with dark text. Rows lift to
`field-fill-focus`, their label turns white and the rule under them hides. Kit headings get
`focus-fill`; kit cards and thumbnails get `card-fill-focus` and a 2px `focus-ring` outline.

Selection is separate and stays when focus leaves: an inset 3px `accent` bar
(`steam-ui-kit-selected`, on a select row and a filter chip), `aria-pressed`, or a status tag. Text
inside a focused white control must stay dark: under `.gpfocus` a select row's detail turns
`focus-detail` and its error `focus-error`, and a coloured status is a tag, whose opaque fill keeps
its own contrast on white.

## Iconography

Row and section glyphs come from the one table `SteamUiIconShapes` in `icons.ts`: 24 x 24, filled
with `currentColor`, drawn by `renderSteamUiIcon(react, name, size)`. Rows use 20px through Field's
`icon` slot (sliders with `iconLocation: "front"`), headers 18px, card stats 13px. No glyph appears
twice in a panel and a header never repeats a row's glyph. `sectionOpen` and `sectionClosed` are
only the fold caret; `chevron` is a breadcrumb separator; `check`, `cross` and `dash` are the state
marks. Host-supplied paths go through `renderSteamGlyph`, which accepts SVG path commands only.

Controller hints are Steam's. Declare `onOKActionDescription`, `onCancelActionDescription`,
`onSecondaryActionDescription`, `onOptionsActionDescription` or `onMenuActionDescription`, and
Steam's footer draws the connected controller's glyph and label. Never draw a hint bar or a glyph
legend in a page. WSGM's own button glyphs (`src/WSGM/Assets/Glyphs`) belong to the Overlay.

## Surfaces

### Quick Access panel

Owners: `SteamQuickAccessRowsSurface` (rows), `SteamQuickAccessLayoutSurface` (sections, titles,
glyphs, folds), `SteamPanelFoldsSurface` (open sections, kept in `quick-access-folds.json`); WSGM's
layout is `src/WSGM/Shell/Steam/NativeQamLayout.cs`. Top to bottom:

1. Valve's battery line, one line tall through `steam-ui-kit-battery`.
2. WSGM sections as `renderSteamUiGroup` blocks: `group-fill`, 1px `group-border`, `radius-group`,
   padding 4px 12px 8px, 10px apart. The heading is `renderSteamUiHeader`: an 18px glyph, the title,
   the folded summary and the caret.
3. Rows: Steam's fields through `renderSteamSettingRow`, a dropdown below its label, the row glyph
   in Field's `icon` slot.
4. Valve's own sections, wrapped in `steam-ui-kit-valve` for the same block and heading.
5. Plugin rows, then `PerformanceEnd`. **Reset to Default is always last**, after every fixed,
   dynamic and plugin row.

Every section starts folded except Profile scope. A folded body stays mounted with
`childFocusDisabled`; its heading reports what the rows hold (`17 W sustained · 25 W boost`,
`60 fps cap`). A section whose rows draw nothing stays mounted with the `hidden` class.

### Routed page

Owners: `SteamPageSurface`, `SteamPagePatch` and `registerSteamPage` in
`external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/page-gate.ts`. The frame owns
everything but the content:

| Part    | What                                                                                        |
| ------- | ------------------------------------------------------------------------------------------- |
| Header  | Steam's own; the frame offsets by `--basicui-header-height` (40px fallback)                 |
| Ground  | `steam-ui-kit-page`: `surface-page`, `text-body`                                            |
| Tabs    | `ui.tabs` on `surface-tabs`; only the active tab draws; one X or options footer action      |
| Banner  | `renderSteamUiBanner`, 8px 48px: the page's refusal first, then the host's message          |
| Loading | `renderSteamUiEmpty` `loading` until the host publishes; `unavailable` with the refusal     |
| Content | the page's `Page`, usually in `renderSteamUiPane` (12px 4px 72px) or a bounded 960px column |
| Footer  | Steam's hint bar with the actions the focused element declares                              |

The Game Tools composition (`game-tools.ts`): `renderSteamUiHero` with the library art, the "Game
Tools" eyebrow and the game's name; the host's messages as banners under it; a main column
(`flex: 1; min-width: 0`) with `renderSteamUiItemHeading` (name, tags, latest version, summary), the
compact primary action row, native field rows, the supporting actions and Remove set apart; the
340px "This game" `renderSteamUiSidePanel` 28px to the right; 24px 40px page padding, bounded at
`--steam-ui-kit-page-width` (1360px) with the hero's text aligned to it. The root scrolls under
Steam's header. The side panel stacks below 760px. A nested level is `renderSteamUiLevel` or
`renderSteamUiDetail`: it takes focus and B returns to the level before it.

### Modals and confirm dialogs

Steam's gamepad dialog: `surface-page` with a 2px `surface-control` border, 660px wide, padding
`calc(12px + 2.25vh) 2.8vw`, over `overlay-dim` with a 3px blur; the title is `bp-heading`; it
slides in over 0.5s. `showSteamUiConfirm` uses Steam's own confirm modal with `bDestructiveWarning`
on by default, so the confirm button focuses `destructive`. Cancel and B do nothing. `focusCancel`
passes Steam's `focusButton: "secondary"`, so Cancel takes the controller first for a question whose
safe answer is no; text split by a blank line keeps its paragraphs (`white-space: pre-line`, as
Steam's own notices do). Without a native confirm the kit's modal draws the same title
(`kit-modal-title`), a paragraph each and buttons: 12px body gaps, actions right-aligned 8px apart,
Cancel then the primary.

`showSteamUiPrompt` asks for one line with Steam's TextField and sends only a trimmed nonempty
value. `renderSteamUiSheet` is a scrolling body (at most `min(70vh, 100vh - 160px)`) with a note, an
error line and an action row that stays visible. `showSteamModal(ui, { wide: true })` caps at 1000px
and the viewport minus 64px. `showSteamFilePicker` is a 900px picker whose places rail and file list
scroll on their own; it sizes against its parent.

## Components

Each entry gives its layer, its owner, its states and do/don't. Kit owners are in `ui-kit.ts` unless
noted; flows are in `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/ui-flows.ts`.

**DialogButton.** Native: `ui.dialogButton`, `ui.dialogButtonPrimary`, `ui.smallButton`. Padding
10px 24px, `bp-body`, minimum width 160px (0 in kit grids), `radius-control`. Default `button-fill`
with `text-body`; Primary looks the same at rest and focuses to `accent`; Steam's Destructive
focuses to `destructive` and is reached through the confirm modal. States: default, focused (white,
`text-on-light`), selected (inset `accent` bar), disabled (0.4 opacity, `disabled` and
`aria-disabled`, callback refused). Do lead with a verb; one Primary per view. Don't draw an HTML
button or colour a page button red; a destructive command reads "Remove…" and confirms.

**Field row.** Native: `ui.toggleField`, `ui.dropdown`, `ui.sliderField`, `ui.textField`,
`ui.valueField`; host descriptors through `renderSteamSettingRow` (`settings.ts`) and
`renderSteamUiFields`. Label `bp-body` `text-body`, description `bp-field-description`
`text-secondary`, a `line-separator` rule. Toggle 38 x 22, off `button-fill`, on `accent`, 22px
knob. Dropdown is a DialogButton-styled button (10px 16px) with a 1em chevron and Steam's centred
menu. Slider: 6px track, `accent` fill, 24px handle, notches with `bp-notch` labels, sends on
completion. Text: `surface-control`, 10px 16px, square, white when focused, sends on a changed blur.
States: default, focused (`field-fill-focus`), disabled (`text-disabled`), refused (the host's
reason on the row in `danger-text`), nested (`steam-ui-kit-nested`). Do keep descriptions to one or
two sentences. Don't cycle values with a button.

**Choice.** Kit:
`renderSteamUiChoice(ui, { label, showLabel, rgOptions, selectedOption, disabled, onChange })`.
Steam's bare dropdown clamped to its box, value ellipsized, 14px label 6px above. States: default,
focused, disabled, long value. Do use it in boxes, toolbars, tables and side panels; always pass a
label. Don't put a full DropDownField beside another label.

**Select row.** Kit:
`renderSteamUiSelectRow(ui, { key, title, detail, error, status, selected, media, disabled, description, secondary, autoFocus, onClick })`.
One DialogButton holding a `minmax(0,1fr) auto` grid (`auto minmax(0,1fr) auto` with `media`, a
120px picture spanning both lines): title, trailing status, detail in 13px `text-muted` or
`danger-text` that keeps its line breaks. In a `steam-ui-kit-list` the row is full width (10px 14px,
height by content); `steam-ui-kit-list columns` is a grid of rows, `minmax(min(100%,360px),1fr)`.
States: default, focused (detail `focus-detail`, error `focus-error`), selected (accent bar), error,
disabled (refuses its activation). `secondary: { label, onActivate, disabled }` puts a second
command on X, which Steam's footer names; a disabled one binds nothing. Do use it for candidates,
sources, games and managed items, with status in tags. Don't append loose spans to a button or
colour status text.

**Section header.** Kit:
`renderSteamUiHeader(ui, { title, icon, detail, collapsed, onToggle, sub, page })`.
`kit-section-title` in `text-heading` (white once open), 18px glyph, detail in `text-detail`, the
caret. Variants: `sub`, `page`, `plain` (not focusable). States: folded, open, focused
(`focus-fill`), hover. The A hint reads Expand or Collapse. Do keep titles short and let the summary
carry values. Don't repeat a row's glyph.

**Group and fold.** Kit:
`renderSteamUiGroup(ui, { key, title, icon, detail, collapsed, onToggle, hidden }, ...children)`.
`group-fill`, `group-border`, `radius-group`, padding 4px 12px 8px. States: open, folded (`closed`,
mounted, `childFocusDisabled`), hidden, plain. Do start QAM sections folded. Don't unmount a folded
body.

**Actions.** Kit: `renderSteamUiActions(ui, actions, { compact })`, `renderSteamUiChips`,
`renderSteamUiMore`. Grid: two columns, 8px gaps, 36px buttons; a label over 18 characters or an odd
last button spans both. Compact: wrapping row, 8px 18px padding. Chips: 32px; as a filter the
current chip carries a 3px `accent` bar along its foot and `aria-pressed`. An action with `pick`
opens `showSteamFilePicker`. States per button. Do use the grid in QAM and compact on pages. Don't
build button rows by hand.

**Toolbar.** Kit: `renderSteamUiToolbar(ui, ...tools)`,
`renderSteamUiTool(ui, label, control, grow)`. A row aligned to the bottom, 12px gaps; a 12px label
4px over each control; a `grow` tool takes the rest (160px floor); 40px buttons. Do submit search on
blur or Enter. Don't set per-page tool widths; three pages do today, and a width option belongs in
the kit.

**Card and grid.** Kit:
`renderSteamUiCard(ui, { image, media, placeholder, stats, badge, icon, subtitle, title, tags, meta, description, secondary, autoFocus, onActivate })`,
`renderSteamUiGrid(ui, cards)`. 16:10 shot, stats strip, badge, 15px title, 11px meta, `card-fill`;
grid `repeat(auto-fill, minmax(240px, 1fr))`, 14px gap. With `icon` it is the compact program card
(Emulators' Installed and Get emulators): 14px padding, the icon tile beside the 15px title and a
13px `text-muted` subtitle, 13px meta lines, then tags. `description` names A (Open by default);
`secondary` puts a second command on X. States: default, focused (`card-fill-focus` and
`focus-ring`), placeholder, badge. Do bound mounted cards with Load More. Don't nest focusable
controls in a card; a second command is X.

**Icon tile.** Kit: `renderSteamUiIconTile(react, { images, glyph, size, label })`, with the sources
from `steamAppIconImages(appId)` (`library-capsule.ts`, Steam's `GetIconURLForApp`, which includes a
shortcut's own icon). A square on `surface-media` with a 1px `surface-control` border and
`radius-control`; the icon is contained, and the `controller` glyph (42% of the edge, `text-muted`)
shows where there is no icon or every source failed. No focus; decorative without a `label`.

**State mark.** Kit: `renderSteamUiStateMark(react, { state, label })`. A 20px circle for a
checklist line in a field's `icon` slot: `ready` (`success-fill`, `success-text` check), `missing`
(`danger-tag-fill`, `danger-text` cross), `optional` (`tag-fill`, a dash). The row's description
says the same in words. No focus.

**Gallery.** Kit: `renderSteamUiGallery(ui, { images, index, onSelect, empty })`,
`renderSteamUiVideo`, `renderSteamUiImagePreview`. A hero up to 556px, 96px thumbnails at 0.6
opacity, a counter chip. States: current or focused thumbnail (full opacity, outline; focus
selects), empty. Do release video on unmount. Don't autoplay sound.

**Empty state.** Kit: `renderSteamUiEmpty(ui, { kind, text, title, action })`. Kinds `loading`,
`unavailable` (status), `empty`, `noMatch`, `error` (alert, `danger-text`). With a title: the large
panel, 40px 24px, dashed `line-dashed`, 6px radius, 22px title, one primary action. Do tell empty
from no match and loading from unavailable. Don't hand-build the large panel.

**Banner and status.** Kit:
`renderSteamUiBanner(ui, { key, text, title, error, success, actions, onDismiss })`,
`renderSteamUiStatus(ui, lines)`. Banner 10px 14px on `info-fill`, `danger-fill` (an alert) or
`success-fill` with `success-text` and the 16px `check` glyph (a status, for a saved change with a
next step); an optional bold title leads the text. `actions` puts what to do about it at the end as
compact actions before Dismiss, such as Update both on the waiting updates. Status lines 8px 12px on
`info-fill-soft` or `danger-fill`, Dismiss as a small button. States: notice, success, error,
titled, dismissible. Do keep the service's refusal text. Don't hide a partial failure behind a
notice.

**Progress and meter.** Kit: `renderSteamUiProgress(ui, props)` (`phase`, `done`, `total`, `count`,
`steps`, `step`, `detail`, `note`, `onCancel`, `cancelLabel`, `cancelling`, `outcome`),
`renderSteamUiMeter(react, { value, max, usage, label })`. A `group-fill` box: the steps (22px
numbered dots on `tag-fill`, the current one on `accent` with dark text, the done ones checked on
`success-fill`), phase, "9 of 14" or the caller's `count` such as "12 MB of 31 MB", a 6px
`track-fill` track with an `accent` bar, detail, Cancel, then the note on what cancelling does.
States: determinate, indeterminate (sliding bar), stepped, cancelling, ended. Do show the same phase
and step text as the Overlay. Don't use the meter for an operation.

**Tags and badges.** Kit tag: `renderSteamUiTag(react, { text, tone })` and
`renderSteamUiTags(react, tags)`: 22px, 0 8px, `radius-control`, `tag` type. Tones: neutral
(`tag-fill`, `text-secondary`), info (`tag-fill`, `text-body`), success (`success-fill`,
`success-text`), update (`update-fill`, `update-text`), danger (`danger-tag-fill`,
`danger-text-soft`). A row of tags wraps and skips empty ones; cards (`tags`), select rows (as
`status`), the item heading and the side panel take them. Kit badge:
`renderSteamUiCard({ badge: { text, warn } })`, an 11px bold pill on `badge-success` or
`badge-warn`, over a card's image only. Do keep words to one to four. Don't colour status text by
hand; card-manager's `.wsgm-cards-state` and sounds' `.wsgm-sounds-active` still do and belong on
the tag.

**Page header.** Kit: `renderSteamUiHeader(ui, { title, detail, page: true })`. `kit-page-title`
with a 14px wrapping detail in `text-detail`; not focusable. Do state the page's purpose and scope.
Don't repeat the route name as a second heading.

**Detail.** Kit flow: `renderSteamUiDetail`, `renderSteamUiBox`; `renderSteamUiItem`,
`renderSteamUiAcquire` and `steamUiAcquireLabel` in `ui-flows.ts`. Main column and a 300px aside of
`surface-box` boxes, 32px apart, Back on B and at the aside's foot; the acquire box says Install,
Update or Reinstall and "Working…" while busy; removal confirms first. States: loading, error, no
description, busy. Do keep Back reachable.

**Side panel.** Kit: `renderSteamUiSidePanel(ui, { label, sections, note })`, each section
`{ key, label, value, detail, error, tags, rule, action }`. 340px on `surface-side`, `radius-group`,
18px padding, 14px gaps; `eyebrow` labels in `text-muted`, 15px semibold values, 12/16 details in
`text-secondary` (`danger-text` when `error`), wrapping tags, a `line-subtle` hairline above a
section with `rule`, a 12px note in `text-muted`. Only a section with an action is a navigation
node; its button is the compact size and refuses while disabled. A column for the controller; stacks
below 760px. Do carry at most one action. Don't put a primary action in it.

**Hero band.** Kit: `renderSteamUiHero(ui, { title, eyebrow, detail, images, icon, tags })`. Under
Steam's header: the art (tried source by source with `renderSteamUiArt`, cover, 30% from the top)
under the `hero-scrim` gradient, on `surface-hero` without art; an `eyebrow`, the `page-hero` title
and a `kit-body` purpose line in `text-secondary`, 24px 40px 22px padding, 168px tall (132px compact
without a purpose line), a `line-subtle` rule. The text aligns with `--steam-ui-kit-page-width` when
a page sets it. An item's band can lead with its icon tile, 20px before the words, and carry tags
after the title on a wrapping line (an emulator's detail: its systems and "2.3.0 available"). No
focus. Do name the item or page in the title. Don't repeat it below.

**Item heading.** Kit: `renderSteamUiItemHeading(ui, { title, tags, fact, summary })`. The
`item-heading` name, tags and a 14px `text-muted` fact on one wrapping line 8px 12px apart, then a
`kit-body` summary in `text-secondary`. No focus.

**Art.** Kit: `renderSteamUiArt(react, { images, className, fallback, lazy })` tries each source in
turn, as Steam's own artwork does, and draws `fallback` once every one failed. The sources come from
`steamAppHeroImages(appId)` or `steamAppHeaderImages(appId)` (`library-capsule.ts`), which read
Steam's `appDetailsStore` the way its game page does; `readSteamLibraryGames()` lists the library
the way the host's `SteamLibraryData.ReadGamesAsync` does.

**Collection.** Kit flow: `renderSteamUiCollection(ui, props)` with the `SteamUiBrowse` commands.
Toolbar, status, grid or list, loading, error with "Try again", empty, no match, "Load More (24 of
120)". It restores the focused item and scroll by `id` and fetches on first show. Do give each tab
its own `id`. Don't fetch before the collection shows.

**Manage.** Kit flow: `renderSteamUiManage(ui, props)` (`title`, `items`, `itemKey`, `label`,
`detail`, `status`, `error`, `selected`, `onOpen`, `add`, `empty`). Compact add actions, then select
rows in a settings section with the selected one marked and failures on their own row. Do show
failures without opening the item.

**Candidate list.** WSGM composition: `GameToolsChooser` in
`src/WSGM/Core/SteamUiAssets/Source/game-tools.ts`, a `steam-ui-kit-list` of
`renderSteamUiSelectRow` under `renderSteamUiHeader({ page })`, with `renderSteamUiEmpty`, notice
banners and compact actions ("Browse for the program…", "Back to the game"). Rows show the file
name, the "In use" and role tags (success for the game), architecture and folder, then the reasons;
the chosen one carries the accent bar. Do keep the best candidate first.

**Confirm modal.** Native with a kit fallback:
`showSteamUiConfirm(ui, { title, text, confirmLabel, destructive, focusCancel, onConfirm })`. Do
name the item and the consequence and repeat the verb on the button; focus Cancel when the safe
answer is no (Game Tools focuses Cancel on every question, as the Overlay does). Don't confirm
navigation or harmless choices.

**Tabs.** Native: `ui.tabs` through `registerSteamPage({ tabs })`. Rest `bp-caps` `text-body`,
selected `button-fill` pill, focused white with `shadow-tab-focus`, count badge on `accent`. Do keep
titles to one or two words with counts in the title. Don't draw tabs from buttons.

## Proposals

The hero band, tags, side panel, item heading, success banner and the full-width list row are in the
kit now (see Components). What is left is proposed: build each once in the kit, cover it in
`eng/check-ui-kit.mjs` and the toolkit reference, and move page-local copies onto it.

- **Fact row.** A 12px `text-muted` label with its value and a `line-subtle` rule, replacing
  `.wsgm-import-fact`.
- **List row meter.** An optional meter line under a list row's detail, for a capacity per row.
- **Move the remaining page-local copies.** The Game Library page's own 30px header and crumb onto
  the hero band; `.wsgm-cards-state` and `.wsgm-sounds-active` onto the tag; the card-manager and
  library-tab-manager full-width row rules onto `steam-ui-kit-list`, which now carries them.

### Reconciling the mockups

| Mockup                         | Build with Steam's value                                       |
| ------------------------------ | -------------------------------------------------------------- |
| Button `#3d4450`, 15px medium  | `button-fill` (about `#32373d` on the page), `bp-body`         |
| Focused button label `#0e141b` | `text-on-light` (`#23262e`)                                    |
| Primary `#1a9fff` at rest      | Primary matches default at rest; `accent` on focus             |
| Dropdown `#32353c`, 260px      | DialogButton-styled dropdown, 250px minimum                    |
| Toggle 52 x 26                 | 38 x 22 track, 22px knob                                       |
| Label 600 16px white           | 16/20 regular `text-body`, white when focused                  |
| Description 13px `#8b929a`     | 12/16 `text-secondary`                                         |
| Row 60px with a `#23262e` rule | 12px block padding, `line-separator`; focus `field-fill-focus` |
| Figtree                        | Motiva Sans and its fallback stack                             |
| Crumb "Game › Game Tools"      | Hero eyebrow "Game Tools" over the game's name                 |
| Tag "Update 0.9.3"             | The Overlay's words: "Update 0.9.3 available"                  |
| Side note `#6b737c`            | `text-muted`; the mockup's grey is 3.5:1                       |
| Remove in the action row       | Its own row under a rule, after the supporting actions         |

## Guidelines

### Controller navigation and focus

- Every actionable element is one Focusable; its content is not separately focusable.
- Containers declare a direction: `flow-children` `column` for lists and panes, `row` for toolbars
  and action rows, `grid` for card grids and the detail view.
- A pane or level takes focus when it mounts; B leaves the level before the route. The frame's root
  takes focus so B reaches page navigation first.
- Folded and hidden bodies stay mounted with `childFocusDisabled`.
- Name the A action where it is not obvious ("Open", "Expand", "Collapse"); give a tab its X or menu
  action through the frame.
- Triggers page through a card's images with `onSteamTriggers` (codes 7 and 8).
- Keep row keys stable across publications. A refresh must not replace the focused control or close
  an open dropdown; `useSteamSettingDrafts` holds a pending pick and shows a refusal on its row.

### Fitting the narrow QAM

- Use group blocks; put a field's dropdown below its label.
- Use `renderSteamUiChoice` in boxes and tables; the full DropDownField has a fixed minimum width.
- Keep action labels short; one over 18 characters takes a whole grid row.
- A folded heading reports its values. Reset to Default stays last. No glyph twice.

### Flex, grid and box rules

- Make every grid or flex child that holds text or a control shrinkable: `min-width: 0`, grid
  columns `minmax(0, 1fr)`.
- Give percentage or full-width controls `box-sizing: border-box` and `max-width: 100%`.
- Wrap long names and paths with `overflow-wrap: anywhere`, or ellipsize one line; never widen the
  column.
- Size pickers and sheets against their modal parent, never from the viewport width alone; scroll
  the list and keep actions outside the scroller.
- Check long names, long paths, narrow columns and an open dropdown popup in the running client. A
  build or a stand-in render does not prove Steam layout.

### Writing

- Sentence case; uppercase only through a style.
- Buttons lead with a verb: "Install OptiScaler 0.9.3", "Update to 0.9.3". An ellipsis means the
  action asks first or opens a picker.
- Descriptions say what the setting does in this game, in one or two short sentences.
- Confirmations name the item and the consequence: "Remove OptiScaler from Alan Wake 2?"
- Empty states say why and what to do. Errors keep the service's own reason.
- Progress names the phase: "Installing…", "Saving settings".
- No exclamation marks, emoji, internal ids, file formats or transport steps.

### Parity with the Overlay

Every Big Picture feature has the same task in WSGM's Overlay through the same service, commands,
availability, progress and refusal text; each surface uses its own controls. See the **WSGM
Overlay** design system for the Avalonia side.

| Steam                                                | Overlay                                                     |
| ---------------------------------------------------- | ----------------------------------------------------------- |
| DialogButton, `renderSteamUiActions`                 | `ActionButton`, `Row`, `PrimaryRow`, `DangerRow`            |
| Native toggle, dropdown and slider rows              | `DeviceControlRows`, `DeviceSliderRow`, `ChoiceRow`         |
| `renderSteamUiHeader`, `renderSteamUiGroup`          | `CollapsibleSection`                                        |
| Page tabs, `renderSteamUiLevel`, `renderSteamUiItem` | `ServiceSubView`, `ToolTabs`, Navigate and Back             |
| `renderSteamUiCard`, `renderSteamUiGallery`          | `PreviewCard`, `OverlayPreviewImage`, `OverlayMediaPreview` |
| `renderSteamUiEmpty`, `renderSteamUiStatus`          | `ServiceSubView.AddStatus`, captions                        |
| `renderSteamUiProgress`, `renderSteamUiMeter`        | `OverlayProgress`, `OverlayProgress.UsageMeter`             |
| `showSteamUiPrompt`, `showSteamFilePicker`           | `RequestText`, `OverlayFilePicker`                          |

Show the same phase text and button labels on both. A link that sends Overlay users to Steam is not
a counterpart.
