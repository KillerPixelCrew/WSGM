# WindowsDeviceControl (WDC) review

Domain: `external/windows-device-control/**` (whole library: 26 source files, 13 test files, csproj x2, README,
AGENTS.md, docs/radios.md, .coderabbit.yaml, .gitignore), including the uncommitted W02_01 EDID change, plus every
WSGM consumer where an ownership or contract crosses the boundary.

Baseline read on 2026-10-03: parent `master` 1329813f; child `main` 2f07485 with two modified files
(`src/WindowsDeviceControl/DisplayTopology.cs`, `tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs`) carrying
W02_01. Read-only review: nothing built, run or mutated.

Every non-generated source and test file in scope was read in full. Consumers were traced with `git ls-files | grep`
across `src/`, `tests/` and `tools/` (89 WSGM files reference WDC; see section 4.4).

---

## 1. Plan claims check

Sources: `refactor-plan.md` (RP), `planning-corrections.md` (PC), `planning-corrections-r2.md` (PC2),
`api-integration.md` (AI), `versions/pre-AM01/task-briefs.md` W01/W02 objectives (TB), `batches/W02_01.md`,
`batches/W02_02.md`, ledger `claude-findings-disposition.md` (U01-*), `audit/A01/findings.json` (A01-F*).
A02 and A02S01 contain no WDC findings (grep for `windows-device-control` returns 0).

| # | Claim (source) | Verdict | Evidence | Correction |
| --- | --- | --- | --- | --- |
| C1 | WDC is clean `main` at 2f07485 (RP L13) | stale | `git status`: two modified files (W02_01). HEAD unchanged. | Baseline must say "2f07485 plus accepted uncommitted W02_01 diff". |
| C2 | WSGM.slnx holds two library projects and two library test projects (RP L15) | accurate | `WSGM.slnx:4` (WDC lib), `WSGM.slnx:40` (WDC tests). | Note the consequence: `eng/verify.ps1` runs the WDC suite, including the real power test until W02_02 lands. |
| C3 | 23 pure display-library tests passed (RP L17) | not re-verified | Read-only stage. | Keep as historical evidence only. |
| C4 | "RadioManager, AudioManager, display arrival, Bluetooth identity and CCD interop overlap reusable library mechanisms" (RP L47) | partially accurate | Display arrival: `src/WSGM/Shell/DisplayArrivalWaiter.cs:40-130` duplicates `DisplayTopology.cs:33-67,223-249`. Bluetooth identity: `BluetoothDeviceCatalog.cs:86-88,105-108` re-normalizes containers. CCD: `WSGM.Plugin.IntelGpu/Display/DisplayIdentity.cs:36-148` and `WSGM.DeviceLab/Wizard/LabSystemDump.Display.cs` own private CCD reads. RadioManager overlap is only the watch-generation workaround (`RadioManager.cs:485-491,680-712`) forced by the global feed slot. AudioManager has no native duplicate. | AudioManager has no overlap; RadioManager's overlap is a symptom of the static feed slot, not a duplicated mechanism. |
| C5 | "WDC has stateful static feeds and incomplete native seams" (RP L47) | accurate | `WindowsRadio.WifiWatch.cs:19-20`, `WindowsRadio.Bluetooth.cs:27-34`, `WindowsRadio.Wifi.cs:14-19`, `WindowsRadio.Power.cs:12-14`, `CoreAudio.Native.cs:15-16`; only `IPowerRequestApi` (`WindowsPowerRequest.cs:154-161`) is a seam. | none |
| C6 | Libraries get "per-instance native/transport resources" (RP L73) and "per-instance RadioPowerService, WifiService, BluetoothService, AudioService and DisplayService behind injectable native adapters" (RP L121, TB W01 step 1, W02 step 1) | over-engineered | The defects are (a) process-global feed slots that one Start replaces, and (b) untestable write/rollback orchestration. Neither needs five instance services. Caches (`_radioCache`, `_statusClient`, saved-profile cache, MMDeviceEnumerator) are identity-free performance caches. | Replace with: per-call disposable watch registrations, internal native ports only for the safety-critical orchestrations (WLAN connect/profile, CCD apply/rollback, power actions), pure planners for restore paths. Public facades stay static. See 3.1. |
| C7 | "multiple consumers subscribe to the same service instead of replacing a process feed" (RP L121) | partially accurate | Defect real (`WifiWatch.cs:33-36` "one feed per process"; plugins share the host WDC assembly through host-first loading). | Each `Start*Watch` call returns its own `IDisposable` registration; no fan-out hub is needed, the native APIs already support independent registrations. |
| C8 | "Callback delivery occurs outside the state lock; ... callback-initiated disposal is safe" (RP L121) | inaccurate / over-engineered | WLAN forbids calling `WlanRegisterNotification` from its own callback; making it "safe" needs thread-identity detection and deferred unregister. Bluetooth today delivers under its lock and reentrant `Monitor` already lets a callback stop its own watch. | Deliver under a per-registration gate; document "do not dispose a Wi-Fi registration from inside its callback; post instead" (WSGM already posts to its dispatcher). |
| C9 | "Native delegates remain rooted until unregister completes" (RP L121) | accurate target | Registration roots the delegate (`WifiWatch.cs:231-257`), but the concurrent-Start race unroots a live one (U01-001, A01-F003). | Fixed by per-registration ownership. |
| C10 | "Service loss invalidates caches and reports unavailable; read-only discovery reconnects" (RP L121) | accurate, scope refine | `GetWifiStatus` already reopens once on `ERROR_INVALID_HANDLE` (`Wifi.cs:34-44`); the cached enumerator never does (`CoreAudio.Native.cs:25-38`); DeviceWatcher Stopped/Aborted not observed (`Bluetooth.cs:159-191`). | Drop the cached enumerator on disconnected/service HRESULTs; surface watcher abort as one terminal change kind. No WLAN service-restart detector (document). |
| C11 | Wi-Fi identity "immutable raw-byte WifiNetworkKey with security/interface selection" (RP L123) | partially accurate | Raw bytes are the identity (`Wifi.cs:494-544`); text-based connect/forget is the defect (U01-016). | Key = raw SSID bytes only. Security is a fact of the network, interface choice stays internal (`ChooseInterface`). |
| C12 | "multiple saved names are not ambiguity; select connected profile, else ordinal first matching profile; forget removes every matching profile reporting each result" (RP L123) | accurate | `WifiRules.cs:420-422`, `Wifi.cs:665-670`, `Wifi.cs:621-640` treat differing profile names as ambiguity. | none |
| C13 | "A definite refused connection restores ... once; a dispatched timeout leaves it intact and reports pending" (RP L123) | accurate | `Wifi.cs:290-319`: timeout rolls back, unreachable keeps a new profile (U01-015). | none |
| C14 | "Pairing is an attempt object with caller token, one overall 90 s active deadline" (RP L125) | partially accurate | Two sequential 90 s waits (`Bluetooth.cs:413-422,518-534`). | Plain wall-clock 90 s plus caller `CancellationToken`. "Active" (freeze-aware) time would make WDC depend on the SDK clock; WDC must not. |
| C15 | "Make dangerous power tests call fake action/process ports before admitting the full WDC suite" (RP L125, PC none) | accurate defect | `WindowsPowerTests.cs:102-109` calls the public suspend/restart paths. | Mechanism in W02_02 is over-specified; see C30. |
| C16 | "Display mutations serialize through one injected service per host" (RP L127, TB W02 step 1 "one instance DisplayService mutation lane") | inaccurate design | CCD state is machine-global and the NVIDIA plugin loads WDC host-first into the same process (`WSGM.Plugin.NvidiaGpu.csproj:12`, `NvApi.cs:343`). An instance per host does not serialize a plugin against the host. Today only `DisplayModes.Gate` exists (`DisplayModes.cs:45`). | One internal static display write gate shared by `DisplayModes`, `DisplayLayouts`, `DisplayScaling`, `DisplayColor`. |
| C17 | "mode/layout/scaling/HDR use the same CCD declarations and resolved topology" (RP L127) | partially accurate | Mode enumeration/apply is GDI `EnumDisplaySettingsEx`/`ChangeDisplaySettingsEx` (`DisplayModes.cs:281-298`) because CCD has no mode list; it is already keyed by the CCD-resolved GDI source (`DisplayModes.cs:262-269`). | Say "resolved through the same CCD topology"; modes stay on GDI calls. |
| C18 | "EDID valid flag is 0x4" (RP L127; U01-004; A01-F001; W02_01) | accurate, implemented | Working tree `DisplayTopology.cs:162-180` (`DecodeEdidIds`, `1u << 2`). Tests `DisplayTopologyTests.cs:29-95`. | Commit after line-ending normalization (WDC-012). |
| C19 | "Include rotation in equality/readback" (RP L127) | accurate | `DisplayLayouts.cs:378-399` compares position, size, refresh only. | none |
| C20 | "Skip unrelated unreadable paths; an unreadable matched target is a typed failure" (RP L127) | accurate | `DisplayTopology.cs:19-31,86-109` (comment L94 states the current failure). | none |
| C21 | "Post-write disappearance returns Unverified ... rollback ... attempted once on confirmed mismatch" (RP L127) | accurate, incomplete | `DisplayModes.cs:158-172` and `DisplayLayouts.cs:318-339` also roll back after a definite refusal (non-zero status), which is not a mismatch. | Add: a refused write is reported Refused with its native status, no rollback (WDC-003). |
| C22 | "Move DisplayArrivalWaiter mechanism and Bluetooth catalog normalization into WDC, retiring duplicate WSGM implementations" (RP L127) vs "Preserve useful current owners: ... BluetoothDeviceCatalog" (RP L75) | partially accurate, internally inconsistent | `BluetoothDeviceCatalog.cs` holds product policy ("Unnamed device", paired endpoints stay as disconnected rows, pairable-endpoint choice). | Move only container normalization (one library normalizer). The waiter moves and replaces the two defective library waits. Catalog stays in WSGM. |
| C23 | "Persistence is an explicit argument: preserve current WSGM choices" (RP L127, U01-028, TB W02 step 3) | over-engineered | Only one caller, always persisting (`DisplayLayouts.cs:318-326`); WSGM recovery (`GameModeLaunchConfiguration.cs:176` PendingReturnLayout) is built around persisted layouts. | Keep `SDC_SAVE_TO_DATABASE` fixed and document it. No parameter without a second behavior to select. |
| C24 | "Keep legacy advanced-color behavior until the 24H2 replacement is independently verified" (RP L127, U01-033) | accurate | `DisplayColor.cs:186-187` (packets 9/10). | none |
| C25 | "Fixing EDID decoding does not erase saved monitor paths: exact stored path still wins, existing EDID fields are preserved and missing fields are upgraded only after unambiguous identity resolution" (RP L103) | partially accurate | `DisplayTopology.Types.cs:295-307` is path-first; W02_01 test `ExistingPathIdentitySurvivesEdidPopulation` covers it. Nothing ever writes identities back, and an identity with neither path nor EDID could never match before either. | Drop the "upgrade missing fields" migration: there is nothing to migrate and it adds a write path to recovery data. |
| C26 | "Wake/sign-in operations become independent primitives composed by WSGM" (RP L129, U01-037) | accurate | `WindowsWakeSecurity.cs:66-82` bundles three policies. Also `Restore` writes guessed defaults when the snapshot has no schemes (`WindowsWakeSecurity.cs:104-117`), which WSGM relies on (`LockScreenSettings.cs:95-104`). | Add: the "no snapshot, restore Windows defaults" decision moves to WSGM (WDC-006). |
| C27 | "Capture registry kinds/data exactly, including non-DWORD and DC-only values" (RP L129, U01-075) | partially over-engineered | DC-only policy is a real logic gap (`WindowsWakeSecurity.cs:58-63`). Non-DWORD values for these Windows-authored settings have no observed case; capturing kinds changes the persisted recovery fields (`LockScreenSettings.cs:81-104`). | Fix DC-only. For non-DWORD: Capture throws a typed "unsupported value kind" so DisableSignIn never mutates a value it cannot restore (type check only, no new persisted shape). |
| C28 | "WaveOut retains any driver-held buffers after unverified close, including repeated dispose, and serializes Play/Dispose" (RP L129, U01-026) | partially over-engineered | Retained-buffer defect real (`WaveOutFeedback.cs:33-48`). The single consumer serializes (`VolumeFeedback.cs`). | Fix the retention; document the type as single-owner, not thread-safe. No lock. |
| C29 | "Avoid probing network volumes" (RP L129, U01-077) | accurate, UI-neutral | `WindowsStorage.cs:54-93`. Network drives have `DiskNumber -1` and never join in `SteamStorageBridge.cs:318-330,620-622`. | Skip `DriveType.Network` before `IsReady`; no visible change. |
| C30 | W02_02 spec: two internal interfaces, nested native process wrapper, about 12 tests incl. a blocking-fake "dispatched suspend cannot be undone" case (batches/W02_02.md) | accurate defect, over-specified mechanism | `WindowsPower.Actions.cs:30-57`. | One internal port `IPowerActionApi { bool Suspend(bool hibernate); Task<int> RunToolAsync(ProcessStartInfo, CancellationToken); }` with a static native instance; drop the process wrapper and the Task.Run-semantics test. See 3.2. |
| C31 | "x86 request-list decoding reports unsupported" (RP L129, U01-085) | accurate | `PowerRequestList.cs:127-216` reads x64 offsets unconditionally. | none |
| C32 | "Own child build/style configuration and multi-target tests for .NET 8 and 10" (RP L129, U01-042/045) | accurate | Test csproj `TargetFramework net8.0-windows...` only; no child `Directory.Build.props`/`.editorconfig`; parent `Directory.Build.props` sets `EnforceCodeStyleInBuild`, `NuGetAudit=false` in-tree only. | none |
| C33 | "Advance WDC to 0.2.0" (RP L129, U01-046) | accurate | `WindowsDeviceControl.csproj:40` `0.1.0`. | Bump in the first breaking batch (WDC-B2), not at the end. |
| C34 | "Full public API audit covers unused helpers; no API is deleted merely because WSGM does not call it" (RP L129, AI) | accurate requirement | Inventory in section 4.3; every removal there is a replacement for a defect, never for non-use. | none |
| C35 | WDC 0.2.0 required consumers include "Intel display/CCD integration" and "Device Lab transports/platform consumers" (AI table) | inaccurate | `WSGM.Plugin.IntelGpu.csproj:13` and `WSGM.DeviceLab.csproj:28-30` do not reference WDC; neither file set uses the namespace. Missing from the list: `WSGM.UiTests` (6 files), `tools/PerfLab/Program.cs:34` (assembly-name string only). | Consumers are: WSGM main (66 files), WSGM.Tests (16), WSGM.UiTests (6), WSGM.Plugin.NvidiaGpu (`NvApi.cs`, `NvOutputControls.cs`). |
| C36 | Intel plugin and Lab CCD duplicates should share one CCD interop (U01-040 direction, RP L47) | over-engineered | Intel needs one `DisplayConfigGetDeviceInfo` call keyed by IGCL LUID/target with output technology, already correct (`DisplayIdentity.cs:42,89`) and pinned by its own layout test. Sharing adds a WDC package dependency and a new public lookup to a GPU plugin. Lab dumps are attended evidence code. | No change for Intel and Lab; record as accepted duplication. |
| C37 | TB W01 validation filter `FullyQualifiedName~WifiProfileTests` | inaccurate | No `WifiProfileTests` exists in the WDC suite; WifiProfile tests live in `tests/WSGM.Tests/Shell/RadioManagerTests.cs:115-160` (U01-041). | Create `WifiProfileTests` in WDC by moving those cases (WDC-B1). |
| C38 | TB W01 ownership includes `Interop*` while W02 owns Display/Power | partially accurate | `Interop.cs` (Win32Error, Kernel32, NativeText, WinRt) is used by Display*, WindowsPower*, ModernStandby, WindowsStorage, Backlight. | Interop.cs is shared; whichever batch touches it owns it for that batch only. |
| C39 | TB W01 "full WDC suite remains barred until W02 removes real power dispatch seam" | stale | Task graph now orders W02_02 first. | Drop the clause. |
| C40 | TB W02 acceptance "All 85 U01 findings resolved/dispositioned" | accurate count | 85 U01 rows (U01-001..085). | Dispositions in section 2.1. |
| C41 | W02_01 spec: internal pure decoder, no public change, three tests | accurate, implemented exactly | Diff matches steps 1-5. | Line endings mixed (WDC-012). |
| C42 | A01-F003 "A lock-only repair can deadlock native unregister/callback" | accurate | `WifiWatch.cs:40-44` comment. | Resolved by per-registration ownership with unregister outside any lock. |
| C43 | A01-F006 available-wait test can reach native query on regression | accurate | `DisplayTopologyTests.cs:109-117`. | Test deleted with the waits it covers (WDC-B8). |
| C44 | "Radio writes return per-adapter native status/access result; audio keeps HRESULTs; WLAN keeps reason codes in typed outcomes" (RP L125) | accurate | `WindowsRadio.Power.cs:66-107`. | Typed outcomes keep the raw code fields; library AGENTS.md L71-77 ("two raw integer contracts") needs a sign-off edit (open question 1). |
| C45 | "Restore attempts every original ... treats a vanished scheme as no longer applicable, retains only unresolved entries" (RP L129, U01-005/038) | accurate | `WindowsWakeSecurity.cs:86-127`, `ModernStandby.cs:320-330`. | none |
| C46 | "Pin native sizes/offsets with independent buffers, not a decoder-derived fixture" (RP L129, U01-043/080) | accurate | `PowerRequestListTests.cs:15-41` mirrors decoder offsets; WLAN/PROPVARIANT/PathInfo/TargetDeviceName sizes unpinned. | none |
| C47 | Reusable libraries contain no WSGM policy (requirements 7) | not yet true | Host references in docs (`Backlight.cs:8-11,62`, `PowerRequestList.cs:31-39`, `CoreAudio.Native.cs:92-96`); bundled sign-in policy; default-restore guess; user-facing English strings shown verbatim by WSGM UI (`DisplayModeView.cs:209`, `DisplayLayoutEditor.cs:763`). | Batches B7, B9, B10. |
| C48 | "Restoring wording to WSGM" (U01-036) implied neutral | incomplete | Strings are visible UI text. | WSGM must reproduce the exact current strings (UI parity requirement). Mapping tables in WDC-B7. |

Claims checked: 48. Inaccurate or stale: C1, C8, C16, C35, C37, C39 (plus over-engineered C6, C23, C25, C27, C28, C30, C36).

---

## 2. Findings

### 2.1 Ledger confirmation (U01 and A01)

All 85 U01 entries and the four WDC A01 entries were checked against current code. None is refuted as a defect;
dispositions are refined where the simplify / no-limits / no-readback rules apply.

| ID | Verdict on current code | Evidence (current lines) | Disposition / batch |
| --- | --- | --- | --- |
| U01-001 | confirmed | WifiWatch.cs:37-69,77-94 | per-registration watch, B2 |
| U01-002 | confirmed | WifiRules.cs:412-437; Wifi.cs:621-640,665-670 | merge by bytes+security, B3 |
| U01-003 | confirmed | DisplayModes.cs:152-172 (`Find` after write throws) | B6 |
| U01-004 | fixed in working tree | DisplayTopology.cs:162-180 | commit, B0 |
| U01-005 | confirmed | WindowsWakeSecurity.cs:86-127 | per-item restore, B9 |
| U01-006 | confirmed | WindowsRadio.Power.cs:139-153 | enumerate outside lock, no new timeout, B4 |
| U01-007 | confirmed | static slots listed in C5 | static feed slots removed (B2); caches stay static; no instance services |
| U01-008 | confirmed | only IPowerRequestApi seam | internal ports for WLAN (B3), CCD (B6), power actions (B1), pure restore planners (B9) |
| U01-009 | confirmed | WindowsPowerTests.cs:102-109 | B1 (simplified W02_02) |
| U01-010 | confirmed | Power.cs:81-106 | per-adapter result, B4 |
| U01-011 | confirmed | Interop.cs:94-97 | token only on pairing; other blocking calls documented, no new timeouts, B4/B10 |
| U01-012 | confirmed | WlanNative.cs:169-191 throws on no interface | GetWifiStatus returns Unknown when no WLAN interface, B3 |
| U01-013 | confirmed | Wifi.cs:354-388 | B3 (key API makes empty input unrepresentable) |
| U01-014 | confirmed | Wifi.cs:646-649 ignores status | B3 |
| U01-015 | confirmed | Wifi.cs:284-319 | B3 |
| U01-016 | confirmed | WindowsRadio.cs:373-379; Wifi.cs:494-544 | B3 |
| U01-017 | confirmed | WifiProfile.cs:210-215 | always hex plus name, B3 |
| U01-018 | confirmed | WifiWatch.cs:22-36 | docs, B2 |
| U01-019 | confirmed | Bluetooth.cs:413-422,518-534 | B4 |
| U01-020 | confirmed | Bluetooth.cs:406-410,452 | B4 |
| U01-021 | confirmed | CoreAudio.Watch.cs:77-98 | B2 |
| U01-022 | confirmed | CoreAudio.Watch.cs:229-256 | B2 |
| U01-023 | confirmed | CoreAudio.Formats.cs:186-191 | B5 |
| U01-024 | confirmed | CoreAudio.Native.cs:25-38; Bluetooth.cs:159-191 | B5 (enumerator), B2 (watcher abort) |
| U01-025 | confirmed | Backlight.cs:62-64 | B5 |
| U01-026 | confirmed | WaveOutFeedback.cs:33-48 | B5, no lock (C28) |
| U01-027 | confirmed | DisplayLayouts.cs:378-399 | B6 |
| U01-028 | confirmed behavior, refuted as defect | DisplayLayouts.cs:318-326 | document, no parameter (C23), B10 |
| U01-029 | confirmed | DisplayTopology.cs:33-67,223-249 | waits replaced by moved settle waiter, B8 |
| U01-030 | confirmed | DisplayTopology.cs:19-31,86-109 | B6 |
| U01-031 | confirmed | DisplayModes.cs:129,135,154,175,247 | typed result, B7 |
| U01-032 | confirmed | DisplayModes.cs:231-233 | doc fix, B7 |
| U01-033 | confirmed | DisplayColor.cs:186-187 | no change, evidence gate |
| U01-034 | confirmed | DisplayEdid.cs:39-41 | B6 |
| U01-035 | confirmed | DisplayModes.cs:45 only gate | static shared gate, B6 |
| U01-036 | confirmed | DisplayLayoutPlanner.cs:20-66; DisplayScaling.cs:59-99; DisplayColor.cs:219-260; DisplayModes.cs:129-175; DisplayLayouts.cs:258-339; PowerRequestList.cs:72-87; CoreAudio.cs:256 | codes in WDC, identical strings in WSGM, B7/B9/B5 |
| U01-037 | confirmed | WindowsWakeSecurity.cs:66-82 | B9 |
| U01-038 | confirmed | ModernStandby.cs:320-330 | B9 |
| U01-039 | confirmed | WindowsPower.Notifications.cs:91-124 | SafeHandle registrations, B9 |
| U01-040 | confirmed | see inventory 4.3 | per-API decisions in 4.3; Intel/Lab CCD duplicates accepted (C36) |
| U01-041 | confirmed | RadioManagerTests.cs:90-160 | move to WDC suite, B1 |
| U01-042 | confirmed | Tests csproj L4 | B1 |
| U01-043 | confirmed | WlanNative.cs:197-307; CoreAudio.Native.cs:117-138; DisplayTopology.Native.cs:89-161 | B1 |
| U01-044 | confirmed | WindowsRadioTests.cs:10-31,134-139; CoreAudioTests.cs:319-324 | B1 |
| U01-045 | confirmed | no child props/editorconfig | B1 |
| U01-046 | confirmed | csproj L40 | 0.2.0 in B2 |
| U01-047 | confirmed | Wifi.cs:417; CoreAudio.Bluetooth.cs:97 | ExceptionDispatchInfo, B3/B5 |
| U01-048 | confirmed | Wifi.cs:235,619; WifiRules.cs:398-402; WindowsStorage.cs:46,85-86; CoreAudio.Spatial.cs:219-230 (`ToWinRtDeviceId` capture arm only reached by tests) | remove, B3/B5/B9 |
| U01-049 | confirmed | WindowsRadio.cs:260 vs Wifi.cs:516-519 | doc, B3 |
| U01-050 | confirmed | WindowsRadio.cs:198 vs RadioPanel.axaml.cs:301-302 | neutral doc, B4 |
| U01-051 | confirmed | Bluetooth.cs:468-507 | B4 |
| U01-052 | confirmed | Bluetooth.cs:356-358 | removed by async API, B4 |
| U01-053 | confirmed | WifiProfile.cs:48-51,87-90,194-203 | retain public, fix docs and null check, B3 |
| U01-054 | confirmed | WifiRules.cs:36-40 | doc, B3 |
| U01-055 | confirmed | WifiWatch.cs:51-55,108-111 | ACM only, B2 |
| U01-056 | confirmed | WlanNative.cs:144-167; WifiWatch.cs:248-257 | idempotent owner, B2 |
| U01-057 | confirmed | WlanNative.cs:85-93 | zeroed `ref`, B3 |
| U01-058 | confirmed | WindowsPowerRequest.cs:203-208 | full union size, B9 |
| U01-059 | confirmed | CoreAudio.Formats.cs:185-187; Native.cs:445-446 | pass NULL, B5 |
| U01-060 | confirmed (not a high-rate path) | CoreAudio.Watch.cs:320; WifiWatch.cs:107,190,203; WaveOutFeedback.cs:108 | direct blittable reads where touched, B2/B5 |
| U01-061 | confirmed | CoreAudio.cs:187-192; CoreAudio.Bluetooth.cs:29-53 | docs only; no new mute overload (no consumer) |
| U01-062 | confirmed | CoreAudio.cs:202-203; Spatial.cs:398 | B5 |
| U01-063 | confirmed | DisplayLayouts.cs:224-230,426-457; Types.cs:347 | B7 |
| U01-064 | confirmed | DisplayTopology.cs:72-109; DisplayLayouts.cs:248-259 | moot after cap removal (WDC-007); B6 |
| U01-065 | confirmed | DisplayLayouts.cs:346-372,459-486 | B6 |
| U01-066 | confirmed | Backlight.cs:8-11,62; PowerRequestList.cs:31-39; CoreAudio.Native.cs:92-96 | B10 |
| U01-067 | confirmed | csproj L17-19,31-33 | B1 |
| U01-068 | confirmed | AGENTS.md L26-28,133,223-227 | guidance proposal, B10 (sign-off) |
| U01-069 | confirmed | WindowsRadio.cs:9-13 | B10 |
| U01-070 | confirmed | README.md L43,60-64,78,93-107,327-333 | B10 |
| U01-071 | confirmed | csproj L36-47 | B10 |
| U01-072 | confirmed | .coderabbit.yaml L32-82 | guidance proposal, B10 |
| U01-073 | confirmed | .gitignore | B1 |
| U01-074 | confirmed | Wifi.cs:297; Interop.cs:34-50; ModernStandby.cs:396-407; WaveOutFeedback.cs:177-180 | typed code spaces where touched, B3/B5/B9 |
| U01-075 | confirmed | WindowsWakeSecurity.cs:58-63,129-132 | DC-only fix, refuse non-DWORD (C27), B9 |
| U01-076 | confirmed | ModernStandby.cs:320-330,415-443 | B9 |
| U01-077 | confirmed | WindowsStorage.cs:54-93 | B9 |
| U01-078 | confirmed | WindowsPower.cs:15-45; Policy.cs:254-267 | B9 |
| U01-079 | confirmed | AudioFilePreview.cs:15-29 | B5 |
| U01-080 | confirmed | DisplayLayoutTests.cs:44-79; PowerRequestListTests.cs:15-41 | B1/B9 |
| U01-081 | confirmed | WaveOutFeedback.cs:211-221 | reuse `CoreAudio.WaveFormat`, B5 |
| U01-082 | confirmed | CoreAudio.Native.cs:75-77; CoreAudio.cs:246-248 | with touched code |
| U01-083 | confirmed | Power.cs:121-126 | B4 |
| U01-084 | confirmed | WifiWatch.cs:44-61 | moot with per-registration, B2 |
| U01-085 | confirmed | PowerRequestList.cs:127-216 | B9 |
| A01-F001 | confirmed, implemented | as U01-004 | B0 |
| A01-F002 | confirmed | as U01-009 | B1 |
| A01-F003 | confirmed | as U01-001 | B2 |
| A01-F005 | confirmed | WifiWatch.cs:248-257 | B2 |
| A01-F006 | confirmed | DisplayTopologyTests.cs:109-117 | B8 (test removed with waits) |

### 2.2 New and re-scoped findings

**WDC-001 (medium, NEW): WDC display records are WSGM's persisted config and recovery wire format.**
`src/WSGM/Core/GameModeLaunchConfiguration.cs:52,132,138,147,176` store `DisplayTargetIdentity` and `DisplayLayout`
(and through it `DisplayLayoutOutput`, `DisplayRefresh`) directly in `config.json`. `PendingReturnLayout` (L176) is
Windows recovery state consumed by `GameModeReturnRecovery.cs:60-67`. The JSON context
(`AppConfig.cs:1050` `[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]`) also
writes the computed `IsPrimary` and `Hertz` properties. No fixture pins this shape anywhere (grep for
`"AdapterLowPart"`/`"IsPrimary"` in tracked files: none). The plan's "breaking API allowed" (requirements 10)
collides here with "preserve recovery originals" (requirements 11): renaming or retyping any of these record
members silently breaks migration. Recommendation: declare these four records serialization-stable in WDC XML docs
and README (member names, types and order frozen for 0.2.0), add a WDC test that pins their public property set by
reflection, and have the config domain add a real-shaped `config.json` round-trip fixture. Do not introduce WSGM-side
DTO copies (that is a compatibility layer with no defect behind it).

**WDC-002 (medium, NEW, cross-domain): `DisplayLayouts.Describe` deletes stored preferences.**
`src/WSGM/Core/ConfigStore.cs:989-996` drops `GameLayout`/`DesktopLayout` whenever `Describe` returns a reason
(called at L891-892). Any tightening of library rules, including the arbitrary 16-display cap
(`DisplayLayoutPlanner.cs:27-30`), silently erases user configuration, which requirements 11 forbids ("never resets
configuration"). WDC side: remove the 16 cap (WDC-007) and treat `Describe` rule changes as migration-relevant in
the library docs. Config side (other domain): preserve and report instead of nulling.

**WDC-003 (low, NEW): rollback after a definite refusal.**
`DisplayModes.cs:158-172`: when `Change(..., 0)` returns non-zero, the code still writes the original mode back
(`same && Change(path.SourceName, ref original, 0)`). `DisplayLayouts.cs:318-339`: when the apply `Supply` returns
non-zero, it supplies the rollback configuration and reapplies output extras, then reports `Unconfirmed`, whose doc
says "Windows accepted it but the readback did not match" (L106). A refused SetDisplayConfig/CDS call changed
nothing; the extra write can blank displays and is an unrequested second write. Recommendation: non-zero apply
status returns `Refused` with the native status and no rollback; rollback only for status 0 plus confirmed mismatch
on a still-verified route (matches plan C21).

**WDC-004 (medium, NEW, related U01-031/U01-063): set results conflate "written, readback differs" with "refused".**
`DisplayScaling.cs:88-100` and `DisplayColor.cs:248-261` return `false` both when Windows refused and when the write
succeeded but readback did not match; `DisplayModes.Apply` does the same through `Applied=false`. WSGM logs both as
"failed" (`src/WSGM/Core/DisplayScale.cs:258-264`). This bakes a readback gate into the contract, contrary to the
maintainer rule "never gate success on readback; publish the written value as observed". Recommendation: typed
results with an outcome enum `{ AlreadySet, Written, Confirmed, Refused, NotActive, Unsupported, Unreadable }` plus
native status. `Written` means status 0 without matching readback; consumers decide.

**WDC-005 (low, NEW): four container normalizers, and the library ones merge empty containers.**
`WindowsRadio.Bluetooth.cs:122-142` (`BluetoothIdentity`, Guid "D"), `:574-594` (`ReadBluetoothDevice`, `Trim` +
lower-case string), `CoreAudio.Bluetooth.cs:69,147` (another trim/lower and `GuidValue` "D"), and WSGM
`BluetoothDeviceCatalog.cs:105-108` (Guid parse, `Guid.Empty` excluded). The library forms keep
`00000000-0000-0000-0000-000000000000`, so `ListBluetoothDevices` (`Bluetooth.cs:56-67`) merges every endpoint
with an empty container into one row and `ConnectedBluetoothCount` (`:109-119`) counts them once. WSGM's explicit
`Guid.Empty` exclusion shows the case occurs. Recommendation: one internal `NormalizeContainer(object?)` (parse,
`Guid.Empty` and garbage to `""`, "D" format) used by all four call sites; delete WSGM's copy.

**WDC-006 (low, NEW): wake-security Restore guesses originals.**
`WindowsWakeSecurity.cs:85-117`: `Restore(null)` and a snapshot with no schemes write `1/1` to every scheme and
delete policy values. WSGM depends on this when nothing was captured (`LockScreenSettings.cs:95-104` builds
`PolicyExisted=true`, `-1`, empty schemes). This is product policy and an invented original inside the library.
Recommendation: `Restore` touches only captured entries; WSGM explicitly composes "restore Windows defaults" from
the new primitives when it has no snapshot (same writes, same UI).

**WDC-007 (low, NEW): arbitrary count/length limits (no-arbitrary-limits rule).**
- `DisplayTopology.cs:72-79` `ValidateBufferCounts` refuses more than 4096 paths / 8192 modes; Windows' own sizing
  call is authoritative and allocation follows it. Documented in README L147-150 and tested
  (`DisplayTopologyTests.cs:12-27`).
- `DisplayLayoutPlanner.cs:27-30` refuses layouts with more than 16 displays (and via WDC-002 deletes them).
- `DisplayModes.cs:75,138,204` stop enumerating at index 4096, silently truncating supported/primary mode lists.
- `WindowsRadio.WlanNative.cs:29-38` truncates to the bound when `rejection` is null (callers `Wifi.cs:649` scan
  facts, `Wifi.cs:719` profile list); interface list throws above 64 (`:180-182`).
- `WindowsPower.HybridCores.cs:242` stops at 64 published values.
- `PowerRequestList.cs:45-52,291`: `MaxBuffer` 1 MiB, `MaxRequests` 100 000, `MaxStringUnits` 4096; a long process
  path makes the whole list unknown.
- `ModernStandby.cs:102,418` (already U01-076).
Recommendation: type and bounds checks only: `checked` conversions, the native buffer length, `Array.MaxLength`;
loops end on the native end-of-list status. `FindFreeProfileName`'s 64-suffix search (`WifiRules.cs:387`) and
`ReadSchemeName`'s three read attempts are refusals, not truncation; keep. The DPI 100-500 and 320x200..32768
bounds are Windows domain ranges; keep.

**WDC-008 (low, NEW): `RespondToPairing` forwards a PIN to every ceremony.**
`WindowsRadio.Bluetooth.cs:477-486` calls `Accept(pin)` whenever a non-empty PIN is given, but the doc (L462-466)
says the PIN is ignored except for `ProvidePin`. Recommendation: pass the PIN only for `ProvidePin` and reject an
empty PIN there (U01-051).

**WDC-009 (nit, NEW): `AudioFilePreview` drops native detail and races `_player`.**
`AudioFilePreview.cs:136-152` raises only `args.ErrorMessage`, losing `MediaPlayerError` and `ExtendedErrorCode`
(HRESULT), against the plan rule "never erase native code-space detail"; `_player` is read on the media thread
(L139) and written by `Stop` on the owner thread without a barrier. Recommendation: event carries
`(MediaPlayerError Error, int HResult, string Message)`; read `_player` with `Volatile.Read`.

**WDC-010 (nit, NEW): stale `DisplayProfileResult` name.**
Commit 2f07485 removed the display profile API; the record (`DisplayTopology.Types.cs:328-339`) now only serves
`DisplayModes.Apply`. Rename to `DisplayModeResult` together with the typed outcome (B7).

**WDC-011 (nit, NEW): WSGM redeclares GUIDs the library publishes.**
`src/WSGM/Core/PowerTimeouts.cs:37` (`SubSleep` = `ModernStandby.SubgroupSleep`),
`src/WSGM/Interop/WindowsCpuBoostApi.cs:28` (= `WindowsPower.SubgroupProcessor`). Use the library constants.

**WDC-012 (nit, NEW): W02_01 left mixed line endings.**
`git ls-files --eol` reports `w/mixed` for `DisplayTopology.cs` and `DisplayTopologyTests.cs` (CR 248 vs LF 269;
CR 43 vs LF 118): the new lines are LF in CRLF files. `core.autocrlf=true` normalizes the index, but Rider cleanup
and format diffs will show noise. Normalize before the B0 commit.

**WDC-013 (low, NEW): W02_02 over-specifies the seam** (C30). Two interfaces, a nested native process wrapper and
a "blocking fake proves a dispatched suspend is not undone" test that only exercises `Task.Run`. The concrete
defect needs one port and four cases.

**WDC-014 (low, NEW): plan requirement "callback-initiated disposal is safe" is unattainable for WLAN without new
mechanism** (C8). `WlanRegisterNotification` must not be called from its callback. Document the rule instead.

**WDC-015 (low, NEW): `DisplayEdid.ReadModes` timeout is indistinguishable from cancellation.**
`DisplayEdid.cs:39-41`: the internal three-second `CancellationTokenSource` surfaces as
`OperationCanceledException`, there is no caller token, and a null monitor throws `NullReferenceException`
(U01-034). Called on the Settings path (`SettingsViewModel.cs:263`). Recommendation: return
`(IReadOnlyList<DisplayMode> Modes, DisplayEdidStatus Status)` with `NotFound`/`TimedOut`/`NoDescriptor`; keep the
three-second lookup bound (it is a native wait, not a count limit).

**WDC-016 (nit, NEW): unsynchronized lazy selector cache.** `Bluetooth.cs:103-108` `_connectedSelectors ??=` is a
benign race; the cache saves two string builds. Remove it (compute per call) rather than add `Lazy`.

**WDC-017 (nit, NEW): connection verdict accepts any completion without a profile name.**
`WifiWatch.cs:198-217`: when the payload is short or the profile name empty, any ACM completion on the adapter
decides the verdict, including an unrelated auto-connect. Keep the behavior (Windows normally fills the name) but
document it in the internal type; the `IsConnectedTo` byte check (`Wifi.cs:292,314`) already backs up success.

**WDC-018 (low, NEW, extends U01-044/U01-080): tests that copy literals, predicates or limits.**
`ModernStandbyTests.cs:239-260` re-states the same GUID literals the source declares; `HybridCoreTests.cs:84-97`
tests `Classes.Count > 1`; `PowerRequestListTests.cs:74-83` copies the `ModeCount` switch;
`DisplayTopologyTests.cs:12-27` tests the arbitrary cap; `WindowsRadioTests.cs:134-139` has no assertion and
mutates the process-global pairing table; `CoreAudioTests.cs:319-324` tests a single bit. Remove or replace with
behavior tests (B1, B9).

**WDC-019 (nit, NEW): library docs prescribe readback confirmation as caller policy.**
`WindowsPower.cs:125`, `WindowsPower.Policy.cs:262`, `WindowsPower.HybridCores.cs:140-141`, README L222-224 tell
callers to "read back to confirm". Neutral wording: "the request is issued once; a read reports what Windows
stores".

**WDC-020 (nit, NEW): no standalone child solution or CI.** The child has no `.sln` and no workflow; WSGM.slnx
(L4, L40) is the only aggregate, so `eng/verify.ps1` runs the WDC suite (including U01-009 until B1). No new CI is
proposed (maintainer: no unrequested infrastructure); the independent validation is the two documented local
commands, run per framework after B1.

**WDC-021 (low, NEW): `DisplayModes.Read` probes every mode under the display gate.**
`DisplayModes.cs:53-113` holds `Gate` while issuing one `CDS_TEST` per distinct enumerated mode (potentially
hundreds of driver calls), called from Settings (`SettingsViewModel.cs:263`). With a single shared write gate
(C16) this would block layout/scaling writes for that whole time. Recommendation: `Read` takes no write gate (tests
change nothing; the route recheck at L96-109 already detects interleaving); only `Apply` and the transient primary
writes take it.

**WDC-022 (nit, NEW): `WifiProfile.TryReadSsid` is substring parsing.** `WifiProfile.cs:156-185,223-234`. Works for
Windows-authored profiles. Keep (simplify), but add fixtures exported from real Windows profiles (hex form, escaped
name, multi-SSID) in the moved WifiProfile tests.

**WDC-023 (low, NEW, extends U01-036): library English text is visible UI text.** `DisplayModeView.cs:209` shows
`DisplayModes.Apply().Detail`; `DisplayLayoutEditor.cs:763` shows `Describe`; overlay power status reads
`PowerRequestList` errors (`OverlayController.Power.cs:145-158`, log only); `CoreAudio.cs:256` supplies the
"Audio device" name shown in audio lists. Moving wording out of WDC therefore requires WSGM tables reproducing
the current strings byte for byte (B7 lists them).

**WDC-024 (nit, NEW): `Restore` can throw on a policy key with subkeys.** `WindowsWakeSecurity.cs:90`
`DeleteSubKey(PolicyKey, false)` throws `InvalidOperationException` when the key gained subkeys. Delete the two
values instead and remove the key only when empty.

**WDC-025 (low, NEW, extends U01-065): layout rollback state comes from two queries at different instants.**
`DisplayLayouts.cs:309-310` captures `Query(OnlyActivePaths)` for the native rollback and `Capture()` (a separate
`QDC_ALL_PATHS` observation with per-path scaling/colour reads) for the extras. After a hotplug between them the
extras restore can disagree with the native rollback. Derive both from one `QDC_ALL_PATHS` query (active subset).

**WDC-026 (nit, NEW): two safety tests rely on argument-check order to stay off native code.**
`WindowsRadioTests.cs:19-23` (`GetPower` would enumerate radios if validation moved) and A01-F006. Covered by
moving validation into pure helpers tested directly (B1) and by deleting the waits (B8).

Findings count: 85 ledger U01 rows confirmed (1 already fixed, 1 refuted as defect), 4 A01 confirmations, 26 WDC
findings (24 NEW, 2 re-scoped).

---

## 3. Plan refinements

### 3.1 Over-engineering to remove (simplify / no-arbitrary-limits)

| Plan mechanism | Why it over-engineers | Simpler shape |
| --- | --- | --- |
| Five per-instance services with injectable native adapters (RP L121, TB W01/W02 step 1) | No defect needs instance lifetimes; only the global feed slots and untestable write orchestration are defects. | Static facades; `Start*Watch` returns `IDisposable`; internal ports `IWlanApi`, `IDisplayConfigApi`, `IPowerActionApi` with a static native instance (pattern of `NativePowerRequestApi`); pure restore planners (pattern of `ModernStandby.RestorePlan`). |
| Fan-out "multiple consumers subscribe to the same service" (RP L121) | A hub with subscriber lists is new state. | Independent native registrations per call. |
| "Callback delivery outside the state lock" + "callback-initiated disposal is safe" (RP L121) | Requires reentrancy detection and deferred unregister. | Per-registration gate; documented "post, do not dispose from inside a Wi-Fi callback". |
| One injected DisplayService per host (RP L127) | Does not serialize plugins sharing the assembly; adds wiring. | One internal static write gate (WDC-021 keeps reads out of it). |
| Persistence parameter on layout apply (RP L127) | One behavior, one caller. | Fixed `SDC_SAVE_TO_DATABASE`, documented. |
| EDID "upgrade missing fields after unambiguous resolution" migration (RP L103) | Nothing to migrate; path-first matching already preserves saved identities. | No migration; W02_01 test is the evidence. |
| Capture registry value kinds exactly (RP L129) | Changes persisted recovery shape for an unobserved case. | Refuse non-DWORD at Capture (typed failure, no mutation); fix DC-only logic. |
| WaveOut Play/Dispose lock (RP L129) | Single owner already serializes. | Document single-owner; fix retained buffers only. |
| "90 s active deadline" for pairing (RP L125) | Would pull an active clock into WDC. | Wall-clock 90 s plus caller token. |
| Shared CCD interop for Intel and Lab (U01-040, RP L47) | New dependency and public lookup for one call. | Accepted duplication, documented. |
| Move Bluetooth catalog into WDC (RP L127) | Catalog holds product policy. | Move only container normalization (WDC-005). |
| W02_02 two interfaces + process wrapper + 12 tests | Mechanism beyond the defect. | One port, four tests (3.2). |
| Library caps (WDC-007) | No-arbitrary-limits rule. | Native counts and type checks only. |
| Token overloads on every blocking call (U01-011 direction) | API churn without consumer need; WSGM runs these on workers. | Token on pairing only; document blocking calls. |
| `SetMuted(AudioDirection, bool)` (U01-061 direction) | No consumer. | Docs only. |

### 3.2 Changed batch: W02_02 (replace its spec)

`WindowsPower.Actions.cs`: add internal `interface IPowerActionApi { bool Suspend(bool hibernate); Task<int>
RunToolAsync(ProcessStartInfo start, CancellationToken cancellationToken); }` and a private static
`NativePowerActionApi` (calls `SetSuspendState`, throws `Win32Exception(GetLastPInvokeError)` on false; starts
the process, awaits exit, returns `ExitCode`, disposes it). Internal overloads `SuspendAsync(bool, IPowerActionApi,
CancellationToken)` (cancellation re-checked inside the delegate immediately before `Suspend`) and
`RequestActionAsync(WindowsPowerAction, IPowerActionApi, CancellationToken)` (validate action, check token, run,
non-zero exit throws the existing `Win32Exception`). Public overloads delegate. Tests replace
`CancelledActionsNeverDispatch`: cancelled token yields zero port calls (both paths); action arguments forwarded
unchanged; non-zero exit throws with the exit code; native `Suspend` failure propagates once. About 120 changed
lines instead of about 300.

### 3.3 Additions

1. Wire-stable display records and a reflection pin test (WDC-001); config-domain round-trip fixture.
2. No rollback after definite refusal (WDC-003); typed set outcomes with `Written` (WDC-004).
3. One container normalizer; WSGM catalog keeps policy (WDC-005).
4. Restore never guesses; WSGM composes defaults (WDC-006).
5. Remove every cap listed in WDC-007, including README and test updates.
6. Exact-string mapping tables in WSGM for every library string that reaches UI (WDC-023).
7. `DisplayModes.Read` outside the write gate (WDC-021).
8. Rollback state from one query (WDC-025).
9. Consumer simplification: RadioManager drops `_bluetoothWatchGeneration` and Stop/Start pairs
   (`RadioManager.cs:485-491,680-712`) once registrations are disposables.
10. Version 0.2.0 at the first breaking batch (B2), not at the end.
11. Library AGENTS.md "two raw integer contracts" (L71-77) needs a human-approved edit because typed outcomes
    wrap the codes (open question 1). Until then the typed results keep the raw code as a named field so the
    existing rule ("do not discard platform detail") still holds.

### 3.4 Removals from the plan

- RP L103 EDID "upgrade" sentence.
- RP L127 "injected service per host" and "persistence is an explicit argument".
- RP L121 "callback-initiated disposal is safe" and "deliver outside locks" for Wi-Fi.
- RP L129 "serializes Play/Dispose"; "capture registry kinds/data exactly" (replace with refuse).
- AI: "Intel display/CCD integration" and "Device Lab" from WDC consumers; add WSGM.UiTests.
- TB W01 `WifiProfileTests` filter (until B1 creates it) and the stale "barred until W02" clause.

---

## 4. Target design

### 4.1 Owners

| Owner (WDC) | Responsibility | State |
| --- | --- | --- |
| `WindowsRadio` (static facade) | radio power, Wi-Fi, Bluetooth discovery/pairing | caches only: radio list (TTL), WLAN status client, parsed saved profiles, pending pairing deferrals by token |
| `WifiWatchRegistration` (private, returned as `IDisposable`) | one WLAN client handle + ACM registration + rooted delegate | per registration, idempotent dispose |
| `BluetoothWatchRegistration` (private, returned as `IDisposable`) | one `DeviceWatcher`, handlers, records | per registration; handlers revoked under its gate |
| `IWlanApi` / `NativeWlanApi` (internal) | WLAN calls used by connect/forget/profile orchestration | none |
| `CoreAudio` (static facade) | endpoints, roles, volume, watches, spatial, formats, BT audio | cached enumerator, dropped on disconnect HRESULTs |
| `Backlight`, `WaveOutFeedback`, `AudioFilePreview` | as today | `WaveOutFeedback` keeps retained buffers after failed close |
| `DisplayTopology` (static) | CCD query, target identity, native declarations, the internal static display write gate | none |
| `IDisplayConfigApi` / native (internal) | `Query`, `Supply`, `ReadTarget` for orchestration tests | none |
| `DisplayLayouts` | observe/capture/describe/validate/apply, `WaitForTargetsAsync` (moved settle waiter) | none |
| `DisplayLayoutPlanner` (internal) | pure planning | none |
| `DisplayModes`, `DisplayScaling`, `DisplayColor`, `DisplayEdid` | as today with typed results | none |
| `WindowsPower` | schemes, settings, overlays, actions (`IPowerActionApi`), hybrid cores, notification registrations | none |
| `WindowsPowerRequest` | as today | per instance |
| `PowerRequestList` | decode with typed status | none |
| `WindowsWakeSecurity` | capture, three primitives, per-item restore via pure plan | none |
| `ModernStandby` | as today; restore aggregates per device from one enumeration | static gate around DevicePowerOpen/Close (kept, native list is process-global) |
| `WindowsStorage` | describe volumes (skips network), disk numbers | none |
| `WifiProfile` | pure XML authoring/parsing | none |

### 4.2 Files

| File | Change |
| --- | --- |
| `WindowsRadio.WifiWatch.cs` | static `_wifiWatch`, `WifiWatchLock`, `DetachWatch`, `StopWifiWatch` deleted; `WifiWatch` becomes `WifiWatchRegistration : IDisposable`; MSM dropped |
| `WindowsRadio.Bluetooth.cs` | static `_bluetoothWatch`, `BluetoothWatchLock`, `StopBluetoothWatch(Core)` deleted; registration class; `PairBluetoothAsync`; `NormalizeContainer`; `_connectedSelectors` deleted |
| `WindowsRadio.WlanNative.cs` | `IWlanApi` + `NativeWlanApi`; `WlanClient` idempotent; `ReadWlanList` without truncation |
| `WindowsRadio.Wifi.cs` | key-based connect/forget, typed result, scan status, uses `IWlanApi` |
| `WindowsRadio.WifiRules.cs` | merge rules (bytes+security), connect decision helpers (pure) |
| `CoreAudio.Watch.cs` | ownership transfer fix, interlocked `EndpointWatch.Dispose` |
| `WaveOutFeedback.cs` | private `WaveFormat` deleted, uses internal `CoreAudio.WaveFormat` |
| `DisplayTopology.cs` | `WaitForPresentAsync`, `WaitForAvailableAsync`, `PollAsync`, `PollInterval`, `Exists`, `ValidateBufferCounts` deleted; `WriteGate`; unreadable-skip |
| `DisplayTopology.Types.cs` | `DisplayWaitOutcome` deleted; `DisplayProfileResult` renamed `DisplayModeResult` with outcome enum; new result enums |
| `DisplayLayouts.cs` | `WaitForTargetsAsync`, `DisplayArrangement.Missing`; rotation; refusal path; one-query rollback |
| `WindowsWakeSecurity.cs` | `DisableSignIn` replaced by three primitives; `Restore` per item |
| `WindowsPower.Notifications.cs` | returns `PowerNotificationRegistration` (SafeHandle) |
| WSGM `Shell/DisplayArrivalWaiter.cs` | dissolved (table below) |

New files: none required (all additions fit existing partials). Tests: new `WifiProfileTests.cs`,
`NativeLayoutTests.cs`, `WifiConnectTests.cs`, `DisplayApplyTests.cs`, `DisplayWaitTests.cs`,
`WakeSecurityRestoreTests.cs` in the WDC suite.

**Old symbol to new owner: WSGM `DisplayArrivalWaiter.cs`**

| Old symbol | New owner |
| --- | --- |
| `IDisplayPresence.Observe` | removed; WDC waiter calls `DisplayLayouts.Observe` (internal `Func<DisplayArrangement>` overload for tests) |
| `IDisplayChangeSignal.WaitForChangeAsync` | stays in WSGM (its MessageWindow-backed implementation); passed to WDC as `Func<TimeSpan, CancellationToken, Task>` |
| `DisplayArrivalWaiter.WaitAsync` | `DisplayLayouts.WaitForTargetsAsync(targets, waitForChange, cancellationToken)` |
| `Settle` (500 ms), `Backstop` (5 s) | private constants in `DisplayLayouts` |
| `Present` | `DisplayArrangement.Missing(targets).Count == 0` |
| `Missing` | `DisplayArrangement.Missing(targets)` (also used by `DisplayLayouts.Run` absent check, `DisplayLayouts.cs:261-265`) |
| `TryObserve` (Win32Exception/InvalidOperationException = not settled) | private in WDC waiter (Win32Exception only after caps removed) |
| `delay` ctor parameter | internal overload parameter for tests |
| `tests/WSGM.Tests/Shell/DisplayArrivalWaiterTests.cs` | moved to WDC `DisplayWaitTests.cs` |

**WDC `DisplayTopology` waits**

| Old | New |
| --- | --- |
| `WaitForPresentAsync`, `WaitForAvailableAsync`, `PollAsync`, `PollInterval`, `Exists<T>` | replaced by `DisplayLayouts.WaitForTargetsAsync` (availability; active presence is confirmed by `Apply` readback) |
| `DisplayWaitOutcome` | deleted (waiter ends only on success or cancellation) |
| test `AvailableWaitRejectsInvalidDeadlineAndCancelsBeforeNativeQuery` | replaced by fake-observer waiter tests |

**`WindowsRadio` watch statics**

| Old | New |
| --- | --- |
| `_wifiWatch`, `WifiWatchLock`, `DetachWatch`, `StopWifiWatch` | per-registration object; caller disposes |
| `_bluetoothWatch`, `BluetoothWatchLock`, `StopBluetoothWatch`, `StopBluetoothWatchCore` | per-registration object; `Dispose` revokes handlers under its gate, then stops the watcher |
| `PublishBluetoothChange`, `RaiseBluetoothChange` | registration methods |
| `PairBluetooth(callback)` + `onFinished` | `Task<PairingResult> PairBluetoothAsync(string, Action<PairingRequest>, CancellationToken)`; consumer exceptions from `onRequest` complete the deferral and fault the task |
| `_connectedSelectors` | computed per call |

**Others**

| Old | New |
| --- | --- |
| `WindowsWakeSecurity.DisableSignIn` | `SetConsoleLockPolicy(int ac, int dc)`, `SetSchemeConsoleLock(Guid, int ac, int dc)` (+ `EnumerateSchemes`), `SetNoLockScreen(bool)`; WSGM `LockScreenSettings.ApplyDirect` composes the same order (policy, schemes, refresh, NoLockScreen) |
| default-restore branch of `Restore` (`WindowsWakeSecurity.cs:111-117`) | WSGM `LockScreenSettings` when no snapshot was captured |
| `WaveOutFeedback.WaveFormat` | `CoreAudio.WaveFormat` (internal, Pack=2) |
| WSGM `BluetoothDeviceCatalog.Container` | WDC normalized `BluetoothDevice.Container` |
| `WindowsPower.Register*Notification` / `Unregister*` | `PowerNotificationRegistration` returned by `RegisterSettingNotification`/`RegisterSuspendResumeNotification`; `Dispose` unregisters |
| `WindowsPower.EnumerateScheme(uint)` | `WindowsPower.EnumerateSchemes()` (public, existing internal body `WindowsPower.cs:36-45`) |
| `DisplayLayoutPlanner.Describe` strings | `DisplayLayoutProblem?` enum; strings in WSGM |
| WSGM tests of WDC contracts (`RadioManagerTests.cs:90-160`) | WDC `WindowsRadioTests`/`WifiProfileTests` |

### 4.3 Public API inventory and decisions

Decision key: R retain, C change (signature or semantics), I internalize, X remove (always replaced, never for
non-use). "Used" lists production WSGM use (`src/`, `tools/`).

| Type / member | Used | Decision |
| --- | --- | --- |
| `WindowsRadio.Access`, `Consent`, `PairingKind`, `PairingOutcome`, `Power`, `RadioKind`, `WifiConnectionState`, `WifiFailureKind`, `WifiSecurity`, `WifiWatchEvent` | yes (Consent no) | R (docs: U01-050 neutral `PairingKind.Unknown`) |
| `WindowsRadio.BluetoothChangeKind` | yes | C: add `Stopped` (watcher aborted; feed is dead) |
| `WindowsRadio.WifiNetwork` | yes | C: add `WifiNetworkKey Key` (raw bytes); `Ssid` stays display text |
| `WindowsRadio.WifiStatus` | yes | R (add `Key` of the joined network) |
| `WindowsRadio.BluetoothDevice`, `BluetoothChange`, `PairingRequest`, `PairingResult` | yes | R (`Container` normalized) |
| new `WindowsRadio.WifiNetworkKey` | | C (readonly struct, value equality over bytes, `Hex`, `DisplayText`) |
| new `WindowsRadio.WifiConnectResult` | | C (`Outcome {Joined, Failed, Pending, Refused}`, `uint ReasonCode`) |
| `GetPower(RadioKind)` | yes | R |
| `RequestAccess()` | no | R (needed before offering toggles; documented use) |
| `SetPower(RadioKind, bool)` | yes | C: returns `RadioPowerResult(Access Access, IReadOnlyList<RadioAdapterResult> Adapters)`; no exception for partial application |
| `GetConsent(string)` | no | R (validate argument, U01-083) |
| `AggregatePower(IEnumerable<Power>)` | tests only | R (pure, documented rule) |
| `GetReasonVerdict(uint)` | yes | R |
| `GetWifiStatus()` | yes | C: returns `Unknown` state instead of throwing when no WLAN interface |
| `RequestWifiScan()` | yes | R (doc exceptions) |
| `ListWifiNetworks()` | yes | C: merges by bytes+security; `Key` populated; scan status failures reported, not "Unsupported" |
| `ConnectWifi(string, string?)` | yes | C: `WifiConnectResult ConnectWifi(WifiNetworkKey, string? passphrase)` |
| `DisconnectWifi()` | yes | R |
| `ForgetWifi(string)` | yes | C: `IReadOnlyList<WifiForgetResult> ForgetWifi(WifiNetworkKey)` per deleted profile |
| `ReasonText(uint)` | yes | R |
| `StartWifiWatch(Action<WifiWatchEvent>)` | yes | C: returns `IDisposable` |
| `StopWifiWatch()` | yes | X (replaced by registration `Dispose`) |
| `ListBluetoothDevices(bool)` | no | R (fix empty-container merge) |
| `ConnectedBluetoothCount()` | yes | R (fix) |
| `StartBluetoothWatch(Action<BluetoothChange>)` | yes | C: returns `IDisposable` |
| `StopBluetoothWatch()` | yes | X (replaced) |
| `PairBluetooth(string, Action<PairingRequest>, Action<PairingResult?, Exception?>)` | yes (RadioManager.cs:95) | X, replaced by `PairBluetoothAsync(string, Action<PairingRequest>, CancellationToken)` |
| `RespondToPairing(uint, bool, string?)` | yes | R (PIN only for ProvidePin; validate) |
| `UnpairBluetooth(string)` | yes | C: returns `DeviceUnpairingOutcome` (raw status kept); validates id |
| `WifiProfile.PskFlavor`, `CreateOpen`, `CreatePsk`, `TryReadSsid`, `PassphraseIsValid` | tests only | R (docs: no "ready for ConnectWifi"; null check; always hex plus name) |
| `CoreAudio.AudioDirection`, `AudioRole`, `VolumeCommand`, `AudioEndpointChange` | yes | R |
| `CoreAudio.AudioEndpoint`, `DefaultEndpointRoleResult`, `AudioEndpointWatchEvent`, `BluetoothAudioContainer`, `SpatialAudioState`, `AudioDeviceFormat` (+ `Pcm`) | yes | R (`AudioEndpoint.Name` empty when Windows has none; WSGM supplies "Audio device") |
| `CoreAudio.SpatialAudioFormats` (7 fields) | yes | R |
| `CoreAudio.SpatialAudioSetStatus` | yes | R (unknown native values map to `UnknownError`, U01-062) |
| `CoreAudio.UnsupportedFormat` | no | R (documented return value) |
| `ApplyCommand`, `GetVolume` x2, `SetVolume` x2, `SetMuted` | yes | R |
| `ListEndpoints` | yes | R (out list assigned only on success) |
| `SetDefaultEndpoint` x2 | yes | R |
| `StartVolumeWatch`, `StartEndpointWatch` | yes | R (fix ownership/dispose) |
| `GetSpatialAudio`, `SetSpatialAudio` | yes | R |
| `GetDeviceFormat`, `SetDeviceFormat`, `ListSupportedDeviceFormats` | yes | R (probe classification) |
| `ListBluetoothAudioContainers`, `SetBluetoothAudioConnection` | yes | R (normalized container; stack-preserving rethrow) |
| `Backlight.TryReadBrightness`, `TrySetBrightness` | yes | R (read level of the policy the driver reports) |
| `WaveOutFeedback.Open`, `Play`, `Dispose` | yes | R |
| `AudioFilePreview` ctor, `Play`, `Stop`, `Dispose` | yes | R |
| `AudioFilePreview.Failed` | yes | C: `Action<AudioPreviewFailure>` (error, HRESULT, message) |
| `DisplayTopology.CaptureActive()` | yes (also NVIDIA plugin) | C: skips unreadable paths instead of throwing (snapshot of readable active paths) |
| `DisplayTopology.WaitForPresentAsync`, `WaitForAvailableAsync` | no | X, replaced by `DisplayLayouts.WaitForTargetsAsync` (defective polling with a 10 min cap, U01-029) |
| `DisplayTargetIdentity` (+ `Matches`) | yes, persisted | R, wire-frozen |
| `ActiveDisplayPath`, `DisplayTopologySnapshot` | yes | R |
| `DisplayProfileResult` | yes | X, replaced by `DisplayModeResult(DisplayModeOutcome Outcome, int NativeStatus, bool RollbackAttempted, bool RollbackSucceeded)` |
| `DisplayWaitOutcome` | no | X (waits replaced) |
| `DisplayRefresh` (+ `Default`, `Hertz`, `FromHertz`, `ToString`) | yes, persisted | R, wire-frozen |
| `DisplayLayoutOutput` (+ `IsPrimary`), `DisplayLayout` | yes, persisted | R, wire-frozen |
| `DisplayTargetObservation`, `DisplayArrangement` | yes | R; add `Missing(IReadOnlyList<DisplayTargetIdentity>)` |
| `DisplayLayoutOutcome` | yes | C: add `Valid` (Validate success), `Refused` (apply status non-zero), `Unverified` (route vanished after write) |
| `DisplayLayoutResult` (+ `Applied`) | yes | C: `Warnings` become `IReadOnlyList<DisplayOutputWarning>` (target, kind, native status); `Detail` removed in favor of outcome + status |
| `DisplayLayouts.Observe`, `Apply` | yes | R (gate, rotation, refusal, one-query rollback) |
| `DisplayLayouts.Capture`, `Validate` | no | R (coherent primitives; Validate returns `Valid`) |
| `DisplayLayouts.Describe` | yes | C: returns `DisplayLayoutProblem?` |
| new `DisplayLayouts.WaitForTargetsAsync` | | C (moved waiter) |
| `DisplayScaling.TryRead`, `TryReadRange` | yes | R |
| `DisplayScaling.TrySet` | yes | C: `DisplaySetResult TrySet(target, percent)` with typed outcome (WDC-004) |
| `DisplayScaling.Snap` | no | R (pure, documented) |
| `DisplayColor.TryReadHdr` | yes | R |
| `DisplayColor.TrySetHdr` | yes | C: typed result as above |
| `DisplayMode`, `DisplayModeSnapshot`, `PrimaryDisplayMode` | yes | R |
| `DisplayModes.Read` | yes | R (no write gate) |
| `DisplayModes.Apply` | yes | C: returns `DisplayModeResult` |
| `DisplayModes.ReadPrimaryMode`, `EnumeratePrimaryModes`, `TestPrimaryMode`, `ApplyPrimaryModeTransient` | yes | R (doc fix U01-032; no 4096 cap) |
| `DisplayEdid.ReadModes` | yes | C: typed status (WDC-015) |
| `WindowsPower.EnumerateScheme(uint)` | yes | X, replaced by public `EnumerateSchemes()` |
| `WindowsPower.ReadSchemeName`, `GetActiveScheme`, `SetActiveScheme`, `ReadSetting`, `WriteSetting`, `GetEffectiveMode`, `SetActiveMode`, `TryGetStatus`, `RefreshActiveScheme`, `QueryHybridCores`, `ReadHybridCores`, `WriteHybridCores` | yes | R (docs WDC-019) |
| `WindowsPower.ReadPossibleValue` | no | R (primitive behind QueryHybridCores) |
| `WindowsPower.SubgroupProcessor`, `SettingHeterogeneousPolicy`, `SettingThreadSchedulingPolicy`, `SettingShortThreadSchedulingPolicy` | indirectly | R (WSGM duplicates replaced, WDC-011) |
| `WindowsPower.SuspendAsync`, `RequestActionAsync`, `WindowsPowerAction` | yes | R (internal port) |
| `WindowsPower.RegisterSettingNotification`, `RegisterSuspendResumeNotification` | yes | C: return `PowerNotificationRegistration` |
| `WindowsPower.UnregisterSettingNotification`, `UnregisterSuspendResumeNotification` | yes | X (replaced by `Dispose`) |
| `WindowsPowerStatus`, `HybridSchedulingPolicy`, `HybridCoreClass`, `HybridCoreSupport` (+ `Hybrid`), `HybridCoreState` | yes | R |
| `WindowsPowerRequest` (ctor, `IsHeld`, `Acquire`, `Release`, `Dispose`), `WindowsPowerRequestKind` | yes | R |
| `PowerRequestEntry` | yes | R |
| `PowerRequestList.Query()` | yes | C: `(IReadOnlyList<PowerRequestEntry>? Entries, PowerRequestListStatus Status, int NativeStatus)`; WSGM words the status |
| `WakeSecurityScheme`, `WakeSecuritySnapshot` | yes (mapped to config fields) | R |
| `WindowsWakeSecurity.Capture`, `IsSignInDisabled` | yes | R (DC-only fix; non-DWORD refused) |
| `WindowsWakeSecurity.DisableSignIn` | yes | X, replaced by three primitives |
| `WindowsWakeSecurity.Restore` | yes | C: returns `WakeSecurityRestoreResult` listing unresolved items; never writes uncaptured values |
| `WakeDeviceControl`, `WakeDevice`, `WakeDeviceSnapshot`, `StandbyTiming` (+ `Slept`, `SinceWake`), `ModernStandbySupport` | partly | R |
| `ModernStandby.Query`, `WasLastResumeUnattended`, `ReadStandbyTiming`, `EnumerateWakeDevices` | yes | R |
| `ModernStandby.TrySetWakeArmed`, `CaptureWakeDevices` | no | R (coherent snapshot/restore set) |
| `ModernStandby.RestoreWakeDevices` | no | C: returns per-device failures, one enumeration |
| `ModernStandby.SubgroupSleep`, `SubgroupNone`, `Setting*` (5) | indirectly | R |
| `StorageVolume`, `WindowsStorage.DescribeVolumes` | yes | R (skip network drives) |
| `WindowsStorage.DiskNumberFor(string)`, `DiskNumberFor(char)` | no (char used internally) | R |

No member is internalized. Removals are the nine replaced members marked X.

### 4.4 Consumers that must change

| WDC change | WSGM consumers |
| --- | --- |
| Watch registrations | `Shell/RadioManager.cs:485-491,680-712` (drop generation and Stop calls) |
| Wi-Fi key and results | `Shell/RadioManager.cs:597-612,893-950,1041-1095`, `Shell/RadioEntries.cs`, `Overlay/RadioPanel.axaml.cs`, `Shell/NativeQamNetworkService.cs:120`, `tests/WSGM.Tests/Shell/RadioManagerTests.cs`, UI tests touching radio rows |
| SetPower result | `Shell/RadioManager.cs:957-990` |
| Pairing async | `Shell/RadioManager.cs:60,95-100` delegate type and callers; `RadioManagerTests` fakes |
| Container normalization | `Shell/BluetoothDeviceCatalog.cs:86-88,105-108`, `BluetoothDeviceCatalogTests.cs` |
| `AudioEndpoint.Name` empty | `Shell/AudioManager.cs:483-484` and `Settings/AudioProfileEditor.cs:59` apply "Audio device" |
| `AudioFilePreview.Failed` | `Shell/SoundPackService.cs:314` |
| Display typed results and wording | `Overlay/DisplayModeView.cs:195-215`, `Core/DisplayProfiles.cs`, `Core/DisplayScale.cs:256-264`, `Settings/DisplayLayoutEditor.cs:763`, `Settings/SettingsViewModel.Displays.cs:115,185`, `Core/ConfigStore.cs:989-996`, `Shell/DisplayLayoutDiagnostics.cs`, `Shell/GameModeEntryServices.cs`, `Shell/GameModeEntryTransaction.cs`, `Shell/GameModeReturnRecovery.cs`, `Shell/ShellSession.Modes.cs`, matching WSGM.Tests and WSGM.UiTests |
| `CaptureActive` skips unreadable | `WSGM.Plugin.NvidiaGpu/NvApi.cs:343`, `Core/DisplayScale.cs:271`, `Overlay/DisplayModeView.cs:33` (behavior only) |
| `DisplayEdid` status | `Settings/SettingsViewModel.cs:263` |
| Waiter move | `Shell/DisplayArrivalWaiter.cs` (deleted), its callers in GameModeEntry services/transaction, `tests/WSGM.Tests/Shell/DisplayArrivalWaiterTests.cs` (moved) |
| Wake-security primitives | `Core/LockScreenSettings.cs:27-104`, `tests/WSGM.Tests/Core/WakeSecurityRecoveryTests.cs` |
| Power notification registrations | `Interop/MessageWindow.cs:92-93,276-290,330-360` |
| `EnumerateSchemes` | `Interop/WindowsPowerSchemeApi.cs:19` |
| `PowerRequestList` status | `Overlay/OverlayController.Power.cs:145-158`, `Core/WakeLockStatus.cs`, `Overlay/WakeLockHoldersView.cs`, `tests/WSGM.Tests/Core/PowerRequestListTests.cs` |
| GUID duplicates | `Core/PowerTimeouts.cs:37`, `Interop/WindowsCpuBoostApi.cs:28` |

---

## 5. Implementation batches

Every batch edits the child and the WSGM consumers together so the managed solution builds after each one.
Child commit and push precede the parent gitlink commit in the same batch. Filters assume the WDC test project
after B1 (net8 and net10).

**WDC-B0: accept W02_01.** Files: `DisplayTopology.cs`, `DisplayTopologyTests.cs`. Steps: normalize the new lines
to CRLF; build; run the three W02_01 tests; commit and push child; record gitlink. Tests:
`--filter "FullyQualifiedName~DisplayTopologyTests"`. Depends: none. About 85 lines (already written).

**WDC-B1: child foundation and test isolation.** Files: child `Directory.Build.props` (explicit root, nullable,
docs, code-style, `NuGetAudit=false`), `.editorconfig` (copied from parent rules the library already meets),
`.gitignore`, both csproj (comments U01-067; tests `TargetFrameworks` net8+net10), `WindowsPower.Actions.cs`
(3.2), `WindowsPowerTests.cs`, new `WifiProfileTests.cs` and moved cases into `WindowsRadioTests.cs` from
`tests/WSGM.Tests/Shell/RadioManagerTests.cs:90-160` (delete there), new `NativeLayoutTests.cs` (make WLAN,
PROPVARIANT, PathInfo, TargetDeviceName, WaveHeader structs internal; pin sizes and field offsets against
hand-written byte buffers), remove literal/predicate-copy tests (WDC-018). Depends: B0. Tests:
`dotnet test external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0`
and `-f net10.0-windows10.0.19041.0` (full suite is now safe), plus
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManagerTests"`. About 650 lines.

**WDC-B2: watch registrations, native lifetime, 0.2.0.** Files: `WindowsRadio.WifiWatch.cs`,
`WindowsRadio.Bluetooth.cs` (watch part, `Stopped` kind, watcher Stopped/Aborted handlers), `WindowsRadio.cs`,
`WindowsRadio.WlanNative.cs` (idempotent `WlanClient`), `CoreAudio.Watch.cs` (U01-021/022), csproj version
`0.2.0`; WSGM `RadioManager.cs` feed start/stop. Steps: per-registration objects; unregister outside any lock;
ACM only; docs on callback thread and "do not dispose a Wi-Fi registration inside its callback". Tests: fake
watcher is not available for WinRT, so registration bookkeeping is tested through an internal registration
factory seam for WLAN (`IWlanApi` notification subset) and through CoreAudio watch ownership with a fake
`IAudioEndpointVolume` (internal interface already declared). Filters:
`FullyQualifiedName~WatchRegistrationTests|FullyQualifiedName~CoreAudioTests`, WSGM
`FullyQualifiedName~RadioManagerTests`. Depends: B1. About 600 lines.

**WDC-B3: Wi-Fi identity and connect outcomes.** Files: `WindowsRadio.cs` (key, result types),
`WindowsRadio.Wifi.cs`, `WindowsRadio.WifiRules.cs`, `WindowsRadio.WlanNative.cs` (`IWlanApi`, `ref` flags,
no truncation), `WifiProfile.cs` (always hex, null check, docs); WSGM `RadioManager.cs`, `RadioEntries.cs`,
`RadioPanel.axaml.cs`, `NativeQamNetworkService.cs`, tests. Steps: merge by bytes+security, multiple profile
names are not ambiguity; connected profile, else ordinal-first; forget deletes every matching profile and reports
each; validate key; refuse enterprise/WEP and unreadable XML before mutation; scan status propagated; definite
refusal restores once, timeout returns `Pending` with the profile intact; `GetWifiStatus` Unknown without
adapters; dead code U01-048. Tests: `WifiConnectTests` with fake `IWlanApi` (create/replace/refuse/timeout/
rollback-failure/unreadable XML/two profiles same bytes), rule tests. Filters:
`FullyQualifiedName~WifiConnectTests|FullyQualifiedName~WindowsRadioTests|FullyQualifiedName~WifiProfileTests`,
WSGM `FullyQualifiedName~RadioManagerTests`. Depends: B2; coordinate with the WSGM radio/QAM domain if it is
reshaping `RadioManager` at the same time (WDC lands first). Real-device Wi-Fi validation required (library
AGENTS.md L198-200). About 1300 lines.

**WDC-B4: radio power, Bluetooth normalization and pairing.** Files: `WindowsRadio.Power.cs`,
`WindowsRadio.Bluetooth.cs`, `CoreAudio.Bluetooth.cs` (normalizer, stack-preserving rethrow), `WindowsRadio.cs`;
WSGM `RadioManager.cs`, `BluetoothDeviceCatalog.cs`, tests. Steps: enumerate radios outside `RadioCacheLock`,
publish under it; `SetPower` per-adapter result; `GetConsent` validation; one `NormalizeContainer`;
`PairBluetoothAsync` with token, one 90 s wall-clock deadline, deferrals completed before cancelling, `onRequest`
exceptions contained and reported through the task; `RespondToPairing` PIN rule; `UnpairBluetooth` status.
Tests: pure pairing-token tests (pending table, completion once, late answer harmless), normalizer cases
(Guid, braces, empty Guid, garbage), aggregation over all classes. Filters:
`FullyQualifiedName~WindowsRadioTests|FullyQualifiedName~PairingTests`, WSGM
`FullyQualifiedName~BluetoothDeviceCatalogTests|FullyQualifiedName~RadioManagerTests`. Depends: B2. Real-device
pairing validation required. About 900 lines.

**WDC-B5: Core Audio, feedback, backlight, preview.** Files: `CoreAudio.Formats.cs`, `CoreAudio.Native.cs`,
`CoreAudio.cs`, `CoreAudio.Spatial.cs`, `WaveOutFeedback.cs`, `Backlight.cs`, `AudioFilePreview.cs`; WSGM
`AudioManager.cs`, `AudioProfileEditor.cs`, `SoundPackService.cs`. Steps: probe returns on HRESULTs other than
S_FALSE/UNSUPPORTED_FORMAT, NULL closest match; out lists on success; spatial status mapping; drop cached
enumerator on disconnected/service HRESULTs; WaveOut retains buffers across repeated Dispose; single
`WaveFormat`; backlight reads the policy byte; preview failure detail; "Audio device" fallback moves to WSGM;
remove capture arm of `ToWinRtDeviceId`. Tests: format classification with a fake verdict sequence (extract the
loop into an internal pure function), WaveOut dispose decision table (internal pure function over three
statuses). Filters: `FullyQualifiedName~CoreAudioTests|FullyQualifiedName~WaveOutTests`, WSGM
`FullyQualifiedName~AudioManagerTests|FullyQualifiedName~AudioPlaybackChoicesTests`. Depends: B1. About 500 lines.

**WDC-B6: display write gate and correctness.** Files: `DisplayTopology.cs`, `DisplayTopology.Native.cs`
(`IDisplayConfigApi`), `DisplayLayouts.cs`, `DisplayLayoutPlanner.cs`, `DisplayModes.cs`, `DisplayScaling.cs`,
`DisplayColor.cs`, `DisplayEdid.cs`; WSGM behavior-only consumers. Steps: one internal static write gate for
Apply/TrySet/TrySetHdr/transient primary writes, `Read` outside it; rotation in `Matches`/`Confirm`; unreadable
unrelated paths skipped in `CaptureActive`/`TryFindActive`, matched unreadable is a typed failure; `Apply` never
throws after the write; no rollback after refusal; one-query rollback capture; layout extras reuse resolved path;
remove `ValidateBufferCounts`, 16-display cap and 4096 mode loops; EDID null monitor. Tests: `DisplayApplyTests`
with fake `IDisplayConfigApi` (refused apply: no rollback; status 0 + mismatch: one rollback; route vanished:
Unverified, no rollback; rotation-only change writes). Filters:
`FullyQualifiedName~DisplayApplyTests|FullyQualifiedName~DisplayLayoutTests|FullyQualifiedName~DisplayTopologyTests`,
WSGM `FullyQualifiedName~DisplayLayoutDiagnosticsTests|FullyQualifiedName~GameModeEntryTransactionTests`.
Depends: B1. Display hardware validation (desktop with IR switch, notebook). About 900 lines.

**WDC-B7: display typed results and wording handoff.** Files: `DisplayTopology.Types.cs`, `DisplayLayouts.cs`,
`DisplayLayoutPlanner.cs`, `DisplayModes.cs`, `DisplayScaling.cs`, `DisplayColor.cs`; WSGM
`DisplayModeView.cs`, `DisplayLayoutEditor.cs`, `SettingsViewModel.Displays.cs`, `ConfigStore.cs` (null check
only), `DisplayProfiles.cs`, `DisplayScale.cs`, `DisplayLayoutDiagnostics.cs`, new WSGM `DisplayText.cs` with the
exact current strings: the eight `Describe` reasons (`DisplayLayoutPlanner.cs:24,29,35,41,50,55,62,65`, the 16
cap string removed with the cap), the `DisplayModes.Apply` details (`DisplayModes.cs:130,135,149,155,163,171-172,175`),
scaling/HDR details (`DisplayScaling.cs:59,65,79,90,99`; `DisplayColor.cs:219,227,234,251,260`), layout details
and warnings (`DisplayLayouts.cs:258,269,296,302,315,322,338-339,363,368`). Steps: rename `DisplayProfileResult`;
outcome enums; WSGM maps codes to the same text. Tests: WSGM table test asserts each enum value maps to the
pre-change literal (copy captured once from current source); UI baselines unchanged. Filters: WDC
`FullyQualifiedName~DisplayLayoutTests`, WSGM `FullyQualifiedName~Display`, UI
`FullyQualifiedName~DisplayPageViewsTests|FullyQualifiedName~GameModeDisplayPageTests`. Depends: B6; config
domain must not be mid-edit on `ConfigStore.NormalizeLayout`. About 1100 lines.

**WDC-B8: display arrival waiter consolidation.** Files: `DisplayTopology.cs` (delete waits), `DisplayLayouts.cs`
(waiter, `Missing`), `DisplayTopology.Types.cs` (delete `DisplayWaitOutcome`), README display section; WSGM
`DisplayArrivalWaiter.cs` (delete), its callers, tests moved to WDC `DisplayWaitTests.cs`. Steps per table 4.2.
Tests: settle requires two equal fingerprints; Win32Exception counts as not settled; backstop wait used when no
target; cancellation ends; empty target list returns at once. Filters: WDC `FullyQualifiedName~DisplayWaitTests`,
WSGM `FullyQualifiedName~GameModeEntry`. Depends: B6; the WSGM session/transition domain (owner of
`GameModeEntryTransaction`) only needs the call-site rename. About 450 lines.

**WDC-B9: power, wake and recovery primitives.** Files: `WindowsWakeSecurity.cs`, `ModernStandby.cs`,
`WindowsPower.cs`, `WindowsPower.Notifications.cs`, `WindowsPower.HybridCores.cs`, `WindowsPowerRequest.cs`
(REASON_CONTEXT size), `PowerRequestList.cs`, `WindowsStorage.cs`; WSGM `LockScreenSettings.cs`,
`MessageWindow.cs`, `WindowsPowerSchemeApi.cs`, `OverlayController.Power.cs`, `WakeLockStatus.cs`,
`WakeLockHoldersView.cs`, `PowerTimeouts.cs`, `WindowsCpuBoostApi.cs`, tests. Steps: three wake primitives; pure
`WakeSecurityRestorePlan` plus per-item executor collecting failures; vanished scheme is not applicable;
DC-only policy; non-DWORD refused at Capture; WSGM composes "defaults" for no-snapshot restore (same writes as
today); ModernStandby restore from one enumeration, all devices attempted, no 4096 cap; SafeHandle notification
registrations; public `EnumerateSchemes`; no 64 cap on possible values; request list: typed status, x86 returns
`Unsupported`, caps removed, independent captured byte fixtures; skip network drives. Tests:
`WakeSecurityRestoreTests` (plan and executor with fake steps), `ModernStandbyTests.RestorePlan` extended,
`PowerRequestListTests` with captured buffers, WSGM `WakeSecurityRecoveryTests`. Filters:
`FullyQualifiedName~WakeSecurity|FullyQualifiedName~ModernStandbyTests|FullyQualifiedName~PowerRequestListTests|FullyQualifiedName~WindowsPowerTests`,
WSGM `FullyQualifiedName~WakeSecurityRecoveryTests|FullyQualifiedName~PowerRequestListTests`. Depends: B1;
MessageWindow owner change coordinated with the interop/session domain (its subscription refactor can consume the
disposable directly). About 1100 lines.

**WDC-B10: docs, metadata and guidance proposal.** Files: README (type table, samples, status placement, removed
caps, wire-stable records, SaveToDatabase note, threading/blocking rules), `docs/radios.md` (hex rule, watch
rules, pairing deadline), XML docs (U01-012/018/049/053/054/069, WDC-019), host-neutral wording (U01-066),
package metadata (U01-071), reflection pin test for the four wire-stable records (WDC-001). Proposed (not
applied) diffs for library `AGENTS.md` and `.coderabbit.yaml` for sign-off. Final child validation on both
frameworks, child commit/push, parent gitlink. Depends: B0-B9. About 600 lines.

Order: B0, B1, then B2 to B5 (radio and audio) and B6 to B9 (display and power) can interleave serially in any
order respecting the listed dependencies; B10 last.

---

## 6. Risks and open questions

Risks:
- Wire stability (WDC-001): any rename in the four display records breaks config and recovery migration; the
  reflection pin test and a config fixture are the guard.
- UI string parity (WDC-023, B7): strings must be copied verbatim; UI baselines must stay identical.
- Behavior changes needing real hardware: Wi-Fi multi-profile merge and timeout `Pending` (B3), pairing
  redesign (B4), display refusal/rollback and gate (B6). Unit tests do not prove these (library AGENTS.md
  L198-200); they belong in the M01 matrix on the notebook and the IR-switch desktop.
- `CaptureActive` now skipping unreadable paths changes NVIDIA plugin input from "exception" to "fewer paths";
  the GPU domain should confirm its handling.

Open questions for the maintainer:
1. The child `AGENTS.md` (L71-77) states that `ConnectWifi` returns the raw WLAN reason and forbids success/failure
   enums. The plan's typed `WifiConnectResult` keeps the reason code as a field. Approve the guidance edit that
   allows typed outcomes which carry the raw code?
