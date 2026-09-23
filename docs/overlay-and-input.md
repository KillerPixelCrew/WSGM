# Overlay surfaces and the input stack

How WSGM's fullscreen sheet, controller navigation and raw touch recognizer work. Styling and
headless rendering are in [UI mechanisms](ui.md); utility, keyboard and power-menu ownership are in
[in-window surfaces](overlay-surfaces.md). The [Steam Input lease](steam-input.md) and
[Device plugin system](device-plugin-system.md) document their separate integration boundaries.

## The quick access sheet

`OverlayWindow` covers the summoning application's display. Avalonia requests live DWM acrylic or
blur, and the command-deck palette supplies the glass tint and opaque fallback. The fixed top-right
Close control is the touch dismissal path. Closing preserves the 150 ms touch-promotion grace and
synthesized-mouse filter. No exposed game strip or global tap-outside observer remains. Native blur,
battery-saver behavior and physical touch acceptance need attended validation for issue #114.

The fixed header carries the WSGM context, utility controls and status. Horizontal tabs select Quick
access, Steam, Device, Tools or Power. Each destination has a persistent one-third section rail
beside a two-thirds controls pane; both scroll independently. The workspace supports a 980 × 640 DIP
floor and a shared maximum width. Desktop scaling is capped to keep that minimum usable. Close, the
header and the bottom app/tray rail remain outside the scrolling workspace.

Steam offers Library and Per-game launch fixes; Tools offers System, Performance, Storage, Display,
Plugins and Controller ownership; Power offers Wake, Idle timeouts, Power and Session. Those
sections open directly beside their rail rather than behind a category menu. Device has an Overview
plus sections derived from current descriptors. Windows power schemes and Performance remain
available without device integration. Device > Power adds AC/battery assignments and presets when
available; Controller retains glyph selection and explains unavailable output.

Quick access contains pinned actions, complete sections and plugin widgets.
`AppConfig.QuickAccessPins` stores stable action and section IDs. Pinned actions invoke the same
handler as their source. A Device group such as Fans or Charging has one Pin section action in its
heading; X, right-click and touch hold inside a group target that group, including its nested
editors. Pinned controls use the source renderer and preserve active drafts during value refresh.
Unavailable sections remain removable.

Open apps and tray icons share the bottom rail. `AppSwitcherViewModel` reconciles window chips in
place so refresh does not replace the focused chip. Y cycles to the next window; X on a tray icon
opens its context menu. Process and window enumeration runs off the UI thread; only its detached
snapshot returns to Avalonia.

## Sections, nested pages and navigation

The session remembers each destination's selected section across tab switches and window reopen.
Rail selection and keyboard/controller focus are independent: moving focus to a peer does not change
the selected controls. Right from a rail row selects that section and enters its controls. LT/RT and
LB/RB switch destinations with wrap. An open utility, keyboard or power surface confines input to
itself; see [surface ownership](overlay-surfaces.md). Re-summoning an already open sheet returns to
the selected section. An ordinary focus or activation change leaves its current page intact.

B, Escape and the header Back button share the same route handling. From primary controls, Back
focuses the selected rail row; from that rail, Back returns to Quick access. At home, Back dismisses
the sheet. A deeper editor pops one level and restores its invoker. The Wake-lock list therefore
returns to Wake, and the Card Manager to Steam library. Switching destination unwinds nested views,
then restores the remembered primary section.

`OverlayNavigation` owns stable routes and section IDs. `OverlayWindow`'s `SubViews` table declares
the host, parent and teardown for nested pages. Add a nested page through that owner so Back,
DefaultFocusTarget and leaving the destination agree. Pages are in-window controls, while actual
selector popups retain their owning ComboBox as the controller target. The fixed Back control stays
reachable when the content scrolls.

## Device sections, performance profiles and lighting color

Plugin-declared Device sections and WSGM-owned sections appear in the Device rail.
`OverlayPage.DevicePluginSection` carries the open section's id in the route rather than adding an
enum value per section. Rows are grouped under declared category headings in sort-then-snapshot
order, with groups assigned by measured column height. The SDK's closed prominence and pairing hints
guide presentation without allowing plugin markup. A section that vanishes with a descriptor
generation while its page is open renders a plain "no longer available" line. Leaving one runs the
same body as leaving a WSGM section: the glyph sample lease is released and both panels are redrawn,
which the generic pop fallback it used to take did neither of.

Per-application performance profiles belong to Device → Profiles; they are not a second detector and
not a device-plugin feature. `PerformanceOverlayBridge` projects the session's one
`PerformanceService` into closed rows. The value rows also sit beside the power controls on Device →
Power and thermals, including when Device Integration is off. Identity-only Steam games stay visible
as "executable pending", with edits stored for that AppID until foreground observation supplies the
RTSS profile. Descriptor/layout identity changes rebuild the affected rows; value publications
update retained editors and readings in place. Profiles stays available whenever WSGM has profile
rows.

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
whose current value remains visible and whose click opens the in-window keyboard through
`KeyboardService.Request`. A `TextBox` in a panel looks editable but is unusable on a controller:
`GamepadNavigation` skips TextBoxes so the Windows touch keyboard cannot pop, so focus never lands
on one and nothing types. When `KeyboardService.Request` returns false there is no way to type at
all; log it rather than leaving a row that silently does nothing when pressed.

## Controller ownership and the on-screen keyboard

Tools → Controller ownership shows the controller ownership status and offers Release to Steam and
Reacquire for WSGM. The session's Steam handoff coordinator owns both actions. A manual release
stays in force across native surface closure until explicit reacquisition; touch remains available
while WSGM's controller readers pause.

On-Screen Keyboard, beside them, dismisses the sheet before invoking the current mode's keyboard:
Steam in Game Mode, the existing Windows touch-keyboard integration in Desktop Mode. A failed
request reopens the sheet with a warning. The Steam invocation lives in SteamUiToolkit and uses the
session's ownership handoff; native keyboard visibility prevents early reacquisition. A manual
release takes precedence.

## Input stack

`Input\SdlGamepads` is the process-wide SDL3 owner with a single event pump. Two `GamepadService`
instances exist while Settings is open, and per-instance pumps would steal each other's hotplug
events. A 16 ms UI-thread `DispatcherTimer` poll produces edge-triggered `ButtonPressed` (with
direction auto-repeat) and full-state `StateChanged` (for chords), feeding `GamepadNavigation` and
`GamepadChordWatcher`. `GamepadNavigation` moves focus through tab order, synthesizes Enter to
activate, mirrors arrow keys with a 250 ms dedupe and skips TextBoxes.

`Overlay\TouchSwipeMonitor` observes the raw HID digitizer (`RIDEV_INPUTSINK`) for four configurable
edge swipes. It registers no mouse sink and consumes no touch input; the foreground application
still receives its events. Settings exposes each edge binding:

| Edge   | Action                                                       |
| ------ | ------------------------------------------------------------ |
| top    | opens the sheet                                              |
| bottom | disabled by default; optionally opens Open apps in Game Mode |
| left   | sends Steam's installed-client mapping Ctrl+1 (Steam menu)   |
| right  | sends Ctrl+2 (Quick Access Menu)                             |

`GestureConfig.StripThickness` is the first-contact bezel-zone width in physical pixels, not the
required swipe travel. New configurations default to 4; recognition clamps saved values to 1–8
without rewriting them. An existing value of 16 therefore uses an 8-pixel zone. The previous hidden
48-pixel minimum is gone. Coordinates still map the digitizer's logical range to the primary panel.

Entry requires 8 pixels of inward movement within 120 ms. The completed swipe needs 48 pixels within
800 ms, with inward displacement at least twice the accumulated sideways travel. A delayed entry or
sideways drag cannot recover by moving inward later. These are provisional thresholds, not
calibrated Claw measurements. A very fast drag starting exactly at the bezel can still resemble a
swipe; attended traces determine whether the limits need adjustment.

For calibration, enable Settings > System > Diagnostics > Verbose logging. `Touch edge trace:`
summarizes contacts within 48 pixels of an enabled edge, including starts rejected outside the
narrow zone. Each summary records first raw and physical coordinates, digitizer ranges, screen
geometry, time to the first 2-pixel motion, admitted 8-pixel entry time, elapsed time, travel and
outcome. Output is limited to one summary per contact and 12 per minute, with no per-report logging.
Compare real bezel swipes, maximized-title-bar drags and slow edge touches. The regression traces in
`TouchSwipeMonitorTests` are synthetic and do not establish attended calibration.

Live device and performance publications may request a redraw while a finger or mouse button is
down. The sheet coalesces those redraws and defers them until the routed pointer release has
completed; replacing a release-mode Avalonia `Button` between its press and release drops its
`Click` and looks exactly like a control that needs a second tap.

On the desktop Explorer's taskbar owns the bottom edge; falling back to the sheet there read as a
regression (device-reported). Left and right always send their keys, including while a game is
foreground, because bringing Steam's menu over the game is their purpose.

## Managed-controller capture and source switching

Each owning top-level WSGM window has a named capture claim. In-window overlay surfaces retain that
window's claim and navigation owner. The first claim neutralizes the virtual target, nested claims
keep capture active, and the last close resumes game forwarding only after every control the UI used
is released. Lifecycle handoffs and source faults share one forwarding-blocked state, cleared only
by a successfully created or replaced target. There is no parallel enum of hypothetical zero
reasons: capture, routing admission and target state decide delivery.

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
observation only. The main-app low-level keyboard hook, in `KeyRecorder`, exists only during
explicit shortcut recording; device-specific interception belongs to its plugin. The overlay uses
explicit dismissal rather than deactivation because Next-app cycling can deactivate a sheet that
must stay open.

### Avalonia's touch promotion produces a ghost click; the close is deferred 150 ms

Avalonia never marks touch raw events handled, so `WM_POINTER` reaches `DefWindowProc`, which
synthesizes a delayed mouse click (root-caused in Avalonia source). `OverlayController.CloseOverlay`
therefore defers the actual `Close()` by 150 ms, and `OverlayWindow`'s WndProc hook eats
`MI_WP_SIGNATURE`-tagged mouse messages. Removing either brings back ghost clicks that press buttons
in whatever sits under the sheet.

Historical evidence, reference device, 2026-09-01: a promoted click carried `WM_MOUSEACTIVATE` and
raised the sheet above a separate topmost status window one frame after it opened. The old design
suppressed that activation while its peer window was open. Window ownership did not fix it: Avalonia
reassigned `ShowInTaskbar=false` owners to its hidden helper, leaving the windows as siblings in
z-order captures. The in-window surfaces introduced for #114 remove that peer-window handoff; those
dated captures are not validation of the new surface lifetime.

### Avalonia's three-argument DispatcherTimer constructor auto-starts

`DispatcherTimer(interval, priority, callback)` starts the timer. This once made `IsRunning`
permanently true and silently broke every "start if not running" guard. Use the parameterless
constructor, `Tick +=` and an explicit `Start()` wherever `IsEnabled` is consulted.

### The focus-restore target and its suppression must not outlive an abandoned close

Picking an Open apps chip waits for the sheet's 150 ms deferred close and its Steam Input lease
release before activating the destination. The chip retains both HWND and owning PID; a reused
handle replaces the chip, and activation refuses a changed owner. Reopening the sheet, selecting
another chip or shutting down cancels the pending return.

For a non-console destination, the resident session makes one experimental Steam activation attempt
through `SteamGameWindowActivation`. Steam must already expose exactly one game-overlay window for
that PID. Its full GameID string supplies `RaiseWindowForGame`; WSGM then restores and focuses the
exact selected HWND, even if Steam chose a mod loader's console. This uses the selected window and
Steam's own overlay association directly, rather than guessing an HWND from RTSS's process identity
or Steam's preferred main window. Multiple games do not authorize a fallback to another process. CEF
disabled, unavailable or incompatible leaves ordinary exact-window activation available.

`Game return:` logs distinguish completion of the Steam call from verified Windows foreground.
Neither proves overlay rendering or controller routing recovered. The maintainer's successful
Balatro return test is recorded in `steam-cef.md`; other games and comparison with keyboard
Shift+Tab remain unverified. The implementation does not reinject DLLs or continuously force
foreground focus.

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

Panel brightness is available in Tools → Display through the resident session's shared brightness
service. Steam QAM and Overlay use the same serialized writes and confirmed readback. The slider
remains in place during updates, disables when readback is unavailable, and works with CEF disabled.

The same page shows resolution and refresh for the first active display in Windows path-priority
order. Pickers contain driver-validated modes; changing resolution updates the offered refresh
rates. Only Apply changes the display. Fresh observations every five seconds while open replace
stale choices after reconnect, resume or profile changes. Windows Device Control rechecks target
identity and driver validation before applying, confirms readback and attempts rollback on an
unconfirmed write. Ambiguous clone sources are unavailable. This does not establish physical
visibility.

Device provides a manual TDP mode selector when a paired capability is available. Selecting Unified
saves the active global/per-game preference without writing hardware. Subsequent sustained-slider
edits coordinate both limits through the plugin. Advanced mode keeps independent edits.

In unified TDP mode, the primary row is labeled TDP and the boost row becomes read-only. Switching
to split restores independent editing without changing the observed limits.

Open ComboBox popups retain their owning selector as the controller navigation target even when
Avalonia focuses a popup item. D-pad selection stays inside the selector, and confirmation closes
the popup and restores focus to the selector before another control is activated.

## Display controls

The brightness slider uses the session brightness owner. After one accepted hardware write, that
owner waits up to 500 ms for readback to settle without repeating the write. The percentage remains
in the heading; unavailable or unconfirmed results have a separate message below the slider.

Resolution and refresh-rate dropdowns apply the committed selection directly. Browsing an open popup
does not switch display modes; closing it commits once, and selecting the already active mode does
nothing. Editors are disabled during a mode change, then restored from fresh readback. There is no
separate Apply display mode button. Windows Device Control retains its route checks, validation and
rollback behavior.

### Application profiles

Global / Per-application stays in the top bar on every destination. The adjacent profile-list button
opens a scrollable editor for saved profiles, their names, activation executable names and
performance overrides. Profiles can be configured while their applications are closed. Disabling
preserves values; deleting requires a second explicit click. See `rtss.md` for matching, persistence
and device scope.
