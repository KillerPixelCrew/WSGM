# Settings

Settings owns persistent user policy, setup flows, configuration-facing view models, and its bounded
input claim while focused. Device-specific implementation and session-long capability ownership
remain elsewhere.

- Keep the established tab structure and the 1024 by 640 minimum window size. Pages may scroll when
  their content requires it; do not hide controls to satisfy an arbitrary no-scroll rule.
- A save operation reads fresh configuration, applies the page's owned fields, validates, and
  commits through ConfigStore. Do not overwrite fields owned by another page or hold the config lock
  while doing external work.
- Write dependent sidecars or manifests atomically and keep them consistent with the committed
  configuration.
- Display edits use stable display identities and must handle a disconnected or stale target
  explicitly.
- Quick Setup uses an integer revision, disables the settings pages while modal, and applies nothing
  until Continue. Stamp the answered revision only in the successful save, so Skip means off and a
  failed save asks again.
- Steam Input reconciliation happens after configuration is saved and outside the config lock, with
  the existing elevation and pending-update behavior.
- Device and Plugin tabs remain available when integration is disabled so users can enable it and
  manage target, glyph, package, and offline profile policy. Only live controller-management and
  AutoTDP controls become unavailable. A view model must not probe hardware simply to decide how to
  render.
- Input-lease and on-screen-keyboard handoffs are paired and released on close, cancellation,
  failure, or disposal.
- A game-mode window registers its named Steam Input claim before acquisition and releases the claim
  even if native acquisition failed. During overlay handoff, claim before the overlay's deferred
  release and acknowledge close before ending the temporary deactivation exemption.
- Required text credentials need a controller-accessible OnScreenKeyboard path; gamepad navigation
  deliberately skips ordinary TextBox controls.
- The production parameterless SettingsViewModel intentionally loads the real ConfigStore and
  installed-package state. Tests and injected constructors use explicit stores, paths, and services
  and never fall back to the real profile.

Add focused view-model and persistence tests for every changed page, including stale state, partial
failure, repeated save, and integration-disabled cases.
