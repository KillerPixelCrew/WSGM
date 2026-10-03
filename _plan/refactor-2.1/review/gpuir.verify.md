# GPU / IR review: adversarial verification

Verifies `_plan/refactor-2.1/review/gpuir.md` against master `1329813f`. Read-only; nothing built or run. Every high/medium
finding, every claim marked partially/inaccurate, and every batch was checked against the cited code and its
callers (`DeviceCapabilityRouter`, `PluginCapabilityChannel`, `GpuCoordinator`, `PluginHost`, `CommonPluginManager`,
`CommonPluginActions`, `PluginActionSequence`, `GraphicsOverlaySource`, `OverlayWindow.Graphics`).

## Refuted

- **GPUIR-009** (`DriverRuntime.SyncApplicationProfilesAsync` returns `(0,0,[])` when not running). The code returns
  that, but the claimed consequence ("the set is skipped until it changes or a game starts") does not occur. When the
  session is lost or failed to open, the runtime has already published an empty descriptor set
  (`DriverRuntime.cs:136,163,207,424`), so `GpuCoordinator.SyncAsync` exits before calling the plugin because no
  `NativePerApplication` descriptor is left (`GpuCoordinator.cs:568-572`). When the session reopens, the republished set
  goes through `OnDescriptorsAccepted`, which sets `_syncRequired = true` (`GpuCoordinator.cs:640-647`). A cycle
  mismatch goes through `OnCycleStarted`, which also sets `_syncRequired` (`:631-638`). `_running == false` means
  admission is already closed (`PluginHost.StopAsync` → `capabilities?.Suspend()`). The worst case is a misleading
  log line, not a skipped sync. Drop B2 step 4, or keep it as a Nit consistency change only.
- **GPUIR-014** (IR stop and suspend cannot preempt a running action; add a plugin lifetime CTS). The host already
  does this. `PluginHost.InvokeActionAsync` runs every action through `RunAsync`, which serializes on `_lifecycle` and
  records the action's budget as `_activeCancellation` (`PluginHost.cs:336-350,548-577`). `PluginHost.StopAsync`
  cancels that active budget first (`:451-457`), so a scene `Task.Delay` or sequence wait ends at once, and the
  sequence wait's existing catch sends its one `cancel`. `IrPlugin.StopAsync` can never run while an action holds the
  plugin lane, because the host's lifecycle lane already orders them. Every action also ends at its own deadline
  (`RunAsync` budget; route steps clamp to 120 s, `PluginActionSequence.cs:110`), so the "ten minutes" wait cannot
  outlive it. Suspend waits behind the action but within its deadline. A plugin-owned lifetime CTS adds mechanism
  without a defect, which breaks the simplify rule. Drop it from B6.

## Corrected

- **GPUIR-003**: downgrade to Low and limit it to the 4 MiB size cap. The data-loss path is mostly wrong. On Windows
  `File.Exists` is true for a locked or ACL-denied file (attributes stay queryable), and `File.ReadAllBytes` then
  throws `IOException` or `UnauthorizedAccessException`. `DriverStateFile.Read` does not catch either, so `NvProfiles`
  and `NvSession` construction fail closed and nothing is rewritten (`DriverStateFile.cs:12-23`,
  `NvSession.cs:23-31`). A directory at the path makes `Exists` false, but the later `File.Move` onto a directory
  throws, so the "original" cannot be replaced either. What remains is the arbitrary size cap. The real cost of fail
  closed is GPUIR-V-002.
- **GPUIR-004** (IR firmware learn reply retargeting): downgrade to Medium. The defect is confirmed
  (`main.cpp:450,584-585`), but the leaked data is one captured IR timing frame, tagged with an id the new host does not
  match (`IrEndpoint.cs:461-465`). The firmware's own trust model already lets any LAN client displace the paired host
  ("newest connection wins"). The fix as written is right: cancel the learn before `client.stop()`.
- **GPUIR-005** (Intel has no pre-setter admission recheck): downgrade to Low. Intel checks the deadline and then
  writes synchronously in the same call, with no await between admission and the setter (`IntelGpuPlugin.cs:96-117`).
  The only window is the few preparatory IGCL reads. That is not a cancellable gap like NVIDIA/AMD's `Task.Run`
  hop. Threading `WriteAdmission` through five Intel entry points is mechanism for uniformity, not for a defect.
  Do it only if Intel moves onto `DriverRuntime` (B4), where the hop appears.
- **GPUIR-008** (every runtime refusal is `TransportFaulted`): downgrade to Low. Router preflight already rejects
  stale cycles, generations and invalid values with the right codes before the plugin sees them
  (`PluginCapabilityChannel.cs:197-206`, `DeviceCapabilityRouter.cs:443-490`). Only races reach the runtime's codes.
  Still worth fixing with `CommandResults` because the overlay maps `TransportFaulted` to Faulted
  (`DeviceOverlayBridge.cs:1391-1394`).
- **GPUIR-016** (AMD process-static epoch): downgrade to Low. ADLX initialization and termination are process-wide.
  The constructor comment says an already-initialized ADLX owner must not be terminated, so a static epoch matches
  the native scope, and only one `AdlxSession` exists at a time. The per-session `AdlxEpoch` changes no behaviour.
  It is a test-isolation nicety and optional.
- **GPUIR-017** (NVIDIA and AMD rediscover every 10 s): the cost is real, but the recommendation silently drops
  behaviour. Rediscovery is how NVIDIA adds the driver's current value to a setting's choices when an external editor
  set an unlisted value (`NvSession.cs:61-65`), and how AMD refreshes `GetRange` bounds and per-feature `supported`
  flags that change with display mode (`AdlxSession.Graphics.cs:154-213`, `AdlxSession.Display.cs:123-192`).
  Discovering "only on topology change" freezes both, so an externally set NVIDIA value becomes an out-of-descriptor
  state. Simpler fix: cache `NvApi.Values(id)` and `SettingIds()` per session (the driver's enumeration table does not
  change within a session) and keep the per-pass rediscovery. The large-object churn goes away with no behaviour
  change. GPUIR-V-003 describes the bigger NVIDIA cost.
- **GPUIR-019** (health republished every pass): understated. Besides `GpuCoordinator.Changed`, every publication
  raises `CommonPluginSteamUiSource.OnHealthChanged` → `OnChanged` → `_revision++` (`CommonPluginSteamUiSource.cs:431-445`),
  which republishes the Steam UI plugin model every 10 s for NVIDIA and AMD. The fix is unchanged: dedupe in
  `PluginHost.Publish`.
- **GPUIR-035** (lifecycle fields written outside the lane): the IR part is refuted. `ResumeAsync`, `SessionChangedAsync`
  and actions are serialized by `PluginHost._lifecycle` (`PluginHost.cs:371-400,433-448`). The GPU part is benign
  because a single reference swap of an immutable `PluginContext` is atomic. Nit at most; drop it from B6.
- **C3 / refinement 7** ("move the written-value overlay into `DriverRuntime` for all vendors"): rejected as a plan
  change. The product rule is to publish the written value as observed on a successful write, and `DriverRuntime`
  already does that (`DriverRuntime.cs:173-176`). Intel's overlay goes further: it masks later readbacks that equal the
  post-write readback (`IntelGpuPlugin.cs:571-583`). No NVIDIA/AMD defect is cited that needs it. Adding it there
  changes the values the UI shows for those vendors, which conflicts with "UI identical", and adds mechanism. Keep the
  overlay inside the Intel adapter (`IgclDriverControl`) if B4 lands.
- **GPUIR-013**: the finding stands, but its scope is too narrow. See GPUIR-V-004 for local pre-emission failures that
  are also reported as `Unconfirmed`. The impact is concrete: `PluginActionStepResult.MayHaveActed` treats every
  non-Rejected outcome as possibly acted, so route compensation runs (`PluginActionSequence.cs:25,73-76`).

## Confirmed (ids)

GPUIR-001, GPUIR-002, GPUIR-006, GPUIR-007, GPUIR-010 (PLAUSIBLE, as the reviewer marked it), GPUIR-011, GPUIR-012, GPUIR-013,
GPUIR-015, GPUIR-018, GPUIR-020, GPUIR-021, GPUIR-022, GPUIR-023, GPUIR-024, GPUIR-025, GPUIR-026, GPUIR-027, GPUIR-028,
GPUIR-029, GPUIR-030, GPUIR-031, GPUIR-032, GPUIR-033, GPUIR-034, GPUIR-036, GPUIR-037, GPUIR-038, GPUIR-039, GPUIR-040;
plan claims C1, C2, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13, C14, C15, C16, C17, C18, C19, C20, C21, C22, C23, C24.

Notes on confirmations:

- GPUIR-001: the chain from `OverlayWindow.Graphics.cs:291` through `GraphicsOverlaySource` → `GpuCoordinator.ExecuteAsync:316`
  → `DeviceCapabilityRouter.ExecuteAsync` (`commandGate.WaitAsync`, synchronous when free) →
  `PluginCapabilityChannel.ExecuteCommandAsync:222` → `IntelGpuPlugin.ExecuteCommandAsync` has no hop. `ctlSet*` and the
  readback run on the dispatcher whenever the lane is free.
- GPUIR-002: `StateFile.TryLoad` catches `IOException`, `UnauthorizedAccessException` and `JsonException` and returns null.
  The synchronizer then saves over the file on the first wanted entry (`ApplicationProfileSynchronizer.cs:147,527-531`).
  `StateFile.Save` writes without a flush (`StateFile.cs:51-53`), so a power loss mid-save can produce exactly the corrupt
  file that triggers this. That is a realistic case on a handheld.
- GPUIR-012: `NvProfiles.cs:246-257` and `:179-187`. Gating reported success on DRS readback contradicts the recorded
  "never gate success on readback". With the fix, a later mismatch surfaces as the "external editor" message on the next
  sync rather than as a failure on this one. Name that in the B3 test.
- GPUIR-015: `PluginHost.RunAsync` sets `Context.Deadline` before `StopAsync`, so the recommendation works for stop. See
  the batch problems for dispose.

## Missed findings

### GPUIR-V-001 (Medium) Shared runtime hides a control for the plugin lifetime after one transient read failure

`src/Shared/Gpu/DriverRuntime.cs:463-476,524-529`. `CheckSupport` stores any read exception in `_supportResults` under the
control's `SupportKey`, and the final filter drops every control with a non-null entry. `_supportResults` is never cleared:
not on driver loss and reopen, and not on suspend/resume, since `SuspendAsync`/`ResumeAsync` reuse the same instance
(`DriverRuntime.cs:275-283`). So one transient read failure at first discovery hides the control until WSGM restarts. Two
examples: an output asleep at start (`NvApi.Color` failing), or a busy ADLX call. On NVIDIA it hides every control that
shares the key: all six colour fields (`"color/"+instance`) and all three dithering fields (`NvOutputControls.cs:78,119`).
Reads are safe to repeat. The "never retry" rule covers uncertain writes, not reads, and gating a control's existence on a
read result goes against "never gate a control on readback". Intel today hides only explicit unsupported-feature results
(`IntelGpuPlugin.cs:610-622`). B4 as written ("keep only the runtime cache keyed by SupportKey") would import this
regression into Intel.
Recommendation: never record a read failure in `_supportResults`. Skip that control in this pass and read it again at the
next discovery. Cache only probe (write) outcomes. Test: a fake session whose first `Read` throws publishes the control on
the second pass, and the probe runs exactly once.

### GPUIR-V-002 (Medium) An unreadable NVIDIA per-app journal takes the whole NVIDIA plugin down and reinitializes NVAPI every 10 s

`NvSession.cs:19-32`, `NvProfiles.cs:53-58`, `DriverRuntime.cs:399,421-426`. A corrupt or locked
`nvidia-profiles.v1.json` makes the `NvSession` constructor throw (`NvProfilesTests.CorruptJournalAbortsWithoutReplacingIt`).
`ObserveAsync` then publishes an empty model and Unavailable, which retracts every global DRS, colour, dithering, HDR and
G-SYNC control because of a per-app bookkeeping file. Every 10 s pass repeats the whole sequence: `nvapi64.dll` load,
`NvAPI_Initialize`, DRS session, full `LoadSettings` and teardown. GPUIR-002's own recommendation for Intel is the
proportionate one: per-app sync fails and the controls keep working. B1 applies a different rule to NVIDIA.
Recommendation: `NvSession` loads the journal and, when it is unreadable, keeps the session and controls. `Sync` then
returns one failure per requested executable ("per-application record unreadable; nothing changed") and never writes the
file. Same rule for Intel and IR (B1/B6). Test: corrupt journal → global controls still published; sync returns failures;
bytes unchanged.

### GPUIR-V-003 (Medium, PLAUSIBLE on cost) NVIDIA reloads the DRS database once per control per observation

`NvSession.cs:265-269` (`NvSettingControl.Read` → `_api.Load()`), `NvOutputControls.cs:247-251` (`NvGsyncControl.Read`), plus
the load in `Discover` (`NvSession.cs:37`). With about 40 curated settings, each 10 s pass makes about 40
`NvAPI_DRS_LoadSettings` calls. That call re-reads the whole driver profile store, so its cost exceeds the
`Values()` buffers that GPUIR-017 counts. It is not a per-sample path, but it is the dominant steady driver load of the
NVIDIA package.
Recommendation: one `Load()` per pass. NVIDIA implements the `BeginPass` hook that B2 already adds. Reads call `Get` against
the loaded state. Writes and probes keep their own Load → Set → Save → Load bracket. Fold this into B3.

### GPUIR-V-004 (Low) IR reports local pre-emission failures as Unconfirmed, so route compensation runs for actions that never left the host

`IrPlugin.cs:468-472` turns every non-`IrRejectedException` into `Unconfirmed`. That includes `Target()` with no port
configured or no pairing (`:700-716`), `IntegerArgument` range errors (`:804-813`; `remote-press` validates `delay-ms` before
emitting, `:595`), import without a backup (`:431-434`), and link-open/identify failures that occur before any request
frame is written. `PluginActionStepResult.MayHaveActed` then marks them as possibly acted, so leave-time compensation is
owed (`PluginActionSequence.cs:25,73-76`).
Recommendation: as part of GPUIR-013, raise `IrRejectedException` from `Target()`, `IntegerArgument` and the missing
backup check. Classify failures before the endpoint's request write as Rejected. Only a failure after the frame was
written stays `Unconfirmed`. No new state is needed: the existing `emitted` flag plus the throw site is enough.

### GPUIR-V-005 (Low) A host refusal of the post-write state publish turns a successful GPU write into Indeterminate

`DriverRuntime.cs:174-175` awaits `PublishValueAsync(..., CancellationToken.None)` outside any catch after the driver
accepted the write. If `PublishCapabilityStateAsync` throws, the exception leaves `ExecuteCommandAsync`, and the router
reports `Uncertain` (`DeviceCapabilityRouter.cs` catch → `Uncertain(command, ex.Message)`) for a write that applied. Intel
contains the same failure (`IntelGpuPlugin.cs:662-667`).
Recommendation: catch and trace around the post-write publish, then return the write's own outcome. This belongs in B2
step 7 ("publish failures contained"); make that step name the command path explicitly.

### GPUIR-V-006 (Low) Intel per-app record save failures are swallowed, so applied overrides are reported without durable ownership

`StateFile.Save` logs and returns on `IOException`/`UnauthorizedAccessException` (`StateFile.cs:55-58`). The synchronizer
still counts the write, keeps the names only in memory and returns success (`ApplicationProfileSynchronizer.cs:253-263,527-531`).
After a restart the old record loads and the new `<exe>_<Setting>` registry values have no owner, which IGCL cannot delete.
That is GPUIR-002's orphaning by another route.
Recommendation: when B1 moves Intel onto `DriverStateFile.Write` (which throws), make a failed record save a sync failure
for that executable. The driver write stays done and is not retried; only the reported outcome changes.

### GPUIR-V-007 (Nit) ADL allocation callback caps requests at 64 MiB

`AdlDitherApi.cs:81` returns null for any request above 64 MiB, which makes ADL fail. It is an arbitrary limit (no-arbitrary-limits).
Recommendation: allocate what ADL asks for (`size > 0`).

## Batch problems

1. **B2 depends on B1 but says it has no dependency.** B2 moves `IntelLog` to `src/Shared/Gpu/DriverLog.cs` and renames the
   Intel call sites. Intel only compiles `Shared/Gpu` after B1 step 2, so B2 alone does not build.
2. **B2 step 5 (written-value overlay for all vendors)** adds mechanism to NVIDIA and AMD without a cited defect and changes the
   values the UI shows for them. Remove it and keep the overlay in the Intel adapter (see C3 above).
3. **B2 step 6 / GPUIR-017** silently drops behaviour: NVIDIA's current-value choice injection and AMD range/support refresh.
   Replace it with per-session caching of `Values`/`SettingIds` and one DRS load per pass (GPUIR-V-003).
4. **B2 step 7, dispose**: `DisposeAsync()` receives no context. The runtime's cached `_context.Deadline` is whatever the last
   lifecycle call carried, possibly already expired or `Never`. `PluginHost.DisposeAsync` runs under its own 5 s
   `RunAsync` deadline but cannot pass it in (`PluginHost.cs:489-511`). So "honour `context.Deadline`" is impossible for
   dispose. Dispose should start or join retirement and return without waiting (the retiring task keeps the lane and the
   DLL), not read a stale deadline. Stop can honour the token as proposed.
5. **B2 step 4 (GPUIR-009)** is not needed (refuted). If it is kept, the coordinator logs a "refused" warning for every
   sync that races a close. Harmless, but noise.
6. **B4 behaviour drops not listed:**
   - (a) Adopting `DriverRuntime.CheckSupport` unchanged replaces Intel's "hide only explicit unsupported results" with
     "hide on any read failure, forever" (GPUIR-V-001). Fix V-001 first or keep Intel's rule.
   - (b) Intel reports `PluginHealth.Failed` for open and build failures (`IntelGpuPlugin.cs:378,427`), while the runtime
     reports `Unavailable`. Health shows in Settings, so name the mapping.
   - (c) Intel publishes `Verified` quality after a matching readback (`:144-146`) and the runtime publishes `Observed`.
   - (d) Deleting `TraceDue` brings back per-pass string formatting that `IntelControl.cs:266-284` exists to avoid.
     `TraceChange` dedupes the line, not the formatting. Keep it, or keep per-value tracing out of the runtime pass.
   - (e) `ControlLib.dll` moves from one load per plugin lifetime to one per session (the reviewer lists this as a risk).
     This also interacts with the 10 s reopen once backoff is removed (GPUIR-025): it becomes a load/free cycle every 10 s on
     a machine whose IGCL refuses `ctlInit`.
7. **B6** carries the refuted GPUIR-014 lifetime CTS and the IR half of GPUIR-035. Remove both. Add GPUIR-V-004 to the
   GPUIR-013 change.
8. **B1 test expectations**: "locked file / directory at path leaves bytes unchanged" already holds for the NVIDIA journal
   today (GPUIR-003 corrected). The B1 change for NVIDIA reduces to removing the size cap and to GPUIR-V-002 (keep controls,
   fail sync), not to fixing a replace-on-unreadable bug.
9. **B3 / GPUIR-016**: optional. Re-enabling AMD test parallelization is safe only if no test touches the native statics after
   the change. Verify `NativeContractTests` does not rely on `AdlxNative.Epoch` before removing `DisableParallelization`.
10. **B8 / F007**: a build-time refusal of a catalog whose `remotes` reply exceeds 32768 bytes respects the protocol frame
    bound, but the maintainer's "payloads are chunked" rule points to paging the catalog instead. That is a protocol
    addition, so put it to the maintainer as an open question rather than deciding it in the batch.
11. **Ordering**: C18's independence claim holds (IR shares no code with GPU or Device Lab). The remaining serialization
    between B-chains is the single-writer rule only, as the reviewer says.
