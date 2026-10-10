# Review since 2.1.0: implementation tasks

Goal: finish every item in review-since-2.1.0.md. Baseline WSGM e8d7b383; LibHandheld 3525145;
LibGPUDriverInteract 8649b41. Branch chore/device-integration-rework.

Implementation remains distinct from manual hardware acceptance and deferred automated tests. A
shared correction may close several ids, but every id needs actual source evidence. Checked review
items record source implementation, not final delivery or a hardware pass.

## Maintainer decisions

- [x] Remove generations across both libraries and native host routing
- [x] One shared recovery journal; failures log and never block writes
- [x] Keep Intel support probes; retain AMD/NVIDIA probes until hardware proves support queries
- [x] Use plain family ids and new state/profile ids; no legacy migration; replace NVIDIA profile
      names
- [x] WSGM calls the unified semantic HandheldDevice API; retire plugin-shaped adapters
- [x] One WSGM software fan curve loop for every family
- [x] Exact profiles only; unmatched machines are passive

## Review items (130)

- [x] **X-1**: The retired plugin model still runs on both sides of each library (structural).
- [x] **X-2**: Generations, admission and serialization are layered three to five deep (structural).
- [x] **X-3**: Settings manifests are dead end to end (local, pure deletion).
- [x] **X-4**: UI placement and labels round-trip through the boundary (local).
- [x] **LH-R1**: Journal failure blocks writes.
- [x] **LH-R2**: Uncertain writes latch a control forever.
- [x] **LH-R3**: Generic Aura speed bytes run opposite to HC.
- [x] **LH-R4**: Missing pad logs twice a second.
- [x] **LH-R5**: `DeviceTrace` is silent on 7 of 9 families.
- [x] **LH-1**: Engines repeat the same orchestration.
- [x] **LH-2**: `AsusAuraDeviceEngine` decorates every non-ASUS engine.
- [x] **LH-3**: Detection runs three or four times and Generic patches profiles to survive it.
- [x] **LH-4**: Journal subclasses.
- [x] **LH-5**: Public surface wider than WSGM uses.
- [x] **LH-6**: Ally XInput reader copies `StandardControllerSource`.
- [x] **LH-7**: Four HID descriptor decoders, hid.dll imported five times.
- [x] **LH-8**: OEM button sources repeat one HID reader lifecycle.
- [x] **LH-9**: ASUS duplicates.
- [x] **LH-10**: Command plumbing copies.
- [x] **LH-11**: Platform helper copies.
- [x] **LH-12**: Unused public API.
- [x] **LH-13**: Legion fan curve loop.
- [x] **LH-14**: Glyphs and family ids.
- [x] **LH-15**: `SteamDeckDeviceEngine.cs`.
- [x] **LH-16**: Shared profile engine knows family strings.
- [x] **LH-17**: Ally keeps three "last written" caches.
- [x] **LH-18**: Outcome fields nobody needs.
- [x] **LH-19**: Small remnants.
- [x] **LH-E1**: Legion queries XInput capabilities per input report.
- [x] **LH-E2**: IMU reads allocate an RCW per report.
- [x] **LH-E3**: IMU poll loop formats an error string every 2 ms while reads fail.
- [x] **LH-E4**: OneX serial OEM reader throws `TimeoutException` every 100 ms while idle.
- [x] **LH-E5**: OneX pulse timer runs at 50 Hz all session.
- [x] **LH-E6**: Ally XInput threads convert `ValueTask` to `Task` per sample.
- [x] **LH-E7**: `HandheldDevice.StartAsync` re-reads identity through five sequential WMI queries.
- [x] **LH-E8**: Claw re-reads identity on start, resume and each controller-management enable.
- [x] **LH-E9**: Legion builds a fresh WMI searcher per method call.
- [x] **LH-E10**: `HidDevices.PresentNodes` walks every device class with per-node allocations.
- [x] **LH-E11**: Claw mode switch re-enumerates HID every 50 ms.
- [x] **LH-E12**: Publication overhead.
- [x] **GPU-R1**: Support probe writes at discovery.
- [x] **GPU-R2**: Written value snaps back on AMD and NVIDIA.
- [x] **GPU-1**: Two complete driver engines.
- [x] **GPU-2**: AMD and NVIDIA rediscover everything every 10 s.
- [x] **GPU-3**: Four layers of retirement bookkeeping.
- [x] **GPU-4**: Built-in drivers modelled as multi-instance plugins.
- [x] **GPU-5**: Vendor detection lives in WSGM.
- [x] **GPU-6**: Dead lifecycle members.
- [x] **GPU-7**: Republish every 20 s is dead mechanism.
- [x] **GPU-8**: Every write re-enumerates first.
- [x] **GPU-9**: NVIDIA allocation and function lookups.
- [x] **GPU-10**: Duplicate descriptor builders and control bases.
- [x] **GPU-11**: Intel display identity re-implements WindowsDeviceControl.
- [x] **GPU-12**: Logging and helpers.
- [x] **GPU-13**: Dead members.
- [x] **GPU-14**: Small efficiency items.
- [x] **W-1**: Device Lab still carries the device-package pipeline.
- [x] **W-2**: Legion control id in WSGM.
- [x] **C-1**: Every desktop and laptop is a supported handheld.
- [x] **C-2**: AMD limits are "read" by sending SET commands with 0.
- [x] **C-3**: One driver step failure rolls back the whole install.
- [x] **C-4**: AYANEO SuperJoy key map is off by one.
- [x] **C-5**: FLIP DS secondary brightness clears EC 0xFF instead of 0x4F.
- [x] **D-1**: AsusAura wrapper breaks stop.
- [x] **D-2**: AsusAura hides failed restores.
- [x] **D-3**: AsusAura dispose writes hardware.
- [x] **D-4**: AsusAura races and polls under the command lock.
- [x] **D-5**: Aura accessories duplicate lighting roles on Ally and Claw.
- [x] **D-6**: Steam Deck foreign-firmware guard is bypassed.
- [x] **D-7**: OneX X2/OXP3 takeover writes the whole EC 0x04EB.
- [x] **D-8**: Legion Go 2 fan override not released.
- [x] **D-9**: Profiled Intel rejects split limits.
- [x] **D-10**: AYANEO eject cuts power after a failed eject.
- [x] **D-11**: Legion restores limits after mode.
- [x] **D-12**: Legion lighting caches update before the write.
- [x] **D-13**: GPD rewrites WinControls EEPROM on every acquire and release.
- [x] **D-14**: Zotac VRAM encoding overflows.
- [x] **D-15**: Zotac lighting reloads the profile per command.
- [x] **D-16**: AMD SMU on KrackanPoint2.
- [x] **D-17**: Intel needs PawnIO and holds the PCI mutex around KX.
- [x] **D-18**: Steam Deck HC divergences.
- [x] **D-19**: Other HC divergences.
- [x] **D-20**: Torn fan RPM.
- [x] **J-1**: Complete every correction specified under this review id.
- [x] **J-2**: Complete every correction specified under this review id.
- [x] **J-3**: Complete every correction specified under this review id.
- [x] **J-4**: Complete every correction specified under this review id.
- [x] **J-5**: Complete every correction specified under this review id.
- [x] **J-6**: Complete every correction specified under this review id.
- [x] **J-7**: Complete every correction specified under this review id.
- [x] **J-8**: Complete every correction specified under this review id.
- [x] **L-1**: Start re-detects with an uncancellable WMI reader.
- [x] **L-2**: Commands cancelled before dispatch become Indeterminate.
- [x] **L-3**: Chord hooks swallow releases asymmetrically.
- [x] **L-4**: Profiled engine duplicates services.
- [x] **L-5**: Input supervisor faults.
- [x] **L-6**: Legion fault flags.
- [x] **L-7**: Haptic and HID interop.
- [x] **L-8**: Publication gating.
- [x] **L-9**: Small.
- [x] **L-10**: Trace sink.
- [x] **G-1**: Complete every correction specified under this review id.
- [x] **G-2**: Complete every correction specified under this review id.
- [x] **G-3**: Complete every correction specified under this review id.
- [x] **G-4**: Complete every correction specified under this review id.
- [x] **G-5**: Complete every correction specified under this review id.
- [x] **G-6**: Complete every correction specified under this review id.
- [x] **G-7**: Complete every correction specified under this review id.
- [x] **W-3**: GPU drivers default to enabled.
- [x] **W-4**: Writable controls land on the read-only Info page.
- [x] **W-5**: The undeclared-role guard is gone.
- [x] **W-6**: Prerequisite banner.
- [x] **W-7**: A glyph exception tears the cycle down.
- [x] **W-8**: Weaker wsgm.log.
- [x] **W-9**: Config ordering.
- [x] **W-10**: Stale settings surfaces.
- [x] **W-11**: Right mouse button can stick.
- [x] **W-12**: Plugin API admission.
- [x] **S-1**: Complete every correction specified under this review id.
- [x] **S-2**: Complete every correction specified under this review id.
- [x] **S-3**: Complete every correction specified under this review id.
- [x] **S-4**: Complete every correction specified under this review id.
- [x] **S-5**: Complete every correction specified under this review id.
- [x] **S-6**: Complete every correction specified under this review id.
- [x] **S-7**: Complete every correction specified under this review id.
- [x] **S-8**: Complete every correction specified under this review id.
- [x] **S-9**: Complete every correction specified under this review id.
- [x] **P-1**: Complete every correction specified under this review id.
- [x] **P-2**: Complete every correction specified under this review id.
- [x] **P-3**: Complete every correction specified under this review id.

## Delivery

- [x] Finish emulator cancellation fix and enforce real portable mode for all six emulators across
      install/update/repair/external registration/launch
- [x] Verify item coverage against the authoritative review and actual final source
- [x] Complete solution-wide Rider cleanup, formatting and warning-free Release compilation
- [x] Run allowed validation; record deferred tests and hardware proof explicitly
- [x] Push each changed submodule before the WSGM gitlinks
- [x] Build the full setup and deploy the established development installation
- [x] Record final source/package identities and leave task-owned checkouts clean

## Current source checkpoint

All 130 review items and seven maintainer decisions have source evidence below. The native APIs,
shared providers/journal, host fan/charge/lighting policy, protocol corrections, packaging and SDK
retirement are applied. Source completion is distinct from final delivery and hardware acceptance.

The library/test-project compilation checkpoints passed with zero warnings/errors; no tests were
executed. Solution-wide parent Rider cleanup, library cleanup, Prettier, asset drift and
guidance/pin checks have run. The last portable save-path corrections, affected-file cleanup,
warning-free whole Release compilation, committed/pushed gitlinks, full setup and development
deployment are pending.

Unknown Flow factory reset is not invented: known originals restore; pre-dispatch refusal permits
only the declared maximum-duty software fallback; uncertain writes stop automatic policy. Automated
tests and hardware acceptance remain deferred under the maintainer's manual-first policy. Source
evidence for checked items: X-3 removes device settings manifests throughout the libraries, host and
SDK; C-1 uses exact `GenericModels` predicates with no CPU fallback; C-2
`AmdSmuTransport.ReadLimits` performs no SET calls; C-3 Setup marks PawnIO/InpOut steps nonfatal;
C-4 SuperJoy encodes modifier/key at offsets 10/12; C-5 FLIP brightness writes 0x4F then 0x4E; W-12
accepts compatible API ranges with floor 5. Native execution is not claimed by this source evidence.

Parent implementation audit: all W items and C-3/S items are source-complete after lifecycle/refusal
logging, lighting selection/application, fan status publication, development family-notice copy
patterns and obsolete scope corrections. P-1 guidance validation passed; P-3 records primary
decompiled HC provenance. Current private-library visibility and missing WSGM_CI_ENABLED variable
were verified remotely; workflow opt-in stays inactive. This does not close final formatting,
build/delivery or deferred hardware acceptance.

GPU source audit: DriverRuntime owns session discovery, changed-state publication and support
results; GpuDriver owns serialized native admission and retirement. IntelSession, AdlxSession and
NvSession share descriptor builders and cached adapter/output objects. BuiltinGpuService starts
detected vendors in parallel; GpuDriverPublisher feeds the common router directly and retains
sessions during sleep. Settings no longer repeats retired-plugin filtering. GpuCoordinator logs
driver-level sync failures without empty per-game labels. INativeProfileTarget remains because its
fake is used by the native profile synchronizer tests. Both WindowsDeviceControl gitlinks are
843ea58bef9bb921e749700b51bf22d6ab99a916. Sticky observations, topology cancellation/loss and native
profile ownership corrections remain in progress; those review IDs stay unchecked until the bounded
correction is accepted. Handheld source acceptance: shared ProfiledDeviceEngine/provider factories
own lifecycle, observation, command admission and descriptor ownership (LH-1/2/15/16).
DeviceRecoveryJournal is one permissive service-original store, with no generation, status or
readback lockout (LH-R1/2, LH-4, J-1..7). DeviceTrace owns diagnostics for every family and is
released with the facade (LH-R5, L-10). HidUsageReader, HidReportSource, LowLevelHook,
StandardControllerSource, cached WmiProvider, NativeDevice, PinnedPayload and HardwareBus replace
the duplicated native helpers (LH-6..11). Filtered HID arrival notifications attach an optional Aura
service without polling every device under the command lock (D-1..5). Shared observed values,
one-shot serial OEM edges, raw COM motion reports, transactional EC RPM and controller
reconnect/retirement cover LH-17/18, LH-E1..12 and L-1..8. Host curve/charge/lighting policy remains
separate from native firmware mechanics.

Final GPU correction source compiled warning-free, including test sources: support filtering is
central in every publication; confirmed observations advance the sticky baseline; explicit read-only
topology loss reopens once, with no polling or uncertain-write reopen. AMD unsupported profiles and
unreadable Intel/NVIDIA journals report failures. NV DRS creation/settings/save check admission at
actual dispatch and preserve original intent after an uncertain save. Tests remain unexecuted. Final
source acceptance covers all remaining native leaf items: live OneX 0x04EB bit RMW (D-7); Intel
PL1/PL2 encoding without PawnIO/global process mutex (D-9/17); confirmed-only AYANEO eject
power-down (D-10); explicit-only GPD EEPROM writes (D-13); bounded Zotac VRAM and acknowledged
lighting cache updates without reload (D-14/15); HC-supported AMD codenames and read-only mailbox
fallback (D-16); Valve mapping watchdog, PDCS/four-byte ceiling and initial LED setup (D-18). D-19
covers PawnIO short returns, VID/PID discovery, OneX remap/intercept/resend/preset/delays/
brightness/serial fallback, KUN channel orders, software charging and immediate lighting replay,
FLIP confirmation, safe AYANEO ambiguity, Loki triggers, Mini selectors, exact Win Max 2 board
fallback and Menu VK07. The now-unused internal-port proof helper and its isolated tests are
removed. L-9 small remnants are corrected; P-2 package-contract wording is removed and family IDs
are plain.

X-1/2/4 use direct semantic APIs, canonical public handheld DTOs, one native owner and consumer UI
projection. EngineDiagnostics is removed; diagnostics return the dictionary directly. Glyph paths
are discovered from embedded profile prefixes rather than manual family dictionaries (LH-14).
Own-label sandbox screening is removed (LH-19). Definitions intentionally remains a public model
catalogue for the maintainer's explicit-model consumer API requirement; semantic operation caches
are used by real host routing and retained under decision 5 (LH-5/12), rather than deleted as dead.

Final source validation: the restored entire WSGM.slnx Release build passed with zero warnings and
zero errors, including every library/application/test project
(artifacts/review-solution-release-clean.log). No test execution occurred. Parent and changed child
Full Cleanup passes plus affected-file repair cleanup ran; Prettier, emitted asset drift, guidance,
dependency pins, PowerShell parsing and no-live- data-path checks passed. UI fixtures now follow
current layout/withdrawal contracts; images remain for the maintainer's subsequent manual visual
acceptance. Full setup/deployment remains the next step.

## Completed delivery (2026-10-10)

- Production build source: WSGM f4c4ad0b5d8f26d1a0c6d5de68aca6a298a5c6bf
  (chore/device-integration-rework), with LibHandheld 61093e497313f23cb5418d34dcea7e3004ccd1ff and
  LibGPUDriverInteract 67d7e37ef20cb00dc01050b7ded4ffc4aa1471a3; children pushed before the parent
  gitlinks.
- Entire solution Release compilation: zero warnings and zero errors; all test projects compiled, no
  tests executed. Required formatting, source coverage, guidance/assets/pin checks completed.
- Full build.ps1 -DeferTests setup: Z:\WSGM-Setup-2.1.0.exe, 410294403 bytes, SHA-256
  7DF0AEABA2867D0EED4566DAA62E09F05587DAC4E71653EF516A33EBD38589BB.
- Previous setup retained as Z:\WSGM-Setup-2.1.0-before-review-20261010.exe.
- Development deployment: eng/dev-deploy.ps1 -Desktop -SkipBuild reported Deployed and restarted
  WSGM/Steam. Installed WSGM 2.1.0.1662 plus native libraries, wrappers, payloads and family notices
  match publish/App by SHA-256. Wrapper saw a stale native exit code after success; no repeated
  deploy.
- Private libraries and inactive CI remain verified. Human _mockup content remains unmodified.
- Automated tests, refreshed UI baselines, hardware and real emulator/download acceptance remain
  deferred for the maintainer's manual-first sequence. Startup/process/hash evidence is not that
  proof.

All 130 implementation items and seven maintainer decisions are accounted for; delivery is complete.
The final follow-up commit records this evidence and any test-only reference formatting, without
changing the production source used by the setup above.
