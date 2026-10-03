# WindowsDeviceControl audit unit (ledger U01) findings

Scope: the 85 saved ledger rows U01-001 to U01-085 from the first Claude audit of WindowsDeviceControl (WDC), unit
`U01-WDC` (`reports/U01-WDC.findings.json`, bodies in `claude-findings-raw.json`). They cover the radio (Wi-Fi,
Bluetooth, radio power), Core Audio, backlight, display (topology, layouts, modes, scaling, colour, EDID), power,
Modern Standby, wake security, storage and the library's tests, build and docs, plus the WSGM consumers where a contract
crosses the boundary. Every body survived. The Codex audit A01 recorded five WDC ids (A01-F001, F002, F003, F005, F006);
they appear here under Related only, because each duplicates a U01 row.

Baseline: parent `master` 1329813f, child `external/windows-device-control` `main` 2f07485 plus the uncommitted W02_01
edit (`DisplayTopology.cs`, `DisplayTopologyTests.cs`). The domain review `_plan/refactor-2.1/review/wdc.md` (section 2.1) re-checked
all 85 rows against this baseline and refuted none as a defect; `_plan/refactor-2.1/review/wdc.verify.md` re-read U01-001, 015, 019,
021, 022, 027, 029 and 035 and confirmed them. I spot-checked every row below against the same code; nothing has changed
since. Line numbers are current where I re-read them; anchor edits by symbol anyway, because the review's own anchors were
often off (verifier general note).

Rows whose fix is fully written up as a domain finding (`_plan/refactor-2.1/findings/wdc.md`, `_plan/refactor-2.1/findings/winsvc.md`) get a
short section with a Coverage line. The rest get a full write-up.

Counts (ledger severities; the verifier changed none): 0 critical, 0 high, 9 medium, 37 low, 39 nit. Of these, 81 are
sections below (9 medium, 35 low, 37 nit) and 4 are no-change (U01-033, U01-040, U01-061, U01-082). The solution
checker added one nit in the same scope (U01-C-001) and corrected solutions in place; each correction says "checker".
Plan v2 parks no U01 row in B178.

Plan v2 batches for this area, in execution order: B001, B002, B046, B063, B064 (D4 decided: approved), B065, B066, B067,
B068 (config migration for the rotation rule), B069, B070, B071, B072. Child commits are pushed before each parent
gitlink commit.

Test commands used below:

- "WDC filter `X`" means
  `dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "X"`,
  repeated with `-f net10.0-windows10.0.19041.0` once B046 adds that target.
- "WSGM filter `X`" means `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "X"`.
- "M01-nn" is a row of the attended manual acceptance matrix (`manual-acceptance-matrix.md`): unit tests cannot prove
  WLAN, pairing or display behaviour (library AGENTS.md, Validation).

## Medium

### U01-001: Wi-Fi watch start/stop is not serialized; a replaced registration can outlive its rooted delegate

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:19-20` (`WifiWatchLock`,
  `_wifiWatch`), `:37-69` (`StartWifiWatch`), `:77-95` (`StopWifiWatch`, `DetachWatch`), `:231-257`
  (`WlanNotificationRegistration`); consumer `src/WSGM/Shell/RadioManager.cs` (feed start/stop, `_feedWork`).
- **Coverage:** confirmed by the verifier and fully covered by WDC-014: per-registration `IDisposable` from
  `StartWifiWatch`, no static slot, unregister outside any lock, delegate rooted until unregister completes. Two
  concurrent starts now create two independent registrations, so nothing is overwritten or unrooted.
- **Plan v2:** B063.
- **Related:** WDC-014, A01-F003, WINSVC-022, U01-007, U01-018, U01-056, U01-084.

### U01-002: A network with more than one saved profile is treated as ambiguous and can be neither joined nor forgotten

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:646-692` (`ReadScanFacts`:
  `conflictingIdentity` includes a differing `ProfileName`), `:595-640` (`ChooseInterface`, cross-adapter profile-name
  comparison at `:621-629`), `:133-137` (`ConnectWifi` throws on `Ambiguous`), `:362-366` (`ForgetWifi` throws),
  `:88-92` (`ListWifiNetworks` shows an ambiguous row as `Unsupported`, not connectable); `WindowsRadio.WifiRules.cs:89-117`
  (`FindFreeProfileName` creates `"<ssid> 2"`, the second profile that later triggers this), `:119` (`MergeNetworkFacts`).
- **Problem:** WLAN returns one available-network entry per saved profile. When one SSID has two profiles (a user
  profile plus one Windows or this library created under a suffixed name), the facts are marked ambiguous. The network
  then shows as unsupported in WSGM's list, `ConnectWifi` throws "More than one network advertises this display name",
  and `ForgetWifi` throws the same, so the user can neither join nor clean it up from WSGM.
- **Best solution:** identity is the network, not the profile. In B064, with the `WifiNetworkKey` of U01-016 (raw SSID
  bytes plus security class):
  - Remove the profile-name comparison from the conflict test in `ReadScanFacts`, `MergeNetworkFacts` and
    `ChooseInterface`. Entries with the same bytes and security class are one network whatever their profile names.
    Same bytes with different security are two rows with two keys, not an ambiguity. The `Ambiguous` flag and its two
    throws go away.
  - Profile selection for a join: the profile of the current connection when this network is the connected one, else
    the first matching profile in `WlanGetProfileList` order, which is Windows' own connection priority. No ranking of
    our own (checker: the earlier "ordinal-first name" was an invented order).
  - `ForgetWifi(WifiNetworkKey)` deletes every profile whose stored SSID bytes equal the key (the name set it already
    builds; no scan read is needed once the key carries the bytes) and returns one
    `WifiForgetResult(string ProfileName, uint Status)` per deletion instead of throwing on the first failure. Every
    deletion is attempted once. `RadioManager.ForgetAsync` shows today's "Could not forget {ssid}." when any result
    has a non-zero status and logs each status, so the visible text is unchanged.
  - `FindFreeProfileName` keeps its rule; a second profile it creates is no longer harmful.
  This beats keeping "ambiguous" and teaching WSGM to pick: the library already has the raw bytes, and only it can map a
  network to its profiles.
- **Tests:** new `WifiConnectTests` with the fake `IWlanApi` from B063/B064: two profiles with the same bytes give one
  connectable row; connect uses the connected profile, else the first matching name in profile-list order; forget
  deletes both and reports two results; one failed deletion still attempts the other. Rewrite `WindowsRadioTests.ConflictingAdapterFactsAreNotMergedIntoAConnectableCandidate` and
  `ExistingSsidAmbiguityCannotBeResetByLaterObservation` to the new rule (different bytes or security stay separate rows;
  different profile names merge). WDC filter `FullyQualifiedName~WifiConnectTests|FullyQualifiedName~WindowsRadioTests`;
  WSGM filter `FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~NativeQamNetwork`.
  Real-device Wi-Fi validation M01-21.
- **Plan v2:** B064 (decided: D4 approved; the library AGENTS.md edit that allows a typed result carrying the raw WLAN
  reason code is shown with B064 and applied).
- **Related:** U01-016, U01-013, U01-015; WDC-V-003 (the "More than one network..." literal leaves this path; WSGM's
  parity table keeps every remaining literal); wdc.verify R4.

### U01-003: DisplayModes.Apply can throw after the mode write, skipping readback and the one rollback

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:120-177` (`Apply`; the
  post-write `Find(path.Target)` at `:159`, also the unguarded `Find` at `:126` and `:152`), `:262-268` (`Find` calls
  `DisplayTopology.CaptureActive`, which throws `Win32Exception` when any active target is unreadable); compare `Read`
  `:50-67`, which already catches it.
- **Problem:** after `ChangeDisplaySettingsEx` has changed the mode, a hotplug or an unreadable unrelated target makes
  `Find` throw. The caller gets an exception, the new mode is neither confirmed nor rolled back, and the result type
  promised by `Apply` is never produced.
- **Best solution:** `Apply` never throws after the write. In B067:
  - With U01-030, `CaptureActive` skips unreadable unrelated paths, so `Find` throws only when the whole CCD query fails.
    Wrap each `Find` in `Apply` the way `Read` does: a `Win32Exception` before the write returns a typed refusal; after
    the write it means "route unverified".
  - Decision after the write is WDC-003's internal pure `Decide(status, routeSame, current, requested, original)`, in
    its order and nothing else: route gone or mode unreadable is `Unverified` (no rollback, no exception); current equals
    requested is `Confirmed` whatever the status; current equals original means no rollback (`Refused` with the status
    when it is non-zero, else `Unconfirmed` without rollback); any other mode on a still-verified route gets one
    rollback plus one readback. A `Win32Exception` from the post-write `Find` is the "route gone" input, never a throw.
    The typed shape lands in B069 (`DisplayModeResult`, WDC-010); B067 keeps today's record and only removes the throw
    and the refusal rollback.
  - No retry of the mode write in any branch.
  D9 does not reach this path: it removes readback machinery for vendor device writes, while this is Windows' own
  display state, which the library reads directly.
- **Tests:** the GDI calls sit outside the `IDisplayConfigApi` port, so per wdc.verify batch problem 7 there is no
  fake-backed `DisplayModes` test; the decision rule is covered for layouts by `DisplayApplyTests` and for modes by the
  attended display check M01-19 (notebook and IR desktop). WDC filter
  `FullyQualifiedName~DisplayApplyTests|FullyQualifiedName~DisplayTopologyTests`.
- **Plan v2:** B067.
- **Related:** U01-030, U01-031, U01-035; WDC-003, WDC-010.

### U01-004: Target identity decoding tests the wrong flag bit for EDID ID validity

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs:152-180` (`ReadTarget`,
  `DecodeEdidIds`); `external/windows-device-control/tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs`.
- **Coverage:** already fixed in the child working tree by W02_01 (`DecodeEdidIds` tests `1u << 2`, three tests including
  `ExistingPathIdentitySurvivesEdidPopulation`), but not committed. B001 normalizes the mixed line endings (WDC-012),
  commits and pushes the child and records the gitlink. No EDID migration code: stored identities are path-first, and
  Settings already refreshes matched identities (wdc.verify R3).
- **Plan v2:** B001.
- **Related:** A01-F001, WDC-012, WDC-V-004.

### U01-005: WindowsWakeSecurity.Restore stops at the first failing write and leaves later settings unrestored

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsWakeSecurity.cs:85-127` (`Restore`), the
  `WriteScheme` helper below it; consumer `src/WSGM/Core/LockScreenSettings.cs`.
- **Coverage:** fully covered by WDC-006: a pure `WakeSecurityRestorePlan` plus an executor that attempts every item,
  treats a vanished scheme as no longer applicable and returns the unresolved items, so WSGM retains only those instead
  of retrying a snapshot that can never complete. The returned items are writes that failed to dispatch, not entries
  waiting on a readback, so D9 leaves them as they are.
- **Plan v2:** B071.
- **Related:** WDC-006, WDC-024, U01-037, U01-075, U01-038.

### U01-006: The radio cache lock is held across an unbounded WinRT enumeration

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Power.cs:134-160` (`GetRadios`:
  `Radio.GetRadiosAsync().WaitWinRt()` at `:149` inside `lock (RadioCacheLock)`); `Interop.cs:94-97` (`WaitWinRt`).
- **Problem:** the cache exists because WinRT radio enumeration can stall, yet a cache miss runs that enumeration while
  holding `RadioCacheLock`. One stalled enumeration then blocks every other radio call, including `GetPower` callers
  that would have been served from a fresh cache.
- **Best solution:** check the cache under the lock; on a miss release the lock, enumerate, then take the lock again
  and publish the new array and timestamp (last writer wins; both are fresh). Two concurrent misses may enumerate twice,
  which is harmless. Add no timeout and no "Unknown on timeout" outcome: the defect is the lock scope, and a deadline
  would add a new outcome and cache state for a stall nobody has observed (review C6, simplify rule). The blocking
  nature of the call is documented under U01-011.
- **Tests:** none (WinRT `Radio` cannot be faked; the change is a lock-scope move). WDC filter
  `FullyQualifiedName~WindowsRadioTests` stays green; radio toggles are covered by M01-22.
- **Plan v2:** B065.
- **Related:** U01-011, U01-007.

### U01-007: WindowsRadio is a 2,978-line static aggregate with process-global feeds and no instance seam

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:19-20`;
  `WindowsRadio.Bluetooth.cs:26-34` (`BluetoothWatchLock`, `_bluetoothWatch`, pairing counters, `_connectedSelectors`);
  `WindowsRadio.Wifi.cs:13-19` (status client and saved-profile caches); `WindowsRadio.Power.cs:13` (radio cache).
- **Coverage:** the defect part (one Wi-Fi and one Bluetooth feed slot per process, which plugins share through
  host-first loading) is removed by WDC-014. The review rejected the "five instance services" remedy (C6): the remaining
  statics are identity-free performance caches and token-keyed pairing tables, which stay static. Testability comes from
  internal ports (U01-008), not from instances. `_connectedSelectors` is deleted in B065 (WDC-016).
- **Plan v2:** B063.
- **Related:** WDC-014, WDC-016, WINSVC-022, U01-001, U01-008, U01-039, U01-046.

### U01-008: The safety-critical native write and restore paths have no seam and no tests

- **Severity:** medium
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:116-331` (`ConnectWifi`
  profile write, verdict and rollback); `WindowsWakeSecurity.cs:36-127`; `ModernStandby.cs:258-330`;
  `DisplayModes.cs:120-177`; `DisplayLayouts.cs:235-340`; `WindowsPower.Actions.cs:30-57`;
  `external/windows-device-control/tests/WindowsDeviceControl.Tests/WakeSecurityTests.cs`. Only `IPowerRequestApi`
  (`WindowsPowerRequest.cs:154-161`) is injectable today.
- **Coverage:** covered by the seams the domain findings add, each with its own tests: `IPowerActionApi` (WDC-013, B002);
  the WLAN notification seam (WDC-014, B063) and `IWlanApi` for connect, profile and forget with `WifiConnectTests`
  (B064); `IDisplayConfigApi` with `DisplayApplyTests` limited to `DisplayLayouts` (B067); the pure
  `WakeSecurityRestorePlan` and executor (WDC-006, B071) and the existing `ModernStandby.RestorePlan` extended by U01-038
  (B071); pure pairing-table tests (U01-019, U01-020, B065); native layout pins (U01-043, B046). No instance services.
- **Plan v2:** B002, B046, B063, B064, B065, B067, B071.
- **Related:** WDC-006, WDC-013, WDC-014, U01-007, U01-041, U01-043, U01-044.

### U01-009: A power test runs the real suspend and restart paths, guarded only by a pre-cancelled token

- **Severity:** medium
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsPowerTests.cs:55-62`
  (`CancelledActionsNeverDispatch`); `external/windows-device-control/src/WindowsDeviceControl/WindowsPower.Actions.cs:30-57`.
- **Coverage:** fully covered by WDC-013: one internal `IPowerActionApi` port with a native instance, four fake-backed
  tests replace the test that could suspend or restart the machine running the gate.
- **Plan v2:** B002.
- **Related:** WDC-013, A01-F002, BUILD-001 (power half).

## Low

### U01-010: SetPower reports a partial application as an exception and loses refusals

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Power.cs:50-106` (`SetPower` and
  its XML); consumer `src/WSGM/Shell/RadioManager.cs:960-990` (`SetPower` call, `ApplyRadioResult`,
  `ReportCommandFailure`).
- **Problem:** the XML says it throws when no adapter accepted the state, but the code throws whenever any adapter
  threw, after other adapters already changed, and a refusal from another adapter is dropped in that case. The caller
  cannot tell "nothing changed" from "half changed".
- **Best solution:** `SetPower` returns
  `public sealed record RadioPowerResult(Access Access, IReadOnlyList<RadioAdapterResult> Adapters)` with
  `public readonly record struct RadioAdapterResult(string Name, Access? Access, int HResult)`: each adapter is attempted
  once and reports either its mapped `Access` or the exception's `HResult`. `Access` is `Allowed` only when every
  adapter allowed; otherwise the first refusal, or `Access.Unspecified` (the enum has no `Unknown`; checker correction)
  when an adapter failed without a refusal. A `RequestAccess` refusal before any write returns
  `new RadioPowerResult(access, [])` as today's early return. The method throws only for invalid arguments and for "no
  radio of the requested kind" (unchanged, documented). No readback decides success: the write status is the result.
  `RadioManager` keeps its two current texts: any failed adapter (`Access is null`) takes the `ReportCommandFailure`
  text with the adapter's HRESULT in the log line, otherwise `ApplyRadioResult(label, on, (int)result.Access)` as today.
- **Tests:** WDC: the pure fold from adapter results to `Access` (all allowed, one refused, one failed, mixed). WSGM:
  a `RadioManager` test that a failed adapter shows "Could not turn ...". WDC filter `FullyQualifiedName~WindowsRadioTests`;
  WSGM filter `FullyQualifiedName~RadioManager`.
- **Plan v2:** B065.
- **Related:** U01-006, U01-011.

### U01-011: Synchronous WinRT and WLAN waits have no timeout or cancellation across the public API

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/Interop.cs:94-104` (`WaitWinRt`); callers in
  `WindowsRadio.Bluetooth.cs` (`ListBluetoothDevices`, `ConnectedBluetoothCount`, `UnpairBluetooth`, `ReadEndpoint`),
  `CoreAudio.Spatial.cs` (`SetSpatialAudio`), `WindowsRadio.Wifi.cs:13,284` (the 25 s connect wait).
- **Problem:** these calls block their thread until Windows answers, with no caller token, and the public docs say
  nothing about it (the class remarks even call every member "synchronous and safe from any thread").
- **Best solution:** a caller token only where a consumer cancels: pairing (`PairBluetoothAsync`, U01-019). Every other
  blocking call stays synchronous and is documented in its XML and in the README threading section as "blocks until
  Windows answers; call it from a worker thread". WSGM already runs them on workers (`Task.Run` in `RadioManager`). No
  token overloads elsewhere: no consumer needs them, and each would be API churn plus a new cancellation outcome
  (review section 3.1).
- **Tests:** none (behaviour unchanged; docs).
- **Plan v2:** B065 (pairing token), B072 (docs).
- **Related:** U01-006, U01-019, U01-069.

### U01-012: Wi-Fi absence and exception contracts are inaccurate or missing

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:20-55` (`GetWifiStatus`),
  `:56-68` (`RequestWifiScan` documents `InvalidOperationException`, but `ForEachAdapter` rethrows the last
  `Win32Exception`), `:116` and `:347-354` (`ConnectWifi`, `ForgetWifi` exception docs);
  `WindowsRadio.WlanNative.cs:169-192` (`WlanClient.Interfaces` throws `InvalidOperationException` when Windows reports
  no interface); `WindowsRadio.cs:~170-174` (`WifiConnectionState.Unknown` documented as "no adapter"). Consumers:
  `src/WSGM/Shell/RadioManager.cs:581-595` (catches and logs), `src/WSGM/Shell/NativeQamNetworkService.cs:120` (does not
  catch).
- **Problem:** on a machine without a WLAN interface `GetWifiStatus` throws instead of returning the documented
  `Unknown`; the QAM network service lets that exception escape. Several Wi-Fi members document the wrong exception type
  or none.
- **Best solution:** in B064 split the interface read: `WlanClient.Interfaces()` returns a possibly empty list, and a new
  `RequireInterfaces()` throws today's `InvalidOperationException` when it is empty. `GetWifiStatus` uses `Interfaces()`
  and returns `new WifiStatus(WifiConnectionState.Unknown, 0, "")` when the list is empty; every other caller
  (`ConnectWifi`, `ForEachAdapter`, scan, disconnect, forget) uses `RequireInterfaces()`, so their behaviour is unchanged.
  Other open or enumeration failures still throw and are documented. In B072 the XML of `RequestWifiScan`, `ConnectWifi`,
  `ForgetWifi` and `ListWifiNetworks` lists the exceptions they actually throw. WSGM's tick already treats `Unknown` as
  "no Wi-Fi", so the UI is unchanged; only the "Wi-Fi status query unavailable" log line disappears on such machines.
- **Tests:** `WifiConnectTests` (fake `IWlanApi` with zero interfaces): `GetWifiStatus` returns `Unknown`;
  `RequestWifiScan` and `ConnectWifi` still throw `InvalidOperationException`. WDC filter `FullyQualifiedName~WifiConnectTests`;
  WSGM filter `FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~RadioManager`.
- **Plan v2:** B064 (code), B072 (XML).
- **Related:** U01-062, U01-074, U01-049; WINSVC-023 (the QAM service projects from `RadioManager` instead of polling).

### U01-013: ForgetWifi accepts an empty SSID and can delete the lone hidden network's profile

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:354-388` (`ForgetWifi`, no
  argument check; `ReadScanFacts` matches `network.Ssid == ""` for a hidden network and the bound profile is deleted).
- **Problem:** `ConnectWifi` rejects an empty SSID, `ForgetWifi` does not. An empty string matches a visible hidden
  network and deletes its saved profile, a destructive call on input that names nothing.
- **Best solution:** the B064 signature `ForgetWifi(WifiNetworkKey key)` makes this unrepresentable: the
  `WifiNetworkKey` constructor throws `ArgumentException` for empty SSID bytes and copies the bytes it is given. Because
  the key is a struct, `default(WifiNetworkKey)` exists; `ConnectWifi` and `ForgetWifi` throw the same
  `ArgumentException` for it (a type check, no new state). No string overload remains.
- **Tests:** `new WifiNetworkKey([], WifiSecurity.Open)` throws; `ForgetWifi(default)` throws before any WLAN call;
  `ForgetWifi` with the fake `IWlanApi` deletes only profiles whose SSID bytes equal the key. WDC filter `FullyQualifiedName~WifiConnectTests|FullyQualifiedName~WindowsRadioTests`.
- **Plan v2:** B064.
- **Related:** U01-016, U01-083.

### U01-014: ConnectWifi ignores a failed scan read and reports a misleading security error

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:646-649` (`ReadScanFacts`
  discards the status of `ReadAvailableNetworks`), `:138-143` and `:197-203` (the resulting "does not advertise a
  supported personal-key authentication method" / "authentication method is not supported" errors); compare
  `ListWifiNetworks` `:499`, which checks the same status.
- **Problem:** when the available-network list fails (location consent denied returns ERROR_ACCESS_DENIED on 24H2, or the
  service fails), the facts come back empty and `ConnectWifi` blames the network's security. The user sees a wrong reason
  and WSGM cannot show its consent guidance.
- **Best solution:** `ReadScanFacts` passes the status through `CheckWlan("WlanGetAvailableNetworkList", status)` like
  `ListWifiNetworks`, so a failed list throws `Win32Exception` with the WLAN status before any profile is written. A
  successful list with no entry for the key stays "not visible" and follows today's saved-profile and hidden-network path;
  "visible but unsupported" stays its own refusal. WSGM maps status 5 to the existing consent text through the same
  status check WDC-V-002 adds for scans, and every other failure keeps today's text through the WDC-V-003 parity table.
- **Tests:** `WifiConnectTests`: the fake list returns status 5, so `ConnectWifi` throws `Win32Exception` with
  `NativeErrorCode == 5` and makes no profile call. WSGM theory from WDC-V-002 extended to the connect path. WDC filter
  `FullyQualifiedName~WifiConnectTests`; WSGM filter `FullyQualifiedName~RadioManager`.
- **Plan v2:** B064.
- **Related:** WDC-V-002, WDC-V-003, U01-012, U01-074, U01-002.

### U01-015: ConnectWifi rolls back after an uncertain timeout but keeps the profile after a definite failure

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:150-154` (`Fail` rolls back
  on every exception), `:284-319` (definite failure restores only when the profile existed or the verdict is
  `KeyRejected`/`SecurityMismatch`; the 25 s timeout throws `Fail(new TimeoutException(...))`, which rolls back).
- **Problem:** after a timeout the attempt may still complete, yet the profile it is using is deleted or overwritten:
  an uncertain outcome is undone automatically. (The ledger also called keeping a newly created profile after a definite
  `Unreachable` failure "inverted"; checker: that is the documented, deliberate rule in the `ConnectWifi` remarks, so
  the user is not asked to retype a password that was never rejected, and it stays.)
- **Best solution:** in B064, `ConnectWifi(WifiNetworkKey, string?)` returns
  `WifiConnectResult(WifiConnectOutcome Outcome, uint ReasonCode)` with `Outcome { Joined, Failed, Pending, Refused }`
  (plus the `WifiConnectRefusal` of WDC-V-003 for `Refused`):
  - Joined: verdict success, or the byte check `IsConnectedTo` (unchanged).
  - Failed (definite failure reported by WLAN): today's restore rule, unchanged: a replaced profile gets its captured XML
    back once; a created profile is deleted once only on `KeyRejected` or `SecurityMismatch`. A rollback failure is
    thrown as today (`AggregateException` of the reason and the rollback failure, so WSGM's catch shows the same text)
    and is never retried.
  - Pending (verdict timed out and not connected): leave the profile in place, no rollback; the watch or the next status
    tick reports the real outcome. This is the "uncertain write is never retried or undone automatically" rule, and
    the only behaviour change of this finding.
  - Refused (validation before any mutation): no write happened.
  WSGM maps `Pending` to today's literal "The Wi-Fi connection attempt did not complete." and every other outcome to its
  current text through the WDC-V-003 table. This narrows B064's spec wording "definite refusal restores a created or
  replaced profile once" to today's documented rule; Codex follows this finding, not that phrase.
- **Tests:** `WifiConnectTests`: create plus `KeyRejected` deletes the new profile once; create plus `Unreachable` keeps
  it; replace plus any definite failure rewrites the old XML once; timeout returns `Pending` and makes no delete or set
  call; a rollback failure is thrown once and not retried. WDC filter `FullyQualifiedName~WifiConnectTests`; WSGM filter `FullyQualifiedName~RadioManager`.
  Real-device check M01-21.
- **Plan v2:** B064 (decided: D4 approved, guidance diff shown with the batch and applied).
- **Related:** U01-008, U01-011, U01-016; WDC-V-003.

### U01-016: The public Wi-Fi identity is the display string while the library's identity is raw SSID bytes

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.cs:259-275` (`WifiNetwork` carries
  only `Ssid` text); `WindowsRadio.Wifi.cs:494-544` (`MergeNetworks` merges by raw bytes), `:116`, `:354` (connect and
  forget take text). Consumers: `src/WSGM/Shell/RadioManager.cs` (connect and forget), `src/WSGM/Shell/RadioEntries.cs`,
  `src/WSGM/Overlay/RadioPanel.axaml.cs`, `src/WSGM/Shell/NativeQamNetworkService.cs`.
- **Problem:** two byte-distinct networks whose names decode to the same text are listed as two connectable rows, but
  connect and forget receive only the text and refuse both as ambiguous. The caller has no way to name the row it shows.
- **Best solution:** add `public readonly struct WifiNetworkKey : IEquatable<WifiNetworkKey>` holding a private copy of the
  SSID bytes and the `WifiSecurity` class, with value equality over both, plus `Hex` and `DisplayText` (wdc.verify R4: the
  security class is part of the key). `WifiNetwork` gains `WifiNetworkKey Key`; `WifiStatus` gains
  `WifiNetworkKey? Key`, null when not connected (the key cannot be empty, U01-013), built from the current
  connection's raw SSID and its security attributes through the same `ClassifySecurity` the list uses. `Ssid` stays the
  display text on both.
  `ConnectWifi(WifiNetworkKey, string?)` and `ForgetWifi(WifiNetworkKey)` replace the string forms; adapter choice stays
  internal. WSGM rows keep the key and pass it back; displayed names are unchanged. The only visible change is that two
  colliding names now both work (winsvc review note).
- **Tests:** key equality and hashing over bytes and security; merge produces one row per key; connect and forget use the
  row's key. WDC filter `FullyQualifiedName~WifiConnectTests|FullyQualifiedName~WindowsRadioTests`; WSGM filter
  `FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~NativeQamNetwork`.
- **Plan v2:** B064 (decided: D4 approved, guidance diff shown with the batch and applied).
- **Related:** U01-002, U01-013, U01-015, U01-049; wdc.verify R4.

### U01-017: Profile XML carries hex only for invalid UTF-8, while the docs promise exact bytes for non-ASCII names

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WifiProfile.cs:210-215` (`SsidElement`);
  `external/windows-device-control/docs/radios.md` (profile generation rules); library `AGENTS.md`.
- **Problem:** a valid UTF-8 non-ASCII SSID is written with a `<name>` element only, so Windows matches on its own
  encoding of the name rather than the exact bytes the docs promise. Joining such networks is unverified.
- **Best solution:** whenever the raw bytes are known, write both elements in the order Windows itself exports:
  `<hex>` followed by `<name>`. With no known bytes (a hidden network joined by name) write `<name>` only. `TryReadSsid`
  already prefers `<hex>`. This matches Windows-authored profiles, so it removes a special case instead of adding one.
- **Tests:** move the profile tests into a new WDC `WifiProfileTests` (U01-041) and add: an ASCII and a non-ASCII UTF-8
  SSID both produce `<hex>` and `<name>` and round-trip their bytes; empty raw bytes produce `<name>` only. WDC filter
  `FullyQualifiedName~WifiProfileTests`. Real-device join of a non-ASCII SSID in M01-21.
- **Plan v2:** B064 (code and tests), B072 (radios.md text).
- **Related:** U01-053, WDC-022.

### U01-018: Watch callbacks have undocumented reentrancy and deadlock hazards

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:22-36, 96-130`;
  `CoreAudio.Watch.cs` (`StartVolumeWatch`, `StartEndpointWatch` docs).
- **Coverage:** covered by WDC-014: each registration delivers under its own gate, and both Wi-Fi and Bluetooth docs state
  the callback thread and "do not dispose a Wi-Fi registration from inside its callback; post to your own thread
  first". No thread-identity detection. B072 carries the same rules into README and `docs/radios.md`.
- **Plan v2:** B063 (XML), B072 (README, radios.md).
- **Related:** WDC-014, U01-001, U01-022.

### U01-019: Pairing has no caller cancellation, a 180 s worst case, and completes deferrals only after cancellation is observed

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:26` (`PairingTimeout`
  90 s), `:366-454` (`PairBluetooth`; the `RequiredHandlerNotRegistered` second `Pair` at `:419-422`), `:518-534`
  (`Pair` creates a new 90 s timeout per call and completes deferrals only in the cancellation catch); consumer
  `src/WSGM/Shell/RadioManager.cs` (pairing delegate near `:60, :95-100`, `_pairingToken`).
- **Problem:** the docs promise one 90 s bound and attempt-local cancel, but there is no caller token, the fallback ceremony
  starts a second 90 s wait, and pending deferrals are completed only after cancellation was observed, which can leave the
  WinRT operation waiting on its own deferral.
- **Best solution:** replace `PairBluetooth(..., onFinished)` with
  `Task<PairingResult> PairBluetoothAsync(string deviceId, Action<PairingRequest> onRequest, CancellationToken cancellationToken)`.
  One linked `CancellationTokenSource` per attempt with `CancelAfter(90 s)` covers both `PairAsync` calls (wall-clock, not
  an active clock: WDC must not depend on the SDK clock). Register on that token a callback that first marks the attempt
  inactive and completes every pending deferral of the attempt (`CompletePendingPairings(attempt)`), so WinRT is never
  left waiting on a deferral when cancellation reaches it. The deadline throws `TimeoutException` with today's message,
  the caller token throws `OperationCanceledException`. `onFinished` and its both-null case disappear (U01-052).
  `RadioManager` awaits the task, writes `_pairingToken` on the UI post (WINSVC-021) and keeps today's texts through
  the pairing parity table (WDC-V-003). It cancels the token only in `Dispose`, where no text is shown; `CancelPairing`
  keeps declining the pending question exactly as today, so a user cancel still ends as Windows' `Cancelled` outcome
  with today's text instead of a new "operation was canceled" message (checker: cancelling through the token from
  `CancelPairing` would have changed the visible text).
- **Tests:** pure tests over the pending-pairing table: cancelling an attempt completes each pending deferral exactly
  once before removal; a late `RespondToPairing` for that token is harmless; one deadline spans both ceremonies (an
  internal overload takes the `PairAsync` call as a delegate and the deadline as a parameter, so the test uses a short
  deadline and a never-completing fake). WDC filter `FullyQualifiedName~PairingTests|FullyQualifiedName~WindowsRadioTests`;
  WSGM filter `FullyQualifiedName~RadioManager`. Real-device pairing M01-22.
- **Plan v2:** B065.
- **Related:** U01-011, U01-020, U01-052, U01-069; WDC-V-003, WINSVC-021, WINSVC-V-005.

### U01-020: Pairing callbacks are not contained: a throwing onRequest stalls the ceremony and onFinished errors vanish

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:387-411` (the
  `PairingRequested` handler calls `onRequest` after registering the token, with no catch), `:452` (`onFinished` inside a
  fire-and-forget `Task.Run`).
- **Problem:** a consumer exception in `onRequest` leaves the deferral open, so the ceremony stalls for 90 s and is
  reported as a timeout; an exception from `onFinished` is lost with the discarded task.
- **Best solution:** in `PairBluetoothAsync` (U01-019) wrap the `onRequest` call: on an exception remove the token,
  complete the deferral without accepting, store the exception and cancel the attempt, so the returned task faults with
  the consumer's exception instead of a timeout. `onFinished` no longer exists; the task carries the result or the
  failure.
- **Tests:** through the injected `PairAsync` delegate and the pending table: a throwing `onRequest` completes the deferral
  once and the task faults with that exception, not `TimeoutException`. WDC filter `FullyQualifiedName~PairingTests`.
- **Plan v2:** B065.
- **Related:** U01-019, U01-052.

### U01-021: StartVolumeWatch releases the device and volume RCWs twice when registration fails

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Watch.cs:70-98` (`StartVolumeWatch`:
  `registration.Dispose()` on a failed `Register`, then the `finally` releases the same `volume` and `device` again),
  `:163-207` (`VolumeWatch.Dispose`).
- **Problem:** confirmed by the verifier. A failed registration releases each RCW twice, which throws
  `InvalidComObjectException` or drops a reference another holder of the shared RCW still needs.
- **Best solution:** transfer ownership at construction: set `device = null; volume = null;` immediately after
  `new VolumeWatch(device, volume, onChanged)` and before `Register`. On failure `registration.Dispose()` releases them
  once and the `finally` sees nulls. No other change.
- **Tests:** none new (checker: `StartVolumeWatch` opens the real default endpoint through `OpenDefaultVolume`, and the
  `Release` helper ignores a managed fake because it is not a COM object, so a double release is not observable in a
  test). The change is two assignments moved above `Register`; existing WDC filter `FullyQualifiedName~CoreAudioTests`
  stays green.
- **Plan v2:** B063.
- **Related:** WDC-014, U01-022.

### U01-022: EndpointWatch.Dispose is unsynchronized, unlike VolumeWatch

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Watch.cs:210-256` (`EndpointWatch`,
  `private bool _disposed`, `Dispose`); consumer instance `src/WSGM` audio profile endpoint resolution (CONFIG-027).
- **Problem:** confirmed by the verifier. Two concurrent `Dispose` calls both unregister and both `Marshal.Release` the
  callback CCW.
- **Best solution:** `private int _disposed;` and `if (Interlocked.Exchange(ref _disposed, 1) != 0) return;` at the top of
  `Dispose`. No lock.
- **Tests:** `EndpointWatch` takes its enumerator in its constructor, so with `IMMDeviceEnumerator` internal (B063's
  visibility change) a managed fake enumerator counts `UnregisterEndpointNotificationCallback` calls: a double and a
  concurrent `Dispose` (two threads) unregister exactly once. WDC filter `FullyQualifiedName~CoreAudioTests`.
- **Plan v2:** B063.
- **Related:** WDC-014, U01-021, CONFIG-027.

### U01-023: ListSupportedDeviceFormats reports success with a truncated list when probes fail

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Formats.cs:177-196` (every non-zero
  `IsFormatSupported` verdict is treated as "unsupported", and the method returns 0).
- **Problem:** a device invalidated or an audio service failure in the middle of the probe yields a partial list reported
  as complete, which WSGM then offers as the device's formats.
- **Best solution:** extract the loop into an internal pure function over a verdict delegate. A verdict of `S_OK` adds
  the format. `AUDCLNT_E_DEVICE_INVALIDATED` (0x88890004) or `AUDCLNT_E_SERVICE_NOT_RUNNING` (0x88890010), the two
  documented "the device or the audio service went away" results, return that HRESULT at once with an empty list. Every
  other verdict skips the candidate exactly as today. The out list is assigned only on success (U01-062). The native
  call passes a NULL closest-match pointer (U01-059).
  Checker correction: the ledger's "any other HRESULT returns at once" would regress real endpoints. With exclusive mode
  turned off for a device, every probe answers `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` (0x8889000E), and some drivers
  answer `E_INVALIDARG` for individual unsupported layouts. Today that yields an empty or partial list and 0. As a
  failure it would make `AudioProfileService.ReadPlaybackCapabilities` return null, which also drops the spatial-audio
  controls and turns every playback-format apply into "no longer supported": a visible workflow change.
- **Tests:** a fake verdict sequence `[S_OK, S_FALSE, UNSUPPORTED, EXCLUSIVE_MODE_NOT_ALLOWED, E_INVALIDARG, S_OK]`
  returns two formats and 0; `[S_OK, AUDCLNT_E_DEVICE_INVALIDATED]` returns that HRESULT and an empty list. WDC filter `FullyQualifiedName~CoreAudioTests`;
  WSGM filter `FullyQualifiedName~AudioProfile|FullyQualifiedName~AudioManager`.
- **Plan v2:** B066.
- **Related:** U01-059, U01-062.

### U01-024: Cached native clients and feeds have no staleness or death signal

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Native.cs:15-38` (`_enumerator` cached
  for the process, never dropped); `WindowsRadio.Bluetooth.cs:159-197` (`StartBluetoothWatch` does not observe the
  watcher's `Stopped`); WLAN and endpoint registrations do not survive a service restart.
- **Problem:** after the audio service restarts, the cached enumerator keeps failing until the process exits; an aborted
  `DeviceWatcher` goes silent and the consumer keeps showing stale Bluetooth rows.
- **Best solution:** three small parts, no detector or restart machinery:
  - B066: where an enumerator call fails with `RPC_E_DISCONNECTED` (0x80010108), `RPC_S_SERVER_UNAVAILABLE` (0x800706BA)
    or `AUDCLNT_E_SERVICE_NOT_RUNNING` (0x88890010), clear the cache with
    `Interlocked.CompareExchange(ref _enumerator, null, failed)` and do not release it (checker correction): concurrent
    callers and every live `EndpointWatch`, which keeps the enumerator to unregister on `Dispose`, may still hold the
    same RCW, and `ReleaseComObject` on it would make their next call throw `InvalidComObjectException`, which
    `EndpointWatch.Dispose` does not catch. The GC releases it once nothing references it. The current call still
    returns its error; the next call creates a fresh enumerator through the existing lazy path.
  - B063 (WDC-014): the Bluetooth registration handles the watcher's `Stopped` event when it was not disposing and raises
    one terminal `BluetoothChangeKind.Stopped`. `RadioManager` disposes that registration and logs once; its existing
    start trigger creates the next one.
  - B072: document that WLAN and Core Audio registrations end with a service restart and a consumer re-registers when it
    chooses.
- **Tests:** WDC: the reset rule as a pure predicate over HRESULTs (the three codes reset, `E_INVALIDARG` does not). WSGM:
  `RadioManager` handles `Stopped` by disposing once. WDC filter `FullyQualifiedName~CoreAudioTests|FullyQualifiedName~WatchRegistrationTests`;
  WSGM filter `FullyQualifiedName~RadioManager`.
- **Plan v2:** B063 (watcher), B066 (enumerator), B072 (docs).
- **Related:** WDC-014, U01-001, U01-018.

### U01-025: TryReadBrightness always reports the AC level

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/Backlight.cs:46-64` (`buffer[0]` is
  `ucDisplayPolicy`, the code always returns `buffer[1]`, the AC level).
- **Problem:** when the driver reports a DC-only policy, the read returns the AC byte, so a battery-side change is
  misreported. Harmless on the reference device, where both levels track one slider (hypothesis rather than an observed
  bug).
- **Best solution:** pick the byte the driver's policy names: `DISPLAYPOLICY_DC` (2) returns `buffer[2]`; `DISPLAYPOLICY_AC`
  (1) and `DISPLAYPOLICY_BOTH` (3) return `buffer[1]` as today. No power-source lookup and no new heuristic. Replace the
  "reference device" comment with neutral wording (U01-066).
- **Tests:** extract `internal static byte LevelFor(ReadOnlySpan<byte> brightness)`; cases for policies 1, 2 and 3 in a
  new WDC `BacklightTests`. WDC filter `FullyQualifiedName~BacklightTests`; B066's listed filter
  (`CoreAudioTests|WaveOutTests`) does not select it, so add it to that batch's command.
- **Plan v2:** B066.
- **Related:** U01-066.

### U01-026: WaveOutFeedback frees buffers the driver may still hold on a second Dispose

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WaveOutFeedback.cs:26-62` (`Dispose`: a failed
  reset, unprepare or close keeps the buffers but zeroes `_output`, so a second `Dispose` sees "released" and frees them);
  consumer `src/WSGM/Shell/VolumeFeedback.cs`.
- **Problem:** after a device disconnects mid-playback, the deliberate leak is undone by any later `Dispose`, handing the
  driver freed heap.
- **Best solution:** make the leak final by forgetting the pointers: when `driverReleasedBuffers` is false, set
  `_header = 0` and `_samples = 0` and return. A later `Dispose` then has nothing to free. No new field and no lock: the
  type is documented as single-owner and not thread-safe, which matches its only consumer (review C28).
- **Tests:** none new (checker: extracting a three-way `== 0` conjunction to test it would add a seam with nothing to
  prove; the fix is two assignments). The single-bit `IsQueued` test is replaced as WDC-018 says. WDC filter
  `FullyQualifiedName~WaveOut|FullyQualifiedName~CoreAudioTests`.
- **Plan v2:** B066.
- **Related:** U01-081, WDC-018.

### U01-027: The already-active check ignores rotation, so rotation-only changes are never written

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayLayouts.cs:378-399` (`Matches`), `:410-420`
  (`Confirm`); `DisplayLayoutPlanner.cs` (`Plan` writes rotation); `src/WSGM/Settings/DisplayLayoutEditor.cs` (`ToOutput`).
- **Coverage:** confirmed by the verifier as a library defect, and fully covered by WDC-V-001, which makes it safe to
  land: `Rotation = 0` means "keep the display's current rotation" in the planner, `Matches` and `Confirm`;
  `DisplayLayoutEditor.ToOutput` emits 0; a non-zero rotation is compared and written. The B068 migration rewrites stored
  1 to 0 in `GameLayout` and `DesktopLayout` only; `PendingReturnLayout` keeps its captured rotation.
- **Plan v2:** B067 (B068 migration).
- **Related:** WDC-V-001, U01-063.

### U01-028: Layout apply and rollback always persist to the Windows display database

- **Severity:** low (the review refuted it as a defect, C23; only documentation remains)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayLayouts.cs:318-326` (`Run`:
  `SdcApply | SaveToDatabase` for apply and rollback); WSGM recovery `src/WSGM/Core/GameModeLaunchConfiguration.cs:176`
  (`PendingReturnLayout`), `src/WSGM/Shell/GameModeReturnRecovery.cs`.
- **Problem:** the behaviour is right for the only caller (Game Mode layouts and their recovery are built on persisted
  layouts, so a crash or reboot leaves a layout WSGM can restore), but the library never says it persists, so a reader
  of the API cannot know.
- **Best solution:** keep `SDC_SAVE_TO_DATABASE` fixed. Document on `DisplayLayouts.Apply` and in the README display
  section: "Apply and its rollback are saved to the Windows display database, so the arrangement survives a reboot; a
  caller that needs to undo it applies the captured layout." No persistence parameter: there is one caller and one
  behaviour to select.
- **Tests:** none (docs).
- **Plan v2:** B072.
- **Related:** U01-032, U01-035.

### U01-029: The display waits abort on a transient query error, cap at ten minutes, and are duplicated in WSGM

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs:38-67`
  (`WaitForPresentAsync`, `WaitForAvailableAsync`), `:223-249` (`PollAsync`, ten-minute cap at `:226`);
  `src/WSGM/Shell/DisplayArrivalWaiter.cs`; `tests/WSGM.Tests/Shell/DisplayArrivalWaiterTests.cs`.
- **Coverage:** confirmed by the verifier and covered by WINSVC-028 plus review C22: WSGM's settle waiter moves into WDC
  as `DisplayLayouts.WaitForTargetsAsync`, settle interval and backstop are caller parameters (WSGM passes 500 ms and
  5 s), `Win32Exception` counts as "not settled", and the library waits, `PollAsync`, `DisplayWaitOutcome` and the test
  that could reach native code are deleted.
- **Plan v2:** B070.
- **Related:** WINSVC-028, A01-F006, U01-040, U01-063.

### U01-030: CaptureActive and TryFindActive fail completely when any active target is unreadable

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs:19-31` (`CaptureActive` reads
  every target and throws), `:84-109` (`TryFindActive`, comment "an unreadable display still fails the lookup");
  consumers `DisplayModes.Find`, `DisplayScaling`, `DisplayColor`, `src/WSGM/Core/DisplayScale.cs:271`,
  `src/WSGM/Overlay/DisplayModeView.cs:33`, `src/WSGM.Plugin.NvidiaGpu/NvApi.cs:343`.
- **Problem:** one unreadable target (Miracast, indirect or virtual display) makes every snapshot, mode, scaling and HDR
  lookup fail, although `Observe` already tolerates it.
- **Best solution:** in B067:
  - `CaptureActive` reads each path's target identity and source name inside one `try`/`catch (Win32Exception)` and
    skips a path when either read fails, returning the readable active paths. A failed CCD query still throws.
  - `TryFindActive` skips unreadable candidates. When nothing readable matches and an unreadable candidate sits on the
    stored route of the requested identity (same adapter LUID and target id), it reports an internal `Unreadable` result
    instead of "not active"; B069 surfaces that as the `Unreadable` outcome of the typed set results (WDC-004).
  - No new matching heuristic.
  The NVIDIA plugin now receives fewer paths instead of an exception; the GPU domain confirms its handling in B069 when the
  plugin is rebuilt.
- **Tests:** `DisplayApplyTests`/`DisplayTopologyTests` through the fake `IDisplayConfigApi`: one unreadable unrelated path
  is skipped; an unreadable path on the target's route yields `Unreadable`. WDC filter
  `FullyQualifiedName~DisplayTopologyTests|FullyQualifiedName~DisplayApplyTests`. Hardware M01-19.
- **Plan v2:** B067.
- **Related:** U01-003, U01-029, U01-064; WDC-004.

### U01-031: DisplayModes uses -2 as its own failure code, which is DISP_CHANGE_BADMODE

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:129, 135, 154, 175, 247`;
  `DisplayTopology.Types.cs:59-70` (`DisplayProfileResult.NativeStatus` documented as a SetDisplayConfig status but
  carrying `ChangeDisplaySettingsEx` codes).
- **Coverage:** covered by WDC-004 and WDC-010: `DisplayProfileResult` becomes
  `DisplayModeResult(DisplayModeOutcome Outcome, int NativeStatus, bool RollbackAttempted, bool RollbackSucceeded)`, the
  outcome enum carries the library's own refusals, `NativeStatus` carries only real `DISP_CHANGE_*` values and is
  documented as such. `ApplyPrimaryModeTransient` keeps returning the raw `ChangeDisplaySettingsEx` status and returns
  `DISP_CHANGE_FAILED` (-1) instead of -2 when the current mode cannot be read, documented; its one caller
  (`src/WSGM/Core/DisplayProfiles.cs:250`) only logs the value.
- **Plan v2:** B069.
- **Related:** WDC-004, WDC-010, U01-003, U01-074.

### U01-032: ApplyPrimaryModeTransient documents that exit or a crash restores the saved mode

- **Severity:** low (hypothesis)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:226-241` (remark "exit, a crash or
  a reboot restores the user's saved configuration"); the same claim in `src/WSGM/Core/DisplayProfiles.cs:184-187`
  (`TryApplyTransientRefreshRate`); caller `src/WSGM/Core/RefreshRatePairingService.cs`.
- **Problem:** a dynamic `ChangeDisplaySettingsEx` with flags 0 is not written to the registry, but it is expected to stay
  in force after the process exits, until a display reset, sign-out or reboot. Only reboot is a certain restore. If so,
  a WSGM crash during a refresh-rate pairing leaves the paired rate in place, and both docs mislead whoever relies on them.
- **Best solution:** settle the fact once, then fix the words. In the attended matrix (M01-19) end WSGM forcibly while a
  transient refresh rate is applied and record whether the rate reverts. Then rewrite both remarks to the observed
  behaviour, for example "Not saved to the registry: the mode stays until Windows resets the display (sign-out, reboot or
  another mode change); process exit does not restore it." Keep flags 0; do not switch to `CDS_FULLSCREEN`, which would
  change today's behaviour. If the rate survives a crash, file that as a separate session-domain finding rather than
  adding recovery here.
- **Tests:** none (docs plus one attended observation).
- **Plan v2:** B069 (both files are in its list).
- **Related:** U01-028.

### U01-034: DisplayEdid.ReadModes dereferences a possibly null monitor

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayEdid.cs:31-42` (`FromInterfaceIdAsync` may
  return null; `GetDescriptor` is called without a check); consumer `src/WSGM/Settings/SettingsViewModel.cs:263`.
- **Coverage:** fully covered by WDC-015: `ReadModes` returns `DisplayEdidModes(Modes, Status)` with
  `DisplayEdidStatus { Read, NotFound, TimedOut, NoDescriptor }`; a null monitor is `NotFound`.
- **Plan v2:** B067.
- **Related:** WDC-015.

### U01-035: Display mutations have no shared serialization

- **Severity:** low (hypothesis; verifier re-read and confirmed)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayModes.cs:45` (the only gate);
  `DisplayLayouts.cs:235-340`, `DisplayScaling.cs`, `DisplayColor.cs` (no gate); NVIDIA plugin HDR writes through the
  host-first WDC assembly.
- **Coverage:** fully covered by WDC-021: one `internal static readonly object WriteGate` in `DisplayTopology` taken only
  by writes (mode apply, transient primary mode, layout apply, scaling set, HDR set); reads take no gate. Static, because
  CCD state is machine-global and the plugin shares the assembly (review C16).
- **Plan v2:** B067.
- **Related:** WDC-021, U01-003, U01-028.

### U01-036: User-facing English wording is produced inside the library

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayLayoutPlanner.cs:20-66` (`Describe`),
  `DisplayModes.cs` (`Apply` details), `DisplayScaling.cs`, `DisplayColor.cs`, `DisplayLayouts.cs` (details and
  warnings), `PowerRequestList.cs:59-90` (`Query` error text), `CoreAudio.cs:256` ("Audio device"), Wi-Fi and pairing
  exception texts.
- **Coverage:** covered by WDC-023 (display codes in WDC, exact literals in a new WSGM `DisplayText.cs` with a table test),
  WDC-V-003 (Wi-Fi, scan and pairing parity tables), WDC-V-005 (`AudioEndpoint.Name` may be empty; WSGM applies "Audio
  device" in the views and at the capture points) and the typed `PowerRequestList` status worded by WSGM.
- **Plan v2:** B066 (audio name), B069 (display), B071 (request list); radio text in B064 and B065.
- **Related:** WDC-023, WDC-V-003, WDC-V-005, U01-063, U01-080.

### U01-037: DisableSignIn bundles three separate policies into one call

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsWakeSecurity.cs:65-82` (`DisableSignIn`);
  `src/WSGM/Core/LockScreenSettings.cs`.
- **Coverage:** fully covered by WDC-006: `SetConsoleLockPolicy(int ac, int dc)`, `SetSchemeConsoleLock(Guid, int ac,
  int dc)`, `SetNoLockScreen(int)` and public `EnumerateSchemes()`; WSGM composes them in today's order, so nothing
  visible changes.
- **Plan v2:** B071.
- **Related:** WDC-006, U01-005.

### U01-038: RestoreWakeDevices aborts on the first failing write

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/ModernStandby.cs:320-330`
  (`RestoreWakeDevices`), `:258-292` (`TrySetWakeArmed` re-enumerates every device and `Check` throws on a failed
  `DevicePowerSetDeviceState`), `:333` (`RestorePlan`).
- **Problem:** one device whose write fails throws out of the loop, so later devices keep the changed wake arming. Each
  item also re-enumerates the whole device list.
- **Best solution:** `RestoreWakeDevices` takes `WakeDeviceGate` once, enumerates once, builds the existing
  `RestorePlan`, and for each item looks the device up in that enumeration, skips non-programmable or absent devices,
  writes through an internal `SetArmedCore(name, armed)` (the `DevicePowerSetDeviceState` call without re-enumeration),
  and catches the `Win32Exception` per item. It returns `IReadOnlyList<WakeDeviceRestoreFailure>` (name, native error);
  empty means everything restorable was restored. Each write is attempted once. Public `TrySetWakeArmed` keeps its own
  re-read, because it is a single standalone write.
- **Tests:** extend `ModernStandbyTests.RestorePlan`; an executor test with a fake write delegate: a failure on the first
  device still attempts the rest and reports one failure. WDC filter `FullyQualifiedName~ModernStandbyTests`.
- **Plan v2:** B071.
- **Related:** U01-005, U01-076, WDC-007.

### U01-039: Power notification registrations are raw handles with manual unregister

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsPower.Notifications.cs:10-46`
  (`Register*Notification` return `nint`, `Unregister*`); consumer `src/WSGM/Interop/MessageWindow.cs:276-290, 349-381,
  455-470`.
- **Problem:** the caller must pair each handle with the matching unregister function and read
  `GetLastPInvokeError` itself; a missed or mismatched unregister leaks a registration for the process lifetime.
- **Best solution:** `public sealed class PowerNotificationRegistration : SafeHandleZeroOrMinusOneIsInvalid` whose
  `ReleaseHandle` calls the unregister function that matches how it was created (two private factory paths set a private
  kind). `RegisterSettingNotification` and `RegisterSuspendResumeNotification` return it and throw `Win32Exception` with
  the captured error on failure; the two `Unregister*` methods are removed. `MessageWindow` holds
  `PowerNotificationRegistration?` fields, catches the exception and logs the same warning text with
  `ex.NativeErrorCode`, and disposes to unregister. The "Unregister... failed" warning disappears, because a SafeHandle
  release has no failure channel and nothing could act on it.
- **Tests:** none automated (registration needs a real window and there are no `MessageWindow` tests). Manual: after
  the change wsgm.log still shows "Suspend/resume notifications registered" and the display-state line at startup, and a
  sleep/resume plus an AC/DC switch are still handled.
- **Plan v2:** B071 (MessageWindow owner change coordinated with the session domain).
- **Related:** U01-007.

### U01-041: Library contract tests live in WSGM's test project

- **Severity:** low
- **Where:** `tests/WSGM.Tests/Shell/RadioManagerTests.cs:91-185` (`AggregatePower`, `GetReasonVerdict`, `WifiProfile`
  shapes and `PassphraseIsValid`); `tests/WSGM.Tests/Shell/AudioManagerTests.cs:28-37`
  (`InvalidEndpointFlowsFailWithoutCallingCom` tests `CoreAudio.ListEndpoints`);
  `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsRadioTests.cs`.
- **Problem:** WDC contracts are tested only in WSGM, so the library's own suite does not guard them and the library's
  docs point at tests it does not have.
- **Best solution:** move those cases unchanged: radio cases into WDC `WindowsRadioTests`, profile cases into a new WDC
  `WifiProfileTests`, the endpoint case into WDC `CoreAudioTests`; delete them from the WSGM files in the same parent
  commit as the gitlink. Keep the WSGM tests that test WSGM wording. B046's file list names only
  `RadioManagerTests.cs`; `tests/WSGM.Tests/Shell/AudioManagerTests.cs` (the `InvalidEndpointFlowsFailWithoutCallingCom`
  deletion) belongs to the same commit.
- **Tests:** full WDC suite on net8 and net10; WSGM filter `FullyQualifiedName~RadioManagerTests|FullyQualifiedName~AudioManagerTests`.
- **Plan v2:** B046.
- **Related:** U01-008, U01-044, U01-017.

### U01-042: The test project targets net8.0 only

- **Severity:** low
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsDeviceControl.Tests.csproj:4`.
- **Problem:** the library ships net8.0 and net10.0 and WSGM consumes net10.0, which the library suite never runs.
- **Best solution:** `<TargetFrameworks>net8.0-windows10.0.19041.0;net10.0-windows10.0.19041.0</TargetFrameworks>`
  replacing `<TargetFramework>`, and remove the redundant `SupportedOSPlatformVersion` (it restates the TFM's platform
  version). Every later WDC batch runs its filter on both.
- **Tests:** full WDC suite with `-f net8.0-windows10.0.19041.0` and `-f net10.0-windows10.0.19041.0`.
- **Plan v2:** B046.
- **Related:** U01-045.

### U01-043: Documented native-layout regressions have no regression tests because the structs are private

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WlanNative.cs:197-307` (WLAN
  structs, `WLAN_AVAILABLE_NETWORK` 628 bytes); `CoreAudio.Native.cs:117-138` (PROPVARIANT, 24 bytes on x64);
  `DisplayTopology.Native.cs:89-161` (`PathInfo`, `TargetDeviceName`); `WaveOutFeedback.cs` (`WaveHeader`).
- **Problem:** layouts that once regressed (per the library's own notes) are unpinned; only `ModeInfo` and the wave
  formats have size tests.
- **Best solution:** make those structs `internal` (visible to the test assembly through the existing
  `InternalsVisibleTo`) and add `NativeLayoutTests` that assert `Marshal.SizeOf` and `Marshal.OffsetOf` of the fields the
  code reads against the Windows SDK header values written in the test, and decode a hand-written byte buffer for the
  WLAN available-network record. The numbers come from the SDK, not from the source, so the test is independent.
- **Tests:** `NativeLayoutTests`; WDC filter `FullyQualifiedName~NativeLayoutTests` on both targets (x64 process).
- **Plan v2:** B046.
- **Related:** U01-004, U01-008, U01-058, U01-081.

### U01-044: Several tests are single-value or cannot detect the behaviour they are named for

- **Severity:** low
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsRadioTests.cs:10-31, 78-96,
  134-139`; `CoreAudioTests.cs:161-166`.
- **Coverage:** covered by WDC-018 (with the verifier's corrected anchors and its R5 correction: the pairing test does
  not mutate the table, it only lacks an assertion). The aggregation tests gain all-class coverage in B065, the
  conflict test is rewritten by U01-002 in B064, pending-deferral tests come with U01-019.
- **Plan v2:** B046 (removals), B064, B065 (replacements).
- **Related:** WDC-018, WDC-026, U01-002, U01-041.

### U01-045: The library's build depends on the checkout it sits in

- **Severity:** low
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj:1-34`; parent
  `Directory.Build.props` and `.editorconfig` (inherited in-tree only).
- **Problem:** in-tree the library inherits WSGM's `EnforceCodeStyleInBuild`, `NuGetAudit` and style rules; standalone it
  does not, so the same commit builds differently in the two places.
- **Best solution:** add `external/windows-device-control/Directory.Build.props` that does not import the parent, and
  restate exactly what the library relies on today: `EnforceCodeStyleInBuild`, `Nullable`, documentation generation and
  `NuGetAudit=false`, so in-tree diagnostics stay identical (wdc.verify batch problem 2). Add a child `.editorconfig`
  with `root = true` that copies only parent rules the library already meets, so no new warning appears.
- **Tests:** build the library and run the full WDC suite in-tree and from a standalone clone of the child (both
  frameworks); no new warnings.
- **Plan v2:** B046.
- **Related:** U01-042, U01-067, BUILD-012, WDC-020.

### U01-046: The package version never changes, while the plugin host shares assemblies by identity

- **Severity:** low (hypothesis)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj:38-40` (`Version`
  0.1.0); `src/WSGM/Core/PluginPackageLoader.cs:205-224` (`PluginLoadContext` resolves WDC host-first).
- **Problem:** every revision is 0.1.0, so a plugin built against another library commit binds to WSGM's copy with no
  signal and fails later with `MissingMethodException`.
- **Best solution:** set `Version` to 0.2.0 in B063, the first breaking batch, and keep it for the rest of this refactor
  (one release). Host-first loading stays; plugins do not carry their own copy. Every batch that changes a member a
  bundled plugin calls rebuilds that plugin in the same batch (the NVIDIA package in B069, which changes `TrySetHdr`).
- **Tests:** none; the B069 plugin rebuild runs `dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj`.
- **Plan v2:** B063 (version), B069 (plugin rebuild).
- **Related:** U01-007, WDC-004, wdc.verify batch problem 8.

## Nit

### U01-047: Rethrowing a stored exception loses its stack trace

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:417` (`ForEachAdapter`:
  `throw last;`); `CoreAudio.Bluetooth.cs:97` (`throw last ?? new InvalidOperationException(...)`).
- **Problem:** `throw last` resets the original stack trace, so logs point at the rethrow, not the failing call.
- **Best solution:** `ExceptionDispatchInfo.Throw(last)` at both sites (keep the `InvalidOperationException` branch for
  the no-exception case).
- **Tests:** none needed.
- **Plan v2:** B064 (Wi-Fi), B065 (Core Audio Bluetooth). Checker: plan v2's mapping says B066, but
  `CoreAudio.Bluetooth.cs` is in B065's file list (the container normalizer) and in no B066 list.
- **Related:** U01-012.

### U01-048: Dead code and unreachable branches

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:235` (`profileName ??= ssid`
  after both branches already set it), `:619` (`best ?? ...` when the ranking always sets `best`);
  `WindowsRadio.WifiRules.cs:105-109` (the unreadable-XML refusal inside `FindFreeProfileName`); `WindowsStorage.cs:46`
  (unused `OpenExisting`; the code uses `Kernel32.OpenExisting`), `:85-86` (redundant exception filter);
  `CoreAudio.Spatial.cs:219-230` (the capture arm of `ToWinRtDeviceId`, reached only by a test).
- **Problem:** unreachable code hides the real control flow and keeps a test-only branch in shipping code.
- **Best solution:** delete the two Wi-Fi fallbacks; B064 keeps exactly one unreadable-XML refusal, made before any
  mutation, and deletes the other; delete the storage constant and simplify the filter; remove the `direction` parameter
  of `ToWinRtDeviceId` with its capture arm and the test line that calls it (`CoreAudioTests.cs:82`).
- **Tests:** existing suites stay green. WDC filter `FullyQualifiedName~WindowsRadioTests|FullyQualifiedName~CoreAudioTests`.
- **Plan v2:** B064 (Wi-Fi), B066 (spatial), B071 (storage).
- **Related:** U01-040.

### U01-049: WifiNetwork.Ssid documents hidden networks that the list never returns

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.cs:260`; `WindowsRadio.Wifi.cs`
  (`MergeNetworks` skips empty SSIDs).
- **Problem:** the doc says `Ssid` is empty for a hidden network, but such networks are never listed.
- **Best solution:** "The network name as text. Never empty: networks that hide their name are not listed." Add the
  same rule to the `Key` doc.
- **Tests:** none.
- **Plan v2:** B064.
- **Related:** U01-016.

### U01-050: PairingKind.Unknown tells consumers to reject, while WSGM accepts it

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.cs:94-95`;
  `src/WSGM/Overlay/RadioPanel.axaml.cs:~301-302`; `external/windows-device-control/docs/radios.md:45-47`.
- **Problem:** the library tells consumers what to do with an unclassified ceremony, and its own docs and WSGM do the
  opposite.
- **Best solution:** neutral wording: "A ceremony this library does not classify. Windows still waits for an answer; the
  caller decides whether to accept or reject it." Align `radios.md`. WSGM unchanged.
- **Tests:** none.
- **Plan v2:** B065 (XML), B072 (radios.md).
- **Related:** U01-036.

### U01-051: RespondToPairing and UnpairBluetooth accept invalid input silently and hide status

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:468-492`
  (`RespondToPairing`), `:501-507` (`UnpairBluetooth`); consumer `src/WSGM/Shell/RadioManager.cs:1200-1225`.
- **Problem:** `ProvidePin` with an empty PIN accepts without one, and a PIN is forwarded to every ceremony;
  `UnpairBluetooth` validates nothing and its `bool` drops the unpairing status.
- **Best solution:** the PIN rule is WDC-008 (look up first, require a PIN only for `ProvidePin`, pass it only there).
  `UnpairBluetooth(string deviceId)` calls `ArgumentException.ThrowIfNullOrEmpty(deviceId)` and returns
  `public readonly record struct BluetoothUnpairResult(bool Unpaired, int NativeStatus)` (`Unpaired` for `Unpaired` or
  `AlreadyUnpaired`, `NativeStatus` the raw `DeviceUnpairingResultStatus`). `RadioManager` uses `.Unpaired` exactly as it
  uses the bool today and logs `NativeStatus` on failure.
- **Tests:** `RespondToPairing` cases from WDC-008; the status mapping as a pure function over the three statuses. WDC
  filter `FullyQualifiedName~PairingTests|FullyQualifiedName~WindowsRadioTests`; WSGM filter `FullyQualifiedName~RadioManager`.
- **Plan v2:** B065.
- **Related:** WDC-008, U01-020, WINSVC-V-005.

### U01-052: The onFinished both-null case is unreachable

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Bluetooth.cs:356-358`.
- **Problem:** the doc describes an "abandoned" case with both arguments null that the code never produces.
- **Best solution:** removed with `onFinished` by `PairBluetoothAsync` (U01-019); the task's result or exception is the
  only outcome.
- **Tests:** none beyond U01-019.
- **Plan v2:** B065.
- **Related:** U01-019, U01-020.

### U01-053: WifiProfile documents a sink that does not exist and throws NullReferenceException for null

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WifiProfile.cs:48-51, 87-90` (XML says the result
  is "ready for ConnectWifi"), `:194-203` (`PassphraseIsValid(null)` throws `NullReferenceException`).
- **Problem:** no public API accepts profile XML, and a null argument fails with the wrong exception.
- **Best solution:** keep the type public (review inventory: tests and outside consumers use it). Docs say "profile XML
  in the form this library writes through WlanSetProfile". `PassphraseIsValid` starts with
  `ArgumentNullException.ThrowIfNull(passphrase)`.
- **Tests:** `PassphraseIsValid(null!)` throws `ArgumentNullException`, in the new `WifiProfileTests`. WDC filter
  `FullyQualifiedName~WifiProfileTests`.
- **Plan v2:** B064.
- **Related:** U01-017, U01-040.

### U01-054: A public XML doc references a private exception type

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiRules.cs:36-40`
  (`GetReasonVerdict` cites private `WlanReasonException`).
- **Problem:** consumers see a `Win32Exception`; the cref names a type they cannot use.
- **Best solution:** "A WLAN reason code, as carried by `WifiConnectResult.ReasonCode` or by
  `Win32Exception.NativeErrorCode` after a profile write failed."
- **Tests:** none.
- **Plan v2:** B064.
- **Related:** U01-074.

### U01-055: The Wi-Fi feed subscribes to MSM notifications and drops them all

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:51-55` (registers
  `Acm | Msm`, falls back to ACM), `:107-111` (drops every non-ACM notification after the lock and the struct copy).
- **Problem:** every MSM notification costs a callback, a lock and a marshal for nothing.
- **Best solution:** register `WlanNotificationSourceAcm` only, as `ConnectionVerdict` already does (`:161`), and delete
  the fallback branch and the `WlanNotificationSourceMsm` constant.
- **Tests:** `WatchRegistrationTests` asserts the registration requests ACM only.
- **Plan v2:** B063.
- **Related:** WDC-014, U01-060.

### U01-056: WLAN handle owners have non-idempotent Dispose and no SafeHandle

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WlanNative.cs:144-167`
  (`WlanClient.Dispose` closes the handle every call); `WindowsRadio.WifiWatch.cs:248-257`
  (`WlanNotificationRegistration.Dispose`).
- **Problem:** a second `Dispose` closes the same raw handle value again, which may by then belong to another client.
- **Best solution:** `WlanClient` gets `private int _closed` and closes only on the first
  `Interlocked.Exchange(ref _closed, 1) == 0`; the registration owner does the same (WDC-014). No SafeHandle finalizer:
  a WLAN handle closed on the finalizer thread would also drop live notification registrations at an arbitrary time,
  and every owner is disposed deterministically.
- **Tests:** `WatchRegistrationTests`: double dispose unregisters and closes once.
- **Plan v2:** B063.
- **Related:** A01-F005, WDC-014, U01-001.

### U01-057: WlanGetProfile's in/out flags argument is declared out

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WlanNative.cs:85-93`
  (`out uint flags`).
- **Problem:** `pdwFlags` is in/out. Declared `out`, the call relies on the marshaller passing zero so it never asks for
  `WLAN_PROFILE_GET_PLAINTEXT_KEY`, which would put a plaintext key in the parsed-profile cache.
- **Best solution:** declare `ref uint flags` and initialize it to 0 at each call site.
- **Tests:** none (declaration).
- **Plan v2:** B064.
- **Related:** U01-058, U01-043.

### U01-058: REASON_CONTEXT is declared with only its simple-string arm

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsPowerRequest.cs:203-208`
  (`ReasonContext` is 16 bytes managed, 32 native on x64).
- **Problem:** benign while only the simple string is used, but the managed struct is smaller than the native one.
- **Best solution:** declare the detailed arm, which is the larger union member, and write the reason string into its
  first field (same offset as `SimpleReasonString`):
  `uint Version; uint Flags; nint LocalizedReasonModuleOrSimpleString; uint LocalizedReasonId; uint ReasonStringCount; nint ReasonStrings;`
  That gives the native size on both bitnesses (32 bytes x64, 24 x86) without explicit offsets.
- **Tests:** `NativeLayoutTests` pins `Marshal.SizeOf<ReasonContext>()` at 32 on x64 (make it internal). B071's listed
  filter does not select `NativeLayoutTests`; add `|FullyQualifiedName~NativeLayoutTests` to that batch's command.
- **Plan v2:** B071.
- **Related:** U01-043.

### U01-059: IsFormatSupported is called in exclusive mode with a non-null closest-match pointer

- **Severity:** nit (hypothesis; works in practice)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Native.cs:446`
  (`out nint closestMatch`); `CoreAudio.Formats.cs:185-187`.
- **Problem:** Microsoft documents a NULL closest-match pointer for exclusive mode; the `out` declaration always passes
  one.
- **Best solution:** declare the parameter `nint closestMatch`, pass `nint.Zero`, and delete the `FreeCoTaskMem(closest)`
  call.
- **Tests:** covered by the U01-023 probe tests.
- **Plan v2:** B066.
- **Related:** U01-023.

### U01-060: Avoidable per-notification allocations in callback paths

- **Severity:** nit (not a high-rate path, per the review)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Watch.cs:320-323, 373`
  (`OnPropertyValueChanged` marshals the device id string even for ignored properties);
  `WindowsRadio.WifiWatch.cs:107, 190, 203`, `WaveOutFeedback.cs:108`, `WindowsPower.cs:108` (`Marshal.PtrToStructure`).
- **Problem:** small allocations and boxing per notification. Notifications are rare, so this is tidiness, not a
  performance defect.
- **Best solution:** only in code these batches already rewrite: `OnPropertyValueChanged` ignores every property
  change and returns 0 (`CoreAudio.Watch.cs:320-323`), so declare its device-id parameter as `nint` in
  `IMMNotificationClient` and the callback and never marshal it (checker: there is no property key to match first);
  read blittable structs with `*(T*)pointer` (the project already allows unsafe code).
- **Tests:** none (no behaviour change).
- **Plan v2:** B063 (watch code, `OnPropertyValueChanged`), B066 (WaveOut), B071 (`WindowsPower.cs:108`, a file only
  B071 lists).
- **Related:** U01-055.

### U01-062: CoreAudio mixes error models and fills out-lists on failure

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.cs:196-276` (`ListEndpoints` assigns the
  out list at `:203` before the loop); `CoreAudio.Spatial.cs:175` (`(SpatialAudioSetStatus)result.Status` for any value);
  `CoreAudio.Bluetooth.cs` (container members throw undocumented `COMException`).
- **Problem:** a failure mid-loop returns an error together with a partial list; an unknown native status becomes an
  undefined enum value; callers are not told which members throw.
- **Best solution:** build into a local and assign `endpoints = records` only on success, `[]` otherwise (the parameter
  doc already says "empty when the call fails"); map statuses outside 0-5 to `SpatialAudioSetStatus.UnknownError`;
  document the `COMException` on the container members.
- **Tests:** the moved `InvalidEndpointFlowsFailWithoutCallingCom` stays green; a pure status-mapping test for an
  out-of-range value. WDC filter `FullyQualifiedName~CoreAudioTests`.
- **Plan v2:** B066 (`CoreAudio.cs`, `CoreAudio.Spatial.cs`), B065 (the `COMException` docs in
  `CoreAudio.Bluetooth.cs`, which only B065 lists).
- **Related:** U01-012, U01-023.

### U01-063: Display naming and documentation nits

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayLayouts.cs:76-81` (`Fingerprint` doc
  "changes whenever the observation changes" although it covers only availability, activity, position, size and
  refresh), `:226-233` (`Validate` success is reported as `Applied`), `:454-457` (private `Describe(DisplayTargetIdentity)`
  label rule) beside `DisplayLayoutPlanner.cs:240-247` (`Name`, a second label rule with a different fallback);
  `DisplayTopology.Types.cs:72-80` (`DisplayWaitOutcome.TimedOut` says "active" for the available wait too);
  `DisplayScaling.cs:52` ("requested step" when the value was clamped and snapped).
- **Problem:** several docs say more or other than the code does, `Validate` reports success with the apply outcome, and
  two private helpers label the same display differently.
- **Best solution:** `DisplayLayoutOutcome.Valid` for a successful `Validate` (part of the B069 outcome change); the
  fingerprint doc lists exactly what it covers; both label helpers leave WDC with the wording handoff (WDC-023), and
  WSGM's `DisplayText` keeps one label function: the planner's `Name` rule, because the `Describe` reasons that use it are
  visible UI text and must stay byte-identical, while the layout warnings that used the other rule only reach the log;
  `DisplayWaitOutcome` is deleted with the waits in B070; `TrySet` documents that it confirms the snapped step.
- **Tests:** `DisplayLayoutTests` asserts `Validate` returns `Valid`. WDC filter `FullyQualifiedName~DisplayLayoutTests`.
- **Plan v2:** B069 (B070 for the wait doc).
- **Related:** U01-027, U01-036, WDC-004, WDC-023.

### U01-064: InvalidOperationException escapes result-returning display calls

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs:72-79, 121`
  (`ValidateBufferCounts`), `:84-109` (`TryFindActive` catches only `Win32Exception`); `DisplayLayouts.cs:248-259`.
- **Coverage:** moot once WDC-007 deletes `ValidateBufferCounts` (the only source of that exception) with its README text
  and test; allocation follows the size Windows reports with `checked` arithmetic.
- **Plan v2:** B067.
- **Related:** WDC-007, U01-030.

### U01-065: Layout extras and observations repeat full topology queries

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/DisplayLayouts.cs:346-372` (`ApplyPerTarget`,
  `ApplyOutputExtras` call `DisplayColor.TrySetHdr(target)` and `DisplayScaling.TrySet(target)`, each re-running
  `TryFindActive` and a full CCD query), `:459-486` (`ReadOutput` already reuses the resolved path for reads), `:305-316`
  (rollback state from two queries).
- **Problem:** each HDR or DPI write in an apply re-queries the whole topology for a path `Run` already holds, and the
  rollback extras come from a second, later observation.
- **Best solution:** add internal overloads `DisplayColor.TrySetHdr(in PathInfo path, bool enabled)` and
  `DisplayScaling.TrySet(in PathInfo path, int percent)` that the public target-based methods call after their own
  lookup; `ApplyOutputExtras` passes the path `Run` resolved for that output. Rollback extras come from the same
  snapshot as the native rollback (WDC-025). `Observe` keeps reading scaling and colour per active path, because the
  observation is defined to include them.
- **Tests:** `DisplayApplyTests` with the fake port: one `Query` before the apply and no extra query per output. WDC
  filter `FullyQualifiedName~DisplayApplyTests`.
- **Plan v2:** B067.
- **Related:** WDC-025, U01-036.

### U01-066: Host-specific context in a public library's docs and comments

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/Backlight.cs:8-11, 62` ("reference Claw");
  `PowerRequestList.cs:31-39` (the maintainer's WakeWatch project, with Markdown in XML); `CoreAudio.Native.cs:92-96`
  ("QAM poll").
- **Problem:** a public MIT library describes its host instead of itself.
- **Best solution:** neutral wording: "verified on a device whose AC and DC levels track one slider"; "Decoding follows
  the undocumented POWER_REQUEST_LIST layout" with plain XML text and the attribution moved to a source comment if it must
  stay; "a frequent caller" instead of "QAM poll".
- **Tests:** none.
- **Plan v2:** B072 (B066 already rewrites the Backlight comment it touches).
- **Related:** U01-068, U01-025.

### U01-067: csproj comments are wrong or stale

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj:17-19`
  (`AllowUnsafeBlocks` rationale names only WLAN and backlight stack buffers), `:31-33` (`EmbedUntrackedSources` comment
  describes reference-assembly parameter names).
- **Problem:** both comments describe something the setting does not do.
- **Best solution:** "Unsafe code reads native buffers and fixed-size native structs (WLAN, CCD, backlight, Core Audio)."
  and "Embeds generated and untracked source files in the PDB so source-link debugging works."
- **Tests:** none.
- **Plan v2:** B046.
- **Related:** U01-045.

### U01-068: The library AGENTS.md map has drifted

- **Severity:** nit
- **Where:** `external/windows-device-control/AGENTS.md:26-28, 133, 223-227` (repository map misses the COM declarations
  in `CoreAudio.Watch.cs:343-374`; DisplayModes and DisplayEdid notes are appended after the Validation section).
- **Problem:** the guidance map no longer matches the files.
- **Best solution:** write the exact diff that adds the missing locations and moves the two notes into the map, show it
  with B072 and apply it (D4: guidance edits the batches need are approved).
- **Tests:** none.
- **Plan v2:** B072 (decided: D4 approved, diff shown with the batch and applied).
- **Related:** U01-072, U01-066.

### U01-069: WindowsRadio says every member is synchronous

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.cs:9-13` (class remarks).
- **Problem:** pairing returns immediately and completes on a worker, and several members block.
- **Best solution:** "Every member except `PairBluetoothAsync` blocks until Windows answers; call them from a worker
  thread. All members are safe to call from any thread."
- **Tests:** none.
- **Plan v2:** B072.
- **Related:** U01-011, U01-019.

### U01-070: README samples and inventory are inaccurate

- **Severity:** nit
- **Where:** `external/windows-device-control/README.md:43` (`status` declared twice), `:60-64` (`PairBluetooth` sample
  without `onFinished`), `:78, 93-107` (type table omits five types; `WindowsStorage` is documented nowhere), `:327-333`
  (`AudioFilePreview` under Status).
- **Problem:** the samples do not compile and the inventory is incomplete.
- **Best solution:** rewrite after B063 to B071 against the final API: compiling samples (registrations, `WifiNetworkKey`,
  `PairBluetoothAsync`), a type table listing every public type, `AudioFilePreview` under Audio, removed caps, the
  SaveToDatabase note (U01-028), threading and blocking rules (U01-011).
- **Tests:** none; optionally compile the samples in a scratch project before committing.
- **Plan v2:** B072.
- **Related:** U01-071, U01-028.

### U01-071: Package metadata is stale and incomplete

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj:36-47`.
- **Problem:** description and tags mention only radio, audio and brightness; no `Authors`, `Copyright` or
  `PackageProjectUrl`.
- **Best solution:** describe all areas (radios, Wi-Fi, Bluetooth, Core Audio, backlight, display topology and modes,
  power, Modern Standby, wake security, storage), extend the tags to match, add `Authors`, `Copyright` and
  `PackageProjectUrl` (the repository URL already present).
- **Tests:** `dotnet pack` of the library succeeds.
- **Plan v2:** B072.
- **Related:** U01-070.

### U01-072: CodeRabbit path instructions cover only the original surface

- **Severity:** nit
- **Where:** `external/windows-device-control/.coderabbit.yaml:32-82` (`path_instructions`).
- **Problem:** no instructions for display, power, Modern Standby, request list, wake security, storage, preview or
  interop files.
- **Best solution:** write a diff that mirrors the AGENTS.md rules for those files, show it with U01-068 in B072 and
  apply it (D4 approved).
- **Tests:** none.
- **Plan v2:** B072 (decided: D4 approved, diff shown with the batch and applied).
- **Related:** U01-068.

### U01-073: .gitignore covers only bin and obj

- **Severity:** nit
- **Where:** `external/windows-device-control/.gitignore:1-2`.
- **Problem:** IDE and test output folders are not ignored, and the parent's rules do not apply inside the submodule.
- **Best solution:** add `.vs/`, `.idea/`, `*.user`, `*.DotSettings.user` and `TestResults/`.
- **Tests:** none.
- **Plan v2:** B046.
- **Related:** none.

### U01-074: Error codes from different spaces are mixed in one channel

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Wifi.cs:297` (a zero WLAN reason
  becomes Win32 `ERROR_NOT_FOUND` 1168 in the reason channel), `:903-907` (`WlanReasonException` puts WLAN reasons in a
  `Win32Exception`); `ModernStandby.cs:396-407` (`CallNtPowerInformation` NTSTATUS in a `Win32Exception`);
  `WaveOutFeedback.cs:177-180` (MMRESULT packed as a FACILITY_WIN32 HRESULT); `Interop.cs:47-50`.
- **Problem:** a consumer cannot tell which code space a number belongs to.
- **Best solution:** only where these batches already rewrite the code:
  - B064: no value change. `WifiConnectResult.ReasonCode` keeps today's substitution of `ERROR_NOT_FOUND` (1168) for a
    failure WLAN reported without a reason, and its XML says so. Checker correction: the ledger proposed
    `WLAN_REASON_CODE_UNKNOWN` (0x10001), but on this machine (2026-10-03) `WlanReasonCodeToString(1168)` fails with
    87, so today's status line reads "Wi-Fi reason code 1168", while 0x10001 returns Windows' localized "Unknown
    error". The swap would change visible text and need a parity entry that maps one code to another code's text.
  - B071: the `CallNtPowerInformation` failure message says "NTSTATUS 0x..." and the XML states that `NativeErrorCode` is
    an NTSTATUS there.
  - B066: `WaveOutFeedback.Open` documents that its non-zero result is an HRESULT built from the MMRESULT, keeping the
    value WSGM logs today.
  No new exception types.
- **Tests:** none (docs and messages only; the WDC-V-003 parity table already pins the Wi-Fi text).
- **Plan v2:** B064, B066, B071.
- **Related:** U01-031, U01-054, WDC-V-002, WDC-V-003.

### U01-075: Wake-security snapshot nits

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsWakeSecurity.cs:36-52` (`Capture`),
  `:58-63` (`IsSignInDisabled` ignores a DC-only policy), `:129-132` (`Read` returns -1 for a non-DWORD value, which
  `Restore` then deletes).
- **Problem:** a DC-only console-lock policy is evaluated as "no policy", and a non-DWORD value is captured as absent and
  deleted on restore.
- **Best solution:** evaluate each power line on its own: effective AC is `PolicyAc` when it is set, else every scheme's
  AC value; effective DC likewise with `PolicyDc` and scheme DC values; sign-in is disabled when both effective sides are
  0. For non-DWORD values, `Capture` checks `GetValueKind` and throws `InvalidDataException` naming the key and value,
  so no mutation starts on a value it could not restore (a type check, and no new persisted shape: review C27). WSGM's
  existing capture-failure path already refuses to apply.
- **Tests:** `WakeSecurityTests`: DC-only policy 0 with schemes AC 0 gives disabled; DC-only policy 1 gives enabled; a
  `REG_SZ` value makes `Capture` throw (through the B071 plan seam). WDC filter `FullyQualifiedName~WakeSecurity`.
- **Plan v2:** B071.
- **Related:** U01-005, WDC-006.

### U01-076: ModernStandby enumeration nits

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/ModernStandby.cs:92, 102, 212, 415-443`
  (`Enumerate` stops at 4096 entries and returns the partial list; 4096-byte name buffer), `:320-330` (each restore write
  re-enumerates).
- **Problem:** a list longer than the bound is returned as complete; restore re-enumerates per device.
- **Best solution:** WDC-007: the loop ends only on `ERROR_NO_MORE_ITEMS`, and the name buffer grows to the size Windows
  reports on `ERROR_INSUFFICIENT_BUFFER` instead of a fixed 4096. The single enumeration per restore is U01-038.
- **Tests:** as WDC-007 and U01-038. WDC filter `FullyQualifiedName~ModernStandbyTests`.
- **Plan v2:** B071.
- **Related:** WDC-007, U01-038.

### U01-077: DescribeVolumes does not filter by drive type

- **Severity:** nit (hypothesis)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsStorage.cs:54-93` (`IsReady` on every
  drive); consumer `src/WSGM/Shell/SteamStorageBridge.cs` (network drives never join: `DiskNumber` -1).
- **Problem:** `IsReady` on a disconnected network share can block the enumerating thread for the SMB timeout.
- **Best solution:** `if (drive.DriveType == DriveType.Network) continue;` before `IsReady`. No visible change, since
  WSGM never shows network drives.
- **Tests:** none (DriveInfo cannot be faked); covered by the storage page in the manual pass.
- **Plan v2:** B071.
- **Related:** U01-011.

### U01-078: WindowsPower API and documentation nits

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsPower.cs:15-45` (public
  `EnumerateScheme(uint)`, internal list helper `EnumerateSchemes`); `WindowsPower.Policy.cs:37-56` (`GetEffectiveMode`,
  `SetActiveMode` lack returns and exception docs; the overlay exports they use are undocumented by Microsoft);
  consumer `src/WSGM/Interop/WindowsPowerSchemeApi.cs:19`.
- **Problem:** the convenient list form is internal while the index form is public, and the overlay calls do not say they
  rely on undocumented exports.
- **Best solution:** make `EnumerateSchemes()` public returning `IReadOnlyList<Guid>`; keep `EnumerateScheme(uint)`,
  which WSGM calls (wdc.verify correction). Complete the returns and exception docs, and add a remark that the overlay
  functions are undocumented Windows exports. Apply WDC-019's neutral readback wording in the same pass.
- **Tests:** none (existing power tests stay green). WDC filter `FullyQualifiedName~WindowsPowerTests`.
- **Plan v2:** B071.
- **Related:** U01-039, WDC-019.

### U01-079: AudioFilePreview nits

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/AudioFilePreview.cs:15-29` (`Action<string>`
  `Failed` event; `Play` documents only `ArgumentException`); consumer `src/WSGM/Shell/SoundPackService.cs:314`.
- **Problem:** the event drops the native error, and `MediaPlayer` creation can fail on N editions without the media
  feature pack, which `Play` and the constructor do not document.
- **Best solution:** the event change is WDC-009: `Failed` becomes `Action<AudioPreviewFailure>` with the
  `MediaPlayerError`, HRESULT and message, and `_player` is read with `Volatile.Read`. Add the constructor and `Play`
  exception docs (`COMException` when Windows media components are missing). `SoundPackService` logs the same text plus
  the HRESULT.
- **Tests:** none in WDC (MediaPlayer); WSGM filter `FullyQualifiedName~SoundPack`.
- **Plan v2:** B066.
- **Related:** WDC-009, U01-070.

### U01-080: Test assertions on wording and self-referential layouts

- **Severity:** nit
- **Where:** `external/windows-device-control/tests/WindowsDeviceControl.Tests/DisplayLayoutTests.cs:44-79`
  (substring assertions on `Describe` text); `PowerRequestListTests.cs:15-41` (synthetic buffer built from the decoder's
  own offsets).
- **Coverage:** covered by WDC-018 (removal plan) plus two replacements: `Describe` returns `DisplayLayoutProblem?` in
  B069 (WDC-023), so its tests assert the enum; B071 replaces the synthetic buffer with captured `POWER_REQUEST_LIST`
  byte fixtures, including a long process path (WDC-007).
- **Plan v2:** B046 (removals), B069 (Describe assertions), B071 (captured fixtures).
- **Related:** WDC-018, WDC-023, U01-036, U01-085.

### U01-081: WAVEFORMATEX is declared twice with different packing

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WaveOutFeedback.cs:211-221` (private
  `WaveFormat`, default packing, 20 bytes); `CoreAudio.Native.cs:141-151` (internal `CoreAudio.WaveFormat`, `Pack = 2`,
  18 bytes).
- **Problem:** two declarations of one native struct, one with the wrong size. Harmless for PCM, where `cbSize` is
  ignored.
- **Best solution:** delete the private copy and use `CoreAudio.WaveFormat` in `WaveOutFeedback`.
- **Tests:** `NativeLayoutTests` already pins `CoreAudio.WaveFormat` at 18 bytes (U01-043). WDC filter
  `FullyQualifiedName~NativeLayoutTests|FullyQualifiedName~CoreAudioTests`; B066's listed filter lacks
  `NativeLayoutTests`, so add it to that batch's command.
- **Plan v2:** B066.
- **Related:** U01-043, U01-026.

### U01-083: GetConsent accepts null or empty capability names

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Power.cs:121-126` (`GetConsent`).
- **Problem:** null or empty reads the ConsentStore root and reports `Unset` as if it were an answer.
- **Best solution:** `ArgumentException.ThrowIfNullOrWhiteSpace(capability)` at the top; document it.
- **Tests:** `GetConsent("")` and `GetConsent(null!)` throw before any registry read. WDC filter `FullyQualifiedName~WindowsRadioTests`.
- **Plan v2:** B065.
- **Related:** U01-013.

### U01-084: Restarting the Wi-Fi feed tears down the old feed before the new one exists

- **Severity:** nit
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs:44-61` (`DetachWatch()?.Dispose()`
  before the new registration).
- **Coverage:** moot with WDC-014: there is no "replace" any more. A consumer that wants a new feed creates it and then
  disposes the old registration, so a failed start leaves the old feed running.
- **Plan v2:** B063.
- **Related:** WDC-014, U01-001.

### U01-085: The power-request decoder assumes a 64-bit structure layout

- **Severity:** nit (hypothesis; WSGM runs x64)
- **Where:** `external/windows-device-control/src/WindowsDeviceControl/PowerRequestList.cs:59-90` (`Query`), `:127-216`
  (`DecodeWithBuild`, `DecodeOne` read pointer-sized fields as 8 bytes at x64 offsets).
- **Problem:** in a 32-bit process the decoder would read garbage and could report a wrong holder list instead of
  "unknown".
- **Best solution:** with the typed status B071 introduces
  (`(IReadOnlyList<PowerRequestEntry>? Entries, PowerRequestListStatus Status, int NativeStatus)`), `Query` returns
  `PowerRequestListStatus.Unsupported` before decoding when `!Environment.Is64BitProcess`. WSGM words that status like
  today's unknown state.
- **Tests:** none for the x86 branch (the test process is x64); captured x64 fixtures from U01-080. WDC filter
  `FullyQualifiedName~PowerRequestListTests`; WSGM filter `FullyQualifiedName~PowerRequestList`.
- **Plan v2:** B071.
- **Related:** U01-043, U01-080, WDC-007.

### U01-C-001: The Steam network projection drops a scanned network when the joined one is merged in

- **Severity:** nit (checker addition, same Wi-Fi contract boundary)
- **Where:** `src/WSGM/Shell/NativeQamNetworkService.cs:142-146` (`ReadStateAsync`: after inserting the connected network
  from `GetWifiStatus`, `if (networks.Count > 24) networks.RemoveAt(networks.Count - 1);`).
- **Problem:** a count cap that drops content: with 24 or more scanned networks, merging the connected one silently
  removes the weakest scanned row from Steam's list. The scan list itself is not capped, so the rule only ever removes
  one row and serves no bound; it survived a Qodana refactor (d811ded9) as a leftover.
- **Best solution:** delete the two-line cap when WINSVC-023 rewrites this method in B064 to project the connected
  network from `RadioManager` (keyed by `WifiNetworkKey`). The projection publishes every row. Nothing else changes.
- **Tests:** a `NativeQamNetwork` test with 25 scanned rows plus a connected network that is not among them publishes
  26 rows. WSGM filter `FullyQualifiedName~NativeQamNetwork`.
- **Plan v2:** B064 (with WINSVC-023; `NativeQamNetworkService.cs` is in its file list).
- **Related:** WINSVC-023, U01-012, U01-016.

## Missing bodies

None. All 85 U01 bodies are present in `claude-findings-raw.json`. The two provisional records that mention the unit
(PV03 `reports/lead-check-U01.md`, PV06 `reports/U01-WDC.wip.md`) carry assignment metadata and no observations.

## Refuted or no-change

- U01-033 (low, hypothesis): `DisplayColor` keeps the legacy advanced-colour packets. Plan v2 and review C24: no change
  until the Windows 11 24H2 HDR packets are independently verified on hardware (evidence gate); nothing in this refactor
  adopts unverified native calls.
- U01-040 (low): no change as a whole. Every public member got a per-API decision in review section 4.3, and nothing is
  removed merely for non-use. The WSGM mechanisms it names move under their own ids (arrival waiter: U01-029, B070;
  Bluetooth container normalization: WDC-005, B065). The Intel GPU and Device Lab private CCD reads are accepted
  duplication (review C36): Intel needs one call already pinned by its own test, and sharing would add a WDC dependency
  and a public lookup to a GPU plugin.
- U01-061 (nit): no code change. No direction-aware `SetMuted` overload, because nothing would call it. The doc
  alignment (the Bluetooth container list returns every endpoint state, while `docs/radios.md` says active and
  unplugged) rides in B072's `radios.md` edit and, for the `ListBluetoothAudioContainers` XML, in B065, the batch that
  lists `CoreAudio.Bluetooth.cs` (checker: the ledger named B066, which owns neither file).
- U01-082 (nit): no change on its own. A formatting preference with no defect; mid-operator line splits are tidied only
  where a batch already edits those lines. (Checker: the ledger's "the Rider cleanup profile owns formatting elsewhere"
  is wrong for this code, because eng/verify.ps1 runs the cleanup over src and tests only, not external.)
