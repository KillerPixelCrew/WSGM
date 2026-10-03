# Configuration findings

Scope: the per-user configuration store (`src/WSGM/Core/ConfigStore.cs`, `AppConfig.cs`, `AtomicFile.cs`), the boot manifest projection, the sparse profile store (`Core/Profiles/*`, `Shell/ProfileService.cs`, `Shell/ProfileFanOut.cs`), logging (`Core/Log.cs`), desktop return recovery (`Shell/GameModeReturnRecovery.cs`), audio and display profile helpers (`Shell/AudioProfileService.cs`, `Core/DisplayProfiles.cs`), and the WSGM sidecar state files (artwork, library import, Quick Access folds, theme update journal). Baseline `master` at `1329813f`. Line numbers below are from that baseline and drift; anchor every edit by symbol.

This file merges the config review, its adversarial verification (severity changes, refuted items and the eight `CONFIG-V-*` findings it added), the critic's cross-domain resolutions (conflicts 1, 8, 9, 10, 16, 17, 23, 26, 27) and plan v2, which wins where it changed a recommendation. The maintainer's answers of 2026-10-03 (`DECISIONS.md`) override both; the solutions below follow them.

54 ids exist for this area. 49 are findings below: 2 high, 10 medium, 25 low, 12 nit. 5 are refuted or no-change (end of file); the solution check of 2026-10-03 moved CONFIG-026 and CONFIG-046 there. The decisions moved no finding: CONFIG-004, CONFIG-006 and CONFIG-V-001 now load a newer or unknown-recovery-enum config best effort, CONFIG-038 drops the journal byte bound under D2, and CONFIG-V-004 stays as a functional fix.

Plan v2 batches that implement them, in execution order:

- B013 (settings save merge, carries CONFIG-022)
- B018 (theme journal, CONFIG-038 and CONFIG-V-002)
- B037 `UserDataContext` and an instance `ConfigStore`, behaviour unchanged
- B038 pure config rules, generic enum repair, limit removal
- B039 read outcomes, one writer transaction, durable writes, sidecar rules
- B040 profile service ownership and fan-out admission close
- B041 logging
- B042 desktop return recovery as an instance, audio profile tests
- B068 schema version and the one-time 2.0 to 2.1 migration (a newer schema loads best effort, no `UnsupportedSchema` outcome and no read-only mode)
- B098 display mode services (CONFIG-028, CONFIG-029)
- B116 session config reloader (CONFIG-V-006, and the reloader side of CONFIG-001, CONFIG-002 and CONFIG-030; CONFIG-046 is refuted, so B116 does not track the common-plugin reconcile)
- B122 overlay controller (CONFIG-V-003)
- B124, B133, B137 (the session, Settings and library-tab parts of CONFIG-040)
- B140 shutdown wiring of the admission closes (decided: D1, safety-first ordered steps under one deadline)

Rules that bind every solution here: config.json stays the single serialized authority for preferences and registry recovery snapshots. A strict write never replaces a file it could not read. Fail-open recovery paths (shell unregistration, boot disarm) keep their fallback so a user is never stranded in the WSGM shell. No count or length cap drops content. No new state machine, generation counter or injected port without a concrete defect.

## High

### CONFIG-003: A corrupt or unreadable config.json re-arms Game Mode sign-in through boot.json

- **Severity:** high
- **Where:** `src/WSGM/Shell/ShellSession.cs` (session start calls `BootManifestWriter.WriteCurrent(_config)`, about line 392); `src/WSGM/Program.cs` (`ConfigStore.Load()` at about 102, `DisarmCrashLoop` at about 454-480, restore-shell disarm at about 259-268); `src/WSGM/Core/BootManifestWriter.cs` (`WriteCurrent`, `WriteSignInDisabled`); defaults in `src/WSGM/Core/AppConfig.cs` (`StartAtSignIn = true`, `StartMode = Game`).
- **Problem:** Every session start projects boot.json from `_config`, which came from the lenient `ConfigStore.Load`. When config.json is corrupt, briefly locked by another opener, or unreadable, `Load` returns `new AppConfig()`, so boot.json is rewritten with `GameModeBoot = true`. A user who chose Desktop or no sign-in start, or a crash-loop disarm that just turned sign-in start off, is silently re-armed into Game Mode at the next sign-in. `WriteSignInDisabled` also mutates the caller's config instance (U04A-LFA-031).
- **Best solution:** (B039) Program reads with the new `ConfigStore.Read()` (CONFIG-004) and passes the read status to `ShellSession` through `App.axaml.cs` beside the config (a constructor parameter, the same way `serviceBoot` is passed today). At session start, write boot.json only when the status is Loaded or Absent. Otherwise log one warning ("boot.json left unchanged: config.json is {status}") and skip the write. In `BootManifestWriter`, add one private `Project(AppConfig config, bool startAtSignIn)` that builds and saves the manifest. `WriteCurrent(config)` calls `Project(config, config.StartAtSignIn)`, and `WriteSignInDisabled(config)` calls `Project(config, false)` without touching the argument. The fail-open disarm callers (restore-shell, `DisarmCrashLoop`) keep writing the disarmed manifest from whatever they read, defaults included, because a disarm is always safe. Settings and the Steam settings page write boot.json inside the strict writer transaction (CONFIG-039), so they never project from defaults. Refusing to start the shell on a bad read is the obvious alternative and is wrong: the shell must keep working, and only the projection is unsafe.
- **Tests:** session-start projection with a Corrupt and an Unreadable read leaves a pre-existing boot.json byte-identical; Loaded and Absent write it; `WriteSignInDisabled` leaves the passed instance unchanged; `BootManifestTests` call the production projection instead of re-implementing it (U04A-LFA-008). `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BootManifest|FullyQualifiedName~Configuration"`.
- **Plan v2:** B039.
- **Related:** U05-LFB-002, U04A-LFA-002, U04A-LFA-031, U04A-LFA-008; depends on CONFIG-004.

### CONFIG-004: Lenient load conflates absent, corrupt and transiently unreadable config

- **Severity:** high
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`Load`, `LoadCurrentDocument`, about 122-163). Lenient consumers (verifier-corrected list): `src/WSGM/Shell/ShellSession.Config.cs` (reload, about 109), `src/WSGM/Shell/ShellSession.Modes.cs` (about 605, 753, 858, 874), `src/WSGM/Overlay/LibraryTabsView.cs` (90, 375), `src/WSGM/Settings/SettingsViewModel.cs` (18), `src/WSGM/Settings/SettingsViewModel.System.cs` (187, 219, 228), `src/WSGM/Settings/SettingsWindowServices.cs` (25), `src/WSGM/Program.cs` (102, 262 restore-shell disarm, 596 `ExportSetupAnswers`, 788), `src/WSGM/Core/Installer.cs` (35, 45, 72), `src/WSGM/Core/OtherManagers.cs` (372, 457), `src/WSGM/Core/SteamAutostartService.cs` (92, 146), `src/WSGM/Shell/LaunchWrapperStore.cs` (20), `src/WSGM/Shell/LibraryTabManager.cs` (99, `SyncAllDetailedAsync`), `src/WSGM/Core/ShellRegistration.cs` (115). `SettingsWindow.axaml.cs:138` and `SettingsViewModel.cs:273` are only comments. Re-run `rg -n "ConfigStore\.Load\(|ConfigStore\.Load\b" src` before editing.
- **Problem:** `Load` catches every exception, preserves a copy and returns defaults. A parse failure, a sharing violation from a non-WSGM opener (antivirus, an editor) and an access failure all become live defaults. WSGM's own writers cannot cause the sharing violation, because `Load` waits on the same mutex as every writer and only degrades after the 2 s timeout (verifier correction). Consumers then act on defaults: the session reload applies them (SESSION-017), boot.json is re-armed (CONFIG-003), restore paths report success (CONFIG-005), and an Unreadable file is quarantined as if it were corrupt.
- **Best solution:** (B039) Replace `Load` with `ConfigRead Read()`, where `ConfigRead(ConfigReadStatus Status, AppConfig? Config, string? Detail)` and the status is `Loaded`, `Absent`, `Corrupt` or `Unreadable`. There is no `UnsupportedSchema` status and no read-only mode (DECISIONS.md): a file written by a newer WSGM, whatever its `SchemaVersion` (B068), loads best effort as `Loaded`, as today, and members this build does not know are dropped on the next save; an unknown recovery enum loads too (CONFIG-006 step 3). Mapping: file missing (`File.Exists` false, or `FileNotFoundException`/`DirectoryNotFoundException` on open) is Absent with `new AppConfig()`; `JsonException` or `InvalidOperationException` from deserialize or repair (root not an object) is Corrupt, which quarantines once (CONFIG-008) and carries no config; `IOException` or `UnauthorizedAccessException` is Unreadable, with no quarantine and no config. Only Loaded and Absent carry a config. Give `ConfigRead` two helpers: `bool IsUsable` (Loaded or Absent) and `AppConfig ConfigOrDefaults` (the config, or `new AppConfig()`), so pure display readers keep today's behaviour in one expression. Writers never use `ConfigOrDefaults`: they go through `Update`/`Transaction` (CONFIG-012), which throw `ConfigUnavailableException` (CONFIG-V-001). Read keeps the reader-side mutex with today's 2 s degrade (verifier batch problem 4). Per consumer: the shell reload keeps its previous `_config` on a non-usable read and logs once (B039 changes only that call; B116 owns the reloader); recovery readers follow CONFIG-005; boot projection follows CONFIG-003; `ExportSetupAnswers` stays fail-open: it exports `ConfigOrDefaults` as today and logs the read status, because `SetupEngine.PrepareAnswers` turns any non-zero exit into "WSGM could not export its settings" and stops, so a fail-closed export would block every repair and upgrade of a user whose config.json is corrupt, and `--setup` already refuses to apply answers onto a config it cannot read strictly (`LoadForMutation`, later `Transaction`); `LibraryTabManager.SyncAllDetailedAsync` pushes config state into Steam, so on a non-usable read it throws `ConfigUnavailableException` into its existing catch ("Could not sync library tabs — see the log.") instead of retracting or replacing the user's tabs from defaults; `ShellRegistration` stays fail-open; the rest are display readers and use `ConfigOrDefaults`. No `.bak` copy and no repair UI (plan v2 dropped both).
- **Tests:** state fixtures on a temp root: no file (Absent), valid file (Loaded), garbage (Corrupt, one quarantine copy), JSON array root (Corrupt), file held open by the test with `FileShare.None` (Unreadable, no copy); `ExportSetupAnswers` over a Corrupt file still exits 0 and writes answers. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~SetupAnswers"`.
- **Plan v2:** B039 (it replaces every `Load` call, so `src/WSGM/Shell/LibraryTabManager.cs`, `src/WSGM/Shell/LaunchWrapperStore.cs`, `src/WSGM/Overlay/LibraryTabsView.cs` and the Settings readers are in scope even though its file list omits them).
- **Related:** U04A-LFA-002, U05-LFB-002, PV10-002, PV11-002, SESSION-017; prerequisite for CONFIG-003, CONFIG-005, CONFIG-008, CONFIG-V-001.

## Medium

### CONFIG-001: Every profile edit rewrites config.json and the shell reloads itself

- **Severity:** medium
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs` (`MutateProfilesAsync`, about 274-287; the reload callback, about 104-157); `src/WSGM/Core/ConfigStore.cs` (`Mutate`, about 1194-1201); `src/WSGM/Shell/ProfileService.cs` (constructor contract, about 50-54).
- **Problem:** The production profile-store adapter calls `edit(config.Profiles)`, discards the returned bool and always saves through `ConfigStore.Mutate`. `ProfileService` documents that the store "saves it when the edit reports a change". No-op edits, `LearnExecutable` returning false and resets with nothing to reset all rewrite config.json. Every write fires the shell's FileSystemWatcher, which runs the full reload: `ApplyDeviceConfig` (coordinator `ApplyConfigAsync`), a second RTSS `ApplyProfilesAsync` and about 25 other steps. Plugin declaration cache writes (CONFIG-044) and widget-pin edits echo the same way.
- **Best solution:** Two halves, each in exactly one batch (verifier batch problem 9). Store half (B039): replace `Mutate(Action<AppConfig>)` with `AppConfig Update(Func<AppConfig, bool> change)`, which loads strictly under the writer lock and saves only when `change` returns true. Callers that always change return true; callers with a natural no-op return a real bool (Quick Access pins, the plugin declaration cache, widget pins, recovery clears). Adapter half (B040): the adapter becomes `ConfigProfileStore` (Shell), whose `MutateAsync` runs `store.Update(c => { var changed = edit(c.Profiles); stored = ConfigJson.Clone(c.Profiles, ConfigJsonContext.Default.ProfileConfig); return changed; })` on a worker and returns `stored`. Do not add a whole-document equality skip to the reloader: once no-op edits stop writing, every watcher event follows a real change, the profile section already compares in `ProfileService.ReloadAsync` (CONFIG-002), and the other steps receive the same input again. While building B116, confirm that `DeviceCoordinator.ApplyConfigAsync` and `PerformanceService.ApplyProfilesAsync` are no-ops for unchanged input; if one is not, that step compares its own input.
- **Tests:** `Update(_ => false)` leaves the file bytes and timestamp unchanged and creates no temp file; through `ConfigProfileStore` on a temp root, a no-op `ProfileService` edit (an already known executable, a reset with nothing to reset) performs no write. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ProfileService"`.
- **Plan v2:** B039 (store), B040 (adapter).
- **Related:** CONFIG-044 (same fix), CONFIG-002, CONFIG-030 (a reload is what resets `--verbose`), CONFIG-036 (adapter untested).

### CONFIG-005: Restore paths read lenient defaults and report success

- **Severity:** medium
- **Where:** `src/WSGM/Core/Installer.cs` (`RestoreMachineSettings`, three separate `ConfigStore.Load` calls at about 35, 45, 72); `src/WSGM/Core/SteamAutostartService.cs` (about 146-171); `src/WSGM/Core/OtherManagers.cs` (about 456-475); `src/WSGM/Program.cs` (`RestoreDisplayScalesBestEffort`, about 788); `src/WSGM/Shell/ShellSession.Modes.cs` (`ApplyReturnLayoutAsync`, `ApplyReturnAudioAsync`, about 858 and 874); `src/WSGM/Core/ShellRegistration.cs` (`Uninstall`, about 104-131).
- **Problem:** Uninstall restore, other-manager restore and Steam autostart restore read the registry snapshots through `Load`. On a corrupt or unreadable file they see `PreviousUacSnapshotCaptured = false` and friends, do nothing and report success, so the user's UAC level, lock-on-wake, display scaling and startup entries are never restored, and nothing says so. The Game Mode return path silently falls back to "no recorded layout".
- **Best solution:** (B039) Each restore reads once with `Read()` and classifies itself (verifier batch problem 5). Fail-closed callers write nothing on a non-usable read and say so in wsgm.log: `Installer.RestoreMachineSettings` reads once at the top instead of three times; on a non-usable read it logs one error line naming the status and that the preserved `config.bad.*.json` copy holds the snapshots (Corrupt only), skips display scaling, UAC and lock-on-wake, and still runs `SteamAutostartService.RestoreAll` and `OtherManagers.RestoreAll` (each does its own strict read). Its exit code does not change: `--uninstall-restore` keeps returning 0 or `UninstallHidHideUnverifiedExitCode`, because `SetupEngine.RestoreController` reads every non-zero code as "HidHide did not confirm the change" and keeps the HidHide ledger, so a config failure there would misreport the uninstall step (a UI and workflow change). Program's `RestoreDisplayScalesBestEffort` also logs and skips. `UacSettings.ApplyDirect` and `LockScreenSettings.ApplyDirect` already load strictly (`Mutate`/`LoadForMutation`) and need only the new exception type. Fail-open callers keep today's fallback: `ShellRegistration.Uninstall` (deletes WSGM's Shell value when nothing was captured, so the user gets Explorer back), the restore-shell boot disarm and `DisarmCrashLoop`. The Game Mode return path keeps its in-memory `_pendingReturnLayout` and the launch configuration as fallbacks and logs the read status instead of silently treating the record as absent. Steam autostart and other-manager restores belong to WINSVC (critic conflict 27): B039 supplies only the strict read and the split; B016 (CRIT-001) and B095 (`RestoreAll` returns 1 on unreadable config without writing) own the rest of those two files.
- **Tests:** `RestoreMachineSettings` with a Corrupt config calls no snapshot restore primitive and logs the status line (fake the primitives through the existing seams, or assert the log line if no seam exists), and `--uninstall-restore`'s exit code still depends only on HidHide; `ShellRegistration.Uninstall` with an Unreadable config still deletes the Shell value. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ShellRegistration|FullyQualifiedName~OtherManagers|FullyQualifiedName~SteamAutostart"`.
- **Plan v2:** B039; B095 and B016 for the WINSVC-owned files.
- **Related:** U04B-LFA-004, U04A-LFA-002, U04B-LFA-009, CRIT-001; depends on CONFIG-004.

### CONFIG-006: The enum-name repair list is incomplete, so one unknown name quarantines the whole file

- **Severity:** medium
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`DeserializeConfig` and the `Repair*` helpers, about 172-420). Missing enums: `AppConfig.LogVerbosity`, `DevicePowerCustomValues.WindowsMode` (`src/WSGM/Core/DevicePowerPresetReference.cs:28`, inside `Profiles.Global/Games[].Values.AcPowerPreset/BatteryPowerPreset.CustomValues`), `GameLibraryConfig.DefaultMode` and `ArtworkPreference` (`src/WSGM/Core/Library/GameLibraryConfig.cs:51, 60`), plus the recovery enums `PreviousShellValueKind`, `PreviousStartupToGamingHomeValueKind` and `SteamAutostartRecord.Kind/Scope`.
- **Problem:** Every enum is written by name. An unknown name (hand edit, or a file written by a newer build) in any member the hand list does not cover makes the retry throw, and the whole otherwise valid file is quarantined and replaced by defaults, taking every setting with it. Each new enum must be remembered in the list, which already failed several times.
- **Best solution:** (B038) Replace the hand list with one generic mechanism in a new `src/WSGM/Core/ConfigRepair.cs`:
  1. A tolerant enum converter factory registered on `ConfigJsonContext` (via `JsonSourceGenerationOptions.Converters`, ordered so it is consulted before the string enum converter). It reads exactly what today's converter accepts (declared names, comma-separated names for `[Flags]` enums, integers) and writes the same text the built-in string converter writes. An unknown name or number becomes an undefined sentinel value of that enum instead of an exception.
  2. A post-deserialize walker, `ConfigRepair.RepairUndefined(AppConfig)`, that visits the config object graph and lists. For each enum member that is undefined (for a `[Flags]` enum: any bit outside the defined mask, so `LaunchWrapperMode` combinations such as 6 stay valid, verifier batch problem 1), it sets the template value: the property's initializer value from a fresh instance of the declaring type, except for explicit templates. `FilterNode` uses `FilterDefaults` verbatim, `SplashConfig` and `SplashElementPlacement` fields use `new SplashConfig()` field defaults (not the 2.0 preset), and nullable enums become null (CONFIG-007). Null non-nullable reference members get the template value and null list elements are removed.
  3. Recovery enums load what is understood (DECISIONS.md: best effort as today, no read-only mode). The factory converts `RegistryValueKind`, `SteamAutostartKind` and `SteamAutostartScope` like every other enum, so an unknown name becomes the undefined value exactly as an unknown number already does today, and the file stays Loaded instead of becoming Corrupt. The walker skips these three types and leaves the undefined value in place rather than guessing a template, because the point of use already bounds it: `ShellRegistration`'s snapshots map any kind other than `ExpandString`/`QWord` to `String`/`DWord` through `normalizeKind` (`RegistryValueSnapshot`), and an undefined `SteamAutostartRecord.Kind` or `Scope` matches no branch in `SteamAutostart`/`SteamAutostartTakeover`, the same as an unknown number today. No per-record raw subtree, no new outcome.
  4. SDK enums with a type-level `[JsonConverter(JsonStringEnumConverter<T>)]` (for example `CapabilityValueKind`) go through the factory too, because options-level converters win over type attributes; the written text must stay byte-identical (verifier batch problem 3).
  Then delete `RepairEnum`, `RepairOptionalEnum`, `RepairDeviceIntegrationJson`, `RepairPluginSettingsDeclarationJson`, `RepairProfilesJson`, `RepairProfileValuesJson`, `RepairCapabilityValueJson`, `RepairPlacement`, `RepairFilterJson` and every `Definite` call (CONFIG-016). The obvious alternative, adding the missing names to the hand list, leaves the same defect waiting for the next enum.
- **Tests:** one fixture per enum-bearing type with an unknown name and an unknown number (including `DevicePowerCustomValues.WindowsMode`, `GameLibraryConfig.DefaultMode`, `ArtworkPreference`, `LogVerbosity`); a `LaunchWrapperMode` value of 6 survives; `FilterNode` and Splash template fixtures; a recovery enum with an unknown name and one with an unknown number both load as Loaded with the undefined value kept, and the shell snapshot restore writes the normalized registry kind; byte-identical round trips of a cached `PluginSettingsManifest` and a profile `CapabilityValue`; instance preservation (`Assert.Same`) kept. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ConfigRepair|FullyQualifiedName~Splash|FullyQualifiedName~PluginSettingsDeclarationCache|FullyQualifiedName~DevicePowerAssignments"`.
- **Plan v2:** B038.
- **Related:** U04A-LFA-001, PV10-001; CONFIG-007, CONFIG-016 (fall out of the same change).

### CONFIG-009: config.json and boot.json are replaced without a durable flush

- **Severity:** medium
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`Save`, `AtomicFile.WriteText(ConfigPath, json, false, ...)` at about 1165); `src/WSGM/Core/BootManifest.cs` (`BootManifestStore.Save`, about 121); `src/WSGM/Core/AtomicFile.cs` (the `durable` parameter, about 64-70).
- **Problem:** Both files hold state that must survive a power loss (registry recovery snapshots; the SYSTEM service's sign-in decision). They are written to a temp file and renamed without flushing the temp file to disk first, so a crash or battery death right after the rename can leave a zero-length or torn file under the final name.
- **Best solution:** (B039) Pass `durable: true` at both call sites. `AtomicFile` already implements the flush before the rename. Nothing else changes.
- **Tests:** an `AtomicFile` test that writes with `durable: true` over an existing file and reads the new content back (also closes the AtomicFile part of CONFIG-036). `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AtomicFile|FullyQualifiedName~BootManifest"`.
- **Plan v2:** B039.
- **Related:** U04A-LFA-009, PV10-007.

### CONFIG-010: ConfigStore is a static god class and AppConfig a flat aggregate

- **Severity:** medium
- **Where:** `src/WSGM/Core/ConfigStore.cs` (1,409 lines: IO, locking, JSON repair and 12 feature normalizers); `src/WSGM/Core/AppConfig.cs` (15 types plus `ConfigJsonContext`).
- **Problem:** Persistence, lock policy, enum repair and every feature's validation rules sit in one static class. Tests cannot run it on a temp root (they contend on the user's real mutex and file), a feature's rules cannot be found beside the feature, and every change to one section touches the shared file.
- **Best solution:** Two steps. B037: `ConfigStore` becomes an instance over `UserDataContext` with today's semantics (CONFIG-011). B038: dissolve the rest by symbol. `ConfigJsonContext` moves to `Core/ConfigJsonContext.cs`; `ConfigJson.Clone<T>` lives in `Core/ConfigJson.cs` (CONFIG-021); `ConfigRepair` holds the tolerant converter and the walker (CONFIG-006); `AppConfigRules.Normalize(AppConfig, ICollection<string> diagnostics)` calls static per-section rules co-located with their types: `DeviceIntegrationRules` in `DeviceConfiguration.cs`, `ProfileRules` in `Core/Profiles`, the game library rules in `GameLibraryConfig.cs`, `GameModeLaunchRules` (launch, audio profile, endpoint, format, layout, steps) in `GameModeLaunchConfiguration.cs`, the filter rules in `LibraryFilter.cs`, `SplashConfigRules` beside `SplashConfig` (it takes the splash bound constants; `AppearancePage.axaml` literals must still match them), and the animation, theme and artwork rules in their section files. `NormalizePerformance` is deleted (the walker covers it). The store keeps only `Read`, `Update`, `Transaction`, quarantine and the mutex. Not adopted, as over-engineering: an injected file backend, a normalizer list or diagnostics sink, a store worker thread, and extension data on every class.
- **Tests:** existing configuration tests move with their rules; diagnostics content asserted for one rule per section. Filter as CONFIG-006.
- **Plan v2:** B037 (instance), B038 (split).
- **Related:** U04A-LFA-007, PV10-006; CONFIG-011, CONFIG-013, CONFIG-016, CONFIG-021.

### CONFIG-011: The data root is the static Log.Directory

- **Severity:** medium
- **Where:** `src/WSGM/Core/Log.cs` (`Directory`, about 80-81). 27 code sites in 19 files outside Log.cs (verifier-corrected count) plus a doc reference at `src/WSGM/Core/SteamInputShim.cs:91`. Sites: `AnimationLibrary.cs:42`, `BootManifestWriter.cs:15`, `ConfigStore.cs` (105, 1106, 1135, 1162), `Core/Library/ImportStateStore.cs:90`, `Core/QuickAccessFolds.cs:28`, `SoundPackLibrary.cs:30`, `SplashAssets.cs:46`, `SteamInputShim.cs:169`, `ThemePaths.cs:33`, `UnelevatedLauncher.cs` (47, 57), `UpdateChecker.cs:68`, `Program.cs` (638, 819), `SettingsViewModel.System.cs` (271, 279, 280, 292), `Shell/ArtworkStateStore.cs:14`, `AutoTdpTraceRecorder.cs:71`, `DeviceCoordinator.cs:150`, `SdFormatManager.cs:1102`, `ShellSession.Config.cs:94`, `ShellSession.cs:302`. Verify the list with a grep before editing.
- **Problem:** Every per-user file path derives from the logger's static directory, so nothing can run against a temp root, tests touch the real `%LOCALAPPDATA%\WSGM` and the real `Local\WSGM.Config` mutex (U04A-LFA-022, BUILD-001), and the early restore-shell path cannot know the root without initializing logging.
- **Best solution:** (B037) Add `sealed record UserDataContext(string Root, string ConfigMutexName)` in `Core/UserDataContext.cs` with one factory, `ForCurrentUser()` (`%LOCALAPPDATA%\WSGM`, `Local\WSGM.Config`), and path helpers `ConfigPath` and `File(string name)`. It is pure, so restore-shell builds it before Log and config (root CLAUDE.md safety rule). No `ForInteractiveUser` and no user identity (critic conflict 17). Program builds it once and passes it to `Log.Init(name, root)`, the `ConfigStore` instance and every store that needs a root (`ImportStateStore` and `ArtworkStateStore` take the root here: LIBRARY-B7 folds in, file names unchanged). Delete `Log.Directory`. Static recovery services take the store or context as a parameter until their own batches make them instances. Tests use a temp root and `Local\WSGM.Tests.Config.<guid>`. Do not change mutex ACLs here (CONFIG-V-004).
- **Tests:** rewrite the lock tests on unique mutex names; first temp-root round trips. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~BootManifest|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~SetupAnswers|FullyQualifiedName~ImportState|FullyQualifiedName~ArtworkState"`.
- **Plan v2:** B037, run first in the config group, before B038 (verifier batch problem 10, critic conflict 26).
- **Related:** U04A-LFA-004, U04A-LFA-005, PV10-004, U04A-LFA-022, LIBRARY-014, BUILD-001.

### CONFIG-018: Profile fan-out disposal can block shutdown indefinitely

- **Severity:** medium
- **Where:** `src/WSGM/Shell/ProfileFanOut.cs` (`DisposeAsync`, about 58-87); `src/WSGM/Shell/ShellSession.Shutdown.cs` (about 89-106, awaited before device cleanup).
- **Problem:** `DisposeAsync` cancels the active pass and then awaits the worker without a bound. Shutdown awaits that before the safety-critical device cleanup (AutoTDP restore, controller release, HidHide cloak-off). A consumer that ignores cancellation, for example a plugin call stuck in `ApplyProfilesAsync`, blocks every later shutdown step, including the never-strand ones.
- **Best solution:** (B040) Replace `DisposeAsync` with a synchronous `CloseAdmission()` and a `Completion` task (plan v2's architecture names the method `CloseAdmission`; B040's "Close()" is the same thing). `CloseAdmission` sets the closed flag under `_gate`, clears `_pending`, unsubscribes from `ProfileService.Changed`, and cancels `_shutdown` and the active pass. `Completion` returns the current worker task (it replaces `Idle`). `_shutdown` is never disposed: a `CancellationTokenSource` without a timer holds nothing that needs disposal, and disposing it underneath a hung pass is exactly what must not happen, so no disposal hand-off to the worker is needed (a hung consumer is retained, not freed). Shutdown (B140, step 0) calls `CloseAdmission()` and does not await `Completion` before device safety: the coordinator's own admission close (B083) refuses anything a late consumer sends. B140 may observe `Completion` later within the remaining deadline. The Codex B3 percentage cutoffs and preliminary drain are dropped (decided: D1).
- **Tests:** a consumer that never completes and ignores its token does not delay `CloseAdmission`; after `CloseAdmission`, `Queue` is a no-op; `Completion` completes once a cooperative consumer observes cancellation. Gate-controlled consumers, no `Task.Delay` (CONFIG-045). `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ProfileFanOut"`.
- **Plan v2:** B040; shutdown wiring in B140 (decided: D1, safety-first ordered steps under one deadline, no percentage cutoffs or preliminary drain).
- **Related:** U05-LFB-001 family, critic conflict 1; CONFIG-019 (same pattern on `ProfileService`).

### CONFIG-024: GameModeReturnRecovery mixes restoring with clearing

- **Severity:** medium
- **Where:** `src/WSGM/Shell/GameModeReturnRecovery.cs` (static `Gate` at 13; clear duplicated at 28-42 and 94-100; reflection `JsonSerializer.Serialize` at 30, 37, 65, 96; `RestorePendingAsync` with `WaitAsync` at 44-50); call sites `src/WSGM/Program.cs` (290 restore-shell, 769 panic `RestoreBestEffort`), `src/WSGM/Shell/ShellSession.cs` (266, session start), `src/WSGM/Shell/ShellSession.Modes.cs` (837 `RestorePendingReturnAsync`; 874-875 duplicate audio rule), `src/WSGM/Shell/ShellSession.Shutdown.cs` (846-871), `src/WSGM/Shell/GameModeEntryTransaction.cs` (143).
- **Problem:** Restore clears the record only when Explorer is running but returns "complete" either way, so callers cannot tell a restored-and-cleared record from a restored-but-kept one. The clear logic exists twice. The fingerprint uses the reflection serializer while every other config path uses the source-generated context. The gate is static. `WaitAsync` lets a caller move on while the abandoned work still holds the gate and can still run the uncancellable `DisplayLayouts.Apply` (the abandoned work can mutate config only if the token fires after its `ThrowIfCancellationRequested`, verifier correction). The "desktop audio overrides captured audio" rule is duplicated in `ShellSession.Modes`.
- **Best solution:** (B042) Replace the static class with an instance `DesktopReturnRecovery(ConfigStore store, Func<DisplayLayout, CancellationToken, Task<bool>> applyLayout, AudioProfileService audio)` in `Shell/DesktopReturnRecovery.cs`, with an instance gate. Members: `RestoreAsync(CancellationToken)` restores layout and audio and never clears; `Pending()` returns the record and its fingerprint, serialized through `ConfigJsonContext.Default.GameModeLaunchRecovery` (add that type to the context); `ClearIfUnchanged(string fingerprint)` does `store.Update(c => { if fingerprint differs return false; reset; return true; })`; `RestoreBestEffort()` stays for panic. Keep the `WaitAsync` so a budget still stops waiting: since restore no longer clears, abandoned work cannot touch config. Add `GameModeLaunchRecoveryRules.DesktopAudio(launch, pending) => launch.DesktopAudio ?? pending.PendingReturnAudio` in `Core/GameModeLaunchConfiguration.cs` and use it in recovery and `ApplyReturnAudioAsync`. Because restore stops clearing, every site that relied on the implicit clear must capture `Pending()` before restoring and call `ClearIfUnchanged` itself (verifier batch problem 6), under exactly today's implicit rule, so behaviour does not change: clear only when the restore was complete and `ExplorerControl.IsDesktopShellRunning()` is true. Put that rule in one place: the constructor also takes `Func<bool> desktopShellRunning` (production passes `ExplorerControl.IsDesktopShellRunning`, tests a fake), and a member `ClearAfterRestore(string fingerprint, bool complete)` calls `ClearIfUnchanged` only when both hold. That applies at the Game Mode entry path (`RestorePendingReturnAsync`; this covers a non-Custom launch without `GameAudio`, where `PersistPendingReturnAsync` never runs), session start, restore-shell (after `StartExplorerAndVerify`) and panic. The desktop-shell condition matters: a service boot straight into Game Mode has no Explorer, and today the kept record is what `ApplyReturnLayoutAsync`/`ApplyReturnAudioAsync` fall back to on the later desktop return, so an unconditional clear at entry would lose it. Shutdown keeps its own rule (clear after `RestoreDesktopAsync` reports Normal or Degraded). The composition root builds the instance; restore-shell and panic build one with their own explicitly constructed `AudioProfileService` and dispose it.
- **Tests:** layout success plus audio failure keeps the record; clear only when the fingerprint is unchanged; cancellation stops waiting and performs no second clear; the entry path clears exactly once with a desktop shell running, including a non-Custom launch without `GameAudio`, and keeps the record without one; session start without a desktop shell keeps the record. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DesktopReturnRecovery|FullyQualifiedName~GameModeEntryTransaction"`.
- **Plan v2:** B042.
- **Related:** U05-LFB-017, PV11-005, PV11-016, SESSION-027; CONFIG-025, CONFIG-036.

### CONFIG-038: The theme update journal enforces caps its writer does not, on the reflection serializer

- **Severity:** medium (verifier raised from low)
- **Where:** `src/WSGM/Core/Themes/ThemeInstaller.cs` (journal write in `Unpack`, about 136-158; `Recover`, about 180-201; `ThemeUpdateJournal`).
- **Problem:** `Recover` rejects a journal over 128 KiB or with more than 256 names, but `Unpack` writes the journal without either bound, so an archive with more than 256 top-level entries leaves a marker that `Recover` throws on at every later theme load. Together with CONFIG-V-002 that stops the WSGM shell from starting. The journal uses the reflection serializer.
- **Best solution:** (B018) Add a source-generated `ThemeJournalJsonContext` for `ThemeUpdateJournal` and use it for both write and read. `ThemeUpdateJournal` is a `private sealed class` nested in `ThemeInstaller` today; the source generator cannot reach a private type, so make it `internal` (nested or top-level in the same file). Remove the `Names.Count > 256` check and the 128 KiB length check. Keep the GUID id, `SafeName` and `Existing ⊆ Names` checks, which are shape and containment checks. B018's text keeps the 128 KiB bound, but D2 is decided (accept exactly the plan v2 list, remove every other cap) and that list does not include the journal, which is WSGM's own file, not untrusted input, so the bound goes and B018's step is corrected accordingly.
- **Tests:** a journal with more than 256 names round-trips through `Unpack` and `Recover`. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Themes"`.
- **Plan v2:** B018.
- **Related:** STEAMHOST-031 (journal part), CONFIG-V-002 (same defect, exception side).

### CONFIG-V-002: An invalid theme update journal makes every shell session start fail

- **Severity:** medium
- **Where:** `src/WSGM/Core/Themes/ThemeInstaller.cs` (`Recover` throws `InvalidDataException`, about 189-201; `Unpack` post-commit `Recover` at about 159 and the catch at about 163-167 that calls `Recover` again); `src/WSGM/Core/Themes/ThemeLoader.cs` (`Load` catch, about 66-73); `src/WSGM/Shell/ThemeService.cs` (`Start`, about 778); `src/WSGM/Shell/ShellSession.cs` (`StartSessionServices`, about 736); `src/WSGM/App.axaml.cs` (`ObserveSessionStartupAsync`, about 91-95).
- **Problem:** `ThemeLoader.Load` catches only `IOException`, `UnauthorizedAccessException` and `JsonException`. `InvalidDataException` is a `SystemException`, not an `IOException`, so an invalid journal escapes `ThemeService.Start`, faults session startup and reaches `desktop.Shutdown(1)`. The marker survives, so every later start fails the same way. The install itself can leave such a marker, and in `Unpack` the second `Recover` in the catch throws again and replaces the `ThemeStoreException` the user should have seen.
- **Best solution:** (B018) In `ThemeLoader.Load`, add `InvalidDataException` to the existing catch so a bad journal becomes the existing "Theme update recovery remains pending" load error and the loader stays usable (no catch-all). In `Unpack`, wrap the `Recover(root)` inside the catch in its own try/catch that logs the recover failure, so the original failure is still thrown as `ThemeStoreException`. Together with CONFIG-038's cap removal, a normal large theme no longer produces an invalid journal at all.
- **Tests:** a garbage journal and an invalid-id journal leave `ThemeLoader.Load` usable with exactly one load error; `ThemeService.Start` does not throw with such a journal present (session start survives); an `Unpack` whose recover also fails still throws `ThemeStoreException` with the original message. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Themes|FullyQualifiedName~ThemeService"`.
- **Plan v2:** B018.
- **Related:** STEAMHOST-V-001 (same defect), CONFIG-038.

## Low

### CONFIG-002: A reload can publish an older profile store after a newer committed edit

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Shell/ProfileService.cs` (`ApplyConfig`, about 110-130, does not take `_writeGate`; `MutateAsync`, about 363-405); `src/WSGM/Shell/ShellSession.Config.cs` (reload loads at T1 on a worker and applies on the UI thread later, about 106-121).
- **Problem:** If the reload loaded the file at T1 and a profile edit committed and published at T2, the posted T1 apply can still run and replace the newer store, and the fan-out pushes the old values to RTSS, the device and GPU. The window is narrow (the T2 watcher event usually bumps the reload generation first) and heals after the next 500 ms debounce, but it exists.
- **Best solution:** (B040) Replace `ApplyConfig(ProfileConfig)` with `Task ReloadAsync(CancellationToken)`. It awaits `_writeGate`, re-reads the stored profiles through the existing port by calling `_mutate(_ => false, ct)` (after CONFIG-001 that is a strict read under the writer lock that never writes, so no second port is needed), and then runs today's `ApplyConfig` body (compare, activate, publish, learn). Serializing on the write gate guarantees the published store is at least as new as every committed edit, without a generation counter. The reloader (B116) calls `ReloadAsync` instead of passing `config.Profiles`, through `Log.Observe`; a `ConfigUnavailableException` keeps the current snapshot. Overlay-test's `InMemoryProfileStore` (CONFIG-023) answers the same call with its copy.
- **Tests:** with deterministic gates, start a reload while the file holds T1, commit a mutation T2 while the reload waits on the write gate, release: the published store equals T2. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ProfileService"`.
- **Plan v2:** B040 (service), B116 (reloader call).
- **Related:** CONFIG-001 (makes the read port write-free), CONFIG-020.

### CONFIG-007: An unknown optional controller target becomes an override the user never chose

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs` (name repair to `SteamDeckComposite` at about 317; number repair to null at about 819-822; `RepairOptionalEnum` rationale at about 387-401); `tests/WSGM.Tests/Core/ConfigurationTests.cs` (about 121-125 locks in the name behaviour).
- **Problem:** An unknown name in the nullable `ControllerTarget` is repaired to `SteamDeckComposite`, which invents a per-game override, while an unknown number becomes null. The same input class gets two answers, and one of them changes which virtual controller a game gets.
- **Best solution:** (B038) The generic walker repairs every undefined nullable enum to null (CONFIG-006 step 2). Update the test to expect null for an unknown name.
- **Tests:** unknown name and unknown number in `Profiles.Global.ControllerTarget` and a game's `ControllerTarget` both load as null. Filter as CONFIG-006.
- **Plan v2:** B038.
- **Related:** CONFIG-006.

### CONFIG-008: Preserved corrupt copies multiply and are then pruned

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`PreserveCorruptFile`, `PruneCorruptFiles`, about 1096-1155).
- **Problem:** Every lenient load of a broken file (shell reload, Settings, elevated one-shots) writes another `config.bad.<guid>.json`, and the prune keeps the newest five by timestamp. Because strict writes refuse to overwrite a corrupt config.json, the original stays in place and the copies are identical until the user hand-edits the file; but a user who edits a broken file repeatedly loses earlier preserved versions. A keep-five prune is an arbitrary count cap. Unreadable files are also "quarantined" today, which copies nothing useful.
- **Best solution:** (B039) Quarantine only on a Corrupt read (CONFIG-004), never on Unreadable. `Read` already holds the bytes that failed to parse (read them with `File.ReadAllBytes` and decode, instead of `ReadAllText`), so quarantine writes exactly those bytes: name the copy by content, `config.bad.<SHA-256 hex of the bytes>.json`, and write it with `FileMode.CreateNew`. No second open of config.json, so the copy cannot differ from what was judged corrupt. An existing file at that name means this content is already preserved, so nothing is written or logged again. Delete `PruneCorruptFiles`. Keep quarantine private to `ConfigStore`; no shared `StateFile` type (critic conflict 16).
- **Tests:** two Corrupt reads of the same bytes produce one copy; an edit to different broken bytes adds a second copy and keeps the first; an Unreadable read produces none. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration"`.
- **Plan v2:** B039.
- **Related:** PV10-002; CONFIG-004.

### CONFIG-012: The lock machinery exists only to support nesting

- **Severity:** low (recommendation corrected by the verifier)
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`LockDepth`, `HasExclusiveLock`, `AcquireLock`, the `ConfigMutex` class with its depth counter, out-of-order dispose rules and the degraded-nesting exception, about 107-115 and 1203-1408); the three depth tests in `tests/WSGM.Tests/Core/ConfigurationTests.cs`.
- **Problem:** The thread-static depth counter, `HasExclusiveOwnership`, the out-of-order dispose rules and the "exclusive inside degraded read" exception exist only because callers nest `AcquireLock` + `LoadForMutation` + `Save` + `Mutate` (CONFIG-039). The review proposed lock-free share-delete reads; the verifier showed that is unsafe: the Settings transaction writes config.json, promotes splash images, may write config.json a second time and writes boot.json under the lock, and the shell's reload must not see the intermediate document (verifier batch problem 4).
- **Best solution:** (B039) Keep the reader-side mutex and its 2 s degrade for reads. Remove only the nesting. One writer scope, `T Transaction<T>(Func<ConfigTransaction, T> work)`, acquires the mutex once (a timeout or open failure throws `ConfigUnavailableException`), loads strictly once, and exposes `tx.Config` and `tx.Save(AppConfig)`; `Update` is `Transaction` plus a conditional save. The private mutex helper becomes a plain acquire/release scope. Delete `LockDepth`, `HasExclusiveLock`, `HasExclusiveOwnership`, `CurrentDepth`, the out-of-order rules, the degraded-nesting exception and the three depth tests. No replacement guard is needed: a same-thread re-acquire of a Win32 mutex succeeds immediately, so an accidental nested call cannot deadlock, and the timeout cost the counter avoided only arose for nested degraded reads, which disappear with the three nesting callers.
- **Tests:** a `Transaction` holds the mutex across its body (a second process-simulating thread with the test mutex name times out on a write during it); a read during a transaction waits and then sees the final document. Filter as CONFIG-004.
- **Plan v2:** B039.
- **Related:** CONFIG-039 (consumers), U04A-LFA-022.

### CONFIG-013: Normalizers write the log directly

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs` (about 576-579 and 621-623, `Log.Warn` inside normalization).
- **Problem:** Pure validation rules log as a side effect, so Settings' normalization of an injected config, tests and every reload log the same warnings, and the rules cannot be tested without the logger.
- **Best solution:** (B038) Every section rule takes an `ICollection<string> diagnostics` and appends instead of logging. `ConfigStore.Read` logs the collected diagnostics once per Loaded read. Settings' normalization ignores them or shows them per B133.
- **Tests:** a rule that drops an invalid entry reports one diagnostic and logs nothing. Filter as CONFIG-006.
- **Plan v2:** B038.
- **Related:** U04A-LFA-028; CONFIG-010.

### CONFIG-014: Load-time normalization truncates or drops content by length

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs`: game profile names truncated to 80 (about 684-687), authored device profile names truncated to 48 (about 598-601), `DisabledSources` ids over 32 dropped (about 727), preset references with plugin id over 128 or preset id over 64 dropped (about 863), a custom preset whose `Scenario` is over 128 dropped (about 874), audio endpoint ids over 512 drop the endpoint (about 943) and names over 256 are dropped (about 954). `src/WSGM/Core/Profiles/ProfileEdits.cs` (importer replaces a title longer than 80 with the id, about 264-271). `src/WSGM/Settings/DeviceProfileRowViewModel.cs` (`Name` setter truncates to 48, about 53-56).
- **Problem:** These caps silently cut or delete user content on every load or import, which the no-arbitrary-limits rule forbids.
- **Best solution:** (B038) Type and shape checks only. Remove the length parts of each check and keep the shape parts: a game or authored profile name keeps its full text; `DisabledSources` keeps non-empty, distinct ids; a preset reference keeps non-empty trimmed plugin and preset ids; a custom preset keeps a `Scenario` that is null or non-empty (`values.Scenario is { Length: 0 }` is the only rejection); an audio endpoint keeps a non-empty trimmed id and a non-empty trimmed name or null. Value-range checks on audio formats (channels, sample rate, bits) are validity checks, not length caps, and stay. The importer keeps the full trimmed title when it is non-empty and has no control characters. Then grep the moved rules for any remaining `Length >`/`Length is ... or >` comparison and remove each one that drops content. Authored device-profile names follow SETTINGS-V-007, which B038 executes in the same change: delete `DeviceAuthoredProfile.MaxNameLength`, the row setter's truncation and the keyboard maximum in `PluginSettingsPage.axaml.cs` (with that finding's pass-through fix for `ShowOnScreenKeyboard`); one instruction, not two. Game profile names: `ProfileEdits.SaveGame` keeps its refusal and its exact message ("Give the profile a name of 1 to 80 characters."), but applies the 80-character bound only when the name differs from the stored profile's name (`existing?.Name != name`). Without that, an imported title over 80 characters, which the importer now keeps, would make every later save of that profile (toggling it, editing its processes) fail with the name message, a workflow regression. New names typed by the user are still refused over 80. The same batch removes the 64-point curve cap and the router's matching limit (CRIT-005) and keeps stored layouts that fail `Describe` (WDC-002).
- **Tests:** load fixtures with a 200-character game profile name, a 100-character authored name, a 40-character disabled source, a 200-character plugin id, a 200-character scenario and a 600-character endpoint id all survive unchanged; the importer keeps a 120-character title; `SaveGame` on that imported profile with its name unchanged succeeds, and renaming any profile to 81 characters is still refused with the existing message. Filter as B038: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ProfileEdits|FullyQualifiedName~DeviceProfile"`.
- **Plan v2:** B038.
- **Related:** DEVICE-013, LIBRARY-017, SETTINGS-V-007, CRIT-005, WDC-002.

### CONFIG-015: A hardcoded spatial-audio allow-list drops formats

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`IsKnownSpatialFormat`, about 972-981, used at about 924); apply-time validation in `src/WSGM/Shell/AudioProfileService.cs` (`SetSpatialFormat`, about 300-305).
- **Problem:** The list duplicates WDC `SpatialAudioFormats`. A format Windows reports that is not on the list is silently dropped from the saved preference at load, even though apply already validates against the endpoint's live supported formats.
- **Best solution:** (B038) Delete `IsKnownSpatialFormat` and keep any stored GUID. Apply keeps its live check and reports an unsupported format as today.
- **Tests:** a config with an unlisted spatial GUID loads with that GUID. Filter as CONFIG-006.
- **Plan v2:** B038.
- **Related:** none.

### CONFIG-016: Normalizers re-state defaults by hand

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`NormalizeGameLibrary` re-states initializer defaults, about 714-722, against `src/WSGM/Core/Library/GameLibraryConfig.cs:51, 60`; `Definite` calls at about 421, 554, 657, 889-890, 1016-1022, 1064-1065, 1088-1089).
- **Problem:** Each enum default is written twice (initializer and normalizer), so the two drift.
- **Best solution:** (B038) Delete the re-stated enum defaults and every `Definite` call; the walker's templates come from the initializers (CONFIG-006). Non-enum rules in `NormalizeGameLibrary` move to the game library rules.
- **Tests:** covered by the CONFIG-006 fixtures.
- **Plan v2:** B038.
- **Related:** CONFIG-006.

### CONFIG-017: Custom tabs get a random id per load until saved

- **Severity:** low
- **Where:** `src/WSGM/Core/AppConfig.cs` (custom tab `Id` initializer mints a GUID, about 505); `src/WSGM/Core/ConfigStore.cs` (Normalize mints one for a blank id, about 507).
- **Problem:** A tab without a stored id gets a new random id on every load until something saves the file, so anything keyed by tab id (Steam-side state, selection) loses its key between loads.
- **Best solution:** (B068) The initializer default becomes `""` and Normalize no longer mints. The 0-to-1 migration assigns each blank id a deterministic value from the tab's index and name: `tab-<index>-<name lowered, every character outside a-z and 0-9 replaced by '-'>`, with `-2`, `-3` and so on appended on a collision. It is persisted on the next strict write like the rest of the migration. The type is `CustomTabConfig`. The only construction that relied on the initializer is the new-tab path in `src/WSGM/Overlay/LibraryTabsView.cs` (`_editing = existing is null ? new CustomTabConfig() : Clone(existing)`, about 243): give it `Id = Guid.NewGuid().ToString("N")` there, as today. `Clone` (about 923) already copies the id, and `LibraryTabManager` only adds tabs it received. Re-check with `rg -n "new CustomTabConfig" src tests`.
- **Tests:** a 2.0 fixture with a blank tab id gets the same id on two loads; two same-named tabs get distinct ids. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Migration|FullyQualifiedName~Configuration"`.
- **Plan v2:** B068.
- **Related:** U04A-LFA-019, PV10-015.

### CONFIG-019: Executable-learning writes are untracked and survive shutdown

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Shell/ProfileService.cs` (`LearnRunningExecutable`, `LearnAsync`, about 415-461, `CancellationToken.None`; `LearningIdle` "for tests", about 77).
- **Problem:** Learning starts `Task.Run` chains that call `MutateAsync` with `CancellationToken.None`. They are neither tracked nor joined, and admission never closes, so config.json can be written during or after shutdown. The write is still atomic and valid, so no recovery state is at risk, but shutdown has no owner for it.
- **Best solution:** (B040) Give `ProfileService` one lifetime `CancellationTokenSource` and no other new state. `LearnAsync` passes its token to `MutateAsync` instead of `CancellationToken.None`, and catches `OperationCanceledException` silently so shutdown adds no warning line. `LearnRunningExecutable` returns before starting a learn when the token is cancelled. A synchronous `CloseAdmission()` cancels the token; that is all it does (no separate closed flag: user edits through `MutateAsync` keep their own tokens, and the reloader, the only other caller, is closed at the same step). Rename `LearningIdle` to `Completion` (the same task chain) and drop the "for tests" note. B140 calls `CloseAdmission()` at step 0; its list of step-0 owners must name `ProfileService` beside the fan-out.
- **Tests:** after `CloseAdmission`, a new running application starts no learn write; a learn blocked in the store port observes cancellation, logs nothing, and `Completion` finishes. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ProfileService"`.
- **Plan v2:** B040; shutdown wiring B140 (decided: D1, safety-first ordered steps under one deadline).
- **Related:** CONFIG-018.

### CONFIG-025: AudioProfileService has a test seam and no tests

- **Severity:** low
- **Where:** `src/WSGM/Shell/AudioProfileService.cs` (`IAudioProfileOperations`, about 28-52; constructor defaults to `new CoreAudioProfileOperations()`, about 67-71); `src/WSGM/Shell/GameModeReturnRecovery.cs` (builds its own instance, about 85); `src/WSGM/Shell/ShellSession.cs` (693).
- **Problem:** The port exists "for deterministic tests" but no test uses it, and the hidden native default lets recovery build an extra service silently.
- **Best solution:** (B042) Make `operations` a required constructor parameter; the composition root, restore-shell and panic pass `new CoreAudioProfileOperations()` explicitly (CONFIG-024 hands the instance to recovery). Add tests over a fake `IAudioProfileOperations`. B099 later folds the port into the shared `IAudioEndpoints` adapter and retargets these tests; the review's "WDC instance AudioService" replacement is dropped (critic conflict 9).
- **Tests:** endpoint arrival wait (fake `WatchEndpoints` raises after a delay controlled by a gate), refusal when the configured playback endpoint is not the default, refusal of an unsupported format. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioProfileService"`.
- **Plan v2:** B042 (tests and explicit construction), B099 (port consolidation).
- **Related:** CONFIG-024, CONFIG-026 (refuted; keep both capability reads), WINSVC-020.

### CONFIG-029: WSGM parses EDID itself

- **Severity:** low
- **Where:** `src/WSGM/Core/DisplayProfiles.cs` (registry EDID read and `ReadPrimaryMonitorInstanceId`, about 288-352); `src/WSGM/Core/EdidModes.cs`; `tests/WSGM.Tests/Core/EdidModesTests.cs`.
- **Problem:** WSGM reads EDID from the registry Enum key and parses detailed timings, duplicating WDC `DisplayEdid`, which reads through the exact monitor interface.
- **Best solution:** (B098, conditional) After B069, check whether WDC `DisplayEdid` exposes the advertised refresh rates WSGM needs. If it does, delete `EdidModes.cs` and the registry read, call WDC, and move the `EdidModesTests` fixtures into WDC as equivalence fixtures first. If it does not, change nothing in this release. No new display-mode owner type (critic conflict 10 drops config B7; WINSVC-012 owns the files).
- **Tests:** if moved, the equivalence fixtures pass in WDC on both target frameworks and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RefreshRatePairing|FullyQualifiedName~DisplayResolutionService"`.
- **Plan v2:** B098.
- **Related:** WINSVC-012, WINSVC-027, CONFIG-028.

### CONFIG-030: The --verbose flag is lost at the first reload

- **Severity:** low
- **Where:** `src/WSGM/Program.cs` (`ApplyLogVerbosity`, `HasVerboseFlag`, about 103-106 and 672-706); `src/WSGM/Overlay/OverlayController.cs` (`ApplyConfig` calls `Log.SetVerbosity(config.LogVerbosity)`, about 418-421).
- **Problem:** Program says the flag wins for the run, but every config reload resets verbosity from config.json, and with CONFIG-001 the first profile edit triggers a reload. A one-off verbose reproduction silently drops back to Normal. Verbosity policy also sits in the overlay controller.
- **Best solution:** (B041 with B116) Pass the parsed flag into `ShellSession` the same way `Program.ServiceBoot` is passed today (B111's `StartupOptions` later carries it). The reload path applies `Log.SetVerbosity(verboseFlag ? LogVerbosity.Verbose : config.LogVerbosity)`. Delete the two lines in `OverlayController.ApplyConfig`. Settings processes keep calling `ApplyLogVerbosity` at their own start.
- **Tests:** with the flag set, a reload carrying `LogVerbosity.Normal` leaves Debug lines enabled; without it, the reload applies the config value. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Log|FullyQualifiedName~SessionConfigReloader"`.
- **Plan v2:** B041; the reloader placement in B116.
- **Related:** CONFIG-001.

### CONFIG-031: Log rotation can stall every logging thread

- **Severity:** low
- **Where:** `src/WSGM/Core/Log.cs` (`Write` calls `RotateIfLarge` inside `lock (Gate)`, about 346-357; `RotateIfLarge` waits up to 1 s on `Local\WSGM.LogRotate`, about 255-300).
- **Problem:** When another process holds the rotation mutex, the rotating thread waits up to a second while holding `Gate`, so every logging call in the process, UI thread included, blocks behind it.
- **Best solution:** (B041) Inside `Gate`, only decide that a rotation check is due (set a local). Run `RotateIfLarge` after leaving `Gate`, and change its wait to `WaitOne(0)`: if another process holds the mutex it is rotating right now, so return and try at the next interval. Appends use `FileShare.Delete`, so a rename during a concurrent append is safe. This lives in the internal `LogFile` instance (CONFIG-034).
- **Tests:** with the rotation mutex held by another thread, an append completes without waiting; rotation renames to `.old.log` once the mutex is free. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Log"`.
- **Plan v2:** B041.
- **Related:** U04A-LFA-016, PV10-016; CONFIG-034.

### CONFIG-034: LogLevelTests only assert enum order and a getter

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Core/LogLevelTests.cs` (about 18-58); `src/WSGM/Core/Log.cs`.
- **Problem:** `Log.Change` suppression and held counts, rotation and cross-process append sharing are untested, because the logger is a static singleton bound to the real log path.
- **Best solution:** (B041) Move the file core (path, rotation counters, append, and the `Change` key map) into an internal `LogFile` instance; the static `Log` facade keeps its API and delegates to one instance created by `Init(name, root)`. Tests construct `LogFile` on a temp path without touching the singleton. Rename the test file to `LogFileTests.cs` and replace the enum-order and getter asserts.
- **Tests:** `Change` suppresses a repeated value and reports the held count when the value changes; two `LogFile` instances on one path append without losing lines; rotation (CONFIG-031). Filter `FullyQualifiedName~Log`.
- **Plan v2:** B041.
- **Related:** CONFIG-031, CONFIG-032.

### CONFIG-035: ConfigurationTests assert initializers and touch the real machine

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Core/ConfigurationTests.cs` (getter-only tests at about 769-793, 887-890, 970-978, 990-999, 1227-1236; real mutex at 583-766; reflection serializer at 1211-1225; splash import test at 552-565).
- **Problem:** Several tests only read property initializers, which proves nothing about upgrades. The lock tests contend on the user's real `Local\WSGM.Config` (U04A-LFA-022). One test round-trips through the reflection serializer instead of the production context (U04A-LFA-023). A splash import test sits in the wrong suite (PV10-026). Nothing tests Load, Save, Mutate or quarantine against a real file.
- **Best solution:** B037 moves the lock tests to `Local\WSGM.Tests.Config.<guid>` and a temp root. B038 replaces each getter-only test with an old-shape JSON fixture that asserts the loaded value, switches the serializer test to `ConfigJsonContext`, and moves the splash import test to the splash theme tests. B039 adds the file-backed state fixtures (CONFIG-004, CONFIG-008, CONFIG-012).
- **Tests:** as described; filter `FullyQualifiedName~Configuration|FullyQualifiedName~Splash`.
- **Plan v2:** B038 (resolution), with parts in B037 and B039.
- **Related:** U04A-LFA-022, U04A-LFA-023, PV10-026.

### CONFIG-036: Several config owners have no tests

- **Severity:** low
- **Where:** `src/WSGM/Shell/GameModeReturnRecovery.cs`, `src/WSGM/Core/AtomicFile.cs`, `src/WSGM/Core/DisplayProfiles.cs`, the production `MutateProfilesAsync` adapter in `src/WSGM/Shell/ShellSession.Config.cs`, `src/WSGM/Core/BootManifestWriter.cs` (whose tests re-implement the projection).
- **Problem:** The owners that carry CONFIG-001, CONFIG-003 and CONFIG-024 have no direct tests, which is how the adapter's ignored bool and the boot re-arm went unnoticed.
- **Best solution:** Add the tests in the batch that changes each owner, so each change lands with its guard: `AtomicFile` (durable replace, temp cleanup on failure) and `BootManifestWriter` through the production projection in B039; `ConfigProfileStore` (no write on a false edit) in B040; `DesktopReturnRecovery` in B042; `DisplayProfiles` through the operating-point fake that B098 adds.
- **Tests:** as listed in CONFIG-009, CONFIG-003, CONFIG-001, CONFIG-024 and B098.
- **Plan v2:** B039 (resolution), with parts in B040, B042 and B098.
- **Related:** U04A-LFA-025, U04A-LFA-008.

### CONFIG-037: Sidecar stores overwrite state they could not read

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Shell/ArtworkStateStore.cs` (`Read` caches an empty state after an IO, access or parse failure, about 77-110; `Write`, about 124-140); `src/WSGM/Core/QuickAccessFolds.cs` (`Read`, about 101-121; `SetOpen`, about 66-90); the existing correct pattern in `src/WSGM/Core/Library/ImportStateStore.cs` (`Read` and `Quarantine`, about 315-366).
- **Problem:** A transient read failure on artwork.json caches an empty state, and the next `SaveFilter` or `SaveGame` overwrites the user's saved artwork links and filters. quick-access-folds.json does the same with fold state. These are preferences, not recovery state, which is why the verifier lowered it.
- **Best solution:** (B039) Copy `ImportStateStore`'s two existing rules into `ArtworkStateStore`; no shared `StateFile` type and no five-state model (critic conflict 16, LIBRARY-013). (1) An `IOException` or `UnauthorizedAccessException` on read is not cached: reads in that call return the empty answer, and `SaveFilter`/`SaveGame` log "artwork.json could not be read; nothing was saved" and return without writing, which is exactly how a failed `Write` already behaves, so callers need no change. (2) A `JsonException` moves the file aside as `artwork.json.corrupt-<UTC yyyyMMddHHmmss>` with `File.Move(..., false)` (as `ImportStateStore.Quarantine` does) and then starts empty and writable; if the move fails, treat it as rule 1. Make the shape filter null-tolerant (CONFIG-V-005). For quick-access-folds.json, only rule 1: on an IO or access failure `Read` does not cache, `IsOpen`/`Open` return the default answer, and `SetOpen` returns the error text through its existing failure return without writing; a parse failure keeps today's "start empty" because only fold state is lost. update.json keeps its current reset-on-failure behaviour (it holds only the last update-check result). The B039 test filter's `StateFile` term matches nothing and can be dropped.
- **Tests:** artwork: a file held with `FileShare.None` during a `SaveGame` leaves the file byte-identical; garbage JSON is moved aside and a later save writes a fresh file. Folds: an unreadable file is not overwritten by `SetOpen`, which returns a message. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ArtworkState|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~ImportState"`.
- **Plan v2:** B039.
- **Related:** U04A-LFA-002, A02-F007 (same defect class in the SDK journal), LIBRARY-013, CONFIG-V-005.

### CONFIG-039: Three callers depend on nested lock scopes

- **Severity:** low
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs` (`CommitWsgmSetting`, about 31-43); `src/WSGM/Settings/SettingsViewModel.Save.cs` (save transaction, about 530-576); `src/WSGM/Core/CommonPluginConfiguration.cs` (`ApplicationPluginConfigurationStore.Read`, about 54-58).
- **Problem:** These are the callers that need the depth machinery CONFIG-012 deletes.
- **Best solution:** (B039) Rewrite each as one `Transaction`. `CommitWsgmSetting(change, boot)` becomes `store.Transaction(tx => { change(tx.Config); tx.Save(tx.Config); if (boot) BootManifestWriter.WriteCurrent(tx.Config); return tx.Config; })`. The Settings save uses `tx.Config` where it called `LoadForMutation`, `tx.Save` for both the first save and `RestoreSlotsThatFailedToPromote`, and keeps the splash `Commit` and the boot manifest write inside the same transaction, so the reload still never sees the intermediate document. B013 (SETTINGS-B1) lands first and builds the new merge; B039 only swaps the lock calls inside it (critic conflict 23). `ApplicationPluginConfigurationStore.Read` becomes `store.Transaction(tx => ReadFrom(tx.Config, identity))` (strict, no save), and `Save` becomes `Update`.
- **Tests:** the existing Settings save and plugin configuration tests on a temp root; a Settings save followed by a read sees config.json and boot.json in agreement. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~Configuration|FullyQualifiedName~CommonPlugin"`.
- **Plan v2:** B039 (after B013).
- **Related:** CONFIG-012.

### CONFIG-040: Views and the session touch config directly

- **Severity:** low
- **Where:** `src/WSGM/Shell/ShellSession.Actions.cs` (`EnableDeviceIntegrationAsync` sets `_config.DeviceIntegration.Enabled = true` from a worker, about 114-115); `src/WSGM/Shell/ShellSession.cs` (the game library's `updateSettings` callback assigns `_config.GameLibrary = persisted.GameLibrary` on the Steam command's thread, about 833-834, so `GameLibraryService` can read its sources back before the reload arrives; B124 moves that read-back value into the service the same way); UI-thread loads in `src/WSGM/Settings/SettingsViewModel.cs:18`, `src/WSGM/Settings/SettingsViewModel.System.cs:219` and `src/WSGM/Settings/SettingsWindowServices.cs:25`; view-owned config reads in `src/WSGM/Overlay/LibraryTabsView.cs` (90, 375, already on a worker). Verifier corrections: `SettingsWindow.axaml.cs:138` is a comment, and `ShellSession.Modes.cs:824` writes the session field `_pendingReturnLayout`, not the shared config.
- **Problem:** A worker mutates the shared `AppConfig` snapshot that the UI thread reads, without synchronization; three Settings reads can block the UI thread for up to the 2 s mutex timeout; views own config IO that belongs to their services.
- **Best solution:** Split by owner. B039 switches every such read to the instance store's `Read()` with no behaviour change. B124 removes the worker mutation: `EnableDeviceIntegrationAsync` only calls `Update`, and the watcher reload delivers the new config instance; if the overlay banner needs an immediate change, it updates its own view state on the UI thread. B133 reads config once on a worker before the Settings window opens and passes the `ConfigRead` in, so `SettingsViewModel`, `DescribeOtherManagers` and the accent callback no longer load on the UI thread. B137 routes `LibraryTabsView`'s reads through the `ILibraryTabs` interface.
- **Tests:** in B124, the device-integration enable path does not touch `_config`; in B133, constructing Settings performs no config read on the calling thread (inject the read). Filters of those batches.
- **Plan v2:** B039 (resolution), with the session part in B124, the Settings part in B133 and the library tabs part in B137.
- **Related:** U05-LFB-019, SESSION-013, SETTINGS-022.

### CONFIG-044: Plugin manifests cached in config.json rewrite it on every refresh

- **Severity:** low
- **Where:** `src/WSGM/Shell/PluginSettingsCoordinator.cs` (`ConfigStore.Mutate` with `CacheDeclaration`, about 150-170).
- **Problem:** Every manifest publication rewrites config.json even when the declaration is unchanged, which triggers a full shell reload (CONFIG-001).
- **Best solution:** (B039) Keep the cache where it is (moving it would cost a migration). `CacheDeclaration` returns whether the stored declaration changed, comparing the serialized declaration (through `ConfigJsonContext`) before and after, and the call becomes `store.Update(config => { lock (_gate) { return ...CacheDeclaration(...); } })` returning false when unchanged or stale.
- **Tests:** publishing the same manifest twice writes config.json once. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginSettingsDeclarationCache"`.
- **Plan v2:** B039.
- **Related:** CONFIG-001.

### CONFIG-V-001: The strict config path's exception contract is wrong and callers' catch filters miss it

- **Severity:** low
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`Mutate` documents `InvalidDataException`, about 1193; `DeserializeConfig` actually throws `JsonException` or `InvalidOperationException`, about 172-231; the mutex throws `TimeoutException`); `src/WSGM/Shell/ThemeService.cs` (`ChangeConfig` catch, about 749-751); `src/WSGM/Shell/AnimationService.cs` (about 981-982); `src/WSGM/Shell/SoundPackService.cs`; `src/WSGM/Shell/ShellSession.cs` (734, 740, 755 route Steam theme, sound and animation changes through `CommitWsgmSetting`).
- **Problem:** With a corrupt config.json, a theme, sound or animation change from Steam throws `JsonException` out of the command handler instead of returning `Refuse`, because the callers' filters list IO, access, invalid-operation and timeout exceptions only.
- **Best solution:** (B039) Add `ConfigUnavailableException(ConfigReadStatus Status, string message, Exception? inner)` as the single strict-path failure type. `Update` and `Transaction` throw it for Corrupt and Unreadable reads and for a mutex timeout or open failure. Anything thrown by the caller's own lambda propagates unchanged. Fix the doc comment. Then grep every caller of `Update`, `Transaction` and the old `Mutate`/`AcquireLock`/`LoadForMutation` and replace config-related `TimeoutException`/`InvalidDataException`/`JsonException` entries in their filters with `ConfigUnavailableException`: `ThemeService.ChangeConfig` and `AnimationService` (both filter IO, access, invalid-operation and timeout only, so `JsonException` escapes), and `CommonPluginSteamUiSource` (about 119 and 302, filtering invalid-operation, argument and timeout). `SoundPackService` needs no change: its command paths catch every exception and return a failed result. `GameLibraryService` (the four `_updateSettings(...)` calls, about 571, 626, 732, 788) has no catch at all around the write that `ShellSession` routes through `CommitWsgmSetting`, so a busy or broken config.json throws straight out of the Steam command; wrap each call so `ConfigUnavailableException` returns `Refuse(ex.Message)`, the pattern `ThemeService` uses, which adds no new UI string.
- **Tests:** with a corrupt config.json, a theme, animation and sound change and a game-library source toggle each return a refusal and leave the file untouched. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService|FullyQualifiedName~SoundPackService|FullyQualifiedName~GameLibraryService|FullyQualifiedName~Configuration"`.
- **Plan v2:** B039 (its file list lacks `src/WSGM/Shell/GameLibraryService.cs` and `src/WSGM/Shell/CommonPluginSteamUiSource.cs`; add both).
- **Related:** CONFIG-004.

### CONFIG-V-003: Quick Access pin saves can persist out of order

- **Severity:** low
- **Where:** `src/WSGM/Overlay/OverlayController.cs` (pin toggle, about 970-996: updates `_config.QuickAccessPins` in place, then starts an unordered `Task.Run(() => ConfigStore.Mutate(...))` per change); the echo through `ShellSession.Config.cs` reload into `OverlayController.ApplyConfig` (`SetPins`, about 410).
- **Problem:** Two quick pin changes start two unordered saves that race for the mutex, so the older full snapshot can be written last. The shell's own reload then pushes that stale list back into the overlay, and the user's last pin change visibly reverts. The toggle also mutates the shared config snapshot.
- **Best solution:** (B122) Chain the saves as `LibraryTabManager.SaveTabOrder` does: one `Task` field, and each change awaits the previous save before writing its snapshot, so the last change is persisted last. The save uses `Update` and returns false when the stored list already equals the snapshot. The controller keeps its current pin list in its own field (seeded from config in `ApplyConfig`) instead of writing into `_config`. No pins owner and no Changed event (plan v2).
- **Tests:** two toggles in quick succession persist the second list, with a gate holding the first save. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayControllerLifecycle|FullyQualifiedName~QuickAccessSheet"`.
- **Plan v2:** B122.
- **Related:** OVERLAY-012, CONFIG-001.

### CONFIG-V-004: The config and log-rotation mutexes have no explicit ACL in a mixed-elevation process set

- **Severity:** low (verify once before any change)
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`new Mutex(false, @"Local\WSGM.Config")`, about 1368); `src/WSGM/Core/Log.cs` (`Local\WSGM.LogRotate`, about 268).
- **Problem:** Both mutexes take the creator's default security. The shell may run elevated, as do the elevated one-shots. While an elevated process holds the object, an unelevated WSGM process may get `UnauthorizedAccessException` on open (a high integrity label blocks the modify right `ReleaseMutex` needs). Then a save fails and a read degrades to an unlocked read. The window is short because handles live for one scope.
- **Best solution:** First the attended check plan v2 lists under attended evidence: an elevated process holds `Local\WSGM.Config` while an unelevated process opens and waits on it. Only if that reproduces the failure, create both mutexes with an explicit security descriptor that grants the current user's SID full access and carries a medium mandatory label, so an unelevated process can open them. Build it from `MutexSecurity`/SDDL in the code that `UserDataContext` names the mutex for. If the check does not reproduce it, record no-change. B037 must not change ACLs before the check. This is a functional fix, not the hardening DECISIONS.md drops: the descriptor widens access so WSGM's own mixed-elevation processes can share the lock, and it restricts nothing.
- **Tests:** none automated (it needs two integrity levels); the attended check is the evidence.
- **Plan v2:** attended evidence (section 4), then a batch appended after B037 only if reproduced.
- **Related:** CONFIG-011.

### CONFIG-V-008: The EDID identity migration has no config fixture

- **Severity:** low
- **Where:** stored `DisplayTargetIdentity` values in config.json: `GameModeLaunch.KnownDisplays[].Target`, every `DisplayLayout.Outputs[].Target`, `WaitForDisplay` and `GameModeLaunchRecovery.PendingReturnLayout` (`src/WSGM/Core/GameModeLaunchConfiguration.cs:52, 132-147, 176`).
- **Problem:** The plan requires that fixing EDID decoding (B001, W02_01) does not erase saved monitor paths, but no batch loads a real 2.0 config with stored targets and checks identity equality and layout matching after the decoder change.
- **Best solution:** (B068) Add a real-shaped 2.0 fixture that carries stored display targets in all four places. Assert that after load, migration and a save round trip each target compares equal to the identity WDC now reports for the same EDID bytes, and that layout matching still finds each output. No EDID "upgrade" migration (plan v2 dropped it); the fixture proves none is needed.
- **Tests:** the fixture above in the migration suite. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Migration|FullyQualifiedName~Configuration"`.
- **Plan v2:** B068.
- **Related:** W02_01, A01-F001, WDC-012, CONFIG-017.

## Nit

### CONFIG-020: Profile change detection serializes the whole store under the lock

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ProfileService.cs` (`SameStore`, about 482-486, called under `_gate` from `ApplyConfig` and `MutateAsync`; whole-store serialization in `SaveGameAsync`, about 324-326).
- **Problem:** Each mutation and reload serializes the full profile store twice while holding `_gate`, which the UI thread also takes to read `Current`.
- **Best solution:** (B040) Keep the serialized JSON of the published store in a `_currentJson` field, set together with `_current`. Callers serialize the candidate outside `_gate`; inside the lock they compare strings only. All writers run under `_writeGate` after CONFIG-002, so computing outside `_gate` is race-free. The per-edit before and after serialization in `SetAsync` and `SaveGameAsync` stays (it is edit-local).
- **Tests:** existing ProfileService tests; filter `FullyQualifiedName~ProfileService`.
- **Plan v2:** B040.
- **Related:** CONFIG-002.

### CONFIG-021: Two JSON clone mechanisms

- **Severity:** nit
- **Where:** `src/WSGM/Core/ConfigStore.cs` (`CloneJson`, about 1241-1245); `src/WSGM/Core/Profiles/ProfileFields.cs` (`Copy`, about 323-329).
- **Problem:** Two implementations of the same deep copy through the production contract.
- **Best solution:** (B038) Add `internal static T ConfigJson.Clone<T>(T value, JsonTypeInfo<T> typeInfo)` in `Core/ConfigJson.cs`, delete `CloneJson` and `ProfileFields.Copy`, and change callers to `ConfigJson.Clone(x, ConfigJsonContext.Default.X)`.
- **Tests:** compile plus the existing profile and splash tests; filter as CONFIG-006 with `FullyQualifiedName~ProfileResolver`.
- **Plan v2:** B038.
- **Related:** CONFIG-010.

### CONFIG-022: Settings writes the Global controller target directly

- **Severity:** nit (verifier lowered from low)
- **Where:** `src/WSGM/Settings/SettingsViewModel.Save.cs` (Global `ControllerTarget` merge, about 406-422; fan-curve references, about 455-460).
- **Problem:** The review said Settings bypasses `ProfileEdits`. The fan-curve path already calls `ProfileEdits.RemoveFanCurveReferences`, and the controller target is one field merged onto the fresh strict load under the lock, which is exactly what a `ProfileEdits` call would do. No behaviour is wrong.
- **Best solution:** (B013) The shared-field table (`Core/WsgmSharedSettings`) lists `Profiles.Global.ControllerTarget` and writes it only when it was edited in Settings. No new `ProfileEdits` method for a single field.
- **Tests:** B013's round-trip guard test. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"`.
- **Plan v2:** B013.
- **Related:** SETTINGS-V-002, critic conflict 23.

### CONFIG-023: The overlay-test profile store duplicates a test builder

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs` (`MutateSimulatedProfilesAsync`, about 290-302); `tests/WSGM.Tests/Builders/PerformanceBuilders.cs` (about 10-21).
- **Problem:** Two copies of the same in-memory profile store.
- **Best solution:** (B040) One `internal sealed class InMemoryProfileStore` in `Shell/InMemoryProfileStore.cs` with the same `MutateAsync(Func<ProfileConfig,bool>, CancellationToken)` shape as `ConfigProfileStore`, used by overlay-test and by `PerformanceBuilders`. It honours the edit's bool the same way.
- **Tests:** existing ProfileService and PerformanceService tests through the shared store. Filter `FullyQualifiedName~ProfileService|FullyQualifiedName~PerformanceService`.
- **Plan v2:** B040.
- **Related:** CONFIG-001.

### CONFIG-028: DisplayProfiles has a stale doc and a misleading name

- **Severity:** nit (half refuted by the verifier)
- **Where:** `src/WSGM/Core/DisplayProfiles.cs` (doc referring to a "display-profile path above", about 186-192; `ReadPrimaryOperatingPoint`, about 33-37).
- **Problem:** The class is a static primary-display mode facade, not "profiles", and one doc comment points at code that no longer exists. The second `DisplayLayouts.Observe()` in `ReadPrimaryOperatingPoint` is a stability check (the primary must be the same target before and after the read) and stays.
- **Best solution:** (B098) Fix the stale doc comment. Rename the class to `PrimaryDisplayModes` only if B098 already moves it onto WDC `DisplayEdid` (CONFIG-029); otherwise leave the name to avoid churn.
- **Tests:** none beyond B098's filter.
- **Plan v2:** B098.
- **Related:** CONFIG-029, WINSVC-027.

### CONFIG-032: A redundant private SetMinimumLevel wrapper in Log

- **Severity:** nit (the `Log.Observe` half refuted by the verifier)
- **Where:** `src/WSGM/Core/Log.cs` (`SetMinimumLevel`, about 114-117; `Observe`, about 171-196).
- **Problem:** The private `SetMinimumLevel` wrapper adds nothing over its single caller. `Log.Observe` is not a duplicate of `TaskFaults.ObserveFaults`: it logs faults with the operation name at 14 call sites, while `ObserveFaults` only swallows them (verifier batch problem 7).
- **Best solution:** (B041) Inline `SetMinimumLevel` into `SetVerbosity`. Keep `Log.Observe` with its logging intact.
- **Tests:** filter `FullyQualifiedName~Log`.
- **Plan v2:** B041.
- **Related:** U04A-LFA-026.

### CONFIG-041: AppConfig housekeeping

- **Severity:** nit
- **Where:** `src/WSGM/Core/AppConfig.cs` (duplicate `<summary>` on `Splash`, about 738-746; `using WSGM.Themes` for `SplashPresets.Wsgm20()` and `AccentPalette.DefaultAccent`, about 6, 747, 753; `using Microsoft.Win32` for `RegistryValueKind`, about 4, 919, 935).
- **Problem:** A duplicated doc block, and a persisted model that depends on the Themes and Win32 namespaces.
- **Best solution:** (B038) Delete the duplicate summary. Leave the two dependencies: `SplashPresets.Wsgm20()` is pure data, the default accent moves to Core in B120, and `RegistryValueKind` is the wire type of the recovery snapshots, so replacing it would change the wire format or need a mapping for no defect.
- **Tests:** none.
- **Plan v2:** B038.
- **Related:** U04A-LFA-029, U04A-LFA-028.

### CONFIG-043: Profile snapshot accessors repeat work on UI paths

- **Severity:** nit
- **Where:** `src/WSGM/Core/Profiles/ProfileResolver.cs` (`ProfileSnapshot.Game` and `Layers` recompute on every access, about 146-152); `src/WSGM/Core/Profiles/ProfileFields.cs` (`Count` calls `Enum.GetValues` per call, about 311-318).
- **Problem:** Each access repeats a linear search and allocates; both run on UI paths.
- **Best solution:** (B040) Turn `ProfileSnapshot` from a positional record into a `sealed class` with a constructor that computes `Game` and `Layers` once into get-only properties. A record must not cache them, because `with` would copy a stale value: the one `with` today (`ProfileService.SetRunningApplication`) becomes `new ProfileSnapshot(_current.Config, active, _current.Generation + 1)`. Grep for any use of record equality or deconstruction on `ProfileSnapshot` first. In `ProfileFields`, cache `Enum.GetValues<ProfileField>()` in a static readonly array.
- **Tests:** existing ProfileResolver and ProfileService tests. Filter `FullyQualifiedName~ProfileResolver|FullyQualifiedName~ProfileService`.
- **Plan v2:** B040.
- **Related:** none.

### CONFIG-045: A fan-out test depends on timing

- **Severity:** nit
- **Where:** `tests/WSGM.Tests/Shell/ProfileFanOutTests.cs` (300 ms `Task.Delay`, about 68).
- **Problem:** A wall-clock wait makes the test slow and flaky.
- **Best solution:** (B040) Use a consumer gated by a `TaskCompletionSource` that the test releases.
- **Tests:** filter `FullyQualifiedName~ProfileFanOut`.
- **Plan v2:** B040.
- **Related:** CONFIG-018.

### CONFIG-V-005: ArtworkStateStore can throw NullReferenceException on explicit JSON nulls

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ArtworkStateStore.cs` (`link.ProviderId.Length`, `link.GameId.Length`, `filter.Tab.Length` after the try/catch, about 100-110).
- **Problem:** `"providerId": null` deserializes to null and throws out of `FindGame`/`SaveGame`.
- **Best solution:** (B039, with CONFIG-037) Use null-tolerant shape checks: `link.ProviderId is { Length: > 0 }`, `link.GameId is { Length: > 0 }`, `filter.Tab is { Length: > 0 }`.
- **Tests:** a file with null provider and tab fields loads with those entries dropped. Filter `FullyQualifiedName~ArtworkState`.
- **Plan v2:** B039.
- **Related:** CONFIG-037.

### CONFIG-V-006: A late watcher event recreates the debounce timer after shutdown

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs` (`Debounce` does `_configDebounce ??= new Timer(...)` with no disposed check, about 164-172); `src/WSGM/Shell/ShellSession.Shutdown.cs` (disposes the timer before the watcher, about 657-664).
- **Problem:** An event between the two disposals creates a timer nobody disposes, which then runs one config read (and possibly a quarantine copy). The apply itself is blocked by `_disposed`.
- **Best solution:** (B116) `SessionConfigReloader` owns watcher and timer and stops them in this order: disable and dispose the watcher first, then dispose the timer; `Debounce` returns early under its gate once the reloader is closed.
- **Tests:** a watcher event raised after `CloseAdmission` creates no timer and runs no read. Filter `FullyQualifiedName~SessionConfigReloader`.
- **Plan v2:** B116.
- **Related:** SESSION-018.

### CONFIG-V-007: ProfileFanOut's constructor doc is wrong

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ProfileFanOut.cs` (constructor summary, about 36); `src/WSGM/Shell/ShellSession.Performance.cs` (the caller queues the first snapshot, about 361).
- **Problem:** The doc says the constructor "applies its current snapshot once"; it only subscribes.
- **Best solution:** (B040) Fix the doc to "Subscribes to the service; the owner queues the first snapshot." Leave the initial queue in the caller.
- **Tests:** none.
- **Plan v2:** B040.
- **Related:** CONFIG-018.

## Refuted or no-change

- **CONFIG-027** (late audio-watch callback releases a disposed semaphore): refuted. WDC `EndpointCallback.Raise` wraps every callback in a try/catch and `Dispose` detaches the delegate first, so a late release is swallowed and nothing changes. Dropped from B042 and from the W01 note.
- **CONFIG-033** (the `Log.Change` key map is cleared at 512 keys): reviewed no-change. The cap bounds memory, not content; only repeat counts reset, so it complies with the no-arbitrary-limits rule.
- **CONFIG-042** (Settings normalizes a config that Load already normalized): refuted. It is deliberate and commented: injected configs in `FromLoadedConfig` and the tests never pass through Load, and Normalize is idempotent for a loaded config.
- **CONFIG-026** (one audio apply probes capabilities twice): refuted, no change. In `AudioProfileService.Apply`, `SetPlaybackFormat` writes the device format before `SetSpatialFormat` validates against `ReadPlaybackCapabilities`; spatial support can change with the device format, so the second probe is the fresh read the spatial check needs, and one shared read would validate spatial sound against stale capabilities. It runs once per Game Mode entry or return, not on a high-rate path. B042 drops it.
- **CONFIG-046** (the reload starts an untracked common-plugin reconcile): refuted, no change. `CommonPluginManager.ReconcileAsync` bumps `_requestedRevision` and `ReconcileCoreAsync` runs under `_gate`, returning when a newer revision or `_stopping` is seen, so two quick reloads never run two reconciles at once. `CommonPluginManager.StopAsync` sets `_stopping` and waits on the same `_gate` within its deadline, and the reload passes `_shutdownCancellation.Token`, so shutdown already orders any in-flight reconcile before the plugins stop; `ApplyCommonPluginConfigAsync` already handles every exception. B116 drops the "tracks ApplyCommonPluginConfigAsync" item.
