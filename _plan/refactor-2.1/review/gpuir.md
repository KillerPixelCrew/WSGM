# GPU plugins, shared GPU runtime and IR plugin review

Reviewer domain: `src/WSGM.Plugin.IntelGpu/**`, `src/WSGM.Plugin.NvidiaGpu/**`, `src/WSGM.Plugin.AmdGpu/**`,
`src/Shared/**` (only `Gpu/` exists), `src/WSGM.Plugin.Ir/**` including `Firmware/**`, and their tests.
Baseline: master `1329813f`. Read-only review; nothing was built, run or flashed.

Files read in full: all 4 `src/Shared/Gpu` sources; all 8 NVIDIA sources plus README/AGENTS/PROVENANCE/csproj/manifest;
all 6 AMD sources plus docs/csproj/manifest; all 27 Intel sources plus README/AGENTS/PROVENANCE/csproj/manifest; IR
`IrPlugin.cs`, `IrEndpoint.cs`, `IrPayload.cs`, `IrRejectedException.cs`, README, protocol.md, `.gitignore`, firmware
`main.cpp`, `platformio.ini` and the validation/limit paths of `embed_remotes.py`; test projects for all four packages
(Intel tests read for structure and the fixtures that matter, the rest in full). Callers traced outside scope:
`PluginCapabilityChannel`, `GpuCoordinator`, `DeviceCapabilityRouter.ExecuteAsync`, `GraphicsOverlaySource`,
`OverlayWindow.Graphics`, `PluginHost.Publish`, `CommonPluginManager`, `CommandResults`, WDC `DisplayTopology.Types`.

The prior Claude ledger has no entry for this domain (U16A/U16B were never started, `audit-coverage.md:41-42`). The only
existing findings are Codex A02-F015..F018 (IR host) and A02S01-F001..F014 (IR firmware). Those are cited, not re-derived;
each was checked against current code and is confirmed unless stated.

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | "GPU plugins share the existing `src/Shared/Gpu/DriverRuntime` lane/retirement/observation mechanism" (refactor-plan.md:151) | Partially accurate | Only NVIDIA and AMD link `../Shared/Gpu/*.cs` (`WSGM.Plugin.NvidiaGpu.csproj:13`, `WSGM.Plugin.AmdGpu.csproj:12`). Intel has its own 810-line lifecycle (`IntelGpuPlugin.cs:27-902`) and no link. | State it as the target. The present state is two lifecycles with different semantics (GPUIR-007). |
| C2 | "with Intel implementing an `IgclDriverSession` and control adapters rather than retaining a second plugin lifecycle loop" (refactor-plan.md:151; pre-AM01 GP01 step 1) | Accurate as direction, under-specified | `DriverControl.Read()` returns only `CapabilityValue` (`DriverRuntime.cs:30`); Intel needs availability + reason (`IntelControl.cs:48-88`, used by Arc Sync, scaling, LACE). Intel writes return `ControlWrite` (Refused/Uncertain), not exceptions. | The shared contract must grow a `DriverRead(Value, Available, Reason)` and the adapter must map `ControlWrite` to `DriverFailure(attempted)`. See section 4. |
| C3 | "retain Intel package-specific written-value/profile/color behavior" (GP01 step 1) | Partially accurate | The written-value overlay (`IntelGpuPlugin.cs:571-583`) is not Intel-specific; it implements the product rule "publish the written value as observed" that `DriverRuntime` handles differently (publishes once, next poll may revert, `DriverRuntime.cs:174,406`). | Move the overlay into the one runtime so all vendors follow one rule. Profile sync and colour stay package code. |
| C4 | "Keep IGCL count-first/type layouts, NVIDIA DRS intent journaling and inherited-value probes, AMD vtable/reference ownership and exact support/discovery logic" (refactor-plan.md:151) | Accurate | `IgclSession.cs:245-292`, `NvProfiles.cs:208-258`, `NvSession.cs:213-232`, `AdlxNative.cs:169-210`, `DriverRuntime.cs:450-531`. | None. Note: the NVIDIA journal readback currently gates reported success (GPUIR-012); the journalling itself stays. |
| C5 | "`DriverWriteScope` becomes explicit command admission passed to driver session/control methods rather than a thread-static callback" (refactor-plan.md:151; GP01 step 2) | Accurate diagnosis, incomplete scope | `[ThreadStatic] _check` at `DriverWriteScope.cs:8`; checked in `NvApi.cs:84,116,128,431`, `NvApiPrivate.cs:16,40`, `AdlxNative.cs:138,146,154`, `AdlDitherApi.cs:69`. Intel has no pre-setter recheck at all (GPUIR-005). | Include Intel setters in the explicit admission, otherwise merging Intel silently keeps writes-after-cancel. |
| C6 | "No driver DLL redistribution or live setter discovery is added" (refactor-plan.md:151) | Accurate | All three load from the installed driver. Startup support probes already write (existing, preserved per refactor-plan.md:111). | Intel loads `ControlLib.dll` by bare name (`IgclApi.cs:103`) while NVIDIA/AMD use an absolute System32 path; align (GPUIR-034). |
| C7 | "Independent package suites and native-layout checks remain mandatory; lack of AMD/other model hardware coverage is honestly recorded" (refactor-plan.md:151) | Accurate | AMD README/PROVENANCE say blind; NVIDIA README says no NVAPI scenario run. | `DriverRuntimeTests` live only in the NVIDIA test project (`tests/WSGM.Plugin.NvidiaGpu.Tests/DriverRuntimeTests.cs`); AMD's suite never exercises the runtime (GPUIR-036). |
| C8 | "Existing vendor support probes are preserved as deliberately authored initial setter checks, not expanded or repeated after uncertainty" (refactor-plan.md:111) | Accurate | `DriverRuntime.cs:458-516` caches by `SupportKey`; Intel `_supportResults` (`IntelGpuPlugin.cs:444-461`). | Intel caches the same outcome in four places (GPUIR-024); keep one. One Intel probe is defective (GPUIR-010). |
| C9 | "Write success publishes the written value as Observed even when readback is absent/mismatched ... Readback never gates supported writes or controls" (refactor-plan.md:111) | Partially accurate for this domain | Command paths comply (`DriverRuntime.cs:173-180`, `IntelGpuPlugin.cs:135-150`). Violations: Intel shared-memory probe gates control publication on registry readback (`IntelGraphicsMemoryTransport.cs:160-163`), NVIDIA profile sync reports failure on readback mismatch after a successful Save (`NvProfiles.cs:246-254,180-187`). | Add GPUIR-011 and GPUIR-012 to the plan. |
| C10 | Common GPU routers stay independent of the device router (refactor-plan.md:65) and "Common GPU/IR plugins remain admitted independently" with Device Integration off (refactor-plan.md:85) | Accurate | `GpuCoordinator.cs:55-60,130-169`; one `DeviceCapabilityRouter` per publisher. | None. |
| C11 | B3 "GPU driver's native lane: forbidden continuation = close/free that driver or issue another native write" (planning-corrections.md:51) | Accurate on retention, inaccurate on timing | Retention: `DriverRuntime.cs:285-305` retiring task; Intel `_closePending` (`IntelGpuPlugin.cs:325-335,741-761`). Timing: Intel stop has its own 5 s + 5 s budgets and the loop wait ignores cancellation (`IntelGpuPlugin.cs:39,314-318,689-690`); `DriverRuntime.DisposeAsync` uses its own 5 s (`DriverRuntime.cs:317`). | Owners must honour the host's `context.Deadline`/token, not private budgets, or the B3 cutoffs (SessionEnd: device/GPU phase ends at 3.5 s) cannot hold (GPUIR-015). |
| C12 | api-integration.md WDC 0.2.0 consumers: "Intel display/CCD integration and GPU packages where they reference WDC" | Partially accurate | Intel references no WDC (own `DisplayConfigGetDeviceInfo`, `DisplayIdentity.cs:136-137`). NVIDIA references WDC in exactly two places: `NvApi.cs:340-349` (`DisplayTopology.CaptureActive`) and `NvOutputControls.cs:197-218` (`DisplayColor.TryReadHdr/TrySetHdr`). | Name those two NVIDIA call sites as the WDC consumers; Intel becomes a consumer only if GPUIR-027 is adopted. |
| C13 | api-integration.md Device 12 / Plugin 4 group lists "Intel/NVIDIA/AMD and `src/Shared/Gpu`; IR" | Accurate | `Deadline` use: `DriverRuntime.cs:93,112,123,496`, `IntelGpuPlugin.cs:96`; IR uses `PluginContext`, `PluginActionRequest`, `PluginText`. GPU packages do not use `PluginTrace` (they trace through `ICapabilityHost`, `IntelLog.cs:9-11`). | None; F02's `PluginDiagnostics` change does not touch GPU/IR code paths except via `PluginContext`. |
| C14 | "IR separates endpoint connection/identity/framing, command library persistence, catalog cache, action execution and UI projection. Reuse current IIrEndpoint/IIrLink seams, exact protocol 1 matching ID/version/frame bounds and single lane" (refactor-plan.md:153) | Accurate | `IrPlugin.cs` mixes all five (822 lines); seams at `IrEndpoint.cs:61-97`. | None. |
| C15 | "No migration/transient publication/pin sends IR. Reconnect precedes a new explicit operation and never replays an uncertain emission" (refactor-plan.md:153) | Accurate | `IrPlugin.cs:255-259` fresh Wi-Fi link per action; `EndpointAsync` identifies first (`IrPlugin.cs:532-543`); no resend path. | None. |
| C16 | "Preserve ... host scene order/compensation" (refactor-plan.md:153) | Partially accurate | Scene step order is in `IrPlugin.cs:392-402`; entry/leave compensation lives in WSGM Core/Shell, not the IR plugin (README "Core automation"). | Say compensation is owned by the host route automation; the IR plugin only preserves step order and the Rejected/Dispatched distinction. |
| C17 | "Firmware is audited/refined only where a source-confirmed host contract defect requires it; no flashing/protocol bump is implicit" (refactor-plan.md:153) | Too narrow | A02S01-F001 (learn reply retargeted to a replacement network client, `main.cpp:450,584-585`) is a firmware security defect, not a host contract defect, and is confirmed. | Admit firmware-only defects F001-F006 to a firmware batch with build-only validation; flashing stays an explicit maintainer action; firmware version moves to 0.4.1, protocol stays 1. |
| C18 | IR01 prerequisite "Q_GP01 accepted-or-waived"; B01 prerequisite "IR01" (versions/pre-AM01/task-briefs.md:946-990) | Inaccurate dependency | IR shares no code, contract or test with the GPU packages or with Device Lab. | Make GP*, IR* and B01 independent chains; only the serialized-writer rule orders them. |
| C19 | audit-coverage: U16A/U16B "unstarted ... 0 saved IDs" | Accurate | No ledger rows for GPU files; A02 produced IR-host findings only and no `U16A-GIR.md`. | This report supplies the source closure for U16A/U16B. |
| C20 | A02_04 batch (IR UTF-8 reply bound, reply ownership) | Accurate and safe | `IrEndpoint.cs:444` counts chars; `IrEndpoint.cs:460-485` disposes only on some paths. The 32768 bound is a protocol bound (firmware `MaxFrame`, `main.cpp:19`), not an arbitrary cap. | Execute as written; fold GPUIR-013 into the same `ReadReply` extraction if both are admitted together. |
| C21 | A02-F017 "IR state loaders cannot distinguish absent from inaccessible state", NOT READY | Accurate; NOT READY is unnecessary | `IrPayload.cs:112-115,149-152` use `File.Exists`. | Same fix as GPUIR-002/003: open directly, only FileNotFound/DirectoryNotFound means absent, anything else fails Start without writing. No migration needed; file shapes unchanged. |
| C22 | A02-F018 "Serial link constructor does not release SerialPort on failed Open", NOT READY pending a "serial factory/adapter failure seam" | Accurate defect, over-engineered disposition | `IrEndpoint.cs:107-119`. `TcpIrLink` already uses try/catch-dispose (`IrEndpoint.cs:170-178`). | A three-line try/catch-dispose fixes it; no new seam (simplify rule). |
| C23 | A02S01-F001..F006 firmware findings | Accurate | Confirmed at `main.cpp:450/584-585` (F001), `72,731` (F002), `193-195` (F003), `174-180` (F004), `428-429,523-525` (F005), `718-719` (F006). | F001 needs no "connection generation": retire the learn when its client is replaced (section 3). |
| C24 | A02S01-F007 catalog can exceed the reply frame; "no arbitrary remote count or silent truncation" | Accurate | `embed_remotes.py:424-425` caps buttons at 128, `263-264` steps at 32, neither bounds the actual reply. | Replace both count caps with one exact build-time byte check of the emitted `remotes` reply against 32768 (GPUIR-031). |

## 2. Findings

Severity scale per task. "NEW" means not in the Claude ledger or Codex audits.

### GPUIR-001 (High) Intel native writes and readback run on the caller's thread, which is the Avalonia UI thread for overlay edits

`IntelGpuPlugin.cs:80-150`. After `await _lane.WaitAsync(...)` (synchronous when the lane is free) the method calls
`GuardWrite` and `GuardRead` directly, so `ctlSet*`/`ctlGet*` run inline. The overlay path is synchronous down to the
plugin: `OverlayWindow.Graphics.cs:291-308` → `GraphicsOverlaySource.cs:181` → `GpuCoordinator.cs:316` →
`DeviceCapabilityRouter.cs:261-279` → `PluginCapabilityChannel.cs:222`. `ConfigureAwait(false)` does not help before
the first real suspension. `DriverRuntime` avoids this deliberately (`DriverRuntime.cs:108-129`, "All native work stays
off it"). Arc Sync, colour LUT and LACE writes can block the UI for the duration of a driver call.
NEW. Recommendation: resolved structurally by moving Intel onto `DriverRuntime` (GPUIR-B4); until then no interim fix
(greenfield rule, do not patch code the next batch deletes).

### GPUIR-002 (High) Intel per-application record is replaced when it cannot be read, orphaning driver values for good

`ApplicationProfileSynchronizer.cs:145-148` loads with `StateFile.TryLoad`, which returns null on `IOException`,
`UnauthorizedAccessException` and `JsonException` (`StateFile.cs:18-32`) and on `File.Exists` false for an inaccessible
path. The synchronizer then starts from `SyncRecord.Empty` and the first `Save` (`:527-531`) overwrites
`application-profiles.v1.json`. Every `<exe>_<Setting>` value WSGM created under `3DKeys` loses its owner record; IGCL has
no delete, so those overrides can only be removed in Intel's own software (the code comment at `:145-146` concedes this).
A transient sharing violation at start is enough. NVIDIA treats the same situation as fatal and keeps the file
(`NvProfilesTests.CorruptJournalAbortsWithoutReplacingIt`). Sibling of A02-F007/A02-F017; NEW for this file.
Recommendation: one shared reader with Loaded/Absent/Unreadable (GPUIR-B1). Unreadable makes `Sync` return one failure per
requested executable and write nothing; the file is never rewritten until it was read.

### GPUIR-003 (Medium) `DriverStateFile.Read` treats an inaccessible journal as absent and caps it at 4 MiB

`DriverStateFile.cs:12-20`. `File.Exists` is false for access-denied or a directory at the path, so `NvProfiles`
(`NvProfiles.cs:57`) starts with an empty journal and the next `Persist` (`:260-263`) replaces the unread original,
losing prior explicit values and inheritance needed for restoration. The 4 MiB refusal is an arbitrary size cap
(no-arbitrary-limits); a large but valid journal would disable NVIDIA profiles. Corrupt JSON throws `JsonException`, not
`DriverFailure`, which `DriverRuntime.ObserveAsync` catches anyway. NEW. Recommendation: open directly, map only
`FileNotFoundException`/`DirectoryNotFoundException` to absent, drop the size cap, throw `DriverFailure` for everything
else (GPUIR-B1). `Write` already flushes with `WriteThrough` and a unique temp name; keep it.

### GPUIR-004 (High) IR firmware routes a pending network learn reply to whichever client replaced the requester

A02S01-F001, confirmed: `learningOut = &out` (`main.cpp:450`) points at the global `client` that `serveNetwork`
reassigns (`main.cpp:584-585`) without retiring the learn. The capture or timeout is written to the new connection even
if it never presented the token. Recommendation: in `serveNetwork`, before `client.stop()`, call `cancelLearn("cancelled")`
when `learningOut == &client` (the old client gets its terminal reply, the new one inherits nothing). No generation
counter is needed. Firmware batch GPUIR-B8.

### GPUIR-005 (Medium) Intel has no pre-setter admission recheck; a cancelled or expired command still writes

`IntelGpuPlugin.cs:96-117` checks `Deadline` once before `GuardWrite`; nothing checks the token or deadline after the
preparatory reads that `FieldControl.Encode` (`FieldControl.cs:76-81`), `ThreeDFeature.BaseForWrite` (`ThreeDFeature.cs:308-321`)
and `ArcSyncDisplay.WriteCustom` (`ArcSyncControls.cs:311-330`) perform. NVIDIA/AMD recheck immediately before each setter
through `DriverWriteScope`. NEW. Recommendation: thread the explicit `WriteAdmission` (GPUIR-006) into the Intel set
entry points: `IgclSource.Write`, `ThreeDFeature.Write`, `ColorPipeline.Apply`, `IntelGraphicsMemoryTransport.TryWrite`
and the PSR off/on writes in `LowRefreshRateControl` (GPUIR-B4).

### GPUIR-006 (Medium) Write admission is ambient thread-static state

`DriverWriteScope.cs:6-27`. Covered by refactor-plan.md:151. Recommendation detail: replace with a readonly struct
`WriteAdmission(CancellationToken Token, Deadline Deadline, Func<bool> Admitted)` exposing `void Check()`, passed as a
parameter to `DriverControl.Write`, `DriverControl.ProbeSupport` and `IDriverSession.Sync`, then to the native setters
(`NvApi.Save/Set/Inherit/Color(2)/OutputMode(set)/SetDither`, `AdlxNative.SetInteger/SetBoolean/Call`, `AdlDitherApi.Write`).
`NvApi.Application(create: true)` stages DRS changes before `Save` without a check (`NvApi.cs:175-201`); that is correct
because only `Save` commits, and should be documented, not guarded.

### GPUIR-007 (Medium) Two GPU lifecycles with divergent behaviour for the same SDK contract

`DriverRuntime.cs` (NVIDIA, AMD) and `IntelGpuPlugin.cs` (Intel) differ in: command reason codes (GPUIR-008), sync result
when not running (GPUIR-009), health publication (GPUIR-019), host-refusal containment (Intel catches publish failures,
`IntelGpuPlugin.cs:662-667`; runtime lets them fault the pass), written-value overlay (C3), reopen policy (GPUIR-025), stop
budgets (GPUIR-015), discovery cadence (GPUIR-017) and UI-thread safety (GPUIR-001). Covered by refactor-plan.md:151 as
direction; the list of behaviours to unify is NEW. Recommendation: section 4.

### GPUIR-008 (Medium) `DriverRuntime` reports every refusal as `TransportFaulted`

`DriverRuntime.cs:95,102,145,593-602`. Generation mismatch, expired deadline, invalid value and pre-dispatch cancellation all
carry `CapabilityReasonCode.TransportFaulted`, non-retryable. Intel uses `GenerationChanged`/`Quiescing`/`ValueOutOfRange`
through `CommandResults` (`IntelGpuPlugin.cs:85-111`). The router and UI cannot tell a stale descriptor from a driver
fault. NEW. Recommendation: build results with the SDK `CommandResults` helpers and the specific codes Intel uses.

### GPUIR-009 (Medium) `DriverRuntime.SyncApplicationProfilesAsync` reports success when nothing was applied

`DriverRuntime.cs:194-197` returns `(0, 0, [])` when not running, when the session is null (after driver loss) or on a cycle
mismatch. `GpuCoordinator.cs:606-609` treats zero failures as "this set is applied" and stores the fingerprint, so the set is
skipped until it changes or a game starts. Intel returns an explicit failure (`IntelGpuPlugin.cs:167-173`). NEW.
Recommendation: return one `ApplicationProfileFailure("driver", "", "", reason)`; the coordinator then retries on its existing
triggers.

### GPUIR-010 (Medium, PLAUSIBLE) Intel colour support probe fails on an untouched display and hides colour for the plugin lifetime

`ColorPipeline.cs:195-232` probes with a raw `Query`; `ReadCurve`/`ReadMatrix` (`:438-480`) treat
`CTL_RESULT_ERROR_DATA_NOT_FOUND` as "nothing set yet, neutral". When the driver answers DATA_NOT_FOUND for the current LUT,
the probe returns `Uncertain`, the five colour controls are omitted, and the outcome is cached for the plugin lifetime
(`IntelGpuPlugin.cs:444-466`, `ColorPipeline.cs:222-229`). Whether the driver answers DATA_NOT_FOUND here is unverified, but the
code explicitly expects it. NEW. Recommendation: in the probe, DATA_NOT_FOUND means "no stored block": report Applied without a
write, as `ThreeDFeature.ProbeSupport` already does for its inherited default (`ThreeDFeature.cs:110-119`).

### GPUIR-011 (Medium) Shared-memory support probe gates a control on registry readback

`IntelGraphicsMemoryTransport.cs:160-163` returns `readback == stored`; a false result omits `graphics.shared-memory`
(`AdapterControls.cs:39-45`). Violates "never gate a write or a control on readback". NEW. Recommendation: probe success is
"`SetDWord` did not throw"; keep the readback only for the trace.

### GPUIR-012 (Medium) NVIDIA profile sync reports failure after a successful Save when readback differs

`NvProfiles.cs:246-257` (apply) and `:179-187` (restore) throw `DriverFailure(attempted: true)` when the post-Save readback
does not match, leaving `Pending = true`; the next sync with the same value then refuses with "An earlier NVIDIA write is
unconfirmed" (`:228-230`) unless the driver now reports it. This gates reported success on readback. NEW (maintainer memory
"no hard readback requirement, sweep every consumer"). Recommendation: Save returning 0 marks the entry written
(`Pending = false`) and counts as written; a mismatching readback is logged. Keep `Pending = true` only when Save itself threw
(genuinely uncertain), which keeps the "never blindly repeated" rule (`NvProfilesTests.UnconfirmedSaveIsNotRepeated...`).
External-edit detection is unchanged because it compares current value with `Written`.

### GPUIR-013 (Medium) IR host turns definite firmware refusals into `Unconfirmed`

`IrEndpoint.cs:474-485` maps only nine statuses to `IrRejectedException`; everything else becomes `InvalidDataException`, which
`IrPlugin.cs:468-471` reports as `Unconfirmed`. Firmware replies `timeout`, `capture-overflow`, `timing-limit` (learn),
`invalid-payload`, `invalid-timing`, `duration-limit`, `invalid-code`, `unknown-protocol`, `unsupported-protocol`,
`invalid-timeout`, `invalid-wifi`, `invalid-web`, `protocol-mismatch` before any `emit()` (`main.cpp:128-218,436-536`). A learn
timeout is therefore "Unconfirmed" in Tools and to route automation, contradicting the README ("refusals ... show up as plain
instructions"). `IrEndpointConnectionTests.cs:151` enshrines the current behaviour. NEW. Recommendation (simplify): a matched
protocol-1 reply whose status is not the expected success status is a refusal (`IrRejectedException`), because the firmware
never answers a non-success status after emitting. Delete the `knownRefusal` list; keep `Describe` for messages. Transport
failures, timeouts and malformed frames stay `Unconfirmed`.

### GPUIR-014 (Medium) IR stop and suspend cannot preempt a running action

`IrPlugin.cs:144-163` waits on `_lane` with the stop token only. An action holds the lane through scene delays
(`:394-399`, up to 5 s per step) or a sequence wait of up to ten minutes (`:12-13,666-685`); only the host's action token can
end it. NEW. Recommendation: a plugin-owned lifetime `CancellationTokenSource` linked into every action; `StopAsync` cancels it
before waiting on the lane. The sequence wait's existing `catch` then sends one `cancel` (a distinct operation, not a retry).

### GPUIR-015 (Medium) GPU stop/dispose use private budgets instead of the host deadline

Intel: `StopBudget` 5 s for the loop plus 5 s for the lane (`IntelGpuPlugin.cs:39,314-318`), and `StopLoopAsync` ignores the
caller's token (`:689-690`); `DisposeAsync` calls `StopAsync(..., CancellationToken.None)` (`:359`). Runtime: `DisposeAsync`
creates its own 5 s budget (`DriverRuntime.cs:317`). Neither reads `context.Deadline`. Under planning-corrections B3 the
device/GPU phase ends at 0.7 B (3.5 s for SessionEnd). NEW relative to the plan. Recommendation: honour `context.Deadline` and
the token, return `false` (not throw) when release is unconfirmed, retain the retiring task; delete `StopBudget`.

### GPUIR-016 (Medium) AMD interface validity is a process-static mutable epoch

`AdlxNative.cs:11-12,185-190`; `AdlxObject` captures it (`:216-231`); `AdlxSession.Dispose` compares it (`AdlxSession.cs:229`).
The AMD test assembly must disable parallelization because of it (`NativeContractTests.cs:6`). Plan diagnosis of mutable
process globals (refactor-plan.md:38) applies; NEW for AMD. Recommendation: an `AdlxEpoch` object owned by `AdlxSession`, passed
to `AdlxObject` and to `Check`; behaviour identical (no Release through invalidated pointers).

### GPUIR-017 (Medium) NVIDIA and AMD rediscover the whole driver model every 10 seconds

`DriverRuntime.cs:399-401` calls `Discover()` on every observation. NVIDIA allocates a 414,112-byte values buffer per curated
setting (`NvApi.cs:262-280`, roughly 40 settings, about 16 MB of large-object heap every 10 s) and recaptures CCD topology per
output check; AMD re-acquires every feature interface and the GPU/display lists (`AdlxSession.cs:105-211`). Intel rebuilds only
when its active outputs change (`IntelGpuPlugin.cs:544-548`). Not a per-sample path, but steady churn and driver load. NEW.
Recommendation: one shared rule from Intel: discover on open and when `IDriverSession.OutputsChanged()` reports a topology
change; ordinary passes only read controls. NVIDIA's "outputs changed" compares display ids from `Displays()`; AMD compares
display unique ids. The fingerprint check stays.

### GPUIR-018 (Low) Shared descriptor labels are truncated to 48/64 characters

`DriverDescriptors.cs:14,32,61,70-73` (`.Take(max)`). The SDK has no label length limit and Intel does not truncate
(`Descriptors.cs:122-139`). No-arbitrary-limits violation. NEW. Recommendation: keep the unsafe-character filter, drop `Take`.

### GPUIR-019 (Low) `DriverRuntime` republishes unchanged health every observation

`DriverRuntime.cs:416-419,584-591` publishes on every pass; `PluginHost.Publish` (`src/WSGM/Shell/PluginHost.cs:184-213`) does
not deduplicate, so each pass posts to the UI and raises `HealthChanged` → `GpuCoordinator.Changed`. Intel deduplicates
(`IntelGpuPlugin.cs:807-837`). NEW. Recommendation: deduplicate once in `PluginHost.Publish` (same instance, generation, health
and detail → no post) and delete Intel's `_health`. If the Shell owner declines, deduplicate in the runtime instead.

### GPUIR-020 (Low) Arbitrary count bounds that fail discovery

`AdlxNative.cs:61-64` (list > 1024 fails the whole discovery), `ColorPipeline.cs:263` (`NumBlocks > 32`), `NvApi.cs:246-257`
(fixed 4096-entry setting id buffer, larger tables fail). ABI-defined maxima are fine (`NVAPI_SETTING_MAX_VALUES` 100,
`NVAPI_MAX_PHYSICAL_GPUS` 64, IGCL `MaxFrame`). NEW. Recommendation: size the buffers from the driver's count (NVAPI returns the
required count; IGCL and ADLX lists report it) and drop the non-ABI caps.

### GPUIR-021 (Low) Three atomic JSON state helpers with different safety

`DriverStateFile` (durable, unique temp), Intel `StateFile` (`StateFile.cs:47-54`: no flush, fixed `.tmp` name, unreadable →
null), IR `JsonFile` (`IrPayload.cs:169-196`: durable, unique temp). NEW. Recommendation: Intel uses `DriverStateFile` once it
links `Shared/Gpu` (GPUIR-B1); IR keeps `JsonFile` but adopts the same Loaded/Absent/Unreadable reader (GPUIR-B6). No
cross-package shared assembly.

### GPUIR-022 (Low) Dead preparatory reads before writes

`NvSession.cs:239-246` and `NvOutputControls.cs:255-263` call `Get` and discard it; `AdlxSession.cs:271-283` calls `read()` and
discards it. The comments say the read must not gate the write, which is true of not reading at all. NEW. Recommendation:
delete the reads.

### GPUIR-023 (Low) `NvGsyncControl` duplicates `NvSettingControl`'s DRS logic

`NvOutputControls.cs:224-267` repeats `NvSession.cs:213-257` (probe, read, write) for setting `0x1094f157` with a boolean
encoding. NEW. Recommendation: one DRS helper (`Probe/Read/Write(uint setting)`) used by both controls, or a boolean mode on
`NvSettingControl`.

### GPUIR-024 (Low) Intel caches support outcomes in four places

`IgclSource._support` (`IgclSource.cs:64,90-106`), `ThreeDFeature._support` (`ThreeDFeature.cs:53,103-129`),
`ColorPipeline._curveSupport/_matrixSupport` (`ColorPipeline.cs:159-161,195-232`) and the plugin's `_supportResults`
(`IntelGpuPlugin.cs:46,444-461`). NEW. Recommendation: keep only the runtime cache keyed by `SupportKey`; the adapter's
`SupportKey` is the shared structure's identity (source, feature, colour block), so shared structures are still probed once.

### GPUIR-025 (Low) Intel reopen backoff is mechanism without a cited defect

`IntelGpuPlugin.cs:41-42,528-543,784-804` doubles the reopen delay to five minutes. The package only loads on machines with an
Intel adapter (manifest `displayAdapters` 8086), `IgclApi` load is cheap and logging already goes through `TraceChange`.
NEW (simplify). Recommendation: drop it; the runtime reopens on the 10 s observation like NVIDIA/AMD.

### GPUIR-026 (Low) Intel `StopLoopAsync` forgets a loop that is still running

`IntelGpuPlugin.cs:682-700` disposes the CTS and clears `_loopTask` even when the loop did not finish; a later `ResumeAsync`
(`:300`) can start a second loop while the first completes its pass. Removed by GPUIR-B4. NEW.

### GPUIR-027 (Low) Intel duplicates WDC's CCD display identity

`DisplayIdentity.cs:61-97,136-137` and `ValueMapping.DecodeManufacturer` (`ValueMapping.cs:157-185`) re-implement the target-name
query that WDC `DisplayTopology.CaptureActive()` already returns as `ActiveDisplayPath` (device path, EDID ids, output
technology, LUID and target id; `DisplayTopology.Types.cs:14-52`); NVIDIA already uses WDC. Partially covered by pre-AM01 GP01
step 3 ("share WDC CCD mechanism"). Recommendation: look the IGCL output up by LUID + target id in one topology capture per build,
keep `DisplayIdentityResolver.InstanceId` and its format byte for byte (persisted per-display values depend on it), keep the
IGCL encoder flag precedence. Requires W02_01 (EDID flag `0x4`) first, otherwise EDID ids become null and instance ids change.

### GPUIR-028 (Low) IR `_lastCommand` mirrors `IrLibrary.SelectedCommandId`

`IrPlugin.cs:19,109,307,313,418-421,439,817-820`. Every path that sets one sets the other, except start, which falls back to the
last command. NEW. Recommendation: derive "selected" from the library (`SelectedCommandId ?? Commands.LastOrDefault()?.Id`) and
delete the field.

### GPUIR-029 (Low) IR sequence wait reports completion when it stops polling

`IrPlugin.cs:666-679` returns normally after 1,200 polls whether or not `sequenceRunning` cleared, and the action reports
`Dispatched`. NEW. Recommendation: if still running at the end, return `Unconfirmed` with "sequence still running"; keep the poll
bound since it mirrors the firmware's ten-minute sequence limit.

### GPUIR-030 (Low) IR request framing round-trips through a dictionary

`IrEndpoint.cs:413-423` serializes the argument object, deserializes it into `Dictionary<string, object?>`, adds envelope fields
and serializes again. NEW (simplify). Recommendation: build the frame with `JsonObject` or `Utf8JsonWriter` directly; wire output
unchanged.

### GPUIR-031 (Low) IR firmware remaining defects and arbitrary authoring caps

A02S01-F002..F006 confirmed (see C23). A02S01-F007: confirmed. The 128-button (`embed_remotes.py:424-425`) and 32-step
(`:263-264`) caps are arbitrary counts that still do not bound the reply. A02S01-F008..F011 confirmed as authoring-tool
robustness. A02S01-F012..F014 concern generated page JavaScript and status ordering; low value, keep as documented limits unless
the maintainer wants them. Recommendation: section 3 and GPUIR-B9.

### GPUIR-032 (Low) Firmware answers `busy` to non-emitting operations; protocol.md says only emission operations do

`main.cpp:436-439` checks `busy()` before `learn`, `send*`, `press/climate/run`, `protocols` and `wifi`. protocol.md lists only
send, press, climate, sequence start and learn. NEW. Recommendation: move `protocols` above the busy check (read-only); document
that `wifi` is refused while busy (changing radio state mid-learn is unwanted).

### GPUIR-033 (Low) `SerialIrLink` leaks the port on `Open` failure

A02-F018 confirmed (`IrEndpoint.cs:107-119`). Recommendation: try/catch dispose as in `TcpIrLink`; no seam (C22).

### GPUIR-034 (Low) Intel loads `ControlLib.dll` by bare name

`IgclApi.cs:103`. NVIDIA and AMD load an absolute System32 path (`NvApi.cs:28-29`, `AdlxSession.cs:34-35`, `AdlDitherApi.cs:19-20`).
WSGM is elevated, so library search order matters. NEW. Recommendation: `Path.Combine(Environment.SystemDirectory,
"ControlLib.dll")`.

### GPUIR-035 (Low) Lifecycle fields updated outside the lane

`SessionChangedAsync` writes `_context` without the lane in `DriverRuntime.cs:268-273` and `IntelGpuPlugin.cs:250-255`, while the
observation reads `_context!.StateDirectory` (`DriverRuntime.cs:399`); IR `ResumeAsync` sets `_stopped` without its lane
(`IrPlugin.cs:134-141`). NEW. Recommendation: take the lane, or make the context a volatile immutable snapshot read once per
operation.

### GPUIR-036 (Low) Test quality gaps

- Predicate copies: `UnsupportedFeatureTests` (asserts membership of the same `is` lists, `UnsupportedFeatureTests.cs:8-28`),
  `ThreeDFeatureTests.TheFeaturesThatAreNotRowsAreSkipped/TheFeaturesTheHeaderDescribesArePublished` (`:63-79`) and
  `ArcSyncTests.CustomIsOfferedOnlyWhenTheRangeAllowsIt` (`:84-88`) restate tables.
- `NvColorTests.ActionAdmissionRequiresNullInsteadOfAValue` tests `DriverControl.Accepts`, not NVIDIA colour.
- `DriverRuntimeTests` exist only in the NVIDIA project; no test drives the runtime with AMD or Intel sessions.
- No lifecycle test for `IntelGpuPlugin` (start/stop/closePending/reopen/command admission), no `ColorPipeline` test, no
  synchronizer test for an unreadable record, no test for `DriverStateFile` absent vs unreadable.
- `IrEndpointConnectionTests.cs:151` asserts the GPUIR-013 misclassification.
NEW. Recommendation: replace the copies with behaviour through real owners (a fake `IDriverSession` driving the runtime; a fake
`IgclSession` seam is not needed once controls sit behind the adapter); add the missing cases in the batches below.

### GPUIR-037 (Nit) Documentation drift

- NVIDIA README says "No setting is written merely to discover support" (Controls) and "probes their setters with unchanged
  native state" (Lifetime); the second is true.
- Intel README places support discovery under `## Build`; `DisplayIdentity` record doc says "at most 48 characters" although
  nothing truncates (`DisplayIdentity.cs:9`).
- IR README says "no Device SDK dependency" (covered by A02-F002/SET_DOC).
- `ApplicationProfileSynchronizer.cs:500-501` and `AdapterControls.cs:62-65` blame "WSGM needs elevation" for any failure,
  including an out-of-range value.
NEW. Recommendation: fix in the doc batch; AGENTS.md text changes are proposals for the maintainer (instruction files).

### GPUIR-038 (Nit) Small inconsistencies

- Revision guard `<` in Intel (`ApplicationProfileSynchronizer.cs:163`) vs `<=` in NVIDIA (`NvProfiles.cs:63`); the coordinator
  never repeats a revision, so either works; pick `<=`.
- `ThreeDFeatureControl` clamps live FPS at 1000 (`ThreeDFeature.cs:361,562`): a status value is silently truncated; publish
  unknown above the descriptor maximum or raise the maximum.
- `AdlxSession.Discover` checks `pnp.Length == 0` after `Contains("VEN_1002")` (`AdlxSession.cs:120-128`), unreachable.
- IR publishes `carrier-hz` with `PluginStateOrigin.Initialization` after actions (`IrPlugin.cs:885-891`).
- `set-carrier` re-implements `ReplaceCommandAsync` inline (`IrPlugin.cs:375-385`).
- `TcpIrLink` connects with sync-over-async inside `Task.Run` (`IrEndpoint.cs:170-173`); acceptable, note only.

### GPUIR-039 (Low, cross-domain note) Policy inconsistency on driver frame limiters

Intel publishes feature 2, "Frame rate limit" (`ThreeDFeatureCatalog.cs:69,263-272`; README table), while NVIDIA omits driver
limiters "because WSGM/RTSS already own the frame limit" (NVIDIA README) and AMD omits Chill/FRTC for the same reason
(`AdlxSession.Graphics.cs:151`). NEW. This is a product decision (section 6).

### GPUIR-040 (Low, cross-domain note) `GpuCoordinator._syncRevision` is a process-static counter

`src/WSGM/Shell/GpuCoordinator.cs:66-70`. Owned by the Shell reviewer; listed because GPU sync correctness depends on it. An
instance field on `GpuCoordinator` gives the same guarantee within the session.

## 3. Plan refinements

Additions:

1. Admit GPUIR-001/002/003/005/009/010/011/012/013/014/015 as concrete defects in GP/IR batches, with the tests named in
   section 5.
2. Extend the shared driver contract explicitly (section 4) instead of "control adapters" alone: `DriverRead`, `WriteAdmission`,
   `OutputsChanged`, `BeginPass`, `DriverLog`.
3. One Loaded/Absent/Unreadable reader for every GPU journal (`nvidia-profiles.v1.json`, `application-profiles.v1.json`,
   `color.v1.json`) and the IR files (`library.json`, `endpoint.json`). Unreadable never seeds empty state and never writes. File
   names and JSON shapes stay identical, so no migration step is needed; list these files in the migration inventory
   (refactor-plan.md:103) as "unchanged".
4. Persisted identifiers that must survive byte for byte: Intel adapter `pci-8086-...`, display `[internal-]edid-...`/
   `display-...`, section ids `graphics[-n]`/`display-<fnv>`; NVIDIA `driver.<id>`/`output-<sha24>`/`display.*`; AMD
   `pci-<sha24>`/`internal-|output-<sha24>`; all capability ids. Add a fixture test per package that pins them.
5. Firmware batch for A02S01-F001..F011 plus GPUIR-032, build-only, flashing only on explicit direction, firmware 0.4.1,
   protocol 1 unchanged.
6. Host-side health deduplication in `PluginHost.Publish` (Shell owner) so runtimes do not each carry it.

Changes:

7. Replace "retain Intel written-value behavior" with "move the written-value overlay into `DriverRuntime` for all vendors".
8. GP01 step 3 "share WDC CCD mechanism where candidate directs" becomes the concrete GPUIR-B5, gated on W02_01 and WDC 0.2.0
   integration (I01).
9. IR01: replace "NOT READY" dispositions of A02-F017/F018 with the direct fixes above.
10. Remove the IR01 → GP01 and B01 → IR01 prerequisites (C18).
11. Refactor-plan.md:153 firmware sentence: "Firmware defects confirmed in source are fixed in a firmware-only batch; flashing and
    protocol changes require explicit direction."

Over-engineering to remove or not introduce:

| Mechanism | Where | Simpler shape |
| --- | --- | --- |
| Learn "connection generation" (A02S01-F001 disposition) | firmware | Retire the pending learn with a terminal reply when its client is replaced. |
| "Serial factory/adapter failure seam" (A02-F018) | IR host | try/catch-dispose in the constructor. |
| Feedback "explicit active state" is fine, but no scheduler | firmware | One `bool feedbackActive` plus wrap-safe subtraction. |
| NVS "partial-state semantics and host outcome handling" design (A02S01-F005) | firmware | Check `putString` return lengths; on any failure reply `storage-failed` and leave RAM state unchanged; no retry. Host shows the status as a refusal. |
| `knownRefusal` status list | IR host | Any matched non-success status is a refusal (GPUIR-013). |
| Intel reopen exponential backoff | Intel | Fixed 10 s reopen via the observation loop. |
| Intel `StopBudget` and runtime private 5 s dispose budget | GPU | Host deadline and token only. |
| Four Intel support caches | Intel | Runtime cache by `SupportKey`. |
| `IntelControl.TraceDue` per-control trace dedupe | Intel | `ICapabilityHost.TraceChange` already deduplicates per key in `Log.Change`. |
| Count caps: ADLX 1024 items, colour 32 blocks, NVAPI 4096 ids, 128 buttons, 32 steps, 4 MiB journal, 48/64-char labels | GPU, firmware tooling | Driver-reported counts, ABI maxima and the one protocol byte bound only. |
| A shared GPU runtime assembly | plan option | Keep linked source `src/Shared/Gpu/*.cs` (already how two packages work); add the link to Intel. No new project. |

Nothing in the current plan adds new GPU or IR states or generations beyond these; the plan's generic command phases
(refactor-plan.md:111) already match what `DriverRuntime` does.

## 4. Target design

### Owners

| Owner | Responsibility | Location |
| --- | --- | --- |
| `DriverRuntime` | The only GPU plugin lifecycle: lane, start/suspend/resume/stop/dispose honouring `context.Deadline`, observation loop, open/reopen, discovery on open and on `OutputsChanged`, support probe cache, descriptor fingerprint/generation, value publication with 20 s refresh and written-value overlay, command execution with `WriteAdmission`, sync dispatch, retirement retaining the native lane. All native work through `Task.Run`. | `src/Shared/Gpu/DriverRuntime.cs` (linked into all three packages) |
| `IDriverSession` | One open driver: `DriverModel Discover()`, `bool OutputsChanged()`, `void BeginPass()`, `ApplicationProfileSyncResult Sync(sync, WriteAdmission, token)`, `Dispose()`. | `src/Shared/Gpu/DriverRuntime.cs` |
| `DriverControl` | `DriverRead Read()`, `void Write(CapabilityValue, WriteAdmission)`, `void ProbeSupport(CapabilityValue current, WriteAdmission)`, `SupportKey`, `Accepts`. | same |
| `WriteAdmission` | readonly struct: token, `Deadline`, admitted predicate; `Check()` throws `DriverFailure(attempted: false)` or `OperationCanceledException`. Replaces `DriverWriteScope`. | `src/Shared/Gpu/WriteAdmission.cs` (renamed from `DriverWriteScope.cs`) |
| `DriverRead` | readonly record struct `(CapabilityValue? Value, bool Available = true, CapabilityReason? Reason = null)`. | `DriverRuntime.cs` |
| `DriverLog` | Info/Warn/Error/Change/Failure over `ICapabilityHost`; never throws. Former `IntelLog`. Replaces the `Action<string,string> report` delegate. | `src/Shared/Gpu/DriverLog.cs` |
| `DriverStateFile` | `bool TryRead<T>(path, out T value)` returns false only for absent; throws `DriverFailure` for unreadable; durable atomic write. No size cap. | `src/Shared/Gpu/DriverStateFile.cs` |
| `DriverDescriptors` | NVIDIA/AMD descriptor builders, unsafe-character filter only. Intel keeps its own `Descriptors` (placement/categories/sort order would change NVIDIA/AMD publication if merged). | `src/Shared/Gpu/DriverDescriptors.cs` |
| `IgclDriverSession` | Intel `IDriverSession`: loads `IgclApi` from System32, opens `IgclSession`, enumerates adapter class keys, builds `IntelModel`, filters explicitly unsupported controls by their first read, wraps each `IntelControl` in an adapter, owns `ColorStore`, `IntelGraphicsMemoryTransport` and `ApplicationProfileSynchronizer`; `OutputsChanged` = `IgclSession.RefreshOutputs`; `BeginPass` = `IgclSession.BeginPass`. | `src/WSGM.Plugin.IntelGpu/IgclDriverSession.cs` (new) |
| `IgclDriverControl` | Adapter `DriverControl` over `IntelControl`: maps `ControlRead` to `DriverRead`, `ControlWrite.Refused` to `DriverFailure(attempted: false)`, `Uncertain` to `DriverFailure(attempted: true)`, session-lost to `lost: true`; `SupportKey` = structure identity. | same file |
| `IrPlugin` | SDK facade: declarations (settings, actions, widgets, contributions), lifecycle, lane, lifetime token, action dispatch. | `src/WSGM.Plugin.Ir/IrPlugin.cs` |
| `IrEndpointSession` | Owns `_endpoint`, transport preferences, pairing, `Target()`, fresh Wi-Fi link per action, identify-before-use, `PairAsync`, remote catalog cache and one-refresh lookup. | `src/WSGM.Plugin.Ir/IrEndpointSession.cs` (new) |
| `IrStore` | Library and pairing read (Loaded/Absent/Unreadable) and atomic write; backup/import. | `src/WSGM.Plugin.Ir/IrStore.cs` (new, persistence moved from `IrPayload.cs`) |
| `IrStatusPublisher` | All `PublishState` projections (status, ports, network, library, selected, carrier, remotes). | `src/WSGM.Plugin.Ir/IrStatusPublisher.cs` (new) |
| `IrEndpointConnection`, links | Unchanged seams; `ReadReply` per A02_04 plus GPUIR-013 classification. | `IrEndpoint.cs` |

### Dissolved file: `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs` (lifecycle part)

`IntelGpuPlugin` stays as a 55-line forwarder identical in shape to `NvidiaGpuPlugin` (`Id => "wsgm.gpu.intel"`).

| Old symbol | New owner |
| --- | --- |
| `ObservationInterval` (10 s) | `DriverRuntime` periodic timer (already 10 s) |
| `RefreshInterval` (20 s) | `DriverRuntime.PublishValueAsync` (already 20 s) |
| `StopBudget` | deleted; host `context.Deadline` |
| `MaxReopenDelay`, `_reopenAt`, `_reopenDelay`, `ResetReopen`, `ScheduleReopen` | deleted (GPUIR-025) |
| `_lane`, `ReleaseLane`, `_closePending`, `CloseDriver` | `DriverRuntime._lane`, `ReleaseLaneAsync`, retiring task |
| `_published`, `PublishedState`, `PublishAsync` | `DriverRuntime._published`/`PublishValueAsync`, extended with `Available`, `Reason`, `Quality`, and containment of host refusals |
| `_supportResults` | `DriverRuntime._supportResults` keyed by `SupportKey` |
| `_written`, `WrittenValue` | `DriverRuntime` written-value overlay (all vendors) |
| `_adapterKeys` | `IgclDriverSession` (per session) |
| `_api`, `_session`, `CloseSession` | `IgclDriverSession` ctor/`Dispose` (library loaded and freed per session, as NVIDIA/AMD) |
| `_colors` (`ColorStore.Load`) | `IgclDriverSession` ctor with `stateDirectory` |
| `_memory` | `IgclDriverSession` ctor |
| `_synchronizer` | `IgclDriverSession.Sync` |
| `_model` (`IntelModel`) | `IgclDriverSession`; `DriverModel` carries sections and adapters |
| `_capabilities`, `_context`, `_host`, `_running`, `_disposed` | `DriverRuntime` |
| `_cycleGeneration`, `_descriptorGeneration`, `_descriptorFingerprint` | `DriverRuntime._cycle`, `_generation`, `_fingerprint` |
| `_health`, `Health` | `PluginHost.Publish` dedupe (or runtime) |
| `_log` (`IntelLog`) | `DriverLog` |
| `_loop`, `_loopTask`, `StartLoop`, `StopLoopAsync`, `RunLoopAsync` | `DriverRuntime.ObserveLoopAsync`, `RetireAsync` |
| `ExecuteCommandAsync` | `DriverRuntime.ExecuteCommandAsync` + adapter |
| `SyncApplicationProfilesAsync` | `DriverRuntime` → `IgclDriverSession.Sync` (BeginPass, `synchronizer.Apply(sync, model.FindTarget, token)`) |
| `StartAsync`, `SessionChangedAsync`, `SuspendAsync`, `ResumeAsync`, `StopAsync`, `DisposeAsync` | `DriverRuntime` |
| `OpenCycleGuardedAsync`, `OpenCycleAsync` | `DriverRuntime.ObserveAsync` open path; missing library or adapter → `IgclDriverSession` ctor throws `DriverFailure` → empty set + Unavailable |
| `BuildAsync` (read all, `RecordUnsupported`, probe) | unsupported-read filter → `IgclDriverSession.Discover`; probe → `DriverRuntime.CheckSupport` |
| `PublishDescriptorsAsync`, `RetractAsync` | `DriverRuntime.PublishModelAsync` |
| `PassAsync` | `DriverRuntime.ObserveAsync` (`OutputsChanged` → `Discover`) |
| `ObserveAsync`, `TraceDue` | `DriverRuntime.ObserveAsync`; per-key trace via `TraceChange` |
| `RecordUnsupported` | `IgclDriverSession.Discover` |
| `GuardRead`, `GuardWrite`, `Guard` | `IgclDriverControl` + runtime exception handling |
| `Render` | `DriverLog.Render` |

Also dissolved: `StateFile.cs` (→ `DriverStateFile`), `IntelLog.cs` (→ `DriverLog`, moved to `src/Shared/Gpu`),
`IgclSource._support`/`ProbeSupport` caching, `ThreeDFeature._support`, `ColorPipeline._curveSupport/_matrixSupport`
(→ runtime cache; the probe methods remain, uncached). `DriverWriteScope.cs` → `WriteAdmission.cs`.

### Dissolved file: `src/WSGM.Plugin.Ir/IrPlugin.cs` (split)

| Old symbol | New owner |
| --- | --- |
| `Settings`, `Actions`, `Widgets`, `Contributions`, `Text()` | `IrPlugin` (unchanged order and text) |
| `ConfigureAsync` validation | `IrPlugin`; transport fields → `IrEndpointSession.Configure` |
| `StartAsync` loads | `IrStore.LoadLibraryAsync`/`LoadPairingAsync` |
| `_endpoint`, `_createEndpoint`, `_port`, `_transport`, `_hostName`, `_pairing`, `Target`, `EndpointAsync`, `PairAsync` | `IrEndpointSession` |
| `_remotes`, `RefreshRemotesAsync`, `Find` | `IrEndpointSession` catalog cache |
| `RemoteActionAsync`, `WaitForSequenceAsync`, `SequencePollLimit`, `LearnAsync`, `SendAsync` | `IrPlugin` dispatch using `IrEndpointSession` |
| `_library`, `SaveLibraryAsync`, `ReplaceCommandAsync`, `ResolveCommand`, `ResolveScene`, `LibraryPath`, `PairingPath` | `IrStore` (+ `IrPlugin` holds the current `IrLibrary` value) |
| `_lastCommand` | deleted (GPUIR-028) |
| `Publish`, `PublishNetwork`, `PublishLibrary`, `PublishRemotes`, `Describe`, `_sequence` | `IrStatusPublisher` |
| `_lane`, `_stopped`, `_context`, `_host`, `ExecuteActionAsync`, `IntegerArgument` | `IrPlugin` (+ new `_lifetime` CTS) |
| `IrLibrary.LoadAsync/SaveAsync`, `IrPairing.LoadAsync/SaveAsync`, `JsonFile` (`IrPayload.cs`) | `IrStore`; validation records stay in `IrPayload.cs` |

### Public API changes and consumers

- No public type in any GPU or IR package changes; every changed type is `internal`. Consumers of the internal shared runtime:
  `NvidiaGpuPlugin.cs`, `NvSession.cs`, `NvOutputControls.cs`, `NvApi.cs`, `NvApiPrivate.cs`, `NvProfiles.cs`;
  `AmdGpuPlugin.cs`, `AdlxSession*.cs`, `AdlxNative.cs`, `AdlDitherApi.cs`; Intel as above; tests
  `DriverRuntimeTests.cs`, `NvProfilesTests.cs`, `NvColorTests.cs`, `NativeContractTests.cs`, all Intel tests constructing
  controls.
- `WSGM.Plugin.IntelGpu.csproj` gains `<Compile Include="../Shared/Gpu/*.cs" Link="Runtime/%(Filename)%(Extension)"/>`; optional
  WDC project reference only with GPUIR-B5.
- Manifests, `apiVersion` ranges and package ids unchanged; F02's Plugin 4 bump updates them mechanically.
- IR wire: no change to protocol 1; firmware 0.4.1 adds the `storage-failed` status (refusal) and moves `protocols` before the
  busy check. Host treats any unknown non-success status as a refusal, so older/newer firmware stays compatible.
- Shell (other domain): `PluginHost.Publish` health dedupe; `GpuCoordinator` unchanged except optional instance `_syncRevision`.

## 5. Implementation batches

Every batch builds green on its own, changes no UI, and runs the narrow tests listed. All GPU/IR batches are independent of
other domains except where named.

### GPUIR-B1 Journals: Loaded/Absent/Unreadable and Intel on the shared state file (about 350 lines)

Files: `src/Shared/Gpu/DriverStateFile.cs`, `src/WSGM.Plugin.NvidiaGpu/NvProfiles.cs`,
`src/WSGM.Plugin.IntelGpu/WSGM.Plugin.IntelGpu.csproj`, `StateFile.cs` (delete), `Profiles/ApplicationProfileSynchronizer.cs`,
`Display/ColorPipeline.cs` (`ColorStore`), tests `NvProfilesTests.cs`, `ApplicationProfileSynchronizerTests.cs`, new
`tests/WSGM.Plugin.IntelGpu.Tests/StateFileTests.cs`.
Steps: (1) `DriverStateFile.TryRead` opens directly, absent only on FileNotFound/DirectoryNotFound, `DriverFailure` otherwise,
no size cap. (2) Link `Shared/Gpu` into Intel (DriverRuntime compiles unused). (3) Synchronizer: unreadable record → every sync
returns failures and writes nothing; record never rewritten. (4) `ColorStore`: unreadable → keep publishing colour as unknown,
never overwrite (colour writes still apply; the record write is skipped and logged). (5) Delete `StateFile.cs`.
Tests: unreadable (locked file, directory at path) leaves bytes unchanged for NVIDIA journal, Intel record and colour record;
absent seeds empty; corrupt JSON aborts. Filter: `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~NvProfilesTests"` and
`dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter "FullyQualifiedName~ApplicationProfileSynchronizerTests|FullyQualifiedName~StateFileTests"`.
Dependencies: none.

### GPUIR-B2 Shared runtime contract and behaviour (about 900 lines)

Files: `src/Shared/Gpu/*` (`DriverWriteScope.cs` → `WriteAdmission.cs`, new `DriverLog.cs` moved from Intel `IntelLog.cs` with
Intel call sites renamed), NVIDIA and AMD sources, `DriverRuntimeTests.cs`, `NvColorTests.cs`, `NativeContractTests.cs`.
Steps: (1) `WriteAdmission` parameter through `DriverControl.Write/ProbeSupport`, `IDriverSession.Sync` and every native setter
listed in GPUIR-006. (2) `DriverRead` result type; NVIDIA/AMD controls return `new DriverRead(value)`. (3) `CommandResults` with
specific reason codes (GPUIR-008). (4) Sync not running → failure (GPUIR-009). (5) Written-value overlay. (6) `OutputsChanged`/
`BeginPass` on `IDriverSession`; NVIDIA and AMD implement topology comparison; discovery only on open/change (GPUIR-017).
(7) Stop/dispose honour `context.Deadline`, no private budget; publish failures contained. (8) Label truncation removed.
(9) `DriverLog` replaces the report delegate. Health dedupe here only if the Shell owner declines it.
Tests: fake session drives runtime for write-after-cancel (no setter call), generation/value/deadline reason codes, overlay
keeps written value until driver reports a different value, no rediscovery without topology change, stop with cancelled token
returns false and keeps the session until the call returns, sync when lost returns a failure. Filter:
`dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter "FullyQualifiedName~DriverRuntimeTests"`,
`dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj`.
Dependencies: if F02 (Device 12 clock/Deadline) lands first, use its `Deadline`; otherwise F02 adapts these call sites
mechanically. Shell: `PluginHost.Publish` dedupe (optional).

### GPUIR-B3 NVIDIA and AMD cleanups (about 400 lines)

Files: `NvProfiles.cs`, `NvSession.cs`, `NvOutputControls.cs`, `NvApi.cs`, `AdlxNative.cs`, `AdlxSession*.cs`, tests.
Steps: NVIDIA Save-success marks written, readback diagnostic only (GPUIR-012); delete dead `Get` (GPUIR-022); one DRS helper for
G-SYNC and settings (GPUIR-023); setting-id buffer sized from the driver (GPUIR-020); AMD per-session `AdlxEpoch` (GPUIR-016),
re-enable test parallelization, delete dead read and dead PNP check, list size from driver count.
Tests: `NvProfilesTests` updated for GPUIR-012 (mismatching readback after Save → written, external edit later preserved, Save
throw → not repeated); AMD epoch isolation across two sessions; persisted ids fixture. Filters as B2.
Dependencies: B2.

### GPUIR-B4 Intel onto `DriverRuntime` (about 1,400 lines, mostly deletion)

Files: `IntelGpuPlugin.cs` (reduced to forwarder), new `IgclDriverSession.cs`, `IntelModel.cs`, `Controls/IntelControl.cs`,
`Controls/FieldControl.cs`, `Igcl/IgclSource.cs`, `Igcl/IgclApi.cs`, `Graphics/ThreeDFeature.cs`,
`Graphics/IntelGraphicsMemoryTransport.cs`, `Graphics/AdapterControls.cs`, `Display/ColorPipeline.cs`,
`Display/ArcSyncControls.cs`, `Display/PowerControls.cs`, Intel tests.
Steps: (1) `IgclDriverSession` and `IgclDriverControl` per section 4. (2) `WriteAdmission` into the Intel set entry points
(GPUIR-005). (3) Remove support caches (GPUIR-024), backoff (GPUIR-025), `TraceDue`, `_health`. (4) Colour probe treats
DATA_NOT_FOUND as no stored block (GPUIR-010). (5) Shared-memory probe without readback gate (GPUIR-011). (6) System32 path for
`ControlLib.dll` (GPUIR-034). (7) Replace predicate-copy tests (GPUIR-036).
Tests: runtime + fake `IDriverSession` already cover lifecycle; add adapter mapping tests (Refused → Rejected, Uncertain →
Indeterminate, unavailable read → Available false with reason), colour probe on DATA_NOT_FOUND, shared-memory probe with
mismatching readback still publishes, persisted Intel ids fixture (adapter, display, sections). Filter:
`dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`.
Dependencies: B1, B2. Manual: M01-20 on the Optimus notebook (N) and Claw (C).

### GPUIR-B5 Intel display identity through WDC (about 200 lines)

Files: `Display/DisplayIdentity.cs`, `ValueMapping.cs` (remove `DecodeManufacturer` if unused), Intel csproj (WDC reference),
`DisplayIdentityTests.cs`, `ValueMappingTests.cs`.
Steps: resolve outputs from one `DisplayTopology.CaptureActive()` per build by LUID + target id; keep `InstanceId` format and
IGCL encoder precedence; fall back exactly as today when no path matches.
Tests: instance id equality for recorded fixtures (internal panel, external, no-EDID). Filter:
`dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter "FullyQualifiedName~DisplayIdentityTests"`.
Dependencies: WDC W02_01 (EDID flag `0x4`) and the WDC 0.2.0 integration (I01) if `DisplayTopology` becomes an instance service.
Skip the batch entirely if the maintainer prefers to keep Intel free of WDC (open question 2).

### GPUIR-B6 IR host correctness (about 450 lines, includes A02_04)

Files: `IrEndpoint.cs`, `IrPayload.cs`, `IrPlugin.cs`, `IrEndpointConnectionTests.cs`, `IrLibraryTests.cs`,
`IrRemoteActionTests.cs`.
Steps: A02_04 as specified; GPUIR-013 classification inside the same `ReadReply`; loaders Loaded/Absent/Unreadable (A02-F017):
unreadable fails `StartAsync` with an explanatory health and writes nothing; `SerialIrLink` try/catch dispose (A02-F018);
lifetime token cancelled by `StopAsync` (GPUIR-014); sequence-wait end reports still running (GPUIR-029); delete `_lastCommand`
(GPUIR-028); direct frame building (GPUIR-030); `_stopped`/context under lane (GPUIR-035).
Tests: learn `timeout` and send `invalid-payload` → `Rejected`, never resent; unknown status → `Unconfirmed`; stop during scene
delay returns promptly with one action result and no further send; stop during sequence wait sends exactly one `cancel`;
locked `library.json` keeps bytes and fails start. Filter:
`dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`.
Dependencies: none (A02_04 merged here or run first unchanged). SET_DOC for README wording.

### GPUIR-B7 IR plugin split (about 900 lines, mostly moves)

Files: `IrPlugin.cs`, new `IrEndpointSession.cs`, `IrStore.cs`, `IrStatusPublisher.cs`, `IrPayload.cs`, tests.
Steps: move per the table in section 4; no behaviour change; action ids, labels and publication keys identical.
Tests: existing suite unchanged plus one test that the published state keys/texts after start, learn and select match a recorded
fixture. Filter as B6. Dependencies: B6.

### GPUIR-B8 Firmware fixes (about 250 lines C++/Python)

Files: `Firmware/src/main.cpp`, `Firmware/embed_remotes.py`, `protocol.md`, `README.md`.
Steps: F001 retire learn on client replacement; F002 `feedbackActive`; F003 numeric model range; F004 strict hex lexing; F005 check
`putString`/`begin`, reply `storage-failed`; F006 check `deserializeJson`, serve zero remotes on failure; F007 replace 128-button
and 32-step caps with an exact byte check of the emitted `remotes` reply ≤ 32768; F008-F011 authoring checks; GPUIR-032 order;
`Firmware = "0.4.1"`.
Validation: `.codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware` (build only); no upload.
Dependencies: none. Manual: M01-37/38 on the desktop (D) after the maintainer flashes.

### GPUIR-B9 Documentation (about 150 lines)

Files: Intel/NVIDIA/AMD README and PROVENANCE where behaviour changed, IR README/protocol.md, `docs/plugin-system.md` if it
describes GPU lifecycle. AGENTS.md changes (Intel "Stop and dispose stay bounded", "reopen less often", per-plugin lane wording)
are written as proposals for the maintainer, not committed in this batch. `npm run format` on touched Markdown.
Dependencies: B4, B6, B8.

## 6. Risks and open questions

Risks:

- GPUIR-B4 changes when Intel loads and frees `ControlLib.dll` (per session instead of per plugin start) and when discovery runs.
  Only manual M01-20 on N and C can confirm no driver-side regression; the plan already requires it.
- GPUIR-012 and GPUIR-010 change reported outcomes, not writes; NVIDIA and Intel per-game behaviour should be retested on N.
- Firmware fixes only take effect after an attended flash; until then the host must keep tolerating 0.4.0 behaviour (it does).
- No AMD hardware: B2/B3 AMD changes are compile- and fake-tested only.

Open questions for the maintainer:

1. Intel publishes its driver frame-rate limiter while NVIDIA and AMD omit theirs because WSGM/RTSS own frame limiting
   (GPUIR-039). Omit Intel feature 2 too, or keep it?
2. Should Intel take a WDC dependency for display identity (GPUIR-B5), or stay WDC-free and keep its own CCD query?
