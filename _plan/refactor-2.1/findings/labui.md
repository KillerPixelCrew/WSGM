# Device Lab GUI, wizard and evidence authoring findings

Scope: the presentation and evidence-authoring half of Device Lab. That is `src/WSGM.DeviceLab/Gui/**` (`App`, `DeviceLabGui`, `DeviceLabGuiOperationState`, `MainWindow`, `WizardHelp`, `WizardWindow` and its ten partials), the wizard project, stage, export and report files in `src/WSGM.DeviceLab/Wizard/` (`LabStages`, `LabProject`, `LabExport`, `LabReport`, `LabReview*`, `LabReviewArchive`, `LabPromote`, `LabButtonPlan`, `LabPowerResults`, `WizardElevation`), `src/WSGM.DeviceLab/Capture/CapturePrivacyPreview.cs`, and their tests in `tests/WSGM.DeviceLab.Tests/{Gui,Wizard}`. The worker, capture internals, transports and machine record belong to the labcore findings; where a fix crosses into them, the owning batch is named.

The area has 40 ids. 33 are findings to implement: 2 high, 9 medium, 15 low and 7 nit (one, LABUI-C-001, added by the solution check). The other 7 are refuted or no-change and are listed at the end. Plan v2 implements them in B019 and B020 (Phase A live defects), B158 and B161 (labcore batches that carry three of these ids), and B163 to B171 (the Lab refactor sequence). No maintainer decision gates any of them. Three decisions touch the area, all settled in DECISIONS.md: D2 keeps the review-archive guard that LABUI-014 relies on; D3 relicenses Device Lab as GPL, so the linked interop files stay as they are and only the Lab's licence file, csproj metadata, README, AGENTS and notices change, none of them part of these fixes; D9 removes readback machinery from device packages and WSGM host paths, and the Lab is neither (see the last rule below).

Before you start:

- Many line numbers in the review are wrong. Anchor every edit by symbol. The locations below were checked against master `1329813f`.
- The plan claim checks C1 to C16 in the review are not findings. Plan v2 already applies what came out of them: no 64-request worker queue or Busy outcome (C4), no UI revisions or generations for the Lab (C10), no B3 shutdown budgets for the Lab (C12), and M01-42 now requires the `%LOCALAPPDATA%\WSGM Device Lab\wizard` folder to be absent (C13, plan v2 section 5).
- `tests/WSGM.DeviceLab.Tests` has no Avalonia headless harness. Adding one would touch the frozen Avalonia packages. So window-level behaviour is checked manually, and it gets automated tests only once the logic moves into non-Avalonia owners (B165, B166 onwards).
- These rules hold everywhere: strings, control order, sizes and styles stay identical; nothing is retried automatically; record-before-write and readback-before-clear stay as the Lab's documented exception, because the Lab exists to verify and write-only Aura is already cleared without readback. D9 does not reach this exception: it covers device packages and WSGM host paths, while the Lab is the tool that collects readback evidence. No fix below adds readback machinery, and a missed readback is never turned into a contradiction (LABUI-002).

## High

### LABUI-001: Hardware orchestration and safety paths live inside the Avalonia window

- **Severity:** high
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs` (`RunPowerAsync`, `RunAsus*`, `RunMsi*`, `RestorePendingAsync`, `ForgetRecordedAsync`, `WriteEvidenceOnce`), `Gui/WizardWindow.ProcessorPower.cs`, `Gui/WizardWindow.ClawLighting.cs`, `Gui/WizardWindow.Rumble.cs` (`RumbleSession`, `WorkerRumbleOutput`, `WatchWorkerStreamAsync`), `Gui/WizardWindow.Hardware.cs` (`CaptureAsync`, `WorkerAsync`, `CuratedModeAsync`, `RecoverControllerInitAsync`, `RunHardware`), `Gui/WizardWindow.ModeCommands.cs`, `Gui/WizardWindow.cs` (fields `_lifetime`, `_machine`, `_owner`, `_project`, `_operation`, `_stage`; `Start`, `Run`, `RunCore`, `CloseAfterCleanupAsync`).
- **Problem:** Worker checkpoints, `LabPowerRecovery.Record`, readback sampling, restore ordering, `_machine.Update` clears, rumble route sessions, controller-init recovery and the ordered close are all private members of a `Window` split across 11 partial files (about 5,600 lines). Nothing can run them without a window, a worker process and hardware, so none of these safety paths has a test. The subtree AGENTS rule "Keep hardware policy out of UI code" is broken.
- **Best solution:** Extract by state and responsibility, not into more partial files, in the order B166 to B171.
  - `Wizard/Session/WizardSession` owns project create and open, the `Global\WSGM.DeviceOwner` reservation, the lazily started `ILabWorkerClient` (from B157) and `ILabInputCapture` (from B161), the stage loop (LABUI-006), navigation, Stop, notes (LABUI-018), `PowerRestorationPendingAsync` and an idempotent `CloseAsync`. `CloseAsync` keeps today's order exactly: cancel the stage, await it, undo the HidHide entry, dispose capture, dispose worker, release the reservation, once.
  - `Wizard/Session/WizardServices` is a plain constructor bundle: `LabMachineState`, `LabRecovery` (B158), `DeviceKnowledgeBase`, `LabPawnIo`, a worker factory, a capture factory, `DeviceLabPathBoundaries` and `TimeProvider`. `Program` composes the production instance.
  - `Wizard/Session/WizardStageContext` carries the project, attempt directory, confirmed record, elevated flag, owner-present flag, stage token and services. `WizardOptions` moves to `Wizard/Session/`, and `ToolSha256`, `SourceRevision` and `ToolVersion` move to a static `WizardBuildInfo`.
  - Stage controllers implement `IWizardStage` and return the next stage id (or null when they leave a result page up):
    - B167: `Stages/{PreflightStage, IdentityStage, SystemDumpStage, FinishStage}`.
    - B168: `Stages/{ButtonsStage, MotionStage, ModeCommandRun, ControllerInitRunner}`.
    - B169: `Stages/Rumble/{RumbleStage, RumbleSession}`.
    - B170: `Stages/Power/{PowerStage, PowerTestGuard, AsusPowerTests, MsiPowerTests, ProcessorPowerTests}`.
    - B171: `Stages/Power/LightingTests` and `Stages/SleepStage`.
  - `Wizard/Session/IWizardUi` is a narrow port listing only what the stages use: `ShowPage`, `Status`, `Warning`, `Muted`, `Heading`, an updatable `Line`, one `AskAsync(labels, wrap, stop, elsewhere)`, `CountdownAsync`, `ConfirmIdentityAsync`, `ChooseRedoAsync`, `ManagerRows`, `RumbleSlidersAsync`, `RumblePulsesAsync`, `PickReportPathAsync` and `WindowHandle`. `WizardWindow` implements it.
  - The window keeps rendering, the stage list, the notes box, the close-confirmation dialog, help and "open folder". Page helpers (`Page`, `PageTitle`, `Heading`, `Status`, `Warning`, `Muted`, `Action`, `Buttons`) move verbatim to `Gui/WizardControls.cs`, and the rumble slider and pulse pages move to `Gui/RumbleViews.cs`.
  - In B166 the existing stage bodies become `IWizardStage` adapters that still live in the window partials, so every batch builds green. Each later batch moves code with write order, values, record and clear points, and restore order kept line for line.
  - Add none of the following: UI revisions, generations, a Busy outcome or shutdown budgets. The one-operation gate is the whole concurrency mechanism.
- **Tests:** New `tests/WSGM.DeviceLab.Tests/Wizard/WizardSessionTests.cs` with `FakeWizardUi`, a fake worker, a fake capture and a temp `LabMachineState`. It covers: one operation at a time; close requested twice runs the close order exactly once; a hardware stage without the reservation refuses with today's text. Later batches add the stage tests listed under LABUI-007 and in B169 and B170. Filters: `--filter "FullyQualifiedName~WizardSession"` (B166), then `~Stages`, `~ButtonsStage|~MotionStage`, `~RumbleStage`, `~PowerStage|~PowerTests`, `~LightingTests|~SleepStage`.
- **Plan v2:** B166 (resolves), with the structure finished in B167 to B171. Depends on B157 (`ILabWorkerClient`), B158 (`LabRecovery`) and B161 (`ILabInputCapture`).
- **Related:** plan claims C1, C3, C11, C14; LABUI-006, LABUI-010, LABUI-011, LABUI-018, LABUI-019, LABUI-025; LABCORE-011, LABCORE-026.

### LABUI-V-001: A synchronous Continue loses the next stage's operation

- **Severity:** high (found by the verifier)
- **Where:**
  - `src/WSGM.DeviceLab/Gui/WizardWindow.cs`: `Run`, `RunCore`, `Next`, `StartStage`, `StopStage`, `CloseAfterCleanupAsync`, and the `_stages.SelectionChanged` handler.
  - `Gui/WizardWindow.Motion.cs`: the result page's "Continue" (`Run(page, () => { Next(LabStages.Motion); return Task.CompletedTask; })`).
  - `Gui/WizardWindow.Rumble.cs`: the failed-rumble result page's "Continue" (same shape).
  - `Gui/WizardWindow.Power.cs`: the "Continue" / "Continue anyway" button at the end of `RunPowerAsync`, and the "Try restoring again" button in `RestorePendingAsync`.
- **Problem:** `Run(page, () => { Next(x); return Task.CompletedTask; })` runs `work()` synchronously inside `RunCore`. `Next` then starts the next stage chained, so the inner `Run` sets `_operation = Task.WhenAll(oldCompleted, nextStage)`. Control returns to the outer `Run`, which then executes `_operation = operation;` with its own already-completed task, overwriting the tracked stage. Motion's Continue is the normal path, so in every wizard run the Rumble stage (and, after a failed rumble, the Power stage) runs while `_operation.IsCompleted` is true. As a result:
  - Stop and Escape do nothing.
  - The outer `RunCore` finally re-enables the stage list. A list click then calls `ShowStage` without cancelling, which detaches the running page, and `Start` on another hardware stage runs concurrently with open rumble routes or recorded power changes.
  - Closing awaits a completed `_operation` and disposes capture, worker and the owner reservation while the stage's finally block is still zeroing routes or restoring power.

  A second path: Power's "Continue anyway" calls `Next` directly from a click handler. While "Try restoring again" (a non-chained `Run`) is running, the chained `WhenAll` starts the Sleep stage beside the retry.
- **Best solution:** Add one method to `WizardWindow.cs` for result-page Continue buttons:
  ```csharp
  // A result page's Continue starts a new operation; it is refused while one is running.
  private void ContinueAfter(string after)
  {
      var next = LabStages.All.SkipWhile(stage => stage.Id != after).Skip(1).FirstOrDefault()?.Id;
      if (next is null) { ShowStage(FinishId); } else { StartStage(next); }
  }
  ```
  `StartStage` and `ShowStage` called without `fromCompletedOperation` already refuse while `_operation` is not completed, and they go through the non-chained `Run`, which creates a fresh `_stage` token and assigns `_operation` once.
  - The Motion and Rumble Continue buttons become `Action("Continue", () => ContinueAfter(LabStages.Motion))` and `Action("Continue", () => ContinueAfter(LabStages.Rumble))`, without the `Run` wrapper.
  - Power's button becomes `Action(pending ? "Continue anyway" : "Continue", () => ContinueAfter(LabStages.Power))`.
  - `Next` stays only for calls made from inside a running operation, after an await: the Continue of Preflight and Identity (both wrap an awaited `Task.Run` in `Run`), the end of SystemDump, Buttons and Sleep, the Skip action, Motion's `if (skipped) Next(LabStages.Motion)` and Rumble's success path `Next(LabStages.Rumble)`. Their outer `Run` has already stored its still-running task in `_operation`, so the chained `WhenAll` is tracked correctly.

  This beats plan v2's B019 wording ("call `Next` directly and check `IsCompleted`"): `Next` from a click handler still takes the chained path, which reuses the finished stage's `_stage` token (already cancelled if Stop was pressed while the previous stage's finally ran, so the next stage would stop at once) and builds a `WhenAll`. `ContinueAfter` uses the ordinary one-operation path with identical visible behaviour; B019's spec should be read with this method. B166 later deletes `Next`, `chained` and `WhenAll` altogether.
- **Tests:** There is no automated window test (no headless harness, see the intro). Verify manually: after Motion's Continue, Escape stops Rumble and the stage list stays disabled while Rumble runs; closing during Rumble waits for its final zero; pressing Continue anyway while "Try restoring again" runs does nothing until the retry ends. Then run `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Gui|FullyQualifiedName~Wizard"` to confirm nothing else moved. The automated regression ("after Continue, the operation is not completed until the next stage ends; close waits for its finally") lands in `WizardSessionTests` in B166.
- **Plan v2:** B019.
- **Related:** LABUI-006 (the structural fix), LABUI-001.

## Medium

### LABUI-002: Review never confirms a real TDP or processor-power test

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Wizard/LabReview.Power.cs` (`ReviewPower`: the `passed` computation and the `outcome switch` that builds `LabReviewItem`s); writers in `Gui/WizardWindow.Power.cs` (`RunAsusTdpAsync` and `RunMsiTdpAsync` write `"applied-readback-matched"` / `"readback-mismatch"`) and `Gui/WizardWindow.ProcessorPower.cs` (`RunAmdPowerAsync` and `RunIntelPowerAsync`, feature `processor-power` over `ryzen-smu` and `kx`); `Wizard/LabPowerResults.cs` (`LabPowerSummary.IsPass`); fixture in `tests/WSGM.DeviceLab.Tests/Wizard/LabReviewTests.cs` (power-tests fixture with `Outcome = "passed"` for TDP).
- **Problem:** Review counts a run as passed only when `outcome == "passed"`. The wizard writes `"applied-readback-matched"` for every TDP and processor test, so real reports review TDP as Unresolved and `promote` never confirms the mechanism. `LabPowerSummary.IsPass` already accepts both strings, so the tester's stage summary and the developer's review disagree. The test fixture writes `"passed"`, a shape the wizard never produces, so the suite stays green. Simply switching to `IsPass` would introduce a new problem: a passed `processor-power` run has no matching record mechanism, so it would become a `New` proposal that `PromotedByDefault` writes into a curated device record.
- **Best solution:** In `ReviewPower`:
  - Compute `passed` with `LabPowerSummary.IsPass(LabReviewArchive.Text(run["outcome"]) ?? string.Empty)` plus `restored == true`, as today.
  - Leave `failed` as it is (`outcome == "failed"` or `restored == false`). A `"readback-mismatch"` run then is neither passed nor failed and falls to the default arm: Unresolved when the record has the mechanism, Observed when it does not, never Disagrees. The no-readback rule forbids turning a missed readback into a contradiction.
  - Leave the `ran` filter as it is: TDP and processor results are written with `Restored = null` and get their value afterwards (`RestoreAsusPowerAsync`, the MSI `Restored: null` loop, `Restored = outcome.Restored` in the processor tests), so a run counts once its restore is known.
  - In the verdict switch, guard the `"passed" =>` New arm with `when PowerFeatures.Contains(feature)`. A passed result for any feature outside `PowerFeatures` (`processor-power` over `ryzen-smu` or `kx`, and the legacy `fan-full-speed` of older reports, see LABUI-V-002) then falls to the default arm. That arm yields Observed with no proposal, so `promote` never writes a generic processor test into a device record.

  No typed evidence records and no new fields: this is the small fix plan v2 chose over the LABUI-B1 refactor.
- **Tests:** In `LabReviewTests`, the power fixture already uses `LabPowerTestResult`, but with `Outcome = "passed"` for the `tdp`/`wmi-method` run, a value the wizard never writes for TDP. Change that run to `"applied-readback-matched"` (what `RunMsiTdpAsync` writes) so the fixture matches real reports. Add three cases:
  - a TDP run with `applied-readback-matched` and restored on a record with that mechanism: Confirmed;
  - a `readback-mismatch` run: Unresolved, not Disagrees;
  - a passed `processor-power`/`ryzen-smu` run: Observed with `Promotable == false` and `PromotedByDefault == false`.

  Filter: `--filter "FullyQualifiedName~LabReviewTests"`.
- **Plan v2:** B020.
- **Related:** LABUI-003, LABUI-V-002 (same drift class), LABUI-004 (no-change).

### LABUI-003: Aura and Claw lighting always review as failed

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Wizard/LabReview.Power.cs` (the `shown` list and the `seenRight` filter in `ReviewPower`); writers `Gui/WizardWindow.Power.cs` (`RunAuraAsync` adds `($"{colour}:{zone}", AuraAnswer(answer))`, with `"matched"` for a match) and `Gui/WizardWindow.ClawLighting.cs` (adds `($"claw:{colour.Name}", "matched")`); the summary's own rule is in `Wizard/LabPowerResults.cs` (`LabPowerSummary.Lighting`).
- **Problem:** Review passes a lighting transport only when `Shown == Seen`. Aura writes `("red:both rings", "matched")` and Claw writes `("claw:red", "matched")`, which never compare equal. So the curated `lighting`/`hid-output` mechanisms of `wsgm.claw-8-a2vm` and `wsgm.rog-ally-x` review as Disagrees, and so does their `DynamicLighting` capability, while the tester's stage summary (`LabPowerSummary.Lighting`, which accepts `"matched"`) says the colours were seen.
- **Best solution:** Change the `seenRight` predicate to `item.Shown != "off" && (item.Seen == "matched" || item.Shown == item.Seen)`, exactly the rule `LabPowerSummary.Lighting` already uses. Lamp-array answers (shown colour name equals seen colour name) keep working. Add no lighting fields and no legacy-shape fallback.
- **Tests:** In `LabReviewTests`: a `wsgm.rog-ally-x` report with `{ shown: "red:both rings", seen: "matched" }` gives lighting `hid-output` Confirmed and `capability:DynamicLighting` Confirmed. A Claw report with `"claw:red"`/`"matched"` gives Confirmed. All answers `"different"` gives Disagrees. A lamp-array answer still passes. Filter: `~LabReviewTests`.
- **Plan v2:** B020.
- **Related:** LABUI-002, LABUI-V-002.

### LABUI-006: Operation chaining by flags and Task.WhenAll emulates a loop

- **Severity:** medium (the verifier inverted the original diagnosis; the live defect is split out as LABUI-V-001, high)
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs`: `Run(..., bool chained)`, `RunCore`, `Next`, `StartStage(id, fromCompletedOperation)`, `ShowStage(id, fromCompletedOperation)`, `NavigateToStageAsync`, the fields `_navigating`, `_requestedStage`, `_running`, `_closing`, `_confirmingClose`, `_closeReady`, and the error page built in `RunCore`.
- **Problem:** A stage that calls `Next` starts the next stage inside its still-running operation, and `_operation = Task.WhenAll(outer, operation)` papers over it. Navigation adds `_navigating` and `_requestedStage`, and close adds `_closing`, `_confirmingClose` and `_closeReady`. The flags interact in non-obvious ways (LABUI-V-001 is one consequence). The error page's "Continue with the next step" reads the field `_running` instead of the `running` value `RunCore` captured, so a later stage start changes which stage it continues from.
- **Best solution:** In B166, replace the chaining with one sequential loop in `WizardSession`:
  - `RunFromAsync(string id)` runs `while (next is not null) { next = await RunStageAsync(next, stageToken); }` as the single tracked operation. Stage controllers return the next stage id, or null when they leave a result page up.
  - Result pages (Power, Motion, failed Rumble: "Run again" / "Continue") stay outside the running operation: the controller returns null, and the page's Continue calls `session.ContinueAfter(id)`, which starts a new operation. Stop, Escape and stage-list clicks on those pages therefore behave as today (verifier batch problem 8).
  - `Navigate(id)` records the requested id and cancels the stage token. The loop shows that stage only after the cancelled stage's finally blocks have run.
  - Delete `chained`, `fromCompletedOperation`, `Task.WhenAll`, `_navigating`, `_requestedStage` and `Next`. `_closing` becomes the session's cached close task, so a second close request returns the same task. `_refreshing`, `_confirmingClose` (the dialog) and `_closeReady` (the window's own `Closing` gate) stay in the window.
  - The error page uses the captured stage id.
- **Tests:** In `WizardSessionTests`:
  - navigation during a running stage cancels it, waits for its finally, then shows the requested stage;
  - Stop marks the stage not done;
  - after a result page's Continue the session's operation stays incomplete until the next stage ends (the LABUI-V-001 regression);
  - close during a stage waits for that stage's finally.

  Filter: `~WizardSession`.
- **Plan v2:** B166.
- **Related:** LABUI-V-001, LABUI-001, LABUI-018, LABUI-019.

### LABUI-007: Power stage reports Completed after an error or an unverified restore

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs`, `RunPowerAsync`: the `catch (Exception ex) when (ex is not OperationCanceledException ...)` that shows the error and logs `"stage-error"`, and the `project.Finish(LabStages.Power, LabSegmentStatus.Completed, ...)` after the finally block. Compare `Gui/WizardWindow.Rumble.cs` `RunRumbleAsync`, which finishes Failed on the same condition.
- **Problem:** A stage error is caught and shown, then the segment is finished Completed. The report claims the power stage completed when it failed or left a setting not confirmed as put back.
- **Best solution:** In `RunPowerAsync` (`PowerStage` after B170):
  - Keep a local `string? stageError` set in the catch.
  - After the finally block, read `var pending = LabPowerChanges.PowerPending(await Task.Run(() => _machine.Read().Power));` once (today it is read later for the Continue label) and finish with `stageError is null && !pending ? Completed : Failed`.
  - Reuse `pending` for the "Continue anyway" label.

  The summary text is unchanged. This mirrors Rumble, where a failed final zero fails the stage.
- **Tests:** `tests/.../Wizard/Stages/PowerStageTests.cs`, with fake `ILabAtkAcpi`/`ILabMsiWmi` and a temp `LabMachineState`:
  - a thrown stage error finishes Failed;
  - an unverified restore keeps the record, finishes Failed, and shows the retry button without retrying;
  - a verified run finishes Completed.

  Filter: `--filter "FullyQualifiedName~PowerStage|FullyQualifiedName~PowerTests"`.
- **Plan v2:** B170.
- **Related:** LABUI-001, LABUI-012, LABUI-023.

### LABUI-008: Developer GUI discards a finished result when Cancel came late

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Gui/MainWindow.cs`, `RunAsync`: `current.Token.ThrowIfCancellationRequested();` right after `await Task.Run(() => operation(current.Token), current.Token)`.
- **Problem:** When Cancel is pressed while an operation is finishing, a completed result is turned into "Operation cancelled". For "Run attended hardware action" the result carries the restore and cleanup verification, so a late Cancel hides whether cleanup was verified.
- **Best solution:** Extract the runner into a non-Avalonia `Gui/GuiOperationRunner.cs`. It owns the `CancellationTokenSource`, the finished task, the close-after-operation flag and the `DeviceLabGuiOperationState` transitions. It exposes `RunAsync(Func<CancellationToken, Task<object?>> operation, Action<object?>? accepted, Func<object?, object?>? display)`, `Cancel()`, `WaitForIdleAsync()` and a `StateChanged` callback that `MainWindow` applies to its controls.
  - Delete the post-completion `ThrowIfCancellationRequested`. Operations throw their own `OperationCanceledException` when they actually stop early.
  - Serialize the display JSON inside the worker task, not on the UI thread (LABUI-011 part).
  - Delete the 1,024-character cut in `OperationFailureMessage`; the message is shown whole (LABUI-015 part).
  - The duplicate-operation refusal text, cancel button and close-wait behaviour stay identical.
- **Tests:** Replace `tests/.../Gui/DeviceLabGuiOperationStateTests.cs` with `GuiOperationRunnerTests`:
  - a second start while one runs is refused with today's text;
  - cancel before completion gives Cancelled and keeps the last successful result;
  - an operation that completes after a late cancel gives Succeeded;
  - close waits for the running operation, then closes once.

  Filter: `--filter "FullyQualifiedName~Gui"`.
- **Plan v2:** B165.
- **Related:** LABUI-025, LABUI-015, LABUI-011.

### LABUI-010: Process globals feed the windows

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Gui/DeviceLabGui.cs` (static `Wizard` property set by `RunWizard`), `Gui/App.cs` (`OnFrameworkInitializationCompleted` reads `DeviceLabGui.Wizard`), `Gui/WizardWindow.cs` (`_machine = LabMachineState.ForCurrentUser`, `DeviceKnowledgeBase.Default` in `RunIdentityAsync`), `Gui/WizardWindow.Hardware.cs` (`LabControllerInit.HasPending`, `HasControllerModePending`, `LabModeCommands.HasPending` static `File.Exists` checks; `DeviceKnowledgeBase.Default`), `Gui/MainWindow.cs` constructor (`new DeviceLabApplication(...)`).
- **Problem:** Windows reach for mutable statics and ambient singletons, so no owner can be built with test instances, and the composition is spread over the views.
- **Best solution:**
  - In B165, `App` gets one constructor parameter, `Func<Window> mainWindow`, and `OnFrameworkInitializationCompleted` sets `desktop.MainWindow = mainWindow()`. `DeviceLabGui.Run(application, boundaries)` builds the app with `AppBuilder.Configure(() => new App(() => new MainWindow(application, boundaries)))` and `RunWizard(options, boundaries)` with `() => new WizardWindow(options, boundaries)`; `Program` creates the `DeviceLabApplication` (today built in the `MainWindow` constructor) only on the developer-tabs path, so the wizard never builds one. Delete the static `DeviceLabGui.Wizard`.
  - In B166, `WizardWindow` receives a `WizardSession` built from `WizardServices`, which `Program` composes with the production `LabMachineState`, `DeviceKnowledgeBase`, `LabPawnIo`, `LabRecovery` and factories. The pending-record checks go through the one path helper B158 introduces, not through fixed static paths.
  - No DI container and no service locator.
- **Tests:** `GuiOperationRunnerTests` and `WizardSessionTests` construct their owners with temp-root instances; no test touches `%LOCALAPPDATA%`. Filters: `~Gui` (B165), `~WizardSession` (B166).
- **Plan v2:** B165 (resolves), completed by B166 and B158.
- **Related:** plan claim C11; LABUI-032; LABCORE-020.

### LABUI-014: Export drops or refuses evidence by size and count

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Wizard/LabExport.cs` (`MaximumFileBytes`, `MaximumTotalBytes`, `MaximumFiles` and their uses in `Prepare`); `Wizard/LabReviewArchive.cs` (`MaximumTotalBytes = LabExport.MaximumTotalBytes`, and `ReadJson` uses `LabReport.MaximumEntryBytes`); `Wizard/LabReport.cs` (`MaximumEntryBytes`).
- **Problem:** A project file over 32 MiB is silently moved to "Kept on this computer". More than 4,096 files or more than 256 MiB refuses the whole export. Both break the no-arbitrary-limits rule for evidence the tester produced on their own machine.
- **Best solution:**
  - Delete the three constants and their checks from `LabExport.Prepare`, and remove the `InvalidDataException` doc line.
  - Keep the in-memory build, preview and single write: the subtree AGENTS rule says "The shared report is built in memory, previewed, and written once", and the preview must equal the bytes written. Do not add the review's staging zip, which would add a file lifecycle and change a documented rule.
  - `LabReviewArchive` gets its own constants as the untrusted-archive guard listed in D2: `MaximumEntryBytes = 64 MiB` (moved from `LabReport`, see LABUI-013) and `MaximumTotalBytes = 256 MiB` as literals, so review behaviour is unchanged. These bounds count only bytes the review actually reads, and the review reads JSON evidence, not the raw ACPI tables, so a large export stays reviewable. They refuse with `InvalidDataException`, never truncate.
  - `LabReviewArchive.MaximumEntries` (65,536, checked in `Open`) stays unchanged as part of the same D2 guard: it refuses an untrusted archive whose central directory is implausibly large and never drops entries. Only an export of more than 65,536 files would hit it, far above anything the wizard writes.
  - Each constant moves in the same change that deletes its old home, so the build stays green.
- **Tests:** `LabExportTests`: a project with a 33 MiB `.dat` file previews and writes it with a matching SHA-256, and nothing is listed as too large. Existing refusal of an existing target stays. In the reports suite, an archive entry over the review bound refuses with `InvalidDataException`. Filter: `--filter "FullyQualifiedName~LabExportTests|FullyQualifiedName~LabProjectTests|FullyQualifiedName~Reports"`.
- **Plan v2:** B164. Decided: D2 keeps the review-archive guard (entry, total and entry-count bounds that refuse, never truncate); every other export cap goes.
- **Related:** LABUI-013, LABUI-030; LABCORE-027 (same rule on the capture side).

### LABUI-027: Start-up recovery is inline UI code, and an unreadable record is overwritten

- **Severity:** medium (verifier raised it from low)
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs` `Start` (PawnIO reconcile, power recovery, rumble recovery and controller-init recovery in sequence); `Wizard/LabMachineState.cs` `Read` (maps `IOException`, `JsonException` and `UnauthorizedAccessException` to an empty record) and `Update` (writes `change(Read())`).
- **Problem:** A transient sharing violation or a corrupt file reads as "nothing recorded", and the next `Update` writes that empty record back, erasing pending restores. The recovery order lives in a window method with three different stores and no containment, so one throwing item skips the rest.
- **Best solution:**
  - `LabMachineState.Read` returns an empty record only when the file does not exist and throws for anything else (same signature). `Update` can then never overwrite what it could not read, and the existing page error shows the problem.
  - A new `Wizard/LabRecovery` owns the start-up order. It takes the owner reservation around controller and mode-command recovery (refusing with the existing "Close WSGM, then start Device Lab again" wording), contains every item with one rule (all exceptions except `OutOfMemoryException`) so one failure never skips the others, starts the worker only when worker-bound work is pending (LABUI-V-005), and returns the notice lines.
  - The wizard (later `WizardSession.StartAsync`) renders those lines with today's strings.
  - Records stay where they are, with one path helper and no fold or migration.
  - The close path must survive the new throw, or an unreadable record would keep the tester from closing the Lab and leave the owner reservation, worker and HidHide entry held. Two edits in `Gui/WizardWindow.cs`, in the same batch:
    - `ConfirmCloseAsync`: a `PowerRestorationPending()` call that throws counts as pending (catch `IOException`, `JsonException` and `UnauthorizedAccessException` around it and set `pending = true`), so the existing "Close anyway" dialog appears. Today the exception faults the discarded `ConfirmCloseAsync` task and every later close request does nothing.
    - `CloseAfterCleanupAsync`: the catch around `Task.Run(RestoreHidHide)` widens to `when (ex is not OutOfMemoryException)`. `RestoreHidHide` starts with `_machine.Read()`, and a `JsonException` would otherwise escape before capture, worker and reservation are disposed and `_closeReady` is set. The record stays, so the next start offers the removal.
- **Tests:** `--filter "FullyQualifiedName~LabMachineState|FullyQualifiedName~LabRecovery|FullyQualifiedName~LabPowerRecovery|FullyQualifiedName~PreflightTests|FullyQualifiedName~LabPawnIo|FullyQualifiedName~LabModeCommands"`. Cases: an unreadable record makes `Update` throw and leaves the file untouched; one recovery item throwing still runs the others; no worker starts with an empty record. Check manually that the wizard closes with a corrupt `machine-changes.json` (the window path has no harness until B166, whose `WizardSessionTests` then add "close with an unreadable record still disposes and releases once").
- **Plan v2:** B158 (owned by the labcore batch).
- **Related:** LABCORE-001, LABCORE-010, LABCORE-011, LABCORE-V-001, LABCORE-V-005; LABUI-V-005; plan claim C5.

### LABUI-V-002: MSI fan test can never confirm the curated fan mechanism and promotes a bogus one

- **Severity:** medium (found by the verifier)
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs`, `RunMsiFansAsync` (`Feature = "fan-full-speed", Transport = "wmi-method"`); `Knowledge/Devices/wsgm.claw-8-a2vm.json` (declares `fan` over `wmi-method`); `Wizard/LabReview.Power.cs` (`PowerFeatures`, `FeatureCapabilities`); `Wizard/LabPowerResults.cs` (`LabPowerSummary.Name` entry `"fan-full-speed"`).
- **Problem:** Review matches features exactly and maps only `fan` to `FanControl`. On every Claw report, the record's fan mechanism is "Not tested in this report", `FanControl` is never confirmed, and a passed run becomes a `New` mechanism `fan-full-speed:wmi-method` that `PromotedByDefault` writes into the curated record (the parser does not restrict feature names).
- **Best solution:**
  - The stage writes `Feature = "fan"`. The detail text ("Set both fans to full speed and captured RPM.") and the page heading "Full-speed fans (100%)" stay.
  - Delete the now-unused `"fan-full-speed"` arm from `LabPowerSummary.Name`. The one tester-visible consequence is that the Claw stage summary says "fans" where it said "100% fan speed", the same word the Asus fan test already uses.
  - Older reports that still carry `fan-full-speed` are handled by the LABUI-002 guard (outside `PowerFeatures`, so Observed and never promoted). No alias table.
  - `LabPowerPlan.For`'s coverage switch has no arm for `wmi-method` `fan`, so the Claw fan mechanism is also listed under the stage's "No test for this build covers" line although the test runs. `RunMsiTestsAsync` runs the fan test when the plan has an MSI layout with `HasTdp` or `Charge`, and the worker's checkpoint captures `Fans` exactly when `FanCustom` and `FanFullSpeed` are set (`LabMsiWmi`, `Fans = _layout.FanCustom is not null && _layout.FanFullSpeed is not null ? ReadFans() : null`). Add the arm `{ Transport: "wmi-method", Feature: "fan" } => msi is { FanCustom: not null, FanFullSpeed: not null } && (msi.HasTdp || msi.Charge is not null)`.
- **Tests:** In `LabReviewTests`: a Claw report whose `fan`/`wmi-method` run passed and was restored gives the mechanism Confirmed and `capability:FanControl` Confirmed. A legacy `fan-full-speed` run gives Observed and is not promotable. In `LabPowerPlanTests`: the `wsgm.claw-8-a2vm` plan no longer lists `fan (wmi-method)` in `Untested`. Filter: `--filter "FullyQualifiedName~LabReviewTests|FullyQualifiedName~LabPowerPlanTests"`.
- **Plan v2:** B020. B020's file list omits `Wizard/LabPowerResults.cs`, `Wizard/LabPowerPlan.cs` and `tests/.../Wizard/LabPowerPlanTests.cs`; add them, and widen its test filter as above.
- **Related:** LABUI-002, LABUI-003.

## Low

### LABUI-009: Curated facts and device heuristics as UI literals

- **Severity:** low
- **Where:**
  - `src/WSGM.DeviceLab/Gui/WizardWindow.ClawLighting.cs`: `plan.Record?.Id != "wsgm.claw-8-a2vm"`.
  - `Gui/WizardWindow.Power.cs`, `RunAuraAsync`: `plan.Record!.Id.Contains("xbox", ...)` and the local `zones` array.
  - `Gui/WizardWindow.Buttons.cs`: the `"mouse"` / `" 000D:"` device filter in the liveness and quiet checks.
  - The same filter in `Wizard/LabReview.Buttons.cs` and `Wizard/LabInputAnalysis.cs`.
  - `Gui/WizardWindow.ProcessorPower.cs`: `CpuVendor`.
- **Problem:** The record id, a substring heuristic, the Aura zone names, a duplicated pointer filter and a CPUID read sit in UI code, against "keep hardware policy out of UI code" and "curated facts come from the record".
- **Best solution:**
  - Pointer filter (B168): add `LabInputAnalysis.IsPointer(string? description)` returning `description` starting with `"mouse"` or containing `" 000D:"`. Use it at the three device-description sites. The activity-detail `"mouse"` checks in `Buttons.cs` and `Sleep.cs` stay as they are, because they deliberately do not drop touch activity.
  - Claw gate (B171): `LabPowerPlan` derives a Claw lighting layout from the record's `lighting`/`hid-output` mechanism with `vendorId` `0DB0` (the `wsgm.claw-8-a2vm` record carries `vendorId`, `reportLength`, `header` and `profileAddress`), the same way `AuraLayout` does, through an `IsClawLighting(mechanism)` predicate beside `IsAura`. `LightingTests` runs the Claw test only when that layout is present and passes it to `LabClawLighting` (LABCORE-025). No schema change. The coverage arm becomes `{ Transport: "hid-output", Feature: "lighting" } => aura is not null && IsAura(mechanism) || claw is not null && IsClawLighting(mechanism)`, so the Claw lighting mechanism stops being listed under "no test for", a correction, not a redesign.
  - Aura zones (B171): the zone names become constants beside `LabAuraLighting`, which is transport code.
  - The `"xbox"` Dynamic Lighting hint (B171): delete the branch. `RunAuraAsync` runs only when `plan.Aura` is set. `IsAura` accepts only the RC72LA endpoint (0B05:1B4C, FF31:0080), which only `wsgm.rog-ally-x` declares, and the `wsgm.xbox-rog-ally-x` record has no such mechanism (its FF31:0080 has no output report). So the branch is unreachable today, and the shown text does not change.
  - `CpuVendor` (B170) moves with `ProcessorPowerTests`.
- **Tests:** `ButtonsStage` tests use `IsPointer` through a fake capture; `LabPowerPlanTests` gives a Claw layout for the Claw record and none for the Ally records. Filters: `~ButtonsStage|~MotionStage` (B168), `~LightingTests|~SleepStage|~LabPowerPlan` (B171).
- **Plan v2:** B168 (resolves), with the lighting part in B171 and `CpuVendor` in B170. B168's file list names `Capture/Live/LabInputAnalysis.cs`; the file is `Wizard/LabInputAnalysis.cs`.
- **Related:** plan claim C8; LABCORE-025.

### LABUI-011: Blocking file and system calls on the UI thread

- **Severity:** low
- **Where:**
  - `src/WSGM.DeviceLab/Gui/WizardWindow.cs`: `_machine.Read()` in `Start`, `ShowFinishAsync` and `PowerRestorationPending`; `LabControllerInit.HasPending`.
  - `Gui/WizardWindow.Power.cs`: `_machine.Read()` and `Update` in `RunPowerAsync`, `RunMsiFansAsync`, the restore methods and `RestorePendingAsync`; `LabPowerTelemetry.AcLine()` in the power-source repeat.
  - `Gui/WizardWindow.ProcessorPower.cs` and `Gui/WizardWindow.ClawLighting.cs`: `_machine.Update`.
  - `Gui/WizardWindow.Hardware.cs`: the `File.Exists` pending checks.
  - `Gui/MainWindow.cs`: `JsonSerializer.Serialize` of the result in `RunAsync`.
- **Problem:** The subtree AGENTS rule requires files, registry and drivers off the UI thread. These calls block it, and the machine-record calls hold a lock while reading and writing durably.
- **Best solution:** When each piece moves into its owner, every `_machine.Read/Update`, `File.Exists` and `AcLine` call goes through `Task.Run` (or the session's worker context). The window only renders. Use `ForgetRecordedAsync`, which already does this, for record clears (LABUI-012). `MainWindow`'s serialization moves into the runner's worker task (LABUI-008).
- **Tests:** No dedicated test; the moved code is covered by the stage tests of each batch.
- **Plan v2:** B170 (resolves). The same rule applies to the `WizardWindow.cs`/`Hardware.cs` sites in B166, `ClawLighting` in B171 and `MainWindow` in B165.
- **Related:** LABUI-001, LABUI-008, LABUI-012.

### LABUI-013: Two readers of the same .wsgmlab, one of them unhardened

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Wizard/LabReport.cs` (`Read`, private `ReadJson`, `MaximumEntryBytes`) versus `Wizard/LabReviewArchive.cs` (`Open`, `Segments`, `ReadEvidence`, `JsonFiles`, typed leaf readers).
- **Problem:** `LabReport.Read` opens the archive with `ZipFile.OpenRead`, trusts the declared entry length, has no total read bound, does not validate segment ids, and throws `InvalidOperationException` from `GetValue<int>` on a malformed `attempts`. `LabReviewArchive` is the hardened twin: counted reads, a total bound, id validation and tolerant typed readers.
- **Best solution:** Reimplement `LabReport.Read` on `LabReviewArchive`:
  - `using var archive = LabReviewArchive.Open(path);`
  - device and tool version from `archive.Manifest`;
  - segments from `archive.Segments`;
  - each segment's evidence list from a new `archive.Files(segment)`: every entry name that starts with `segment.Prefix`, nested paths and non-JSON files included, in ordinal order, or empty when `Prefix` is null. That is exactly the list `report` prints today (`names.Where(StartsWith(prefix)).Order()`). `JsonFiles` stays unchanged (direct `.json` children only, used by the motion review);
  - button candidates from `archive.ReadEvidence(segment, "candidates.json")`, with the candidate array read through `LabReviewArchive.Objects` instead of `AsArray()`, so a malformed array reads as empty instead of throwing.

  Delete `LabReport.ReadJson` and move `MaximumEntryBytes` into `LabReviewArchive` in the same change. The `report` output shape stays identical, including the 5-candidate display summary (LABUI-034, accepted).
- **Tests:** New `tests/WSGM.DeviceLab.Tests/Reports/LabReportTests.cs` (no `LabReport` test exists today): a manifest with `"attempts": "x"` reads as 0 attempts instead of throwing; an entry larger than the bound refuses with `InvalidDataException`; an invalid segment id refuses; a normal export lists the same evidence paths, nested ones included, as before. Filter: `--filter "FullyQualifiedName~Reports"`.
- **Plan v2:** B163.
- **Related:** LABUI-014, LABUI-017.

### LABUI-015: Small UI and review caps that truncate or drop

- **Severity:** low
- **Where:**
  - `src/WSGM.DeviceLab/Gui/WizardWindow.cs`: `_note` with `MaxLength = 2000`.
  - `Gui/WizardWindow.Buttons.cs`: the extra-button loop `extra <= 12`.
  - `Gui/MainWindow.cs`: `OperationFailureMessage` cuts at 1,024 characters; `MaximumRecentPathsBytes`, `MaximumRememberedPathCharacters`, `MaximumRecentPathCount`, the 128-character key filter and the longest-first eviction loop in `LoadRecentPaths` and `SaveRecentPaths`.
  - `Wizard/LabReview.Power.cs`: `Compact` cuts readback JSON to 120 characters, and the detail is copied into the promoted provenance note by `LabPromote.Provenance`.
- **Problem:** Each cap silently drops or truncates content: tester notes, extra buttons, failure messages, remembered paths (only 16 compile-time keys exist), and readback evidence persisted into curated records.
- **Best solution:**
  - B163: delete `Compact` and drop the `" (read back ...)"` suffix from the power review detail. The evidence reference already points at the whole readback, so neither the review detail nor the provenance note carries truncated JSON. Including the full JSON in a curated provenance note would bloat the record for no reader.
  - B165: delete the 1,024 cut. Move recent paths to `Gui/RecentPaths.cs` with `Load` and `Save`. `Load` deserializes `Dictionary<string,string>` and keeps entries with a non-empty key and value (type checks only); the file-size check that reads an over-64 KiB file as empty goes too. `Save` writes the dictionary as is, with the same temp-file-and-move. The three constants, the key-length filter, `Take` and the eviction loop go, and so does the `input.Text.Length > MaximumRememberedPathCharacters` early return in `MainWindow.RememberPath`, which silently skips remembering a long path.
  - B168: the extra-button loop runs until the tester answers that there are no more buttons.
  - The notes `MaxLength` is not named by any plan v2 batch. Remove it in B166 together with the notes rework (LABUI-018).
- **Tests:** `RecentPathsTests`: a round trip of every key with a 5,000-character path. `Load` and `Save` take the file path as a parameter (`MainWindow` passes today's `RecentPathsFile()`), so the test uses a temp file and never touches `%LOCALAPPDATA%`. A `LabReviewTests` case: the detail of a TDP run carries no `"read back"` text and no truncation marker. Filters: `~Reports`, `~Gui`.
- **Plan v2:** B163 (resolves), with parts in B165, B166 and B168.
- **Related:** LABUI-008, LABUI-018, LABUI-034 (display-only caps, accepted).

### LABUI-017: Offline report tooling sits in the wizard folder

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Wizard/{LabReport, LabReview, LabReview.Buttons, LabReview.Motion, LabReview.Power, LabReview.Rumble, LabReviewArchive, LabPromote}.cs`; consumers `Cli/DeviceLabCli.cs`, `Gui/MainWindow.cs`, `Scaffolding/ScaffoldFromLabProjectWorkflow.cs`; test `tests/.../Wizard/LabReviewTests.cs`.
- **Problem:** These are offline developer tools for returned `.wsgmlab` files, used by the CLI, the developer tabs and scaffolding, but they live beside the machine-changing tester code.
- **Best solution:** Move them to `src/WSGM.DeviceLab/Reports/` with namespace `WSGM.DeviceLab.Reports`, no behaviour change. Update the three consumers' usings and move `LabReviewTests` to `tests/WSGM.DeviceLab.Tests/Reports/`. Wizard code must not depend on `Reports`, so `DescribeButton` moves out first (LABUI-022). `Reports` may reference `Wizard` constants (`LabStages`, `LabProject.ManifestFileName`, `LabPowerSummary`).
- **Tests:** Existing review tests under the new namespace. Filter: `~Reports`.
- **Plan v2:** B163.
- **Related:** plan claim C15; LABUI-013, LABUI-022.

### LABUI-018: Notes bypass the one-operation rule and close does not wait for them

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs`, `SaveNote` (fire-and-forget `Task.Run(...).ContinueWith(...)`) and `CloseAfterCleanupAsync`.
- **Problem:** A note write is not awaited by anything. The completion clears the text box even if the tester kept typing, and close can release the project while a note is still being written.
- **Best solution:**
  - `WizardSession.SaveNoteAsync(string? stage, string text)` writes the note attempt exactly as today (`BeginAttempt("notes")`, `WriteEvidence("note")`, `Finish("notes")`) on a worker and keeps the returned task. `CloseAsync` awaits it after the stage and before undoing HidHide.
  - Notes stay allowed while a stage runs; they do not take the stage operation.
  - The window's "Save note" button is disabled while the save runs, so there is only ever one note task and no queue is needed.
  - On success the box is cleared only if its trimmed text still equals the saved text. On failure it shows today's "The note could not be saved: ..." text.
- **Tests:** `WizardSessionTests`: close during a slow note save waits for it; two notes become two attempts. Filter: `~WizardSession`.
- **Plan v2:** B166.
- **Related:** LABUI-006, LABUI-030, LABUI-015 (notes `MaxLength`).

### LABUI-019: Two cancellation tokens with swapped-looking names

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Hardware.cs` (`Lifetime` is the stage token `_stage.Token`); `Gui/WizardWindow.cs` (`_lifetime` is the window token; `RunIdentityAsync` passes `_lifetime.Token` to `LabIdentity.Observe`; `RunPreflightAsync` passes it to `_pawnIo.InstallAsync` and `ReplaceAsync`; `ShowFinishAsync` passes it to `RemoveAsync`).
- **Problem:** The names invite mistakes, and the identity inventory runs on the window token, so Stop and Escape cannot stop it.
- **Best solution:** In B166, name them `WizardStageContext.Cancellation` (stage) and `WizardSession.Closing` (window), and choose each one deliberately:
  - Identity collection takes the stage token, since it is read-only.
  - PawnIO install, replace and remove keep the window token on purpose, with a comment. The installer has its own deadline, `LabPawnIo` records before it runs, and abandoning an installer wait on Stop (for example between uninstall and install in `ReplaceAsync`) would leave the tester without PawnIO and with an unclear record. Only closing the window stops waiting for it, as today.
- **Tests:** `WizardSessionTests`: Stop during a fake identity collection cancels it. Filter: `~WizardSession`.
- **Plan v2:** B166.
- **Related:** LABUI-006, LABUI-001.

### LABUI-020: System dump finishes only after Continue, and abandoned sections are silent

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.SystemDump.cs`, `RunSystemDumpAsync`: `await AskAsync(page, "Continue")` before `project.Finish(...)`, and `Task.Run(() => LabSystemDump.Run(section, context), Lifetime).WaitAsync(Lifetime)`.
- **Problem:** Stop at the final Continue prompt leaves a complete, written dump marked not started. A section abandoned by `WaitAsync` on Stop or close keeps running on its thread with no trace that it was abandoned.
- **Best solution:** In `SystemDumpStage`:
  - Call `project.Finish(LabStages.SystemDump, Completed, summary, ...)` right after writing `system-dump`, then show "Done" and the Continue prompt.
  - Wrap the per-section await in `catch (OperationCanceledException) { LabTrace.Write($"system dump: {section.Title} abandoned, still running"); throw; }`. That is the whole report: read-only sections need no wait, no tracking state and no close-time list, and the process exit ends their threads.
- **Tests:** `SystemDumpStageTests` with `FakeWizardUi`: Stop at the Continue prompt leaves the segment Completed. Filter: `--filter "FullyQualifiedName~Stages"`.
- **Plan v2:** B167.
- **Related:** LABUI-001.

### LABUI-021: Wrong evidence text and stale comments in rumble and power code

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Rumble.cs`: the `Method` string in `WriteRumbleEvidence` ("at 10 to 500 ms") against `RumblePulseLengths = [5, 10, 25, 50, 100, 250, 500]`, and the comment "Pulse page lengths; ..." placed above `MaxRumbleReplays`. `Gui/WizardWindow.Power.cs`: the comment "The TDP test ..." above `RunMsiFansAsync`.
- **Problem:** The rumble calibration evidence misstates the method, and two comments describe the wrong member.
- **Best solution:**
  - Build the method text with `$"... at {RumblePulseLengths[0]} to {RumblePulseLengths[^1]} ms; ..."`, so evidence now says 5 to 500 ms.
  - Move the pulse-lengths comment onto `RumblePulseLengths` and give `MaxRumbleReplays` its own one-line comment.
  - Rename the fan comment to "The full-speed fan test ...".
- **Tests:** None beyond the review filter; it is evidence text. Filter: `~LabReviewTests`.
- **Plan v2:** B020. B020's file list omits `Gui/WizardWindow.Rumble.cs`, where the method text and the two rumble comments live; add it.
- **Related:** none.

### LABUI-023: WriteEvidenceOnce hides every IO error

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs`: `WriteEvidenceOnce` (catches every `IOException`), `WriteContext`, and their callers, including the checkpoint callbacks of the MSI and Asus power families.
- **Problem:** Disk-full or access errors on `identity-baseline` and `original-*` evidence are swallowed as "already written". The verifier showed that no collision case exists: every `RunPowerAsync` begins a new attempt, `identity-baseline` is written once per attempt, and every other name carries a distinct pass label.
- **Best solution:** Delete `WriteEvidenceOnce` and `WriteContext` and call `project.WriteEvidence` directly. Inside a checkpoint callback a write failure then throws, the checkpoint fails, and no device write follows (fail-closed).
- **Tests:** `PowerTestGuard` tests: a throwing evidence write inside the checkpoint callback means the fake transport receives no write. Filter: `~PowerStage|~PowerTests`.
- **Plan v2:** B170.
- **Related:** LABUI-022, LABUI-007.

### LABUI-025: Test quality for the GUI, wizard and review

- **Severity:** low
- **Where:** `tests/WSGM.DeviceLab.Tests/Gui/DeviceLabGuiOperationStateTests.cs` (record transitions only); `tests/.../Wizard/LabButtonsTests.cs` (tests `LabInputCapture.NextId`, a capture concern); `tests/.../Wizard/LabReviewTests.cs` (hand-copied evidence shapes); no wizard flow, close or recovery test.
- **Problem:** The runner logic, the wizard's one-operation gate and the ordered close have no tests. A capture test sits in the wizard suite. Review fixtures drift from what the wizard writes, which is how LABUI-002 and LABUI-003 stayed green.
- **Best solution:**
  - `GuiOperationRunnerTests` replaces the getter test (B165, see LABUI-008).
  - `WizardSessionTests` with `FakeWizardUi` covers the one-operation gate, navigation, Stop, notes and close order (B166).
  - Review fixtures are built from `LabPowerTestResult` and the other existing stage types serialized with `LabProject.JsonOptions` (B020).
  - Move the `NextId` test to `tests/.../Capture/` beside the capture tests.
- **Tests:** As listed. Filters: `~Gui`, `~WizardSession`, `~LabReviewTests`.
- **Plan v2:** B166 (resolves), with parts in B165 and B020.
- **Related:** LABUI-008, LABUI-002, LABUI-001.

### LABUI-032: Repository root found differently by the two windows

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/MainWindow.cs` constructor (current directory, then `AppContext.BaseDirectory`); `Gui/WizardWindow.cs` `Boundaries()` (current directory only, rebuilt per call in `PickProjectAsync` and `SaveReportAsync`).
- **Problem:** The output-path boundary that refuses the repository root differs by launch folder between the developer tabs and the wizard.
- **Best solution:** `Program` builds one `DeviceLabPathBoundaries` (B162 adds the single instance and facade for all `ForCurrentUser` sites) with the `MainWindow` rule: current directory, then executable folder. It passes that instance to `App`, which hands it to both windows. Delete `WizardWindow.Boundaries()` and the locator calls in `MainWindow`.
- **Tests:** `--filter "FullyQualifiedName~Gui"`. B162's application tests cover the single instance.
- **Plan v2:** B165, after B162.
- **Related:** LABUI-010; LABCORE-020, LABCORE-040.

### LABUI-033: Viewing "Finish and share" changes the machine

- **Severity:** low
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs`: `ShowFinishAsync` calls `Task.Run(RestoreHidHide)` and shows a warning on failure; the comment above `ShowStage` ("Only the finish page does work on selection ... restoring HidHide"); `CloseAfterCleanupAsync` also calls `RestoreHidHide`.
- **Problem:** Selecting the finish page removes the session's HidHide entry. Going back to Buttons afterwards silently runs without the allowance. The subtree AGENTS rule is "Selecting a stage only shows it".
- **Best solution:**
  - Remove the `RestoreHidHide` call and its warning from `ShowFinishAsync` (`FinishStage` after B167). Close stays the single removal point: `WizardSession.CloseAsync` already undoes the entry in its ordered close.
  - If that removal fails, the record stays, and the next start's preflight offers the existing "Left over from an earlier test" removal.
  - Update the `ShowStage` comment ("Only the finish page does work on selection, and that work (restoring HidHide, building the preview) changes no evidence" loses "restoring HidHide"). In `src/WSGM.DeviceLab/README.md` step 1, "removes exactly that entry when the test finishes or the window closes" becomes "removes exactly that entry when the window closes". The subtree AGENTS.md already says closing undoes the entry and needs no change.
  - The finish page's warning "The HidHide entry could not be removed: ..." goes with the call. This is the one visible change, and it is the point of the fix.
  - This deletes mechanism instead of adding a hint on hardware stages. Building the preview remains the finish page's only work, and it changes no evidence.
- **Tests:** `FinishStageTests` with a fake allowance: showing the finish page leaves the recorded entry in place; `WizardSessionTests`: close removes it once. Filter: `~Stages`.
- **Plan v2:** B167; add `src/WSGM.DeviceLab/README.md` to its file list.
- **Related:** never-strand rule (the lab's own entry only; the cloak itself is WSGM's and is not touched here).

### LABUI-V-003: Cancelled capture steps stay open, so a stopped baseline can teach input as noise

- **Severity:** low (found by the verifier)
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Buttons.cs` (`capture.BeginStep("buttons/baseline", true)` then `CountdownAsync` with no cancel path); `Gui/WizardWindow.Sleep.cs` (`BeginStep($"{LabStages.Sleep}/cycle")` before `Lifetime.ThrowIfCancellationRequested()`, and `BeginStep($"{LabStages.Sleep}/press")` with only a `finally` that unhooks `Activity`); `Capture/Live/LabInputCapture.cs` (`BeginStep` and `EndStep`; a non-baseline `BeginStep` does not clear the noise map).
- **Problem:** On Stop the stage throws before `capture.EndStep()`, so the step (and, for the baseline, the baseline flag) stays open until the next `BeginStep`. A tester who stops during "Hands off..." and then handles the device before Motion or Sleep feeds real input into the noise map used for attribution.
- **Best solution:** End the step in the cancel path, as `RunControlAsync` already does: `catch (OperationCanceledException) { capture.EndStep(); throw; }` around the baseline countdown and around both sleep waits. In the cycle wait, call `EndStep()` before `ThrowIfCancellationRequested`. No new state. After B161 the calls go to `ILabInputCapture`, whose step logic lives in `LabInputStepBuffer`.
- **Tests:** The defect is in stage code, which has no harness in B161, so B161 checks it by review of the three cancel paths and a manual Stop during "Hands off..." that must leave the wizard usable. `LabInputStepBufferTests` pins the property the fix relies on: after a baseline step ends, later input is not learned as noise (filter `--filter "FullyQualifiedName~LabInputStepBuffer"`). The automated regression lands with the moved code: `ButtonsStage` tests in B168 ("Stop during the baseline countdown calls `EndStep` on the fake capture") and `SleepStage` tests in B171 (same for both sleep waits).
- **Plan v2:** B161.
- **Related:** LABCORE-027 (capture caps, same batch).

### LABUI-V-004: The capture preview recomputes the bundle writer's lane bytes and hashes

- **Severity:** low (found by the verifier)
- **Where:** `src/WSGM.DeviceLab/Capture/CapturePrivacyPreview.cs` (`Create` passes `JsonSerializer.SerializeToUtf8Bytes(value, DeviceLabCompactJson.CaptureStreamEvent / CaptureAnalysisResult)` lambdas into `Lane`); `Capture/CaptureBundle.cs` (the same lambdas passed to `WriteNdjson` in the static class `CaptureBundleWriter`).
- **Problem:** The subtree rule requires preview, count and hash consistency, but the per-lane serialization exists twice and must stay byte-identical by hand.
- **Best solution:** Add two internal static methods to `CaptureBundleWriter` (there is no `CaptureBundle` type; the file holds `CaptureBundleLayout`, `CaptureSchemaValidator`, `CaptureHashFile` and `CaptureBundleWriter`), `StreamLine(CaptureStreamEvent)` and `AnalysisLine(CaptureAnalysisResult)`, each returning the UTF-8 bytes. Pass them by method group in both places. The newline framing already matches (`Newline` after each item in both). The global 64-sample and 128-byte blob-prefix caps in the preview are display-only, since every lane is listed whole with count, length and hash, so they stay (same reasoning as LABUI-034).
- **Tests:** `CapturePrivacyPreviewTests`: for a sample bundle, each preview lane's `Sha256` equals the bundle's written hash entry for that path. Filter: `--filter "FullyQualifiedName~CapturePrivacyPreview"`.
- **Plan v2:** B161.
- **Related:** LABUI-016 (the capture-preview part was refuted: both front ends already show this preview).

## Nit

### LABUI-012: Null-forgiving `changes.Power!` in restore paths

- **Severity:** nit
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs` (`RunMsiFansAsync`: two `_machine.Update(changes => changes with { Power = changes.Power! with { MsiFans = null } })`); `Gui/WizardWindow.ClawLighting.cs` (the same pattern in its finally).
- **Problem:** These are safe only because a checkpoint recorded `Power` first. In the Claw finally block, a throw there would be reported as "RGB restoration failed" after a successful restore and leave the checkpoint unreleased.
- **Best solution:** Use the existing `ForgetRecordedAsync(power => power with { MsiFans = null })` helper (which maps a null `Power` to null and runs off the UI thread) at all three sites.
- **Tests:** `PowerTestGuard` test: clearing with no recorded power leaves the record unchanged and does not throw. Filter: `~PowerStage|~PowerTests`.
- **Plan v2:** B170 (the Claw site moves in B171).
- **Related:** LABUI-011.

### LABUI-022: Small duplications

- **Severity:** nit
- **Where:**
  - Ask helpers: `src/WSGM.DeviceLab/Gui/WizardWindow.Hardware.cs` (`AskAsync` overloads), `Gui/WizardWindow.Motion.cs` (`AskWrappedAsync`), `Gui/WizardWindow.Rumble.cs` (`RumbleAskAsync`), and the redo-grid `TaskCompletionSource` in `Gui/WizardWindow.Buttons.cs`.
  - Button descriptions: `Wizard/LabButtonPlan.cs` `Belief` against `Wizard/LabReview.Buttons.cs` `DescribeButton`.
  - `Gui/WizardWindow.Power.cs` `WriteContext`, and the identical `plan.HasDeviceTests` / `_options.Elevated` branches in `RunPowerAsync`.
  - Two `Buttons` and `Heading` helpers in `MainWindow` and `WizardWindow`.
- **Problem:** The same logic is written several times and drifts. `Belief` already differs from `DescribeButton` for `Gamepad` and unknown sources.
- **Best solution:**
  - B163: one description method, `DeviceButtonKnowledge.Describe()` in `Knowledge/` (an instance method, so serialization is unaffected), with `DescribeButton`'s current body. Both `LabButtonPlan` and the review call it, and `Belief` is deleted. The wizard thus does not depend on `Reports`. The text lands in evidence `known` fields only.
  - B166: one `IWizardUi.AskAsync(labels, wrap, stop, elsewhere)` replaces the ask helpers.
  - B170: `WriteContext` goes with LABUI-023, and the two power branches merge into `if (plan.HasDeviceTests || _options.Elevated)`.
  - Leave the two `Buttons` and `Heading` helpers apart: their spacing, margins and font sizes differ, and merging them would change appearance.
- **Tests:** A `LabButtonPlan` test that the `known` text equals `Describe()`. Filters: `~Reports`, `~LabButtons`.
- **Plan v2:** B163 (resolves), with parts in B166 and B170.
- **Related:** LABUI-023, LABUI-017.

### LABUI-024: Leaked process handle and repeated help windows

- **Severity:** nit
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs`, `OpenInExplorer` (drops the `Process` from `Process.Start`) and `ShowHelp` (every click opens another window).
- **Problem:** The `Process` returned by `Process.Start` is never disposed, so its handle lives until finalization. Each "Help and licences" click opens another help window.
- **Best solution:**
  - `using var explorer = Process.Start(...);` in `OpenInExplorer`. That is the whole fix.
  - Leave `ShowHelp` as it is. Several help windows are owned by the wizard and close with it; nothing breaks, and a `_help` field with `Activate` and a `Closed` handler would add state for a nuisance, against the simplify rule. B165's "repeated help windows" item becomes no change.
- **Tests:** None (window code).
- **Plan v2:** B165. The fix is in `Gui/WizardWindow.cs`, which B165's file list omits; add it there.
- **Related:** none.

### LABUI-029: Magic answer indices

- **Severity:** nit
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Buttons.cs` (`RunControlAsync` answers 0 to 4; string device kinds in `Liveness`), `Gui/WizardWindow.Motion.cs` (step answers), `Gui/WizardWindow.Power.cs` (`AuraAnswer(int)` and `answer == 2`).
- **Problem:** Answers are compared as bare integers tied to label order, so reordering a label silently changes meaning.
- **Best solution:** When the code moves, give each prompt a small private enum in the same order as its labels (for example `ControlAnswer { Next, Again, Absent, SkipRest, Previous }`). The ask call returns the index, cast once. Labels and their order stay identical.
- **Tests:** Covered by the `ButtonsStage` redo and skip-the-rest tests. Filter: `~ButtonsStage|~MotionStage`.
- **Plan v2:** B168 (Buttons, Motion); the Aura answer moves in B171.
- **Related:** LABUI-022.

### LABUI-030: Export reads the manifest several times and is not cancellable

- **Severity:** nit
- **Where:** `src/WSGM.DeviceLab/Wizard/LabExport.cs`, `Prepare` (reads `project.Manifest` repeatedly while building `analysis.json`; takes no token).
- **Problem:** A note saved during `Prepare` can replace the manifest between reads, mixing two versions in `analysis.json`. A long export cannot be stopped.
- **Best solution:**
  - Read `var manifest = project.Manifest;` once at the top. The project replaces the immutable manifest as a whole under its lock, so one read is a consistent snapshot.
  - `Prepare(LabProject project, CancellationToken cancellationToken)` checks the token per file.
  - `FinishStage` (or `ShowFinishAsync` before B167) passes the stage token.
- **Tests:** `LabExportTests`: a pre-cancelled token throws `OperationCanceledException` and writes nothing. Filter: `~LabExportTests`.
- **Plan v2:** B164.
- **Related:** LABUI-014, LABUI-018.

### LABUI-V-005: Every elevated wizard start launches the hardware worker

- **Severity:** nit (found by the verifier)
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.cs`, `Start`: `if (_options.Elevated && await WorkerAsync() is var worker && ...)` evaluates `WorkerAsync()` before asking whether anything is recorded.
- **Problem:** The elevated worker process starts on every elevated open, even with an empty record, which adds needless process and hardware-service startup to every run, including the M01-42 elevated case.
- **Best solution:** In `LabRecovery` (B158), read the record once and start the worker only when it holds pending power (`LabPowerChanges.PowerPending`), rumble entries, or a curated-init record. PawnIO reconcile and mode-command recovery do not need the worker.
- **Tests:** `LabRecoveryTests`: with an empty record and a counting worker factory, no worker is started; with a pending power record, exactly one is. Filter: `~LabRecovery`.
- **Plan v2:** B158.
- **Related:** LABUI-027; LABCORE-011.

### LABUI-C-001: The Asus restore rewrites the restore result of the earlier power-source pass

- **Severity:** nit (found by the solution check)
- **Where:** `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs`, `RestoreAsusPowerAsync`: `foreach (var test in tests.Where(test => test.Transport == "atkacpi" && test.Feature != "charge-limit").ToArray()) { tests[tests.IndexOf(test)] = test with { Restored = restored }; }`. Compare `RunMsiTdpAsync`, which filters `test is { Transport: "wmi-method", Feature: "tdp", Restored: null }`.
- **Problem:** `tests` holds the results of both power-source passes (`ac-or-battery`, then `ac` or `battery` from `RunPowerSourceRepeatAsync`). The second pass runs only when the first was restored, so its restore overwrites the first pass's `Restored = true` with its own value. A failed second restore then records the first pass's TDP, mode and fan results as not restored in `power-tests.json`. The review verdict does not change (the second pass fails the feature anyway), but the evidence misstates what happened on the first power source.
- **Best solution:** Mark only results whose restore is still unknown, as the MSI path does: filter `test is { Transport: "atkacpi", Restored: null } && test.Feature != "charge-limit"`. One condition, no new state.
- **Tests:** None in B020 (window code). The `AsusPowerTests` tests of B170 add: two passes, the second restore fails; the first pass's results keep `Restored == true`. Filter: `~PowerStage|~PowerTests`.
- **Plan v2:** B020 (already edits `Gui/WizardWindow.Power.cs`); add LABUI-C-001 to its Resolves list.
- **Related:** LABUI-002, LABUI-007.

## Refuted or no-change

- **LABUI-004** (evidence schema as anonymous objects): no change. The review defects it was meant to prevent are fixed directly in B020, and typed evidence records are optional mechanism that plan v2 dropped. Review fixtures use the existing stage types instead (LABUI-025).
- **LABUI-005** (rumble `report` field read but never written): refuted. `LabRumbleRoute.Report => Layout?.Text` is not `[JsonIgnore]`, so HID routes serialize `"report"`, and the review puts it into promoted rumble mechanisms. The read stays.
- **LABUI-016** (developer GUI differs from the CLI): accepted parity gap. Adding a report button and a USB instance field would change the developer UI. The capture-preview part is refuted: both front ends already show `CapturePrivacyPreview`.
- **LABUI-026** (mode commands run in the wizard process): no change. They have no readable original and are recorded before sending, so they stay in the wizard process, now behind the owner reservation (B158, LABCORE-V-001). The AGENTS wording is corrected under LABCORE-026.
- **LABUI-028** (`LabProject` JSON options and attempt reuse): refuted. `JsonOptions` is a getter-only property whose options freeze on first use, and an empty attempt directory left by a crash is harmless because evidence is written only after `BeginAttempt` returns.
- **LABUI-031** (rumble slider frames allocate per frame): accepted. Device Lab slider frames are an attended diagnostic path, not a product high-rate path, and nothing is logged per frame.
- **LABUI-034** (review summary line caps): accepted. They are display-only summaries; the archive keeps everything and every lane is listed with count and hash. The one persisted truncation is fixed under LABUI-015.
