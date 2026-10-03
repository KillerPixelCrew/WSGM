# Domain review: configuration store, AppConfig, profiles, logging, recovery journals, display/audio profiles

Reviewer: Claude (read-only stage). Baseline: `master` 1329813f, clean tree.

Files read in full: `src/WSGM/Core/ConfigStore.cs` (1409), `Core/AppConfig.cs` (1074), `Core/Profiles/{ProfileConfig,ProfileEdits,ProfileFields,ProfileResolver}.cs`, `Shell/ProfileService.cs`, `Shell/ProfileFanOut.cs`, `Core/Log.cs`, `Core/AtomicFile.cs`, `Core/JsonRead.cs`, `Core/DisplayProfiles.cs`, `Shell/AudioProfileService.cs`, `Shell/GameModeReturnRecovery.cs`, `Core/GameModeLaunchConfiguration.cs`, `Shell/ShellSession.Config.cs`, `Core/BootManifest.cs`, `Core/BootManifestWriter.cs`, tests `Core/ConfigurationTests.cs`, `Core/LogLevelTests.cs`, `Shell/ProfileServiceTests.cs`, `Shell/ProfileFanOutTests.cs`, `Builders/PerformanceBuilders.cs`. Read in part (callers/contracts): `Program.cs`, `ShellSession{,.Modes,.Shutdown,.Performance,.Actions}.cs`, `SettingsViewModel.Save.cs`, `OverlayController.cs`, `Installer.cs`, `OtherManagers.cs`, `SteamAutostartService.cs`, `ShellRegistration.cs`, `CommonPluginConfiguration.cs`, `ArtworkStateStore.cs`, `QuickAccessFolds.cs`, `Core/Themes/ThemeInstaller.cs`, `LibraryFilter.cs`, `DeviceConfiguration.cs`, WDC `DisplayEdid.cs`, ledger, audits, batches.

Prior coverage note: `ProfileService`, `ProfileFanOut` (U06B), `AudioProfileService` (U07B) and `DisplayProfiles` (U11B) sat in units the Claude audit never started. Every finding below on those files is new. A02_02 (SDK `DeviceRecoveryJournal`) is the only admitted batch touching a recovery journal; it is outside `src/WSGM` and was not re-reviewed, but its defect class (unreadable treated as absent) recurs here in CONFIG-004/037.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "ConfigStore is 1,409 lines; file paths come from Log.Directory, feature normalizers and enum repair are centrally hand-listed, recovery readers cannot distinguish missing data from corrupt/transiently inaccessible data" (plan L46) | accurate | ConfigStore.cs:105, 183-229, 419-541, 122-139 (catch-all at 129 turns a sharing violation from `File.ReadAllText` at 160 into "corrupt") | Add: 17 files / 22 sites use `Log.Directory` as data root, not just ConfigStore (list in §4). |
| C2 | "Reload applies about 30 consumers in one callback after lenient ConfigStore.Load" (L42) | accurate | ShellSession.Config.cs:104-158 (26 apply steps after `ConfigStore.Load` at 109) | Also: the shell triggers this reload itself after every profile edit (CONFIG-001). |
| C3 | ProfileService is the "sole sparse Global/per-game policy and explicit edit authority" (L66) | partially | In-shell: yes, all overlay/QAM/Steam writes go through it. Cross-process: Settings writes `Profiles.Global.ControllerTarget` and fan-curve references directly in its save merge, SettingsViewModel.Save.cs:145, 406-421, 455-462 | ProfileService is the in-shell owner; `ProfileEdits` (pure) is the sole edit rule set, and Settings must call it inside its store transaction. |
| C4 | ProfileService depends on "store transaction port and diagnostic sink" (L66) | partially | The transaction port already exists as a delegate (ProfileService.cs:44, 55-62). The production adapter ignores the edit's change flag (ShellSession.Config.cs:274-287). | Keep the delegate; fix the adapter. Drop the diagnostic sink: Log is adequate here and no defect needs injection. |
| C5 | Preserve `ProfileService`, `ProfileFanOut` as useful owners (L75) | accurate | Both are small, single-purpose, already testable with fakes. | Keep; add admission close (CONFIG-018/019). |
| C6 | SessionConfigReloader with "ordered subscribers" and "apply consumer slices" (L63-64) | partially (over-mechanism) | Today's fixed apply list is fine; defects are no per-step isolation (U05-LFB-018), lenient load (U05-LFB-002), self-echo (CONFIG-001), stale profile publication (CONFIG-002). | A fixed explicit method list with per-step try/catch, whole-document equality skip, and the Profiles section routed to `ProfileService.ReloadAsync`. No subscriber registry. |
| C7 | "Keep config.json as the single serialized atomic authority. Do not introduce a multi-file transaction for preferences/recovery" (L97) | partially | Recovery state also lives in `hidhide-ownership.json` (Program.cs:638, DeviceCoordinator.cs:150), `PluginState/` journals, the theme journal (ThemeInstaller.cs:136-158), pending plugin removals. Settings already runs a multi-file transaction under the config lock: config.json + splash images + boot.json (SettingsViewModel.Save.cs:526-576). | State the actual inventory. config.json is the authority for preferences and the registry/autostart/wrapper/display/return snapshots; sidecars own their own records and each needs the same read-outcome rule (CONFIG-037). |
| C8 | Instance store takes "UserDataContext, atomic file backend, feature normalizers and diagnostics" (L97) | over-engineered | Normalizers are pure static rules (the plan itself says pure rules stay static, L54). `AtomicFile` is already a tested-in-place mechanism; temp roots give real file behavior. | `ConfigStore(UserDataContext)` only. Normalizers static and co-located with section types; they return diagnostics the store logs. No file backend port. |
| C9 | "Never hold the thread-affine named mutex across await. File I/O ... finish synchronously on the store's worker" (L97, F01 step 1 "strict mutation worker") | partially | The rule is already honored: every store call is synchronous and async callers wrap it in `Task.Run` (ShellSession.Config.cs:277, DeviceCoordinator.cs:2069, ShellSession.Modes.cs:822). | Keep the rule, drop the dedicated "store worker": a synchronous API is sufficient. |
| C10 | "Production keeps Local\WSGM.Config; tests use Local\WSGM.Tests.Config.<guid>" (L97) | accurate, needed | ConfigurationTests.cs:583-766 contend on the user's real mutex (U04A-LFA-022). | Mutex name comes from `UserDataContext`. |
| C11 | Read returns Loaded/Absent/Corrupt/Unreadable/UnsupportedSchema; "Initial UI can show actionable failure" (L99) | accurate, with a UI caveat | ConfigStore.cs:122-139 collapses all into defaults. | No new UI. Report through existing chrome (Settings `StatusText`, existing log). |
| C12 | "Preserve a copy only for a parse-corrupt file; transient IO/lock failures do not quarantine" (L99) | accurate | ConfigStore.cs:129-136 quarantines on any exception. | Add: quarantine once per distinct content, delete the keep-5 prune (CONFIG-008). |
| C13 | "Retain a validated previous `.bak` ... An explicit repair path can present that backup later" (L99) | over-engineered | No defect needs it: atomic replace never leaves a torn file, and a corrupt file is preserved. An "explicit repair path" is a new UI workflow, which requirement 9 excludes. | Remove `.bak` and the repair path. Keep the durable flush and the quarantine copy. |
| C14 | "Durable temp flush precedes atomic replace" (L99) | accurate | ConfigStore.cs:1165 and BootManifest.cs:121 pass `durable: false`; AtomicFile.cs:64-70 already supports it. | One-argument fix. |
| C15 | 0->1 migrator "runs under the existing lock, makes a durable untouched pre-migration backup ... next startup can retry" (L101) | partially (heavier than needed) | Plan keeps every wire/property name. The real v0 differences are: a missing `Id` on custom tabs, missing `SchemaVersion`. | Migrate on read (pure, in memory). Persist on the next strict write. Write one `config.v0.json` copy with CreateNew before the first v1 write. No separate migration phase. |
| C16 | "Unknown preference enum names/numbers use each property's actual initialized default through source-generated metadata" (L101) | partially inaccurate | Current deliberate repair values differ from initializers: `FilterNode.Kind` repairs to `Installed` (ConfigStore.cs:93-102) while its initializer is `Collection` (LibraryFilter.cs:141, enum member 0). A null `Splash` repairs to the classic `new SplashConfig()` (ConfigStore.cs:538) while the `AppConfig.Splash` initializer is the 2.0 preset (AppConfig.cs:747). Nullable `ControllerTarget` repairs to `SteamDeckComposite` by name (ConfigStore.cs:317) but to null by number (819-822). | Generic repair with explicit per-type templates: FilterNode uses `FilterDefaults`, nullable enums become null, and a null Splash keeps the classic default. Pin each one with a fixture. |
| C17 | "Unknown recovery enum/type values preserve their raw recovery subtree and mark that operation unreadable" (L101) | over-engineered | Recovery enums: `RegistryValueKind` (AppConfig.cs:919, 935), `SteamAutostartRecord.Kind/Scope` (129, 132). A per-record raw-JSON holder is new mechanism for a downgrade-only case. | An undefined recovery enum makes the document `Corrupt`: the file is preserved untouched, strict writes refuse, restore reports Unreadable. No guessing and no per-record raw holder. |
| C18 | "Unknown optional fields are retained in extension data through migration" (L101) | no concrete defect | Plan keeps every wire name, so 2.0 -> 2.1 drops nothing. | Drop it. If wanted later, `[JsonExtensionData]` on the `AppConfig` root only. |
| C19 | "Feature validators return diagnostics instead of writing Log" (L101) | accurate | ConfigStore.cs:576-579, 621-623 (U04A-LFA-028) | Keep. |
| C20 | Custom-tab deterministic IDs "persisted once" (L103) | accurate | ConfigStore.cs:507 and the initializer AppConfig.cs:505 both mint random GUIDs per load (U04A-LFA-019) | Keep. Note the initializer path too: a missing property never reaches Normalize as blank. |
| C21 | "Old fold titles map to stable section IDs" listed under config migration (L103) | partially | Folds live in `quick-access-folds.json` (QuickAccessFolds.cs:28), owned by the Steam UI host (SteamUiSessionHost.cs:284) | Route to the Steam UI domain. It uses the shared `StateFile` read rule. |
| C22 | "Copy IR pairing/library/scene files and package temporary-state journals unchanged" (L103) | inaccurate wording | No data-root relocation is planned anywhere | "Leave in place." Nothing is copied. |
| C23 | Setup one-shots carry an authenticated target-user root (L105) | accurate need | Log.cs:80-81 derives the root from the running token's LocalAppData (U04A-LFA-005) | `UserDataContext` must also be constructible in the early restore-shell path without Log/config (root CLAUDE.md safety rule). |
| C24 | B3: close config publication and profile fan-out at T0; "Profile fan-out awaiting a consumer" is a busy owner (corrections L27, L48) | accurate need | ProfileFanOut.cs:76-84 awaits the worker without a bound, and the shell awaits that before device cleanup (ShellSession.Shutdown.cs:94-96). ProfileService learning writes use `CancellationToken.None` and are untracked (ProfileService.cs:439-450). | `ProfileFanOut.Close()` and `ProfileService.Close()` are synchronous admission closes. Shutdown observes their completion tasks only to the preliminary cutoff. |
| C25 | B3 row "Config read/normalizer worker: forbidden: dispose its active file/store resources" | stale under the refined design | With lock-free reads (CONFIG-012) a reader holds only a share-delete file handle for milliseconds. | The row becomes "config reload task: forbidden to publish after admission close". |
| C26 | F01 acceptance "fixture migrations preserve ... across injected crash points" | over-engineered | — | Use state fixtures instead of injection: leftover `.tmp`, an existing `config.v0.json`, a v0 file, a v1 file, a corrupt file, and a file held open with FileShare.None for Unreadable. |
| C27 | "Current sparse profile fallback, native per-app GPU values/executable bindings, device identities and plugin instance/revision keys remain stable" (L101) | accurate | ProfileFields.cs:60-156 key formats; ProfileConfig.cs; CommonPluginConfiguration.cs:96-111 | Keep. |
| C28 | Ledger U05-LFB-017, U04A-LFA-001/002/004/007/009/016/019/022/028/029/031, U04B-LFA-004/009, U05-LFB-002/018, PV10-001/002/015/016, PV11-005/016 | confirmed against current code | See CONFIG findings citing them | PV10-001 is incomplete: `GameLibraryConfig.DefaultMode`/`ArtworkPreference` names are not repaired either (GameLibraryConfig.cs:51, 60; absent from ConfigStore.cs:183-229). |

## 2. Findings

Severity scale: critical / high / medium / low / nit. "Covered" cites ledger IDs; "NEW" means no prior record.

**CONFIG-001 (medium, NEW): every profile edit rewrites config.json and the shell then reloads itself.** `MutateProfilesAsync` calls `edit(config.Profiles)`, discards its bool and calls `ConfigStore.Mutate`, which always saves (ShellSession.Config.cs:274-287, ConfigStore.cs:1194-1201). The ProfileService constructor documents "saves it when the edit reports a change" (ProfileService.cs:50-54), so the adapter breaks its own contract. No-op edits, `LearnExecutable` false results, and resets with nothing to reset all write the file. Every write fires the shell's FileSystemWatcher, which runs the full 26-step reload (ShellSession.Config.cs:104-157). That reload includes `ApplyDeviceConfig` -> `coordinator.ApplyConfigAsync` and a second RTSS `ApplyProfilesAsync`. Plugin declaration cache writes (PluginSettingsCoordinator.cs:150-160) and widget-pin edits echo the same way. Fix: `ConfigStore.Update(Func<AppConfig,bool>)` saves only on true. The reloader skips a document equal to the last applied one, and the Profiles section reloads only `ProfileService`.

**CONFIG-002 (medium, NEW): reload can publish an older profile store after a newer committed edit.** `ProfileService.ApplyConfig` does not take `_writeGate` (ProfileService.cs:110-130). The reload loads at T1 on a worker and applies on the UI thread later (ShellSession.Config.cs:106-121). A `MutateAsync` committed and published at T2 > T1 (ProfileService.cs:363-405) is then replaced by the T1 copy. The fan-out writes the old values to RTSS/device/GPU until the T2 watcher reload rewrites them. The generation check on the reload (lines 108, 112) only helps if the T2 watcher event already arrived. Fix by serialization rather than a new generation: `ProfileService.ReloadAsync()` awaits `_writeGate` and re-reads profiles through the store port before comparing.

**CONFIG-003 (high, covered by U05-LFB-002 / U04A-LFA-002 as a consumer instance not enumerated): a corrupt or unreadable config re-arms Game Mode sign-in.** Session start writes boot.json from `_config` (ShellSession.cs:392). `_config` came from lenient `ConfigStore.Load` (Program.cs:102), which returns defaults on any failure: `StartAtSignIn=true`, `StartMode=Game` (AppConfig.cs:763, 770). A user's Desktop or no-sign-in choice, or a crash-loop disarm (Program.cs:454-480, BootManifestWriter.cs:52-56), is overwritten. Fix: project boot.json only from a `Loaded`/`Absent` read.

**CONFIG-004 (high, covered U04A-LFA-002, U05-LFB-002, PV10-002, PV11-002): lenient load conflates absent, corrupt and transient.** ConfigStore.cs:122-139, 155-162. A sharing violation during another process's replace, or an AV hold, is quarantined and turned into live defaults. Runtime consumers include ShellSession.Config.cs:109, ShellSession.Modes.cs:605, 753, 858, 874, LibraryTabsView.cs:90/375, and Settings (SettingsWindow.axaml.cs:138, SettingsWindowServices.cs:25).

**CONFIG-005 (medium, covered U04B-LFA-004, U04A-LFA-002, U04B-LFA-009): restore paths read lenient defaults and report success.** Installer.cs:35/45/72 (three separate loads), SteamAutostartService.cs:146-171, OtherManagers.cs:456-475, Program.cs:788, ShellSession.Modes.cs:858/874 (the return layout falls back to none).

**CONFIG-006 (medium, covered U04A-LFA-001 / PV10-001, extended): the enum-name repair list is incomplete.** One unknown name quarantines the whole file. Missing from ConfigStore.cs:183-229: `LogVerbosity` (AppConfig.cs:842), `PreviousShellValueKind` / `PreviousStartupToGamingHomeValueKind` (919, 935), `SteamAutostartRecord.Kind/Scope` (129, 132), and, NEW instance, `GameLibrary.DefaultMode` / `ArtworkPreference` (GameLibraryConfig.cs:51, 60).

**CONFIG-007 (low, NEW): unknown values in the optional controller target are handled inconsistently.** An unknown name becomes a `SteamDeckComposite` override (ConfigStore.cs:317). An unknown number becomes null (819-822). The name path invents a per-game override the user never chose, which contradicts the `RepairOptionalEnum` rationale (387-401). ConfigurationTests.cs:121-125 locks in the name behavior. Fix: nullable enums repair to null, and update the test.

**CONFIG-008 (medium, NEW; related PV10-002): preserved corrupt copies multiply and are then pruned.** `PreserveCorruptFile` copies the file on every lenient load (shell reload, Settings, elevated one-shots) and `PruneCorruptFiles` keeps the newest five by mtime (ConfigStore.cs:1096-1155). A user who edits a broken file repeatedly loses the earlier preserved versions, which may be the only copy of the registry snapshots. That breaks the no-arbitrary-limits rule. Fix: quarantine only on parse failure, one copy per distinct content (a content hash in the name, CreateNew), and no prune.

**CONFIG-009 (medium, covered U04A-LFA-009 / PV10-007): config.json and boot.json are replaced without a durable flush.** ConfigStore.cs:1165, BootManifest.cs:121.

**CONFIG-010 (medium, covered U04A-LFA-007 / PV10-006): ConfigStore is a static god class.** It mixes IO, locking, JSON repair and 12 feature normalizers, and there is no schema version. AppConfig.cs is a 975-line flat aggregate of 15 types plus the JSON context.

**CONFIG-011 (medium, covered U04A-LFA-004/005, PV10-004): the static data root.** `Log.Directory` (Log.cs:80-81) is used at 22 sites in 17 files: AnimationLibrary.cs:42, BootManifestWriter.cs:15, ConfigStore.cs:105/1106/1135/1162, ImportStateStore.cs:90, QuickAccessFolds.cs:28, SoundPackLibrary.cs:30, SplashAssets.cs:46, SteamInputShim.cs:169, ThemePaths.cs:33, UnelevatedLauncher.cs:47/57, UpdateChecker.cs:68, Program.cs:638/819, SettingsViewModel.System.cs:271-292, ArtworkStateStore.cs:14, AutoTdpTraceRecorder.cs:71, DeviceCoordinator.cs:150, SdFormatManager.cs:1102, ShellSession.Config.cs:94, ShellSession.cs:302.

**CONFIG-012 (low, NEW; simplification): the lock machinery exists only to support nesting.** The re-entrancy depth counter, out-of-order dispose rules, degraded read scopes and `HasExclusiveOwnership` (ConfigStore.cs:1203-1230, 1264-1408) exist because callers nest `AcquireLock` + `LoadForMutation` + `Save` + `Mutate` (ShellSession.Config.cs:31-43, CommonPluginConfiguration.cs:54-58, SettingsViewModel.Save.cs:530-560). Readers also take the mutex and wait up to 2 s, although every write is an atomic replace. Fix: readers open the file with `FileShare.ReadWrite | FileShare.Delete` and take no lock. Writers use one `Transaction` scope that hands them the loaded document and a `Save`. Delete the depth machinery and its three tests.

**CONFIG-013 (low, covered U04A-LFA-028): normalizers write Log.** ConfigStore.cs:576-579, 621-623.

**CONFIG-014 (low, NEW; no-arbitrary-limits): load-time normalization truncates or drops content by length.** Game profile names are truncated to 80 (ConfigStore.cs:684-687) and authored profile names to 48 (598-601). `DisabledSources` ids over 32 characters are dropped (727). Preset references with plugin id > 128 or preset id > 64 are dropped (863). Audio endpoint ids over 512 drop the endpoint (943), and names over 256 are dropped (954). Separately, the importer renames a profile to its id when the title is longer than 80 (ProfileEdits.cs:264-271). Fix: type and shape checks only on load and import. Editor input limits stay where they are UI.

**CONFIG-015 (low, NEW): a hardcoded spatial-audio allow-list drops formats.** ConfigStore.cs:972-981 duplicates WDC `SpatialAudioFormats`. A format Windows reports that is missing from the list is silently dropped from a saved preference. The apply path already validates against live `SupportedSpatialFormats` (AudioProfileService.cs:303-305). Delete the allow-list.

**CONFIG-016 (low, NEW; duplication): normalizers re-state defaults.** NormalizeGameLibrary re-states the initializer defaults (ConfigStore.cs:714-722 vs GameLibraryConfig.cs:51, 60). The `Definite` calls repeat every enum default by hand (421, 554, 657, 889-890, 1016-1022, 1064-1065, 1088-1089). The generic repair in B1 removes these.

**CONFIG-017 (low, covered U04A-LFA-019 / PV10-015): custom tabs get a random id per load.** Two paths: the initializer (AppConfig.cs:505) and Normalize (ConfigStore.cs:507).

**CONFIG-018 (medium, covered by B3 / U05-LFB-001 family): fan-out disposal can block shutdown indefinitely.** `ProfileFanOut.DisposeAsync` awaits the worker with no bound (ProfileFanOut.cs:58-87). Shutdown awaits it before the safety-critical device cleanup (ShellSession.Shutdown.cs:89-106). A consumer that ignores cancellation, such as a plugin call stuck in `ApplyProfilesAsync`, blocks every later step.

**CONFIG-019 (medium, NEW): learning writes are untracked and survive shutdown.** `LearnRunningExecutable` starts `Task.Run` chains that call `MutateAsync(..., CancellationToken.None)` (ProfileService.cs:415-461). They are not tracked or joined, and admission never closes, so config.json can be written during or after shutdown. `LearningIdle` is exposed "for tests" (line 77). Fix: add `Close()`, make learning use the service's own cancellation, and repurpose `LearningIdle` as the completion task shutdown observes.

**CONFIG-020 (nit, NEW): change detection serializes the store under the lock.** It serializes the whole profile store twice under `_gate` on every mutation and reload (`SameStore`, ProfileService.cs:482-486; also 324-326, 161-163). Cache the current snapshot's JSON once and compare outside the lock.

**CONFIG-021 (nit, NEW): two JSON clone mechanisms.** `ConfigStore.CloneJson` (ConfigStore.cs:1241-1245) and `ProfileFields.Copy` (ProfileFields.cs:323-329). This also makes `Core/Profiles` depend on the persistence context. Keep one `ConfigJson.Clone`.

**CONFIG-022 (low, NEW): Settings writes profile fields directly.** See C3. SettingsViewModel.Save.cs:145, 406-421, 459.

**CONFIG-023 (nit, NEW): the overlay-test profile store duplicates a test builder.** The in-memory store in production (ShellSession.Config.cs:290-302) duplicates PerformanceBuilders.cs:10-21. Keep one internal `InMemoryProfileStore` used by both.

**CONFIG-024 (medium, covered U05-LFB-017, PV11-005/016; extended NEW parts): GameModeReturnRecovery mixes restoring with clearing.** It is static with a static gate (GameModeReturnRecovery.cs:13). It clears the record only when Explorer is running, while returning "complete" either way (92-103). NEW parts:
- The clear logic is duplicated (28-42 vs 94-100).
- The fingerprint uses the reflection serializer while every other config path uses source-gen (30, 37, 65, 96).
- `WaitAsync` abandons running work that still holds the gate and can still mutate config (44-50).
- The "desktop audio overrides captured audio" policy is duplicated in ShellSession.Modes.cs:874-875.

**CONFIG-025 (low, NEW): AudioProfileService has a test seam and no tests.** Its `IAudioProfileOperations` port is "isolated for deterministic tests" (AudioProfileService.cs:28-52), but no test uses it. The constructor builds the native adapter by default (67-71), and recovery builds its own instance (GameModeReturnRecovery.cs:85).

**CONFIG-026 (nit, NEW): Apply probes capabilities twice.** One audio apply probes playback capabilities, including the format-support sweep, twice: once for format and once for spatial (AudioProfileService.cs:289, 302).

**CONFIG-027 (low, NEW consumer instance of U01-022): a late callback can hit a disposed semaphore.** In `ResolveEndpoint` the watch callback releases a `SemaphoreSlim` that is disposed right after the watch (AudioProfileService.cs:339-360). With WDC `EndpointWatch.Dispose` unsynchronized, a late native callback can throw ObjectDisposedException on the native thread. This resolves when W01 makes watch disposal quiescent.

**CONFIG-028 (nit, NEW): DisplayProfiles is misnamed and has stale docs.** It is a static primary-display mode facade, not "profiles". Its doc refers to a "display-profile path above" that does not exist (DisplayProfiles.cs:186-192). `ReadPrimaryOperatingPoint` observes the CCD topology twice (33-37).

**CONFIG-029 (low, NEW; library boundary/duplication): WSGM parses EDID itself.** WSGM reads EDID from the registry Enum key and parses detailed timings in `EdidModes` (DisplayProfiles.cs:288-352, EdidModes.cs). WDC `DisplayEdid.ReadModes(target)` parses the same descriptors through the exact monitor interface. Use WDC after a fixture equivalence check (W02).

**CONFIG-030 (low, NEW): `--verbose` is lost at the first reload.** Program says the flag wins for the run (Program.cs:103-106, 685-706). `OverlayController.ApplyConfig` resets verbosity from config on every reload (OverlayController.cs:418-421), and with CONFIG-001 the first profile edit triggers a reload. Verbosity policy also sits in the overlay controller. Fix: the reloader applies `flag || config.LogVerbosity`.

**CONFIG-031 (low, covered U04A-LFA-016 / PV10-016): log rotation can stall every logging thread.** Rotation runs inside `Gate` with a 1 s mutex wait (Log.cs:346-357, 268-269).

**CONFIG-032 (nit, partially covered U04A-LFA-026): small Log duplicates.** `Log.Observe` duplicates `TaskFaults.ObserveFaults` (Log.cs:171-196). The private `SetMinimumLevel` wrapper adds nothing (114-117).

**CONFIG-033 (nit, NEW; reviewed, no change): the Log.Change key cap.** `Log.Change` clears its key map at 512 keys (Log.cs:60-65, 235-238). It bounds memory, not content: only repeat counts reset. It complies with the no-arbitrary-limits rule. Keep it.

**CONFIG-034 (low, NEW; test quality): LogLevelTests only assert enum order and a getter.** LogLevelTests.cs:18-58. `Change` suppression, held counts, rotation and append sharing are untested.

**CONFIG-035 (low; mixed covered/NEW): ConfigurationTests quality.**
- NEW: getter-only tests (lines 769-793, 887-890, 970-978, 990-999, 1227-1236) assert property initializers instead of the upgrade contract. Replace them with old-shape JSON fixtures.
- Covered: the real mutex in lines 583-766 (U04A-LFA-022); the reflection serializer in 1211-1225 (U04A-LFA-023); the misplaced splash import test at 552-565 (PV10-026).
- NEW: there is no test of Load/Save/Mutate/quarantine against a file.

**CONFIG-036 (low, NEW; partially U04A-LFA-025/008): untested owners.** No tests for `GameModeReturnRecovery`, `AtomicFile`, `DisplayProfiles`, the production `MutateProfilesAsync` adapter (the one with CONFIG-001), or `BootManifestWriter` (whose tests re-implement the projection).

**CONFIG-037 (medium, NEW; same class as U04A-LFA-002 / A02-F007): sidecar stores overwrite unreadable state.** `ArtworkStateStore` treats IO, access or parse failure as an empty state and the next `Write` overwrites the user's artwork links (ArtworkStateStore.cs:84-96, 124-140). `QuickAccessFolds` does the same for folds (QuickAccessFolds.cs:101-121, 66-90). `ImportStateStore` already sets corrupt data aside (ImportStateStore.cs:323-366); that is the pattern to share.

**CONFIG-038 (low, NEW): the theme update journal enforces caps its writer does not.** `ThemeUpdateJournal` uses the reflection serializer, and `Recover` rejects journals over 128 KiB or 256 names (ThemeInstaller.cs:189-201). The install path writes the journal without that bound (136-158). An archive with more than 256 top-level entries leaves a marker that throws on every later theme load. Fix: use a source-gen context and remove the caps, keeping the `SafeName` and containment checks.

**CONFIG-039 (low, NEW; consumer side of CONFIG-012): three callers depend on nested lock scopes.** `CommitWsgmSetting`, the Settings save and `ApplicationPluginConfigurationStore.Read` (ShellSession.Config.cs:31-43; SettingsViewModel.Save.cs:530-576; CommonPluginConfiguration.cs:54-58).

**CONFIG-040 (low, partially covered U05-LFB-019): views and the session touch config directly.** Views do config IO: LibraryTabsView.cs:90, 375 and SettingsWindow.axaml.cs:138. The session mutates the shared snapshot in place from a worker (ShellSession.Actions.cs:114-115, ShellSession.Modes.cs:824).

**CONFIG-041 (nit, mixed): AppConfig housekeeping.** Duplicate summary at AppConfig.cs:738-746 (U04A-LFA-029). The model depends on Themes (AppConfig.cs:6, 747, 753; U04A-LFA-028). NEW: the JSON contract imports `Microsoft.Win32` for `RegistryValueKind` (AppConfig.cs:4, 919, 935).

**CONFIG-042 (nit, NEW): double normalization.** SettingsViewModel.cs:72 normalizes a config that `Load` already normalized.

**CONFIG-043 (nit, NEW): profile snapshot accessors do repeated work.** `ProfileSnapshot.Game` and `Layers` repeat a linear search on every access (ProfileResolver.cs:146-152). `ProfileFields.Count` enumerates `Enum.GetValues` per call (ProfileFields.cs:311-318). Both run on UI paths.

**CONFIG-044 (low, NEW): plugin manifests are cached inside the user's config file.** `PluginSettingsScope.Declaration` (PluginSettingsCoordinator.cs:150-160) lives in config.json, so every manifest refresh rewrites the file and triggers a reload. Keep the location, since moving it costs a migration, but write only on change (CONFIG-001 fix).

**CONFIG-045 (nit, NEW): a fan-out test depends on timing.** ProfileFanOutTests.cs:68 uses a 300 ms `Task.Delay`. Use a gate-controlled consumer.

**CONFIG-046 (low, covered PV11-001): an unobserved fire-and-forget in reload.** `_ = ApplyCommonPluginConfigAsync(config)` on reload (ShellSession.Config.cs:211) is not tracked for shutdown.

## 3. Plan refinements

Additions:
1. A shared `StateFile` read/write rule for every per-user JSON file in `src/WSGM`: config.json, artwork.json, quick-access-folds.json, library-import.json, update.json and the theme journal.
   - Reads return `Absent | Loaded(T) | Corrupt (quarantined once by content) | Unreadable`.
   - A store whose last read was not Loaded or Absent refuses to write.
   - This fixes CONFIG-004/037 together and removes four hand-written try/catch copies.
2. `ConfigStore.Update(Func<AppConfig,bool>)` saves only on true (CONFIG-001). The reloader skips a document equal to the last applied one.
3. Boot manifest projection only from a Loaded or Absent read (CONFIG-003).
4. `ProfileService.ReloadAsync()` serialized on `_writeGate` and re-reading through the port (CONFIG-002). `ProfileService.Close()` and `ProfileFanOut.Close()` as B3 admission closes with completion tasks (CONFIG-018/019).
5. Settings routes its two profile writes through `ProfileEdits` inside its store transaction (CONFIG-022).
6. Effective log verbosity (`flag || config`) is applied by the reload owner, not OverlayController (CONFIG-030).
7. Recovery enum policy: an undefined recovery enum makes the document `Corrupt`.

Changes:
- Migration is migrate-on-read, persisted on the next strict write, with one `config.v0.json` CreateNew copy (C15).
- Generic enum and null repair uses explicit templates for FilterNode, Splash and nullable enums (C16).
- Normalizers are static, co-located with their section types, and return diagnostics (C8, C19).
- `SessionConfigReloader` is a fixed list with per-step isolation, not a subscriber registry (C6).
- Recovery and sidecar inventory stated explicitly (C7). Folds go to the Steam UI domain (C21). "Copy" becomes "leave in place" (C22).

Removals, as over-engineering under the simplify and no-arbitrary-limits rules:

| Plan mechanism | Why it goes | Simpler replacement |
| --- | --- | --- |
| `.bak` previous copy and "explicit repair path" (L99) | No defect needs it; it adds UI | Durable flush, atomic replace, one-time quarantine copy |
| Store "worker" thread (L97, F01 step 1) | The synchronous API already honors no-await-under-mutex | Synchronous `Read` / `Update` / `Transaction`; async callers keep `Task.Run` |
| Injected atomic file backend, feature normalizer list and diagnostics sink (L97) | Pure rules, tested with temp roots | `ConfigStore(UserDataContext)` |
| Raw recovery-subtree preservation (L101) | Mechanism for a downgrade-only case | Document-level Corrupt: preserved, writes refused, restore reports Unreadable |
| Extension data on every config class (L101) | Wire names do not change | None, or root-only if ever required |
| Injected crash points (F01 acceptance) | Port only for tests | State-fixture tests |
| Re-entrant config lock depth machinery (current code) | Exists only for nesting | Lock-free share-delete reads plus one writer `Transaction` scope |
| Keep-5 corrupt-copy prune; load-time length caps; theme journal caps; spatial allow-list | No-arbitrary-limits rule | One copy per content; type and shape checks only |
| ProfileService "diagnostic sink" | Not needed | Static `Log` stays the process diagnostic sink. Its file core becomes an internal instance only so tests can run it on a temp path without initializing the singleton. |

## 4. Target design

Owners:
- `UserDataContext` (new, Core): `sealed record UserDataContext(string Root, string ConfigMutexName)`.
  - `ForCurrentUser()` gives `%LOCALAPPDATA%\WSGM` and `Local\WSGM.Config`.
  - The setup/launch domain supplies `ForInteractiveUser(...)`.
  - Path helpers: `ConfigPath`, `File(string name)`.
  - Pure, so it is safe in restore-shell before Log and config exist.
- `StateFile` (new, Core, internal static): `Read<T>(path, JsonTypeInfo<T>) -> StateRead<T>`, `Write<T>(path, value, typeInfo, durable)`, `Quarantine(path)` (content-hash name, CreateNew). Uses `AtomicFile`.
- `ConfigStore` (instance, Core):
  - `ConfigRead Read()`, where `ConfigRead(ConfigReadStatus Status, AppConfig? Config, string? Detail)` and Status is Loaded, Absent, Corrupt, Unreadable or UnsupportedSchema. Only Loaded and Absent carry a config.
  - `AppConfig Update(Func<AppConfig,bool> change)`: strict; throws `ConfigUnavailableException` unless the document is Loaded or Absent; saves only on true.
  - `T Transaction<T>(Func<ConfigTransaction,T> work)`: strict; one writer-lock scope; the transaction exposes `Config` and `Save(AppConfig)`.
  - Durable writes. Migration is applied on read; the v0 copy is written before the first v1 save.
- `ConfigRepair` (new, static): tolerant enum converter factory registered on `ConfigJsonContext` (unknown name or number -> undefined sentinel); `RepairUndefined(AppConfig)` metadata walker with explicit templates. Undefined recovery enum types -> Corrupt. Null non-nullable members get template values; null list elements are removed. Also `Migrate(AppConfig, int fromVersion)`.
- `AppConfigRules` (new, static): `Normalize(AppConfig, ICollection<string> diagnostics)` calls the per-section static rules, co-located with their section types.
- `ConfigJsonContext` moves to its own file. `ConfigJson.Clone<T>` replaces `CloneJson` and `ProfileFields.Copy`.
- `ProfileService` (kept): `ReloadAsync`, `Close`, `Completion`; port `(Func<ProfileConfig,bool> edit) -> stored copy` plus `Func<ProfileConfig?> read`. `InMemoryProfileStore` is shared by overlay-test and tests.
- `ProfileFanOut` (kept): `Close()` (synchronous: unsubscribe, cancel the active pass, refuse queueing) and `Completion`.
- `DesktopReturnRecovery` (instance, replaces the static `GameModeReturnRecovery`): constructor `(ConfigStore, Func<DisplayLayout, CancellationToken, Task<bool>> applyLayout, AudioProfileService audio)`.
  - `RestoreAsync` (never clears), `Pending()` (record plus fingerprint through source-gen), `ClearIfUnchanged(fingerprint)`.
  - `GameModeLaunchRecoveryRules.DesktopAudio(launch, pending)` is shared with ShellSession.Modes.
- `AudioProfileService` (kept): constructed explicitly by the composition root. After W01, the WDC `AudioService` instance replaces `IAudioProfileOperations`.
- `PrimaryDisplayModes` (rename of DisplayProfiles): static until W02 delivers the instance `DisplayService`, then an instance over it. Advertised rates come from WDC `DisplayEdid`.
- `Log` (static facade kept):
  - `Init(name, root)`. `Directory` is deleted.
  - Rotation happens outside `Gate` with a zero-wait try.
  - The internal `LogFile` instance holds the path, rotation and append, so tests can drive it on a temp path without initializing the singleton.

Old symbol -> new owner, ConfigStore.cs (dissolved):

| Old | New |
| --- | --- |
| `MutexName`, `MutexTimeoutMs` | `UserDataContext.ConfigMutexName`; writer timeout constant in `ConfigStore` |
| Splash bounds consts (MinFontSize..MaxAbsoluteCoordinate) | `SplashConfigRules` beside SplashConfig. AppearancePage.axaml literals must still match. |
| `Defaults`, `SplashFieldDefaults`, `PlacementDefaults`, `FilterDefaults` | `ConfigRepair` templates (`FilterDefaults` kept verbatim) |
| `ConfigPath` | `UserDataContext.ConfigPath` |
| `LockDepth`, `HasExclusiveLock`, `ConfigMutex` | Deleted, together with the three depth tests. The writer lock becomes a private acquire/release. |
| `Load` | `ConfigStore.Read` |
| `LoadForMutation`, `LoadCurrentDocument`, `Save` | Private inside `Update` / `Transaction` |
| `DeserializeConfig` | `ConfigRepair.Deserialize` (internal, tested) |
| `RepairEnum`, `RepairOptionalEnum`, `RepairDeviceIntegrationJson`, `RepairPluginSettingsDeclarationJson`, `RepairProfilesJson`, `RepairProfileValuesJson`, `RepairCapabilityValueJson`, `RepairPlacement`, `RepairFilterJson`, `Definite` | Deleted; tolerant converter plus `ConfigRepair.RepairUndefined` |
| `Normalize` | `AppConfigRules.Normalize` |
| `NormalizeDeviceIntegration` | `DeviceIntegrationRules.Normalize` (DeviceConfiguration.cs) |
| `NormalizeProfiles`, `NormalizeProfileValues`, `NormalizePowerPreset` | `ProfileRules.Normalize` (Core/Profiles) |
| `NormalizeGameLibrary` | `GameLibraryConfig` rules (enum parts deleted) |
| `NormalizeAnimations`, `NormalizeThemes`, `NormalizeArtwork` | Their section files |
| `NormalizePerformance` | Deleted (walker) |
| `NormalizeGameModeLaunch`, `NormalizeAudioProfile`, `NormalizeAudioEndpoint`, `NormalizeAudioFormat`, `NormalizeLayout`, `NormalizeSteps` | `GameModeLaunchRules` (GameModeLaunchConfiguration.cs) |
| `IsKnownSpatialFormat` | Deleted |
| `NormalizeFilter` | `LibraryFilter` rules (non-enum parts) |
| `NormalizeSplash`, `Blank`, `NormalizePlacement` | `SplashConfigRules` |
| `PreserveCorruptFile` | `StateFile.Quarantine` |
| `PruneCorruptFiles` | Deleted |
| `Mutate` | `ConfigStore.Update` |
| `AcquireLock` | `ConfigStore.Transaction` |
| `CloneJson` | `ConfigJson.Clone` |

Other files:

| Old | New |
| --- | --- |
| GameModeReturnRecovery.cs: `Gate`, `RestoreBestEffort`, `PendingFingerprint`, `ClearRestored`, `RestorePendingAsync`, `RestoreUnderGateAsync`, `AttemptAsync` | DesktopReturnRecovery.cs: instance gate, `RestoreBestEffort`, `Pending`, `ClearIfUnchanged`, `RestoreAsync`, private steps; audio choice -> `GameModeLaunchRecoveryRules.DesktopAudio` |
| DisplayProfiles.cs (all members), `DisplayResolution` | PrimaryDisplayModes.cs, same members. `ReadPrimaryMonitorInstanceId` deleted after W02. `DisplayResolution` stays in that file. |
| EdidModes.cs | Deleted after the W02 equivalence fixture. EdidModesTests fixtures move to WDC DisplayEdidTests. |
| AppConfig.cs `ConfigJsonContext` | ConfigJsonContext.cs. Adds `GameModeLaunchRecovery`, the tolerant enum converter, and a `SchemaVersion` property on AppConfig. |
| ProfileFields.`Copy` | `ConfigJson.Clone` |
| ShellSession.Config.cs `MutateProfilesAsync`, `MutateSimulatedProfilesAsync` | `ConfigProfileStore` adapter (Shell) and `InMemoryProfileStore` |
| Log.`Directory`, `SetMinimumLevel`, `Observe` | `UserDataContext.Root`; inlined into `SetVerbosity`; `TaskFaults.ObserveFaults` |

Public/internal API changes and every consumer that must change:
- `ConfigStore` static -> instance with Read/Update/Transaction. Consumers:
  - Program.cs, App.axaml.cs (composition)
  - ShellSession.cs, .Config, .Modes, .Actions
  - SettingsWindow.axaml.cs, SettingsWindowServices.cs
  - SettingsViewModel.cs, .System, .Save, .Appearance
  - CommonPluginConfiguration.cs, CommonPluginOverlaySource.cs
  - DeviceCoordinator.cs:2069, PluginSettingsCoordinator.cs:156, SteamUiSessionHost.cs:110
  - LibraryTabManager.cs:99/849, LaunchWrapperStore.cs, LibraryTabsView.cs, OverlayController.cs:553/990
  - GameModeReturnRecovery.cs, Installer.cs, DisplayScale.cs:180, LockScreenSettings.cs, UacSettings.cs, OtherManagers.cs, SteamAutostartService.cs, ShellRegistration.cs (static recovery services take the store as a parameter until H01 makes them instances)
  - SplashTheme.cs (NormalizeSplash/CloneJson)
  - Tests: ConfigurationTests, SettingsSaveMergeTests, SettingsViewModelSplashTests, SplashStyleTests, PluginSettingsDeclarationCacheTests, ArtworkConfigTests, GameLibrarySettingsTests, ArtworkTabSettingsTests, AnimationOverridesTests
- `Log.Directory` removed: the 22 sites listed in CONFIG-011.
- `ProfileService.ApplyConfig` -> `ReloadAsync`; new `Close`. `ProfileFanOut.Close`. Consumers: ShellSession.Config.cs:121, ShellSession.Shutdown.cs:94-106, ProfileServiceTests, ProfileFanOutTests, PerformanceBuilders.
- `GameModeReturnRecovery.*` -> instance. Consumers: Program.cs:290, 769; ShellSession.cs:266; ShellSession.Modes.cs:837; ShellSession.Shutdown.cs:846-871.
- `DisplayProfiles.*` -> `PrimaryDisplayModes.*`. Consumers: DisplayResolutionService.cs:34-36, RefreshRatePairingService.cs:45-50, NativeQamResolutionService.cs:34, ShellSession.Performance.cs:76.
- `AudioProfileService` constructor without a native default. Consumers: ShellSession.cs:693, GameModeReturnRecovery/DesktopReturnRecovery, Program restore paths.

## 5. Implementation batches

**CONFIG-B1: Pure config rules and generic repair (~1300 lines)**
- Depends on: nothing.
- Files: ConfigStore.cs (shrinks), new AppConfigRules.cs, ConfigRepair.cs, ConfigJson.cs, ConfigJsonContext.cs; AppConfig.cs; DeviceConfiguration.cs; Profiles/ProfileFields.cs plus a new ProfileRules.cs; GameLibraryConfig.cs; GameModeLaunchConfiguration.cs; LibraryFilter.cs; Themes/Artwork/Animations config files; SplashTheme.cs; SettingsViewModel{,.Appearance,.Save}.cs; tests.
- Steps:
  1. Move `ConfigJsonContext` out and register the tolerant enum converter.
  2. Add `ConfigRepair.RepairUndefined` with explicit templates.
  3. Delete the hand repair list and the `Definite` calls.
  4. Move section normalizers beside their types and return diagnostics.
  5. Nullable enums repair to null (CONFIG-007).
  6. Remove load-time length caps and the spatial allow-list (CONFIG-014/015).
  7. Undefined recovery enum types throw `ConfigCorruptException`, which today's Load still quarantines.
  8. Add `ConfigJson.Clone` and replace `CloneJson` and `ProfileFields.Copy`.
- Tests: one fixture per enum-bearing type with unknown name and unknown number; null member/element fixtures; FilterNode/Splash template fixtures; diagnostics content; instance-preservation (`Assert.Same`) kept.
- Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ConfigRepair|FullyQualifiedName~Splash|FullyQualifiedName~ProfileEdits|FullyQualifiedName~ProfileResolver|FullyQualifiedName~PluginSettingsDeclarationCache"`

**CONFIG-B2a: UserDataContext and instance ConfigStore, semantics unchanged (~1300 lines)**
- Depends on: B1. Setup/launch domain supplies `ForInteractiveUser` later; this batch uses `ForCurrentUser` everywhere.
- Files: new UserDataContext.cs; ConfigStore.cs; Log.cs (`Init(name, root)`, `Directory` removed); all ConfigStore consumers and Log.Directory sites listed in §4; tests.
- Steps:
  1. Program builds the context once (restore-shell builds it before anything else).
  2. Thread the store and context through constructors. Static recovery services get a parameter.
  3. Tests use a temp root and a `Local\WSGM.Tests.Config.<guid>` mutex.
- Tests: rewrite the lock tests on unique mutex names; first temp-root Load/Save round trips.
- Filter: `--filter "FullyQualifiedName~Configuration|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~BootManifest|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~SetupAnswers"`

**CONFIG-B2b: Read outcomes, Update/Transaction, durable writes, StateFile (~1400 lines)**
- Depends on: B2a. H02 session domain: the reloader keeps the last good config and isolates steps; this batch changes only the call at ShellSession.Config.cs:109 to `Read()` and keeps the previous `_config` on non-Loaded results.
- Files: ConfigStore.cs; new StateFile.cs; AtomicFile.cs; BootManifest.cs; BootManifestWriter.cs; ShellSession{,.Config,.Modes}.cs; SettingsViewModel.Save.cs; CommonPluginConfiguration.cs; Installer.cs; OtherManagers.cs; SteamAutostartService.cs; ShellRegistration.cs; Program.cs; ArtworkStateStore.cs; QuickAccessFolds.cs; ImportStateStore.cs; UpdateChecker.cs; tests.
- Steps:
  1. Five-way read with lock-free share-delete reads.
  2. Delete the `ConfigMutex` depth machinery; add `Transaction`.
  3. `Update` with a bool result (CONFIG-001 adapter fix).
  4. Durable saves.
  5. Quarantine once per content; delete prune.
  6. Boot manifest only from Loaded/Absent; recovery readers report Unreadable as failure.
  7. Sidecars adopt `StateFile` and refuse writes after Unreadable.
  8. `BootManifestWriter.WriteSignInDisabled` stops mutating its argument (U04A-LFA-031).
- Tests:
  - absent / corrupt / unreadable (file held with FileShare.None) / unsupported
  - no write after a non-Loaded read
  - quarantine dedupe
  - `Update(false)` writes nothing
  - Transaction save, then boot manifest
  - restore returns failure on Unreadable
  - artwork/folds not overwritten after an unreadable read
- Filter: `--filter "FullyQualifiedName~Configuration|FullyQualifiedName~StateFile|FullyQualifiedName~BootManifest|FullyQualifiedName~ArtworkState|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~ImportState|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~OtherManagers|FullyQualifiedName~SteamAutostart"`

**CONFIG-B3: Schema version and migration (~500 lines)**
- Depends on: B2b. The Steam UI domain owns fold-id migration in quick-access-folds.json.
- Files: AppConfig.cs (`SchemaVersion`); ConfigRepair.cs (`Migrate`); ConfigStore.cs (v0 copy before the first v1 write; UnsupportedSchema); tests with real-shaped 2.0 fixtures.
- Steps: deterministic custom-tab ids from index and name plus a collision ordinal; tab ids no longer minted by the initializer or by Normalize (CONFIG-017).
- Tests: 2.0 fixture round trip preserves every section, sparse profiles, plugin revisions and recovery snapshots; v0 copy created once; a v2 file is never written.
- Filter: `--filter "FullyQualifiedName~Migration|FullyQualifiedName~Configuration"`

**CONFIG-B4: Profiles ownership (~700 lines)**
- Depends on: B2b. H02 wires `Close()` and completion into the B3 shutdown phases and calls `ReloadAsync` from the reloader.
- Files: ProfileService.cs; ProfileFanOut.cs; new Shell/ConfigProfileStore.cs and InMemoryProfileStore.cs; ShellSession.Config.cs; SettingsViewModel.Save.cs (ProfileEdits for the controller target and fan-curve references); ProfileEdits.cs; ProfileResolver.cs (snapshot caching nit); tests.
- Steps: `ReloadAsync` under `_writeGate`; `Close` and `Completion`; learning uses the service's cancellation; cached snapshot JSON; one in-memory store.
- Tests:
  - stale reload loses to a newer mutation (deterministic gates)
  - no-op edit causes no store write
  - after `Close`, no write or queue
  - a hung consumer does not block `Close`
  - fan-out tests use gates instead of `Task.Delay`
- Filter: `--filter "FullyQualifiedName~ProfileService|FullyQualifiedName~ProfileFanOut|FullyQualifiedName~ProfileEdits|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~PerformanceService"`

**CONFIG-B5: Desktop return recovery and audio profiles (~700 lines)**
- Depends on: B2b. H02 owns the call sites in startup and shutdown ordering. W01 later swaps the operations port for the WDC `AudioService`.
- Files: GameModeReturnRecovery.cs -> DesktopReturnRecovery.cs; AudioProfileService.cs; ShellSession{,.Modes,.Shutdown}.cs; Program.cs; GameModeLaunchConfiguration.cs; Core/Themes/ThemeInstaller.cs; tests.
- Steps: instance recovery; restore never clears; source-gen fingerprint; shared desktop-audio rule; explicit audio construction; one capability read per apply; theme journal on source-gen without caps.
- Tests:
  - restore with layout success and audio failure keeps the record
  - clear only when unchanged
  - cancellation stops waiting without a second clear
  - AudioProfileService endpoint wait, unselected-playback refusal, unsupported format
  - theme journal round trip with more than 256 names
- Filter: `--filter "FullyQualifiedName~DesktopReturnRecovery|FullyQualifiedName~AudioProfileService|FullyQualifiedName~ThemeInstaller"`

**CONFIG-B6: Logging (~400 lines)**
- Depends on: B2a. H02 places the verbosity apply in the reloader.
- Files: Log.cs (internal `LogFile` instance, rotation outside the lock, `Observe` and `SetMinimumLevel` removed); OverlayController.cs:418-421; Program.cs; ShellSession.Config.cs; LogLevelTests.cs -> LogFileTests.cs.
- Tests: `Change` suppression and held counts; rotation does not block a concurrent append; verbose flag survives a reload.
- Filter: `--filter "FullyQualifiedName~Log"`

**CONFIG-B7: Primary display modes over WDC (~400 lines)**
- Depends on: W02 (instance `DisplayService`, `DisplayEdid` fixes U01-034/030).
- Files: DisplayProfiles.cs -> PrimaryDisplayModes.cs; EdidModes.cs deleted; DisplayResolutionService.cs; RefreshRatePairingService.cs; NativeQamResolutionService.cs; ShellSession.Performance.cs; tests (EdidModes fixtures become WDC equivalence fixtures).
- Filter: `--filter "FullyQualifiedName~DisplayResolutionService|FullyQualifiedName~RefreshRatePairing"`, plus the WDC DisplayEdid suite.

Order: B1 -> B2a -> B2b -> {B3, B4, B5, B6 in any order} -> B7 after W02.

## 6. Risks and open questions

Risks:
- Lock-free reads rely on share-delete reads. A third-party reader without FILE_SHARE_DELETE (an editor, AV) makes a save fail. That is visible and not retried, the same as today. Mitigation: the strict path reports it.
- An undefined recovery enum, or SchemaVersion > 1 after a downgrade, makes config.json read-only for the 2.1 build. Settings saves and record clears refuse until the file is repaired. This is the safe choice under "never overwrite recovery originals", but it is user-visible through the existing status text.
- The generic repair must reproduce today's deliberate values (FilterNode, Splash, nullable enums). B1 pins each with a fixture before any hand list is deleted.

Open question:
1. Is the read-only outcome for a downgraded or newer-schema config.json acceptable, rather than loading it best-effort and dropping unknown fields on the next save as today? Everything else above follows from the code and the recorded rules.
