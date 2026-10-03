# Device Lab part 1: GUI, wizard, stages, view models, export/preview

Reviewer: Claude (LABUI). Baseline: master `1329813f`, read-only. Nothing was built, run or mutated.

## Scope split

Device Lab has 65 k lines. This review owns the presentation and evidence-authoring half:

- `src/WSGM.DeviceLab/Gui/**` (16 files, 6,958 lines): `App`, `DeviceLabGui`, `DeviceLabGuiOperationState`,
  `MainWindow`, `WizardHelp`, `WizardWindow` and its 10 partials (`Buttons`, `ClawLighting`, `Hardware`,
  `ModeCommands`, `Motion`, `Power`, `ProcessorPower`, `Rumble`, `Sleep`, `SystemDump`; 5,578 lines together).
- Wizard project/stage/export/report files: `LabStages`, `LabProject`, `LabExport`, `LabReport`, `LabReview`,
  `LabReview.{Buttons,Motion,Power,Rumble}`, `LabReviewArchive`, `LabPromote`, `LabButtonPlan`, `LabMotionSteps`,
  `LabIdentity`, `LabPowerResults`, `WizardElevation`.
- `Capture/CapturePrivacyPreview.cs` (the capture export preview the GUI and CLI show).
- Tests: `Gui/DeviceLabGuiOperationStateTests`, `Wizard/LabExportTests`, `LabProjectTests`, `LabReviewTests`,
  `LabButtonsTests`.

The other half (worker, CLI, capture/live, transports, machine state, power/mode/PawnIO/HidHide/system dump/analysis
files in `Wizard/`) belongs to the worker/CLI/capture/machine-state reviewer. Where a contract crosses, it is named.

The Claude ledger has no Device Lab GUI/wizard entries (Device Lab units U17A-U19B were never started; only
A02-F011, A02-F012 and A02-F022 touch Device Lab, all in the other half). Every finding below is NEW unless a
plan line or ledger ID is cited. No admitted batch (W02_01, W02_02, T01_01, A02_01..04) touches this half;
A02_03 (`LabWorkerSession` zero retry) is compatible with the GUI: `WatchWorkerStreamAsync`
(`WizardWindow.Rumble.cs:385-408`) already polls sticky `StreamError` and needs no change.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "Device Lab: Same CLI/GUI application services; explicit project/session/worker owners; no runtime policy dependency" (refactor-plan.md:72) | partially | Both front ends call the same code, but the `.wsgmlab` trio bypasses `Application/` in both (`MainWindow.cs:425,438,446`, `DeviceLabCli.cs:295,306,328,401`). GUI has no `report` action and cannot pass `--usb-instance` to a lab-report scaffold (`MainWindow.cs:440-448` vs `DeviceLabCli.cs:399-405`). There is no session owner: project, owner reservation, worker, capture, lifetime and operation are window fields (`WizardWindow.cs:43-71`, `WizardWindow.Hardware.cs:21-22`). The csproj links four GPL WSGM interop files (A02-F022). | State the gaps as work items (LABUI-016, LABUI-001/006). "No runtime policy dependency" holds for policy; the linked interop is a licence/ownership item owned by A02-F022. |
| C2 | "Device Lab reuses CLI/GUI application workflows" (refactor-plan.md:155) | partially | As C1. | Same. |
| C3 | "decomposes WizardWindow into stage controllers owning attempts/drafts and a window renderer" (refactor-plan.md:155; pre-AM01 B02 step 1) | partially accurate, "drafts" inaccurate | The window is one class in 11 partial files, 5,578 lines. It calls `project.BeginAttempt` in every stage and owns machine-change sessions (`RumbleSession` and `WorkerRumbleOutput` nested in the window, `WizardWindow.Rumble.cs:729-937`; checkpoint/record/restore/release in `WizardWindow.Power.cs:335-976`, `ProcessorPower.cs:23-359`, `ClawLighting.cs:13-81`, `Hardware.cs:77-139`). There are no drafts; the stage state is in-memory test sessions and window fields (`_pinnedAcLine`, `_powerRecord`, `_powerSensors`, `Power.cs:37-43`). | "Stage controllers own attempts, machine-change sessions, evidence writing and restore; the window renders through a narrow UI port." The plan's own rule "Extraction is by state and responsibility, not more partial files" (refactor-plan.md:75) applies directly: the partials are the anti-pattern. |
| C4 | Worker "bounded worker queue of 64 pending requests with explicit Busy response" (refactor-plan.md:155; pre-AM01 B02 step 2) | over-engineered for this client | The only worker client is the wizard, which runs one operation at a time (`WizardWindow.cs:887-911`). Concurrent requests are at most: the running call, the rumble page's `stream-status` poll every 250 ms (`Rumble.cs:385-408`), and a per-sample fan read during the load burst (`Power.cs:199-229`, `LabPowerLoad.RunAsync`). No defect needs a 64 cap or a Busy outcome, and Busy would be a new failure path every stage must render. | Remove the 64 cap and Busy from the plan (no-arbitrary-limits, simplify). Keep "reader thread plus one dispatch lane, cancel readable while work runs". Owned by the worker reviewer; recorded here because the GUI would have to handle Busy. |
| C5 | "`LabMachineState` retains record-before-write/readback-before-clear" (refactor-plan.md:155) | partially | Power transports record in the checkpoint callback before the worker admits a write and clear only after a verified restore (`Power.cs:368-378,583-586,669-677`, `ProcessorPower.cs:126-142`). Controller-mode and mode-command records live in side files beside it, read through static paths (`LabControllerInit.cs:67-73`, `LabModeCommands.cs:345,746`), contradicting the subtree AGENTS "`LabMachineState` is the one record". Aura is cleared without readback by design (`Power.cs:1106-1116,1177`), which matches the maintainer's no-readback rule. | Keep the rule for lab power evidence (documented subtree exception: the Lab's purpose is verification). Add: one recovery owner for all three records (LABUI-027, other domain). |
| C6 | "owner mutex stays on its dedicated owning thread" (refactor-plan.md:155; pre-AM01 B02 step 3) | inaccurate for the wizard | The wizard's `Global\WSGM.DeviceOwner` reservation is an unowned handle (`WindowsPreflightInspection.cs:16-34`), reserved on a pool thread (`WizardWindow.cs:484`) and disposed on the UI thread (`WizardWindow.cs:1039`); no thread affinity exists. `LabMachineState` uses an in-process lock, not a mutex (`LabMachineState.cs:72-98`). | Reword: "the owner reservation handle is held until the window's ordered cleanup has finished; unverified plugin cleanup retains it for the process lifetime." Drop the thread clause for the wizard. |
| C7 | "timeout retains uncertain ownership and never retries" (refactor-plan.md:155) | accurate | Restore retry is an explicit button only (`Power.cs:1207-1222`); the rumble page never resends (`Rumble.cs:595-645`). | None. |
| C8 | "Existing safety allowlists, WMI quarantine, no EC access, curated facts and export preview/approval remain unchanged" (refactor-plan.md:155) | partially | Export preview equals what is written (`LabExport.cs:71-200`, shown before save at `WizardWindow.cs:795-835`). Curated facts leak into UI code as literals: Claw record id (`ClawLighting.cs:16`), `"xbox"` substring (`Power.cs:1092`), Aura zone names (`Power.cs:1124-1128`). | Add: curated facts used by stages come from the record/plan, never from UI literals (LABUI-009). |
| C9 | "Capture buffering/redaction/publication/scaffold validation are independently testable with retained handles and temp roots" (refactor-plan.md:155) | accurate for export | `LabExportTests` uses temp roots and the real redactor. | None; extend to typed evidence (LABUI-004). |
| C10 | "UI snapshots are detached values with revisions. A stale completion checks both operation generation and current view-model/owner identity" (refactor-plan.md:115) | not applicable to Device Lab | Both windows already serialize to one operation (`MainWindow.cs:654-711`, `WizardWindow.cs:887-911`). | Exclude Device Lab from revisions/generations explicitly; the one-operation gate is the whole mechanism. |
| C11 | "UI owners ... no global service lookup, store/native acquisition in views" (refactor-plan.md:71) | target accurate, current state violates | `LabMachineState.ForCurrentUser` (`WizardWindow.cs:45`), `LabPawnIo.ForMachine` (`:76`), `LabInputCapture.Start(hwnd)` (`Hardware.cs:53`), `LabWorkerClient.Start` (`Hardware.cs:69`), `DeviceKnowledgeBase.Default` (`Hardware.cs:147`, `WizardWindow.cs:699`), static `DeviceLabGui.Wizard` (`DeviceLabGui.cs:32`, read in `App.cs:19`), `new DeviceLabApplication` in the window (`MainWindow.cs:55-57`). | Applies to Device Lab via LABUI-B4/B5. |
| C12 | Shutdown phase budgets B3 (planning-corrections.md B3; refactor-plan.md:87-93) | not applicable | Device Lab close is "cancel, wait for the operation, undo HidHide, dispose capture, worker, owner" (`WizardWindow.cs:1014-1042`), bounded by the worker call deadline. | State that B3 does not apply to Device Lab; keep today's close order (subtree AGENTS rule). |
| C13 | M01-42 "wizard navigation/cancel/close ... no plugin load/hardware action/probe" (manual-acceptance-matrix.md:52) | partially | Opening the wizard elevated runs PawnIO reconcile and recorded power/rumble/controller restores before the welcome page (`WizardWindow.cs:149-180`); "Get ready" can install PawnIO and add a HidHide entry (`WizardWindow.cs:450-586`). | M01-42 must run the wizard with the UAC prompt declined (or with an empty `%LOCALAPPDATA%\WSGM Device Lab\wizard` record and Preflight skipped). Elevated stage flows belong to separately authorized hardware rows. |
| C14 | Pre-AM01 B02 acceptance "Wizard cleanup/worker crash/repeated close proven without machine mutation" | accurate goal, unreachable as specified | No seams exist: worker, capture, machine state and project are concrete/static. | Add the ports in section 4 and the session tests in LABUI-B5. |
| C15 | audit-coverage.md U18A groups LabExport/LabProject/LabPromote/LabReport/LabStages with machine-changing wizard core | partially | The `.wsgmlab` readers and promote are offline developer tools used by CLI and Scaffolding. | Move them to `Reports/` (LABUI-017). |
| C16 | audit-coverage.md U18C "Device Lab GUI (17 files)" | accurate | 16 source files plus one test. | None. |

## 2. Findings

Severity scale: critical / high / medium / low / nit.

### LABUI-001 (high, architecture) Hardware orchestration lives in the window
`WizardWindow.Power.cs:335-976`, `ProcessorPower.cs:23-359`, `ClawLighting.cs:13-81`, `Rumble.cs:156-937`,
`Hardware.cs:77-139`, `ModeCommands.cs:16-123`. Worker sessions, checkpoints, `LabPowerRecovery.Record`, readback
sampling, restore ordering, `_machine.Update` clears, rumble route sessions and controller-init recovery are
private members of an Avalonia `Window`. The subtree AGENTS says "Keep hardware policy out of UI code"; none of
these safety paths has a test, because nothing can run them without a window, a worker process and hardware.
Partly covered by refactor-plan.md:155 (stage controllers); the concrete owners are NEW.
Recommendation: stage controllers in `Wizard/Stages/` that take an `IWizardUi` port and a `WizardStageContext`
(section 4). The window keeps only rendering.

### LABUI-002 (medium, bug) Review never confirms a real TDP or processor-power test
`LabReview.Power.cs:80-86` counts a run as passed only when `outcome == "passed"`. The wizard writes
`"applied-readback-matched"` for every TDP and processor test (`WizardWindow.Power.cs:455,847`,
`ProcessorPower.cs:177,313`). `LabPowerSummary.IsPass` (`LabPowerResults.cs:415-418`) already knows both strings.
Real reports therefore review TDP as Unresolved and `promote` never confirms the mechanism. The test fixture
writes `Outcome = "passed"` for TDP (`LabReviewTests.cs:258-261`), so the suite passes against a schema the wizard
never produces. NEW.
Recommendation: use `LabPowerSummary.IsPass` in review; build the fixture from the same writer type (LABUI-004).

### LABUI-003 (medium, bug) Aura and Claw lighting always review as failed
The review splits lighting answers into lamp-array `(shown colour, seen colour)` and everything else as
`hid-output` and passes only when `Shown == Seen` (`LabReview.Power.cs:63-71,97-105`). Aura writes
`("red:both rings", "matched")` (`WizardWindow.Power.cs:1153`), Claw writes `("claw:red", "matched")`
(`ClawLighting.cs:50-51`). They never compare equal, so the curated `lighting/hid-output` mechanism of the Claw
and ROG Ally X (`wsgm.claw-8-a2vm.json:207`, `wsgm.rog-ally-x.json:189`) reviews as Disagrees, and a
`DynamicLighting` claim as well. The wizard's own stage summary (`LabPowerSummary.Lighting`,
`LabPowerResults.cs:393-410`) accepts `"matched"`, so the tester and the developer see opposite verdicts. NEW.
Recommendation: typed lighting evidence `{ Transport, Shown, Colour, Seen, Matched }` written by the stage; review
reads `Matched`. Old reports keep their wire names and are parsed by the existing fallback.

### LABUI-004 (medium, structure/test quality) Evidence schema is anonymous objects split across writer and reader
Writers: `WizardWindow.Buttons.cs:86-95,208-220,444-454`, `Power.cs:150-153`, `Rumble.cs:700-722`,
`Sleep.cs:144,203-218`, `ModeCommands.cs:72-77,107-113`, `WizardWindow.cs:529,773`. Readers parse camelCase names
by hand (`LabReview*.cs`, `LabReport.cs:313-339`). Tests hand-copy the shape (`LabReviewTests.cs:211-272`).
LABUI-002, -003 and -005 are drift that this structure guarantees. Motion is the exception: it already uses
typed `LabMotionSummary` and reviews correctly. NEW.
Recommendation: `Wizard/Evidence/` records for each file the review reads, keeping today's property names (old
reports stay readable). Stages and test fixtures both construct those records.

### LABUI-005 (low, dead code) Review reads a rumble `report` field nobody writes
`LabReview.Rumble.cs:244,368-371` reads `route["report"]`; `LabRumbleRoute` has no such member
(`LabRumbleRoutes.cs:19-31`, layout and target are `[JsonIgnore]`). A promoted new rumble mechanism never carries
a report parameter. NEW. Recommendation: delete the read and the `Report` tuple element; do not invent evidence.

### LABUI-006 (medium, concurrency/lifecycle) Operation chaining by flags and `Task.WhenAll`
`WizardWindow.cs:313-327,395-448,887-964` run a stage, and when it calls `Next` the next stage starts "chained"
inside the still-running operation (`_operation = Task.WhenAll(outer, operation)`), skipping the one-operation
guard. Navigation adds `_navigating`, `_requestedStage`, `_refreshing`, and close adds `_closing`,
`_confirmingClose`, `_closeReady`. The power page's Continue calls `Next` outside `Run` (`Power.cs:165`), so it
uses the chained path from a click handler, while motion and rumble wrap the same action in `Run`
(`Motion.cs:160-164`, `Rumble.cs:89-93`). The error page uses the field `_running` rather than the captured
stage (`WizardWindow.cs:942-948`). NEW.
Recommendation (removes mechanism): one `WizardSession` loop: `while (next is not null) { next = await
RunStageAsync(next, ct); }`. Stage controllers return the next stage id; navigation sets the requested id and
cancels the stage token; the loop shows it after the cancelled stage's finally blocks have run. No chaining
flag, no `WhenAll`.

### LABUI-007 (medium, bug) Power stage reports Completed after an error
`WizardWindow.Power.cs:132-136` catches a stage error, shows it and logs it, then line 157-160 finishes the
segment Completed. Rumble finishes Failed on the same condition (`Rumble.cs:61-66`). The report claims the
power stage completed. NEW. Recommendation: Failed when the stage caught an error or any restore is unverified.

### LABUI-008 (medium, bug) Developer GUI discards a finished result when Cancel came late
`MainWindow.cs:679-681`: after the operation returned, `current.Token.ThrowIfCancellationRequested()` turns the
result into "Operation cancelled". For "Run attended hardware action" the result carries the restore/cleanup
verification; a Cancel pressed while it finished hides whether cleanup was verified. NEW.
Recommendation: delete line 680; operations throw their own `OperationCanceledException`.

### LABUI-009 (low, policy in UI) Curated facts and device heuristics as UI literals
`ClawLighting.cs:16` (`"wsgm.claw-8-a2vm"`), `Power.cs:1092` (`Contains("xbox")`), `Power.cs:1124-1128` (Aura zone
names), `Buttons.cs:326-340` (`"mouse"` and `" 000D:"` display filters, duplicated in
`LabReview.Buttons.cs:307-309` and `LabInputAnalysis.WithoutPointer`), `ProcessorPower.cs:377-390` (CPUID vendor).
NEW. Recommendation: the lighting kind/zones and the Dynamic-Lighting hint come from `LabPowerPlan` (curated
record), the pointer filter is one `LabInputAnalysis.IsPointer` used by both, CPUID moves next to the processor
power tests (not UI).

### LABUI-010 (medium, ownership) Process globals feed the windows
`DeviceLabGui.Wizard` is a mutable static the `App` reads (`DeviceLabGui.cs:32,41`, `App.cs:19`);
`LabMachineState.ForCurrentUser` (`WizardWindow.cs:45`); `LabControllerInit.HasPending`/`HasControllerModePending`
and `LabModeCommands.HasPending` are static `File.Exists` checks on fixed paths (`WizardWindow.cs:174`,
`Hardware.cs:107-108`); `DeviceKnowledgeBase.Default` (`Hardware.cs:147`, `WizardWindow.cs:699`). Covered in
principle by refactor-plan.md:71 (C11); concrete items NEW.
Recommendation: `AppBuilder.Configure(() => new App(options))`; `WizardSession` receives machine state, knowledge
base and recovery owner by constructor; Program composes production instances.

### LABUI-011 (low) Blocking file and system calls on the UI thread
`_machine.Read()`/`Update` and `File.Exists` run on the UI thread in `WizardWindow.cs:165,174,972`,
`Power.cs:52,162,264,269,301,771,797,1195`, `ProcessorPower.cs:26,31`, `ClawLighting.cs:65`,
`Hardware.cs:107-110`; `LabPowerTelemetry.AcLine()` at `Power.cs:306,324`; full result JSON serialization at
`MainWindow.cs:681`. The subtree AGENTS requires blocking work off the UI thread. NEW.
Recommendation: falls out of LABUI-001: controllers run on the session's worker context; the window only renders.

### LABUI-012 (nit) Null-forgiving `changes.Power!` in UI restore paths
`Power.cs:771,797`, `ClawLighting.cs:65`. Safe only because a checkpoint recorded `Power` first; in the Claw
`finally` an exception there would be caught as "RGB restoration failed" after a successful restore and the
checkpoint would stay unreleased. NEW. Recommendation: use the `changes.Power is null ? null : ...` form the other
clears already use (`Power.cs:585,675,887,971`), inside the controller.

### LABUI-013 (low, duplication/safety) Two readers of the same `.wsgmlab`
`LabReport.cs:296-371` opens the archive with `ZipFile.OpenRead`, trusts the declared entry length, has no total
read bound, does not validate segment ids and throws `InvalidOperationException` from `GetValue<int>` on a
malformed `attempts`. `LabReviewArchive.cs:464-654` is the hardened twin (counted reads, total bound, id
validation, typed leaf readers). NEW. Recommendation: reimplement `LabReport.Read` on `LabReviewArchive` and delete
its private `ReadJson`.

### LABUI-014 (medium, no-arbitrary-limits) Export drops or refuses evidence by size and count
`LabExport.cs:47-53,88-93,114-126`: a file over 32 MiB is excluded from the report, more than 4,096 files or more
than 256 MiB refuses the whole export, and the report is buffered in memory (`_files`, line 57), which is the only
reason for the bounds. NEW. Recommendation: Prepare streams redacted entries into one staging zip (hidden name
inside the project, already excluded by the dot rule at `LabExport.cs:82-86`), hashing as it writes; the preview
is built from that pass, Save copies that exact file to the chosen target through `DurableFile` and the
create-new move. No size or count caps. The review-side bounds in `LabReviewArchive` stay: they protect the
reader against untrusted archives (a real IO safety boundary) and must be at least what any export can produce,
so derive them independently rather than from `LabExport.MaximumTotalBytes` (`LabReviewArchive.cs:478`).

### LABUI-015 (low, no-arbitrary-limits) Small UI caps that truncate or drop
Notes `MaxLength = 2000` (`WizardWindow.cs:52`); at most 12 extra buttons (`Buttons.cs:141`); failure messages cut
at 1,024 characters (`MainWindow.cs:720-727`); recent paths limited to 32 entries, 128-char keys, 4,096-char
values and a 64 KiB file with a longest-first eviction loop (`MainWindow.cs:26-28,983-1064`) although only 16
compile-time keys exist; `Compact` truncates readback JSON to 120 characters in review details
(`LabReview.Power.cs:272-276`), and that detail is copied into the promoted record's provenance note
(`LabPromote.cs:352`). NEW. Recommendation: remove each cap; recent paths become a dictionary of the known keys
with type checks only; provenance notes carry the outcome and detail without the truncated JSON.

### LABUI-016 (low, CLI/GUI parity) Developer GUI differs from the CLI
No GUI action for `report` (`LabReport.Read`); the lab-report scaffold button cannot pass `--usb-instance`
(`MainWindow.cs:440-448` vs `DeviceLabCli.cs:399-405`); capture preparation shows a GUI-only anonymous projection
(`MainWindow.cs:283-294`) while the CLI prints `CapturePrivacyPreview` (`DeviceLabCli.cs:249-253`). NEW.
Recommendation: add the report button and USB instance field to the existing Lab report tab (same layout
helpers, no redesign); show the same `CapturePrivacyPreview` plus paths in both. Do not add facade methods on
`DeviceLabApplication` for the report trio; both front ends already call one implementation.

### LABUI-017 (low, structure) Offline report tooling sits in the wizard folder
`LabReport`, `LabReview*`, `LabReviewArchive`, `LabPromote` are developer tools used by the CLI, the developer
tabs and `ScaffoldFromLabProjectWorkflow`, but live in `Wizard/` with the machine-changing tester code. NEW.
Recommendation: move to `Reports/` (namespace `WSGM.DeviceLab.Reports`), no behavior change.

### LABUI-018 (low, lifecycle) Notes bypass the one-operation rule
`WizardWindow.cs:1053-1070` saves a note with fire-and-forget `Task.Run`, clears the text box on completion even
if the tester kept typing, and close does not wait for it. NEW. Recommendation: `WizardSession.SaveNoteAsync`
awaited by the button; clear only the saved text; close awaits pending note writes.

### LABUI-019 (low) Two cancellation tokens with swapped-looking names
`Lifetime` is the stage token (`Hardware.cs:28`), `_lifetime` the window token (`WizardWindow.cs:43`). Identity
inventory and the PawnIO install/replace/remove use the window token (`WizardWindow.cs:651,662,699,846`), so
Stop and Escape cannot stop them. NEW. Recommendation: `WizardStageContext.Cancellation` (stage) and
`WizardSession.Closing` (window), each used deliberately; identity collection takes the stage token.

### LABUI-020 (low) System dump finishes only after Continue, and abandoned sections outlive close
`WizardWindow.SystemDump.cs:136-141` waits for Continue before `Finish`, so Stop at that prompt leaves a complete
dump marked not started. Line 118 abandons a slow section on close (`WaitAsync`), and it keeps writing into the
project after the owner reservation is released. NEW. Recommendation: finish before the prompt; record
abandoned sections in the session so close reports them (read-only, no wait).

### LABUI-021 (low) Wrong evidence text and stale comments
The rumble calibration `Method` says pulses run "at 10 to 500 ms" while the lengths start at 5 ms
(`Rumble.cs:36` vs `:712`); the comment at `Rumble.cs:27` describes pulse lengths above `MaxRumbleReplays`;
`Power.cs:762` calls the fan test "The TDP test". NEW. Recommendation: derive the method text from
`RumblePulseLengths`; fix the comments when the code moves.

### LABUI-022 (nit, duplication)
Four ask helpers with the same shape (`Hardware.cs:159-223`, `Motion.cs:322-344`, `Rumble.cs:649-689`, plus the
redo grid TCS in `Buttons.cs:243-280`); `LabButtonPlan.Belief` duplicates `LabReview.DescribeButton`
(`LabButtonPlan.cs:181-191` vs `LabReview.Buttons.cs:33-45`); `WriteContext` only forwards to `WriteEvidenceOnce`
(`Power.cs:1307-1310`); identical branches at `Power.cs:98-108`; `MainWindow` and `WizardWindow` each define
`Buttons`/`Heading`. Recommendation: one `AskAsync(labels, layout, stop, elsewhere)` in the UI port, one
`DescribeButton`, delete `WriteContext`, merge the branches.

### LABUI-023 (low) `WriteEvidenceOnce` hides every IO error
`Power.cs:1313-1323` treats any `IOException` as "already written by an earlier pass", so disk-full or access
errors on `identity-baseline` and `original-*` evidence vanish. NEW. Recommendation: name the two-pass case
explicitly (`{name}-{passLabel}` is already unique except `identity-baseline`); catch only the create-new
collision.

### LABUI-024 (nit) Leaked process handle, repeated help windows
`OpenInExplorer` drops the `Process` (`WizardWindow.cs:1080-1084`); every Help click opens another window
(`:1091-1108`). Recommendation: `using`, and activate an existing help window.

### LABUI-025 (low, test quality)
`DeviceLabGuiOperationStateTests` only assert record transitions (getter checks); the runner logic in `MainWindow`
(duplicate rejection, cancel, close-wait, LABUI-008) has no test. No wizard flow, close or recovery test exists.
`LabButtonsTests.cs:9-19` tests `LabInputCapture.NextId` (capture domain) inside the wizard plan tests.
`LabReviewTests` fixtures copy the evidence shape (LABUI-004). NEW. Recommendation: test the extracted runner and
session with fakes (LABUI-B4/B5), build review fixtures from evidence records, move the capture test to the
capture suite.

### LABUI-026 (low, cross-domain) Mode commands run in the wizard process, curated init in the worker
`ModeCommands.cs:38,60,102-103` call `LabModeCommands.Locate/Start/Stop` in-process; the curated init uses the
worker checkpoint (`Hardware.cs:77-93`). The subtree AGENTS says wizard hardware access runs in the worker. NEW.
Recommendation: owned by the machine-state reviewer; the GUI change is only to call whichever owner results.

### LABUI-027 (low, cross-domain) Start-up recovery is inline UI code over three records
`WizardWindow.cs:149-180` calls PawnIO reconcile, power recovery, rumble recovery and controller-init recovery in
sequence, each with its own store (`LabMachineState`, `controller-mode.json`, `mode-commands.json`).
`LabMachineState.Read` reads an unreadable file as "nothing recorded" (`LabMachineState.cs:70-90`), so a corrupt
record silently forgets pending restores. NEW. Recommendation: one `LabRecovery.RunAsync` owner in the
machine-state domain returning the notice lines; the wizard renders them. Corrupt state is reported, not erased
(matches refactor-plan.md:99 for WSGM's own recovery).

### LABUI-028 (low) `LabProject` details
`JsonOptions` is a public static mutable options object shared by all evidence and `LabMachineState`
(`LabProject.cs:189-197`); `BeginAttempt` reuses an `attempt-N` directory left by a crash between
`CreateDirectory` and `Save` (`:302-313`), after which `WriteEvidence` fails on "exists". NEW.
Recommendation: make the options a private readonly instance exposed read-only; `BeginAttempt` skips to the next
free attempt number when the directory already exists.

### LABUI-029 (nit) Magic answer indices
`Buttons.cs:385-459` (answers 0..4), `Motion.cs:229-241`, `Power.cs:1152-1158`, string device kinds in
`Liveness` (`Buttons.cs:492-514`). Recommendation: small enums per prompt when the code moves.

### LABUI-030 (nit) Export reads the manifest without the project lock and is not cancellable
`LabExport.cs:130-146` reads `project.Manifest` while a note may update it (LABUI-018); `Prepare`/`Write` take no
token though they run inside a cancellable stage. Recommendation: snapshot the manifest under the lock; pass the
stage token.

### LABUI-031 (nit, accepted) Rumble slider frames allocate per frame
`WorkerRumbleOutput.Write` goes through the worker's DispatchProxy every 50 ms (`Rumble.cs:879-885`,
`LabRumbleStream.cs:22`). Device Lab is not a product high-rate path; record as accepted no-change.

### LABUI-032 (low) Repository root found differently by the two windows
`MainWindow.cs:55-56` tries the current directory then the executable folder; `WizardWindow.Boundaries`
(`WizardWindow.cs:1110-1113`) only the current directory, so the output-path boundary differs by launch folder.
NEW. Recommendation: one `DeviceLabPathBoundaries` built in Program and passed to both.

### LABUI-033 (low) Viewing "Finish and share" changes the machine
Selecting the finish page removes the session's HidHide entry (`WizardWindow.cs:310-312,789`); going back to
Buttons afterwards runs without the allowance, silently. Documented in a code comment only. NEW.
Recommendation: keep behavior (subtree AGENTS: removed when the test finishes), but show the existing
"Get ready" hint on hardware stages when the entry was removed, using existing strings and layout helpers. If
that counts as a workflow change, leave as no-change and document.

### LABUI-034 (nit) Review summaries cap lines
`LabReport.cs:337` (5 candidates), `LabReview.Buttons.cs:283,300,355,368,372`, `LabReview.Rumble.cs:270`,
`LabReview.Power.cs:241`. Display summaries only; the archive keeps everything. Accepted no-change except where
the text is persisted (LABUI-015).

## 3. Plan refinements

Additions:

1. Typed stage evidence (`Wizard/Evidence/`) shared by stages, review and fixtures, wire names unchanged; fix
   LABUI-002/003/005 in the same batch. Old `.wsgmlab` files remain reviewable; no migration needed.
2. `WizardSession` as the single owner of project, owner reservation, lazily started worker and capture, stage
   loop, navigation, notes and ordered close. Close order stays exactly: cancel stage, await it, undo HidHide,
   dispose capture, dispose worker, release reservation.
3. Narrow `IWizardUi` port listing only the interactions the stages use (section 4), implemented by the window.
4. `GuiOperationRunner` (non-Avalonia) for the developer tabs, with LABUI-008 fixed.
5. `Reports/` folder for offline `.wsgmlab` tools; `LabReport` built on `LabReviewArchive`.
6. Streamed export (LABUI-014) and removal of UI caps (LABUI-015).
7. Explicit exclusions: Device Lab is outside B3 shutdown budgets and outside UI revisions/generations (C10, C12).
8. M01-42 wording (C13).

Changes:

- refactor-plan.md:155 "stage controllers owning attempts/drafts" becomes "stage controllers owning attempts,
  machine-change sessions, evidence and restore".
- refactor-plan.md:155 "owner mutex stays on its dedicated owning thread" becomes "the owner reservation handle is
  held until ordered cleanup finishes; unverified plugin cleanup retains it for the process lifetime" (C6).
- Keep "record-before-write / readback-before-clear" for lab power evidence and state it as the documented
  Device Lab exception to the product's no-readback-gating rule: the Lab exists to verify; write-only lighting is
  already cleared without readback.

Removals (over-engineering under the simplify and no-arbitrary-limits rules):

| Plan mechanism | Why it over-engineers | Simpler shape |
| --- | --- | --- |
| Worker queue bounded at 64 with Busy (refactor-plan.md:155) | No defect; the only client is serialized; Busy is a new failure mode for every stage | One reader thread, one dispatch lane, cancel readable while a call runs; no cap |
| UI snapshot revisions and generation checks applied to all UI owners (refactor-plan.md:115) | Device Lab already runs one operation at a time | One-operation gate in `WizardSession`/`GuiOperationRunner` |
| B3 phase budgets (planning-corrections.md B3) if read as covering Device Lab | Separate tool, no shared state with WSGM shutdown | Today's ordered wait, bounded by worker call deadlines |
| Operation chaining (`chained`, `WhenAll`, navigation flags) in today's code | Mechanism to emulate a loop | One sequential stage loop (LABUI-006) |
| Export size/count caps and in-memory buffer | Drop or refuse evidence | Streamed staging zip, no caps (LABUI-014) |
| Recent-path caps and eviction loop | 16 fixed keys | Known-key dictionary, type checks only |

## 4. Target design

### Owners

| Owner | File(s) | Responsibility |
| --- | --- | --- |
| `Program` | `Program.cs` | Builds `WizardOptions`, `DeviceLabPathBoundaries`, production `WizardServices`; starts the GUI with them |
| `App(WizardOptions?, WizardServices?)` | `Gui/App.cs` | Creates `WizardWindow(session)` or `MainWindow(application, boundaries)`; no statics |
| `WizardSession` | `Wizard/Session/WizardSession.cs` (new) | Project create/open, owner reservation, lazy `ILabWorkerClient` and capture, stage loop, navigation, stop, notes, `PowerRestorationPendingAsync`, ordered `CloseAsync` |
| `WizardServices` | `Wizard/Session/WizardServices.cs` (new) | Constructor bundle: `LabMachineState`, `LabRecovery` (other domain), `DeviceKnowledgeBase`, `LabPawnIo`, worker factory, capture factory, `DeviceLabPathBoundaries`, clock (`TimeProvider`) |
| `WizardStageContext` | `Wizard/Session/WizardStageContext.cs` (new) | Project, attempt, confirmed record, options (elevated), owner present, stage token, services |
| `IWizardStage` + 9 controllers | `Wizard/Stages/*.cs` (new) | `Preflight`, `Identity`, `SystemDump`, `Buttons`, `Motion`, `Rumble`, `Power`, `Sleep`, `Finish`; each returns the next stage id |
| `ModeCommandRun`, `ControllerInitRunner` | `Wizard/Stages/ModeCommandRun.cs`, `ControllerInitRunner.cs` (new) | Moved from window partials |
| `RumbleSession`, `WorkerRumbleOutput` | `Wizard/Stages/Rumble/*.cs` (new) | Moved from window; stream-status polling included |
| `PowerTestGuard`, `AsusPowerTests`, `MsiPowerTests`, `ProcessorPowerTests`, `LightingTests` | `Wizard/Stages/Power/*.cs` (new) | Moved from window partials; checkpoint/record/restore/release unchanged |
| Evidence records | `Wizard/Evidence/LabStageEvidence.cs` (new) | Every file the review reads, wire names unchanged |
| `IWizardUi` | `Wizard/Session/IWizardUi.cs` (new) | `ShowPage`, `Status`, `Warning`, `Muted`, `Heading`, `Line` (updatable), `AskAsync(labels, wrap, stop, elsewhere)`, `CountdownAsync`, `ConfirmIdentityAsync`, `ChooseRedoAsync`, `ManagerRows`, `RumbleSlidersAsync`, `RumblePulsesAsync`, `PickReportPathAsync`, `PickProjectFolderAsync`, `WindowHandle` |
| `WizardWindow` | `Gui/WizardWindow.cs` + `Gui/WizardControls.cs` + `Gui/RumbleViews.cs` | Renders and implements `IWizardUi`; stage list, notes box, close confirmation dialog, help, open folder |
| `GuiOperationRunner` | `Gui/GuiOperationRunner.cs` (new) | One-operation gate, cancel, close-wait, `DeviceLabGuiOperationState` |
| `RecentPaths` | `Gui/RecentPaths.cs` (new) | Known-key load/save |
| `Reports/*` | moved `LabReport`, `LabReview*`, `LabReviewArchive`, `LabPromote` | Offline `.wsgmlab` tools |

### Dissolved files: old symbol to new owner

`Gui/WizardWindow.cs` (stays, shrinks to rendering)

| Symbol | New owner |
| --- | --- |
| `WizardOptions` | `Wizard/Session/WizardOptions.cs` |
| `FinishId` | `LabStages.Finish` |
| `_lifetime`, `_stage`, `_operation`, `_running`, `_requestedStage`, `_navigating`, `_closing`, `Run`, `RunCore`, `StopStage`, `NavigateToStageAsync`, `ShowStage` (logic), `StartStage`, `Next` | `WizardSession` (loop, `Navigate`, `Stop`) |
| `_machine`, `_pawnIo`, `_owner`, `_project`, `CreateProjectAsync`, `OpenAsync`, `Load` | `WizardSession` + `WizardServices` |
| `Start` (recovery) | `LabRecovery.RunAsync` (other domain) called by `WizardSession.StartAsync` |
| `RunPreflightAsync`, `ManagersPanel` (logic), `PawnIoAsync`, `Outcome` | `Stages/PreflightStage` (manager rows rendered via `IWizardUi.ManagerRows`) |
| `RunIdentityAsync`, `Facts` | `Stages/IdentityStage`; facts grid in `WizardControls` |
| `ShowFinishAsync`, `SaveReportAsync`, `RestoreHidHide` | `Stages/FinishStage` |
| `ConfirmCloseAsync` | window dialog + `WizardSession.PowerRestorationPendingAsync` |
| `CloseAfterCleanupAsync` | `WizardSession.CloseAsync` |
| `SaveNote` | `WizardSession.SaveNoteAsync` |
| `Boundaries`, `ToolSha256`, `SourceRevision`, `ToolVersion` | `Wizard/Session/WizardBuildInfo.cs` (static pure) and injected boundaries |
| `_refreshing`, `RefreshStages`, `ShowWelcome`, `PickProjectAsync` (picker), `OpenProjectFolder`, `OpenInExplorer`, `Quote`, `ShowHelp`, `_confirmingClose`, `_closeReady`, `_note`, `_page`, `_projectLine`, `_stages` | stay in `WizardWindow` |
| `Page`, `PageTitle`, `Heading`, `Status`, `Warning`, `Muted`, `Action`, `Buttons` | `Gui/WizardControls.cs` |

`WizardWindow.Hardware.cs` (deleted)

| Symbol | New owner |
| --- | --- |
| `_capture`, `_worker`, `CaptureAsync`, `WorkerAsync` | `WizardSession` lazies |
| `Lifetime` | `WizardStageContext.Cancellation` |
| `RunHardware` | `WizardSession` admission (`IWizardStage.NeedsOwner`), same refusal strings |
| `SendCuratedInitAsync`, `CuratedModeAsync`, `RecoverControllerInitAsync` | `Stages/ControllerInitRunner` |
| `ConfirmedRecord` | `WizardStageContext.ConfirmedRecord` |
| `SkipStage` | `WizardSession.SkipAsync` |
| `AskAsync` (both), `CountdownAsync`, `OnUi` | `IWizardUi` implementation in `WizardWindow` |

`WizardWindow.Buttons.cs` (deleted): `RunButtonsAsync`, `ButtonSummaryAsync` (logic), `RunControlAsync`,
`WatchQuietAsync`, `Liveness`, `ButtonControlResult` go to `Stages/ButtonsStage`; the redo grid renders through
`IWizardUi.ChooseRedoAsync`; the pointer filter goes to `LabInputAnalysis.IsPointer` (other domain file, one
added method).

`WizardWindow.Motion.cs` (deleted): `RunMotionAsync`, `RunMotionStepAsync`, `RestOf`, `HoldAsync`,
`RecordMovementAsync`, `MotionPlural`, `MotionSentence`, `ShowMotionResult` (text) go to `Stages/MotionStage`;
`AskWrappedAsync` becomes `IWizardUi.AskAsync(..., wrap: true)`.

`WizardWindow.ModeCommands.cs` (deleted): `OfferModeCommandsAsync`, `EndModeCommandsAsync`, `NotSent`,
`ModeCommandRun` go to `Stages/ModeCommandRun`.

`WizardWindow.Rumble.cs` (deleted): constants, `RunRumbleAsync`, `RumbleFlowAsync`, `ProbeRouteAsync`,
`CalibrateRouteAsync`, `ConfirmSidesAsync`, `PulseAndAskAsync`, `Play`, `WriteRumbleEvidence`,
`RumbleStoppedException` go to `Stages/Rumble/RumbleStage`; `RumbleSession`, `WorkerRumbleOutput`,
`WatchWorkerStreamAsync` go to `Stages/Rumble/RumbleSession`; `SliderPageAsync`, `AddSliderRow`, `PulsePageAsync`,
`TryPulseAsync`, `RumbleAskAsync` become `Gui/RumbleViews` behind `IWizardUi.RumbleSlidersAsync/RumblePulsesAsync`
(the views call back into the session for frames and pulses; the XInput A/B answer pad stays in the view).

`WizardWindow.Power.cs` (deleted): `LoadBurst`, `LoadTest`, `SampleInterval`, `SeenColours`, `LouderAnswers`,
`RunPowerAsync`, `RunTelemetryAsync`, `RunLoadBurstAsync`, `ReadTelemetry`, `FanReader`,
`ShowEmbeddedControllerNote`, `RunDeviceTestsAsync`, `RunPowerSourceRepeatAsync`, `RestorePendingAsync`,
`SourceName`, `Failed`, `RestoreLine` go to `Stages/Power/PowerStage`; `_pinnedAcLine`, `_powerRecord`,
`PowerUnchangedAsync`, `ReadbackSamplesAsync`, `ForgetRecordedAsync`, `WriteEvidenceOnce` go to
`Stages/Power/PowerTestGuard`; `RunAsusTestsAsync`, `RunAsusPowerFamilyAsync`, `RunAsusTdpAsync`,
`RunAsusProfileAsync`, `RunAsusFanAsync`, `RestoreAsusPowerAsync`, `RunAsusChargeAsync`, `RestoreAsusChargeAsync`
go to `AsusPowerTests`; `RunMsiTestsAsync`, `RunMsiFansAsync`, `RunMsiTdpAsync`, `RestoreMsiPowerAsync`,
`RunMsiChargeAsync`, `RestoreMsiChargeAsync` go to `MsiPowerTests`; `RunLightingAsync`, `RunLampArrayAsync`,
`ShowLampColoursAsync`, `RunAuraAsync`, `AuraAnswer` go to `LightingTests`; `_powerSensors` goes to `PowerStage`;
`PowerRestorationPending` goes to `WizardSession`; `WriteContext` is deleted.

`WizardWindow.ProcessorPower.cs` (deleted): `RunProcessorPowerAsync`, `RunAmdPowerAsync`, `RunIntelPowerAsync`,
`CpuVendor` go to `ProcessorPowerTests`; `ReleaseQuietlyAsync` goes to `PowerTestGuard`.

`WizardWindow.ClawLighting.cs` (deleted): `RunClawLightingAsync` goes to `LightingTests` (gated by the plan's
lighting kind, not the record id literal).

`WizardWindow.Sleep.cs` (deleted): `RunSleepAsync`, `RunSleepCycleAsync`, `SleepPressAsync` go to
`Stages/SleepStage`.

`WizardWindow.SystemDump.cs` (deleted): `RunSystemDumpAsync` goes to `Stages/SystemDumpStage`.

`Gui/MainWindow.cs` (stays): `RunAsync`, `HandleClosing` (logic), `_operation`, `_operationFinished`,
`_closeAfterOperation`, `_displayState`, `ApplyDisplayState` (state part), `OperationFailureMessage` go to
`GuiOperationRunner`; `LoadRecentPaths`, `SaveRecentPaths`, `RecentPathsFile`, `MaximumRecent*` go to `RecentPaths`
(caps deleted); the tab builders, path pickers and dialogs stay.

`Gui/DeviceLabGui.cs`: static `Wizard` property deleted; `Run`/`RunWizard` take their options and services.

`Wizard/LabReport.cs`: moves to `Reports/`, private `ReadJson` and `MaximumEntryBytes` deleted, reads through
`LabReviewArchive` (the archive keeps its own per-entry bound constant).

### API changes and consumers

All types are `internal`; consumers are inside `WSGM.DeviceLab` and its test project.

| Change | Consumers to update |
| --- | --- |
| `App` constructor, `DeviceLabGui.Run/RunWizard` signatures | `Program.cs` |
| `WizardOptions` namespace move | `Program.cs`, `WizardWindow` |
| `WizardWindow(WizardSession)`, `MainWindow(DeviceLabApplication, DeviceLabPathBoundaries)` | `App` |
| `WSGM.DeviceLab.Reports` namespace for report types | `Cli/DeviceLabCli.cs`, `Gui/MainWindow.cs`, `Scaffolding/ScaffoldFromLabProjectWorkflow.cs`, tests `LabReviewTests` (moves to `tests/.../Reports/`) |
| Evidence records replace anonymous objects | stages, `LabReview*`, `LabReport`, `LabReviewTests` |
| `LabExport.Prepare(project, cancellationToken)` writes a staging zip; `Write(target, boundaries)` copies it; `Dispose` deletes an unsaved staging file | `FinishStage`, `LabReviewTests`, `LabExportTests` |
| `LabExport.MaximumFileBytes/MaximumTotalBytes/MaximumFiles` deleted | `LabReviewArchive.MaximumTotalBytes` gets its own value |
| `LabProject.JsonOptions` becomes read-only exposure | `LabMotionSteps`, `LabMachineState`, `LabExport`, `LabReport` |
| `LabInputAnalysis.IsPointer` added (other domain file) | `ButtonsStage`, `LabReview.Buttons` |

Interfaces needed from other domains (named dependencies):

- Worker domain: `ILabWorkerClient : IDisposable` with exactly today's `Open<T>`, `Checkpoint<T>`, `Release`,
  `StreamError` (`LabWorkerClient.cs:179,208,235,245`), implemented by `LabWorkerClient`. No new semantics.
- Capture domain: `ILabInputCapture : IDisposable` with today's `Devices`, `Unavailable`, `Now`,
  `SwallowShortcuts`, `Detailed`, `Activity`, `SuspendResume`, `BeginStep`, `EndStep`, `NoiseMap`,
  `RescanHidCollections`, implemented by `LabInputCapture`.
- Machine-state domain: `LabRecovery.RunAsync(ILabWorkerClient, elevated, CancellationToken)` returning notice
  lines, replacing the inline sequence at `WizardWindow.cs:149-180` (LABUI-027).

## 5. Implementation batches

All batches keep strings, control order, sizes and styles identical. Each ends with a green
`dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj -c Release` and the named filter
(`dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj -c Release --filter "..."`). Manual check for
every GUI batch: M01-42 with the UAC prompt declined, compared against the pre-batch window.

### LABUI-B1 Evidence records and review fixes (about 550 lines)
Files: new `Wizard/Evidence/LabStageEvidence.cs`; `Wizard/LabReview.Power.cs`, `LabReview.Rumble.cs`,
`LabReview.Buttons.cs`; `Gui/WizardWindow.Power.cs`, `ClawLighting.cs`, `Rumble.cs`, `Buttons.cs`, `Sleep.cs`
(writer call sites only); `tests/.../Wizard/LabReviewTests.cs`.
Steps: define records with today's property names for `power-tests`, `lighting`, `rumble-routes`,
`rumble-calibration`, `candidates`, `sleep`; replace the anonymous writers; review uses
`LabPowerSummary.IsPass` (LABUI-002) and the lighting `Matched` flag with the old-shape fallback (LABUI-003);
delete the `report` read (LABUI-005); derive the rumble method text from `RumblePulseLengths` (LABUI-021).
Tests: fixtures build the records; new cases for a TDP with `applied-readback-matched` (Confirmed), Aura
`matched` (Confirmed), a legacy-shape lighting file (still parsed). Filter: `FullyQualifiedName~LabReviewTests`.
Dependencies: none.

### LABUI-B2 Reports folder and one archive reader (about 650 lines, mostly moves)
Files: move `LabReport`, `LabReview*`, `LabReviewArchive`, `LabPromote` to `Reports/`; usings in
`Cli/DeviceLabCli.cs`, `Gui/MainWindow.cs`, `Scaffolding/ScaffoldFromLabProjectWorkflow.cs`; move
`LabReviewTests` to `tests/.../Reports/`.
Steps: namespace move; reimplement `LabReport.Read` on `LabReviewArchive` (LABUI-013); remove the
120-character `Compact` from persisted details (LABUI-015); one `DescribeButton` used by `LabButtonPlan`
(LABUI-022). Tests: add a malformed-attempts and oversized-entry case for `report`. Filter:
`FullyQualifiedName~Reports`. Dependencies: CLI/Scaffolding owners accept the using change (no logic change).

### LABUI-B3 Streamed export, project tidy (about 450 lines)
Files: `Wizard/LabExport.cs`, `Wizard/LabProject.cs`, `Reports/LabReviewArchive.cs`, `Gui/WizardWindow.cs`
(finish call site), tests `LabExportTests`, `LabProjectTests`.
Steps: staging zip written during Prepare with per-entry hashing, preview from that pass, `Write` copies the exact
staged bytes create-new, staging deleted on dispose/failure; delete the three caps (LABUI-014); manifest snapshot
under the project lock and a cancellation token (LABUI-030); `JsonOptions` read-only, `BeginAttempt` skips an
existing attempt directory (LABUI-028). Tests: preview hashes equal written entry hashes for every file; staging
removed after a failed write; existing attempt directory skipped; refusal of an existing target kept.
Filter: `FullyQualifiedName~LabExportTests|FullyQualifiedName~LabProjectTests|FullyQualifiedName~Reports`.
Dependencies: none.

### LABUI-B4 Developer GUI runner and statics (about 650 lines)
Files: `Gui/App.cs`, `Gui/DeviceLabGui.cs`, `Program.cs`, `Gui/MainWindow.cs`, new `Gui/GuiOperationRunner.cs`,
`Gui/RecentPaths.cs`, tests `Gui/DeviceLabGuiOperationStateTests.cs` (becomes `GuiOperationRunnerTests`).
Steps: `App` constructor with options (LABUI-010); one boundaries instance from Program (LABUI-032); runner
extraction; delete the post-completion cancel discard (LABUI-008) and message truncation; known-key recent paths
(LABUI-015); add the report button, USB instance field and shared capture preview to existing tabs (LABUI-016).
Tests: duplicate start refused; cancel before completion gives Cancelled and keeps the last result; completion
after a late cancel gives Succeeded; close waits for the running operation then closes once. Filter:
`FullyQualifiedName~Gui`. Dependencies: none.

### LABUI-B5 WizardSession and the UI port (about 1,300 lines)
Files: new `Wizard/Session/{WizardSession,WizardServices,WizardStageContext,WizardOptions,IWizardUi,WizardBuildInfo}.cs`,
`Gui/WizardControls.cs`; `Gui/WizardWindow.cs`, `Gui/WizardWindow.Hardware.cs` (deleted), `Program.cs`; new tests
`tests/.../Wizard/WizardSessionTests.cs`, `FakeWizardUi.cs`.
Steps: session owns project, reservation, lazies, loop, navigation, stop, notes (LABUI-018), close order; existing
stage bodies are wrapped as `IWizardStage` adapters still living in the window partials for this batch, so the
build stays green; delete chaining (LABUI-006); tokens renamed (LABUI-019); recovery called through the session.
Tests: one operation at a time; navigation during a running stage cancels it, waits for its finally, then shows
the requested stage; Stop marks the stage not done; close waits, undoes HidHide, disposes capture, worker, then
the reservation, exactly once, also when close is requested twice; a hardware stage without the reservation
refuses with today's text. Filter: `FullyQualifiedName~WizardSession`. Dependencies: worker domain
`ILabWorkerClient`, capture domain `ILabInputCapture`; machine-state domain `LabRecovery` (if not ready, the session
calls today's statics behind one private method and the batch notes the follow-up).

### LABUI-B6 Preflight, Identity, SystemDump, Finish controllers (about 1,100 lines)
Files: new `Wizard/Stages/{PreflightStage,IdentityStage,SystemDumpStage,FinishStage}.cs`; window partial
`SystemDump.cs` deleted; corresponding code removed from `WizardWindow.cs`; tests.
Steps: move logic; Finish uses the B3 export; system dump finishes before its prompt and reports abandoned
sections (LABUI-020); `WriteEvidence` failures surface (no blanket catch). Tests with `FakeWizardUi` and fakes for
HidHide allowance/PawnIO detection where their types allow; identity stage writes inventory and confirmed device;
finish preview equals the saved file. Filter: `FullyQualifiedName~Stages`. Dependencies: B5.

### LABUI-B7 Buttons and Motion controllers (about 1,200 lines)
Files: new `Wizard/Stages/{ButtonsStage,MotionStage,ModeCommandRun,ControllerInitRunner}.cs`; partials
`Buttons.cs`, `Motion.cs`, `ModeCommands.cs` deleted; `LabInputAnalysis.IsPointer` (other domain, one method).
Steps: move logic; extra-button cap removed (LABUI-015); answer enums (LABUI-029); pointer filter shared
(LABUI-009). Tests with a fake capture: a control redo keeps the earlier attempt; skip-the-rest marks every
remaining control skipped; a reversible init is recovered on stage cancel. Filter:
`FullyQualifiedName~ButtonsStage|FullyQualifiedName~MotionStage`. Dependencies: B5; capture domain interface.

### LABUI-B8 Rumble controller (about 1,100 lines)
Files: new `Wizard/Stages/Rumble/{RumbleStage,RumbleSession}.cs`, `Gui/RumbleViews.cs`; partial `Rumble.cs`
deleted.
Steps: session and output move; views keep identical layout and the controller A/B answer. Tests with a fake
worker: each opened route is recorded before the first write and gets one final zero on success, failure and
cancel; a failed final zero fails the stage; a failed pulse is never replayed; "Still vibrating" zeroes every
route once. Filter: `FullyQualifiedName~RumbleStage`. Dependencies: B5; worker interface; A02_03 may land before
or after (no interaction).

### LABUI-B9 Power tests controllers (about 1,450 lines)
Files: new `Wizard/Stages/Power/{PowerStage,PowerTestGuard,AsusPowerTests,MsiPowerTests,ProcessorPowerTests}.cs`;
partials `Power.cs` (power part), `ProcessorPower.cs` deleted.
Steps: move logic unchanged in order and values; machine updates off the UI thread with null-safe clears
(LABUI-011, -012); stage error finishes Failed (LABUI-007); `WriteEvidenceOnce` catches only the create-new
collision (LABUI-023); identical branches merged. Tests with fake `ILabAtkAcpi`/`ILabMsiWmi`/`ILabAmdSmu`/
`ILabIntelKx` and a temp `LabMachineState`: the record exists before the first write; a verified restore clears it
and releases the checkpoint; an unverified restore keeps it and shows the retry button without retrying; a charger
change before a write stops that test; a thrown stage error yields Failed. Filter:
`FullyQualifiedName~PowerStage|FullyQualifiedName~PowerTests`. Dependencies: B5; worker interface.

### LABUI-B10 Lighting and Sleep controllers (about 750 lines)
Files: new `Wizard/Stages/Power/LightingTests.cs`, `Wizard/Stages/SleepStage.cs`; partials `ClawLighting.cs`,
`Sleep.cs` and the lighting part of `Power.cs` deleted; `LabPowerPlan` gains the lighting kind and Aura zones
from the curated record (other domain file).
Steps: remove UI literals (LABUI-009); Claw and Aura evidence through the B1 records. Tests: Claw profile restored
exactly after a cancelled test; Aura record cleared after the tester is told; lamp-array answers recorded;
sleep with a skipped cycle finishes Skipped. Filter: `FullyQualifiedName~LightingTests|FullyQualifiedName~SleepStage`.
Dependencies: B9; machine-state/plan owner for the `LabPowerPlan` fields.

Order: B1, B2, B3, B4 are independent of each other and of other domains. B5 needs the two interfaces. B6 to B10
follow B5 in any order except B10 after B9. Line counts are estimates of changed lines including moves.

## 6. Risks and open questions

Risks:

- Pixel drift while moving page construction. Device Lab has no headless baselines; mitigate by moving the helper
  constructors verbatim and comparing M01-42 screenshots before and after each GUI batch.
- Machine-changing paths move between classes. Mitigate by keeping write order, values, record/clear points and
  restore order line for line, and by the fake-transport tests in B8/B9 before any attended run.
- Close can still wait as long as a stuck in-process native call (HID enumeration, lamp arrays, motion recorder)
  in the wizard process; today's behavior, not changed by this plan.

Open questions for the maintainer:

1. Should the HID mode commands from `LabModeCommands` run in the elevated worker like the curated init does
   (LABUI-026)? It changes where those writes execute, so it is a safety decision, owned with the machine-state
   reviewer.
2. Is the B3 export staging file inside the test folder acceptable? It is excluded from the report by the
   existing dot-file rule and deleted after saving or on close.
