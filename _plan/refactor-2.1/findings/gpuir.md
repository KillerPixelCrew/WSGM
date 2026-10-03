# GPU plugins, shared GPU runtime and IR plugin findings

Scope: `src/WSGM.Plugin.IntelGpu`, `src/WSGM.Plugin.NvidiaGpu`, `src/WSGM.Plugin.AmdGpu`, the linked runtime in
`src/Shared/Gpu`, `src/WSGM.Plugin.Ir` including `Firmware/`, their four test projects, and two Shell call sites the GPU
path depends on (`PluginHost.Publish`, `GpuCoordinator._syncRevision`). Baseline is master `1329813f`. This file merges
the domain review, its adversarial verification, the completeness critic and plan v2; where they disagree, plan v2 and
the verifier win. Cited line numbers come from the review and are often a few lines off: anchor every edit by symbol.

The area has 48 ids (GPUIR-001 to GPUIR-040, GPUIR-V-001 to GPUIR-V-007, and GPUIR-C-001 added by the solution
check). 39 are written up below: 2 high, 13 medium, 21 low and 3 nit. The other 9 are refuted or no-change and listed at
the end. The maintainer's decisions of 2026-10-03 (`../DECISIONS.md`) are applied throughout: D9 removes the NVIDIA
journal's unconfirmed entries (GPUIR-012), D11 brings IR catalog paging and protocol 2 into B156 (GPUIR-031), and the
security-only GPUIR-034 moved to the no-change list.

Plan v2 batches for this area, in execution order per chain:

| Review batch | Plan v2 | Content |
| --- | --- | --- |
| A02_04 | B003 | IR UTF-8 reply bound and reply ownership (A02-F015, A02-F016), prerequisite of B154 |
| SDK-B9 part | B148 | Instance `_syncRevision` (GPUIR-040) |
| GPUIR-B1 | B150 | Journals and state files |
| GPUIR-B2 | B151 | Shared runtime contract and behaviour |
| GPUIR-B3 | B152 | NVIDIA and AMD cleanups |
| GPUIR-B4 | B153 | Intel onto `DriverRuntime` |
| GPUIR-B5 | none | Skipped (critic conflict 11, WDC rules Intel's CCD query an accepted duplication) |
| GPUIR-B6 | B154 | IR host correctness |
| GPUIR-B7 | B155 | IR split by responsibility (no finding of its own, see the last section) |
| GPUIR-B8 | B156 | Firmware fixes plus IR protocol 2 with catalog paging on firmware and host (D11 decided) |
| GPUIR-B9 | B177 | Documentation |

The GPU chain is B150, B151, B152, B153. The IR chain is B003, B154, B155, B156. The chains share no code and are
ordered only by the one-writer rule.

Rules that apply to every GPU and IR solution below:

- Use the SDK's existing `Deadline` (`src/WSGM.Device.Sdk/Lifecycle/Deadline.cs`, process `ActiveClock`) and `PluginTrace`.
  No owned clock or trace sink is introduced (critic conflict 18). Nothing waits on an "F02 clock".
- No new shared assembly. `src/Shared/Gpu/*.cs` stays linked source; B150 adds the link to Intel.
- State-file semantics everywhere in this area (critic conflict 16): a missing file or directory is Absent; a parse or
  validation failure is Corrupt; any other IO error is Unreadable. Corrupt and Unreadable files are never rewritten. No
  shared new type is required for this; each assembly applies the rule with what it has.
- NVIDIA and AMD visible values, labels and health texts stay as they are; Intel keeps its visible behaviour after B153
  (overlay, health texts, written-value display).
- Readback never gates a write, a control, a support decision or reported success. An uncertain write is never retried.
- D9: no readback machinery. A write either dispatched (published as written) or failed to dispatch; no journal entry,
  flag or refusal waits for the driver to report a value before the next write may run.
- Security hardening against same-user threats is dropped (DECISIONS.md); only functional fixes stay.

---

### GPUIR-001: Intel driver calls run on the overlay's UI thread

- **Severity:** high
- **Where:** `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs:75-156` (`ExecuteCommandAsync`), `:159-193`
  (`SyncApplicationProfilesAsync`); call chain `src/WSGM/Overlay/OverlayWindow.Graphics.cs:291-308` ->
  `src/WSGM/Shell/GraphicsOverlaySource.cs:181` -> `src/WSGM/Shell/GpuCoordinator.cs:316` ->
  `DeviceCapabilityRouter.ExecuteAsync` -> `src/WSGM/Shell/PluginCapabilityChannel.cs:222`.
- **Problem:** After `await _lane.WaitAsync(...)`, which completes synchronously when the lane is free, Intel calls
  `GuardWrite` and `GuardRead` inline. Nothing in the overlay chain hops off the Avalonia dispatcher before the plugin,
  and `ConfigureAwait(false)` does nothing before the first real suspension. So `ctlSet*` writes and the readback for Arc
  Sync, the colour LUT, LACE and every other Intel control run on the UI thread and freeze the overlay for the length of
  the driver call. `DriverRuntime` avoids this on purpose (`Task.Run` around every native call, comment "All native work
  stays off it").
- **Best solution:** No interim patch to `IntelGpuPlugin` (greenfield rule: B153 deletes this code). B153 moves Intel
  onto `DriverRuntime` as described in GPUIR-007, so every Intel write, readback, probe and sync runs inside the
  runtime's `Task.Run`. After B153, `IntelGpuPlugin.ExecuteCommandAsync` is a one-line forward to
  `DriverRuntime.ExecuteCommandAsync`, and the dispatcher thread only waits on the lane and awaits the hop.
- **Tests:** In the Intel test project, a runtime test with a fake `IDriverSession` whose control records
  `Environment.CurrentManagedThreadId` in `Write` and `Read`; invoke `ExecuteCommandAsync` from a single-threaded
  `SynchronizationContext` and assert the native calls ran on a different thread. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`. Manual M01-20 on the Optimus
  notebook and the Claw (attended, maintainer).
- **Plan v2:** B153.
- **Related:** GPUIR-007 (the move), GPUIR-005, plan claim C1/C2.

### GPUIR-002: An unreadable Intel per-app record is replaced, orphaning driver overrides for good

- **Severity:** high
- **Where:** `src/WSGM.Plugin.IntelGpu/Profiles/ApplicationProfileSynchronizer.cs:145-148` (constructor load), `:527-531`
  (`Save`); `src/WSGM.Plugin.IntelGpu/StateFile.cs:15-32` (`TryLoad`), `:41-59` (`Save`).
- **Problem:** `StateFile.TryLoad` returns null for `IOException`, `UnauthorizedAccessException` and `JsonException`. The
  synchronizer then starts from `SyncRecord.Empty`, and the first `Save` overwrites `application-profiles.v1.json`. Every
  `<exe>_<Setting>` value WSGM created under the adapter's `3DKeys` loses its owner record. IGCL has no delete, so those
  overrides can then only be removed in Intel's own software. `StateFile.Save` also writes `path + ".tmp"` without a flush
  and moves it, so a power loss during a save can produce exactly the corrupt file that triggers the reset. A transient
  sharing violation at start is enough on its own.
- **Best solution:** B150 links `../Shared/Gpu/*.cs` into `WSGM.Plugin.IntelGpu.csproj` (same `<Compile Include ...
  Link=...>` item NVIDIA and AMD use; `DriverRuntime` compiles unused until B153) and deletes `StateFile.cs`.
  - The constructor reads with `DriverStateFile.Read(_path, SyncRecord.Empty)` (new semantics in GPUIR-003) inside a
    `try`. On `DriverFailure` it stores the message in a readonly `string? _unreadable`, sets `Record = SyncRecord.Empty`
    and logs one warning. A null `_path` (no state directory) behaves as today.
  - `Apply` checks `_unreadable` first. When set, it returns one `ApplicationProfileFailure(profileId, executable, "",
    "The Intel per-application record could not be read (<reason>); nothing was changed.")` per requested executable,
    performs no registry write, no removal and no save.
  - `Save` calls `DriverStateFile.Write` (unique temp name, `WriteThrough`, `Flush(true)`, atomic move). Its exceptions
    are handled as GPUIR-V-006 describes.
  - `ColorStore` (`Display/ColorPipeline.cs:28-51`) gets the same rule: on an unreadable `color.v1.json` it keeps an
    empty in-memory map (colour reads as unknown, as today for unrecognised LUTs), `Set` still updates memory so the
    colour write applies, and the file write is skipped with one logged line. The record is never overwritten.
    `ColorStore.Set` also catches `IOException`/`UnauthorizedAccessException` from `DriverStateFile.Write` and logs
    it: `ColorPipeline.Write` calls `Set` after the native write succeeded, and today's `StateFile.Save` swallowed the
    error, so letting the new throwing helper escape would turn an applied colour write into Indeterminate through
    `GuardWrite`. The colour record only helps recognise WSGM's own LUT later; a failed save costs nothing else.
  - A null state directory keeps today's behaviour: no read, no write (both helpers get a null-path guard at the
    caller, `DriverStateFile` itself takes a non-null path).

  This is fail-closed for the per-app record only. The global Intel controls keep working, which is the same rule
  GPUIR-V-002 applies to NVIDIA.
- **Tests:** New `tests/WSGM.Plugin.IntelGpu.Tests/StateFileTests.cs` is not needed once `StateFile` is gone; put the
  cases in `ApplicationProfileSynchronizerTests`: a record file held open with `FileShare.None` and a directory at the
  record path both leave the bytes unchanged and make `Apply` return one failure per executable with no registry write
  (fake `IRegistryNode`); a corrupt JSON record behaves the same; an absent record seeds empty and saves. Two
  `ColorStore` cases: unreadable colour record, `Set` does not change the file; a directory at the colour record path
  after load, `Set` returns normally and `Get` returns the new value. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter "FullyQualifiedName~ApplicationProfileSynchronizerTests|FullyQualifiedName~ColorStore"`.
- **Plan v2:** B150.
- **Related:** GPUIR-003, GPUIR-021, GPUIR-V-002, GPUIR-V-006, A02-F007 and A02-F017 (siblings), critic conflict 16.

### GPUIR-004: IR firmware sends a pending network learn reply to whichever client replaced the requester

- **Severity:** medium (verifier lowered from high)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp:450` (`learningOut = &out`), `:584-585` (new client replaces the
  old one in `serveNetwork`), also `:512` (`client.stop()` in the Wi-Fi reset path) and `:594` (idle timeout
  `client.stop()`); `cancelLearn` at `:310-321`.
- **Problem:** `learningOut` points at the global `client` object. When a new TCP connection arrives, or the old one is
  stopped for idleness or a Wi-Fi reset, the learn stays pending and its later `learned`, `timing-limit`,
  `capture-overflow` or `timeout` reply is written to whatever connection now occupies `client`, even one that never
  presented the token. What leaks is one captured IR timing frame tagged with an id the new host does not match
  (`IrEndpoint.cs:461-465` ignores it); the firmware's own trust model already lets any LAN client displace the paired
  host, which is why the verifier lowered it.
- **Best solution:** Add one helper and use it at every place `client` is stopped or replaced:

  ```cpp
  void stopClient()
  {
      if (learningOut == &client) cancelLearn("cancelled");
      if (client) client.stop();
  }
  ```

  In `serveNetwork` call `stopClient()` before `client = incoming;` (replacing `if (client) client.stop();`), and use it
  at `:512` and `:594`. The old client gets its terminal `cancelled` reply (or nothing if it is gone), the new one
  inherits nothing, and the receiver resumes through `cancelLearn`. No connection generation or token check is added;
  this removes the dangling target instead of guarding it. It ships in firmware 0.5.0 with protocol 2 (GPUIR-031, D11).
  The fix is functional (the old requester never gets its terminal reply); no trust check is added, since same-user and
  LAN trust hardening is dropped (DECISIONS.md).
- **Tests:** Build only:
  `.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware`. No upload. Manual M01-37/38 on
  the desktop after the maintainer flashes.
- **Plan v2:** B156; D11 decided: catalog paging and the protocol 2 bump ship in the same batch.
- **Related:** A02S01-F001 (same defect), plan claim C17/C23, critic section 5 (A02S01-F001 high).

### GPUIR-006: GPU write admission is ambient thread-static state

- **Severity:** medium
- **Where:** `src/Shared/Gpu/DriverWriteScope.cs:6-27`; checked at `src/WSGM.Plugin.NvidiaGpu/NvApi.cs:84,116,128,431`,
  `NvApiPrivate.cs:16,40`, `src/WSGM.Plugin.AmdGpu/AdlxNative.cs:138,146,154`, `AdlDitherApi.cs:69`; set up in
  `DriverRuntime.ExecuteCommandAsync` (`:117-128`), `SyncApplicationProfilesAsync` (`:201-202`) and `CheckSupport`
  (`:489-505`).
- **Problem:** The pre-setter recheck (cancelled, expired, no longer running) lives in a `[ThreadStatic] Action`. Any
  native setter called outside `DriverWriteScope.Run`, or from a continuation on another thread, throws "No active GPU
  write admission"; any setter called on a thread that still has a stale scope checks the wrong command. Nothing in a
  method signature shows that a setter needs admission.
- **Best solution:** Replace `DriverWriteScope` with an explicit value passed down. Rename the file to
  `src/Shared/Gpu/WriteAdmission.cs`:

  ```csharp
  internal readonly struct WriteAdmission(CancellationToken token, Deadline deadline, Func<bool> admitted)
  {
      internal bool Admitted => !token.IsCancellationRequested && !deadline.HasExpired && admitted();
      internal void Check()
      {
          if (!Admitted) throw new DriverFailure("The GPU write is no longer admitted (cancelled, expired or stopped).");
      }
  }
  ```

  - `DriverControl.Write(CapabilityValue value, WriteAdmission admission)`, `DriverControl.ProbeSupport(CapabilityValue
    current, WriteAdmission admission)` and `IDriverSession.Sync(ApplicationProfileSync sync, WriteAdmission admission,
    CancellationToken token)` take it.
  - Every native setter takes it as a parameter and calls `admission.Check()` immediately before the native call. The
    full list (today's ten production `DriverWriteScope.Check()` sites, plus the one in `DriverRuntimeTests`): `NvApi.Save`, `NvApi.Set`, `NvApi.Inherit`,
    `NvApi.Color` (the check stays inside `command == 2`), `NvApi.OutputMode` (only when `requested` is set),
    `NvApi.SetDither`, `AdlxNative.SetInteger`, `AdlxNative.SetBoolean`, `AdlxNative.Call`, `AdlDitherApi.Write`.
    `INvProfiles.Save/Set/Inherit` change with them, so `NvProfiles` threads the sync admission into `Apply` and the
    restore path, and the `INvProfiles` fake in `NvProfilesTests` gains the parameter. AMD's `AdlxControl` write
    delegate becomes `Action<CapabilityValue, WriteAdmission>`.
  - The runtime builds one per operation: command `new(cancellationToken, command.Deadline, () => _running && _session
    is not null)`; probe `new(token, deadline, () => _running && _session is not null)` where `deadline` is the deadline
    of the call running the pass (GPUIR-C-001: `context.Deadline` in `StartAsync`, `Deadline.Never` in the loop, never
    `_context.Deadline`); sync `new(cancellationToken, Deadline.Never, () => _running && _session is not null)` (sync
    today checks only the token).
  - `Check` throws `DriverFailure` with `attempted: false`, which the command path maps to Rejected exactly as today's
    check lambda does. The hop's own `cancellationToken.ThrowIfCancellationRequested()` and `HasExpired` lines before
    `Write` become redundant and are deleted.
  - `NvApi.Application(create: true)` stages DRS changes before `Save` without a check. That is correct because only
    `Save` commits; add a one-line comment saying so, no guard.

  This removes the thread-static and the "no active admission" failure mode; it adds no state. No live defect rests on
  the thread-static today (every setter runs synchronously inside `DriverWriteScope.Run`, which restores the previous
  scope in `finally`); the change is kept because plan v2 makes the admission explicit and GPUIR-005 and GPUIR-C-001
  need it as a value.
- **Tests:** In `DriverRuntimeTests`: a fake control whose setter calls `admission.Check()` and records the call; a
  command whose token is cancelled (or whose deadline expires) between admission and the hop returns Rejected and the
  setter never runs; a probe after `StopAsync` began never reaches the setter. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`
  and `dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj`.
- **Plan v2:** B151.
- **Related:** GPUIR-005 (Intel half, B153), GPUIR-V-001 (admission refusals are not probe outcomes), plan claim C5,
  critic conflict 18.

### GPUIR-007: Two GPU lifecycles with different behaviour for the same SDK contract

- **Severity:** medium
- **Where:** `src/Shared/Gpu/DriverRuntime.cs` (NVIDIA, AMD) versus `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs:27-902`
  (Intel's own 810-line lifecycle).
- **Problem:** The two lifecycles differ in command reason codes (GPUIR-008), health publication (GPUIR-019), publish
  failure containment (GPUIR-V-005), stop budgets (GPUIR-015), discovery cadence and UI-thread safety (GPUIR-001), and
  each fix has to be made twice. Intel's copy also carries its own lane, close-pending flag, loop and four support
  caches.
- **Best solution:** B153 makes `DriverRuntime` the only GPU lifecycle and turns Intel into an `IDriverSession` plus a
  control adapter, keeping Intel's visible behaviour. Concretely:
  - `IntelGpuPlugin` becomes a forwarder with the same shape as `NvidiaGpuPlugin` (`Id => "wsgm.gpu.intel"`) over
    `new DriverRuntime("wsgm.gpu.intel", Open, Describe)`. It keeps two Intel-only pieces of plugin-lifetime state in
    the forwarder: the existing reopen backoff (`_reopenAt`, `_reopenDelay`, `MaxReopenDelay`, see GPUIR-025 no-change)
    and the written-value map (`Dictionary<string, WrittenValue>`). Both are reset in the forwarder's `StartAsync` and
    `ResumeAsync` before delegating, which is exactly when Intel resets them today. While the
    backoff has not expired, `Open` rethrows the last open failure (the forwarder keeps that `DriverFailure` beside the
    backoff fields), so a refused `ctlInit` is not a `ControlLib.dll` load and free every 10 s and the published health
    text stays the one the failed open produced (today's loop skips the pass and leaves health unchanged; GPUIR-019's
    dedupe makes the rethrow publish nothing new). After a successful open it resets the backoff and clears the kept
    failure, after a failed one it doubles the delay.
  - New `src/WSGM.Plugin.IntelGpu/IgclDriverSession.cs` implements `IDriverSession`. Its constructor loads `IgclApi`
    as today (GPUIR-034 is no-change), opens `IgclSession`, enumerates `AdapterClassKey`, loads `ColorStore`, creates
    `IntelGraphicsMemoryTransport` and `ApplicationProfileSynchronizer`. Missing library and no adapter throw
    `DriverFailure` with today's texts and `Unavailable`; any other open exception is wrapped as
    `DriverFailure("Opening the Intel driver failed.", health: PluginHealth.Failed)`. `Dispose` closes the session and
    frees the library. It logs through the `DriverLog` the runtime hands to `open` (B151 replaces the
    `Action<string, string>` report parameter of `open` with `DriverLog`, which is today's `IntelLog` moved to
    `src/Shared/Gpu`; NVIDIA and AMD call its `Change` where they call `report` today, so their trace lines keep their
    keys and texts).
  - `IgclDriverSession.Discover()` builds `IntelModel` on the first call and returns the cached model afterwards unless
    `IgclSession.RefreshOutputs()` reports a change (today's Intel rule; NVIDIA and AMD keep their per-pass
    rediscovery). It applies `RecordUnsupported` (explicit unsupported-feature read results hide the control, as today)
    and wraps each `IntelControl` in an `IgclDriverControl`. A build exception becomes `DriverFailure("Reading the
    driver's capabilities failed.", health: PluginHealth.Failed)`. `BeginPass()` calls `IgclSession.BeginPass()`.
    `Sync` calls `IgclSession.BeginPass()` and `synchronizer.Apply(sync, model.FindTarget, token)`.
  - `IgclDriverControl : DriverControl` maps `ControlRead` to `DriverRead(Value, Available, Reason)`, `ControlWrite`
    `Refused` to `DriverFailure(attempted: false)`, `Uncertain` to `DriverFailure(attempted: true)`, and a lost session to
    `DriverFailure(lost: true)`. `SupportKey` is the shared structure's identity (IGCL source, 3D feature, colour curve or
    matrix of one display). It keeps Intel's written-value overlay: `Write` records `(value, pending)` in the forwarder's
    map; the first `Read` after the write (the runtime's readback) records the raw readback and returns it unchanged, so
    verification stays honest; later reads return the written value while the driver keeps reporting null or that same
    readback, and drop the entry once it reports something else. It keeps `IntelControl.TraceDue` around its read
    tracing, so the runtime pass formats no per-value strings.
  - Runtime additions needed for Intel and nothing else: `DriverRead` (GPUIR-008 section of B151 already adds it),
    `DriverFailure` gains an optional `PluginHealth health = PluginHealth.Unavailable` that `ObserveAsync` publishes
    for a `DriverFailure` (other exceptions stay Unavailable, so NVIDIA and AMD are unchanged), and the constructor takes
    an optional `Func<int, (PluginHealth Health, string Detail)> describe` for the health after a successful pass.
    NVIDIA and AMD pass nothing and keep "Driver controls available." / "The driver reported no supported GPU
    controls."; Intel passes `count => (PluginHealth.Ready, $"{count} controls published.")`. These texts are visible in
    the overlay and Steam UI (`CommonPluginOverlaySource.cs:82`, `CommonPluginSteamUiSource.cs:152`), so they must not
    change.
  - After a write the runtime publishes `HardwareStateQuality.Verified` when the readback equals the requested value and
    `Observed` otherwise, for every vendor. The overlay renders both the same (`DeviceOverlayBridge.cs:1170`), so this
    keeps Intel's Verified quality without a visible change for NVIDIA or AMD.
  - Deleted from Intel: `ObservationInterval`, `RefreshInterval`, `StopBudget`, `_lane`, `ReleaseLane`, `_closePending`,
    `CloseDriver`, `_published`/`PublishedState`/`PublishAsync`, `_supportResults` (GPUIR-024), `_health`/`Health`
    (GPUIR-019), the loop (`_loop`, `_loopTask`, `StartLoop`, `StopLoopAsync`, `RunLoopAsync`, GPUIR-026),
    `OpenCycleGuardedAsync`, `OpenCycleAsync`, `BuildAsync`, `PublishDescriptorsAsync`, `RetractAsync`, `PassAsync`,
    `ObserveAsync`, `GuardRead`/`GuardWrite`/`Guard` (moved into the adapter), `IntelLog.cs` (becomes
    `src/Shared/Gpu/DriverLog.cs` in B151), `StateFile.cs` (B150).

  Not done: no written-value overlay for NVIDIA or AMD (the verifier rejected it: it changes values those vendors show
  and fixes no cited defect), and no new runtime assembly.
- **Tests:** Adapter mapping tests (`Refused` -> Rejected, `Uncertain` -> Indeterminate, unavailable read -> published
  `Available = false` with the reason, lost -> empty descriptor set and Unavailable); written-value overlay through the
  runtime (readback after write is the raw value, later reads show the written value, a different driver value clears
  it); backoff: a fake open that fails twice is called at 10 s then 20 s, not every pass; health texts for open failure
  (Failed, "Opening the Intel driver failed.") and success ("N controls published."); a persisted-id fixture pinning
  adapter `pci-8086-...`, display `[internal-]edid-...`/`display-...` and section ids `graphics[-n]`/`display-<fnv>`.
  Filter: `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`. Manual M01-20 on the
  notebook and the Claw.
- **Plan v2:** B153 (depends on B150, B151, B152).
- **Related:** GPUIR-001, GPUIR-005, GPUIR-010, GPUIR-011, GPUIR-024, GPUIR-025, GPUIR-026, GPUIR-034, GPUIR-036,
  GPUIR-V-001 (support rule Intel must not lose), verifier batch problem 6 (a) to (e), plan claims C1 to C3.

### GPUIR-010: Intel colour support probe fails on an untouched display and hides colour for the plugin lifetime

- **Severity:** medium (PLAUSIBLE: whether the driver answers DATA_NOT_FOUND here is unverified)
- **Where:** `src/WSGM.Plugin.IntelGpu/Display/ColorPipeline.cs:195-232` (probe), `:438-480` (`ReadCurve`/`ReadMatrix`),
  `IntelGpuPlugin.cs:444-466` (cached outcome).
- **Problem:** The probe uses a raw `Query`, while `ReadCurve` and `ReadMatrix` treat `CTL_RESULT_ERROR_DATA_NOT_FOUND`
  as "nothing set yet, neutral". If the driver answers DATA_NOT_FOUND for a display nobody has touched, the probe
  returns `Uncertain`, the five colour controls (brightness, contrast, gamma, hue, saturation) are omitted, and the
  outcome is cached for the plugin lifetime.
- **Best solution:** In the curve and matrix probe, treat `CTL_RESULT_ERROR_DATA_NOT_FOUND` from the query as "no stored
  block": return `Applied` without writing, the same way `ThreeDFeature.ProbeSupport` handles its inherited default
  (`ThreeDFeature.cs:110-119`). Any other non-success result keeps today's outcome. With B153 the outcome is cached once
  by the runtime under the colour block's `SupportKey`.
- **Tests:** A `ColorPipeline` probe test with a fake IGCL call returning DATA_NOT_FOUND for the query: probe is
  Applied, no set call happened, the five colour controls are published. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter "FullyQualifiedName~Color"`.
- **Plan v2:** B153.
- **Related:** GPUIR-024, GPUIR-036 (no `ColorPipeline` test exists today).

### GPUIR-011: Intel shared-memory support probe gates a control on registry readback

- **Severity:** medium
- **Where:** `src/WSGM.Plugin.IntelGpu/Graphics/IntelGraphicsMemoryTransport.cs:160-163`,
  `Graphics/AdapterControls.cs:39-45`.
- **Problem:** The probe returns `readback == stored`; a false result omits `graphics.shared-memory`. This breaks the
  rule "never gate a write or a control on readback".
- **Best solution:** Probe success is "`SetDWord` did not throw". Keep the readback only as a trace line ("read back X
  after writing Y") and never use it for the result. An exception from `SetDWord` stays a failed probe (cached, never
  retried).
- **Tests:** `IntelGraphicsMemoryTests`: a fake registry node whose read returns a different value after `SetDWord`
  still publishes `graphics.shared-memory`; a node whose `SetDWord` throws omits it. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter "FullyQualifiedName~IntelGraphicsMemoryTests"`.
- **Plan v2:** B153.
- **Related:** plan claim C9, maintainer rule "no hard readback requirement".

### GPUIR-012: NVIDIA profile sync reports failure after a successful Save when readback differs

- **Severity:** medium
- **Where:** `src/WSGM.Plugin.NvidiaGpu/NvProfiles.cs:246-257` (apply), `:179-187` (restore), `:228-230` (refusal of a
  pending entry).
- **Problem:** After `Save` returns success, a readback mismatch throws `DriverFailure(attempted: true)` and leaves the
  journal entry `Pending = true`. The next sync with the same value then refuses with "An earlier NVIDIA write is
  unconfirmed" unless the driver now reports the value. Reported success is gated on readback.
- **Best solution:** D9 applies: the journal holds no unconfirmed state. A write is dispatched when `_api.Save()`
  returns and failed when `Set`, `Inherit` or `Save` throws; nothing waits for the driver to report a value.
  - `NvOwnedSetting` loses `Pending` and `Restoring` (greenfield, no journal migration). The refusals "An earlier NVIDIA
    write is unconfirmed..." and "An earlier NVIDIA restoration is unconfirmed..." are deleted. In `Apply`'s
    `previous.Written == request.Value` branch, a matching driver value refreshes `ProfileId` as today and any other
    value keeps today's "An external editor changed this NVIDIA setting" message.
  - Apply: the durable-intent `Persist()` of the new entry before `Set` stays (it keeps `Original` across a crash). After
    `_api.Save()` returns, the request counts as written and the entry is already persisted. The trailing
    `Load`/`FindProfile`/`Get` readback and its `DriverFailure("...did not confirm its value.", true)` are deleted; no
    diagnostic readback replaces them, since the next observation pass reads the driver anyway. The early return
    `current.Explicit && current.Value == request.Value` drops its second `Persist()` (there is no flag to clear).
  - Apply failure: when `Set` or `Save` throws (not lost), `Apply` puts back the journal it found (`_owned[key] =
    previous`, or `_owned.Remove(key)` when there was none), calls `Persist()` and rethrows; `Sync`'s existing catch
    reports the failure and calls `_api.Load()` to drop the uncommitted buffer. The write counts as not dispatched. The
    next sync on a new revision writes afresh; that is a new request, not an automatic retry. If the driver committed
    despite the exception, the next sync finds the value in place and adopts it as found.
  - Restore: drop the `Restoring` persist before `Set`/`Inherit` and the readback after `Save`. After `Save` returns,
    control falls through to the existing `_owned.Remove(entry.Key); Persist(); removed++`. When `Set`, `Inherit` or
    `Save` throws, the entry stays as it was and the failure is reported; the next sync's removal checks the driver
    value against `Written` as it does today (restore again when it still holds the written value, drop the entry when
    it does not).
- **Tests:** `NvProfilesTests`: a Save that succeeds while the fake's later `Get` reports another value counts written,
  persists the entry once before the write and performs no `Load` after `Save`; a Save that throws leaves the journal
  as it was before the request (bytes compared) and reports one failure; a following sync with a new revision writes
  once more; a restore whose `Save` succeeds removes the entry without reading back; a restore whose `Save` throws keeps
  the entry and the next sync restores it again. Replace `UnconfirmedSaveIsNotRepeatedAfterReloadOrProcessRestart`,
  `SaveThatCommittedBeforeFailureIsConfirmedWithoutAnotherWrite` and `UnconfirmedRestorationIsNotRepeated` with these
  cases; `ExternalEditIsPreservedDuringSyncAndRemoval` stays.
  Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~NvProfilesTests"`.
- **Plan v2:** B152.
- **Related:** plan claim C4/C9, maintainer rule "no hard readback requirement, sweep every consumer", D9.

### GPUIR-013: IR host turns definite firmware refusals into Unconfirmed

- **Severity:** medium (verifier: scope too narrow, widened by GPUIR-V-004)
- **Where:** `src/WSGM.Plugin.Ir/IrEndpoint.cs:474-485` (`knownRefusal` list in the reply reader),
  `src/WSGM.Plugin.Ir/IrPlugin.cs:463-472` (action outcome mapping),
  `tests/WSGM.Plugin.Ir.Tests/IrEndpointConnectionTests.cs:151` (asserts the current behaviour),
  `src/WSGM/Shell/PluginActionSequence.cs:25,73-76` (`MayHaveActed`).
- **Problem:** Only nine statuses become `IrRejectedException`; every other matched status becomes
  `InvalidDataException`, which `ExecuteActionAsync` reports as `Unconfirmed`. The firmware answers `timeout`,
  `capture-overflow`, `timing-limit`, `invalid-payload`, `invalid-timing`, `duration-limit`, `invalid-code`,
  `unknown-protocol`, `unsupported-protocol`, `invalid-timeout`, `invalid-wifi`, `invalid-web` and `protocol-mismatch`
  before any `emit()` (`main.cpp:128-218,436-536`). A learn timeout therefore shows as "Unconfirmed" in Tools, and route
  automation treats every non-Rejected outcome as possibly acted, so leave-time compensation runs for actions that sent
  nothing.
- **Best solution:** Add one `internal const int ProtocolVersion = 1;` to `IrEndpoint` and use it for the request `v`
  (including the learn `cancel` frame), the reply check and the identify check (B156 raises it to 2, GPUIR-031). In the reply reader built by B003
  (`ReadReply`), once a reply's `id` matches: a `protocol-mismatch` status throws `IrRejectedException("The IR endpoint
  speaks another protocol; flash the firmware that ships with this WSGM.")` whatever its `v` (firmware of the other
  protocol echoes its own); otherwise, if `v == ProtocolVersion` and `status` equals the expected success status,
  return it; if `v == ProtocolVersion` and the status is anything else, throw
  `IrRejectedException(Describe(operation, status))`. This follows protocol 1 as written ("Malformed, unsupported and
  incompatible requests receive explicit error status", `protocol.md`) and the firmware source: every non-success
  status is returned before `emit()`, and the two that follow an `emit()` call (`unsupported-protocol` from
  `sendCode`/`sendAc` when the library's `send` returns false) transmit nothing. Plan v2's B154 test sentence "unknown
  status is Unconfirmed" conflicts with its own spec sentence "any matched non-success status is a refusal"; this
  finding follows the spec and reads the test sentence as an unrecognised reply (`v != 1` or malformed), which stays
  Unconfirmed. Delete the `knownRefusal` list; keep `Describe` for the message
  text and add a line for `storage-failed` (firmware 0.5.0, GPUIR-031). A reply with another `v`, a malformed frame, a
  transport failure or a timeout stays `InvalidDataException`/IO and therefore `Unconfirmed`. This rests on the firmware
  never answering a non-success status after emitting; the scene and remote paths already set `emitted` after a
  successful step, so a refusal on a later scene step still reports `Unconfirmed`. Fold GPUIR-V-004 into the same
  change.
- **Tests:** `IrEndpointConnectionTests`: replace the assertion at `:151`; learn `timeout` and send `invalid-payload`
  raise `IrRejectedException`; an unknown non-success status with the current `v` is a refusal; a reply with another
  `v` is `InvalidDataException` unless its status is `protocol-mismatch`, which is a refusal naming the firmware. `IrRemoteActionTests`: a refused send returns `Rejected` and the scripted link saw exactly one
  send frame (never resent). Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`.
- **Plan v2:** B154 (on top of B003).
- **Related:** GPUIR-V-004, A02-F015/A02-F016 (B003 builds `ReadReply`), plan claim C20.

### GPUIR-015: GPU stop and dispose use private budgets instead of the host's

- **Severity:** medium
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:285-305` (`StopAsync`), `:307-327` (`DisposeAsync` with its own 5 s
  `CancellationTokenSource`); Intel `IntelGpuPlugin.cs:39,310-347,350-364,682-700` (`StopBudget`, loop wait that ignores
  the caller's token, `DisposeAsync` calling `StopAsync(..., CancellationToken.None)`); host
  `src/WSGM/Shell/PluginHost.cs:451-457,489-511`.
- **Problem:** Neither lifecycle stops within the budget the host gives it. The runtime's `StopAsync` waits on the
  retiring task with the caller's token but always returns `true` and throws on cancellation; its `DisposeAsync` reads
  `_context`, which holds whatever deadline the last lifecycle call carried, and makes its own 5 s budget. Intel spends
  up to 5 s on the loop plus 5 s on the lane regardless of the host. That breaks the bounded shutdown plan v2 builds in
  B140 (one deadline, owners stop within it).
- **Best solution:** In `DriverRuntime` (Intel inherits it in B153, which deletes `StopBudget`):
  - Extract the `lock (_stopGate)` block into `Task BeginRetirement()` (starts `RetireAsync` once, or returns the running
    one).
  - `StopAsync(context, token)`: `_running = false; var retiring = BeginRetirement();` then `await
    retiring.WaitAsync(token)`; on `OperationCanceledException` when `token.IsCancellationRequested`, trace "The driver is
    still finishing an owned call; cleanup remains queued." and return `false` instead of throwing. Return `true` when
    retirement completed. The retiring task keeps the lane and the DLL until the native call returns, as today.
  - `DisposeAsync()`: set `_disposed`, call `BeginRetirement()` and return without waiting. Dispose receives no context
    or deadline, and `PluginHost.DisposeAsync` already bounds it with its own `RunAsync` deadline, so reading the stale
    `_context.Deadline` or creating a 5 s budget is wrong. The private budget is deleted.
- **Tests:** `DriverRuntimeTests`: a fake control whose `Read` blocks on a gate; `StopAsync` with an already-cancelled
  token returns `false` promptly, the session is not disposed until the gate opens, then it is disposed exactly once;
  `DisposeAsync` returns before the gate opens and the session is disposed after; a second `StartAsync` after the gate
  opens works. The existing `CancelledStopKeepsBlockedCallOwnedAndPreventsPrematureReopen` asserts that a cancelled
  `StopAsync` throws; change that one assertion to `Assert.False(await runtime.StopAsync(context, cancelled.Token))` and
  keep the rest of the test (a cancelled `StartAsync` still throws while retirement runs). `PluginHost` already treats
  `false` as an unconfirmed release that keeps its slot reserved. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`.
- **Plan v2:** B151 (runtime), B153 (Intel deletion); D1 decided: safety-first ordered steps under one deadline, which
  this finding serves by returning `false` within the host's budget instead of spending its own.
- **Related:** plan claim C11, verifier batch problem 4, GPUIR-026.

### GPUIR-017: NVIDIA rediscovery reallocates its enumeration tables on every 10 s pass

- **Severity:** medium (verifier corrected the solution; the review's version dropped behaviour)
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:399-401` (`Discover()` every observation),
  `src/WSGM.Plugin.NvidiaGpu/NvApi.cs:244-280` (`SettingIds`, `Values` with a 414,112-byte buffer per setting),
  `NvSession.cs:33-80` (`Discover`).
- **Problem:** Each pass enumerates the setting-id table and allocates one large `Values` buffer for each of about 40
  curated settings, roughly 16 MB of large-object heap every 10 s. The review proposed rediscovery only on topology
  change, but that freezes two behaviours: NVIDIA adds the driver's current value to a setting's choices when an external
  editor set an unlisted value (`NvSession.cs:61-65`), and AMD refreshes `GetRange` bounds and per-feature `supported`
  flags that change with display mode (`AdlxSession.Graphics.cs:154-213`, `AdlxSession.Display.cs:123-192`).
- **Best solution:** Keep per-pass rediscovery for both vendors. In `NvSession`, cache `SettingIds()` in a field on first
  use and `Values(id)` in a `Dictionary<uint, uint[]>`, both for the life of the session (the driver's enumeration table
  does not change inside one session; a driver update loses the session and a new `NvSession` starts empty). The current
  value is still read per pass and appended, so the external-value injection keeps working. AMD is unchanged. The
  dominant remaining NVIDIA cost is the per-control DRS load, fixed by GPUIR-V-003.
- **Tests:** `NvSession` talks to the concrete `NvApi` (function pointers), and adding a seam only for this test is not
  worth it. Keep the NVIDIA suite green and have the batch reviewer confirm that `SettingIds` and `Values` are reached
  only through the session caches. Manual: M01-20 on the Optimus notebook shows the same NVIDIA choices, including an
  externally set value. Filter: `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B151.
- **Related:** GPUIR-V-003, GPUIR-020 (setting-id buffer size), verifier batch problem 3.

### GPUIR-V-001: The shared runtime hides a control until restart after one transient read failure

- **Severity:** medium
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:450-531` (`CheckSupport`), especially `:463-476` and the final filter at
  `:524-529`; NVIDIA shared keys at `src/WSGM.Plugin.NvidiaGpu/NvOutputControls.cs:78,119`.
- **Problem:** `CheckSupport` stores any read exception in `_supportResults` under the control's `SupportKey`, and the
  final filter drops every control with a non-null entry. `_supportResults` is never cleared, not on driver loss and
  reopen, not on suspend and resume (the same runtime instance is reused). One transient read failure at first discovery
  (an output asleep at start making `NvApi.Color` fail, a busy ADLX call) hides the control until WSGM restarts. On
  NVIDIA it hides every control sharing the key: all six colour fields (`"color/" + instance`) and all three dithering
  fields. The same happens when the probe's admission is refused (deadline expired or stop began): the
  `OperationCanceledException` is recorded before it is rethrown. Gating a control's existence on a read result goes
  against "never gate a control on readback", and B153 would import this into Intel, which today hides only explicit
  unsupported-feature results.
- **Best solution:** Record only probe outcomes, never read failures or admission refusals:
  - In the read loop, on exception: trace it (`TraceChange`, key `support/<SupportKey>`, "Support read failed; control
    skipped this pass"), do not touch `_supportResults`, rethrow a lost failure as today. The control is simply not in
    `values` and therefore not probed and not published this pass.
  - In the probe loop, build the `WriteAdmission` once (GPUIR-006) from the pass's own deadline (GPUIR-C-001). On
    exception, when the exception is the admission refusal itself (a `DriverFailure` with `Attempted == false` while
    `!admission.Admitted`, or an `OperationCanceledException`), record nothing and rethrow: nothing was written, so the
    probe runs again at the next discovery. Every other exception is recorded as today, including an attempted failure
    that happens to coincide with the admission lapsing, so a failed or uncertain probe is cached and never retried.
  - The final filter publishes a writable control only when `_supportResults` holds `null` for its key (probe
    succeeded). Controls whose read failed this pass are left out of this pass's model and come back on the next pass
    once the read succeeds, with exactly one probe.
- **Tests:** `DriverRuntimeTests`: a fake session whose first `Read` throws publishes the control on the second pass and
  the probe ran exactly once; a probe whose admission is refused (cancelled token) runs on the next pass; a probe that
  throws `DriverFailure(attempted: true)` is never run again. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`.
- **Plan v2:** B151; B153 must use this rule for Intel (GPUIR-024).
- **Related:** GPUIR-006, GPUIR-024, verifier batch problem 6(a).

### GPUIR-V-002: An unreadable NVIDIA per-app journal takes the whole NVIDIA plugin down and reinitializes NVAPI every 10 s

- **Severity:** medium
- **Where:** `src/WSGM.Plugin.NvidiaGpu/NvSession.cs:19-32` (constructor), `NvProfiles.cs:53-58` (journal read),
  `src/Shared/Gpu/DriverRuntime.cs:399,421-426`; test `NvProfilesTests.CorruptJournalAbortsWithoutReplacingIt`.
- **Problem:** A corrupt or locked `nvidia-profiles.v1.json` makes the `NvSession` constructor throw. `ObserveAsync` then
  publishes an empty model and Unavailable, which retracts every global DRS, colour, dithering, HDR and G-SYNC control
  because of a per-app bookkeeping file. Each 10 s pass repeats the whole sequence: `nvapi64.dll` load,
  `NvAPI_Initialize`, DRS session, full `LoadSettings`, teardown.
- **Best solution:** `NvProfiles` has no trace channel today; its constructor gains the session's
  `Action<string, string> report` (`NvSession` passes its `_report`; `NvProfilesTests` pass a recording delegate; B151
  later swaps it for `DriverLog` with the rest of the runtime). The constructor wraps `DriverStateFile.Read` in `try`;
  on `DriverFailure` it keeps an empty `_owned`, stores the reason in a readonly `string? _unreadable` and reports it
  once (key `profiles`). `NvProfiles.Sync` checks `_unreadable` before the revision guard: it returns one
  `ApplicationProfileFailure(game.ProfileId, executable, "", "The NVIDIA per-application record could not be read
  (<reason>); nothing was changed.")` per requested executable, performs no DRS `Load`, no write and never calls
  `Persist`. `NvSession` and every global control keep working. Same rule as Intel (GPUIR-002) and IR (A02-F017, in
  GPUIR-021).
- **Tests:** `NvProfilesTests`: replace `CorruptJournalAbortsWithoutReplacingIt` with "corrupt journal -> constructor
  succeeds, Sync returns one failure per executable, the fake DRS saw no Set or Save, file bytes unchanged"; same for a
  locked file and a directory at the path. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~NvProfilesTests"`.
- **Plan v2:** B150.
- **Related:** GPUIR-002, GPUIR-003, verifier batch problem 8.

### GPUIR-V-003: NVIDIA reloads the DRS database once per control per observation

- **Severity:** medium (PLAUSIBLE on cost)
- **Where:** `src/WSGM.Plugin.NvidiaGpu/NvSession.cs:265-269` (`NvSettingControl.Read` -> `_api.Load()`),
  `NvOutputControls.cs:247-251` (`NvGsyncControl.Read`), `NvSession.cs:37` (load in `Discover`).
- **Problem:** With about 40 curated settings, every 10 s pass makes about 40 `NvAPI_DRS_LoadSettings` calls, each of
  which re-reads the whole driver profile store. That is the dominant steady driver load of the NVIDIA package.
- **Best solution:** Add `void BeginPass()` to `IDriverSession`. The runtime calls `_session.BeginPass()` at the start of
  `ObserveAsync` (before `Discover`) and at the start of the command's native work (before `Write`). NVIDIA implements it
  as one `_api.Load()`; `Discover` drops its own `Load`; `NvSettingControl.Read` and `NvGsyncControl.Read` call `Get`
  against the loaded state without loading. Probes keep their `Load -> Set -> Save -> Load` bracket unchanged. Writes
  today are `Load -> Set -> Save` with no trailing load, and the readback was honest only because `Read` reloaded:
  `NvSettingControl.Write` and `NvGsyncControl.Write` therefore drop their leading `Load` (the command's `BeginPass`
  just loaded) and gain one `_api.Load()` after `Save`, so the runtime's readback reads the committed store, not the
  session buffer it just set. AMD implements `BeginPass` as empty; Intel maps it to
  `IgclSession.BeginPass` in B153. `NvProfiles.Sync` keeps its own `Load`.
- **Tests:** `DriverRuntimeTests` with a fake session: `BeginPass` is called once per observation pass, before
  `Discover` and every `Read`, and once per command before `Write`. The NVIDIA side has no seam below `NvApi`; keep its
  suite green (`INvProfiles` covers only the profile path). Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B151.
- **Related:** GPUIR-017, GPUIR-023.

### GPUIR-C-001: A support probe after start checks the expired start deadline and kills the observation loop

- **Severity:** medium (added by the solution check; not in the review)
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:493-505` (probe admission reads `_context?.Deadline`), `:268-273`
  (`SessionChangedAsync` replaces `_context`), `:508-516` (the refusal is recorded, then rethrown), `:421` (`ObserveAsync`
  does not catch `OperationCanceledException`), `:390` (`ObserveLoopAsync` catches it only when its own token is
  cancelled); `src/WSGM/Shell/PluginHost.cs:573` (`Context = Context with { Deadline = deadline }` for every lifecycle
  call).
- **Problem:** `_context.Deadline` is the deadline of the last lifecycle call, the start or a session change, and it has
  expired seconds after that call returned. Every later 10 s pass that meets a support key it has not probed yet (a
  display connected after start: NVIDIA colour, dithering and HDR controls are keyed per output, AMD display controls
  per instance; an AMD feature whose `supported` flag turns on with a display mode) reaches the admission check, which
  throws `OperationCanceledException("GPU support discovery deadline expired.")`. `CheckSupport` records that message
  in `_supportResults`, so the control stays hidden for the plugin lifetime, and rethrows. `ObserveAsync` lets the
  exception through, and `ObserveLoopAsync`'s filter does not match because its token is not cancelled, so the loop task
  faults: no more state refresh, no driver-loss detection and no new displays until the plugin is stopped and started.
  The runtime tests pass `Deadline.Never`, so none of them can see it. Docking a notebook with an NVIDIA or AMD GPU is
  enough.
- **Best solution:** The probe admission takes the deadline of the operation that runs the pass and never reads
  `_context.Deadline`. `ObserveAsync(CancellationToken token, Deadline deadline)` passes it to `CheckSupport`;
  `StartAsync` passes `context.Deadline`; `ObserveLoopAsync` passes `Deadline.Never` (the loop is already bounded by its
  own token, which stop cancels). With GPUIR-006 the probe admission is `new(token, deadline, () => _running && _session
  is not null)`, and with GPUIR-V-001 a refused probe records nothing, so a probe refused because the start deadline ran
  out ends that pass as Unavailable and runs on the next loop pass. Together with GPUIR-015 (dispose no longer reads the
  stale deadline) the runtime stops reading `_context.Deadline` anywhere. No state, guard or timer is added.
- **Tests:** `DriverRuntimeTests`: a fake session whose `Discover` returns one control at start and a second control with
  a new `SupportKey` from the second pass on; start with `Deadline.Never`, then `SessionChangedAsync(context with
  { Deadline = Deadline.Expired })`; after one observation pass (about 10 s, the only slow runtime test) the second
  control is published and its probe ran once, and the following `StopAsync` traces no "Driver observation stopped"
  line (`RetireAsync` writes it only for a faulted loop). Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`.
- **Plan v2:** B151 (with GPUIR-006 and GPUIR-V-001, which touch the same lines). B151's resolves list does not name it;
  the batch must carry it.
- **Related:** GPUIR-006, GPUIR-V-001, GPUIR-015, GPUIR-017 (per-pass rediscovery is what surfaces new keys).

### GPUIR-003: DriverStateFile treats an inaccessible journal as absent and caps it at 4 MiB

- **Severity:** low (verifier lowered from medium; the data-loss path is mostly wrong)
- **Where:** `src/Shared/Gpu/DriverStateFile.cs:10-24`.
- **Problem:** On Windows `File.Exists` is true for a locked or ACL-denied file, and `File.ReadAllBytes` then throws, so
  construction fails closed and nothing is rewritten; a directory at the path makes `Exists` false, but the later
  `File.Move` onto a directory throws, so the original survives too. What remains: the 4 MiB refusal is an arbitrary size
  cap (a large valid journal disables NVIDIA profiles), a corrupt journal throws a raw `JsonException`, and the
  `Exists` probe is a race. The real cost of failing closed is GPUIR-V-002.
- **Best solution:** Rewrite `Read<T>(string path, T empty)`:
  open with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)` and deserialize from the stream;
  `FileNotFoundException` and `DirectoryNotFoundException` return `empty` (Absent); `JsonException` or a null result
  throws `DriverFailure("<file> is corrupt: ...")`; `IOException` and `UnauthorizedAccessException` throw
  `DriverFailure("<file> could not be read: ...")`. Delete the `FileInfo.Length` check. `Write` already uses a unique
  temp name, `WriteThrough` and `Flush(true)`; keep it unchanged. This is the plugin-side form of critic conflict 16's
  rule; no shared outcome type is added.
- **Tests:** A small `DriverStateFile` test in the NVIDIA project (the file is linked there): absent returns empty, a
  directory at the path and a file opened with `FileShare.None` throw `DriverFailure`, corrupt JSON throws
  `DriverFailure`, a 5 MiB valid journal reads. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverStateFile|FullyQualifiedName~NvProfilesTests"`.
- **Plan v2:** B150.
- **Related:** GPUIR-002, GPUIR-V-002, critic conflict 16, rule "no arbitrary limits".

### GPUIR-005: Intel has no pre-setter admission recheck

- **Severity:** low (verifier lowered from medium)
- **Where:** `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs:96-117`; preparatory reads in `Controls/FieldControl.cs:76-81`,
  `Graphics/ThreeDFeature.cs:308-321`, `Display/ArcSyncControls.cs:311-330`.
- **Problem:** Intel checks the deadline once and then writes synchronously in the same call. Today the only window is
  the few preparatory IGCL reads, so this is a uniformity gap rather than a live defect. It becomes real once B153 puts
  Intel writes behind the runtime's `Task.Run` hop, where a command can be cancelled or expire between admission and the
  setter.
- **Best solution:** As part of B153 only: `IntelControl.Write(CapabilityValue value, WriteAdmission admission)` and the
  Intel set entry points take the admission and call `admission.Check()` immediately before the native setter:
  `IgclSource.Write`, `ThreeDFeature.Write`, `ColorPipeline.Apply`, `IntelGraphicsMemoryTransport.TryWrite`, and the
  PSR off and on writes in `LowRefreshRateControl` (`Display/PowerControls.cs`). `IgclDriverControl.Write` passes the
  runtime's admission through. `ProbeSupport` gets the probe admission the same way. No change before B153.
- **Tests:** Adapter test: a command cancelled after admission but before the setter returns Rejected and the fake IGCL
  setter was not called. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`.
- **Plan v2:** B153.
- **Related:** GPUIR-006, GPUIR-007, plan claim C5.

### GPUIR-008: DriverRuntime reports every refusal as TransportFaulted

- **Severity:** low (verifier lowered from medium; router preflight already rejects most cases with the right codes)
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:92-103,140-146,593-602` (`Result`), overlay mapping
  `src/WSGM/Shell/DeviceOverlayBridge.cs:1391-1394`.
- **Problem:** Generation mismatch, expired deadline, unknown control, invalid value and pre-dispatch cancellation all
  carry `CapabilityReasonCode.TransportFaulted`, non-retryable. Router preflight (`PluginCapabilityChannel.cs:197-206`,
  `DeviceCapabilityRouter.cs:443-490`) catches most of these first, so only races reach the runtime. The impact is
  diagnostic: the overlay's status comes from the outcome (`Rejected` is `CommandProgress.Failed` whatever the code,
  `DeviceCapabilityRouter.Progress`) and from the state's reason, not from the command result's reason, so the wrong
  code shows up in wsgm.log and in `CapabilityDesiredReconciler`'s failure line, not on screen. It matters for B153,
  where Intel's correct codes would otherwise regress to the runtime's.
- **Best solution:** Build every result with the SDK helpers in `CommandResults`, using Intel's codes:
  not running or no session -> `CommandResults.Rejected(command, HostUnavailable, "The GPU driver is not open.")`;
  cycle or descriptor generation mismatch -> `CommandResults.Rejected(command, GenerationChanged, ..., retryable:
  true)`; deadline expired before dispatch, cancellation before dispatch (today's `OperationCanceledException` catch) or
  admission refused in the hop -> `CommandResults.Rejected(command, Quiescing, ..., retryable: true)`; control not found
  -> `Rejected(..., Unsupported, ...)`; `Accepts` false -> `Rejected(..., ValueOutOfRange, ...)`; driver refusal
  (`DriverFailure` with `attempted: false` from the native call) -> `Rejected(..., Unsupported, message)`; attempted
  failure -> `CommandResults.Indeterminate(command, TransportFaulted, message, RollbackResult.NotRequired)`. Applied
  results also take Intel's shape: `CommandResults.Verified(command, readback)` on a match and
  `CommandResults.Unverified(command, requested)` otherwise (the SDK helper whose contract is "the written value
  stands"), instead of today's `AppliedUnverified` carrying a `TransportFaulted` reason, which marked a successful write
  as a fault; the session lost before readback keeps `CommandResults.Unverified(command, detail)` with today's text.
  The router reads only the outcome of an applied result, so nothing visible changes. Delete the private `Result`
  helper.
- **Tests:** `DriverRuntimeTests`: one case per code above. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`.
- **Plan v2:** B151.
- **Related:** GPUIR-007.

### GPUIR-018: Shared descriptor labels are truncated to 48 or 64 characters

- **Severity:** low
- **Where:** `src/Shared/Gpu/DriverDescriptors.cs:14,32,61,70-73` (`.Take(max)`).
- **Problem:** NVIDIA and AMD labels are cut at 48 or 64 characters. The SDK has no label length limit and Intel does
  not truncate (`src/WSGM.Plugin.IntelGpu/Controls/Descriptors.cs:122-139`). This violates "no arbitrary limits".
- **Best solution:** Keep the unsafe-character filter and delete every `Take(max)` and the `max` parameters. Labels
  that are short today are byte-identical; longer ones now show whole, the same as Intel's.
- **Tests:** A `DriverDescriptors` test in the NVIDIA project: a 100-character label survives whole; control characters
  are still filtered. Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B151.
- **Related:** rule "no arbitrary limits", GPUIR-037 (Intel `DisplayIdentity` doc claims a 48-character limit).

### GPUIR-019: Unchanged plugin health is republished on every observation

- **Severity:** low (verifier: impact understated, severity unchanged)
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:416-419,584-591`, `src/WSGM/Shell/PluginHost.cs:184-213` (`Publish`),
  `src/WSGM/Shell/CommonPluginSteamUiSource.cs:431-445`; Intel's own dedupe at `IntelGpuPlugin.cs:807-837`.
- **Problem:** The runtime publishes health on every 10 s pass and `PluginHost.Publish` does not deduplicate. Each pass
  posts to the UI thread, raises `HealthChanged` -> `GpuCoordinator.Changed`, and through
  `CommonPluginSteamUiSource.OnHealthChanged` -> `OnChanged` -> `_revision++` republishes the Steam UI plugin model every
  10 s for NVIDIA and AMD.
- **Best solution:** Deduplicate once, in `PluginHost.Publish`, inside the existing lock: after the admission checks,
  `if (owner.Health == publication) return;` (`PluginHealthPublication` is compared by instance, generation, health and
  detail), before assigning and posting. Every plugin benefits, and B153 deletes Intel's `_health` field. The runtime's
  `Health` keeps calling `TraceChange`, which already dedupes the log line.
- **Tests:** `tests/WSGM.Tests` `PluginHost` test: two identical publications post once and raise `HealthChanged` once;
  a changed detail posts again. Filter:
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginHost"`.
- **Plan v2:** B151 (the batch lists `src/WSGM/Shell/PluginHost.cs`).
- **Related:** GPUIR-007.

### GPUIR-020: Non-ABI count caps fail discovery

- **Severity:** low
- **Where:** `src/WSGM.Plugin.AmdGpu/AdlxNative.cs:58-64` (`Items`: list over 1024 fails discovery),
  `src/WSGM.Plugin.NvidiaGpu/NvApi.cs:244-260` (`SettingIds`: fixed 4096-entry buffer, larger tables fail),
  `src/WSGM.Plugin.IntelGpu/Display/ColorPipeline.cs:263` (`NumBlocks > 32`).
- **Problem:** Each cap is a count chosen by WSGM, not by the native ABI, and exceeding it removes the whole feature.
  ABI-defined maxima are fine and stay (`NVAPI_SETTING_MAX_VALUES` 100 in `Values`, `NVAPI_MAX_PHYSICAL_GPUS` 64, IGCL
  frame sizes).
- **Best solution:**
  - AMD `Items`: delete the `count > 1024` check; the list's own `Size()` is the count.
  - NVIDIA `SettingIds`: start with the current 4096 buffer. `nvapi.h` documents `NVAPI_END_ENUMERATION` (-7) as "the
    provided pMaxCount is not enough to hold all settingIds"; on that status (or an in/out count larger than the
    buffer) allocate the reported count when it is larger, otherwise double the buffer, and call again until
    `NVAPI_OK`. It is a read, so repeating it is fine. With GPUIR-017 the result is cached per session.
  - Intel colour: keep `NumBlocks is 0` as "no colour pipe" and delete `or > 32`; the block array is already allocated
    from `caps.NumBlocks`.
- **Tests:** No native fakes exist for these; keep the existing suites green and add a `NativeContractTests` assertion
  only if AMD's list helper is reachable with a fake vtable. Filters:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`,
  `dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj`,
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`.
- **Plan v2:** B152 (NVIDIA, AMD); the Intel colour cap rides in B153, which already edits `ColorPipeline.cs`.
- **Related:** rule "no arbitrary limits", GPUIR-V-007.

### GPUIR-021: Three atomic JSON state helpers with different safety

- **Severity:** low
- **Where:** `src/Shared/Gpu/DriverStateFile.cs` (durable, unique temp), `src/WSGM.Plugin.IntelGpu/StateFile.cs:41-59`
  (no flush, fixed `.tmp`, unreadable -> null), `src/WSGM.Plugin.Ir/IrPayload.cs:110-123,147-160,166-196`
  (`IrLibrary.LoadAsync`, `IrPairing.LoadAsync` with `File.Exists`, durable `JsonFile.WriteAsync`).
- **Problem:** The three packages read and write their state with different guarantees. Intel's is the weakest
  (GPUIR-002). IR's loaders use `File.Exists`; as with GPUIR-003 the data-loss reading of A02-F017 is mostly wrong: a
  locked or ACL-denied `library.json` passes `File.Exists`, `File.OpenRead` throws, and `IrPlugin.StartAsync` (which
  has no catch) already fails start and writes nothing; a directory at the path loads as empty, but every later save
  fails on the `File.Move` and the directory survives. What remains is the `Exists` race, raw exception texts in the
  Failed health, and a third read rule beside the GPU one.
- **Best solution:** Two assemblies, two helpers, one rule; no cross-package shared code (IR does not link `Shared/Gpu`).
  - GPU: Intel uses `DriverStateFile` (B150, see GPUIR-002 and GPUIR-003); `StateFile.cs` is deleted.
  - IR (B154, A02-F017): `IrLibrary.LoadAsync` and `IrPairing.LoadAsync` open the file directly
    (`new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous)`).
    `FileNotFoundException`/`DirectoryNotFoundException` -> `IrLibrary.Empty` / `null` (Absent). A parse or `Validate`
    failure throws `InvalidDataException("<file> is corrupt: ...; nothing was changed.")`. Any other IO error throws
    `IOException("<file> could not be read: ...; nothing was changed.")`. `IrPlugin.StartAsync` lets these propagate:
    `PluginHost.StartAsync` then quarantines the plugin and publishes Failed health with that message
    (`PluginHost.cs:600-615`), so the user sees why, and no save path can run. `JsonFile.WriteAsync` already writes
    durably with a unique temp name; keep it.
- **Tests:** `IrLibraryTests`: a `library.json` opened with `FileShare.None`, a directory at the path and a corrupt file
  each make `LoadAsync` throw and leave the bytes unchanged; an absent file loads empty; same three cases for the
  pairing file. GPU cases are in GPUIR-002 and GPUIR-003. Filters:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrLibraryTests"` and
  the GPU filters above.
- **Plan v2:** B150 (GPU half), B154 (IR loaders, resolves A02-F017).
- **Related:** GPUIR-002, GPUIR-003, A02-F017 (plan claim C21: "NOT READY" was unnecessary), critic conflict 16.

### GPUIR-022: Dead preparatory reads before writes

- **Severity:** low
- **Where:** `src/WSGM.Plugin.NvidiaGpu/NvSession.cs:239-246` and `NvOutputControls.cs:255-263` (`Get` result
  discarded), `src/WSGM.Plugin.AmdGpu/AdlxSession.cs:271-283` (`read()` result discarded).
- **Problem:** Each write performs a read whose result is thrown away; the comments say the read must not gate the write,
  which is equally true of not reading at all. It costs a driver call per write and obscures the write path.
- **Best solution:** Delete the three reads and their comments. Nothing else depends on them (the readback after the
  write is the runtime's).
- **Tests:** Existing NVIDIA and AMD suites. Filters:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`,
  `dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj`.
- **Plan v2:** B152.
- **Related:** GPUIR-023.

### GPUIR-023: NvGsyncControl duplicates NvSettingControl's DRS logic

- **Severity:** low
- **Where:** `src/WSGM.Plugin.NvidiaGpu/NvOutputControls.cs:224-267` versus `NvSession.cs:213-257`.
- **Problem:** The G-SYNC control repeats probe, read and write over the global DRS profile for setting `0x1094f157`
  with a boolean encoding. Two copies of the Load/Set/Save/Load bracket and the journal-free global write must be kept
  in step (GPUIR-006, GPUIR-022 and GPUIR-V-003 each touch both; GPUIR-012 touches only `NvProfiles`).
- **Best solution:** Extract one internal helper in `NvSession.cs`, `NvDrsSetting(INvProfiles api, uint id)` (both
  controls take `INvProfiles` today, which is what keeps them testable with the profile fake) with
  `uint Read()`, `void Write(uint value, WriteAdmission admission)` and `void Probe(uint current, WriteAdmission
  admission)`, holding the bracket once. `NvSettingControl` and `NvGsyncControl` both delegate to it and keep their own
  descriptors and value encoding (choice versus boolean), so capability ids, labels and sections are unchanged. Merging
  the two controls into one class with a boolean mode was rejected because it would touch descriptor construction and
  the persisted ids.
- **Tests:** Existing NVIDIA suite plus the persisted-id fixture (B152 adds `driver.<id>`, `output-<sha24>`,
  `display.*`). Filter: `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B152.
- **Related:** GPUIR-022, GPUIR-V-003.

### GPUIR-024: Intel caches support outcomes in four places

- **Severity:** low
- **Where:** `src/WSGM.Plugin.IntelGpu/Igcl/IgclSource.cs:64,90-106` (`_support`), `Graphics/ThreeDFeature.cs:53,103-129`
  (`_support`), `Display/ColorPipeline.cs:159-161,195-232` (`_curveSupport`, `_matrixSupport`),
  `IntelGpuPlugin.cs:46,444-461` (`_supportResults`).
- **Problem:** The same probe outcome is cached in the structure, in the feature, in the colour pipeline and in the
  plugin; the copies can disagree and each needs its own "never retried" reasoning.
- **Best solution:** In B153 keep only the runtime cache keyed by `SupportKey`, under GPUIR-V-001's rule (probe outcomes
  only). Each `IntelControl` exposes its shared structure as `SupportKey` (source id for `IgclSource`, feature plus
  adapter for `ThreeDFeature`, `"color-curve/<display>"` and `"color-matrix/<display>"` for the colour pipeline), so a
  structure is still probed once. Delete the four caches; the probe methods remain and are uncached.
- **Tests:** Adapter test: two controls sharing a structure are probed once; a failed probe is never repeated across
  passes; a read failure is not cached. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`.
- **Plan v2:** B153.
- **Related:** GPUIR-V-001, GPUIR-007, GPUIR-010, plan claim C8.

### GPUIR-026: Intel StopLoopAsync forgets a loop that is still running

- **Severity:** low
- **Where:** `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs:682-700` (`StopLoopAsync`), `:300` (`ResumeAsync` ->
  `StartLoop`).
- **Problem:** After its 5 s wait `StopLoopAsync` disposes the CTS and clears `_loopTask` even when the loop has not
  finished, so a later `ResumeAsync` can start a second loop while the first is still in a pass.
- **Best solution:** Removed with the loop in B153. The runtime's `RetireAsync` awaits the running loop before closing
  the session, and `DriverRuntime.StartAsync` awaits `_retiring` before starting a new cycle, so a resume cannot overlap
  a running pass. No change to `IntelGpuPlugin` before B153. Plan v2's B153 wording "StopLoopAsync waits for a running
  loop" is superseded: `StopLoopAsync` is deleted, not patched.
- **Tests:** `DriverRuntimeTests` (run from the Intel project or the NVIDIA one; the runtime is linked source): a pass
  blocked on a gate, `StopAsync` with a short token, then `ResumeAsync`; the second loop starts only after the gate opens
  and the first pass finished; only one pass runs at a time. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`.
- **Plan v2:** B153.
- **Related:** GPUIR-015, GPUIR-007.

### GPUIR-029: IR sequence wait carries a poll bound that only fakes completion

- **Severity:** low (unreachable from today's callers)
- **Where:** `src/WSGM.Plugin.Ir/IrPlugin.cs:666-685` (`WaitForSequenceAsync`, `SequencePollLimit` 1,200 at `:12-13`),
  caller `remote-run` at `:600-614`; host budgets `src/WSGM/Shell/CommonPluginOverlaySource.cs:105` and
  `CommonPluginSteamUiSource.cs:293` (10 s), `src/WSGM/Shell/PluginActionSequence.cs:109` (route steps, at most 120 s),
  `PluginHost.InvokeActionAsync` (every action runs under `RunAsync(deadline)`).
- **Problem:** After 1,200 half-second polls (ten minutes) the wait returns normally whether or not `SequenceRunning`
  cleared, and the action reports `Dispatched` as if the sequence completed. No caller can reach that path: every
  action carries a host deadline of 10 s (overlay and Steam UI) or at most 120 s (route steps), and cancellation already
  ends the wait through the existing `catch (OperationCanceledException)`, which sends the endpoint's `cancel` and
  rethrows, so the action reports `Unconfirmed`. The bound is a count chosen by WSGM that can only produce a false
  completion; the authoring check that keeps a sequence's delays within ten minutes (`embed_remotes.py:272-279`) stays
  where it belongs, in the firmware build.
- **Best solution:** Delete `SequencePollLimit` and its comment; `WaitForSequenceAsync` polls until the endpoint reports
  the sequence finished, and the action's token, which the host always bounds, ends it otherwise through the existing
  cancel path. Nothing is added. This deviates from plan v2's B154 wording ("the sequence wait reports 'still running'
  when it stops polling"): with the bound gone the wait stops only by cancellation, which already reports `Unconfirmed`
  and sends `cancel`, so the extra outcome would be dead code.
- **Tests:** `IrRemoteActionTests`: a scripted endpoint that reports `sequenceRunning` twice and then not yields
  `Dispatched` after three polls; one that keeps reporting running with the action token cancelled after the first poll
  yields `Unconfirmed` and the endpoint saw exactly one `cancel`. Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrRemoteActionTests"`.
- **Plan v2:** B154.
- **Related:** GPUIR-014 (refuted: the host already cancels the wait on stop).

### GPUIR-030: IR request framing round-trips through a dictionary

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/IrEndpoint.cs:408-430` (`Exchange`).
- **Problem:** The argument object is serialized with `IrLibrary.Json`, deserialized into
  `Dictionary<string, object?>`, given `v`, `id`, `op` and `token`, and serialized again: two extra passes and a
  dictionary of boxed `JsonElement`s per request.
- **Best solution:** `var request = JsonSerializer.SerializeToNode(arguments, IrLibrary.Json)!.AsObject();` then
  `request["v"] = ProtocolVersion; request["id"] = id; request["op"] = operation;` and the token when present, then
  `var frame = request.ToJsonString();`. Property order (arguments first, then `v`, `id`, `op`, `token`) and the compact
  default encoding stay as today, so the wire bytes are identical in B154 (`ProtocolVersion` is 1 there, GPUIR-013). The
  UTF-8 byte check against `MaxFrame` stays.
- **Tests:** `IrEndpointConnectionTests`: pin the exact frame string for a send, a learn with token and a climate
  request against the scripted link (record them on master first, then assert equality). B156 changes only the `v`
  value in those pinned strings when it moves to protocol 2. Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrEndpointConnection"`.
- **Plan v2:** B154.
- **Related:** A02-F015 (B003 frame bound).

### GPUIR-031: IR firmware remaining defects and arbitrary authoring caps

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (feedback `:72` and loop, `sendAc` `:193-195`, `sendCode`
  `:174-180`, NVS writes `:428-429,523-525`, generated definitions `:718-719`); `Firmware/embed_remotes.py` (button cap
  `:424-425`, step cap `:263-264`, value checks around `:151,229`, page references `:404`, JSON load `:412`, page script
  `:325-335`); `Firmware/remotes/hisense-tv/index.html:255`; `protocol.md`, `README.md`.
- **Problem:** The Codex firmware audit's F002 to F014 are confirmed in source. In short: the feedback motor can stay on
  after a `millis()` wrap (F002); a numeric A/C model narrows from `int` to `int16_t` without a range check (F003);
  `strtoull` accepts signed or whitespace-prefixed hex (F004); NVS writes report `ok` without checking persistence (F005);
  generated definitions are used after an unchecked `deserializeJson` (F006); the 128-button and 32-step authoring caps
  are arbitrary counts that still do not bound the `remotes` reply, which can exceed the 32 KiB protocol frame (F007);
  malformed definitions escape as raw tracebacks (F008); page-reference checking misses valid attribute syntaxes (F009);
  an explicit address without a command is silently ignored (F010); duplicate JSON keys are silently last-wins (F011);
  the climate page trusts stale stored drafts (F012), parallel climate clicks derive from the same confirmed state
  (F013), and an older reply can overwrite a newer status line (F014).
- **Best solution:** One batch: firmware `0.5.0` with protocol 2 (D11), plus the host side of that protocol change:
  - F002: replace the `feedbackDeadline` sentinel with `bool feedbackActive` and `uint32_t feedbackStart`; the loop
    ends feedback when `feedbackActive && millis() - feedbackStart >= feedbackDuration`. GPIO, duration and colour
    unchanged. No scheduler.
  - F003: in `sendAc`, a numeric `model` outside `[-1, 32767]` replies `invalid-ac-state` before any `IRac` call.
  - F004: before `strtoull`, accept only an optional `0x`/`0X` followed by 1 to 16 hex digits; anything else replies
    `invalid-code`.
  - F005: write NVS first and update RAM only on success. Check `settings.begin(...)` and the byte count each
    `putString` returns; on any failure reply `storage-failed` and leave the RAM credentials and token unchanged. No
    retry. The host shows it as a refusal (GPUIR-013); add its text to `Describe` and `protocol.md`.
  - F006: check both `deserializeJson` results in `setup`; on failure log to serial and serve zero remotes (empty
    catalog, no emission from the failed definitions).
  - F007 (D11 decided: catalog paging now, no build-time size refusal): delete the 128-button and 32-step caps and add
    no size check in `embed_remotes.py`.
    - Firmware: `remotes` takes an optional integer `offset` (default 0) and answers `ok` with data `{ "size": <byte
      length of the generated CatalogJson>, "offset": <offset>, "text": <catalog bytes from offset> }`. A page carries
      at most 8192 bytes, shortened so it never ends inside a UTF-8 sequence. The catalog is compact JSON whose control
      characters `json.dumps` already escaped, so re-escaping a page as a string at most doubles it (only `"` and `\`):
      every reply stays under 17 KiB, inside the 32 KiB frame, for a catalog of any size. A negative, non-integer or
      larger-than-`size` offset answers `invalid-offset`. The `catalog` document stays for the web pages'
      `remote.json`.
    - Protocol 2: `reply()` echoes `v: 2`, `dispatch` answers any other `v` with `protocol-mismatch`, `describe`
      reports `protocol: 2`, `Firmware` becomes `0.5.0`. Every other operation keeps its fields and statuses.
    - Host, in the same batch so both sides change together: `IrEndpoint.ProtocolVersion` (GPUIR-013) becomes 2,
      `ParseIdentity` requires protocol 2 ("this plugin requires protocol 2; flash firmware 0.5.0"), and
      `ListRemotesAsync` requests pages from offset 0, appending each page's `text` as UTF-8 bytes until `offset`
      reaches `size`, then deserializes the whole buffer into `IrRemoteCatalog`. Each page is its own request and id
      under the existing 5 s timeout. A page whose `offset` is not the requested one, whose `size` changed, or whose
      `text` is empty before the end throws `InvalidDataException`, which fails the refresh; the next refresh starts at
      0 (a read, nothing is emitted). No page count cap: the loop ends because `offset` grows toward a fixed `size`.
    - `protocol.md` becomes "IR endpoint protocol 2" and documents the paged `remotes`, `invalid-offset` and
      `storage-failed`; the IR README says firmware 0.5.0 and this WSGM need each other.
  - F008: explicit type and presence checks at the cited boundaries raise `DefinitionError` with the definition's
    location; valid output unchanged.
  - F009: check static `data-button`/`data-sequence` attributes with the standard library `html.parser.HTMLParser`
    (decoded values, any quoting or case); document that only static literal attributes are checked.
  - F010: `code()` rejects an explicit `address` without `command` (after inherited-default removal) with
    `DefinitionError`. The tracked remotes all use `command` and stay valid.
  - F011: `json.load(..., object_pairs_hook=...)` rejects duplicate keys at every nesting level with a `DefinitionError`
    naming the file; the deliberate `remotes.local` folder override stays.
  - F012: when the climate page restores a stored draft, keep each field only if it is valid for the current catalog
    (mode and fan in the remote's lists, degrees a finite number in range, power boolean); otherwise use the catalog
    default for that field. Appearance unchanged.
  - F013 and F014: each page keeps one request counter; a reply updates the status line only when it belongs to the
    latest request, and climate steps compute from the draft the page shows (updated immediately on click) instead of
    the last confirmed state. Every click still sends its own request; nothing is queued or retried.
- **Tests:** Build only:
  `.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware`. The generator's checks run as
  part of that build; add negative definitions under a scratch folder to see F008, F010 and F011 fail, and one remote
  with 400 buttons to see the build succeed with a catalog above 32 KiB, then delete them. No upload. Host:
  `IrEndpointConnectionTests` with the scripted link: a three-page catalog whose page boundary falls inside a
  multi-byte label reassembles to the same `IrRemoteCatalog`; a page with the wrong `offset` or a changed `size` throws
  `InvalidDataException`; an identity reporting protocol 1 is refused as incompatible; a `protocol-mismatch` reply is a
  refusal. Filter: `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`. Manual M01-37/38 after the
  maintainer flashes: Read built-in remotes lists every remote.
- **Plan v2:** B156; D11 decided: paging now with the protocol bump on firmware and host, no build-time refusal.
- **Related:** A02S01-F002 to A02S01-F014, GPUIR-004 (F001), GPUIR-013 (`ProtocolVersion`), GPUIR-030, GPUIR-032,
  plan claims C17, C23, C24, D2 (decided: the IR 32 KiB protocol frame is on the accepted list and stays; the catalog
  pages within it), D11.

### GPUIR-032: Firmware answers busy to non-emitting operations

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp:436-439` (`busy()` check before `learn`, `send*`,
  `press/climate/run`, `protocols` and `wifi`); `src/WSGM.Plugin.Ir/protocol.md` (lists only send, press, climate,
  sequence start and learn as refused while busy).
- **Problem:** `protocols` is a read-only query but is refused with `busy` during a learn or a sequence, contrary to
  `protocol.md`.
- **Best solution:** Move the `protocols` branch above the `busy()` check in the dispatcher. Keep `wifi` refused while
  busy (changing radio state mid-learn is unwanted) and add that sentence to `protocol.md`.
- **Tests:** Build only, as GPUIR-031. Manual check after flashing: `protocols` answers during a learn.
- **Plan v2:** B156.
- **Related:** GPUIR-031.

### GPUIR-033: SerialIrLink leaks the port when Open fails

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/IrEndpoint.cs:103-119` (`SerialIrLink` constructor; plan v2 lists a `SerialIrLink.cs`,
  but the class lives in `IrEndpoint.cs`).
- **Problem:** When `_port.Open()` throws, the `SerialPort` is never disposed and its handle stays until finalization.
- **Best solution:** Wrap `_port.Open()` in `try { ... } catch { _port.Dispose(); throw; }`, as `TcpIrLink` already does
  (`IrEndpoint.cs:170-178`). No factory or adapter seam (the Codex disposition's seam is over-engineering).
- **Tests:** None practical without a seam (opening a missing COM port throws before any handle exists on most
  machines); keep the IR suite green. Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`.
- **Plan v2:** B154.
- **Related:** A02-F018 (same defect), plan claim C22.

### GPUIR-036: GPU and IR test quality gaps

- **Severity:** low
- **Where:** `tests/WSGM.Plugin.IntelGpu.Tests/UnsupportedFeatureTests.cs:8-28`, `ThreeDFeatureTests.cs:63-79`
  (`TheFeaturesThatAreNotRowsAreSkipped`, `TheFeaturesTheHeaderDescribesArePublished`), `ArcSyncTests.cs:84-88`
  (`CustomIsOfferedOnlyWhenTheRangeAllowsIt`); `tests/WSGM.Plugin.NvidiaGpu.Tests/NvColorTests.cs`
  (`ActionAdmissionRequiresNullInsteadOfAValue`); `DriverRuntimeTests.cs` only in the NVIDIA project;
  `tests/WSGM.Plugin.Ir.Tests/IrEndpointConnectionTests.cs:151`.
- **Problem:** Several Intel tests restate the production tables they test, so they cannot fail for a real change. An
  NVIDIA colour test actually tests `DriverControl.Accepts`. No test drives the runtime with an Intel session, there is
  no `IntelGpuPlugin` lifecycle test, no `ColorPipeline` test, no unreadable-record test and no `DriverStateFile` absent
  versus unreadable test. One IR test enshrines GPUIR-013.
- **Best solution:** Replace, do not add beside:
  - Delete the three Intel predicate copies and replace them with behaviour through the real owners: the
    `IgclDriverSession.Discover` filter hides a control whose first read is an explicit unsupported-feature result and
    keeps one whose read failed otherwise; Arc Sync custom is published for a fake range that allows it and not for one
    that does not.
  - Move `ActionAdmissionRequiresNullInsteadOfAValue` into `DriverRuntimeTests` as an `Accepts` test.
  - Keep one `DriverRuntimeTests` file. Since B150 links the runtime into Intel too, host it in the NVIDIA project as
    today; do not duplicate it per vendor.
  - The missing cases are added by the findings that need them: GPUIR-002/003/V-002 (state files), GPUIR-010 (colour),
    GPUIR-007 (adapter mapping, backoff, health, persisted ids), GPUIR-013 (IR assertion).
- **Tests:** Filters: `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`,
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B153 (Intel replacements), B151 (runtime test moves), B154 (IR assertion).
- **Related:** plan claim C7, Z03 (B178) audit of never-named test files.

### GPUIR-040: GpuCoordinator._syncRevision is a process-static counter

- **Severity:** low (cross-domain; owned by the Shell/SDK batches)
- **Where:** `src/WSGM/Shell/GpuCoordinator.cs:66-70`, increment at `:593`.
- **Problem:** The per-application sync revision is a `static long`, shared by every `GpuCoordinator` instance in the
  process and by tests. Plugins compare revisions (`NvProfiles` `<=`, Intel `<`), so a counter shared across instances or
  test cases couples them.
- **Best solution:** Make it an instance field `private long _syncRevision;`; `Interlocked.Increment(ref _syncRevision)`
  stays. Within one session (one coordinator) the guarantee is the same: `ShellSession` creates one `GpuCoordinator`
  and the `CommonPluginManager` that starts the graphics plugins beside it (`ShellSession.cs:298-301`), so a plugin
  stopped and started again within the process still talks to the same coordinator. Rewrite the field's summary, which
  today justifies the static with "shared by every publisher and every open of one", to say the revision belongs to the
  coordinator that owns those plugins for the session.
- **Tests:** `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GpuCoordinator|FullyQualifiedName~PluginCapabilityChannel"`.
- **Plan v2:** B148.
- **Related:** SDK-022, GPUIR-038 (revision guard).

### GPUIR-V-004: IR reports local pre-emission failures as Unconfirmed, so route compensation runs for actions that never left the host

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/IrPlugin.cs:463-472` (catch mapping), `:700-716` (`Target()` throws
  `InvalidOperationException`), `:804-813` (`IntegerArgument` range errors; `remote-press` validates `delay-ms` before
  emitting at `:595`), `:431-434` (import without a backup), `EndpointAsync` (`:532-543`, link open and identify);
  `src/WSGM/Shell/PluginActionSequence.cs:25,73-76`.
- **Problem:** Every exception other than `IrRejectedException` becomes `Unconfirmed`, including failures that happen
  before any request frame is written: no port configured, no pairing, an out-of-range argument, a missing backup, a
  link that will not open or an identify that fails. `MayHaveActed` then marks them possibly acted and leave-time
  compensation runs.
- **Best solution:** Raise `IrRejectedException` at the pre-emission sites: `Target()` (both messages unchanged),
  `IntegerArgument` range errors, the missing-backup check, and in `EndpointAsync` wrap link open and identify in
  `try/catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)` rethrowing
  `IrRejectedException("The IR endpoint could not be reached: " + ex.Message)`; identify never emits IR. A failure after
  an emitting request frame was written keeps its current classification. The existing `emitted` flag and the
  `catch (IrRejectedException) when (!emitted)` filter already handle scenes whose later step fails; no new state.
- **Tests:** `IrRemoteActionTests`: no port configured -> `Rejected`; `delay-ms` out of range -> `Rejected` and no frame
  written; a scripted link whose open throws -> `Rejected`; a scene whose second step fails after the first emitted ->
  `Unconfirmed`. Filter: `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`.
- **Plan v2:** B154.
- **Related:** GPUIR-013.

### GPUIR-V-005: A host refusal of the post-write publish turns a successful GPU write into Indeterminate

- **Severity:** low
- **Where:** `src/Shared/Gpu/DriverRuntime.cs:173-176` (`PublishValueAsync` after the write, outside any catch);
  router catch in `DeviceCapabilityRouter.ExecuteAsync` (maps an exception to `Uncertain`); Intel's containment at
  `IntelGpuPlugin.cs:662-667`.
- **Problem:** If `PublishCapabilityStateAsync` throws after the driver accepted the write, the exception leaves
  `ExecuteCommandAsync` and the router reports the applied write as uncertain.
- **Best solution:** Make `PublishValueAsync` contain host refusals for every caller, as Intel does: wrap the
  `PublishCapabilityStateAsync` call in `catch (Exception error) when (error is not OperationCanceledException and not
  OutOfMemoryException)`, remove the `_published` entry so the next pass publishes again, and `TraceChange(Warn,
  "publish/<key>", "WSGM refused the state: ...")`. The command then returns the write's own outcome. This also keeps a
  refused state from faulting an observation pass (today the pass's outer catch would close the session and publish
  Unavailable). `PublishModelAsync` gets the same containment around `PublishDescriptorsAsync`, again as Intel does
  today (`IntelGpuPlugin.cs:479-506`, where a refused set leaves the fingerprint unset and is published again on the
  next pass): on a host refusal it keeps `_model`, leaves `_fingerprint` unchanged and traces the refusal once through
  `TraceChange`, so the session stays open. Without this, B153 would turn a descriptor set WSGM rejects into an Intel
  session close, a `ControlLib.dll` free and a reopen every 10 s.
- **Tests:** `DriverRuntimeTests`: a fake `ICapabilityHost` whose `PublishCapabilityStateAsync` throws; the command
  returns `AppliedVerified`/`AppliedUnverified` and the session stays open; the next pass publishes the state again.
  A host whose `PublishDescriptorsAsync` throws once: the session is not disposed and the next pass publishes the set.
  Filter:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`.
- **Plan v2:** B151.
- **Related:** GPUIR-007.

### GPUIR-V-006: Intel per-app record save failures are swallowed

- **Severity:** low
- **Where:** `src/WSGM.Plugin.IntelGpu/StateFile.cs:55-58` (logs and returns),
  `Profiles/ApplicationProfileSynchronizer.cs:253-263,527-531`.
- **Problem:** When the record cannot be saved, the synchronizer still counts the write, keeps the value names only in
  memory and returns success. After a restart the old record loads, and the new `<exe>_<Setting>` registry values have no
  owner that IGCL could ever delete: GPUIR-002's orphaning by another route.
- **Best solution:** With B150, `Save` calls `DriverStateFile.Write`, which throws, and the synchronizer saves in four
  places (switch confirmed `:200`, switch written `:215`, feature write `:262`, each removal `:506`), so the handling
  lives in `Save` itself rather than around one call. `Save(entries)` sets `Record` in memory (so this session can still
  remove the values), returns null when the file was written or there is no state directory, and on
  `IOException`/`UnauthorizedAccessException` logs one warning and returns the reason. Each call site turns a returned
  reason into failures for the entries that save was recording:
  - switch or feature write (`:215`, `:262`): those items are not counted as written and each gets
    `ApplicationProfileFailure(profileId, executable, capabilityId, "Applied to the driver, but WSGM could not record it
    (<reason>); remove it in Intel Graphics Software if it stays after a restart.")`;
  - switch confirmed (`:200`): the same failure for the switch entry, so a second sync never reports success while the
    record is still missing on disk;
  - removal (`:506`): one failure for that entry, "Removed from the driver, but WSGM could not update its record
    (<reason>)." The stale record entry is harmless: its names are gone and `DeleteValue(name, false)` ignores a missing
    value on the next removal.

  The driver write is done and is never repeated; the next sync saves the file again, which is a file write, not a
  device write.
- **Tests:** `ApplicationProfileSynchronizerTests`: the record path made a read-only file after construction; `Apply`
  reports one failure per item for that executable, the fake registry holds the value once, and a second `Apply` with the
  same request writes the registry no further and reports the failure again; after the file becomes writable a third
  `Apply` reports success and the record holds the names. Filter:
  `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter "FullyQualifiedName~ApplicationProfileSynchronizerTests"`.
- **Plan v2:** B150.
- **Related:** GPUIR-002, GPUIR-021.

### GPUIR-037: Documentation drift in the GPU and IR packages

- **Severity:** nit
- **Where:** `src/WSGM.Plugin.NvidiaGpu/README.md` (Controls says "No setting is written merely to discover support",
  Lifetime says it "probes their setters with unchanged native state"); `src/WSGM.Plugin.IntelGpu/README.md` (support
  discovery under `## Build`); `src/WSGM.Plugin.IntelGpu/Display/DisplayIdentity.cs:9` (doc says "at most 48
  characters", nothing truncates); `src/WSGM.Plugin.Ir/README.md` ("no Device SDK dependency");
  `Profiles/ApplicationProfileSynchronizer.cs:500-501` and `Graphics/AdapterControls.cs:62-65` (any failure blamed on
  "WSGM needs elevation", including an out-of-range value).
- **Problem:** Docs and messages contradict the code, and the elevation message misleads users about the cause.
- **Best solution:** In B177: NVIDIA README Controls says setters are probed once with the unchanged native value; move
  Intel's support-discovery text out of `## Build`; drop the 48-character sentence from the `DisplayIdentity` doc; fix the
  IR README dependency sentence (covered by A02-F002/SET_DOC); also describe the B150 to B156 behaviour changes
  (unreadable record keeps controls and fails sync, refusals before emission are Rejected, the NVIDIA journal has no
  unconfirmed entries any more, firmware 0.5.0 with protocol 2, paged catalog and `storage-failed`). The two "needs
  elevation" messages are code: change them to name the registry error (access denied keeps the elevation hint, other
  errors print the exception text) in the batch that touches the file (B150 for the synchronizer, B153 for
  `AdapterControls`). AGENTS.md changes (Intel "Stop and dispose stay bounded", "reopen less often", per-plugin lane
  wording) are approved under D4: show each diff with its batch and apply it, then run `eng/check-agent-guidance.ps1`;
  `npm run format` on touched Markdown.
- **Tests:** `npm run format:check`; `./eng/check-agent-guidance.ps1`.
- **Plan v2:** B177 (docs), B150/B153 (the two messages).
- **Related:** A02-F002, D4 (decided: approved, diffs shown with their batch).

### GPUIR-038: Small inconsistencies in GPU and IR code

- **Severity:** nit
- **Where:** `src/WSGM.Plugin.IntelGpu/Profiles/ApplicationProfileSynchronizer.cs:163` (`<`) versus
  `src/WSGM.Plugin.NvidiaGpu/NvProfiles.cs:63` (`<=`); `Graphics/ThreeDFeature.cs:361,562` (live FPS clamped at 1000);
  `src/WSGM.Plugin.AmdGpu/AdlxSession.cs:120-128` (unreachable `pnp.Length == 0` after `Contains("VEN_1002")`);
  `src/WSGM.Plugin.Ir/IrPlugin.cs:885-891` (`carrier-hz` always published with `PluginStateOrigin.Initialization`),
  `:370-386` (`set-carrier` re-implements `ReplaceCommandAsync`); `IrEndpoint.cs:170-173` (`TcpIrLink` sync-over-async
  inside `Task.Run`).
- **Problem:** Minor drift: two revision guards that differ, a status value silently truncated, dead code, a wrong state
  origin after actions, a duplicated helper.
- **Best solution:**
  - Revision guard: Intel uses `sync.Revision <= _appliedRevision` like NVIDIA (the coordinator never repeats a
    revision). Plan v2 assigns this to B152, but B152 edits no Intel file, so make the one-operator change in B150,
    which already edits the synchronizer.
  - Live FPS: stop clamping; when `TargetFps` exceeds the descriptor maximum (1000), publish the value as unknown
    (`null`) rather than a false 1000. The descriptor stays as it is, so the row looks the same for every real value.
    B153.
  - AMD: delete the unreachable `pnp.Length == 0` branch. B152.
  - IR `carrier-hz`: pass the operation id into the carrier publish and use `PluginStateOrigin.Action` when it is
    present, `Initialization` otherwise, the same rule `Publish` already applies (`:901`). B154.
  - IR `set-carrier`: call `ReplaceCommandAsync` with the edited command instead of the inline copy. B154.
  - `TcpIrLink` connect: acceptable as is (it runs on a pool thread inside `Task.Run`); no change.
- **Tests:** Existing suites; add an Intel synchronizer case that a repeated revision is skipped. Filters: the Intel,
  AMD and IR project test commands above.
- **Plan v2:** B150 (Intel revision guard, see above), B152 (AMD branch; B152 is the batch whose resolves list names
  GPUIR-038), B153 (FPS), B154 (IR items). B150, B153 and B154 do not list GPUIR-038, so each must carry its item
  explicitly.
- **Related:** GPUIR-040.

### GPUIR-V-007: ADL allocation callback caps requests at 64 MiB

- **Severity:** nit
- **Where:** `src/WSGM.Plugin.AmdGpu/AdlDitherApi.cs:77-85` (`Allocate`).
- **Problem:** The callback returns null for any request above 64 MiB, which makes ADL fail: an arbitrary limit.
- **Best solution:** `return size > 0 ? Marshal.AllocCoTaskMem(size) : 0;` keeping the `try/catch` that returns 0 on an
  allocation failure (an `UnmanagedCallersOnly` callback must not throw).
- **Tests:** `dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj`.
- **Plan v2:** B152.
- **Related:** GPUIR-020, rule "no arbitrary limits".

---

## Structural batch without a finding: B155 IR split

B155 moves code out of `src/WSGM.Plugin.Ir/IrPlugin.cs` with no behaviour change; action ids, labels, setting keys and
publication keys stay identical. Target owners:

| Old symbol | New owner |
| --- | --- |
| `Settings`, `Actions`, `Widgets`, `Contributions`, `Text()`, `ConfigureAsync` validation | `IrPlugin` (unchanged order and text) |
| `_endpoint`, `_createEndpoint`, `_port`, `_transport`, `_hostName`, `_pairing`, `Target`, `EndpointAsync`, `PairAsync`, `_remotes`, `RefreshRemotesAsync`, `Find` | new `IrEndpointSession.cs` (fresh Wi-Fi link per action, identify before use, remote catalog cache with one refresh on a miss) |
| `_library`, `SaveLibraryAsync`, `ReplaceCommandAsync`, `ResolveCommand`, `ResolveScene`, `LibraryPath`, `PairingPath`, `IrLibrary.LoadAsync/SaveAsync`, `IrPairing.LoadAsync/SaveAsync`, `JsonFile` | new `IrStore.cs` (validation records stay in `IrPayload.cs`; `IrPlugin` holds the current `IrLibrary` value) |
| `Publish`, `PublishNetwork`, `PublishLibrary`, `PublishRemotes`, `Describe`, `_sequence` | new `IrStatusPublisher.cs` |
| `RemoteActionAsync`, `WaitForSequenceAsync`, `LearnAsync`, `SendAsync`, `_lane`, `_stopped`, `_context`, `_host`, `ExecuteActionAsync`, `IntegerArgument` | `IrPlugin` (no lifetime CTS: GPUIR-014 is refuted; `SequencePollLimit` is already gone after B154, GPUIR-029) |
| `_lastCommand` | `IrPlugin`, beside the current `IrLibrary` value it shadows (GPUIR-028 is no-change); `ResolveCommand` and `PublishLibrary` receive it as a parameter for `selected`/`last` and the selected-command status |

Test: the existing IR suite unchanged plus one fixture that pins the published state keys and texts after start, learn
and select. Filter: `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`. Account for every symbol in
the table when the batch ends (CLAUDE.md: a dissolved file is where losses hide).

## Refuted or no-change

- **GPUIR-009** (`DriverRuntime.SyncApplicationProfilesAsync` returns success when not running): refuted. When the session
  is lost the runtime has already published an empty descriptor set, so `GpuCoordinator.SyncAsync` never calls the
  plugin, and a reopen or new cycle sets `_syncRequired`; the worst case is a misleading log line. No "sync when not
  running" step in B151.
- **GPUIR-014** (IR stop and suspend cannot preempt a running action; add a plugin lifetime CTS): refuted.
  `PluginHost.StopAsync` cancels the active action budget first and the host's lifecycle lane already orders stop after
  the action; a lifetime CTS would add mechanism without a defect.
- **GPUIR-016** (AMD process-static interface epoch): no change. ADLX initialization and termination are process-wide and
  only one `AdlxSession` exists at a time, so the static epoch matches the native scope; `DisableParallelization` in the
  AMD tests stays.
- **GPUIR-025** (Intel reopen backoff is mechanism without a defect): no change. The backoff stays (in the B153
  forwarder) so a refused `ctlInit` does not become a `ControlLib.dll` load and free every 10 s once the library is
  loaded per session.
- **GPUIR-027** (Intel duplicates WDC's CCD display identity): accepted duplication. Intel keeps its own CCD query and
  GPUIR-B5 is skipped (critic conflict 11; WDC rules it an accepted duplication).
- **GPUIR-028** (IR `_lastCommand` mirrors `IrLibrary.SelectedCommandId`): no change. The two fields are not one fact.
  `_lastCommand` holds two session-only states the library deliberately does not persist: the start fallback to the
  last command when nothing is selected (`IrPlugin.cs:109`, not written to `library.json`), and "nothing selected"
  after the selected command is deleted (`:418-421` set it to `""` while the saved library has `SelectedCommandId =
  null`). Folding them into `SelectedCommandId ?? Commands.LastOrDefault()` changes what `send selected` emits after a
  delete: today it fails until the user selects again, afterwards it would transmit whichever command is last, which is
  an emitting workflow change; keeping the old behaviour instead would need the fallback written into `library.json`.
  Plan v2's B154 step "delete _lastCommand (028)" is dropped; the B155 table keeps the field in `IrPlugin`.
- **GPUIR-034** (Intel loads `ControlLib.dll` by bare name, so a planted DLL in an earlier search directory would load
  into the elevated process; plan v2 B153, plan claim C6): Dropped by maintainer decision (security theater,
  DECISIONS.md). There is no functional part: with the driver installed the bare name finds its System32 library, and a
  missing library keeps today's "control library is not installed" health. `IgclDriverSession` (B153,
  GPUIR-007) loads the library as today.
- **GPUIR-035** (lifecycle fields written outside the lane): the IR half is refuted (`PluginHost._lifecycle` already
  serializes `ResumeAsync`, `SessionChangedAsync` and actions); the GPU half is a single reference swap of an immutable
  `PluginContext`, which is atomic and benign.
- **GPUIR-039** (Intel publishes its driver frame-rate limiter while NVIDIA and AMD omit theirs): no change. Removing
  Intel feature 2 would remove a visible control (requirement 9); it stays a product question for later, not part of
  this refactor.
