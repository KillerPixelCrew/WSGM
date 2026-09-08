# WSGM 2.0 implementation tracker

Status: the previous implementation baseline is on `master`; the current 2.0 workoff contains
39 open issues. The maintainer directed this workoff to use default-branch commits, including
submodule changes, without feature branches or pull requests.

## Current issue workoff

GitHub was read with `gh` on 2026-09-08: implementation issues #19 through #64 are open.
Issues #41–#45, #47 and #48 remain deferred; the other 39 are the 2.0 scope.
The immediate queue is #60, #57, #55A, #56A and #56B. The later Windows-policy part of #56
depends on #25, #35 and #36 and must not block the controller repair.

- #60: separate restore origin, bounded lighting readiness restoration and resume publication
  ordering implemented. Ten new hardware-free tests pass; fresh hardware validation remains.
  The attempted full gate stopped at pre-existing guidance-link failures in retired untracked
  device trees. Per maintainer direction, further validation runs only newly added tests.
- #57: confirmed brightness readback, ordered observations and suppression of programmatic slider
  writes implemented; six new service tests and emitted-JavaScript regression checks pass. Live focus,
  touch/controller and reconnect validation remains.
- #55A: shared limiter availability, coordinator admission and automatic release on limiter loss
  implemented; ten new tests pass. #55B still needs effective scenario and paired-limit projection.
  The Overlay now refreshes retained sliders without scheduling user writes; its new control test
  also checks that pending edits survive readback and are cancelled when the control becomes unavailable.
- #56B: fixed the confirmed successful-probe stall and stable minimum/floor/maximum feedback;
  three new replay tests cover repeated descent, failed-probe recovery and `Can't Reach`.
  #56A paired writes and restoration implemented through an optional SDK command and the existing
  Claw ordered-write/rollback path. New service, SDK and fake-transport tests cover the pair;
  live hardware verification, expanded runtime diagnostics and #55B remain.
  The architectural #25/#49/#50 work follows the repair lane.

This is the repository's only progress tracker. Mechanism details and device findings live in the
focused `docs\` topics, and product decisions in `docs\decisions.md` and `_plan\2.0-decisions.md`.
Completed milestones are rolled up here to what they settled; their narratives stay in git history,
at the commits that closed them and in `4225cb3:_plan/implementation-todo.md` as it read before this
compression of 2026-09-04.

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

No completed item was closed by disabling a feature. Existing device evidence remains valid.

## Current implementation

```text
WSGM.exe (self-contained CoreCLR)
  |- one collectible in-process Device Plugin runtime
  |- controller management (VIIPER + HidHide)
  |- RTSS performance control + AutoTDP
  |- the Steam CEF surfaces: gates, QAM rows, components, session host
  |- Wi-Fi, Bluetooth, Core Audio and touch-keyboard integration
  `- shell/session, overlay and Settings

Real separate boundaries
  WSGM.LogonService                  SYSTEM logon/watchdog process
  WSGM.Launch                        per-game medium-integrity wrapper
  native/SteamInput                  Steam Input lease/proxy ABI (submodule)
  src/WSGM.Device.Sdk                public plugin and package contract (MIT)
  external/windows-device-control    radio/Wi-Fi/audio/brightness library (submodule)
  external/steam-ui-toolkit          CDP transport, patch lifecycle, bridge, modules (submodule)
  src/WSGM.DeviceLab                 diagnostic/authoring GUI + CLI (MIT)
  src/WSGM.Device.Msi.Claw8A2Vm        built-in MSI Claw device package (MIT)
  src/WSGM.Device.HandheldCompanion   HC integration design scaffold (MIT, unfinished)
  VIIPER                             native virtual-controller backend
```

The solution contains WSGM, Launch, LogonService and their tests plus the production and test
projects for the SDK, Device Lab, Claw plugin, HC scaffold, and pinned reusable libraries. The application
still loads the installed package dynamically. A process, project, helper, mirror, protocol or
abstraction is not retained for future flexibility; it needs a current consumer or an OS, lifetime,
packaging or public-contract boundary.

## Completed

Each line is closed in source, focused tests, diagnostics and documentation. The doc named beside it
holds the mechanism; the commit that closed it holds the reasoning.

- **Device import second review, 2026-09-05.** Capture export reports leftover temporary files
  when cleanup fails, operator markers reject malformed UTF-8, and both device packers publish
  through one atomic archive step. Focused tests cover locked files, cancellation, destination
  collisions, and valid Unicode; no hardware validation is claimed.
- **Device import review fixes, 2026-09-05.** Corrected HC API metadata, bounded Claw shutdown and
  malformed-response handling, shared capture redaction and rejection paths, and honest scaffold
  command results. Focused SDK, Device Lab, Claw, and HC tests cover the follow-up; this is software
  validation with no new attended hardware claim.
- **Device projects consolidated, 2026-09-05.** SDK, Device Lab, the Claw plugin, and the HC design
  scaffold now live under `src` and `tests` in WSGM, with one SDK project in `WSGM.slnx` and one PR
  for contract and consumer changes. MIT licenses, dynamic plugin loading, optional Device Lab
  installation, glyph bytes, and offline packaging are preserved. Generic PC had no implementation
  to import and is retired; the HC scaffold remains unfinished and is not shipped. Release builds
  have zero warnings/errors; all 2,661 solution tests and the main coverage run pass. Device Lab
  publishing, SDK packing, and Claw installer staging pass without hardware access.
  `docs\device-projects.md` records the layout and exact import revisions.
- **Simplification milestone.** NativeAOT and its compensating architecture dropped; DeviceHost
  collapsed into one collectible `AssemblyLoadContext` inside WSGM; native radio and volume shims
  replaced by the WLAN API, managed WinRT, managed Core Audio and waveOut; one-consumer projects and
  binding mirrors folded in; `PersistentSteamUiTransport` made the only CDP owner; dead and parallel
  policy paths deleted; packaging reduced to one self-contained managed closure; comments and public
  XML documentation rewritten to contracts. `docs\radios.md`, `docs\steam-cef.md`,
  `docs\device-integration.md`.
- **PR #19 review disposition.** All 202 confirmed round-two findings fixed, including the high-risk
  startup, recovery, UI-capture, controller-lifetime, settings round-trip, CEF patch-ownership and
  installer paths. The two hardware-uncertain findings were resolved conservatively in source: the
  virtual Deck's documented digital-trigger noise threshold, and Claw rumble frames padded to the
  advertised HID output length.
- **Earlier repository extraction, superseded for device projects on 2026-09-05.** `steam-input-lease`, `windows-device-control`,
  `WSGM.Device.Sdk`, `WSGM.DeviceLab` and `WSGM.Device.Msi.Claw8A2Vm` were extracted into independent
  `KillerPixelCrew` repositories pinned as submodules; the SDK, Device Lab and the Claw package are
  MIT so a plugin author or vendor is not forced to GPL-3 by linking the contract. Device Lab and the
  Claw package built from those pins rather than from acquired release assets, and staging
  asserts the staged glyph count against the source because package validation treats glyphs as
  optional. `steam-ui-toolkit` steps 1 to 7 landed and the revived Valve surfaces moved into it on
  2026-09-03; only the Extensions tab is left, below.
- **2.0 cleanup of `src\WSGM`, waves 1 to 3.** CEF/QAM, device integration, input, performance,
  radios, boot/shell and settings collapsed in wave 1 (`328f577`); the overlay in wave 2; docs and
  gates in wave 3. `src\WSGM` went from 315 files / 93,943 lines to 295 / 88,961. 2.0 ships from a
  `KillerPixelCrew` repository and recommends a full reinstall, so every upgrade path for pre-2.0
  state was removed with it.
- **Per-application performance profiles.** Canonical application identity, deferred RTSS writes
  until the executable profile is known, the QAM per-game toggle over Valve's own export and id 769,
  per-layer persist and restore for the power limit and VRR, and the Device-root headline toggle with
  the detail rows on Power and thermals. `docs\rtss.md`.
- **Controller and Quick Settings milestone.** VIIPER Xbox 360 and DualShock 4 encoders, Steam Deck
  motion projection, overlay charge-limit and lighting controls, Claw charge-limit support,
  native-QAM device controls composed from Steam's own primitives, and capture-endpoint microphone
  volume with independent render/capture state. Live target replacement was fixed twice: patch 0005
  for the plugout mutex, then the real cause, a `SafeNative` overload whose `() => _ = action()` body
  bound back to `Func<int>` and recursed until the stack died.
- **Field regressions and corrections, 2026-09-01 to 2026-09-04.** The QAM ownership-claim crash over
  a MobX accessor, the second CsWinRT runtime from the package's own `WinRT.Runtime.dll`, touch
  activation on the docked panels, the Big Picture transport ordering, the RTSS overlay levels and the
  byte-order mirrored `RTSS` signature, plugin health while the controller service is deliberately
  off, Steam's `0xEA`/`0x8F` haptics, the physical Claw accelerometer and gyroscope with a measured
  zero-rate offset, device-value persistence through one funnel, the Device page merged with the
  plugin's declared layout, and the frame-limit range, drift repair, `deferred` vocabulary and RTSS
  rendering-set pairing. Mechanisms in `docs\rtss.md`, `docs\device-integration.md`,
  `docs\boot-and-shell.md` and `docs\overlay-and-input.md`.

Milestone verification (`./eng/verify.ps1 -Fix`): formatting and repository invariants passed; the
Steam UI asset reproduced SHA-256
`32CE9F983B97461B077CE240EA3FAE8A01FD3D09BB13A347BF251F3C9C23D9C5`; Rust lint/build and 41 native
tests passed; the Release build completed with zero warnings or errors; all 2,064 managed tests
passed with coverage. `./build.ps1` produced `publish\WSGM-Setup-1.5.1.exe` (160,006,841 bytes,
SHA-256 `F179E3F1B4757ED632AED0AC5D1993F219FDB77F5DB623E2FBBF385779761458`), committed as `7ddda25`
with the clean-checkout asset-format fix in `6d6762c`.

## Closed by decision, do not reopen

Findings that were investigated and deliberately not acted on. Each has a measurement behind it.

- **The RTSSSharedMemoryNET substitution.** It wraps the OSD shared-memory surface but does not
  replace WSGM's profile API, so vendoring its C++/CLI project would add a language boundary and
  leave the profile implementation in place. The direct `IFrametimeSource`/`IRtssAdapter` stays, and
  `Core\RtssOsd.cs` is a C# port of the slot protocol rather than a vendored fork. `docs\rtss.md`.
- **The card badge and library tabs keep their resident mutations.** Replacing either needs the
  focused automated regression coverage first: both focus/hero signals, SPA survival, leave-game
  clearing and CSSLoader coexistence for the badge; boot sync, card insert/eject, filters, native-tab
  hiding and badge sync, then one release of rollback soak, for the tabs.
- **The three docked panels stay three windows.** The duplication the finding aimed at is gone: the
  dock, the touch-ghost filter, Escape and focus-into-view all live in `TaskbarPanel`, leaving audio
  and eject at 46 and 68 lines. A merge would collapse about two net lines of shared XAML behind
  roughly 120 lines of `OverlayController` slot plumbing, and would move window lifetime, activation,
  focus, the Steam Input lease handover and the pairing-prompt decline. Reopen only if the panels are
  being reworked for another reason anyway.
- **The four `Palette.axaml` includes stay.** Each `Styles` file resolving its own tokens keeps it
  independent of what is merged into `Application.Resources` and in what order. It also cannot be
  checked here: the XAML compiler does not validate `StaticResource` at all, so a build that stays
  green after removing an include is not evidence, and the failure would appear only on the device.
  The same applies to window-creation properties applied through a style.
- **Public XML documentation is not the line-count lever.** The wave-3 survey costed ~10.7k doc lines
  as the largest one; inspection disproved it. Most of it is contract, and the wasteful part
  (chronology, review narration, duplicated topic prose) was already removed in wave 1.

## Accepted as-is

Product calls, working today, recorded so they are not read as defects: `ManualReviewedProfile` has
no profile picker; the SDL and managed trigger thresholds differ on purpose (0.24 against 0.5, with
the reason at `src\WSGM\Input\UiInputRouter.cs`); and `VolumeButtonService` writes Core Audio
directly, so the taskbar slider lags the OSD by one poll.

## Open work

### Steam CEF startup regression

- Desktop cold-start and module-resolution corrections are implemented on
  `fix/steam-cef-cold-start`, with toolkit changes on `fix/network-probe-startup`.
  The full offline gate passed on 2026-09-05. PR review and attended startup/UI validation remain.
  Evidence, audit scope and remaining scenarios: `docs/steam-cef-startup-audit.md`.

### Repository extraction

- [ ] **`steam-ui-toolkit`: the Extensions tab.** The extension host from step 7 is built and tested;
      the surface is not, and it is not next. Also open: whether extensions may carry a .NET backend,
      which should not arrive as a side effect of building the tab. Plan: `_plan\steam-ui-toolkit.md`.

### Windows-generic platform

The Core boundary is implementation-based: when the same Windows or RTSS implementation and
semantics apply on every supported device, WSGM owns it. A vendor API, external protocol, peripheral
or environment integration belongs in a plugin even when many PCs can use it. There is no Generic PC
device package: a plain Windows PC is WSGM Core with no hardware plugin, while any optional
capability plugins compose alongside one when installed. NVIDIA DRS, eISCP receivers, Home Assistant,
network IR and device-family hardware controls remain plugins; a package groups a coherent provider
and may expose many capabilities.

RTSS performance control and the established generic Windows surfaces (per-mode resolution, refresh,
DPI and HDR; audio endpoints and playback/capture volume; Wi-Fi and Bluetooth; panel brightness;
keep-awake and screen-off mute) are already Core and stay there. A hardware plugin may publish a
device-specific power limit consumed by AutoTDP, but it never owns or reimplements RTSS.

- [ ] **Desktop First is a complete resident WSGM session, not a reduced agent.** Separate whether
      WSGM starts at logon from whether the initial session mode is Desktop or Game. Desktop First
      still initializes the device/capability plugins, overlay, keyboard hotkey, controller chord,
      running-application monitor, performance services, Steam integration permitted on the
      desktop, config watching and a desktop notification-area entry. Explorer remains the shell
      and game-mode-only effects stay off: no takeover, replacement tray host, Game display
      profile, startup-app sequence or Big Picture request. Returning from Game mode restores this
      same fully running Desktop state rather than stopping or downgrading WSGM.
- [ ] **Make on-demand Game Mode one cancellable, fail-open transaction.** The overlay and configured
      direct keyboard/controller triggers can begin it from Desktop First. Show the WSGM splash over
      the live desktop, prepare optional plugin participants, display every bounded prerequisite
      that is still waiting, and allow cancellation before Explorer exit without changing WSGM's
      shell or display state. After the prerequisites resolve, capture the verified Explorer
      recovery anchor, exit Explorer, apply the destination scene and display profile in order, and
      enter the existing Game surfaces/Steam launch path. A failure after the irreversible boundary
      compensates successful participants in reverse order and restores the Desktop scene, display
      profile and Explorer. External preparation such as IR or Home Assistant calls is explicitly
      best-effort/compensated; it cannot truthfully promise that no external side effect occurred.
- [ ] **Wait for display arrival is a release-blocking Game Mode path.** Support the reference setup
      where the inactive HDMI-extractor input exposes no EDID and Windows therefore has no TV target
      to configure. A Desktop First Game Mode request keeps the complete WSGM session and Explorer
      running, leaves the Desktop scene/profile untouched, and shows an actionable waiting line on
      the splash until the designated TV arrives, the user cancels, or WSGM shuts down. It must not
      inherit the boot splash's 120-second timeout, proceed without the target, start Big Picture or
      run game-mode startup applications while waiting. An IR or Home Assistant participant may ask
      the extractor/TV to switch first, but Windows display arrival remains the authoritative gate.
      On `WM_DISPLAYCHANGE`/`WM_DEVICECHANGE`, confirm the target through `QueryDisplayConfig`, wait
      for two identical enumerations 500 ms apart, apply the TV scene, then continue the same
      transaction automatically. If the target disappears again before Explorer exit, return to
      waiting without partially entering Game Mode; disappearance after Game Mode is established is
      non-fatal. Cover the state machine with synthetic tests. Live observation on the exact
      extractor/TV path is optional maintainer-directed diagnosis, not a completion gate.
- [ ] **Add captured Windows display-topology scenes to Core.** Capture `desk`, `tv` and `both` from
      the current Windows arrangement; persist stable target identity, active paths, modes and the
      primary display; expose verified apply/readback and the currently active outputs; and never
      make a user hand-author raw `DISPLAYCONFIG_*` structures. An absent designated target is a
      retryable waiting state, not a failed transition. Observe display/device notifications,
      confirm the target with `QueryDisplayConfig`, require two identical enumerations 500 ms apart
      before applying, and let WSGM's existing per-mode resolution/refresh/DPI/HDR profile run after
      the scene establishes which targets exist. Include crash, cancellation and Desktop rollback
      coverage so a topology change cannot strand the session without Explorer or a usable display.
- [x] **Add Windows power-scheme selection to Core.** Enumerate installed schemes, identify and read
      the active scheme, select one through the locale-independent `powrprof` API, and verify with
      `PowerGetActiveScheme`. Project it on WSGM's Power/Performance surfaces independently of
      Device Integration; persist GUIDs rather than localized names. Windows remains the authority
      for an ordinary manual selection. If session-mode or per-application scheme policy is added,
      it belongs beside WSGM's existing performance policy and restores the applicable Core layer
      when that scope ends, never in a device profile or hardware plugin.
      Implemented in overlay → Device with a staged dropdown, Apply and Refresh, independently of
      Device Integration. GUID-based enumeration, verified selection, native error reporting,
      shared serialization with idle-timeout writes and synthetic workflow tests are complete.
      The last verified GUID is persisted as a reference, never automatically reapplied. WSGM
      Settings remains WSGM configuration only. No live power settings were changed.
      Steam QAM → Performance provides the same selection through a native dropdown, backed by
      the shared Core service and a reusable toolkit row.
      `docs\power-and-display.md`.

- [x] **Shared device sections and power-source assignments.** SDK Power, RGB, Controller and Info IDs combine host and plugin controls. Device > Power contains Windows plans, presets and AC/battery assignments, with global defaults and per-game inheritance. Automatic transitions do not retry uncertain writes or overwrite manual watt preferences.
- [x] **Keep Custom power profiles per source.** A confirmed deviation after successful profile
      application saves PL1, PL2, Windows mode and any included firmware scenario to the current
      AC/battery assignment. Both dropdowns show Custom, and source/game/cycle transitions restore
      those values with current descriptor validation. Inherited edits create per-game overrides;
      the other source and global defaults remain intact. Software tests cover restoration,
      persistence, failed/stale observations and dropdown selection. The initial full repository
      gate passed; final UI refinements passed 86 focused managed tests, three UI tests and emitted
      Steam dropdown/ownership checks. No live validation is claimed.
- [x] **Claw A2VM power presets on Device and QAM Performance.** Plugin-defined Super Battery,
      Balanced, Extreme Performance and Full Power apply firmware scenario, PL1/PL2 and Windows power mode
      through the shared host. HC's AC mapping is Eco/Green/Sport; battery uses Comfort for all
      four. Full Power uses 37/37 W and Windows Best Performance, with Sport on AC and Comfort on battery.
      Scenario readback and refreshed watt limits precede ordered watt writes. Observed drift
      displays Custom without reapplying the preset. Safe write order, power-source changes,
      generation changes, partial failures, cancellation, preview and UI synchronization have
      deterministic coverage. The Windows scheme picker stays independent. No live deployment or
      hardware write is part of validation. `docs\power-and-display.md`.

### Product backlog

Capabilities that were already incomplete or explicitly future work. None was removed to make the
architecture smaller.

- [x] **Fix two SD cards showing under one card's name.** Steam's `libraryfolders.vdf` `label`
      belongs to a path registration, not a card, so re-registering a reader path left the previous
      card's label on the new card's content id; discovery's two-way name sync then adopted it and
      renamed one card to the other (reference Claw, 2026-09-05, two distinct content ids). The
      card's own `libraryfolder.vdf` marker is now the only name discovery follows, `LastSteamLabel`
      and the follow-Steam rule are gone, a rename writes the marker whether or not Steam is
      running, and the card volume monitor labels the registration it adds so Steam's storage page
      agrees. Regression tests cover the merge rule, cross-card isolation, unlabelled cards,
      forgetting and the marker reader. `eng/verify.ps1` passed: 2,056 managed tests, coverage and a
      Release build with zero warnings/errors. No live card-swap validation was run.
      `docs\sd-cards.md`.
- [ ] **Fix the Claw OEM button opening Xbox Game Bar on the Windows desktop.** Reopened after
      the maintainer reported continued Game Bar activation on 2026-09-05. The follow-up adds
      missing extended-key flag and HC's Win+G key-down interception, including ordinary keyboard
      Win+G with modifiers, as requested by the maintainer. Tests cover repeats, release ordering,
      partial/failed injection and reset; 32 focused tests pass. Attended desktop validation remains.
      Earlier work corrected the
      plugin's x64 `INPUT` layout from 32 to 40 bytes. Windows rejected the undersized synthetic
      Win-key release, which made `FirmwareChordSuppressor` pass the measured orphan `G UP` through.
      The existing device-specific matcher also covers the long-press `Tab UP`; normal Win+Tab,
      modified orphan-up sequences, injected input, volume keys and unknown sequences pass through.
      Regression tests cover the ABI, shortcut preservation, failed release and hook reset/startup
      state. `eng/verify.ps1` passed: 2,445 managed tests, 45 native tests, coverage and a Release
      build with zero warnings/errors. No live device validation was run. `docs\device-integration.md`.
- [ ] Add a WSGM-owned Windows Night Light backend. Valve's row depends on an unavailable,
      non-configurable gamescope gate and is not a viable revival.
- [ ] Add WASAPI session/per-app volume and multichannel speaker configuration/reapply. The Claw's
      stereo endpoint cannot establish the multichannel contract.
- [x] **Avalonia headless interaction tests and visual baselines.** Overlay and Settings now run
      against explicit fixture services with their real XAML, bindings and themes. The 27-test suite
      covers navigation, focus/scrolling, pins, staged power selection, Settings saves and cleanup;
      twelve exact-pixel baselines cover six states at two sizes. Binding errors and visual changes
      fail verification. Baseline promotion is an explicit case-filtered command, never a test or
      `-Fix` side effect. Three fresh Release test processes reproduced the images; the full gate
      passed with 2,548 managed tests and a warning-clean build. No live validation or deployment
      was performed. `docs\ui.md`.

### Maintainer choice

- [ ] **Legacy RTSS own-statistics cleanup.** `EnableStat=1` remains in RTSS's global profile and
      several application profiles from the retired property mapping, which is what the orange
      overlay seen after deployment is. Current WSGM no longer writes `EnableStat` in either
      direction and its own slot followed every requested level. Do not bulk-clear the external
      profiles without choosing global-only versus all-profile cleanup. `docs\rtss.md`.

## What a checked item means

Code, focused tests, diagnostics and documentation are complete, and source, build and
automated-test evidence closed it. Attended and live device validation is optional and
maintainer-directed; the attended gates that once sat under these milestones were retired by
maintainer decision on 2026-09-04, and their diagnostic recipes stay in git history. Never describe
an attended pass as performed unless it actually ran.
