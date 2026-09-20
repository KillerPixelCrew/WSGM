# Overlay

Overlay owns the game-facing sheet, its navigation model, capture and input handoff, Steam presentation, and overlay
view models. Read docs/overlay-and-input.md, docs/steam-input.md, and docs/ui.md before changing behavior.
Read docs/overlay-surfaces.md for utility, keyboard, credential or power-menu lifetime changes.

- Keep stable page IDs and route semantics. Back, close, and repeated-open behavior must be deterministic across
  keyboard, controller, and touch input.
- Device includes Core Windows controls even with Device Integration off. Windows power profiles belong on the Device
  overview and Device > Power, not in WSGM Settings, which configures WSGM itself only.
- Use the SDK's shared Power, RGB, Controller and Info IDs for host and plugin controls. Power stays reachable without
  integration. The session owns AC/battery preset assignments; overlay controls report intent and never run a second
  automatic profile loop.
- Quick Access pin IDs persist in AppConfig. Device groups use stable section/category keys. Plugin pages use the
  DevicePluginSection route plus a section ID rather than new enum values.
- OverlayController owns lifetime and integration sequencing. Views and controls render state and report intent; they do
  not mutate ConfigStore or acquire leases directly.
- Capture, focus, cursor, and input-lease transitions are paired operations. Every close, cancellation, failure, and
  superseded open must release what it acquired.
- Open apps activation waits for deferred closure and input-lease release. Preserve the selected HWND and PID through
  Steam activation; Steam may raise a launcher console. Cancel pending returns when another action takes over, the sheet
  reopens or the session shuts down.
- Preserve the 150 ms deferred close and synthesized-mouse filtering after touch input so a gesture cannot activate a
  control behind the sheet.
- Keep the fullscreen sheet's fixed Close control reachable at the 980 × 640 DIP workspace floor. Native blur uses
  Avalonia transparency with an opaque fallback; it must not depend on captured desktop frames.
- Every destination uses the persistent section rail and controls pane. Remember section selection across destination
  switches and window reopen. Selection remains visible independently of focus; telemetry preserves rail instances.
- One GamepadNavigation owns the overlay and its in-window surfaces, with one action per edge or repeat decision.
  LT/RT and LB/RB switch destinations; a utility or keyboard confines focus and input until dismissed. Back from primary
  controls focuses the selected section, Back from its rail returns home, and nested editors pop one level.
- Async searches, artwork, Steam state, and telemetry updates carry a generation or cancellation token so stale results
  cannot replace the current page.
- UI-observable collections and properties change on the Avalonia dispatcher. High-rate telemetry is sampled or
  coalesced before reaching controls.
- Raw-touch left and right gestures send Steam's Big Picture shortcuts even while a game is foreground. Top and bottom
  gestures remain WSGM-owned. Keep bezel entry, early movement and directional dominance distinct; threshold changes
  need synthetic rejection traces and attended calibration evidence, not just a recognizer test pass.
- Use shared theme and focus tokens. Overlay-specific layout may be compact, but it must not fork the application's
  control language.

Test open/close idempotence, route transitions, stale async results, touch/mouse deduplication, capture release, and
integration-disabled behavior.

Device sections use FlexPanel groups placed by measured column height. ActionButton represents commands; value rows
use actual editors in FluentAvalonia footers and read-only values use compact statistics. Keep assignments and performance
together, preserve rows and drafts during value refresh, and rebuild when the descriptor/layout identity changes.
Windows energy plans remain reachable with integration disabled. Pin grouped sections with all their controls through
one heading action; nested editors resolve their group rather than a separate value pin. Keep source and front-page
renderers aligned. Use the shared bordered groups, readable section headings and consistent group gaps in the controls
pane; pinned groups retain their natural height. Inspect complete Claw publication captures when changing Device layout; the one-row fake is not
sufficient coverage. Test execution follows the root manual-first timing.
