# Device packages review: adversarial verification

Verifier: Claude (read-only). Baseline master `1329813f`. Target: `_plan/refactor-2.1/review/packages.md`.
Read in full: every C# file in `src/WSGM.Device.Msi.Claw`, `src/WSGM.Device.Asus.RogAlly`, the HC scaffold
(manifest, csproj, README), both package AGENTS.md, the Claw/Ally/HC test projects where they pin behaviour, and
the SDK pieces the packages lean on (`DeviceRecoveryJournal`, `DeviceService`, `DeviceServiceLifecycle`,
`DeviceCommandSerializer`, `CommandResults`). Callers traced into `DeviceCapabilityRouter`, `DevicePluginRuntime`,
`DeviceCoordinator`, `Core/DeviceConfiguration.cs`, Device Lab `PluginTestWorkflow`, `eng/pack-device.ps1`. HC
cross-checks: `ClawA1M.cs` (Open, StartWatching, ApplyM12Configuration), `PerformanceManager.cs`, `ROGAlly.cs`.

Verdict summary: the review is accurate and well grounded. None of its critical/high/medium findings is wrong about
what the code does. Several severities and recommendations need correcting, two recommendations add mechanism
without a demonstrated defect, three recommended changes contradict package AGENTS rules that the batch order only
gets signed off afterwards, and the review missed a class of Claw timeout-handling defects that breaks the readback
rule.

---

## Refuted

- **C9 (Ally poll part)**: "Ally poll loop `.AsTask()` per sample" is not an allocation per sample.
  `ValueTask.AsTask()` on a synchronously completed non-generic `ValueTask` returns `Task.CompletedTask`
  (`AllyInput.cs:477-484`). It allocates only when the host's publish completes asynchronously, which is what
  PACKAGES-012 itself says correctly. C9 stays valid for the WinRT motion path only (`AllyMotion.cs:169-199`).

No finding is refuted outright: every critical/high/medium finding describes the code as it behaves.

---

## Corrected

- **PACKAGES-002** high -> **medium**. Mechanism confirmed (`ClawCapabilities.cs:112-128,256-277`): `_target` is set
  only after success, a failed watchdog write leaves it armed, and `_lastReassert` starts at `default`, so the
  post-command refresh can reassert at once. But what gets rewritten is the previously accepted pair, rate-limited
  to 5 s, which is HC's own watchdog behaviour (HC ignores write errors and repeats). It is a plan-rule violation
  (refactor-plan.md:149), not a high-impact defect.
- **PACKAGES-006** medium -> **low**, and the fix needs guidance sign-off first. The rollback is real
  (`AllyAcpiCapabilities.cs:539-553`), but it writes the captured original, not the uncertain value again, and it is
  documented design: Ally AGENTS says "Only a journalled original's failed rollback faults the service". Deleting it
  is a simplification that changes a recorded rule (plan line 175: AGENTS changes need separate approval), not a fix
  for a rule violation.
- **PACKAGES-007** low -> **medium**. Worse than reported: `ApplyLimitsAsync` passes `Effective()` as the
  current state (`AllyAcpiCapabilities.cs:105`), and `Effective()` fills a silent firmware's limits from `_written`
  (`:78-86`). On the Xbox Ally X, which reports 0 W (= unknown), re-sending the value last written in this cycle
  (profile re-apply, AutoTDP settling on the same value) is skipped entirely (`WriteOrderedAsync`, `:239-242`), yet
  `_written` is updated and the host is told Observed. After a firmware-side reset there is no way to put the limit
  back without changing it first. HC writes unconditionally (`ROGAlly.cs:694-702`).
- **PACKAGES-008** scope: **controller is not affected**. The controller entry is bound to the constant `"mcu"` and
  compared with `"mcu"` (`RogAllyPlugin.Recovery.cs:26-28`, `AllyControllerService.cs:339`), so it never reaches
  `ReportOnly`. Only power and fans strand after a BIOS change. Severity medium stands.
- **PACKAGES-010** medium -> **low**. Real (`AllyRecoveryJournal.cs:21-28`), but Ally resume already runs
  `CheckHealthAsync` (`RogAllyPlugin.cs:243`), and the visible effect is one misreported command on an unwritable
  state directory. With A02_02 the first failed save latches the failure anyway (see PACKAGES-V-006).
- **PACKAGES-013** recommendation rejected. The drift is real, but "re-acquire every Ally service on resume as Claw
  does" names no defect: Ally power/fans are non-suspendable, stay Owned across suspend, and have no watchdog, so
  re-acquiring would only re-read, while recreating `AllyPowerCapability`/`AllyFanCapability` (`AllyServices.cs:66,189`)
  would throw away `_written` and `WrittenCpu`, which the reviewer then has to patch around. Under the simplify rule,
  keep the Ally resume filter. If stale values after a wake matter, the minimal change is one `RefreshAsync(null)`
  before `PublishStatesAsync` in Ally resume.
- **PACKAGES-014** medium -> **low**. The host already rejects out-of-descriptor values before dispatch
  (`DeviceCapabilityRouter.cs:456-482`), so the weaker Ally copy has no observable effect today. The dedupe is still
  worth doing. `PlainText` is already in the SDK, so moving `ValueMatches`/`CurveIsValid` is self-contained.
- **PACKAGES-017** detail: only the provider probe, `Get_WMI` and `Get_EC` go through the 3 s serialized WMI gate.
  The SMBIOS, PnP and `BatteryStatus` queries run on `Task.Run` with no timeout at all, bounded only by the caller
  token (`MsiWmiPlatform.cs:332-356,437-438`). With the 5 s host command deadline (`DeviceCapabilityRouter.cs:386`)
  this per-command burst also eats the budget that `ClawWriteBudget.Require` later checks, which feeds PACKAGES-004.
  Severity medium stands.
- **PACKAGES-018** medium -> **low**. The use-after-free needs the stop token to be cancelled already while the reader
  is inside the synchronous `TryDecode`. `StopAsync` disposes the stream first, so a reader parked in `ReadAsync` or
  `publish` exits without decoding. The window is narrow but would crash natively, so the recommended fix stays
  (reader frees its own descriptor; this removes state).
- **PACKAGES-019** low -> **nit**, recommendation rejected. The overlap is real but only follows an already abnormal
  2 s dispose timeout. "Retain an unfinished stream and refuse a second start" adds state and a guard with no
  observed defect, which the simplify rule forbids. Recommend no change.
- **PACKAGES-030** add a consumer: the host has a third copy of the persisted IDs
  (`src/WSGM/Core/DeviceConfiguration.cs:7-15`, `DeviceAuthoredProfileCapabilities.FanCurve`/`Lighting`). Any SDK
  constant move has to include it, or the move does not achieve what it is for.
- **PACKAGES-035** also applies to **Claw**. `ClawJournalledService.RestoreJournalledAsync` calls
  `ClawWriteBudget.Require` (`ClawServiceBase.cs:42`), which throws an `OperationCanceledException` the caller did not
  cancel. `DeviceServiceLifecycle.InvokeAsync` faults the service, and the Claw controller release records a budget
  refusal as `RestoreFailed` (`ClawControllerService.cs:397,412-417`).
- **PACKAGES-037** missing pins on the **Ally** side: `PluginTests.UnverifiedFanRestoreKeepsItsRecoveryEntry` and
  `PluginTests.AnUnverifiedRestoreIsNotRetriedButAnExplicitCommandRearmsIt` build their unverified restore from a
  readback mismatch (`IgnoreWritesTo`). Under PACKAGES-005 that restore becomes verified, so both tests change
  meaning and must be rebuilt on `FailWritesTo`. No fake simulates a transport timeout (OCE) at all, which is why
  PACKAGES-V-001 is untested.
- **PACKAGES-038**: the shipped package is already x64. `eng/pack-device.ps1:142` builds with `/p:PlatformTarget=x64`,
  so only test builds run AnyCPU. Still a nit.

---

## Confirmed (ids only)

PACKAGES-001, PACKAGES-003, PACKAGES-004, PACKAGES-005, PACKAGES-009, PACKAGES-011, PACKAGES-012, PACKAGES-015,
PACKAGES-016, PACKAGES-020, PACKAGES-021, PACKAGES-022, PACKAGES-023, PACKAGES-024, PACKAGES-025, PACKAGES-026,
PACKAGES-027, PACKAGES-028, PACKAGES-029, PACKAGES-031, PACKAGES-032, PACKAGES-033, PACKAGES-034, PACKAGES-036,
PACKAGES-039; plan claims C1-C8, C10-C21 (C9 except its Ally-poll clause).

Notes on confirmed items:
- PACKAGES-001: the `Block` branch is documented Claw design ("only a failed restore blocks", Claw AGENTS Exact
  device boundary), so the fix changes recorded guidance. See Batch problems.
- PACKAGES-005: the host impact is bigger than "reported unverified". An unverified stop blocks a restart of the
  device cycle until the machine sleeps (`DeviceCoordinator.cs:641-660`, comment at `AllyControllerService.cs:399-400`),
  so on a firmware whose mode settles late, toggling device integration off and on does not bring Ally control back.
- PACKAGES-011 sets the allocation-free rule against the HC-order rule. HC reads WinRT first
  (`AllyMotion.cs:26-28` cites it). Claw resolved the same conflict by reading the same default sensors through the
  legacy API. That precedent makes the change defensible, but it is a maintainer call and needs an X-environment
  manual row.
- PACKAGES-026: both in-repo hosts stop before they dispose (`DevicePluginRuntime.cs:95-121`,
  `PluginTestWorkflow.cs:418`), so "Dispose never writes hardware" is safe in-repo. It must become a documented
  SDK host obligation, because the SDK is MIT for outside hosts.

---

## Missed findings

### PACKAGES-V-001 (high) Claw treats every transport timeout as caller cancellation, so timed-out reads disable services and timed-out readbacks turn accepted writes into Indeterminate
- Where: `ClawCapabilities.cs:157,302,386,633,713,782`; `ClawServiceBase.cs:82` (`ClawObservation.TryAsync`);
  `MsiWmiPlatform.cs:458-470` (`TryGetAsync`) and `:400-409`; timeouts raised as OCE at `MsiWmiPlatform.cs:111-121`
  (3 s WMI) and `WindowsHidTransports.cs:253-261` (1 s MCU read).
- What: every "a read may fail without consequence" catch is written `when (ex is not ... OperationCanceledException)`.
  A WMI or MCU timeout is an OCE the caller never requested, so it escapes those catches:
  - `TryGetAsync` lets a `Get_WMI`/`Get_EC` timeout escape. The outer catch then nulls `WmiFirmwareIdentity`, so a
    single slow WMI answer at start or resume makes power, charge, fans and telemetry Passive for the whole cycle,
    and makes any command that hits it `FirmwareNotVerified`. The code comment at `:366-368` and Claw AGENTS ("`Get_WMI`
    and `Get_EC` ... may fail without consequence") say the opposite. WSGM starts at logon, when WMI is slowest.
  - At acquire time, a read timeout in `ClawObservation.TryAsync` (power/charge/fans) or in
    `ClawLightingCapability.ReadAsync` escapes and faults the service, instead of leaving the value unknown "until
    the first write". For lighting this means a Claw whose MCU does not answer the 0x04 read never gets lighting,
    although AGENTS says "Lighting is offered whenever the MCU collection is present". Every model except the
    reference A2VM was built blind from HC, and HC never reads the profile.
  - After a successful write, a readback timeout (`TryReadAsync`, `TryReadSnapshotAsync`, charge readback, lighting
    readback) escapes the handler. `ExecuteBoundCommandAsync` then reports `Indeterminate` +
    `RestoreFailed` for a write the transport accepted. That gates success on readback, which breaks the recorded
    rule and refactor-plan.md:111.
- Coverage: NEW. PACKAGES-023 sees only the mislabelling (Quiescing vs TransportFaulted), not the escapes.
- Recommendation: one rule, no new mechanism. A read catch rethrows an OCE only when the caller's token is cancelled
  (`when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)`). Apply it at the nine sites
  above plus `TryGetAsync`, and add a fake transport timeout to the Claw fakes. Fold into PACKAGES-B1.

### PACKAGES-V-002 (medium) A transient binding read makes Claw discard a valid crash-recovery entry
- Where: `MsiWmiPlatform.cs:385-391`, `ClawPlugin.Recovery.cs:35-55`, `ClawRecoveryJournal.cs:80-98`.
- What: the power/fan binding is built from that cycle's reads: `ec:<EC>` when `Get_EC` answered, otherwise
  `bios:<BIOS>`, plus `msi-acpi:<Get_WMI>` or `msi-acpi:unknown`. When `Get_EC` is refused once, the same firmware
  reads `bios:E1T52IMS.114;msi-acpi:8.0` instead of `ec:1T52EMS1.109;msi-acpi:8.0`. `Get_WMI` failing once has the
  same effect. Neither form is `ec:unknown;`, so `Decide` returns `Discard`, and the TDP/fan state WSGM left behind
  after a crash is never restored. That strands the user on WSGM-written limits. The same variance affects a
  command-time binding (`ClawPlugin.Commands.cs:105`) that is compared at the next start.
- Recommendation: bind power/fans to the SMBIOS BIOS version only, as Ally does (`AllyIdentity.cs:28`). The
  identity reader already has it, and the code's own comment says MSI ships EC updates inside BIOS packages
  (`MsiWmiPlatform.cs:383-384`). This removes `Get_WMI`/`Get_EC` from recovery entirely. Migration
  (requirements.md:17): an existing `ec:`-bound entry is still compared the old way once, then discarded or
  restored per the current rule. Needs a Claw AGENTS wording change.

### PACKAGES-V-003 (medium) Ally publishes the readback instead of the written value after an unverified write
- Where: `RogAllyPlugin.Surface.cs:162-166,184-208`; `AllyAcpiCapabilities.cs:78-86`; `AllyServices.cs:74-77,197-206`.
- What: the published value is `CurrentState(...) ?? _written[...]`, and `CurrentState` returns the firmware's read
  whenever it has one. `_written` is used only when the firmware is silent. After an unverified write with a
  different readback, such as a clamped limit, or the fan case the code itself expects ("DSTS may report the
  firmware's table for the mode rather than the curve now in force", `AllyAcpiCapabilities.cs:529-532`), WSGM
  shows the firmware's value. The slider snaps back and the fan editor shows a curve the user did not set. Ally
  AGENTS ("After an unverified write, publish the written value as `Observed` for the rest of the cycle") and
  refactor-plan.md:111 both require the written value. Claw does this right with `Observe()`
  (`ClawCapabilities.cs:98-105,441-460`).
- Recommendation: the written value wins for the cycle wherever the read differs, as in Claw's `Observe`: one rule in
  `CurrentState`, and `Effective()` reversed. Fold into PACKAGES-B3, with a test for the fan mismatch case.

### PACKAGES-V-004 (low) Ally faults power/fans when a command is cancelled or fails unexpectedly after the journal is armed
- Where: `RogAllyPlugin.Commands.cs:56-75`; `AllyAcpiCapabilities.cs:221,534` (catch only `IOException`/`Win32Exception`).
- What: an OCE from `_delay` during the 100 ms write spacing (command deadline reached after waiting behind the
  serializer), or any non-IO exception, leaves the handler. It is stamped `RollbackResult.RestoreFailed`, and when
  the service holds a pending original it is faulted until re-acquire. Ally power/fans are non-suspendable, so
  "re-acquire" means the next resume or restart. AutoTDP and the TDP row are dead until then, although no rollback
  ran. That contradicts Ally AGENTS ("Only a journalled original's failed rollback faults the service").
- Recommendation: `Indeterminate` with `RollbackResult.NotRequired`, and delete the fault branch together with
  PACKAGES-006/016 in B3.

### PACKAGES-V-005 (low) Ally caches a missing Aura collection for the whole cycle
- Where: `AllyHid.cs:382-401` (`_searched`), `AllyServices.cs:365-379`, resume filter `RogAllyPlugin.cs:249-252`.
- What: if the Aura collection is not enumerated when the cycle starts (typical straight after boot or wake),
  `Find()` stores `_searched = true` with `_aura = null`. Lighting goes Passive, and the resume re-acquire (Passive is
  not Owned, so it is re-acquired) gets the cached null back. Lighting stays unavailable until WSGM restarts the
  cycle. Only a write failure clears the flag, and no write is possible while Passive.
- Recommendation: cache only a found collection and drop the negative cache. This removes state.

### PACKAGES-V-006 (medium) A journal that fails mid-cycle misreports every power/fan command as Indeterminate, and A02_02 makes that permanent for the process
- Where: `ClawPlugin.Commands.cs:506-510` -> `DeviceRecoveryJournal.cs:109`; `AllyServices.cs:99,228` ->
  `RogAllyPlugin.Commands.cs:61-66`; batches/A02_02.md step 3 (failed save latches `FailureReason`).
- What: when the record becomes unavailable during a cycle, `BeginAsync` throws before any hardware access. Claw
  turns that into `Indeterminate` + `RestoreFailed` and Ally into `Indeterminate` + `RestoreFailed`. The service stays
  Owned, so every later command reports an uncertain write that never happened. Today the failure clears on the
  next successful save. After A02_02 it latches for the life of the journal object, so the misreporting lasts until
  stop. A02_02 claims "No subclass or schema/API adaptation required", which holds for compilation but not for
  command truthfulness.
- Recommendation: a journal refusal before the first write is `Rejected` (TransportFaulted). Whether the write then
  goes ahead without a restore point is a policy choice. The current rule says no journal means no mutation
  (SDK remarks: "blocked rather than mutate without a restore point"), so Rejected is the consistent answer. Fold into
  B1/B3 and schedule after A02_02.

---

## Batch problems

1. **B1 (Claw truthfulness)**: "pre-write read failures `Rejected`, `Indeterminate` only after the first setter"
   must be done at each read site, not by mapping handler exceptions in `ExecuteBoundCommandAsync`. Today a caller
   cancellation or readback timeout after an accepted write also leaves the handler (PACKAGES-V-001), and a blanket
   "handler threw -> Rejected" would report a written value as not written. Add V-001 and V-006 to B1. B1 also
   creates a Claw-local typed budget exception that B4 deletes again; either land B4's `DeviceWriteBudget` first or
   accept the churn knowingly.
2. **B2 (unified recovery)**: it adds public SDK API (`PendingOriginalFor`, `Reconcile`, `DeviceRecoveryAction`)
   and changes documented `BeginAsync` semantics (`reference.md:848` "refuses to overwrite an unresolved entry")
   while claiming the `DeviceApi` bump "waits for F02". Per SDK AGENTS the API integer governs loading, and contract
   changes need API-history/reference updates. Either B2 joins the F02 group or the policy stays package-internal
   until then. The unified rule ("RestoredUnverified/RestoreFailed -> keep, write nothing") must also exempt
   controller entries explicitly. A Claw controller entry re-reads the mode first (allowed by "re-read state"), so
   it must keep restoring at every start whatever its status. Otherwise, with controller management off, the pad
   stays in DirectInput after a failed release, which strands the user. The Ally tables cannot be read back, so the
   maintainer has to decide whether enabling controller management is the user action that re-arms them.
3. **B2/B3/B6 vs B7 ordering**: B2 changes Claw AGENTS behaviour ("only a failed restore blocks"), B3 changes Ally
   AGENTS behaviour (rollback faults, readback-verified restore), and B6 changes "every command must revalidate live
   identity" (Q2). The plan requires separate human approval of guidance changes (line 175), but B7, which carries
   the AGENTS proposals, runs last. The proposals must be approved before the batches that contradict the current
   text.
4. **B3 (Ally)**: legacy-first motion (PACKAGES-011) reverses HC's documented order. It is a maintainer decision and
   cannot be validated with fakes, so mark it for the X tester row. B3's test list leaves out the two pinned Ally
   tests named under PACKAGES-037 and should take V-003 and V-004. "Restore writes mode then limits
   unconditionally" still needs the current-limits read to order SPL <= SPPT <= FPPT (Q1). That read is ordering, not
   gating, and should be stated as such.
5. **B4**: add the host copy of capability IDs (`Core/DeviceConfiguration.cs`) to its consumer list. Its additive
   SDK helpers have the same API-level problem as B2 if they land outside F02.
6. **B5**: "DisposeAsync handles only" needs the matching SDK contract line (the host must Stop before Dispose)
   in `reference.md` and the plugin-lifecycle docs in the same batch. Both in-repo hosts already comply.
7. **B6**: drop "bounded motion stop with retention" (PACKAGES-019, mechanism without a defect). The identity
   snapshot change needs the Q2 AGENTS sign-off first. With V-002 adopted, the per-command `Get_WMI`/`Get_EC` reads
   leave the command path entirely, which simplifies PACKAGES-017's fix.
8. **Severity-driven order**: V-001 (readback gating success, lighting lost on blind models) and V-002 (crash restore
   discarded) are more important than most of B4/B5 and belong in the first Claw batch, not after F02.
