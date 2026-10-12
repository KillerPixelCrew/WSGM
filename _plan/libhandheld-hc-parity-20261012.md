# LibHandheld parity with Handheld Companion

The initial audit found concrete Handheld Companion parity defects. The implementation follow-up now corrects their native integrations, input mappings and glyph delivery. The original findings remain below as the comparison record; the current implementation status and executed proof take precedence.

## Implementation follow-up

- All 112 exact definitions select authored glyphs through 106 profiles. Present physical controls have packaged artwork and native OEM identifiers. Missing upstream font references use attributed equivalents. Every embedded definition imports and its actual control outlines parse through Avalonia/Skia.
- Corrected cyclic HC motion transforms, Legion and modular AYANEO rear order, configurable Legion touch timing, current KUN/FLIP packets, XG fan reset, Zotac CMOS UMA, ONEX serial rear colors and the separate fan-duty measurements.
- Added independent AMD fast PPT through uniform requests, presets and custom AC/DC profiles; source-bounded processor clocks, curve/thermal settings, Intel undervolting and supported automatic clock restoration.
- Native Valve haptics use HC packets and a real 10 ms output owner. Haptic uncertainty preserves input and remains latched until an explicit lifecycle action. Native charge/GPU/fabric measurements remain separate from accepted control state.
- Both WSGM surfaces expose native descriptors, including write-only firmware curves. Native fan/bypass choices yield competing software owners. Steam retains the established Reset behavior.
- Original WIN4 automatic fan address and RGB-only SuperJoy packet now follow current HC. Claw exposes independent Battery Master control while preserving measured fan/paddle/chord behavior. Apex reproduces HC's last-assignment/zero-axis map explicitly; the physical orientation remains an upstream limitation.
- GPD WIN5's native bypass-supply getter reads indexed EC `0x0577`; failed reads publish unknown and never gate writable controls. Its seven focused protocol/lifecycle/model-wiring tests passed.
- Release application/library compilation passed with zero warnings/errors. Library regression run: 693 of 696 initially passed, then all three failed cases passed in the 15-test focused repair. Host run: 294 of 296 initially passed, then both failed cases passed in the 13-test repair after renderer-fixture initialization and Steam-policy correction. The SDK contract subset passed 44 tests. Toolkit: 27 of 28 initially passed, then its ordering fixture passed after correction. All 36 affected UI cases passed. The final glyph batch passed 11 tests, including every embedded definition and real SVG outline parsing. These results are retained under `artifacts/libhandheld-parity`; deployment identities are recorded below after delivery.

Hardware purchases and model-by-model physical acceptance are not implementation gates. No unrun hardware, live Steam rendering or firmware behavior is claimed as tested. Host-only HC policies such as screen-color capture, Windows EPP/core scheduling and controller-remapper authoring remain distinct from LibHandheld's native integration/mapping/glyph scope.

This audit covers detection, model variants, power, fans, charging, lighting, physical input, OEM controls, motion, haptics, native dependencies, application routing, both WSGM interfaces and runtime transitions. A mapped model or matching packet is not a hardware acceptance result. Findings below distinguish implementation defects, missing functionality, deliberate product choices and unresolved hardware questions.

## Source identities and proof

- Audit date: 2026-10-12.
- WSGM audit baseline: `d144cedeb9315c7e27a4134345430cb7d508b1f0`, branch `chore/device-integration-rework`. Concurrent RTSS/CEF work advanced the checkout to `1088c454a42213e024698bdb3bebdbb928a0d623` during the audit. The cited handheld capability, controller mapping and native library files were unchanged by those commits.
- LibHandheld: `c24e8de74f4b33597383ae954a3f958b443ceadc`, same branch name; library checkout was clean.
- Primary current comparator: [HC 1.3.3.1](https://github.com/Valkirie/HandheldCompanion/releases/tag/1.3.3.1), commit `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`, released 2026-10-04. The GitHub main commit was resolved during this audit. This is also the commit already recorded by LibHandheld's HC inventory.
- Secondary comparator: shipped, decompiled HC 1.3.1.6 under `D:/Coding/WSGM/_ref/HandheldCompanion`. Installer SHA-256: `fa2cd693334a82abccb4c47256f27b8e16b9eaf16f3f6923079253bfc345a48e` according to its provenance record.
- Current HC source was downloaded and inspected under `C:/Users/N1GHT/AppData/Local/Temp/wsgm-hc-parity-20261012/upstream/HandheldCompanion`. Source references prefixed `HC/` below are relative to that directory. References prefixed `L/` are relative to `D:/Coding/WSGM/external/libhandheld/src/LibHandheld`. WSGM references are repository-relative.
- Initial audit evidence: source comparison, model-table reconciliation, current upstream identity lookup and native payload SHA-256 comparisons. No compilation, automated tests, deployment, live CEF, hardware commands or UI interaction ran during that initial audit. Existing unrelated RTSS/application edits were preserved. Implementation follow-up proof is recorded above and in the delivery record.

## Initial audit findings: coverage and stale inventory claims

The HC inventory contains 90 source records: 89 factory entries and the default fallback. They include CPU variants, aliases and broad vendor behavior, rather than 90 unique physical machines. Two records represent ASUS/default fallback behavior deliberately excluded from current exact-device selection.

The current source contains **112 runtime definitions**, counted from the actual model tables:

| Library family | Current definitions | Initial audit comparison result |
| --- | ---: | --- |
| AYANEO, including SuiPlay | 31 | Native EC controls exist; KUN and FLIP current-HC corrections missing |
| Generic, including AYN and remaining exact targets | 16 | Exact profiles exist; Loki motion defect; uncertain AYN fan measurement |
| GPD | 20 | Fan/controller protocols exist; several IMU permutations incorrect |
| Legion Go | 7 | Power, fan, charging, lighting and controller paths exist; tablet paddle/gesture differences |
| MSI Claw | 5 | Native integration exists; documented measured differences and unvalidated variant behavior |
| ONEXPLAYER and AOKZOE | 25 | Native controls exist; cyclic motion defect, serial lighting and observation differences |
| ROG Ally | 4 | Native integration exists; deliberate range differences and XG reset omission |
| Valve | 3 | Deck LCD/OLED and Steam Machine paths exist; richer haptics and observations differ |
| Zotac | 1 | Zone integration exists; active HC UMA route and fan-duty observations differ |

The original coverage documents claimed 123 definitions, including 11 fallback ranges, and 27 Generic definitions. Follow-up reconciliation now records 112 exact definitions and 16 Generic profiles, marking those 11 source fallback records as policy exclusions. The implementation coverage and inventory introduction are updated; immutable pinned upstream inventories remain unchanged.

Known exact HC selectors have corresponding model tables, with documented additional HHD aliases. Pure selection and construction remain separate from acquisition. The removed generic CPU/vendor fallbacks are a deliberate exact-identity boundary. Firmware, sensor, internal USB topology and installed native providers can still make a selected definition unavailable at runtime.

## Initial audit findings: implementation defects and protocol differences

### L01 Cyclic motion transforms are reversed

**P1, LibHandheld.** HC's `AxisSwap` maps each input axis to its output axis. HC writes `readingAxis[remapIndices[i]] = raw_i`. `MotionAxisMap.Apply` instead treats the copied indices as the input source for each output axis. A Y/Z swap is self-inverse and conceals this error; a three-axis cycle is not.

For the GPD WIN4 2023/2024/WIN5 gyro map, HC produces `(rawZ, -rawX, -rawY)` while LibHandheld produces `(rawY, -rawZ, -rawX)`. Physical rotations therefore reach different reported axes.

Affected source rows: GPD WIN4 7640U/7840U/8640U/8840U/HX370, WIN5, AMD WIN Max2 except HX370, WIN Mini 7640U/7840U/HX370; ONEX Mini Intel, X2 and X2 Mini Pro; AYN Loki Mini Pro, Zero and both Max variants. Gyro is affected throughout; accelerometer is additionally affected on the original WIN Mini and ONEX X2. Self-inverse Ally, Claw and AYANEO maps are unaffected by this particular defect.

Evidence: [HC gyro remap](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Sensors/IMUGyrometer.cs#L175), `HC/Sensors/IMUAccelerometer.cs`; [MotionAxisMap](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Internal/Devices/ProfiledDeviceModel.cs:192); `L/Families/Gpd/GpdModels.cs:32-42`, `L/Families/OneXPlayer/OneXPlayerModels.cs:30-46`, `L/Families/Generic/GenericModels.cs:16-17`. Corresponding HC JSON matrices were checked against both current and shipped sources.

Correct the direction of the permutation at its shared owner and recheck every affected row. Do not compensate by applying another transform in WSGM.

### L02 Legion tablet middle paddles are swapped on the virtual controller

**P2, LibHandheld input.** HC maps Y1 to L4, Y2 to L5, M3 to R4 and Y3 to R5. LibHandheld assigns canonical paddles 1, 2, 3, 4 in that order, while WSGM's rich target expects L4, R4, L5, R5. Consequently Y2 becomes R4 and M3 becomes L5. The OEM identifiers remain correct, so event actions can work while controller forwarding is wrong. Go S's two-paddle mapping is unaffected.

Evidence: `HC/Controllers/Lenovo/LegionController.cs:225-228`, shipped `HandheldCompanion.Controllers.Lenovo/LegionController.cs:256-260`; [Legion decoder](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/LegionGo/LegionInput.cs:89); [actual target encoding](D:/Coding/WSGM/src/WSGM/Input/SteamDeckNeptuneReport.cs:112), lines 132-133; `L/Families/SteamDeck/NeptuneReport.cs:39-42` independently uses the correct canonical order.

### L03 AYANEO KUN LED zones retain the older channel order

**P2, current HC drift.** HC 1.3.3.1 writes zones 1/3 as G,R,B and zones 2/4 as G,B,R. LibHandheld's zones 3/4 still use B,R,G and B,G,R. This matches the older shipped reference, but differs from the current correction. Distinct green/blue values produce different encoded colors on those zones.

Evidence: [HC KUN correction](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/AYANEO/AYANEOKUN.cs#L40); [library channel map](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/Ayaneo/AyaneoNativeService.cs:366).

### L04 AYANEO FLIP secondary-display latch remains the older address

**P2, current HC drift.** Current HC resets EC address `0xFF` before writing scaled brightness to `0x4E`. LibHandheld resets `0x4F`, as shipped HC 1.3.1.6 did. The library shares that path between FLIP DS and FLIP 1S DS. Scaling itself matches. The packet discrepancy is confirmed; its visible hardware consequence was not exercised.

Evidence: [HC FLIP brightness](https://github.com/Valkirie/HandheldCompanion/blob/a250b19a4b7e29dac3b383f2d1ac517d1af43e4e/HandheldCompanion/Devices/AYANEO/AYANEOFlipDS.cs#L140); [library latch](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/Ayaneo/AyaneoNativeService.cs:229); shipped `HandheldCompanion.Devices/AYANEOFlipDS.cs:119-125`.

### L05 XG Mobile automatic fan reset is absent

**P2, native transition.** HC sends `5E D1 02` to reset dock fan control before disabling XG Mobile. LibHandheld offers dock enable, lighting and custom fan curves, but enable writes only ACPI and fan writes only the custom command `5E D1 01`. Release closes the handles without the dock reset.

This path is reachable on Ally: `ProfiledDeviceEngine` composes `GenericAsusService(firmwareControls: false)` when dock USB IDs are present, including hot attachment. Removing generic ASUS model fallbacks does not remove this accessory path.

Evidence: `HC/Devices/ASUS/XGM.cs:46-49`, `HC/Commands/Functions/Performance/RogGPU.cs:59-60`; [XG enable and fan transport](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/Generic/AsusXgTransport.cs:48), lines 73-91; `L/Families/Generic/GenericAsusService.cs:265-267,503-525`; `L/Internal/Devices/ProfiledDeviceEngine.cs:142-148,619-628`.

### L06 Zotac UMA control loses HC's active direct route

**P2, capability availability.** HC's active `SetVRamSize` uses CMOS ports `0x70/0x71`, addressing `0x7A` and writing `(installedGB + additionGB) * 4`. HC separately contains a private WMI alternative. LibHandheld implements only that alternative through `UMAInterface.SetEcValue(Index=122)` and refuses control without the provider.

The arithmetic matches, but the installed WMI provider becomes a prerequisite absent from HC's active path. There is no evidence that every supported Zone installation supplies it. The library's overflow rejection is sensible and should be preserved.

Evidence: `HC/Devices/Zotac/GamingZone.cs:655-663,689-703`; [library UMA prerequisite and write](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/Zotac/ZotacNativeService.cs:68), lines 44-45 and 174-187. The shipped comparator has the same active direct route.

### L07 ONEX serial presets alter independently authored rear colors

**P2, lighting behavior.** HC's serial X1 protocol writes controller and rear solid colors separately; `SetLEDPreset` changes only controller position 0. LibHandheld applies the selected effect to both the primary and secondary zones, sending it to rear positions 3/4 too. A preset therefore changes rear-zone behavior instead of preserving its independent solid color. This concerns serial profiles; it is not a claim about HID-v1 X2/3 breathing modes.

Evidence: `HC/Devices/OneXPlayer/OneXPlayerX1.cs:349-394,411-438`; [library effect application](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/OneXPlayer/OneXNativeService.cs:289), lines 331-339 and 369-375. Both HC comparators separate the serial zones.

### L08 Legion touch timing and long-press state differ

**P2, input behavior.** Legion gestures are implemented and connected to the real reader, including delayed single tap, double-tap dragging and secondary-click long press. HC obtains timing from Windows with `SystemParametersInfo(106, ...)`; LibHandheld fixes those delays at 500 ms. HC clears the touch bit during a long click; LibHandheld retains it. WSGM does route the secondary mouse press/release.

Evidence: current `HC/Controllers/Lenovo/LegionController.cs:108-109,334,369,412-414,436-441`, shipped `HandheldCompanion.Controllers.Lenovo/LegionController.cs:142-143,337,365,400,412`; [library gesture owner](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Families/LegionGo/LegionHid.cs:559), lines 422-424, 579-611 and 641-646; `src/WSGM/Shell/DeviceOemActionRouter.cs:272-289,410-421`.

## Initial audit findings: observations and processor capabilities

### L09 Actual fan-duty observations are missing on several models

HC reads ONEX X2/X2 Mini Pro/3 fan duty from `0x44B` through their appropriate WMI/indexed transports. LibHandheld's family refresh reads temperature at `0x470` but not the fan duty. HC also reads Zone fan duty at indexed `0x44B`; the shared library fan service has no corresponding Zone observation. These are **duty observations**, not missing fan-write functionality or confirmed missing RPM measurements.

Evidence: `HC/Devices/OneXPlayer/OneXPlayerX2.cs:176-188`, `OneXPlayerX2MiniPro.cs:186-188`, `HC/Devices/Zotac/GamingZone.cs:685`; `L/Families/OneXPlayer/OneXNativeService.cs:41-58,378-405`; `L/Families/Zotac/ZotacModels.cs:25`; `L/Internal/Devices/ProfiledHardwareServices.cs:192-212`.

Project policy intentionally makes acknowledged writes authoritative control state. Any restored observation API must remain separate from write admission and must not disable controls when observation fails.

### L10 Steam Deck charge and live clock getters are absent from public observations

HC reads the actual charge ceiling and live GfxClk/FClk. LibHandheld exposes charge and clock writes but publishes accepted requested values rather than those native getters.

Evidence: `HC/Devices/Valve/SteamDeck.cs:312-321`, `HC/Devices/Valve/VangoghGPU.cs:202-209`; `L/Families/SteamDeck/SteamDeckNativeService.Hardware.cs:123-147,211-213`, `SteamDeckNativeService.Clocks.cs:170-196`, `SteamDeckHardware.cs:250-262`. This is observation coverage, not missing clock or charging setters.

### L11 Generic AMD power controls collapse three independently addressable limits

HC's non-OEM AMD path separately addresses STAPM, slow PPT and fast PPT. The library's transport contains all three setters, with matching CPU-family command IDs and write order, but the profiled service exposes only sustained and slow roles and constructs `CpuPowerLimits(sustained, boost, boost)`. Fast and slow targets are forced together. The public API's optional fast role cannot reach a capability that the provider never declares.

Evidence: `HC/Processors/AMDProcessor.cs:41-80`, `HC/Processors/AMD/RyzenSmuService.cs:745-795`; [profiled descriptors and request](D:/Coding/WSGM/external/libhandheld/src/LibHandheld/Internal/Devices/ProfiledProviderControls.cs:10), line 52; `L/Internal/Transports/AmdSmuTransport.cs:41-50,237-248`; `L/HandheldDevice.Controls.cs:55-69`.

This finding applies to the generic AMD processor backend. OEM paths such as Ally legitimately pair their short-limit writes and must be assessed by their own protocol.

### L12 General AMD and Intel GPU clocks lack an end-to-end route

HC routes GPU clock requests to its AMD/Intel processors. LibHandheld's generic AMD `SetGpuClock` is internal and unused; current descriptor-backed clock controls are Steam Deck specific. No equivalent generic clock route was found in LibGPUDriverInteract or WSGM profiles and controls.

Evidence: `HC/Managers/PerformanceManager.cs:461,1090-1100`, `HC/Processors/AMDProcessor.cs:102-145`; `L/Internal/Transports/AmdSmuTransport.cs:106`; `L/Families/SteamDeck/SteamDeckNativeService.Clocks.cs:10-12`; production-wide searches across both libraries and WSGM. Graphics-driver separation is not proof that these processor controls were implemented elsewhere.

### L13 HC processor tuning lacks equivalent backend and controls

HC exposes AMD all-core CPU and iGPU curve optimizer operations, and Intel Core/Cache, GPU and System Agent undervolting. No equivalent production implementation was found in LibHandheld, LibGPUDriverInteract or WSGM. HC's additional SMU thermal-limit methods also have no matching library endpoints; their presence is API coverage, separate from whether a particular HC UI uses them.

Evidence: `HC/Managers/PerformanceManager.cs:306-347`; `HC/Processors/AMDProcessor.cs:149-208`; `HC/Processors/IntelProcessor.cs:143-157`; `HC/Views/Pages/DevicePage.xaml.cs:455-476`. These features require native processor support and host controls; adding a selector alone would not deliver them.

### L14 Steam Deck haptic semantics exceed the current output contract

LibHandheld sends simple rumble command `0xEB`. HC's native controller uses `0xEA`, weak/strong styles and per-button release feedback. The library's output contract carries four motor intensities and cannot express all those styles and release semantics.

Evidence: `L/Families/SteamDeck/NeptuneReport.cs:91-104`, `NeptuneDevice.cs:182-190`, `L/Contracts/HapticOutput.cs:15-49`; shipped `HandheldCompanion.Controllers.Steam/NeptuneController.cs:404-454` and `steam-hidapi.net/steam_hidapi.net/NeptuneController.cs:118-139`; current controller implementation corroborates the difference. Ordinary rumble exists. Source alone does not establish that it feels wrong; physical comparison remains necessary.

## Initial audit findings: WSGM host parity gaps

These findings are application integration gaps or behavioral differences, distinct from missing family protocols.

| ID | Gap | Concrete source evidence |
| --- | --- | --- |
| H01 | Steam Quick Access omits many controls available in Overlay: fans/curves, charge protection and bypass choices, lighting power/effect/speed and generic native controls. Power controls have other Performance routes; they are not all absent. | `src/WSGM/Shell/NativeQamSemanticServices.cs:842-989` constructs only charge, brightness and zone colors; `SteamUiSessionHost.cs:1011-1014`, `NativeQamLayout.cs:37-42`. Overlay supports all descriptor value kinds at `Overlay/DeviceCapabilityControl.cs:32-67`. No alternate generic handheld route was found in Steam Settings or Extensions. |
| H02 | Native firmware fan curves are replaced by one host software curve whenever writable duty exists, except the XG curve. Normal WSGM controls cannot select those native curves. | `Shell/HandheldDeviceRuntime.cs:100-115,331-342`; library Claw/Ally native descriptors at `ClawDeviceEngine.Surface.cs:67-68`, `AllyDeviceEngine.Surface.cs:41-54`; HC writes native tables from power-profile application. |
| H03 | Software fan behavior differs from HC's aggressivity averaging and Tjmax latch, hysteresis, cooldown and safe-sample handling. WSGM interpolates instantaneous max(CPU,GPU) temperature once per second. | `HC/Managers/PowerProfileManager.cs:208-225`, `HC/Misc/FanProfile.cs:47-112,143-159`; `Shell/DeviceCoordinator.FanCurves.cs:51-58`, `DeviceFanCurveController.cs:249-319,376-399`. Curves are implemented, but their response is not equivalent. |
| H04 | Screen-driven Ambilight is absent. A firmware effect named ambilight does not provide HC's continuous screen-color sampling. | `HC/Managers/DynamicLightingManager.cs:336-342,411`; no corresponding WSGM capture owner; static/effect routing in `Shell/HandheldDeviceRuntime.cs:402-412`. |
| H05 | HC's profiled CPU frequency ceiling, EPP and active-core count lack matching Windows/application policy. WSGM does have CPU boost. | `HC/Managers/PerformanceManager.cs:1187-1282`; `Core/Profiles/ProfileConfig.cs:55-100`; searches of WSGM, WindowsDeviceControl and LibGPUDriverInteract found no equivalent operations. |
| H06 | Heterogeneous-core policy is global manual thread scheduling in WSGM rather than HC's per-power-profile parking policy. | `HC/Managers/PerformanceManager.cs:475,1135-1185`; `Core/HybridCores.cs:257-275`, `Shell/NativeQamHybridCoreService.cs:34-70`; no profile field for that policy. |

H02 and H03 are architectural/behavior differences, not proof that WSGM's software fan controller is nonfunctional. Choosing to retain the current policy still means literal HC parity is not achieved.

## Deliberate differences and unresolved hardware questions

- **Claw native fan tables:** HC writes eight duty bytes scaled to 150 at fixed temperatures. LibHandheld uses the six-point percentage/temperature layout measured on MS-1T52 and calls `Set_Temperature`. That measurement takes precedence over blind HC copying on the reference unit. Applying the same codec to the other four Claw definitions still lacks model-specific hardware proof. Evidence: `L/Families/MsiClaw/ClawCapabilities.cs:517-519,626-661`, family `PROVENANCE.md`; `HC/Devices/MSI/ClawA1M.cs:959-977`.
- **Claw Battery Master:** HC independently toggles bit 7 of `0xD7`; LibHandheld always enables it together with 60/80/100% charge selection. There is no independent disable operation. Evidence: `HC/Devices/MSI/ClawA1M.cs:388-398,906-958`; `L/Families/MsiClaw/ClawCapabilities.cs:311-362`. This is a real API/control difference, although a 100% ceiling covers the ordinary unrestricted-charging choice.
- **Claw firmware fallback:** HC selects the nearest firmware-address row. An unreadable revision falls toward its oldest row; LibHandheld selects the most recent measured layout. Old-device behavior under unreadable revision is unverified. Evidence: `L/Families/MsiClaw/ClawModels.cs:240-250` and shipped/current `ClawA1M.FirmwareDevice` selection.
- **Claw paddles, sensors and firmware chords:** the measured MS-1T52 paddle order and physical-sensor preference intentionally correct or extend the donor. Preserve those observations rather than changing them to obtain textual equality. WSGM's measured chord-suppression implementation differs from HC's PS/2 service-toggle workflow. Evidence: `L/Families/MsiClaw/HidDescriptorGamepad.cs:19-25,106-107`, family guidance and provenance.
- **Xbox Ally power range:** HC declares 15-35 W and clamps preset requests to that range. LibHandheld declares 5-35 W to allow lower presets. This is a documented deliberate expansion, not HC-equivalent limits. Evidence: `HC/Devices/ASUS/XboxROGAlly.cs:17-27`, `XboxROGAllyX.cs:17-27`, `HC/Managers/PerformanceManager.cs:374-380,1027-1030`; `L/Families/RogAlly/AllyModels.cs:239-262`.
- **Original GPD WIN4 automatic fan register:** HC's manual command writes `0xC311`, but its inherited automatic-duty path writes `0xC880`. LibHandheld writes zero to `0xC311`. The source difference is confirmed; which restores automatic control correctly requires authoritative protocol/hardware evidence. Evidence: `HC/Devices/GPD/GPDWin4.cs:27-28,71-92`; `L/Families/Gpd/GpdModels.cs:20-21`, `L/Internal/Devices/ProfiledHardwareServices.cs:269-273`.
- **AYN fan measurement:** HC explicitly labels its direct-port `0x20/0x21` measurement uncertain. LibHandheld does not implement it, despite provenance wording suggesting an EC duty read. Do not treat it as verified RPM or blindly copy it. Evidence: `HC/Devices/Ayn/AynLoki.cs:87-110`; `L/Families/Generic/AynNativeService.cs`, family `PROVENANCE.md:10-12`.
- **AYANEO SuperJoy configuration:** the library lighting packet includes controller configuration fields at shifted Windows report offsets 22/23/37/38 and keeps nonzero RGB with off-mode. HC's RGB-only frame does not. These originate from HHD's broader configuration; possible non-lighting effects need evidence. Check ownership before adopting the extra fields. Evidence: `HC/Devices/AYANEO/AYANEOSuperJoyRgb.cs:120-147`; `L/Families/Ayaneo/AyaneoSuperJoy.cs:76-103`.
- **ONEX Apex IMU donor ambiguity:** HC's JSON maps both input X and Z to output Y, overwriting one axis and never updating another. The library duplicates an axis differently. Neither is a sound three-axis rotation. Hardware orientation evidence is required. Evidence: HC `Resources/Devices/OneXPlayerApex.json:9-12,21-24`; `L/Families/OneXPlayer/OneXPlayerModels.cs:145`.
- **Steam Deck fan conversion:** library rounding versus HC truncation can differ by at most one RPM. This minor numeric difference is not a demonstrated defect.
- **Application scope:** WSGM intentionally delegates controller layout/remapping to Steam and excludes HC's remapper authoring UI. HC directional pad-click expansions, trigger soft/full actions, gyro-to-mouse/stick policy, sensor-selection/calibration UI and generic external-controller management must be compared separately if whole-application parity is desired. They should not be mislabeled as broken onboard hardware decoders. `docs/decisions.md:311-319` and `L/Contracts/CanonicalControllerState.cs:10-17` define the current boundary.
- **Unknown-device fallbacks:** broad ASUS/default CPU fallback behavior is excluded by the exact-identity policy. HHD-only devices and accessory records increase library coverage but have no HC model comparator. Inventory counts cannot compensate for a missing HC operation.

## Equivalent paths and ruled-out gaps

- AYANEO charge-threshold policy is implemented in WSGM: a one-second battery poll drives raw bypass at the selected threshold. `Shell/DeviceChargeLimitController.cs:26-99`, `DeviceCoordinator.FanCurves.cs:23-50,146-158`, `HandheldDeviceRuntime.cs:117-141`.
- Software fan curves execute real semantic duty writes. `Shell/DeviceFanCurveController.cs:249-319`, `DeviceCoordinator.FanCurves.cs:60-74`.
- Runtime integration and controller-management switches, suspend/resume and ordered stop routes exist. `Shell/DeviceCoordinator.cs:810-855,907-1003,1795-2020`. Their existence is source evidence, not a fresh transition test.
- Typed handheld commands and generic actions route through the public library API. `Shell/HandheldDeviceRuntime.cs:371-433`, `HandheldUiProjection.cs:80-118`. H02 identifies the specific native-fan exception to otherwise broad routing.
- Legion touch gestures and long-press secondary click exist end to end. The initial codec-only missing-gesture suspicion was withdrawn after tracing `LegionTouchState` and the OEM action owner.
- ONEX F1/F1 Pro's 12-color palette matches HC's caller-level quantization. The low-level HC sender accepts arbitrary RGB, but its actual caller first selects the closest palette color. A palette-mismatch suspicion was withdrawn. Mini Pro lighting is a library extension beyond the corresponding HC class.
- ONEX X2 remapping bank bytes match current HC after distinguishing the preset parameter from its fixed packet bank. That suspicion was withdrawn.
- Claw native power order remains `0x50`, then `0x51`, 200 ms apart, with BZ2EM's additional `0x52`. Scenario arithmetic and charge/fan control flags are implemented. Ally SPL/SPPT/FPPT order, charge bytes, Aura primary/secondary colors and speed bands are implemented.
- GPD model fan transports, Lenovo WMI power/fan/80% charge protection, ONEX model-specific fan and charging protocols, Zotac lighting framing, and Valve firmware-specific EC writes have actual production owners. Family-specific exceptions above prevent a blanket parity claim.
- Canonical input feeds the real WSGM router and virtual target, including haptic output. `Shell/ControllerManager.cs:558-642`, `Input/ViiperControllerBackend.cs:101-103,625-642`. This trace exposed L02; it does not prove an installed device works.

## Dependencies checked

The current upstream KX executable matches the library's pinned digest and the shipped local reference: `1695E6C7877979B1297179680E5AA3C767EAF13D27EE259D8F332EC60083EAE0`.

The embedded library payloads also match current HC:

| Payload | SHA-256 |
| --- | --- |
| RyzenSMU.bin | `301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450` |
| LedsValve.bin | `1C1809281AE6F3E3C5B362F1536C0E6706013E6E33BFBA66CAD74636E61436D6` |

`src/WSGM/WSGM.csproj:105-120` references LibHandheld and stages KX/InpOut. Setup selects exact-definition components and native installation paths in `src/WSGM.Setup/SetupEngine.cs:388-395,443-487`. MSI event repair still requires the installed MSI dependency that cannot be redistributed. Staging declarations and matching hashes do not establish successful driver installation or native execution on every model.

## Work needed before a parity claim

1. Correct the shared cyclic motion transform and Legion canonical rear mapping, then validate the affected device rows and actual target output.
2. Reconcile current HC KUN/FLIP protocol changes, XG fan reset, Zotac UMA transport and ONEX serial-zone semantics at their native owners.
3. Complete independent AMD power limits, general processor clocks and the requested tuning/observation surface without readback-gating writes.
4. Expose the implemented handheld control set in Steam as well as Overlay. Resolve native versus software fan access and HC host-policy differences explicitly.
5. Resolve ambiguous firmware, GPD automatic fan, Apex orientation, SuperJoy configuration and native haptic behavior with model-specific evidence. Preserve measured Claw corrections.
6. Reconcile inventory status with the actual source, build/deploy the agreed implementation increment, and perform attended hardware/runtime acceptance on the affected models and transitions. A percentage derived from source-record counts would be misleading.

The requested source audit is complete. No parity fixes were applied, and no runtime certification is claimed.

## HC model record reconciliation

The following appendix accounts for every HC inventory record. The mappings are the stored source associations cross-checked against the current model tables; they do not certify every operation. ASUS/default fallback associations are stale and deliberately excluded in the current source. Family findings above qualify the remaining mappings.

| HC record | Current library definition association | Audit qualification |
| --- | --- | --- |
| MinisforumV3 | minisforum-v3 | Family and variant findings above apply; no hardware certification |
| LokiMiniPro | ayn-loki-mini-pro | Family and variant findings above apply; no hardware certification |
| LokiZero | ayn-loki-zero | Family and variant findings above apply; no hardware certification |
| LokiMax6600U | ayn-loki-max-6600u | Family and variant findings above apply; no hardware certification |
| LokiMax6800U | ayn-loki-max-6800u | Family and variant findings above apply; no hardware certification |
| AOKZOEA1 | aokzoe-a1 | Family and variant findings above apply; no hardware certification |
| AOKZOEA1Pro | aokzoe-a1-pro | Family and variant findings above apply; no hardware certification |
| AOKZOEA1X | aokzoe-a1x | Family and variant findings above apply; no hardware certification |
| AOKZOEA2 | aokzoe-a2 | Family and variant findings above apply; no hardware certification |
| AYANEOAIR | ayaneo.air | Family and variant findings above apply; no hardware certification |
| AYANEOAIRPro1S | ayaneo.air-pro-1s | Family and variant findings above apply; no hardware certification |
| AYANEOAIR2 | ayaneo.air-2 | Family and variant findings above apply; no hardware certification |
| AYANEOAIRPro | ayaneo.air-pro | Family and variant findings above apply; no hardware certification |
| AYANEOAIR1S | ayaneo.air-1s | Family and variant findings above apply; no hardware certification |
| AYANEOAIRLite | ayaneo.air-lite | Family and variant findings above apply; no hardware certification |
| AYANEO2021 | ayaneo.2021 | Family and variant findings above apply; no hardware certification |
| AYANEO2021Pro | ayaneo.2021-pro | Family and variant findings above apply; no hardware certification |
| AYANEOKUN | ayaneo.kun | Family and variant findings above apply; no hardware certification |
| AYANEOSlide | ayaneo.slide | Family and variant findings above apply; no hardware certification |
| AYANEONEXT | ayaneo.next | Family and variant findings above apply; no hardware certification |
| AYANEONEXTLite4500U | ayaneo.next-lite-4500u | Family and variant findings above apply; no hardware certification |
| AYANEONEXTLite | ayaneo.next-lite | Family and variant findings above apply; no hardware certification |
| AYANEONEXT2 | ayaneo.next-2 | Family and variant findings above apply; no hardware certification |
| AYANEO2 | ayaneo.2 | Family and variant findings above apply; no hardware certification |
| AYANEOAIRPlusAMD | ayaneo.air-plus-amd | Family and variant findings above apply; no hardware certification |
| AYANEOAIRPlusAMDMendocino | ayaneo.air-plus-mendocino | Family and variant findings above apply; no hardware certification |
| AYANEOAIRPlusIntel | ayaneo.air-plus-intel | Family and variant findings above apply; no hardware certification |
| AYANEO3 | ayaneo.3-8840u, ayaneo.3-hx370 | Family and variant findings above apply; no hardware certification |
| AYANEOAIRPlusNxx | ayaneo.air-plus-nxx | Family and variant findings above apply; no hardware certification |
| AYANEO2S | ayaneo.2s | Family and variant findings above apply; no hardware certification |
| AYANEOFlipKB | ayaneo.flip-kb | Family and variant findings above apply; no hardware certification |
| AYANEOFlipDS | ayaneo.flip-ds | Family and variant findings above apply; no hardware certification |
| AYANEOFlip1SDS | ayaneo.flip-1s-ds | Family and variant findings above apply; no hardware certification |
| AYANEOFlip1SKB | ayaneo.flip-1s-kb | Family and variant findings above apply; no hardware certification |
| AYANEOFlip11 | ayaneo.flip-11 | Family and variant findings above apply; no hardware certification |
| SuiPlay0X1 | ayaneo.suiplay-0x1 | Family and variant findings above apply; no hardware certification |
| GPDWin2 | gpd-win2 | Family and variant findings above apply; no hardware certification |
| GPDWinMini_7640U | gpd-win-mini-7640u | Family and variant findings above apply; no hardware certification |
| GPDWinMini | gpd-win-mini-7840u | Family and variant findings above apply; no hardware certification |
| GPDWinMini_8840U | gpd-win-mini-8840u | Family and variant findings above apply; no hardware certification |
| GPDWinMini_HX370 | gpd-win-mini-hx370 | Family and variant findings above apply; no hardware certification |
| GPDWin3 | gpd-win3 | Family and variant findings above apply; no hardware certification |
| GPDWin4 | gpd-win4-6800u | Family and variant findings above apply; no hardware certification |
| GPDWin4_2023_7640U | gpd-win4-7640u | Family and variant findings above apply; no hardware certification |
| GPDWin4_2023 | gpd-win4-7840u | Family and variant findings above apply; no hardware certification |
| GPDWin4_2024_8640U | gpd-win4-8640u | Family and variant findings above apply; no hardware certification |
| GPDWin4_2024 | gpd-win4-8840u | Family and variant findings above apply; no hardware certification |
| GPDWin4_2024_HX370 | gpd-win4-hx370 | Family and variant findings above apply; no hardware certification |
| GPDWin5 | gpd-win5 | Family and variant findings above apply; no hardware certification |
| GPDWinMax2Intel | gpd-win-max2-intel | Family and variant findings above apply; no hardware certification |
| GPDWinMax2_2022_6800U | gpd-win-max2-6800u | Family and variant findings above apply; no hardware certification |
| GPDWinMax2_2023_7640U | gpd-win-max2-7640u | Family and variant findings above apply; no hardware certification |
| GPDWinMax2_2023_7840U | gpd-win-max2-7840u | Family and variant findings above apply; no hardware certification |
| GPDWinMax2_2024_8640U | gpd-win-max2-8640u | Family and variant findings above apply; no hardware certification |
| GPDWinMax2_2024_8840U | gpd-win-max2-8840u | Family and variant findings above apply; no hardware certification |
| GPDWinMax2_2024_HX370 | gpd-win-max2-hx370 | Family and variant findings above apply; no hardware certification |
| OneXPlayerX1Intel | oxp-x1-intel | Family and variant findings above apply; no hardware certification |
| OneXPlayerX1AMD | oxp-x1-amd | Family and variant findings above apply; no hardware certification |
| OneXPlayerX1Mini | oxp-x1-mini | Family and variant findings above apply; no hardware certification |
| OneXPlayerX1Pro | oxp-x1-pro | Family and variant findings above apply; no hardware certification |
| OneXPlayerApex | oxp-apex | Family and variant findings above apply; no hardware certification |
| OneXPlayerX2 | oxp-x2 | Family and variant findings above apply; no hardware certification |
| OneXPlayerX2MiniPro | oxp-x2-mini-pro | Family and variant findings above apply; no hardware certification |
| OneXPlayerG1Intel | oxp-g1-intel | Family and variant findings above apply; no hardware certification |
| OneXPlayerG1AMD | oxp-g1-8840u, oxp-g1-hx370 | Family and variant findings above apply; no hardware certification |
| OneXPlayerOneXFly | oxp-f1 | Family and variant findings above apply; no hardware certification |
| OneXPlayerOneXFlyF1Pro | oxp-f1-pro | Family and variant findings above apply; no hardware certification |
| OneXPlayerMiniAMD | oxp-mini-amd, oxp-mini-a07 | Family and variant findings above apply; no hardware certification |
| OneXPlayerMiniIntel | oxp-mini-intel | Family and variant findings above apply; no hardware certification |
| OneXPlayerMiniPro | oxp-mini-pro | Family and variant findings above apply; no hardware certification |
| OneXPlayer2 | oxp-2 | Family and variant findings above apply; no hardware certification |
| OneXPlayer2Pro | oxp-2-pro | Family and variant findings above apply; no hardware certification |
| OneXPlayer3 | oxp-3 | Family and variant findings above apply; no hardware certification |
| ROGAlly | rc71l | Family and variant findings above apply; no hardware certification |
| ROGAllyX | rc72la | Family and variant findings above apply; no hardware certification |
| XboxROGAlly | rc73ya | Family and variant findings above apply; no hardware certification |
| XboxROGAllyX | rc73xa | Family and variant findings above apply; no hardware certification |
| ASUS | Excluded by exact-identity policy | Stored fallback mapping is stale |
| SteamMachine | steam-machine | Family and variant findings above apply; no hardware certification |
| SteamDeck | steamdeck-lcd, steamdeck-oled | Family and variant findings above apply; no hardware certification |
| LegionGoTablet | lenovo.legion-go.83e1 | Family and variant findings above apply; no hardware certification |
| LegionGoTablet2 | lenovo.legion-go.83n0, lenovo.legion-go.83n1 | Family and variant findings above apply; no hardware certification |
| LegionGoSZ2 | lenovo.legion-go-s.83l3 | Family and variant findings above apply; no hardware certification |
| LegionGoSZ1 | lenovo.legion-go-s.83n6, lenovo.legion-go-s.83q2, lenovo.legion-go-s.83q3 | Family and variant findings above apply; no hardware certification |
| ClawA1M | ms-1t41 | Family and variant findings above apply; no hardware certification |
| ClawA2VM | ms-1t42, ms-1t52 | Family and variant findings above apply; no hardware certification |
| ClawBZ2EM | ms-1t8k | Family and variant findings above apply; no hardware certification |
| ClawCG3EM | ms-1t91 | Family and variant findings above apply; no hardware certification |
| GamingZone | zotac-zone | Family and variant findings above apply; no hardware certification |
| DefaultDevice | Excluded by exact-identity policy | Stored fallback mapping is stale |
