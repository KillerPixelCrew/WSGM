# Codex audit ledger (A01, A02, A02S01) findings

Scope: the 42 findings of the earlier Codex audits: `audit/A01/findings.json` (A01-F001 to A01-F006, Windows Device
Control and the Steam UI toolkit), `audit/A02/findings.json` and `findings.md` (A02-F001 to A02-F022, SDKs, device
packages, Device Lab worker, IR host, logon service, eng scripts, licensing) and `audit/A02S01/findings.json` and
`findings.md` (A02S01-F001 to A02S01-F014, IR firmware and its authoring generator). Every body survives in those
files; none is missing. Plan v2 Appendix B assigns every id to a batch or to a no-change reason; none is parked in
B178.

Every id below was checked against master `1329813f` (the audit baseline is the current head, so nothing has been
fixed since). One exception to "unchanged since the audit": the A01-F001 fix sits uncommitted in the
windows-device-control child working tree and is landed by B001.

Counts: 39 findings to implement (critical 0, high 3, medium 21, low 15, nit 0) and 3 no-change ids. Severities are
the current ones: where a domain verifier corrected the severity of the covering finding, that is noted. 37 of the
39 are already covered by a domain finding in `_plan/refactor-2.1/review/*.md` and `*.verify.md`; those sections name the
covering finding, confirm the claim still holds and give the batch, and the solution lives in that finding. A02-F015
and A02-F016 have no domain finding of their own (the gpuir review only confirmed the admitted A02_04 batch, plan
claim C20), so they are written up in full.

Plan v2 batches for this range: B001, B002, B046, B063, B070 (WDC); B053 (toolkit dispatch outcomes); B003, B154,
B156 (IR host and firmware); B004, B157 (Device Lab worker); B010, B043, B045, B052, B082, B142, B143, B147 (SDKs,
device host, packages); B025 (logon service); B036 (eng download and export helper); B172, B173 (shared sources and
licensing). The maintainer's answers in `_plan/refactor-2.1/DECISIONS.md` (2026-10-03) are applied: D2 accepts exactly the plan v2
byte-bound list (B147); D3 relicenses Device Lab as GPL instead of moving interop files (B173); D4 approves the
guidance diffs and D9 removes all readback machinery outside the Claw (B143); D11 adds IR catalog paging now with a
protocol bump on firmware and host (B156). Where a decision changed the covering finding's solution (A02-F010,
A02-F022, A02S01-F006, A02S01-F007), the section here carries the decided solution and wins over the covering
finding. No finding in this range is a security-hardening item, so the security drop moves nothing here, and the
counts are unchanged.

---

## High

### A02-F007: Recovery journal treats an unreadable existing path as absent

- **Severity:** high
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`LoadAsync`, `if (!File.Exists(_path))` at :230).
- **Covered by:** SDK-003 (`_plan/refactor-2.1/review/sdk.md`, confirmed by `sdk.verify.md`). Still present: `File.Exists` returns
  false for access denied, a directory at the path or an IO error, so the journal loads empty and the next save
  replaces the unread recovery originals. Fix per SDK-003 (A02_02 step 1): delete the `File.Exists` probe and add
  `catch (FileNotFoundException or DirectoryNotFoundException) { return; }` (absent, healthy, empty) ahead of the
  existing `catch (Exception ex) when (ex is not OutOfMemoryException)`, which already sets `FailureReason`, leaves
  the file untouched and so blocks every later save. A directory at the path surfaces as
  `UnauthorizedAccessException` and lands in that existing catch. Nothing else changes in `LoadAsync`.
- **Plan v2:** B010 (after B008).
- **Related:** A02-F009 (same batch), A02-F008 (no-change), A02_02, DEVICE-001, critic conflicts 16 and 19.

### A02-F010: Claw fresh-cycle reconciliation can retry an uncertain restoration

- **Severity:** high
- **Where:** `src/WSGM.Device.Msi.Claw/ClawRecoveryJournal.cs` (`Decide`, `return ClawReconciliationAction.Restore` at
  :87 before the `RestoreFailed` check at :90; statuses recorded at :45-50); pinned by `ClawPluginTests.cs` around
  :757-811.
- **Covered by:** PACKAGES-001 (`_plan/refactor-2.1/review/packages.md`, confirmed and widened: the existing `Block` branch also
  strands services). Still present. Fix per PACKAGES-001 as narrowed by D9: the Claw is the only vendor whose state
  can be read back, so the unresolved-entry policy belongs to the Claw alone and is not a shared rule.
  1. `ClawRecoveryJournal.Decide` looks at the status first. In order: a null firmware identity gives `Wait` (write
     nothing, entry stays Pending); an entry in `RestoredUnverified` or `RestoreFailed` gives `Keep` (trace, write
     nothing, the service stays usable); a different or unknown EC binding gives `Discard`; otherwise `Restore`, once.
     The `Block` branch and the `BlockService` calls on the restore path go, so no reconciliation outcome faults a
     service (`BlockService` stays only for an unavailable journal at start).
  2. SDK `DeviceRecoveryJournal.BeginAsync` stops throwing for an existing entry: with a matching binding an
     unresolved entry goes back to Pending with the first original kept and `Opened = false`, so an explicit Claw
     command is the re-arm; a different binding is replaced by the fresh Pending entry (PACKAGES-036).
  3. Claw controller entries are exempt from `Keep`: `RestoreControllerJournalEntryAsync` re-reads the mode before it
     writes, so it runs at every start whatever the status.
  4. `ClawRecoveryJournal.CompleteCommandAsync` is deleted; `JournalCommandAsync` keeps only "this command opened the
     entry and was Rejected, so `SetStatusAsync(RestoredVerified)` removes it". The two pinned Claw tests are
     inverted.

  The Ally half of PACKAGES-001 changes under D9: the Ally records no `RestoredUnverified` or `RestoreFailed` and
  has no re-arm. A restore whose writes all dispatched removes the entry (published as written); one whose write
  threw before reaching the device leaves the entry Pending for the next release or start, which is the first write
  of that original, not a retry of an uncertain one. `AllyRecoveryJournal.Decide`'s unresolved `Block` branch,
  `ArmAsync`, the `ReleasedUnverified` result on an unconfirmed power restore and the "enabling controller management
  re-arms" rule are deleted; a partly written controller-table release keeps the entry Pending.
- **Tests:** Claw (`ClawModelLifecycleTests`, `ClawPluginTests`): a `RestoreFailed` power entry with the same
  binding writes nothing at the next start, power is Owned and accepts a command, that command re-arms the entry, and
  stop writes the first original; a `RestoreFailed` entry with a changed binding is discarded; a controller entry
  restores at start whatever its status. SDK `DeviceRecoveryJournalTests`: `BeginAsync` on an unresolved entry
  returns it Pending with the first original and `Opened = false`. Ally `PluginTests`: a dispatched restore removes
  the entry, a restore whose write throws leaves it Pending, and no Ally path stores an unresolved status.
- **Plan v2:** B143 (after B142, Device API 12); decided: D4 approved (Claw AGENTS diff shown with the batch), D9 no
  readback machinery for any vendor except the Claw.
- **Related:** PACKAGES-008, PACKAGES-036, PACKAGES-037, SDK-016, SDK-017 (refuted, do not reintroduce).

### A02-F012: Lab worker session disarms a failed safety zero and hides its failure

- **Severity:** high
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerSession.cs` (`ZeroQuietly` at :185, called from the stream failure
  path :121 and `ZeroIfStale` :180; `_lastFrame` :35; `StreamError` :47).
- **Covered by:** LABCORE-003 (`_plan/refactor-2.1/review/labcore.md`, confirmed by `labcore.verify.md`, which notes the
  "kills the worker" part is not reachable today because every rumble output throws an `InvalidOperationException`
  subtype). Still present. Fix: admitted A02_03 with the R4 simplification, `_lastFrame = DateTime.MaxValue` only after
  a zero that returned normally, sticky `StreamError`, `_zeroFailure` kept only to de-duplicate the log line.
- **Plan v2:** B004.
- **Related:** A02_03, R4, LABCORE-002.

---

## Medium

### A01-F001: Display topology decodes EDID ids on the wrong validity bit

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs` (`ReadTarget`,
  `DecodeEdidIds`).
- **Covered by:** U01-004 and WDC-012 (`_plan/refactor-2.1/review/wdc.md` claim C18, ledger row "as U01-004"). The fix (bit
  `1u << 2`, `DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAG_EDID_IDS_VALID`) is already in the child working tree
  (`DisplayTopology.cs:169-173`, plus tests) but uncommitted, with mixed line endings (WDC-012). B001 normalizes to
  CRLF, builds, runs the three W02_01 tests, commits and pushes the child, then records the gitlink with a pathspec
  commit.
- **Plan v2:** B001.
- **Related:** W02_01, U01-004, WDC-012.

### A01-F002: WDC power test can dispatch a real suspend or restart

- **Severity:** medium
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsPowerTests.cs:55-62`
  (`CancelledActionsNeverDispatch`); `WindowsPower.Actions.cs:30-57`.
- **Covered by:** WDC-013 (U01-009). Still present: the test calls `WindowsPower.SuspendAsync` and
  `RequestActionAsync(Restart, ...)` directly. Fix per WDC-013: one internal `IPowerActionApi` port with a native
  instance and fake-backed tests, replacing the sealed W02_02 brief's two interfaces.
- **Plan v2:** B002.
- **Related:** U01-009, W02_02, BUILD-001 (power half).

### A01-F003: Wi-Fi watch start, stop and publish are not serialized

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs` (static
  `WifiWatchLock` :19, `StartWifiWatch`/`StopWifiWatch`/`DetachWatch` :37-100).
- **Covered by:** WDC-014 (WDC-B2) and WINSVC-022 (consumer). Still present. Fix per WDC-014: per-registration
  ownership, `StartWifiWatch` returns an `IDisposable` registration that unregisters outside any lock; the static slot
  and `StopWifiWatch` are deleted; `RadioManager` drops `_feedWork`, `QueueFeedWork` and `_bluetoothWatchGeneration`.
  No lock-only patch (A01-F003's own deadlock warning stands).
- **Plan v2:** B063.
- **Related:** U01-001, U01-007, U01-084, A01-F005, WINSVC-022, critic conflict 9.

### A01-F004: Answered JavaScript errors become successful evaluations

- **Severity:** medium
- **Where:** `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiTransportSession.cs` (`EvaluateAsync` around
  :205-212); `Client/SteamClientScript.cs` (`ParseWrite` around :137-178).
- **Covered by:** TOOLKITCS-003 (`_plan/refactor-2.1/review/toolkitcs.md` claim C38). Still present. Fix per TOOLKITCS-003: one
  `SteamUiDispatch` outcome (NotSent, Closed, Unanswered, Answered); client writes report NotSent, Unknown, Rejected or
  Applied, an answered JavaScript error is Rejected; `SteamUiTransportSession` and `CefEvalResult` are deleted. T01_01
  is folded in and its tests become this batch's tests.
- **Plan v2:** B053.
- **Related:** U02A-SUTC-001, U02B-SUTC-007, T01_01, TOOLKITCS-V-003.

### A02-F003: A throwing deadline cancellation callback can escape the clock thread

- **Severity:** medium (verifier lowered SDK-001 from high)
- **Where:** `src/WSGM.Device.Sdk/Lifecycle/ActiveClock.cs` (`Run`, `source.Cancel()` at :106 with only
  `ObjectDisposedException` caught at :108).
- **Covered by:** SDK-001. Still present. Fix per SDK-001: an internal `ActiveClockCore(Func<long>)` holds Advance,
  Pending and CollectDue; due sources cancel with `CancelAsync` and their faults are observed, so plugin continuations
  never run on the clock thread. No owned clock service, no clock-bearing `Deadline`, no shutdown join.
- **Plan v2:** B043.
- **Related:** A02-F004 (no-change), SDK-040, SDK-041, critic conflict 18.

### A02-F005: Device serializer can dispose its semaphore before retained work releases it

- **Severity:** medium (verifier lowered SDK-002 from high)
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceCommandSerializer.cs` (`Dispose` :73-76 disposes `_gate`;
  `StartObservation` :198 discards the `Task.Run`; `StopObservation` :220).
- **Covered by:** SDK-002. Still present. Fix per SDK-002: the gate is no longer disposed (a `SemaphoreSlim` without
  `AvailableWaitHandle` holds nothing to free), so a late `Release` in a `finally` cannot hit a disposed object; the
  journal loses `IAsyncDisposable` in B142.
- **Plan v2:** B010 (serializer), B142 (journal half).
- **Related:** SDK-V-004.

### A02-F006: Post-command publication uses a wall timer for an active-time deadline

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceCommandSerializer.cs` (`RepublishAsync`, `CancelAfter` at :281);
  host side `DevicePluginRuntime.cs` per-command `CancelAfter(command.Deadline.Remaining)`.
- **Covered by:** SDK-004 (serializer) and DEVICE-028 (host half, low). Still present. Fix per those findings: the
  wait uses the SDK active-time `Deadline` cancellation instead of a wall timer, so freeze time is not consumed;
  ScenarioMode publication failure stays Indeterminate.
- **Plan v2:** B010 (serializer), B082 (router and runtime, after B043).
- **Related:** critic conflict 18.

### A02-F009: Undefined recovery status can be stored and loaded

- **Severity:** medium
- **Where:** `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs` (`SetStatusAsync`, `Validate` around :335-354).
- **Covered by:** SDK-003 (A02_02 step 4). Still present: no `Enum.IsDefined` anywhere in the file. Fix:
  `SetStatusAsync` throws `ArgumentOutOfRangeException` when `!Enum.IsDefined(status)`, before the gate or any IO;
  `Validate` throws `InvalidDataException` for an entry whose `Status` is undefined, so the existing `LoadAsync`
  catch sets `FailureReason` and the file is kept (there is no separate "Corrupt" state; unavailable is the state).
- **Plan v2:** B010.
- **Related:** A02-F007, A02_02.

### A02-F014: Lab worker EOF cleanup still runs queued operations before retirement

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs` (`Run` `finally`, `dispatcher.Join()` at :136, then
  `lock (sessions)` :137).
- **Covered by:** LABCORE-005 (with the EOF half of LABCORE-004). Still present. Fix per B157: after EOF the
  dispatcher stops invoking queued requests, the running call is awaited, then every session zeroes and disposes. No
  new deadline. Concretely: the host sets one `closing` flag (volatile field of the instance host B157 introduces)
  in the `finally` before `CompleteAdding`; `Dispatch` keeps draining `GetConsumingEnumerable` but skips `Handle`
  while `closing` is set (the reader is gone, so no reply is owed); `Join` then returns once the running call ends,
  and the existing zero-and-dispose loop runs. Refusing calls cancelled before dispatch is not part of it
  (LABCORE-004 main half is no-change).
- **Plan v2:** B157 (after B004).
- **Related:** LABCORE-004, A02-F011, A02-F013.

### A02-F015: IR reply bound counts UTF-16 characters instead of wire UTF-8 bytes

- **Severity:** medium
- **Where:** `src/WSGM.Plugin.Ir/IrEndpoint.cs` (`IrEndpointConnection.Exchange`, the `line.Length >= MaxFrame` guard
  at :444; `MaxFrame = 32768` at :228; the request side already checks UTF-8 bytes at :424).
- **Problem:** The protocol frame is 32768 bytes (firmware `MaxFrame`, protocol.md). The reply reader counts `char`s,
  so a reply padded with non-ASCII text passes at up to 32768 characters while being up to three times that on the
  wire. The host then parses a frame the protocol forbids, and the request and reply sides disagree about the same
  bound. Still present.
- **Best solution:** Admitted batch A02_04 step 1, unchanged. Keep the incremental `line.Length` guard (it bounds
  memory when no LF arrives). After the completed line is extracted and before the boot-noise check and
  `JsonDocument.Parse`, refuse `Encoding.UTF8.GetByteCount(text) > MaxFrame` with the existing
  `InvalidDataException("IR response exceeds frame limit.")`. CR stripping and LF framing are unchanged. The 32 KiB
  frame is a protocol bound listed in D2, so this refuses and never truncates. Nothing is resent; the existing failure
  path drops the link.
- **Tests:** In `IrEndpointConnectionTests`, ScriptedLink fixtures: a matching identify reply padded with non-ASCII
  text to 32769 UTF-8 bytes (below the character bound) throws `InvalidDataException`, drops the link and writes
  exactly one identify frame; the same reply at exactly 32768 bytes is accepted; surrogate-pair padding honours the
  byte bound. Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrEndpointConnection"`.
- **Plan v2:** B003 (prerequisite of B154).
- **Related:** A02_04, A02-F016, GPUIR-013 (classification added later on the same `ReadReply`), GPUIR-030, gpuir
  review claim C20.

### A02-F017: IR state loaders treat inaccessible state as absent

- **Severity:** medium
- **Where:** `src/WSGM.Plugin.Ir/IrPayload.cs` (`IrLibrary.LoadAsync` `File.Exists` at :112, `IrPairing.LoadAsync`
  at :149).
- **Covered by:** GPUIR-021 (IR loader half) and gpuir review claim C21, which removes the NOT READY disposition. Still
  present. Fix per GPUIR-021, with no new result type: `IrLibrary.LoadAsync` and `IrPairing.LoadAsync` drop the
  `File.Exists` probe and open the file directly; only `FileNotFoundException`/`DirectoryNotFoundException` returns
  `IrLibrary.Empty` / `null`. Every other failure (access denied, a directory at the path, malformed JSON, failed
  `Validate`) throws as a corrupt file already does today; `IrPlugin.StartAsync` lets it propagate and
  `PluginHost` quarantines the plugin with Failed health carrying the message (`PluginHost.cs` around 600-615), so
  no save path runs. The import action keeps its explicit missing-backup refusal (`IrPlugin.cs:431`). File shapes
  unchanged, so no migration. Tests per GPUIR-021 (`IrLibraryTests`: locked file, directory at path, corrupt file
  each throw and keep the bytes; absent loads empty; same for the pairing file).
- **Plan v2:** B154.
- **Related:** A02-F007 (sibling), GPUIR-002, GPUIR-003, critic conflict 16.

### A02-F020: UWP bridge export validation is an ASCII search

- **Severity:** medium
- **Where:** `eng/build-uwp-bridge.ps1` (Validate branch, `[Text.Encoding]::ASCII.GetString($bytes)` at :83, search
  around :78-91).
- **Covered by:** BUILD-007 (`_plan/refactor-2.1/review/build.md` claim C22). Still present. Fix per BUILD-007: a `Get-DllExports`
  helper built from the existing `vswhere`/`VsDevCmd`/`dumpbin /exports` block in `build-steam-input-lease.ps1`, and
  the four names are matched in the export table. No PE parser, no new dependency (MSVC is already required).
- **Plan v2:** B036.
- **Related:** BUILD-008 (same batch, `Get-PinnedAsset`).

### A02-F021: Common packer admits entry counts and payloads the runtime rejects

- **Severity:** medium
- **Where:** `eng/package-plugin.ps1:52` (4096 entries, managed PE check only) versus `src/WSGM/Core/PluginPackageFile.cs`
  limits (`MaxPackageEntries`/`MaxPackageFiles` at :31-32); the Device Lab copy in
  `src/WSGM.DeviceLab/Packaging/PluginPackageWorkflow.cs` and its entry-count refusal in
  `DeviceLabPackageSnapshot.cs` around :80-89; `eng/pack-device.ps1`.
- **Covered by:** SDK-012 (medium) and SDK-V-003 (low). Still present. Fix per those findings and D2: package rules
  live in one place and every packer calls `plugin-manifest.cs validate-package`, which also refuses native images;
  the entry and file count caps are removed (no arbitrary limits) and only the 128 MiB per-file and 512 MiB total
  byte bounds stay, in both reader and packers.
- **Plan v2:** B147; decided: D2 accepts exactly the plan v2 list, which includes the 128 MiB per-file and 512 MiB
  total package bounds.
- **Related:** SDK-008, BUILD-014, BUILD-027.

### A02-F022: MIT Device Lab compiles generic source from GPL product paths

- **Severity:** medium
- **Where:** `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj:32-39` (`Kernel32.cs`, `NativePackageSource.cs`,
  `NativePathIdentity.cs`, `NativeHidHide.cs` from `src/WSGM/Interop`).
- **Covered by:** LABCORE-039, LIBRARY-025 (`NativePackageSource` placement) and BUILD-019 (linked sources home).
  Still present. Fix per D3 (decided: Device Lab becomes GPL): no interop file is relicensed, duplicated or moved for
  licensing; the four `Compile Include` links stay valid because the Lab now carries the product's licence.
  Concretely: delete `src/WSGM.DeviceLab/LICENSE` and embed the root GPL-3.0-or-later `LICENSE` under the same
  `WSGM.DeviceLab.Help.LICENSE` logical name (as `WSGM.csproj` already ships the root file instead of a second copy),
  so the wizard's "Help and licences" window shows GPL; add
  `<PackageLicenseExpression>GPL-3.0-or-later</PackageLicenseExpression>` to `WSGM.DeviceLab.csproj`; the README
  "Licence" section says GPL-3.0-or-later and drops the paragraph that licenses the four interop copies to the Lab
  under MIT; `src/WSGM.DeviceLab/AGENTS.md` (line 7, "separate MIT-licensed") and `docs/device-plugin-system.md`
  section 19 (`src\WSGM.DeviceLab`, MIT) say GPL; `THIRD_PARTY_NOTICES.md` keeps its third-party entries. The SDKs
  stay MIT, and the scaffold template's `LICENSE.txt` stays MIT because a generated plugin links only the MIT Device SDK.
  `NativePackageSource` still moves into Device Lab, but as LIBRARY-025's placement fix, not for the licence.
- **Tests:** none beyond the build: `dotnet build src\WSGM.DeviceLab\WSGM.DeviceLab.csproj -c Release` and a check
  that the embedded `WSGM.DeviceLab.Help.LICENSE` resource starts with "GNU GENERAL PUBLIC LICENSE".
- **Plan v2:** B173 resolves it (Appendix B); decided: D3 relicenses Device Lab as GPL, so B173 updates the Lab's
  licence file, csproj metadata, README, AGENTS and docs instead of creating MIT declarations under
  `src/Shared/Interop`. B172 no longer waits on a licence decision: the `NativeHidHide.Paths` merge and the shared
  control adapter are plain cleanup. `NativePackageSource` has no other consumer (only `DeviceLabPackageSnapshot.cs`
  uses it).
- **Related:** INPUT-032 (its licence gate is lifted by D3), BUILD-023, INSTALL-032.

### A02S01-F001: An unfinished network learn is retargeted to a replacement socket

- **Severity:** medium (verifier lowered GPUIR-004 from high)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (`learningOut = &out` at :450, `client = incoming` at :585,
  the other `client.stop()` sites).
- **Covered by:** GPUIR-004. Still present (`network` is `Channel{client, false}`, so `learningOut` for a network
  learn is `&client` itself). Fix per GPUIR-004: one `stopClient()` helper
  (`if (learningOut == &client) cancelLearn("cancelled"); if (client) client.stop();`) used at :512, :594 and in
  `serveNetwork`, where it replaces `if (client) client.stop();` unconditionally before `client = incoming;`, so a
  learn whose socket already dropped (`client` false) is still retired before the slot is reused. The learn check
  stays inside the helper because `cancelLearn` also clears the LED. No connection generation.
- **Plan v2:** B156; decided: D11 catalog paging now, so the batch gate is lifted.
- **Related:** GPUIR-031, GPUIR-032.

### A02S01-F002: Feedback deadline can equal the inactive sentinel after millis wrap

- **Severity:** medium (Codex; the covering GPUIR-031 bundle is rated low)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (`feedbackDeadline = millis() + duration` at :72, loop check
  at :731).
- **Covered by:** GPUIR-031 (F002 item). Still present. Fix, simpler than GPUIR-031's F002 item: the loop's
  `int32_t(millis() - feedbackDeadline) >= 0` is already wrap-safe; only the `0` sentinel is wrong. Add one
  `bool feedbackActive`, set it in `feedback()`, test `feedbackActive && int32_t(millis() - feedbackDeadline) >= 0`
  in `loop()` and clear the flag instead of zeroing the deadline. No `feedbackStart` or stored duration. GPIO,
  duration and colour unchanged.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F003: Numeric A/C model narrows from int to int16 without bounds

- **Severity:** medium (Codex; GPUIR-031 bundle low)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (`sendAc`, `model.as<int16_t>()` at :194).
- **Covered by:** GPUIR-031 (F003 item). Still present. Fix: a numeric model outside `[-1, 32767]` replies
  `invalid-ac-state` before any `IRac` call.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F004: Scalar code parser accepts signed or whitespace-prefixed hex

- **Severity:** medium (Codex; GPUIR-031 bundle low)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (`sendCode`, `strtoull` at :177).
- **Covered by:** GPUIR-031 (F004 item). Still present. Fix: accept only an optional `0x`/`0X` and 1 to 16 hex digits
  before `strtoull`, anything else replies `invalid-code`.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F005: NVS pairing and web writes report ok without checking persistence

- **Severity:** medium (Codex; GPUIR-031 bundle low)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (`putString` at :428-429 and :523-525, `settings.begin` in
  `setup`).
- **Covered by:** GPUIR-031 (F005 item, simplified in the gpuir review's over-engineering table). Still present. Fix:
  write NVS first, check `begin` (kept in one `bool storageReady` from `setup`), `settings.clear()` on the Wi-Fi reset
  path and each `putString` byte count against `value.length()` (Preferences returns the string length on success
  and 0 on failure, and the web user and password may legitimately be empty, so test equality, not non-zero); on
  any failure reply `storage-failed` and leave the RAM
  credentials, token and Wi-Fi state unchanged; no retry. Preferences has no transaction, so a failure mid-way can
  leave the flash partly written: the reply tells the user and they set it again by hand, nothing is retried. The
  host needs no edit for this status (B156's host change is only the D11 catalog paging): after B154 any matched
  non-success status is a
  refusal, and `Describe` already shows unknown statuses literally ("IR endpoint refused wifi: storage-failed.").
  `protocol.md` lists the new status.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031, GPUIR-013.

### A02S01-F006: Generated definitions are used after unchecked deserialization

- **Severity:** medium (Codex; GPUIR-031 bundle low)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/src/main.cpp` (`deserializeJson(remotes, ...)` and
  `deserializeJson(catalog, ...)` at :718-719).
- **Covered by:** GPUIR-031 (F006 item). Still present. Fix: check both results; if either fails, log to serial,
  `remotes.clear()`, `catalog.clear()` and set `catalog["remotes"].to<JsonArray>()`, and keep one
  `bool definitionsReady`; while it is false the paged `remotes` reply (A02S01-F007) answers a single chunk
  `{"chunk":"{\"remotes\":[]}","index":0,"count":1}` instead of slicing `CatalogJson`, so the host assembles the
  same empty catalog it already parses, presses answer `unknown-remote`, and failed definitions never emit.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F007: A validated catalog can exceed the protocol reply frame limit

- **Severity:** medium (Codex; GPUIR-031 bundle low)
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (button cap around :424-425, step cap around :263-264,
  `load`/`generate`); `reply()` in `main.cpp`.
- **Covered by:** GPUIR-031 (F007 item) and gpuir review claim C24. Still present. Fix per D11 (decided: catalog
  paging now, no build-time size refusal). Delete the 128-button cap (:424-425) and the 32-step cap (:263-264); no
  size check replaces them. The catalog is served in chunks under protocol 2:
  1. `embed_remotes.py` `generate()` keeps emitting `CatalogJson` once (the web routes still parse it into `catalog`)
     and also emits `CatalogChunkEnds[]` and `CatalogChunkCount`: byte offsets into `CatalogJson` chosen so each
     slice, JSON-escaped inside the longest reply envelope (64-character id, `"status":"ok"`, the chunk fields and
     LF), fits 32768 bytes, and no boundary splits a UTF-8 sequence. Any catalog size fits; nothing is refused.
  2. Firmware: `remotes` takes `chunk` (default 0). In range it answers `ok` with
     `{"chunk":"<slice>","index":i,"count":N}`, the slice copied from `CatalogJson` into the reply as a string; out of
     range it answers `invalid-chunk`. The envelope `v` and the identify `protocol` become 2, `dispatch` answers
     `protocol-mismatch` to anything else, and the firmware version becomes 0.5.0.
  3. Host: `IrEndpointConnection` sends `v = 2` and `ParseIdentity` requires protocol 2 (the existing "incompatible"
     message tells the user to flash). `ListRemotesAsync` requests chunk 0, reads `count`, requests 1 to `count - 1`
     in order, appends the slices and parses the joined text with the existing `IrRemoteCatalog` deserializer
     (`MaxDepth = 16`). A reply whose `index` or `count` disagrees with the request fails with
     `InvalidDataException` through the existing failure path; nothing is resent. Greenfield 2.0: no protocol 1
     fallback.
  4. `protocol.md` becomes "IR endpoint protocol 2" and documents `chunk`, `index`, `count` and `invalid-chunk`;
     README notes the firmware and protocol versions.

  Every reply still respects the 32 KiB frame (A02-F015 keeps refusing an oversized one), so this pages rather than
  truncates.
- **Tests:** platformio build of a catalog whose joined JSON exceeds 32768 bytes (no build failure); host
  `IrEndpointConnectionTests` with ScriptedLink: a catalog served in three chunks, one containing multi-byte
  characters at a boundary, assembles into the same `IrRemoteCatalog`; a chunk reply with a wrong `index` or `count`
  fails and drops the link without a resend; an identify reporting protocol 1 is refused as incompatible. Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrEndpointConnection"`.
- **Plan v2:** B156; decided: D11 catalog paging now. B156 is no longer build only: it gains `IrEndpoint.cs` and
  `IrEndpointConnectionTests`, firmware 0.5.0 and protocol 2 replace "firmware 0.4.1, protocol 1 unchanged", and
  the manual M01-37/38 run after the maintainer flashes also lists the remotes.
- **Related:** GPUIR-031, A02-F015 (host frame bound stays, per chunk), A02S01-F006 (failed definitions serve an
  empty catalog as one chunk).

---

## Low

### A01-F005: WLAN notification registration dispose is not idempotent

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs`
  (`WlanNotificationRegistration`, `_registered` at :236, `Dispose` around :248-257, set at :288).
- **Covered by:** WDC-014. Still present. Fix: the per-registration owner's `Dispose` is idempotent (interlocked flag),
  unregisters before closing the handle and outside any lock, with an idempotent `WlanClient`.
- **Plan v2:** B063.
- **Related:** U01-056, A01-F003.

### A01-F006: Available-wait cancellation test can reach native topology code

- **Severity:** low
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs:110`
  (`AvailableWaitRejectsInvalidDeadlineAndCancelsBeforeNativeQuery`).
- **Covered by:** WDC-026 (wait-test part) and WINSVC-028. Still present. Fix: the WSGM settle waiter moves into WDC as
  `WaitForTargetsAsync` with caller-supplied timing and replaces WDC's own topology waits; this test is deleted with
  the waits it covers and replaced by pure waiter tests. No query seam.
- **Plan v2:** B070 (the radio half of WDC-026 is B046).
- **Related:** U01-008, U01-029, BUILD-B11 prerequisite list (build.verify).

### A02-F001: Common manifest reader does not enforce byte or depth bounds

- **Severity:** low (verifier lowered SDK-010 from medium; the Plugins root is administrator-protected)
- **Where:** `src/WSGM.Plugin.Sdk/PluginManifestReader.cs` (`TryRead`, only `json.Length == 0` at :24, default-depth
  `Deserialize` at :32); the host's routing parse.
- **Covered by:** SDK-010. Still present. Fix: reuse the Device SDK 256 KiB and depth 16 bounds (listed in D2) in the
  common reader and the host routing parse; refuse, never truncate. Replaces A02_01.
- **Plan v2:** B045.
- **Related:** A02_01, SDK-009, SDK-011, critic conflict 20.

### A02-F002: Plugin SDK README contradicts its real dependencies and GPU placement

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Sdk/README.md:5` ("Graphics") and :13 ("It depends on nothing").
- **Covered by:** SDK-019. Still present. Fix: README rewritten to the real `Device.Sdk` and `SteamUiToolkit`
  references and the Device > GPU placement as part of Plugin API 4; the IR README dependency sentence is fixed in the
  documentation pass.
- **Plan v2:** B052 (IR README in B177).
- **Related:** SDK-V-006, TOOLKITCS-V-001, A02_DOC.

### A02-F011: Lab watchdog waits behind a blocked hardware call on every session

- **Severity:** low (verifier lowered LABCORE-002 from high: no current flow streams on one session while another runs
  a slow call)
- **Where:** `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs` (timer at :91, `lock (sessions)` in `Dispatch` at :166 and
  in `ZeroStale` at :304-306).
- **Covered by:** LABCORE-002 as corrected by `labcore.verify.md`. Still present. Fix: `ZeroStale` takes the existing
  lock with `Monitor.TryEnter` and returns when busy. No per-session locks, no concurrent zero on a busy lane.
- **Plan v2:** B157.
- **Related:** A02-F014.

### A02-F016: Malformed IR reply access can leave a JsonDocument undisposed

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/IrEndpoint.cs` (`IrEndpointConnection.Exchange`, `JsonDocument.Parse` at :460 and the
  property reads that follow through :485).
- **Problem:** After `Parse`, `responseId.GetString()`, `GetProperty("v").GetInt32()` and `GetProperty("status")` throw
  on a reply with a non-string id, a missing or non-integer `v` or a missing `status`. Those throws leave `response`
  undisposed, so its pooled buffers are not returned. Only the matching success path is meant to hand ownership to
  the caller. Still present.
- **Best solution:** Admitted batch A02_04 steps 2 and 3, unchanged. Extract
  `private static JsonDocument? ReadReply(string text, string id, string operation)` in `IrEndpointConnection`: parse
  with `MaxDepth = 16`, hold `transferred = false`, and in a `try`/`finally` dispose unless transferred. A foreign or
  missing id returns null (the caller keeps reading); a matching reply with the expected status sets `transferred` and
  returns the document; refusals and unknown or wrong-version statuses keep today's exception classes and messages.
  `Exchange` loops while `ReadReply` returns null and returns the transferred document to its existing `using`
  callers. This beats sprinkling `Dispose` calls on each branch because one `finally` covers every throw, including
  future ones. GPUIR-013's refusal classification later lands inside the same method.
- **Tests:** ScriptedLink fixtures in `IrEndpointConnectionTests`: a matching reply with a malformed id type, version
  or status shape fails and drops the link without a resend; a stale valid id followed by the current reply sends one
  request and the accepted body is still readable by the caller. Also fix the test helpers `Frame.Id`/`Op` to read
  inside a scoped `using` document. Filter:
  `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrEndpointConnection"`.
- **Plan v2:** B003.
- **Related:** A02_04, A02-F015, GPUIR-013, gpuir review claim C20.

### A02-F018: Serial link constructor does not release the SerialPort on a failed Open

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/IrEndpoint.cs` (`SerialIrLink` constructor, `new SerialPort` at :109, `_port.Open()`
  at :118).
- **Covered by:** GPUIR-033 and gpuir review claim C22, which replaces the "serial factory seam" disposition. Still
  present. Fix: try/catch around `Open` that disposes the port and rethrows, as `TcpIrLink` already does; DTR, RTS and
  rate unchanged; no new seam.
- **Plan v2:** B154.
- **Related:** GPUIR-033.

### A02-F019: Logon service reports stopped while queued launch work remains

- **Severity:** low (install.verify lowered INSTALL-007: the window is the milliseconds between the STOPPED report
  and process exit)
- **Where:** `src/WSGM.LogonService/ServiceHost.cs` (`StopRequested` :22, wait at :136, STOPPED at :138, set at :172);
  `SessionLauncher.cs` around :41-68.
- **Covered by:** INSTALL-007 (`_plan/refactor-2.1/review/install.md` claim C15, corrected by `install.verify.md`). Still present.
  Fix: one stop flag under `Gate`; no dispatch-owner type and no watchdog join (setup stops the service so the
  watchdogs die with it). Concretely: `SessionLauncher` gets `private static bool _stopping` and
  `internal static void Stop()` that sets it under `Gate`; `ServiceMain` calls `SessionLauncher.Stop()` right after
  `StopRequested.Wait()` and before the STOPPED report. In `HandleLogon` the WSGM launch takes `Gate` once around
  the `_stopping` check, the `TryLaunch` at :121 and the `Sessions[sessionId] = state` insert, so a launch either
  completes and is recorded before STOPPED or never starts (checking the flag and then launching outside the lock
  would leave the same window). A stopping service logs "Sign-in startup skipped: the service is stopping." and
  returns through the existing token cleanup. The watchdog's Explorer fallback (:283) and the desktop-shell probe
  (:523) are not gated: they die with the process, and gating the fallback could strand a user without a shell.
  Test: with `_stopping` set, the launch path makes no `TryLaunch` call (through the `ISessionHost` seam INSTALL-V-006
  adds in the same batch).
- **Plan v2:** B025.
- **Related:** INSTALL-001, INSTALL-V-006.

### A02S01-F008: Malformed remote values escape the DefinitionError contract

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (`choice`, `button`, `climate`, `sequence` around :229).
- **Covered by:** GPUIR-031 (F008 item). Still present. Fix: explicit presence and type checks at those boundaries
  raise `DefinitionError` naming the definition; valid output unchanged.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F009: Static page-reference check misses valid HTML attribute syntaxes

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (`check_page_references` around :401-404).
- **Covered by:** GPUIR-031 (F009 item). Still present. Fix: standard-library `html.parser.HTMLParser` reads static
  `data-button`/`data-sequence` attributes with any quoting or case; document that only static literal attributes are
  checked.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F010: Explicit address without command is silently ignored

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (`code`, `button` defaults around :151).
- **Covered by:** GPUIR-031 (F010 item). Still present. Fix: `code()` rejects an explicit `address` without `command`
  after inherited-default removal; tracked remotes all use `command` and stay valid.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F011: Duplicate JSON definition keys silently replace earlier values

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (`load`, JSON parse around :412).
- **Covered by:** GPUIR-031 (F011 item). Still present (no `object_pairs_hook`). Fix: `object_pairs_hook` rejects
  duplicate keys at every nesting level with a `DefinitionError` naming the file; the `remotes.local` folder override
  stays.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031.

### A02S01-F012: Generated climate page trusts stale stored drafts

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (`SCRIPT` climate stored draft around :325).
- **Covered by:** GPUIR-031 (F012 item). Still present. Fix: a restored draft keeps each field only if valid for the
  current catalog, otherwise the catalog default; appearance unchanged; nothing inferred about the appliance.
- **Plan v2:** B156; decided: D11 catalog paging now (plan v2 allows recording it as no-change with the reason if
  the source does not confirm it at implementation time).
- **Related:** GPUIR-031.

### A02S01-F013: Parallel climate intents derive from the same confirmed state

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/embed_remotes.py` (`SCRIPT` send and step buttons around :335).
- **Covered by:** GPUIR-031 (F013 item). Still present (`send` builds `next` from the confirmed `state`, which only
  advances after `await post(...)`). Fix, keeping the page's "shows what it last sent" behaviour: add
  `let draft = Object.assign({}, state)` and one request counter in `post`. Each click builds `next` from `draft` and
  sets `draft = next` at once, so two rapid `+` clicks ask for +1 and +2. A reply touches the status line only when
  it belongs to the latest request; when that latest reply is `transmitted`, `state = next` is saved and rendered,
  otherwise `draft` resets to `state`. The rendered values stay the confirmed ones, so appearance is unchanged;
  every click still sends its own request, nothing is queued or retried.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031, A02S01-F014.

### A02S01-F014: Custom remote status can be overwritten by an older reply

- **Severity:** low
- **Where:** `src/WSGM.Plugin.Ir/Firmware/remotes/hisense-tv/index.html` (press and click handler around :255).
- **Covered by:** GPUIR-031 (F014 item). Still present. Fix: one request counter; a reply updates the status only when
  it belongs to the latest request. Layout and codes unchanged, no retransmission.
- **Plan v2:** B156; decided: D11 catalog paging now.
- **Related:** GPUIR-031, A02S01-F013.

---

## Missing bodies

None. All 42 ids in this range have full bodies in `audit/A01/findings.json`, `audit/A02/findings.json` and
`findings.md`, and `audit/A02S01/findings.json` and `findings.md`; the ledger rows in
`claude-findings-disposition.md` (lines 626-653) repeat their titles.

## Refuted or no-change

- **A02-F004** (deadline registrations outlive disposed sources): accepted bounded no-change (SDK-040). No in-repo
  deadline exceeds about 20 s, so retention is small and self-ending, and sdk.verify showed the proposed pruning
  cannot see disposed sources, so it would fix nothing.
- **A02-F008** (journal save failure does not latch unavailability): no change (sdk.verify batch problem 1). A failed
  `SaveAsync` already throws and refuses its own mutation before the hardware write, and `_entries` changes only after
  a successful move; a latch would turn one transient file lock into the loss of every journalled control until
  restart. A02_02 steps 2 and 3 are dropped.
- **A02-F013** (Lab worker queue and frame parsing have no admission bound): no change. The only producer is the
  authenticated wizard, which sends one blocking call at a time plus status polls and slider frames; client calls are
  bounded by their deadlines, a count cap or Busy reply would be an arbitrary limit, and labcore.verify dropped the
  stale-frame filter (LABCORE-006) proposed as its replacement.
