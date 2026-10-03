# Install (setup, launch, logon service, packaged launch) findings

Scope: `src/WSGM.Install`, `src/WSGM.Setup`, `src/WSGM.Launch`, `src/WSGM.LogonService`, `src/WSGM.PackagedLaunch`, the linked sources they compile (`BootManifest.cs`, `ScheduledTaskXml.cs`, `Win32Common.cs`) and the WSGM one-shots setup calls (`Program.RunSetup`, `Program.ExportSetupAnswers`, `SetupAnswers`). Baseline is `master` 1329813f. Line numbers were re-checked against that baseline where this file gives them; the review's own numbers are sometimes from a concatenated listing (install.verify.md lists the wrong ones), so anchor every edit by symbol.

Inputs folded in: the install review (`_plan/refactor-2.1/review/install.md`, which stops inside INSTALL-010 and never wrote the bodies of 26 further ids), its adversarial verification (`install.verify.md`: corrections, one refuted sub-claim, missed findings INSTALL-V-001 to V-007), the critic (GAP-03 in crosscutting), the maintainer decisions of 2026-10-03 (`_plan/refactor-2.1/DECISIONS.md`, which drop every security-hardening item and D14) and plan v2. This file did not exist when the solution check started; the checker wrote it from those inputs and re-read the cited code.

Counts: 19 findings written up (0 high, 5 medium, 10 low, 4 nit; INSTALL-C-001 added by the solution check), 7 ids in the refuted or no-change list (six of them security items the maintainer dropped), and 29 review ids that have no body anywhere and are retired in the last section. The review's plan-claim checks C1 to C23 are not findings; their substance lives below (C1 and C8 in INSTALL-017, C2 and C14 in INSTALL-006, C9 in INSTALL-005, C10 and C17 in INSTALL-020, C13 in INSTALL-027, C15 in INSTALL-007, C16 in INSTALL-032).

Plan v2 batches for this area: B007, B024, B025, B028, B030, B031 (install domain), B068 (config migration constraint from INSTALL-006), B173 (INSTALL-032). Per DECISIONS.md, B026 is removed whole, B027 keeps only the U04A-LFA-011 download move (owned by ledger-u04), B029 keeps only U04B-LFA-012 (owned by ledger-u04), B025 drops INSTALL-001 and INSTALL-V-001, and B031 drops INSTALL-010. Matrix row M01-49 loses its "starts only the installed WSGM" clause and keeps the dirty-exit Explorer fallback.

## Medium

### INSTALL-006: A failure after "Applying your profile" still reports "Nothing was changed"

- **Severity:** medium
- **Where:** `src/WSGM.Setup/Engine/SetupEngine.cs` `PlanInstall` (fatal "Applying your profile" at 324-325, then fatal `RegisterServiceStep` at 326), `RegisterServiceStep` (462-467), `RollBack` (1023-1069); `src/WSGM.Setup/UI/SetupViewModel.cs` `ShowSummary` (the `!ok` branch, "Nothing was changed"); `src/WSGM.LogonService/ServiceInstaller.cs` (`binPath` is `Environment.ProcessPath`, so the service runs from `App\WSGM.LogonService.exe`).
- **Problem:** `--setup --answers` writes config.json and may run the Steam autostart takeover and the other-manager disable. If the next fatal step, service registration, fails, setup rolls the files back and tells the user nothing changed, while config.json and those Windows changes stay. The review's remedy (register the service before the answers) is unsafe: `--install` starts the service from `App\WSGM.LogonService.exe`, so if the answers then fail, `RollBack` must delete or swap an `App` folder whose service executable is running and fails with an IOException, leaving a pending journal that blocks the next sign-in. The verifier's fallback (reuse the legacy-removed "partly changed" summary) does not fit: that text names the removed 1.0 version and would need new wording.
- **Best solution:** make "Registering the sign-in service" non-fatal in `PlanInstall` (`RegisterServiceStep(..., false)`), and keep today's order. The answers are then the last fatal step, so "Nothing was changed" is true for every fatal failure before them, and a registration failure ends on the existing "Done, with a problem" summary with the step's existing note ("The sign-in service could not be registered; see setup.log."). This removes a rollback path rather than adding one, and needs no new text. Also, at the top of `RollBack`, when `_shutdownApplied` is true, call `_runtime.StopService()` before restoring files: the only remaining rollback after the service started is a failed `Commit`, and it has the same running-executable problem. INSTALL-V-005 makes the answers step itself fail before any change when config.json is unreadable. Binding constraint for B068 (already in its spec): `--export-setup-answers` migrates in memory and never writes, because the staged new WSGM runs it before the user confirms.
- **Tests:** `SetupShutdownContractTests` already drives `SetupEngine` over the recording runtime; add one test that a `RollBack` after `StopRuntime` records `StopService` before any file restore (fake runtime, temp paths from INSTALL-017). Plan composition: `PlanInstall` marks the registration step non-fatal (test over a temp payload once INSTALL-017 lands). Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup"`. Manual: M01-39 update and repair.
- **Plan v2:** B028 (replace "register the sign-in service before Applying your profile" and the "partly changed summary" sentence with this solution).
- **Related:** INSTALL-V-005, INSTALL-020, B068, C2, C14.

### INSTALL-015: USB/IP presence and removal match any uninstall entry containing "USBip"

- **Severity:** medium
- **Where:** `src/WSGM.Setup/Engine/Registration.cs` `FindUninstallCommand` (142-165, `DisplayName.Contains`, first hit across both views), `InstalledComponents.UsbipPresent` (271-276); `src/WSGM.Setup/Engine/SetupEngine.cs` `RemoveComponent(step, "USBip")` (PlanUninstall, 368-375); usbip-win2's installer `_ref/usbip-win2/userspace/innosetup/setup.iss` (`AppId={{#AppGUID}`, `AppGUID "{199505b0-b93d-4521-a8c7-897818e0205a}"`).
- **Problem:** Microsoft's `usbipd-win` (common on WSL machines) has a DisplayName containing "usbip". `UsbipPresent` then reports the driver present (the drivers page heads-up under-reports), and when `components.json` says setup installed USB/IP, uninstall's "Removing the USB/IP driver" runs usbipd-win's MSI uninstall string with Inno switches appended, waits 180 s for an entry that never disappears and reports failure, or removes the wrong product. A DisplayName prefix is not enough: `usbipd-win` also starts with "usbip".
- **Best solution:** identify usbip-win2 by its Inno uninstall key, which its AppId fixes: `{199505b0-b93d-4521-a8c7-897818e0205a}_is1`. Add `Registration.FindUninstallCommandByKey(string keyName)` that opens `Uninstall\{keyName}` in the 64- and 32-bit views and returns its `UninstallString`; `UsbipPresent` and the uninstall step use it with one `UsbipUninstallKey` constant (comment cites the setup.iss AppId). The `%ProgramFiles%\USBip\usbip.exe` fallback in `UsbipPresent` stays. HidHide keeps the DisplayName lookup (no known collision); `RemoveComponent` takes the lookup as a `Func<string?>` so both cases share the wait loop. `Install-UsbipDriver.ps1`'s `-like 'USBip*'` only locates a folder and falls back to `%ProgramFiles%\USBip` when `libusbip.dll` is absent, so it needs no change.
- **Tests:** `RegistrationTests`: a pure `Registration.IsUsbipUninstallKey(string)` (used by the lookup) accepts the usbip-win2 key and rejects `usbipd-win`'s and an arbitrary GUID. Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Registration"`.
- **Plan v2:** B030 (replace "exact uninstall key or the DisplayName prefix" with the key).
- **Related:** INSTALL-047, C3.

### INSTALL-020: Update and repair rewrite the three edge-gesture switches from one collapsed answer

- **Severity:** medium (violates Setup/AGENTS.md: a repair or update must not rewrite a switch the user chose)
- **Where:** `src/WSGM/Core/SetupAnswers.cs` `Export` (`EdgeGestures = TopEdge || LeftEdgeSteamMenu || RightEdgeSteamQuickAccess`) and `ApplyTo` (writes `Features.EdgeGestures` into all three); `src/WSGM.Setup/UI/ProfilePage.cs` `WriteTo`.
- **Problem:** a user with only the top edge on exports `EdgeGestures = true`; the update applies it and turns the left and right swipes on. Every other answer maps 1:1 to one config field, so re-applying an unchanged answer is already a no-op.
- **Best solution:** in `ApplyTo` only, compare against the current collapsed value: `var edgeOn = config.Gestures.TopEdge || config.Gestures.LeftEdgeSteamMenu || config.Gestures.RightEdgeSteamQuickAccess; if (Features.EdgeGestures != edgeOn) { set all three to Features.EdgeGestures; }`. No general diff against `Export(current)`, no edit mask, no schema or setup UI change.
- **Tests:** `tests/WSGM.Tests/Core/SetupAnswersTests.cs`: Export then ApplyTo with only TopEdge on keeps Left and Right off; answer false from a split state turns all three off; answer true from all-off turns all three on. Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SetupAnswers"`.
- **Plan v2:** B028 (narrow "ApplyTo changes a field only when the answer differs from Export(current)" to the gesture comparison).
- **Related:** U04A-LFA-003 (ledger-u04), INSTALL-V-004, C10, C17.

### INSTALL-V-002: The failure summary's Close button starts WSGM

- **Severity:** medium
- **Where:** `src/WSGM.Setup/UI/SetupViewModel.cs` `OnPrimary` (`case SummaryPage: if (!_flow.Contains("uninstall")) _engine?.StartWsgm();`), `ShowSummary` (the `!ok` pages with "Close"); `SetupEngine.StartWsgm` (`--shell --activate` when nothing was running), `SetupEngine.RollBack` (already restarts the previous WSGM).
- **Problem:** after a failed install or update, "Close" calls `StartWsgm`. When WSGM was running, `RollBack` already restarted it, so a second start follows; when it was not, an update failure opens a WSGM session the user never had, right after "Nothing was changed". The quiet path starts WSGM only on success (`QuietSetup.Finish`).
- **Best solution:** a `private bool _installSucceeded` set to true only in the success branch of `ShowSummary` (the "Done"/"Done, with a problem" page); `OnPrimary` calls `StartWsgm` only when it is set. Button texts and pages are unchanged.
- **Tests:** none automated (the setup view model has no test host and none is added for this). Manual: a refused update (Steam kept open with a game) closes without starting WSGM (M01-40).
- **Plan v2:** B007.
- **Related:** INSTALL-V-003.

### INSTALL-V-003: Uninstall with `App\WSGM.exe` missing skips every restore and deletes the HidHide ledger

- **Severity:** medium (never-strand rule)
- **Where:** `src/WSGM.Setup/Engine/SetupEngine.cs` `PlanUninstall` (every `app` step is `!File.Exists(app) || ...`), `RestoreController` (returns true when the exe is missing), `DeleteUserData` (keeps `hidhide-ownership.json` only when `StillHiddenDevices` is non-empty), `PrepareAnswers` (the existing pattern that extracts the payload's `App` into `AppStaging`), `DeleteProgramFiles` (already deletes `AppStaging`).
- **Problem:** when `App` is gone (antivirus quarantine, interrupted manual cleanup), uninstall reports success while the controller stays hidden, Steam autostart and other managers stay disabled, and, without "keep data", `DeleteUserData` deletes the HidHide ledger and config.json that hold the only record of what to undo.
- **Best solution:** one private `string? UninstallExe()` in `SetupEngine`, evaluated when the first `app` step runs: `InstallLayout.AppExe` when it exists; otherwise, when the setup carries its payload (the stored repair setup and any downloaded setup do), `Payload.Extract("App", AppStaging)` once and return `AppStaging\WSGM.exe`; otherwise null. The shim, chord, shell and `--uninstall-restore` steps run that executable, so every restore works from the payload copy. When it is null, `RestoreController` sets `StillHiddenDevices = ReadLedgerDevices()` and returns `StillHiddenDevices.Count == 0`, so a non-empty ledger is kept and the existing "Your controller may still be hidden" summary appears. No new text.
- **Tests:** after INSTALL-017 gives `SetupEngine` its paths: with a temp root lacking `App`, no payload and a ledger listing one device, `RestoreController` fails, `StillHiddenDevices` lists it and `DeleteUserData` keeps the ledger; with an empty ledger it succeeds. Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup"`. Manual: M01-48 (also run once with the payload path: `App` renamed, uninstall from the stored setup shows the pad again).
- **Plan v2:** B007 for the code; its "tests cover both decisions through the existing engine seams with temp paths" is not possible before B030 (no path seam exists), so the unit test lands with B030 and B007 is validated by M01-48.
- **Related:** INSTALL-V-002, INSTALL-017, never-strand group (crosscutting), INPUT-V-003, DEVICE-005, STEAMHOST-015 (decided D10: the same uninstall also restores the boot-movie override, `.wsgm-original` and the `themes_custom` junction and hands back Steam's startup-movie choice; steamhost.md and B138 own that step, and it must run from the same `UninstallExe()` result when it needs WSGM).

## Low

### INSTALL-005: Over-the-shoulder elevation applies the profile and shortcuts to the wrong account

- **Severity:** low (only when a standard user elevates setup with another account's credentials)
- **Where:** `src/WSGM.Setup/Engine/SetupEngine.cs` `Detect`, and the per-user work resolved from the elevated token: `ApplyAnswers` and `PrepareAnswers` (the account's config.json), `Register` and `DeleteProgramFiles` (per-user Start menu), `DeleteUserData` and `ReadLedgerDevices` (`LocalApplicationData`), `StopRuntime`'s `PackagedLaunch --recover`, `StartWsgm`; `WindowsSetup.SteamInstalled` (HKCU).
- **Problem:** in that case every per-user effect lands in the administrator's profile; the user who signs in has no config, no boot manifest and no shortcuts. Not a 2.x regression (the Inno script did the same).
- **Best solution:** in `Detect`, compare the session's interactive user (`WTSQuerySessionInformation` WTSUserName and WTSDomainName for the current session) with the process user (`WindowsIdentity.GetCurrent().Name`); when they differ, refuse before any change through the existing refusal page with one sentence telling the user to run setup from the account that uses WSGM. No `TargetUser` threaded through the steps. Put the comparison in `src/WSGM.Install` so UNCOVERED-003 can reuse it from WSGM's elevated one-shots.
- **Tests:** the comparison as a pure function over two names (case-insensitive, domain included). Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup"`.
- **Plan v2:** B030.
- **Related:** C9, C18, U04A-LFA-005 (ledger-u04), CONFLICT-17, UNCOVERED-003. The refusal adds one message in an unsupported configuration; it is listed for the maintainer under remaining concerns given the 2026-10-03 "not enterprise PCs" stance.

### INSTALL-007: A logon queued at service stop can still launch WSGM

- **Severity:** low
- **Where:** `src/WSGM.LogonService/ServiceHost.cs` `ServiceMain` (`StopRequested.Wait()` then STOPPED at 136-141), `HandlerEx` (queues `OnSessionLogon` on the thread pool, 190-203); `src/WSGM.LogonService/SessionLauncher.cs` `HandleLogon` (`TryLaunch` at 121, outside `Gate`).
- **Problem:** between the STOPPED report and process exit, a queued logon can still reach `CreateProcessAsUser`. Setup stops the service first and then force-stops WSGM only in its own session, so a second user's sign-in in that window can start WSGM during a file swap. The window is milliseconds.
- **Best solution:** one `static bool _stopping` in `SessionLauncher`, set under `Gate` by a new `SessionLauncher.Stop()` that `ServiceMain` calls right after `StopRequested.Wait()` and before reporting STOPPED. `HandleLogon` takes `Gate` around the `_stopping` check, `TryLaunch` and the `Sessions[sessionId] = state` insert, so no launch starts after STOPPED is reported. No dispatch owner, no watchdog join (watchdogs die with the process, which is what setup relies on).
- **Tests:** through the INSTALL-V-006 seam: after `Stop()`, a logon makes no launch call. Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LogonService"`.
- **Plan v2:** B025.
- **Related:** A02-F019 (its "explicit service dispatch owner" is rejected), INSTALL-V-006, C15.

### INSTALL-016: Update and repair choices are composed twice

- **Severity:** low
- **Where:** `src/WSGM.Setup/QuietSetup.cs` `Run` (device, common list, `deviceIntegration` rule) and `DevicePlugin`; `src/WSGM.Setup/UI/SetupViewModel.cs` `Choices()` (the `_hardware is null` branch).
- **Problem:** "keep the installed device plugin, keep installed common plugins, add new GPU offers, set `deviceIntegration` only when the device choice was made or there is none" is written twice and can drift between the window and quiet updates.
- **Best solution:** one `SetupEngine.KeptChoices(JsonObject answers, IEnumerable<string> addedGpuIds)` that returns the `InstallChoices` for an update or repair. `Choices()` calls it in its update branch with the checked new graphics; `QuietSetup` calls it for a non-fresh run without `/plugin`, passing every `NewGpuOffers()` id. Fresh-install and explicit-plugin paths stay where they are.
- **Tests:** over a temp payload after INSTALL-017: installed device and common ids are kept, added GPU ids appended once, `deviceIntegration` untouched when a device is kept. Filter `FullyQualifiedName~Setup`.
- **Plan v2:** B030.
- **Related:** C1.

### INSTALL-017: SetupEngine reads the real Program Files, so its tests filter around it

- **Severity:** low
- **Where:** `src/WSGM.Setup/Engine/SetupEngine.cs` (every `InstallLayout.*` use, `StopRuntime` at 544 and 560, `DeleteUserData`, `ReadLedgerDevices`); `tests/WSGM.Tests/Setup/SetupShutdownContractTests.cs` (expectations shaped around the real machine).
- **Problem:** the engine's decisions depend on whether the developer's machine has `%ProgramFiles%\WSGM\App`, and nothing about uninstall or rollback can be tested with temp folders.
- **Best solution:** `SetupEngine`'s internal constructor takes `string root, string machineData, string userData` beside the payload and runtime, exactly as `SetupFileTransaction` takes its paths; `Detect` passes `InstallLayout.Root`, `InstallLayout.MachineData` and `%LOCALAPPDATA%\WSGM`. The engine derives `App`, `Plugins`, `Setup`, `SetupPackages`, the bundle and components files from them. `InstallLayout` stays static (it is linked into the SYSTEM service). Extend `IRuntimeShutdown` only with an operation a test needs; do not build an all-machine port.
- **Tests:** `SetupShutdownContractTests` drops its real-machine filtering and asserts the same order against a temp root with and without `App`. Filter `FullyQualifiedName~Setup`.
- **Plan v2:** B030.
- **Related:** C1, C8, INSTALL-V-003, INSTALL-016.

### INSTALL-027: Setup's shell mutex, device-owner and anchor names are unpinned copies

- **Severity:** low
- **Where:** `src/WSGM.Setup/Engine/WindowsSetup.cs:46-50`; `src/WSGM/Program.cs:710` (`Local\WSGM.Shell`), `src/WSGM/Shell/DeviceCoordinator.cs` (`Global\WSGM.DeviceOwner`), `src/WSGM/Core/ExplorerShellAnchor.cs` (`Local\WSGM.ShellAnchor.RecoverySettled`), `src/WSGM.DeviceLab/Preflight/WindowsPreflightInspection.cs` (fourth DeviceOwner copy); `SetupShutdownContractTests` pins only the exit events.
- **Problem:** Setup/AGENTS.md says the contract tests pin every cross-version name; three are not pinned, so a rename on one side silently breaks update and uninstall handoff.
- **Best solution:** as SESSION-047 decided: one linked `SessionProtocolNames.cs` with today's values, used by WSGM, setup and Device Lab; `SetupShutdownContractTests` asserts each value as a literal.
- **Tests:** `SetupShutdownContractTests`. Filter `FullyQualifiedName~Setup`.
- **Plan v2:** B031.
- **Related:** SESSION-047, C13.

### INSTALL-032: Launcher-shared linked sources have no home

- **Severity:** low
- **Where:** `src/WSGM.Launch/WSGM.Launch.csproj:30-35`, `src/WSGM.LogonService/WSGM.LogonService.csproj:24-29`, `src/WSGM.PackagedLaunch/WSGM.PackagedLaunch.csproj:113-121` (links into `src/WSGM/Core` and `src/WSGM/Interop`).
- **Problem:** small executables compile files out of the application's folders, so an edit to an app file silently changes three other binaries.
- **Best solution:** move the files that are still shared after the session and install batches (`BootManifest.cs`, `ScheduledTaskXml.cs`, `WindowsCommandLine.cs`, `Win32Common.cs`, `SessionProtocolNames.cs`) under `src/Shared/Process` as BUILD-B6 lays out; values and namespaces unchanged.
- **Tests:** `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true` and the B173 filter.
- **Plan v2:** B173.
- **Related:** C16, BUILD-019, BUILD-023.

### INSTALL-V-004: Every update re-runs the Steam autostart takeover and the other-manager disable

- **Severity:** low
- **Where:** `src/WSGM/Program.cs` `RunSetup` (`if (answers.SteamAutostartTakeover) SteamAutostartService.Apply(...)`, `if (answers.OtherManagersTakeover) OtherManagers.Disable(...)`); the stored `true` re-exported by `SetupAnswers.Export`.
- **Problem:** once accepted, each update or repair (quiet in-app updates included) disables again whatever the user re-enabled by hand, with no page shown. Setup/AGENTS.md: only the user's choice applies it.
- **Best solution:** in `RunSetup`, read `config.SteamAutostartTakeoverAccepted` and `config.OtherManagersTakeoverAccepted` from the config loaded before `Mutate`, and run each action only when the answer is true and the stored value was false. No new state; the Settings toggles keep applying them as today.
- **Tests:** none automated (`RunSetup` has no seam and none is added); manual M01-39: update with the takeover accepted and Handheld Companion's autostart re-enabled keeps it enabled.
- **Plan v2:** B028.
- **Related:** CONFLICT-27, CRIT-001, INSTALL-020.

### INSTALL-V-005: An unreadable config.json makes setup report "Profile applied"

- **Severity:** low
- **Where:** `src/WSGM/Program.cs` `RunSetup` (`config` null after `LoadForMutation` throws, the answers block is skipped, `return 0` at the `config is null` check); `ExportSetupAnswers` (falls back to defaults through `ConfigStore.Load`).
- **Problem:** setup shows defaults as the current settings and then reports the fatal "Applying your profile" step as done without applying anything.
- **Best solution:** at the `if (config is null)` return, return `ArgumentValue(args, "--answers=") is null ? 0 : 1`. The shim reconcile and shell restore above it still run; setup's existing failure path and summary apply.
- **Tests:** none automated (same seam reason as V-004); manual with a corrupt config.json on a test install.
- **Plan v2:** B028.
- **Related:** INSTALL-006, U04A-LFA-024 (ledger-u04), CONFIG-V-001.

### INSTALL-V-006: The logon service has no seam for its token, dedup and stop decisions

- **Severity:** low
- **Where:** `src/WSGM.LogonService/SessionLauncher.cs` (static, native calls inline); `tests/WSGM.Tests/LogonService` (only `LogonDecisionTests`); `src/WSGM.LogonService/AGENTS.md` (requires seams for manifest rejection, token selection, session deduplication and watchdog races).
- **Problem:** the guide's required coverage does not exist, and INSTALL-007 needs a seam to be tested.
- **Best solution:** one internal `ISessionHost` with the native operations `SessionLauncher` performs (query user token, profile directory, elevation type and linked token, launch, session active, desktop-shell probe, close handle); `SessionLauncher` becomes an instance over it, `ServiceHost` holds the one production instance. No further seams. The INSTALL-001 test ("elevated launch ignores ExePath") is dropped with INSTALL-001.
- **Tests:** dedup of a live logon racing the catch-up sweep, stop admission (INSTALL-007), elevated manifest on a limited token uses the linked token and on a full or standard token uses the user token. Filter `FullyQualifiedName~LogonService`.
- **Plan v2:** B025 (remove the INSTALL-001, V-001 and `BootManifest.cs` parts of its spec; keep INSTALL-007 and this seam).
- **Related:** INSTALL-007, C4.

### INSTALL-C-001: Byte caps outside D2 in setup, the logon guard and the overlay broker

- **Severity:** low
- **Where:** `src/WSGM.Install/InstallLayout.cs` `HasPendingSetup` (`stream.Length > 16 * 1024` reports a pending setup); `src/WSGM.Setup/Engine/SetupFileTransaction.cs` `Recover` (same 16 KiB refusal); `src/WSGM.Setup/Engine/RtssInstaller.cs` (`MaxDownloadBytes` 64 MiB, checked against Content-Length and while copying); `src/WSGM.PackagedLaunch/Injection/OverlayObjectBroker.cs` `Respond` (operation 1 refused when `high != 0 || low > 64 * 1024 * 1024`).
- **Problem:** plan v2 D2 keeps exactly its listed byte bounds and removes every other cap; none of these is listed. The journal cap turns a valid journal into "setup pending" (the service then skips every sign-in), the RTSS cap duplicates what the pinned SHA-256 already decides, and the broker cap refuses a Steam overlay mapping by size.
- **Best solution:** delete the four size checks. `HasPendingSetup` and `Recover` parse whatever is there (schema and flag checks stay); `TryDownload` becomes `source.CopyTo(target)` and the hash check decides; the broker keeps its allowlist and its `access != 4` protection check (a type check) and drops the size condition. `LaunchPayload`'s pipe bounds and `WSGM.Launch`'s 64 KiB failure-message read on the same pipe stay under D2's "LaunchPayload pipe bounds". `SetupAnswers.MaxBytes` and `BundleManifest.MaxBytes` are removed by U04A-C-001 (ledger-u04, B028).
- **Tests:** `SetupFileTransactionTests`: a valid journal padded past 16 KiB recovers. Filter `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SetupFileTransaction|FullyQualifiedName~RtssInstaller"`.
- **Plan v2:** none yet; append to B030 (add `InstallLayout.cs`, `RtssInstaller.cs`) and to B031 for the broker line.
- **Related:** U04A-C-001, D2.

## Nit

### INSTALL-026: PackagedLaunch redeclares P/Invokes it already has

- **Severity:** nit
- **Where:** `src/WSGM.PackagedLaunch/Interop/NativeMethods.cs` (`CloseHandle` 130-132 and `WaitForSingleObject` 166-167 beside the linked `Win32Common`); `Injection/OverlayObjectBroker.cs` `Api.CreateFileW` (NativeMethods has `CreateFileW` at 114-117); `Session/GameForegroundProxy.cs` `GetClassNameW` and `EnumChildWindows` (NativeMethods has both at 322-325).
- **Problem:** two declarations per call drift in marshalling (Win32Common's `CloseHandle` returns void, NativeMethods' returns bool).
- **Best solution:** delete the local `CreateFileW`, `GetClassNameW` and `EnumChildWindows` copies and call `NativeMethods`; use `Win32Common.WaitForSingleObject`; keep NativeMethods' `CloseHandle` only where a caller reads its result, otherwise use `Win32Common.CloseHandle`. The SCM and job-object duplicates across projects stay until B173 moves shared sources.
- **Tests:** build; filter `FullyQualifiedName~PackagedLaunch`.
- **Plan v2:** B031.
- **Related:** INSTALL-033, C6.

### INSTALL-033: Setup and the logon service each declare the SCM API

- **Severity:** nit
- **Where:** `src/WSGM.Setup/Engine/NativeMethods.cs` (OpenSCManager, OpenService, QueryServiceStatus, ChangeServiceConfig, CloseServiceHandle), `src/WSGM.LogonService/Interop/NativeMethods.cs` (same family); `src/WSGM.Launch/JobObject.cs` and PackagedLaunch NativeMethods (job objects).
- **Problem:** duplicate declarations of the same native surface.
- **Best solution:** no change now. Setup and the service are separate executables with no shared home yet; when B173 creates `src/Shared`, move the SCM declarations there only if both still need them. Do not create a shared file for these alone.
- **Tests:** none.
- **Plan v2:** B031 records it; the move, if any, is B173.
- **Related:** INSTALL-026, INSTALL-032.

### INSTALL-047: Plugin package files are matched by an id prefix

- **Severity:** nit
- **Where:** `src/WSGM.Setup/Engine/SetupEngine.cs` `InstallPlugin` (`Directory.EnumerateFiles(Plugins, id + "-*.wsgmpkg")`, 731) and `InstalledIds` (`StartsWith(plugin.Id + "-")`, 1108); names are `{id}-{version}.wsgmpkg` (eng/build-bundle.ps1:77).
- **Problem:** ids contain hyphens (`wsgm.device.asus.rog-ally`), so an id that is a hyphen-prefix of another would delete or count the other's package. No current pair collides.
- **Best solution:** one `static bool IsPackageOf(string fileName, string id)`: the name starts with `id + "-"` and the next character is an ASCII digit (every version starts with one). `InstallPlugin` filters `*.wsgmpkg` through it, `InstalledIds` uses it.
- **Tests:** `IsPackageOf("wsgm.device.asus.rog-ally-1.0.0.wsgmpkg", "wsgm.device.asus.rog")` is false; the exact id is true. Filter `FullyQualifiedName~Setup`.
- **Plan v2:** B030.
- **Related:** INSTALL-015, C22.

### INSTALL-V-007: Payload extraction's containment test is a bare prefix

- **Severity:** nit
- **Where:** `src/WSGM.Setup/SetupPayload.cs` `Extract` (`target.StartsWith(Path.GetFullPath(destination))`).
- **Problem:** `..\App.staging2\x` passes for destination `App.staging`. The payload is embedded at build time, so this is correctness of an existing check, not a threat fix.
- **Best solution:** compare against `Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar`.
- **Tests:** a directory payload whose file resolves to a sibling folder is refused. Filter `FullyQualifiedName~Setup`.
- **Plan v2:** B030.
- **Related:** none.

## Refuted or no-change

- **INSTALL-001** (logon service starts `boot.json`'s user-writable `ExePath` with the linked elevated token): confirmed in code (`SessionLauncher.HandleLogon` 89-121, `TryGetElevatedToken`), but DECISIONS.md drops it as same-user security hardening. No change; B025 loses this part, the `BootManifest.cs` edit and M01-49's "only the installed WSGM" clause. The verifier's refuted sub-claim stands: the desktop-shell probe runs only `WSGM.exe` with the unlinked token (`IsDesktopShellInSession` 521-523).
- **INSTALL-002** (elevated setup runs installers and scripts staged in the user's `%TEMP%`): dropped by DECISIONS.md. B026 is removed whole.
- **INSTALL-003** (`%ProgramData%\WSGM` keeps the default ProgramData DACL): dropped by DECISIONS.md. B027 keeps only the U04A-LFA-011 updater download move, which ledger-u04 owns, and only if it fixes a functional bug.
- **INSTALL-008** (WSGM.Launch stages the task XML in `%LOCALAPPDATA%\WSGM`): dropped by DECISIONS.md. The duplication half needs nothing either: the XML builder is already one linked file (`ScheduledTaskXml.cs`, WSGM.Launch.csproj:30), and the two `schtasks` runners have different deadline contracts and log sinks. B029 keeps only U04B-LFA-012 (one `/Delete` with its own short timeout), owned by ledger-u04.
- **INSTALL-009** (owned-task cleanup record for WSGM.Launch): no change. WSGM.Launch deletes its task right after the pipe connects; no record, no `Unknown` state.
- **INSTALL-010** (AppContainer overlay broker forwards the game's access and file disposition): a low-integrity-to-user hardening item, dropped with the other security findings. Impact is the game truncating its own renderer log. Its size cap is removed by INSTALL-C-001; B031 drops the broker-constants step.
- **INSTALL-V-001** (single-file host extracts natives into a writable temp folder): no change per D14 and the security list. The logon service keeps `PublishSingleFile`.

## Unwritten review ids

The review assigned these ids but stopped before writing them, and no other input names their subject: INSTALL-004, INSTALL-011, INSTALL-012, INSTALL-013, INSTALL-014, INSTALL-018, INSTALL-019, INSTALL-021, INSTALL-022, INSTALL-023, INSTALL-024, INSTALL-025, INSTALL-028, INSTALL-029, INSTALL-030, INSTALL-031, INSTALL-034, INSTALL-035, INSTALL-036, INSTALL-037, INSTALL-038, INSTALL-039, INSTALL-040, INSTALL-041, INSTALL-042, INSTALL-043, INSTALL-044, INSTALL-045, INSTALL-046.

- **Disposition:** retired with no content. A disposition "per id" as B024 asks cannot be written, because nothing ties an id to a defect. B024 stays a read-only re-review of the five projects plus the U04B files the critic lists (`SteamAutostart*.cs`, `KnownStartupApps.cs`, `WindowsPolicyOperation.cs`, `DesktopAppProcessBackend.cs`), but it records new findings under new ids (INSTALL-C-002 onward), lists these 29 as "no body, retired", and applies DECISIONS.md (no security hardening) and the simplify and no-arbitrary-limits rules. It appends any fix as a batch before B030.
- **Plan v2:** B024 (change "write one disposition per id" to the above).
