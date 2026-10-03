# Input domain review: adversarial verification

Verifier scope: everything in `_plan/refactor-2.1/review/input.md`, checked against master `1329813f`. Read in full: `Shell/ControllerManager.cs`,
`Input/ManagedControllerRouter.cs`, `Input/ControllerOutputRouter.cs`, `Input/ViiperControllerBackend.cs`, `Input/IControllerTargetBackend.cs`,
`Input/ManagedUiPad.cs`, `Input/UiPadAxes.cs`, `Input/ManagedControllerSampleValidator.cs`, `Input/ChordTracker.cs`,
`Input/GamepadChordWatcher.cs`, `Input/GamepadChordRecorder.cs`, `Input/KeyRecorder.cs`, `Shell/HidHideOwnership.cs`,
`Shell/HidHideControl.cs`, `Shell/ControllerProcessPriority.cs`, `Interop/NativeHidHide*.cs`, `Interop/NativeViiper.cs`,
`Interop/KeyboardInput.cs`, `Core/SteamInputBlocker.cs`, `Core/SteamInputManagement.cs`, `Core/SteamGuideChordMirror.cs`; the relevant parts
of `SteamInputShim.cs`, `GamepadService.cs`, `SdlGamepads.cs`, `GamepadNavigation.cs`, `TouchSwipeMonitor.cs`. Callers traced in
`DeviceCoordinator.cs` (ctor, ApplyConfigAsync, ShutdownAsync, ObserveRuntimeCompletionAsync, StopCycleUnderGateAsync,
RunClientTeardownAsync, SetControllerManagementUnderGateAsync, OnControllerTargetLost, DetachAsync), `ShellSession.Actions.cs`,
`ShellSession.cs:955-1030`, `ShellSession.Config.cs`, `ShellSession.Shutdown.cs`, `DeviceOemActionRouter.cs`, `Program.cs`,
`SettingsWindow.axaml.cs`, `OverlayController.cs`, HC `ControllerManager.cs`.

## Refuted

- **INPUT-003** (medium). The claimed defect, "the user-initiated controller-management-off path does not compensate, so the pad
  stays hidden while WSGM runs", does not occur. That path is `ApplyConfigAsync(config)` from `ShellSession.Config.cs:227` with
  the default token. `SetControllerManagementUnderGateAsync` passes that `CancellationToken.None` to `ReleaseAsync`
  (DeviceCoordinator.cs:1619-1625); the 6 s deadline goes only to the plugin release. The shutdown path also passes `None`
  (ShutdownAsync:815-818 into TeardownOwnerAsync). The runtime-fault path passes 15 s but uses `keepPhysicalHidden: true`, so no
  show is skipped, and the target-loss path compensates (1714-1728). No caller produces the claimed hidden pad. "Every caller
  passes a deadline-derived token" is wrong for two of the four paths. The three cancellation styles are a tidy-up at most and
  belong in the INPUT-001 change.
- **INPUT-035** (nit, priority write under `_stateGate`). The recommendation is harmful. `ControllerProcessPriority` documents
  "Calls are serialized by the controller manager's state gate" (ControllerProcessPriority.cs:8). Its `_active`/`_original`
  read-modify-write has no lock of its own. Moving the call out of `_stateGate` creates a race between SetState callers (drain
  worker, coordinator, target-loss threads) or forces a second lock (added mechanism). The call is one cheap syscall, so keep it
  under the gate.

## Corrected

- **INPUT-001** (high -> medium). The mechanism is real: `DisposeAsync` runs the HidHide show only after an untokened
  `_transition.WaitAsync()` and two unguarded awaits. The exception trigger is close to theoretical, though:
  `Output.DisposeAsync` awaits its worker with `SuppressThrowing`; backend native calls are wrapped by `SafeNative`; the router
  unsubscribes `TargetLost` before the backend raises it. The realistic trigger is a hung transition. `DeviceAttach` runs
  usbip.exe synchronously under the backend gate inside a manager transition, so the gate wait never returns and the show never
  runs. When a plugin client exists, the shutdown `ReleaseAsync` (with `None`) already shows HidHide. The dispose show is the
  only one when no client exists, for example after a fault restart that kept the pad hidden. Keep the fix: the show goes in
  `finally`, and the wait is bounded by the `Deadline` that `DeviceCoordinator.ShutdownAsync` already has, not a new internal
  5 s budget.
- **INPUT-005** (medium, unchanged severity for the allocation). A `SemaphoreSlim.WaitAsync()` on zero count allocates a TaskNode
  per sample whenever the drain is idle, and this is confirmed. The "Submit races disposal" part and the "after dispose every
  sample takes the Log lock" part cannot happen in practice. `DetachAsync` (DeviceCoordinator.cs:1745-1749) unsubscribes
  `Controllers.Submit` during the stop cycle, before `Controllers.DisposeAsync` runs (ShutdownAsync:851-854), and with no client
  nothing calls Submit. The race leaves only a microsecond window behind several awaited steps. Keep the Channel change for the
  allocation alone.
- **INPUT-006** (simplification). "The router's own Neutral state gates nothing" is false. Synthetic pulses
  (`SetSyntheticButtonAsync` -> `_router.RouteAsync`, ControllerManager.cs:787) bypass `ActivateSource`. The router's
  `State is not Active` check (ManagedControllerRouter.cs:154) is the only thing that refuses a Steam/QAM/rear-paddle pulse
  while the overlay holds UI capture, while forwarding is blocked for sleep, and between a capture release and the first clean
  sample. If the state is removed as proposed, those pulses reach Steam and the game in all three cases, which changes
  behaviour. Removal must move an explicit capture/forwarding check into `SetSyntheticButtonAsync`, and it must come after the
  INPUT-V-001 fix, because that refusal path is what latches the button today.
- **INPUT-011** (medium -> low). "Whichever path wins the Enabled comparison decides whether a refused write gets its elevated
  retry" is wrong. `SteamInputManagement.Apply` never compares `Enabled`. It always reconciles and always runs the
  elevation fallback (SteamInputManagement.cs:17-40), so a Settings save or Steam-page change in the same process gets its retry
  regardless of the reload watcher. The reload path fires without the fallback only for changes made by another process, which
  already ran `Apply` with its own fallback. What remains is an untracked `Task.Run` (ShellSession.Config.cs:63) and a duplicate
  code path, which is low.
- **INPUT-015** (low). The fact is confirmed: the backend `DisposeAsync` raises `TargetLost` under `_gate` (ViiperControllerBackend.cs:304).
  The recommendation for the router is not acceptable: "record loss under the router's own short lock, the next transition
  observes it". It adds a lock and defers the synchronous fault report that `ControllerManager.OnRouterTargetFaulted` and the
  coordinator's target-loss recovery depend on. Only move the raise in backend `DisposeAsync` outside the gate.
- **INPUT-027** (nit). The proposed replacement, "stop when the search returns to the starting element", can loop forever. The
  start is a non-TextBox, so a directional cycle among two TextBoxes (A -> B -> A) never returns to it. Keep the 64-step guard
  (it is a loop bound, not a content cap) or use a visited set. No change is the simpler choice.
- **INPUT-034** (nit -> part of INPUT-V-003). The 1 MiB read bound does not truncate, but it matters. A read that fails with
  ERROR_MORE_DATA makes `ShowUnderGateAsync` return before it turns the cloak off (HidHideOwnership.cs:295-300). Once the cloak
  write no longer depends on the read (INPUT-V-003), the bound is harmless.
- **INPUT-039** (test quality). The `SpinWait` polls in ManagedControllerRouterTests wait for the output router's background
  worker to drain the channel, not for time to pass. A fake `TimeProvider` removes the wait only from the pulse-stop test
  (198-219). The others need a completion signal from the fake sink, or they stay as they are. The other bullets stand. Add
  the missing synthetic-pulse tests (INPUT-V-001): there is no test of `PressSteamButtonAsync`/`PulseRearButtonAsync` at all.

## Confirmed (ids only)

INPUT-002, INPUT-004, INPUT-007, INPUT-008, INPUT-009, INPUT-010, INPUT-012, INPUT-013, INPUT-014, INPUT-016, INPUT-017, INPUT-018,
INPUT-019, INPUT-020, INPUT-021, INPUT-022, INPUT-023, INPUT-024, INPUT-025, INPUT-026, INPUT-028, INPUT-029, INPUT-030,
INPUT-031, INPUT-032, INPUT-033, INPUT-036, INPUT-037, INPUT-038.

Plan claims C1-C18 are confirmed as the reviewer judged them. INPUT-009 is stronger than the reviewer stated. Overlay-launched
Settings claims UI capture (OverlayController.cs:844, `ClaimUiSurface`), so the virtual target is neutral while Settings is
open. With an Xbox360 or DS4 target, Settings' SDL pad therefore reads a silent virtual pad, and with a Deck target SDL ignores
the virtual pad. Navigation then works only if SDL happens to see the physical pad through WSGM's HidHide allowlist. The
attended question stays, but a defect is the likely answer. INPUT-022 is confirmed with a scope caveat (see Batch problems,
INPUT-B5).

## Missed findings

### INPUT-V-001 (high) A refused synthetic press latches the button on the virtual pad
`src/WSGM/Shell/ControllerManager.cs:725-745`, `747-793`.
`SetSyntheticButtonAsync(enabled: true)` sets `_syntheticButtons |= button` (781) before `_router.RouteAsync` (787). If the
router returns false, `PulseButtonsAsync` returns at 727-731 without entering the try/finally that releases the button. The
router returns false when its state is Neutral (UI capture, forwarding block, before the first clean sample after capture),
for an out-of-range last sample, or when a publish is refused. The bit stays set. Every later live sample merges
`_syntheticButtons` into the routed report (559-561), so after capture ends the game and Steam see the button held until a
later successful pulse of the same button clears it.
Concrete case: the WSGM overlay is open (capture, router Neutral), and the user presses an OEM button mapped to
ToggleSteamQuickAccess or a rear paddle (`DeviceOemActionRouter.DispatchAsync` runs whatever the overlay state). On an Xbox360
or DS4 target, `PressSteamButtonAsync` latches Guide|A; on a Deck target it latches QuickAccess, and `PulseRearButtonAsync`
latches RearPaddle1/2. After the overlay closes, a held Guide turns every game button press into a Steam guide chord.
There are no tests for synthetic pulses (ControllerManagerTests has none).
Recommendation: put the press inside the existing try/finally (call `SetSyntheticButtonAsync(pressed, true)` in `try`, release
in `finally`). The release path already clears the bit before any check (758-761). This is one moved line and no new state.
Add tests: a pulse while captured or forwarding-blocked returns false and leaves `_syntheticButtons` empty, and the next live
sample after capture carries no synthetic bit.

### INPUT-V-002 (medium) A refused VIIPER removal leaves a stale router target that the next start "keeps"
`src/WSGM/Input/ManagedControllerRouter.cs:256-277`; `src/WSGM/Shell/ControllerManager.cs:979-1011`;
`src/WSGM/Input/ViiperControllerBackend.cs:196-199, 257-275`.
When `RemoveTargetAsync` returns false (VIIPER refused `DeviceRemove`, logged at ViiperControllerBackend.cs:766), the router sets
`State = Faulted` and throws, but keeps `Target`. The backend has already dropped its own handle (`_target = null`, 270).
`ReleaseAsync` logs the failure and sets Idle/Off. On the next start with the same target kind (re-enabling management, or the
restart after a fault), `ApplyTargetUnderGateAsync` sees `_router.Target.Kind == resolved.Target` and keeps the stale handle:
- Not captured: `_router.ActivateSource()` throws because the state is Faulted. The start reports Faulted and shows the pad,
  and every later same-kind start fails the same way for the rest of the session.
- Captured (overlay open during the restart): `NeutralizeAsync` sets the router to Neutral, `_neutral` is already true, and no
  publish happens. State becomes Active, the first clean sample activates the source, and every publish is silently refused
  by the backend's generation check (196-199, no TargetLost). The manager reports Active and the physical pad is hidden, but
  nothing reaches the game.
Recommendation (removes mechanism): `RemoveUnderGateAsync` clears `Target`, detaches output and sets Absent whatever the backend
reported, as the backend itself does, and still throws or logs the unverified removal. Add a test: the backend refuses
removal, then a same-kind start creates a fresh target.

### INPUT-V-003 (medium) The HidHide show gates the cloak-off write on a successful read and on readback
`src/WSGM/Shell/HidHideOwnership.cs:294-325`; `src/WSGM/Interop/NativeHidHide.cs:103-145, 186-219`.
`ShowUnderGateAsync` returns failure before it attempts `WriteActive(false)` when `_control.Read()` fails for any reason other
than "not installed": ERROR_MORE_DATA past the 1 MiB bound, access or sharing errors, a transient driver error. It also writes
cloak-off only when the read reported `Active`. `Remove()` can throw before the cloak write: `EncodeMultiString` throws
`ArgumentException` for a whitespace-only entry that another tool left in the list. HC's
`RestoreAllControllersForUninstall` calls `HidHide.SetCloaking(false)` first and unconditionally, then reads and unhides
(`_ref/HandheldCompanion/.../ControllerManager.cs:1491-1505`). This violates "never gate a write on readback" and "never strand
users on exit".
Recommendation: open, write cloak-off unconditionally first (skip only on "not installed"), then read and remove the ledger
entries, collecting failures as today. Fold this into INPUT-B1 next to INPUT-002.

### INPUT-V-004 (medium) A cloak left on by a crash persists through the next session when management or integration is off
`src/WSGM/Shell/ControllerManager.cs:256-261`; `src/WSGM/Shell/DeviceCoordinator.cs:474-482`.
The ledger and the cloak are consumed only on a leave path. After a crash (or a killed process) with management active, the
next WSGM start does not show the pad if controller management is now off: `StartAsync` with a disabled selection
"leaves HidHide alone". With Device Integration off, no cycle starts at all. The physical pad stays hidden from Steam and games
for that whole session, until its `ControllerManager.DisposeAsync` at exit. The same happens while management is on but the
plugin never publishes identities, until restart exhaustion.
Recommendation: one existing call, no new state. When a cycle starts with management off, or when the coordinator is created
with integration off, run `ShowPhysicalControllerAsync("no controller management this session")` once. The show is
idempotent, deletes the ledger, and turns off the cloak WSGM owns.

### INPUT-V-005 (low) B3's dispose and release refactor must keep the fault-restart hide
`src/WSGM/Shell/DeviceCoordinator.cs:1235-1243, 1412-1420, 1349`.
`keepPhysicalHidden` is true on the runtime-fault paths, and the only later show is `ScheduleFaultRecovery` exhaustion (1349)
or a subsequent leave. INPUT-B1's "show in finally" for `ReleaseAsync` must stay conditional on `!keepPhysicalHidden`. An
unconditional show briefly hands the pad to Steam during every plugin fault restart (the duplicate-input window the parameter
exists to prevent). The reviewer's B1 text says "HidHide show and SetState in finally" without that condition.
Recommendation: state the condition in INPUT-B1 and test it (a cancelled fault-restart release does not show).

## Batch problems

- **INPUT-B1** does not build within its listed files. Changing `ReleaseAsync` to take a `Deadline` changes four call sites in
  `Shell/DeviceCoordinator.cs` (1236, 1412, 1619, 1710), which the file list omits. Those methods are also targets of the
  device-coordinator domain's batches, so ownership has to be settled. The new "internal 5 s deadline" in `DisposeAsync` is an
  unjustified new budget: use the shutdown `Deadline` already in hand. B1 should also carry INPUT-V-001 (same file, safety),
  INPUT-V-003 and INPUT-V-004 (same HidHide owner), and keep `keepPhysicalHidden` (INPUT-V-005). Drop INPUT-003 as a defect
  (refuted).
- **INPUT-B2** silently drops behaviour: removing the router's Neutral state and `ActivateSource` removes the only refusal of
  synthetic pulses during capture or forwarding block (see Corrected, INPUT-006). It needs an explicit gate in
  `SetSyntheticButtonAsync` and must follow the INPUT-V-001 fix. Its INPUT-015 step ("record loss under router lock, observe on
  next transition") adds a lock and defers fault reporting without a defect. Keep only the backend `DisposeAsync` raise outside
  the gate. The `SpinWait`-to-fake-clock test change does not work as described (INPUT-039 correction).
- **INPUT-B3** understates its scope. INPUT-031's final-state parameter and the `ReportTargetFault` removal rewrite the
  target-loss recovery (DeviceCoordinator.cs:1683-1736), not only the ctor. It also edits Settings, Overlay, App and DesktopTray
  files owned by other domains. It bundles INPUT-035, which is refuted, so drop it. Split composition (INPUT-008/018) from the
  managed-pad reach (INPUT-009) so the UI change can be tested on its own.
- **INPUT-B4**: the "session-owned instance" for `SteamInputBlocker` cannot be purely session-owned. The panic handler
  (Program.cs:774-777) and the post-Avalonia shutdown (221-224) run outside any session and must reach the live lease. The plan
  must say where the process-level reference lives (for example, Program creates the instance and hands it to the session),
  or the static simply moves. At about 900 lines across the Core, Steam host, Settings, Overlay and Shell domains, it should
  be split: shim instance, then lease owner.
- **INPUT-B5**: "never fall back to an older file" must apply only to the size or fit decision. Skipping a `.vdf` in 443510
  that is not a chord layout, and moving past a read failure, are deliberate content-based selection
  (SteamGuideChordMirror.cs:578-583, 316-335) and must keep moving to the next candidate.
- **INPUT-B7** adds `GamepadNavigationOptions`, `IDirectionalControl` and `TimeProvider` injection into two classes for
  testability only, with no defect behind them. That conflicts with the simplify rule, so justify each one or drop it. Its
  INPUT-027 recommendation can loop forever (see Corrected).
- **Coverage gap**: no batch covers INPUT-V-001 or INPUT-V-002, and the domain acceptance lists no synthetic-pulse test.
