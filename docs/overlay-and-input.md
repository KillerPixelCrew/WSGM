# Overlay surfaces and the input stack

How WSGM's fullscreen sheet works: its layout and navigation, the in-window utility, keyboard and
power-menu surfaces, the controller and raw-touch input stack, and the findings that shaped them.
Styling and headless rendering are in [UI mechanisms](ui.md). The
[Steam Input lease](steam-input.md) and the [device plugin system](device-plugin-system.md) document
their own integration boundaries.

## The quick access sheet

`OverlayWindow` covers the summoning application's display. It requests a transparent Avalonia
window and owns one `Avalonia.LiveBackdrop` attachment while open. A native companion window
directly behind it draws the live desktop through DirectComposition with an 8-pixel Gaussian blur by
default. Settings > Quick Access adjusts the radius from 0 to 60 physical pixels, and a config
reload updates an open sheet without creating another backdrop. The command-deck palette supplies
the translucent glass tint; a failed or unavailable attachment leaves an opaque canvas. The
compositor path works with Windows Transparency Effects disabled on the tested Claw. The fixed
top-right Close control is the touch dismissal path. Closing preserves the 150 ms touch-promotion
grace and the synthesized-mouse filter. No exposed game strip or global tap-outside observer
remains. The maintainer confirmed the integrated Overlay over Steam and a game on the Claw,
including bright/dark readability and no noticeable frame-time change. Battery-saver behavior and
physical touch still need attended validation for issue #114.

The fixed header carries the WSGM context, utility controls and status. Horizontal tabs select Quick
access, Steam, Device, Tools or Power. Each destination has a persistent one-third section rail
beside a two-thirds controls pane; both scroll independently. The workspace supports a 980 × 640 DIP
floor and a shared maximum width, and desktop scaling is capped to keep that minimum usable. Close,
the header and the bottom app/tray rail stay outside the scrolling workspace.

Steam offers Library and Per-game launch fixes; Tools offers System, Performance, Storage, Display,
Plugins, Keyboard and About; Power offers Wake, Idle timeouts, Power and Session. Those sections
open directly beside their rail rather than behind a category menu. Device has an Overview plus
sections derived from the current descriptors. Windows power schemes and Performance stay available
without device integration. Device > Power adds AC/battery assignments and presets when available;
Controller keeps glyph selection and explains unavailable output.

Quick access holds pinned actions, complete sections and plugin widgets. `AppConfig.QuickAccessPins`
stores stable action and section IDs. A pinned action invokes the same handler as its source. A
Device group such as Fans or Charging has one Pin section action in its heading; X, right-click and
touch hold inside a group target that group, including its nested editors. Pinned controls use the
source renderer and keep active drafts through a value refresh. An unavailable section stays
removable.

Open apps and tray icons share the bottom rail. `AppSwitcherViewModel` reconciles window chips in
place, so a refresh does not replace the focused chip. Y cycles to the next window; X on a tray icon
opens its context menu. Process and window enumeration runs off the UI thread, and only its detached
snapshot returns to Avalonia.

## Sections, nested pages and navigation

The session remembers each destination's selected section across tab switches and window reopen.
Rail selection and keyboard/controller focus are independent: moving focus to a peer does not change
the selected controls. Right from a rail row selects that section and enters its controls. LT/RT and
LB/RB switch destinations with wrap. An open utility, keyboard or power surface confines input to
itself; see "In-window surfaces" below. Re-summoning an open sheet returns to the selected section.
An ordinary focus or activation change leaves the current page intact.

B, Escape and the header Back button share one route. From primary controls, Back focuses the
selected rail row; from that rail, Back returns to Quick access; at home, Back dismisses the sheet.
A deeper editor pops one level and restores its invoker, so the Wake-lock list returns to Wake and
the Card Manager to Steam library. Switching destination unwinds nested views, then restores the
remembered primary section.

`OverlayNavigation` owns stable routes and section IDs. `OverlayWindow`'s `SubViews` table declares
the host, parent and teardown for nested pages. Add a nested page through that owner, so Back,
DefaultFocusTarget and leaving the destination agree. Pages are in-window controls, while selector
popups keep their owning ComboBox as the controller target. The fixed Back control stays reachable
when the content scrolls.

## Device sections, performance profiles and lighting color

Plugin-declared Device sections and WSGM-owned sections appear in the Device rail.
`OverlayPage.DevicePluginSection` carries the open section's id in the route rather than adding an
enum value per section. Rows are grouped under declared category headings in sort-then-snapshot
order, with groups assigned by measured column height. The SDK's closed prominence and pairing hints
guide presentation without allowing plugin markup. A section that vanishes with a descriptor
generation while its page is open renders a plain "no longer available" line. Leaving one runs the
same body as leaving a WSGM section: the glyph sample lease is released and both panels are redrawn.
The generic pop fallback it used to take did neither.

Per-application performance profiles belong to Device → Profiles. They are not a second detector and
not a device-plugin feature: `PerformanceOverlayBridge` projects the session's one
`PerformanceService` into closed rows. The value rows also sit beside the power controls on Device →
Power and thermals, including when Device Integration is off. An identity-only Steam game stays
visible as "executable pending", with its edits stored for that AppID until foreground observation
supplies the RTSS profile. Descriptor and layout identity changes rebuild the affected rows; value
publications update retained editors and readings in place. Profiles stays available whenever WSGM
has profile rows.

Device lighting color opens `DeviceColorView` rather than cycling an opaque integer in the row. The
hue field, RGB sliders and exact `#RRGGBB` entry are staged locally, and only the explicit Apply row
invokes the capability. That is a persistence constraint: the Claw has no volatile RGB path, so a
write on every controller step would wear and repeatedly commit its non-volatile lighting profile.
`DeviceColorSpectrum` consumes Left/Right like a horizontal slider, so Up/Down still move focus.

Brightness is not part of that editor. `lighting.brightness` is one device-wide value, because the
Claw's committed lighting profile carries a single brightness byte for all zones and separate colors
per zone. It renders as its own debounced slider row on the Lighting page; repeating it inside each
zone's color editor claimed a per-zone brightness the firmware does not have.

## Text entry

Text entry in the panel is a press-to-edit row, never a bare `TextBox`. Every editable name is a row
whose current value stays visible and whose click opens the in-window keyboard through
`KeyboardService.Request`. A `TextBox` in a panel looks editable but is unusable on a controller:
`GamepadNavigation` skips TextBoxes so the Windows touch keyboard cannot pop, so focus never lands
on one and nothing types. When `KeyboardService.Request` returns false there is no way to type at
all; log it rather than leaving a row that silently does nothing when pressed.

## The on-screen keyboard

Tools → Keyboard holds On-Screen Keyboard. It dismisses the sheet before invoking the current mode's
keyboard: Steam in Game Mode, the existing Windows touch-keyboard integration in Desktop Mode. A
failed request reopens the sheet with a warning. The Steam invocation replays Steam's native
Keyboard action through SteamUiToolkit on the window it observed; it never hands the physical
controller to Steam
([Steam surfaces from OEM buttons](steam-input.md#steam-surfaces-from-oem-buttons)).

## In-window surfaces

Radio, audio, brightness, removable-drive, text-entry and power controls share the fullscreen
overlay window. `OverlayController` creates one `GamepadNavigation` for that window. Opening a
utility surface keeps the overlay capture and input lease in place, and closing it returns focus to
the control that opened it. A transparent shield blocks pointer input to the deck without dimming it
or switching its controls to disabled styles. Keyboard Tab cycles within the surface. Controller
shoulders switch radio tabs and otherwise stay inside the current surface.

`RadioPanel`, `AudioPanel` and `EjectPanel` keep their manager operations, error reporting,
selectors and confirmation flows. Radio scanning ends when the panel is detached, and an unfinished
Bluetooth pairing ceremony is declined before its subscription is removed. Utility panels create no
native windows and acquire no navigation, input capture or lease of their own.

### Keyboard and credentials

`KeyboardService.Request` opens `KeyboardPanel` for an internal text field. The keyboard stretches
across the bottom of the overlay. Accept returns the text once; cancel leaves the original value
unchanged. Radio password and PIN entry use the same keyboard with masked input. The radio panel
stays attached below the keyboard, so its pending pairing ceremony and subscriptions stay alive.
Closing the keyboard restores the radio prompt, whose explicit Cancel action can decline the
ceremony.

The editor keyboard includes F1–F12, one-shot Ctrl/Alt/Shift/Win modifiers, latched Caps Lock, and
navigation and editing keys. Ctrl+A/C/X/V, selection and navigation use the local TextBox's real key
handling. Function and system keys raise local key events and issue no Windows shortcuts. The Num
pad view includes the numeric keys, Insert, Print Screen, Scroll Lock, Pause and Menu; Num Lock
switches the numeric keys between digits and navigation. Both views keep six rows with at least
44-DIP key targets, and switching views returns focus to their visible toggle.

The header, Tools and OEM on-screen-keyboard actions are external application requests. They use the
session's Steam or Windows keyboard integration and are separate from internal text editing.
Reopening the overlay cancels a pending external keyboard request, including its deferred-close
wait.

### Power menu

The centred 640-DIP power menu projects the Power destination's action controls into two columns.
Titles, explanations, availability and click handlers come from those same controls, so standby,
hibernate, restart, shutdown, sign-out and the Desktop/Game Mode transition share one handler each.
Restart, shutdown and sign-out keep the same five-second second-press confirmation in both
presentations, and closing the menu clears those confirmations. Focus starts on Keep playing. Back,
the close control, controller X and a repeated menu request cancel without executing a power action.

`OverlayController.ShowPowerMenu` opens the menu from the desktop or from an existing overlay. A new
desktop power surface acquires no Steam Input lease. Cancelling that standalone menu closes its
containing overlay; a menu opened from an existing overlay returns to that deck. `TogglePowerMenu`
treats a repeated request as cancellation. `TryConsumePowerPress` returns true while the menu
handles a short press, and its caller must then skip its ordinary sleep action. These entry points
install no physical power-button capture and change no Windows power-button policy; that integration
belongs to the separate hardware-button work.

### Closure and validation

All temporary controls live in `OverlayWindow.Surfaces.cs`. Surface closure disables its controls at
once and keeps the 150 ms touch promotion grace, and the containing window keeps its
synthesized-mouse filter. Overlay closure releases all surface contents before disposing their
managers. There is no raw-pointer outside-window hit test and no separate-window activation handoff.
Native file pickers still suspend controller navigation while open, because they own a separate
Windows dialog.

Headless regression source covers safe initial focus, cancellation and invoker focus return,
keyboard nesting and a single commit, credential masking, real editor key handling, numeric-keypad
navigation, 720p/980-DIP/4K-scaled keyboard bounds, shared power actions and confirmation reset.
Execution follows the repository's validation timing unless the maintainer explicitly requests early
tests. Native touch promotion, actual radio/audio/eject actions, desktop/game focus and the Steam
input handoff still require attended checks.

### Explicit choices in nested editors

Library-tab filter modes, category presets, review sources, time units, conditions and SD-card
scopes use labeled ComboBoxes. The option list includes every supported choice and keeps an existing
category preset or unavailable card reference until the user changes it. Selecting a local filter
value updates the staged model in place. A popup selection commits only when the popup closes, so
browsing intermediate values issues no repeated card-library writes. The card manager likewise
chooses explicit Steam-tab and hidden-state values rather than cycling state on an action button.

## Input stack

`Input\SdlGamepads` is the process-wide SDL3 owner with a single event pump. Two `GamepadService`
instances exist while Settings is open, and per-instance pumps would steal each other's hotplug
events. A 16 ms UI-thread `DispatcherTimer` poll produces edge-triggered `ButtonPressed` (with
direction auto-repeat) and full-state `StateChanged` (for chords), feeding `GamepadNavigation` and
`GamepadChordWatcher`. `GamepadNavigation` moves focus through tab order, synthesizes Enter to
activate, mirrors arrow keys with a 250 ms dedupe and skips TextBoxes.

`Overlay\TouchSwipeMonitor` observes the raw HID digitizer (`RIDEV_INPUTSINK`) for four configurable
edge swipes. It registers no mouse sink and consumes no touch input, so the foreground application
still receives its events. Settings exposes each edge binding:

| Edge   | Action                                                       |
| ------ | ------------------------------------------------------------ |
| top    | opens the sheet                                              |
| bottom | disabled by default; optionally opens Open apps in Game Mode |
| left   | sends Steam's installed-client mapping Ctrl+1 (Steam menu)   |
| right  | sends Ctrl+2 (Quick Access Menu)                             |

A swipe must start within 2 mm of the edge. The width comes from the digitizer's HID physical
extents, about 22 pixels on the Claw 8. A digitizer that reports no plausible length unit uses 2 %
of the panel axis instead, and the result is clamped to 8–48 pixels. Windows allows a precision
touchpad's first edge-swipe report to land up to 2 mm inside the edge, and the Claw's own digitizer
placed deliberate swipes 10 and 36 pixels in, so the earlier 4-pixel zone rejected real swipes.
Coordinates still map the digitizer's logical range to the primary panel.

Direction is decided once, when the net displacement from the first contact passes 16 pixels, and
that must happen within 400 ms. The contact is admitted when its inward movement exceeds its net
sideways movement; otherwise that edge is dropped and cannot recover. After entry, the swipe
triggers once it has moved 48 pixels inward within 800 ms of contact, with inward displacement at
least twice the net sideways displacement. Only net displacement counts. The previous recognizer
summed every report's sideways change and needed entry within 120 ms; on 2026-09-26 that rejected 8
of 9 deliberate top swipes, because digitizer jitter added up to 170–210 pixels of "sideways travel"
on swipes that ended 15 pixels off their starting column, and a finger resting 125–280 ms on the
bezel missed the entry window. The rule follows Android's back gesture, which judges direction from
net displacement after a slop within 250 ms, GNOME's edge drag, which starts within 20 pixels and
decides at 20 pixels, and HHD, which starts within 2 % and allows 400 ms.

An edge swipe is one finger. A report whose Contact Count exceeds one cancels the gesture until
every finger lifts. A hybrid-mode continuation report, whose Contact Count is zero, is skipped,
because its first slot holds a later contact rather than the primary one. Gesture timing uses the
high-resolution `Stopwatch` rather than the 15.6 ms `TickCount64`.

For calibration, enable Settings > System > Diagnostics > Verbose logging. `Touch edge trace:`
summarizes contacts within 64 pixels of an enabled edge, including starts rejected outside the zone.
Each summary records the first raw and physical coordinates, digitizer ranges, screen geometry, the
start-zone widths, time to the first 2-pixel motion, the 16-pixel entry time, elapsed time, summed
path length and outcome. The digitizer's physical size and Contact Count support are logged once per
device. Output is limited to one summary per contact and 12 per minute, with no per-report logging.
The regression traces in `TouchSwipeMonitorTests` are synthetic; the ones modelled on the 2026-09-26
rejections are reconstructions from those summaries, not attended recordings.

Live device and performance publications may request a redraw while a finger or mouse button is
down. The sheet coalesces those redraws and defers them until the routed pointer release has
completed, because replacing a release-mode Avalonia `Button` between its press and release drops
its `Click` and looks exactly like a control that needs a second tap.

On the desktop Explorer's taskbar owns the bottom edge; falling back to the sheet there read as a
regression (device-reported). Left and right always send their keys, including while a game is
foreground, because bringing Steam's menu over the game is their purpose.

## Managed-controller capture and source switching

Each owning top-level WSGM window has a named capture claim, and in-window overlay surfaces keep
that window's claim and navigation owner. The first claim neutralizes the virtual target, nested
claims keep capture active, and the last close resumes game forwarding only after every control the
UI used is released. Sleep, session lock, controller release and source faults share one
forwarding-blocked state, cleared by a successfully created or replaced target, or by
`ControllerManager.ResumeForwardingAsync` on wake and unlock. There is no parallel enum of
hypothetical zero reasons: capture, routing admission and target state decide delivery.

WSGM's own navigation reads the managed controller as one more pad. Every sample the plugin
publishes writes its buttons into `ManagedUiPad`, allocation-free, and `GamepadService` reads that
pad on its 16 ms UI-thread poll exactly as it reads an SDL pad, so edges, direction auto-repeat and
chords behave the same and a control already held when a surface opens produces no press. While
controller management is Active the managed pad is the only pad the UI reads, because SDL sees the
same hands through the virtual controller; SDL is still pumped for hotplug, and the SDL pads leave
through the ordinary stale-pad release so a chord in progress on one cannot stay held. When
management leaves Active the managed pad clears its buttons and the UI reads SDL again.

| Source      | Synthesized trigger threshold |
| ----------- | ----------------------------- |
| SDL         | 8000/32767 (about 0.24)       |
| managed pad | 0.5                           |

The difference is long-shipped behavior; align the two only with device re-verification.

A VIIPER submission failure is also a target-lifetime event: `DeviceRemove` runs before WSGM forgets
the handle, because the native device object and feedback callback otherwise outlive the managed
bookkeeping.

## Curve editing

A focused `CurveEditor` consumes directions before window navigation, for both Steam's mirrored
arrow keys and SDL input. Both paths use the same semantics (left/right select a point, up/down
change its output), so whichever duplicate arrives first cannot change the result. Shift+left/right
stays the keyboard path for moving an interior point's input. Refused edits log the requested
operation and the current selection instead of appearing dead.

## Findings

### Raw input is observed, never intercepted

Never intercept mouse or keyboard input globally; `TouchSwipeMonitor` is the pattern, raw-input
observation only. The main-app low-level keyboard hook, in `KeyRecorder`, exists only during
explicit shortcut recording, and device-specific interception belongs to its plugin. The overlay
uses explicit dismissal rather than deactivation, because Next-app cycling can deactivate a sheet
that must stay open.

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
z-order captures. The in-window surfaces introduced for #114 removed that peer-window handoff, so
those dated captures do not validate the new surface lifetime.

### Avalonia's three-argument DispatcherTimer constructor auto-starts

`DispatcherTimer(interval, priority, callback)` starts the timer. This once made `IsRunning`
permanently true and silently broke every "start if not running" guard. Use the parameterless
constructor, `Tick +=` and an explicit `Start()` wherever `IsEnabled` is consulted.

### The focus-restore target and its suppression must not outlive an abandoned close

Picking an Open apps chip waits for the sheet's 150 ms deferred close and its Steam Input lease
release before activating the destination. The chip keeps both HWND and owning PID: a reused handle
replaces the chip, and activation refuses a changed owner. Reopening the sheet, selecting another
chip or shutting down cancels the pending return.

For a non-console destination, the resident session makes one experimental Steam activation attempt
through `SteamGameWindowActivation`. Steam must already expose exactly one game-overlay window for
that PID; its full GameID string supplies `RaiseWindowForGame`, and WSGM then restores and focuses
the exact selected HWND, even if Steam chose a mod loader's console. This uses the selected window
and Steam's own overlay association directly, rather than guessing an HWND from RTSS's process
identity or Steam's preferred main window. Multiple games do not authorize a fallback to another
process. CEF disabled, unavailable or incompatible leaves ordinary exact-window activation
available.

The call borrows the session's Steam transport and requires a ready SharedJSContext generation. It
has a one-second budget and an in-page expiry check, and involves no launch, subscription, retry or
Big Picture fallback. A timed-out or cancelled call may already have reached Steam and cannot be
recalled, so no later WSGM focus action runs after a cancellation.

`Game return:` logs distinguish completion of the Steam call from verified Windows foreground.
Neither proves overlay rendering or controller routing recovered. The maintainer's successful
Balatro return test is recorded in [steam-cef.md](steam-cef.md); other games and a comparison with
keyboard Shift+Tab remain unverified. The implementation does not reinject DLLs or continuously
force foreground focus.

On close the sheet refocuses the window that was foreground when it opened (`_restoreFocusTo`,
captured in `ShowOverlay`), because an exclusive-fullscreen game sits minimized after the sheet took
focus. The refocus fires only in game mode (no Explorer in the session) and only when no overlay
action redirected focus. Every focus-redirecting action sets `_suppressFocusRestore`: Next-app
cycling via `PickWindow`, picking an Open apps chip, activating a tray icon. That suppression is
load-bearing, because the app the user just chose has to stay foreground.

A close that is cancelled because the sheet is re-shown inside the 150 ms deferral must clear both
fields. A latched suppression would silently disable the refocus for the rest of that sheet's life,
and a stale target would call back a window the user has since left. The fields live in
`OverlayController`, which is why they are reset in the cancelled-close path and not only in
`Closed`.

Panel brightness is available in Tools → Display through the resident session's shared brightness
service. Steam QAM and the Overlay use the same serialized writes and confirmed readback. The slider
stays in place during updates, disables when readback is unavailable, and works with CEF disabled.

The same page shows resolution and refresh for the first active display in Windows path-priority
order. Pickers hold driver-validated modes, and changing resolution updates the offered refresh
rates. Only Apply changes the display. Fresh observations every five seconds while open replace
stale choices after a reconnect, resume or profile change. Windows Device Control rechecks target
identity and driver validation before applying, confirms readback and attempts rollback on an
unconfirmed write. Ambiguous clone sources are unavailable. None of this establishes physical
visibility.

Device provides a manual TDP mode selector when a paired capability is available. Selecting Unified
saves the active global or per-game preference without writing hardware, and later sustained-slider
edits coordinate both limits through the plugin. Advanced mode keeps independent edits. In unified
TDP mode the primary row is labeled TDP and the boost row becomes read-only; switching to split
restores independent editing without changing the observed limits.

Open ComboBox popups keep their owning selector as the controller navigation target even when
Avalonia focuses a popup item. D-pad selection stays inside the selector, and confirmation closes
the popup and restores focus to the selector before another control is activated.

## Display controls

The brightness slider uses the session brightness owner. After one accepted hardware write, that
owner waits up to 500 ms for readback to settle without repeating the write. The percentage stays in
the heading; unavailable or unconfirmed results get a separate message below the slider.

Resolution and refresh-rate dropdowns apply the committed selection directly. Browsing an open popup
does not switch display modes; closing it commits once, and selecting the already active mode does
nothing. Editors are disabled during a mode change, then restored from fresh readback. There is no
separate Apply display mode button. Windows Device Control keeps its route checks, validation and
rollback behavior.

### Application profiles

Global / Per-application stays in the top bar on every destination. The adjacent profile-list button
opens a scrollable editor for saved profiles, their names, activation executable names and
performance overrides. Profiles can be configured while their applications are closed. Disabling
preserves values; deleting requires a second explicit click. See [RTSS](rtss.md) for matching,
persistence and device scope.
