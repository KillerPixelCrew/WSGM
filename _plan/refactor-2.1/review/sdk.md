# SDK domain review: Device SDK, Plugin SDK, common plugin host, packages, GPU coordinator

Reviewer: Claude (read-only stage). Baseline: master `1329813f`, clean tree. No build, test, git mutation or live
action was run. Line numbers are current source.

Scope read in full: every non-generated file under `src/WSGM.Device.Sdk/**` (Capabilities, Glyphs, Identity, Input,
Lifecycle, Packaging, Plugin, Serialization, Services, Settings, Testing, Windows; `LegacyMotionSensors.cs` read for
lifecycle, sampling and fault paths, its COM declaration block skimmed), `src/WSGM.Plugin.Sdk/**`, both SDK test
projects, `Shell/CommonPluginManager.cs`, `Core/PluginPackageManager.cs`, `Core/PluginPackageFile.cs`,
`Shell/GpuCoordinator.cs`, the seven `plugin.wsgm.json` manifests, the Device Lab plugin template, and
`eng/plugin-manifest.cs` / `eng/package-plugin.ps1`. Read for boundary tracing: `Shell/PluginHost.cs`,
`Shell/CommonPluginPackage.cs`, `Shell/PluginPackageLoader.cs`, `Shell/PluginCapabilityChannel.cs`,
`Core/PluginPackageCatalog.cs`, `Core/CommonPluginEnablement.cs`, `Shell/DevicePluginCompatibilityAdapter.cs` (head),
and the relevant Claw/Ally/IntelGpu/Shared GPU call sites.

Ledger context: the Claude units for this domain (U14A-SDK, U14B-SDK, U06C-DVH) were never started
(audit-coverage.md lines 19, 37, 38). The only prior findings are Codex A02-F001..F009 and A02-F021, plus the PV04
static-field inventory in `claude-provisional-raw.json`. Almost everything below is therefore NEW.

---

## 1. Plan claims check

| # | Claim (source) | Verdict | Evidence and correction |
| --- | --- | --- | --- |
| C1 | "SDK `ActiveClock` owns an immortal process thread and pending cancellations" (refactor-plan.md:48) | accurate, framing partial | `ActiveClock.cs:28` static `Pending`, `:66` background thread started once. The thread being immortal is by design: freeze detection is a process property. The real defects are the unguarded callback path and the clock stalling behind a slow callback (SDK-001), and bounded retention (SDK-041). |
| C2 | "`PluginTrace` owns a shared mutable sink" (refactor-plan.md:48) | accurate, no production defect | `PluginTrace.cs:37`. Only one device plugin runs per process; the host clears it at unload (`DevicePluginRuntime.cs:133`). The only demonstrated cost is test serialization (`[Collection("plugin-trace")]` in 10 test files). See SDK-021. |
| C3 | "`Plugin.Sdk` really references Device.Sdk and SteamUiToolkit, contrary to its README's zero-dependency claim" (refactor-plan.md:48, A02-F002) | accurate | `WSGM.Plugin.Sdk.csproj:18-21`; README.md:13 "It depends on nothing". README.md:5 also says "Graphics", README.md:15 advertises a "compatibility adapter", README.md:40 says the reader is "bounded", which is false until A02_01. |
| C4 | Preserve `DeviceServiceLifecycle`, `DeviceCommandSerializer`, `DeviceRecoveryJournal` as useful owners (refactor-plan.md:75) | accurate, with defects | Both packages use all three. They carry defects: SDK-002, SDK-003, SDK-004, SDK-016, SDK-017. |
| C5 | Common GPU/IR plugins are admitted independently of Device Integration (refactor-plan.md:85) | accurate | `ShellSession.cs:298-303` constructs `GpuCoordinator` and `CommonPluginManager` unconditionally; `CommonPluginManager.cs:37-40`. |
| C6 | Shutdown order "stop device/common plugins; dispose GPU routers" (refactor-plan.md:91) | accurate as order, stale as budget | `ShellSession.Shutdown.cs:139-190` runs device, then common, then GPU on one deadline. `CommonPluginManager.StopAsync` (`:360`) throws `TimeoutException` from `Remaining(deadline)` before stopping any entry when the device stop consumed the budget. Already covered by U05-LFB-001 / B3; this domain only needs the active-time waits of SDK-004. |
| C7 | Replace global ActiveClock with an owned clock service, a clock-bearing `Deadline`, cross-clock refusal, `DeadlineCancellation`, clock shutdown join (refactor-plan.md:113, F02 brief steps 1-2) | inaccurate as a necessity; over-engineered | No defect requires per-instance clocks. 114 `Deadline.After` call sites in 39 files and every lifecycle context would gain clock plumbing, and a new "cross-clock" refusal state would be invented. The freeze-aware clock is inherently process-wide. Replace with SDK-B2 (contain callbacks, run them off the clock thread, prune cancelled entries, internal test core). Host shutdown budgets that need fake time are wall budgets (`Deadline.At`, `ShellSession.Shutdown.cs:154,170`) and should use `TimeProvider` in the host (H02), not a new SDK clock type. |
| C8 | "Preserve 250 ms observation and maximum 1 s counted freeze step" (refactor-plan.md:113) | accurate | `ActiveClock.cs:22,25`. |
| C9 | Replace `PluginTrace.Install` with instance `PluginDiagnostics` bound to host context (refactor-plan.md:113, F02 step 3) | inaccurate as a necessity | ~30 files in Claw, Ally and SDK helpers trace through the static, including static helpers (`LegacyMotionSensors.TryOpen`, `HidDevices` callers, `LowLevelKeyboardHook`, `MotionSampleBuilder`, `DeviceCommandSerializer`) that would need a diagnostics parameter threaded through. The SDK guide's own rationale is that instrumentation that costs plumbing does not get written. Keep the static for the single device plugin; give common plugins an instance channel on `IPluginHost` because they run several instances (SDK-020). |
| C10 | "Native callbacks contain exceptions" (refactor-plan.md:115) | correct rule, unmet in the SDK | `LowLevelKeyboardHook.cs:210-225` lets a handler exception escape the hook procedure; `LegacyMotionStream.cs:107-137` poll thread has no fault boundary (SDK-005, SDK-006). The plan's evidence list cites only WSGM window procedures. |
| C11 | High-rate samples/haptic frames are structs with no per-sample allocation or logging (refactor-plan.md:115) | partially accurate | Contract types are structs (`ContractBoundaryTests.cs:33-37`). The legacy sensor event path allocates one unique RCW per report (`LegacyMotionSensors.Events.cs:276`, inherent COM cost) and `MotionSampleBuilder.Build` traces under its lock on the per-sample path (`MotionSampleBuilder.cs:75,108,115`). |
| C12 | "consolidate duplicated lifecycle service scaffolding through the existing SDK helpers" (refactor-plan.md:149, DP01 step 1) | stale / partial | Device API 11 already moved the scaffolding into the SDK (`DeviceApi.cs:54-58`). What remains duplicated is the `ReconciliationBlockReason` short-circuit in every service (SDK-016) and the per-package journal decision logic (device domain, A02-F010). |
| C13 | "API becomes Device 12 / SDK 0.5.0 and common Plugin 4 / SDK 0.3.0" (refactor-plan.md:149) | baseline accurate; justification changes | Current: `DeviceApi.Version = 11` (`DeviceApi.cs:76`), Device SDK 0.4.0 (`WSGM.Device.Sdk.csproj:31`), `PluginApi.Version = 3` (`PluginManifest.cs:16`), Plugin SDK 0.2.0. Bumps remain warranted, but by the breaking set in section 4 (journal disposability, `CapabilityStateDelta` move, glyph limit constants, manifest rules, `PluginText` removal, trace channel move, init-only manifest), not by clock changes, which stay non-breaking. |
| C14 | "manifests, templates, fixtures and all consumers update coherently" (refactor-plan.md:149) | accurate list needed | Device: `src/WSGM.Device.Msi.Claw/plugin.wsgm.json`, `src/WSGM.Device.Asus.RogAlly/plugin.wsgm.json`, `src/WSGM.Device.HandheldCompanion/plugin.wsgm.json`, `src/WSGM.DeviceLab/Templates/MinimalPlugin/plugin.wsgm.json.template` (`{{API_VERSION}}`), `tests/Shared/PluginManifestFixture.cs`, `ContractBoundaryTests.cs:29`. Common: AmdGpu, IntelGpu, NvidiaGpu, Ir manifests (`minimumApiVersion`/`maximumApiVersion` 3); `eng/new-plugin.ps1:40` reads the version from `plugin-manifest.cs api-version`, so it needs no edit. |
| C15 | "common GPU routers stay independent" (refactor-plan.md:65) | accurate | `GpuCoordinator.cs:136-137` builds one `DeviceCapabilityRouter` per publisher. |
| C16 | F02 is "one atomic producer-plus-mechanical-consumer group" spanning nearly every project (api-integration.md, Device 12 row) | inaccurate under the refined design | With the clock and `PluginTrace` kept static, the breaking surface touches the SDKs, both device packages, the GPU plugins, IR, a handful of WSGM host files and the manifests. Split into SDK-B5 (Device 12) and SDK-B6 (Plugin 4), each compiling the whole solution. |
| C17 | A02_01 "Direct consumers are PluginPackageFile.ReadManifest (309), SDK ManifestTests and eng/plugin-manifest.cs" | accurate | `rg` finds no other `PluginManifestReader.TryRead` caller. `CommonPluginPackage.cs:151` calls `Validate`, which A02_01 does not change. |
| C18 | A02_01 leaves "parent package pre-routing/all-entry allocation limits" unresolved | accurate | `PluginPackageFile.cs:298` parses the manifest entry (up to 128 MiB) with `JsonDocument` before either reader's bound applies. Folded into SDK-B4. |
| C19 | A02_02 evidence (File.Exists at load, unlatched save failure, undefined status) | accurate | `DeviceRecoveryJournal.cs:230`, `:109`/`:145` (pre-gate check only), `:176-204` (only CheckHealth latches), `:343-354` (no `Enum.IsDefined`). A02_02 misses the same-class disposal bug at `:54` (SDK-002). |
| C20 | A02-F004 "Sources disposed before a distant deadline remain in Pending" | accurate, severity overstated | No in-repo deadline exceeds 20 s (`DeviceCoordinator.cs:1689`), so retention is bounded and small. Low. |
| C21 | A02-F021 common packer admits what the runtime rejects | accurate | `package-plugin.ps1:52` (4096 entries, managed check only) vs `PluginPackageFile.cs:31-34` (1024 entries, 512 files, 128 MiB/file, 512 MiB total). |
| C22 | "The HC project remains an explicitly non-installable design scaffold" (refactor-plan.md:34) | accurate | `src/WSGM.Device.HandheldCompanion/plugin.wsgm.json` declares no `hardware`, so `HardwareMatcher.Match` never matches. |
| C23 | No-arbitrary-limits rule applied to libraries (refactor-plan.md:141) | gap for this domain | The plan never mentions the glyph count/length caps (SDK-007) or the Plugins-folder package cap (SDK-008), both of which contradict the SDK's own guide and the API 11 note. |
| C24 | State root for plugin instances | covered elsewhere | `ShellSession.cs:302` derives `PluginState` from `Log.Directory` (PV10-004 / F01). The migration must keep `PluginState/<pluginId>/<SHA-256(instanceId)>` (`CommonPluginManager.cs:309-312`) unchanged. |

---

## 2. Findings

Severity scale: critical, high, medium, low, nit. "NEW" means not in any saved ledger row.

### SDK-001 (high) Active clock runs cancellation callbacks inline and unguarded
`src/WSGM.Device.Sdk/Lifecycle/ActiveClock.cs:102-112`. `source.Cancel()` runs every registered callback
synchronously on the clock thread and only `ObjectDisposedException` is caught. A throwing callback raises
`AggregateException` on a dedicated thread and terminates the process (A02-F003, confirmed). NEW aspect: a callback
that blocks stalls the only observer, so `Advance` stops, every following step counts as at most `MaximumStep`, and
active time undercounts. All other deadlines in the process then fire late.
Recommendation: dispatch each due source with `CancelAsync()` and observe the returned task's faults; keep
`ObjectDisposedException` handling. No public API change.

### SDK-002 (high) Serializer and journal dispose the semaphore that in-flight work still releases
`Services/DeviceCommandSerializer.cs:73-77` disposes `_gate` while an observation pass, `RunAsync` or `ExecuteAsync`
may still `Release()` it in a `finally` (A02-F005, confirmed). NEW: the same pattern in
`Services/DeviceRecoveryJournal.cs:52-57`; both packages dispose the journal at stop (`ClawPlugin.cs:407,441,469`,
`RogAllyPlugin.cs:350,380,435`) while service releases can still call `SetStatusAsync`.
Recommendation: remove the mechanism. A `SemaphoreSlim` whose wait handle is never used owns nothing that needs
disposing. Serializer `Dispose` becomes `StopObservation`; the journal stops being `IAsyncDisposable` (breaking,
SDK-B5).

### SDK-003 (high) Recovery journal treats unreadable state as absent and does not latch write failure
`Services/DeviceRecoveryJournal.cs:230`, `:109`, `:145`, `:176-204`, `:343-354`. A02-F007/F008/F009, confirmed.
Covered by batch A02_02, which is correct. Simplification to A02_02: the pre-gate `ThrowIfUnavailable()` becomes
redundant once it is checked after the gate; keep only the post-gate check.

### SDK-004 (medium) Active-time deadlines converted to wall timers
`DeviceCommandSerializer.cs:279-282` uses `CancelAfter(deadline.Remaining)`, which the SDK reference forbids
(A02-F006, confirmed). NEW instances of the same pattern on the host side: `PluginCapabilityChannel.cs:217-218`
(every GPU command), `CommonPluginManager.cs:282` (`WaitAsync(TimeSpan.FromSeconds(15))` beside an active
15 s registration deadline at `:331`), `:491` and `:510` (`WaitAsync(Remaining(deadline))`).
Recommendation: `deadline.Earliest(Deadline.After(limit)).CreateCancellationSource(tokens)` everywhere. Shutdown
waits that are genuinely wall-clock (`StopAsync`, `:360`) stay wall-clock.

### SDK-005 (medium) Keyboard hook: unguarded native callback and a stop race
`Windows/LowLevelKeyboardHook.cs:210-225`: a plugin handler exception propagates out of a reverse P/Invoke hook
procedure, which fails the process. `:148-171` / `:111-138`: `started` is signalled after `SetWindowsHookEx` but
before the thread has a message queue; `PostThreadMessage(WM_QUIT)` to a thread without a queue fails, so a stop
right after start times out after 1 s and leaves the thread in `GetMessage` with the hook installed. NEW.
Recommendation: wrap the handler call (report through `_fault`, then `CallNextHookEx`); force queue creation with
`PeekMessage(PM_NOREMOVE)` before publishing `_threadId`, and re-check `_stopping` after that. Both are small,
HC-style mechanics with no new state.

### SDK-006 (medium) Motion poll thread has no fault boundary
`Windows/LegacyMotionStream.cs:107-137`: an exception from the plugin's `onReading` on the dedicated poll thread
terminates the process. The event path (`LegacyMotionSensors.Events.cs:264-292`) instead turns it into an HRESULT
that the Sensor API discards silently. NEW.
Recommendation: one guard around `onReading` in both paths that traces once per stream and continues.

### SDK-007 (medium) Glyph import imposes count and length caps, and silently truncates the renderer projection
`Glyphs/GlyphProfile.cs:31-68` (`MaxAssets` 128, `MaxProfiles` 32, `MaxControls`, `MaxAliases`, `MaxExactDevices`,
`MaxIdentifierLength`, `MaxDisplayNameLength`, `MaxPhysicalLabelLength`, `MaxSvgPaths`, `MaxSvgCommands`,
`MaxPathDataLength`), `GlyphPackageImporter.cs:87-88,125,150-157,346-349,366-369,420-423,451,510-513,677,683`,
`GlyphAssetValidation.cs:112-117,179-198` (paths beyond 256 or longer than 64 KiB are dropped from the projection
without a diagnostic; more than 4096 commands rejects the whole asset, contradicting its own remark that unreadable
paths still import), `ImmutableGlyphPackageDirectorySource.cs:76,85`, `GlyphPackageLayout.cs:23,40`,
`PluginPackageFile.cs:111`. This contradicts `src/WSGM.Device.Sdk/AGENTS.md` ("A length or count limit on
published content rejects a valid plugin and protects nothing") and the API 11 note that removed exactly these caps
everywhere else (`DeviceApi.cs:63-66`). NEW.
Recommendation: keep the byte and decode bounds (`MaxDocumentBytes`, `MaxAssetBytes`, `MaxProfileBytes`,
`MaxNoticeBytes`, `MaxDimension`, `MaxRasterPixels`, JSON depth); delete every count and length cap; keep every path
in the projection (asset bytes already bound it).

### SDK-008 (medium) Arbitrary caps in package discovery
`Core/PluginPackageCatalog.cs:88,174-179`: more than 128 packages refuses the entire Plugins folder.
`Core/PluginPackageFile.cs:342`: entry names over 260 characters are refused although nothing is unpacked to disk
any more (`:21`). NEW. Recommendation: remove both; the per-file and total byte bounds are the real IO limits.

### SDK-009 (medium) Discovery reads every package fully, repeatedly, sometimes on the UI thread, with side effects
`PluginPackageCatalog.Discover` opens each package through `PluginPackageFile.Open`, which reads every entry into
memory and PE-validates every image (`PluginPackageFile.cs:221-290`). Callers: `SettingsViewModel.cs:20,195`
(construction and refresh), `SettingsViewModel.Plugins.cs:403` and `ShellSession.cs:784`
(`InstalledDevicePluginId`, a full discovery to read one id), `UpdateChecker.cs:304`, `ShellSession.Actions.cs:93`,
`DeviceCoordinator.cs:2736`, and every `CommonPluginManager` reconcile (`:168`). `DiscoverInstalled` deletes pending
removals as a side effect (`:114`). `PluginPackageManager.Rows` then SHA-256 hashes every installed package
(`PluginPackageManager.cs:114,326-338`). NEW.
Recommendation: a manifest-only open for discovery (read `plugin.wsgm.json` and the entry image header), full open
only at load; apply pending removals once in startup composition; Settings reads the catalog snapshot the manager
already caches (`CommonPluginManager.Catalog`); compute the bundle hash once per discovery off the UI thread.

### SDK-010 (medium) Common manifest parse is unbounded, including the host's pre-routing parse
`src/WSGM.Plugin.Sdk/PluginManifestReader.cs:256-289` (A02-F001, confirmed; A02_01). NEW part:
`PluginPackageFile.cs:298` routes by parsing up to 128 MiB with `JsonDocument` before either reader's bound, and the
common reader does not catch `NotSupportedException` as the device reader does (`Packaging/PluginManifestReader.cs:186`).
Recommendation: length check against `ManifestLimits.MaxDocumentBytes` before routing; keep A02_01's reader change.

### SDK-011 (medium) Two manifest dialects with divergent duplicated validators
`Packaging/PluginManifestValidator.cs` and `Plugin.Sdk/PluginManifestReader.cs:294-447` both validate identity,
versions, entry assembly and entry type, with different rules: identifiers (uppercase allowed vs lowercase only),
package version (canonical vs any `Version.TryParse`), entry type (backtick allowed vs not), entry assembly (relative
subpath allowed at `PluginManifestValidator.cs:353-372` although `PluginPackageFile.cs:202-206` refuses any
non-root entry). NEW.
Recommendation: keep both schemas (their wire shape is established and setup/bundle tooling reads them), but move
the shared rules into one public `ManifestRules` in `Device.Sdk.Packaging` that both readers call; device entry
assembly becomes root filename only.

### SDK-012 (medium) Packers restate package rules the runtime enforces differently
A02-F021, confirmed: `eng/package-plugin.ps1:52-62` vs `PluginPackageFile.cs:31-34,261-282`; `eng/pack-device.ps1`
has its own copy. Recommendation: move package layout validation (entry name normalization, duplicate folding,
managed-image check, byte bounds) into a public `PluginPackageLayout` in `Device.Sdk.Packaging`;
`PluginPackageFile` and a new `plugin-manifest.cs validate-package <file>` mode both use it, and both packers call
that mode on the produced archive. One rule set, no new limits.

### SDK-013 (medium) Device and common loaders duplicate each other and signal cleanup through exception type
`Shell/PluginPackageLoader.cs:48-141` and `Shell/CommonPluginPackage.cs:148-220` repeat reopen, manifest
comparison, entry type checks, activation, identity check and cleanup. The common path reports "construction cleanup
unconfirmed" by throwing `AggregateException`, which `CommonPluginManager.cs:337` decodes
(`entry.Loaded is null && ex is AggregateException`). NEW.
Recommendation: one `PluginPackageLoader.Load<TEntry>(path, admittedIdentity)` returning a `LoadedPluginPackage<T>`
(owns `PluginPackageFile`, `PluginLoadContext`, entry instance) and an explicit failure result carrying
`CleanupConfirmed`.

### SDK-014 (medium) `CommonPluginPackage` fakes every optional plugin interface
`CommonPluginPackage.cs:14-15` implements `IConfigurablePlugin`, `IPluginActions`, `IPluginUi`, `IPluginSteamUi`
and `ICapabilityPlugin` for every package and forwards with type checks. Consequences: the host's own `is` checks
are always true, so `CommonPluginManager.cs:317-318` needs a `PublishesCapabilities: false` special case, and a
non-configurable plugin reports `PluginConfigurationOutcome.Applied` for any configuration (`:59-66`). NEW.
Recommendation: delete the forwarding wrapper; the host works on `LoadedPluginPackage.Plugin` (the real `IPlugin`)
and the loaded package object owns only lifetime (plugin dispose, then unload, then file close).

### SDK-015 (medium) Device runtime wrapped in a second lifecycle lane
`DeviceCoordinator.cs:1060-1066` admits a `DevicePluginCompatibilityAdapter` (139 lines) into `PluginHost`, so every
device start, suspend, resume and stop runs through `PluginRegistration.RunAsync` (its own `Task.Run`, semaphore,
deadline and quarantine, `PluginHost.cs:548-621`) on top of `DevicePluginRuntime`'s own lane. Two deadlines, two
quarantine models, one "compatibility" layer the maintainer rules disallow. NEW (cross-domain).
Recommendation: owned by the device-runtime domain (D01). Keep the Device slot reservation and health projection in
`PluginHost` as plain data published by `DeviceCoordinator`; delete the adapter and its lifecycle lane.

### SDK-016 (medium) Reconciliation block is promised by the SDK but enforced by every service
`Services/DeviceService.cs:65-67` documents that a service carrying `ReconciliationBlockReason` "acquires and
releases as faulted", but `DeviceServiceLifecycle` never checks it. Each service repeats the check:
`ClawServiceBase.cs:24`, `ClawServices.cs:136,251`, `ClawControllerService.cs:106`, `AllyServices.cs:49,107,172,236`.
NEW. Recommendation: enforce once in `DeviceServiceLifecycle.AcquireAsync/SuspendAsync/ReleaseAsync`; delete the
eight copies.

### SDK-017 (medium) Journal status turns missing readback into a write gate
`DeviceRecoveryJournal.cs:113-118` refuses a new mutation while an entry is `RestoredUnverified`. Packages record
`RestoredUnverified` when the restore write succeeded but did not read back (`AllyServices.cs:135-142`,
`ClawServiceBase.cs:57-59`). A successful restore without matching readback therefore blocks the service's next write
until an explicit re-arm, which is the readback gate the maintainer rules forbid. NEW.
Decision derived from the rules: `RestoredUnverified` means the original was written; `SetStatusAsync` removes the
entry exactly as `RestoredVerified` does. Only `RestoreFailed` (a write that threw or whose outcome is unknown)
remains blocking until explicit user action. Enum values and wire names stay; a loaded `RestoredUnverified` entry is
treated as resolved and dropped at the next save. Package `Decide` logic must check `RestoreFailed` first
(A02-F010, device domain).

### SDK-018 (medium) `PluginText` duplicates `PlainText`
`src/WSGM.Plugin.Sdk/PluginText.cs` is a verbatim copy of `Device.Sdk/Capabilities/PlainText.cs` although Plugin.Sdk
references Device.Sdk. Consumers: `PluginManifestReader.cs`, `PluginConfiguration.cs`, `IrPayload.cs`,
`CommonPluginActions.cs`, `PluginWidgetPins.cs`, `PluginTextTests.cs`. NEW. Recommendation: delete; use `PlainText`.

### SDK-019 (low) Plugin SDK README is wrong in four places
`README.md:5` (Graphics placement), `:13` (zero dependency), `:15` (compatibility adapter), `:40` (bounded reader).
A02-F002 confirmed plus two NEW claims. Rewrite after SDK-B6.

### SDK-020 (low) Common plugins have no log channel except through the capability host
`ICapabilityHost.Trace/TraceChange` (`PluginCapabilities.cs:40,47`) duplicate `IPluginHostAdapter`'s; `IPluginHost`
has none, so IR cannot log; Intel wraps the capability host in its own `IntelLog`; the host formats plugin logs in two
places (`PluginCapabilityChannel.cs:139-181` and the device runtime adapter). NEW.
Recommendation: `Trace`/`TraceChange` move to `IPluginHost` (every common plugin, one host implementation in
`PluginRegistration` over a shared formatter), removed from `ICapabilityHost`. Instance-based because common plugins
run several instances; device plugins keep `PluginTrace`.

### SDK-021 (low) `PluginTrace` details
Keep the static (C2, C9). Fix: `_sink` is read without `Volatile` (`PluginTrace.cs:108,143`); `Failure` writes raw
exception messages including control characters while `DiagnosticText.FromException` sanitizes them
(`PluginTrace.cs:135-139` vs `Plugin/DiagnosticText.cs:16-29`); route `Failure` through `DiagnosticText`. NEW.

### SDK-022 (low) Static mutable sync revision in an instance owner
`GpuCoordinator.cs:70` `private static long _syncRevision` (PV04 inventory). Plugins keep revisions in memory only
(`NvProfiles.cs:63`, `ApplicationProfileSynchronizer.cs:137,163`), so an instance field suffices. Side note for the GPU
domain: NVIDIA skips an equal revision (`<=`), Intel re-applies it (`<`).

### SDK-023 (low) GPU coordinator calls device-coordinator statics
`GpuCoordinator.cs:305,330` (`DeviceCoordinator.PerformanceProfileOwnsRole`) and `:518`
(`DeviceCoordinator.ReadOnAcPower()`, a native read). NEW. Recommendation: the pure rule moves to the profile policy
type both use; the power source becomes an injected `Func<bool?>` (the same port `DeviceCoordinator.cs:134` already
passes).

### SDK-024 (low) GPU coordinator leaves untracked work
`GpuCoordinator.cs:145,162,191` dispose publishers fire-and-forget; `:671` starts refreshes with
`Task.Run(...).ObserveFaults()`. `DisposeAsync` (`:105-128`) cannot join them. NEW. Recommendation: keep one list
of pending disposal/refresh tasks and await it in `DisposeAsync`.

### SDK-025 (low) Host reuses `CommandOutcome.Accepted` with a different meaning
SDK documents `Accepted` as "Validated and queued" (`CapabilityCommand.cs` enum doc); no plugin returns it; the host
uses it for "saved for the game, the driver applies it" (`CapabilityUserWrites.cs:120`, `GpuCoordinator.cs:272`,
consumers `NativeQamSemanticServices.cs:113`, `SteamGraphicsService.cs:88`). NEW. Recommendation: document the
host meaning on the enum member; no new type.

### SDK-026 (low) Host-only type in the contract
`Capabilities/CapabilityStateDelta.cs` is constructed and consumed only by WSGM (`DeviceCapabilityRouter.cs:82,568`,
`DevicePluginRuntime.cs:165,961`, `PluginCapabilityChannel.cs:135,187`, `ICapabilityPublisher.cs:29`). NEW.
Move to `src/WSGM/Shell`.

### SDK-027 (low) `ReadbackValue` contract text contradicts the factory and the no-readback rule
`CapabilityCommand.cs` says "Present only for AppliedVerified"; `CommandResults.Unverified(command, written)` sets it
for `AppliedUnverified` (`CommandResults.cs:29-39`) to publish the written value as observed. NEW. Fix the doc.

### SDK-028 (nit) Duplicated validation helpers
`DevicePowerPair.IsLimit` equals `DevicePowerPreset.IsPowerLimit` (`DevicePowerPair.cs:135-147`,
`DevicePowerPreset.cs:94-106`); `DevicePowerPreset.Fits` restates the range check in `TryResolve`;
`PluginManifestValidator.ValidateVersion` and `ValidateDottedVersion` repeat the canonical check;
`CapabilitySection.TryValidate` and `CapabilityCategory.TryValidate` repeat the custom-title rule. NEW.

### SDK-029 (low) Two identity comparison rules
`HardwareMatcher.Field` (`Identity/HardwareMatchRule.cs:249-263`) compares with `Trim` + ignore-case, while
`IdentityText.Normalize` also collapses internal whitespace and `DeviceIdentitySnapshot` docs claim values arrive
normalized. A BIOS string with a doubled space matches in one path and not the other. NEW.
Recommendation: `HardwareMatcher` uses `IdentityText.Matches`.

### SDK-030 (low) Common manifest record is mutable
`Plugin.Sdk/PluginManifest.cs:74,77,86,89,95,101,107` use `set`, which forces defensive copies
(`CommonPluginPackage.cs:222-231`). NEW. Make them `init`.

### SDK-031 (low) `PluginPackageManager` mixes UI projection, file operations and persistence
`Core/PluginPackageManager.cs`: English UI text and badge tones (`:101-225`), install/remove file operations
(`:232-271`; `UnauthorizedAccessException` escapes `Remove`), and `PendingPluginRemovals` (`:345-434`: non-atomic
write, an unreadable file reads as empty and is then overwritten, `Forget` reads twice). The Settings view projection
lives in Core. `PluginPackageManagerTests` reach the live `InstallLayout.PendingPluginRemovals` file through `Rows`.
NEW. Recommendation: rows projection moves to the Settings feature; file operations and the pending-removal store
take explicit paths; the store uses the existing atomic file helper and distinguishes unreadable from absent.

### SDK-032 (low) Adapter inventory is a process-global lazy that never refreshes
`Core/CommonPluginEnablement.cs:22-30`, used by Settings (`SettingsViewModel.Plugins.cs:205`) while
`CommonPluginManager` has its own injected seam (`:78`). NEW. Settings should ask the manager (`EnabledByDefault`
already exists at `CommonPluginManager.cs:97`).

### SDK-033 (nit) Inconsistent event threading and unsynchronized entry flags
`CommonPluginManager.Changed` is raised on pool threads (`:298,342,531`) while `GpuCoordinator.Changed` is raised on the
UI dispatcher; `Entry.Suspended` is written inside `Task.WhenAll` lambdas (`:456`). NEW. Document the thread of
`Changed`; no new mechanism.

### SDK-034 (low) Startup rollback ignores its deadline
`Services/DeviceServiceLifecycle.cs:160-187` releases with `CancellationToken.None` although the doc says the context
carries a fresh deadline. NEW. Use `context.Deadline.CreateCancellationSource()`.

### SDK-035 (nit) Serializer observation fields are unsynchronized
`DeviceCommandSerializer.cs:45-46,192-221,279`: `_observation`/`_observationToken` are written and read without a
barrier; the loop task is discarded. Safe in practice because packages call Start/Stop on their lifecycle lane; a
plain `Lock` around the two fields is enough. NEW.

### SDK-036 (nit) `DeviceReconnect.Start` leaks the previous source
`Windows/DeviceReconnect.cs:48-50` overwrites `_cancellation` after a completed loop without disposing it. NEW.

### SDK-037 (low) Logging on the per-sample motion path
`Input/MotionSampleBuilder.cs:75,101-119` emits `PluginTrace.Info` while holding the per-sample lock. Rate-limited by
bias change, but log I/O sits on the high-rate path. NEW. Capture the message under the lock, trace after it.

### SDK-038 (nit) Per-report RCW in the event path
`LegacyMotionSensors.Events.cs:276` allocates one unique RCW per sensor report. Inherent to the COM Sensor API and
chosen to avoid finalizer pressure. Record as an accepted exception to the zero-allocation rule; no change. NEW.

### SDK-039 (low) Test quality
- Constant and configuration pins rather than behavior: `ContractBoundaryTests.cs:21-30` (API constant), `:8-19`
  (reads csproj XML), `DeadlineTests.cs:32-36` (constant ratio), `CapabilityValueFactoryTests.cs:8-31` (factory equals
  initializer), `LowLevelKeyboardHookTests.cs:8-21` (reflection on a private P/Invoke).
- Copies: `PluginTextTests.cs` duplicates the PlainText tests.
- Limit pins that must go with SDK-007: `SdkGlyphTests.cs:100-124,201-238`.
- Missing behavior: serializer observation loop and dispose race, `StartResult`/`StopResult`/`ReasonFor`/`Ownership`,
  `DiagnosticText`, `CommandResults`, `IdentityText`, `DeviceReconnect`, `LegacyMotionStream` fault containment,
  `OemButtonLatch`, ActiveClock callback fault and pruning, journal failure paths (A02_02 adds those).
- `PluginPackageManagerTests` reads live machine state (SDK-031).
NEW.

### SDK-040 (low) Plan item A02-F004 (registration retention)
`ActiveClock.cs:48-57,84-95`. Confirmed but bounded by the longest deadline (20 s). Fix inside SDK-B2 by dropping
entries whose source is already cancelled at each pass; no `DeadlineCancellation` type.

### SDK-041 (nit) Clock wakes four times a second for the process lifetime
`ActiveClock.cs:66,78` at `AboveNormal` priority even with nothing pending. Required for freeze detection; document
the cost, no change.

### SDK-042 (nit) Version history kept in two places
`DeviceApi.cs:7-75` XML remarks duplicate the API history table in `docs/reference.md:977-987`. Keep the table; reduce
the XML to the current version.

### SDK-043 (nit) Host-only helpers and vocabulary in the contract
`DeviceSections.IncludePredefined` (`Capabilities/DeviceSections.cs:50`) is used only by WSGM
(`DeviceCapabilityRouter.cs:542`, `DeviceOverlayBridge.cs:374,1289`). Move it to the host. `DeviceServiceLifecycle.Ownership`
and `DeviceRecoveryJournal.DiagnosticState` are shared package vocabulary and stay. NEW.

### SDK-044 (low, no-change) Two plugin settings contracts
Device `PluginSettingsManifest` (runtime-published, `CapabilityValue`) and common `PluginSetting`/`PluginConfiguration`
(static, `PluginValue`, revisioned) with separate host coordinators. No defect; merging would change stored values and
needs a migration and UI work the requirements do not ask for. Disposition: accepted no-change, documented.

### SDK-045 (nit, no-change) Two icon vocabularies
`PluginWidget.Icon` free strings (`PluginActions.cs:130-133`) vs the closed `SectionIcon` enum. Documented, no change.

### SDK-046 (nit) Triple copy of glyph asset bytes
`PluginPackageFile.cs:128` copies, `GlyphPackageImporter.cs:249` copies again, `GlyphAssetValidation.cs:125,467`
copy a third time. Keep one owned copy. NEW.

### SDK-047 (low) PNG inspector refuses ordinary metadata chunks
`GlyphAssetValidation.cs:397` rejects `tEXt`/`zTXt`/`iTXt`, which common exporters write into static PNGs. These are
ancillary and harmless; skip them like other ancillary chunks. Animation chunks stay refused. NEW.

### SDK-048 (nit) Generic router carries a device name
`GpuCoordinator.cs:137` builds a `DeviceCapabilityRouter` per GPU publisher. Rename to `CapabilityRouter` when the
device-router domain next touches it. NEW.

### SDK-049 (nit) Registration state written outside the host lock
`PluginHost.cs:465` writes `Health` without the host lock that `Publish` (`:186-196`) uses; `Context` is replaced per
operation (`:573`) and read from other threads. Benign today (reference writes). NEW. Write `Health` under the host
lock.

---

## 3. Plan refinements

### Additions
1. Glyph and catalog limits (SDK-007, SDK-008) belong in the no-arbitrary-limits work; add them to the plan text that
   currently lists only toolkit limits (refactor-plan.md:141).
2. SDK fault boundaries (SDK-005, SDK-006) belong under "Native callbacks contain exceptions" (refactor-plan.md:115).
3. Journal semantics: `RestoredUnverified` is resolved, only `RestoreFailed` blocks (SDK-017). This pairs with
   A02-F010 in DP01.
4. Shared package rules (SDK-011, SDK-012): one `ManifestRules` and one `PluginPackageLayout` in
   `Device.Sdk.Packaging`; packers call them through `plugin-manifest.cs`.
5. Loader unification and removal of `CommonPluginPackage`'s forwarding (SDK-013, SDK-014).
6. Discovery cost and side effects (SDK-009) and the `PluginPackageManager` split (SDK-031).
7. `ReconciliationBlockReason` enforcement in the SDK walk (SDK-016) replaces the vague "consolidate duplicated
   lifecycle scaffolding".
8. Delete `PluginText`; trace channel on `IPluginHost` (SDK-018, SDK-020).
9. Cross-domain: remove `DevicePluginCompatibilityAdapter` (SDK-015) in D01.

### Changes
1. F02 shrinks to non-breaking SDK internals (SDK-B2) plus two separate contract batches (SDK-B5 Device 12,
   SDK-B6 Plugin 4). The api-integration "atomic group across almost every project" is replaced by those two batches,
   each ending with a full managed solution build.
2. A02_02: keep, minus the redundant pre-gate check; add removal of `_writeGate.Dispose()` (or leave that to SDK-B5,
   which removes `IAsyncDisposable`).
3. A02_01: keep steps 1, 2 and 4. Drop step 3 (the CLI's `FileStream` growth detection with `ReadByte`, `FileShare`
   handling and exception mapping) and the GUID-directory fixture choreography in step 5: a dev-time CLI reading its
   own build output does not need them, the reader's own byte bound is the defect fix, and the package-level check
   moves into SDK-B8.

### Over-engineering to remove (simplify rule)
| Plan mechanism | Why it fails the rule | Simpler shape |
| --- | --- | --- |
| Explicitly owned clock service passed through every context and factory (refactor-plan.md:113, F02 step 1) | No defect needs more than one clock; 114 call sites gain plumbing | Keep the process clock; fix callbacks and pruning inside it (SDK-B2) |
| Clock-bearing `Deadline` with cross-clock refusal except sentinels | Invents a state (clock identity) and a refusal path no current code can reach | `Deadline` stays a value on the single active clock |
| `DeadlineCancellation` owning token source plus removable registration | Retention is bounded by deadlines of at most 20 s | Prune cancelled entries each pass; dispatch with `CancelAsync` |
| Clock shutdown that cancels registrations and joins its thread | A background thread dies with the process; joining adds a stop protocol for nothing | None |
| Instance `PluginDiagnostics` bound to host context for device plugins (F02 step 3) | No production defect; ~30 consumer files and static helpers change | Keep `PluginTrace`; add a log channel to `IPluginHost` only for multi-instance common plugins |
| A02_01 step 3 CLI growth detection and fixture GUID roots | Guards a developer tool against itself | Reader byte bound plus SDK-B8 package validation |

### No-arbitrary-limits violations found in this domain
`GlyphProfileLimits` count and length constants (SDK-007), SVG projection truncation (SDK-007),
`PluginPackageCatalog.MaxPackages` (SDK-008), the 260-character entry name cap and `MaxNoticePathLength`
(SDK-007/008). Kept as genuine IO/decode bounds: manifest 256 KiB and depth 16, glyph document/asset/profile/notice
bytes, raster dimension and pixel bounds, package per-file and total bytes, package entry count (central directory
read before any allocation).

---

## 4. Target design

Owners after the refactor:

| Owner | Responsibility | Change |
| --- | --- | --- |
| `ActiveClock` (static, Device.Sdk) | Process freeze-aware clock | Internal `ActiveClockCore` (timestamp source, pending list, `Advance`, `CollectDue`) behind the unchanged public facade; due sources cancelled with `CancelAsync`; cancelled entries pruned. `InternalsVisibleTo("WSGM.Device.Sdk.Tests")` added to the csproj. |
| `Deadline` | Active-time value | Unchanged public API. |
| `DeviceCommandSerializer` | One gate per plugin | `Dispose` = `StopObservation`; gate never disposed; post-command refresh on an active deadline; observation fields under a `Lock`. |
| `DeviceRecoveryJournal<TState>` | Crash-recovery originals | A02_02 behavior; no longer `IAsyncDisposable`; `RestoredUnverified` resolves the entry; only `RestoreFailed` blocks. |
| `DeviceServiceLifecycle` | Service walks | Enforces `ReconciliationBlockReason`; rollback bounded by context deadline. |
| `PluginTrace` | Device plugin ambient sink | Unchanged API; `Volatile` read; `Failure` via `DiagnosticText`. |
| `LowLevelKeyboardHook`, `LegacyMotionStream`, `LegacyMotionSensors` events | Windows helpers | Callback fault boundaries; queue created before start is signalled. |
| Glyph importer and sources | Glyph package import | Byte and decode bounds only; full path projection. |
| `ManifestRules` (new, public static, `Device.Sdk.Packaging`) | Identifier, canonical version, root entry assembly, entry type rules | Used by both manifest readers. |
| `PluginPackageLayout` (new, public static, `Device.Sdk.Packaging`) | `.wsgmpkg` entry naming, duplicates, managed-image check, byte bounds, manifest-only read | Used by `PluginPackageFile` and `eng/plugin-manifest.cs validate-package`. |
| `IPluginHost` (Plugin.Sdk) | Common host boundary | Gains `Trace`/`TraceChange`. `ICapabilityHost` loses them. |
| `PluginLoader` (WSGM, replaces `PluginPackageLoader` statics and `CommonPluginPackage.LoadAsync`) | Load any package entry | `Load<TEntry>` returns `LoadedPluginPackage<TEntry>` or a failure carrying `CleanupConfirmed`. |
| `PluginCatalog` (WSGM, replaces `PluginPackageCatalog` static use from UI) | Discovery snapshot | Manifest-only discovery; pending removals applied once at startup; Settings reads the snapshot. |
| `PluginPackageInstaller` + `PendingPluginRemovalStore` (WSGM Core) | Install/remove files | Explicit roots; atomic store. |
| `PluginPackageRows` (WSGM Settings) | Plugins page projection | Moved from Core. |
| `GpuCoordinator` | GPU publishers | Instance revision; injected power source and pure profile rule; tracked disposals. |

### Old symbol to new owner (dissolved or split files)

`src/WSGM.Plugin.Sdk/PluginText.cs` (deleted)
| Old symbol | New owner |
| --- | --- |
| `PluginText.TryValidate(string?, string, out string?)` | `WSGM.Device.Sdk.Capabilities.PlainText.TryValidate` (identical) |
| `PluginText.TryValidate(string?, int, string, out string?)` | `PlainText.TryValidate` (identical overload) |
| `PluginText.IsUnsafe(char)` | `PlainText.IsUnsafe` |

`src/WSGM.Device.Sdk/Capabilities/CapabilityStateDelta.cs` (moved)
| Old symbol | New owner |
| --- | --- |
| `CapabilityStateDelta(long Sequence, CapabilityState State)` | `WSGM.Shell.CapabilityStateDelta` (internal, same shape) |

`src/WSGM/Shell/CommonPluginPackage.cs` (dissolved)
| Old symbol | New owner |
| --- | --- |
| `LoadAsync(packagePath, admitted, token)` | `PluginLoader.LoadAsync<IPlugin>` (common entry rules: category not Device, GPU requires `ICapabilityPlugin`) |
| `Snapshot(manifest)` | Deleted; manifest becomes init-only (SDK-030) |
| `PublishesCapabilities` | Deleted; host checks `loaded.Plugin is ICapabilityPlugin` |
| Forwarders for `IPlugin`, `IConfigurablePlugin`, `IPluginActions`, `IPluginUi`, `IPluginSteamUi`, `ICapabilityPlugin` | Deleted; consumers use the real plugin instance |
| `DisposeAsync` (plugin dispose, unload, file close) | `LoadedPluginPackage<T>.DisposeAsync` (same order) |
| `_context`, `_package`, `_plugin`, `_disposed` | `LoadedPluginPackage<T>` fields |

`src/WSGM/Shell/PluginPackageLoader.cs` (split)
| Old symbol | New owner |
| --- | --- |
| `Load(InstalledDevicePackage)` | `PluginLoader.Load<IDevicePlugin>` with device entry rules |
| `Plugin`, `Package`, `Dispose` | `LoadedPluginPackage<IDevicePlugin>` |
| `ConstrainPackagePath` | `PluginStatePaths.Constrain` beside the state-directory code that uses it (`CommonPluginManager`) |
| `PluginLoadContext` (`HostOwned` list, host-first `Load`, `TryLoadFromPackage`, `PackageFileName`) | Unchanged, nested in `PluginLoader` |

`src/WSGM/Core/PluginPackageManager.cs` (split)
| Old symbol | New owner |
| --- | --- |
| `PluginPackageAction`, `PluginBadgeTone`, `PluginBadge`, `PluginPackageSection`, `PluginPackageRowState` | `src/WSGM/Settings/PluginPackageRows.cs` (same names, same values) |
| `Rows`, `Facts`, `AdapterVendors`, `Provenance`, `Contact` | `PluginPackageRows` |
| `Hash` | Computed once by `PluginCatalog` discovery and stored on each catalog entry |
| `Install`, `Remove`, `IsInside` | `src/WSGM/Core/PluginPackageInstaller.cs` (roots passed in) |
| `PendingPluginRemovals.Read/Add/Forget/Apply/Write` | `PendingPluginRemovalStore` (explicit file path, atomic write, unreadable reported) |
| `PendingRemovalsJsonContext` | Stays with the store |

`src/WSGM/Core/PluginPackageCatalog.cs` (made an owned service)
| Old symbol | New owner |
| --- | --- |
| `Discover(root)` | `PluginCatalog.Discover` using `PluginPackageLayout.ReadManifest` |
| `DiscoverInstalled()` | Removed; startup composition applies pending removals once and calls `Discover` |
| `InstalledDevicePluginId()` | `PluginCatalog.Current.Device.InstalledPackage?.Manifest?.Id` |
| `MaxPackages` | Deleted |
| Records `DevicePackageInventory`, `InstalledDevicePackage`, `DevicePackageDiscovery`, `CommonInstalledPlugin`, `PluginPackageNotice`, `Candidate` | Unchanged, plus a `Sha256` field on installed entries |

`src/WSGM/Core/PluginPackageFile.cs` (slimmed)
| Old symbol | New owner |
| --- | --- |
| `MaxPackageEntries/Files/FileBytes/Bytes`, `TryNormalizeEntryName`, `IsImageName`, `IsManagedImage`, `ReadEntries` rules | `PluginPackageLayout` (260-character cap removed) |
| `ReadManifest` routing | `PluginPackageLayout.ReadManifest` with the length check before routing |
| `Open`, `TryOpenAssembly`, `TryRead`, `EnumerateProfileIds`, `HostVersion`, `IsForThisHost`, `EntryIsX64Assembly` | Stay in `PluginPackageFile` (`EnumerateProfileIds` without `Take`) |

`src/WSGM.Device.Sdk/Packaging/PluginManifestValidator.cs` (rules extracted)
| Old symbol | New owner |
| --- | --- |
| `ValidateIdentifier`, `ValidateVersion`, `ValidateDottedVersion`, `ValidateRelativeAssemblyPath`, `ValidateEntryType` | `ManifestRules` (entry assembly becomes root filename) |
| `ValidateHardware`, `ValidateCapabilities`, `Validate`, `Add` | Stay |

`src/WSGM/Shell/DevicePluginCompatibilityAdapter.cs` (deleted in D01, cross-domain): every member's behavior is
already in `DevicePluginRuntime`; health projection goes to `PluginHost` as data published by `DeviceCoordinator`.

### Public API changes and consumers

Device API 12, package 0.5.0:
- `DeviceRecoveryJournal<TState>` no longer implements `IAsyncDisposable`. Consumers: `ClawPlugin.cs:407,441,469`,
  `RogAllyPlugin.cs:350,380,435`, journal tests.
- `CapabilityStateDelta` removed. Consumers: `DeviceCapabilityRouter.cs`, `DevicePluginRuntime.cs`,
  `PluginCapabilityChannel.cs`, `ICapabilityPublisher.cs`, `tests/WSGM.Tests/Fakes/FakeCapabilityPublisher.cs`.
- `GlyphProfileLimits`: `MaxAssets`, `MaxProfiles`, `MaxControls`, `MaxAliases`, `MaxExactDevices`,
  `MaxIdentifierLength`, `MaxDisplayNameLength`, `MaxPhysicalLabelLength`, `MaxSvgPaths`, `MaxSvgCommands`,
  `MaxPathDataLength` removed. Consumers: importer, both sources, `GlyphPackageLayout`, `PluginPackageFile.cs:111`,
  `SdkGlyphTests.cs`.
- New `ManifestRules`, `PluginPackageLayout`; device manifest entry assembly must be a root filename (all in-repo
  manifests already are).
- `HardwareMatcher` comparison uses `IdentityText` normalization. Consumers: `WSGM.Install`, Device Lab, Ally tests.
- Journal status semantics (SDK-017). Consumers: `ClawRecoveryJournal`, `AllyRecoveryJournal`, `ClawServiceBase`,
  `ClawControllerService`, `ClawPlugin.Recovery`, `AllyServices`, `RogAllyPlugin.Recovery`.
- `DeviceSections.IncludePredefined` moves to the host. Consumers: `DeviceCapabilityRouter.cs:542`,
  `DeviceOverlayBridge.cs:374,1289`, `SdkCapabilitySectionTests`.
- Manifests: Claw, Ally, HC, Device Lab template (placeholder), `tests/Shared/PluginManifestFixture.cs`,
  `ContractBoundaryTests`.

Plugin API 4, package 0.3.0:
- `PluginText` removed (consumers listed under SDK-018).
- `PluginManifest` setters become `init`. Consumers: `CommonPluginPackage.Snapshot` (deleted), any test that mutates.
- `IPluginHost.Trace/TraceChange` added, `ICapabilityHost.Trace/TraceChange` removed. Consumers: `PluginRegistration`,
  `PluginCapabilityChannel`, `IntelLog.cs`, `src/Shared/Gpu/DriverRuntime.cs:400,411,470,518,581`, IR (may now log),
  test fakes.
- `PluginManifestReader.TryRead` bounded (A02_01); `Validate` uses `ManifestRules`.
- Manifests: AmdGpu, IntelGpu, NvidiaGpu, Ir.

---

## 5. Implementation batches

Every batch ends with `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true` green.

### SDK-B1 Journal and serializer correctness (non-breaking), about 400 lines
Files: `Services/DeviceRecoveryJournal.cs`, `Services/DeviceCommandSerializer.cs`, `Services/DeviceServiceLifecycle.cs`,
`tests/WSGM.Device.Sdk.Tests/Services/*`.
Steps: apply A02_02 with only the post-gate availability check; `_writeGate` and `_gate` no longer disposed (journal
`DisposeAsync` becomes a no-op until SDK-B5 removes it in the same release); `RepublishAsync` uses
`command.Deadline.Earliest(Deadline.After(PostCommandLimit)).CreateCancellationSource(cancellationToken, observation)`;
observation fields under a `Lock`; `RollBackStartAsync` bounded by `context.Deadline`.
Tests: A02_02 fixtures; dispose while a gated operation is running completes without `ObjectDisposedException`;
post-command publish cancelled on the active deadline; rollback honors its deadline.
Filter: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Services"`.
Dependencies: none.

### SDK-B2 Active clock robustness (non-breaking), about 300 lines
Files: `Lifecycle/ActiveClock.cs`, `WSGM.Device.Sdk.csproj` (`InternalsVisibleTo`), `tests/.../Lifecycle/*`.
Steps: extract internal `ActiveClockCore(Func<long> timestamp)` holding `Advance`, `Pending`, `CollectDue`, pruning
of already-cancelled sources; the static facade owns one core and the thread; due sources are cancelled with
`CancelAsync` and their faults observed; `Tick`/`MaximumStep`/`CountedStep` unchanged.
Tests: fake timestamp drives freeze steps; a throwing callback does not stop later deadlines; a blocking callback does
not delay another deadline; cancelled sources are pruned; delete the constant-ratio test.
Filter: `--filter "FullyQualifiedName~Lifecycle"`. Dependencies: none.

### SDK-B3 Windows helper fault boundaries (non-breaking), about 250 lines
Files: `Windows/LowLevelKeyboardHook.cs`, `Windows/LegacyMotionStream.cs`, `Windows/LegacyMotionSensors.Events.cs`,
`Windows/DeviceReconnect.cs`, `Input/MotionSampleBuilder.cs`, `Plugin/PluginTrace.cs`, tests.
Steps: guard the hook callback and the poll/event delivery; `PeekMessage(PM_NOREMOVE)` before publishing the hook
thread id and re-check stop; dispose the previous reconnect source; trace outside the motion lock; `Volatile` sink;
`Failure` through `DiagnosticText`.
Tests: `DeviceReconnect` start/stop/restart; `MotionSampleBuilder` trace emission count; `PluginTrace.Failure`
sanitizes newlines. The hook and sensor guards have no seam and are covered by the attended Claw/Ally rows in M01.
Filter: `--filter "FullyQualifiedName~Windows|FullyQualifiedName~Input|FullyQualifiedName~PluginTrace"`.
Dependencies: none.

### SDK-B4 Bounded common manifest (A02_01 simplified), about 150 lines
Files: `Plugin.Sdk/PluginManifestReader.cs`, `tests/WSGM.Plugin.Sdk.Tests/ManifestTests.cs`, `Core/PluginPackageFile.cs`.
Steps: A02_01 steps 1, 2 and 4; catch `NotSupportedException`; length check against `ManifestLimits.MaxDocumentBytes`
before `JsonDocument.Parse` in `PluginPackageFile.ReadManifest`.
Tests: 262144/262145-byte boundary; depth refusal; oversized manifest entry in a package refused before parse.
Filters: `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj --filter "FullyQualifiedName~ManifestTests"`;
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage"`. Dependencies: none.

### SDK-B5 Device API 12 contract batch, about 1,400 lines
Files: Device.Sdk (`Glyphs/*`, `Services/*`, `Packaging/*` with new `ManifestRules.cs`, `Identity/HardwareMatchRule.cs`,
`Capabilities/DevicePowerPair.cs`, `DevicePowerPreset.cs`, `CapabilitySection.cs`, `CapabilityCommand.cs` docs,
`DeviceSections.cs`, `DeviceApi.cs`, csproj 0.5.0, `README.md`, `docs/reference.md`); host `Shell/CapabilityStateDelta.cs`
and its five consumers, `PluginPackageFile.cs:111`; Claw and Ally journal/service files (delete eight block checks,
remove journal dispose calls, adapt `Decide` to the new status semantics); three device manifests, the template,
`tests/Shared/PluginManifestFixture.cs`; SDK, Claw, Ally, WSGM and Device Lab tests that pin the removed constants.
Steps: remove glyph count/length caps and projection truncation (SDK-007); enforce `ReconciliationBlockReason` in the
walk (SDK-016); journal not disposable and `RestoredUnverified` resolving (SDK-017); move `CapabilityStateDelta` and
`IncludePredefined` to the host; `ManifestRules` with root-only entry assembly (SDK-011); `HardwareMatcher` uses
`IdentityText` (SDK-029); dedupe power helpers (SDK-028); doc fixes (SDK-027, SDK-042); bump to 12.
Tests: glyph package with 200 assets and 40 profiles imports; long SVG keeps every path; blocked service acquires and
releases as faulted without the service's own check; `RestoredUnverified` status removes the entry and permits the
next `BeginAsync`, `RestoreFailed` still refuses; old file with a `RestoredUnverified` entry loads and drops it at
the next save; doubled-space BIOS string matches.
Filters: `--filter "FullyQualifiedName~Glyph|FullyQualifiedName~Services|FullyQualifiedName~Packaging|FullyQualifiedName~Identity"`
for the SDK; `dotnet test tests\WSGM.Device.Msi.Claw.Tests\... --filter "FullyQualifiedName~Recovery"` and the Ally
equivalent; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Capability|FullyQualifiedName~Glyph"`.
Dependencies: device-package domain (DP01) agrees the Claw `Decide` change for A02-F010 lands here or immediately
after; no other.

### SDK-B6 Plugin API 4 contract batch, about 900 lines
Files: Plugin.Sdk (`PluginText.cs` deleted, `PluginManifest.cs`, `PluginContracts.cs`, `PluginCapabilities.cs`,
`PluginManifestReader.cs` using `ManifestRules`, `README.md`, csproj 0.3.0); host `PluginHost.cs`,
`PluginCapabilityChannel.cs`, a shared `PluginLogSink.cs`, `CommonPluginActions.cs`, `PluginWidgetPins.cs`;
`IntelLog.cs`, `src/Shared/Gpu/DriverRuntime.cs`, `IrPayload.cs`; four common manifests; Plugin.Sdk and WSGM tests and
fakes.
Steps: delete `PluginText`; init-only manifest; trace channel moved to `IPluginHost`; one host log formatter; README
rewrite (SDK-019); bump to 4.
Tests: IR or a fixture plugin logs through `IPluginHost.Trace` with instance prefix; manifest record cannot be
mutated after read; README-claim tests are not added (prose).
Filters: `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj`;
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~PluginHost|FullyQualifiedName~PluginCapabilityChannel"`;
the IntelGpu, NvidiaGpu, AmdGpu and Ir test projects. Dependencies: GPU domain (GP01) only for awareness of the trace
call-site edit in `DriverRuntime.cs`; it is mechanical.

### SDK-B7 One loader, no forwarding wrapper, about 700 lines
Files: `Shell/PluginPackageLoader.cs` to `PluginLoader.cs`, `Shell/CommonPluginPackage.cs` (deleted),
`Shell/CommonPluginManager.cs`, `Shell/DevicePluginRuntime.cs` (load call only), `Shell/PluginCapabilityChannel.cs`
constructor input, tests.
Steps: `Load<TEntry>` and `LoadedPluginPackage<T>`; explicit `CleanupConfirmed` failure; manager uses the real
plugin for interface checks; active-time waits in the manager (SDK-004).
Tests: GPU package whose entry lacks `ICapabilityPlugin` is refused at load; a non-configurable plugin is not
reported as configured; cleanup-unconfirmed load keeps the instance reserved (existing
`CommonPluginManagerTests.UnconfirmedCleanupRetainsTheInstanceAndPreventsReplacement` adapted).
Filter: `--filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~DevicePluginRuntime"`. Dependencies: device-runtime
domain must not be mid-change in `DevicePluginRuntime.cs` (serialize writers).

### SDK-B8 Catalog, installer and package layout, about 1,300 lines
Files: Device.Sdk `Packaging/PluginPackageLayout.cs` (new); `Core/PluginPackageFile.cs`, `Core/PluginPackageCatalog.cs`
to `PluginCatalog`, `Core/PluginPackageManager.cs` split into `Core/PluginPackageInstaller.cs`,
`Core/PendingPluginRemovalStore.cs`, `Settings/PluginPackageRows.cs`; `Settings/SettingsViewModel*.cs`,
`Core/UpdateChecker.cs:304`, `Shell/ShellSession.cs:784`, `Shell/ShellSession.Actions.cs:93`,
`Shell/DeviceCoordinator.cs:2736`, `Core/CommonPluginEnablement.cs`; `eng/plugin-manifest.cs` (`validate-package`),
`eng/package-plugin.ps1`, `eng/pack-device.ps1`; tests.
Steps: manifest-only discovery; remove `MaxPackages` and the 260 cap; pending removals once at startup; Settings
reads the catalog snapshot and the manager's adapter inventory; hash per discovery off the UI thread; packers call
`validate-package`.
Tests: discovery of a package with a 100 MiB data entry reads only the manifest; 200 packages discover; unreadable
pending-removal store is reported and not overwritten; Rows tests use explicit paths; packer validation matches the
runtime on fixture archives (temporary paths only).
Filters: `--filter "FullyQualifiedName~PluginPackage|FullyQualifiedName~PluginCatalog|FullyQualifiedName~CommonPluginEnablement"`
and SDK `--filter "FullyQualifiedName~Packaging"`. Dependencies: F01 owns `Log.Directory`/`InstallLayout` path ports;
use them if landed, otherwise pass the existing paths explicitly. Note: renaming the Settings rows type may touch UI
bindings; UI must stay identical.

### SDK-B9 GPU coordinator hygiene, about 350 lines
Files: `Shell/GpuCoordinator.cs`, `Shell/ShellSession.cs` (constructor arguments), the profile policy home of
`PerformanceProfileOwnsRole`, `Shell/DeviceCoordinator.cs` (move the static, keep a forwarding call site until D01
reshapes it), tests.
Steps: instance `_syncRevision`; injected `Func<bool?> onAcPower`; tracked disposal and refresh tasks awaited in
`DisposeAsync`; active-time GPU command cancellation in `PluginCapabilityChannel.cs:217-218` if SDK-B7 did not
already cover it.
Tests: two coordinators in one process issue independent revisions; `DisposeAsync` waits for a slow publisher
disposal; AC/DC selection uses the injected source.
Filter: `--filter "FullyQualifiedName~GpuCoordinator|FullyQualifiedName~PluginCapabilityChannel"`.
Dependencies: device-coordinator domain (D01/D03) agrees where `PerformanceProfileOwnsRole` lives.

### SDK-B10 Test cleanup, about 400 lines
Files: SDK test projects, `tests/WSGM.Tests/Core/PluginPackageManagerTests.cs`.
Steps: delete the constant/csproj/reflection pins listed in SDK-039 (keep the HID layout offsets and the value-type
guard), delete `PluginTextTests`, add the missing behavioral tests not already added in B1 to B9 (`StartResult`,
`StopResult`, `ReasonFor`, `Ownership`, `DiagnosticText`, `CommandResults`, `IdentityText`, `OemButtonLatch`).
Filters: both SDK test projects in full (they are fast and isolated).

Cross-domain item, not an SDK batch: remove `DevicePluginCompatibilityAdapter` (SDK-015) in D01.

Order: B1, B2, B3, B4 (independent, any order) then B5, B6, B7, B8, B9, B10.

---

## 6. Risks and open questions

Risks:
- SDK-017 changes how persisted recovery entries are interpreted. An old `temporary-state.v1.json` holding a
  `RestoredUnverified` entry is dropped at the next save instead of blocking. That matches the rules (the original was
  written), but it must be in the M01 recovery rows for Claw 8 A2VM and the Xbox Ally X tester.
- Removing glyph caps lets a package ship more artwork; memory stays bounded by the per-profile byte bound, but the
  Avalonia renderer now receives every path of a large SVG. The asset byte bound (512 KiB) limits the worst case.
- SDK-B8 touches the Settings Plugins page data flow; layout and wording must stay identical (UI baseline review).

Open questions for the maintainer:
1. SDK-015: is the device instance's health row in the common plugin host (`PluginHost.Snapshot`) still wanted by any
   surface? If not, the compatibility adapter and the Device slot policy in `PluginHost` can both go; if yes, only the
   adapter's lifecycle lane goes and `DeviceCoordinator` publishes the row as data.
