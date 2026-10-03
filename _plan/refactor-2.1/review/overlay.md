# Overlay UI review (Avalonia overlay, Controls, headless UI tests)

Reviewer scope: `src/WSGM/Overlay/**` (93 files, about 16,900 lines including XAML), `src/WSGM/Controls/**` (15 files, about 3,000 lines), `tests/WSGM.UiTests/**` (non-generated sources, fakes, fixtures, infrastructure, visual harness) plus the overlay/controls unit tests in `tests/WSGM.Tests/Overlay` and `tests/WSGM.Tests/Controls`. Callers and contracts were traced into `Shell/ShellSession*.cs`, `Shell/DeviceOverlayBridge.cs`, `Shell/CommonPluginOverlaySource.cs`, `Shell/NativeQamPowerProfileService.cs`, `Shell/LibraryTabManager.cs`, `Shell/HotkeyService.cs`, `Settings/SettingsWindow.axaml.cs`, `Input/GamepadNavigation.cs`, `Input/GamepadService.cs`, `Core/KeyboardService.cs`, `Core/SteamLaunchConfig.cs`, `Core/PowerActions.cs` and `WSGM.Plugin.Sdk/PluginActions.cs`. Read-only; nothing was built or run. Baseline `master` 1329813f.

Context that matters for every section below: the prior audit never reached this domain. `audit-coverage.md:31-36` marks U12A-OVL, U12B-OVL, U12C-OVL and U13C-SUI as "unstarted Claude part; 0 saved IDs", and `audit/A01/remaining-scope.json` lists all 155 Overlay/Controls/UiTests paths as "structural census only". The findings ledger therefore has only two overlay-adjacent rows (U05-LFB-011, U05-LFB-028). Everything numbered OVERLAY-NNN here is new unless a ledger id is cited.

## 1. Plan claims check

| # | Plan statement (location) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "Every tracked scoped file has a final coverage status" (refactor-plan.md:23) | Stale for this domain | audit-coverage.md:31-36; remaining-scope.json (155 overlay paths, no status) | This report is the first semantic pass. AF02 must record a per-file status for every overlay/controls/UiTests path; section 4 gives one. |
| C2 | "UI owners: surface lifetime, drafts, navigation/focus and rendering; feature command/query interfaces and UI dispatcher; no global service lookup, store/native acquisition in views" (refactor-plan.md:71) | Accurate as a target, far from current code, and unscoped | Views and the window acquire stores and native state directly: `OverlayWindow.Power.cs:49,55,84,95,106` (PowerActions), `DisplayModeView.cs:31-36` (DisplayTopology/DisplayModes), `LibraryTabsView.cs:90,237,375` (ConfigStore.Load, static LibraryTabManager), `OverlayWindow.LaunchFixes.cs:196-207,348,388-395` (LaunchWrapperStore/SteamLaunchConfig statics), `OverlayController.cs:553,990` (ConfigStore.Mutate), `Core/KeyboardService.cs:16` (static handler), `OverlayController.Lease.cs:46,56` (static SteamInputBlocker), `OverlayMediaPreview.cs:68,196-373` (HttpClient, temp files, WebView2) | Add the explicit violation inventory and owners from section 4; state that Avalonia's own `Dispatcher.UIThread` stays the view dispatcher (no new UI dispatch port for views). |
| C3 | "Existing route IDs, strings, layout, styling, minimum sizes, focus/keyboard/touch/controller behavior ... are preserved" (refactor-plan.md:26) | Accurate | 51 baselines under `tests/WSGM.UiTests/Baselines`, `VisualTests.cs` | Add: `OverlayPage` enum names are rail focus keys (`OverlayWindow.Workspace.cs:79` uses `view.Page.ToString()`) and tests address them (`UiFixture.Rail(window, OverlayPage)`), so no page enum may be renamed during the refactor. |
| C4 | "Overlay-test composition stays inert until the currently allowed surface operations are explicitly requested; headless tests compose explicit fake surfaces" (refactor-plan.md:85) | Partially inaccurate | Headless tests do compose fakes (`UiFixture.Overlay`). But every `OverlayController`, including `--overlay-test` (`ShellSession.cs:864-898`, `previewOnly=_overlayTestOnly`) and the Settings "Test sheet" (`SettingsWindow.axaml.cs:259`), registers a global hotkey, an SDL chord watcher and a raw-input touch sink in its constructor (`OverlayController.cs:215-227`) | Preview and overlay-test compositions get the sheet host without activation sources (OVERLAY-002, batch B3). |
| C5 | Shutdown "close admission first: Settings/Overlay intents ..." (refactor-plan.md:91; planning-corrections.md B3) | Partially accurate | `ShellSession.Shutdown.cs:73-85` disposes the overlay first, but `OverlayController.Dispose` defers the window close by 150 ms (`OverlayController.cs:327`), so `OnOverlayClosed` later raises `UiSurfaceClosed` into the device coordinator and the still-open window stays subscribed to bridges that `Shutdown.cs:678-690` disposes | The overlay needs no budget slice. Its Dispose must end claims and detach sources synchronously (OVERLAY-013). |
| C6 | "UI snapshots are detached values with revisions. A stale completion checks both operation generation and current view-model/owner identity" (refactor-plan.md:115) | Already true for the overlay; adding revisions would be new mechanism | Owner identity checks exist (`OverlayController.Apps.cs:263`, `OverlayController.cs:568,814`), sub-views carry `_navigationGeneration` (`OverlaySubView.cs:34`), device rows carry descriptor/cycle generations, and the window re-checks generations that the bridge already enforces (`OverlayWindow.DeviceControls.cs:20-28` vs `DeviceOverlayBridge.cs:523-524`) | Do not add snapshot revisions to overlay projections; delete the duplicated view-side generation checks (OVERLAY-007). |
| C7 | "Bound collections reconcile by stable keys; no replacement that loses drafts/focus" (refactor-plan.md:115) | Accurate, already implemented five times | `AppSwitcherViewModel.Reconcile`, `ServiceSubView.Reconcile` (`ServiceSubView.cs:121-188`), `ReconcilePerformanceRows`, `ReconcileSectionRail`, `RenderPins` | Consolidate rather than add (OVERLAY-007, OVERLAY-035). |
| C8 | "Native windows use HWND user data/owned callback registrations rather than replaceable `_instance`; one MessageWindow owner hands out disposable subscriptions" (refactor-plan.md:115) | Accurate and applies here, but the overlay instance is not named | `TouchSwipeMonitor.cs:65-118` keeps a static instance registry, a static shared HWND and its own message-only window | Name `TouchSwipeMonitor` in the owner list; one session-owned monitor removes the registry (OVERLAY-002/014). |
| C9 | "High-rate samples/haptic frames remain structs and latest-wins, with no per-sample allocation or logging" (refactor-plan.md:115) | Violated in this domain and not known to the plan | `OverlayWindow.Device.cs:854-872` and `GlyphInputTestMap.cs:70-92` allocate a `HashSet` per controller sample | Add OVERLAY-004 to the plan as a defect correction. |
| C10 | U05-LFB-011 "Opening the overlay dismisses a Game Mode entry splash while the entry continues uncovered" (H02) | Accurate | `OverlayController.cs:486` raises `OverlayShown` on every show, including the warning reopen | Keep the event; the decision stays in H02. |
| C11 | U05-LFB-028 "SessionModes receives configuration only through OverlayController" (H02) | Accurate | `OverlayController.cs:385`, only caller `ShellSession.Config.cs:152` | The overlay batch removes the forward once H02's reloader subscribes `SessionModes` directly (B3). |
| C12 | planning-corrections B2 picker policy and M01-32 "Existing paths remain selectable from Steam and Overlay" | Partially inaccurate | B2 specifies the toolkit picker; the overlay has its own `OverlayFilePicker.cs` (drives incl. mapped, "Enter a path" with `Path.GetFullPath`, UNC works) | State that B2's machinery (200-entry pages, four workers, ProviderBusy/ProviderCapacity, spool) does not apply to `OverlayFilePicker`; preserve its behavior and add plain temp-directory tests (OVERLAY-026). |
| C13 | pre-AM01 V01 brief ("Extract featurecontrollers/viewmodels from largewindowpartials ...; remove SettingsPluginActions/KeyboardService globals; preserve ... deferred150msclose, inputclaims and nestedBack/OSK") | Direction accurate; stale as a task | V01 no longer exists in the current graph (only `versions/pre-AM01/task-briefs.md:873`); its only prerequisite is G01 although the overlay depends on D01, D03, S02, T01, W02/I01 outputs | Replace with OVERLAY-B1..B12 (section 5) with the cross-domain dependencies named. |
| C14 | api-integration.md "Toolkit 0.2.0" consumer list | Partially inaccurate | Missing overlay consumers of toolkit types: `ServiceSubView.cs:227-275` (SteamUiCommandResult), `GameLibraryView*.cs`, `ThemesView*.cs`, `AnimationsView*.cs`, `ArtworkView.cs`, `OverlaySubView.cs` (SteamLibraryApp), `LaunchWrapperView.cs`, `OverlayWindow.LaunchFixes.cs` (SteamCurrentPage, SteamLaunchConfig), `LibraryTabsView.cs`, `CardManagerView.cs` | Add them to the group. |
| C15 | api-integration.md "Device 12 / common Plugin 4" consumer list | Partially inaccurate | Overlay consumes `CapabilityValue/Role/Unit`, `GlyphControlId`, `CanonicalControllerSample`, `PluginActionOutcome`, `PluginUiContribution`, `SettingSectionKey`, `SectionIcon` (`DeviceCapabilityControl.cs`, `GlyphInputTestMap.cs`, `CommonPluginPanel.cs`, `DeviceOverlaySectionPages.cs`) | Add Overlay and Controls (`CurveEditing`, `PhysicalGlyphService`) to the group. |
| C16 | "WDC 0.2.0 ... Settings/display editor and Overlay projections" consumers | Accurate | `RadioPanel`, `AudioPanel`, `DisplayModeView`, `WakeLockHoldersView`, `OverlayController.Power.cs` | None. |
| C17 | Requirement 12 "WSGM-owned UI internals (view models, bindings, service wiring and backdrop integration) may be refactored" | Accurate | n/a | Batches keep `OverlayWindow.axaml` byte-identical except where a page is moved into a code-behind class; visual baselines must not change. |
| C18 | "Make dangerous power tests call fake action/process ports before admitting the full WDC suite" (refactor-plan.md:125) | Accurate for WDC, incomplete for WSGM | `OverlayWindow.Power.cs:46-107` calls real `PowerActions` from click handlers inside a headless-testable window | Extend the port rule to the overlay (OVERLAY-020). |
| C19 | Batches W02_01, W02_02, T01_01, A02_01..04 | No overlay scope | `batches/*.md` touch WDC, toolkit and packages only; W02_02 keeps the WDC public API, so `Core/PowerActions` and the overlay are unaffected | None. |

## 2. Findings

Severity scale: critical, high, medium, low, nit. "PLAUSIBLE" marks a defect whose trigger depends on runtime behavior not provable from source alone.

### OVERLAY-001 (high) OverlayController is the lifecycle owner and has no lifecycle test
- Location: `OverlayController.cs:166-233, 477-672, 886-936`; partials `.Apps`, `.Gestures`, `.Keyboard`, `.Lease`, `.Power`, `.SteamExit` (1,957 lines total).
- The constructor builds `HotkeyService(MessageWindow.Create())`, `GamepadService`, `GamepadChordWatcher` and `TouchSwipeMonitor`; the sheet code calls static `SteamInputBlocker`, `KeyboardService`, `ExplorerControl`, `NativeMethods`, `WindowFinder`, `ConfigStore`, `PowerTimeouts`, `UacSettings`, `LockScreenSettings`, `AppLauncher`. The only test is a table copy of `DecideSwipe` (`QuickAccessSheetTests.cs:16-29`). Lease Hold/Drop pairing, UI capture claim pairing, the 150 ms deferred close and its cancellation on resummon, reopen-for-warning, the keyboard request flow, Open-apps return after close and lease release, and the show-failure teardown (`OverlayController.cs:655-665`) are untested. These are the rules `Overlay/AGENTS.md` calls invariants.
- Ledger: NEW (plan diagnosis covers globals in general).
- Recommendation: move activation out (OVERLAY-002), inject four narrow ports (lease, platform probes, sheet factory, session power actions) and add a controller lifecycle suite on the headless platform (B4).

### OVERLAY-002 (medium) The Settings preview controller arms the session's global activation inside the resident process
- Location: `SettingsWindow.axaml.cs:255-262` creates `new OverlayController(..., previewOnly: true)` in the session process (Desktop Settings reuses the resident window); `OverlayController.cs:215-227`; `TouchSwipeMonitor.cs:65-118, 287-346`; `Core/KeyboardService.cs:16`; `OverlayController.cs:645, 911`; `ShellSession.cs:963`.
- Effects once the Test sheet has been used while Settings stays open: (a) the second `TouchSwipeMonitor` joins the shared raw-input registration and both monitors dispatch every `WM_INPUT`, so one top swipe opens the session sheet and the preview sheet; (b) a second chord watcher fires on the same chord; (c) the preview's hotkey registration fails with `ERROR_HOTKEY_ALREADY_REGISTERED`; (d) `KeyboardService.Handler` is last-opener-wins and first-closer-nulls, so closing either sheet breaks text entry in the other; (e) the preview never gets `UseManagedPad`, so with controller management on the handheld pad cannot drive the Test sheet (PLAUSIBLE).
- Ledger: NEW.
- Recommendation: activation is session-only. Split `OverlayActivation` (hotkey, chord, swipe) from the sheet host; the preview composes only the sheet host. `TouchSwipeMonitor` becomes one instance owned by the activation (the static registry, snapshot array and last-instance teardown disappear). Keyboard requests go through the window (OVERLAY-014). The preview obtains the session's button source from the Settings composition (Settings domain).

### OVERLAY-003 (medium) The preview sheet writes real Windows idle timeouts
- Location: `OverlayController.cs:779-818` (fallback branch 789-802 locks `PowerSchemes.MutationGate` and calls `PowerTimeouts.Write` when `_displayTimeouts` is null); `OverlayWindow.PowerEditors.cs:146-147` enables editors whenever a value reads; `SettingsWindow.axaml.cs:259` passes no `DisplayTimeouts`.
- Energy plan, core preference, UAC and lock-on-wake are read-only in a preview (`PowerSchemeSelection(readOnly)`, `HybridCoreSelection(readOnly)`, `RefreshWindowsPolicies(... !_previewOnly)`, tested by `HybridCoreViewTests.APreviewOverlayReadsTheStateButRefusesToChangeIt`). Idle timeouts are the exception.
- Ledger: NEW.
- Recommendation: delete the fallback branch; the session always supplies `DisplayTimeouts`; the preview passes read-only to the timeout editors. This also removes the overlay's direct use of `PowerSchemes.MutationGate`.

### OVERLAY-004 (high) The glyph input test allocates per controller sample
- Location: `OverlayWindow.Device.cs:854-872`; `GlyphInputTestMap.cs:70-92`.
- `OnPhysicalGlyphSample` runs on the sampling thread at input rate and builds a new `HashSet<GlyphControlId>` for every sample before comparing; `_pressedGlyphControls` is replaced on the sampler thread and enumerated on the UI thread without a barrier. Violates the no-allocation rule for high-rate input.
- Ledger: NEW.
- Recommendation: `GlyphInputTestMap.Pressed` returns a `ulong` mask (one bit per `GlyphControlId`), the handler compares masks, stores with `Interlocked.Exchange` and posts only on change; tiles test bits. Add an allocation test (`GC.GetAllocatedBytesForCurrentThread` around 10,000 samples equals zero).

### OVERLAY-005 (high) Views own business workflows, persistence and native writes
- Location and owner each:
  - `OverlayWindow.LaunchFixes.cs:69-224, 226-412`: the whole launch-wrapper transaction (snapshot, `LaunchWrapperStore.RememberAsync`, `SteamLaunchConfig.ApplyAsync/ApplyCustomAsync/RestoreAsync`, `ForgetAsync`) through statics, duplicated between the wrapper and custom paths.
  - `LibraryTabsView.cs:88-100, 230-238, 362-381` and `CardManagerView.cs:44-168`: `ConfigStore.Load` and static `LibraryTabManager` saves; tab-order save is fire-and-forget with no failure shown.
  - `OverlayWindow.Power.cs:46-107`: machine power actions.
  - `DisplayModeView.cs:31-36`: display reads and writes through WDC directly.
  - `OverlayController.cs:552-553, 969-997`: `ConfigStore.Mutate` for the energy-plan reference and the pin list.
  - `OverlayMediaPreview.cs:68, 193-373`: HTTP download, temp files and WebView2 environment inside a control.
- Ledger: NEW (C2 is the plan's general target).
- Recommendation: owners in section 4: `LaunchFixService` (Shell), instance `LibraryTabManager` injected into the two views, `ISessionPowerActions`, the session display port, `QuickAccessPins` (Shell), media download in the animation/artwork owners. Views keep rendering and report intent.

### OVERLAY-006 (high) OverlayWindow is a 6,000-line partial-class aggregate
- Location: 21 partial files (`OverlayWindow.*.cs`, 6,013 lines of C#) plus `OverlayWindow.axaml` (976 lines). One class holds navigation, focus memory, the section rail, device/graphics/performance rendering, Quick Access pins, transient surfaces, launch fixes, the SD-card format flow, power editors, Windows policy toggles, header profile scope and placement/slide animation; `OverlayController.ShowOverlayCore` calls 20 `Attach*` methods (`OverlayController.cs:540-596`).
- Ledger: NEW (pre-AM01 V01 objective, step 1).
- Recommendation: plain page-controller classes over the existing named XAML elements (no new UserControls, XAML unchanged), section 4.

### OVERLAY-007 (medium) Device, Graphics and Performance duplicate the capability-row machinery
- Location: `OverlayWindow.DeviceReconciliation.cs:20-77` (`SameDeviceLayout`, a hand-maintained field list) vs `OverlayWindow.Graphics.cs:326-354` (`GraphicsLayout`, a string built on every refresh); `OverlayWindow.Sections.cs:219-283` vs `OverlayWindow.Graphics.cs:203-241` (identical readings `FlexPanel` logic); `OverlayWindow.DeviceControls.cs:12-48` and `OverlayWindow.Graphics.cs:271-317` re-check descriptor/cycle generations that `DeviceOverlayBridge.cs:523-524, 670-671` already enforce; command wrappers `RunDeviceCommandAsync`, `CommitDeviceValueAsync`, `RunGraphicsCommandAsync`, `WritePerformanceValueAsync`, `InvokePerformanceAsync`, `UseGlobalOnPerformance` (`OverlayWindow.Device.cs:714-735`, `OverlayWindow.Performance.cs:100-173`) are the same try/cancel/log shape six times.
- Ledger: NEW.
- Recommendation: one `CapabilityRowRenderer` (rows, readings grouping, value refresh, layout key from ids/generations/kinds) and one `RunCommandAsync(description, Func<CancellationToken,Task>)`. Delete the view-side generation checks: the bridge is the admission owner and already refuses stale writes.

### OVERLAY-008 (medium, PLAUSIBLE) Five combo editors commit while the dropdown is browsed; four others guard
- Location: guarded: `OverlayWindow.PowerEditors.cs:76-113`, `DeviceControlRows.cs:78-136`, `OverlayEditors.cs:89-147` (`OverlayChoice<T>`), `OverlayWindow.Header.cs:70-81`. Unguarded: `DevicePowerPresetView.cs:49-68` (AC/battery preset assignment), `ManualTdpModeView.cs:26-49`, `AudioPanel.axaml.cs:169-193` and the TwoWay `SelectedOutput`/`SelectedInput` bindings in `AudioPanel.axaml`, `DisplayModeView.cs:44-63` (resolution commits after `UpdateRates` when not open, refresh guarded).
- The guarded implementations and `OverlayInteractionTests.WakePopupDoesNotApplyIntermediateModesWhileBrowsing` exist because browsing a dropdown with the controller changes `SelectedItem`; the unguarded editors would then write a preset assignment, audio format or default endpoint per browsed item.
- Ledger: NEW.
- Recommendation: one `CommitComboBox` in Controls (commit on close or on a closed-state selection change, refresh suppression, committed-value comparison) replacing all nine.

### OVERLAY-009 (medium) The overlay changes display modes outside the session's display owner and always on the first active path
- Location: `DisplayModeView.cs:31-36` (`DisplayTopology.CaptureActive().Paths[0]`, `DisplayModes.Apply`); other writers `Core/DisplayProfiles.cs:50-53, 250`.
- Writes are not serialized with display profile and refresh-pairing writes (plan: "display mutations serialize through one injected service per host"), and on a multi-monitor desktop `paths[0]` is not necessarily the display the sheet covers.
- Ledger: NEW (relates to U01-029/W02 display ownership).
- Recommendation: inject the session display-mode port from W02/I01. Keep the `paths[0]` target in the refactor (behavior identical); whether to target the sheet's display is open question Q1.

### OVERLAY-010 (medium, PLAUSIBLE) An uncertain launch-fix write deletes the only restoration record
- Location: `OverlayWindow.LaunchFixes.cs:201-209` and `391-396` call `LaunchWrapperStore.ForgetAsync` on any `!result.Ok` when no prior snapshot existed; `Core/SteamLaunchConfig.cs:273-288` folds "unreachable", "rejected" and a dispatched write whose answer was lost into `Ok=false`.
- If Steam applied the change but the answer was lost, a shortcut's original Target is overwritten and WSGM forgets it, so Restore cannot recover the program.
- Ledger: NEW (depends on T01's NeverSent/DispatchedUnknown outcomes).
- Recommendation: the moved `LaunchFixService` forgets the snapshot only for NeverSent/Refused outcomes and keeps it for DispatchedUnknown.

### OVERLAY-011 (medium) Session policy lives in the overlay controller
- Location: `OverlayController.SteamExit.cs:12-66` (auto-relaunch of Steam after exit); `OverlayController.cs:383-385, 420-421` (`AccentPalette.Apply`, `_modes.ApplyConfig`, `Log.SetVerbosity` on config reload).
- Ledger: U05-LFB-028 for the modes forward; relaunch and the process-global reapply are NEW.
- Recommendation: relaunch moves to the Steam lifecycle owner (`SessionModes`), the three reload consumers subscribe to H02's reloader; the overlay keeps only its own reload work.

### OVERLAY-012 (medium) Quick Access pins mutate the shared live config and persist out of order
- Location: `OverlayController.cs:969-997` sets `_config.QuickAccessPins` on the session's live `AppConfig` and persists each toggle in its own `Task.Run(ConfigStore.Mutate)`; two quick toggles can commit in either order, after which the reload hands back the older list and the sheet visibly reverts. A second, independent pin system (`PinnedPluginWidgets.cs`, plugin widget preferences) polls every second.
- Ledger: NEW.
- Recommendation: `QuickAccessPins` owner in Shell with one serialized write queue and a `Changed` event; the overlay reports intent; `PinnedPluginWidgets` subscribes to `CommonPluginOverlaySource`'s pin change instead of polling.

### OVERLAY-013 (medium, PLAUSIBLE) Overlay dispose at shutdown leaves late callbacks into disposed owners
- Location: `OverlayController.cs:264-328` (stale comment at 276-279 says only the Settings preview calls Dispose); `ShellSession.Shutdown.cs:73-85, 678-690`.
- After Dispose the window stays open for 150 ms, subscribed to device/performance/graphics sources that shutdown disposes immediately, and the deferred `OnOverlayClosed` raises `UiSurfaceClosed` into the device coordinator and stops a disposed `GamepadService`.
- Ledger: NEW (relates to plan B3 admission close).
- Recommendation: Dispose runs the teardown synchronously (release UI claims, detach sources, close surfaces, release lease) and only the HWND destruction is deferred; the deferred handler does nothing else after Dispose.

### OVERLAY-014 (medium) Process-global mutable state in the overlay
- Location: `OverlayWindow.axaml.cs:33` (`SharedSession` shared by the session and preview sheets), `TouchSwipeMonitor.cs:65-78`, `Core/KeyboardService.cs:16`, `OverlayController.Lease.cs:46-56`, `OverlaySubView.cs:143-148` and `ServiceSubView.cs:30-49` (reach the window through `TopLevel.GetTopLevel(this) as OverlayWindow`), `CommonPluginPanel.cs:104` (same cast).
- Ledger: NEW (plan diagnosis "mutable process globals").
- Recommendation: `SessionState` owned by each controller; `IOverlaySurfaceHost` (text entry, local path pick, surface state) handed to views at attach time; `KeyboardService` deleted (its only users are overlay files). Immutable bounded caches (`GlyphIcon`, `OverlayPreviewImage`) stay.

### OVERLAY-015 (medium, PLAUSIBLE) Render methods fire network fetches
- Location: `ThemesView.Tools.cs:44-47` (`browse.Page == 0 && !Loading && Error is null`), `AnimationsView.Tools.cs:53-56` (`browse.Total == 0 ...`).
- Each publication re-renders; if a completed browse leaves the predicate true (an empty repository, a cancelled query that publishes Page 0 without an error) the view loops fetches.
- Ledger: NEW.
- Recommendation: fetch once on tab entry (`SelectTab`, `Open`), never from a render thunk.

### OVERLAY-016 (medium) "Select visible" toggles one entry at a time from a stale snapshot
- Location: `GameLibraryView.Tools.cs:150-154, 198-216`.
- N round trips and N publications; an entry changed meanwhile on the Steam page is toggled back the wrong way.
- Ledger: NEW.
- Recommendation: `SetSelectedAsync(ids, selected)` on the library facade (G01), idempotent.

### OVERLAY-017 (medium) Overlay views consume the Steam page's JSON wire shapes and stringly states
- Location: `GameLibraryView.cs:262-263` (probes `"acknowledge"` in a `JsonElement` payload), `GameLibraryView.Tools.cs:328-330` (deserializes `GameLibraryMatchesAnswer`), `ThemesView.Tools.cs:111-122` (`SetSettingAsync("enabled", JsonSerializer.SerializeToElement(...))`), string phases and kinds (`"scanning"`, `"applying"`, `"Add"`, `"outdated"`, `"manage"`, `"logo"`, `"folder"`) in `GameLibraryView*.cs`, `GameLibraryRows.cs`, `ThemesView*.cs`, `ArtworkView.cs`.
- Ledger: NEW.
- Recommendation: typed C# command results and state enums on the backends (S02, G01); the Steam page projects to JSON at its own boundary. The overlay changes only consumers.

### OVERLAY-018 (medium) The SteamGridDB filter vocabulary exists three times and has drifted
- Location: `ArtworkView.cs:345-413`, `Shell/SteamArtworkBrowserSource.cs:1195-1210` (grid lacks 512x512 and 1024x1024), `Core/SteamUiAssets/Source/artwork-browser.ts:20-30`.
- Ledger: NEW.
- Recommendation: the source publishes offered styles/dimensions/formats in its state; both surfaces render that.

### OVERLAY-019 (medium) Two Windows energy-plan selection workflows
- Location: `PowerSchemeSelection.cs:57-131` (overlay) and `Shell/NativeQamPowerProfileService.cs:40-90` (Steam QAM) both lock `PowerSchemes.MutationGate`, call `Select` and persist `LastSelectedPowerSchemeId`; the overlay's persist lambda is defined in `OverlayController.cs:552-553`.
- Ledger: NEW.
- Recommendation: one session-owned Windows power-profile owner (D03) used by both surfaces; `PowerSchemeSelection` keeps only the UI-thread projection, `readOnly` and busy state.

### OVERLAY-020 (medium) Machine power actions run from view click handlers
- Location: `OverlayWindow.Power.cs:46-107`.
- A headless UI test that clicks Standby or Shut down would act on the test machine; the session also never learns that the user asked for a session end.
- Ledger: NEW (plan line 125 covers WDC only).
- Recommendation: `ISessionPowerActions` injected through the controller; tests use a recording fake. Behavior unchanged.

### OVERLAY-021 (medium) A dispatched plugin action is shown as a failure
- Location: `CommonPluginPanel.cs:500-501` shows "Change was not confirmed" for `PluginActionOutcome.Dispatched`; only `AppliedVerified` reads "Applied".
- Contradicts "never gate success on readback; publish the written value as observed"; IR emission is acknowledgement-only, so every successful IR action reads as unconfirmed.
- Ledger: NEW.
- Recommendation: Dispatched and AppliedVerified read "Applied"; Unconfirmed reads "Not confirmed"; Rejected shows its detail.

### OVERLAY-022 (medium) Back and destination policy is split between the pure model and focus-dependent window branches, held together by re-entrancy guards
- Location: `OverlayWindow.Navigation.cs:464-557` (`TryCancelSubView`, `CancelOpenPage`), `193-263`, `604-647` (`_showingDestination` guard against `ShowDestination -> RefreshDevicePanel -> ConfigureTabs -> RebuildDestinationTabs -> ShowDestination`), `OverlayWindow.Workspace.cs:24, 195-238` (`_selectingSection`), `OverlayWindow.Device.cs:235`.
- Ledger: NEW.
- Recommendation: `OverlayNavigation.Back(BackContext)` returns one action (close surface, nested back, focus rail, return home, pop page, close sheet) and becomes unit-testable; destination visibility is updated only from source change handlers, so `RefreshDevicePanel` stops calling `ConfigureTabs` and both guards are deleted.

### OVERLAY-023 (low) Arbitrary navigation depth cap
- Location: `OverlayNavigation.cs:134, 233`; `OverlayWindow.Navigation.cs:734`.
- The page graph bounds depth at four; the cap of eight and the loop bound add a refusal path that cannot be reached legitimately.
- Ledger: NEW.
- Recommendation: delete `MaximumDepth`; the unwind loop runs while a sub-view is open and stops if a pop makes no progress.

### OVERLAY-024 (low) Arbitrary text-entry caps
- Location: `LibraryTabsView.cs:252` (40), `CardManagerView.cs:108` (40), `GameLibraryView.Tools.cs:57` (32 for extensions), 64/128 for searches and names (`ThemesView.Tools.cs:34,93`, `AnimationsView.Tools.cs:46`, `SoundsView.cs:170`, `ArtworkView.cs:83,332`, `GameLibraryView.Tools.cs:117`), 256 (`ThemesView.Tools.cs:167`, `OverlayWindow.Surfaces.cs:78`), 1024 (`OverlayWindow.Surfaces.cs:111`), 2048 (`LaunchWrapperView.cs:83`), 4096 fallbacks (`DeviceControlRows.cs:164`, `CommonPluginPanel.cs:543`), `ApplicationProfilesView.cs:25` (80).
- Ledger: NEW.
- Recommendation: unbounded (0) except contract-backed values: a capability's `MaximumLength`, a plugin field's declared length, the WPA passphrase limit for Wi-Fi credentials, the target filesystem's volume label length, the 7-character hex colour format.

### OVERLAY-025 (low) Four paging mechanisms with four chunk sizes
- Location: `LibraryTabsView.cs:729-771` and `LaunchWrapperView.cs:136-193` (200-row pages), `OverlayFilePicker.cs:21, 123, 148-155` (100, "Show more"), `GameLibraryView.Tools.cs:161-190` and `ArtworkView.cs:103-117` (48, "Load more"), `SoundsView.cs:191` (service page size 24 hard-coded); `CardManagerView.cs:196-201` renders every game unpaged.
- Ledger: NEW. Chunking is allowed; nothing is truncated.
- Recommendation: one `PagedRows` helper that appends a chunk and focuses its first new row; Sounds reads the page size from state.

### OVERLAY-026 (low) Picker navigation loses controller position
- Location: `OverlayFilePicker.cs:41-53, 148-155`; `OverlayWindow.Surfaces.cs:313` captures the preferred focus once.
- Each folder change or "Show more" replaces `Content`; the captured focus target is detached, so focus recovers to the top of the list.
- Ledger: NEW. Not a B2 case (C12): keep the current simple picker.
- Recommendation: after a page load focus the first new entry; after navigation focus the first entry; tests over temporary directories (local, 450 entries, inaccessible folder, Enter-a-path UNC string normalization).

### OVERLAY-027 (low) Native folder picker without navigation suspension
- Location: `OverlayWindow.Storage.cs:140-175` (Add Steam Library) has no `SystemDialogActive` pairing, unlike `OverlayWindow.LaunchFixes.cs:76-94`; `Overlay/AGENTS.md` says file/folder selection stays inside the overlay.
- Ledger: NEW.
- Recommendation: add the pairing now (defect fix, no UI change); migrating both native pickers to the in-overlay picker is open question Q2.

### OVERLAY-028 (low) Polling where the owner can notify
- Location: `CommonPluginPanel.cs:54` (500 ms per panel, so per pinned widget), `ManualTdpModeView.cs:52` (500 ms), `PinnedPluginWidgets.cs:28` (1 s over an in-memory array), `OverlayWindow.Sources.cs:16-29` (preset refresh 1 s).
- Ledger: NEW.
- Recommendation: subscribe to the owners' change events (common plugin source, device coordinator, preset service). Keep polling only for Windows state with no notification (power requests, window list, display modes).

### OVERLAY-029 (low) Async-void paths that can throw into the dispatcher
- Location: `ProfileOverrideMarker.cs:59, 83-108` (no catch; `OverlayWindow.Sources.cs:69-76` passes an unwrapped `ClearGameOverrideAsync`), `AudioPanel.axaml.cs:169-193`, `OverlayController.cs:821-830` (UAC/lock change lambdas).
- Ledger: NEW.
- Recommendation: the marker contains and logs; handlers wrap their awaits.

### OVERLAY-030 (low) The window reaches into DeviceCoordinator profile internals and a Steam-surface helper
- Location: `OverlayWindow.Sources.cs:52-77` (`coordinator.Profiles.Current.Layers`, `NativeQamUi.OverrideId`, `coordinator.Profiles.ClearGameOverrideAsync`); `OverlaySources.cs:224` exposes the whole `DeviceCoordinator` as `ManualTdp`.
- Ledger: NEW.
- Recommendation: `IManualTdpModeSource` (read, set, override id, use global, changed) supplied by D01/D03.

### OVERLAY-031 (low) The application-profile editor is the only overlay editor built on raw TextBoxes
- Location: `ApplicationProfilesView.cs:25-31, 164`. `GamepadNavigation` skips TextBoxes, so name and process list are not editable by controller, and `_name.Focus()` can raise the Windows touch keyboard.
- Ledger: NEW.
- Recommendation: no change inside the refactor (visual change); Q3.

### OVERLAY-032 (low) Generic descriptor row hard-codes the frame-limit format
- Location: `DescriptorControlView.cs:37-43` formats every ranged descriptor as "Off" or "N FPS".
- Ledger: NEW.
- Recommendation: `DescriptorRange` carries its unit/off label from `PerformanceOverlayBridge`.

### OVERLAY-033 (low) Duplicate run-button block
- Location: `DeviceCapabilityControl.cs:65-98` and `DescriptorControlView.cs:44-76` (identical `_invoking`, disable, focus-restore code).
- Ledger: NEW.
- Recommendation: one `InvokeButtonRow` used by both.

### OVERLAY-034 (low) Identity carried in strings on `Control.Tag`
- Location: tag rewriting `OverlayWindow.Sections.cs:229-241`; `Replace("device.section.", "section.device.")` at `OverlayWindow.Sections.cs:212-213` and `OverlayWindow.Device.cs:468-470`; index placeholder `new Control { IsVisible = false }` kept so reconciliation offsets stay valid (`OverlayWindow.Sections.cs:63-65`, `OverlayWindow.Performance.cs:91`); `OnWorkspacePolicyChanged` reacts to any property name starting with "Show" (`OverlayWindow.Workspace.cs:31-45`).
- Ledger: NEW.
- Recommendation: one key helper producing both key forms from a typed `SectionKey`; reconcile by key, not index; react to an explicit property list. Keys keep their current string values (focus memory and tests depend on them).

### OVERLAY-035 (low) Five hand-written keyed reconcilers
- Location: `ServiceSubView.cs:121-188`, `OverlayWindow.Performance.cs:61-98`, `OverlayWindow.Workspace.cs:115-160`, `OverlayWindow.Pins.cs:102-229`, `AppSwitcherViewModel.cs` reconcile methods.
- Ledger: NEW.
- Recommendation: keep `AppSwitcherViewModel` (bound collections) and `ServiceSubView.Reconcile`; move the three window reconcilers onto one `ReconcileChildren(panel, keyedControls)` helper in the page controllers.

### OVERLAY-036 (low) PhysicalGlyphService caches more than it can use and sits in the wrong layer
- Location: `PhysicalGlyphService.cs:53-160, 247-261, 375-380`. The cache key includes theme and scale bucket, but `BuildPlan` uses neither, so one plan is stored up to 45 times; the byte budget counts asset bytes the imported profile already retains. The service is a non-visual resolver in Controls consumed by `Shell/DeviceOverlayBridge.cs:322,347`.
- Ledger: NEW.
- Recommendation: a dictionary keyed by (profile id, revision, control), cleared on catalog change; drop the LRU and byte accounting and the cache-size constructor parameters; move the service to Shell next to its only consumer; `PhysicalGlyphRenderPlan` and `PhysicalGlyphImage` stay in Controls.

### OVERLAY-037 (low) Preview images re-run visibility checks on every tree-wide layout pass
- Location: `OverlayPreviewImage.cs:39-91` subscribes `LayoutUpdated` (fires for the whole tree) per unloaded image.
- Ledger: NEW.
- Recommendation: `EffectiveViewportChanged`, which reports this control's viewport only.

### OVERLAY-038 (low) Movie preview temp files and a second downloader
- Location: `OverlayMediaPreview.cs:68, 196-201, 330-373` (per-preview `%TEMP%\WSGM-media-<guid>` never swept after a crash; its own static `HttpClient` and copy loop beside `ArtworkDownload`/`BoundedHttp`).
- Ledger: NEW.
- Recommendation: the animation/artwork owners provide the bytes through their existing bounded downloaders; temp root under WSGM's cache folder, cleared at start.

### OVERLAY-039 (low) Overlay depends on Settings for a label
- Location: `AudioPanel.axaml.cs:11, 112` (`AudioProfileEditor.SpatialName`).
- Ledger: NEW.
- Recommendation: move the label to Core.

### OVERLAY-040 (low) View state stored on shared session models
- Location: `RadioPanel.axaml.cs:160-163, 218-221` and `EjectPanel.axaml.cs:40-43` set `Expanded` on `RadioManager`/`RemovableDriveManager` entries shared with Steam surfaces; static `RadioManager.RespondToPairing` (`RadioPanel.axaml.cs:61, 393-396, 413`).
- Ledger: NEW.
- Recommendation: the panel keeps the expanded key; pairing responses through the manager instance (W01 consumer).

### OVERLAY-041 (low) Pending slider/curve edits are dropped when the sheet closes quickly
- Location: `DeviceSliderRow.cs:154, 216-228`, `DeviceCurveRow.cs:97, 186-198` flush on detach, but `OverlayWindow.axaml.cs:374` sets `_closed` first and `OverlayWindow.DeviceControls.cs:15` refuses writes after close.
- A drag followed by B within the 250/400 ms settle window writes nothing (PLAUSIBLE).
- Ledger: NEW.
- Recommendation: the window flushes pending editors when dismissal starts (before the deferred close), once.

### OVERLAY-042 (low) DevicePowerPresetSelection disposal mechanism
- Location: `DevicePowerPresetSelection.cs:23-39, 74-85, 111-118` (three conditional CTS dispose sites).
- Ledger: NEW.
- Recommendation: cancel only; a CancellationTokenSource without timers needs no Dispose.

### OVERLAY-043 (low) Synchronous native reads on the UI thread at every open
- Location: `OverlayController.cs:508, 522, 559, 699` and `OverlayController.Power.cs:18-52` (`PowerTimeouts.ReadAll`, `UacSettings.Read`, `LockScreenSettings`, two process-table scans per open).
- Ledger: NEW.
- Recommendation: one off-thread read into the view model after show, like the switcher snapshot; values render a moment later with the same layout. Keep if the maintainer prefers zero visible change (Q4).

### OVERLAY-044 (medium) Tests copy production wiring and include getter and grab-bag tests
- Location: `ControllerNavigationTests.cs:56-60, 139-142, 158-161, 181-184` and `CommonPluginPanelTests.cs:203` construct `GamepadNavigation` with their own lambda sets, which differ from `OverlayController.cs:606-642`; `UiFixture.cs:170-176` duplicates `DockToTopEdge`'s scale transform; `QuickAccessSheetTests.cs` mixes the `DecideSwipe` table, Settings snapshot getters, a virtual-key constant echo, `DisplayScale` tests and `WindowEntryPreservesTheActivationTargetAndPresentationState` (pure getters); `EveryDestinationHasAUserFacingLabel`.
- Ledger: NEW (plan line 38: copied predicates do not establish behavior).
- Recommendation: `OverlayInput.Create(window, buttonSource, back)` used by the controller and the tests; move `DisplayScale` and settings snapshot tests to their domains; delete getter-only tests; replace the swipe table with an activation test that injects a swipe and observes the requested surface.

### OVERLAY-045 (low) Missing tests for risky overlay mechanisms
- Location: no tests for `TouchSwipeMonitor` arm/disarm and teardown, `OverlayFilePicker`, launch-fix workflow, pin persistence ordering, controller Dispose ordering, `OverlayMediaPreview` lifetime.
- Ledger: NEW.
- Recommendation: added inside the batches that touch each area (section 5).

### OVERLAY-046 (nit) Private StyledProperties behind public wrappers
- Location: `TabStrip.cs:79-84`, `OnScreenKeyboard.cs:14`, `CurveEditor.cs` (`SelectedIndex`, `MarkerInput`, `RisingOutput`). Controls/AGENTS.md calls StyledProperty surfaces the public contract.
- Recommendation: make them public; no visual change.

### OVERLAY-047 (nit) Curve point limit declared twice
- Location: `CurveEditing.cs:53-54` repeats the router's 64-point limit.
- Recommendation: one SDK constant referenced by router and editor (contract validation, not an arbitrary cap).

### OVERLAY-048 (nit) Service sub-view waits are never cancelled
- Location: `ServiceSubView.cs:227-275` passes `CancellationToken.None`; late completions are already filtered by generation.
- Recommendation: pass a view-lifetime token cancelled on Leave for the wait only; the service operation is not cancelled.

### OVERLAY-049 (nit) Whole-tree walks per render
- Location: `OverlayWindow.Pins.cs:238-254` (`UpdatePinnedIndicators` walks every logical descendant after each coalesced device refresh); `OverlayWindow.DeviceReconciliation.cs:79-129` nested capability loops.
- Recommendation: page controllers track their own pin headers and rows.

### OVERLAY-050 (nit) Small debts
- `StatusPanel` is a focus-scroll and DPI helper, not a panel (`StatusPanel.cs`); `FluentExtensions.Also` has one use (`CardManagerView.cs:47`); duplicate `<summary>` blocks (`ThemesView.cs:20-22`, `AnimationsView.cs:19-21`, `DeviceOverlaySectionPages.cs:175-179`); `AnimationsView.RenderDetail` is a pass-through; `LaunchWrapperView.cs:144-146` comment contradicts the caption; `ThemesView._service` and `_browser` are the same object; `OverlayViewModel.PowerTimeoutMinimums` never raises a change although `OverlayWindow.PowerEditors.cs:68-70` listens for it; `ConfirmingCloseLauncher` raises unconditionally; stale Dispose comment (`OverlayController.cs:276-279`); `SteamStorageFormat`/`SteamGameLibrary` pages belong to the System destination (keep the names, C3); `private protected` fields named with underscores in `OverlaySubView`.

## 3. Plan refinements

Additions:
1. Name an overlay objective with the batches of section 5, replacing the stale V01 record (C13), and list its cross-domain dependencies: H02 (reloader subscribers, shutdown order), D01/D03 (manual TDP source, Windows power owner), S02/G01 (typed backend results, `SetSelectedAsync`, offered artwork vocabulary), T01 (write outcomes for launch fixes), W01/W02/I01 (radio/display ports).
2. Add OVERLAY-004 (per-sample allocation) and OVERLAY-021 (dispatched shown as failure) to the explicit defect-correction list; both are rule violations, not refactor choices.
3. Add the overlay to the test-safety rule of refactor-plan.md:125 (OVERLAY-020) and require a controller lifecycle suite (OVERLAY-001) as the overlay's "critical lifecycle state table".
4. Add the overlay and Controls consumers to the Toolkit 0.2.0 and Device 12/Plugin 4 groups (C14, C15).
5. Freeze `OverlayPage`/`OverlayDestination` enum names and every Tag/focus key string (C3); require visual baselines unchanged after every overlay batch (`eng/update-ui-baselines.ps1` is not run).
6. Preview and overlay-test compositions: sheet host only, no activation sources (C4).

Changes:
1. Overlay shutdown is a synchronous admission close; it needs no slice of the B3 budget (C5).
2. B2's picker machinery applies only to the toolkit picker; `OverlayFilePicker` keeps its simple worker read and gets focus and paging fixes (C12).

Removals and mechanism that would over-engineer this domain:
1. "UI snapshots are detached values with revisions" must not add revision counters to overlay projections. Existing descriptor/cycle generations, owner identity checks and sub-view generations already cover stale completion; the simplification is to delete the duplicated view-side checks (C6, OVERLAY-007).
2. No UI dispatcher port for views and no MVVM framework. Headless tests run on Avalonia's real dispatcher; the only ports the overlay needs are the four in section 4.
3. No ports that mirror Avalonia (no `IWindow`, no `IFocusManager`); the sheet factory returns a real `OverlayWindow`.
4. Simplifications that remove existing mechanism: `TouchSwipeMonitor` static registry, `KeyboardService` static, `SharedSession` static, `MaximumDepth`, `_showingDestination`/`_selectingSection` guards, view-side generation re-checks, `SameDeviceLayout` field list, `PhysicalGlyphService` LRU and byte budget, `DevicePowerPresetSelection` disposal branches, render-triggered fetches, five combo implementations folded into one, the timeout-write fallback in the controller.

## 4. Target design

### Owners

| Owner | Kind / placement | Responsibilities | Inputs |
| --- | --- | --- | --- |
| `OverlayActivation` | new, `Overlay/OverlayActivation.cs`, session-only | hotkey, controller chord, the one `TouchSwipeMonitor`, swipe routing (`SwipeAction`, `DecideSwipe`), arm/disarm while a sheet is up | `HotkeyService`, `GamepadService` (raw SDL), gesture/hotkey/chord config; raises `QuickAccessRequested`, `OpenAppsRequested`; sends Steam shortcuts |
| `OverlayController` | existing, slimmed | sheet lifetime: open/reactivate/deferred close, Steam Input lease, UI capture claims, focus restore, warning reopen, keyboard request, Open apps return, power menu | `ISteamInputLease`, `IOverlayPlatform`, `IOverlaySheetFactory`, `ISessionPowerActions`, `QuickAccessPins`, `OverlaySources`, session managers; `OverlayHostOptions.Preview` |
| `OverlayWindow` | existing | XAML, construction, placement/slide, backdrop, page-controller composition, `IOverlaySurfaceHost` implementation | page controllers |
| `OverlayNavigationController` | new class over named XAML elements | destination strip, section rail, nested pages, focus memory, back actions | `OverlayNavigation` (pure, gains `Back(BackContext)`) |
| `OverlaySurfaceHost` | new | transient surfaces (utility, keyboard, power menu), local path pick, text entry | implements `IOverlaySurfaceHost` |
| `DevicePage`, `GraphicsPage`, `PerformanceRows`, `QuickAccessPinsPage`, `PowerPage`, `StorageFormatPage`, `SteamLaunchFixesPage` | new page controllers | rendering and intent for one destination area | sources from `OverlaySources` |
| `CapabilityRowRenderer` | new | capability rows for device, graphics and pins, readings grouping, value refresh, layout key | `DeviceOverlayCapability` |
| `CommitComboBox`, `InvokeButtonRow`, `PagedRows` | new, Controls | shared editors | none |
| `QuickAccessPins` | new, Shell | pin list, serialized persistence, `Changed` | `ConfigStore` (instance after F01) |
| `LaunchFixService` | new, Shell | launch-wrapper transaction and restore record | toolkit client (T01/T02), `LaunchWrapperStore` |
| Windows power-profile owner | D03 | one select/persist path for overlay and QAM | n/a |
| `PhysicalGlyphPlans` | moved from `Controls/PhysicalGlyphService.cs` to Shell | plan resolution with a simple cache | `PhysicalGlyphCatalog` |

Ports (all narrow, all with one production implementation):
- `ISteamInputLease { void Hold(string owner); Task Drop(string owner, string reason); event Action<string> RecoveryWarning; }` over `SteamInputBlocker`.
- `IOverlayPlatform { nint Foreground(); bool ExplorerRunning(); PixelPoint? WindowCenter(nint); void BringToForeground(nint); bool StartTaskManager(); }`.
- `IOverlaySheetFactory { OverlayWindow Create(OverlayViewModel, AppSwitcherViewModel, SystemStatus, double uiScale, PixelPoint? point, SessionState state); }`.
- `ISessionPowerActions { Standby, Hibernate, Restart, Shutdown, SignOut }`.
- `IOverlaySurfaceHost { bool RequestText(...); Task<string?> PickLocalPathAsync(...); bool HasActiveSurface; event Action SurfaceClosed; }`.
- `IManualTdpModeSource` (from D01/D03).

### Old symbol to new owner, dissolved and slimmed files

| Old file | Symbol(s) | New owner |
| --- | --- | --- |
| `OverlayController.Gestures.cs` | `SwipeAction`, `DecideSwipe`, `ApplyGestures`, `OnSwipeTriggered`, `Hide/Show/DisposeTouchEdges`, `_touchSwipes` | `OverlayActivation` (file deleted) |
| `OverlayController.cs` | `_hotkey`, `_chordWatcher`, chord start/stop in ctor and `ApplyConfig`, hotkey apply | `OverlayActivation` |
| `OverlayController.cs` | `ApplyConfig`: accent reapply, `_modes.ApplyConfig`, `Log.SetVerbosity` | H02 reloader subscribers (ShellSession) |
| `OverlayController.cs` | `OnPinToggleRequested` persistence, `_config.QuickAccessPins` mutation | `QuickAccessPins`; controller forwards intent |
| `OverlayController.cs` | `PowerTimeoutSelected` fallback branch | deleted; session `DisplayTimeouts` is the only writer |
| `OverlayController.cs` | `PowerSchemeSelection` persist lambda | D03 Windows power-profile owner |
| `OverlayController.cs` | `ShowOverlayCore` sheet construction and 20 `Attach*` calls | `IOverlaySheetFactory` + `OverlayWindow.Attach(OverlaySources)` |
| `OverlayController.cs` | `GamepadNavigation` construction | `OverlayInput.Create` (shared with tests) |
| `OverlayController.SteamExit.cs` | `OnSteamExited`, `RelaunchSteamAfterExit`, `DecideSteamExitReaction`, `SteamRelaunchDelay`, `_pendingSteamRelaunch` | `SessionModes` (Steam lifecycle owner) (file deleted) |
| `OverlayController.Keyboard.cs` | `RequestOnScreenKeyboardAsync`, `OpenKeyboard`, `CloseKeyboardNow` | `OverlayController` (sheet lifetime) and `OverlaySurfaceHost` (file deleted) |
| `OverlayController.Lease.cs` | `AcquireSteamInputLease`, `ReleaseSteamInputLease`, `Claim/ReleaseUiSurface`, `UseManagedPad` | `OverlayController` via `ISteamInputLease`; `UseManagedPad` on `OverlayActivation`'s and the sheet's button source |
| `OverlayController.Power.cs` | `RefreshPowerTimeouts`, `RefreshWindowsPolicies`, keep-awake mirror, wake-lock poll, `ShowPowerMenu`, `TogglePowerMenu` | `PowerPage` (reads, off-thread) and `OverlayController` (menu entry points) |
| `OverlayController.Apps.cs` | switcher refresh, icon resolve, tray forwarding, `PickWindow`, task manager | `OpenAppsStrip` class owned by the controller; platform calls through `IOverlayPlatform` |
| `Core/KeyboardService.cs` | `Handler`, `Request` | `IOverlaySurfaceHost.RequestText` (file deleted) |
| `OverlayWindow.axaml.cs` | `SharedSession` static | `SessionState` owned per controller |
| `OverlayWindow.Navigation.cs`, `.Workspace.cs`, `.Rail.cs`, `.Header.cs` | destinations, rail, nested pages, back, focus memory, header profile scope, Open apps focus | `OverlayNavigationController`; back decision in `OverlayNavigation.Back`; header profile in `PerformanceRows` |
| `OverlayWindow.Surfaces.cs` | surface stack, power menu projection, `PickLocalPathAsync`, keyboard surface | `OverlaySurfaceHost` |
| `OverlayWindow.Device.cs`, `.DeviceControls.cs`, `.DeviceOverview.cs`, `.DeviceReconciliation.cs` | device render, sections, glyph preview, input test, host rows, value refresh, layout identity | `DevicePage` + `CapabilityRowRenderer`; `DevicePinSections` projection moves to `DeviceOverlaySectionPages` |
| `OverlayWindow.Graphics.cs` | graphics render, pins, write | `GraphicsPage` + `CapabilityRowRenderer` |
| `OverlayWindow.Performance.cs` | performance rows, placement | `PerformanceRows` |
| `OverlayWindow.Pins.cs`, `.Sections.cs` | pin mirrors, pinned sections, indicators, folds | `QuickAccessPinsPage`; fold helper in `OverlayNavigationController` (folds are session state) |
| `OverlayWindow.Power.cs`, `.PowerEditors.cs`, `.WindowsPolicies.cs` | power actions, confirms, keep-awake and timeout editors, UAC/lock toggles | `PowerPage`; actions through `ISessionPowerActions` |
| `OverlayWindow.Storage.cs` | format flow, add library | `StorageFormatPage` |
| `OverlayWindow.LaunchFixes.cs` | launch fix and custom action transactions | `LaunchFixService` (Shell) + `SteamLaunchFixesPage` (labels, picker hand-off) |
| `OverlayWindow.Sources.cs` | `Attach*` for presets, manual TDP, brightness, schemes, cores, tools, plugins, device, performance | the page controller that renders each |
| `StatusPanel.cs` | `WirePanelBehaviour`, `CurrentWindowScale` | `OverlaySurfaceHost` and `OverlayWindow.Placement` (file deleted) |
| `FluentExtensions.cs` | `Also` | inlined (file deleted) |
| `Controls/PhysicalGlyphService.cs` | `PhysicalGlyphService`, cache, `ToAvaloniaPathData` | `Shell/PhysicalGlyphPlans.cs`; enums and `PhysicalGlyphRenderPlan` stay in Controls |
| `GlyphInputTestMap.cs` | `Pressed` (HashSet) | same file, `ulong` mask |

Files with reviewed-no-change status (behavior kept, only consumers adjust): `AppSwitcherViewModel.cs`, `OverlayViewModel.cs` (nits only), `OverlayNavigation.cs` (gains `Back`), `DeviceOverlaySectionPages.cs`, `GraphicsSectionPins.cs`, `GameLibraryRows.cs`, `DescriptorRow.cs`, `VisiblePoll.cs` (fewer users), `GlyphIcon.cs`, `KeyboardPanel.*`, `EjectPanel.*`, `SectionPinHeader.cs`, `HybridCoreSelection.cs`, `HybridCoreView.cs`, `PowerSchemeView.cs`, `DisplayBrightnessView.cs`, `DeviceColorView.cs`, `WakeLockHoldersView.cs`, `Controls/ActionButton.cs`, `CollapsibleSection.cs`, `HexBrushConverter.cs`, `Icons.cs`, `RadioIcon.cs`, `DeviceColorSpectrum.cs`, `CurveEditing.cs` (constant source only), `CurveEditor.cs`, `OnScreenKeyboard.cs` and `TabStrip.cs` (property visibility only), `PhysicalGlyphImage.cs`, all `tests/WSGM.UiTests` fakes and fixtures (adjust to new constructors), all PNG baselines (must stay identical).

### Public/internal API changes and every consumer

| Change | Consumers that must change |
| --- | --- |
| `OverlayController` constructor takes ports and `OverlayHostOptions`; loses hotkey/chord/gesture/Steam-exit members; `SwipeAction`/`DecideSwipe` move to `OverlayActivation` | `ShellSession.cs:864-1000`, `ShellSession.Config.cs:152`, `ShellSession.Modes.cs:67,730`, `ShellSession.Shutdown.cs:76,680`, `SettingsWindow.axaml.cs:255-262`, `tests/WSGM.Tests/Overlay/QuickAccessSheetTests.cs` |
| `KeyboardService` deleted | `CommonPluginPanel.cs:543`, `DeviceControlRows.cs:164`, `OverlaySubView.cs:281`, `OverlayWindow.Storage.cs:33`, `OverlayController.cs:645,911` |
| `OverlayWindow` public constructor gains `SessionState` (internal constructor already exists) | `OverlayController`, `UiFixture.cs:153-180` |
| `OverlaySources.ManualTdp` becomes `IManualTdpModeSource` | `ShellSession.cs:870-890`, `OverlayWindow.Sources.cs:52-77` |
| `GlyphInputTestMap.Pressed` returns `ulong` | `OverlayWindow.Device.cs`, `tests/WSGM.Tests/Overlay/GlyphInputTestMapTests.cs` |
| `PhysicalGlyphService` renamed/moved, constructor without cache sizes | `Shell/DeviceOverlayBridge.cs:322,347,776`, `tests/WSGM.Tests/Controls/PhysicalGlyphServiceTests.cs` |
| Backend typed results (S02/G01 owned) | `ServiceSubView.cs`, `GameLibraryView*.cs`, `ThemesView*.cs`, `AnimationsView*.cs`, `ArtworkView.cs`, `SoundsView.cs`, `tests/WSGM.UiTests/Fakes/OverlayToolsSources.cs` |

## 5. Implementation batches

All batches keep `OverlayWindow.axaml` and every baseline image unchanged unless the batch names a defect correction. Narrow filters: `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"` (UI), `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual"` (captures), `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay|FullyQualifiedName~WSGM.Tests.Controls"` (unit). Requirement 13 allows these before the maintainer's manual pass.

| Id | Title | Files | Steps | Depends on | Tests | Lines |
| --- | --- | --- | --- | --- | --- | --- |
| OVERLAY-B1 | Defect corrections that need no new owners | `OverlayWindow.Device.cs`, `GlyphInputTestMap.cs`, `CommonPluginPanel.cs`, `ProfileOverrideMarker.cs`, `AudioPanel.axaml.cs`, `OverlayController.cs` (timeout fallback, UAC lambdas), `OverlayWindow.PowerEditors.cs`, `OverlayWindow.Storage.cs`, `ThemesView.Tools.cs`, `AnimationsView.Tools.cs`, `DeviceSliderRow.cs`, `DeviceCurveRow.cs`, `OverlayWindow.axaml.cs` | OVERLAY-004 mask; OVERLAY-021 outcome text; OVERLAY-029 containment; OVERLAY-003 delete fallback and make preview editors read-only; OVERLAY-027 dialog pairing; OVERLAY-015 fetch on tab entry; OVERLAY-041 flush on dismissal | none | allocation test for the glyph map; `PreviewTimeoutEditorsAreReadOnly`; `DispatchedActionReadsApplied`; `BrowseFetchesOnceForAnEmptyRepository`; `DismissFlushesAPendingSliderOnce`; UI + unit filters | 350 |
| OVERLAY-B2 | Surface host replaces globals | new `IOverlaySurfaceHost`, `OverlaySurfaceHost.cs`; `OverlayWindow.Surfaces.cs` (moved), `OverlayWindow.axaml.cs`, `OverlaySubView.cs`, `ServiceSubView.cs`, `DeviceControlRows.cs`, `CommonPluginPanel.cs`, `OverlayWindow.Storage.cs`, `OverlayController*.cs`, delete `Core/KeyboardService.cs`, `StatusPanel.cs`, `FluentExtensions.cs` | views get the host at attach; `SharedSession` becomes per controller; delete the three files | none | `KeyboardEditingTests`, `OverlaySurfaceTests`, `UtilitySurfaceTests`, new `TwoSheetsKeepTheirOwnTextEntry` | 500 |
| OVERLAY-B3 | Activation out of the controller; session policy out of the overlay | new `OverlayActivation.cs`; `TouchSwipeMonitor.cs`, `OverlayController.cs`, delete `OverlayController.Gestures.cs`, `OverlayController.SteamExit.cs`; `ShellSession.cs`, `ShellSession.Config.cs`, `SessionModes.cs`, `SettingsWindow.axaml.cs` | single monitor without static registry; preview composes no activation; relaunch into `SessionModes`; accent/log/modes reload subscribers in ShellSession (or H02's reloader when it exists) | H02 (if its reloader lands first, subscribe there; otherwise ShellSession's reload callback, then H02 moves it); Settings domain for the preview's button source | new `OverlayActivationTests` (fake hotkey/chord/touch sources, preview has none); swipe-routing tests move; `SessionModes` relaunch tests in WSGM.Tests Shell filter | 800 |
| OVERLAY-B4 | Controller ports and lifecycle suite | `OverlayController*.cs`, new port files, `OpenAppsStrip.cs`, `QuickAccessPins.cs` (Shell), `OverlayWindow.Power.cs`, `ShellSession.cs`, `ShellSession.Shutdown.cs` | `ISteamInputLease`, `IOverlayPlatform`, `IOverlaySheetFactory`, `ISessionPowerActions`; synchronous Dispose teardown (OVERLAY-013); pins owner with one write queue (OVERLAY-012); `OverlayInput.Create` | B2, B3; H02 for shutdown ordering text only | new `OverlayControllerLifecycleTests`: open/close idempotence, lease Hold/Drop balance, claim balance, resummon cancels deferred close, warning reopen, Dispose releases synchronously, Open apps return waits for close and lease, keyboard request round trip, preview refuses mode switch and power-timeout writes; `QuickAccessPinsTests` (ordering) | 1300 |
| OVERLAY-B5 | Shared capability rows and editors | new `CapabilityRowRenderer.cs`, `Controls/CommitComboBox.cs`, `InvokeButtonRow.cs`; `OverlayWindow.Device*.cs`, `.Graphics.cs`, `.Sections.cs`, `.Performance.cs`, `DeviceControlRows.cs`, `OverlayEditors.cs`, `OverlayWindow.PowerEditors.cs`, `DevicePowerPresetView.cs`, `ManualTdpModeView.cs`, `AudioPanel.*`, `DisplayModeView.cs`, `DescriptorControlView.cs`, `DeviceCapabilityControl.cs` | OVERLAY-007, -008, -032, -033; delete view-side generation checks and `SameDeviceLayout` | B1 | `DeviceRowReconciliationTests`, `OverlayInteractionTests`, `DisplayPageViewsTests`, new browse-without-commit tests for preset/audio/manual TDP; Visual filter (images identical) | 1200 |
| OVERLAY-B6 | Navigation controller | new `OverlayNavigationController.cs`; `OverlayNavigation.cs`, `OverlayWindow.Navigation.cs`, `.Workspace.cs`, `.Rail.cs`, `.Header.cs` | `Back(BackContext)`; break the render cycle and delete both guards and `MaximumDepth`; typed keys with unchanged strings (OVERLAY-022, -023, -034) | B4 | `OverlayNavigationTests` (back table), `ControllerNavigationTests`, `OverlayInteractionTests`; Visual filter | 1100 |
| OVERLAY-B7 | Page controllers | new `DevicePage.cs`, `GraphicsPage.cs`, `PerformanceRows.cs`, `QuickAccessPinsPage.cs`, `PowerPage.cs`, `StorageFormatPage.cs`; the remaining `OverlayWindow.*.cs` partials shrink or are deleted | move code per the symbol table; one reconciler helper (OVERLAY-035); pin indicators tracked per page (OVERLAY-049) | B5, B6 | full UI filter; `DevicePageCaptureTests`, `GraphicsPageCaptureTests`; Visual filter | 1500 |
| OVERLAY-B8 | Launch fixes and library tools through owners | new `Shell/LaunchFixService.cs`, `SteamLaunchFixesPage.cs`; `OverlayWindow.LaunchFixes.cs` (deleted), `LibraryTabsView.cs`, `CardManagerView.cs`, `Shell/LibraryTabManager.cs` (instance surface used by the views) | transaction moves unchanged first; then keep the snapshot on DispatchedUnknown (OVERLAY-010) | T01 outcome types for the uncertainty step; S02 for the `LibraryTabManager` instance | new `LaunchFixServiceTests` (fake client: refused forgets, unknown keeps, existing snapshot preserved, wrapped shortcut refusal); `OverlayToolsTests` | 900 |
| OVERLAY-B9 | Tool views on typed backends | `ServiceSubView.cs`, `GameLibraryView*.cs`, `ThemesView*.cs`, `AnimationsView*.cs`, `ArtworkView.cs`, `SoundsView.cs`, `LaunchWrapperView.cs`, `OverlayFilePicker.cs`, new `Controls/PagedRows.cs`, `Fakes/OverlayToolsSources.cs` | consume typed results (OVERLAY-017); `SetSelectedAsync` (OVERLAY-016); offered artwork vocabulary from state (OVERLAY-018); one paging helper and picker focus (OVERLAY-025, -026); remove caps (OVERLAY-024) | S02 and G01 backend changes | `OverlayToolsTests`, new `OverlayFilePickerTests` over temp dirs, `PreviewTools` captures identical | 1100 |
| OVERLAY-B10 | Windows/device/display projections through session owners | `PowerSchemeSelection.cs`, `OverlayController.cs`, `DisplayModeView.cs`, `ManualTdpModeView.cs`, `OverlayWindow.Sources.cs`, `PinnedPluginWidgets.cs`, `CommonPluginPanel.cs`, `Controls/PhysicalGlyphService.cs` -> `Shell/PhysicalGlyphPlans.cs`, `DeviceOverlayBridge.cs`, `RadioPanel.axaml.cs`, `EjectPanel.axaml.cs` | OVERLAY-019, -009 (target unchanged), -030, -028, -036, -040, -043 | D03 power-profile owner, W02/I01 display port, W01 radio manager instance, D01 manual TDP source | `HybridCoreViewTests`, `DisplayPageViewsTests`, `PowerSchemeSelectionTests`, `PhysicalGlyphServiceTests` (rewritten for the simpler cache), Visual filter | 1000 |
| OVERLAY-B11 | Test quality | `ControllerNavigationTests.cs`, `CommonPluginPanelTests.cs`, `UiFixture.cs`, `QuickAccessSheetTests.cs`, `OverlayViewModelTests.cs`, new `TouchSwipeMonitorLifetimeTests`, `OverlayMediaPreview` lifetime test with a fake environment factory | use `OverlayInput.Create`; move misfiled tests; delete getter-only tests (OVERLAY-044, -045) | B4, B6 | the filters it edits | 600 |
| OVERLAY-B12 | Nit sweep | `TabStrip.cs`, `OnScreenKeyboard.cs`, `CurveEditor.cs`, `CurveEditing.cs`, `DevicePowerPresetSelection.cs`, `OverlayPreviewImage.cs`, `OverlayMediaPreview.cs`, `AudioPanel.axaml.cs`, `ThemesView.cs`, `AnimationsView.cs`, `LaunchWrapperView.cs`, `OverlayViewModel.cs`, `ServiceSubView.cs` | OVERLAY-037, -038, -039, -042, -046, -047, -048, -050 | B7, B9 | unit and UI filters; Visual filter | 500 |

Order: B1 and B2 first (independent of other domains), then B3, B4, B5, B6, B7. B8, B9 and B10 wait on the named domains and can interleave with B6/B7. B11 after B4/B6, B12 last. The full `.\eng\verify.ps1` runs once after the initial implementation set per the root policy, not per batch.

## 6. Risks and open questions

Risks:
- Visual drift from moving code that builds controls (B5, B7). Mitigation: visual filter after every batch, baselines never refreshed by these batches.
- Focus and back behavior regressions (B6). Mitigation: back table unit tests plus the existing controller navigation tests running through the shared `OverlayInput.Create` wiring.
- Real-hardware input paths (swipe, chord, managed pad) are not covered headless; M01-13 and M01-33 must be rerun on the Claw and the Ally X tester build after B3/B4.

Open questions for the maintainer:
- Q1: The overlay display-mode selector always targets the first active display path. Keep that, or target the display the sheet is shown on?
- Q2: Add Steam Library and Replace launch action use the native Windows pickers, while `Overlay/AGENTS.md` says file and folder selection stays inside the overlay. Move them to the in-overlay picker (workflow change) or keep them native with navigation suspended?
- Q3: The application-profile editor's name and process fields are plain text boxes that a controller cannot reach. Convert them to press-to-edit rows like every other overlay field (visible change) or leave them?
- Q4: Read idle timeouts and Windows policies off the UI thread after the sheet opens (values appear a frame later), or keep the synchronous read?
