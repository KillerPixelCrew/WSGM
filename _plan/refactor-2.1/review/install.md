# Domain review: setup, install layout, launch de-elevation, logon service, packaged launch

Reviewer: Claude (INSTALL), read-only, baseline `master` 1329813f. Scope read in full: `src/WSGM.Install/**`
(8 files), `src/WSGM.Launch/**` (14), `src/WSGM.LogonService/**` (10), `src/WSGM.PackagedLaunch/**` (28 incl.
`Bridge/*.cpp`, CMake, manifest), `src/WSGM.Setup/**` (27 incl. `Install-UsbipDriver.ps1`, axaml code-behind;
`docs/mockup.html` and the two axaml layouts skimmed for bindings only), the linked sources they compile
(`BootManifest.cs`, `ScheduledTaskXml.cs`, `Win32Common.cs`, `ParentProcessStart.cs` consumers), and the covering
tests `tests/WSGM.Tests/{Install,Launch,LogonService,PackagedLaunch,Setup}/*` (16 files). Callers outside scope were
traced with rg (`SetupAnswers`, `Program.RunSetup/ExportSetupAnswers/--uninstall-restore`, `UnelevatedLauncher`,
`SelfElevation`, `ElevationPolicy`, `PendingPluginRemovals`, `UpdateChecker`, `build.ps1` payload staging, the removed
Inno script at `47e940a3^:installer/WSGM.iss`).

None of the seven admitted batches (`W02_01`, `W02_02`, `T01_01`, `A02_01..04`) touches this domain. The whole domain
is still listed in `batches/remaining-audit-inputs.json` (unit U20 in `audit/A02/ownership-and-remaining.md:19`); the
only prior finding with a body is A02-F019. U04A/U04B rows that touch setup are consumers in `src/WSGM` and are cited
where this domain is the other end.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "Setup uses immutable plan/choices plus `SetupTransactionRunner`, separate file-swap, components, runtime-stop and per-user-answers ports; SetupEngine becomes the coordinator" (refactor-plan.md:157) | partially | A runtime-stop port already exists: `IRuntimeShutdown` (Engine/RuntimeShutdown.cs:69-106) with a recording fake in SetupShutdownContractTests.cs:165-250. `SetupEngine.Run` is already the runner (SetupEngine.cs:397-438, 40 lines). The real defects are elsewhere: the App swap is split between SetupEngine.cs:625-682 and SetupFileTransaction.cs:155-166; statics (`InstallLayout`, `Registration`, `WindowsSetup`) make tests touch the real machine (INSTALL-017); choice policy is duplicated (INSTALL-016). | Drop `SetupTransactionRunner` and the four-port split. Keep `SetupEngine.Run`; rename/extend `IRuntimeShutdown` into one `ISetupMachine` port listing the machine operations the engine performs; move the App swap into `SetupFileTransaction`; add one pure `InstallPlanner`; pass an `InstallLayout` instance. |
| C2 | "pages keep their current order/wording/close behavior" (:157) | accurate, one caveat | `Page.OnClose` per page (Pages.cs:72-101, 395-411, 448, 460); flows in SetupViewModel.cs:181-190. Caveat: the failure summary "Nothing was changed" (SetupViewModel.cs:470) is false when a fatal step fails after "Applying your profile" (INSTALL-006). | Keep wording; make it true by reordering steps so no fatal step follows the answers. |
| C3 | "Keep the App.previous transaction, component ownership, USB/IP two-run finishdrivers flow, explicit first-run choices and cross-version exit protocol" (:157) | accurate | SetupFileTransaction.cs:82-199; `InstalledComponents` (Registration.cs:202-252); `PrepareDriverUpdateBoot` (SetupEngine.cs:835-853); quiet fresh install forces takeovers off (QuietSetup.cs:208-213); `RequestExit` (WindowsSetup.cs:238-279). | Keep. Note the ownership record `components.json` is user-plantable (INSTALL-003) and component identification is substring based (INSTALL-015). |
| C4 | "Launch/logon/PackagedLaunch stay dependency-light: inject process/token/job/pipe/session probes" (:157) | partially, over-engineered | PackagedLaunch already has the seams worth having: `IGameProcesses` (GameSessionSupervisor.cs:10-19), pure `LaunchRouteSelector`, `GameSessionExitDecision`, `FollowedGameRule`, journal with injected liveness. Launch's protocol already runs over `Stream` (LaunchPayload.cs:44-149). The untested owner is `SessionLauncher` (static, LogonService/SessionLauncher.cs:20-577), which its own guide requires to be tested (LogonService/AGENTS.md:19-20). | Two seams only: an `ISessionHost` for the logon service, and Launch parent/child protocol functions taking a `Stream` plus a target-start delegate. No job/token/probe interfaces in Launch or PackagedLaunch. |
| C5 | "retaining exact routing, asInvoker, process start identity, noninjecting follow/controller-only, lease containment and uncertain remote-write latch" (:157) | accurate | asInvoker in all three manifests; id+start-time identity (GameSessionJob.cs:144-167, PackageDebugRecoveryRecord.cs:262-275); controller-only first (LaunchRoute.cs:75-80); follow never injects (FollowSession.cs:291-334); latch (GameInjector.cs:386-418); lease fail-open (SteamInputLeaseHost.cs:76-104, Program.cs:136-176). | Keep, plus the broker/injector fixes below. |
| C6 | "Shared linked native declarations remain one authored source" (:157) | partially | `Win32Common` linked (LogonService.csproj:49, PackagedLaunch.csproj:109) but PackagedLaunch/Interop/NativeMethods.cs:127-171 redeclares `CloseHandle`/`WaitForSingleObject`; `GameForegroundProxy.cs:315-334` and `OverlayObjectBroker.cs:273-316` redeclare user32/kernel32 calls already in NativeMethods; job-object P/Invokes exist twice (Launch/JobObject.cs:120-151, PackagedLaunch NativeMethods.cs:207-221); SCM P/Invokes exist twice (Setup/Engine/NativeMethods.cs:40-61, LogonService/Interop/NativeMethods.cs:75-123). | INSTALL-026, INSTALL-033. |
| C7 | "do not add GPL product policy to MIT SDK/library helpers" (:157) | accurate | `WSGM.Install` references MIT `WSGM.Device.Sdk` only for types (WSGM.Install.csproj:13); no reverse dependency. | Keep. |
| C8 | "Tools and install layout/static readers receive explicit path/environment ports where they own state" (:157) | partially | `InstallLayout` is a static class over `Environment.GetFolderPath` (InstallLayout.cs:13-99) consumed by the stateful SetupEngine; `StopRuntime` branches on the real `%ProgramFiles%` (SetupEngine.cs:544, 560) and tests filter around it (SetupShutdownContractTests.cs:169-172). `DeviceMachineIdentity`, `DisplayAdapterInventory`, `PluginOffers`, `BundleManifest` own no state. | Turn `InstallLayout` into an instance record with one `Machine` value; leave the pure readers static. |
| C9 | "Setup one-shots carry an authenticated target interactive user identity/root; setup derives it from its original interactive process/token" (:105) | partially | Correct for the WSGM one-shots (U04A-LFA-005) but the setup side has more per-user operations resolved from the elevated token: `DeleteUserData` and `ReadLedgerDevices` (SetupEngine.cs:954-975, 1071-1095), per-user Start-menu shortcuts (SetupEngine.cs:891-899, 932-939), `PackagedLaunch --recover` journal (SetupEngine.cs:560-566), `WebViewRuntimeInstaller.Present` HKCU (WebViewRuntimeInstaller.cs:199), `SteamInstalled` HKCU (WindowsSetup.cs:61), `StartWsgm` launching WSGM elevated as the elevating account (SetupEngine.cs:487-496). "Original interactive process/token" is undefined for an elevated process. | INSTALL-005: resolve one `TargetUser` in `Detect` from the session (WTS user name -> SID -> `ProfileList`), cross-checked with the session shell token when present; every per-user operation takes it; refuse per-user steps before any write when it cannot be resolved. |
| C10 | "Update/repair sends an edit mask and independent edge-gesture values; untouched switches stay byte/semantically unchanged" (:105) | over-engineered | Setup never reads config; WSGM exports and applies (`SetupAnswers.Export/ApplyTo`, src/WSGM/Core/SetupAnswers.cs). The collapse is in `ApplyTo` writing all three gestures. | INSTALL-020: `ApplyTo` changes a field only when the answer differs from `Export(current)`; gestures are written only when the collapsed value changed. No mask, no schema change, no setup UI change. |
| C11 | "Scheduled-task de-elevation registers the existing least-privilege InteractiveToken XML through a narrow Task Scheduler COM adapter taking the XML string" (:107) | partially | True for `UnelevatedLauncher` (src/WSGM/Core/UnelevatedLauncher.cs:44-66). `WSGM.Launch` has a second, independent implementation with the same staging defect (Launch/ScheduledTaskLauncher.cs:15-31). | INSTALL-008: one linked `DeelevatedTask.cs` used by both executables. |
| C12 | "retain an exact owned-task cleanup record when the common budget expires and delete it once on the next explicit maintenance/recovery operation" (:107) | accurate for WSGM, should not be extended | WSGM.Launch deletes the task right after the pipe connects (Program.cs:261-270); the only leak is a kill inside the 20 s handshake, leaving a trigger-less on-demand task. | INSTALL-009: no record for WSGM.Launch. |
| C13 | "Cross-version shutdown event/mutex/anchor names and access rights remain unchanged" (:103) | accurate, under-tested | Only the exit events are pinned (SetupShutdownContractTests.cs:11-16). `ShellMutex`, `DeviceOwner`, `AnchorRecoverySettled` (WindowsSetup.cs:48-50) are unpinned literals that duplicate src/WSGM/Program.cs:710, DeviceCoordinator.cs:56, ExplorerShellAnchor.cs:22; Setup/AGENTS.md:38-40 says the tests pin them. | INSTALL-027. |
| C14 | Configuration migration section (:97-101) | partially | Silent on setup: the staged new WSGM runs `--export-setup-answers` before the user confirms (SetupEngine.cs:236-259, started by SetupViewModel.cs:192 and QuietSetup.cs:203) and `--setup --answers` before a later fatal step (SetupEngine.cs:324-326). A migrating load in either path writes schema 1 under a binary that rollback may remove. | INSTALL-006: export is read-only (migrate in memory, never write); answers are the last fallible step before commit; a downgrade-readability fixture for the old 2.0.x reader is part of the migration acceptance. |
| C15 | A02-F019 "Logon service reports stopped while queued launch/catch-up work remains unowned ... explicit service dispatch owner and stop admission with exact token/session/watchdog lifetime" | accurate defect, correction to remedy | ServiceHost.cs:331-349, 400-414; SessionLauncher.cs:41-68. Setup stops the service precisely so the watchdogs die with it (WindowsSetup.cs:186-191). | INSTALL-007: close admission with a flag checked under `Gate` before `TryLaunch`; do not join watchdogs; no dispatch-owner type. |
| C16 | A02 linked-declaration inventory (ownership-and-remaining.md:9) | accurate | WSGM.Launch.csproj:63-68, WSGM.LogonService.csproj:44-49, WSGM.PackagedLaunch.csproj:105-113. | Add a home for the launcher-shared sources (INSTALL-032). |
| C17 | Ledger U04A-LFA-003 disposition "explicit guidance disposition; no live instruction edit" | inaccurate | Setup/AGENTS.md:33-35 already forbids the behavior; the defect is in code (SetupAnswers.ApplyTo, ProfilePage.cs:243-256). | Code fix, INSTALL-020. |
| C18 | Ledger U04A-LFA-005 scope | partially | See C9. | INSTALL-005 extends it. |
| C19 | Ledger U04B-LFA-002 (TOCTOU XML staging) | accurate, incomplete | Same in WSGM.Launch. | INSTALL-008. |
| C20 | Ledger U04A-LFA-011 (setup download in shared machine dir) | accurate, root cause wider | `%ProgramData%\WSGM` has no protective DACL at all (no `AccessControl` use in Setup/Install). | INSTALL-003 fixes the directory for every record. |
| C21 | Manual matrix M01-06, M01-39..41 cover setup | partially | No row for `WSGM.Launch --deelevate [--input-lease]` under elevated Steam (including the UAC-off fail-open), and none for the logon service elevated linked-token boot plus dirty-exit Explorer fallback. | Add two rows (Section 3). |
| C22 | refactor-plan.md:83 one device package, never two | accurate in setup | `InstallPlugin` deletes every build of the id and its `Replaces` (SetupEngine.cs:724-739); quiet choice refuses non-bundled ids (QuietSetup.cs:289-294). | Prefix matching nit INSTALL-047. |
| C23 | Frozen Avalonia | accurate | WSGM.Setup.csproj:106-109 pins 12.1.2. | Keep untouched. |

## 2. Findings

Severity counts: high 4, medium 13, low 25, nit 5. "NEW" means no ledger or audit row covers it.

### Security and trust boundaries

**INSTALL-001 (high, NEW) The logon service launches a user-chosen executable with the user's elevated linked token.**
`boot.json` lives in the user-writable `%LOCALAPPDATA%\WSGM` and is read by SYSTEM (SessionLauncher.cs:88-98).
With `Elevate: true` the service duplicates the linked full token and calls `CreateProcessAsUser` on
`manifest.ExePath` (SessionLauncher.cs:111-121, 318-379). Any medium-integrity process of an administrator can
write `{"SchemaVersion":1,"GameModeBoot":true,"Elevate":true,"ExePath":"C:\\Users\\x\\a.exe"}` and is started
elevated at the next sign-in with no consent prompt. The shared source's justification "launching ExePath AS THAT
USER (which is why a user-writable manifest is not an escalation)" (src/WSGM/Core/BootManifest.cs:15-18) is false for
the elevated branch. The probe also runs `ExePath` (SessionLauncher.cs:517-544). Context for the threat-model
question in Section 6: `ElevationPolicy` already elevates WSGM when the user-writable config lists elevated startup
apps (src/WSGM/Core/ElevationPolicy.cs:28-31), so config writers can already get elevated execution after a WSGM
start; this path needs no WSGM at all and survives uninstall of nothing. Recommendation: the service launches only
`InstallLayout.Machine.AppExe` (admin-protected; dev deploy installs there too, eng/dev-deploy.ps1:61) and ignores
`ExePath` (keep the field in the wire format, write-only). Fix the comment.

**INSTALL-002 (high, NEW) Elevated setup executes installers and scripts staged in the elevating user's `%TEMP%`.**
`Path.GetTempPath()` of the elevated token is the user's writable temp. Setup extracts and runs: the USB/IP script
with `-ExecutionPolicy Bypass` and the USB/IP installer it verifies then runs (SetupEngine.cs:761-822;
Install-UsbipDriver.ps1:417-430 hash/signature check followed by `Start-Process` of the same path), the HidHide
installer with no hash check (SetupEngine.cs:864-875), the RTSS setup extracted after hashing only the archive
(RtssInstaller.cs:71-105), the WebView2 installer hashed then run (WebViewRuntimeInstaller.cs:229-243), and the answers
documents read by elevated WSGM (SetupEngine.cs:245-249, 743-747). A same-user medium process can replace any of them
between write/verify and use. This is a regression against the Inno installer, which ran the script and HidHide from
`{app}` under Program Files (`47e940a3^:installer/WSGM.iss`:176-184). Not covered by U04A-LFA-011 (that is the WSGM
updater's download). Recommendation: one setup staging root under `InstallLayout.Machine.Root` (for example
`%ProgramFiles%\WSGM\Setup\Staging\<guid>`, inheriting the admin-only Program Files DACL), created with
`FileMode.CreateNew`, hashed after the copy into it, deleted in `finally`.

**INSTALL-003 (medium, NEW; root cause of U04A-LFA-011) `%ProgramData%\WSGM` keeps the default ProgramData DACL, so
any local user can plant the records that SYSTEM, setup and elevated WSGM trust.**
Setup only calls `Directory.CreateDirectory(InstallLayout.MachineData)` (Registration.cs:227, SetupEngine.cs:719,
SetupLog.cs:47, UpdateFailure.cs:24); nothing in Setup/Install sets an ACL. Under the default ProgramData ACL,
BUILTIN\Users may create files and folders. A planted uncommitted `setup-transaction.json` with `Existing: []` makes
the next setup run `Recover()` -> `RollBack()` delete App, Plugins, Packages and the setup image
(SetupFileTransaction.cs:155-161) and makes the logon service skip every sign-in (SessionLauncher.cs:72-77); a
planted `components.json` makes uninstall offer and remove a USB/IP or HidHide that predates WSGM (Registration.cs:
210-223, SetupViewModel.cs:201-203, contradicting Setup/AGENTS.md:65-68); `plugin-removals.json` deletes plugin
packages; `update-failed.txt` fakes a failed update; the `Updates` folder is U04A-LFA-011; logs appended by SYSTEM
(`wsgm-service.log`) and admin (`setup.log`, `usbip-install.log`) can be pre-created by the user. Recommendation:
setup sets a protected DACL on the folder every run (SYSTEM and Administrators full, Users read and execute, no
inheritance from ProgramData). One call; no reader changes.

**INSTALL-008 (medium; extends U04B-LFA-002) WSGM.Launch stages the de-elevation task XML in the user-writable profile
from an elevated process.** ScheduledTaskLauncher.cs:15-31 writes `launch-task-*.xml` into `%LOCALAPPDATA%\WSGM` and
registers it with `schtasks /Create /XML`; a swapped file with `<RunLevel>HighestAvailable</RunLevel>` and another
`<Command>` runs elevated. It is a second copy of the WSGM `UnelevatedLauncher` path (src/WSGM/Core/UnelevatedLauncher.cs:
44-66) with its own runner (ScheduledTaskLauncher.cs:67-122). Recommendation: one linked `DeelevatedTask.cs`
(Task Scheduler COM `ITaskService.RegisterTask` with the XML string, `Run`, `DeleteTask`) compiled into both
executables, replacing both schtasks runners and the staging files.

**INSTALL-010 (medium, NEW) The AppContainer overlay broker forwards game-chosen access rights and file disposition.**
`OverlayObjectBroker.Respond` passes the low-integrity game's `access` to `