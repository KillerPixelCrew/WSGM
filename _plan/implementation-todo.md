# WSGM 2.1.0 refactor

Implementation follows the [simplified plan](refactor-2.1/refactor-plan-v2.md), the verified
[findings](refactor-2.1/findings/README.md) and binding
[maintainer decisions](refactor-2.1/DECISIONS.md). Source implementation and automated validation
are finished. Live/manual acceptance remains outstanding; older entries below record earlier source
and validation states.

## Steam Settings issues 209–212 and sound packs 202, 2026-10-06

- [x] Extend native Display, Power, Audio and Controller Settings using the existing QAM owners.
- [x] Complete sound-pack compatibility, update identity, corrupt-asset fallback and decoder status.
- [x] Compile and stage the development package without changing the release version.
- [ ] Deploy after the running application is closed or the maintainer authorizes its restart.
- [ ] Maintainer live Steam/device pass, including Stable/Beta sound restoration and navigation.
- [ ] Run deferred focused tests and the initial gate after that manual pass.

Source is applied, with warning-free Release compilation and a current generated Steam asset.
The installed native Settings source fingerprints matched uniquely. The whole-client fingerprint
check reports the pre-existing Screensaver section unavailable on this client; the new native
Display timeout controls use the same owner independently. Rider cleanup and formatting completed.
WSGM and Steam remain running, so the installation has not been replaced. Rumble calibration
feasibility is documented under Native Steam Settings; user calibration is optional in issue 212
and has not been implemented. All five issues retain their live acceptance gaps.

## Active continuation, 2026-10-05 to 2026-10-06

Source verification used at most six concurrent agents, below the requested maximum ten, and
reopened stale done claims. The 190-item index now has 34 historical Implemented, 154 Source
applied, one maintainer No change and B179 In progress. Full goal remains active.

Applied corrections include immutable configuration/declaration normalization, stable display
operations, shared Overlay/QAM power selection, user-command closure with AutoTDP final restore,
truthful package/GPU/IR recovery, safe storage accessor recognition, complete Lab evidence and
restore-before-result ordering, testable destructive format/setup operations and the missing
one-shot account checks. Finding mappings and no-change dispositions are reconciled; the manual
matrix includes M01-43 to M01-54. Frozen Avalonia, VIIPER and .gitmodules have no foundation diff.

Executed validation: warning-free Release compilation; composed and standalone toolkit emitted
checks; all 562 toolkit tests; 174 WDC tests on each supported framework; 437 Lab tests; 216 Device
SDK, 128 Ally, 223 Claw, 22 common SDK, 201 Intel, 36 NVIDIA, 12 AMD and 78 IR tests. IR firmware
0.5.0 compiled with pinned PlatformIO/ESP32 dependencies and all five catalog-generator tests
passed; nothing flashed. Latest complete application suite: 3,726 passed.

B179 remains open for manual acceptance. The initial full gate stopped after Rider changed
DevicePageCaptureTests.cs; the reviewed file passed focused idempotence. Import-order conflicts were
corrected without changing formatter rules, then style, analyzer and warning-as-error solution build
checks passed. All non-UI solution test projects passed, including 3,726 application tests with
coverage. Final UI suite: 271 passed. Corrections preserve keyed folds through busy publications and
disable native Expander content transitions only in captures. All 42 changed baselines were
reviewed. A documented eight-pixel opaque raster allowance applies only inside the unchanged
Overview icon in graphics captures; comparisons elsewhere and alpha changes remain strict. Final
affected Rider cleanup is idempotent; affected style/analyzer checks and the final Release solution
build passed. All four developer tools built with zero warnings/errors. The stopped gate's remaining
checks were completed through these affected rechecks; its failed runs remain recorded rather than
described as an uninterrupted pass. Toolkit 6396f27 and WDC 79dae8b are committed and pushed; the
parent records both gitlinks. No installer release, deployment, live Steam or hardware operation
occurred. WSGM and Steam are still running, so their installed files were not replaced.
Logs/toolchains are outside the checkout; renders remain in TestResults. Requirement 16's complete
manual regression matrix remains Pending. These checks do not establish live acceptance.

Final committed-head verification, 2026-10-06: the complete eng/verify.ps1 passed on 10276262,
including solution-wide Rider idempotence, style/analyzers, warning-as-error Release build,
application coverage, 6,265 tests across all 16 test targets and all four tool builds. The repeat
was warranted by the final shared UI capture infrastructure changes. The earlier failed runs above
are preserved. B179 and the full goal remain open solely for required attended manual acceptance; no
live operations are authorized by this verification result.

## Previous handoff to Codex, 2026-10-05

Plan items: 36 Implemented, 143 Source applied, 1 No change, 7 In progress and 3 Pending (B177 to
B179). Source applied means the source work is done and Release compiles with zero warnings; no
tests have run. Every status is in `refactor-2.1/batches.json` and the plan table.

State of the tree:

- master is ahead of origin and unpushed, as the plan defers pushes. The Steam asset is current.
- The WDC child has unpushed commits 19c89b4, cc77bc8, b14c704 and c04c421; the toolkit child has
  16a2d05, 8b03384 and c2bbe03. Neither gitlink is recorded in the parent yet: push each child
  before committing its gitlink.
- The last commit holds partial batch 7 work that was interrupted: a fault-free
  `CommandResults.Unverified(command)` overload (GPUIR-008) whose GPU callers in
  `src/Shared/Gpu/DriverRuntime.cs` and `IntelGpuPlugin` still use the TransportFaulted overload;
  removal of the LABCORE-027 Device Lab process bounds; the Device Lab AGENTS.md storage wording;
  and IR firmware texts. All of it compiles but is unreviewed.

Remaining work, in order:

1. Verify the 62 source-applied items from batches 4 to 6, as batches 1 to 3 were verified
   (verification reopened 31 items so far; all are fixed except those listed below): B122, B125 to
   B132, B074, B078, B082, B115, B116, B124, B140, B141, B098, B102, B136, B138, B139, B047, B051,
   B056, B052, B142, B143 to B149, B034, B172, B173, B150 to B156, B175, B176, B072, B157 to B171.
2. In progress: B101 (display-power signal shared by the mute service and standby guard,
   WINSVC-025), B123 (top-level `PluginActionOption`, SETTINGS-015), B174 (cold-start order test
   seam, qam-harness allowlist), B088 and B097 (test seams, test phase), B062 (toolkit test quality,
   test phase), B049 (needs an attended read of the transport module source on live Steam, by
   literal id, never during a Steam cold start).
3. Finish the partial GPUIR-008 switch above, the LABCORE-033 manager flag and the sleep-stage
   truthfulness fix (B171), and the LabWmi quarantine SidePath (B161).
4. B177 documentation pass (docs/device-integration.md still describes the deleted Device
   compatibility adapter; IR texts for protocol 2; manual matrix rows from plan section 5 and the
   batch notes below), then B178 finding reconciliation and B179 final validation: warning-free
   build, toolkit prelude claims and `steam-assets:claims`, affected tests, independent WDC and
   toolkit validation, UI baseline review (profile editor, Display fold) and the full gate once. The
   IR firmware 0.5.0 needs a PlatformIO build before any flash.

## Workflow batches, 2026-10-04 and 2026-10-05

Work ran in agent batches of at most ten, each followed by one solution compile and a commit.

Batch 1 finished the nine in-progress items as source applied: B024 (Setup closure complete, new
B189 and B190), B030, B037 (test callers moved to an explicit temporary store), B080, B097, B099,
B101, B112 and B114. It also re-verified the 36 Implemented items against current source. Ten were
reopened as In progress for real gaps; their fixes go into the next batch:

- B006: `CrashLoopBreaker.Reset` still runs after a failed shell startup (SESSION-002).
- B009: NativeHidHide 1 MiB multi-string cap; unreadable ledger throws from hide and skips
  uninstall's own-executable removal; one bad foreign entry aborts the show; release has no
  free-gate fast path after its deadline (INPUT-001/002/034, INPUT-V-003).
- B012: resume failure filter drops the resume-deadline cancellation; unlock teardown is not
  reported (DEVICE-V-002).
- B015: LIBRARY-V-006 unused `maximum` parameter remains; blank import identities now load
  (LIBRARY-003).
- B017: `WindowsPowerModes.Apply` still throws on readback mismatch; 64-profile cap; written-value
  caches added against WINSVC-010.
- B018: theme update journal 128 KiB cap remains (CONFIG-038, STEAMHOST-V-001).
- B020: unguarded passed power arm, mismatch outranks failed, missing Claw fan coverage arm, stale
  fan label and comments (LABUI-002, LABUI-V-002, LABUI-021).
- B021: dead Claw reassert throttle (PACKAGES-002).
- B022: Ally fan factory release catches too narrowly (PACKAGES-005).
- B183: dead `PackageDebugRecoveryRecord.Remove` still public (INSTALL-C-005).

Unowned observations to check: INPUT-C-001 (record an empty ledger before cloaking) has no item;
`--restore-shell` may start Explorer without checking `ExplorerShellAnchor.HasRecoveryOwner`.

Batch 1 checkpoint was committed as b42713db after the compile repair agent restored a warning-free
Release build. It deleted tests of deliberately removed types (DeviceTeardownFailureTracker,
SplitLetters, a removed DecideResume parameter) and moved adapter tests onto DevicePluginRuntime.

Batch 2 closed the reopened gaps in B006 (no crash-loop reset after failed startup;
`--restore-shell` now waits up to 15 s for a live recovery anchor before its own Explorer start),
B012, B015, B017, B018, B020, B021, B022 and B183, and implemented INPUT-C-001 under B009. Source
applied: B068, B076, B077, B079, B088, B091, B092, B093, B189 and B190. The UiTests harness now
passes a SteamInputBlocker that never takes the lease.

Reopened after verification of B031 to B045:

- B009: `ControllerManager.DisposeAsync` lets the deadline cancellation escape instead of logging it
  (INPUT-001); a failed ledger save escapes `HideAsync`.
- B032: verify.ps1's format check ignores untracked C# files (BUILD-002).
- B038: 512/256 audio endpoint caps reintroduced (CONFIG-014); layout structural check lost with
  NormalizeLayout (WDC-002); duplicate Splash summary (CONFIG-041); PointCount doc (CRIT-005);
  unused `ProfileConfig.MaxNameLength`; a second enum walk per section.
- B039: `--uninstall-restore` exit code depends on machine settings (CONFIG-005 regression); setup
  answers export fails closed (CONFIG-004); ArtworkStateStore throws to uncaught callers and
  QuickAccessFolds refuses forever after a parse failure (CONFIG-037); plugin Steam commands miss
  ConfigUnavailableException (CONFIG-V-001); silent boot.json skip (CONFIG-003); mutex timeout reads
  as Unreadable (CONFIG-004/012).
- B042: spatial format validated against pre-change capabilities (CONFIG-026 regression);
  AudioProfileService hides its default operations (CONFIG-025).
- B044: the SDK keyboard hook stops and faults after one throwing callback (SDK-005); a legacy
  motion delivery exception ends the poll thread (SDK-006).

Recorded for the maintainer: 2.1 drops pre-2.0 retired config keys that a 2.0.x install never saved
over (a27e049b removed those migrations deliberately). The B093 suspend path cancels a pending
controller start before the plugin suspends; a publication during that call could still start one.
docs/device-integration.md still describes the deleted Device compatibility adapter.

Batch 2 was committed as cedd830e. Batch 3 changed the Steam UI toolkit (child commit 16a2d05, not
pushed, gitlink not yet recorded) and the Steam host. Source applied: B047, B048, B050, B051, B053,
B055 to B061, B084, B085, B087, B136 to B139, and the reopened B032, B038, B039, B042 and B044. B009
is closed again. The toolkit prelude, the WSGM Steam asset and both solutions build without
warnings; prelude and asset checks, tests and the attended Steam checks are deferred. The toolkit CI
action SHA pins from TOOLKITJS-038 were reverted to keep the tags, matching the BUILD-009 decision;
the lockfile name fix stays.

Still in progress:

- B049: the storage gate still calls the transport module's exports once at install. Choosing the
  provider by source tokens needs an attended read of that module's source on the live client, by
  literal id and not during a Steam cold start.
- B054 and B086: `SteamUiBridgeHost` needs a replaceable allowed-command vocabulary so plugin
  modules added through `ReplaceModulesAsync` are authorized; then wire plugin readiness to it
  (STEAMHOST-006). The `native-qam:` correlation prefix rename (TOOLKITCS-038) is also open.
- B062: the C# toolkit test-quality findings (TOOLKITCS-064 to 068) wait for the test phase.

Open notes from batch 3: the bridge's 32 M-character delivery cap is not a D2 bound and still
refuses large picker listings (TOOLKITJS-V-008, TOOLKITCS-031); STEAMHOST-018 (download sort as a
gate) is not done; `PendingReturnLayout` has no structural check; GameLibraryService settings writes
lack a ConfigUnavailableException catch (CONFIG-V-001); B057 publishes an accent flag and lets the
toolkit add the label instead of host-built status text, with the same visible text. B136 placed
Steam admission closing and deadline-bounded joins in shutdown; B140 must keep them. M01-53 now
covers `--restore-steam-content` and setup's boot movie and themes restore step.

Batch 3 was committed as c62cdb33. Batch 4 (interrupted once, resumed from its run cache) changed
Windows Device Control (B046, B063 to B067, B069 to B071; local child edits), Windows services
(B090, B096), Settings (B117 to B121, B123, B133 to B135), the overlay (B122, B125 to B132) and
finished the bridge command vocabulary with dynamic plugin Steam UI modules (B054, B086). All are
source applied. WDC now builds on its own and tests both frameworks; its AGENTS.md changes were
applied under D4 and go with the child commit.

Reopened after verification of B073 to B113:

- B074: controller status is still three unsynchronized properties (INPUT-014); `ReportTargetFault`
  still produces Faulted, Idle, Faulted (INPUT-031).
- B078: `_steamDeckTargetActive` is still written off the UI thread (INPUT-013); the chord mirror
  keeps its SHA-256 overflow comparison (INPUT-023).
- B082: `ObserveLateCommandAsync` is fire-and-forget and not joined at disposal (DEVICE-006/031);
  `ManualTdpMode` snapshots every capability per read (DEVICE-021).
- B098: stale `TryApplyTransientRefreshRate` remark and the `DisplayProfiles` rename (CONFIG-028,
  WINSVC-027).
- B102: artwork types stay public without outside consumers (LIBRARY-039).

Other batch 4 notes: `NativeQamNetworkService` still caps the access point list at 24; a stale glyph
remark in `OverlayWindow.Device.cs`; `AttachBrightness` may replace a folded section header. For the
maintainer: B121 now cancels an update download when Settings closes, which SETTINGS-018's checker
advised against; B130 leaves DisplayModeView's own WDC read/apply defaults and separate
select-and-persist code in the overlay picker and the Quick Access service. Profile editor UI
baselines changed on purpose (B129) and need a refresh and review.

Batch 4 was committed as 5f925156 (WDC b14c704, toolkit 8b03384, both local). Batch 5 is source
applied for session (B115, B116, B124, B140 shutdown order, B141), SDK (B052, B142, B146 to B149),
packages (B143 to B145), GPU (B150 to B153) and IR (B154 to B156, protocol 2 catalog paging), and
closed the reopened B074, B078, B082, B098 (DisplayProfiles renamed PrimaryDisplayModes) and B102.
The 24-entry access point cap in the Quick Access network projection is gone.

Reopened after verification of the batch 3 Steam items:

- B047: checks still slice adjacent declarations instead of whole fragments (TOOLKITJS-011).
- B051: module resolver keeps 16-token, 512-character and 32,768-id bounds (TOOLKITJS-005); an
  rpc.ts comment names a webpack module id (TOOLKITJS-007); check-ownership-claims still uses its
  private helper (TOOLKITJS-036); no registerSteamPage check case (TOOLKITJS-V-003).
- B056: toolkit reference still documents removed caps (TOOLKITCS-031).
- B136: card services still live in the Steam UI partial (STEAMHOST-046, placement).
- B138: ThemeService still does loader file I/O under its state lock (STEAMHOST-030).
- B139: download sort is still a C# raw-string script outside the asset pipeline (STEAMHOST-018);
  themes and animations keep duplicate page helpers (STEAMHOST-034).

Batch 5 notes: IR firmware 0.5.0 is not compiled here (PlatformIO venv missing); build it before
flashing. Removing `CommandResults.Unverified(command, value)` makes applied-but-unconfirmed GPU
writes carry a TransportFaulted reason, the noise GPUIR-008 complained about; check it. Shutdown now
bounds the mode transition, boot worker and gate loop by the deadline and disposes UI-built owners
on the dispatcher; the boot cover arms only when the launch sequence asks Steam. These need the
shutdown and boot manual rows. The overlay Display layout test attaches brightness after the window
opened, so its baseline is unfolded unlike production. Intel's lifecycle was not merged onto
DriverRuntime (GPUIR-007), by choice.

Batch 5 was committed as 964a17d8. Batch 6 is source applied for Device Lab (B157 to B171), build
(B034, B172, B173 with the D3 GPL relicense, B175, B176), WDC docs (B072, new
WindowsDeviceControl.slnx) and the reopened B047, B051, B056, B136, B138 and B139 (the download sort
is now a gate fragment; themes and animations share a page kit). tools/DeckSpike and
tools/SteamReceiver are deleted (D12).

Open after batch 6:

- B174: the cold-start order behaviour test needs a seam on the static `Steam.ColdStart`;
  `tools/WsgmLibTest/qam-harness.mjs` still scrapes toolkit surfaces and needs a C#-emitted
  allowlist. `eng/check-steam-module-discovery.mjs` was already failing before B174 fixed it.
- B088: three test-only static delegate indirections remain in DeviceCoordinator (DEVICE-042), tied
  to the deferred DEVICE-014 owner tests.
- B097: the format run has no seam for its destructive calls (WINSVC-007), test phase.
- B101: `ModernStandbyGuard` still borrows the mute decider's display signal (WINSVC-025).
- B123: `PluginActionOption` is still nested in SettingsViewModel and built by Shell (SETTINGS-015).
- LABCORE-027 process bounds (SelfWorkerProtocol, probe stderr/response/request bounds) are not
  removed; the finding names B162 and the appendix B161, and neither did it. LABCORE-033's manager
  flag is unowned. The sleep stage reports Completed when the curated init cannot be put back.
  Device Lab AGENTS.md still says per-step storage is bounded (B177).
- docs/steam-cef.md names webpack module ids and the deleted `SteamDownloadSort.ScriptVersion` as
  dated evidence; left for the maintainer.

- [x] Simplify the active plan: remove prescribed owner/port proliferation, serial dependency
      chains, per-item review/validation ceremonies and duplicated architecture prose. Keep all 188
      item IDs, statuses and finding dispositions. Findings remain evidence; their proposed designs
      are optional. Align the JSON index, findings guide and historical requirements with the
      current instructions.

- [x] B001: correct the EDID identity validity bit. Both WDC targets built cleanly; 12 filtered
      cases passed.
- [x] B002: isolate power actions from tests. Both WDC targets built cleanly; 18 filtered cases
      passed.
- [x] B003: enforce the IR UTF-8 reply bound and document ownership. Release build clean; 18
      connection cases passed.
- [x] B004: keep failed safety zeroes armed. Release build clean; 24 filtered worker cases passed.
- [x] B005: start and keep RTSS alive for the enabled service lifetime; apply overlay level without
      a game executable.
- [x] B006: route exits through one bounded cleanup owner and preserve OS session-end and
      startup-failure behavior.
- [x] B007: preserve the HidHide recovery ledger when uninstall cannot restore; start WSGM only
      after successful setup.
- [x] B008: retire the device slot after a completed stop, including Failed and Unverified outcomes.
- [x] B009: recover the physical controller on cancellation and inactive startup; clear stale
      virtual routes, release synthetic buttons on refusal, and bound plugin release/emergency stop
      without unloading code still running. All 125 affected Release cases passed after Rider
      cleanup; full Release solution compilation had zero warnings/errors. Prettier and guidance
      checks passed.
- [x] B010: distinguish absent and unreadable recovery records, reject undefined statuses, preserve
      in-flight managed gates, and use active deadlines for post-command publication and startup
      rollback. All 38 SDK service/trace, 5 MSI consumer and 7 Ally consumer cases passed after
      formatting. Full Release solution compilation had zero warnings/errors; Rider cleanup,
      Prettier and guidance checks passed. Live/manual acceptance remains open.
- [x] B011: retain AutoTDP control and original-watts restoration across device cycle changes.
- Validation for B005–B008 and B011: 255 targeted Release test cases passed; Rider cleanup,
  Prettier, Steam asset drift and guidance checks passed. Live/manual acceptance remains open.
- [x] B012: restart after failed unlock resumes, retire passive runtimes without calling an
      unstarted plugin's stop, and remove the OEM router's unused cycle generation. All 103 affected
      Release cases passed after Rider cleanup; full Release solution compilation had zero
      warnings/errors. Prettier and guidance checks passed; live/manual acceptance remains open.
- [x] B013: merge Settings-owned fields onto the fresh strict configuration, share one field table
      with Steam, preserve runtime media/recovery state, and merge plugin/profile edits through one
      path. All 105 Settings/Steam and 31 isolated Settings UI cases passed after formatting. Full
      Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance
      checks passed. Two reviewed Display baselines were refreshed for the footer change already in
      `a27e049b`. Live/manual acceptance remains open. The refactor plan now carries batch status
      directly.
- [x] B014: add exact composed-shortcut golden fixtures for packaged, launcher, direct, folder and
      follow routes, including Amazon discovery order and drive-root refusal. All 31 golden cases
      passed.
- [x] B015: source fixes applied for library helper identity, import-record shape handling, command
      refusals, cancelled-scan pruning, update read-back and absolute folder validation. All 806
      required library cases passed, including B014's 31 golden cases. Full Release solution
      compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed.
      LIBRARY-V-006 was already absent in the baseline. The stale Overlay navigation test was
      updated to the existing Tools/System route from `79789dc2`. Live/manual acceptance remains
      open.
- [x] B016: all 27 targeted cases passed after formatting. Recording failures now refuse takeover
      writes, accepted task/approval writes need no confirming read, and restore still checks the
      exact owned marker. Full Release solution compilation had zero warnings/errors; Rider cleanup,
      Prettier and guidance checks passed. No live startup settings were changed; manual acceptance
      remains open.
- [x] B016 follow-up: B180 removes UNCOVERED-002's task-restore pre-read; accepted enabling writes
      return the record and refused writes retain it. The earlier recording/write changes remain
      applied.
- [x] B017: source changes applied for power writes, Overlay/QAM written-state publication and
      display-timeout selection, including the Overlay timeout event path. Confirming reads and
      refresh-required write gates are removed. All 102 required power cases passed after
      formatting; full Release solution compilation had zero warnings/errors. Rider cleanup,
      Prettier and guidance checks passed. No live power settings were changed; manual acceptance
      remains open.
- [x] B018: all 101 targeted cases passed after formatting for bounded/generated theme journals,
      startup recovery errors and busy-state cleanup.
- [x] B019: result-page Continue preserves the next operation and refuses concurrent restoration
      handoff. All 205 GUI/wizard cases passed using isolated fixtures after formatting.
- B018–B019: full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and
  guidance checks passed. No live Steam changes or hardware stages ran; manual acceptance remains
  open.
- [x] B020: review classification, fan feature label and rumble method text are applied. All 18
      targeted review cases passed using exported fixtures after formatting, including absent
      restoration and mixed incomplete/completed runs. Full Release solution compilation had zero
      warnings/errors; Rider cleanup, Prettier and guidance checks passed. No hardware probes ran;
      manual acceptance remains open.
- [x] B021: Claw timeout classification, pre-write refusal, watchdog disarming and periodic-only
      reassertion are applied. Undeclared scenarios publish null; dead rollback/descriptor code is
      removed. All 137 targeted capability/plugin/model cases passed with fake transports; budget
      refusal leaves no restore entry or shutdown write. Full Release solution compilation had zero
      warnings/errors; Rider cleanup and guidance checks passed. No hardware run; manual acceptance
      remains open.
- [x] B022–B023: Ally commands/restores write through without readbacks, power follows HC's
      SPL/SPPT/FPPT order, and written values win for the cycle. Failed restores stay Pending;
      command failures perform no rollback or retry and leave services usable. Aura caching,
      controller publish, redundant haptic locking and OEM Clear are corrected; approved D4 guide
      changes are applied. All 66 targeted fake-transport cases passed after formatting. Full
      Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance
      checks passed. Legacy journal migration remains B143. No hardware run; manual acceptance
      remains open.
- [ ] B024: partial Core startup closure saved in `_plan/refactor-2.1/install-closure.md`. New
      matching defect, omitted task restore fix and unused EnableLua disposition are assigned to
      B180. All 29 unknown install ids are individually retired; five-project/U04B closure remains
      open.
- B024 source coverage now includes all 7 WSGM.Install and 12 WSGM.Launch C# bodies, plus the
  launcher's application-owned linked XML/quoting contracts. The other 54 project files remain.
- B024 now also covers all 10 logon-service bodies and their application-owned linked contracts: 29
  of 73 project files reviewed, 44 remain in PackagedLaunch/Setup. B182 owns two new error-path
  corrections and the omitted boot-manifest cap; no build/test/live action ran in that source pass.
- B024 packaged-launch progress: 11 of its 24 bodies read, bringing project coverage to 40 of 73.
  The journal/activation/selection pass found a last-claim loss race assigned to B183. Injection,
  native process supervision and callback interaction reviews remain; no package/Steam action ran.
- B024 supervision pass: 7 further bodies reviewed (47 of 73 overall, 18 of 24 packaged-launch).
  B184 owns inspection caps, unknown-creation containment and token-resize ownership corrections.
  Injection, foreground/callback/native declarations and Setup remain; no build/test/live action
  ran.
- B024 packaged-launch bodies are now read: overall coverage is 54 of 74, with 20 Setup bodies
  unread. Linked command remainder and callback/repeated-load cross-checks remain. B185 owns
  module/path truncation and late foreground retirement; no build/test/live action ran.
- B024 linked command and packaged callback/repeated-load source cross-checks are now reviewed.
  Existing command limits remain B107; B186 owns truthful load cache and owner-thread retirement.
  Current project coverage stays 56 of 76; Setup and individual U04B dispositions remain.
- [x] B025: the logon launch/stop lock prevents a queued launch after SCM reports Stopped. One
      ISessionHost seam covers token selection, dedup, cleanup and watchdog decisions; tests
      reference the actual service assembly through an alias. All 23 focused cases passed after
      Rider cleanup; the full Release solution build had zero warnings/errors. Prettier, guidance
      and diff checks passed. No live service operation ran; attended setup/logon acceptance remains
      open.
- [x] B026: no change, removed by the maintainer decision in DECISIONS.md.
- [x] B025 cap follow-up: B182 removes U04A-LFA-021's boot cap; padded valid JSON loads in both
      consumers.
- [x] B027: updater bodies use the shared read-stall timeout, updater caps are removed, and finally
      removes partial downloads while preserving an existing setup until SHA-256 verification
      succeeds. All 21 focused fake HTTP/temp-directory cases passed after formatting, including
      stalled setup/hash, cancellation, mismatch, progress and slow destination writes. The full
      Release solution build had zero warnings/errors; Rider cleanup, Prettier, guidance and diff
      checks passed. No real download or setup execution ran; manual acceptance remains open.
- [x] B028 source: non-fatal service registration, service stop before rollback, truthful
      partial/repair summaries, strict read-only answers export and false-to-true takeover
      transitions are applied. Unchanged collapsed gesture answers preserve individual switches. All
      64 focused setup/answer cases passed after formatting; the full Release solution build had
      zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. No live
      setup/service/Steam/hardware action ran.
- [ ] B028 acceptance: takeover/corrupt-config/summary manual checks and B030's isolated
      rollback-order and registration-plan fixtures remain. B068 must keep export migration
      read-only.
- [x] B028 cap follow-up: B181 removes U04A-C-001's answer/bundle caps; other B028 acceptance gaps
      remain.
- [x] B029: a potentially created de-elevation task gets one bounded cleanup attempt even after its
      dispatch deadline closes or the caller cancels. Cleanup failure preserves
      dispatch/cancellation; WSGM.Launch needs no source change. All 315 selected launch cases
      passed after formatting; the full Release solution build had zero warnings/errors. Rider
      cleanup, Prettier, guidance and diff checks passed. No live Task Scheduler action ran;
      attended acceptance remains open.
- [x] B180: exact Steam executable matching on all three startup surfaces, task restore without a
      state query and deletion of the unused EnableLua field/read are applied. All 40 autostart
      cases passed with fakes after formatting; the full Release solution build had zero
      warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. No live
      startup/task/UAC action ran; attended acceptance and B024's broader source review remain open.
- [x] B181: answer/bundle size caps and file-read pre-check removed; empty/malformed/null/schema
      refusal remains. All 35 selected cases passed after formatting, preserving large valid
      documents through parse and file APIs. The full Release solution build had zero
      warnings/errors; Rider cleanup, Prettier, guidance and diff checks passed. No live
      setup/config/installer action ran; B024's remaining review and manual acceptance stay open.
- B024 Setup support pass: nine further bodies reviewed, current coverage 67 of 78. B187 owns failed
  payload-open disposal; RTSS body-stall handling joins B030's download work. Eleven Setup bodies,
  registry/deletion/ledger cross-checks and individual U04B dispositions remain.
- [x] B187: failed payload opening releases its stream/archive; successful extraction retains
      ownership until normal disposal. All 22 selected payload/plugin-offer cases passed after
      formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier,
      guidance and diff checks passed. No live setup/network/registry/installer action ran; manual
      acceptance and B179 remain open.
- B024 transaction/shutdown/native pass: three further bodies reviewed, current coverage 70 of 78.
  B188 applies owned registry-root disposal; eight Setup bodies, engine recovery/ledger/deletion
  cross-checks and individual U04B dispositions remain.
- [x] B188: owned Setup registry roots close after their children on every scope exit; shared roots
      remain alive. All 13 selected registration cases passed after formatting; the full Release
      solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks
      passed. Native handle closure has source/compilation evidence only; no live
      registry/service/setup action ran.
- B030 implementation increment: explicit installation/machine/user paths, shared kept choices,
  exact USB/IP/package matching, payload containment, transaction/RTSS cap removal, account refusal
  and RTSS body-stall cancellation are applied. Existing engine fixtures now construct temporary
  roots; transaction registration and child commands use the recording runtime seam.
- B007 correction: missing-App uninstall extracts the bundled application once for shim/chord/shell/
  controller restoration. Failed restoration retains config.json as well as the controller ledger.
- Validation timing changed by maintainer instruction: tests and gates wait until the end. No test,
  build, cleanup gate or native action ran for this increment. B030 remains in progress until
  remaining closure work and the deferred fixtures/validation are complete.
- [ ] B030 to B178: remaining implementation, independent child validation and finding
      reconciliation.
- [x] B031 source: shared process names with unchanged values, native declaration consolidation and
      broker mapping-cap removal applied. Literal contract assertions are prepared; execution is
      deferred.
- [x] B032 source: cleanup diff comparison, live-path forms and PerfLab output corrected; asset
      formatting uses stdin, dead discovery and byte cap removed, process-output cap removed.
      Generated asset/hash remain untouched. Tests/builds/gates and generated-byte proof wait until
      the end.
- Pushes now wait until the end too: ci.yml runs verification on every push. The latest prior push
  (`e62013c9`, run 37181243266) is already terminal with failure; no active run remained to cancel.
  That failure is untriaged and belongs in final validation, not a claimed pass. Continue local
  commits.
- [x] B033 source: runtime/SQLite notices shipped and required, controller names read from the lock,
      runtime notice copying shared with Device Lab, unused Steam Input CLI staging removed.
- [x] B035 source: common/GPU examples extracted into compiled C# templates, generator and entry
      types aligned, docs and line-ending rules updated. Generated-project compilation remains
      deferred.
- [x] B036 source: one pinned asset helper preserves digest/signers and verifies partial files
      before publication; one export-table reader replaces bridge binary-string checks and supplies
      Steam Input ordinal checks. No downloads, native builds, tests, gates or publication ran.
- [ ] B037 full migration: instance ConfigStore and UserDataContext are applied; startup/App/shell/
      settings receive the owner explicitly, with boot/crash-loop/import/artwork roots passed
      through. Production callers and roots are now migrated through recovery, display,
      plugin/device, overlay/library/card and launch-wrapper paths. Log.Directory is removed;
      assets, updates, task XML, diagnostics, device state and media profiles take explicit roots.
      Test caller migration and end validation remain. Compilation is unproven; no
      build/test/gate/push ran.
- [x] B038 production source: metadata-driven enum repair replaces the hand list, validates defined
      flag bits and uses field templates; nullable unknowns remain unset. Recovery numbers remain
      and unknown recovery names become unsupported values, retained at Steam restore's use-time
      refusal. Section rules return diagnostics without logging; root callers report them.
      Clone/cap/layout/ spatial changes are applied. No build/test/gate/push ran. Compilation, SDK
      byte-equivalence, enum/forward-recovery fixtures and test caller migration remain in the end
      validation phase.
- [x] B039 production source: classified reads, one explicit writer scope, change-gated updates and
      durable config/boot publication applied. Distinct corrupt content is retained without pruning;
      normal boot projection needs a successful read and terminal recovery remains fail-open.
      Unreadable recovery reports failure, reload retains current state, and sidecars refuse
      overwrite after failed reads. No build/test/gate/push ran. Transactions, mutex/failure/sidecar
      fixtures, test caller migration, compilation and manual acceptance remain in end validation.
- Simplification after maintainer feedback: removed the empty PerformanceRules wrapper; the existing
  metadata pass already repairs its enum. Consolidated B039's evidence into this tracker. Continue
  direct implementation without additional generic helpers or auxiliary check loops.
- [x] B040 production source: the existing profile adapter honours the changed flag; reload reads
      fresh state behind the write gate. Shutdown cancels learning/fan-out without waiting before
      device safety and joins them only within the remaining deadline. Preview/tests share one
      in-memory store; profile snapshots and enum lists avoid repeated lookup/allocation. Affected
      test callers are migrated and the timing-dependent fan-out test uses completion signals. No
      build/test/gate/push ran; compilation, regression fixtures and manual acceptance remain
      deferred.
- [x] B041 production source: rotation runs outside the append lock with a zero-wait mutex; change
      suppression and append ordering stay together. Reload preserves the command-line verbosity
      override, and logging policy leaves the overlay. Removed the redundant threshold wrapper and
      startup catch. No new logging subsystem. No build/test/gate/push ran; logging regressions
      remain deferred.
- [x] B042 production source: restoring desktop state no longer clears its record. Startup, entry,
      restore-shell and panic clear explicitly after success, using a source-generated fingerprint
      that protects newer records. Desktop-audio selection is shared; audio applies query
      capabilities once. Kept the existing recovery routine with a small display delegate for
      isolated coverage, rather than introducing another service. No build/test/gate/push ran;
      recovery/audio regressions remain deferred.
- [x] B043–B044 production source: clock cancellation callbacks run asynchronously and failures are
      observed; keyboard/native motion callbacks and poll-thread failures are contained. Reconnect
      disposes completed sources before replacement, trace publication is volatile, diagnostics use
      DiagnosticText, and motion calibration tracing leaves the sample lock. SDK-038 remains an
      evidenced no-change: each COM report has a new pointer, so a vtable rewrite would add
      complexity for this nit. No build/test/gate/push ran; callback, clock and motion regression
      coverage remains deferred.
- [x] B045 production source: common manifests enforce the existing 256 KiB/depth-16 bounds and
      catch unsupported deserialization; package routing checks length before its JSON parse. Reused
      the existing reader/context and limits. No build/test/gate/push ran; boundary/parallel-read
      fixtures remain deferred.
- [x] B073 production source: a one-slot channel replaces the allocating sample semaphore; duplicate
      router states and backend neutral/health wrappers are removed. The manager owns
      capture/forwarding guards and backend disposal; invalid streams publish neutral once.
      Target-loss notification leaves the backend gate, managed gates are retained for late
      releases, haptic dispatch reuses its worker and drop counters are atomic. Shared wire scaling
      removes duplicate arithmetic without changing layouts. No build/test/gate/push ran; test API
      migration, behavioural/allocation checks and hardware acceptance remain deferred.
- [x] B075 production source: overlay-launched Settings receives the existing managed UI pad, using
      its current input service. Corrected the malformed saved-accent expression from the config
      migration. No build/test/gate/push ran; controller navigation acceptance remains deferred.
- [x] B082 production source: late results require the latest command id and descriptor generation;
      late faults are recorded. Runtime refuses cancellation before dispatch and uses active-time
      deadlines. Freshness time is supplied directly, one projection builder supports snapshots and
      direct lookups, and every router log runs outside its state lock. No build/test/gate/push ran;
      command-race, cancellation and freshness regressions remain deferred.
- [x] B074 production source: controller creation and per-user HidHide construction have one home;
      uninstall/panic use the same recovery factory and filename. USB/IP folder resolution is pure
      and skips stale uninstall paths before its default-folder fallback. The state gate uses Lock
      and capture state has its own file. Status was already an immutable record. Kept
      ReportTargetFault: its narrow call after physical recovery preserves the failure detail
      without new final-state plumbing. No build/test/gate/push ran; discovery/ownership regressions
      remain deferred.
- [x] B078 production source: removed the arbitrary layout byte cap; the newest readable chord file
      is selected before Steam's template-fit decision, so size cannot select an older file. The
      session now owns and detaches its controller-status subscription, with no extra binding
      service. No build/test/gate/push ran; Steam binding acceptance remains deferred.
- [ ] B080 in progress: Escape and hook failure report cancellation, retaining the hotkey; empty
      chord recordings retain the chord while explicit Clear still clears. KeyboardInput resolves
      the foreground layout once per chord, and GamepadButtons has its own file. Public-surface
      visibility cleanup remains open. No build/test/gate/push ran; recorder regressions remain
      deferred.
- [x] B083 production source: one stored device shutdown task closes command/task admission, waits
      within the existing deadline and attempts controller safety after an earlier timeout. Running
      or unverified work retains its owners; teardown failures log locally instead of a
      failure-tracker class. AutoTDP has one deadline-bounded stop task and never starts a duplicate
      restore. OEM dispatch and plugin-settings work are tracked, cancelled and included in
      shutdown; late runtime cleanup is observed and restore reconciliation stays inside its
      observed task. Diagnostics backs off only before a client connects, with change-based
      failure/recovery logs. No build/test/gate/push ran; shutdown timing, retained-owner,
      cancellation and diagnostics fixtures, test API migration and live acceptance remain open.
- [x] B081 production source: removed DevicePluginCompatibilityAdapter and the Device slot in
      PluginHost; the coordinator calls the runtime directly and resumes from its actual lifecycle
      state. Lifecycle callers wait only to their deadline while the worker retains the runtime gate
      until the plugin returns. Blocked disposal completes the host-facing exit and defers native
      cleanup; a fresh start waits for that retirement. An attempted stop is not replayed, and
      unknown stop statuses become reported failure reasons with completion set. DeviceWidgetSource
      already supplies the device health row, so it remains. No build/test/gate/push ran;
      deleted-adapter test migration, lifecycle/deadline fixtures and manual acceptance remain open.
- [x] B089 production source: device entries are the only active PL2 storage. Legacy BoostWatts is
      read solely for one-time migration on writable PL2 publication; an existing device value wins.
      Explicit boost edits save the entry and split-mode selection atomically. Split reconciliation
      reads device PL2, unified mode skips it, custom assignments remain authoritative, and
      Overlay/QAM Use global clears that device entry. Device/GPU user-write policy is shared, each
      restore candidate is resolved fresh by key, and pure value equality/profile cycling moved to
      Core. Kept the existing shared restore routine and coordinated lighting/fan ownership instead
      of adding a forwarding restorer service. No build/test/gate/push ran; migration,
      split/unified/reset, assignment, SDK/profile-wire fixtures, test API migration and manual
      acceptance remain open.
- [x] B094 production source: format helpers use the captured target; RTSS sampling is locked and
      renderer retirement retains live resources. Card passes wait, audio refresh and UI-owned
      disposal use the dispatcher, with MessageWindow last. Eject reconciliation runs in finally.
      Paths use typed volume letters, row setters notify their own names, drive-layout buffers grow
      on insufficient-buffer responses, and unused native declarations/duplicate GUID are removed.
      Application publish passed before the instruction to stop builds; warnings and test/manual
      acceptance remain open.
- [x] B095 production source: replaced three console runners with one result carrying start
      certainty, exit code and captured output. sc.exe/schtasks accept only confirmed success;
      unknown outcomes are retained, never retried. Existing process-owner seam remains. Format uses
      the same runner; its full unknown-erasure mapping remains B097. Manager startup is tracked,
      cancellation is checked between changes, and shutdown joins only within its remaining
      deadline. Strict unreadable restore already returns failure. Kept the explicit-store functions
      and existing injected system ports rather than adding a takeover wrapper. Test API
      migration/acceptance remain open. No build, publish, test or gate ran for the console-runner
      increment; the earlier staged app is now behind current source.
- [ ] B097 in progress: diskpart now carries its real outcome through formatting. An unknown clean
      result restores registration only if the old marker survived and retires the card record only
      if the marker is gone. Unknown format results stop without retry; every existing identity
      recheck remains. File writes now use a volume GUID verified against the captured disk; Steam
      registration keeps its letter path. Library removal awaits its async operation on a worker,
      format work is session-owned and cancellable, and the overlay uses the supplied manager.
      Destructive-call isolation and supporting regression fixtures remain open. No build, publish,
      test or gate ran.
- [x] B098 source applied: refresh pairing requires operating-point and captured-target operations;
      removed the test sentinel and legacy fallback branches. Updated existing test fakes and
      corrected the stale GDI description. Advertised rates use WDC DisplayEdid; deleted WSGM's
      duplicate parser and registry lookup, and moved the Claw equivalence fixtures into WDC.
      Validation remains deferred.
- [ ] B099 in progress: display-off mute captures and restores the same endpoint ID, without restore
      readbacks. Volume buttons now run in order on a worker and publish the OSD on the UI thread;
      mode changes discard queued old work. Shutdown disposes feedback and rejects late opens.
      Shared endpoint-access cleanup remains open. No build, publish, test or gate ran.
- [x] B100 source applied: removed RTSS entry/file/export-name caps, retaining region/file bounds;
      discovery uses limited process-image queries. LHM XML reads the full view within the read
      API's integer range. Frametime mapping remains read-only and independent of OSD writes.
      Existing startup probing warms the signature cache; revocation policy is unchanged. Validation
      remains deferred.
- [ ] B101 in progress: keep-awake and standby disposal cancel without disposing token sources still
      used by active operations. Download queries are supplied at composition. Card monitoring now
      waits for readiness/Steam-start events instead of rescanning every three seconds, including
      desktop mode. Corrected format-stage and library-watcher comments. SystemStatus requires
      supplied managers; Settings owns preview managers and the UI fixture supplies unstarted
      instances. Standby operations are supplied as delegates, and unused public static helpers are
      internal. Pure display-signal rules remain shared without a new wrapper. Owner-level
      regression coverage remains open. No build, publish, test or gate ran.
- [x] B102 source applied: SteamGridDB's implementation and provider share one partial class;
      removed query-less forwarding APIs. Each provider owns its HTTP client and request gate and
      accepts supplied handlers/gates; Screenscraper media uses that client too. Browser contexts
      share their supplied provider list, including official-artwork lookup. Provider pages carry
      HasMore from the raw SGDB response, before filtering; Screenscraper has no pages beyond zero.
      Expired failures are pruned on the next failure. One slot table supplies IDs, labels and
      display order; persisted enum values are unchanged. Unused artwork implementation types are
      internal. Existing test API references were migrated; provider/browser regression cases and
      validation remain deferred.
- [x] B103 source applied: unchanged artwork/provider settings keep the current browser page and
      caches. Credential changes reset caches once at the parent; tab-layout changes do not reset
      providers. Browser and library reuse the same provider signature. Reload-triggered opens run
      on a worker; disposal cancels loads and clears page state without disposing sources still used
      by active work. Request-token links were removed from tab loads. Apply/find/clear share
      shortcut-icon filenames; ICO files are discoverable, and accepted reset deletes only matching
      files in the captured account. Store-icon cache behavior stays unchanged pending attended
      evidence. Managed previews and filter reads run outside page locks; overlay opens run on
      workers. Preview MIME comes from image bytes, and reads use the shared size bound. Load tokens
      are captured before replacement can dispose their source. Cancelling browsing notifies
      subscribers. Regression fixtures/validation remain deferred. No build, publish, test or gate
      ran.
- [x] B104 source applied: removed unused ImportPlan.Matches and GameLibrarySteamTarget. Deleted the
      test-only throwing CommandShortcut.Compose wrapper; existing fixtures use TryCompose through a
      test helper, and the refusal case asserts the nonthrowing result. Both artwork-options APIs
      share one projection body. No new production class was added. Validation remains deferred.
- [x] B105 source applied: overlay service commands and confirmation commits start their backends on
      workers, preserving UI continuations and existing library edit/save ordering. Import records
      are copied on read/save. Library admission closes at shutdown entry; its running task and
      collection writer drain within the caller's deadline, without blocking Dispose or disposing
      live token sources. The service owns copied library settings, adopts successful commits and
      receives reload snapshots; the worker no longer writes live AppConfig. Existing test
      construction was migrated without running tests. Revision updates remain under the library
      lock; subscribers run after release. Artwork reset captures requests under the lock and
      publishes outside it. Save failures still notify local changes. Rematch's nested event is
      covered by its edit's notification after release. Existing delegates remain; no forwarding
      service framework was added. Regression coverage and validation remain deferred. No build,
      publish, test or gate ran.
- [x] B106 source applied: each planned adoption immediately claims its AppId. A matching shortcut
      claimed by another title offers an unticked Add for a separate entry. Apply revalidation
      excludes other titles' current record claims, including shortcuts created earlier in the same
      run, so an Add cannot rewrite another title's shortcut. The existing records dictionary
      supplies ownership; no separate tracker was added. Regression tests and validation remain
      deferred.
- [x] B107 source applied: missing launcher byte files remain absent; read failures and
      accepted-bound refusals now throw with the filename so scanning preserves that source's
      imports. Store lookup reuses one HTTP client and contains non-cancellation fetch failures.
      Running-app construction is inert, composition starts it, disposal is atomic and foreground
      projection snapshots profile state under its lock. Removed the executable-name, AUMID and XML
      character caps. Shortcut composition checks the full quoted target and arguments against
      Windows' 32,767-character limit, including NUL; parsing adds no artificial argument bound.
      Helper logging lives in Steam, and sibling paths share Installer's resolver. Catalog suffix
      filtering is unchanged pending attended response evidence; its stale byte-format comment is
      corrected. Existing argument/manifest fixtures were migrated. Build, tests, deployment and
      attended acceptance remain deferred.
- [x] B108 source applied: previews disable idle-timeout editors and refuse writes, including
      overlay-test; removed the direct Windows-write fallback. Glyph sampling uses a packed input
      key instead of allocating a set per sample; existing map fixtures exercise Key/Lights. Empty
      animation browsing fetches on tab entry rather than on every render. Logo filters offer the
      four logo styles. Detail-less plugin Dispatched/Rejected outcomes show Applied/Not applied
      while preserving supplied detail. Native library picking suspends navigation and refuses work
      after the sheet closes. Profile reset, audio-format and UAC/lock async handlers contain
      failures without retrying. Allocation/UI/lifetime regression checks, build, deployment and
      manual acceptance remain deferred.
- [x] B109 source applied: removed the process-global keyboard service and shared navigation
      session. Each controller supplies its session state to its windows; text entry opens the
      requesting window's real keyboard surface. Plugin text editors receive that owner's request
      callback. No second surface host/interface was introduced; existing window methods and local
      top-level lookup provide ownership. Stateless StatusPanel/FluentExtensions helpers remain
      because they carry no mutable global state. UI fixtures now supply an isolated store and
      explicit sessions. Added a two-window text-entry case and migrated the plugin editor fixture
      without running them. Validation remains deferred.
- [x] B110 source applied: Back decisions use one context in the existing navigation model,
      including surface, nested-view and rail focus state; controller, keyboard and header share the
      window action. Device/GPU visibility updates happen at attachment and source-change handling
      rather than inside render methods, removing the render cycle and its two guards. Removed the
      eight-page cap and unwind bound; each pop reduces depth. Typed section keys preserve both
      existing string spellings, and workspace refresh reacts only to its named visibility
      properties. Index placeholders remain until the B127 reconciler change. Updated depth/Back
      fixtures and added context/key regression cases without running them. Build, visual/manual
      acceptance and validation remain deferred.
- [x] B111 source applied: immutable StartupOptions replace public process-mode flags and are passed
      through the App factory; the shell receives their boot/residency/verbosity values. Program
      keeps only its private panic snapshot and its existing one-shot/crash-loop code. Update
      pre-stop runs on the watcher callback, only in shell mode, with lifetime exit posted afterward
      even after failure. Restore-shell waits on the shell mutex rather than unrelated Settings
      processes. Shell cleanup exposes one cached task; deleted the unused DisposeAsync budget path.
      Runtime exit publication is serialized, startup failure and OS-end flags remain sticky, and
      SessionEnd wins at cleanup entry. The existing direct log-verbosity setter needed no further
      recovery mechanism. Mode/log fixtures were migrated without running them. Compilation, tests,
      deployment and live exit proof are deferred.
- [ ] B112 in progress: Explorer and desktop-app identity use limited process-image queries.
      Retired-shell state and exit polling belong to ExplorerDesktopHost; removed the static exit
      lock and sleep polling, retaining cancelled observations for the next explicit return. Native
      exit waits observe cancellation. Failed anchor cleanup releases every later resource after a
      disposal error. Deleted unused synchronous disposal wrappers and the diagnostic module cap.
      Terminal and session recovery share ExplorerLauncher: direct when unelevated, scheduler first
      when elevated, direct only after NotDispatched, and no competing start after Unknown or
      cancellation. Steam keeps scheduler uncertainty rather than treating it as refusal. Elevated
      Explorer refusal is documented. Added launcher policy/cancellation fixtures without running
      them. Native exit-loop seams/behaviour coverage and notebook/Claw recovery acceptance remain
      open. No build, deployment, test or gate ran.
- [x] B113 source applied: Settings previews explicitly omit hotkey/chord/touch activation and stop
      navigation polling after close even when the configured chord is enabled. Session and
      overlay-test compositions retain all reopen triggers. Activation subscriptions detach before
      their owners are disposed; the hotkey service already detached its named message-window
      handler. Previews receive the same managed pad supplied to Settings. Kept the existing
      controller/gesture code with an explicit composition flag rather than adding an activation
      service layer. Regression/manual acceptance and validation remain deferred. No build,
      deployment, test or gate ran.
- Build/deployment checkpoint under updated instructions: Release application compilation and full
  win-x64 publish succeeded after fixing eight earlier migration/reference/cast errors. Compilation
  reported 66 warnings, chiefly documentation/style, not a clean gate. App, WSGM.Launch and
  WSGM.PackagedLaunch are staged in `publish/refactor-app`. Deployment to
  `C:\Program Files\WSGM\App` is blocked by running WSGM PID 11180; nothing was replaced, launched
  or closed. Tests, Rider cleanup, gates, live acceptance and pushes remain deferred. Raw
  build/publish logs are in the session temp folder.
- [x] B186: first load outcomes remain truthful without retry; process latch overrides cached
      success. Shutdown callbacks request guarded cancellation; the launch scope alone retires its
      exemption. All 117 packaged-launch cases passed after formatting; the full Release solution
      build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. No
      live console/COM, package, injection or Steam action ran; native acceptance and B179 remain
      open.
- B024 inventory adjustment: B186 adds two reviewed production helpers. Current coverage is 58 of 78
  project bodies; the 20 Setup bodies and individual U04B dispositions remain open.
- [x] B185: complete API-sized module/path reads and bounded foreground retirement applied. All 109
      packaged-launch cases passed after formatting through isolated sizing/lifecycle helpers; the
      full Release solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and
      diff checks passed. No live enumeration/window/hook/injection/Steam action ran; native
      acceptance stays open.
- B024 inventory adjustment: B185 adds two reviewed/validated production helpers. Current source
  coverage is 56 of 76 bodies; the same 20 original Setup bodies and linked/callback cross-checks
  remain.
- [x] B184: unlisted inspection caps removed, buffer resize ownership made failure-safe, and
      containment requires known matching creation time. Small public buffer/identity helpers use
      isolated fake operations; unused contained count is removed. All 105 packaged-launch cases
      passed after formatting; the full Release solution build had zero warnings/errors. Rider
      cleanup, Prettier, guidance and diff checks passed. No live process
      query/containment/injection ran.
- B024 inventory adjustment: B184 adds one production helper file, reviewed/validated with its
  source increment. Current coverage is 48 of 74 C# bodies; 26 original bodies remain unread.
- [x] B182: failed service opens distinguish absence from errors, successful WTS responses release
      their buffers on size/decode refusal, and the omitted boot cap is removed. All 49 selected
      cases passed after formatting via helpers/test buffers/temp files; the full Release solution
      build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. No
      live SCM/WTS action ran; native acceptance and B024's remaining review stay open.
- [x] B183: both failed-exemption branches retain recovery intent. All 26 selected journal/route
      cases passed after formatting with temporary journal/scripted-liveness fixtures; the full
      Release solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff
      checks passed. Caller branches have source/compilation evidence; no live COM/package/Steam
      action ran. B024 and attended package/overlay/input acceptance remain open.
- [ ] B179: full automated gate on the committed head.
- [ ] Maintainer manual acceptance matrix on the notebook, IR desktop, MSI Claw 8 A2VM and Xbox Ally
      X.

# GPU plugins, Display and Audio

Work continues on `master`. Vendor code belongs to its GPU package; Windows display and audio
mechanisms belong to `windows-device-control`. Hardware validation is separate from blind
implementation.

Current scope: complete the AMD and NVIDIA packages under
[the vendor implementation checklist](gpu-vendor-implementation.md). No test execution or live
driver changes were requested. Unchecked acceptance items remain outstanding; compilation does not
count as a hardware pass.

## Overlay Tools and CEF parity

Full specification: [Overlay Tools parity plan](overlay-tools-cef-parity.md). Feature code is
implemented; live acceptance is pending.

- [x] Audit the four CEF surfaces and their Overlay entry points, services and partial views.
- [x] Enumerate all CEF commands, settings, previews and import workflows in the parity plan.
- [x] Audit other Overlay routes and shared navigation/editor behavior for defects of the same
      caliber.
- [x] Restore all four Tools routes with shared entry/leave lifecycle and native Artwork attachment.
- [x] Correct nested Back precedence, live route visibility, no-op command states, editor focus,
      false empty-library results and missing theme/movie deletion confirmations.
- [x] Add the shared controller file/folder picker, image cache, media viewport and scoped
      view-session ownership.
- [x] Complete CSS Loader Browse, Installed, Profiles and Settings, including screenshots and exact
      patch editors.
- [x] Complete Video Switcher Browse, Library and Settings, including playback and local WebM
      import.
- [x] Complete Artwork game selection, slots, filters, details, local/official/invisible artwork and
      logo editing.
- [x] Complete importer sources/folders, filtered review, launch choices, staged per-title/bulk
      artwork and apply results.
- [x] Reconcile docs/guidance/assets, compile without warnings and review full populated Overlay
      previews.
- [ ] Maintainer manual acceptance for Overlay and CEF parity, then focused tests, baselines and the
      broad implementation gate.

## Overlay and QAM regression fixes, 2026-10-01

- [x] Render GPU controls directly under Device > GPU and remove the Graphics destination.
- [x] Use the Processor cores native Expander style for folding sections.
- [x] Publish GPU categories in QAM Performance through the shared settings renderer.
- [x] Restore Claw chord suppression from before `24368aef` and compare `BlockWinG.zip`.
- [x] Separate Channels and Format in Overlay and QAM, place QAM choices under Audio, and label
      Spatial Off.
- [x] Resume display-mode reads after regrouping the Overlay Display controls.
- [x] Move Format SD Card from Card Manager to Tools > Storage.
- [x] Remove device/GPU duplicates from Tools > Plugins and hide an empty Plugins entry.
- [x] Compile the Release solution with no warnings, format changed sources, check asset
      drift/guidance, and review 1280 × 800 and 980 × 640 renders.
- [ ] Maintainer manual pass on Claw and Steam QAM.
- [ ] Run deferred focused tests, review/update affected UI baselines, then run the initial
      implementation gate.

## Original issues 201 and 202

- [x] Implement the reusable folding Overlay section and documented session expansion state.
- [x] Implement sound-pack management, preview, reversible Steam overrides and restore defaults.
- [x] Merge these implementations into the Intel branch.
- [ ] Verify issue 201 in the running Overlay: controller, mouse/touch, dynamic sections and focus
      restoration.
- [ ] Verify issue 202 on Windows Steam Stable/Beta: pack switching, preview, restore,
      restart/reload and failure recovery.
- [ ] Run the deferred focused tests and gate after the maintainer's manual pass.
- [ ] Reconcile remaining acceptance gaps before considering either issue finished.

## Integration

- [x] Merge sound packs and folding Overlay groups into the Intel branch.
- [x] Merge and publish the toolkit child before the parent gitlink.
- [x] Keep one typed Graphics capability/profile backend, reachable from Device > GPU.
- [x] Compile the merged solution without warnings.

## Intel, issue 178

- [x] Preserve the standalone Intel plugin and typed GPU capability/profile surfaces during the
      merge.
- [x] Keep Device > GPU connected to the shared Graphics controls.
- [ ] Audit the final extraction against issues 32, 33 and 37, including configuration migration and
      retained Claw controls.
- [ ] Verify plugin loading, Device Integration off, absence of duplicate controls, sleep/resume and
      driver reconnect.
- [ ] Record which Intel hardware scenarios were actually tested and which remain unavailable.

## NVIDIA, issue 177

- [x] Inspect official NVAPI headers and Driver Settings documentation.
- [x] Clone ColorControl and identify its NVIDIA display-color implementation.
- [x] Finish serialized driver lifecycle, discovery, generations and shutdown.
- [x] Bind documented NVAPI DRS structures, functions and supported setting values.
- [x] Use NoVidiaApp for curated driver controls, including G-SYNC policy, synchronization, power,
      cache and supported DLSS/RTX settings.
- [x] Implement native per-game profiles, owned-setting journals and inherit/reset without erasing
      unrelated settings.
- [x] Use ColorControl for connected-output color, BPC, dithering and HDR10+ behavior; revalidate
      physical monitor identity and complete color combinations.
- [x] Account for the RTX 5080 and the laptop's Optimus RTX 4070 without assuming NVIDIA drives the
      panel.
- [x] Add package/build/setup integration, provenance and focused regression sources.
- [x] Compile the Release solution without warnings. Test execution and hardware acceptance remain
      deferred.

## AMD, issue 179, blind implementation explicitly requested

- [x] Locate HC 1.3.1.6's AMDGPU and ADLX backend.
- [x] Inspect official ADLX/ADL contracts and ColorControl's AMD display path.
- [x] Bind ADLX interfaces from their documented C vtables, with native contract regression sources.
- [x] Discover adapters and their connected displays; never use HC's fixed display index as
      identity.
- [x] Publish supported FreeSync, scaling, color, dithering and Radeon 3D controls, including
      RSR/AFMF and supported FidelityFX upgrades from the HC/SDK reference.
- [x] Model driver-global, display-wide and host-switched game settings according to actual API
      scope.
- [x] Investigate native Radeon profile APIs; do not invent native per-application support.
- [x] Implement loss/reconnect, sleep/resume, truthful write outcomes and readback.
- [x] Add package/build/setup integration, focused regression sources and explicit blind provenance.
- [x] Compile the Release solution without warnings. AMD hardware acceptance remains unverified.

## Windows Display

- [ ] Add a dedicated Display section under Device, available with Device Integration off.
- [ ] Reuse the current Windows brightness/display owners and retain their pin identities.
- [ ] Keep HDR capability, get/set and topology handling in `windows-device-control`.
- [ ] Expose HDR from the Windows Display surface, separate from vendor color controls.
- [ ] Account for every control moved out of Tools > Display and preserve its navigation path.

## Overlay Audio

- [ ] Verify and complete the existing spatial-audio controls and service attachment.
- [ ] Add a dedicated Channels selector, separate from Format.
- [ ] Restrict Format to supported sample-rate/bit-depth combinations for the chosen channels.
- [ ] Preserve endpoint identity, channel mask and other fields when either selector changes.
- [ ] Keep failed/uncertain writes visible, with no automatic retry.
- [ ] Add focused coverage for independent selection, endpoint changes and unsupported combinations.

## Delivery and validation

- [ ] Update mechanism docs, package provenance and this checklist as work lands.
- [ ] Regenerate Steam assets after toolkit/source changes and check drift.
- [ ] Run formatting, guidance checks and warning-free Release compilation.
- [x] Refresh and review the 11 affected headless Overlay baselines for the Intel-branch PR.
- [ ] Commit and push to the Intel branch, publishing changed children first.
- [ ] After the maintainer's manual pass, run focused tests, refresh affected UI baselines and run
      the required gate.

No new release version, tag or GitHub release is requested. Deployment and live driver actions still
require explicit direction. The PR request invokes the repository's required automated verification;
manual hardware and live Steam acceptance remain pending.

## Overlay and QAM layout follow-up, 2026-10-01

- [x] Stack controller, device and performance folds vertically with consistent native styling.
- [x] Center collapsible title blocks vertically and give summary headers more breathing room.
- [x] Replace section pin labels with centered tack icon buttons and a visible pinned state.
- [x] Compact Overlay Audio into labeled rows without separate cards for Channels, Format or
      Spatial.
- [x] Group QAM GPU controls into one vendor-named fold with plain inner sections.
- [x] Preserve individual Overlay GPU folds, stacked vertically at full width.
- [x] Keep QAM Reset to Default last, after dynamic GPU/plugin sections, and record this in the CEF
      skill.
- [x] Remove the left-menu Graphics entry and place WSGM immediately above the final Power entry.
- [x] Check GPU setters through startup native-state round trips before publishing Overlay/QAM
      controls, including FBC.
- [x] Apply support discovery to Intel, AMD and NVIDIA; keep layouts stable after later failed
      writes.
- [x] Complete formatting, asset checks and warning-free compilation.
- [x] Commit and push the toolkit before the WSGM gitlink.
- [x] Rebuild the setup and copy it to Z:.
- [ ] Maintainer manual pass, then focused tests and affected UI baselines.

Handoff: `Z:\WSGM-Setup-2.1.0.exe`, file version `2.1.0.1540`, built from `085987ee`. The copied
setup matches the build output by SHA-256. Release compilation had zero warnings and errors;
isolated Overlay previews were reviewed. Application tests, the full gate and UI baseline refresh
remain deferred until the maintainer reports a manual pass.
