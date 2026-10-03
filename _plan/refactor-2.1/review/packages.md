# Device packages review: MSI Claw, ASUS ROG Ally, Handheld Companion scaffold

Reviewer: Claude (read-only stage). Baseline: master `1329813f`, clean. Scope read in full: every tracked
non-generated source in `src/WSGM.Device.Msi.Claw/**` (14 C# files, 5,742 lines), `src/WSGM.Device.Asus.RogAlly/**`
(17 C# files, 5,145 lines), `src/WSGM.Device.HandheldCompanion/**` (no C# beyond AssemblyInfo; manifest, README,
protocol doc, notices), the three test projects (Claw 11 files, Ally 6, HC 1), both package AGENTS.md, manifests,
csproj files and the curated bundle list. Glyph SVG/PNG/JSON assets were not re-reviewed byte by byte (data, no
logic). No `src/WSGM.Device.Asus.RogAllyX` exists. Cross-boundary reads: `src/WSGM.Device.Sdk` Services/Plugin/
CommandResults/HidDevices/PluginTrace, `src/WSGM/Shell/DeviceCapabilityRouter.cs`, `DevicePluginRuntime.cs`,
`DeviceCoordinator.cs`, `eng/build-bundle.ps1`, `plugins/curated/*`, and `_ref/HandheldCompanion` (ClawA1M.cs,
PerformanceManager.cs, ROGAlly.cs, AsusACPI.cs).

Prior coverage: the Claude audit never ran U15A/U15B (audit-coverage.md:39-40, 0 saved IDs). The only ledger entry in
this domain is **A02-F010** (claude-findings-disposition.md:641; audit/A02/findings.md:95-101). Units U15A-DVP and
U15B-DVP are `UNFINISHED_FULL_CLOSURE` (audit/A02/unit-status.json:36-58). This report is the first full source
closure of both units; every finding below except PACKAGES-001 is NEW.

---

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
|---|---|---|---|---|
| C1 | "Device packages keep model tables, bytes/provenance and independent service degradation" (refactor-plan.md:149) | accurate | Per-model rows `ClawModels.cs:127-212`, `AllyModels.cs:192-268`; per-service fault isolation `DeviceServiceLifecycle.cs:333-353`; Claw reader loss degrades only the controller `ClawControllerService.cs:271-285`, Ally `AllyControllerService.cs:442-459` | none |
| C2 | "Adapt explicit clock/diagnostics/command contexts" (refactor-plan.md:149, :113) | accurate, under-specified | 62 static `PluginTrace.*` calls; `PluginTrace.Install` at `ClawPlugin.cs:113`, `RogAllyPlugin.cs:126`; 10 `Deadline.After` self-made deadlines (`ClawPlugin.cs:214,429,463`, `ClawPlugin.Recovery.cs:142`, `ClawControllerService.cs:325`, `ClawCapabilities.cs:750`, `RogAllyPlugin.cs:182,371,429`, `AllyControllerService.cs:513`); wall clock in `ClawCapabilities.cs:116,121,747,765`, `MsiWmiPlatform.cs:792-796`; transports built in the parameterless ctor before any host exists (`ClawPlugin.cs:533-551`, `RogAllyPlugin.cs:459-470`) | Name the consumer list (section 4) and use a per-plugin-instance trace sink (refinement R3), so transports built in the ctor need no reshuffle |
| C3 | "Replace `PluginTrace.Install` global routing with instance `PluginDiagnostics`" (refactor-plan.md:113) | inaccurate (name) | `PluginDiagnostics` already exists as the record `IDevicePlugin.GetDiagnosticsAsync` returns (`PluginContracts.cs:85,191`), used by both packages (`ClawPlugin.cs:322-335`, `RogAllyPlugin.cs:271-286`) and Device Lab (`PluginTestWorkflow.cs:99`, `SyntheticPluginFixture.cs:521`) | Call the instance trace sink `PluginTraceSink` (or similar); keep `PluginDiagnostics` as the diagnostics-values record |
| C4 | "consolidate duplicated lifecycle service scaffolding through the existing SDK helpers" (refactor-plan.md:149; DP01 step 1, versions/pre-AM01/task-briefs.md:907+) | stale / partial | API 11 already moved `DeviceService`, `DeviceServiceLifecycle`, `DeviceCommandSerializer`, `DeviceRecoveryJournal` out of both packages (`DeviceApi.cs:54`, SDK AGENTS). What remains duplicated is different: command value validation (3 copies incl. host), write budget, recovery decision/re-arm, motion attachment, capability ID constants, and the plugin shells, which already drifted (PACKAGES-013/014/027/028/030) | Replace with the concrete dedupe list in section 3/4; do not introduce an SDK plugin base class |
| C5 | "Do not change unknown readback into a permission gate" (refactor-plan.md:149; DP01 step 4) | partially (incomplete) | Ally already gates on readback: restore skips limits when the mode readback differs and success requires limit/curve readback (`AllyAcpiCapabilities.cs:151-172,254-273,488-500`); writes are skipped when readback equals target (`:127,239-242,258`) | Add explicit corrections PACKAGES-005/007 |
| C6 | "Preserve Claw watchdog only for a successfully accepted requested pair; an uncertain transport result disarms automatic resend until explicit action" (refactor-plan.md:149; DP01 step 3) | accurate as target, not current behaviour | A failed pair write keeps the previous `_target` armed (`ClawCapabilities.cs:262-271`), a failed watchdog write keeps it armed and repeats every observation pass (`:112-128`), and the watchdog also runs inside the pre-dispatch "revalidation" (`ClawPlugin.Commands.cs:43-48` -> `ClawServices.cs:154-159`) | It is a required change, not a preservation (PACKAGES-002/003) |
| C7 | "Ally tables remain single-shot HC order, release best effort, no HHD timed re-send" (refactor-plan.md:149) | accurate | One write per table in HC order `AllyProtocol.cs:106-112`, `AllyControllerService.cs:364-384`; best-effort release `:402-434`; no timer anywhere | none |
| C8 | DP01 step 3 "Ally tables remain once/per explicit acquire" (versions/pre-AM01/task-briefs.md DP01) | inaccurate | Tables are also written on automatic cycle start/resume and on every pad return (`AllyControllerService.cs:183,498-519`), as HC does on `Device_Inserted` | "once per acquisition or pad return (HC `Device_Inserted`), never timed" |
| C9 | "Preserve motion transform exactly once, bias/resampling, reconnect semantics, haptic stop and allocation-free paths" (refactor-plan.md:149) | partially | Transform once: Claw `WindowsMotionSource.cs:38-46,92-95`, Ally `AllyMotion.cs:34`. Not allocation-free: Ally WinRT sensor path allocates RCWs per sample (`AllyMotion.cs:169-199`), Ally poll loop `.AsTask()` per sample (`AllyInput.cs:477-484`) | Preserve Claw; correct Ally (PACKAGES-011/012) |
| C10 | DP01 step 4 "first original journaling/recovery binding unchanged" | inaccurate | A02-F010 (Claw retry) and Ally `ReportOnly` stranding (PACKAGES-008) require a recovery-policy change; first-original capture stays | "first original capture unchanged; reconciliation policy unified per PACKAGES-001/008/009" |
| C11 | A02-F010 "exact matching firmware returns Restore before inspecting RestoredUnverified/RestoreFailed ... merely returning Block would strand" (audit/A02/findings.md:95-101) | accurate, and understated | `ClawRecoveryJournal.cs:82-88`; the retry is pinned by `ClawPluginTests.cs:757-811`. The existing `Block` branch (EC changed + RestoreFailed, `ClawRecoveryJournal.cs:90-93` -> `ClawPlugin.Recovery.cs:57-64`) already strands the service on every start, contradicting the comment at `:95-97` | Resolution in PACKAGES-001 |
| C12 | A02_02 "ClawRecoveryJournal and AllyRecoveryJournal stay exact same subclasses" (batches/A02_02.md) | accurate, partially protective | Claw `OpenAsync` runs `CheckHealthAsync` (`ClawRecoveryJournal.cs:19-27`), Ally does not (`AllyRecoveryJournal.cs:21-28`), so an unwritable state dir on Ally is first seen inside a command | Follow-up PACKAGES-010 after A02_02 |
| C13 | "API becomes Device 12 / SDK 0.5.0 ... manifests, templates, fixtures and all consumers update coherently" (refactor-plan.md:149; api-integration.md:9) | accurate | Current `apiVersion: 11` in all three manifests (`WSGM.Device.Msi.Claw/plugin.wsgm.json:4`, `RogAlly/plugin.wsgm.json:4`, `HandheldCompanion/plugin.wsgm.json:4`), SDK `<Version>0.4.0</Version>`; HC scaffold test asserts equality (`ScaffoldManifestTests.cs:9-16`) | Include the HC scaffold manifest in the F02 bump |
| C14 | "Current enum numeric values and persisted wire names remain for migration" (refactor-plan.md:149) | accurate, needs a note | `temporary-state.v1.json`: Claw `ControllerMode` is serialized as a number (no string converter on `ClawControllerMode`, `ClawHardware.cs:48-53`, `ClawRecoveryJournal.cs:174`); kinds are strings (`:177`, `AllyRecoveryJournal.cs:114`); capability IDs are persisted in profiles | Keep `ClawControllerMode` numbers 1/2 and all capability ID strings |
| C15 | "Commands have explicit pre-dispatch, dispatched and completed phases. Cancellation before dispatch is Rejected/Cancelled with no write" (refactor-plan.md:111) | accurate as target; violated today | Claw pre-write failures become `Indeterminate`/`RestoreFailed` (PACKAGES-004); Claw pre-dispatch refresh can write but reports `Rejected` (PACKAGES-003); Ally journal-arm failure becomes `Indeterminate` (`RogAllyPlugin.Commands.cs:61-66`) | Name these consumers in the commands contract work |
| C16 | "Write success publishes the written value as Observed ... matching readback upgrades" (refactor-plan.md:111) | partially | Packages do this (`ClawCapabilities.cs:98-105,350-353,441-460`; `AllyAcpiCapabilities.cs:78-86`, `RogAllyPlugin.Commands.cs:77-83`) but SDK `CommandResults.Unverified(command, written)` puts the written value in `ReadbackValue` (`CommandResults.cs:29-39`) against the SDK reference (`reference.md:890`), and both packages strip it again (`ClawPlugin.Commands.cs:474-476`, `RogAllyPlugin.Commands.cs:85-87`) | Fix in SDK, delete package copies (PACKAGES-015) |
| C17 | "Existing vendor support probes are preserved as deliberately authored initial setter checks" (refactor-plan.md:111) | accurate | Claw SHIFT bit 7 check as HC's `SetShiftMode` (`ClawCapabilities.cs:188-197`); Ally mid-fan probe (`AllyAcpiCapabilities.cs:386-391`) | none |
| C18 | "The HC project remains an explicitly non-installable design scaffold" (refactor-plan.md:34; DP01 step 5) | accurate in effect | No `plugins/curated/wsgm.device.handheld-companion.json` (bundle is curated-driven, `eng/build-bundle.ps1:181-190`); manifest names a type that does not exist (`HandheldCompanion/plugin.wsgm.json:6`); README states it (`README.md:17-29`) | Keep as is; no new guard mechanism |
| C19 | B3 busy-owner table: "Device plugin command/lifecycle call: forbidden ... another plugin write/release/Stop, unload" (planning-corrections.md B3) | accurate; package consequences unstated | `DisposeAsync` runs its own `StopAsync` with an invented 12 s deadline (`ClawPlugin.cs:424-431`, `RogAllyPlugin.cs:368-373`); ATKACPI `DeviceIoControl` is synchronous and uncancellable inside the serializer gate (`AsusAcpi.cs:294-319`); `PluginStartContext` carries no deadline (`PluginContracts.cs:145-165`) so start uses 15 s of its own | PACKAGES-026 |
| C20 | Manual row M01-14 "Claw QS/long chord suppress GameBar ...; Xbox Ally X front/rear mapping matches its model" (manual-acceptance-matrix.md:24) | accurate | Chord state machine `ClawInput.cs:129-333`; Ally front/rear tables `AllyModels.cs:184-190,304-322`, `AllyOemServices.cs:165-182` | Add rows for PACKAGES-001 (recovery re-arm) and PACKAGES-011 (Ally motion source order) |
| C21 | Plan preserves `DeviceServiceLifecycle`, `DeviceCommandSerializer`, `DeviceRecoveryJournal` as owners (refactor-plan.md:75) | accurate | Both packages build on them unchanged | none |

---

## 2. Findings

Severity scale: critical / high / medium / low / nit. "Plan" names the refactor-plan line that already demands the
outcome; "NEW" means no plan line or ledger ID covers it.

### PACKAGES-001 (high) Claw re-writes an uncertain restore every fresh cycle, and its Block branch strands services
- Where: `ClawRecoveryJournal.cs:68-99`, `ClawPlugin.Recovery.cs:28-118`, `ClawServiceBase.cs:24-66`, `ClawPlugin.Commands.cs:506-510`; pinned by `ClawPluginTests.cs:757-811` and `ClawModelLifecycleTests.cs:286-294`.
- What: `Decide` returns `Restore` for a matching known firmware before looking at `RestoreFailed`/`RestoredUnverified`, so a restore whose previous write failed is written again at every start without user action. Power and fan restores re-read nothing first. The other branch, `RestoreFailed` + changed EC, returns `Block`, which `ReconcileOutstandingAsync` turns into a permanent `ReconciliationBlockReason` -> `Faulted` on every start, with no command able to reach a faulted service. The comment at `:95-97` claims the opposite. In addition, any explicit command on a service whose entry is unresolved throws inside `BeginAsync` (`DeviceRecoveryJournal.cs:113-118`) and is reported `Indeterminate`, so Claw has no re-arm path at all.
- Coverage: A02-F010 (confirmed). The stranding branch and the missing re-arm are NEW detail.
- Recommendation: one reconciliation rule for both packages, owned by the SDK journal: Pending + same known firmware -> restore once (first crash restore); any firmware change or indistinguishable firmware -> discard with a trace; `RestoredUnverified`/`RestoreFailed` -> keep the entry, write nothing, do not block the service; the next explicit command re-arms it to Pending with the first original kept (Ally's `ArmAsync`, moved into `DeviceRecoveryJournal.BeginAsync`). Release restores only Pending entries. The Claw controller entry keeps its re-read-first restore (`ClawPlugin.Recovery.cs:121-152`), which re-reads state before writing and is allowed. Invert the two pinned tests.

### PACKAGES-002 (high) Claw TDP watchdog stays armed after an uncertain write
- Where: `ClawCapabilities.cs:112-128` (`ReassertAsync`), `:256-277` (`ApplyPairCoreAsync`).
- What: `_target` is set only after a successful write, so a failed write leaves the previous pair armed; the watchdog then rewrites that stale pair whenever the EC reads otherwise. A failed watchdog write leaves `_target` armed and the next observation pass (10 s, `DeviceCommandSerializer.cs:32`) writes again, indefinitely. After an accepted write with mismatching readback, the post-command refresh reasserts immediately (no 5 s spacing yet) because `_lastReassert` starts at `MinValue`.
- Coverage: plan line 149 (target); no ledger ID. NEW finding ID.
- Recommendation: clear `_target` (and `_targetScenario` stays) in both catch paths; reassert only from the periodic pass (PACKAGES-003). That is HC's watchdog (`PerformanceManager.cs:799-848`) minus the uncertain-retry. Tests with a fake clock: failed apply disarms, failed reassert disarms, a new explicit command re-arms.

### PACKAGES-003 (medium) Claw pre-dispatch "revalidation" can write hardware and then report Rejected
- Where: `ClawPlugin.Commands.cs:43-63` -> `ClawPlugin.Observation.cs:71-96` -> `ClawServices.cs:154-159`.
- What: every command on an owned WMI service first calls `RefreshObservedAsync`; for power that runs `ReassertAsync`, which can write the previous pair (two WMI writes, 200 ms apart) before the new command. A failure there is returned as `Rejected` "Current-state revalidation failed", which claims nothing was written. The refresh is also redundant: every handler reads what it needs (`ClawCapabilities.cs:190,497,534`, `JournalCommandAsync` read-original). Ally has no such step.
- Coverage: NEW (plan line 111 phase contract).
- Recommendation: delete the pre-dispatch refresh; split `PowerService.RefreshAsync` into a read-only refresh (post-command) and the watchdog pass (periodic only).

### PACKAGES-004 (medium) Claw reports pre-write failures as Indeterminate with a fabricated RestoreFailed
- Where: `ClawHardware.cs:229-250` (`ClawWriteBudget` throws `OperationCanceledException`), `ClawPlugin.Commands.cs:76-91,492-514,545-552`, `ClawCapabilities.cs:190,497,534`.
- What: a short deadline, a pre-write read failure (scenario byte, fan snapshot), a WMI 3 s timeout during the original read (it is an OCE, so the `not OperationCanceledException` filter at `:498` lets it escape), or a journal `BeginAsync` refusal all land in the generic catch and return `Indeterminate` with `RollbackResult.RestoreFailed`, although nothing was dispatched. The second `Require` at `:511` runs after `BeginAsync` opened an entry, so a refusal there leaves a Pending entry for an unchanged device, and the release later writes that original back (a spurious write). Ally does this right with a typed exception (`AllyWriteBudget.cs:28-29`, `RogAllyPlugin.Commands.cs:51-55`).
- Coverage: NEW (plan line 111 generic).
- Recommendation: one budget helper with a typed exception mapped to `Rejected` (PACKAGES-027); handlers return `Rejected` when their pre-write read fails; move the budget check before `BeginAsync`; `Indeterminate` only after the first setter call, with `RollbackResult.NotRequired`.

### PACKAGES-005 (medium) Ally restore gates writes and success on readback
- Where: `AllyAcpiCapabilities.cs:151-172` (power), `:254-273` (mode), `:488-500` (fans); consumers `AllyServices.cs:119-143,246-273`.
- What: if the mode does not read back within 150 ms the limits are not restored at all; otherwise success requires SPL/SPPT/FPPT (or curve) readback equality. A firmware that clamps or reports late leaves `RestoredUnverified`, the entry is kept, the stop is reported Unverified, and the host then treats the teardown as unverified (resume only after sleep, `DeviceCoordinator.cs:642-660,1535-1553`). This breaks the recorded no-readback rule and contradicts Claw, where a restore is complete once its writes went through (`ClawCapabilities.cs:230-248`).
- Coverage: NEW (plan line 149 states the rule only).
- Recommendation: write mode then limits unconditionally; return true once the writes went through; use readback only to trace. Same for fans.

### PACKAGES-006 (medium) Ally fan curve write performs an automatic rollback write after a failed write
- Where: `AllyAcpiCapabilities.cs:502-554`; fault path `RogAllyPlugin.Commands.cs:68-75`.
- What: when a curve write throws, the plugin immediately writes the previously read curves back. That is an automatic second write after an uncertain one; HC never rolls back, and Claw explicitly does not (`ClawCapabilities.cs:12-19`). A failed rollback then faults the fan service.
- Coverage: NEW.
- Recommendation: delete the rollback; return `Indeterminate` with `NotRequired`; delete the rollback-fault branch in `ExecuteBoundCommandAsync`.

### PACKAGES-007 (low) Ally skips requested writes when a readback already matches
- Where: `AllyAcpiCapabilities.cs:127` (mode), `:239-242` (limits), `:258` (restore mode).
- What: a stale or clamped readback suppresses a requested write. HC writes SPL, then SPPT+FPPT, unconditionally (`ROGAlly.cs:694-702`, `AsusACPI.cs:343-352`).
- Coverage: NEW.
- Recommendation: always write; keep the SPL <= SPPT <= FPPT ordering that the package AGENTS records (see open question Q1).

### PACKAGES-008 (medium) Ally strands power/fans/controller after a BIOS update
- Where: `AllyRecoveryJournal.cs:58-76`, `RogAllyPlugin.Recovery.cs:46-51`, `AllyServices.cs:49-52,172-175`, `AllyControllerService.cs:144-147`.
- What: any firmware mismatch returns `ReportOnly`, which sets `ReconciliationBlockReason`, so the service faults on every start for as long as the entry exists, and nothing can clear it (a faulted service refuses commands, `RogAllyPlugin.Commands.cs:198-200`). Claw discards in the same case for exactly that reason (`ClawRecoveryJournal.cs:95-98`).
- Coverage: NEW.
- Recommendation: the shared policy of PACKAGES-001: changed firmware -> discard with a trace.

### PACKAGES-009 (medium) A restore that was never attempted is recorded as failed
- Where: `RogAllyPlugin.Recovery.cs:53-72,76-103`; Claw analogue `ClawPlugin.Recovery.cs:67-117,125-135`.
- What: Ally `RestoreEntryAsync` returns false when ATKACPI or the vendor collection is not available yet (the `default` branch), and the caller writes `RestoreFailed`, which from then on blocks every automatic restore although nothing uncertain happened. Claw writes `RestoredUnverified` when the controller is not present yet or the record cannot be decoded.
- Coverage: NEW.
- Recommendation: a transport that is not available leaves the entry Pending and the service waits (`ReportOnly` for this cycle); only an attempted write that failed is `RestoreFailed`.

### PACKAGES-010 (medium) Ally journal open skips the writability check
- Where: `AllyRecoveryJournal.cs:21-28` vs `ClawRecoveryJournal.cs:19-27`; `RogAllyPlugin.cs:145-177`.
- What: Ally starts with `FailureReason` unset even when the state directory is not writable, so the first `ArmAsync` inside a command throws and the command is reported `Indeterminate` (`RogAllyPlugin.Commands.cs:61-66`) instead of power/fans/controller being blocked at start.
- Coverage: NEW; adjacent to A02_02 (which does not change subclasses).
- Recommendation: the SDK `LoadAsync` ends with the same write probe `CheckHealthAsync` performs, so both packages get it and Claw's explicit call goes away. Schedule after A02_02 (same file).

### PACKAGES-011 (medium) Ally motion runs the allocating WinRT path first
- Where: `AllyMotion.cs:40-57,143-199`.
- What: WinRT `ReadingChanged` allocates projection objects on every sensor sample, the high-rate path the product rule forbids; Claw dropped WinRT for this reason (Claw AGENTS motion section). The RC73XA Device Lab run found the BMI320 on the standard legacy fields (`AllyMotion.cs:24-30`), which the SDK's allocation-free `LegacyMotionStream` reads.
- Coverage: plan line 149 claims allocation-free paths (inaccurate for Ally). NEW.
- Recommendation: legacy Sensor API first, WinRT only when no legacy sensor pair exists. This is an API order, not device logic; HC's sensor and axis maps stay. Manual X row required.

### PACKAGES-012 (low) Ally controller poll allocates per sample when publication is asynchronous
- Where: `AllyInput.cs:459-485`.
- What: `publish(...).AsTask().GetAwaiter().GetResult()` at 125 Hz allocates a Task whenever the host's publish does not complete synchronously.
- Coverage: NEW.
- Recommendation: fast path on `IsCompletedSuccessfully`; block via `AsTask()` only on the rare slow path.

### PACKAGES-013 (medium) The two plugin shells are copies that already drifted
- Where: `ClawPlugin.cs:92-531` vs `RogAllyPlugin.cs:105-457` (Start skeleton, Suspend, Resume, Stop, Dispose, RollBack, Context, SetControllerManagement, ReleaseController, diagnostics); `ClawPlugin.Commands.cs:14-94` vs `RogAllyPlugin.Commands.cs:16-88`.
- What: drift with behavioural effect: Claw resume re-acquires every service (`ClawPlugin.cs:289`), Ally only suspendable or non-owned ones (`RogAllyPlugin.cs:249-252`); journal health at open (PACKAGES-010); pre-write classification (PACKAGES-004 vs typed Ally path); Claw refreshes before commands, Ally does not (PACKAGES-003).
- Coverage: plan line 149 ("consolidate ... through existing SDK helpers"), stale as written (C4). NEW detail.
- Recommendation: no SDK base class (the Claw is the copyable MIT reference and a framework would add mechanism). Remove the duplicated decisions instead, via the SDK pieces of PACKAGES-001/010/014/015/027/028, and align resume on one rule: re-acquire every service, as Claw does, because firmware may reset limits across a sleep and acquire is where the services re-read; Ally's power/fan acquire then reuses its existing capability object instead of replacing it (`AllyServices.cs:66,189`), and the Ally resume filter (`RogAllyPlugin.cs:249-252`) goes.

### PACKAGES-014 (medium) Command value validation exists three times with different strictness
- Where: Claw `ClawPlugin.Commands.cs:288-447`; Ally `RogAllyPlugin.Commands.cs:206-272` (no step, no curve ordering, no boolean); host `DeviceCapabilityRouter.cs:1126-1160` (`DeviceCapabilityValidation.ValueMatches`), which already rejects out-of-descriptor values before dispatch (`:456-482`).
- Coverage: NEW.
- Recommendation: move `ValueMatches` into the SDK as a public deterministic helper; host and both packages call it; delete the package copies. The packages keep their generation/deadline/power-source checks, which are their own revalidation duty.

### PACKAGES-015 (medium) SDK `CommandResults.Unverified(command, written)` sets `ReadbackValue`, both packages undo it
- Where: `CommandResults.cs:29-39`; `ClawPlugin.Commands.cs:454-477`; `RogAllyPlugin.Commands.cs:85-87`; SDK reference `reference.md:890`.
- Coverage: NEW (SDK domain, consumer here).
- Recommendation: fix the SDK helper (unverified carries no readback); delete `NormalizeCommandResult` and the Ally strip. Host consumers only read `ReadbackValue` on `AppliedVerified` (`CommandOutcomeExtensions.cs:32`) or for a trace (`AutoTdpService.cs:938`).

### PACKAGES-016 (low) Rollback reporting is fiction and its branches are dead
- Where: Claw handlers only return `NotRequired` (`ClawCapabilities.cs:32-42`), so `ClawPlugin.Commands.cs:516-540` and most of `ClawRecoveryJournal.CompleteCommandAsync` (`:43-58`) never run; the Claw `Indeterminate` helper stamps `RestoreFailed` on every handler exception (`ClawPlugin.Commands.cs:545-552`); Ally lighting reports `RestoreFailed` for a never-journalled write (`AllyServices.cs:430-431`). The host only logs `Rollback` (`DeviceCapabilityRouter.cs:698`).
- Coverage: NEW.
- Recommendation: report `NotRequired` everywhere in both packages (with PACKAGES-006 there are no command rollbacks left); delete the dead branches and shrink `CompleteCommandAsync` to "an entry this command opened is closed when the command was refused".

### PACKAGES-017 (medium) Claw identity revalidation per command is a burst of WMI work that rebinds MSI_ACPI
- Where: `MsiWmiPlatform.cs:32-47,330-456`; called per command (`ClawPlugin.Commands.cs:33`), per resume and per controller enable.
- What: three SMBIOS queries, a PnP query, a provider probe that disposes and rebinds the WMI instance, `Get_WMI`, `Get_EC` and a battery query, each behind the 3 s serialized gate, before every slider step. Ally reads the registry copy of SMBIOS (`AllyIdentity.cs:42-67`).
- Coverage: NEW.
- Recommendation: read the SMBIOS/PnP snapshot and firmware binding once per `StartAsync`/resume; per command re-read only the AC line (cheap `GetSystemPowerStatus`, as Ally does); probe the provider without invalidating it. Keeps the model/generation refusal. See open question Q2 for the AGENTS wording.

### PACKAGES-018 (medium) Freed HID preparsed data can be used by a still-running reader
- Where: `WindowsHidTransports.cs:363-428` (`StopAsync`), `HidDescriptorGamepad.cs:62-70,118-173`.
- What: when `reader.WaitAsync(cancellationToken)` is cancelled, `StopAsync` continues and disposes the descriptor (`HidD_FreePreparsedData`) while the reader may be inside `TryDecode` calling `HidP_*` on that pointer. Affects every Claw except the measured MS-1T52 path.
- Coverage: NEW.
- Recommendation: the reader owns the descriptor and frees it in its own `finally`; `StopAsync` never frees it underneath. No new state.

### PACKAGES-019 (low) Motion stream stop semantics differ and the Claw timeout can leave two streams
- Where: Claw `WindowsMotionSource.cs:51-66` (2 s timeout throws, `_stream` already null so the next start opens a second stream while the first poll thread may still publish); Ally `AllyMotion.cs:60-71` (unbounded synchronous dispose).
- Coverage: NEW.
- Recommendation: one bounded stop in the SDK `LegacyMotionStream` that retains an unfinished stream and refuses a second start until it has exited.

### PACKAGES-020 (low) Redundant locks around haptic output
- Where: `ClawControllerService.cs:74-79,427-467,552-556`; `WindowsHidTransports.cs:281-292,430-460`; Ally `AllyControllerService.cs:121-122,280-312`.
- What: `_hapticGate` lives inside a semaphore that already serializes output; the only out-of-semaphore use (the reset in stop) can move inside it.
- Coverage: NEW. Recommendation: delete `_hapticGate` in both packages.

### PACKAGES-021 (low) Claw release compares physical location by string
- Where: `ClawControllerService.cs:403-405` vs `HidDevices.SamePhysicalLocation` everywhere else (`ClawPlugin.Recovery.cs:148-151`, `ClawControllerService.cs:150`).
- Coverage: NEW. Recommendation: use `SamePhysicalLocation`.

### PACKAGES-022 (low) Claw publishes an undeclared scenario value
- Where: `ClawPlugin.Surface.cs:580-596` returns `"unknown"`, not among the declared choices (`:137-143`); the host drops such a state (`DeviceCapabilityRouter.cs:576-587`) and keeps showing the previous one.
- Coverage: NEW. Recommendation: return null (Unknown quality).

### PACKAGES-023 (low) WMI timeouts are reported as Quiescing
- Where: `ClawCapabilities.cs:32-42`; the 3 s WMI timeout surfaces as an OCE (`MsiWmiPlatform.cs:108-121`).
- Coverage: NEW. Recommendation: `Quiescing` only when the caller's token was cancelled, otherwise `TransportFaulted`.

### PACKAGES-024 (low) Clocks and delays are not injectable
- Where: `ClawCapabilities.cs:116,121` (watchdog), `:747-765` (lighting rate limit uses wall clock and `Task.Delay`, ignoring the injected delay), `MsiWmiPlatform.cs:792-796`, the 10 `Deadline.After` sites (C2), `ObservedAt = DateTimeOffset.UtcNow` (`ClawPlugin.Surface.cs:327`, `RogAllyPlugin.Surface.cs:177`).
- Coverage: plan line 113 (F02).
- Recommendation: take the plan's owned clock through the start context; route the lighting wait through the same delay/clock.

### PACKAGES-025 (medium) Static trace sink, and tests serialized around it
- Where: `ClawPlugin.cs:113`, `RogAllyPlugin.cs:126`, 62 static calls including static helpers (`ClawObservation`, `ClawApplied.Failed`, `MsiEventRepair`, `WindowsClawMcuTransport.AwaitAcknowledgementAsync`, `WindowsAsusAcpi.Read`); all eight Claw test classes share `[Collection("plugin-trace")]` (`ClawPluginTests.cs:17-19` and siblings); Ally tests run in parallel against the same static.
- Coverage: plan line 113, api-integration.md:9 (F02).
- Recommendation: per-plugin-instance `PluginTraceSink` created in the plugin ctor and handed to every transport and helper; `StartAsync` attaches the host, stop/dispose detaches. Removes the test collection.

### PACKAGES-026 (low) Plugin-invented deadlines bypass the host's shutdown budget
- Where: `ClawPlugin.cs:214,424-431,463`; `RogAllyPlugin.cs:182,368-373,429`; `PluginContracts.cs:145-165` (no start deadline).
- What: start acquires under 15 s of the plugin's choosing; `DisposeAsync` runs a full `StopAsync` with 12 s if the host never stopped it; rollback uses 12 s. Under B3 the host owns one outer deadline.
- Coverage: B3 (partially). NEW detail.
- Recommendation: the start context carries the host deadline and clock (F02); `DisposeAsync` releases handles only and never writes hardware (the journal covers an un-stopped cycle on the next start).

### PACKAGES-027 (low) Two copies of the write budget with different exception contracts
- Where: `ClawHardware.cs:229-250`, `AllyWriteBudget.cs:8-29`.
- Coverage: NEW. Recommendation: one SDK helper with a typed exception; packages map it to `Rejected`. The 2 s minimum is an existing, justified boundary (a write the host cannot wait for becomes uncertain), not new mechanism.

### PACKAGES-028 (low) Motion attachment is duplicated
- Where: Claw `ClawServices.cs:393-542`, Ally `AllyControllerService.cs:21-95` (latest sample + `GyroFrameResampler` + `Current(now)`); Ally lacks the one-shot staleness trace.
- Coverage: NEW. Recommendation: one SDK `MotionAttachment` (pure, allocation-free) that both services wrap.

### PACKAGES-029 (nit) Reader-fault/reconnect pattern duplicated
- Where: `ClawControllerService.cs:271-344`, `AllyControllerService.cs:442-494`, `AllyOemServices.cs:57-94`.
- Coverage: NEW. Recommendation: no change; the protocols differ and the copies have not drifted.

### PACKAGES-030 (low) Shared capability IDs and surface vocabulary duplicated
- Where: `ClawCapabilities.cs:855-906` vs `RogAllyPlugin.Surface.cs:364-405`; `SourceOwnershipChoices` (`ClawPlugin.Surface.cs:97`, `RogAllyPlugin.Surface.cs:17`); descriptor builder helpers in both.
- What: the IDs are persisted wire names (profiles), so a silent divergence would orphan saved per-game values.
- Coverage: NEW. Recommendation: well-known capability ID constants and the ownership choices in the SDK beside `DeviceSections`; values unchanged.

### PACKAGES-031 (nit) Dead code and test-only seams
- `ClawPlugin.Surface.cs:558-578` `BooleanDescriptor` unused; `AllyInput.cs:135-145` `AllyOemButtonState.Clear` unused; `ClawPlugin.cs:65` `Services` and `WindowsHidTransports.cs:19-20` `Serializer` exist for tests; `ClawHardware.cs:35-36` `DefaultLightingProfileAddress` and `WindowsHidTransports.cs:623-626` `IsSupportedDevicePath` are used only by tests. Recommendation: delete the unused; keep the two seams only if the rewritten tests still need them.

### PACKAGES-032 (low) Claw reads the MCU revision through WMI PnP parsing and falls back to its own row choice
- Where: `MsiWmiPlatform.cs:417-423,513-545`, `ClawModels.cs:239-251`.
- What: HC reads `Attributes.Version` of the HID device (`ClawA1M.cs:395-398`); the SDK already exposes it as `HidCollection.ReleaseNumber` (`HidDevices.cs:29,426`). An unreadable revision takes the last table row, an own heuristic (HC would take the row nearest zero).
- Coverage: NEW. Recommendation: take the revision from the MCU collection's `ReleaseNumber`, which makes the fallback practically unreachable; keep the documented fallback (open question Q3).

### PACKAGES-033 (low) MSI_Event repair: timeout escapes and the repair repeats on every resume
- Where: `MsiWmiPlatform.cs:744-856`; HC `ClawA1M.cs:417,846-866`.
- What: `WaitForExitAsync` timeout throws an OCE that the catch list (`:785-786`) does not cover, so the OEM service faults and `pnputil` keeps running. The repair runs in `OemEventService.AcquireAsync`, so a repair that does not help restarts `ACPI\PNP0C14` (the device MSI_ACPI power/fan calls go through) on every wake.
- Coverage: NEW. Recommendation: catch the timeout and trace; run the repair from `StartAsync` only (HC runs it from `Open`), not from resume.

### PACKAGES-034 (nit) HC scaffold
- `plugin.wsgm.json:6` names a type that does not exist; the only test asserts the API level (`ScaffoldManifestTests.cs:9-16`). Non-installability rests on the missing curated entry, which is enough. Recommendation: bump `apiVersion` with F02; no other change.

### PACKAGES-035 (low) Ally release budget refusal faults the service
- Where: `AllyServices.cs:121,251,287` -> `DeviceServiceLifecycle.cs:346-352`.
- What: the package AGENTS says a budget refusal "has touched nothing: never fault the service"; at release it reports Faulted and the stop as Failed.
- Coverage: NEW. Recommendation: `ReleasedUnverified`, entry stays Pending for the next start.

### PACKAGES-036 (low) Ally re-arm restores an original captured under another BIOS
- Where: `AllyRecoveryJournal.cs:36-55`.
- What: re-arm ignores the entry's firmware binding, and release writes that original.
- Coverage: NEW. Recommendation: in the shared rule, a changed binding discards and the command captures afresh.

### PACKAGES-037 (medium) Tests pin defects and lack the lifecycle edges that matter
- Pinned: `ClawPluginTests.cs:757-811` (automatic retry), `ClawModelLifecycleTests.cs:286-294` (stranding Block).
- Missing: watchdog disarm; pre-dispatch classification; unknown-scenario publication; Ally firmware-change discard; not-attempted restore; release budget; descriptor freed while decoding; Ally restore without readback. Real-time dependencies block deterministic tests (`ClawCapabilities.cs:116,747-760`).
- Coverage: NEW. Recommendation: per batch in section 5.

### PACKAGES-038 (nit) Claw project does not pin x64
- `WSGM.Device.Msi.Claw.csproj` has no `<PlatformTarget>x64</PlatformTarget>` though its AGENTS says x64 and the 40-byte `INPUT` layout is x64-only (`ClawInput.cs:492-505`); Ally pins it (`WSGM.Device.Asus.RogAlly.csproj:6`). Latent (host is x64). Recommendation: add it.

### PACKAGES-039 (nit) Ally keyboard hook queue machinery
- `AllyInput.cs:598-616,703-741`: bounded channel of 64, withdrawing the source when full, plus a generation counter. The bound is a real hook-thread boundary (the callback cannot block), not an arbitrary limit. No change.

---

## 3. Plan refinements

Additions:
- R1. Add a package recovery-policy item resolving A02-F010 for both packages (PACKAGES-001/008/009/036): shared rule in the SDK journal, `BeginAsync` re-arms an unresolved entry instead of throwing, release restores only Pending entries, firmware change discards. This turns the NOT READY A02-F010 into a concrete batch (PACKAGES-B2).
- R2. Add the Ally readback/rollback corrections (PACKAGES-005/006/007) under "Do not change unknown readback into a permission gate", which today reads as preservation.
- R3. Replace "instance `PluginDiagnostics`" with a per-plugin-instance `PluginTraceSink` (name collision, C3). The sink is created in the plugin constructor, so production transports built there need no factory reshuffle; `StartAsync` attaches the host.
- R4. Name the SDK-level dedupes the packages need (PACKAGES-014/015/027/028/030) and route them through F02 so the API bump happens once.
- R5. Add command-phase truthfulness consumers for both packages (PACKAGES-003/004/016/023) to the commands contract.
- R6. Add manual rows: Claw recovery after a failed restore plus explicit command re-arm (C environment); Ally motion with legacy-first order and power/fan restore without readback (X environment).

Changes:
- DP01 step 3: "Ally tables once per acquisition or pad return, never timed" (C8).
- DP01 step 4: "first-original capture unchanged; reconciliation unified" (C10).
- refactor-plan.md:149 "consolidate duplicated lifecycle service scaffolding": replace with the concrete list in section 4; explicitly no SDK plugin base class.

Over-engineering to avoid or remove (simplify rule):
- Do not add an SDK `DevicePluginBase`/cycle framework to absorb the two shells (PACKAGES-013). Fix the drifted decisions instead.
- Remove the Ally fan rollback (PACKAGES-006), the Claw/Ally rollback-fault branches and `NormalizeCommandResult` (PACKAGES-015/016), the Claw pre-dispatch refresh (PACKAGES-003), `_hapticGate` (PACKAGES-020), Ally readback-skip logic (PACKAGES-007), `ArmAsync`/`PendingOriginalFor` as package mechanism (moved into SDK `BeginAsync`).
- Do not add a guard for HC-scaffold installability (C18), nor a once-only flag for MSI_Event repair: move the call instead (PACKAGES-033).
- The B3 table must not introduce per-plugin budgets inside packages: packages honour the host deadline/token and never invent one (PACKAGES-026).
- No arbitrary limits found or proposed in this domain; the 64-entry Ally keyboard channel is a real hook boundary (PACKAGES-039), and the 2 s write budget is an existing boundary kept as one copy.

---

## 4. Target design

Owners stay per package; no new project, no base class. Pure shared rules move into the MIT SDK.

SDK additions/changes (land with F02, Device API 12):
- `DeviceRecoveryJournal<TState>`: `BeginAsync` re-arms an unresolved entry to Pending (first original kept, `Opened=false`); new `PendingOriginalFor(serviceId)`; `LoadAsync` ends with the writability probe; new static `Reconcile(entry, currentFirmware, bool firmwareComparable)` returning `DeviceRecoveryAction { Restore, Wait, Keep, Discard }`.
- `CapabilityValueValidation.ValueMatches` (moved from host `DeviceCapabilityValidation`).
- `CommandResults.Unverified(command, written)` no longer sets `ReadbackValue`.
- `DeviceWriteBudget.IsAvailable/Require` + `DeviceWriteBudgetException`.
- `MotionAttachment` (latest sample, resampler, `Current(now)`, one-shot staleness trace through the sink).
- `PluginTraceSink` (instance) replacing static `PluginTrace`.
- Well-known capability ID constants and ownership choices next to `DeviceSections` (values unchanged).

Old symbol -> new owner:

| Old symbol (file) | New owner |
|---|---|
| `AllyRecoveryJournal.ArmAsync` (AllyRecoveryJournal.cs:36-49) | `DeviceRecoveryJournal.BeginAsync` re-arm semantics |
| `AllyRecoveryJournal.PendingOriginalFor` (:52-55) | `DeviceRecoveryJournal.PendingOriginalFor` |
| `AllyRecoveryJournal.Decide`, `AllyReconciliationAction` (:58-112) | `DeviceRecoveryJournal.Reconcile`, `DeviceRecoveryAction` |
| `ClawRecoveryJournal.Decide`, `ClawReconciliationAction` (ClawRecoveryJournal.cs:68-148) | same SDK members; Claw passes `firmwareComparable = !IsUnknownEc(...)` |
| `ClawRecoveryJournal.OpenAsync` health call (:25) | SDK `LoadAsync` |
| `ClawRecoveryJournal.CompleteCommandAsync` (:35-66) | three lines inlined in `ClawPlugin.JournalCommandAsync` |
| `ClawWriteBudget` (ClawHardware.cs:229-250), `AllyWriteBudget`, `AllyBudgetException` (AllyWriteBudget.cs) | `DeviceWriteBudget`, `DeviceWriteBudgetException` |
| `ClawPlugin.ValidateCommandValue`, `ValueOutOfRange` value cases (ClawPlugin.Commands.cs:354-447) | `CapabilityValueValidation.ValueMatches` |
| `RogAllyPlugin.Validate` value switch (RogAllyPlugin.Commands.cs:245-260) | same |
| host `DeviceCapabilityValidation.ValueMatches` (DeviceCapabilityRouter.cs:1126-1160) | same (host calls SDK) |
| `ClawPlugin.NormalizeCommandResult` (:454-477), Ally strip (RogAllyPlugin.Commands.cs:85-87) | deleted (SDK fix) |
| `ClawPlugin.Indeterminate` (:545-552) | `CommandResults.Indeterminate(..., NotRequired)` |
| Claw rollback-fault branch (:516-540), Ally rollback-fault (RogAllyPlugin.Commands.cs:68-75), Ally fan rollback (AllyAcpiCapabilities.cs:539-553) | deleted |
| Claw pre-dispatch refresh (ClawPlugin.Commands.cs:43-48) | deleted |
| `PowerService.RefreshAsync` watchdog part (ClawServices.cs:154-159) | `PowerService.WatchdogAsync`, called only by `RefreshAllObservedAsync` |
| `MotionService` core (ClawServices.cs:393-542), `AllyMotionService` core (AllyControllerService.cs:21-95) | `MotionAttachment` wrapped by each service |
| `CapabilityIds`, `SourceOwnershipChoices` in both packages | SDK well-known constants |
| `PluginTrace` static calls (62) | `PluginTraceSink` instance passed from the plugin ctor |
| `BooleanDescriptor`, `AllyOemButtonState.Clear`, `DefaultLightingProfileAddress` | deleted |
| `ControllerService._hapticGate` (both) | deleted |
| Claw MCU revision from WMI PnP (MsiWmiPlatform.cs:417-423) | MCU `HidCollection.ReleaseNumber` |

No file is dissolved; `AllyWriteBudget.cs` is deleted (both declarations move to the SDK). Public API changes are
SDK-only (above); package public surface stays `ClawPlugin`/`RogAllyPlugin` with their parameterless ctors.

Consumers that must change: both packages and their tests; host `DeviceCapabilityRouter.cs` (validation call) and any
other `DeviceCapabilityValidation.ValueMatches` caller (7 references); `AutoTdpService.cs:938` (trace only, reads
`ReadbackValue`); SDK tests (`DeviceRecoveryJournalTests`, `CommandResults` tests); Device Lab `SyntheticPluginFixture`
and test runner (API 12, trace sink); `TestPluginHostAdapter` unaffected; three manifests and templates (API 12).

---

## 5. Implementation batches

Narrow test filters use the package test projects:
`dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~X"` and the
Ally/HC equivalents.

### PACKAGES-B1 Claw command truthfulness and watchdog (no cross-domain dependency)
- Files: `ClawCapabilities.cs`, `ClawServices.cs`, `ClawPlugin.Commands.cs`, `ClawPlugin.Observation.cs`, `ClawPlugin.Surface.cs`, `ClawHardware.cs`; tests `ClawCapabilitiesTests.cs`, `ClawPluginTests.cs`.
- Steps: disarm `_target` on failed apply/reassert (002); split read-only refresh from `WatchdogAsync`, call the watchdog from the periodic pass only (003); delete the pre-dispatch refresh (003); typed budget exception mapped to `Rejected`, budget before `BeginAsync`, pre-write read failures `Rejected`, `Indeterminate` only after the first setter with `NotRequired` (004/016); caller-cancel vs timeout classification (023); Scenario unknown -> null (022); delete dead rollback-fault branch (016) and `BooleanDescriptor` (031).
- Tests: failed pair write disarms; failed reassert disarms; explicit command re-arms; no write before a command; short deadline -> `Rejected` with no journal entry; scenario read failure -> `Rejected`; WMI timeout -> `TransportFaulted`; undeclared scenario -> null state. Filter `FullyQualifiedName~ClawCapabilitiesTests|FullyQualifiedName~ClawPluginTests`.
- Estimate: ~600 lines.

### PACKAGES-B2 Unified recovery policy (A02-F010) (depends on A02_02 landing in `DeviceRecoveryJournal.cs`)
- Files: `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (re-arm in `BeginAsync`, `PendingOriginalFor`, `Reconcile`, health probe at the end of `LoadAsync`); `ClawRecoveryJournal.cs`, `ClawPlugin.Recovery.cs`, `ClawServiceBase.cs` (restore only Pending), `ClawPlugin.Commands.cs` (journal call); `AllyRecoveryJournal.cs`, `RogAllyPlugin.Recovery.cs`, `AllyServices.cs`, `AllyControllerService.cs`; SDK `DeviceRecoveryJournalTests.cs`; package tests.
- Steps: as R1; unavailable transport -> Wait (009); firmware change -> Discard in both (008/036); release budget refusal -> `ReleasedUnverified` (035); invert the two pinned Claw tests (037).
- Dependency: SDK domain owns the file; coordinate with A02_02 (same file, run after it). The `DeviceApi` bump itself waits for F02 (behaviour-only change, in-repo consumers only).
- Tests: failed restore not rewritten at next start and service usable; explicit command re-arms and release writes the first original; EC/BIOS change discards; unavailable ATKACPI keeps Pending; Ally unwritable dir blocks at start. Filters `FullyQualifiedName~DeviceRecoveryJournalTests`, `FullyQualifiedName~ClawModelLifecycleTests|FullyQualifiedName~ClawPluginTests`, `FullyQualifiedName~PluginTests` (Ally).
- Estimate: ~700 lines.

### PACKAGES-B3 Ally write-through semantics and hot paths (no cross-domain dependency)
- Files: `AllyAcpiCapabilities.cs`, `AllyServices.cs`, `RogAllyPlugin.Commands.cs`, `AllyMotion.cs`, `AllyInput.cs`; tests `AcpiCapabilityTests.cs`, `PluginTests.cs`, `AllyFakes.cs`.
- Steps: restore writes mode then limits unconditionally and succeeds when the writes went through (005); remove readback-based skips (007); delete fan rollback and the rollback fault branch, lighting `NotRequired` (006/016); journal-arm failure -> `Rejected` (C15); legacy-first motion (011); publish fast path (012); delete `_hapticGate` (020) and `AllyOemButtonState.Clear` (031).
- Tests: restore with a mode that reads back late still writes limits and clears the entry; equal readback still writes; failed curve write writes nothing else; fake motion source order. Filter `FullyQualifiedName~AcpiCapabilityTests|FullyQualifiedName~PluginTests`.
- Estimate: ~500 lines.

### PACKAGES-B4 Shared deterministic helpers (depends on SDK/F02 and the device-host domain for the router call)
- Files: SDK `CapabilityValueValidation`, `CommandResults.cs`, `DeviceWriteBudget`, `MotionAttachment`, well-known ID constants; host `DeviceCapabilityRouter.cs`; package `ClawPlugin.Commands.cs`, `RogAllyPlugin.Commands.cs`, `ClawHardware.cs`, `AllyWriteBudget.cs` (deleted), `ClawServices.cs`, `AllyControllerService.cs`, both surface files.
- Steps: move and call (section 4 table); delete package copies (014/015/027/028/030).
- Tests: SDK unit tests for each helper (value kinds, step, curve order, unverified without readback); package suites unchanged in behaviour. Filters `FullyQualifiedName~Capabilities` (SDK), package suites, `FullyQualifiedName~DeviceCapabilityRouter` (host).
- Estimate: ~900 lines.

### PACKAGES-B5 F02 consumer adaptation (atomic with F02, api-integration.md:9)
- Files: all plugin and transport files of both packages, three manifests, `ScaffoldManifestTests.cs`, all package tests.
- Steps: `PluginTraceSink` instance from the ctor (025); owned clock and host start/stop deadlines through contexts, `DisposeAsync` handles only (024/026); `apiVersion: 12`; drop `[Collection("plugin-trace")]`; fake clock in watchdog and lighting-rate tests.
- Dependency: SDK clock/`Deadline`/trace-sink API from F02; whole solution must build at the end of the group.
- Tests: full package suites (mechanical); add deterministic second-reassert and lighting-interval tests. Filter: whole package test projects.
- Estimate: ~1,400 lines, mostly mechanical.

### PACKAGES-B6 Claw transports and identity (after B5 to avoid signature churn)
- Files: `MsiWmiPlatform.cs`, `WindowsHidTransports.cs`, `HidDescriptorGamepad.cs`, `WindowsMotionSource.cs` (or SDK `LegacyMotionStream` bounded stop, SDK domain), `ClawControllerService.cs`, `ClawModels.cs`, `WSGM.Device.Msi.Claw.csproj`; tests `ClawPluginTests.cs`, `WindowsHidTransportsTests.cs`.
- Steps: identity snapshot per start/resume, AC per command, provider probe without rebind (017); MCU revision from `ReleaseNumber` (032); reader owns and frees the descriptor (018); bounded motion stop with retention (019); `SamePhysicalLocation` (021); MSI_Event repair from `StartAsync` only, timeout caught (033); `PlatformTarget x64` (038); delete test-only leftovers (031).
- Tests: identity read count per command; stop during decode frees after reader exit (fake reader); second start refused while the old stream runs. Filter `FullyQualifiedName~ClawPluginTests|FullyQualifiedName~WindowsHidTransportsTests|FullyQualifiedName~WindowsMotionSourceTests`.
- Estimate: ~500 lines.

### PACKAGES-B7 Documentation (Z02)
- Files: both package README/PROVENANCE, SDK `reference.md`, proposed AGENTS diffs (separate sign-off, plan line 175).
- Content: recovery rule, no command rollback, watchdog disarm, Ally write-through restore, motion source order. ~200 lines.

Order: B1 and B3 first (independent), B2 after A02_02, B4 and B5 inside the F02 group, B6 after B5, B7 last.
Tests deferred in this read-only stage: all.

---

## 6. Risks and open questions

Risks: B2 changes what happens after a failed restore on real hardware (needs the C and X manual rows); B3 changes the
Ally motion API order and restore semantics on a blind package (X tester only, ordinary scenarios); B5 is a wide
mechanical change inside the atomic F02 group.

Open questions for the maintainer:
- Q1. Ally power write order: the package AGENTS requires SPL <= SPPT <= FPPT after every step (an Ally X Lab rule), while HC writes SPL then SPPT+FPPT. Keep the ordered writes (recommended, recorded rule) or mirror HC's order?
- Q2. Claw AGENTS says every command "must revalidate live identity". SMBIOS cannot change at runtime; PACKAGES-017 re-reads it per start/resume and only the AC line per command. Accept the wording change?
- Q3. Unreadable MCU revision: keep the reference unit's row (current, documented) or HC's nearest-to-zero row? With PACKAGES-032 the case becomes practically unreachable.
