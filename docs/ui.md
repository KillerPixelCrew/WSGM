# UI layer and splash engine

The UI mechanisms whose behavior depends on Avalonia layout or on imported assets: theme tokens,
focus, shared controls, layout floors, and the splash engine with its import limits. Overlay
navigation and input are in `docs\overlay-and-input.md`.

## Overlay design and production preview

The standalone [C# overlay mockup](../tools/OverlayMockup/README.md) explores issue 114 with a new
compact dark glass composition with horizontal LT/RT navigation. It keeps WSGM's palette and content
inventory, with a new layout and control composition. It includes interactive simulated controls,
in-window status and keyboard surfaces, and a power menu. That standalone tool remains a simulated
design reference outside the production solution. Its README records the mockup's own behavior.

The production implementation now follows that layout through `OverlayWindow`, with a fullscreen
canvas, persistent one-third section rail and two-thirds controls plane. `CommandDeck.axaml` owns
the shared glass, opaque, spacing and focus tokens. The workspace is capped at 1600 DIPs; scaling is
bounded so its 980 × 640 DIP floor remains usable. Rail buttons stay 48 DIPs tall with 4-DIP gaps;
selected sections remain marked when focus enters the controls. One shared compositor blur defaults
to 8 physical pixels and is adjustable in Settings > Quick Access from 0 to 60 pixels. It underlies
the full window when available, even with Windows Transparency Effects disabled. The deck canvas
becomes opaque if the backdrop fails. The maintainer confirmed the integrated Overlay over Steam and
a game on the Claw, with readable bright/dark content and no noticeable frame-time change.

The controls plane uses bordered groups with 12-DIP separation and 12-DIP inner padding. Section
headings use 18-DIP semibold text above a divider; supporting captions remain distinct from
headings. Empty descriptions reserve no space. Labels and values are centered beside 36-DIP
dropdowns, whose closed and popup surfaces use the deck palette. Fan curves sit directly in their
group with their presets below the graph. Windows energy plans, power assignments and manual power
each have their own group. Pinned sections keep their natural height so short sections do not leave
large empty blocks between controls.

Game Mode lowers Windows scaling to 100%; the overlay keeps using WSGM's saved desktop-DPI
preference from `DisplayScale.GetUiScalePercent`. Desktop Mode uses native window DPI without
applying that preference again. A presentation-only fit limit preserves the logical workspace at
720p, including when Windows desktop DPI is greater than 100%. It never rewrites the saved scale.
The UI tests compare physical sizing in both modes and cover native DPI as well as WSGM's transform.

`tools/OverlayPreview` exports the actual production control tree through isolated headless
fixtures, without invoking test methods or live services. Use it for visual review before the
manual-first test gate. Raster exports show Avalonia content and the opaque fallback, not the
Windows compositor's live blur. [Overlay surfaces](overlay-surfaces.md) documents utility, keyboard
and power-menu ownership.

## Shared UI

All styling lives in `Themes\` (`Palette.axaml`, `Typography.axaml`, `Shared.axaml` and the control
themes); `App.axaml` only includes them. Consumer XAML uses palette tokens rather than literal
colours. The runtime accent family (`HcAccentBrush`, `HcOnAccentBrush`, `HcOnAccentCaptionBrush`)
uses `DynamicResource`; stable tokens use `StaticResource`.

Focus uses one mechanism: `FocusAdorner={x:Null}` plus a constant two-pixel border whose brush
changes on `:focus-visible`. That keeps the controller/keyboard cursor visible without leaving the
same glow behind after every touch. Recreating Avalonia's adorner during focus movement loses it on
activation transitions.

Avalonia owns the visual tree, bound collections and observable presentation state on its
dispatcher. Potentially slow Windows enumeration and process snapshots run off that thread; a
completed detached result is posted back at background priority. Telemetry refreshes are coalesced
before changing the visual tree, and never replace a control during its active pointer gesture.
Compiled bindings are enabled project-wide. Current non-virtualized `ItemsControl` uses are bounded
UI sets (device settings, radios, drives and open apps); an unbounded collection belongs in a
height-constrained virtualizing control instead.

Device sliders accept readback in place while the page retains focus. Programmatic changes never
start their user-write timer; active pointer gestures and pending user edits retain their value.
Sliders and curves submit a pending edit once when navigation removes their row. Losing capability
availability cancels the pending edit. Device submissions recheck the current capability and its
descriptor/cycle generation, so a removed or replaced capability cannot receive an old draft.

Shared controls live under `Controls\`: `TabStrip` (the destination bar), `ActionButton` (commands)
and `Icons` (stroke-style `StreamGeometry`). Stroke icons use `Fill={x:Null}` so their interior
detail stays visible.

Descriptor rows keep semantic ids independent of placement. The performance projection renders both
as the Device → Profiles workflow and, for its value controls, beside Device power; the window adds
a placement-specific focus prefix when it creates each `DescriptorControlView`. Do not clone state
or command logic to place the same control twice: the descriptor and its bridge stay the one owner,
and each rendered row keeps a stable focus key.

Settings keeps its page controls alive and switches `IsVisible`, which preserves scroll position and
recorder lifetime.

| Surface  | Layout floor   |
| -------- | -------------- |
| overlay  | 980 × 640 DIPs |
| Settings | 1024 × 640     |

Avalonia's `Shape` scales `Stretch=Uniform` geometry and aligns it at the geometry origin rather
than centering the unused space, so a wide, short glyph in a square path box sits at the top. Give
such paths only their dominant dimension and let the containing layout size the other axis.

## Headless regression tests

`tests/WSGM.UiTests` exercises the actual Overlay and Settings windows using Avalonia Headless, Skia
and xUnit v3. It loads the production themes without starting an application session. Window
lifetime and save dependencies are explicit; fixture stores, device rows and Windows power schemes
are synthetic. The main test project covers pure policy and native seams.

After maintainer manual testing, or an explicit instruction to run UI tests earlier, run it from the
repository root:

```powershell
dotnet test tests/WSGM.UiTests/WSGM.UiTests.csproj
```

Interaction checks cover controller/keyboard/pointer navigation, section selection independent of
focus, remembered sections, telemetry row identity, pins, nested Back, in-window surface focus,
integration-disabled controls, explicit power selection, Settings saves and window cleanup. Binding
warnings fail the suite. Headless tests do not prove native window activation, global input hooks,
Steam Input handoffs or device behavior.

PNG baselines cover Quick Access, Widgets, Plugins, Display, Core Device, synthetic Device rows, and
Settings System, Quick Access, Display and Appearance. Overlay cases cover the 980×640 floor,
1280×720, 1280×800, 1920×1080 and 3840×2160 fullscreen viewports with selected content-scaling
variants. Settings uses 1024×700 (its supported minimum width) and 1280×800 client sizes. Culture,
dark theme, accent, scale and embedded Inter fonts are fixed; transitions, focus and pointer hover
are removed before capture. Focus behavior is covered by interaction tests. Comparisons use decoded
pixels with a two-level per-channel antialiasing tolerance; alpha must match.

Missing or changed baselines fail `eng/verify.ps1`. Each case writes `actual.png` and, when
available, `expected.png` and `diff.png` under `TestResults/ui/<case-name>`, included in the CI test
artifact. For an intentional visual change, follow the test timing above, inspect actual, expected
and difference images, and promote only the reviewed named cases:

```powershell
./eng/update-ui-baselines.ps1 -Case settings-system-1024,settings-system-1280
dotnet test tests/WSGM.UiTests/WSGM.UiTests.csproj
```

Tests and verification never update baselines, including with `-Fix`. A font, theme or renderer
upgrade requires the same image review. Headless layout acceptance does not establish live touch,
controller capture, Steam lease handoff or Windows blur behavior.

## Splash engine

The splash is a customization engine over `SplashConfig`, `SplashStyle`, `SplashPresets`,
`SplashAssets`, `SplashTheme` and `ImageHeader`. Presets prefill editable fields; rendering never
branches on the selected preset.

Imported `.wsgmsplash` files follow these contracts:

- Archive entries must be simple contained file names. Extraction enforces per-entry and aggregate
  byte budgets, and configuration paths are replaced with the files actually extracted.
- `ImageHeader` checks declared PNG, JPEG and BMP dimensions before decode. Logo and background
  decode also have output-area budgets. WebP preview input is limited only by the existing 16 MB
  encoded-byte cap, because `ImageHeader` does not parse WebP dimensions.
- `ConfigStore.NormalizeSplash` bounds text and colour strings and clamps numeric fields, for both
  ordinary configuration load and theme import.
- Imports stay in an owned temporary directory for the Settings-window lifetime, so another window
  cannot collect an unsaved import.
- `SplashAssets` stages sidecars and promotes them only after the configuration save succeeds. A
  failed promotion leaves the previous persisted path intact and keeps the picked source available
  for a retry.

Path-based image validation and decode use separate streams, so callers keep both the byte and the
decode-size limits and handle decode failure locally. A stricter identity guarantee would need a
single open-handle decode API shared by every call site.

### Complete Device-page captures

The Device Overview keeps Power and Performance visible beside the persistent section rail.
Available sections and Quick Access pin choices retain stable identities. Windows energy plans are
directly visible, including with integration disabled. Power adds assignments and device-specific
groups for manual power/display, fans, charging and automatic control. Profile details/reset remain
collapsible. Controller places button glyph selection beside controller output and explains
unavailable output. Normal capability persistence/readback details are tooltips; faults remain
visible in their rows. Groups share spacing and headings. FlexPanel groups use measured column
heights; closed SDK prominence and pairing hints guide their presentation. FluentAvalonia footer
rows hold actual sliders, numeric inputs, toggles or ComboBoxes, while compact statistics display
read-only values. Commands use ActionButton and attention messages use InfoBar.

`DevicePageCaptureTests` renders the Claw descriptor/state fixture with simulated WSGM services at
720p, 1280 × 800, 1920 × 1200 and 4K. It writes viewport and full-content PNGs under
`TestResults/ui/claw-*`. Full-content captures expand the test window to include the entire scroll
extent; they are not screenshots of the live desktop.

The checked-in `tests/WSGM.UiTests/Fixtures/claw-ui-publication.json` comes from the Claw plugin's
`StartAsync_FakeHardware_PublishesDirectCapabilityAndOemSurfaces` test, which loads the plugin with
fake transports and writes `claw-ui-publication.json` beside its test assembly. After descriptor
changes, run that test at the authorized test stage, copy its output into the host fixture, then
inspect the host captures. The host controls are explicit simulations; no capture test starts live
hardware, Steam or RTSS.

Live value refreshes retain row/editor instances and drafts. Descriptor generation, availability or
layout identity changes can rebuild affected groups; those refreshes preserve scroll position and
suppress accidental bring-into-view. Explicit navigation keeps its normal focus scrolling.

### Readable controls and pins

Steam, Tools and Device use the shared adaptive workspace width. Quick Access keeps compact commands
and complete pinned sections. Each grouped section, such as Fans or Charging, has one Pin section
action in its heading. The complete group appears on the front page with its sliders, selectors,
curves and readings together. X, right-click or touch hold within a group targets that section.
Right-click is intercepted before an editor can change its value. Individual Device value controls
are not separate pin targets. A front page containing only sections starts with those sections,
without an empty action row above them.

Pinned sections use the same Device grouping and control renderer as their source pages. Active
editors survive telemetry refreshes. Power assignments, Windows plans and processor controls reuse
the owning selection model and detach their observation when removed. Missing Device providers keep
an unavailable section with an Unpin action.

Widget actions use shared command buttons and readable labels. Each widget has one pin toggle;
arrangement controls live under an expander, and unavailable providers remain visible. Internal
plugin identities are persistence keys, not widget headings.

Settings uses compact natural-width tabs with horizontal scrolling when necessary; selecting a tab
brings its full label into view. Display modes show resolution and refresh, scaling has an explicit
percentage label, and secondary-display coordinates live under Display position. Session actions are
expandable, with one unavailable-provider hint rather than a repeated message for each event.
