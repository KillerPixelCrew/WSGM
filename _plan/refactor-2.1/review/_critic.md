# Completeness critic: whole-codebase refactor review

Critic: Claude, read-only (this file is the only write). Baseline `master` 1329813f. Inputs: the 19 domain reports and
their `.verify.md` files, `_user-reported.md`, `refactor-plan.md`, `planning-corrections*.md`, `requirements.md`,
`manual-acceptance-matrix.md`, `source-inventory.json` (1,670 paths), `claude-findings-raw.json`,
`claude-findings-disposition.*`, `audit/{A01,A02,A02S01}/findings.json`, `batches/*`. Nothing was built or run.

Working-tree note: `git status` shows ` m external/windows-device-control` with `DisplayTopology.cs` and
`DisplayTopologyTests.cs` modified in the child (W02_01, uncommitted). The parent snapshot said "clean" because the
gitlink itself is unchanged. Every WDC batch must start from WDC-B0 (commit and push W02_01) or it builds on top of an
unrecorded change.

---

## 1. Uncovered files

Method: every inventory path was matched against all report text by full path, path suffix and file name, then the
directory scopes each report declares as read in full (for example "all 14 files in `Core/Library/`",
`Core/Themes/**`, `src/WSGM.DeviceLab/**`, toolkit `src/**`) were subtracted. Vendored `external/minhook` and
`external/LoadingIndicators.Avalonia` are excluded by the audit rules. Result: 180 files (23.4 k lines) remain that no
report names or scopes. 21 are production files, 159 are tests.

### 1.1 Production files no report reviewed

| Area | Files |
| --- | --- |
| `src/WSGM/Core` | `ApplicationProfileRules`, `Credits`, `DesktopAppProcessBackend` (216), `DevicePrerequisites`, `DeviceProfileValidation`, `DisplayOperatingPoint`, `DisplayTimeoutPolicy`, `ExceptionList`, `FanCurvePresets`, `FileCleanup`, `ForegroundApplicationFilter` (named only in library.verify), `ManualTdpProfile`, `ModernStandbyPolicy`, `NativeQamPerfProjection` (200), `ObservableObject`, `PerApplicationPowerPolicy` (167), `PerformanceModels`, `RelayCommand`, `Sounds/SoundsConfig`, `SteamGlyphPresentation` (332), `WindowsPolicyOperation` |
| `src/WSGM/Shell` | `AnimationBrowseSession`, `ApplicationProfileSyncBuilder`, `CommonPluginDependencyPlan`, `GameModeCardServicePolicy`, `GameWindowReturn`, `IChangeSource`, `IExtensionsTabSection`, `NativeQamCpuBoostService`, `NativeQamHybridCoreService`, `OverlayToolSessions`, `SimulatedGraphicsOverlaySource` (255), `SplashPolicy`, `SplashStyle`, `SteamExtensionsTabBackend`, `SteamGameContextMenuBackend`, `SteamPowerMenuBackend`, `ThemeBrowseSession` (348), `VolumeIndicatorWindow.axaml.cs` |
| `src/WSGM/Interop` | `OverlayMediaNative` |
| Named but not owned by any domain | `Shell/SoundPackService.cs` (422, winsvc "lightly swept"), `Core/Sounds/SoundPackLibrary.cs` (341), `Core/SteamAutostart.cs`, `Core/SteamAutostartTakeover.cs`, `Core/KnownStartupApps.cs` (U04B scope, see 1.3) |
| Tools/eng | `eng/checkout-controller-dependency-sources.ps1`, `eng/extract-hc-devices.ps1`, `tools/WsgmLibTest/capture-steam-window.ps1`, `tools/PerfLab/WSGM.PerfLab.csproj` |

### 1.2 Critic review of the important ones

I read `SoundPackService`, `SoundPackLibrary` (install and override paths), `ThemeBrowseSession`,
`DesktopAppProcessBackend`, `NativeQamHybridCoreService`, `NativeQamCpuBoostService`, `DeviceProfileValidation`,
`SteamAutostartTakeover`/`SteamAutostartService.RecordDisabled` and `OtherManagers.Record`, and grep-swept the rest of
1.1 for statics, caps, fire-and-forget, readback gates and live-state access.

**CRIT-001 (medium, confirmed; ledger U04B-LFA-003, unowned).** `SteamAutostartService.RecordDisabled`
(`SteamAutostartService.cs:178-201`) catches and logs a failed `ConfigStore.Mutate`, so
`SteamAutostartTakeover.Disable` (`SteamAutostartTakeover.cs:79-94`) goes on to disable the task or rewrite the
approval with no recorded original. Uninstall can then never restore Steam autostart. `OtherManagers.Record`
(`OtherManagers.cs:478-488`) throws instead and is correct. WINSVC-B3 states "Disable records before each change
(existing test kept)", which is true for other managers but not for Steam autostart. Fix by removing the catch so the
item's existing `try` refuses the write. No new state. Owner: WINSVC-B3, which already edits `AutostartSystem`/takeover
call sites. Test: a throwing record delegate leaves the task enabled and the approval untouched.

**CRIT-002 (medium, rule: no arbitrary limits; unowned).** Sound packs drop and refuse valid content:
`SoundPackLibrary.BuildOverrides` silently counts any asset over 1 MiB as "missing" (`:173-177`) and throws for the
whole pack past a 16 MB "playback budget" (`:191-194`). The Steam delivery is already chunked (toolkit `DeliverAsync`
parts), so neither cap protects a transport. Install refuses more than 512 entries (`:221`) and
`SoundPackService.ImportAsync` refuses a ZIP over 64 MB (`SoundPackService.cs:201`). Keep only the zip-bomb guard
(expanded bytes plus symlink and containment checks), and keep it only as an explicit IO-safety exception the
maintainer accepts. Remove the 1 MiB skip, the 16 MB budget and the entry count. Minor in the same file: `Load()` first
publishes an empty override set with a new revision (`:336-339`), so every refresh, select or preview briefly retracts
the pack. It is deliberate ("retract stale bytes"), but publish the empty set only on the failure path. Owner:
steamhost B8 (content services). Migration of the stored selection belongs to config (plan L103 "sound choices").
M01-28 covers it.

**CRIT-003 (medium, rule: no readback gate; unowned).** `NativeQamHybridCoreService.SetHybridCoresAsync`
(`:33-68`) refuses a user's selection when a fresh `cores.Read()` fails. After any failed write it also latches
`_requiresRead`, which refuses every later selection until a publication read succeeds ("Windows state must be
refreshed before another selection"). A new explicit user action is the allowed retry, so this gates a control on
readback. HC model, as `ApplicationPerformanceReconciler.ApplyCpuBoostAsync` already does: validate against the
published option list, write, publish the written value, no latch. Owner: WINSVC-B2 (HybridCores). Add the file to
its list.

**CRIT-004 (low-medium).** `DesktopAppProcessBackend` uses `Process.MainModule` for capture and PID-reuse identity
(`:31, :83`). That is the same defect class as U04B-LFA-010. Seen from a medium WSGM, an elevated integration throws a
`Win32Exception` before the intended integrity message, which fails the whole `Capture` for every rule. The elevated
`RestartAsync` path lets a declined-UAC `Win32Exception` escape (only the poll loop is in the `try`), and its poll loop
has no cancellation (at most 5 s). Use the limited-query image path that SESSION-B2 introduces
(`NativeShellProcess.TryGetImagePath`). Owner: SESSION-B2. `Kill(true)` applies only to the listed integrations,
never Explorer, so the Explorer rule holds.

**CRIT-005 (low, rule: no arbitrary limits).** `DeviceProfileValidation.MaximumPoints = 64` (`:38`) refuses an
authored curve with more points than the device declares nothing against. Remove it together with the router's
matching limit and enforce only the descriptor's bounds. Owner: device (DEVICE-B9 with the config caps).

**CRIT-006 (low, note only).** `ThemeBrowseSession.Fetch/OpenAsync` (and `AnimationBrowseSession`) start
fire-and-forget reads linked to the per-request token. They are benign today because the runtime disposes, not
cancels, the request CTS (`SteamUiModuleRuntime.cs:178-179`). But the cancellation path never clears
`_loading`/`_detailLoading`, so any future change that cancels request tokens on completion would leave a stuck
spinner. Link only to `_lifetime`. No new mechanism.

The rest of 1.1 is pure rules, small projections or MVVM helpers. The grep sweep found no static mutable state, caps,
untracked work or live-state access beyond the items above. Z03 can record them as reviewed-no-change, with
"grep-swept, not line-read" stated honestly. `SimulatedGraphicsOverlaySource` is overlay-test only, and overlay B3/B4
must keep it inert.

### 1.3 Partial coverage that the domain list hides

- **install.md is truncated.** It ends mid-sentence in INSTALL-010. The bodies of INSTALL-005, 006, 007, 009, 015,
  016, 017, 020, 026, 027, 032, 033 and 047, and Sections 3 to 6 (refinements, target design, batches, manual rows),
  were never written. install.verify confirms the substance and adds INSTALL-V-001 (high, plausible) to V-007, but
  **no install batch exists**. This is the largest hole: two high findings (INSTALL-001 SYSTEM starts
  `boot.json`'s `ExePath` elevated; INSTALL-002 elevated setup runs user-writable temp payloads), INSTALL-003
  (ProgramData DACL), INSTALL-008 (task XML staging), and INSTALL-V-003 (uninstall with a missing `App\WSGM.exe`
  deletes the HidHide ledger and strands the cloak, a never-strand violation). Re-run the install domain's Sections
  3 to 6 from install.verify's ordering constraints before any install work.
- **U04B closure (37 missing bodies) is not done.** No report owns `SteamAutostart.cs`, `SteamAutostartTakeover.cs`,
  `KnownStartupApps.cs`, `WindowsPolicyOperation.cs` or `DesktopAppProcessBackend.cs`. Session and install cover the
  rest only partly. U04B-LFA-003 (CRIT-001) and U04B-LFA-012 (leaked `WSGM_StartUnelevated_*` tasks) have no
  disposition in any report.
- **Exclusions the input report named:** `LastInput` and `TouchKeyboard` are reviewed by nobody. `HotkeyService`,
  `KeyboardService`, `VolumeFeedback` and `UpdateChecker` only by winsvc's light sweep. winsvc also only swept
  `ForegroundWindowWatcher`, `WindowIcons`, `VariableRefreshCapabilities`, `VolumeIndicator`,
  `AudioPlaybackChoices`, `UacSettings`, `WakeLockStatus`, `ModernStandbyDiagnostics`, `RegistryValueSnapshot` and
  `UpdateExitWatcher`.
- **AutoTDP policy:** `Core/AutoTdp.cs` (1,371) and `AutoTdpTrace.cs` were read for structure only, and
  `AutoTdpControllerTests.cs` (823) by nobody. That is acceptable because no retuning is allowed. Record it as
  "structure reviewed".
- **Tests:** 159 test files (about 19 k lines) were never named, among them `AutoTdpControllerTests`,
  `SplashAssetsTests` (631), `SplashThemeTests` (721), `SteamAutostartTests` (431), `SteamGlyphCssTests` (409),
  `ProfileEditsTests`, `ProfileResolverTests`, 44 Shell and 58 Core test files. A live-machine sweep (registry, real
  config/mutex, `Log.Directory`, process start, power APIs) found only `RegistryValueSnapshotTests`
  (HKCU\Software\WSGM.Tests, disposable) and `ThemePathsTests` (`mklink /J` inside temp). Both are safe. The
  getter-only and copied-predicate audit the plan asks for (L38) was not done on these files. Z03 must do it.
- **Manual matrix:** 25 of 42 M01 rows are referenced by no report (01, 02, 04, 05, 07-12, 15-19, 21-24, 26, 28, 29,
  38, 40, 41). Nobody reviewed the matrix against the refined batches. Concretely: no row covers the RTSS overlay
  without opening the QAM (USER-001). M01-31 hard-codes "multiple 200 entry pages", a UI the picker does not have
  today (see conflict 7). Session.verify item 9 and install.verify list rows the matrix lacks (tray Exit on a device
  session, update handoff log line, restore-shell from Game Mode, sign-out without blocker, uninstall with App
  missing).

---

## 2. Plan and requirement items no domain addressed

1. **USER-001 (maintainer-reported, binding)** is in no domain batch. Device B8 edits `PerformanceService` (DEVICE-033)
   without it. winsvc B9 rewrites RTSS, steamhost B1/B2 rewrite `SteamUiSessionHost`, and steamhost.verify item 3 even
   tells B2 to preserve `ReleasePerformanceObservation` as an edge side effect, which USER-001 deletes. overlay B10 edits
   `OverlayWindow.Sources.cs`. Make USER-001 its own first batch. Files: `PerformanceService.cs` (start RTSS at
   service start, unconditional poll, defer only `FrameLimit`), `RtssLauncher.cs` (cooldown gone, `Process.Exited`
   keep-alive), `SteamUiSessionHost.cs`, `PerformanceOverlayBridge.cs`, `OverlayWindow.Sources.cs` (delete the
   observation plumbing), `docs/rtss.md`, `docs/decisions.md`. Filter `FullyQualifiedName~PerformanceService`. Every
   later batch starts from it. Add the matrix row.
2. **Install domain batches** for plan L105-107 and L157 (see 1.3).
3. **Sound choice migration** (plan L103) and the sound-pack service as a whole: no owner (CRIT-002).
4. **Frozen-foundation check** (requirement 12, plan L173 "frozen-dependency checks"): no batch implements a check.
   BUILD-R4 only adds a Dependabot ignore. The simplest check, with no new tool: at Z04,
   `git diff 1329813f -- '*.csproj' Directory.Build.props external/viiper` must show no Avalonia, FluentAvaloniaUI or
   VIIPER version or gitlink change.
5. **Requirement 16, independent child validation:** if per-batch publication (conflict 21) replaces I01/I02, every
   WDC and toolkit batch must still run child-local build and tests on both WDC TFMs and the full toolkit Node suite
   (build.verify R2 note). No WDC or toolkit batch lists this today.
6. **Requirement 6, toolkit JS API inventory:** toolkitcs inventories 229 public C# types and WDC inventories its API
   (4.3). Nobody inventories the toolkit's exported JS surface (kit functions, gate factories, fragment entry points
   that WSGM fragments consume) with retain/internalize/remove decisions.
7. **Never-strand acceptance** has no single batch. INPUT-B1 (HidHide show on every leave path), DEVICE-005 (stale
   ledger consumed at start with integration off), INSTALL-V-003 (uninstall with App missing) and session B7 (cloak
   off on every exit path) are spread over four domains. Group them into one early safety batch with one acceptance
   list.
8. **Medium ledger rows referenced by no report:** U02A-SUTC-001 (one-shot session turns a JS exception into
   success; resolved in substance by TOOLKITCS-B3 but not cited), U02A-SUTC-004 (bridge open to any SharedJSContext
   script; document it under the identity decision, conflict 6), U03A-SUTS-004 (performance gate accepts an
   undecodable update as an empty delta), U03A-SUTS-006 (no emitted-asset checks for the audio, performance and network
   gates; toolkitjs.verify V-003 touches it), U04A-LFA-008 (BootManifestTests re-implement the projection),
   U04B-LFA-003 (CRIT-001).

---

## 3. Cross-domain conflicts and recommended resolutions

| # | Conflict | Recommendation |
| --- | --- | --- |
| 1 | **Shutdown model.** Session R1/R2 replaces the B3 percentage cutoffs and `SessionLifetime` with a fixed safety-first step list under one deadline. device ("percentage cutoffs are binding at session level"), config B4 ("B3 shutdown phases"), steamhost B5 and winsvc B10 ("SessionLifetime" step registration), toolkitcs A5 ("B3 shutdown phases") and input change 2 assume the opposite. | Adopt session R1/R2, maintainer Q1. Every owner exposes `CloseAdmission()` (synchronous, at T0) and `StopAsync(Deadline)`. No phases, no registry. Rewrite the dependency lines in those five reports. |
| 2 | **APIs session B7 consumes that nobody provides.** `DeviceCoordinator.CloseAdmission` appears in no device batch. steamhost calls its method `CloseAdmission()`, session calls it `CloseCommandAdmission`. | Add `CloseAdmission` to DEVICE-B3. Use one name, `CloseAdmission`, everywhere. |
| 3 | **Controller release ownership.** DEVICE-B3 cannot bound shutdown while `ReleaseAsync` is unbounded (device.verify 3). INPUT-B1 changes `ReleaseAsync(Deadline)` and four `DeviceCoordinator` call sites it does not list (input.verify). DEVICE-B7 needs a built `ControllerManager` (INPUT-B3). | INPUT-B1 owns the signature and the four call sites and lands before DEVICE-B3. INPUT-B3 does the constructor change. DEVICE-B7 follows it. |
| 4 | **NativeQamSemanticServices cycle.** steamhost B4 waits on device ports. DEVICE-B6/B8 wait on steamhost's member moves and both edit the same file. | Run steamhost B4 first against the concrete `DeviceCoordinator` (it allows this). DEVICE-B6/B8 then edit the split files. No interim ports. |
| 5 | **Toolkit contract.** steamhost B7 expects `DependsOn`, a quarantine API, dynamic module (un)registration and an `ExistingWindowsPaths` policy type. toolkitcs R3/R4/R8 remove all four. library B5 and overlay B8 map `DispatchedUnknown`, but toolkitcs's outcomes are `NotSent / Unknown / Rejected / Applied`. | Adopt toolkitcs. Drop steamhost B7 steps 2 and 5. Plugin modules are read at session start (steamhost.verify 6). Consumers map `Unknown`. |
| 6 | **Bridge identity.** toolkitjs R17/B7 add `hostId` and nonce. toolkitcs R6 drops both. | Drop them. toolkitjs.verify shows `hostId` would refuse a restarted WSGM as foreign. The underlying U02A-SUTC-016 is a low hypothesis. Keep asset hash, schema and generations, and document the trust boundary (U02A-SUTC-004). |
| 7 | **File picker.** Plan B2 (200-entry pages, spool, 4 workers, `ProviderBusy`/`ProviderCapacity`), M01-31/32, toolkitcs R8 (one fixed wait), toolkitjs R2 (paging with the kit's "More" control) and steamhost B7 (policy type) all differ. `file-picker.ts` renders the whole list today and never calls `renderSteamUiMore`, and the C# surface has no cap, so paging is a visible UI change (toolkitcs.verify 6). | Enumerate and sort on a worker, cancel on navigation, reject stale tickets, normalize extended paths and refuse device namespaces. Render the whole list as today, bounded by the bridge request timeout (no second clock). No pages, spool, worker caps or policy type. Rewrite M01-31 and plan B2. Library folders keep refusing UNC (library C14). |
| 8 | **Fold-id migration.** config B3 says steamhost owns it, steamhost B7 says config owns it, toolkitjs R1 says none is needed. | No migration. The published section ids are today's strings (toolkitjs R1). Remove it from plan L103, config B3 and steamhost B7. |
| 9 | **WDC shape.** WDC proposes static facades plus disposable watch registrations. winsvc B8 ("Radio over WDC instance services"), config B5/B7 ("instance AudioService/DisplayService") and plan L121 assume per-instance services. | Adopt WDC. winsvc B8 consumes registrations, and disposing one is the stop. config B7 has no instance dependency. |
| 10 | **Display modes.** config B7 (`PrimaryDisplayModes`) and winsvc B6 (`TransientDisplayMode`) rewrite or delete the same files (`DisplayProfiles`, `DisplayResolutionService`, `RefreshRatePairingService`, `NativeQamResolutionService`, `EdidModes`). winsvc.verify rejects B6 as a workflow change. | One owner, winsvc: WINSVC-012 (fakes, delete test-only branches) plus moving `DisplayProfiles`/`EdidModes` under W02 only if W02 exposes advertised rates. Drop config B7. |
| 11 | **Intel display identity.** GPUIR-B5 routes it through WDC. WDC C36 rules "accepted duplication". | Skip GPUIR-B5 (gpuir offers this). |
| 12 | **Bluetooth catalog.** winsvc B8 deletes `BluetoothDeviceCatalog` as "moved to WDC". WDC moves only container normalization. | WDC position: product policy stays in WSGM. |
| 13 | **Windows power statics.** WINSVC-B2 (`PowerPolicyLane`, deletes the four `Windows` statics) and DEVICE-B6 (`CpuBoostReconciler`, "static Windows adapters passed from the composition root") target the same statics. `NativeQamCpuBoostService` and `NativeQamHybridCoreService` are owned by neither. | WINSVC-B2 owns the statics, with the lock inside the injected `PowerSchemes` instance (winsvc.verify) and CRIT-003. DEVICE-B6 consumes the instances. No second lane type. |
| 14 | **MessageWindow.** SESSION-B3 and WINSVC-B10 both own `MessageWindow.cs` with different shapes (session: no new registration objects; winsvc: reference-counted `IDisposable` claims, which winsvc.verify rejects). | One batch, SESSION-B3: owner-only construction, WndProc guard, provider disposal ahead of window destruction (winsvc C8). WINSVC-B10 shrinks to `DisplayChangeWindow` and the `EffectivePowerModeNotification` guard. |
| 15 | **Storage.** STEAMHOST-B1 adds a lock plus a cached projection with revision to `SteamStorageBridge`, while WINSVC-B4 replaces the same enumerations with a `StorageInventory` projection. | No revision cache in steamhost B1, only the lock-scope fix. WINSVC-B4 owns the inventory. |
| 16 | **State-file read outcome**, implemented five times: config `StateFile` (B2b, which also lists `ImportStateStore`/`ArtworkStateStore`), library B1/B3 (own read outcomes for the same two stores), gpuir B1 (GPU and IR journals), SDK A02_02 (`FileNotFound` means absent), labcore (`LabMachineState`). Library B7 and config B2a both thread roots into the same two stores. | config B2b owns every WSGM sidecar with one rule. Library B1 keeps only the `Sanitize` cap removal, library B3 drops its `ArtworkStateStore` step, and library B7 folds into config B2a. Plugins and Lab apply the same stated semantics in their own assemblies (missing file or directory is Absent, a parse failure is Corrupt, any other IO is Unreadable and never written). No shared new type is required. |
| 17 | **UserDataContext identity.** config waits for install to supply `ForInteractiveUser`. install.verify replaces the INSTALL-005 `TargetUser` plumbing with a refusal in `Detect`. | `UserDataContext` is the root plus the mutex name, `ForCurrentUser` only. Delete "user identity" from plan L97. |
| 18 | **Clock and trace.** SDK keeps the process `ActiveClock` and `PluginTrace` (adding an `IPluginHost` log channel for common plugins). PACKAGES-B5 assumes an owned clock and a `PluginTraceSink` instance. device (`CommandOperation` across sleep) and gpuir B2 wait on an "F02 clock". | Adopt SDK. PACKAGES-B5 shrinks to the API bump and the `DisposeAsync` contract line. The across-sleep command timeout (device.md:210) needs its own fix inside the existing clock (a deadline registration on `ActiveClock`) and is otherwise left unresolved. Device B2 keeps an injected `Func<DateTimeOffset>` as final, not interim. |
| 19 | **DeviceRecoveryJournal edited by three batches.** A02_02 (latch), SDK-B1 and PACKAGES-B2 all edit it. sdk.verify and packages.verify V-006 reject the latch. device wants DEVICE-001 before A02_02. | A02_02 steps 1 and 4 only, after a small DEVICE-001 fix. Then SDK-B1. PACKAGES-B2 runs inside the Device 12 batch (packages.verify 2) with the controller-entry exemption. |
| 20 | **Admitted batches against the reports.** | T01_01: do not land, fold into TOOLKITCS-B3. A02_01: replaced by SDK-B4 (without step 3). A02_02: trimmed as above. A02_03: keep with labcore R4. A02_04: keep (GPUIR-B6). W02_01: commit first (WDC-B0, uncommitted in the child today). W02_02: the simplified single port (wdc 3.2), standalone. |
| 21 | **Publication model.** build R2 (per batch: child commit and push, then the parent gitlink with consumer edits, per CLAUDE.md) conflicts with wdc.verify 1 (PC2 R2-3 I01/I02 binding) and with toolkitjs B5a/B5b. B5a/B5b leave the parent "on the old gitlink", but `ProjectReference` into the child working tree breaks the parent build as soon as B5a lands. | Per-batch publication (CLAUDE.md wins over a planner addition), confirmed once by the maintainer. Each library API batch carries its consumers in the same parent commit: TOOLKITCS-B3 with library B5, the steamhost explicit-client edits and overlay B8's outcome mapping. TOOLKITJS B5a and B5b merge. WDC-B7 includes the NVIDIA plugin (wdc.verify 8). Child-local validation runs per batch (item 2.5). |
| 22 | **Linked sources.** BUILD-B6 moves `NativePackageSource` to `src/Shared/Interop`, while library says it belongs in the Device Lab project. BUILD-B6 also moves `ScheduledTaskXml`, which INSTALL-008 replaces with a COM `DeelevatedTask`. | `NativePackageSource` goes into WSGM.DeviceLab (its only consumer), subject to the licence decision Q1. BUILD-B6 runs after the install and session batches and moves only files whose sharing survives them. |
| 23 | **Settings save.** config B1/B2b/B4 and settings B1/B3/B7 all edit `SettingsViewModel.Save.cs`. config C3 routes Settings' profile writes through `ProfileEdits`, while settings B1 rebuilds the merge. | SETTINGS-B1 (data loss) first, merged with B2's field table (settings.verify 1). config B4 then adds `ProfileEdits` inside the new merge. |
| 24 | **Steam Input lease.** OVERLAY-B4 adds an `ISteamInputLease` port, while INPUT-B4 makes the lease an instance that Program owns (input.verify). | The overlay takes the input owner's concrete instance. No mirroring port. |
| 25 | **Activation sources.** SESSION-B3 waits on overlay's `HotkeyService`/`OverlayController` constructor change. WINSVC-B10 edits `HotkeyService` and `OverlayController` L215. OVERLAY-B3 moves activation out of the controller. | OVERLAY-B3 owns hotkey, chord and swipe construction. SESSION-B3 follows it. WINSVC-B10 does not touch these. |
| 26 | **Config B2a timing.** It threads the instance store through nearly every domain's constructors, and library B7, steamhost B6, settings B7, overlay C2 and SDK-B8 all wait for it. | Run config B2a early: mechanical, behaviour-neutral, before B1 (config.verify 10) and before structural domain batches, so constructors are not rewritten twice. |
| 27 | **Steam autostart restore.** config B2b (strict `RestoreAll`), WINSVC-B3 (takeover instance, strict restore) and install.verify V-004 (apply only on false-to-true) all edit `SteamAutostartService`/`OtherManagers`. | WINSVC-B3 owns both files, including CRIT-001 and V-004. config B2b supplies only the strict read. |

**Hotspots.** `ShellSession*.cs` is edited by 10 domains and `DeviceCoordinator.cs` by 6 (device, input, steamhost,
config, sdk, packages). Serialization alone does not prevent rework, so the order below matters.

## 4. Recommended dependency order (cross-domain)

1. **Live defects, small:** USER-001, the never-strand batch (2.7), DEVICE-001 and DEVICE-V-001 small fixes, the
   SESSION V-001 exit-cleanup trigger, SETTINGS-B1+B2, LIBRARY-B0 then the B1 subset without Q1, INSTALL-001/002/003
   (after their batches exist), CRIT-001. Admitted set per conflict 20.
2. **Foundations:** config B2a, then B1, then B2b. SDK-B1 to B4. WDC-B0, W02_02, WDC-B1. BUILD-B1, B2, B4 and B5.
3. **Libraries with consumers in the same parent commit:** WDC-B2..B9 with the winsvc consumers. TOOLKITCS-B1..B5
   with TOOLKITJS-B1..B5 (merged) and the library, steamhost and overlay consumer edits.
4. **Domain structure:** input, then device. steamhost B4 before DEVICE-B6/B8. winsvc, library, overlay (B3 before
   SESSION-B3), settings, Lab, packages (Device 12 group), GPU and IR.
5. **Session composition and shutdown last** (SESSION-B3 with MessageWindow, B6, B7). They consume every owner's
   `CloseAdmission` and `StopAsync(Deadline)`.
6. BUILD-B6/B8 after the moves, then docs, Z03, Z04, M01.

No hard cycle remains once conflicts 4, 8 and 25 are resolved as above.

---

## 5. Prior high findings

- The Claude ledger has no High or Critical row (parsed severities: 49 Medium, 153 Low, 133 Nit). All eight Codex
  Highs are referenced:
  - A02-F003 (SDK-B2) and A02-F005 (SDK-B1): confirmed.
  - A02-F007: A02_02 step 1, kept.
  - **A02-F008: sdk.verify recommends no-change, which contradicts admitted A02_02. The maintainer decides (conflict
    19).**
  - A02-F010: PACKAGES-B2, needs the Device 12 placement.
  - A02-F011: labcore, where the verifier replaces per-session locks with `Monitor.TryEnter`.
  - A02-F012: A02_03, kept.
  - A02S01-F001: gpuir B8 retires the pending learn with a terminal reply.
- **New high findings without a batch:** USER-001 (2.1), INSTALL-001, INSTALL-002, and INSTALL-V-001 (plausible; the
  single-file host extracts natives to user-writable temp for elevated setup and the SYSTEM service).
- **Referenced only through ranges** ("A02S01-F001..F011", "A02-F015..F018"): A02-F016 and A02S01-F003, F004, F006,
  F009, F010, F011, F013 and F014. Z03 must give each one an individual disposition.
- **Unreferenced Low/Nit ledger IDs (43):** U02A-SUTC-015, 016, 019, 024, 026, 027, 028, 041, 043, 046, 047, 048,
  050; U02B-SUTC-007, 018, 020, 025, 029, 030, 039, 043; U03A-SUTS-008, 011, 012, 013, 017, 032, 033; U03B-SUTS-025,
  026, 028, 032, 034, 040, 043; U04A-LFA-012, 021, 024, 030, 032, 033, 035; U04B-LFA-012.

---

## 6. Over-engineering flags not already raised by a verifier

| Where | Mechanism | Simpler shape |
| --- | --- | --- |
| Plan B2, M01-31, toolkitjs R2 | 200-entry picker pages and a "More" control the picker does not have today; spool, worker caps, provider outcomes | Conflict 7: whole list, worker, cancel, stale ticket |
| toolkitjs R17/B7 | `hostId` plus per-bootstrap nonce | Asset hash, schema, generations (conflict 6) |
| steamhost B1 | Storage projection cache with revision | Lock-scope fix. The WINSVC-B4 inventory is the owner (conflict 15) |
| overlay B4 | `ISteamInputLease` port | The input domain's concrete lease instance (conflict 24) |
| packages R3/B5 | Per-instance `PluginTraceSink`, owned clock through contexts | SDK keeps `PluginTrace`/`ActiveClock` (conflict 18) |
| config L185/B2a | `UserDataContext.ForInteractiveUser`, user identity | Root plus mutex name only (conflict 17) |
| WINSVC-B2 + DEVICE-B6 | Two new owners for the same Windows power statics | One owner (conflict 13) |
| library B7 + config B2a; library B3 + config B2b | The same store roots and read outcomes implemented twice | config owns them (conflict 16) |
| labcore addition 2 | One-time migration of the Lab side files into `LabMachineChanges` | Read the existing files where they are. A dev tool needs no migration layer |
| config B3 / steamhost B7 | Fold-id migration | None (conflict 8) |
| config B7 + WINSVC-B6 | A new display-mode owner type | WINSVC-012 only (conflict 10) |
| `SoundPackLibrary`, `DeviceProfileValidation` (existing code) | 1 MiB per-asset skip, 16 MB budget, 512 entries, 64-point curve | Remove (CRIT-002, CRIT-005) |
| `NativeQamHybridCoreService` (existing code) | Read-before-write and `_requiresRead` latch | HC write-through (CRIT-003) |

No report proposes an Avalonia or VIIPER change, an Explorer kill, a per-sample allocation, or a retry of an uncertain
write. Each verifier already flagged its domain-local over-engineering: device `CapabilityCommandPolicy` and the
lifecycle `Task.Run` worker, input B7 options, labcore per-session locks, library `LibraryDisk`/B3 task set, gpuir
written-value overlay, settings flag pair and native vectors, winsvc `PowerPolicyLane`/claims/allocation assertion,
A02_02 latch, SDK clock plumbing, overlay pins owner, and steamhost dynamic modules. They are not repeated here.
