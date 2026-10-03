# Settings UI, themes and backdrop library: adversarial verification

Verifies `_plan/refactor-2.1/review/settings.md` against master 1329813f. Every critical/high/medium finding was re-read at the
cited code and its callers; every inaccurate/stale/partially plan claim was rechecked; every batch was checked for
build independence, dependencies, dropped behaviour and maintainer-rule conflicts. Files the reviewer cited least
(`SettingsViewModel.Plugins.cs`, `DeviceSetup.cs`, `Steam.cs`, `Updates.cs`, `PluginActionListEditor.cs`,
`DisplayLayoutEditor.cs`, the page code-behinds, `PluginSettingsPage.axaml`, `Native/Backdrop.cpp`) were read in full.

Read-only stage: nothing was built, run or tested.

## Refuted

| ID | Reason |
| --- | --- |
| C19 | The provisional census was right. The 13 `SettingsViewModel*.cs` files total 3,750 lines (`wc -l`: 169+132+236+185+391+498+155+903+115+216+303+152+295), not 3,349. |

No critical/high/medium finding was refuted outright. The corrections below cover the ones whose evidence, scope or
severity does not hold as written.

## Corrected

| ID | Correction |
| --- | --- |
| SETTINGS-001 | Confirmed, and the scope is wider than stated. `AnimationsConfig.SteamSetAside` (`Core/Animations/AnimationsConfig.cs:24`) is **recovery state**: Steam's own startup-movie choice, which WSGM sets aside while one of its own movies plays. A Settings save that started from the window-open snapshot writes the stale value (often null) over it, and the user's Steam movie choice can no longer be restored. That breaks the never-strand rule as well as a preference. Themes are not "uninstalled": `ThemesConfig` holds `Enabled`, `TranslationsBranch` and `HiddenThemes`, and per-theme enable state lives in the theme files. B1 needs a regression test for `SteamSetAside` specifically. |
| SETTINGS-005 | Real, but narrower than stated. Unit tests with the convenience constructors do read the real machine: `ModernStandbyDiagnostics.Read`, the real `KnownStartupApps.Detected` and `UpdateChecker.ReadState()` (update.json). `Persist` is only wired to the real ConfigStore. No test that uses these constructors calls `SaveCommand`; they use `CaptureSaveRequest`/`SnapshotForPreview`, so no test currently writes config.json. The hazard is latent. Line refs: `SettingsViewModel.Updates.cs` has 152 lines, so the relevant ones are 71-74 and 88, not 590-610. |
| SETTINGS-006 | `ReadUpdates` does not fall back to production. It falls back to `new UpdateState()` (`Updates.cs:73`), which is inert. Only `DetectOtherManagers`, `ApplyOtherManagers`, `LoadPersisted` (`System.cs:187, 215, 228, 236`) and `ReadAudio` (`AudioProfileEditor.cs:97`, which the VM always supplies) fall back to production. No UI test presses either takeover button today (`rg OtherManagers tests` finds only Core tests), so the hazard is latent. The severity should be low. The fix (all members required) stands. |
| SETTINGS-009 | The scenario is reachable: B/Escape/the title-bar close stay live while `SettingsRoot.IsEnabled=false`. The consequence is milder than "stranding", because `SteamAutostartTakeover.Disable` records before it writes (`SteamAutostartTakeover.cs:79-81`) and the next save re-runs every external apply. The real loss is an interrupted shim/takeover pass, plus a config commit that can land without its splash promotion. The severity should be low. See the B5 batch problem about OS-shutdown close reasons. |
| SETTINGS-011 | Confirmed, with a concrete defect the reviewer did not state. The overlay claims a **set-based** UI surface (`OverlayController.Lease.cs:59-83`, `_uiSurfaces.Add/Remove`). Two overlay Settings requests create two windows (`OverlayController.cs:843-845`), and closing the first releases the "settings" claim while the second is still open. Managed controller capture is then handed back under an open Settings window. Also: `SettingsActivation` always opens through `DesktopTray.OpenSettings` (`ShellSession.cs:933-938`) with `gameModeSurface: false`. A Start-menu Settings launch while in game mode is therefore never registered with `WindowFinder.IncludeOwnWindow`, and it is unreachable from the Open apps strip once it drops behind Big Picture. `SettingsSurface.Open` has to choose the surface mode from the session's current mode, not from the caller. |
| SETTINGS-014 | `AudioProfileService` lives in `Shell` (`src/WSGM/Shell/AudioProfileService.cs`, namespace `WSGM.Shell`), not Core. "Core audio beside AudioProfileService" names a home that does not exist. Either move to Core next to the WDC adapters, or keep them in Shell beside `AudioProfileService`; B6 must pick one. |
| SETTINGS-020 | The line refs are wrong (`SettingsViewModel.Startup.cs` has 115 lines). The Program Files probes are `Startup.cs:60-77` via `_services.DetectStartupApps`. In the resident process, overlay-opened Settings also runs `LoadPluginPackages` (`Plugins.cs:130-166`: bundle manifest, `DeviceMachineIdentity.Collect`, `DisplayAdapterInventory.Collect`, cheap registry/CfgMgr reads) and `ConfigStore.Load` on the shell UI thread. "No change unless measured" is still the right call. |
| SETTINGS-025 | `SelectorValueListsCoverEveryEnumMember` is weak but not a tautology. It fails when a member is appended after the named last member, and its `DoesNotContain(WithText)` assertion is real. Keep the `DoesNotContain` assertion; do not delete the whole test. |
| SETTINGS-031 | Passing `DeckCanvasBrush` (#171A1F) instead of the library default (#181B20) is a one-step colour change of the fallback fill. That is not "visually identical", and requirements freeze UI appearance. Drop the optional fallback change; keep only the README move. |
| SETTINGS-035 | `DeviceAuthoredProfile.MaxNameLength = 48` is a WSGM-chosen constant (`Core/DeviceConfiguration.cs:115`), not an SDK storage contract. It truncates silently in `DeviceProfileRowViewModel.cs:53-56` (not 295-299) and in `ConfigStore.cs:598-600`. Under the no-arbitrary-limits rule it should be removed or put to the maintainer (see SETTINGS-V-007). The plugin text bound (`PluginSettingRowViewModel.cs:186-190`) is SDK-declared (`PluginSettingsManifest.cs:190`) and may stay. |
| SETTINGS-041 | Correct in substance (`Shared.axaml:155-169` use DynamicResource for stable tokens). `Themes/AGENTS.md` has 16 lines; the rule is at line 7, not 53. |
| Line refs (nits) | Wrong line numbers, though the substance holds, in SETTINGS-018 (`Updates.cs:105-151`), 032 (`System.cs:58-106`, `QuickAccess.cs:70-79`), 033 (`QuickAccess.cs:85` clamp 0..2, `PluginActionListEditor.cs:132`, `PluginSettingsPage.axaml.cs:120` max 9), 037 (`Launch.cs:135-143` + `PluginActionListEditor.cs:68-74`), 038 (`DisplayPage.axaml.cs:19`), 039 (`StartupPage.axaml.cs:66-92`, `SteamPage.axaml.cs:24-40`) and 040 (`DisplayArrangementView.cs:57-81` subscribes to editor PropertyChanged). An implementer must not trust the reviewer's line numbers for nits. |

## Confirmed

SETTINGS-002, SETTINGS-003, SETTINGS-004, SETTINGS-007, SETTINGS-008, SETTINGS-010, SETTINGS-012, SETTINGS-013,
SETTINGS-015, SETTINGS-016, SETTINGS-017, SETTINGS-019, SETTINGS-021, SETTINGS-022, SETTINGS-023, SETTINGS-024,
SETTINGS-026, SETTINGS-027, SETTINGS-028, SETTINGS-029, SETTINGS-030, SETTINGS-034, SETTINGS-036, SETTINGS-042;
plan claims C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14, C15, C16, C17, C18, C20, C21, C22, C23.

Notes on confirmations: in C13, G01 is a sequencing artifact of the linear objective order (`task-briefs.md:9`)
rather than a data dependency, so "drop G01" means "do not wait on it", not "it was wrong". For C2/C3 I agree that
revision snapshots and keyed reconciliation would add mechanism without a defect in Settings.

## Missed findings

| ID | Sev | Location | Finding | Recommendation |
| --- | --- | --- | --- | --- |
| SETTINGS-V-001 | medium | `Settings/SettingsViewModel.Save.cs:132-133, 405-410, 429-490`; `SettingsViewModel.Plugins.cs:418-485`; tests `WSGM.Tests/Settings/PluginSettingsViewModelTests.cs:215-246`, `DeviceProfileAuthoringTests.cs:108-130` | Two plugin-setting/profile merges. `ApplyTo` calls `ApplyPluginSettingsTo`/`ApplyDeviceProfilesTo` on the window snapshot, and `ApplyCapturedValues` then throws that work away (`config.DeviceIntegration = fresh.DeviceIntegration`). It re-merges through `FindOrAddSaveScope` plus an inline verbatim copy of `PluginSettingsResolver.Store` (`Save.cs:436-448` vs `Core/PluginSettingsResolver.cs:131-148`), and only the second path runs `ProfileEdits.RemoveFanCurveReferences`. Four tests named "AnEditReachesTheConfigurationTheSaveActuallyWrites" etc., and the remarks in `Plugins.cs` ("the save re-reads configuration from disk and applies the view model onto THAT object"), describe the dead path. They give false assurance. | In B1: make the merge the single path. It calls `PluginSettingsResolver.Store` on the fresh scope, `FindOrAddSaveScope` merges with `FindOrAddScope`, and `ApplyPluginSettingsTo`/`ApplyDeviceProfilesTo` are deleted together with their calls from `ApplyTo`. Retarget the four tests to `CaptureSaveRequest` + `SettingsSaveMerge.Apply` (the `SettingsSaveMergeTests.cs:178` shape). This removes code. |
| SETTINGS-V-002 | medium | `SettingsViewModel.DeviceSetup.cs:10-18, 54-102`; `SettingsViewModel.cs:169-178`; `Save.cs:138-157, 227-245, 405-427, 830-835, 880-896` | The three bespoke edited/saved pairs (AutoTDP, controller target, glyph selection) are a second hand-written copy of the generic `SharedFields` baseline mechanism. Each has flags, saved values, six `SaveRequest` members, double application in `ApplyTo` and in the merge, and its own `AdvanceSharedBaseline` branch. B1 as written adds a fourth pair for `DeviceIntegration.Enabled`. | One table of fields that another surface writes while Settings is open: the Steam-page fields plus `DeviceIntegration.Enabled`, `AutoTdpEnabled`, `Profiles.Global.ControllerTarget` and `GlyphSelection`. Delete the pairs, the six request members and the special cases; `DeviceEditsMade` becomes a query over the table. This is the "remove mechanism" form of SETTINGS-003. |
| SETTINGS-V-003 | low | `Save.cs:268-271, 568-585` | When splash promotion or the boot-manifest write fails, `CompletePersistedSave` throws after config.json was committed. `SaveWithStatusAsync` then skips `ApplySteamInput` (shim reconcile, Steam autostart and other-manager takeover). Persisted intent and Steam's folder disagree until the next save, which contradicts the method's own "deployment follows persisted intent" remark (`Save.cs:591-596`). | Run the external apply whenever the commit succeeded, then report the splash/boot failure. Fold this into the SETTINGS-021 status change. |
| SETTINGS-V-004 | low | `Save.cs:229, 597-667`; `Core/SteamInputManagement.cs:17-41` | Every save, including an unrelated one, reconciles the shim, which can self-elevate with a UAC prompt when unelevated and access is denied. When accepted, it also runs `SteamAutostartService.Scan/Apply(prompt: true)` and `OtherManagers.Detect` (schtasks). WSGM's Steam page applies the shim only when its toggle changes (`WsgmSteamSettingsService` `SteamInput: true`). | Maintainer question: apply only when the relevant persisted field changed in this save (the takeover buttons already cover "re-check"). If re-applying on every save is intended, record it in Settings/AGENTS.md. Do not add a new guard; compare the request's values with the baseline that already exists. |
| SETTINGS-V-005 | low | `Settings/Pages/AboutPage.axaml.cs:24-27` | About/credits links use a direct `ShellExecute` of https URLs. Settings normally runs inside the resident, elevated process (SettingsActivation hand-off). `docs/elevation.md:53-57` states that a direct ShellExecute from the elevated shell only works while an unelevated Explorer brokers it, and routes `ms-settings:` through the medium one-shot for that reason. In game mode the links fail or start an elevated browser. | Route URL opens through the existing medium one-shot used for `ms-settings:`. If that is out of scope for V01, record it as a known limitation. |
| SETTINGS-V-006 | low | `Settings/Pages/PluginSettingsPage.axaml:41-90` | A page-local literal palette for the plugin badges (accent/community/info/good/warn/bad), against `Themes/AGENTS.md:6,10`. The "accent" badge hard-codes orange (`#26FF9D3D`, `#73FF9D3D`, `#FFFFB870`) and ignores the user's accent. A fifth copy of the default accent digits (extends SETTINGS-017). | Move the literals to named Palette tokens with identical values (no visual change). Whether the accent badge should follow the user accent is a maintainer decision, because it changes appearance for non-default accents. |
| SETTINGS-V-007 | low | `Core/DeviceConfiguration.cs:115`; `Settings/DeviceProfileRowViewModel.cs:53-56`; `Core/ConfigStore.cs:598-600`; `Pages/PluginSettingsPage.axaml.cs:78` | A WSGM-chosen 48-character name cap silently truncates authored profile names (load-time normalize and edit). This is an arbitrary length cap; the reviewer misfiled it as a storage contract. | Remove the cap (the controls already trim visually), or ask the maintainer. Coordinate with the Core/config owner because `ConfigStore` normalizes the same field. |
| SETTINGS-V-008 | low | `src/WSGM/Settings/SettingsWindowServices.cs:22-23`; `Shell/BootSplash.cs:102` | Each Settings window builds its own `GamepadService` (a 16 ms `DispatcherTimer` SDL poller) inside the resident process, beside the session's own input sources. The plan does not name this owner; a SettingsSurface composition (B4) should decide whether Settings borrows the session's button source. | Out of this domain's authority (Input owns it). Record it as an open question for B4/H02 rather than change it here. |

## Batch problems

1. **SETTINGS-B1 adds mechanism where it should remove it.** Step 3 ("Enabled becomes edited-only with saved/edited pair
   like AutoTDP") adds a fourth bespoke flag pair. Fold `Enabled` and the three existing pairs into the single shared-field
   table instead (SETTINGS-V-002). B1 and B2 should then merge, or B2's table should land first, so the rule
   "write a field another surface owns only when edited here" has exactly one mechanism.
2. **B1 omits the dead duplicate merge.** It must delete `ApplyPluginSettingsTo`/`ApplyDeviceProfilesTo` (or
   `FindOrAddSaveScope`'s inline copy) and retarget `PluginSettingsViewModelTests` and `DeviceProfileAuthoringTests`,
   which are missing from its file list (SETTINGS-V-001). Without this, B1 builds green but keeps two merge paths, and
   the tests keep exercising the one the save does not use.
3. **B1's top-level classification guard test is low value.** With an allowlist that starts from `fresh`, an
   unclassified new top-level field is safe by default (it keeps the saved value). The failure mode flips to "Settings
   forgot to save its own new field", which manual testing shows at once. The test also ignores partially owned
   sections (`DeviceIntegration`, `Profiles`, `GameLibrary`, `Artwork`, `GameModeLaunch`). Replace it with a round-trip
   test: every bound Settings value set to a non-default survives `CaptureSaveRequest` + merge; the shared and
   runtime fields keep `fresh`. Include `Animations.SteamSetAside`.
4. **B1 still needs two field lists.** "Copy the fields `ApplyTo` already enumerates" means a second hand-written list
   (`ApplyTo` VM->snapshot and the copy snapshot->fresh). The batch must say how the two stay in step; the round-trip
   test in item 3 is the guard. Otherwise the denylist drift risk only moves.
5. **The batches are scattered across objectives that run before V01.** The linear order (`task-briefs.md:9`) runs I01,
   F01, H02 and S01 long before V01. B6 says "before I01", B4 says "with H02", B2 says "coordinate with S01" and B7 says
   "after F01". The plan must assign each batch to an owning objective: B1 as an early admitted fix (like W02_01),
   B2 inside S01, B4 inside H02, B6 inside I01 or the W-group. Otherwise the V01 objective dispatches work that its
   predecessors were supposed to have absorbed, or I01 edits Settings files without owning them.
6. **B3 (1,200 lines) bundles six independent changes.** It requires services, deletes the convenience constructors
   and retargets ~20 tests, replaces display discovery timing, adds external apply, adds the saved accent and removes
   ConfigStore reads. The display-discovery change alters when UI tests see displays: today the injected path captures
   in the constructor, so the first render already has rows, and `GameModeDisplayPageTests` (479 lines) depends on that.
   Async discovery needs every UI test to await it. That needs its own batch with a UI baseline check; split
   `StartDisplayDiscoveryAsync` out of B3.
7. **B5 must not veto OS or application shutdown.** Cancelling `Closing` while `IsSaving` has to skip
   `WindowCloseReason.OSShutdown`/`ApplicationShutdown`; otherwise a slow save vetoes logoff, and in the resident process
   `DesktopTray.Dispose` → `_settings?.Close()` is deferred during session shutdown. B5 also does not depend on B3; it only
   needs `IsSaving`, so it can land earlier.
8. **B6's target home is wrong.** "Core audio beside AudioProfileService" names a home that does not exist
   (`AudioProfileService` is in Shell); pick one.
9. **B7 "keep Save disabled" changes a workflow.** Today Save stays enabled and fails with a message; requirements
   freeze workflows. Show the load outcome in the status strip and keep Save's strict refusal, which is already the
   behaviour, rather than disabling the button.
10. **B9 bundles a native rebuild with nits.** Reusing vectors (SETTINGS-029) fixes no measured defect; it adds member
    state to a native payload that needs the M01-34 manual pass, which breaks the simplify rule. Keep B9 to the internal
    seam (three internal delegate fields are simpler than an `IBackdropNative` interface) and the `StateChanged` fix. Do
    the named constants only if the native file is rebuilt anyway.
11. **B10 drops SETTINGS-031's fallback-colour change.** As corrected above, it is not appearance-neutral.
12. **Deferred validation is stated correctly.** The batches run narrow filters only; the full gate and UI baselines run
    once at the end, per repository rules. No batch was found to violate the readback, HC-reference, VIIPER or Avalonia
    freezes.
