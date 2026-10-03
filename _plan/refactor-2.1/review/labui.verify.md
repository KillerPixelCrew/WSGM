# Device Lab part 1 (GUI, wizard, stages, export): adversarial verification of labui.md

Verifier: Claude. Baseline master `1329813f`, read-only. Every Gui/ file was read in full (App, DeviceLabGui,
DeviceLabGuiOperationState, MainWindow, WizardWindow and all ten partials), plus LabExport, LabReport,
LabReviewArchive, LabReview(.Power/.Rumble, parts of .Buttons), LabPowerResults, LabProject, WizardElevation,
CapturePrivacyPreview, LabRumbleRoutes (route record), LabMachineState.Read/Update, LabWorkerHost loop, the CLI
report/review/promote/scaffold/capture paths and the five scope tests. Nothing was built or run.

General note: several cited line numbers in labui.md do not exist in the current files (LabReport.cs is 112 lines
but is cited at 296-371; LabReviewArchive.cs is 292 lines, cited at 464-654; LabPowerResults.cs is 147 lines, cited
at 393-418; LabReview.Rumble.cs is 202 lines, cited at 244/368-371; WizardWindow.SystemDump.cs is 61 lines, cited at
118/136-141; LabProject.cs JsonOptions is at 129-137, not 189-197). The Gui partial, MainWindow, LabReview.Power,
LabReview.Buttons and test citations are accurate. Implementers must re-anchor by symbol, not by line.

## Refuted

- **LABUI-005** (review reads a rumble `report` field nobody writes). False. `LabRumbleRoute` declares
  `public string? Report => Layout?.Text;` without `[JsonIgnore]` (`Capture/Live/LabRumbleRoutes.cs:33-34`); only
  `Target`, `Endpoint` and `Layout` are ignored. `WriteRumbleEvidence` serializes `session.Discovery.Routes`
  (`WizardWindow.Rumble.cs:700-708`) through `LabProject.JsonOptions` (camelCase), so HID routes carry `"report"`.
  `LabReview.Rumble.cs:35-42,127,163-169` reads it and puts it into the promoted rumble mechanism's parameters.
  Deleting the read (as LABUI-B1 schedules) would silently drop the HID report layout from promoted records.
- **LABUI-028** (LabProject details). Neither half holds as a defect. (a) `JsonOptions` is a getter-only property
  (`LabProject.cs:129-137`) whose `JsonSerializerOptions` instance freezes on first use; it is not a writable static.
  (b) A crash between `CreateDirectory` and `Save` in `BeginAttempt` (`LabProject.cs:241-252`) leaves an empty
  directory, because evidence is only written after `BeginAttempt` returns; reusing it is harmless and
  `WriteEvidence` cannot collide. The proposed "skip to the next free attempt" adds mechanism for no defect.

## Corrected

- **LABUI-006** (operation chaining). The diagnosis is inverted. Power's Continue (`WizardWindow.Power.cs:165`)
  calls `Next` directly; the chained `Run` then sets `_operation = Task.WhenAll(completedOuter, next)`, so the next
  stage IS tracked. The Motion and Rumble Continue buttons that "wrap the same action in Run"
  (`WizardWindow.Motion.cs:160-164`, `WizardWindow.Rumble.cs:89-93`) are the broken ones: see LABUI-V-001 (high).
  The remaining part of the finding (flags plus `WhenAll` emulating a loop) stands. Severity raised to high through
  V-001.
- **LABUI-014** (export caps). The caps are real (`LabExport.cs:46-53,88-93,114-126`), but the recommendation is
  wrong on three counts: (1) the subtree AGENTS.md states "The shared report is built in memory, previewed, and
  written once" and the class remarks say the same (`LabExport.cs:37-43`), so a staging zip in the project changes a
  documented rule; (2) it adds mechanism (staging file lifecycle, cleanup on dispose and close, an open question) where
  removing the per-file exclusion and the count/total refusals is enough; (3) "derive the reader bounds
  independently ... at least what any export can produce" is unsatisfiable once the export is unbounded.
  Corrected recommendation: delete `MaximumFileBytes`/`MaximumFiles`/`MaximumTotalBytes` from `LabExport`, keep the
  in-memory build; give `LabReviewArchive` its own entry and total constants as an untrusted-archive guard (today it
  borrows `LabExport.MaximumTotalBytes` at `LabReviewArchive.cs:39` and `LabReport.MaximumEntryBytes` at `:184`, so
  both constants must move in the same change to stay green).
- **LABUI-016** (CLI/GUI parity). The capture-preview part is wrong: the GUI display already embeds
  `privacyPreview = CapturePrivacyPreview.Create(prepared.ExportPlan.Bundle)` (`MainWindow.cs:283-294`), the same
  object the CLI prints (`DeviceLabCli.cs:249-253`), plus paths and prompts. The missing `report` action and the
  missing `--usb-instance` on the lab-report scaffold (`MainWindow.cs:440-448` vs `DeviceLabCli.cs:394-408`) are real.
  Adding controls is a UI change; see Batch problems.
- **LABUI-023** (`WriteEvidenceOnce` hides IO errors). Stronger than stated: no collision case exists. Every
  `RunPowerAsync` begins a new attempt (`Power.cs:58`), `identity-baseline` is written once per attempt (`:77`), and
  every other name carries a distinct `passLabel` (`ac-or-battery`, then `ac` or `battery`). The catch guards nothing.
  Recommendation: call `project.WriteEvidence` and delete `WriteEvidenceOnce` and `WriteContext`. Inside the checkpoint
  callbacks (`Power.cs:376,720`) a throw then fails the checkpoint, so nothing is written: fail-closed.
- **LABUI-002** (TDP never confirmed). Confirmed, with two additions. (1) After switching review to
  `LabPowerSummary.IsPass`, `processor-power` runs over `ryzen-smu`/`kx` (`ProcessorPower.cs:177,313`) also pass,
  and because the record has no such mechanism they become `New` mechanism proposals that `PromotedByDefault`
  (`LabReview.cs`, `Promotable`/`PromotedByDefault`) writes into a curated device record. Decide whether a generic
  processor test belongs in a device record; if not, report it as Observed. (2) `readback-mismatch` must stay a
  non-failure (Unresolved, not Disagrees): the maintainer's no-readback rule forbids turning a missed readback into a
  contradiction.
- **LABUI-027** (start-up recovery, other domain). Worse than "a corrupt record is forgotten":
  `LabMachineState.Read` also maps a transient `IOException`/`UnauthorizedAccessException` to "nothing recorded"
  (`LabMachineState.cs:72-88`) and `Update` writes `change(Read())` back (`:94-100`), so one sharing violation during
  any update overwrites pending restores. Raise to medium; owned by the machine-state reviewer.
- **C13** (M01-42). Declining UAC is not enough for a hardware-free run. With `controller-mode.json` or
  `mode-commands.json` present, the non-elevated `Start` still calls `RecoverControllerInitAsync`
  (`WizardWindow.cs:174-180`), which runs `LabModeCommands.RecoverPending()` in-process (HID writes) and, for a
  controller-mode record, starts the worker (`WizardWindow.Hardware.cs:105-139`). M01-42 must also require that the
  whole `%LOCALAPPDATA%\WSGM Device Lab\wizard` folder (machine-changes, controller-mode, mode-commands) is absent.
- **LABUI-013, LABUI-020**: content confirmed, line numbers wrong (LabReport `Read`/`ReadJson` are at 45-111;
  SystemDump `WaitAsync` is at :36 and the Continue-before-Finish at :56-57).

## Confirmed (ids only)

LABUI-001, LABUI-002 (with correction), LABUI-003, LABUI-004, LABUI-007, LABUI-008, LABUI-009, LABUI-010, LABUI-011,
LABUI-012, LABUI-013, LABUI-015, LABUI-017, LABUI-018, LABUI-019, LABUI-020, LABUI-021, LABUI-022, LABUI-024,
LABUI-025, LABUI-026, LABUI-027 (with correction), LABUI-029, LABUI-030, LABUI-031, LABUI-032, LABUI-033, LABUI-034;
plan claims C1, C2, C3, C5, C6, C7, C8, C9, C10, C11, C12, C14, C15, C16. C4 is plausible for the GUI side: the
worker queue is an unbounded `BlockingCollection` (`LabWorkerHost.cs:90`), but growth behind a stuck call is bounded
by the client call deadline, so no 64 cap is needed (worker reviewer owns it).

## Missed findings

### LABUI-V-001 (high, concurrency/lifecycle) A synchronous Continue loses the next stage's operation
`WizardWindow.cs:887-911` with `WizardWindow.Motion.cs:160-164` and `WizardWindow.Rumble.cs:89-93`.
`Run(page, () => { Next(x); return Task.CompletedTask; })` calls `RunCore`, which runs `work()` synchronously; `Next`
starts the next stage chained, so the inner `Run` sets `_operation = WhenAll(oldCompleted, nextStage)`. Control then
returns to the outer `Run`, which executes `_operation = operation;` with its own already-completed `RunCore` task,
overwriting the tracked stage. Motion's Continue is the normal path, so in every wizard run the Rumble stage (and,
after a failed rumble, the Power stage) runs while `_operation.IsCompleted` is true:
- Stop and Escape do nothing (`StopStage`, `:1044-1050`);
- the outer `RunCore` finally re-enables the stage list (`:962`), and `SelectionChanged` calls `ShowStage` without
  cancelling (`:111-114`), detaching the running stage's page; `Start` on another hardware stage passes the guard and
  runs concurrently with open rumble routes or recorded power changes;
- closing awaits a completed `_operation` (`:1019`) and disposes capture, worker and the owner reservation while the
  stage's finally is still zeroing routes or restoring power. Safety then rests on the worker's exit zero and the
  next-start recovery, which contradicts the subtree rule "Closing waits for the running operation, then undoes ...
  and releases the owner reservation".
A second path: Power's "Continue anyway" (`Power.cs:165`) calls `Next` directly, so while "Try restoring again"
(`Power.cs:1213-1221`, a non-chained `Run`) is running, the Sleep stage starts beside it.
Recommendation (removes mechanism): until LABUI-B5 lands, the Motion and Rumble Continue buttons call `Next` directly
like Power, and every result-page Continue checks `_operation.IsCompleted` before `Next`. LABUI-B5's single stage loop
then deletes `chained`, `WhenAll` and the flags. Test: after a stage completes and its Continue is pressed,
`_operation` is not completed until the next stage ends; close during that stage waits for its finally.

### LABUI-V-002 (medium, bug) MSI fan test can never confirm the curated fan mechanism and promotes a bogus one
`WizardWindow.Power.cs:806-812` writes the Claw fan test as feature `fan-full-speed`, transport `wmi-method`. The
curated Claw record declares `fan` over `wmi-method` (`Knowledge/Devices/wsgm.claw-8-a2vm.json:168-169`). Review
matches features exactly and maps only `fan` to `FanControl` (`LabReview.Power.cs:14-20,72-89`). Result on every
Claw report: the record's fan mechanism is "Not tested in this report" (Unresolved), `FanControl` is never
confirmed, and a passed run becomes a `New` mechanism `fan-full-speed:wmi-method` whose proposal is
`PromotedByDefault`, so `promote` without `--field` writes a non-standard mechanism into the curated record (the
parser does not restrict feature names, `Knowledge/DeviceKnowledge.cs:271`). Same drift class as LABUI-002/003.
Recommendation: the stage writes `Feature = "fan"` (the display name can stay "100% fan speed" via the detail),
fixed in the same batch as LABUI-002/003, with a review test built from the stage's own result type.

### LABUI-V-003 (low, evidence) Cancelled capture steps stay open; a stopped baseline can teach input as noise
`WizardWindow.Buttons.cs:83-85` (baseline countdown), `WizardWindow.Sleep.cs:118-132` and `:246-255`. On Stop the
stage throws before `capture.EndStep()`. `LabInputCapture` keeps the step (and, for the baseline, `_baseline = true`)
until the next `BeginStep` (`Capture/Live/LabInputCapture.cs:269-312`); a non-baseline `BeginStep` does not clear
`_noise`. A tester who presses Stop during "Hands off..." and then handles the device before running Motion or Sleep
leaves real input in the noise map used for attribution. Recommendation: end the step in the cancel path (as
`RunControlAsync` already does at `Buttons.cs:370-375`); no new state.

### LABUI-V-004 (low, duplication) The capture preview recomputes the bundle writer's lane bytes and hashes
`Capture/CapturePrivacyPreview.cs:94-128` serializes each lane with `DeviceLabCompactJson` plus a newline and hashes
it; `Capture/CaptureBundle.cs:737-820` does the same when writing. The subtree rule requires preview/count/hash
consistency, and two copies must stay byte-identical by hand. Recommendation: one lane serializer used by both. The
global 64-sample and 128-byte blob-prefix caps are display-only (every lane is listed with count, length and hash):
accepted no-change, same as LABUI-034. labui.md listed this file in scope but recorded nothing on it.

### LABUI-V-005 (nit, lifecycle) Every elevated wizard start launches the hardware worker
`WizardWindow.cs:158` evaluates `await WorkerAsync()` before asking whether anything is recorded, so the elevated
worker process starts on every elevated open even with an empty record. Recommendation: start it only when
`_machine.Read()` holds power, rumble or curated-init state, which also makes the M01-42 elevated run lighter.

## Batch problems

1. **LABUI-B1** drops behaviour: deleting the rumble `report` read (refuted LABUI-005) removes the HID report layout
   from promoted rumble mechanisms.
2. **LABUI-B1** is over-scoped for a bug fix. The defects (LABUI-002, -003, V-002) are fixed in about 20 lines:
   review uses `LabPowerSummary.IsPass`, treats `seen == "matched"` as a pass exactly as `LabPowerSummary.Lighting`
   already does (`LabPowerResults.cs:128-132`), and the MSI fan test writes `fan`. The LABUI-003 recommendation adds
   new lighting fields plus a legacy-shape fallback: mechanism with no defect behind it. Land the fix first; the
   typed-evidence refactor (LABUI-004) is optional and must keep wire names without adding fields.
3. **LABUI-B1/LABUI-002 side effect**: the fix makes `processor-power` runs promotable by default as `New` device
   mechanisms. The batch must state the intended verdict, and keep `readback-mismatch` non-failing.
4. **Dependencies are wrong**: "B1, B2, B3, B4 are independent" does not hold. B2 moves the `LabReview*` files B1
   edits; B3 edits `Reports/LabReviewArchive.cs`, which exists only after B2; B4 edits `MainWindow.cs` usings that B2
   changes. Order: B1, B2, B3; B4 after B2.
5. **LABUI-B2/B3 build break**: `LabReviewArchive` consumes `LabExport.MaximumTotalBytes` and
   `LabReport.MaximumEntryBytes`; B2 deletes `MaximumEntryBytes` and B3 deletes `MaximumTotalBytes`, so each batch must
   relocate the constant it deletes into `LabReviewArchive` to stay green.
6. **LABUI-B3** contradicts the subtree AGENTS rule (in-memory, previewed, written once) and adds a staging-file
   lifecycle; replace with deleting the caps (corrected LABUI-014). Drop the `BeginAttempt` skip (refuted LABUI-028).
7. **LABUI-B4** adds a report button and a USB instance field to the developer tabs. requirements.md keeps UI
   appearance and workflows identical; this needs the maintainer's explicit approval or must be recorded as an
   accepted parity gap. The "shared capture preview" step is unnecessary (both front ends already show
   `CapturePrivacyPreview`).
8. **LABUI-B5** comes too late for V-001: B5 waits on two other domains' interfaces, while V-001 breaks the
   one-operation and close-order rules in the normal path today. Schedule the small V-001 fix as its own first batch.
   B5's loop must also keep the post-stage result pages (Power, Motion, failed Rumble: "Run again / Continue")
   outside the running operation; if the stage awaits Continue inside the loop, Stop, Escape and stage-list clicks on
   those pages start behaving differently (workflow change).
9. **LABUI-B9**: replace `WriteEvidenceOnce` with `WriteEvidence` (corrected LABUI-023) instead of narrowing the
   catch.
10. **LABUI-B10** under-states its cost: moving Aura zones and the Dynamic Lighting hint into `LabPowerPlan` from the
    curated record needs new record members, which the strict parser rejects until `DeviceKnowledge.cs` is extended
    and the schema version bumped (subtree AGENTS rule), plus curated JSON edits. Gating the Claw RGB test by the
    record's `lighting`/`hid-output` mechanism (vendor 0DB0) needs no schema change; the Aura zone names can stay as
    transport constants next to `LabAuraLighting`, which is not UI.
