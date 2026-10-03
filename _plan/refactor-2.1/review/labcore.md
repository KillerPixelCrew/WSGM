# Device Lab part 2: worker, CLI, admission, capture, machine state, transports, safety

Reviewer: Claude (LABCORE). Baseline: master `1329813f`, read-only. Nothing was built, run or mutated.

## Scope

Owned here (the GUI/wizard-presentation half is `labui.md`):

- `Program.cs`, `DeviceLabJsonContext.cs`, `WSGM.DeviceLab.csproj` (links), `AGENTS.md` (as contract).
- `Application/**` (9), `Cli/**` (2), `Worker/**` (6), `Probes/**` (5), `Preflight/**` (5).
- `Transports/**` (11), `Capture/*.cs` except `CapturePrivacyPreview` (8), `Capture/Live/**` (17).
- `Wizard/`: `LabMachineState`, `LabPowerRecovery`, `LabRumbleRecovery`, `LabControllerInit`, `LabModeCommands`,
  `HidHideAllowance`, `ManagerConflicts`, `LabPawnIo`, `PawnIoSetup`, `AuthenticodeSignature`, `LabPowerGate`,
  `LabPowerLoad`, `LabPowerLog`, `LabPowerTelemetry`, `LabPowerPlan`, `LabSleep`, `LabFanReading`,
  `LabLightingLampArray`, `LabRumbleEvidence`, `LabSystemDump*` (14), `LabInputAnalysis`, `LabMotionAnalysis`.
- Tests: `Worker/**`, `Cli/**`, `Preflight/**`, `Probes/**`, `Application/**`, `Capture/**`, `Wizard/{PreflightTests,
  LabPowerRecoveryTests, LabPawnIoTests, LabAtkAcpiTests, LabMsiWmiTests, LabModeCommandsTests, LabClawLightingTests,
  LabRumbleRoutesTests, LabRumbleHidLayoutTests, LabMotion*Tests, LabSystemDumpTests}`.

Depth: Application, Cli, Worker, Probes, Preflight, Transports, the machine-state/recovery/HidHide/PawnIO files and
the worker/preflight/recovery tests were read in full. Capture core, `LabInputCapture` (all partials' lifecycle,
hooks, polling, WMI) and `LabRumble*` were read for lifecycle/threading/limits; the pure analysis files
(`LabInputAnalysis`, `LabMotionAnalysis`), `LabMotionRecorder*`, `LabSystemDump.*` sections, `LabPowerTelemetry`
and `LabPowerPlan` were read by structure, caps and native-call placement, not line-by-line arithmetic.

Ledger: the Claude ledger has no Device Lab unit entries (U17A-U19B never started). Codex A02 has A02-F011..F014
(worker) and A02-F022 (licensing); A02_03 is the one admitted batch here. Everything else below is NEW.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| P1 | "Device Lab: Same CLI/GUI application services; explicit project/session/worker owners; no runtime policy dependency" (refactor-plan.md:72) | partially | Worker owner exists (`LabWorkerClient`); no session owner (labui C1). Lab links four GPL product interop files (`WSGM.DeviceLab.csproj:34-41`); `NativePathIdentity`/`NativePackageSource` are used by `Packaging/DeviceLabPackageSnapshot.cs`, `NativeHidHide`/`Kernel32` by `HidHideAllowance`, `PawnIoSetup`, `LabMotionRecorder.Serial`. | Keep the target; record A02-F022 as an open licensing decision (section 6). |
| P2 | "decomposes worker request admission, checkpoints, stream watchdog and transport-session registry" (refactor-plan.md:155) | partially accurate, over-scoped | Already split: `LabWorkerHost` (read loop, dispatch, sessions, timer; 327 lines), `LabWorkerSession` (checkpoint, stream, watchdog per session), `LabWorkerCalls` (cancellation). The real defect is that the host is a static over `Console.In/Out` with a static timer, so none of it is testable (`LabWorkerHost.cs:68-150`). | Replace with: "make `LabWorkerHost` one instance over an input reader, output writer, service table and `TimeProvider`; keep Session and Calls". No separate registry/admission types. |
| P3 | "Use a bounded worker queue of 64 pending requests with explicit Busy response, finite framed input, and one dispatch lane" (refactor-plan.md:155; pre-AM01 B02 step 2; A02-F013 disposition) | inaccurate as a need, over-engineered | The only client is the authenticated wizard, which sends one blocking call at a time plus 250 ms `stream-status` polls and 20 Hz slider frames (`LabWorkerClient.Send` blocks, `WizardWindow.Rumble.cs:277-330`). Growth is already bounded by time: every non-stream request carries a 60 s deadline and the client kills the job after it (`LabWorkerClient.cs:279-284`). The actual defects are stale-frame replay and execution after EOF (LABCORE-006/004). | Remove the 64 cap, Busy and frame bounds (no-arbitrary-limits, simplify). Keep "reader thread plus one dispatch lane, cancel readable while work runs". Add: drop stream frames older than `StreamTimeout` at dispatch; drop queued requests after EOF; refuse a call whose token is cancelled before dispatch. |
| P4 | "`LabMachineState` retains record-before-write/readback-before-clear" (refactor-plan.md:155) | partially | Record-before-write holds for power, rumble, HidHide, PawnIO, curated init (`LabPowerRecovery.Record`, `HidHideAllowance.cs:96-97`, `LabPawnIo.cs:81`). Broken by `Read()` returning empty on IO/JSON errors and `Update` then replacing the file (`LabMachineState.cs:72-105`, LABCORE-001). Controller-mode and mode-command records live in side files written by other code, one of them by the worker process (`LabControllerInit.cs:66-67,349-355`, `LabModeCommands.cs:344-345,1176-1214`). Aura and mode-command undo clear without readback by design (write-only hardware). | "One machine record; unreadable or corrupt state is never overwritten; write-only changes clear after the undo was sent." |
| P5 | "owner mutex stays on its dedicated owning thread" (refactor-plan.md:155; pre-AM01 B02 step 3) | inaccurate | The reservation is deliberately an unowned named mutex whose open handle is the lease: "waiting or releasing would add thread affinity across asynchronous plugin cleanup" (`WindowsPreflightInspection.cs:108-136`). Same for the recovery reservations (`LabPowerRecovery.cs:128`, `LabRumbleRecovery.cs:178`). | "The `Global\WSGM.DeviceOwner` reservation is an unowned handle held until ordered cleanup ends; unverified cleanup retains it for the process lifetime." (labui C6 agrees.) |
| P6 | "timeout retains uncertain ownership and never retries" (refactor-plan.md:155) | accurate, with a recorded exception | A missed deadline is `LabWorkerLostException`, never resent (`LabWorkerClient.cs:279-284,350-368`). Start-up recovery replays restores of recorded originals and mode-command undo writes on every start until they succeed (`LabPowerRecovery.RestoreRecorded`, `LabModeCommands.RecoverPending` 1035-1066). That is the documented Lab recovery design (AGENTS "The next wizard start undoes whatever a killed session left"). | State the exception: restoring a recorded original or zero is recovery, not a retry of the uncertain write. |
| P7 | "Stream watchdog zero is not acknowledged before actual zero write" (refactor-plan.md:155) | accurate as target; current code violates | `ZeroQuietly` sets `_lastFrame = DateTime.MaxValue` before invoking zero and swallows only three exception types (`LabWorkerSession.cs:512-526`). | A02_03 fixes it; see refinement R4 for a simpler shape. |
| P8 | "Existing safety allowlists, WMI quarantine, no EC access, curated facts and export preview/approval remain unchanged" | accurate for allowlists, quarantine and EC; partially for curated facts | Interface allowlist (`LabWorkerSession.Method`), ATKACPI id allowlist (`LabAtkAcpi.Call` 608-614), MSI `Get_`/`Set_` prefix check, WMI quarantine (`LabInputCapture.Watch`, `LabWmiQuarantine`), no EC access (`LabLowLevel` remarks, `LabLhmSensors` motherboard group off). Curated facts as transport literals: `LabClawLighting.cs:31,110-113` (record id, VID/PID/usages). | Add: transports take curated layouts as open arguments, as `LabAuraLighting` already does (LABCORE-027). |
| P9 | "Capture buffering/redaction/publication/scaffold validation are independently testable with retained handles and temp roots" | partially | Export/redaction/reader: tested with temp roots (`CaptureExportTests`, `CaptureBundleReaderTests`, `RedactionTests`). Live buffering (`LabInputCapture.Record`, `OnHidReport` noise/dedupe, step accounting) is private inside the native-thread owner and untested (`LabInputCapture.cs:298-460`). | Extract the buffer state into `LabInputStepBuffer` (LABCORE-B5). |
| P10 | "Do not turn evidence records into runtime drivers or add tester probes" | accurate | Probes are Lab-only (`ReadProbeProfiles.cs:14-15` remark); knowledge records stay evidence. | None. |
| P11 | "Compiled read probes require an exact live device and endpoint match" (AGENTS safety boundary; plan B01 "inject ... readprobe ports") | inaccurate today | `RunReadProbeAsync` gates on the imported inventory file, never on live identity (`DeviceLabApplication.cs:171-232`), unlike the attended path which recollects (`:426-432`). | LABCORE-008: recollect live inventory for the gate. |
| P12 | High-rate "no per-sample allocation or logging" (refactor-plan.md:115) applied to the Lab | not applicable for allocation; logging rule holds | Capture and the slider allocate per report/frame (`LabInputCapture.cs:398-460`, `LabWorkerSession.Stream` reflection + JSON). Lab is an attended diagnostic; LABUI-031 already accepts frame allocation. Logging: stream/status are not logged (`LabWorkerHost.cs:159`). | State the Lab exemption for allocation explicitly; keep the no-per-sample-logging rule. |
| P13 | Shutdown phase budgets B3 (planning-corrections.md B3) | not applicable | Worker retirement is client `Dispose`: close stdin, wait 5 s, then job terminate (`LabWorkerClient.cs:66-85`). | Exclude Device Lab from B3 (labui C12). |
| P14 | pre-AM01 B02 "Test actual stage/worker/lifetime flows through simulated interfaces, no native capture or PawnIO install" | accurate goal, unreachable today | Host is static Console I/O; capture, machine state and worker are concrete/static; only `LabWorkerClientTests` spawns a real process for the handshake. | Ports in section 4; tests per batch. |
| P15 | pre-AM01 B01 "inject path/process/readprobe ports" | partially | `IReadProbeProcessLauncher` already exists (`ReadProbeWorkerSupervisor.cs:18-35`). Path boundaries are rebuilt ambiently in 7 places with two different repository-root rules (`ReadProbeWorkerSupervisor.cs:408-412` uses CWD only; CLI uses CWD then base directory, `DeviceLabCli.cs:574-578`). | One `DeviceLabPathBoundaries` built in `Program` and passed down (with labui B4). |
| P16 | A02-F011 "Lab watchdog waits behind a blocked hardware call on every session" | accurate, worse than stated | `Dispatch` holds `lock (sessions)` across `Handle` (`LabWorkerHost.cs:166-169`); the 100 ms `Timer` callback takes the same lock (`:91,304-313`). Timer callbacks are queued every tick regardless, so a 60 s blocked call parks a new thread-pool thread every 100 ms. | Per-session lock; watchdog skips a busy session with `Monitor.TryEnter` (LABCORE-B1). |
| P17 | A02-F012 / A02_03 | accurate; batch correct, one redundant piece | See P7. Also: an inner exception outside `IOException/InvalidOperationException/Win32Exception` escapes `ZeroQuietly`; on the timer thread that terminates the worker process, and on `close` it skips `Dispose`/`Remove` (`LabWorkerHost.cs:247-252`). A02_03's broadened catch fixes both. | Keep A02_03; R4 trims `_zeroFailure` from the stale predicate. |
| P18 | A02-F013 | accurate observation, wrong disposition | As P3. | Replace disposition with LABCORE-006. |
| P19 | A02-F014 "EOF cleanup drains queued stream/open operations before retirement" | accurate | `finally` calls `CancelAll`, `CompleteAdding`, then an unbounded `dispatcher.Join()` that still executes every queued request (`LabWorkerHost.cs:130-147`). | Drop queued work after EOF (LABCORE-004). |
| P20 | A02-F022 "MIT Device Lab compiles generic source from GPL product paths" | accurate | `WSGM.DeviceLab.csproj:34-41`; `src/WSGM.DeviceLab/LICENSE` is MIT; the four files carry no SPDX header. | Maintainer decision (section 6 Q2). |
| P21 | M01-42 "no plugin load/hardware action/probe" for the wizard | partially | Elevated wizard start launches the worker and runs PawnIO reconcile and recovery restores before the welcome page (`WizardWindow.cs:149-180`), even when nothing is pending (`LabPowerRecovery.RestoreRecorded` is called with a started worker). | Same as labui C13; LABCORE-011 also stops starting the worker when nothing is pending. |

## 2. Findings

### LABCORE-001 (high, bug/safety) An unreadable machine record is silently replaced

`LabMachineState.Read` returns an empty record on `IOException`, `JsonException` or `UnauthorizedAccessException`
(`Wizard/LabMachineState.cs:72-88`). `Update` reads through the same method and atomically replaces the file
(`:93-105`). A transient sharing violation (antivirus, backup) or a damaged file therefore discards every recorded
original the next time anything is recorded: the HidHide entry, power/charge/fan originals, pending rumble routes,
`PawnIoInstalledByLab`. The next start then has nothing to undo, which strands the lab's HidHide entry and power
changes. This is the same missing-vs-unreadable confusion the plan fixes for `ConfigStore` (refactor-plan.md:99).
NEW. Recommendation: `Read` returns `Absent | Loaded | Unreadable | Corrupt`; `Update` refuses unless Absent or
Loaded; a parse-corrupt file is copied aside once (`machine-changes.corrupt-<utc>.json`) and kept; the wizard shows
"An earlier test's record could not be read" through the recovery notice. No retry loop, no backup rotation.

### LABCORE-002 (high, concurrency) Global worker lock blocks every watchdog and piles up timer threads

A02-F011 confirmed (P16). NEW detail: `System.Threading.Timer` keeps queuing `ZeroStale` every 100 ms while the
lock is held, so a call that blocks for its 60 s deadline parks hundreds of thread-pool threads in the worker.
Recommendation: sessions in a `Dictionary` guarded only for add/remove/snapshot; each `LabWorkerSession` owns a
`Lock` taken by `Call`, `Stream`, `Checkpoint`, `Acknowledge`, `Release`, `Close` and by the watchdog via
`TryEnter` (skip a busy session; zeroing a lane that is mid-call would be a concurrent write to the same device).
Independent sessions are zeroed on time; a blocked lane is reported as it is today.

### LABCORE-003 (high, safety) Failed safety zero is disarmed, hidden, and can kill the worker

A02-F012 confirmed (P7, P17). `ZeroQuietly` clears `_lastFrame` before the call (`LabWorkerSession.cs:514`), logs a
failure only for three exception types (`:521-525`), and never sets `StreamError` for watchdog failures. Any other
inner exception propagates: from the timer callback it terminates the process (unhandled thread-pool exception), from
`close` it leaves the session in the dictionary undisposed. Covered by A02_03. Recommendation: execute A02_03 with R4.

### LABCORE-004 (medium, safety) A call cancelled before dispatch is still invoked; EOF still runs queued work

`LabWorkerCalls.Begin` returns an already-cancelled source after `CancelAll` or an early `cancel`
(`Worker/LabWorkerCalls.cs:145-160`), but `Handle` invokes the method anyway (`LabWorkerHost.cs:213-226`), so a
`[LabWorkerWrite]` method that does not check its token writes after the wizard is gone or after the wizard cancelled
it. The plan's command contract says cancellation before dispatch is Rejected with no write (refactor-plan.md:111).
Related A02-F014. Recommendation: in `Handle`, a `call` whose token is already cancelled replies
`OperationCanceledException` without invoking; after EOF the dispatcher stops executing and only drains; the
`finally` then zeroes and disposes sessions as today. `CancelAll`'s `_closed` flag then has one meaning.

### LABCORE-005 (medium, lifecycle) Worker retirement joins the dispatcher without a bound

A02-F014: `dispatcher.Join()` (`LabWorkerHost.cs:136`) waits for whatever is queued, so the client's 5 s wait
(`LabWorkerClient.cs:71`) usually ends in a job kill that skips the zero/dispose path. With LABCORE-004 the queue is
empty after EOF except the running call; the join then only waits for that call, which is the correct semantics
(never zero a lane under an active call). No new deadline mechanism needed.

### LABCORE-006 (medium, safety) Stale slider frames replay after the safety zero

Frames queue behind a slow request (`LabWorkerHost.cs:127`). Once LABCORE-002 lets the watchdog zero a quiet lane,
the dispatcher would then apply the backlog of old frames, re-energizing a motor the watchdog just stopped. Today
the same happens after any slow `stream-status`/fan read. NEW (relates A02-F013). Recommendation: stamp each request
with the reader's clock on arrival; the dispatcher drops a `stream` request older than `StreamTimeout`. One field, no
queue cap, no Busy.

### LABCORE-007 (medium, bug) Two of five compiled read probes crash on the reference Claw

`MsiWmiReadProbeProfile.InvokeGetter` always assigns `input["Data"]` (`Probes/ReadProbeProfiles.cs:298-331`). On the
A2VM, `Get_WMI` and `Get_EC` declare no in-parameters, so `GetMethodParameters` returns null; the Claw plugin fixed
exactly this NullReferenceException after a device-verified failure (`WSGM.Device.Msi.Claw/MsiWmiPlatform.cs:186-204`)
and `LabMsiWmi.WmiChannel` copies the fix (`Transports/LabMsiWmi.cs:560-574`). The NRE is not in
`ReadProbeExecutor`'s catch list, escapes to `SelfWorkerProtocol.Run`, exits 70 and is reported `WorkerCrashed`
for `msi.claw-a2vm.wmi-version` and `msi.claw-a2vm.ec-version`. NEW. Recommendation: one MSI_ACPI channel
(`Transports/MsiAcpiChannel`, extracted from `LabMsiWmi.WmiChannel`) used by both the worker transport and the
probes; probes keep their compiled allowlist and getter bytes.

### LABCORE-008 (medium, safety contract) Read-probe gate trusts the imported inventory

`RunReadProbeAsync` matches family and endpoint against the `--from` file (`Application/DeviceLabApplication.cs:178-196`);
the worker only checks that one active `MSI_ACPI` instance exists (`ReadProbeProfiles.cs:300-322`). An inventory
copied from a Claw lets the probe run on another MSI machine with `MSI_ACPI`. The attended path explicitly refuses to
let imported inventory authorize anything and recollects (`:426-432`). NEW. Recommendation: same as the attended
path: validate the file's shape, then collect live inventory and gate on it. Read-only, so low blast radius, but the
documented contract says "exact live device".

### LABCORE-009 (medium, dead functionality) The `.wsgmcap` observe-only pipeline records nothing live

`ObserveOnlyCaptureWorkflow` registers only `ClosedObserveOnlyCaptureSource`, which emits an
"inventory-snapshot-recorded" marker or an `Unavailable` event per step (`Capture/ObserveOnlyCaptureWorkflow.cs:263-271,657-699`).
`GuidedOperatorMarkers` are produced nowhere outside tests (`PassiveCapture.cs:385`, `PassiveCaptureTests.cs:39`),
so `correlate` can never return a finding for a real capture (`PassiveCorrelation.cs:74`). The timeline's clock
segments, device generations, late-arrival detection, blobs and analysis sections (`PassiveCapture.cs`,
`CaptureBundle.cs`, `CaptureBundleReader.cs`, `CaptureModels.cs`, about 3,000 lines) serve only that. The real
capture is the wizard's `LabInputCapture` into `.wsgmlab`. The useful part of `.wsgmcap` today is its redacted
inventory, which `scaffold --from` consumes. NEW. Recommendation: maintainer decision (Q1). Until then: no refactor
investment in this pipeline; keep it byte-for-byte.

### LABCORE-010 (medium, ownership) Three records and three path computations for controller state

`machine-changes.json` holds `CuratedInitRecordId`; `controller-mode.json` holds the reversible mode and is written by
`LabControllerInit.Send`, which runs inside the worker process (`LabCuratedInitWorker.cs:68-71`,
`LabControllerInit.cs:126,349-355`); `mode-commands.json` holds opt-in commands (`LabModeCommands.cs:344-345,1176-1214`).
`wmi-enabling.txt`/`wmi-blocked.txt` sit in the same folder. Each file derives the folder itself
(`LabControllerInit.cs:66`, `LabModeCommands.cs:344`, `LabWmiFirmwareEvents.cs:280-286`). The worker writing a
recovery file bypasses the checkpoint contract (the wizard records, the worker writes hardware). Side-file IO errors
are uncaught in `AddPending`/`RemovePending`/`File.Delete` (`LabModeCommands.cs:1184-1206`, `LabControllerInit.cs:198,205`).
Related labui C5/LABUI-027. NEW. Recommendation: `LabMachineChanges` gains `ControllerMode`
(`LabPendingControllerMode?`) and `ModeCommands` (`IReadOnlyList<LabPendingModeCommand>`); the curated-init checkpoint
persists the original mode (the snapshot already returns it); the worker writes no files; a one-time fold migrates
existing side files (requirement 11: preserve recovery state). WMI quarantine stays a separate plain text file: it
must be written immediately before a possibly fatal enable with minimal IO.

### LABCORE-011 (medium, ownership) Start-up recovery is inline UI code and always starts the worker

`WizardWindow.Start` sequences PawnIO reconcile, power, rumble and controller recovery (`Gui/WizardWindow.cs:149-180`)
and starts the elevated worker even when nothing is recorded, because `RestoreRecorded` takes a started client.
Each recovery has a different catch filter: `LabRumbleRecovery` catches only `InvalidOperationException`/`IOException`
(`LabRumbleRecovery.cs:204`) while the client rethrows `Win32Exception`, `TimeoutException`, `ArgumentException`
(`LabWorkerClient.cs:409-427`), so one escaping exception aborts the later recoveries. LABUI-027 (cross-domain, owned
here). Recommendation: `LabRecovery.RunAsync(Func<ILabWorkerClient> worker, bool elevated, CancellationToken)` runs
the same order, reads the record once, starts the worker only if something worker-bound is pending, and contains
each item's failure (catch `Exception` except OOM per item, as the Claw lighting branch already does,
`LabPowerRecovery.cs:205`).

### LABCORE-012 (medium, test quality) The worker host has no tests

`LabWorkerHost` is static over `Console.In/Out` with a private static `Dispatch` and a timer
(`LabWorkerHost.cs:60-150`). The only host-level test spawns a real process to check the handshake
(`LabWorkerClientTests.cs`). Nothing covers dispatch order, cancel delivery, EOF retirement, watchdog behavior,
error-type mapping or the client's lost-worker path. NEW. Recommendation: instance host (section 4) driven in-process
by a pipe pair; tests listed in LABCORE-B1.

### LABCORE-013 (low, bug) `Release` with a wrong token reports success

`LabWorkerSession.Release` ignores a mismatched token (`LabWorkerSession.cs:494-500`) and the host replies `Ok`
(`LabWorkerHost.cs:243-246`): the wizard believes writes are disarmed while the checkpoint stays armed.
`Acknowledge` throws in the same case. NEW. Recommendation: throw like `Acknowledge`.

### LABCORE-014 (low, lifecycle) An unacknowledged checkpoint blocks the session forever

If the wizard's persist step throws (`LabWorkerClient.Checkpoint` 208-216), no `ack` is sent and `_pending` stays
set; every later `Checkpoint` on that session throws "already open" (`LabWorkerSession.cs:456-459`) until the
session closes. Nothing was written under a pending checkpoint. NEW. Recommendation: `Checkpoint` refuses only while
armed; a new checkpoint replaces an unacknowledged one. Removes a state, adds none.

### LABCORE-015 (low, structure) Service methods are resolved by reflection on every call and never validated

`Method(name)` scans `Interface.GetMethods()` per call and per stream frame; `ZeroQuietly`/`Checkpoint` rescan for
attributes (`LabWorkerSession.cs:461-463,515-516,528-532`). Overloads, a missing snapshot or a missing zero are
discovered at run time. NEW. Recommendation: `LabWorkerService` resolves its method table, snapshot and zero once
when constructed and throws on overloads/duplicates; one test enumerates `LabWorkerServices.All`.

### LABCORE-016 (low, bug) Client can wait 60 s on a worker that already died; unexpected read errors hang every call

`Send` checks `_lost` before registering the reply (`LabWorkerClient.cs:256-263`); if `ReadLoop` finished in between,
its final sweep (`:388-392`) missed the new entry and the caller waits the full deadline. `ReadLoop` catches only
`IOException`, `JsonException`, `ObjectDisposedException`; anything else ends the task without setting `_lost`
(`:383-393`). NEW. Recommendation: re-check `_lost` after registering; `ReadLoop` uses `finally` for the lost sweep.

### LABCORE-017 (low, duplication) The hardware worker re-implements self-worker authorization and launch

`LabWorkerHost.Run` reads the secret with no deadline (`CancellationToken.None`, `:70-73`) unlike
`SelfWorkerProtocol.AuthorizeAsync` (5 s, `SelfWorkerProtocol.cs:254-261`). Process start + job + inheritable pipe
+ secret delivery is written twice (`LabWorkerClient.Start` 97-171, `SystemReadProbeProcessLauncher.RunAsync`
146-212). NEW. Recommendation: one `SelfWorkerProcess.Start(launch, mode, arguments, secret)` in `Application/`
returning the started, job-assigned process; the hardware worker reads its secret with the same deadline. The stdin
hello line stays (it is the pipe protocol's own first frame).

### LABCORE-018 (low, safety) Elevated roles append to and rename a log in a possibly user-writable folder

`LabTrace` writes `wsgm-device.log` beside the executable and rotates it with `File.Move(..., overwrite: true)`
(`Application/LabTrace.cs:308-353`). The elevated wizard and worker follow a hard link or symlink a medium-integrity
process planted there (portable download folder). NEW. Recommendation: in an elevated process, after opening, refuse
a handle whose file is a reparse point or has more than one link and fall back to the temp location; skip rotation in
that case. Keep the file name and location otherwise (tester workflow).

### LABCORE-019 (low, duplication) CI and elevation checks have different meanings in different places

The CLI gates `capture run` and `test hardware` on `CI == "true"` only (`DeviceLabCli.cs:184-188,471-472`) while
`DeviceLabEnvironment.IsContinuousIntegration` accepts 1/true/yes and `GITHUB_ACTIONS` (`DeviceLabEnvironment.cs:19-30`).
The plugin worker re-checks with the broad rule; `capture run` does not, so `CI=1` passes its gate. Elevation is
computed three times (`DeviceLabEnvironment.IsElevated`, `LabTrace.Elevated`, `DeviceLabDoctor.IsElevated`). NEW.
Recommendation: CLI uses `DeviceLabEnvironment`; one elevation helper.

### LABCORE-020 (low, ownership) Path boundaries rebuilt ambiently with different repository-root rules

See P15. `DeviceLabPathBoundaries.ForCurrentUser` is called from `DeviceLabApplication.Boundaries`, `DeviceLabDoctor.Run`,
`ObserveOnlyCaptureWorkflow` (twice), `ReadProbeWorkerSupervisor`, `DeviceLabCli.RunPromote`, `RunScaffold` and the GUI.
Related LABUI-032. Recommendation: `Program` builds one instance; `DeviceLabApplication(DeviceLabPathBoundaries, string
executable)`; workflows take it as a parameter.

### LABCORE-021 (low, bug) MSI checkpoint fails entirely when only the fan flags cannot be read

`LabMsiWmi.Original` contains power and charge failures but reads fans unguarded (`Transports/LabMsiWmi.cs:196-199`),
contradicting its own "what could be captured, and why the rest could not" contract; a fan-flag read failure blocks
the TDP and charge tests. NEW. Recommendation: same containment and a `FansUnavailable` reason.

### LABCORE-022 (low, leak) `LabIntelKx.Open` leaves its admin-only folder on failure

The folder is created before the digest check and `PowerUnitWatts` (`LabIntelKx.cs:173-204`); on a pin mismatch the
stream is disposed but the folder stays; `Dispose` is the only delete. NEW nit. Recommendation: delete on the failure
path.

### LABCORE-023 (low, duplication) Three readers of embedded pin files

`LabPawnIoModule.PinnedDigest`, `LabIntelKx.PinnedDigest`, `PawnIoSetup.ReadPin` each parse an embedded lock JSON
(`LabPawnIoModule.cs:126-140`, `LabIntelKx.cs:309-315`, `PawnIoSetup.cs:435-453`). NEW nit. Recommendation: one
`LabPins` static with `PawnIo`, `RyzenSmuSha256`, `KxSha256`.

### LABCORE-024 (low, structure) The general HID helper lives in rumble capture code

`LabRumbleNative.HidEndpoints/OpenForWrite/WriteReport` and `LabRumbleHidEndpoint` (`Capture/Live/LabRumble.Native.cs:20-90`)
are used by Aura and Claw lighting, controller init and mode commands. NEW. Recommendation: move that half to
`Transports/LabHid.cs` (`LabHidEndpoint`, `LabHid`); XInput helpers stay with rumble.

### LABCORE-025 (low, policy placement) Curated facts as transport literals

`LabClawLighting` hardcodes the record id and the MCU collection identities (`Transports/LabClawLighting.cs:31,110-113`).
LABUI-009 covers the UI literals. Recommendation: open with a `LabClawLightingLayout` from `LabPowerPlan` (the curated
record), like `LabAuraLighting` takes `LabAuraLayout`.

### LABCORE-026 (low, documentation/containment) The worker boundary in AGENTS is not what the code does

AGENTS says "Wizard hardware access runs in the elevated `Worker/LabWorkerHost`". In-process instead: HC mode
commands (HID feature/output writes, `LabModeCommands.Start/Undo`, via `WizardWindow.ModeCommands.cs`; LABUI-026),
the reversible mode switch when used as a mode command (`LabModeCommands.cs:925-940`), the system dump's RyzenSMU
mailbox and MSR reads (`LabLowLevel`, `LabSystemDump.LowLevel.cs:32,48`), LibreHardwareMonitor (`LabLhmSensors`),
input capture, motion serial port configuration. NEW. Decision (simplify): no new worker services. Writes that
capture a readable original run in the worker behind a checkpoint; HC mode commands (no readable original, recorded
before sending) and read-only collection stay in the wizard. Correct AGENTS/README to say so.

### LABCORE-027 (low, no-arbitrary-limits) Caps that drop or truncate evidence

Silent truncation: WMI event properties (24 properties, 32 array items, 128 bytes, 256 characters;
`LabInputCapture.cs:530-546`), HID reports over 512 bytes (`:112,398`). Reported caps: 6,000 events per step and
200 noise samples per device (`:104-110`), system-dump issue lists at 50 (`LabSystemDump.cs:214-226`), device tree
4,000 (`LabSystemDump.DeviceTree.cs:80,120-134`), ACPI tables 256 (`LabSystemDump.Acpi.cs:77,164-200`), SMBIOS
2,048 structures, firmware-event tables 256 (`LabWmiFirmwareEvents.cs:42`), worker/probe diagnostic text 16 KiB
(`SelfWorkerProtocol.Bound`, `ReadProbeWorkerSupervisor` stderr). NEW. Recommendation: remove the silent truncations
and the count caps on issues/devices/tables/structures (the OS list is the natural bound); keep per-item byte bounds
on untrusted input (archive reader, request/response files, 8 MiB firmware table, 64 KiB device property) as IO
safety. The per-step event cap is Q3.

### LABCORE-028 (low, bug) Redaction replaces the user and machine name anywhere

`ReplaceAccountNames` replaces any case-insensitive occurrence longer than two characters (`Capture/Redaction.cs:139-160`):
a user "Max" or a PC named "Ally" rewrites "Win Max 2" or "ROG Ally X" in shared evidence. NEW. Recommendation: whole-
token matches only (word boundary on both sides); other patterns unchanged.

### LABCORE-029 (low, safety) Controller init writes to the first matching collection

`LabControllerInit.Endpoint` and `SwitchMode` take `FirstOrDefault` (`LabControllerInit.cs:242-247,322-332`) while the
AGENTS rule and `LabModeCommands.Locate` require exactly one match (`LabModeCommands.cs:886-914`). With two same-vendor
controllers attached the write goes to whichever enumerates first. NEW. Recommendation: same single-match rule,
refusing ambiguity with today's wording style.

### LABCORE-030 (low, concurrency) No cross-process exclusion on the machine record

`LabMachineState` locks in-process only (`:57`); side files have no lock. A second wizard window (double launch on a
handheld) runs the same start-up recovery, reconcile and HidHide record updates concurrently. The owner reservation
serializes the power/rumble restores but not the record's read-modify-write. NEW. Recommendation: Q4.

### LABCORE-031 (low, lifecycle) Capture start failure can orphan its message thread

`LabInputCapture.Start` disposes after a 10 s readiness timeout (`LabInputCapture.cs:238-252`); `Dispose` posts
`WM_QUIT` only if `_messageThreadId` was set (`:203-206`). A late-starting thread keeps its hooks. NEW nit.
Recommendation: set the thread id before readiness and post quit unconditionally once known; join as today.

### LABCORE-032 (low, structure) Worker and transports depend on wizard types

The wire format uses `LabProject.JsonOptions` (`LabWorkerHost.cs:31,63`, `LabWorkerClient.cs`, `LabWorkerSession.cs`),
the worker and transports log into `Wizard.LabPowerLog`, transports use `Wizard.LabPowerPlan` layouts, and the wizard
depends back on worker/transports. NEW nit. Recommendation: `LabWorkerProtocol.Json` owns the wire options (same
settings); `LabPowerLog`/`LabPowerEvent` move to `Worker/` unchanged in shape (evidence wire names unchanged).
Layouts stay where they are.

### LABCORE-033 (nit, duplication) Hand-listed pending predicates and manager lists

`LabPowerChanges.AnyPending`/`PowerPending` list the fields twice (`LabPowerRecovery.cs:66-87`);
`LabPowerGate.BlockingManagers` repeats process names from `ManagerConflicts.Known` (`LabPowerGate.cs:40-44`).
Recommendation: one field list (`PowerPending || AuraWrittenAt`); an `OwnsPowerHardware` flag on `KnownManager`.

### LABCORE-034 (nit, duplication) Probe descriptors repeat their metadata

`MsiClawReadProbes.Probe` and the `MsiWmiReadProbeProfile` constructor both spell endpoint, rate 2, timeout 5,000 and
repetitions 2 (`ReadProbeProfiles.cs:118-198,270-282`). Recommendation: build the descriptor from the metadata.

### LABCORE-035 (nit) Worker logs the 2 s fan samples

`Dispatch` logs every non-stream op (`LabWorkerHost.cs:159-174`), including the power stage's sampled `FanSpeeds`
calls (`WizardWindow.Power.cs:185,214,224`), about 20 lines per 20 s burst. AGENTS forbids per-sample logging but also
requires every worker operation. Recommendation: no change; 2 s attended sampling is not high-rate and the hard-reset
trace is worth it. Record the interpretation in AGENTS.

### LABCORE-036 (nit) Lab capture allocates per report and per 4 ms poll

`OnHidReport` builds a key string and a `List<int>` per report; `PollGameControllers` allocates arrays and LINQ per
controller every 4 ms (`LabInputCapture.cs:396-460,710-750`). Recommendation: accept for the attended tool (P12);
B5's buffer extraction may reuse a list but must not change recorded output.

### LABCORE-037 (low, test quality) Tests that assert metadata or arithmetic

`HardwareWorker_RegistersRumbleAndCuratedInitWithCheckpointedWrites` asserts attributes on two interfaces
(`LabWorkerSessionTests.cs` last fact); `ReadProbeSupervisor_OutlivesTheWorkersSemanticDeadline` asserts
`t + 2000 > t` (`ReadProbeTests.cs:97-104`); `AnyPending_And_PowerPending_TrackWhatIsRecorded` restates the predicate;
`StagingPathCreatesAUniqueHiddenSibling` and `StagingPathIsHiddenUniqueAndBesideTarget` test the same thing
(`DurableFileTests.cs`). NEW. Recommendation: replace the attribute test with the registry invariant test (LABCORE-015);
delete the arithmetic test; merge the staging tests.

### LABCORE-038 (low, test gaps) Behavior with no tests

`LabMachineState` failure modes, `LabRumbleRecovery`, controller-mode and mode-command pending records,
`LabWorkerClient` lost/timeout/rethrow, probe channel null-input handling, `LabIntelKx` output parsing
(`ReadMsr`, `Return`), `LabAmdSmu.CommandsFor` (HC parity table), `LabControllerInit` single-match. NEW.
Recommendation: added in the batches that touch them.

### LABCORE-039 (medium, licensing) MIT Lab links GPL product interop

A02-F022 confirmed (P20). Q2.

### LABCORE-040 (nit) CLI rebuilds the application facade per command and serializes anonymous objects reflectively

`Application()` and `RepositoryRoot()` per command (`DeviceLabCli.cs:569-578`); `WriteJson` uses reflection
`JsonSerializerOptions` over anonymous objects (`:27-32,668-671`) while results elsewhere use the source-generated
context. Recommendation: one facade from `Program` (LABCORE-020); leave CLI JSON shape as is.

## 3. Plan refinements

Additions:

1. Device Lab worker contract (replaces refactor-plan.md:155 worker sentence): "One instance `LabWorkerHost` per
   worker process over an input reader, output writer, service table and `TimeProvider`; one reader thread and one
   dispatch lane; per-session lock; the watchdog skips a busy session; a call cancelled before dispatch is refused
   without invoking; stream frames older than `StreamTimeout` are dropped at dispatch; after EOF queued requests are
   dropped, the running call is awaited, then every session is zeroed and disposed."
2. Machine record contract: "`LabMachineState.Read` distinguishes Absent/Loaded/Unreadable/Corrupt; `Update` never
   overwrites Unreadable/Corrupt; controller-mode and mode-command records fold into `LabMachineChanges` with a one-time
   migration of the side files; the worker process writes no record files."
3. `LabRecovery` owns start-up recovery order (PawnIO reconcile, power, rumble, controller/mode commands); the worker
   starts only when worker-bound work is pending; one item's failure never skips the others.
4. Read probes gate on live inventory and share the MSI_ACPI channel with the worker transport (fixes LABCORE-007/008).
5. Capture: extract `LabInputStepBuffer` (pure step buffering) from `LabInputCapture`; expose `ILabInputCapture` for
   labui's session.
6. One `DeviceLabPathBoundaries` and one `DeviceLabApplication` from `Program`.
7. State the Lab exemptions explicitly: no B3 budgets, no UI revisions/generations, allocation allowed in attended
   capture paths; logging rule kept.

Changes:

- R1 "owner mutex stays on its dedicated owning thread" becomes "unowned reservation handle held until ordered cleanup;
  retained for the process lifetime on unverified cleanup" (P5).
- R2 "transport-session registry" decomposition becomes "instance host"; no new registry/admission classes (P2).
- R3 A02-F013 disposition becomes LABCORE-006 (stale-frame drop); A02-F014 disposition becomes LABCORE-004.
- R4 A02_03: keep the batch. Simplification: once `_lastFrame` is cleared only after a successful zero, `ZeroIfStale`'s
  existing stale predicate already re-fires; `_zeroFailure` is needed only for log de-duplication, so drop the
  "`_zeroFailure` non-null OR stale" clause from step 1. Everything else in A02_03 stands.
- R5 Restores of recorded originals at next start are recovery, not retries (P6); say so in the plan.

Removals (over-engineering or limit violations in the current plan):

- 64-request bounded queue, explicit Busy response, finite framed input (P3). Simpler shape: points 1 and R3.
- Dedicated owning thread for the owner mutex (P5): would add thread affinity the code deliberately avoids.
- Separate admission/checkpoint/watchdog/registry types (P2).
- Any refactor of the `.wsgmcap` passive pipeline until Q1 is decided (LABCORE-009).
- Count caps that drop evidence (LABCORE-027), except the per-step event cap pending Q3.

## 4. Target design

### Owners

| Owner | File | Responsibility |
| --- | --- | --- |
| `LabWorkerHost` (instance) | `Worker/LabWorkerHost.cs` | Reader loop, dispatch lane, sessions map, watchdog timer, retirement; `static int Run(args)` stays the thin process entry (authenticate, then `new LabWorkerHost(Console.In, Console.Out, LabWorkerServices.All, TimeProvider.System).Run()`). |
| `LabWorkerSession` | `Worker/LabWorkerSession.cs` | Checkpoint/stream/zero per session, own `Lock`, method table from its service. |
| `LabWorkerService` | `Worker/LabWorkerHost.cs` (record moves to `Worker/LabWorkerService.cs`) | Name, interface, open delegate, resolved method table, snapshot, zero; validates on construction. |
| `LabWorkerProtocol` | `Worker/LabWorkerProtocol.cs` | Wire records plus `Json` options (copied settings of `LabProject.JsonOptions`). |
| `ILabWorkerClient` | `Worker/ILabWorkerClient.cs` (new) | Exactly today's `Open<T>`, `Checkpoint<TState>`, `Release`, `StreamError` (labui contract). |
| `LabWorkerClient` | `Worker/LabWorkerClient.cs` | Implements `ILabWorkerClient`; launch via `SelfWorkerProcess`. |
| `SelfWorkerProcess` | `Application/SelfWorkerProcess.cs` (new) | Start process, assign job, deliver secret; used by `LabWorkerClient` and `SystemReadProbeProcessLauncher`. |
| `LabMachineState` | `Wizard/LabMachineState.cs` | Typed read, guarded update, side-file fold, folder path owner (`StateDirectory`). |
| `LabRecovery` | `Wizard/LabRecovery.cs` (new) | Start-up recovery sequence; returns notice lines. |
| `LabPowerRecovery`, `LabRumbleRecovery` | unchanged files | Per-feature restores; called by `LabRecovery` and the power stage button. |
| `MsiAcpiChannel` | `Transports/MsiAcpiChannel.cs` (new, from `LabMsiWmi.WmiChannel`) | The one MSI_ACPI WMI call path with timeout/stuck latch. |
| `LabHid` | `Transports/LabHid.cs` (from `LabRumble.Native.cs` HID half) | HID endpoint enumeration, open-for-write, write report. |
| `LabPins` | `Transports/LabPins.cs` (new) | Embedded lock-file readers. |
| `LabInputStepBuffer` | `Capture/Live/LabInputStepBuffer.cs` (new) | Step begin/end, event store, dropped/repeated counts, noise learning/sampling, pointer motion totals. |
| `ILabInputCapture` | `Capture/Live/ILabInputCapture.cs` (new) | labui contract: `Devices`, `Unavailable`, `Now`, `SwallowShortcuts`, `Detailed`, `Activity`, `SuspendResume`, `BeginStep`, `EndStep`, `NoiseMap`, `RescanHidCollections`. |
| `DeviceLabApplication(DeviceLabPathBoundaries, string executable)` | `Application/DeviceLabApplication.cs` | Same workflows; read-probe gate on live inventory. |

### Dissolved or split files: old symbol to new owner

`Worker/LabWorkerHost.cs` (static class becomes instance; nothing deleted)

| Symbol | New owner |
| --- | --- |
| `Mode`, `StreamTimeout`, `AcknowledgeTimeout` | `LabWorkerHost` constants (unchanged) |
| `WireOptions` | `LabWorkerProtocol.Json` (same settings) |
| `Output` lock, `Write` | instance writer lock in `LabWorkerHost` |
| `Run(args)` authentication part | `LabWorkerHost.Run(args)` static entry using `SelfWorkerAuthorization.ReadSecretAsync` with the 5 s deadline |
| loop, `queue`, `dispatcher`, `watchdog`, `finally` | instance `Run()` |
| `Dispatch`, `Handle`, `Reply`, `ZeroStale`, `Option` | instance methods |
| `Result(JsonElement?)` | `LabWorkerProtocol.Result` |
| `LabWorkerService` record and `Arg<T>` | `Worker/LabWorkerService.cs` (adds resolved `Methods`, `Snapshot`, `Zero`) |

`Capture/Live/LabRumble.Native.cs` (split)

| Symbol | New owner |
| --- | --- |
| `LabRumbleHidEndpoint`, `From` | `Transports/LabHid.cs` as `LabHidEndpoint` |
| `HidEndpoints`, `OpenForWrite`, `WriteReport`, `WriteFile` import | `LabHid` |
| `XInputConnected`, `XInputButtons`, `XInputVibrate`, XInput imports and structs | stay in `LabRumbleNative` |

`Transports/LabMsiWmi.cs` (nested class extracted)

| Symbol | New owner |
| --- | --- |
| `WmiChannel` (`Open`, `Get`, `Set`, `Invoke`, `InvokeCore`, `CallTimeout`, `_stuck`, `Dispose`) | `MsiAcpiChannel` (internal sealed, implements `ILabMsiWmiChannel`) |

`Probes/ReadProbeProfiles.cs` (one method replaced)

| Symbol | New owner |
| --- | --- |
| `MsiWmiReadProbeProfile.InvokeGetter` | `MsiAcpiChannel.Get` (opened per read as today, one active instance required) |
| `MsiWmiReadProbeProfile` constructor constants | derived from `MsiClawReadProbes.Family.Probes` metadata |

`Wizard/LabControllerInit.cs` and `Wizard/LabModeCommands.cs` (state parts only)

| Symbol | New owner |
| --- | --- |
| `LabControllerInit.StatePath`, `WritePending`, file read/delete in `RecoverControllerMode`, `HasControllerModePending` | `LabMachineState` (`Changes.ControllerMode`); `LabControllerInit.Send` takes a `record` callback and no longer touches files |
| `LabModeCommands.StatePath`, `HasPending`, `ReadPending`, `AddPending`, `RemovePending`, `WritePending` | `LabMachineState` (`Changes.ModeCommands`) |
| `LabControllerInit.HasPending` | `LabRecovery` (reads one record) |

`Gui/WizardWindow.cs` recovery block (labui owns the window; logic moves here)

| Symbol | New owner |
| --- | --- |
| `Start` recovery sequence (`:149-180`) | `LabRecovery.RunAsync` |
| `RecoverControllerInitAsync` (`WizardWindow.Hardware.cs:101-139`) | `LabRecovery` |

`Wizard/LabPowerRecovery.cs`: `RestoreRecorded` (start hook) moves into `LabRecovery`; `RestorePower`, `Record`,
`ClearAura`, `Same`, `LabPowerChanges` stay.

Three folder computations (`LabControllerInit.cs:66`, `LabModeCommands.cs:344`, `LabWmiFirmwareEvents.cs:280`) and
`LabMachineState.ForCurrentUser`: one `LabMachineState.StateDirectory` (path unchanged, so existing records are found).

### API changes and consumers (all `internal`)

| Change | Consumers |
| --- | --- |
| `LabWorkerHost` instance + static entry | `Program.cs` (unchanged call), tests |
| `ILabWorkerClient` | labui `WizardSession`, `RumbleSession`, power stages; `LabPowerRecovery`, `LabRumbleRecovery`, `LabRecovery` signatures take the interface |
| `LabWorkerHost.WireOptions` to `LabWorkerProtocol.Json` | `LabWorkerClient`, `LabWorkerSession`, tests |
| `LabMachineState.Read()` returns `LabMachineRead` (state + changes); `Changes` convenience for callers that accept failure display | `WizardWindow` (labui), `HidHideAllowance`, `LabPawnIo`, `LabPowerRecovery`, `LabRumbleRecovery`, tests |
| `LabMachineChanges.ControllerMode`, `.ModeCommands` | `LabControllerInit`, `LabModeCommands`, `LabRecovery` |
| `LabControllerInit.Send(plan, record, token)` | `LabCuratedInitWorker` (record callback is a no-op in the worker; the wizard's checkpoint persist records the original mode), `LabModeCommands.Start` |
| `LabRumbleNative` HID members to `LabHid` | `LabRumbleRoutes`, `LabAuraLighting`, `LabClawLighting`, `LabControllerInit`, `LabModeCommands`, `LabRumblePad` (XInput stays), tests `LabRumbleRoutesTests`, `LabRumbleHidLayoutTests` |
| `MsiAcpiChannel` | `LabMsiWmi.Open`, `MsiWmiReadProbeProfile` |
| `DeviceLabApplication(boundaries, executable)` | `DeviceLabCli`, `MainWindow` (labui B4), tests |
| `ILabInputCapture` | labui `WizardSession`/stages |

Wire format, evidence JSON names, CLI output, exit codes and UI strings do not change.

## 5. Implementation batches

All batches: `dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj -c Release` green after each; no process, device,
PawnIO, HidHide or live machine state in tests (temp roots, fake channels, in-process pipes).

### LABCORE-B0 Execute A02_03 (admitted, about 120 lines)

As specified in `batches/A02_03.md`, with R4 (drop the OR clause; keep `_zeroFailure` for log de-duplication).
Filter: `FullyQualifiedName~LabWorkerSessionTests|FullyQualifiedName~LabRumbleWorkerTests`. Dependencies: none.

### LABCORE-B1 Worker host instance and admission fixes (about 1,100 lines)

Files: `Worker/LabWorkerHost.cs`, `Worker/LabWorkerSession.cs`, `Worker/LabWorkerCalls.cs`, `Worker/LabWorkerProtocol.cs`,
new `Worker/LabWorkerService.cs`, new `Worker/ILabWorkerClient.cs`, `Worker/LabWorkerClient.cs`, new
`Application/SelfWorkerProcess.cs`, `Probes/ReadProbeWorkerSupervisor.cs` (launcher uses it), tests
`Worker/LabWorkerHostTests.cs` (new), `LabWorkerSessionTests.cs`, `LabWorkerClientTests.cs`.
Steps: instance host over `TextReader`/`TextWriter`/`TimeProvider` (LABCORE-012); per-session lock and `TryEnter`
watchdog (002); refuse pre-cancelled calls, drop after EOF (004/005); stale-frame drop (006); `Release` mismatch throws
(013); unacknowledged checkpoint replaceable (014); resolved method table with validation (015); client `_lost`
re-check and `finally` sweep (016); shared launcher and secret deadline (017); `ILabWorkerClient`.
Tests: a blocked call on session A does not delay zero on session B; a busy session is skipped, not zeroed
concurrently; cancel before dispatch writes nothing; EOF with a queued write never invokes it and still zeroes and
disposes; a frame older than 300 ms is dropped; release with a wrong token fails; a second checkpoint after a failed
persist succeeds; every `LabWorkerServices.All` entry validates (replaces the attribute test, 037); client sees
`LabWorkerLostException` at once when the reader ended first; rethrow maps each error type.
Filter: `FullyQualifiedName~WSGM.DeviceLab.Tests.Worker`. Dependencies: B0. labui B5 consumes `ILabWorkerClient`.

### LABCORE-B2 Machine record and start-up recovery (about 1,000 lines)

Files: `Wizard/LabMachineState.cs`, new `Wizard/LabRecovery.cs`, `Wizard/LabPowerRecovery.cs`,
`Wizard/LabRumbleRecovery.cs`, `Wizard/LabControllerInit.cs`, `Wizard/LabModeCommands.cs`,
`Transports/LabCuratedInitWorker.cs`, `Capture/Live/LabWmiFirmwareEvents.cs` (folder path only),
`Wizard/HidHideAllowance.cs`, `Wizard/LabPawnIo.cs` (typed read callers), `Gui/WizardWindow.cs`/`WizardWindow.Hardware.cs`
(call `LabRecovery`; coordinate with labui B5), tests `Wizard/LabMachineStateTests.cs` (new),
`LabRecoveryTests.cs` (new), `LabPowerRecoveryTests.cs`, `PreflightTests.cs`, `LabPawnIoTests.cs`.
Steps: typed read and guarded update (001); fold controller-mode/mode-command records with a one-time migration of
existing side files (write merged record durably, then delete the side files; failure keeps them) (010); curated-init
checkpoint persists the original mode, worker writes nothing; single-match controller collections (029); `LabRecovery`
with per-item containment and lazy worker start (011); merged pending predicate (033).
Tests: unreadable file is never overwritten and is reported; corrupt file is preserved aside and not loaded; absent
seeds an empty record; side files are folded once and survive a failed fold; recovery runs every item when one throws;
no worker is created when nothing worker-bound is pending; rumble route gone is forgotten; mode-command undo failure
keeps the record; two matching collections refuse.
Filter: `FullyQualifiedName~LabMachineState|FullyQualifiedName~LabRecovery|FullyQualifiedName~LabPowerRecovery|FullyQualifiedName~PreflightTests|FullyQualifiedName~LabPawnIo|FullyQualifiedName~LabModeCommands`.
Dependencies: B1 (`ILabWorkerClient`); labui B5 (window calls `LabRecovery`; if B5 is later, B2 replaces the inline
block in place).

### LABCORE-B3 Read probes and the MSI_ACPI channel (about 450 lines)

Files: new `Transports/MsiAcpiChannel.cs`, `Transports/LabMsiWmi.cs`, `Probes/ReadProbeProfiles.cs`,
`Application/DeviceLabApplication.cs`, tests `Probes/ReadProbeTests.cs`, `Wizard/LabMsiWmiTests.cs`.
Steps: extract channel (007); probes call it (null-input methods handled); live-inventory gate (008); descriptor from
metadata (034); fan containment in `LabMsiWmi.Original` (021); delete the arithmetic test (037).
Tests: a fake channel whose `Get_WMI` takes no input returns a sample, not a crash; an imported inventory that
matches but a live inventory that does not refuses the probe; MSI checkpoint with an unreadable fan flag still
captures power and charge.
Filter: `FullyQualifiedName~ReadProbe|FullyQualifiedName~LabMsiWmi`. Dependencies: B6 if `DeviceLabApplication`'s
constructor changes there first (either order works; the later batch adapts).

### LABCORE-B4 Transport tidy (about 550 lines)

Files: new `Transports/LabHid.cs`, `Capture/Live/LabRumble.Native.cs`, `LabRumbleRoutes.cs`, `LabRumblePad.cs`,
`Transports/LabAuraLighting.cs`, `LabClawLighting.cs`, `LabIntelKx.cs`, `LabPawnIoModule.cs`, new
`Transports/LabPins.cs`, `Wizard/PawnIoSetup.cs`, `LabControllerInit.cs`, `LabModeCommands.cs`, `LabPowerGate.cs`,
`ManagerConflicts.cs`, `Worker/LabWorkerProtocol.cs` (+`LabPowerLog` move), tests touching moved names.
Steps: HID helper move (024); pins (023); KX folder cleanup (022); manager flag (033); `LabPowerLog` to `Worker/`
(032); Claw lighting layout argument (025) only if labui B10 has added the lighting kind to `LabPowerPlan`,
otherwise leave it to labui B10.
Tests: `LabIntelKx` parse of `Msr Data`/`Return` lines; `LabAmdSmu.CommandsFor` equals HC's table; pins read from the
embedded lock files.
Filter: `FullyQualifiedName~LabRumble|FullyQualifiedName~LabClawLighting|FullyQualifiedName~LabIntelKx|FullyQualifiedName~LabAmdSmu|FullyQualifiedName~PreflightTests`.
Dependencies: B2 (both touch `LabControllerInit`/`LabModeCommands`; do B2 first).

### LABCORE-B5 Capture step buffer, interface and evidence limits (about 750 lines)

Files: new `Capture/Live/LabInputStepBuffer.cs`, new `Capture/Live/ILabInputCapture.cs`, `Capture/Live/LabInputCapture*.cs`,
`Capture/Redaction.cs`, `Wizard/LabSystemDump.cs`, `.DeviceTree.cs`, `.Acpi.cs`, `.Smbios.cs`,
`Capture/Live/LabWmiFirmwareEvents.cs`, tests `Capture/LabInputStepBufferTests.cs` (new), `RedactionTests.cs`,
`Wizard/LabSystemDumpTests.cs`.
Steps: move `Record`, `BeginStep`, `EndStep`, `NoiseMap`, `OnHidReport` decision logic and the counters into the buffer
(P9); `LabInputCapture : ILabInputCapture` keeps threads, hooks, WMI and polling; remove silent WMI/HID truncation and
the issue/device/table/structure count caps (027); whole-token account redaction (028); start/dispose orphan fix (031).
Recorded output for in-bound inputs is byte-identical.
Tests: baseline learns noise offsets; noise-only reports are sampled one in 50; repeats are counted; a step's events and
counts reset; a 600-byte report is stored whole; a WMI event with 40 properties keeps all; "Win Max 2" survives a user
named "Max" while `C:\Users\Max` is redacted.
Filter: `FullyQualifiedName~LabInputStepBuffer|FullyQualifiedName~Redaction|FullyQualifiedName~LabSystemDump`.
Dependencies: Q3 decides whether `MaximumEventsPerStep` stays; labui B5/B7 consume `ILabInputCapture`.

### LABCORE-B6 Application, CLI and process hygiene (about 500 lines)

Files: `Program.cs`, `Application/DeviceLabApplication.cs`, `DeviceLabEnvironment.cs`, `LabTrace.cs`,
`Preflight/DeviceLabDoctor.cs`, `Cli/DeviceLabCli.cs`, `Capture/ObserveOnlyCaptureWorkflow.cs`,
`Probes/ReadProbeWorkerSupervisor.cs`, tests `Cli/CliArgumentsTests.cs`, `Application/*`, `Preflight/*`.
Steps: one boundaries instance and facade from `Program` (020/040; coordinate labui B4 which edits `Program`/`App`);
one CI predicate and one elevation helper (019); elevated log link/reparse refusal (018); merge duplicate staging tests
(037).
Tests: `CI=1` refuses `capture run`; read-probe session boundaries equal the CLI's; an elevated log path that is a link
falls back to temp (fake file system check on a temp hard link).
Filter: `FullyQualifiedName~Cli|FullyQualifiedName~Application|FullyQualifiedName~Preflight`.
Dependencies: labui B4 (`Program`); order with B3 either way.

### LABCORE-B7 Decision-gated (size depends on answers)

- Q1 `.wsgmcap`: if retired, remove `capture run`, `inspect`, `compare`, `correlate`, `fixture extract`, the passive
  timeline/correlation and bundle streams, keep inventory scaffolding (about 3,000 lines removed, docs updated). If
  kept, no change.
- Q2 licensing: move the four linked files to `src/Shared/Interop/` with SPDX headers chosen by the maintainer; both
  projects link them; no code change.
- Docs: AGENTS/README boundary wording (026, 035, P6) goes with the final documentation pass; instruction files need
  the maintainer's sign-off.

Order: B0, B1, then B2, B3, B5, B6 in any order (B2 before B4). Line counts include moves and tests.

## 6. Risks and open questions

Risks:

- Worker behavior changes touch the only path that writes hardware. Mitigation: B1's in-process host tests cover each
  admission rule with fake services before any attended run; wire format and service interfaces stay identical.
- Folding recovery side files must not lose a pending restore. Mitigation: merged record is written durably before any
  side file is deleted; a failed fold leaves both and is retried at the next start.
- The curated Xbox Ally X record claims readback for TDP, mode and curves; the maintainer's no-readback note says the
  Ally cannot read back. If the Lab run shows readback failing, the ASUS TDP test refuses without a stable snapshot
  (`LabAtkAcpi.cs:567-601`) and produces no write evidence. That is the Lab's safe behavior (no restorable original),
  not a change for this refactor.

Open questions:

1. `.wsgmcap` observe-only capture records only an inventory marker and `correlate` can never find anything on a real
   capture (LABCORE-009). Retire `capture run`/`inspect`/`compare`/`correlate`/`fixture extract` (keeping
   `scaffold --from` on inventory), or keep them frozen?
2. Device Lab is MIT but compiles four GPL product interop files (A02-F022). Relicense those files as MIT shared
   declarations, or keep the arrangement and document it?
3. Per-step capture storage drops events past 6,000 and samples noise one in 50 (counted, documented in AGENTS).
   Keep as the documented exception to "no arbitrary limits", or record every event?
4. Should a second wizard launch be refused (one `Local\` named-object check at start, a visible message) rather than
   adding cross-process locking to the machine record (LABCORE-030)?
