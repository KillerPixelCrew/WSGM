# Device packages findings

Scope: the MSI Claw package (`src/WSGM.Device.Msi.Claw`), the ASUS ROG Ally package (`src/WSGM.Device.Asus.RogAlly`), the Handheld Companion scaffold (`src/WSGM.Device.HandheldCompanion`), their three test projects, and the SDK pieces they lean on (`DeviceRecoveryJournal`, `CommandResults`, the host's `DeviceCapabilityValidation`). Sources: `_plan/refactor-2.1/review/packages.md`, its verification `packages.verify.md` (corrections and the six missed findings PACKAGES-V-001 to V-006 are applied here), the critic's conflict 18 and 19, and plan v2.

38 findings need work: 0 critical, 2 high, 14 medium, 19 low, 3 nit. Eight more are no-change (listed at the end). The solution checker (2026-10-03) added PACKAGES-C-001, moved PACKAGES-012 to no-change, and corrected PACKAGES-001, -004, -005, -015, -017, -030, -033, -V-002, -V-003 and -V-006. The maintainer's decisions (`DECISIONS.md`, 2026-10-03) are applied: D7 (WinRT motion first, like HC) moves PACKAGES-011 to no-change; D8 (HC's SPL, then SPPT and FPPT order, no SPL <= SPPT <= FPPT stepping) reshapes PACKAGES-005, -007 and -V-003; D9 (no readback machinery for any vendor except the Claw) removes every Ally unresolved entry, `Keep` outcome and re-arm rule from PACKAGES-001, -005, -009, -036 and -037; D4 approves the guidance diffs. Plan v2 batches: B021 (Claw command truthfulness), B022 (Ally write-through and the D8 write order), B023 (Ally fan rollback), B142 (Device API 12 contract), B143 (one recovery policy), B144 (shared SDK helpers), B145 (Claw transports and identity), and B177 for the package docs (recovery rule, no command rollback, watchdog disarm, Ally write-through restore, Ally write order SPL then SPPT and FPPT, Ally recovery without unresolved entries).

Anchor every edit by symbol. Line numbers below are from master `1329813f` and are approximate.

Gaps found while consolidating, folded into the solutions below: the Ally half of PACKAGES-V-006 and the Ally lighting part of PACKAGES-016 are in no batch spec (they go into B022, which edits the same files); the Claw half of PACKAGES-020 is in no batch spec (it goes into B145); PACKAGES-031 and PACKAGES-037 are split across several batches.

## High

### PACKAGES-001: Claw replays an uncertain restore at every start, and its Block branch strands services

- **Severity:** high
- **Where:** `src/WSGM.Device.Msi.Claw/ClawRecoveryJournal.cs` (`Decide` ~68-99, `CompleteCommandAsync` ~35-66); `ClawPlugin.Recovery.cs` (`ReconcileOutstandingAsync` ~15-118, `BlockService`); `ClawServiceBase.cs` (`RestoreJournalledAsync` ~13-66); `ClawPlugin.Commands.cs` (`JournalCommandAsync` ~479-541); `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`BeginAsync` ~95-130). Pinned by `tests/WSGM.Device.Msi.Claw.Tests/ClawPluginTests.cs` (~757-811) and `ClawModelLifecycleTests.cs` (~286-294).
- **Problem:** `Decide` returns `Restore` for a matching firmware binding before it looks at the entry's status, so a power or fan restore that failed or was unverified in the previous cycle is written again at every start without any user action. That is an automatic retry of an uncertain write. The other branch, `RestoreFailed` plus a changed EC, returns `Block`; `ReconcileOutstandingAsync` turns it into a `ReconciliationBlockReason`, so the service faults on every start and no command can reach a faulted service. The code comment above the `Discard` branch claims the opposite. A restore that fails during reconciliation also blocks the service for the cycle. Finally, SDK `BeginAsync` throws for an entry in `RestoredUnverified` or `RestoreFailed`, so any explicit command on that service is reported `Indeterminate`: the Claw has no re-arm path.
- **Best solution:** one recovery policy owned by the SDK journal and used by both packages, inside unreleased Device API 12:
  1. `DeviceRecoveryJournal<TState>.BeginAsync` never throws for an existing entry (it still throws when the record is unavailable or the save fails). If the existing entry's `FirmwareIdentity` differs from the one passed, it is replaced by the new Pending entry with the fresh original and `Opened = true` (this is PACKAGES-036). If the binding matches and the status is `RestoredUnverified` or `RestoreFailed`, the status goes back to `Pending` (one save), the first original is kept, and `Opened = false`; only the Claw ever records those two statuses (D9), so this re-arm is the Claw's alone. A Pending entry with a matching binding is returned unchanged with `Opened = false`, as today. Update the `<exception>` doc and `reference.md` ("refuses to overwrite an unresolved entry" goes; add that the unverified and failed statuses are for packages that can read their state back).
  2. New public `TState? PendingOriginalFor(string serviceId)`, moved from `AllyRecoveryJournal`: the original only for a Pending entry. The power and fan release paths of both packages (`ClawJournalledService.RestoreJournalledAsync`, Ally `PowerService`/`FanService.ReleaseAsync`) use it instead of `HasUnrestoredMutation`/`OriginalStateFor`, so release restores only Pending entries. The controller releases keep their in-memory original and are unchanged (step 5).
  3. New public static `DeviceRecoveryAction Reconcile(DeviceRecoveryEntry<TState> entry, string? currentFirmware)` with `enum DeviceRecoveryAction { Restore, Wait, Keep, Discard }`, decided in this order: `currentFirmware` null gives `Wait` (write nothing, leave the entry Pending); a different binding gives `Discard` (trace, remove); status `RestoredUnverified` or `RestoreFailed` gives `Keep` (trace, write nothing, service stays usable); otherwise `Restore`, once. It replaces `ClawRecoveryJournal.Decide`, `ClawReconciliationAction`, `AllyRecoveryJournal.Decide` and `AllyReconciliationAction`, so the net effect is fewer types. `Keep` cannot arise for an Ally entry, because the Ally never records those statuses; entries left by pre-2.0 builds are not migrated (2.0 is unreleased).
  4. Both packages' `ReconcileOutstandingAsync`: power and fans follow `Reconcile`. No reconciliation outcome blocks a service. Claw: a `Restore` that throws records `RestoreFailed`, one that completes records `RestoredVerified` (the Claw restore returns true once its writes went through); delete the `Block`/`ReportOnly` branches and the `BlockService` calls on the restore path. Ally (D9: a write either dispatched or failed to dispatch): a `Restore` that completes removes the entry (`SetStatusAsync(RestoredVerified)`, the journal's removal call); one that throws failed to dispatch, so it is traced and the entry stays Pending with no status write, and the next release or start writes that original once more. Delete the `ReportOnly` branch (PACKAGES-008), the `if (!restored) Block(...)` after a failed restore and the `RestoreFailed` status write, and widen the restore catch from `IOException or Win32Exception or InvalidOperationException` to `when (ex is not OutOfMemoryException)` so a timeout is traced instead of failing the start. `BlockService`/`Block` stay only for the journal-unavailable case at start.
  5. Controller entries. Claw: exempt from `Keep`: `RestoreControllerJournalEntryAsync` re-reads the mode before it writes (re-reading state is allowed on the Claw), so it runs at every start whatever the status. Otherwise, with controller management off, a pad left in DirectInput after a failed release would stay stranded. Ally (D9): no unresolved entry and no re-arm rule. The controller entry reconciles like power and fans: a start restore that writes every factory table removes it, one that throws or has a table refused leaves it Pending. `AllyControllerService.ConfigureAsync` calls `BeginAsync` in place of `ArmAsync` (PACKAGES-036), which keeps an existing Pending entry. `RestoreConfigurationAsync` stops recording `RestoreFailed`/`RestoredUnverified` when tables are refused: it leaves the entry Pending, returns its `TransportFaulted` reason as today, and removes the entry only when every table was acknowledged. Delete the Ally AGENTS fan rule "Keep an unverified restore in the recovery record ... the next explicit command ... re-arms" (B177).
  6. Delete `ClawRecoveryJournal.CompleteCommandAsync`. `JournalCommandAsync` keeps the one rule that survives: when this command opened the entry and the result is `Rejected`, `SetStatusAsync(RestoredVerified)` removes it.
  7. Invert the two pinned Claw tests; delete the two Ally re-arm tests (PACKAGES-037).

  A shared static rule beats fixing the Claw copy alone because the two `Decide` copies have already drifted (PACKAGES-008), and the shared rule deletes two enums.
- **Tests:** SDK `DeviceRecoveryJournalTests`: `BeginAsync` on a `RestoreFailed` or `RestoredUnverified` entry returns it Pending with the first original and `Opened = false`; with a different binding it replaces the entry; a truth table for `Reconcile`. Claw: a `RestoreFailed` power entry with the same binding writes nothing at the next start, the power service is Owned and accepts a command, that command re-arms the entry, and stop writes the first original; a `RestoreFailed` entry with a changed binding is discarded; a controller entry restores at start whatever its status. Ally: a power restore that throws at start leaves the entry Pending with no status write and power Owned, and the next stop writes that original; a release whose factory tables are partly refused leaves the controller entry Pending and records no status, and the next start writes the tables once; no Ally code path writes `RestoredUnverified` or `RestoreFailed`. Filters: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~DeviceRecoveryJournalTests"`, `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawModelLifecycleTests|FullyQualifiedName~ClawPluginTests"` and `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B143 (after B142, inside Device API 12). Decided: D4 approved (Claw AGENTS: "only a failed restore blocks" becomes "unresolved entries are kept and never written automatically; an explicit command re-arms"; Ally AGENTS loses its unverified-restore and re-arm rule) and D9 (no readback machinery for any vendor except the Claw).
- **Related:** PACKAGES-C-001 (resume-time health latch, same batch), A02-F010 (Codex ledger; Codex units U15A-DVP and U15B-DVP), SDK-016 (each service decides its reconciliation block), SDK-017 (refuted; do not reintroduce the `RestoredUnverified` reinterpretation), B010 (A02_02 trimmed, lands first in the same file). Depends with PACKAGES-008, -009, -036, -037, -V-002.

### PACKAGES-V-001: Claw treats every transport timeout as caller cancellation

- **Severity:** high
- **Where:** `src/WSGM.Device.Msi.Claw/ClawCapabilities.cs` read catches (~157 `ReadFastAsync`, ~302 `TryReadAsync`, ~386 charge readback, ~633 fan `TryReadSnapshotAsync`, ~713 and ~782 lighting read and readback); `ClawServiceBase.cs` (`ClawObservation.TryAsync` ~82); `ClawPlugin.Commands.cs` (`JournalCommandAsync` original-state read ~498); `MsiWmiPlatform.cs` (`WindowsClawIdentityReader.TryGetAsync` ~458-470, outer catch ~400-409). Timeouts are raised as `OperationCanceledException` at `MsiWmiPlatform.cs` ~111-121 (3 s WMI) and `WindowsHidTransports.cs` ~253-261 (1 s MCU read).
- **Problem:** every "a read may fail without consequence" catch is written `when (ex is not ... OperationCanceledException)`. A WMI or MCU timeout is an OCE the caller never requested, so it escapes. Consequences: a single slow `Get_WMI`/`Get_EC` at start or resume escapes `TryGetAsync`, the outer catch nulls `WmiFirmwareIdentity`, and power, charge, fans and telemetry go Passive for the whole cycle (WSGM starts at logon, when WMI is slowest). An acquire-time read timeout faults power, charge, fans or lighting instead of leaving the value unknown; a Claw whose MCU does not answer the lighting profile read never gets lighting, although every model but the A2VM was built blind and HC never reads the profile. After an accepted write, a readback timeout escapes the handler and the command is reported `Indeterminate` with `RestoreFailed`, which gates success on readback.
- **Best solution:** one filter rule, no new mechanism. Each listed catch becomes `catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)`, using the method's own token. `TryGetAsync` keeps its positive type list and adds `or OperationCanceledException` when the token is not cancelled, so a `Get_WMI`/`Get_EC` timeout returns null ("recorded as unknown") and never reaches the outer catch. A real caller cancellation still propagates everywhere. Add a timeout mode to the Claw fakes: the fake WMI and MCU transports throw `new OperationCanceledException()` with the caller token not cancelled.
- **Tests:** a `Get_EC` timeout at start keeps power and fans Owned; a power read timeout at acquire leaves `LastObserved` null and the service Owned; a lighting read timeout at acquire keeps lighting offered; a power write followed by a readback timeout returns `AppliedUnverified` and publishes the written pair; a caller-cancelled read still propagates. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawCapabilitiesTests|FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B021.
- **Related:** PACKAGES-023 (the mislabel of the same OCE), PACKAGES-004 (the original-state read in `JournalCommandAsync`), PACKAGES-037 (no fake simulated a timeout).

## Medium

### PACKAGES-002: Claw TDP watchdog stays armed after an uncertain write

- **Severity:** medium (verifier lowered from high: what gets rewritten is the previously accepted pair, rate-limited, as HC's own watchdog does; it is a plan-rule violation, not a high-impact defect)
- **Where:** `src/WSGM.Device.Msi.Claw/ClawCapabilities.cs`, `ClawPowerCapability.ReassertAsync` (~112-128), `ApplyPairCoreAsync` (~256-277), fields `_target`, `_lastReassert`, `ReassertInterval`.
- **Problem:** `_target` is set only after a successful write, so a failed pair write leaves the previous pair armed and the watchdog keeps rewriting that stale pair whenever the EC reads otherwise. A failed watchdog write leaves `_target` armed, and the next observation pass writes again, indefinitely: an automatic retry of an uncertain write. `_lastReassert` starts at `default`, so the post-command refresh can reassert at once.
- **Best solution:** in the `ApplyPairCoreAsync` catch, set `_target = null` before returning `ClawApplied.Failed`. In `ReassertAsync`, wrap `WritePairAsync` in `try { ... } catch { _target = null; throw; }` so the observation pass still records and traces the failure. `_targetScenario` stays as is (it only shapes what `Observe` publishes). Only a later successful explicit command re-arms (`ApplyPairCoreAsync` sets `_target` after its write, unchanged); `RestoreAsync` clears it as today. Once PACKAGES-003 makes the 10 s periodic pass the only caller of the watchdog (`DeviceCommandSerializer.ObservationInterval`), the 5 s spacing can never bind: delete `_lastReassert` and `ReassertInterval`, which also removes the watchdog's wall-clock read.
- **Tests:** a fake failing the second `Set_Data` leaves the capability disarmed, and a later pass with a differing EC writes nothing; a failed reassert disarms; a new successful command re-arms and the next pass reasserts once when the EC differs. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawCapabilitiesTests"`.
- **Plan v2:** B021.
- **Related:** PACKAGES-003 (moves the watchdog to the periodic pass), PACKAGES-024 (wall clock, otherwise no-change).

### PACKAGES-003: Claw pre-dispatch "revalidation" can write hardware and then report Rejected

- **Severity:** medium
- **Where:** `src/WSGM.Device.Msi.Claw/ClawPlugin.Commands.cs` (`ExecuteBoundCommandAsync`, the `RefreshObservedAsync` call ~43-48 and its catch ~50-63); `ClawPlugin.Observation.cs` (`RefreshObservedAsync`, `RefreshAllObservedAsync`); `ClawServices.cs` (`PowerService.RefreshAsync` ~154-159).
- **Problem:** every command on an owned WMI service first calls `RefreshObservedAsync`. For power that runs `ReassertAsync`, which can write the previous pair (two WMI writes 200 ms apart) before the new command. A failure there is returned as `Rejected` "Current-state revalidation failed", which tells the host nothing was written. The refresh is also redundant: each handler reads what it needs. Ally has no such step.
- **Best solution:** delete the `if (service.State is Owned && ...) await RefreshObservedAsync(...)` block from `ExecuteBoundCommandAsync`; the identity read and model check stay (B145 changes them). Split `PowerService.RefreshAsync` into a read-only `RefreshAsync` (read, then `Observe`), used by the post-command refresh, and `WatchdogAsync` (read, `ReassertAsync`, `Observe`), called only from `RefreshAllObservedAsync` in place of `RefreshAsync`. The remaining catch around the identity read keeps returning `Rejected`, which is now true.
- **Tests:** a command on owned power issues no `Set_*` before its own writes (fake records call order); the post-command refresh with a differing EC writes nothing; the periodic pass reasserts. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawPluginTests|FullyQualifiedName~ClawCapabilitiesTests"`.
- **Plan v2:** B021.
- **Related:** PACKAGES-002, PACKAGES-013 (drift between the shells, no-change).

### PACKAGES-004: Claw reports pre-write failures as Indeterminate with a fabricated RestoreFailed

- **Severity:** medium
- **Where:** `src/WSGM.Device.Msi.Claw/ClawHardware.cs` (`ClawWriteBudget.Require` throws `OperationCanceledException`, ~229-250); `ClawPlugin.Commands.cs` (`ExecuteBoundCommandAsync` catches ~76-91, `JournalCommandAsync` ~479-541, `Indeterminate` helper ~545-552); `ClawCapabilities.cs` pre-write reads (`ApplyScenarioAsync` scenario read ~190, fan snapshot reads ~497 and ~534).
- **Problem:** a short deadline, a failed pre-write read (scenario byte, fan snapshot), or a refused journal `BeginAsync` all land in the generic catch and return `Indeterminate` with `RollbackResult.RestoreFailed`, although nothing was dispatched. The second `Require` runs after `BeginAsync` opened an entry, so a refusal there leaves a Pending entry for an unchanged device and the release later writes that original back: a spurious write.
- **Best solution:** each site decides; no blanket "handler threw means Rejected" mapping, because after an accepted write a caller cancellation can still leave the handler (verifier batch problem 1).
  - `JournalCommandAsync`: replace both `Require` calls with one `ClawWriteBudget.IsAvailable(command.Deadline)` check placed after the original-state read and before either write path: the read's catch only records `originalState = null` (traced as today), then the budget check runs, then a null original goes to `apply` without a restore point (today's HC-style fallback, which currently bypasses every budget check) and a captured one goes to `BeginAsync`. On refusal return `CommandResults.Rejected(command, Quiescing, "Not enough time left to write safely; nothing was written.")`, retryable. Nothing has been journalled at that point.
  - Where a budget check sits below the handler boundary (lighting rate path, controller mode switch), `Require` throws a Claw-local `ClawWriteBudgetException : Exception` (no longer an OCE) that `ExecuteBoundCommandAsync` maps to `Rejected` `Quiescing`. B144 replaces it with the SDK type (accepted churn).
  - Pre-write reads in handlers (`ApplyScenarioAsync`'s `ReadScenarioAsync`, the fan handlers' snapshot reads before the first `Set_*`) are wrapped and return `Rejected` `TransportFaulted` ("could not read the current state; nothing was written"), or `Quiescing` when the caller token is cancelled.
  - `Indeterminate` (the helper and both post-admission catches) stamps `RollbackResult.NotRequired`. It then means only "a setter was called and its outcome is unknown".
- **Tests:** a short deadline returns `Rejected` and leaves no journal entry; a scenario read failure returns `Rejected` with no `Set_*`; a budget refusal before `BeginAsync` leaves no Pending entry, so stop writes nothing; a handler exception after a setter is `Indeterminate` with `NotRequired`. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawPluginTests|FullyQualifiedName~ClawCapabilitiesTests"`.
- **Plan v2:** B021.
- **Related:** PACKAGES-016, -027 (SDK budget helper in B144), -V-001, -V-006; plan line 111 phase contract.

### PACKAGES-005: Ally restore gates writes and success on readback

- **Severity:** medium
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs`: `AllyPowerCapability.RestoreAsync` (~151-172), `RestoreModeAsync` (~254-273), `AllyFanCapability.RestoreAsync` (~488-500). Consumers: `AllyServices.cs` release paths (~119-143, ~246-273), `RogAllyPlugin.Recovery.cs` (`ReconcileOutstandingAsync`).
- **Problem:** if the performance mode does not read back within 150 ms, the captured limits are not restored at all; otherwise success requires SPL/SPPT/FPPT (or curve) readback equality. A firmware that clamps or reports late leaves `RestoredUnverified` or `RestoreFailed`, keeps the entry, faults the service at the next start, and reports the stop Unverified, so a restart of the device cycle is blocked until the machine sleeps (`DeviceCoordinator`). Toggling device integration off and on then does not bring Ally control back. This breaks the no-readback rule and contradicts the Claw, whose restore is complete once its writes went through.
- **Best solution:** mirror HC (D8, D9): a restore is a sequence of writes that either dispatched or failed to dispatch, with no read before or after. `AllyPowerCapability.RestoreAsync` becomes `ValueTask` (it completes or throws, so the bool goes): when `original.Mode` is set, write it unconditionally and wait `ModeSettle`; then write SPL, SPPT and FPPT in HC's order with the existing `WriteSpacing`, through the same fixed-order writer PACKAGES-007 leaves (no `Read()` for ordering, no readback comparison). Delete `RestoreModeAsync` and its `RollbackResult` (the mode write is inlined) and the `!original.LimitsReadable` early return: `ValidateEntry` already guarantees valid watts for every journalled entry. `AllyFanCapability.RestoreAsync` likewise becomes `ValueTask`: write the channels, no readback (its `!original.Readable` return stays as a guard, since `ValidateEntry` guarantees it too). Release paths in `PowerService`/`FanService.ReleaseAsync`: a completed restore removes the entry and gives `Idle`; the `RestoredUnverified` branch and its `ReleasedUnverified` "did not read back" reason go. Their catches widen from `IOException or Win32Exception` to `when (ex is not OutOfMemoryException)`; on any failure (an `OperationCanceledException` from `ModeSettle` or the spacing included) the entry stays Pending with no status write, the failure is traced, and the service reports `Faulted` with the `TransportFaulted` reason as today. The Pending original is the restore point the next start writes once (PACKAGES-001 step 4); under D9 nothing about it is uncertain, so nothing records `RestoreFailed`.
- **Tests:** a restore writes the mode, then SPL, SPPT and FPPT in that order and issues no ATKACPI status read; a restore against a fake whose readback would differ still removes the entry and stop is `Idle`, not `ReleasedUnverified`; a release cancelled during the write spacing leaves the entry Pending with no status write, and the next start writes the original once; the fan restore writes the channels with no readback. Rebuild or delete the readback-based Ally tests per PACKAGES-037. Filter: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~AcpiCapabilityTests|FullyQualifiedName~PluginTests"`.
- **Plan v2:** B022. Decided: D8 (mirror HC: SPL, then SPPT and FPPT; the stepping rule goes, folded into B022 because it edits the same writer) and D9 (no readback machinery outside the Claw).
- **Related:** SDK-V-002 (same defect, resolved by B022), SDK-017 (refuted), DEVICE-001 (B008), PACKAGES-007, -V-003.

### PACKAGES-007: Ally skips requested writes when a readback already matches

- **Severity:** medium (verifier raised from low)
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs`: `WriteOrderedAsync` (the `Current(current, id) == watts` skip ~239-242), `ApplyScenarioAsync` (`before.Mode != target` ~127), `RestoreModeAsync` (~258), `ApplyLimitsAsync` passing `Effective()` (~105).
- **Problem:** a stale or clamped readback suppresses a requested write. Worse, `ApplyLimitsAsync` passes `Effective()` as the current state, which fills a silent firmware's limits from `_written`. On the Xbox Ally X, which reports 0 W (unknown), re-sending the value last written in the cycle (profile re-apply, AutoTDP settling on the same value) is skipped entirely, yet `_written` is updated and the host is told Observed. After a firmware-side reset there is no way to put the limit back without changing it first. HC writes SPL, then SPPT and FPPT, unconditionally.
- **Best solution:** write as HC does (D8: `SetLongPowerLimit` writes SPL, then `SetShortPowerLimit` writes SPPT and FPPT, `AsusACPI.cs` ~343-352). Replace `WriteOrderedAsync` with `WriteLimitsAsync(int sustained, int slow, int fast, CancellationToken)`, which writes SPL, then SPPT, then FPPT unconditionally with the existing `WriteSpacing` between writes. Delete `WriteOrder`, `Current(...)` and the skip. `ApplyLimitsAsync` (private overload) drops its `AllyPowerState before` parameter, and the public overload stops calling `Effective()` for it. Delete the `var before = Read()` and the `before.Mode != target` condition in `ApplyScenarioAsync` so the mode is always written (the `_written` limit reset and `ModeSettle` wait stay); `RestoreModeAsync` is gone after PACKAGES-005. Docs (B177): the Ally AGENTS power line "SPL <= SPPT <= FPPT after every write" becomes "SPL, then SPPT and FPPT, as HC writes them"; README ~54-55 and the PROVENANCE row "Write order keeping SPL <= SPPT <= FPPT" change to cite HC `AsusACPI.cs` ~343-352.
- **Tests:** re-sending the last written limits writes all three; every limit write goes SPL, SPPT, FPPT whatever the current limits (including lowering SPL below the current SPPT and raising FPPT above it); a mode equal to the readback is still written. Delete the `WriteOrder` cases in `AcpiCapabilityTests`. Filter: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~AcpiCapabilityTests"`.
- **Plan v2:** B022. Decided: D8 (mirror HC order, stepping rule removed, writes stay write-through with no gating).
- **Related:** PACKAGES-005, -V-003.

### PACKAGES-008: Ally strands power and fans after a BIOS update

- **Severity:** medium (verifier: the controller is not affected; its entry is bound to the constant `"mcu"`)
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyRecoveryJournal.cs` (`Decide` ~58-76), `RogAllyPlugin.Recovery.cs` (`ReportOnly` branch ~46-51), `AllyServices.cs` (`AcquireAsync` block ~49-52, ~172-175).
- **Problem:** any binding mismatch returns `ReportOnly`, which sets `ReconciliationBlockReason`, so power and fans fault on every start for as long as the entry exists, and nothing can clear it (a faulted service refuses commands). The Claw discards in the same case for exactly this reason.
- **Best solution:** Ally `ReconcileOutstandingAsync` uses the SDK `Reconcile` from PACKAGES-001: a changed BIOS binding gives `Discard`, traced and removed. Delete the `ReportOnly` branch, `AllyRecoveryJournal.Decide` (with its `Block` on unverified or failed entries, which D9 removes) and `AllyReconciliationAction`. The `Block` helper stays only for the journal-failure case at start.
- **Tests:** a power entry bound to an older BIOS is discarded at start, power is Owned, and nothing is written. Filter: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B143. Decided: D4 approved, D9 (no readback machinery outside the Claw).
- **Related:** PACKAGES-001, -036, A02-F010.

### PACKAGES-009: A restore that was never attempted is recorded as failed

- **Severity:** medium
- **Where:** `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Recovery.cs` (`RestoreEntryAsync` `default` branch ~76-103, status write ~61-72); Claw analogue `src/WSGM.Device.Msi.Claw/ClawPlugin.Recovery.cs` (`RestoreControllerJournalEntryAsync` returning false when the pad is absent ~121-135, `_ => false` arm ~67-117).
- **Problem:** Ally `RestoreEntryAsync` returns false when ATKACPI or the vendor collection is not available yet, and the caller writes `RestoreFailed`, which from then on prevents every automatic restore although nothing uncertain happened. The Claw writes `RestoredUnverified` when the controller is not present yet.
- **Best solution:** availability is decided before `Reconcile`, by passing `currentFirmware = null` when the transport is not reachable, which yields `Wait`: no status write, the entry stays Pending, no block. Ally: power and fans pass `identity.FirmwareIdentity` only when `_hardware.Acpi.TryOpen()` succeeds; the controller passes `AllyServiceIds.McuFirmware` only when `_vendor` is available. Claw: the controller entry's `DiscoverAsync` runs first, and a missing pad or physical location is `Wait`. `RestoreEntryAsync` then handles only attempted writes; delete its `default: return false` and the Claw `_ => false` arm (entries are validated by `ValidateEntry` at load). A Pending entry left by `Wait` stays the restore point: a later command's `BeginAsync` keeps that first original, and the release writes it. On the Ally, D9 already removes every failure status (PACKAGES-001 step 4), so an Ally restore that is attempted and fails leaves the entry Pending as well; `Wait` only saves the pointless attempt.
- **Tests:** Ally start with ATKACPI unavailable leaves the power entry Pending with no status write and no ATKACPI write; Claw start with the pad absent leaves the controller entry Pending. Filters: Ally `--filter "FullyQualifiedName~PluginTests"`, Claw `--filter "FullyQualifiedName~ClawModelLifecycleTests|FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B143.
- **Related:** PACKAGES-001.

### PACKAGES-015: SDK `CommandResults.Unverified(command, written)` sets ReadbackValue, both packages undo it

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Capabilities/CommandResults.cs` (`Unverified(CapabilityCommand, CapabilityValue)` ~29-39); `src/WSGM.Device.Msi.Claw/ClawPlugin.Commands.cs` (`NormalizeCommandResult` ~454-477), `ClawCapabilities.cs` (`ClawApplied.Result`); `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Commands.cs` (strip ~85-87); `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs` (~150); SDK `docs/reference.md` (~890).
- **Problem:** the helper puts the written value in `ReadbackValue`, against the SDK reference, and both packages strip it again. The value overload invites the same mistake in every package.
- **Best solution:** delete the `Unverified(CapabilityCommand, CapabilityValue)` overload. An unverified result carries no readback; the written value reaches the host through the package's published state (`Observe`, `_written`). `ClawApplied.Result` and `IntelGpuPlugin` switch to `Unverified(command, detail)`. Delete `NormalizeCommandResult` and the Ally strip; its two other checks (a result for another command id, `AppliedVerified` without a readback) are unreachable because every Claw handler builds its result with `CommandResults` from the command it was given, and `Verified` takes a non-null readback. `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs` is not in B144's file list; add it there (the GPU plugin already publishes the written value itself, ~144-147, so only the result line changes). Host consumers read `ReadbackValue` only on `AppliedVerified` (`CommandOutcomeExtensions`) or for a trace (`AutoTdpService` ~938), so no host change is needed. Deleting beats keeping a parameter the method ignores.
- **Tests:** SDK test that an unverified result has a null `ReadbackValue`; Claw and Ally suites unchanged in behaviour. Filters: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Capabilities"`, then both package projects.
- **Plan v2:** B144 (the `reference.md` text is fixed in B142 by SDK-027/042).
- **Related:** SDK-027, SDK-042, gpuir domain (Intel GPU consumer).

### PACKAGES-017: Claw identity revalidation per command is a burst of WMI work that rebinds MSI_ACPI

- **Severity:** medium
- **Where:** `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs` (`IsProviderAvailableAsync` ~32-47, `WindowsClawIdentityReader.ReadAsync` ~330-456, AC read via `BatteryStatus` ~559); called per command (`ClawPlugin.Commands.cs` ~33), per resume and per controller enable.
- **Problem:** before every slider step: three SMBIOS queries, a PnP query, a provider probe that disposes and rebinds the WMI instance, `Get_WMI`, `Get_EC` and a battery query. Only the probe and the two getters go through the 3 s WMI gate; the rest run on `Task.Run` bounded only by the caller token. With the 5 s host command deadline this burst eats the budget the write later checks (feeds PACKAGES-004). SMBIOS cannot change at runtime.
- **Best solution:** `_cycleIdentity` already exists and is already read at `StartAsync` (~134) and at resume (~279); commands stop calling `_services.Identity.ReadAsync` and use `_cycleIdentity with { OnAcPower = ... }`. The AC line is read per command by `GetSystemPowerStatus` as `AllyIdentity` does, replacing the WMI `BatteryStatus` query, and stays injectable through the reader's existing `readOnAcPower` seam (expose it as a separate `ReadOnAcPower()` on the identity reader) so the Claw tests keep their fake. The per-command model-change check goes (the model cannot change mid-cycle; resume re-reads it). The re-read when controller management is enabled (~365) stays: it is rare, and the MCU revision can change while management is off (MSI's updater). In `IsProviderAvailableAsync`, delete the leading `InvalidateProvider()`: `AcquireProvider` already returns the bound instance, and the failure paths in `RunSerializedAsync` still invalidate. With PACKAGES-V-002 adopted, `Get_WMI`/`Get_EC` leave the command path entirely.
- **Tests:** a command performs no SMBIOS, PnP or `Get_*` identity reads (count on the fake reader); resume re-reads once; the AC check still refuses an AC-only value on battery. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B145. Decided: D4 approved (Claw AGENTS "every command must revalidate live identity" becomes "identity snapshot per start and resume; AC per command").
- **Related:** PACKAGES-004, -V-002.

### PACKAGES-037: Tests pin defects and lack the lifecycle edges that matter

- **Severity:** medium
- **Where:** Claw `tests/WSGM.Device.Msi.Claw.Tests/ClawPluginTests.cs` (~757-811, automatic retry) and `ClawModelLifecycleTests.cs` (~286-294, stranding Block); Ally `tests/WSGM.Device.Asus.RogAlly.Tests/PluginTests.cs` `UnverifiedFanRestoreKeepsItsRecoveryEntry` and `AnUnverifiedRestoreIsNotRetriedButAnExplicitCommandRearmsIt`; fakes in both `Fakes` folders.
- **Problem:** two Claw tests pin the retry and the stranding Block. The two Ally tests pin the unresolved-entry and re-arm machinery that D9 removes from the Ally, and they build their unverified restore from a readback mismatch (`IgnoreWritesTo`), which PACKAGES-005 stops reading. No fake simulates a transport timeout. Missing edges: watchdog disarm, pre-dispatch classification, undeclared scenario publication, Ally firmware-change discard, not-attempted restore, release budget, descriptor freed while decoding, Ally restore without readback.
- **Best solution:** each missing test lands with the batch that fixes its defect (listed in each finding's Tests line). The two Claw pinned tests are inverted in B143: no rewrite at the next start, the service is usable, an explicit command re-arms. The two Ally tests are deleted in B022 (D9: there is no unresolved Ally entry left to keep or re-arm) and replaced by one built on `FailWritesTo`: a restore that fails to dispatch leaves the entry Pending with no status write, and the next start writes it once. The fake transport timeout is added in B021 (PACKAGES-V-001). No lighting-interval or fake-clock tests (PACKAGES-024 is no-change).
- **Tests:** as named per batch; filters in B022 and B143.
- **Plan v2:** B143 (Claw part, listed there) and B022 (Ally part, named in B022's steps). Decided: D9.
- **Related:** PACKAGES-001, -005, -V-001.

### PACKAGES-V-002: A transient binding read makes Claw discard a valid crash-recovery entry

- **Severity:** medium
- **Where:** `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs` (binding string ~385-391), `ClawPlugin.Recovery.cs` (~35-55), `ClawRecoveryJournal.cs` (`Decide` ~80-98), `ClawServiceBase.cs` (`ClawFirmwareIdentities.IsWmi`, `IsUnknownEc`), `ClawPlugin.Commands.cs` (`WmiFirmware()` binding ~105), `ClawHardware.cs` (`ClawIdentityState`).
- **Problem:** the power and fan binding is built from that cycle's reads: `ec:<EC>` when `Get_EC` answered, otherwise `bios:<BIOS>`, plus `msi-acpi:<Get_WMI>` or `msi-acpi:unknown`. One refused `Get_EC` or `Get_WMI` changes the string for unchanged firmware, so `Decide` returns `Discard` and the TDP and fan state WSGM left behind after a crash is never restored: the user is stranded on WSGM-written limits. Command-time bindings vary the same way.
- **Best solution:** bind power and fans to the SMBIOS BIOS version only, as the Ally does: `ClawIdentityState.RecoveryBinding = "bios:" + Snapshot.BiosVersion` (null when the BIOS version is unknown, which reconciles as `Wait`). MSI ships EC updates inside BIOS packages, as the code's own comment says. `JournalCommandAsync` and reconciliation use `RecoveryBinding`; `WmiAvailable` becomes a plain bool from the provider probe. With the BIOS version unknown (`RecoveryBinding` null), a power or fan command writes without a restore point, traced, exactly like the existing "original could not be captured" fallback in `JournalCommandAsync`; `WmiFirmware()` and its `InvalidOperationException` go. One-time migration of existing entries: an entry whose binding contains `;msi-acpi:` is a legacy entry. It is restored at start only when its status is `Pending` and its binding equals the old-form string, still built at start from that start's reads (the reads stay for the diagnostics snapshot) with today's semantics (`ec:unknown` never matches, so `IsUnknownEc` survives only inside this comparison); every other legacy entry is discarded and traced. A legacy `RestoredUnverified` or `RestoreFailed` entry is discarded rather than kept, because today's code would replay it (an automatic retry) and under the new rule no command can re-arm it (its binding never equals the new form, so `BeginAsync` would replace it anyway). `ValidateEntry` accepts both forms.
- **Tests:** a `Get_EC` refusal at start still restores a `bios:`-bound entry; a legacy Pending `ec:...;msi-acpi:8.0` entry restores once when the old-form string matches and is discarded otherwise; a legacy `RestoreFailed` entry is discarded with no write; a new command journals `bios:<version>`. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawModelLifecycleTests|FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B143. Decided: D4 approved (Claw AGENTS: power and fan recovery bound to the BIOS version).
- **Related:** PACKAGES-001, -017; requirements.md item 17 (migration).

### PACKAGES-V-003: Ally publishes the readback instead of the written value after an unverified write

- **Severity:** medium
- **Where:** `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Surface.cs` (publish loop ~162-166, `CurrentState` ~184-208); `AllyAcpiCapabilities.cs` (`Effective` ~78-86); `AllyServices.cs` (`PowerService.Refresh` ~74-77, `FanService.Refresh` ~197-206).
- **Problem:** the published value is `CurrentState(...) ?? _written[...]`, and `CurrentState` returns the firmware's read whenever it has one; `_written` is used only when the firmware is silent. After an unverified write with a different readback (a clamped limit, or the fan case the code itself expects, where DSTS reports the mode's table rather than the curve in force), the slider snaps back and the fan editor shows a curve the user did not set. Ally AGENTS and plan line 111 require the written value.
- **Best solution:** the written value wins for the cycle wherever the read differs, as the Claw's `Observe` does, taken from what each capability last wrote.
  - Power: `Effective()` (used only by `PowerService.Refresh` for publication once PACKAGES-007 removes its use as the write-order input) becomes `_written.X ?? read.X` for each field; a mode change still clears the written limits, so the mode's own limits show after it. The fixed D8 write order needs no current state, so nothing else reads `Effective()`.
  - Fan curve: publish `_fans.Capability.WrittenCpu` first, then the read, then the default (`CurrentState`'s `FanCurve` case and `Fallback` merge into that one expression). Not `_written[fan.curve]`: choosing automatic mode writes the captured or factory curves through `ApplyAutomaticAsync`, which updates `WrittenCpu` but leaves the last custom curve in `_written`, so the dictionary would show a curve no longer in force.
  - Charge limit (the only other commanded capability with a read): in the publish loop take `_written[(id, instance)]` before `CurrentState`. Lighting and fan mode already publish `_written` (no read). Readings that are never commanded (fan RPM) are unaffected.
- **Tests:** a fan-curve write whose readback differs publishes the written curve; automatic mode after a custom curve publishes the restored curve; a power write with a clamped readback publishes the written values; after a mode change the firmware's limits publish; the SPL, SPPT, FPPT write order from PACKAGES-007 is unchanged. Filter: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~PluginTests|FullyQualifiedName~AcpiCapabilityTests"`.
- **Plan v2:** B022.
- **Related:** PACKAGES-005, -007.

### PACKAGES-V-006: A journal that fails mid-cycle misreports every power and fan command as Indeterminate

- **Severity:** medium
- **Where:** Claw `src/WSGM.Device.Msi.Claw/ClawPlugin.Commands.cs` (`JournalCommandAsync`, `BeginAsync` call ~506-510); Ally `src/WSGM.Device.Asus.RogAlly/AllyServices.cs` (journal arming ~99, ~228) reached from `RogAllyPlugin.Commands.cs` (`ApplyAsync`, generic catch ~61-66); `DeviceRecoveryJournal.BeginAsync` (`ThrowIfUnavailable`).
- **Problem:** when the record becomes unavailable during a cycle, `BeginAsync` throws before any hardware access, and both packages report `Indeterminate` with `RestoreFailed`. The service stays Owned, so every later command reports an uncertain write that never happened. Plan v2 drops the A02_02 save-failure latch (B010), so a failed save affects only its own command; but a `FailureReason` set by the resume-time `CheckHealthAsync` lasts for the rest of the cycle (PACKAGES-C-001 removes that check), and the misreport remains for every command that meets an unwritable record.
- **Best solution:** a journal refusal before the first write is `Rejected`, consistent with the SDK rule "blocked rather than mutate without a restore point". Claw: wrap `BeginAsync` in `JournalCommandAsync`: caller cancellation gives `Rejected` `Quiescing`, any other non-OOM exception gives `Rejected` `TransportFaulted` "The recovery record is unavailable; nothing was written." Ally: the arming call in `ApplyAsync` before the power and fan writes gets the same two-branch catch.
- **Tests:** with a fake journal directory that refuses writes mid-cycle, a power command is `Rejected` with no `Set_*` (Claw) and no ATKACPI write (Ally). Filters: Claw `--filter "FullyQualifiedName~ClawPluginTests"`, Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B021 (Claw half, as listed). The Ally half is in no batch spec; it rides in B022, which edits `RogAllyPlugin.Commands.cs` and `AllyServices.cs`.
- **Related:** B010 (A02_02 trimmed, latch dropped), SDK-002/003, PACKAGES-004, -010, -C-001.

### PACKAGES-C-001: A resume-time journal health check latches for the cycle and skips the stop restore

- **Severity:** medium (found by the solution checker)
- **Where:** `src/WSGM.Device.Msi.Claw/ClawPlugin.cs` (resume, `CheckHealthAsync` then `BlockService` for power, fans and controller ~279-286); `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.cs` (resume ~243-247, `Block(journalFailure, _power, _fans, _controller)`); consumers `ClawServiceBase.cs` (`RestoreJournalledAsync` returns Faulted on `ReconciliationBlockReason` ~24-27), `ClawControllerService.cs` (~106-108), Ally `AllyServices.cs` (`ReleaseAsync` ~107-110, ~236-239), `AllyControllerService.cs` (~144-146); `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`CheckHealthAsync` sets `FailureReason`, which nothing ever clears).
- **Problem:** at every resume both plugins rewrite the record to probe it. One failed write (a file lock from a scanner or backup right after wake) sets `FailureReason` for the rest of the cycle, so every later `BeginAsync` and `SetStatusAsync` throws, and it sets `ReconciliationBlockReason` on power, fans and controller. The suspendable controller is re-acquired straight away and faults for the cycle. Power and fans either fault when the Claw resume re-acquires them, or stay Owned on the Ally (non-suspendable, not re-acquired) with every TDP and fan command failing (today as `Indeterminate`, PACKAGES-V-006); either way AutoTDP is dead until the next start. Their release then returns Faulted before it looks at the Pending original, so stop leaves the WSGM-written limits and curves on the device until the next WSGM start. This is the latch B010 refuses for the SDK ("one transient file lock into the loss of every journalled control"), applied at resume.
- **Best solution:** delete the resume-time health check and its `BlockService`/`Block` calls in both plugins. A record that is unwritable after resume is then found by the command that needs it: its `BeginAsync` save fails and the command is `Rejected` with nothing written (PACKAGES-V-006), and the next command tries again with no latch. The release restores the Pending original as usual. With PACKAGES-010 moving the write probe into `LoadAsync`, `CheckHealthAsync` has no caller left: delete it from the SDK inside API 12 and from `reference.md`. `ReconciliationBlockReason` is then set only by a journal failure at start, where the services never become Owned.
- **Tests:** Claw and Ally: a resume while the fake state directory refuses one write leaves power, fans and controller Owned; the next power command after the directory recovers is applied; stop after a resume restores the Pending power original. Filters: Claw `--filter "FullyQualifiedName~ClawPluginTests|FullyQualifiedName~ClawModelLifecycleTests"`, Ally `--filter "FullyQualifiedName~PluginTests"`, SDK `--filter "FullyQualifiedName~DeviceRecoveryJournalTests"`.
- **Plan v2:** in no batch spec; it rides in B143 (same recovery policy, after B142), which must add `ClawPlugin.cs` and `RogAllyPlugin.cs` to its file list.
- **Related:** PACKAGES-010, -V-006, -001; B010 (latch dropped for the same reason); SDK-016.

## Low

### PACKAGES-006: Ally fan curve write performs an automatic rollback write after a failed write

- **Severity:** low (verifier lowered from medium; the change alters a recorded Ally AGENTS rule and needs sign-off first)
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs` (`AllyFanCapability.WriteAllAsync` rollback block ~539-553); fault branch in `RogAllyPlugin.Commands.cs` (`ExecuteBoundCommandAsync` ~68-75).
- **Problem:** when a curve write throws, the plugin immediately writes the previously read curves back: a second automatic write after an uncertain one. HC never rolls back and the Claw explicitly does not. A failed rollback then faults the fan service, which takes AutoTDP-adjacent fan control down until the next resume or restart.
- **Best solution:** delete the rollback block and the `before` read that only feeds it; a failed write returns `CommandResults.Indeterminate(command, TransportFaulted, "The fan-curve write failed.", RollbackResult.NotRequired)` and writes nothing else. Delete the `RestoreFailed` fault branch in `ExecuteBoundCommandAsync`, whose last producer this was (PACKAGES-V-004 already stops the catches producing `RestoreFailed`). Ally AGENTS ("Only a journalled original's failed rollback faults the service") changes in a separate diff approved first.
- **Tests:** a failed curve write performs no further ATKACPI write and leaves the fan service Owned. Filter: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj`.
- **Plan v2:** B023. Decided: D4 approved (the Ally AGENTS diff is shown with the batch and applied); D9 confirms it (no readback-fed rollback on the Ally).
- **Related:** PACKAGES-016 (Ally part), -V-004.

### PACKAGES-010: Ally journal open skips the writability check

- **Severity:** low (verifier lowered from medium: Ally resume already checks health, and the visible effect is one misreported command)
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyRecoveryJournal.cs` (`OpenAsync` ~21-28) vs `src/WSGM.Device.Msi.Claw/ClawRecoveryJournal.cs` (`OpenAsync` ~19-27); `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`LoadAsync`, `CheckHealthAsync`).
- **Problem:** Ally starts with `FailureReason` unset even when the state directory is not writable, so the start-time block (`RogAllyPlugin.StartAsync` blocks power, fans and controller on `FailureReason`) never fires and the first command fails inside the journal instead.
- **Best solution:** `DeviceRecoveryJournal.LoadAsync` ends with the same write probe `CheckHealthAsync` performs, so every package gets it at open; delete the explicit `CheckHealthAsync` call in `ClawRecoveryJournal.OpenAsync`. Both packages' existing start code then blocks the journalled services. `CheckHealthAsync` itself is deleted with its last caller by PACKAGES-C-001.
- **Tests:** SDK: load against an unwritable directory sets `FailureReason`; Ally start with an unwritable directory blocks power, fans and controller. Filters: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~DeviceRecoveryJournalTests"`, Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B143 (after B010, same file).
- **Related:** A02_02 / B010, PACKAGES-V-006.

### PACKAGES-014: Command value validation exists three times with different strictness

- **Severity:** low (verifier lowered from medium: the host already rejects out-of-descriptor values before dispatch, so the weaker Ally copy has no observable effect today)
- **Where:** Claw `src/WSGM.Device.Msi.Claw/ClawPlugin.Commands.cs` (`ValidateCommandValue` and the `ValueOutOfRange` value cases ~288-447); Ally `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Commands.cs` (`Validate` value switch ~206-272: no step, no curve ordering, no boolean); host `src/WSGM/Shell/DeviceCapabilityRouter.cs` (`DeviceCapabilityValidation.ValueMatches`/`CurveIsValid` ~1036-1160, used ~456-482, 7 references).
- **Problem:** three copies of the same deterministic rule with different strictness; any future package copies whichever it finds.
- **Best solution:** move `ValueMatches` and `CurveIsValid` verbatim into a public static `CapabilityValueValidation` in `WSGM.Device.Sdk.Capabilities` (`PlainText` is already in the SDK, so the move is self-contained). The host's 7 references call the SDK; `DeviceCapabilityValidation` keeps only members that are not value checks, or is deleted if none remain. Both packages replace their value switches with one call. They keep their generation, deadline and power-source checks, which are their own revalidation duty.
- **Tests:** SDK unit tests per value kind (integer range and step, choice, boolean, curve point order and bounds); host router tests unchanged. Filters: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Capabilities"`, `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCapabilityRouter"`, both package projects.
- **Plan v2:** B144.
- **Related:** B082 (router command correctness edits the same router file earlier).

### PACKAGES-016: Rollback reporting is fiction and its branches are dead

- **Severity:** low
- **Where:** Claw `ClawCapabilities.cs` (handlers only return `NotRequired`), `ClawPlugin.Commands.cs` (rollback-fault branch after `CompleteCommandAsync` ~516-540, `Indeterminate` helper ~545-552), `ClawRecoveryJournal.CompleteCommandAsync` (~43-58); Ally `AllyServices.cs` (lighting reports `RestoreFailed` for a never-journalled write ~430-431), `RogAllyPlugin.Commands.cs` (rollback fault branch ~68-75). The host only logs `Rollback` (`DeviceCapabilityRouter.cs` ~698).
- **Problem:** Claw handlers never roll back, so the rollback-fault branch and most of `CompleteCommandAsync` never run, while the `Indeterminate` helper stamps `RestoreFailed` on every handler exception. Ally lighting claims a failed restore for a write that was never journalled. The reported rollbacks describe nothing that happened.
- **Best solution:** report `RollbackResult.NotRequired` everywhere in both packages; with PACKAGES-006 no command rollback remains. Claw (B021): `Indeterminate` helper uses `NotRequired`; delete the block after `_journal.CompleteCommandAsync` that faults power or fans on `RestoreFailed`. `CompleteCommandAsync` itself goes in B143 (PACKAGES-001 step 6). Ally lighting (B022): return `NotRequired`. Ally rollback-fault branch: deleted in B023 with PACKAGES-006.
- **Tests:** a Claw handler exception after a setter yields `Indeterminate` with `NotRequired` and the service stays Owned; a failed Ally lighting write yields `NotRequired`. Filters: Claw `--filter "FullyQualifiedName~ClawPluginTests"`, Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B021 (Claw, as listed); Ally lighting rides in B022 (not named in its spec); Ally fault branch in B023.
- **Related:** PACKAGES-004, -006, -V-004.

### PACKAGES-018: Freed HID preparsed data can be used by a still-running reader

- **Severity:** low (verifier lowered from medium: the window needs the stop token already cancelled while the reader is inside the synchronous `TryDecode`; it would crash natively, so the fix stays)
- **Where:** `src/WSGM.Device.Msi.Claw/WindowsHidTransports.cs` (`StopAsync` ~363-428, `ReadLoopAsync` ~492, descriptor creation ~327-344); `HidDescriptorGamepad.cs` (~62-70, ~118-173).
- **Problem:** when `reader.WaitAsync(cancellationToken)` is cancelled, `StopAsync` continues and disposes the descriptor (`HidD_FreePreparsedData`) while the reader may still call `HidP_*` on that pointer. Affects every Claw except the measured MS-1T52 path.
- **Best solution:** the reader owns the descriptor: `ReadLoopAsync` already receives it as an argument; its `finally` disposes it. `StopAsync` only sets `_descriptor = null` and never disposes it. If start fails after creating the descriptor but before the reader starts, start disposes it itself. This removes a shared-ownership path and adds no state.
- **Tests:** with a fake reader blocked in decode and the stop token cancelled, the descriptor is disposed only after the reader exits. Filter: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~WindowsHidTransportsTests"`.
- **Plan v2:** B145.
- **Related:** none.

### PACKAGES-020: Redundant locks around haptic output

- **Severity:** low
- **Where:** Claw `src/WSGM.Device.Msi.Claw/ClawControllerService.cs` (`_hapticGate` ~74, `ApplyHapticsAsync` ~427-467, stop reset ~552-556); Ally `src/WSGM.Device.Asus.RogAlly/AllyControllerService.cs` (`_hapticGate` ~121, stop reset ~263-267, `ApplyHapticsAsync` ~280-312).
- **Problem:** `_hapticGate` sits inside a semaphore (`_outputSerializer`, `_outputGate`) that already serializes output. Its only use outside the semaphore is the reset of the last-written levels in stop.
- **Best solution:** delete `_hapticGate` in both services. The last-level fields are touched only under the output semaphore. Move the reset from stop into `AcquireAsync`, done under the output semaphore before the service becomes Owned: `ApplyHapticsAsync` returns early while the service is not Owned, so this is the one place a reset cannot race an in-flight write.
- **Tests:** after release and re-acquire, the first non-zero rumble frame is written even when it equals the last frame of the previous cycle. Filters: Claw `--filter "FullyQualifiedName~ClawPluginTests"`, Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B022 (Ally, as listed). The Claw half is in no batch spec; it rides in B145, which edits `ClawControllerService.cs`.
- **Related:** none.

### PACKAGES-021: Claw release compares physical location by string

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/ClawControllerService.cs` (release mode check ~403-405) vs `HidDevices.SamePhysicalLocation` used elsewhere (`ClawPlugin.Recovery.cs` ~148-151, `ClawControllerService.cs` ~150).
- **Problem:** `string.Equals(..., OrdinalIgnoreCase)` can call a correctly restored pad unverified when the location strings differ in form, recording `RestoredUnverified` for nothing.
- **Best solution:** use `HidDevices.SamePhysicalLocation(restored.PhysicalLocation, _original.PhysicalLocation)`.
- **Tests:** a release whose switched pad reports an equivalent location in another form records `RestoredVerified`. Filter: `--filter "FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B145.
- **Related:** none.

### PACKAGES-022: Claw publishes an undeclared scenario value

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/ClawPlugin.Surface.cs` (`Scenario(byte, ClawModel)` returns `"unknown"` ~580-596; declared choices ~137-143); host drops it (`DeviceCapabilityRouter.cs` ~576-587).
- **Problem:** `"unknown"` is not a declared choice, so the host drops the state and keeps showing the previous scenario.
- **Best solution:** `Scenario` returns `CapabilityValue?` and null for an undeclared mode; the publisher already maps a null value to `Unknown` quality with no `ObservedAt`.
- **Tests:** an EC scenario byte with an undeclared mode publishes an Unknown state. Filter: `--filter "FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B021.
- **Related:** none.

### PACKAGES-023: WMI timeouts are reported as Quiescing

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/ClawCapabilities.cs` (`ClawApplied.Failed` ~32-42); the 3 s WMI timeout surfaces as an OCE (`MsiWmiPlatform.cs` ~108-121).
- **Problem:** any `OperationCanceledException` becomes `Quiescing`, so a transport timeout reads to the host like a shutdown.
- **Best solution:** `ClawApplied.Failed(command, operation, exception, cancellationToken)`: `Quiescing` only when `exception is OperationCanceledException && cancellationToken.IsCancellationRequested`, otherwise `TransportFaulted`. Every handler passes its token.
- **Tests:** a WMI write timeout with an uncancelled token gives `TransportFaulted`; a caller cancellation gives `Quiescing`. Filter: `--filter "FullyQualifiedName~ClawCapabilitiesTests"`.
- **Plan v2:** B021.
- **Related:** PACKAGES-V-001.

### PACKAGES-026: Plugin DisposeAsync writes hardware with an invented deadline

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/ClawPlugin.cs` (`DisposeAsync` ~418-445: `StopAsync` with `Deadline.After(12 s)`); `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.cs` (`DisposeAsync` ~368-373); SDK `docs/reference.md` (plugin lifecycle).
- **Problem:** if the host never stopped the plugin, `DisposeAsync` runs a full `StopAsync` with 12 s of its own, writing hardware outside the host's shutdown budget. Both in-repo hosts already stop before they dispose (`DevicePluginRuntime`, Device Lab `PluginTestWorkflow`), but the SDK is MIT for outside hosts and the obligation is unwritten.
- **Best solution:** `DisposeAsync` releases handles only and never writes hardware: delete the `if (_active) await StopAsync(...)` block in both plugins; the journal covers an un-stopped cycle at the next start. Dispose every owner while collecting failures (SDK-V-004), and drop the `_journal.DisposeAsync()` calls (the journal stops being `IAsyncDisposable` in API 12). Add the contract line to `reference.md` and the plugin-lifecycle docs: the host awaits `StopAsync` before `DisposeAsync`. Start's 15 s and failed-start rollback's 12 s budgets stay: plan v2 adds no start deadline to `PluginStartContext` (critic conflict 18), and the host's cancellation token still bounds both.
- **Tests:** disposing an active plugin performs no hardware write and disposes every transport even when one throws. Filters: both package projects.
- **Plan v2:** B142.
- **Related:** SDK-V-004, critic conflict 18, PACKAGES-024 and -025 (no-change).

### PACKAGES-027: Two copies of the write budget with different exception contracts

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/ClawHardware.cs` (`ClawWriteBudget` ~229-250, throws OCE), `src/WSGM.Device.Asus.RogAlly/AllyWriteBudget.cs` (`AllyWriteBudget`, `AllyBudgetException`).
- **Problem:** the Claw's OCE is indistinguishable from a cancellation (feeding PACKAGES-004), the Ally's typed exception is right, and the two drift.
- **Best solution:** one public SDK helper, `DeviceWriteBudget.IsAvailable(Deadline)` and `Require(Deadline, string operation)`, throwing `DeviceWriteBudgetException : Exception` (not an OCE), with the existing 2 s minimum in one place (a write the host cannot wait for becomes uncertain, so the boundary is justified). Command boundaries map it to `Rejected` `Quiescing` (retryable); release paths map it to `ReleasedUnverified` (PACKAGES-035). Delete `ClawWriteBudget`, the Claw-local exception from B021, and `AllyWriteBudget.cs`.
- **Tests:** SDK unit test for both methods; package suites unchanged in behaviour. Filters: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Capabilities"`, both package projects.
- **Plan v2:** B144.
- **Related:** PACKAGES-004, -035.

### PACKAGES-028: Motion attachment is duplicated

- **Severity:** low
- **Where:** Claw `src/WSGM.Device.Msi.Claw/ClawServices.cs` (`MotionService` core ~393-542), Ally `src/WSGM.Device.Asus.RogAlly/AllyControllerService.cs` (`AllyMotionService` ~21-95).
- **Problem:** both keep the latest sample, a `GyroFrameResampler` and `Current(now)`; only the Claw has the one-shot staleness trace.
- **Best solution:** one public sealed SDK `MotionAttachment`: `Publish(in MotionSample)`, `Current(DateTimeOffset now)`, the resampler and the one-shot staleness trace through the process `PluginTrace` (which stays, critic conflict 18). It keeps the Claw's allocation-free code shape (no per-sample allocation or logging). Both services wrap it; the Ally gains the staleness trace.
- **Tests:** SDK unit tests for latest-sample, resampling and the single staleness trace; `MotionFreshnessReportingTests` unchanged. Filters: SDK `--filter "FullyQualifiedName~Capabilities"` (or the new test class name), both package projects.
- **Plan v2:** B144.
- **Related:** none.

### PACKAGES-030: Shared capability IDs and surface vocabulary duplicated

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/ClawCapabilities.cs` (`CapabilityIds` ~855-906), `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Surface.cs` (`CapabilityIds` ~364-405), `SourceOwnershipChoices` in both surface files, descriptor builder helpers in both; host third copy `src/WSGM/Core/DeviceConfiguration.cs` (`DeviceAuthoredProfileCapabilities.FanCurve`/`Lighting` ~7-15).
- **Problem:** the IDs are persisted wire names in profiles; a silent divergence would orphan saved per-game values.
- **Best solution:** a public static `CapabilityIds` and the ownership choices in `WSGM.Device.Sdk.Capabilities` beside `DeviceSections`, values byte-identical. It holds the union of what the two packages declare, including the IDs only one uses (Claw `fan.measured-rpm` and `telemetry.temperature`, Ally `fan.reading`, `lighting.effect` and `lighting.effect-speed`); the two different fan-reading IDs both stay, since renaming either changes a wire name. Section, instance and effect-name constants that differ per package stay in the packages. Delete both package copies of the shared IDs. Delete `DeviceAuthoredProfileCapabilities` and point its users at the SDK constants; replace host string literals for the same IDs with the constants where found.
- **Tests:** an SDK test pinning every constant's string value (they are persisted); package and host suites unchanged. Filters: SDK `--filter "FullyQualifiedName~Capabilities"`, `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCapabilityRouter"`.
- **Plan v2:** B144.
- **Related:** plan claim C14 (wire names stay).

### PACKAGES-032: Claw reads the MCU revision through WMI PnP parsing

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs` (`mcuFirmware` from `UsbEndpoints.DeviceRelease` ~417-423, PnP parsing ~513-545), `ClawModels.cs` (`McuLayout` ~239-251); consumers `ClawControllerService.cs` (~474), `ClawPlugin.cs` (~145).
- **Problem:** HC reads `Attributes.Version` of the HID device; the SDK already exposes it as `HidCollection.ReleaseNumber`. The PnP parse can fail where the HID attribute does not, and an unreadable revision falls back to the last table row.
- **Best solution:** the identity reader takes `McuFirmwareVersion` from the MCU HID collection's `ReleaseNumber`, formatted as four hex digits like bcdDevice; `UsbEndpoints` stays for diagnostics only. The documented fallback (reference unit's row) is unchanged, since open question Q3 got no plan v2 decision, and it becomes practically unreachable.
- **Tests:** a fake MCU collection with `ReleaseNumber` 0x0230 selects the 0230 layout. Filter: `--filter "FullyQualifiedName~ClawPluginTests|FullyQualifiedName~ClawModelsTests"`.
- **Plan v2:** B145.
- **Related:** none.

### PACKAGES-033: MSI_Event repair timeout escapes and the repair repeats on every resume

- **Severity:** low
- **Where:** `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs` (`MsiEventRepair` ~744-856, `WaitForExitAsync` ~853; called from the OEM event watcher's start ~628); HC `ClawA1M.cs` (Open).
- **Problem:** the 10 s `WaitForExitAsync` timeout throws an OCE the catch list does not cover, so the OEM service faults. The repair runs in `OemEventService.AcquireAsync`, so a repair that does not help restarts `ACPI\PNP0C14`, the device MSI_ACPI power and fan calls go through, on every wake.
- **Best solution:** take the `MsiEventRepair.EnsureAsync` call out of `MsiOemEventSource.StartAsync` and expose it as a second member of `IMsiOemEventSource` (`EnsureEventClassAsync`, a no-op in the Claw fakes), which `ClawPlugin.StartAsync` calls once per cycle before services acquire, as HC runs it from `Open`; resume re-acquire only subscribes. Calling the static repair from the plugin directly would make every Claw plugin test query live WMI and, on a machine missing `MSI_Event`, write the registry and restart `ACPI\PNP0C14`; the interface keeps it behind the hardware seam. In the restart helper, catch `OperationCanceledException` when the caller token is not cancelled, trace "pnputil did not finish within 10 s", and return false; pnputil is left to finish (killing a device restart midway is worse). No once-only flag.
- **Tests:** start invokes the fake's repair once and a resume does not invoke it again; the OEM service acquires after a resume. The pnputil timeout branch has no unit seam (real `Process`) and is checked by review. Filter: `--filter "FullyQualifiedName~ClawPluginTests"`.
- **Plan v2:** B145.
- **Related:** none.

### PACKAGES-035: A release budget refusal faults the service

- **Severity:** low (verifier: applies to the Claw as well)
- **Where:** Ally `src/WSGM.Device.Asus.RogAlly/AllyServices.cs` (`AllyWriteBudget.Require` in release ~121, ~251, ~287); Claw `src/WSGM.Device.Msi.Claw/ClawServiceBase.cs` (`RestoreJournalledAsync`, `Require` ~42) and `ClawControllerService.cs` (release ~397, records `RestoreFailed` ~412-417); `DeviceServiceLifecycle.InvokeAsync` faults on the exception.
- **Problem:** the Ally AGENTS says a budget refusal "has touched nothing: never fault the service", yet at release it reports Faulted and the stop as Failed. On the Claw, the OCE from `ClawWriteBudget.Require` faults power and fans, and the controller release records `RestoreFailed`, which PACKAGES-001 would then never replay.
- **Best solution:** release paths check `IsAvailable` first. On refusal: no write, no journal status change (the entry stays Pending and is restored at the next start), and `Set(ReleasedUnverified, Quiescing "Not enough time to restore; the original stays recorded for the next start")`. The Claw controller release leaves the status untouched (its entry re-reads and restores at the next start anyway).
- **Tests:** a release with less than 2 s left writes nothing, leaves the entry Pending and returns `ReleasedUnverified`, in both packages. Filters: Claw `--filter "FullyQualifiedName~ClawModelLifecycleTests|FullyQualifiedName~ClawPluginTests"`, Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B143.
- **Related:** PACKAGES-001, -027, DEVICE-001 (B008: an unverified stop no longer blocks a restart).

### PACKAGES-036: Ally re-arm restores an original captured under another BIOS

- **Severity:** low
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyRecoveryJournal.cs` (`ArmAsync` ~36-55); release paths in `AllyServices.cs`.
- **Problem:** `ArmAsync` re-arms an unresolved entry without comparing its firmware binding, and the release then writes an original captured under another BIOS. D9 removes unresolved Ally entries and the re-arm rule, so that half is gone; what remains is that a Pending entry kept under an older binding (one left by `Wait` at start, PACKAGES-009) is reused by the next command instead of replaced.
- **Best solution:** delete `ArmAsync` and `PendingOriginalFor` from `AllyRecoveryJournal`; callers (`PowerService`, `FanService`, `AllyControllerService.ConfigureAsync`) use the SDK `BeginAsync` and `PendingOriginalFor` from PACKAGES-001, where a changed binding replaces the entry with a fresh capture and a matching Pending entry keeps its first original.
- **Tests:** a Pending entry bound to an old BIOS is replaced by the next command's capture, and release writes the new original. Filter: Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B143. Decided: D9 (no re-arm rule on the Ally).
- **Related:** PACKAGES-001, -008.

### PACKAGES-V-004: Ally faults power and fans when a command is cancelled or fails unexpectedly after the journal is armed

- **Severity:** low
- **Where:** `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Commands.cs` (`ExecuteBoundCommandAsync` catches ~56-66 and fault branch ~68-75); `AllyAcpiCapabilities.cs` (handlers catch only `IOException`/`Win32Exception` ~221, ~534).
- **Problem:** an OCE from `_delay` during the 100 ms write spacing (the command deadline reached after waiting behind the serializer), or any non-IO exception, leaves the handler and is stamped `RestoreFailed`; when the service holds a pending original, it is faulted until re-acquire. Power and fans are non-suspendable, so re-acquire means the next resume or restart: AutoTDP and the TDP row are dead until then although no rollback ran.
- **Best solution:** both post-admission catches return `Indeterminate` with `RollbackResult.NotRequired` (`Quiescing` for caller cancellation, `TransportFaulted` otherwise) and never fault the service. The fault branch then has only the fan rollback as producer, and B023 deletes both (PACKAGES-006).
- **Tests:** a power command cancelled during write spacing returns `Indeterminate` with `NotRequired`, and power stays Owned and accepts the next command. Filter: Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B022.
- **Related:** PACKAGES-006, -016.

### PACKAGES-V-005: Ally caches a missing Aura collection for the whole cycle

- **Severity:** low
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyHid.cs` (`Find` with `_searched` ~382-401); `AllyServices.cs` (lighting acquire ~365-379); Ally resume filter (`RogAllyPlugin.cs` ~249-252).
- **Problem:** if the Aura collection is not enumerated when the cycle starts (typical straight after boot or wake), `Find` stores `_searched = true` with `_aura = null`. Lighting goes Passive, and the resume re-acquire gets the cached null back. Lighting stays unavailable until WSGM restarts the cycle; only a write failure clears the flag, and no write is possible while Passive.
- **Best solution:** drop the negative cache: delete `_searched`; `Find` returns `_aura` when set, otherwise enumerates and stores only a found collection. The write-failure path that cleared `_searched` now clears `_aura`. Enumeration happens only at acquire while Passive, which is not a high-rate path.
- **Tests:** a first acquire with no collection is Passive; a resume after the collection appears acquires lighting. Filter: Ally `--filter "FullyQualifiedName~PluginTests"`.
- **Plan v2:** B022.
- **Related:** PACKAGES-013 (resume filter kept, no-change).

## Nit

### PACKAGES-031: Dead code and test-only seams

- **Severity:** nit
- **Where:** `src/WSGM.Device.Msi.Claw/ClawPlugin.Surface.cs` (`BooleanDescriptor` ~558-578, unused); `src/WSGM.Device.Asus.RogAlly/AllyInput.cs` (`AllyOemButtonState.Clear` ~135-145, unused); `src/WSGM.Device.Msi.Claw/ClawHardware.cs` (`DefaultLightingProfileAddress` ~36) and `WindowsHidTransports.cs` (`IsSupportedDevicePath` ~623-626), used only by tests; `ClawPlugin.cs` (`Services` ~65) and `WindowsHidTransports.cs` (`Serializer` ~19-20), seams that exist for tests.
- **Problem:** unused members and production members kept alive only by tests.
- **Best solution:** delete `BooleanDescriptor` (B021) and `AllyOemButtonState.Clear` (B022). In B145 delete `IsSupportedDevicePath` together with its only test (`WindowsHidTransportsTests` ~15), and delete `DefaultLightingProfileAddress`, whose three users in `ClawCapabilitiesTests` (~142, ~163, ~198) are lighting tests that keep running with a local `0x024A` constant, and keep the `Services` and `Serializer` seams only if the rewritten tests of B021 to B145 still need them.
- **Tests:** compile plus the touched package suites.
- **Plan v2:** B021 (Claw descriptor), B022 (Ally, as listed in plan v2), B145 (the rest).
- **Related:** none.

### PACKAGES-034: HC scaffold manifest and test

- **Severity:** nit
- **Where:** `src/WSGM.Device.HandheldCompanion/plugin.wsgm.json` (`apiVersion` 11, entry type ~6 that does not exist); `tests/WSGM.Device.HandheldCompanion.Tests/ScaffoldManifestTests.cs` (~9-16).
- **Problem:** the manifest names a type that does not exist and the only test asserts the API level. Non-installability rests on the missing curated entry (`plugins/curated`, `eng/build-bundle.ps1`), which is enough.
- **Best solution:** bump `apiVersion` to 12 with the other two manifests; no guard, no other change.
- **Tests:** `dotnet test tests\WSGM.Device.HandheldCompanion.Tests\WSGM.Device.HandheldCompanion.Tests.csproj`.
- **Plan v2:** B142.
- **Related:** plan claims C13 and C18.

### PACKAGES-038: Claw project does not pin x64

- **Severity:** nit (verifier: the shipped package is already x64 because `eng/pack-device.ps1` builds with `/p:PlatformTarget=x64`; only plain project builds are AnyCPU)
- **Where:** `src/WSGM.Device.Msi.Claw/WSGM.Device.Msi.Claw.csproj`; compare `src/WSGM.Device.Asus.RogAlly/WSGM.Device.Asus.RogAlly.csproj` (~6). The 40-byte `INPUT` layout in `ClawInput.cs` (~492-505) is x64-only.
- **Problem:** a non-packaged build of the library is AnyCPU although its interop layout assumes x64 and its AGENTS says x64.
- **Best solution:** add `<PlatformTarget>x64</PlatformTarget>` as the Ally project has. The Claw test project already targets x64.
- **Tests:** warning-free `dotnet build src\WSGM.Device.Msi.Claw\WSGM.Device.Msi.Claw.csproj -c Release`, then the Claw test project.
- **Plan v2:** B145.
- **Related:** none.

## Refuted or no-change

- **PACKAGES-013** (medium): no change. The shell drift is real, but re-acquiring Ally power and fans on resume names no defect: they are non-suspendable, stay Owned and have no watchdog, and recreating their capabilities would discard `_written`/`WrittenCpu`. The Ally resume filter stays; the real drifts are fixed by PACKAGES-003, -004, -010, -014, -015, -027, -028.
- **PACKAGES-019** (nit, verifier lowered from low): no change. The two-stream overlap follows only an already abnormal 2 s dispose timeout; retaining the stream and refusing a second start would add state without an observed defect.
- **PACKAGES-024** (low): no change. `ActiveClock` and delays stay process-wide (critic conflict 18); test-only clock injection is mechanism. The watchdog's wall-clock read disappears anyway with PACKAGES-002.
- **PACKAGES-025** (medium): no change. `PluginTrace` stays the process sink (critic conflict 18); no per-instance `PluginTraceSink`, and the `[Collection("plugin-trace")]` serialization stays.
- **PACKAGES-029** (nit): no change. The reader-fault and reconnect protocols differ between the packages and the copies have not drifted.
- **PACKAGES-039** (nit): no change. The 64-entry Ally keyboard channel is a real hook-thread boundary (the callback cannot block), not an arbitrary limit.
- **PACKAGES-012** (low): no change (moved by the solution checker). The proposed fast path has no effect: `ValueTask.AsTask()` on a synchronously completed `ValueTask` returns `Task.CompletedTask` (no allocation, also for an `IValueTaskSource`-backed one that has completed), and in the asynchronous case both shapes must call `AsTask()` because blocking on an incomplete source-backed `ValueTask` is not allowed. Any allocation there comes from the host's publish, which B073 makes synchronous. An if/else with no observable effect is mechanism without a defect; B022 drops its "PACKAGES-012 publish fast path" step.
- **PACKAGES-011** (medium): no change, by maintainer decision D7 (`DECISIONS.md`): the Ally reads motion from WinRT first, like HC, and falls back to the legacy sensor fields only when WinRT has no sensor, as today. B023 drops the motion-order swap and its Ally AGENTS diff.
- **Plan claim C9, Ally poll clause** (refuted by the verifier): `AsTask()` on a synchronously completed `ValueTask` returns `Task.CompletedTask`, so the poll loop does not allocate per sample; the remainder, PACKAGES-012, is no-change as well. The WinRT motion part of C9 is settled by D7 (PACKAGES-011, no change).
