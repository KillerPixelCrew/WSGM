# B024 install and U04B source closure

Status: **source closure complete**. Baseline: `master` at `0afe97fb`; the final pass ran on the
working tree over `c906b386`. All 79 production C# bodies in the five-project scope are read and
dispositioned, every U04B-LFA-013 to 049 id has a recorded disposition, and the two new defects are
assigned to B189 and B190. This is a read-only source review. No source was edited, no test or
build ran, and no Windows startup, service, shell, installer or hardware action was invoked during
this review. Native and attended acceptance stay with the implementing batches and B179.

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

Implementation follow-up: **B185 is implemented.** API-sized module/path inspection and bounded
foreground retirement are applied, with two reviewed production sizing/lifecycle helpers. All 109
packaged-launch cases passed after formatting; the full Release solution build had zero warnings/errors.
Rider cleanup, Prettier, guidance and diff checks passed. Helpers use fake operations; actual native
enumeration and foreground behavior have source/compilation evidence only. No live window/hook,
injection or Steam action ran. Current inventory is 56 reviewed bodies of 76, with 20 original Setup
bodies still unread plus the linked/callback cross-checks below. Baseline counts remain historical.

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

## Linked command and deferred packaged cross-checks

At `master` `09301d86`, the remaining PackagedLaunchCommand compose/parse/refusal/tokenization
bodies were read. No command, package, process or Steam action was invoked. Its AUMID/game/follow
length caps and quoting ownership are already assigned to LIBRARY-032/LIBRARY-030 in B107;
do not create a duplicate fix or treat the 2048/512 product limits as retained LaunchPayload bounds.
Unknown modes refuse, follow recognition paths remain explicit, and the route is still selected
from the activated runtime. This source pass does not prove every quoting round trip; B107 owns
those tests.

### INSTALL-C-011: attempted load is returned as successful without a successful result

`GameInjector.cs:78-82` adds the PID/path key before file/access/remote loading and returns true
on every later occurrence. A definite first failure therefore looks like a successful load to a
subsequent call, though no module was loaded. Current route sequences normally stop at the first
failure, so this is a latent internal load-result contract defect, not a reproduced live title issue.
Retain the first load result: only a completed successful load is reusable as true; failed/uncertain
attempts stay false and do not dispatch again. The process timeout latch remains dominant.
Owner: B186; use an isolated attempt/result helper, with no DLL or remote process action.

### INSTALL-C-012: package exemption retirement has competing callback/owner paths

`Program.cs:182` owns exemption scope disposal while shutdown handlers at lines 389 and 396
also call Dispose on the same object. `PackageDebugExemption.Dispose` reads and uses the RCW
before clearing it, without serialization; Request also publishes the RCW before the COM call.
Console control can overlap owner work, yielding repeated retirement/RCW release or a managed
exception crossing its native callback boundary. These are source interleavings, not triggered
shutdown/COM failures.

Prefer shutdown callbacks that request cancellation/retirement and never perform competing COM
cleanup; the owner performs the existing final retirement, and abrupt process loss stays covered
by the journal. Guard callback boundaries so managed exceptions do not escape. Preserve callback
delegate lifetime, cancellation's game-left-running policy, no retry and B183's failed intent.
No general shutdown coordinator, broad COM port or unbounded callback wait is needed. Test a
request concurrent with owner work through a small request/retirement helper and prove one
cleanup attempt; actual COM/thread/console acceptance remains attended. Owner: B186.

The linked command remainder and packaged callback/repeated-load source cross-checks are now
reviewed and explicitly dispositioned.

Implementation follow-up: **B186 is implemented.** `LoadAttemptResults` preserves the first actual
result, records uncertainty before dispatch and refuses every load while the process is latched,
including cached success. `ShutdownRequest` contains cancellation failures at callback boundaries;
console control and ProcessExit no longer touch the exemption. The launch scope alone performs
COM retirement, preserving cancellation's game-left-running policy and B183's failed intent.
All 117 packaged-launch cases passed after formatting, including repeated/interrupted loads,
latch refusal, concurrent requests during owner work, disposed sources and throwing cancellation
registrations. The full Release solution build had zero warnings/errors; Rider cleanup, Prettier,
guidance and diff checks passed. No live console/COM/package/injection/Steam action ran. Native
wiring has source/compilation evidence only; native acceptance and B179 remain open. The two
reviewed helpers bring current project coverage to 58 of 78, with 20 Setup bodies still unread.

## Setup support and payload pass

At `master` `78a9d28f`, nine Setup bodies and its project definition were read in full. This source
pass invokes no setup, registry, installer, network, service or shell action. Current project
coverage is 67 of 78; 11 original Setup bodies remain unread. The observations below are review
evidence, with B187's implementation checks recorded separately.

| File | Disposition |
| --- | --- |
| `Program.cs` | No new fix: exclusive setup marker, quiet exception boundary and UI entry preserve the approved workflow. Setup-mode architecture remains within later refactor scope. |
| `SetupOptions.cs` | No new fix: unknown arguments refuse and uninstall-only switches are checked. Existing parsing defaults and mode selection were read; this is not a claim of every parser round trip. |
| `SetupPayload.cs` | New INSTALL-C-013 below. INSTALL-V-007's bare-prefix extraction check remains B030. Bundle bounds were already removed by B181. |
| `Engine/SetupExecutable.cs` | No change: the retained executing path supports self recovery; do not substitute the installed filename. |
| `Engine/SetupLog.cs` | No new fix: serialized append and expected IO refusal preserve setup's best-effort log behavior. This is not native logging acceptance. |
| `Engine/RtssInstaller.cs` | New INSTALL-C-014 below. INSTALL-C-001's 64 MiB cap removal remains B030; pin, no-retry and non-fatal installation policy remain. |
| `Engine/WebViewRuntimeInstaller.cs` | No new functional fix: offline payload, hash before execution and final stage cleanup were read. Shared WindowsSetup child completion policy remains; no installer ran. |
| `Engine/WindowsSetup.cs` | Existing name-copy consolidation remains B031. Service inspection distinguishes absence from unreadable, Steam/wrappers are not force-stopped, and device-owner admission waits for marker release. Child timeout observation waits for the same operation rather than overlapping rollback. Native disposal and deletion acknowledgement cross-checks remain before full closure. |
| `Engine/Registration.cs` | Exact USB/IP lookup remains B030; current substring lookup is not closed. Version/RunOnce, Inno handoff, component ledger and USB/IP outcome bodies were read. Base-key ownership and ledger failure interactions remain cross-checks against SetupEngine before full closure. |

### INSTALL-C-013: failed payload opening leaves its archive without an owner

`SetupPayload.Open` opens the embedded resource and ZipArchive before reading bundle.json. If the
entry is absent or parsing fails, no SetupPayload is returned and neither archive nor backing
stream is disposed. Constructor failure can also leave the resource stream open. Transfer ownership
only after successful manifest parsing; close the local archive or stream on every failed open.
Use the real stream entry point to test invalid archives, absent/invalid/null/unsupported manifests,
and successful extraction followed by owner disposal. Owner: B187. This is resource lifecycle
correctness, with no staging or security policy change.

Implementation follow-up: **B187 is implemented.** The real internal archive-open path disposes
its stream or archive on failed construction/manifest parsing and transfers ownership only on
success. All 22 selected payload/plugin-offer cases passed after formatting. Tests verify exactly
one stream disposal for invalid archives, missing/malformed/null/unsupported manifests, and after
successful extraction and owner retirement. The full Release solution build had zero warnings/errors;
Rider cleanup, Prettier, guidance and diff checks passed. No live setup/registry/service/network/
installer action ran. This does not close B024, native acceptance or B179.

### INSTALL-C-014: RTSS body reads have no stall timeout

`RtssInstaller.TryDownload` requests ResponseHeadersRead with HttpClient's five-minute timeout,
then reads the returned body synchronously. After headers, that timeout does not constrain body
reads, so a stalled mirror can keep setup's non-fatal RTSS step stuck indefinitely. Add body-read
stall cancellation and disposal through B030's already-required fake download seam; preserve pin
verification, incomplete-body refusal and no retry. No mirror request or live setup reproduction
was attempted. Owner: B030, alongside its RTSS cap removal, not a second competing download batch.

## Setup transaction, shutdown and native pass

At `master` `07097a24`, `Engine/RuntimeShutdown.cs`, `Engine/SetupFileTransaction.cs` and
`Engine/NativeMethods.cs` were read in full. Coverage is now 70 of 78 project bodies; eight Setup
bodies remain unread. No service, registry, process, installer or shell operation ran for the review.

| File | Disposition |
| --- | --- |
| `Engine/RuntimeShutdown.cs` | No new fix: the narrow runtime port retains setup's ordering authority and delegates the existing native contracts. |
| `Engine/SetupFileTransaction.cs` | Existing 16 KiB recovery refusal stays B030. Durable journal-before-backup ordering, commit failure reset, retained executing image, registration recovery and terminal cleanup were read. Engine stop-before-recovery/rollback interaction remains to cross-check; source presence is not failure-path acceptance. |
| `Engine/NativeMethods.cs` | No new fix: declarations and shell-link interface order reviewed; SCM duplication remains B173 under INSTALL-033. No native call was invoked. |

### INSTALL-C-015: Setup closes registry children but leaves owned roots to finalization

`Registration` chains Machine/OpenBaseKey into child-key opens or deletion without disposing the
owned root. `LegacyInstall` opens two machine roots in an array and can return before either closes.
`FindUninstallCommand` and `WindowsSetup.SteamInstalled` likewise dispose a child but not its base
key. These owned handles remain until finalization or process exit. Add scoped root disposal while
preserving hive/view/lookup order and closing children first. Registry.CurrentUser is a shared
predefined key and stays alive. Owner: B188. No registry port or live writes are needed for this
source ownership correction; native handle closure has source/compilation evidence only.

Implementation follow-up: **B188 is implemented.** Every owned base key in Registration and
SteamInstalled now has scoped disposal; child scopes retire first, including early returns and
exceptions. The shared CurrentUser root stays alive. Source comparison preserves the exact hive,
view, lookup order, registry values and commands. All 13 selected registration cases passed after
formatting; these cover existing isolated decisions, not native handle counts. The full Release
solution build had zero warnings/errors; Rider cleanup, Prettier, guidance and diff checks passed.
No live registry/service/setup action ran. B024, attended acceptance and B179 remain open.

## Setup engine and quiet-flow implementation pass

At `master` `b30af14c`, the full SetupEngine and QuietSetup bodies were read. B030's source
increment also adds and reads SetupUserIdentity, bringing coverage to 73 of 79 production bodies.
The six original UI bodies remain; reading the choice branch alone does not close SetupViewModel.

- B030's explicit paths, shared kept choices, exact USB/IP/package matching, payload containment,
  omitted cap removal, account identity refusal and RTSS body-stall cancellation are applied.
- B007's earlier completion did not implement the missing-App payload fallback: PlanUninstall
  still skipped shim/chord/shell commands when App was missing. That omission is now corrected;
  the application is extracted once, and failed restore retains config.json plus the HidHide ledger.
- File-transaction registration capture/restore and child commands use the existing runtime seam.
  Existing shutdown fixtures now use temporary installation/machine/user paths. Deferred fixtures
  must assert recovery/rollback ordering and refused-stop retention against these paths.
- Maintainer instruction changes validation timing: tests and gates run at the very end. No test,
  build, Rider cleanup, gate or live operation ran for this source increment. Source application
  is recorded separately from validation; B030 and B024 are not marked complete.

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
in `findings/ledger-u04.md`. The per-id dispositions are recorded in the U04B section below.

## Setup UI and cross-check closure pass

On the working tree over `c906b386`, the six remaining Setup UI bodies were read in full:
`UI/Pages.cs`, `UI/ProfilePage.cs`, `UI/SetupApp.axaml.cs`, `UI/SetupViewModel.cs`,
`UI/SetupWindow.axaml.cs` and `UI/XInput.cs`, with the action-row bindings in `SetupWindow.axaml`.
`SetupEngine`, `QuietSetup`, `WindowsSetup`, `InstallLayout.HasPendingSetup`,
`SetupFileTransaction.Recover/Begin/Commit/RollBack`, `InstalledComponents.Read/Write` and the
packaged recovery journal with its liveness predicate were re-read for the open cross-checks.
Coverage is now 79 of 79 production bodies. No setup, window, gamepad, registry, service or
deletion action ran.

| File | Disposition |
| --- | --- |
| `UI/Pages.cs` | INSTALL-C-017 below: pages hide an action by giving it an empty label, but nothing stops the commands. Close behaviours, the confirm page, uninstall confirmation and step rows otherwise match the setup guide. |
| `UI/ProfilePage.cs` | No new defect established: presets, the fresh-install level that follows the hardware choice until edited, parent enablement and answer writing match the guide. Answers come from the WSGM export, so the typed reads keep its schema. |
| `UI/SetupApp.axaml.cs` | No change: options are set before Avalonia starts and the window gets its view model once. |
| `UI/SetupViewModel.cs` | INSTALL-C-017 below. The rest of the flow, planning refusal page, summary truthfulness (legacy removed, profile started, rollback incomplete, still-hidden devices) and the driver-update restart page match the guide. Two nits go with B190: `Shutdown()` has no caller, and the `_hardware is not null \|\| device is null` test in `Choices` is always true after the early return. |
| `UI/SetupWindow.axaml.cs` | INSTALL-C-017's entry point: gamepad A falls through to `PrimaryCommand` whatever is focused. Presses are edge-detected, inactive-window presses are ignored and Closing defers to the page's close behaviour. |
| `UI/XInput.cs` | No change: one struct read per poll and no allocation. A missing DLL means no gamepad. The 50 ms setup-window poll is not a product input path. |

Cross-checks recorded as open in earlier rows are now closed:

- **Stop before recovery.** `StopRuntime` inspects and stops the service, stops WSGM, asks Steam to
  exit, refuses blockers, recovers the packaged journal on uninstall and reserves the device owner
  before `SetupFileTransaction.Recover`. Detect runs that path for a pending record. Its other
  branch only sees a terminal record, which `Recover` cleans up and never replays. If the pending
  path fails, the service stays stopped and WSGM is not restarted, but the pending record already
  keeps the runtime from starting, and setup shows the refusal. Recovery waits for the user's next
  run, with no automatic retry. No change.
- **Rollback ordering.** `Run` rolls back on a fatal step or a failed commit. `RollBack` stops the
  service again first and keeps the transaction on refusal (`RollbackIncomplete`). The summary
  reports that. A failed legacy 1.0 step does not restart 1.0, and its note tells the user to
  remove 1.0 from Settings. No change.
- **Component ledger.** An unreadable `components.json` reads as nothing owned, and a failed write
  after an installed driver leaves it unrecorded. Either way setup never offers to remove a driver
  it cannot show it installed, which is the guide's direction. No change.
- **Deletion and reboot scheduling.** New INSTALL-C-016 below.
- **WindowsSetup native disposal.** SCM and service handles close in finally, process and event
  handles are disposed, the shortcut's COM pointer is released and the device-owner mutex is
  returned or disposed. No new defect established.
- **Packaged journal liveness and malformed records.** The launcher's predicate treats its own PID
  as alive, needs a matching start time within two seconds when one was recorded, and treats an
  unreadable live process as gone. A record without a start time is kept while its PID lives,
  which keeps the exemption rather than releasing a possibly live launcher's game. Malformed JSON
  returns failure to every caller, and records missing a package or PID are dropped on read. No
  change.

### INSTALL-C-016: uninstall reports deleted files that were neither deleted nor scheduled

- Severity: low, truthful uninstall outcome and leftover program files.
- Evidence: `WindowsSetup.DeleteOrScheduleAtReboot` catches the IO failure from a recursive
  `Directory.Delete`, then calls `MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)` on the
  directory and ignores the result. Windows removes a scheduled directory at restart only if it is
  empty ([MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)),
  and the locked file inside it is never scheduled. The log still says the path "is deleted at the
  next restart". `DeleteProgramFiles` and `DeleteUserData` return true regardless, so the step says
  "Program files deleted". A shell anchor that did not settle its recovery is deliberately left
  running by `StopWsgm`, and its image in `App` triggers this. The leftover folder also stops the
  self-delete script from removing the install root. This is source-derived, not reproduced.
- Fix: when immediate deletion fails, schedule each remaining file and then each remaining
  directory deepest first, check every `MoveFileExW` result, log the Win32 error for a refusal, and
  return whether everything was deleted or scheduled. `DeleteProgramFiles` and `DeleteUserData`
  report a refusal with the path in the step note. `FinishInstall` and the self-delete keep their
  non-fatal warning. Keep the recovery-record exclusions, no retry and no new state.
- Acceptance: a temporary tree with one held-open file and a fake scheduling operation records the
  file before its parent directories and reports success. A refusing fake makes the step fail with
  its path. Never call the real `MoveFileExW` in tests, since it writes the machine's pending-rename
  list. Owner: B189, after B030 because both edit `SetupEngine` and `WindowsSetup`.

### INSTALL-C-017: hidden setup actions still run from the gamepad and desynchronize the flow

- Severity: medium, setup window stuck after an install or uninstall.
- Evidence: a page hides its primary or back button with an empty label (`ProgressPage`, the
  loading `MessagePage`s, `MaintainPage`, `SummaryPage` back), but `OnPrimary` and `OnBack` never
  check the label. `SetupWindow.ActivateFocused` sends gamepad A to the focused button's command or
  to `PrimaryCommand`, so A on the progress page reaches `OnPrimary`. No case matches it, so it
  advances `_step` to `summary`. When `Run` returns, `RunAsync` calls `GoTo(_step + 1)` past the end
  of `_flow`. The `ArgumentOutOfRangeException` is lost in the discarded task, `ShowSummary` never
  runs, and the window stays on a progress page that refuses to close and has no buttons. The
  summary, Start WSGM and the driver-update restart page are lost. On the loading
  `MessagePage`s, A closes setup, and B or Escape steps back while the answers task is still
  running. The task then publishes the profile page over an earlier step. This is source-derived,
  not reproduced.
- Fix: `OnPrimary` returns when `Page.Primary` is empty and `OnBack` when `Page.Back` is empty, so
  a hidden action does nothing from any input, matching the visible buttons. Escape on an
  `UpdatePage` without outdated plugins then does nothing instead of closing. Delete the unused
  `SetupViewModel.Shutdown()` and assign `deviceIntegration` in `Choices` without the always-true
  test. No new page state, flags or input layer.
- Acceptance: with a fake engine or view-model seam, primary and back on progress, loading and
  maintain pages leave `_step` and `Page` unchanged, and a completed run reaches its summary. A
  run with a primary press during progress still reaches the summary. Visible actions keep their
  current behaviour. Window and gamepad acceptance stays attended. Owner: B190, independent of
  B030 (UI files only).

## U04B-LFA-013 to 049 dispositions

These ids have no saved title, location or claim. Each row records where the area they sat in is
now covered, from the ledger's citation evidence and this closure's full reads. No claim is
invented for a missing body, and none of them adds a defect beyond those assigned below.
Security-only concerns stay dropped by maintainer decision (security theater, DECISIONS.md).

| Id | Band | Disposition |
| --- | --- | --- |
| U04B-LFA-013 | low | Retired without body. Scheduled-task de-elevation is covered by INSTALL-009 and U04B-LFA-012 (B029, implemented) and SESSION-037 (B112). INSTALL-008's XML staging is dropped by maintainer decision. |
| U04B-LFA-014 | low | Retired without body. Same files covered by SESSION-034 to 042 (B112) and INSTALL-009 (B029). |
| U04B-LFA-015 | low | Retired without body. `ExplorerShellAnchor` is covered by SESSION-038, 039 and 041 (B112), and SESSION-047 with INSTALL-027 (B031, source applied). |
| U04B-LFA-016 | low | Retired without body. Same coverage as 014. |
| U04B-LFA-017 | low | Retired without body. Same coverage as 014. |
| U04B-LFA-018 | low | Retired without body. Same coverage as 014. |
| U04B-LFA-019 | low | Retired without body. Registry and config recovery covered by CONFIG-005 (B039), WINSVC-013/014 (B095), INSTALL-V-004 (B028), CRIT-001 (B016) and UNCOVERED-002 (B180, implemented). WINSVC-034 is refuted. |
| U04B-LFA-020 | low | Retired without body. Same coverage as 019. |
| U04B-LFA-021 | low | Retired without body. Same coverage as 019. |
| U04B-LFA-022 | low | Retired without body. Same coverage as 014. |
| U04B-LFA-023 | low | Retired without body. Same coverage as 019. |
| U04B-LFA-024 | low | Retired without body. Same coverage as 014. |
| U04B-LFA-025 | low | Retired without body. Same coverage as 014. |
| U04B-LFA-026 | low | Retired without body. Steam autostart takeover and recording are covered by CRIT-001 (B016), INSTALL-V-004 (B028), and INSTALL-C-002 with UNCOVERED-002 (B180, implemented). The three autostart files were read in full above. |
| U04B-LFA-027 | low | Retired without body. Same coverage as 014. `WindowsPolicyOperation.cs` was read in full: no defect. |
| U04B-LFA-028 | low | Retired without body. Same coverage as 027. |
| U04B-LFA-029 | low | Retired without body. Same coverage as 027. |
| U04B-LFA-030 | low | Retired without body. Same coverage as 027. |
| U04B-LFA-031 | low | Retired without body. Same coverage as 027. |
| U04B-LFA-032 | low | Retired without body. Same coverage as 027. |
| U04B-LFA-033 | low | Retired without body. Same coverage as 027. |
| U04B-LFA-034 | low | Retired without body. Process identity through MainModule or image path is covered by SESSION-035 and CRIT-004 (B112). |
| U04B-LFA-035 | nit | Retired without body. Interop nits are covered by WINSVC-040 (B094), WINSVC-033 (B100) and INSTALL-026/033 (B031, source homes in B173). |
| U04B-LFA-036 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-037 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-038 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-039 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-040 | nit | Retired without body. Explorer orchestration covered by SESSION-038, 040 and 041 (B112). |
| U04B-LFA-041 | nit | Concrete: unused `EnableLua` snapshot deleted by B180. Spot-checked on this tree: no `EnableLua` remains in src or tests. |
| U04B-LFA-042 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-043 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-044 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-045 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-046 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-047 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-048 | nit | Retired without body. Same coverage as 035. |
| U04B-LFA-049 | nit | Retired without body. Same process-identity coverage as 034 (B112). |

## Closure

- Source coverage is complete: WSGM.Install, WSGM.Launch, WSGM.LogonService, WSGM.PackagedLaunch
  and WSGM.Setup, plus their application-owned linked sources, are read and dispositioned above.
  Every cross-check that was left open in an earlier row is closed in the pass above.
- New defects from the whole review are assigned: B180 to B188 (implemented), B189 (deletion
  acknowledgement, after B030) and B190 (hidden setup actions). B030 no longer waits on B024.
- Spot-checked on this tree: B180's exact `steam.exe` filename match and its restore that no longer
  reads the task state first, and B188's scoped `SteamInstalled` base key, are present.
- Not claimed: native, attended or live acceptance of any install, service, package, injection or
  window behaviour. That stays with the implementing batches and B179.
