# UI layer and splash engine

The UI mechanisms whose behavior depends on Avalonia layout or on imported assets: theme tokens,
focus, shared controls, layout floors, and the splash engine with its import limits. Overlay
navigation and input are in `docs\overlay-and-input.md`.

## Overlay design prototype

The standalone [C# overlay mockup](../tools/OverlayMockup/README.md) explores issue 114 with a new
compact dark glass composition with horizontal LT/RT navigation. It keeps WSGM's palette and content
inventory, with a new layout and control composition. It includes interactive simulated controls,
in-window status and keyboard surfaces, and a power menu. It is outside the production solution and
does not change the shipped behavior described below. Controller, hardware and live-game acceptance
remain pending.

The prototype's Device root contains section tiles only. Its controls live in nested pages, with
Back/B returning one level and LT/RT retaining horizontal destination navigation.

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
Losing capability availability cancels a pending slider write.

Shared controls live under `Controls\`: `TabStrip` (the LB/RB tab bar), `CardButton` (card actions)
and `Icons` (stroke-style `StreamGeometry`). Stroke icons use `Fill={x:Null}` so their interior
detail stays visible.

Descriptor rows keep semantic ids independent of placement. The performance projection renders both
as the Device → Profiles workflow and, for its value controls, beside Device power; the window adds
a placement-specific focus prefix when it creates each `DescriptorStatusRow`. Do not clone state or
command logic to place the same control twice: the descriptor and its bridge stay the one owner, and
each rendered row keeps a stable focus key.

Settings keeps its page controls alive and switches `IsVisible`, which preserves scroll position and
recorder lifetime.

| Surface  | Layout floor |
| -------- | ------------ |
| shell    | 1280 × 800   |
| Settings | 1024 × 640   |

Avalonia's `Shape` scales `Stretch=Uniform` geometry and aligns it at the geometry origin rather
than centering the unused space, so a wide, short glyph in a square path box sits at the top. Give
such paths only their dominant dimension and let the containing layout size the other axis.

## Headless regression tests

`tests/WSGM.UiTests` exercises the actual Overlay and Settings windows using Avalonia Headless, Skia
and xUnit v3. It loads the production themes without starting an application session. Window
lifetime and save dependencies are explicit; fixture stores, device rows and Windows power schemes
are synthetic. The existing xUnit v2 suite still covers pure policy and native seams.

Run it from the repository root:

```powershell
dotnet test tests/WSGM.UiTests/WSGM.UiTests.csproj
```

Interaction checks cover keyboard/pointer navigation, focus and scrolling, pins, nested Back,
integration-disabled controls, staged power selection, Settings saves and window cleanup. Binding
warnings fail the suite. Headless tests do not prove native window activation, global input hooks,
Steam Input handoffs or device behavior.

PNG baselines cover Quick Access, Widgets, Plugins, Display, Core Device, synthetic Device rows, and
Settings System, Quick Access, Display and Appearance. Overlay captures use 1280×800 and 1920×1080
screens with the normal sheet-height fraction. Settings uses 1024×700 (its supported minimum width)
and 1280×800 client sizes. Culture, dark theme, accent, scale and embedded Inter fonts are fixed;
transitions, focus and pointer hover are removed before capture. Focus behavior is covered by
interaction tests. Comparisons use decoded pixels with a two-level per-channel antialiasing
tolerance; alpha must match.

Missing or changed baselines fail `eng/verify.ps1`. Each case writes `actual.png` and, when
available, `expected.png` and `diff.png` under `TestResults/ui/<case-name>`, included in the CI test
artifact. For an intentional visual change, run the suite, inspect those images, and promote only
the named cases:

```powershell
./eng/update-ui-baselines.ps1 -Case settings-system-1024,settings-system-1280
dotnet test tests/WSGM.UiTests/WSGM.UiTests.csproj
```

Tests and verification never update baselines, including with `-Fix`. A font, theme or renderer
upgrade requires the same image review. Initial baselines must repeat across three fresh test
processes and the Windows CI runner before delivery.

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

The Device root uses a bounded two-column Power and Performance overview, then navigation to
available device sections and Quick Access pin choices. Windows energy plans are directly visible,
including with integration disabled. Power adds assignments and device-specific groups for manual
power/display, fans, charging and automatic control. Profile details/reset remain collapsible.
Controller places button glyph selection beside controller output and explains unavailable output.
Normal capability persistence/readback details are tooltips; faults remain visible in their rows.
Groups share spacing and headings; their controls use the same card backgrounds and focus borders as
the rest of the overlay.

`DevicePageCaptureTests` renders the actual Claw descriptor/state publication with simulated WSGM
services at 1280 × 800 and, for Power, 1920 × 1200. It writes viewport and full-content PNGs under
`TestResults/ui/claw-*`. Full-content captures expand the test window to include the entire scroll
extent; they are not screenshots of the live desktop.

The checked-in `tests/WSGM.UiTests/Fixtures/claw-ui-publication.json` comes from the Claw plugin's
`StartAsync_FakeHardware_PublishesDirectCapabilityAndOemSurfaces` test, which loads the plugin with
fake transports and writes `claw-ui-publication.json` beside its test assembly. After descriptor
changes, run that test, copy its output into the host fixture, then inspect the host captures. The
host controls are explicit simulations; no capture test starts live hardware, Steam or RTSS.

Live Device/performance refreshes preserve the current scroll offset through layout and suppress
bring-into-view requests raised by replacement controls during that refresh. Explicit navigation
keeps its normal focus scrolling.

### Readable controls and pins

Steam and Tools category pages use a consistent 720-DIP content width. Quick Access keeps compact
action cards and places pinned sections in two columns. Each grouped section, such as Fans or
Charging, has one Pin section action in its heading. The complete group appears on the front page
with its sliders, selectors, curves and readings together. X, right-click or touch hold within a
group targets that section. Right-click is intercepted before an editor can change its value.
Individual Device value controls are not separate pin targets. A front page containing only sections
starts with those sections, without an empty action row above them.

Pinned sections use the same Device grouping and control renderer as their source pages. Active
editors survive telemetry refreshes. Power assignments, Windows plans and processor controls reuse
the owning selection model and detach their observation when removed. Missing Device providers keep
an unavailable section with an Unpin action.

Widget actions use shared cards and readable labels. Each widget has one pin toggle; arrangement
controls live under an expander, and unavailable providers remain visible. Internal plugin
identities are persistence keys, not widget headings.

Settings uses compact natural-width tabs with horizontal scrolling when necessary; selecting a tab
brings its full label into view. Display modes show resolution and refresh, scaling has an explicit
percentage label, and secondary-display coordinates live under Display position. Session actions are
expandable, with one unavailable-provider hint rather than a repeated message for each event.
