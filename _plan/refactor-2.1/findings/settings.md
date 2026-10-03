# Settings UI, themes and LiveBackdrop findings

Scope: `src/WSGM/Settings/**`, `src/WSGM/Themes/**`, `src/Avalonia.LiveBackdrop/**`, their tests
(`tests/WSGM.Tests/Settings`, `tests/WSGM.Tests/Themes`, `tests/WSGM.UiTests/Settings`, the Settings part of
`tests/WSGM.UiTests/Infrastructure/UiFixture.cs`, `tests/Avalonia.LiveBackdrop.Tests`) and the Shell, Overlay and Core
call sites they touch. Baseline `master` 1329813f. Sources: `_plan/refactor-2.1/review/settings.md`, its adversarial verification
`settings.verify.md` (corrections applied below, and the eight missed findings SETTINGS-V-001 to V-008 added as full
findings), the completeness critic and `refactor-plan-v2.md`, which wins wherever it changed a recommendation.

Line numbers in the review were often wrong (the verifier lists them). Anchor every edit by symbol; the lines here
were re-read at 1329813f but still drift as batches land.

Counts of the 45 findings written out below: 1 high, 10 medium, 23 low, 11 nit. Five more ids are no-change (list at
the end; the solution checker moved SETTINGS-037 there). The review's plan-claim checks C1 to C23 are not findings;
the verifier refuted only C19 (the 13 `SettingsViewModel*.cs` files are 3,750 lines, not 3,349).

Solution-checker notes (re-read at 1329813f): the solutions below deviate from plan v2 batch specs in four places,
each for a stated reason: SETTINGS-008 keeps the takeover buttons' own flow over the same service members instead of
routing them through `SettingsExternalApply` (B117), SETTINGS-016 puts the test-sheet factory on
`SettingsWindowServices` (B117 named `SettingsServices`), SETTINGS-018 adds no window-lifetime token (B121), and
SETTINGS-037 is no-change (B135).

Maintainer decisions (`DECISIONS.md`, 2026-10-03) are applied and win over plan v2 and the earlier solutions:
SETTINGS-022 drops the UnsupportedSchema refusal (a config from a newer WSGM loads best effort as today, no read-only
mode), SETTINGS-035 refuses instead of truncating (D2), SETTINGS-019 removes the Modern Standby cap as decided,
SETTINGS-023 keeps the existing binding, and SETTINGS-V-005 keeps only its functional part (the dropped security
hardening, B029 included, is not a dependency). No finding moved to the no-change list; the counts are unchanged.

Plan v2 batches for this area: B013 (save merge and shared-field table, Phase A live defect), B117 (required
services), B118 (display discovery on the worker), B119 (close during save, truthful status), B120 (type moves and
the default accent), B121 (updates and packages through services), B123 (one Settings window per process), B133
(instance config store, load outcome), B134 (LiveBackdrop seam), B135 (tests and nits), plus B038 (config caps, for
V-007) and B177 (guidance, for 042). Related outside this area: B016 (CRIT-001 Steam autostart record), B039 (config
read outcomes), B075 (managed pad into Settings, INPUT-009), B076 (Steam Input shim instance), B095 (other-manager
takeover instance).

Cross-domain rules from the critic that shape these solutions: SETTINGS-B1 and B2 land merged and first (conflict
23), config B4 later adds `ProfileEdits` inside the new merge rather than beside it; config B2a (B037) lands before
the structural Settings batches so constructors are rewritten once (conflict 26); `SteamAutostartService` and
`OtherManagers` belong to WINSVC-B3/B095 including the "apply only on false-to-true" rule (conflict 27), so Settings
only decides when to call them.

### SETTINGS-001: A Settings save reverts Steam-side theme, sound and animation choices, including recovery state

- **Severity:** high (verifier confirmed and widened the scope).
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`CaptureSaveRequest`, `ApplyCapturedValues`);
  `src/WSGM/Core/Animations/AnimationsConfig.cs` (`SteamSetAside`); writers in `src/WSGM/Shell/ShellSession.cs`
  (theme, sound and animation services), `Shell/ThemeService.cs`, `Shell/AnimationService.cs`,
  `Shell/SoundPackService.cs`.
- **Problem:** `CaptureSaveRequest` clones the window-open `_config` and `ApplyCapturedValues` restores only a
  hand-listed set of runtime fields from the fresh load. `Themes`, `Sounds` and `Animations` are not on the list, so
  any Settings save writes back the values captured when the window opened. Open Settings, change the theme switch,
  hidden themes, translations branch, sound pack or boot movie in Steam, then press Save in Settings: the stale values
  are written and the config reload re-applies them. Worse, `AnimationsConfig.SteamSetAside` is recovery state
  (Steam's own startup-movie choice that WSGM sets aside while its movie plays). A stale write (often null) loses the
  user's Steam movie choice for good, which breaks the never-strand rule, not only a preference. (Themes are not
  uninstalled: per-theme enable state lives in the theme files; `ThemesConfig` holds `Enabled`, `TranslationsBranch`
  and `HiddenThemes`.)
- **Best solution:** fixed by SETTINGS-002's allowlist merge: the merge starts from the fresh strict load, so every
  section Settings does not own (Themes, Sounds, Animations with `SteamSetAside`, and anything added later) keeps the
  saved value with no list to maintain. No Themes/Sounds/Animations code is added to Settings.
- **Tests:** in `tests/WSGM.Tests/Settings/SettingsSaveMergeTests.cs`, the unedited-save test from SETTINGS-002 with
  a disk config whose `Themes`, `Sounds`, `Animations` and `Animations.SteamSetAside` differ from what the window
  loaded; assert each survives byte-for-byte. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~WsgmSteamSettings"`.
  Manual: M01-03 extended (theme, sound and animation changed in Steam while Settings is open, then an unrelated save).
- **Plan v2:** B013.
- **Related:** SETTINGS-002 (root cause), SETTINGS-026, CONFIG-022; plan v2 section 1 "Corrected facts"; D10 (WSGM
  hands Steam's startup-movie choice back from `SteamSetAside` at exit and uninstall, so a stale save must not lose it).

### SETTINGS-002: The save merge is a denylist, so every unlisted field defaults to "write the stale snapshot"

- **Severity:** medium.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`ApplyCapturedValues`, the 40-line restore list from
  `config.PluginConfigurations = fresh.PluginConfigurations` to `PreviousConsoleLockPolicyDc`, and
  `SettingsSaveMerge` call sites in `tests/WSGM.UiTests/Infrastructure/UiFixture.cs`).
- **Problem:** the merge starts from the window's snapshot (`request.Values`) and copies back from `fresh` only the
  fields someone remembered to list. Every new section, extension data or runtime/recovery field silently regresses to
  the window-open value on the next Settings save (SETTINGS-001 is the first instance; F01's `SchemaVersion` would be
  the next).
- **Best solution:** new `src/WSGM/Settings/SettingsSaveMerge.cs`, `internal static AppConfig Apply(AppConfig fresh,
  SettingsViewModel.SaveRequest request, SplashConfig preparedSplash)`, replacing `ApplyCapturedValues`:
  1. Start from `fresh` and mutate it (it is the strict load inside the writer lock).
  2. Copy only Settings-owned fields from `request.Values`, the same set `ApplyTo` writes: the top-level scalars
     (`SteamAutoRelaunch`, `SteamLaunchUnelevated`, `StartupDelayMs`, `StaggerDelayMs`, `BootSplashEnabled`,
     `SteamAutostartTakeoverAccepted`, `OtherManagersTakeoverAccepted`, `MuteWhileDisplayOff`, `CheckForUpdates`,
     `ResuspendUnexplainedWakes`, `LogVerbosity`, `AutoTdpTraceEnabled`, `Hotkey`, `GamepadChord`, `GlyphStyle`,
     `AccentColor`, `OverlayBlurRadius`, `StartupApps`), the Settings fields of `Performance` and `Gestures`, the
     artwork and Game Library fields exactly as today, `DeviceIntegration.ControllerManagementEnabled` and
     `KeepGuideChordEdits`, `GameModeLaunch` (then the existing known-display merge, reading discovered displays from
     the `fresh.GameModeLaunch` captured before the overwrite), and `Splash = preparedSplash`.
  3. Shared fields only when edited here (SETTINGS-004/V-002 table); common-plugin, plugin-setting and device-profile
     edits through the single path of SETTINGS-V-001.
  4. Delete the runtime restore list and `ApplyCapturedValues`; `UiFixture` and `PersistSave` call
     `SettingsSaveMerge.Apply`.
  `ApplyTo` (view model to snapshot) and the owned-field copy (snapshot to fresh) are two lists by necessity: the view
  model is read on the UI thread and fresh is loaded on the worker. The round-trip test below is what keeps them in
  step. Do not add the review's top-level classification guard test (plan v2 dropped it): with an allowlist an
  unclassified field is safe by default, and the test ignores partly owned sections.
- **Tests:** in `SettingsSaveMergeTests`: (1) an unedited save is a no-op against the current disk: a view model loaded
  from config X, disk now Y (X with Themes, Sounds, Animations including `SteamSetAside`, every shared-table field,
  plugin instances, library and card fields and recovery fields changed), capture plus `SettingsSaveMerge.Apply` with
  no edits, assert the result serializes identically to Y. (2) Owned round trip: set every Settings-bound property to a
  non-default (the setter list lives in the test helper), capture plus merge onto a default fresh, build a second view
  model from the result and compare every public read-write property of the two view models by reflection. Same
  filters as SETTINGS-001 plus `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings"`.
- **Plan v2:** B013.
- **Related:** SETTINGS-001, SETTINGS-V-001, SETTINGS-V-002; critic conflict 23; C6, C8.

### SETTINGS-003: An unrelated Settings save turns Device Integration back off after the overlay banner enabled it

- **Severity:** medium.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`ApplyTo` writes `DeviceIntegration.Enabled`;
  `ApplyCapturedValues` re-applies `editedDevice.Enabled` unconditionally); writer
  `src/WSGM/Shell/ShellSession.Actions.cs` (`EnableDeviceIntegrationAsync`, overlay prerequisites banner).
- **Problem:** `DeviceIntegration.Enabled` is written on every save from the window-open value. Enabling integration
  from the overlay banner while Settings is open, then saving anything in Settings, disables it again.
- **Best solution:** make `DeviceIntegration.Enabled` one entry of the shared-field table (SETTINGS-V-002), so it is
  written only when changed in this window. Do not add a fourth bespoke edited/saved flag pair, which the review
  proposed and the verifier and plan v2 rejected.
- **Tests:** merge test: disk has `Enabled = true`, the window loaded `false` and did not touch it, save, assert
  `true`; and the converse (edited here to `false` is written). Filter as SETTINGS-001.
- **Plan v2:** B013. Manual M01-03 extension (Device Integration enabled from the banner while Settings is open).
- **Related:** SETTINGS-V-002.

### SETTINGS-004: The Settings shared-field list and the Steam page's toggles are two hand-kept copies

- **Severity:** medium.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`SharedFields`, `SharedField` record, `_sharedBaseline`,
  `RecordSharedBaseline`); `src/WSGM/Shell/WsgmSteamSettingsService.cs` (`Toggles`, `StartModeKey` handling in
  `SetAsync`); guidance `src/WSGM/Settings/AGENTS.md` (shared-field paragraph).
- **Problem:** each field both surfaces write has its read/write lambdas twice, and the only thing keeping them aligned
  is a guidance sentence. A toggle added to the Steam page but not to `SharedFields` is reverted by the next Settings
  save.
- **Best solution:** new `src/WSGM/Core/WsgmSharedSettings.cs`: a generic `WsgmSharedSetting<T>(string Name,
  Func<AppConfig, T> Read, Action<AppConfig, T> Write)` with two non-generic members the merge uses (`object
  ReadBoxed(AppConfig)` and `Copy(AppConfig to, AppConfig from)`), static entries for every field another surface
  writes while Settings is open, and `All`. Entries: the ten `Cef.*` toggles, `SteamStorageFormatEnabled`,
  `StartAtSignIn`, `StartMode`, `SteamInputLeaseEnabled`, `SteamInputManagementEnabled`, plus the device fields of
  SETTINGS-V-002. `WsgmSteamSettingsService.SettingToggle` takes a `WsgmSharedSetting<bool>` instead of its own two
  lambdas (labels, descriptions, confirmations and the `Boot`/`SteamInput` flags stay where they are), and the
  `StartMode` branch writes through `WsgmSharedSettings.StartMode.Write`. Settings drops `SharedFields`/`SharedField`
  and iterates `WsgmSharedSettings.All` for the baseline, `SharedEdits` and the merge.
- **Tests:** existing shared-field tests retargeted to the table; one test that every Steam toggle's field is in
  `WsgmSharedSettings.All`. Filter as SETTINGS-001 (it includes `WsgmSteamSettings`).
- **Plan v2:** B013 (SETTINGS-B1 and B2 merged). The guidance update is SETTINGS-042.
- **Related:** SETTINGS-V-002, SETTINGS-042; S01 owns `WsgmSteamSettingsService` but this batch edits it under the
  one-writer rule.

### SETTINGS-005: Injected view-model constructors fall back to production services

- **Severity:** medium (verifier: real but narrower than stated; latent).
- **Where:** `src/WSGM/Settings/SettingsViewModel.cs` (`_services = services ?? SettingsServices.Windows()`, the
  `SettingsViewModel(AppConfig)` and `(AppConfig, string?)` overloads, the parameterless constructor);
  `SettingsViewModel.Updates.cs` (`LoadUpdateState`); tests in `tests/WSGM.Tests/Settings/*` and
  `tests/WSGM.Tests/Overlay/QuickAccessSheetTests.cs`.
- **Problem:** about twenty unit tests use the convenience constructors and therefore read the real machine:
  `ModernStandbyDiagnostics.Read` (armed wake devices), `KnownStartupApps.Detected` (Program Files probes) and
  `UpdateChecker.ReadState()` (the developer's `update.json`). `Persist` is wired to the real `ConfigStore`; no test
  calls `SaveCommand` today, so nothing writes `config.json` yet, but the hazard is one test away. It contradicts
  `Settings/AGENTS.md` ("never fall back to the real profile").
- **Best solution:** move the record to `src/WSGM/Settings/SettingsServices.cs` and make the constructor take a
  non-null `SettingsServices`. Delete `SettingsViewModel()`, `SettingsViewModel(AppConfig)` and
  `SettingsViewModel(AppConfig, string?)`. Production uses `SettingsViewModel.FromLoadedConfig(AppConfig config,
  SettingsServices services)` (App.axaml.cs, and the window entry points until SETTINGS-011 replaces them).
  `SettingsServices.Windows(...)` builds the production set. Tests use one helper,
  `tests/WSGM.Tests/Builders/SettingsTestServices.Inert(AppConfig saved)`, whose members are all inert (empty display
  arrangement, null facts, no plugin actions, no startup apps, no-op import session, `Persist` merging onto an
  in-memory `saved` through `SettingsSaveMerge.Apply`, no-op reconcile, recorded `Report`, a fixed standby report,
  empty autostart scan and complete apply, `AudioDiscovery.Empty`, `new UpdateState()`, no managers, `LoadPersisted`
  returning a clone of `saved`, inert update and package members). Overrides use record `with` expressions. WSGM.UiTests
  does not reference the WSGM.Tests project; link the helper into `tests/WSGM.UiTests/WSGM.UiTests.csproj` with a
  `<Compile Include="../WSGM.Tests/Builders/SettingsTestServices.cs" Link="Builders/SettingsTestServices.cs"/>` item,
  as the csproj already does for `Fakes/FakeButtonSource.cs`, so the helper must use no xunit API. UiFixture then
  builds its services from `SettingsTestServices.Inert(Saved)` with its recording overrides.
- **Tests:** every Settings test constructs through `SettingsTestServices`; add a test that `SaveCommand` on an inert
  model never touches the real store (it calls the in-memory `Persist`). Filters
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~QuickAccessSheet"`
  and the UiTests Settings filter.
- **Plan v2:** B117.
- **Related:** U04A-LFA-004 (path seam), SETTINGS-006, SETTINGS-042.

### SETTINGS-007: Production display discovery timing is inferred from whether a test injected services

- **Severity:** medium.
- **Where:** `src/WSGM/Settings/SettingsViewModel.cs` (`_queryDisplaysOnWorker = services is null`);
  `SettingsViewModel.Displays.cs` (`StartDisplayDiscovery`, `RefreshDisplaysAsync`:
  `_queryDisplaysOnWorker ? await Task.Run(ReadDisplayCatalog) : ReadDisplayCatalog()`);
  `SettingsViewModel.Launch.cs` (`LoadLaunchConfiguration`, the synchronous `CaptureDisplays` branch);
  `src/WSGM/Settings/SettingsWindow.axaml.cs` (`Opened`).
- **Problem:** a test-only branch in production code: the injected path captures displays synchronously in the
  constructor, the production path on a worker after `Opened`. Tests never exercise the production timing, and once
  SETTINGS-005 makes services mandatory the flag has no meaning.
- **Best solution:** delete `_queryDisplaysOnWorker` and the synchronous branch in `LoadLaunchConfiguration`. Rename
  `StartDisplayDiscovery` to `internal Task StartDisplayDiscoveryAsync()` returning `RefreshDisplaysAsync()`, which
  always reads on `Task.Run`. The window's `Opened` handler observes it with `Log.Observe`. Discovery starts on
  `Opened`, which fires inside `Show`, so `UiFixture.Settings` cannot show the window "after" discovery: after
  `Show(window)` it pumps `Dispatcher.UIThread.RunJobs()` until the existing `ReadingDisplays` property is false
  (bounded by `AsyncConditions.TimeLimit`), then returns. Do not have the fixture call `StartDisplayDiscoveryAsync`
  itself: `ReadDisplaysAsync` returns at once while a read is running, so a second call proves nothing. View-model
  unit tests that need rows await `StartDisplayDiscoveryAsync()` directly (no window, so no `Opened`). No new member.
- **Tests:** UI filter
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~GameModeDisplayPageTests|FullyQualifiedName~WSGM.UiTests.Settings"`
  and the WSGM.Tests Settings filter. Production rendering does not change. Test baselines can change only where a
  fixture now gets production's first-render state, which the synchronous test branch skipped: `ReadDisplaysAsync`
  merges the display facts and seeds an empty game or desktop layout from the observed arrangement
  (`SeedDisplayLayout`). Review every changed image against that rule; any other diff is a defect.
- **Plan v2:** B118 (split out of B3 because `GameModeDisplayPageTests` depends on first-render rows; verify batch
  problem 6).
- **Related:** SETTINGS-005, SETTINGS-038.

### SETTINGS-008: Post-save Steam autostart and other-manager takeover bypass the injected services

- **Severity:** medium.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`ApplySteamInputManagementAfterSave`,
  `ApplySteamAutostartAfterSave`, `ApplyOtherManagersAfterSave`); `SettingsViewModel.System.cs`
  (`TakeOverSteamAutostartAsync`, `TakeOverOtherManagersAsync`).
- **Problem:** the same policy exists twice: the post-save step calls the statics `SteamAutostartService.Scan/Apply`
  and `OtherManagers.Detect/Apply`, while the "Check and take over" buttons use `SettingsServices`. Only the button
  path is testable, and the two can drift.
- **Best solution:** the defect is that the post-save step calls the statics instead of the injected members the
  buttons already use. New `src/WSGM/Settings/SettingsExternalApply.cs` (internal static, over `SettingsServices`)
  holds the one post-save sequence: `AfterSaveAsync(services, saved, changes)` runs on a worker the shim reconcile
  (`services.ReconcileSteamInputShim`, which replaces the whole-routine `ApplySteamInput` member and wraps
  `SteamInputManagement.Apply(config, "settings-save")`, later through the B076 shim instance), then the autostart
  takeover (`services.ScanSteamAutostart`, keep `Enabled` sources, `services.ApplySteamAutostart` when any) and the
  other-managers takeover (`services.DetectOtherManagers`, `services.ApplyOtherManagers` when any), each step gated by
  SETTINGS-V-004 and each in its own `try` with today's `Log.Warn` texts, so a throwing step does not skip the later
  ones. Delete `ApplySteamInputManagementAfterSave`, `ApplySteamAutostartAfterSave` and `ApplyOtherManagersAfterSave`.
  The two buttons keep their own flow (scan first, saved-policy check, apply, rich status text), which already goes
  through the same service members once SETTINGS-006 removes the fallbacks; do not restructure them around a shared
  takeover method, which would change when their status lines appear.
- **Tests:** fakes in `SettingsTestServices`: with all three `SaveChanges` flags set, post-save calls reconcile, scan
  and apply, detect and apply in that order; a throwing reconcile still runs the two takeovers; post-save never
  reaches a static (the inert members record every call). WSGM.Tests Settings filter.
- **Plan v2:** B117.
- **Related:** SETTINGS-V-004, SETTINGS-021, CRIT-001 (B016 makes the autostart record refuse; Settings only calls
  it), WINSVC-014 (B095, `OtherManagerTakeover` instance; Settings takes it through the service delegate), critic
  conflict 27.

### SETTINGS-010: A process-wide mutable static routes the plugin action source into Settings

- **Severity:** medium.
- **Where:** `src/WSGM/Settings/SettingsPluginActions.cs` (`_source`, `Publish`, `Withdraw`, `Read`);
  `src/WSGM/Shell/ShellSession.cs` (`Publish`); `src/WSGM/Shell/ShellSession.Shutdown.cs` (`Withdraw`);
  `SettingsServices.Windows()` (`SettingsPluginActions.Read`).
- **Problem:** hidden global state set at session start and cleared at shutdown; any Settings window in the process
  reads whatever was published last.
- **Best solution:** delete `SettingsPluginActions`. The session passes its `ReadPluginActionOptions` delegate to the
  Settings surface (SETTINGS-011), which puts it in `SettingsServices.ReadPluginActions`. Standalone `--settings`
  passes `() => []`, which renders saved steps read-only as today. The session's shutdown closes the window, so no
  withdraw step is needed.
- **Tests:** standalone composition has an empty action source; a session composition returns the session's options.
  Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"` and the
  UiTests Settings and Overlay filters.
- **Plan v2:** B123.
- **Related:** U05-LFB-005 / PV11-005 (same finding), SETTINGS-011, SETTINGS-015.

### SETTINGS-011: The resident process can show two Settings windows and release the overlay's claim under an open one

- **Severity:** medium (verifier confirmed with a concrete defect).
- **Where:** `src/WSGM/Shell/DesktopTray.cs` (`OpenSettings`, `_settings`); `src/WSGM/Overlay/OverlayController.cs`
  (`SettingsRequested` handler, `new SettingsWindow(true)`, `ClaimUiSurface(SettingsSurface)`);
  `src/WSGM/Overlay/OverlayController.Lease.cs` (`_uiSurfaces` set); `src/WSGM/Shell/ShellSession.cs`
  (`SettingsActivation` callback calls `_desktopTray?.OpenSettings()`).
- **Problem:** the tray keeps one instance, the overlay creates a new window per request. Tray plus overlay, two
  overlay requests, or Test sheet then Settings inside Settings give two windows with independent drafts, so the later
  save discards the other's edits. The overlay's UI-surface claim is set-based: closing the first window releases the
  "settings" claim while the second is open, and managed controller capture is handed back under an open Settings
  window. Separately, a Start-menu launch always goes through `DesktopTray.OpenSettings` with `gameModeSurface: false`,
  so in game mode it is never registered with `WindowFinder.IncludeOwnWindow` and is unreachable from the Open apps
  strip once it drops behind Big Picture.
- **Best solution:** new `src/WSGM/Shell/SettingsSurface.cs`, built by `ShellSession`, the only creator of Settings
  windows in the resident process. `Open()`: if a window exists, restore and `Activate()` it; otherwise compose the
  view model and window with the session's services (SETTINGS-010, 016) and show it. The surface mode comes from the
  session's current mode, not the caller: when the session is in game mode, the window is included as switchable
  (expose an idempotent `SettingsWindow.IncludeAsSwitchable()` replacing the constructor flag), also for an existing
  window reopened after the session entered game mode. The overlay's "settings" UI-surface claim keeps today's rule:
  only an overlay request takes it (tray and Start-menu opens never did, and claiming there would change
  managed-controller capture in desktop mode). The overlay's handler calls `Open()`, then
  `ClaimUiSurface(SettingsSurface)`, and subscribes `ReleaseUiSurface(SettingsSurface)` to that window's `Closed` only
  when the claim was newly added (the `_uiSurfaces.Add` result), so a second overlay request for the same window
  neither double-claims nor double-releases and the claim lasts exactly as long as the one window. Tray, overlay
  `SettingsRequested` and `SettingsActivation` call `Open()`; the overlay keeps its `CloseOverlay` and
  `_suppressFocusRestore` steps. The preview sheet (`previewOnly`) hides or ignores
  the Settings row, like its mode switch. Delete `DesktopTray._settings`, the overlay's window construction and the
  parameterless `SettingsWindow(bool)` constructor. Standalone `--settings` in `App.axaml.cs` keeps building its one
  window directly.
- **Tests:** a second `Open()` activates the same window; an overlay open of an existing desktop window in game mode
  includes it as switchable; two overlay requests for one window give one claim and one release, and a tray open
  claims nothing; standalone has an empty action source. Filters
  as SETTINGS-010.
- **Plan v2:** B123 (after the overlay ports in B122; serialized with the H02 ShellSession work).
- **Related:** OVERLAY-V-002 (same defect seen from the overlay, resolved here), SETTINGS-010, SETTINGS-015,
  SETTINGS-016, SETTINGS-V-008, INPUT-009 (B075).

### SETTINGS-V-001: Two plugin-setting and device-profile merges; the tested one is dead

- **Severity:** medium (missed by the review, found by the verifier).
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`ApplyTo` calls `ApplyPluginSettingsTo` and
  `ApplyDeviceProfilesTo`; `ApplyCapturedValues` then sets `config.DeviceIntegration = fresh.DeviceIntegration` and
  re-merges through `FindOrAddSaveScope` plus an inline copy of `PluginSettingsResolver.Store`);
  `SettingsViewModel.Plugins.cs` (`ApplyDeviceProfilesTo`, `ApplyPluginSettingsTo`, `FindOrAddScope`, their remarks);
  `src/WSGM/Core/PluginSettingsResolver.cs` (`Store`); tests
  `tests/WSGM.Tests/Settings/PluginSettingsViewModelTests.cs` (`AnEditReachesTheConfigurationTheSaveActuallyWrites`,
  `AnUntouchedSettingIsLeftExactlyAsAnotherProcessWroteIt`) and `DeviceProfileAuthoringTests.cs`
  (`AnEditedProfileListIsWrittenAtSave`, `AnUntouchedProfileListIsLeftAsAnotherProcessWroteIt`).
- **Problem:** the first merge writes onto the snapshot and is thrown away by the second. Only the second runs
  `ProfileEdits.RemoveFanCurveReferences`. The four tests and the `Plugins.cs` remarks describe the dead path, so they
  give false assurance about what the save writes.
- **Best solution:** one path inside `SettingsSaveMerge.Apply`: find or add the scope (one private helper, replacing
  both `FindOrAddSaveScope` and `FindOrAddScope`), call `PluginSettingsResolver.Store` for each edit, and for an edited
  profile list run `ProfileEdits.RemoveFanCurveReferences` for removed ids and replace `scope.Profiles`. Delete
  `ApplyPluginSettingsTo`, `ApplyDeviceProfilesTo`, `FindOrAddScope`, `FindOrAddSaveScope`, the inline copy of
  `Store` and the two calls in `ApplyTo`; correct the remarks.
- **Tests:** retarget the four tests to `CaptureSaveRequest` plus `SettingsSaveMerge.Apply` (the shape of the
  existing `SettingsSaveMergeTests`); keep their names and assertions. Filter as SETTINGS-001.
- **Plan v2:** B013.
- **Related:** CONFIG-022 (verifier lowered to nit: the fan-curve path already uses `ProfileEdits`; config B4 adds
  any further `ProfileEdits` calls inside this merge, critic conflict 23).

### SETTINGS-V-002: Three bespoke edited/saved flag pairs duplicate the shared-field baseline

- **Severity:** medium (missed by the review; this is the critic's "settings flag pair" over-engineering flag).
- **Where:** `src/WSGM/Settings/SettingsViewModel.DeviceSetup.cs` (`_deviceAutoTdpEdited`,
  `_deviceControllerTargetEdited`, `_deviceGlyphSelectionEdited`, `_savedAutoTdp`, `_savedTargetIndex`,
  `_savedGlyphIndex`, the three setters, `DeviceEditsMade`); `SettingsViewModel.cs` (constructor reset block);
  `SettingsViewModel.Save.cs` (`ApplyTo` conditional writes, `SaveRequest` members `AutoTdpEdited`,
  `ControllerTargetEdited`, `GlyphSelectionEdited`, `DeviceAutoTdp`, `DeviceTargetIndex`, `DeviceGlyphIndex`, the
  merge branches, `AdvanceSharedBaseline`).
- **Problem:** AutoTDP, the global controller target and the glyph policy each carry flags, saved values, six request
  members, double application and their own baseline branch: a second hand-written copy of the generic shared-field
  mechanism. The review's SETTINGS-B1 would have added a fourth pair for `DeviceIntegration.Enabled`.
- **Best solution:** add `DeviceIntegration.Enabled`, `DeviceIntegration.AutoTdpEnabled`,
  `Profiles.Global.ControllerTarget` and `DeviceIntegration.GlyphSelection` to `WsgmSharedSettings` (SETTINGS-004).
  The controller-target entry reads `ControllerTarget ?? ProfileFields.DefaultControllerTarget` so an unset target
  does not look edited against the baseline; its write stores the value it is given. The merge only ever copies a
  shared entry from the snapshot (an unedited entry simply keeps the fresh value), and `ApplyTo` always writes a
  concrete target there, so `Copy` as `Write(to, Read(from))` is exact and no per-entry special case is needed.
  `ApplyTo` writes all four into the snapshot unconditionally; the merge copies them only when named in `SharedEdits`. Delete the six fields, the six request
  members, the constructor reset block and the special cases; the setters become plain `SetFieldIfChanged`.
  `DeviceEditsMade` becomes a query over `SharedEdits` against `_sharedBaseline` (or is replaced in tests by that
  query).
- **Tests:** per entry: unedited keeps the disk value, edited here is written, and a second save after a first saved
  edit does not count it as edited again. Filter as SETTINGS-001.
- **Plan v2:** B013.
- **Related:** SETTINGS-003, SETTINGS-004, SETTINGS-032 (setters).

### SETTINGS-006: Optional service members silently fall back to production

- **Severity:** low (verifier lowered from medium: latent, no test presses the takeover buttons).
- **Where:** `src/WSGM/Settings/SettingsViewModel.cs` (`SettingsServices` optional members);
  `SettingsViewModel.System.cs` (`_services.LoadPersisted ?? ConfigStore.Load`, `_services.DetectOtherManagers ??
  (() => OtherManagers.Detect())`, `_services.ApplyOtherManagers ?? ...`); `src/WSGM/Settings/AudioProfileEditor.cs`
  (`_read = read ?? AudioDiscovery.Read`); `tests/WSGM.UiTests/Infrastructure/UiFixture.cs`.
- **Problem:** `DetectOtherManagers`, `ApplyOtherManagers`, `LoadPersisted` and `ReadAudio` fall back to the real
  machine when omitted, and UiFixture omits the first three, so a UI test pressing "take over other managers" would
  run `schtasks` and read the real config. (`ReadUpdates` falls back to an inert `new UpdateState()`, not to
  production.)
- **Best solution:** every `SettingsServices` member is a required positional parameter; delete every `?? production`
  fallback in the view model and make `AudioProfileEditor` take a non-null reader. `SettingsTestServices.Inert`
  supplies inert versions.
- **Tests:** compile-time; plus the inert-model test of SETTINGS-005. Filters as SETTINGS-005.
- **Plan v2:** B117.
- **Related:** SETTINGS-005, SETTINGS-013.

### SETTINGS-009: Closing Settings while a save runs can cut the post-save external work

- **Severity:** low (verifier lowered from medium).
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`SaveWithStatusAsync`, `IsSaving`);
  `SettingsServices.Windows()` (`Task.Run` persist and apply); `src/WSGM/Settings/SettingsWindow.axaml.cs`
  (`UpdateSettingsEnabled`, no `Closing` handler); `src/WSGM/App.axaml.cs` (standalone Settings is the main window).
- **Problem:** B, Escape and the title-bar close stay live while `SettingsRoot.IsEnabled` is false. Closing standalone
  Settings mid-save exits the process and kills the worker that is reconciling the Steam Input shim and applying the
  autostart and other-manager takeovers; a config commit can also land without its splash promotion. It does not
  strand anything (`SteamAutostartTakeover.Disable` records before it writes, and the next save re-runs the apply),
  but the pass is interrupted.
- **Best solution:** in `SettingsWindow`, handle `Closing`: when `_viewModel.IsSaving` and
  `e.CloseReason` is neither `WindowCloseReason.OSShutdown` nor `WindowCloseReason.ApplicationShutdown`, set
  `e.Cancel = true` and a `_closeAfterSave` bool; in `OnViewModelPropertyChanged`, when `IsSaving` turns false and the
  bool is set, call `Close()`. OS and application shutdown are never vetoed, so a slow save cannot block sign-out.
  `IsProgrammatic` cannot separate user closes: B and Escape reach `BackOrClose()`, which calls `Close()` itself. So a
  programmatic close during a save (`DesktopTray.Dispose` calls `_settings?.Close()` at session shutdown, later
  `SettingsSurface`) is deferred too; the window then closes itself when the save completes, and the lifetime's own
  `ApplicationShutdown` close still goes through at once. The Closed handler's releases (Steam Input claim, input,
  import session) run when the window actually closes, as today. No other state.
- **Tests:** in `tests/WSGM.UiTests/Settings/SettingsInteractionTests.cs`: with a pending fake `Persist` and Steam
  Input management toggled before Save (after SETTINGS-V-004 an unrelated save makes no reconcile call), a user close
  is deferred and the window closes after completion with exactly one `reconcile` call; an `ApplicationShutdown`
  close is not deferred. Filter
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~SettingsInteractionTests"`.
  Manual: close standalone Settings right after Save with Steam Input management toggled; the shim deploys and the
  window closes once the save finishes.
- **Plan v2:** B119.
- **Related:** SETTINGS-021, SETTINGS-V-003, C4, C5.

### SETTINGS-012: Closing Settings re-reads config.json on the UI thread to restore the accent

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsWindowServices.cs` (`Create`: `() => ConfigStore.Load().AccentColor`);
  `src/WSGM/Settings/SettingsWindow.axaml.cs` (`Closed` handler, `AccentPalette.Apply(..., _services.ReadSavedAccent())`).
- **Problem:** closing a window takes the cross-process config mutex on the UI thread, and the lenient `Load` can
  quarantine a corrupt file (writing `config.bad.json`) as a side effect of a close.
- **Best solution:** Settings is the only writer of `AccentColor`, so the view model knows the saved accent. Add
  `internal string SavedAccentColor { get; private set; }`, set from `_config.AccentColor` at load and from
  `result.Config.AccentColor` after each successful persist; `SettingsWindowServices.Create` passes
  `() => viewModel.SavedAccentColor`. Set it whenever `Persist` returned (the commit happened, even when a
  splash repair or boot-manifest write then failed), not only on a fully successful status. No disk read on close.
- **Tests:** a close after an abandoned accent preview restores the saved accent with no store call (inert services
  record any `LoadPersisted` call); after a save it applies the newly saved value. WSGM.Tests and UiTests Settings
  filters.
- **Plan v2:** B117.
- **Related:** CONFIG-040 (Settings part, B133), SETTINGS-022.

### SETTINGS-013: DescribeOtherManagers reads the store on the UI thread, bypassing the seam

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsViewModel.System.cs` (`TakeOverOtherManagersAsync`:
  `DescribeOtherManagers(ConfigStore.Load())`).
- **Problem:** a direct store read on the UI thread, two lines from the `LoadPersisted` seam the same method uses, so a
  test cannot control it.
- **Best solution:** `DescribeOtherManagers(await Task.Run(_services.LoadPersisted))`.
- **Tests:** the "nothing found" branch reads the inert `LoadPersisted`. WSGM.Tests Settings filter.
- **Plan v2:** B117.
- **Related:** SETTINGS-006.

### SETTINGS-014: Hardware query types live in UI editor files and pull Shell and Overlay into Settings

- **Severity:** low.
- **Where:** `src/WSGM/Settings/AudioProfileEditor.cs` (`AudioDiscovery`, `SpatialAudioOption`, `SpatialName`);
  `src/WSGM/Settings/DisplayLayoutEditor.cs` (`DisplayCatalogFacts`); `SettingsViewModel.cs`
  (`ReadWindowsDisplayFacts`); consumers `src/WSGM/Overlay/AudioPanel.axaml.cs`,
  `src/WSGM/Shell/NativeQamAudioFormatService.cs`, `tests/WSGM.Tests/Shell/AudioPlaybackChoicesTests.cs`,
  `tests/WSGM.UiTests/Visual/PreviewAudioPanel.cs` and `tests/WSGM.UiTests/Infrastructure/UiFixture.cs` (both use
  `AudioDiscovery`).
- **Problem:** CoreAudio and WDC display reads are defined inside Settings UI files, so the overlay and the native QAM
  service import `WSGM.Settings` for `SpatialName` and `SpatialAudioOption`, and the WDC consumer set includes a
  Settings file.
- **Best solution:** pure moves. `AudioDiscovery`, `SpatialAudioOption` and `SpatialName` (as
  `SpatialAudioNames.For(Guid)`) go to `src/WSGM/Shell/AudioDiscovery.cs` beside `AudioProfileService` (which lives in
  Shell, not Core; the verifier corrected the review). `DisplayCatalogFacts` and the reader (as
  `DisplayCatalogFacts.Read(DisplayTargetIdentity)`) go to `src/WSGM/Core/DisplayCatalogFacts.cs`.
  `SettingsServices.Windows()` references the new homes; callers update their usings.
- **Tests:** existing tests follow the moves. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~WSGM.Tests.Themes|FullyQualifiedName~AudioPlaybackChoices"`.
- **Plan v2:** B120.
- **Related:** C15 (WDC consumer list), SETTINGS-015.

### SETTINGS-015: Settings and Shell/Overlay depend on each other

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Launch.cs` (`PluginActionOption` nested record);
  `src/WSGM/Shell/ShellSession.Actions.cs` (`ReadPluginActionOptions` return type);
  `src/WSGM/Settings/SettingsWindow.axaml.cs` (uses `OverlayController`, `SessionModes`, `WindowFinder`);
  `Shell/DesktopTray.cs`, `Settings/SettingsPluginActions.cs`.
- **Problem:** Shell constructs `SettingsWindow` and uses `SettingsPluginActions` and
  `SettingsViewModel.PluginActionOption`, while Settings uses Shell and Overlay types: a cycle between UI and session
  layers.
- **Best solution:** `PluginActionOption` becomes a top-level record in `src/WSGM/Core/PluginActionOption.cs` (B120).
  SETTINGS-010, 011 and 016 remove the rest: Shell only touches `SettingsWindow` through `SettingsSurface`, and the
  window no longer builds overlay types. `GlyphIcon` is a shared control and stays.
- **Tests:** existing tests compile against the new record. Filters of B120 and B123.
- **Plan v2:** B120 (record move), B123 (the rest).
- **Related:** SETTINGS-010, SETTINGS-011, SETTINGS-016.

### SETTINGS-016: The Settings view builds an OverlayController and SessionModes for the test sheet

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsWindow.axaml.cs` (`ShowTestOverlay`, `_testOverlay`).
- **Problem:** in the resident process a window constructs a second overlay controller and session-mode object.
- **Best solution:** add `Func<AppConfig, IDisposable> ShowTestSheet` to `SettingsWindowServices`, the window's own
  services record (the window's `_services` field is that record; it cannot reach the view model's private
  `SettingsServices`, and the test sheet is a window concern). `SettingsWindowServices.Create` takes the factory as a
  parameter, so the overlay types are named only by the composers: `SettingsSurface` and standalone `App.axaml.cs`
  pass today's construction (`new OverlayController(config, null, new SessionModes(config, null), previewOnly: true)`,
  then `ShowOverlay()`, returned as the disposable). The window keeps `_testOverlay` as an `IDisposable` and calls
  `_services.ShowTestSheet(_viewModel.SnapshotForPreview())`. Behaviour is identical. UiFixture passes a recording
  fake. (B117's "test-sheet member" on `SettingsServices` is superseded by this.)
- **Tests:** UI test: Test sheet calls the injected factory with the preview snapshot and disposes the previous one.
  Filters of B123.
- **Plan v2:** B123.
- **Related:** SETTINGS-011, SETTINGS-015, OVERLAY-V-002 (preview hides the Settings row).

### SETTINGS-017: Core depends on Themes for the default accent, whose digits exist in several places

- **Severity:** low.
- **Where:** `src/WSGM/Core/AppConfig.cs` (`AccentColor = AccentPalette.DefaultAccent`);
  `src/WSGM/Themes/AccentPalette.cs` (`DefaultAccent`); `src/WSGM/Themes/Palette.axaml` (`HcAccentBrush`);
  `src/WSGM/Settings/Pages/AppearancePage.axaml.cs` (first swatch); `SettingsViewModel.Plugins.cs` (profile colour
  seed `0xFF9D3D`); `src/WSGM/Settings/Pages/PluginSettingsPage.axaml` (badge, see SETTINGS-V-006).
- **Problem:** the config model references a UI theme type, and the same colour is typed in five places.
- **Best solution:** `public const string DefaultAccentColor = "#FFFF9D3D";` on `AppConfig` in Core;
  `AccentPalette.DefaultAccent` refers to it; the Appearance swatch and the profile seed derive from it (the seed via
  `AccentPalette.Parse(AppConfig.DefaultAccentColor)` masked to RGB). `Palette.axaml` keeps its literal, because XAML
  cannot reference the C# constant without a markup change.
- **Tests:** one agreement test in `tests/WSGM.Tests/Themes/AccentPaletteTests.cs` loading `Palette.axaml` and
  asserting `HcAccentBrush` equals the constant (it replaces `Parse_DefaultAccent_IsTheClassicOrange`, SETTINGS-025).
  Filter as SETTINGS-014.
- **Plan v2:** B120.
- **Related:** U04A-LFA-028 (Core to Themes edge), SETTINGS-V-006, SETTINGS-025.

### SETTINGS-018: The update flow uses statics and CancellationToken.None and cannot be tested from the view model

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Updates.cs` (`ShowUpdateState` calls `UpdateFailure.Read()`;
  `CheckForUpdatesNowAsync`, `ApplyUpdateAsync` call `UpdateChecker.CreateHttpClient/CheckAsync/DownloadAsync/RunSetup`
  with `CancellationToken.None`).
- **Problem:** the check, download, run and failure read are untestable statics called straight from the view model.
- **Best solution:** four `SettingsServices` members: `Func<Task<UpdateState>> CheckUpdates`,
  `Func<ReleaseInfo, IProgress<double>, Task<string>> DownloadUpdate` (the production versions own the `HttpClient`
  and keep today's `Task.Run` and `CancellationToken.None`), `Action<string> RunSetup` and `Func<string?>
  ReadUpdateFailure`. The view model calls them where it calls the statics today; status texts and the catch filter
  stay. Do not add a window-lifetime token (a deviation from B121's spec, made by the solution checker): the update is
  the user's explicit, confirmed action, and today a confirmed download finishes and starts setup even if the
  resident Settings window is closed meanwhile. Cancelling it on close would change that workflow, and a token
  cancellation surfaces as `OperationCanceledException`, which the current catch filter does not handle. The
  `_displayDiscoveryClosed` flag stays as it is. The download's stall bound belongs to `UpdateChecker`'s owner.
- **Tests:** with fakes: confirm then apply calls download then run setup with the downloaded path; a download that
  throws `HttpRequestException` shows "The update did not start: ..." and never calls run setup; a recorded failure
  is prefixed to the status. WSGM.Tests Settings filter.
- **Plan v2:** B121.
- **Related:** U04A-LFA-010 (caller side), SETTINGS-024.

### SETTINGS-019: The Modern Standby line drops armed wake devices past sixteen

- **Severity:** low.
- **Where:** `src/WSGM/Core/ModernStandbyDiagnostics.cs` (`MaximumReportedWakeSources = 16`, the
  `armed.Count < MaximumReportedWakeSources` check), rendered on the System page.
- **Problem:** an arbitrary count cap silently hides armed wake devices, which is exactly the list a user reads to
  find what wakes the machine.
- **Best solution:** delete the constant and the count condition so every armed device is listed; the System page row
  already wraps.
- **Tests:** a report with more than sixteen armed devices lists all of them. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~ModernStandby"`.
- **Plan v2:** B135; decided: remove the 16-device cap, the row may wrap.
- **Related:** no-arbitrary-limits rule.

### SETTINGS-021: A failed post-save Steam Input step reports "Save failed" although the save committed

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`SaveWithStatusAsync`: one `try` around persist,
  `CompletePersistedSave` and `ApplySteamInput`).
- **Problem:** an exception from the external apply shows "Save failed: ..." after `config.json` was written and the
  baseline advanced, so the user believes nothing was saved.
- **Best solution:** together with SETTINGS-V-003: a throw from `Persist` still reports "Save failed: ..." and skips
  the apply. After a successful commit, catch the apply step separately, report it through `_services.Report` and set
  `StatusText = $"Saved; applying Steam Input failed: {ex.Message}"`.
- **Tests:** with Steam Input management toggled before Save (so SETTINGS-V-004 runs the reconcile), a throwing
  reconcile fake gives the "Saved; applying ..." status and the baseline advanced. Filters of
  B119.
- **Plan v2:** B119.
- **Related:** SETTINGS-V-003, SETTINGS-009.

### SETTINGS-022: An unreadable config at open shows defaults and the save later fails generically

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsViewModel.cs` (production load through lenient `ConfigStore.Load`);
  `SettingsViewModel.Save.cs` (`PersistSave`, strict `LoadForMutation`).
- **Problem:** with a corrupt or unreadable `config.json`, Settings opens on defaults with no hint, and Save fails with
  a generic message.
- **Best solution:** after the config store gains read outcomes (B039), move `PersistSave` and the splash slot repair
  into `src/WSGM/Settings/SettingsPersistence.cs` over the store's writer transaction. Settings receives the load
  outcome with its config; a Corrupt or Unreadable outcome is shown in the existing status strip. Save stays enabled
  and refuses strictly as today (the review's "disable Save" was dropped: it changes a workflow). A config written by
  a newer WSGM, or one with unknown recovery enum values, is not an error state (maintainer decision): it loads best
  effort as today, Settings opens on what was understood with no status warning, and a save writes normally. There is
  no read-only mode, so Settings handles no UnsupportedSchema outcome; if B039 or the schema batch still produce one,
  that is their defect to fix against the same decision, not a Settings refusal. Normalize diagnostics are returned
  rather than logged from the view-model path. The window and view model no longer read config on the UI thread.
- **Tests:** Corrupt and Unreadable at open each show the status text, and a save in that state refuses without
  writing; a config with a higher `SchemaVersion` and an unknown enum value opens with the understood values, shows
  no warning and saves. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~SettingsSaveMerge"`.
- **Plan v2:** B133 (after F01/B039).
- **Related:** C7, CONFIG-040 (Settings part), SETTINGS-012.

### SETTINGS-023: A recorder that fails to start leaves the UI in "Press keys..." or "Press buttons..."

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsWindow.axaml.cs` (`ArmHotkeyRecorder`, `ArmChordRecorder`, `Observe`).
- **Problem:** if `KeyRecorder.Start` or `GamepadChordRecorder.Start` throws after the arming delay, the error is
  logged but the view model stays in the recording state.
- **Best solution:** in each `Arm...Recorder` method (which resumes on the UI thread after its `await`), wrap
  `Start()` in `try`/`catch`: dispose and null the recorder, call `_viewModel.SetHotkeyRecording(false)` or
  `SetChordRecording(false)`, then rethrow so `Observe` still logs. Simpler than a dispatcher post from the fault
  continuation. The existing shortcut or chord stays bound (the setters only change the recording flag); per the
  maintainer decision, only an explicit Clear clears a binding. Today `KeyRecorder` reports `Cleared()` on Escape;
  making Escape and the 3 s timeout keep the binding is the recorder change in B080, not part of this fix.
- **Tests:** none new. `KeyRecorder` and `GamepadChordRecorder` are constructed inside the window with no seam, and
  adding a recorder factory only to make `Start()` throw in a test is mechanism the fix does not need. The existing
  UiTests Settings filter must stay green; the change is a reviewed `try`/`catch`.
- **Plan v2:** B135.
- **Related:** INPUT recorder fixes in B080 (navigation and recorders), no overlap in files.

### SETTINGS-024: Plugin package actions and setup repair are statics with real paths inside the view model

- **Severity:** low.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Plugins.cs` (`CanRepair => File.Exists(InstallLayout.SetupExe)`,
  `StartRepair`, `LoadPluginPackages`, `ActOnPackageAsync`); `src/WSGM/Settings/PluginPackageRow.cs`.
- **Problem:** install, remove, bundle offers and repair use `InstallLayout` paths and `PluginPackageManager`
  statics directly, and a bound getter probes the disk on every read.
- **Best solution:** `SettingsServices` members `Func<PluginPackageCatalog, PluginPackagePage> ReadPackages`,
  `Func<PluginPackageRowState, BundleManifest?, Task<string>> ActOnPackage` (today's `ActOnPackageAsync` body, still on
  `Task.Run`), `Func<bool> RepairAvailable` and `Action StartRepair` (today's `StartRepair` body). New
  `internal sealed record PluginPackagePage(BundleManifest? Bundle, IReadOnlyList<PluginPackageRowState> Rows)` in
  `src/WSGM/Settings/PluginPackageRow.cs`; the production `ReadPackages` is today's read half of `LoadPluginPackages`
  (bundle read, `PluginOffers.Compute` with the machine and adapter inventory, `PluginPackageManager.Rows`, the same
  `Log.Warn` on an unreadable bundle). `LoadPluginPackages` keeps only the collection fill, passing `page.Bundle` to
  each row's act. `CanRepair` becomes a get-only value set from `RepairAvailable()` in `LoadPluginPackages` and raised
  there; setup.exe does not appear or vanish while the window is open, so one read per load is enough (today nothing
  reloads the page after a package action, and that stays).
- **Tests:** package act notices with fakes (install, remove, access denied text); repair hidden when unavailable.
  WSGM.Tests Settings filter.
- **Plan v2:** B121.
- **Related:** SETTINGS-018.

### SETTINGS-025: Weak or misplaced Settings and Themes tests

- **Severity:** low (verifier corrected one item).
- **Where:** `tests/WSGM.Tests/Settings/StartupAppRowTests.cs`; `SettingsViewModelSplashTests.cs`
  (`SelectorValueListsCoverEveryEnumMember`); `GameLibrarySettingsTests.cs`; `tests/WSGM.Tests/Themes/AccentPaletteTests.cs`
  (`Parse_DefaultAccent_IsTheClassicOrange`).
- **Problem:** `StartupAppRowTests` asserts INPC on auto-properties only; two of three `GameLibrarySettingsTests` test
  `ConfigStore` defaults and repair, not Settings; the accent test restates a constant.
  `SelectorValueListsCoverEveryEnumMember` is weak but not a tautology: it fails when a member is appended after the
  named last member, and its `DoesNotContain(WithText)` assertion is real.
- **Best solution:** delete `StartupAppRowTests`; keep `SelectorValueListsCoverEveryEnumMember`'s `DoesNotContain`
  assertion and replace its last-member comparison with a check that the selector list contains every
  `Enum.GetValues` member; move the two `ConfigStore` cases to Core configuration tests; fold the accent constant test
  into the SETTINGS-017 agreement test.
- **Tests:** the edited tests themselves. Filters
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~WSGM.Tests.Themes|FullyQualifiedName~Configuration"`.
- **Plan v2:** B135.
- **Related:** SETTINGS-017.

### SETTINGS-026: Missing behavioural tests for the defects above

- **Severity:** low.
- **Where:** `tests/WSGM.Tests/Settings`, `tests/WSGM.UiTests/Settings`.
- **Problem:** nothing covers Themes/Sounds/Animations surviving a save, banner-enabled Device Integration surviving,
  close during save, post-save apply order and failure, the update flow, or an unreadable config at open.
- **Best solution:** each test lands with its fix (B013, B117, B119, B121, B133, as listed under SETTINGS-001, 003,
  008, 009, 018, 022). B135 adds only those still missing when it runs.
- **Tests:** as listed in those findings.
- **Plan v2:** B135 (remainder).
- **Related:** SETTINGS-001, 003, 008, 009, 018, 022.

### SETTINGS-027: LiveBackdrop's native session lifecycle has no seam and no lifecycle tests

- **Severity:** low.
- **Where:** `src/Avalonia.LiveBackdrop/LiveBackdrop.cs` (direct `NativeMethods` create, blur and destroy calls);
  `src/Avalonia.LiveBackdrop/NativeMethods.cs`; `tests/Avalonia.LiveBackdrop.Tests/AttachmentTests.cs`.
- **Problem:** failure callback generations, `Retry`, blur-update failure, hide/show recreate and dispose with a queued
  callback are untested; the four tests cover only the headless no-handle path.
- **Best solution:** three `internal` static delegate fields in `LiveBackdrop` (create, set blur, destroy) initialized
  to the P/Invoke calls, with `InternalsVisibleTo("Avalonia.LiveBackdrop.Tests")` in the csproj. No interface, no
  public API change, no retry policy (the README contract and the "uncertain is never retried automatically" rule).
  Avalonia 12.1.2 pins stay. The native delegates alone do not reach the create call in the headless test platform:
  `Reconcile` first requires `ActualTransparencyLevel == Transparent` and an `HWND` platform handle, and headless
  windows have no HWND (the existing tests all end at "The window has no Win32 handle."). Add one more internal static
  delegate, `Func<Window, nint> ReadWindowHandle`, defaulting to today's `TryGetPlatformHandle` HWND check (0 when
  absent), and keep the transparency wait as is. Before writing the tests, check whether a headless window with
  `TransparencyLevelHint = [WindowTransparencyLevel.Transparent]` reports `Transparent`; if it does not, write only
  the tests that path allows (disabled, hidden, dispose, `Retry` clearing a failure) and record the create-path tests
  as dropped rather than adding a transparency seam.
- **Tests:** create failure, late callback after `Stop` ignored, `Retry` clears the failure, blur failure stops the
  session, hide then show recreates, dispose with a queued callback.
  `dotnet test tests\Avalonia.LiveBackdrop.Tests\Avalonia.LiveBackdrop.Tests.csproj`. Manual M01-34 only if the
  native payload changes (it does not in this batch).
- **Plan v2:** B134.
- **Related:** SETTINGS-028, SETTINGS-029, SETTINGS-030.

### SETTINGS-V-003: A splash or boot-manifest failure after the commit skips the Steam Input apply

- **Severity:** low (missed by the review).
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`CompletePersistedSave` throws `IOException` when
  `result.Failure` is set; `SaveWithStatusAsync` then never reaches `ApplySteamInput`).
- **Problem:** `config.json` is committed, but the shim reconcile and the takeovers are skipped, so persisted intent
  and Steam's folder disagree until the next save, against the method's own "deployment follows persisted intent".
- **Best solution:** `CompletePersistedSave` returns the failure text instead of throwing. `SaveWithStatusAsync` runs
  the external apply whenever `Persist` returned (the commit succeeded), then sets the status: the splash or boot
  failure keeps today's "Save failed: ..." text; otherwise SETTINGS-021's apply failure text; otherwise "Saved HH:mm:ss".
- **Tests:** with Steam Input management toggled before Save, a `Persist` fake returning a `Failure` (and
  `SaveChanges` with the shim flag set) still records one `reconcile` and shows the failure text. Filters
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~SettingsInteractionTests"` and
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SettingsSaveMerge"`.
- **Plan v2:** B119.
- **Related:** SETTINGS-021, SETTINGS-009.

### SETTINGS-V-004: Every save reconciles the shim and re-runs both takeovers, with possible UAC prompts

- **Severity:** low (missed by the review; plan v2 settled the maintainer question).
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`ApplySteamInputManagementAfterSave` and the two takeover
  helpers); `src/WSGM/Core/SteamInputManagement.cs` (`Apply` self-elevates on access denied).
- **Problem:** an unrelated save (say, an accent change) reconciles the shim, which can raise a UAC prompt when
  unelevated, and with the takeovers accepted rescans scheduled tasks and Steam autostart. WSGM's Steam page applies the
  shim only when its own toggle changes.
- **Best solution:** apply only what this save changed. `SettingsSaveMerge.Apply` (B013) returns
  `(AppConfig Config, SaveChanges Changes)`: before mutating `fresh` it reads `SteamInputManagementEnabled`,
  `SteamAutostartTakeoverAccepted` and `OtherManagersTakeoverAccepted`, and after the merge it fills
  `internal sealed record SaveChanges(bool Shim, bool SteamAutostartAccepted, bool OtherManagersAccepted)` (shim value
  differs, autostart acceptance went false to true, managers acceptance went false to true). Computing it inside the
  merge means `PersistSave` (later `SettingsPersistence`) and every fake `Persist` that merges through
  `SettingsSaveMerge.Apply` (UiFixture, `SettingsTestServices`) report the same answer with no second copy of the
  rule. `SaveResult` gains a `Changes` member, and `SettingsExternalApply.AfterSaveAsync` runs each step only when its
  bool is set. No new baseline or guard: the comparison is against the disk value the merge already loads. The two "Check and take over" buttons remain the explicit re-check. Record the rule in
  `Settings/AGENTS.md` with SETTINGS-042.
- **Tests:** an accent-only save makes no reconcile, scan or detect calls; toggling management makes one reconcile;
  accepting a takeover runs it once and a later unrelated save does not. WSGM.Tests Settings filter.
- **Plan v2:** B117.
- **Related:** SETTINGS-008, install.verify V-004 and critic conflict 27 (WINSVC-B3/B095 owns the
  `SteamAutostartService`/`OtherManagers` side), B076 (shim instance used by the reconcile).

### SETTINGS-V-005: About and credit links open through a direct ShellExecute from the elevated process

- **Severity:** low (missed by the review).
- **Where:** `src/WSGM/Settings/Pages/AboutPage.axaml.cs` (`OnOpenLink`); `docs/elevation.md` ("Settings pages open
  through a medium one-shot").
- **Problem:** Settings normally runs inside the resident elevated process. A direct `ShellExecute` of an https URL
  only works through an unelevated Explorer; in game mode the link fails or starts an elevated browser. Plan v2 says
  to use "the existing medium one-shot used for `ms-settings:`", but that one-shot no longer exists in code (no
  `ms-settings` launch remains in `src`; `RadioManager` replaced it and `docs/elevation.md` is stale).
- **Best solution:** keep plan v2's intent with the smallest addition. When `ElevationCheck.IsCurrentProcessElevated()
  is true` (it returns `bool?`; unknown opens directly), start the link through the existing de-elevation path,
  `UnelevatedLauncher.TryStartViaScheduledTask(Environment.ProcessPath, "--open-link=" + url)`, on `Task.Run` and
  observed with `Log.Observe`: the call is synchronous with a 30-second budget and must not run on the UI thread.
  Add the one-shot in `Program.Main` beside `--export-setup-answers=`, parsed with the existing `ArgumentValue(args,
  "--open-link=")` (the `RunOneShot` table is for flag-only commands that take no input). It accepts only an
  `https://` URL, calls `AppLauncher.StartProtocol`, returns 0 or 1, and takes no mutex and starts no Avalonia. When
  not elevated, or when the task does not dispatch, open directly as today. Rewrite the stale `docs/elevation.md`
  section to describe this one-shot. If the maintainer prefers no new one-shot, the fallback is a documented known
  limitation and the doc fix alone.
- **Tests:** the one-shot refuses non-https input; the About handler chooses the task path only when elevated (pass the
  elevation check and launcher as delegates to a small static helper). Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"`. Attended check:
  an About link from game mode opens a normal browser.
- **Plan v2:** B135.
- **Related:** docs pass B177. Kept for its functional effect (a link from game mode should open the user's normal
  browser); the elevated-browser concern is not pursued as hardening. INSTALL-008 / B029 (task XML staging) is dropped
  by maintainer decision, so this uses the de-elevation task exactly as it is today.

### SETTINGS-V-006: The Plugins page carries its own literal badge palette

- **Severity:** low (missed by the review).
- **Where:** `src/WSGM/Settings/Pages/PluginSettingsPage.axaml` (badge styles for accent, community, info, good, warn
  and bad, including `#26FF9D3D`, `#73FF9D3D`, `#FFFFB870`); `Themes/AGENTS.md` (tokens come from the palette).
- **Problem:** a page-local palette against the theme rules, and a fifth copy of the default accent digits.
- **Best solution:** move each literal to a named token in `src/WSGM/Themes/Palette.axaml` with the identical value and
  reference it with `StaticResource`. The accent badge stays orange (following the user's accent would change
  appearance for non-default accents, which is not in scope). Extend the SETTINGS-017 agreement test to the badge
  token.
- **Tests:** Settings Visual filter with unchanged baselines,
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings"`.
- **Plan v2:** B135.
- **Related:** SETTINGS-017, SETTINGS-041.

### SETTINGS-V-007: Authored profile names are silently cut to 48 characters

- **Severity:** low (missed by the review, which misfiled it as a storage contract under SETTINGS-035).
- **Where:** `src/WSGM/Core/DeviceConfiguration.cs` (`DeviceAuthoredProfile.MaxNameLength = 48`);
  `src/WSGM/Settings/DeviceProfileRowViewModel.cs` (name setter truncation); `src/WSGM/Core/ConfigStore.cs` (load-time
  normalize truncation); `src/WSGM/Settings/Pages/PluginSettingsPage.axaml.cs` (keyboard maximum).
- **Problem:** a WSGM-chosen cap that truncates content silently, at edit and at every load.
- **Best solution:** delete `MaxNameLength`, the truncation in the row setter and in `ConfigStore`'s normalize, and
  open the on-screen keyboard without a maximum. The keyboard needs one change for that: the string overload
  `SettingsWindow.ShowOnScreenKeyboard(string, int, string, Func<string, string?>)` clamps with `Math.Max(1,
  maximumLength)`, so passing 0 would allow one character. Make it pass the value through (`OpenKeyboardEditor`
  already clamps to 0, and `TextBox.MaxLength = 0` means no limit), document 0 as "no maximum", and have the profile
  name call in `Pages/PluginSettingsPage.axaml.cs` pass 0. Plugin text rows keep their positive SDK maximum and the
  colour editor its 9. Empty-name fallback to the id stays. Done with the other config caps so `ConfigStore` is edited
  once; B038's file list must add `src/WSGM/Settings/SettingsWindow.axaml.cs` and
  `src/WSGM/Settings/Pages/PluginSettingsPage.axaml.cs`.
- **Tests:** a 100-character name survives edit, save and reload. Filter of B038 (includes
  `FullyQualifiedName~DeviceProfile`).
- **Plan v2:** B038 (config domain); decided (D2): the profile-name cap is not on the kept list, so it goes.
- **Related:** SETTINGS-035, CONFIG caps in B038.

### SETTINGS-V-008: Each Settings window runs its own GamepadService poller

- **Severity:** low (missed by the review; outside this domain's authority).
- **Where:** `src/WSGM/Settings/SettingsWindowServices.cs` (`Create`: `new GamepadService()`);
  `src/WSGM/Shell/BootSplash.cs`.
- **Problem:** inside the resident process each Settings window adds a 16 ms `DispatcherTimer` SDL poller beside the
  session's input sources. `SdlGamepads` is already the single pump, so this is a second reader, not a second pump.
- **Best solution:** no change in this refactor. Input owns `GamepadService`; B075 already threads the managed pad
  into `SettingsWindowServices.Create`, which fixes the one real defect (INPUT-009). Whether Settings should borrow the
  session's button source is recorded as an open item for the input owner and `SettingsSurface`; do not add a shared
  source now.
- **Tests:** none.
- **Plan v2:** no batch (recorded open item); B075 is the adjacent change.
- **Related:** INPUT-009, SETTINGS-011.

### SETTINGS-028: LiveBackdrop raises StateChanged when nothing changed

- **Severity:** nit.
- **Where:** `src/Avalonia.LiveBackdrop/LiveBackdrop.cs` (visibility and state property handlers raising
  `StateChanged` while disabled or hidden).
- **Problem:** subscribers re-render on every visibility or window-state change even when `IsActive`, `FailureReason`
  and the enabled state are unchanged.
- **Best solution:** compare `(IsActive, FailureReason, enabled)` before and after each transition and raise only on a
  difference.
- **Tests:** toggling visibility while disabled raises nothing; an activation raises once. LiveBackdrop test project.
- **Plan v2:** B134.
- **Related:** SETTINGS-027.

### SETTINGS-031: The library README carries WSGM product text and probe evidence

- **Severity:** nit (verifier dropped the fallback-colour half).
- **Where:** `src/Avalonia.LiveBackdrop/README.md` (WSGM evidence section); `LiveBackdrop.cs` (default fallback
  `#181B20`).
- **Problem:** WSGM-specific evidence lives in a reusable library's README. Passing WSGM's `DeckCanvasBrush`
  (`#171A1F`) as the fallback was proposed as "visually identical" but is a one-step colour change, which the UI
  freeze forbids.
- **Best solution:** in the README's "Support and evidence" section, move the WSGM-specific sentences ("The integrated
  WSGM Overlay was then confirmed ..." and "WSGM exposes the radius in Settings > Quick Access, from 0 to 60 physical
  pixels.") to the backdrop paragraph of `docs/overlay-and-input.md`, keeping their date context; the native-probe
  and sample evidence describe the library itself and stay. `docs/decisions.md` (the 2026-09-24 overlay glass entry)
  points at the README for evidence; point it at both. Do not change the fallback colour.
- **Tests:** `npm run format:check` for the Markdown.
- **Plan v2:** B135.
- **Related:** SETTINGS-027.

### SETTINGS-032: Some Settings setters raise unconditionally

- **Severity:** nit.
- **Where:** `src/WSGM/Settings/SettingsViewModel.DeviceSetup.cs` (device setters), `SettingsViewModel.System.cs`
  (around lines 58-106), `SettingsViewModel.QuickAccess.cs` (around 70-79), `SettingsViewModel.Launch.cs` (around
  57-101).
- **Problem:** these setters assign and raise on every set while the rest use `SetField`/`SetFieldIfChanged`.
- **Best solution:** convert them to `SetFieldIfChanged` plus the dependent `Raise` calls they already make (after
  B013 the device setters carry no edited flags). No behaviour change beyond fewer redundant notifications.
- **Tests:** WSGM.Tests and UiTests Settings filters, baselines unchanged.
- **Plan v2:** B135.
- **Related:** SETTINGS-V-002.

### SETTINGS-033: Magic numbers repeated across owners

- **Severity:** nit.
- **Where:** glyph index 2 means Nintendo (`SettingsWindow.axaml.cs` `IsNintendoLayout`,
  `SettingsViewModel.QuickAccess.cs` clamp 0..2); plugin action step timeout 1..120 (`PluginActionListEditor.cs`,
  `Core/ConfigStore.cs`, `Shell/PluginActionSequence.cs`); blur radius 0..60 (`Core/ConfigStore.cs`,
  `Overlay/OverlayWindow.axaml.cs`, `LiveBackdrop.cs`, `Native/Backdrop.cpp`); colour keyboard maximum 9
  (`Pages/PluginSettingsPage.axaml.cs`).
- **Problem:** the same bound typed in several places can drift.
- **Best solution:** compare with `(GlyphStyle)GlyphStyleIndex == GlyphStyle.Nintendo` and clamp with the enum's
  range; one `PluginActionStep.MinimumTimeoutSeconds`/`MaximumTimeoutSeconds` pair used by the three sites; a public
  `LiveBackdrop.MaximumBlurRadius` constant reused by WSGM's C# sites (the native file keeps its literal, see
  SETTINGS-030); a named constant for the colour keyboard length. These are validation bounds that refuse or clamp
  user input, not new caps.
- **Tests:** existing tests. Filters of B135.
- **Plan v2:** B135.
- **Related:** SETTINGS-030.

### SETTINGS-034: The MaximumLength doc comment says zero means no limit

- **Severity:** nit.
- **Where:** `src/WSGM/Settings/PluginSettingRowViewModel.cs` (`MaximumLength` doc).
- **Problem:** the comment says zero places no limit while the text setter truncates to it; only non-text rows have
  zero (the SDK requires a positive maximum for text), so the doc is wrong, not the code.
- **Best solution:** correct the comment: the value is the SDK-declared text length (longer input is refused, see
  SETTINGS-035), and non-text rows report zero.
- **Tests:** none.
- **Plan v2:** B135.
- **Related:** SETTINGS-035.

### SETTINGS-035: Plugin text settings are bounded by the SDK-declared length

- **Severity:** nit (verifier split off the WSGM profile-name cap as SETTINGS-V-007).
- **Where:** `src/WSGM/Settings/PluginSettingRowViewModel.cs` (text setter); `Device.Sdk` `PluginSettingsManifest`
  (declared maximum).
- **Problem:** the review grouped two bounds. The plugin text bound is an SDK storage contract and the input controls
  (TextBox `MaxLength`, on-screen keyboard maximum) already stop input at it, so the setter's truncation never fires
  from the UI. The profile-name cap is a different, WSGM-chosen limit (V-007).
- **Best solution:** keep the plugin bound: it is the plugin's own declared contract (`CapabilityDescriptor.MaximumLength`,
  validated by `DeviceCapabilityRouter`), not a WSGM cap, so D2's removal list does not reach it. D2 does require
  bounds to refuse, never truncate, so the `TextValue` setter stops cutting the string: a value longer than
  `MaximumLength` is ignored (the row keeps its previous text and publishes nothing). The input controls already stop
  at the bound, so nothing visible changes. Reword the comment to say it is the plugin's declared storage length,
  enforced at input and refused past it. No new cap anywhere.
- **Tests:** in the plugin setting row tests, setting a text one character over the declared maximum leaves the value
  and the published capability unchanged. Filter
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginSettings"`.
- **Plan v2:** B135; decided (D2): refuse, never truncate.
- **Related:** SETTINGS-V-007, SETTINGS-034.

### SETTINGS-036: Profile equality uses reflection JSON serialization

- **Severity:** nit.
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (`AdvanceSharedBaseline`: `JsonSerializer.Serialize` of
  the profile lists).
- **Problem:** reflection-based serialization in an app that otherwise uses the source-generated
  `ConfigJsonContext`, and trimming-unsafe.
- **Best solution:** serialize both sides with the source-generated context's `DeviceAuthoredProfile` list type
  (`ConfigJsonContext.Default`), or the `ConfigJson.Clone` helper's serializer once B038 adds it.
- **Tests:** existing profile tests. WSGM.Tests Settings filter.
- **Plan v2:** B135.
- **Related:** B038 (`ConfigJson`).

### SETTINGS-038: Fire-and-forget tasks are discarded without observation

- **Severity:** nit.
- **Where:** `SettingsViewModel.Displays.cs` (`StartAudioDiscovery`), `AudioProfileEditor.cs`,
  `Pages/DisplayPage.axaml.cs`, `SettingsWindow.axaml.cs` (`_ = _services.RefreshDeviceOwner()`).
- **Problem:** `_ = task` lets a throwing injected reader become an unobserved exception.
- **Best solution:** `Log.Observe(task, "...")` at each site, as `DeviceOwnershipPage` already does.
- **Tests:** none beyond existing.
- **Plan v2:** B135.
- **Related:** SETTINGS-007.

### SETTINGS-040: Row edits repaint the arrangement view only through an implicit Rows notification

- **Severity:** nit.
- **Where:** `src/WSGM/Settings/DisplayLayoutEditor.cs` (`Revalidate` raises `PropertyChanged(nameof(Rows))`);
  `src/WSGM/Settings/DisplayArrangementView.cs` (editor `PropertyChanged` subscription).
- **Problem:** the repaint contract is unstated on both sides and easy to break.
- **Best solution:** one comment on each side naming the contract; no mechanism change.
- **Tests:** none.
- **Plan v2:** B135.
- **Related:** none.

### SETTINGS-041: Theme resource hygiene: DynamicResource for stable tokens and an undocumented second palette

- **Severity:** nit.
- **Where:** `src/WSGM/Themes/Shared.axaml` (glyph-tile setters using `DynamicResource`); `Themes/CommandDeck.axaml`
  (deck palette); `Palette.axaml` merged by `Typography.axaml`, `Shared.axaml`, `TabStripTheme.axaml`,
  `SettingsPages.axaml` and `App.axaml`; `Themes/AGENTS.md` (rule at line 7).
- **Problem:** stable tokens resolved dynamically against the theme guidance; CommandDeck's palette is a second
  source nobody documents.
- **Best solution:** switch the glyph-tile setters to `StaticResource`; document CommandDeck as the overlay deck
  palette owner in `Themes/AGENTS.md` (shown as a separate guidance diff). Leave the per-file `Palette.axaml`
  includes: static resolution at style load may rely on them.
- **Tests:** UI baselines for Settings and Overlay unchanged.
- **Plan v2:** B135.
- **Related:** SETTINGS-V-006.

### SETTINGS-042: Settings guidance describes the mechanisms this plan replaces

- **Severity:** nit.
- **Where:** `src/WSGM/Settings/AGENTS.md` (the `SharedFields` paragraph and "The production parameterless
  SettingsViewModel ... never fall back to the real profile").
- **Problem:** after B013 and B117 the guidance names a list that no longer exists and a constructor that is deleted.
- **Best solution:** one AGENTS.md diff for sign-off: shared fields live in `Core/WsgmSharedSettings` and a field
  another surface writes goes there; the merge starts from the fresh load and copies only Settings-owned fields;
  services are required and tests use `SettingsTestServices`; the post-save external apply runs only for what the save
  changed (SETTINGS-V-004); one Settings window per process through `SettingsSurface`. Then run
  `eng/check-agent-guidance.ps1`.
- **Tests:** `./eng/check-agent-guidance.ps1`; `npm run format:check`.
- **Plan v2:** B177.
- **Related:** SETTINGS-004, 005, 011, V-004.

## Refuted or no-change

- **SETTINGS-020** (synchronous reads on the UI thread at construction): no change unless a stall is measured, by the
  simplify rule. The verifier corrected the anchors (Program Files probes are `SettingsViewModel.Startup.cs` via
  `_services.DetectStartupApps`; overlay-opened Settings also runs `LoadPluginPackages` on the shell UI thread). If a
  stall is ever measured, move the read into the `Opened` worker kick-off beside display and audio discovery.
- **SETTINGS-029** (native vector reuse and synchronous owner refresh in `Backdrop.cpp`): no change; it fixes no
  measured defect and forces a native rebuild plus an M01-34 pass.
- **SETTINGS-030** (named constants for the private thumbnail flag and DWM ordinals in `Backdrop.cpp`): no change
  unless the native file is rebuilt for another reason.
- **SETTINGS-037** (editors alias the loaded config, "forcing a second clone"): no change; moved here by the solution
  checker. The proposed fix (clone `GameModeLaunch` once at load, then capture with one clone) does not remove the
  need for the second clone. That clone cuts aliasing between the captured snapshot and the live editors, not between
  the editors and `_config`: `ApplyTo` puts live editor objects into the snapshot (`PluginActionListEditor.Build()`
  returns `[.. Rows.Select(row => row.Step)]`, the rows' own mutable steps; `ApplyLaunchTo` copies `KnownDisplays`
  entries by reference, which display discovery mutates), and its own comment says so ("ApplyTo intentionally reuses
  several bound objects"). Removing the clone would need deep-copying builders, which is more code than the one
  `CloneJson` call. B135 drops it from its list.
- **SETTINGS-039** (shared file-picker helper and tag-switch handlers in page code-behinds): optional; the page-local
  handlers are readable and stay as they are.
