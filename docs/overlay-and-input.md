# Overlay surfaces and the input stack

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

**Quick access panel** (`Overlay\OverlayWindow`): a `TabStrip` over three always-alive panels —
Session / Tools / Power — LB/RB cycling with wrap (via `GamepadNavigation`'s optional
`tabPrevious`/`tabNext`), reopening on Session, focus landing on the first row after a switch, and
the warning `InfoBar` staying panel-level above the tabs. `DefaultFocusTarget` resolves to the
ACTIVE tab's first row (HomeAppButton is invisible on the other tabs). Note the taskbar's navigation
deliberately passes NO tab callbacks: during the 150 ms surface handover both navigations are alive,
so routing LB/RB there would double-advance the panel's tabs. The Tools tab hosts five in-place
sub-views (`PanelFormat`, `LibraryTabsView`, `CardManagerView`, `ArtworkView`, `LaunchWrapperView`);
adding one means extending `AnySubView`, `DefaultFocusTarget`, `TryCancelSubView`, the `Activated`
teardown, and an `Enter*`/`Leave*` pair — never a Popup/Flyout, which `GamepadNavigation` cannot
reach.

**Text entry in the panel is a press-to-edit ROW, never a bare `TextBox`** (maintainer, on the
format name reading as broken). Every editable name — the tab editor, card rename, filter patterns —
is a `CardButton`/`Row` whose Description shows the current value and whose click opens the peer
keyboard window through `KeyboardService.Request`. A `TextBox` dropped into a panel looks editable
but is unusable on a controller: `GamepadNavigation` deliberately skips TextBoxes so the Windows
touch keyboard cannot pop, so focus never lands on it and nothing types. The format library name was
the last holdout and is now a row like the rest. When `KeyboardService.Request` returns false there
is no way to type at all — log it rather than leaving a row that silently does nothing when pressed.

**Input stack** (`Input\`): `SdlGamepads` is the process-wide SDL3 owner (single event pump — two
`GamepadService` instances exist when Settings is open; per-instance pumps would steal hotplug
events). UI-thread 16 ms `DispatcherTimer` poll → edge-triggered `ButtonPressed` (+ direction
auto-repeat) and full-state `StateChanged` (chords) → `GamepadNavigation` (focus movement through
tab order, synthesized Enter to activate, arrow-key mirror with 250 ms dedupe, skips TextBoxes so
the touch keyboard doesn't pop) and `GamepadChordWatcher`. `Overlay\TouchSwipeMonitor` observes the
raw HID digitizer (`RIDEV_INPUTSINK`, observation only) for four configurable edge swipes _and_
tap-outside-overlay dismissal. Bottom/right retain WSGM's taskbar/quick-access actions; left/top
always send Steam's installed-client keyboard mappings Ctrl+1 (Steam menu) / Ctrl+2 (Quick Access
Menu), including while a game is foreground — bringing Steam's menu over the game is their purpose.

**Managed-controller capture and source changes**: each visible WSGM surface owns one named capture
claim. The first claim neutralizes the virtual target; nested claims keep capture active, and the
last close resumes game forwarding only after every control used by the UI is released. Lifecycle
handoffs and source faults use one forwarding-blocked state which is cleared only by a successfully
created or replaced target. There is deliberately no parallel enum of hypothetical zero reasons:
capture, routing admission, and target state are the mechanisms that actually decide delivery.

WSGM navigation switches to managed canonical input only after its first complete sample and keeps
SDL live as the ready fallback. Controls held across that switch stay suppressed until released or
for at most two seconds when the incoming source cannot observe them. Every completed source switch
logs the old source, new source, suppression mask, and managed-health state. A VIIPER submission
failure is also a target-lifetime event: `DeviceRemove` runs before WSGM forgets the handle, because
the native device object and feedback callback outlive managed bookkeeping otherwise.

**Curve editing**: a focused `CurveEditor` consumes directions before window navigation for both
Steam's mirrored arrow keys and SDL input. Both paths deliberately use identical semantics—left and
right select a point; up and down change its output—so whichever duplicate arrives first cannot
change the result. Shift+left/right remains the keyboard path for moving an interior point's input.
Refused edits log the requested operation and current selection instead of silently appearing dead.

2. **Never intercept mouse or keyboard globally** — raw-input _observation_ only (TouchSwipeMonitor
   pattern). The low-level keyboard hook in `KeyRecorder` exists only during explicit shortcut
   recording.

3. **Avalonia touch promotion bug** (root-caused in Avalonia source): Avalonia never marks touch raw
   events handled, so `WM_POINTER` reaches `DefWindowProc`, which synthesizes a delayed mouse click.
   Hence: `OverlayController.CloseOverlay` defers the actual `Close()` by 150 ms, and
   `OverlayWindow`'s WndProc hook eats `MI_WP_SIGNATURE`-tagged (touch-synthesized) mouse messages.
   Removing either brings back ghost clicks that press buttons in whatever sits under the panel.

4. **Avalonia's 3-arg `DispatcherTimer(interval, priority, callback)` ctor auto-starts the timer.**
   This once made `IsRunning` permanently true and silently broke every "start if not running"
   guard. Use the parameterless ctor + `Tick +=` + explicit `Start()` when `IsEnabled` is consulted.

5. **Overlay dismissal refocuses only under strict gates** (intentional since b7234f8): on close,
   the overlay calls back the window that was foreground when it opened (`_restoreFocusTo`, captured
   in ShowOverlay) — exclusive-fullscreen games sit minimized after the panel took focus. The
   refocus fires **only** when no overlay action redirected focus (every focus-redirecting action,
   including Next-app cycling via `PickWindow`, sets `_suppressFocusRestore`) **and** only in game
   mode (no explorer in the session). That suppression is load-bearing for Next-app cycling, which
   depends on the switched-to window staying foreground. Tap-outside dismissal is raw-observation
   hit-testing, deliberately not dismiss-on-deactivate (cycling deactivates the panel while it must
   stay open).
