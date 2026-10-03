# Device SDK, Plugin SDK and plugin host findings

Scope: `src/WSGM.Device.Sdk`, `src/WSGM.Plugin.Sdk`, both SDK test projects, the common plugin host
(`Shell/CommonPluginManager.cs`, `Shell/CommonPluginPackage.cs`, `Shell/PluginPackageLoader.cs`, `Shell/PluginHost.cs`,
`Shell/PluginCapabilityChannel.cs`), package discovery and install (`Core/PluginPackageFile.cs`,
`Core/PluginPackageCatalog.cs`, `Core/PluginPackageManager.cs`, `Core/CommonPluginEnablement.cs`),
`Shell/GpuCoordinator.cs`, the seven `plugin.wsgm.json` manifests plus the Device Lab template, and the packers
(`eng/plugin-manifest.cs`, `eng/package-plugin.ps1`, `eng/pack-device.ps1`). Sources: `_plan/refactor-2.1/review/sdk.md`, its
adversarial verification `sdk.verify.md`, the critic `_critic.md` (conflicts 16, 18, 19, 20, 26) and plan v2.

Line numbers in the review are often wrong (the verifier names SDK-010 and SDK-011 explicitly). Anchor every edit
by symbol; the lines below are orientation only.

Counts: 49 findings to implement (critical 0, high 1, medium 16, low 21, nit 11) and 7 refuted or no-change ids
(SDK-024 moved there by the solution check).

Plan v2 batches for this area: B010 (journal and serializer), B043 (active clock), B044 (native callbacks and
poll threads), B045 (bounded common manifest, replaces A02_01), B052 (Plugin API 4), B142 (Device API 12),
B146 (one loader), B147 (catalog, installer, layout, packers; decided: D2, the plan v2 byte bounds only), B148 (GPU coordinator), B149 (test
cleanup). Cross-domain owners: B022 (Ally restore, SDK-V-002), B081 (compatibility adapter, SDK-015), B082
(router rename, SDK-048). B148 keeps SDK-022 and the AC half of SDK-023 only (SDK-024 is no-change, the role
move belongs to B089).

Decisions that bind every SDK batch (critic conflict 18, plan v2 section 1): the process `ActiveClock` and the
static `PluginTrace` stay. There is no owned clock service, no clock-bearing `Deadline`, no
`DeadlineCancellation`, no clock shutdown join, no instance `PluginDiagnostics` and no `PluginTraceSink`. Common
plugins get one trace channel on `IPluginHost`. The A02_02 save-failure latch is not implemented (A02-F008 is
no-change in plan v2).

---

## High

### SDK-003: Recovery journal treats an unreadable record as absent and accepts undefined statuses

- **Severity:** high (verifier confirmed A02-F007 and A02-F009, rejected the A02-F008 latch part)
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`LoadAsync`, the `File.Exists(_path)` check
  around line 230; `SetStatusAsync`; `Validate`/`ValidateDocument` around 335-354)
- **Problem:** `LoadAsync` asks `File.Exists` first. `File.Exists` returns false for access denied, a directory at
  the path or any IO error, so an unreadable record loads as an empty journal and the next `BeginAsync` save
  overwrites it, losing the recovery originals of a crashed session (Claw power, fans, controller mode; Ally arm
  path). Status values are deserialized without `Enum.IsDefined`, so a corrupt or future-version file with an
  unknown status is treated as a valid entry, and `SetStatusAsync` accepts an undefined status and persists it.
- **Best solution:** Apply `batches/A02_02.md` steps 1 and 4 only.
  - Step 1: delete the `File.Exists` probe. Open the file directly; `FileNotFoundException` (and
    `DirectoryNotFoundException`, which cannot occur after the `Directory.CreateDirectory` just above but is the
    same "absent" meaning per critic conflict 16) leaves the journal empty and healthy. Every other failure
    (`UnauthorizedAccessException` including a directory at the path, other `IOException`, `JsonException`,
    `InvalidDataException`) keeps the file untouched and sets `FailureReason` so the journal is unavailable and
    never written.
  - Step 4: `Validate` rejects an entry whose `Status` is not `Enum.IsDefined`, so a loaded file with one is
    Corrupt (FailureReason set, file kept); `SetStatusAsync` rejects an undefined status with
    `ArgumentOutOfRangeException` before taking the gate or touching the file.
  - Do not add the post-gate availability re-check or the save-failure latch (A02_02 steps 2 and 3). A failed
    `SaveAsync` already throws and refuses its own mutation before the hardware write (`ClawPlugin.Commands.cs`
    around 506-513), and `_entries` only changes after a successful move, so a latch fixes no defect and would
    turn one transient file lock into the loss of every journalled control until restart. The existing pre-gate
    `ThrowIfUnavailable()` stays as it is.
- **Tests:** A02_02 fixtures for absent file (healthy, empty), directory at the record path (unavailable, nothing
  written), malformed JSON (unavailable, file bytes unchanged), undefined status in the file (unavailable, file
  unchanged), `SetStatusAsync` with `(DeviceRecoveryStatus)99` throws before IO. Run
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Services"`.
- **Plan v2:** B010 (after B008, the DEVICE-001 fix, per critic conflict 19).
- **Related:** A02-F007, A02-F009 (fixed here); A02-F008 (no-change); A02_02; critic conflicts 16 and 19; SDK-002
  and SDK-034 land in the same batch.

---

## Medium

### SDK-001: Active clock runs deadline cancellations inline on its own thread

- **Severity:** medium (verifier lowered from high)
- **Where:** `src/WSGM.Device.Sdk/Lifecycle/ActiveClock.cs` (`Run`, the `source.Cancel()` loop around 102-112);
  `src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj`
- **Problem:** Due sources are cancelled with `source.Cancel()` on the single AboveNormal clock thread. That runs
  every registered callback and every synchronous await continuation (plugin code after
  `await Task.Delay(x, token)`) inline on the clock thread. A blocking continuation stalls the only observer, so
  later deadlines fire late and the freeze-aware step accounting is distorted; a throwing callback raises
  `AggregateException` on a dedicated thread, which terminates the process (no in-repo registration throws
  today, which is why the verifier lowered the severity).
- **Best solution:** Keep the static facade, `Tick`, `MaximumStep`, `CountedStep`, `Now` and the one background
  thread exactly as they are.
  - Extract an internal `ActiveClockCore` constructed with `Func<long> timestamp` (Stopwatch ticks). It holds
    `_active`, `_lastTimestamp`, the `Pending` list and the lock, with `TimeSpan Now()`,
    `void CancelAt(long ticks, CancellationTokenSource source)` and
    `TimeSpan CollectDue(List<CancellationTokenSource> due)` (advances, moves due sources into `due`, returns the
    next wait clamped to 1 ms..`Tick`). `ActiveClock` owns one `new ActiveClockCore(Stopwatch.GetTimestamp)` and
    the thread; `Run` becomes wait, `CollectDue`, dispatch.
  - Dispatch each due source with `source.CancelAsync()` instead of `Cancel()`. Catch the synchronous
    `ObjectDisposedException` as today. Observe the returned task with one
    `ContinueWith(OnlyOnFaulted | ExecuteSynchronously, TaskScheduler.Default)` that reads `task.Exception` and,
    unless the inner exception is `ObjectDisposedException`, writes
    `PluginTrace.Failure("clock", "deadline cancellation callback failed", ...)` (a no-op without a sink).
    Callbacks and continuations then run on the pool and can never block or crash the clock thread.
  - Add `<InternalsVisibleTo Include="WSGM.Device.Sdk.Tests" />` to the csproj for the core.
  - Not done (plan v2): no pruning of cancelled entries (it cannot see disposed sources, SDK-040), no owned clock,
    no clock-bearing `Deadline`, no shutdown join.
- **Tests:** New `tests/WSGM.Device.Sdk.Tests/Lifecycle/ActiveClockTests.cs`: a fake timestamp shows a 5 s gap
  counted as `MaximumStep`; `CollectDue` returns only due sources and the right next wait; through the real
  facade, a source whose registered callback throws does not stop a second deadline from cancelling, and a
  callback registered on a `Deadline.After(50 ms)` source runs on a thread not named "WSGM active clock". Delete
  the constant-ratio test in `DeadlineTests.cs` (around 32-36). Run
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Lifecycle"`.
- **Plan v2:** B043.
- **Related:** A02-F003; critic conflict 18; SDK-040, SDK-041 (no-change).

### SDK-002: Serializer and journal dispose a semaphore that in-flight work still releases

- **Severity:** medium (verifier lowered from high; the journal half is not reachable today)
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceCommandSerializer.cs` (`Dispose`, around 73-77);
  `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`DisposeAsync`, around 52-57); callers
  `src/WSGM.Device.Msi.Claw/ClawPlugin.cs` (`DisposeAsync`), `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.cs`
  (`DisposeAsync`)
- **Problem:** `DeviceCommandSerializer.Dispose` disposes `_gate` while an observation pass, `RunAsync` or
  `ExecuteAsync` can still call `_gate.Release()` in its `finally`; that `Release` then throws
  `ObjectDisposedException` out of a command or the observation task. The journal disposes `_writeGate` the same
  way. The serializer race needs a command queued during stop, which admission closing makes rare, but it is
  real; the journal race was not demonstrated because every journal call runs inside the serializer lane or
  reconnect work that release joins.
- **Best solution:** Remove the mechanism. A `SemaphoreSlim` whose `AvailableWaitHandle` is never touched owns no
  OS handle, so it needs no disposal (the router already makes this choice, `DeviceCapabilityRouter.cs` around
  143-145).
  - `DeviceCommandSerializer.Dispose` keeps only `StopObservation()`; the `_gate.Dispose()` line is deleted.
    `IDisposable` stays on the serializer (packages call it).
  - `DeviceRecoveryJournal.DisposeAsync` stops disposing `_writeGate` and becomes a no-op in B010; Device API 12
    (B142) then removes `IAsyncDisposable` from the journal and the `await _journal.DisposeAsync()` lines in
    `ClawPlugin` (stop, `DisposeAsync`, `RollBackFailedStartAsync`; around 407, 441, 469) and `RogAllyPlugin`
    (around 350, 380, 435). The `_journal = null;` beside each stays, because the next cycle opens a fresh
    journal. The `await using var journal = ...OpenAsync(...)` locals in
    `tests/WSGM.Device.Msi.Claw.Tests/ClawModelLifecycleTests.cs` (around 109-332) and
    `tests/WSGM.Device.Asus.RogAlly.Tests/PluginTests.cs` (around 200, 654) become plain `var`, and the SDK
    journal tests drop any `await using` on `TestJournal`.
- **Tests:** Serializer: start a gated `RunAsync` whose operation awaits a `TaskCompletionSource`, call `Dispose`,
  complete the operation; the `RunAsync` task completes without `ObjectDisposedException`. Journal (B010 only, the
  test goes with `DisposeAsync` in B142): `DisposeAsync` while a `SetStatusAsync` waits on the gate completes
  cleanly. Filter
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Services"`;
  after B142 also `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj` and the Ally
  test project.
- **Plan v2:** B010 (gate no longer disposed), B142 (journal loses `IAsyncDisposable`).
- **Related:** A02-F005; SDK-V-004 (dispose chains, same B142 edit to the plugin `DisposeAsync`).

### SDK-004: Active-time deadlines converted to wall-clock timers

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceCommandSerializer.cs` (`RepublishAsync`, the `CancelAfter`
  around 279-282); `src/WSGM/Shell/PluginCapabilityChannel.cs` (command dispatch, `budget.CancelAfter(remaining)`
  around 217-218); `src/WSGM/Shell/CommonPluginManager.cs` (`StartWork.WaitAsync(TimeSpan.FromSeconds(15), ...)`
  around 282 beside the 15 s registration `Deadline` at 331; `StopEntryAsync` `WaitAsync(Remaining(deadline))`
  around 491 and 510)
- **Problem:** The SDK reference forbids turning a `Deadline` into a wall timer, because sleep and Modern Standby
  then consume the budget: a command or start in flight when the handheld sleeps is reported as timed out on
  wake although the plugin had no time to run. Each listed site reads `Remaining` once and arms a wall
  `CancelAfter`/`WaitAsync(TimeSpan)`.
- **Best solution:** Use the deadline's own active-time cancellation everywhere; no new type.
  - Serializer (B010): `using var bounded = command.Deadline.Earliest(Deadline.After(PostCommandLimit))
    .CreateCancellationSource(cancellationToken, _observationToken);` replaces the linked source plus
    `CancelAfter`.
  - `PluginCapabilityChannel` dispatch: `using var budget = command.Deadline.CreateCancellationSource(
    cancellationToken, lifetime);` replaces `CreateLinkedTokenSource` plus `CancelAfter(remaining)`; the
    `remaining <= 0` early return stays.
  - `CommonPluginManager` start: create one `var startDeadline = Deadline.After(TimeSpan.FromSeconds(15))` per
    entry, pass it to `StartEntryAsync` (used for `Registration.StartAsync`) and wait with
    `using var bounded = startDeadline.CreateCancellationSource(cancellationToken);
    await entry.StartWork.WaitAsync(bounded.Token)`. When `bounded` fired but the caller's token did not, set the
    same `entry.Error = "Plugin startup did not finish: ..."` text as today's `TimeoutException` path.
  - `StopEntryAsync`: wait with `using var bounded = deadline.CreateCancellationSource();` and map a cancellation
    from `bounded` to `TimeoutException("Plugin cleanup deadline expired.")` so the existing "Cleanup
    unconfirmed" flow and wording stay. `Remaining(deadline)` is deleted once unused.
  - `StopAsync` (around 360) is a shutdown wait and stays wall-clock; B140 owns shutdown budgets.
  - The start timeout keeps today's error text exactly: when `bounded` fired and `cancellationToken` did not,
    `entry.Error = "Plugin startup did not finish: The operation has timed out."` (the `TimeoutException`
    message the Settings and Steam rows show today); a caller cancellation keeps its current text.
- **Tests:** Serializer: post-command publish is cancelled when the command's active deadline expires (B010
  filter `FullyQualifiedName~Services`). Manager: an entry whose start never completes reports "Plugin startup
  did not finish" and stop of a stuck disposal reports cleanup unconfirmed
  (`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin"`). Channel: a
  command with an expired deadline returns Canceled
  (`--filter "FullyQualifiedName~PluginCapabilityChannel"`).
- **Plan v2:** B010 (serializer), B146 (manager waits and the `PluginCapabilityChannel` dispatch; B146 already
  lists that file). B148's "if B146 did not cover it" clause then has nothing left to do.
- **Related:** A02-F006; DEVICE-028 (router half in B082); critic conflict 18.

### SDK-005: Keyboard hook lets a handler exception escape the native callback

- **Severity:** medium (verifier refuted the stop-race half; only the callback guard remains)
- **Where:** `src/WSGM.Device.Sdk/Windows/LowLevelKeyboardHook.cs` (`Callback`, around 210-225)
- **Problem:** `Callback` is a reverse P/Invoke hook procedure. An exception from the plugin's handler propagates
  out of it, which fails the process (a managed exception cannot cross a native frame).
- **Best solution:** Wrap only the handler call:
  `try { if (handler(in key)) return 1; } catch (Exception ex) when (ex is not OutOfMemoryException) { PluginTrace.Failure("keyboard", $"{_threadName} handler failed", ex); }`
  and fall through to `CallNextHookEx`, so the key reaches Windows unswallowed and the hook keeps running. Do not
  route it through `ReportFault`: the `fault` callback is documented as "the hook's message loop ends without a
  stop request", and the Claw chord service answers it by marking itself Degraded with "The firmware chord
  suppressor stopped" (`ClawServices.cs`, `ReportFault` around 574-579), which would be false while the hook still
  runs. Keyboard events are user keystrokes, not a sample stream, so one trace line per failed key needs no
  latch. No `PeekMessage(PM_NOREMOVE)`, no `_stopping` re-check, no other change: `Run` checks `_stopping`
  before every `GetMessage`, so the claimed stop race cannot hang.
- **Tests:** No seam for the native hook; covered by the attended Claw and Ally rows in M01. Keep
  `LowLevelKeyboardHookTests` (signed `GetMessage` import guard). Build with
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Windows"`.
- **Plan v2:** B044.
- **Related:** plan "Native callbacks contain exceptions" rule; SDK-006.

### SDK-006: Motion poll thread has no fault boundary

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Windows/LegacyMotionStream.cs` (constructor's local `Deliver`, `Poll` around
  107-137); `src/WSGM.Device.Sdk/Windows/LegacyMotionSensors.Events.cs` (`SensorEventSink.OnDataUpdated`, around
  264-292)
- **Problem:** On the poll path an exception from the plugin's `onReading` ends the dedicated "WSGM IMU poll"
  thread with an unhandled exception and terminates the process. On the event path the same exception escapes
  `OnDataUpdated` (only COM, cast and invalid-operation exceptions are caught) and the COM wrapper turns it into
  an HRESULT the Sensor API discards silently, so the two paths disagree and neither reports it.
- **Best solution:** One guard in `Deliver`, which both paths already call:
  `try { onReading(reading); } catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_deliveryFaultTraced) { _deliveryFaultTraced = true; PluginTrace.Failure("motion", "IMU reading callback failed", ex); } }`.
  One bool field mirrors the poll loop's existing `failing` flag; nothing is traced per sample and nothing
  allocates on the success path. Delivery continues with the next reading.
- **Tests:** `LegacyMotionStream` has no fake sensor seam; covered by attended rows. If a seam exists for
  `Deliver` through `TrySubscribe`, add a test where the callback throws once and later readings still arrive.
  Run `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Windows"`.
- **Plan v2:** B044.
- **Related:** SDK-005, SDK-038, SDK-V-004.

### SDK-007: Glyph import imposes count and length caps and silently truncates SVG paths

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Glyphs/GlyphProfile.cs` (`GlyphProfileLimits`, around 31-68);
  `src/WSGM.Device.Sdk/Glyphs/GlyphPackageImporter.cs` (`MaxNoticePathLength` at 88 and every use of the count
  and length constants); `src/WSGM.Device.Sdk/Glyphs/GlyphAssetValidation.cs` (`GlyphSvgNormalizer`, the
  `MaxSvgCommands` refusal around 112-117 and the path loop around 179-198);
  `src/WSGM.Device.Sdk/Glyphs/ImmutableGlyphPackageDirectorySource.cs`; `src/WSGM.Device.Sdk/Glyphs/GlyphPackageLayout.cs`;
  `src/WSGM/Core/PluginPackageFile.cs` (`EnumerateProfileIds` `.Take(GlyphProfileLimits.MaxProfiles + 1)`);
  `tests/WSGM.Device.Sdk.Tests/Glyphs/SdkGlyphTests.cs`
- **Problem:** A glyph package is refused for more than 128 assets, 32 profiles, 64 controls or aliases, 32 exact
  devices, identifiers over 128, names over 128, labels over 32 characters or a notice path over 256. In the
  SVG projection, paths beyond the 256th or with data over 64 KiB are dropped without a diagnostic, while more
  than 4096 commands refuses the whole asset. This contradicts the SDK guide ("A length or count limit on
  published content rejects a valid plugin and protects nothing") and the API 11 note that removed these caps
  elsewhere.
- **Best solution:** Delete `MaxAssets`, `MaxProfiles`, `MaxControls`, `MaxAliases`, `MaxExactDevices`,
  `MaxIdentifierLength`, `MaxDisplayNameLength`, `MaxPhysicalLabelLength`, `MaxSvgPaths`, `MaxSvgCommands`,
  `MaxPathDataLength` and the importer's private `MaxNoticePathLength`, with every check that reads them and the
  `Take(MaxProfiles + 1)` in both sources. `GlyphSvgNormalizer` adds every non-blank path to the projection and
  stops counting commands (`CountCommands` goes with it). Keep only the byte and decode bounds plan v2 D2 lists:
  `MaxDocumentBytes`, `MaxAssetBytes` (also the XML reader's `MaxCharactersInDocument`), `MaxProfileBytes`,
  `MaxNoticeBytes`, `MaxDimension` and `MaxRasterPixels`. The importer's `MaxJsonDepth = 12` stays: it is a parser
  bound that refuses, not a content cap, and no review raised it. The asset byte bound already bounds the
  renderer's worst case. Fix the importer comment that cites `MaxAssets` as part of the aggregate bound (the
  `MaxProfileBytes` check is the bound). Type and identifier-character checks stay.
  - Docs in the same batch: `src/WSGM.Device.Sdk/docs/reference.md` "SVG rules" paragraph (around 784-790) drops
    the `MaxSvgPaths`/`MaxSvgCommands`/`MaxPathDataLength` sentence, and the "Limits at a glance" table (around
    914-929) loses the assets/profiles/controls/aliases/exact-devices row, the identifier/name/label row, the SVG
    row and the notice-path row.
- **Tests:** In `SdkGlyphTests.cs` replace the limit pins (around 100-124 and 201-238) with: a package with 200
  assets and 40 profiles imports; an SVG with 300 paths projects all 300; an SVG with a 100 KiB path keeps it; an
  asset over `MaxAssetBytes` is still refused. Run
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Glyph"` and
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Glyph"`.
- **Plan v2:** B142 (Device API 12). Decided: D2, the kept bounds are exactly the plan v2 list.
- **Related:** SDK-046, SDK-047 (same files and batch); maintainer no-arbitrary-limits rule.

### SDK-008: Arbitrary caps in package discovery

- **Severity:** medium
- **Where:** `src/WSGM/Core/PluginPackageCatalog.cs` (`MaxPackages` at 88, the `Take(MaxPackages + 1)` and refusal
  around 174-179); `src/WSGM/Core/PluginPackageFile.cs` (`TryNormalizeEntryName`, `name.Length > 260` around 342)
- **Problem:** More than 128 `.wsgmpkg` files makes discovery refuse the whole Plugins folder, so every plugin,
  including the device package, disappears. Entry names over 260 characters are refused although packages are
  never unpacked to disk.
- **Best solution:** Delete `MaxPackages`, the `Take` and the refusal; enumerate every package. Delete the
  `name.Length > 260` condition; the remaining normalization (no backslash, colon, leading slash, empty, `.` or
  `..` segment) stays. Per-file and total byte bounds stay (D2). The entry and file count caps go too
  (SDK-V-003).
- **Tests:** Discovery of 200 small fixture packages in a temp folder returns all of them; a package with a
  300-character entry name opens. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter
  "FullyQualifiedName~PluginPackage|FullyQualifiedName~PluginCatalog"`.
- **Plan v2:** B147 (decided: D2, only the plan v2 byte bounds stay; every count and name-length cap goes).
- **Related:** SDK-V-003, SDK-012, SDK-009.

### SDK-009: Discovery reads every package fully, repeatedly, on the UI thread, with side effects

- **Severity:** medium
- **Where:** `src/WSGM/Core/PluginPackageCatalog.cs` (`Discover`, `DiscoverInstalled`, `InstalledDevicePluginId`);
  `src/WSGM/Core/PluginPackageFile.cs` (`Open`, `ReadEntries` around 221-290); callers
  `src/WSGM/Settings/SettingsViewModel.cs` (around 20 and 195), `src/WSGM/Settings/SettingsViewModel.Plugins.cs`
  (around 403), `src/WSGM/Shell/ShellSession.cs` (around 784), `src/WSGM/Core/UpdateChecker.cs` (around 304),
  `src/WSGM/Shell/ShellSession.Actions.cs` (around 93), `src/WSGM/Shell/DeviceCoordinator.cs` (around 2736),
  `src/WSGM/Shell/CommonPluginManager.cs` (reconcile, around 168); `src/WSGM/Core/PluginPackageManager.cs`
  (`Rows`, `Hash` around 114 and 326-338)
- **Problem:** Every discovery opens each package with `PluginPackageFile.Open`, which decompresses every entry
  into memory and PE-parses every image. Settings does this on the UI thread at construction and refresh,
  `InstalledDevicePluginId` runs a full discovery to read one id, and `PluginPackageManager.Rows` then SHA-256
  hashes every package file. `DiscoverInstalled` also deletes pending removals as a side effect, which is what
  makes SDK-V-001 possible.
- **Best solution:** Fix the repetition, the thread and the side effect; keep one validation level.
  - Discovery keeps the full `PluginPackageFile.Open`. A manifest-only discovery (as the review proposed) would
    let a package with a native image, a corrupt entry or a duplicate name pass discovery: today it shows as a
    "Refused" row with its reason on the Plugins page and never becomes a candidate, so it cannot win the
    highest-version selection over a valid older file. Manifest-only discovery would show it as "Installed", let
    it shadow the valid file and fail only at load, which changes the page and the device selection. Packages are
    a few MiB, so one full read per discovery off the UI thread is cheap.
  - `PluginPackageCatalog` becomes `PluginCatalog` with `Discover(root)` free of side effects. Each installed
    entry (common and the device `InstalledPackage`) carries a `Sha256` computed in `Discover` from the package
    handle `Open` already holds (seek to 0, `SHA256.HashData(stream)`), so `Rows` no longer reopens and hashes
    every file. `Hash` itself moves with `Install` (SDK-031), which still hashes the bundled source file to match
    it against the bundle.
  - `DiscoverInstalled` is deleted. Startup composition applies pending removals once (SDK-V-001) and calls
    `Discover`. `InstalledDevicePluginId` reads `catalog.Device.InstalledPackage` (`Valid` and `Manifest.Id`, as
    today) from the snapshot it is handed instead of discovering.
  - Settings gets the catalog through its plugin-package service (B121): the session's
    `CommonPluginManager.Catalog` snapshot when a session runs, otherwise one `Discover` on a worker thread. No
    discovery on the UI thread, and one discovery per page load instead of three.
  - `UpdateChecker`, `ShellSession.Actions` and `DeviceCoordinator` call `Discover(InstallLayout.Plugins)`
    (already off the UI thread, or moved to `Task.Run` where they are not).
- **Tests:** `Discover` leaves a pending-removal file untouched; catalog entries carry the expected hash; a
  package carrying a native image still produces the same "Refused" row and is not a candidate; Settings
  construction performs no discovery on the calling thread (service fake).
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage|FullyQualifiedName~PluginCatalog"`.
- **Plan v2:** B147 (after B146 and B121; decided: D2, the plan v2 byte bounds only). Critic conflict 26: config B2a lands before it so
  constructors are not rewritten twice. B147's "manifest-only discovery" wording is superseded by the reason
  above (UI and device selection must not change); its "off the UI thread, hash per discovery" part stands.
- **Related:** SDK-V-001, SDK-012, SDK-031, SDK-032, SDK-010.

### SDK-011: Two manifest dialects with divergent duplicated validators

- **Severity:** medium (line references corrected by the verifier)
- **Where:** `src/WSGM.Device.Sdk/Packaging/PluginManifestValidator.cs` (`ValidateIdentifier`, `ValidateText`,
  `ValidateVersion`, `ValidateRelativeAssemblyPath` around 150-169, `ValidateEntryType`);
  `src/WSGM.Plugin.Sdk/PluginManifestReader.cs` (`Validate` around 59-152, `Identifier`)
- **Problem:** Both readers validate identity, version, entry assembly and entry type with different rules:
  identifiers (device allows uppercase via `PlainText.IsIdentifier`, common requires lowercase starting with a
  letter or digit), package version (device canonical, common any `Version.TryParse`), entry type (device allows
  a backtick, common does not), entry assembly (device allows a relative subpath although
  `PluginPackageFile.Open` refuses any non-root entry), and display name (device checks only non-blank, common
  rejects control and bidi characters through `PluginText`).
- **Best solution:** Keep both wire schemas. Add `public static class ManifestRules` in
  `src/WSGM.Device.Sdk/Packaging/ManifestRules.cs` with `IsPackageIdentifier`, `IsCanonicalVersion`,
  `IsRootAssemblyFileName`, `IsEntryTypeName` and `TryValidateName` (`PlainText.TryValidate`). Both readers call
  it and keep their own error codes and messages. The chosen rules, one per item:
  - identifier: lowercase ASCII letters, digits, `.`, `-`, `_`, first character a letter or digit (the common
    rule; ids name state folders on a case-insensitive file system, and every in-repo id already conforms);
  - version: canonical dotted numeric (the device rule; all manifests conform);
  - entry type: ASCII letters, digits, `.`, `_`, `+` (no backtick; an open generic type cannot be activated);
  - entry assembly: a root `.dll` file name of ASCII letters, digits, `.`, `_`, `-`, not starting with `.`
    (matches what `PluginPackageFile.Open` already enforces);
  - name: `PlainText.TryValidate` (SDK-V-007).
  `ValidateVersion`/`ValidateDottedVersion` collapse into `IsCanonicalVersion` (SDK-028).
- **Tests:** `ManifestRulesTests` with one case per rule; device manifest with `entryAssembly: "lib/x.dll"` is
  refused; device manifest with an uppercase id is refused; common manifest with version `1.02` is refused.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Packaging"`
  and `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj --filter "FullyQualifiedName~ManifestTests"`.
- **Plan v2:** B142.
- **Related:** SDK-V-007, SDK-028, SDK-012, SDK-010.

### SDK-012: Packers restate package rules the runtime enforces differently

- **Severity:** medium
- **Where:** `eng/package-plugin.ps1` (around 52-62: 4096 entries, managed check only); `eng/pack-device.ps1`;
  `eng/plugin-package-common.ps1`; `eng/build-bundle.ps1`; `eng/plugin-manifest.cs`;
  `src/WSGM/Core/PluginPackageFile.cs` (around 31-34, `ReadEntries`, `TryNormalizeEntryName`, `IsImageName`,
  `IsManagedImage`); the Device Lab copy in `PluginPackageWorkflow.cs` (around 44-47)
- **Problem:** The common packer admits archives the runtime then refuses (different entry count, no name, size or
  duplicate rules), the device packer has its own copy, and Device Lab a third. A package can build green and
  fail on the user's machine.
- **Best solution:** One rule set in `public static class PluginPackageLayout` in
  `src/WSGM.Device.Sdk/Packaging/PluginPackageLayout.cs`: `MaxFileBytes` (128 MiB), `MaxPackageBytes`
  (512 MiB), `TryNormalizeEntryName`, case-insensitive duplicate folding, `IsImageName`, `IsManagedImage`, and
  `ReadEntries(Stream)` that applies all of them to an archive and returns the entries (today's
  `PluginPackageFile.ReadEntries` body, moved). No count caps (SDK-V-003) and no manifest parsing in the layout:
  the common manifest reader lives in Plugin.Sdk, which Device.Sdk does not reference, so manifest routing stays
  in WSGM's `PluginPackageFile.ReadManifest` and in the packer script. `PluginPackageFile.Open` calls
  `ReadEntries`.
  - `eng/plugin-manifest.cs` (it references Plugin.Sdk, which brings Device.Sdk) gains `validate-package <file>`
    (runs `PluginPackageLayout.ReadEntries` plus the manifest reader and the root entry-assembly check) and
    `host-provided` (prints the host-owned assembly names, BUILD-014). `package-plugin.ps1` and `pack-device.ps1`
    drop their own checks (the 4096-entry count, the PE loop) and call `validate-package` on the produced
    archive.
  - Device Lab calls `PluginPackageLayout` in process (it already references Device.Sdk; shelling out to a
    `dotnet run` script would need an SDK on the tester's machine). Its count caps go too:
    `PluginPackageWorkflow.MaximumPackageEntries`/`MaximumPackageFiles` (around 44-45, the check around 395) and
    the entry-count refusal in `DeviceLabPackageSnapshot.cs` (around 80-89); its byte constants become the
    layout's. These two Device Lab files join B147's file list.
  - The bundle's SDK feed version is read from the csproj (BUILD-027).
- **Tests:** Layout tests for name normalization, duplicate folding, oversize entry and native image; packer
  validation agrees with the runtime on fixture archives built in temp paths; Device Lab packs a source tree of
  2000 small files;
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Packaging"`;
  `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Packag"`;
  one local `eng/package-plugin.ps1` run on `src/WSGM.Plugin.Ir` into a temp path.
- **Plan v2:** B147 (decided: D2, the 128 MiB per-file and 512 MiB total bounds stay, no count caps).
- **Related:** A02-F021, BUILD-014, BUILD-027, SDK-V-003, SDK-008.

### SDK-013: Device and common loaders duplicate each other and signal cleanup through exception type

- **Severity:** medium
- **Where:** `src/WSGM/Shell/PluginPackageLoader.cs` (around 48-141); `src/WSGM/Shell/CommonPluginPackage.cs`
  (`LoadAsync`, around 148-220); `src/WSGM/Shell/CommonPluginManager.cs` (around 337,
  `entry.Loaded is null && ex is AggregateException`)
- **Problem:** Both loaders repeat reopen, manifest comparison, entry type checks, activation, identity check and
  cleanup. The common path reports "construction cleanup unconfirmed" by throwing `AggregateException`, and the
  manager decodes the exception type to decide whether the instance stays reserved. A different exception type
  from a future edit silently frees an instance whose cleanup never ran.
- **Best solution:** `src/WSGM/Shell/PluginLoader.cs` replaces both: `Load<TEntry>(string packagePath,
  admitted manifest identity, entry rules)` returns either `LoadedPluginPackage<TEntry>` (owns the
  `PluginPackageFile`, the `PluginLoadContext` and the entry instance) or a `PluginLoadFailure(string Reason, bool
  CleanupConfirmed)`. The plugin is disposed exactly once, by whoever owns it at that moment: once admitted,
  `PluginRegistration.DisposeAsync` disposes it as today, and the manager then calls the loaded package's
  `Unload()` (unload the context, then close the file, the current order) only after that disposal completed;
  a package that was never admitted is released with `LoadedPluginPackage.DisposeAsync` (dispose the plugin,
  then `Unload()`). An unconfirmed disposal never reaches `Unload()`, so the context stays reserved as today. Device entry rules (`IDevicePlugin`) and common entry rules (category not Device, a
  `wsgm.gpu` entry must implement `ICapabilityPlugin`) are the only difference. `PluginLoadContext` moves
  unchanged as a nested type. `ConstrainPackagePath` moves to the state-directory code in `CommonPluginManager`.
  The manager reads `failure.CleanupConfirmed` instead of the exception type. `DevicePluginRuntime` changes only
  its load call.
- **Tests:** Adapt `CommonPluginManagerTests.UnconfirmedCleanupRetainsTheInstanceAndPreventsReplacement` to the
  explicit failure; a GPU package whose entry lacks `ICapabilityPlugin` is refused at load.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~PluginHost"`.
- **Plan v2:** B146 (after B142 and B081; serialize with any device-runtime writer).
- **Related:** SDK-014, SDK-030.

### SDK-015: Device runtime wrapped in a second lifecycle lane

- **Severity:** medium (verifier corrected the framing: an internal bridge, not a forbidden compatibility facade)
- **Where:** `src/WSGM/Shell/DeviceCoordinator.cs` (around 1060-1066);
  `src/WSGM/Shell/DevicePluginCompatibilityAdapter.cs`;
  `src/WSGM/Shell/PluginHost.cs` (`PluginRegistration.RunAsync`, around 548-621)
- **Problem:** The device runtime is admitted into `PluginHost` through a 139-line adapter, so every device start,
  suspend, resume and stop runs through `PluginRegistration.RunAsync` (its own `Task.Run`, semaphore, deadline
  and quarantine) on top of `DevicePluginRuntime`'s own lane: two deadlines and two quarantine models for one
  plugin.
- **Best solution:** Owned by the device domain. `DeviceCoordinator` calls the runtime's
  `Start/Suspend/Resume/Stop/DisposeAsync` directly; `DevicePluginCompatibilityAdapter.cs` is deleted;
  `PluginHost` no longer admits a Device category. Whether any surface still shows a device health row from
  `PluginHost.Snapshot` is decided from code: if one does, `DeviceCoordinator` publishes that row as plain data;
  if none does, the Device slot goes too.
- **Tests:** As B081: unverified stop followed by a new cycle; a plugin whose `StopAsync` never returns lets
  `StopAsync(deadline)` return at the deadline and keeps the load context.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~PluginHost"`.
- **Plan v2:** B081 (device domain, after B012 and B010).
- **Related:** DEVICE-002, DEVICE-030.

### SDK-016: Reconciliation block is documented as SDK-enforced but each service enforces it

- **Severity:** medium (the verifier reduced the fix to a documentation change)
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceService.cs` (doc on `ReconciliationBlockReason`, around 65-67);
  nine service sites: `ClawServiceBase.cs` (around 24), `ClawServices.cs` (around 136, 251),
  `ClawControllerService.cs` (around 106), `AllyServices.cs` (around 49, 107, 172, 236),
  `AllyControllerService.cs` (around 144)
- **Problem:** The doc says a service carrying the block "acquires and releases as faulted", but
  `DeviceServiceLifecycle` never checks it; each service decides. The services do not share one semantics: the
  controllers check only in acquire after `!Enabled` returns Passive, Claw controller release has no check so a
  controller blocked mid-cycle is still released at stop, and `ClawControllerService.ReacquireAsync` calls
  `AcquireAsync` directly. Central enforcement would skip that stop-time release, turn a disabled-and-blocked
  controller from Passive into Faulted and miss the direct reacquire.
- **Best solution:** Change only the doc: "Set when an outstanding recovery entry makes acquiring this service
  unsafe. It outlives the cycle. The service itself decides how it acquires and releases while it is set; the
  lifecycle walk does not check it." No SDK enforcement, no edits to the nine sites.
- **Tests:** None (documentation).
- **Plan v2:** B142.
- **Related:** sdk.verify batch problem 5.

### SDK-018: `PluginText` duplicates `PlainText`

- **Severity:** medium
- **Where:** `src/WSGM.Plugin.Sdk/PluginText.cs`; consumers `src/WSGM.Plugin.Sdk/PluginManifestReader.cs`,
  `PluginConfiguration.cs`, `src/WSGM.Plugin.Ir/IrPayload.cs`, `src/WSGM/Shell/CommonPluginActions.cs`,
  `src/WSGM/Core/PluginWidgetPins.cs` (Core, not Shell as B052's file list says);
  `tests/WSGM.Plugin.Sdk.Tests/PluginTextTests.cs`
- **Problem:** `PluginText` is a verbatim copy of `WSGM.Device.Sdk.Capabilities.PlainText` although Plugin.Sdk
  references Device.Sdk; the two can drift and every fix lands twice.
- **Best solution:** Delete `PluginText.cs`. Every consumer calls `PlainText.TryValidate` (both overloads are
  identical) and `PlainText.IsUnsafe`. Delete `PluginTextTests.cs`; the PlainText tests in the Device SDK test
  project already cover the behaviour.
- **Tests:** `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj` and the Ir test project.
- **Plan v2:** B052 (Plugin API 4); the test deletion is listed again in B149.
- **Related:** SDK-019, SDK-039.

### SDK-V-001: Removed common plugins load again before their pending removal is applied

- **Severity:** medium (verifier-found)
- **Where:** `src/WSGM/Shell/CommonPluginManager.cs` (reconcile, around 168); `src/WSGM/Core/PluginPackageCatalog.cs`
  (`DiscoverInstalled`, around 112-116); `src/WSGM/Shell/ShellSession.cs` (around 298-311);
  `src/WSGM/Shell/DeviceCoordinator.cs` (around 926, 994); `src/WSGM/Core/PluginPackageManager.cs` (around
  249-271)
- **Problem:** Removing a loaded package on the Plugins page records it as pending and tells the user "Removed at
  the next start". Pending removals are applied only inside `DiscoverInstalled`. The common manager discovers
  with `Discover` (no apply), and its first reconcile starts before `DeviceCoordinator.TryStartAsync`, whose
  discovery is the only startup caller of `DiscoverInstalled` and runs only with Device Integration on. With
  integration off, a removed IR or GPU package is loaded and held open on every start, and the next
  `DiscoverInstalled` (opening Settings) cannot delete it because of the sharing violation. With integration on
  it is a race.
- **Best solution:** In `ShellSession` startup composition, inside the existing `if (!_overlayTestOnly)` block
  (overlay test never touches packages) and immediately before `new CommonPluginManager(...)`, which runs before
  `DeviceCoordinator.TryStartAsync`, apply pending removals once, synchronously, regardless of Device
  Integration: one call `new PendingPluginRemovalStore(InstallLayout.PendingPluginRemovals)
  .Apply(InstallLayout.Plugins)` (SDK-031). `DiscoverInstalled` and its side effect are deleted (SDK-009). No new
  state.
- **Tests:** With Device Integration off: a temp Plugins root holding a package listed in the pending store; after
  the startup step the file is gone and the manager's first catalog does not contain it. `Discover` alone never
  deletes a pending package.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage|FullyQualifiedName~PluginCatalog|FullyQualifiedName~CommonPlugin"`.
- **Plan v2:** B147.
- **Related:** SDK-009, SDK-031.

### SDK-V-002: Ally restore gates writes and controls on readback

- **Severity:** medium (verifier-found, cross-domain: Ally package)
- **Where:** `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs` (`AllyPowerCapability.RestoreAsync` around
  151-172, `WriteOrder` around 174-200, `WriteOrderedAsync` around 230-252, `RestoreModeAsync` around 254-272,
  `AllyFanCapability.RestoreAsync` around 488-499); `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Recovery.cs`
  (`ReconcileOutstandingAsync`, around 17-77); `src/WSGM.Device.Asus.RogAlly/AllyServices.cs` (power and fan
  `ReleaseAsync`, around 112-147 and 245-275); `src/WSGM.Device.Asus.RogAlly/AllyRecoveryJournal.cs` (`ArmAsync`,
  `Decide`)
- **Problem:** The power restore writes the performance mode, reads it back and returns false without writing the
  captured limits when the mode readback differs; after writing the limits it returns the readback comparison.
  The fan restore does the same. At cycle start any false is recorded as `RestoreFailed` and sets
  `ReconciliationBlockReason`, and `AllyServices.AcquireAsync` faults the service, so TDP and fan controls
  disappear for the cycle. At stop a false becomes `RestoredUnverified` plus `ReleasedUnverified`, and that entry
  then waits for an explicit command to re-arm it. This is readback machinery on a vendor that cannot read back.
- **Best solution:** Decided by D8 and D9: the Ally restore mirrors HC and carries no readback machinery. A
  restore either dispatched (resolved) or failed to dispatch (a write threw).
  - `RestoreModeAsync` writes the mode unconditionally (no `Scalar` compare before or after), waits `ModeSettle`
    and returns dispatched or failed. `RestoreAsync` then writes SPL, then SPPT and FPPT, the order of HC's
    `set_long_limit` followed by `set_short_limit` (`ROGAlly.cs` around 694-702), each value written without the
    skip-if-equal check, with today's `WriteSpacing` between writes. It returns true when every write
    dispatched; a throw is the failure.
  - `WriteOrder`, its SPL <= SPPT <= FPPT remarks and the `current` parameter of `WriteOrderedAsync` are deleted;
    the command path `ApplyLimitsAsync` writes in the same HC order (D8, B022). The `!original.LimitsReadable`
    early return stays: `PrepareWriteAsync` journals only an original with readable limits and mode, so it is
    unreachable from the journal.
  - `AllyFanCapability.RestoreAsync` writes the channels and returns true; the `Read`/`Same` comparison goes.
  - Power and fan `ReleaseAsync`: a dispatched restore sets `RestoredVerified` (the status that removes the
    entry), a throw sets `RestoreFailed` and faults as today. The `RestoredUnverified` and `ReleasedUnverified`
    branches are deleted. `ReconcileOutstandingAsync` resolves a dispatched restore and blocks the service for the
    cycle only when a write threw.
  - No Ally entry waits on a re-arm (D9, owned by B143): `Decide` loses its `Block` branch for
    `RestoredUnverified`/`RestoreFailed`, `ArmAsync` loses its re-arm of a non-pending entry, and a
    `RestoreFailed` entry, whose write never reached the device, is restored at the next cycle start like a
    `Pending` one. The firmware-mismatch `ReportOnly` rule stays. The SDK journal keeps `RestoredUnverified` and
    its `BeginAsync` refusal for the Claw, the one vendor with readback (SDK-017).
- **Tests:** Rebuild the Ally tests that relied on a readback mismatch (`IgnoreWritesTo`) on `FailWritesTo`: a
  mismatched readback resolves the entry and keeps controls; the restore and command writes go SPL, then SPPT,
  then FPPT, even when a value already equals the current one; a throwing write records `RestoreFailed`, and the
  next start restores that entry; no Ally path records `RestoredUnverified` or `ReleasedUnverified`.
  `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~AcpiCapabilityTests|FullyQualifiedName~PluginTests"`.
- **Plan v2:** B022 (packages domain, after B010) for the restore, order and release changes; B143 for `Decide`
  and `ArmAsync`. Decided: D8, HC order SPL then SPPT+FPPT with the stepping rule removed; D9, no readback
  machinery for the Ally.
- **Related:** PACKAGES-005; SDK-017 (refuted for the SDK, this is the real defect it reached for).

---

## Low

### SDK-010: Common manifest parse is unbounded, including the host's routing parse

- **Severity:** low (verifier lowered from medium: the Plugins root is administrator-protected; line references
  corrected)
- **Where:** `src/WSGM.Plugin.Sdk/PluginManifestReader.cs` (`TryRead`, around 21-54);
  `src/WSGM/Core/PluginPackageFile.cs` (`ReadManifest`, the `JsonDocument.Parse` routing parse);
  `src/WSGM.Device.Sdk/Packaging/PluginManifestReader.cs` (its `NotSupportedException` catch around 76-79 as the
  model); `tests/WSGM.Plugin.Sdk.Tests/ManifestTests.cs`
- **Problem:** The common reader deserializes without the 256 KiB `ManifestLimits.MaxDocumentBytes` or depth 16
  bound the device reader applies, and it does not catch `NotSupportedException`. `PluginPackageFile.ReadManifest`
  parses the manifest entry (up to 128 MiB) with `JsonDocument` before either reader's bound applies.
- **Best solution:** Implement A02_01 steps 1, 2 and 4 as B045: `TryRead` refuses `bytes.Length >
  ManifestLimits.MaxDocumentBytes` before parsing, deserializes with `MaxDepth = ManifestLimits.MaxDepth`, and
  catches `NotSupportedException` alongside `JsonException`. `PluginPackageFile.ReadManifest` checks the same
  length before `JsonDocument.Parse` and throws `InvalidDataException("The plugin manifest exceeds ...")`. Drop
  A02_01 step 3 (CLI `FileStream` growth detection) and the GUID-directory fixture choreography;
  `eng/plugin-manifest.cs` is not touched here. The bound refuses, never truncates (D2).
- **Tests:** 262144-byte manifest accepted, 262145 refused; depth 17 refused; parallel reads; a package whose
  manifest entry exceeds the bound is refused before parse.
  `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj --filter "FullyQualifiedName~ManifestTests"`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage"`.
- **Plan v2:** B045 (replaces A02_01; critic conflict 20).
- **Related:** A02-F001, A02_01, SDK-009, SDK-011.

### SDK-014: `CommonPluginPackage` fakes every optional plugin interface

- **Severity:** low (verifier lowered from medium: no user-visible effect today)
- **Where:** `src/WSGM/Shell/CommonPluginPackage.cs` (around 14-15, 59-66, 184-187); `src/WSGM/Shell/CommonPluginManager.cs`
  (around 214, 317-318); `src/WSGM/Shell/PluginHost.cs` (around 304-311); `src/WSGM/Shell/CommonPluginSteamUiSource.cs`
  (around 93, 176)
- **Problem:** The wrapper implements `IConfigurablePlugin`, `IPluginActions`, `IPluginUi`, `IPluginSteamUi` and
  `ICapabilityPlugin` for every package and forwards with type checks. Every host `is` check is therefore true:
  `PluginHost` builds `CommonPluginSettings` for every package, a non-configurable plugin reports
  `PluginConfigurationOutcome.Applied`, and the manager needs a `PublishesCapabilities` special case that
  duplicates the loader's own GPU entry check.
- **Best solution:** Delete the wrapper (SDK-013). The host works on `LoadedPluginPackage<IPlugin>.Plugin`, the
  real instance, so interface checks tell the truth; `PublishesCapabilities` is deleted. The only projection
  that changes is `PluginRegistration.Settings`, which becomes null for a plugin that is not
  `IConfigurablePlugin` instead of an empty schema; `CommonPluginActions` already reads actions, UI
  contributions, widgets and Steam UI with `as`/`is`, so it is unaffected. The `Settings` consumers already
  treat null like empty (`CommonPluginManager` reconcile skips `Settings: null` around 213,
  `CommonPluginSteamUiSource` uses `owner.Settings?.` around 93 and 143-155); check the overlay and Settings
  plugin rows do the same, so Settings, overlay and Steam rows stay identical. A non-configurable plugin is no
  longer sent a configuration it cannot apply, so it stops reporting `Applied`.
- **Tests:** A non-configurable plugin is not reported as configured; Settings and Steam rows for IR and a GPU
  fixture are unchanged. `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~PluginHost"`;
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings"`.
- **Plan v2:** B146.
- **Related:** SDK-013, SDK-030; sdk.verify batch problem 6.

### SDK-019: Plugin SDK README is wrong in four places

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Sdk/README.md` (line 5 "Graphics" placement, 13 "It depends on nothing", 15
  "compatibility adapter", 40 "bounded" reader)
- **Problem:** The README says the SDK has no dependencies (it references Device.Sdk and SteamUiToolkit), names a
  left-menu Graphics entry removed in 7f7fee74, advertises a compatibility adapter, and calls the reader bounded
  before B045.
- **Best solution:** Rewrite those four statements after B045 and the API 4 changes: list the real references
  (Device.Sdk for shared capability and text types, SteamUiToolkit for `ISteamUiModule` types, and that those
  toolkit types are part of the Plugin API 4 contract per TOOLKITCS-V-001), describe where GPU controls appear
  today (overlay pages and Steam Quick Access), drop the adapter claim, state the 256 KiB and depth 16 manifest
  bound, and document `permissions` as metadata (SDK-V-006).
- **Tests:** None (prose). Run `npm run format` (Markdown).
- **Plan v2:** B052.
- **Related:** A02-F002, SDK-V-006, TOOLKITCS-V-001.

### SDK-020: Common plugins have no log channel except through the capability host

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Sdk/PluginCapabilities.cs` (`ICapabilityHost.Trace`/`TraceChange`, around 36-47);
  `src/WSGM.Plugin.Sdk/PluginContracts.cs` (`IPluginHost`); `src/WSGM/Shell/PluginCapabilityChannel.cs`
  (`Trace`/`TraceChange`/`TryFormat`, around 139-181); `src/WSGM/Shell/PluginHost.cs` (`PluginRegistration`);
  `src/WSGM/Shell/DevicePluginRuntime.cs` (adapter `Trace`/`TraceChange`, around 1028-1070);
  `src/WSGM.Plugin.IntelGpu/IntelLog.cs`; `src/Shared/Gpu/DriverRuntime.cs` (around 400, 411, 470, 518, 579-589)
- **Problem:** Only capability plugins can log, because tracing lives on `ICapabilityHost`. IR has no way to
  write to wsgm.log, Intel wraps the capability host in its own `IntelLog`, and the host formats plugin log lines
  in two places.
- **Best solution:** Move `Trace(DeviceTraceLevel, string scope, string message)` and
  `TraceChange(DeviceTraceLevel, string scope, string key, string message)` from `ICapabilityHost` to
  `IPluginHost`, as default-bodied no-op members like the existing `PublishState`, so fakes need no change.
  `PluginRegistration` implements them. Move `Normalize`, the line building and the level mapping out of
  `PluginCapabilityChannel` and the device runtime adapter (`DevicePluginRuntime.cs` around 1028-1101) into one
  internal static formatter (`src/WSGM/Shell/PluginLogLine.cs`) taking the prefix, so every line stays exactly
  as today: common plugins `plugin/<pluginId>/<scope>: <message>` with change key
  `plugin/<pluginId>/<scope>/<key>`, the device package `plugin/<scope>: <message>` with key
  `plugin/<scope>/<key>`. Each owner keeps its own "still open" check (`_closed` under the channel lock, the
  adapter's `_disposed`, the registration's stop state) before calling the formatter. `IntelLog` takes `IPluginHost`; `DriverRuntime` keeps an
  `IPluginHost` beside its capability host for tracing. Device plugins keep `PluginTrace` (critic conflict 18).
- **Tests:** A fixture plugin logs through `IPluginHost.Trace` and the line carries the instance prefix; a
  `TraceChange` repeat is suppressed.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~PluginHost|FullyQualifiedName~PluginCapabilityChannel"`
  plus the IntelGpu, NvidiaGpu, AmdGpu and Ir test projects.
- **Plan v2:** B052.
- **Related:** critic conflict 18; SDK-021.

### SDK-021: `PluginTrace` reads its sink without a barrier and writes unsanitized exception text

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Plugin/PluginTrace.cs` (`Install`, `Change` around 108, `Failure` around
  135-139, `Write` around 143); `src/WSGM.Device.Sdk/Plugin/DiagnosticText.cs`
- **Problem:** `_sink` is written and read as a plain field across threads. `Failure` writes
  `ex.Message` raw, including newlines and control characters, while `DiagnosticText.FromException` already
  sanitizes the same content; a multi-line exception message breaks the one-line log format.
- **Best solution:** `Install` uses `Volatile.Write(ref _sink, sink)`; `Write` and `Change` use
  `Volatile.Read(ref _sink)`. `Failure` becomes
  `Write(DeviceTraceLevel.Warn, scope, DiagnosticText.FromException(context, ex))`. Keep the static (critic
  conflict 18). The line shape moves from `context: Type: message` to `DiagnosticText`'s
  `context (Type): message` (plus a Win32 error code when there is one); that is log text only, no UI.
- **Tests:** `PluginTrace.Failure` with a message containing `\r\n` and a bidi control writes one line with those
  characters replaced by spaces.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~PluginTrace"`.
- **Plan v2:** B044.
- **Related:** SDK-020.

### SDK-022: Static mutable sync revision in an instance owner

- **Severity:** low
- **Where:** `src/WSGM/Shell/GpuCoordinator.cs` (`private static long _syncRevision` around 70; use around 593)
- **Problem:** Process-global mutable state inside an owner built per session; two coordinators (tests, or a
  rebuilt session) share and skew one counter. Plugins keep revisions in memory only, so an instance field is
  enough.
- **Best solution:** Make `_syncRevision` an instance field. Keep the existing documented guarantee within one
  coordinator (a stopped and restarted publisher never sees a revision repeat). The NVIDIA `<=` versus Intel `<`
  equal-revision difference belongs to the GPU domain (GPUIR-040) and is not changed here.
- **Tests:** Two coordinators in one process issue independent revisions.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GpuCoordinator"`.
- **Plan v2:** B148.
- **Related:** GPUIR-040; PV04 static inventory.

### SDK-023: GPU coordinator calls device-coordinator statics

- **Severity:** low
- **Where:** `src/WSGM/Shell/GpuCoordinator.cs` (`DeviceCoordinator.PerformanceProfileOwnsRole` around 305 and
  330; `DeviceCoordinator.ReadOnAcPower()` around 518); `src/WSGM/Shell/DeviceCoordinator.cs`
  (`PerformanceProfileOwnsRole` around 2339, its own caller around 2189; `ReadOnAcPower` around 289);
  `src/WSGM/Shell/CapabilityUserWrites.cs`; `src/WSGM/Shell/ShellSession.cs`
- **Problem:** The GPU owner depends on the device coordinator type for a pure role rule and for a native power
  read, which couples two owners and leaves the GPU coordinator untestable without live power state.
- **Best solution:** The role half is already done by the time B148 runs: B089 (DEVICE-010) moves
  `PerformanceProfileOwnsRole` into `CapabilityUserWrites` and points both coordinators at it, and DEVICE-010
  says B148 must not move it a second time. B148 only checks no `DeviceCoordinator.PerformanceProfileOwnsRole`
  reference is left. For the power read, `GpuCoordinator` takes a `Func<bool?> onAcPower` constructor argument;
  `ShellSession` passes the reader the device side uses at that point (`DeviceCoordinator.ReadOnAcPower` today, or
  the power owner's reader once DEVICE-009 moved it). `UpdateContext` keeps "unknown reads as AC".
- **Tests:** AC/DC desired-value selection follows the injected source.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GpuCoordinator"`.
- **Plan v2:** B148 (after B089). B148's spec line "with both PerformanceProfileOwnsRole callers moved in this
  batch" is superseded by B089, which does that move.
- **Related:** sdk.verify batch problem 8; DEVICE-010, DEVICE-009; B090.

### SDK-025: Host reuses `CommandOutcome.Accepted` with a different meaning

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Capabilities/CapabilityCommand.cs` (enum `CommandOutcome.Accepted`, around 63-66);
  `src/WSGM/Shell/CapabilityUserWrites.cs` (`StoreForApplicationAsync`, around 89 and 120);
  `src/WSGM/Shell/GpuCoordinator.cs` (around 272); consumers `src/WSGM/Shell/NativeQamSemanticServices.cs` (around
  113), `src/WSGM/Shell/SteamGraphicsService.cs` (around 88)
- **Problem:** The SDK documents `Accepted` as "Validated and queued. Nothing has reached the hardware yet." No
  plugin returns it; the host returns it for "saved to the running game's profile, the driver applies it at the
  next sync", and two consumers treat it as success. Readers of the contract cannot know that.
- **Best solution:** Make the one meaning explicit instead of adding a type: rewrite the `Accepted` summary to
  "Validated and kept for a later apply; nothing has reached the hardware yet. WSGM returns it for a native
  per-application value saved to the running game's profile, which the driver applies from the next sync." The
  host code and both consumers stay as they are. A new outcome value would ripple through every switch over
  `CommandOutcome` for no behavioural gain.
- **Tests:** None (documentation).
- **Plan v2:** listed under B146, but the only edit is in `CapabilityCommand.cs`, which B142 (a dependency of
  B146) already rewrites for SDK-027: make it there, and B146's "stops reusing `Accepted`" item needs no host
  change.
- **Related:** SDK-027.

### SDK-026: Host-only `CapabilityStateDelta` lives in the contract

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Capabilities/CapabilityStateDelta.cs`; consumers
  `src/WSGM/Shell/DeviceCapabilityRouter.cs` (around 82, 568), `src/WSGM/Shell/DevicePluginRuntime.cs` (around
  165, 961), `src/WSGM/Shell/PluginCapabilityChannel.cs` (around 135, 187), `ICapabilityPublisher.cs` (around 29),
  `tests/WSGM.Tests/Fakes/FakeCapabilityPublisher.cs`
- **Problem:** Only WSGM constructs and consumes the type, yet it is public SDK surface that packages must carry
  and that constrains future host changes.
- **Best solution:** Move it to `src/WSGM/Shell/CapabilityStateDelta.cs` as `internal sealed record
  CapabilityStateDelta(long Sequence, CapabilityState State)` in namespace `WSGM.Shell`; update the usings of the
  five consumers. Breaking, so it rides Device API 12. Remove its sentence from
  `src/WSGM.Device.Sdk/docs/reference.md` (around 395).
- **Tests:** Build; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Capability"`.
- **Plan v2:** B142.
- **Related:** SDK-043.

### SDK-027: `ReadbackValue` contract text contradicts the factory and the no-readback rule

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Capabilities/CapabilityCommand.cs` (`ReadbackValue` doc);
  `src/WSGM.Device.Sdk/Capabilities/CommandResults.cs` (`Unverified`, around 29-39)
- **Problem:** The doc says `ReadbackValue` is "present only for AppliedVerified", but `CommandResults.Unverified`
  sets it to the written value for `AppliedUnverified`, which is how a package publishes the written value as
  observed. A package author following the doc would leave it empty and lose the published value.
- **Best solution:** Rewrite the doc: "The value the device now holds as WSGM should publish it: the readback for
  AppliedVerified, the written value for AppliedUnverified. Absent when nothing was applied."
- **Tests:** None (documentation).
- **Plan v2:** B142.
- **Related:** SDK-025; no-hard-readback rule.

### SDK-029: Two identity comparison rules

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Identity/HardwareMatchRule.cs` (`HardwareMatcher.Field` around 141-155 and
  `Contains` around 125); `src/WSGM.Device.Sdk/Identity/IdentityText.cs` (`Normalize`, `Matches`)
- **Problem:** `HardwareMatcher` compares with `Trim` plus ignore-case, while `IdentityText.Normalize` also
  collapses internal whitespace and the snapshot docs claim values arrive normalized. A BIOS string with a doubled
  space matches in one path and not the other, so installer detection and runtime matching can disagree.
- **Best solution:** `Field` uses `IdentityText.Matches(observed, expected)`; `Contains` normalizes both sides
  with `IdentityText.Normalize` before its ordinal-ignore-case `Contains`. Explanations keep their text.
- **Tests:** A rule `"ROG  Ally"` (doubled space) matches an observed `"ROG Ally"` and vice versa; existing Ally
  identity tests stay green.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Identity"`
  and the Ally test project.
- **Plan v2:** B142.
- **Related:** consumers `WSGM.Install`, Device Lab.

### SDK-030: Common manifest record is mutable

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Sdk/PluginManifest.cs` (setters around 74-107); `src/WSGM/Shell/CommonPluginPackage.cs`
  (`Snapshot`, around 222-231)
- **Problem:** Public `set` accessors let any holder change a manifest after validation, which forces the host to
  take defensive copies.
- **Best solution:** Change every setter to `init`. Delete `CommonPluginPackage.Snapshot` (the file is dissolved in
  B146; in B052 the copy just becomes unnecessary and is removed). Fix any test that mutates a manifest to use
  `with`.
- **Tests:** `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj` and
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin"`.
- **Plan v2:** B052.
- **Related:** SDK-014.

### SDK-031: `PluginPackageManager` mixes UI projection, file operations and persistence

- **Severity:** low
- **Where:** `src/WSGM/Core/PluginPackageManager.cs` (row projection and badge text around 101-225, `Install` and
  `Remove` around 232-271, `PendingPluginRemovals` around 345-434); `tests/WSGM.Tests/Core/PluginPackageManagerTests.cs`
- **Problem:** Core holds Settings-page text and badge tones; the pending-removal store writes non-atomically,
  reads an unreadable file as empty and then overwrites it (losing pending removals), and `Forget` reads twice.
  Tests reach the live `InstallLayout.PendingPluginRemovals` file. (`Remove` letting
  `UnauthorizedAccessException` escape is not a defect: its only caller, `SettingsViewModel.ActOnPackageAsync`
  around 167-187, catches it and shows "Changing the Plugins folder needs administrator rights.")
- **Best solution:** Split by owner, same names and values:
  - `src/WSGM/Settings/PluginPackageRows.cs`: `PluginPackageAction`, `PluginBadgeTone`, `PluginBadge`,
    `PluginPackageSection`, `PluginPackageRowState`, `Rows`, `Facts`, `AdapterVendors`, `Provenance`, `Contact`;
    the installed hash comes from the catalog entry (SDK-009). UI text unchanged.
  - `src/WSGM/Core/PluginPackageInstaller.cs`: `Install` (with `Hash`, still used for the bundled source file),
    `Remove`, `IsInside`, with the Plugins root passed in. Exception behaviour unchanged, so the caller's
    messages stay identical.
  - `src/WSGM/Core/PendingPluginRemovalStore.cs`: constructed with an explicit file path. `TryRead(out string[]
    entries)` returns true with an empty array when the file is absent (`FileNotFoundException`/
    `DirectoryNotFoundException`, opened directly, no `File.Exists`, critic conflict 16), true with the entries
    when it parses, and false when it is unreadable or malformed. `Add`, `Forget` and `Apply` return without
    writing (one `Log.Warn`) when `TryRead` is false, so an unreadable list is never overwritten; `Rows` treats
    false as "no pending removals". Writes go through `AtomicFile.WriteText`; `Forget` reads once.
    `PendingRemovalsJsonContext` stays with it.
- **Tests:** Rows tests use explicit temp paths; an unreadable store is reported and not overwritten; `Apply`
  keeps a locked file pending.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage"`.
- **Plan v2:** B147 (after B121, which moves the Settings calls behind services).
- **Related:** SDK-009, SDK-V-001, SDK-032, SDK-039; critic conflict 16.

### SDK-032: Adapter inventory is a process-global lazy that never refreshes

- **Severity:** low
- **Where:** `src/WSGM/Core/CommonPluginEnablement.cs` (around 22-30); `src/WSGM/Settings/SettingsViewModel.Plugins.cs`
  (around 205); `src/WSGM/Shell/CommonPluginManager.cs` (its injected adapter seam around 78, `EnabledByDefault`
  around 97)
- **Problem:** GPU plugin default enablement reads a `Lazy` adapter list computed once per process
  (`CommonPluginEnablement.PresentAdapters`). The manager's default adapter seam is that same `Lazy`
  (`CommonPluginManager` constructor around 78), so a docked or newly enabled GPU is seen by neither until WSGM
  restarts. Settings also disagrees with itself: `LoadPluginPackages` passes a fresh
  `DisplayAdapterInventory.Collect()` to `PluginOffers.Compute` (around 144-145) while `LoadCommonPlugins` decides
  enablement from the stale `Lazy` (around 205).
- **Best solution:** Delete the `Lazy` and `PresentAdapters`; `ReadAdapters` (with its existing log line and
  failure fallback) becomes `internal static IReadOnlyList<DisplayAdapterIdentity> ReadAdapters()` and the
  one-argument `EnabledByDefault(manifest)` overload goes, leaving only the pure rules that take the adapter
  list. The manager's default seam calls `ReadAdapters` at each reconcile that has a graphics package (it already
  reads only then, around 177). Settings asks the manager (`CommonPluginManager.EnabledByDefault`) through its
  plugin-package service when a session runs; otherwise the service reads the adapters once per page load on its
  worker and uses that one list for both the offers and the enablement default.
- **Tests:** Enablement follows the adapter list passed in; Settings offers and default enablement use the same
  adapter read.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPluginEnablement"`.
- **Plan v2:** B147.
- **Related:** SDK-031, B121.

### SDK-034: Startup rollback ignores its deadline

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceServiceLifecycle.cs` (`RollBackStartAsync`, around 160-187)
- **Problem:** The rollback releases services and retracts publications with `CancellationToken.None` although the
  doc says the context carries a fresh deadline, so a hung release blocks the start failure path past its
  budget.
- **Best solution:** `using var rollback = context.Deadline.CreateCancellationSource();` and pass
  `rollback.Token` to `ReleaseAllAsync` and the three `TryRetractAsync` publications.
- **Tests:** A service whose release never completes lets `RollBackStartAsync` return once the context deadline
  passes. `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Services"`.
- **Plan v2:** B010.
- **Related:** SDK-004.

### SDK-037: Trace emitted under the per-sample motion lock

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Input/MotionSampleBuilder.cs` (`Build` around 75, `ReportCalibration` around
  101-119)
- **Problem:** `Build` runs at the sensor rate and calls `ReportCalibration` under `_gate`, which can call
  `PluginTrace.Info` (log I/O) while holding the lock other sensor threads wait on. The lines are rate-limited by
  bias change, but log I/O sits on the high-rate path.
- **Best solution:** `ReportCalibration` returns the message (`string?`) instead of tracing; `Build` stores it in a
  local, leaves the lock, then calls `PluginTrace.Info("motion", message)` only when it is non-null. The string
  is built only when the rate limit already allows a line, so the per-sample path allocates nothing.
- **Tests:** A builder fed a stationary sequence emits exactly one calibration line and one uncalibrated notice
  through a test sink. `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Input"`.
- **Plan v2:** B044.
- **Related:** high-rate rule.

### SDK-039: SDK test quality

- **Severity:** low
- **Where:** `tests/WSGM.Device.Sdk.Tests/*`, `tests/WSGM.Plugin.Sdk.Tests/*`,
  `tests/WSGM.Tests/Core/PluginPackageManagerTests.cs`
- **Problem:** Some tests pin constants or copies instead of behaviour (`DeadlineTests` constant ratio,
  `CapabilityValueFactoryTests` factory-equals-initializer, `PluginTextTests` duplicating PlainText), glyph limit
  pins must go with SDK-007, `PluginPackageManagerTests` reads live machine state, and behaviour is untested for
  `StartResult`, `StopResult`, `ReasonFor`, `Ownership`, `DiagnosticText`, `CommandResults`, `IdentityText` and
  `OemButtonLatch`.
- **Best solution:** Delete only true duplicates: `PluginTextTests` (after SDK-018), the constant-ratio clock test
  (if B043 did not), the factory-equals-initializer test. Keep `ContractBoundaryTests` (public-member
  documentation enforcement, API version, dependency freedom; the SDK guide requires these) and
  `LowLevelKeyboardHookTests` (guards the signed `GetMessage` import whose loss spins the hook thread on -1).
  Add behavioural tests for the eight types listed. Live-state reads in `PluginPackageManagerTests` are fixed by
  SDK-031's explicit paths.
- **Tests:** `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj` and
  `dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj` in full.
- **Plan v2:** B149 (after B148 and B147).
- **Related:** SDK-018, SDK-031, SDK-007; sdk.verify batch problem 9.

### SDK-047: PNG inspector refuses ordinary metadata chunks

- **Severity:** low
- **Where:** `src/WSGM.Device.Sdk/Glyphs/GlyphAssetValidation.cs` (`GlyphPngInspector.Inspect`, the chunk refusal
  around 397)
- **Problem:** `tEXt`, `zTXt` and `iTXt` are refused together with the animation chunks, but common exporters
  write them into static PNGs, so ordinary glyph artwork is rejected.
- **Best solution:** Remove `tEXt`, `zTXt` and `iTXt` from the refusal list so they are skipped like every other
  ancillary chunk. `acTL`, `fcTL` and `fdAT` stay refused (animation). The asset byte bound still applies.
- **Tests:** A static PNG with a `tEXt` chunk imports; an APNG is still refused.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Glyph"`.
- **Plan v2:** B142.
- **Related:** SDK-007, SDK-046.

### SDK-V-003: Package entry and file count caps protect nothing

- **Severity:** low (verifier-found)
- **Where:** `src/WSGM/Core/PluginPackageFile.cs` (`MaxPackageEntries` and `MaxPackageFiles` around 31-32, the
  checks in `ReadEntries` around 236-241 and 261)
- **Problem:** `MaxPackageEntries` (1024) is checked after `ZipArchive` has already parsed the whole central
  directory and allocated every entry, so it bounds nothing; `MaxPackageFiles` (512) refuses a valid package by
  count. The real limits are the 512 MiB file length check before opening and the per-file and total byte
  bounds.
- **Best solution:** Delete both constants and both checks. Keep `MaxPackageFileBytes`, `MaxPackageBytes` and the
  file-length check (D2), moved into `PluginPackageLayout` (SDK-012). Do not copy any count into the packers.
- **Tests:** A package with 2000 small entries opens.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage"`.
- **Plan v2:** B147 (decided: D2, both count caps go, the byte bounds stay).
- **Related:** SDK-008, SDK-012, A02-F021.

### SDK-V-004: Plugin disposal chains stop at the first throwing owner

- **Severity:** low (verifier-found, cross-domain: device packages)
- **Where:** `src/WSGM.Device.Msi.Claw/ClawPlugin.cs` (`DisposeAsync`, around 415-446);
  `src/WSGM.Device.Msi.Claw/WindowsMotionSource.cs` (around 51-69); `src/WSGM.Device.Sdk/Windows/LegacyMotionStream.cs`
  (`Dispose` throws `TimeoutException` after 2 s, around 60-94); `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.cs`
  (`DisposeAsync`, around 359-385)
- **Problem:** `ClawPlugin.DisposeAsync` awaits each owner in sequence without a guard. A motion stream whose poll
  thread is stuck in a synchronous sensor read throws `TimeoutException`, which skips the controller, MCU, OEM
  event and WMI disposals and the serializer. The Ally chain has the same shape. Skipped owners keep HID handles
  and threads alive until process exit.
- **Best solution:** Dispose every owner and collect failures, the pattern `DeviceCoordinator` teardown already
  uses. In both plugins: `List<Exception> failures = []` and a local
  `async ValueTask Step(string name, Func<ValueTask> dispose)` that awaits and, on any non-OOM exception, traces
  `PluginTrace.Failure("plugin", name + " disposal failed", ex)` and adds it. The stop call and every owner go
  through `Step` in today's order; the journal line is gone (SDK-002); the serializer is last. If any failed,
  throw `new AggregateException(failures)` at the end. `LegacyMotionStream.Dispose` keeps its retained-owner
  timeout.
- **Tests:** Claw and Ally plugin tests with a motion fake whose dispose throws: every other owner is still
  disposed and `DisposeAsync` throws `AggregateException` carrying the one failure.
  `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj` and
  `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj`.
- **Plan v2:** B142.
- **Related:** SDK-002, SDK-006, PACKAGES-026.

### SDK-V-005: `IncludePredefined` move misses three UiTests call sites

- **Severity:** low (verifier-found)
- **Where:** `tests/WSGM.UiTests/Overlay/ControllerNavigationTests.cs` (around 89),
  `tests/WSGM.UiTests/Overlay/DevicePageCaptureTests.cs` (around 45), `tests/WSGM.UiTests/Visual/PreviewExports.cs`
  (around 45); `tests/WSGM.Device.Sdk.Tests/Capabilities/SdkCapabilitySectionTests.cs` (around 144-173)
- **Problem:** SDK-043 moves `DeviceSections.IncludePredefined` into the host; these test call sites and the SDK
  section tests would break the build if the move forgot them.
- **Best solution:** In the same batch as SDK-043, point the three UiTests sites at the host method (WSGM already
  grants `InternalsVisibleTo("WSGM.UiTests")`) and move the three `SdkCapabilitySectionTests` cases that exercise
  `IncludePredefined` into `tests/WSGM.Tests` beside the router tests.
- **Tests:** `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests|FullyQualifiedName~DevicePageCaptureTests"`
  and `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Capability"`.
- **Plan v2:** B142.
- **Related:** SDK-043.

---

## Nit

### SDK-028: Duplicated validation helpers

- **Severity:** nit
- **Where:** `src/WSGM.Device.Sdk/Capabilities/DevicePowerPair.cs` (`IsLimit`, around 135-147);
  `src/WSGM.Device.Sdk/Capabilities/DevicePowerPreset.cs` (`IsPowerLimit` around 94-106, `Fits` around 133);
  `src/WSGM.Device.Sdk/Packaging/PluginManifestValidator.cs` (`ValidateVersion`, `ValidateDottedVersion`);
  `CapabilitySection.TryValidate` and `CapabilityCategory.TryValidate` (custom-title rule)
- **Problem:** The same rule is written twice in each pair, so a fix can land in one copy only.
- **Best solution:** `DevicePowerPreset.IsPowerLimit` calls (or is replaced by) one internal
  `DevicePowerPair.IsLimit`; `Fits` reuses the range check `TryResolve` already performs; the two version
  validators become `ManifestRules.IsCanonicalVersion` (SDK-011); the custom-title rule becomes one internal
  helper both `TryValidate` methods call. No behaviour change.
- **Tests:** Existing capability and packaging tests.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Capabilities|FullyQualifiedName~Packaging"`.
- **Plan v2:** B142.
- **Related:** SDK-011.

### SDK-033: Inconsistent event threading and undocumented entry flag ownership

- **Severity:** nit
- **Where:** `src/WSGM/Shell/CommonPluginManager.cs` (`Changed` raised from pool threads around 298, 342, 531;
  `Entry.Suspended` written inside the `Task.WhenAll` lambdas around 456); `src/WSGM/Shell/GpuCoordinator.cs`
  (`Changed` raised on the UI dispatcher)
- **Problem:** Two sibling owners raise `Changed` on different threads without saying so, and the entry flags look
  unsynchronized. Both are safe today: the only subscriber (`CommonPluginSteamUiSource`) locks its own gate, and
  every `Entry` field is read and written while the manager's `_gate` semaphore is held (`Task.WhenAll` completes
  before release).
- **Best solution:** Documentation, no mechanism: the XML doc on `CommonPluginManager.Changed` states it is raised
  on the thread that finished the operation and subscribers marshal themselves; a comment on `Entry` states its
  fields are touched only while `_gate` is held.
- **Tests:** None.
- **Plan v2:** B146.
- **Related:** SDK-049.

### SDK-036: `DeviceReconnect.Start` leaks the previous cancellation source

- **Severity:** nit
- **Where:** `src/WSGM.Device.Sdk/Windows/DeviceReconnect.cs` (`Start`, around 48-50)
- **Problem:** After a wait that ended on its own (device found or attempt threw), `Start` overwrites
  `_cancellation` without disposing it; only `StopAsync` disposes it.
- **Best solution:** Inside the lock, after the `_loop.IsCompleted` check, call `_cancellation?.Dispose()` before
  assigning the new source. The previous loop has completed, so nothing still uses it.
- **Tests:** Start, let the attempt return true, Start again, Stop: no exception and the second wait runs.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Windows"`.
- **Plan v2:** B044.
- **Related:** none.

### SDK-038: One RCW allocated per sensor report on the event path

- **Severity:** nit
- **Where:** `src/WSGM.Device.Sdk/Windows/LegacyMotionSensors.Events.cs` (`SensorEventSink.OnDataUpdated`, around 276)
- **Problem:** `Marshal.GetUniqueObjectForIUnknown(report)` allocates one wrapper per report on a high-rate path.
  It is released immediately, chosen over the shared wrapper to avoid finalizer pressure.
- **Best solution:** No code change. Plan v2 allows a fix only "where it is a simple hoist", and it is not: the
  report pointer differs every call, so there is nothing to cache, and the allocation-free alternative (calling
  the `ISensorDataReport` vtable through function pointers) adds more mechanism than it saves. Record the
  exception in the existing comment above the call ("an accepted exception to the zero-allocation rule: the COM
  Sensor API hands a new report each time") so the next review does not re-raise it.
- **Tests:** None.
- **Plan v2:** B044.
- **Related:** SDK-006; high-rate rule.

### SDK-042: API version history kept in two places

- **Severity:** nit
- **Where:** `src/WSGM.Device.Sdk/DeviceApi.cs` (XML remarks around 7-75);
  `src/WSGM.Device.Sdk/docs/reference.md` (API history table around 977-987). There is no repository-root
  `docs/reference.md`; B142's file list means the SDK one.
- **Problem:** The full history is duplicated in XML remarks and the reference table and will drift.
- **Best solution:** Keep the table in `src/WSGM.Device.Sdk/docs/reference.md` and add the API 12 row there;
  also set the "Limits at a glance" API version row (around 917) to 12. Reduce the `DeviceApi` remarks to the
  current version's summary and a pointer to the reference (the package README already links
  `docs/reference.md` relative to the SDK folder).
- **Tests:** None. Run `npm run format` for the Markdown.
- **Plan v2:** B142.
- **Related:** none.

### SDK-043: Host-only helper `DeviceSections.IncludePredefined` in the contract

- **Severity:** nit
- **Where:** `src/WSGM.Device.Sdk/Capabilities/DeviceSections.cs` (`IncludePredefined`, around 50); callers
  `src/WSGM/Shell/DeviceCapabilityRouter.cs` (around 542), `src/WSGM/Shell/DeviceOverlayBridge.cs` (around 374,
  1289)
- **Problem:** Only WSGM uses it, but it is public SDK surface that packages carry.
- **Best solution:** Move the method unchanged to an internal static class in `src/WSGM/Shell` (for example
  `DeviceSectionLayout.IncludePredefined`), keep the predefined section constants in the SDK, and update the host
  and test callers (SDK-V-005). Reword `src/WSGM.Device.Sdk/docs/reference.md` (around 249), which tells hosts to
  use `IncludePredefined`, to say WSGM adds the predefined sections itself. `DeviceServiceLifecycle.Ownership` and `DeviceRecoveryJournal.DiagnosticState`
  stay in the SDK (shared package vocabulary).
- **Tests:** As SDK-V-005.
- **Plan v2:** B142.
- **Related:** SDK-V-005, SDK-026.

### SDK-046: Glyph asset bytes copied three times

- **Severity:** nit
- **Where:** `src/WSGM/Core/PluginPackageFile.cs` (`TryRead`, `bytes = [.. stored]` around 128);
  `src/WSGM.Device.Sdk/Glyphs/GlyphPackageImporter.cs` (`suppliedBytes.ToArray()` around 249);
  `src/WSGM.Device.Sdk/Glyphs/GlyphAssetValidation.cs` (`SvgUtf8 = bytes.ToArray()` around 125,
  `RasterPng = bytes.ToArray()` around 467)
- **Problem:** Every asset is copied by the source, again by the importer and a third time into the imported asset.
- **Best solution:** Keep the one copy at the source boundary (`IGlyphPackageSource.TryRead` hands out an array the
  caller owns). The importer uses that array directly; `GlyphSvgNormalizer.Normalize` and
  `GlyphPngInspector.Inspect` take `byte[]` and store the same array as `SvgUtf8` / `RasterPng`. The
  `compressedImage.ToArray()` for the concatenated IDAT stream is a different buffer and stays.
- **Tests:** Existing glyph import tests.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Glyph"`.
- **Plan v2:** B142.
- **Related:** SDK-007, SDK-047.

### SDK-048: Generic router carries a device name

- **Severity:** nit
- **Where:** `src/WSGM/Shell/GpuCoordinator.cs` (around 137, `new DeviceCapabilityRouter(...)` per GPU publisher);
  `src/WSGM/Shell/DeviceCapabilityRouter.cs`
- **Problem:** The router serves GPU publishers too, but its name says Device.
- **Best solution:** Rename the type to `CapabilityRouter` only if it is a trivially local rename when B082 already
  edits the router; otherwise leave the name. No behaviour change.
- **Tests:** Build; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCapabilityRouter"`.
- **Plan v2:** B082 (device domain).
- **Related:** none.

### SDK-049: Registration health written outside the host lock

- **Severity:** nit
- **Where:** `src/WSGM/Shell/PluginHost.cs` (`PluginRegistration.StopAsync` writes `Health` around 465;
  `PluginHost.Publish` writes it under `_gate` around 186-196; `Context` replaced per operation around 573)
- **Problem:** The stop path writes `Health` without the lock `Publish` and `Snapshot` use. Reference writes make it
  benign today, but the two writers do not follow one rule.
- **Best solution:** Add `internal void SetHealth(PluginRegistration owner, PluginHealthPublication publication)`
  on `PluginHost` that assigns under `_gate`, and have the stop path call it instead of assigning directly. No
  `IsCurrent` check and no UI post, so behaviour stays as today. `Context` stays as it is.
- **Tests:** Existing `PluginHost` tests.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginHost"`.
- **Plan v2:** B146.
- **Related:** SDK-033.

### SDK-V-006: Common manifest `permissions` is validated and copied but never read

- **Severity:** nit (verifier-found)
- **Where:** `src/WSGM.Plugin.Sdk/PluginManifest.cs` (around 89-90); `src/WSGM.Plugin.Sdk/PluginManifestReader.cs`
  (around 136-141); `src/WSGM/Shell/CommonPluginPackage.cs` (around 227)
- **Problem:** No host, setup or UI code consumes `permissions`; only IR declares `usb.serial`. A contract field
  nobody reads needs an explicit disposition.
- **Best solution:** Keep it as documented metadata (plan v2): the XML doc on `Permissions` and the README say the
  host records but does not grant or enforce declarations. Validation stays as is.
- **Tests:** None.
- **Plan v2:** B052.
- **Related:** SDK-019.

### SDK-V-007: Device manifest name accepts control and bidi characters

- **Severity:** nit (verifier-found)
- **Where:** `src/WSGM.Device.Sdk/Packaging/PluginManifestValidator.cs` (`ValidateText`, around 121-130)
- **Problem:** The device package name is checked only for non-blank but is shown on the Plugins page and in logs,
  where control and bidi characters corrupt lines and misrepresent text. Common manifests already reject them.
- **Best solution:** `ValidateText` keeps the `MissingField` check for a blank name, then calls
  `ManifestRules.TryValidateName` (`PlainText.TryValidate`) and reports a failure with the existing
  `ManifestValidationCode.InvalidText` and a "The package name contains control or formatting characters."
  message. Part of SDK-011.
- **Tests:** A device manifest whose name contains U+202E is refused.
  `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Packaging"`.
- **Plan v2:** B142.
- **Related:** SDK-011.

---

## Refuted or no-change

- **SDK-017** (journal `RestoredUnverified` as a write gate): refuted for the SDK. The Claw is the one vendor
  with readback (D9) and never records `RestoredUnverified` for a successful write; dropping persisted
  `RestoredUnverified` entries would discard its recovery originals, so the SDK journal semantics stay. The Ally
  stops recording `RestoredUnverified` and loses its re-arm rule under D9; that is SDK-V-002 (B022, B143).
- **SDK-024** (GPU coordinator leaves untracked work): no change; B148 drops it. The "untracked" publisher
  disposals finish synchronously: `GpuPublisher.DisposeAsync` unhooks events, calls `Channel.Dispose()` (which
  closes the channel under its lock, sets `_open = false` and cancels its lifetime), `Router.Detach()` and
  `Router.DisposeAsync()`, which returns `ValueTask.CompletedTask` on every path (`DeviceCapabilityRouter.cs`
  around 132-149), so nothing is still disposing when `Open`, `Close` or `DisposeAsync` return. A refresh started
  by `Run` waits on `_refreshGate` with the cancelled `_lifetime` token, re-checks `_closed`/`Channel.IsOpen`
  under that gate, and any command it still issues is refused by the closed channel; `_refreshGate` is never
  disposed and `TryGetLifetime` already tolerates the disposed source. A `_pending` task list would be new
  mechanism with no defect behind it.
- **SDK-035** (serializer observation fields unsynchronized): no change. Packages call Start and Stop on their
  lifecycle lane; a lock would be mechanism for a safe race.
- **SDK-040** (A02-F004, `ActiveClock` retains disposed sources): accepted bounded no-change. Registrations live at
  most 20 s and pruning cannot see disposed sources, so the proposed pruning fixes nothing.
- **SDK-041** (clock wakes four times a second): no change. The 250 ms observation is what makes freeze detection
  work and the plan preserves it.
- **SDK-044** (two plugin settings contracts): accepted no-change. Merging needs a stored-value migration and UI
  work nobody asked for.
- **SDK-045** (two icon vocabularies): accepted no-change. Both are documented and serve different surfaces.
