# Overlay review: adversarial verification

Verifier pass over `_plan/refactor-2.1/review/overlay.md` against `master` 1329813f. Read-only; nothing was built or run.
Every critical/high/medium finding, every plan claim marked inaccurate/stale/partial, and every batch was
checked against the cited code and its callers. Files the reviewer cited least were read for missed issues:
`OverlayController.Apps.cs`, `.Keyboard.cs`, `.Lease.cs`, `.SteamExit.cs`, `OverlayFilePicker.cs`,
`OverlayWindow.Surfaces.cs`, `OverlayWindow.Storage.cs`, `OverlayWindow.LaunchFixes.cs`, `OverlaySubView.cs`,
`ServiceSubView.cs`, `OverlayMediaPreview.cs`, `PinnedPluginWidgets.cs`, `PluginWidgetPinControls.cs`,
`ArtworkView.cs` (filters), `TouchSwipeMonitor.cs` (report path), `DevicePowerPresetSelection.cs`,
`OverlayWindow.Header.cs`, `Shell/DesktopTray.cs`, `Shell/GraphicsOverlaySource.cs`,
`Core/SteamLaunchConfig.cs`, toolkit `SteamClientScript.ParseWrite`.

## Refuted

None of the findings is refuted outright. Two plan-claim verdicts and one finding rationale do not hold
(listed under Corrected because a narrower defect survives).

## Corrected

- **C4 / plan refinement 6 / OVERLAY-002 recommendation (overlay-test half).** `--overlay-test` must keep its
  activation sources. `ShellSession.cs:355-363` shows the sheet once at start. In overlay-test there is no
  `DesktopTray`, `SessionActivation` or `SettingsActivation` (`ShellSession.cs:900-935` are all
  `!_overlayTestOnly`), so the hotkey, chord and swipe are the only way to reopen it, and overlay-test is
  where those triggers get tried. Composing it "sheet host only" would leave a sheet that cannot be reopened
  after the first close. Only the Settings **preview** (`SettingsWindow.axaml.cs:255-262`, inside a process
  whose session already owns activation) must drop activation. The rest of OVERLAY-002 holds: two
  `TouchSwipeMonitor`s dispatch every `WM_INPUT` (`TouchSwipeMonitor.cs:295-311`), the preview stays alive
  until Settings closes (`SettingsWindow.axaml.cs:197`), and `KeyboardService.Handler` is a single static.
- **OVERLAY-003.** The defect is wider than stated and the proposed fix is incomplete. `--overlay-test`
  passes the session's real `DisplayTimeouts` (`ShellSession.cs:899`, unconditional), so its timeout editors
  write real Windows idle timeouts through `DisplayTimeouts.Select` (`Shell/DisplayTimeouts.cs:39,177`),
  although CLAUDE.md calls `--overlay-test` safe. The Settings preview writes through the fallback
  (`OverlayController.cs:795-802`). The fix must key read-only on `_previewOnly` (as UAC/lock-on-wake do,
  `OverlayController.Power.cs:50-51`), not on "DisplayTimeouts present". After that the fallback branch can
  be deleted. Severity stays medium.
- **OVERLAY-001.** Severity is medium, not high. It is a coverage gap, not a behavioural defect. Also,
  "the only test is a table copy of `DecideSwipe`" is wrong: `QuickAccessSheetTests.cs:17-28` table-tests
  the production `OverlayController.DecideSwipe`. Line count: the controller partials total 2,142 lines, not
  1,957.
- **OVERLAY-004.** The per-sample `HashSet` allocation is real (`GlyphInputTestMap.cs:70-92`,
  `OverlayWindow.Device.cs:854-858`). Severity is medium: the path runs only while the Device glyph
  input-test page observes samples (`OverlayWindow.Device.cs:820-844`). The "no barrier" race claim is
  refuted: each sample publishes a fresh set that is never mutated afterwards, and a reference write is
  atomic. The mask fix is fine because `GlyphControlId` is dense from 0 with fewer than 64 members. A
  simpler option is to compare the raw `CanonicalButtons` plus the two trigger booleans with the previous
  sample, and map to glyphs only on change.
- **OVERLAY-006.** Sizes: 21 `OverlayWindow.*` files of C#, 6,507 lines including `OverlayWindow.axaml.cs`.
  `ShowOverlayCore` makes 16 `Attach*` calls, not 20. The finding stands.
- **C6 / OVERLAY-007 (delete view-side generation checks).** The deletion would drop behaviour.
  `WriteDeviceValue` (`OverlayWindow.DeviceControls.cs:20-30`) re-reads the snapshot, refuses when
  `!snapshot.Visible` or the current row is no longer `CanInvoke`, then sends `current with { NextValue }`.
  `DeviceOverlayBridge.InvokeAsync` (`:504-535`) checks only the passed object's `CanInvoke`. The graphics
  path has the same gap: `GraphicsOverlaySource.WriteAsync` (`Shell/GraphicsOverlaySource.cs:158-182`, not
  `DeviceOverlayBridge.cs:670` as cited) checks generations and not `CanInvoke`. Removing the view checks
  only holds if the bridges first re-check current `CanInvoke` and visibility. Otherwise keep one shared
  helper. The rest of OVERLAY-007 (duplicated layout/readings/command wrappers) holds.
- **OVERLAY-008.** `DisplayModeView` is guarded: resolution commits only when `!_resolution.IsDropDownOpen`,
  and the refresh rate the same way (`DisplayModeView.cs:44-63`). That leaves four unguarded editors:
  `DevicePowerPresetView.cs:49-68`, `ManualTdpModeView.cs:26-49`, `AudioPanel.axaml.cs:169-193`, and the
  TwoWay endpoint bindings in `AudioPanel.axaml:21,53`. The recommended `CommitComboBox` cannot be a new
  ComboBox subclass. The header profile combo lives in `OverlayWindow.axaml` and the endpoint combos in
  `AudioPanel.axaml`, so a subclass changes XAML element types, and a subclass also needs
  `StyleKeyOverride` (as `OverlayChoice<T>` has at `OverlayEditors.cs:110`) or it renders without a
  template. Make it an attach helper over existing ComboBoxes.
- **OVERLAY-013.** Severity is low. The deferred `UiSurfaceClosed` reaches `ControllerManager.ReleaseUi`
  (`Shell/ControllerManager.cs:597-604`), which is a locked release and cannot throw ObjectDisposed. The
  bridges are disposed in the second cleanup pass (`ShellSession.Shutdown.cs:678-690`), well after the
  150 ms close has run. The useful part remains: Dispose should release the UI claim synchronously (the
  lease already is, `OverlayController.cs:307-316`). If the dispatcher stops before the timer fires, the
  claim is never released.
- **OVERLAY-015.** Severity is low and the trigger is narrower. Themes cannot loop: `BrowseAsync` sets
  `Loading = true` synchronously (`Shell/ThemeService.cs:331-355`) and a completed page sets `Page = 1`
  (`:1050-1056`). The only loop is Animations with an empty repository (`Total == 0`, no error,
  `AnimationsView.Tools.cs:53-56`); `Total` is the repository total, not the search result, so an empty
  search does not loop. Moving the fetch to tab entry is still the simpler design.
- **OVERLAY-018.** The cited drift is wrong: `SteamArtworkBrowserSource.DefaultFilter`
  (`Shell/SteamArtworkBrowserSource.cs:1195-1210`) holds default selections, not the offered list, so it
  missing 512x512 is not drift. There is a real drift the reviewer missed. For the **logo** tab,
  `ArtworkView.OfferedStyles` falls to `_ =>` and offers grid styles (`alternate, white_logo, no_logo,
  blurred, material`, `ArtworkView.cs:392-401`). The Steam page offers `official, white, black, custom`
  (`artwork-browser.ts:15-17`), and the default logo filter selects those. A controller user can therefore
  neither see nor clear the selected logo styles, and can toggle styles SteamGridDB logos do not have.
  Severity rises to medium (functional defect). The fix stays "source publishes the offered vocabulary".
- **OVERLAY-021.** The rationale is refuted. The IR plugin returns `Dispatched` **with** a detail
  ("IR emitted; appliance state is not verified.", `WSGM.Plugin.Ir/IrPlugin.cs:390,401,647`), and the panel
  shows the detail (`CommonPluginPanel.cs:500-501`). So IR actions do not read "Change was not confirmed".
  What survives (low): an outcome without a detail is mislabelled (`Rejected` reads "Change was not
  confirmed"), and wording differs from the Steam surface, which treats `Dispatched` as success
  (`CommonPluginSteamUiSource.cs:295`). A correction keeps the plugin's detail when present, so the honest
  "not verified" text is not replaced by "Applied".
- **OVERLAY-028.** The recommendation adds mechanism. `ICommonPluginOverlaySource` has no change event
  (`Shell/CommonPluginOverlaySource.cs:39-42`), and neither does the coordinator's `ManualTdpMode`. Replacing
  those polls means adding notification APIs in other domains with no defect behind them. Keep the polls
  unless the owner already publishes a change.
- **OVERLAY-043.** Minor fact: the fresh-open path scans the process table once (`OverlayController.cs:508`).
  Line 699 is the alternative reactivate path, not a second scan per open.
- **C15.** Overstated. The Device 12 / Plugin 4 group (F02) changes the clock, `Deadline`,
  `DeadlineCancellation`, `PluginDiagnostics` and context APIs. No file in Overlay or Controls uses any of
  them; the overlay consumes only SDK value types. Name the overlay only if F02 renames those value types.
- **C5.** "The still-open window stays subscribed to bridges that Shutdown.cs:678-690 disposes" does not
  hold in practice: see OVERLAY-013.

## Confirmed

C1, C2, C3, C7, C8, C9 (with the OVERLAY-004 correction), C10, C11, C12, C13, C14, C16, C17, C18, C19;
OVERLAY-002 (preview half), OVERLAY-005, OVERLAY-009, OVERLAY-010 (PLAUSIBLE: toolkit `ParseWrite` maps
a dispatched write with no answer to `Accepted=false` and "No response from Steam", and
`OverlayWindow.LaunchFixes.cs:203-205,391-396` then forgets the only restoration snapshot), OVERLAY-011,
OVERLAY-012, OVERLAY-014, OVERLAY-016, OVERLAY-017, OVERLAY-019, OVERLAY-020, OVERLAY-022, OVERLAY-023,
OVERLAY-024, OVERLAY-025, OVERLAY-026, OVERLAY-027, OVERLAY-029 (`ProfileOverrideMarker.ResetAsync` has
try/finally with no catch under an async-void click), OVERLAY-030, OVERLAY-031, OVERLAY-032 through
OVERLAY-042, OVERLAY-044, OVERLAY-045 (note: `tests/WSGM.Tests/Overlay/TouchSwipeMonitorTests.cs` covers
the recognizer; arm/disarm/teardown remain untested), OVERLAY-046 through OVERLAY-050.

## Missed findings

### OVERLAY-V-001 (medium) A failed library lookup leaves a launch-fix row stuck on "Asking Steam…"

- Location: `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs:248,299-321,415-425`.
- `StartLaunchFix` fires `_ = ApplyLaunchFixAsync(mode, button)`. `ApplyLaunchFixAsync` sets the title to
  "Asking Steam…", then calls `SafeGameLookupAsync()`. That method throws `InvalidOperationException`
  whenever `OverlayLibraryLookup` reports an error, for example when Steam's collection store is not
  ready. The call sits outside any try (the try lives in `ApplyLaunchFixToAsync`). The task is discarded,
  so the row stays at "Asking Steam…" with no outcome. The exception surfaces only as a later
  `UnobservedTaskException` log line (`Program.cs:205`). The custom-action path is safe because it runs
  inside `OnPickCustomLaunchAction`'s try.
- Recommendation: when the moved `LaunchFixService` (B8) resolves the current game, treat a failed lookup
  like the not-listed case (use the page's app id and its shortcut range, as `ResolveCurrentGameAsync`
  already does), and give every exit path an outcome title. Add to B8's tests: "lookup failure still
  applies or reports".

### OVERLAY-V-002 (medium, PLAUSIBLE) Overlay Settings handoff creates a second Settings window

- Location: `src/WSGM/Overlay/OverlayController.cs:837-863` (`new SettingsWindow(true)` on every request);
  `src/WSGM/Shell/DesktopTray.cs:52-64` (the session's single-instance Settings owner).
- The sheet's Settings row always constructs a new `SettingsWindow`. It never reuses the tray's instance
  and is not gated by `_previewOnly`. Summoning the sheet over an open Settings window and pressing Settings
  opens a second one. Inside Settings, Test sheet then Settings opens a nested Settings from the preview
  controller. Two Settings view models edit independent snapshots of the same config, so the later save
  silently discards the other window's edits. It also makes Overlay construct Settings UI directly, a
  layering inversion.
- Recommendation: one session-owned "open Settings" intent (the `DesktopTray.OpenSettings` single
  instance, extended with the `gameModeSurface` flag and the Steam Input lease claim) that the overlay
  calls. The preview sheet hides or ignores the row like the mode switch. Fold this into B4's
  `IOverlayPlatform` or the session power/intent port. No new mechanism: it reuses the existing
  single-instance owner.

### OVERLAY-V-003 (low) Add Steam Library continues after the sheet closed behind the native picker

- Location: `src/WSGM/Overlay/OverlayWindow.Storage.cs:140-175`.
- After `OpenFolderPickerAsync` returns there is no `_closed` check, unlike
  `OverlayWindow.LaunchFixes.cs:97`. If the sheet was dismissed while the dialog was up (hotkey toggle,
  OEM button), the code navigates a closed window (`OpenStorageFormat`) and starts
  `_format.AddLibraryAsync(path)` with no visible progress. The result then pops the sheet through
  `OnFormatFinished`. Together with OVERLAY-027 (no `SystemDialogActive` pairing), this path has neither
  half of the native-picker discipline that the launch-fix picker has.
- Recommendation: in B1 with OVERLAY-027, add the pairing and the same `_closed` return as the launch-fix
  picker.

### OVERLAY-V-004 (low) `PinnedPluginWidgets` and `CommonPluginPanel` latch closed on first detach

- Location: `src/WSGM/Overlay/PinnedPluginWidgets.cs:29` (`_closed = true`, never reset);
  `src/WSGM/Overlay/CommonPluginPanel.cs:55` (`_closed.Cancel()` on a CTS never recreated).
- `VisiblePoll` restarts on re-attach (`VisiblePoll.cs:30-34`), but the refresh returns immediately and
  actions run with a cancelled token. Today the controls are created per sheet and live under hosts that
  toggle `IsVisible`, so this is latent. Any page-controller move in B7 that re-parents these controls would
  make the widgets go dead. `ManualTdpModeView.cs:50` shows the correct pattern: reset on attach.
- Recommendation: reset on attach (or create the CTS per attach) when B7 moves these hosts. Add a B7 test
  that detaches and re-attaches a pinned widget.

## Batch problems

1. **B3 drops the Steam-exit "show overlay" reaction.** The symbol table moves `OnSteamExited` wholesale to
   `SessionModes`, but `SteamExitReaction.ShowOverlay` (`OverlayController.SteamExit.cs:16-18`) opens the
   sheet. `SessionModes` must raise an event the overlay subscribes to, or the controller keeps that one
   branch. B3 also edits `SessionModes`, which H02 is splitting into `SessionTransitions`. Land B3's
   relaunch move only after H02's owner exists, or the same code moves twice.
2. **B3 and plan refinement 6 strip activation from `--overlay-test`** (see the C4 correction). Restrict
   B3 to the in-session Settings preview; overlay-test keeps hotkey, chord and swipe.
3. **B1 fixes OVERLAY-003 incompletely** unless read-only keys on `_previewOnly`. Its listed test
   `PreviewTimeoutEditorsAreReadOnly` must cover an overlay-test composition that has a real
   `DisplayTimeouts`.
4. **B1 bundles OVERLAY-041 (PLAUSIBLE, low) as a new "flush on dismissal" mechanism.** No concrete
   defect is shown, and writing a device value after the user dismissed the sheet is itself a behaviour
   change. Per the simplify rule, drop it from B1 until a log or test reproduces the loss.
5. **B1 changes OVERLAY-021's user-visible strings on a refuted rationale.** Keep plugin detail text. At
   most relabel the detail-less outcomes.
6. **B4 `QuickAccessPins` adds an owner, a queue and a `Changed` event** where the defect is only write
   ordering. The existing precedent is a single continuation chain (`LibraryTabManager.SaveTabOrder`,
   `Shell/LibraryTabManager.cs:739-744`). Chain the pin writes the same way, stop mutating the shared
   `_config` (`OverlayController.cs:977`), and drop the new owner unless H02's reloader needs it.
7. **B5's `CommitComboBox` must be an attach helper, not a subclass,** so `OverlayWindow.axaml` stays
   byte-identical (header profile combo) and styles keep applying. AudioPanel's TwoWay endpoint bindings
   need a one-way plus commit change in `AudioPanel.axaml`; B5 must list that file and check the
   `PreviewAudioPanel` capture. Its scope is four unguarded editors, not five.
8. **B5 deletes the view-side generation checks** without moving the current-`CanInvoke`/visibility
   re-check into `DeviceOverlayBridge.InvokeAsync` and `GraphicsOverlaySource.WriteAsync`, which are
   outside this domain (Shell, D01/GP01). As written, B5 silently admits writes to rows that became
   non-invokable. Either add the bridge check (a cross-domain dependency to name) or keep one shared view
   helper.
9. **B10 includes OVERLAY-043** (off-thread reads, values appear a frame later) although the reviewer made
   it open question Q4, and requirement 12 keeps UI behaviour identical. Gate it on Q4. B10 also includes
   OVERLAY-028, which adds change events to `CommonPluginOverlaySource` and the coordinator in other
   domains (see the correction). Drop it or limit it to owners that already notify.
10. **B6 depends on B4 without need.** The navigation controller, `Back(BackContext)` and the guard
    removals touch only `OverlayWindow.Navigation/Workspace/Rail/Header` and `OverlayNavigation`. They
    need B2 (surface host for `HasActiveSurface`), not the controller ports. Relaxing this lets B6 proceed
    while B3/B4 wait on H02.
11. **B8 should carry OVERLAY-V-001** so the moved transaction does not inherit the stuck-title path, and
    **B1 should carry OVERLAY-V-003** with OVERLAY-027.
12. **B9's OVERLAY-018 step targets the wrong drift.** The concrete defect is the logo-tab style list. Fix
    it in B1 as a defect correction (no new owner needed), and keep "source publishes the vocabulary" for
    B9 when G01/S02 land.
