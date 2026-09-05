# Overlay

Overlay owns the game-facing sheet, its navigation model, capture and input handoff, Steam
presentation, and overlay view models. Read docs/overlay-and-input.md, docs/steam-input.md, and
docs/ui.md before changing behavior.

- Keep stable page IDs and route semantics. Back, close, and repeated-open behavior must be
  deterministic across keyboard, controller, and touch input.
- Device includes Core Windows controls even with Device Integration off. Windows power profiles
  belong on Device > Power, not in WSGM Settings, which configures WSGM itself only.
- Use the SDK's shared Power, RGB, Controller and Info IDs for host and plugin controls.
  Power stays reachable without integration. The session owns AC/battery preset assignments;
  overlay controls report intent and never run a second automatic profile loop.
- Quick Access pin IDs persist in AppConfig. Device rows use capability keys, and plugin sections
  use the DevicePluginSection route plus a section ID rather than new enum values.
- OverlayController owns lifetime and integration sequencing. Views and controls render state and
  report intent; they do not mutate ConfigStore or acquire leases directly.
- Capture, focus, cursor, and input-lease transitions are paired operations. Every close,
  cancellation, failure, and superseded open must release what it acquired.
- Preserve the 150 ms deferred close and synthesized-mouse filtering after touch input so a gesture
  cannot activate a control behind the sheet.
- The sheet leaves a game strip visible as its tap-outside dismissal target. Do not make it
  fullscreen without another dismissal path.
- Only one GamepadNavigation instance may be enabled at a time, with one action per edge or repeat
  decision. A status panel or keyboard takes ownership while it has focus.
- Async searches, artwork, Steam state, and telemetry updates carry a generation or cancellation
  token so stale results cannot replace the current page.
- UI-observable collections and properties change on the Avalonia dispatcher. High-rate telemetry is
  sampled or coalesced before reaching controls.
- Raw-touch left and right gestures send Steam's Big Picture shortcuts even while a game is
  foreground. Top and bottom gestures remain WSGM-owned.
- Use shared theme and focus tokens. Overlay-specific layout may be compact, but it must not fork
  the application's control language.

Test open/close idempotence, route transitions, stale async results, touch/mouse deduplication,
capture release, and integration-disabled behavior.
