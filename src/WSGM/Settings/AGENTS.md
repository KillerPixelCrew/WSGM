# Settings

Settings owns persistent user policy, setup flows, configuration-facing view models, and its bounded input claim while
focused. Device-specific implementation and session-long capability ownership remain elsewhere.

WSGM Settings is strictly for configuring WSGM itself. Do not add controls here for Windows or external system settings.
Those belong on the overlay's relevant page or in Steam QAM; Windows power-scheme selection belongs on Device and QAM
Performance. Configuring how WSGM manages a feature is a WSGM setting; manually changing the external system's current
state is not.

- Keep the established tab structure and the 1024 by 640 minimum window size. Pages may scroll when their content
  requires it; do not hide controls to satisfy an arbitrary no-scroll rule.
- A save operation reads fresh configuration, applies the page's owned fields, validates, and commits through
  ConfigStore. Do not overwrite fields owned by another page or hold the config lock while doing external work.
- WSGM's page in Steam writes some of the same fields while this window may be open. Those are listed in
  `SettingsViewModel.SharedFields`; a save writes one only when the user changed it here, measured against what the
  window last loaded or saved, and keeps the saved value otherwise. A field that page gains goes on that list.
- Write dependent sidecars or manifests atomically and keep them consistent with the committed configuration.
- Display edits use stable display identities and must handle a disconnected or stale target explicitly.
- First-run choices are asked by setup, not here. Setup exports and applies them through `Core/SetupAnswers`
  (`--export-setup-answers`, `--setup --answers`); a field setup asks is added there as well as on its page.
- The Plugins page (`PluginSettingsPage`) installs a bundled package by copying it into the Plugins folder and removes
  one by deleting it, or at the next start while it is loaded. It never loads plugin code and never installs drivers:
  missing components go through setup's repair.
- The UPDATES section shows what the daily check recorded, read through the injected services. Applying an update is
  the user's explicit, confirmed action, because setup closes Steam; never start it from a check.
- Steam Input reconciliation happens after configuration is saved and outside the config lock, with the existing
  elevation and pending-update behavior.
- Device and Plugin tabs remain available when integration is disabled so users can enable it and manage target, glyph,
  package, and offline profile policy. Only live controller-management and AutoTDP controls become unavailable. A view
  model must not probe hardware simply to decide how to render.
- Input-lease and on-screen-keyboard handoffs are paired and released on close, cancellation, failure, or disposal.
- Every focused Settings window uses a named Steam Input lease, including standalone Desktop Settings and shortcut
  capture. Native acquire/release stays on workers; a handoff claim must not wait for the native-operation lock. A
  game-mode window registers its claim before acquisition and releases the claim even if native acquisition failed.
  During overlay handoff, claim before the overlay's deferred release and acknowledge close before ending the temporary
  deactivation exemption.
- Desktop Settings launches reuse the resident session's Settings window when it is available, so Settings and the
  overlay acquire through the same process owner and integrity level. Only a launch with no resident receiver creates a
  standalone Settings runtime.
- Required text credentials need a controller-accessible OnScreenKeyboard path; gamepad navigation deliberately skips
  ordinary TextBox controls.
- The production parameterless SettingsViewModel intentionally loads the real ConfigStore and installed-package state.
  Tests and injected constructors use explicit stores, paths, and services and never fall back to the real profile.

Add focused view-model and persistence tests for every changed page, including stale state, partial failure, repeated
save, and integration-disabled cases.
