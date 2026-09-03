# SD cards and card libraries

The card manager and the Format SD Card flow in the overlay. Registering a card's library with a
running Steam, the duplicate-registration behaviour that makes a swapped card show the previous
card's games, and the reconcile on volume arrival and removal are in `docs\steam-cef.md`
("Registering a library with a running Steam"). The format mechanism itself, the three-diskpart
sequence and the volume-arrival wait it survives, is beside the code in `src\WSGM\Shell\AGENTS.md`.

## Format SD Card lives inside the Card Manager

Formatting a card and managing tracked cards are one subject, so the Format button is a Card Manager
action rather than a Tools entry. `CardManagerView` raises `FormatRequested`; the overlay leaves
that sub-view and enters `PanelFormat`, because two Tools sub-views must not own the surface at
once. Cancel and Back return to the Card Manager through `LeaveFormatSubViewToOrigin`, which
rescans, so a card that was just formatted appears immediately.

The two feature toggles stay independent. `OverlayViewModel.ShowFormatInTools`
(`ShowSdCard && !ShowCardManager`) brings the Tools button back when the Card Manager is switched
off, so `Cef.SdFormat` can never be on with no way to reach it. `_formatReturnsToCards` is cleared
in `LeaveFormatSubView` so the `Activated` teardown cannot bounce a fresh summon back into the Card
Manager.
