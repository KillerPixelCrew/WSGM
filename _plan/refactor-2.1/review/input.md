# Input domain review: controller manager, routing, VIIPER adapter, HidHide, Steam Input shim, navigation, chords

Reviewer scope: `src/WSGM/Input/**` (21 C# files plus AGENTS.md), `Shell/ControllerManager.cs`, `Core/SteamInputShim.cs`,
`Core/SteamGuideChordMirror.cs`, `Interop/NativeHidHide.cs`, `Interop/NativeHidHide.Paths.cs`, `Interop/NativeViiper.cs`,
`Interop/KeyboardInput.cs`, `Overlay/TouchSwipeMonitor.cs`, read in full. Boundary files read in full because they are the
domain's direct collaborators: `Shell/HidHideOwnership.cs`, `Shell/HidHideControl.cs`, `Shell/ControllerProcessPriority.cs`,
`Shell/ControllerTargetSelection.cs`, `Shell/PluginHapticSink.cs`, `Core/SteamInputBlocker.cs`, `Core/SteamInputManagement.cs`,
`Settings/SettingsWindowServices.cs`. Callers traced in `Shell/DeviceCoordinator.cs` (ctor 115-166, suspend/resume 606-709,
teardown 1220-1431, controller toggle 1606-1665, target-loss 1683-1830), `Shell/ShellSession*.cs`, `Program.cs`, `Core/Steam.cs`,
`Settings/SettingsWindow.axaml.cs`, `Overlay/OverlayController*.cs`. Tests read: `Shell/ControllerManagerTests.cs`,
`Input/ManagedControllerRouterTests.cs`, `Input/ControllerDependencyAdapterTests.cs`, `Input/InputTests.cs`; test inventories of
every other domain test file listed in audit-coverage U07A-INS.

Ledger status: the Claude audit never reached this domain. U07A-INS (audit-coverage.md:232-243) is one of the unstarted units;
`audit/A01/U07A-INS.md` does not exist. The only ledger entries touching it are PV11-005 / U05-LFB-005 (process-wide statics,
naming SteamInputShim) and A02-F022 (Device Lab links `NativeHidHide.cs`). None of the seven admitted batches touches this
domain. Every finding below except those three is NEW. HotkeyService, KeyboardService, VolumeFeedback, LastInput and
TouchKeyboard (in U07A/D02 ownership but not in this assignment) were not reviewed.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | DeviceCoordinator's private constructor creates "haptic sink, VIIPER backend, native HidHide control, real ledger" (refactor-plan.md:43) | accurate | DeviceCoordinator.cs:115 (private ctor), 143-154 | None. Also note it resolves `Environment.ProcessPath` and `Log.Directory` there (149-153), and Program.cs:636-638 duplicates the ledger path literal. |
| C2 | ControllerManager row: "Existing backend/router/HidHide seams; injected priority/input-lease owner; frozen VIIPER behind unchanged adapter" (refactor-plan.md:67) | partially | Seams exist: IControllerTargetBackend.cs:34-63, IPhysicalHapticSink.cs:7-14, HidHideControl.cs:24-31, HidHideOwnership.cs:34-41. Priority is already injected (ControllerManager.cs:101-107, ControllerProcessPriority.cs:23-33). ControllerManager has no input-lease involvement; the lease is SteamInputBlocker, claimed by OverlayController.Lease.cs:46/56 and SettingsWindowServices.cs:26. ViiperControllerBackend is WSGM code, not VIIPER. | Drop "input-lease owner" from the ControllerManager row (the lease belongs to the surfaces' owner, see INPUT-012). "Unchanged adapter" is wrong: only the VIIPER submodule and the libviiper C ABI that `NativeViiper.cs` declares are frozen; `ViiperControllerBackend` may change. |
| C3 | Preserve ControllerManager and ManagedControllerRouter as useful owners (refactor-plan.md:75) | accurate | Both are already constructor-injected and covered by behavioural tests (ControllerManagerTests 25 cases, ManagedControllerRouterTests 11 cases) | Keep both; simplify the duplicated state between them (INPUT-006) rather than add a layer. |
| C4 | "Every factory constructs inert objects. Explicit StartAsync opens admission" (refactor-plan.md:81) | stale for this domain | ControllerManager ctor starts `DrainSamplesAsync` (ControllerManager.cs:119); ControllerOutputRouter ctor starts `RunAsync` (ControllerOutputRouter.cs:61). Neither acquires a resource; the backend initialises lazily in DiscoverAsync (ViiperControllerBackend.cs:112-132). | Record an explicit exemption: an idle in-memory worker loop is not resource acquisition. Do not add Start methods to these types. |
| C5 | Shutdown order "neutralize controller output, release physical plugin controller, remove target and WSGM HidHide entries" (refactor-plan.md:91) | accurate | ControllerManager.ReleaseAsync 824-866: neutralize, `releasePhysicalAsync`, `_router.RemoveAsync`, `ShowPhysicalUnderGateAsync` | Order is right; the cancellation behaviour is not (INPUT-003). |
| C6 | "High-rate samples/haptic frames remain structs and latest-wins, with no per-sample allocation or logging" (refactor-plan.md:115) | partially | Structs: CanonicalControllerSample, HapticOutputFrame are `readonly record struct` (Device.Sdk Input/CanonicalControllerState.cs:122, HapticOutput.cs:14); latest-wins: ControllerManager.cs:454-457, ControllerOutputRouter.cs:29-35. But the sample drain allocates a SemaphoreSlim TaskNode per sample whenever it is idle (ControllerManager.cs:480) and an invalid stream builds a string and takes the global Log lock per sample (ManagedControllerRouter.cs:165-168). | "remain" is wrong: these must be fixed, not preserved (INPUT-005, INPUT-007, INPUT-020). |
| C7 | "Native windows use HWND user data/owned callback registrations rather than replaceable `_instance`; one MessageWindow owner hands out disposable subscriptions" (refactor-plan.md:115) | partially applicable | TouchSwipeMonitor uses a static registry plus shared HWND (TouchSwipeMonitor.cs:65-78, 106-118). KeyRecorder uses static `_active` (KeyRecorder.cs:27) because a WH_KEYBOARD_LL hook has no user-data parameter. | Apply to TouchSwipeMonitor as one owner with subscriptions (INPUT-030). Exempt KeyRecorder: a static slot is the only correct shape for a low-level hook; it is already bounded to one recording. |
| C8 | B3 controller-safety row: "Release physical ownership only if the plugin's conflicting work is quiescent, then remove target/HidHide" and "A retained target stays neutral ... and keeps the physical hide relation until physical release and target removal can safely finish" (planning-corrections.md:35, 54) | inaccurate and unsafe | The VIIPER USB/IP server runs in-process (NativeViiper.cs:9-12, ViiperControllerBackend.cs:38); the virtual pad disappears when WSGM exits. The HidHide cloak is driver state that persists after exit (HidHideOwnership.cs:106-113). HC model in code: "Each step is attempted whatever the one before did ... nothing waits for a readback" (ControllerManager.cs:804-808). | At exit (and on every leave path) HidHide entries and the cloak must be removed even when the plugin release or target removal is unverified; retaining the hide relation past process exit strands the user with no controller. Replace the quiescence condition for the controller with: every step attempted within the deadline, HidHide show always attempted last. |
| C9 | B3 busy-owner rows for "Device plugin command/lifecycle call" and "Controller route/output call" (planning-corrections.md:48-49) | partially | Route/neutralize/remove are already serialized by `_routeGate` (ControllerManager.cs:72, 525, 660, 826, 974) and router `_transition` (ManagedControllerRouter.cs:21) | No new quiescence tracking is needed for the controller lanes; the existing gates are the evidence. Pass one deadline into ReleaseAsync and DisposeAsync. |
| C10 | D02 objective: "one shared SDL pump owner" (versions/pre-AM01/task-briefs.md:723) | stale | SdlGamepads is already the single process-wide pump (SdlGamepads.cs:9-17); GamepadService instances only poll it | Do not build another owner. The real defect is that only the overlay's GamepadService sees the managed pad (INPUT-009). |
| C11 | D02: "Make process input lease/shim/keyboard services explicit session or standalone-preview owners" (task-briefs.md:723) | accurate | SteamInputShim statics SteamInputShim.cs:111-118; SteamInputBlocker statics SteamInputBlocker.cs:24-46 | Keep, scoped as INPUT-B4. |
| C12 | D02: "Keep ... neutral/physical release/target remove/HidHide cleanup and 20 s active-time fault recovery" (task-briefs.md:725) | partially | The 20 s is the budget of the target-loss recovery transaction in DeviceCoordinator.cs:1689, not an "active time"; it lives in DeviceCoordinator, outside D02's ownership list | Reword as "20 s target-loss recovery budget (DeviceCoordinator)" and assign it to the device-coordinator domain. |
| C13 | D02: "Join routing drains and zero haptics on all paths" (task-briefs.md:726) | mostly already true | Drain joined in DisposeAsync (ControllerManager.cs:174-175); stop frame on neutralize (ManagedControllerRouter.cs:245); target-loss stop is fire-and-forget but observed (ManagedControllerRouter.cs:292-296) | No new work beyond INPUT-001/003/005. |
| C14 | D02: "keep Claw chord implementation in package" (task-briefs.md:727); "working Claw chord state machine preserved" (refactor-plan.md:34) | accurate | Input/AGENTS.md:17-18; no Claw-specific code in src/WSGM/Input | None. WSGM's own ChordTracker (HC InputsManager model) also stays as is. |
| C15 | PV11-005 / U05-LFB-005: session drives process-wide mutable statics including SteamInputShim | accurate | `_enabled`, `_lastStatus`, `_loadedVector` (SteamInputShim.cs:113-118); `SetEnabled` called from Program.cs:348, 437, SteamInputManagement.cs:19, ShellSession.cs:226, ShellSession.Config.cs:62 | INPUT-010. SteamInputBlocker (not named in PV11-005) has the same shape (INPUT-012). |
| C16 | A02-F022: MIT Device Lab compiles NativeHidHide from the GPL product path | accurate | WSGM.DeviceLab.csproj:38-39; Lab re-implements the control read in Wizard/HidHideAllowance.cs:255-290 | Licensing decision stays open; see INPUT-032 for the duplication once decided. |
| C17 | Commands section: uncertain write never retried automatically; readback never gates (refactor-plan.md:111) | accurate for this domain | HidHide: "Writes are trusted when the driver accepts them; nothing is compared or retried" (HidHideOwnership.cs:112, 329); refused show keeps ledger for next exit. VIIPER attach retry (ViiperControllerBackend.cs:166-181) retries only a definite refusal after removing the device (361), documented wake behaviour | No change. Keep the attach retry; it is not an uncertain write. |
| C18 | D02 validation filter (task-briefs.md:733) | accurate but broad | `FullyQualifiedName~Input` also matches SteamInputShimTests and every `WSGM.Tests.Input` class | Fine as the batch-final filter; each batch below names a narrower one. |

## 2. Findings

Severity scale: critical, high, medium, low, nit. "NEW" means not in the Claude ledger or Codex audits.

### INPUT-001 (high) Controller dispose can skip the HidHide show
`Shell/ControllerManager.cs:147-183`. NEW.
`DisposeAsync` awaits `_router.DisposeAsync()` (which awaits `Output.DisposeAsync()` and `_backend.DisposeAsync()`,
ManagedControllerRouter.cs:75-77, neither guarded) and only then `ShowPhysicalUnderGateAsync`. Any exception from the
output router, backend `_gate` (ViiperControllerBackend.cs:290, 324-325) or native shutdown propagates and the cloak stays
on after WSGM exits. `_transition.WaitAsync()` (154) has no token, so a hung `ReleaseAsync` holding the gate blocks shutdown
indefinitely. This is the last HidHide cleanup on the shutdown path (DeviceCoordinator.cs:851-854) and violates "never strand
users on exit".
Recommendation: `DisposeAsync(Deadline)`; router/backend disposal in try, HidHide show in finally with its own short bounded
wait; the gate wait observes the deadline and, on timeout, still runs the show (HidHide IOCTLs do not share the route gate).

### INPUT-002 (high) Unreadable HidHide ledger leaves the cloak on
`Shell/HidHideOwnership.cs:290-335`, `52-70`. NEW.
`ShowUnderGateAsync` loads the ledger before turning the cloak off. A truncated or corrupt `hidhide-ownership.json`
(JsonException) or a transient IO error throws out of `ShowAsync`; ControllerManager logs "cleanup failed"
(ControllerManager.cs:922-925) and the cloak and WSGM's device entries stay. The same applies to uninstall
(Program.cs:632-658). The class contract (106-113) says the cloak goes off on every leave because WSGM owns it.
Recommendation: on an unreadable ledger, still turn the cloak off, keep the ledger file untouched (do not delete or rewrite
it), remove WSGM's own application entries by notation as uninstall already does, and return a non-succeeded result naming
the unreadable ledger. Distinguish absent (null) from unreadable in the store port, in line with the plan's
Loaded/Absent/Corrupt/Unreadable read model.

### INPUT-003 (medium) A cancelled release skips the show and the state update
`Shell/ControllerManager.cs:809-879`; callers DeviceCoordinator.cs:1616-1625, 1236-1250, 1410-1427, 1689-1712. NEW.
The neutralize and remove catches exclude `OperationCanceledException` (836, 858), and the `_transition`/`_routeGate` waits
(816, 826) throw on cancellation. Every caller passes a deadline-derived token (6, 15 or 20 s). When it fires mid-release
the method throws before `ShowPhysicalUnderGateAsync` and `SetState`. The target-loss path compensates (1714-1728); the
user-initiated "controller management off" path (1616-1626) does not, so the pad stays hidden while WSGM runs. The release
physical step catches everything but OOM (849), so the three steps handle cancellation three different ways.
Recommendation: ReleaseAsync takes a `Deadline`; each step is attempted with what remains of it, each failure (including
cancellation) is logged, the HidHide show and `SetState` run in finally. One rule for all steps, as HC's Close.

### INPUT-004 (medium) Plan B3 would retain the hide relation past exit
planning-corrections.md:35, 54. NEW (plan defect). See C8. The refinement in section 3 replaces it.

### INPUT-005 (medium) Sample drain allocates per sample; Submit races disposal
`Shell/ControllerManager.cs:441-467`, `476-507`, `173-182`. NEW.
`DrainSamplesAsync` awaits `_sampleAvailable.WaitAsync()` with a zero count after each sample, which allocates a TaskNode
per sample on the hottest path. `Submit` checks `_disposed` under `_sampleGate` while `DisposeAsync` sets it under
`_stateGate` (162-166) and then disposes `_sampleAvailable` (182), so a Submit that passed the check can call `Release()` on a
disposed semaphore and throw `ObjectDisposedException` into the plugin's HID reader thread. After dispose every sample also
enters the global `Log.Change` lock (448-450).
Recommendation: replace `_sampleGate`, `_pendingSample` and `_sampleAvailable` with
`Channel.CreateBounded<CanonicalControllerSample>(new(1) { FullMode = DropOldest, SingleReader = true })`, read with a
token-less `ReadAsync` (the reader singleton is reused, as ControllerOutputRouter.cs:193-195 already relies on), `TryWrite`
after completion simply returns false, and `ReleaseAsync` drains with `TryRead`. Three fields and one race removed.

### INPUT-006 (medium, simplification) The router's Neutral/Active state duplicates the manager's forwarding decision
`Input/ManagedControllerRouter.cs:9-15`, `128-147`, `149-183`; `Shell/ControllerManager.cs:540-554`. NEW.
ControllerManager already decides per sample whether a sample may reach the game (`_uiCapture.Withholds`,
`_forwardingBlocked`) and then re-arms the router with `ActivateSource()` whenever it sees `Neutral`. The router's own
`Neutral` state therefore gates nothing: an invalid sample sets it, and the very next sample re-activates it. Each
`ActivateSource` also calls `Output.Attach`, bumping the output epoch and dropping queued rumble on every capture release
(ControllerOutputRouter.cs:89-100). Four layers of state (router Absent/Neutral/Active/Faulted, `_neutral`, manager
`_forwardingBlocked`, `UiCaptureState`) express two facts: is there a target, and may this sample reach the game.
Recommendation: router keeps `Target`, `_neutral` and a faulted flag; `RouteAsync` publishes whenever a target exists and
publishes one neutral report for an invalid sample; ControllerManager owns the forwarding decision. Remove
`ActivateSource` and the per-activation `Output.Attach` (attach happens once at create/replace). Tests that assert
`ManagedTargetState.Neutral` change to assert the published neutral report.

### INPUT-007 (medium) Invalid-sample path does work and allocates per sample
`Input/ManagedControllerRouter.cs:159-172`. NEW.
For each refused sample: interpolated string allocation, global Log lock, `Output.StopAsync` (a stop haptic frame to the
plugin) and, because of INPUT-006's re-activation, a fresh neutral publish. A plugin emitting NaN floods its own haptic lane
with stop frames at sample rate. Fixed by INPUT-006: neutralize once while `_neutral` is false, constant keyed message.

### INPUT-008 (medium) Production controller stack is constructed inside DeviceCoordinator
`Shell/DeviceCoordinator.cs:143-154`; `Program.cs:636-638`. Covered by refactor-plan.md:43 (no ledger ID).
Native backend, native HidHide control, the per-user ledger path from `Log.Directory`, `Environment.ProcessPath`
conversion and the real priority writer are built in DeviceCoordinator's private constructor. The ledger path literal is
duplicated in Program's uninstall path.
Recommendation: one `ControllerManager.CreateProduction(string userDataRoot)` (or composition-root code) builds the stack;
DeviceCoordinator receives the manager. One `HidHideOwnership.ForUser(string userDataRoot)` factory serves both the session
and uninstall. When the config domain's `UserDataContext` lands, pass it instead of the root string.

### INPUT-009 (medium) Settings and boot splash never read the managed pad
`Settings/SettingsWindowServices.cs:20-27`; `Overlay/OverlayController.cs:843`; `Shell/BootSplash.cs:102`;
`Shell/ShellSession.cs:963`; `Input/SdlGamepads.cs:76`. NEW (needs one attended confirmation).
Only the overlay's GamepadService receives `UseManagedPad`. Settings opened from the overlay (`new SettingsWindow(true)`)
creates its own GamepadService with no managed pad. While a Steam Deck target is active SDL deliberately ignores
`0x28de/0x1205`, so that window's controller navigation depends on whatever else SDL can see. Input/AGENTS.md:6-7 requires
deterministic switching between managed and SDL sources for every surface.
Recommendation: no new owner. Pass the session's `ManagedUiPad?` into `SettingsWindowServices.Create` (overlay-launched
and resident Settings get it; standalone Desktop Settings and the boot splash get null) and call `UseManagedPad` there.

### INPUT-010 (medium) SteamInputShim keeps configuration and status in process statics
`Core/SteamInputShim.cs:111-227`; callers listed in C15. Covered: PV11-005, U05-LFB-005.
`SetEnabled` mirrors a persisted setting into a static so code without configuration can read it; `Reconcile`/`Probe`
read it implicitly; `LastStatus` and `LoadedVector` are process-global, read by SteamInputBlocker, Steam.ColdStart, Settings
and ShellSession. The algorithm itself (`ReconcileIn`, `ProbeIn`, `RemoveIn`) is already pure and well tested
(SteamInputShimTests 18 cases).
Recommendation: keep the pure functions static. Replace the static state with one instance
`SteamInputShim(string? steamDirectory, string payloadPath)` exposing `Reconcile(bool enabled, string reason)`, `Probe(bool
enabled)`, `Remove`, `LastStatus`, `LoadedVector`, owned by the session and handed to the Steam launcher, the lease owner and
Settings; one-shot modes construct their own. Delete `SetEnabled`/`Enabled`.

### INPUT-011 (medium) Three divergent shim apply paths
`Shell/ShellSession.Config.cs:55-64`, `Core/SteamInputManagement.cs:17-41`, `Settings/SettingsViewModel.Save.cs:599`,
`Shell/ShellSession.cs:772`. NEW.
The config-reload path reconciles through an untracked `Task.Run` with no elevation fallback; Settings save and the Steam
page use `SteamInputManagement.Apply` with the fallback. Whichever path wins the `Enabled` comparison decides whether a
refused write gets its elevated retry, and the reload task is not joined at shutdown.
Recommendation: one `SteamInputManagement.Apply(shim, config, reason)` used by all three, run through the session's tracked
background work.

### INPUT-012 (medium) SteamInputBlocker: static, blocking release, untested claims
`Core/SteamInputBlocker.cs:24-46`, `77-121`, `218-245`; `Program.cs:97, 221-224, 300, 493, 774-777`. Partly PV11-005.
Two locks and two `ContinueWith` chains in static state. `Acquire` holds `Sync` across `SteamInputShim.Probe()` (reads DLL
bytes) and the native pipe handshake. `ReleaseBestEffort` blocks synchronously up to 15 s (35, 236), longer than the plan's
5 s SessionEnd budget. There is no test file for it although Core/AGENTS.md states the claim-balancing contract. The calls
in the one-shot modes (Program.cs:97, 300, 493) run in a fresh process that never acquired a lease, so they do nothing;
the comment at Program.cs:298-299 already says a crashed shell's lease is released by Windows closing the pipe.
Recommendation: instance owner with an injected lease port (`Func<ILease>` over SteamInputClient) and the shim instance;
`ReleaseAsync(Deadline)`; delete the three no-op one-shot calls; unit tests for owner balancing, drop-while-acquiring and
release ordering with a fake port.

### INPUT-013 (medium) Controller status events run on arbitrary threads; chord-mirror policy sits in a ShellSession lambda
`Shell/ControllerManager.cs:1028-1045`; `Shell/ShellSession.cs:964-974`. NEW.
`StatusChanged` is raised from the drain worker, the VIIPER callback chain (via target loss) and coordinator threads.
ShellSession subscribes an anonymous lambda (never removed) that writes `_steamDeckTargetActive` off the UI thread and drives
`SteamGuideChordMirror.Apply`; ShellSession.Config.cs:151 reads that field on reload.
Recommendation: document `StatusChanged` as raised on any thread; move the "mirror follows a Steam Deck target" rule into a
small `GuideChordMirrorBinding` (subscribes, unsubscribes on dispose, holds the bool, applies on status and on config) owned
by the session composition.

### INPUT-014 (low) Unsynchronised reads of manager state
`Shell/ControllerManager.cs:123-136, 718, 898, 940-948, 995`. NEW.
`State`, `Detail` and `Effective` are written under `_stateGate` in `SetState`, but `Effective` is also written outside it
(947, 995) and all three are read without it. Recommendation: hold one immutable `ControllerManagerStatus` snapshot,
replaced atomically (Volatile) in `SetState`; `Snapshot()` returns it.

### INPUT-015 (low) Target-loss handling mutates router state outside its gate; backend raises TargetLost under its gate
`Input/ManagedControllerRouter.cs:284-299`; `Input/ViiperControllerBackend.cs:283-305` vs `228-233`. NEW.
`OnTargetLost` runs on the backend's calling thread and clears `Target`/`State` while a transition may hold `_transition`.
`DisposeAsync` raises `TargetLost` while holding `_gate`, contradicting the reason given at 228-229. Benign today because
the router unsubscribes before disposing the backend (ManagedControllerRouter.cs:58). Recommendation: raise outside the gate
in both places; the router records loss under its own short lock and the next transition observes it.

### INPUT-016 (low) Backend lifetime has two owners
`Input/ManagedControllerRouter.cs:76`; `Shell/ControllerManager.cs:61, 117, 290`. NEW.
The router disposes a backend it did not create; the manager keeps the same backend for `DiscoverAsync`. Recommendation: the
manager owns and disposes the backend after the router.

### INPUT-017 (low, simplification) Redundant backend contract members
`Input/IControllerTargetBackend.cs:10-22, 53-56`; `Input/ViiperControllerBackend.cs:98-103, 244-254`. NEW.
`NeutralizeAsync` is `PublishAsync` plus a throw; `ControllerBackendHealthState` is a two-value enum next to a nullable
capabilities record that repeats a static list. Recommendation: remove `NeutralizeAsync` (the router throws on a refused
neutral publish); `ControllerBackendHealth(bool Ready, string Detail, IReadOnlyList<ManagedControllerTarget> Targets)`.

### INPUT-018 (low) usbip PATH discovery is untestable process mutation inside the backend
`Input/ViiperControllerBackend.cs:72, 436-476`. NEW.
A static once-flag gates a registry read (UninstallEntries) and a process PATH write, executed inside `DiscoverAsync`
under the backend gate. Recommendation: pure `UsbipTool.Resolve(string path, IEnumerable<UninstallEntry>, string programFiles)`
with tests; the single PATH write stays at the call site.

### INPUT-019 (low) Backend disposal races pending waiters
`Input/ViiperControllerBackend.cs:283-327`. NEW.
`_gate.Dispose()` in finally while a `PublishAsync`/`RemoveTargetAsync` may be queued on it (or a second `DisposeAsync`
entered past the unsynchronised `_disposed` check) gives `ObjectDisposedException` instead of a clean false.
Recommendation: do not dispose the semaphore (it holds no unmanaged handle unless `AvailableWaitHandle` is touched); keep
the `_disposed` checks.

### INPUT-020 (low) Output router per-frame details
`Input/ControllerOutputRouter.cs:64, 186, 205-208, 262-313`. NEW.
`DroppedFrames++` runs both inside and outside `_gate` (lost increments; tests read it). `DispatchAsync` is a separate
`async Task` whose box allocates per frame whenever the plugin sink completes asynchronously. Recommendation:
`Interlocked.Increment`; inline the dispatch into `RunAsync` so the one loop state machine is reused.

### INPUT-021 (nit) Wire helpers duplicated across three report writers
`Input/SteamDeckNeptuneReport.cs:200-234`, `Xbox360Report.cs:66-79`, `DualShock4Report.cs:122-153`. NEW.
`Mask`, `Trigger`, `Axis`, `ScaledMotion` are re-declared with slightly different ranges. Optional: shared `WireScale`
helpers with the range as a parameter; byte layouts stay per format and keep their existing tests.

### INPUT-022 (low) Guide-chord mirror size cap can mirror a stale layout
`Core/SteamGuideChordMirror.cs:57, 305-313, 359-375`. NEW.
`MaximumLayoutBytes` (4 MiB) skips the newest autosave and the loop falls through to an older candidate, which is then
mirrored over the template. The cap is also redundant: `FitToSize` already refuses anything that cannot match Valve's byte
count. Violates the no-arbitrary-limits rule and the "newest layout wins" contract (586-587).
Recommendation: delete the cap; read the newest chord layout; if it does not fit, keep the existing "not mirrored" path and
never fall back to an older file. Add the regression test.

### INPUT-023 (nit) Hashing used for string equality
`Core/SteamGuideChordMirror.cs:365-372, 409, 623-626`. NEW. Compare the strings; use a `Log.Change` key for the overflow
de-duplication and drop `_overflowHash`.

### INPUT-024 (low) Shim ownership check reads whole DLLs repeatedly
`Core/SteamInputShim.cs:566-585`. NEW. Each `IsOurs` allocates the signature bytes and reads the entire file; a reconcile
calls it up to eight times; bare `catch`. Recommendation: static `u8` signature, catch only IO/UnauthorizedAccess.

### INPUT-025 (low, open question) Recorder cancel paths erase the existing binding
`Input/KeyRecorder.cs:81-89, 137-139`; `Input/GamepadChordRecorder.cs:35, 87-94`; `Settings/SettingsWindow.axaml.cs:568-573,
619-624`; `Settings/SettingsViewModel.QuickAccess.cs:124-142`. NEW.
A hook-install failure reports `Cleared()`, Escape reports `Cleared()`, and a chord recording with no input for 3 s reports
empty buttons; the view model treats each as "clear the binding". A failed hook installation silently empties the draft.
The hook-failure case is a defect; Escape and timeout may be intended. See section 6.

### INPUT-026 (low) GamepadNavigation couples Input to concrete Controls and wall time
`Input/GamepadNavigation.cs:7, 19-128, 271-295, 403`. NEW.
Input depends on `DeviceColorSpectrum`, `CurveEditor` and `OnScreenKeyboard.EditorKeyEventArgs`; the constructor takes 12
parameters (9 optional callbacks); eight suppression deadlines read `Environment.TickCount64`, so the cross-source
de-duplication cannot be tested without real time. Recommendation: an `IDirectionalControl { bool ApplyDirection(...) }`
implemented by the two controls; a `GamepadNavigationOptions` record; `TimeProvider` injected. Behaviour unchanged.

### INPUT-027 (nit) TextBox skip uses a 64-step guard
`Input/GamepadNavigation.cs:606-626`. NEW. A loop guard, not a content cap, so acceptable; stopping when the search returns
to the starting element removes the magic number.

### INPUT-028 (nit) Public types in the application assembly
`GamepadService`, `GamepadButtons`, `IUiButtonSource`, `GamepadNavigation`, `GamepadChordRecorder`, `GamepadChordWatcher`,
`KeyRecorder`, `TouchSwipeMonitor`, `ScreenEdge`, `SteamInputShim` and its enums, `SteamGuideChordMirror`, `SteamInputBlocker`
are `public`; their siblings are `internal`. NEW. Make them internal (tests already use internals).

### INPUT-029 (low) GamepadService is untestable in isolation and owns an unrelated enum
`Input/GamepadService.cs:14-90, 143-149, 208-314`. NEW.
Edge, repeat and stale-pad release logic (the AGENTS.md test contract) is coupled to `SdlGamepads.Update()` and a
`DispatcherTimer`, so none of it is unit-tested. Recommendation: move `GamepadButtons` to its own file; give `Poll` an
injected `Func<IReadOnlyList<PadSnapshot>>` and `TimeProvider`, keep the production defaults, and test edges, repeat,
diagonal re-arm and unplug-mid-chord.

### INPUT-030 (low) TouchSwipeMonitor: static registry, duplicated native state, one 1,081-line file
`Overlay/TouchSwipeMonitor.cs:65-184, 288-392, 442-581, 929-1056`. Covered by refactor-plan.md:115 (native window
ownership); details NEW.
Two OverlayControllers can exist (ShellSession.cs:864, SettingsWindow.axaml.cs:259), which is why the registration is shared,
but each monitor re-reads `hRawInput` and builds its own preparsed-data cache per device. `_disposed` is read by the window
procedure without a barrier, and `Dispose` frees preparsed data that the procedure may be using if disposal ever happens off
the window thread. Recommendation: `RawTouchInput` (registration, message window, device caps, one read per WM_INPUT, UI
thread asserted) hands out subscriptions; `EdgeSwipeRecognizer` (the existing `GestureTrace`, `PickTriggeredEdge`,
`StartBandPx`, `PhysicalSpanMm`, unchanged thresholds) moves to its own pure file; each subscriber keeps its own gesture
state and arming. Tests move with the pure code.

### INPUT-031 (low) Target-loss recovery is a four-hop event chain
`Input/ManagedControllerRouter.cs:284-299` -> `Shell/ControllerManager.cs:191-205` -> `Shell/DeviceCoordinator.cs:1683-1736`. NEW.
The manager sets Faulted, the coordinator's recovery releases (setting Idle) and then calls `ReportTargetFault` to set Faulted
again. Recommendation: `ReleaseAsync` takes the final state as a parameter and `ReportTargetFault` is deleted. The plugin
conversation stays in the coordinator.

### INPUT-032 (low) NativeHidHide split exists only for logging; Device Lab duplicates the control adapter
`Interop/NativeHidHide.Paths.cs:8-49`; `Shell/HidHideControl.cs:7-109`; DeviceLab `Wizard/HidHideAllowance.cs:255-290`.
Licensing part covered by A02-F022; duplication NEW.
`FromDosPath` logs through WSGM.Core, which is why it sits in a partial that Lab cannot link; Lab therefore re-implements
the read. Recommendation (after the A02-F022 decision): `FromDosPath` returns the converted path plus the Win32 error and the
caller logs; `HidHideControlState`, `IHidHideControl` and `NativeHidHideControl` move next to the declarations with a local
entry-kind enum so both projects share one adapter.

### INPUT-033 (nit) HidHide IOCTL threading is inconsistent
`Shell/HidHideOwnership.cs:150, 194, 231, 294, 316, 355, 374`. NEW. Reads and adds go through `Task.Run`; removals and the
cloak-off run inline. The IOCTLs are short; call them inline everywhere and drop the `Task.Run` wrappers.

### INPUT-034 (nit) HidHide IOCTL read stops at 1 MiB
`Interop/NativeHidHide.cs:15, 103-145`. NEW. It fails with ERROR_MORE_DATA rather than truncating, so it is an IO bound, not a
dropped-content cap. No change; mention it in the interop doc comment.

### INPUT-035 (nit) Native calls under managed locks
`Shell/ControllerManager.cs:1030-1035` (process priority write under `_stateGate`); `Core/SteamInputBlocker.cs:79-120`
(covered by INPUT-012). NEW. Move the priority write after the lock using the decided boolean.

### INPUT-036 (nit) Mixed lock types
`Shell/ControllerManager.cs:78` uses `object`, siblings use `Lock`. NEW.

### INPUT-037 (nit) KeyboardInput resolves the foreground layout per key record
`Interop/KeyboardInput.cs:49-53`. NEW. Resolve the layout once per chord.

### INPUT-038 (nit) Unkeyed repeat logs in the shim
`Core/SteamInputShim.cs:264, 374`. NEW. "Steam is not installed" is logged on every reconcile; removal lines omit the vector.

### INPUT-039 (medium, test quality) Tests that prove nothing, missing contract tests
NEW.
- `tests/WSGM.Tests/Input/InputTests.cs:134-141` asserts record-struct getters. Delete.
- `tests/WSGM.Tests/Input/ControllerDependencyAdapterTests.cs:10-20` copies the static `SupportedTargets` list. Replace with a
  check that every supported target has a writer whose length the submit path uses.
- `tests/WSGM.Tests/Input/ManagedControllerRouterTests.cs:9-27` tests the fake backend; `176-195` mostly tests the fake.
  Delete the first; keep the second only as a router ordering test.
- Output-router tests poll real time with `SpinWait` (ManagedControllerRouterTests.cs:142, 150, 169, 172, 194, 219) although
  the router takes a `TimeProvider`. Drive them with a fake time provider.
- Missing: the `Submit` drain path (every ControllerManager test calls `RouteAsync` directly); release under cancellation;
  dispose when the router or backend throws; synthetic-button release on cancellation (`PulseButtonsAsync`); corrupt
  HidHide ledger; GamepadChordWatcher press/hold/`HoldConsumed`; GamepadService edges/repeat (after INPUT-029);
  SteamInputBlocker claims (after INPUT-012).
- `tests/WSGM.Tests/Fakes/FakeButtonSource.cs` is used only by WSGM.UiTests through a link (WSGM.UiTests.csproj:24); the tests
  guide puts cross-project helpers in tests/Shared.

## 3. Plan refinements

Additions
1. Add INPUT-001, 002, 003 as a first, independent safety batch: every controller leave path ends with a HidHide show that
   runs whatever happened before it, and an unreadable ledger still turns the cloak off.
2. Add the sample-path fixes (INPUT-005, 006, 007, 020) as the concrete content of "no per-sample allocation".
3. Add INPUT-009 to the controller objective: thread `ManagedUiPad?` into SettingsWindowServices.
4. Add INPUT-022 under the no-arbitrary-limits rule.
5. Add the missing tests listed in INPUT-039 to the domain acceptance.

Changes
1. ControllerManager row (refactor-plan.md:67): remove "input-lease owner"; replace "frozen VIIPER behind unchanged
   adapter" with "libviiper ABI in NativeViiper.cs unchanged; ViiperControllerBackend is WSGM code".
2. B3 controller-safety row and the retained-target sentence (planning-corrections.md:35, 54): for the controller, the rule
   becomes "block forwarding, neutralize, ask the plugin to release, remove the target, then show HidHide, each attempted
   within the remaining deadline; the HidHide show is attempted on every path including timeout". Never keep the cloak past
   process exit.
3. D02 "20 s active-time fault recovery": rename to "20 s target-loss recovery budget" and move it to the device-coordinator
   objective that owns DeviceCoordinator.cs.
4. Inert-factory rule (refactor-plan.md:81): exempt idle in-memory worker loops (ControllerManager drain, output router loop).
5. Native-window rule (refactor-plan.md:115): exempt KeyRecorder's static hook slot; apply the subscription-owner shape to
   TouchSwipeMonitor only.

Mechanisms to avoid (simplify rule, no-arbitrary-limits rule)
- B3's quiescence evidence, per-phase percentage cutoffs and "retain the whole conflicting lifecycle" add nothing for the
  controller: `_routeGate` and the router's transition gate already serialize every conflicting operation. One `Deadline`
  argument on `ReleaseAsync` and `DisposeAsync` replaces them.
- Do not introduce a "shared SDL pump owner" type (C10); SdlGamepads already is one.
- Do not add a Start/Stop admission state machine to ControllerManager or ControllerOutputRouter (C4).
- Do not add a new UI pad registry or source abstraction for INPUT-009; pass the existing `ManagedUiPad`.
- Do not wrap HidHide in another retry or verification layer; the ledger-for-next-exit rule is already the HC-simple shape.
- Remove, rather than generalise, the router's Neutral/Active state (INPUT-006), `IControllerTargetBackend.NeutralizeAsync`
  and the health enum (INPUT-017), `ReportTargetFault` (INPUT-031), `_overflowHash` and the 4 MiB cap (INPUT-022/023), and
  the no-op one-shot lease calls (INPUT-012).
- Keep the VIIPER attach retry (6 attempts, documented wake refusal; the failed device is removed first). It is not an
  uncertain-write retry.

## 4. Target design

Owners after the refactor

| Owner | Responsibility | Construction |
| --- | --- | --- |
| `ControllerManager` (Shell) | Selection, target choice, UI capture, forwarding block, synthetic buttons, HidHide hide/show, ordered release, status snapshot | Built by composition root via `ControllerManager.CreateProduction(userDataRoot)`; injected into DeviceCoordinator. Tests keep the current constructor. |
| `ManagedControllerRouter` (Input) | Target create/replace/remove, neutral publication, live publication, output router lifetime | Created by ControllerManager; no longer disposes the backend |
| `ControllerOutputRouter` (Input) | Rumble return path: clamp, floor, pacing, pulse end, epoch | Unchanged ownership |
| `ViiperControllerBackend` (Input) | libviiper adapter, wire encoding, feedback decoding | Owned and disposed by ControllerManager |
| `UsbipTool` (Input, new static, small) | Pure usbip.exe folder resolution | Static pure function plus one call site |
| `HidHideOwnership` (Shell) | Ledger-backed hide/show, cloak ownership | `HidHideOwnership.ForUser(userDataRoot)` used by the manager factory and uninstall |
| `SteamInputShim` (Core) | Shim deployment state for one Steam install | Instance owned by ShellSession; one-shot modes create their own |
| `SteamInputBlocker` (Core) | Named lease claims over one native lease | Instance with lease port and the shim instance; owned by the session (overlay, settings) or by a standalone Settings runtime |
| `GuideChordMirrorBinding` (Shell, new, small) | "mirror follows an active Steam Deck target and the setting" | Session composition; subscribes to ControllerManager.StatusChanged and config reload |
| `RawTouchInput` (Overlay, new) and `EdgeSwipeRecognizer` (Overlay, pure) | Raw-input registration and per-subscriber recognition | RawTouchInput is process-scoped and reference-counted by subscriptions |
| `GamepadService`, `GamepadNavigation`, `ChordTracker`, recorders, `KeyRecorder`, `SdlGamepads`, `ManagedUiPad`, `UiPadAxes`, report writers | Unchanged roles | As today, with the injections of INPUT-026/029 |

Files split, merged or deleted

| File | Action |
| --- | --- |
| `Shell/ControllerManager.cs` | `UiCaptureState` (1048-1109) moves to `Input/UiCaptureState.cs`; enum/record `ControllerManagementState`/`ControllerManagerStatus` stay |
| `Input/GamepadService.cs` | `GamepadButtons` (14-90) moves to `Input/GamepadButtons.cs` |
| `Input/ViiperControllerBackend.cs` | `ExposeUsbipTool`/`UsbipInstallFolder` (436-476) move to `Input/UsbipTool.cs`; `DecodedHapticFeedback` stays |
| `Overlay/TouchSwipeMonitor.cs` | Split into `Overlay/RawTouchInput.cs`, `Overlay/EdgeSwipeRecognizer.cs`, `Overlay/TouchSwipeMonitor.cs` (subscriber, arming, dispatch) |
| `Interop/NativeHidHide.Paths.cs` | Merged into `NativeHidHide.cs` once `FromDosPath` stops logging (only after A02-F022) |
| `Shell/HidHideControl.cs` | Moves into the shared interop file with a local entry-kind enum (only after A02-F022) |

Old symbol to new owner for every dissolved file

| Old file | Symbol | New owner |
| --- | --- | --- |
| Overlay/TouchSwipeMonitor.cs | `WindowClassName`, `Gate`, `Instances`, `_instanceSnapshot`, `_sharedHwnd`, `CreateSharedWindowAndRegister`, `WndProc`, de-registration block in `Dispose`, `ProcessRawInput`, `ProcessRawInputBuffer`, `_inputBuffer`, `_devices`, `GetDeviceCaps`, `BuildDeviceCaps`, `EvictDevice`, `DeviceCaps`, `_usageBuffer`, `ProcessReport`, `DescribeForeground` | `RawTouchInput` (delivers decoded contact reports: tip, raw X/Y, contact count, caps ref) |
| Overlay/TouchSwipeMonitor.cs | `StartBandMm`, `StartBandFraction`, `MinStartBandPx`, `MaxStartBandPx`, `EntrySlopPx`, `EntryWindowMs`, `TriggerDistancePx`, `TriggerTimeMs`, `PhysicalSpanMm`, `StartBandPx`, `InwardDistance`, `SidewaysDistance`, `PickTriggeredEdge`, `GestureTrace`, `ScaleToScreen` | `EdgeSwipeRecognizer` (values unchanged) |
| Overlay/TouchSwipeMonitor.cs | `ScreenEdge`, `Triggered`, `Configure`, `Arm`, `Disarm`, `_armed`, edge flags, `OnContactDown`, `OnContactMove`, `_dispatchPending` UI post, diagnostics (`DiagnosticBandPx`, `DiagnosticLimitPerMinute`, `CompleteDiagnostic`, counters), `Dispose` (subscription release) | `TouchSwipeMonitor` (subscriber) |
| Input/ViiperControllerBackend.cs | `_usbipToolExposed`, `ExposeUsbipTool`, `UsbipInstallFolder` | `UsbipTool.Resolve` (pure) plus `UsbipTool.ExposeOnce` (the PATH write) |
| Interop/NativeHidHide.Paths.cs | `FromDosPath`, `BoundedString`, `QueryDosDeviceW` | `NativeHidHide` (logging moves to DeviceCoordinator/factory caller) |
| Shell/HidHideControl.cs | `HidHideControlState`, `IsNotInstalled`, `IHidHideControl`, `NativeHidHideControl`, `Failure` | Shared interop HidHide control file |

Public/internal API changes and consumers

| Change | Consumers to update |
| --- | --- |
| `IControllerTargetBackend.NeutralizeAsync` removed; `ControllerBackendHealth` reshaped; `ControllerBackendHealthState`/`ControllerBackendCapabilities` removed | ViiperControllerBackend, ManagedControllerRouter, ControllerManager.StartUnderGateAsync, DeterministicFakeControllerBackend, ControllerManagerTests (operations strings) |
| `ManagedTargetState` reduced; `ActivateSource` removed | ControllerManager.RouteAsync/ApplyTargetUnderGateAsync, ManagedControllerRouterTests |
| `ControllerManager.ReleaseAsync(HandoffScope, Func<CancellationToken,Task>, Deadline, bool keepPhysicalHidden, ControllerManagementState finalState)`; `DisposeAsync` keeps `IAsyncDisposable` but uses an internal deadline; `ReportTargetFault` removed; `ControllerManager.CreateProduction` added | DeviceCoordinator.cs:1236, 1412, 1619, 1710, 1726, 143-154; ControllerManagerTests |
| `HidHideOwnership.ForUser`; `IHidHideOwnershipStore.LoadAsync` returns a read result (Absent/Loaded/Unreadable) | DeviceCoordinator ctor, Program.RestoreHidHideForUninstallAsync, InMemoryHidHideOwnershipStore, HidHideOwnershipTests |
| `SteamInputShim` becomes an instance; `SetEnabled`/`Enabled` removed; `Reconcile(bool enabled, string reason)` | Program.cs:348-367, 437-438; Steam.cs:286, 319-323 (ColdStart needs the instance and the enabled flag); SteamInputBlocker; SteamInputManagement; ShellSession.cs:226, 773; ShellSession.Config.cs:55-64; SettingsViewModel.Steam.cs:113; SettingsViewModel.Save.cs:599; SteamInputShimTests (unchanged for `*In`) |
| `SteamInputBlocker` becomes an instance with `Hold`, `Drop`, `ReleaseAsync(Deadline)`, `RecoveryWarningRaised` | OverlayController.cs:49, 213, 282; OverlayController.Lease.cs:46, 56; SettingsWindow.axaml.cs:48; SettingsWindowServices.cs:26; Program.cs:221-224, 774-777 (97, 300, 493 deleted) |
| `SettingsWindowServices.Create(viewModel, ManagedUiPad?, SteamInputBlocker)` | SettingsWindow.axaml.cs:90-95, OverlayController.cs:843, DesktopTray.cs:56, App.axaml.cs:72, UiFixture.cs:139 |
| `GamepadNavigation(GamepadNavigationOptions)`, `IDirectionalControl` | OverlayController.cs:606, BootSplash.cs:103, SettingsWindow.axaml.cs:298, 441, 528; CurveEditor; DeviceColorSpectrum; WSGM.UiTests ControllerNavigationTests |
| Internal visibility for the INPUT-028 types | Any `public` signature in Settings/Overlay exposing them (check with the build) |

## 5. Implementation batches

Every batch builds green on its own and runs only its filter; the domain-final filter is D02's.

### INPUT-B1 Never strand the physical controller (~260 lines)
Files: Shell/ControllerManager.cs, Shell/HidHideOwnership.cs, tests/WSGM.Tests/Shell/ControllerManagerTests.cs,
tests/WSGM.Tests/Shell/HidHideOwnershipTests.cs.
Steps: (1) `ReleaseAsync` takes a `Deadline`, every step attempted, show plus `SetState` in finally; callers in DeviceCoordinator
pass their existing deadline. (2) `DisposeAsync`: router and backend disposal in try, show in finally; the transition wait
observes an internal 5 s deadline and the show still runs after it. (3) Store `LoadAsync` distinguishes Absent from
Unreadable; `ShowUnderGateAsync` turns the cloak off and removes WSGM's applications on Unreadable, keeps the file, reports
failure.
Dependencies: none (DeviceCoordinator call sites change signature only).
Tests: cancelled release still shows and sets state; dispose with a throwing backend still shows; corrupt ledger turns the
cloak off and leaves the file byte-identical; refused show still keeps the ledger.
Filter: `FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~HidHideOwnershipTests`.

### INPUT-B2 One sample path without per-sample allocation (~550 lines)
Files: Shell/ControllerManager.cs, Input/ManagedControllerRouter.cs, Input/ControllerOutputRouter.cs,
Input/IControllerTargetBackend.cs, Input/ViiperControllerBackend.cs, tests (router, manager, dependency adapter).
Steps: bounded(1) channel drain (INPUT-005); router state reduction and single neutral publication (006, 007); remove
`NeutralizeAsync` and the health enum (017); router stops disposing the backend, manager disposes it (016); raise
`TargetLost` outside gates, record loss under the router lock (015); do not dispose the backend semaphore (019);
`Interlocked` counter and inline dispatch (020).
Dependencies: B1.
Tests: `Submit` drain routes the newest sample and drops intermediate ones; submit after dispose is ignored without throwing;
invalid stream publishes one neutral report and one stop frame; capture release does not drop queued rumble; output tests on
a fake time provider; delete the fake-only tests (INPUT-039).
Filter: `FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerDependencyAdapterTests`.

### INPUT-B3 Composition and managed pad reach (~380 lines)
Files: Shell/ControllerManager.cs, Shell/HidHideOwnership.cs, Shell/DeviceCoordinator.cs (ctor only), Program.cs (uninstall
helper), Input/ViiperControllerBackend.cs, new Input/UsbipTool.cs, Settings/SettingsWindowServices.cs,
Settings/SettingsWindow.axaml.cs, Overlay/OverlayController.cs, Shell/DesktopTray.cs, App.axaml.cs, Shell/ControllerManager.cs
(`ReportTargetFault` removal, final-state parameter), tests.
Steps: `ControllerManager.CreateProduction`, `HidHideOwnership.ForUser` (INPUT-008); pure usbip resolver (018);
`ManagedUiPad?` into SettingsWindowServices (009); final-state release parameter (031); immutable status snapshot (014);
priority write outside the lock (035); `Lock` type (036); move `UiCaptureState` to its own file.
Dependencies: device-coordinator domain must accept DeviceCoordinator receiving a built ControllerManager (or take this ctor
edit inside its own batch); config domain's `UserDataContext` if already landed, otherwise the root string.
Tests: UsbipTool resolution cases (on PATH, uninstall entry, fallback, none); status snapshot consistency; one UI test that
overlay-launched Settings navigates from the managed pad (WSGM.UiTests `FullyQualifiedName~ControllerNavigationTests`).
Filter: `FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~HidHideOwnershipTests|FullyQualifiedName~UsbipTool`.

### INPUT-B4 Steam Input shim and lease as owned instances (~900 lines)
Files: Core/SteamInputShim.cs, Core/SteamInputBlocker.cs, Core/SteamInputManagement.cs, Core/Steam.cs (ColdStart
signature), Program.cs, Shell/ShellSession.cs, Shell/ShellSession.Config.cs, Shell/ShellSession.Shutdown.cs,
Overlay/OverlayController*.cs, Settings/SettingsWindow*.cs, Settings/SettingsViewModel.Save.cs,
Settings/SettingsViewModel.Steam.cs, tests.
Steps: shim instance state, explicit `enabled` (INPUT-010); single apply path through tracked background work (011); lease
owner instance with a lease port and `ReleaseAsync(Deadline)`, delete the no-op one-shot calls (012); shim signature/IO
cleanups (024, 038).
Dependencies: shell-session domain (where the session owns and shuts these down within the B3 budget), Steam/CEF domain
(`Steam.ColdStart` receives the shim), settings domain (SettingsWindowServices). Must land after B3 because both edit
SettingsWindowServices.
Tests: new `SteamInputBlockerTests` (claims balance with failing acquire, overlapping surfaces keep the lease, drop during
acquire, release waits within the deadline); existing `SteamInputShimTests` unchanged.
Filter: `FullyQualifiedName~SteamInputShimTests|FullyQualifiedName~SteamInputBlockerTests|FullyQualifiedName~SettingsViewModel`.

### INPUT-B5 Guide-chord mirror binding and cap removal (~220 lines)
Files: Core/SteamGuideChordMirror.cs, new Shell/GuideChordMirrorBinding.cs, Shell/ShellSession.cs, Shell/ShellSession.Config.cs,
tests/WSGM.Tests/Core/SteamGuideChordMirrorTests.cs.
Steps: delete `MaximumLayoutBytes` and the fall-through, never mirror an older file over a newer one (022); string equality
and `Log.Change` (023); binding owner for the status and config triggers (013).
Dependencies: shell-session domain composition (where the binding is constructed and disposed).
Tests: the newest layout that does not fit leaves the template untouched even when an older layout would fit; binding applies
on status and config, unsubscribes on dispose.
Filter: `FullyQualifiedName~SteamGuideChordMirror|FullyQualifiedName~GuideChordMirrorBinding`.

### INPUT-B6 Touch edge input owner (~700 lines)
Files: Overlay/TouchSwipeMonitor.cs, new Overlay/RawTouchInput.cs, new Overlay/EdgeSwipeRecognizer.cs,
Overlay/OverlayController.Gestures.cs, tests/WSGM.Tests/Overlay/TouchSwipeMonitorTests.cs.
Steps: per INPUT-030 and the symbol table above; thresholds and log texts unchanged.
Dependencies: none, unless the core domain's single MessageWindow owner lands first, in which case RawTouchInput registers
through it.
Tests: existing recognizer tests retargeted unchanged; subscription count keeps the registration until the last subscriber;
a disposed subscriber receives nothing.
Filter: `FullyQualifiedName~TouchSwipe|FullyQualifiedName~EdgeSwipe`.

### INPUT-B7 Navigation, polling and recorders (~650 lines)
Files: Input/GamepadNavigation.cs, Input/GamepadService.cs, new Input/GamepadButtons.cs, Input/GamepadChordRecorder.cs,
Input/GamepadChordWatcher.cs, Input/KeyRecorder.cs, Controls/CurveEditor*, Controls/DeviceColorSpectrum*, the five
GamepadNavigation call sites, Interop/KeyboardInput.cs, tests.
Steps: `IDirectionalControl`, options record, `TimeProvider` (026); TextBox cycle detection (027); injected pad source and
clock in GamepadService (029); internal visibility (028); hook failure no longer clears the draft (025, plus the maintainer's
answer on Escape/timeout); layout once per chord (037).
Dependencies: B3 (SettingsWindowServices), UI domain for the Controls edits.
Tests: GamepadService edges, repeat, diagonal re-arm, unplug-mid-chord; GamepadChordWatcher press and hold; navigation
cross-source suppression with a fake clock; WSGM.UiTests ControllerNavigationTests unchanged.
Filter: `FullyQualifiedName~WSGM.Tests.Input` then `FullyQualifiedName~ControllerNavigationTests` in WSGM.UiTests.

### INPUT-B8 HidHide adapter sharing and test hygiene (~250 lines)
Files: Interop/NativeHidHide*.cs, Shell/HidHideControl.cs, Shell/HidHideOwnership.cs, DeviceLab Wizard/HidHideAllowance.cs,
WSGM.DeviceLab.csproj, tests/WSGM.Tests/Fakes/FakeButtonSource.cs -> tests/Shared, WSGM.UiTests.csproj, InputTests.cs.
Steps: INPUT-032 (only after the A02-F022 licensing decision), 033, 034 comment; move FakeButtonSource; delete the getter test.
Dependencies: maintainer decision on A02-F022; DeviceLab domain for the Lab edit.
Filter: `FullyQualifiedName~HidHide|FullyQualifiedName~InputTests` plus the DeviceLab test project's HidHide filter.

## 6. Risks and open questions

Risks
- Release-order changes touch suspend, resume and target-loss paths, which only hardware shows. The Claw 8 and Xbox Ally X
  manual rows for sleep/wake with management on, toggling management off, and exiting during a game must be rerun after
  B1 to B3.
- Turning the cloak off on an unreadable ledger also turns off a cloak another tool may rely on. That is the recorded rule
  (WSGM owns the cloak) and is what happens today on a readable ledger.

Open questions for the maintainer
1. Recorder cancellation (INPUT-025): should Escape during shortcut capture and a 3 s chord-recording timeout keep the
   existing binding (only an explicit Clear clears it)? The hook-failure case will keep the binding regardless.
2. INPUT-009 is inferred from code: does controller navigation work today in Settings opened from the overlay while a Steam
   Deck target is active on the Claw? The proposed fix is the same either way; the answer decides whether it is a defect fix
   or a consistency change.
3. A02-F022 (MIT Device Lab linking GPL `NativeHidHide.cs`) must be decided before INPUT-B8 moves more HidHide code into the
   shared file.
