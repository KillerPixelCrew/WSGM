# Controls

Controls are reusable, presentation-focused Avalonia components. Business policy, persistence,
device access, and long-lived orchestration belong in the owning feature.

- Read docs/ui.md and docs/overlay-and-input.md before changing visual or interaction conventions.
- Consume shared theme tokens instead of embedding page-specific colors, typography, radii, or focus
  styles.
- Icons are stroke-style StreamGeometry. Render them with Fill set to null or interior detail
  collapses. With Uniform stretch, size a Path by its dominant dimension because Avalonia aligns the
  scaled geometry at the top left of an oversized box.
- Preserve keyboard, controller, touch, and screen-reader behavior. A custom control must expose
  stable focus and automation semantics.
- Curve editors and controller widgets report user intent; the owner validates and persists it.
  On-screen-keyboard controls do not acquire global hooks or leases themselves.
- An on-screen-keyboard layer rebuild restores focus to the modifier that initiated it; otherwise
  controller navigation loses its place.
- StyledProperty, DirectProperty, and event surfaces are the public contract. Avoid hidden service
  lookup or application-singleton dependencies.

Add focused control or view-model tests where behavior is separable, and exercise the owning
settings or overlay flow for integration changes.
