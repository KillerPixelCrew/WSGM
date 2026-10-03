# LABCORE adversarial verification

Verifier: Claude. Baseline: master `1329813f`, read-only. Nothing was built, run or mutated. Every cited file below
was read at the cited lines; callers were traced with rg.

Scope read in full for this pass: `Worker/**`, `Application/{DurableFile,LabTrace,DeviceLabEnvironment,DeviceLabPaths,
DeviceLabExecutable,SelfWorkerAuthorization,SelfWorkerProtocol,WorkerJobObject}.cs`, `Probes/{ReadProbeProfiles,
ReadProbeWorker}.cs`, `ReadProbeWorkerSupervisor.cs` (launcher and run), `Preflight/WindowsPreflightInspection.cs`,
`Wizard/{LabMachineState,HidHideAllowance,LabPawnIo,LabPowerRecovery,LabRumbleRecovery,LabControllerInit,
LabPowerLoad}.cs`, `LabModeCommands.cs` (state, Locate, Start, Stop, Undo, recovery), `Transports/{LabRumbleWorker,
LabCuratedInitWorker,LabAmdSmu}.cs`, `LabAtkAcpi.cs` (Set, Restore, Snapshot, Call), `LabMsiWmi.cs` (Original,
writes, channel), `LabIntelKx.cs` (Open, MSR), `LabClawLighting.cs`, `Capture/Live/{LabInputCapture,
LabRumble.Native,LabRumbleStream}.cs`, `LabInputCapture.Native.cs` (message loop, hooks), `LabRumbleRoutes.cs`
(outputs), `LabWmiFirmwareEvents.cs` (quarantine), `ObserveOnlyCaptureWorkflow.cs` (closed source), `Redaction.cs`
(account names), `Cli/DeviceLabCli.cs` (capture run, test hardware, facade), `Program.cs`, the wizard start and
hardware partials, the rumble slider page, and the worker/probe/durable-file tests.

General correction: several line citations in labcore.md do not match the file at `1329813f`. The content of each
finding still holds; the right lines are: `LabWorkerSession.cs` `ZeroQuietly` 185-199 (cited 512-526), `Release`
167-173 (494-500), `Checkpoint` 127-142 (456-459), `Method` 201-205 and attribute scans 134-136/188-189 (461-463,
515-516, 528-532); `LabWorkerCalls.Begin` 27-42 (145-160); `LabTrace` `Choose`/`RotateIfLarge` 118-163 (308-353);
`LabRumbleRecovery` `Reserve` :30 and the catch :56 (178, 204); `PawnIoSetup.ReadPin` 277-296 (435-453);
`LabInputCapture.Dispose` quit post :217-220 (203-206); the read-probe CWD-only root rule
`ReadProbeWorkerSupervisor.cs:332-333` (408-412). The plan citations (refactor-plan.md:72, 99, 111, 115, 155) are right.

## Refuted

None outright. Every finding describes code that behaves as stated. Several do not hold at their stated severity or
need a different (simpler) remedy; those are in Corrected.

## Corrected

- **LABCORE-001** (high to medium). Behaviour confirmed: `Read` swallows IO/JSON/ACL errors as an empty record
  (`LabMachineState.cs:72-88`) and `Update` replaces the file with `change(Read())` (`:93-105`). Likelihood is lower
  than stated: writes are staged and durably moved, so corruption needs disk damage, and `File.ReadAllText` shares
  read with typical scanners. Simpler remedy than a four-state enum, quarantine copy and new notice: stop swallowing in
  `Read` (missing file stays "none", every other failure throws), so `Update` can never overwrite what it could not
  read and the existing page error shows the failure. That keeps the `Read()` signature, which also removes the B2
  file-list problem below. labui.verify raised LABUI-027 to medium for the same code.
- **LABCORE-002** (high to low). The lock and timer behaviour is real (`LabWorkerHost.cs:91,166-169,304-313`), but
  (a) "hundreds of thread-pool threads" overstates it: blocked callbacks queue every 100 ms, while the pool injects
  threads only gradually past its minimum, so a 60 s call parks dozens, ending when the client's 60 s deadline kills
  the worker; (b) no current flow streams on one session while another session runs a slow call. Streaming only
  happens on the rumble slider page, where the only other requests are that session's 250 ms `stream-status` polls
  (`WizardWindow.Rumble.cs:385-405`), and a zero on the session that is mid-call would be a concurrent write anyway.
  Simplest fix: `ZeroStale` takes the existing lock with `Monitor.TryEnter` and returns when it is busy. That removes
  the pile-up with one line and no per-session locks. Per-session locking adds mechanism for a scenario the wizard
  never produces (simplify rule).
- **LABCORE-003** (high, confirmed with one correction). The disarm-before-zero and the missing `StreamError` are real
  and contradict AGENTS ("a failed zero leaves the safety zero armed"). The claim that another inner exception type
  kills the worker from the timer thread is not reachable today: the only `[LabWorkerZero]` method is
  `LabRumbleWorker.Zero` (`LabRumbleWorker.cs:116-134`), and every output converts its failure to
  `LabRumbleWriteException`, which derives from `InvalidOperationException` (`LabRumbleRoutes.cs:80,434-541`). A02_03's
  broadened catch is still right as a guard.
- **LABCORE-004** (medium to low). The behaviour is real, but invoking a call whose token was cancelled before dispatch
  matches the Lab's documented contract ("Cancelling only ends a wait", `LabWorkerCalls.cs` remarks; AGENTS worker
  bullet), and the client still awaits and reports the reply (`LabWorkerClient.cs:251-293`). refactor-plan.md:111
  describes WSGM runtime commands, not the Lab worker. Because the wizard runs one blocking operation at a time, the
  queue rarely holds more than stream frames. Refusing pre-cancelled calls is a behaviour change for the maintainer to
  accept, not a defect fix. One side effect to weigh: `SendCuratedInitAsync` records `CuratedInitRecordId` before the
  call (`WizardWindow.Hardware.cs:84-87`). A refused call then reports "a controller setup stopped before its result was
  known" on the next start, although nothing was sent. The EOF-drain half duplicates A02-F014 and LABCORE-005.
- **LABCORE-005** (wording). "Usually ends in a job kill" is wrong: at `Dispose` the queue is normally empty and the
  worker exits at once. The kill happens only when a call is still running past 5 s. The remedy (no new deadline) stands.
- **LABCORE-006** (medium to nit; drop the mechanism). Frames queue only behind a slow request on the single lane, and
  on the slider page the only other requests are fast status polls. Replayed frames are earlier values of a slider the
  tester is still moving, and the stream's own idle stop and final zero (`LabRumbleStream.cs:81-122`) still apply. The
  arrival timestamp and age filter add state without a concrete defect. With the corrected LABCORE-002 fix (watchdog
  skips while busy) there is nothing for a backlog to undo. Remove from B1.
- **LABCORE-014** (low to nit; drop). No caller reuses a session after a failed persist. Every checkpoint failure path
  disposes the proxy and returns (`WizardWindow.Power.cs:364-385`, `WizardWindow.Rumble.cs:774-792`,
  `LabPowerRecovery` `using` blocks). Remove from B1.
- **LABCORE-016** (wording). The `_lost` race window is tiny, and writing to a dead worker's stdin throws, which
  already calls `Lose`. For a `ReadLoop` that faults with an unlisted exception, only the next call waits 60 s. Its
  `Lose` then sets `_lost` and later calls fail at once, so not "every call". The `finally` sweep remedy stands.
- **LABCORE-027** (corrected scope). Confirmed caps: `LabInputCapture.cs:535` (24 properties, silently skipped),
  `:542-546` (128 bytes, 32 items, 256 characters), `:398` (512-byte report truncation), `:104-110`. Two corrections.
  (1) Keeping "per-item byte bounds on untrusted input" (8 MiB firmware table `LabWmiFirmwareEvents.cs:42`, 64 KiB
  device property) conflicts with the recorded no-arbitrary-limits rule (type checks only, chunk payloads). Either
  list them for the maintainer as explicit exceptions or remove them; do not keep them silently as "IO safety".
  (2) Missing from the list: `LabTrace` rotates at 4 MiB and overwrites the previous log (`LabTrace.cs:29,151-163`),
  which discards older diagnostic content at start-up.
- **LABCORE-028** (low; needs sign-off). The over-replacement is real (`Redaction.cs:139-160`), but whole-token
  matching weakens a privacy redactor whose output leaves the machine. A user "Max" whose name appears inside
  "MaxGaming" or a share name would then be exported. Present it as a maintainer decision (evidence fidelity vs.
  privacy), not a bug fix inside B5.
- **LABCORE-031** (low, worse than stated). Besides the missed `WM_QUIT`, a late message thread calls `_ready.Set()`
  after `Dispose` disposed `_ready` (`LabInputCapture.cs:224`, `LabInputCapture.Native.cs:118,153-156`). The
  `ObjectDisposedException` is thrown inside the thread's catch block, so it escapes the background thread and ends
  the process. Fix with the same change: do not dispose `_ready` until the thread has been joined.
- **LABCORE-037** (wording). `ReadProbeSupervisor_OutlivesTheWorkersSemanticDeadline` calls `ProcessDeadline`, which
  is `timeout + 2000` (`ReadProbeWorkerSupervisor.cs:435-438`). The test is tautological but does guard against a
  regression in that helper. Deleting it is fine; it is not literally `t + 2000 > t`.
- **P15** (count). `DeviceLabPathBoundaries.ForCurrentUser` is called at 11 sites, not 7: `DeviceLabApplication`,
  `ObserveOnlyCaptureWorkflow` x2, `DeviceLabCli` x2, `MainWindow`, `WizardWindow`, `DeviceLabInventoryWorkflow`,
  `DeviceLabDoctor`, `ReadProbeWorkerSupervisor` and `PluginTestWorker`. B6 must cover the last two as well.
- **P16** (as LABCORE-002).
- **R4** (verified). After A02_03 moves `_lastFrame = MaxValue` behind a successful zero, every path that can leave a
  failed zero pending has `_lastFrame` set: the stream catch path sets it just before (`LabWorkerSession.cs:111`). The
  `close` and EOF paths dispose and remove the session anyway. The stale predicate therefore re-fires on its own, and
  `_zeroFailure` is needed only for log de-duplication. R4 is correct.

## Confirmed (ids only)

LABCORE-003 (main claim), LABCORE-007, LABCORE-008, LABCORE-009, LABCORE-010, LABCORE-011, LABCORE-012, LABCORE-013,
LABCORE-015, LABCORE-017, LABCORE-018, LABCORE-019, LABCORE-020, LABCORE-021, LABCORE-022, LABCORE-023, LABCORE-024,
LABCORE-025, LABCORE-026, LABCORE-029, LABCORE-030, LABCORE-032, LABCORE-033, LABCORE-034, LABCORE-035, LABCORE-036,
LABCORE-038, LABCORE-039, LABCORE-040; plan claims P1-P14, P17-P21; A02-F011 (mechanism), A02-F012, A02-F014,
A02-F022.

## Missed findings

### LABCORE-V-001 (medium, safety) Controller and mode-command recovery writes hardware without the owner reservation

`WizardWindow.Start` reserves nothing before `RecoverControllerInitAsync` (`Gui/WizardWindow.cs:174-180`). That method
runs `LabModeCommands.RecoverPending()` in the wizard process (HID output and feature writes, `LabModeCommands.cs`
`RecoverPending`/`Undo`). For a `controller-mode.json` it also starts the worker and switches the controller mode
(`WizardWindow.Hardware.cs:105-139`, `LabControllerInit.cs:174-210`). Power and rumble recovery both refuse unless
`DeviceLabOwnerInspector.Reserve()` succeeds (`LabPowerRecovery.cs:128`, `LabRumbleRecovery.cs:30`). Controller
recovery does not, and it runs even when the wizard is not elevated (the condition has no `_options.Elevated`). With
WSGM running and its Claw package owning the controller, the next Lab start switches the Claw's controller mode
underneath WSGM. AGENTS requires hardware writes behind the reservation. labui.verify (C13) noted the non-elevated
path, not the missing reservation. Recommendation: in `LabRecovery` (LABCORE-011/B2), take the same
`DeviceLabOwnerInspector.Reserve()` around controller and mode-command recovery and refuse with the existing "Close
WSGM, then start Device Lab again" wording. The records stay for the next start. No new state.

### LABCORE-V-002 (medium, safety) The WMI quarantine fails open, and disabling a class is not quarantined

`LabWmiQuarantine.IsBlocked` returns false when the blocked list cannot be read (`LabWmiFirmwareEvents.cs:317-329`).
`Begin` swallows a failure to write the pending marker (`:333-346`). In both cases `Watch` still enables the class
(`LabInputCapture.cs:497-512`). So a class that bugchecked the machine before (the 0x44 Ally X case AGENTS records)
is enabled again when the list is unreadable, and a crash during an enable whose marker was not written is never
remembered. Separately, `Dispose` stops every watcher with no marker (`LabInputCapture.cs:203-214`), although stopping
sends the disable request through the same `WmipSendEnableDisableRequest` path the recorded bugcheck hit, so a class
that crashes on disable is never blocked. Recommendation, which removes risk without adding state: `Begin` returns
whether the marker reached the disk; an unreadable list or an unwritten marker skips the class (`MarkUnavailable`);
wrap `watcher.Stop()` in the same `Begin(key)`/`End()`.

### LABCORE-V-003 (medium, bug/safety) An AMD original below 5 W is recorded but can never be restored; limit validation allows partial writes

`LabAmdSmu.Plausible` accepts captured limits from 3 W (`LabAmdSmu.cs:187-191`), and the stage records them as the
original (`WizardWindow.ProcessorPower.cs:100-140`). `WriteLimits` refuses anything below `MinimumTestWatts = 5`, and
it checks each limit inside the write loop (`LabAmdSmu.cs:131-148`). Consequences:

- An original STAPM of 3-4.9 W gives a test target of `max(5, ...)` = 5 W (`ProcessorPower.cs:153`), which raises the
  limit. The restore of the 4 W original then throws `ArgumentOutOfRangeException`. `RestoreAmd` keeps the record
  (`LabPowerRecovery.cs:415-421`), every later start fails the same way, and the processor stays at the test value.
- An original with slow below 5 W but STAPM and fast at or above 5 W writes STAPM and fast and then throws: a partial
  restore.

ASUS avoids this because its snapshot accepts only values `Set` can write back (`LabAtkAcpi.cs:567-583`), and MSI
writes the captured pair back verbatim (`LabMsiWmi.RestorePower`). Recommendation: one bound for capture and write
(`Plausible` uses `MinimumTestWatts`, so a sub-5 W original is refused before anything is written, as ASUS does), and
`WriteLimits` validates all three limits before the first command. Test: 4 W original refuses the test; 4.5 W slow is
refused before any command.

### LABCORE-V-004 (low, bug) Shortcut state is tracked only while shortcuts are swallowed

`KeyboardHook` evaluates `SwallowShortcuts && Shortcut(key, up)` (`LabInputCapture.Native.cs:514`), so `_windowsHeld`
and `_altHeld` (`:529-543`) are updated only while swallowing is on. A Windows key pressed during a button step and
released after the step turned swallowing off leaves `_windowsHeld = true`. When the next step turns swallowing on,
the default branch swallows every key system-wide, the wizard's Escape included, until a Windows key is pressed
again. Recommendation: always call `Shortcut` and swallow only when `SwallowShortcuts` is set. This is a reordering, no
new state.

### LABCORE-V-005 (low, containment) `RestorePower` items use different catch filters, so one item can skip the rest

Inside `LabPowerRecovery.RestorePower` the per-item filters differ:

- Claw lighting catches every exception except OOM (`:205`).
- ASUS catches only Win32, InvalidOperation and IO (`:304`).
- MSI catches `IsTransportFailure` (`:378`).
- AMD lacks IO and UnauthorizedAccess (`:415`).
- Intel lacks Timeout and Argument (`:447`).

`LabWorkerClient.Rethrow` produces Timeout, UnauthorizedAccess, Argument and ArgumentOutOfRange exceptions
(`LabWorkerClient.cs:409-427`). One of those escaping an item skips the later items and `ClearWhenEmpty`, and in
`WizardWindow.Start` it also skips rumble and controller recovery. This is the same defect class as LABCORE-011, one
level down. Recommendation: B2 uses one containment rule per item (every exception except OOM, as the Claw branch
already does); the record stays for the next start.

### LABCORE-V-006 (low, documentation) AGENTS describes the read-probe gate and worker zero rule as current behaviour

AGENTS says "A zero counts as done only after it was written; a failed zero leaves the safety zero armed", and that
compiled read probes require "an exact live device and endpoint match". The code does neither (LABCORE-003,
LABCORE-008). Recommendation: the B0 and B3 acceptance criteria cite these AGENTS sentences as the specification, so
the final documentation pass does not reword them to match broken code.

## Batch problems

- **LABCORE-B0** (A02_03 + R4): sound. The allowed files are self-contained, it builds alone, and the filter is right.
  Keep the broadened catch even though no current zero throws an unlisted type.
- **LABCORE-B1**:
  - It carries mechanism without a demonstrated defect: per-session locks (replace with `Monitor.TryEnter` in
    `ZeroStale`), the stale-frame timestamp filter (LABCORE-006) and replaceable checkpoints (LABCORE-014). This
    violates the simplify rule; drop them and the tests "blocked call on session A does not delay zero on session B"
    and "a frame older than 300 ms is dropped".
  - Refusing pre-cancelled calls (LABCORE-004) is a contract change that needs maintainer acceptance; see the curated-
    init side effect above.
  - `SelfWorkerProcess` changes the read-probe launcher (`ReadProbeWorkerSupervisor.cs`), which is unrelated to the
    worker fixes and probe-owned. Move it to B3 or B6 so B1 stays reviewable.
  - B1 builds green alone only if `ILabWorkerClient` is added without changing consumer signatures. The consumer
    switch belongs in B2 or labui B5.
- **LABCORE-B2**:
  - If `LabMachineState.Read()` changes its return type, B2 does not build. Its file list omits
    `Gui/WizardWindow.Power.cs` (6 calls) and `Gui/WizardWindow.ProcessorPower.cs` (2 calls). Use the corrected
    LABCORE-001 remedy (throw, same signature) or add those files.
  - Add LABCORE-V-001 (reservation around controller and mode-command recovery) and LABCORE-V-005 (per-item
    containment). Without them `LabRecovery` keeps both defects.
  - The dependency on B1 is only `ILabWorkerClient`; `LabRecovery` can take the concrete client and a factory, so B2
    can precede B1 if needed.
  - It edits `WizardWindow.cs` and `WizardWindow.Hardware.cs`, which labui B5 also edits. Serialize explicitly.
- **LABCORE-B3**:
  - The live-inventory gate test ("imported matches, live does not") needs a seam, because
    `WindowsInventoryCollector.Collect` is static and is called directly (`DeviceLabApplication.cs:430`). Add an
    injected collector function to the file list, or the test cannot run offline.
  - Routing probes through `MsiAcpiChannel` also brings its 3 s per-call timeout and stuck latch into the probe worker,
    which today relies on the probe deadline plus process kill. State this behaviour change in the batch.
  - Add LABCORE-V-003 (AMD bounds) here or in B4; it touches `LabAmdSmu.cs`, which B4 already lists for tests.
- **LABCORE-B4**: moving `LabPowerLog`/`LabPowerEvent` to `Worker/` changes their namespace, and the project has no
  global usings. That needs edits in `Gui/WizardWindow.ClawLighting.cs`, `WizardWindow.Power.cs`,
  `WizardWindow.ProcessorPower.cs`, `Transports/LabAtkAcpi.cs`, `LabMsiWmi.cs`, `LabAuraLighting.cs`,
  `Wizard/LabPowerRecovery.cs` and three test files, none of which are listed. Either list them or keep the namespace.
  Pure tidying with this blast radius is low value; consider dropping the move.
- **LABCORE-B5**:
  - The redaction change (LABCORE-028) needs maintainer sign-off as a privacy trade-off.
  - Keeping byte bounds on untrusted input needs an explicit no-arbitrary-limits exception from the maintainer
    (LABCORE-027 correction).
  - Add V-002 (quarantine fail-open) and V-004 (shortcut state), both in the capture files B5 already edits, and the
    corrected LABCORE-031 (do not dispose `_ready` before the join).
- **LABCORE-B6**:
  - The elevated-log link test assumes `LabTrace` can be pointed at a temp path. `LabTrace` is a static with a
    process-wide `Path` chosen from `Environment.ProcessPath`, so the batch must include a seam for the chosen folder,
    or the test cannot be written as described.
  - It must also cover the `PluginTestWorker` and `ReadProbeWorkerSupervisor` boundary sites (P15 correction).
- **LABCORE-B7**: retiring the `.wsgmcap` commands is feature removal. requirements.md item 9 says "No feature removal
  ... is authorized". Only the maintainer's explicit answer to Q1 can admit it, and the README/AGENTS command lists
  would change with it.
- **Order**: "B2 before B4" is right (both touch `LabControllerInit` and `LabModeCommands`). B3 and B6 both edit
  `DeviceLabApplication.cs`, and B5 and labui B5/B7 share `LabInputCapture`. Serialize them explicitly rather than
  "any order".
