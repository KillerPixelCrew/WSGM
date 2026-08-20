# SD cards and card libraries

Device-verified behaviour and the reasoning behind it. These are findings, not style: where a
section says device-verified or live-verified, it encodes something that only revealed itself on
real hardware or against a live Steam client, and changing it without re-verifying is a regression
waiting to happen.

Card work is split across three places, because the card is only ever half the story:

- **This file** covers the card manager and format UI.
- **`docs\steam-cef.md`** covers how a library is registered with a RUNNING Steam, including the
  duplicate-registration behaviour that makes a swapped card show the previous card's games, and the
  reconcile that runs on volume arrival and removal.
- **`src\WSGM\Shell\AGENTS.md`** carries the format mechanism itself - the three-diskpart sequence
  and the volume-arrival wait it exists to survive.

**Format SD Card lives inside the Card Manager**, not the Tools list (maintainer): formatting a card
and managing tracked cards are one subject. `CardManagerView` raises `FormatRequested`, the overlay
leaves that sub-view and enters `PanelFormat` (two Tools sub-views must never own the surface at
once), and Cancel/Back returns to the Card Manager via `LeaveFormatSubViewToOrigin` — which rescans,
so a card that was just formatted appears immediately. The two feature toggles stay independent:
`OverlayViewModel.ShowFormatInTools` (`ShowSdCard && !ShowCardManager`) brings the Tools button back
when the Card Manager is switched off, so `Cef.SdFormat` can never be on with no way to reach it.
`_formatReturnsToCards` is cleared in `LeaveFormatSubView` so the `Activated` teardown cannot bounce
a fresh summon back into the Card Manager.
