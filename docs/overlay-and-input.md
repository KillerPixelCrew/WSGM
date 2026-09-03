# Overlay surfaces and the input stack

How WSGM's quick access sheet is shaped, how gamepad, touch and raw input reach it, and the Avalonia
and Windows findings its dismissal and focus handling depend on. Theme and control styling is in
`docs\ui.md`; the lease the sheet takes while open is in `docs\steam-input.md`; the plugin
descriptors the Device tab renders are in `docs\device-plugin-system.md`.

## The quick access sheet

`Overlay\OverlayWindow` is one top-docked surface that replaced the earlier side panel and bottom
taskbar. It covers `SheetHeightFraction` (81 %) of the display and leaves the game visible below.
That strip lies outside the window rectangle, so the raw-input tap-outside dismissal closes the
sheet with no extra code; a fullscreen window would have needed a second dismiss mechanism.

The header carries the wordmark, the active-destination eyebrow and the status pills, bound to a
per-open `SystemStatus`. The radio, audio and eject panels hang from the header's measured bottom
edge (`HeaderBottomScreenY` → `StatusPanel.DockBelowHeader`). A `TabStrip` sits over the
always-alive destination roots: Quick access, Session, Steam, optional Device, Tools and Power.
LB/RB cycle with wrap; the sheet reopens on its last destination; focus lands on the first row after
a switch; the warning `InfoBar` stays above the tabs.

Quick access is the home root and the Back target of every other root. `AppConfig.QuickAccessPins`
holds row ids (X, touch-hold or right-click toggles one through `PinToggleRequested`). The root
renders live mirrors of the source rows that follow their title, description, badge and visibility
and press through to the source's Click handler, so a row that rewrites its own title ("Really?",
"Applied to …") keeps working when pinned. Device rows are re-rendered from the current snapshot on
every Device render.

The Open apps chip strip (`AppSwitcherViewModel`) sits along the sheet's bottom and is reconciled in
place every second; a wholesale rebuild would destroy the focused chip under the gamepad cursor. Y
cycles to the next window; X on a tray pill opens its context menu. The peer keyboard window hangs
over the sheet's lower edge because the exposed game strip is too short for it; D-pad Down off the
sheet's last row crosses into it and Up off the keyboard's top row crosses back.

The process/window snapshot behind that reconciliation runs off the UI thread. Only its immutable
result returns to Avalonia: synchronous process and `EnumWindows` work on the dispatcher would
compete with the 16 ms gamepad poll and pointer delivery.

## Sub-views and navigation

Destinations host nested pages in place: six self-drawing sub-views over `OverlaySubView`, the XAML
`PanelFormat`, and the Device sections. The open page is `OverlayNavigation.Page`, not a flag per
page; the two used to be tracked separately and could disagree. Adding a page means adding a row to
`OverlayWindow`'s `SubViews` table (page, host, parent panel, destination, state released on the way
out); the enter/leave sequence, `DefaultFocusTarget`, the `Activated` teardown and B-cancel all read
that table. A page is never a Popup or Flyout, which `GamepadNavigation` cannot reach.

The way back out is `BackButton` in the fixed header, shown while `OverlayNavigation.Depth > 1` and
pressing exactly what B presses (`TryCancelSubView`). It is header chrome rather than a row in the
page because a control inside the scrolling content is not reachable from where the user is — a long
Device section pushes one past the bottom edge — and because the tab strip switches destinations
rather than levels, which left touch-only users no way off a Device section at all.
`TryCancelSubView` resolves the button's visibility for every route out; the enter paths and
`ShowDestination` resolve it for every route in.

## Device sections, performance profiles and lighting color

Plugin-declared Device sections render as their own pages and lead the Device root menu; WSGM's own
sections (profiles, glyphs, diagnostics, unplaced rows) follow. `OverlayPage.DevicePluginSection`
carries the open section's id in the route rather than adding an enum value per section. Rows are
grouped under the declared category eyebrows in sort-then-snapshot order. A section that vanishes
with a descriptor generation while its page is open renders a plain "no longer available" line.
Leaving one runs the same body as leaving a WSGM section: the glyph sample lease is released and
both panels are redrawn, which the generic pop fallback it used to take did neither of.

Per-application performance profiles belong to Device → Profiles; they are not a second detector and
not a device-plugin feature. `PerformanceOverlayBridge` projects the session's one
`PerformanceService` into closed rows. The value rows also sit beside the power controls on Device →
Power and thermals; when Device does not exist, System shows the complete profile workflow.
Identity-only Steam games stay visible as "executable pending", with edits stored for that AppID
until foreground observation supplies the RTSS profile. Performance state changes rebuild both the
owning Device page and its section-card count, so the Profiles page cannot disappear merely because
no plugin publishes a hardware profile.

Device lighting color opens `DeviceColorView` rather than cycling an opaque integer in the row. The
hue field, RGB sliders and exact `#RRGGBB` entry are staged locally; only the explicit Apply row
invokes the capability. This is a persistence constraint: the Claw has no volatile RGB path, so a
write on every controller step would wear and repeatedly commit its non-volatile lighting profile.
`DeviceColorSpectrum` consumes Left/Right like a horizontal slider, so Up/Down still move focus.

Brightness is not part of that editor. `lighting.brightness` is one device-wide value — the Claw's
committed lighting profile carries a single brightness byte for all zones and separate colors per
zone — so it renders as its own debounced slider row on the Lighting page. Repeating it inside each
zone's color editor claimed a per-zone brightness the firmware does not have.

## Text entry

Text entry in the panel is a press-to-edit row, never a bare `TextBox`. Every editable name is a row
whose Description shows the current value and whose click opens the peer keyboard through
`KeyboardService.Request`. A `TextBox` in a panel looks editable but is unusable on a controller:
`GamepadNavigation` skips TextBoxes so the Windows touch keyboard cannot pop, so focus never lands
on one and nothing types. When `KeyboardService.Request` returns false there is no way to type at
all; log it rather than leaving a row that silently does nothing when pressed.

## Input stack

`Input\SdlGamepads` is the process-wide SDL3 owner with a single event pump. Two `GamepadService`
instances exist while Settings is open, and per-instance pumps would steal each other's hotplug
events. A 16 ms UI-thread `DispatcherTimer` poll produces edge-triggered `ButtonPressed` (with
direction auto-repeat) and full-state `StateChanged` (for chords), feeding `GamepadNavigation` and
`GamepadChordWatcher`. `GamepadNavigation` moves focus through tab order, synthesizes Enter to
activate, mirrors arrow keys with a 250 ms dedupe and skips TextBoxes.

`Overlay\TouchSwipeMonitor` observes the raw HID digitizer (`RIDEV_INPUTSINK`) for four configurable
edge swipes and for tap-outside dismissal. The edge map is SteamOS's:

| Edge   | Action                                                     |
| ------ | ---------------------------------------------------------- |
| top    | opens the sheet                                            |
| bottom | opens the sheet on the Open apps strip, in game mode only  |
| left   | sends Steam's installed-client mapping Ctrl+1 (Steam menu) |
| right  | sends Ctrl+2 (Quick Access Menu)                           |

Live device and performance publications may request a redraw while a finger or mouse button is
down. The sheet coalesces those redraws and defers them until the routed pointer release has
completed; replacing a release-mode Avalonia `Button` between its press and release drops its
`Click` and looks exactly like a control that needs a second tap.

On the desktop Explorer's taskbar owns the bottom edge; falling back to the sheet there read as a
regression (device-reported). Left and right always send their keys, including while a game is
foreground, because bringing Steam's menu over the game is their purpose.

## Managed-controller capture and source switching

Each visible WSGM surface owns one named capture claim. The first claim neutralizes the virtual
target, nested claims keep capture active, and the last close resumes game forwarding only after
every control the UI used is released. Lifecycle handoffs and source faults share one
forwarding-blocked state, cleared only by a successfully created or replaced target. There is no
parallel enum of hypothetical zero reasons: capture, routing admission and target state decide
delivery.

| Source                 | Synthesized trigger threshold |
| ---------------------- | ----------------------------- |
| SDL                    | 8000/32767 (about 0.24)       |
| managed canonical path | 0.5                           |

The difference is long-shipped behavior; align the two only with device re-verification.

Navigation switches to managed canonical input only after its first complete sample and keeps SDL
live as the fallback. Controls held across the switch stay suppressed until released, or for at most
two seconds when the incoming source cannot observe them. Every completed switch logs the old
source, new source, suppression mask and managed-health state. A VIIPER submission failure is also a
target-lifetime event: `DeviceRemove` runs before WSGM forgets the handle, because the native device
object and feedback callback otherwise outlive the managed bookkeeping.

## Curve editing

A focused `CurveEditor` consumes directions before window navigation, for both Steam's mirrored
arrow keys and SDL input. Both paths use identical semantics (left/right select a point, up/down
change its output) so whichever duplicate arrives first cannot change the result. Shift+left/right
remains the keyboard path for moving an interior point's input. Refused edits log the requested
operation and the current selection instead of appearing dead.

## Findings

### Raw input is observed, never intercepted

Never intercept mouse or keyboard input globally; `TouchSwipeMonitor` is the pattern, raw-input
observation only. The one low-level keyboard hook, in `KeyRecorder`, exists only during explicit
shortcut recording. Tap-outside dismissal is raw-observation hit-testing, deliberately not
dismiss-on-deactivate, because Next-app cycling deactivates the sheet while it must stay open.

### Avalonia's touch promotion produces a ghost click; the close is deferred 150 ms

Avalonia never marks touch raw events handled, so `WM_POINTER` reaches `DefWindowProc`, which
synthesizes a delayed mouse click (root-caused in Avalonia source). `OverlayController.CloseOverlay`
therefore defers the actual `Close()` by 150 ms, and `OverlayWindow`'s WndProc hook eats
`MI_WP_SIGNATURE`-tagged mouse messages. Removing either brings back ghost clicks that press buttons
in whatever sits under the sheet.

The swallowed click still carries `WM_MOUSEACTIVATE`. It re-activated the sheet after the tap's real
click had opened a status panel over it; two topmost windows order by activation, so the panel was
covered one frame after it appeared. Touch only: the mouse sends no second activation (reference
device, 2026-09-01). While a radio, audio or eject panel is open the sheet answers
`WM_MOUSEACTIVATE` with `MA_NOACTIVATE` (`OverlayWindow.SuppressMouseActivation`, kept in step by
`OverlayController.SyncSheetMouseActivation`): the click is still delivered, only the activation is
dropped. A real finger activates the sheet through `WM_POINTERACTIVATE`, which is unaffected, and
the tap-outside rule closes the panel on that tap anyway.

Tried and disproven: window ownership. Avalonia re-points every `ShowInTaskbar=false` window's owner
slot at its hidden helper on `Show()` and on every property update, so `Window.Show(owner)` and a
`GWLP_HWNDPARENT` write after `Show()` both left panel and sheet as siblings (z-order captures,
reference device, 2026-09-01).

### Avalonia's three-argument DispatcherTimer constructor auto-starts

`DispatcherTimer(interval, priority, callback)` starts the timer. This once made `IsRunning`
permanently true and silently broke every "start if not running" guard. Use the parameterless
constructor, `Tick +=` and an explicit `Start()` wherever `IsEnabled` is consulted.

### The focus-restore target and its suppression must not outlive an abandoned close

On close the sheet refocuses the window that was foreground when it opened (`_restoreFocusTo`,
captured in `ShowOverlay`): exclusive-fullscreen games sit minimized after the sheet took focus. The
refocus fires only in game mode (no Explorer in the session) and only when no overlay action
redirected focus. Every focus-redirecting action sets `_suppressFocusRestore`: Next-app cycling via
`PickWindow`, picking an Open apps chip, activating a tray icon. That suppression is load-bearing,
because the app the user just chose has to stay foreground.

A close that is cancelled because the sheet is re-shown inside the 150 ms deferral must clear both
fields. A latched suppression would silently disable the refocus for the rest of that sheet's life,
and a stale target would call back a window the user has since left. The fields live in
`OverlayController`; this is why they are reset in the cancelled-close path and not only in
`Closed`.
