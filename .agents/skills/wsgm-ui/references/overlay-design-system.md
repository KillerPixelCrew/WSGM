# Overlay design system

The visual and interaction reference for WSGM's Avalonia Overlay, the fullscreen command deck in
`src/WSGM/Overlay`. Every value below comes from the code at refactor/3.0 67a8e8ec. The PNG
baselines in `tests/WSGM.UiTests/Baselines` are the rendered reference, and the
[WSGM Overlay design system](https://claude.ai/artifact/QQF1C14hH7qZzR9uTKxRgL) shows each token and
component as a live preview. The sibling
[Steam design system](../../wsgm-steam-cef-toolkit/references/design-system.md) covers the same
features in Steam's Big Picture and Quick Access Menu.

## When to use it

Read this before designing, building or reviewing any Overlay page, row, surface or shell change.
Use the token and component owners named here; add a reusable element to its code owner first and
then document it here. Settings (`src/WSGM/Settings`) shares the `Hc*` palette and badges but has
its own row family (`src/WSGM/Themes/SettingsPages.axaml`), which this file does not cover.

Where the approved Game Tools mockup (`_mockup/game-tools/project`) differs from the code, this file
records the code's value and marks the mockup's as the proposed direction.

## Tokens

Token names map to Avalonia resources: drop `Brush` and split PascalCase with hyphens. Avalonia
writes alpha first (`#602B333F`). Consume the accent family with `DynamicResource` and everything
else with `StaticResource`. Overlay-only tokens live in `src/WSGM/Themes/CommandDeck.axaml`; shared
`Hc*` tokens live in `src/WSGM/Themes/Palette.axaml`. Pages never contain literal colors.

### Deck surfaces and text (`CommandDeck.axaml`)

| Resource               | Value       | Use                                                                |
| ---------------------- | ----------- | ------------------------------------------------------------------ |
| `DeckCanvasBrush`      | `#171A1F`   | Opaque canvas and transparency fallback; captures show it          |
| `DeckGlassCanvasBrush` | `#80171A1F` | Glass tint over the blurred desktop while the backdrop runs        |
| `DeckRailBrush`        | `#481C2026` | Header, tab bar, rail pane, Open apps strip, footer                |
| `DeckControlsBrush`    | `#30191F27` | Controls pane; TextBox and NumericUpDown fill                      |
| `DeckGroupBrush`       | `#602B333F` | Cards, ActionButton rest fill, pills, app chips, selected rail row |
| `DeckGroupBorderBrush` | `#607E8B9E` | Card border, ComboBox border, pin header divider                   |
| `DeckSurfaceBrush`     | `#303741`   | In-window surfaces, ComboBox face and popup, progress track        |
| `DeckDividerBrush`     | `#414751`   | Chrome hairlines, setting row dividers, surface and toast borders  |
| `DeckHoverBrush`       | `#363D47`   | Pointer-over, and the fill under a focused deck action             |
| `DeckPressedBrush`     | `#454D59`   | Pressed fill                                                       |
| `DeckTextBrush`        | `#F4F5F7`   | Primary text in the Overlay; focus border on a primary button      |
| `DeckSecondaryBrush`   | `#BEC3CC`   | Captions, descriptions, rail icons, unselected radio ring          |

`DeckGroupBrush` composites to about `#1F232B` on the canvas. `DeckGroupBorderBrush` (1.8:1) and
`DeckDividerBrush` (1.9:1 on the canvas) are structural lines below the 3:1 floor, so they never
carry state.

### Shared palette (`Palette.axaml`)

| Resource               | Value       | Use in the Overlay                                              |
| ---------------------- | ----------- | --------------------------------------------------------------- |
| `HcBackgroundBrush`    | `#FF232323` | Base window background; unselected curve handle fill            |
| `HcCardBrush`          | `#FF2C2C2C` | Settings tiles; the deck replaces `Border.tile`                 |
| `HcCardBorderBrush`    | `#FF343434` | Header pill border, LT/RT bumper border                         |
| `HcCardHoverBrush`     | `#FF383838` | Destination tab hover, pin toast fill                           |
| `HcControlBrush`       | `#FF3A3A3A` | Base Button fill (curve presets, surface close), curve plot     |
| `HcControlBorderBrush` | `#FF454545` | Base Button border, curve grid                                  |
| `HcControlHoverBrush`  | `#FF484848` | Base Button hover                                               |
| `HcTextPrimaryBrush`   | `White`     | Window default text, active tab label                           |
| `HcTextSecondaryBrush` | `#FFD6D6D6` | Captions outside the deck (Settings)                            |
| `HcTextMutedBrush`     | `#FFB8B8B8` | Inactive tabs, pill icons, bumpers, eyebrow, neutral badge text |
| `HcDangerBrush`        | `#FFFFA0A0` | Danger button text, bad badge text                              |
| `HcWarningBrush`       | `#FFFFC66D` | Warn badge text                                                 |
| `HcSuccessBrush`       | `#FFA3DCA3` | Good badge text, completed step                                 |

Also defined and unused in the Overlay: `HcHeroCardBrush` `#FF000000`, `HcHeaderBrush` `#FF282828`,
`HcFooterBrush` `#F0222222`, `HcOsdBackgroundBrush` `#E61B1B1B` and the `HcOnBlackButton*` trio
(`#FF161616`, `#FF2E2E2E`, `#FF242424`).

### Accent

| Resource                 | Default     | Rule                                                       |
| ------------------------ | ----------- | ---------------------------------------------------------- |
| `HcAccentBrush`          | `#FFFF9D3D` | User-chosen; `AppConfig.DefaultAccentColor` is the default |
| `HcOnAccentBrush`        | `Black`     | Black above WCAG luminance 0.1791, else white              |
| `HcOnAccentCaptionBrush` | `#CC000000` | On-accent color at alpha 0xCC, for captions in primaries   |

`src/WSGM/Themes/AccentPalette.cs` parses the configured accent, forces it opaque, sets
FluentAvalonia's `CustomAccentColor` and shadows the three resources in `Application.Resources`. UI
tests fix the accent at `#4CC2FF`, which is why the baselines are blue. FluentAvalonia controls
(ToggleSwitch, Slider, InfoBadge) use their own lighter shades derived from the same accent.

### Status chips (`Palette.axaml`, styled in `Shared.axaml`)

| Tone        | Fill                             | Border                           | Text                                 |
| ----------- | -------------------------------- | -------------------------------- | ------------------------------------ |
| neutral     | `HcBadgeBrush` `#14FFFFFF`       | `HcBadgeBorderBrush` `#2EFFFFFF` | `HcTextMutedBrush`                   |
| `accent`    | `HcBadgeAccentBrush` `#26FF9D3D` | `#73FF9D3D`                      | `HcBadgeAccentTextBrush` `#FFFFB870` |
| `community` | `#26B79CFF`                      | `#73B79CFF`                      | `#FFCDB9FF`                          |
| `info`      | `#268AB4F8`                      | `#738AB4F8`                      | `#FFA8C7FA`                          |
| `good`      | `#26A3DCA3`                      | `#73A3DCA3`                      | `HcSuccessBrush`                     |
| `warn`      | `#26FFC66D`                      | `#73FFC66D`                      | `HcWarningBrush`                     |
| `bad`       | `#26FFA0A0`                      | `#73FFA0A0`                      | `HcDangerBrush`                      |
| `action`    | `HcAccentBrush`                  | `HcAccentBrush`                  | `HcOnAccentBrush`                    |

The `accent` chip stays default orange whatever accent the user picks. `HcPluginIconBrush`
`#14FFFFFF` fills plugin icon tiles.

### Focus

One mechanism: `FocusAdorner` is null and each control keeps a constant 2px border whose brush
becomes `HcAccentBrush` on `:focus-visible`. A primary button's focus border is `DeckTextBrush`.
Pointer focus stays quiet. Never draw a second focus visual.

### FluentAvalonia colors (sampled, not WSGM resources)

| Control                  | Value                | Baseline                                 |
| ------------------------ | -------------------- | ---------------------------------------- |
| InfoBar Success          | `#393D1B`            | overlay-game-tools-installed             |
| InfoBar Warning          | `#433519`            | overlay-game-tools-anticheat             |
| InfoBar Error            | `#442726`            | overlay-game-tools-anticheat             |
| InfoBar Informational    | `#1E2127`            | overlay-game-tools-program (on the pane) |
| Expander header and body | `#23262B`, `#23282F` | claw-power-1280x800                      |

## Type

Inter, embedded by `Avalonia.Fonts.Inter` (`WithInterFont` in `src/WSGM/Program.cs`), falling back
to "Segoe UI Symbol" and "Segoe UI" (`Window` style in `src/WSGM/Themes/Shared.axaml`). Default size
is 14. Letter spacing is in DIPs. Line height is Inter's natural 1.21 unless set.

| Style           | Size            | Weight   | Where                                                   |
| --------------- | --------------- | -------- | ------------------------------------------------------- |
| Power title     | 28              | SemiBold | Power menu surface title                                |
| Workspace title | 26              | SemiBold | Destination title at the top of the rail                |
| Page title      | 22 (20 compact) | Bold     | Game Tools game name, program question                  |
| Level heading   | 20              | SemiBold | `OverlaySubView.NewStack`, surface titles               |
| Wordmark        | 20              | Bold     | "WSGM" in the header                                    |
| Section heading | 18              | SemiBold | `CollapsibleSection`, `SectionPinHeader`, pane eyebrow  |
| Tool name       | 18              | Bold     | Game Tools tool card                                    |
| Power action    | 16              | SemiBold | Power menu tiles                                        |
| Rail label      | 15              | Regular  | Rail rows, two lines at most                            |
| Clock           | 15              | SemiBold | Header clock                                            |
| Body            | 14              | Regular  | Default text, ComboBox values, setting labels           |
| `setting-title` | 14              | SemiBold | ActionButton titles, slider titles and values           |
| Pane caption    | 13 / 19         | Regular  | `caption` inside `ContentScroller` and row descriptions |
| `caption`       | 12              | Regular  | Rail description, footer hints; profile label is 11     |
| Header detail   | 12              | SemiBold | Volume and Wi-Fi values in pills                        |
| Tab label       | 13, tracking 1  | SemiBold | Destination tabs, uppercase                             |
| Badge           | 11.5            | SemiBold | `Border.badge` text                                     |
| `eyebrow`       | 11, tracking 2  | SemiBold | `OPEN APPS`, in `HcTextMutedBrush`                      |
| Fact label      | 11              | SemiBold | Uppercase labels in the game header                     |
| Bumper          | 11              | SemiBold | LT/RT chips                                             |

## Spacing and size

| Resource or source                    | Value                     | Use                                           |
| ------------------------------------- | ------------------------- | --------------------------------------------- |
| `DeckRowGap`                          | 8                         | Rows in a stack, command strip gap            |
| `DeckSectionGap`                      | 12                        | Between groups, rail to pane, label to editor |
| `DeckGroupPadding`, `DeckPanePadding` | 12                        | Card padding, rail and pane padding           |
| `DeckWorkspaceMargin`                 | 16, 12                    | Around rail and pane                          |
| `DeckControlPadding`                  | 10, 4                     | Deck actions, inputs, ComboBox items          |
| `DeckSelectorPadding`                 | 10,4,0,4                  | ComboBox; Fluent adds a 32 arrow column       |
| `DeckControlHeight`                   | 36                        | ComboBox, TextBox, ToggleSwitch, setting rows |
| `DeckActionHeight`                    | 44                        | ActionButton and every deck Button            |
| `DeckContentMaxWidth`                 | 1600                      | Rail plus pane maximum                        |
| Header, tab bar                       | 72, 44                    | `OverlayWindow.axaml` rows                    |
| Rail width, rail row                  | 240, 48 with 4 gaps       | Section rail                                  |
| Header and footer padding             | 24 horizontal, 8 vertical | Chrome rows                                   |
| Surface padding, margin               | 20, 16                    | `OverlaySurfaceStack`                         |
| Surface width                         | 620 utility, 640 power    | `OverlaySurfaceStack`                         |
| Pill, app chip, pin button            | 44, 48, 36                | Header, Open apps, section header             |
| Progress bar                          | 6                         | `ProgressBar.overlay-progress`                |
| Keyboard keys                         | 44 rows, 1 gaps           | `KeyboardPanel`                               |
| Floor                                 | 980 x 640                 | Minimum workspace                             |

The code keeps a few exact odd values: 17 between footer hints and 22 between install steps.

## Radius and elevation

| Resource or source          | Value      | Use                                                         |
| --------------------------- | ---------- | ----------------------------------------------------------- |
| `DeckControlRadius`         | 4          | Buttons, pills, ComboBox, rail rows, app chips, capsule art |
| `DeckGroupRadius`           | 6          | Cards, rail and pane, surfaces                              |
| Tool tile                   | 8          | Game Tools 44px icon tile                                   |
| Badge, toast                | 10         | `Border.badge`, `PinToast`                                  |
| Step dot                    | 12         | 24px install step chips                                     |
| Progress                    | 3          | Half the bar height                                         |
| Override bar, tab underline | 2, 1.5 top | Profile override, active tab                                |

Surfaces separate by tint and 1px borders. The only shadow is the pin toast:
`BoxShadow="0 10 30 0 #73000000"`. In-window surfaces are solid `DeckSurfaceBrush` with a
`DeckDividerBrush` border over a transparent hit-test shield; the deck behind is not dimmed.

## Iconography and motion

Icons are stroke `StreamGeometry` in `src/WSGM/Controls/Icons.cs` on a 24px grid. Render with `Fill`
null, round caps and joins, stroke 1.5 to 1.6; size by the dominant dimension only. Sizes: 20 in
ActionButton and rail, 18 in pills and pin buttons, 15 in tabs, 22 in the tool tile. Controller
prompts come from `GlyphIcon` (`src/WSGM/Overlay/GlyphIcon.cs`), Kenney Input Prompts (CC0) in Xbox,
PlayStation and Nintendo styles at 18px. No emoji. The header has no logo; it sets "WSGM" as text.

Motion is functional. The sheet slides down from above the display over 180 ms with a cubic ease-out
on a 16 ms timer (`src/WSGM/Overlay/OverlayPlacement.cs`). Close waits 150 ms after touch. The pin
toast shows for 2.4 s. Sliders commit 250 ms after the last change and curves after 400 ms. Restart,
shut down and sign out need a second press within 5 s. Captures remove transitions.

## Shell anatomy

`src/WSGM/Overlay/OverlayWindow.axaml` (class `command-deck`) has five rows; only the workspace
scrolls.

| Row                          | Contents                                                                                                                                                          |
| ---------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Header (72, `DeckRailBrush`) | Back pill on nested pages, wordmark, Profile selector (176), Manage profiles; right: Wi-Fi, Bluetooth, Volume, Brightness, Eject, Keyboard, Battery, Clock, Close |
| Tabs (44, `DeckRailBrush`)   | LT, Quick access, Steam, Device, Tools, Power, RT (`TabStrip.sheet`)                                                                                              |
| Workspace                    | Rail pane (240, `DeckRailBrush`) and controls pane (`DeckControlsBrush`), 12 apart                                                                                |
| Open apps                    | `OPEN APPS` eyebrow, app chips, tray icons, Y Next app                                                                                                            |
| Footer hints                 | A Select, B Back, X Pin to Quick access, optional Y; right: LT/RT, D-pad, B / Esc                                                                                 |

The rail pane starts with the destination title and description, then rail rows. The controls pane's
`ContentScroller` owns scrolling and brings focus into view; pages never nest a scroller. At the
floor the header hides `header-detail` labels; Game Tools narrows below an 820 pane.

## Content page anatomy

Game Tools (`src/WSGM/Overlay/GameToolsView*.cs`) is the reference.

1. Breadcrumb caption: `Game Tools › Alan Wake 2 › Program`. Back removes the last segment.
2. Page header: level heading with a caption, or a game header card with capsule art (184x86 wide,
   120x56 narrow), page title, path and facts.
3. Page-level InfoBar for success, warning or error, directly under the header.
4. Primary action inside the card it acts on, top right when wide and under the title when narrow.
5. Cards (`Border.device-group`) with rows separated by hairlines.
6. Secondary commands last, in a command strip; destructive ones in danger text.

Collections put a search row and filter commands on one toolbar line, then grouped lists with an
18px heading each, then a Load more row.

## Components

Each entry lists its owner, states, and do and don't guidance. States follow ActionButton unless
noted: rest `DeckGroupBrush`, hover `DeckHoverBrush`, pressed `DeckPressedBrush`, focused
`DeckHoverBrush` with a 2px accent border, disabled at 0.5 opacity.

**ActionButton.** `src/WSGM/Controls/ActionButton.cs` (`ActionButton`, class `deck-action`); pages
use `OverlayActionButton` via `OverlaySubView.Row`, `PrimaryRow`, `DangerRow`, `Command`. Title,
optional description, icon, trailing text or glyph, pin mark. Primary: accent fill kept on hover and
press, on-accent text, focus border `DeckTextBrush`. Danger: `HcDangerBrush` text. Do use one
primary per card and name verb and object. Don't create page-local button styles. Mockup direction:
6px radius with a 1px border.

**Header pill.** `Button.pill` in `OverlayWindow.axaml`; `Border.pill-static` for battery. 44px,
`DeckGroupBrush`, 2px `HcCardBorderBrush`, focus border accent, icon `HcTextMutedBrush` at 1.5. Do
return focus to the pill when its utility closes. Don't hide Close at any size.

**Segmented tabs.** `OverlaySubView.ToolTabs` and `CommandStrip`
(`src/WSGM/Overlay/OverlaySubView.Tools.cs`). Commands min width 52, padding 6, 8 apart; the
selected one is `primary`. Do keep labels to one or two words. Don't use it for more than four
values. Mockup direction: a true segmented control with an inset track for Release channel, which
the code shows as a ComboBox.

**View tabs.** `OverlaySubView.ViewTabs` (`OverlaySubView.Tools.cs`), styled by
`Border.deck-segments` and `Button.deck-action.segment` in `CommandDeck.axaml`. An inset track
(`DeckGroupBrush`, 1px `DeckGroupBorderBrush`, radius 6, padding 3) of 36px segments, padding 12 x
2, transparent at rest in `DeckSecondaryBrush`; the selected segment is filled `DeckPressedBrush`
with `DeckTextBrush` text, focus is the accent border. It wraps when the pane is narrow, and each
segment is keyed `{tag}:{id}`. Tools › Emulators switches its five views with it and with LB/RB. Do
use it for a page's views, up to five, and keep focus on the chosen segment after switching. Don't
use it for values; use a dropdown or the segmented tabs above.

**Filter chips.** `GameToolsView.Toolbar` (`src/WSGM/Overlay/GameToolsView.cs`) with `CommandStrip`.
Labels carry counts ("All 9", "Updates 1"); the active filter is `primary`. Do keep counts live.
Don't use `Border.badge` as a filter; badges take no focus.

**Search field.** A press-to-edit `Row` with the `ListLines` icon that opens `KeyboardPanel` through
`EditText` and `OverlaySurfaceStack.RequestText`. Shows the query or what it searches. Do report an
unavailable keyboard in place. Don't place a bare `TextBox`; `GamepadNavigation` skips it.

**Setting row.** `src/WSGM/Overlay/DeviceSettingRow.cs` (`DeviceSettingRow`, a
`FASettingsExpanderItem`) and `src/WSGM/Overlay/DeviceSliderRow.cs` (`DeviceSliderRow`); template in
`CommandDeck.axaml`; Game Tools uses `ValueRow`. Title, 13/19 description, editor at the right, 1px
`DeckDividerBrush` below. Dropdown: 36px, `DeckSurfaceBrush`, 2px `DeckGroupBorderBrush`, focus
border accent, disabled border `DeckDividerBrush`, commits when the popup closes. Toggle:
FluentAvalonia ToggleSwitch with On/Off. Slider: title and value above, the Slider is the only focus
stop, commits after 250 ms, "Unknown" without readback. Do keep drafts through refresh. Don't gate
writes on readback or hide rows at runtime.

**Curve editor.** `src/WSGM/Controls/CurveEditor.cs` in `src/WSGM/Overlay/DeviceCurveRow.cs`. 150
tall; plot `HcControlBrush`, quarter grid `HcControlBorderBrush`, area white at 6%, 2px accent
curve, dashed accent temperature marker, 7px handles filled `HcBackgroundBrush`. The selected handle
is filled accent with a 3px ring and is the focus indicator. Left/right pick a point, up/down change
output. Presets (Quiet, Default, Aggressive) are base Buttons below. Do keep presets under the
graph. Don't add dense grids or axis labels.

**Collapsible section.** `src/WSGM/Controls/CollapsibleSection.cs` (`CollapsibleSection`,
`Persisted`). 18px title, optional summary (header 60 tall with one), optional action. Starts
folded, remembers expansion per section for the session. Left collapses, Right expands, Down enters;
collapsing moves focus to the header. Do fold task groups. Don't fold destructive confirmations.

**Section pin header.** `src/WSGM/Overlay/SectionPinHeader.cs`; button
`Button.deck-action.section-pin`, 36px, transparent; `pinned` turns the icon accent. Name and
tooltip say "Pin Fans" or "Unpin Fans". Do pin whole groups. Don't add pins to single rows.

**Profile override marker.** `src/WSGM/Controls/ProfileOverrideMarker.cs`. 3px accent bar, "Game
override" in 12 SemiBold accent, "Use global" button 32 tall. Focus returns to the row after reset.
Do mark every per-game value. Don't recolor the editor.

**Card.** `Border.device-group` (`CommandDeck.axaml`), `OverlaySubView.Card`. `DeckGroupBrush`, 1px
`DeckGroupBorderBrush`, radius 6, padding 12, natural height. Tool cards add a 44px tile
(`HcBadgeBrush`, radius 8, accent icon). Do keep one subject and its primary action per card. Don't
nest cards or add colored side borders. Mockup direction: radius 8, padding 14 to 16. A row that is
the whole body of a card (a catalogue card or a core row) takes the `flat` class
(`Button.deck-action.flat`): no fill at rest, the deck hover and focus fills when used.

**Chip.** `Border.badge` with tone classes (`src/WSGM/Themes/Shared.axaml`); `OverlaySubView.Chip`
and `GameToolsView.StatusChip`. Radius 10, padding 8 x 2, 11.5 SemiBold. Do pair tone with a word
and use `action` at most once per row. Don't make chips clickable. Mockup direction: 24px tall,
radius 12.

**Banner.** FluentAvalonia `FAInfoBar` via `OverlaySubView.Banner`, not closable, icon shown.
Severities Success, Warning, Error, Informational. Anti-cheat is an Error banner on the game page,
then a confirm before installing. Do place it where the failure happened and use the service's text.
Don't stack banners for one event or hide partial failure behind success. Mockup direction: a titled
anti-cheat callout.

**Progress.** `src/WSGM/Overlay/OverlayProgress.cs` (`OverlayProgress`, `inline` for Cancel beside
the meter) and `OverlaySubView.StepChips`. Steps: 24px chips, done `good` with a check, current
`action`, pending neutral with caption labels, 22 apart. Bar 6px accent on `DeckSurfaceBrush`.
Cancel 120 wide while safe. Do explain what Cancel does. Don't block navigation. Mockup direction:
inner panel and an 8px bar.

**Page header.** `OverlaySubView.NewStack` and the Game Tools header (`Fact` for labelled facts). Do
keep the breadcrumb true to Back. Don't make breadcrumb segments focusable.

**List row.** `OverlaySubView.Row`; paging through `PagedRows.More`
(`src/WSGM/Overlay/PagedRows.cs`), which focuses the first added item. Do page long lists and keep
the user's place. Don't put two commands in one row.

**Media row.** `src/WSGM/Overlay/OverlayMediaRow.cs` with `OverlayPreviewImage`, whose `Placeholder`
shows a fallback such as an icon tile while loading and when there is no image. Capsule art 120x56
or 96x45, title, detail, chips at the right; art is kept across refresh. Do show the status that
matters on the row. Don't preload or cache all art.

**Candidate radio row.** `GameToolsView.Candidate` and `Radio`
(`src/WSGM/Overlay/GameToolsView.Program.cs`). 22px ring, `DeckSecondaryBrush` or accent with a 10px
dot; the row border shows focus and the radio shows selection. Do order by likelihood and say why.
Don't use a ComboBox when options need explanations.

**Empty state.** `OverlaySubView.RenderMessage`, `RenderLoading`, page captions and
`QuickAccessPins.CreateGhost`. Distinguish loading, empty, no matches, unavailable and failed, each
with a next step. Don't add illustrations.

**Rail item.** `OverlayWindow.CreateSectionButton` (`src/WSGM/Overlay/OverlayWindow.Workspace.cs`,
class `deck-section`). 48 tall; 4x24 rule, 20px icon in `DeckSecondaryBrush`, 15px label,
`FAInfoBadge` count. Selected: `DeckGroupBrush`, accent label and rule, persisting while focus is
elsewhere. Do keep sections flat. Don't seed counts.

**Destination tabs.** `src/WSGM/Controls/TabStrip.cs` with `TabStrip.sheet`
(`src/WSGM/Themes/TabStripTheme.axaml`). Muted at rest, `HcCardHoverBrush` hover, accent focus
border, active label primary with a 3px accent underline. Do keep the destination order. Don't add a
destination for one feature.

**Footer hints.** Row 4 of `OverlayWindow.axaml` with `GlyphIcon`. A page names its own Y through a
property the footer binds, such as `EmulatorManagerView.SecondaryHint` (Details on Get emulators,
Search on RetroArch cores, nothing elsewhere). Do show only buttons the page answers. Don't draw
prompts as letters.

**Open apps strip.** Row 3 of `OverlayWindow.axaml`, `AppSwitcherViewModel`. Chips 48 tall, accent
underline on the foreground window, minimized at 0.5. Do keep chip positions stable.

**Quick Access tile.** `PinnedGrid` (two columns) filled by `src/WSGM/Overlay/QuickAccessPins.cs`.
Tiles mirror the source's content and handler; the Game Mode / Desktop switch is always first. Do
pin from the source with X, right-click or touch hold. Don't copy command logic into the tile.

**Keycap.** `src/WSGM/Overlay/Keycaps.cs` (`Keycaps.Create`), styled by `Border.keycap`,
`Border.keycap.large` and `TextBlock.keycap-joiner` in `CommandDeck.axaml`. `DeckSurfaceBrush` fill,
`DeckGroupBorderBrush` border of 1 with a 2 bottom edge, radius 4, minimum 26 x 24, padding 7 x 1
and 12 SemiBold `DeckTextBrush` text. The editor's large cap is minimum 36 x 34, padding 10 x 3
and 15. Keys join with a `+` in `DeckSecondaryBrush`, and the panel's accessible name is the chord,
such as "Ctrl+Alt+S". Presentation only, never focusable. Do show every key of a combination in
press order. Don't make keycaps clickable or use them for controller prompts, which are `GlyphIcon`.

**Hotkey tile.** `src/WSGM/Overlay/HotkeyActionRow.cs` (`HotkeyActionRow`), drawn by `HotkeysView`
on Tools › Hotkeys and, with the `tile` class, by `HotkeysView.ResolvePin` in the Quick Access grid.
An ActionButton with the keyboard icon, the hotkey's name, its keycaps as the footer, "Send" as the
trailing text and the last refusal as its description. States follow ActionButton. Do keep the page
row and the tile one control, so both send the same way. Don't report a successful send in it.

**Hotkey capture field.** `src/WSGM/Overlay/HotkeyCapture.cs` in a `ShowConfirm` surface. A
focusable card (`Border.device-group.hotkey-capture`) with a constant 2 px border that turns
`HcAccentBrush` on focus, showing "Waiting for keys…" or the held keys as large keycaps. Do keep
Cancel beside it and Esc or B as the way out. Don't put a TextBox in it.

**Confirm dialog.** `OverlaySurfaceStack.ShowConfirm`; Game Tools content in
`GameToolsView.Confirm`. 620 wide, centered, `DeckSurfaceBrush`, title 20 with a 44px close,
paragraphs at line height 22, Cancel focused, accept in danger text. Back cancels; focus returns to
the invoker. Do name the item and the risk. Don't confirm ordinary navigation.

**Utility surface.** `OverlaySurfaceStack.Show` with `RadioPanel`, `AudioPanel`, `EjectPanel`,
`KeyboardPanel`, `OverlayFilePicker` and the power menu. Utility: 620, under its invoker. Power:
640, centered, 28px title, two columns of tiles, Keep playing focused. Keyboard: full width at the
bottom. Do return focus to the invoker. Don't open native windows or take leases.

**Toast.** `PinToast` in `OverlayWindow.axaml`, shown by `QuickAccessPins.ShowToast`. 40 tall,
radius 10, `HcCardHoverBrush`, the toast shadow, never focusable, 2.4 s. Do use it for pin
confirmations. Don't report failures in a toast.

## Guidelines

**Controller first.** Every page works with D-pad, A, B, X, Y and the triggers before touch. One
`GamepadNavigation` owns the window and its surfaces. New levels focus their first control; surfaces
focus the safe answer. Text entry opens the in-window keyboard. Keep Close, Back and the primary
action reachable at the floor, and keep footer hints true. Touch targets are 44 or more, except 36px
selectors and pin buttons.

**Selection is separate from focus.** Focus is the 2px accent border and moves with the cursor.
Selection is a persistent fill or mark: the rail rule and accent label, the tab underline, a primary
command, a filled radio, the `action` step chip. Both can show at once. Selection and failures stay
visible without opening an item.

**States.** Loading says what is happening. Empty gives the next step. Unavailable rows stay visible
and disabled with their reason. Failures show the service's text where they happened, with Try again
only when retrying is safe. Working shows steps, a meter and what Cancel does.

**Writing.** Sentence case; uppercase only for tabs, eyebrows and fact labels. Specific verbs
(Install, Update, Remove, Pin, Browse, Use global, Keep playing) and units on values. Captions state
the consequence ("Changes apply immediately."). Confirmations ask a question that names the item and
risk. No exclamation marks, emoji or internal ids.

**Parity with WSGM Steam.** Every Big Picture feature has the same capability here through the same
backend, actions, availability and results. Each surface uses its own controls; labels, step order,
refusal text and progress phases match. Settings configures WSGM; live Windows and game state
belongs on the Overlay and in Steam.

**Accessibility.** Every focusable control has an accessible name; tooltips add detail and never
carry the only label. Color is never the only signal.
