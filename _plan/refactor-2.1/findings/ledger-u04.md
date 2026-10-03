# Lifecycle audit units A and B (ledger U04A-LFA, U04B-LFA) findings

Scope: the 84 ledger ids of the first Claude audit's two lifecycle units. U04A-LFA (35 rows, `reports/U04A-LFA.review.md`) covers process startup and exit in `Program` and `App`, `ConfigStore` and `AppConfig`, the boot manifest, setup answers, the update checker, logging, `ConsoleTool`, the crash-loop breaker, small Core helpers and their tests. U04B-LFA (49 ids, 52 files listed in `unit-plan.md`) covers Explorer exit, restore and the shell anchor, elevation and de-elevation, shell registration, UAC and lock-on-wake, Steam autostart, other managers and the shell Interop. Only U04B-LFA-001 to 012 were saved; 012 stops mid-observation and 013 to 049 have no body (`recovered-U04B-write-1.md`, `claude-evidence-status.json`). Codex A01 and A02 preserved every row without confirming or rejecting it and raised no A0x-F id in this scope.

Method: a row that a current domain review cites as covered gets a short section naming the covering finding and its batch. Every other row was re-checked against `master` 1329813f and gets a full write-up or a no-change entry. Severity is the current one: the covering finding's severity after verification, with the ledger's original noted when it differs. Line numbers were checked against 1329813f where this file gives them; anchor edits by symbol anyway.

Counts: 44 sections (0 critical, 1 high, 13 medium, 24 low, 6 nit), 4 ids in the no-change list and 37 ids in the missing-bodies list. One id was added by the solution check (U04A-C-001).

Plan v2 batches for this area: B006, B111, B112, B140 (session); B016, B095 (winsvc); B024, B025, B027, B028, B029, B030, B031 (install); B037, B038, B039, B041, B068 (config); B120, B121 (settings); B032, B176 (build hygiene and test placement). D2 is decided: accept exactly the plan v2 list, which names none of the byte bounds in this scope (update metadata 1 MiB, setup download 1 GiB, boot.json 64 KiB, setup answers 256 KiB, bundle.json 1 MiB). U04A-LFA-010, U04A-LFA-021 and U04A-C-001 therefore remove them. One plan inconsistency to resolve while implementing: B111 lists SESSION-005 as resolved but its text says "restore-shell wait unchanged" (see U04A-LFA-015).

Maintainer decisions (`DECISIONS.md`, 2026-10-03) applied here: all security hardening is dropped, so U04B-LFA-002 (task XML in a user-writable folder) moves to the no-change list, U04A-LFA-011 keeps only its `.partial` cleanup, and nothing in this file relies on B025 ignoring the boot.json `ExePath`, on the B026 temp staging, on the B027 `%ProgramData%\WSGM` DACL or download move, or on the B029 admin-only XML folder. B027 keeps only the functional `UpdateChecker` work and B029 only the task cleanup. config.json from a newer WSGM loads best effort as today, so B039 has no UnsupportedSchema outcome and B068 has no write refusal (U04A-LFA-002, U04A-LFA-007). D1 is decided for B140 (U04B-LFA-006). No finding in this scope involves device readback (D9).

## High

### U04A-LFA-002: The defensive loader conflates absent, corrupt and briefly unreadable files, so recovery readers restore nothing

- **Severity:** high (CONFIG-004; ledger said medium)
- **Where:** `src/WSGM/Core/ConfigStore.cs:122-139` (`Load`), `:1096-1121` (`PreserveCorruptFile`); `src/WSGM/Core/Installer.cs:35, 45, 72`; `src/WSGM/Program.cs:102, 259-263, 784-794`; `src/WSGM/Core/SteamAutostartService.cs:146`; `src/WSGM/Core/OtherManagers.cs:457`.
- **Coverage:** confirmed by CONFIG-004, with the consumer instances CONFIG-003 (boot manifest re-armed from defaults) and CONFIG-005 (restore paths report success). The verifier corrects the mechanism: WSGM's own replace cannot cause the sharing violation because `Load` takes the writer mutex; the transient comes from a non-WSGM opener or a degraded read. It adds unlisted lenient consumers (`Program.cs:596`, `OtherManagers.cs:372`, `SteamAutostartService.cs:92`, `LaunchWrapperStore.cs:20`, `ShellRegistration.cs:115`, the last deliberately fail-open).
- **Plan v2:** B039 (read outcomes Loaded, Absent, Corrupt, Unreadable; quarantine only after a parse failure; fail-closed recovery callers report failure on Unreadable, fail-open ones keep today's fallback). Decided by the maintainer: no UnsupportedSchema outcome and no read-only mode; a config.json from a newer WSGM, or one with unknown recovery enums, loads best effort as today and reads as Loaded with whatever is understood.
- **Related:** CONFIG-003, CONFIG-004, CONFIG-005, U05-LFB-002, SESSION-017, U04B-LFA-004, PV10-002, PV11-002.

## Medium

### U04A-LFA-001: The enum-name repair pass misses persisted enums, so one unknown name quarantines the whole config

- **Severity:** medium
- **Where:** `src/WSGM/Core/ConfigStore.cs:172-233` (`DeserializeConfig`, hand list at 183-229); `src/WSGM/Core/AppConfig.cs:129, 132, 842, 919, 935`; `src/WSGM/Core/Library/GameLibraryConfig.cs:51, 60`; `src/WSGM/Core/DevicePowerPresetReference.cs:28`.
- **Coverage:** confirmed by CONFIG-006, which adds `GameLibrary.DefaultMode` and `ArtworkPreference`; the verifier adds `DevicePowerCustomValues.WindowsMode` inside profile power presets.
- **Plan v2:** B038 (tolerant enum converter plus metadata walker, hand list deleted).
- **Related:** CONFIG-006, PV10-001, U04A-LFA-007.

### U04A-LFA-003: Setup answers collapse three edge gestures into one switch, so an update or repair rewrites per-edge choices

- **Severity:** medium
- **Where:** `src/WSGM/Core/SetupAnswers.cs:177-178` (`Export` ORs the three edges), `:216-218` (`ApplyTo` writes the one value back to all three); `src/WSGM/Program.cs:397-404`; `src/WSGM.Setup/UI/Pages/ProfilePage.cs:243-256`.
- **Coverage:** covered by INSTALL-020 (install.md C17 rejects the ledger's "guidance only" disposition: `Setup/AGENTS.md:33-35` already forbids this, the defect is in code). INSTALL-020's body was never written; its substance is the B028 step: `ApplyTo` changes a field only when the answer differs from `Export(current)`, and the gestures are written only when the collapsed value changed. No edit mask, no schema change.
- **Plan v2:** B028.
- **Related:** INSTALL-020, INSTALL-V-004, U04A-LFA-024, U04A-LFA-012.

### U04A-LFA-004: The per-user data root is the static `Log.Directory` with no seam, so the strict-mutation safety rule is untestable

- **Severity:** medium
- **Where:** `src/WSGM/Core/Log.cs:79-81`; 27 code sites in 19 files (config.verify.md CONFIG-011 corrected count), among them `ConfigStore.cs:105, 1106, 1135, 1162`, `BootManifestWriter.cs:15`, `UpdateChecker.cs:68`, `UnelevatedLauncher.cs:47, 57`, `Program.cs:638, 819`.
- **Coverage:** confirmed by CONFIG-011 (library.md C11 adds `ImportStateStore` and `ArtworkStateStore`).
- **Plan v2:** B037 (`UserDataContext(Root, ConfigMutexName)` with `ForCurrentUser()` only, instance `ConfigStore`, `Log.Directory` deleted at every site).
- **Related:** CONFIG-011, LIBRARY-014, BUILD-001, U04A-LFA-022, PV10-004.

### U04A-LFA-005: Elevated one-shots resolve per-user data from the elevated token's profile

- **Severity:** medium (hypothesis; confirmed for over-the-shoulder elevation only)
- **Where:** `src/WSGM/Core/Log.cs:80-81`; `src/WSGM/Program.cs:122-127, 312-334, 376-449`; setup side `src/WSGM.Setup/Engine/SetupEngine.cs` (`DeleteUserData`, `ReadLedgerDevices`, per-user shortcuts).
- **Coverage:** covered by INSTALL-005 (install.md C9/C18 widen it to the setup side). install.verify.md replaces the remedy: no target-user plumbing; setup's `Detect` compares the session's interactive user (`WTSQuerySessionInformation` user and domain) with the process user and shows the existing actionable refusal before any machine change. Critic conflict 17 keeps `UserDataContext` at `ForCurrentUser()` only.
- **Plan v2:** B030.
- **Related:** INSTALL-005, CONFIG C23, U04A-LFA-004.

### U04A-LFA-007: `ConfigStore` and `AppConfig` concentrate every subsystem's schema and rules, without a schema version

- **Severity:** medium
- **Where:** `src/WSGM/Core/ConfigStore.cs` (mutex, IO, JSON repair and about 12 feature normalizers); `src/WSGM/Core/AppConfig.cs:587-975`.
- **Coverage:** confirmed by CONFIG-010 (rules split and normalizers moved beside their section types) and the schema-version part by CONFIG-B3.
- **Plan v2:** B038 (rules and repair split), B068 (`SchemaVersion` 1, migrate on read, one `config.v0.json` written with `CreateNew` before the first v1 save). Decided by the maintainer: a file with a newer `SchemaVersion` is not refused for writes and gets no read-only mode; it loads best effort as today (unknown members and enum names are skipped) and later saves write the current schema. B068's "newer schema reads as UnsupportedSchema and strict writes refuse" step and its "a v2 file is never written" test are dropped; the replacement test loads a v2 fixture with an unknown section and an unknown enum name and asserts the understood fields survive.
- **Related:** CONFIG-010, CONFIG-017, PV10-006, STEAMHOST (observed caches in `AppConfig`).

### U04A-LFA-009: config.json and boot.json are replaced without a durable flush

- **Severity:** medium (hypothesis)
- **Where:** `src/WSGM/Core/ConfigStore.cs:1165` (`durable: false`); `src/WSGM/Core/BootManifest.cs:121`; mechanism in `src/WSGM/Core/AtomicFile.cs:64-75`.
- **Coverage:** confirmed by CONFIG-009. Plan v2 takes the durable flush and drops the ledger's `.bak` idea ("No .bak, no repair UI").
- **Plan v2:** B039.
- **Related:** CONFIG-009, PV10-007.

### U04A-LFA-017: Startup mode is ambient static state that `App` reads through `Program`

- **Severity:** medium (SESSION-001; ledger said low)
- **Where:** `src/WSGM/Program.cs:42-52`; `src/WSGM/App.axaml.cs:51-58`; `Program.cs:738, 774` (`Panic`).
- **Coverage:** confirmed by SESSION-001: an immutable `StartupOptions` passed to `App` and the runtime.
- **Plan v2:** B111.
- **Related:** SESSION-001, U04A-LFA-006.

### U04B-LFA-001: Sync de-elevation wrapper collapses `Unknown` dispatch, so callers launch a competing Explorer or Steam

- **Severity:** medium
- **Where:** `src/WSGM/Core/UnelevatedLauncher.cs:22-32` (`TryStartViaScheduledTask` returns `disposition is Dispatched`); `src/WSGM/Core/ExplorerControl.cs:178-184` (`VerifyAndRepairElevation` falls back to an elevated start); `src/WSGM/Core/Steam.cs:295-308` (`ColdStart` falls back to `AppLauncher.Start`).
- **Coverage:** confirmed by SESSION-037 (two Explorer launch policies; one `ExplorerLauncher` with the tri-state scheduler) and SESSION-055 (Steam cold start never starts a competing Steam after `Unknown`).
- **Plan v2:** B112 (Appendix B also maps the row to B112).
- **Related:** SESSION-037, SESSION-055, U04B-LFA-006, U04B-LFA-012.

### U04B-LFA-003: Steam autostart takeover writes even when the "recorded before the write" record failed to persist

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamAutostartTakeover.cs:77-94` (`Disable`); `src/WSGM/Core/SteamAutostartService.cs:178-202` (`RecordDisabled` catches every exception at 198 and only warns).
- **Coverage:** confirmed by CRIT-001.
- **Plan v2:** B016 (remove the catch so a failed mutation propagates and the item's existing try refuses the write, as `OtherManagers.Record` already does).
- **Related:** CRIT-001, U04B-LFA-009.

### U04B-LFA-004: `RestoreAll` (Steam autostart, other managers) reports success when config.json is unreadable

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamAutostartService.cs:142-172` (`ConfigStore.Load()` at 146); `src/WSGM/Core/OtherManagers.cs:453-475` (457).
- **Coverage:** confirmed by WINSVC-013 and CONFIG-005.
- **Plan v2:** B039 (strict read outcome, fail-closed caller), B095 (`RestoreAll` returns 1 on unreadable config without writing). Appendix B maps the row to B095.
- **Related:** WINSVC-013, CONFIG-005, U04A-LFA-002.

### U04B-LFA-008: Safety-critical Explorer exit, restore and anchor orchestration has no seams and no behavioral tests

- **Severity:** medium
- **Where:** `src/WSGM/Core/ExplorerControl.cs:200-303`; `src/WSGM/Core/ExplorerDesktopHost.cs:21, 92-210, 327-442, 449-507, 515-604`; `src/WSGM/Core/ExplorerShellAnchor.cs:378-535`.
- **Coverage:** confirmed by SESSION-034.
- **Plan v2:** B112 (exit loop and retired-shell state into the host behind `IExplorerNative`, fake-driven sequence tests).
- **Related:** SESSION-034, SESSION-038, U04B-LFA-005, U04B-LFA-011.

### U04B-LFA-009: Registry and config recovery services are untested because they hard-wire ConfigStore, Registry and elevation

- **Severity:** medium (test-quality gap)
- **Where:** `src/WSGM/Core/ShellRegistration.cs:65-154`; `src/WSGM/Core/UacSettings.cs:58-121` (`ApplyDirect`); `src/WSGM/Core/LockScreenSettings.cs:27-79` (`ApplyDirect`); tests `tests/WSGM.Tests/Core/ShellRegistrationTests.cs` (one parser theory) and `WakeSecurityRecoveryTests.cs` (two pure snapshot tests).
- **Problem:** CONFIG-005 cites this row, but only for the lenient read. The test gap is still open at 1329813f. `SteamAutostartTests` and `OtherManagersTests` already drive their services through fakes (`IAutostartSystem`, `IServiceSystem`), and B016, B039 and B095 add the missing cases there. The other three recovery services have no behavioural test: nothing covers capture-once on upgrade, "restore only while the value is still WSGM's", or the optimistic-concurrency clear in `UacSettings.ApplyDirect(false)` (the `Mutate` that clears `PreviousUacSnapshotCaptured` only when the snapshot is unchanged) and its twin in `LockScreenSettings`. These are the rules that put back UAC, lock-on-wake and the shell value at uninstall, and a regression in them leaves a user's Windows modified with no error.
- **Best solution:** give each of the three services an internal entry point that takes what it touches as parameters and keep the public static method as a one-line call with production values. No interface hierarchy, no new state.
  1. After B037, every recovery service already receives the `ConfigStore` instance as a parameter.
  2. `UacSettings` gets internal `Read(RegistryKey hive)` and `ApplyDirect(bool disablePrompts, ConfigStore store, RegistryKey hive)`; the internal `ApplyDirect` calls the internal `Read` with the same hive. `ShellRegistration`'s `ApplyGamingHomeGuard` and `Uninstall` take the `RegistryKey` hive they open their fixed subkey paths under. Production passes `Registry.LocalMachine` or `Registry.CurrentUser`.
  3. `LockScreenSettings.ApplyDirect` takes the three WDC calls it makes (`Capture`, `DisableSignIn`, `Restore`) as delegates, as `SteamAutostartTakeover.Disable` takes its `record` delegate.
  4. The `WindowsPolicyOperation` lock stays in the public `ApplyDirect(bool)` of both services, which takes it and then calls the internal entry point. Tests call the internal one and so never contend on the machine's `Local\WSGM.Policy.*` mutexes.
  5. `ElevationCheck` stays where it is; the tests call the elevated entry points directly.
  This beats a registry-port abstraction because the test guide (`tests/WSGM.Tests/AGENTS.md:10`) already allows a disposable subtree under `HKCU\Software\WSGM.Tests`, so the real `RegistryKey` code runs under test.
- **Tests:** new `UacSettingsTests` and `LockScreenSettingsTests`, and new cases in `ShellRegistrationTests`, all over a temp `UserDataContext` and a per-test `HKCU\Software\WSGM.Tests\<guid>` subtree that a `finally` deletes. Cases: capture happens once and survives a second apply (upgrade); restore writes the snapshot and clears the flag; a snapshot changed by another writer between restore and clear keeps the flag; an unreadable config.json aborts UAC and lock-on-wake changes with no registry or WDC write (already true today through `Mutate` and `LoadForMutation`; the test pins it); the shell value is restored only while it still names WSGM (write `Environment.ProcessPath` as the registered shell), and `StartupToGamingHome` only while it is still 0. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~UacSettings|FullyQualifiedName~LockScreenSettings|FullyQualifiedName~ShellRegistration|FullyQualifiedName~WakeSecurityRecovery"`.
- **Plan v2:** B039 (it already edits `ShellRegistration` and makes UAC and lock-on-wake restore fail-closed; add `UacSettings.cs`, `LockScreenSettings.cs` and the three test files to its list). Depends on B037.
- **Related:** CONFIG-005, WINSVC-014, U04B-LFA-003, U04B-LFA-004; missing bodies U04B-LFA-019, 020, 021, 023 sit in this layer.

### U04B-LFA-010: `IsDesktopShellRunning` relies on `Process.MainModule` on UI and poll paths

- **Severity:** medium (SESSION-035; ledger said low)
- **Where:** `src/WSGM/Core/ExplorerControl.cs:70-89` (82), `:222`; alternative `src/WSGM/Interop/NativeShellProcess.cs:65-81` (`TryGetImagePath`).
- **Coverage:** confirmed by SESSION-035. session.verify.md refutes SESSION-012 and keeps the live probe as the authority for "is the desktop up", so only the probe's implementation changes. CRIT-004 is the same defect class in `DesktopAppProcessBackend`.
- **Plan v2:** B112.
- **Related:** SESSION-035, CRIT-004, SESSION-012 (refuted).

## Low

### U04A-LFA-006: Process startup and App shutdown orchestration have no behavioral tests

- **Severity:** low (SESSION-009; ledger said medium)
- **Where:** `src/WSGM/Program.cs:67-244, 248-303, 454-498, 735-778`; `src/WSGM/App.axaml.cs:41-146`.
- **Coverage:** confirmed by SESSION-009. The ledger's "phase-plan router over injected ports" is not adopted; plan v2 tests the real exit sequence at the runtime seam instead.
- **Plan v2:** B006 (fake lifetime: cleanup runs once for tray Exit, update and restore-shell; startup failure exits 1 after cleanup), B111 (`StartupOptions` precedence, `ApplicationRuntime`, `CrashLoopBreaker` on a temp dir and fake clock).
- **Related:** SESSION-009, SESSION-V-001, PV10-005.

### U04A-LFA-008: Two BootManifestTests re-implement the projection instead of exercising `BootManifestWriter`

- **Severity:** low (CONFIG-036; ledger said medium)
- **Where:** `tests/WSGM.Tests/Core/BootManifestTests.cs:80-88` (`TheManifestProjectsBothSignInChoices` evaluates the writer's pattern inside the test), `:90-99` (`DisarmingTheSignInStartKeepsTheChosenMode` asserts an untouched property); `src/WSGM/Core/BootManifestWriter.cs:22-56`.
- **Coverage:** covered by CONFIG-036 (untested owners, "BootManifestWriter whose tests re-implement the projection") and assigned explicitly in Appendix B. The fix is the projection extraction described under U04A-LFA-031: both tests call `BootManifestWriter.Project` and a temp-root test writes through `BootManifestStore` after B037. A swap of `GameModeBoot` and `DesktopResident` in the writer must fail a test.
- **Plan v2:** B039.
- **Related:** CONFIG-036, U04A-LFA-031.

### U04A-LFA-010: Update metadata and setup downloads have no stall timeout

- **Severity:** low
- **Where:** `src/WSGM/Core/UpdateChecker.cs:324-339` (`GetBytesAsync`: `ReadAsByteArrayAsync` after `ResponseHeadersRead`), `:154-215` (`DownloadAsync` with its own copy loop at 172-190), `:64-65` (`MaxMetadataBytes` 1 MiB, `MaxSetupBytes` 1 GiB), `:77-82` (`CreateHttpClient`, 10 min timeout); `src/WSGM/Core/BoundedHttp.cs:51-90` (`CopyAsync` with the 30 s stall timeout); `src/WSGM/Settings/SettingsViewModel.Updates.cs:112, 136` (callers pass `CancellationToken.None`).
- **Problem:** both reads start after the headers arrive, where `HttpClient.Timeout` no longer applies. A connection that stalls mid-body leaves "Checking for updates" or "Downloading" hanging and the `AsyncRelayCommand` disabled until Settings restarts. A chunked metadata body without Content-Length is also read whole before the size check. The ledger's "BoundedHttp goes unused" is stale: eight call sites in six files use it now (artwork, themes, animations, overlay media), and it has stall and oversize tests. `UpdateChecker` is the only HTTP reader left with a private loop.
- **Best solution:** route both reads through `BoundedHttp`, delete the private loop and drop the two byte caps, which D2 does not list ("accept exactly this list; every other cap is removed").
  1. `GetBytesAsync(http, url, ct)` loses its `limit` parameter and becomes `using var body = await BoundedHttp.ReadAsync(response.Content, int.MaxValue, static () => new InvalidDataException("The answer is too large to read."), ct); return body.ToArray();`. `int.MaxValue` is the `MemoryStream` ceiling, not a WSGM cap. Delete `MaxMetadataBytes`; the bundle read stops passing `BundleManifest.MaxBytes` (U04A-C-001 deletes that constant).
  2. `DownloadAsync` deletes `MaxSetupBytes`, the `Content-Length` pre-check and its own read loop, and calls `BoundedHttp.CopyAsync(response.Content, file, long.MaxValue, <same factory>, ct, stallTimeout, copied: total => { if (length is > 0) progress?.Report((double)total / length.Value); })`. Progress needs one optional `Action<long>? copied = null` parameter after `stallTimeout` on `CopyAsync`, invoked with the running total after each write; the two existing `CopyAsync` callers pass nothing. A callback beats a progress-reporting stream wrapper because it adds one parameter, not a type. The SHA-256 check stays the integrity gate.
  3. Delete the `.partial` file in a `finally` when the copy or the hash check fails (the U04A-LFA-011 cleanup half, same method), through `FileCleanup.TryDelete`.
  4. For the tests, an internal overload `DownloadAsync(http, release, progress, downloadDirectory, stallTimeout, ct)` holds the body; the public method passes `DownloadDirectory` and null. The hash fetch passes the same `stallTimeout` to `GetBytesAsync`, which needs one optional `TimeSpan? stallTimeout = null` on `BoundedHttp.ReadAsync`, forwarded to `CopyAsync` (its existing callers pass nothing).
  The cancellation token from the Settings window comes with SETTINGS-018 in B121; the stall bound must not wait for it.
- **Tests:** in the existing `tests/WSGM.Tests/Core/UpdateCheckerTests.cs` (it covers only parsing, hash format and the state file today), a fake `HttpMessageHandler` and a temp download directory through the internal overload. A hash body that stalls after the headers ends `DownloadAsync` with `IOException` within a 100 ms stall (the `GetBytesAsync` path); a setup body that stalls does the same and leaves no `.partial`; a hash mismatch throws `InvalidDataException` and leaves no `.partial`; a body with `Content-Length` reports progress up to 1.0. `CheckAsync` is not driven here: it catches `IOException` and returns `ReadState()`, which reads the real per-user `update.json` until B037 threads the root. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~UpdateChecker|FullyQualifiedName~BoundedHttp"`.
- **Plan v2:** B027 (it already edits `UpdateChecker.cs` and runs the `UpdateChecker` filter; add `src/WSGM/Core/BoundedHttp.cs` to its files). After `DECISIONS.md` drops the `%ProgramData%\WSGM` DACL and the download move, this fix and its `.partial` cleanup are the functional work B027 keeps; the download stays in `UpdateChecker.DownloadDirectory`.
- **Related:** SETTINGS-018 (caller token, B121), U04A-LFA-011, U04A-LFA-025, U04A-C-001.

### U04A-LFA-011: The setup download leaves `.partial` files behind

- **Severity:** low (reduced to its functional part; the shared-directory half is dropped)
- **Where:** `src/WSGM/Core/UpdateChecker.cs:71` (`DownloadDirectory` under `%ProgramData%\WSGM\Updates`), `:164-215`, `:218-222` (`RunSetup`).
- **Coverage:** the ledger's second half (verifying and then running the setup elevated from a directory with the default `%ProgramData%` DACL, install.md C20 and INSTALL-003) is dropped by maintainer decision (security theater, DECISIONS.md): no protected DACL, and the download stays in `%ProgramData%\WSGM\Updates`, since moving it fixes no functional bug. What remains is functional: a failed copy or hash check leaves a `.partial` file that nothing removes. Step 3 of U04A-LFA-010 deletes it in a `finally` in the same method.
- **Plan v2:** B027 (functional part only, with U04A-LFA-010).
- **Related:** INSTALL-003 (dropped), U04A-LFA-010.

### U04A-LFA-012: `RunSetup` reports success without applying answers and misses several exception types

- **Severity:** low
- **Where:** `src/WSGM/Program.cs:376-450` (`RunSetup`: answers skipped when `config` is null at 397, catch filter at 426, `return 0` at 442-445); `src/WSGM/Core/ConfigStore.cs:1193` (doc names only `InvalidDataException`), `:176-182` (`JsonException`), `:1384` (`TimeoutException`); `src/WSGM/Core/SetupAnswers.cs:124` (`required SetupFeatures Features`), `:229-256` (`Parse`).
- **Problem:** three separate holes, all still present at 1329813f.
  1. With an unreadable config.json, `--setup --answers` logs, skips the answers and exits 0, so setup reports "Profile applied" (this half is INSTALL-V-005).
  2. `ConfigStore.Mutate` throws `JsonException` from `DeserializeConfig` and `TimeoutException` when the mutex is busy. Neither matches the catch filter (`IOException or UnauthorizedAccessException or InvalidDataException`), so the exception escapes `Main` and the one-shot dies with an unhandled-exception exit instead of the logged exit 1.
  3. `{"schemaVersion":1,"features":null}` passes `Parse`, because the source-generated `required` check only proves the key exists. `ApplyTo` and `Describe` then throw `NullReferenceException`, which also escapes the filter.
- **Best solution:** keep `RunSetup` as it is and close the three holes.
  1. Return 1 when `--answers=` was given and `config` is null (INSTALL-V-005, already in B028).
  2. Widen the filter to `IOException or UnauthorizedAccessException or InvalidDataException or JsonException or TimeoutException`. When B039 introduces `ConfigUnavailableException` as the one strict-path failure type, it replaces `JsonException or TimeoutException` here, and B039's `<exception>` docs on the strict path replace the wrong one at `ConfigStore.cs:1193`.
  3. In `SetupAnswers.Parse`, add `{ Features: null } => throw new InvalidDataException("The setup answers have no feature choices.")` to the existing switch. That is a type check, not a limit.
  The ledger's "distinct exit code" is not needed: setup treats any non-zero code as the failure that shows the existing partial-change summary.
- **Tests:** `SetupAnswersTests.MalformedAnswers_AreRefused` gains `{"schemaVersion":1,"features":null}` and `{"schemaVersion":1}`. The `RunSetup` exit code has no seam until B111 moves the one-shots into `StartupCommands`; until then it is a manual check (setup repair over a corrupt config.json shows the failure summary). Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SetupAnswers"`.
- **Plan v2:** B028 (it already edits `RunSetup` and `SetupAnswers.cs`); the exception-type swap follows in B039.
- **Related:** INSTALL-V-005, CONFIG-V-001, U04A-LFA-003, U04A-LFA-024, U04A-C-001 (deletes the 256 KiB `SetupAnswers.MaxBytes` cap, which D2 does not list, in the same `Parse`).

### U04A-LFA-013: The crash-loop breaker is reset on every lifetime exit, including startup failure

- **Severity:** low
- **Where:** `src/WSGM/Program.cs:216-237` (`CrashLoopBreaker.Reset()` runs whenever `StartWithClassicDesktopLifetime` returns in Shell mode), `:817-884`; `src/WSGM/App.axaml.cs:91-95`.
- **Coverage:** the crash-loop half is confirmed by SESSION-002's recommendation and session.md C22 ("only a successfully started, clean exit 0 resets"). The exit-code half is refuted by session.verify.md: `desktop.Shutdown(1)` is forced, `OnShutdownRequested` never runs, and the process exits 1 today. It becomes true once B006 routes exits through the handler, which is why B006 keeps the startup-failure code sticky.
- **Plan v2:** B006 (sticky exit code 1), B111 (reset only when started and exit code 0; `CrashLoopBreaker` an instance over a directory and clock).
- **Related:** SESSION-002 (refuted half), SESSION-V-001, U04A-LFA-020.

### U04A-LFA-014: `ConsoleTool.Run` abandons a timed-out process and reports a definite failure

- **Severity:** low
- **Where:** `src/WSGM/Core/ConsoleTool.cs:62-98` (`Run`), `:237-309` (`RunCapturedAsync` folds not-started, killed and exit codes into -1).
- **Coverage:** confirmed and widened by WINSVC-015.
- **Plan v2:** B095 (one `RunAsync` with a tri-state `ConsoleToolResult` replaces `Run` and `RunCapturedAsync`; a timeout kills the tree and returns Unknown).
- **Related:** WINSVC-015, U04A-LFA-025.

### U04A-LFA-015: `--restore-shell` waits for every WSGM process, not only the resident shell

- **Severity:** low
- **Where:** `src/WSGM/Core/UpdateExitWatcher.cs:174-205` (`RequestResidentShellExit` polls `FindProcessIds("WSGM")`); `src/WSGM/Program.cs:282` (45 s); the shell mutex `Local\WSGM.Shell` at `Program.cs:710`.
- **Coverage:** confirmed by SESSION-005 (wait on the shell mutex instead of process names). Plan v2 lists SESSION-005 as resolved by B111 but the B111 text says "restore-shell wait unchanged". Implement SESSION-005's solution in B111: after `SetEvent`, keep the 200 ms poll and the 45 s timeout, but end it when `Mutex.TryOpenExisting(@"Local\WSGM.Shell", out var m)` returns false, disposing each handle it opens, exactly as `WindowsSetup.ShellRunning` (`src/WSGM.Setup/Engine/WindowsSetup.cs:219`) tests it. `UnauthorizedAccessException` (an elevated shell's default-DACL mutex opened from an unelevated `--restore-shell`) counts as still running. Do not `WaitOne` on the mutex: that needs SYNCHRONIZE on the same object, and an unhandled access exception would escape the restore-shell path before `StartExplorerAndVerify`. The shell never releases the mutex, so it disappears when the shell process exits. The name comes from `SessionProtocolNames` once B031 lands. The path stays free of config, logging and Avalonia. Test: the poll predicate as a `Func<bool> shellAlive` (returns at once when absent, times out when held), filter `FullyQualifiedName~StartupCommands|FullyQualifiedName~UpdateExitWatcher`.
- **Plan v2:** B111.
- **Related:** SESSION-005, PV10-010.

### U04A-LFA-016: Log rotation can block every logging thread for up to 1 s

- **Severity:** low
- **Where:** `src/WSGM/Core/Log.cs:346-357` (`RotateIfLarge` inside `lock (Gate)`), `:261-290` (`WaitOne(1000)`).
- **Coverage:** confirmed by CONFIG-031.
- **Plan v2:** B041 (rotation outside `Gate` with a zero-wait try).
- **Related:** CONFIG-031, PV10-016.

### U04A-LFA-018: `ApplyLogVerbosity` guards a property read that cannot throw

- **Severity:** low (SESSION-006; ledger said nit)
- **Where:** `src/WSGM/Program.cs:685-706` (catch at 698-702).
- **Coverage:** confirmed by SESSION-006: remove the try/catch.
- **Plan v2:** B111.
- **Related:** SESSION-006.

### U04A-LFA-019: A custom tab without an `Id` gets a new random id on every load

- **Severity:** low
- **Where:** `src/WSGM/Core/AppConfig.cs:505` (initializer `Guid.NewGuid()`); `src/WSGM/Core/ConfigStore.cs:507`.
- **Coverage:** confirmed by CONFIG-017, which notes both minting paths.
- **Plan v2:** B068 (deterministic ids from source index and name with a collision ordinal, minted by the migration, no longer by the initializer or `Normalize`).
- **Related:** CONFIG-017, PV10-015.

### U04A-LFA-020: Escalating the shutdown reason during an in-progress shutdown does not affect the running cleanup

- **Severity:** low
- **Where:** `src/WSGM/App.axaml.cs:103-109`; `src/WSGM/Core/ApplicationShutdown.cs:32-51`.
- **Coverage:** confirmed by SESSION-032; session.verify.md adds the session-side instance SESSION-V-005.
- **Plan v2:** B006 (`OnSessionEnding` records the reason even while a shutdown runs; escalation only tightens the deadline), B111 (`ApplicationRuntime` owns the reason with priority).
- **Related:** SESSION-032, SESSION-V-005, U04A-LFA-013.

### U04A-LFA-021: `BootManifestStore.TryLoad` enforces its size bound only once, at open

- **Severity:** low
- **Where:** `src/WSGM/Core/BootManifest.cs:58` (`MaxBytes` 64 KiB), `:88-111` (`TryLoad`: `FileShare.ReadWrite | FileShare.Delete`, one `stream.Length` check, then `ReadToEnd`); caller `src/WSGM.LogonService/SessionLauncher.cs:91` (SYSTEM service).
- **Problem:** the comment claims one handle stops the file from "growing in between", but the share mode lets another process keep writing, and `ReadToEnd` reads to EOF, so the comment is false. The 64 KiB cap is a refuse-only byte bound that D2 does not list (decided: "accept exactly this list; every other cap is removed"), so the cap itself goes rather than being made airtight.
- **Best solution:** delete `MaxBytes`, the `stream.Length` check and the false "one handle" comment. Keep the open with its `FileShare.ReadWrite | FileShare.Delete` share mode (narrowing it would let WSGM's own writer block the sign-in boot) and `TryParse(reader.ReadToEnd())`. Update the `TryLoad` summary to "null when absent, unreadable or unparsable". Nothing is lost: boot.json is written by WSGM for the same user it boots. The service keeps launching the manifest's `ExePath` as today; the INSTALL-001 change that would ignore it is dropped by maintainer decision (security theater, DECISIONS.md).
- **Tests:** in `tests/WSGM.Tests/Core/BootManifestTests.cs`, replace `OversizedFileLoadsAsNull` with a test that a valid manifest padded past 64 KiB with whitespace loads. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BootManifest|FullyQualifiedName~LogonService"`. WSGM and WSGM.LogonService must both build (the file is linked).
- **Plan v2:** B025 (it edits `BootManifest.cs`, linked into both WSGM and the logon service, and fixes its other false comment; its INSTALL-001 `ExePath` part is dropped).
- **Related:** INSTALL-001 (security part dropped), U04A-LFA-008, U04A-C-001.

### U04A-LFA-022: Config lock tests contend on the user's real `Local\WSGM.Config` mutex

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Core/ConfigurationTests.cs:582-766`; `src/WSGM/Core/ConfigStore.cs:25`.
- **Coverage:** confirmed by CONFIG-035 (and config.md C10, build.md BUILD-001).
- **Plan v2:** B037 (the mutex name comes from `UserDataContext`; tests use `Local\WSGM.Tests.Config.<guid>`). Mutex ACLs stay unchanged until the attended CONFIG-V-004 check.
- **Related:** CONFIG-035, BUILD-001, U04A-LFA-004.

### U04A-LFA-023: The gesture default tests bypass the production JSON contract

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Core/ConfigurationTests.cs:1210-1225`.
- **Coverage:** confirmed by CONFIG-035 (deserialize through `ConfigJsonContext`, old-shape JSON fixtures instead of getter tests).
- **Plan v2:** B038.
- **Related:** CONFIG-035.

### U04A-LFA-024: SetupAnswersTests claim to cover every choice but cover five fields and only fresh defaults

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Core/SetupAnswersTests.cs:8-28` (`ExportThenApply_RoundTripsEverySetupChoice` asserts 5 of about 20 fields and applies to a new `AppConfig`), `:92-101` (`MalformedAnswers_AreRefused`).
- **Problem:** still as described at 1329813f. Applying to a fresh config hides the lossy edge-gesture mapping (U04A-LFA-003): the test passes while every update re-enables the edges a user turned off. The malformed cases miss a null or missing `features` and a numeric `startMode`.
- **Best solution:** replace the five-field test with an identity round trip on an existing config. Build an `AppConfig` where every exported field differs from its default and the three edges are mixed (Top on, Left off, Right off). Then serialize it with `ConfigJsonContext`, run `Export`, `ToUtf8Json`, `Parse`, `ApplyTo(config, freshInstall: false)` on that same instance, serialize again and assert the two JSON documents are equal. That checks every field without listing them, so a field added later is covered too. Add `Parse` cases: `features` null (refused after U04A-LFA-012) and missing (already refused by the `required` check), `startMode` 99 (refused) and `startMode` 1 (accepted as `Game`; `SessionStartMode` is `Desktop` = 0, `Game` = 1, and the string-enum converter also reads numbers).
- **Tests:** the identity test must fail on today's code (edges) and pass after B028. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SetupAnswers"`.
- **Plan v2:** B028 (same batch as the `ApplyTo` diff rule, INSTALL-020).
- **Related:** U04A-LFA-003, U04A-LFA-012, INSTALL-020.

### U04A-LFA-025: Several mechanisms in scope have no tests

- **Severity:** low
- **Where:** `src/WSGM/Core/RelayCommand.cs:51-97` (`AsyncRelayCommand`); `src/WSGM/Core/UpdateChecker.cs:116-148, 154-215`; `src/WSGM/Core/UpdateExitWatcher.cs:44` (`RestoreShellEventName`, not pinned), `:86` (`ReportHandoff`); `src/WSGM/Program.cs:817-884` (`CrashLoopBreaker`); `src/WSGM/Core/ConsoleTool.cs:62-98, 237-307`; `src/WSGM/Core/Log.cs:218-245, 253-322`.
- **Problem:** re-checked by grep over `tests/` at 1329813f. `BoundedHttp` is now covered (`BoundedHttpTests`: stall and oversize), so that instance is fixed. The rest still has no test. For the instances that have no owning batch, nothing stops a regression in the re-entrancy guard that keeps an update button from starting two downloads, or in the cross-version name setup uses to ask a resident shell to restore Explorer.
- **Best solution:** test each instance in the batch that already reshapes it, and add the two orphans to the batches that use them. No new seams beyond the ones those batches introduce.
  - `Log.Change` and rotation: CONFIG-034 in B041 (`LogFile` instance on a temp path).
  - `ConsoleTool.Run`, `RunCapturedAsync` and the `RunUntilAsync` deadline and failure branches: WINSVC-015 in B095 (one `RunAsync`, tested through its process port).
  - `CrashLoopBreaker`: B111 (instance over a directory and clock).
  - `ReportHandoff`: B111 (`ApplicationRuntime` reports the handoff exactly once).
  - `RestoreShellEventName`: B031 moves every exit event name into `SessionProtocolNames.cs` and `SetupShutdownContractTests` pins each one; include `Local\WSGM.ExitForRestoreShell`.
  - `UpdateChecker.DownloadAsync` (and the shared `GetBytesAsync` read through its hash fetch): the fake-handler tests of U04A-LFA-010 in B027. `CheckAsync` writes and reads the real per-user `update.json`, so its test waits until B037 threads the data root; add it in B039, which already lists `UpdateChecker.cs`.
  - `AsyncRelayCommand`: three tests added to the existing `tests/WSGM.Tests/Core/RelayCommandTests.cs`, which covers only the synchronous `RelayCommand` types. A second `Execute` while the first awaits a `TaskCompletionSource` does not run the delegate. `CanExecute` is false while running and `CanExecuteChanged` fires twice. A throwing delegate is contained and `CanExecute` returns true afterwards. It goes in B121, which reworks the update commands.
- **Tests:** as listed per batch; for the orphan, `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RelayCommand"`.
- **Plan v2:** B027, B031, B039, B041, B095, B111, B121.
- **Related:** CONFIG-034, CONFIG-036, WINSVC-015, SESSION-009, U04A-LFA-010, U04A-LFA-014.

### U04A-C-001: Setup answers and bundle.json refuse documents by byte caps that D2 does not list

- **Severity:** low
- **Where:** `src/WSGM/Core/SetupAnswers.cs:96-97` (`MaxBytes` 256 KiB), `:228-234` (`Parse`); `src/WSGM.Install/BundleManifest.cs:153-154` (`MaxBytes` 1 MiB), `:183-189` (`Parse`), `:213-230` (`TryRead`); readers `src/WSGM/Core/UpdateChecker.cs:128-131`, `src/WSGM.Setup/SetupPayload.cs:50, 66`, `src/WSGM.Setup/UI/SetupViewModel.cs:317`, `src/WSGM/Settings/SettingsViewModel.Plugins.cs:136`.
- **Problem:** both constants are refuse-only byte caps, and plan v2 D2 ("accept exactly this list; every other cap in the reviews is removed") lists neither. No review owns them: U04A-LFA-012 only mentioned the answers cap, and no domain finding names `BundleManifest.MaxBytes`. Both documents come from WSGM itself (the answers from `--export-setup-answers` and setup's own page, bundle.json from the release build inside the signed setup), so the caps protect nothing. A legitimate bundle.json past 1 MiB, for example many community plugins with their outdated-build entries, would make setup refuse its own payload and every update check fail.
- **Best solution:** delete both constants and the size half of each check. `SetupAnswers.Parse` and `BundleManifest.Parse` keep `utf8Json.Length is 0` with the message "empty"; `BundleManifest.TryRead` becomes `File.Exists(path) ? Parse(File.ReadAllBytes(path)) : null`. Drop "too large" from both `<exception>` docs. The update check's bundle download stops passing `BundleManifest.MaxBytes` with U04A-LFA-010.
- **Tests:** `SetupAnswersTests`: a valid answers document padded past 256 KiB with whitespace parses, an empty one is refused. `tests/WSGM.Tests/Install/PluginOffersTests.cs`: a valid bundle.json padded past 1 MiB parses. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SetupAnswers|FullyQualifiedName~PluginOffers"`. WSGM and WSGM.Setup must both build.
- **Plan v2:** B028 (it edits `SetupAnswers.cs`; add `src/WSGM.Install/BundleManifest.cs` to its files). It runs after B027, which already removes the bundle download limit from `UpdateChecker`.
- **Related:** U04A-LFA-010, U04A-LFA-012, U04A-LFA-021.

### U04A-LFA-028: The Core config model depends on Themes, and its normalizers log

- **Severity:** low (CONFIG-013 and SETTINGS-017; ledger said nit)
- **Where:** `src/WSGM/Core/AppConfig.cs:6, 753` (`AccentPalette.DefaultAccent`); `src/WSGM/Core/ConfigStore.cs:576-578, 621-623` (`Log.Warn` in `Normalize`); `src/WSGM/Core/JsonRead.cs:28` (`ThemeManifestException`).
- **Coverage:** confirmed by CONFIG-013 (normalizers return diagnostics), CONFIG-041 (housekeeping, Themes dependency) and SETTINGS-017 (default accent constant moves to Core, duplicates in `Palette.axaml`, `AppearancePage.axaml.cs`, `SettingsViewModel.Plugins.cs` reference it, with an agreement test).
- **Plan v2:** B038 (diagnostics, Core exceptions), B120 (accent constant).
- **Related:** CONFIG-013, CONFIG-041, SETTINGS-017.

### U04B-LFA-005: Anchor setup failure path can throw from `StopFailedChildAsync`, escaping `StartAsync` and leaking handles

- **Severity:** low (SESSION-039; ledger said medium)
- **Where:** `src/WSGM/Core/ExplorerShellAnchor.cs:228-242`, `:735-755` (unguarded `writer.DisposeAsync()` at 749); contrast `DisposeQuietly` at 117-122.
- **Coverage:** confirmed by SESSION-039.
- **Plan v2:** B112 (`StopFailedChildAsync` disposes each resource independently).
- **Related:** SESSION-039, U04B-LFA-008.

### U04B-LFA-006: Unowned fire-and-forget Explorer elevation repair can race later session transitions

- **Severity:** low (ledger said medium, hypothesis; re-checked here)
- **Where:** `src/WSGM/Core/ExplorerControl.cs:91-131` (`StartExplorerCore`, `Task.Run(VerifyAndRepairElevation)` at 124), `:133-190`; only caller of the non-waiting variant `src/WSGM/Program.cs:764` (`Panic`).
- **Coverage:** the race the ledger describes cannot happen at 1329813f. The only caller that queues the repair is `Panic`, and the process exits right after it, so no later Game Mode entry exists in that process; the queued repair is simply torn down. What remains is that the non-waiting variant does nothing useful. SESSION-037 replaces both `StartExplorer` variants with one `ExplorerLauncher` that terminal paths call synchronously, which deletes the `Task.Run`. SESSION-019 lists the same `Task.Run` among unowned work.
- **Plan v2:** B112 (one launcher, terminal paths verify synchronously); B140 (owners refuse late work once stopping; decided: D1, safety-first ordered steps under one deadline, without the B3 percentage cutoffs or preliminary drain).
- **Related:** SESSION-037, SESSION-019, U04B-LFA-001.

### U04B-LFA-007: High-integrity Explorer (UAC off or built-in Administrator) blocks Game Mode entry and is classified a failed desktop; undocumented

- **Severity:** low (SESSION-042; ledger said medium)
- **Where:** `src/WSGM/Core/ExplorerShellPolicy.cs:71-76, 115-131`; `src/WSGM/Core/ExplorerDesktopHost.cs:110-116, 214-217`; `docs/boot-and-shell.md:228-231, 257-259`.
- **Coverage:** confirmed by SESSION-042. Plan v2 keeps the refusal (policy decided) and documents the limitation.
- **Plan v2:** B112 (refusal kept, limitation documented), B177 (docs pass).
- **Related:** SESSION-042; missing body U04B-LFA-041 (`EnableLua` read but unused, `UacSettings.cs:45, 146, 159`).

### U04B-LFA-011: `ExitExplorerAndWait` can throw despite its bool contract

- **Severity:** low
- **Where:** `src/WSGM/Core/ExplorerControl.cs:218-225`; caller guard `src/WSGM/Core/ExplorerDesktopHost.cs:250-258`.
- **Coverage:** confirmed by SESSION-036.
- **Plan v2:** B112 (never-throw exit contract; "exit not confirmed" is logged with its cause).
- **Related:** SESSION-036, U04B-LFA-008.

### U04B-LFA-012: Budget-closed cleanup leaks registered `WSGM_StartUnelevated_*` tasks; no sweep exists; a test locks the behavior in

- **Severity:** low (body truncated in the ledger; title, location and direction survive)
- **Where:** `src/WSGM/Core/UnelevatedLauncher.cs:136-226` (`RunScheduledTaskSequenceAsync`; the `finally` at 197-225 skips `/Delete` and only logs at 203 when the token is cancelled or the shared deadline has passed); tests `tests/WSGM.Tests/Core/UnelevatedLauncherTests.cs:147` (`ScheduledTaskDeadlineClosesAfterCreate_SkipsRunAndCleanup`) and `:182` (`ScheduledTaskCancellationAfterCreate_SkipsRunAndCleanup`).
- **Problem:** once `/Create` succeeded or ended Unknown, a deadline that closes, or a cancellation that arrives before cleanup, leaves a registered `WSGM_StartUnelevated_<pid>-<rand>` task in the user's Task Scheduler library. Nothing ever deletes it: there is no sweep and the name is random. Slow boots and resumes, the very cases that hit the deadline, add one more stale task each time. Two tests assert that the cleanup is skipped.
- **Best solution:** in the `finally`, when `taskMayExist` is true, always make one `/Delete /TN <name> /F` attempt. While the shared budget is open, keep today's call (shared deadline, caller token). Once it is closed or the caller cancelled, pass its own short deadline (`utcNow() + 5 s`) and `CancellationToken.None` instead. Log a failure and never retry. This is cleanup, not a retry of an uncertain write: `/Delete` is idempotent and does not stop an instance that `/Run` already started. Do not add a persistent cleanup record, a startup sweep or a new disposition state (plan v2 C12). The outcome of the `try` is unchanged: a return value stays as it was and an `OperationCanceledException` still propagates after the cleanup.
- **Tests:** rewrite the two tests. `ScheduledTaskDeadlineClosesAfterCreate_...`: the fake `runCommand` sees `/Create` then exactly one `/Delete`, whose deadline is later than the shared one, and the result is still `NotDispatched`. `ScheduledTaskCancellationAfterCreate_...`: `OperationCanceledException` still propagates, and the fake sees `/Create` then exactly one `/Delete` whose token is not cancelled. Rename both from `..._SkipsRunAndCleanup` to `..._SkipsRunAndStillDeletes`. Keep `ScheduledTaskRunFailure_DeletesWithinTheSameAbsoluteDeadline` as is. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~UnelevatedLauncher"`.
- **Plan v2:** B029 (named in its steps and Resolves). With the admin-only XML folder dropped by maintainer decision (security theater, DECISIONS.md), this cleanup and INSTALL-009 are what B029 keeps; the XML stays where it is written today.
- **Related:** U04B-LFA-002 (dropped), INSTALL-008 (dropped), INSTALL-009.

## Nit

### U04A-LFA-029: Duplicate `<summary>` on `AppConfig.Splash`

- **Severity:** nit
- **Where:** `src/WSGM/Core/AppConfig.cs:738-746`.
- **Coverage:** confirmed by CONFIG-041.
- **Plan v2:** B038.
- **Related:** CONFIG-041.

### U04A-LFA-030: The app.manifest comment contradicts its value

- **Severity:** nit
- **Where:** `src/WSGM/app.manifest:3-8` (comment "Deliberately NOT the product version", value `version="2.1.0.0"`); `src/WSGM/WSGM.csproj:13` (`<Version>2.1.0</Version>`).
- **Problem:** still present, but the comment is what is wrong, not the value. The comment (2026-08-31, commit 1432accd) says the identity is deliberately not the product version. `eng/check-version-sync.ps1` was added later (2026-09-16, f0a512b3, kept by a27e049b on 2026-10-01): it requires the identity to equal the numeric core of the `WSGM.csproj` version padded to four parts, and it runs in `eng/verify.ps1:103` and `build.ps1:22`. `eng/stamp-version.ps1` stamps the same value from a release tag. The comment tells the next reader the opposite of what the gate enforces.
- **Best solution:** rewrite the comment only and keep `version="2.1.0.0"`: "Side-by-side identity; Windows resolves nothing through it. It carries the numeric core of the WSGM.csproj version padded to four parts, because SxS takes four numbers. eng/stamp-version.ps1 stamps it and eng/check-version-sync.ps1 checks it." Do not set an inert `1.0.0.0`: `check-version-sync.ps1` would then fail `verify.ps1` and `build.ps1`. Decoupling the identity (1.0.0.0 like the other four app manifests, deleting the manifest half of both scripts) would also work, but it reverses a check the maintainer added after the comment, so it is not done without being asked.
- **Tests:** `.\eng\check-version-sync.ps1` passes; no build change.
- **Plan v2:** B032 (stale comments and build hygiene).
- **Related:** none.

### U04A-LFA-031: `BootManifestWriter.WriteSignInDisabled` mutates its argument

- **Severity:** nit
- **Where:** `src/WSGM/Core/BootManifestWriter.cs:22-56` (`WriteSignInDisabled` sets `config.StartAtSignIn = false` at 54); callers `src/WSGM/Program.cs:263, 463`.
- **Problem:** still present. It is harmless today: in `DisarmCrashLoop` the mutated `startupConfig` reaches only `RestoreDisplayScalesBestEffort`, which ignores the flag. But a disarm silently changes the caller's startup config object, and the writer has no pure projection to test (U04A-LFA-008).
- **Best solution:** extract `internal static BootManifest Project(AppConfig config, bool startAtSignIn, bool requiresElevatedShell, string exePath)`. It holds the two pattern checks, `ElevationPolicy.WantsElevation` and the exe path. `WriteCurrent(config)` saves `Project(config, config.StartAtSignIn, Steam.RequiresElevatedShell, Installer.InstalledExePath)` (renamed per U04A-LFA-035). `WriteSignInDisabled(config)` saves the same projection with `startAtSignIn: false` and never touches `config`. Logging and the bool result stay as they are.
- **Tests:** `BootManifestTests` call `Project` for the four sign-in and mode rows, and add a test that `WriteSignInDisabled` leaves `config.StartAtSignIn` true (temp root after B037). Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BootManifest"`.
- **Plan v2:** B039 (named in its steps).
- **Related:** U04A-LFA-008, CONFIG-036, CONFIG-003.

### U04A-LFA-033: Credits has a test-mutable static, and a drift claim nothing enforces

- **Severity:** nit
- **Where:** `src/WSGM/Core/Credits.cs:12-16` (summary: "the two surfaces and the README cannot drift apart"), `:56-58` (`VersionText { get; internal set; }`); `tests/WSGM.UiTests/Infrastructure/TestApplication.cs:25`; `README.md:310-321` (hand-written credits prose).
- **Problem:** the README credits are prose written by hand and nothing ties them to `Credits.People` or `Credits.Projects`, so the drift claim is false. The settable `VersionText` is documented in its remarks as the UI-test seam for baselines that cannot follow the revision. It is deliberate and has one writer.
- **Best solution:** reword the summary to what holds: one list feeds the Settings and overlay About pages, so those two cannot drift; the README credits are kept by hand. Leave `VersionText` as it is. A README-parsing test would add a check for prose the maintainer edits freely, which is more mechanism than a comment fix.
- **Tests:** none.
- **Plan v2:** B032 (stale comments).
- **Related:** none.

### U04A-LFA-034: Test placement and test-guide drift

- **Severity:** nit
- **Where:** `tests/WSGM.Tests/Core/CommandRouteTests.cs` (15 tests of `ProtocolHandler`, `CommandShortcut` and `ShortcutRoute`, which live in `src/WSGM/Core/Library/`); `tests/WSGM.Tests/Core/ConfigurationTests.cs:552-565` (a `SplashTheme` import test); `tests/WSGM.Tests/Core/LogLevelTests.cs:10` (`[Collection("log-level")]`, redundant with `AssemblyInfo.cs:1` `DisableTestParallelization = true`); `tests/WSGM.Tests/AGENTS.md:3` ("application, launcher, and logon service", while `WSGM.Tests.csproj:26-32` also references Setup, PackagedLaunch, Plugin.Ir, the Device SDK and the toolkit).
- **Problem:** all four still hold. They break the guide's own rules ("test files sit in the folder of the production type", "tests for one type share one class"), and the guide's first line misdescribes the project.
- **Best solution:** move, do not rewrite.
  - Split `CommandRouteTests` into `tests/WSGM.Tests/Core/Library/ProtocolHandlerTests.cs` and `ShortcutRouteTests.cs` (named after `src/WSGM/Core/Library/ShortcutRoute.cs`, which declares both `ShortcutRoute` and `CommandShortcut`), with the shared fixtures as private statics in each.
  - The `SplashTheme` test moves to `SplashThemeTests` with CONFIG-035 in B038.
  - The redundant `[Collection]` goes when B041 renames `LogLevelTests` to `LogFileTests`.
  - The AGENTS.md line becomes "deterministic xUnit coverage for WSGM and the projects it references (see WSGM.Tests.csproj)". It is a separate diff for sign-off, edited in AGENTS.md only, followed by `eng/check-agent-guidance.ps1`.
- **Tests:** the moved tests keep their names; filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ProtocolHandler|FullyQualifiedName~ShortcutRoute"`.
- **Plan v2:** B176 (test helpers and `tests/WSGM.Tests/AGENTS.md`), with the splash move in B038 and the attribute in B041.
- **Related:** CONFIG-035, PV10-026, STEAMHOST (an analog placement nit).

### U04A-LFA-035: Misleading names and comments in Program and Installer

- **Severity:** nit
- **Where:** `src/WSGM/Program.cs:384-386` ("Read before the load, which creates the file"); `src/WSGM/Core/Installer.cs:10-22` (`InstalledExePath` is the running image path; callers `BootManifestWriter.cs:32`, `ShellRegistration.cs:170`, `Program.cs:433, 640`).
- **Problem:** both still hold. `LoadForMutation` never creates config.json (only `Mutate`'s save does), so the comment gives a wrong reason for an ordering that is still right: the file must be checked before the answers are applied. `InstalledExePath` names the running copy, which the property's own remarks admit differs under a dev deploy, so a reader of `BootManifestWriter` or `ShellRegistration` assumes the install folder where the dev-deploy path is recorded.
- **Best solution:** rewrite the comment to the real reason ("checked before the answers are applied, because applying them saves config.json; existence is the only way to tell a first install from a repair or upgrade"). Rename `InstalledExePath` to `RunningExePath` at its four call sites. Keep the class name `Installer`: its summary already states what it holds, and a class rename would only add churn.
- **Tests:** build only.
- **Plan v2:** B028 (it edits `RunSetup`). The rename also touches `BootManifestWriter.cs`, `ShellRegistration.cs` and `Program.cs`, which B039 edits; the batch dependencies do not order B028 against B039, so whichever lands second rebases onto the new name.
- **Related:** U04A-LFA-031, INSTALL-001 (security part dropped).

## Missing bodies

U04B-LFA-013 to U04B-LFA-049 (37 ids) have no saved title, location or claim. The recovered header of `U04B-LFA.review.md` gives their severity bands: 013 to 034 are low and 035 to 049 are nit (49 findings in total: 9 medium, 25 low, 15 nit). The unit's scope is the 52 files in `unit-plan.md` under U04B-LFA. In `src/WSGM/Core` those are `ElevationCheck`, `ElevationPolicy`, `SelfElevation`, `UnelevatedLauncher`, `ExplorerControl`, `ExplorerDesktopHost`, `ExplorerExitPolicy`, `ExplorerShellAnchor`, `ExplorerShellPolicy`, `ShellRegistration`, `UacSettings`, `LockScreenSettings`, `AutostartSystem`, `SteamAutostart`, `SteamAutostartService`, `SteamAutostartTakeover`, `OtherManagers`, `KnownStartupApps`, `ScheduledTaskXml`, `RegistryValueSnapshot`, `WindowsPolicyOperation`, `WindowsCommandLine`, `InputDesktop`, `TrayProtocol`, `WindowFinder`, `AppLauncher`, `DesktopAppLifecycle` and `DesktopAppProcessBackend`. In `src/WSGM/Interop` they are `Kernel32`, `Win32Common`, `NativeMethods`, `NativeShellProcess` (and `.TokenLaunch`), `ParentProcessStart`, `ShellLink`, `NativeAuthenticode` and `NativePathIdentity`. The rest are 13 test classes in `tests/WSGM.Tests/Core`. Do not invent claims for these ids. Plan v2 assigns all 37 to B024, a read-only closure that writes one disposition per id into `claude/install-closure.md` and appends any required fix as a new batch before B030. Per `DECISIONS.md`, B024 records any id whose only defect is same-user security hardening (privilege boundaries, ACLs, staging folders, sender or integrity checks) as no-change with the reason "Dropped by maintainer decision (security theater, DECISIONS.md)" and keeps only functional fixes.

What survives is only the cross-references inside the saved bodies, which point B024 at the right files:

- **U04B-LFA-013** (low): cited as related by 001 and 002, so it sits in scheduled-task de-elevation (`UnelevatedLauncher`, `ScheduledTaskXml`). Re-covered by INSTALL-009 and U04B-LFA-012 (B029) and SESSION-037 (B112); INSTALL-008's XML staging is dropped by maintainer decision.
- **U04B-LFA-014** (low): nothing survives. Re-covered by the session (SESSION-034 to 042) and install (INSTALL-009; INSTALL-008 dropped) reviews of the same files.
- **U04B-LFA-015** (low): cited by 005, so it sits in `ExplorerShellAnchor`. Re-covered by SESSION-038, SESSION-039, SESSION-041 (B112) and SESSION-047 with INSTALL-027 (anchor event names in one linked file, B031).
- **U04B-LFA-016, U04B-LFA-017, U04B-LFA-018** (low): nothing survives. Same re-cover as 014.
- **U04B-LFA-019, U04B-LFA-020, U04B-LFA-021, U04B-LFA-023** (low): cited by 009 as defects in the registry and config recovery services (`ShellRegistration`, `UacSettings`, `LockScreenSettings`, `SteamAutostartService`, `OtherManagers`). Re-covered by CONFIG-005 (B039), WINSVC-013 and WINSVC-014 (B095), WINSVC-034 (refuted: the no-snapshot restore writes the secure default on purpose), INSTALL-V-004 (B028), CRIT-001 (B016) and the U04B-LFA-009 section above.
- **U04B-LFA-022, U04B-LFA-024, U04B-LFA-025** (low): nothing survives. Same re-cover as 014.
- **U04B-LFA-026** (low): cited by 003, so it sits in Steam autostart takeover and recording. Re-covered by CRIT-001 (B016) and INSTALL-V-004 (B028). The critic notes that no domain review owns `SteamAutostart.cs`, `SteamAutostartTakeover.cs` or `KnownStartupApps.cs`, so B024 must read those in full.
- **U04B-LFA-027, U04B-LFA-028, U04B-LFA-029, U04B-LFA-030, U04B-LFA-031, U04B-LFA-032, U04B-LFA-033** (low): nothing survives. Same re-cover as 014; `WindowsPolicyOperation.cs` is unowned per the critic and B024 reads it.
- **U04B-LFA-034** (low): cited by 010, so it sits in process identity through `MainModule` or image path. Re-covered by SESSION-035 and CRIT-004 (`DesktopAppProcessBackend`, B112).
- **U04B-LFA-035, U04B-LFA-036, U04B-LFA-037, U04B-LFA-038, U04B-LFA-039** (nit): nothing survives. Interop nits are partly re-covered by WINSVC-040 (dead `NativeMethods` declarations, B094), WINSVC-033 (`NativeAuthenticode` online revocation, B100) and INSTALL-026 and 033 (duplicate P/Invokes, B031).
- **U04B-LFA-040** (nit): cited by 008, so it sits in the Explorer orchestration. Re-covered by SESSION-038, SESSION-040 and SESSION-041 (B112).
- **U04B-LFA-041** (nit): cited by 007 as "`UacSettings` reads `EnableLua` but nothing uses it". At 1329813f, `UacSettings.cs:45` reads `EnableLUA` into `UacState.EnableLua` (146, 159) and grep finds no consumer. B024 records the disposition: delete the property, or use it in the SESSION-042 refusal message if B112 wants to name "UAC is off" as the reason.
- **U04B-LFA-042, U04B-LFA-043, U04B-LFA-044, U04B-LFA-045, U04B-LFA-046, U04B-LFA-047, U04B-LFA-048** (nit): nothing survives. Same re-cover as 035.
- **U04B-LFA-049** (nit): cited by 010, the same process-identity area as 034. Re-covered by SESSION-035 and CRIT-004 (B112).

## Refuted or no-change

- **U04B-LFA-002** (elevated WSGM registers a task from XML staged in a user-writable directory; was a medium section, confirmed and extended to WSGM.Launch by INSTALL-008, planned in B029): Dropped by maintainer decision (security theater, DECISIONS.md). `UnelevatedLauncher` and `WSGM.Launch/ScheduledTaskLauncher.cs` keep writing the XML where they do today. The functional work in the same files stays in B029: the budget-closed `/Delete` of U04B-LFA-012 and INSTALL-009.
- **U04A-LFA-027** (LoadingIndicators compiles through a tracked git symlink): no change. Plan v2 Appendix A records BUILD-031 as "keep the tracked symlink until a Link-based include is proven to keep the avares paths", and build.verify.md calls the ledger's "accepted build simplification" unproven. The link resolves on the maintainer's machine; only the prerequisite note may go into the build docs with B176.
- **U04A-LFA-026** (small duplicate helpers for detached-task observation and best-effort deletion; was a nit section): no change. The observation half is refuted by config.verify.md: `Log.Observe` logs faults with the operation name, while `TaskFaults.ObserveFaults` deliberately only swallows faults of cancellation, disposal and wait-abandoned work, and B041 keeps both; `ObserveLateCleanup` goes with `ApplicationShutdown.cs` in B111, and the silent drop at `GpuCoordinator.cs:671` belongs to SDK-024. The deletion half is not a removable duplicate: `AtomicFile.cs` is a linked source compiled into `WSGM.LogonService` (`src/WSGM.LogonService/WSGM.LogonService.csproj:25`, and its header says it stays free of WSGM-only types), while `FileCleanup.cs` is not linked there. Calling `FileCleanup.TryDelete` from `AtomicFile` would break the logon service build or add a second linked file to save ten lines.
- **U04A-LFA-032** (`ExceptionList.Combine` on an empty list yields an empty `AggregateException`): no change. All four callers guard the count first (`DeviceCoordinator.cs:864` with `Count > 0`, `DeviceCoordinator.cs:1259` and `:1593` behind `!Verified`, `ShellSession.Shutdown.cs:645` behind `Count == 0 ? null`). Adding a guard would be new mechanism with no defect behind it.
