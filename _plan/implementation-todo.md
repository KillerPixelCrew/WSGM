# WSGM 2.0 implementation tracker

Status: the aggressive simplification and PR #19 review-fix milestone is source-, gate-, package-,
hand-off-, commit- and push-complete as of 2026-08-31.

Branch: `2.0` (PR #19 -> `master`)

This is the repository's only progress tracker. Mechanism details and device findings live in the
focused `docs\` topics; historical implementation plans and review transcripts were removed after
their actionable work was absorbed here and in the code.

## Preservation contract

Simplification must preserve every established outcome. WSGM remains Steam-exclusive and retains:

- Explorer-first logon initialization, boot cover, shell takeover, desktop restoration, tray and
  recovery behavior;
- the single community Device Plugin package, public SDK, Device Lab, MSI Claw integration and
  plugin-declared settings/glyphs;
- Steam Deck Composite output, controller ownership, HidHide, WSGM UI capture, per-app policy and
  Steam Input fallback;
- one persistent Steam CEF session with native-QAM performance, network, Bluetooth, audio,
  brightness, resolution/refresh, glyph, library, artwork, download and launch-option features;
- RTSS performance control, shared policy and frametime-driven AutoTDP;
- the overlay, Settings, SD-card, display/HDR, screen-off audio, keep-awake, update, uninstall and
  recovery flows.

No completed item below was closed by disabling a feature. Existing device/live evidence remains
valid, but this source refactor does not claim a new attended pass.

## Current implementation

```text
WSGM.exe (self-contained CoreCLR)
  |- one collectible in-process Device Plugin runtime
  |- controller management (VIIPER + HidHide)
  |- RTSS performance control + AutoTDP
  |- one persistent Steam CEF transport + patch/session host
  |- Wi-Fi, Bluetooth, Core Audio and touch-keyboard integration
  `- shell/session, overlay and Settings

Real separate boundaries
  WSGM.Device.Sdk       public plugin and package contract
  WSGM.DeviceLab        independent diagnostic/authoring GUI + CLI
  WSGM.LogonService     SYSTEM logon/watchdog process
  WSGM.Launch           per-game medium-integrity wrapper
  native/SteamInput                  Steam Input lease/proxy ABI (submodule)
  external/windows-device-control    radio/Wi-Fi/audio/brightness library (submodule)
  VIIPER                             native virtual-controller backend
```

The solution contains seven projects in this repository: five product/tool projects, the built-in
Claw plugin and one test project, plus the referenced library project from the device-control
submodule. A process, project, helper, mirror, protocol or abstraction is not retained for future
flexibility; it needs a current consumer or an OS, lifetime, packaging or public-contract boundary.

## Simplification milestone - complete in source

- [x] **Drop NativeAOT and its compensating architecture.** WSGM, Launch and the logon service are
      self-contained CoreCLR applications. Managed COM/WinRT and the one package-local plugin load
      are direct. AOT isolation checks, annotations and publish workarounds are gone.
- [x] **Collapse DeviceHost into WSGM.** The sole plugin loads through one collectible
      `AssemblyLoadContext` and one lifecycle adapter in the owning process. DeviceHost, its process
      manager, named-pipe protocol, shared input ring, wire DTOs, restart state machine and installer
      staging tree are deleted. Package replacement still reserves the same machine-wide owner and
      never moves files while plugin code is loaded.
- [x] **Delete native radio and volume shims.** Wi-Fi uses the Windows WLAN API, Bluetooth uses
      managed WinRT, audio uses managed Core Audio, feedback uses waveOut and the touch keyboard is
      invoked directly. The Rust Radio workspace, C++ volume helper, native ABI wrappers, staging
      scripts and shipped helper binaries are gone. Rationale and device constraints moved to
      `docs\radios.md`.
- [x] **Collapse one-consumer projects and mirrors.** LoadingIndicators source is linked into WSGM;
      the duplicate SteamInterop binding tree is replaced by links to the canonical binding; device
      tests are merged into `WSGM.Tests`; the obsolete DeviceHost project is removed.
- [x] **Make one CEF system.** `PersistentSteamUiTransport` is the only CDP connection owner.
      One-shot calls lease that transport; the second `SteamCef` socket/evaluation stack is gone.
      Download sorting is a managed MainWindow patch, network availability/scanning/indicator state
      is one gate, lifecycle is centralized, host routing/publication are tables, and the QAM source
      is split into ordered TypeScript fragments that still produce the same single hashed asset.
      The card badge and library tabs retain their proven resident mutations while using the unified
      transport; replacing those mutations requires the attended matrices below.
- [x] **Remove dead and parallel policy paths.** Legacy controller source/output types, unused
      performance-profile models, duplicate network services, unused CEF rollback rows, production
      test fakes, redundant readiness loops, duplicate task observers, repeated native declarations
      and zero-consumer helpers are deleted or merged into their surviving owners.
- [x] **Simplify packaging.** The running shell stack is one self-contained managed closure instead
      of separate WSGM and DeviceHost closures. Device Lab remains an independently runnable,
      optional tool with its own closure; the plugin remains a package component. VIIPER and both
      pinned controller drivers are required, verified release inputs instead of a build that could
      silently ship an incomplete selected component. MSBuild runs single-node to avoid the
      high-core-machine node explosion seen during this work.
- [x] **Clean comments and documentation.** Public API XML documentation now describes contracts,
      ownership, lifetime, side effects and failure behavior. Review chronology, stale AOT/host/IPC
      claims, oversized threat-model essays and comments for deleted mechanisms were removed.
      Still-valid rationale was moved to the focused topic docs.
- [x] **Resolve the proposed RTSSSharedMemoryNET substitution.** It wraps the RTSS OSD shared-memory
      surface but does not replace WSGM's profile API. Vendoring its C++/CLI project would add a
      language/project boundary while leaving the profile implementation in place, so the direct,
      tested `IFrametimeSource`/`IRtssAdapter` implementation remains.

## PR #19 review disposition

All 202 confirmed round-two findings (58 surviving Codex comments and 144 fresh findings) were
fixed. The two hardware-uncertain findings were also resolved conservatively in source: virtual Deck
digital triggers use the documented noise threshold, and Claw rumble frames pad to the advertised
HID output length. Their real-device confirmation remains explicit below.

The fixes include the high-risk paths rather than only cleanup:

- startup failures return failure, orderly bridge disposal cannot abort desktop restoration, and a
  failed game-mode commit restores Explorer;
- UI capture is wired to presentation lifetime; controller suspend/resume creates one fresh cycle,
  reader faults are observed, and make-safe cleanup covers fault and cancellation paths;
- Choice settings round-trip correctly; plugin defaults/ranges and stored values are validated;
- RTSS/per-game TDP, VRR, refresh and frame-limit fields survive policy writes and reloads;
- Device Lab attended actions validate live identity and await cleanup on cancellation/window close;
- plugin acquisition, recovery, rollback, haptics and lighting report uncertain/failing writes
  honestly;
- CEF patch ownership, generation replacement, request replay, cancellation and retraction are
  deterministic and covered by focused transport/bridge/session tests;
- installer shutdown, component selection, stale staging and package rollback cannot silently
  produce a partial or falsely successful result.

The bulky review transcript was deleted after disposition; tests, code and git history are the
durable evidence.

## Repository extraction - in progress

The 2.0 branch is large because it carries work that is not WSGM's. Anything with no dependence on
WSGM, Steam or gaming moves to its own `KillerPixelCrew` repository under MIT and comes back as a
pinned submodule, so it can be versioned, consumed and reported against on its own.

- [x] **`steam-input-lease`** (`native\SteamInput`, public). Extracted with history; people had
      already asked for it separately. Its C ABI is now a public compatibility promise: change it in
      that repository, bump `sil_abi_version()`, then move the pin here.
- [x] **`windows-device-control`** (`external\windows-device-control`, public). Wi-Fi, Bluetooth and
      pairing, Core Audio endpoints and volume, panel brightness and the volume cue — 3,100 lines
      that were never WSGM-specific. Extracted with history, given enums in place of every magic
      integer on its surface, documented so the build fails on an undocumented member, and wired
      back in as a project reference. WSGM keeps policy and wording; see `docs\radios.md` for that
      split. Verified: solution builds with zero warnings, 2,064 tests pass, and a clean
      `--recursive` clone builds from scratch.
- [ ] **`WSGM.Device.Sdk`.** The reason the extraction started: pinning an SDK version through a
      submodule instead of moving in lockstep with the application. Deliberately waits for one
      confirming milestone, because the SDK is a published contract and a rushed split would strand
      plugin authors on a version that is about to change.
- [ ] **`claw-plugin`.** After the SDK, since it consumes it.
- [ ] **`steam-ui-toolkit`.** Generalize the CEF work so others can add and remove QAM and Settings
      surfaces. It consumes `windows-device-control` for the backends behind those surfaces, which
      is why that library moved first. Waits on the CEF simplification pass.

## Verification for this milestone

- [x] `./eng/verify.ps1 -Fix`: formatting and repository invariants passed; Steam UI asset reproduced
      SHA-256 `32CE9F983B97461B077CE240EA3FAE8A01FD3D09BB13A347BF251F3C9C23D9C5`;
      Rust lint/build and 41 native tests passed; Release build completed with zero warnings/errors;
      all 2,064 managed tests passed with coverage output.
- [x] `./build.ps1`: Steam Input, pinned VIIPER, usbip-win2 and HidHide inputs validated; WSGM,
      Launch, LogonService, Device Lab and the Claw package published; Inno Setup produced
      `publish\WSGM-Setup-1.5.1.exe` (160,006,841 bytes).
- [x] Installer copied to `Z:\WSGM-Setup-1.5.1.exe`; source and destination SHA-256 both
      `F179E3F1B4757ED632AED0AC5D1993F219FDB77F5DB623E2FBBF385779761458`.
- [x] Intended tree committed as `7ddda25`, with the clean-checkout asset-format fix in `6d6762c`,
      and pushed on `2.0`; the maintainer's unrelated Rust edit remained unstaged and untouched.

Measured against the pre-milestone `HEAD`, the intended tree has 51 fewer tracked/source files and
10,399 fewer net text lines while retaining the generated Steam asset and moving tests rather than
discarding them. The solution is seven projects, down from nine; the additional one-consumer
LoadingIndicators project file is also gone.

## Product backlog - preserved, not simplification debt

These are capabilities that were already incomplete or explicitly future work. None was removed to
make the architecture smaller.

- [ ] Add real VIIPER Xbox 360 and DualShock 4 encoders; advertise each target only once its backend
      can produce it.
- [ ] Read and implement the Claw charge-limit encoding; add charge fields to the SteamOS Manager
      seam. Read the RGB effect/animation protocol before adding those controls.
- [ ] Project the shared performance services onto the redesigned overlay.
- [ ] Add a WSGM-owned Windows Night Light backend. Valve's row depends on an unavailable,
      non-configurable gamescope gate and is not a viable revival.
- [ ] Add capture-endpoint microphone volume, WASAPI session/per-app volume and multichannel speaker
      configuration/reapply. The Claw's stereo endpoint cannot establish the multichannel contract.
- [ ] Supply `SteamClient.System.DisplayManager` only if live probing proves it can replace the
      current VRR projection without losing behavior.
- [ ] Redesign the overlay presentation, especially Device, without moving state ownership out of
      its existing services.
- [ ] Add Avalonia headless interaction tests, deterministic render capture and selective visual
      baselines before that redesign.
- [ ] Evaluate richer read-only CDP developer tooling (screenshots are already in `qam-harness`;
      DOM/CSS/source-map tooling remains a development-only spike).

## Attended/live acceptance still required

These checks intentionally do not run unattended and are not source-completion blockers:

- [ ] **Shell/recovery:** boot cancellation on both sides of Explorer exit, repeated game/desktop
      transitions, crash/restore, taskbar/tray/UWP/touch, MO2 launch, jobless restored Explorer and
      upgrade from an older job-bound session. The narrower dead-parent token/job inheritance proof
      also remains.
- [ ] **Controller/device:** Deck, then X360/DS4 when implemented; per-app targets, slots, duplicate
      input, suspend/resume, reader/host fault, external owner, UI capture, physical trigger noise,
      padded rumble and integration-off coexistence on the reference Claw.
- [ ] **Plugin/settings:** package update/rollback/uninstall, Claw lifecycle/recovery, real manifest
      rendering, gamepad/touch navigation, curve authoring/application and stored-value revalidation
      after a plugin update narrows a range.
- [ ] **CEF/QAM:** complete Settings/QAM control pass, Steam restart/reconnect, per-game performance,
      each frame-limit strategy, RTSS restart/external edits, AutoTDP games/menus/suspend/manual
      override, network scan/indicator, Bluetooth, audio, brightness, resolution/refresh and
      download-sort focus behavior.
- [ ] **Badge migration prerequisite:** prove both focus/hero signals, SPA survival, leave-game
      clearing and CSSLoader coexistence before replacing the verified resident mutation.
- [ ] **Library-tab migration prerequisite:** prove boot sync, card insert/eject, filters,
      native-tab hiding and badge sync, then keep the verified resident route for one release of
      rollback soak before deletion.
- [ ] **Overlay/UI:** controller, touch, keyboard, scaling, accessibility, both themes,
      cancellation/disposal and responsiveness on the handheld.
- [ ] **Display/audio/power:** device switching during a game, volume buttons, per-app mixer when
      implemented, brightness across lock/resume, resolution changes during a game, screen-off mute
      and keep-awake behavior.
- [ ] **Installer:** clean install, in-place update, component deselection, atomic plugin swap,
      rollback, uninstall, external-state preservation and recovery-first bypass.

A checked implementation item means code, focused tests, diagnostics and documentation are complete.
An attended item stays unchecked until it actually runs on the reference device; automated evidence
must never be reported as device acceptance.
