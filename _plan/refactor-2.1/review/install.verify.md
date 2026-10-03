# Adversarial verification: setup, install layout, launch de-elevation, logon service, packaged launch

Verifier: Claude (INSTALL-V), read-only, baseline `master` 1329813f. Input: `_plan/refactor-2.1/review/install.md`
(17 KB, ends mid-sentence inside INSTALL-010; Section 1 claims C1-C23 and findings INSTALL-001, 002, 003,
008 are complete, INSTALL-010 is cut off, and the bodies of INSTALL-005, 006, 007, 009, 015, 016, 017, 020,
026, 027, 032, 033 and 047 plus the reviewer's Section 3 (manual rows and batches) were never written).
Where a missing body is referenced from Section 1 with enough substance, it was verified from that
reference.

Read in full for this pass: `SessionLauncher.cs`, `ServiceHost.cs`, `ServiceInstaller.cs`, `ServiceLog.cs`,
`LogonDecision.cs`, LogonService `Program.cs` and csproj; `SetupEngine.cs`, `SetupFileTransaction.cs`,
`WindowsSetup.cs`, `Registration.cs`, `RtssInstaller.cs`, `WebViewRuntimeInstaller.cs`, `SetupLog.cs`,
`SetupExecutable.cs`, `SetupPayload.cs`, Setup `Program.cs`, `SetupOptions.cs`, `QuietSetup.cs`,
`SetupViewModel.cs`, `ProfilePage.cs`, `Install-UsbipDriver.ps1` (main body), Setup csproj and manifest;
`InstallLayout.cs`, `SetupComponents.cs`, `UpdateFailure.cs`; Launch `Program.cs`,
`ScheduledTaskLauncher.cs`, `Elevation.cs`, `LaunchPayload.cs`, `SuspendedProcess.cs`,
`SteamInputLeaseHost.cs`, `RotatingFileLog.cs`, `LaunchLog.cs`; PackagedLaunch `Program.cs`,
`OverlayObjectBroker.cs`, `OverlayObjectAllowList.cs`, `PackageDebugExemption.cs`; the three AGENTS.md;
cross-boundary `src/WSGM/Core/{BootManifest,BootManifestWriter,Installer,SetupAnswers,UnelevatedLauncher,
UpdateChecker,PluginPackageManager(PendingPluginRemovals)}.cs`, `src/WSGM/Program.cs` setup one-shots,
`ConfigStore.Load/PreserveCorruptFile`; `build.ps1` payload staging; `tests/WSGM.Tests/Setup/
SetupShutdownContractTests.cs`; the removed Inno script `47e940a3^:installer/WSGM.iss`.

## Refuted

None of the reviewer's findings is wrong in substance. Every refutation attempt below ended in either
confirmation or a narrower correction. One sub-claim is refuted and folded into Corrected:

- INSTALL-001's sentence "The probe also runs `ExePath` (SessionLauncher.cs:517-544)" as part of the
  escalation: the probe runs only when the file name is `WSGM.exe` and always with the unlinked user token
  (SessionLauncher.cs:521-523), so it is not part of the elevation path.

## Corrected

**Line citations (whole review).** Several citations use the line numbers of a concatenated `cat -n` of a
folder, not the file. Wrong: `ServiceHost.cs:331-349, 400-414` (file has 230 lines; the stop path is
136-141 and 166-173), `QuietSetup.cs:208-213` (actual 71-77), `QuietSetup.cs:289-294` (153-157),
`WebViewRuntimeInstaller.cs:199` and `229-243` (16 and 46-60), `SetupLog.cs:47` (36),
`WSGM.Setup.csproj:106-109` (27-30), `WSGM.LogonService.csproj:44-49/49` (24-29/29),
`WSGM.PackagedLaunch.csproj:105-113/109` (113-121/117), `WSGM.Launch.csproj:63-68` (30-35). Citations into
SetupEngine, SetupFileTransaction, Registration, WindowsSetup, SessionLauncher, SetupViewModel, the
Launch files and the AGENTS.md files are correct. Anyone executing from this review must re-anchor the
wrong ones.

**INSTALL-001** (keep high; same-user medium to elevated, a UAC bypass, not cross-user). Confirmed:
`boot.json` is read from the user profile by SYSTEM (SessionLauncher.cs:88-98), `Elevate:true` duplicates the
linked full token (318-379) and `CreateProcessAsUser` starts `manifest.ExePath` (121). LogonService/AGENTS.md
lines 7-8 already require path validation that the code does not do. Corrections: drop the probe sub-claim
(see Refuted); `InstallLayout.Machine.AppExe` does not exist, the fix is `InstallLayout.AppExe` (already
linked into the service). Note the behaviour change: `Installer.InstallDir` documents that a dev run from
another folder records that folder (src/WSGM/Core/Installer.cs:12-18); after the fix only the installed
copy (dev-deploy installs there) boots at sign-in. Fix the false comment in BootManifest.cs:12-18 in the
same change, since the file is linked into both projects.

**INSTALL-002** (keep high). Confirmed for USB/IP script and installer, HidHide, RTSS, WebView2 and the
answers files. The scope is wider than written: (a) `InstallApplication` extracts the controller payload
to `%TEMP%\wsgm-controller-*` and copies `libviiper*`/`VIIPER*` from there into `App.staging`
(SetupEngine.cs:636-649), so a same-user swap plants a DLL that the installed WSGM loads; (b) the
elevated setup's own native libraries are extracted to the user's temp before any setup code runs
(INSTALL-V-001), which the proposed staging root cannot cover. The recommended staging root under
`%ProgramFiles%\WSGM\Setup\Staging` is fine but must not be under `InstallLayout.Setup` paths that
`SelfDeleteAfterExit` and `StoreSetup` delete or replace during the same run; a sibling such as
`%ProgramFiles%\WSGM\Staging` avoids that.

**INSTALL-003** (raise medium to high; correct the remedy). Confirmed that nothing in Setup/Install sets an
ACL and that the default ProgramData ACL lets BUILTIN\Users create files and folders (CI-only ACE), so any
local user can plant the absent records. The planted-journal chain is exact: `SetupFileTransaction.Cleanup`
deletes the journal after every commit (SetupFileTransaction.cs:215-223), `HasPendingSetup` then reports a
plant as pending, the logon service skips every sign-in (SessionLauncher.cs:72-77), the WSGM shell refuses
to start (src/WSGM/Program.cs Mode==Shell branch), and merely opening setup runs `StopRuntime` (closing
WSGM and Steam) and `Recover()`->`RollBack()`, which deletes App, Plugins, Packages, the setup image and
bundle.json (SetupEngine.cs:171-184, SetupFileTransaction.cs:155-161). Two corrections:
1. Worse than written: if `%ProgramData%\WSGM` does not exist yet (any machine before first install), a
   standard user can create it and becomes its owner with full control. SYSTEM then appends and rotates
   `wsgm-service.log` with `File.Move(..., overwrite: true)` (RotatingFileLog.cs:56-63) and elevated setup
   writes and deletes records inside a user-owned folder, which is the classic mount-point/object-manager
   symlink route to an arbitrary SYSTEM file write or delete. Cross-user, so high. The fix must reset
   the owner (Administrators) as well as set a protected DACL, every run, before setup writes anything.
2. "One call; no reader changes" is wrong. The WSGM updater, which may run unelevated ("Windows asks
   for elevation when WSGM is not elevated", UpdateChecker.cs:217), downloads into
   `%ProgramData%\WSGM\Updates` (UpdateChecker.cs:71). A Users-read-only DACL breaks that download. The
   DACL change must land together with or after the U04A-LFA-011 fix that moves the download.
   `PendingPluginRemovals.Write` from an unelevated WSGM only degrades to a logged warning, which is
   acceptable.

**INSTALL-005** (C9/C18; keep the defect, replace the remedy). The per-user operations resolved from the
elevated token are confirmed (DeleteUserData, ReadLedgerDevices, Start-menu shortcuts, `--setup
--answers`, HKCU Steam and WebView2 checks, StartWsgm). The defect exists only for over-the-shoulder
elevation (a standard-user session elevated with another account's credentials). It is not a 2.x
regression: the Inno script ran `WSGM.exe --setup` and the post-install start without
`runasoriginaluser` (WSGM.iss:171, 188-190). The proposed `TargetUser` resolution (WTS name -> SID ->
ProfileList, cross-checked with the shell token, threaded through every per-user step) adds a new
identity mechanism for an unsupported configuration. Simplest fix that satisfies plan line 105 ("refuse
before writes"): in `Detect`, compare the session's interactive user (`WTSQuerySessionInformation`
WTSUserName/WTSDomainName for the current session) with the process user and, when they differ, show the
existing actionable-refusal pattern ("run setup from the account that uses WSGM") before modifying the
machine. No per-step plumbing. C9's "original interactive process/token is undefined for an elevated
process" is overstated: the session shell's token is reachable from an elevated admin, it is just not
needed for the refusal.

**INSTALL-006** (C2/C14; confirm, narrow). Confirmed: "Applying your profile" (fatal) is followed by the
fatal "Registering the sign-in service" (SetupEngine.cs:324-326), so a service-registration failure rolls
back files while config.json, the Steam autostart takeover and the other-manager changes stay applied, and
the summary still says "Nothing was changed" (SetupViewModel.cs:470). Narrowing: (a) reordering so the
service step runs before the answers is feasible (registration does not depend on the answers; the
installer start skips the catch-up sweep, ServiceHost.cs:117-120), but `ApplyAnswers` itself performs
irreversible work (SteamAutostartService.Apply, OtherManagers.Disable) inside one fatal step
(src/WSGM/Program.cs RunSetup), so a failure inside it still makes the wording false. The honest minimal
fix is ordering plus treating a failure after the answers ran as the legacy-removed branch's "partly
changed" summary, which is existing UI text, not new wording. (b) The migration point is forward-looking,
not a current defect: today `--export-setup-answers` writes nothing to config (ConfigStore.Load only copies
a corrupt file aside, ConfigStore.cs:1096-1121). It becomes a defect the moment the 2.1 migration persists
on load, because the staged new WSGM runs the export before the user confirms and before a rollback can
restore the old binary. Keep it as a binding constraint on the migration design: export migrates in
memory only, and rollback after a persisted migration needs the old reader to still read the file.

**INSTALL-007** (C15, A02-F019; lower to low). The window is real but tiny: `ServiceMain` reports STOPPED
(ServiceHost.cs:136-141), returns, the dispatcher returns and the process exits, killing background
thread-pool workers and watchdogs. A queued `OnSessionLogon` can reach `CreateProcessAsUser` only in the
milliseconds between the STOPPED report and process exit, and only if a logon event arrived at that
moment. The reviewer's remedy (one stop flag checked under `Gate` before `TryLaunch`, no dispatch owner,
no watchdog join) is the right size; A02-F019's "explicit service dispatch owner ... exact
token/session/watchdog lifetime" adds mechanism without a defect that needs it.

**INSTALL-008** (C11/C19; confirm the defect, correct the remedy). Confirmed: elevated WSGM.Launch writes
`launch-task-<pid>-<guid>.xml` into `%LOCALAPPDATA%\WSGM` and passes it to `schtasks /Create /XML`
(ScheduledTaskLauncher.cs:15-31); a same-user watcher can swap it between write and register. The
proposed shared Task Scheduler COM file changes behaviour the reviewer did not account for: WSGM's
`UnelevatedLauncher` deliberately bounds create/run/delete by one shared deadline by killing the
`schtasks` process (UnelevatedLauncher.cs:137-226, with tests on that contract), and an in-process COM call
into the Task Scheduler service cannot be cancelled that way. Either the COM adapter keeps that contract
(not shown how), or the smaller fix is used: keep `schtasks`, write the XML with `FileMode.CreateNew` into
a folder the medium user cannot write (both callers are elevated by definition), and delete it in
`finally`. Plan line 107 has the same gap.

**INSTALL-010** (lower medium to low; body truncated). The broker does forward the game's `access` for
open operations 2/4/6 and the game's access, share, disposition and flags for the renderer-log create
(OverlayObjectBroker.cs:191-228). Impact is bounded: opens are limited to the allowlisted per-pid/game-id
Steam objects (OverlayObjectAllowList.cs:351-403) that Steam's renderer opens with full access anyway, and
the file create is pinned to `%LOCALAPPDATA%\WSGM\packaged-launch.renderer-<pid>.log`, a folder the
AppContainer game cannot write, so the worst outcome is the game truncating or deleting its own renderer
log. Fix by constants (FILE_APPEND_DATA|SYNCHRONIZE, OPEN_ALWAYS, normal attributes), which removes input
rather than adding checks.

**C8 / INSTALL-017** (keep the defect, shrink the remedy). Confirmed: `StopRuntime` reads the real
`%ProgramFiles%` (SetupEngine.cs:544, 560) and the tests filter around it
(SetupShutdownContractTests.cs:169-172). Turning `InstallLayout` into an instance changes a type that is
linked into the SYSTEM service and used across WSGM. Only SetupEngine needs the seam; pass it the root and
machine-data paths the way `SetupFileTransaction` already takes them (SetupFileTransaction.cs:20-38) and
leave `InstallLayout` static.

**C1** (agree with the verdict, note the residue). Dropping `SetupTransactionRunner` and the four ports is
right. The reviewer's own `ISetupMachine` "listing the machine operations the engine performs" risks
becoming a 30-member port; extend the existing `IRuntimeShutdown` only with the operations a test actually
needs (service register, script run, uninstall-entry lookup).

**C12** (plan line 107; "accurate for WSGM" is too generous). The persistent "owned-task cleanup record
deleted on the next maintenance operation" plus a new `Unknown` disposition is new state. The concrete
leak is an on-demand task left when the budget closes before `/Delete` (UnelevatedLauncher.cs:199-202).
The simpler fix is one `/Delete` attempt with its own short timeout outside the dispatch budget, as the XML
delete already is. Flag line 107 under the simplify rule.

**C13 / INSTALL-027** (confirm, add one copy). Besides Program.cs:710, DeviceCoordinator.cs:56 and
ExplorerShellAnchor.cs:22, `Global\WSGM.DeviceOwner` is a fourth literal in
`src/WSGM.DeviceLab/Preflight/WindowsPreflightInspection.cs:67`.

**INSTALL-015** (body missing; confirm with the concrete failure). `FindUninstallCommand` matches by
DisplayName substring and returns the first hit across both views (Registration.cs:142-165). Microsoft's
`usbipd-win` (common on WSL machines) contains "USBip", so `UsbipPresent` reports the driver present and,
with `components.json` saying setup installed USB/IP, uninstall's "Removing the USB/IP driver" can run
usbipd-win's MSI uninstall string with Inno switches appended (Registration.cs:129-139), then waits 180 s
for an entry that never disappears and reports failure. Medium. Match the exact uninstall key or the
DisplayName prefix the pinned installers write, not a substring.

## Confirmed (ids only)

INSTALL-001, INSTALL-002, INSTALL-003, INSTALL-005, INSTALL-006, INSTALL-007, INSTALL-008, INSTALL-009,
INSTALL-010, INSTALL-015, INSTALL-016, INSTALL-017, INSTALL-020, INSTALL-026, INSTALL-027, INSTALL-032,
INSTALL-033, INSTALL-047, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14, C15, C16, C17, C18,
C19, C20, C22, C23. (C21, the manual-matrix rows, was not re-verified.)

## Missed findings

**INSTALL-V-001 (high, PLAUSIBLE) Elevated setup and the SYSTEM logon service load native DLLs that the
.NET single-file host extracts into a temp folder writable by lower-privileged users.**
`src/WSGM.Setup/WSGM.Setup.csproj:20-25` and `src/WSGM.LogonService/WSGM.LogonService.csproj:16-17` set
`PublishSingleFile` with `IncludeNativeLibrariesForSelfExtract=true`. The host extracts bundled native
libraries before `Main` into `%TEMP%\.net\<app>\<bundle-hash>\` and, when that folder already exists,
reuses files that are present (it re-extracts only missing ones; it does not verify content). For setup
(`requireAdministrator`), `%TEMP%` is the elevating user's own temp, so a same-user medium process can
pre-create the folder with a doctored `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` or `av_libglesv2.dll`
(Avalonia natives), which the elevated setup then loads. For the service, SYSTEM's temp is
`C:\Windows\Temp` on builds before `SystemTemp`, where Users may create folders. This lands before any of
setup's own code, so INSTALL-002's staging fix does not cover it. Verify on the built artifact first
(run the release setup once, inspect `%TEMP%\.net\WSGM.Setup`, then confirm reuse with a pre-created
folder on a scratch VM; attended). Recommendation: (a) logon service: set `PublishSingleFile=false` like
WSGM.Launch and WSGM.PackagedLaunch; it is installed into `App` beside the same self-contained runtime and
nothing needs it to be one file (build.ps1 already copies every App `*.dll`, line 155-157). That removes
mechanism. (b) Setup must stay one downloadable file; choose the mitigation with evidence (for example a
setup-controlled extraction base directory created admin-only before the elevated instance starts). Not
covered by any ledger row.

**INSTALL-V-002 (medium) After a failed install or update, the summary's "Close" button starts WSGM.**
`src/WSGM.Setup/UI/SetupViewModel.cs:507-512` calls `_engine?.StartWsgm()` for every `SummaryPage` that is
not an uninstall, including the failure pages built at 458-473 whose button reads "Close". `RollBack` has
already restarted the previous WSGM when one was running (SetupEngine.cs:1059-1064), so this starts a
second instance; when nothing was running, `StartWsgm` launches `--shell --activate` (SetupEngine.cs:495),
i.e. it opens a WSGM session the user never had, elevated with setup's token, right after telling them
nothing changed. The quiet path only starts WSGM on success (QuietSetup.cs:138-141). Recommendation: start
WSGM only from the success summary (pass `ok` to the page or check it in `OnPrimary`). No UI change.

**INSTALL-V-003 (medium) Uninstall can strand the HidHide cloak and delete its only record when
`App\WSGM.exe` is missing.**
`RestoreController` returns success without doing anything when `InstallLayout.AppExe` does not exist
(SetupEngine.cs:902-907), so `StillHiddenDevices` stays empty and, without "keep data", `DeleteUserData`
deletes `hidhide-ownership.json` (SetupEngine.cs:954-975). The physical controller stays hidden with no
ledger left to undo it, and the summary reports success. App goes missing after an interrupted manual
cleanup, an antivirus quarantine, or the INSTALL-003 planted-journal rollback. This violates "never
strand users on exit/uninstall". Recommendation, smallest form: when the executable is missing, set
`StillHiddenDevices = ReadLedgerDevices()` and return false, so the ledger is kept and the existing
"controller may still be hidden" summary appears. Running `--uninstall-restore` from the installed setup's
own payload would be the fuller fix but adds extraction work.

**INSTALL-V-004 (low) Every update and repair re-runs the Steam autostart takeover and the other-manager
disable when acceptance was stored once, including quiet in-app updates.**
`src/WSGM/Program.cs` RunSetup runs `SteamAutostartService.Apply` when `answers.SteamAutostartTakeover` and
`OtherManagers.Disable` when `answers.OtherManagersTakeover`, on every `--setup --answers`. A quiet update
re-exports the stored `true` (SetupAnswers.cs:161-162, QuietSetup.cs:68-70), so a user who later turned
Handheld Companion's autostart back on has it silently disabled again by the next WSGM update, with no page
shown. Setup/AGENTS.md:46-48 says only the user's choice applies it. Recommendation: apply both only on a
false-to-true transition against the exported value, the same diff rule INSTALL-020 proposes for the
feature switches. No new state.

**INSTALL-V-005 (low) An unreadable config makes setup show defaults as the current settings and then
report "Profile applied" without applying anything.**
`--export-setup-answers` loads with `ConfigStore.Load`, which falls back to defaults on a parse error, and
exports them with `freshInstall=false` (src/WSGM/Program.cs ExportSetupAnswers). `--setup --answers` then
uses `LoadForMutation`, which throws, logs, skips the answers and returns 0 when `config` is null, so
setup's fatal "Applying your profile" step passes. Recommendation: return non-zero from RunSetup when
answers were supplied but config could not be loaded, so the existing failure path and summary apply. This
also matters for the 2.1 migration: a config the new reader cannot parse must not be reported as applied.

**INSTALL-V-006 (low) The logon service's elevated branch is the only consumer of the linked-token
duplication and is untested, while LogonService/AGENTS.md:19-20 requires seams for token selection and the
watchdog.**
Only `LogonDecisionTests` exist under `tests/WSGM.Tests/LogonService`. Combined with INSTALL-001 this means
the sign-in elevation path has no automated coverage at all. Recommendation: when INSTALL-001 lands, cover
"elevated launch uses `InstallLayout.AppExe` regardless of the manifest's `ExePath`" through the
`ISessionHost` seam the reviewer proposed (C4); do not add more seams than that.

**INSTALL-V-007 (nit) `SetupPayload.Extract`'s containment check is a bare prefix test.**
`target.StartsWith(Path.GetFullPath(destination))` (SetupPayload.cs:77-81) accepts `..\App.staging2\x`
for destination `App.staging`. The payload is embedded and trusted, so this is hygiene only; append a
directory separator to the prefix.

## Batch problems

- None of the seven admitted batches (W02_01, W02_02, T01_01, A02_01..04) touches this domain; a grep of
  `batches/*.md` for the five projects, `UnelevatedLauncher` and `BootManifest` finds nothing. The domain
  remains U20 in `audit/A02/ownership-and-remaining.md:19`.
- The reviewer's Section 3 (manual matrix rows and implementation batches) and the bodies of 13
  referenced findings are missing, so no reviewer batch exists to check. The refined plan must not cite
  INSTALL-005..047 as specified; this file supplies the verified substance for the ones that matter.
- Ordering constraints any future batch must respect:
  1. INSTALL-003 (protected `%ProgramData%\WSGM`) must ship with or after the U04A-LFA-011 move of the
     updater download, or an unelevated WSGM can no longer download updates.
  2. INSTALL-001 edits `BootManifest.cs`, which is linked into both WSGM and the service; both must build
     in the same batch.
  3. INSTALL-008 shared de-elevation code touches WSGM's `UnelevatedLauncher` deadline contract and its
     tests; a batch that replaces `schtasks` with COM is not green until that contract is re-specified.
  4. INSTALL-V-001 (a) changes the logon service publish shape; `eng/dev-deploy.ps1` deliberately skips the
     service (line 12), so a dev loop will not exercise it and the change needs an attended setup run.
  5. INSTALL-006's reorder and INSTALL-V-002 change setup sequencing; Setup/AGENTS.md:76-79 makes
     install/update/repair/rollback/uninstall runs attended live-machine actions.
- Maintainer-rule checks: plan line 105's edit mask and line 107's cleanup record plus `Unknown`
  disposition add state without a defect that needs it (simplify rule); A02-F019's dispatch owner likewise.
  The reviewer's alternatives (diff in `ApplyTo`, one stop flag, no record) comply. INSTALL-005's
  `TargetUser` plumbing does not comply; the refusal in Corrected does. No proposal here adds caps,
  readback gates or UI changes. `LaunchPayload`'s 4 MB/16,384 bounds refuse malformed pipe input rather
  than truncate content, so they do not fall under the no-arbitrary-limits rule.
