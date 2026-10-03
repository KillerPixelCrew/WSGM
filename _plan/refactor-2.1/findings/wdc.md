# WindowsDeviceControl (WDC) findings

Scope: the whole `external/windows-device-control` child (library, tests, csproj files, README, docs, guidance) at child
`main` 2f07485 plus the uncommitted W02_01 EDID edit, and every WSGM consumer where a WDC contract crosses the boundary
(`src/WSGM`, `tests/WSGM.Tests`, `tests/WSGM.UiTests`, `src/WSGM.Plugin.NvidiaGpu`). The Intel GPU plugin and Device Lab
do not reference WDC.

This file has 32 findings: 0 critical, 0 high, 5 medium, 16 low, 11 nit. It merges `review/wdc.md`, its adversarial
verification (`review/wdc.verify.md`, whose corrections are applied here and whose five missed findings are WDC-V-001 to
WDC-V-005), the completeness critic, plan v2 and the solution checker (WDC-C-001, plus corrections to WDC-003,
WDC-004, WDC-010 and WDC-023). Plan v2 batches covering this area: B001, B002, B046, B063, B064, B065,
B066, B067, B069, B070, B071, B072 (WDC, in that order), plus B038 and B068 in the config domain. B064 no longer
waits: D4 is decided (approved). A table at the end maps the confirmed U01 ledger rows to their batches.

The maintainer's answers in `_plan/refactor-2.1/DECISIONS.md` (2026-10-03) are applied and override plan v2 where they differ. For
this area that means D9 (no readback machinery on any host path: a display write either dispatched and is published as
written, or failed to dispatch; WDC-003, WDC-004, WDC-010, WDC-019, WDC-023, WDC-025 and WDC-V-001 follow it), D2 (only
the listed byte bounds stay; WDC-007 now also removes the 64-suffix profile name search cap) and D4 (B064's guidance
diff is approved). No WDC finding depended on a dropped security item, so the counts are unchanged.

Rules for whoever implements these:

- Line numbers are approximate; many in the original review were wrong. Anchor every edit by symbol.
- Publication is per batch: commit and push the child on `main` first, then one parent commit with the gitlink and
  the consumer edits (`git commit -- <paths>`). No commit message mentions HC.
- WDC test command, written below as "WDC filter X":
  `dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "X"`,
  then the same with `-f net10.0-windows10.0.19041.0` from B046 on (net10 does not exist before B046).
- The public facades stay static. Do not add per-instance services, a subscriber hub, a display service per host, a
  persistence parameter, an EDID "upgrade" migration, exact registry-kind capture, an active-clock pairing deadline,
  token overloads on every blocking call, or a WaveOut lock. Plan v2 dropped all of them.

---

## Medium

### WDC-001: WDC display records are WSGM's persisted config and recovery format, with no fixture guarding them

- **Severity:** medium (verifier kept it and widened the set from four records to five)
- **Where:** `src/WSGM/Core/GameModeLaunchConfiguration.cs` (`KnownDisplay.Target`, `KnownDisplay.Modes`, `GameLayout`,
  `WaitForDisplay`, `DesktopLayout`, `GameModeLaunchRecovery.PendingReturnLayout`); `src/WSGM/Core/AppConfig.cs`
  (`ConfigJsonContext` options, around :1073); `src/WSGM/Shell/GameModeReturnRecovery.cs:60-73`; records `DisplayTargetIdentity`
  (`external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.Types.cs:14`), `DisplayLayout` and
  `DisplayLayoutOutput` (`DisplayLayouts.cs:46`), `DisplayRefresh`, `DisplayMode` (`DisplayModes.cs:13`).
- **Problem:** `config.json` serializes these five WDC records directly, including the computed `IsPrimary` and
  `Hertz` properties. `PendingReturnLayout` is Windows recovery state. Nothing pins the JSON shape, so any rename,
  retype or new positional parameter without a default in a later batch (B067 and B069 both touch these records)
  silently breaks loading of existing configs and recovery originals.
- **Best solution:** As the first step of B067, before any record changes, add a WSGM test with a real-shaped 2.0
  `config.json` fixture that contains `KnownDisplays` (with `Modes`), `GameLayout`, `WaitForDisplay`, `DesktopLayout` and
  `GameModeLaunchRecovery.PendingReturnLayout`, including identities with and without EDID ids and `Rotation` values 1
  and 2. Produce the fixture once by serializing such a config with today's code, so it carries the computed
  `IsPrimary` and `Hertz` members exactly as 2.0 writes them. The test deserializes with `ConfigJsonContext`, asserts
  the records' values, reserializes and asserts JSON equality of those display subtrees with the fixture (the whole
  file would also compare unrelated defaults). Rules for every later batch: never rename, retype or remove a public property of the five records; a new
  positional parameter on `DisplayLayoutOutput` must carry a default (old JSON lacks it); `DisplayLayoutOutput.Rotation`
  keeps its default of 1. Do not add a WDC-side reflection pin test (a library pinning one consumer's file format is
  consumer policy inside the library) and do not add WSGM DTO copies (a compatibility layer with no defect behind it).
  B072's README may say neutrally that the records are plain positional data and that members are only ever added with
  defaults.
- **Tests:** new `tests/WSGM.Tests/Core/ConfigurationDisplayRecordsTests.cs` plus a fixture file;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration"`. B068 later extends the
  same fixture for the migration.
- **Plan v2:** B067 (first step); B068 uses the fixture.
- **Related:** CONFIG-V-008; WDC-V-001 (rotation semantics change touches `DisplayLayoutOutput`); WDC-004 and WDC-010
  (result types change in B069, not the persisted records).

### WDC-004: display set results treat "written but the readback differs" as failure

- **Severity:** medium (verifier kept it and found more consumers)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayScaling.cs` (`TrySet`, :53-100),
  `DisplayColor.cs` (`TrySetHdr`, :42-91), `DisplayModes.cs` (`Apply`, :120-183), `DisplayTopology.Types.cs`
  (`DisplayProfileResult`); consumers `src/WSGM/Core/DisplayScale.cs` (`TrySetScale` ~:258, restore loop ~:147-154),
  `src/WSGM.Plugin.NvidiaGpu/NvOutputControls.cs:212-218`, `src/WSGM/Overlay/DisplayModeView.cs:195-215`,
  `src/WSGM/Core/DisplayProfiles.cs`, `src/WSGM/Shell/DisplayLayoutDiagnostics.cs`.
- **Problem:** `TrySet` and `TrySetHdr` return `false` both when Windows refused the write and when the write
  succeeded but the readback did not match. That bakes a readback gate into the contract. It already drives behaviour:
  `DisplayScale` keeps the restore entry and rewrites it later on a readback mismatch (an automatic retry of a write
  Windows accepted), and the NVIDIA plugin throws `DriverFailure` when the HDR readback is inconclusive.
- **Best solution:** D9 decides this: no readback machinery on host paths, so a set either dispatched (published as
  written) or failed to dispatch. In B069 delete the post-write `TryRead` comparison in `DisplayScaling.TrySet` and
  `DisplayColor.TrySetHdr` and replace the bool plus `out string detail` shapes with typed results that keep the
  native status:
  - `public enum DisplaySetOutcome { AlreadySet, Written, Refused, NotActive, Unsupported, Unreadable }`. `Written`
    means `DisplayConfigSetDeviceInfo` returned 0; nothing is read after the write. `Refused` carries the non-zero
    status. The pre-write read stays because it is input to the write, not a confirmation: the DPI packet needs the
    current and recommended steps to compute its relative index, and HDR needs the support bit. `Unreadable` means that
    pre-write read failed, so nothing was written. `AlreadySet` is today's no-op when that same read already shows the
    requested value.
  - `public readonly record struct DisplaySetResult(DisplaySetOutcome Outcome, int NativeStatus)` with
    `public bool Succeeded => Outcome is AlreadySet or Written;` for HDR, and
    `DisplayScaleResult(DisplaySetOutcome Outcome, int NativeStatus, int Percent)` for scaling, where `Percent` is the
    snapped step (WSGM needs it for today's text).
  - `DisplayScaling.TrySet(target, percent, out detail)` becomes `DisplayScaleResult Set(target, percent)`;
    `DisplayColor.TrySetHdr(target, enabled, out detail)` becomes `DisplaySetResult SetHdr(target, enabled)`. HDR on
    a display without HDR support: `enabled == false` is `AlreadySet`, `enabled == true` is `Unsupported`. Scaling
    whose recommended step is not in the step table (today's "is not a scaling step this display offers") is
    `Unsupported`. Doc comments drop "confirms it by readback" and say the request is issued once.
  - `DisplayModes.Apply` returns `DisplayModeResult` (see WDC-010 and WDC-003 for its outcomes).
  Consumers: `DisplayScale.TrySetScale` returns `Succeeded`, so the restore loop removes an entry once the write
  dispatched (`AlreadySet` or `Written`) and keeps it only when nothing was written (`Refused`, `NotActive`,
  `Unreadable`, `Unsupported`), exactly as it keeps a `false` today. No entry waits on a readback any more.
  `NvOutputControls` throws only when either HDR call is not `Succeeded`; the plugin keeps a private map from
  `DisplaySetOutcome` to today's library phrases ("Windows refused the HDR change (status {status})", "the display is
  not active, so its colour state was left alone", "its colour state could not be read", "this display does not
  support HDR") so its `DriverFailure` text reads as today for every remaining failure. Layout extras
  (`DisplayLayouts.ApplyOutputExtras`) warn only for an outcome that is not `Succeeded`. The NVIDIA package must be
  rebuilt in the same batch because `PluginLoadContext` resolves WDC host-first and a package built against 0.1.0
  would fail with `MissingMethodException`.
- **Tests:** WDC: an internal pure `DisplayScaling.Classify(...)`/`DisplayColor.Classify(...)` over (pre-write read
  available, current value, supported, set status) tested as a table in a new `DisplaySetResultTests`, including that
  status 0 is `Written` with no further read; WDC filter
  `FullyQualifiedName~DisplayLayoutTests|FullyQualifiedName~DisplaySetResultTests` (B069's batch filter names only
  `DisplayLayoutTests`; add the new class to it). WSGM: a `DisplayScale` restore test where a `Written` result removes
  the entry and a `Refused` result keeps it;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Display"`; NVIDIA:
  `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B069. D9 overrides B069's "Written (written, readback differs)": `Written` means status 0 and there is
  no readback, and B069's "DisplayScale keeps its restore entry only on a definite failure" now means only when the
  write did not dispatch.
- **Related:** U01-031, U01-063, U01-036; WDC-003, WDC-010, WDC-023.

### WDC-V-001: the layout editor never stores rotation, so the planned rotation fix would rotate displays

- **Severity:** medium (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Settings/DisplayLayoutEditor.cs:274-281` (`ToOutput`) and `Load` (~:255-268);
  `external/windows-device-control/src/WindowsDeviceControl/DisplayLayoutPlanner.cs:166`; `DisplayLayouts.cs`
  (`Matches` ~:378-399, `Confirm` ~:410); `src/WSGM/Core/ConfigRepair.cs` (B068 migration).
- **Problem:** `ToOutput()` builds `DisplayLayoutOutput` without `Rotation`, so every editor-saved `GameLayout` and
  `DesktopLayout` carries the default 1, even for a portrait or panel-rotated display. The planner always writes the
  rotation (`output.Rotation == 0 ? 1 : output.Rotation`). Today the damage is limited because `Matches` ignores
  rotation, so a matching desktop is reported `AlreadyActive` and left alone; any real apply already resets a rotated
  display to landscape. U01-027 adds rotation to `Matches`, after which no rotated display is `AlreadyActive` any more
  and every entry and return writes rotation 1: a visible behaviour change. (`Confirm`, the post-apply readback, is
  deleted under D9 in WDC-003, so only `Matches` is left to fix.)
- **Best solution:** Make `Rotation == 0` mean "keep the display's current rotation", since rotation has no editor
  control:
  - Planner: when `output.Rotation == 0`, write the rotation of the target's currently active path (same adapter LUID
    and target id) taken from the queried `paths`, read before the planner sets the active flag; if the target has no
    active path, write 1 (today's value). This is input to the write, not a readback.
  - `Matches` (now used only by the `AlreadyActive` pre-check in `Run`): compare rotation only when `output.Rotation != 0`:
    `(output.Rotation == 0 || current.Rotation == output.Rotation)`, using the `Rotation` that `ReadOutput` already
    fills.
  - `DisplayLayoutEditor.ToOutput` passes `Rotation: 0`. The record default stays 1 (WDC-001).
  - B068 migration rewrites `Rotation 1 -> 0` in `GameLayout` and `DesktopLayout` only. They are always editor-authored
    (`SettingsViewModel.Launch` saves `Build()`, and "copy current desktop" goes through `CopyFrom`, `Load` and
    `ToOutput`, which drops the captured rotation), so no chosen value is lost. `PendingReturnLayout` comes from `Capture()` and keeps its exact rotation and the full
    U01-027 behaviour.
  The alternative (adding a rotation control or copying rotation in `Load`) changes the editor UI or still breaks
  layouts saved before the fix.
- **Tests:** WDC `DisplayLayoutTests`: `Matches` ignores rotation for 0 and compares it otherwise; planner keeps the
  active path's rotation for 0 and writes 1 for an inactive target; a rotation-only difference with a non-zero
  rotation writes. WSGM: editor `ToOutput` emits 0; B068 migration test (1 -> 0 in the two editor layouts,
  `PendingReturnLayout` unchanged). WDC filter `FullyQualifiedName~DisplayLayoutTests|FullyQualifiedName~DisplayApplyTests`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DisplayLayout|FullyQualifiedName~Migration"`.
  Attended: matrix row M01-19 (a rotated display stays rotated after entry and return).
- **Plan v2:** B067 (semantics and editor), B068 (stored-layout migration). D9 overrides B067's "rotation compared in
  Matches and Confirm": `Confirm` no longer exists, so the rotation rule lives in `Matches` only.
- **Related:** U01-027; WDC-001; WDC-003; CONFIG-017 (same migration batch).

### WDC-V-002: WSGM classifies Wi-Fi scan failures by substring-matching WDC exception text, and the match is wrong

- **Severity:** medium (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/RadioManager.cs` (`ListWifiNetworks` catch ~:600-613 storing `failure = ex.Message`,
  `Apply` ~:831, `DescribeScanFailure` ~:880-886);
  `external/windows-device-control/src/WindowsDeviceControl/Interop.cs:47-50` (`WlanFailure` message);
  `tests/WSGM.Tests/Shell/RadioManagerTests.cs:54-63`.
- **Problem:** `DescribeScanFailure` tests `message.Contains("Win32 5")`. Any status whose decimal starts with 5
  (`ERROR_NOT_SUPPORTED` 50, `ERROR_INVALID_STATE` 5023, any 5xx) shows the location-consent message. The contract is
  also invisible: B064 changes how scan status is reported and would silently drop the consent guidance.
- **Best solution:** In B064, `ListWifiNetworks` keeps throwing `Win32Exception` for list and scan failures, with
  `NativeErrorCode` set to the WLAN status and the message format unchanged (it already does through `CheckWlan`;
  keep it). Separately, `ReadScanFacts` on the connect and forget paths stops ignoring the
  `WlanGetAvailableNetworkList` status, so a failed list is no longer read as an empty, "unsupported" network
  (U01-014). `RadioManager`'s snapshot stores `int FailureStatus` beside the message
  (`ex.NativeErrorCode` for `Win32Exception`, 0 otherwise). `DescribeScanFailure(int status, string message)` returns
  the existing consent literal when `status == 5` (ERROR_ACCESS_DENIED, the 24H2 location gate) and
  `$"Wi-Fi scan failed: {message}"` otherwise, exactly as today.
- **Tests:** replace `TheLocationConsentGateIsNamedRatherThanShownAsARawError` with a theory: status 5 gives the
  consent text; 50, 5023 and 1168 give the generic text with the message unchanged.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager"`.
- **Plan v2:** B064 (decided: D4 approved, so the batch is no longer gated).
- **Related:** WDC-V-003; U01-014, U01-074.

### WDC-V-003: Wi-Fi, scan and pairing library text is UI text, and the radio batches had no parity table

- **Severity:** medium (missed by the review, found by the verifier)
- **Where:** `src/WSGM/Shell/RadioManager.cs` (connect catch ~:1058-1063 via `DescribeConnectFailure` fallback,
  `OnPairingDone` ~:1380-1420 using `failure?.Message`); library sources of the text:
  `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs` (`ConnectWifi` ~:116-319, profile
  path ~:360-366), `WindowsRadio.Bluetooth.cs:532` ("Bluetooth pairing timed out.").
- **Problem:** WSGM shows `ex.Message` from `ConnectWifi` and from pairing verbatim. B064 (`WifiConnectResult` with
  `Pending`) and B065 (`PairBluetoothAsync`, unpair outcome) replace these exceptions with typed outcomes, but only the
  display batch planned exact-string tables. Without one, the overlay's Wi-Fi and Bluetooth messages would change.
- **Best solution:** Add `src/WSGM/Shell/RadioText.cs`, a static mapping owned by WSGM, filled with today's literals,
  and route every radio outcome through it:
  - B064: `WifiConnectResult` carries `Outcome { Joined, Failed, Pending, Refused }`, `uint ReasonCode` and, for
    `Refused`, a `WifiConnectRefusal { InvalidPassphrase, UnsupportedAuthentication, NeedsPassword, UnsupportedSecurity }`.
    `RadioText` maps them to the current strings: "This network does not advertise a supported personal-key
    authentication method.", "This network needs a password and has no saved profile.", "This network's authentication
    method is not supported.", and for `InvalidPassphrase` the exact text the UI shows today, which is the
    `ArgumentException.Message` including its parameter suffix: "The password must be 8-63 printable ASCII
    characters, or 64 hex digits. (Parameter 'passphrase')" (reachable: `RadioPanel.OnPromptAccept` passes the typed
    password unchecked). The refusal for an unreadable existing profile stays an `InvalidOperationException` with its
    current message. `FindFreeProfileName`'s "No collision-free Wi-Fi profile name is available for this network."
    becomes unreachable once its 64-suffix cap goes (WDC-007, D2), so it has no entry. `Pending` maps to
    "The Wi-Fi connection attempt did not complete." `Failed` keeps the existing `DescribeConnectFailure` path. The
    "More than one network advertises this display name" refusal becomes unreachable once the key carries bytes plus
    security, so it has no entry. Genuine Win32 failures (WlanConnect, WlanSetProfile, rollback aggregates) stay
    exceptions whose message format does not change, so the existing catch shows the same text.
  - B065: `PairBluetoothAsync` faults with `TimeoutException` on its 90 s deadline and with
    `OperationCanceledException` on the caller token. `RadioText` maps `TimeoutException` to "Bluetooth pairing timed
    out." and every other exception to its message as today; the `Windows status {RawStatus}` text stays as it is.
- **Tests:** a WSGM table test per batch asserting each enum value (and the timeout exception type) maps to the
  pre-change literal, with the literals copied once from current source.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager|FullyQualifiedName~RadioText"`.
  Attended: M01-21 and M01-22 (Wi-Fi, scan, consent and pairing texts read as today).
- **Plan v2:** B064 (Wi-Fi part; decided: D4 approved), B065 (pairing part).
- **Related:** U01-036, U01-015, U01-019; WDC-023 (same rule for display); WDC-V-002.

---

## Low

### WDC-002: `ConfigStore.NormalizeLayout` deletes stored layouts that fail `DisplayLayouts.Describe`

- **Severity:** low (verifier lowered it from medium: latent, no current input triggers it)
- **Where:** `src/WSGM/Core/ConfigStore.cs:891-892,989-996` (`NormalizeLayout`);
  `external/windows-device-control/src/WindowsDeviceControl/DisplayLayoutPlanner.cs:20` (`Describe`).
- **Problem:** On every load `GameLayout` and `DesktopLayout` are set to null when `Describe` returns a reason. The
  editor enforces the same rules, so nothing triggers it today, but any later tightening of the library rules would
  silently erase user configuration. `PendingReturnLayout` is not normalized and is not affected.
- **Best solution:** `NormalizeLayout` keeps only its structural type check (a layout whose `Outputs` is null or holds a
  null output or null `Target` is malformed JSON and is dropped) and stops calling `DisplayLayouts.Describe`. A layout
  that fails `Describe` stays stored and is reported as a load diagnostic (B038 normalizers return diagnostics). At use
  time `DisplayLayouts.Run` already returns `Invalid` from `Describe` before touching Windows, and the editor shows the
  reason when the layout is opened. Removing the 16-display cap (WDC-007) loosens `Describe` further.
- **Tests:** a config test where a stored layout fails `Describe` (for example two outputs at the same position) and
  survives a load and save round trip with a diagnostic.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ConfigRepair"`.
- **Plan v2:** B038 (config domain).
- **Related:** WDC-007.

### WDC-003: display apply rolls back after a definite refusal without checking whether anything changed

- **Severity:** low (verifier corrected the recommendation)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:155-172` (`Apply` after
  `Change(..., 0)`); `DisplayLayouts.cs:318-339` (`Run` after `Supply(... SdcApply | SaveToDatabase)`), and the
  `DisplayLayoutOutcome.Unconfirmed` doc (~:106).
- **Problem:** When the apply call returns a non-zero status, both paths still write the original back and report
  "not confirmed". `Unconfirmed`'s doc says "Windows accepted it but the readback did not match", which is false for a
  refusal. The review's "a refusal changed nothing, so never roll back" is also not established:
  `DISP_CHANGE_FAILED` and generic SetDisplayConfig failures can follow a partial driver change. Under D9 the larger
  defect is the opposite case: both paths also roll back a write Windows accepted (status 0) whenever the readback
  differs, which gates success on readback.
- **Best solution:** D9 decides this: no readback machinery on any host path. The apply status alone decides, for both
  paths; nothing is read back after the write and no readback decides a rollback. A write either dispatched (status 0,
  published as written) or failed to dispatch (non-zero status, one restore of the captured original so the user is
  never left on a half-applied desktop, since a refusal can follow a partial driver change).
  - `DisplayModes.Apply`: extract an internal pure `Decide(int status, bool routeSame, int rollbackStatus)`. Status 0:
    `Applied`, no further read (the post-write `Find`, `SameRoute` and `ReadNative` comparison are deleted, which also
    removes the throw-after-write of U01-003). Non-zero status: re-find the route once; when it is still the same, write
    the original back once and report `Refused` with `RollbackAttempted = true` and `RollbackSucceeded =
    rollbackStatus == 0` (the restore's own status, not a readback); when the route changed, write nothing (the source
    name may now name another display) and report `Refused` with `RollbackAttempted = false`.
  - `DisplayLayouts.Run`: `Confirm` is deleted. Status 0: `Applied`, extras applied as today. Non-zero status: one
    rollback as today with the native snapshot, outcome `Refused` with the apply status and the rollback flags taken
    from the rollback's `Supply` status; the extras restored after a successful rollback come from the same snapshot
    (WDC-025). `DisplayLayoutResult.Applied` stays `Applied or AlreadyActive`, so Game Mode entry stays silent and
    return recovery clears its pending layout as soon as the layout dispatched; no recovery entry waits on a readback.
    The pre-apply `Matches` check that reports `AlreadyActive` stays: it decides whether to write at all, it is not a
    confirmation of a write.
  - Replace `DisplayLayoutOutcome.Unconfirmed` with `Refused` ("Windows refused the apply; the captured arrangement was
    restored once, never retried").
  No outcome retries the requested write. In B067 both paths keep today's user-visible strings: the mode path still
  returns `DisplayProfileResult`, a refusal with a successful restore shows "Mode was not confirmed; the original mode
  was restored." and one without a restore or with a failed restore shows "Mode was not confirmed; display recovery
  could not be verified."; the layout `Refused` details are today's "The layout was not confirmed; the previous
  arrangement was restored." and "The layout was not confirmed and the rollback failed with status {rollbackStatus}.".
  The applied texts reach only the log (nothing shows an applied result's detail), so the layout one becomes "Layout
  applied." and the mode one "Display mode applied.". B069 then moves both into typed results (WDC-010, WDC-023).
- **Tests:** WDC `DisplayModeDecisionTests` over the pure `Decide` table; `DisplayApplyTests` through the internal
  `IDisplayConfigApi { Query, Supply, ReadTarget }` port with fake extras: status 0 gives `Applied` with no Query,
  Observe or rollback Supply after the apply; a non-zero status gives `Refused`, exactly one rollback Supply of the
  snapshot and the extras of that snapshot; a failed rollback reports its status. WSGM: `GameModeEntryTransaction` and
  `GameModeReturnRecovery` tests treat a status-0 apply as applied without any readback. GDI mode calls stay outside the
  port (plan v2). WDC filter `FullyQualifiedName~DisplayApplyTests|FullyQualifiedName~DisplayModeDecisionTests`.
- **Plan v2:** B067 (behaviour), B069 (typed result). D9 overrides B067's "one readback decides whether the original
  is still in place, and rollback happens once only on a confirmed mismatch on a still-verified route": the status
  decides and a refusal restores once without a readback.
- **Related:** U01-003, U01-030; WDC-004, WDC-010, WDC-025.

### WDC-005: four Bluetooth container normalizers, and the library ones merge empty containers

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:122-142`
  (`BluetoothIdentity`), `:574-594` (`ReadBluetoothDevice`), `:46-67` (`ListBluetoothDevices` grouping);
  `CoreAudio.Bluetooth.cs:69,147`; `src/WSGM/Shell/BluetoothDeviceCatalog.cs:80-111` (`Snapshot`, `Container`).
- **Problem:** Each site normalizes the container id differently. The library forms keep
  `00000000-0000-0000-0000-000000000000`, so `ListBluetoothDevices` merges every endpoint with an empty container into
  one row and `ConnectedBluetoothCount` counts them once. WSGM's explicit `Guid.Empty` exclusion shows the case occurs.
- **Best solution:** One `internal static string NormalizeContainer(object? value)` in `WindowsRadio.Bluetooth.cs`:
  a `Guid`, or a string that `Guid.TryParse` accepts (with or without braces), becomes the lower-case "D" form unless
  it is `Guid.Empty`; everything else becomes `""`. Use it in `BluetoothIdentity` (empty falls back to
  `endpoint:{id}`), `ReadBluetoothDevice`, and both `CoreAudio.Bluetooth.cs` sites. `BluetoothDevice.Container` is then
  normalized at the source. In WSGM delete `BluetoothDeviceCatalog.Container` and use `device.Container` directly;
  the catalog and its product policy ("Unnamed device", paired endpoints as disconnected rows, pairable-endpoint
  choice) stay in WSGM.
- **Tests:** WDC `WindowsRadioTests`: Guid, braced string, upper case, `Guid.Empty`, garbage, null. WSGM
  `BluetoothDeviceCatalogTests` unchanged in expectations. WDC filter `FullyQualifiedName~WindowsRadioTests`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BluetoothDeviceCatalog"`.
- **Plan v2:** B065.
- **Related:** WINSVC-029, critic conflict 12 (catalog stays in WSGM).

### WDC-006: wake-security `Restore` invents originals when nothing was captured

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsWakeSecurity.cs:65-127`
  (`DisableSignIn`, `Restore`); `src/WSGM/Core/LockScreenSettings.cs:81-104` (`CaptureInto`, `RecoverySnapshot`).
- **Problem:** `Restore(null)`, and a snapshot without schemes, write 1/1 to every scheme and delete policy values. That
  is product policy (the secure default) inside the library. WSGM relies on it: when nothing was captured,
  `RecoverySnapshot` builds `PolicyExisted = true`, -1 values and no schemes, and `Restore` then even creates the
  policy key just to delete values from it.
- **Best solution:** Replace `DisableSignIn` with three primitives and make `Restore` touch only captured entries:
  - `SetConsoleLockPolicy(int ac, int dc)`, `SetSchemeConsoleLock(Guid scheme, int ac, int dc)`,
    `SetNoLockScreen(int value)`, plus public `EnumerateSchemes()`. -1 deletes the value (the snapshot's existing
    convention). A primitive creates a key only when it writes a value; a deletion opens the existing key and does
    nothing when the key is absent.
  - `Restore(WakeSecuritySnapshot snapshot)` takes a non-null snapshot, builds a pure `WakeSecurityRestorePlan` from
    it, executes every item, collects per-item failures in a returned `WakeSecurityRestoreResult` and treats a vanished
    scheme as no longer applicable.
  - WSGM `LockScreenSettings.ApplyDirect` composes disable in today's order (policy 0/0, every scheme 0/0, refresh,
    NoLockScreen 1). When no snapshot was captured, WSGM composes the secure default with the same writes as today
    (delete policy values, every scheme 1/1, refresh, delete NoLockScreen), now without creating an absent policy key.
    UI and outcome are unchanged.
- **Tests:** WDC `WakeSecurityRestoreTests` on the pure plan and on an executor with fake steps (every item attempted,
  failures collected, vanished scheme skipped); WSGM `WakeSecurityRecoveryTests` for the no-snapshot default. WDC
  filter `FullyQualifiedName~WakeSecurity`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WakeSecurityRecovery"`.
- **Plan v2:** B071.
- **Related:** U01-005, U01-037, U01-075; WDC-024; WINSVC-034 (refuted: the secure default stays).

### WDC-007: arbitrary count and length limits in the library

- **Severity:** low (verifier completed the inventory)
- **Where (remove):** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs:72-79,121`
  (`ValidateBufferCounts`, 4096 paths / 8192 modes); `DisplayLayoutPlanner.cs` (16-display cap in `Describe`, ~:27-30);
  `DisplayModes.cs:75,138,204` (enumeration stops at index 4096); `WindowsRadio.WlanNative.cs:29-38` (truncation when
  `rejection` is null, used for scan facts and the profile list) and `:180-182` (interface list throws above 64);
  `WindowsPower.HybridCores.cs:242` (64 possible values) and `:85,271` (`MaximumCpuSetBytes`);
  `WindowsPower.cs:13,61` (`MaximumNameBytes` 64 KiB scheme name); `PowerRequestList.cs:45-52,291` (`MaxBuffer`
  1 MiB, `MaxRequests` 100000, `MaxStringUnits` 4096); `ModernStandby.cs:92,102,212,418,423` (4096-entry and
  4096-byte name caps); `WindowsRadio.WifiRules.cs` (`FindFreeProfileName`, 64 suffixes). README L147-150 documents
  the topology cap.
- **Problem:** These caps refuse or truncate valid data. A long process path makes the whole power-request list
  unknown, a large mode list is silently cut, and with WDC-002 a 17-display layout would be deleted.
- **Best solution:** Allocation follows the size Windows reports, with `checked` arithmetic and `Array.MaxLength` as
  the only natural ceiling; loops end on the native end-of-list result (`EnumDisplaySettingsEx` returning false,
  `dwNumberOfItems`, ERROR_NO_MORE_ITEMS); buffer decoders keep structural bounds checks (an offset or length outside
  the buffer is a typed `Malformed`/unreadable result, never truncation). `ReadWlanList` loses its `maximum` and
  `rejection` parameters and reads `dwNumberOfItems` records for all three callers (available networks and the profile
  list at 4096, interfaces at 64). Delete `ValidateBufferCounts`, its README
  text and its test (`DisplayTopologyTests.cs:15-27`) in B067 together. D2 keeps only the listed byte bounds, so
  `FindFreeProfileName` (`WindowsRadio.WifiRules.cs`) loses its 64-suffix cap too: it loops
  `suffix <= profiles.Count + 1`, which always reaches a free name because each saved profile can occupy at most one
  candidate, and its "No collision-free Wi-Fi profile name" throw goes. Keep, because they are not count or length
  caps on content: `ReadSchemeName`'s three attempts (a retry count for a size race), DPI 100-500 and the 320x200 to
  32768 mode bounds (Windows domain ranges), and the EDID 32 KiB maximum (`DisplayEdid.cs:46`, listed in D2).
- **Tests:** planner accepts 17 outputs; `PowerRequestList` captured-buffer fixtures with a long path decode fully;
  `FindFreeProfileName` with 64 colliding profiles returns suffix 65; remove the cap tests. WDC filter
  `FullyQualifiedName~DisplayLayoutTests|FullyQualifiedName~DisplayTopologyTests|FullyQualifiedName~PowerRequestListTests|FullyQualifiedName~ModernStandbyTests|FullyQualifiedName~WifiRules`.
- **Plan v2:** B067 (display part), B064 (WLAN part, "no truncation" in `WlanNative`, plus the profile name search),
  B071 (power, request list, standby). Decided: D2 accepted exactly the plan v2 byte-bound list; every other cap goes.
- **Related:** U01-064, U01-076; WDC-002.

### WDC-008: `RespondToPairing` forwards a PIN to every ceremony

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:460-492`.
- **Problem:** `Accept(pin)` is called whenever a non-empty PIN is supplied, although the doc says the PIN is ignored
  except for `ProvidePin`. An empty PIN for a `ProvidePin` request calls `Accept()` without one.
- **Best solution:** Look the request up with `TryGetValue` first. When `accept` is true and the pending request's
  `PairingKind` is `ProvidePin`, `ArgumentException.ThrowIfNullOrEmpty(pin)` before the token is consumed, so the
  caller can still answer it; then `Accept(pin)`. For every other kind call `Accept()` and ignore `pin`. Remove the
  entry and complete the deferral in `finally` as today.
- **Tests:** pure tests over the pending table: the PIN is passed only for `ProvidePin`; an empty PIN for `ProvidePin`
  throws and leaves the token answerable. WDC filter `FullyQualifiedName~PairingTests|FullyQualifiedName~WindowsRadioTests`.
- **Plan v2:** B065.
- **Related:** U01-051; WINSVC-V-005 (WSGM stops logging the PIN, same batch).

### WDC-013: the sealed W02_02 brief over-specifies the power-action seam

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsPower.Actions.cs:30-57`;
  `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsPowerTests.cs:55-62`
  (`CancelledActionsNeverDispatch`).
- **Problem:** The test calls the public suspend and restart paths, so the canonical gate can suspend or restart the
  machine. The W02_02 brief fixes this with two interfaces, a nested native process wrapper and a blocking-fake test
  that only exercises `Task.Run`.
- **Best solution:** One internal port:
  `internal interface IPowerActionApi { bool Suspend(bool hibernate); Task<int> RunToolAsync(ProcessStartInfo start, CancellationToken cancellationToken); }`
  with a private static `NativePowerActionApi` (calls `SetSuspendState`, throws `Win32Exception(GetLastPInvokeError())`
  on false; starts the process, throws today's `InvalidOperationException("Windows power tool did not start.")` when
  `Process.Start` returns null, awaits exit, returns `ExitCode`, disposes the process). Internal overloads
  `SuspendAsync(bool, IPowerActionApi, CancellationToken)` (cancellation re-checked inside the delegate right before
  `Suspend`) and `RequestActionAsync(WindowsPowerAction, IPowerActionApi, CancellationToken)` (validate the action,
  check the token, run, throw the existing `Win32Exception` on a non-zero exit). Public overloads delegate with the
  native instance. About 120 changed lines.
- **Tests:** replace `CancelledActionsNeverDispatch` with four fake-backed tests: a cancelled token makes zero port
  calls on both paths; action arguments are forwarded unchanged; a non-zero exit throws with the code; a native
  `Suspend` failure propagates once. WDC filter `FullyQualifiedName~WindowsPowerTests` (net8 only at this point).
- **Plan v2:** B002.
- **Related:** U01-009, A01-F002, BUILD-001 (power half).

### WDC-014: "callback-initiated disposal is safe" is unattainable for WLAN without new mechanism

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs` (static `_wifiWatch`,
  `WifiWatchLock`, `DetachWatch`, `StopWifiWatch`, registration ~:231-257); `WindowsRadio.Bluetooth.cs` (static
  `_bluetoothWatch`, `BluetoothWatchLock`, `StopBluetoothWatch(Core)`); `CoreAudio.Watch.cs`;
  `src/WSGM/Shell/RadioManager.cs` (`_feedWork`, `QueueFeedWork`, `_bluetoothWatchGeneration`, ~:485-491,680-712).
- **Problem:** The Codex plan asked for callback delivery outside the state lock and safe disposal from inside a
  callback. `WlanRegisterNotification` must not be called from its own callback, so "safe" would need thread-identity
  detection and a deferred unregister. Meanwhile the real defect stands: one process-global feed slot per watch, so a
  second `Start` replaces the first and can unroot a live native delegate.
- **Best solution:** Per-registration ownership with no hub. `StartWifiWatch` and `StartBluetoothWatch` return an
  `IDisposable` registration (`WifiWatchRegistration`: own WLAN client handle, ACM-only registration, rooted delegate;
  `BluetoothWatchRegistration`: own `DeviceWatcher`, handlers, records). `Dispose` is idempotent (interlocked flag),
  unregisters outside any lock, then closes the handle, so the delegate stays rooted until unregister completes. Each
  registration delivers under its own gate. The watcher's `Stopped` handler (an aborted watcher) raises one terminal
  `BluetoothChangeKind.Stopped`. Document on both methods: callbacks arrive on a native thread, and a Wi-Fi
  registration must not be disposed from inside its own callback; post to your own thread first. No thread detection.
  Delete the static slots, `StopWifiWatch` and `StopBluetoothWatch`. `RadioManager` holds the registrations, disposing
  one is the stop (it already posts callbacks to the dispatcher), and `_feedWork`, `QueueFeedWork` and
  `_bluetoothWatchGeneration` go. CoreAudio watch ownership transfer and `EndpointWatch.Dispose` become interlocked in
  the same batch. Two verifier corrections apply: the WLAN notification seam the tests need is created here, not in
  B064, and the batch must widen the visibility of the private COM interfaces it fakes (today the only internal
  interface is `IPowerRequestApi`). Version becomes 0.2.0 in this batch.
- **Tests:** WDC `WatchRegistrationTests` through the internal WLAN notification seam: two registrations coexist,
  disposing one leaves the other delivering, double dispose is harmless, unregister happens before handle close; CoreAudio
  ownership tests through the widened interfaces. WDC filter
  `FullyQualifiedName~WatchRegistrationTests|FullyQualifiedName~CoreAudioTests`; WSGM
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~BluetoothAction|FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~NativeQamBluetooth"`.
- **Plan v2:** B063.
- **Related:** U01-001, U01-007, U01-018, U01-021, U01-022, U01-024 (watcher abort), U01-046, U01-055, U01-056, U01-060,
  U01-084; A01-F003, A01-F005; WINSVC-022; critic conflict 9.

### WDC-015: `DisplayEdid.ReadModes` timeout is indistinguishable from cancellation, and a missing monitor throws

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayEdid.cs:31-42`; consumer
  `src/WSGM/Settings/SettingsViewModel.cs:263` (`ReadWindowsDisplayFacts`).
- **Problem:** The internal three-second `CancellationTokenSource` surfaces as `OperationCanceledException`, and
  `DisplayMonitor.FromInterfaceIdAsync` returning null makes `monitor.GetDescriptor` throw `NullReferenceException`.
  Settings cannot tell a slow display from a missing one, and one failure loses the display's other facts.
- **Best solution:** `public static DisplayEdidModes ReadModes(DisplayTargetIdentity target)` returning
  `public sealed record DisplayEdidModes(IReadOnlyList<DisplayMode> Modes, DisplayEdidStatus Status)` with
  `DisplayEdidStatus { Read, NotFound, TimedOut, NoDescriptor }`. No device path or a null monitor is `NotFound`; the
  internal timeout is caught and reported as `TimedOut`; a null or empty descriptor is `NoDescriptor`; an invalid
  descriptor parses to an empty list with `Read` (as documented today). Keep the three-second bound (a native wait,
  not a count cap) and add no caller token (no consumer needs one). Settings uses `.Modes`; the visible mode list is the
  same (`MergeCatalog` already ignores an empty mode list, so remembered modes survive), and the HDR and scaling facts
  of that display are no longer lost. `SettingsServices.ReadWindowsDisplayFacts` logs a non-`Read` status
  (`Log.Warn`, display name plus status) so wsgm.log still records why a display had no EDID modes.
- **Tests:** status classification as an internal pure function over (monitor found, timed out, descriptor bytes).
  WDC filter `FullyQualifiedName~DisplayEdid|FullyQualifiedName~DisplayTopologyTests`.
- **Plan v2:** B067.
- **Related:** U01-034.

### WDC-018: WDC tests that copy literals, predicates or limits instead of testing behaviour

- **Severity:** low (verifier corrected the anchors and refuted one sub-claim)
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/ModernStandbyTests.cs:124` (re-states
  the source GUID literals); `HybridCoreTests.cs:88` (`Classes.Count > 1`); `PowerRequestListTests.cs:80` (copies the
  `ModeCount` switch); `DisplayTopologyTests.cs:15-27` (tests the arbitrary cap); `WindowsRadioTests.cs:134-139`
  (`RespondToPairing(uint.MaxValue, ...)` with no assertion; it does not mutate the pairing table, since removing an
  absent key changes nothing); `CoreAudioTests.cs:162` (a single WaveOut bit).
- **Problem:** These tests pass by construction or pin arbitrary values, so they guard nothing and some block fixes
  (the cap test blocks WDC-007).
- **Best solution:** In B046 delete the GUID literal test, the `Count > 1` predicate test and the WaveOut bit test, and
  give the `RespondToPairing` test a real assertion (an unknown token returns without throwing and leaves no pending
  entry). Replace the `ModeCount` copy with captured-buffer fixtures in B071 (U01-080) and delete the cap test in B067
  together with `ValidateBufferCounts`. B066 adds the WaveOut dispose decision-table test that covers the queued bit
  (the bit test itself is already gone in B046).
- **Tests:** the full WDC suite on net8 and net10 after B046.
- **Plan v2:** B046 (with the B066, B067 and B071 follow-ups named above).
- **Related:** U01-044, U01-080; WDC-007, WDC-026.

### WDC-021: `DisplayModes.Read` probes every mode while holding the display gate

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:45-113` (`Gate`, `Read`), plus
  `ReadPrimaryMode`, `EnumeratePrimaryModes`, `TestPrimaryMode`, `ApplyPrimaryModeTransient`; `DisplayLayouts.cs`,
  `DisplayScaling.cs`, `DisplayColor.cs` (no gate today); caller `src/WSGM/Settings/SettingsViewModel.cs:263`.
- **Problem:** `Read` issues one `CDS_TEST` per distinct mode (possibly hundreds of driver calls) under `Gate`. Today
  only `DisplayModes` has a gate, so layout, scaling and HDR writes are not serialized against each other or against the
  NVIDIA plugin, which shares the host-first WDC assembly. Once one shared write gate exists (U01-035), keeping `Read`
  under it would block those writes for the whole probe.
- **Best solution:** Replace `DisplayModes.Gate` with one `internal static readonly object WriteGate` in
  `DisplayTopology`, taken only by writes: `DisplayModes.Apply`, `DisplayModes.ApplyPrimaryModeTransient`,
  `DisplayLayouts.Run` when applying, `DisplayScaling` set and `DisplayColor` HDR set (`Monitor` is reentrant, so the
  extras called from `Run` nest safely). Reads take no gate: `Read`, `ReadPrimaryMode`, `EnumeratePrimaryModes`,
  `TestPrimaryMode` (a test changes nothing), `Observe`, `Capture`, `TryRead*`. `Read`'s existing route recheck already
  detects interleaving. A static gate, not an instance per host, because CCD state is machine-global and the plugin
  shares the assembly.
- **Tests:** none beyond the existing display filters (locking is not unit-tested); WDC filter
  `FullyQualifiedName~DisplayLayoutTests|FullyQualifiedName~DisplayApplyTests`. Covered by attended M01-19.
- **Plan v2:** B067.
- **Related:** U01-035.

### WDC-023: library English text is visible UI text

- **Severity:** low
- **Where:** `src/WSGM/Overlay/DisplayModeView.cs:209` (shows `DisplayModes.Apply().Detail`);
  `src/WSGM/Settings/DisplayLayoutEditor.cs:763` (shows `DisplayLayouts.Describe`);
  `src/WSGM/Shell/GameModeEntryTransaction.cs:236-238` (shows layout `Detail` to the user: `layoutWarning` becomes
  `GameModeEntryResult.Warning`, which `SessionModes` raises through `SteamStartFailed`);
  `src/WSGM/Shell/GameModeEntryServices.cs:140` (WSGM-authored layout detail "This session cannot change displays.");
  `src/WSGM/Shell/DisplayLayoutDiagnostics.cs:33` (logs layout `Detail` and `Warnings`); library sources
  `DisplayLayoutPlanner.Describe`/`Plan`, `DisplayModes.Apply`, `DisplayScaling.TrySet`, `DisplayColor.TrySetHdr`,
  `DisplayLayouts.Run`/`ApplyPerTarget`/`ApplyOutputExtras` under `external/windows-device-control/src/WindowsDeviceControl/`.
- **Problem:** Moving wording out of WDC (U01-036) is not neutral: the mode-apply details, the layout validation
  reasons and every non-applied layout detail are shown verbatim in the overlay, Settings and the Game Mode entry
  warning. Any wording drift is a visible UI change. The review assumed layout details only reach the log; they do not.
- **Best solution:** In B069 WDC returns codes, and WSGM owns every literal:
  - `DisplayLayouts.Describe` returns `DisplayLayoutProblem?` (one value per current reason; the 16-display reason is
    gone with WDC-007). `DisplayModes.Apply` returns `DisplayModeResult` (WDC-010); set calls return the WDC-004
    results.
  - `DisplayLayoutResult` drops `Detail` and `IReadOnlyList<string> Warnings` and gains `DisplayLayoutProblem? Problem`
    (the `Describe` reasons plus `NoDisplayPath`, `NoFreeSource`, `ReadFailed`, `ValidationRejected`,
    `RollbackCaptureFailed`), `DisplayTargetIdentity? ProblemTarget` (for the two planner problems, which stop being
    `InvalidOperationException`s), `string? FailureMessage` (the caught `Win32Exception.Message` for `ReadFailed`;
    exception messages stay library text, the same rule WDC-V-003 applies to genuine Win32 failures), `int
    RollbackStatus`, and `IReadOnlyList<DisplayOutputWarning> Warnings` with
    `DisplayOutputWarning(DisplayTargetIdentity Target, DisplayOutputWarningKind Kind, DisplaySetOutcome Outcome, int NativeStatus)`.
  - New `src/WSGM/Core/DisplayText.cs` reproduces every current literal byte for byte: the `Describe` reasons; "No
    display path reaches {name} on this adapter." and "No free display source is available for {name}." with the
    planner's name rule (friendly name, else device path, else `target {TargetId}`); "The current display
    configuration could not be read: " + `FailureMessage`; "Waiting for " + the absent names joined with ", " + "."
    with `DisplayLayouts`' name rule (friendly name, else device path, else `{EdidManufacturerId}-{EdidProductCodeId}-{FriendlyName}`);
    "The desktop already matches this layout."; "Windows rejected this layout during validation (status {status}).";
    "The layout is valid for this hardware."; "The current arrangement could not be captured for rollback; nothing was
    applied."; WDC-003's log-only "Layout applied."; the two `Refused` texts (today's `Unconfirmed` wording, the failed
    one uses `RollbackStatus`). There is no confirmation or "not confirmed by readback" text any more (D9). The extras
    warnings reach only the log (entry shows `Detail` only when the layout is not applied), so they keep today's
    "{name}: {reason}" shape and may name the native status where today's text names the percent; a warning is raised
    only for a set that did not dispatch (WDC-004). A `Rejected` result with no `Problem` maps to its `FailureMessage` verbatim, which is how
    `GameModeEntryServices`' fallback keeps "This session cannot change displays." `GameModeEntryTransaction` and
    `DisplayLayoutDiagnostics` call `DisplayText` where they read `Detail` today.
- **Tests:** a WSGM table test asserting every enum value and problem maps to the pre-change literal (copied once from
  current source), including both name rules; UI baselines unchanged.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Display"`;
  `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DisplayPageViewsTests|FullyQualifiedName~GameModeDisplayPageTests"`.
- **Plan v2:** B069.
- **Related:** U01-036, U01-063; WDC-004, WDC-010; WDC-V-003 (radio text), WDC-V-005 (audio name).

### WDC-025: layout rollback state comes from two queries taken at different instants

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayLayouts.cs:305-316` (`Run`:
  `rollback = DisplayTopology.Query(OnlyActivePaths)` then `rollbackLayout = Capture()`).
- **Problem:** The native rollback and the scaling/colour extras restored after it come from two separate CCD
  observations. A hotplug between them makes the extras restore disagree with the native rollback.
- **Best solution:** Keep the single `Query(OnlyActivePaths)` snapshot for the native rollback (the exact shape
  SetDisplayConfig accepts today) and derive the extras from that same snapshot: for each of its paths,
  `DisplayTopology.ReadTarget` plus `ReadOutput(path, rollback.Modes, identity)`, skipping a path whose target read
  throws `Win32Exception` or whose output reads null (what `Capture()` does through `Observe` today). Those outputs form
  the rollback `DisplayLayout` whose extras are restored after a successful rollback; with D9 nothing compares against
  it (WDC-003 no longer reads back), so this only keeps the native rollback and the extras restore consistent. Delete
  the `Capture()` call there.
  This beats the review's "one QDC_ALL_PATHS query, active subset" because it does not change what is supplied to
  SetDisplayConfig.
- **Tests:** `DisplayApplyTests` with the fake port: one Query call before the apply, and the rollback extras follow the
  rollback snapshot's outputs. WDC filter `FullyQualifiedName~DisplayApplyTests`.
- **Plan v2:** B067.
- **Related:** U01-065; WDC-003.

### WDC-V-004: `DisplayTargetIdentity.Matches` is not reflexive for identities with neither path nor EDID ids

- **Severity:** low (missed by the review, found by the verifier)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.Types.cs:26-38` (`Matches`);
  `DisplayLayouts.cs:183-189` (`Observe` folding); `src/WSGM/Settings/SettingsViewModel.Launch.cs:300-316`
  (`KnownDisplays`); `src/WSGM/Settings/DisplayLayoutEditor.cs` (row creation); consumers
  `src/WSGM/Shell/GameModeEntryTransaction.cs:288-302`, `src/WSGM/Shell/DisplayArrivalWaiter.cs`.
- **Problem:** With an empty path and null EDID ids, `x.Matches(x)` is false. `Observe` cannot fold the several
  `QDC_ALL_PATHS` routes of such a target into one row, and Settings and the editor can save a display that
  `RequiredDisplays`, the arrival waiter and `Run`'s absent check can never find, so Game Mode entry waits until the
  user cancels. W02_01 makes this rarer but does not remove it.
- **Best solution:** No new matching heuristic. In `Observe`, fold two observations when `Matches` is true or when both
  identities are unmatchable (`!identity.Matches(identity)`) and share adapter LUID and target id (the route).
  In WSGM, skip unmatchable observations (`!observed.Target.Matches(observed.Target)`, a type check that needs no new
  API) when adding to `KnownDisplays` and when building editor rows, so such an identity is never persisted.
- **Tests:** WDC `DisplayLayoutTests`: two routes of an unmatchable target fold into one observation. WSGM: a Settings
  test where an unmatchable observation is not added to `KnownDisplays`. WDC filter `FullyQualifiedName~DisplayLayoutTests`;
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SettingsViewModel|FullyQualifiedName~DisplayLayout"`.
- **Plan v2:** B067.
- **Related:** U01-004 (EDID decoding), WDC-001.

### WDC-V-005: `AudioEndpoint.Name` is persisted, so an empty library name would reach config

- **Severity:** low (missed by the review, found by the verifier)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.cs:256` (the "Audio device" default);
  `src/WSGM/Shell/AudioProfileService.cs:364` (log `preference.Name ?? id`) and `:388` (`Endpoint()`);
  `src/WSGM/Settings/AudioProfileEditor.cs:59` and `:338-339` (`Build`); `src/WSGM/Shell/AudioManager.cs:483-484`.
- **Problem:** B066 moves the "Audio device" fallback out of WDC (`AudioEndpoint.Name` becomes empty when Windows has
  no name). The name is stored in `AudioEndpointPreference.Name`, including `PendingReturnAudio` recovery, so captured
  profiles would store `""`, and the log at :364 would print `''` instead of falling back to the id.
- **Best solution:** One WSGM helper, for example `internal static string AudioEndpointText.Name(CoreAudio.AudioEndpoint endpoint) => endpoint.Name.Length > 0 ? endpoint.Name : "Audio device";`,
  used at every point that shows or stores an endpoint name: the `AudioManager` list, `AudioProfileEditor` rows and
  `Build`, and `AudioProfileService.Endpoint`. Stored names stay exactly what they are today. Fix the log to
  `string.IsNullOrEmpty(preference.Name) ? id : preference.Name`.
- **Tests:** WSGM audio profile test: an endpoint with an empty name is captured as "Audio device".
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioManager|FullyQualifiedName~AudioPlaybackChoices|FullyQualifiedName~AudioProfile"`.
- **Plan v2:** B066.
- **Related:** U01-036; WDC-023.

### WDC-C-001: the display-scale recovery snapshot is keyed by the volatile GDI source name

- **Severity:** low (found by the solution checker)
- **Where:** `src/WSGM/Core/DisplayScale.cs` (`ApplyGameMode` capture, `RestoreDpiSnapshot` match by
  `SourceName`, `ShouldLowerDisplay`); `src/WSGM/Core/AppConfig.cs:112-119` (`DisplayScaleEntry.DeviceName`, documented
  as `\\.\DISPLAY1`); WDC contract `ActiveDisplayPath.SourceName` ("display-only numbering is volatile",
  `DisplayTopology.Types.cs`).
- **Problem:** Default Game Mode entry saves each lowered display's scaling under its GDI source name and the return
  path restores by that name. WDC documents the name as volatile, and the class itself expects a dock or undock
  between capture and restore. When the numbering moves in between, the saved percentage is written to a different
  monitor and the original monitor stays at 100%, while the entry is consumed as restored.
- **Best solution:** Key the snapshot by the monitor identity WDC already provides, without a migration:
  - `DisplayScaleEntry` gains `public DisplayTargetIdentity? Target { get; set; }`; `DeviceName` stays for the log.
  - `ApplyGameMode` stores `Target = source.Target` when the identity can match itself (WDC-V-004), else null.
  - One internal pure `FindSource(IReadOnlyList<ActiveDisplayPath> sources, DisplayScaleEntry entry)` used by the
    restore loop and `ShouldLowerDisplay`: an entry with a `Target` matches the source whose `Target.Matches(entry.Target)`;
    an entry without one (an unmatchable display, or a snapshot a 2.0 session left behind) matches by `DeviceName` as
    today. Nothing else changes: unmatched entries are kept for a later restore as today.
  The new property is nullable and absent in 2.0 files, so no B068 migration step and no fixture change are needed.
- **Tests:** new `tests/WSGM.Tests/Core/DisplayScaleTests.cs` over `FindSource`: identity wins over a name that now
  belongs to another monitor; an entry without `Target` matches by name; an absent identity matches nothing.
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DisplayScale|FullyQualifiedName~Configuration"`.
- **Plan v2:** B069 (it already edits `DisplayScale`'s restore loop for WDC-004).
- **Related:** WDC-004, WDC-V-004; WINSVC-037 (same file, different defect).

---

## Nit

### WDC-009: `AudioFilePreview` drops native error detail and reads `_player` without a barrier

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/AudioFilePreview.cs:11-15,30-41,63-64`;
  consumer `src/WSGM/Shell/SoundPackService.cs:315`.
- **Problem:** `Failed` carries only `args.ErrorMessage`, losing `MediaPlayerError` and the `ExtendedErrorCode`
  HRESULT. `_player` is read on the media thread inside `MediaFailed` and cleared by `Stop` on the owner thread with no
  barrier.
- **Best solution:** `public event Action<AudioPreviewFailure>? Failed;` with
  `public readonly record struct AudioPreviewFailure(MediaPlayerError Error, int HResult, string Message)`
  (`HResult = args.ExtendedErrorCode?.HResult ?? 0`). Read `_player` with `Volatile.Read` in the handler and clear it
  with `Interlocked.Exchange` in `Stop`. `SoundPackService` uses `failure.Message` where it used the string, so its
  text is unchanged, and logs the error and HRESULT.
- **Tests:** none new (WinRT media is not faked); build plus
  `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SoundPack"`.
- **Plan v2:** B066.
- **Related:** U01-079.

### WDC-010: `DisplayProfileResult` keeps the name of a removed API

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.Types.cs:65-70`; `DisplayModes.cs`
  (`Apply`); `src/WSGM/Overlay/DisplayModeView.cs`, `src/WSGM/Core/DisplayProfiles.cs`.
- **Problem:** Commit 2f07485 removed the display profile API; the record now only serves `DisplayModes.Apply`.
- **Best solution:** Together with the typed outcome, replace it with
  `public sealed record DisplayModeResult(DisplayModeOutcome Outcome, int NativeStatus, bool RollbackAttempted, bool RollbackSucceeded)`
  with `public bool Applied => Outcome is DisplayModeOutcome.Applied;` (kept for `DisplayModeView` and
  `DisplayProfiles.TryRestoreRefreshRate`) and
  `DisplayModeOutcome { Applied, Refused, Stale, RouteChanged, NotAdvertised, ValidationRefused, Unreadable }`, one
  value per `Apply` exit after WDC-003 (D9 removes the readback-based `Confirmed`, `Unconfirmed` and `Unverified`
  exits) so `DisplayText` can reproduce each string: `Stale` "Display changed or the selected mode was not offered.
  Refresh and select again.", `Unreadable` "Current display mode is unavailable.", `ValidationRefused` "The display
  rejected mode validation.", `RouteChanged` "Display route changed before application.", `NotAdvertised` "The
  selected mode is no longer advertised.", `Applied` the log-only "Display mode applied.". `Refused` with
  `RollbackSucceeded` (the restore write returned 0) is "Mode was not confirmed; the original mode was restored.";
  `Refused` without a rollback or with a failed one is "Mode was not confirmed; display recovery could not be
  verified.". No `Detail` field.
- **Tests:** covered by the WDC-023 table test and the WDC-003 decision tests.
- **Plan v2:** B069.
- **Related:** U01-031; WDC-003, WDC-004, WDC-023.

### WDC-011: WSGM redeclares power GUIDs the library publishes

- **Severity:** nit
- **Where:** `src/WSGM/Core/PowerTimeouts.cs:37` (`SubSleep`); `src/WSGM/Interop/WindowsCpuBoostApi.cs:28`
  (`ProcessorSubgroup`); library `ModernStandby.SubgroupSleep` (`ModernStandby.cs:111`) and
  `WindowsPower.SubgroupProcessor` (`WindowsPower.HybridCores.cs:89`).
- **Problem:** Duplicate constants can drift.
- **Best solution:** Delete both WSGM fields and use `ModernStandby.SubgroupSleep` and `WindowsPower.SubgroupProcessor`
  (both `public static readonly Guid`, so the `in` P/Invoke argument in `WindowsCpuBoostApi` still compiles).
- **Tests:** build; `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerTimeout|FullyQualifiedName~CpuBoost"`.
- **Plan v2:** B071.
- **Related:** none.

### WDC-012: the uncommitted W02_01 edit left mixed line endings

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs`,
  `external/windows-device-control/tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs` (both `w/mixed` in
  `git ls-files --eol`).
- **Problem:** The new lines are LF inside CRLF files. `core.autocrlf=true` normalizes the index, but cleanup and
  format diffs show noise, and the edit is still uncommitted, so every later WDC batch would build on an unrecorded
  change.
- **Best solution:** Convert the new lines to CRLF so both files read `w/crlf`, build the child for both target
  frameworks, run the three W02_01 tests, commit the two files on child `main`, push, then record the gitlink in the
  parent with a pathspec commit. No other change.
- **Tests:** WDC filter
  `FullyQualifiedName~EdidIdsFollowOnlyTheValidityBit|FullyQualifiedName~ValidZeroEdidIdsArePreserved|FullyQualifiedName~ExistingPathIdentitySurvivesEdidPopulation`
  (net8 only).
- **Plan v2:** B001.
- **Related:** U01-004, A01-F001.

### WDC-016: unsynchronized lazy selector cache in `ConnectedBluetoothCount`

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:34,99-120`
  (`_connectedSelectors ??=`).
- **Problem:** A benign race on a static cache that only saves two selector string builds.
- **Best solution:** Delete `_connectedSelectors` and build the two selector strings inside `ConnectedBluetoothCount`
  on every call. Plan v2 words this as "synchronized"; having no shared state is the simplest synchronization and
  still keeps WinRT failures out of the type initializer. Do not add `Lazy<T>` or a lock.
- **Tests:** none (behaviour unchanged).
- **Plan v2:** B065.
- **Related:** none.

### WDC-017: a Wi-Fi connection verdict is accepted from a completion without a profile name

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:180-222`
  (`ConnectionVerdict` callback); `WindowsRadio.Wifi.cs:284-319`.
- **Problem:** When the notification payload is short or the profile name is empty, any ACM completion on the adapter
  decides the verdict, including an unrelated auto-connect. The success path then returns joined without checking the
  target network.
- **Best solution:** Plan v2 chose to require the name, which removes this wrong-success path: the callback returns
  early unless the profile name is non-empty and equals `_profile` ordinally
  (`if (profile.Length == 0 || !string.Equals(profile, _profile, StringComparison.Ordinal)) return;`). Windows fills the
  name for ACM completions, so normal behaviour is unchanged. When it does not, the wait ends at `ConnectTimeout` and
  the existing `IsConnectedTo` check reports `Joined`, otherwise B064's `Pending`.
- **Tests:** extract the payload check into an internal pure function (code, profile name, expected name) and test it:
  an empty name and a different name are ignored. WDC filter `FullyQualifiedName~WifiConnectTests|FullyQualifiedName~WindowsRadioTests`.
- **Plan v2:** B065 (it can land in B064 if that batch is already editing the verdict).
- **Related:** U01-015.

### WDC-019: library docs prescribe readback confirmation as caller policy

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsPower.cs:125`, `WindowsPower.Policy.cs:45`
  and ~:215, `WindowsPower.HybridCores.cs:140-141`, `external/windows-device-control/README.md` (~:159, 192, 222-224).
- **Problem:** "Read back to confirm" tells consumers to gate on readback, which contradicts the maintainer rule.
- **Best solution:** Neutral wording everywhere: "The request is issued once; a read reports what Windows stores."
  For the display layout and mode README text, describe what `Apply` does after WDC-003 (D9): it applies once,
  reports the write as applied when Windows accepted it, and restores the captured original once when Windows refused
  it; it never reads back to confirm. The same goes for the `DisplayScaling` and `DisplayColor` set docs (WDC-004).
- **Tests:** none (docs).
- **Plan v2:** B072.
- **Related:** U01-066, U01-070; B017 (winsvc removes the readback gates in WSGM).

### WDC-020: the child has no standalone solution

- **Severity:** nit
- **Where:** `external/windows-device-control/` root; `WSGM.slnx:4,40`.
- **Problem:** The child cannot be built and tested on its own, and the parent gate is the only aggregate that runs
  the WDC suite.
- **Best solution:** Add a minimal `WindowsDeviceControl.slnx` at the child root listing the library and test
  projects. No CI workflow (no unrequested infrastructure). B072 then runs the full child suite on both frameworks from
  that solution as the independent validation.
- **Tests:** `dotnet test external\windows-device-control\WindowsDeviceControl.slnx -f net8.0-windows10.0.19041.0` and
  the net10 equivalent.
- **Plan v2:** B072.
- **Related:** BUILD-012 (child build props in B046).

### WDC-022: `WifiProfile.TryReadSsid` is substring parsing

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WifiProfile.cs:156-185,223-234`.
- **Problem:** It works for Windows-authored profiles but is unproven against their real variants.
- **Best solution:** Keep the parser (simplest). Add fixtures exported from real Windows profiles to the new
  `WifiProfileTests`: hex-only SSID, escaped name, name plus hex, multiple SSID elements. Fix only what a fixture shows
  is wrong.
- **Tests:** WDC filter `FullyQualifiedName~WifiProfileTests`.
- **Plan v2:** B064 (decided: D4 approved, so the batch is no longer gated).
- **Related:** U01-017, U01-041, U01-053.

### WDC-024: `Restore` can throw on a policy key that gained subkeys

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsWakeSecurity.cs:90`
  (`DeleteSubKey(PolicyKey, false)`).
- **Problem:** When the snapshot says the policy key did not exist, `Restore` deletes the key, which throws
  `InvalidOperationException` if something else added a subkey since.
- **Best solution:** In the WDC-006 restore plan, a "policy did not exist" item deletes the `ACSettingIndex` and
  `DCSettingIndex` values and then deletes the key only when it has no values and no subkeys left.
- **Tests:** a plan test asserting the item sequence (delete values, then conditional key delete).
  WDC filter `FullyQualifiedName~WakeSecurity`.
- **Plan v2:** B071.
- **Related:** WDC-006.

### WDC-026: two safety tests stay off native code only because of argument-check order

- **Severity:** nit
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsRadioTests.cs:19-23` (`GetPower`
  would enumerate radios if validation moved); `DisplayTopologyTests.cs:109-117` (A01-F006).
- **Problem:** If validation moves after the native call, these tests silently reach real hardware.
- **Best solution:** Move the argument checks into small internal pure helpers (for example
  `WindowsRadio.ValidateKind(RadioKind)`) called first by the facade, and test the helpers directly; delete the
  facade-level tests. The A01-F006 test goes away with the topology waits in B070.
- **Tests:** WDC filter `FullyQualifiedName~WindowsRadioTests`.
- **Plan v2:** B046 (radio part), B070 (wait test).
- **Related:** A01-F006; WDC-018.

---

## Ledger rows confirmed for this area

The review re-checked all 85 U01 rows and the WDC A01 rows against current code; none is refuted as a defect. The
batch column follows plan v2 Appendix B. Rows marked no-change are in the list below this table.

| Ledger id | Defect (short) | Fix | Batch |
| --- | --- | --- | --- |
| U01-001 | one Wi-Fi feed slot per process; a concurrent Start unroots a live delegate | per-registration `IDisposable` (WDC-014) | B063 |
| U01-002 | same SSID with different saved profile names treated as ambiguous | merge by SSID bytes plus security; connected profile, else ordinal first; forget removes every match | B064 |
| U01-003 | `DisplayModes.Apply` can throw after the write (`Find`) | `Apply` never throws after the write | B067 |
| U01-004 | EDID validity bit | done in the working tree | B001 |
| U01-005 | wake restore stops at the first failure | per-item plan and executor (WDC-006) | B071 |
| U01-006 | radio enumeration inside `RadioCacheLock` | enumerate outside, publish under the lock; no new timeout | B065 |
| U01-007 | stateful static feed slots | removed with registrations; caches stay static | B063 |
| U01-008 | only one native seam | internal ports for WLAN, CCD and power actions, pure restore planners | B046, B064, B067, B071 |
| U01-009 | the WDC suite calls real suspend and restart | single `IPowerActionApi` port (WDC-013) | B002 |
| U01-010 | `SetPower` throws on partial application | `RadioPowerResult` with per-adapter results | B065 |
| U01-011 | blocking calls without a token | token on pairing only; other blocking calls documented | B065, B072 |
| U01-012 | `GetWifiStatus` throws without a WLAN interface | returns `Unknown` | B064 |
| U01-013 | empty input reaches Wi-Fi calls | the key type makes it unrepresentable | B064 |
| U01-014 | scan facts ignore the native status | status propagated (WDC-V-002) | B064 |
| U01-015 | timeout rolls back, unreachable keeps a new profile | definite refusal restores once; timeout returns `Pending` with the profile intact | B064 |
| U01-016 | connect and forget by display text | `WifiNetworkKey` (SSID bytes plus security class; the review's bytes-only key was refuted) | B064 |
| U01-017 | profile SSID element form | always hex plus name | B064 |
| U01-018 | watch docs ("one feed per process") | rewritten with the registration rules | B063 |
| U01-019 | two sequential 90 s pairing waits | one 90 s wall-clock deadline plus caller token | B065 |
| U01-020 | pairing request handling and deferrals | `PairBluetoothAsync`: deferrals completed before cancelling, `onRequest` exceptions contained | B065 |
| U01-021 | CoreAudio watch ownership transfer | fixed with registrations | B063 |
| U01-022 | `EndpointWatch.Dispose` not interlocked | interlocked, idempotent | B063 |
| U01-023 | format probe misclassifies HRESULTs | return on HRESULTs other than S_FALSE/UNSUPPORTED_FORMAT | B066 |
| U01-024 | cached enumerator never reset; watcher abort unobserved | drop enumerator on disconnected/service HRESULTs (B066); `Stopped` change kind (B063) | B063, B066 |
| U01-025 | backlight reads the wrong level | read the level of the policy the driver reports | B066 |
| U01-026 | WaveOut frees driver-held buffers after an unverified close | retain them across repeated `Dispose`; single owner documented, no lock | B066 |
| U01-027 | rotation ignored by `Matches`/`Confirm` | compared in `Matches` when non-zero; `Confirm` deleted per D9 (WDC-V-001, WDC-003) | B067 |
| U01-028 | `SDC_SAVE_TO_DATABASE` always set (behaviour confirmed, not a defect) | keep fixed, document; no parameter | B072 |
| U01-029 | topology waits poll with a 10 min cap | replaced by the moved WSGM settle waiter `DisplayLayouts.WaitForTargetsAsync`, settle 500 ms and backstop 5 s passed by WSGM | B070 |
| U01-030 | an unreadable unrelated path fails capture | skip it; a matched unreadable target is a typed failure | B067 |
| U01-031 | mode results conflate outcomes | `DisplayModeResult` (WDC-010) | B069 |
| U01-032 | `DisplayModes` transient-write doc | doc fix | B069 |
| U01-034 | EDID read null monitor and timeout | WDC-015 | B067 |
| U01-035 | only `DisplayModes` has a gate | one static write gate (WDC-021) | B067 |
| U01-036 | library English strings | codes in WDC, strings in WSGM (WDC-023, WDC-V-003, WDC-V-005) | B066, B069, B071 |
| U01-037 | `DisableSignIn` bundles three policies | three primitives (WDC-006) | B071 |
| U01-038 | ModernStandby restore stops early | one enumeration, every device attempted | B071 |
| U01-039 | power notifications as raw handles | `PowerNotificationRegistration` (SafeHandle), `Dispose` unregisters; replaces `Unregister*` | B071 |
| U01-041 | WDC contract tests live in WSGM | move `RadioManagerTests` 90-160 into `WindowsRadioTests` and new `WifiProfileTests` | B046 |
| U01-042 | tests target net8 only | net8 and net10 | B046 |
| U01-043 | native struct sizes unpinned | `NativeLayoutTests` against hand-written byte buffers | B046 |
| U01-044 | tests without real assertions | WDC-018 | B046 |
| U01-045 | no child build props or editorconfig | explicit-root `Directory.Build.props` restating `EnforceCodeStyleInBuild`, nullable, docs, `NuGetAudit=false`; `.editorconfig` | B046 |
| U01-046 | version 0.1.0 | 0.2.0 at the first breaking batch | B063 |
| U01-047 | rethrow loses the stack | `ExceptionDispatchInfo` | B064, B066 |
| U01-048 | dead code | removed (Wi-Fi helpers, storage branches, capture arm of `ToWinRtDeviceId`) | B064, B066, B071 |
| U01-049 | Wi-Fi status doc mismatch | doc | B064 |
| U01-050 | `PairingKind` doc names host UI | neutral doc | B065 |
| U01-051 | PIN validation | WDC-008 | B065 |
| U01-052 | pairing callback signature | removed by the async API | B065 |
| U01-053 | `WifiProfile` docs and null check | fixed; stays public | B064 |
| U01-054 | `WifiRules` doc | doc | B064 |
| U01-055 | watch registers MSM as well | ACM only | B063 |
| U01-056 | `WlanClient` dispose not idempotent | idempotent owner | B063 |
| U01-057 | WLAN query flag out-parameter not zeroed | zeroed `ref` | B064 |
| U01-058 | REASON_CONTEXT size | full union size | B071 |
| U01-059 | closest-match pointer | pass NULL | B066 |
| U01-060 | marshalling on notification paths (not high-rate) | direct blittable reads where touched | B063, B066 |
| U01-062 | unknown spatial set statuses | map to `UnknownError` | B066 |
| U01-063 | layout details and warnings as strings | typed (WDC-023) | B069 |
| U01-064 | buffer count checks | moot after the cap removal (WDC-007) | B067 |
| U01-065 | layout extras do not reuse the resolved path | reuse it; rollback from one query (WDC-025) | B067 |
| U01-066 | host references in library docs | host-neutral wording | B072 |
| U01-067 | csproj comments | fixed | B046 |
| U01-068 | library AGENTS.md | proposed diff for sign-off | B072 |
| U01-069 | `WindowsRadio.cs` doc | fixed | B072 |
| U01-070 | README | rewritten sections | B072 |
| U01-071 | package metadata | fixed | B072 |
| U01-072 | `.coderabbit.yaml` | proposed diff for sign-off | B072 |
| U01-073 | `.gitignore` | added | B046 |
| U01-074 | mixed native code spaces | typed code spaces where touched | B064, B066, B071 |
| U01-075 | DC-only policy logic; non-DWORD values | fix DC-only; refuse non-DWORD at `Capture` with a typed failure (no new persisted shape) | B071 |
| U01-076 | ModernStandby caps | removed (WDC-007) | B071 |
| U01-077 | network volumes probed | skip `DriveType.Network` before `IsReady` | B071 |
| U01-078 | no public scheme list | public `EnumerateSchemes()`; the index form `EnumerateScheme(uint)` is kept (WSGM calls it) | B071 |
| U01-079 | preview failure detail | WDC-009 | B066 |
| U01-080 | tests derived from the decoder | independent byte fixtures | B046, B071 |
| U01-081 | duplicate `WaveFormat` | use internal `CoreAudio.WaveFormat` | B066 |
| U01-083 | `GetConsent` argument validation | validate | B065 |
| U01-084 | watch start race | moot with registrations | B063 |
| U01-085 | x86 request-list decoding | `Unsupported` on x86 | B071 |
| A01-F001 | EDID validity bit | as U01-004 | B001 |
| A01-F002 | real power dispatch in tests | as U01-009 | B002 |
| A01-F003 | lock-only repair can deadlock native unregister | per-registration ownership, unregister outside any lock | B063 |
| A01-F005 | WLAN registration lifetime | idempotent owner | B063 |
| A01-F006 | available-wait test can reach native code | deleted with the waits | B070 |

Former open item, now decided: the child `AGENTS.md` forbids success/failure enums for `ConnectWifi` ("two raw
integer contracts"). Decided: D4 approved; B064 shows the child `AGENTS.md` diff that allows typed outcomes carrying
the raw WLAN reason code and applies it with the batch.

## Refuted or no-change

- U01-033: no change. The legacy advanced-colour packets stay until the 24H2 replacement is independently verified
  (evidence gate).
- U01-040: no change beyond the per-API decisions above. The Intel GPU and Device Lab private CCD reads are accepted
  duplication: Intel needs one call already pinned by its own test, and sharing would add a WDC dependency and a public
  lookup to a GPU plugin (critic conflict 11 skips GPUIR-B5).
- U01-061: no change in code; docs only. No `SetMuted(AudioDirection, bool)` overload, because nothing would call it.
- U01-082: no change on its own; it is tidied only where B066 already edits the code.
- wdc.verify R1 (refuted sub-claim): the CoreAudio COM interfaces are private, not "internal, already declared"; B063
  widens their visibility itself (applied in WDC-014).
- wdc.verify R2 (refuted sub-claim): the WLAN notification seam cannot come from B064; it lands in B063 (applied in
  WDC-014).
- wdc.verify R3 (refuted reason): "nothing ever writes identities back" is false (`SettingsViewModel.Launch` refreshes
  matched identities); the conclusion stands, so no EDID migration code is added.
- wdc.verify R4 (refuted sub-claim): a bytes-only Wi-Fi key cannot tell two same-name networks with different security
  apart; the key carries SSID bytes plus security class (applied in the U01-016 row and B064).
- wdc.verify R5 (refuted sub-claim): the `RespondToPairing` test does not mutate the pairing table; only its missing
  assertion is a defect (applied in WDC-018).
