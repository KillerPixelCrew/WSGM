# Device domain: adversarial verification of device.md

Verifier: Claude (Opus 5.5), read-only. Baseline `master` 1329813f. Nothing was built, tested, mutated or run live.

Read for this pass: `DeviceCoordinator.cs` (in full), `DevicePluginRuntime.cs`, `PluginHost.cs`,
`DevicePluginCompatibilityAdapter.cs`, `DeviceCapabilityRouter.cs` (1-1035), `AutoTdpService.cs` (in full),
`ApplicationPerformanceReconciler.cs`, `DevicePowerPresets.cs` (1-200), `DevicePowerAssignments.cs`,
`DeviceOemActionRouter.cs`, `DeviceDesiredWriteAdmission.cs`, `CapabilityDesiredReconciler.cs`,
`CapabilityUserWrites.cs`, `CommandOutcomeExtensions.cs`, `DeviceCoordinatorDiagnostics.cs`, `PluginHapticSink.cs`,
`EffectivePowerModeNotification.cs`, `WindowsCpuBoostApi.cs`, relevant parts of `DeviceOverlayBridge.cs`,
`SimulatedDeviceOverlaySource.cs`, `GpuCoordinator.cs` (250-340), `HidHideOwnership.cs` (110-350),
`ControllerManager.cs` (100-240, 805-935), `ShellSession.cs` (290-545), `ShellSession.Shutdown.cs` (90-210),
`ShellSession.Power.cs` (150-282), `Core/ApplicationShutdown.cs` (75-180), `Core/ConfigStore.cs` (590-605, 850-885),
`Core/PerformanceService.cs` (55-200, 590-700, 950-983), `Core/Profiles/ProfileResolver.cs` (100-135),
`DeviceServiceLifecycle.cs` (200-298). Tests: `PluginHostTests` (names), `AutoTdpServiceTests` (names, 755-812),
`DeviceIntegrationOffTests`, `DeviceCoordinatorConcurrencyTests` (grep). Prior ledgers (disposition, A01, A02, A02S01)
contain nothing on AutoTDP or the device coordinator lifecycle; the reviewer's statement that this domain is new ground
holds.

## Refuted

None of the 42 findings is wholly refuted. Two sub-claims are refuted and recorded under Corrected:

- DEVICE-007, router half: `commandGate.WaitAsync(cancellationToken)` throwing `OperationCanceledException` before
  `PrepareCommand` (DeviceCapabilityRouter.cs:261) is already the right pre-dispatch behaviour: nothing reaches the
  plugin and nothing is recorded in `_pendingValues`, `_lastResults` or `_lastCommandValues`. It is not a defect.
- DEVICE-003, "WSGM exit waits forever": `ApplicationShutdownCoordinator.ShutdownAsync` (Core/ApplicationShutdown.cs:
  118-170) races the session cleanup against the outer budget (15 s normal, 10 Update, 5 SessionEnd, 20 Uninstall)
  and exits with `TimedOut`. The process does not hang. The real consequence is different (see Corrected).

## Corrected

| Id | Correction |
| --- | --- |
| DEVICE-003 | Mechanism confirmed (ShutdownAsync waits the gate with `CancellationToken.None`, awaits `_powerAssignmentTask` and `Task.WhenAll(background)` unbounded, and `ObserveRuntimeCompletionAsync` awaits `client.Completion` that a hung stop never sets). The outcome is not an endless exit. It is that the session's `ShutdownAsync` sits inside device cleanup until the outer budget fires, so every later step is skipped: common plugins, GPU routers, tray retirement and Explorer recovery, Steam host, managers (ShellSession.Shutdown.cs:138-210). A worse, unmentioned hang point sits earlier: `ControllerManager.ReleaseAsync` awaits `releasePhysicalAsync` (ControllerManager.cs:845-852) and `DevicePluginRuntime.ReleaseControllerAsync` awaits `Plugin.ReleaseControllerAsync` directly (DevicePluginRuntime.cs:488-490). Neither uses `WaitAsync`, so a plugin that ignores cancellation in its release blocks `ShowPhysicalUnderGateAsync` and leaves the physical pad hidden after exit. That breaks "never strand users". The runtime's emergency `Plugin.StopAsync` in `DisposeAsync` (99-101) is unbounded in the same way. Severity stays high. The fix needs the input/controller domain (D02) for the release lane. |
| DEVICE-004 | Downgrade to low. Only two production callers exist, and they are mutually exclusive: `ShellSession.cs:342` disposes a coordinator that was never adopted, and `ShellSession.Shutdown.cs:154` shuts down the adopted one and nulls the field. No production path issues a concurrent second shutdown. The single-task contract is still worth having, but the claimed race does not occur today. |
| DEVICE-005 | The stated mechanism is wrong. The ledger is also consumed by `ControllerManager.DisposeAsync` → `ShowPhysicalUnderGateAsync` (ControllerManager.cs:178-179), which the coordinator calls on every shutdown (DeviceCoordinator.cs:851-854), with integration off too, and by `ShowPhysicalControllerAsync` after restart exhaustion (1349). The real gap: after a crash, a session that runs no cycle (integration off, no package, two packages, passive) keeps the pad hidden for that whole session and frees it only at the next WSGM exit. Severity medium stands. B7's acceptance must test start, not exit. |
| DEVICE-006 | Downgrade to low. Packages serialize commands per device (`DeviceCommandSerializer`), so a queued B normally completes after A's late result, and `ReconcileResult(B)` then overwrites the stale state. The bad final state needs B to finish before A's late completion. A late result after a new descriptor set finds `_lastCommandValues` cleared, so `PreviousResultUncertain` cannot fire. The defect is real but narrow. |
| DEVICE-007 | Downgrade to low and keep only the runtime half: cancellation that lands between router gate acquisition and `Plugin.ExecuteCommandAsync` (DevicePluginRuntime.cs:177-197) is reported `Indeterminate`. That window is microseconds. Mid-flight cancellation reporting `Indeterminate` is truthful. The router half is refuted (above). |
| DEVICE-008 | Upgrade PLAUSIBLE → CONFIRMED and extend the scope. Traced: a user PL2 write persists `BoostWatts` (2209-2214) and a device entry (2217 → `CapabilityUserWrites.PersistAsync`), the router resolves the device entry as PL2 desired (DeviceCapabilityRouter.cs:734, 780-806), the restore pass writes it (2562-2585), and "Use global" clears only `BoostWatts` (DeviceOverlayBridge.cs:1224-1225, 583-590). There is also a third persistent representation: when a power-preset assignment is in force, an out-of-band watt change is adopted as a `custom` assignment with Sustained/Slow values (DevicePowerAssignments.cs:221-233), and `HasCurrentAssignment` then suppresses the per-app power reconcile (ApplicationPerformanceReconciler.cs:102-103). The single-home design and the migration must cover all three. See the B5 problems below. |
| DEVICE-010 | Downgrade to low and change the recommendation. The shared policy already exists as the static `CapabilityUserWrites`. What remains duplicated is about 25 lines per coordinator (GpuCoordinator.cs:301-334 vs DeviceCoordinator.cs:2184-2223). Move `PerformanceProfileOwnsRole` plus a `HandleUserWriteAsync` helper into `CapabilityUserWrites`. A new per-publisher `CapabilityCommandPolicy` class adds mechanism that no defect justifies (simplify rule). |
| DEVICE-011 | Downgrade to low. The races are real (`_cpuBoostImposed` written outside `_cpuBoostGate` at 261/267/309, read at 348; `_profilePowerImposed` written from the manual hook at 399), but the worst effect is one wrong "imposed" flag that decides whether the next app change releases a value. |
| DEVICE-012 | Confirmed and stronger than stated: `Applied(int)` also returns false for an `AppliedVerified` result whose `ReadbackValue` is null, not only for a mismatch (CommandOutcomeExtensions.cs). |
| DEVICE-031 | The list is incomplete. Add `PluginSettingsCoordinator.cs:152` (`_ = Task.Run`) and `:211` (`_ = PublishAndPushAsync`), and `DevicePluginRuntime.cs:215` (`_ = RemoveCommandWhenCompleteAsync`). The OEM router also reads `_lifetime.Token` outside its lock (229) while `Dispose` may already have disposed it (88-89). |
| P14 (plan claim) | Partially refuted (see DEVICE-007): throwing at the router gate is the correct pre-dispatch shape. Only the runtime window is inaccurate. |

## Confirmed (ids only)

DEVICE-001, DEVICE-002, DEVICE-009, DEVICE-013, DEVICE-014, DEVICE-015, DEVICE-016, DEVICE-017, DEVICE-018,
DEVICE-019, DEVICE-020, DEVICE-021, DEVICE-022, DEVICE-023, DEVICE-024, DEVICE-025, DEVICE-026, DEVICE-027,
DEVICE-028, DEVICE-029, DEVICE-030, DEVICE-032, DEVICE-033, DEVICE-034, DEVICE-035, DEVICE-036, DEVICE-037,
DEVICE-038, DEVICE-039, DEVICE-040, DEVICE-041, DEVICE-042.

Plan-claim rows confirmed: P1, P2, P3, P4, P5, P6, P7, P8, P9, P10, P11, P12, P13, P15, P16, P17, P18, P19, P20, P21,
P22, P23.

Verification notes on the most important confirmations:

- DEVICE-001: `PluginCategoryPolicy.Device = new(0, 1, true)` (WSGM.Plugin.Sdk/PluginManifest.cs:46). After an
  `Unverified` or `Failed` stop, `DevicePluginRuntime.StopAsync` sets a non-null `Reason` (414-424). The adapter then
  returns `false` (DevicePluginCompatibilityAdapter.cs:75), `PluginRegistration.DisposeAsync` skips `Retire`
  (PluginHost.cs:520-523), and every later `Admit` throws at PluginHost.cs:87-91 because the Device slot is full. The
  fault restart, the post-sleep restart, integration off/on, the controller-management fallback restart and Retry all
  fail. Packages return `Unverified` routinely (DeviceServiceLifecycle.cs:272-281; AllyServices.cs:140, 272;
  ClawServiceBase.cs:63). A timed-out `registration.StopAsync` (`_stopFailure` set) or a stop that never entered
  (`_stopAttempted` false, so dispose throws at 498-501) blocks the slot the same way.
- DEVICE-013: ConfigStore.cs:863 (`> 128`, `> 64`), 874 (`> 128`) and 598-600 (truncates to 48) are as stated.
  ConfigStore.cs:684-686 truncates game profile names to 80 on load in the same way (config domain, same rule).
- DEVICE-016: the server loop (DeviceCoordinatorDiagnostics.cs:72-106) has no delay and starts in the constructor.

## Missed findings

### DEVICE-V-001 (high): AutoTDP stops working for the rest of the session after any lock/unlock, sleep or device restart, and the exit restore is skipped

- AutoTdpService.cs:162-169: `Availability` reports "The previous power owner must be restored before control can
  resume" whenever `_restoreTo` is set and `_restoreCycle` differs from the power capability's current
  `CycleGeneration`. `_restoreTo`/`_restoreCycle` are set on the first automatic write of a generation (894-913) and
  cleared only by a successful `StopAsync` (1093-1097) or a first-write `Rejected` (946-954).
- `StopAsync` refuses to restore when the cycle differs (1032-1040) and returns without clearing the obligation. The
  latch cannot be cleared from inside the session.
- The cycle generation advances on every `DeviceCoordinator.ResumeAsync` (674), and that runs on every session unlock
  as well as every wake (ShellSession.Power.cs:40-45, 67-74), and on every fault or post-sleep restart (1027).
- Result: a user with AutoTDP on locks the screen or sleeps the device once. From then on every tick publishes
  Unavailable (651-661), switching it off restores nothing, and `SetAutoTdpEnabledAsync(true)` throws
  (DeviceCoordinator.cs:1988-1993) until WSGM restarts. At exit, `StopAsync` hits the same check and leaves AutoTDP's
  last wattage on the device. That is the exact failure the shutdown ordering comment says was fixed
  (ShellSession.Shutdown.cs:111-115). No test advances the cycle after a write (AutoTdpServiceTests inventory).
- This guard has no defect to justify it, and HC restores the original TDP without any cycle identity, which also
  violates the "simplify" rule.
- Recommendation: remove `_restoreCycle`/`_restoreCapability` from both checks. The restore obligation is just the
  original watts (and the pair's original), written through whichever primary power capability is published at stop
  time. If none is published, log once and keep the value. This also collapses DEVICE-026's record. Tests: enable,
  write once, advance the cycle generation, then assert `Availability.Available`, that a tick writes, and that
  disposal restores the original once. Land it as a small standalone fix ahead of DEVICE-B6.

### DEVICE-V-002 (medium): a failed resume after a session unlock leaves the device quiesced and controller forwarding blocked

- DeviceCoordinator.cs:683-699: only `afterSystemSleep` failures are caught and turned into a fresh cycle. On unlock,
  the exception propagates past `SetState` and `Controllers.ResumeForwardingAsync` (701-703). Forwarding was blocked at
  lock (627). ShellSession.Power.cs:243-254 only logs and marks `_deviceSuspended = true`.
- The plugin has closed its devices (suspend), the virtual pad receives nothing, and nothing changes until the next
  lock, sleep or Retry. On a handheld that means no controller input on the desktop.
- Recommendation (remove a branch, not add one): handle a failed resume the same way whatever the trigger, by
  restarting the cycle as the sleep path does. Test: unlock-resume throwing leads to one restart and forwarding resumes.

### DEVICE-V-003 (low): a passive detection keeps a live runtime and registration; every lock and sleep then errors and restarts the package

- When detection does not match, `runtime.StartAsync` returns `Passive` without `Plugin.StartAsync`
  (DevicePluginRuntime.cs:302-305). The coordinator keeps `_client`, the registration and the supervision task anyway
  (DeviceCoordinator.cs:1047-1089).
- On lock or sleep, `SuspendAsync` → `runtime.SuspendAsync` → `EnsureLifecycleActive` throws "not active" (344, 540-543).
  The registration is quarantined (PluginHost.cs:611-613) and an error is logged. On unlock or wake, `DecideResume`
  returns Restart, which reloads the package.
- On stop, `runtime.StopAsync` calls `Plugin.StopAsync` on a plugin that never started (400-411, unlike `DisposeAsync`,
  which checks `_pluginStartAttempted`, 95). `ReleaseControllerAsync` throws "not active", so every passive teardown
  reports unverified.
- Recommendation: treat Passive like no package. Dispose the runtime after the detection result, keep no client, and
  set the state to Passive. Guard `StopAsync` with `_pluginStartAttempted`. Test: a passive fixture suspends and
  resumes with zero plugin calls and no restart.

### DEVICE-V-004 (low): OEM router carries a dead generation field and a stale design comment

- `DeviceOemActionRouter._cycleGeneration` (70) is written in `Attach`/`Reset` (109, 137) and never read. No event is
  checked against a generation. The coordinator comment at DeviceCoordinator.cs:1657-1659 and the
  `_oemActions.Reset(activeGeneration)` call (1680) describe a check that no longer exists.
- Recommendation: delete the field and the generation parameter of `Reset`, and fix the comment. No behaviour change.

### DEVICE-V-005 (low): the restore pass's per-candidate re-snapshot is deliberate; the reviewer's "one snapshot per pass" would replay an obsolete profile

- CapabilityDesiredReconciler.cs:79-85 re-resolves each candidate because "a preceding command can take seconds", so an
  application or profile change during a pass is honoured. DEVICE-021 and DEVICE-B5 ("one snapshot per pass") would
  remove that and write values from the old application's layer.
- Recommendation: keep one ordered snapshot for the ordering, and use the router's `TryGetView(key)` per candidate for
  the fresh read. That still removes the O(n²) full rebuild without dropping behaviour.

## Batch problems

1. DEVICE-B1 bundles the live DEVICE-001 defect (no device restart after an unverified release, reachable on the Xbox
   Ally X through routine release paths) into a 600-line structural batch with a new worker pattern. Ship a small fix
   first, HC model: the device's release counts as done once the runtime stop returned, whatever its status (adapter
   `StopAsync` returns true after `runtime.StopAsync` returns, or the device registration retires on completed
   disposal). Then B1 deletes the adapter. B1 also introduces a `Task.Run` worker per lifecycle call into the runtime.
   Keep it to lifecycle calls only, never commands or haptic frames (high-rate rule).
2. DEVICE-B2's "gate-wait cancellation returns Rejected" changes behaviour visibly. Today a caller cancelled before
   dispatch gets an exception and nothing is recorded. Returning a `Rejected` result goes through
   `ReconcileResult(key, refusal)` and records it as the last result, which shows as `CommandProgress.Failed` on the row
   (DeviceCapabilityRouter.cs:888-898). It also changes caller flows: in AutoTDP, the cancelled-write path
   (AutoTdpService.cs:788-799) becomes the "did not accept the last write" status path, and the restore-obligation
   clearing at 946-954 would fire. `DevicePowerPresets` loses its "Preset selection was cancelled" status (165-173).
   That violates "UI unchanged". Keep the pre-gate exception. Only the runtime's post-gate/pre-dispatch check is
   needed.
3. DEVICE-B3 cannot meet its own test ("hung plugin stop returns at the deadline") while
   `ControllerManager.ReleaseAsync` and `DevicePluginRuntime.ReleaseControllerAsync` await plugin code without a bound
   (Corrected, DEVICE-003). Add the D02/input-domain dependency for a bounded physical release that keeps the HidHide
   show step reachable, or the batch only moves the hang. The DEVICE-004 part has no production trigger; keep it small.
4. DEVICE-B5 silently drops behaviour in two places:
   - "One snapshot per pass" removes the deliberate re-resolution (DEVICE-V-005).
   - Making PL2 performance-owned loses PL2-only persistence. `PersistManualBoostAsync` stores `BoostWatts` only when
     `ManualTdp()` is non-null (DeviceCoordinator.cs:2248-2251; ProfileResolver.cs:121-133), and the reconciler
     restores `BoostWatts` only in the split-pair branch with a sustained target (ApplicationPerformanceReconciler.cs:
     129-153). A user who only ever moved the boost slider would have the value persisted and restored today (device
     entry plus restore pass) and nowhere after B5.

   B5 must make boost persistence unconditional and restore `BoostWatts` on its own. Its migration fixtures need a
   PL2-only layer (no sustained, no `BoostWatts`) and a layer carrying a `custom` preset assignment (DEVICE-008
   correction). Replace the new `CapabilityCommandPolicy` class with additions to the existing `CapabilityUserWrites`
   (DEVICE-010 correction, simplify rule).
5. DEVICE-B6 is the only batch that touches AutoTDP restore state, but DEVICE-V-001 should not wait for a 1500-line
   ownership move. Land it before B6 as its own small fix with the cycle-advance test. B6 must keep AutoTDP's exit
   restore ahead of device stop: once AutoTDP is owned by `PowerLimitOwner` inside the coordinator, the order
   "AutoTDP restore, then coordinator teardown" (ShellSession.Shutdown.cs:111-136) has to become an explicit first step
   of the coordinator's shutdown task.
6. DEVICE-B7: `HidHideOwnership.ShowAsync` turns the cloak off whenever it is active, even with an empty ledger
   (HidHideOwnership.cs:306-325), so the "ledger non-empty" precondition must be checked by the caller before
   `ShowAsync`, as the batch says. Its test must run at start, because the exit path already consumes the ledger
   (DEVICE-005 correction).
7. DEVICE-B8's file list is incomplete. `tests/WSGM.UiTests/Fakes/FakeDevice.cs` implements `IDeviceOverlaySource`
   and relies on both default members, so removing the defaults breaks the UiTests build unless that fake is edited in
   the same batch. `DeviceWidgetSource` consumes the interface but does not implement it; it needs no change.
8. DEVICE-B9 covers the device-config caps but not the identical load-time truncation of game profile names
   (ConfigStore.cs:684-686). Hand it to the config domain under the same no-arbitrary-limits rule so the two are fixed
   together.
9. Ordering: the reviewer's sequence B1→B8 puts the two user-visible defects (DEVICE-001, DEVICE-V-001) behind large
   structural moves. Recommended order: fix DEVICE-001 and DEVICE-V-001 first, then DEVICE-V-002, then B1 (alongside
   A02_02), then B2-B8 as planned. Tests deferred everywhere (read-only stage): every filter listed in device.md plus
   new cases for DEVICE-V-001..003.
