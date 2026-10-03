# SDK domain: adversarial verification of sdk.md

Verifier: Claude, read-only. Baseline master `1329813f`, clean tree. No build, test, git mutation or live action.
Every verdict below comes from reading the cited code and its callers at the current head.

Scope re-read in full for this pass: `Lifecycle/ActiveClock.cs`, `Lifecycle/Deadline.cs`, `Services/*` (all four),
`Windows/LowLevelKeyboardHook.cs`, `Windows/LegacyMotionStream.cs`, `Windows/LegacyMotionSensors.Events.cs`,
`Windows/LegacyMotionSensors.cs` (lifecycle, open, read), `Windows/PrecisionTicker.cs`, `Windows/DeviceReconnect.cs`,
`Windows/HidDevices.cs` (public surface and walk), `Input/MotionSampleBuilder.cs`, `Input/OemButtonLatch.cs`,
`Glyphs/GlyphProfile.cs` (limits), `Glyphs/GlyphAssetValidation.cs` (SVG path), `Identity/HardwareMatchRule.cs`,
`Identity/IdentityText.cs`, `Packaging/*`, `Plugin/PluginTrace.cs`, `Plugin/DiagnosticText.cs`, `Capabilities/PlainText.cs`,
all of `src/WSGM.Plugin.Sdk/*.cs` except PluginActions/PluginSteamUi (skimmed), `Core/PluginPackageFile.cs`,
`Core/PluginPackageCatalog.cs`, `Core/PluginPackageManager.cs`, `Core/CommonPluginEnablement.cs`,
`Shell/CommonPluginManager.cs`, `Shell/CommonPluginPackage.cs`, `Shell/PluginPackageLoader.cs`, `Shell/GpuCoordinator.cs`,
`Shell/PluginCapabilityChannel.cs:120-240`, all eight manifests. Traced into Claw/Ally service, journal, recovery and
motion files, `ShellSession.cs:285-320`, `DeviceCoordinator.cs:926,994,1050-1075,1575-1600,2730-2740`,
`PluginHost.cs:269-311`, `DeviceCapabilityRouter.cs:132-149`.

---

## Refuted

### SDK-017 (medium) "Journal status turns missing readback into a write gate" - refuted as stated
The claimed mechanism does not occur in either package:
- Ally never reaches the `BeginAsync` refusal for a non-pending entry. `AllyRecoveryJournal.ArmAsync`
  (`AllyRecoveryJournal.cs:36-49`) turns any non-`Pending` entry back to `Pending` with `SetStatusAsync` and only calls
  `BeginAsync` when no entry exists. A `RestoredUnverified` entry is re-armed by the next explicit command; no write is
  refused. `Decide` returns `Block`, which `ReconcileOutstandingAsync` treats as "not retried and not blocking: the
  service stays usable" (`RogAllyPlugin.Recovery.cs:37-44`).
- Claw's cited source (`ClawServiceBase.cs:57-59`) never yields `RestoredUnverified` for power or fans: both
  `RestoreAsync` implementations return `true` unconditionally after the writes (`ClawCapabilities.cs:232-248`,
  `:558-571`), exactly as the Claw guide requires ("a restore is complete once its writes went through"). Claw
  `RestoredUnverified` comes only from the controller mode switch settling at another mode or location
  (`ClawControllerService.cs:403-410`) and from reconcile when the record has no usable original
  (`ClawPlugin.Recovery.cs:66-111`, `_ => false`). Neither is "a write succeeded but did not read back".
- The proposed remedy (drop persisted `RestoredUnverified` entries at the next save, SDK-B5) discards recovery originals
  on upgrade, which conflicts with requirements.md item 11 ("preserve Windows recovery state") and with the Ally
  design comment at `AllyRecoveryJournal.cs:62-64`. It is a policy change that needs a maintainer decision, not a
  derived consequence of the rules.
The real readback gate in this area is elsewhere and was missed: see SDK-V-002.

### SDK-005, second half (stop race) - refuted
`Run` checks `_stopping` before every `GetMessage` (`LowLevelKeyboardHook.cs:167`). A stop that lands before the
thread has a message queue sets `_stopping = 1` first (`:126`); the failed `PostThreadMessage` is then irrelevant
because the loop exits on the flag before it ever blocks in `GetMessage`. Once `SetWindowsHookEx` has run, the thread
is a GUI thread with a queue, so a later post succeeds. In addition, `StopAsync` is only reachable after `StartAsync`
returned or was cancelled. No hang with the hook installed is reachable. The `PeekMessage(PM_NOREMOVE)` step in SDK-B3
is unneeded mechanism and should be dropped. (The handler-exception half survives, see Corrected.)

### SDK-040 remedy (prune cancelled entries) - refuted as a fix for A02-F004
A02-F004 concerns sources that were *disposed* before their deadline. A disposed, uncancelled
`CancellationTokenSource` is not `IsCancellationRequested`, and there is no public "is disposed" probe, so pruning
"already-cancelled sources" (SDK-B2) does not remove them. The finding itself is bounded exactly as the reviewer says
(largest literal budget is 20 s, `Deadline.Never` never registers, `Deadline.cs:94`). Disposition should be accepted
no-change, not new pruning code.

---

## Corrected

| Id | Correction |
| --- | --- |
| SDK-001 | Severity high -> medium. The crash path needs a registration callback that throws; no in-repo registration on a deadline token throws (`rg "\.Register\("`: only `TrySet*`/`ThreadPool.QueueUserWorkItem` callbacks, all in Device Lab or Artwork). The stronger real effect is that `source.Cancel()` runs registered callbacks *and synchronous await continuations* (e.g. code after `await Task.Delay(x, token)`) inline on the clock thread, so plugin code can execute on the AboveNormal clock thread until its next await. `CancelAsync` remains the right, removal-only fix. |
| SDK-002 | Severity high -> medium; journal half not reachable today. The serializer race needs a command queued on the gate during stop and released by the stop's own `Release` before `Dispose` (`ClawPlugin.cs:389-445`), which host admission closing makes rare. Every journal call in both packages runs inside the serializer lane or inside reconnect work that release joins (`DeviceReconnect.StopAsync`), so `DeviceRecoveryJournal.DisposeAsync` racing a `SetStatusAsync` was not demonstrated. The remedy (stop disposing a `SemaphoreSlim` whose wait handle is never used) is still correct and matches the router's own choice at `DeviceCapabilityRouter.cs:143-145`. |
| SDK-003 | A02-F007 and A02-F009 confirmed. A02-F008 (latch on any save failure) is mechanism without a defect: a failed `SaveAsync` already throws and refuses that mutation before the hardware write (`ClawPlugin.Commands.cs:506-513`), and `_entries` only changes after a successful move. Latching turns one transient file lock (indexer or AV on the `.tmp` or target during `File.Move`) into loss of every journalled control (Claw power, fans, controller mode; Ally arm path) until the plugin restarts. See Batch problems. |
| SDK-005 | First half confirmed (a handler exception escapes a reverse-P/Invoke hook callback, `:210-225`); stop-race half refuted (above). Severity stays medium only for the callback guard. |
| SDK-008 | Confirmed for `MaxPackages` and the 260-character name cap, but the reviewer's "kept as genuine IO bounds" list is wrong for the package entry count: `ZipArchive.Entries` parses the whole central directory and allocates every `ZipArchiveEntry` before the `> MaxPackageEntries` check (`PluginPackageFile.cs:236-241`), so the 1024 cap bounds nothing. `MaxPackageFiles` (512) is likewise a count cap; the per-file and total byte bounds are the real limits. See SDK-V-003. |
| SDK-010 | Severity medium -> low. The Plugins root is the administrator-protected install root (`ShellSession.cs:293-295`), so an oversized manifest needs an administrator. Line references are wrong: the common reader is 218 lines; `TryRead` is `PluginManifestReader.cs:21-54`, not 256-289. The device reader's `NotSupportedException` catch is `Packaging/PluginManifestReader.cs:76-79`, not :186. The fix (length check before routing) is still cheap and right. |
| SDK-011 | Substance confirmed; line references wrong. Common `Validate` is `Plugin.Sdk/PluginManifestReader.cs:59-152` (not 294-447); device `ValidateRelativeAssemblyPath` is `PluginManifestValidator.cs:150-169` (not 353-372). Additional divergence the reviewer missed: device `name` is checked only for non-blank (`PluginManifestValidator.cs:121-130`), while the common name and every other plugin label go through `PlainText`/`PluginText` (control and bidi characters rejected). Fold into `ManifestRules`. |
| SDK-014 | Severity medium -> low. Consequence exists (`PluginHost.cs:304-306` builds `CommonPluginSettings` for every common package because the wrapper always `is IConfigurablePlugin`) but it is not user-visible: the Steam source filters `Schema.Count > 0` (`CommonPluginSteamUiSource.cs:176`). The GPU `PublishesCapabilities` special case is also redundant with `CommonPluginPackage.cs:184-187`, which already refuses a GPU entry type without `ICapabilityPlugin`. |
| SDK-015 | Framing corrected: the maintainer's "no compatibility facades" rule targets layers that keep old APIs or behaviour alive; this adapter is an internal bridge between two current lifecycles. The duplicate lane (two deadlines, two quarantine models) is real. Keep as a cross-domain simplification for D01, not a rule violation. |
| SDK-016 | Doc/code mismatch confirmed; recommendation would drop behaviour. There are nine sites, not eight (`AllyControllerService.cs:144` missing), and they do not share one semantics: Claw power/fans check in `AcquireAsync` and in the journalled restore; Ally power/fans check in acquire and release; both controller services check only in acquire and only after `!Enabled` returns Passive (`ClawControllerService.cs:100-109`, `AllyControllerService.cs:140-147`). Claw controller `ReleaseAsync` (`:354-359`) has no block check, so a controller blocked mid-cycle (`:621`, `:638`) is still released (source and output stopped, mode restored) at stop; enforcing the block in `DeviceServiceLifecycle.ReleaseAsync` would skip that. `ClawControllerService.ReacquireAsync` calls `AcquireAsync` directly (`:325`), bypassing the walk, so deleting the per-service check would let a reconnect acquire a blocked controller. Correct fix: change the doc on `DeviceService.cs:65-67` to say each service decides; no SDK enforcement. |
| SDK-019 | Confirmed; README.md:5 still says "projects ... into Graphics", although the left-menu Graphics entry was removed in 7f7fee74. |
| C21 / SDK-012 | Confirmed, but aligning packers to the runtime must remove the runtime count caps (SDK-V-003) rather than copy 1024/512 into the packers. |

---

## Confirmed (ids only)

SDK-004, SDK-006, SDK-007, SDK-009, SDK-012, SDK-013, SDK-018, SDK-020, SDK-021, SDK-022, SDK-023, SDK-024, SDK-025,
SDK-026, SDK-027, SDK-028, SDK-029, SDK-030, SDK-031, SDK-032, SDK-033, SDK-034, SDK-035, SDK-036, SDK-037, SDK-038,
SDK-039 (test inventory; but see Batch problems for the deletion list), SDK-041, SDK-042, SDK-043, SDK-044, SDK-045,
SDK-046, SDK-047, SDK-048, SDK-049. Plan-claim rows C1-C5, C8, C12-C15, C17-C19, C22-C24 confirmed; C6, C7, C9, C16
confirmed as the reviewer framed them; C20 confirmed (bounded, low).

---

## Missed findings

### SDK-V-001 (medium) Removed common plugins are loaded again before their pending removal is applied
Files: `src/WSGM/Shell/CommonPluginManager.cs:168`, `src/WSGM/Core/PluginPackageCatalog.cs:112-116`,
`src/WSGM/Shell/ShellSession.cs:298-311`, `src/WSGM/Shell/DeviceCoordinator.cs:926,994`,
`src/WSGM/Core/PluginPackageManager.cs:249-271`.
Removing a loaded package on the Plugins page records it in `PendingPluginRemovals` and tells the user "Removed at the
next start" (`PluginPackageManager.cs:265-269`). Pending removals are applied only inside `DiscoverInstalled`. The common
manager discovers with `Discover` (no apply), and its first reconcile is started at `ShellSession.cs:303`, before
`DeviceCoordinator.TryStartAsync` (`:306-311`), whose discovery is the only startup caller of `DiscoverInstalled` and
runs only while Device Integration is on (`DeviceCoordinator.cs:926`). Scenario: Device Integration off, user removes
the IR or a GPU package, restarts WSGM. The common reconcile opens and loads the package and holds it open; the next
`DiscoverInstalled` (opening Settings) fails to delete it with a sharing violation and keeps it pending. The plugin keeps
running on every start. With integration on it is a race between the two startup discoveries.
Recommendation: apply pending removals once, synchronously, in startup composition before `CommonPluginManager` is
constructed (one call, no new state); remove the side effect from `DiscoverInstalled`. This is what SDK-009 proposes,
but the ordering constraint must be written into SDK-B8 or the bug survives the refactor.

### SDK-V-002 (medium, cross-domain: Ally package) Ally restore gates writes and controls on readback
Files: `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs:151-172,254-272,488-499`,
`src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Recovery.cs:52-71`, `src/WSGM.Device.Asus.RogAlly/AllyServices.cs:46-52,118-143`.
`AllyPowerCapability.RestoreAsync` writes the performance mode, reads it back, and returns `false` *without writing the
captured power limits* when the mode readback differs (`:154-159`); after writing the limits it returns the readback
comparison (`:169-171`). The fan restore does the same (`:495-498`). At cycle start, `ReconcileOutstandingAsync` records
any `false` as `RestoreFailed` (not even unverified) and sets `ReconciliationBlockReason` (`Recovery.cs:61-71`), and
`AllyServices.AcquireAsync` then faults the service (`:49-52`), so the TDP and fan controls disappear for that cycle.
This violates "never gate a write or a control on readback" and contradicts the Claw rule that a restore is complete
once its writes went through. It is the defect SDK-017 was reaching for.
Recommendation: mirror HC and the Claw package: write mode then limits unconditionally, return success when the writes
went through (readback only upgrades the trace), record `RestoreFailed` only for an exception. Owned by the device
package domain (DP01); SDK-B5 must not change journal semantics to compensate.

### SDK-V-003 (low) Package entry and file count caps protect nothing
File: `src/WSGM/Core/PluginPackageFile.cs:31-32,236-241,261`.
`MaxPackageEntries` (1024) is checked after `ZipArchive` has already read the full central directory, and
`MaxPackageFiles` (512) refuses a valid package by count. The 128 MiB per-file and 512 MiB total byte bounds, plus the
file-length check at `:189`, are the real limits. Under the no-arbitrary-limits rule both counts go; remove them in
SDK-B8 together with `MaxPackages` and the 260-character cap, and do not copy them into the packers (A02-F021).

### SDK-V-004 (low, cross-domain: device packages) Plugin disposal chains stop at the first throwing service
Files: `src/WSGM.Device.Msi.Claw/ClawPlugin.cs:415-446`, `src/WSGM.Device.Msi.Claw/WindowsMotionSource.cs:51-69`,
`src/WSGM.Device.Sdk/Windows/LegacyMotionStream.cs:60-94`, `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.cs:359-385`.
`LegacyMotionStream.Dispose` throws `TimeoutException` after 2 s when the poll thread is blocked in a synchronous
sensor read, and `WindowsMotionSource.StopAsync` adds its own 2 s `WaitAsync`. `ClawPlugin.DisposeAsync` awaits each
service in sequence without a guard, so a motion timeout skips the controller, MCU, OEM-event and WMI disposals, the
journal and the serializer. The Ally chain has the same shape. Recommendation: dispose every owner and collect
failures, the pattern `DeviceCoordinator` teardown already uses (`DeviceCoordinator.cs:1560-1581`). No new state.

### SDK-V-005 (low) SDK-B5 consumer list for `DeviceSections.IncludePredefined` is incomplete
`tests/WSGM.UiTests/Overlay/ControllerNavigationTests.cs`, `tests/WSGM.UiTests/Overlay/DevicePageCaptureTests.cs` and
`tests/WSGM.UiTests/Visual/PreviewExports.cs` also call it. Moving it into the host as `internal` needs those updated
(and UiTests must see the host's internals) in the same batch.

### SDK-V-006 (nit) Common manifest `permissions` is validated and copied but never read
`Plugin.Sdk/PluginManifest.cs:89-90`, `PluginManifestReader.cs:136-141`, `CommonPluginPackage.cs:227`. No host, setup
or UI code consumes it; only IR declares `usb.serial`. The README says declarations are not grants, which is honest,
but a contract field nobody reads should get an explicit disposition in Plugin API 4 (keep as documented metadata, or
remove).

### SDK-V-007 (nit) Device manifest name accepts control and bidi characters
`Packaging/PluginManifestValidator.cs:121-130`. Shown on the Plugins page and in logs. Use `PlainText.TryValidate` as
the common reader does (part of SDK-011's `ManifestRules`).

---

## Batch problems

1. **A02_02 / SDK-B1 latch step adds a sticky failure state.** A02_02 steps 2-3 make any save failure permanent for the
   journal's lifetime and recheck it after the gate. A failed save already refuses its own mutation before the write,
   so the latch fixes no concrete defect and turns one transient file lock into the loss of every journalled control
   until restart (Claw power/fans/controller mode, Ally `ArmAsync`). This fails the simplify rule. Keep steps 1
   (`FileNotFoundException` alone means absent) and 4 (`Enum.IsDefined`), drop steps 2-3 and their fixtures, and record
   A02-F008 as no-change. SDK-B1's "post-gate availability check only" then disappears as well.
2. **SDK-B1 adds a lock for a nit.** "Observation fields under a `Lock`" (SDK-035) is mechanism for a race the reviewer
   calls safe in practice; both packages call Start/Stop on their lifecycle lane. Drop it.
3. **SDK-B2 pruning fixes nothing.** Cancelled-entry pruning does not reach disposed sources (see Refuted). Keep the
   `CancelAsync` dispatch and fault observation. The internal `ActiveClockCore` test seam is justified by requirement 7
   (testability without live state) but should stay minimal; delete the pruning code and its test.
4. **SDK-B3 includes an unneeded `PeekMessage` step.** The stop race does not exist. Keep only the handler guard,
   the motion poll/event guards, the reconnect source dispose, and the trace fixes.
5. **SDK-B5 drops behaviour and violates a binding requirement.**
   - `ReconciliationBlockReason` enforcement in `DeviceServiceLifecycle` skips the Claw controller's stop-time release
     after a mid-cycle block, turns a disabled-and-blocked controller from Passive into Faulted, and misses the direct
     `AcquireAsync` call in `ClawControllerService.ReacquireAsync` and the ninth site. Replace with a doc fix.
   - The `RestoredUnverified` reinterpretation drops persisted recovery originals at the next save, against
     requirements.md item 11 and the Ally design. Take it out of B5; the actual readback gate is SDK-V-002 in DP01.
   - `IncludePredefined` consumers in `tests/WSGM.UiTests` are missing (SDK-V-005); its filters list no UiTests build.
   - Once the two items above are removed, B5 is mechanical (glyph caps, type moves, `ManifestRules`, identity matching,
     docs, version bump) and builds green on its own.
6. **SDK-B7 must preserve UI projections when the wrapper goes.** With the real plugin exposed, `is IConfigurablePlugin`
   / `IPluginUi` / `IPluginActions` / `IPluginSteamUi` become false for plugins that do not implement them, so
   `PluginRegistration.Settings` becomes null where it is `[]` today (`PluginHost.cs:304-311`). Every consumer
   (`CommonPluginManager.cs:214`, `CommonPluginSteamUiSource.cs:93,176`, overlay Tools and Settings rows) must treat
   null and empty alike, and the batch needs a UI parity check, because the requirement is identical UI.
7. **SDK-B8 must order removal before load and drop all count caps.** "Pending removals applied once at startup" fixes
   SDK-V-001 only if it runs before `ShellSession.cs:303`; write that ordering into the batch and add a test with
   Device Integration off. Remove `MaxPackageEntries` and `MaxPackageFiles` along with `MaxPackages` and the name cap
   (SDK-V-003); packers then validate the same byte bounds only.
8. **SDK-B9 keeps a forwarding call site** for `PerformanceProfileOwnsRole` "until D01 reshapes it". That is a
   temporary shim; move both callers in the same batch instead.
9. **SDK-B10 deletes regression guards the SDK guide asks for.** `ContractBoundaryTests` pins public-member documentation
   enforcement and the API version (the SDK AGENTS.md lists "dependency freedom, enum/API compatibility" among required
   tests), and `LowLevelKeyboardHookTests` guards the signed `GetMessage` import whose loss spins the hook thread on
   -1. Keep them; delete only the true duplicates (`PluginTextTests` after SDK-018, the constant-ratio clock test,
   the factory-equals-initializer test).
10. **Line anchors in sdk.md are unreliable in places.** Wrong: SDK-010 (`Plugin.Sdk/PluginManifestReader.cs:256-289`,
    `Packaging/PluginManifestReader.cs:186`), SDK-011 (`:294-447`, `PluginManifestValidator.cs:353-372`). Batches that
    reuse these anchors must re-derive them from symbols.
11. **SDK-B4 and A02_01 both edit `PluginManifestReader.cs` and `ManifestTests.cs`.** They are one change; execute
    SDK-B4 as the replacement for A02_01 (with step 3 and the GUID fixture choreography dropped, as the reviewer
    proposes), not in addition to it.
