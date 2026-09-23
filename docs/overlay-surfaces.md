# In-window overlay surfaces

Radio, audio, brightness, removable-drive, text-entry and power controls share the fullscreen
overlay window. `OverlayController` creates one `GamepadNavigation` for that window. Opening a
utility surface keeps the overlay capture and input lease in place; closing it returns focus to the
control that opened it. A transparent shield blocks pointer input to the deck without dimming it or
changing its controls to disabled styles. Keyboard Tab cycles within the surface. Controller
shoulders switch radio tabs and otherwise stay inside the current surface.

`RadioPanel`, `AudioPanel` and `EjectPanel` keep the existing manager operations, error reporting,
selectors and confirmation flows. Radio scanning ends when the panel is detached. An unfinished
Bluetooth pairing ceremony is declined before its subscription is removed. Utility panels do not
create native windows or acquire their own navigation, input capture or lease.

## Keyboard and credentials

`KeyboardService.Request` opens `KeyboardPanel` for an internal text field. The keyboard stretches
across the bottom of the overlay. Accept returns text once; cancel leaves the original value
unchanged. Radio password and PIN entry use the same keyboard with masked input. The radio panel
stays attached below the keyboard so its pending pairing ceremony and subscriptions remain alive.
Closing the keyboard restores the radio prompt, where its explicit Cancel action can decline the
ceremony.

The editor keyboard includes F1–F12, one-shot Ctrl/Alt/Shift/Win modifiers, latched Caps Lock,
navigation and editing keys. Ctrl+A/C/X/V and selection/navigation use the local TextBox's real key
handling. Function and system keys raise local key events; they do not issue Windows shortcuts. The
Num pad view includes numeric keys, Insert, Print Screen, Scroll Lock, Pause and Menu. Num Lock
switches numeric keys between digits and navigation. Both views retain six rows with at least 44-DIP
key targets. Switching views returns focus to their visible toggle.

The header, Tools and OEM on-screen-keyboard actions remain external application requests. It uses
the session's existing Steam or Windows keyboard integration and is separate from internal text
editing. Reopening the overlay cancels a pending external keyboard request, including its
deferred-close wait.

## Power menu

The centred 640-DIP power menu projects the Power destination action controls into two columns.
Titles, explanations, availability and click handlers come from those same controls. It reuses the
handlers for standby, hibernate, restart, shutdown, sign-out and the Desktop/Game Mode transition.
Restart, shutdown and sign-out keep the same five-second second-press confirmation state in both
presentations. Closing the menu clears those confirmations. Focus starts on Keep playing; Back, the
close control, controller X and a repeated menu request cancel without executing a power action.

`OverlayController.ShowPowerMenu` is the entry point for opening from the desktop or an existing
overlay. A new desktop power surface does not acquire a Steam Input lease. Cancelling that
standalone menu closes its containing overlay; a menu opened from an existing overlay returns to
that deck. `TogglePowerMenu` treats a repeated request as cancellation. `TryConsumePowerPress`
returns true while the menu handles a short press, and its caller must skip its ordinary sleep
action in that case. These entry points do not install physical power-button capture or change
Windows power-button policy; that integration belongs to the separate hardware-button work.

## Closure and validation

All temporary controls live in `OverlayWindow.Surfaces.cs`. Surface closure disables its controls
immediately and retains the 150 ms touch promotion grace, and the containing window retains its
synthesized-mouse filter. Overlay closure releases all surface contents before disposing their
managers. There is no raw-pointer outside-window hit test or separate-window activation handoff.
Native file pickers still temporarily suspend controller navigation because they own a separate
Windows dialog.

Headless regression source covers safe initial focus, cancellation and invoker focus return,
keyboard nesting and a single commit, credential masking, real editor key handling, numeric-keypad
navigation, 720p/980-DIP/4K-scaled keyboard bounds, shared power actions, and confirmation reset.
Execution follows the repository's validation timing unless the maintainer explicitly requests early
tests. Native touch promotion, actual radio/audio/eject actions, desktop/game focus and Steam input
handoff still require attended checks.

## Explicit choices in nested editors

Library-tab filter modes, category presets, review sources, time units, conditions and SD-card
scopes use labeled ComboBoxes. The option list includes every supported choice and preserves an
existing category preset or unavailable card reference until the user changes it. Selecting a local
filter value updates the staged model in place. A popup selection commits only when the popup
closes, so browsing intermediate values does not issue repeated card-library writes. The card
manager likewise chooses explicit Steam-tab and hidden-state values rather than cycling state on an
action button.
