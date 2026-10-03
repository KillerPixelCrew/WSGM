# Overlay findings

Scope: the Avalonia overlay (`src/WSGM/Overlay/**`), the shared controls (`src/WSGM/Controls/**`), the headless UI tests (`tests/WSGM.UiTests/**`) and the overlay and controls unit tests (`tests/WSGM.Tests/Overlay`, `tests/WSGM.Tests/Controls`), on baseline `master` 1329813f. Sources: `_plan/refactor-2.1/review/overlay.md`, its adversarial verification `overlay.verify.md` (corrections applied below, four missed findings added as OVERLAY-V-001..004), the completeness critic `_critic.md`, plan v2 and the maintainer's `DECISIONS.md` (2026-10-03). Where they disagree, DECISIONS.md wins, then plan v2, and the critic's cross-domain resolutions are applied.

Counts after the solution check and DECISIONS.md: 0 critical, 2 high, 21 medium, 23 low and 5 nit findings that need work, plus 4 no-change ids (OVERLAY-028 and -041, which plan v2 closes, and OVERLAY-032 and -040, which the checker moved there with evidence). DECISIONS.md reopened OVERLAY-031 (press-to-edit rows) and OVERLAY-043 (reads off the UI thread), which plan v2 had closed, and changed OVERLAY-009 (target the sheet's display) and OVERLAY-010 (a dispatched write counts as written). No overlay finding was a security-hardening item. The checker added OVERLAY-C-001. The review's plan claims C1..C19 are not findings; plan v2 already folded them in.

Plan v2 batches for this area: B108 (defects needing no new owner), B109 (surface host), B110 (navigation controller), B113 (activation), B122 (controller ports and lifecycle suite), B123 (one Settings window, settings domain), B125 (session policy out), B126 (shared rows and combo helper), B127 (page controllers), B128 (launch fixes), B129 (tool views), B130 (Windows, display and device projections), B131 (tests), B132 (nits), and B053 (toolkit outcomes, carries OVERLAY-010). DECISIONS.md adds two overlay batches that `batches.json` does not list yet: one for OVERLAY-043 (after B122) and one for OVERLAY-031 (after B109 and B129). Cross-domain prerequisites named per finding: B005, B017, B037, B038, B041, B067, B075, B077, B079, B090, B093, B105, B116, B120, B124, B137.

Rules that apply to every overlay batch:

- Anchor every edit by symbol; the review's line numbers drift (the verifier corrected several). Exclude `.claude/` from caller inventories.
- `OverlayWindow.axaml` stays byte-identical unless a finding says otherwise; no PNG baseline under `tests/WSGM.UiTests/Baselines` changes and `eng/update-ui-baselines.ps1` is not run. The one exception is OVERLAY-031's batch, a visible change the maintainer decided: it refreshes the application-profile editor baselines and the images are reviewed. `OverlayPage`/`OverlayDestination` enum names and every `Tag` and focus-key string are frozen, because rail focus memory (`view.Page.ToString()`) and `UiFixture.Rail(window, OverlayPage)` depend on them.
- No MVVM framework, no UI dispatcher port, no ports that mirror Avalonia (`IWindow`, `IFocusManager`). Headless tests run on Avalonia's real dispatcher. The controller keeps three ports only: platform, sheet factory and session power actions. It takes the concrete Steam Input lease instance from B077 (critic conflict 24).
- `SimulatedGraphicsOverlaySource` is overlay-test only and must stay inert through B113 and B122 (critic 1.2).
- B005 (USER-001) deletes `_performanceObservation` and the RTSS observation plumbing in `OverlayWindow.Sources.cs`. No later overlay batch reintroduces it when it moves `Sources` code.

## High

### OVERLAY-005: Views own business workflows, persistence and native writes

- **Severity:** high
- **Where:** `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs:69-224, 226-412`; `src/WSGM/Overlay/LibraryTabsView.cs:88-100, 230-238, 362-381`; `src/WSGM/Overlay/CardManagerView.cs:44-168`; `src/WSGM/Overlay/OverlayWindow.Power.cs:46-107`; `src/WSGM/Overlay/DisplayModeView.cs:31-36`; `src/WSGM/Overlay/OverlayController.cs:552-553, 969-997`; `src/WSGM/Overlay/OverlayMediaPreview.cs:68, 193-373`.
- **Problem:** UI classes run whole transactions through statics. The launch-wrapper transaction (snapshot, `LaunchWrapperStore.RememberAsync/ForgetAsync`, `SteamLaunchConfig.ApplyAsync/ApplyCustomAsync/RestoreAsync`) lives in the window and is written twice (wrapper and custom paths). The library tab views call `ConfigStore.Load` and the static `LibraryTabManager`, and the tab-order save is fire-and-forget. Click handlers call real `PowerActions`. `DisplayModeView` reads and writes displays through WDC directly. The controller calls `ConfigStore.Mutate` for the energy-plan reference and the pin list. A control downloads over HTTP into temp files. None of it can be tested without live Steam, displays or power APIs, and each workflow is duplicated by the Steam surface.
- **Best solution:** No new layer; each workflow moves to one owner and the view only renders and reports intent. Launch fixes: `Shell/LaunchFixService` with `ApplyWrapperAsync(game, mode)`, `ApplyCustomAsync(game, path, arguments)` and `RestoreAsync(appId)`. Both apply paths share one private `WriteWithSnapshotAsync(appId, snapshot, write)` that remembers before the write and forgets only on `NotSent`/`Rejected` (OVERLAY-010). `SteamLaunchFixesPage` keeps the button titles and the picker hand-off (B128). Library tabs: `LibraryTabsView` and `CardManagerView` take the `ILibraryTabs` instance from B137 (STEAMHOST-013) and read its snapshot instead of `ConfigStore.Load`. Its tracked worker keeps press order and logs failures. Power actions go through `ISessionPowerActions` (OVERLAY-020). Display modes come from session-supplied read/apply delegates (OVERLAY-009). The pin list uses a chained write (OVERLAY-012), and the energy-plan reference goes through the single power-profile workflow (OVERLAY-019). The media preview keeps its own bounded download (OVERLAY-038 explains why that is not a defect). The direct calls leave in different batches: power actions in B122, launch fixes in B128 (after B127), display modes and the energy-plan persist in B130, and the library-tab `ConfigStore.Load` and static `LibraryTabManager` calls only in B137. So the closing check runs in the B137 review, not B127: no file under `src/WSGM/Overlay` references `ConfigStore`, `LibraryTabManager`, `DisplayModes`, `DisplayTopology`, `SteamLaunchConfig` or `LaunchWrapperStore`, and none calls `PowerActions.(Standby|Hibernate|Restart|Shutdown|SignOut)`. The bare word `PowerActions` cannot be grepped because the frozen enum member `OverlayPage.PowerActions` and the XAML panel `PanelPowerActions` keep it. B128's file list names `LibraryTabsView.cs` and `CardManagerView.cs`, but nothing in B128 changes them; their move is B137.
- **Tests:** covered by the owning findings' tests; B127 runs `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"` and the Visual filter `--filter "FullyQualifiedName~WSGM.UiTests.Visual"`.
- **Plan v2:** B127 (the rest), with parts in B122, B128, B130, B132 and B137.
- **Related:** OVERLAY-009, -010, -012, -019, -020, -038; STEAMHOST-013; review claim C2.

### OVERLAY-006: OverlayWindow is a 6,500-line partial-class aggregate

- **Severity:** high
- **Where:** `src/WSGM/Overlay/OverlayWindow.*.cs` (21 partial files, 6,507 lines including `OverlayWindow.axaml.cs`, per the verifier), `src/WSGM/Overlay/OverlayWindow.axaml`; `src/WSGM/Overlay/OverlayController.cs` `ShowOverlayCore` (16 `Attach*` calls, verifier-corrected from 20).
- **Problem:** One class holds navigation, focus memory, the section rail, device/graphics/performance rendering, Quick Access pins, transient surfaces, launch fixes, SD-card format, power editors, Windows policy toggles, header profile scope and placement animation. Every change touches shared private state, and nothing below the whole window can be tested.
- **Best solution:** Plain page-controller classes over the existing named XAML elements: no new UserControls, XAML unchanged. B109 adds `OverlaySurfaceHost` and B110 adds `OverlayNavigationController`. B127 adds `DevicePage` (device render, sections, glyph preview, input test, host rows, value refresh), `GraphicsPage`, `PerformanceRows` (with the header profile scope), `QuickAccessPinsPage` (pins and folds), `PowerPage` (actions, confirms, keep-awake and timeout editors, UAC and lock toggles), `StorageFormatPage` and `SteamLaunchFixesPage`. Each class gets the generated named-element fields it renders into through its constructor and owns those elements' handlers. `OverlayWindow` keeps construction, placement, slide, backdrop and composition. `OverlayWindow.Attach(OverlaySources)` calls each page's `Attach` once, so the twelve attaches of `OverlaySources` members in `ShowOverlayCore` (brightness, manual TDP, device, graphics, prerequisites, common plugins, game library, themes, animations, sounds, artwork, performance) become one. The other four of the 16 calls (`AttachPowerSchemes`, `AttachHybridCores`, `AttachPowerPresets`, `AttachFormatManager`) take objects the controller builds per open or owns, so they stay separate calls on the owning page. Use the review's symbol table (overlay.md section 4) as the move list and account for every symbol of each dissolved partial before deleting it (CLAUDE.md rule). `OverlayWindow.Sources.cs` content goes to the page that renders each source. Do not carry back the B005 deletions.
- **Tests:** existing `DevicePageCaptureTests`, `GraphicsPageCaptureTests`, `OverlayInteractionTests` and `ControllerNavigationTests` unchanged and green; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"` and the Visual filter with identical images.
- **Plan v2:** B127 (after B109, B110, B126).
- **Related:** OVERLAY-005, -035, -049, -V-004; review C13 (stale pre-AM01 V01 brief).

## Medium

### OVERLAY-001: OverlayController is the lifecycle owner and has no lifecycle test

- **Severity:** medium (verifier lowered from high: a coverage gap, not a behavioural defect)
- **Where:** `src/WSGM/Overlay/OverlayController.cs:166-233, 477-672, 886-936` and partials `.Apps`, `.Gestures`, `.Keyboard`, `.Lease`, `.Power`, `.SteamExit` (2,142 lines).
- **Problem:** The constructor builds `HotkeyService`, `GamepadService`, `GamepadChordWatcher` and `TouchSwipeMonitor`. Sheet code calls static `SteamInputBlocker`, `ExplorerControl`, `WindowFinder`, `NativeMethods`, `ConfigStore`, `PowerTimeouts`, `UacSettings`, `LockScreenSettings` and `AppLauncher`. Nothing tests the rules `Overlay/AGENTS.md` calls invariants: lease Hold/Drop pairing, UI-capture claim pairing, the 150 ms deferred close and its cancellation on resummon, warning reopen, the keyboard request flow, the Open-apps return after close and lease release, and the show-failure teardown. (The existing `QuickAccessSheetTests` table does test the production `DecideSwipe`; the review was wrong there.)
- **Best solution:** After activation moves out (OVERLAY-002), the controller takes three ports by constructor, each with one production implementation: `IOverlayPlatform { nint Foreground(); bool ExplorerRunning(); PixelPoint? WindowCenter(nint); void BringToForeground(nint); bool StartTaskManager(); }`, `IOverlaySheetFactory { OverlayWindow Create(...) }` (returns a real `OverlayWindow`), and `ISessionPowerActions` (OVERLAY-020). It takes the concrete `SteamInputBlocker` instance from B077, with no `ISteamInputLease` port. Switcher refresh, icon resolve, `PickWindow` and the task manager move into an `OpenAppsStrip` class the controller owns, calling the platform port. Then add `tests/WSGM.Tests/Overlay/OverlayControllerLifecycleTests.cs` on the headless platform with fakes for the three ports and a real `SteamInputBlocker` over a fake lease port. Cases: open/close idempotence; lease Hold/Drop balance; claim balance; resummon cancels the deferred close; warning reopen; Dispose releases claim and lease synchronously; Open-apps return waits for close and lease; keyboard request round trip; a preview refuses the mode switch and power-timeout writes.
- **Tests:** the new suite; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayControllerLifecycle|FullyQualifiedName~QuickAccessSheet"`; UI filter `FullyQualifiedName~WSGM.UiTests.Overlay`.
- **Plan v2:** B122 (after B113, B121, B077).
- **Related:** OVERLAY-002, -012, -013, -020; INPUT-012 (B077); critic conflict 24 and over-engineering flag "overlay B4 ISteamInputLease port".

### OVERLAY-002: The Settings preview controller arms the session's global activation inside the resident process

- **Severity:** medium
- **Where:** `src/WSGM/Settings/SettingsWindow.axaml.cs:255-262` (`ShowTestOverlay` builds `new OverlayController(..., previewOnly: true)` in the session process); `src/WSGM/Overlay/OverlayController.cs:215-227`; `src/WSGM/Overlay/TouchSwipeMonitor.cs:65-118, 287-346`; `src/WSGM/Core/KeyboardService.cs:16`; `src/WSGM/Shell/ShellSession.cs:864-898`.
- **Problem:** After the Test sheet was used once while Settings stays open (the preview lives until Settings closes): a second `TouchSwipeMonitor` joins the shared raw-input registration and both dispatch every `WM_INPUT` (`TouchSwipeMonitor.cs:295-311`), so one top swipe opens the session sheet and the preview sheet. A second chord watcher fires on the same chord. The preview's hotkey registration fails with `ERROR_HOTKEY_ALREADY_REGISTERED`. `KeyboardService.Handler` is last-opener-wins and first-closer-nulls, so closing either sheet breaks text entry in the other. The preview never gets `UseManagedPad`, so the handheld pad may not drive it (plausible).
- **Best solution:** Extract `src/WSGM/Overlay/OverlayActivation.cs`: it owns the `HotkeyService`, the `GamepadChordWatcher` over the session's `GamepadService`, the touch-edge subscription, `SwipeAction`/`DecideSwipe`, `ApplyGestures` and arm/disarm while a sheet is up. It raises `QuickAccessRequested` and `OpenAppsRequested` and sends the Steam shortcuts. `ShellSession` builds one for the session and one for `--overlay-test`, and `--overlay-test` keeps hotkey, chord and swipe: there is no tray, `SessionActivation` or `SettingsActivation` in that mode (`ShellSession.cs:900-935`), so they are its only reopen path (verifier correction to C4). The Settings preview composes the controller with no activation (`activation: null`). The controller's rule "keep the gamepad running after close" becomes `_activation?.ChordEnabled == true`. `OverlayController.Gestures.cs` is deleted. Overlay-test still shows the sheet once at start. The static monitor registry is removed by B079 (INPUT-030, one raw-input owner with subscribers), which B113 builds on. Text entry stops being global in B109 (OVERLAY-014). The preview's managed pad arrives with B075/B123 (INPUT-009). B113 owns hotkey, chord and swipe construction; SESSION-B3 (B114) follows and WINSVC-B10 does not touch them (critic conflict 25).
- **Tests:** new `OverlayActivationTests` with fake hotkey, chord and touch sources: session activation raises a request, the preview controller has no activation, overlay-test composition keeps all three triggers. Move the swipe-routing table with `DecideSwipe`. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayActivation|FullyQualifiedName~QuickAccessSheet"`; UI filter `FullyQualifiedName~WSGM.UiTests.Overlay`. Manual: M01-13 and M01-33 on the Claw and the Ally X tester build.
- **Plan v2:** B113 (after B110, B079).
- **Related:** OVERLAY-014, -044; INPUT-030 (B079), INPUT-009 (B075); critic conflict 25; review C4, C8.

### OVERLAY-003: Preview sheets write real Windows idle timeouts

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayController.cs:779-818` (`PowerTimeoutSelected` handler; fallback branch locks `PowerSchemes.MutationGate` and calls `PowerTimeouts.Write`); `src/WSGM/Overlay/OverlayWindow.PowerEditors.cs:146-147` (`editor.IsEnabled = current is not null`); `src/WSGM/Overlay/OverlayController.cs:517`; `src/WSGM/Shell/ShellSession.cs:899`.
- **Problem:** Energy plan, core preference, UAC and lock-on-wake are read-only in a preview; idle timeouts are not. The Settings Test sheet writes through the controller's fallback branch. Per the verifier, `--overlay-test` (also `previewOnly`) gets the session's real `DisplayTimeouts` and writes through `DisplayTimeouts.Select`, although CLAUDE.md calls `--overlay-test` safe. Keying read-only on "DisplayTimeouts absent" would miss overlay-test.
- **Best solution:** Key read-only on `_previewOnly`, as the UAC and lock toggles already do. Add `PowerTimeoutsEditable` to `OverlayViewModel` beside `ModeSwitchAvailable` and set it to `!_previewOnly` where the view model is built. `RefreshPowerEditors` sets `editor.IsEnabled = current is not null && vm.PowerTimeoutsEditable`. The handler returns at once when `_previewOnly`. Then delete the fallback branch (`lock (PowerSchemes.MutationGate) { PowerTimeouts.Read ... Write }`): the session always supplies `DisplayTimeouts`, so it was the only path that gated a write on a prior read. The handler keeps its continuation chain and calls `_displayTimeouts?.Select(kind, seconds)` only. Values still read and display in previews. B108's file list lacks `src/WSGM/Overlay/OverlayViewModel.cs`; add it.
- **Tests:** `PreviewTimeoutEditorsAreReadOnly` in `HybridCoreViewTests` or a new power-editor UI test, run for both a Settings-style preview (no `DisplayTimeouts`) and an overlay-test composition with a fake `DisplayTimeouts` that records `Select` calls (expect none). `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"`; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay"`.
- **Plan v2:** B108.
- **Related:** WINSVC-010 (B017, `DisplayTimeouts.Select` writes without a prior read); WINSVC-009/030 (B090 moves the gate into `PowerSchemes`); OVERLAY-001 lifecycle case "preview refuses power-timeout writes".

### OVERLAY-004: The glyph input test allocates per controller sample

- **Severity:** medium (verifier lowered from high: it only runs while the Device glyph input-test page observes samples)
- **Where:** `src/WSGM/Overlay/OverlayWindow.Device.cs:854-872` (`OnPhysicalGlyphSample`), `:696-700` (`ApplyGlyphInputTest`); `src/WSGM/Overlay/GlyphInputTestMap.cs:70-92` (`Pressed`); `src/WSGM/Overlay/OverlayWindow.axaml.cs:80` (`_pressedGlyphControls`).
- **Problem:** On the sampling thread, at input rate, `GlyphInputTestMap.Pressed` builds a new `HashSet<GlyphControlId>` for every sample before `SetEquals`. That breaks the rule that high-rate input paths allocate nothing per sample. (The review's "no barrier" race is refuted: each set is published once and never mutated, and a reference write is atomic.)
- **Best solution:** Compare raw input, not glyph sets (the verifier's simpler option, adopted by plan v2). Add `GlyphInputTestMap.Key(CanonicalControllerSample) => (long)sample.Buttons | (left > TriggerThreshold ? 1L << 32 : 0) | (right > TriggerThreshold ? 1L << 33 : 0)` (`CanonicalButtons` is a `uint` enum, so bits 32 and 33 are free) and `GlyphInputTestMap.Lights(long key, GlyphControlId control)`, which walks the existing `Buttons` table and the two trigger bits. The window replaces `_pressedGlyphControls` with a `long _glyphInputKey`. The handler computes the key, returns when it equals `Volatile.Read(ref _glyphInputKey)`, otherwise does `Volatile.Write` (one sampling thread writes it, so no `Interlocked` is needed) and posts once. `ApplyGlyphInputTest` sets each tile's `pressed` class from `Lights(key, control)`. Reset sets the key to 0. The `HashSet` overload is deleted. A change in an unmapped button posts one harmless refresh, which is cheaper than mapping on the sampler. The field lives in `OverlayWindow.axaml.cs`, which B108's file list lacks; add it.
- **Tests:** update `tests/WSGM.Tests/Overlay/GlyphInputTestMapTests.cs` to assert `Lights` for every table row, both pad touch and click lighting the trackpad, and the trigger threshold. Add an allocation test: `GC.GetAllocatedBytesForCurrentThread()` around 10,000 calls of the key-and-compare path equals zero. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GlyphInputTestMap"`.
- **Plan v2:** B108.
- **Related:** review C9; INPUT-005 (B073, same rule on the controller path).

### OVERLAY-007: Device, Graphics and Performance duplicate the capability-row machinery

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayWindow.DeviceReconciliation.cs:20-77` (`SameDeviceLayout`, a hand-kept field list) vs `src/WSGM/Overlay/OverlayWindow.Graphics.cs:326-354` (`GraphicsLayout`, a string built per refresh); readings `FlexPanel` logic in `OverlayWindow.Sections.cs:219-283` and `OverlayWindow.Graphics.cs:203-241`; command wrappers `RunDeviceCommandAsync`, `CommitDeviceValueAsync`, `RunGraphicsCommandAsync`, `WritePerformanceValueAsync`, `InvokePerformanceAsync`, `UseGlobalOnPerformance` (`OverlayWindow.Device.cs:714-735`, `OverlayWindow.Performance.cs:100-173`); view-side re-checks in `OverlayWindow.DeviceControls.cs:12-48` and `OverlayWindow.Graphics.cs:271-317`.
- **Problem:** Three copies of the layout identity, readings grouping and value refresh, and six copies of one try/cancel/log command shape. A field added to the capability model has to be added to `SameDeviceLayout` by hand or rows stop rebuilding.
- **Best solution:** One `src/WSGM/Overlay/CapabilityRowRenderer.cs` used by device, graphics and pins. It builds rows, groups readings and refreshes values in place. The per-row layout fields are declared once, in `CapabilityRowRenderer.SameRowLayout(DeviceOverlayCapability before, DeviceOverlayCapability after)`: `CapabilityId`, `InstanceId`, `CycleGeneration`, `DescriptorGeneration`, `ValueKind`, `Writable`, `SupportsAction`, `Title`, `CategoryId` and `PluginSectionId`, the union of what `SameDeviceLayout` and `GraphicsLayout` compare today. Do not shrink it to ids, generations and kinds: a title, writability or category change must still rebuild the row. `SameDeviceLayout` keeps its device-only snapshot fields (visibility, plugin sections, host selections, glyph preview, controller, AutoTDP, authored profile, recovery) and calls `SameRowLayout` per capability. Graphics compares its previous section with `SameRowLayout` plus title, categories and section count instead of building a string per refresh, so `GraphicsLayout` and `_pinnedGraphicsLayouts`' string values go. One `RunCommandAsync(string description, Func<CancellationToken, Task> command)` replaces the six wrappers, keeping each wrapper's log text as its description. Correction from the verifier: the view-side generation checks are not redundant. `WriteDeviceValue` re-reads the snapshot and refuses when it is not `Visible` or the current row is no longer `CanInvoke`, then sends `current with { NextValue }`. `DeviceOverlayBridge.InvokeAsync` (`Shell/DeviceOverlayBridge.cs:504-535`) and `GraphicsOverlaySource.WriteAsync` (`Shell/GraphicsOverlaySource.cs:158-182`) check only the passed object. So keep exactly one shared view helper, `CapabilityRowRenderer.CurrentInvokable(IEnumerable<DeviceOverlayCapability> published, DeviceOverlayCapability seen)`, which returns the published row with the same `GpuPluginId` (null for device rows), `CapabilityId` and `InstanceId` when it is `CanInvoke` and both generations still match, else null. `WriteDeviceValue` passes the device snapshot's capabilities (and keeps its `snapshot.Visible` check), `WriteGraphicsValue` passes the graphics sections' capabilities. Delete the two inline copies. Do not add snapshot revisions (review C6).
- **Tests:** `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DeviceRowReconciliationTests|FullyQualifiedName~OverlayInteractionTests|FullyQualifiedName~GraphicsPageCaptureTests"` (note: `DeviceRowReconciliationTests` lives in WSGM.UiTests, not WSGM.Tests as B126's filter says). Add a case where a row turns non-invokable between render and commit and no write reaches the fake bridge. Visual filter with identical images.
- **Plan v2:** B126.
- **Related:** OVERLAY-033, -034, -049; review C6, C7.

### OVERLAY-008: Four combo editors commit while the dropdown is browsed

- **Severity:** medium, plausible (verifier corrected the scope from five editors to four)
- **Where:** unguarded: `src/WSGM/Overlay/DevicePowerPresetView.cs:49-68` (AC and battery preset assignment), `src/WSGM/Overlay/ManualTdpModeView.cs:26-49`, `src/WSGM/Overlay/AudioPanel.axaml.cs:169-193` (format and spatial), TwoWay `SelectedOutput`/`SelectedInput` bindings in `src/WSGM/Overlay/AudioPanel.axaml:21, 53`. Guarded implementations to fold: `OverlayWindow.PowerEditors.cs:76-113` (`ObservePowerChoice`), `DeviceControlRows.cs:78-136`, `OverlayEditors.cs:89-147` (`OverlayChoice<T>`), `OverlayWindow.Header.cs:70-81`. `DisplayModeView` is already guarded.
- **Problem:** Browsing an open dropdown with the controller changes `SelectedItem`. The guarded editors (and `OverlayInteractionTests.WakePopupDoesNotApplyIntermediateModesWhileBrowsing`) exist because of that. The four unguarded ones write a preset assignment, an audio format, a spatial format or the default endpoint for every item the user passes.
- **Best solution:** An attach helper in Controls, not a ComboBox subclass: a subclass would change element types in `OverlayWindow.axaml` (header profile combo) and `AudioPanel.axaml`, and it would render without a template unless it set `StyleKeyOverride`. `ComboCommit.Attach<T>(ComboBox editor, Func<bool> refreshing, Action<T> commit)` lifts `ObservePowerChoice` verbatim: it tracks drop-down open state, commits on `DropDownClosed` or on a selection change while closed, records the baseline instead of committing while `refreshing()` is true, and skips a value equal to the last committed one or a disabled editor. Apply it to the four unguarded editors and replace the three closure copies with it: `ObservePowerChoice` (its extra `RefreshPowerEditors` on close stays as its own `DropDownClosed` subscription, added after `Attach` so the commit runs first), `DeviceControlRows.Choice` (`refreshing` is `row?.Refreshing is not false`) and the header profile combo (`refreshing` is `_profileScopeSynchronizing || _profileScopeWriting`). `OverlayChoice<T>` stays as it is: it is already a `ComboBox` subclass with `StyleKeyOverride` whose baseline is reset through `RefreshFrom`, so folding it gains nothing. `AudioPanel.axaml` endpoint bindings become `Mode=OneWay`, and the helper commits through the view model's setter. `ManualTdpModeView` keeps its `writing` flag and status text.
- **Tests:** new browse-without-commit UI tests for preset AC/battery, manual TDP mode, audio format and output endpoint: open the drop-down, move selection twice, assert no write, close, assert exactly one write. Keep `WakePopupDoesNotApplyIntermediateModesWhileBrowsing`. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayInteractionTests|FullyQualifiedName~DisplayPageViewsTests|FullyQualifiedName~ManualTdpModeTests|FullyQualifiedName~DevicePowerSectionTests"`; Visual filter including the `PreviewAudioPanel` capture.
- **Plan v2:** B126.
- **Related:** none in the ledger.

### OVERLAY-009: The overlay changes display modes outside the session's display owner

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/DisplayModeView.cs:28-36` (constructor falls back to `DisplayTopology.CaptureActive().Paths[0]`, `DisplayModes.Read/Apply`); `src/WSGM/Overlay/OverlayWindow.Sources.cs:80-90` (passes only `readMode`, never an apply); other writers `src/WSGM/Core/DisplayProfiles.cs:50-53, 250`.
- **Problem:** The production apply is always the view's own WDC fallback, so overlay display writes bypass the session's composition and cannot be faked in tests. Serialization with display-profile and refresh-pairing writes is only as good as WDC's internal gate. The fallback also always targets `paths[0]`, so on a machine with a second display the selector reads and changes a display other than the one the sheet is shown on.
- **Best solution:** Make both `DisplayModeView` delegates required and delete the WDC fallback lambdas. `OverlaySources` carries a `DisplayModeAccess(Func<string, Task<DisplayModeSnapshot?>> Read, Func<DisplayModeSnapshot, DisplayMode, Task<DisplayProfileResult>> Apply)` record that `ShellSession` builds from the same WDC calls it already uses. `Read(sourceName)` captures the active topology on a worker, picks the path whose `ActiveDisplayPath.SourceName` equals the given GDI name (ordinal, ignoring case) and returns `DisplayModes.Read(path.Target)`, or null when no active path matches. Per DECISIONS.md the target is the display the sheet is on, not `paths[0]`: `OverlayWindow` exposes `internal string? DisplaySourceName()`, which takes the screen the sheet was placed on (`Screens.ScreenFromWindow(this)`, the same lookup `OverlayWindow.Placement.cs` uses), gets its `HMONITOR` from `Screen.TryGetPlatformHandle()` and returns `MONITORINFOEXW.szDevice` from a new `GetMonitorInfoW` import in `NativeMethods` (the `\\.\DISPLAYn` form `SourceName` uses). `DisplayModeView` takes a `Func<string?> display` from the window and calls it on every read, so a re-placed sheet follows its screen. No source name or no matching path shows the view's existing unavailable state; there is no fallback to another display. The apply keeps the snapshot it was read from, so it writes the same display. WDC's single internal display write gate from B067 serializes every display write, so WSGM adds no lock.
- **Tests:** `DisplayPageViewsTests` constructs the view with a fake access record over two fake displays and asserts that a view whose display delegate names the second display reads and applies only the second, one apply per committed choice, and that an unknown source name shows the unavailable state with no apply. `OverlayLayoutTests` (it calls `AttachBrightness(brightness, readMode)` today) and any capture that attaches brightness pass a fake access record, so no test reads or writes the real display. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DisplayPageViewsTests|FullyQualifiedName~OverlayLayoutTests"`; Visual filter. Manual: on a device with an external display, open the sheet on each display and confirm the selector lists that display's modes.
- **Plan v2:** B130 (after B067 for the write gate). Decided: target the display the overlay sheet is shown on (DECISIONS.md, overlay display-mode selector).
- **Related:** OVERLAY-005; U01-029; WDC-021 and U01-035 (B067); critic conflict 10 (no new display owner type).

### OVERLAY-010: An uncertain launch-fix write deletes the only restoration record

- **Severity:** medium, plausible
- **Where:** `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs:199-209` (custom action, `case false when existing is null: ForgetAsync`) and `:388-396` (wrapper, `if (!result.Ok && existing is null) ForgetAsync`); `src/WSGM/Core/SteamLaunchConfig.cs:273-288`; toolkit `SteamClientScript.ParseWrite`.
- **Problem:** `Ok=false` folds "Steam unreachable", "rejected" and "dispatched but the answer was lost" together. If Steam applied the change and the answer was lost, a shortcut's original Target is overwritten and WSGM forgets the snapshot, so Restore can never recover the program.
- **Best solution:** Map the toolkit's write outcomes (critic conflict 5: `NotSent`, `Unknown`, `Rejected`, `Applied`; not the review's `DispatchedUnknown`) onto the two cases D9 allows: the write failed to dispatch or Steam refused it, or it was dispatched. `SteamLaunchConfig.ApplyAsync/ApplyCustomAsync` return the client outcome. `NotSent` and `Rejected` are the failures: the snapshot is forgotten only there, and only when no prior snapshot existed, and the button shows the failure as today. `Unknown` is a dispatched write: it is treated exactly like `Applied`, so the remembered snapshot stays and the button publishes the written launch option as applied. There is no "may not have been applied" state, no uncertain entry and no retry. On `Applied` or `Unknown` with an existing snapshot, re-remember as today. This lands in the B053 parent commit with the client redesign (critic conflict 21); B128 then moves the already-corrected code unchanged into `LaunchFixService`.
- **Tests:** in B053, `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LaunchFix"` with a fake client: Rejected forgets, NotSent forgets, Unknown keeps the snapshot and shows the same applied title as Applied, an existing snapshot is preserved on every outcome. B128 re-homes these as `LaunchFixServiceTests`.
- **Plan v2:** B053 (TOOLKITCS-B3 with parent consumers). Decided: D9, a write either dispatched (published as written) or failed to dispatch.
- **Related:** A01-F004, TOOLKITCS-003, TOOLKITCS-013; OVERLAY-005, OVERLAY-V-001; critic conflicts 5 and 21.

### OVERLAY-011: Session policy lives in the overlay controller

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayController.SteamExit.cs:12-66` (auto-relaunch of Steam, `DecideSteamExitReaction`, `_pendingSteamRelaunch`); `src/WSGM/Overlay/OverlayController.cs:383-385, 418-421` (`AccentPalette.Apply`, `_modes.ApplyConfig`, `Log.SetVerbosity` in `ApplyConfig`).
- **Problem:** Process-global reload work and Steam lifecycle policy run only because an overlay controller exists. `SessionModes` receives configuration only through the overlay. (The Settings preview does not add a second Steam-exit subscription: `SettingsWindow.ShowTestOverlay` passes a null `SteamMonitor`, so only the session controller subscribes.)
- **Best solution:** Three moves, each to its real owner. Verbosity: B041 already applies the effective verbosity (flag or config) in the reload path (CONFIG-030); delete the `Log.SetVerbosity` and "Config reloaded at" lines here. Modes: B116 makes `SessionConfigReloader` apply modes config directly (SESSION-026, U05-LFB-028); delete `_modes.ApplyConfig`. Accent: becomes an ordered reloader step. Steam exit: `OnSteamExited`, `DecideSteamExitReaction`, `SteamRelaunchDelay` and the relaunch timer move to `SessionTransitions` (B124). It raises `SteamExitShowOverlayRequested` for the `SteamExitReaction.ShowOverlay` branch, and the session's overlay controller subscribes to it, so that reaction stays reachable (verifier batch problem 1). `OverlayController.SteamExit.cs` is deleted. The controller's `ApplyConfig` keeps only its own work (blur, pins, CEF visibility, lease re-claim, glyph style).
- **Tests:** move the relaunch decision tests to `SessionTransitions`; add a test that the show-overlay reaction reaches a subscribed controller. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionTransitions|FullyQualifiedName~QuickAccessSheet"`; UI filter `FullyQualifiedName~WSGM.UiTests.Overlay`.
- **Plan v2:** B125 (after B124); verbosity in B041, modes in B116.
- **Related:** U05-LFB-028, CONFIG-030, SESSION-026; review C11.

### OVERLAY-012: Quick Access pins mutate the shared live config and persist out of order

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayController.cs:969-997` (`OnPinToggleRequested`), `:410`, `:584`.
- **Problem:** Each toggle sets `_config.QuickAccessPins` on the session's live `AppConfig` and persists in its own `Task.Run(ConfigStore.Mutate)`. Two quick toggles can commit in either order; the reload then hands back the older list and the sheet visibly reverts. Mutating the shared live config from the overlay is CONFIG-V-003.
- **Best solution:** No pins owner and no `Changed` event (verifier batch problem 6, plan v2 over-engineering table). The controller keeps a `List<string> _pins`, seeded from config at construction and replaced in `ApplyConfig`. A toggle edits a copy of `_pins`, assigns it, calls `SetPins`, and unless `_previewOnly` chains the write: `_pinWrites = _pinWrites.ContinueWith(_ => SavePins(snapshot), TaskScheduler.Default)`, the `LibraryTabManager.SaveTabOrder` pattern. `SavePins` runs the store mutation (the instance store after B037) and logs a failure as today. `_config` is never written, and the two readers of `_config.QuickAccessPins` (the `SetPins` call in `ShowOverlayCore` and the one in `ApplyConfig`) read `_pins`. Writes commit in press order, so the last reload carries the last intent. `PinnedPluginWidgets` keeps its poll (see OVERLAY-028).
- **Tests:** a controller test over a real `ConfigStore` on a temporary `UserDataContext` root (B037 gives tests that seam; no store fake is needed): two rapid toggles leave the file holding the second intent; the live config object is unchanged; a preview never writes the file. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayControllerLifecycle"`.
- **Plan v2:** B122.
- **Related:** CONFIG-V-003; critic over-engineering flag "overlay pins owner".

### OVERLAY-014: Process-global mutable state in the overlay

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayWindow.axaml.cs:33` (`SharedSession` static shared by session and preview sheets); `src/WSGM/Core/KeyboardService.cs:16` (`Handler` static); `src/WSGM/Overlay/OverlaySubView.cs:143-148, 281`, `src/WSGM/Overlay/ServiceSubView.cs:30-49`, `src/WSGM/Overlay/CommonPluginPanel.cs:104, 543`, `src/WSGM/Overlay/DeviceControlRows.cs:164`, `src/WSGM/Overlay/OverlayWindow.Storage.cs:33`, `src/WSGM/Overlay/OverlayController.cs:645, 911` (`TopLevel.GetTopLevel(this) as OverlayWindow` casts and `KeyboardService.Request`).
- **Problem:** Two sheets share navigation session state and one keyboard handler. Views find their host by casting the top level. Closing one sheet nulls the other's text entry.
- **Best solution:** Add `IOverlaySurfaceHost { bool RequestText(string prompt, string initial, int maxLength, Action<string> accept); Task<string?> PickLocalPathAsync(bool folder, params string[] extensions); bool HasActiveSurface { get; } event Action SurfaceClosed; }`, implemented by `OverlaySurfaceHost`. That class takes over the surface stack, keyboard surface, power-menu projection and `PickLocalPathAsync` from `OverlayWindow.Surfaces.cs`. The window hands the host to sub-views, `CommonPluginPanel` and `DeviceControlRows` at attach time, and every `GetTopLevel(...) as OverlayWindow` cast and `KeyboardService.Request` call goes. `RequestText` does what the controller's `OpenKeyboard` does today (`new KeyboardPanel(prompt, initial, maxLength)`, subscribe `Accepted`, `ShowKeyboardSurface`). `Core/KeyboardService.cs` is deleted; its only users are overlay files and `CommonPluginPanelTests` (which swaps `KeyboardService.Handler` and moves to the window's host). `SharedSession` and the public `OverlayWindow` constructor that supplies it are deleted; the controller holds one `OverlayWindow.SessionState` for its lifetime and passes it with `DockToTopEdge` to the internal constructor. `UiFixture` already passes its own `SessionState` and needs no change for this. `StatusPanel.cs` and `FluentExtensions.cs` are stateless helpers, not process-global state; they go here only because B109 lists them: `WirePanelBehaviour` (used by `RadioPanel` and `EjectPanel`) moves into the surface host, `CurrentWindowScale` into `OverlayWindow.Placement.cs`, and `FluentExtensions.Also` (one use, `CardManagerView.cs:47`) is inlined. Immutable bounded caches (`GlyphIcon`, `OverlayPreviewImage`) stay static.
- **Tests:** new `TwoSheetsKeepTheirOwnTextEntry` (two windows with separate `SessionState`; closing one leaves the other's text entry working) plus the existing `KeyboardEditingTests`, `OverlaySurfaceTests`, `UtilitySurfaceTests`. `CommonPluginPanelTests`' keyboard case is rewritten against the host. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~KeyboardEditingTests|FullyQualifiedName~OverlaySurfaceTests|FullyQualifiedName~UtilitySurfaceTests|FullyQualifiedName~WSGM.UiTests.Overlay"`.
- **Plan v2:** B109.
- **Related:** OVERLAY-002, -050 (StatusPanel, FluentExtensions items); review C2, C8.

### OVERLAY-016: "Select visible" toggles one entry at a time from a stale snapshot

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/GameLibraryView.Tools.cs:148-154, 198-216` (`SelectVisibleAsync` loops `ToggleEntryAsync`).
- **Problem:** N round trips and N publications. Because each call toggles, an entry changed meanwhile on the Steam page is flipped the wrong way.
- **Best solution:** Add `SetSelectedAsync(IReadOnlyList<string> ids, bool selected, CancellationToken)` to `IGameLibraryBackend` (`GameLibraryState.cs`, beside the existing bulk `SelectAsync(group, query, selected)`) and implement it in `GameLibraryService` exactly like that bulk method: under `_gate`, return the `Guard()` refusal if any, set (not toggle) `entry.Selected = selected && entry.Selectable` for each listed id still present, then `Publish()` once. Selection is in-memory state: `ToggleEntryAsync` and the bulk `SelectAsync` persist nothing, so neither does this. The view passes the ids of its existing `selectable` array (Selectable and not a Remove action, the overlay's current rule) and the target value, and deletes `SelectVisibleAsync`. The call is idempotent, so a concurrent Steam-page edit ends in the requested state. The bulk `SelectAsync` is not reused because it filters by `BulkSelectable` and has no source filter, which would change which titles the overlay's row ticks. The Steam page is unchanged; no wire change. The fake in `tests/WSGM.UiTests/Fakes/OverlayToolsSources.cs` implements the new member.
- **Tests:** a `GameLibraryService` test (one publication, idempotent when repeated, skips non-selectable entries, refused while an apply runs, as `Guard()` does); `OverlayToolsTests` select-visible case. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GameLibrary"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayToolsTests"`.
- **Plan v2:** B129 (after B105, which owns `GameLibraryService` threading).
- **Related:** OVERLAY-017; library domain (LIBRARY-006/009 in B105).

### OVERLAY-017: Overlay views consume the Steam page's JSON wire shapes and stringly states

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/GameLibraryView.cs:255-266` (probes `"acknowledge"` in a `JsonElement` payload); `src/WSGM/Overlay/GameLibraryView.Tools.cs:320-330` (deserializes `GameLibraryMatchesAnswer` from `Payload`); `src/WSGM/Overlay/ThemesView.Tools.cs:105-122` (`SetSettingAsync("enabled", JsonSerializer.SerializeToElement(...))`, `"translationsBranch"`); string literals `"scanning"`, `"applying"`, `"Add"`, `"Remove"`, `"outdated"`, `"manage"`, `"logo"`, `"folder"` in `GameLibraryView*.cs`, `GameLibraryRows.cs`, `ThemesView*.cs`, `ArtworkView.cs`.
- **Problem:** A C# view parses JSON written for a JavaScript page and compares free strings. A rename on the backend silently breaks the overlay; the compiler cannot help.
- **Best solution:** Typed C# members on the backends that the Steam backend wraps into JSON at its own boundary: `GameLibraryService.CycleLaunchAsync` returns `GameLibraryLaunchCycle(SteamUiCommandResult Command, bool NeedsAcknowledgement)`, `SearchMatchAsync` returns `(SteamUiCommandResult Command, GameLibraryMatchesAnswer? Matches)`, and the theme service gets `SetEnabledAsync(bool)` and `SetTranslationsBranchAsync(string)`. The Steam backends keep their current payload shapes by projecting from these. For the state strings, do not convert record fields to enums: the same records serialize to the Steam page, so an enum would change the wire. Put each vocabulary in one static class beside its record (`GameLibraryActions.Add`, `GameLibraryPhases.Scanning`, `ThemeStates.Outdated`, and so on) used by both backend and overlay. That removes the drift with zero wire risk.
- **Tests:** existing `OverlayToolsTests` and `PreviewTools` captures unchanged; update `tests/WSGM.UiTests/Fakes/OverlayToolsSources.cs` to the typed members. Steam asset checks unchanged (`npm run steam-assets:check`). `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayToolsTests"`; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GameLibrary|FullyQualifiedName~Theme"`.
- **Plan v2:** B129.
- **Related:** OVERLAY-016, -018; review C14.

### OVERLAY-018: The logo artwork tab offers grid styles instead of logo styles

- **Severity:** medium (verifier retargeted the defect: the cited grid-dimension drift is not drift, `DefaultFilter` holds default selections; the real drift is the logo tab)
- **Where:** `src/WSGM/Overlay/ArtworkView.cs:392-401` (`OfferedStyles`, logo falls to `_ =>`), `:403-413` (`Dimensions`); `src/WSGM/Core/SteamUiAssets/Source/artwork-browser.ts:14-30` (`artworkFilterOptions`); `src/WSGM/Shell/SteamArtworkBrowserSource.cs:1195-1210` (`DefaultFilter`).
- **Problem:** For the logo tab the overlay offers `alternate, white_logo, no_logo, blurred, material`, while the Steam page offers `official, white, black, custom`, and the default logo filter selects those. A controller user can neither see nor clear the selected logo styles and can toggle styles SteamGridDB logos do not have.
- **Best solution:** Two steps. B108 (defect fix, no new owner): add `"logo" => ["official", "white", "black", "custom"]` to `OfferedStyles`, matching the Steam page. B129 (one C# vocabulary): move the overlay's `OfferedStyles` and `Dimensions` and the source's `DefaultFilter` into one C# table, `ArtworkFilterOptions.For(tab)` beside `SteamArtworkBrowserSource`, with the overlay's current order kept. Do not publish it to the Steam page, which plan v2's B129 spec asks for: the two surfaces order the grid and wide styles differently (overlay `alternate, blurred, white_logo, material, no_logo`; `artwork-browser.ts` `alternate, white_logo, no_logo, blurred, material`), so one published list would visibly reorder one surface. `artwork-browser.ts` stays unchanged and no Steam asset rebuild is needed.
- **Tests:** B108: an `OverlayToolsTests` case that the logo filter page shows exactly the four logo styles with the defaults selected. B129: a table test that every tab's default styles are a subset of its offered styles and that each tab's offered style set equals the Steam page's set (the icon tab's default dimensions are intentionally wider than the offered ones on both surfaces, so dimensions are not asserted). `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayToolsTests"`; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ArtworkFilterOptions"`.
- **Plan v2:** B108 (logo list), B129 (one C# table; supersedes the "source publishes the offered vocabulary" part of B129's spec).
- **Related:** OVERLAY-017.

### OVERLAY-019: Two Windows energy-plan selection workflows

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/PowerSchemeSelection.cs:57-131` (`RunAsync`); `src/WSGM/Shell/NativeQamPowerProfileService.cs:38-90` (`SetPowerProfileAsync`); persist lambda `src/WSGM/Overlay/OverlayController.cs:552-553`.
- **Problem:** Both surfaces lock `PowerSchemes.MutationGate`, call `Select` and persist `LastSelectedPowerSchemeId` with separate error wording. The QAM copy also latches `_requiresRead = true` after every select and refuses the next selection ("Windows state must be refreshed before another selection") until a read clears it. That gates a control on readback, the same class as CRIT-003. The overlay copy does the same in its own way: `CanSelect` requires `ActiveId is not null`, a failed select sets `ActiveId = null` ("Refresh to read Windows state before trying again"), and a successful select publishes `ReadActive()` rather than the written id.
- **Best solution:** One select path in the session owner. After B090 the lock lives inside the injected `PowerSchemes` instance. Give `NativeQamPowerProfileService` (the session-owned instance) an internal `SelectAsync(Guid id, CancellationToken)`: validate against the offered ids, `Select`, persist the reference, publish the written id as active, and return the status text. It has no `_requiresRead` latch: a new explicit selection is the user action, and the readback only refreshes the published list. `SetPowerProfileAsync` parses the GUID and calls it. `SelectAsync` throws when Windows refuses the write and otherwise returns null, or the existing "Windows applied the profile, but WSGM could not save the reference: …" text when only the persist failed. `PowerSchemeSelection` takes `Func<Guid, CancellationToken, Task<string?>> select` from the controller instead of `schemes` plus `persist`. It keeps only the UI-thread projection, `readOnly`, busy state and the enumerate/read refresh (open and destination show). The overlay copy gates on readback too, and that goes with it: after a select it publishes the written id as `ActiveId` instead of running `Enumerate` and `ReadActive` again, a thrown select keeps the previous `ActiveId` and shows the error without "Refresh to read Windows state before trying again", and `CanSelect` drops `ActiveId is not null` (the scheme list being non-empty is the type check). The persist lambda in the controller and its `ConfigStore.Mutate` go.
- **Tests:** `PowerSchemeSelectionTests` over a fake select: a successful select shows the written id as active with no read call; a thrown select leaves the control selectable; a persist failure shows its text. A `NativeQamPowerProfileService` test where two consecutive selections both write (no latch) and a failed persist reports the existing "Windows applied the profile, but WSGM could not save the reference" text. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerSchemeSelection|FullyQualifiedName~NativeQamPowerProfile"`; UI `HybridCoreViewTests`.
- **Plan v2:** B130 (after B090 and B017). Add `NativeQamPowerProfileService.cs` latch removal to B130's file list, or to B017 if it lands there first.
- **Related:** WINSVC-009, WINSVC-030 (B090), WINSVC-010 and CRIT-003 (B017); critic conflict 13; OVERLAY-005.

### OVERLAY-020: Machine power actions run from view click handlers

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayWindow.Power.cs:46-107` (Standby, Hibernate, Restart, Shut down, Sign out call `Core/PowerActions`).
- **Problem:** A headless UI test that clicks Standby or Shut down acts on the test machine, and the session never learns that the user asked to end the session.
- **Best solution:** `ISessionPowerActions { void Standby(); void Hibernate(); void Restart(); void Shutdown(); void SignOut(); }` with one production implementation over `Core/PowerActions` built in `ShellSession`, passed to the controller and from it to `PowerPage`. The window raises intent and the controller calls the port after closing the sheet, exactly as today. Behaviour unchanged; tests use a recording fake. This extends plan rule L125 (fake action ports before admitting dangerous power tests) to the overlay.
- **Tests:** a UI test that clicks each confirmed power action and asserts the recording fake got exactly one call and no real API was reached. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"`.
- **Plan v2:** B122.
- **Related:** OVERLAY-001, -005; B002 (WDC `IPowerActionApi`, a different layer); review C18; M01-47.

### OVERLAY-022: Back and destination policy is split across the pure model and focus-dependent branches, held together by re-entrancy guards

- **Severity:** medium
- **Where:** `src/WSGM/Overlay/OverlayWindow.Navigation.cs:464-557` (`TryCancelSubView`, `CancelOpenPage`), `:193-263`, `:604-647` (`_showingDestination` guard against `ShowDestination -> RefreshDevicePanel -> ConfigureTabs -> RebuildDestinationTabs -> ShowDestination`); `src/WSGM/Overlay/OverlayWindow.Workspace.cs:24, 195-238` (`_selectingSection`); `src/WSGM/Overlay/OverlayWindow.Device.cs:235`.
- **Problem:** Back behaviour depends on which branch of focus state the window is in, cannot be unit-tested, and two boolean guards break a render cycle instead of removing it.
- **Best solution:** `OverlayNavigation.Back(BackContext context)` returns one `BackAction` (CloseSurface, NestedBack, FocusRail, ReturnHome, PopPage, CloseSheet). `BackContext` carries `HasActiveSurface` (from the B109 surface host), `HasNestedLevel`, `FocusInRail` and the current route, so the decision is pure. `OverlayNavigationController` executes the action. Destination visibility is updated only from the source change handlers (device and graphics visibility changed), so `RefreshDevicePanel` stops calling `ConfigureTabs`. That removes both cycles: `ShowDestination -> RefreshDevicePanel -> ConfigureTabs -> RebuildDestinationTabs -> ShowDestination` (`_showingDestination`) and `SelectWorkspaceSection -> RefreshDevicePanel or EnterDeviceSection -> ConfigureTabs -> SelectDestination or RebuildDestinationTabs -> ShowDestination -> RefreshWorkspace -> SelectWorkspaceSection` (`_selectingSection`, whose two checks in `RefreshWorkspace` exist only for that re-entry). `OnWorkspacePolicyChanged` posts its refresh, so it is not a re-entry path. After the change no call made inside `SelectWorkspaceSection` reaches `RefreshWorkspace`; verify that by reading the call graph in the B110 review, then delete both flags. `SelectWorkspaceSection` keeps its `HasActiveSurface` refusal.
- **Tests:** a table test in `tests/WSGM.Tests/Overlay/OverlayNavigationTests.cs` covering every `BackContext` combination; existing `ControllerNavigationTests` and `OverlayInteractionTests`. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayNavigationTests"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests|FullyQualifiedName~OverlayInteractionTests"`; Visual filter.
- **Plan v2:** B110 (depends only on the surface host, verifier batch problem 10).
- **Related:** OVERLAY-023, -034.

### OVERLAY-044: Tests copy production wiring and include getter and grab-bag tests

- **Severity:** medium
- **Where:** `tests/WSGM.UiTests/Overlay/ControllerNavigationTests.cs:56-60, 139-142, 158-161, 181-184` and `tests/WSGM.UiTests/Overlay/CommonPluginPanelTests.cs:203` (own `GamepadNavigation` lambda sets that differ from `OverlayController.cs:606-642`); `tests/WSGM.UiTests/Infrastructure/UiFixture.cs:170-176` (copies `DockToTopEdge`'s scale transform); `tests/WSGM.Tests/Overlay/QuickAccessSheetTests.cs` (swipe table, Settings snapshot getters, a virtual-key constant echo, `DisplayScale` tests, `WindowEntryPreservesTheActivationTargetAndPresentationState`); `EveryDestinationHasAUserFacingLabel`.
- **Problem:** Tests that copy the production predicates prove the copy, not the product (plan L38). Getter-only tests pin nothing.
- **Best solution:** Extract `OverlayInput.Create(OverlayWindow window, IGamepadButtonSource buttons, Action back)` from `OverlayController.cs:606-642`. The controller and both test files use it, and the copied lambda sets are deleted. `UiFixture` calls the production placement code instead of its own transform: extract from `DockToTopEdge` only the part the fixture copies (computing the content scale with `ComputeContentScale`, storing it in `_contentScale` and setting `RootScale.LayoutTransform`) as an internal `ApplyContentScale(double factor)` used by both. `DockToTopEdge` also sets `TrayScroller.MaxWidth`, which the fixture does not; if sharing more than the scale changes any capture, keep the fixture's narrower call, because baselines must not change. Move the `DisplayScale` tests to a Core test file and the Settings snapshot tests to `WSGM.Tests.Settings`. Delete the getter-only and constant-echo tests. Replace the swipe table with an `OverlayActivationTests` case that injects a swipe and observes the requested surface (after B113).
- **Tests:** the edited files; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay|FullyQualifiedName~WSGM.Tests.Controls"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"`.
- **Plan v2:** B131.
- **Related:** OVERLAY-002, -045; critic 1.3 (getter and copied-predicate audit).

### OVERLAY-V-001: A failed library lookup leaves a launch-fix row stuck on "Asking Steam…"

- **Severity:** medium (verifier-found)
- **Where:** `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs:248` (`_ = ApplyLaunchFixAsync(mode, button)`), `:299-321` (`ApplyLaunchFixAsync`), `:134-148` (`ResolveCurrentGameAsync`), `:415-425` (`SafeGameLookupAsync`).
- **Problem:** `ApplyLaunchFixAsync` sets the title to "Asking Steam…" and calls `SafeGameLookupAsync`, which throws whenever `OverlayLibraryLookup` reports an error (for example Steam's collection store not ready yet). The call is outside any try and the task is discarded, so the row stays on "Asking Steam…" and the exception only appears later as an `UnobservedTaskException` log line. `SteamCurrentPage.GetAsync` failing has the same effect.
- **Best solution:** Replace `SafeGameLookupAsync` with `LookupGameAsync(long appId)`, which returns the listed `SteamLibraryApp` or, on a missing entry or a failed lookup (logged once as a warning), the fallback `new SteamLibraryApp(appId, appId.ToString(CultureInfo.InvariantCulture), appId >= 0x80000000L)` that `ResolveCurrentGameAsync` already builds. Both `ResolveCurrentGameAsync` and `ApplyLaunchFixAsync` use it. Wrap the body of `ApplyLaunchFixAsync` in a try/catch that sets "Couldn't reach Steam" and logs, so every exit path leaves an outcome title. In B128 this becomes `LaunchFixService.ResolveGameAsync`.
- **Tests:** `LaunchFixServiceTests`: "lookup failure still applies or reports" (failing lookup plus page app id applies through the fallback; failing page read sets the outcome title). `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LaunchFixService"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayToolsTests"`.
- **Plan v2:** B128.
- **Related:** OVERLAY-005, -010.

### OVERLAY-V-002: Overlay Settings handoff creates a second Settings window

- **Severity:** medium, plausible (verifier-found)
- **Where:** `src/WSGM/Overlay/OverlayController.cs:837-863` (`new SettingsWindow(true)` on every request); `src/WSGM/Shell/DesktopTray.cs:52-64` (the session's single-instance owner).
- **Problem:** The sheet's Settings row always constructs a new window, never reuses the tray's, and is not gated by `_previewOnly`. Summoning the sheet over an open Settings window and pressing Settings opens a second one; inside Settings, Test sheet then Settings nests one more. Two view models edit independent snapshots and the later save silently discards the other's edits. Overlay also constructs Settings UI directly, a layering inversion.
- **Best solution:** `Shell/SettingsSurface.Open()` opens or activates one Settings window per process for the tray, the overlay and `SettingsActivation`. It takes the surface mode from the session's current mode (not the caller) and claims the Steam Input lease as the overlay path does today. The controller gets an `openSettings` delegate from the session instead of constructing `SettingsWindow`. The preview sheet hides the Settings row, the same way it hides the mode switch (`ModeSwitchAvailable`). This reuses the existing single-instance owner and adds no new mechanism.
- **Tests:** a second open activates the same window; an overlay open of an existing desktop window includes it as switchable; the preview sheet has no Settings row. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings|FullyQualifiedName~WSGM.UiTests.Overlay"`.
- **Plan v2:** B123 (settings domain, after B122).
- **Related:** SETTINGS-010, SETTINGS-011, SETTINGS-015, SETTINGS-016; INPUT-009 (B075).

### OVERLAY-C-001: The overlay's processor core preference publishes the readback and reports a write as failed when it cannot be read back

- **Severity:** medium (checker-found; breaks the no-hard-readback rule)
- **Where:** `src/WSGM/Overlay/HybridCoreSelection.cs` `RunAsync` (`cores.Apply(mode, token); return cores.Read();`, then `Status = status`, and the catch's "The processor core preference was not applied: …"); `src/WSGM/Core/HybridCores.cs:214-242` (`Apply` throws "Windows did not confirm the processor core preference" on a readback mismatch).
- **Problem:** After a selection the overlay shows what Windows reads back, not what WSGM wrote. If the readback differs, `Describe` says "The current preference was not set by WSGM" for a value WSGM just wrote, and if `Apply` throws on the mismatch (today) or the follow-up `Read()` throws, the row says "was not applied" although the write went out. B017 (CRIT-003, WINSVC-010) fixes `HybridCores.Apply` and the QAM service, but its file list does not include this overlay consumer, so the rule "publish the written value as observed" is not swept here.
- **Best solution:** In `RunAsync`, when `requested` is a mode: call `cores.Apply(mode, token)` and, if it returns, publish `Status with { OnAc = mode, OnBattery = mode }` and `Describe` it, without a follow-up `Read()`. Only an exception from `Apply` itself (a refused write) produces "was not applied: …", and it leaves `Status` and `CanSelect` as they were. The refresh path (`requested is null`, run on open and on destination show) keeps reading. No retry and no new state. Same model as OVERLAY-019's scheme selection and B017's QAM service.
- **Tests:** `HybridCoreViewTests` over the existing `FakeHybridCoreApi` behind a real `HybridCores`: a selection publishes the written mode for both sources with no read after it; a fake whose readback disagrees still shows the written mode; a refused write shows "was not applied" and the control stays selectable. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~HybridCoreViewTests"`.
- **Plan v2:** B130 (after B090, so after B017's `Apply` no longer throws on a mismatch). Add `src/WSGM/Overlay/HybridCoreSelection.cs` to B130's file list.
- **Related:** OVERLAY-019; CRIT-003, WINSVC-010 (B017).

## Low

### OVERLAY-013: Overlay Dispose leaves the UI capture claim to a deferred close

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Overlay/OverlayController.cs:264-328` (`Dispose`, stale comment at 276-279), `:886-936` (`OnOverlayClosed`); `src/WSGM/Shell/ShellSession.Shutdown.cs:73-85`.
- **Problem:** Dispose releases the Steam Input lease synchronously but leaves the UI capture claim (`UiSurfaceClosed` into the controller manager) to the 150 ms deferred close. If the dispatcher stops before the timer fires during shutdown, the claim is never released. The review's other effects do not hold: `ControllerManager.ReleaseUi` is a locked release that cannot throw, and the bridges are disposed well after 150 ms. The comment claiming only the Settings preview calls Dispose is wrong, since session shutdown calls it too.
- **Best solution:** In `Dispose`, inside the existing `if (_overlay is not null)` block, call `ReleaseUiSurface(QuickAccessSurface)` next to `ReleaseSteamInputLease()`. `ReleaseUiSurface` is already idempotent, so the deferred handler's second call only logs the existing "skipped" change line. Keep the deferred `Close()` for the ghost-click grace. Rewrite the stale comment to say that both the Settings preview and session shutdown call Dispose. No new guard.
- **Tests:** `OverlayControllerLifecycleTests.DisposeReleasesClaimAndLeaseSynchronously`: open, dispose, assert one `UiSurfaceClosed` and one lease drop before any dispatcher run. Filter `FullyQualifiedName~OverlayControllerLifecycle`.
- **Plan v2:** B122.
- **Related:** review C5; SESSION B140 shutdown order.

### OVERLAY-015: Animations browse fetches from a render thunk

- **Severity:** low (verifier lowered from medium and narrowed the trigger)
- **Where:** `src/WSGM/Overlay/AnimationsView.Tools.cs:53-56` (`if (browse.Total == 0 && !browse.Loading && browse.Error is null) Run(...BrowseAsync...)` inside `RenderMovieBrowse`); `src/WSGM/Overlay/AnimationsView.Tools.cs:12-16` (`SelectTab`).
- **Problem:** Each publication re-renders. With an empty repository (`Total == 0`, no error) every completed browse leaves the predicate true and the view loops fetches. Themes cannot loop (`BrowseAsync` sets `Loading` synchronously and a completed page sets `Page = 1`), so `ThemesView` stays as is.
- **Best solution:** Delete the render-time fetch. `SelectTab("browse")` starts the browse once when the current state has `Total == 0`, `!Loading` and no error, then calls `Replace(RenderHome)`. The browser session starts on the Library tab for every attach, so tab entry is the only way into Browse. Search, sort and Refresh keep their explicit fetches.
- **Tests:** `BrowseFetchesOnceForAnEmptyRepository` in `OverlayToolsTests` with a fake browser that publishes an empty, completed state: one `BrowseAsync` call after entering Browse and none after further publications. Filter `FullyQualifiedName~OverlayToolsTests`.
- **Plan v2:** B108.
- **Related:** CRIT-006 (browse sessions' fire-and-forget reads, a separate fix).

### OVERLAY-021: A detail-less plugin action outcome is mislabelled

- **Severity:** low (verifier lowered from medium; the IR rationale is refuted)
- **Where:** `src/WSGM/Overlay/CommonPluginPanel.cs:500-501`; `src/WSGM/Shell/CommonPluginSteamUiSource.cs:295`; `src/WSGM.Plugin.Ir/IrPlugin.cs:390, 401, 647`.
- **Problem:** The panel shows "Applied" only for `AppliedVerified`. Otherwise it shows the plugin's detail or, without one, "Change was not confirmed". IR returns `Dispatched` with the detail "IR emitted; appliance state is not verified.", so IR reads correctly. What survives: a `Dispatched` without detail reads as unconfirmed, where the Steam surface treats `Dispatched` as success, and a `Rejected` without detail reads "Change was not confirmed".
- **Best solution:** A plugin's detail always wins when present, so the honest "not verified" text is never replaced. Otherwise: `AppliedVerified` and `Dispatched` read "Applied" (matching the Steam surface and the rule that a write is published as observed), `Unconfirmed` keeps "Change was not confirmed", and `Rejected` reads "Not applied". `AppliedVerified` keeps "Applied" even with a detail, as today.
- **Tests:** `CommonPluginPanelTests`: Dispatched without detail reads "Applied", Dispatched with detail shows the detail, Rejected without detail reads "Not applied", Unconfirmed reads "Change was not confirmed". Filter `FullyQualifiedName~CommonPluginPanelTests`.
- **Plan v2:** B108.
- **Related:** none.

### OVERLAY-023: Arbitrary navigation depth cap

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayNavigation.cs:134` (`MaximumDepth = 8`), `:136`, `:233` (`Push` refuses at depth 8); `src/WSGM/Overlay/OverlayWindow.Navigation.cs:734` (`LeaveAllNestedPages` loop bound).
- **Problem:** The page graph bounds depth at four. The cap adds a refusal path no legitimate route reaches and breaks the no-arbitrary-limits rule.
- **Best solution:** Delete `MaximumDepth`. `Push` refuses only a page from another destination. The stack is `new List<OverlayRoute>()`. `LeaveAllNestedPages` loops `while (AnySubView && _navigation.Depth > 1) LeaveActiveSubView();`. `Pop` removes one route whenever the depth is above one, so each pass makes progress and the loop ends without a count bound or an extra progress flag.
- **Tests:** `OverlayNavigationTests`: push to depth 10 within one destination succeeds; a cross-destination push is refused. Filter `FullyQualifiedName~OverlayNavigationTests`.
- **Plan v2:** B110.
- **Related:** OVERLAY-022.

### OVERLAY-024: Arbitrary text-entry caps

- **Severity:** low
- **Where:** `src/WSGM/Overlay/LibraryTabsView.cs:252` (40), `CardManagerView.cs:108` (40), `GameLibraryView.Tools.cs:57` (32), searches and names at 64 or 128 (`ThemesView.Tools.cs:34, 93`, `AnimationsView.Tools.cs:46`, `SoundsView.cs:170`, `ArtworkView.cs:83, 332`, `GameLibraryView.Tools.cs:117, 290`), 256 (`ThemesView.Tools.cs:167`, Wi-Fi entry in `OverlayWindow.Surfaces.cs:78`), 1024 (`OverlayWindow.Surfaces.cs:111`, local path), 2048 (`LaunchWrapperView.cs:83`), 4096 fallbacks (`DeviceControlRows.cs:164`, `CommonPluginPanel.cs:543`, plus 64 for number fields), `ApplicationProfilesView.cs:25` (`MaxLength = 80`), `OverlayWindow.Storage.cs:33` (32).
- **Problem:** `KeyboardPanel` stops typing at the cap, which silently truncates valid input (long search terms, launch arguments, paths, theme values) with no contract behind the number.
- **Best solution:** Pass 0 (`KeyboardPanel` treats 0 as unbounded) at every site. Keep only contract-backed values: a device capability's declared `MaximumLength` (`maximumLength ?? 0` in `DeviceControlRows`), the 7 of the `#RRGGBB` entry in `DeviceColorView.EditHex` (the format itself, parsed as the type check) and the NTFS volume label length for the SD format name. For the label, add `SdFormatManager.MaximumLabelLength = 32`, used by both `SanitizeLabel` and `OnFormatEditName`, because the format script runs `format fs=ntfs ... label=`. Wi-Fi credentials go unbounded: B064's typed connect outcome reports an invalid key, which is a type check rather than an entry cap. Plugin fields declare no length in the SDK, so text and number fields both pass 0; number parsing stays the type check. `ApplicationProfilesView` drops `MaxLength = 80` together with B038's removal of the 80-character profile-name cap. D2 lists no overlay bound, so nothing else stays.
- **Tests:** `KeyboardEditingTests`: a 300-character search and a 3,000-character launch argument are accepted whole. Filter `FullyQualifiedName~KeyboardEditingTests|FullyQualifiedName~OverlayToolsTests`.
- **Plan v2:** B129. Widen its file list to every site above (`OverlayWindow.Surfaces.cs`, `OverlayWindow.Storage.cs`, `DeviceControlRows.cs`, `CommonPluginPanel.cs`, `LibraryTabsView.cs`, `CardManagerView.cs`, `ApplicationProfilesView.cs`, `SdFormatManager.cs`). Decided: D2, only plan v2's listed byte bounds stay and none of them is an overlay entry cap.
- **Related:** CONFIG-B1 caps (B038); OVERLAY-031.

### OVERLAY-025: Four paging mechanisms

- **Severity:** low
- **Where:** `src/WSGM/Overlay/LibraryTabsView.cs:729-771` and `LaunchWrapperView.cs:136-193` (200-row pages); `OverlayFilePicker.cs:21, 123, 148-155` (100, "Show more"); `GameLibraryView.Tools.cs:161-190` and `ArtworkView.cs:103-117` (48, "Load more"); `SoundsView.cs:191` (service page size 24 hard-coded); `CardManagerView.cs:196-201` (unpaged).
- **Problem:** The same "append a chunk and keep focus" logic exists four times, and Sounds duplicates the service's page size as a literal. Chunking is allowed and nothing is truncated.
- **Best solution:** One `src/WSGM/Controls/PagedRows.cs` helper: `PagedRows.Render(Panel host, int total, int shown, string label, string tag, Action showMore)` appends the existing more-row, and after `showMore` re-renders, focuses the first newly added row. Each call site keeps its chunk size, label and tag so the UI is identical. `SoundsView`'s Next-page check uses a `ThemeStoreClient.SoundsPageSize = 24` constant that `QuerySoundsAsync` also puts into its `perPage=24` query (`Core/Themes/ThemeStoreClient.cs:209`), instead of its own literal 24; the service state carries no page size, so no state or wire field is added. `CardManagerView` stays unpaged (no defect).
- **Tests:** `OverlayToolsTests` "load more focuses the first new row" for the library review; existing captures identical. Filter `FullyQualifiedName~OverlayToolsTests`; Visual filter.
- **Plan v2:** B129.
- **Related:** OVERLAY-026; critic conflict 7 (no picker paging change).

### OVERLAY-026: Picker navigation loses controller position

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayFilePicker.cs:41-53, 148-155`; `src/WSGM/Overlay/OverlayWindow.Surfaces.cs:313` (preferred focus captured once).
- **Problem:** Each folder change or "Show more" replaces `Content`, the captured focus target is detached, and focus falls back to the top of the surface.
- **Best solution:** Keep the simple picker (`OverlayFilePicker` is not the toolkit picker; plan B2/B059 machinery does not apply, review C12 and critic conflict 7). After a folder navigation, focus the first entry. After "Show more", focus the first new entry through `PagedRows` (OVERLAY-025). "Enter a path" keeps `Path.GetFullPath`, and UNC still works.
- **Tests:** new `tests/WSGM.UiTests/Overlay/OverlayFilePickerTests.cs` over temporary directories: a local folder, 450 entries with two "Show more" presses (focus on the first new entry), an inaccessible folder, and Enter-a-path normalization of a UNC string. Filter `FullyQualifiedName~OverlayFilePicker`.
- **Plan v2:** B129.
- **Related:** OVERLAY-025, -045.

### OVERLAY-027: Add Steam Library opens a native picker without suspending navigation

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayWindow.Storage.cs:140-175` (`AddLibraryAsync`); reference pattern `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs:76-94`.
- **Problem:** Unlike the launch-action picker, the folder picker does not raise `SystemDialogActive`, so the controller can drive the overlay behind the dialog.
- **Best solution:** Wrap `OpenFolderPickerAsync` in `SystemDialogActive?.Invoke(true)` / `finally { SystemDialogActive?.Invoke(false); }`, exactly as the launch-action picker does. Keep the native picker: decided, the Add Steam Library and Replace launch action pickers stay native with navigation suspended while open (DECISIONS.md).
- **Tests:** a UI test that attaches `new SdFormatManager()` (its constructor has no side effects), presses Add Steam Library and asserts `SystemDialogActive` raised true then false. Avalonia's headless platform supplies its no-op storage provider, which returns no folder, so no fake provider or new seam is needed. Filter `FullyQualifiedName~WSGM.UiTests.Overlay`.
- **Plan v2:** B108.
- **Related:** OVERLAY-V-003.

### OVERLAY-029: Async-void paths that can throw into the dispatcher

- **Severity:** low
- **Where:** `src/WSGM/Controls/ProfileOverrideMarker.cs:59, 83-108` (`_useGlobalButton.Click += async ... await ResetAsync()`, try/finally with no catch; `OverlayWindow.Sources.cs:69-76` passes an unwrapped `ClearGameOverrideAsync`); `src/WSGM/Overlay/AudioPanel.axaml.cs:159-185` (`OnFormatChanged`, `OnSpatialChanged`); `src/WSGM/Overlay/OverlayController.cs:821-830` (UAC and lock-on-wake lambdas).
- **Problem:** A throwing awaited call in an async-void handler reaches the dispatcher's unhandled-exception path.
- **Best solution:** `ProfileOverrideMarker.ResetAsync` adds `catch (Exception ex) { Log.Error("Could not return the setting to the Global value", ex); }` before its `finally`. The two `AudioPanel` handlers wrap the set call in try/catch, log, put `ex.Message` in `CapabilityStatus.Text`, and still run `RefreshCapabilitiesAsync` so the shown values match Windows. The UAC and lock lambdas wrap the `Task.Run` in try/catch with `Log.Error` and refresh the policies in both cases. No retry. The marker is `src/WSGM/Controls/ProfileOverrideMarker.cs`; B108's file list names it under `src/WSGM/Overlay/` by mistake.
- **Tests:** a marker test with a throwing reset delegate (no exception escapes, button re-enabled). Filter `FullyQualifiedName~WSGM.UiTests.Overlay`.
- **Plan v2:** B108.
- **Related:** none.

### OVERLAY-030: The window reaches into DeviceCoordinator profile internals and a Steam-surface helper

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayWindow.Sources.cs:52-77` (`coordinator.ManualTdpMode`, `NativeQamUi.OverrideId(coordinator.Profiles.Current.Layers, ...)`, `coordinator.Profiles.ClearGameOverrideAsync`); `src/WSGM/Overlay/OverlaySources.cs:24` (exposes the whole `DeviceCoordinator` as `ManualTdp`).
- **Problem:** The overlay depends on profile-layer internals and on a Steam QAM helper to render one row.
- **Best solution:** `ManualTdpModeView` already takes four delegates, so no interface is needed. The device facade from B093 gains `ManualTdpOverrideId()` and `UseGlobalManualTdpAsync()`, which wrap the profile-layer lookup that belongs to the device owner. `OverlaySources.ManualTdp` becomes `ManualTdpModeAccess(Func<ManualTdpMode> Read, Func<bool, Task> Set, Func<string?> OverrideId, Func<Task> UseGlobal)`, built by `ShellSession`. The window passes it through. The `NativeQamUi` reference leaves the overlay.
- **Tests:** `ManualTdpModeTests` over a fake access record. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ManualTdpModeTests"`.
- **Plan v2:** B130 (after B093).
- **Related:** DEVICE-022, DEVICE-023 (B093); OVERLAY-028.

### OVERLAY-031: The application-profile editor's Name and Process fields cannot be reached with a controller

- **Severity:** low (reopened by DECISIONS.md; plan v2 had closed it as a visible change)
- **Where:** `src/WSGM/Overlay/ApplicationProfilesView.cs` (`_name`, a `TextBox` with `MaxLength = 80`; `_processes`, a multi-line `TextBox`; `ProcessNames()`, `NewProfile()`, `Load`, the "Add current application's process" button); host `src/WSGM/Overlay/OverlayWindow.Header.cs:22`; tests `tests/WSGM.UiTests/Overlay/ApplicationProfilesViewTests.cs:63-84`, captures `overlay-profiles-720p.png` and `overlay-profiles-4k-scaled.png`.
- **Problem:** `GamepadNavigation` skips `TextBox`es so the Windows touch keyboard never pops (`OverlaySubView.EditText` explains the rule, and `docs\overlay-and-input.md` calls text entry in the panel a press-to-edit row). The profile editor's two text fields are raw `TextBox`es, so a controller user cannot name a profile or edit its activation processes; only touch or a hardware keyboard can.
- **Best solution:** Convert both fields to press-to-edit rows, the same shape as `DeviceControlRows.Text` and `OverlaySubView.EditText`, over the surface host's `RequestText` from B109 (OVERLAY-014). Name: a `Button` with the `deck-action` class whose content is the draft name, or "Profile name" when empty; pressing it calls `RequestText("Profile name", draft, 0, accept)` (0 is unbounded, OVERLAY-024 and B038 drop the 80-character cap) and the accept sets the draft and the button content. Processes: the view keeps a `List<string> _processNames` draft instead of the multi-line text. It renders one button row per name (pressing it opens the keyboard with that name; accepting an empty value removes the entry, any other value replaces it) followed by an "Add process" row that opens an empty keyboard and appends a non-empty, case-insensitively new name. "Add current application's process" appends to the same list. `ProcessNames()` returns the list, so `SaveAsync` and `PerformanceOverlayBridge.SaveProfileAsync` are unchanged. `Load` fills both drafts and re-renders the rows; `NewProfile()` focuses the name row without opening the keyboard. When `RequestText` returns false the status line says the keyboard is unavailable, as `DeviceControlRows.Text` does. The explanatory text about one executable per line becomes "One executable name per row, including .exe. ...". No new control type: the rows are plain buttons in the existing `StackPanel`.
- **Tests:** rewrite `EditorCreatesEditsAndDeletesProfilesWithoutARunningApplication` to drive the rows through the host's keyboard surface (press the name row, accept "My game"; add two processes; edit one; clear one to remove it; save; reload shows the names). Add a `ControllerNavigationTests` case that directional focus reaches the name row and the Add process row. This is the one overlay batch that changes baselines: run `eng\update-ui-baselines.ps1`, review `overlay-profiles-720p.png` and `overlay-profiles-4k-scaled.png`, and confirm no other image changed. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ApplicationProfilesViewTests|FullyQualifiedName~ControllerNavigationTests"`; Visual filter.
- **Plan v2:** decided: the Name and Process fields become controller-reachable press-to-edit rows (DECISIONS.md). New overlay batch appended after B109 (surface host) and B129 (cap removal); not yet in `batches.json`.
- **Related:** OVERLAY-014, -024; CONFIG B038 (80-character profile-name cap).

### OVERLAY-033: Duplicate run-button block

- **Severity:** low
- **Where:** `src/WSGM/Overlay/DeviceCapabilityControl.cs:65-98` and `src/WSGM/Overlay/DescriptorControlView.cs:44-76` (identical `_invoking`, disable and focus-restore code).
- **Problem:** Two copies of the same invoke button behaviour drift apart.
- **Best solution:** One small `src/WSGM/Overlay/InvokeButtonRow.cs` class (not a `Button` subclass, so styles keep matching `Button`): `new InvokeButtonRow(object content, Func<bool> canInvoke, Func<Task> invoke)` creates the plain `Button`, owns the click handler with the in-flight flag, disable-while-running and the focus restore, and exposes `Button` and `bool Invoking`. Both views keep wrapping `Button` in their `DeviceSettingRow` and replace their `_invoking` field with `Invoking` in their refresh paths (`DeviceCapabilityControl.cs:130`, `DescriptorControlView.cs:105`), which read it today. `canInvoke` reads the view's current `_capability`/`_descriptor`, as the copies do. Visuals identical.
- **Tests:** existing device and performance captures identical; one UI test that a second press while running is ignored. Filter `FullyQualifiedName~WSGM.UiTests.Overlay`.
- **Plan v2:** B126.
- **Related:** OVERLAY-007, -032.

### OVERLAY-034: Identity carried in strings on Control.Tag

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayWindow.Sections.cs:63-65, 212-213, 229-241`; `src/WSGM/Overlay/OverlayWindow.Device.cs:468-470` (`Replace("device.section.", "section.device.")`); index placeholders `new Control { IsVisible = false }` in `OverlayWindow.Sections.cs:63-65`, `OverlayWindow.Performance.cs:91`; `src/WSGM/Overlay/OverlayWindow.Workspace.cs:31-45` (reacts to any property name starting with "Show").
- **Problem:** Two key forms are converted by string replacement, reconciliation depends on invisible placeholders that keep indexes stable, and a property-name prefix test reacts to unrelated properties.
- **Best solution:** A `SectionKey` record struct with `FocusKey` and `PinKey` properties that produce today's exact strings (`section.device.<id>` and `device.section.<id>`). Every `Replace` and every Tag rewrite uses it. `OnWorkspacePolicyChanged` switches on an explicit `nameof` list of the workspace policy properties. String values are unchanged (review C3). The invisible index placeholders cannot go in B110: they exist for the index-based reconcilers, which OVERLAY-035 replaces in B127, and they are deleted there.
- **Tests:** a unit test that `SectionKey` round-trips both forms for every current section id; `ControllerNavigationTests` focus-memory cases unchanged. Filter `FullyQualifiedName~OverlayNavigationTests|FullyQualifiedName~ControllerNavigationTests`.
- **Plan v2:** B110.
- **Related:** OVERLAY-035.

### OVERLAY-035: Five hand-written keyed reconcilers

- **Severity:** low
- **Where:** `src/WSGM/Overlay/ServiceSubView.cs:121-188`, `OverlayWindow.Performance.cs:61-98`, `OverlayWindow.Workspace.cs:115-160`, `OverlayWindow.Pins.cs:102-229`, `AppSwitcherViewModel.cs`.
- **Problem:** The three window reconcilers implement the same keep-by-key, insert, remove and reorder logic separately.
- **Best solution:** Keep `AppSwitcherViewModel` (bound collections) and `ServiceSubView.Reconcile`. Move the three window reconcilers (performance rows, section rail, pins) onto one `ReconcileChildren(Panel panel, IReadOnlyList<(string Key, Func<Control> Create, Action<Control> Update)> rows)` helper inside the page controllers. It reuses controls by key so focus survives, which also removes the invisible placeholders (`OverlayWindow.Sections.cs:65`, `OverlayWindow.Performance.cs:91`) left over from OVERLAY-034.
- **Tests:** `DeviceRowReconciliationTests` plus a helper test: reorder keeps instances, removal drops only the removed key, focus stays on a kept row. Filter `FullyQualifiedName~DeviceRowReconciliationTests|FullyQualifiedName~WSGM.UiTests.Overlay`.
- **Plan v2:** B127.
- **Related:** OVERLAY-034; review C7.

### OVERLAY-036: PhysicalGlyphService caches more than it can use and sits in the wrong layer

- **Severity:** low
- **Where:** `src/WSGM/Controls/PhysicalGlyphService.cs:53-160, 247-261, 375-380`; consumer `src/WSGM/Shell/DeviceOverlayBridge.cs:322, 347, 776`.
- **Problem:** The cache key includes theme and scale bucket, which `BuildPlan` never uses, so one plan is stored up to 45 times. The byte budget counts asset bytes the imported profile already retains. A non-visual resolver lives in Controls although its only consumer is in Shell.
- **Best solution:** Move it to `src/WSGM/Shell/PhysicalGlyphPlans.cs`: a plain `Dictionary<(string ProfileId, int Revision, GlyphControlId Control), PhysicalGlyphRenderPlan>` under the existing lock, cleared when the catalog changes. Its size is bounded by the catalog's profiles times their controls, so it needs no eviction. Delete the LRU, the byte accounting, `EstimateCost`, `CachedBytes` and the cache-size constructor parameters. `Resolve` drops its now-unused `theme` and `scale` parameters, and its callers in `DeviceOverlayBridge` follow; the surface authorization check stays. `PhysicalGlyphRenderPlan`, its enums, `PhysicalGlyphImage` and `ToAvaloniaPathData` (if Avalonia-typed) stay in Controls.
- **Tests:** rewrite `tests/WSGM.Tests/Controls/PhysicalGlyphServiceTests.cs` as `PhysicalGlyphPlansTests`: same plan for every theme and scale, catalog change clears. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PhysicalGlyph"`.
- **Plan v2:** B130.
- **Related:** Device API 12 glyph cap removals.

### OVERLAY-037: Preview images re-run visibility checks on every tree-wide layout pass

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayPreviewImage.cs:39-91` (`LayoutUpdated += LoadWhenVisible` per unloaded image plus a `ScrollChanged` subscription and an ancestor walk per check).
- **Problem:** `LayoutUpdated` fires for every layout pass of the whole tree, so a grid of unloaded previews runs transform math on each pass.
- **Best solution:** Subscribe to `EffectiveViewportChanged` on attach and unsubscribe on detach. Load when `IsEffectivelyVisible`, `Bounds.Width > 0` and `e.EffectiveViewport.Intersects(new Rect(Bounds.Size))`. Delete `_scroller`, `OnScroll` and the ancestor lookup. Load and cancel behaviour is otherwise unchanged.
- **Tests:** `OverlayToolsTests`: an image inside a scroll viewer loads only after being scrolled into view, and an image on a page that starts hidden loads once the page is shown (`LayoutUpdated` covers that case today, so prove `EffectiveViewportChanged` does too before deleting it); captures identical. Filter `FullyQualifiedName~OverlayToolsTests`; Visual filter.
- **Plan v2:** B132.
- **Related:** none.

### OVERLAY-038: Movie preview temp files and a second downloader

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayMediaPreview.cs:68` (static `HttpClient`), `:193-201` (`%TEMP%\WSGM-media-<guid>`), `:330-373` (`DownloadAsync` with its own copy loop); `src/WSGM/Core/Animations/AnimationRepoClient.cs:153-190`.
- **Problem:** A crash leaves `WSGM-media-*` folders in the user's temp folder forever. The review also counted the control's own `HttpClient` download and local copy loop as a second downloader; that part is not a defect: the remote branch already copies through the shared `BoundedHttp.CopyAsync` with `AnimationRepoClient.MaximumMovieBytes`, the D2 64 MB bound, and one static `HttpClient` per process is the normal .NET pattern. Threading a download delegate through `IAnimationBrowseSession`, `AnimationsView` and `ArtworkView` (the two creators) would add plumbing with no defect behind it.
- **Best solution:** Fix only the leak. Each preview's folder moves under one fixed root, `<UserDataContext.Root>\MediaPreview\Files\<guid>` (after B037; today's equivalent is `%LOCALAPPDATA%\WSGM\MediaPreview\Files`, beside the WebView2 profile the control already uses). Add `internal static void OverlayMediaPreview.DeleteStaleFiles(string root)`, which deletes the `Files` folder and logs (does not throw on) IO or access errors, and call it once from `ShellSession` start before any sheet exists. Each preview still deletes its own folder on detach. The static `HttpClient`, the remote branch and the bounded local copy stay as they are.
- **Tests:** `DeleteStaleFiles` over a temporary root removes leftover folders and ignores a locked file. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayMediaPreview"`. No WebView2 lifetime test: the headless platform creates no native handle for a `NativeControlHost`, and no environment-factory seam exists to fake one.
- **Plan v2:** B132 (after B037). Supersedes the "second downloader goes" part of B132's spec.
- **Related:** OVERLAY-005, -045; D2 (animation 64 MB bound).

### OVERLAY-039: Overlay depends on Settings for a label

- **Severity:** low
- **Where:** `src/WSGM/Overlay/AudioPanel.axaml.cs:11, 112` (`AudioProfileEditor.SpatialName`).
- **Problem:** The overlay references a Settings UI type for a spatial-audio label.
- **Best solution:** B120 already moves `SpatialAudioNames` and `SpatialAudioOption` to Shell beside `AudioProfileService` (SETTINGS-014) and edits `AudioPanel.axaml.cs` to use it. That is the target, not Core as the review said. B132 only verifies that no `WSGM.Settings` reference remains in `src/WSGM/Overlay`.
- **Tests:** build only; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioPlaybackChoices"`.
- **Plan v2:** B120 (implementation), listed again in B132 (check).
- **Related:** SETTINGS-014.

### OVERLAY-042: DevicePowerPresetSelection disposal mechanism

- **Severity:** low
- **Where:** `src/WSGM/Overlay/DevicePowerPresetSelection.cs:23-39, 74-85, 111-118` (three conditional `_lifetime.Dispose()` sites keyed on `Busy` and `_refreshing`).
- **Problem:** Bookkeeping that decides who disposes a `CancellationTokenSource` with no timer, which needs no disposal.
- **Best solution:** `Dispose` sets `_disposed`, calls `_lifetime.Cancel()` and clears `Changed`. Delete the other two dispose sites and their conditions. In-flight work keeps its token as today.
- **Tests:** existing `DevicePowerSectionTests`; a test disposing during an assignment shows no exception. Filter `FullyQualifiedName~DevicePowerSectionTests`.
- **Plan v2:** B132.
- **Related:** none.

### OVERLAY-043: Idle-timeout and Windows-policy reads run synchronously on the UI thread at every open

- **Severity:** low (reopened by DECISIONS.md; plan v2 had closed it because values must not appear a frame later)
- **Where:** `src/WSGM/Overlay/OverlayController.Power.cs` `RefreshPowerTimeouts` (`PowerTimeouts.ReadAll()`) and `RefreshWindowsPolicies` (`UacSettings.Read()`, `LockScreenSettings.SignInOnWakeDisabled()`); callers in `src/WSGM/Overlay/OverlayController.cs` `ShowOverlayCore` (`RefreshPowerTimeouts(vm)` and `RefreshWindowsPolicies(_overlay)`), the reopen path (`RefreshPowerTimeouts(_overlayViewModel)`), the `powerSchemes.Changed` handler, the `PowerTimeoutSelected` continuation, the UAC and lock-on-wake lambdas, and `OnDisplayTimeoutsChanged`.
- **Problem:** Opening the sheet reads the active power scheme's four idle timeouts and two registry policies on the UI thread before the window shows, and repeats the timeout read on every reopen and scheme change. Native power and registry calls can stall, and the sheet waits on them. (The verifier corrected the review's "two scans per open" to one.)
- **Best solution:** Split each refresh into a worker read and a UI apply, and accept that the values fill in a frame later (DECISIONS.md). `RefreshPowerTimeouts(vm)` chains its read on one controller field, `_windowsReads = _windowsReads.ContinueWith(_ => ReadPowerTimeouts(vm), TaskScheduler.Default)`, the same chaining pattern as the pin writes (OVERLAY-012). The worker step calls `PowerTimeouts.ReadAll()` (logging and returning all nulls if it throws) and posts `ApplyPowerTimeouts(vm, timeouts)`, which returns when `_disposed` or `!ReferenceEquals(_overlayViewModel, vm)` and otherwise sets the view-model properties the method sets today; `_displayTimeouts?.Minimum` is in memory and stays in the apply. `RefreshWindowsPolicies(overlay)` chains on the same field: the worker reads both policies and posts `overlay.RefreshWindowsPolicies(...)` when `ReferenceEquals(_overlay, overlay)`. Chained reads finish in request order and same-priority posts run in order, so an older read never overwrites a newer one, with no counter or generation. Until the first values land, the badges show the placeholder `Format` already uses for a missing value, the timeout editors stay disabled (`current is not null` is already their condition) and the policy toggles stay disabled (`AddWindowsPolicyRow` already creates them disabled until Windows has been read). Every caller listed above calls the same two methods, so nothing else changes. Hybrid cores and power schemes already read off the UI thread.
- **Tests:** an `OverlayControllerLifecycleTests` case: after open, before the dispatcher runs the posted apply, the timeout badges show the missing-value placeholder and the policy toggles are disabled; after the reads complete and the dispatcher runs, the badges hold values and the toggles are enabled (not in a preview). A second case closes the sheet before the apply runs and sees no exception and no write to the closed sheet's view model. The reads are read-only Windows queries, so the headless run touches no machine state. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayControllerLifecycle"`; UI filter `FullyQualifiedName~WSGM.UiTests.Overlay` and the Visual filter with identical images.
- **Plan v2:** decided: the sheet's idle-timeout and Windows-policy reads move off the UI thread and fill in a frame later (DECISIONS.md). New overlay batch appended after B122 (it extends the lifecycle suite); not yet in `batches.json`.
- **Related:** OVERLAY-001, -003, -012, -029; WINSVC-010 (B017).

### OVERLAY-045: Missing tests for risky overlay mechanisms

- **Severity:** low
- **Where:** no tests for `TouchSwipeMonitor` arm, disarm and teardown (the recognizer is covered by `tests/WSGM.Tests/Overlay/TouchSwipeMonitorTests.cs`), `OverlayFilePicker`, the launch-fix workflow, pin persistence order, controller Dispose order, or `OverlayMediaPreview` lifetime.
- **Problem:** The mechanisms most likely to regress during the moves have no safety net.
- **Best solution:** Each test lands in the batch that touches its area: launch fixes in B053/B128 (`LaunchFixServiceTests`), pins and Dispose in B122 (`OverlayControllerLifecycleTests`), picker in B129 (`OverlayFilePickerTests`), media preview leftovers in B132 (`DeleteStaleFiles`, OVERLAY-038). B079 already adds the touch registration tests (lives until the last subscriber, a disposed subscriber receives nothing) and B113 the activation tests, so B131 adds only the missing arm/disarm case to `OverlayActivationTests` (a disarmed activation raises no request for an injected swipe) rather than a duplicate `TouchSwipeMonitorLifetimeTests` file. No `OverlayMediaPreview` WebView2 lifetime test: the headless platform creates no native handle for a `NativeControlHost`, and adding an environment-factory seam only for a test is mechanism without a defect. B131's file list entries "TouchSwipeMonitorLifetimeTests (new)" and "OverlayMediaPreview lifetime test (new)" drop accordingly.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay|FullyQualifiedName~WSGM.Tests.Controls"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"`.
- **Plan v2:** B131 (plus the batches named).
- **Related:** OVERLAY-001, -012, -013, -026, -038, -044; INPUT-030.

### OVERLAY-V-003: Add Steam Library continues after the sheet closed behind the native picker

- **Severity:** low (verifier-found)
- **Where:** `src/WSGM/Overlay/OverlayWindow.Storage.cs:140-175` (no `_closed` check after `OpenFolderPickerAsync`); reference `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs:97`.
- **Problem:** If the sheet is dismissed while the dialog is up (hotkey toggle, OEM button), the code navigates a closed window (`OpenStorageFormat`) and starts `_format.AddLibraryAsync(path)` with no visible progress. The result then pops the sheet through `OnFormatFinished`.
- **Best solution:** Together with OVERLAY-027's pairing, return right after the picker when `_closed || folders.Count == 0`, exactly as the launch-action picker does. Nothing is started for a closed sheet.
- **Tests:** none automated. The headless storage provider never returns a folder and `SdFormatManager` is concrete, so a test would need a picker or format seam added only for it. The B108 review checks the `_closed` return by reading it against the launch-action picker; manual: open Add Steam Library, dismiss the sheet with the hotkey, pick a folder, and confirm nothing starts. OVERLAY-027's test still covers the dialog pairing.
- **Plan v2:** B108.
- **Related:** OVERLAY-027.

### OVERLAY-V-004: PinnedPluginWidgets and CommonPluginPanel latch closed on first detach

- **Severity:** low (verifier-found, latent)
- **Where:** `src/WSGM/Overlay/PinnedPluginWidgets.cs:29` (`DetachedFromVisualTree += ... _closed = true`, never reset); `src/WSGM/Overlay/CommonPluginPanel.cs:55` (`_closed.Cancel()` on a CTS never recreated); correct pattern in `src/WSGM/Overlay/ManualTdpModeView.cs:50`.
- **Problem:** `VisiblePoll` restarts on re-attach, but the refresh returns at once and actions run with a cancelled token. Today the controls are created per sheet under hosts that only toggle `IsVisible`, so this is latent. A B127 move that re-parents them would make the widgets go dead.
- **Best solution:** Only if B127 re-parents either control; if its hosts still only toggle `IsVisible`, record it as no-change in the B127 review. `PinnedPluginWidgets` adds `AttachedToVisualTree += (_, _) => _closed = false;`. `CommonPluginPanel` makes `_closed` non-readonly and on attach replaces a cancelled source with `new CancellationTokenSource()` (no disposal needed, it has no timer). Same shape as `ManualTdpModeView`.
- **Tests:** a B127 UI test that detaches and re-attaches a pinned widget and a panel, then sees a refresh and a successful action. Filter `FullyQualifiedName~CommonPluginPanelTests`.
- **Plan v2:** B127.
- **Related:** OVERLAY-006, -028.

## Nit

### OVERLAY-046: Private StyledProperties behind public wrappers

- **Severity:** nit
- **Where:** `src/WSGM/Controls/TabStrip.cs:79-84`; `src/WSGM/Controls/OnScreenKeyboard.cs:14`; `src/WSGM/Controls/CurveEditor.cs` (`SelectedIndex`, `MarkerInput`, `RisingOutput`).
- **Problem:** `Controls/AGENTS.md` names StyledProperty surfaces as the public contract, but these are private, so styles and bindings cannot target them.
- **Best solution:** Make the `StyledProperty` fields `public static readonly`, with names and defaults unchanged. No visual change.
- **Tests:** build; Visual filter identical.
- **Plan v2:** B132.
- **Related:** none.

### OVERLAY-047: The curve editor keeps a 64-point cap nothing else enforces

- **Severity:** nit (critic CRIT-005 changes the fix)
- **Where:** `src/WSGM/Controls/CurveEditing.cs:44-54, 162-166` (`MaximumPoints = 64` and the add refusal); `src/WSGM/Controls/CurveEditor.cs:129-130, 305` (refusal log lines); `tests/WSGM.Tests/Controls/CurveEditingTests.cs:20, 118`.
- **Problem:** The review asked for one shared constant. But B038 (CRIT-005) deletes `DeviceProfileValidation.MaximumPoints`, and `DeviceCapabilityRouter.CurveIsValid` already enforces only point order and the descriptor's bounds. After B038 the editor's 64 matches no contract and refuses curves the device would accept.
- **Best solution:** Delete `CurveEditing.MaximumPoints` and its three refusals: the count check in `CurveEditing.Add`, the `case >= CurveEditing.MaximumPoints` in `CurveEditor.AddPointAtWidestGap`, and the pointer-add `ReferenceEquals(updated, Points)` branch with its "point limit" log line, which becomes dead because `Add` then always returns a new list. Adding a point is limited only by what the existing rules already imply: strictly ascending integer inputs inside the bounds, so `AddPointAtWidestGap` refuses only when no gap of at least two input units remains. Update the class remark ("between 1 and 64 points") to "at least one point". This supersedes plan v2's "one curve point limit declaration" because the other declarations are gone.
- **Tests:** `CurveEditingTests`: the helper validator drops its 64 check and the 64-point case at line 118 becomes a 65th-point-added case; `CurveEditorTests` line 59-67 (fills to `MaximumPoints - 1`) is rewritten to add past 64 when a gap allows it. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CurveEditing|FullyQualifiedName~CurveEditor"`.
- **Plan v2:** B132 (after B038).
- **Related:** CRIT-005 (B038).

### OVERLAY-048: Service sub-view command waits outlive the view

- **Severity:** nit
- **Where:** `src/WSGM/Overlay/ServiceSubView.cs:227-239` (`Run` passes `CancellationToken.None` and toasts a refusal whenever the command finishes); `:241-274` (`ConfirmCommand` already filters by `_navigationGeneration`).
- **Problem:** A refusal that completes after the user left the sub-view still calls `Toast`. Leaving does not detach the view (`LeaveActiveSubView` hides its host and `Leave()` clears `_current` and `Content`), so `Toast` stores `_notice`, renders nothing, and the stale notice then appears at the top of the view's first level the next time the user opens it. Late completions otherwise do nothing.
- **Best solution:** Do not cancel the service operation: the services own their work, and a Steam write that was already dispatched runs regardless. In `Run`, the posted refusal calls `Toast` only while `_current is not null` (the view has not been left); otherwise it only logs, as `Toast` already does. An attachment check would not work, because a left sub-view stays attached. No new `CancellationTokenSource` or counter; this is the concrete form of plan v2's "waits honour cancellation" with less mechanism than the review's view-lifetime token.
- **Tests:** an `OverlayToolsTests` case where a fake command refuses after the user backed out of the view, and reopening the view shows no stale notice. Filter `FullyQualifiedName~OverlayToolsTests`.
- **Plan v2:** B132.
- **Related:** none.

### OVERLAY-049: Whole-tree walks per render

- **Severity:** nit
- **Where:** `src/WSGM/Overlay/OverlayWindow.Pins.cs:238-254` (`UpdatePinnedIndicators` walks every logical descendant after each coalesced device refresh); `src/WSGM/Overlay/OverlayWindow.DeviceReconciliation.cs:79-129` (nested capability loops).
- **Problem:** Indicator updates cost a full tree walk per refresh.
- **Best solution:** Each page controller keeps the `SectionPinHeader` and pinnable rows it created (keyed by `SectionKey`) and updates only those. `UpdatePinnedIndicators` iterates the pages' lists. The nested loops become one dictionary lookup by capability id inside `CapabilityRowRenderer`.
- **Tests:** existing pin and device capture tests identical. Filter `FullyQualifiedName~WSGM.UiTests.Overlay`; Visual filter.
- **Plan v2:** B127.
- **Related:** OVERLAY-006, -007, -034.

### OVERLAY-050: Small debts

- **Severity:** nit
- **Where:** `src/WSGM/Overlay/ThemesView.cs:20-22`, `AnimationsView.cs:19-21`, `DeviceOverlaySectionPages.cs:175-179` (duplicate `<summary>`); `AnimationsView.RenderDetail` (pass-through); `LaunchWrapperView.cs:144-146` (comment contradicts the caption); `ThemesView._service`/`_browser` (same object); `OverlayViewModel.PowerTimeoutMinimums` (`OverlayViewModel.cs:14`, never notifies while `OverlayWindow.PowerEditors.cs:68-70` listens for it); `OverlayViewModel.ConfirmingCloseLauncher` (`:92-98`, raises unconditionally); `OverlaySubView` `private protected` fields named with underscores; `StatusPanel.cs`, `FluentExtensions.cs`; the stale Dispose comment; `SteamStorageFormat`/`SteamGameLibrary` page names.
- **Problem:** Small inconsistencies that mislead readers or do dead work.
- **Best solution:** Delete the duplicate summaries. Inline `RenderDetail`. Rewrite the `LaunchWrapperView` comment to match the caption. Keep one field in `ThemesView`. Remove `PowerTimeoutMinimums` from the window's property listener: it is always set just before `PowerTimeoutValues`, which notifies, so the listener branch is dead. `ConfirmingCloseLauncher` uses `SetFieldIfChanged`. Rename the `private protected` fields to Rider's convention for non-private members (`NavigationGeneration`, ...). `StatusPanel` and `FluentExtensions` are deleted by B109 (OVERLAY-014), and the Dispose comment is fixed by B122 (OVERLAY-013). The two page names stay because enum names are frozen (review C3).
- **Tests:** build, Rider cleanup clean; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"`; Visual filter identical.
- **Plan v2:** B132 (with the parts in B109 and B122).
- **Related:** OVERLAY-013, -014.

## Refuted or no-change

- **OVERLAY-028** (low, polling where the owner could notify): no change. `ICommonPluginOverlaySource`, the coordinator's manual TDP mode and the preset service publish no change event; adding them would add mechanism in other domains with no defect behind it (verifier correction, plan v2 appendix). The polls stay, including `PinnedPluginWidgets`.
- **OVERLAY-041** (low, plausible, pending slider and curve edits dropped on a quick close): no change. No reproduced loss, and writing a device value after the user dismissed the sheet would itself be a behaviour change (verifier batch problem 4).
- **OVERLAY-032** (low, generic descriptor row hard-codes the frame-limit format): no change (checker). The only `DescriptorRange` producer is `PerformanceOverlayBridge.FrameLimitRange` (`Shell/PerformanceOverlayBridge.cs:674-688`; `DescriptorRange` is constructed nowhere else), so every ranged descriptor is the frame limit and "Off"/"N FPS" is correct for all of them. Adding `Unit` and `OffLabel` to the record would serve a hypothetical second range; whoever adds one adds its unit then. B126 drops 032.
- **OVERLAY-040** (low, view state on shared session models): no change (checker). `Expanded` on the radio and drive entries (`Shell/RadioEntries.cs:134, 359`, `Shell/RemovableDriveEntries.cs:154`) is read only by the overlay's own item templates (`RadioPanel.axaml:65, 86, 119, 140`, `EjectPanel.axaml:44, 66`); no Steam surface reads it or subscribes to the entries' `PropertyChanged` (the native QAM services listen only to `AudioManager`). Moving it off the entries would need per-row wrapper view models or template changes for no observable defect. `RadioManager.RespondToPairing` is a stateless static over WDC's static `WindowsRadio.RespondToPairing`; B065 owns its PIN rule. B130 drops 040.
