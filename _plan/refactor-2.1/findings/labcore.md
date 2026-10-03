# Device Lab core findings

Scope: the non-GUI half of `src/WSGM.DeviceLab`, which covers the elevated hardware worker (`Worker/**`), the CLI and application facade (`Cli/**`, `Application/**`, `Program.cs`), read probes (`Probes/**`), preflight and the owner reservation (`Preflight/**`), transports (`Transports/**`), live capture (`Capture/Live/**`, `Capture/Redaction.cs`, the `.wsgmcap` pipeline) and the machine-state and recovery files under `Wizard/` (`LabMachineState`, `LabPowerRecovery`, `LabRumbleRecovery`, `LabControllerInit`, `LabModeCommands`, `HidHideAllowance`, `LabPawnIo`, `PawnIoSetup`, `LabPowerGate`, `ManagerConflicts`, `LabSystemDump*`). The wizard presentation half is the labui area. Sources: `_plan/refactor-2.1/review/labcore.md` (40 findings), its verification `labcore.verify.md` (6 missed findings, several severity and remedy corrections), the critic `_critic.md` and plan v2. Where they disagree, plan v2 and the verifier win, and that is already applied below.

Counts: 46 ids. 37 are sections below (1 high, 10 medium, 22 low, 4 nit), and 9 are no-change (listed at the end). The verifier lowered LABCORE-001 and LABCORE-002 from high, and plan v2 made 8 findings no-change. The solution check (2026-10-03) moved LABCORE-018 to no-change and lowered LABCORE-039 to low. The maintainer's decisions (`_plan/refactor-2.1/DECISIONS.md`, 2026-10-03) then dropped all security hardening, which settles LABCORE-018 as no-change, and decided D3 (Device Lab becomes GPL), which rewrites LABCORE-039. B162 in `batches.json` still lists LABCORE-018; its executor skips that part.

Plan v2 batches for this area, in execution order:

- B004 (A02_03 with R4)
- B157 (worker host)
- B158 (machine record and start-up recovery; depends on B020 and is serialized with labui B5 on `WizardWindow.cs`)
- B159 (probes, MSI_ACPI channel, AMD bounds)
- B160 (transport tidy)
- B161 (capture buffer and evidence completeness)
- B162 (application, CLI, process hygiene)
- B171 (labui lighting stage, carries LABCORE-025)
- B173 (Device Lab licence; decided: D3, relicense Device Lab as GPL, no file moves)
- B177 (documentation)

Decisions: D2 (decided: the byte bounds on plan v2's list stay and refuse, every other cap goes) and D3 (decided: Device Lab becomes GPL; the interop files are neither relicensed nor duplicated). D9 (no readback machinery outside the Claw) covers the device packages and WSGM's host paths. No finding here adds readback, re-arm or unresolved-entry machinery for a non-Claw vendor: the only verified restore a finding touches is the Claw's `controller-mode` record (LABCORE-010).

Every build step is `dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj -c Release`. Tests use temp roots, fake services and in-process pipes. They never use a device, PawnIO, HidHide, the production `Global\WSGM.DeviceOwner` mutex or the real `%LOCALAPPDATA%\WSGM Device Lab` folder. Line numbers are from `master` 1329813f and use the verifier's corrections where it gave them. Anchor by symbol.

Rules from the plan-claim check that every batch here follows:

- The owner reservation stays an unowned named mutex whose open handle is the lease (`WindowsPreflightInspection.Reserve`). Never move it to a dedicated owning thread.
- Restoring a recorded original, or zeroing a recorded rumble route, at the next start is recovery. It is not a retry of an uncertain write. A recovery item clears its record only after its readback matches, but the write itself is never gated.
- Device Lab is exempt from the shutdown phase budgets (B140). Allocation in attended capture paths is accepted. The rule against per-sample logging still holds.
- The following were rejected and must not be built:
  - a 64-request worker queue, a Busy reply or framed-input caps
  - per-session worker locks
  - a stale-frame filter
  - replaceable checkpoints
  - refusing pre-cancelled calls
  - folding or migrating the side-file records
  - a `LabPowerLog` namespace move
  - any refactor of the `.wsgmcap` pipeline
  - whole-token redaction

## High

### LABCORE-003: a failed safety zero disarms the watchdog and is not reported

- **Severity:** high (confirmed. One sub-claim corrected: an inner exception of another type cannot reach the timer thread today.)
- **Where:**
  - `src/WSGM.DeviceLab/Worker/LabWorkerSession.cs:185-199` (`ZeroQuietly`), `:176-182` (`ZeroIfStale`), `:103-123` (`Stream` catch path)
  - `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs:247-252` (`close`), `:304-313` (`ZeroStale`)
  - `src/WSGM.DeviceLab/Transports/LabRumbleWorker.cs:116-134` (the only `[LabWorkerZero]`)
  - `src/WSGM.DeviceLab/Capture/Live/LabRumbleRoutes.cs:80,434-541`
- **Problem:**
  - `ZeroQuietly` sets `_lastFrame = DateTime.MaxValue` before it invokes the zero. If the zero fails, the stale predicate never fires again, so nothing tries the zero again and the motor can stay at the last streamed value.
  - The failure is logged only for `IOException`, `InvalidOperationException` and `Win32Exception`, and is never put into `StreamError`. The wizard's 250 ms `stream-status` poll therefore never learns the stream stopped unsafely.
  - This contradicts the AGENTS rule "a zero counts as done only after it was written; a failed zero leaves the safety zero armed".
  - Today every rumble output converts its failure to `LabRumbleWriteException : InvalidOperationException`, so the "kills the worker" path is not reachable. The broadened catch below stays as a guard.
- **Best solution:** execute `batches/A02_03.md` with the R4 simplification, in `LabWorkerSession` only.
  1. In `ZeroQuietly`, set `_lastFrame = DateTime.MaxValue` only after `zero?.Invoke(instance, [])` returns normally. On success, clear `_zeroFailure`. A service without a zero keeps today's no-op.
  2. Add `private string? _zeroFailure`, used only to de-duplicate the `zero-failed` log line. Do not add A02_03's "`_zeroFailure` non-null OR stale" clause to `ZeroIfStale` (R4).
     - Every path that can leave a failed zero pending already has `_lastFrame` set. The stream catch path sets it just before the zero. `close` and EOF dispose the session anyway.
     - So the existing stale predicate re-fires every 100 ms until the zero succeeds.
  3. Catch `TargetInvocationException` with any inner exception except `OutOfMemoryException`:
     - Take the inner message.
     - Set `StreamError ??= message`.
     - Log `zero-failed` only when the message differs from `_zeroFailure`, then store it.
     - `StreamError` stays sticky after a later successful zero. The next acknowledged checkpoint clears it, as today.
  4. No timer, lock, wire or GUI change.
  - The watchdog may attempt the established neutral zero again. That is the safety rest, not a retry of an uncertain write: no nonzero frame, TDP write or controller command is ever replayed.
- **Tests:** extend `FakeService` in `LabWorkerSessionTests` with a `FailingZeros` counter and `ZeroAttempts`. Cases:
  - Stale stream, first zero fails, next `ZeroIfStale` zeroes again and succeeds (2 attempts). Later stale checks make no attempt.
  - The same failure repeated logs once and stays pending. A different message logs once more.
  - `StreamError` reports the first zero failure and stays after success until the next acknowledge.
  - `Stream_DoesNotRetryAfterAFailedFrame` still passes.
  - Filter: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabWorkerSessionTests|FullyQualifiedName~LabRumbleWorkerTests"`.
- **Plan v2:** B004.
- **Related:**
  - A02-F012, admitted batch A02_03, critic conflict 20 (keep A02_03 with R4), plan claims P7, P17 and refinement R4
  - LABCORE-002 builds on this in B157
  - LABCORE-V-006: AGENTS keeps the zero rule as the spec

## Medium

### LABCORE-001: an unreadable machine record is silently replaced

- **Severity:** medium (verifier lowered from high: writes are staged and moved durably, so corruption needs disk damage or an outside writer).
- **Where:**
  - `src/WSGM.DeviceLab/Wizard/LabMachineState.cs:72-88` (`Read`), `:93-105` (`Update`)
  - Callers:
    - `Gui/WizardWindow.cs:165,174,459,804,879`
    - `Gui/WizardWindow.Power.cs:52,162,264,269,301,1195`
    - `Gui/WizardWindow.ProcessorPower.cs:26,31`
    - `Gui/WizardWindow.Hardware.cs:110`
    - `Wizard/HidHideAllowance.cs:132,141`
    - `Wizard/LabPawnIo.cs:68,116`
    - `Wizard/LabPowerRecovery.cs:113,148,180,232,511`
    - `Wizard/LabRumbleRecovery.cs:24`
- **Problem:** `Read` returns an empty `LabMachineChanges` on `IOException`, `JsonException` or `UnauthorizedAccessException`. `Update` builds the new record from `change(Read())` and replaces the file.
  - A sharing violation (antivirus, backup) or a damaged file therefore throws away every recorded original the next time anything is recorded. That includes the Lab's HidHide allowance entry, power, charge, fan and lighting originals, pending rumble routes and `PawnIoInstalledByLab`.
  - The next start then has nothing to undo, so the HidHide entry and the power changes stay on the machine.
- **Best solution:** follow the critic conflict 16 semantics, keeping the same signature (plan v2 chose this over the review's four-state enum and quarantine copy). The rule is: missing is absent, and anything else is never written over.
  - `Read()`: if `!File.Exists(Path)`, return `new LabMachineChanges()`.
  - Otherwise read and deserialize. Let `IOException` and `UnauthorizedAccessException` propagate.
  - Wrap a `JsonException`, or a document that deserializes to null, in `new IOException($"The Device Lab machine record {Path} could not be read: {ex.Message}", ex)`. Existing `IOException` filters then handle it.
  - `Update` is unchanged in shape. Because it calls `Read()` first, it now throws before anything is staged, so an unreadable record is never overwritten. Update the XML doc of `Read` ("a missing file reads as none; any other failure throws").
  - There is no automatic quarantine copy. The file holds the only record of the originals, so the tester moves it after reading the named path in the error.
  - Callers:
    - Start-up recovery runs inside the wizard's `Run` wrapper, which already shows a failure on the page.
    - The close path (`WizardWindow.cs:1026-1033`) already catches `IOException`/`UnauthorizedAccessException` around `RestoreHidHide`, and the record stays for the next start.
    - `PowerRestorationPending()` (`WizardWindow.Power.cs:50-53`, used by `ConfirmCloseAsync`, which runs outside `Run`) has no catch today. Add one for `IOException` and `UnauthorizedAccessException` that returns `true`. An unreadable record then asks before closing instead of crashing the close.
    - The finish page check (`WizardWindow.cs:804`) and the preflight leftover check (`:459`) run inside `Run`, so the existing page error shows.
    - Every other listed call site is inside `Run` or a `Task.Run` awaited inside `Run`. Confirm each with rg while editing.
- **Tests:** new `tests/WSGM.DeviceLab.Tests/Wizard/LabMachineStateTests.cs` on a temp path:
  - A missing file reads empty and `Read` creates nothing.
  - Corrupt JSON makes `Read` throw `IOException`. `Update` throws and the file bytes are unchanged.
  - A file held open with `FileShare.None` makes `Update` throw and stays unchanged.
  - The round trip works.
  - Filter: `--filter "FullyQualifiedName~LabMachineState"`.
- **Plan v2:** B158.
- **Related:** LABUI-027 (same code, raised to medium by labui.verify), critic conflict 16, plan claim P4, LABCORE-010 (side files follow the same rule).

### LABCORE-005: worker retirement runs every queued request after the wizard is gone

- **Severity:** medium (verifier corrected the wording: the job kill happens only when a call is still running past the client's 5 s wait. The remedy stands.)
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs:130-147` (`finally`: `CancelAll`, `CompleteAdding`, unbounded `dispatcher.Join()`), `:152-176` (`Dispatch`); `Worker/LabWorkerCalls.cs` (`_closed`); `Worker/LabWorkerClient.cs:66-85` (`Dispose`: close stdin, wait 5 s, terminate job).
- **Problem:** after stdin reaches EOF, the dispatcher still executes everything already queued: `open`, `call` (including `[LabWorkerWrite]` methods that ignore their pre-cancelled token) and `stream` frames. These writes reach hardware after the wizard closed. If one of them outlives the client's 5 s wait, the job kill also skips the final zero and dispose.
- **Best solution:** reuse the existing `_closed` flag in `LabWorkerCalls`. No new state or deadline.
  - Add `public bool Closed` to `LabWorkerCalls`, reading `_closed` under `_gate`.
  - In `Dispatch`, `continue` without calling `Handle` and without a reply once `calls.Closed` is true (nobody reads the reply). Queued requests after EOF are consumed and dropped.
  - The `finally` keeps its order: `CancelAll`, `CompleteAdding`, `Join`. The join now waits only for the call that was already running, because a lane must never be zeroed under an active call. Then every session is zeroed and disposed.
  - If that running call outlives the client's 5 s, the job kill ends it as today.
  - `Begin` keeps pre-cancelling a call that races the close (the documented contract, see LABCORE-004).
- **Tests:** in `LabWorkerHostTests` (needs LABCORE-012). A fake service has a call blocked on an event, and a `[LabWorkerWrite]` call is queued behind it. Close the input, then release the block. Expect:
  - the queued write is invoked 0 times
  - each session gets one zero attempt and `Dispose`
  - `Run` returns 0
  - Filter: `--filter "FullyQualifiedName~WSGM.DeviceLab.Tests.Worker"`.
- **Plan v2:** B157.
- **Related:** A02-F014, plan claim P19, LABCORE-004 (no-change for the pre-cancelled half), LABCORE-012.

### LABCORE-007: two of five compiled MSI read probes crash on the reference Claw

- **Severity:** medium.
- **Where:**
  - `src/WSGM.DeviceLab/Probes/ReadProbeProfiles.cs:290-338` (`MsiWmiReadProbeProfile.InvokeGetter`), the `ReadProbeExecutor` catch list (about `:225-256`)
  - `src/WSGM.DeviceLab/Transports/LabMsiWmi.cs:451-580` (`WmiChannel`, null-input handling in `InvokeCore`)
  - Reference fix: `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs:186-204`
- **Problem:** `InvokeGetter` always assigns `input["Data"]`. On the A2VM, `Get_WMI` and `Get_EC` declare no in-parameters, so `GetMethodParameters` returns null and the assignment throws `NullReferenceException`.
  - That exception is not in `ReadProbeExecutor`'s catch list. It escapes to `SelfWorkerProtocol.Run`, which exits 70, and `msi.claw-a2vm.wmi-version` and `msi.claw-a2vm.ec-version` report `WorkerCrashed`.
  - The Claw plugin fixed this exact failure after a device-verified crash, and `LabMsiWmi.WmiChannel` copies that fix.
- **Best solution:** one MSI_ACPI call path shared by the worker transport and the probes. This beats patching the null check into the probe: a third copy of the same WMI call would drift again.
  - Move the nested `LabMsiWmi.WmiChannel` unchanged to `src/WSGM.DeviceLab/Transports/MsiAcpiChannel.cs` as `internal sealed class MsiAcpiChannel : ILabMsiWmiChannel`. It keeps:
    - `Open` (exactly one active instance, `Get_WMI` present)
    - `Get`, which accepts only `Get_*`
    - `Set`, which accepts only 32-byte `Set_*`
    - `Invoke` with the 3 s `CallTimeout` and the `_stuck` latch
    - `InvokeCore` with null-input handling
    - `Dispose`
  - `LabMsiWmi.Open` calls `MsiAcpiChannel.Open()`.
  - Delete `MsiWmiReadProbeProfile.InvokeGetter`. The base class takes `Func<ILabMsiWmiChannel> open`, defaulting to `MsiAcpiChannel.Open`.
  - Each profile's `ReadOnceAsync` opens one channel for its primary and corroboration reads and calls `channel.Get(method, selector)` with its compiled method and selector. It keeps its own shape checks on the bytes (32 bytes, status `0x01`, otherwise `InvalidDataException`).
  - Add a `TimeoutException` catch to `ReadProbeExecutor` that returns `Rejected` with the exception message. The run ends there, so no further call is stacked on a stuck one. The channel's `FileNotFoundException` and `InvalidDataException` already map to `PrerequisiteMissing` and `Rejected`.
  - Behaviour change to state in the commit message: probe reads now have the channel's 3 s per-call timeout inside the 5 s probe deadline, and `Get_WMI`/`Get_EC` work on the A2VM.
- **Tests:** in `Probes/ReadProbeTests.cs`, with a fake `ILabMsiWmiChannel`:
  - The WMI-version profile returns a `Version` sample from a canned `Get_WMI` response.
  - A channel whose `Get` throws `TimeoutException` yields `Rejected`, not a crash.
  - A non-`0x01` status yields `Rejected`.
  - Filter: `--filter "FullyQualifiedName~ReadProbe|FullyQualifiedName~LabMsiWmi"`.
- **Plan v2:** B159.
- **Related:** LABCORE-034 (same file), LABCORE-021 (same transport), LABCORE-038 (probe null-input coverage).

### LABCORE-008: the read-probe gate trusts the imported inventory instead of the live machine

- **Severity:** medium.
- **Where:** `src/WSGM.DeviceLab/Application/DeviceLabApplication.cs:171-232` (`RunReadProbeAsync`: `ReadInventory`, `Candidates`, `ProbeFamilyMatches`, `ProbeEndpointMatches`), `:426-432` (`RunAttendedPluginAsync` recollects live identity), `:67` (constructor).
- **Problem:** the family and endpoint gate is evaluated against the `--from` file. The probe worker only checks that one active `MSI_ACPI` instance exists. An inventory copied from a Claw therefore lets a probe run on a different MSI machine. The attended plugin path explicitly refuses to let an imported inventory authorize anything, and AGENTS requires "an exact live device and endpoint match".
- **Best solution:** gate on the live inventory, as the attended path does, through an injected collector so the test runs offline.
  - `DeviceLabApplication` gains an optional constructor parameter `Func<CancellationToken, MachineInventory>? collectInventory`. The default is `token => WindowsInventoryCollector.Collect(DateTimeOffset.UtcNow, cancellationToken: token)`.
  - `RunReadProbeAsync` keeps `_ = ReadInventory(inventoryPath, cancellationToken)` as the shape check. It then runs `var inventory = _collectInventory(cancellationToken)` and feeds that to `Candidates`, `ProbeFamilyMatches` and `ProbeEndpointMatches`.
  - `RunAttendedPluginAsync` uses the same function instead of its direct static call.
  - The CLI still requires `--from`, and its output and exit codes are unchanged.
- **Tests:** the probe is looked up in the candidates of the inventory it is given, so a live machine that is not a Claw never reaches preflight. Two cases, both with an imported inventory that matches the Claw family:
  - A fake live inventory of another machine makes `RunReadProbeAsync` throw today's `InvalidDataException` ("not a positively matched reviewed read probe").
  - A fake live inventory that matches the family but has no available `MSI_ACPI` class with the probe's method gives a `Blocked` preflight and `Run == null`.
  - Do not add a case where both inventories match: the launcher is still the real `SystemReadProbeProcessLauncher`, and a passing preflight would start a probe worker.
  - Filter: `--filter "FullyQualifiedName~ReadProbe"`.
- **Plan v2:** B159. B162 later replaces the constructor's repository-root argument (LABCORE-020) and adapts this parameter.
- **Related:** plan claim P11, LABCORE-V-006 (AGENTS keeps the live-match rule as the spec), LABCORE-020.

### LABCORE-010: controller-state records are written by the worker and located three ways

- **Severity:** medium.
- **Where:**
  - `src/WSGM.DeviceLab/Wizard/LabControllerInit.cs:66-73` (`StatePath`, `HasControllerModePending`), `:126` (`WritePending` inside `Send`), `:174-209` (`RecoverControllerMode` reads and deletes), `:349-355`
  - `src/WSGM.DeviceLab/Transports/LabCuratedInitWorker.cs:68-83` (runs `Send` and `RecoverControllerMode` in the worker)
  - `src/WSGM.DeviceLab/Wizard/LabModeCommands.cs:344-345,746,925-940,1018,1176-1214`
  - `src/WSGM.DeviceLab/Capture/Live/LabWmiFirmwareEvents.cs:280-286`
  - `Gui/WizardWindow.Hardware.cs:76-139`
- **Problem:**
  - `controller-mode.json` is written and deleted by the elevated worker process. That bypasses the checkpoint contract, in which the wizard records and the worker only writes hardware.
  - `mode-commands.json` and the WMI quarantine files are separate side files, and each file computes the folder itself.
  - IO errors in `AddPending`, `RemovePending` and `File.Delete` are uncaught. A corrupt `mode-commands.json` surfaces as a `JsonException` that no caller's filter expects.
- **Best solution:** keep the files where they are, with one path helper and no fold or migration (plan v2; the critic flagged the migration as over-engineering for a dev tool).
  1. Add `public string SidePath(string fileName)` to `LabMachineState`, returning `Path.Combine(Path.GetDirectoryName(this.Path)!, fileName)`.
     - Delete the static `StatePath` fields in `LabControllerInit` and `LabModeCommands`. Their record functions take a `LabMachineState`, and the wizard passes `_machine`.
     - `LabWmiQuarantine.Folder` uses `LabMachineState.ForCurrentUser.SidePath(...)`. That file belongs to B161, so make the edit there.
     - File names and folder are unchanged, so existing records are found.
  2. Recording moves to the wizard. Add `LabControllerInit.RecordPending(LabMachineState, LabControllerInitPlan, int originalMode)`, `ReadPending(LabMachineState)` and `ClearPending(LabMachineState)`, and remove the `WritePending` call from `Send`.
     - In `SendCuratedInitAsync`, the checkpoint callback receives the worker's snapshot (`int? original`). For a `controller-mode` plan with a non-null original, it calls `RecordPending` before it acknowledges. The plan comes from `LabControllerInit.For(record)` on the same knowledge base the worker uses. `CuratedInitRecordId` is still recorded there as today.
     - `LabModeCommands.Start` runs in the wizard. It reads `CurrentMode` and, when that reads, calls `RecordPending` before `Send` (an unreadable mode makes `Send` refuse with nothing written, as today). `session.RestoreOwed` becomes "a record was written" instead of `HasControllerModePending`.
     - A record whose mode already equals the target is harmless: recovery sees the original mode and clears it.
  3. Change the recovery signature to `ILabCuratedInitWorker.RecoverControllerMode(LabPendingControllerMode pending, CancellationToken)`. The wizard reads the record and passes it in. The worker switches the mode and returns null on a verified restore, and reads or writes no file. The wizard calls `ClearPending` after a null result, and `LabModeCommands.Stop` does the same in-process.
     - Trust is unchanged: the worker reads a user-writable file today.
  4. Side-file reads follow LABCORE-001: a missing file is none, and anything else throws `IOException` naming the file (JSON failures wrapped). `AddPending`, `RemovePending` and `ClearPending` let IO exceptions reach their callers.
     - `AddPending` runs before any write, so a failure refuses the command with nothing sent.
     - In recovery, LABCORE-011's per-item containment reports the failure and keeps the file.
- **Tests:** on a temp `LabMachineState` folder:
  - Controller-mode record written by `RecordPending`, read back and cleared by `ClearPending`.
  - An unreadable `mode-commands.json` makes `RecoverPending` report the failure and leaves the bytes unchanged.
  - `AddPending` on a corrupt file throws before any write.
  - Filter: `--filter "FullyQualifiedName~LabModeCommands|FullyQualifiedName~LabMachineState"`.
- **Plan v2:** B158 (the `LabWmiFirmwareEvents` path line in B161).
- **Related:** LABUI-027, labui C5, critic over-engineering flag "labcore addition 2", LABCORE-001, LABCORE-011, LABCORE-V-001.

### LABCORE-011: start-up recovery is inline UI code, always starts the worker and one failure skips the rest

- **Severity:** medium.
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs:145-190` (`Start`); `Gui/WizardWindow.Hardware.cs:101-139` (`RecoverControllerInitAsync`); `Wizard/LabPowerRecovery.cs:111-157` (`RestoreRecorded`); `Wizard/LabRumbleRecovery.cs:22-75`; `Worker/LabWorkerClient.cs:409-427` (`Rethrow`).
- **Problem:**
  - `WizardWindow.Start` sequences PawnIO reconcile, power, rumble and controller recovery itself.
  - It starts the elevated worker even when nothing is recorded, because `RestoreRecorded` takes a started client.
  - Each item has its own catch filter. `LabRumbleRecovery` catches only `InvalidOperationException`/`IOException`, while the client rethrows `Win32Exception`, `TimeoutException`, `UnauthorizedAccessException` and `ArgumentException`. One escaping exception aborts the later recoveries, and the notice page never shows.
- **Best solution:** add a new `src/WSGM.DeviceLab/Wizard/LabRecovery.cs`:

  ```
  static Task<IReadOnlyList<string>> RunAsync(
      LabMachineState machine,
      bool elevated,
      LabPawnIo pawnIo,
      Func<Task<ILabWorkerClient>> worker,
      Func<DeviceLabOwnerReservationResult> reserve,
      CancellationToken token)
  ```

  It returns the notice lines.
  - Order, unchanged from today:
    1. PawnIO reconcile (elevated only)
    2. Read the record and the two side files once
    3. Power (any pending)
    4. Rumble (elevated)
    5. Controller mode and mode commands
  - Take the owner reservation once, through `reserve()`, when any hardware item is pending (see LABCORE-V-001). Dispose it before returning so preflight can reserve again.
    - `LabPowerRecovery.RestoreRecorded` moves into `LabRecovery`, and `LabRumbleRecovery.RestoreRecorded` no longer reserves itself. The power stage's own restore button keeps calling `RestorePower` under its own reservation as today.
  - Call `worker()` only when worker-bound work is pending: power, rumble, or a controller-mode record or `CuratedInitRecordId`. This is LABUI-V-005.
  - Wrap every item in `catch (Exception ex) when (ex is not OutOfMemoryException)`, as the Claw lighting branch already does. The record stays, and the item adds "… could not be put back: {message}" using today's wording for that item.
  - `WizardWindow.Start` keeps only the page, the notice display and the call to `LabRecovery.RunAsync`. All strings are unchanged.
  - `LabPowerRecovery` and `LabRumbleRecovery` take `ILabWorkerClient` (from B157) so tests can fake the worker.
- **Tests:** new `LabRecoveryTests` with a temp `LabMachineState`, a fake `ILabWorkerClient` and a test reservation (`() => DeviceLabOwnerInspector.Reserve($@"Local\WSGM.DeviceLab.Test.{Guid.NewGuid():N}")`). Cases:
  - Every item runs when one throws `TimeoutException`.
  - No worker is created when nothing worker-bound is pending.
  - A route-gone rumble entry is forgotten.
  - A mode-command undo failure keeps the record.
  - `LabPowerRecoveryTests.RestoreRecorded_WithNothingRecorded_ReturnsNull` moves to `LabRecoveryTests` with the method it covers.
  - Filter: `--filter "FullyQualifiedName~LabRecovery|FullyQualifiedName~LabPowerRecovery"`.
- **Plan v2:** B158 (serialize with labui B5 on `WizardWindow.cs` and `WizardWindow.Hardware.cs`).
- **Related:** LABUI-027, LABUI-V-005, labui C13, plan claim P21, LABCORE-V-001, LABCORE-V-005.

### LABCORE-012: the worker host cannot be tested

- **Severity:** medium.
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs:49-327` (static class over `Console.In`/`Console.Out`, static `Output` lock, private static `Dispatch`, `Timer` built in `Run`); `tests/WSGM.DeviceLab.Tests/Worker/LabWorkerClientTests.cs` (spawns a real process for the handshake only).
- **Problem:** nothing covers dispatch order, cancel delivery, EOF retirement, watchdog behaviour, error-type mapping or the client's lost-worker path. Each worker fix in this area would land untested.
- **Best solution:** make `LabWorkerHost` one instance, and keep `LabWorkerSession` and `LabWorkerCalls` as they are. Do not add a registry, admission or watchdog type (plan claim P2).
  - Use `internal sealed class LabWorkerHost(TextReader input, TextWriter output, IReadOnlyDictionary<string, LabWorkerService> services, TimeProvider time)` with an instance `int Serve()`. It contains today's read loop, queue, dispatcher thread and `finally`.
  - Make `Dispatch`, `Handle`, `Reply`, `Write` and `ZeroStale` instance methods. The output lock becomes an instance field.
  - The watchdog is `time.CreateTimer(_ => ZeroStale(), null, 100 ms, 100 ms)`. `ZeroStale` stays `internal` so tests can call one tick directly.
  - Sessions get `clock: () => time.GetUtcNow().UtcDateTime`.
  - `public static int Run(IReadOnlyList<string> args)` stays the process entry. It authenticates (LABCORE-017 adds the secret deadline), then runs `new LabWorkerHost(Console.In, Console.Out, LabWorkerServices.All, TimeProvider.System).Serve()`. `Program.cs` is unchanged.
  - Add `Worker/ILabWorkerClient.cs` with exactly today's `Open<T>`, `Checkpoint<TState>`, `Release` and `StreamError` members, and make `LabWorkerClient` implement it. Consumer signatures do not change in this batch; B158 switches the recovery classes.
  - `WireOptions` and `Result` stay where they are (LABCORE-032 is no-change).
- **Tests:** new `Worker/LabWorkerHostTests.cs`. Drive the host in-process over an `AnonymousPipeServerStream`/`AnonymousPipeClientStream` pair, or use a blocking `TextReader` test helper. Use fake services registered in the dictionary and a small test `TimeProvider` subclass that overrides `GetUtcNow`. Do not add the `Microsoft.Extensions.TimeProvider.Testing` package. Cases:
  - Requests run in order.
  - A cancel reaches a running call.
  - A busy lane is skipped by the watchdog (LABCORE-002).
  - The EOF case from LABCORE-005.
  - Release with a wrong token fails (LABCORE-013).
  - Every `LabWorkerServices.All` entry validates (LABCORE-015).
  - The client sees `LabWorkerLostException` at once when its reader ended first (LABCORE-016).
  - `Rethrow` maps each error type.
  - Filter: `--filter "FullyQualifiedName~WSGM.DeviceLab.Tests.Worker"`.
- **Plan v2:** B157.
- **Related:** plan claims P2 and P14; LABCORE-002, 005, 013, 015, 016, 035 land in the same batch.

### LABCORE-V-001: controller and mode-command recovery writes hardware without the owner reservation

- **Severity:** medium (missed finding, safety).
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs:174-180`; `Gui/WizardWindow.Hardware.cs:105-139`; `Wizard/LabModeCommands.cs:1035-1066` (`RecoverPending`/`Undo`); `Wizard/LabControllerInit.cs:174-210`; compare `Wizard/LabPowerRecovery.cs:128` and `Wizard/LabRumbleRecovery.cs:30`.
- **Problem:** power and rumble recovery refuse unless `DeviceLabOwnerInspector.Reserve()` succeeds. Controller recovery reserves nothing, and it runs even when the wizard is not elevated.
  - It sends HID output and feature writes in the wizard process for mode commands.
  - It starts the worker to switch the controller mode for a `controller-mode.json`.
  - With WSGM running and its Claw package owning the controller, the next Lab start switches the Claw's controller mode underneath WSGM. AGENTS requires every hardware write to sit behind the reservation.
- **Best solution:** inside `LabRecovery` (LABCORE-011), controller-mode and mode-command recovery run only while the single recovery reservation is held.
  - When `reserve()` returns no reservation, skip both items and keep their records. Add one notice with the existing wording style: "An earlier test left a controller setting changed. Close WSGM, then start Device Lab again to put it back."
  - The elevation rules stay as today. No new state.
- **Tests:** in `LabRecoveryTests`, hold the test reservation name before calling `RunAsync`. A pending controller-mode record and a pending mode command then stay on disk, and the fake worker is never opened. Filter: `--filter "FullyQualifiedName~LabRecovery"`.
- **Plan v2:** B158.
- **Related:** LABCORE-011, LABCORE-010, labui C13 (non-elevated path).

### LABCORE-V-002: the WMI quarantine fails open, and disabling a class is never quarantined

- **Severity:** medium (missed finding, safety).
- **Where:** `src/WSGM.DeviceLab/Capture/Live/LabWmiFirmwareEvents.cs:317-329` (`IsBlocked` returns false when the list cannot be read), `:333-346` (`Begin` swallows a failed marker write); `Capture/Live/LabInputCapture.cs:490-522` (`Watch`), `:203-214` (`Dispose` stops watchers without a marker).
- **Problem:** the quarantine exists because enabling a firmware WMI class once bugchecked an Ally X (0x44). It fails open in three ways:
  - An unreadable blocked list re-enables a class that crashed the machine before.
  - A marker that never reached the disk means a crash during that enable is never remembered.
  - `watcher.Stop()` sends the disable request through the same `WmipSendEnableDisableRequest` path the bugcheck hit, but has no marker, so a class that crashes on disable is never blocked.
- **Best solution:** remove the swallowing instead of adding state.
  - `IsBlocked` drops its catch, so `IOException` and `UnauthorizedAccessException` propagate.
  - `Begin` returns `bool`: true only when the marker was written and flushed.
  - In `Watch`, an exception from `IsBlocked` gives `MarkUnavailable($"wmi {key}", $"skipped: the quarantine list could not be read ({ex.Message})")` and returns. `Begin` returning false gives `MarkUnavailable($"wmi {key}", "skipped: the quarantine marker could not be written")` and returns. The class is not enabled in either case.
  - Change `_watchers` to `List<(string Key, ManagementEventWatcher Watcher)>`. `Dispose` calls `LabWmiQuarantine.Begin(key)` before `watcher.Stop()` and `End()` after it, in a `finally`.
    - The stop runs even when `Begin` returns false. A watcher cannot be left running, and process exit would disable it anyway.
- **Tests:** these are pure file-level tests on `LabWmiQuarantine` with its folder pointed at a temp path (through the LABCORE-010 path helper):
  - An unreadable blocked list (held with `FileShare.None`) makes `IsBlocked` throw.
  - `Begin` into a read-only folder returns false.
  - `RecoverFromCrash` moves a pending name to the blocked list.
  - Filter: `--filter "FullyQualifiedName~LabWmi"`.
- **Plan v2:** B161.
- **Related:** plan claim P8 (the quarantine stays), LABCORE-010 (folder helper).

### LABCORE-V-003: an AMD original below 5 W is recorded but can never be restored, and limit validation allows partial writes

- **Severity:** medium (missed finding, bug and safety).
- **Where:** `src/WSGM.DeviceLab/Transports/LabAmdSmu.cs:187-191` (`Plausible` accepts 3 to 150 W), `:131-148` (`WriteLimits` checks each limit inside the write loop against `MinimumTestWatts = 5`, `:61`); `Gui/WizardWindow.ProcessorPower.cs:100-153`; `Wizard/LabPowerRecovery.cs:400-421` (`RestoreAmd` keeps the record on failure).
- **Problem:**
  - A captured STAPM of 3 to 4.9 W passes `Plausible` and is recorded. The test target becomes `max(5, …) = 5 W`, which raises the limit. Restoring the 4 W original then throws `ArgumentOutOfRangeException`, and `RestoreAmd` keeps the record. Every later start fails the same way, and the processor stays at the test value.
  - An original with slow below 5 W but STAPM and fast at or above it writes STAPM and fast, then throws on slow. That is a partial restore.
- **Best solution:** use one bound for capture and write, as the ASUS snapshot does (it accepts only values `Set` can write back).
  - `Plausible` uses `MinimumTestWatts` as the lower bound for all three limits. A sub-5 W original fails the existing "did not give stable, plausible limits" path before anything is recorded or written.
  - Extract `internal static void Validate(LabAmdLimits limits)`, which throws `ArgumentOutOfRangeException` if any of the three is outside `[MinimumTestWatts, 150]`. `WriteLimits` calls it before the first command, so a write is all three or none.
  - A record captured below 5 W by an older build stays reported. No special handling (greenfield dev tool).
- **Tests:** in a new `tests/WSGM.DeviceLab.Tests/Wizard/LabAmdSmuTests.cs` (no `LabAmdSmu` tests exist yet; both members are static, so nothing opens PawnIO): `Plausible(new(4, 6, 5))` is false. `Validate(new(10, 10, 4.5))` throws. `Validate(new(5, 5, 5))` passes. Filter: `--filter "FullyQualifiedName~LabAmdSmu"`.
- **Plan v2:** B159.
- **Related:** LABCORE-038 (`LabAmdSmu` coverage), LABCORE-V-005 (the restore item that kept failing).

## Low

### LABCORE-002: the global worker lock makes every watchdog tick wait behind a blocked call

- **Severity:** low (verifier lowered from high: a 60 s call parks dozens of pool threads, not hundreds, and no current flow streams on one session while another runs a slow call).
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs:91` (100 ms `Timer`), `:166-169` (`Dispatch` holds `lock (sessions)` across `Handle`), `:304-313` (`ZeroStale` takes the same lock).
- **Problem:** `Dispatch` holds the sessions lock for the whole hardware call. The timer keeps queuing `ZeroStale` every 100 ms, and each one blocks on that lock. A call blocked until its 60 s deadline therefore parks a growing number of thread-pool threads, until the client kills the job. No watchdog zero runs during that time.
- **Best solution:** in `ZeroStale`, `if (!Monitor.TryEnter(sessions)) return;` then iterate in `try`/`finally { Monitor.Exit(sessions); }`.
  - A tick that finds the lane busy returns at once. Zeroing a lane that is mid-call would be a concurrent write to the same device anyway.
  - Overlapping ticks also return.
  - No per-session locks: that mechanism serves a scenario the wizard never produces.
- **Tests:** in `LabWorkerHostTests`, while a fake call blocks the lane, call `ZeroStale()` directly. It returns immediately and makes no zero attempt. After release, a stale session is zeroed on the next tick. Filter: `--filter "FullyQualifiedName~WSGM.DeviceLab.Tests.Worker"`.
- **Plan v2:** B157.
- **Related:** A02-F011 (critic notes the verifier's `TryEnter` replacement), plan claim P16, LABCORE-003, LABCORE-012.

### LABCORE-013: `Release` with a wrong token reports success

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerSession.cs:167-173` (`Release`); `Worker/LabWorkerHost.cs:243-246` (replies `Ok`).
- **Problem:** a mismatched or stale token is ignored and the wizard gets `Ok`. The wizard then believes writes are disarmed while the checkpoint is still armed. `Acknowledge` throws in the same case.
- **Best solution:** `Release` throws `new InvalidOperationException("The checkpoint release does not match.")` when `_armed is null || token != _armed`, mirroring `Acknowledge`. The host's existing catch turns it into an error reply. The 9 `worker.Release` call sites each release their own token once after a matching checkpoint; confirm this with rg while editing.
- **Tests:** in `LabWorkerSessionTests`, release with a wrong token throws and `Armed` stays true. A second release after a successful one throws. Filter: `--filter "FullyQualifiedName~LabWorkerSessionTests"`.
- **Plan v2:** B157.
- **Related:** LABCORE-012.

### LABCORE-015: service methods are resolved by reflection on every call and never validated

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerSession.cs:201-205` (`Method` scans `GetMethods()` per call and per stream frame), `:134-136`, `:188-189` (attribute scans for snapshot and zero); `Worker/LabWorkerHost.cs:18-34` (`LabWorkerService`); `Worker/LabWorkerServices.cs`.
- **Problem:** overloads, duplicate names, a missing snapshot or a missing zero are discovered only when a call arrives. Every stream frame repeats the scan.
- **Best solution:** move `LabWorkerService` to `Worker/LabWorkerService.cs` and resolve everything once at construction.
  - Add a constructor body that builds `IReadOnlyDictionary<string, LabWorkerMethod> Methods`. `LabWorkerMethod` is `(MethodInfo Info, bool Write, bool Stream, bool Sampled)`.
  - Throw `InvalidOperationException` naming the service and method on an overload (duplicate name). Also throw on more than one `[LabWorkerSnapshot]` or `[LabWorkerZero]`.
  - Expose `MethodInfo? Snapshot` and `MethodInfo? Zero`.
  - `LabWorkerSession` uses the table and does no reflection per call.
  - `Interface.GetMethods()` on an interface excludes inherited `IDisposable.Dispose`, so `Dispose` stays uncallable. Keep that property.
- **Tests:** one test enumerates `LabWorkerServices.All` and asserts every entry constructs. This replaces `HardwareWorker_RegistersRumbleAndCuratedInitWithCheckpointedWrites` (LABCORE-037). A test interface with an overload throws at construction. Filter: `--filter "FullyQualifiedName~WSGM.DeviceLab.Tests.Worker"`.
- **Plan v2:** B157.
- **Related:** LABCORE-035 (the `Sampled` flag), LABCORE-037.

### LABCORE-016: the client can wait 60 s on a worker that already died

- **Severity:** low (verifier corrected the wording: after one wait, `Lose` sets `_lost` and later calls fail at once).
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerClient.cs:256-263` (`Send` checks `_lost` before it registers the reply), `:370-393` (`ReadLoop` catches only `IOException`, `JsonException`, `ObjectDisposedException`, then sweeps).
- **Problem:**
  - If `ReadLoop` finishes between the `_lost` check and `_pending[id] = reply`, its sweep misses the new entry and the caller waits the full 60 s deadline.
  - If `ReadLoop` faults with any other exception, the task ends without setting `_lost` or sweeping, so the next call also waits 60 s.
- **Best solution:**
  - In `Send`, after `_pending[id] = reply;`, add `if (_lost is { } lost) { _pending.TryRemove(id, out _); throw new LabWorkerLostException(lost); }`.
  - In `ReadLoop`, move the `_lost ??= …` and the sweep into a `finally`, keeping the existing catch for the expected types. An unexpected exception still surfaces through `LabTrace`'s unobserved-task handler.
- **Tests:** a client over a stub process whose output ends at once. `Send` throws `LabWorkerLostException` immediately instead of after the deadline. The `ReadLoop` sweep fails every pending call. If spawning is needed, reuse the existing `LabWorkerClientTests` harness. Filter: `--filter "FullyQualifiedName~LabWorkerClientTests"`.
- **Plan v2:** B157.
- **Related:** LABCORE-012.

### LABCORE-017: the hardware worker re-implements self-worker launch and reads its secret with no deadline

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs:70-73` (`ReadSecretAsync(handle, CancellationToken.None)`); `Application/SelfWorkerProtocol.cs:30,85-91` (5 s `AuthorizationDeadline`); `Worker/LabWorkerClient.cs:97-171` and `Probes/ReadProbeWorkerSupervisor.cs:44-140` (two copies of start, job assignment, inheritable pipe and secret delivery).
- **Problem:** a worker whose authorization pipe never delivers waits forever. The launch sequence exists twice with small differences (redirects, error handling), so a fix to one drifts from the other.
- **Best solution:**
  - Add `Application/SelfWorkerProcess.cs` with `internal static (Process Process, WorkerJobObject Job) Start(string fileName, IReadOnlyList<string> arguments, ReadOnlySpan<byte> secret, bool redirectInput)`. It does exactly today's shared steps:
    - create the job
    - create the inheritable `AnonymousPipeServerStream`
    - append `--authorization-handle <h>`
    - start the process and assign the job
    - dispose the local client handle
    - write, flush and dispose the pipe
    - On failure it disposes both and rethrows.
  - `LabWorkerClient.Start` and `SystemReadProbeProcessLauncher.RunAsync` call it. Each keeps its own redirects, greeting or result handling, and error mapping.
  - Move the 5 s constant to `SelfWorkerAuthorization.AuthorizationDeadline`. `LabWorkerHost.Run` reads the secret under `CancelAfter(AuthorizationDeadline)`, like `SelfWorkerProtocol.AuthorizeAsync`.
  - The stdin hello line stays: it is the pipe protocol's first frame.
- **Tests:** the existing handshake test in `LabWorkerClientTests` still passes. Probe launcher tests in `ReadProbeTests` still pass. Filter: `--filter "FullyQualifiedName~Application|FullyQualifiedName~LabWorkerClientTests|FullyQualifiedName~ReadProbe"`.
- **Plan v2:** B162 (moved out of B157 so the worker batch stays reviewable).
- **Related:** LABCORE-012.

### LABCORE-019: CI and elevation checks have different meanings in different places

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Cli/DeviceLabCli.cs:184-188` (`capture run`), `:471-472` (`test hardware`), both accepting only `CI == "true"`; `Application/DeviceLabEnvironment.cs:11-30` (accepts 1/true/yes and `GITHUB_ACTIONS`); `Application/LabTrace.cs` `Elevated()`; `Preflight/DeviceLabDoctor.cs:221` `IsElevated()`.
- **Problem:** `CI=1` passes the `capture run` interactive gate, while the plugin worker refuses it under the broad rule. Elevation is computed three times.
- **Best solution:**
  - Both CLI gates call `DeviceLabEnvironment.IsContinuousIntegration()`.
  - Add an internal overload `IsContinuousIntegration(Func<string, string?> variable)` for tests.
  - Delete `LabTrace.Elevated` and `DeviceLabDoctor.IsElevated` and call `DeviceLabEnvironment.IsElevated()`.
  - Refusal texts and exit codes are unchanged.
- **Tests:** in `Application` tests, the overload returns true for `CI=1`, `CI=yes`, `CI=TRUE` and `GITHUB_ACTIONS=true`, and false for unset or `0`. No process-wide environment mutation. Filter: `--filter "FullyQualifiedName~Application|FullyQualifiedName~Cli"`.
- **Plan v2:** B162.
- **Related:** LABCORE-018 (no-change; `LabTrace.Elevated` is deleted here).

### LABCORE-020: path boundaries are rebuilt ambiently with two repository-root rules

- **Severity:** low.
- **Where:** `DeviceLabPathBoundaries.ForCurrentUser` at 11 sites:
  - `Application/DeviceLabApplication.cs:480`
  - `Capture/ObserveOnlyCaptureWorkflow.cs:209,412`
  - `Cli/DeviceLabCli.cs:328,404`
  - `Gui/MainWindow.cs:82`
  - `Gui/WizardWindow.cs:1112`
  - `Inventory/DeviceLabInventoryWorkflow.cs:88`
  - `Preflight/DeviceLabDoctor.cs:37`
  - `Probes/ReadProbeWorkerSupervisor.cs:332`
  - `Testing/PluginTestWorker.cs:120`
  
  Root rules: `DeviceLabCli.cs:574-578` (current directory, then base directory) versus `ReadProbeWorkerSupervisor.cs:332-333` and `WizardWindow.cs:1112` (current directory only).
- **Problem:** depending on the process's working directory, a read-probe session can see different allowed output roots than the CLI that started it.
- **Best solution:**
  - `Program` computes the repository root once with the CLI rule (`DeviceLabRepositoryLocator.Find(Environment.CurrentDirectory) ?? DeviceLabRepositoryLocator.Find(AppContext.BaseDirectory)`). It builds one `DeviceLabPathBoundaries` and passes it to whichever role runs: CLI, GUI, wizard, probe worker or plugin worker.
  - `DeviceLabApplication(DeviceLabPathBoundaries boundaries, string executable, …)` replaces the `repositoryRoot` argument.
  - The workflows (`ObserveOnlyCaptureWorkflow`, `DeviceLabInventoryWorkflow`, `DeviceLabDoctor.Run`, `LabPromote`, scaffold, `ReadProbeWorkerSupervisor`, `PluginTestWorker`) take the instance as a parameter.
  - `ObserveOnlyCaptureWorkflow` gets only the parameter change; its pipeline stays byte-for-byte (LABCORE-009).
  - Coordinate with labui B4, which edits `Program` and `App`.
- **Tests:** the read-probe session boundaries equal the CLI's for a fixed root. Filter: `--filter "FullyQualifiedName~Application|FullyQualifiedName~Preflight|FullyQualifiedName~Cli"`.
- **Plan v2:** B162.
- **Related:** LABUI-032, plan claim P15 (count corrected to 11), LABCORE-040, LABCORE-008.

### LABCORE-021: the MSI checkpoint fails entirely when only the fan flags cannot be read

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Transports/LabMsiWmi.cs:180-199` (`Original`), `:26-34` (`LabMsiOriginal`); `Gui/WizardWindow.Power.cs:707-740`.
- **Problem:** `Original` contains power and charge read failures, as its contract says ("what could be captured, and why the rest could not"). But it calls `ReadFans()` unguarded, so a fan-flag read failure fails the whole checkpoint and blocks the TDP and charge tests too.
- **Best solution:**
  - Add `public string? FansUnavailable { get; init; }` to `LabMsiOriginal`.
  - In `Original`, read the fans inside `try`/`catch (Exception ex) when (IsTransportFailure(ex))` and set `FansUnavailable = ex.Message`.
  - The power stage skips the fan test when `Fans` is null and reports `FansUnavailable` the way it reports `ChargeUnavailable`. Wording is unchanged for the other two.
  - The channel's `_stuck` latch still stops later calls after a timeout, as today.
- **Tests:** in `LabMsiWmiTests`, a fake channel that throws on the fan selectors yields an `Original` with power and charge captured and `FansUnavailable` set. Filter: `--filter "FullyQualifiedName~LabMsiWmi"`.
- **Plan v2:** B159.
- **Related:** LABCORE-007.

### LABCORE-022: `LabIntelKx.Open` leaves its admin-only folder behind on failure

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Transports/LabIntelKx.cs:172-204` (`Open`).
- **Problem:** the folder is created before the resource copy, the digest check and the MCHBAR probe. On a pin mismatch or any later failure, the stream is disposed but the folder and `KX.exe` stay, because only `Dispose` deletes them.
- **Best solution:** wrap everything from `CreateAdministratorsOnlyDirectory` to `return kx` in `try`/`catch`.
  - On failure, dispose `kx` if it was constructed, since its `Dispose` already deletes the folder.
  - Otherwise dispose `held` if it was opened and call `Directory.Delete(directory, recursive: true)` inside a catch for `IOException`/`UnauthorizedAccessException`.
  - Rethrow.
- **Tests:** none offline (needs the embedded resource and an admin folder). Covered by the B160 build and review. Filter for the batch: `--filter "FullyQualifiedName~LabIntelKx"` (the output-parsing tests from LABCORE-038).
- **Plan v2:** B160.
- **Related:** LABCORE-023, LABCORE-038.

### LABCORE-023: three readers of embedded pin files

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Transports/LabPawnIoModule.cs:126-140`; `Transports/LabIntelKx.cs:309-315`; `Wizard/PawnIoSetup.cs:277-296` (`ReadPin`).
- **Problem:** each site opens a manifest resource, parses JSON and handles a missing resource with its own exception type.
- **Best solution:** add `Transports/LabPins.cs` with `internal static class LabPins` exposing:
  - `string PawnIoModuleSha256(string id)`
  - `string KxSha256()`
  - `PawnIoPin PawnIoInstaller()`
  
  All three use one private `JsonDocument Lock(string resource)` that throws `InvalidOperationException($"The {resource} lock file is not embedded.")`. A missing embedded resource is a build defect, and no caller handles it specially; confirm with rg. The three sites call `LabPins`, and `PawnIoSetup.LazyPin` wraps `LabPins.PawnIoInstaller`.
- **Tests:** in the existing `Wizard/PreflightTests.cs`, beside `PawnIo_PinMatchesTheLockFileAndNeverAllowsUnrestrictedModules`, so B160's filter picks them up: pins read from the embedded lock files are 64-character hex, and the installer pin has a version and arguments. `PawnIoSetup.ReadPin` throws `InvalidDataException` today and `LabPins` throws `InvalidOperationException`; confirm with rg that no caller catches either for a missing resource. Filter: `--filter "FullyQualifiedName~PreflightTests|FullyQualifiedName~LabPawnIo"`.
- **Plan v2:** B160.
- **Related:** LABCORE-022.

### LABCORE-024: the general HID helper lives in rumble capture code

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Capture/Live/LabRumble.Native.cs:20-90` (`LabRumbleHidEndpoint`, `From`, `HidEndpoints`, `OpenForWrite`, `WriteReport`, the `WriteFile` import). Consumers: `LabRumbleRoutes`, `LabAuraLighting`, `LabClawLighting`, `LabControllerInit`, `LabModeCommands`, `LabRumblePad`.
- **Problem:** lighting, controller init and mode commands depend on a rumble-named capture type for plain HID enumeration and writes.
- **Best solution:** a move with no behaviour change.
  - Move that half to `src/WSGM.DeviceLab/Transports/LabHid.cs` as `LabHidEndpoint` (renamed from `LabRumbleHidEndpoint`, same members) and `internal static class LabHid` (`HidEndpoints`, `OpenForWrite`, `WriteReport`).
  - The XInput helpers, imports and structs stay in `LabRumbleNative`.
  - Update every consumer and the tests `LabRumbleRoutesTests` and `LabRumbleHidLayoutTests`.
- **Tests:** the existing suites pass unchanged. Filter: `--filter "FullyQualifiedName~LabRumble|FullyQualifiedName~LabClawLighting"`.
- **Plan v2:** B160.
- **Related:** LABCORE-029 (the single-match helper uses `LabHidEndpoint`).

### LABCORE-025: Claw lighting hardcodes curated facts in the transport

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Transports/LabClawLighting.cs:31-33` (record id `wsgm.claw-8-a2vm`), `:108-113` (VID `0x0DB0`, PIDs `0x1902`/`0x1901`, usages); compare `Wizard/LabPowerPlan.cs:129,480-484` (`LabAuraLayout`).
- **Problem:** the transport decides which device and collections it may write, when that should be data from the curated plan. Aura already takes its layout as an argument.
- **Best solution:** no schema change.
  - Add `internal sealed record LabClawLightingLayout(ushort VendorId, IReadOnlyList<LabHidCollectionId> Collections)` beside `LabAuraLayout` in `LabPowerPlan`. `LabHidCollectionId` is `(ushort ProductId, ushort UsagePage, ushort Usage, int OutputLength)`.
  - The layout is built from fields the record already has, so no schema change is needed. In `Knowledge/Devices/wsgm.claw-8-a2vm.json`, the `lighting`/`hid-output` mechanism gives `vendorId` `0DB0` and `reportLength` `64`. The `controller-mode` mechanism's `commandEndpoints` (`1901 FFA0:0001, 1902 FFF0:0040`) are exactly today's two MCU collections. `LabPowerPlan` builds the layout only when both are present, the same way `AuraLayout` gates on its mechanism. The parse of `commandEndpoints` reuses the one `LabControllerInit` already has.
  - `LabClawLighting.Service.Open` keeps taking the record id. The worker resolves the layout itself through `LabPowerPlan.For(record)` from its own knowledge base, so the wizard cannot hand the worker arbitrary collections. It refuses with today's message when there is none.
  - `Exchange` matches against the layout.
  - The Claw RGB test is gated on the plan having that layout (labui B10).
- **Tests:** in the labui `LightingTests`, the Claw profile is restored exactly after a cancelled test. `LabPowerPlan` for the A2VM record yields today's collection identities. Filter: `--filter "FullyQualifiedName~LightingTests|FullyQualifiedName~LabPowerPlan|FullyQualifiedName~LabClawLighting"`.
- **Plan v2:** B171 (labui B10).
- **Related:** LABUI-009 (UI literals), plan claim P8.

### LABCORE-026: AGENTS describes a worker boundary the code does not have

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/AGENTS.md` ("Wizard hardware access runs in the elevated `Worker/LabWorkerHost`") and `README.md`. The in-process exceptions are:
  - `LabModeCommands.Start/Undo` via `WizardWindow.ModeCommands.cs`
  - the reversible mode switch as a mode command (`LabModeCommands.cs:925-940`)
  - the system dump's RyzenSMU and MSR reads (`LabSystemDump.LowLevel.cs:32,48`)
  - `LabLhmSensors`
  - input capture
  - motion serial configuration
- **Problem:** the guidance states a stronger boundary than the code enforces, so a reviewer cannot tell an intended exception from a defect.
- **Best solution:** documentation only, with no new worker services (simplify).
  - Reword AGENTS and README: writes that capture a readable original run in the worker behind a checkpoint; HC mode commands (no readable original, recorded before sending) and read-only collection stay in the wizard.
  - Also record the LABCORE-035 logging rule ("every worker operation is traced, except streamed frames, status polls and calls marked sampled").
  - Show the AGENTS change as a separate diff for sign-off and run `eng/check-agent-guidance.ps1`. Docs may cite HC; commit messages must not.
- **Tests:** `npm run format:check`; `./eng/check-agent-guidance.ps1`.
- **Plan v2:** B177.
- **Related:** LABUI-026, LABCORE-V-006, LABCORE-035.

### LABCORE-027: caps that drop or truncate evidence

- **Severity:** low (verifier: the review's "keep byte bounds as IO safety" is replaced by the explicit D2 list, and the `LabTrace` overwrite is added).
- **Where:**
  - `src/WSGM.DeviceLab/Capture/Live/LabInputCapture.cs`:
    - `:104` `MaximumEventsPerStep = 6000` and the drop branch near `:329`
    - `:107` `MaximumNoisePerDevice = 200` and `:441`
    - `:112,398` `MaximumReportBytes = 512` slice
    - `:535-546` (WMI event: 24 properties, 128 bytes, 32 items, 256 characters)
  - `Capture/Live/LabWmiFirmwareEvents.cs:42` (`MaximumTables = 256`)
  - `Wizard/LabSystemDump.cs:214-226` (issue lists at 50), `LabSystemDump.DeviceTree.cs:80,120-134` (4,000 devices), `LabSystemDump.Acpi.cs:77,164-200` (256 tables), `LabSystemDump.Smbios.cs` (2,048 structures)
  - `Application/LabTrace.cs:29,151-163` (4 MiB rotation overwrites the previous log)
  - `Application/SelfWorkerProtocol.cs:29,167` (`Bound`, 16 KiB)
  - `Probes/ReadProbeWorkerSupervisor.cs:40` (stderr 16 KiB), `:282` (response 1 MiB)
  - `Probes/ReadProbeWorker.cs:20` (request 256 KiB)
- **Problem:** hardware evidence is silently cut or dropped:
  - long HID reports
  - WMI event properties, bytes, array items and strings
  - events past 6,000 per step
  - dump issues, devices, tables and structures past fixed counts
  - diagnostic messages past 16 KiB
  - the older trace past 4 MiB
  
  Attribution happens afterwards, so a dropped item is lost evidence (the "capture all inputs" rule).
- **Best solution:** remove every cap above, except the bounds D2 keeps. D2 keeps the 8 MiB firmware table (`MaximumTableBytes`), the 64 KiB device property and the review-archive guard. Each of those refuses the item (reports it as unreadable with its size) and never truncates; check each while editing.
  - Capture (B161):
    - Store whole HID reports. Delete `MaximumReportBytes`.
    - Keep every non-null WMI property with full hex, all array items and whole strings.
    - Delete `MaximumEventsPerStep` and its drop and count branch (D2: "every other cap is removed").
    - Keep the counted one-in-50 noise sampling (`NoiseSampleEvery`), which plan v2 keeps documented. Also delete `MaximumNoisePerDevice`: it is the same kind of count cap as the 6,000 limit, while the sampling itself stays.
    - Remove the issue, device, table and structure count caps in `LabSystemDump*` and `MaximumTables`. The OS lists are the natural bound.
    - Recorded output for in-bound inputs stays byte-identical.
  - Process hygiene (B162):
    - Delete `RotateIfLarge` and `MaxBytes`, so the log is append-only and no earlier content is overwritten. This is simpler than timestamped rotation files, and the tester still sends one file. The Lab logs nothing high-rate.
    - Delete `SelfWorkerProtocol.Bound` and `MaximumErrorLength` (read whole stderr).
    - Delete the probe request and response byte bounds, which are not on the D2 list.
  - The `.wsgmcap` model caps (`CaptureModels`, `CaptureBundleReader`) are not touched (LABCORE-009).
  - The AGENTS mention of the per-step cap changes in B177.
- **Tests:**
  - `LabInputStepBufferTests`, which needs the buffer extraction in this batch: a 600-byte report is stored whole; 7,000 events in one step are all kept; noise-only reports are sampled one in 50 and counted.
  - A WMI event with 40 properties keeps all 40.
  - `LabSystemDumpTests`: more than 50 issues are all listed.
  - Filter: `--filter "FullyQualifiedName~LabInputStepBuffer|FullyQualifiedName~LabSystemDump"`.
  - B162 filter: `--filter "FullyQualifiedName~Application|FullyQualifiedName~ReadProbe"`.
- **Plan v2:** B161 (capture and dump), B162 (trace, diagnostic text, probe bounds). D2 lists the bounds kept.
- **Related:** LABCORE-018 (no-change; this finding removes the rotation), LABCORE-036 (allocation accepted), plan claim P9 (`LabInputStepBuffer` extraction in the same batch), LABUI-V-003, LABUI-V-004 (same batch).

### LABCORE-029: controller init writes to the first matching collection

- **Severity:** low.
- **Where:** `src/WSGM.DeviceLab/Wizard/LabControllerInit.cs:322-332` (`Endpoint` uses `FirstOrDefault`), `:236-250` (`SwitchMode` takes the first present match); compare `Wizard/LabModeCommands.cs:886-907` (`Locate` requires exactly one).
- **Problem:** with two same-vendor controllers attached, the curated init or mode switch goes to whichever collection enumerates first. AGENTS and `Locate` require exactly one match.
- **Best solution:**
  - Add a pure helper `static (LabHidEndpoint? Endpoint, string? Problem) Single(IEnumerable<LabHidEndpoint> matches)` that returns:
    - "The controller's vendor collection is not present." (today's text) for none
    - the endpoint for one
    - `$"{n} collections match, so it is not clear which one to write to."` (the `Locate` wording) for several
  - `Endpoint` and `SwitchMode` use it, and the latter returns `LabInitResult(false, …, problem)` before any write.
  - Several product IDs that share a mode are still one match while only one is present.
- **Tests:** `Single` over fabricated `LabHidEndpoint`s handles zero, one and two matches. Put them in a new `Wizard/LabControllerInitTests.cs`. B158's batch filter has no `LabControllerInit` term, so add `|FullyQualifiedName~LabControllerInit` when running it. Filter: `--filter "FullyQualifiedName~LabModeCommands|FullyQualifiedName~LabControllerInit"`.
- **Plan v2:** B158.
- **Related:** LABCORE-024, LABCORE-038.

### LABCORE-031: a capture start failure can orphan its message thread and crash the process

- **Severity:** low (verifier: worse than stated, because a late `_ready.Set()` throws `ObjectDisposedException` inside the thread's catch and ends the process).
- **Where:** `src/WSGM.DeviceLab/Capture/Live/LabInputCapture.cs:238-252` (`Start` disposes after a 10 s readiness timeout), `:217-224` (`Dispose` posts `WM_QUIT` only when `_messageThreadId != 0`, then disposes `_ready`); `Capture/Live/LabInputCapture.Native.cs:51` (thread id set), `:118` (`_ready.Set()`), `:153-156` (catch, `_ready.Set()` again).
- **Problem:** when the message thread starts late:
  - `Dispose` posts no quit, and the late thread installs global keyboard and mouse hooks that stay for the life of the process.
  - Its `_ready.Set()` on the disposed event throws inside the catch block. The exception escapes the background thread and terminates the wizard.
- **Best solution:** use the existing `volatile bool _disposed` (no new state).
  - In `MessageLoop`, after `_ready.Set()` and before the `GetMessage` loop, skip the loop when `_disposed` is true and fall through to the existing unhook and cleanup code.
    - `Dispose` sets `_disposed` before it reads `_messageThreadId`, and the thread sets its id before creating its window.
    - Either the quit is posted to a queue that exists, or the thread sees `_disposed` and never enters the loop.
  - In `Dispose`, call `_ready.Dispose()` only when `_messageThread is null || _messageThread.Join(TimeSpan.FromSeconds(5))`. A thread still alive after the join keeps the event, which the GC collects later.
- **Tests:** none offline (native thread and hooks); covered by build and review. The behaviour belongs to the attended capture check.
- **Plan v2:** B161.
- **Related:** LABCORE-V-004 (same file).

### LABCORE-037: tests that assert metadata or arithmetic

- **Severity:** low.
- **Where:** `tests/WSGM.DeviceLab.Tests/Worker/LabWorkerSessionTests.cs` (last fact, `HardwareWorker_RegistersRumbleAndCuratedInitWithCheckpointedWrites`); `Probes/ReadProbeTests.cs:97-104` (`ReadProbeSupervisor_OutlivesTheWorkersSemanticDeadline`, which checks `ProcessDeadline = timeout + 2000` and is tautological; verifier corrected the wording); `Wizard/LabPowerRecoveryTests.cs` (`AnyPending_And_PowerPending_TrackWhatIsRecorded`); `Application/DurableFileTests.cs` (`StagingPathCreatesAUniqueHiddenSibling` and `StagingPathIsHiddenUniqueAndBesideTarget`).
- **Problem:** these tests restate attributes, a constant sum or a predicate, or duplicate each other. They catch no regressions and cost maintenance.
- **Best solution:**
  - Replace the attribute test with the registry validation test (LABCORE-015, B157).
  - Delete the deadline test (B159).
  - Delete the pending-predicate test once LABCORE-033 makes `AnyPending` one line; the recovery tests cover the behaviour (B158).
  - Merge the two staging tests into one (B162).
- **Tests:** the filters of B157, B158, B159 and B162 as given in their sections.
- **Plan v2:** B157, B158, B159, B162.
- **Related:** LABCORE-015, LABCORE-033.

### LABCORE-038: behaviour without tests

- **Severity:** low (plan v2: covered by the tests each Lab batch adds, no separate batch).
- **Where:** `LabMachineState`, `LabRumbleRecovery`, controller-mode and mode-command pending records, `LabWorkerClient` lost/timeout/rethrow, probe channel null-input handling, `LabIntelKx` output parsing (`ReadMsr`, `Return`), `LabAmdSmu.CommandsFor`, `LabControllerInit` single match.
- **Problem:** the safety and recovery paths that decide whether hardware is left changed have no offline coverage.
- **Best solution:** add each test in the batch that touches the code:
  - B157: client lost and rethrow (LABCORE-012, 016).
  - B158: machine state, recovery, pending records, single match (LABCORE-001, 010, 011, 029).
  - B159: probe channel and AMD bounds (LABCORE-007, V-003).
  - B160:
    - `LabIntelKx` parse of `Msr Data` and `Return` lines from captured KX output strings.
    - `LabAmdSmu.CommandsFor` against HC's codename table (`_ref/HandheldCompanion`, RyzenAdj command ids `0x1A/0x1B/0x1C` for codenames 2, 6, 7, 15 and `0x14/0x15/0x16` for the mobile list).
  - Make the parsers `internal static` where they are private today.
- **Tests:** B160 filter `--filter "FullyQualifiedName~LabIntelKx|FullyQualifiedName~LabAmdSmu"`. The others are as listed.
- **Plan v2:** B157 to B160 (no own batch).
- **Related:** the ids above.

### LABCORE-039: the MIT Device Lab compiles GPL product interop files

- **Severity:** low (solution check lowered it from medium: `src/WSGM.DeviceLab/README.md:238-241` already states that the copyright holder licenses these four copies to Device Lab under MIT, so the binary's licence is stated, not ambiguous).
- **Where:** `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj:34-41` (links `Kernel32`, `NativePathIdentity`, `NativeHidHide`, `NativePackageSource` from `src/WSGM/Interop`); `src/WSGM.DeviceLab/LICENSE` (MIT, embedded at `WSGM.DeviceLab.csproj:75` as `WSGM.DeviceLab.Help.LICENSE` for the wizard's help window, `Gui/WizardHelp.cs:14`); `src/WSGM.DeviceLab/README.md:233-241` (Licence section and the grant); `src/WSGM.DeviceLab/AGENTS.md:7` ("a separate MIT-licensed Windows authoring and diagnostics application"); `docs/device-plugin-system.md:986` (`src\WSGM.DeviceLab`, MIT); `external/kx/README.md:13` and `external/kx/kx.lock.json:10` ("not covered by the Device Lab MIT licence").
- **Problem:** the grant lives only in the Lab README. The four source files sit in the GPL product tree with no header, so someone reading `src/WSGM/Interop` cannot tell that the Lab's copies are MIT. `NativeHidHide.cs` (251 lines) carries logic, not only declarations.
- **Best solution:** decided by D3: relicense Device Lab as GPL-3.0-or-later. The four interop files stay where they are, unchanged and linked as today. Nothing is moved, duplicated or given an MIT header, and the SDKs stay MIT.
  - Replace `src/WSGM.DeviceLab/LICENSE` with the text of the repository's root `LICENSE` (GPL v3). The help window embeds that file, so it shows the new licence without a code change.
  - `WSGM.DeviceLab.csproj` carries no licence property today, and the interop `Compile Include` links stay. The comment at `:66-68` is about the scaffold template that generated plugins get, and stays true. Confirm with rg that no other csproj or props metadata names the Lab MIT.
  - README Licence section: "GPL-3.0-or-later, see `LICENSE`", keeping the `THIRD_PARTY_NOTICES.md` sentence. Delete the grant paragraph (`:238-241`), since the linked interop sources and the AllyXLab ports now share the Lab's licence; keep one plain line crediting the AllyXLab port. The scaffold paragraph (`:214-216`) stays: a generated plugin links only the MIT SDK and keeps its MIT `LICENSE.txt` template; reword "WSGM itself is GPL-3.0-or-later" to "WSGM and Device Lab are GPL-3.0-or-later".
  - `src/WSGM.DeviceLab/AGENTS.md:7`: "a separate GPL-3.0-or-later Windows authoring and diagnostics application". Show the guidance diff with the batch (D4) and run `eng/check-agent-guidance.ps1`.
  - `docs/device-plugin-system.md:986`: `(src\WSGM.DeviceLab, GPL)`. `external/kx/README.md:13` and `kx.lock.json:10`: "is not covered by Device Lab's licence", dropping "MIT".
  - `THIRD_PARTY_NOTICES.md` lists third-party components under their own licences and needs no change.
  - The batch no longer depends on B172 (HidHide adapter sharing), because no file moves.
- **Tests:** no unit test. `dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj -c Release`; `npm run format:check` (Markdown and JSON changed); `./eng/check-agent-guidance.ps1`; `rg -n -i "MIT" src/WSGM.DeviceLab docs/device-plugin-system.md external/kx` shows only the scaffold template, the SDK and third-party notices.
- **Plan v2:** B173; decided: D3, Device Lab becomes GPL (licence file, README, AGENTS and notices), with no interop file relicensed or duplicated.
- **Related:** A02-F022, plan claims P1 and P20, critic conflict 22, LIBRARY-025, BUILD-019.

### LABCORE-V-004: shortcut state is tracked only while shortcuts are swallowed

- **Severity:** low (missed finding).
- **Where:** `src/WSGM.DeviceLab/Capture/Live/LabInputCapture.Native.cs:514` (`var swallow = SwallowShortcuts && Shortcut(...)`), `:529-543` (`Shortcut` updates `_windowsHeld` and `_altHeld`).
- **Problem:** because of the short-circuit, the Windows and Alt state updates only while swallowing is on. Suppose a Windows key goes down during a button step and comes up after that step turned swallowing off. `_windowsHeld` then stays true, and the next swallowing step swallows every key system-wide, the wizard's Escape included, until the Windows key is pressed again.
- **Best solution:**
  - Add `internal bool OnKey(ushort key, bool up)`, which evaluates `var tracked = Shortcut(key, up); return SwallowShortcuts && tracked;`.
  - `KeyboardHook` calls it.
  - This is a reordering with no new state.
- **Tests:** construct `LabInputCapture` through its internal constructor without `Start`.
  1. Turn swallowing on and send Windows down (`OnKey` returns true).
  2. Turn swallowing off and send Windows up.
  3. Turn swallowing on and send `A` down.
  
  `OnKey` returns false. Today's code returns true at step 3, because the Windows up in step 2 was never tracked. (Releasing the key while swallowing is off from the start would pass on today's code too, so it does not test the defect.) Filter: `--filter "FullyQualifiedName~LabInputCapture|FullyQualifiedName~LabInputStepBuffer"`.
- **Plan v2:** B161.
- **Related:** LABCORE-031.

### LABCORE-V-005: `RestorePower` items use different catch filters, so one item can skip the rest

- **Severity:** low (missed finding, containment).
- **Where:** `src/WSGM.DeviceLab/Wizard/LabPowerRecovery.cs` catch filters:
  - `:205` Claw: everything but OOM
  - `:304` ASUS: Win32, InvalidOperation, IO
  - `:378` MSI: `IsTransportFailure`
  - `:415` AMD: no IO or UnauthorizedAccess
  - `:447` Intel: no Timeout or Argument
  
  Also `Wizard/LabRumbleRecovery.cs:56` and `Worker/LabWorkerClient.cs:409-427`.
- **Problem:** the client rethrows `TimeoutException`, `UnauthorizedAccessException`, `ArgumentException` and `ArgumentOutOfRangeException`. One of those escaping an item skips the later items and `ClearWhenEmpty`. In the start-up sequence it also skips rumble and controller recovery.
- **Best solution:**
  - Every per-item catch in `RestorePower`, `RestoreAsus`, `RestoreMsi`, `RestoreAmd`, `RestoreIntel` and `LabRumbleRecovery` becomes `catch (Exception ex) when (ex is not OutOfMemoryException)`, keeping each item's message and leaving its record for the next start.
  - `LabRumbleRecovery` keeps its `break` on `LabWorkerLostException`. Later calls would fail at once anyway.
- **Tests:** in `LabPowerRecoveryTests`, a fake worker whose AMD open throws `TimeoutException` still runs the Intel item, and `ClearWhenEmpty` runs. Filter: `--filter "FullyQualifiedName~LabPowerRecovery|FullyQualifiedName~LabRecovery"`.
- **Plan v2:** B158.
- **Related:** LABCORE-011 (same rule one level up), LABCORE-V-003.

### LABCORE-V-006: AGENTS states the zero and read-probe rules as current behaviour

- **Severity:** low (missed finding, documentation).
- **Where:** `src/WSGM.DeviceLab/AGENTS.md`: "A zero counts as done only after it was written; a failed zero leaves the safety zero armed" and "compiled read probes require an exact live device and endpoint match".
- **Problem:** the code does neither today (LABCORE-003, LABCORE-008). A documentation pass that "aligns docs with code" would weaken the rules to match the defects.
- **Best solution:** B004 and B159 cite these two sentences as their specification. B177 keeps them unchanged, now true, and does not reword them. No AGENTS edit is needed for these two rules.
- **Tests:** `./eng/check-agent-guidance.ps1` after any AGENTS change in B177.
- **Plan v2:** B177 (verification only), with B004 and B159 implementing the behaviour.
- **Related:** LABCORE-003, LABCORE-008, LABCORE-026.

## Nit

### LABCORE-033: hand-listed pending predicates and manager lists

- **Severity:** nit.
- **Where:** `src/WSGM.DeviceLab/Wizard/LabPowerRecovery.cs:66-87` (`AnyPending`, `PowerPending` list the fields twice); `Wizard/LabPowerGate.cs:40-44` (`BlockingManagers` repeats process names); `Wizard/ManagerConflicts.cs:14,46-67` (`KnownManager`, `Known`).
- **Problem:** a new recorded field or manager must be added in two places, and the lists can drift.
- **Best solution:**
  - B158: `AnyPending(c) => PowerPending(c) || c?.AuraWrittenAt is not null`.
  - B160: `KnownManager` gains a trailing `bool OwnsPowerHardware = false`, set with the named argument `OwnsPowerHardware: true` on the ten managers in today's `BlockingManagers` list. `BlockingManagers` becomes `Known.Where(m => m.OwnsPowerHardware).Select(m => m.Process)`.
- **Tests:** the derived blocking set equals today's ten names. Filter: `--filter "FullyQualifiedName~PreflightTests|FullyQualifiedName~LabPowerRecovery"`.
- **Plan v2:** B158 (predicate), B160 (manager flag).
- **Related:** LABCORE-037 (the predicate test goes).

### LABCORE-034: probe descriptors repeat their metadata

- **Severity:** nit.
- **Where:** `src/WSGM.DeviceLab/Probes/ReadProbeProfiles.cs:118-198` (`MsiClawReadProbes.Probe`: endpoint, rate 2, timeout 5,000, repetitions 2), `:276-292` (`MsiWmiReadProbeProfile` constructor repeats them, and each subclass passes its endpoint again).
- **Problem:** the reviewed probe facts live in two places that can disagree.
- **Best solution:** the `MsiWmiReadProbeProfile` constructor takes only the probe id. It builds `Descriptor` from `MsiClawReadProbes.Family.Probes.Single(p => p.Id == id)` (endpoint, family, `MaximumReadsPerSecond`, `TimeoutMilliseconds`, `Repetitions`), and subclasses stop passing endpoint and family.
- **Tests:** every MSI profile's descriptor equals its metadata entry. Filter: `--filter "FullyQualifiedName~ReadProbe"`.
- **Plan v2:** B159.
- **Related:** LABCORE-007.

### LABCORE-035: the worker logs every sampled fan reading

- **Severity:** nit (plan v2 changed the review's "no change" to a fix).
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs:157-174` (logs `start`/`returned` for every non-stream op); `Gui/WizardWindow.Power.cs:185,214,224,784` (2 s `FanSpeeds` sampling); `Transports/LabMsiWmi.cs:55`; `Transports/LabAtkAcpi.cs:81`.
- **Problem:** each sampled `FanSpeeds` call writes two trace lines, about 20 per 20 s burst. AGENTS forbids per-sample logging.
- **Best solution:**
  - Add `LabWorkerSampledAttribute` beside the other worker attributes in `LabWorkerProtocol.cs` and put it on `ILabMsiWmi.FanSpeeds` and `ILabAtkAcpi.FanSpeeds`.
  - `Dispatch` skips the `start`/`returned` lines for a `call` whose resolved method (the LABCORE-015 table) is `Sampled`. A failure is still traced by `Handle`'s catch.
  - A marker on the interface beats a method-name check in the host, which must not know service methods.
- **Tests:** in `LabWorkerHostTests`, a sampled call on a fake service writes no trace line, and a failing sampled call writes one. If `LabTrace` cannot be observed, assert on the predicate helper instead. Filter: `--filter "FullyQualifiedName~WSGM.DeviceLab.Tests.Worker"`.
- **Plan v2:** B157; AGENTS wording in B177 (LABCORE-026).
- **Related:** LABCORE-015, LABCORE-026.

### LABCORE-040: the CLI rebuilds the application facade per command

- **Severity:** nit.
- **Where:** `src/WSGM.DeviceLab/Cli/DeviceLabCli.cs:569-578` (`Application()`, `RepositoryRoot()` per command), `:27-32,668-671` (`WriteJson` uses reflection-based options over anonymous objects).
- **Problem:** every command recomputes the repository root and facade, which is the second root rule behind LABCORE-020.
- **Best solution:** `DeviceLabCli` receives the one `DeviceLabApplication` and `DeviceLabPathBoundaries` built by `Program` (LABCORE-020), and `Application()` and `RepositoryRoot()` are deleted. `WriteJson` stays as it is, so the CLI JSON shape is unchanged.
- **Tests:** existing `CliArgumentsTests` pass. Filter: `--filter "FullyQualifiedName~Cli"`.
- **Plan v2:** B162.
- **Related:** LABCORE-020.

## Refuted or no-change

The verifier refuted nothing outright. These ids are no-change (plan v2, plus LABCORE-018 under the maintainer's security decision) and must not be implemented:

- **LABCORE-004** (pre-cancelled call is still invoked; verifier lowered medium to low): invoking a call whose token was cancelled before dispatch is the Lab's documented contract ("Cancelling only ends a wait"). Refusing it would make the curated-init path report "a controller setup stopped before its result was known" although nothing was sent. The EOF half is fixed under LABCORE-005.
- **LABCORE-006** (stale slider frames replay after the zero; verifier lowered to nit): replayed frames are earlier values of a slider the tester is still moving, and the stream's idle stop and final zero still apply. With the `TryEnter` watchdog there is no backlog for a filter to undo.
- **LABCORE-009** (the `.wsgmcap` observe-only pipeline records nothing live): retiring `capture run`, `inspect`, `compare`, `correlate` and `fixture extract` would remove features (requirement 9). They stay frozen byte-for-byte, with no refactor investment.
- **LABCORE-014** (an unacknowledged checkpoint blocks the session; verifier lowered to nit): no caller reuses a session after a failed persist, because every failure path disposes the proxy.
- **LABCORE-018** (elevated roles append to a log in a possibly user-writable folder): Dropped by maintainer decision (security theater, DECISIONS.md). The solution check had already reached the same result: the elevated wizard is `WizardElevation` relaunching `DeviceLabExecutable.CurrentPath` with `runas`, and the elevated wizard starts the hardware worker from that same path (`LabWorkerLaunch.Self`) with no prompt. Anyone who can plant a link named `wsgm-device.log` in that folder can equally rename the running executable and drop a replacement or plant a DLL beside it, which the elevated roles then run. Link and reparse checks on the log therefore close no boundary and only add mechanism. The rotation rename goes away under LABCORE-027 (B162). The B162 spec line for 018 ("elevated log link and reparse refusal with a seam") is not built.
- **LABCORE-028** (redaction replaces account names inside words): whole-token matching would weaken a privacy redactor whose output leaves the machine. Redaction stays as today.
- **LABCORE-030** (no cross-process exclusion on the machine record): a second-wizard lock adds mechanism for a dev tool. The owner reservation already serializes the hardware restores, and the limitation is documented.
- **LABCORE-032** (worker and transports depend on wizard types): the `LabPowerLog` namespace move and the separate wire options change about ten files for pure tidying. `LabProject.JsonOptions` and `WireOptions` stay where they are.
- **LABCORE-036** (Lab capture allocates per report and per 4 ms poll): accepted for the attended tool, since capture is not a product high-rate path (plan claim P12). The B161 buffer extraction must not change recorded output.
