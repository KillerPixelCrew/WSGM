# B024 install and U04B source closure

Status: **partial review, not closed**. Baseline: `master` at `0afe97fb`.
This is a read-only source review. No source was edited, no test or build ran, and no Windows
startup, service, shell, installer or hardware action was invoked during this review.

## Coverage and findings

Implementation follow-up: **B180 is implemented.** Exact-filename matching, the task-restore write
and unused UAC field deletion are applied. All 40 autostart cases passed with fakes after
formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier,
guidance and diff checks passed. No live startup/task/UAC action ran. The observations below remain
the baseline review evidence; these three fixes are now applied, but B024's broader review stays open.

The full source bodies of these Core files were reviewed against the baseline:

| File | Disposition |
| --- | --- |
| `src/WSGM/Core/SteamAutostart.cs` | New functional finding INSTALL-C-002 below. |
| `src/WSGM/Core/SteamAutostartTakeover.cs` | UNCOVERED-002 remains unfixed; B016 did not cover its restore branch. |
| `src/WSGM/Core/SteamAutostartService.cs` | Recovery recording is fixed by B016; strict restore loading is already assigned to B095 with B039's config read contract. |
| `src/WSGM/Core/KnownStartupApps.cs` | No change: filename deduplication and root priority are deliberate; empty roots are excluded and this only offers suggestions. |
| `src/WSGM/Core/WindowsPolicyOperation.cs` | No change: acquisition failure disposes the handle, abandoned acquisition owns the mutex, and the two callers use synchronous scopes. No evidence of double disposal or a cross-thread release was found. |
| `src/WSGM/Core/DesktopAppProcessBackend.cs` | Existing MainModule/process identity and integrity failures remain assigned to SESSION-035/CRIT-004 in B112; this review does not close that implementation. |

Supplemental source checks covered `AutostartSystem.IsTaskEnabled`/`ReadTaskEnabled` and
`UacSettings.UacState`. A C# and PowerShell reference search under src, tests and tools found no
consumer of `EnableLua`; the two UAC consumers use `PromptsDisabled`.

### INSTALL-C-002: Steam's unknown-path fallback matches other executable names

- Severity: low, functional startup matching.
- Evidence: `src/WSGM/Core/SteamAutostart.cs:204` accepts an executable whose text ends in
  `steam.exe`; line 212 accepts it immediately when the known Steam path is absent.
  The existing branch therefore classifies `C:\Tools\notsteam.exe -start` as Steam when
  `steamExePath` is null. This is a source-derived case, not an executed reproduction.
- Effect: the takeover can disable an unrelated startup entry.
- Fix: require the executable's filename to equal `steam.exe`, ignoring case, then retain the
  existing full-path comparison when Steam's path is known. Preserve command-line parsing and
  surface isolation.
- Acceptance: fake Run, shortcut and task sources with suffix-only names are excluded; exact
  `steam.exe` still matches with an unknown path; known-path matching remains unchanged.
- Owner: B180, placed before B030.

### UNCOVERED-002: existing finding omitted from B016's applied work

`findings/crosscutting.md` already describes this defect. The current
`SteamAutostartTakeover.Restore` branch at lines 139-144 treats `IsTaskEnabled` as proof that the
user re-enabled the task and drops the record. `AutostartSystem.cs:119` maps an unreadable task
state to true. B016 fixed recording and confirming reads after disable, but did not remove this
restore pre-read.

The existing remedy is unchanged: attempt `SetTaskEnabled(entry.Location, true)` directly, drop
the record only on accepted dispatch, and keep it on failure. No retry, new recovery state or
readback is needed. Fake tests must show that an unreadable/throwing task query is never consulted
and a refused enabling write keeps the record. Owner: B180 as a B016 follow-up.

### U04B-LFA-041: unused UAC enablement snapshot

`UacSettings.cs:23,45,146,159` retains a registry read, constructor argument and property for
`EnableLua`, with no consumer in the searched source. Delete those declarations and adjust the
three local constructor calls; preserve consent/secure-desktop values and `PromptsDisabled`.
This is the concrete disposition requested by the surviving cross-reference. Owner: B180.

## Five-project review progress

Implementation follow-up: **B181 is implemented.** The omitted answer/bundle caps are removed.
All 35 selected cases passed after formatting, including complete large documents and retained
input refusal; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier,
guidance and diff checks passed. No live setup/config/installer action ran. INSTALL-C-001's
transaction/RTSS/broker removal remains assigned to B030/B031, and B024 stays open.

The following source pass was completed at `master` `dffd56fc`. This is source review only,
not a runtime pass. WSGM.Install (7 files) and WSGM.Launch (12 files) account for 19 of the
73 tracked C# files in the five-project scope.

| WSGM.Install file | Disposition |
| --- | --- |
| `BundleManifest.cs` | U04A-C-001 remains: 1 MiB refusal in Parse/TryRead. Assigned to B181, correcting B028's omission. |
| `DeviceMachineIdentity.cs` | No new defect established: reads identity without plugin execution; StableKey normalizes the documented identity fields. |
| `DisplayAdapterInventory.cs` | No new defect established: present PCI-only collection, parsed hex vendor/device fields, read-failure empty result as documented. No hardware probe ran. |
| `InstallLayout.cs` | INSTALL-C-001 remains: 16 KiB transaction check rejects an otherwise terminal record. Explicitly assigned to B030. Location/schema/terminal flag rules remain. |
| `PluginOffers.cs` | No new defect established: exact/fallback and tested/blind ordering, explicit tie handling and per-adapter graphics offers. This does not validate the hardware matcher's SDK internals. |
| `SetupComponents.cs` | No new defect established: declared roles map once to a distinct ordered controller-stack requirement. No installer is invoked here. |
| `UpdateFailure.cs` | No new defect established: failure explanation is best effort and cleared on success; machine-data location is kept by decision. Testable path work remains with B030. |

| WSGM.Launch file | Disposition |
| --- | --- |
| `CommandLine.cs` | No new defect established: target vector is preserved after --, conflicting lease flags refuse, diagnostics need no target, ordinary launch needs a behavior flag. |
| `Elevation.cs` | No new defect established: queried token handles close in finally and uncertainty stays nullable. Token policy is not tightened. |
| `JobObject.cs` | No new defect established in its callers: suspended target assignment precedes resume; tree tracking and exact job termination retain lease lifetime. Native duplication remains B173's shared-source scope. |
| `LaunchLog.cs` | No new defect established: best-effort diagnostic adapter over the shared rotating writer. |
| `LaunchPayload.cs` | No change: versioned vector/environment wire format and the existing pipe bounds are explicitly retained by D2. SDL exclusion is removed from the controlled child payload. |
| `Program.cs` | No new defect established: lease acquisition precedes de-elevation; readiness and launch report phases are distinct; parent disconnect stops the exact target tree; empty target refuses; environment/argv are reconstructed before contained launch. Fail-open marker/token policy and pipe SID access stay as decided. No game or process was launched. |
| `Properties/AssemblyInfo.cs` | No change: test visibility declaration only. |
| `RotatingFileLog.cs` | No new defect established: append/rotation errors cannot fail a launch and sharing admits concurrent writers. Rotation is cosmetic, not a recovery state. |
| `ScheduledTaskLauncher.cs` | INSTALL-009 remains no-change: task deletion after pipe connection plus final cleanup already exist; schtasks and XML staging remain. B029's app-side deadline cleanup is implemented separately. |
| `SteamControllerExclusion.cs` | No change: one case-insensitive name predicate shared with packaged launch. |
| `SteamInputLeaseHost.cs` | No new defect established: lease release is exchanged once, acquisition failure is fail-open, and injection is opt-in. The canonical binding implementation itself is not re-reviewed here. |
| `SuspendedProcess.cs` | No new defect established: job assignment before resume, owned native handles close, failed creation/assignment/resume terminates the exact created process, command quoting and environment ordering are retained. |

The launch project's application-owned linked `ScheduledTaskXml.cs` and `WindowsCommandLine.cs`
bodies were also read. No new defect was established: UTF-16/InteractiveToken XML and escaping,
optional working directory and Windows argument quoting are preserved. Their shared source-home
move remains B173. Pinned SteamInterop bindings were identified from the csproj, not independently
validated by this source pass.

### Existing cap findings requiring ownership corrections

- U04A-C-001 is still present in `SetupAnswers.cs:97,237` and
  `BundleManifest.cs:154,186,222`. It was omitted from B028's applied source. B181 removes the
  two constants and size checks, retaining empty/malformed/schema refusal. Test large valid answers
  and manifests through parse/file APIs. No new byte/count cap or streaming abstraction is needed.
- INSTALL-C-001's transaction checks remain in `InstallLayout.cs:61` and
  `SetupFileTransaction.cs:47`; the RTSS checks remain at `RtssInstaller.cs:27,149,163`.
  B030 must explicitly include those files and remove those checks while retaining the RTSS hash
  gate. The broker size check stays assigned to B031, with access/type rules preserved.
- These are existing consolidated findings, not newly invented identifiers. Their current source
  presence means the corresponding work is still pending despite earlier ownership notes.

## Logon service review

Implementation follow-up: **B182 is implemented.** Service-open error reporting, successful WTS
buffer ownership and the omitted boot cap are corrected. All 49 selected cases passed after
formatting using helpers, test buffers and temporary files; the full Release solution build had
zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. No live SCM/WTS
operation ran; the baseline findings below are resolved in source, with native acceptance and
B024's remaining two-project review still open.

All 10 tracked WSGM.LogonService C# bodies were reviewed at `master` `f5a6f52d`, bringing
the five-project source count to 29 of 73. No native operation, build or test ran in this pass.

| File | Disposition |
| --- | --- |
| `ISessionHost.cs` | B025's single session-operation seam remains; no new defect established. |
| `Interop/NativeMethods.cs` | SCM/WTS/token/process declarations and layouts reviewed; new uninstall error classification needs the missing-service constant. Shared declaration placement remains B173. |
| `LogonDecision.cs` | No new defect established: dedup/stale decisions precede the manifest action and the current desktop/game arguments are preserved. Its linked-test comment is stale after B025's assembly reference; clean up with B182. |
| `Program.cs` | No new defect established: install/uninstall one-shots are explicit and the dispatcher is default. No service action was invoked. |
| `Properties/AssemblyInfo.cs` | No change: visibility for the service-assembly tests. |
| `ServiceHost.cs` | B025 closes admission before Stopped; installer starts skip catch-up. No watchdog join or dispatch owner is added. |
| `ServiceInstaller.cs` | New functional finding INSTALL-C-003 below; start-tag and bounded stop/delete behavior otherwise remain. |
| `ServiceLog.cs` | No new defect established: best-effort diagnostics over the previously reviewed rotating writer. |
| `SessionLauncher.cs` | B025's gate, dedup and token decisions are present; watchdog retains the unlinked token, anchor grace and one Explorer fallback, closing handles in finally. Its source is unchanged from B025. No live logon proof is claimed. |
| `WindowsSessionHost.cs` | New ownership finding INSTALL-C-004 below; the B025 adapter's source is unchanged since extraction. |

Application-owned linked BootManifest, AtomicFile and Win32Common bodies were read in this pass.
InstallLayout and RotatingFileLog were already read in the earlier project pass. The service keeps
PublishSingleFile and the manifest ExePath launch as decided; security-only alternatives are dropped.

### INSTALL-C-003: failed service open reports successful absence

- Severity: low, truthful uninstall outcome.
- Evidence: `ServiceInstaller.cs:149-153` returns zero for every null OpenServiceW handle and
  says the service is absent. The API also reports access, handle/name and registry failures,
  which do not prove absence. [Microsoft's OpenServiceW contract](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-openservicew).
- Fix: capture the error immediately; only ERROR_SERVICE_DOES_NOT_EXIST yields the idempotent
  absent success. Other errors log the failed open and return failure without stop/delete attempts.
  Preserve the SCM-handle finally. This changes error reporting, not access rights or service DACLs.
- Acceptance: missing-service result is success; a refused/failed open is failure with no later
  mutation. Verify through a minimal helper/overload or existing fake operation, without a broad
  platform port or a live service test. Owner: B182.

### INSTALL-C-004: successful short WTS response bypasses buffer ownership

- Severity: low, error-path native buffer leak.
- Evidence: `WindowsSessionHost.cs:295-300` returns before the cleanup finally when a successful
  query reports fewer bytes than WtsInfoW requires. Microsoft requires the returned buffer to be
  freed. [WTS query buffer contract](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationw).
- Fix: keep failed-query handling separate, then put byte-count refusal inside the successful
  query's try/finally so every successful response is freed exactly once, even when short or
  decoding fails. Preserve the null/unknown-age result and the existing catch-up policy.
- Acceptance: short, invalid-date and valid responses all release the owned buffer once, with
  unknown age for refused data. Use a small internal decode/cleanup helper if needed for fake
  allocation/free checks; no WTS/session action runs. Owner: B182.

### U04A-LFA-021: boot manifest cap still omitted from B025

BootManifest.cs retains its 64 KiB MaxBytes check and oversized-file comments. The consolidated
ledger assigns its removal to B025, but the batch's exclusion of the dropped security ExePath
change also excluded this functional D2 work. B182 owns the omitted removal: keep share mode,
schema/shape checks and ExePath behavior; delete only the cap/check and misleading comment.
Replace the oversized-refusal test with valid padded JSON loading, building both linked consumers.
B039's durable-write work and B173's source-home move remain separate.

## Packaged launch review in progress

Implementation follow-up: **B183 is implemented.** Both failed request branches now keep their
recorded intent. All 26 selected journal/route cases passed after formatting; the full Release
solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed.
The A/B journal interleaving and later cleanup were tested through temporary files and scripted
liveness; native HRESULT/COM caller behavior is checked by source and compilation only. No live
package, COM, overlay or Steam action ran. The remaining packaged-launch source review and live
acceptance remain open.

At `master` `8a96bf5d`, 11 of the 24 packaged-launch source bodies were read. Together with
the three reviewed projects above, this is 40 of the five-project scope's 73 C# files. This
remains a read-only source pass: no package activation, remote write, Steam operation, build or
test ran, and no live title acceptance is claimed.

| Packaged-launch file | Disposition |
| --- | --- |
| `Diagnostics/PackagedLaunchLog.cs` | No new defect established: rotating file diagnostics precede optional console output; changed observations are suppressed. Logging is not a session recovery signal. |
| `Diagnostics/PrivilegeJournal.cs` | No new defect established: first-refusal diagnostic and opt-in exact-process access reporting, with opened handles closed. No access probe ran. |
| `Packaging/PackageActivation.cs` | No new defect established: AAM-only activation, separate failure outcome and COM release in finally; route is not selected here. |
| `Packaging/PackageDebugExemption.cs` | New INSTALL-C-005 ownership race below. Exemption and release remain attended native operations. |
| `Packaging/PackageDebugRecoveryRecord.cs` | Full journal body read; serialized retire/sweep rules retain failed releases. C-005 involves the caller removing intent after another owner relies on it. The native liveness predicate and malformed-record behavior still need cross-checking with the remaining process inspector. |
| `Packaging/PackageIdentity.cs` | No new defect established: queried process handles and package-name allocations close; token/GDK evidence selects known runtime, otherwise Unknown. No installed package was queried. |
| `Program.cs` | Composition and lifecycle body read: recover before activate, actual seed classification, route selection, controller-only/follow refusal of injection, package exemption and stop cleanup. C-005's request caller is reachable here. Shutdown callback/RCW concurrency needs further review with supervision/foreground code; this row does not declare that interaction closed. |
| `RawCommandLine.cs` | No new defect established: removes only the executable prefix and separating whitespace, preserving follow arguments verbatim. |
| `Session/FollowedGame.cs` | Recognition/cache body read: creation time participates in cache reuse, launcher images are excluded and path/marker boundaries are explicit. Native process inspection still remains. |
| `Session/GameSessionExitDecision.cs` | No new defect established: pure cancellation/running/settle/grace decisions; timings are operational, not removed input caps. Native cancellation containment remains part of the remaining supervisor/job review. |
| `Strategies/LaunchRoute.cs` | No new defect established: controller-only always avoids injection; unestablished runtime is supervise-only; known runtime chooses its recorded route. |

### INSTALL-C-005: a refused new exemption can erase recovery for an older grant

- Severity: medium, package-lifetime recovery ownership.
- Source evidence: `PackageDebugExemption.cs:84,96` removes the newly recorded claim on a
  negative HRESULT or COM construction/cast failure. `PackageDebugRecoveryRecord.cs:164-165`
  removes a retiring launcher's claim while another live owner remains, without disabling the
  package-wide exemption.
- Concrete interleaving: A's exemption is active; B records its request intent; A retires and
  keeps the exemption because B is alive; B's request fails and removes B's record. The package
  can remain exempt with no record for a later recovery sweep. This is source-derived reasoning,
  not an executed COM race.
- Smallest correction: do not blindly remove B's intent on either failure path. Keep it for
  existing retirement/recovery, or use the existing package-wide retirement operation only if a
  release is attempted safely. A recorded request that never granted an exemption already costs
  only the documented harmless later DisableDebugging. No new recovery state or automatic
  EnableDebugging retry is required.
- Acceptance: model A/B liveness and retirement with the public journal's temporary-file seam;
  after B's failure the last recovery intent remains, failed release keeps it, and successful
  retirement/sweep removes it. Preserve the controller-only/unknown/follow injection matrix.
  Verify HRESULT and COM-failure caller paths without live package/Steam writes or a broad port.
- Owner: B183, queued before B030 with verified inputs independent of the remaining B024 pass.

## Packaged supervision pass

Implementation follow-up: **B184 is implemented.** All 105 packaged-launch cases passed after
formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier,
guidance and diff checks passed. `Session/InspectionBuffer.cs` adds a reviewed buffer-owner and
creation-identity helper; fake operations verify allocation failure, release counts and assignment
authorization. Native query/containment callers have source/compilation evidence only; no live
process query, job assignment or injection ran. This new source file changes current inventory to
48 reviewed bodies of 74, with the same 26 original bodies still unread. Baseline counts below
describe the earlier source pass, not the current total.

Seven further source bodies were reviewed at `master` `3a8b7ac5`, bringing the source count to
47 of 73 (18 of 24 packaged-launch files). No process enumeration, job assignment, remote write,
package activation, Steam operation, build or test ran for this source pass.

| File | Disposition |
| --- | --- |
| `Session/DetachedStart.cs` | No new defect established: parent process/token handles close; fallback retains a query handle and strips SDL exclusion. The linked ParentProcessStart implementation still needs its own pass. |
| `Session/FollowSession.cs` | No new defect established: failed launcher state requires both nonzero exit and no resident copy; managed cancellation abandons containment. Native job behavior remains unaccepted live. |
| `Session/GameSessionJob.cs` | INSTALL-C-007 below: missing creation time skips identity validation. Existing query/assignment handles and allocation cleanup were otherwise read. |
| `Session/GameSessionSupervisor.cs` | Zero/unknown job count causes discovery rather than premature exit. The unused contained-count field/comments do not participate in that rule; remove with B184. Native discovery and callback timing remain unaccepted live. |
| `Session/ProcessInspector.cs` | INSTALL-C-006 unlisted read caps and INSTALL-C-008 resize ownership error below. Process/token/window/path query bodies were read; no query ran. |
| `Injection/SteamInstallation.cs` | No new defect established: reads Steam's components/session, excludes SDL variables, checks component presence before the route. No registry or module probe ran. |
| `Strategies/PackagedWin32OverlayRoute.cs` | No new defect established: x64/component/session prerequisite, environment before loads, and renderer observation without reinjection are preserved. These checks are source policy, not overlay acceptance. |

### INSTALL-C-006: native inspection retains unlisted byte caps

`ProcessInspector.cs:256` refuses command-line buffers above 1 MiB; line 418 refuses token query
buffers above 4096 bytes. Neither is on D2's retained list. Remove those arbitrary ceilings while
keeping API sizing/progress checks, allocation failures and unknown-read behavior. The Win32 path
representation limit and operational query retries are distinct from these product byte caps.
Owner: B184; use fake required-size/query results and owned buffers, with no remote process query.

### INSTALL-C-007: containment can use a PID without its creation time

`GameSessionJob.cs:161` performs its open-handle creation-time check only when facts.StartedAt is
known. ProcessInspector can produce facts with unknown creation time, so the other branch can
assign a process solely by PID even though the guide requires both identity components. Refuse
containment when creation time is unknown; on a known identity, query the opened handle and
require the same timestamp before assignment. Supervision may remain degraded and continue
observing; do not guess an identity or add a registry/retirement state. Owner: B184.

### INSTALL-C-008: token resize can free the same buffer again after allocation failure

`ProcessInspector.cs:423-424` frees the old token buffer, then assigns the newly allocated pointer.
If allocation throws, the variable still holds the freed pointer; the OutOfMemory catch returns
null and the finally at line 441 frees that old address again. Clear ownership before the new
allocation, or allocate the replacement before releasing the old buffer. Test the allocation-failure
sequence through a small owned-buffer seam, without live token/process calls. Owner: B184.

B184 also removes GameSessionSupervisor's unused `_containedCount` field/increment and the
comment that implies it gates the already-existing positive-count fast path. Preserve that fast
path and the zero-count re-scan behavior; do not add whole-machine polling while established.

## Packaged injection and callback pass

The six remaining packaged-launch C# bodies were read at `master` `5c9693db`. Including B184's
reviewed helper, all 25 current packaged-launch bodies are now read: overall source coverage is
54 of 74, with 20 Setup bodies still unread. This is source coverage, not live acceptance.

| File | Disposition |
| --- | --- |
| `Injection/GameInjector.cs` | INSTALL-C-009 module/path truncation below. Timeout keeps remote allocations alive and latches further work; source was read, not remotely executed. Repeated-load attempted/succeeded semantics remain a cross-check item before full closure. |
| `Injection/OverlayObjectAllowList.cs` | No new defect established: the observed object/name rules remain. Security-only alternatives are dropped by decision. |
| `Injection/OverlayObjectBroker.cs` | Existing INSTALL-C-001 size refusal remains B031; access/disposition hardening remains dropped. Worker stop/join and handle/map cleanup were read, with no IPC operation invoked. |
| `Interop/NativeMethods.cs` | Native declarations/layouts read; existing duplication cleanup remains B031/B173. Declaration review is not runtime marshalling acceptance. |
| `Session/GameForegroundProxy.cs` | INSTALL-C-010 signal/pump retirement race below. Foreground re-checks precede raising the CoreWindow; no window/hook was created. Managed callback exception interaction remains an explicit closure cross-check. |
| `Strategies/AppContainerOverlayRoute.cs` | No new defect established: bridge/component/runtime prerequisites, broker before renderer and no direct-injection fallback are preserved. No bridge or remote export ran. |

The linked ParentProcessStart body was also read: native environment/attribute/process-thread
allocations have finally cleanup, and ownership of the returned process handle is explicit. The
linked PackagedLaunchCommand file was read only through its declaration/vocabulary prefix; its
remaining compose/parse bodies must be finished before B024 closes. Already reviewed linked
Win32Common/SDL-exclusion/rotating-log sources are not claimed as fresh runtime evidence.

### INSTALL-C-009: module inspection truncates its list and paths

`GameInjector.cs:431,439-440` uses 1024 module slots, clips the reported count with Math.Min and
uses a 520-character filename buffer. A module beyond the retained list or a truncated long path
can be reported absent, changing renderer/bridge routing. D2 retains neither cap and forbids
truncation. Size the module array from the API's required bytes and complete path reads from the
API result, keeping representation/checked-allocation failures rather than a replacement product
cap. Test an array beyond 1024 and a long path through a small enumeration helper/fake, without
remote module enumeration. Owner: B185.

### INSTALL-C-010: foreground pump can outlive its disposed signal

`GameForegroundProxy.cs:75-76` ignores a timed-out two-second Join and disposes ready; Pump can
still call ready.Set at lines 120,137,152,167. Constructor's bounded startup wait also permits a
late window creation after Dispose saw window == 0 and sent no close. The late pump can throw
ObjectDisposedException or leave its window/hook live without a retiring owner.

Close creation admission at disposal, let the pump own window/hook cleanup, and release the
signal only after confirmed pump completion (or let the pump retire it). Preserve the existing
bounded waits, foreground ownership checks and callback delegate lifetime. Do not add a general
window/process port or turn the bounded join into an unbounded UI wait. Test late start and
join-timeout ordering through a small lifecycle seam/fixture; live foreground acceptance stays
attended. Owner: B185.

## Unwritten identifiers

The consolidated install findings establish that the following identifiers have no surviving
body or known subject. Each is retired for that reason, not declared to be a proven non-defect:

| Identifier | Disposition |
| --- | --- |
| INSTALL-004 | Retired: no surviving body or subject. |
| INSTALL-011 | Retired: no surviving body or subject. |
| INSTALL-012 | Retired: no surviving body or subject. |
| INSTALL-013 | Retired: no surviving body or subject. |
| INSTALL-014 | Retired: no surviving body or subject. |
| INSTALL-018 | Retired: no surviving body or subject. |
| INSTALL-019 | Retired: no surviving body or subject. |
| INSTALL-021 | Retired: no surviving body or subject. |
| INSTALL-022 | Retired: no surviving body or subject. |
| INSTALL-023 | Retired: no surviving body or subject. |
| INSTALL-024 | Retired: no surviving body or subject. |
| INSTALL-025 | Retired: no surviving body or subject. |
| INSTALL-028 | Retired: no surviving body or subject. |
| INSTALL-029 | Retired: no surviving body or subject. |
| INSTALL-030 | Retired: no surviving body or subject. |
| INSTALL-031 | Retired: no surviving body or subject. |
| INSTALL-034 | Retired: no surviving body or subject. |
| INSTALL-035 | Retired: no surviving body or subject. |
| INSTALL-036 | Retired: no surviving body or subject. |
| INSTALL-037 | Retired: no surviving body or subject. |
| INSTALL-038 | Retired: no surviving body or subject. |
| INSTALL-039 | Retired: no surviving body or subject. |
| INSTALL-040 | Retired: no surviving body or subject. |
| INSTALL-041 | Retired: no surviving body or subject. |
| INSTALL-042 | Retired: no surviving body or subject. |
| INSTALL-043 | Retired: no surviving body or subject. |
| INSTALL-044 | Retired: no surviving body or subject. |
| INSTALL-045 | Retired: no surviving body or subject. |
| INSTALL-046 | Retired: no surviving body or subject. |

U04B-LFA-013 through 049 also lack saved bodies. Their surviving cross-references remain mapped
in `findings/ledger-u04.md`; this partial review confirms the Core startup concerns above and
gives 041 a concrete disposition. It does not yet close the remaining per-id source coverage.

## Remaining B024 work

- Finish the remaining two-project pass: WSGM.PackagedLaunch and WSGM.Setup,
  including their application-owned linked contract sources. WSGM.Install and WSGM.Launch source
  and WSGM.LogonService bodies are reviewed above; 11 packaged-launch bodies are also read,
  with cross-check gaps recorded per row. The supervision pass brings packaged coverage to 18 of
  24 at the earlier review baseline. The final packaged pass and B184's helper make current
  coverage 54 of 74; the other 20 Setup C# files remain. Finish the linked command body and the
  explicitly recorded callback/repeated-load cross-checks too.
- Finish the individual U04B-LFA-013 through 049 dispositions against that pass and the existing
  session/install findings. Keep missing-body uncertainty explicit.
- Reconcile any further actual defect into a bounded batch before B030. Security-only concerns
  remain no-change under DECISIONS.md; the decided identity refusal, stop flag and task-XML
  placement are not reopened.
- B024 stays open until that coverage is complete. B180's Core fixes have verified inputs and
  may proceed independently; B030 still waits for the full closure.
