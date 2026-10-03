# Device and power findings

Scope: the device coordinator and its collaborators in `src/WSGM/Shell` (`DeviceCoordinator`, `DeviceCapabilityRouter`, `DevicePluginRuntime`, `PluginHost` as used for the device, `DevicePluginCompatibilityAdapter`, `DeviceOverlayBridge`, `SimulatedDeviceOverlaySource`, `AutoTdpService`, `ApplicationPerformanceReconciler`, `DevicePowerPresets`, `DevicePowerAssignments`, `DeviceOemActionRouter`, `DeviceCoordinatorDiagnostics`, `CapabilityUserWrites`, `CommandOutcomeExtensions`), plus `Core/PerformanceService.cs`, the device parts of `Core/ConfigStore.cs` and the Windows power ports in `src/WSGM/Interop`. Sources: `_plan/refactor-2.1/review/device.md` (42 findings), its adversarial verification `device.verify.md` (5 missed findings, several corrections), the critic `_critic.md`, plan v2 and the maintainer's answers in `_plan/refactor-2.1/DECISIONS.md`. Where they disagree, DECISIONS.md wins, then plan v2 and the verifier corrections; all are already applied below. The decisions that change this file are D6 (keep the device-stored PL2, drop `BoostWatts`: DEVICE-008 rewritten) and D9 (no readback machinery for any vendor but the Claw: DEVICE-001, DEVICE-012, DEVICE-V-001 and DEVICE-032 checked against it). No device finding depended on the dropped security hardening, D14 or readback re-arm machinery, so none moved to no-change.

Counts after verification: 0 critical, 3 high, 8 medium, 30 low, 6 nit (47 ids). Nothing was wholly refuted; seven sub-claims were, four of them by the solution checker against the code at 1329813f and one by D6 (see the end of the file).

Plan v2 batches that implement this area, in execution order: B008 (DEVICE-001 small fix), B009 (input-owned never-strand batch, carries DEVICE-005 and the release half of DEVICE-003), B011 (DEVICE-V-001), B012 (DEVICE-V-002..V-004), B038 (config domain, DEVICE-013), B057 (toolkit, library side of DEVICE-025), B081, B082, B083, B087 (steamhost NativeQam split the device batches build on), B088, B089 (decided D6: keep the device-stored PL2), B090 (winsvc, DEVICE-040), B091, B092, B093, then B140 (session shutdown) consumes the device owner's `CloseAdmission()` and `StopAsync(Deadline)`.

Line numbers come from the review at `master` 1329813f and are often off by a few lines. Anchor every edit by symbol.

Target shape used by the solutions (plan v2 section 2, "Device and power"): `DeviceCoordinator` becomes a facade over `DeviceCycle` (package discovery and runtime lifecycle), `PowerLimitOwner` (the only sustained, boost and preset lane, owning AutoTDP), `DeviceDesiredStateRestorer` and `DeviceControllerHandoff`. The runtime is driven directly. There is no `CapabilityCommandPolicy` class, no catalog/lane router split, no AutoTDP runner/writer split and no per-owner phase scheduler or budget: those were rejected as over-engineering by the verifier, the critic and plan v2.

### DEVICE-001: an Unverified or Failed device stop permanently blocks every device restart

- **Severity:** high (confirmed by the verifier).
- **Where:** `src/WSGM/Shell/DevicePluginCompatibilityAdapter.cs:64-77` (`StopAsync`, `_released = LastState.Reason is null`); `src/WSGM/Shell/DevicePluginRuntime.cs:414-427` (non-null `Reason` for `Unverified` and `Failed`); `src/WSGM/Shell/PluginHost.cs:84-92` (`Admit` throws on a reserved identity or full Device slot), `PluginHost.cs:518-523` (`PluginRegistration.DisposeAsync` retires only when `_released is true`); `src/WSGM/Shell/DeviceCoordinator.cs:1062-1065` (admits `(packageId, "device")` every cycle); `src/WSGM.Plugin.Sdk/PluginManifest.cs:46` (`PluginCategoryPolicy.Device = new(0, 1, true)`); routine Unverified sources `src/WSGM.Device.Sdk/Services/DeviceServiceLifecycle.cs:258-282`, `AllyServices.cs:138-142, 270-273`, `ClawServiceBase.cs:63`.
- **Problem:** packages return `PluginStopStatus.Unverified` whenever a restore did not read back (routine on the Ally family, which cannot read back) and `Failed` when a restore threw. D9 removes the readback cause in the packages, but `Failed` and any remaining `Unverified` stop still reach this path. The adapter then reports "not released", the registration never retires, and the single Device slot stays reserved for the rest of the process. Every later `Admit` throws, so the fault restart, the post-sleep restart, integration off/on, the controller-management fallback restart and the user's Retry all end Faulted until WSGM restarts. The coordinator comment at `DeviceCoordinator.cs:1256-1258` says the opposite is intended (restart anyway, as HC's Close ignores its results). This gates a restart on readback.
- **Best solution:** a small HC-model fix ahead of the structural batches: the device release counts as done once `runtime.StopAsync` has returned, whatever its status.
  - `DevicePluginCompatibilityAdapter.StopAsync`: after `runtime.StopAsync` returns, set `_released = true` and return `true`. Keep `LastState` (with its `Reason`) so the coordinator still logs an unverified teardown.
  - `PluginRegistration.DisposeAsync`: retire the registration once `plugin.DisposeAsync()` completed when the category is `PluginCategories.Device` (condition becomes `_released is true || Category == PluginCategories.Device`). This covers the path the adapter change cannot: `runtime.StopAsync` throwing after the plugin returned (the command-quiesce `AggregateException` at the end of `DevicePluginRuntime.StopAsync`, or an unknown status before DEVICE-030), which leaves `_released` null through `_stopFailure`. Common plugins keep today's rule, so `PluginHostTests.UnconfirmedStopIsNotRetriedAndContinuesReservingTheSlot` stays valid for them.
  - A registration whose stop timed out with the plugin still inside `StopAsync` keeps the lane and does not retire; that is correct retention (the plugin is still running) and is resolved structurally by DEVICE-002 and DEVICE-003.
  - This beats deleting the adapter first (B081) because it fixes a live, user-visible defect in a few lines and must land before A02_02 (B010), whose journal behaviour makes Faulted stops more common.
- **Tests:** in `tests/WSGM.Tests/Shell/DevicePluginRuntimeTests.cs` (fixture runtime plus a real `PluginHost`): an Unverified stop and a Failed stop are each followed by a successful admission and start of a new cycle with the same package id. Filter: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~PluginHost|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B008 (fix); B081 then deletes the adapter and the device admission altogether.
- **Related:** DEVICE-002 (structural removal), DEVICE-003; A02_02 / A02-F007 (B010 depends on B008; critic conflict 19); manual matrix Xbox Ally X unverified-release restart.

### DEVICE-003: device shutdown waits without a bound on plugin code, which starves every later shutdown step and can strand the pad

- **Severity:** high (verifier kept high and corrected the consequence).
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:809, 1147-1155` (`ShutdownAsync` waits `_transitionGate` with `CancellationToken.None`), `:811` (`_powerAssignmentTask`), `:833-842` (`Task.WhenAll(background)`), `:1219` (`ObserveRuntimeCompletionAsync` awaits `client.Completion` with no token), `:61, 1601-1604` (`CanceledStartCleanupBudget` 5 s, `NormalShutdownDeadline()` 15 s used inside shutdown fallbacks); `src/WSGM/Shell/DevicePluginRuntime.cs:20, 73-83` (`EmergencyCleanupBudget` 5 s; gate-wait timeout throws before the `finally` that calls `Complete`), `:99-101` (emergency `Plugin.StopAsync` unbounded), `:414-428` (unknown stop status throws after `_stopped = true`), `:488-490` (`ReleaseControllerAsync` awaits `Plugin.ReleaseControllerAsync` directly); `src/WSGM/Shell/PluginHost.cs:491` (registration dispose 5 s); `src/WSGM/Shell/ControllerManager.cs:845-852` (`ReleaseAsync` awaits `releasePhysicalAsync` without `WaitAsync`); `src/WSGM/Shell/ShellSession.Shutdown.cs:138-210`.
- **Problem:** with a plugin that ignores cancellation in `StopAsync` or in its controller release, `Completion` is never set and the coordinator awaits it, the transition gate and its background tasks without a bound. The process itself does not hang (verifier: `ApplicationShutdownCoordinator.ShutdownAsync` races the session cleanup against an outer budget of 15/10/5/20 s and exits `TimedOut`), but the session's shutdown sits inside device cleanup until that budget fires, so every later step is skipped: common plugins, GPU routers, tray retirement and Explorer recovery, Steam host, managers. Worse, the unbounded physical release blocks `ShowPhysicalUnderGateAsync`, so the physical pad stays hidden after exit, which breaks "never strand users". Private fixed budgets (5 s, 15 s) also ignore the outer deadline.
- **Best solution:** one outer `Deadline` flows to every awaited step and nothing waits past it; work that misses it is retained (never freed underneath, never re-issued). Two parts in two batches:
  - Release half (B009, input-owned, lands first): `ControllerManager.ReleaseAsync(Deadline)` attempts every step and runs the HidHide show and `SetState` in `finally` (unless the fault-restart path deliberately keeps the pad hidden). The show in `finally` passes `CancellationToken.None`: `ShowUnderGateAsync` starts with `Task.Run(_control.Read, token)`, which never runs on an already-cancelled token, so an expired deadline would otherwise skip exactly the step that must not be skipped (it is a short local HidHide call). `DevicePluginRuntime.ReleaseControllerAsync` and the emergency `Plugin.StopAsync` in `DisposeAsync` await plugin code with `.WaitAsync(deadlineToken)` and, on expiry, keep the load context and return. In `ReleaseControllerAsync` the `_lifecycleGate` is then released by a continuation on the plugin task, not by the method's `finally`, so no later Stop or Dispose enters plugin code while the abandoned release is still inside it (the same "worker keeps the gate until the plugin returns" rule B081 applies to every lifecycle call). The four `DeviceCoordinator` call sites pass their deadline.
  - Shutdown half (B083): the stored shutdown task from DEVICE-004 (`StopAsync(Deadline)`, today `ShutdownAsync(reason, deadline)`) waits the transition gate with a token from `deadline.CreateCancellationSource()`. If the gate is not acquired by then it logs that a transition is still running, still disposes `Controllers` (whose show runs in `finally` after B009, so the pad comes back), and returns without disposing anything the running transition uses (the transition gate, `_lifetime`, the router, the runtime). `_powerAssignmentTask` and the background set are awaited with `.WaitAsync(token)` and reported as unverified on expiry. In `DevicePluginRuntime`, `Complete(...)` runs in every terminal path: the lifecycle gate-wait timeout (move `Complete` into a `finally` that covers the wait), the unknown stop status (DEVICE-030 maps it to Failed instead of throwing), and the end of `DisposeAsync`. `ObserveRuntimeCompletionAsync` awaits `Completion` with the coordinator lifetime token. Inside the shutdown path, `NormalShutdownDeadline()`, `EmergencyCleanupBudget` and the registration's 5 s dispose budget are replaced by the passed deadline (`EmergencyCleanupBudget` is deleted; the registration hop disappears with DEVICE-002). Non-shutdown recovery paths (canceled start cleanup, restarts) keep passing their existing local deadline as an argument; no new budget, phase or state is added.
  - Per critic conflict 1 and D1 (decided): no percentage cutoffs or phase scheduler. The device owner exposes `CloseAdmission()` and `StopAsync(Deadline)` (see DEVICE-004) and B140 calls them in its fixed safety-first order.
- **Tests:** B009: a fixture plugin whose `ReleaseControllerAsync` never returns lets release return at the deadline and the HidHide show still runs. B083: a fixture plugin whose `StopAsync` never returns lets the coordinator's `StopAsync(deadline)` return at the deadline with `Completion` set (Unverified) and the load context retained; a later return of the plugin completes once. Filters: B009 `--filter "FullyQualifiedName~ControllerManager|FullyQualifiedName~HidHideOwnership|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceIntegrationOff"`; B083 `--filter "FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceOemActionRouter"` (both on `tests\WSGM.Tests\WSGM.Tests.csproj`).
- **Plan v2:** B009 (release half), B083 (shutdown half); B140 consumes it (decided D1: safety-first ordered steps under one deadline, no percentage cutoffs or preliminary drain).
- **Related:** INPUT-B1 / INPUT-001, INPUT-V-005 (B009); critic conflicts 1, 2, 3; DEVICE-002, DEVICE-004, DEVICE-027, DEVICE-030; SESSION-V-001 (B006: without it no programmatic exit reaches this code at all).

### DEVICE-V-001: AutoTDP stops working for the rest of the session after any lock, sleep or device restart, and the exit restore is skipped

- **Severity:** high (missed finding, verifier).
- **Where:** `src/WSGM/Shell/AutoTdpService.cs:162-169` (`Availability` refuses when `_restoreCycle` differs from the power capability's `CycleGeneration`), `:894-913` (first write records `_restoreTo`, `_restorePair`, `_restoreCycle`, `_restoreCapability`), `:946-954, 1093-1098` (only cleared by a first-write Rejected or a successful stop), `:1032-1040, 1064` (`StopAsync` refuses to restore across a cycle change); `src/WSGM/Shell/DeviceCoordinator.cs:674` (generation advances on every resume, unlock included), `:1988-1993` (`SetAutoTdpEnabledAsync(true)` throws while unavailable); `src/WSGM/Shell/ShellSession.Power.cs:40-45, 67-74`.
- **Problem:** the cycle generation advances on every session unlock, every wake and every fault or post-sleep restart. After the first AutoTDP write, any of those makes every tick publish Unavailable ("The previous power owner must be restored..."), switching AutoTDP off restores nothing, switching it on throws, and at exit `StopAsync` leaves AutoTDP's last wattage on the device. The latch cannot clear inside the session. HC restores the original TDP without any cycle identity; the guard answers no defect.
- **Best solution:** delete the cycle identity from the restore obligation.
  - Replace `_restoreTo`, `_restorePair`, `_restoreCycle`, `_restoreCapability` with one field `AutoTdpRestorePoint? _restore`, where `private sealed record AutoTdpRestorePoint(int Watts, int? PairWatts)`. On the first automatic write of a generation set it to the current watts and, when the descriptor has a paired limit, the pair's `ObservedValue ?? DesiredValue` integer (today's source, captured as a value instead of a stale `DeviceCapabilityView`). The manual-change path (`:461-467`) updates both values the same way. The "paired-unobserved" first-write refusal stays as it is: despite its trace note it checks only that the paired limit is commandable (`CanWrite` is `DeviceCapabilityRouter.CanCommand`), never a readback, so D9 leaves it.
  - `Availability`: delete the restore-owner check entirely.
  - `StopAsync`: find the primary power capability published now. If none is writable, log once ("AutoTDP could not restore N W: no power limit is published") and keep `_restore` so a later stop can still restore. Otherwise write `Watts` through it and, if `PairWatts` is set and a writable pair is published, write the pair. Clear `_restore` (and `_powerMayDiffer`) after the writes return `IsApplied()` (dispatched; DEVICE-012 removes the readback comparison). No cycle or capability-key comparison anywhere.
  - Never retry (D9: a write either dispatched or failed to dispatch): a restore write that did not report applied is logged and stated in the status, `_resync` behaves as today, and nothing re-issues it automatically.
  - Land it standalone ahead of B091; it also delivers the restore-record part of DEVICE-026.
- **Tests:** `AutoTdpServiceTests`: enable, let one tick write, advance the power capability's `CycleGeneration`, then `Availability.Available` is true, the next tick writes, and disposal restores the original watts exactly once (and the pair once when paired). A second case: no power capability published at stop logs and keeps the obligation. Filter: `--filter "FullyQualifiedName~AutoTdpService"`.
- **Plan v2:** B011. Manual matrix M01-17 extended (lock or sleep between AutoTDP writes, then exit restores the original).
- **Related:** DEVICE-026 (restore record), DEVICE-027 (bounded stop), DEVICE-012 (pair restore uses `IsApplied`, done in B091).

### DEVICE-002: three serial lifecycle lanes and a compatibility adapter for the device runtime

- **Severity:** medium.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:83` (`_transitionGate`), `:1062-1065`, `:1433-1445` (`StopPluginAsync`), `:658` (`DecideResume(... registrationUsable ...)`); `src/WSGM/Shell/PluginHost.cs:73-76` (Device-only branch in `Admit`), `:243, 489-527, 548-621` (`PluginRegistration._lifecycle`, `Task.Run` worker, quarantine, 5 s dispose budget); `src/WSGM/Shell/DevicePluginRuntime.cs:28` (`_lifecycleGate`); `src/WSGM/Shell/DevicePluginCompatibilityAdapter.cs:11-139`; `src/WSGM/Shell/ShellSession.cs:306-312` (`TryStartAsync(config, pluginHost, ...)`).
- **Problem:** three gates serialize the same lifecycle calls. The adapter exists only to fit `IPlugin` (`SessionChangedAsync` is a no-op for the device). What `PluginHost` adds for the device is redundant: the slot reservation duplicates the machine mutex `Global\WSGM.DeviceOwner`, the single `_client`, `PackageDiscovery` cardinality and `CommonPluginPackage` refusing the Device category; device health publications have no consumer (`DeviceWidgetSource` builds the device row from the coordinator, `CommonPluginPanel` filters the Device category); `Quarantined` duplicates the runtime's own state check. The extra reservation is what caused DEVICE-001.
- **Best solution:** delete `DevicePluginCompatibilityAdapter.cs` and the device admission into `PluginHost`.
  - The coordinator (later `DeviceCycle`) calls `client.StartAsync/SuspendAsync/ResumeAsync/StopAsync/DisposeAsync` on `DevicePluginRuntime` directly and reads `client` lifecycle state instead of `_pluginAdapter.LastState`. `_pluginHost`, `_pluginAdapter`, `_pluginRegistration` and `StopPluginAsync` go.
  - Move the one useful `PluginRegistration` behaviour into the runtime's lifecycle methods only: the plugin call runs as `Task.Run(() => Plugin.XAsync(ctx, token).AsTask())`, the worker keeps `_lifecycleGate` until the plugin actually returns (release in a continuation on the call), and the caller stops waiting at its deadline with `.WaitAsync(deadlineToken)`. Never use this for commands or haptic frames (high-rate rule).
  - `DecideResume` drops the `registrationUsable` parameter and decides from the runtime's state.
  - Delete the Device branch at `PluginHost.cs:73-76` if nothing else uses it; keep `PluginCategoryPolicy.Device` in the SDK (public contract). Before deleting, grep `PluginHost.Snapshot` consumers to confirm none shows a Device row; if one does, the coordinator publishes that row as data.
  - `DeviceCoordinator.TryStartAsync` loses the `PluginHost` parameter (`ShellSession.cs:306-312`).
- **Tests:** rewrite adapter tests against the runtime; unverified stop then a new cycle starts; a plugin whose `StopAsync` never returns lets `StopAsync(deadline)` return at the deadline, keeps the load context and later completes once; update `DeviceCoordinatorConcurrencyTests` for the new `DecideResume` signature. Filter: `--filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~PluginHost"`.
- **Plan v2:** B081 (after B012 and B010).
- **Related:** DEVICE-001, DEVICE-003, DEVICE-030; SDK-015 (resolved in B081); B146 (plugin loader) builds on it.

### DEVICE-005: a stale HidHide ledger stays applied for a whole session when no device cycle runs

- **Severity:** medium (verifier corrected the mechanism).
- **Where:** `src/WSGM/Shell/HidHideOwnership.cs:306-325` (`ShowAsync` turns the cloak off even with an empty ledger), `:349-355` (deltas journalled before writes); `src/WSGM/Shell/ControllerManager.cs:178-179` (`DisposeAsync` shows), `:916`; `src/WSGM/Shell/DeviceCoordinator.cs:851-854` (shutdown disposes `Controllers`), `:1349`; `src/WSGM/Program.cs:638-639`.
- **Problem:** after a crash, the ledger keeps the physical pad hidden. The exit path already consumes it (`ControllerManager.DisposeAsync` on every coordinator shutdown, integration off included). The gap is at start: a session that runs no cycle (integration off, no package, two packages, passive detection) or a cycle with controller management off keeps the pad hidden from every other application for the whole session.
- **Best solution:** at the point where the coordinator knows no cycle will manage the controller (created with integration off in `TryStartAsync`'s else branch, the no-package/invalid-package branch of the cycle start, passive detection per DEVICE-V-003, or a cycle started with controller management off), call one new `ControllerManager.ShowStaleHidHideAsync(string reason, CancellationToken)`: it takes `_transition` like `ShowPhysicalControllerAsync`, returns if `State is Active`, asks `HidHideOwnership.HasOwnedEntriesAsync` (new, `(await _store.LoadAsync(token))?.Deltas.Count > 0`; an unreadable ledger counts as non-empty so INPUT-002's cloak-off still runs) and only then runs `ShowPhysicalUnderGateAsync`. The non-empty check is needed because `ShowAsync` also turns the cloak off with an empty ledger, which on a machine where the user runs HidHide for their own devices would switch off a cloak WSGM never set. No new state: it is a one-time call at that decision point.
- **Tests:** a crash ledger plus integration off shows once at start and leaves no WSGM entries, touching only owned entries; an empty ledger makes no HidHide call. The test runs at start, not exit. Filter: `--filter "FullyQualifiedName~ControllerManager|FullyQualifiedName~HidHideOwnership|FullyQualifiedName~DeviceIntegrationOff"`.
- **Plan v2:** B009 (the never-strand batch). B092 later moves the call into `DeviceControllerHandoff` unchanged. Manual matrix M01-12 extended.
- **Related:** INPUT-V-004, INPUT-V-003 (cloak-off written first, B009); INSTALL-V-003 (B007); critic 2.7.

### DEVICE-008: the boost limit (PL2) has two, sometimes three, persistent homes

- **Severity:** medium (verifier upgraded to confirmed and extended the scope).
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2209-2214, 2243-2267` (`PersistManualBoostAsync`, stores `BoostWatts` only when `ManualTdp()` is non-null), `:2217-2222` (also persists a device desired entry via `CapabilityUserWrites.PersistAsync`), `:2331-2342` (`PerformanceProfileOwnsRole` covers sustained and VRR only), `:2562-2585` (restore pass writes the device entry), `:2126-2167` (`RestoreSplitPowerAsync`); `src/WSGM/Shell/DeviceCapabilityRouter.cs:734, 780-806`; `src/WSGM/Shell/DeviceOverlayBridge.cs:583-590, 1224-1225` ("Use global" clears only `BoostWatts`); `src/WSGM/Shell/NativeQamSemanticServices.cs:757` (boost row override id); `src/WSGM/Shell/DevicePowerAssignments.cs:221-233` (out-of-band change adopted as a `custom` assignment); `src/WSGM/Shell/ApplicationPerformanceReconciler.cs:102-103, 129-153`; `src/WSGM/Core/Profiles/ProfileResolver.cs:121-133`; `src/WSGM/Core/ManualTdpProfile.cs`, `Core/Profiles/ProfileFields.cs:28, 168, 190, 237`, `Shell/ProfileService.cs:355`.
- **Problem:** a user PL2 write persists `BoostWatts` and a device desired entry. "Use global" clears `BoostWatts` but the restore pass keeps writing the game's stored device PL2, while the reconciler writes `BoostWatts` through `RestoreSplitPowerAsync`: the exact two-homes failure the comment at 2331-2338 describes for the other roles. A third representation exists: with a preset assignment in force, an out-of-band watt change is adopted as a `custom` assignment and `HasCurrentAssignment` then suppresses the per-app power reconcile.
- **Best solution (decided D6):** the device-stored PL2 entry becomes the only home of PL2 and `BoostWatts` goes.
  - A user PL2 write keeps persisting the device desired entry through `PersistUserCapabilityValueAsync` exactly as today, with or without a manual sustained value, so PL2-only persistence already works. `PowerSlowLimit` stays out of `CapabilityUserWrites.PerformanceProfileOwnsRole` (moved there by DEVICE-010).
  - `PersistManualBoostAsync` stops writing `BoostWatts`. Its only remaining job is today's mode switch: an applied boost write while the layer is unified sets `TdpUnified = false` (an explicit boost edit selects split mode). It moves into `PowerLimitOwner` with DEVICE-009 under that narrower purpose.
  - `ApplicationPerformanceReconciler`: the split branch becomes `manualProfile is { Unified: false }` plus a published PL2 peer whose resolved desired value is set (`peer.Projection.DesiredValue?.IntegerValue`, the game's device entry resolved for the running application). `RestoreSplitPowerAsync` takes that value as `boost`, so the pair-then-boost ordering that keeps PL1 below PL2 is unchanged. Without a device PL2 entry the reconciler writes the sustained target alone as today.
  - Unified mode: the unified target moves both limits, so the restore pass skips the `PowerSlowLimit` candidate while `Profiles.Current.Layers.ManualTdp()?.Unified == true` (one condition in the restorer's `Include`), and a stored PL2 entry no longer fights the unified target.
  - "Use global": `DeviceOverlayBridge.OverrideIdFor` and the native QAM boost row drop their `ProfileField.BoostWatts` arm and use `DeviceOverrideId(view)` like every other device row, so Use global clears the game's device PL2 entry and its restore stops.
  - Delete `ProfileField.BoostWatts`, `ManualTdpProfile.BoostWatts`, the resolver's boost read in `ManualTdp()` and the `ProfileService` case.
  - The `custom` assignment adopted in `DevicePowerAssignments` keeps its Sustained/Slow values as today; the single-home rule applies to the per-game layers, and a fixture proves the assignment still wins while it is in force.
  - Stored `BoostWatts` (D6): a layer with both values keeps the device entry and drops `BoostWatts`. A layer with only `BoostWatts` cannot be moved at config load, because the device identity key and the PL2 capability id are known only once a cycle publishes its descriptors. So the move runs in the cycle, once the first descriptor set with a writable `PowerSlowLimit` is published: one profile-store update that, for every layer with `BoostWatts` set, adds a device entry (`DeviceIdentityKey`, the PL2 descriptor's capability and instance id, the watts) when the layer has none, then clears `BoostWatts` in every layer. Once cleared it never runs again; no flag. `ProfileValues.BoostWatts` stays only as the stored property this move reads. B068 carries nothing for it.
- **Tests:** a PL2 user write stores the device entry only and switches a unified layer to split; Use global on the boost row clears the game's device PL2 entry and stops its restore; a PL2-only layer persists and restores; split mode restores sustained then the device-entry boost; unified mode skips the stored PL2 entry. Move fixtures: a `BoostWatts`-only layer becomes a device entry, both values present with different values keeps the device value, and a layer under a `custom` preset assignment. Filter: `--filter "FullyQualifiedName~CapabilityUserWrites|FullyQualifiedName~DeviceDesiredWrite|FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~DevicePowerAssignments|FullyQualifiedName~ProfileResolver|FullyQualifiedName~NativeQamPerformance"`.
- **Plan v2:** B089; decided: keep the device-stored PL2 and drop `BoostWatts` (D6, overriding plan v2's recommended answer).
- **Related:** DEVICE-010 (helper move), DEVICE-009 (power owner), DEVICE-032 (restorer `Include`).

### DEVICE-009: power-limit ownership is spread over six writers and five coordination mechanisms

- **Severity:** medium.
- **Where:** writers: `src/WSGM/Shell/DeviceCoordinator.cs:2088-2169` (user funnel, `RestoreSplitPowerAsync`), `DevicePowerPresets.cs:66-189`, `DevicePowerAssignments.cs:151-267`, `ApplicationPerformanceReconciler.cs:122-209, 477-510`, `AutoTdpService.cs:847-1116`, the desired-state restore pass, native QAM TDP and preset services (`NativeQamSemanticServices.cs:791, 847, 1101`, split into `src/WSGM/Shell/NativeQam/*` by B087). Coordination: public `SemaphoreSlim MutationGate` (`DevicePowerPresets.cs:30`, borrowed at `DeviceCoordinator.cs:2109, 2136`), `CapabilityCommandOrigin` side effects (`DeviceCoordinator.cs:30-51, 2205-2230`), `Attach*` hooks (`:1935-1959`), settable `AutomaticPowerOwner` (`DevicePowerPresets.cs:31`, set at `ShellSession.cs:520`, nulled at `ShellSession.Shutdown.cs:128-135`), `HasCurrentAssignment` (`ApplicationPerformanceReconciler.cs:103`), `_profilePowerImposed/_profilePowerPaired` (`:41-43`). Implicit lock order assignments `_gate` then `MutationGate` then `_transitionGate` (`SavePowerAssignmentAsync` 313-346, `PersistManualBoostAsync` 2243-2267).
- **Problem:** no single owner decides who may write sustained/boost/scenario limits, so hooks are wired after construction and nulled during shutdown while commands may still run, a gate is shared across classes by a public field, and the lock order is implicit. It works today by care, not by structure.
- **Best solution:** one `PowerLimitOwner` (new file `src/WSGM/Shell/PowerLimitOwner.cs`) with one private lane (`SemaphoreSlim`), constructed by the coordinator facade.
  - Moves in (per `device.md` section 4 table): `_powerAssignmentChanges`, `_powerAssignmentTask`, `ObservePowerAssignmentsAsync`, `RequestPowerAssignmentReconcile`, `OnPowerControlsChanged`, `_powerControls`, `PowerControlReading`, `_powerModeNotification`, `OnPowerSourceChanged`, `ReadOnAcPower` (behind a small `IPowerSourceReader`-style delegate only if a test needs it), `ExecutePresetCapabilityAsync`, `SavePowerAssignmentAsync`, `PowerPresets`, `PowerAssignments`, `ManualTdpMode`, `ManualTdpUnified`, `SetManualTdpModeAsync`, `RestoreSplitPowerAsync`, `PersistManualBoostAsync` (after DEVICE-008 only the split-mode switch), `NotifyManualPowerChange`, `_assignedPowerOverride`, and the AutoTDP hooks.
  - `PowerLimitOwner` constructs and owns `AutoTdpService`; the hooks become direct calls on it (`PauseForManualChange`, availability). `DevicePowerPresets` and `DevicePowerAssignments` are constructed by the owner and receive the lane (as a `Func` that runs work in it) and the AutoTDP-owns-power predicate through their constructors. `MutationGate` and the `AutomaticPowerOwner` setter are deleted.
  - Lock order is fixed by structure: power lane, then the cycle lane, never the reverse. The cycle never awaits the power lane while it holds its gate (the cycle restore pass is already started as a separate task). Write this rule in the class remarks; no runtime enforcement.
  - Consumers move to `coordinator.Power`: `ApplicationPerformanceReconciler.cs:103, 151`, NativeQam power services, `OverlayWindow.Sources.cs:71`, `ShellSession.Actions.cs:174`, `DeviceOverlayBridge.cs:394`, `DeviceWidgetSource.cs`.
  - The AutoTDP exit restore becomes the explicit first step of the coordinator's shutdown task, before device stop (verifier batch problem 5), replacing today's ShellSession ordering (`ShellSession.Shutdown.cs:111-136`).
  - Windows power statics come from B090 instances (critic conflict 13: no second lane type).
- **Tests:** a manual write pauses AutoTDP once; a per-app restore does not pause it; preset apply and assignment reconcile run inside the lane (a concurrent manual write waits); the shutdown task restores AutoTDP before stopping the device. Filter: `--filter "FullyQualifiedName~AutoTdp|FullyQualifiedName~DevicePower|FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~ManualTdp|FullyQualifiedName~NativeQamPerformance"`.
- **Plan v2:** B091 (after B090 and B089); may be split into a power-owner commit and a reconciler/CPU-boost commit.
- **Related:** DEVICE-011, DEVICE-012, DEVICE-017, DEVICE-019, DEVICE-020, DEVICE-026, DEVICE-037, DEVICE-040; critic conflicts 4 and 13; review claim P4 (`DevicePowerAssignmentOwner` would duplicate `DevicePowerAssignments`).

### DEVICE-012: readback gates success for paired power writes only

- **Severity:** medium (verifier: stronger than stated).
- **Where:** `src/WSGM/Shell/CommandOutcomeExtensions.cs` (`Applied(this CapabilityCommandResult, int)`); callers `src/WSGM/Shell/AutoTdpService.cs:942, 1074`, `src/WSGM/Shell/ApplicationPerformanceReconciler.cs:501`, `src/WSGM/Shell/DeviceCoordinator.cs:2153, 2163`.
- **Problem:** `Applied(int)` returns false for an `AppliedVerified` result whose readback differs from the request, and also when `ReadbackValue` is null. Paired writes use it, unpaired writes use `IsApplied()`. A clamped or late readback turns a delivered write into "not applied", makes AutoTDP resync and reports a restore as unconfirmed. This violates the no-readback rule.
- **Best solution:** delete `Applied(int)`. All five callers use `result.Outcome.IsApplied()`; the `paired ? ... : ...` branches at `AutoTdpService.cs:942` and `ApplicationPerformanceReconciler.cs:501` collapse to the single call. No new logging: AutoTDP already logs every write's outcome and its trace records `WriteReadbackWatts`, and the router logs outcome changes. Publishing the written value as observed is the router's existing behaviour and is unchanged. This is the host half of D9 for power writes: success means the write dispatched, for every vendor; a Claw readback stays trace and log data only. D9 check on the rest of the host: `DeviceDesiredWriteAdmission`'s `PreviousResultUncertain` skip waits on no readback (it ends with a different desired value or the user's own write, and its comment records why waiting on a readback was removed on 2026-09-29), and `DeviceLightingRestore` waits on none either, so neither is readback machinery and both stay.
- **Tests:** a verified mismatching paired readback counts as applied in `AutoTdpServiceTests` (no resync, restore reported confirmed) and in the reconciler's split-pair restore. Filter: `--filter "FullyQualifiedName~AutoTdp|FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~ManualTdp"`.
- **Plan v2:** B091.
- **Related:** review claim P16; DEVICE-V-001 (the pair restore call at 1074).

### DEVICE-013: arbitrary length limits drop or truncate device configuration

- **Severity:** medium.
- **Where:** `src/WSGM/Core/ConfigStore.cs:863` (plugin id over 128 or preset id over 64 drops the reference), `:874` (custom scenario over 128 drops it), `:598-600` (authored profile names truncated to `DeviceAuthoredProfile.MaxNameLength` = 48 on load); `src/WSGM/Core/DeviceConfiguration.cs:115`; `tests/WSGM.Tests/.../DevicePowerAssignmentsTests.cs:353-383` (`AssignmentLengthLimitsApplyAfterTrimming` pins the cap). Same rule, config domain: `ConfigStore.cs:684-686` truncates game profile names to 80. Critic CRIT-005: `src/WSGM/Core/DeviceProfileValidation.cs:38, 72` (`MaximumPoints = 64`).
- **Problem:** SDK identifiers have no length bound, so a valid 65-character preset id can never be assigned, and load-time truncation rewrites user data. The 64-point curve cap refuses authored curves the device declares nothing against.
- **Best solution:** shape checks only. In the device-config normalizer: keep "non-empty after trim" and the `custom` values checks (`SustainedWatts > 0`, `SlowWatts >= SustainedWatts`, defined `WindowsMode`, non-empty scenario when present) and delete the `> 128`, `> 64` and scenario `> 128` comparisons. Delete the load-time name truncation for authored profiles (and for game profiles in the same batch). `DeviceAuthoredProfile.MaxNameLength` stays only as the Settings editor's input limit if the row view model uses it (UI choice; the editor appearance stays identical), never on load. `DeviceProfileValidation` drops `MaximumPoints` and enforces only the descriptor's bounds and the monotonic-input check; the router's `CurveIsValid` (`DeviceCapabilityRouter.cs:1356`) has no count limit today, so nothing changes there. The Settings curve editor's own `CurveEditing.MaximumPoints` is a UI editing limit and is out of scope unless B038 lists it.
- **Tests:** replace `AssignmentLengthLimitsApplyAfterTrimming` with a test that a 65-character preset id and a 200-character scenario survive normalization; a stored 60-character authored name loads unchanged; a 65-point curve within descriptor bounds validates. Filter: `--filter "FullyQualifiedName~Configuration|FullyQualifiedName~DevicePowerAssignments|FullyQualifiedName~DeviceProfile|FullyQualifiedName~ProfileResolver"`.
- **Plan v2:** B038 (config domain executes it).
- **Related:** CRIT-005 (implemented together in B038), CONFIG limit removals in B038; decided D2: only the plan v2 byte-bound list stays and it names none of these fields, so every cap here goes.

### DEVICE-014: the device coordinator has no behavioural test

- **Severity:** medium.
- **Where:** `tests/WSGM.Tests/Shell/DeviceCoordinatorConcurrencyTests.cs` (static helpers with fakes that perform the compensation being asserted, a constant test at 456-460, a misplaced entry-point test at 10-20); `tests/WSGM.Tests/Shell/DeviceIntegrationOffTests.cs:36-49` (predicates and a POCO getter); six `DeviceOverlayBridgeTests` that assert `SimulatedDeviceOverlaySource` preview data.
- **Problem:** the private constructor builds native objects, so nothing constructs a `DeviceCoordinator`. The lifecycle the Claw sleep and Ally re-enumeration fixes depend on is untested, which makes the B081/B088 moves risky.
- **Best solution:** after `DeviceCycle` is extracted with minimal ports (package source, runtime loader, machine identity; ports only where a test needs them), write `tests/WSGM.Tests/Shell/DeviceCycleTests.cs` against the real cycle with the existing fixture runtime used by `DevicePluginRuntimeTests`. Delete the tests of extracted statics as DEVICE-042 inlines them, move the entry-point test to the program tests, and replace the predicate tests in `DeviceIntegrationOffTests` with an integration-off cycle test. Keep the preview-data bridge tests only where UI baselines need them, and add production-projection tests for `DeviceOverlayBridge.ToOverlayCapability`. Keep `DevicePluginRuntimeTests`, `AutoTdpServiceTests`, `DevicePowerPresetsTests`, `DevicePowerAssignmentsTests`, `PerformanceServiceTests`, `DeviceLightingRestoreTests` as they are.
- **Tests:** integration off makes zero runtime loads and zero plugin calls; two packages refuse with the diagnostic; a start fault restarts twice then Faulted, and Retry starts once; resume with a faulted cycle restarts; suspend then resume advances the generation once; a caller-cancelled start runs bounded cleanup and never auto-restarts. Filter: `--filter "FullyQualifiedName~DeviceCycle|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~DeviceIntegrationOff"`.
- **Plan v2:** B088.
- **Related:** U05-LFB-006 (same pattern); DEVICE-042; review risk note (keep `DecideResume` cases and the post-sleep fresh-cycle rule as explicit tests before moving code).

### DEVICE-V-002: a failed resume after a session unlock leaves the device quiesced and controller forwarding blocked

- **Severity:** medium (missed finding, verifier).
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:648-711` (`ResumeAsync`: the catch is `when (afterSystemSleep && ...)`), `:627` (forwarding blocked at lock), `:701-703` (`ResumeForwardingAsync` skipped on throw); `src/WSGM/Shell/ShellSession.Power.cs:243-254` (only logs and sets `_deviceSuspended = true`).
- **Problem:** only sleep failures turn into a fresh cycle. On unlock, the exception propagates past `SetState` and `Controllers.ResumeForwardingAsync`, so the plugin has closed its devices, the virtual pad receives nothing, and the handheld has no controller input on the desktop until the next lock, sleep or Retry.
- **Best solution:** remove the branch instead of adding one. Change the catch filter to `when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)` (the cycle start's own rule: only the caller's cancellation propagates; a resume that ran out of its 5 s deadline surfaces as an `OperationCanceledException` once B081 calls the runtime directly, and today's `ex is not OperationCanceledException` filter would let exactly that failure strand the device again) and, on failure, call `RestartCycleUnderGateAsync(afterSystemSleep, cancellationToken)` (its existing non-sleep branch already reports an unverified teardown through `ReportDeviceTeardown`). The log line names the trigger ("after a sleep" or "after a session unlock"). A cycle restart is not a write retry: the restore pass after it still goes through `DeviceDesiredWriteAdmission`, so an uncertain value is not replayed.
- **Tests:** unlock-resume throwing leads to exactly one restart and forwarding resumes; sleep-resume behaviour is unchanged. Filter: `--filter "FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~DevicePluginRuntime"`.
- **Plan v2:** B012 (after B009).
- **Related:** DEVICE-V-003 (same batch), DEVICE-014 (becomes a `DeviceCycle` test in B088).

### DEVICE-004: coordinator shutdown is not a single stored task

- **Severity:** low (verifier lowered from medium: the two production callers are mutually exclusive, so no concurrent second shutdown happens today).
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:800-805` (`_disposed` plain bool), `:244-249` (`DisposeAsync` builds its own 15 s deadline); callers `ShellSession.cs:342`, `ShellSession.Shutdown.cs:154`.
- **Problem:** a second `ShutdownAsync` or `DisposeAsync` returns immediately while the first is still releasing hardware, and `DisposeAsync` invents its own deadline. B140 needs a synchronous `CloseAdmission()` at T0 and a repeatable `StopAsync(Deadline)`.
- **Best solution:** keep it small. Replace `_disposed` with two fields: `private volatile bool _admissionClosed` and `private Task? _shutdown`. `CloseAdmission()` sets `_admissionClosed`; every public intent method that checks `_disposed` today checks it instead and returns the same refusal. `StopAsync(Deadline deadline)` calls `CloseAdmission()` and returns `Interlocked.CompareExchange(ref _shutdown, ...)`'s winner so repeated calls await one task. `DisposeAsync` awaits `StopAsync` with the same deadline the session passes (no private 15 s). Use the name `CloseAdmission` everywhere (critic conflict 2).
- **Tests:** two concurrent `StopAsync` callers await the same task; an intent after `CloseAdmission()` is refused without reaching the runtime. Filter: `--filter "FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B083.
- **Related:** DEVICE-003; critic conflicts 1 and 2; B140.

### DEVICE-006: a late command completion can overwrite a newer command's progress and admission evidence

- **Severity:** low (verifier lowered from medium: packages serialize per device, so the bad order is narrow).
- **Where:** `src/WSGM/Shell/DeviceCapabilityRouter.cs:285-307` (per-key gate released when the immediate result returns), `:288` (`_ = ObserveLateCommandAsync`), `:648-674` (checks runtime, cycle and command id only), `:676-701` (`ReconcileResult` replaces `_pendingValues`/`_lastResults` for the key); `src/WSGM/Shell/DeviceDesiredWriteAdmission.cs:79-84`.
- **Problem:** if command B for the same key completes before A's late completion, A's late result removes B's pending value and replaces `_lastResults[key]` while `_lastCommandValues[key]` holds B's value. Admission then pairs A's outcome with B's value: an old Indeterminate blocks restore of a value that was never uncertain, or an old Applied clears a pending marker early. The observer is fire-and-forget with no fault handling.
- **Best solution:** record the latest command id per key beside `_lastCommandValues` under `_gate` (a `Dictionary<DeviceCapabilityKey, Guid> _lastCommandIds` set in `PrepareCommand` where `_lastCommandValues` is set, and cleared everywhere `_lastCommandValues` is cleared: attach, cycle-generation change and descriptor-set replacement). `ObserveLateCommandAsync` adds one condition to its existing check: its command id is still `_lastCommandIds[key]`; otherwise it logs the late outcome (the existing "ignored" line) and drops it. Because a descriptor replacement or cycle change clears the map, no separate descriptor-generation comparison is needed. Track observer tasks in the router (a small set joined in `DisposeAsync`), with faults logged. This uses ids the router already has; no new state machine.
- **Tests:** late A cannot clear B's pending value or replace B's last result; a descriptor replacement drops an old late result. Filter: `--filter "FullyQualifiedName~DeviceCapabilityRouter|FullyQualifiedName~DeviceDesiredWrite"`.
- **Plan v2:** B082.
- **Related:** review claim P15; DEVICE-031 (observer tracking).

### DEVICE-007: a command cancelled after the router gate but before dispatch is reported Indeterminate

- **Severity:** low (verifier lowered from medium and refuted the router half).
- **Where:** `src/WSGM/Shell/DevicePluginRuntime.cs:177-197` (`ExecuteCommandAsync` calls `Plugin.ExecuteCommandAsync` with an already-cancelled token), `:714-729` (classified Indeterminate unless the deadline fired). Not a defect: `DeviceCapabilityRouter.cs:261` (`commandGate.WaitAsync(cancellationToken)` throws before `PrepareCommand`, nothing recorded).
- **Problem:** cancellation landing in the microsecond window between router gate acquisition and the plugin call still invokes plugin code and reports Indeterminate, which marks a never-sent value uncertain and blocks its automatic restore until the user acts.
- **Best solution:** runtime half only. In `DevicePluginRuntime.ExecuteCommandAsync`, after the operation is admitted to `_commands` and before `operation.Start(...)`, check `operation.Token.IsCancellationRequested`; if set, remove and dispose the operation and return `Rejected(command, operation.DeadlinePassed ? "The command deadline passed before dispatch." : "The command was cancelled before dispatch.")`. Keep the router's pre-gate exception exactly as today: changing it to Rejected would show `CommandProgress.Failed` on rows, turn AutoTDP's cancelled-write path into a "did not accept" status and drop the presets' "Preset selection was cancelled" status (UI must stay identical).
- **Tests:** a pre-cancelled command leaves the fixture plugin's command count at zero and returns Rejected. Filter: `--filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCapabilityRouter"`.
- **Plan v2:** B082.
- **Related:** review claim P14 (partially refuted).

### DEVICE-010: user-write policy is duplicated between the device and graphics coordinators

- **Severity:** low (verifier lowered from medium and replaced the recommendation).
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2171-2241` (`ExecuteCapabilityCoreAsync`, about 2184-2223 duplicated), `:2339-2342` (`PerformanceProfileOwnsRole`); `src/WSGM/Shell/GpuCoordinator.cs:281-336` (calls `DeviceCoordinator.PerformanceProfileOwnsRole` at 304 and 327); `src/WSGM/Shell/CapabilityUserWrites.cs`.
- **Problem:** about 25 lines per coordinator implement the same decisions: native per-application store instead of command, persist after an applied write, the VRR manual hook, and performance-owned roles; the GPU side reaches into the device coordinator's static.
- **Best solution:** extend the existing static `CapabilityUserWrites`; no `CapabilityCommandPolicy` class. Move `PerformanceProfileOwnsRole(CapabilityRole)` there and add `HandleUserWriteAsync(profiles, profileKey, view, value, executeAsync, onVariableRefresh, cancellationToken)` that does: decide store versus command, store for the application when the layer is game-scoped and not command-backed, otherwise execute, then either call the VRR callback or persist when the role is not performance-owned. Both coordinators call it with their router's execute delegate and their VRR preference writer (passed as a parameter, replacing `AttachManualVariableRefreshOverride` and GPU's `_manualVariableRefresh` hook). Per-coordinator state such as the device's user-command counter stays on the device side.
- **Tests:** device and GPU stores land in the same layers as today (existing GPU tests unchanged); a user VRR write calls the preference writer once. Filter: `--filter "FullyQualifiedName~CapabilityUserWrites|FullyQualifiedName~GpuCoordinator|FullyQualifiedName~DeviceDesiredWrite"`.
- **Plan v2:** B089. B148 (SDK-023) must consume the moved helper, not move it a second time.
- **Related:** DEVICE-008, DEVICE-035; SDK-023 (B148).

### DEVICE-011: unsynchronized state in ApplicationPerformanceReconciler

- **Severity:** low (verifier lowered from medium: worst effect is one wrong "imposed" flag).
- **Where:** `src/WSGM/Shell/ApplicationPerformanceReconciler.cs:34-43` (`_profilePowerImposed`, `_profilePowerPaired`, `_profileVrrImposed`, `_cpuBoostImposed`, `_cpuBoostBaseline`, `_lastReconciledCpuBoostKey`), `:261, 267, 309` (`_cpuBoostImposed` written outside `_cpuBoostGate`), `:348` (read inside it), `:389-407` (`PersistManualPowerLimit` from the manual hook, `_profilePowerImposed` at 399).
- **Problem:** flags are written from the fan-out worker, the coordinator's manual hooks on whatever thread a user command completes, and UI calls. A race can leave one wrong flag that decides whether the next application change releases a value.
- **Best solution:** the power half of the reconciler moves into `PowerLimitOwner`: `ReconcileApplicationPowerLimitAsync`, `ApplyProfilePowerLimitAsync`, `PersistManualPowerLimit`, `_profilePowerImposed` and `_profilePowerPaired`, so both flags are only touched inside the owner's lane (the manual-write path and the application fan-out both enter the lane). The reconciler's fan-out calls `owner.ReconcileApplicationPowerAsync(manual, applicationId, token)` where it calls `ReconcileApplicationPowerLimitAsync` today. The VRR half (`_profileVrrImposed`, `PersistManualVariableRefresh`, the VRR reconcile) stays in the reconciler and reads and writes its flag under one private `Lock`. CPU boost moves to `src/WSGM/Shell/CpuBoostReconciler.cs` (`ReconcileApplicationCpuBoostAsync`, `RefreshCpuBoostAsync`, `SetCpuBoostFromUserAsync`, `ApplyCpuBoostAsync`, `PublishCpuBoost`, `CpuBoostAvailable`, `CpuBoostStatus`, `CpuBoostChanged` and the CPU fields), where every read and write of its fields is under its one `_cpuBoostGate`. CPU boost consumers (`PerformanceOverlayBridge.cs:73-79, 314-318, 373-381`, native QAM CPU boost rows) take the `CpuBoostReconciler` instance. The `CpuBoost` instance comes from B090.
- **Tests:** concurrent manual and fan-out calls leave consistent flags; CPU boost user set and app change interleaved under the gate. Filter: `--filter "FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~CpuBoost|FullyQualifiedName~NativeQamPerformance"`.
- **Plan v2:** B091.
- **Related:** U05-LFB-019; DEVICE-009, DEVICE-020; WINSVC-009 (B090 instances).

### DEVICE-015: DeviceTeardownFailureTracker has contradictory semantics and adds no behaviour

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:1265` (cleared unconditionally after unverified cleanup), `:1401-1405` (cleared only after verified), `:724` (drained and discarded after sleep), `:2942-2951` (`HasFailures`, tests only); shutdown rethrows them as one exception ShellSession only logs.
- **Problem:** the tracker retains, discards and rethrows the same failures inconsistently, and the only observable effect is a log line.
- **Best solution:** delete `DeviceTeardownFailureTracker`, `_teardownFailures` and `RetainDeviceShutdownFailure*`. Each unverified step logs where it happens (HC model, already the effective behaviour). `ShutdownAsync` returns normally after logging.
- **Tests:** remove the tracker tests; the shutdown test asserts an unverified stop is logged and does not throw. Filter: `--filter "FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B083.
- **Related:** DEVICE-003.

### DEVICE-016: diagnostics pipe server spins without delay on a persistent failure

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinatorDiagnostics.cs:49-56` (loop started in the constructor), `:72-106` (`RunAsync` loops immediately after `IOException`, `UnauthorizedAccessException`, `JsonException`); constructed at `DeviceCoordinator.cs:142`; consumer `src/WSGM/Settings/SettingsViewModel.DeviceSetup.cs:111-131` (`RefreshDeviceOwnerStatusAsync`, 750 ms read).
- **Problem:** a squatted or denied pipe name gives a hot loop and a log flood for the whole session.
- **Refuted half (checker):** "starts with integration off" is not a defect. The coordinator exists with integration off (`TryStartAsync` else branch) and the pipe is how standalone Settings shows "Disabled · no package · 0/0 healthy · cycle 0". Starting the server only with a device cycle would turn that into "No running device coordinator detected. Saved changes apply at the next shell start." while WSGM is running, a visible and misleading UI change. The server stays started by the coordinator as today, integration on or off; B088's "integration off constructs no diagnostics loop" must not be applied to this server.
- **Best solution:** fix the hot loop only. Split the loop body so a failure before a client connected (pipe creation or `WaitForConnectionAsync` throwing `IOException`/`UnauthorizedAccessException`: the squatted or denied name) waits a fixed 5 s on the lifetime token before the next attempt, while a failure after a client connected (the client closed early, the routine `IOException`) loops at once as today, so Settings' next read is never delayed. Log through `Log.Change` with one fixed key so a persistent failure logs once and the first successful serve after it logs one recovery line; no flag, counter or backoff.
- **Tests:** if pipe creation is injectable, a factory that throws twice then succeeds logs once and recovers, and dispose during the wait returns promptly; otherwise keep the test to start/dispose behaviour and a second server on the same name (squatted) does not spin. Filter: `--filter "FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B083. The B088 spec sentence "integration off constructs no diagnostics ... loops" is corrected here: it applies to the power loops only.
- **Related:** review claim P8.

### DEVICE-017: power-mode callback has no exception boundary

- **Severity:** low.
- **Where:** `src/WSGM/Interop/EffectivePowerModeNotification.cs:88-98` (`[UnmanagedCallersOnly] OnChanged` invokes the action without try/catch), `:25-28` (static callback table).
- **Problem:** an exception thrown by the managed action escapes an `[UnmanagedCallersOnly]` callback, which terminates the process.
- **Best solution:** wrap `changed?.Invoke()` in `try { ... } catch (Exception ex) { Log.Warn(...); }` inside `OnChanged`. Keep the static id table: it is the safe pattern for a racing native callback. Ownership of the registration moves to `PowerLimitOwner` with DEVICE-009.
- **Tests:** none practical for the native path; a unit test can call the managed dispatch through an internal seam only if one already exists. Filter (for the batch): `--filter "FullyQualifiedName~AutoTdp|FullyQualifiedName~DevicePower"`.
- **Plan v2:** B091.
- **Related:** U05-LFB-013, WINSVC-V-003 (same fix, resolved in B091).

### DEVICE-018: the coordinator writes config itself

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2065-2075` (`PersistConfigurationAsync` calls static `ConfigStore.Mutate` and replaces `_config`), used by AutoTDP enable (`:1981-2010`) and glyph selection.
- **Problem:** the coordinator's `_config` diverges from ShellSession's until a reload arrives, and it bypasses the session's config store.
- **Best solution:** take the instance `ConfigStore` from B037 in the facade constructor and replace only the static `ConfigStore.Mutate` call in `PersistConfigurationAsync` with the instance's update, so the session and the coordinator share one store and cannot diverge. Keep assigning `_config` from the value the store returns and keep raising `ConfigurationChanged`: `AutoTdpEnabled`, `ToggleAutoTdpAsync` (`!_config...AutoTdpEnabled`) and the glyph selection read `_config` right after the write, so waiting for `ApplyConfigAsync` would show the old toggle state and make a quick second toggle repeat the first. Persistence still runs exclusive with cycle transitions (`DeviceCycle.RunExclusiveAsync` after B088).
- **Tests:** toggling AutoTDP persists through the injected store, the coordinator reports the new value immediately, and two quick toggles end where they started. Filter: `--filter "FullyQualifiedName~DeviceOverlayBridge|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B093 (after the config store instance from B037/B039).
- **Related:** config B037, B039.

### DEVICE-019: setter injection and nullable delegate hooks

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:1935-1959` (`AttachAutoTdpManualOverride`, `AttachAutoTdpAvailability`, `AttachManualVariableRefreshOverride`), `:2414-2417` (`ConfigureOemActions`); `DevicePowerPresets.AutomaticPowerOwner`; `src/WSGM/Shell/ShellSession.cs:519-537, 983`; `src/WSGM/Shell/ShellSession.Shutdown.cs:128-135` (nulled while commands may run); `GpuCoordinator.cs:256-259`.
- **Problem:** dependencies are assigned after construction and nulled during shutdown, read without synchronization, so a command running during shutdown can see a half-wired object.
- **Best solution:** constructor injection, no hooks, for the hooks that race. The AutoTDP hooks become direct calls because `PowerLimitOwner` owns AutoTDP (DEVICE-009). The VRR hook becomes a parameter of `CapabilityUserWrites.HandleUserWriteAsync` supplied by each coordinator's constructor (DEVICE-010); `_applicationProfiles` exists before both coordinators are built (`ShellSession.cs:212`, `:300`), so this needs no reordering. Delete the three `Attach*` methods, the `AutomaticPowerOwner` setter and the shutdown nulling (`ShellSession.Shutdown.cs:128-135`). `ConfigureOemActions` stays as it is: it is assigned once under the OEM router's lock, never nulled, and the router already handles the window before it is set ("unavailable before UI services attach"); its services close over `_overlay`, `_modes` and `_monitor`, which `WireSessionEvents` asserts exist, so moving it into the constructor would mean reordering session startup for no defect.
- **Tests:** covered by the B091 power-owner tests; the build proves no caller remains. Filter: as DEVICE-009.
- **Plan v2:** B091.
- **Related:** DEVICE-009, DEVICE-010, DEVICE-020.

### DEVICE-020: late-bound service lookup and a dependency cycle

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ShellSession.cs:212-213`; `src/WSGM/Shell/ApplicationPerformanceReconciler.cs:27-32` (`Func<DeviceCoordinator?>`, `Func<AutoTdpService?>`, `Func<GpuCoordinator?>`).
- **Problem:** AutoTDP writes through the coordinator, the coordinator calls AutoTDP through hooks, and the reconciler calls both through funcs over session fields.
- **Best solution:** break the cycle by ownership, not by construction order. The reconciler is built in the `ShellSession` constructor (`ShellSession.cs:212`), before the GPU coordinator that already captures `PersistManualVariableRefresh` and long before the asynchronous `DeviceCoordinator.TryStartAsync`, so it cannot take the owner as an instance. Its two funcs `Func<DeviceCoordinator?>` and `Func<AutoTdpService?>` collapse into one `Func<PowerLimitOwner?>` (null without a coordinator: overlay test or owner mutex taken), alongside the existing GPU lookup. The cycle disappears because the owner never calls back into the reconciler: today's manual hook (`ShellSession.cs:527-531`, `_autoTdp?.NoteManualChange` plus `_applicationProfiles.PersistManualPowerLimit`) becomes the owner's own manual-write step, since `PersistManualPowerLimit` moves into the owner with DEVICE-011, and AutoTDP is reached only through the owner.
- **Tests:** as DEVICE-009/011.
- **Plan v2:** B091.
- **Related:** DEVICE-009, DEVICE-011, DEVICE-019.

### DEVICE-021: repeated full router snapshot builds

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2099-2102, 2186-2190, 2211, 2226, 2375, 2401-2411` (`FindCapability`/`FindDescriptor`), `:203-206` (`ManualTdpMode`); `src/WSGM/Shell/CapabilityDesiredReconciler.cs:72-85`; `src/WSGM/Shell/AutoTdpService.cs:149-183, 651, 1118-1140`.
- **Problem:** the whole router snapshot (with desired resolution and validation) is rebuilt up to five times per command, about six times per AutoTDP tick, and once per candidate in the restore pass (quadratic per pass). Not high-rate, but wasteful.
- **Best solution:** add `DeviceCapabilityRouter.TryGetView(DeviceCapabilityKey key, out DeviceCapabilityView view)` that builds one view under the router lock. `FindCapability`/`FindDescriptor` and `ManualTdpMode` use it; AutoTDP takes one snapshot per tick and passes it down. In the restore pass keep one ordered snapshot for ordering and read each candidate fresh through `TryGetView` (DEVICE-V-005); do not replace that with one snapshot per pass.
- **Tests:** existing router, AutoTDP and desired-write tests stay green; a `TryGetView` test for a missing key and for a key whose descriptor was replaced. Filter: `--filter "FullyQualifiedName~DeviceCapabilityRouter|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceDesiredWrite"`.
- **Plan v2:** B082 (`TryGetView`, and the coordinator's `FindCapability`/`FindDescriptor`/`ManualTdpMode` switched to it), B089 (restore pass), B091 (AutoTDP's one snapshot per tick, since B091 is the batch that edits `AutoTdpService` and constructs it inside `PowerLimitOwner`).
- **Related:** DEVICE-V-005.

### DEVICE-022: DeviceOverlayBridge mixes Shell, Avalonia and the overlay contract

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceOverlayBridge.cs:9` (`using Avalonia.Threading`), `:1133-1136` (`OnAutoTdpStatusChanged` posts to the UI thread), `:21-313` (contract types declared in the bridge file); `src/WSGM/Overlay/OverlayWindow.Device.cs:35-37, 110-122` (`QueueLiveRefresh` coalesces with `Interlocked` and posts itself).
- **Problem:** the Shell bridge depends on Avalonia only to marshal one event that the overlay already marshals, and the file declares seven types including the overlay contract.
- **Best solution:** raise `Changed` directly in `OnAutoTdpStatusChanged` and delete the Avalonia using. Before deleting, grep every subscriber of `DeviceOverlayBridge.Changed`/`IDeviceOverlaySource.Changed` (today `OverlayWindow.Sources.cs:323` through `QueueLiveRefresh`) and confirm each one marshals itself; if one does not, leave the post. Move `DeviceOverlaySection`, `DeviceOverlayCapability`, `DeviceOverlayCategory`, `DeviceOverlayPluginSection`, `DeviceOverlayGlyphPreviewItem`, `DeviceOverlayGlyphPreview`, `DeviceOverlaySnapshot`, `DeviceHostRowIds`, `DeviceHostSelection` and `IDeviceOverlaySource` unchanged to `src/WSGM/Shell/DeviceOverlayContracts.cs`.
- **Tests:** existing bridge tests and `DevicePageCapture` UI baselines unchanged. Filters: `--filter "FullyQualifiedName~DeviceOverlayBridge"`; `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DevicePageCapture"`.
- **Plan v2:** B093.
- **Related:** DEVICE-023.

### DEVICE-023: interface default members exist to spare the preview source

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceOverlayBridge.cs:281-298` (`IDeviceOverlaySource.SetHostSelectionAsync` defaults to a `NotSupportedException` task, `UseGlobalAsync` to a silent no-op); `src/WSGM/Shell/SimulatedDeviceOverlaySource.cs:328` (implements `SetHostSelectionAsync`, not `UseGlobalAsync`), `:319-321`; `tests/WSGM.UiTests/Fakes/FakeDevice.cs` (relies on both defaults).
- **Problem:** default members hide which sources support what, and the UiTests fake silently depends on them.
- **Best solution:** remove both default bodies. `SimulatedDeviceOverlaySource` gains an explicit `UseGlobalAsync` returning `Task.CompletedTask` (today's effective behaviour). `FakeDevice` implements both members with today's effective behaviour (faulted `NotSupportedException` task for host selection, completed task for use-global) in the same batch. Leave the simulated source's preview data, including the raw enum names in controller choices, unchanged, because UI baselines are captured from it.
- **Tests:** UiTests build; `DevicePageCapture`, `ControllerNavigation` and preview exports unchanged. Filter: `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DevicePageCapture"`.
- **Plan v2:** B093.
- **Related:** DEVICE-022, DEVICE-024.

### DEVICE-025: power presets depend on a Steam toolkit type and a magic id

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DevicePowerPresets.cs:6, 66` (`ApplyAsync` returns `SteamUiToolkit.SteamUiCommandResult`), `:74`; `src/WSGM/Core/DevicePowerPresetReference.cs:34-38`; `src/WSGM/Shell/DevicePowerAssignments.cs:78-80, 234`; `src/WSGM/Shell/NativeQamPowerPresetService.cs`.
- **Problem:** a Shell power owner returns a toolkit type, and "custom"/"custom-values" are host sentinels shared with the Steam rows.
- **Best solution:** `DevicePowerPresets.ApplyAsync` returns a small host record `PowerPresetApplyResult(bool Applied, string? Detail)` (the two fields of today's `SteamUiCommandResult(bool, string?)`; the detail is null on success, `DevicePowerPresets.cs:152`); `NativeQamPowerPresetService` maps it to `SteamUiCommandResult`. The library side (B057) replaces the magic `custom` id in the toolkit with `Selectable` semantics. The persisted `"custom"` preset id in config stays as is (it is stored data, not a sentinel crossing a boundary).
- **Tests:** existing preset tests adapted to the record; NativeQam preset mapping produces today's results. Filter: `--filter "FullyQualifiedName~DevicePowerPresets|FullyQualifiedName~NativeQam"`.
- **Plan v2:** B093 (host side), B057 (library side, toolkit).
- **Related:** U03A-SUTS-005, TOOLKITCS-047, TOOLKITJS-V-006.

### DEVICE-026: AutoTdpService restore state sprawl and unclear dependency ownership

- **Severity:** low.
- **Where:** `src/WSGM/Shell/AutoTdpService.cs:90-100` (`_restoreTo`, `_restorePair`, `_restoreCycle`, `_restoreCapability`, `_powerMayDiffer`), reset at `:950-953, 1094-1098`; `:228` (disposes the injected frame-time source); `src/WSGM/Shell/AutoTdpTraceRecorder.cs:96` (disposes an injected context).
- **Problem:** one obligation lives in five fields reset in three places, and the service disposes dependencies it did not create.
- **Best solution:** the restore record lands in B011 (`AutoTdpRestorePoint?`, see DEVICE-V-001). In B091, `PowerLimitOwner` constructs AutoTDP and its frame-time source and trace recorder; whoever constructs a dependency disposes it. `AutoTdpService.DisposeAsync` stops disposing `_frametimes`, and `AutoTdpTraceRecorder` stops disposing its injected context (the owner does both after AutoTDP stopped).
- **Tests:** AutoTDP disposal leaves an injected frame-time fake undisposed; the owner's stop disposes it once. Filter: `--filter "FullyQualifiedName~AutoTdp"`.
- **Plan v2:** B011 (restore record), B091 (ownership).
- **Related:** DEVICE-V-001, DEVICE-009, DEVICE-027.

### DEVICE-027: AutoTDP disposal is unbounded

- **Severity:** low.
- **Where:** `src/WSGM/Shell/AutoTdpService.cs:209-219` (awaits the last stop and generation with `CancellationToken.None`), `:1057, 1073` (restore of up to three 5 s writes); `src/WSGM/Shell/ShellSession.Shutdown.cs:115-136`.
- **Problem:** the session awaits AutoTDP's restore with no deadline, ahead of every safety step.
- **Best solution:** add `AutoTdpService.StopAsync(Deadline deadline)` running today's sequence (cancel application writes, await last stop, stop generation, restore) with waits bounded by `deadline.CreateCancellationSource()` via `.WaitAsync(token)`. On expiry, log that the restore to N W is still in flight and unconfirmed, leave the write running, and never issue a second write. Do not dispose `_write` or anything the in-flight write uses when the deadline expired (retain, never free under an active call). `DisposeAsync` calls `StopAsync` with the deadline it is given. ShellSession passes the outer deadline (B083); after B091 the coordinator's shutdown task calls it as its first step.
- **Tests:** a restore write that never completes lets `StopAsync` return at the deadline and no second write is issued. Filter: `--filter "FullyQualifiedName~AutoTdpService"`.
- **Plan v2:** B083.
- **Related:** DEVICE-003, DEVICE-V-001; B140 step (2).

### DEVICE-028: per-command wall timer for an active-time deadline

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DevicePluginRuntime.cs:820-839` (`CommandOperation` uses `_deadline.CancelAfter(command.Deadline.Remaining)`); `src/WSGM.Device.Sdk/Lifecycle/Deadline.cs:84-100` (`CreateCancellationSource` uses `ActiveClock`).
- **Problem:** a command spanning a sleep times out on wake because the wall timer keeps running while the process is frozen.
- **Best solution:** in `CommandOperation`, create the deadline source with `command.Deadline.CreateCancellationSource()` instead of `new CancellationTokenSource()` plus `CancelAfter(...)`. It is already cancelled for an expired deadline and is cancelled by `ActiveClock` otherwise. `DeadlinePassed` keeps reading `_deadline.IsCancellationRequested`. No new clock plumbing (critic conflict 18: the SDK keeps `ActiveClock`; B043 makes its cancellation dispatch safe).
- **Tests:** with the test clock, a command whose deadline has active time left is not cancelled by elapsed wall time; an expired deadline cancels before dispatch (ties into DEVICE-007). Filter: `--filter "FullyQualifiedName~DevicePluginRuntime"`.
- **Plan v2:** B082 (after B043).
- **Related:** A02-F006 (host half), critic conflict 18.

### DEVICE-029: runtime calls outside the lifecycle lane

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DevicePluginRuntime.cs:444-450` (`ApplySettingsValuesAsync` checks admission then calls the plugin outside `_lifecycleGate`), `:172, 456` (`_cycleState` read non-volatile by `ExecuteCommandAsync` and `ApplyHapticOutputAsync`).
- **Problem:** settings application can overlap Stop or Suspend, and state reads can be stale.
- **Best solution:** `ApplySettingsValuesAsync` takes `_lifecycleGate` (with the caller's deadline) around its admission check and plugin call. Declare `_cycleState` `volatile` (enum with an int underlying type). Haptic frames stay outside any lock (high-rate rule).
- **Tests:** settings applied during a Stop wait for the stop and are refused after it. Filter: `--filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCycle"`.
- **Plan v2:** B088.
- **Related:** DEVICE-041.

### DEVICE-030: unknown stop status leaves the runtime half-stopped

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DevicePluginRuntime.cs:413-428` (`_ => throw new InvalidDataException(...)` after `_stopped = true`, before `PublishLifecycle` and `Complete`).
- **Problem:** an undefined `PluginStopStatus` leaves the runtime stopped but never publishes Disabled or sets `Completion`, which feeds DEVICE-003.
- **Best solution:** map the default arm to a Failed reason (`new CapabilityReason(CapabilityReasonCode.TransportFaulted, $"Plugin returned an unknown stop status {result.Status}.")`) and continue through `PublishLifecycle` and `Complete` as for Failed.
- **Tests:** a fixture returning `(PluginStopStatus)99` stops as Failed with `Completion` set. Filter: `--filter "FullyQualifiedName~DevicePluginRuntime"`.
- **Plan v2:** B081.
- **Related:** DEVICE-003.

### DEVICE-031: untracked fire-and-forget work in the domain

- **Severity:** low (verifier extended the list).
- **Where:** `src/WSGM/Shell/DeviceOemActionRouter.cs:229` (`_ = DispatchAsync(..., _lifetime.Token)`, token read outside the lock while `Dispose` at 88-89 may have disposed it); `src/WSGM/Shell/DeviceCapabilityRouter.cs:288` (`_ = ObserveLateCommandAsync`); `src/WSGM/Shell/DeviceCoordinator.cs:2823-2824` (`_ = restore.ContinueWith`); `src/WSGM/Shell/ApplicationPerformanceReconciler.cs:451-459` (`Save` continuations); `src/WSGM/Shell/PerformanceOverlayBridge.cs:168` (`_ = RefreshCpuBoostAsync()`); `src/WSGM/Shell/PluginSettingsCoordinator.cs:152` (`_ = Task.Run`), `:211` (`_ = PublishAndPushAsync`); `src/WSGM/Shell/DevicePluginRuntime.cs:215` (`_ = RemoveCommandWhenCompleteAsync`).
- **Problem:** none of these tasks is joined at shutdown or has its fault observed, so work can outlive its owner and touch disposed state.
- **Best solution:** each owner keeps its own small tracked set (the coordinator's existing `Observe`/`CompleteObservedAsync`/`RemoveObservedAsync` three-method pattern, private per owner) and joins it within its `StopAsync(Deadline)`. The OEM router reads `_lifetime.Token` under its lock and refuses dispatch after dispose. The coordinator's `ContinueWith` becomes an `await` inside the observed restore task. No shared task registry type.
- **Tests:** OEM dispatch after dispose is refused without `ObjectDisposedException`; stop joins an in-flight dispatch. Filter: `--filter "FullyQualifiedName~DeviceOemActionRouter|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~PluginSettings"`.
- **Plan v2:** B083 (the router observer is tracked in B082 with DEVICE-006; reconciler and CPU boost continuations follow their owners in B091).
- **Related:** DEVICE-006, DEVICE-011.

### DEVICE-032: lighting restore runs a second restore mechanism

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2576, 2832-2854` (readiness-triggered lighting pass, scheduling flag, user-command counter with an `Abandon` hook); `src/WSGM/Shell/DeviceLightingRestore.cs:21` (per-zone attempt budget of 3).
- **Problem:** two restore passes live in the coordinator. The lighting attempt budget answers a documented defect (writes refused right after wake), retries only Rejected writes (failed to dispatch) and stops at the first dispatched one without waiting for a readback, so it fits D9 and does not violate the no-uncertain-retry rule.
- **Best solution:** keep the behaviour byte for byte and move both passes into `src/WSGM/Shell/DeviceDesiredStateRestorer.cs` with `_profileReconcileGate`, `ReconcileDesiredValuesAsync`, `RestoreDesiredValueAsync`, `ReconciliationPriority`, `OnLightingStateChanged`, `_lightingRestore`, `_lightingRestoreScheduled`, `_resumeRestore`, `UpdateCapabilityDesiredContext`, the authored fan-profile members (`AuthoredProfileSelection`, `CycleAuthoredProfileAsync`, `SelectAuthoredProfileAsync`, `ApplyAuthoredProfilesAsync`), `DescribeCapability`, `ActivePluginScope`. The user-command counter is read through a constructor `Func<bool>` ("a user command is running"). The restore pass's `Include` also carries DEVICE-008's one condition (skip the PL2 candidate in unified mode). Add no further mechanism.
- **Tests:** existing `DeviceLightingRestoreTests` and desired-write tests unchanged. Filter: `--filter "FullyQualifiedName~DeviceLightingRestore|FullyQualifiedName~DeviceDesiredWrite|FullyQualifiedName~DeviceProfileApplier"`.
- **Plan v2:** B089.
- **Related:** DEVICE-V-005, DEVICE-021.

### DEVICE-033: PerformanceService constructs its RTSS launcher, truncates tokens and clamps arguments

- **Severity:** low.
- **Where:** `src/WSGM/Core/PerformanceService.cs:67` (`_launcher = new RtssLauncher()`), `:268-269, 961-970` (`SanitizeToken` takes 80 characters), `:71-72, 972-982` (`BoundInterval`/`BoundTimeout` clamp silently); construction sites `src/WSGM/Shell/ShellSession.cs:554`, `tests/WSGM.Tests/Builders/PerformanceBuilders.cs:60`, `tests/WSGM.Tests/Core/PerformanceServiceTests.cs:88`, `tests/WSGM.UiTests/Overlay/ApplicationProfilesViewTests.cs:111`.
- **Problem:** the launcher cannot be faked, origin and correlation ids are truncated (an arbitrary cap), and out-of-range constructor arguments are silently changed.
- **Best solution:** this lands after USER-001 (B005), which reshapes `RtssLauncher` to start RTSS with WSGM, hold the process and restart it on exit. Then: the `PerformanceService` constructor takes the `RtssLauncher` instance (built at the composition root in `ShellSession`; tests build one with the existing `start` delegate parameter, so no new interface is needed). `SanitizeToken` keeps control-character stripping and the fallback and drops `.Take(80)`. Replace `BoundInterval`/`BoundTimeout` with `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero)` and use the value as given. Nothing in this change may alter the B005 behaviour the maintainer decided: RTSS starts with WSGM and is kept alive at all times (restart on exit, no cooldown, the 5 s poll as backstop), is not launched while RTSS integration is off, and is never killed.
- **Tests:** a long origin is logged whole; a non-positive interval throws; existing `PerformanceServiceTests` (including the B005 keep-alive cases) stay green. Filter: `--filter "FullyQualifiedName~PerformanceService"`.
- **Plan v2:** B093 (after B005).
- **Related:** USER-001 (B005), critic 2.1.

### DEVICE-037: reconcile keys built from record ToString

- **Severity:** low.
- **Where:** `src/WSGM/Shell/ApplicationPerformanceReconciler.cs:554-590` (`ApplicationReconcileKeys.Take` builds `$"{applicationId}|{manual}"` and `$"{applicationId}|{variableRefresh}"`).
- **Problem:** the key depends on the compiler-generated `ToString` of `ManualTdpProfile` (a record) and allocates strings for a comparison.
- **Best solution:** store `(string? ApplicationId, ManualTdpProfile? Manual)?` and `(string? ApplicationId, bool? VariableRefresh)?` as fields and compare with `==`/`EqualityComparer<T>.Default` (record value equality). Behaviour identical.
- **Tests:** the existing reconcile-keys tests plus one where two different profiles with equal `ToString` output would collide (if such a case exists) or simply same/different inputs. Filter: `--filter "FullyQualifiedName~ApplicationPerformance"`.
- **Plan v2:** B091.
- **Related:** DEVICE-011.

### DEVICE-039: PluginHost per-instance state ownership is inverted

- **Severity:** low.
- **Where:** `src/WSGM/Shell/PluginHost.cs:262-267, 119-148` (`Health`, `State`, `StateGeneration`, `StateSequence` mutable on the registration, owned by the host lock), `:465` (`StopAsync` writes `Health` without that lock), `:14-22` (configuration store defaults to the real `ApplicationPluginConfigurationStore`).
- **Problem:** a health write races the host's snapshot, and a test that forgets the store argument silently uses the user's real plugin configuration.
- **Best solution:** the registration's `StopAsync` sets "Stopping" health through `host.Publish(this, ...)` (which takes the host lock) instead of assigning `Health`; make `Health`, `State`, `StateGeneration`, `StateSequence` setters private to the host path. Make the configuration store a required constructor parameter; `ShellSession` passes the real store and tests pass a fake.
- **Tests:** existing `PluginHostTests` with an explicit fake store; a stop while a snapshot is taken sees a consistent health row. Filter: `--filter "FullyQualifiedName~PluginHost"`.
- **Plan v2:** B093. B146 (SDK-033, SDK-049: registration state under the host lock) must build on this rather than redo it.
- **Related:** SDK-033, SDK-049.

### DEVICE-040: Windows power ports duplicate small pieces and owners use static singletons

- **Severity:** low.
- **Where:** `src/WSGM/Interop/WindowsCpuBoostApi.cs:10, 18, 33, 52-58`, `src/WSGM/Interop/WindowsHybridCoreApi.cs:9, 13, 19, 39` (duplicate `ReadActiveScheme`/`RefreshActiveScheme`; own `PowerWriteSettingAttributes` import); statics `src/WSGM/Core/CpuBoost.cs:73`, `HybridCores.cs:80`, `PowerSchemes.cs:20`, `WindowsPowerModes.cs:10`; used at `DeviceCoordinator.cs:134`, `ShellSession.cs:213`.
- **Problem:** active-scheme reads and refreshes are implemented in several ports, and owners reach static `Windows` singletons, so they cannot be faked and share no lock over the machine-global active scheme.
- **Best solution:** follow WINSVC-B2 as corrected (critic conflict 13): one injected `PowerSchemes` instance holds the lock over the active scheme and exposes the active-scheme read and refresh; `CpuBoost`, `HybridCores`, `WindowsPowerModes` and `PowerTimeouts` take that instance and drop their duplicated members; the four `Windows` statics are deleted and the composition root constructs the instances and passes them to the device coordinator and the reconciler. Keep the narrow ports. Leave the `PowerWriteSettingAttributes` import where it is (moving it into WDC is not needed). No `PowerPolicyLane` type.
- **Tests:** `--filter "FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~PowerTimeout|FullyQualifiedName~DisplayTimeout"`; UiTests `--filter "FullyQualifiedName~HybridCoreView|FullyQualifiedName~DevicePageCapture"`.
- **Plan v2:** B090 (winsvc executes; device consumes in B091).
- **Related:** WINSVC-009, WINSVC-030, critic conflict 13, B017 (readback gates removed first).

### DEVICE-041: lifecycle notifications race transitions

- **Severity:** low.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2763-2775` (`OnLifecycleState` runs on the plugin thread outside the transition gate and calls `SetState`), `:2803-2830` (`SetState` can start a restore pass), `State` a plain property read from the UI.
- **Problem:** a notification during a stop can start a spurious restore pass (refused by the closed router, so only noise), and `State` is written from two threads without ordering.
- **Checker correction:** every `PublishLifecycle` call (`DevicePluginRuntime.cs:304-428`) runs synchronously inside a runtime lifecycle method (`Raise` invokes handlers inline), so notifications normally arrive on the coordinator's own call under its transition gate. The race exists only for a stopping client and for a lifecycle call whose caller stopped waiting. Every stop and fault teardown sets `_client = null` before it starts (`DeviceCoordinator.cs:1228`, `:1384`) and sets Deactivating and Disabled itself.
- **Best solution:** drop notifications from a client that is no longer current; do not queue them. Subscribe per client with a handler that carries it (`state => OnLifecycleState(client, state)`, kept in one field so `DetachAsync` can unsubscribe it), and `OnLifecycleState` returns at once when `!ReferenceEquals(client, Volatile.Read(ref _client))`, before the existing generation check. A notification raised during a stop therefore never reaches `SetState` and cannot start a restore pass. `State` is backed by a `volatile` field for UI reads. Queuing notifications as tasks on the cycle lane (the earlier proposal) is rejected: `SemaphoreSlim` does not keep FIFO order, so a later Active could be applied before an earlier Degraded, start notifications would be applied after the start had already set the final state, and queued tasks would meet a disposed gate at shutdown.
- **Tests:** a lifecycle notification raised during `StopAsync` does not start a restore pass; a current-cycle notification still updates state. Filter: `--filter "FullyQualifiedName~DeviceCycle|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B088.
- **Related:** DEVICE-029.

### DEVICE-V-003: a passive detection keeps a live runtime, so every lock and sleep errors and reloads the package

- **Severity:** low (missed finding, verifier).
- **Where:** `src/WSGM/Shell/DevicePluginRuntime.cs:302-305` (Passive returned without `Plugin.StartAsync`), `:344, 540-543` (`SuspendAsync` throws "not active"), `:400-411` (`StopAsync` calls `Plugin.StopAsync` on a never-started plugin; `DisposeAsync` checks `_pluginStartAttempted` at 95); `src/WSGM/Shell/DeviceCoordinator.cs:1047-1089` (keeps `_client`, registration and supervision for a passive cycle); `src/WSGM/Shell/PluginHost.cs:611-613` (quarantine).
- **Problem:** on a machine the package does not match, every lock or sleep logs an error, quarantines the registration and, on unlock or wake, reloads the package; every passive teardown reports unverified because `ReleaseControllerAsync` throws "not active".
- **Best solution:** treat Passive like no package. In the cycle start, right after the start returns and before `SetDeviceDefinitionId`, `_pluginSettings.Attach` and the glyph load, when `activation.State` is Passive: tear the cycle down through the existing path (`registration.StopAsync` then `registration.DisposeAsync` while the registration exists, so the Device slot retires per DEVICE-001; after B081 `client.StopAsync` then `client.DisposeAsync`), detach the runtime events, capabilities and the OEM router, keep no `_client`, skip supervision, log the detection reason and set the state to Passive (the overlay keeps showing "Device passive"). Guard `DevicePluginRuntime.StopAsync` with `_pluginStartAttempted`: when no plugin start was attempted it calls no plugin code, sets `_stopped`, publishes Disabled with no reason and calls `Complete(Intentional, ...)`, so the stop counts as clean and `Completion` is set (DEVICE-003). The B009 stale-ledger show at start (DEVICE-005) runs on this path.
- **Tests:** a passive fixture suspends and resumes with zero plugin calls and no restart; teardown of a passive cycle reports clean; a passive cycle followed by integration off/on admits a new cycle (slot retired). Filter: `--filter "FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~DevicePluginRuntime"`.
- **Plan v2:** B012.
- **Related:** DEVICE-005, DEVICE-V-002.

### DEVICE-V-004: OEM router carries a dead generation field and a stale design comment

- **Severity:** low (missed finding, verifier).
- **Where:** `src/WSGM/Shell/DeviceOemActionRouter.cs:70` (`_cycleGeneration`), `:101-109` (`Attach`), `:131-137` (`Reset(long? cycleGeneration)`); `src/WSGM/Shell/DeviceCoordinator.cs:1657-1659` (comment), `:1680` (`_oemActions.Reset(activeGeneration)`).
- **Problem:** the field is written and never read; the comment and call describe a generation check that no longer exists.
- **Best solution:** delete `_cycleGeneration`, the generation parameter of `Attach` (if only stored there) and of `Reset`, update the call sites, and rewrite the coordinator comment to describe what `Reset` actually does. No behaviour change.
- **Tests:** existing OEM router tests. Filter: `--filter "FullyQualifiedName~DeviceOemActionRouter"`.
- **Plan v2:** B012.
- **Related:** none.

### DEVICE-V-005: the restore pass's per-candidate re-resolution is deliberate and must be kept

- **Severity:** low (missed finding, verifier; a constraint on DEVICE-021 and B089).
- **Where:** `src/WSGM/Shell/CapabilityDesiredReconciler.cs:79-85` (re-resolves each candidate because "a preceding command can take seconds").
- **Problem:** the review's "one snapshot per pass" would replay values from the old application's layer when the application or profile changes during a pass.
- **Best solution:** in `DeviceDesiredStateRestorer`, take one ordered snapshot for the ordering and, before each candidate's write, read that candidate fresh with `DeviceCapabilityRouter.TryGetView(key)` (desired value resolved against the current context). This keeps the behaviour and removes the quadratic full rebuild.
- **Tests:** a profile change between two candidates of one pass makes the second write use the new layer's value. Filter: `--filter "FullyQualifiedName~DeviceDesiredWrite|FullyQualifiedName~CapabilityDesired"`.
- **Plan v2:** B089.
- **Related:** DEVICE-021, DEVICE-032.

### DEVICE-024: dangling duplicate summary

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/SimulatedDeviceOverlaySource.cs:20-21`.
- **Problem:** two `<summary>` blocks; the first ("Two named profiles...") describes a removed field.
- **Best solution:** delete the first summary line.
- **Tests:** none (build only).
- **Plan v2:** B093.
- **Related:** DEVICE-023.

### DEVICE-034: opposite persistence order for RTSS and device values is undocumented as a rule

- **Severity:** nit.
- **Where:** `src/WSGM/Core/PerformanceService.cs:464-499` (RTSS persists before writing); `src/WSGM/Shell/DeviceCoordinator.cs:2351-2361` (device persists after the device applied); `docs/device-integration.md`.
- **Problem:** both orders are deliberate and commented in place, but nowhere stated as rules, so a refactor could "unify" them.
- **Best solution:** add a short paragraph to `docs/device-integration.md` stating both rules and why (RTSS settings are profile data applied by RTSS itself, device values are stored only after the hardware took them). No behaviour change. Run `npm run format`.
- **Tests:** none.
- **Plan v2:** B093.
- **Related:** USER-001 (B005 changes RTSS start, not this order).

### DEVICE-035: misplaced static helpers on the coordinator

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:2663-2684` (`SameValue`, used by `DeviceDesiredWriteAdmission.cs:72, 82`, `DeviceLightingRestore.cs:107`, `CapabilityUserWrites.cs:69`), `:2339-2342` (`PerformanceProfileOwnsRole`, used by `GpuCoordinator.cs:304, 327`), `:289-294` (`ReadOnAcPower`, static native call), `:2460` (`CycleAuthoredProfileAsync` calls `DeviceOverlayBridge.NextProfile`).
- **Problem:** shared pure helpers live on a 3,000-line coordinator and one native read cannot be faked.
- **Best solution:** `SameValue` moves to `src/WSGM/Core/CapabilityValues.cs` as `CapabilityValues.Same`; `PerformanceProfileOwnsRole` moves to `CapabilityUserWrites` (DEVICE-010); `NextProfile` moves next to the authored-profile code in `DeviceDesiredStateRestorer`; `ReadOnAcPower` moves into `PowerLimitOwner` as a constructor `Func<bool?>` defaulting at the composition root to the native call (B148 gives GPU its own injected `Func<bool?>`).
- **Tests:** existing admission, lighting and GPU tests. Filter: `--filter "FullyQualifiedName~DeviceDesiredWrite|FullyQualifiedName~DeviceLightingRestore|FullyQualifiedName~GpuCoordinator"`.
- **Plan v2:** B089 (`ReadOnAcPower` lands with B091).
- **Related:** DEVICE-010, SDK-023 (B148).

### DEVICE-036: inconsistent visibility

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:54, 175, 260, 525, 606, 648, 774` (`public sealed` class with public members and events); `src/WSGM/Shell/DeviceProfileApplier.cs:9` (`DeviceProfileApplyOutcome` public).
- **Problem:** public surface in an application whose collaborators are internal.
- **Best solution:** make `DeviceCoordinator` and `DeviceProfileApplyOutcome` internal along with their members (InternalsVisibleTo already covers WSGM.Tests and WSGM.UiTests). Members that implement an interface (`DisposeAsync` for `IAsyncDisposable`) stay `public`; if the compiler reports a public signature elsewhere that exposes either type, that member becomes internal too.
- **Tests:** build of WSGM, WSGM.Tests and WSGM.UiTests.
- **Plan v2:** B093.
- **Related:** none.

### DEVICE-038: logging under the router lock

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/DeviceCapabilityRouter.cs:570-603` (`LogAvailabilityChange`), `:620-640` (rejection `Log.Change`), both under `_gate` on the state-delta path.
- **Problem:** low rate and change-only, but the rule is no logging under locks.
- **Best solution:** build the log line (or a small list of lines) under the lock and write it after release in the same method.
- **Tests:** existing router tests. Filter: `--filter "FullyQualifiedName~DeviceCapabilityRouter"`.
- **Plan v2:** B082.
- **Related:** none.

### DEVICE-042: test-only static indirections

- **Severity:** nit.
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:946-1215, 1447-1582` (`RunCancellationSafeStartAsync`, `RunCanceledStartCleanupPolicyAsync`, `CancelLifetimeAndWaitForTransitionAsync`, `RunFreshBoundedCleanupAsync`, `RunClientTeardownWithStateNotificationsAsync`, `RunClientTeardownAsync`).
- **Problem:** internal statics exist only so tests can call them with fakes that perform the compensation being asserted.
- **Best solution:** inline them as private methods of `DeviceCycle` once the real-owner tests from DEVICE-014 exist, and delete their tests from `DeviceCoordinatorConcurrencyTests`.
- **Tests:** the DEVICE-014 `DeviceCycleTests` replace them. Filter: `--filter "FullyQualifiedName~DeviceCycle|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B088.
- **Related:** DEVICE-014.

## Refuted or no-change

No device finding id was dropped by the verifier, marked no-change in plan v2 or moved here by DECISIONS.md: none rests on the dropped security hardening, D14, the tray relay refusal, a read-only config mode, picker paging or readback re-arm machinery. All 47 ids appear above as sections. These sub-claims were refuted or overridden and are already reflected in the solutions:

- DEVICE-008, "`BoostWatts` becomes the only home of PL2" (plan v2's recommended D6 answer): overridden by the maintainer (D6, DECISIONS.md), who keeps the device-stored PL2 and drops `BoostWatts`.

- DEVICE-007, router half: the router's pre-gate `OperationCanceledException` is the correct pre-dispatch shape and stays; only the runtime's post-gate window is fixed.
- DEVICE-003, "WSGM exit waits forever": the outer application shutdown budget ends the process; the real consequence is skipped later shutdown steps and a possibly hidden pad.
- DEVICE-016, "starts with integration off" (solution checker): the pipe is how standalone Settings reports a running coordinator with integration off (`SettingsViewModel.DeviceSetup.cs:111-131`); not starting it would change the Settings text to "No running device coordinator detected". Only the hot loop is fixed.
- DEVICE-019, `ConfigureOemActions` as a constructor argument (solution checker): the OEM services are set once under the router's lock, never nulled, and depend on session objects created after the coordinator; nothing races, so it stays.
- DEVICE-018, "do not replace `_config` locally" (solution checker): the coordinator reads `_config` right after persisting (toggle state, `ToggleAutoTdpAsync`), so dropping the local assignment would regress the toggle; only the static store call is replaced.
- DEVICE-041, "notifications race transitions on the plugin thread" (solution checker, narrowed): notifications are raised inline from lifecycle calls the coordinator awaits; only a stopping or abandoned client's notifications race, and a current-client filter removes them without a queue.

The review's retain list (not finding ids) also stands unchanged: `AutoTdpController` and its constants, the AutoTDP trace types, `DeviceDesiredWriteAdmission`, `DeviceProfileApplier`, `DevicePrerequisiteSource`, `PerformanceOverlayBridge` (its `MaximumFrameLimit = 280` is a UI range), `DeviceCapabilityValidation` rules, `DeviceOemActionRouter` policy and `CommonPluginPackage`'s refusal of the Device category. Rejected mechanisms that must not be built: the catalog/lane router split, a per-publisher `CapabilityCommandPolicy` class, an AutoTDP runner/writer split, per-owner phase schedulers or budgets, and changing the router's pre-gate cancellation to Rejected.
