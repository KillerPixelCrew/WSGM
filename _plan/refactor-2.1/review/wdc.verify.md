# WDC review: adversarial verification

Verifies `_plan/refactor-2.1/review/wdc.md` against the code on 2026-10-03: parent `master` 1329813f, child `main` 2f07485 plus the
uncommitted W02_01 diff (`DisplayTopology.cs`, `DisplayTopologyTests.cs`, both `w/mixed` line endings). Read-only: no
build, test or git mutation.

Read in full for this pass: `WindowsRadio.WifiWatch.cs`, `WindowsRadio.Bluetooth.cs`, `WindowsRadio.Wifi.cs`,
`WindowsRadio.WifiRules.cs`, `WindowsRadio.WlanNative.cs` (1-200), `WindowsRadio.Power.cs` (1-200), `DisplayTopology.cs`,
`DisplayTopology.Types.cs`, `DisplayLayouts.cs`, `DisplayLayoutPlanner.cs` (1-80, 150-175), `DisplayModes.cs`,
`DisplayScaling.cs`, `DisplayColor.cs`, `DisplayEdid.cs` (1-120), `WindowsWakeSecurity.cs`, `ModernStandby.cs` (85-480),
`PowerRequestList.cs` (1-140), `WindowsPower.cs`, `.Policy.cs`, `.Status.cs`, `.Notifications.cs`, `.Actions.cs`,
`.HybridCores.cs` (100-307), `WindowsStorage.cs`, `Interop.cs`, `Backlight.cs`, `AudioFilePreview.cs`,
`CoreAudio.Watch.cs`, `CoreAudio.Native.cs` (1-140), `CoreAudio.cs` (180-300), both csproj files, the W02_01 diff, the
WDC test method list plus `PowerRequestTests`, `WakeSecurityTests`, `TestFixtures`, `WindowsPowerTests`. WSGM consumers
traced: `ConfigStore`, `GameModeLaunchConfiguration`, `LockScreenSettings`, `DisplayScale`, `DisplayProfiles`,
`RefreshRatePairingService`, `DisplayArrivalWaiter`, `GameModeEntryTransaction`, `SettingsViewModel.Launch`,
`DisplayLayoutEditor`, `RadioManager`, `RadioManagerTests`, `BluetoothDeviceCatalog`, `VolumeFeedback`,
`AudioProfileService`, `PluginPackageLoader`, `NvApi`, `NvOutputControls`.

General accuracy note: many of the reviewer's line citations do not exist in the current files and must be fixed
before the report is used as a work order. Examples: `DisplayTopology.Types.cs:295-307/328-339/347` (file has 80 lines;
`Matches` is 26-38, `DisplayProfileResult` 65-70), `DisplayColor.cs:186-187, 219-260, 248-261` (131 lines; the packets
are 15-16, `TrySetHdr` 42-91), `WindowsPowerTests.cs:102-109` (69 lines; the test is 55-62), `CoreAudioTests.cs:319-324`
(167 lines; the WaveOut bit test is 162), `ModernStandbyTests.cs:239-260` (171 lines; the GUID test is 124),
`WindowsPower.Policy.cs:254-267/262` (57 lines; the doc is 215). The findings behind them still exist; the anchors do not.

---

## Refuted

**R1. B2 test seam claim.** B2 says CoreAudio watch ownership is tested "with a fake `IAudioEndpointVolume` (internal
interface already declared)". `IAudioEndpointVolume` is a `private` `[ComImport]` interface (`CoreAudio.Native.cs:285`),
as are every other Core Audio COM interface (`:189-476`) and both callback interfaces (`CoreAudio.Watch.cs:346,355`).
The only internal interface in the library is `IPowerRequestApi` (`WindowsPowerRequest.cs:154`). B2 must widen the
visibility itself and say so.

**R2. B2 depends on a seam that B3 creates.** B2's WLAN registration tests run "through an internal registration factory
seam for WLAN (`IWlanApi` notification subset)", but `IWlanApi`/`NativeWlanApi` are introduced in B3
(`WindowsRadio.WlanNative.cs (IWlanApi, ...)`), and B3 depends on B2. As written B2 cannot carry its tests.

**R3. C25 "Nothing ever writes identities back".** False. `SettingsViewModel.Launch.cs:306-313` matches each available
observation against `KnownDisplays` with path-first `Matches` and then assigns `existing.Target = observed.Target`, so
saving Settings already upgrades stored identities with fresh EDID fields after an unambiguous match. The reviewer's
conclusion survives (no separate migration code is needed; W02_01's `ExistingPathIdentitySurvivesEdidPopulation` plus
this existing refresh is the whole story), but the stated reason is wrong.

**R4. C11 "Key = raw SSID bytes only".** Inconsistent with the reviewer's own C12/B3 rule "merge by bytes+security" and
with the plan ("immutable raw-byte `WifiNetworkKey` with security ... selection"). Two visible networks with the same
SSID bytes and different security (a WPA2 network and an open guest network with the same name) stay two rows after a
bytes+security merge; a bytes-only key cannot tell `ConnectWifi` which one was chosen, which recreates the ambiguity
U01-002/U01-016 are meant to remove. The key must carry the security class (interface choice can stay internal).

**R5. WDC-018 sub-claim "`WindowsRadioTests.cs:134-139` ... mutates the process-global pairing table".** The test
calls `RespondToPairing(uint.MaxValue, ...)`; `PendingPairings.TryRemove` on an absent key changes nothing
(`WindowsRadio.Bluetooth.cs:470-473`). The "no assertion" half stands.

---

## Corrected

**WDC-001 (medium, keep): wire-stable set is five records, and the guard lands too late.**
`KnownDisplay.Modes` persists `List<DisplayMode>` (`GameModeLaunchConfiguration.cs:55`), so `DisplayMode`
(`DisplayModes.cs:13`) joins `DisplayTargetIdentity`, `DisplayLayout`, `DisplayLayoutOutput`, `DisplayRefresh`. Any new
`DisplayLayoutOutput` positional parameter must carry a default (old JSON lacks it; `Rotation = 1` already relies on
this). Drop the WDC-side reflection pin test: pinning a library type for one consumer's file format is consumer policy
inside the library (requirement 7) and duplicates the real guard. Keep only the WSGM real-shaped `config.json`
round-trip fixture, and land it before B6/B7 touch any of these records, not in B10.

**WDC-002 (medium -> low, latent): `NormalizeLayout` deletes on `Describe`.** Mechanism confirmed
(`ConfigStore.cs:891-892,989-996`), but no current input triggers it: `GameLayout`/`DesktopLayout` are only authored by
the editor, which enforces the same `Describe`, and `PendingReturnLayout` (recovery) is not normalized at all. It
becomes a loss only if a later batch tightens `Describe`. None of B6/B7 does; removing the 16 cap loosens it.

**WDC-003 (low): rollback after refusal, recommendation.** The premise "a refused SetDisplayConfig/CDS call changed
nothing" is not established: `DISP_CHANGE_FAILED` and SetDisplayConfig generic failures can follow a partial driver
change, and B6's own rule is "rollback once on confirmed mismatch". Replace "Refused, no rollback" with: non-zero status
is reported Refused with its status; one readback decides whether the original is still in place; roll back once only
on a confirmed mismatch on a still-verified route. Doc mismatch on `DisplayLayoutOutcome.Unconfirmed`
(`DisplayLayouts.cs:106`) confirmed.

**WDC-004 (medium, keep): consumers are wider than listed.** The readback-gated result already drives behaviour: a
mismatched readback keeps the scaling restore entry for another attempt (`DisplayScale.cs:147-154`), and the NVIDIA
plugin throws `DriverFailure` when `TrySetHdr` reports unconfirmed (`NvOutputControls.cs:212-218`). The typed result
change must list `NvOutputControls.cs` and decide whether `Written` is success there (the plan's Commands section says
it is: "Write success publishes the written value as Observed even when readback is absent/mismatched").

**WDC-007 (low): cap inventory incomplete.** Also present: `WindowsPower.HybridCores.cs:85,271` `MaximumCpuSetBytes`
(refuses, makes `QueryHybridCores` throw), `WindowsPower.cs:13,61` `MaximumNameBytes` (64 KiB scheme name refused),
`ModernStandby.cs:92,212,423` `MaximumNameBytes` 4096 (device name buffer). `DisplayEdid.cs:46` (32768) is the EDID
domain maximum (256 blocks) and stays.

**WDC-018 (low): anchors.** Correct anchors are `ModernStandbyTests.cs:124`, `HybridCoreTests.cs:88`,
`PowerRequestListTests.cs:80`, `CoreAudioTests.cs:162`, `DisplayTopologyTests.cs:15-27`. See R5.

**C21/C19 + B6 rotation: see WDC-V-001.** "Include rotation in equality/readback" is accurate as a library defect but
unsafe to land alone, because WSGM's editor never stores rotation.

**WDC-023 scope.** Display strings and "Audio device" are not the only library text the UI shows verbatim; Wi-Fi,
pairing and scan text is too (WDC-V-002, WDC-V-003).

**`WindowsPower.EnumerateScheme(uint)` X (inventory 4.3).** No defect backs the removal (U01-078 asks for a public list
helper, not deletion) and WSGM calls it (`Interop/WindowsPowerSchemeApi.cs:19`). Make `EnumerateSchemes()` public and
retain or remove the index form as a stated simplification choice, not as a defect replacement.

---

## Confirmed (ids only)

WDC-005, WDC-006, WDC-008, WDC-009, WDC-010, WDC-011, WDC-012, WDC-013, WDC-014, WDC-015, WDC-016, WDC-017, WDC-019,
WDC-020, WDC-021, WDC-022, WDC-024, WDC-025, WDC-026; claims C1, C4, C5, C6, C8, C9, C12, C13, C14, C15, C16, C17, C18,
C20, C22, C23, C26, C27, C28, C29, C30, C31, C32, C35, C37, C38, C41, C42, C43, C47, C48. U01-001, U01-015, U01-019,
U01-021, U01-022, U01-027 (as a library defect), U01-029, U01-035 re-read against code and hold. W02_01 diff matches its
spec exactly (12 selected cases, internal pure decoder, no public change).

---

## Missed findings

**WDC-V-001 (medium): the layout editor drops rotation, so the planned rotation fix will rotate displays.**
`src/WSGM/Settings/DisplayLayoutEditor.cs:274-279` `ToOutput()` builds `DisplayLayoutOutput` without `Rotation`, so
every editor-saved `GameLayout`/`DesktopLayout` carries the default `1`, even for a display loaded from a captured
layout with another rotation (`Load`, L265-266, keeps DPI and HDR but not rotation). The planner always writes it
(`DisplayLayoutPlanner.cs:166`). Today the damage is limited because `Matches` ignores rotation
(`DisplayLayouts.cs:378-399`), so an otherwise matching desktop is reported `AlreadyActive` and not written; any real
apply already resets a portrait or panel-rotated display to rotation 1. B6 (U01-027, "rotation in Matches/Confirm")
makes every such display mismatch, so entry/return writes rotation 1 or ends `Unconfirmed` with a rollback. That is a
visible behaviour change (requirement 9). Recommendation: rotation has no editor control, so editor-authored outputs
should not encode it: let `Rotation = 0` mean "keep the display's current rotation" in the planner and in
`Matches`/`Confirm`, have `ToOutput` emit 0, and in the config 0->1 migration rewrite `Rotation 1 -> 0` in
`GameLayout`/`DesktopLayout` only (they are always editor-authored, so nothing chosen is lost). `PendingReturnLayout`
comes from `Capture()` and keeps its exact rotation and the U01-027 behaviour. This must land in or before B6.

**WDC-V-002 (medium): WSGM classifies Wi-Fi scan failures by substring-matching WDC exception text, and the match is
wrong.** `src/WSGM/Shell/RadioManager.cs:880-886` `DescribeScanFailure` tests `message.Contains("Win32 5")` against the
text built by `Interop.cs:47-50` (`"{operation} failed (Win32 {status})."`), pinned by
`tests/WSGM.Tests/Shell/RadioManagerTests.cs:54-63`. Any status whose decimal starts with 5 (`ERROR_INVALID_STATE` 5023,
`ERROR_NOT_SUPPORTED` 50, 5xx) is shown as the location-consent message. The contract is also invisible: B3's "scan
status propagated / typed" change would silently drop the consent guidance. Recommendation: in B3 the scan result
carries the native status; WSGM compares `status == 5` (ERROR_ACCESS_DENIED) and keeps the exact current UI strings;
replace the message-text test with a status test.

**WDC-V-003 (medium): Wi-Fi, scan and pairing library text is UI text, and B3/B4 have no parity table.**
`RadioManager.cs:1058-1063` shows `ex.Message` from `ConnectWifi` through `DescribeConnectFailure`'s fallback (for
example "More than one network advertises this display name; it cannot be identified safely.",
`WindowsRadio.Wifi.cs:136,365`; "This network needs a password and has no saved profile.", `:204`; "The Wi-Fi
connection attempt did not complete.", `:319`); `:612-613,833` show `ListWifiNetworks` failure text;
`:1389,1415-1420` show `failure.Message` from pairing (for example "Bluetooth pairing timed out.",
`WindowsRadio.Bluetooth.cs:532`). B3 (`WifiConnectResult`, `Pending`, scan status) and B4 (`PairBluetoothAsync`,
`UnpairBluetooth` outcome) replace these exceptions with typed outcomes, but only B7 plans exact-string tables, and only
for display. Recommendation: B3/B4 each add the WSGM mapping with the current literals (the new `Pending` outcome keeps
today's timeout text) and a table test, as B7 does.

**WDC-V-004 (low): `DisplayTargetIdentity.Matches` is not reflexive for identities with neither path nor EDID IDs.**
`DisplayTopology.Types.cs:26-38` returns false when both sides have an empty path and null IDs, including `x.Matches(x)`.
Consequences: `DisplayLayouts.Observe` cannot fold the many `QDC_ALL_PATHS` routes of such a target into one row
(`DisplayLayouts.cs:183-189`), so the editor and `KnownDisplays` (`SettingsViewModel.Launch.cs:306-311`) can offer and
save a display that `RequiredDisplays` (`GameModeEntryTransaction.cs:288-302`), `DisplayArrivalWaiter.Present` and
`Run`'s absent check can never find, so entry waits until the user cancels. W02_01 makes this rarer (EDID now decodes)
but does not remove it. Recommendation: no new matching heuristic; `Observe` folds routes of such targets by `RouteKey`,
and WSGM does not persist an identity that cannot match (a type check at the editor/catalog boundary).

**WDC-V-005 (low): `AudioEndpoint.Name` is persisted, so B5's "Name empty, WSGM supplies 'Audio device'" reaches
config.** `AudioProfileService.cs:388` and `AudioProfileEditor.cs:338-339` store `endpoint.Name` in
`AudioEndpointPreference.Name`, including `PendingReturnAudio` recovery. With an empty library name, captured profiles
store `""` and the log at `AudioProfileService.cs:364` prints `''` instead of falling back to the id (`"" ?? id`).
Recommendation: B5 lists `AudioProfileService.cs` and both editor lines, and applies the WSGM fallback at the capture
points as well as the list views.

---

## Batch problems

1. **Publication model contradicts PC2 R2-3.** Every batch ends "commit and push child; record gitlink". The binding
   correction makes child commit/push and final gitlinks a single I02 step after the last child edit (with I01 as
   interim API publication), and no child commit/push is authorized outside it. Per-batch publication needs explicit
   maintainer approval or the batches must stop at a validated child working tree.
2. **B1 swallows W02_02 and grows to about 650 lines.** W02_02 is a pending standalone batch with its own review state;
   folding it into a foundation batch (new child `Directory.Build.props` as explicit root, child `.editorconfig`, net10
   test target, test moves, native layout tests) loses its small admission scope. Admit W02_02 (simplified port, C30)
   alone first. Also note an explicit-root child `Directory.Build.props` stops importing the parent one, so
   `EnforceCodeStyleInBuild`/`NuGetAudit=false` must be restated or WDC diagnostics change in-tree.
3. **B2:** R1 and R2 above (private COM interfaces; `IWlanApi` seam created in B3). Move the WLAN notification seam into
   B2 or the tests into B3.
4. **B3:** must include WDC-V-002 and WDC-V-003 (status-based consent detection, exact Wi-Fi strings) and fix R4 (key
   includes security). `RadioManagerTests.TheLocationConsentGateIsNamedRatherThanShownAsARawError` breaks or becomes
   meaningless otherwise.
5. **B4:** needs the pairing-text parity table (WDC-V-003).
6. **B5:** consumer list misses `AudioProfileService.cs:364,388` and `AudioProfileEditor.cs:338-339` (WDC-V-005).
7. **B6:** `IDisplayConfigApi { Query, Supply, ReadTarget }` cannot make the stated tests fake-only. `Run` also reaches
   `DisplayScaling.TryRead`/`DisplayColor.TryRead` through `ReadOutput` and `DisplayColor.TrySetHdr`/`DisplayScaling.TrySet`
   (each calling `TryFindActive -> Query` on the real CCD) through `ApplyOutputExtras`, and `DisplayModes` refusal
   (WDC-003) is GDI `ChangeDisplaySettingsEx`, outside the port. Either the port covers the device-info get/set packets
   and the two GDI calls, or the refusal tests are limited to `DisplayLayouts` with fake extras. B6 must also carry
   WDC-V-001 (editor rotation) or it regresses rotated displays.
8. **B7 does not build green on its own.** `DisplayColor.TrySetHdr` changes signature, and
   `src/WSGM.Plugin.NvidiaGpu/NvOutputControls.cs:212-218` calls it; the NVIDIA plugin is missing from B7's file list and
   from 4.4. Because `PluginLoadContext` resolves WDC host-first (`PluginPackageLoader.cs:205-223`), the bundled NVIDIA
   package must be rebuilt in the same batch; a stale package built against 0.1.0 would fail with `MissingMethodException`.
9. **B10 is the wrong place for the wire guard** (WDC-001). The config fixture must precede B6/B7.
10. **B8 moves product timing (settle 500 ms, 5 s backstop, no deadline) into the library.** Acceptable only as
    caller-supplied parameters or documented generic defaults; otherwise it is WSGM policy in WDC (requirement 7).
