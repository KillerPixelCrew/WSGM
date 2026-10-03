# Input findings

Scope: controller management (`Shell/ControllerManager.cs`, `Input/ManagedControllerRouter.cs`, `Input/ControllerOutputRouter.cs`, `Input/ViiperControllerBackend.cs` and the report writers), HidHide ownership and interop (`Shell/HidHideOwnership.cs`, `Shell/HidHideControl.cs`, `Interop/NativeHidHide*.cs`), the Steam Input shim and lease (`Core/SteamInputShim.cs`, `Core/SteamInputBlocker.cs`, `Core/SteamInputManagement.cs`), the guide-chord mirror, touch edge swipes, gamepad navigation, recorders and `Interop/KeyboardInput.cs`. Sources: `_plan/refactor-2.1/review/input.md`, its verification `input.verify.md`, the completeness critic and plan v2. Line numbers are from master `1329813f`; anchor every edit by symbol.

45 finding ids exist for this area (44 from the review and verification, plus INPUT-C-001 from the solution check). 41 are written up below: 0 critical, 2 high, 15 medium, 17 low, 7 nit. Four are refuted or no-change (listed at the end). Plan v2 batches: B009 (never strand the controller, first and safety-critical), B073 (sample path), B074 (composition), B075 (managed pad in Settings), B076 and B077 (shim and lease instances), B078 (guide-chord mirror), B079 (touch input), B080 (navigation and recorders), B172 (HidHide adapter sharing; D3 decided: Device Lab becomes GPL and links the WSGM files as they are). Neighbouring batches that touch the same code: B006 (every exit runs the session cleanup; until it lands no programmatic exit reaches the controller release at all), B007 (uninstall keeps the HidHide ledger), B092 (moves controller start, loss and the haptic sink into `DeviceControllerHandoff` after B074), B113 (overlay activation after B079), B140 (session shutdown order).

Four places below deliberately differ from plan v2 or the review: INPUT-013 needs no new `GuideChordMirrorBinding` class and INPUT-012 keeps the lease release synchronous (both to remove mechanism), and INPUT-034 removes the 1 MiB HidHide read bound that the review and verifier would have kept, because D2 does not list it. INPUT-025 follows the maintainer's decision that Escape and the 3 s timeout keep the existing binding, which replaces plan v2 requirement 9. INPUT-008 also explains why the coordinator calls the controller factory instead of receiving a built manager. `LastInput`, `TouchKeyboard`, `HotkeyService`, `KeyboardService` and `VolumeFeedback` were not reviewed by this domain and have no findings here.

### INPUT-002: An unreadable HidHide ledger leaves the cloak on

- **Severity:** high
- **Where:** `src/WSGM/Shell/HidHideOwnership.cs:290-335` (`ShowUnderGateAsync`), `52-70` (`FileHidHideOwnershipStore.LoadAsync`), `338-360` (`AddAsync`); `src/WSGM/Shell/ControllerManager.cs:912-926`; `src/WSGM/Program.cs:632-658` (`RestoreHidHideForUninstallAsync`).
- **Problem:** `ShowUnderGateAsync` loads the ledger before it turns the cloak off. A truncated or corrupt `hidhide-ownership.json` (`JsonException`) or an IO or access error throws out of `ShowAsync`; `ControllerManager` logs "cleanup failed" and the cloak and WSGM's entries stay after exit. The uninstall restore fails the same way. On the hide side `AddAsync` throws the same exception out of `HideAsync` and `StartAsync`.
- **Best solution:** implement together with INPUT-V-003 in the same method. After the unconditional cloak-off write (V-003), load the ledger inside `try { } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)`. On failure: add the problem "ownership ledger unreadable (message); its entries were left in HidHide and the file was kept", remove no ledger-derived entries (without the ledger WSGM cannot tell its entries from another tool's, and it never removes an entry it did not add), still remove `extraApplications` (the uninstall passes WSGM's own executables, which do not depend on the ledger), never delete or rewrite the file, and return `Succeeded = false`. The cloak is already off, so the pad is back. In `AddAsync`, the same catch returns `new HidHideResult(false, "The HidHide ownership ledger could not be read; WSGM does not hide the controller without recording it.")`, so `StartUnderGateAsync` reports Unavailable and the pad stays visible instead of an exception escaping. Keep `IHidHideOwnershipStore.LoadAsync` as it is (null for absent, throws for unreadable): one catch at the two call sites is smaller than the review's new Absent/Loaded/Unreadable result type and leaves the in-memory fake untouched.
- **Tests:** `HidHideOwnershipTests`: with a store whose `LoadAsync` throws `JsonException`, `ShowAsync` writes cloak-off, writes neither list, never calls `DeleteAsync` and returns not succeeded; `ShowForUninstallAsync` with that store still removes the given executables; `HideAsync` with that store returns not succeeded and writes nothing; with the real file store and a corrupt file the file is byte-identical afterwards. `--filter "FullyQualifiedName~HidHideOwnershipTests"`.
- **Plan v2:** B009.
- **Related:** INPUT-V-003, INPUT-034, INSTALL-V-003 (B007: uninstall with `App\WSGM.exe` missing must not delete the ledger), plan v2 configuration rule "sidecars refuse to overwrite what they could not read".

### INPUT-V-001: A refused synthetic press latches the button on the virtual pad

- **Severity:** high (missed by the reviewer, found by the verifier)
- **Where:** `src/WSGM/Shell/ControllerManager.cs:725-745` (`PulseButtonsAsync`), `747-793` (`SetSyntheticButtonAsync`), `556-562` (merge into live samples); callers `PressSteamButtonAsync` (714-722), `PulseRearButtonAsync` (687-704), reached from `Shell/DeviceOemActionRouter.cs` whatever the overlay state.
- **Problem:** `SetSyntheticButtonAsync(enabled: true)` ORs the button into `_syntheticButtons` (781) before `_router.RouteAsync` (787). When the router refuses (its state is Neutral during UI capture, during a forwarding block and before the first clean sample after capture; the last sample is out of range; or the publish is refused), `PulseButtonsAsync` returns at 727-731, before the `try/finally` that releases the button. The bit stays set and `RouteAsync` merges it into every later live sample. Concrete case: the overlay is open and the user presses an OEM button mapped to Steam Quick Access or a rear paddle. On an Xbox360 or DS4 target Guide+A latches, on a Deck target QuickAccess latches, and RearPaddle1/2 latch on all. After the overlay closes the game and Steam see the button held until another pulse of the same button; a held Guide turns every button press into a Steam chord.
- **Best solution:** move the press into the existing `try/finally`:
  ```csharp
  try
  {
      if (!await SetSyntheticButtonAsync(pressed, true, cancellationToken).ConfigureAwait(false))
      {
          return false;
      }

      await Task.Delay(SyntheticPressInterval, cancellationToken).ConfigureAwait(false);
      return true;
  }
  finally
  {
      await SetSyntheticButtonAsync(pressed, false, CancellationToken.None).ConfigureAwait(false);
  }
  ```
  The release branch already clears the bit before any check (758-761), so a refused press leaves `_syntheticButtons` clean. The release publish after a refused press is itself refused and logs one more warning line; that is acceptable. No new state.
- **Tests:** in `tests/WSGM.Tests/Shell/ControllerManagerTests.cs` (there are no synthetic-pulse tests today): while a surface holds capture, `PressSteamButtonAsync(true)` returns false, `_syntheticButtons` is empty, and after `ReleaseUi` plus a clean sample the fake backend's published sample carries no Guide/A bit; the same after `BlockForwardingAsync`; a `PulseRearButtonAsync` cancelled during the delay still publishes the release. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B009.
- **Related:** INPUT-006 (the router state that refuses the press today must keep refusing after B073), INPUT-039.

### INPUT-001: Controller dispose and release can skip the HidHide show

- **Severity:** medium (verifier lowered from high)
- **Where:** `src/WSGM/Shell/ControllerManager.cs:147-183` (`DisposeAsync`), `809-879` (`ReleaseAsync`); `src/WSGM/Input/ManagedControllerRouter.cs:42-78`; `src/WSGM/Shell/DeviceCoordinator.cs:795-861` (`ShutdownAsync`, controller disposal at 851-854), release call sites 1236, 1412, 1619, 1710.
- **Problem:** `DisposeAsync` waits on `_transition` with no token, then awaits the drain and `_router.DisposeAsync()` (output router and backend), and only then runs `ShowPhysicalUnderGateAsync`. A transition that never returns (the realistic case: `DeviceAttach` runs usbip.exe synchronously under the backend gate inside a manager transition) blocks shutdown and the show never runs; an exception from router or backend disposal skips it too (unlikely: the output router suppresses, the backend wraps native calls). When no plugin client exists, for example after a fault restart that kept the pad hidden, this dispose show is the only cloak-off at exit. `ReleaseAsync` has the same shape: its gate waits and the neutralize and remove catches let cancellation escape before the show and `SetState` (the verifier showed no current caller produces a hidden pad, see INPUT-003, but the rule should be one).
- **Best solution:**
  1. Add `internal async ValueTask DisposeAsync(Deadline deadline)`. `DeviceCoordinator.ShutdownAsync` calls it with the deadline it already has (`() => Controllers.DisposeAsync(deadline)`). The parameterless `IAsyncDisposable.DisposeAsync()` forwards `Deadline.Never` and stays only for the tests' `await using`. Body: enter `_transition` with `_transition.Wait(0)` first and only otherwise wait through `deadline.CreateCancellationSource()` (an already expired deadline gives a cancelled token, and `WaitAsync` on a cancelled token refuses even a free gate, which would skip the target removal for nothing); mark `_disposed` and clear process priority under `_stateGate` whether or not the wait succeeded; complete the sample input (in B009 that is today's `_sampleAvailable.Release()`, after INPUT-005 `_samples.Writer.TryComplete()`); if the gate was entered, await the drain, dispose the router and then the backend (INPUT-016) inside one `try/catch` that logs; if not, log that a controller transition was still running at shutdown and leave the virtual pad to process exit (VIIPER runs in-process). The show runs in `finally` with `CancellationToken.None`: HidHide IOCTLs never wait on the transition or route gates, and the session's outer exit deadline (B006, B140) is the backstop for a hung driver call. Release `_transition` only if it was entered. Stop disposing `_transition`, `_routeGate` and the sample semaphore: they hold no handle, and a holder that outlived the deadline would otherwise throw `ObjectDisposedException` on release. No internal 5 s budget (verifier and plan v2). A controller start that is still running when the wait gives up cannot hide the pad again after this show: its token is linked to the coordinator lifetime (`OnPhysicalIdentities`), which `ShutdownAsync` cancels first, so a late `HideAsync` refuses at its gate.
  2. `ReleaseAsync(HandoffScope scope, Func<CancellationToken, Task> releasePhysicalAsync, Deadline deadline, CancellationToken cancellationToken, bool keepPhysicalHidden = false)`. One token from `deadline.CreateCancellationSource(cancellationToken)`. Enter `_transition` the same way as dispose (`Wait(0)`, then the token); if it cannot be entered in time, log it and skip the three steps. Otherwise drop the pending sample, set `_forwardingBlocked` under `_stateGate` first, then attempt neutralize (route gate plus `_router.NeutralizeAsync`), `releasePhysicalAsync` and `_router.RemoveAsync`, each in its own `catch (Exception ex) when (ex is not OutOfMemoryException)` that logs, cancellation included. In `finally`: show unless `keepPhysicalHidden` (INPUT-V-005), passing `CancellationToken.None` as dispose does (today the show takes the caller's token, and `ShowPhysicalUnderGateAsync` lets `OperationCanceledException` through, so an expired token would skip the show and `SetState` alike), and, if the gate was entered, `SetState` and release the gate. Call sites: 1236 and 1412 pass their existing `cleanupDeadline`/`deadline` and the step token; 1619 passes its 6 s `deadline` and `cancellationToken`; 1710 creates `var budget = Deadline.After(TimeSpan.FromSeconds(20))` once and passes it with the lifetime-linked token. Critic conflict 3: this batch owns the signature and the four call sites.
- **Tests:** `ControllerManagerTests`: dispose with a backend whose `DisposeAsync` throws still calls the HidHide show; dispose while a fake `CreateTargetAsync` never completes returns at an expired deadline and still shows; a release cancelled before neutralize still shows and ends Off/Idle; a release cancelled while another transition holds the gate still shows; dispose with `Deadline.Expired` and a free gate still disposes the router (the target is removed under the router's own cleanup budget) and shows; a release with `Deadline.Expired` and a free gate still shows and sets its final state. `--filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B009 (depends on B008), lands before the device shutdown batches.
- **Related:** INPUT-003 (refuted, its tidy-up is step 2), INPUT-004, INPUT-016, INPUT-019, INPUT-V-005, SESSION-V-001 (B006), critic conflict 3.

### INPUT-004: Plan B3 would keep the hide relation past process exit

- **Severity:** medium (plan defect)
- **Where:** `planning-corrections.md:35, 54` (Codex B3 controller-safety row and the retained-target sentence); superseded by plan v2 D1 and B140.
- **Problem:** the Codex plan released the physical controller only after the plugin's conflicting work was quiescent and kept the physical hide relation for a retained target until removal could finish. VIIPER's USB/IP server runs in WSGM's process, so the virtual pad disappears at exit, while the HidHide cloak is driver state that survives it. Keeping the hide relation past exit leaves the user with no controller.
- **Best solution:** no separate code. Do not implement the planning-corrections B3 controller row. The controller rule, implemented by INPUT-001 in B009 and ordered by B140 step 3, is: block forwarding, neutralize, ask the plugin to release, remove the target, then show HidHide; each step gets what remains of the deadline and is attempted whatever the previous one did; the show is attempted on every leave path including timeout and exception. The only exception is the fault-restart path (`keepPhysicalHidden`), whose later show is restart exhaustion (`ScheduleFaultRecovery`, DeviceCoordinator.cs:1349) or the next leave. No quiescence tracking: `_routeGate` and the router's `_transition` already serialize every conflicting controller operation.
- **Tests:** covered by the INPUT-001 and INPUT-V-005 tests; manual row M01-44 (tray Exit on a device session shows the pad with the cloak off).
- **Plan v2:** B009; B140; D1 decided: safety-first ordered steps under one deadline, with the planning-corrections B3 percentage cutoffs and preliminary drain dropped.
- **Related:** INPUT-001, INPUT-V-005, critic conflict 1, plan claim C8.

### INPUT-005: The sample drain allocates on every sample

- **Severity:** medium
- **Where:** `src/WSGM/Shell/ControllerManager.cs:74-77, 89-90` (fields), `441-467` (`Submit`), `476-507` (`DrainSamplesAsync`), `280-283` and `819-822` (pending-sample drops), `173-182`.
- **Problem:** after each routed sample the drain awaits `_sampleAvailable.WaitAsync()` at count zero, which allocates a waiter node per sample on the hottest path (every plugin report). The verifier refuted the second half of the review: the Submit/dispose race and "every sample after dispose takes the Log lock" cannot happen, because `DetachAsync` unsubscribes `Submit` before disposal. The allocation alone justifies the change.
- **Best solution:** replace `_sampleGate`, `_pendingSample` and `_sampleAvailable` with one `Channel<CanonicalControllerSample> _samples = Channel.CreateBounded<CanonicalControllerSample>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = false })`. `Submit` becomes `_samples.Writer.TryWrite(sample)`: latest wins, and after completion it returns false silently, so the `controller-sample-after-dispose` log line goes. The drain is `await foreach (var sample in _samples.Reader.ReadAllAsync().ConfigureAwait(false))` with no token: an uncancellable read reuses the channel's reader singleton, which `ControllerOutputRouter.RunAsync` already relies on. `StartUnderGateAsync` and `ReleaseAsync` drop a pending sample with `_samples.Reader.TryRead(out _)`; leave `SingleReader` at its default (false) because those calls read from another thread while the drain waits (the bounded channel has no single-reader fast path to lose). `DisposeAsync` calls `_samples.Writer.TryComplete()` and awaits the drain. Routing stays on the drain worker (the comment at 460-462 explains why).
- **Tests:** `ControllerManagerTests` through `Submit` (every test calls `RouteAsync` directly today): several submits while the fake backend's publish is blocked deliver the newest sample and drop the ones between; `Submit` after `DisposeAsync` neither throws nor publishes. `--filter "FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-001, INPUT-039, plan claim C6.

### INPUT-006: The router's Neutral/Active state duplicates the manager's forwarding decision

- **Severity:** medium, simplification (verifier corrected the premise)
- **Where:** `src/WSGM/Input/ManagedControllerRouter.cs:9-15, 36` (`ManagedTargetState`, `State`), `128-147` (`ActivateSource`), `149-183` (`RouteAsync`), `224-277`; `src/WSGM/Shell/ControllerManager.cs:551-554, 1003-1011, 652-681, 747-793`; `src/WSGM/Input/ControllerOutputRouter.cs:89-100` (`Attach`).
- **Problem:** `ControllerManager` decides per sample whether input may reach the game (`_uiCapture.Withholds`, `_forwardingBlocked`) and then re-arms the router with `ActivateSource()` whenever it sees Neutral. Each `ActivateSource` calls `Output.Attach`, which resets the output epoch, drops a queued rumble frame and re-logs "output active" on every capture release. Four layers (router Absent/Neutral/Active/Faulted, `_neutral`, `_forwardingBlocked`, `UiCaptureState`) express two facts. The verifier showed the router's Neutral state is not dead: it is the only thing that refuses a synthetic pulse (`SetSyntheticButtonAsync` routes straight to `_router.RouteAsync`) during capture, during a forwarding block and between a capture release and the first clean sample. Removing it without a replacement would send those pulses to Steam and the game.
- **Best solution:** after INPUT-V-001 and INPUT-V-002 have landed.
  1. Router: delete `ManagedTargetState`, `State` and `ActivateSource`. `Target` is the only state (null means absent; `OnTargetLost` and, after V-002, a refused removal already clear it). `RouteAsync` publishes whenever `Target` is set. `Output.Attach` runs once, in `CreateUnderGateAsync`.
  2. Manager: one bool `_gameLive`, guarded by `_stateGate`. `RouteAsync` sets it true in the lock block where `toUi` came out false. `NeutralizeRoutingAsync` (capture claim and `BlockForwardingAsync`), `ReleaseAsync`, `OnRouterTargetFaulted` and `ApplyTargetUnderGateAsync` when captured set it false; `ApplyTargetUnderGateAsync` when not captured sets it true (today's immediate `ActivateSource`). `SetSyntheticButtonAsync` refuses a press and a release alike while `_gameLive` is false, with the existing refusal log line and reason `forwarding-closed`; the release still clears its bit first, as today. The release must be refused too: once the router publishes whenever `Target` is set, a release routed during capture would publish `_lastSample`, the physical state the UI is reading, to the game. This moves the router's Neutral-to-Active edge into the object that already owns the decision, so every pulse refused today stays refused, including after `ResumeForwardingAsync` before a fresh sample arrives.
  3. Delete the `if (_router.State is ManagedTargetState.Neutral ...) _router.ActivateSource();` block in `RouteAsync` and the `else _router.ActivateSource();` branch in `ApplyTargetUnderGateAsync`.
  This beats keeping the router state and only dropping `Attach` from `ActivateSource`: it removes a state machine and the throw path `ActivateSource` has on a Faulted router (the failure INPUT-V-002 describes) instead of patching one call.
- **Tests:** router tests that assert `ManagedTargetState.Neutral` assert the published neutral report instead. `ControllerManagerTests`: a synthetic pulse is refused while captured, while forwarding is blocked, after `ResumeForwardingAsync` before a sample, and after capture release while a withheld button is still down; it is accepted after the first clean sample; a synthetic release during capture publishes nothing; after a capture release, the first clean sample does not reset the output epoch (a rumble frame queued after the claim's stop is still dispatched; the claim itself still stops output and starts a new epoch through `Output.StopAsync`, as today). `--filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~ManagedControllerRouterTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-007, INPUT-017, INPUT-V-001, INPUT-V-002.

### INPUT-007: An invalid sample stream does work and allocates on every sample

- **Severity:** medium
- **Where:** `src/WSGM/Input/ManagedControllerRouter.cs:159-172`; `src/WSGM/Input/ManagedControllerSampleValidator.cs:13-28`.
- **Problem:** for each out-of-range sample the router builds an interpolated message (an allocation) for `Log.Change`, sends a stop haptic frame to the plugin through `Output.StopAsync`, and sets Neutral, which the manager re-activates on the next sample. A plugin that emits NaN floods its own haptic lane with stop frames and the target with neutral publishes at sample rate.
- **Best solution:** with INPUT-006's router, an invalid sample does exactly this: `Log.Change("managed-controller-neutralized", RefusedSampleMessage, LogLevel.Warn)` with a `const string` message (the validator has a single reason, `out-of-range-sample`, so the message is a constant); then, only if `!_neutral`, call the existing gated `NeutralizeAsync` (stop output, publish one neutral report, `_neutral = true`; it stays under the router's `_transition` gate as today, so it cannot interleave with a removal); return false. A steady invalid stream then allocates nothing, publishes nothing, sends no stop frames and writes one log line (repeats are counted, not written). Replace `TryValidate(sample, out string reason)` with `bool IsValid(sample)` once nothing reads the reason.
- **Tests:** `ManagedControllerRouterTests`: ten invalid samples after a live one produce one neutral publish and one stop frame; a valid sample afterwards publishes normally. `--filter "FullyQualifiedName~ManagedControllerRouterTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-006, plan claim C6.

### INPUT-008: The production controller stack is built inside DeviceCoordinator

- **Severity:** medium
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:143-154`; `src/WSGM/Program.cs:636-638` (ledger path literal repeated); `src/WSGM/Shell/PluginHapticSink.cs`.
- **Problem:** the private `DeviceCoordinator` constructor builds the VIIPER backend, the native HidHide control, the ledger path from `Log.Directory`, the NT-path conversion of `Environment.ProcessPath` and the priority writer. The ledger file name is repeated in the uninstall restore. The coordinator cannot be constructed without the native stack.
- **Best solution:** add `internal static HidHideOwnership HidHideOwnership.ForUser(string userDataRoot)` (the native control plus `FileHidHideOwnershipStore` at `Path.Combine(userDataRoot, "hidhide-ownership.json")`, the only place that names the file) and `internal static ControllerManager ControllerManager.CreateProduction(string userDataRoot, IPhysicalHapticSink hapticSink)` (`ViiperControllerBackend`, `ForUser`, `NativeHidHide.FromDosPath(Environment.ProcessPath ?? throw ...)`, `ControllerProcessPriority`). The coordinator constructor calls `ControllerManager.CreateProduction(userDataRoot, _hapticSink)`, with the root handed in by `TryStartAsync` (`UserDataContext.Root` once B037 landed, `Log.Directory` before). `Program.RestoreHidHideForUninstallAsync` uses `HidHideOwnership.ForUser(root)`. Tests keep the internal constructor. Plan v2 says the coordinator "receives the built manager"; it cannot yet, because `PluginHapticSink` calls back into the coordinator's live client (`ApplyHapticOutputAsync`, DeviceCoordinator.cs:251-257), so passing the coordinator's sink into the factory is the simplest shape. B092 moves the sink and this call into `DeviceControllerHandoff`.
- **Tests:** no new behaviour; existing tests stay green. `--filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~HidHideOwnershipTests|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B074 (depends on B073 and B012), then B092.
- **Related:** Codex refactor-plan.md:43 (no ledger id), plan claim C1, critic conflict 3, B037, INPUT-032.

### INPUT-009: Settings opened from the overlay never reads the managed pad

- **Severity:** medium (verifier: a defect is the likely answer)
- **Where:** `src/WSGM/Settings/SettingsWindowServices.cs:20-27`; `src/WSGM/Overlay/OverlayController.cs:837-850` (`SettingsRequested`); `src/WSGM/Overlay/OverlayController.Lease.cs:10-13`; `src/WSGM/Shell/ShellSession.cs:963`; `src/WSGM/Shell/DesktopTray.cs:52-60`; `src/WSGM/App.axaml.cs:72`; `src/WSGM/Input/GamepadService.cs:203-207`; `tests/WSGM.UiTests/Infrastructure/UiFixture.cs:139`.
- **Problem:** only the overlay's `GamepadService` receives `UseManagedPad`. Settings opened from the overlay builds its own `GamepadService` without it. The overlay claims UI capture for the Settings surface, so the virtual target is neutral while Settings is open: with an Xbox360 or DS4 target, Settings' SDL poll reads a silent virtual pad, and with a Deck target SDL deliberately ignores 28de:1205. Navigation then works only if SDL happens to see the physical pad through WSGM's HidHide allowance. Input/AGENTS.md requires deterministic managed/SDL switching for every surface.
- **Best solution:** `SettingsWindowServices.Create(SettingsViewModel viewModel, ManagedUiPad? managedPad)` calls `gamepad.UseManagedPad(managedPad)` when it is not null. `OverlayController.UseManagedPad` also stores the pad in a `_managedPad` field, and `SettingsRequested` replaces `new SettingsWindow(true)` with `SettingsViewModel viewModel = new(); new SettingsWindow(viewModel, SettingsWindowServices.Create(viewModel, _managedPad), true)`; the `SettingsWindow(SettingsViewModel, bool)` constructor passes null. `DesktopTray` takes a `Func<ManagedUiPad?>` from `ShellSession` (`() => _deviceCoordinator?.Controllers.UiPad`) and passes its result; `App.axaml.cs` (Settings-only process) and the boot splash pass null; `UiFixture` passes null. `ManagedUiPad` is a polled, lock-free state holder, so two `GamepadService` instances can read it. No pad registry or new source abstraction.
- **Tests:** in `tests/WSGM.UiTests/Overlay/ControllerNavigationTests.cs`, a Settings window built with an active `ManagedUiPad` moves focus on a published DPadDown sample. `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests"`. Attended: Claw with a Deck target, open Settings from the overlay and navigate.
- **Plan v2:** B075 (after B074). B077 changes the same `Create` signature afterwards.
- **Related:** plan claim C10 (SdlGamepads is already the single pump; add no owner).

### INPUT-010: SteamInputShim keeps configuration and status in process statics

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamInputShim.cs:111-230`; callers `src/WSGM/Program.cs:348-357, 437-438`, `src/WSGM/Core/Steam.cs:286, 319`, `src/WSGM/Core/SteamInputManagement.cs:19-34, 55`, `src/WSGM/Core/SteamInputBlocker.cs` (`Acquire`), `src/WSGM/Shell/ShellSession.cs:226, 773`, `src/WSGM/Shell/ShellSession.Config.cs:55-64`, `src/WSGM/Settings/SettingsViewModel.Steam.cs:113`, `src/WSGM/Settings/SettingsViewModel.Save.cs:597-600`.
- **Problem:** `SetEnabled` mirrors a persisted setting into a static so code without configuration can read it; `Reconcile` and `Probe` read the mirror implicitly, so a stale mirror lets a cold-start reconcile undo a change saved elsewhere. `LastStatus` and `LoadedVector` are process globals read by the lease, `Steam.ColdStart`, Settings and the session.
- **Best solution:** `SteamInputShim` becomes an `internal sealed class` with instance `Lock`, `_lastStatus` and `_loadedVector`, and members `Reconcile(bool enabled, string reason)`, `Probe(bool enabled)`, `Remove(string reason)`, `RecordLoad(vector)`, `LastStatus`, `LoadedVector`. It keeps resolving `Steam.InstallDirectory` and the payload path per call, as today, because Steam can be installed or moved while WSGM runs; the pure static `ReconcileIn`, `ProbeIn`, `RemoveIn`, `FileNameFor` and `StartupTracePath` stay static and remain the test seam. Delete `_enabled`, `Enabled` and `SetEnabled`. `Program` creates one instance per process before Avalonia starts and passes it to App, ShellSession (`Steam.ColdStart` receives the instance and `config.SteamInputManagementEnabled` as arguments), `SteamInputBlocker`, `SteamInputManagement.Apply(shim, config, reason)` and `SettingsViewModel` (a constructor argument behind `SteamInputShimStatusText`). The one-shot modes (elevated apply, uninstall remove, setup) construct a local instance. Land B076 and B077 as one commit (execution protocol 7 allows consecutive batches that only make sense together) so the blocker takes the shim through its constructor and no interim static setter is ever written.
- **Tests:** `SteamInputShimTests` stay as they are (they exercise the `*In` functions). `--filter "FullyQualifiedName~SteamInputShimTests|FullyQualifiedName~SettingsViewModel"`.
- **Plan v2:** B076 (committed with B077).
- **Related:** PV11-005, U05-LFB-005, plan claim C15, INPUT-011, INPUT-012.

### INPUT-012: SteamInputBlocker is static, blocks release for 15 s and has no tests

- **Severity:** medium
- **Where:** `src/WSGM/Core/SteamInputBlocker.cs:15-46, 77-121, 218-245`; `src/WSGM/Program.cs:97, 221-224, 300, 493, 774-777`; `src/WSGM/Overlay/OverlayController.cs:49, 213, 282`; `src/WSGM/Overlay/OverlayController.Lease.cs:46, 56`; `src/WSGM/Settings/SettingsWindow.axaml.cs:48`; `src/WSGM/Settings/SettingsWindowServices.cs:26`.
- **Problem:** process-static state with two locks and two continuation chains. `ReleaseBestEffort` blocks synchronously for up to a fixed 15 s (`PendingReleaseWait`), longer than the session-end budget. The calls in `--unregister-shell`, `--restore-shell` and the crash-loop disarm run in a fresh process that never acquired a lease, so they do nothing (the lease is pipe-backed; Windows closes a crashed shell's pipe, as the comment at 298-299 already says). Core/AGENTS.md states a claim-balancing contract that no test covers.
- **Best solution:** `internal sealed class SteamInputBlocker(SteamInputShim shim, Func<ISteamInputLeaseHandle> acquireLease)` with `Hold`, `Drop`, `NewOwner`, `IsApplied` and `RecoveryWarningRaised`, and the existing owner and reconcile algorithm moved unchanged onto instance fields. `ISteamInputLeaseHandle` exposes only what the blocker uses from `SteamInputBlockLease` (the initial status and the release call); production wraps `new SteamInputClient(new SteamInputClientOptions { AllowInjection = false }).Acquire()`. Replace `ReleaseBestEffort` with `Release(string reason, Deadline deadline)`: the same steps, with the wait for a pending native release bounded by `deadline.Remaining` instead of 15 s. It stays synchronous on purpose (plan v2 calls it `ReleaseAsync`): the panic handler cannot await, and the post-UI-loop shutdown is already a blocking tail, so an async form would only be blocked on. `Program` creates the instance once per process (Shell, OverlayTest and Settings modes) and keeps it as the process root's field, so Panic and the post-Avalonia shutdown reach the live lease; it hands the instance to App, ShellSession, `OverlayController` and `SettingsWindowServices.Create`. `ApplicationRuntime` and its exit deadline only arrive in B111, after this batch, so in B077 the post-UI-loop release and Panic both pass `Deadline.After(TimeSpan.FromSeconds(15))`, today's value; B111 then hands the post-loop release the runtime's remaining exit deadline, which is what finally bounds it below the session-end budget. Drop the `Mode is Shell or OverlayTest || IsApplied` guards: a process can only release its own pipe lease, so a release with none is a no-op. Delete the three one-shot calls (Program.cs:97, 300, 493) and fix their comments. The overlay takes this concrete instance; no `ISteamInputLease` overlay port (critic conflict 24).
- **Tests:** new `tests/WSGM.Tests/Core/SteamInputBlockerTests.cs` with a fake lease handle: two owners keep one lease until both drop; a failing acquire leaves the owner set balanced; a drop during an acquire releases the lease once the acquire returns; `Release` waits for a pending native release only until the deadline. `--filter "FullyQualifiedName~SteamInputBlockerTests|FullyQualifiedName~SteamInputShimTests"`.
- **Plan v2:** B077 (committed with B076); the deadline hand-over lands with B111.
- **Related:** PV11-005 (partly), critic conflict 24, B113 and the OVERLAY-B4 batch (overlay ports), INPUT-009, INPUT-010.

### INPUT-013: Controller status events run on any thread; the chord-mirror rule lives in a session lambda

- **Severity:** medium
- **Where:** `src/WSGM/Shell/ControllerManager.cs:207-208, 1028-1045`; `src/WSGM/Shell/ShellSession.cs:955-975`; `src/WSGM/Shell/ShellSession.Config.cs:151`; `src/WSGM/Shell/ShellSession.Shutdown.cs:457`; `src/WSGM/Core/SteamGuideChordMirror.cs:144-173`.
- **Problem:** `StatusChanged` is raised from the sample drain, the VIIPER target-loss chain and coordinator threads. ShellSession subscribes an anonymous lambda that is never removed, writes `_steamDeckTargetActive` off the UI thread without synchronization and calls `_chordMirror.Apply`; the config reload reads the field on the UI thread.
- **Best solution:** the mirror already stores both inputs (`_enabled`, `_targetActive`) under its own `_gate` inside `Apply`. Replace `Apply(bool, bool)` with `SetEnabled(bool keepGuideChordEdits)` and `SetSteamDeckTargetActive(bool active)`, each updating its field under `_gate` and running the existing start or stop logic with the stored other value. ShellSession deletes `_steamDeckTargetActive`, subscribes a named `OnControllerStatusChanged(ControllerManagerStatus status) => _chordMirror?.SetSteamDeckTargetActive(status is { State: ControllerManagementState.Active, Target: ManagedControllerTarget.SteamDeckComposite })`, unsubscribes it in shutdown before `_chordMirror.Dispose()`, and the reload calls `_chordMirror?.SetEnabled(config.DeviceIntegration.KeepGuideChordEdits)`. ShellSession also calls `SetEnabled(_config.DeviceIntegration.KeepGuideChordEdits)` right after `SteamGuideChordMirror.ForInstalledSteam()`: today the setting reaches the mirror only as an argument of each status event (the lambda reads `_config`), so without this the mirror would stay disabled until the first config reload. Document `StatusChanged` as raised on any thread, with handlers that must not touch UI state. This replaces the review's and B078's new `GuideChordMirrorBinding` class: the mirror is already the synchronized owner of both inputs, so a binding object would only duplicate its two fields. B078 then adds no file and its filter drops `GuideChordMirrorBinding`.
- **Tests:** `SteamGuideChordMirrorTests`: target active plus enabled starts the mirror, clearing either restores Valve's file, and both call orders converge. `--filter "FullyQualifiedName~SteamGuideChordMirror"`.
- **Plan v2:** B078 (simplified as described).
- **Related:** INPUT-014, INPUT-022, INPUT-023.

### INPUT-039: Tests that prove nothing, and missing contract tests

- **Severity:** medium (test quality; verifier corrected the time-provider part)
- **Where:** `tests/WSGM.Tests/Input/InputTests.cs:134-141`; `tests/WSGM.Tests/Input/ControllerDependencyAdapterTests.cs:10-20`; `tests/WSGM.Tests/Input/ManagedControllerRouterTests.cs:9-27, 142-219`; `tests/WSGM.Tests/Fakes/FakeButtonSource.cs`; `tests/WSGM.UiTests/WSGM.UiTests.csproj:24`.
- **Problem:** `PadSnapshotKeepsItsControllerIdentityAndButtons` asserts record-struct getters. `ProductionBackendAdvertisesExactlyTheTargetsWithWireEncoders` copies the static `SupportedTargets` list. The router tests at 9-27 test the fake backend, and 176-195 mostly the fake. The output-router tests poll with `SpinWait`. There are no tests for the `Submit` drain, release under cancellation, dispose with a throwing router or backend, synthetic pulses, or a corrupt HidHide ledger. `FakeButtonSource` lives in WSGM.Tests but only WSGM.UiTests uses it, through a link.
- **Best solution:** delete the getter test. Extract `internal static int ReportLength(ManagedControllerTarget kind)` from `ViiperControllerBackend.SubmitUnderGate`'s switch (680-700) and replace the copied-list test with a theory over `SupportedTargets` asserting each length is positive and at most `SteamDeckNeptuneReport.Length`, the size of the stack buffer and `_lastFrame` the submit path uses. Delete the fake-only router test at 9-27; keep 176-195 only as a router ordering test. The pulse-stop test (198-219) drives `tests/WSGM.Tests/Fakes/ManualTimeProvider.cs` instead of real time; the other `SpinWait` loops stay, because they wait for the output worker to drain, not for time to pass. Add the missing tests named under INPUT-001, 002, 005, 006, 007, V-001, V-002 and V-003. Move `FakeButtonSource` to `tests/Shared` and link it from WSGM.UiTests. `GamepadChordWatcher` and `GamepadService` timing tests are not added: they would need the injection plan v2 dropped (INPUT-026, INPUT-029); `ChordTracker` remains the tested pure part.
- **Tests:** `--filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerDependencyAdapterTests"`; after the move, `--filter "FullyQualifiedName~InputTests"` and the UiTests `ControllerNavigationTests`.
- **Plan v2:** B073 (router, manager and adapter tests); B172 (the `FakeButtonSource` move and the getter test); per-finding tests in B009 and B077.
- **Related:** INPUT-V-001, INPUT-012.

### INPUT-V-002: A refused VIIPER removal leaves a stale router target that the next start keeps

- **Severity:** medium (found by the verifier)
- **Where:** `src/WSGM/Input/ManagedControllerRouter.cs:256-277` (`RemoveUnderGateAsync`); `src/WSGM/Shell/ControllerManager.cs:979-1011` (`ApplyTargetUnderGateAsync`); `src/WSGM/Input/ViiperControllerBackend.cs:196-199, 257-275`.
- **Problem:** when `RemoveTargetAsync` returns false (VIIPER refused `DeviceRemove`), the router sets Faulted and throws but keeps `Target`, while the backend has already dropped its handle. The next start with the same target kind (re-enabling management, or the restart after a fault) sees `_router.Target.Kind == resolved.Target` and keeps the stale handle. Not captured: `ActivateSource` throws on the Faulted router, the start reports Faulted and every later same-kind start fails the same way for the rest of the session. Captured (overlay open during the restart): the start reports Active and hides the physical pad, but every publish is refused by the backend's generation check without a `TargetLost`, so nothing reaches the game.
- **Best solution:** in `RemoveUnderGateAsync`, after neutralize and `Output.Detach`, clear the router's bookkeeping whatever the backend reported, exactly as the backend does: `var removed = await _backend.RemoveTargetAsync(...); Target = null; _neutral = true; State = ManagedTargetState.Absent;` then, if `!removed`, throw the existing `InvalidOperationException("Virtual target removal was not observed.")` so callers still log the failed removal. No retry: VIIPER refused the removal, so it failed to dispatch and is reported once (D9). Removes a failure mode instead of adding a guard.
- **Tests:** `ManagedControllerRouterTests`: a backend that refuses removal leaves `Target` null and throws; `ControllerManagerTests`: refused removal during release, then a same-kind start calls `CreateTargetAsync` again and publishes to the new generation. `--filter "FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B009.
- **Related:** INPUT-006 (after this, the router's Faulted state has no remaining job).

### INPUT-V-003: The HidHide show gates the cloak-off write on a successful read and on readback

- **Severity:** medium (found by the verifier)
- **Where:** `src/WSGM/Shell/HidHideOwnership.cs:290-335`; `src/WSGM/Interop/NativeHidHide.cs:97-145` (`TryReadMultiString`), `215-225` (`EncodeMultiString`); reference `_ref/HandheldCompanion/source/HandheldCompanion/HandheldCompanion.Managers/ControllerManager.cs:1487-1505`.
- **Problem:** `ShowUnderGateAsync` returns before attempting `WriteActive(false)` whenever `_control.Read()` fails for a reason other than "not installed" (ERROR_MORE_DATA past the 1 MiB read bound, access or sharing errors, a transient driver error), and it writes cloak-off only when the read reported the cloak active. `Remove` can also throw before the cloak write: `EncodeMultiString` throws `ArgumentException` for a whitespace-only entry another tool left in the list. HC's `RestoreAllControllersForUninstall` calls `HidHide.SetCloaking(false)` first and unconditionally, then reads and unhides. This breaks "never gate a write on readback" and "never strand users on exit".
- **Best solution:** reorder `ShowUnderGateAsync` to HC's order. (1) `var cloakError = _control.WriteActive(false);` first, always. If `HidHideControlState.IsNotInstalled(cloakError)`, delete the ledger and return success ("HidHide is not installed, so it hides nothing"), as the read path does today; otherwise record `cloak off (Win32 error N)` as a problem when it failed, or log "HidHide cloak turned off" when it succeeded. (2) Read the state; on failure record the problem and return not succeeded with the ledger kept (the pad is already visible). (3) Load the ledger (INPUT-002 catch), then remove the owned applications plus `extraApplications` and the owned devices; wrap each list write in `catch (ArgumentException ex)` that records `"{kind} list ({ex.Message})"` so one bad foreign entry cannot stop the other list. (4) Any problem keeps the ledger for the next leave and returns not succeeded; no retry. No new state; one write moves to the top.
- **Tests:** `HidHideOwnershipTests` with the fake control: a failing read still records exactly one `WriteActive(false)`, issued before the read; a cloak already reported off still gets the write; a whitespace entry in the device list makes the result not succeeded while the application list was still written and the cloak is off; not-installed on the cloak write deletes the ledger and succeeds. `--filter "FullyQualifiedName~HidHideOwnershipTests"`.
- **Plan v2:** B009.
- **Related:** INPUT-002, INPUT-034, INPUT-033 (inline IOCTLs).

### INPUT-V-004: A cloak left on by a crash persists through a session that runs no controller management

- **Severity:** medium (found by the verifier; same defect as DEVICE-005)
- **Where:** `src/WSGM/Shell/ControllerManager.cs:256-261` (`StartAsync` leaves HidHide alone for a disabled selection); `src/WSGM/Shell/DeviceCoordinator.cs:474-482` (`TryStartAsync` with integration off) and the cycle-start branches for no package, two packages and passive detection.
- **Problem:** the ledger and the cloak are consumed only on a leave path. After a crash or kill with management active, the next WSGM start never shows the pad when controller management is now off (`StartAsync` returns before touching HidHide), when Device Integration is off (no cycle starts), or when no package or two packages are installed or detection is passive. The physical pad stays hidden from Steam and games for that whole session, until `ControllerManager.DisposeAsync` at exit.
- **Best solution:** add `HidHideOwnership.ShowIfOwnedAsync(CancellationToken)`: under the gate, load the ledger; if it is absent (null), return without touching HidHide, because WSGM owns the cloak only after it recorded entries and must not turn off a cloak another tool set while WSGM never managed the pad; if it is present or unreadable, run `ShowUnderGateAsync([], token)`. Expose it as `ControllerManager.ShowLeftoverHiddenControllerAsync(string reason, CancellationToken)` (under `_transition`, skipped while Active, logging the reason). Call it from three places, with reason "no controller management this session": (a) in `StartCycleAsync` where `EnsureHidHideReadableAsync(controllerManagement, ...)` runs, when `controllerManagement` is false; this is the call that matters for "management off", because with management off the plugin does not take the controller, so no identity publication arrives and `StartAsync` is never called; (b) `StartAsync` when `!selection.Enabled` (replacing the "leaves HidHide alone" comment), for a publication that still arrives; (c) the coordinator once when `TryStartAsync` creates it with integration off (observed, not awaited), and in the cycle-start branch that ends without a plugin client (`discoveredPackage is null || !discoveredPackage.Valid`, which covers no package, two packages and a rejected package; B012's passive detection reuses it). One existing operation, no new state; see INPUT-C-001 for the ledger write that makes "present" cover every cloak WSGM turned on.
- **Tests:** `DeviceIntegrationOffTests`: a coordinator created with integration off and a ledger present shows once and deletes the ledger; with no ledger it never writes the cloak. A cycle started with controller management off and a ledger present shows once before the plugin starts. `ControllerManagerTests`: `StartAsync` with a disabled selection and a ledger shows. `--filter "FullyQualifiedName~DeviceIntegrationOff|FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~HidHideOwnershipTests"`.
- **Plan v2:** B009 (resolves DEVICE-005 as well); manual row M01-12 extension (crash ledger, then a start with integration off).
- **Related:** DEVICE-005, critic 2.7 (one never-strand group), B012 (passive detection keeps no runtime), INPUT-C-001.

### INPUT-C-001: A cloak WSGM turned on is not recorded when every entry was already listed

- **Severity:** medium (found by the solution check)
- **Where:** `src/WSGM/Shell/HidHideOwnership.cs:184-247` (`HideAsync`, cloak-on at 226-239), `338-360` (`AddAsync` records only entries it adds).
- **Problem:** the ledger records only list entries WSGM added. When WSGM's executable and the pad's instance paths are already listed (the user hid the pad and allowed WSGM in HidHide's own client, or another tool left them; HC's uninstaller turns the cloak off but its entries can stay), `HideAsync` turns the cloak on and writes no ledger. After a crash or kill, INPUT-V-004's `ShowIfOwnedAsync` finds no ledger and leaves the cloak on for a session without controller management, so the pad stays hidden. The normal leave is unaffected (`ShowAsync` turns the cloak off whatever the ledger says).
- **Best solution:** in `HideAsync`, right before `WriteActive(true)` when `!state.Active`, load the ledger and, if it is absent, save an empty `HidHideOwnershipLedger` (same INPUT-002 catch: an unreadable ledger returns not succeeded and writes nothing). The file's presence, not its delta count, then means "WSGM holds the cloak", which is exactly what `ShowIfOwnedAsync` tests, and `ShowUnderGateAsync` already deletes it on a clean show. No new field, kind or state.
- **Tests:** `HidHideOwnershipTests`: with every entry already listed and the cloak off, `HideAsync` saves a ledger before the cloak-on write; with the cloak already on it saves nothing; `ShowIfOwnedAsync` after that hide turns the cloak off and deletes the ledger. `--filter "FullyQualifiedName~HidHideOwnershipTests"`.
- **Plan v2:** B009 (with INPUT-V-004).
- **Related:** INPUT-V-004, INPUT-002.

### INPUT-011: The config-reload shim reconcile is untracked

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM/Shell/ShellSession.Config.cs:55-64` (`ApplySteamInputManagement`), call at 133; `src/WSGM/Core/SteamInputManagement.cs:17-41`; `src/WSGM/Settings/SettingsViewModel.Save.cs:597-600`; `src/WSGM/Shell/ShellSession.cs:772`.
- **Problem:** the verifier refuted the claimed race: `SteamInputManagement.Apply` never compares `Enabled`, it always reconciles and always runs the elevation fallback, so a Settings save or Steam-page change in the shell process gets its elevated retry. The reload path fires without the fallback only for changes saved by another process, which already ran `Apply` with its own fallback. What remains: the reload reconciles through an untracked `_ = Task.Run(...)` that shutdown never waits for, and it compares against the static mirror INPUT-010 deletes.
- **Best solution:** after INPUT-010, the reload compares the new `config.SteamInputManagementEnabled` with the value of the config it replaces (captured in the UI post before `_config = config`) and, only when it changed, stores `_steamInputReconcile = Task.Run(() => _steamInputShim.Reconcile(enabled, "settings-change"))`. ShellSession shutdown awaits `_steamInputReconcile` within the remaining deadline in the feature-owner step. The reload path keeps no elevation fallback, on purpose: the surface that saved the change already ran it, and a second prompt after a declined UAC would be a new workflow. All user-initiated writes stay on `SteamInputManagement.Apply(shim, config, reason)`.
- **Tests:** none beyond compilation; `--filter "FullyQualifiedName~SteamInputShimTests|FullyQualifiedName~SettingsViewModel"`.
- **Plan v2:** B076.
- **Related:** INPUT-010.

### INPUT-014: Manager state is read without synchronization

- **Severity:** low
- **Where:** `src/WSGM/Shell/ControllerManager.cs:123-136` (`State`, `Detail`, `Effective`), `216-224` (`Snapshot`), `945-948`, `995`, `1028-1045` (`SetState`).
- **Problem:** `State`, `Detail` and `Effective` are written under `_stateGate` in `SetState`, but `Effective` is also written outside it (947, 995) and all three are read without it, so `Snapshot()` can combine a state with another transition's target.
- **Best solution:** replace the three properties with one `ControllerManagerStatus _status` field (the record is already immutable). `SetState` builds the new status under `_stateGate` (target fields from the resolved target for Active and Idle, cleared otherwise, as today) and publishes it with `Volatile.Write`. The two outside writes become a helper that, under `_stateGate`, sets `_status = _status with { Target = ..., TargetSource = ..., ApplicationId = ... }` without raising `StatusChanged` (today's line 947 raises nothing either). `Snapshot()` returns `Volatile.Read(ref _status)`; `State` reads `_status.State`; `PressSteamButtonAsync` and `ReconcileTargetUnderGateAsync` read the target kind from the snapshot.
- **Tests:** `ControllerManagerTests`: after a running-application change that keeps the target kind, `Snapshot()` reports the new source and application id and no `StatusChanged` was raised. `--filter "FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B074.
- **Related:** INPUT-013, INPUT-036.

### INPUT-015: The backend raises TargetLost under its own gate

- **Severity:** low (verifier corrected the recommendation)
- **Where:** `src/WSGM/Input/ViiperControllerBackend.cs:283-327` (`DisposeAsync`, raise at 304) against the stated design at 228-233; `src/WSGM/Input/ManagedControllerRouter.cs:284-299`.
- **Problem:** `DisposeAsync` raises `TargetLost` while holding `_gate`, which the comment at 228-229 says must not happen because the handler stops the output sink, which comes back through the backend. It is harmless today only because the router unsubscribes before the backend is disposed.
- **Best solution:** in `DisposeAsync`, record `long? lostGeneration` under the gate and invoke `TargetLost` after `_gate.Release()`, as `PublishAsync` already does. Change nothing in the router: the review's "record loss under a router lock and observe it on the next transition" would add a lock and defer the synchronous fault report that `ControllerManager.OnRouterTargetFaulted` and the coordinator's target-loss recovery rely on.
- **Tests:** none automated: the change is inside `ViiperControllerBackend`, which cannot run without libviiper (same as INPUT-019), and a router test over the fake backend would test the fake, the kind of test INPUT-039 deletes. Build and `--filter "FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerDependencyAdapterTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-016, INPUT-019.

### INPUT-016: The backend's lifetime has two owners

- **Severity:** low
- **Where:** `src/WSGM/Input/ManagedControllerRouter.cs:76`; `src/WSGM/Shell/ControllerManager.cs:61, 117, 290`.
- **Problem:** the router disposes a backend it did not create, while the manager keeps the same backend for `DiscoverAsync`.
- **Best solution:** delete `await _backend.DisposeAsync()` from `ManagedControllerRouter.DisposeAsync` (the router still unsubscribes `TargetLost` and disposes the output router it created). `ControllerManager.DisposeAsync` disposes `_backend` right after `_router.DisposeAsync()`, inside the same logged `try` and before the HidHide show in `finally` (INPUT-001).
- **Tests:** adjust router tests that expected the router to dispose the backend; a manager test that disposal disposes the backend once. `--filter "FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-001.

### INPUT-017: Redundant backend contract members

- **Severity:** low, simplification
- **Where:** `src/WSGM/Input/IControllerTargetBackend.cs:10-22, 53-56`; `src/WSGM/Input/ViiperControllerBackend.cs:112-132, 238-254`; `src/WSGM/Input/ManagedControllerRouter.cs:248`; `src/WSGM/Shell/ControllerManager.cs:290-301`; `tests/WSGM.Tests/Input/ManagedControllerRouterTests.cs:295-380` (`DeterministicFakeControllerBackend`); `tests/WSGM.Tests/Shell/ControllerManagerTests.cs:90`.
- **Problem:** `NeutralizeAsync` is `PublishAsync` plus a throw. `ControllerBackendHealthState` is a two-value enum beside a nullable `ControllerBackendCapabilities` record that only repeats a static list.
- **Best solution:** remove `NeutralizeAsync` from the interface, the VIIPER backend and the fake; the router's neutralize calls `PublishAsync` and throws `InvalidOperationException("The controller backend could not write a neutral report to the virtual target.")` when it returns false (same message as today). Replace the health types with `internal sealed record ControllerBackendHealth(bool Ready, string Detail, IReadOnlyList<ManagedControllerTarget> Targets)`, `Targets` empty when not ready; delete `ControllerBackendHealthState` and `ControllerBackendCapabilities`. `StartUnderGateAsync` checks `health.Ready` and uses `health.Targets`.
- **Tests:** the existing router and manager tests with the reshaped fake. `--filter "FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-006.

### INPUT-018: usbip PATH discovery is untestable process mutation inside the backend

- **Severity:** low
- **Where:** `src/WSGM/Input/ViiperControllerBackend.cs:72` (`_usbipToolExposed`), `383` (call), `436-478` (`ExposeUsbipTool`, `UsbipInstallFolder`).
- **Problem:** a static once-flag gates a registry read (`UninstallEntries.Read()`) and a process PATH write, executed inside `DiscoverAsync` under the backend gate, with no test of the folder choice.
- **Best solution:** new `src/WSGM/Input/UsbipTool.cs` with a pure `internal static UsbipLocation Resolve(string path, IEnumerable<UninstallEntry> entries, string programFiles, Func<string, bool> fileExists)` returning `OnPath`, `Found(folder)` or `Missing`, with today's order: any PATH entry containing usbip.exe, then the first uninstall entry whose display name starts with "USBip" and has an install location, then `programFiles\USBip`, each accepted only if `folder\usbip.exe` exists. One deliberate difference from today: an uninstall entry whose folder lacks the exe now falls through to the Program Files fallback instead of ending the search with "not found" (today `UsbipInstallFolder` returns the first entry and the exe check happens afterwards); log texts stay. `UsbipTool.ExposeOnce()` keeps the once-flag, reads PATH and the uninstall entries, calls `Resolve`, writes PATH for `Found` and logs the same lines as today. The backend calls `UsbipTool.ExposeOnce()` where it calls `ExposeUsbipTool()` now.
- **Tests:** new `UsbipToolTests`: on PATH, from an uninstall entry, from the Program Files fallback, missing, and an uninstall entry whose folder lacks the exe falling through to the fallback. `--filter "FullyQualifiedName~UsbipTool"`.
- **Plan v2:** B074.
- **Related:** none.

### INPUT-019: Backend disposal disposes a semaphore other callers may still wait on

- **Severity:** low
- **Where:** `src/WSGM/Input/ViiperControllerBackend.cs:283-327` (`_gate.Dispose()` at 325).
- **Problem:** `_gate.Dispose()` in `finally` while a `PublishAsync` or `RemoveTargetAsync` is queued on the gate, or a second `DisposeAsync` passed the unsynchronized `_disposed` check, turns a clean `false` into `ObjectDisposedException`.
- **Best solution:** delete `_gate.Dispose()`. A `SemaphoreSlim` holds no unmanaged handle unless `AvailableWaitHandle` is touched, which it never is. Keep the `_disposed` checks so late callers return false.
- **Tests:** a backend-level test is not practical without libviiper; covered by review. Build and `--filter "FullyQualifiedName~ControllerDependencyAdapterTests"`.
- **Plan v2:** B073.
- **Related:** INPUT-001 (same rule for the manager's gates), INPUT-015.

### INPUT-020: Output router per-frame details

- **Severity:** low
- **Where:** `src/WSGM/Input/ControllerOutputRouter.cs:64` (`DroppedFrames`), `186, 207, 275, 371` (increments), `191-258` (`RunAsync`), `262-313` (`DispatchAsync`).
- **Problem:** `DroppedFrames++` runs both inside and outside `_gate`, so increments are lost while tests read the counter. `DispatchAsync` is a separate `async Task` whose state machine box is allocated per frame whenever the plugin sink completes asynchronously.
- **Best solution:** back `DroppedFrames` with an `int` field changed only through `Interlocked.Increment` and read with `Volatile.Read`. Inline the body of `DispatchAsync` into the `RunAsync` loop (its `return` statements become `continue`), so the loop's one state machine is reused for every frame. Pacing, epoch checks and the pulse timer stay as they are.
- **Tests:** existing output-router tests. `--filter "FullyQualifiedName~ManagedControllerRouterTests"`.
- **Plan v2:** B073.
- **Related:** plan claim C6.

### INPUT-022: The guide-chord size cap can mirror a stale layout

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamGuideChordMirror.cs:57` (`MaximumLayoutBytes`), `303-341` (candidate loop, cap at 307-311), `586-587` (newest-wins contract).
- **Problem:** the 4 MiB cap skips the newest autosave and the loop falls through to an older candidate, which is then mirrored over the template. The cap is also redundant: `FitToSize` already refuses anything that cannot match Valve's byte count. It breaks the no-arbitrary-limits rule and the newest-layout-wins contract.
- **Best solution:** delete `MaximumLayoutBytes` and the size check. The loop then reads the newest candidate that is a chord layout and stops there; if that layout does not fit, the existing "not mirrored" path runs and no older file is used. The verifier's scope note stays: a `.vdf` in 443510 that is not a chord layout, and a read failure, still move on to the next candidate, because that is content-based selection, not a size decision.
- **Tests:** `SteamGuideChordMirrorTests`: a newest chord layout that does not fit leaves the template untouched even when an older layout would fit; a newer non-chord `.vdf` is skipped and the older chord layout is mirrored. `--filter "FullyQualifiedName~SteamGuideChordMirror"`.
- **Plan v2:** B078.
- **Related:** INPUT-023.

### INPUT-024: The shim ownership check allocates its signature and catches everything

- **Severity:** low
- **Where:** `src/WSGM/Core/SteamInputShim.cs:566-585` (`IsOurs`).
- **Problem:** each `IsOurs` call encodes the signature string again and reads the whole file; a reconcile calls it up to eight times; a bare `catch` hides every exception.
- **Best solution:** a `private static readonly byte[] OwnershipSignatureBytes = Encoding.ASCII.GetBytes(OwnershipSignature);` used by `IsOurs`, and `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` returning false (fail closed, as documented). Every path `IsOurs` sees is built under a directory `Directory.Exists` already accepted, so IO and access errors are the only realistic failures; anything else would surface in `ReconcileIn`'s and `ProbeIn`'s own catch-all (Failed status) or, in `RemoveIn` and the deploy clean-up loop, which have no outer handler, as a logged failure of the uninstall step instead of a silent "not ours". Reading the whole DLL stays: the signature position is not fixed.
- **Tests:** existing `SteamInputShimTests`. `--filter "FullyQualifiedName~SteamInputShimTests"`.
- **Plan v2:** B076.
- **Related:** INPUT-038.

### INPUT-025: A cancelled or failed shortcut capture erases the existing binding

- **Severity:** low
- **Where:** `src/WSGM/Input/KeyRecorder.cs:36-40` (`Recorded`), `60-89` (`Start`), `135-154` (Escape in `HookProc`); `src/WSGM/Input/GamepadChordRecorder.cs:32-35` (expiry tick), `46-47` (`Recorded`), `79-95` (`Finish`); `src/WSGM/Input/ChordTracker.cs:22` (`RecordingExpiry`, 3 s); `src/WSGM/Settings/SettingsWindow.axaml.cs:565-574, 617-625`; `src/WSGM/Settings/SettingsViewModel.QuickAccess.cs:121-156` (`ApplyRecordedHotkey`, `ApplyRecordedChord`, `ClearHotkey`, `ClearChord`).
- **Problem:** when `SetWindowsHookExW` fails, `Start` raises `Recorded(Cleared())`, and the window applies it as a recorded shortcut, so a hook failure silently clears the draft binding. Escape during keyboard capture also reports `Cleared()`, and the chord recorder's 3 s no-input expiry reports empty buttons, which `ApplyRecordedChord` stores as a disabled chord. The maintainer decided that Escape and the timeout keep the existing binding and only the explicit Clear button clears it (this replaces plan v2 requirement 9).
- **Best solution:** one "ended without a capture" signal per recorder, applied as "stop recording, keep the binding".
  1. `KeyRecorder.Recorded` becomes `Action<HotkeyConfig?>`; null means nothing was captured. Escape in `HookProc` posts `Recorded(null)` instead of `Cleared()`; a hook failure in `Start` logs as today and raises `Recorded(null)`. `Cleared()` stays for `SettingsViewModel.ClearHotkey`; update the event's doc comment.
  2. `GamepadChordRecorder` keeps its signature (empty buttons already mean cancelled or timed out). `Finish` logs "Controller chord recording timed out; the chord is unchanged." for the expiry instead of "Recorded controller chord: None".
  3. `SettingsWindow`: the hotkey handler calls `_viewModel.ApplyRecordedHotkey(hotkey)` when `hotkey` is not null and `_viewModel.SetHotkeyRecording(false)` otherwise; the chord handler calls `_viewModel.ApplyRecordedChord(buttons, hold)` when `buttons != 0` and `_viewModel.SetChordRecording(false)` otherwise. Both still dispose their recorder. `_hotkey` and `_chord` are untouched on the null and empty paths.
  4. The Quick access page's Clear buttons stay the only way to clear: `SettingsWindow.ClearHotkey`/`ClearChord` and the view-model `ClearHotkey`/`ClearChord` are unchanged. Fix the `ApplyRecordedHotkey` parameter doc, which still names `KeyRecorder.Cleared` as a recorded value.
  No new state: a nullable event argument and two branches in the existing handlers.
- **Tests:** a hook cannot be installed or made to fail and the chord expiry needs a `DispatcherTimer` (Input/AGENTS.md; INPUT-026 and INPUT-029 keep the timers uninjected); add `SettingsViewModel` tests that `SetHotkeyRecording(true)` then `SetHotkeyRecording(false)` keeps the previous hotkey text, the same for the chord, and that `ClearHotkey` and `ClearChord` still clear. `--filter "FullyQualifiedName~WSGM.Tests.Input|FullyQualifiedName~SettingsViewModel"`. Attended: in Settings, start a keyboard recording and press Escape, start a chord recording and wait 3 s; both keep the previous binding, and Clear clears it.
- **Plan v2:** B080; decided: Escape and the 3 s timeout keep the existing binding, only an explicit Clear clears it.
- **Related:** requirement 9 (superseded by DECISIONS.md), INPUT-026, INPUT-029.

### INPUT-029: GamepadService owns an unrelated enum

- **Severity:** low (plan v2 kept only the move)
- **Where:** `src/WSGM/Input/GamepadService.cs:14-90`.
- **Problem:** the review found the edge, repeat and stale-pad logic untestable because it is coupled to `SdlGamepads.Update()` and a `DispatcherTimer`, and the file also declares the `GamepadButtons` enum used across Input, Overlay and Settings.
- **Best solution:** move `GamepadButtons` (lines 14-90, unchanged) to `src/WSGM/Input/GamepadButtons.cs`. Do not inject a poll source or a `TimeProvider` into `GamepadService`: plan v2 dropped that test-only plumbing under the simplify rule (same reasoning as INPUT-026).
- **Tests:** build only. `--filter "FullyQualifiedName~WSGM.Tests.Input"`.
- **Plan v2:** B080.
- **Related:** INPUT-026, INPUT-028.

### INPUT-030: TouchSwipeMonitor keeps a static registry and duplicated native state in one file

- **Severity:** low
- **Where:** `src/WSGM/Overlay/TouchSwipeMonitor.cs:65-184, 288-392, 442-581, 929-1056`; `src/WSGM/Overlay/OverlayController.Gestures.cs`; `tests/WSGM.Tests/Overlay/TouchSwipeMonitorTests.cs`.
- **Problem:** two `OverlayController` instances can exist (ShellSession.cs:864, SettingsWindow.axaml.cs:259), which is why raw-input registration is shared through a static registry and shared HWND, but each monitor re-reads `hRawInput` and builds its own preparsed-data cache per device. The window procedure reads `_disposed` without a barrier, and `Dispose` frees preparsed data the procedure may be using if disposal ever happens off the window thread. The file is 1,081 lines.
- **Best solution:** split into three files with thresholds and log texts unchanged. `RawTouchInput` (process-scoped, reference-counted by subscriptions, UI thread asserted) owns `WindowClassName`, the shared HWND creation and registration, `WndProc`, `ProcessRawInput`/`ProcessRawInputBuffer`, `_inputBuffer`, the device caps cache (`_devices`, `GetDeviceCaps`, `BuildDeviceCaps`, `EvictDevice`, `DeviceCaps`, `_usageBuffer`), `ProcessReport` and `DescribeForeground`, reads each `WM_INPUT` once and delivers decoded contact reports (tip, raw X/Y, contact count, caps) to subscribers; the last subscription's disposal unregisters and frees the caps on the window thread. `EdgeSwipeRecognizer` (pure) takes the existing constants and functions (`StartBandMm`, `StartBandFraction`, `MinStartBandPx`, `MaxStartBandPx`, `EntrySlopPx`, `EntryWindowMs`, `TriggerDistancePx`, `TriggerTimeMs`, `PhysicalSpanMm`, `StartBandPx`, `InwardDistance`, `SidewaysDistance`, `PickTriggeredEdge`, `GestureTrace`, `ScaleToScreen`). `TouchSwipeMonitor` stays the subscriber: `ScreenEdge`, `Triggered`, `Configure`, `Arm`, `Disarm`, edge flags, `OnContactDown`/`OnContactMove`, the `_dispatchPending` UI post, the diagnostics counters and a `Dispose` that releases its subscription. The static `Instances`, `Gate` and `_instanceSnapshot` registry goes. If the core domain's single owned MessageWindow lands first, `RawTouchInput` registers through it.
- **Tests:** the existing recognizer tests retargeted to `EdgeSwipeRecognizer` unchanged; the registration lives until the last subscriber disposes; a disposed subscriber receives nothing. `--filter "FullyQualifiedName~TouchSwipe|FullyQualifiedName~EdgeSwipe"`.
- **Plan v2:** B079; B113 (overlay activation) follows it.
- **Related:** Codex refactor-plan.md:115 (native window ownership), plan claim C7 (KeyRecorder's static hook slot is exempt).

### INPUT-031: Target-loss recovery sets Faulted twice through a four-hop chain

- **Severity:** low
- **Where:** `src/WSGM/Input/ManagedControllerRouter.cs:284-299`; `src/WSGM/Shell/ControllerManager.cs:191-205` (`OnRouterTargetFaulted`, `ReportTargetFault`); `src/WSGM/Shell/DeviceCoordinator.cs:1683-1736` (`OnControllerTargetLost`).
- **Problem:** the manager sets Faulted, the coordinator's recovery releases (which sets Idle) and then calls `ReportTargetFault` to set Faulted again, so observers see Faulted, Idle, Faulted.
- **Best solution:** add `string? faultDetail = null` as the last parameter of `ReleaseAsync` (INPUT-001 signature); when it is set, the final `SetState` is `Faulted` with that detail instead of Off or Idle. Delete `ReportTargetFault`. In `OnControllerTargetLost`, the release passes `faultDetail: detail` and is followed by `cleanupNeeded = false`, because after INPUT-001 the release itself shows the pad on every path; the `finally` keeps only `ShowPhysicalControllerAsync("virtual target lost", ...)` for the case where the release is never reached (the cancel-start wait times out), in which the manager is still Faulted from `OnRouterTargetFaulted`. Without the flag reset the pad would be shown twice. The plugin conversation stays in the coordinator (later `DeviceControllerHandoff`, B092).
- **Tests:** `ControllerManagerTests`: a release with `faultDetail` raises one `StatusChanged` ending in Faulted and shows the pad once; DeviceCoordinator target-loss test shows the physical pad once. `--filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~DeviceCoordinator"`.
- **Plan v2:** B074.
- **Related:** INPUT-001, B092.

### INPUT-032: The NativeHidHide split exists only for logging; Device Lab duplicates the control adapter

- **Severity:** low
- **Where:** `src/WSGM/Interop/NativeHidHide.Paths.cs:8-49` (`FromDosPath`); `src/WSGM/Shell/HidHideControl.cs:1-109`; `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj:38-39`; `src/WSGM.DeviceLab/Wizard/HidHideAllowance.cs:250-292` (`NativeHidHideDevice`).
- **Problem:** `FromDosPath` logs through WSGM.Core, so it sits in a partial that Device Lab cannot link, and the Lab re-implements the open-and-read sequence that `NativeHidHideControl` already has. The Lab already compiles `Interop/NativeHidHide.cs` through a `Compile Include` link; the licensing side is A02-F022, settled by D3 (Device Lab becomes GPL).
- **Best solution:** no relicensing, no shared folder and no duplicated file (D3). `FromDosPath` returns `(string Path, int Error, string? Skipped)` and does not log; `ControllerManager.CreateProduction` (INPUT-008) logs the two warnings exactly as today. Merge `NativeHidHide.Paths.cs` into `NativeHidHide.cs`. Move `HidHideEntryKind` from `Shell/HidHideOwnership.cs` into `Shell/HidHideControl.cs`, which then holds `HidHideControlState`, `IHidHideControl`, `NativeHidHideControl` and `HidHideEntryKind` and depends only on `WSGM.Interop` (no WSGM.Core logging). `WSGM.DeviceLab.csproj` adds `<Compile Include="..\WSGM\Shell\HidHideControl.cs" Link="Shared\HidHideControl.cs"/>` beside the existing `NativeHidHide.cs` link. The Lab's `NativeHidHideDevice.Read` maps `new NativeHidHideControl().Read()` to its `HidHideState` (not installed when `HidHideControlState.IsNotInstalled(error)`, otherwise the read error text as today), and `WriteApplications` calls `Write(HidHideEntryKind.Application, ...)`, keeping its own empty-entry check and message before the call. The Device Lab license file, csproj metadata, README/AGENTS and notices change with A02-F022 (B173), not here; if B173 moves the linked files, this link moves with them.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~HidHide"`; `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~HidHide"`.
- **Plan v2:** B172; decided: D3 relicenses Device Lab as GPL, so the Lab links the WSGM files and nothing is relicensed or duplicated; the batch no longer waits on D3.
- **Related:** A02-F022, D3, B173, INPUT-033.

### INPUT-V-005: The release refactor must keep the fault-restart hide

- **Severity:** low (found by the verifier)
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs:1235-1243, 1412-1420, 1349`; `src/WSGM/Shell/ControllerManager.cs:863-866`.
- **Problem:** `keepPhysicalHidden` is true on the runtime-fault paths, and the only later show is restart exhaustion (`ScheduleFaultRecovery`) or a later leave. The review's "show in finally" for `ReleaseAsync` did not carry that condition; an unconditional show hands the pad to Steam during every plugin fault restart, the duplicate-input window the parameter exists to prevent.
- **Best solution:** the `finally` in INPUT-001's `ReleaseAsync` shows only when `!keepPhysicalHidden`, including when the deadline expired or the transition gate could not be entered. Nothing else changes.
- **Tests:** `ControllerManagerTests`: a release with `keepPhysicalHidden: true` that is cancelled mid-way never calls the HidHide show; one with false does. `--filter "FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B009.
- **Related:** INPUT-001, INPUT-004.

### INPUT-021: Wire helpers duplicated across three report writers

- **Severity:** nit
- **Where:** `src/WSGM/Input/SteamDeckNeptuneReport.cs:200-234`; `src/WSGM/Input/Xbox360Report.cs:66-79`; `src/WSGM/Input/DualShock4Report.cs:122-153`.
- **Problem:** `Axis` (16-bit, clamped to -32767) is identical in the Deck and Xbox 360 writers, `Trigger` (8-bit) in the Xbox 360 and DS4 writers, `ScaledMotion` in the Deck and DS4 writers, and `Mask` exists in four overloads.
- **Best solution:** one `internal static class WireScale` in `src/WSGM/Input/WireScale.cs` with `Axis16(float)`, `Trigger8(float)` and `Motion16(float value, float scale)`, bodies copied from today's helpers. The format-specific ones stay in their writer: the Deck's 16-bit `Trigger` (with its signed-range remark), the DS4's `sbyte` `Axis` and `Touch`. Replace `Mask` calls with the inline expression `(buttons & flag) != 0 ? bit : 0`. Byte layouts do not change.
- **Tests:** the existing `SteamDeckNeptuneReportTests`, `Xbox360ReportTests` and `DualShock4ReportTests` must pass unchanged. `--filter "FullyQualifiedName~ReportTests"`.
- **Plan v2:** B073.
- **Related:** none.

### INPUT-023: Hashing used for string equality in the chord mirror

- **Severity:** nit
- **Where:** `src/WSGM/Core/SteamGuideChordMirror.cs:362-374` (`_overflowHash`), `409` (backup comparison), `623-626` (`Hash`).
- **Problem:** SHA-256 hashes are computed to compare two strings and to de-duplicate the overflow warning.
- **Best solution:** compare the backup and template text with `string.Equals(..., StringComparison.Ordinal)`. Log the overflow warning with `Log.Change("steam-chord-mirror", message, LogLevel.Warn)`, the key the successful mirror line already uses, so a mirror in between resets the de-duplication exactly as clearing `_overflowHash` does today. Delete `_overflowHash` and `Hash`.
- **Tests:** existing `SteamGuideChordMirrorTests`. `--filter "FullyQualifiedName~SteamGuideChordMirror"`.
- **Plan v2:** B078.
- **Related:** INPUT-022.

### INPUT-028: Public types in the application assembly

- **Severity:** nit
- **Where:** `GamepadService` and `GamepadButtons` (Input/GamepadService.cs:15, 96), `IUiButtonSource` (Input/UiButtonSource.cs:13), `GamepadNavigation` (Input/GamepadNavigation.cs:19), `GamepadChordRecorder`, `GamepadChordWatcher`, `KeyRecorder`, `TouchSwipeMonitor` and `ScreenEdge` (Overlay/TouchSwipeMonitor.cs:14, 45), `SteamInputShim` and its enums and status struct, `SteamGuideChordMirror`, `SteamInputBlocker`.
- **Problem:** these are `public` while their siblings are `internal`; nothing outside the assembly uses them (WSGM.Tests and WSGM.UiTests have `InternalsVisibleTo`).
- **Best solution:** make each `internal`. Where the compiler then reports inconsistent accessibility on a public member that exposes one of them (for example `SettingsViewModel.ApplyRecordedChord(GamepadButtons, bool)` or `OverlayController` members), make that member `internal` too, unless XAML binds to it; leave bound members and their types public. `SteamInputShim` and `SteamInputBlocker` become internal as part of INPUT-010 and INPUT-012.
- **Tests:** build of WSGM, WSGM.Tests and WSGM.UiTests; `--filter "FullyQualifiedName~WSGM.Tests.Input"`.
- **Plan v2:** B080.
- **Related:** INPUT-029.

### INPUT-033: HidHide IOCTL threading is inconsistent

- **Severity:** nit
- **Where:** `src/WSGM/Shell/HidHideOwnership.cs:150, 194, 231, 355` (`Task.Run`), `316, 374` (inline).
- **Problem:** reads, adds and the cloak-on go through `Task.Run`; removals and the cloak-off run inline. The IOCTLs are short, so the wrappers buy nothing the inline half lacks.
- **Best solution:** call `_control.Read()`, `Write` and `WriteActive` inline everywhere and delete the four `Task.Run` wrappers. The methods stay async for the ledger file IO. Justification is that the IOCTLs are short driver calls that the show path already runs inline, not that every caller is off the UI thread: a coordinator call that finds its gates free can still be on the UI thread when it reaches HidHide.
- **Tests:** `--filter "FullyQualifiedName~HidHide"`.
- **Plan v2:** B172 (independent of D3; decided: Device Lab becomes GPL, which only INPUT-032 relies on).
- **Related:** INPUT-032, INPUT-V-003.

### INPUT-034: The HidHide read stops at 1 MiB

- **Severity:** low (verifier rated it nit once INPUT-V-003 lands; raised because the bound is not on the D2 list)
- **Where:** `src/WSGM/Interop/NativeHidHide.cs:15` (`MaximumBufferBytes`), `97-145`.
- **Problem:** the read fails with ERROR_MORE_DATA past 1 MiB rather than truncating. It mattered most because a failed read stopped the cloak-off (HidHideOwnership.cs:295-300), which INPUT-V-003 fixes. It is still a cap of the kind the maintainer rule removes: D2 is decided as exactly the plan v2 list of byte bounds, every other count or length cap removed, and the HidHide read is not on it. A list past 1 MiB (another tool's long allowlist) would still make every hide fail and every show leave WSGM's entries behind.
- **Best solution:** delete `MaximumBufferBytes`. `TryReadMultiString` keeps doubling the buffer while the driver answers ERROR_INSUFFICIENT_BUFFER or ERROR_MORE_DATA and stops only at the type limit (`size > Array.MaxLength / 2`, where it returns ERROR_MORE_DATA as today); nothing else changes. No new D2 entry: the driver's list is the user's own HidHide configuration, and refusing to read it is what strands entries.
- **Tests:** none automated (the native read is not exercised in tests); covered for the show path by the INPUT-V-003 failing-read test.
- **Plan v2:** B009.
- **Related:** INPUT-V-003, INPUT-002.

### INPUT-036: Mixed lock types

- **Severity:** nit
- **Where:** `src/WSGM/Shell/ControllerManager.cs:78`.
- **Problem:** `_stateGate` is an `object` while its siblings use `System.Threading.Lock`.
- **Best solution:** `private readonly Lock _stateGate = new();`; the `lock` statements stay as they are.
- **Tests:** `--filter "FullyQualifiedName~ControllerManagerTests"`.
- **Plan v2:** B074.
- **Related:** INPUT-014.

### INPUT-037: KeyboardInput resolves the foreground layout per key record

- **Severity:** nit
- **Where:** `src/WSGM/Interop/KeyboardInput.cs:7-36` (`SendControlChord`), `49-53` (`Key`).
- **Problem:** each of up to six `Key` calls asks for the foreground window, its thread and its keyboard layout again.
- **Best solution:** resolve the layout once at the top of `SendControlChord` (`GetWindowThreadProcessId(GetForegroundWindow())`, `GetKeyboardLayout`) and pass it into `Key(ushort virtualKey, bool up, nint layout)` for the main records and the partial-send release records.
- **Tests:** build only (SendInput is not exercised in tests).
- **Plan v2:** B080.
- **Related:** none.

### INPUT-038: Unkeyed repeat logs in the shim

- **Severity:** nit
- **Where:** `src/WSGM/Core/SteamInputShim.cs:264` ("Steam is not installed"), `374` (removal lines).
- **Problem:** "Steam is not installed" is written on every reconcile, and removal lines do not name the vector.
- **Best solution:** `Log.Change("steam-input-shim.steam-missing", "Steam Input shim: Steam is not installed - nothing deployed.")`, and include `FileNameFor(vector)` in the removal and park lines.
- **Tests:** existing `SteamInputShimTests`. `--filter "FullyQualifiedName~SteamInputShimTests"`.
- **Plan v2:** B076.
- **Related:** INPUT-024.

## Refuted or no-change

- **INPUT-003** (refuted): no caller produces the claimed hidden pad; the user-initiated off path and shutdown pass `CancellationToken.None`, the fault path keeps the pad hidden on purpose, and target loss compensates. The single cancellation rule is folded into INPUT-001's `ReleaseAsync` change in B009.
- **INPUT-026** (no change): `GamepadNavigationOptions`, `IDirectionalControl` and `TimeProvider` injection fix no defect and would be test-only mechanism (simplify rule; plan v2 B080).
- **INPUT-027** (no change): the 64-step TextBox skip guard is a loop bound, not a content cap, and the proposed "stop at the start element" can loop forever between two TextBoxes.
- **INPUT-035** (refuted): `ControllerProcessPriority` relies on `_stateGate` to serialize its read-modify-write; moving the priority write out of the lock would add a race or a second lock. It stays under `_stateGate`.
