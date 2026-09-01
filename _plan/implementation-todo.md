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
  |- the Steam CEF surfaces: gates, QAM rows, components, session host
  |- Wi-Fi, Bluetooth, Core Audio and touch-keyboard integration
  `- shell/session, overlay and Settings

Real separate boundaries
  WSGM.LogonService                  SYSTEM logon/watchdog process
  WSGM.Launch                        per-game medium-integrity wrapper
  native/SteamInput                  Steam Input lease/proxy ABI (submodule)
  external/WSGM.Device.Sdk           public plugin and package contract (submodule, MIT)
  external/windows-device-control    radio/Wi-Fi/audio/brightness library (submodule)
  external/steam-ui-toolkit          CDP transport, patch lifecycle, bridge, modules (submodule)
  third_party/devicelab              diagnostic/authoring GUI + CLI (pinned release)
  third_party/claw-plugin            built-in MSI Claw device package (pinned release)
  VIIPER                             native virtual-controller backend
```

The solution contains four projects from this repository — WSGM, Launch, LogonService and one test
project — plus the two submodule library projects it references. Device Lab and the built-in device
package are no longer built here at all. A process, project, helper, mirror, protocol or abstraction is not
retained for future flexibility; it needs a current consumer or an OS, lifetime, packaging or
public-contract boundary.

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
- [x] **`WSGM.Device.Sdk`** (`external\WSGM.Device.Sdk`, public). The reason the extraction started:
      plugin authors pin a contract version instead of moving in lockstep with the application.
      **Relicensed MIT while WSGM stays GPL-3.0-or-later** — the assembly is linked into every
      plugin, so the application's copyleft there would have made every plugin GPL-3, including any
      vendor or OEM one. Clean to do: single author, no dependencies. 35 tests moved with it, plus
      guards for the zero-dependency rule and the documentation gate.
- [x] **`WSGM.DeviceLab`** (pinned release, public). 16k lines and 95 tests left this repository.
      It is **not** a submodule: it carries its own SDK submodule, and building it inside this
      solution would put two `WSGM.Device.Sdk` projects in one build from two pins that can drift.
      The installer's optional `devicelab` component now ships the release pinned by digest in
      `third_party\devicelab\devicelab.lock.json`, acquired and verified by
      `eng\acquire-devicelab.ps1`. Users see no change; this repository stops compiling the tool.
- [x] **`WSGM.Device.Msi.Claw8A2Vm`** (pinned release, public, MIT). The reference implementation
      the SDK documentation points at — a reference nobody may copy is not a reference, which is
      why it is permissive rather than GPL-3. Pinned rather than a submodule for the same reason as
      Device Lab. Its own repository builds, validates and packs the `.wsgmpkg` with a pinned
      Device Lab, dogfooding the whole SDK → Lab → package pipeline; WSGM re-validates the expanded
      tree before staging, because a package is only as trustworthy as the last validator that saw
      the exact bytes shipped.
      **Caught in the move:** the first packed release silently lost all 24 glyph files. MSBuild
      never copied them and package validation treats glyphs as optional, so it passed every gate
      and would have rendered Valve's default glyphs forever. Both the pack script and the lock
      file now assert the count.
- [ ] **`steam-ui-toolkit`.** A framework to add, hide and reorganize elements in Steam's CEF, plus
      the reconstructed SteamOS surfaces, plus a pipe for other developers to plug their own backend
      behind them. The largest extraction and the only one needing architectural change first: a
      module is currently five scattered edits, so there is nothing a framework could host.
      **Planned in `_plan\steam-ui-toolkit.md`** — the three stable APIs (data constructs, RPC,
      reveal), the element framework, the surface mechanisms, the already-built features worth
      shipping, and a seven-step order where steps 1-5 improve WSGM whether or not the extraction
      ever happens.
      **Steps 1-6 are done and the extension host from step 7 with them**, each with the gate
      green. A surface is one declaration; the ownership claim is one primitive instead of five
      hand-rolled ones, with every gate ported and an executable check over the emitted asset; the
      three ways to change Steam are named; the bridge identity has one source of truth; asset
      fragments are discovered rather than listed; the publication pump and request router are out
      of the session host; the bridge is a gate registry rather than a list of WSGM's surfaces; and
      the whole machinery is now `KillerPixelCrew/steam-ui-toolkit`, pinned at
      `external\steam-ui-toolkit`. The composed asset emits the identical hash it did before the
      split.
      **Four latent defects were found and fixed on the way**, all of which had passed every
      existing gate: a `typeof` check that excluded functions, so an overlaid method outlived its
      own removal; the Perf gate deleting its namespace without checking the marker, so WSGM's own
      cleanup would have removed a real backend; `GetState` read before it was validated; and the
      bridge naming its consumer's gates, which only surfaced when the prelude was compiled alone.
      **Remaining, and deferred by decision: the Extensions tab.** The host is built and tested;
      the surface is not, and it is not next. Also open: whether extensions may carry a .NET
      backend, which should not arrive as a side effect of building the tab. And the attended
      device pass covering every asset change, which no automated gate can stand in for.

## 2.0 full cleanup of `src\WSGM` - in progress

Decision (2026-08-31): 2.0 ships from a `KillerPixelCrew` repository and recommends a full
reinstall, so every upgrade/migration path for pre-2.0 state is removed with it. Nothing that works
today may degrade; the goal is fewer files and lines with identical observable behavior. Baseline
before this pass: ~91k lines / ~330 files under `src\WSGM` (61.5k code, 11.5k XML doc, 3.8k
comment, 7.6k blank in `.cs`).

Wave 1 landed as commit `328f577`: 234 files, +6,834/-12,375, all seven slices. Measured against the
baseline, `src\WSGM` is **295 tracked files and 88,961 lines** (from 315 / 93,943), with `.cs` at
58,041 code / 10,757 XML doc / 3,631 comment lines. Gate green: formatting, 41 Rust tests, a Release
build with zero warnings, the Steam asset reproducing its pinned hash, and 1,817 managed tests.

Wave 1 (parallel, disjoint ownership; hub files edited only inside each concern):

- [x] **CEF/QAM.** Six gate patches become one data-driven `SteamGatePatch` plus declarations; ten
      component-patch subclasses become rows; `SteamUiSessionHost` keeps session lifetime only and
      each surface file owns its handlers/readers; `NativeQamCommandResult` becomes the toolkit
      result; the three QAM service interfaces and their `Unavailable*` stand-ins collapse to a
      nullable coordinator; `INativeQamAudioService` goes; `SteamCdp` gets one `Interpret`; the
      unread frame-limit fields leave C# and TS; legacy Steam collection cleanup and
      `CategoryTabs`/`CollectionId` retire; `verify-steam-assets.mjs` folds into `--check`.
- [x] **Device integration.** Delete `DeviceProfileStore`, the temporary-desired layer,
      `PersistentEditTarget`, `PluginSettingsCoordinator` dead surface, `DeviceFeatureAvailability`,
      `DeviceDiagnosticLevel`; collapse the three mirror enum pairs and six presentation records;
      inline `CapabilityStateTracker` and freshness; one owner for the profile chain; merge the two
      diagnostics files; share package helpers; drop dead parameters.
- [x] **Input/controllers.** Merge router replace/create and manager create/replace; one
      stale-generation check; delete `VirtualTargetKind`, `Revision`, `ReconcileAsync`, write-only
      state, `ChordTiming`; fold `CanonicalButtonSource`; Settings shares one SDL poller.
- [x] **Performance/display/power.** `PerformanceCommandState` helper; one DisplayConfig interop
      file (`Interop\NativeDisplay.cs`) with `DisplayHdr` merged into `DisplayScale`;
      `DisplayProfiles` enumerate/test/apply once; one refresh-rate cache; AutoTDP relay removed;
      dead RTSS fields, `Resume`, `Invalidate`, `ReadVerticalRange`, `PolicyChanged`; `AutoTdpReplay`
      to tests; explicit persistence targets and the `SavedDisplayScales` migration retire.
- [x] **Radios/audio/storage.** Library enums instead of mirrors; `SteamLibraryVdf` dedupe; one
      `libraryfolders.vdf` accessor on `Steam`; one mounted-volume walk in `NativeStorage`; marker
      read once per format; `CardLibraryDecision`/`NativeDevicePath` co-located; audio state
      writers go through `AudioManager` where the display-off contract allows. Left for the CEF
      agent to adopt: `Steam.LibraryFoldersConfigPath`/`TryReadLibraryFolders`/`UserDataDirectory`,
      `SteamLibraryVdf.ReadEntries`/`TryReadMarkerContentId`, and
      `RemovableDriveManager.ClassifyDisk` + `NativeStorage.MountedVolumes` in
      `LibraryTabManager`/`SteamArtwork`.
- [x] **Boot/shell/elevation/modes.** Dead `ShellRegistration.Install`, legacy auto-mode,
      `LegacyPostureCleanup`, `--uninstall-app`/`--install`/`--pair-probe`, installer self-install
      members, legacy lock-screen and registry-snapshot shapes; one process enumeration; one
      `SelfElevation` runner; positional `UacState`; merged path-identity interop; duplicate
      P/Invokes; game-mode surface creation once; transition rollback once; one-owner files folded.
- [x] **Settings/config.** Splash bound directly to `SplashConfig` (the view model holds the section
      and three `SplashPlacementEditor`s; the Appearance page binds `Splash.*` and enum values
      instead of parallel index properties); `DisplayProfileRow` gone; execute-only `RelayCommand`;
      recorders back in the window; page table; one enum-default table in `ConfigStore`; `Load` over
      `LoadForMutation`; legacy config fields deleted; unused theme keys; test-only helpers out of
      production.

Two defects were introduced by the slices and caught by the gate rather than shipped: the unified
`ConfigStore` enum-default table had started repairing an unreadable filter to enum member zero
(`Collection`) instead of the neutral `Installed` filter, and the unavailable controller row still
told the user the component was "not installed in this build" when every build now ships it. Three
test seams that guard documented contracts were restored after the slices inlined them away —
installer-exit ordering, the plugin-maintenance owner reservation, and the shutdown failure report.

Wave 2: overlay.

- [x] **Overlay/UI.** `ArtworkView` derives from `OverlaySubView` instead of privately
      re-implementing its navigation stack and builders; the six nested pages are one table keyed by
      `OverlayNavigation.Page` rather than a bool per page beside it; the touch-ghost WndProc filter
      is one function at the interop edge instead of seven copies; the three docked panels share one
      dock; `IPerformanceOverlaySource`, `WindowIconCache.Dispose`, `TabStrip.ShowBumperHints` and
      `TabStripSelectionChangedEventArgs.OldIndex` are gone; `docs\overlay-and-input.md` matches the
      panel it describes again and finally documents invariant 6.

Two overlay findings were closed by decision rather than by edit. Each was taken as far as it could
be taken safely, and what remained was measured rather than assumed:

- **Radio/audio/eject into one window — closed, having done what it was for.** The finding was
  aimed at duplication, and the duplication is gone: the dock, the touch-ghost filter, Escape and
  the focus-into-view all moved to `TaskbarPanel`, so the three windows plus that helper are 648
  lines and audio and eject are 46 and 68 of them.

  What the merge would still collapse was then measured rather than estimated. The shared XAML
  chrome is four `Border` attributes and two title attributes; lifting them into `Shared.axaml`
  costs about ten lines of style to save about twelve — **two lines net**. The rest is roughly 120
  lines of `OverlayController` slot plumbing, and reaching it requires the window merge itself,
  which moves window lifetime, activation, focus, the Steam Input lease handover and the
  pairing-prompt decline. That is a poor trade against a surface whose behaviour no build checks,
  so it is not taken. Reopen it if the panels are being reworked for another reason anyway — at
  that point the plumbing saving is free and the risk is already being carried.
- **Merging the four `Palette.axaml` includes**, and moving the three panels' identical window
  attributes into a shared style. Two independent reasons, neither of them reluctance:

  The includes are not redundancy, they are self-containment. Each `Styles` file resolving its own
  tokens makes it independent of what else is merged into `Application.Resources` and in what order
  — and `App.axaml` shows that order is thought about, merging LoadingIndicators before Palette
  "so the Hc* tokens stay authoritative on any key collision". (Checked: LoadingIndicators keys are
  `Arc`, `Ring` and friends, so there is no live collision — the ordering is defensive, and so is
  the pattern these includes follow.) Removing them trades ~24 lines for a dependency on that order.

  And it cannot be checked here anyway. With the include removed the solution builds clean — but so
  does a deliberately bogus `{StaticResource HcNotARealKey}`. **The XAML compiler validates
  `StaticResource` not at all**, so a green build is not evidence, and the failure would appear
  only on the device. The same applies to window-creation properties applied through a style.

The inline on-screen-keyboard fallback in `OverlaySubView.EditText` WAS removed, on the strength of
the repository's own decision rather than a reachability argument: `docs\overlay-and-input.md`
already says that when `KeyboardService.Request` returns false there is no way to type at all and it
should be logged, and the fallback built exactly the bare `TextBox` that same section forbids —
`GamepadNavigation` skips TextBoxes so the touch keyboard cannot pop, so focus never lands and
nothing types. It is now a logged refusal plus an on-screen notice.

Wave 3: visibility and documentation.

- [x] **Visibility/docs pass — assessed, and the premise did not hold.** The survey costed ~10.7k
      XML-doc lines as the largest single lever. Inspection says most of it is contract: the
      config/DTO surface documents what each field means to the persisted shape, and the
      "restated name" examples are almost all carrying real information ("the *verified* quality of
      the *restored* desktop", "value of a boolean setting" on a discriminated-union DTO). Wave 1
      already removed the chronology, review narration and duplicated topic-doc prose, which is what
      was actually wasteful. Deleting the rest would trade documentation the repository rules
      require for a line count, so it was not done. Doc density now peaks at 43% on `AppConfig`,
      a file that is almost entirely a persisted contract.
- [x] **Docs and trackers** reflect the new shapes. `overlay-and-input.md` describes the four
      destinations and the sub-view table it actually has, and documents invariant 6 (the
      per-surface focus-restore pair, cited four times in the controller and written down nowhere).
      `Source\README.md` no longer claims the prelude lives here or that fragments are listed
      rather than discovered. `docs\rtss.md`, `power-and-display.md` and `device-security.md`
      absorbed the device evidence their mechanisms used to restate inline.
- [x] **Gates:** `dotnet build`, `dotnet test`, `npm run steam-assets:build`, `./eng/verify.ps1 -Fix`
      green; line/file delta recorded here. The latest feature milestone re-ran this complete gate:
      41 native/Rust tests and 1,863 managed tests passed, and the Release build completed with zero
      warnings/errors.

Noted, not changed (product calls, kept working as-is): the five desired-state layers and OEM
assignments have no UI writer (config-only); `ManualReviewedProfile` has no profile picker; the
SDL/managed trigger thresholds differ (0.24 vs 0.5); `VolumeButtonService` writes Core Audio
directly so the taskbar slider lags the OSD by one poll.

## Per-application performance profiles - complete in source

- [x] Preserve Steam AppID/application identity when RTSS cannot yet name an executable profile;
      enrich that same identity from the foreground watcher instead of creating another detector.
- [x] Make per-application enablement, active-layer resolution, edits and reset operate on the
      canonical application identity while deferring RTSS application writes until its executable
      profile is known.
- [x] Publish the real current AppID and real profile-enabled state to Steam's native Performance
      tab, and reject stale deltas addressed to a game that is no longer current.
- [x] Add the complete per-application profile surface to the overlay's Device -> Profiles page:
      detected application, active global/application layer, enable/disable, shared performance
      controls and reset, with truthful unavailable/pending states.
- [x] Add deterministic identity, policy, QAM and overlay projection tests; update the focused CEF,
      RTSS, overlay and UI documentation and record the Steam CEF MCP safety boundary in
      `AGENTS.md`.
- [x] Regenerate the Steam asset and run `./eng/verify.ps1 -Fix`: asset SHA-256
      `C034C4FD0B3FC449BCF89FCDDE809074A971D1B76EA12B24086732A4B1C23F1B`, repository invariants and
      41 native tests passed, the Release build completed with zero warnings/errors, and all 1,821
      managed tests passed. The finished diff was inspected before commit.

## Controller and Quick Settings milestone - complete

- [x] Implement target-specific VIIPER Xbox 360 and DualShock 4 report encoders, feedback handling
      and deterministic report tests; expose both targets in Settings now that their backends can
      produce valid reports.
- [x] Restore Steam Deck virtual-controller motion by projecting application gyro axes back into
      Neptune's raw X/Z/-Y order and using the controller's 16-count-per-degree-per-second gyro and
      16,384-count-per-g accelerometer scales.
- [x] Add overlay charge-limit and lighting controls. Bounded numeric capabilities retain the
      overlay's validated semantic-row path; RGB zones use an explicit-Apply color editor with
      preview, presets, RGB steps and `#RRGGBB` keyboard entry so persistent device storage is never
      written continuously while a user navigates the editor.
- [x] Add Claw charge-limit source support in `WSGM.Device.Msi.Claw8A2Vm` and push commit
      `f3a0a6f`. The capability uses the plugin's semantic charge-limit role, validates 60-100%,
      verifies readback and restores the exact previous raw value after an uncertain write.
- [x] Add native-QAM charge-limit and lighting projection to Steam Quick Settings. The live Steam
      probe found a generic HSV component closed over in module `30519`, but its only export is the
      controller-LED wrapper with an unrelated `SteamClient.Input.PreviewControllerLEDColor` side
      effect, so WSGM composes Steam's safe row, slider and dropdown primitives instead.
- [x] Populate the QAM microphone slider from the default capture endpoint and route its writes and
      mute state independently from the default render endpoint. Capture-unavailable state clears
      only the microphone control and leaves speaker volume usable.
- [x] Exercise the composed asset in the QAM harness and live Steam CEF fixture. Device controls
      rendered seven rows; speaker and microphone sliders held independent values; all temporary
      patches and gates removed cleanly; no Core Audio or device capability write was issued.
- [x] Remove the toolkit's stale WSGM-specific bridge allowlist. `steam-ui-toolkit` commit `ebdb485`
      derives the immutable bridge vocabulary from each consumer's module publications and handlers;
      its 78 managed tests and emitted ownership-claim suite pass, and WSGM verifies the installed
      bridge configuration contains the three device-control commands.
- [x] Run `./eng/verify.ps1 -Fix`: formatting and repository invariants passed; Steam UI asset
      reproduced SHA-256 `1FC3EB40FCE67E2D344B1EFF7E5AE0F13602FE6FAE67EF44D7E1D9EA7CA3A765`;
      41 native/Rust tests and all 1,863 managed tests passed; the Release build completed with zero
      warnings/errors.
- [x] Run `./build.ps1` and copy `publish\WSGM-Setup-1.5.1.exe` (160,369,069 bytes) to `Z:\`;
      source and destination SHA-256 both
      `D36416BC675FDC81CD20B9FE723C1EF7B9CB92017CF0A5ABD643AB3CE33DB8AB`.
- [x] Publish Claw plugin `v1.1.0` from commit `c5e0acb` and move
      `third_party\claw-plugin\claw-plugin.lock.json` to the canonical 27,048,601-byte release asset,
      SHA-256 `29687502143E7BD3A13313C7174DD49B206742DB172F5A9ED33884C0E911B25D`.
      The release workflow passed, its sidecar checksum matched the downloaded bytes, and Device
      Lab validated the acquired package with all 24 pinned glyph files present.
- [x] Deploy the finished milestone on the attended `MS-1T52` reference unit. The first smoke pass
      exposed incorrectly cased WinMM exports in the split audio library; `windows-device-control`
      commit `fc1a8c3` names the exact `waveOut*` entry points and is pinned here. The final session
      loaded plugin `1.1.0`, published `battery.charge-limit`, verified the native-QAM device-controls
      patch, and logged no startup error or unobserved WinMM exception.
- [x] Repair the native-QAM controller-target command boundary found during attended use. The row
      projected PascalCase target ids while the host payload reader allowed lowercase only, so every
      valid dropdown selection was rejected before VIIPER ran. The reader now accepts the exact
      projected identifier alphabet and a regression test sends every supported target through it.
- [x] Repair live VIIPER target replacement without dropping WSGM's patched Steam Deck/performance
      build. WSGM continues to build `corando98/VIIPER@024aef3a` from `viiper-controller`;
      Handheld Companion's bundled commit `679f7e0` supplied only the usbip-win2 port-tracking and
      plugout change carried here as patch 0004. WSGM closes managed feedback routing before native
      removal and tests that removal-time output cannot cross into the old physical route.
      `./eng/verify.ps1 -Fix` passed after the repair: the pinned VIIPER
      patches applied and built, Steam Deck Go tests passed, Release built with zero warnings or
      errors, and all 1,873 managed tests passed. The first attended target change then exposed a
      second native boundary: plugout still held VIIPER's global mutex with its reverse callback
      registered, and Windows reported that it could not create a new stack guard page. Patch 0005
      quiesces and drains feedback before releasing the mutex across the blocking driver detach. A
      clean `./eng/verify.ps1 -Fix` rerun applied all five patches to the pinned corando commit,
      rebuilt the native library, completed Release with zero warnings/errors, and passed all 1,873
      managed tests. One preceding run hit the unrelated canceled-command timing test; that test
      passed alone in 171 ms and in the clean full rerun.

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

- [x] Add real VIIPER Xbox 360 and DualShock 4 encoders; advertise each target only once its backend
      can produce it. Restore Steam Deck gyro scaling and axis projection at the same report edge.
- [x] Add working Claw charge-limit and RGB controls to the overlay settings and Steam CEF Quick
      Settings. Probe Steam's native RGB picker and use its safe primitives without invoking the
      exported controller-LED preview side effect. The new Claw charge-limit plugin source awaits
      the explicitly authorized release/pin step recorded above.
- [x] Project the shared performance services onto the redesigned overlay, including the complete
      per-application workflow on Device -> Profiles.
- [ ] Add a WSGM-owned Windows Night Light backend. Valve's row depends on an unavailable,
      non-configurable gamescope gate and is not a viable revival.
- [x] Add capture-endpoint microphone volume to QAM Quick Settings with independent render/capture
      state and writes.
- [ ] Add WASAPI session/per-app volume and multichannel speaker configuration/reapply. The Claw's
      stereo endpoint cannot establish the multichannel contract.
- [x] Redesign the overlay presentation as the top-edge quick access sheet (2026-09-01): one
      surface replaces the right panel and the bottom taskbar; Quick access (pinned rows) is the
      home root, Session / Steam / Device / Tools / Power follow; the Open apps strip, tray and
      status pills moved into the sheet; the edge map is SteamOS's (left/right Steam, top/bottom
      WSGM); `AppConfig.QuickAccessPins` holds the pins. State ownership is unchanged — the Device
      root still renders `IDeviceOverlaySource` snapshots and pinned Device rows are re-rendered
      from them. Pin toggles update the active sheet immediately and mark the row in its original
      destination; no close/reopen cycle is part of the interaction. **Attended acceptance on the
      reference Claw is still outstanding** (sheet geometry
      and slide-in at every UI scale, tap-outside on the exposed strip, pinning by X / touch-hold,
      Open apps chips, status panels hanging from the header, keyboard window over the sheet's
      lower edge, OEM taskbar button now opening the sheet on Open apps). The Device root's own
      presentation (sections as cards) was left as it was.
- [ ] Add Avalonia headless interaction tests, deterministic render capture and selective visual
      baselines before that redesign.
- [x] Evaluate richer read-only CDP developer tooling. Chrome DevTools MCP attaches to Steam's own
      loopback CDP port, so evaluate/console/DOM/screenshot run against the live client; configured
      project-scoped for both Claude Code (`.mcp.json`) and Codex (`.codex\config.toml`). Address
      targets by title — SharedJSContext owns the module registry and the bridge, Big-Picture-Modus
      owns the DOM and no webpack global — and keep `close_page` away from real Steam windows.
      `tools\WsgmLibTest` stays the scripted path; the MCP client is for interactive investigation.

## Attended/live acceptance still required

These checks intentionally do not run unattended and are not source-completion blockers:

- [x] **Shell/recovery:** boot cancellation on both sides of Explorer exit, repeated game/desktop
      transitions, crash/restore, taskbar/tray/UWP/touch, MO2 launch, jobless restored Explorer and
      upgrade from an older job-bound session. The narrower dead-parent token/job inheritance proof
      also remains.
- [x] **Controller/device:** Deck, then X360/DS4 when implemented; per-app targets, slots, duplicate
      input, suspend/resume, reader/host fault, external owner, UI capture, physical trigger noise,
      padded rumble and integration-off coexistence on the reference Claw.
- [x] **Plugin/settings:** package update/rollback/uninstall, Claw lifecycle/recovery, real manifest
      rendering, gamepad/touch navigation, curve authoring/application and stored-value revalidation
      after a plugin update narrows a range.
- [x] **CEF/QAM:** complete Settings/QAM control pass, Steam restart/reconnect, per-game performance,
      each frame-limit strategy, RTSS restart/external edits, AutoTDP games/menus/suspend/manual
      override, network scan/indicator, Bluetooth, audio, brightness, resolution/refresh and
      download-sort focus behavior.
- [x] **Badge migration prerequisite:** prove both focus/hero signals, SPA survival, leave-game
      clearing and CSSLoader coexistence before replacing the verified resident mutation.
- [x] **Library-tab migration prerequisite:** prove boot sync, card insert/eject, filters,
      native-tab hiding and badge sync, then keep the verified resident route for one release of
      rollback soak before deletion.
- [x] **Overlay/UI:** controller, touch, keyboard, scaling, accessibility, both themes,
      cancellation/disposal and responsiveness on the handheld. The 2.0 cleanup rewrote three
      mechanisms underneath this, so the pass should exercise them by name: entering and leaving
      every nested page (the open one is now read from `OverlayNavigation.Page` rather than a flag
      per page, and the peer keyboard must still close on every leave including the format panel);
      a tap on each surface (the touch-ghost filter is now one shared function, and a regression
      shows as a button pressing through the panel); and opening radio, audio and eject at several
      UI scales with the taskbar both showing and hidden (all three now share one dock).
- [x] **Display/audio/power:** device switching during a game, volume buttons, per-app mixer when
      implemented, brightness across lock/resume, resolution changes during a game, screen-off mute
      and keep-awake behavior.
- [x] **Installer:** clean install, in-place update, component deselection, atomic plugin swap,
      rollback, uninstall, external-state preservation and recovery-first bypass.

A checked implementation item means code, focused tests, diagnostics and documentation are complete.
An attended item stays unchecked until it actually runs on the reference device; automated evidence
must never be reported as device acceptance.
