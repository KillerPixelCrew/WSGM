# WSGM 2.0 implementation tracker

Status: the previous implementation baseline is on `master`; the current open workoff contains
15 issues for 2.0 and seven deferred issues. The maintainer directed this workoff to use default-branch commits, including
submodule changes, without feature branches or pull requests.

## Current issue workoff

After delivery of #38/#39, #51/#53, #58/#59, #61, #65–#68, #22/#26/#35/#36 and, on 2026-09-11,
#20/#28/#30/#31/#34/#70/#71/#72 and, that evening, #52, eight issues remain open. Issues #41, #42,
#44, #45, #47 and #48 remain deferred; #21 and #40 are the 2.0 scope. #20 and #28 were closed on 2026-09-10 and
reopened on 2026-09-11: #20 because the Session tab had not followed the category migration, #28
because the September 9 Steam Client Beta reworked the library UI and added Big Art Mode; both were
delivered the same day. #70–#72 were filed on 2026-09-11 for the same beta and closed that evening.

Every remaining 2.0 issue is waiting on something outside the repository, which is why the count
stops here rather than at zero:

- #64 still needs a decision on what "driver-level VSync" maps to: Intel's header has no
  `CTL_3D_FEATURE_VSYNC`. `CTL_3D_FEATURE_GAMING_FLIP_MODES` and `CTL_3D_FEATURE_LOW_LATENCY` both
  answer on the reference unit but neither is a VSync toggle.
- #21 needs the hardware: power-button capture over ACPI/HID/EC.
- #20 is delivered (2026-09-11, `804df0d`). The reopen named Session: four buttons on a root tab
  of their own. It is a Power category now, beside Wake, Idle timeouts and Power, with rows, tags
  and handlers unchanged so pins survive. The tab-by-tab audit the issue asked for is in
  `docs\overlay-and-input.md`, Quick access recorded as the one intentional direct layout. Nine
  overlay baselines re-promoted for the shorter strip; the UI tests that click tabs by index are
  renumbered.
- #28 is delivered (2026-09-11). The badge is a toolkit surface now, `SteamLibraryBadgeSurface`:
  the library tile is one exported memo on the September beta, the toolkit claims its `type` and
  places the badge immediately left of Valve's Steam Input badge by element identity, so Home's
  carousel and the library grid are covered by one claim and no pixel or class is measured. Name
  only, green installed and grey not, as the maintainer chose. Big Art Mode is Steam's own
  `library_home_big_art` setting, read from the settings store and reported through `homeLayout`;
  the badge is tile-relative and needs no separate placement per layout. The in-page resident
  script and its boot retry are gone. Verified on the beta with the card present and pulled.
  Follow-up the same evening: the library also shows on the game's own page, as a stat after Last
  Played and Play Time (`steam-ui.library-details`), through a new shared JSX-runtime claim that
  download sort moved onto. Offline checks pass on Stable and the beta, and the maintainer confirmed
  the page on Stable. Download sort on the shared claim, now on in either mode, was confirmed on
  screen after the next deploy (`da0b56f`).
- #72 is delivered (2026-09-11). Home's carousel is `SteamHomeCarouselSurface`: the toolkit
  claims Home's memo, replaces the one `games` array Home hands the carousel and its background, and
  orders it from Steam's own collections under WSGM's instruction (games on disconnected cards
  excluded, uninstalled games optional and greyed). The maintainer's memory concern was the
  overscan: the carousel is already virtualized and Home passed the whole list as overscan, so it
  now gets the component default of 3. Settings rows under Integration, the carousel on and
  uninstalled off by default. Verified live on the beta: the patch verified once Big Picture had
  built its tree, the carousel reported 93 installed entries with the 88 games found only on
  absent cards left out (matching the card model with SDCard1 in the reader), and the maintainer
  confirmed Home. Greyed uninstalled games were not checked on screen.
- #70 is delivered and closed (2026-09-11). The beta renumbered Steam's module registry: on its
  first start the audio, performance, brightness and Bluetooth gates refused and every Quick Access
  row degraded, and the side-menu snapshot and native QAM/Home/keyboard replay (#65, #67) named ids
  that were gone. Nothing names a module id or a minified export any more: the resolver's
  `exported(tokens, predicate)` finds a module by fingerprint and an export by shape, the UI store is
  Steam's `window.SteamUIStore`, and the localizer is chosen by what it does rather than by its
  parameter names. `eng\check-steam-fingerprints.mjs` reads every fingerprint out of the toolkit and
  WSGM and counts its matches in the installed bundle: all unique on the beta. Evidence and the new
  beta features' evaluation are in `docs\steam-cef.md`. Verified live the same evening on the beta and
  then on Stable: every patch verified on both, the Screensaver rows standing aside on Stable, which
  has no screensaver. Those passes also moved the header Wi-Fi indicator to both modes and made a
  stale screensaver bound drop when its surface does not hold. Closed at the maintainer's direction
  after those passes; the restart and desktop/game transition matrix was not run as its own pass.
- #71 is delivered and closed (2026-09-11). `SteamScreensaverSurface` appends "Turn display off
  after (on battery)" and "(plugged in)" to Steam's Screensaver section through a new shared
  `useMemo` claim the Quick Access host now uses too. Both rows and the overlay's screen-off rows go
  through one owner, `Shell\DisplayTimeouts`, over the active scheme; Steam's reported screensaver
  timeout bounds them (`Core\DisplayTimeoutPolicy`), a report raises a breach once, choices below the
  bound are neither offered nor accepted. 30 toolkit cases, the emitted-gate check and 30 WSGM cases
  (policy, owner and host) pass. On the beta the patch verified and raised the plugged-in display
  timeout from 1 to 5 min; on Stable it stood aside. Closed at the maintainer's direction; not run as
  separate passes: CEF reload, desktop-to-game transition, AC/battery changes and
  `powercfg /requests`.
- #27 and #40 are implemented as far as they can be without a device session and the brand mark.

- #30 and #31 are delivered and verified live on the September 2026 beta (2026-09-11). The
  audit that reopened them was right: the bridge existed and was referenced by nothing. It is
  declared now, over session-owned managers shared with the overlay. What it took, each found by
  driving Steam's own page and each recorded in its commit: Steam caches its two storage queries
  forever, so the gate invalidates the client's query keys; the wire contract is eleven and
  fourteen fields with `uint32` ids and an idle `adopt_stage` of 1; requests arrive as an envelope
  whose `Body()` holds the message; the folder row matches `mount_paths` exactly against the
  library path; the page never sends `Format` — its Format Drive modal sends `Adopt` with a label.
  Windows-side: the disk↔volume join goes through `WindowsStorage.DescribeVolumes` in
  windows-device-control; the library flag comes from the card's marker; eject, hard pull, format
  (the existing `SdFormatManager` flow, retrim included) and trim all run from Steam's page. The
  format switch is a Settings row, on by default. `docs\sd-cards.md`, `docs\steam-cef.md`.
- #34 is delivered. `KillerPixelCrew/VIIPER@wsgm` carries the former six patches as commits plus two
  new ones (`add_ex` no longer attaches; the device-interface size query is guarded), WSGM pins
  `fe726ce`, the patch files are gone, and every rejected variant change has a stated reason in
  `third_party\controller\viiper\README.md`. Validated on the reference Claw.

- #32 is implemented. The Claw package publishes the Intel GPU memory share as a 13-87 percent
  device-persistent row on the Power page. It is a driver setting rather than an IGCL call:
  `GpuSystemMemoryPinninglimit` under the adapter's `GMM` key, which the driver reads when it
  initialises its memory manager. Confirmed both directions on the reference unit by driving Intel
  Graphics Software — 44% wrote 44, reset wrote 57 back rather than deleting the value, and nothing
  else in the registry or on disk moved. The default is the literal 57, so an absent value reads as
  the default rather than as a missing feature. The write is verified; the split needs a restart and
  the row's label says so. Not journalled and not restored on stop, like the charge limit. Eighteen
  focused tests pass against a disposable HKCU subtree. **The effect after a restart is not measured.**

- #35 is implemented. Windows Device Control exposes hybrid processor core placement: efficiency
  classes from CPU set information, whether a scheme actually exposes HETEROPOLICY, SCHEDPOLICY and
  SHORTSCHEDPOLICY, the values Windows publishes for each, and per-power-source read/write with an
  explicit scheme reapply. The library stays policy neutral. Twelve parse and support cases pass.
  Verified on the Core Ultra 200V handheld: four cores in each of two efficiency classes, all three
  settings readable, one stored value written and restored with confirmed readback. The scheme was
  not reactivated during that check, so running behaviour never changed.

- #36 is implemented. `Core\HybridCores` owns the modes, the write and the readback; the overlay
  puts it on Device > Power beside the Windows energy plan and Steam gets the same control as a
  Performance dropdown through a new toolkit row. Both surfaces hold one policy object and one id
  vocabulary, and neither caches — activating a scheme can carry a different preference with it.
  WSGM writes only the two thread settings Windows names itself and carries the heterogeneous-policy
  value through untouched, because `powercfg /qh` publishes no meaning for its numbers. A stored
  pair that disagrees with itself reads back as no mode rather than the nearest one. 23 core/QAM
  cases, 5 overlay cases and 8 toolkit cases pass. Live Steam QAM review remains.

- #26 is implemented. Windows Device Control reports S0 low-power-idle capability and the mandatory
  wake paths, enumerates the actionable wake sources — programmable, plus armed-but-fixed — and
  arms one named device at a time, re-reading programmability before it writes. There is no call
  that disables every source, and the power button, sleep button and lid are reported rather than
  writable. Verified against `powercfg` on the reference handheld: capabilities match `powercfg /a`,
  the device list matches `devicequery wake_programmable` and `wake_armed` exactly, and arming a
  disarmed device then restoring from the snapshot returned the machine to its exact prior arming.
  Writes need elevation; unelevated Windows fails them with `ERROR_WMI_SET_FAILURE`.

- #20's first pass, 2026-09-10, reopened and completed the next day (see above). Steam, Tools and
  Power present large category tiles and their controls live
  one level down as ordinary sub-views, so Back and B leave a category like any other page. A page
  opened from inside a category names that category as its parent, so one press moves one level.
  Two latent defects surfaced: leaving any non-`OverlaySubView` page called the format panel's
  return-to-origin, which silently did nothing for anything else, and switching destination closed a
  hand-written list of pages that had already gone stale. Both are driven by the stack now. Three
  new visual baselines and interaction cases for enter/leave, nested return and destination switch.

- #28 was implemented as below against the Steam Stable client and reopened on 2026-09-11: the
  September 9 Steam Client Beta reworked the library UI and added Big Art Mode, and the badge's
  injection point has not been revalidated against it; see the open-issue note above. As built,
  the library badge anchors under the hero art rather than over Steam's search
  bar, is larger, names the internal library from the absence of a map entry, and states connection
  with a glyph and a word rather than colour alone. A disconnected library keeps its remembered name
  because that name comes from the card's own marker. Hidden cards are pushed as well: hiding
  governs the tab, not where the game is. Seven payload cases pass, including that a library name is
  untrusted text from a card and is escaped before it reaches the script. The hero anchor has not
  been seen on a live page; a page without hero art keeps the old corner.

- #22 is implemented, and was already wired end to end before the issue was filed — the microphone
  Quick Settings work landed on 2026-09-01. `CoreAudio.AudioDirection.Capture` carries the volume
  both ways, `AudioManager` polls Windows once a second so external changes reflect back, and Steam's
  own audio store backs both the Quick Access section and the Settings audio page. What was missing
  was any test coverage of the capture half; four projection cases now cover it, including that a
  machine with no capture endpoint publishes no microphone volume rather than zero. Steam's Settings
  audio page was not re-opened to confirm the slider on today's client.

- #27 is implemented as far as it can be here. `Core\ModernStandbyPolicy` decides and
  `Shell\ModernStandbyGuard` acts: after a wake Windows attributes to the machine rather than a
  person, with the display dark and no input, the handheld is suspended again. Off by default, from
  Settings > System > Power. WSGM changes no power settings and arms no wake sources, so nothing
  global is left to restore — that shape came from decompiling Winhanced, whose sleep coordinator
  writes no scheme values at all, unlike Handheld Companion's Enhanced Sleep. The display gate is
  what stops it suspending a machine under someone's hands, because a gamepad does not advance the
  last-input time. Eleven policy cases pass. The issue asks for measured drain reduction, which
  needs standby sessions on the device, so it stays open.

- #40 is partially implemented. A WSGM 2.0 preset seeds a fresh configuration; one that already
  carries a splash section keeps whatever it says. Composed from the application's own tokens, with
  a bottom accent sweep, a ground a hair above black for OLED, and a caption at the muted text token
  rather than the older presets' #5F5F5F — roughly 3:1 on black for the line that has to carry a
  startup failure. A defect surfaced doing it: `Normalize` repaired an individual splash field to
  `new AppConfig().Splash`, which would have spliced 2.0 values into a Classic splash; field repair
  now targets `SplashConfig`'s own defaults. It stays open for the brand mark, which is not mine to
  invent, and for the resolution and DPI validation the issue asks for.

- #51 is implemented. Overlay > Tools > Display routes binds declared plugin actions, arguments,
  display targets and captured profiles to entry, exit, Desktop startup and Desktop wake. Saves are
  explicit and update only their owned policy. The logon manifest supports Desktop residency.
  Entry switches route, waits for an available target, applies its profile and places Steam there
  under a cancellable splash. Exit restores the Desktop profile before its external action.
  Failed takeover restores the pre-entry topology; still-running native work blocks subsequent
  route dispatch. Wake/startup requests wait for plugin readiness, recheck mode and coalesce duplicates.
  Release application/service builds are warning-clean. Focused route/session/logon checks, headless
  editor checks and WDC wait validation pass. Live HDMI, Steam placement and sign-in review is not
  claimed; the maintainer requested implementation closure with review later.

- #53 is implemented. The common SDK supplies structured widgets with stable identities, state
  predicates, icons and category navigation. Device and IR use the shared renderer. Source-page
  pinning and front-page unpin/reorder/reset persist across restarts and retain unavailable providers.
  Generation and availability checks reject stale actions; focus follows reordered or removed pins.
  The controller choice-editor test uncovered and fixed popup focus routing in shared navigation.
  Eight widget UI tests and 33 focused contract, persistence, adapter and input tests pass.
  These are headless checks, not a physical controller or live hardware pass.

- #61 is implemented. Overlay and QAM share manual mode, paired dispatch and independent saved
  unified/advanced preferences. Unified mode shows one editable TDP slider and both readbacks.
  Profile restoration uses the plugin pair and restores advanced boost under the mutation gate.
  Eleven policy/projection cases pass for this follow-up; earlier focused SDK, persistence, UI and
  toolkit checks remain applicable. Live AC/battery, reconnect and gameplay review remains.

- #59 is implemented: Tools offers current resolution/refresh and supported mode drafts with explicit Apply. Windows Device Control owns fresh enumeration, exact-route revalidation, readback and rollback. A headless picker test and warning-clean builds pass. Physical mode changes remain for field review.

- #38/#39 are implemented: Desktop notification icon, Settings focus, Game Mode entry and coordinated Exit; Start Menu and optional Desktop shortcuts start or activate the resident session. The activation test and 21 existing shutdown tests pass; the build is warning-clean. Installer and live tray review remain for field validation.

- #58 is implemented: Overlay Tools shares the session-owned brightness service with Steam QAM, including with CEF disabled. Confirmed reads update a retained slider without writes; unavailable displays disable it. Seven service tests and one headless UI test pass. Live hardware review remains.

- #52 is delivered and closed (2026-09-11). The XIAO ESP32-C3 enumerates on COM3. A common-SDK IR package now owns
  the USB endpoint protocol, persistent command/scene library, named actions and module-local firmware.
  The firmware builds for the reference board; source and hardware archive from Seeed were inspected.
  Tools now provides command selection, naming, relearning, repeat timing and scene management using
  common action forms and the controller keyboard. Real package loading alongside a Device-category
  fixture passes. Live learn/send verification remains open.
  Carrier provenance and a separate manual override are part of the initial payload/library/UI;
  actual frequency measurement remains unverified. A full 4 MiB factory backup and firmware upload
  succeeded through esptool 5.1's ROM path after the older stub stalled. The real C# plugin identified
  the flashed XIAO on COM3 as firmware 0.1.0/protocol 1. Eight plugin tests, three package tests and the
  focused controller-keyboard editor test pass.
  Live firmware checks also pass malformed/version rejection, invalid-send refusal and learn cancellation.
  On 2026-09-11 the plugin gained a network transport: firmware 0.2.0 serves the same protocol on TCP
  7521 with mDNS, credentials and a plugin-minted token are stored through a USB-only `wifi` operation,
  and network requests without the token are refused. Learn, send and scenes identify the endpoint on
  demand so #51 routes work after a restart without a manual Connect, and endpoint refusals read as
  instructions. Seventeen plugin tests cover the wire framing, cancellation, pairing and on-demand
  identification. Firmware 0.2.0 is flashed on the reference XIAO and passed the USB checks again plus
  invalid Wi-Fi argument refusal and learn timeout. The endpoint then paired and joined the network,
  resolved by mDNS, identified over Wi-Fi through the real plugin, refused unpaired LAN clients and
  reported a remote-less learn as a timeout instruction. Finally the plugin learned a real HDMI
  switch remote button over Wi-Fi as a 71-timing NEC frame and replayed it twice; the maintainer
  confirmed the switch changed to input 1 both times. Every acceptance criterion on the issue is met.
  Other appliances and carriers remain unverified and are field-review follow-up.

- #67 is implemented: one Tools keyboard action routes by current mode, with Steam-specific invocation
  in the toolkit and Windows using the existing touch keyboard. Sheet dismissal precedes invocation;
  keyboard visibility participates in ownership restoration. Failures reopen the sheet with a warning.
  Focused toolkit and WSGM tests cover the changed paths. Wrapped-game interaction remains for field review.

- #68 is implemented: Tools shows controller ownership and manual release/reacquire controls.
  Manual release takes precedence over surface closure and Steam exit, using the #65 adapters.
  Repeated transitions are rejected; explicit recovery and shutdown use the same ownership boundary.
  All 24 focused Steam controller tests pass. Live game/controller checks remain for field review.

- #66 is closed by `6cc870f`. SDL exclusion sanitization now covers every controlled child, including de-elevation
  without a lease and fallback after lease failure. Parent state and unrelated environment entries
  are preserved. All 81 launcher tests pass and the Release launcher build has zero warnings.
  Existing native sanitizer coverage and the dated 2026-09-08 Eden result remain applicable;
  no fresh live-game validation is claimed for this follow-up.

- #65 is implemented. Native pass-through claims and the toolkit's surface observer are
  pushed. WSGM registers overlay observation with the CEF session and has tested controller-pause
  operations and session-lifetime handoff policy. Physical/lease adapters and OEM QAM/Overlay
  dispatch are connected through exact-window native handlers. Main-window replay has live CEF
  evidence; game-overlay dispatch is not live-verified. An original-process handle detects Steam
  replacement even when the monitor misses its exit. Suspend, disable and runtime replacement now
  retire the old interaction without a stale replay, hardware reacquisition or stranded block.
  Physical disconnect/reconnect now waits for verified released instance IDs before blocking Steam
  and reacquiring once. Lease-only handoffs use the same native claims without device writes.
  WSGM closes its SDL readers during Steam ownership and waits for neutral input on return.
  All 78 focused tests pass. End-to-end hardware verification remains deferred to field review.

The maintainer directs issues to close when their fixes are committed and pushed; failed field
validation will be handled by reopening them. Hardware checks are not a closure gate. #60, #57 and
#56, #55, #62, #63, #19, #54, #29 and #25 are closed.

- #60: separate restore origin, bounded lighting readiness restoration and resume publication
  ordering implemented. Ten new hardware-free tests pass; fresh hardware validation remains.
  The attempted full gate stopped at pre-existing guidance-link failures in retired untracked
  device trees. Per maintainer direction, further validation runs only newly added tests.
- #57: confirmed brightness readback, ordered observations and suppression of programmatic slider
  writes implemented; six new service tests and emitted-JavaScript regression checks pass. Live focus,
  touch/controller and reconnect validation remains.
- #55A: shared limiter availability, coordinator admission and automatic release on limiter loss
  implemented; ten new tests pass. Shared runtime ownership now projects Custom in both surfaces,
  manual changes retire automatic dispatch and replace the old restoration target, and native
  slider completion ignores unchanged readback. Focused ownership tests and an emitted-JavaScript
  regression check cover these paths.
  The Overlay now refreshes retained sliders without scheduling user writes; its new control test
  also checks that pending edits survive readback and are cancelled when the control becomes unavailable.
- #56B: fixed the confirmed successful-probe stall and stable minimum/floor/maximum feedback;
  three new replay tests cover repeated descent, failed-probe recovery and `Can't Reach`.
  #56A paired writes and restoration implemented through an optional SDK command and the existing
  Claw ordered-write/rollback path. New service, SDK and fake-transport tests cover the pair;
  live hardware verification remains observational follow-up. The Windows-policy extension remains
  scheduled with #25/#35/#36 despite closing the immediate #56 controller repair.
  The architectural #25/#49/#50 work follows the repair lane.

- #62: container-based canonical Bluetooth collection implemented for both surfaces, with separate
  Windows action endpoints and stale-watcher rejection. Seven new catalog tests pass. Closes with
  its fix commit; #63 action dispatch follows immediately.

- #63: Steam Pair now starts Windows pairing with the shared prompt panel ready. Audio connection
  waits for readback, operation progress reaches Steam, and backend failures remain failures.
  Five new isolated action tests and an emitted-JavaScript regression check pass. Live pairing,
  reconnect and wake checks remain field follow-up.

- #19: Format uses query-only physical discovery; Eject includes letterless disks and a guarded
  media-eject path. Physical interface changes and periodic reader snapshots detect Linux media.
  Three new isolated discovery/layout tests pass; no live format or eject was performed.

- #54: bottom-edge activation is disabled by default; top remains Overlay. Settings names the
  optional Open apps action and its desktop exclusion. Existing explicit choices are preserved.
  Focused default and configuration round-trip tests pass.

- #29: native artwork preserves active-plugin control filtering. Individual viewer rows are hidden
  without removing shared rear-button sections, and stale profile CSS is reapplied. Two new focused
  glyph tests pass; installed Steam selector presence was checked offline, not through live CEF.

- #25: Windows power policy, lifecycle actions, source/status reads, wake-request ownership and
  decoding, wake sign-in primitives and notification registration moved to Windows Device Control.
  WSGM retains policy ordering, logging, dispatch and persisted recovery. New focused tests cover
  action admission, handle lifetime and recovery mapping; migrated decoding tests pass in the library.
  No live power actions or policy mutations were performed.

- #49A: common MIT contract assembly adds identity, open categories, host-owned slot
  policies, bounded manifests and resident lifecycle. The current Device runtime is unchanged.
  Eight new contract tests cover admission, compatibility, dependency ranges and optional Device slots.
  The Device compatibility adapter now maps this lifecycle onto the existing runtime; two new fixture
  tests cover resident transitions, resume generations and identity rejection. Production Device
  admission now uses the resident common host. Seven new host/fixture tests cover independent instances,
  singleton admission, stale UI publications, canceled work, slot retention and obsolete mode intent.
  Common configuration and state events now separate saved user revisions from origin-tagged effective
  observations. Three new SDK and eight new host tests cover schemas, stale edits, failed persistence,
  failed/mismatched delivery, serialization and bounded generation/sequence validation.
  Named actions now validate generation and argument schemas, distinguish dispatch from verified
  effects, and never retry uncertain results. Declarative UI links are validated before startup;
  six new focused tests cover dispatch, rejection, confirmation and shutdown cancellation.
  Common collectible loading reuses SDK/WinRT identity rules. A non-device package fixture validates
  configuration, actions, contributions and resident transitions. Five new tests cover that path,
  metadata admission and dependency ordering/rejection without disabling independent packages.
  Installed metadata discovery and explicitly enabled instances now integrate with Shell startup,
  configuration reload, power transitions and shutdown. Ten new tests cover metadata-only discovery,
  independent enable/disable, obsolete startup cancellation, retained failures/loading tasks, power
  deduplication and revision refresh.
  Settings now merges explicit instance activation edits; Overlay Tools renders common status,
  action, toggle and slider contributions with drafts separate from confirmed readback. Common
  project scaffolding and create-new archive tooling accompany the existing Device Lab specialization.
  The final surface/tooling slice uses a warning-clean application build, script syntax checks and
  successful generation/build/packaging of the harmless example, without
  another test suite run. Generic common-preference editors and pinning are follow-on UI work;
  common schema/persistence contracts and Device settings remain available. #49 foundation complete.

- #50: Windows Device Control now captures versioned CCD display profiles, rematches monitor
  device-path/EDID identities after hotplug, waits for target presence, validates supplied topology
  before a display write, and captures/attempts rollback after rejected or unconfirmed application.
  A multi-target warning-clean build passed. DisplayMagician informed the transient-route versus
  persistent-identity design; the MIT implementation uses documented Windows contracts. WSGM's
  existing profile flow remains available, and #51 consumes the reusable API for Desktop-first
  orchestration. Closes with its fix commit; no live display change was run.

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

Desktop Mode design agreed with the maintainer on 2026-09-11. The reference machine is the desktop
PC from #51: it shares an HDMI switch with a TV box, and the TV exposes no EDID while the switch is
on the other input. Inventory at that date: #38/#39/#50/#51/#52 supply the notification icon,
shortcuts, WDC profile primitives, route bindings and the IR endpoint; none of the items below was
complete. Build order follows the list.

- [x] **Desktop Mode is a complete resident WSGM session, not a reduced agent.** Settings offers two
      independent choices: start WSGM at sign-in, and start in Desktop or Game. Desktop residency
      must not depend on route automation (`BootManifestWriter` currently derives it from
      `DisplayRoutes.Enabled`). Desktop Mode keeps the device/capability plugins, overlay, keyboard
      hotkey, controller chord, running-application monitor, performance services, Steam
      integration permitted on the desktop, config watching and the notification icon, which is
      shown in Desktop Mode only. WSGM starts Steam without Big Picture when it is not already
      running, so Steam inherits WSGM's elevation instead of relying on a user autostart or
      scheduled task. Explorer remains the shell and game-mode-only effects stay off: no takeover,
      replacement tray host, Game display layout, startup-app sequence or Big Picture request.
      Returning from Game Mode restores this same fully running Desktop state.
      Implemented as phase 1. `StartAtSignIn` and `StartMode` replace `GameModeBootEnabled`, with a
      `Core\ConfigMigrations` JSON pass ahead of typed deserialization so a load and a mutation see
      the same values. One `Core\ElevationPolicy` now serves both `SelfElevation` and the manifest's
      `Elevate`, and starting Steam at WSGM's integrity is one of its reasons. `Steam.ColdStart`
      backs both `LaunchBigPicture` and the new `LaunchDesktop`, so the shim reconcile, debug port
      and integrity choice cannot diverge. `SteamMonitor.Paused` now means only that a transition is
      in flight; an explicit Close Steam sets `SessionModes.SteamClosedByUser`, and
      `Shell\SteamExitPolicy` keeps the desktop from ever popping the overlay. 117 focused managed
      tests and 5 Settings UI tests pass. No live sign-in, Steam or service validation was run.
      Phase 2 added the Steam autostart takeover: `Core\SteamAutostart` finds Run values, Startup
      shortcuts and logon tasks that launch Steam, `SteamAutostartTakeover` disables them through
      Windows' own `StartupApproved` bytes with the previous state recorded before each write, and
      the elevated `--disable-steam-autostart` and `--restore-steam-autostart` one-shots cover
      machine scope and the uninstall restore. Quick Setup revision 2 asks the sign-in choices and
      refuses Continue while entries are found and the takeover is not allowed. 25 scanner and
      takeover tests pass with a fake startup surface; the live takeover on the reference PC is
      maintainer-attended.
      Phase 3 gave the IR host the endpoint's built-in remotes: `remotes`, `press`, `climate`,
      `run` and `cancel` on `IIrEndpoint`, the 0.4.0 identity fields, and four plugin actions whose
      free-text ids are validated against the endpoint's own catalog with one refresh on a miss.
      Old firmware, unknown ids, undeclared climate states and a busy endpoint are refusals that
      emit nothing. 28 plugin tests pass; no IR was emitted.
- [x] **Add editable display layouts to Windows Device Control.** Capture the current arrangement
      into an editable layout: active targets, primary, position, resolution, refresh rate and HDR,
      keyed by stable target identity. Never make a user hand-author raw `DISPLAYCONFIG_*` data.
      Expose verified apply with readback of paths, modes and primary, and the currently active
      outputs. Keep a catalog of every display seen, with identity, supported modes and HDR
      support, so an absent display can still be configured. An absent designated target is a
      retryable waiting state, not a failed transition. DPI applies after the layout establishes
      which targets exist. Include crash, cancellation and Desktop rollback coverage so a layout
      change cannot strand the session without Explorer or a usable display.
      Phase 4 delivered the library half in `external\windows-device-control`: `DisplayLayouts`
      observes every monitor with a stable fingerprint, captures the desktop as values, and
      validates or applies a layout with readback and one rollback. `DisplayLayoutPlanner` holds the
      rules (one primary at 0,0, no duplicates, no overlaps, connected) and builds the supplied
      configuration; `DisplayScaling` and `DisplayColor` own per-display scaling and HDR by target
      identity. An absent monitor returns `TargetsAbsent`, and an already-matching arrangement is
      not rewritten. 13 new tests run on synthetic path arrays; 106 WDC tests pass. The display
      catalog and WSGM's crash and rollback coverage belong to the phases that consume this.
- [ ] **Wait for display arrival is a release-blocking Game Mode path.** Support the reference setup
      where the inactive HDMI-extractor input exposes no EDID and Windows therefore has no TV target
      to configure. A Game Mode request from Desktop keeps the complete WSGM session and Explorer
      running, leaves the Desktop layout untouched, and shows an actionable waiting line on the
      splash until the designated TV arrives, the user cancels, or WSGM shuts down. It must not
      inherit the boot splash's 120-second timeout or the current 1–120 s route deadline, proceed
      without the target, start Big Picture or run game-mode startup applications while waiting.
      A plugin action may ask the extractor/TV to switch first, but Windows display arrival remains
      the authoritative gate. On `WM_DISPLAYCHANGE`/`WM_DEVICECHANGE`, confirm the target through
      `QueryDisplayConfig`, wait for two identical enumerations 500 ms apart, then continue the same
      transaction automatically. If the target disappears again before Explorer exit, return to
      waiting without partially entering Game Mode; disappearance after Game Mode is established is
      non-fatal. Cover the state machine with synthetic tests. Live observation on the exact
      extractor/TV path is optional maintainer-directed diagnosis, not a completion gate.
- [ ] **Make on-demand Game Mode one cancellable, fail-open transaction.** The overlay and the
      notification icon begin it from Desktop Mode. Show the WSGM splash over the live desktop, run
      the configured plugin actions, display every prerequisite that is still waiting, and allow
      cancellation before Explorer exit without changing WSGM's shell or display state. After the
      prerequisites resolve, capture the verified Explorer recovery anchor, exit Explorer, apply the
      destination layout in order, and enter the existing Game surfaces/Steam launch path. Today
      the profile is applied and Big Picture requested before Explorer exits; that order changes. A
      failure after the irreversible boundary compensates successful steps in reverse order and
      restores the Desktop layout and Explorer. External actions such as IR or Home Assistant calls
      are best-effort/compensated; they cannot truthfully promise that no external side effect
      occurred.
- [ ] **Game Mode launch configuration in WSGM Settings.** Default keeps today's launch on the main
      display. Custom shows the launch editor: which displays are active, which is primary, each
      display's position, resolution, refresh rate, DPI and HDR, an optional display wait, and
      optional plugin actions chosen per enabled plugin with their arguments. Snapshot fills the
      editor from the current Windows arrangement, DisplayMagician style, and Save stores it.
      Saving changes nothing live; test actions that switch displays or fire external devices stay
      in the overlay. Leaving Game Mode returns to a configurable layout: the arrangement captured
      at entry, persisted for crash recovery, or a configured Desktop layout from the same editor
      with its own Snapshot. A Desktop section keeps #51's Leave Game Mode, Desktop startup and
      Desktop wake plugin actions, so a switch that auto-selects the PC on wake can be sent back to
      its preferred source. This replaces the fixed Desktop/Game profiles in Settings > Display and
      the Overlay > Tools > Display routes editor, with migration of existing configuration.
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

- [ ] **Author IR commands from known codes.** The maintainer has lost the remotes for the Hisense
      TV and a Koenic air conditioner, so learning is unavailable for both. Add commands from
      protocol codes: first NEC/NECext encoded to raw timings, to test public Hisense power
      candidates such as Flipper-IRDB entries, then stateful air-conditioner protocols where one
      frame carries mode, temperature, fan and swing. Identify the Koenic model's protocol before
      building AC support; Koenic is not listed by name in the maintainer-supplied reference
      [pyhvac](https://github.com/frawau/pyhvac) (MIT, derived from IRremoteESP8266, 70+ brands
      including Midea, Gree, TCL and Haier), so the unit's OEM protocol must be matched first.
      Check the upstream license provenance before porting any frame encoder.
      Encoded commands report their carrier as protocol-derived, not measured.
      Most TV power codes toggle, and an endpoint acknowledgement proves emission only; where the
      HDMI chain allows it, display arrival confirms power-on.
      Progress 2026-09-11: Hisense's published discrete NEC power on/off codes work on the
      maintainer's TV. Firmware 0.3.0 adds `sendCode`, `sendAc` and `protocols` and fixes long USB
      frames. The Koenic KAC 12020 (no Wi-Fi module) answers the library's `MIDEA` A/C protocol for
      power, Cool/Auto/Fan/Dry, set point, fan speed and swing. Midea swing is a toggle, so a host
      A/C action must model it as an explicit toggle rather than stored state.
      Firmware 0.4.0 bakes remote definitions and optional pages into the image at build time and
      serves them over HTTP with Basic authentication; the Hisense example is tracked, the switch
      and air conditioner live in the untracked `remotes.local`. Remaining: the WSGM plugin lists
      and presses built-in remotes (`remotes`, `press`, `climate`, `run`), the switch audio-reset
      delay is tuned live, and host commands from codes cover endpoints without built-in remotes. Remaining: host commands authored from codes and A/C states, and a switch
      power-cycle scene whose delay is long enough for the second press to register.

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
- [x] **Give removable libraries one owner.** The card volume monitor decided registrations and
      applied them itself, and a media-level eject — which leaves the card in the reader for Windows
      to remount within seconds — put back the library the user had just ejected, from either
      surface. `Shell\LibraryPolicy` now owns adopt, eject, format and what an arriving or departing
      volume means; the monitor detects and reports, in every mode and from every startup path
      (it was game-mode-only, and the desktop-startup path never started it, and it bailed before
      scanning when Steam was not up yet — three separate reasons a pulled card stayed in Steam).
      An eject is an intent that survives the remount, matched on the card's identity, cleared by
      the media leaving, a different card, or an explicit adopt. Verified live on the reference Claw,
      2026-09-11. `docs\sd-cards.md`.
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
