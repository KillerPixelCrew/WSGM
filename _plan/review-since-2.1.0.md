# Review since 2.1.0

Baseline `1df3f4d8` (2.1.0 release) to `e8d7b383` (LibHandheld `3525145`, LibGPUDriverInteract
`8649b41`). Focus: `external/libhandheld` (about 43k source lines) and
`external/libgpu-driver-interact` (about 14k), both new since 2.1.0, plus the WSGM code that consumes
them.

Two passes, both read-only:

- **Part 1, simplification:** six agents covering reuse, simplification, efficiency and altitude.
- **Part 2, correctness:** the `/code-review xhigh` pass over the whole range, including a
  completeness check of the Claw and Ally migration. Where a correctness finding is the symptom of a
  Part 1 item, it points there.

Every item was verified by reading the code and grepping callers; nothing was built, run, tested on
hardware or changed. Line estimates are the reviewers' approximations.

# Part 1: Simplification

Paths: `LH/` is `external/libhandheld/src/LibHandheld`, `GPU/` is
`external/libgpu-driver-interact/src/LibGPUDriverInteract`, `src/` is WSGM.

## Maintainer decisions (2026-10-09)

These settle the items that changed intended behaviour or reversed a recorded choice. They override
the "Change" text of the items they name.

1. **Generations: drop them.** Lifecycle is serialized by one owner, so an active flag per cycle
   replaces cycle, descriptor and expected generations in both libraries, their contracts, the
   adapters and WSGM's channel and router (X-2, G-1, LH-19). PLAN.md's "consumer-owned lifecycle
   generations" is superseded.
2. **Recovery journal: one shared journal that never blocks.** Keep crash restoration of captured
   originals, in one journal for all families (serviceId to original). Load and save failures are
   logged and the write proceeds; no entry ever locks or hides a control; no fixed caps (LH-R1,
   LH-4, J-1 to J-7).
3. **GPU support discovery: Intel keeps the probe.** Intel's driver capability queries were tried
   and reported features as available that were not, so the write-back probe stays the evidence for
   Intel. AMD and NVIDIA may move to their driver support queries (ADLX supported slots, NVAPI
   `EnumAvailableSettingIds`/`Values`) only where that is shown to work on hardware; until then they
   keep the probe (GPU-R1, J-8).
4. **Ids: rename everything.** One plain family-id scheme for every family, Claw and Ally included,
   with state directories to match; no legacy ids or legacy journal formats (LH-14, P-2). 2.1.0
   users lose saved device profiles, OEM assignments and pending journal restores; there is no
   migration. NVIDIA DRS profiles get a new name and the old `"WSGM <hash>"` profiles are deleted
   once (GPU-12).
5. **One unified abstraction layer.** WSGM calls the unified `HandheldDevice` API and never reaches
   the device functions below it. The semantic operations (`SetTdpAsync`, fan, charge, lighting)
   are that layer and WSGM adopts them; they are not deleted. The plugin-shaped adapter and runtime
   in WSGM go (X-1). Whether vendor-specific controls without a semantic operation keep a typed
   command entry on `HandheldDevice` is settled when the layer is designed (LH-12).
6. **Fan curves: WSGM, for every family.** The library exposes `FanDuty` (and a native `FanCurve`
   only where firmware has one); WSGM runs one software curve loop for all families. Legion's
   in-library loop goes (LH-13).
7. **Generic fallback: remove it.** Only exact model profiles are supported; any other machine is
   passive. This removes C-1 and with it the setup and banner effects on desktops (C-1, C-3, W-6).

## Cross-cutting themes

### X-1. The retired plugin model still runs on both sides of each library (structural)

- LibHandheld keeps a private copy of the old Device SDK: `LH/Internal/{Capabilities,Input,
Lifecycle,Identity}` are field-for-field copies of `LH/Contracts` (14 to 16 file pairs, only
  visibility, namespace and wording differ). `LH/HandheldConversions.cs` (488 lines) maps one to the
  other; about a dozen of its overloads have no caller.
- `LH/Internal/Plugin/PluginContracts.cs` keeps `IDeviceEngine` as a plugin entry contract
  (`PackageId`, `DetectAsync` with an unread `Reason`, `ApplySettingsAsync`, `PluginStopReason`).
- WSGM converts back: `src/WSGM/Shell/HandheldDeviceAdapter.cs` (758 lines) re-implements
  `IDevicePlugin` and maps Contracts to `WSGM.Device.Sdk` types; about 11 of its 34 overloads have
  no caller. `HandheldDeviceRuntime.cs` is the old `DevicePluginRuntime` (94% similar) with
  `DirectPluginHostAdapter` at :1133.
- GPU repeats the pattern: every `Gpu*` record and enum in `GPU/Contracts` mirrors the Plugin SDK
  capability model, and `src/WSGM/Shell/GpuDriverAdapter.cs` (256 lines) converts back with
  `Enum.Parse(x.ToString())` (9 sites, runtime failure on any name drift) into `ICapabilityPlugin`,
  which then runs through `PluginCapabilityChannel` and plugin health.
- To keep the device side working, about 13k lines of Device SDK moved into
  `src/WSGM.Plugin.Sdk/Shared`. About 3,500 to 4,900 of them (DeviceRecoveryJournal,
  DeviceServiceLifecycle, DeviceCommandSerializer, DeviceService, LegacyMotionStream,
  LowLevelKeyboardHook, MotionFilters, MotionSampleBuilder, OemButtonLatch, DeviceReconnect,
  PrecisionTicker, DeviceWriteBudget, ActiveClock, DiagnosticText, CommandResults,
  GlyphAssetValidation, PluginManifestValidator) have no consumer but their own tests. DeviceLab
  uses only `HidDevices` and `LegacyMotionSensors`.

**Change:** family code uses `LibHandheld.Contracts` directly; delete the internal copies and
`HandheldConversions`. `HandheldDeviceRuntime` holds a `HandheldDevice`, implements
`IHandheldObserver` and consumes Contracts types; delete the adapter and `DirectPluginHostAdapter`.
For GPU, give `GpuCoordinator` a non-plugin publisher that feeds `GpuDriver` events into
`DeviceCapabilityRouter`, or keep the mirror with exhaustive switches instead of `Enum.Parse`. Strip
`WSGM.Plugin.Sdk/Shared` to what IR, GPU and DeviceLab use (DeviceLab could reference LibHandheld).
Estimate: 2,250 lines in LibHandheld, 3,000 to 5,000 in WSGM, plus tests.

### X-2. Generations, admission and serialization are layered three to five deep (structural)

- Device commands: generation checked in each engine, again in `LH/HandheldDevice.cs:104-143`, again
  in `HandheldDeviceRuntime.cs:1149-1194`. Publications filtered by `HandheldDevice`, by
  `DirectPluginHostAdapter` and by `DeviceCapabilityRouter`. `AsusAuraDeviceEngine.cs:288,367`
  renumbers generations to merge two sources. Engines throw "inconsistent cycle generation" when
  context and sink disagree (`ClawDeviceEngine.cs:106`, Ally, Legion). `HandheldDevice.BeginCycle`
  polices mixing library-owned and consumer-owned generations.
- Device serialization: `HandheldDevice` (`_lifecycle`, `_commands`, manual in-flight counter), the
  engine `DeviceCommandSerializer`, `AsusAuraDeviceEngine._commands`/`_publications`, and WSGM
  `HandheldDeviceRuntime` (`_lifecycleGate`, `_commandGate`, command-id dictionary).
- GPU: commands checked in `PluginCapabilityChannel.cs:98,130,165`, `GPU/GpuDriver.cs:223`, and
  `GPU/Runtime/DriverRuntime.cs:156` or `GPU/Intel/IntelDriverEngine.cs:114`. Engines keep
  `_cycle`, `_generation`, `_cycleGeneration`, `_descriptorGeneration` and receive the cycle twice
  (`IDriverSink.CycleGeneration` and `DriverContext.Generation`).
- GPU deadlines: `GPU/Contracts/DriverDeadline.cs` (184) and `GPU/Runtime/DriverActiveClock.cs`
  (142) are renamed copies of the Plugin SDK `Deadline`/`ActiveClock`. Production uses only
  `HasExpired` and `Never`; `DriverContext.Deadline` is always `Never` (`GpuDriver.cs:483`). The
  channel already turns the deadline into a token (`PluginCapabilityChannel.cs:180`). The first
  command starts a second always-on AboveNormal 250 ms clock thread.

**Change:** one owner per concern. Lifecycle and commands are already serialized by one owner, so
one gate in `HandheldDevice` / `GpuDriver` plus the engine serializer for observation is enough.
Either the library stamps and checks generations once (engines publish plain data) or WSGM's channel
is the only gate. Delete `DriverDeadline`, `DriverActiveClock`, `GpuCommand.Deadline`/`CommandId`,
`DriverContext.Generation`, `IDriverSink.CycleGeneration`. Estimate 400 to 600 lines device side,
about 450 GPU side. Needs decision 1.

### X-3. Settings manifests are dead end to end (local, pure deletion)

- `LH/Internal/Settings/PluginSettingsManifest.cs` (427) and `PluginSettingSection.cs` (110),
  `IDeviceEngine.ApplySettingsAsync`, `IDeviceSink.PublishSettingsManifestAsync`. No engine
  publishes one; `HandheldDevice.cs:174` throws on a non-empty manifest; `AsusAuraDeviceEngine.cs:
232,319` only forwards.
- WSGM: `HandheldDeviceAdapter.ApplySettingsAsync` throws on non-empty values, yet
  `HandheldDeviceRuntime.cs:592`, `PluginSettingsCoordinator.cs:158,325`, `DeviceCoordinator.cs:
3126`, `PluginSettingsResolver.cs`, `PluginSettingsPage.axaml.cs`, `PluginSettingRowViewModel.cs`,
  `SettingsViewModel.Plugins.cs:430-442` and `DeviceConfiguration.Declaration` are still wired. The
  IR plugin and `CommonPluginManager` publish nothing into it (verify once more before deleting the
  shared pipeline; only the device branch is certainly dead).

**Change:** delete. About 540 lines library, up to 1,100 WSGM, about 900 test lines.

### X-4. UI placement and labels round-trip through the boundary (local)

- Engines set `SectionId`, `CategoryId`, `SortOrder`, `Prominence`, `LayoutPair` and `Sections` at
  60 to 136 sites, using `CapabilitySection.cs` (245), `CapabilityLayout.cs` (65), `DeviceSections`,
  `CustomTitleRule`, per-engine `Category()` helpers and identical `SectionIds`/`CategoryIds`/
  `CapabilityInstances` classes (`ClawCapabilities.cs:943-977`, `AllyDeviceEngine.Surface.cs:
357-380`). `HandheldConversions.ToPublic` drops all of it and WSGM `Place()`
  (`HandheldDeviceAdapter.cs:693`) recomputes it from `Role`. The validators are test-only.
- `HandheldConversions.Label` (:455) flattens `DisplayKey` to English; `HandheldDeviceAdapter.
Display` (:730) parses the English back. Two 21-entry tables must stay identical. Profile-driven
  families publish Custom labels ("Controller source", "Fan RPM") where Claw/Ally publish
  `DisplayKey.Controller`, so the same role is localized on two families and English on the rest.
- Legion branches on `SectionId == RgbId` (`LegionGoDeviceEngine.Surface.cs:234`, `.Commands.cs:44`).

**Change:** WSGM `Place()` stays the single owner. Delete the placement fields and types from the
library, switch Legion's branches to `Role`, put `DisplayKey` (plus an optional custom name for
vendor-specific roles) on the public descriptor. About 700 lines.

## LibHandheld

### Rule conflicts

- **LH-R1. Journal failure blocks writes.** `AllyDeviceEngine.cs:175-177`
  `Block(journalFailure, _power, _fans, _controller)`, `ClawDeviceEngine.cs:193-197` `BlockService`,
  services return Faulted (`AllyServices.cs:44,108,174,244`, `ClawServices.cs:134,250`),
  `ClawDeviceEngine.Commands.cs:348` "recovery journal is unavailable; nothing was written",
  `LegionGoDeviceEngine.Commands.cs:47` rejects volatile writes. Decision 2.
- **LH-R2. Uncertain writes latch a control forever.** `GenericAsusService.cs:57,179,277`
  `_uncertainFirmwareCommands` and `AsusAuraController.cs:11,253` `_uncertain` reject every later
  user write and are never cleared. Legion's `_fanCurveFaulted` clears on the next user write
  (`LegionGoDeviceEngine.Commands.cs:153,178,205`), which matches the rule. **Change:** delete both
  latches, return `Indeterminate`, accept the next explicit command.
- **LH-R3. Generic Aura speed bytes run opposite to HC.** `Families/Generic/AsusAuraProtocol.cs:106`
  maps slow/medium/fast to `0xE1/0xEB/0xF5`; `AllyProtocol.Speed` follows HC (`0xEB/0xF5/0xE1`).
  Correctness, fold into LH-9.
- **LH-R4. Missing pad logs twice a second.** `Internal/Devices/ProfiledInputServices.cs:167-191`
  stops the source and publishes an empty device set every 500 ms while the pad is absent or
  ambiguous (possibly permanent); each publish runs `DeviceCoordinator.OnPhysicalIdentities`
  (`src/WSGM/Shell/DeviceCoordinator.cs:2149-2184`) into `ControllerManager.StartAsync`, a HidHide
  call and a `Log.Info`. **Change:** publish `[]` once on the connected-to-disconnected transition,
  stop only an existing reader; ideally react to `CM_Register_Notification` arrivals like HC.
- **LH-R5. `DeviceTrace` is silent on 7 of 9 families.** Code traces through `IDeviceSink.Trace`/
  `TraceChange` (69 sites) and static `DeviceTrace` (103 sites), but only `ClawDeviceEngine.cs:113`
  and `AllyDeviceEngine.cs:131` call `DeviceTrace.Install`. Shared helpers (DeviceServiceLifecycle,
  DeviceCommandSerializer, LegacyMotionSensors, LowLevelKeyboardHook, MotionSampleBuilder) log
  nothing on Legion, GPD, OneXPlayer, AYANEO, Zotac, Valve and the generic ranges. **Change:**
  `HandheldDevice` installs `DeviceTrace` from its diagnostics callback; drop `Trace`/`TraceChange`
  from the sink. Restores missing wsgm.log lines.

### Structural

- **LH-1. Engines repeat the same orchestration.** `ClawDeviceEngine*` (1,835), `AllyDeviceEngine*`
  (1,335), `LegionGoDeviceEngine*` (1,378), `ProfiledDeviceEngine*` (714) and
  `AsusAuraDeviceEngine` each implement journal open, service build, `AcquireAllAsync`, surface,
  publish, observe, suspend/resume/stop, controller release/reacquire and command routing.
  Claw/Ally lifecycles are clones (`ClawDeviceEngine.cs:242-528` vs `AllyDeviceEngine.cs:212-476`,
  about 200 lines: Suspend, Resume, SetControllerManagement, Stop, Dispose with local `Step`,
  ReleaseControllerCore, RollBackFailedStart with 12 s deadline, CurrentStartResult, diagnostics).
  `ExecuteBoundCommandAsync` is near-identical (`ClawDeviceEngine.Commands.cs:11` vs
  `AllyDeviceEngine.Commands.cs:13`). Legion was written new but copies the hand-written shape with
  eight `_xxx`/`_xxxFaulted` fields instead of the GPD/OneX profiled pattern.
  `ProfiledDeviceEngine` hardcodes SMU/Intel power, indexed EC fan, XInput and one `_native` slot,
  routed by a role switch (~:200).
  **Change:** make the profiled engine composable: `DeviceService` gains the provider members
  `ProfiledFeatureService` has (`Descriptors`, `States`, `ExecuteAsync`, `RefreshAsync`) plus
  `RequiresControllerOwnership` and optional `ApplyHaptics`; the model holds a list of service
  factories defaulting to SMU, EC and XInput (the Steam Deck `openPower`/`openEc` parameters are half
  of this); commands route by descriptor ownership. Legion first, then Ally and Claw (hardware-tested,
  last). Short of that, move the clone lifecycle into `DeviceServiceLifecycle`
  (`DisposeAllAsync`, `SetControllerAsync`, `Diagnostics`). Estimate 2,000 to 2,500 lines.
- **LH-2. `AsusAuraDeviceEngine` decorates every non-ASUS engine** (`Internal/Devices/
AsusAuraDeviceEngine.cs`, 502; `Internal/DeviceFamilies.cs:89-94`). It proxies `IDeviceSink`, has
  its own command and publication semaphores, generation renumbering, start/stop merging, and a 2 s
  `PeriodicTimer` (:420) enumerating HID on every handheld, Claw and Legion included.
  **Change:** with LH-1, ASUS accessory lighting is one more service in a model's list
  (`GenericAsusService` already has that shape); discovery event-driven or gated. About 350 lines.
- **LH-3. Detection runs three or four times and Generic patches profiles to survive it.** WSGM
  detects in `DeviceCoordinator`, `HandheldDeviceAdapter.DetectAsync` and
  `HandheldDeviceRuntime.cs:418`; `HandheldDevice.cs:252-256` re-reads WMI identity and calls
  `DetectAsync`; each engine re-reads and re-matches (`ClawDeviceEngine.cs:118`, `AllyDeviceEngine.
cs:133`, `LegionGoDeviceEngine.cs:89`). `GenericModels.cs:341` `Bind()` rewrites a profile's
  matchers to the live identity so the re-match passes. `Families/Generic/ProfiledEngineSelector.cs`
  (86, with `GenericEngine`/`ZotacDeviceEngine`) exists because the engine is built before identity
  is known. Ally re-reads SMBIOS and power on every command (`AllyDeviceEngine.Commands.cs:29`);
  Claw/Ally services re-check `ExactMachineMatch` in every `AcquireAsync` (20 references).
  **Change:** family `Match` is the only detection; `DeviceFamily.CreateEngine(model, identity)`
  builds from the matched profile; read identity once per start, AC status per command. Remove
  `IDeviceEngine.DetectAsync`, `Bind()` and `ProfiledEngineSelector`. About 400 lines.
- **LH-4. Journal subclasses.** `DeviceRecoveryJournal` (403) has 11 subclasses (Claw, Ally, Legion,
  Profiled, SteamDeck, SteamMachine, Ayaneo, Ayn, GPD, OneX, Zotac), each with a state record,
  `JsonSerializerContext`, `ValidateEntry` with fixed caps (`Addresses.Length <= 8` at
  `ProfiledRecoveryJournal.cs:32`, `<= 250` W, `ClockIds.Length <= 11`) and a copied 6-line
  `Open`/`OpenAsync`. The capture-original-once step is repeated about 9 times
  (`GenericAsusService.cs:296`, `OneXNativeService.cs:187`, `AynNativeService.cs:146`,
  `GpdNativeService.cs:93`, `SteamDeckNativeService.Hardware.cs:170-185,227`,
  `SteamMachineLighting.cs:133-140`, `ProfiledHardwareServices.cs:78-100,309`,
  `LegionGoDeviceEngine.Commands.cs:353,381`) although `DeviceRecoveryJournal.BeginAsync` (:91)
  already keeps a same-firmware first original. Legacy formats survive: `ClawFirmwareIdentities.
IsLegacy`, `LegacyRecoveryBinding` (`ClawServiceBase.cs:149`, `ClawRecoveryJournal.cs:38,80`),
  Ally "earlier builds" migration (`AllyDeviceEngine.Recovery.cs:28-43`).
  **Change (after decision 2):** one journal mapping serviceId to original, failures logged,
  no caps, no legacy paths, `BeginAsync(serviceId, firmware, Func<TState?> capture, ct)`. About 600
  to 800 lines.
- **LH-5. Public surface wider than WSGM uses.** WSGM uses `Create`, `Detect`, `Observer`,
  `Definition`, `GlyphResources`, Start/Suspend/Resume/Stop with consumer generations, `ExecuteAsync`,
  `ApplyHapticsAsync`, `ReleaseControllerAsync`, `SetControllerManagementAsync`,
  `GetDiagnosticsAsync`, `OpenGlyphResource`, `DisposeAsync`. See LH-12.

### Reuse (local unless noted)

- **LH-6. Ally XInput reader copies `StandardControllerSource`.** `Families/RogAlly/AllyInput.cs:
210-570` vs `Internal/Windows/StandardControllerSource.cs:18-349`: same xinput1_4 #100/#108/SetState
  imports, slot scan, 8 ms `PrecisionTicker` poll, neutral on disconnect, rumble, button table,
  `Axis()`, 1 s join. **Change:** call `StandardControllerSource` with an `oem` callback and a slot
  predicate (`Discover(Func<ushort,ushort,bool> accepts)`). About 300 lines.
- **LH-7. Four HID descriptor decoders, hid.dll imported five times.** `LegionGo/LegionDInput.cs`,
  `MsiClaw/HidDescriptorGamepad.cs`, `Internal/Windows/StandardControllerHidSource.cs`,
  `StandardControllerTouchpad.cs`. `LegionDInput` and `HidDescriptorGamepad` are the same class.
  `HidP_GetCaps` is in both `NativeHid.cs` and `HidDevices.cs:496-511`. Hat switch 3×, normalization
  4×. **Change:** all imports in `NativeHid`; one `HidUsageReader` (`TryValue`, `Usages`, `Hat`,
  `Signed`, `Unsigned`) built from `HidDevices.Inspect`; families keep button maps. About 300 lines.
- **LH-8. OEM button sources repeat one HID reader lifecycle** (`Generic/TecnoButtonSource.cs`,
  `Zotac/ZotacWheelSource.cs`, `Gpd/GpdWin5OemSource.cs`, `OneXPlayer/OneXOemSource.cs`,
  `StandardControllerTouchpad.cs`, about 60 lines each). The "enumerate, filter, require exactly one"
  lookup appears 10 times; `LegionHid`, `OneXOemSource`, `OneXVendorTransport`, `AllyHid` use
  `FirstOrDefault` instead. `OneXOemSource.cs:176,206-230` rebuilds `OemButtonLatch` with its own
  timer. `Ayaneo/AyaneoKunMouseSource.cs` (232) re-implements `LowLevelKeyboardHook` for
  `WH_MOUSE_LL`. **Change:** `HidDevices.Unique(...)`, a small `HidReportSource` (start, stop,
  `Action<ReadOnlySpan<byte>>`), OneX on `OemButtonLatch`, generalize to `LowLevelHook(hookId, ...)`.
- **LH-9. ASUS duplicates.** `Generic/AsusXgTransport.cs:33-40,127-160` is a second ATKACPI client;
  use `AsusAcpiProtocol`/`WindowsAsusAcpi` with new `AsusAcpiId.XgConnected`/`XgEnable`.
  `Generic/GenericAsusService.cs:145-160,225-260,385-398` re-implements charge limit, scenario mode
  and fan curve that `AllyChargeLimitCapability`, `AllyPowerCapability.ApplyScenarioAsync`,
  `AllyFanCapability` and `AllyModels.ScenarioValue/Name` already provide. `Generic/
AsusAuraProtocol.cs:58-118` duplicates `RogAlly/AllyProtocol.cs:132-190`; share one
  `AuraProtocol` with report id/length (fixes LH-R3).
- **LH-10. Command plumbing copies.** Admission written 5 times and drifted
  (`ClawDeviceEngine.Commands.cs:261-330`, `AllyDeviceEngine.Commands.cs:208-262`,
  `ProfiledDeviceEngine.cs:166-193`, `LegionGoDeviceEngine.Commands.cs:13-41`,
  `AsusAuraDeviceEngine.cs:76-110`; Claw uses `Deadline.HasExpired` and lets null through as an
  action, others use `DeviceWriteBudget` and reject). "Dispatched means Indeterminate else Rejected"
  catch 10 times. Observed-value to `CapabilityState` projection 7 times with drift (Zotac `:49` and
  Legion `:268` stamp `ObservedAt = UtcNow` on every projection; Ayn `:56` reports Observed for null).
  Lost-reader handling identical in `ClawControllerService.cs:274-347` and
  `AllyControllerService.cs:437-491`. Descriptor factories copied in about 15 files, `Label()` 11
  times, fan curve descriptor 4 times. **Change:** `CommandAdmission.TryAdmit(...)`,
  `CommandResults.TransportFailure(command, dispatched, ex)`, an `ObservedValues` store in
  `Internal/Devices`, a `DeviceReconnect.ReaderLost(...)` helper, static `CapabilityDescriptors`
  factories.
- **LH-11. Platform helper copies.** Machine identity read 4 ways (`MsiWmiPlatform.cs:498-560` repeats
  `WindowsMachineIdentity.Read`; `AllyIdentity.cs:44-76` and `LegionWmi.cs:128-152` are identical
  registry SMBIOS readers). Hex VID/PID parsing 3×, `GetSystemPowerStatus` 3×, physical identities
  built 7×. `CreateFile`/`DeviceIoControl` imports 5× (`AsusXgTransport`, `AsusAcpi`,
  `SteamDeckHardware`, `PawnIoModule`, `UsbPortIdentity`). No `HidDevices.GetFeature`, so
  `GpdControlsProtocol.cs:307-313` and `NeptuneDevice.cs:225-229` import their own. Write-then-match
  reply 3× (`AyaneoSuperJoy.ExchangeAsync:42` with an 8-reply cap, `WindowsHidTransports.
ReadMatchingAsync:253`, `GpdWin5OemSource.StartAsync:30-45`). WMI first-instance plus
  `InvokeMethod` 4× (`LegionWmi.cs:105-125`, `OneXWmiEc.cs:14-20`, `ZotacNativeService.cs:62,169-178`
  without timeout, `MsiWmiPlatform.cs:180-260`). Pinned-payload hash check 2×
  (`SteamDeckHardware.cs:121-133`, `IntelPowerTransport.cs:70-79`). Mutex names as 14 literals.
  **Change:** `SmbiosRegistry.Read()`, `UsbIds.Parse`, `PowerSource.IsOnAc()`,
  `PhysicalDeviceIdentity.FromNode`, `NativeDevice`, `HidDevices.GetFeature/GetInputReport/
ExchangeAsync`, `WmiProvider.TryOpen/Invoke`, `PinnedPayload.OpenHeld`, `HardwareBus` constants.

### Simplification (local)

- **LH-12. Unused public API.** `HandheldDevice.Controls.cs` (485), `Contracts/DeviceOperations.cs`
  (164), `CreateCurrent`/`DetectCurrent`/`ReadCurrentIdentity`/`Definitions`, the library-owned
  generation overloads and their mixing guard (`HandheldDevice.cs:233-238,315-320,443-447`), the
  `_states`/`_descriptors` cache. Only library tests call them. About 700 lines. Decision 5.
- **LH-13. Legion fan curve loop** (`LegionGoDeviceEngine.Surface.cs:191-209`) evaluates
  `CurveAt(_fanCurve, temperature)` per tick and writes EC 0xC6C8. HC keeps curves as policy
  (`PowerProfileManager.cs:204`) and devices implement `SetFanDuty`. Profiled EC families expose
  `FanDuty` but never receive WSGM curves. **Change:** library exposes `FanDuty` (and native
  `FanCurve` where firmware has one); WSGM runs one software curve for every family. Decision 6.
- **LH-14. Glyphs and family ids.** `HandheldDevice.Glyphs.cs:7-150` hand-lists about 140 resource
  names and switches on `wsgm.device.msi.claw`/`wsgm.device.asus.rog-ally` although
  `HandheldDefinition.GlyphProfileId` exists. Four id schemes (`wsgm.device.*`,
  `libhandheld.lenovo.legion-go`, plain `gpd`/`zotac`). **Change:** derive glyphs from the manifest
  prefix `LibHandheld.Families.<Folder>.glyphs.`, one id scheme. Decision 4.
- **LH-15. `SteamDeckDeviceEngine.cs`** (96 to 120) forwards ten methods so haptics reach
  `SteamDeckNativeService.Rumble`, and passes an `openEc` lambda that throws. **Change:** virtual
  `ApplyHapticOutput` on `ProfiledFeatureService`, optional fan.
- **LH-16. Shared profile engine knows family strings.** `ProfiledDeviceEngine.cs:310,318,338,427`
  filters by `"controller"`, `"oem"`, `"touchpad"`; `ProfiledDeviceModel.cs:31` defaults
  `NativeOemReplacesChords` to GPD's `["l4","r4","gamepad"]` and every other source overrides it to
  `[]`. **Change:** `RequiresControllerOwnership` on the service, default `[]`. Also: no
  `PowerPresets` on `ProfiledDeviceModel`, so only Claw, Ally and Legion can declare presets while
  HC has device presets for AYANEO, Zotac, OneXPlayer X1, Minisforum V3 and a default.
- **LH-17. Ally keeps three "last written" caches** (`AllyDeviceEngine.cs:44` `_written`,
  `AllyAcpiCapabilities.cs:58-90` `_written`/`Effective()`, `FanCapability.WrittenCpu`) and
  `AllyDeviceEngine.Surface.cs:154-159` special-cases power and fan curve. One engine-level map.
- **LH-18. Outcome fields nobody needs.** `RollbackResult` has four values, only `NotRequired` is
  produced (21 sites). `CommandResults.Verified` puts the requested value into `ReadbackValue`
  (`GpdNativeService.cs:184`, `ClawCapabilities.cs:23`, `LegionGoDeviceEngine.Commands.cs:304`).
  `PluginStopReason`/`DeviceStopReason` (8 values) and `HandoffScope` are never read by an engine.
  **Change:** drop `Rollback`, `ReadbackValue`, stop reason and handoff scope; merge Verified and
  Unverified into one Applied outcome across library, Contracts and WSGM. About 180 lines.
- **LH-19. Small remnants.** `IDeviceSink.CycleGeneration` exists only for the X-2 throw guards;
  `PluginDiagnostics` wraps one dictionary; `SourceOwnership.Plugin = "plugin"` is a published
  choice value (`CapabilityIds.cs:68`); `PlainText` bidi/control screening of the library's own
  labels was third-party plugin sandboxing.

### Efficiency

Hot or continuous paths (rule: allocate nothing, log nothing per sample):

- **LH-E1. Legion queries XInput capabilities per input report** (`LegionHid.cs:377`, and per rumble
  frame at :209). Validate the slot once at start, re-find only when `StateEx`/`SetState` fails.
- **LH-E2. IMU reads allocate an RCW per report** (`LegacyMotionSensors.cs:479,574` poll mode, about
  500/s at 2 ms; `LegacyMotionSensors.Events.cs:276` event mode). Declare `GetData(out nint)` and
  call through vtable function pointers, then `Marshal.Release`.
- **LH-E3. IMU poll loop formats an error string every 2 ms while reads fail**
  (`LegacyMotionStream.cs:151-161`, strings at `LegacyMotionSensors.cs:482,562,706,752`). Return a
  code plus HRESULT, format once at the trace site.
- **LH-E4. OneX serial OEM reader throws `TimeoutException` every 100 ms while idle**
  (`OneXOemSource.cs:100-104`, `ReadTimeout = 100` at `OneXVendorTransport.cs:149`) and pins a pool
  thread. Use `DataReceived` like HC's `SerialUSBIMU`, or `BaseStream.ReadAsync` with the stop token.
- **LH-E5. OneX pulse timer runs at 50 Hz all session** (`OneXOemSource.cs:208`). One-shot timer
  armed from `Edge()`.
- **LH-E6. Ally XInput threads convert `ValueTask` to `Task` per sample** (`AllyInput.cs:473,479`).
  Free with WSGM's synchronous observer; use `IsCompletedSuccessfully` first. Disappears with LH-6.

Lifecycle, reconnect and publication paths:

- **LH-E7. `HandheldDevice.StartAsync` re-reads identity through five sequential WMI queries**
  (`HandheldDevice.cs:256`, `WindowsMachineIdentity.cs:21-31`), including a full
  `Win32_PnPEntity LIKE 'USB\VID_%'` scan no detection reads, uncancellable inside WSGM's 15 s start
  budget. Read registry SMBIOS or accept the caller's snapshot (WSGM already has
  `DeviceMachineIdentity.Collect`). Folds into LH-3.
- **LH-E8. Claw re-reads identity on start, resume and each controller-management enable**
  (`ClawDeviceEngine.cs:118,282,362`, `MsiWmiPlatform.cs:332-440`): three SMBIOS WMI queries, a
  `Win32_PnPEntity` scan, `Get_WMI`/`Get_EC`, a HID enumeration and AC status, all sequential, on the
  wake path. Keep `_cycleIdentity` for controller toggles, read endpoints once, run independent reads
  concurrently.
- **LH-E9. Legion builds a fresh WMI searcher per method call** (`LegionWmi.cs:107-121`), about 7 per
  10 s refresh plus each command. Cache the `ManagementObject` like `OneXWmiEc`, Zotac and
  `MsiWmiPlatform`.
- **LH-E10. `HidDevices.PresentNodes` walks every device class with per-node allocations**
  (`HidDevices.cs:143-181`, `MatchesProduct` :97-104, `ReadPhysicalLocation` :449-479): a
  `StringBuilder(1024)`, instance string, closure and formatted `VID_xxxx&PID_yyyy` per product per
  node. Callers: Ally reconnect poll every 500 ms, LH-R4's supervisor, and
  `StandardControllerSource.Discover` once per companion id (:60,:95). Format search strings once,
  reuse buffers, filter `SetupDiGetClassDevs` by enumerator.
- **LH-E11. Claw mode switch re-enumerates HID every 50 ms** (`WindowsHidTransports.cs:167-182`).
  Wait for an interface-arrival notification or use a path-only check.
- **LH-E12. Publication overhead.** States converted twice with fresh `Array.AsReadOnly` wrappers
  even for empty curves (`HandheldConversions.cs:49-93`, `HandheldDeviceAdapter.cs:61-108`);
  `HandheldDevice.PublishCapabilityStateAsync` (:132) does a closure plus linear `Any` per state
  (quadratic per pass). Use a static empty list and a descriptor `HashSet`; mostly gone with X-1.

## LibGPUDriverInteract

### Rule conflicts and decisions

- **GPU-R1. Support probe writes at discovery** (`GPU/Runtime/DriverRuntime.cs:550-624`
  `CheckSupport`, `GPU/Intel/IntelDriverEngine.cs:479-555` `BuildAsync`, `GPU/Nvidia/
NvSession.cs:266-285`, `ProbeSupport`/`SupportKey` overrides in 12 files). Writes without user
  action and hides controls on failed reads. The cache is per engine and WSGM creates a new
  `GpuDriver` per start, so it repeats every launch; on NVIDIA each probe costs two DRS
  `LoadSettings` plus `Set` (plus `SaveSettings`) for about 42 settings, inside the 5 s budget of
  `src/WSGM/Shell/BuiltinGpuService.cs:146`, and a timeout retires the driver. **Change:** publish
  what the driver support queries report (ADLX supported slot, NVAPI `EnumAvailableSettingIds`/
  `Values`, IGCL caps); a refused user write returns Rejected. About 200 lines. Decision 3.
- **GPU-R2. Written value snaps back on AMD and NVIDIA.** Intel keeps the written value until the
  driver reports something new (`_written`, `IntelDriverEngine.cs:668-680`); the shared runtime
  publishes it once and the next 10 s read overwrites it. Fold into GPU-1.

### Structural

- **GPU-1. Two complete driver engines.** `GPU/Intel/IntelDriverEngine.cs` (1,062) and
  `GPU/Runtime/DriverRuntime.cs:107-703` both implement a `SemaphoreSlim` lane, 10 s
  `PeriodicTimer`, stop-without-unloading (`_closePending`/`ReleaseLane` vs `_retiring`/
  `ReleaseLaneAsync`), the same `ExecuteCommandAsync` steps with near-identical rejection strings
  (Intel 104-190, runtime 135-253), `_supportResults`, SHA-256-of-JSON fingerprint
  (`IntelModel.cs:56`, `DriverRuntime.cs:519`), 20 s republish, empty-set retraction and health
  dedup. Only Intel has reopen backoff and the written-value rule. **Change:** Intel becomes an
  `IDriverSession` (open, `BeginPass`, `Discover`, `Sync`) under `DriverRuntime`; `IgclSession` and
  `IntelModel` already provide the pieces. The runtime owns the written-value rule. 500 to 600 lines.
- **GPU-2. AMD and NVIDIA rediscover everything every 10 s** (`DriverRuntime.cs:485-488`,
  `GPU/Amd/AdlxSession.cs:97-177`, `GPU/Nvidia/NvSession.cs:38-132`). AMD re-enumerates GPUs and
  displays and recreates 30 to 40 ADLX COM objects and every closure and descriptor. NVIDIA reloads
  the DRS database, re-enumerates GPUs, runs `DisplayTopology.CaptureActive`, re-probes color,
  dither and HDR per output and does about 42 extra `Get`s. Each result is fingerprinted by
  reflection JSON plus SHA-256. When a driver is unusable the runtime reopens it every pass with no
  backoff (`NativeLibrary.Load`, ADLX/NVAPI init, DRS `CreateSession`, `LoadSettings`, throw, free),
  while Intel has its own backoff (`IntelDriverEngine.cs:37,929-939`). **Change:** discover once per
  session, read-only passes; re-discover on `GpuDriver.RefreshTopologyAsync()` called from WSGM's
  `OnDisplayTopologyChanged` (`src/WSGM/Shell/ShellSession.SteamUi.cs:62`); reopen on that signal or
  the next command. Drop both backoffs.
- **GPU-3. Four layers of retirement bookkeeping.** `GpuDriver` (`_retirement`,
  `_retirementRevision`, `_startupRevision`, `_disposal`, `EnterLifecycleAsync` loop), the engines,
  `src/WSGM/Shell/BuiltinGpuService.cs` (`Entry.Retirement`, `Clean`, `RestartRequested`, reconcile
  continuation), and `GpuDriverAdapter.CloseAdmission` (stop with cancelled token, then `Retire`
  stops again, then dispose). **Change:** after GPU-1, `GpuDriver` alone owns "never unload under a
  native call"; `Retire` awaits dispose and logs. About 60 lines in WSGM.
- **GPU-4. Built-in drivers modelled as multi-instance plugins** (`src/WSGM/Core/
BuiltinGpuDrivers.cs:27-45`). Two `GpuDriver`s on one physical driver would both write it.
  `BuiltinGpuDrivers.Contains` exclusions in 8 places (`CommonPluginEnablement` ×2,
  `PluginPackageCatalog`, `SettingsViewModel.Plugins` ×3, `WsgmSteamSettingsService` ×2),
  `PluginInstanceIdentity`, `CommonPluginInstanceConfig`, the `PluginState/<id>/<sha(instance)>`
  formula copied from `CommonPluginManager.cs:357`, instance-suffixed names in
  `SettingsViewModel.Graphics.cs`, hard-coded `OpenBuiltin` role list (`GpuCoordinator.cs:191`).
  **Change:** one `Enabled` per vendor, fixed state directory, keep `wsgm.gpu.*` ids. 60 to 80 lines.
- **GPU-5. Vendor detection lives in WSGM** (`src/WSGM/Core/BuiltinGpuDrivers.cs:16-18`,
  `BuiltinGpuService.cs:83-86`) while the backends filter vendors again (`AdlxSession.cs:117,145`,
  IGCL `0x8086`). Expose `GpuDriver.DetectVendors()`.

### Local

- **GPU-6. Dead lifecycle members.** `IDriverEngine.SuspendAsync`/`ResumeAsync`/
  `SessionChangedAsync` (`DriverContracts.cs:10-12`, `IntelDriverEngine.cs:284-337`,
  `DriverRuntime.cs:336-363`) are called only by tests. `GpuDriver.ResumeAsync` equals `StartAsync`,
  `SuspendAsync` is Stop plus a health line. Collapse to Start/Stop. About 100 lines. The comment at
  `IntelDriverEngine.cs:255` ("resolved once for the life of the plugin") is no longer true.
- **GPU-7. Republish every 20 s is dead mechanism.** `DriverRuntime.cs:631-632`,
  `IntelDriverEngine.cs:31-34,736` justify it by "WSGM's router expires a generic observation after
  30 seconds", but `src/WSGM/Shell/DeviceCapabilityRouter.cs:1098` only ages Telemetry and
  FanMeasuredRpm. Each republish costs a lock and scan, `Enum.Parse`, a channel bump, a UI-thread
  snapshot rebuild and overlay refreshes. Publish on change only; delete `RefreshInterval` and the
  `At` fields.
- **GPU-8. Every write re-enumerates first.** AMD `Validate(target)` (`AdlxSession.cs:216-250`)
  re-lists GPUs/displays; NVIDIA `RequireOutput` (`NvApi.cs:381-401`) runs full `Displays()` twice
  per color, dither or HDR write (`NvOutputControls.cs:94/101,135,182/207`) and twice per probe
  (81/83, 169/171). Drop after GPU-2, or at least call once.
- **GPU-9. NVIDIA allocation and function lookups** (`NvApi.cs:95-123,238-243,270-288,506-512`):
  `Get` allocates 12,320 bytes twice per setting per pass (about 1 MB per 10 s), `Values` allocates
  414,112 bytes on the LOH per setting at discovery (about 17 MB), `Function(id)` calls
  `nvapi_QueryInterface` per native call, `NvColorControl.Read` reissues the color GET per field.
  Session-owned buffers, cached function pointers, one color read per output per pass.
- **GPU-10. Duplicate descriptor builders and control bases.** `GPU/Runtime/DriverDescriptors.cs` vs
  `GPU/Intel/Controls/Descriptors.cs` (Intel `Label` trims, runtime does not; Intel read-only is
  Volatile, runtime is DevicePersistent). `DriverControl.Accepts` (`DriverRuntime.cs:78`) vs
  `IntelControl.Validate` (`IntelControl.cs:235`). Keys `cap/inst` vs `cap|inst`. One builder with
  `Placement`, one validation, one key helper. About 100 lines.
- **GPU-11. Intel display identity re-implements WindowsDeviceControl** (`GPU/Intel/Display/
DisplayIdentity.cs:56-149`: own `DisplayConfigGetDeviceInfo`, structs, EDID flags) where
  `DisplayTopology.CaptureActive()` already returns `DisplayTargetIdentity` and NVIDIA uses it
  (`NvApi.cs:351`). Match on (LUID, TargetId), keep `InstanceId(...)` so saved ids stay. About 70
  lines.
- **GPU-12. Logging and helpers.** `GPU/Intel/IntelLog.cs` (94) wraps the sink in try/catch that
  `GpuDriver.Log` already provides; trace and health are deduped twice (`IntelControl.TraceDue` then
  `Sink._changes`; `IntelDriverEngine.Health` :942 then `Sink.ReportHealth`). Instance hashing
  `Convert.ToHexString(SHA256...)[..24]` 3× (`AdlxSession.cs:280`, `NvSession.cs:117`,
  `NvProfiles.cs:305`). System32 library loading 3× while Intel uses bare
  `NativeLibrary.TryLoad("ControlLib.dll")` (`IgclApi.cs:102`). Profile journals repeat one shell
  (`NvProfiles.cs:56-102`, `ApplicationProfileSynchronizer.cs:150-216`) including a revision gate
  `GpuDriver` already makes redundant. WSGM wording in library strings and comments
  (`IntelGraphicsMemoryTransport.cs:241`, `ApplicationProfileSynchronizer.cs:598`,
  `IntelDriverEngine.cs:32,579,590,615,763`, `AdlxSession.cs:184`); `NvProfiles.cs:304` DRS name is
  decision 4. **Change:** sink extension methods, `DriverIds.Hash`, `SystemLibrary.Load`, one
  `OwnershipJournal<T>` or no revision gate, neutral wording.
- **GPU-13. Dead members.** `GpuCommandOutcome.TimedOut`/`Accepted`, `GpuSectionKind.Other`
  (`GpuEnums.cs:168,171,213`) exist only for `Enum.Parse`; `DriverRuntime.Id` (:132) and
  `IntelDriverEngine.Id` (:88) are unread with a stale `<inheritdoc/>`; `SupportsAction` is derivable
  from `Role == Action`; `DriverRuntime` stores one sink as `_host` and `_capabilities`
  (:306,308); `INativeProfileTarget` has one implementation and no fake; WSGM never reads
  `GpuDriver.DescriptorSet`, `States`, `Status`, `CycleGeneration`, yet `_states` is maintained
  under a lock.
- **GPU-14. Small efficiency items.** WSGM starts vendors sequentially (`BuiltinGpuService.cs:
98-153`); `Task.WhenAll` helps Intel plus NVIDIA laptops. Intel walks the HKLM display class key
  twice per start (`IntelDriverEngine.cs:256,474`). The runtime uses two `Task.Run` hops per command
  (`DriverRuntime.cs:188,226`) where Intel's `WriteAndRead` uses one.

Not findings: `src/WSGM.Plugin.IntelGpu` and `AmdGpu` hold only untracked `bin`/`obj`;
`tests/WSGM.Plugin.*Gpu.Tests` are untracked and outside WSGM.slnx; `src/Shared/Gpu` is gone; Intel
color math and NVIDIA color tables are different domains; AMD "v"+decimal and NVIDIA "v"+hex choice
encodings are stored in profiles and should stay. No sleeps, delays or automatic write retries exist
in the GPU library.

## WSGM side only

- **W-1. Device Lab still carries the device-package pipeline.** `src/WSGM.DeviceLab/Testing/*`,
  `Packaging/PluginPackageWorkflow.cs`, `SyntheticPluginFixture.cs` (about 4,600 lines) test and
  pack `IDevicePlugin` DLLs WSGM can no longer load. Removing them frees most of
  `WSGM.Plugin.Sdk/Shared` (X-1).
- **W-2. Legion control id in WSGM.** `src/WSGM/Shell/DeviceOemActionRouter.cs:405` matches
  `"touchpad-secondary-click"`, emitted only at `LegionHid.cs:611`. Add a semantic default-action
  hint on `OemControlDescriptor` beside `CompanionApplication`.

## Suggested order

1. Pure deletions, no behaviour change: X-3, X-4, LH-18, LH-19, GPU-6, GPU-7, GPU-13, plus LH-R5
   (restores logs) and LH-R4 (stops log spam).
2. Rule fixes: LH-R2, LH-R3, LH-R1 (decision 2).
3. Hot paths: LH-E1 to LH-E5, GPU-9.
4. Boundary collapse together: X-1 and X-2, then LH-3, LH-2, GPU-3, GPU-4.
5. Shared helpers: LH-6 to LH-11, GPU-10 to GPU-12.
6. Engine consolidation last: GPU-1 with GPU-2, then LH-1 (Legion first, hardware-tested Claw and
   Ally last), then LH-4.

Rough total: 9k to 11k lines in LibHandheld, 6k to 8k in WSGM, about 1k in LibGPUDriverInteract,
not counting tests.

# Part 2: Correctness

The Claw and Ally migration itself is complete: engine code, glyph assets, provenance, definition and
family ids moved one to one, journals keep their directory, file and format, so an unfinished 2.1.0
entry still replays. Neither old package had user preferences. The losses are in the layers added
around the engines. Ranked most severe first within each group.

## Critical

- **C-1. Every desktop and laptop is a supported handheld.** `LH/Families/Generic/GenericModels.cs:134`
  `MatchFallback` returns `generic-amd`/`generic-intel` for any Ryzen, Athlon or Intel Core CPU, so
  detection almost never returns null. Setup announces support, preselects Full, installs PawnIO as a
  fatal step; WSGM starts a cycle with 10 to 25 W bounds and SMU/KX writes and can cap a 170 W
  desktop CPU at 25 W. The passive path becomes unreachable and every desktop gets the prerequisite
  banner.
- **C-2. AMD limits are "read" by sending SET commands with 0.**
  `LH/Internal/Transports/AmdSmuTransport.cs:39` `ReadLimits` sends SET_STAPM/SLOW/FAST with argument
  0 on the 10 s tick, after every TDP command, at capture and at restore verification. Either the TDP
  is reset to the minimum, or reads return 0 and originals are never captured. HC's equivalents are
  dead code.
- **C-3. One driver step failure rolls back the whole install.**
  `src/WSGM.Setup/Engine/SetupEngine.cs:441` makes PawnIO and InpOut fatal (HidHide and USB/IP are
  not). Triggers: PawnIO older than 2.0, a build without `artifacts/pawnio/PawnIO_setup.exe`
  (`WSGM.Setup.csproj:54` embeds it only if present), InpOut blocked by Memory Integrity or the
  vulnerable-driver list. With C-1 this applies to every PC.
- **C-4. AYANEO SuperJoy key map is off by one.** `LH/Families/Ayaneo/AyaneoSuperJoy.cs:131` puts the
  modifier at [9] and key at [11]; HHD uses [10] and [12]. "Configure OEM controller keys" writes all
  33 slots and saves (0x05), durably corrupting the AYANEO 3 module table. The fixture asserts the
  wrong layout.
- **C-5. FLIP DS secondary brightness clears EC 0xFF instead of 0x4F.**
  `LH/Families/Ayaneo/AyaneoNativeService.cs:267`. Writes a register HC never touches; the real
  prefix is never written.

## Device writes and restoration

- **D-1. AsusAura wrapper breaks stop.** `LH/Internal/Devices/AsusAuraDeviceEngine.cs:181` waits on
  its own `_commands` in Stop/Suspend before the inner engine, defeating the inner quiescing. A stop
  deadline during that wait skips Claw restoration and journal release; WSGM never retries. (Root
  cause LH-2.)
- **D-2. AsusAura hides failed restores.** `AsusAuraDeviceEngine.cs:171`: on start or resume failure
  it runs its own 5 s stop with `CancellationToken.None`, discards the result, and the host's later
  stop gets Clean. Also triggered when accessory acquisition throws after a good Claw start.
- **D-3. AsusAura dispose writes hardware.** `AsusAuraDeviceEngine.cs:246` always calls inner
  `StopAsync(WsgmExiting, Deadline.Never, None)`, against the Claw/Ally rule that dispose only
  releases handles; it can hang on a stuck HID/EC write and overwrites state left for journal
  recovery.
- **D-4. AsusAura races and polls under the command lock.** `:359` enumerates accessory descriptors
  and states under `_publications` while the loop mutates `_modules` under `_commands` (throws or
  tears). `:418` runs a 2 s SetupDi walk plus feature probes holding the command semaphore on every
  device including the Claw; acquisition uses `Deadline.Never` and a throw fails the Claw cycle; each
  start publishes two generations so early commands get GenerationChanged.
- **D-5. Aura accessories duplicate lighting roles on Ally and Claw.**
  `LH/Internal/DeviceFamilies.cs:91`: with an XG Mobile or Aura keyboard a second
  LightingBrightness/Effect/Speed appears; QAM brightness requires exactly one match and disappears;
  restore sees two owners.
- **D-6. Steam Deck foreign-firmware guard is bypassed.**
  `LH/Families/SteamDeck/SteamDeckNativeService.cs:172`: the catch calls `ReleaseControllerAsync`,
  which writes the pending original from other firmware to registers 9, 71, 48 and may mark it
  RestoredVerified.
- **D-7. OneX X2/OXP3 takeover writes the whole EC 0x04EB.**
  `LH/Families/OneXPlayer/OneXNativeService.cs:166` writes `(prior ?? 0) | 0x40` and restores 0 when
  the read failed. HC does a live read-modify-write of one bit.
- **D-8. Legion Go 2 fan override not released.**
  `LH/Families/LegionGo/LegionGoDeviceEngine.Commands.cs:408` restores 0xC6C8 only if the original
  was readable; HC always writes 0 on close. The fan stays pinned until reboot.
- **D-9. Profiled Intel rejects split limits.** `LH/Internal/Devices/ProfiledDeviceEngine.cs:229`
  writes `(sustained, boost, boost)` but `IntelPowerTransport.WriteLimits` throws when Slow differs
  from Sustained, after `dispatched()`, so every 15/25 W setting on GPD, OneX, AYANEO, generic-intel
  and asus-intel is Indeterminate though nothing was written. `IntelPowerTransport.cs:32` also reads
  Slow as PL1, so boost is published as 15 W while PL2 runs at 25 W.
- **D-10. AYANEO eject cuts power after a failed eject.** `AyaneoNativeService.cs:622` writes
  0x2D=0xFE in `finally`; `_ejectionPending` stays true; the AYANEO 3 controller stays dead.
- **D-11. Legion restores limits after mode.** `LegionGoDeviceEngine.Commands.cs:448` writes
  SmartFanMode then the four limits, which can flip firmware to Custom (255). HC never restores
  limits.
- **D-12. Legion lighting caches update before the write.** `LegionGoDeviceEngine.Commands.cs:222`;
  a failed brightness is re-sent inside the next colour or effect command (automatic retry of an
  uncertain write).
- **D-13. GPD rewrites WinControls EEPROM on every acquire and release.**
  `LH/Families/Gpd/GpdNativeService.cs:128`; HC never writes it, and the V1 path ends with 0x23
  (commit and restart), so every start, stop, suspend, resume and toggle re-enumerates the pad and
  wears the EEPROM, including on unknown GPDs via the fallback.
- **D-14. Zotac VRAM encoding overflows.** `LH/Families/Zotac/ZotacNativeService.cs:277`
  `(ram + addition) * 4` into a byte (HC's bug); 64 GB wraps to 0.
- **D-15. Zotac lighting reloads the profile per command.** `ZotacNativeService.cs:195` sends 0xF1
  and reopens; a reopen timeout reports Rejected after dispatch and leaves `_hid` null for the
  session. HC does not reload.
- **D-16. AMD SMU on KrackanPoint2.** `AmdSmuTransport.cs:235` enables STAPM/fast/slow for codename
  34 where HC returns CanSetTDP false. `:140` falls back to PSMU only on rejection, never on timeout
  or to `ioctl_send_smu_command`.
- **D-17. Intel needs PawnIO and holds the PCI mutex around KX.**
  `LH/Internal/Transports/IntelPowerTransport.cs:82` requires PawnIO just to read MSR 0x606 (HC needs
  only KX); `:192` holds `Global\Access_PCI` while launching 3 to 4 KX.exe processes (10 s each),
  blocking LHM, HWiNFO and RTSS and possibly deadlocking. `LH/Families/SteamDeck/
SteamDeckHardware.cs:332` holds the same mutex up to 20 s per SMU exchange.
  `IntelPowerTransport.cs:72` hard-codes WSGM's `Resources/` layout; the InpOut digest is duplicated
  in `SteamDeckHardware.cs:23`, `InpOutInstaller.cs:223` and `inpout.lock.json`. `:104` has unused
  `ReadSnapshot`/`Restore`/GPU clock/undervolt code while the real restore writes HC's fixed
  time-window constants.
- **D-18. Steam Deck HC divergences.** `SteamDeckNativeService.cs:147` writes lizard-mode settings
  once; HC clears mappings, re-asserts every second and restores defaults (double input if the
  watchdog re-enables). `SteamDeckNativeService.Hardware.cs:14` omits HC's `PDCS == 0` check and
  writes the charge ceiling as 1 byte (HC 4). `SteamMachineLighting.cs:160` calls `ioctl_init` only
  before brightness changes.
- **D-19. Other HC divergences.** `PawnIoModule.cs:52` throws on short returns (HC and LHM accept).
  `UsbPortIdentity.cs:44` requires ACPI `_UPC` non-connectable on every hub port for unanchored
  XInput pads (HC uses VID/PID). `OneXNativeService.cs:381` waits 50 ms between lighting sides (HC
  200 ms for HidV1X2); `:121` vendor-button remap/intercept pages and the 0x20/0xFE resend are never
  sent; `:578` X1 preset 0x14 missing; `OneXCommandCodec.cs:43` brightness thresholds differ.
  `OneXOemSource.cs:30` lets serial open failures propagate instead of falling back.
  `AyaneoNativeService.cs:400` KUN zone 3/4 channel order; `:692` charge-limit threshold replaced by
  a raw bypass toggle; `:131` LEDs reapplied only on 10 s AC-to-DC polls. `AyaneoModels.cs:99` FLIP
  KB and 1S KB skip confirmation; `:129` uses `SingleOrDefault`. `AynNativeService.cs:174` Loki LED
  omits the 0xB3=0xAA trigger. `OneXPlayerModels.cs:63` Mini requires V01/1002-C/V03.
  `GpdModels.cs:112` Win Max 2 requires an exact CPU; `:54` Menu chord (VK 0x07) missing.
- **D-20. Torn fan RPM.** `ProfiledHardwareServices.cs:281` reads high and low bytes in separate EC
  transactions.

## Journal and readback lockouts

Rule: never gate a write or a control on readback. All stem from per-family journal handling (LH-4,
LH-R1).

- **J-1.** `OneXNativeService.cs:112`: a same-firmware RestoredUnverified or RestoreFailed entry sets
  `_ecUncertain` forever, disabling fan and turbo takeover. `:532`: a successful restore of an
  UnknownOriginal entry does the same for the session.
- **J-2.** `AyaneoNativeService.cs:75`: a non-Pending lighting-owner entry makes acquire Passive
  forever (bypass charging, lighting, secondary display, modules). `:85` throws when the original
  cannot be read and leaks the PawnIO EC handle.
- **J-3.** `SteamDeckNativeService.Clocks.cs:103` and siblings (deck-fan, fremont-init, Zotac
  rear-profile, ProfiledFanService without exact original, `AynNativeService.cs:221`,
  `GenericAsusService.cs:348`): RestoredUnverified entries are never removed, so every later stop
  reports Unverified and WSGM logs a cleanup failure on every shutdown.
- **J-4.** `ZotacNativeService.cs:95`: the M1/M2 remap runs only when no rear-profile entry exists;
  after the first stop it never runs again (HC remaps on every insertion).
- **J-5.** `SteamDeckNativeService.Hardware.cs:269`: foreign-firmware entries throw on every start;
  the profiled services discard them.
- **J-6.** `OneXNativeService.cs:105`, `GpdNativeService.cs:44`, `AyaneoNativeService.cs:65,102`,
  `AynNativeService.cs:70`, `GenericAsusService.cs:116`: foreign-firmware entries are marked
  RestoredVerified with no restore.
- **J-7.** `GpdNativeService.cs:194`: any exception, including a pre-dispatch read, faults the service
  for the session; a read failure in acquire leaks `_protocol`.
- **J-8.** GPU support probing hides controls on read failure (`GPU/Runtime/DriverRuntime.cs:563`,
  `IntelDriverEngine.BuildAsync` 494-506); see GPU-R1.

## Lifecycle, input and concurrency

- **L-1. Start re-detects with an uncancellable WMI reader.** `LH/HandheldDevice.cs:256`; at logon
  before WMI is ready it returns empty fields, detection fails, and a correct Ally or Claw cycle
  throws NotSupportedException and restart-loops. Setup's `HandheldSupport` uses a third identity
  mapping that drops USB, EC and MCU fields. (LH-3, LH-E7.)
- **L-2. Commands cancelled before dispatch become Indeterminate.** `HandheldDevice.cs:351`:
  `_commands.WaitAsync` and the wrapper's waits throw OperationCanceledException, which WSGM maps to
  Indeterminate; 2.1.0 returned Rejected(Quiescing).
- **L-3. Chord hooks swallow releases asymmetrically.**
  `LH/Families/Ayaneo/AyaneoKunMouseSource.cs:151` (KUN and every AYN Loki: left button stuck down,
  or Guide becomes a left click) and `LH/Internal/Windows/KeyboardOemSource.cs:75` (LWin stuck, or
  the Start menu opens). `KeyboardOemSource.cs:101` also swallows chord keys (HC uses
  silenced:false), drops silently when its 128-entry queue overflows, and allocates a closure and
  LINQ enumerator per keystroke in the hook.
- **L-4. Profiled engine duplicates services.** `ProfiledDeviceEngine.cs:95` clears `_services` only
  on a clean stop; after an Unverified stop a restart opens PawnIO/KX twice, two journals write one
  file, and diagnostics throw on the duplicate key.
- **L-5. Input supervisor faults.** `ProfiledInputServices.cs:179` calls `StopAsync` outside the
  inner try, so a reader-join timeout kills the supervisor for the session; `:63` `NeedsReconnect`
  requires `_nativeReady`, so a native OEM source absent at acquire is never retried (Zotac wheel,
  GPD WIN 5, OneX vendor). See LH-R4 for the 500 ms republish.
- **L-6. Legion fault flags.** `LegionGoDeviceEngine.cs:229` (also 460-462, `Surface.cs:217-219`)
  leaves `_hidFaulted` true when the await throws. `Surface.cs:212` reopens HID every tick when not
  reading, duplicating `BeginReconnect`. `LegionHid.cs:260` assigns `_slot` outside the `_output`
  lock. `LegionGoDeviceEngine.Commands.cs:296` refreshes and verifies after each command (18 or more
  WMI calls per TDP change, beyond the 2 s post-command limit).
- **L-7. Haptic and HID interop.** `StandardControllerSource.cs:177` throws Win32Exception on every
  haptic frame for up to 500 ms after disconnect. `StandardControllerHidSource.cs:199`,
  `StandardControllerTouchpad.cs:179` and `NativeHid.cs:11` marshal BOOLEAN HidD returns as
  `UnmanagedType.Bool` (HidDevices correctly uses U1). `ProfiledDeviceEngine.cs:469` blocks reader
  threads on `ValueTask.GetAwaiter().GetResult()`.
- **L-8. Publication gating.** `HandheldDevice.cs:145` lets physical-device, sample and OEM
  publications through after stop, suspend and dispose while filtering states; `StopAsync` clears
  controls before the engine's final unavailable states.
- **L-9. Small.** `HandheldDevice.Controls.cs:297` reports a later command's Rejected outcome after
  earlier commands applied. `ProfiledEngineSelector.cs:29` replaces `_engine` without disposing it
  and returns Clean when nothing was selected. `GpdWin5OemSource.cs:128` misses
  ObjectDisposedException; the doc says 1.11 but the check is 0x11 (1.17). `AynNativeService.cs:56`
  reports Observed for null. `ProfiledDeviceEngine.Surface.cs:90` rebuilds all native states per
  descriptor (400+ objects per Deck publish). `GenericAsusService.cs:141` runs HID discovery on each
  refresh and ATKACPI IOCTLs in the `States` getter. `AyaneoNativeService.cs:168` has a dead 2 s
  throttle. `AsusAuraProtocol.cs:46` has a dead assignment. `GenericModels.cs:295`: the AYN Loki
  depends on the Ayaneo family's private `AyaneoKunMouseSource` (move it to `Internal/Windows`).
- **L-10. Trace sink.** `LH/Internal/Plugin/PluginTrace.cs:44`: installed only by Claw and Ally and
  never uninstalled; WSGM's `PluginTrace.Install(null)` (`HandheldDeviceRuntime.cs:265`) targets the
  unused SDK copy, so the static roots the retired graph. (LH-R5.)

## LibGPUDriverInteract

- **G-1.** `GPU/GpuDriver.cs:546`: the sink swallows the consumer's refusal and engines mark the set
  published, so WSGM stays one generation behind and every GPU command is rejected with
  GenerationChanged until the model changes. (X-2.)
- **G-2.** `GpuDriver.cs:289`: a cancelled wait retires the whole driver; `BuiltinGpuService` (5 s
  budget) only logs, and on resume `Entry.Retirement` stays null so it never restarts. Slow IGCL or
  NVAPI init after wake removes all GPU controls until restart. `:276`: the shared startup task runs
  under the first caller's token.
- **G-3.** `GpuDriver.cs:175`: suspend is a full stop and resume a full start, so every sleep frees
  and reloads ControlLib.dll and rebuilds the memory transport, ColorStore and synchronizer (GPU-6).
- **G-4.** `DriverRuntime.cs:262`: AMD and NVIDIA sync return a success-shaped `(0,0,[])` on a lost
  session (Intel returns failure), so per-game DRS settings are skipped.
- **G-5.** `GpuDriver.cs:405`: a throwing engine stop leaves `_active` true with stale descriptors and
  skips engine dispose. `:446`: `BeginCycle` sets "starting" without `StatusChanged`. `:248`: a
  non-admitted sync is logged as a refused per-game override with empty fields.
- **G-6.** `src/WSGM/Shell/GpuDriverAdapter.cs:112`: every retirement stops each engine two or three
  times and Intel logs "The driver is still closing" on every shutdown (GPU-3). `:202` forces every
  VRR role to the generic label (AMD "FreeSync" lost) and every non-display section to the Gauge
  icon. `GpuCoordinator.cs:194` declares all six roles for every vendor (the old manifests were per
  vendor).
- **G-7.** `GPU/LibGPUDriverInteract.csproj:9` pins its own nested windows-device-control submodule,
  and an `Exists()` check picks WSGM's copy instead, so standalone and in-WSGM builds can use
  different commits.

## WSGM integration

- **W-3. GPU drivers default to enabled.** `src/WSGM/Core/BuiltinGpuDrivers.cs:34`: a 2.1.0 user who
  unchecked the GPU plugin has no instance entry and now gets GPU management and per-game driver
  profiles without consent. `BuiltinGpuService.cs:127` duplicates the state path hashing; Settings
  and Steam list all three vendor toggles regardless of adapters.
- **W-4. Writable controls land on the read-only Info page.** `HandheldDeviceAdapter.cs:716`:
  GenericToggle, Choice, Range, Action, Text, OemControl and VRR fall into "readings" (Legion
  touchpad and controller reset, AYANEO eject, Deck clocks, Zotac VRAM, GPD rumble, Aura toggles).
  `:693` rebuilds the Claw and Ally layout generically: the Claw Performance profile moves into
  Limits, controller, motion and rumble rows become Compact, "Plugin ownership" becomes "Device
  ownership"; `ClawPluginTests` and `claw-ui-publication.json` pin a layout production never
  renders. (X-4.)
- **W-5. The undeclared-role guard is gone.** `HandheldDeviceRuntime.cs:21` declares every
  LibHandheld role, so the router's guard never fires; the Ally test became an `Enum.IsDefined`
  tautology.
- **W-6. Prerequisite banner.** `src/WSGM/Shell/ShellSession.Actions.cs:95` re-collects identity per
  call, ignores `HasController`, PawnIO and InpOut, and nags users who declined integration. Use
  `SetupComponents.Required(definition)` and the coordinator's `DeviceDefinition`.
- **W-7. A glyph exception tears the cycle down.** `DeviceCoordinator.cs:3286` catches only IO,
  UnauthorizedAccess and InvalidData, but `OpenGlyphResource` throws InvalidOperation or Argument;
  2.1.0 only dropped glyphs. The allowlist in `HandheldDevice.Glyphs.cs:146` makes this likely
  (LH-14).
- **W-8. Weaker wsgm.log.** `HandheldDeviceAdapter.cs:528` drops the detection reason; passive is an
  Info line without identity or refusal code; the glyph catalog line is gone; diagnostics and AutoTDP
  traces report the constant library version 0.1.0 (`DeviceCoordinator.cs:225,3309`).
  `HandheldDeviceRuntime.cs:327` computes the state directory twice (they agree only because
  PackageId equals FamilyId) and `HandheldDeviceAdapter.Trace` drops everything before StartAsync.
- **W-9. Config ordering.** `ShellSession.Config.cs:350` awaits the built-in GPU reconcile (up to 5 s
  per driver) before common plugins in the same try; an exception skips the common-plugin reconcile.
- **W-10. Stale settings surfaces.** `SettingsViewModel.Plugins.cs:165` passes the always-null
  `InstalledDevicePluginId`, so the panel tells users to install a device plugin. Dead device-package
  plumbing around `PluginPackageCatalog.cs:121` (InstalledDevicePackage, DevicePackageDiscovery,
  DeviceCandidates, RecommendedDevice, NotForThisHardware, `BundleManifest.IsDevice`,
  `HardwarePage.NeedsChoice`, Device Lab pack, validate and test). (W-1, X-3.)
- **W-11. Right mouse button can stick.** `DeviceOemActionRouter.cs:447` holds the synthesized RMB
  until a release edge, config change or lifecycle transition. Unverified: depends on library
  behaviour on disconnect.
- **W-12. Plugin API admission.** `src/WSGM.Plugin.Sdk/PluginManifestReader.cs:111` now requires
  `Minimum == Maximum == PluginApi.Version`, so every future additive bump invalidates every plugin.
  A floor of 5 plus the old range check is enough.

## Setup, build, CI and licensing

- **S-1.** `src/WSGM.Setup/UI/Pages.cs:307`: the blind/untested caution and the Family match badge
  are gone; every match says "Built-in support, exact model" and promises controller management even
  for power-only definitions. `HardwareVerified` is not on `HandheldDefinition`.
- **S-2.** `src/WSGM.Setup/Engine/SetupEngine.cs:477`: PawnIO and InpOut are not recorded in
  `components.json` and not offered for removal at uninstall.
- **S-3.** `src/WSGM.Setup/Engine/GpuPackageRetirement.cs:15`: `ZipFile.OpenRead` IO errors escape
  and roll back the update; the type is misnamed (it also retires device packages); it omits
  `wsgm.device.msi.claw-8-a2vm`; the retired-id list is duplicated in `eng/dev-deploy.ps1` and
  `BuiltinGpuDrivers`.
- **S-4.** `src/WSGM.Setup/Engine/PawnIoInstaller.cs:283` duplicates Device Lab's `PawnIoSetup` and
  detects a restart by comparing `step.Note` with a literal.
- **S-5.** `build.ps1:155`: the Claw and Ally THIRD_PARTY_NOTICES (PromptFont OFL 1.1, victor-borges
  MIT), LICENSE and PROVENANCE are only embedded in LibHandheld.dll, not shipped as files.
- **S-6.** `LH/Internal/Transports/PawnIoModule.cs:79`: LibreHardwareMonitorLib is a full dependency
  only for embedded PawnIO module bytes; setup now loads LibHandheld, LHM and System.IO.Ports for one
  Detect call.
- **S-7.** `tests/WSGM.Tests/Boundaries/DeviceBoundaryTests.cs:39` asserts a single PackageReference;
  LibHandheld has three. The deferred test run will fail.
- **S-8.** `.github/workflows/ci.yml:21` disables CI by branch name; `.gitmodules` pins both
  libraries to `chore/device-integration-rework`; verify and release check out two private
  submodules. Remove before merging to master.
- **S-9.** `eng/pack-device.ps1:20` targets the deleted Claw project; `.editorconfig:150` scopes
  SYSLIB1054 to deleted files.

## Guidance and provenance

- **P-1.** `AGENTS.md:22` and the scoped guides were not updated: the root still lists
  WSGM.Device.Sdk and the Claw, Ally and HC packages; `Core/AGENTS.md:20`, Setup, DeviceLab and
  `Input/AGENTS.md:17` describe removed packages; the wsgm-device-sdk and device-lab skills point at
  deleted paths. The Claw (162 lines) and Ally AGENTS.md invariants and READMEs have no successor;
  lost rules include the 0x50/0x51 write order, the chord state machine, no INIT/WDOG ATKACPI ids and
  never re-sending controller tables. This breaks the "dissolved file is where losses hide" rule.
- **P-2.** `LH/Internal/Plugin/PluginContracts.cs:18` still uses WSGM package vocabulary
  (`plugin.wsgm.json`, "sole installed device package"); family ids are inconsistent (LH-14).
- **P-3.** `LH/Families/OneXPlayer/PROVENANCE.md:7` and the Ayaneo README:44 cite public HC `a250b19`
  instead of the decompiled 1.3.1.6 reference; several ported values differ from 1.3.1.6.

## Overlap with Part 1

These correctness findings are covered by a simplification item and are fixed by it: the three
contract copies and `Enum.Parse` mappers (X-1), generation layering (X-2, G-1), the dead settings
manifest (X-3), the label round trip and lost layout (X-4, W-4), the SDK device toolkit tested in
place of the shipped copy (X-1), dead GPU suspend paths and the tests' false confidence (GPU-6),
runtime reopen without backoff and 10 s rediscovery (GPU-2), WSGM wording in the GPU library
(GPU-12), duplicated HID interop (LH-7), Legion's per-report XInput capability query (LH-E1), OneX
and AYN fan and journal copies (LH-4), Legion's bespoke engine (LH-1), descriptor helper copies
(LH-10) and the glyph allowlist (LH-14).

## Suggested order for Part 2

1. C-1 to C-5 and D-1 to D-5 (the AsusAura wrapper) before anything else ships.
2. Rule violations: J-1 to J-8, D-12, L-2, W-3.
3. Remaining device writes and HC divergences: D-6 to D-20.
4. Lifecycle and input: L-1 to L-10, then G-1 to G-7.
5. Setup, CI and guidance before merging the branch: S-1 to S-9, P-1 to P-3.
