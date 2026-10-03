# Device domain review: coordination, capability routing, plugin runtime/host, AutoTDP, performance, power

Reviewer: Claude (Opus 5.5), read-only stage. Baseline `master` 1329813f. No build, test, git mutation or live action was run.

Files read in full: `Shell/DeviceCoordinator.cs`, `DeviceCapabilityRouter.cs`, `DevicePluginRuntime.cs`, `PluginHost.cs`,
`DevicePluginCompatibilityAdapter.cs`, `DeviceOverlayBridge.cs`, `SimulatedDeviceOverlaySource.cs`, `AutoTdpService.cs`,
`AutoTdpTraceRecorder.cs`, `ApplicationPerformanceReconciler.cs`, `PerformanceOverlayBridge.cs`, `DevicePowerPresets.cs`,
`DevicePowerAssignments.cs`, `DeviceCoordinatorDiagnostics.cs`, `DeviceDesiredWriteAdmission.cs`, `DeviceLightingRestore.cs`,
`DeviceOemActionRouter.cs`, `DevicePrerequisiteSource.cs`, `DeviceProfileApplier.cs`, `CapabilityDesiredReconciler.cs`,
`CapabilityUserWrites.cs`, `CommandOutcomeExtensions.cs`, `ICapabilityPublisher.cs`, `ShellSession.Performance.cs`,
`Core/PerformanceService.cs`, `Core/AutoTdp.cs` (structure and public surface; policy constants not retuned),
`Core/AutoTdpTrace.cs` (outline), `Core/DevicePowerPresetReference.cs`, `Interop/EffectivePowerModeNotification.cs`,
`Interop/Windows{PowerMode,PowerScheme,HybridCore,CpuBoost}Api.cs`; relevant parts of `ShellSession.cs` (composition 195-625),
`ShellSession.Shutdown.cs` (95-200), `GpuCoordinator.cs` (275-415), `HidHideOwnership.cs`, `ConfigStore.cs` (normalizers),
`DeviceServiceLifecycle.cs`, Ally/Claw release paths. Tests read: `DeviceCoordinatorConcurrencyTests`, `PluginHostTests`,
`DevicePluginRuntimeTests` (first 110 lines), `DeviceIntegrationOffTests`, test inventories of `AutoTdpServiceTests`,
`DeviceOverlayBridgeTests`, `DevicePowerAssignmentsTests`, `DevicePowerPresetsTests`, `PerformanceServiceTests`,
`DeviceLightingRestoreTests`, `DeviceCapabilityRouterTests`.

Prior evidence: the Claude ledger has no unit for this domain (U06A/U06B/U06C/U11A were among the 42 unstarted parts; only
U05-LFB-001, -004, -006, -013, -019 and U03A-SUTS-005 touch it). Codex A01/A02 did not audit these files. Almost every
finding below is therefore NEW. Ids in other ledgers are cited where they overlap.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence and correction |
| --- | --- | --- | --- |
| P1 | "DeviceCoordinator is 2,979 lines" (plan, Source diagnosis) | accurate | `wc -l` = 2979. |
| P2 | "Its private constructor creates the router, power assignments, plugin settings, diagnostics, haptic sink, VIIPER backend, native HidHide control, real ledger and power notification" | partially | DeviceCoordinator.cs:115-166. Also creates `DevicePowerPresets` over the static `WindowsPowerModes.Windows` (134), `DeviceOemActionRouter`, `DeviceLightingRestore` (67-68), `ControllerProcessPriority`, makes the native call `NativeHidHide.FromDosPath(Environment.ProcessPath)` (151-153), starts the background `ObservePowerAssignmentsAsync` loop (156) and the diagnostics named-pipe server (142; the server starts its loop in its own constructor, DeviceCoordinatorDiagnostics.cs:55). All of this runs with Device Integration off. |
| P3 | "It owns cycle admission, settings, profiles, recovery, command policy, controller publication and power reconciliation together" | accurate, incomplete | Add: config persistence through static `ConfigStore.Mutate` (2065-2075), AutoTDP switch persistence (1981-2010), authored fan-profile selection/apply (2426-2521), glyph catalog loading (2701-2730), lighting-restore scheduling (2832-2854), manual TDP/boost persistence (269-286, 2243-2267). |
| P4 | Target: `DeviceCoordinator` = `DeviceCycleOwner`, `DeviceCommandPolicy`, `DevicePowerAssignmentOwner`, `DeviceControllerHandoff` | partially | `DevicePowerAssignments` already is that owner (DevicePowerAssignments.cs:45); a new `DevicePowerAssignmentOwner` would duplicate it. The real missing owner is a single power-limit owner (DEVICE-009). `DeviceCommandPolicy` must be publisher-generic because GpuCoordinator duplicates it (DEVICE-010). Add `DeviceDesiredStateRestorer` (restore pass, lighting, authored profiles). See section 4. |
| P5 | Target: `DeviceCapabilityRouter` split into `CapabilityCatalog` + `CapabilityCommandLane` | inaccurate (over-engineering) | Descriptors, states, pending values and last results share one lock and `PrepareCommand` must read catalog, state, freshness and record the pending value atomically (DeviceCapabilityRouter.cs:366-505). Two classes would need a shared lock or a new protocol between them. Keep one router; move `DeviceCapabilityValidation` (1036-1380) to its own file, inject the clock, fix late completion (DEVICE-006). |
| P6 | "common GPU routers stay independent" | accurate | One router per publisher (DeviceCapabilityRouter.cs:47-51; GpuCoordinator uses `publisher.Router`). Command policy is not independent and should be shared (DEVICE-010). |
| P7 | Preserve `DeviceDesiredWriteAdmission`, `DeviceServiceLifecycle`, `DeviceCommandSerializer`, `DeviceRecoveryJournal` | accurate | These are small, used and correct in this domain. |
| P8 | "Device Integration off keeps the coordinator/reservation ...; never calls device Start/Resume/command, constructs a controller target or starts AutoTDP" | partially | Start is skipped (DeviceCoordinator.cs:475-483, 926), resume skips without a client (DecideResume 761-766), AutoTDP is gated (`ShouldRunAutoTdp`, ShellSession.Performance.cs:111-114). But the coordinator still registers the Windows power-mode notification, starts the diagnostics pipe and power-assignment loop, and `AutoTdpService` is constructed whenever a coordinator exists (ShellSession.cs:497-519). A stale HidHide ledger is never consumed when no cycle starts (DEVICE-005). |
| P9 | "Two installed device packages refuse device integration with a clear diagnostic" | accurate | `PackageDiscovery` cardinality, passive state (DeviceCoordinator.cs:1011-1023) and per-file diagnostic rows (DeviceOverlayBridge.cs:442-455). |
| P10 | "A missing device package is a valid device-disabled composition, not a startup failure" | accurate | `DeviceCycleState.Passive` with `no-package-installed` (DeviceCoordinator.cs:1013-1023). |
| P11 | Shutdown order "restore AutoTDP through the still-live device command lane; ... stop device/common plugins; dispose GPU routers" | accurate as current order | ShellSession.Shutdown.cs:108-189 disposes AutoTDP, then the coordinator, common plugins, GPU. Budgets are not enforced (DEVICE-003, DEVICE-027). |
| P12 | "Never unload a plugin/driver library or dispose its semaphore under an active call" | partially | Runtime unloads only when `canUnload` (DevicePluginRuntime.cs:85-143); router never disposes per-key gates (DeviceCapabilityRouter.cs:143-145). The coordinator avoids disposing its gate under use only by waiting forever (DEVICE-003), which is a hang instead of a retained handle. |
| P13 | B3: single outer deadline with phase cutoffs at 30/50/70/90 percent and busy-owner table | partially (binding, but over-specified for this domain) | The concrete defect it answers is real here (DEVICE-003). In this domain the minimal shape suffices: every stop method takes the outer `Deadline`, never awaits past it, and retains (not frees) work that missed it. No phase scheduler class. See section 3. |
| P14 | "Commands have explicit pre-dispatch, dispatched and completed phases. Cancellation before dispatch is Rejected/Cancelled with no write" | inaccurate for current code | A pre-cancelled command still calls `Plugin.ExecuteCommandAsync` and is reported `Indeterminate` (DevicePluginRuntime.cs:177-219, 714-729); the router's gate wait throws `OperationCanceledException` instead of returning Rejected (DeviceCapabilityRouter.cs:261). DEVICE-007. |
| P15 | "Late completion requires the same runtime instance, command ID, cycle and descriptor generation" | inaccurate for current code | `ObserveLateCommandAsync` checks runtime, cycle and command id, not descriptor generation and not whether a newer command for the same key is in flight (DeviceCapabilityRouter.cs:648-674). DEVICE-006. |
| P16 | "Write success publishes the written value as Observed even when readback is absent/mismatched ... Readback never gates supported writes or controls" | partially | `CanCommand` needs no readback (DeviceCapabilityRouter.cs:1002-1009), presets do not wait for readback (DevicePowerPresets.cs:110-111, 147-149). But `Applied(int)` treats a verified mismatching readback as not applied for every paired write (CommandOutcomeExtensions.cs, used at AutoTdpService.cs:941-943, 1074; ApplicationPerformanceReconciler.cs:501; DeviceCoordinator.cs:2153, 2163). DEVICE-012. |
| P17 | "High-rate samples/haptic frames remain structs and latest-wins, with no per-sample allocation or logging" | accurate | `CanonicalControllerSample` is a `readonly record struct`; `Raise` uses `Delegate.EnumerateInvocationList` (struct enumerator) and logs only on a throwing handler (DevicePluginRuntime.cs:785-805, 978-986). |
| P18 | ShellSession "never joins `_devicePowerWork`" (plan; U05-LFB-001) | accurate | Only ShellSession.Power.cs:208-209 and ShellSession.Modes.cs:619 touch it. |
| P19 | "Replace `PluginTrace.Install` global routing with instance `PluginDiagnostics`" | accurate diagnosis | Packages install it (ClawPlugin.cs:113, RogAllyPlugin.cs:126); the host clears it on unload (DevicePluginRuntime.cs:133). |
| P20 | "Replace global ActiveClock ... `Deadline.After/At` take an explicit clock" | accurate, consumer impact | Domain consumers: DeviceCoordinator (`Deadline.After` at 618, 672, 1066, 1212, 1603, 1616, 1689, 1712, 1718), router (386), runtime (89, 286), PluginHost (491). The runtime's per-command `CancelAfter(command.Deadline.Remaining)` (DevicePluginRuntime.cs:831-839) has the same wall-timer defect as A02-F006. |
| P21 | Plan D01 objective: "Preserve reserved singleton device owner even integration off" | accurate intent; mechanism wrong | The reservation that matters is the machine mutex (`Global\WSGM.DeviceOwner`, DeviceCoordinator.cs:56, 492-522). The second reservation in PluginHost is redundant and causes DEVICE-001. |
| P22 | Plan D03 objective: "Separate AutoTDP algorithm, generation runner, command writer and trace projection" | partially | `AutoTdpController` (Core/AutoTdp.cs:205) is already pure and replayable; trace projection is already separate (`AutoTdpTraceRecorder`, `AutoTdpTraceCsv`). Splitting `AutoTdpService` further adds classes without a defect. The useful change is ownership: AutoTDP is constructed and owned by the power-limit owner, and its four restore fields become one record (DEVICE-026). |
| P23 | A02_02 (journal latch, SDK) touches this domain through consumers | accurate | `File.Exists` fast return is at DeviceRecoveryJournal.cs:230 and `ThrowIfUnavailable` only before the gate (109, 145). Note: once a journal latches, the package's release returns Faulted, stop is `Failed`, and DEVICE-001 then blocks every restart. Land DEVICE-001's fix with or before A02_02. |

## 2. Findings

Severity scale: critical / high / medium / low / nit. "Verdict" CONFIRMED = traced in code; PLAUSIBLE = traced, depends on a
runtime path not exercised here.

### DEVICE-001 (high, CONFIRMED, NEW): an unverified or failed device stop permanently blocks every device restart

- `DevicePluginCompatibilityAdapter.StopAsync` records `_released = LastState.Reason is null` (DevicePluginCompatibilityAdapter.cs:74-76). `DevicePluginRuntime.StopAsync` sets a non-null Reason for `PluginStopStatus.Unverified` and `Failed` (DevicePluginRuntime.cs:414-427), which packages return whenever a restore did not read back or a service faulted (DeviceServiceLifecycle.cs:258-282; AllyServices.cs:138-142, 270-273; ClawServiceBase.cs:63).
- `PluginRegistration.DisposeAsync` retires the slot only when `_released is true` (PluginHost.cs:518-523); `PluginHost.Admit` throws when the identity or the Device category slot is still reserved (PluginHost.cs:84-92). The coordinator admits the same identity `(packageId, "device")` on every cycle (DeviceCoordinator.cs:1062-1065).
- Result: after one unverified teardown, fault recovery (1256-1279), the post-sleep restart (712-737), integration off/on, the controller-management fallback restart (1642-1647) and the user's Retry (774-793) all throw at Admit, run through `ScheduleStartFault`, and end Faulted until WSGM restarts. The coordinator comment at 1256-1258 says the opposite is intended ("restarting anyway", HC's Close ignores its results). `PluginHostTests.UnconfirmedStopIsNotRetriedAndContinuesReservingTheSlot` (PluginHostTests.cs:136-148) pins the host side; no test covers the device combination.
- Violates the no-hard-readback rule (success of a restart gated on release readback). A02_02 increases reachability (P23).
- Recommendation: remove the device's PluginHost admission entirely (DEVICE-002). The runtime itself retains its load context when a call did not return (`canUnload`), which is the only retention B3 needs.

### DEVICE-002 (medium, CONFIRMED, NEW): three serial lifecycle lanes and a "compatibility adapter" for the device runtime

- `DeviceCoordinator._transitionGate` (83), `PluginRegistration._lifecycle` with a `Task.Run` worker, its own deadline, quarantine and a fixed 5 s dispose budget (PluginHost.cs:243, 489-527, 548-621), and `DevicePluginRuntime._lifecycleGate` (28) serialize the same calls. The adapter exists only to fit `IPlugin` (DevicePluginCompatibilityAdapter.cs:11-139); `SessionChangedAsync` is a no-op for device (42-47).
- What PluginHost adds for device: a slot reservation already guaranteed by the machine mutex, the coordinator's single `_client`, `PackageDiscovery` cardinality and `CommonPluginPackage` refusing the Device category (CommonPluginPackage.cs:151); health publications that no consumer reads for device (CommonPluginSteamUiSource only refreshes on any change, GpuCoordinator filters to its own publishers); and the `Quarantined` input to `DecideResume` (658), which the runtime's own Suspended check already covers.
- Recommendation: delete `DevicePluginCompatibilityAdapter`; `DeviceCycle` drives `DevicePluginRuntime` directly. Move the one useful PluginRegistration behaviour into the runtime: a lifecycle call runs on a worker that owns the gate until the plugin actually returns, while the caller stops waiting at the deadline (PluginHost.cs:559-588 pattern). `DecideResume` drops `registrationUsable`.

### DEVICE-003 (high, CONFIRMED, NEW; plan B3 covers the principle): device shutdown can hang forever on a plugin that ignores cancellation

- `ShutdownAsync` takes a `Deadline` but waits the transition gate with `CancellationToken.None` (DeviceCoordinator.cs:809, 1147-1155), awaits `_powerAssignmentTask` (811) and `Task.WhenAll(background)` (833-842) with no bound.
- `background` contains `ObserveRuntimeCompletionAsync`, which awaits `client.Completion` without a token (1219). Completion is set only by `StopAsync` success, `ReportPluginFault` and the end of `DisposeAsync`. With a plugin hung in `StopAsync`: PluginRegistration keeps its lane (correct), so `registration.DisposeAsync` times out after 5 s and never calls the runtime's `DisposeAsync`; Completion is never set; WSGM exit waits forever. Two more paths skip `Complete`: the runtime's own gate-wait timeout throws before its `finally` (DevicePluginRuntime.cs:74-83) and an unknown stop status throws after `_stopped = true` (414-428).
- Independent fixed budgets also bypass the outer deadline: runtime `EmergencyCleanupBudget` 5 s (20, 73), registration dispose 5 s (PluginHost.cs:491), coordinator `NormalShutdownDeadline()` 15 s used inside `ShutdownAsync`'s fallbacks (1601-1604), `CanceledStartCleanupBudget` 5 s (61).
- Recommendation: one `Deadline` parameter flows to every awaited step; `Completion` is set in every terminal path; the coordinator never awaits a background task past the deadline and reports it Unverified. With DEVICE-002 the registration hop disappears.

### DEVICE-004 (medium, CONFIRMED, NEW): coordinator shutdown is not single-task and not concurrency-safe

- `_disposed` is a plain bool checked and set without a lock (DeviceCoordinator.cs:800-805). A second `ShutdownAsync` or `DisposeAsync` returns immediately while the first is still releasing hardware; `DisposeAsync` builds its own 15 s deadline (244-249). Plan requires repeated Stop/Dispose to return the same task.
- Recommendation: `Interlocked.CompareExchange` on a stored shutdown task.

### DEVICE-005 (medium, CONFIRMED, NEW): a stale HidHide ledger is never consumed when no device cycle starts

- WSGM's HidHide deltas are journalled before writes (HidHideOwnership.cs:349-355). They are removed only by `ControllerManager` release inside a live cycle (ControllerManager.cs:916) and by the uninstall one-shot (Program.cs:638-639). After a crash, a start with Device Integration off, no package, two packages, or a passive detection leaves the physical pad hidden from every other application. Violates "never strand users".
- Recommendation (needs D02 controller owner): at coordinator start, when no cycle will run and the ledger is non-empty, consume it once through `ShowAsync` (removes only WSGM-owned entries). This is the "next supported recovery start consumes the retained journal" rule of B3.

### DEVICE-006 (medium, CONFIRMED, NEW): a late command completion can overwrite a newer command's progress and admission evidence

- The per-key gate is released when the immediate result returns, even when a late completion is still pending (DeviceCapabilityRouter.cs:285-307), so a second command for the same key can be in flight. When the first completes, `ReconcileResult` removes the newer command's pending value and replaces `_lastResults[key]` (676-701) while `_lastCommandValues[key]` still holds the newer value.
- `DeviceDesiredWriteAdmission` then pairs the old outcome with the new value (DeviceDesiredWriteAdmission.cs:79-84): an old Indeterminate blocks automatic restore of a value that was never uncertain, and an old Applied clears a pending marker early.
- Descriptor generation is not checked (plan P15). The observer task is `_ =` fire-and-forget with no fault handling or shutdown join (288).
- Recommendation: record the latest command id per key; a late result reconciles only if it is still the latest and the descriptor generation matches; track the observer in the router's owner.

### DEVICE-007 (medium, CONFIRMED, NEW): a command cancelled before dispatch is reported Indeterminate

- `ExecuteCommandAsync` creates the linked token and calls `Plugin.ExecuteCommandAsync` even when it is already cancelled (DevicePluginRuntime.cs:177-197); the cancellation is classified as Indeterminate unless the deadline fired (714-729). The router's `commandGate.WaitAsync(cancellationToken)` throws to the caller (261).
- Consequence: a never-sent restore value is marked uncertain and `PreviousResultUncertain` blocks it until the user acts.
- Recommendation: check the linked token before calling the plugin and return Rejected/Cancelled; catch the gate-wait cancellation in the router and return Rejected.

### DEVICE-008 (medium, PLAUSIBLE, NEW): the boost limit (PL2) has two persistent homes

- A user PL2 write persists `BoostWatts` (DeviceCoordinator.cs:2209-2214, 2243-2267) and, because `PerformanceProfileOwnsRole` covers only the sustained limit and VRR (2339-2342), also a device desired value (2217-2222, 2362-2398).
- The overlay's "Use global" clears `BoostWatts` (DeviceOverlayBridge.cs:1224-1225), but the device desired restore pass (2562-2585, priority 2648) keeps writing the game's stored device PL2, while `ApplicationPerformanceReconciler` writes `BoostWatts` through `RestoreSplitPowerAsync`. The comment at 2331-2338 describes exactly this two-homes failure for the other roles.
- Recommendation: add `PowerSlowLimit` to the performance-owned roles; config migration (config domain) moves an existing device PL2 entry into `BoostWatts` of the same layer when that layer has none, and drops it otherwise. Needs a migration fixture.

### DEVICE-009 (medium, CONFIRMED, NEW): power-limit ownership is spread over six owners and five coordination mechanisms

- Writers of the sustained/boost/scenario limits: DeviceCoordinator user funnel and `RestoreSplitPowerAsync` (2088-2169), `DevicePowerPresets` (66-189), `DevicePowerAssignments` (151-267), `ApplicationPerformanceReconciler` (122-209, 477-510), `AutoTdpService` (847-1116), the desired-state restore pass, native QAM TDP and preset services (NativeQamSemanticServices.cs:791, 847, 1101).
- Coordination: a public `SemaphoreSlim MutationGate` borrowed across classes (DevicePowerPresets.cs:30; DeviceCoordinator.cs:2109, 2136); `CapabilityCommandOrigin` side effects (30-51, 2205-2230); post-construction `Attach*` hooks (1935-1959) and a settable `AutomaticPowerOwner` Func (DevicePowerPresets.cs:31; ShellSession.cs:520) nulled at shutdown while commands may run (ShellSession.Shutdown.cs:128-135); the `HasCurrentAssignment` cross-check (ApplicationPerformanceReconciler.cs:103); `_profilePowerImposed/_profilePowerPaired` flags (41-43).
- Implicit lock order: assignments `_gate` -> `MutationGate` -> coordinator `_transitionGate` (SavePowerAssignmentAsync 313-346, PersistManualBoostAsync 2243-2267). No deadlock today, but nothing states or enforces it.
- Recommendation: one `PowerLimitOwner` (section 4) with a private lane; AutoTDP constructed and owned by it; presets and assignments receive the lane and the AutoTDP ownership predicate through their constructors.

### DEVICE-010 (medium, CONFIRMED, NEW): user-write policy is duplicated between the device and graphics coordinators

- `DeviceCoordinator.ExecuteCapabilityCoreAsync` (2171-2241) and `GpuCoordinator.ExecuteAsync` (GpuCoordinator.cs:281-336) both implement: native per-application store instead of command, persist after an applied write, the VRR manual hook, and `PerformanceProfileOwnsRole` (the GPU side calls the device coordinator's static).
- Recommendation: one `CapabilityCommandPolicy` instance per publisher (profile key, router, hooks), used by both owners.

### DEVICE-011 (medium, CONFIRMED, NEW; related U05-LFB-019): unsynchronized state in ApplicationPerformanceReconciler

- `_profilePowerImposed`, `_profilePowerPaired`, `_profileVrrImposed`, `_cpuBoostImposed`, `_cpuBoostBaseline`, `_lastReconciledCpuBoostKey` (34-43) are written from the fan-out worker, from the coordinator's manual hooks (on whichever thread a user command completes, `PersistManualPowerLimit` 389-407), and from UI calls. `_cpuBoostImposed` is written outside `_cpuBoostGate` (309) and read inside it (348).
- Recommendation: power/VRR flags move into the power owner's lane; CPU boost becomes its own small owner whose state stays under its one gate.

### DEVICE-012 (medium, CONFIRMED, NEW): readback gates success for paired power writes only

- `Applied(int)` returns false for `AppliedVerified` whose readback differs from the request (CommandOutcomeExtensions.cs). Paired writes use it, unpaired writes use `IsApplied()` (AutoTdpService.cs:941-943, 1074; ApplicationPerformanceReconciler.cs:501; DeviceCoordinator.cs:2153, 2163). A clamped or late readback turns a delivered write into "not applied", makes AutoTDP resync and report the restore unconfirmed. Conflicts with the no-readback rule and the plan's Observed-on-mismatch contract.
- Recommendation: use `IsApplied()` everywhere; keep the mismatch only as a log detail. Delete `Applied(int)`.

### DEVICE-013 (medium, CONFIRMED, NEW): arbitrary length limits drop or truncate device configuration

- `ConfigStore` drops a power-preset reference whose plugin id exceeds 128 or preset id exceeds 64 characters, and a custom scenario over 128 (ConfigStore.cs:863, 874), although SDK identifiers have no length bound (PlainText.IsIdentifier, Device.Sdk). A valid plugin preset id of 65 characters can never be assigned. `DevicePowerAssignmentsTests.AssignmentLengthLimitsApplyAfterTrimming` (DevicePowerAssignmentsTests.cs:353-383) pins the cap.
- Stored authored-profile names are silently truncated to 48 on load (ConfigStore.cs:598-600; `DeviceAuthoredProfile.MaxNameLength`, DeviceConfiguration.cs:115). The Settings editor cap can stay (UI choice); load-time truncation rewrites user data.
- Recommendation (config domain owns the edit): shape checks only; remove the length caps and the load-time truncation; replace the pinned test.

### DEVICE-014 (medium, CONFIRMED, NEW; pattern of U05-LFB-006): the device coordinator has no behavioural test

- The private constructor builds native objects, so no test constructs a `DeviceCoordinator`. `DeviceCoordinatorConcurrencyTests` exercise static helpers extracted for testing (`RunCancellationSafeStartAsync`, `RunCanceledStartCleanupPolicyAsync`, `RunFreshBoundedCleanupAsync`, `RunClientTeardownAsync`, `CancelLifetimeAndWaitForTransitionAsync`, `DeviceTeardownFailureTracker`) with fakes that perform the compensation being asserted, a constant (`ProductionOwnerName`, 456-460) and a misplaced entry-point test (10-20).
- `DeviceIntegrationOffTests` test predicates and a POCO getter (36-49), never the coordinator with integration off.
- Six `DeviceOverlayBridgeTests` assert the preview data of `SimulatedDeviceOverlaySource`, not production projection.
- Good counter-examples to keep: `DevicePluginRuntimeTests` (real runtime with a fixture plugin), `AutoTdpServiceTests`, `DevicePowerPresetsTests`, `DevicePowerAssignmentsTests`, `PerformanceServiceTests`, `DeviceLightingRestoreTests`.
- Recommendation: after DEVICE-B4, real-owner tests with the fixture runtime and fake ports (section 5).

### DEVICE-015 (low, CONFIRMED, NEW): `DeviceTeardownFailureTracker` has contradictory semantics and adds no behaviour

- `ObserveRuntimeCompletionAsync` clears retained failures unconditionally after an unverified cleanup (DeviceCoordinator.cs:1265); `StopCycleUnderGateAsync` clears only after a verified one (1401-1405); after a sleep they are drained and discarded (724); shutdown rethrows them as one exception that ShellSession only logs. `HasFailures` (2942-2951) is used only by tests.
- Recommendation: delete the tracker; log each unverified step where it happens (HC model, already the effective behaviour).

### DEVICE-016 (low, CONFIRMED, NEW): diagnostics pipe server spins without backoff on a persistent failure

- `RunAsync` loops immediately after an `IOException` or `UnauthorizedAccessException` (DeviceCoordinatorDiagnostics.cs:74-106); a squatted or denied pipe name gives a hot loop and a log flood for the whole session. It starts in the constructor, also with integration off.
- Recommendation: start it from the owner's `StartAsync`; on a creation failure log once and stop (Settings already treats a missing coordinator as "not detected").

### DEVICE-017 (low, CONFIRMED; covered by U05-LFB-013): power-mode callback has no exception boundary and uses process-global statics

- `OnChanged` invokes the managed action with no try/catch (EffectivePowerModeNotification.cs:88-98); the callback table and id counter are static (25-28).
- Recommendation: catch inside the callback. The static id table is the safe pattern for a racing native callback and may stay; ownership moves to the power owner.

### DEVICE-018 (low, CONFIRMED, NEW; plan Configuration section covers the store): the coordinator writes config itself

- `PersistConfigurationAsync` calls static `ConfigStore.Mutate` and replaces its private `_config` (DeviceCoordinator.cs:2065-2075) for AutoTDP enable and glyph selection, so its `_config` diverges from ShellSession's until a reload arrives.
- Recommendation: persist through the session's config port and receive the result through `ApplyConfigAsync` like every other consumer.

### DEVICE-019 (low, CONFIRMED, NEW): setter injection and nullable delegate hooks

- `AttachAutoTdpManualOverride`, `AttachAutoTdpAvailability`, `AttachManualVariableRefreshOverride` (1935-1959), `ConfigureOemActions` (2414-2417) and `PowerPresets.AutomaticPowerOwner` are assigned after construction (ShellSession.cs:519-537, 983) and nulled during shutdown while commands may still run. Fields are read without synchronization.
- Recommendation: constructor injection into the owners in section 4; no hooks.

### DEVICE-020 (low, CONFIRMED, NEW): late-bound service lookup and a dependency cycle

- `ApplicationPerformanceReconciler` takes `Func<DeviceCoordinator?>`, `Func<AutoTdpService?>`, `Func<GpuCoordinator?>` over ShellSession fields (ShellSession.cs:212-213; ApplicationPerformanceReconciler.cs:27-32). AutoTDP writes through the coordinator; the coordinator calls AutoTDP through hooks; the reconciler calls both.
- Recommendation: break the cycle by ownership: the power owner owns AutoTDP and is passed to the reconciler.

### DEVICE-021 (low, CONFIRMED, NEW): repeated full snapshot builds

- `FindCapability`/`FindDescriptor` rebuild the whole router snapshot (with desired resolution and validation) up to five times per command (DeviceCoordinator.cs:2099-2102, 2186-2190, 2211, 2226, 2375, 2401-2411); `CapabilityDesiredReconciler` re-snapshots per candidate, O(n squared) per pass (CapabilityDesiredReconciler.cs:72-81); AutoTDP builds about six per 1 s tick (149-183, 651, 1118-1140); `ManualTdpMode` builds one per read (203-206). Not a high-rate path, but wasteful and noisy.
- Recommendation: router `TryGetView(key)` and one snapshot per operation.

### DEVICE-022 (low, CONFIRMED, NEW): DeviceOverlayBridge mixes Shell, Avalonia and Overlay

- The Shell bridge imports `Avalonia.Threading` only to post AutoTDP status changes (DeviceOverlayBridge.cs:9, 1133-1136); every other source raises on arbitrary threads and the overlay coalesces with `Interlocked` (OverlayWindow.Device.cs:35-37, 110-112), so the post is redundant. The file declares seven types including the overlay contract.
- Recommendation: drop the dispatcher call; move the contract types (`DeviceOverlaySection`, `DeviceOverlayCapability`, `DeviceOverlaySnapshot`, `IDeviceOverlaySource`, `DeviceHostRowIds`, related records) to `DeviceOverlayContracts.cs`.

### DEVICE-023 (low, CONFIRMED, NEW): interface defaults exist to spare the preview source

- `IDeviceOverlaySource.SetHostSelectionAsync` defaults to a `NotSupportedException` task and `UseGlobalAsync` to a silent no-op (DeviceOverlayBridge.cs:281-298). The simulated controller choices list raw enum names, unlike production labels (SimulatedDeviceOverlaySource.cs:319-321).
- UI baselines are captured from the simulated source (DevicePageCaptureTests, ControllerNavigationTests, PreviewExports), so its data must not change.
- Recommendation: implement both members explicitly in each source and remove the defaults; leave preview data unchanged.

### DEVICE-024 (nit, CONFIRMED, NEW): dangling duplicate summary

- SimulatedDeviceOverlaySource.cs:20-21 carries two `<summary>` blocks; the first describes a removed field.

### DEVICE-025 (low, CONFIRMED; library side covered by U03A-SUTS-005): power presets depend on a Steam toolkit type and a magic id

- `DevicePowerPresets.ApplyAsync` returns `SteamUiToolkit.SteamUiCommandResult` (DevicePowerPresets.cs:6, 66); "custom" and "custom-values" ids are host sentinels (74; DevicePowerPresetReference.cs:34-38; DevicePowerAssignments.cs:78-80, 234).
- Recommendation: a host result record (`PowerPresetApplyResult`); the Steam adapter maps it. Keep the persisted "custom" id for migration compatibility.

### DEVICE-026 (low, CONFIRMED, NEW): AutoTdpService state sprawl and unclear dependency ownership

- The restore obligation lives in five fields (`_restoreTo`, `_restorePair`, `_restoreCycle`, `_restoreCapability`, `_powerMayDiffer`, AutoTdpService.cs:90-100) reset in three places (950-953, 1094-1098). It disposes an injected frame-time source (228) and its trace recorder disposes an injected context (AutoTdpTraceRecorder.cs:96).
- Recommendation: one `AutoTdpRestorePoint?` record; the composition root that creates a dependency disposes it.

### DEVICE-027 (low, CONFIRMED; plan B3 covers): AutoTDP disposal is unbounded

- `DisposeAsync` awaits the last stop and a restore of up to three 5 s writes with `CancellationToken.None` (AutoTdpService.cs:209-219, 1057, 1073); ShellSession awaits it with no deadline (ShellSession.Shutdown.cs:115-136).
- Recommendation: `StopAsync(Deadline)`; on expiry leave the restore obligation logged and the write in flight (never a second write).

### DEVICE-028 (low, CONFIRMED; same defect as A02-F006): per-command wall timer for an active-time deadline

- `CommandOperation` uses `CancelAfter(command.Deadline.Remaining)` (DevicePluginRuntime.cs:831-839). A command spanning a sleep times out on wake. Fix with the F02 clock-owned registration.

### DEVICE-029 (low, CONFIRMED, NEW): runtime calls outside the lifecycle lane

- `ApplySettingsValuesAsync` checks admission then calls the plugin outside `_lifecycleGate` (DevicePluginRuntime.cs:444-450), so it can overlap Stop/Suspend. `ApplyHapticOutputAsync` and `ExecuteCommandAsync` read the non-volatile `_cycleState` (172, 456).
- Recommendation: settings application joins the lifecycle lane; `_cycleState` read with `Volatile.Read`. Haptic frames stay outside any lock (high-rate rule).

### DEVICE-030 (low, CONFIRMED, NEW): unknown stop status leaves the runtime half-stopped

- `InvalidDataException` is thrown after `_stopped = true` and before `PublishLifecycle`/`Complete` (DevicePluginRuntime.cs:413-428). Feeds DEVICE-003. Map unknown status to Failed instead of throwing.

### DEVICE-031 (low, CONFIRMED, NEW): untracked fire-and-forget work in the domain

- `_ = DispatchAsync` in the OEM router (DeviceOemActionRouter.cs, OnEvent end), `_ = ObserveLateCommandAsync` (DeviceCapabilityRouter.cs:288), `_ = restore.ContinueWith` (DeviceCoordinator.cs:2823-2824), reconciler `Save` continuations (ApplicationPerformanceReconciler.cs:451-459), `_ = RefreshCpuBoostAsync()` (PerformanceOverlayBridge.cs:168). None is joined at shutdown.
- Recommendation: each owner tracks its own work and joins it within its stop deadline.

### DEVICE-032 (low, CONFIRMED, NEW): lighting restore runs a second restore mechanism

- A readiness-triggered lighting pass with a scheduling flag, a user-command counter with an Abandon hook, and a per-zone attempt budget of 3 (DeviceCoordinator.cs:2576, 2832-2854; DeviceLightingRestore.cs:21). The budget answers a documented defect (refused writes right after wake) and only retries Rejected writes, so it does not violate the no-uncertain-retry rule.
- Recommendation: keep behaviour; move into `DeviceDesiredStateRestorer` so the one restore owner holds both passes. Do not add further mechanism.

### DEVICE-033 (low, CONFIRMED, NEW): PerformanceService constructs a process launcher and truncates tokens

- `new RtssLauncher()` in the constructor (PerformanceService.cs:67) cannot be faked; `SanitizeToken` truncates origin and correlation ids to 80 characters (961-970), an arbitrary truncation; `BoundInterval`/`BoundTimeout` silently clamp constructor arguments (972-982).
- Recommendation: inject the launcher port; keep control-character stripping but no truncation; reject out-of-range test intervals instead of clamping.

### DEVICE-034 (nit, CONFIRMED, NEW): opposite persistence order for RTSS and device values

- RTSS persists before writing (PerformanceService.cs:464-499); device capabilities persist after the device applied (DeviceCoordinator.cs:2351-2361). Both are deliberate and documented in place. Record the two rules in docs/device-integration.md; no behaviour change.

### DEVICE-035 (nit, CONFIRMED, NEW): misplaced static helpers on the coordinator

- `SameValue` (2663-2684) is used by admission, lighting and `CapabilityUserWrites`; `PerformanceProfileOwnsRole` (2339-2342) by GpuCoordinator; `ReadOnAcPower` (289-294) is a static native call used in several contexts and cannot be faked; `CycleAuthoredProfileAsync` calls `DeviceOverlayBridge.NextProfile` (2460).
- Recommendation: `CapabilityValues.Same` (Core), role ownership on `CapabilityCommandPolicy`, an `IPowerSourceReader` port, `NextProfile` on the authored-profile owner.

### DEVICE-036 (nit, CONFIRMED, NEW): inconsistent visibility

- `DeviceCoordinator` is `public sealed` with public members and events (54, 175, 260, 525, 606, 648, 774) in an application whose collaborators are internal; `DeviceProfileApplyOutcome` is public (DeviceProfileApplier.cs:9). Make internal.

### DEVICE-037 (low, CONFIRMED, NEW): reconcile keys built from record `ToString`

- `ApplicationReconcileKeys.Take` builds `$"{applicationId}|{manual}"` from a record's generated `ToString` (ApplicationPerformanceReconciler.cs:573-574). Use a value tuple of the fields.

### DEVICE-038 (nit, CONFIRMED, NEW): logging under the router lock

- `LogAvailabilityChange` and the rejection `Log.Change` calls run under `_gate` on the state-delta path (DeviceCapabilityRouter.cs:570-603, 620-640). Low rate and change-only, but the plan rule is no logging under locks. Capture the line under the lock and log after release.

### DEVICE-039 (low, CONFIRMED, NEW): PluginHost per-instance state ownership is inverted

- `PluginRegistration.Health`, `State`, `StateGeneration`, `StateSequence` are mutable members owned by the host's lock (PluginHost.cs:262-267, 119-148), but `StopAsync` writes `Health` without that lock (465). The configuration store defaults to the real `ApplicationPluginConfigurationStore` when a caller omits it (14-22), a hazard for tests with a configurable fake.
- Recommendation: host-owned publication record; required store parameter.

### DEVICE-040 (low, CONFIRMED, NEW): Windows power ports duplicate small pieces

- Four ports each declare `ReadActiveScheme`/`RefreshActiveScheme`; `WindowsCpuBoostApi` declares its own `PowerWriteSettingAttributes` import (WindowsCpuBoostApi.cs:52-58) while every other call goes through WDC. Static `Windows` singletons (CpuBoost.cs:73, HybridCores.cs:80, PowerSchemes.cs:20, WindowsPowerModes.cs:10) are used directly by owners (DeviceCoordinator.cs:134; ShellSession.cs:213).
- Recommendation: keep the narrow ports (they match the plan's port rule); construct the adapters at the composition root and pass them in. Moving the attribute import into WDC is optional and not needed for this refactor.

### DEVICE-041 (low, CONFIRMED, NEW): lifecycle notifications race transitions

- `OnLifecycleState` runs on the plugin's thread outside the transition gate and calls `SetState` (DeviceCoordinator.cs:2763-2775), which can start a cycle restore pass (2814-2825) while a stop is in progress. Commands are then refused by the closed router, so the effect is a spurious pass and log noise. `State` is a plain property read from the UI. Route lifecycle notifications into the cycle owner's lane.

### DEVICE-042 (nit, CONFIRMED, NEW): test-only static indirections

- `RunCancellationSafeStartAsync`, `RunCanceledStartCleanupPolicyAsync`, `CancelLifetimeAndWaitForTransitionAsync`, `RunFreshBoundedCleanupAsync`, `RunClientTeardownWithStateNotificationsAsync`, `RunClientTeardownAsync` (DeviceCoordinator.cs:946-1215, 1447-1582) exist as internal statics so tests can call them. Inline into `DeviceCycle` as private methods once real-owner tests exist.

Retain/no-change decisions: `AutoTdpController` and its constants (pure, replayable, documented policy), `AutoTdpTraceCsv`/`AutoTdpTraceWriter`/`AutoTdpTraceRecorder` (bounded per-generation files; unbounded queue at 1 Hz is not an arbitrary cap), `DeviceDesiredWriteAdmission`, `DeviceProfileApplier`, `DevicePrerequisiteSource`, `PerformanceOverlayBridge` (pure projection; `MaximumFrameLimit = 280` is a UI range, not a content cap), `DeviceCapabilityValidation` rules (shape checks; text length is descriptor-declared), `DeviceOemActionRouter` policy, `CommonPluginPackage` refusal of the Device category.

## 3. Plan refinements

Additions:

1. Add DEVICE-001 as a must-fix before any device lifecycle extraction lands, and before A02_02 (P23).
2. Add a `PowerLimitOwner` (section 4) to the target table; it replaces the planned `DevicePowerAssignmentOwner`.
3. Add `CapabilityCommandPolicy` shared by device and graphics owners in place of a device-only `DeviceCommandPolicy`.
4. Add `DeviceDesiredStateRestorer` (restore pass, lighting readiness pass, authored fan profiles).
5. Add the stale HidHide ledger consumption at start (DEVICE-005) to D02's acceptance: "a crash followed by a start with integration off leaves no WSGM HidHide entry".
6. Add the PL2 single-home migration (DEVICE-008) to the configuration migration matrix with a real-shaped fixture.
7. Add explicit acceptance cases: unverified stop then restart succeeds; hung plugin `StopAsync` lets shutdown return by the deadline with the runtime retained and `Completion` set; late completion of command A cannot alter command B's progress; a pre-cancelled command reaches no plugin code.

Changes:

1. Router: no split into catalog and command lane (P5). One router, validation moved to its own file, injected clock, `TryGetView`, late-completion fix.
2. AutoTDP: do not split `AutoTdpService` into runner and writer classes (P22). Change ownership (owned by the power owner) and collapse restore state.
3. B3 inside this domain: implement as "every stop takes the one outer `Deadline`, awaits at most that, and retains anything still running", in the fixed order the plan lists. The percentage cutoffs are binding at session level; the device owners do not need their own phase scheduler, per-owner budgets or new states to satisfy them. Remove the fixed 5 s/15 s private budgets instead of adding more (DEVICE-003).
4. Device Integration off: the coordinator constructs no native objects and starts no loop beyond the machine reservation and the ledger consumption; diagnostics, power-mode notification and power-assignment loop start only with a cycle.

Removals (mechanism that over-engineers or has no consumer):

1. `DevicePluginCompatibilityAdapter` and device admission into `PluginHost` (DEVICE-002). Simpler shape: the coordinator's single runtime plus the machine mutex.
2. `DeviceTeardownFailureTracker` (DEVICE-015). Simpler shape: log at the step.
3. `Attach*` hooks, `AutomaticPowerOwner` setter, public `MutationGate` (DEVICE-019, DEVICE-009). Simpler shape: constructor injection, one private lane.
4. `Applied(int)` (DEVICE-012). Simpler shape: `IsApplied()` only.
5. Length caps in device-config normalization and load-time name truncation (DEVICE-013), and `SanitizeToken` truncation (DEVICE-033). Simpler shape: shape checks only.
6. Interface default members of `IDeviceOverlaySource` (DEVICE-023).
7. Plan mechanisms flagged as over-engineering in this domain: the catalog/lane router split (P5), a separate AutoTDP generation runner and command writer (P22), and any per-owner phase scheduler for B3. None has a concrete defect that a single owner cannot fix.

## 4. Target design

No new project, no container, no generic resource coordinator. Files stay flat in `src/WSGM/Shell` with namespace `WSGM.Shell`.

| Owner | Responsibility | Constructor dependencies | Approx. size |
| --- | --- | --- | --- |
| `DeviceCoordinator` (facade, existing file shrinks) | Machine reservation, composition of the owners below, public intent methods kept for consumers, `ApplyConfigAsync`, `ApplyProfilesAsync`, glyph selection, AutoTDP switch persistence via config port, diagnostics server start, single shutdown task | config port, profile service, owner factories below | 500 |
| `DeviceCycle` (new) | Package discovery, runtime load/start/suspend/resume/stop/dispose, fault recovery and retry, cycle generation, `State`/`StateChanged`, lifecycle notifications in its lane, plugin settings attach, glyph catalog load, OEM router attach/reset, bounded teardown | `IDevicePackageSource`, `IDeviceRuntimeLoader` (wraps `DevicePluginRuntime.StartAsync`), `IMachineIdentitySource`, router, OEM router, plugin settings, glyph catalog, controller handoff | 900 |
| `DeviceControllerHandoff` (new) | Controller start on physical-identity publication, cancel-on-suspend, target-loss recovery, controller-management enable/disable, haptic sink, running-application controller state, UI claims, rear pulse, stale HidHide ledger consumption when no cycle runs | injected `ControllerManager` (constructed at the composition root, D02), `DeviceCycle` current runtime | 350 |
| `CapabilityCommandPolicy` (new, one instance per publisher) | Native per-app store, persist-after-apply, VRR preference hook, performance-owned roles (sustained, boost, VRR), user-command counter | router, profile service, profile key, VRR preference writer | 250 |
| `PowerLimitOwner` (new) | The only writer lane for sustained/boost/scenario: user funnel, manual pause of AutoTDP, split-pair restore, manual boost and unified-mode persistence, presets and assignments (constructed here), AC/DC and Windows power-mode notifications, assignment reconcile loop, owns `AutoTdpService` | policy, router, `IPowerSourceReader`, `WindowsPowerModes`, `IFrametimeSource`, trace recorder, metrics reader, target-frametime reader | 650 |
| `DeviceDesiredStateRestorer` (new) | Cycle restore pass, lighting readiness pass and `DeviceLightingRestore`, reconciliation priority, authored fan-profile selection/apply | router, policy, profile service, config view | 350 |
| `DeviceCapabilityRouter` (existing) | Unchanged role; late-completion fix, pre-dispatch rejection, `TryGetView`, injected clock | post-to-UI, clock | 1000 |
| `DeviceCapabilityValidation` (moved file) | Unchanged static rules | none | 345 |
| `ApplicationPerformanceReconciler` (existing, trimmed) | Per-application power and VRR decisions only | profile service, `PowerLimitOwner`, VRR target lookup | 350 |
| `CpuBoostReconciler` (split from reconciler) | Per-application processor boost with its baseline, under one gate | profile service, `CpuBoost` | 200 |
| `DevicePluginRuntime` (existing) | Single lifecycle lane, worker keeps the gate until plugin returns, caller waits to the deadline, `Completion` set on every terminal path | package loader, state root | 1150 |
| `PluginHost` (existing) | Common and GPU plugins only | post-to-UI, required config store | 600 |
| `AutoTdpService`, `AutoTdpController`, trace types, `PerformanceService`, `PerformanceOverlayBridge`, `DevicePowerPresets`, `DevicePowerAssignments`, `DeviceOverlayBridge` | Existing roles; changes per findings | as today, constructor-injected | unchanged size |

### Old symbol to new owner: `DeviceCoordinator.cs` (dissolved into the owners above)

| Old symbol (line) | New owner |
| --- | --- |
| `CapabilityCommandOrigin` (30-51) | `CapabilityCommandPolicy.cs` |
| `ProductionOwnerName`, `TryCreateOwnerMutex`, `CreateOwnerMutex`, `_ownerMutex`, `TryStartAsync` (56, 445-522) | `DeviceCoordinator` (`CreateAsync` with ports) |
| `AutomaticRestartBackoffs`, `CanceledStartCleanupBudget`, `_automaticRestartAttempts`, `_faultRecoveryPending`, `_intentionalStop`, `_client`, `_cycleGeneration`, `_identity`, `ActiveDeviceDefinitionId`, `PackageDiscovery`, `InstalledPackage`, `DeviceIdentityKey`, `State`, `StateChanged`, `SetState`, `SetDeviceDefinitionId`, `OnLifecycleState` | `DeviceCycle` |
| `_pluginHost`, `_pluginAdapter`, `_pluginRegistration`, `StopPluginAsync` (1433-1445) | deleted (DEVICE-002) |
| `StartCycleAsync`, `RunUnderTransitionGateAsync`, `StartCycleUnderGateAsync`, `StartCycleCoreUnderGateAsync`, `CleanupCanceledStartAsync`, `CleanupAbortedStartAsync`, `ScheduleStartFaultAfterCleanupAsync`, `ObserveRuntimeCompletionAsync`, `ScheduleStartFault`, `HandleStartFaultAsync`, `ScheduleFaultRecovery`, `RestartAfterDelayAsync`, `StopCycleUnderGateAsync`, `RestartCycleUnderGateAsync`, `SuspendAsync`, `ResumeAsync`, `RetryAfterFaultAsync`, `DecideResume`, `ResumeAction`, `SynchronizeGenerationAfterLifecycleCall`, `Attach`, `DetachAsync`, `LoadPhysicalGlyphProfiles`, `DiscoverPackageAsync`, `NormalShutdownDeadline`, `ReportDeviceTeardown`, `DeviceClientTeardownResult` | `DeviceCycle` (helpers private) |
| `RunCancellationSafeStartAsync`, `RunCanceledStartCleanupPolicyAsync`, `CancelLifetimeAndWaitForTransitionAsync`, `RunFreshBoundedCleanupAsync`, `RunClientTeardownWithStateNotificationsAsync`, `RunClientTeardownAsync` | `DeviceCycle` private (DEVICE-042) |
| `DeviceTeardownFailureTracker`, `_teardownFailures` | deleted (DEVICE-015) |
| `_transitionGate` | `DeviceCycle` lane; facade persistence uses `DeviceCycle.RunExclusiveAsync` |
| `_lifetime`, `_backgroundGate`, `_backgroundTasks`, `Observe`, `CompleteObservedAsync`, `RemoveObservedAsync` | each owner keeps its own tracked set (same three-method pattern, private) |
| `ShutdownAsync`, `RetainDeviceShutdownFailure*`, `DisposeAsync`, `_disposed` | `DeviceCoordinator` single shutdown task calling owner `StopAsync(Deadline)` in order: AutoTDP via power owner, controller handoff, cycle, restorer, router |
| `_diagnostics`, `DiagnosticsSnapshot` | `DeviceCoordinator` (started in `StartAsync`) |
| `_hapticSink`, `ApplyHapticOutputAsync`, `_controllerPublication`, `_controllerStartCancellation`, `OnPhysicalIdentities`, `StartControllerManagementAsync`, `CancelControllerStartAsync`, `OnControllerTargetLost`, `SetControllerManagementUnderGateAsync` (handoff part; the restart fallback calls `DeviceCycle`), `CurrentControllerSelection`, `ChosenControllerTarget`, `ClaimUiAsync`, `ReleaseUi`, `PulseRearButtonAsync`, `SetControllerTargetAsync`, `_runningApplicationId`, `_runningExecutable`, `ApplyRunningApplicationAsync` | `DeviceControllerHandoff` |
| `Controllers` construction (144-155) | composition root (D02 ports); `Controllers` property kept on facade |
| `_config`, `ApplyConfigAsync`, `RestoreConfigAfterCanceledStart`, `ConfigurationChanged`, `IntegrationEnabled`, `AutoTdpEnabled`, `ControllerManagementEnabled`, `PhysicalGlyphSelection`, `PersistConfigurationAsync`, `SetPhysicalGlyphSelectionAsync`, `SetAutoTdpEnabledAsync`, `ToggleAutoTdpAsync`, `PhysicalGlyphCatalog`, `PhysicalGlyphSelectionSnapshot`, `PhysicalControlSelectionSnapshot`, `Profiles`, `Capabilities` | `DeviceCoordinator` facade (persistence through config port) |
| `ExecuteCapabilityAsync` | `DeviceCoordinator` facade delegating: power roles to `PowerLimitOwner`, others to `CapabilityCommandPolicy` |
| `ExecuteCapabilityCoreAsync`, `NotifyManualVariableRefreshChange`, `PerformanceProfileOwnsRole`, `PersistUserCapabilityValueAsync`, `FindCapability`, `FindDescriptor`, `_userCapabilityCommands`, `Instance` | `CapabilityCommandPolicy` |
| `_powerAssignmentChanges`, `_powerAssignmentTask`, `ObservePowerAssignmentsAsync`, `RequestPowerAssignmentReconcile`, `OnPowerControlsChanged`, `_powerControls`, `PowerControlReading`, `_powerModeNotification`, `OnPowerSourceChanged`, `ReadOnAcPower`, `ExecutePresetCapabilityAsync`, `SavePowerAssignmentAsync`, `PowerPresets`, `PowerAssignments`, `ManualTdpMode`, `ManualTdpUnified`, `SetManualTdpModeAsync`, `RestoreSplitPowerAsync`, `PersistManualBoostAsync`, `NotifyManualPowerChange`, `_assignedPowerOverride`, `_autoTdpManualOverride`, `_autoTdpAvailability`, `AttachAutoTdpManualOverride`, `AttachAutoTdpAvailability` | `PowerLimitOwner` (hooks become direct calls on the owned `AutoTdpService`) |
| `_manualVariableRefreshOverride`, `AttachManualVariableRefreshOverride` | `CapabilityCommandPolicy` constructor argument (VRR preference writer) |
| `_profileReconcileGate`, `ReconcileDesiredValuesAsync`, `RestoreDesiredValueAsync`, `ReconciliationPriority`, `OnLightingStateChanged`, `_lightingRestore`, `_lightingRestoreScheduled`, `_resumeRestore`, `UpdateCapabilityDesiredContext`, `AuthoredProfileSelection`, `CycleAuthoredProfileAsync`, `SelectAuthoredProfileAsync`, `ApplyAuthoredProfilesAsync`, `DescribeCapability`, `ActivePluginScope` | `DeviceDesiredStateRestorer` |
| `SameValue` | `Core/CapabilityValues.Same` |
| `_oemActions`, `ConfigureOemActions`, `UpdateOemConfiguration` | `DeviceCoordinator` facade (constructor-injected action services); attach/reset in `DeviceCycle` |
| `_pluginSettings` | `DeviceCycle` |

Other dissolved or moved files: `DevicePluginCompatibilityAdapter.cs` deleted (its validation of identity and generation is already enforced by `DevicePluginRuntime`, 281-284 and 367-371). `DeviceOverlayBridge.cs` splits contracts into `DeviceOverlayContracts.cs` (all types declared at 21-313 except the bridge class). `ApplicationPerformanceReconciler.cs` splits CPU boost (`ReconcileApplicationCpuBoostAsync`, `RefreshCpuBoostAsync`, `SetCpuBoostFromUserAsync`, `ApplyCpuBoostAsync`, `PublishCpuBoost`, `CpuBoostAvailable`, `CpuBoostStatus`, `CpuBoostChanged`, CPU fields) into `CpuBoostReconciler.cs`. `DeviceCapabilityRouter.cs` moves `DeviceCapabilityValidation` (1036-1380) unchanged to `DeviceCapabilityValidation.cs`. `CommandOutcomeExtensions.Applied(int)` deleted.

Public/internal API changes and consumers that must change:

- `DeviceCoordinator.TryStartAsync(config, pluginHost, profiles, token)` becomes `CreateAsync(DeviceCoordinatorPorts, ...)` without `PluginHost`: ShellSession.cs:306-312.
- Removed `AttachAutoTdpManualOverride`, `AttachAutoTdpAvailability`, `AttachManualVariableRefreshOverride`, `PowerPresets.AutomaticPowerOwner` setter: ShellSession.cs:497-537, ShellSession.Shutdown.cs:128-135, GpuCoordinator.cs:256-259 (VRR hook moves to its policy instance), ShellSession.cs:300.
- AutoTDP construction moves into `PowerLimitOwner`: ShellSession.cs:497-519, ShellSession.Performance.cs (`_autoTdp` uses read through `coordinator.Power.AutoTdp`), DeviceOverlayBridge constructor, NativeQamSemanticServices AutoTDP rows, SteamUiSessionHost.
- `ApplicationPerformanceReconciler` constructor takes the power owner instead of three Funcs: ShellSession.cs:212-213; CPU boost consumers (`PerformanceOverlayBridge` 73-79, 314-318, 373-381; native QAM CPU boost rows) take `CpuBoostReconciler`.
- `RestoreSplitPowerAsync`, `SetManualTdpModeAsync`, `ManualTdpMode`, `ManualTdpUnified`, `PowerPresets`, `PowerAssignments` move to `coordinator.Power`: ApplicationPerformanceReconciler.cs:103, 151; NativeQamSemanticServices.cs:791; OverlayWindow.Sources.cs:71; ShellSession.Actions.cs:174; DeviceOverlayBridge.cs:394; DeviceWidgetSource.cs (scope string).
- `DeviceCoordinator.PerformanceProfileOwnsRole` and `SameValue` callers: GpuCoordinator.cs:304, 327; CapabilityUserWrites.cs:69; DeviceDesiredWriteAdmission.cs:72, 82; DeviceLightingRestore.cs:107.
- `DecideResume` signature loses `registrationUsable`: DeviceCoordinatorConcurrencyTests.cs:490-514.
- `IDeviceOverlaySource` loses default members: SimulatedDeviceOverlaySource gains `SetHostSelectionAsync` (already present) and `UseGlobalAsync`; UiTests fakes if any.
- Visibility: `DeviceCoordinator` and `DeviceProfileApplyOutcome` become internal (InternalsVisibleTo already covers tests).

## 5. Implementation batches

Every batch builds and keeps existing filtered tests green. Filters use `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "..."`.

### DEVICE-B1: Drive the device runtime directly; delete the compatibility adapter (DEVICE-001, -002, -030)

- Files: DeviceCoordinator.cs, DevicePluginRuntime.cs, DevicePluginCompatibilityAdapter.cs (delete), PluginHost.cs (drop Device-only branch at 73-76 only if no other caller; keep `PluginCategoryPolicy.Device` in the SDK), ShellSession.cs (TryStartAsync argument), tests DevicePluginRuntimeTests.cs, DeviceCoordinatorConcurrencyTests.cs.
- Steps: replace `_pluginRegistration`/`_pluginAdapter` calls with `client.StartAsync/SuspendAsync/ResumeAsync/StopAsync/DisposeAsync`; move the "worker owns the gate until the plugin returns, caller waits to the deadline" pattern into the runtime's lifecycle methods; map an unknown stop status to Failed; `DecideResume` uses runtime state only; publish nothing to PluginHost for device.
- Depends on: nothing. Must land before A02_02 or together with it.
- Tests: unverified stop then a new cycle starts with the same package id; a plugin whose `StopAsync` never returns lets `StopAsync(deadline)` return at the deadline, keeps the load context and later completes once; adapter tests rewritten against the runtime.
- Filter: `FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~PluginHost`.
- Estimate: 600 changed lines.

### DEVICE-B2: Router command correctness (DEVICE-006, -007, -021 router part, -038)

- Files: DeviceCapabilityRouter.cs, DeviceCapabilityValidation.cs (new, moved), DevicePluginRuntime.cs (pre-dispatch check), DeviceCapabilityRouterTests.cs, DevicePluginRuntimeTests.cs.
- Steps: latest command id per key; late result reconciles only when latest and same descriptor generation; observer tracked and faults logged; pre-cancelled command returns Rejected without plugin call; gate-wait cancellation returns Rejected; `TryGetView(key)`; logging after lock release; clock as `Func<DateTimeOffset>` until F02 lands.
- Depends on: F02 clock design only for the final clock type (can follow later).
- Tests: late A cannot clear B's pending value or replace B's last result; pre-cancelled command leaves the fixture plugin's command count at zero; descriptor replacement drops an old late result.
- Filter: `FullyQualifiedName~DeviceCapabilityRouter|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceDesiredWrite`.
- Estimate: 500 (plus 345 moved unchanged).

### DEVICE-B3: Bounded, single-task device shutdown (DEVICE-003, -004, -015, -016, -027, -031)

- Files: DeviceCoordinator.cs, DevicePluginRuntime.cs, DeviceCoordinatorDiagnostics.cs, AutoTdpService.cs, DeviceOemActionRouter.cs, ShellSession.Shutdown.cs (pass deadline to AutoTDP), tests.
- Steps: one stored shutdown task; one `Deadline` through every await; `Completion` set on every runtime terminal path; delete `DeviceTeardownFailureTracker`; diagnostics server explicit start and stop-on-failure; `AutoTdpService.StopAsync(Deadline)`; OEM dispatch tracked.
- Depends on: Shell/session domain's shutdown runner (H02) for the outer deadline value; works with today's `Deadline.At(deadline)` call.
- Tests: concurrent `ShutdownAsync` callers await one task; hung plugin stop returns at the deadline; AutoTDP restore stops waiting at the deadline and does not issue a second write.
- Filter: `FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceOemActionRouter`.
- Estimate: 500.

### DEVICE-B4: Extract `DeviceCycle` with ports; real-owner tests (DEVICE-014, -029, -041, -042, P8)

- Files: DeviceCycle.cs (new), DeviceCoordinator.cs, DevicePluginRuntime.cs (settings in lane), ShellSession.cs (ports), tests DeviceCycleTests.cs (new), DeviceCoordinatorConcurrencyTests.cs (helpers removed), DeviceIntegrationOffTests.cs.
- Steps: move the lifecycle symbols per the table; ports `IDevicePackageSource`, `IDeviceRuntimeLoader`, `IMachineIdentitySource`; lifecycle notifications enter the cycle lane; integration off constructs no diagnostics/power loops.
- Depends on: B1, B3. Config port shape from the config domain (an interface over today's static `ConfigStore` is enough).
- Tests (fixture runtime + fake ports): integration off makes zero runtime loads and zero plugin calls; two packages refuse with the diagnostic; start fault restarts twice then Faulted and Retry starts once; resume after sleep with a faulted cycle restarts; suspend then resume advances generation once; caller-cancelled start runs bounded cleanup and never auto-restarts.
- Filter: `FullyQualifiedName~DeviceCycle|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~DeviceIntegrationOff`.
- Estimate: 1400.

### DEVICE-B5: `CapabilityCommandPolicy` and `DeviceDesiredStateRestorer` (DEVICE-008, -010, -021, -032, -035)

- Files: CapabilityCommandPolicy.cs (new), DeviceDesiredStateRestorer.cs (new), DeviceCoordinator.cs, GpuCoordinator.cs, CapabilityUserWrites.cs, CapabilityDesiredReconciler.cs, DeviceDesiredWriteAdmission.cs, DeviceLightingRestore.cs, Core/CapabilityValues.cs (new, `Same`), tests.
- Steps: move user-write policy; GpuCoordinator uses a policy instance; PL2 joins performance-owned roles; restorer owns both passes and authored profiles; one snapshot per pass.
- Depends on: config domain migration for existing device-stored PL2 entries (fixture: game layer with device PL2 and no `BoostWatts`, and with both).
- Tests: PL2 user write stores `BoostWatts` only; Use global stops the game's PL2 from being restored; GPU and device stores land in the same layers as today (existing GPU tests unchanged).
- Filter: `FullyQualifiedName~CapabilityUserWrites|FullyQualifiedName~DeviceLightingRestore|FullyQualifiedName~GpuCoordinator|FullyQualifiedName~DeviceProfileApplier|FullyQualifiedName~DeviceDesiredWrite`.
- Estimate: 1100.

### DEVICE-B6: `PowerLimitOwner`, AutoTDP ownership, reconciler split (DEVICE-009, -011, -012, -017, -019, -020, -026, -037, -040)

- Files: PowerLimitOwner.cs (new), CpuBoostReconciler.cs (new), DeviceCoordinator.cs, DevicePowerPresets.cs, DevicePowerAssignments.cs, ApplicationPerformanceReconciler.cs, AutoTdpService.cs, CommandOutcomeExtensions.cs, EffectivePowerModeNotification.cs, ShellSession.cs, ShellSession.Performance.cs, ShellSession.Shutdown.cs, NativeQamSemanticServices.cs (power member access), OverlayWindow.Sources.cs, ShellSession.Actions.cs, PerformanceOverlayBridge.cs, tests.
- Steps: power symbols per the table; private lane replaces `MutationGate`; presets/assignments get the lane and the AutoTDP-owns-power predicate via constructor; AutoTDP constructed inside; `Applied(int)` removed; restore record; callback try/catch; static `Windows` adapters passed from the composition root; CPU boost owner.
- Depends on: B5. NativeQam reviewer's domain for the member moves in NativeQamSemanticServices.
- Tests: manual write pauses AutoTDP once; per-app restore does not pause twice; a verified mismatching paired readback counts as applied; reconciler state changes only inside its lane (concurrent manual and fan-out calls).
- Filter: `FullyQualifiedName~AutoTdp|FullyQualifiedName~DevicePower|FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~ManualTdp|FullyQualifiedName~NativeQamPerformance`.
- Estimate: 1500 (split into B6a power owner and B6b reconciler/CPU boost if the diff exceeds that).

### DEVICE-B7: `DeviceControllerHandoff` and stale ledger consumption (DEVICE-005)

- Files: DeviceControllerHandoff.cs (new), DeviceCoordinator.cs, ShellSession.cs (ControllerManager composed outside), tests.
- Steps: handoff symbols per the table; at start, when no cycle will run and the ledger is non-empty, `ShowAsync` once.
- Depends on: D02 controller domain (ControllerManager and HidHide ports injected).
- Tests: crash ledger plus integration off leaves no WSGM entries and calls `Write` only for owned entries; suspend cancels an in-flight controller start; target loss shows the physical pad once.
- Filter: `FullyQualifiedName~DeviceControllerHandoff|FullyQualifiedName~HidHide|FullyQualifiedName~ControllerManager`.
- Estimate: 700.

### DEVICE-B8: Facade, projections and small cleanups (DEVICE-018, -022, -023, -024, -025, -033, -034 docs, -036, -039)

- Files: DeviceCoordinator.cs, DeviceOverlayBridge.cs, DeviceOverlayContracts.cs (new), SimulatedDeviceOverlaySource.cs, DevicePowerPresets.cs (result record), NativeQamPowerPresetService.cs (mapping), PerformanceService.cs, PluginHost.cs, DeviceProfileApplier.cs, docs/device-integration.md.
- Depends on: config domain for the config port; NativeQam domain for the preset result mapping.
- Tests: existing bridge, preset and performance tests; UI baselines unchanged (overlay-test data untouched).
- Filter: `FullyQualifiedName~DeviceOverlayBridge|FullyQualifiedName~DevicePowerPresets|FullyQualifiedName~PerformanceService|FullyQualifiedName~PluginHost`.
- Estimate: 800.

### DEVICE-B9 (config domain executes): device-config limits (DEVICE-013)

- Files: ConfigStore.cs normalizers, DevicePowerAssignmentsTests.cs, ConfigStore tests.
- Steps: remove preset-id and scenario length caps and authored-name load truncation; shape checks remain.
- Filter: `FullyQualifiedName~DevicePowerAssignments|FullyQualifiedName~ConfigStore`.
- Estimate: 120.

Order: B1, B2, B3, B4, B5, B6, B7, B8; B9 anytime with the config work. Deferred: full `eng\verify.ps1` gate and manual hardware matrix (Claw sleep/restart, Xbox Ally X unverified-release restart, AutoTDP restore on exit) per plan Z04/M01.

## 6. Risks and open questions

Risks: B1 and B4 move the device lifecycle that the Claw sleep and Xbox Ally X re-enumeration fixes depend on; keep `DecideResume` cases and the post-sleep fresh-cycle rule as explicit tests before moving code. B6 changes who owns AutoTDP; the exit restore order (AutoTDP before device stop) must stay. The PL2 migration (B5) changes which stored value restores for users who set boost on a per-game layer.

Open questions for the maintainer:

1. DEVICE-008 migration: when a layer holds both a device-stored PL2 and `BoostWatts` with different values, keep `BoostWatts` (the value the overlay and Steam show today) and drop the device entry? This review assumes yes.
