# WSGM 2.1.0 internal refactor, plan v2

This is the plan that will be executed. It replaces `refactor-plan.md` (the Codex plan) and its two correction annexes for implementation purposes, and it folds in the 19 domain reviews, their adversarial verification files, the maintainer-reported USER-001 and the completeness critic, all under `_plan/refactor-2.1/review/`. Requirements stay those in `requirements.md`; the manual matrix stays the maintainer's, with the changes listed in section 5.

Baseline: parent `master` at `1329813f`, WindowsDeviceControl `main` at `2f07485` with the W02_01 edit uncommitted in the child working tree, SteamUiToolkit `main` at `388dd1b`. The parent snapshot reads "clean" only because the gitlink itself is unchanged.

Batch ids below (`B001`...) are the execution order. A batch's spec is written to be executable on its own; where it says "with corrections", the cited `_plan/refactor-2.1/review/<domain>.verify.md` section is part of the spec and wins over the original report. Many line numbers in the reports are wrong (each verify file says so); implementers anchor by symbol, never by the cited line.

## 1. What changed versus the Codex plan

### Corrected facts

- **No programmatic exit runs the session cleanup today** (SESSION-V-001). Tray "Exit WSGM", `--restore-shell`, update and uninstall all call Avalonia's forced `Shutdown()`, which skips `ShutdownRequested`, so AutoTDP restore, controller release, the HidHide cloak-off, plugin stop and Explorer recovery never run. The Codex B3 shutdown design reordered code that is unreachable. `wsgm.log` confirms it (no update handoff line, setup always waits out its grace). Fixed first, in B006.
- **WSGM blocks sign-out and shutdown** (SESSION-007, corrected to high). Avalonia raises `ShutdownRequested` from `WM_QUERYENDSESSION`, WSGM cancels it, then cleans up as Normal and restores Explorer during session end.
- **A startup failure already exits 1** (SESSION-002 refuted as stated), but it becomes wrong the moment the exit path is fixed, so the sticky exit code lands in the same batch.
- **Live device defects the plan missed**: an Unverified device stop permanently blocks every device restart (DEVICE-001, routine on the Ally family); AutoTDP stops working after any lock, sleep or restart and its exit restore is skipped (DEVICE-V-001); a failed unlock resume leaves the device quiesced (DEVICE-V-002); a refused synthetic press latches Guide or a paddle on the virtual pad (INPUT-V-001); the HidHide show gates cloak-off on a successful read (INPUT-V-003).
- **The RTSS overlay only appears after the QAM was opened** (USER-001). The poll that starts RTSS and repairs drift waits for an observer, and the overlay level is deferred when the executable is unknown. The maintainer decided RTSS starts with WSGM and is kept alive.
- **A Settings save reverts Steam-side choices** (SETTINGS-001), including `Animations.SteamSetAside`, which is recovery state.
- **Install defects**: uninstall with `App\WSGM.exe` missing deletes the only HidHide ledger (INSTALL-V-003), and a logon queued at service stop can still launch WSGM (INSTALL-007). The review's install security findings (INSTALL-001, -002, -003, -008, -010 and INSTALL-V-001) are confirmed in code but dropped by maintainer decision as security theater (section 4). `install.md` is truncated; a read-only closure batch (B024) recovers the unwritten bodies.
- **Assumptions the Codex plan built on that do not hold**: the toolkit picker has no paging or "More" control and renders the whole list; published QAM section ids are today's strings, so folds need no migration; the Steam UI host is built once per session, not per generation; toolkit types reachable from `ISteamUiModule` are a plugin ABI governed only by `PluginApi.Version`; WDC display records (five of them, including `DisplayMode`) are WSGM's config and recovery wire format; the layout editor never stores rotation, so a rotation fix alone would rotate displays (WDC-V-001); Settings already writes refreshed display identities back after a path match; the A02_02 latch and the SDK-017 `RestoredUnverified` reinterpretation would lose controls or recovery originals.
- **Admitted Codex batches**: W02_01 is done but uncommitted in the child. Of the other six, verification keeps A02_03 (with R4) and A02_04 unchanged, trims A02_02, replaces A02_01 and W02_02 with smaller versions, and does not land T01_01 because the code it edits is deleted by the client redesign.

### Over-engineering dropped, with the simpler replacement

| Codex mechanism | Replacement |
| --- | --- |
| B3 percentage cutoffs (30/50/70/90 %), 500 ms preliminary drain, recomputation on escalation, `SessionLifetime` five-state machine and owner registration | A fixed, safety-first ordered step list under one deadline; each owner exposes a synchronous `CloseAdmission()` and `StopAsync(Deadline)`; the session joins only the power queue, startup task, transition and gate loop (D1) |
| Owned SDK clock service passed through every context, clock-bearing `Deadline`, `DeadlineCancellation`, instance `PluginDiagnostics`, `PluginTraceSink` | Keep the process `ActiveClock` and `PluginTrace`; dispatch due cancellations with `CancelAsync` and observe faults; one `IPluginHost` trace channel for common plugins |
| Picker spool, 200-entry pages, four-worker cap, per-root busy, `ProviderBusy`/`ProviderCapacity`, injected clock, `FilePickerPathPolicy` type | Worker enumeration with caller cancellation, stale-ticket rejection, extended-path normalization and device-namespace refusal; whole list rendered as today; the bridge request timeout is the only bound |
| Bridge host id and per-bootstrap nonce | Asset hash, schema and generations as today; no trust-boundary note, since the maintainer dropped all same-user hardening (a restarted WSGM would otherwise be refused as foreign) |
| Patch `DependsOn` graph, quarantine reset API, generation-scoped host | The manager applies its bridge first and removes it last on every path; quarantine lives for the module instance; plugin Steam UI modules register when the plugin becomes ready through one module-set replace (maintainer decision) |
| Six dispatch outcomes | Four write outcomes: `NotSent`, `Unknown`, `Rejected`, `Applied` |
| WDC per-instance services with injectable adapters, subscriber hub, `DisplayService` per host, persistence parameter, EDID "upgrade" migration, exact registry-kind capture, active-clock pairing deadline, moving `BluetoothDeviceCatalog` | Static facades returning disposable registrations, internal ports only where orchestration needs tests, one internal display write gate, fixed `SDC_SAVE_TO_DATABASE`, refuse non-DWORD at capture, wall-clock 90 s pairing, move only container normalization |
| Config `.bak` plus repair path, store worker, injected file backend, normalizers and diagnostics, raw recovery subtrees, extension data everywhere, crash-point injection, `UserDataContext` user identity | Durable flush, atomic replace and one quarantine copy per content; synchronous API; `ConfigStore(UserDataContext(Root, MutexName))`; document-level read outcomes; state fixtures |
| `SetupTransactionRunner` plus four ports, `TargetUser` plumbing, update edit mask, persistent owned-task cleanup record and `Unknown` state, Task Scheduler COM adapter, logon dispatch owner | Existing `SetupEngine.Run` with narrow seams, an identity refusal in `Detect`, `ApplyTo` diffing against the exported answers, one extra `/Delete`, schtasks with the XML where it is written today, one stop flag |
| Router split into catalog and lane, per-publisher `CapabilityCommandPolicy`, AutoTDP runner and writer split, per-owner phase schedulers | One router; helpers added to the existing `CapabilityUserWrites`; `PowerLimitOwner` owns the power lane and AutoTDP |
| A02_02 save-failure latch; A02_01 CLI growth detection; W02_02 two interfaces and process wrapper | `FileNotFound` means absent and undefined statuses are refused; the reader's byte bound; one `IPowerActionApi` port |
| Lab 64-request queue with Busy, per-session locks, stale-frame filter, owner-thread mutex, export staging file, record fold migration | `Monitor.TryEnter` in the watchdog; work bounded by call deadlines; in-memory export without caps; records read where they are |
| Storage revision cache, `ISteamInputLease` port, Quick Access pins owner, `PowerPolicyLane`, reference-counted MessageWindow claims, `TransientDisplayMode` | Snapshot on the UI thread; the concrete lease instance; a write chain; the lock inside `PowerSchemes`; one owned MessageWindow; WINSVC-012 only |
| GPU written-value overlay for every vendor, rediscovery only on topology change, IR plugin lifetime CTS | Overlay stays in the Intel adapter; per-session caches and one DRS load per pass; the host already cancels actions on stop |
| Library five-owner split, `LibrarySelectionStore`, `LibraryArtworkCoordinator`, `LibraryDisk`; toolkit-wide Prettier pass; Settings classification test; Lab typed evidence | One state owner with three workers; existing stores; no seam churn; no pass; a round-trip test; the twenty-line review fix |

### Added scope

- USER-001 (B005) and one never-strand group: exit cleanup (B006), controller release and HidHide (B009), setup uninstall (B007), and the stale-ledger show at start when no cycle runs.
- The install batches (B024 to B031) built from `install.verify.md`, reduced to their functional parts after the maintainer dropped security hardening (B026 removed).
- Verify-found defects in every domain (the `-V-` ids in Appendix A), the critic's unowned files (`SoundPack*`, `SteamAutostart*`, `NativeQamHybridCoreService`, `DesktopAppProcessBackend`, `DeviceProfileValidation`, `ThemeBrowseSession`) and the critic's cross-domain conflict resolutions, adopted throughout.
- No-arbitrary-limits removals the plan did not list: sound-pack skips and budgets, device-config and profile-name caps, the 64-point curve cap, glyph and package count caps, toolkit content caps, WDC caps, the 768 KiB asset cap, Lab evidence caps, LHM and drive-layout buffers. Remaining byte bounds on untrusted input are listed for D2.
- A frozen-foundation diff check and the toolkit JavaScript API inventory (critic 2.4, 2.6); per-batch independent child validation (requirement 16) under the CLAUDE.md publication rule (child commit and push, then parent gitlink with consumers, per batch), replacing the I01/I02 phases.

### Where the admitted Codex batches went

| Codex batch | Here |
| --- | --- |
| W02_01 (done, uncommitted) | B001: normalize line endings, commit and push the child, record the gitlink |
| W02_02 | B002: the single-port version from `wdc.md` section 3.2 |
| T01_01 | Not landed; its tests move into B053, which deletes the code it edited |
| A02_01 | Replaced by B045 (steps 1, 2, 4 plus the package manifest length check) |
| A02_02 | Steps 1 and 4 only, in B010, after B008; A02-F008 is no-change |
| A02_03 | B004 with R4 |
| A02_04 | B003 unchanged |

## 2. Target architecture

The shape is the Codex plan's, minus the mechanism listed above. Process and project boundaries stay. There is no DI container, service locator, generic resource coordinator or new runtime assembly. Stateful services are instances built at the composition root and passed by constructor; pure rules and parsers stay static; ports exist only where an owner's orchestration needs a test without live Steam, hardware or the user's configuration. Every owner refuses new work after a synchronous `CloseAdmission()` and stops within the deadline it is given, retaining anything still running instead of freeing it underneath. Writes publish the written value; readback only upgrades verification and never gates a write, a control or success. Outside the Claw there is no readback machinery at all (D9): a write either dispatched (published as written) or failed to dispatch, and no entry waits on a readback. An uncertain write is never retried automatically. Device logic mirrors HC 1.3.1.6.

**Process and session.** `Program` parses immutable `StartupOptions` once and routes one-shots (`StartupCommands`) before any normal composition; the early restore-shell path needs no config, logging, Avalonia or GPU. `ApplicationRuntime` owns the exit sequence: the shutdown reason with priority and a sticky SessionEnd flag, one shutdown task, a deadline that escalation only tightens, the exit code and the single installer handoff. It runs the session cleanup and only then the forced lifetime exit; `ShutdownRequested` is just the OS end-session input, mapped to SessionEnd. `ShellSession` is the composition root with per-owner start isolation. `SessionTransitions` owns boot takeover, launch sequence, desktop resume, splash and desktop actions over one `IGameModeEntryBackend`; `SessionPowerQueue` and `SessionConfigReloader` are small owners (last good config kept, ordered steps isolated). Explorer is probed by image path, exited only with `0x5B4` and `WM_CLOSE`, and started by one `ExplorerLauncher` with a tri-state dispatch result. One owned `MessageWindow` is disposed last. Shutdown is the ordered list in B140.

**Configuration.** `config.json` stays the one serialized authority for preferences and registry recovery state. `ConfigStore` is an instance over `UserDataContext(Root, ConfigMutexName)` with `Read()` returning Loaded, Absent, Corrupt or Unreadable (a file from a newer WSGM loads best effort, with no read-only mode), `Update(Func<AppConfig,bool>)` and one writer `Transaction`; the reader mutex stays and the nesting machinery goes. Strict writes refuse after any outcome but Loaded or Absent; fail-open recovery paths (shell unregistration, boot disarm) keep their fallback. A tolerant enum converter and a metadata walker replace the hand repair list; normalizers live beside their sections and return diagnostics; load-time length caps go. `SchemaVersion` 1 with an in-memory migration persisted on the next strict write and one `config.v0.json` copy; a newer `SchemaVersion` loads best effort as today. `ProfileService` stays the sparse profile authority with `ReloadAsync` and `Close`. Sidecars keep their own files and refuse to overwrite what they could not read.

**Device and power.** `DeviceCoordinator` becomes a facade over `DeviceCycle` (package discovery and runtime lifecycle), `PowerLimitOwner` (the only sustained, boost and preset lane, owning AutoTDP), `DeviceDesiredStateRestorer` and `DeviceControllerHandoff`. The runtime is driven directly (no compatibility adapter, no Device slot in `PluginHost`); an unverified stop never blocks a restart; a passive detection keeps no runtime. The router keeps descriptor-set and generation semantics, rejects only before dispatch, and lets only the latest command reconcile. AutoTDP's restore obligation is the original watts, restored first at shutdown.

**Controller and input.** `ControllerManager` (built at the root) owns target, source, capture, forwarding block, synthetic buttons and an ordered release whose HidHide show always runs, unless a fault restart deliberately keeps the pad hidden. Cloak-off is written first and unconditionally. The sample path is a bounded(1) channel with no per-sample allocation or logging. The Steam Input shim and lease are instances; Program owns the lease so Panic still reaches it. Touch edge input has one raw-input owner, a pure recognizer and subscribers.

**Device SDK and packages.** The SDK keeps its process clock and trace sink, with safe cancellation dispatch and guarded native callbacks. Device API 12 removes glyph caps, moves host-only types out, adds `ManifestRules` and identity normalization, and drops journal disposal; each service decides its own reconciliation block. Plugin API 4 drops `PluginText`, makes the manifest init-only, gives common plugins a host trace channel, and covers the toolkit module closure. Packages report truthfully: a transport timeout is not caller cancellation, pre-write failures are Rejected, the Claw watchdog disarms after an uncertain write, Ally restores write through in HC's order (SPL, then SPPT and FPPT) and publish the written value. One recovery policy: no automatic replay of an uncertain restore; on the Claw, the one vendor that can read back, unresolved entries are kept and an explicit command re-arms, its controller entry exempt; the Ally has no unresolved entry and no re-arm rule (D9). The PL2 limit lives only in the device-stored entry (D6).

**GPU and IR.** All three GPU packages share `src/Shared/Gpu/DriverRuntime` with write admission passed explicitly, specific refusal codes, per-session native caches and one DRS load per pass, contained publish failures, and a read failure that hides nothing. Intel moves onto it while keeping its visible behaviour. Journals never reset on an unreadable file and never take global controls down. NVIDIA's journal holds no unconfirmed state (D9). IR: protocol 2 with a paged `remotes` catalog on firmware and host (D11), matching replies, refusals classified before emission, loaders that distinguish unreadable from absent, a split into session, store and publisher, and firmware fixes built but flashed only on direction.

**Steam UI host.** `SteamUiCoordinator` owns readiness, the master switch and Big Picture holds with one gate; `SteamUiSessionHost` is built once from a module catalog over backends the composition root built, with plugin modules added and removed as plugins become ready or stop, applies one switches snapshot, and closes command admission at T0 while patch retraction stays after device cleanup. Library tabs and badges are instances that write only on change. Content services share one work slot. RTSS is started with WSGM and kept alive.

**SteamUiToolkit.** One transport with dispatch-aware evaluation over a loopback-only connection; an explicit `SteamClient` with one write lane and four write outcomes; no ambient session. The patch manager runs one coalesced loop, applies the bridge first and removes it last, removes after a failed apply without blind reapply, and owns quarantine faults raised by the module runtime, whose handlers run off the bridge pump. Toolkit presentation is host-supplied data (layout, accents, selectable options) with no WSGM policy or labels in generic code; drafts survive unrelated publications. Content caps go; transport byte bounds stay.

**WindowsDeviceControl.** Static facades with per-call disposable watch registrations, internal ports for WLAN, CCD and power actions, typed outcomes that keep native codes, a Wi-Fi key of SSID bytes plus security, one internal display write gate, rotation-aware matching with 0 meaning "keep", display writes decided by their apply status with no readback (D9), a single arrival waiter with caller-supplied timing, and wake, standby and power restores that attempt every item. WSGM owns all user-visible wording through mapping tables. Version 0.2.0; independent child build and tests on .NET 8 and 10.

**Windows services.** One `StorageInventory` snapshot feeds eject, format, Steam storage and library paths; the SD format run keeps every reverification behind ports; one audio endpoint port, with display-off mute restoring the endpoint it muted; the Windows power statics become instances sharing one lock; one console runner with a tri-state result; other-manager takeover as an instance with a strict restore.

**Game library.** `GameLibraryService` stays the one state owner with scan, apply and collection workers and a Steam port; composed shortcut strings are pinned by golden tests; one title owns a shortcut; artwork providers take their handler and gate as arguments; the artwork browser survives unrelated config reloads.

**Overlay and Settings.** The overlay controller keeps three ports (platform, sheet factory, session power actions), a surface host replaces process globals, activation and navigation become their own owners, pages become controllers, and launch fixes go through a service. Appearance, focus and strings stay byte-identical except where section 4 decides otherwise: the profile editor's press-to-edit rows, the display-mode selector on the sheet's own display, and idle-timeout and policy values that fill in a frame later. Settings saves start from the fresh config with one shared-field table, require complete services, discover displays on a worker, wait for a running save on close, and live in one window per process.

**Device Lab.** Licensed GPL (D3). An instance worker host with a non-blocking watchdog, a machine record that is never overwritten after a failed read, start-up recovery behind the owner reservation with per-item containment, a capture step buffer, and a wizard session that owns one operation at a time. Evidence is kept whole.

**Setup, install and launchers.** The logon service stops cleanly; setup applies answers last, reports partial change truthfully and matches components exactly; uninstall restores the Steam-side files WSGM owns; the de-elevation task is deleted once after its dispatch budget; cross-process names live in one linked file. Security hardening is out of scope by maintainer decision.

**Build and tests.** The canonical gate is safe to run (no real power dispatch, no production mutex), compares cleanup diffs before and after, and checks frozen foundations at the end; templates are compiled; a project graph test guards every move; linked sources that survive move to `src/Shared`.

## 3. Execution protocol

1. **One writer.** Exactly one batch agent edits, formats, builds or tests on the shared checkout `D:\Coding\WSGM` at a time, on `master` (children on `main`). No branch, worktree, clone or pull request. Read-only reviewers and the two read-only batches may run beside a writer but never build or format.
2. **Before a batch.** Check `git status` in the parent and both children; anything unexpected stops the batch. Read the batch spec, the cited review section and its verify corrections. Anchor every edit by symbol; the reports' line numbers are often wrong. Caller inventories exclude `.claude/` (stale worktrees live there).
3. **Rules the writer applies without asking.** Simplify by removing mechanism; no count or length caps that drop content (D2 lists the byte bounds that stay); never gate a write, control or success on readback; never retry an uncertain write automatically; device logic mirrors HC 1.3.1.6; WSGM turns its HidHide cloak off on every exit, uninstall and upgrade path; Explorer is only asked to exit (`0x5B4`, `WM_CLOSE`), never killed; no compatibility facades beyond the requested config migration; UI appearance, strings and workflows identical except where section 4 decides a change; no security hardening against same-user threats (section 4); Avalonia packages and VIIPER untouched; high-rate input and telemetry paths allocate and log nothing per sample. A defect found during a batch that is outside its scope becomes a new batch appended at the right position, not a silent widening.
4. **Validation after a batch.** Build the affected projects warning-free (`dotnet build <project> -c Release`; for cross-project batches `dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true`) and run the batch's narrow tests, which requirement 13 authorizes before manual testing. Toolkit batches also run `npm run prelude:claims` in the child and the parent `steam-assets` build, check and claims. WDC batches run their filter on net8 and, from B046 on, net10. Batches that touch overlay or Settings layout run their Visual filter; baselines are refreshed only for an intended change and after the images are reviewed. `eng/verify.ps1` is never run on an uncommitted tree. The batch result names every test deferred to the final gate.
5. **Adversarial review.** After each batch a separate read-only reviewer checks the diff against the spec and section 3 rules: dropped behaviour, new state or caps, readback gates, per-sample allocation, missed call sites, child formatting with parent settings. The same writer fixes findings before the commit; a design-level finding becomes a maintainer decision.
6. **Formatting.** Rider Full Cleanup with `WSGM.slnx.DotSettings` over the parent C# files the batch changed; child trees use their own settings and are never formatted by parent tools (root Prettier ignores `external/`). `npm run format` for parent Prettier-owned files. Inspect the formatting diff.
7. **Commits.** Commit at coherent milestones (one batch, or consecutive batches of one domain that only make sense together) with `git commit -- <paths>` so the maintainer's staged entries are not swept in, and push right after. For a child: commit and push the child on `main` first, then one parent commit with the gitlink and the consumer edits. No commit message mentions HC or Handheld Companion. No version bump, tag, release or `build.ps1`.
8. **Independent child validation** (requirement 16) happens in every child batch (child-local build and filtered tests) and in full at B062, B072 and B179.
9. **Live state.** No deploy, install, service registration, shell or boot mode change, Device Lab hardware action or Steam CEF tool use. Attended checks named in a batch are the maintainer's to run.
10. **Decisions.** D1 to D14 and the other answers in section 4 are decided and binding; no batch waits on a decision. A new design question found during a batch goes to the maintainer, and only that batch waits.
11. **Final gate and manual acceptance.** B179 runs the full automated gate once on the committed head. The manual matrix stays with the maintainer; a manual failure is fixed in its owner with the narrow test rerun, and the full gate repeats only for broad impact, with the reason stated first.

## 4. Maintainer decisions

The maintainer answered every open question on 2026-10-03 (`DECISIONS.md`). The answers are binding and override the recommended answers this section used to carry, the review solutions and the batch specs written before them.

| # | Decision | Answer | Affected batches |
| --- | --- | --- | --- |
| D1 | Shutdown design | Safety-first ordered steps under one deadline. The planning-corrections B3 percentage cutoffs and preliminary drain are dropped. | B140 |
| D2 | Byte bounds on untrusted input | Exactly this list stays, and each bound refuses, never truncates: manifest 256 KiB and depth 16; plugin package 128 MiB per file and 512 MiB total; glyph document, asset, profile and notice bytes and raster dimensions; ThemeStoreClient 64 and 8 MiB; splash zip bounds; the sound-pack zip-bomb guard on expanded bytes; animation repository download 64 MB; artwork 16 MiB image, 64 MiB cache, 4 MiB JSON (reported, never "absent"); CDP 8 MiB response and 1 MiB notification reads plus request backpressure; IR 32 KiB protocol frame; LaunchPayload pipe bounds; Device Lab review-archive guard, 8 MiB firmware table and 64 KiB device property; EDID 32 KiB. Every other count or length cap is removed. | B138, B147 and every cap removal |
| D3 | Device Lab (MIT) compiles GPL interop files (A02-F022) | Relicense Device Lab as GPL: its licence file, csproj metadata, README, AGENTS and notices change. The interop files are neither relicensed nor duplicated; the SDKs stay MIT. `NativePackageSource` still moves into the Lab, its only consumer. | B172, B173, B177 |
| D4 | Guidance (AGENTS.md) edits the batches need | Approved. Each diff is shown with its batch and applied. | B022, B023, B064, B072, B143, B145, B173, B175, B176, B177 |
| D5 | A title whose route resolves to a shortcut another title owns (LIBRARY-002, LIBRARY-V-001) | Offer it as a second game shortcut: its own Add that creates its own Steam shortcut. | B106 |
| D6 | PL2 home when a layer holds both a device-stored PL2 and `BoostWatts` | Keep the device-stored PL2 and drop `BoostWatts`. The device entry becomes the only home of PL2. | B089 |
| D7 | Ally motion sensor order | WinRT first, like HC (today's order, so PACKAGES-011 needs no change). | B023 |
| D8 | Ally power write order | Mirror HC: SPL, then SPPT and FPPT. The recorded SPL <= SPPT <= FPPT stepping rule goes. Writes stay write-through, with no gating. | B022, B177 |
| D9 | Ally controller entry re-arm | No readback machinery for any vendor except the Claw, the only vendor that allows readbacks. Unresolved or uncertain entries, re-arm rules and recovery entries that wait on a readback are removed from every non-Claw package and every host path. A write either dispatched (published as written) or failed to dispatch. | B016, B017, B022, B023, B067, B069, B072, B099, B143, B152, B177 |
| D10 | Uninstall and upgrade cleanup of Steam-side state WSGM owns | Restore the files (boot-movie override and `.wsgm-original`, the `themes_custom` junction) and also hand back Steam's startup-movie choice at WSGM exit when no WSGM movie is chosen, and at uninstall. | B138, B140 |
| D11 | IR catalogs whose `remotes` reply would exceed the 32 KiB frame | Add catalog paging now: chunked `remotes` replies with a protocol version bump on firmware and host. No build-time size refusal. | B156 |
| D12 | `tools/DeckSpike` and `tools/SteamReceiver` | Delete both, plus `InternalsVisibleTo("DeckSpike")`. | B174 |
| D13 | Library badge before the first publication | WSGM shows the library's name, not "Internal"; there can be several internal libraries, especially on desktop. The toolkit holds no product label; the host supplies the text. | B057 |
| D14 | Elevated setup's single-file host extracts native DLLs into the user's temp (INSTALL-V-001) | Dropped as security theater. No change. | none |

**Security hardening dropped.** The maintainer rejects every security-hardening finding: WSGM is a launcher for gaming handhelds, not an enterprise PC, and it has to launch. Each such finding is no-change with the reason "Dropped by maintainer decision (security theater, DECISIONS.md)" and leaves the batches: the logon service launching `boot.json`'s `ExePath` elevated (INSTALL-001), elevated setup running installers from the user's temp (INSTALL-002, B026 removed), the `%ProgramData%\WSGM` DACL (INSTALL-003), de-elevation task XML in a user-writable folder (INSTALL-008, U04B-LFA-002), single-file native extraction (INSTALL-V-001, D14), the overlay broker's forwarded access (INSTALL-010), the tray relay into higher-integrity targets (SESSION-048, U05-LFB-016), bridge `hostId`, nonce, sender, ownership or integrity checks (U02A-SUTC-004), DLL search-path attributes (TOOLKITCS-010, LIBRARY-033, GPUIR-034), the extension and Device Lab log reparse refusals (U02A-SUTC-061, LABCORE-018), the dev-deploy swap script encoding (BUILD-035) and pinned CI action tags (BUILD-009). Their functional parts stay: the clean service stop and token seam (B025), the updater's partial-file cleanup with the download left where it is, since moving it fixes no functional bug (B027), answers applied last and truthful partial-change reporting (B028), the task cleanup (B029) and exact component matching (B030).

Other answers:

| Topic | Answer | Batches |
| --- | --- | --- |
| Tray relay in Game Mode | Today's relay stays unchanged. | B114 |
| config.json from a newer WSGM or with unknown recovery enums | Loads best effort as today: what is understood is loaded. No UnsupportedSchema outcome and no read-only mode. | B039, B068, B133 |
| Overlay display-mode selector | Targets the display the overlay sheet is shown on, not `paths[0]`. | B130 |
| Add Steam Library and Replace launch action pickers | The native Windows pickers stay, with navigation suspended while open. | B108 |
| Application-profile Name and Process fields | Controller-reachable press-to-edit rows. | B129 |
| Escape and the 3 s timeout during shortcut capture | Keep the existing binding; only an explicit Clear clears it. | B080, B135 |
| SD-card library marker watcher | Keeps watching every ready drive; the comment is fixed. | B094 |
| Plugin Steam UI modules | Registered when the plugin becomes ready and removed when it stops. | B054, B086 |
| Overlay sheet reads of idle timeouts and Windows policies | Off the UI thread; the values fill in a frame later. | B130 |
| RTSS (USER-001) | Starts with WSGM and is kept alive at all times (restart on exit, no cooldown); not launched when RTSS integration is off; never killed. | B005 |
| Modern Standby wake-device line | The 16-device cap goes (no arbitrary limits); the row may wrap. | B135 |
| File picker | No paging. | B059 |

Attended evidence, not decisions: the config mutex ACL check (CONFIG-V-004, a functional save failure under mixed elevation), one real Store catalog answer (LIBRARY-V-004), the store-title icon cache (LIBRARY-034), the toolkit fingerprint live check (B061), and a log of a lingering `steamwebhelper` before any `Steam.IsRunning` change (SESSION-054).

## 5. Manual acceptance matrix changes

The matrix in `manual-acceptance-matrix.md` stays the maintainer's. These changes follow from the refined batches:

- **New rows.** M01-43 RTSS overlay at game start without opening the QAM, and after RTSS is closed mid-session (R on all). M01-44 tray "Exit WSGM" on a device session shows the physical pad again with the cloak off (A N, A D, R C, R X). M01-45 an update logs `Installer shutdown handoff completed ... outcome=Clean` and setup reports Completed without waiting out its grace period (R). M01-46 `--restore-shell` from Game Mode on a device session (R C, R X, A N, A D). M01-47 sign-out, restart and shutdown from the overlay power menu with no blocker screen and no Explorer start (R). M01-48 uninstall with `App\WSGM.exe` missing keeps the HidHide ledger and reports the controller may still be hidden (L). M01-49 logon-service elevated boot and the dirty-exit Explorer fallback (A). M01-50 `WSGM.Launch --deelevate [--input-lease]` under elevated Steam, including the UAC-off fail-open (A). M01-51 the overlay's application-profile editor names a profile and adds, edits and removes processes with the controller alone. M01-52 with an external display, the display-mode selector on the sheet lists and changes the modes of the display the sheet is on. M01-53 exit WSGM with no WSGM boot movie chosen and Steam's own startup-movie choice is back; uninstall restores Steam's boot-movie override and removes WSGM's `themes_custom` link. M01-54 in Settings, Escape during keyboard capture and the 3 s chord timeout keep the previous binding, and Clear clears it.
- **Rewritten.** M01-31: "Picker select, cancel, reopen; in a large folder enter two directories and go Back; the whole list renders as today; a stale listing never replaces the current one; explicit selection, cancel or unmount settles once." M01-42: the hardware-free run also requires the Device Lab `wizard` record folder to be absent.
- **Extended.** M01-03 adds theme, sound and animation changes in Steam and Device Integration enabled from the overlay banner while Settings is open. M01-17 adds a lock or sleep between AutoTDP writes, then exit restores the original. M01-19 adds a rotated display that stays rotated after entry and return. M01-21 and M01-22 check that the Wi-Fi, scan, consent and pairing texts read as today. M01-12 adds a crash ledger followed by a start with integration off. M01-25 adds a desktop with two internal Steam libraries, each badge showing its library's name. M01-37/38 run on firmware 0.5.0 (protocol 2) and Read built-in remotes lists every remote of a catalog above 32 KiB.

## 6. Batches

One global order. Every batch leaves the solution building and its narrow tests green. `Sub` names the submodule a batch commits in first (child commit and push, then the parent gitlink with the consumer edits). `Decisions` names the decided answers from section 4 that a batch applies; no batch waits on a decision. A batch removed by a decision keeps its id with "Removed by maintainer decision".

Implementation status below records source changes and their targeted automated checks. The full gate (B179) and the manual acceptance matrix remain open; an implemented batch is not a live or hardware acceptance claim. Detailed check results are kept in [the implementation tracker](../implementation-todo.md).

| Id | Title | Domain | Depends on | Sub | Decisions | Status |
| --- | --- | --- | --- | --- | --- | --- |
| B001 | Land W02_01 (EDID validity bit) in WindowsDeviceControl | wdc | - | WDC | - | Implemented |
| B002 | W02_02 simplified: one internal power-action port | wdc | B001 | WDC | - | Implemented |
| B003 | A02_04: IR UTF-8 reply bound and reply-document ownership | ir | - | - | - | Implemented |
| B004 | A02_03 with R4: safety zero stays armed until it succeeds | lab | - | - | - | Implemented |
| B005 | USER-001: RTSS starts with WSGM and is kept alive; overlay level never waits on an executable | perf | - | - | - | Implemented |
| B006 | Every exit runs the session cleanup; OS end-session is SessionEnd | session | - | - | - | Implemented |
| B007 | Setup: uninstall never drops the HidHide ledger; Close starts WSGM only after success | install | - | - | - | Implemented |
| B008 | DEVICE-001: an unverified device stop no longer blocks every restart | device | - | - | - | Implemented |
| B009 | Never strand the physical controller | input | B008 | - | - | Implemented |
| B010 | A02_02 trimmed plus SDK-B1 journal and serializer correctness | sdk | B008 | - | - | Implemented |
| B011 | DEVICE-V-001: AutoTDP survives lock, sleep and restart; exit restore always runs | device | - | - | - | Implemented |
| B012 | Failed unlock resume restarts the cycle; passive detection keeps no runtime | device | B009 | - | - | Implemented |
| B013 | Settings save starts from the fresh config; one shared-field table | settings | - | - | - | Implemented |
| B014 | Golden composed-shortcut tests before any library move | library | - | - | - | Implemented |
| B015 | Library correctness fixes that need no new owners | library | B014 | - | - | Implemented |
| B016 | Steam autostart takeover refuses when it cannot record the original | winsvc | - | - | - | Implemented; follow-up open |
| B017 | Windows power writes and hybrid cores stop gating on readback | winsvc | - | - | - | Implemented |
| B018 | An invalid theme update journal no longer stops the session | steamhost | - | - | - | Implemented |
| B019 | Device Lab: a synchronous Continue no longer loses the running stage | lab | - | - | - | Implemented |
| B020 | Device Lab review confirms real TDP, lighting and fan evidence | lab | B019 | - | - | Implemented |
| B021 | Claw command truthfulness, timeout classification and watchdog | packages | - | - | - | Implemented |
| B022 | Ally write-through restore in HC order and published values | packages | B010 | - | D4, D8, D9 | Implemented |
| B023 | Ally fan rollback removal | packages | B022 | - | D4, D9 | Implemented |
| B024 | Read-only closure of the unwritten install and U04B finding bodies | install | - | - | - | In progress |
| B025 | Logon service stops cleanly and gets one token seam | install | B007 | - | - | Implemented |
| B026 | Removed by maintainer decision | install | - | - | - | No change: maintainer decision |
| B027 | Updater download leaves no partial file | install | B025 | - | - | Implemented |
| B028 | Setup applies answers last and reports partial change truthfully | install | B027 | - | - | Implemented |
| B029 | De-elevation task deleted once after its dispatch budget | install | B027 | - | - | Implemented |
| B180 | Core startup closure fixes from B024 | install | - | - | - | Pending |
| B030 | Setup identity refusal, exact component match and testable paths | install | B024, B028, B180 | - | - | Pending |
| B031 | Cross-process names in one linked file; native declaration cleanup | install | B030 | - | - | Pending |
| B032 | Gate and CI hygiene; asset builder writes nothing in check mode | build | - | - | - | Pending |
| B033 | Release payload truth: notices and lock-driven controller names | build | B032 | - | - | Pending |
| B034 | Project graph test replaces the single boundary edge | build | - | - | - | Pending |
| B035 | Plugin templates are real compiled files | build | - | - | - | Pending |
| B036 | One pinned-download helper and a real export check | build | B033 | - | - | Pending |
| B037 | UserDataContext and an instance ConfigStore, behaviour unchanged | config | - | - | - | Pending |
| B038 | Pure config rules, generic enum repair and limit removal | config | B037 | - | - | Pending |
| B039 | Read outcomes, one writer transaction, durable writes, sidecar rules | config | B038 | - | - | Pending |
| B040 | Profile service ownership and fan-out admission close | config | B039 | - | - | Pending |
| B041 | Logging: rotation outside the lock, verbose survives reload | config | B039 | - | - | Pending |
| B042 | Desktop return recovery as an instance; audio profile tests | config | B039 | - | - | Pending |
| B043 | Active clock dispatches cancellations safely | sdk | - | - | - | Pending |
| B044 | SDK native callbacks and poll threads contain exceptions | sdk | - | - | - | Pending |
| B045 | A02_01 replacement: bounded common manifest read | sdk | - | - | - | Pending |
| B046 | WindowsDeviceControl child build foundation and test isolation | wdc | B002 | WDC | - | Pending |
| B047 | Toolkit check infrastructure; WSGM runs every check on its asset | toolkit | B032 | toolkit | - | Pending |
| B048 | Toolkit transport and connection correctness | toolkit | B047 | toolkit | - | Pending |
| B049 | Toolkit probe safety, bridge parsing, module runtime and storage resolution | toolkit | B048 | toolkit | - | Pending |
| B050 | Toolkit bridge reassembly and module resolution that recovers | toolkit | B049 | toolkit | - | Pending |
| B051 | Toolkit gate lifecycle, content caps and missing checks | toolkit | B050 | toolkit | - | Pending |
| B052 | Plugin API 4 contract | sdk | B035, B045 | - | - | Pending |
| B053 | Dispatch-aware outcomes, explicit SteamClient, no ambient session | toolkit | B052, B051 | toolkit | - | Pending |
| B054 | Patch contract, one manager loop, quarantine in the manager, bounded teardown | toolkit | B053 | toolkit | - | Pending |
| B055 | Toolkit component host split, behaviour identical | toolkit | B051 | toolkit | - | Pending |
| B056 | Toolkit public surface cleanup, version 0.2.0 | toolkit | B054 | toolkit | - | Pending |
| B057 | Host-supplied Quick Access presentation with identical output | toolkit | B056, B055 | toolkit | D13 | Pending |
| B058 | Drafts and row refusals | toolkit | B057 | toolkit | - | Pending |
| B059 | File picker: worker enumeration, cancellation and stale-result rejection, whole list as today | toolkit | B058 | toolkit | - | Pending |
| B060 | Toolkit theme cache, carousel cost and small JS items | toolkit | B059 | toolkit | - | Pending |
| B061 | Fingerprints without minified tokens (attended) | toolkit | B060 | toolkit | - | Pending |
| B062 | Toolkit test quality and probe execution coverage | toolkit | B061 | toolkit | - | Pending |
| B063 | WDC watch registrations; RadioManager drops its feed generations | wdc | B046 | WDC | - | Pending |
| B064 | Wi-Fi identity by bytes and security; truthful connect outcomes | wdc | B063 | WDC | D4 | Pending |
| B065 | Radio power results, Bluetooth container normalization and pairing attempts | wdc | B064 | WDC | - | Pending |
| B066 | Core Audio, feedback, backlight and preview correctness | wdc | B065 | WDC | - | Pending |
| B067 | Display write gate, refusal semantics and editor rotation | wdc | B066, B039 | WDC | - | Pending |
| B068 | Schema version and the one-time 2.0 to 2.1 migration | config | B067, B042 | - | - | Pending |
| B069 | Typed display results; WSGM owns display wording | wdc | B068 | WDC | - | Pending |
| B070 | One display arrival waiter in WDC with caller-supplied timing | wdc | B069 | WDC | - | Pending |
| B071 | Power, wake and recovery primitives that attempt every item | wdc | B070 | WDC | - | Pending |
| B072 | WDC docs, metadata and final independent child validation | wdc | B071 | WDC | - | Pending |
| B073 | One controller sample path without per-sample allocation | input | B009 | - | - | Pending |
| B074 | Controller stack composed at the root | input | B073, B012 | - | - | Pending |
| B075 | Overlay-launched Settings navigates from the managed pad | input | B074 | - | - | Pending |
| B076 | Steam Input shim as an owned instance with one apply path | input | B075 | - | - | Pending |
| B077 | Steam Input lease owner created by Program | input | B076 | - | - | Pending |
| B078 | Guide-chord mirror binding without the size cap | input | B077 | - | - | Pending |
| B079 | Touch edge input: one raw-input owner, pure recognizer, subscribers | input | B078 | - | - | Pending |
| B080 | Navigation and recorder fixes without new options plumbing | input | B079 | - | - | Pending |
| B081 | Drive the device runtime directly; delete the compatibility adapter | device | B012, B010 | - | - | Pending |
| B082 | Router command correctness and active-time command deadline | device | B081 | - | - | Pending |
| B083 | Bounded single-task device shutdown with admission close | device | B082 | - | - | Pending |
| B084 | Steam host local defect fixes | steamhost | B054 | - | - | Pending |
| B085 | One switches snapshot that keeps every edge effect | steamhost | B084 | - | - | Pending |
| B086 | Steam UI backends built outside the host | steamhost | B085 | - | - | Pending |
| B087 | NativeQam split and projection moves against the concrete coordinator | steamhost | B086 | - | - | Pending |
| B088 | Extract DeviceCycle with minimal ports and real-owner tests | device | B083, B087 | - | - | Pending |
| B089 | Desired-state restorer and user-write helpers; PL2 single home | device | B088 | - | D6 | Pending |
| B090 | Windows power statics become injected instances | winsvc | B017, B088 | - | - | Pending |
| B091 | PowerLimitOwner owns sustained, boost, presets and AutoTDP | device | B090, B089 | - | - | Pending |
| B092 | DeviceControllerHandoff owns controller start, loss and claims | device | B091, B074 | - | - | Pending |
| B093 | Device facade, overlay contracts and small cleanups | device | B092 | - | - | Pending |
| B094 | Windows services correctness fixes and shutdown disposal order | winsvc | B071 | - | - | Pending |
| B095 | One console runner; other-manager takeover as an instance | winsvc | B094, B039 | - | - | Pending |
| B096 | StorageInventory: one storage snapshot for every consumer | winsvc | B095, B084 | - | - | Pending |
| B097 | SD format run split with ports | winsvc | B096 | - | - | Pending |
| B098 | Display mode services lose their test-only branches | winsvc | B069, B094 | - | - | Pending |
| B099 | One audio endpoint port; display-off mute restores the muted endpoint | winsvc | B066, B094 | - | - | Pending |
| B100 | RTSS discovery and shared-memory caps | winsvc | B005, B094 | - | - | Pending |
| B101 | Remaining Windows service owner tests and card monitor polling | winsvc | B097, B099 | - | - | Pending |
| B102 | Artwork providers take their handler and gate as arguments | library | B053 | - | - | Pending |
| B103 | Artwork browser keeps its pages across unrelated config reloads | library | B102, B039 | - | - | Pending |
| B104 | GameLibraryService pure extractions | library | B103 | - | - | Pending |
| B105 | GameLibraryService workers, Steam port, threading and lifetime | library | B104 | - | - | Pending |
| B106 | A Steam shortcut is owned by one title | library | B105 | - | D5 | Pending |
| B107 | Library sources and helpers | library | B106 | - | - | Pending |
| B108 | Overlay defect corrections that need no new owners | overlay | - | - | - | Pending |
| B109 | Overlay surface host replaces process globals | overlay | B108 | - | - | Pending |
| B110 | Overlay navigation controller without depth cap or render-cycle guards | overlay | B109 | - | - | Pending |
| B111 | Startup options, application runtime and crash-loop breaker | session | B006 | - | - | Pending |
| B112 | Explorer primitives behind ports with one launcher | session | B111 | - | - | Pending |
| B113 | Overlay activation sources out of the controller | overlay | B110, B079 | - | - | Pending |
| B114 | One owned MessageWindow; tray retirement verified | session | B112, B113 | - | - | Pending |
| B115 | Game Mode transition backend collapsed; Steam process port | session | B114, B042, B076 | - | - | Pending |
| B116 | Session power queue and config reloader as owners | session | B115, B040, B041 | - | - | Pending |
| B117 | Settings services required and complete; no production fallbacks | settings | B013, B039, B077 | - | - | Pending |
| B118 | Settings discovers displays on the worker in every composition | settings | B117 | - | - | Pending |
| B119 | Closing Settings during a save waits for it; truthful save status | settings | B118 | - | - | Pending |
| B120 | Hardware query types and the default accent move out of UI files | settings | B119 | - | - | Pending |
| B121 | Settings updates and plugin packages through services | settings | B120 | - | - | Pending |
| B122 | Overlay controller ports and lifecycle suite | overlay | B113, B121 | - | - | Pending |
| B123 | One Settings window per process | settings | B122 | - | - | Pending |
| B124 | Session transitions extracted; per-owner startup isolation | session | B116, B123 | - | - | Pending |
| B125 | Session policy leaves the overlay controller | overlay | B124 | - | - | Pending |
| B126 | Shared capability rows and commit-on-close combo helper | overlay | B125 | - | - | Pending |
| B127 | Overlay page controllers | overlay | B126 | - | - | Pending |
| B128 | Launch fixes through one service | overlay | B127, B053 | - | - | Pending |
| B129 | Overlay tool views on typed backend results; press-to-edit profile rows | overlay | B128, B105 | - | - | Pending |
| B130 | Overlay Windows, display and device projections through session owners | overlay | B129, B093, B090 | - | - | Pending |
| B131 | Overlay test quality | overlay | B130 | - | - | Pending |
| B132 | Overlay nit sweep | overlay | B131 | - | - | Pending |
| B133 | Settings on the instance config store with visible load outcome | settings | B123 | - | - | Pending |
| B134 | LiveBackdrop internal seam and lifecycle tests | settings | B133 | - | - | Pending |
| B135 | Settings test cleanup and nits | settings | B134 | - | - | Pending |
| B136 | SteamUiCoordinator owns readiness, the master switch and Big Picture holds | steamhost | B124, B087 | - | - | Pending |
| B137 | Library tabs and badges as instances | steamhost | B136 | - | - | Pending |
| B138 | Theme, animation and sound content services | steamhost | B137, B018 | - | D2, D10 | Pending |
| B139 | Steam UI asset pipeline cleanup | steamhost | B138, B057 | - | - | Pending |
| B140 | Shutdown as ordered safety-first steps under one deadline | session | B136, B093, B040, B138 | - | D1, D10 | Pending |
| B141 | Remaining session dissolution, splash and docs | session | B140 | - | - | Pending |
| B142 | Device API 12 contract with package consumers | sdk | B052, B021, B022, B035 | - | - | Pending |
| B143 | One package recovery policy: no automatic replay of an uncertain restore | packages | B142 | - | D4, D9 | Pending |
| B144 | Shared deterministic helpers move into the SDK | packages | B143 | - | - | Pending |
| B145 | Claw transports and identity snapshot | packages | B144 | - | D4 | Pending |
| B146 | One plugin loader without the forwarding wrapper | sdk | B142, B081 | - | - | Pending |
| B147 | Plugin catalog, installer, package layout and packer validation | sdk | B146, B121 | - | D2 | Pending |
| B148 | GPU coordinator hygiene | sdk | B146, B089 | - | - | Pending |
| B149 | SDK test cleanup that keeps the regression guards | sdk | B148, B147 | - | - | Pending |
| B150 | GPU journals: unreadable never resets, controls survive a bad per-app record | gpu | B052 | - | - | Pending |
| B151 | Shared GPU runtime contract and behaviour | gpu | B150 | - | - | Pending |
| B152 | NVIDIA and AMD cleanups | gpu | B151 | - | - | Pending |
| B153 | Intel onto the shared driver runtime with its visible behaviour kept | gpu | B152 | - | - | Pending |
| B154 | IR host correctness | ir | B003, B052 | - | - | Pending |
| B155 | IR plugin split by responsibility | ir | B154 | - | - | Pending |
| B156 | IR firmware fixes and catalog paging with protocol 2 | ir | B155 | - | D11 | Pending |
| B157 | Device Lab worker host as an instance with tests | lab | B004 | - | - | Pending |
| B158 | Device Lab machine record and start-up recovery | lab | B157, B020 | - | - | Pending |
| B159 | Device Lab read probes and the MSI_ACPI channel | lab | B158 | - | - | Pending |
| B160 | Device Lab transport tidy | lab | B159 | - | - | Pending |
| B161 | Device Lab capture step buffer and evidence completeness | lab | B160 | - | - | Pending |
| B162 | Device Lab application, CLI and process hygiene | lab | B161 | - | - | Pending |
| B163 | Device Lab reports folder and one archive reader | lab | B020, B162 | - | - | Pending |
| B164 | Device Lab export keeps every file in memory without caps | lab | B163 | - | - | Pending |
| B165 | Device Lab developer GUI runner and statics | lab | B164 | - | - | Pending |
| B166 | WizardSession owns one operation at a time | lab | B165 | - | - | Pending |
| B167 | Preflight, Identity, SystemDump and Finish stage controllers | lab | B166 | - | - | Pending |
| B168 | Buttons and Motion stage controllers | lab | B167 | - | - | Pending |
| B169 | Rumble stage controller | lab | B168 | - | - | Pending |
| B170 | Power test stage controllers | lab | B169 | - | - | Pending |
| B171 | Lighting and Sleep stage controllers | lab | B170 | - | - | Pending |
| B172 | HidHide adapter sharing and test hygiene | input | B171, B080 | - | D3 | Pending |
| B173 | Surviving linked sources move to src/Shared; Device Lab becomes GPL | build | B172, B141, B031, B029 | - | D3 | Pending |
| B174 | Source scrapers replaced; tools compiled in the gate | build | B173, B139, B141 | - | D12 | Pending |
| B175 | Owned-staging ceremony replaced by one rule | build | B174 | - | D4 | Pending |
| B176 | Test helpers and build documentation | build | B175 | - | - | Pending |
| B177 | Documentation pass and guidance proposals | docs | B176, B156, B145, B153, B062, B072, B132, B135, B101, B107, B149 | toolkit | - | Pending |
| B178 | Ledger and coverage reconciliation | docs | B177 | - | - | Pending |
| B179 | Final automated gate and hand-off to manual acceptance | docs | B178 | - | - | Pending |

### Phase A: admitted batches and live defects

#### B001 Land W02_01 (EDID validity bit) in WindowsDeviceControl

- **Status: implemented**, parent commit `8d9c6747`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: wdc. Depends on: none. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs`; `external/windows-device-control/tests/WindowsDeviceControl.Tests/DisplayTopologyTests.cs`.
- Steps: The W02_01 edit is already in the child working tree, uncommitted (execution/W02_01/final.md). Normalize the new lines to CRLF so neither file has mixed line endings (WDC-012). Build the child library for both TFMs, run the three W02_01 tests, commit the two files in the child on main and push, then record the gitlink in the parent with a pathspec commit. No other change. Every later WDC batch starts from this commit.
- Tests: `dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~EdidIdsFollowOnlyTheValidityBit|FullyQualifiedName~ValidZeroEdidIdsArePreserved|FullyQualifiedName~ExistingPathIdentitySurvivesEdidPopulation"`.
- Resolves: WDC-012, A01-F001.

#### B002 W02_02 simplified: one internal power-action port

- **Status: implemented**, parent commit `678d6e4b`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: wdc. Depends on: B001. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/WindowsPower.Actions.cs`; `external/windows-device-control/tests/WindowsDeviceControl.Tests/WindowsPowerTests.cs`.
- Steps: Replaces the sealed W02_02 brief (two interfaces plus a process wrapper) with the shape in _plan/refactor-2.1/review/wdc.md section 3.2: internal interface IPowerActionApi { bool Suspend(bool hibernate); Task<int> RunToolAsync(ProcessStartInfo, CancellationToken); } with a private static NativePowerActionApi (SetSuspendState, Win32Exception from GetLastPInvokeError on false; start, await exit, return ExitCode, dispose). Internal overloads SuspendAsync(bool, IPowerActionApi, CancellationToken) re-check cancellation inside the delegate right before Suspend, and RequestActionAsync(action, api, token) validates the action, checks the token, runs, and throws the existing Win32Exception on a non-zero exit. Public overloads delegate to them. Replace CancelledActionsNeverDispatch with four fake-backed tests: cancelled token gives zero port calls on both paths, arguments forwarded unchanged, non-zero exit throws with the code, native Suspend failure propagates once. Child commit and push, then gitlink. This removes the gate's ability to suspend or restart the machine (BUILD-001 power half).
- Tests: `dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~WindowsPowerTests"`.
- Resolves: WDC-013, A01-F002.

#### B003 A02_04: IR UTF-8 reply bound and reply-document ownership

- **Status: implemented**, parent commit `b496693c`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: ir. Depends on: none.
- Files: `src/WSGM.Plugin.Ir/IrEndpoint.cs`; `tests/WSGM.Plugin.Ir.Tests/IrEndpointConnectionTests.cs`.
- Steps: Execute batches/A02_04.md unchanged (UTF-8 byte bound on the completed line, ReadReply with try/finally ownership transfer, ScriptedLink fixtures, test helper JsonDocument scoping). It is self-contained and admitted; GPUIR-013 classification is added later in the IR host batch on top of ReadReply.
- Tests: `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj --filter "FullyQualifiedName~IrEndpointConnection"`.
- Resolves: A02-F015, A02-F016.

#### B004 A02_03 with R4: safety zero stays armed until it succeeds

- **Status: implemented**, parent commit `f1be97f7`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: lab. Depends on: none.
- Files: `src/WSGM.DeviceLab/Worker/LabWorkerSession.cs`; `tests/WSGM.DeviceLab.Tests/Worker/LabWorkerSessionTests.cs`.
- Steps: Execute batches/A02_03.md with the R4 simplification from _plan/refactor-2.1/review/labcore.md section 3 (verified in labcore.verify.md): move _lastFrame = DateTime.MaxValue after a zero that returned normally, so the existing stale predicate re-fires on its own; drop the '_zeroFailure non-null OR stale' clause from step 1 and keep _zeroFailure only to de-duplicate the zero-failed log line. Keep the broadened TargetInvocationException catch and sticky StreamError. No timer, lock or GUI change.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabWorkerSessionTests|FullyQualifiedName~LabRumbleWorkerTests"`.
- Resolves: LABCORE-003, A02-F012.

#### B005 USER-001: RTSS starts with WSGM and is kept alive; overlay level never waits on an executable

- **Status: implemented**, parent commit `b0d8fa5d`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: perf. Depends on: none.
- Files: `src/WSGM/Core/PerformanceService.cs`; `src/WSGM/Core/RtssLauncher.cs`; `src/WSGM/Shell/SteamUiSessionHost.cs`; `src/WSGM/Shell/PerformanceOverlayBridge.cs`; `src/WSGM/Overlay/OverlayWindow.Sources.cs`; `docs/rtss.md`; `docs/decisions.md`; tests/WSGM.Tests (PerformanceService tests).
- Steps: Implement _plan/refactor-2.1/review/_user-reported.md exactly (maintainer decision 2026-10-03, binding). (1) PerformanceService probes immediately at start and when RTSS integration is switched on at runtime; on NotRunning it starts the verified RTSS executable at once. (2) Hold the running RTSS process (started or discovered) and restart it on Process.Exited, as HC RTSSPlatform does; the unconditional 5 s poll is the backstop. (3) Delete RtssLauncher.RestartCooldown; keep 'start only the verified executable on a NotRunning probe' and the 10 s settle. (4) Delete ObservationGate _observers, AcquireObservation, ObserverCount and the _observers.WaitAsync branch; the poll runs for the service lifetime and only probes when disabled. (5) Delete UpdatePerformanceObservation, ReleasePerformanceObservation, _performanceObservation and _observationGate in SteamUiSessionHost, PerformanceOverlayBridge.AcquireObservation and _performanceObservation in OverlayWindow.Sources.cs (RunningApplicationTarget's monitor lease is a different lease; leave it). (6) In ApplyOneAsync defer only FrameLimit when the executable is unknown; OverlayLevel applies on the global profile. No retry path is added: drift repair writes the desired level once RTSS is Ready. Integration off means no launch and WSGM never kills RTSS. Update docs/rtss.md (start/keep-alive, remove the cooldown log line) and docs/decisions.md.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PerformanceService"`.
- Resolves: USER-001.

#### B006 Every exit runs the session cleanup; OS end-session is SessionEnd

- **Status: implemented**, parent commit `b0d8fa5d`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: session. Depends on: none.
- Files: `src/WSGM/Core/ApplicationShutdown.cs`; `src/WSGM/App.axaml.cs`; `src/WSGM/Program.cs`; `src/WSGM/Shell/ShellSession.Shutdown.cs`; `src/WSGM/Shell/DesktopTray.cs`; `tests/WSGM.Tests/Core/ApplicationShutdownTests.cs`.
- Steps: Fix SESSION-V-001 and the defects that land with it (_plan/refactor-2.1/review/session.verify.md, Missed findings and batch problems 1-3). ApplicationShutdownRequest.ShutdownLifetime stops calling the forced lifetime.Shutdown(): the shutdown path runs ShellSession.ShutdownAsync once under the reason's budget and only then calls the forced Shutdown(code). Tray 'Exit WSGM', --restore-shell, update and uninstall requests and WTS_SESSION_LOGOFF all go through it. A platform ShutdownRequested (WM_QUERYENDSESSION) is mapped to SessionEnd: do not cancel it, run the bounded 5 s SessionEnd cleanup, never restore Explorer and never send steam://close/bigpicture on it (SESSION-007). Keep the startup-failure exit code 1 sticky across the now-reachable handler (SESSION-002). OnSessionEnding records the reason even when a shutdown is already running so the running cleanup reads it before the Explorer step (SESSION-V-005, SESSION-032). Guard the DesktopTray menu actions the way SettingsActivation does and add one Shell-mode Dispatcher.UIThread.UnhandledException handler that logs and requests the normal shutdown instead of letting the loop die into Panic (SESSION-V-002). Tests: a fake lifetime whose Shutdown never calls back still sees session cleanup run exactly once for tray Exit, update and restore-shell; an OS end-session request is SessionEnd with zero Explorer calls; startup failure exits 1 after cleanup; escalation during a running shutdown only tightens the deadline.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ApplicationShutdown|FullyQualifiedName~ModeSelection"`.
- Resolves: SESSION-V-001, SESSION-V-002, SESSION-V-003, SESSION-V-005, SESSION-002, SESSION-007, SESSION-032, SESSION-009.

#### B007 Setup: uninstall never drops the HidHide ledger; Close starts WSGM only after success

- **Status: implemented**, parent commit `b0d8fa5d`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: install. Depends on: none.
- Files: `src/WSGM.Setup/Engine/SetupEngine.cs`; `src/WSGM.Setup/UI/SetupViewModel.cs`; `tests/WSGM.Tests/Setup`.
- Steps: INSTALL-V-003: when App\WSGM.exe is missing, RestoreController sets StillHiddenDevices = ReadLedgerDevices() and returns false, so DeleteUserData keeps hidhide-ownership.json and the existing 'controller may still be hidden' summary shows. INSTALL-V-002: SummaryPage's primary action calls StartWsgm only for the success summary (pass ok to the page or check it in OnPrimary). No UI text change. Tests cover both decisions through the existing engine seams with temp paths.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup"`.
- Resolves: INSTALL-V-003, INSTALL-V-002.

#### B008 DEVICE-001: an unverified device stop no longer blocks every restart

- **Status: implemented**, parent commit `b0d8fa5d`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: device. Depends on: none.
- Files: `src/WSGM/Shell/DevicePluginCompatibilityAdapter.cs`; `src/WSGM/Shell/PluginHost.cs`; `tests/WSGM.Tests/Shell/DevicePluginRuntimeTests.cs`.
- Steps: Small HC-model fix ahead of the structural device batches (_plan/refactor-2.1/review/device.verify.md batch problem 1): the device release counts as done once runtime.StopAsync returned, whatever its status. The adapter's StopAsync returns true after the runtime stop returns, and the Device registration retires on completed disposal, so the single Device slot is free for the fault restart, post-sleep restart, integration off/on, controller fallback restart and Retry. Test: an Unverified and a Failed stop are each followed by a successful new cycle with the same package id.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~PluginHost|FullyQualifiedName~DeviceCoordinator"`.
- Resolves: DEVICE-001.

#### B009 Never strand the physical controller

- **Status: implemented**, parent commit `c1ed825c`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: input. Depends on: B008.
- Files: `src/WSGM/Shell/ControllerManager.cs`; `src/WSGM/Shell/HidHideOwnership.cs`; `src/WSGM/Input/ManagedControllerRouter.cs`; `src/WSGM/Shell/DevicePluginRuntime.cs`; `src/WSGM/Shell/DeviceCoordinator.cs`; `tests/WSGM.Tests/Shell/ControllerManagerTests.cs`; `tests/WSGM.Tests/Shell/HidHideOwnershipTests.cs`; `tests/WSGM.Tests/Shell/DevicePluginRuntimeTests.cs`.
- Steps: One safety batch with one acceptance list (critic 2.7). INPUT-B1 as corrected in _plan/refactor-2.1/review/input.verify.md: ReleaseAsync takes the caller's Deadline and attempts every step; the HidHide show and SetState run in finally unless keepPhysicalHidden is set (fault-restart path keeps the pad hidden, INPUT-V-005); DisposeAsync bounds the transition wait with the shutdown Deadline it already has (no new 5 s budget) and shows in finally. Update the four DeviceCoordinator call sites (1236, 1412, 1619, 1710). INPUT-V-003: open HidHide, write cloak-off first and unconditionally (skip only on not-installed), then read and remove ledger entries, collecting failures (HC RestoreAllControllersForUninstall order). INPUT-002: an unreadable ledger still turns the cloak off and leaves the file byte-identical. INPUT-V-001: SetSyntheticButtonAsync(pressed) moves inside the existing try/finally so a refused route never latches a button. INPUT-V-002: RemoveUnderGateAsync clears Target, detaches output and sets Absent whatever the backend reported, still logging the unverified removal. INPUT-V-004 and DEVICE-005: when a cycle starts with controller management off, or the coordinator is created with integration off, no package, two packages or passive detection, call ShowPhysicalControllerAsync once if the ledger is non-empty. DEVICE-003 release half: DevicePluginRuntime.ReleaseControllerAsync and the emergency Plugin.StopAsync wait on plugin code only up to the deadline and keep the load context if it has not returned. Tests: cancelled release shows and sets state; dispose with a throwing backend shows; corrupt ledger turns cloak off; refused synthetic pulse leaves _syntheticButtons empty and the next live sample carries no synthetic bit; refused VIIPER removal then same-kind start creates a fresh target; start with integration off and a crash ledger shows once; fault-restart release does not show; a plugin whose release never returns lets release return at the deadline.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ControllerManager|FullyQualifiedName~HidHideOwnership|FullyQualifiedName~ManagedControllerRouter|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceIntegrationOff"`.
- Resolves: INPUT-001, INPUT-002, INPUT-004, INPUT-034, INPUT-V-001, INPUT-V-002, INPUT-V-003, INPUT-V-004, INPUT-V-005, DEVICE-005.

#### B010 A02_02 trimmed plus SDK-B1 journal and serializer correctness

- **Status: implemented**, parent commit `b63b3775`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: sdk. Depends on: B008.
- Files: `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs`; `src/WSGM.Device.Sdk/Services/DeviceCommandSerializer.cs`; `src/WSGM.Device.Sdk/Services/DeviceServiceLifecycle.cs`; `tests/WSGM.Device.Sdk.Tests/Services/*`.
- Steps: Apply batches/A02_02.md steps 1 and 4 only: LoadAsync opens directly and treats only FileNotFoundException as absent (directory, access, malformed and undefined-status data keep the file and set FailureReason); SetStatusAsync and Validate reject undefined statuses before any IO. Drop steps 2 and 3 (the post-gate re-check and the save-failure latch) per _plan/refactor-2.1/review/sdk.verify.md batch problem 1: a failed save already refuses its own mutation, and the latch would turn one transient file lock into the loss of every journalled control. Then SDK-B1 without its extra lock: stop disposing _writeGate and the serializer _gate (DisposeAsync becomes a no-op until Device API 12 removes it); RepublishAsync uses the command's active Deadline (Earliest with PostCommandLimit) instead of a wall timer; RollBackStartAsync honours context.Deadline. Tests: A02_02 fixtures for absent, directory-at-path, malformed and undefined status; dispose while gated work runs completes without ObjectDisposedException; post-command publish cancels on the active deadline; rollback honours its deadline.
- Tests: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Services"`.
- Resolves: SDK-002, SDK-003, SDK-034, A02-F005, A02-F006, A02-F007, A02-F009.

#### B011 DEVICE-V-001: AutoTDP survives lock, sleep and restart; exit restore always runs

- **Status: implemented**, parent commit `b0d8fa5d`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: device. Depends on: none.
- Files: `src/WSGM/Shell/AutoTdpService.cs`; `tests/WSGM.Tests/Shell/AutoTdpServiceTests.cs`.
- Steps: Remove _restoreCycle and _restoreCapability from Availability and StopAsync (_plan/refactor-2.1/review/device.verify.md DEVICE-V-001). The restore obligation is the original watts (and the pair's original) written through whichever primary power capability is published at stop time; if none is published, log once and keep the value. Collapses the restore record (DEVICE-026 part). Tests: enable, write once, advance the cycle generation, then Availability is Available, a tick writes, and disposal restores the original exactly once.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AutoTdpService"`.
- Resolves: DEVICE-V-001.

#### B012 Failed unlock resume restarts the cycle; passive detection keeps no runtime

- **Status: implemented**, parent commit `5687a12e`; targeted checks recorded in the implementation tracker. Live/manual acceptance remains open.
- Domain: device. Depends on: B009.
- Files: `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/DevicePluginRuntime.cs`; `src/WSGM/Shell/DeviceOemActionRouter.cs`; `tests/WSGM.Tests/Shell/DeviceCoordinatorConcurrencyTests.cs`.
- Steps: DEVICE-V-002: handle a failed resume the same way for unlock and sleep, by restarting the cycle (delete the afterSystemSleep-only branch). DEVICE-V-003: treat Passive like no package: dispose the runtime after the detection result, keep no client, state Passive; guard runtime StopAsync with _pluginStartAttempted. DEVICE-V-004: delete DeviceOemActionRouter._cycleGeneration and the generation parameter of Reset, fix the stale comment. Tests: unlock-resume throwing leads to one restart and forwarding resumes; a passive fixture suspends and resumes with zero plugin calls and no restart.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceOemActionRouter"`.
- Resolves: DEVICE-V-002, DEVICE-V-003, DEVICE-V-004.

#### B013 Settings save starts from the fresh config; one shared-field table

- **Status: implemented.** 105 Settings/Steam tests and 31 isolated Settings UI tests passed; full Release solution compilation had zero warnings/errors. Rider cleanup, Prettier and guidance checks passed. Two Display baselines were reviewed and refreshed for the footer change already in `a27e049b`. Live/manual acceptance remains open.
- Domain: settings. Depends on: none.
- Files: `src/WSGM/Settings/SettingsViewModel.Save.cs`; src/WSGM/Settings/SettingsSaveMerge.cs (new); `src/WSGM/Settings/SettingsViewModel.DeviceSetup.cs`; `src/WSGM/Settings/SettingsViewModel.cs`; `src/WSGM/Settings/SettingsViewModel.Plugins.cs`; src/WSGM/Core/WsgmSharedSettings.cs (new); `src/WSGM/Shell/WsgmSteamSettingsService.cs`; `tests/WSGM.Tests/Settings/SettingsSaveMergeTests.cs`; `tests/WSGM.Tests/Settings/PluginSettingsViewModelTests.cs`; `tests/WSGM.Tests/Settings/DeviceProfileAuthoringTests.cs`; `tests/WSGM.UiTests/Infrastructure/UiFixture.cs`.
- Steps: SETTINGS-B1 and B2 merged with _plan/refactor-2.1/review/settings.verify.md corrections. SettingsSaveMerge.Apply(fresh, request, splash) starts from the fresh strict load and copies only Settings-owned fields; the runtime restore list goes. Fields another surface writes while Settings is open live in one table (Core/WsgmSharedSettings: name, read, write): the Steam page toggles and StartMode plus DeviceIntegration.Enabled, AutoTdpEnabled, Profiles.Global.ControllerTarget and GlyphSelection; each is written only when edited here (SETTINGS-V-002 replaces the three bespoke edited/saved pairs and the six SaveRequest members; DeviceEditsMade becomes a query over the table). The Steam page toggles reference the same entries. Make the plugin-setting and device-profile merge single-path (SETTINGS-V-001): the merge calls PluginSettingsResolver.Store on the fresh scope and runs ProfileEdits.RemoveFanCurveReferences; delete ApplyPluginSettingsTo/ApplyDeviceProfilesTo and retarget the four tests to CaptureSaveRequest plus SettingsSaveMerge.Apply. Guard test is a round trip, not a classification: every bound Settings value set to a non-default survives capture plus merge, and shared and runtime fields keep fresh, including Themes, Sounds, Animations and Animations.SteamSetAside (recovery state).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~WsgmSteamSettings"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings".
- Resolves: SETTINGS-001, SETTINGS-002, SETTINGS-003, SETTINGS-004, SETTINGS-V-001, SETTINGS-V-002, CONFIG-022.

#### B014 Golden composed-shortcut tests before any library move

- **Status: implemented.** All 31 golden cases passed, including Amazon discovery-driven ordering. Final shared compilation/formatting checks are recorded with B015. Live/manual acceptance remains open.
- Domain: library. Depends on: none.
- Files: tests/WSGM.Tests/Core/Library/ComposedShortcutGoldenTests.cs (new).
- Steps: LIBRARY-B0 (_plan/refactor-2.1/review/library.md section 5): for each source route shape (Xbox packaged both modes with and without multiplayer acknowledgement, Epic launcher and direct, GOG direct and Galaxy, Ubisoft, Battle.net launcher and classic, Amazon both orders, itch, Prism, ATLauncher, folder .exe/.lnk/.url, follow with dir/marker and drive-root refusal) assert the exact ShortcutFields strings produced today. Test-only.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ComposedShortcutGolden|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"`.
- Resolves: LIBRARY-030.

#### B015 Library correctness fixes that need no new owners

- **Status: implemented.** All 806 required library cases passed, including B014's 31 golden cases. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. The stale Overlay route expectation from before `79789dc2` now targets the existing Tools/System route. `TryReadIdAnd` and its unused maximum parameter were already absent in this baseline, so LIBRARY-V-006 required no edit. Live/manual acceptance remains open.
- Domain: library. Depends on: B014.
- Files: `src/WSGM/Shell/RunningApplicationTarget.cs`; `src/WSGM/Core/LaunchWrapperCommand.cs`; `src/WSGM/Core/Library/PackagedLauncherShortcut.cs`; `src/WSGM/Core/Library/ImportStateStore.cs`; `src/WSGM/Shell/GameLibraryService.cs`; `src/WSGM/Shell/SteamLibraryImportSurface.cs`; `tests`.
- Steps: Subset of LIBRARY-B1 with _plan/refactor-2.1/review/library.verify.md corrections: (1) LIBRARY-001: list WSGM.PackagedLaunch.exe with the helper names and make NormalizeShortcutTarget refuse it (one helper-name list, LIBRARY-027 name part). (2) LIBRARY-003: Sanitize uses type checks only and logs drops (no length caps). (3) LIBRARY-005: the four settings commands and OpenArtworkAsync refuse instead of throwing so no exception reaches RespondAsync. (4) LIBRARY-019: generation/token check before PruneChoices, delete '_ = previous'. (5) LIBRARY-004 corrected: after an unconfirmed update, re-read the shortcut once through the existing _readShortcut seam and record exactly what Steam holds when it equals the composed fields, otherwise keep the old record and stop (OwnsRecorded stays exact). (6) LIBRARY-V-006: delete TryReadIdAnd's unused maximum parameter. (7) LIBRARY-V-007: check Path.IsPathFullyQualified on AddFolderAsync input before normalizing. (8) Stale comments (LIBRARY-031). Not here: LIBRARY-002/V-001 (decision D5), LIBRARY-007 (moved to the workers batch), HasMore (provider batch), the DisabledSources cap (config rules batch).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"`.
- Resolves: LIBRARY-001, LIBRARY-003, LIBRARY-004, LIBRARY-005, LIBRARY-019, LIBRARY-027, LIBRARY-031, LIBRARY-V-006, LIBRARY-V-007.

#### B016 Steam autostart takeover refuses when it cannot record the original

- Follow-up open: B024 confirmed that UNCOVERED-002's restore pre-read remains in the current source. B180 owns that omitted fix; the previously applied recording/write changes and their checks remain valid.
- **Status: implemented.** All 27 targeted autostart cases passed using fakes after formatting. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. Recovery-record failure now prevents the write, accepted writes do not wait for readback, and restoration retains the owned-byte comparison. No live startup settings were changed; manual acceptance remains open.
- Domain: winsvc. Depends on: none.
- Files: `src/WSGM/Core/SteamAutostartService.cs`; `src/WSGM/Core/SteamAutostartTakeover.cs`; `tests/WSGM.Tests/Core/SteamAutostartTests.cs`.
- Steps: CRIT-001 (U04B-LFA-003): remove the catch in SteamAutostartService.RecordDisabled so a failed ConfigStore mutation propagates and the item's existing try refuses the write, as OtherManagers.Record already does. No new state. D9 on the same host path (findings/crosscutting.md UNCOVERED-010): the task disable trusts the schtasks exit code and drops the confirming IsTaskEnabled read, and the registry-approval branch builds DisabledApproval() once, writes it, records exactly those bytes in WrittenApproval and reads nothing back; Restore still recognises WSGM's own change by comparing the current bytes with WrittenApproval. Tests: a throwing record delegate leaves the task enabled and the approval untouched; a task query that still reports enabled after a successful disable, and an approval read that still means enabled after the write, both land the source Disabled and not pending.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`.
- Resolves: CRIT-001.

#### B017 Windows power writes and hybrid cores stop gating on readback

- **Status: implemented.** All 102 required power cases passed using fakes after formatting. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. Power writes and immediate Overlay/QAM projections, including the Overlay timeout event path, publish accepted values without confirming reads. Explicit timeout selection no longer requires a read; failed writes allow another explicit selection. No live power settings were changed; manual acceptance remains open.
- Domain: winsvc. Depends on: none.
- Files: `src/WSGM/Core/PowerSchemes.cs`; `src/WSGM/Core/CpuBoost.cs`; `src/WSGM/Core/HybridCores.cs`; `src/WSGM/Core/DisplayTimeouts.cs`; `src/WSGM/Shell/NativeQamHybridCoreService.cs`; `tests`.
- Steps: WINSVC-010 under D9 (no readback machinery on any host path): every Windows power apply path writes, refreshes the active scheme where the setting needs it and publishes the written value; no read follows the write, not even for a log line. DisplayTimeouts.Select writes without a prior read. CRIT-003: NativeQamHybridCoreService.SetHybridCoresAsync validates against the published option list, writes, publishes the written value, and the _requiresRead latch is deleted (the ApplicationPerformanceReconciler.ApplyCpuBoostAsync model). Tests: a write publishes the written value with no read after it; a failed hybrid-core write does not refuse the next explicit selection.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~DisplayTimeout|FullyQualifiedName~NativeQamHybridCore"`.
- Resolves: WINSVC-010, CRIT-003.

#### B018 An invalid theme update journal no longer stops the session

- **Status: implemented.** All 101 required theme/animation cases passed after formatting. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. Journal recovery retains the 128 KiB bound, removes the name-count cap and uses generated JSON metadata. Invalid recovery becomes a load error; both work slots release busy state after unexpected failure. No live Steam changes were made; manual acceptance remains open.
- Domain: steamhost. Depends on: none.
- Files: `src/WSGM/Core/Themes/ThemeInstaller.cs`; `src/WSGM/Core/Themes/ThemeLoader.cs`; `src/WSGM/Shell/ThemeService.cs`; `src/WSGM/Shell/AnimationService.cs`; `tests`.
- Steps: STEAMHOST-V-001 and CONFIG-V-002 (same defect): ThemeLoader.Load treats any recovery failure as the existing 'Theme update recovery remains pending' load error by also catching InvalidDataException (no catch-all); wrap the inner Recover call in Unpack's catch; drop the 256-name journal cap and keep the 128 KiB byte bound (CONFIG-038, STEAMHOST-031 journal part); journal on a source-generated context. STEAMHOST-V-002: both StartWorkAsync bodies clear _busy in finally and report any exception text as the slot error. Tests: an over-limit or garbage journal leaves Load usable with one load error and session start survives; a journal with more than 256 names round-trips; an unexpected exception in a work slot leaves the page usable.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Themes|FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService"`.
- Resolves: STEAMHOST-V-001, STEAMHOST-V-002, CONFIG-V-002, CONFIG-038.

#### B019 Device Lab: a synchronous Continue no longer loses the running stage

- **Status: implemented.** All 205 required GUI/wizard cases passed using isolated fixtures after formatting. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. Result-page Continue preserves the next stage's operation and cannot advance beside an active restoration attempt. No hardware stages were run; manual acceptance remains open.
- Domain: lab. Depends on: none.
- Files: `src/WSGM.DeviceLab/Gui/WizardWindow.cs`; `src/WSGM.DeviceLab/Gui/WizardWindow.Motion.cs`; `src/WSGM.DeviceLab/Gui/WizardWindow.Rumble.cs`; `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs`; `tests/WSGM.DeviceLab.Tests`.
- Steps: LABUI-V-001 (_plan/refactor-2.1/review/labui.verify.md): the Motion and Rumble Continue buttons call Next directly like Power, and every result-page Continue checks _operation.IsCompleted before Next, so the next stage's operation is tracked; Power's 'Continue anyway' cannot start Sleep beside a running 'Try restoring again'. Test: after a stage completes and Continue is pressed, _operation is not completed until the next stage ends; close during that stage waits for its finally.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Gui|FullyQualifiedName~Wizard"`.
- Resolves: LABUI-V-001.

#### B020 Device Lab review confirms real TDP, lighting and fan evidence

- **Status: implemented.** All 18 targeted review cases passed using exported fixtures after formatting, including absent restoration and mixed incomplete/completed runs. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. Matched-write evidence uses `LabPowerSummary.IsPass`; mismatch stays Unresolved, processor-power observations have no default promotion, lighting accepts `matched`, fan evidence uses `fan`, and the GUI rumble method text derives from the offered pulse lengths. No new evidence fields or live hardware actions were added; manual acceptance remains open.
- Domain: lab. Depends on: B019.
- Files: `src/WSGM.DeviceLab/Wizard/LabReview.Power.cs`; `src/WSGM.DeviceLab/Wizard/LabReview.Rumble.cs`; `src/WSGM.DeviceLab/Gui/WizardWindow.Power.cs`; `tests/WSGM.DeviceLab.Tests/Wizard/LabReviewTests.cs`.
- Steps: The small fix version of LABUI-B1 (_plan/refactor-2.1/review/labui.verify.md batch problems 1-3): review uses LabPowerSummary.IsPass (LABUI-002); readback-mismatch stays Unresolved, never Disagrees; processor-power runs over ryzen-smu/kx are reported Observed and never promoted by default. Lighting treats seen == 'matched' as a pass like LabPowerSummary.Lighting (LABUI-003). The MSI fan test writes Feature = 'fan' (LABUI-V-002). Derive the rumble method text from RumblePulseLengths (LABUI-021). Keep the rumble 'report' read (LABUI-005 is refuted). No typed-evidence refactor, no new fields.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabReviewTests"`.
- Resolves: LABUI-002, LABUI-003, LABUI-021, LABUI-V-002.

#### B021 Claw command truthfulness, timeout classification and watchdog

- **Status: implemented.** All 137 targeted capability, plugin and model lifecycle cases passed with fake transports. Full Release solution compilation had zero warnings/errors; Rider cleanup and guidance checks passed. Transport timeouts stay unknown or AppliedUnverified on optional reads, required pre-write reads and journal failures reject without writes, budget refusal leaves no restore entry, and failed pair/watchdog writes disarm automatic resend. Commands no longer run the watchdog, undeclared scenarios publish null, and dead rollback/descriptor code is removed. No hardware run; manual acceptance and B179 remain open.
- Domain: packages. Depends on: none.
- Files: `src/WSGM.Device.Msi.Claw/ClawCapabilities.cs`; `src/WSGM.Device.Msi.Claw/ClawServices.cs`; `src/WSGM.Device.Msi.Claw/ClawServiceBase.cs`; `src/WSGM.Device.Msi.Claw/ClawPlugin.Commands.cs`; `src/WSGM.Device.Msi.Claw/ClawPlugin.Observation.cs`; `src/WSGM.Device.Msi.Claw/ClawPlugin.Surface.cs`; `src/WSGM.Device.Msi.Claw/ClawHardware.cs`; `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs`; `tests/WSGM.Device.Msi.Claw.Tests`.
- Steps: PACKAGES-B1 plus the misses from _plan/refactor-2.1/review/packages.verify.md. PACKAGES-V-001: every read catch rethrows an OperationCanceledException only when the caller's token is cancelled (when ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested) at the nine cited sites plus TryGetAsync; a WMI/MCU timeout leaves the value unknown and never faults acquire or turns an accepted write into Indeterminate. Add a fake transport timeout to the Claw fakes. PACKAGES-002: failed apply or reassert disarms _target; an explicit command re-arms. PACKAGES-003: delete the pre-dispatch refresh; the watchdog runs only from the periodic pass. PACKAGES-004/016: budget refusal and pre-write read failures are Rejected (each read site decides, not a blanket handler mapping); Indeterminate only after the first setter, with NotRequired; delete the dead rollback-fault branch. PACKAGES-V-006: a journal refusal before the first write is Rejected (TransportFaulted). PACKAGES-022: undeclared scenario value publishes null. PACKAGES-023: WMI timeout is TransportFaulted, not Quiescing. Delete BooleanDescriptor (PACKAGES-031 Claw part). A Claw-local typed budget exception is accepted churn until the SDK write-budget helper lands.
- Tests: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawCapabilitiesTests|FullyQualifiedName~ClawPluginTests"`.
- Resolves: PACKAGES-002, PACKAGES-003, PACKAGES-004, PACKAGES-016, PACKAGES-022, PACKAGES-023, PACKAGES-V-001, PACKAGES-V-006.

#### B022 Ally write-through restore in HC order and published values

- **Status: implemented.** All 66 targeted capability/plugin cases passed after formatting. Full Release solution compilation had zero warnings/errors; Rider cleanup, Prettier and guidance checks passed. Power writes and restores follow HC's SPL/SPPT/FPPT order without readbacks or equal-value skips; mode writes always settle before limits. Written power, charge and encoded fan values win over conflicting observations. Successful restores clear entries; failed power/fan/controller restores remain Pending without failed/unverified status writes. Command preparation failures reject before setters; subsequent cancellation or unexpected failure is Indeterminate with NotRequired and leaves the service Owned. Aura caches only a found collection; controller publishing returns the host ValueTask directly, the redundant haptic lock and unused OEM Clear are removed. Additional consumers are `AllyControllerService.cs` and `RogAllyPlugin.Recovery.cs`; README and the approved D4 guide changes match behavior. Legacy journal migration remains B143. No hardware run; manual acceptance and B179 remain open.
- Domain: packages. Depends on: B010. Decisions: D4, D8, D9.
- Files: `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs`; `src/WSGM.Device.Asus.RogAlly/AllyServices.cs`; `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Commands.cs`; `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Surface.cs`; `src/WSGM.Device.Asus.RogAlly/AllyInput.cs`; `src/WSGM.Device.Asus.RogAlly/AllyHid.cs`; src/WSGM.Device.Asus.RogAlly/AGENTS.md (diff shown with the batch and applied, D4); `tests/WSGM.Device.Asus.RogAlly.Tests`.
- Steps: PACKAGES-B3 under D8 and D9 (findings/packages.md PACKAGES-005, -007, -V-003; findings/sdk.md SDK-V-002): a write either dispatched (published as written) or failed to dispatch, and nothing on the Ally reads back. PACKAGES-005 / SDK-V-002: a restore is a sequence of writes with no read before or after: RestoreAsync writes the mode unconditionally and waits ModeSettle, then writes SPL, then SPPT and FPPT in HC's order with today's WriteSpacing; RestoreModeAsync and its readback comparison go; the fan restore writes the channels with no readback. Release: a completed restore removes the entry and gives Idle; the RestoredUnverified branch and its ReleasedUnverified 'did not read back' reason go; any failure leaves the entry Pending with no status write, is traced, and the service reports Faulted with TransportFaulted as today; the next start writes that original once. PACKAGES-007 (D8): WriteLimitsAsync writes SPL, then SPPT, then FPPT unconditionally; WriteOrder, Current(...), the readback-equal skip and the SPL <= SPPT <= FPPT stepping rule are deleted, and the scenario mode is always written. PACKAGES-V-003: the written value wins for the cycle wherever the read differs (Effective() becomes the written value before the read; the fan curve publishes WrittenCpu first; the charge limit publishes the written value first), as Claw's Observe does. PACKAGES-V-004: a cancelled or non-IO failure after the journal is armed returns Indeterminate with NotRequired and never faults the service. PACKAGES-V-005: cache only a found Aura collection. PACKAGES-012 publish fast path; delete _hapticGate (020) and AllyOemButtonState.Clear (031). The two Ally tests that relied on a readback mismatch (IgnoreWritesTo) are deleted and replaced by one on FailWritesTo: a restore that fails to dispatch leaves the entry Pending with no status write and the next start writes it once (PACKAGES-037 Ally part). No Ally path records RestoredUnverified or RestoreFailed. The Ally AGENTS power line becomes 'SPL, then SPPT and FPPT, as HC writes them' in a diff shown with the batch and applied (D4). Tests also cover: every limit write goes SPL, SPPT, FPPT whatever the current limits; re-sending the last written limits writes all three; a restore issues no ATKACPI status read.
- Tests: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~AcpiCapabilityTests|FullyQualifiedName~PluginTests"`.
- Resolves: PACKAGES-005, PACKAGES-007, PACKAGES-012, PACKAGES-020, PACKAGES-031, PACKAGES-V-003, PACKAGES-V-004, PACKAGES-V-005, SDK-V-002.

#### B023 Ally fan rollback removal

- **Status: implemented with B022.** The fan rollback block and its pre-write snapshot read are removed, as is the command-fault branch. Failure or cancellation after a setter performs no further fan write and returns Indeterminate with NotRequired; the service remains Owned with its Pending recovery entry. The approved D4 guide change is applied. Validation is covered by B022's 66 targeted cases and warning-free Release solution build; manual hardware acceptance remains open.
- Domain: packages. Depends on: B022. Decisions: D4, D9.
- Files: `src/WSGM.Device.Asus.RogAlly/AllyAcpiCapabilities.cs`; `src/WSGM.Device.Asus.RogAlly/RogAllyPlugin.Commands.cs`; src/WSGM.Device.Asus.RogAlly/AGENTS.md (diff shown with the batch and applied, D4); `tests/WSGM.Device.Asus.RogAlly.Tests`.
- Steps: PACKAGES-006: delete the automatic fan-curve rollback write after a failed write and the before read that only feeds it; a failed curve write returns Indeterminate (TransportFaulted, NotRequired) and writes nothing else; delete the RestoreFailed fault branch in ExecuteBoundCommandAsync, whose last producer this was (PACKAGES-016 Ally part). The Ally AGENTS rule that a journalled original's failed rollback faults the service changes in a diff shown with the batch and applied (D4). Motion stays as it is: WinRT first with the legacy sensor fields as fallback, as HC does (D7, PACKAGES-011 needs no change). Test: a failed curve write performs no further ATKACPI write and leaves the fan service Owned.
- Tests: `dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj`.
- Resolves: PACKAGES-006.

### Phase B: install and launch

#### B024 Read-only closure of the unwritten install and U04B finding bodies

- Status: in progress. [install-closure.md](install-closure.md) records the full Core startup source pass, a new exact-filename matching defect (INSTALL-C-002), the omitted UNCOVERED-002 restore fix, the unused EnableLua disposition and each of the 29 retired install ids. B180 has verified inputs and may proceed independently. The five-project pass and remaining individual U04B dispositions are incomplete; B024 is not closed. No build, test or live action ran for this read-only review.
- Domain: install. Depends on: none.
- Files: (read-only) src/WSGM.Install/**, src/WSGM.Launch/**, src/WSGM.LogonService/**, src/WSGM.PackagedLaunch/**, src/WSGM.Setup/**, src/WSGM/Core/SteamAutostart*.cs, KnownStartupApps.cs, WindowsPolicyOperation.cs, DesktopAppProcessBackend.cs; writes _plan/refactor-2.1/install-closure.md.
- Steps: install.md stops inside INSTALL-010; the bodies of INSTALL-004, 011-014, 018-019, 021-025, 028-031 and 034-046 were never written, and ledger U04B-LFA-013..049 bodies are missing. Re-review the five projects and the U04B files listed by the critic (section 1.3) against the current head, write one disposition per id (defect with file:line, or no-change with reason), and append any required fix as a new batch placed before B030 using the same format as this plan. Apply the simplify and no-arbitrary-limits rules; do not re-open the decided items (INSTALL-005 refusal, INSTALL-007 stop flag, schtasks with the task XML where it is written today), and record any security-only item as no-change: dropped by maintainer decision (security theater, DECISIONS.md). No source edits in this batch.
- Tests: `none (read-only)`.
- Resolves: INSTALL-004, INSTALL-011, INSTALL-012, INSTALL-013, INSTALL-014, INSTALL-018, INSTALL-019, INSTALL-021, INSTALL-022, INSTALL-023, INSTALL-024, INSTALL-025, INSTALL-028, INSTALL-029, INSTALL-030, INSTALL-031, INSTALL-034, INSTALL-035, INSTALL-036, INSTALL-037, INSTALL-038, INSTALL-039, INSTALL-040, INSTALL-041, INSTALL-042, INSTALL-043, INSTALL-044, INSTALL-045, INSTALL-046.

#### B025 Logon service stops cleanly and gets one token seam

- **Status: implemented.** All 23 focused logon-service cases passed after Rider cleanup; the full Release solution build had zero warnings/errors. Prettier, guidance and diff checks passed. One instance stop flag is checked under the same lock as process creation and session registration; SCM reports Stopped only after closing launch admission. `ISessionHost.cs` is the single seam: `WindowsSessionHost.cs` owns the extracted token, profile, process, session, desktop-probe and diagnostic operations, while `SessionLauncher.cs` retains dedup, stop, token-choice and watchdog decisions. All seven extracted Windows helper bodies match the baseline apart from whitespace and probe argument names. Tests reference the service assembly through an alias instead of linking sources with conflicting implicit imports; `Properties/AssemblyInfo.cs` grants test visibility and the test csproj records that reference. Elevated launches retain the manifest ExePath and PublishSingleFile is unchanged. No live service, setup or logon operation ran; attended setup M01-39 and B179 remain open. B024's source closure remains separate and incomplete.
- Domain: install. Depends on: B007.
- Files: `src/WSGM.LogonService/SessionLauncher.cs`; `src/WSGM.LogonService/ServiceHost.cs`; `tests/WSGM.Tests/LogonService`.
- Steps: Functional part only. INSTALL-007 / A02-F019: one stop flag checked under Gate before TryLaunch; no dispatch owner, no watchdog join. INSTALL-V-006: one ISessionHost seam for token selection, dedup and stop decisions, with tests for the stop flag and the token choice. The elevated branch keeps launching boot.json's ExePath, BootManifest.cs is not edited and the service keeps PublishSingleFile (INSTALL-001 and INSTALL-V-001 are dropped by maintainer decision, security theater). dev-deploy skips the service, so the change needs an attended setup run (M01-39).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LogonService"`.
- Resolves: INSTALL-007, INSTALL-V-006, A02-F019.

#### B026 Removed by maintainer decision

- **Status: no change.** `DECISIONS.md` explicitly drops INSTALL-002 and B026; staging behavior is retained. No source edit or validation is required for this removed batch.
- Domain: install. Depends on: none.
- Files: none.
- Steps: Removed by maintainer decision (security theater, DECISIONS.md): elevated setup keeps running its payloads from where it extracts them today (INSTALL-002 is no-change). The id stays so references do not dangle; nothing depends on it.
- Tests: none.
- Resolves: none.

#### B027 Updater download leaves no partial file

- **Status: implemented.** All 21 focused updater/BoundedHttp cases passed after formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. Setup, hash and release metadata bodies use BoundedHttp's read-stall timeout; the updater's metadata/setup caps and its bundle-fetch cap are removed, retaining only stream representation ceilings. The shared helper reports copied bytes after each write and stops the read timer while writing the destination. DownloadAsync removes `.partial` through FileCleanup in finally, preserves an existing setup on failure, and publishes the replacement only after SHA-256 verification. Tests cover stalled setup/hash bodies, mismatch, caller cancellation, complete-file publication, known-length progress, former cap refusal and a slow destination. The download location and DACL stay as decided; BundleManifest's own parser cap remains B031. No real download or setup execution ran; manual acceptance and B179 remain open.
- Domain: install. Depends on: B025.
- Files: `src/WSGM/Core/UpdateChecker.cs`; `src/WSGM/Core/BoundedHttp.cs`; `tests/WSGM.Tests/Core/UpdateCheckerTests.cs`.
- Steps: Functional part only (findings/ledger-u04.md U04A-LFA-010 and U04A-LFA-011). The download stays in %ProgramData%\WSGM\Updates, because moving it fixes no functional bug, and %ProgramData%\WSGM keeps its DACL (INSTALL-003 is dropped by maintainer decision, security theater). DownloadAsync deletes its .partial file in finally when the copy or the hash check fails; the setup and hash reads go through BoundedHttp with a stall bound and without the metadata and setup byte caps, which are not on D2's list; the SHA-256 check stays the integrity gate. Tests through an internal overload with a fake handler and a temp download directory: a stalled body ends with IOException and leaves no .partial, a hash mismatch leaves no .partial, a body with Content-Length reports progress up to 1.0.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~UpdateChecker|FullyQualifiedName~BoundedHttp"`.
- Resolves: U04A-LFA-010, U04A-LFA-011 (functional part; ledger ids, mapped in Appendix B).

#### B028 Setup applies answers last and reports partial change truthfully

- **Status: source implemented.** All 64 focused setup/answer cases passed after formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. Service registration stays in its current order but is non-fatal; rollback stops the service before restoring files, retains its transaction on stop refusal and publishes RollbackIncomplete for truthful failure text. A run that reached the profile step no longer claims nothing changed. Reapplied collapsed gesture answers preserve individual switches; actual changes update all three. Takeover consent transitions are captured from the freshly loaded configuration inside the mutation lock; unchanged consent does not repeat manager writes. Answers supplied with unreadable config return failure, and export reads strictly without saving or quarantining config. No live setup, service, Steam or hardware action ran. Proof gaps remain explicit: takeover/corrupt-config/summary paths need attended acceptance; rollback ordering and non-fatal registration plan fixtures wait for B030's temporary paths. B068 retains the no-write export migration constraint; B179 remains open.
- Domain: install. Depends on: B027.
- Files: `src/WSGM.Setup/Engine/SetupEngine.cs`; `src/WSGM.Setup/UI/SetupViewModel.cs`; src/WSGM/Program.cs (RunSetup, ExportSetupAnswers); `src/WSGM/Core/SetupAnswers.cs`; `src/WSGM.Setup/UI/Pages/ProfilePage.cs`; `tests`.
- Steps: INSTALL-006 (corrected by findings/install.md): keep registration after the profile, but make it non-fatal so a registration failure uses the existing success-with-problem summary. Before rollback restores files, stop the service; preserve the transaction and report repair if stopping or file restoration fails. A failed run after the profile step starts reports that settings may have changed. INSTALL-V-005: RunSetup returns non-zero when answers were supplied but config could not be loaded; export reads strictly without quarantining or saving. INSTALL-020: preserve the individual gesture switches when the collapsed answer is unchanged; an explicit change sets all three (no edit mask or schema change). INSTALL-V-004: Steam autostart takeover and other-manager disable run only on false-to-true consent transitions captured from the fresh configuration under the mutation lock. Binding constraint for B068: --export-setup-answers migrates in memory only and never writes.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup|FullyQualifiedName~SetupAnswers"`.
- Resolves: INSTALL-006, INSTALL-020, INSTALL-V-004, INSTALL-V-005.

#### B029 De-elevation task deleted once after its dispatch budget

- **Status: implemented.** All 315 selected launch/de-elevation cases passed after formatting; the full Release solution build had zero warnings/errors. Rider cleanup, Prettier, guidance and diff checks passed. A potentially created task receives exactly one cleanup attempt, using the shared deadline/token while open or a separate five-second deadline and CancellationToken.None after closure/cancellation. Cancellation during unobservable creation also cleans up; cleanup failure cannot replace the dispatch outcome or caller cancellation. Tests assert cleanup deadlines/tokens outside the caught callback. Task XML locations and schtasks are unchanged; no persistent cleanup record or new cleanup disposition is added. INSTALL-009 is no-change: WSGM.Launch already deletes after pipe connection and clears the task name on success, with its existing final cleanup and command timeout. No live Task Scheduler action ran; attended acceptance and B179 remain open.
- Domain: install. Depends on: B027.
- Files: `src/WSGM/Core/UnelevatedLauncher.cs`; `src/WSGM.Launch/ScheduledTaskLauncher.cs`; tests/WSGM.Tests (UnelevatedLauncher, Launch).
- Steps: Functional part only. Keep schtasks, the existing shared-deadline contract and the task XML where both elevated callers write it today (INSTALL-008 and U04B-LFA-002 are dropped by maintainer decision, security theater). U04B-LFA-012 / plan C12: after the dispatch budget closes, one /Delete attempt with its own short timeout; no persistent cleanup record and no Unknown disposition state. INSTALL-009: no record for WSGM.Launch either.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~UnelevatedLauncher|FullyQualifiedName~Launch"`.
- Resolves: INSTALL-009, U04B-LFA-012.

#### B180 Core startup closure fixes from B024

- Domain: install. Depends on: none; verified Core inputs are in the partial B024 closure.
- Files: `src/WSGM/Core/SteamAutostart.cs`; `src/WSGM/Core/SteamAutostartTakeover.cs`; `src/WSGM/Core/UacSettings.cs`; `tests/WSGM.Tests/Core/SteamAutostartTests.cs`.
- Steps: INSTALL-C-002: require the executable filename to equal steam.exe before the existing known-path comparison; test suffix-only names on Run, shortcut and task surfaces with an unknown Steam path. UNCOVERED-002, omitted from B016: restore scheduled tasks with one SetTaskEnabled(true) write and no pre-read; accepted dispatch drops the record, refusal keeps it. Test unreadable/throwing state queries and refused writes with fakes. U04B-LFA-041: delete the unused EnableLua read, constructor argument and property, adjusting all three local constructor calls without changing PromptsDisabled. No retry, new state, persistent record, security hardening or live startup/UAC action.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamAutostart"`; warning-free Release solution build for the UAC declaration deletion.
- Resolves: INSTALL-C-002, UNCOVERED-002, U04B-LFA-041.

#### B030 Setup identity refusal, exact component match and testable paths

- Domain: install. Depends on: B024, B028, B180.
- Files: `src/WSGM.Setup/Engine/SetupEngine.cs`; `src/WSGM.Setup/Engine/Registration.cs`; `src/WSGM.Setup/Engine/SetupPayload.cs`; `src/WSGM.Setup/QuietSetup.cs`; `tests`.
- Steps: INSTALL-005 (replaced remedy): in Detect, compare the session's interactive user (WTSQuerySessionInformation user and domain) with the process user and, when they differ, show the existing actionable refusal before modifying the machine; no TargetUser plumbing. INSTALL-015: match the exact uninstall key or the DisplayName prefix the pinned installers write, not a substring (usbipd-win must not match). INSTALL-017: SetupEngine takes root and machine-data paths like SetupFileTransaction; InstallLayout stays static. INSTALL-016: one choice-policy helper shared by the UI and quiet paths. INSTALL-047: exact package-id prefix match. INSTALL-V-007: containment check appends a directory separator. Plus any fix appended by B024.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup"`.
- B028 validation dependency: after the temporary path seams land, add rollback fixtures proving StopService precedes file restore and a refused stop retains the transaction with RollbackIncomplete. Exercise the corrected non-fatal service-registration plan over a temporary payload. Until then, B028's rollback ordering and setup UI/corrupt-config paths have source evidence and compilation only.
- Resolves: INSTALL-005, INSTALL-015, INSTALL-016, INSTALL-017, INSTALL-047, INSTALL-V-007.

#### B031 Cross-process names in one linked file; native declaration cleanup

- Domain: install. Depends on: B030.
- Files: src/Shared/Process/SessionProtocolNames.cs (new, linked); `src/WSGM/Core/ExplorerShellAnchor.cs`; `src/WSGM/Core/UpdateExitWatcher.cs`; `src/WSGM/Shell/SessionActivation.cs`; `src/WSGM.Setup/Engine/WindowsSetup.cs`; src/WSGM/Shell/DeviceCoordinator.cs (DeviceOwner literal); `src/WSGM.DeviceLab/Preflight/WindowsPreflightInspection.cs`; `src/WSGM.PackagedLaunch/Interop/NativeMethods.cs`; `tests/WSGM.Tests/Setup/SetupShutdownContractTests.cs`.
- Steps: INSTALL-027 and SESSION-047: Local\WSGM.ShellAnchor.RecoverySettled, the shell mutex, Global\WSGM.DeviceOwner (four copies including Device Lab) and the exit event names move into one linked SessionProtocolNames.cs with unchanged values; SetupShutdownContractTests pins every one. The overlay broker keeps forwarding the game's access and disposition (INSTALL-010 is dropped by maintainer decision, security theater). INSTALL-026/033: delete redeclared P/Invokes in PackagedLaunch where Win32Common or its NativeMethods already declares them; SCM and job-object duplicates stay until linked-sources moves them.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Setup|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~Launch"`.
- Resolves: INSTALL-026, INSTALL-027, INSTALL-033, SESSION-047.

### Phase C: build foundations

#### B032 Gate and CI hygiene; asset builder writes nothing in check mode

- Domain: build. Depends on: none.
- Files: `eng/verify.ps1`; `eng/check-no-live-data-paths.ps1`; `tools/PerfLab/perf-capture.ps1`; `eng/build-steam-assets.mjs`; `.prettierignore`; `eng/wsgm-revision.targets`; tests/WSGM.Tests/Core/SteamInputShimTests.cs (comment); tests/WSGM.Tests/Settings/SettingsViewModelSplashTests.cs (comments); src/WSGM.DeviceLab/Capture/Live/LabWmiFirmwareEvents.cs (comment).
- Steps: BUILD-B1 trimmed (_plan/refactor-2.1/review/build.verify.md). verify.ps1 compares a before/after diff around jb cleanupcode so pre-existing edits and the release version stamp do not fail the gate (BUILD-002, BUILD-V-003). claude.yml keeps its action tags (BUILD-009 is dropped by maintainer decision, security theater). Add the PowerShell and %VAR% live-data patterns and reword the four comment lines they would match (BUILD-010). Fix stale comments (BUILD-032). No Dependabot edit (BUILD-004: frozen check runs in B179). In build-steam-assets.mjs feed Prettier on stdin with --stdin-filepath so --check writes nothing into the embedded-resource folder (BUILD-V-001), delete the 768 KiB maximumAssetBytes bound and keep non-empty/UTF-8/no-BOM/single-file checks (STEAMHOST-V-003, TOOLKITJS-037), delete the dead plugin SteamUiAssets discovery (BUILD-015).
- Tests: ./eng/check-no-live-data-paths.ps1; ./eng/check-agent-guidance.ps1; npm run steam-assets:check; PowerShell parse of changed scripts.
- Resolves: BUILD-002, BUILD-010, BUILD-015, BUILD-032, BUILD-V-001, BUILD-V-003, STEAMHOST-V-003, TOOLKITJS-037.

#### B033 Release payload truth: notices and lock-driven controller names

- Domain: build. Depends on: B032.
- Files: `build.ps1`; `eng/assert-component-staging.ps1`; `eng/assert-controller-pin.ps1`; `eng/build-steam-input-lease.ps1`; `eng/device-lab-publish.ps1`; eng/build-common.ps1 (new).
- Steps: BUILD-B2 corrected: add the two missing notice names to the payload list instead of globbing publish\App (VIIPER notices stay in Payload\Controller) (BUILD-005); read controller asset names from external/controller/controller-components.lock.json (BUILD-006); drop WSGM.DeviceHost.exe; stop staging steam-input-lease.exe and fix the comments (BUILD-011). Copy-RuntimeNotices in build-common.ps1 takes the project's obj\project.assets.json as a parameter. The setup build itself (build.ps1) is the maintainer's manual check.
- Tests: ./eng/assert-controller-pin.ps1; PowerShell parse.
- Resolves: BUILD-005, BUILD-006, BUILD-011.

#### B034 Project graph test replaces the single boundary edge

- Domain: build. Depends on: none.
- Files: tests/WSGM.Tests/Boundaries/ProjectGraphTests.cs (new); tests/WSGM.Tests/Boundaries/DeviceBoundaryTests.cs (replaced).
- Steps: BUILD-B5 corrected: ProjectGraphTests asserts today's project and linked-source matrix (linked sources at their current paths) so every later move updates one row. Only DeviceBoundaryTests is replaced; ContractBoundaryTests stays.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries"`.
- Resolves: BUILD-021.

#### B035 Plugin templates are real compiled files

- Domain: build. Depends on: none.
- Files: `eng/new-plugin.ps1`; eng/templates/CommonPlugin/Plugin.cs (new); eng/templates/GpuPlugin/Plugin.cs (new); `tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj`; `docs/plugin-system.md`; `.gitattributes`.
- Steps: BUILD-018 corrected: move the two here-string templates into files compiled by WSGM.Plugin.Sdk.Tests, restyled to repository layout (braces) so --warnaserror and dotnet format pass, with distinct namespaces (ExamplePlugin.Common, ExamplePlugin.Gpu); new-plugin.ps1 copies them and its generated entryType changes with the namespace; add an eol=crlf rule for eng/**/*.cs. Must land before the Device 12 / Plugin 4 batches.
- Tests: `dotnet build tests/WSGM.Plugin.Sdk.Tests/WSGM.Plugin.Sdk.Tests.csproj -c Release --warnaserror`.
- Resolves: BUILD-018.

#### B036 One pinned-download helper and a real export check

- Domain: build. Depends on: B033.
- Files: `eng/build-common.ps1`; `eng/acquire-controller-dependencies.ps1`; `eng/acquire-pawnio.ps1`; `eng/stage-webview2-runtime.ps1`; `eng/build-steam-input-lease.ps1`; `eng/build-uwp-bridge.ps1`.
- Steps: BUILD-B3: Get-PinnedAsset replaces three download-and-verify implementations (BUILD-008); Get-DllExports (dumpbin through vswhere/VsDevCmd) replaces the ASCII search in build-uwp-bridge.ps1 (BUILD-007, A02-F020).
- Tests: ./eng/assert-pawnio-pin.ps1; build-uwp-bridge.ps1 -Validate; build-steam-input-lease.ps1 -Validate.
- Resolves: BUILD-007, BUILD-008, A02-F020.

### Phase D: foundations (config, SDK, WDC, toolkit)

#### B037 UserDataContext and an instance ConfigStore, behaviour unchanged

- Domain: config. Depends on: none.
- Files: src/WSGM/Core/UserDataContext.cs (new); `src/WSGM/Core/ConfigStore.cs`; `src/WSGM/Core/Log.cs`; `src/WSGM/Program.cs`; `src/WSGM/App.axaml.cs`; all ConfigStore consumers and the 27 Log.Directory sites in 19 files (_plan/refactor-2.1/review/config.md section 4 list, config.verify.md CONFIG-011); `src/WSGM/Core/Library/ImportStateStore.cs`; `src/WSGM/Shell/ArtworkStateStore.cs`; `tests/WSGM.Tests/Core/ConfigurationTests.cs`.
- Steps: CONFIG-B2a first, before the rules rewrite (config.verify.md batch problem 10, critic conflict 26). UserDataContext is sealed record (Root, ConfigMutexName) with ForCurrentUser() only (critic conflict 17: no identity, no ForInteractiveUser). Program builds it once; the early restore-shell path builds it before Log and config (pure). ConfigStore becomes an instance over the context with today's semantics; thread it through constructors (static recovery services take it as a parameter). Log.Init(name, root); Log.Directory is deleted at every site. LIBRARY-B7 folds in: ImportStateStore and ArtworkStateStore take the root (file names unchanged). Tests use a temp root and Local\WSGM.Tests.Config.<guid>, which removes the production-mutex contention (U04A-LFA-022, BUILD-001 config half). Do not change mutex ACLs (CONFIG-V-004 waits for its attended check).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~BootManifest|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~SetupAnswers|FullyQualifiedName~ImportState|FullyQualifiedName~ArtworkState"`.
- Resolves: CONFIG-011, LIBRARY-014, BUILD-001.

#### B038 Pure config rules, generic enum repair and limit removal

- Domain: config. Depends on: B037.
- Files: `src/WSGM/Core/ConfigStore.cs`; src/WSGM/Core/AppConfigRules.cs (new); src/WSGM/Core/ConfigRepair.cs (new); src/WSGM/Core/ConfigJson.cs (new); src/WSGM/Core/ConfigJsonContext.cs (moved); `src/WSGM/Core/AppConfig.cs`; `src/WSGM/Core/DeviceConfiguration.cs`; `src/WSGM/Core/Profiles/*`; `src/WSGM/Core/Library/GameLibraryConfig.cs`; `src/WSGM/Core/GameModeLaunchConfiguration.cs`; `src/WSGM/Core/LibraryFilter.cs`; `src/WSGM/Core/DeviceProfileValidation.cs`; src/WSGM/Shell/DeviceCapabilityRouter.cs (curve limit); `src/WSGM/Core/SplashTheme.cs`; `src/WSGM/Settings/DeviceProfileRowViewModel.cs`; `tests`.
- Steps: CONFIG-B1 with _plan/refactor-2.1/review/config.verify.md batch problems 1-3. Register a tolerant enum converter on ConfigJsonContext and a metadata walker with explicit templates (FilterNode, Splash, nullable enums repair to null); the walker accepts a [Flags] value whose bits are all defined (LaunchWrapperMode fixture). Byte-identical round-trip fixtures for a cached PluginSettingsManifest and a profile CapabilityValue prove SDK enum wire format is unchanged. Cover the enums the hand list missed (DevicePowerCustomValues.WindowsMode, GameLibraryConfig DefaultMode and ArtworkPreference). Delete the hand repair list and Definite calls. Normalizers move beside their section types and return diagnostics. Drop step 7 (no new Corrupt outcome for undefined recovery-enum numbers; keep the use-time bounds). Remove load-time length caps and truncation: profile names (80), preset-id and scenario lengths, authored profile names (48, and DeviceAuthoredProfile.MaxNameLength in the row view model), DisabledSources (32), the 64-point curve cap in DeviceProfileValidation and the router's matching limit (descriptor bounds only), and the spatial-audio allow-list. Stored layouts that fail Describe are kept, not deleted (WDC-002 latent). ConfigJson.Clone replaces CloneJson and ProfileFields.Copy.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~ConfigRepair|FullyQualifiedName~Splash|FullyQualifiedName~ProfileEdits|FullyQualifiedName~ProfileResolver|FullyQualifiedName~PluginSettingsDeclarationCache|FullyQualifiedName~DevicePowerAssignments|FullyQualifiedName~DeviceProfile"`.
- Resolves: CONFIG-006, CONFIG-007, CONFIG-010, CONFIG-013, CONFIG-014, CONFIG-015, CONFIG-016, CONFIG-021, CONFIG-035, CONFIG-041, DEVICE-013, LIBRARY-017, SETTINGS-V-007, CRIT-005, WDC-002.

#### B039 Read outcomes, one writer transaction, durable writes, sidecar rules

- Domain: config. Depends on: B038.
- Files: `src/WSGM/Core/ConfigStore.cs`; `src/WSGM/Core/AtomicFile.cs`; `src/WSGM/Core/BootManifest.cs`; `src/WSGM/Core/BootManifestWriter.cs`; `src/WSGM/Shell/ShellSession{,.Config,.Modes}.cs`; `src/WSGM/Settings/SettingsViewModel.Save.cs`; `src/WSGM/Shell/CommonPluginConfiguration.cs`; `src/WSGM/Core/Installer.cs`; `src/WSGM/Core/OtherManagers.cs`; `src/WSGM/Core/SteamAutostartService.cs`; `src/WSGM/Core/ShellRegistration.cs`; `src/WSGM/Program.cs`; `src/WSGM/Shell/ArtworkStateStore.cs`; `src/WSGM/Shell/QuickAccessFolds.cs`; `src/WSGM/Core/Library/ImportStateStore.cs`; `src/WSGM/Core/UpdateChecker.cs`; `src/WSGM/Shell/ThemeService.cs`; `src/WSGM/Shell/AnimationService.cs`; `src/WSGM/Shell/SoundPackService.cs`; `src/WSGM/Shell/PluginSettingsCoordinator.cs`; `tests`.
- Steps: CONFIG-B2b with _plan/refactor-2.1/review/config.verify.md corrections. Read returns Loaded, Absent, Corrupt or Unreadable; only Loaded and Absent carry a config. A file written by a newer WSGM, or one with unknown recovery enums, loads best effort as Loaded, as today; there is no UnsupportedSchema outcome and no read-only mode (maintainer decision). Keep the reader-side mutex; delete only the nesting (depth counter, out-of-order dispose rules, degraded nesting check): one writer Transaction scope replaces AcquireLock + LoadForMutation + Save + Mutate. Update(Func<AppConfig,bool>) saves only on true (CONFIG-001 store half, CONFIG-044 write on change). One strict-path failure type, ConfigUnavailableException (parse, unreadable, mutex timeout), and fix the catch filters in ThemeService, AnimationService and SoundPackService (CONFIG-V-001). Durable flush for config.json and boot.json. Quarantine once per distinct content, delete the keep-5 prune. Boot manifest is projected only from a Loaded or Absent read (CONFIG-003). Recovery callers are split: fail-open (shell unregistration, restore-shell boot disarm, DisarmCrashLoop) keep today's fallback; fail-closed (Steam autostart, other managers, UAC, lock-on-wake, display-scale restore) report failure on Unreadable. Sidecars: artwork.json and library-import.json refuse writes after an unreadable read and set a parse-corrupt file aside before replacing it (LIBRARY-013, copying ImportStateStore's rules, null-tolerant shape checks for CONFIG-V-005); quick-access-folds.json simply does not write after a failed read. BootManifestWriter.WriteSignInDisabled stops mutating its argument (U04A-LFA-031); BootManifestTests use the production projection (U04A-LFA-008). No .bak, no repair UI, no injected file backend.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Configuration|FullyQualifiedName~StateFile|FullyQualifiedName~BootManifest|FullyQualifiedName~ArtworkState|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~ImportState|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~OtherManagers|FullyQualifiedName~SteamAutostart|FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService"`.
- Resolves: CONFIG-001, CONFIG-003, CONFIG-004, CONFIG-005, CONFIG-008, CONFIG-009, CONFIG-012, CONFIG-036, CONFIG-037, CONFIG-039, CONFIG-040, CONFIG-044, CONFIG-V-001, CONFIG-V-005, LIBRARY-013, SESSION-017.

#### B040 Profile service ownership and fan-out admission close

- Domain: config. Depends on: B039.
- Files: `src/WSGM/Shell/ProfileService.cs`; `src/WSGM/Shell/ProfileFanOut.cs`; src/WSGM/Shell/ConfigProfileStore.cs (new); src/WSGM/Shell/InMemoryProfileStore.cs (new); `src/WSGM/Shell/ShellSession.Config.cs`; `src/WSGM/Core/Profiles/ProfileResolver.cs`; `tests`.
- Steps: CONFIG-B4: ReloadAsync serialized on _writeGate and re-reading through the port (CONFIG-002); Close() (synchronous: unsubscribe, cancel the active pass, refuse queueing) and Completion on ProfileService and ProfileFanOut (CONFIG-018/019); learning writes use the service cancellation; the MutateProfilesAsync adapter becomes ConfigProfileStore and honours the edit's bool (CONFIG-001 adapter half, owned only here); one InMemoryProfileStore shared by overlay-test and tests (CONFIG-023); cached snapshot lookup (CONFIG-043); change detection outside the lock where cheap (CONFIG-020); fan-out tests use gates, not Task.Delay (CONFIG-045); fix the ProfileFanOut constructor doc (CONFIG-V-007).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ProfileService|FullyQualifiedName~ProfileFanOut|FullyQualifiedName~ProfileEdits|FullyQualifiedName~SettingsSaveMerge|FullyQualifiedName~PerformanceService"`.
- Resolves: CONFIG-002, CONFIG-018, CONFIG-019, CONFIG-020, CONFIG-023, CONFIG-043, CONFIG-045, CONFIG-V-007.

#### B041 Logging: rotation outside the lock, verbose survives reload

- Domain: config. Depends on: B039.
- Files: `src/WSGM/Core/Log.cs`; src/WSGM/Overlay/OverlayController.cs (418-421); `src/WSGM/Program.cs`; `src/WSGM/Shell/ShellSession.Config.cs`; tests/WSGM.Tests/Core/LogLevelTests.cs -> LogFileTests.cs.
- Steps: CONFIG-B6 corrected: rotation runs outside Gate with a zero-wait try (CONFIG-031); an internal LogFile instance holds path, rotation and append so tests drive it on a temp path; keep Log.Observe with its logging intact (CONFIG-032 Observe half refuted), inline only SetMinimumLevel; the effective verbosity (flag || config) is applied by the reload path, not OverlayController (CONFIG-030). Tests: Change suppression and held counts, rotation does not block a concurrent append, verbose flag survives a reload.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Log"`.
- Resolves: CONFIG-030, CONFIG-031, CONFIG-032, CONFIG-034.

#### B042 Desktop return recovery as an instance; audio profile tests

- Domain: config. Depends on: B039.
- Files: src/WSGM/Shell/GameModeReturnRecovery.cs -> DesktopReturnRecovery.cs; `src/WSGM/Shell/AudioProfileService.cs`; `src/WSGM/Shell/ShellSession{,.Modes,.Shutdown}.cs`; `src/WSGM/Program.cs`; `src/WSGM/Core/GameModeLaunchConfiguration.cs`; `src/WSGM/Shell/GameModeEntryTransaction.cs`; `tests`.
- Steps: CONFIG-B5 with config.verify.md batch problem 6: instance DesktopReturnRecovery(store, applyLayout, audio); RestoreAsync never clears; ClearIfUnchanged(fingerprint) is called explicitly at every site that relied on implicit clearing: the Game Mode entry path (GameModeEntryTransaction.cs:143, including a non-Custom launch without GameAudio), restore-shell and panic. Source-generated fingerprint; GameModeLaunchRecoveryRules.DesktopAudio shared with the modes code; AudioProfileService constructed explicitly; one capability read per apply (CONFIG-026). Tests: layout success plus audio failure keeps the record; clear only when unchanged; cancellation stops waiting without a second clear; entry path clears once; AudioProfileService endpoint wait, unselected-playback refusal, unsupported format (CONFIG-025). CONFIG-027 is refuted and dropped.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DesktopReturnRecovery|FullyQualifiedName~AudioProfileService|FullyQualifiedName~GameModeEntryTransaction"`.
- Resolves: CONFIG-024, CONFIG-025, CONFIG-026, SESSION-027.

#### B043 Active clock dispatches cancellations safely

- Domain: sdk. Depends on: none.
- Files: `src/WSGM.Device.Sdk/Lifecycle/ActiveClock.cs`; `src/WSGM.Device.Sdk/WSGM.Device.Sdk.csproj`; `tests/WSGM.Device.Sdk.Tests/Lifecycle/*`.
- Steps: SDK-B2 minus pruning (_plan/refactor-2.1/review/sdk.verify.md): an internal ActiveClockCore(Func<long> timestamp) holds Advance, Pending and CollectDue; the static facade keeps one core and the thread (no owned clock service, no clock-bearing Deadline, no shutdown join); due sources cancel with CancelAsync and their faults are observed, so plugin continuations never run on the clock thread. Tick, MaximumStep and CountedStep unchanged. Delete the constant-ratio test. A02-F004 stays an accepted bounded no-change.
- Tests: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Lifecycle"`.
- Resolves: SDK-001, A02-F003.

#### B044 SDK native callbacks and poll threads contain exceptions

- Domain: sdk. Depends on: none.
- Files: `src/WSGM.Device.Sdk/Windows/LowLevelKeyboardHook.cs`; `src/WSGM.Device.Sdk/Windows/LegacyMotionStream.cs`; `src/WSGM.Device.Sdk/Windows/LegacyMotionSensors.Events.cs`; `src/WSGM.Device.Sdk/Windows/DeviceReconnect.cs`; `src/WSGM.Device.Sdk/Input/MotionSampleBuilder.cs`; `src/WSGM.Device.Sdk/Plugin/PluginTrace.cs`; `tests`.
- Steps: SDK-B3 without the PeekMessage step (the stop race is refuted): guard the hook callback (SDK-005) and the motion poll and event delivery (SDK-006); DeviceReconnect.Start disposes the previous source (SDK-036); trace outside the motion lock and never per sample (SDK-037); volatile trace sink and Failure through DiagnosticText (SDK-021); no per-report RCW in the event path where it is a simple hoist (SDK-038).
- Tests: `dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Windows|FullyQualifiedName~Input|FullyQualifiedName~PluginTrace"`.
- Resolves: SDK-005, SDK-006, SDK-021, SDK-036, SDK-037, SDK-038.

#### B045 A02_01 replacement: bounded common manifest read

- Domain: sdk. Depends on: none.
- Files: `src/WSGM.Plugin.Sdk/PluginManifestReader.cs`; `tests/WSGM.Plugin.Sdk.Tests/ManifestTests.cs`; `src/WSGM/Core/PluginPackageFile.cs`.
- Steps: SDK-B4 as the replacement for batches/A02_01.md: steps 1, 2 and 4 (ReadContext with ManifestLimits.MaxDepth, reject length > MaxDocumentBytes before parse, boundary tests at 262144/262145, depth refusal, parallel reads) plus catching NotSupportedException and a length check before JsonDocument.Parse in PluginPackageFile.ReadManifest. Step 3 (CLI FileStream growth detection) and the GUID fixture choreography are dropped; eng/plugin-manifest.cs is not touched.
- Tests: dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj --filter "FullyQualifiedName~ManifestTests"; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage".
- Resolves: SDK-010, A02-F001.

#### B046 WindowsDeviceControl child build foundation and test isolation

- Domain: wdc. Depends on: B002. Submodule: windows-device-control.
- Files: external/windows-device-control/Directory.Build.props (new, explicit root); `external/windows-device-control/.editorconfig`; `external/windows-device-control/.gitignore`; `external/windows-device-control/src/WindowsDeviceControl/WindowsDeviceControl.csproj`; `external/windows-device-control/tests/WindowsDeviceControl.Tests/*`; tests/WSGM.Tests/Shell/RadioManagerTests.cs (moved cases deleted).
- Steps: WDC-B1 without W02_02 (already landed): explicit-root child Directory.Build.props restating EnforceCodeStyleInBuild, nullable, docs and NuGetAudit=false so in-tree diagnostics stay the same (U01-045, U01-067, BUILD-012); .editorconfig with rules the library already meets; .gitignore (U01-073); tests target net8 and net10 (U01-042); new WifiProfileTests and the RadioManagerTests 90-160 cases moved into WindowsRadioTests (U01-041); NativeLayoutTests pin struct sizes and offsets against hand-written byte buffers (U01-043); remove literal and predicate-copy tests with the corrected anchors (WDC-018, U01-044, U01-080, WDC-026). Child commit and push, then gitlink with the parent test deletion. The full WDC suite is now safe to run.
- Tests: full WDC suite on net8 and net10; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManagerTests".
- Resolves: WDC-018, WDC-026, BUILD-012.

#### B047 Toolkit check infrastructure; WSGM runs every check on its asset

- Domain: toolkit. Depends on: B032. Submodule: steam-ui-toolkit.
- Files: external/steam-ui-toolkit/eng/build-prelude.mjs (split: side-effect-free fragment list + thin CLI); `external/steam-ui-toolkit/eng/check-harness.mjs`; `external/steam-ui-toolkit/eng/run-checks.mjs`; `external/steam-ui-toolkit/eng/check-*.mjs`; `external/steam-ui-toolkit/package.json`; `package-lock.json`; `eng/build-steam-assets.mjs`; `eng/check-steam-module-discovery.mjs`; `package.json`; `src/WSGM/Core/SteamUiAssets/NativeQamBootstrap.js`; `src/WSGM/Core/SteamUiAssetCatalog.cs`.
- Steps: TOOLKITJS-B1 with _plan/refactor-2.1/review/toolkitjs.verify.md: export steamUiFragments(extra) from a side-effect-free module (the prelude CLI imports it), insert // @fragment markers (the emitted asset and its hash change; say so in the commit), harness fragment(asset, path), run-checks discovers check-*.mjs (TOOLKITJS-012), replace cross-file slices (TOOLKITJS-011), one fragment-order owner (TOOLKITJS-010), WSGM's builder imports the list and steam-assets:claims runs the whole toolkit suite on the composed asset (TOOLKITJS-009; fix any latent failure, do not narrow), package metadata (TOOLKITJS-038). Child commit and push, then parent gitlink plus regenerated asset and hash.
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests".
- Resolves: TOOLKITJS-009, TOOLKITJS-010, TOOLKITJS-011, TOOLKITJS-012, TOOLKITJS-038.

#### B048 Toolkit transport and connection correctness

- Domain: toolkit. Depends on: B047. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiCdpConnection.cs`; `PersistentSteamUiTransport.cs`; `SteamCef.cs`; `NativeTcp.cs`; `SteamUiToolkit.csproj`; `external/steam-ui-toolkit/tests/SteamUiToolkit.Tests/{SteamUiCdpConnectionTests,PersistentSteamUiTransportTests,SteamCefTests}.cs`; `Fakes/QueueWire.cs`.
- Steps: TOOLKITCS-B1 (_plan/refactor-2.1/review/toolkitcs.md section 5) with verify corrections: send under the connection lifetime token, caller cancellation only ends the wait (002); an answered JavaScript error keeps Ready and resets timeouts (004); request leases do not start reconnect loops (005); an absent target does not escalate backoff (006); delete the dead notification lane (007; the BindingCalled rename happens in B053); per-role latest generation slot (008); connect to 127.0.0.1 only and accept loopback or wildcard owner rows (009; update the localhost row in SteamCefTests and the FailFirstEvaluation expectations, named in the step list so a reviewer does not read them as regressions); the iphlpapi import keeps the default search path (010 is dropped by maintainer decision, security theater); stale csproj comments (011); track reconnect and retirement tasks (012 part). No public API change. Child commit and push, then gitlink.
- Tests: `dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiCdpConnectionTests|FullyQualifiedName~PersistentSteamUiTransportTests|FullyQualifiedName~SteamCefTests"`.
- Resolves: TOOLKITCS-002, TOOLKITCS-004, TOOLKITCS-005, TOOLKITCS-006, TOOLKITCS-007, TOOLKITCS-008, TOOLKITCS-009, TOOLKITCS-011.

#### B049 Toolkit probe safety, bridge parsing, module runtime and storage resolution

- Domain: toolkit. Depends on: B048. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamStorageSurface.cs`; `SteamUiPatchEvaluation.cs`; `Surfaces/SteamGatePatch.cs`; `SteamUiBridge.cs`; `SteamUiModule.cs`; `SteamUiModuleRuntime.cs`; `SteamUiModuleBuilder.cs`; `SteamUiShared.cs`; `Surfaces/SteamPerformanceSurface.cs`; `Surfaces/SteamScreensaverSurface.cs`; `Surfaces/SteamQuickAccessRowPatch.cs`; `Surfaces/SteamOverlayActivationPatch.cs`; `SteamUiLog.cs`; `SteamUiAssets/Source/gates/storage.ts`; `tests`.
- Steps: TOOLKITCS-B2 and the TypeScript storage change in one child commit (toolkitcs.verify batch problem 10): the storage probe and gate resolve the provider by source shape and never invoke exports (TOOLKITCS-001, TOOLKITJS-018). Answered-error probe is Incompatible, root-kind checks (033/034); gate probe uses EvaluateProbeAsync (035); bridge JSON root and name checks, bootstrap error diagnostic, removal acknowledgement check (034/037); module set with installer, publisher and handler indexes and duplicate publication refusal (041); distinct refusal reasons (042); payload lines redact string values but keep names, kinds, numbers and booleans (020, R11); Bound fix (040); invariant parse (051); \z (052); primaryCountName validation (048); B113 map without cap or sticky overflow (054); volatile log sink (043). TOOLKITCS-V-002: OnRequestReceived starts RespondAsync off the pump (Task.Run, tracked in _requestTasks) so a handler's synchronous prefix cannot block cancels. TOOLKITCS-045 is replaced: the runtime normalises a refusal without detail to the fixed 'no reason reported' text instead of builders throwing; builders only null-check delegates. 017 is refuted and dropped. Tests include a Node run asserting no export is invoked and the runtime command matrix.
- Tests: dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamStorageTests|FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamWindowSurfaceTests"; child: npm run prelude:claims in external/steam-ui-toolkit; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITCS-001, TOOLKITCS-020, TOOLKITCS-033, TOOLKITCS-034, TOOLKITCS-035, TOOLKITCS-037, TOOLKITCS-040, TOOLKITCS-041, TOOLKITCS-042, TOOLKITCS-043, TOOLKITCS-045, TOOLKITCS-048, TOOLKITCS-051, TOOLKITCS-052, TOOLKITCS-054, TOOLKITCS-V-002, TOOLKITJS-018.

#### B050 Toolkit bridge reassembly and module resolution that recovers

- Domain: toolkit. Depends on: B049. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/bridge.ts`; `module-resolver.ts`; `ownership.ts`; `rpc.ts`; `eng/check-startup.mjs`; `eng/check-ownership-claims.mjs`.
- Steps: TOOLKITJS-B2 with verify corrections: part reassembly keyed by delivery id so interleaved deliveries do not cancel each other (001); dispose clears gates, refuses later deliveries and returns failed gate names (019); capture the webpack runtime once per bridge in bridge.ts and drop the sticky 'failed' set (V-001, 017), with a check-startup case where a factory throws, is fixed, and resolves on the next try; module-resolver.ts stays a single plain-JS function expression because C# embeds it (V-002) and the batch runs the C# ModuleResolver and Probe tests; remove registry and token caps (005 part); release edge cases (020); comment fixes.
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiBridgeHostTests|FullyQualifiedName~ModuleResolver|FullyQualifiedName~Probe"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITJS-001, TOOLKITJS-017, TOOLKITJS-019, TOOLKITJS-020, TOOLKITJS-V-001, TOOLKITJS-V-002.

#### B051 Toolkit gate lifecycle, content caps and missing checks

- Domain: toolkit. Depends on: B050. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/*.ts`; `gate-helpers.ts`; eng/check-*.mjs (new check-audio, check-performance, check-network); `tests/WSGM.Tests/Core/SteamUiAssetTests.cs`.
- Steps: TOOLKITJS-B3 with verify corrections: release-then-forget in the ten gates (008); remove content caps in TOOLKITJS-005 except the percent device range (it is the value's type) and do not lower home-carousel's 250,000 walk bound; per-React glyph cache; shared route-list helper; module-id, minified-name and product-name comments removed (006 comment part, 007, 026); assertRemoveRetries used by every gate check, with new check-audio, check-performance and check-network fixtures (V-003, U03A-SUTS-006); performance gate snapshots the four displaced fields on first onState and restores them on removal (V-004); an undecodable performance update is refused, not an empty delta (U03A-SUTS-004); consistent check style (036).
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamStorageTests|FullyQualifiedName~SteamLibraryBadgeTests|FullyQualifiedName~SteamHomeCarouselTests|FullyQualifiedName~SteamNavigationPanelTests|FullyQualifiedName~SteamScreensaverTests"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests".
- Resolves: TOOLKITJS-005, TOOLKITJS-007, TOOLKITJS-008, TOOLKITJS-026, TOOLKITJS-036, TOOLKITJS-V-003, TOOLKITJS-V-004.

### Phase E: library and SDK API batches with their consumers

#### B052 Plugin API 4 contract

- Domain: sdk. Depends on: B035, B045.
- Files: src/WSGM.Plugin.Sdk/PluginText.cs (deleted); `src/WSGM.Plugin.Sdk/PluginManifest.cs`; `src/WSGM.Plugin.Sdk/PluginContracts.cs`; `src/WSGM.Plugin.Sdk/PluginCapabilities.cs`; `src/WSGM.Plugin.Sdk/README.md`; src/WSGM.Plugin.Sdk/WSGM.Plugin.Sdk.csproj (0.3.0); `src/WSGM/Shell/PluginHost.cs`; `src/WSGM/Shell/PluginCapabilityChannel.cs`; `src/WSGM/Shell/CommonPluginActions.cs`; `src/WSGM/Shell/PluginWidgetPins.cs`; `src/WSGM.Plugin.IntelGpu/IntelLog.cs`; `src/Shared/Gpu/DriverRuntime.cs`; `src/WSGM.Plugin.Ir/IrPayload.cs`; four common manifests; `tests`.
- Steps: SDK-B6: delete PluginText for PlainText (SDK-018); PluginManifest setters become init (SDK-030); trace channel moves from ICapabilityHost to IPluginHost with one host log formatter (SDK-020); README rewritten to match the real dependencies and placement (SDK-019, A02-F002); common manifest permissions kept as documented metadata (SDK-V-006); PluginApi.Version 4. This bump also covers the toolkit types reachable from ISteamUiModule (TOOLKITCS-V-001): later contract-breaking toolkit batches stay inside unreleased API 4 and the Plugin SDK docs name that closure.
- Tests: dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~PluginHost|FullyQualifiedName~PluginCapabilityChannel"; IntelGpu, NvidiaGpu, AmdGpu and Ir test projects.
- Resolves: SDK-018, SDK-019, SDK-020, SDK-030, SDK-V-006, A02-F002, TOOLKITCS-V-001.

#### B053 Dispatch-aware outcomes, explicit SteamClient, no ambient session

- Domain: toolkit. Depends on: B052, B051. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiTransportModels.cs`; `PersistentSteamUiTransport.cs`; `SteamUiCdpConnection.cs`; SteamUiTransportSession.cs (deleted); Client/SteamClient.cs (new); `Client/*.cs`; `SteamUiPatchManager.cs`; `tests`; parent consumers (_plan/refactor-2.1/review/toolkitcs.md section 4, 19 files); `src/WSGM/Shell/ShellSession.GameLibrary.cs`; `src/WSGM/Core/LibraryFilter.cs`; `src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs`; `src/WSGM/Shell/AnimationService.cs`; `src/WSGM/Shell/RunningApplicationTarget.cs`.
- Steps: TOOLKITCS-B3 (supersedes T01_01, whose tests become tests of this batch) with the parent consumers in the same parent commit. Transport outcome SteamUiDispatch NotSent | Closed | Unanswered | Answered, set by the connection's send phase (003); client writes NotSent | Unknown | Rejected | Applied, an answered JavaScript error is Rejected, cancellation before send NotSent and after send Unknown (R2, A01-F004, U02A-SUTC-001, U02B-SUTC-007). Reachable null or unparseable replies from install-folder and other parsers map to Unknown (V-003). SetEnabled public; delete SteamUiTransportSession and CefEvalResult; SteamClient over one transport with one write lane covering apps, collections and install folders (014/018); typed reads, delete lossy reads (021); uint app ids (022); AddShortcut adoption only when exe and name match (015); partial outcomes keep ownership in collections, startup movie and install folders (016); running-apps observer installed after register with a version stamp, single reader (019); one reply shape (023); docs (024); BindingCalled rename (007 tail). Parent: one SteamClient composed in ShellSession; library mapping Applied confirmed, Rejected refused, Unknown stops the run with no retry and keeps the record (LIBRARY-B5, LIBRARY-004 part 2), 'may not have been created; scan again' wording (LIBRARY-V-005); LibraryFilter takes the client (LIBRARY-024); launch fixes keep the restoration snapshot on Unknown (OVERLAY-010); startup-movie set-aside persists Steam's choice from partial replies (STEAMHOST-016) and the script waits in-script for the settings store with a budget (STEAMHOST-V-005). Child commit and push first.
- Tests: dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamClientTests|FullyQualifiedName~SteamLibraryReadTests|FullyQualifiedName~SteamStartupMovieTests|FullyQualifiedName~PersistentSteamUiTransportTests"; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RunningApplicationTarget|FullyQualifiedName~SteamDownloadSort|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~GameLibrary|FullyQualifiedName~Animation|FullyQualifiedName~LaunchFix"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITCS-003, TOOLKITCS-013, TOOLKITCS-014, TOOLKITCS-015, TOOLKITCS-016, TOOLKITCS-018, TOOLKITCS-019, TOOLKITCS-021, TOOLKITCS-022, TOOLKITCS-023, TOOLKITCS-024, TOOLKITCS-V-003, A01-F004, LIBRARY-024, LIBRARY-V-005, OVERLAY-010, STEAMHOST-016, STEAMHOST-V-005.

#### B054 Patch contract, one manager loop, quarantine in the manager, bounded teardown

- Domain: toolkit. Depends on: B053. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiPatchManager.cs`; `SteamUiModuleRuntime.cs`; `SteamUiBridge.cs`; `Surfaces/SteamUiBridgePatch.cs`; `SteamUiAssets/Source/bridge.ts`; `Surfaces/SteamGatePatch.cs`; `Surfaces/SteamQuickAccessRowPatch.cs`; surfaces constructing gates; `tests`; `src/WSGM/Core/SteamDownloadSort.cs`; `src/WSGM/Core/SteamInputGlyphStylePatch.cs`; `src/WSGM/Shell/SteamChordResetSurface.cs`; `src/WSGM/Shell/SteamControllerCapsSurface.cs`; `src/WSGM/Shell/SteamUiSessionHost.cs`.
- Steps: TOOLKITCS-B4 with toolkitcs.verify corrections, parent implementers and host in the same parent commit. No DependsOn graph: the manager applies its own bridge first and removes it last on every path (025, R3). Delete resource gates and ResourceKey (026), SteamUiPatchBounds, Version, Unique and the expression cap (031, 032). An apply that began and failed or timed out runs a bounded removal and is never blindly reapplied; distinct diagnostics for kill switch, generation change and timeout (027, 028); logging outside the entry lock (029). One coalesced loop raises Synchronized after the iteration completes (asynchronously, never inline inside the loop task), and awaited switch changes keep running their pass under the scheduler gate, which avoids the B4 deadlock (030, R5). The runtime faults the failing module's patches in the manager; quarantine lives for the module instance, with no reset API (044, R4). Toolkit half of plugin Steam UI modules registered when the plugin becomes ready (STEAMHOST-006, maintainer decision): SteamUiModuleRuntime.ReplaceModules swaps the module set under _moduleGate, cancels the in-flight requests of modules that left and drops them from _failedModules, so a re-added module starts unfaulted; SteamUiPatchManager.Unregister retracts and removes one patch; SteamUiBridgeHost.SetAllowedCommands swaps the command vocabulary and bumps a vocabulary revision that joins assetHash in the bootstrap configuration and bridge.ts's reuse check, so the next synchronization reinstalls the bridge with the new map; a replace registers added patches, swaps vocabulary and module set, then unregisters removed patches. Tests: a command reaches an added module and is refused for a removed one, the removed patch is retracted, a re-added module starts unfaulted, a vocabulary change makes the next bootstrap a reinstall. Bridge probe has only webpack/React/asset preconditions and QAM row patches probe tdpAvailability, tdpComponent and profileProjection themselves (036). steam-ui: prefix and fingerprints (038, 039). Bounded teardown only where unbounded today: module runtime publication loop and request tasks, and manager removal across patches (012 corrected); no new ShutdownAsync on transport or bridge. Parent: SteamDownloadSort (Version 5 removed), SteamInputGlyphStylePatch, the two gate constructor callers, and SteamUiSessionHost deletes _failedPatchIds, the remount guard, SynchronizeLoopAsync and the two-pass disable, reconciling in the Synchronized handler (STEAMHOST-004, STEAMHOST-037).
- Tests: dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamUiPatchManagerTests|FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamUiModule|FullyQualifiedName~SteamUiBridge"; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~SteamDownloadSort"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITCS-012, TOOLKITCS-025, TOOLKITCS-026, TOOLKITCS-027, TOOLKITCS-028, TOOLKITCS-029, TOOLKITCS-030, TOOLKITCS-031, TOOLKITCS-032, TOOLKITCS-036, TOOLKITCS-038, TOOLKITCS-039, TOOLKITCS-044, STEAMHOST-004, STEAMHOST-037, STEAMHOST-006 (toolkit half).

#### B055 Toolkit component host split, behaviour identical

- Domain: toolkit. Depends on: B051. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/components.ts`; qam-validators.ts (new); qam-hooks.ts (new); qam-rows.ts (new); qam-sections.ts (new); `gate-helpers.ts`; `eng/check-power-profile.mjs`; `eng/check-startup.mjs`.
- Steps: TOOLKITJS-B4 (pure move per _plan/refactor-2.1/review/toolkitjs.md section 4) keeping today's literals; new qam-* fragments hold only functions and frozen literals (no top-level const reading kit names, TDZ); check-startup builds the host including the new fragments; merge cpu boost into createChoiceControl and one sendPending. Do not add 'clear host state on dispose' (TOOLKITJS-030 refuted). Verify eng/check-steam-fingerprints.mjs still finds every conjunction. Emitted asset diff limited to markers and moved order.
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITJS-004.

#### B056 Toolkit public surface cleanup, version 0.2.0

- Domain: toolkit. Depends on: B054. Submodule: steam-ui-toolkit.
- Files: external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamSurfaceModule.cs (deleted); `SteamUiModuleBuilder.cs`; `SteamUiPayload.cs`; every surface file; Surfaces/SteamUiText.cs (deleted); `Surfaces/SteamPagePatch.cs`; `Surfaces/SteamSideMenuSnapshot.cs`; `Surfaces/SteamSoundOverrideSurface.cs`; `SteamUiEndpointDiscovery.cs`; `SteamUiExtension*.cs`; `SteamUiToolkit.csproj`; src/WSGM/Shell/ShellSession.cs (using); src/WSGM/Shell/SteamUiSessionHost.cs (using); src/WSGM/Shell/NativeQamSemanticServices.cs (SteamUiText); six SteamPagePatch users.
- Steps: TOOLKITCS-B5 minus Selectable (that moves to the presentation contract): one builder (046); payload arity (050); SteamPageProbe tokens list (049); side-menu and window caps removed and KeyboardOpen null when unknown (031, 053, V-004 identifier cap 96, command error text truncation to the page, fingerprint truncation in logs only per R12); sound override record (058); one namespace, updating the parent usings in ShellSession.cs and SteamUiSessionHost.cs (056, toolkitcs.verify batch problem 2); delete SteamUiText and normalise at the three WSGM call sites (057, STEAMHOST-044); internalize unused seams and the authorizer (063); extensions: discovery only, reserved prefix, manifest kept on rejection, no script cap, docs fixed (061, 062, R7), with no reparse refusal (U02A-SUTC-061 is dropped by maintainer decision, security theater); readback wording in contract docs (059); stale docs (060); version 0.2.0. Child commit and push, then parent.
- Tests: dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamSurfaceModuleTests|FullyQualifiedName~SteamChoiceRowTests|FullyQualifiedName~SteamUiExtensionHostTests|FullyQualifiedName~SteamWindowSurfaceTests|FullyQualifiedName~SteamPagePatchTests"; dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true.
- Resolves: TOOLKITCS-031, TOOLKITCS-046, TOOLKITCS-049, TOOLKITCS-050, TOOLKITCS-053, TOOLKITCS-056, TOOLKITCS-057, TOOLKITCS-058, TOOLKITCS-059, TOOLKITCS-060, TOOLKITCS-061, TOOLKITCS-062, TOOLKITCS-063, TOOLKITCS-V-004, STEAMHOST-044.

#### B057 Host-supplied Quick Access presentation with identical output

- Domain: toolkit. Depends on: B056, B055. Submodule: steam-ui-toolkit. Decisions: D13.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/qam-*.ts`; `components.ts`; `settings.ts`; `gates/library-badge.ts`; `gate-helpers.ts`; C# Surfaces in the toolkitjs.md section 4 contract table; `Surfaces/SteamPowerPresetRow.cs`; `src/WSGM/Shell/NativeQamSemanticServices.cs`; `src/WSGM/Shell/NativeQamPowerPresetService.cs`; `src/WSGM/Shell/DevicePowerAssignments.cs`; `src/WSGM/Shell/SteamUiSessionHost.cs`; `src/WSGM/Shell/LibraryBadges.cs`; `src/WSGM/Core/SteamLibraryVdf.cs`; `src/WSGM/Shell/LibraryTabManager.cs`; `Surfaces/SteamLibraryBadgeSurface.cs`; `external/steam-ui-toolkit/eng/check-library.mjs`; graphics and settings page row builders; `tests/WSGM.Tests/Core/SteamUiAssetTests.cs`; regenerated asset and hash.
- Steps: TOOLKITJS-B5a and B5b merged into one parent commit (no parent gitlink may land between them). The layout publication carries today's section ids as opaque strings, titles, icons, order, membership, folds and Reset placement (R1: no fold-id migration); hideValveFpsRows (R6); accent descriptions built by WSGM ('Game override · <status>') replace overrideId, including a host-supplied editDescription for the lighting Edit-color toggle (R5, V-007); Selectable replaces the magic 'custom' id with V-006 semantics (listed only in a dropdown whose current value it is, never sent, no whole-state rejection) (TOOLKITCS-047, DEVICE-025 library side); range rows draw from the descriptor and never vanish for missing or off-step readback (002); delete the frame-limit persistence field (014); the controller restart sentence moves to status; the library badge shows the library's name (D13, 025): the toolkit drops its 'Internal' default (internalLabel in library-badge.ts, InternalLabel in SteamLibraryBadgeState), so a game no published library holds gets no badge; WSGM reads libraryfolders.vdf per entry and publishes one SteamLibraryBadgeLibrary per Steam library registration that is not a tracked card, named by its Steam label or its drive root, with the app ids of its own apps block, while a registered card keeps its card name once; this is a visible change (an internal library shows its own name instead of 'Internal', and no badge is drawn before WSGM's first publication); isNavigableRoute rejects control characters (035); remove the duplicated 200 W checks and keep one type check (STEAMHOST-V-009, R11); the OSD projection keeps raw observed and desired watts while only the slider uses the ceiling fallback (STEAMHOST-V-008 is finished in B087). Move the 'no Use global control' assertion and keep the platform-spoofing invariants in SteamUiAssetTests (013). Golden test: WSGM's published layout equals today's literals. Manual M01-25.
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamPerformanceTests|FullyQualifiedName~SteamChoiceRowTests|FullyQualifiedName~SteamQuickAccessRowPatchTests|FullyQualifiedName~SteamPanelFoldsTests|FullyQualifiedName~SteamSettingsRowsTests|FullyQualifiedName~SteamLibraryBadgeTests"; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQam|FullyQualifiedName~SteamUiAssetTests|FullyQualifiedName~QuickAccessFolds|FullyQualifiedName~LibraryBadges|FullyQualifiedName~SteamLibraryVdf"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITJS-002, TOOLKITJS-003, TOOLKITJS-013, TOOLKITJS-014, TOOLKITJS-025, TOOLKITJS-035, TOOLKITJS-V-006, TOOLKITJS-V-007, TOOLKITCS-047, STEAMHOST-V-009.

#### B058 Drafts and row refusals

- Domain: toolkit. Depends on: B057. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/settings.ts`; `qam-rows.ts`; `gates/extensions-tab.ts`; `eng/check-settings-fields.mjs`; `eng/check-extension-surfaces.mjs`; `eng/check-power-profile.mjs`; `src/WSGM/Core/SteamUiAssets/Source/wsgm-settings.ts`; `wsgm-graphics.ts`.
- Steps: TOOLKITJS-B6 with V-009: useSteamSettingDrafts; onChange returns a promise; a refusal drops only that row's draft and shows the refusal in the row description (U03B-SUTS-019), never truncated (R10); on a new revision drop only drafts whose published value changed or whose write completed, keeping uncommitted text; one draft and confirm implementation (015) and row component dedupe (016); the Extensions panel keeps its own subscription (033 corrected, no change there); WSGM fragments drop the fake-revision workaround.
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiAssetTests".
- Resolves: TOOLKITJS-015, TOOLKITJS-016, TOOLKITJS-V-009.

#### B059 File picker: worker enumeration, cancellation and stale-result rejection, whole list as today

- Domain: toolkit. Depends on: B058. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/Surfaces/SteamFilePickerSurface.cs`; `Surfaces/SteamSurfaceJsonContext.cs`; `SteamUiAssets/Source/file-picker.ts`; eng/check-file-picker.mjs (new); tests/SteamFilePickerTests.cs (new).
- Steps: TOOLKITCS-B6 and TOOLKITJS-B8 merged, following critic conflict 7 and the UI-identical requirement (supersedes planning-corrections B2 and refines R8/R2). C#: places and listings enumerate and sort on a worker with the caller's cancellation (the handler is already off the bridge pump since B049); outcomes Listed, Empty, Inaccessible, TimedOut, Cancelled with fixed refusal strings instead of ex.Message; the existing bridge request timeout is the only wait bound, no second clock; extended \\?\ and \\?\UNC\ normalised to ordinary paths; device namespaces (\\.\, \\?\GLOBALROOT, \Device\, \??\) refused; mapped drives, redirected known folders and reachable UNC keep working as today; Downloads via the known-folder API. The sorted listing returns whole through the existing chunked delivery (TOOLKITJS-V-008: no 32 M cap after B056). JS: renders the whole list as today (no paging and no More control, as the maintainer confirmed), a ticket also guards listPlaces, an unmount effect settles null once, explicit selection or cancel settles once, primary-button fallback (021). No spool, worker caps, ProviderBusy/ProviderCapacity or FilePickerPathPolicy type. Library folders keep refusing UNC (LIBRARY-035). Fixtures: 450-entry temp tree order, two directories then Back with a stale listing ignored and selection pending, cancel and unmount settle null once, extended-path normalization, device namespace refusal, timeout through an internal blocking enumerator seam.
- Tests: dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamFilePicker"; child: npm run prelude:claims in external/steam-ui-toolkit; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITCS-055, TOOLKITJS-021, TOOLKITJS-V-008, LIBRARY-035.

#### B060 Toolkit theme cache, carousel cost and small JS items

- Domain: toolkit. Depends on: B059. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/theme-styles.ts`; `gates/home-carousel.ts`; `components.ts`; `ownership.ts`; `ui-kit.ts`; `checks`.
- Steps: TOOLKITJS-B9 reduced per verify (no cross-gate steamDocuments store until a late unstyled portal is shown live): clear the pattern cache on removal and fix the stale comment (023); status reads cached counts; home carousel costs (024); colour editor keeps unknown colours instead of white (022); tab transform takes a cheap shape gate before filtering and both claims iterate frozen transform arrays (V-005, 029); confirmed small component-host and UI-kit items (031, 032); redundant dedupe and polling (034).
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; dotnet test external\steam-ui-toolkit\tests\SteamUiToolkit.Tests\SteamUiToolkit.Tests.csproj --filter "FullyQualifiedName~SteamThemeStyleTests|FullyQualifiedName~SteamHomeCarouselTests"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: TOOLKITJS-022, TOOLKITJS-023, TOOLKITJS-024, TOOLKITJS-029, TOOLKITJS-031, TOOLKITJS-032, TOOLKITJS-034, TOOLKITJS-V-005.

#### B061 Fingerprints without minified tokens (attended)

- Domain: toolkit. Depends on: B060. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gate-helpers.ts`; `settings.ts`; eng/check-startup.mjs fixture.
- Steps: TOOLKITJS-B10 with the verify correction: select the localizer and value field by author-typed tokens (LocalizeString( plus the module's own literals), never by invoking candidate exports. Needs the maintainer's live Steam validation of the localizer, value field, buttons and tabs on the current client; never during a Steam cold start.
- Tests: child: npm run prelude:claims in external/steam-ui-toolkit; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims; attended live check by the maintainer.
- Resolves: TOOLKITJS-006.

#### B062 Toolkit test quality and probe execution coverage

- Domain: toolkit. Depends on: B061. Submodule: steam-ui-toolkit.
- Files: `external/steam-ui-toolkit/tests/SteamUiToolkit.Tests/*`; `Fakes/RecordingBackend.cs`; `Fakes/TestJson.cs`; `Fakes/NodeScript.cs`; `SteamGatePatchContractTests.cs`.
- Steps: TOOLKITCS-B7 and TOOLKITJS-B11: re-derive the test anchors (many cited test lines do not exist); NodeScript runs every gate and surface probe against claimed, unclaimed, ambiguous, absent and already-owned models, and verify/remove predicates against status objects (U03B-SUTS-006, U02B-SUTC-005/006); keep source-token checks only as secondary structural guards; delete getter-only tests; derive surface Commands from handlers and cover all surfaces; distinct fake labels and configurable results; descriptive wait failures; node resolution with process.execPath and stderr on timeout; log-sink tests in one non-parallel collection.
- Tests: full toolkit .NET suite once; child: npm run prelude:claims in external/steam-ui-toolkit.
- Resolves: TOOLKITCS-064, TOOLKITCS-065, TOOLKITCS-066, TOOLKITCS-067, TOOLKITCS-068, TOOLKITCS-069, TOOLKITCS-070, TOOLKITCS-071.

#### B063 WDC watch registrations; RadioManager drops its feed generations

- Domain: wdc. Depends on: B046. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.WifiWatch.cs`; WindowsRadio.Bluetooth.cs (watch); `WindowsRadio.cs`; `WindowsRadio.WlanNative.cs`; `CoreAudio.Watch.cs`; CoreAudio.Native.cs (visibility); csproj (0.2.0); `tests`; `src/WSGM/Shell/RadioManager.cs`.
- Steps: WDC-B2 with wdc.verify corrections: Start*Watch returns an owned IDisposable per registration (no hub), unregister outside any lock, ACM only, idempotent WlanClient, watcher Stopped/Aborted handlers, documented callback thread and 'do not dispose a Wi-Fi registration inside its callback' (U01-001, 007 feed slots, 018, 021, 022, 024 watcher abort, 055, 056, 060, 084, A01-F003, A01-F005, WDC-014). The WLAN notification seam lands in this batch (not B3) and the batch widens the needed COM interface visibility explicitly (R1, R2). Version 0.2.0. RadioManager consumes registrations; disposing one is the stop; delete _feedWork, QueueFeedWork and _bluetoothWatchGeneration (WINSVC-022).
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~WatchRegistrationTests|FullyQualifiedName~CoreAudioTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~BluetoothAction|FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~NativeQamBluetooth".
- Resolves: WINSVC-022, A01-F003, A01-F005, WDC-014.

#### B064 Wi-Fi identity by bytes and security; truthful connect outcomes

- Domain: wdc. Depends on: B063. Submodule: windows-device-control. Decisions: D4.
- Files: `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.cs`; `WindowsRadio.Wifi.cs`; `WindowsRadio.WifiRules.cs`; `WindowsRadio.WlanNative.cs`; `WifiProfile.cs`; external/windows-device-control/AGENTS.md (diff shown with the batch and applied, D4); `tests`; `src/WSGM/Shell/RadioManager.cs`; `src/WSGM/Shell/RadioEntries.cs`; `src/WSGM/Overlay/RadioPanel.axaml.cs`; `src/WSGM/Shell/NativeQamNetworkService.cs`; `tests/WSGM.Tests/Shell/RadioManagerTests.cs`.
- Steps: WDC-B3 with wdc.verify corrections and the D4 guidance diff shown with the batch and applied (library AGENTS allows a typed result that carries the raw WLAN reason code). WifiNetworkKey is raw SSID bytes plus security class (R4); merge by bytes and security, several saved names are not ambiguity; connected profile, else ordinal first; forget removes every matching profile and reports each; refuse empty keys, enterprise and WEP writes and unreadable XML before mutation; scan status propagated with its native code; definite refusal restores a created or replaced profile once, a dispatched timeout returns Pending with the profile intact; GetWifiStatus is Unknown without a WLAN interface; dead code removed (U01-002, 012-017, 047, 048, 049, 053, 054, 057, 074 Wi-Fi part, WDC-022). WSGM keeps today's strings through a mapping table with a table test (WDC-V-003) and detects location consent by status == 5, not message text (WDC-V-002); NativeQamNetworkService projects from RadioManager instead of polling (WINSVC-023). Real-device Wi-Fi validation (M01-21).
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~WifiConnectTests|FullyQualifiedName~WindowsRadioTests|FullyQualifiedName~WifiProfileTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RadioManager|FullyQualifiedName~RadioEntry|FullyQualifiedName~BluetoothAction|FullyQualifiedName~NativeQamNetwork|FullyQualifiedName~NativeQamBluetooth".
- Resolves: WDC-022, WDC-V-002, WINSVC-023.

#### B065 Radio power results, Bluetooth container normalization and pairing attempts

- Domain: wdc. Depends on: B064. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/WindowsRadio.Power.cs`; `WindowsRadio.Bluetooth.cs`; `CoreAudio.Bluetooth.cs`; `WindowsRadio.cs`; `tests`; `src/WSGM/Shell/RadioManager.cs`; `src/WSGM/Shell/BluetoothDeviceCatalog.cs`; `tests/WSGM.Tests/Shell/BluetoothDeviceCatalogTests.cs`.
- Steps: WDC-B4 with corrections: enumerate radios outside RadioCacheLock and publish under it; SetPower per-adapter result; consent validation; one container normalizer in WDC while BluetoothDeviceCatalog and its product policy stay in WSGM (WDC-005, WINSVC-029, critic conflict 12); PairBluetoothAsync with caller token and one 90 s wall-clock deadline, deferrals completed before cancelling, onRequest exceptions contained; RespondToPairing PIN rule (008); UnpairBluetooth status; lazy selector cache synchronized (016); connection verdict requires a profile name (017) (U01-006, 010, 011, 019, 020, 050, 051, 052, 083). WSGM: pairing-text parity table (WDC-V-003), _pairingToken written on the UI post (WINSVC-021), PIN no longer logged (WINSVC-V-005), O(n^2) device matching and per-row Log.Change keys replaced by one pass (WINSVC-044). Real-device pairing validation (M01-22).
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~WindowsRadioTests|FullyQualifiedName~PairingTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~BluetoothDeviceCatalog|FullyQualifiedName~RadioManager".
- Resolves: WDC-005, WDC-008, WDC-016, WDC-017, WDC-V-003, WINSVC-021, WINSVC-029, WINSVC-044, WINSVC-V-005.

#### B066 Core Audio, feedback, backlight and preview correctness

- Domain: wdc. Depends on: B065. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/CoreAudio.Formats.cs`; `CoreAudio.Native.cs`; `CoreAudio.cs`; `CoreAudio.Spatial.cs`; `WaveOutFeedback.cs`; `Backlight.cs`; `AudioFilePreview.cs`; `tests`; `src/WSGM/Shell/AudioManager.cs`; `src/WSGM/Settings/AudioProfileEditor.cs`; `src/WSGM/Shell/AudioProfileService.cs`; `src/WSGM/Shell/SoundPackService.cs`.
- Steps: WDC-B5: format probe returns on HRESULTs other than S_FALSE/UNSUPPORTED_FORMAT with a NULL closest match; out lists only on success; spatial status mapping; drop the cached enumerator on disconnected or service HRESULTs; WaveOut retains driver-held buffers across repeated Dispose (no Play/Dispose lock, single owner documented); one WaveFormat; backlight reads the policy byte; preview failure keeps native detail and _player access is ordered (WDC-009); remove the capture arm of ToWinRtDeviceId (U01-023, 024, 025, 026, 059, 061 docs, 062, 079, 081, 082). AudioEndpoint.Name may be empty; WSGM applies the 'Audio device' fallback in AudioManager, AudioProfileEditor and at the capture points in AudioProfileService and AudioProfileEditor (WDC-V-005).
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~CoreAudioTests|FullyQualifiedName~WaveOutTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioManager|FullyQualifiedName~AudioPlaybackChoices|FullyQualifiedName~AudioProfile".
- Resolves: WDC-009, WDC-V-005.

#### B067 Display write gate, refusal semantics and editor rotation

- Domain: wdc. Depends on: B066, B039. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs`; `DisplayTopology.Native.cs`; `DisplayLayouts.cs`; `DisplayLayoutPlanner.cs`; `DisplayModes.cs`; `DisplayScaling.cs`; `DisplayColor.cs`; `DisplayEdid.cs`; `tests`; `src/WSGM/Settings/DisplayLayoutEditor.cs`; `src/WSGM/Settings/SettingsViewModel.Launch.cs`; tests/WSGM.Tests (real-shaped 2.0 config fixture with stored display records).
- Steps: First, the WSGM config round-trip fixture for the five persisted display records (DisplayTargetIdentity, DisplayLayout, DisplayLayoutOutput, DisplayRefresh, DisplayMode) lands before any record change (WDC-001 corrected; no reflection pin test in WDC). Then WDC-B6: one internal static write gate for apply and transient writes with reads outside it (U01-035, WDC-021); rotation compared in Matches with Rotation = 0 meaning 'keep the display's current rotation' in the planner and Matches, and DisplayLayoutEditor.ToOutput emitting 0 (WDC-V-001; the config migration rewrite of stored 1 to 0 follows in B068); skip unreadable unrelated paths and report a matched unreadable target as a typed failure; Apply never throws after the write; the apply status alone decides (D9): status 0 is Applied with no read after the write and Confirm is deleted, while a non-zero status is Refused with its status and one restore of the captured original when the route is still the same, never retried, with today's user-visible strings (WDC-003 corrected); rollback state from one query (WDC-025); layout extras reuse the resolved path; remove ValidateBufferCounts, the 16-display cap and the 4096 mode loops (WDC-007 display part, U01-064); EDID null monitor (U01-034); EDID read timeout distinct from cancellation (WDC-015); Observe folds routes of a target with neither path nor EDID by RouteKey and WSGM does not persist an identity that cannot match (WDC-V-004) (U01-003, 027, 030, 065). Tests are limited to DisplayLayouts with fake extras where GDI calls sit outside the port. Hardware validation on the notebook and the IR desktop (M01-19).
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~DisplayApplyTests|FullyQualifiedName~DisplayLayoutTests|FullyQualifiedName~DisplayTopologyTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DisplayLayoutDiagnostics|FullyQualifiedName~GameModeEntryTransaction|FullyQualifiedName~Configuration".
- Resolves: WDC-001, WDC-003, WDC-007, WDC-015, WDC-021, WDC-025, WDC-V-001, WDC-V-004.

#### B068 Schema version and the one-time 2.0 to 2.1 migration

- Domain: config. Depends on: B067, B042.
- Files: src/WSGM/Core/AppConfig.cs (SchemaVersion); src/WSGM/Core/ConfigRepair.cs (Migrate); `src/WSGM/Core/ConfigStore.cs`; tests (real-shaped 2.0 fixtures).
- Steps: CONFIG-B3 with the decided constraints: SchemaVersion 1 (absent means 0); migrate on read in memory, persist on the next strict write, write one config.v0.json with CreateNew before the first v1 save; a file with a newer SchemaVersion loads best effort as today, with no UnsupportedSchema outcome and no read-only mode (maintainer decision); members this build does not know are dropped on the next save. Migrations: deterministic custom-tab ids from source index and name with a collision ordinal, no longer minted by the initializer or Normalize (CONFIG-017); Rotation 1 -> 0 in GameLayout and DesktopLayout only (always editor-authored; PendingReturnLayout keeps its captured rotation) (WDC-V-001); no fold-id migration (published section ids stay today's strings); sidecars and IR, theme, sound and movie state are left in place. Export for setup migrates in memory only and never writes (INSTALL-006 constraint). Tests: a 2.0 fixture round trip preserves every section, sparse profiles, plugin revisions, recovery snapshots, credentials and stored display identities with identity equality and layout matching after the EDID fix (CONFIG-V-008); v0 copy created once; a v2 file loads as Loaded with every section this build understands.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Migration|FullyQualifiedName~Configuration"`.
- Resolves: CONFIG-017, CONFIG-V-008.

#### B069 Typed display results; WSGM owns display wording

- Domain: wdc. Depends on: B068. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.Types.cs`; `DisplayLayouts.cs`; `DisplayLayoutPlanner.cs`; `DisplayModes.cs`; `DisplayScaling.cs`; `DisplayColor.cs`; `tests`; `src/WSGM/Overlay/DisplayModeView.cs`; `src/WSGM/Settings/DisplayLayoutEditor.cs`; `src/WSGM/Settings/SettingsViewModel.Displays.cs`; `src/WSGM/Core/DisplayProfiles.cs`; `src/WSGM/Core/DisplayScale.cs`; `src/WSGM/Shell/DisplayLayoutDiagnostics.cs`; src/WSGM/Core/DisplayText.cs (new); `src/WSGM.Plugin.NvidiaGpu/NvOutputControls.cs`.
- Steps: WDC-B7 with corrections: rename DisplayProfileResult (WDC-010); outcome enums separate semantic disposition from Win32 status and include Written (the write dispatched with status 0 and nothing is read after it, D9) distinct from Refused; the post-write TryRead comparison in DisplayScaling.TrySet and DisplayColor.TrySetHdr is deleted (WDC-004, U01-031, 063); doc fix (U01-032); WSGM DisplayText maps each code to the exact current literal (WDC-023, U01-036) with a table test; NvOutputControls treats Written as success per the product rule and the NVIDIA package is rebuilt in this batch because WDC resolves host-first; DisplayScale keeps its restore entry only when the write did not dispatch (D9). UI baselines unchanged.
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~DisplayLayoutTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Display"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DisplayPageViewsTests|FullyQualifiedName~GameModeDisplayPageTests"; dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj.
- Resolves: WDC-004, WDC-010, WDC-023.

#### B070 One display arrival waiter in WDC with caller-supplied timing

- Domain: wdc. Depends on: B069. Submodule: windows-device-control.
- Files: external/windows-device-control/src/WindowsDeviceControl/DisplayTopology.cs (waits deleted); DisplayLayouts.cs (waiter); `DisplayTopology.Types.cs`; README display section; src/WSGM/Shell/DisplayArrivalWaiter.cs (deleted); its callers; tests/WSGM.Tests/Shell/DisplayArrivalWaiterTests.cs (moved to WDC DisplayWaitTests).
- Steps: WDC-B8: the WSGM settle waiter moves into WDC as WaitForTargetsAsync and replaces WDC's own topology waits (U01-029, WINSVC-028); settle interval and backstop are caller-supplied parameters with WSGM passing today's 500 ms and 5 s (wdc.verify batch problem 10); the AvailableWait test that reached native code goes with the waits (A01-F006). Tests: settle needs two equal fingerprints, Win32Exception counts as not settled, backstop used when no target, cancellation ends, empty target list returns at once.
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~DisplayWaitTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GameModeEntry".
- Resolves: WINSVC-028, A01-F006.

#### B071 Power, wake and recovery primitives that attempt every item

- Domain: wdc. Depends on: B070. Submodule: windows-device-control.
- Files: `external/windows-device-control/src/WindowsDeviceControl/WindowsWakeSecurity.cs`; `ModernStandby.cs`; `WindowsPower.cs`; `WindowsPower.Notifications.cs`; `WindowsPower.HybridCores.cs`; `WindowsPowerRequest.cs`; `PowerRequestList.cs`; `WindowsStorage.cs`; `tests`; `src/WSGM/Core/LockScreenSettings.cs`; `src/WSGM/Interop/MessageWindow.cs`; `src/WSGM/Interop/WindowsPowerSchemeApi.cs`; `src/WSGM/Overlay/OverlayController.Power.cs`; `src/WSGM/Core/WakeLockStatus.cs`; `src/WSGM/Overlay/WakeLockHoldersView.cs`; `src/WSGM/Core/PowerTimeouts.cs`; `src/WSGM/Interop/WindowsCpuBoostApi.cs`.
- Steps: WDC-B9 with corrections: three wake primitives; a pure WakeSecurityRestorePlan plus a per-item executor that attempts every item and collects failures; a vanished scheme is no longer applicable; DC-only policy logic; non-DWORD values are refused at Capture with a typed failure (U01-005, 037, 075, WDC-006, WDC-024). WSGM keeps today's no-snapshot restore: it composes the secure default (sign-in required on wake) with the same writes as today (WINSVC-034 is refuted), skipping CreateSubKey only when the key is absent. ModernStandby restores from one enumeration with every device attempted and no 4096 cap (U01-038, 076); SafeHandle notification registrations (U01-039); public EnumerateSchemes with the index form kept (U01-078); no 64 cap on possible values; remove MaximumCpuSetBytes, the 64 KiB scheme-name cap and the 4096-byte device name cap (WDC-007 remainder); REASON_CONTEXT full union size (U01-058); request list typed status, x86 Unsupported, captured byte fixtures (U01-080, 085); skip network drives (U01-077); WSGM uses the library GUIDs (WDC-011).
- Tests: dotnet test external\windows-device-control\tests\WindowsDeviceControl.Tests\WindowsDeviceControl.Tests.csproj -f net8.0-windows10.0.19041.0 --filter "FullyQualifiedName~WakeSecurity|FullyQualifiedName~ModernStandbyTests|FullyQualifiedName~PowerRequestListTests|FullyQualifiedName~WindowsPowerTests" and the same with -f net10.0-windows10.0.19041.0; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WakeSecurityRecovery|FullyQualifiedName~PowerRequestList".
- Resolves: WDC-006, WDC-011, WDC-024.

#### B072 WDC docs, metadata and final independent child validation

- Domain: wdc. Depends on: B071. Submodule: windows-device-control.
- Files: `external/windows-device-control/README.md`; `docs/radios.md`; XML docs; csproj metadata; child solution file (new, minimal); AGENTS.md and .coderabbit.yaml (diffs shown with the batch and applied, D4).
- Steps: WDC-B10 without the reflection pin test: README type table, samples, status placement, removed caps, SaveToDatabase note, threading and blocking rules; docs/radios.md hex rule, watch rules, pairing deadline; XML docs (U01-012, 018, 049, 053, 054, 069), which describe a write as dispatched or refused with no readback confirmation (WDC-019, D9); host-neutral wording (U01-066); package metadata (U01-071); a minimal child solution so the child builds and tests standalone (WDC-020); diffs for library AGENTS.md and .coderabbit.yaml shown with the batch and applied (D4; U01-068, 072). Then the independent validation of requirement 16: full WDC suite on both frameworks, child commit and push, parent gitlink.
- Tests: `full WDC suite on net8 and net10`.
- Resolves: WDC-019, WDC-020.

### Phase F: owners, consumers and UI wiring

#### B073 One controller sample path without per-sample allocation

- Domain: input. Depends on: B009.
- Files: `src/WSGM/Shell/ControllerManager.cs`; `src/WSGM/Input/ManagedControllerRouter.cs`; `src/WSGM/Input/ControllerOutputRouter.cs`; `src/WSGM/Input/IControllerTargetBackend.cs`; `src/WSGM/Input/ViiperControllerBackend.cs`; `src/WSGM/Input/ManagedControllerSampleValidator.cs`; `tests`.
- Steps: INPUT-B2 with input.verify corrections: bounded(1) channel drain for the allocation (INPUT-005); one neutral publication for invalid streams (007); the router's Neutral/Active state is reduced only together with an explicit capture and forwarding check in SetSyntheticButtonAsync, which keeps refusing pulses during UI capture, forwarding block and before the first clean sample (006 corrected); remove NeutralizeAsync and the health enum (017); the manager owns and disposes the backend (016); backend DisposeAsync raises TargetLost outside its gate, nothing else about loss reporting changes (015 corrected); do not dispose the backend semaphore (019); Interlocked counter and inline dispatch (020); one wire helper (021). Delete fake-only tests; keep the SpinWait drains that wait on the worker (039 corrected).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~ManagedControllerRouterTests|FullyQualifiedName~ControllerDependencyAdapterTests"`.
- Resolves: INPUT-005, INPUT-006, INPUT-007, INPUT-015, INPUT-016, INPUT-017, INPUT-019, INPUT-020, INPUT-021, INPUT-039.

#### B074 Controller stack composed at the root

- Domain: input. Depends on: B073, B012.
- Files: `src/WSGM/Shell/ControllerManager.cs`; `src/WSGM/Shell/HidHideOwnership.cs`; src/WSGM/Shell/DeviceCoordinator.cs (ctor and target-loss recovery); src/WSGM/Program.cs (uninstall helper); `src/WSGM/Input/ViiperControllerBackend.cs`; src/WSGM/Input/UsbipTool.cs (new); src/WSGM/Input/UiCaptureState.cs (moved); `tests`.
- Steps: INPUT-B3 composition half: ControllerManager.CreateProduction(root) and HidHideOwnership.ForUser(root), DeviceCoordinator receives the built manager (008); pure UsbipTool resolution plus one PATH write (018); immutable status snapshot (014); Lock type (036); final-state parameter on release and ReportTargetFault removal, rewriting the coordinator's target-loss recovery (031); UiCaptureState moves to its own file. The priority write stays under _stateGate (INPUT-035 refuted).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ControllerManagerTests|FullyQualifiedName~HidHideOwnershipTests|FullyQualifiedName~UsbipTool|FullyQualifiedName~DeviceCoordinator"`.
- Resolves: INPUT-008, INPUT-014, INPUT-018, INPUT-031, INPUT-036.

#### B075 Overlay-launched Settings navigates from the managed pad

- Domain: input. Depends on: B074.
- Files: `src/WSGM/Settings/SettingsWindowServices.cs`; `src/WSGM/Settings/SettingsWindow.axaml.cs`; `src/WSGM/Overlay/OverlayController.cs`; `src/WSGM/Shell/DesktopTray.cs`; `src/WSGM/App.axaml.cs`; tests/WSGM.UiTests (ControllerNavigationTests).
- Steps: INPUT-009 (confirmed as a likely defect): pass the existing ManagedUiPad? into SettingsWindowServices.Create; no new pad registry. One UI test proves overlay-launched Settings navigates from the managed pad.
- Tests: `dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests"`.
- Resolves: INPUT-009.

#### B076 Steam Input shim as an owned instance with one apply path

- Domain: input. Depends on: B075.
- Files: `src/WSGM/Core/SteamInputShim.cs`; `src/WSGM/Core/SteamInputManagement.cs`; src/WSGM/Core/Steam.cs (ColdStart signature); `src/WSGM/Program.cs`; `src/WSGM/Shell/ShellSession{,.Config}.cs`; `src/WSGM/Settings/SettingsViewModel.Save.cs`; `src/WSGM/Settings/SettingsViewModel.Steam.cs`; `tests`.
- Steps: INPUT-B4 first half: SteamInputShim instance state with explicit enabled (010); one apply path through tracked background work, removing the untracked Task.Run on reload (011 corrected to low); ownership check uses a static u8 signature and catches only IO and access errors (024); keyed repeat logs (038).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamInputShimTests|FullyQualifiedName~SettingsViewModel"`.
- Resolves: INPUT-010, INPUT-011, INPUT-024, INPUT-038.

#### B077 Steam Input lease owner created by Program

- Domain: input. Depends on: B076.
- Files: `src/WSGM/Core/SteamInputBlocker.cs`; `src/WSGM/Program.cs`; `src/WSGM/Shell/ShellSession.Shutdown.cs`; `src/WSGM/Overlay/OverlayController*.cs`; `src/WSGM/Settings/SettingsWindow*.cs`; `tests`.
- Steps: INPUT-B4 second half: SteamInputBlocker becomes an instance with a lease port, Hold, Drop, ReleaseAsync(Deadline) and RecoveryWarningRaised; Program creates it and hands it to the session, overlay and Settings, so Panic and the post-Avalonia shutdown still reach the live lease; delete the no-op one-shot calls (012). The overlay takes this concrete instance; no ISteamInputLease port (critic conflict 24). New SteamInputBlockerTests: claims balance with a failing acquire, overlapping surfaces keep the lease, drop during acquire, release waits within the deadline.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamInputBlockerTests|FullyQualifiedName~SteamInputShimTests"`.
- Resolves: INPUT-012.

#### B078 Guide-chord mirror binding without the size cap

- Domain: input. Depends on: B077.
- Files: `src/WSGM/Core/SteamGuideChordMirror.cs`; src/WSGM/Shell/GuideChordMirrorBinding.cs (new); `src/WSGM/Shell/ShellSession{,.Config}.cs`; `tests/WSGM.Tests/Core/SteamGuideChordMirrorTests.cs`.
- Steps: INPUT-B5 with the verify scope note: delete MaximumLayoutBytes and never mirror an older file over a newer one for the size or fit decision only; skipping non-chord .vdf files and moving past read failures stay (022); string equality and Log.Change (023); a small binding owner subscribes to controller status and config reload and unsubscribes on dispose (013).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamGuideChordMirror|FullyQualifiedName~GuideChordMirrorBinding"`.
- Resolves: INPUT-013, INPUT-022, INPUT-023.

#### B079 Touch edge input: one raw-input owner, pure recognizer, subscribers

- Domain: input. Depends on: B078.
- Files: `src/WSGM/Overlay/TouchSwipeMonitor.cs`; src/WSGM/Overlay/RawTouchInput.cs (new); src/WSGM/Overlay/EdgeSwipeRecognizer.cs (new); `src/WSGM/Overlay/OverlayController.Gestures.cs`; `tests/WSGM.Tests/Overlay/TouchSwipeMonitorTests.cs`.
- Steps: INPUT-B6 per the symbol table in _plan/refactor-2.1/review/input.md section 4: thresholds and log texts unchanged; RawTouchInput is process-scoped and reference-counted by subscriptions, so the static registry goes. Tests: recognizer tests retargeted unchanged, the registration lives until the last subscriber, a disposed subscriber receives nothing.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~TouchSwipe|FullyQualifiedName~EdgeSwipe"`.
- Resolves: INPUT-030.

#### B080 Navigation and recorder fixes without new options plumbing

- Domain: input. Depends on: B079.
- Files: `src/WSGM/Input/GamepadNavigation.cs`; `src/WSGM/Input/GamepadService.cs`; src/WSGM/Input/GamepadButtons.cs (moved); `src/WSGM/Input/GamepadChordRecorder.cs`; `src/WSGM/Input/KeyRecorder.cs`; `src/WSGM/Interop/KeyboardInput.cs`; `src/WSGM/Settings/SettingsWindow.axaml.cs`; `src/WSGM/Settings/SettingsViewModel.QuickAccess.cs`; `tests`.
- Steps: INPUT-B7 trimmed under the simplify rule: a recording that ends without a capture keeps the existing binding and only the explicit Clear clears it (025, maintainer decision, which replaces requirement 9 here): KeyRecorder.Recorded carries null for Escape and for a hook failure, the chord recorder's 3 s expiry logs that the chord is unchanged, and SettingsWindow keeps _hotkey and _chord on a null or empty result; resolve the keyboard layout once per chord (037); internal visibility for the INPUT-028 types; GamepadButtons enum to its own file (029 part). No GamepadNavigationOptions, IDirectionalControl or TimeProvider injection (026); the 64-step TextBox guard stays (027 is a loop bound).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Input|FullyQualifiedName~SettingsViewModel"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests".
- Resolves: INPUT-025, INPUT-028, INPUT-029, INPUT-037.

#### B081 Drive the device runtime directly; delete the compatibility adapter

- Domain: device. Depends on: B012, B010.
- Files: `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/DevicePluginRuntime.cs`; src/WSGM/Shell/DevicePluginCompatibilityAdapter.cs (deleted); `src/WSGM/Shell/PluginHost.cs`; `src/WSGM/Shell/ShellSession.cs`; `tests`.
- Steps: DEVICE-B1 after the small DEVICE-001 fix: the coordinator calls client.Start/Suspend/Resume/Stop/DisposeAsync directly; the 'worker owns the gate until the plugin returns, caller waits to the deadline' pattern moves into the runtime's lifecycle calls only, never commands or haptic frames (high-rate rule); an unknown stop status maps to Failed (030); DecideResume uses runtime state only; PluginHost no longer admits a Device category (002, SDK-015). Whether any surface still shows the device health row from PluginHost.Snapshot is decided from code: if one does, DeviceCoordinator publishes the row as data. Tests: unverified stop then a new cycle; a plugin whose StopAsync never returns lets StopAsync(deadline) return at the deadline, keeps the load context and later completes once.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~PluginHost"`.
- Resolves: DEVICE-002, DEVICE-030, SDK-015.

#### B082 Router command correctness and active-time command deadline

- Domain: device. Depends on: B081.
- Files: `src/WSGM/Shell/DeviceCapabilityRouter.cs`; src/WSGM/Shell/DeviceCapabilityValidation.cs (moved unchanged); `src/WSGM/Shell/DevicePluginRuntime.cs`; `tests`.
- Steps: DEVICE-B2 with device.verify corrections: latest command id per key, a late result reconciles only when it is the latest for the same descriptor generation, observer tracked and faults logged (006); keep the pre-gate cancellation exception exactly as today (UI and AutoTDP flows depend on it) and add only the runtime's post-gate pre-dispatch check, so a command cancelled before dispatch reaches no plugin code (007 runtime half); TryGetView(key) (021 part); logging after lock release (038); the clock is an injected Func<DateTimeOffset> (final, not interim); the per-command timeout uses the SDK active-time Deadline instead of a wall timer so sleep does not consume it (028, A02-F006 host half). Rename the GPU-side instance to CapabilityRouter only if trivially local (SDK-048).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCapabilityRouter|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~DeviceDesiredWrite"`.
- Resolves: DEVICE-006, DEVICE-007, DEVICE-021, DEVICE-028, DEVICE-038, SDK-048.

#### B083 Bounded single-task device shutdown with admission close

- Domain: device. Depends on: B082.
- Files: `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/DevicePluginRuntime.cs`; `src/WSGM/Shell/DeviceCoordinatorDiagnostics.cs`; `src/WSGM/Shell/AutoTdpService.cs`; `src/WSGM/Shell/DeviceOemActionRouter.cs`; `src/WSGM/Shell/PluginSettingsCoordinator.cs`; `src/WSGM/Shell/ShellSession.Shutdown.cs`; `tests`.
- Steps: DEVICE-B3 with the controller release already bounded (B009): one stored shutdown task (004, small); CloseAdmission() synchronous at T0 (critic conflict 2, single name everywhere) and StopAsync(Deadline) that awaits at most the deadline and retains anything still running; Completion set on every runtime terminal path; delete DeviceTeardownFailureTracker and log at the step (015); diagnostics pipe server starts explicitly and backs off on persistent failure (016); AutoTdpService.StopAsync(Deadline) (027); OEM dispatch and the untracked tasks in PluginSettingsCoordinator and DevicePluginRuntime are tracked, and the OEM router reads its lifetime token under its lock (031). No per-owner phase scheduler or budgets (critic conflict 1).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceOemActionRouter"`.
- Resolves: DEVICE-003, DEVICE-004, DEVICE-015, DEVICE-016, DEVICE-027, DEVICE-031.

#### B084 Steam host local defect fixes

- Domain: steamhost. Depends on: B054.
- Files: `src/WSGM/Shell/SteamStorageBridge.cs`; `src/WSGM/Shell/SteamUiSessionHost.cs`; `src/WSGM/Shell/AnimationService.cs`; `src/WSGM/Shell/CommonPluginSteamUiSource.cs`; `src/WSGM/Shell/WsgmSteamSettingsService.cs`; `src/WSGM/Shell/NativeQamSemanticServices.cs`; `tests`.
- Steps: STEAMHOST-B1 with steamhost.verify corrections (the sync loop is already gone since B054). Storage bridge: the bridge projects an immutable snapshot on the UI thread when either collection changes and both ReadState and command resolution read it (007, V-006); keep the per-round projection but make it cheap: describe only ejectable or formattable letters and skip network drives, no revision cache (008, critic conflict 15). Patch and global switches are derived from one snapshot in every pass, including DisableAsync, so ungated Apply calls cannot be overwritten (002 corrected). AnimationService writes config outside its state lock while the start-time override write stays synchronous (009). Remove the plugin route 256 and ordered-choice 64 caps for type and shape validation, typed default branch (025, 026). Truthful command results (024); dispatcher post removed (022); small inaccuracies (038). Storage tests use fake drive and format lists (041). The changed constructors and their test call sites are listed explicitly (not 'no API change').
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~AnimationService|FullyQualifiedName~CommonPluginSteamUiSource|FullyQualifiedName~WsgmSteamSettings|FullyQualifiedName~NativeQam"`.
- Resolves: STEAMHOST-002, STEAMHOST-007, STEAMHOST-008, STEAMHOST-009, STEAMHOST-022, STEAMHOST-024, STEAMHOST-025, STEAMHOST-026, STEAMHOST-038, STEAMHOST-041, STEAMHOST-V-006.

#### B085 One switches snapshot that keeps every edge effect

- Domain: steamhost. Depends on: B084.
- Files: `src/WSGM/Shell/SteamUiSessionHost.cs`; `src/WSGM/Shell/ShellSession{,.SteamUi,.Config}.cs`; `tests`.
- Steps: STEAMHOST-B2 with verify batch problem 3: SteamUiSurfaceSwitches.From(config, glyph selection, profile) and one Apply(switches) replace the eight Apply* methods and the session shadow fields (003, 047); reload, BP restore and master enable go through one call, which removes the Game Mode double apply (V-010). The synchronous on-edge effects stay synchronous: Apply(false) cancels in-flight requests at once; ApplyScreensaverTimeouts(false) ForgetSteam; network indicator off while QAM is off posts StopScanning; the sound integration status text; carousel preference-only publication without retraction; the glyph delivery decision and its log line. Tests: the switch table for every surface, plus 'commands are refused immediately after Apply(QAM off)'.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~SteamUiSurfaceSwitches"`.
- Resolves: STEAMHOST-003, STEAMHOST-047, STEAMHOST-V-010.

#### B086 Steam UI backends built outside the host

- Domain: steamhost. Depends on: B085.
- Files: `src/WSGM/Shell/SteamUiSessionHost.cs`; src/WSGM/Shell/WsgmSteamModuleCatalog.cs (new); `src/WSGM/Shell/ShellSession.cs`; `src/WSGM/Shell/CommonPluginSteamUiSource.cs`; `src/WSGM/Shell/HomeCarousel.cs`; `src/WSGM/Shell/LibraryBadges.cs`; `src/WSGM.Plugin.Sdk/PluginSteamUi.cs`; `src/WSGM.Plugin.Sdk/README.md`; `docs/plugin-system.md`; `tests`.
- Steps: STEAMHOST-B3: a plain SteamUiBackends record plus a static WsgmSteamModuleCatalog.Create (no per-group interfaces); the composition root builds every backend and disposes what it created; required backends non-null; host constructor (transport, modules, options) (005); CommonPluginSteamUiSource owned by the composition root (027); the host builds asset, bridge and runtime before any external subscription so a contained construction failure leaves no half-built subscriber (V-007); plugin Steam UI modules register when the plugin becomes ready and leave when it stops (006, maintainer decision; the toolkit half lands in B054): CommonPluginSteamUiSource.Refresh computes the plugin module list keyed by registration and Context.Generation and raises ModulesChanged when it differs; the host composes catalog and plugin modules and calls the runtime's ReplaceModules, so a restarted plugin's old handlers go; a plugin patch id or command that collides with an earlier one is dropped with one log line; plugin patch ids are recomputed and switch states reapplied after the replace; WsgmSteamModuleCatalog.Create returns host modules only; PluginSteamUi.cs, the Plugin SDK README and docs/plugin-system.md say modules register on readiness (no SDK API change).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~WsgmSteamModuleCatalog|FullyQualifiedName~CommonPluginSteamUiSource"`.
- Resolves: STEAMHOST-005, STEAMHOST-006, STEAMHOST-027, STEAMHOST-V-007.

#### B087 NativeQam split and projection moves against the concrete coordinator

- Domain: steamhost. Depends on: B086.
- Files: src/WSGM/Shell/NativeQamSemanticServices.cs -> src/WSGM/Shell/NativeQam/*.cs; `src/WSGM/Shell/CapabilityProjection.cs`; `src/WSGM/Shell/DeviceOverlayBridge.cs`; `src/WSGM/Overlay/OverlayWindow.Sources.cs`; `src/WSGM/Shell/SteamGraphicsService.cs`; `src/WSGM/Shell/ShellSession.Performance.cs`; `src/WSGM/Shell/NativeQam*Service.cs`; `tests`.
- Steps: STEAMHOST-B4 first, against the concrete DeviceCoordinator (critic conflict 4, no interim ports); the device batches then edit the split files. Split per steamhost.md section 4; move the profile and power-limit projections; the replacement projection keeps ObservedWatts and DesiredWatts for the OSD and only the slider mapping uses the ceiling fallback (021 corrected, V-008); one capability subscription; deduplicate range checks and the timeout constant (023); duplicated sound status strings (036).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~NativeQam|FullyQualifiedName~CapabilityProjection|FullyQualifiedName~AutoTdpService|FullyQualifiedName~DeviceOverlayBridge"`.
- Resolves: STEAMHOST-021, STEAMHOST-023, STEAMHOST-036, STEAMHOST-V-008.

#### B088 Extract DeviceCycle with minimal ports and real-owner tests

- Domain: device. Depends on: B083, B087.
- Files: src/WSGM/Shell/DeviceCycle.cs (new); `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/DevicePluginRuntime.cs`; `src/WSGM/Shell/ShellSession.cs`; tests/WSGM.Tests/Shell/DeviceCycleTests.cs (new); `DeviceCoordinatorConcurrencyTests.cs`; `DeviceIntegrationOffTests.cs`.
- Steps: DEVICE-B4: move the lifecycle symbols per _plan/refactor-2.1/review/device.md section 4 into DeviceCycle; ports only where a test needs them (package source, runtime loader, machine identity); lifecycle notifications enter the cycle lane (041); plugin settings attach in the lane (029); private helpers stop being test-only statics (042); integration off constructs no diagnostics or power loops. Tests with the fixture runtime: integration off makes zero runtime loads; two packages refuse with the diagnostic; start fault restarts twice then Faulted and Retry starts once; resume with a faulted cycle restarts; suspend then resume advances generation once; caller-cancelled start runs bounded cleanup and never auto-restarts (014).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCycle|FullyQualifiedName~DeviceCoordinator|FullyQualifiedName~DeviceIntegrationOff"`.
- Resolves: DEVICE-014, DEVICE-029, DEVICE-041, DEVICE-042.

#### B089 Desired-state restorer and user-write helpers; PL2 single home

- Domain: device. Depends on: B088. Decisions: D6.
- Files: src/WSGM/Shell/DeviceDesiredStateRestorer.cs (new); `src/WSGM/Shell/CapabilityUserWrites.cs`; `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/GpuCoordinator.cs`; `src/WSGM/Shell/CapabilityDesiredReconciler.cs`; `src/WSGM/Shell/DeviceDesiredWriteAdmission.cs`; `src/WSGM/Shell/DeviceLightingRestore.cs`; src/WSGM/Core/CapabilityValues.cs (new); `src/WSGM/Shell/DevicePowerAssignments.cs`; `src/WSGM/Shell/ApplicationPerformanceReconciler.cs`; `src/WSGM/Shell/DeviceOverlayBridge.cs`; NativeQam boost row; `src/WSGM/Core/ManualTdpProfile.cs`; `src/WSGM/Core/Profiles/ProfileFields.cs`; `src/WSGM/Core/Profiles/ProfileResolver.cs`; `src/WSGM/Shell/ProfileService.cs`; `tests`.
- Steps: DEVICE-B5 with device.verify corrections. PerformanceProfileOwnsRole and a HandleUserWriteAsync helper move into the existing static CapabilityUserWrites, used by both coordinators (010 corrected; no CapabilityCommandPolicy class). DeviceDesiredStateRestorer owns the restore pass, lighting readiness pass and authored fan profiles (032), keeps one ordered snapshot for ordering and reads each candidate fresh through TryGetView (V-005, 021 rest). CapabilityValues.Same and misplaced statics move (035). PL2 single home (008, D6): the device-stored PL2 entry becomes the only home of PL2 and BoostWatts goes. A user PL2 write keeps persisting the device desired entry as today; PersistManualBoostAsync stops writing BoostWatts and keeps only the switch to split mode; in split mode the reconciler restores the sustained target, then the game's device PL2 entry as boost; in unified mode the restore pass skips the stored PL2 entry; Use global on the boost row (overlay and native QAM) clears the game's device PL2 entry like every other device row; ProfileField.BoostWatts, ManualTdpProfile.BoostWatts and the resolver's boost read are deleted. Stored BoostWatts moves once in the cycle, when the first descriptor set with a writable PowerSlowLimit is published: a layer with only BoostWatts gets a device entry, a layer with both keeps the device entry and drops BoostWatts, then BoostWatts is cleared everywhere (no flag; B068 carries nothing for it). A custom preset assignment keeps winning while it is in force. Fixtures: BoostWatts-only layer, both values present with different values, custom assignment.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CapabilityUserWrites|FullyQualifiedName~DeviceLightingRestore|FullyQualifiedName~GpuCoordinator|FullyQualifiedName~DeviceProfileApplier|FullyQualifiedName~DeviceDesiredWrite|FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~DevicePowerAssignments|FullyQualifiedName~ProfileResolver|FullyQualifiedName~NativeQamPerformance"`.
- Resolves: DEVICE-008, DEVICE-010, DEVICE-032, DEVICE-035, DEVICE-V-005.

#### B090 Windows power statics become injected instances

- Domain: winsvc. Depends on: B017, B088.
- Files: `src/WSGM/Core/PowerSchemes.cs`; `src/WSGM/Core/CpuBoost.cs`; `src/WSGM/Core/HybridCores.cs`; `src/WSGM/Core/WindowsPowerModes.cs`; `src/WSGM/Core/PowerTimeouts.cs`; `src/WSGM/Interop/WindowsPowerSchemeApi.cs`; `src/WSGM/Overlay/OverlayController.cs`; `src/WSGM/Overlay/PowerSchemeSelection.cs`; `src/WSGM/Shell/NativeQamPowerProfileService.cs`; src/WSGM/Shell/SteamUiSessionHost.cs (ctor args); `src/WSGM/Shell/ShellSession.cs`; UiTests fakes.
- Steps: WINSVC-B2 corrected (critic conflict 13): the lock over the machine-global active scheme moves into the one injected PowerSchemes instance and the other owners (CpuBoost, HybridCores, WindowsPowerModes, PowerTimeouts) take that instance; delete the four Windows statics; no PowerPolicyLane type (009 narrowed, 030); the small duplicated Windows power port pieces fold together (DEVICE-040).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerScheme|FullyQualifiedName~CpuBoost|FullyQualifiedName~HybridCore|FullyQualifiedName~PowerTimeout|FullyQualifiedName~DisplayTimeout"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~HybridCoreView|FullyQualifiedName~DevicePageCapture".
- Resolves: WINSVC-009, WINSVC-030, DEVICE-040.

#### B091 PowerLimitOwner owns sustained, boost, presets and AutoTDP

- Domain: device. Depends on: B090, B089.
- Files: src/WSGM/Shell/PowerLimitOwner.cs (new); src/WSGM/Shell/CpuBoostReconciler.cs (new); `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/DevicePowerPresets.cs`; `src/WSGM/Shell/DevicePowerAssignments.cs`; `src/WSGM/Shell/ApplicationPerformanceReconciler.cs`; `src/WSGM/Shell/AutoTdpService.cs`; `src/WSGM/Shell/CommandOutcomeExtensions.cs`; `src/WSGM/Interop/EffectivePowerModeNotification.cs`; `src/WSGM/Shell/ShellSession{,.Performance,.Shutdown}.cs`; `src/WSGM/Shell/NativeQam/*`; `src/WSGM/Overlay/OverlayWindow.Sources.cs`; `src/WSGM/Shell/ShellSession.Actions.cs`; `src/WSGM/Shell/PerformanceOverlayBridge.cs`; `tests`.
- Steps: DEVICE-B6 (split into a power-owner and a reconciler/CPU-boost commit if the diff is large). Power symbols per device.md section 4 into PowerLimitOwner with one private lane replacing the public MutationGate and the AutomaticPowerOwner setter (009, 019, 020); presets and assignments get the lane and the AutoTDP-owns-power predicate through constructors; AutoTDP is constructed inside (026); Applied(int) removed, IsApplied only, and a verified mismatching paired readback counts as applied (012); restore record with string-free reconcile keys (037); reconciler state changes only inside its lane, CPU boost to CpuBoostReconciler under one gate (011); power-mode callback guarded with try/catch in the [UnmanagedCallersOnly] path (017, WINSVC-V-003); Windows power through the instances from B090. The AutoTDP exit restore becomes the explicit first step of the coordinator's shutdown task, before device stop.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AutoTdp|FullyQualifiedName~DevicePower|FullyQualifiedName~ApplicationPerformance|FullyQualifiedName~ManualTdp|FullyQualifiedName~NativeQamPerformance"`.
- Resolves: DEVICE-009, DEVICE-011, DEVICE-012, DEVICE-017, DEVICE-019, DEVICE-020, DEVICE-026, DEVICE-037, WINSVC-V-003.

#### B092 DeviceControllerHandoff owns controller start, loss and claims

- Domain: device. Depends on: B091, B074.
- Files: src/WSGM/Shell/DeviceControllerHandoff.cs (new); `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/ShellSession.cs`; `tests`.
- Steps: DEVICE-B7 as a move: handoff symbols per device.md section 4 (controller start on identity publication, cancel on suspend, target-loss recovery, management enable and disable, haptic sink, running-application controller state, UI claims, rear pulse). The stale-ledger show at start already landed in B009. Tests: suspend cancels an in-flight controller start; target loss shows the physical pad once.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceControllerHandoff|FullyQualifiedName~HidHide|FullyQualifiedName~ControllerManager"`.
- Resolves: structural move, no finding of its own (see Appendix A).

#### B093 Device facade, overlay contracts and small cleanups

- Domain: device. Depends on: B092.
- Files: `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Shell/DeviceOverlayBridge.cs`; src/WSGM/Shell/DeviceOverlayContracts.cs (new); `src/WSGM/Shell/SimulatedDeviceOverlaySource.cs`; `tests/WSGM.UiTests/Fakes/FakeDevice.cs`; `src/WSGM/Shell/DevicePowerPresets.cs`; `src/WSGM/Shell/NativeQamPowerPresetService.cs`; `src/WSGM/Core/PerformanceService.cs`; `src/WSGM/Shell/PluginHost.cs`; `src/WSGM/Shell/DeviceProfileApplier.cs`; `docs/device-integration.md`.
- Steps: DEVICE-B8: coordinator persistence through the config store port (018); DeviceOverlayBridge contracts to their own file (022); IDeviceOverlaySource loses its default members and the UiTests FakeDevice is edited in the same batch (023); dangling summary (024); preset result mapping without the toolkit type and magic id (025 host side, U03A-SUTS-005); PerformanceService receives its launcher and stops truncating tokens (033); persistence-order docs (034); visibility (036); PluginHost per-instance state ownership (039).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceOverlayBridge|FullyQualifiedName~DevicePowerPresets|FullyQualifiedName~PerformanceService|FullyQualifiedName~PluginHost"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DevicePageCapture".
- Resolves: DEVICE-018, DEVICE-022, DEVICE-023, DEVICE-024, DEVICE-025, DEVICE-033, DEVICE-034, DEVICE-036, DEVICE-039.

#### B094 Windows services correctness fixes and shutdown disposal order

- Domain: winsvc. Depends on: B071.
- Files: `src/WSGM/Shell/RemovableDriveManager.cs`; `src/WSGM/Shell/RemovableDriveEntries.cs`; src/WSGM/Shell/SdFormatManager.cs (target snapshot); `src/WSGM/Core/RtssOsd.cs`; `src/WSGM/Core/RtssNativeAdapter.cs`; `src/WSGM/Shell/CardAcfWatcher.cs`; `src/WSGM/Shell/CardVolumeMonitor.cs`; `src/WSGM/Shell/AudioManager.cs`; `src/WSGM/Shell/DisplayOffMuteService.cs`; `src/WSGM/Interop/NativeMethods.cs`; `src/WSGM/Interop/NativeStorage.cs`; `src/WSGM/Shell/LibraryPolicy.cs`; `src/WSGM/Shell/ShellSession.Shutdown.cs`; `tests`.
- Steps: WINSVC-B1 corrected. WINSVC-006 first: FormatAsync copies an immutable target record and uses only it (a late Apply can no longer retarget a swapped card). WINSVC-001: RtssOsdMetricsSource.Sample under a Lock. WINSVC-008: renderer started by RtssNativeAdapter after construction; Dispose cancels, awaits the loop with its 2 s bound, then releases the slot (no Volatile step). WINSVC-002: EjectAsync runs EjectedAsync(false) in finally (tested through the real LibraryPolicy over temp paths). WINSVC-005: rows carry letters as data, SplitLetters deleted. WINSVC-016: CardAcfWatcher keeps watching every ready drive (maintainer decision) and its class comment says so; its suspension needs no guard, since both callers hold it in using scopes. WINSVC-018: AudioManager.Refresh posts to the UI thread. WINSVC-026: ProcessExit handler unsubscribed. WINSVC-040/041: dead code and duplicate GUID. WINSVC-042: setters raise their own names. WINSVC-V-001: a card pass waits on the gate instead of being dropped. WINSVC-V-004: AudioManager, RadioManager, RemovableDriveManager, storage bridge and card monitors dispose on the UI thread before the MessageWindow (C8). WINSVC-V-007: grow the drive-layout buffer on ERROR_INSUFFICIENT_BUFFER. LockScreenSettings is not changed (WINSVC-034 refuted).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive|FullyQualifiedName~RtssOsd|FullyQualifiedName~SdFormat|FullyQualifiedName~RadioManager|FullyQualifiedName~AudioManager|FullyQualifiedName~CardVolume|FullyQualifiedName~CardAcf"`.
- Resolves: WINSVC-001, WINSVC-002, WINSVC-005, WINSVC-006, WINSVC-008, WINSVC-016, WINSVC-018, WINSVC-026, WINSVC-040, WINSVC-041, WINSVC-042, WINSVC-V-001, WINSVC-V-004, WINSVC-V-007.

#### B095 One console runner; other-manager takeover as an instance

- Domain: winsvc. Depends on: B094, B039.
- Files: `src/WSGM/Core/ConsoleTool.cs`; `src/WSGM/Core/AutostartSystem.cs`; `src/WSGM/Core/UnelevatedLauncher.cs`; `src/WSGM/Core/OtherManagers.cs`; `src/WSGM/Program.cs`; `src/WSGM/Shell/ShellSession.cs`; Settings call sites; `src/WSGM.Setup/UI/Pages/ProfilePage.cs`; `src/WSGM/Core/Installer.cs`; `tests`.
- Steps: WINSVC-B3 corrected: one RunAsync with a tri-state ConsoleToolResult replaces Run and RunCapturedAsync (015); OtherManagerTakeover instance with injected ports (014); RestoreAll uses the strict read and returns 1 on unreadable config without writing (013, U04B-LFA-004); the session tracks the start-up ReapplyAtStart task and joins it only inside the shutdown deadline, with cancellation checked between items so CloseWindows and sc.exe never extend shutdown.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ConsoleTool|FullyQualifiedName~OtherManagers|FullyQualifiedName~UnelevatedLauncher|FullyQualifiedName~Autostart"`.
- Resolves: WINSVC-013, WINSVC-014, WINSVC-015, U04B-LFA-004.

#### B096 StorageInventory: one storage snapshot for every consumer

- Domain: winsvc. Depends on: B095, B084.
- Files: src/WSGM/Shell/StorageInventory.cs (new); `src/WSGM/Shell/RemovableDriveManager.cs`; `src/WSGM/Shell/RemovableDriveEntries.cs`; `src/WSGM/Shell/CardVolumeMonitor.cs`; `src/WSGM/Shell/CardAcfWatcher.cs`; `src/WSGM/Shell/SteamStorageBridge.cs`; `src/WSGM/Shell/LibraryPolicy.cs`; `src/WSGM/Shell/SdFormatManager.cs`; `src/WSGM/Interop/NativeStorage.cs`; `tests`.
- Steps: WINSVC-B4: one reader produces the inventory snapshot; eject, format, Steam storage bridge and library paths become projections of it; delete the duplicate walks; the eject list keeps its cached system disks inside the inventory (004). The storage bridge's UI-thread snapshot from B084 now projects from the inventory. Tests: an unlettered media row produces no paths; eject, format and Steam projections agree on one fixture; a card swap mid-pass is still abandoned.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~RemovableDrive|FullyQualifiedName~SteamStorageBridge|FullyQualifiedName~CardLibrary|FullyQualifiedName~CardName|FullyQualifiedName~SdFormat"`.
- Resolves: WINSVC-004.

#### B097 SD format run split with ports

- Domain: winsvc. Depends on: B096.
- Files: src/WSGM/Shell/SdFormatManager.cs -> SdFormatTargets.cs, SdFormatRun.cs, SteamLibraryRegistration.cs; `src/WSGM/Shell/SdFormatEntries.cs`; `src/WSGM/Overlay/OverlayController.cs`; `src/WSGM/Shell/SteamStorageBridge.cs`; `src/WSGM/Shell/ShellSession.cs`; `src/WSGM/Core/SteamLibraryVdf.cs`; `tests`.
- Steps: WINSVC-B5: move symbols per winsvc.md section 4; ports IDiskpart, IDiskIdentity, ISteamLibraryRegistration and IClock; library writes through the volume GUID path (035); async removal (036); the diskpart Unknown outcome maps to the 'may have been erased' message and runs the card-database retirement (015 rest); keep every reverification point and the three-attempt format; the marker read moves out of SteamLibraryVdf so the parser stays pure (LIBRARY-023). Tests: reverification before each destructive stage, identity change before clean restores the registration once, after clean does not compensate, unknown diskpart outcome gives the uncertain message, a letter-keep failure stops. Manual M01-35/36 on spare media only.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SdFormat|FullyQualifiedName~SteamLibraryVdf"`.
- Resolves: WINSVC-007, WINSVC-035, WINSVC-036, LIBRARY-023.

#### B098 Display mode services lose their test-only branches

- Domain: winsvc. Depends on: B069, B094.
- Files: `src/WSGM/Core/RefreshRatePairingService.cs`; `src/WSGM/Core/DisplayResolutionService.cs`; `src/WSGM/Core/DisplayProfiles.cs`; `src/WSGM/Core/EdidModes.cs`; `tests`.
- Steps: WINSVC-012 only (critic conflict 10; WINSVC-011 refuted and config B7 dropped): give the harness an operating-point fake and delete the 'test' key and null-operating-point production branches. The two original-mode owners and their revision guard stay. Rename DisplayProfiles and delete EdidModes in favour of WDC DisplayEdid only if WDC already exposes the advertised rates after B069; otherwise fix the stale DisplayProfiles doc only (CONFIG-028, CONFIG-029, WINSVC-027).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~FrameLimitPairing|FullyQualifiedName~RefreshRatePairing|FullyQualifiedName~DisplayResolutionService"`.
- Resolves: WINSVC-012, WINSVC-027, CONFIG-028, CONFIG-029.

#### B099 One audio endpoint port; display-off mute restores the muted endpoint

- Domain: winsvc. Depends on: B066, B094.
- Files: `src/WSGM/Shell/AudioProfileService.cs`; `src/WSGM/Shell/AudioManager.cs`; `src/WSGM/Shell/DisplayOffMuteService.cs`; `src/WSGM/Shell/VolumeButtonService.cs`; src/WSGM/Core/VolumeFeedback.cs -> VolumeFeedbackPlayer.cs; `src/WSGM/Shell/RadioManager.cs`; `src/WSGM/Settings/AudioProfileEditor.cs`; ShellSession composition and shutdown; NativeQam audio services; `tests`.
- Steps: WINSVC-B7 with the final id-based restore (B066 has landed): one IAudioEndpoints adapter used by the six consumers (020); the mute owner records the endpoint id and restores that endpoint with no read before or after the unmute, and a failure to dispatch keeps the claim for the existing 2 s tick (003, D9); volume buttons run on the audio write worker, never the dispatcher (019); VolumeFeedback becomes an instance disposed at shutdown (009 VolumeFeedback part).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~AudioManager|FullyQualifiedName~AudioProfile|FullyQualifiedName~DisplayMute|FullyQualifiedName~VolumeAppCommands|FullyQualifiedName~VolumeOsd|FullyQualifiedName~BluetoothAction"`.
- Resolves: WINSVC-003, WINSVC-019, WINSVC-020.

#### B100 RTSS discovery and shared-memory caps

- Domain: winsvc. Depends on: B005, B094.
- Files: `src/WSGM/Core/RtssFrametimeReader.cs`; `src/WSGM/Core/RtssOsd.cs`; `src/WSGM/Core/RtssModels.cs`; `src/WSGM/Core/RtssDiscovery.cs`; `src/WSGM/Core/RtssNativeAdapter.cs`; `tests`.
- Steps: WINSVC-B9 reduced per verify: remove the 32 MiB executable cap, the 4,096-export, 128-byte name and 1,024-entry caps; the LHM sensor XML reads to the view capacity (V-006); RTSS process path from the limited image-name query instead of MainModule (032); the frametime reader keeps its own read-only mapping, independent of the OSD writer's read-write open; no reusable-buffer rewrite or allocation assertion on the 1 Hz path (031 caps only). Authenticode keeps online revocation; confirm the probe runs off any command admission path (033).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Rtss|FullyQualifiedName~AutoTdpService"`.
- Resolves: WINSVC-031, WINSVC-032, WINSVC-033, WINSVC-V-006.

#### B101 Remaining Windows service owner tests and card monitor polling

- Domain: winsvc. Depends on: B097, B099.
- Files: `src/WSGM/Shell/KeepAwakeService.cs`; `src/WSGM/Shell/ModernStandbyGuard.cs`; `src/WSGM/Shell/CardVolumeMonitor.cs`; `src/WSGM/Shell/CardAcfWatcher.cs`; `src/WSGM/Core/OtherManagers.cs`; `src/WSGM/Shell/SystemStatus.cs`; src/WSGM/Overlay/OverlayController.cs (preview SdFormatManager); `tests`.
- Steps: WINSVC-B11: KeepAwakeService loop joined on dispose with a fake download port (024); ModernStandbyGuard over a standby port reusing W02_02 semantics (025); card monitor and ACF watcher over fake inventories; OtherManagers Apply; delete constant pins and move misplaced VDF tests (039). WINSVC-V-002: remove CardVolumeMonitor's 3 s self-reschedule and kick from Steam start, master-switch enable and Steam UI readiness. WINSVC-038: preview compositions pass the session instances or fakes instead of building live managers. WINSVC-043: visibility. WINSVC-046: stale doc comment.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~KeepAwake|FullyQualifiedName~ModernStandby|FullyQualifiedName~CardVolume|FullyQualifiedName~CardAcf|FullyQualifiedName~OtherManagers"`.
- Resolves: WINSVC-024, WINSVC-025, WINSVC-038, WINSVC-039, WINSVC-043, WINSVC-046, WINSVC-V-002.

#### B102 Artwork providers take their handler and gate as arguments

- Domain: library. Depends on: B053.
- Files: `src/WSGM/Core/Artwork/*`; `src/WSGM/Shell/GameLibraryArtwork.cs`; `src/WSGM/Shell/ShellSession.cs`; `src/WSGM/Core/Library/XboxLibrarySource.cs`; `tests`.
- Steps: LIBRARY-B2 in the smaller form (library.verify batch problem 8): provider classes keep their shape; HttpMessageHandler and ArtworkRequestGate become constructor arguments with today's values as defaults; the static SteamGridDb adapter folds into SteamGridDbProvider (010); delete unfiltered overloads and forwarding defaults (015); HasMore from the unfiltered provider page, since the provider seam changes here (011); slot vocabulary in one place (016); prune expired failures on insert (037); internal visibility where touched (039). Parsing tests for SteamGridDB and Screenscraper over a fake handler replace the declaration test.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"`.
- Resolves: LIBRARY-010, LIBRARY-011, LIBRARY-015, LIBRARY-016, LIBRARY-037, LIBRARY-039.

#### B103 Artwork browser keeps its pages across unrelated config reloads

- Domain: library. Depends on: B102, B039.
- Files: `src/WSGM/Shell/SteamArtworkBrowserSource.cs`; src/WSGM/Core/Artwork/SteamArtwork.cs -> SteamArtworkWriter; `src/WSGM/Shell/ShellSession{,.Config,.Shutdown}.cs`; tests/WSGM.Tests/Shell/SteamArtworkBrowserSourceTests.cs (new).
- Steps: LIBRARY-B3 corrected: compute managed slots outside _gate, MIME from ImageFormat, shared 16 MiB constant (006 artwork half); cancel without disposing the CTS or check the disposed flag at each entry, no tracked task set (012); CancelBrowsing raises Changed (038 part); LIBRARY-V-002: compare the artwork section (credentials, tab visibility and order) with the last one seen, reset caches and reopen only when it changed, once from the parent, and reopen off the dispatcher; LIBRARY-V-003: one slot-to-file rule shared by apply, clear and find including .ico, and a shortcut reset deletes the file WSGM wrote (the store-title case waits for attended evidence). Tests: open, select, load-more, stale generation dropped, dispose during load, a reload that changes nothing artwork-related keeps the page.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"`.
- Resolves: LIBRARY-006, LIBRARY-012, LIBRARY-038, LIBRARY-V-002, LIBRARY-V-003.

#### B104 GameLibraryService pure extractions

- Domain: library. Depends on: B103.
- Files: `src/WSGM/Shell/GameLibraryService.cs`; src/WSGM/Core/Library/LibraryReviewEntry.cs (new); src/WSGM/Shell/GameLibraryProjection.cs (new); `src/WSGM/Shell/GameLibraryState.cs`; `src/WSGM/Core/Library/ImportPlan.cs`; `src/WSGM/Core/Library/ShortcutRoute.cs`; `tests`.
- Steps: LIBRARY-B4a: move Entry and its rules and the projection out; delete dead code (028, GameLibrarySteamTarget, ImportPlan.Matches, ShortcutRoute.Compose); share the options body (038 rest). No behaviour change; GameLibraryServiceTests unchanged and green; WSGM.UiTests Game Library overlay captures run.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~GameLibrary".
- Resolves: LIBRARY-028.

#### B105 GameLibraryService workers, Steam port, threading and lifetime

- Domain: library. Depends on: B104.
- Files: `src/WSGM/Shell/GameLibraryService.cs`; src/WSGM/Shell/GameLibraryScan.cs (new); src/WSGM/Shell/GameLibraryApply.cs (new); src/WSGM/Shell/GameLibraryCollections.cs (new); `src/WSGM/Shell/ShellSession{,.GameLibrary,.Config,.Shutdown}.cs`; `src/WSGM/Core/Library/SteamShortcutWriter.cs`; `src/WSGM/Overlay/ServiceSubView.cs`; `tests`.
- Steps: LIBRARY-B4b corrected: extract scan, apply and collection sync; the 18 constructor parameters become a Steam port plus a settings snapshot, and the library no longer writes the live config object from a worker (009); choices persist under _gate so edit order is write order, while ServiceSubView starts the backend call on a worker to keep disk IO off the UI thread (006 library half); Changed is raised after the lock (020); store records are copied on save instead of shared, without changing ImportedEntry to a record (007); Start and collection tasks are tracked and DisposeAsync takes the caller's deadline, replacing the fixed 5 s blocking dispose (008). The per-entry re-read, record-before-slower-work and no-rollback rules move byte for byte. UI captures for the Game Library overlay run.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~GameLibrary".
- Resolves: LIBRARY-006, LIBRARY-007, LIBRARY-008, LIBRARY-009, LIBRARY-020, LIBRARY-036.

#### B106 A Steam shortcut is owned by one title

- Domain: library. Depends on: B105. Decisions: D5.
- Files: `src/WSGM/Core/Library/ImportPlan.cs`; `src/WSGM/Shell/GameLibraryApply.cs`; `tests`.
- Steps: LIBRARY-002: ImportPlan.Build claims adopted AppIds so two sources cannot adopt one shortcut. LIBRARY-V-001: the apply's Add branch considers only shortcuts no other title's record claims (ApplyCoreAsync already holds and updates records). The title whose route resolves to a shortcut another title owns is offered as a second game shortcut: its own Add, which creates its own Steam shortcut (D5). Tests: two sources and one shortcut give one Adopt; a GOG title and a .lnk to the same exe never end with two records on one AppId.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"`.
- Resolves: LIBRARY-002, LIBRARY-V-001.

#### B107 Library sources and helpers

- Domain: library. Depends on: B106.
- Files: `src/WSGM/Core/Library/Sources/*.cs`; `src/WSGM/Core/Library/StoreCatalogClient.cs`; `src/WSGM/Core/LaunchWrapperCommand.cs`; `src/WSGM/Core/Steam.cs`; `src/WSGM/Core/Library/PackagedLauncherShortcut.cs`; src/WSGM/Core/PackagedLaunchCommand.cs (linked into WSGM.PackagedLaunch); `src/WSGM/Interop/ShellLink.cs`; `src/WSGM/Core/Library/XboxManifest.cs`; `src/WSGM/Core/Library/MicrosoftGameConfig.cs`; `src/WSGM/Shell/RunningApplicationTarget.cs`; `tests/WSGM.Tests/Core/Library/*`.
- Steps: LIBRARY-B6 trimmed (no LibraryDisk rewrite, LIBRARY-029 no-change): an oversized launcher file is a reported source failure, not absent (018); one HttpClient and broader lookup failure handling in StoreCatalogClient (021), and drop the URL-suffix image filter only after one real catalog answer or log line shows extensionless URIs (V-004); move and rename StopRunningHelpers (026); one sibling-executable helper (027 rest); the command-line limit replaces 2048 in compose, refusal and the launcher's own parse check, and the batch builds WSGM.PackagedLaunch too (032); character-named manifest constant; RunningApplicationMonitor starts explicitly, snapshots _profile under its lock and drops the 128 cap (022). Golden tests stay green.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Library|FullyQualifiedName~Import|FullyQualifiedName~Artwork|FullyQualifiedName~SteamGridDb|FullyQualifiedName~Xbox|FullyQualifiedName~StoreCatalog|FullyQualifiedName~MicrosoftGameConfig|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~CommandRoute|FullyQualifiedName~SteamShortcutWriter|FullyQualifiedName~LaunchWrapperCommand|FullyQualifiedName~RunningApplication|FullyQualifiedName~LauncherSource|FullyQualifiedName~ShortcutFolderSource|FullyQualifiedName~BattleNetProductDatabase|FullyQualifiedName~ForegroundApplicationFilter|FullyQualifiedName~SteamCustomLaunchCommand"; dotnet build src/WSGM.PackagedLaunch -c Release.
- Resolves: LIBRARY-018, LIBRARY-021, LIBRARY-022, LIBRARY-026, LIBRARY-032, LIBRARY-V-004.

#### B108 Overlay defect corrections that need no new owners

- Domain: overlay. Depends on: none.
- Files: `src/WSGM/Overlay/OverlayWindow.Device.cs`; `src/WSGM/Overlay/GlyphInputTestMap.cs`; `src/WSGM/Overlay/CommonPluginPanel.cs`; `src/WSGM/Overlay/ProfileOverrideMarker.cs`; `src/WSGM/Overlay/AudioPanel.axaml.cs`; `src/WSGM/Overlay/OverlayController.cs`; `src/WSGM/Overlay/OverlayWindow.PowerEditors.cs`; `src/WSGM/Overlay/OverlayWindow.Storage.cs`; `src/WSGM/Overlay/ThemesView.Tools.cs`; `src/WSGM/Overlay/AnimationsView.Tools.cs`; `src/WSGM/Overlay/ArtworkView.cs`; `tests`.
- Steps: OVERLAY-B1 with overlay.verify corrections. 004: compare the raw CanonicalButtons plus the two trigger booleans with the previous sample and map to glyphs only on change (no per-sample HashSet). 021: keep the plugin's detail text when present and relabel only detail-less outcomes. 029: containment for async-void paths. 003: timeout editors are read-only keyed on _previewOnly, covering the overlay-test composition with real DisplayTimeouts; then delete the fallback branch. 027 and V-003: the native pickers stay (maintainer decision) and the folder pickers pair with SystemDialogActive and return when the sheet closed meanwhile. 015: Animations fetch on tab entry (the only loop is an empty repository). 018: the logo tab offers official, white, black, custom like the Steam page. 041 is dropped (no reproduced loss).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay|FullyQualifiedName~WSGM.Tests.Controls"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay".
- Resolves: OVERLAY-003, OVERLAY-004, OVERLAY-015, OVERLAY-018, OVERLAY-021, OVERLAY-027, OVERLAY-029, OVERLAY-V-003.

#### B109 Overlay surface host replaces process globals

- Domain: overlay. Depends on: B108.
- Files: src/WSGM/Overlay/IOverlaySurfaceHost.cs (new); src/WSGM/Overlay/OverlaySurfaceHost.cs (new); `src/WSGM/Overlay/OverlayWindow.Surfaces.cs`; `src/WSGM/Overlay/OverlayWindow.axaml.cs`; `src/WSGM/Overlay/OverlaySubView.cs`; `src/WSGM/Overlay/ServiceSubView.cs`; `src/WSGM/Overlay/DeviceControlRows.cs`; `src/WSGM/Overlay/CommonPluginPanel.cs`; `src/WSGM/Overlay/OverlayWindow.Storage.cs`; `src/WSGM/Overlay/OverlayController*.cs`; src/WSGM/Core/KeyboardService.cs (deleted); `StatusPanel.cs`; `FluentExtensions.cs`.
- Steps: OVERLAY-B2: views get the surface host at attach; SharedSession becomes per controller; KeyboardService, StatusPanel and FluentExtensions statics go (014). New TwoSheetsKeepTheirOwnTextEntry plus existing keyboard and surface tests.
- Tests: dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~KeyboardEditingTests|FullyQualifiedName~OverlaySurfaceTests|FullyQualifiedName~UtilitySurfaceTests".
- Resolves: OVERLAY-014.

#### B110 Overlay navigation controller without depth cap or render-cycle guards

- Domain: overlay. Depends on: B109.
- Files: src/WSGM/Overlay/OverlayNavigationController.cs (new); `src/WSGM/Overlay/OverlayNavigation.cs`; `src/WSGM/Overlay/OverlayWindow.Navigation.cs`; `src/WSGM/Overlay/OverlayWindow.Workspace.cs`; `src/WSGM/Overlay/OverlayWindow.Rail.cs`; `src/WSGM/Overlay/OverlayWindow.Header.cs`.
- Steps: OVERLAY-B6, depending only on the surface host (overlay.verify batch problem 10): Back(BackContext) owns back and destination policy (022); break the render cycle and delete both guards and MaximumDepth (023); typed keys with unchanged strings (034). Visual captures identical.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayNavigationTests"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests|FullyQualifiedName~OverlayInteractionTests"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual".
- Resolves: OVERLAY-022, OVERLAY-023, OVERLAY-034.

#### B111 Startup options, application runtime and crash-loop breaker

- Domain: session. Depends on: B006.
- Files: `src/WSGM/Program.cs`; src/WSGM/StartupOptions.cs (new); src/WSGM/StartupCommands.cs (new); src/WSGM/Core/CrashLoopBreaker.cs (new); src/WSGM/Core/ApplicationRuntime.cs (new); src/WSGM/Core/ApplicationShutdown.cs (deleted); `src/WSGM/App.axaml.cs`; `src/WSGM/Shell/ShellSession.cs`; `src/WSGM/Shell/ShellSession.Shutdown.cs`; `tests`.
- Steps: SESSION-B1 rebuilt on the B006 fix (session.verify batch problems 1-4): parse StartupOptions once (Program.Mode, ServiceBoot, DesktopResident statics deleted, 001); one-shots move verbatim with their order (005 restore-shell wait unchanged); ApplicationRuntime owns the exit sequence: reason with priority and sticky SessionEnd, one shutdown task, deadline that escalation only tightens, outcome, sticky startup failure, exit code and a single handoff report; it runs the session shutdown and then the forced lifetime exit; ShutdownRequested is only the OS end-session input. Crash-loop reset only when started and exit 0; CrashLoopBreaker an instance over a directory and clock. Pre-stop runs on the watcher thread (004); a Settings process does not run the Steam pre-stop (003 residual). ApplyLogVerbosity try/catch removed (006); ShellSession.DisposeAsync budget and non-atomic disposed flag go (008, 031). Tests: StartupOptions precedence, ApplicationRuntime escalation and same-task repeat, CrashLoopBreaker on a temp dir and fake clock.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~StartupOptions|FullyQualifiedName~ApplicationRuntime|FullyQualifiedName~CrashLoopBreaker|FullyQualifiedName~ModeSelection"`.
- Resolves: SESSION-001, SESSION-004, SESSION-005, SESSION-006, SESSION-008, SESSION-031.

#### B112 Explorer primitives behind ports with one launcher

- Domain: session. Depends on: B111.
- Files: src/WSGM/Core/ExplorerControl.cs -> ExplorerShellProbe.cs, ExplorerLauncher.cs; `src/WSGM/Shell/ExplorerDesktopHost.cs`; `src/WSGM/Core/ExplorerShellAnchor.cs`; `src/WSGM/Interop/NativeShellProcess*.cs`; src/WSGM/Core/Steam.cs (ColdStart scheduler); `src/WSGM/Core/DesktopAppProcessBackend.cs`; IsDesktopShellRunning consumers; `tests`.
- Steps: SESSION-B2: probe through NativeShellProcess.TryGetImagePath, no MainModule (035); exit loop and retired-shell state into the host behind IExplorerNative, instance state instead of a static lock (034, 038); never-throw exit contract (036); StopFailedChildAsync disposes each resource independently (039); one ExplorerLauncher with the tri-state scheduler used by the host and the terminal restore-shell and crash-loop paths (037, U04B-LFA-001); Steam ColdStart uses the tri-state result and never starts a competing Steam after Unknown (055); clear desktop-app suspension on a failed restore (040); sync-over-async Dispose wrappers removed (041); high-integrity shell refusal kept and its limitation documented (042). CRIT-004: DesktopAppProcessBackend uses the same image-path probe, the elevated restart path catches a declined UAC and its poll honours cancellation. Explorer is only ever asked to exit with 0x5B4 and WM_CLOSE, never killed. Attended restore-shell and crash-loop pass on the notebook and the Claw.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Explorer|FullyQualifiedName~DesktopAppProcess"`.
- Resolves: SESSION-034, SESSION-035, SESSION-036, SESSION-037, SESSION-038, SESSION-039, SESSION-040, SESSION-041, SESSION-042, SESSION-055, CRIT-004, U04B-LFA-001.

#### B113 Overlay activation sources out of the controller

- Domain: overlay. Depends on: B110, B079.
- Files: src/WSGM/Overlay/OverlayActivation.cs (new); `src/WSGM/Overlay/TouchSwipeMonitor.cs`; `src/WSGM/Overlay/OverlayController.cs`; src/WSGM/Overlay/OverlayController.Gestures.cs (deleted); `src/WSGM/Shell/ShellSession.cs`; `src/WSGM/Settings/SettingsWindow.axaml.cs`; `tests`.
- Steps: OVERLAY-B3 limited to activation (critic conflict 25): OverlayActivation owns hotkey, chord and swipe construction; the in-session Settings preview composes no activation (002 preview half); --overlay-test keeps hotkey, chord and swipe because they are its only reopen path (C4 corrected). The Steam-exit 'show overlay' reaction stays reachable. The relaunch and session-policy move waits for B124 (B125).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayActivation|FullyQualifiedName~QuickAccessSheet"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay".
- Resolves: OVERLAY-002.

#### B114 One owned MessageWindow; tray retirement verified

- Domain: session. Depends on: B112, B113.
- Files: `src/WSGM/Interop/MessageWindow.cs`; `src/WSGM/Shell/TrayHost.cs`; `src/WSGM/Core/TrayProtocol.cs`; `src/WSGM/Shell/SettingsActivation.cs`; `src/WSGM/Shell/SessionActivation.cs`; `src/WSGM/Core/WindowFinder.cs`; `src/WSGM/Shell/CardAcfWatcher.cs`; `src/WSGM/Shell/RemovableDriveManager.cs`; `src/WSGM/Shell/DisplayChangeWindow.cs`; `src/WSGM/Overlay/OverlayController.cs`; src/WSGM/Program.cs (Panic); `tests`.
- Steps: SESSION-B3 plus the MessageWindow part of WINSVC-B10 (critic conflict 14): one MessageWindow instance owned by the composition root, UI-thread affinity, disposed last, consumers take it by constructor; Create() removed (043, WINSVC-017); WndProc exception guard (044); DisplayChangeWindow owner-only construction; TrayHost.Retire() verifies IsWindow (045); Panic destroys the tray synchronously when it runs on the UI thread and skips otherwise (046 corrected); today's tray relay stays unchanged with no added note (SESSION-048 is dropped by maintainer decision, security theater); TrayProtocol cbSize upper bound removed (049); SessionActivation disposal flag (050); WindowFinder warn set locked (051) and stack buffers (052), doc fix (053); Local\WSGM.Activate created with the exit-event SDDL helper, name unchanged (V-004). No HWND user-data or reference-counted claim objects.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Tray|FullyQualifiedName~MessageWindow|FullyQualifiedName~WindowFinder"`.
- Resolves: SESSION-043, SESSION-044, SESSION-045, SESSION-046, SESSION-049, SESSION-050, SESSION-051, SESSION-052, SESSION-053, SESSION-V-004, WINSVC-017.

#### B115 Game Mode transition backend collapsed; Steam process port

- Domain: session. Depends on: B114, B042, B076.
- Files: src/WSGM/Shell/GameModeEntryServices.cs (deleted); src/WSGM/Shell/SessionEntryBackend.cs (new); `src/WSGM/Shell/GameModeEntryTransaction.cs`; `src/WSGM/Shell/SessionModes.cs`; `src/WSGM/Shell/DesktopReturnSequence.cs`; src/WSGM/Core/Steam.cs -> SteamInstallation.cs, SteamSessionControl.cs, BigPictureShortcuts.cs; `src/WSGM/Shell/SteamMonitor.cs`; `src/WSGM/Shell/BootSplash.cs`; `src/WSGM/Core/DisplayScale.cs`; `consumers`; `tests`.
- Steps: SESSION-B4 with batch problem 6: one IGameModeEntryBackend (022); result-based recovery with one warning (023); a refusal before change unpauses the monitor (024); atomic entry cancellation state (025); desktop actions reuse PluginActionSequence (029, no SessionActionRunner); SessionModes takes its hooks in the constructor (063); the pending return layout has one source of truth (016); Steam split into SteamInstallation (static discovery), SteamSessionControl (instance port) and BigPictureShortcuts (056); IsRunning semantics unchanged and ColdStart's shim step stays gated on both process names (SESSION-054 needs log evidence); DisplayScale persists through the store instead of mutating the live config (WINSVC-037). Compensation tests run over the real DesktopReturnSequence (059, 060).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GameModeEntryTransaction|FullyQualifiedName~SessionModes|FullyQualifiedName~DesktopReturnSequence|FullyQualifiedName~DesktopReturnRecovery|FullyQualifiedName~SteamSessionControl"`.
- Resolves: SESSION-016, SESSION-022, SESSION-023, SESSION-024, SESSION-025, SESSION-029, SESSION-056, SESSION-059, SESSION-060, SESSION-063, WINSVC-037.

#### B116 Session power queue and config reloader as owners

- Domain: session. Depends on: B115, B040, B041.
- Files: src/WSGM/Shell/SessionPowerQueue.cs (new); src/WSGM/Shell/SessionConfigReloader.cs (new); src/WSGM/Shell/ShellSession.Power.cs (deleted); src/WSGM/Shell/ShellSession.Config.cs (deleted); `src/WSGM/Shell/ShellSession.cs`; `tests`.
- Steps: SESSION-B5: SessionPowerQueue extracted verbatim with the 2 s stale-suspend rule, WhenIdleAsync tail and an admission flag; SessionConfigReloader owns watcher and debounce (a late watcher event after shutdown cannot recreate the timer, CONFIG-V-006), keeps the last good snapshot on any non-Loaded outcome, applies an ordered TryApply list with per-step isolation (018), applies modes config directly (026), applies verbosity (with B041), routes Profiles to ProfileService.ReloadAsync and tracks ApplyCommonPluginConfigAsync (CONFIG-046). Tests: lock and suspend coalesce, opposite edge cancels, faulted cycle repaired on resume, stale suspend dropped, stop refuses and joins; unreadable keeps snapshot, a throwing step leaves later steps running, stale generation dropped.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionPowerQueue|FullyQualifiedName~SessionConfigReloader|FullyQualifiedName~SystemPowerTransition"`.
- Resolves: SESSION-018, SESSION-026, CONFIG-046, CONFIG-V-006.

#### B117 Settings services required and complete; no production fallbacks

- Domain: settings. Depends on: B013, B039, B077.
- Files: src/WSGM/Settings/SettingsServices.cs (new); `src/WSGM/Settings/SettingsViewModel*.cs`; `src/WSGM/Settings/SettingsWindowServices.cs`; `src/WSGM/Settings/AudioProfileEditor.cs`; src/WSGM/Settings/SettingsExternalApply.cs (new); tests/WSGM.Tests/Builders/SettingsTestServices.cs (new); `tests/WSGM.Tests/Settings/*`; `tests/WSGM.Tests/Overlay/QuickAccessSheetTests.cs`; `tests/WSGM.UiTests/Infrastructure/UiFixture.cs`.
- Steps: SETTINGS-B3 without the display discovery change: move the record, make every member required, add update, package, repair, plugin-action, test-sheet and load-persisted members; delete the '?? production' fallbacks and the convenience constructors (005, 006); one SettingsExternalApply used by the post-save step and the takeover buttons (008); SavedAccentColor feeds ReadSavedAccent with no store read on close (012); DescribeOtherManagers reads through LoadPersisted on the worker (013); the post-save shim, Steam autostart and other-manager apply runs only when its persisted field changed in this save, compared with the existing baseline (SETTINGS-V-004; the takeover buttons still re-check). No ConfigStore.Load calls remain in the view model or window.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings".
- Resolves: SETTINGS-005, SETTINGS-006, SETTINGS-008, SETTINGS-012, SETTINGS-013, SETTINGS-V-004.

#### B118 Settings discovers displays on the worker in every composition

- Domain: settings. Depends on: B117.
- Files: `src/WSGM/Settings/SettingsViewModel.Launch.cs`; `src/WSGM/Settings/SettingsViewModel.cs`; tests/WSGM.UiTests (GameModeDisplayPageTests and display captures).
- Steps: SETTINGS-007 split out (settings.verify batch problem 6): delete _queryDisplaysOnWorker and the synchronous constructor branch; internal Task StartDisplayDiscoveryAsync that UiFixture awaits before capture; every UI test that renders displays awaits it; baselines unchanged.
- Tests: dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~GameModeDisplayPageTests|FullyQualifiedName~WSGM.UiTests.Settings"; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings".
- Resolves: SETTINGS-007.

#### B119 Closing Settings during a save waits for it; truthful save status

- Domain: settings. Depends on: B118.
- Files: `src/WSGM/Settings/SettingsWindow.axaml.cs`; `src/WSGM/Settings/SettingsViewModel.Save.cs`; `tests/WSGM.UiTests/Settings/SettingsInteractionTests.cs`.
- Steps: SETTINGS-B5 corrected: Closing cancels and closes after completion while IsSaving, except for OSShutdown and ApplicationShutdown close reasons, so a slow save never vetoes logoff (009); the external apply runs whenever the commit succeeded and a later splash or boot-manifest failure is reported after it (V-003); a post-save apply failure reports 'Saved; applying Steam Input failed: ...' (021).
- Tests: dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~SettingsInteractionTests"; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SettingsSaveMerge".
- Resolves: SETTINGS-009, SETTINGS-021, SETTINGS-V-003.

#### B120 Hardware query types and the default accent move out of UI files

- Domain: settings. Depends on: B119.
- Files: `src/WSGM/Settings/AudioProfileEditor.cs`; `src/WSGM/Settings/DisplayLayoutEditor.cs`; `src/WSGM/Settings/SettingsServices.cs`; src/WSGM/Shell/AudioDiscovery.cs (beside AudioProfileService); `src/WSGM/Core/DisplayCatalogFacts.cs`; `src/WSGM/Overlay/AudioPanel.axaml.cs`; `src/WSGM/Shell/NativeQamAudioFormatService.cs`; `src/WSGM/Core/AppConfig.cs`; `src/WSGM/Themes/AccentPalette.cs`; src/WSGM/Core/PluginActionOption.cs (new); `tests`.
- Steps: SETTINGS-B6 with the corrected home: AudioDiscovery, SpatialAudioNames and SpatialAudioOption go to Shell beside AudioProfileService; DisplayCatalogFacts and its reader to Core display (014); PluginActionOption becomes a Core record (015 part); the default accent constant moves to Core with a Palette.axaml agreement test (017). Pure moves.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~WSGM.Tests.Themes|FullyQualifiedName~AudioPlaybackChoices"`.
- Resolves: SETTINGS-014, SETTINGS-017.

#### B121 Settings updates and plugin packages through services

- Domain: settings. Depends on: B120.
- Files: `src/WSGM/Settings/SettingsViewModel.Updates.cs`; `src/WSGM/Settings/SettingsViewModel.Plugins.cs`; `src/WSGM/Settings/PluginPackageRow.cs`; `src/WSGM/Settings/SettingsServices.cs`; `tests`.
- Steps: SETTINGS-B8: check, download, run and failure-read plus package rows, act and repair are service members; the download takes the window lifetime token (018, 024).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"`.
- Resolves: SETTINGS-018, SETTINGS-024.

#### B122 Overlay controller ports and lifecycle suite

- Domain: overlay. Depends on: B113, B121.
- Files: `src/WSGM/Overlay/OverlayController*.cs`; port files (IOverlayPlatform, IOverlaySheetFactory, ISessionPowerActions); `src/WSGM/Overlay/OpenAppsStrip.cs`; `src/WSGM/Overlay/OverlayWindow.Power.cs`; `src/WSGM/Shell/ShellSession{,.Shutdown}.cs`; tests/WSGM.Tests/Overlay/OverlayControllerLifecycleTests.cs (new).
- Steps: OVERLAY-B4 with overlay.verify batch problems 6 and 11: three ports only (no ISteamInputLease: the controller takes the concrete SteamInputBlocker from B077); machine power actions go through ISessionPowerActions so tests never reach real power APIs (020); Dispose releases the UI claim synchronously (013); Quick Access pin writes are chained like LibraryTabManager.SaveTabOrder and stop mutating the shared _config, with no new pins owner or Changed event (012, CONFIG-V-003); views stop owning workflows and native writes where this batch moves them (005 part). Lifecycle suite: open and close idempotence, lease and claim balance, resummon cancels deferred close, warning reopen, Dispose releases synchronously, Open-apps return waits for close and lease, keyboard request round trip, preview refuses mode switch and power-timeout writes (001).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~OverlayControllerLifecycle|FullyQualifiedName~QuickAccessSheet"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay".
- Resolves: OVERLAY-001, OVERLAY-012, OVERLAY-013, OVERLAY-020, CONFIG-V-003.

#### B123 One Settings window per process

- Domain: settings. Depends on: B122.
- Files: src/WSGM/Shell/SettingsSurface.cs (new); src/WSGM/Settings/SettingsPluginActions.cs (deleted); `src/WSGM/Settings/SettingsViewModel.Launch.cs`; `src/WSGM/Settings/PluginActionListEditor.cs`; `src/WSGM/Settings/SettingsWindow.axaml.cs`; `src/WSGM/Shell/DesktopTray.cs`; `src/WSGM/Shell/SettingsActivation.cs`; `src/WSGM/Overlay/OverlayController.cs`; `src/WSGM/Shell/ShellSession{,.Actions,.Shutdown}.cs`; `src/WSGM/App.axaml.cs`; `tests`.
- Steps: SETTINGS-B4 with OVERLAY-V-002 and SETTINGS-011: SettingsSurface.Open open-or-activates one window per process for the tray, the overlay and SettingsActivation; the surface mode comes from the session's current mode, not the caller, so a Start-menu launch in game mode is switchable; the preview sheet hides or ignores the Settings row; the plugin action source and the test-sheet factory are passed in, deleting the SettingsPluginActions static and the view-built OverlayController (010, 016); the Shell to Settings cycle shrinks (015 rest). Tests: a second open activates the same window; an overlay open of an existing desktop window includes it as switchable; standalone has an empty action source.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings|FullyQualifiedName~WSGM.UiTests.Overlay".
- Resolves: SETTINGS-010, SETTINGS-011, SETTINGS-015, SETTINGS-016, OVERLAY-V-002.

#### B124 Session transitions extracted; per-owner startup isolation

- Domain: session. Depends on: B116, B123.
- Files: src/WSGM/Shell/SessionTransitions.cs (new); src/WSGM/Shell/ShellSession.Modes.cs (deleted); src/WSGM/Shell/ShellSession.cs -> ShellSession.cs + ShellSession.Composition.cs; src/WSGM/Shell/ShellSession.Actions.cs -> SessionCommands.cs; `tests`.
- Steps: SESSION-B6 without A3 (SESSION-012 refuted; IsDesktopShellRunning stays the authority): move boot takeover, launch sequence, desktop resume, splash and desktop actions; remove the dead continuation (015) and dead wiring (014); fields written from workers move into their owners (013); per-owner TryStart(name, essential, action) in composition with essentials the message window and desktop host, and a tray failure in direct boot returns to resident Desktop (010, 011); ShellSession.Actions.cs no longer mutates _config from a worker (CONFIG-040 session part). Tests: takeover refused keeps the desktop, uncertain exit restores, splash desktop request during takeover, tray create failure returns to Desktop, an optional owner throwing leaves the session running.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionTransitions|FullyQualifiedName~SessionStartup|FullyQualifiedName~BootTakeover|FullyQualifiedName~ExplorerReadiness"`.
- Resolves: SESSION-010, SESSION-011, SESSION-013, SESSION-014, SESSION-015.

#### B125 Session policy leaves the overlay controller

- Domain: overlay. Depends on: B124.
- Files: `src/WSGM/Overlay/OverlayController*.cs`; `src/WSGM/Shell/SessionTransitions.cs`; `src/WSGM/Shell/SessionConfigReloader.cs`; `tests`.
- Steps: OVERLAY-011: relaunch moves to SessionTransitions; SessionTransitions raises an event for the Steam-exit 'show overlay' reaction that the overlay subscribes to; accent, log and modes reload subscriptions live in the reloader.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionTransitions|FullyQualifiedName~QuickAccessSheet"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay".
- Resolves: OVERLAY-011.

#### B126 Shared capability rows and commit-on-close combo helper

- Domain: overlay. Depends on: B125.
- Files: src/WSGM/Overlay/CapabilityRowRenderer.cs (new); src/WSGM/Controls/CommitComboBox attach helper (new); src/WSGM/Overlay/InvokeButtonRow.cs (new); `src/WSGM/Overlay/OverlayWindow.Device*.cs`; `src/WSGM/Overlay/OverlayWindow.Graphics.cs`; `src/WSGM/Overlay/OverlayWindow.Sections.cs`; `src/WSGM/Overlay/OverlayWindow.Performance.cs`; `src/WSGM/Overlay/DeviceControlRows.cs`; `src/WSGM/Overlay/OverlayEditors.cs`; `src/WSGM/Overlay/OverlayWindow.PowerEditors.cs`; `src/WSGM/Overlay/DevicePowerPresetView.cs`; `src/WSGM/Overlay/ManualTdpModeView.cs`; src/WSGM/Overlay/AudioPanel.axaml(.cs); `src/WSGM/Overlay/DescriptorControlView.cs`; `src/WSGM/Overlay/DeviceCapabilityControl.cs`.
- Steps: OVERLAY-B5 corrected: shared row layout, readings and command wrappers (007) with one shared view helper that re-checks current CanInvoke and visibility before a write, since the bridges do not; a commit-on-close attach helper over existing ComboBoxes (no subclass, OverlayWindow.axaml byte-identical) for the four unguarded editors, with AudioPanel endpoint bindings one-way plus commit (008); generic descriptor rows lose the frame-limit format (032); one run-button block (033). Visual captures identical, including PreviewAudioPanel.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceRowReconciliation"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayInteractionTests|FullyQualifiedName~DisplayPageViewsTests"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual".
- Resolves: OVERLAY-007, OVERLAY-008, OVERLAY-032, OVERLAY-033.

#### B127 Overlay page controllers

- Domain: overlay. Depends on: B126.
- Files: `src/WSGM/Overlay/DevicePage.cs`; `GraphicsPage.cs`; `PerformanceRows.cs`; `QuickAccessPinsPage.cs`; `PowerPage.cs`; StorageFormatPage.cs (new); remaining OverlayWindow.*.cs partials; `src/WSGM/Overlay/PinnedPluginWidgets.cs`; `src/WSGM/Overlay/CommonPluginPanel.cs`.
- Steps: OVERLAY-B7: move code per the symbol table in overlay.md section 4 (006, 005 rest); one keyed reconciler helper (035); pin indicators tracked per page instead of whole-tree walks (049); PinnedPluginWidgets and CommonPluginPanel reset their closed state on attach (V-004) with a detach-and-reattach test.
- Tests: dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~DevicePageCaptureTests|FullyQualifiedName~GraphicsPageCaptureTests"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual".
- Resolves: OVERLAY-005, OVERLAY-006, OVERLAY-035, OVERLAY-049, OVERLAY-V-004.

#### B128 Launch fixes through one service

- Domain: overlay. Depends on: B127, B053.
- Files: src/WSGM/Shell/LaunchFixService.cs (new); src/WSGM/Overlay/SteamLaunchFixesPage.cs (new); src/WSGM/Overlay/OverlayWindow.LaunchFixes.cs (deleted); `src/WSGM/Overlay/LibraryTabsView.cs`; `src/WSGM/Overlay/CardManagerView.cs`; tests/WSGM.Tests/Shell/LaunchFixServiceTests.cs (new).
- Steps: OVERLAY-B8: the transaction moves unchanged into LaunchFixService (the Unknown snapshot rule already landed in B053); OVERLAY-V-001: a failed library lookup is handled like the not-listed case and every exit path gives the row an outcome title. Tests: refused forgets, unknown keeps, existing snapshot preserved, wrapped shortcut refused, lookup failure still applies or reports.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LaunchFixService"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayToolsTests".
- Resolves: OVERLAY-V-001.

#### B129 Overlay tool views on typed backend results; press-to-edit profile rows

- Domain: overlay. Depends on: B128, B105.
- Files: `src/WSGM/Overlay/ServiceSubView.cs`; `GameLibraryView*.cs`; `ThemesView*.cs`; `AnimationsView*.cs`; `ArtworkView.cs`; `SoundsView.cs`; `LaunchWrapperView.cs`; `OverlayFilePicker.cs`; src/WSGM/Controls/PagedRows.cs (new); `tests/WSGM.UiTests/Fakes/OverlayToolsSources.cs`; `src/WSGM/Overlay/ApplicationProfilesView.cs`; `tests/WSGM.UiTests/Overlay/ApplicationProfilesViewTests.cs`; UI baselines overlay-profiles-720p.png and overlay-profiles-4k-scaled.png.
- Steps: OVERLAY-B9: views consume typed results instead of the Steam page's wire shapes (017); 'Select visible' through SetSelectedAsync (016); the artwork source publishes the offered vocabulary (018 rest); one paging helper for the four overlay lists and picker focus kept on navigation (025, 026); remove text-entry caps (024). OVERLAY-031 (maintainer decision): the application-profile Name and Process fields become controller-reachable press-to-edit rows over the surface host's RequestText with no length cap; the name is a deck-action button showing the draft or 'Profile name'; processes are one button row per name (accepting an empty value removes it) followed by an 'Add process' row; 'Add current application's process' appends to the same list and SaveAsync is unchanged; ControllerNavigationTests reaches the name and Add process rows. PreviewTools captures identical; the two profile captures change and are refreshed with eng\update-ui-baselines.ps1 and reviewed.
- Tests: dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~OverlayToolsTests|FullyQualifiedName~OverlayFilePicker|FullyQualifiedName~ApplicationProfilesViewTests|FullyQualifiedName~ControllerNavigationTests"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual".
- Resolves: OVERLAY-016, OVERLAY-017, OVERLAY-024, OVERLAY-025, OVERLAY-026, OVERLAY-031.

#### B130 Overlay Windows, display and device projections through session owners

- Domain: overlay. Depends on: B129, B093, B090.
- Files: `src/WSGM/Overlay/PowerSchemeSelection.cs`; `src/WSGM/Overlay/OverlayController.cs`; `src/WSGM/Overlay/OverlayController.Power.cs`; `src/WSGM/Overlay/OverlayWindow.Placement.cs`; `src/WSGM/Interop/NativeMethods.cs`; `src/WSGM/Overlay/DisplayModeView.cs`; `src/WSGM/Overlay/ManualTdpModeView.cs`; `src/WSGM/Overlay/OverlayWindow.Sources.cs`; src/WSGM/Controls/PhysicalGlyphService.cs -> src/WSGM/Shell/PhysicalGlyphPlans.cs; `src/WSGM/Shell/DeviceOverlayBridge.cs`; `src/WSGM/Overlay/RadioPanel.axaml.cs`; `src/WSGM/Overlay/EjectPanel.axaml.cs`.
- Steps: OVERLAY-B10 reduced: one energy-plan selection workflow through the session owner (019); the display-mode selector targets the display the sheet is shown on (maintainer decision) through a session-built display access: DisplayModeView takes required read and apply delegates plus the window's display name (the MONITORINFOEXW device of its screen), with no fallback to paths[0] or to another display (009); the window stops reaching into coordinator profile internals (030); PhysicalGlyphService moves to Shell with a simpler cache and no byte budget (036); view state off shared session models (040). Polls stay where owners have no change event (028 no-change); the sheet's idle-timeout and Windows-policy reads move to a worker and fill in a frame later (043, maintainer decision), chained on one controller field so an older read never overwrites a newer one; until they land the badges show the missing-value placeholder and the editors and policy toggles stay disabled.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PowerSchemeSelection|FullyQualifiedName~PhysicalGlyph|FullyQualifiedName~OverlayControllerLifecycle"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~HybridCoreViewTests|FullyQualifiedName~DisplayPageViewsTests|FullyQualifiedName~OverlayLayoutTests"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual".
- Resolves: OVERLAY-009, OVERLAY-019, OVERLAY-030, OVERLAY-036, OVERLAY-040, OVERLAY-043.

#### B131 Overlay test quality

- Domain: overlay. Depends on: B130.
- Files: `tests/WSGM.UiTests/Overlay/ControllerNavigationTests.cs`; `CommonPluginPanelTests.cs`; `UiFixture.cs`; `tests/WSGM.Tests/Overlay/QuickAccessSheetTests.cs`; `OverlayViewModelTests.cs`; TouchSwipeMonitorLifetimeTests (new); OverlayMediaPreview lifetime test (new).
- Steps: OVERLAY-B11: tests use the shared OverlayInput.Create wiring instead of copying production wiring, misfiled tests move, getter and grab-bag tests go (044); add arm, disarm and teardown tests for the touch monitor and a media preview lifetime test over a fake environment factory (045).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay|FullyQualifiedName~WSGM.Tests.Controls"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay".
- Resolves: OVERLAY-044, OVERLAY-045.

#### B132 Overlay nit sweep

- Domain: overlay. Depends on: B131.
- Files: `src/WSGM/Controls/TabStrip.cs`; `OnScreenKeyboard.cs`; `CurveEditor.cs`; `CurveEditing.cs`; `src/WSGM/Overlay/DevicePowerPresetSelection.cs`; `OverlayPreviewImage.cs`; `OverlayMediaPreview.cs`; `AudioPanel.axaml.cs`; `ThemesView.cs`; `AnimationsView.cs`; `LaunchWrapperView.cs`; `OverlayViewModel.cs`; `ServiceSubView.cs`.
- Steps: OVERLAY-B12: preview images skip visibility re-checks on tree-wide layout passes (037); movie preview temp files and the second downloader go (038); the label dependency on Settings goes (039); DevicePowerPresetSelection disposal branches simplify (042); private StyledProperties behind wrappers (046); one curve point limit declaration (047); service sub-view waits honour cancellation (048); small debts (050). Visual captures identical.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Overlay|FullyQualifiedName~WSGM.Tests.Controls"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Overlay"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Visual".
- Resolves: OVERLAY-037, OVERLAY-038, OVERLAY-039, OVERLAY-042, OVERLAY-046, OVERLAY-047, OVERLAY-048, OVERLAY-050.

#### B133 Settings on the instance config store with visible load outcome

- Domain: settings. Depends on: B123.
- Files: src/WSGM/Settings/SettingsPersistence.cs (new); `src/WSGM/Settings/SettingsServices.cs`; `src/WSGM/Settings/SettingsViewModel.cs`; `src/WSGM/Settings/SettingsViewModel.Save.cs`; `src/WSGM/Shell/SettingsSurface.cs`; `src/WSGM/App.axaml.cs`; `tests`.
- Steps: SETTINGS-B7 corrected: persist through the store's transaction; a load outcome other than Loaded or Absent shows in the existing status strip (a config from a newer WSGM is Loaded, best effort, with no read-only mode) while Save stays enabled and refuses strictly as today (022, verify batch problem 9); normalize diagnostics instead of logging in the view model path; the window and view model no longer read config on the UI thread (CONFIG-040 Settings part).
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~SettingsSaveMerge"`.
- Resolves: SETTINGS-022.

#### B134 LiveBackdrop internal seam and lifecycle tests

- Domain: settings. Depends on: B133.
- Files: `src/Avalonia.LiveBackdrop/LiveBackdrop.cs`; `src/Avalonia.LiveBackdrop/NativeMethods.cs`; src/Avalonia.LiveBackdrop/Avalonia.LiveBackdrop.csproj (InternalsVisibleTo); `tests/Avalonia.LiveBackdrop.Tests/*`.
- Steps: SETTINGS-B9 trimmed: three internal delegate fields with the P/Invoke defaults as the seam (no interface, no public API change) (027); StateChanged only on an actual change (028). No native vector reuse (029 no-change) and no native constants unless the native file is rebuilt (030 no-change). Tests: create failure, late callback after Stop ignored, Retry clears failure, blur failure stops the session, hide and show recreates, dispose with a queued callback.
- Tests: `dotnet test tests\Avalonia.LiveBackdrop.Tests\Avalonia.LiveBackdrop.Tests.csproj`.
- Resolves: SETTINGS-027, SETTINGS-028.

#### B135 Settings test cleanup and nits

- Domain: settings. Depends on: B134.
- Files: tests listed in SETTINGS-025; `src/WSGM/Settings/SettingsViewModel*.cs`; `src/WSGM/Core/ModernStandbyDiagnostics.cs`; `src/WSGM/Settings/SettingsWindow.axaml.cs`; `src/WSGM/Settings/Pages/AboutPage.axaml.cs`; `src/WSGM/Settings/Pages/PluginSettingsPage.axaml`; `src/Avalonia.LiveBackdrop/README.md`; WSGM docs.
- Steps: SETTINGS-B10: remove MaximumReportedWakeSources so the row wraps (019, maintainer decision); recorder fault continuation resets recording state on the UI thread and keeps the existing binding (023, maintainer decision); test cleanup keeping SelectorValueListsCoverEveryEnumMember's DoesNotContain assertion (025); behavioural tests listed in 026 where not added earlier; uniform SetFieldIfChanged (032); named constants (033); doc fixes (034, 035 storage-bound wording); profile equality through the source-generated context (036); clone GameModeLaunch once at load (037); observe fire-and-forget tasks (038); comments on the arrangement repaint contract (040); glyph-tile setters to StaticResource and CommandDeck documented as the deck palette owner, checked against UI baselines (041); README WSGM evidence moves to WSGM docs with no fallback colour change (031); about and credit links open through the existing medium one-shot used for ms-settings: (V-005); plugin badge literals become named Palette tokens with identical values, accent unchanged (V-006).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~WSGM.Tests.Settings|FullyQualifiedName~ModernStandby"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings".
- Resolves: SETTINGS-019, SETTINGS-023, SETTINGS-025, SETTINGS-026, SETTINGS-031, SETTINGS-032, SETTINGS-033, SETTINGS-034, SETTINGS-035, SETTINGS-036, SETTINGS-037, SETTINGS-038, SETTINGS-040, SETTINGS-041, SETTINGS-V-005, SETTINGS-V-006.

#### B136 SteamUiCoordinator owns readiness, the master switch and Big Picture holds

- Domain: steamhost. Depends on: B124, B087.
- Files: src/WSGM/Shell/SteamUiCoordinator.cs (new); src/WSGM/Shell/ShellSession.SteamUi.cs (deleted); src/WSGM/Core/SteamUiReadiness.cs (deleted); `src/WSGM/Shell/ShellSession{,.Config,.Shutdown}.cs`; src/WSGM/Shell/SessionTransitions.cs (hooks); `src/WSGM/Shell/CardAcfWatcher.cs`; `src/WSGM/Shell/CardVolumeMonitor.cs`; src/WSGM/Shell/KeepAwakeService.cs (readiness lambda); src/WSGM/Shell/LibraryTabManager.cs (readiness delegate); src/WSGM/Shell/AnimationService.cs (cref); `src/WSGM/Shell/SteamUiSessionHost.cs`; `tests`.
- Steps: STEAMHOST-B5 with verify batch problems 1 and 2: one instance coordinator with one gate and every task tracked owns the transport gate, master switch, transitions and readiness (012 readiness statics, 040); ApplyConfig(SteamUiConfig); the BP retract-then-close and open-then-apply order and every existing remark are kept (039); card services move to the storage owner (046); the tab boot sync becomes one tracked worker with a reschedulable signal (011, SESSION-021); host DisposeAsync idempotent (028). Shutdown: CloseAdmission() is only the existing no-CEF prefix of DisableAsync (clear switch flags, cancel in-flight requests) and runs at T0; patch retraction and disposal stay after device cleanup (001 corrected, SESSION-020). LibraryTabManager receives RunWhenReadyAsync as a delegate and the AnimationService cref is fixed so the batch builds without warnings; no static forwarder. Tests with a fake transport, window probe and clock: master off retracts before close, BP request hold then release then restore, cold start waits for the window, RunWhenReady attempt per edge, admission closes before device disposal, no CEF call after transport close.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamUiCoordinator|FullyQualifiedName~SteamUiSessionHost|FullyQualifiedName~ShellAnchorDisposal"`.
- Resolves: STEAMHOST-001, STEAMHOST-011, STEAMHOST-012, STEAMHOST-028, STEAMHOST-039, STEAMHOST-040, STEAMHOST-046, SESSION-020, SESSION-021.

#### B137 Library tabs and badges as instances

- Domain: steamhost. Depends on: B136.
- Files: src/WSGM/Shell/LibraryTabManager.cs -> LibraryTabService.cs; `src/WSGM/Shell/LibraryBadges.cs`; `src/WSGM/Overlay/CardManagerView.cs`; `src/WSGM/Overlay/LibraryTabsView.cs`; src/WSGM/Shell/SdFormatManager.cs (now SdFormat*); `src/WSGM/Shell/CardAcfWatcher.cs`; `src/WSGM/Shell/ShellSession.cs`; `tests`.
- Steps: STEAMHOST-B6: instance with config transaction, scanner and SteamClient ports; one tracked worker for boot, card and builder syncs that keeps press order (010); write only on change so a tab sync no longer rewrites config.json and reloads the session (014); views take an ILibraryTabs interface instead of the static API (013); remove MutateConfigAsync; write-only test state stays test-only (035 nit). Tests: sync merges once and writes only on change, last order push wins, cancellation on desktop mode, rename steps marker-first.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~LibraryTab|FullyQualifiedName~LibraryBadges|FullyQualifiedName~CardNameAuthority"`.
- Resolves: STEAMHOST-010, STEAMHOST-013, STEAMHOST-014, STEAMHOST-035.

#### B138 Theme, animation and sound content services

- Domain: steamhost. Depends on: B137, B018. Decisions: D2, D10.
- Files: `src/WSGM/Shell/ThemeService.cs`; `src/WSGM/Shell/AnimationService.cs`; src/WSGM/Shell/ContentWorkSlot.cs (new, internal); `src/WSGM/Core/Themes/ThemeInstaller.cs`; `src/WSGM/Core/Themes/ThemeLoader.cs`; `src/WSGM/Core/Themes/ThemePaths.cs`; `src/WSGM/Core/Themes/InstalledTheme.cs`; `src/WSGM/Shell/ThemeBrowseSession.cs`; `src/WSGM/Shell/AnimationBrowseSession.cs`; `src/WSGM/Core/Animations/AnimationLibrary.cs`; `src/WSGM/Shell/SoundPackService.cs`; `src/WSGM/Core/Sounds/SoundPackLibrary.cs`; `src/WSGM/Core/Animations/AnimationOverrides.cs`; src/WSGM.Install/SteamContentCleanup.cs (new); src/WSGM.Setup/Engine/SetupEngine.cs (uninstall step); `tests`.
- Steps: STEAMHOST-B8 with verify notes and the critic's sound-pack items: one ContentWorkSlot with busy cleared in finally used by both services, a save failure refuses and applies nothing for both, background installs joined on dispose (029, 010 content part); ThemeService lock and thread discipline while AnimationService.Start stays synchronous before Steam starts (030); dependency depth by visited set and the sixth duplicate theme reported (031 rest); mklink timeout reported through a fake process port (032); browse sessions link only to _lifetime so a cancelled request cannot leave a stuck spinner (CRIT-006); local boot-movie import has no 64 MB cap, the repository download keeps its byte bound per D2 (V-004). Sound packs (CRIT-002): remove the 1 MiB per-asset skip, the 16 MB playback budget, the 512-entry install cap and the 64 MB ZIP cap, keep only the zip-bomb guard (expanded bytes, symlink and containment) per D2, and publish the empty override set only on the failure path. D10 (015): AnimationService.HandBackSteamChoiceAsync(uninstalling, deadline) gives Steam's set-aside startup-movie choice back through the existing StartupMovie restore when no WSGM movie is selected, and at uninstall whatever is selected; accepted clears SteamSetAside, anything else keeps it and logs once, with no retry; B140's step 8 calls it at exit. Setup's uninstall step uses a shared SteamContentCleanup in WSGM.Install, with file operations only: a WSGM-marked boot-movie override is deleted and .wsgm-original moved back, a themes_custom link to the user's WSGM themes folder is deleted (never its target), anything else is left alone, and a choice still set aside is named in the uninstall log; it runs whether or not data is kept. Upgrade runs no uninstall step. Tests: SteamContentCleanup over a temp Steam folder; AnimationService with a fake startup-movie access for exit with and without a WSGM movie, uninstall, and a refused give-back.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~ThemeService|FullyQualifiedName~AnimationService|FullyQualifiedName~Themes|FullyQualifiedName~Animations|FullyQualifiedName~SoundPack|FullyQualifiedName~Setup"`.
- Resolves: STEAMHOST-015, STEAMHOST-029, STEAMHOST-030, STEAMHOST-031, STEAMHOST-032, STEAMHOST-V-004, CRIT-002, CRIT-006.

#### B139 Steam UI asset pipeline cleanup

- Domain: steamhost. Depends on: B138, B057.
- Files: src/WSGM/Core/SteamDownloadSort.cs -> src/WSGM/Core/SteamUiAssets/Source/download-sort.ts; `src/WSGM/Core/SteamUiAssetCatalog.cs`; `eng/build-steam-assets.mjs`; `src/WSGM/Shell/Steam*Surface.cs`; `src/WSGM/Core/SteamUiAssets/Source/*.ts`; `chord-reset.ts`; `controller-caps.ts`; regenerated asset; `tests`.
- Steps: STEAMHOST-B9: the asset hash is computed at load, deleting the hash constant and the builder's C# rewrite (017); download sort moves into a TS fragment (018); WSGM patch ids move to a wsgm.* prefix (019); WSGM gates use the toolkit ownership helpers instead of hand-rolled method wrapping (033); duplicated TS page patterns folded where trivial (034); inconsistent visibility in the executable (020); duplicate BP-restore applies (039 done in the coordinator); behaviour coverage gaps and test placement (042, 043). Fold ids keep today's strings (045 no-change). Emitted-asset fixtures for download-sort install, remove and report and gate claim release with a foreign wrapper on top.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamDownloadSort|FullyQualifiedName~SteamUiAsset|FullyQualifiedName~SteamUiSessionHost"; npm run steam-assets:build, npm run steam-assets:check, npm run steam-assets:claims.
- Resolves: STEAMHOST-017, STEAMHOST-018, STEAMHOST-019, STEAMHOST-020, STEAMHOST-033, STEAMHOST-034, STEAMHOST-042, STEAMHOST-043.

#### B140 Shutdown as ordered safety-first steps under one deadline

- Domain: session. Depends on: B136, B093, B040, B138. Decisions: D1, D10.
- Files: `src/WSGM/Shell/ShellSession.Shutdown.cs`; `src/WSGM/Shell/ShellSession.Composition.cs`; `src/WSGM/App.axaml.cs`; `src/WSGM/Core/ApplicationRuntime.cs`; tests/WSGM.Tests/Shell/SessionShutdownTests.cs (new).
- Steps: SESSION-B7 with R1 (D1): (0) record the reason and sticky SessionEnd and call every owner's synchronous CloseAdmission() (UI intents, Steam host commands, reload, power queue, mode requests, profile fan-out); (1) await the power queue tail and the startup task within the deadline; (2) AutoTDP restore through the device owner; (3) device shutdown (controller neutral and release, HidHide show, plugin stop; busy lanes are the owner's concern); (4) common plugins and GPU; (5) transition, boot worker and gate loop; (6) tray retire and verify; (7) Explorer restore only when the reason allows and the tray is gone, otherwise the anchor handles it; (8) hand Steam's startup-movie choice back when no WSGM movie is selected, or at uninstall whatever is selected (D10, B138), then Steam host retraction and transport; (9) feature owners on the UI thread; (10) native providers; (11) MessageWindow last. Each step gets the remaining time; every step is attempted after an earlier failure; escalation only tightens the deadline. The 600-line repeated block becomes one RunStepAsync(name, ui, step) helper (030); no unrelated await precedes the safety steps (033, 019 rest). Tests at the runtime and App seam (verify item 8): tray Exit, update and restore-shell each reach the session shutdown exactly once; OS end-session is SessionEnd with zero Explorer calls; a hung transition does not delay device safety; unverified tray keeps the anchor; repeated shutdown returns the same task; exit with no WSGM movie selected gives Steam's choice back once.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SessionShutdown|FullyQualifiedName~ApplicationRuntime"`.
- Resolves: SESSION-019, SESSION-030, SESSION-033.

#### B141 Remaining session dissolution, splash and docs

- Domain: session. Depends on: B140.
- Files: src/WSGM/Shell/ShellSession.Performance.cs (dissolved into PerformanceSessionWiring); src/WSGM/Shell/ShellSession.GameLibrary.cs (dissolved); `src/WSGM/Shell/BootSplash*.cs`; `tests/WSGM.Tests/Shell/ShellAnchorDisposalTests.cs`; getter tests; `docs/boot-and-shell.md`; `docs/elevation.md`.
- Steps: SESSION-B8: performance wiring and game library adapters move to their owners (calls only); the splash timeout is armed at the Steam request and the entry-splash dismissal rule is explicit (057); splash decode becomes one static helper with its comments condensed (058); orphaned summary removed (028); the ShellAnchorDisposalTests premise test and getter tests go (061, 062); docs updated for the new owners and the exit sequence.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Splash|FullyQualifiedName~ShellAnchor"`.
- Resolves: SESSION-028, SESSION-057, SESSION-058, SESSION-061, SESSION-062.

#### B142 Device API 12 contract with package consumers

- Domain: sdk. Depends on: B052, B021, B022, B035.
- Files: `src/WSGM.Device.Sdk/Glyphs/*`; `Services/*`; Packaging/* (ManifestRules.cs new); `Identity/HardwareMatchRule.cs`; `Capabilities/*`; `DeviceSections.cs`; `DeviceApi.cs`; csproj 0.5.0; `README.md`; `docs/reference.md`; src/WSGM/Shell/CapabilityStateDelta.cs and consumers; `src/WSGM/Core/PluginPackageFile.cs`; Claw and Ally journal and plugin files; three device manifests; `template`; `tests/Shared/PluginManifestFixture.cs`; SDK, Claw, Ally, WSGM, UiTests and Device Lab tests.
- Steps: SDK-B5 with sdk.verify batch problem 5 removed items: glyph count and length caps and SVG projection truncation removed, the PNG inspector skips tEXt, zTXt and iTXt (007, 047); one owned copy of glyph bytes (046); CapabilityStateDelta and DeviceSections.IncludePredefined move to the host, including the three UiTests call sites (V-005, 026, 043); ManifestRules with root-only entry assembly and PlainText validation of the device name (011, V-007); HardwareMatcher uses IdentityText (029); deduplicated power helpers (028); ReadbackValue contract text and version history fixed (027, 042); DeviceRecoveryJournal is no longer IAsyncDisposable; DeviceService doc says each service decides its reconciliation block, with no SDK enforcement (016); no RestoredUnverified reinterpretation (SDK-017 refuted). Packages: apiVersion 12 including the HC scaffold (PACKAGES-034), the host-must-Stop-before-Dispose contract line in reference.md (PACKAGES-026), and plugin DisposeAsync disposes every owner collecting failures (SDK-V-004). PluginTrace and ActiveClock stay process-wide (no PluginTraceSink, no owned clock).
- Tests: dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Glyph|FullyQualifiedName~Services|FullyQualifiedName~Packaging|FullyQualifiedName~Identity"; dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj; dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Capability|FullyQualifiedName~Glyph"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~ControllerNavigationTests|FullyQualifiedName~DevicePageCaptureTests".
- Resolves: SDK-007, SDK-011, SDK-016, SDK-026, SDK-027, SDK-028, SDK-029, SDK-042, SDK-043, SDK-046, SDK-047, SDK-V-004, SDK-V-005, SDK-V-007, PACKAGES-026, PACKAGES-034.

#### B143 One package recovery policy: no automatic replay of an uncertain restore

- Domain: packages. Depends on: B142. Decisions: D4, D9.
- Files: `src/WSGM.Device.Sdk/Services/DeviceRecoveryJournal.cs`; `src/WSGM.Device.Msi.Claw/ClawRecoveryJournal.cs`; `ClawPlugin.Recovery.cs`; `ClawServiceBase.cs`; `ClawPlugin.Commands.cs`; `MsiWmiPlatform.cs`; `src/WSGM.Device.Asus.RogAlly/AllyRecoveryJournal.cs`; `RogAllyPlugin.Recovery.cs`; `AllyServices.cs`; `AllyControllerService.cs`; Claw and Ally AGENTS.md (diffs shown with the batch and applied, D4); SDK DeviceRecoveryJournalTests; package tests.
- Steps: PACKAGES-B2 inside unreleased Device API 12 under D9: readback-based recovery states exist only for the Claw, the one vendor that can read its state back (findings/packages.md PACKAGES-001). SDK: BeginAsync never throws for an existing entry: a changed binding replaces it with a fresh Pending entry (036), and a matching RestoredUnverified or RestoreFailed entry goes back to Pending with its first original, which only the Claw ever records; a public PendingOriginalFor and one static Reconcile (Restore, Wait, Keep, Discard) replace both packages' Decide copies and their enums (001, A02-F010). Claw: RestoredUnverified and RestoreFailed entries are kept and never written automatically, an explicit command re-arms, and the controller entry re-reads the mode and restores at every start; no reconciliation outcome blocks a service. Ally (D9): no unresolved entry, no Keep outcome and no re-arm rule; a restore that completes removes the entry, one that throws leaves it Pending with no status write and the next release or start writes it once; the controller entry reconciles like power and fans and ConfigureAsync uses BeginAsync; ArmAsync, the Ally Decide and its ReportOnly branch go, and a changed BIOS binding discards (008 corrected, 036). Firmware change discards for power and fans only. An unavailable transport waits instead of recording failure (009). A release-budget refusal writes nothing, leaves the entry Pending and reports ReleasedUnverified in both packages (035). Ally journal open checks writability (010). PACKAGES-V-002: Claw binds power and fans to the SMBIOS BIOS version only; an existing ec:-bound entry is compared the old way once, then restored or discarded under the new rule. Invert the two pinned Claw tests; the two Ally re-arm tests are deleted (037). The Claw AGENTS rule becomes 'unresolved entries are kept and never written automatically; an explicit command re-arms' and the Ally AGENTS loses its unverified-restore and re-arm rule, each in a diff shown with the batch and applied (D4). Reference docs and API history updated.
- Tests: dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~DeviceRecoveryJournalTests"; dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawModelLifecycleTests|FullyQualifiedName~ClawPluginTests"; dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj --filter "FullyQualifiedName~PluginTests".
- Resolves: PACKAGES-001, PACKAGES-008, PACKAGES-009, PACKAGES-010, PACKAGES-035, PACKAGES-036, PACKAGES-037, PACKAGES-V-002, A02-F010.

#### B144 Shared deterministic helpers move into the SDK

- Domain: packages. Depends on: B143.
- Files: SDK CapabilityValueValidation, CommandResults.cs, DeviceWriteBudget, MotionAttachment, well-known ids; `src/WSGM/Shell/DeviceCapabilityRouter.cs`; `src/WSGM/Core/DeviceConfiguration.cs`; `ClawPlugin.Commands.cs`; `RogAllyPlugin.Commands.cs`; `ClawHardware.cs`; AllyWriteBudget.cs (deleted); `ClawServices.cs`; `AllyControllerService.cs`; both surface files.
- Steps: PACKAGES-B4 within API 12: one command-value validation (014), CommandResults.Unverified without ReadbackValue (015), one write budget with one exception contract replacing the Claw-local exception from B021 (027), one motion attachment (028), shared capability ids including the host copy in Core/DeviceConfiguration.cs (030). SDK unit tests per helper; package suites unchanged in behaviour.
- Tests: dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Capabilities"; dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj; dotnet test tests\WSGM.Device.Asus.RogAlly.Tests\WSGM.Device.Asus.RogAlly.Tests.csproj; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~DeviceCapabilityRouter".
- Resolves: PACKAGES-014, PACKAGES-015, PACKAGES-027, PACKAGES-028, PACKAGES-030.

#### B145 Claw transports and identity snapshot

- Domain: packages. Depends on: B144. Decisions: D4.
- Files: `src/WSGM.Device.Msi.Claw/MsiWmiPlatform.cs`; `WindowsHidTransports.cs`; `HidDescriptorGamepad.cs`; `ClawControllerService.cs`; `ClawModels.cs`; `WSGM.Device.Msi.Claw.csproj`; `tests`.
- Steps: PACKAGES-B6 with the D4 Claw AGENTS diff for identity revalidation, shown with the batch and applied: identity snapshot per start and resume, AC line per command, provider probe without rebind (017; with V-002 the per-command Get_WMI/Get_EC reads leave the command path); MCU revision from ReleaseNumber (032); the reader owns and frees its preparsed descriptor (018); SamePhysicalLocation instead of string compare (021); MSI_Event repair from StartAsync only with its timeout caught (033); PlatformTarget x64 (038); test-only leftovers removed (031 rest). No bounded motion stop with retention (019 no-change).
- Tests: `dotnet test tests\WSGM.Device.Msi.Claw.Tests\WSGM.Device.Msi.Claw.Tests.csproj --filter "FullyQualifiedName~ClawPluginTests|FullyQualifiedName~WindowsHidTransportsTests|FullyQualifiedName~WindowsMotionSourceTests"`.
- Resolves: PACKAGES-017, PACKAGES-018, PACKAGES-021, PACKAGES-032, PACKAGES-033, PACKAGES-038.

#### B146 One plugin loader without the forwarding wrapper

- Domain: sdk. Depends on: B142, B081.
- Files: src/WSGM/Shell/PluginPackageLoader.cs -> PluginLoader.cs; src/WSGM/Shell/CommonPluginPackage.cs (deleted); `src/WSGM/Shell/CommonPluginManager.cs`; src/WSGM/Shell/DevicePluginRuntime.cs (load call); `src/WSGM/Shell/PluginCapabilityChannel.cs`; `src/WSGM/Shell/PluginHost.cs`; `src/WSGM/Shell/CommonPluginSteamUiSource.cs`; `tests`.
- Steps: SDK-B7 with sdk.verify batch problem 6: Load<TEntry> and LoadedPluginPackage<T> (013); explicit CleanupConfirmed failure instead of exception type; the manager checks interfaces on the real plugin (014), and every consumer of PluginRegistration.Settings treats null and empty alike so Settings and overlay rows stay identical; active-time waits in the manager (004 rest); the host stops reusing CommandOutcome.Accepted with a different meaning (025); entry flags and event threading made consistent and registration state written under the host lock (033, 049).
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~CommonPlugin|FullyQualifiedName~DevicePluginRuntime|FullyQualifiedName~PluginHost"; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings".
- Resolves: SDK-004, SDK-013, SDK-014, SDK-025, SDK-033, SDK-049.

#### B147 Plugin catalog, installer, package layout and packer validation

- Domain: sdk. Depends on: B146, B121. Decisions: D2.
- Files: src/WSGM.Device.Sdk/Packaging/PluginPackageLayout.cs (new); `src/WSGM/Core/PluginPackageFile.cs`; src/WSGM/Core/PluginPackageCatalog.cs -> PluginCatalog; src/WSGM/Core/PluginPackageManager.cs -> PluginPackageInstaller.cs, PendingPluginRemovalStore.cs; `src/WSGM/Settings/PluginPackageRows.cs`; `src/WSGM/Settings/SettingsViewModel*.cs`; `src/WSGM/Core/UpdateChecker.cs`; `src/WSGM/Shell/ShellSession{,.Actions}.cs`; `src/WSGM/Shell/DeviceCoordinator.cs`; `src/WSGM/Core/CommonPluginEnablement.cs`; eng/plugin-manifest.cs (validate-package, host-provided); `eng/package-plugin.ps1`; `eng/plugin-package-common.ps1`; `eng/pack-device.ps1`; `eng/build-bundle.ps1`; `docs/plugin-system.md`; `docs/device-plugin-authoring.md`; `tests`.
- Steps: SDK-B8 plus BUILD-B7: manifest-only discovery off the UI thread, hash per discovery (009); pending removals are applied once, synchronously, in startup composition before CommonPluginManager is constructed, and DiscoverInstalled loses that side effect (SDK-V-001, with a Device-Integration-off test); remove MaxPackages, the 260-character name cap, MaxPackageEntries and MaxPackageFiles, keeping the per-file and total byte bounds per D2 (008, V-003); PluginPackageManager split into installer, pending-removal store and Settings rows (031) with Settings reading the catalog snapshot and the manager's adapter inventory (032 refresh); package rules in one place, packers calling plugin-manifest.cs validate-package, which also carries the native-image refusal (012, BUILD-014, A02-F021); the bundle's SDK feed version follows the csproj (BUILD-027). Rows rename must not change UI.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~PluginPackage|FullyQualifiedName~PluginCatalog|FullyQualifiedName~CommonPluginEnablement"; dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj --filter "FullyQualifiedName~Packaging"; local eng/package-plugin.ps1 run on src/WSGM.Plugin.Ir into a temp path.
- Resolves: SDK-008, SDK-009, SDK-012, SDK-031, SDK-032, SDK-V-001, SDK-V-003, BUILD-014, BUILD-027, A02-F021.

#### B148 GPU coordinator hygiene

- Domain: sdk. Depends on: B146, B089.
- Files: `src/WSGM/Shell/GpuCoordinator.cs`; `src/WSGM/Shell/ShellSession.cs`; `src/WSGM/Shell/CapabilityUserWrites.cs`; `src/WSGM/Shell/PluginCapabilityChannel.cs`; `tests`.
- Steps: SDK-B9 without the forwarding shim: instance _syncRevision (022, GPUIR-040); injected Func<bool?> onAcPower instead of device-coordinator statics, with both PerformanceProfileOwnsRole callers moved in this batch (023); tracked disposal and refresh tasks awaited in DisposeAsync (024); active-time GPU command cancellation in PluginCapabilityChannel if B146 did not cover it.
- Tests: `dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~GpuCoordinator|FullyQualifiedName~PluginCapabilityChannel"`.
- Resolves: SDK-022, SDK-023, SDK-024, GPUIR-040.

#### B149 SDK test cleanup that keeps the regression guards

- Domain: sdk. Depends on: B148, B147.
- Files: `tests/WSGM.Device.Sdk.Tests/*`; `tests/WSGM.Plugin.Sdk.Tests/*`; `tests/WSGM.Tests/Core/PluginPackageManagerTests.cs`.
- Steps: SDK-B10 with sdk.verify batch problem 9: delete only true duplicates (PluginTextTests after SDK-018, the constant-ratio clock test, the factory-equals-initializer test); keep ContractBoundaryTests (documentation enforcement and API version) and LowLevelKeyboardHookTests (signed GetMessage import); add the missing behavioural tests (StartResult, StopResult, ReasonFor, Ownership, DiagnosticText, CommandResults, IdentityText, OemButtonLatch).
- Tests: dotnet test tests\WSGM.Device.Sdk.Tests\WSGM.Device.Sdk.Tests.csproj; dotnet test tests\WSGM.Plugin.Sdk.Tests\WSGM.Plugin.Sdk.Tests.csproj.
- Resolves: SDK-039.

#### B150 GPU journals: unreadable never resets, controls survive a bad per-app record

- Domain: gpu. Depends on: B052.
- Files: `src/Shared/Gpu/DriverStateFile.cs`; `src/WSGM.Plugin.NvidiaGpu/NvProfiles.cs`; `src/WSGM.Plugin.NvidiaGpu/NvSession.cs`; src/WSGM.Plugin.IntelGpu/WSGM.Plugin.IntelGpu.csproj (links Shared/Gpu); src/WSGM.Plugin.IntelGpu/StateFile.cs (deleted); `src/WSGM.Plugin.IntelGpu/Profiles/ApplicationProfileSynchronizer.cs`; `src/WSGM.Plugin.IntelGpu/Display/ColorPipeline.cs`; `tests`.
- Steps: GPUIR-B1 corrected: DriverStateFile opens directly, absent only on FileNotFound or DirectoryNotFound, no 4 MiB cap (003); NVIDIA keeps the session and global controls when its per-app journal is unreadable and Sync returns one failure per executable without writing (V-002); Intel moves onto the shared state file, an unreadable record makes sync return failures and is never rewritten (002), and a failed record save is a sync failure for that executable with the driver write left done (V-006); ColorStore keeps publishing colour as unknown and skips its record write when unreadable; one atomic JSON helper (021).
- Tests: dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter NvProfilesTests; dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj --filter ApplicationProfileSynchronizerTests|StateFileTests.
- Resolves: GPUIR-002, GPUIR-003, GPUIR-021, GPUIR-V-002, GPUIR-V-006.

#### B151 Shared GPU runtime contract and behaviour

- Domain: gpu. Depends on: B150.
- Files: `src/Shared/Gpu/*`; DriverWriteScope.cs -> WriteAdmission.cs; src/Shared/Gpu/DriverLog.cs (from IntelLog.cs); NVIDIA and AMD sources; src/WSGM/Shell/PluginHost.cs (Publish dedupe); `DriverRuntimeTests.cs`; `NvColorTests.cs`; `NativeContractTests.cs`.
- Steps: GPUIR-B2 with gpuir.verify corrections (depends on B150 because Intel only compiles Shared/Gpu after it): WriteAdmission passed through DriverControl.Write/ProbeSupport, IDriverSession.Sync and every native setter instead of a thread-static (006); DriverRead result type; CommandResults with specific reason codes (008); never record a read failure in _supportResults, only probe outcomes, so a transient read failure hides nothing (V-001); per-session caches of NvApi.Values and SettingIds and one DRS Load per pass through BeginPass, keeping per-pass rediscovery (017 corrected, V-003); stop honours the host token while dispose starts or joins retirement without reading a stale deadline (015); the post-write publish failure is contained and the write's own outcome returned (V-005); label truncation removed (018); health deduplicated in PluginHost.Publish (019); DriverLog replaces the report delegate. No written-value overlay for NVIDIA or AMD and no 'sync when not running' step (009 refuted).
- Tests: dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj --filter DriverRuntimeTests; dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj.
- Resolves: GPUIR-006, GPUIR-008, GPUIR-015, GPUIR-017, GPUIR-018, GPUIR-019, GPUIR-V-001, GPUIR-V-003, GPUIR-V-005.

#### B152 NVIDIA and AMD cleanups

- Domain: gpu. Depends on: B151.
- Files: `src/WSGM.Plugin.NvidiaGpu/NvProfiles.cs`; `NvSession.cs`; `NvOutputControls.cs`; `NvApi.cs`; `src/WSGM.Plugin.AmdGpu/AdlxNative.cs`; `AdlxSession*.cs`; `AdlDitherApi.cs`; `tests`.
- Steps: GPUIR-B3: NVIDIA under D9: a Save that returns marks the request written with no readback after it; NvOwnedSetting loses Pending and Restoring and the 'earlier write is unconfirmed' refusals go; a failed Set or Save puts the journal back as it was and is reported once; a restore whose Save returns removes the entry without reading back; a later external change surfaces as the external-editor message on the next sync (012); delete the dead Get (022); one DRS helper for G-SYNC and settings (023); setting-id buffer sized from the driver and ADLX list sizes from the driver count (020); ADL allocation callback allocates what ADL asks (V-007); revision guard uses <= in both packages (038). The AMD process-static epoch stays (016 no-change).
- Tests: dotnet test tests\WSGM.Plugin.NvidiaGpu.Tests\WSGM.Plugin.NvidiaGpu.Tests.csproj; dotnet test tests\WSGM.Plugin.AmdGpu.Tests\WSGM.Plugin.AmdGpu.Tests.csproj.
- Resolves: GPUIR-012, GPUIR-020, GPUIR-022, GPUIR-023, GPUIR-038, GPUIR-V-007.

#### B153 Intel onto the shared driver runtime with its visible behaviour kept

- Domain: gpu. Depends on: B152.
- Files: `src/WSGM.Plugin.IntelGpu/IntelGpuPlugin.cs`; IgclDriverSession.cs (new); `IntelModel.cs`; `Controls/IntelControl.cs`; `Controls/FieldControl.cs`; `Igcl/IgclSource.cs`; `Igcl/IgclApi.cs`; `Graphics/ThreeDFeature.cs`; `Graphics/IntelGraphicsMemoryTransport.cs`; `Graphics/AdapterControls.cs`; `Display/ColorPipeline.cs`; `Display/ArcSyncControls.cs`; `Display/PowerControls.cs`; `tests`.
- Steps: GPUIR-B4 with gpuir.verify batch problem 6: IgclDriverSession and an IgclDriverControl adapter; writes leave the UI thread through the runtime hop (001, 007); WriteAdmission into the Intel set entry points (005); support results cached once by SupportKey with V-001's rule (024); the Intel written-value overlay stays inside the adapter; health maps open and build failures to Failed as today; Verified quality after a matching readback is kept; TraceDue or equivalent per-value formatting suppression stays out of the runtime pass; the existing reopen backoff stays so a refused ctlInit does not become a load and free cycle every 10 s (025 no-change); colour probe treats DATA_NOT_FOUND as no stored block (010); shared-memory probe publishes without a readback gate (011); StopLoopAsync waits for a running loop (026); ControlLib.dll loads by name as today (034 is dropped by maintainer decision, security theater); predicate-copy tests replaced with adapter mapping tests and a persisted-id fixture (036). Manual M01-20 on the notebook and Claw.
- Tests: `dotnet test tests\WSGM.Plugin.IntelGpu.Tests\WSGM.Plugin.IntelGpu.Tests.csproj`.
- Resolves: GPUIR-001, GPUIR-005, GPUIR-007, GPUIR-010, GPUIR-011, GPUIR-024, GPUIR-026, GPUIR-036.

#### B154 IR host correctness

- Domain: ir. Depends on: B003, B052.
- Files: `src/WSGM.Plugin.Ir/IrEndpoint.cs`; `IrPayload.cs`; `IrPlugin.cs`; `SerialIrLink.cs`; `tests`.
- Steps: GPUIR-B6 on top of B003, without the refuted lifetime CTS (014) and the IR half of 035: any matched non-success status is a refusal (013) and failures before the request frame is written (Target(), IntegerArgument, missing backup, link open or identify) raise IrRejectedException, so route compensation runs only for actions that may have emitted (V-004); loaders return Loaded, Absent or Unreadable and an unreadable library or endpoint fails StartAsync with an explanatory health and writes nothing (A02-F017); SerialIrLink disposes the port on a failed Open (033, A02-F018); the sequence wait reports 'still running' when it stops polling (029); delete _lastCommand (028); direct frame building (030). Tests: learn timeout and invalid payload are Rejected and never resent; unknown status is Unconfirmed; a locked library.json keeps its bytes and fails start.
- Tests: `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`.
- Resolves: GPUIR-013, GPUIR-028, GPUIR-029, GPUIR-030, GPUIR-033, GPUIR-V-004, A02-F017, A02-F018.

#### B155 IR plugin split by responsibility

- Domain: ir. Depends on: B154.
- Files: `src/WSGM.Plugin.Ir/IrPlugin.cs`; IrEndpointSession.cs (new); IrStore.cs (new); IrStatusPublisher.cs (new); `IrPayload.cs`; `tests`.
- Steps: GPUIR-B7: move per gpuir.md section 4 with no behaviour change; action ids, labels and publication keys identical; one fixture test pins the published state after start, learn and select.
- Tests: `dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj`.
- Resolves: structural move, no finding of its own (see Appendix A).

#### B156 IR firmware fixes and catalog paging with protocol 2

- Domain: ir. Depends on: B155. Decisions: D11.
- Files: `src/WSGM.Plugin.Ir/Firmware/src/main.cpp`; `Firmware/embed_remotes.py`; `protocol.md`; `README.md`; `src/WSGM.Plugin.Ir/IrEndpoint.cs`; `tests/WSGM.Plugin.Ir.Tests/IrEndpointConnectionTests.cs`.
- Steps: GPUIR-B8 with D11: retire a pending network learn with a terminal reply when its client is replaced (004, A02S01-F001); one feedbackActive bool with wrap-safe subtraction (F002); numeric model range (F003); strict hex lexing (F004); check putString and begin and reply storage-failed without changing RAM state (F005); check deserializeJson and serve zero remotes on failure (F006); delete the 128-button and 32-step caps and add no build-time size check; catalog paging (D11, F007, 031): remotes takes an optional offset and answers pages of {size, offset, text} with at most 8192 catalog bytes, never cut inside a UTF-8 sequence, so every reply stays inside the 32 KiB frame for a catalog of any size, and a bad offset answers invalid-offset; authoring checks (F008-F011); answer busy only for emission operations by moving protocols before the busy check (032); the climate page validates stored values, parallel intents and custom-remote status ordering where source-confirmed, else recorded as no-change with the reason (F012-F014); firmware 0.5.0 with protocol 2 (replies carry v 2, any other v answers protocol-mismatch, describe reports 2). The host side lands in the same batch: IrEndpoint.ProtocolVersion becomes 2, identity requires protocol 2 ('flash firmware 0.5.0'), and ListRemotesAsync requests pages from offset 0 until size, refusing a page whose offset or size is wrong, with no page count cap; protocol.md becomes protocol 2 and the IR README says firmware 0.5.0 and this WSGM need each other. Validation: platformio build only, no upload, including one scratch remote with 400 buttons whose catalog exceeds 32 KiB; host tests over the scripted link reassemble a three-page catalog split inside a multi-byte label and refuse a protocol 1 identity. Manual M01-37/38 after the maintainer flashes.
- Tests: .codex/ir-tools/Scripts/python.exe -m platformio run -d src/WSGM.Plugin.Ir/Firmware (build only); dotnet test tests\WSGM.Plugin.Ir.Tests\WSGM.Plugin.Ir.Tests.csproj.
- Resolves: GPUIR-004, GPUIR-031, GPUIR-032, A02S01-F001, A02S01-F002, A02S01-F003, A02S01-F004, A02S01-F005, A02S01-F006, A02S01-F007, A02S01-F008, A02S01-F009, A02S01-F010, A02S01-F011, A02S01-F012, A02S01-F013, A02S01-F014.

#### B157 Device Lab worker host as an instance with tests

- Domain: lab. Depends on: B004.
- Files: `src/WSGM.DeviceLab/Worker/LabWorkerHost.cs`; `LabWorkerSession.cs`; `LabWorkerCalls.cs`; `LabWorkerProtocol.cs`; LabWorkerService.cs (new); ILabWorkerClient.cs (new); `LabWorkerClient.cs`; tests/WSGM.DeviceLab.Tests/Worker/LabWorkerHostTests.cs (new).
- Steps: LABCORE-B1 trimmed per labcore.verify: instance host over TextReader, TextWriter and TimeProvider (012); ZeroStale takes the existing lock with Monitor.TryEnter and returns when busy (002, A02-F011), no per-session locks; after EOF queued requests are not invoked, the running call is awaited, then every session zeroes and disposes, with no new deadline (005, A02-F014); Release with a wrong token fails (013); resolved and validated method table replacing the attribute test (015); the client re-checks _lost and sweeps pending calls in finally (016); sampled FanSpeeds calls are not logged per sample (035); ILabWorkerClient added without changing consumer signatures. Not here: pre-cancelled call refusal (004 no-change), stale-frame filter (006 no-change), replaceable checkpoint (014 no-change), the launcher move (B162).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~WSGM.DeviceLab.Tests.Worker"`.
- Resolves: LABCORE-002, LABCORE-005, LABCORE-012, LABCORE-013, LABCORE-015, LABCORE-016, LABCORE-035, A02-F011, A02-F014.

#### B158 Device Lab machine record and start-up recovery

- Domain: lab. Depends on: B157, B020.
- Files: `src/WSGM.DeviceLab/Wizard/LabMachineState.cs`; Wizard/LabRecovery.cs (new); `Wizard/LabPowerRecovery.cs`; `Wizard/LabRumbleRecovery.cs`; `Wizard/LabControllerInit.cs`; `Wizard/LabModeCommands.cs`; `Transports/LabCuratedInitWorker.cs`; `Wizard/HidHideAllowance.cs`; `Wizard/LabPawnIo.cs`; `Gui/WizardWindow.cs`; `Gui/WizardWindow.Hardware.cs`; `tests`.
- Steps: LABCORE-B2 corrected: LabMachineState.Read throws for anything but a missing file (same signature), so Update can never overwrite what it could not read and the existing page error shows (001, LABUI-027); records stay where they are with one path helper, no fold or migration (010); LabRecovery owns start-up recovery order, takes the owner reservation around controller and mode-command recovery and refuses with the existing 'Close WSGM, then start Device Lab again' wording (V-001), contains every item with one rule (all exceptions except OOM) so one failure never skips the others (011, V-005), and starts the worker only when worker-bound work is pending (LABUI-V-005); curated-init checkpoint records the original mode and the worker writes no record; refuse when two controller collections match (029); merged pending predicate (033).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabMachineState|FullyQualifiedName~LabRecovery|FullyQualifiedName~LabPowerRecovery|FullyQualifiedName~PreflightTests|FullyQualifiedName~LabPawnIo|FullyQualifiedName~LabModeCommands"`.
- Resolves: LABCORE-001, LABCORE-010, LABCORE-011, LABCORE-029, LABCORE-033, LABCORE-V-001, LABCORE-V-005, LABUI-027, LABUI-V-005.

#### B159 Device Lab read probes and the MSI_ACPI channel

- Domain: lab. Depends on: B158.
- Files: src/WSGM.DeviceLab/Transports/MsiAcpiChannel.cs (new); `Transports/LabMsiWmi.cs`; `Probes/ReadProbeProfiles.cs`; `Application/DeviceLabApplication.cs`; `Transports/LabAmdSmu.cs`; `Gui/WizardWindow.ProcessorPower.cs`; `tests`.
- Steps: LABCORE-B3 with verify notes: one MSI_ACPI channel used by probes and the worker transport, null-input methods handled, its 3 s per-call timeout stated as a behaviour change for the probe worker (007); the probe gate uses the live inventory through an injected collector function (008); descriptors from metadata (034); MSI checkpoint survives an unreadable fan flag (021); LABCORE-V-003: Plausible uses MinimumTestWatts so a sub-5 W AMD original refuses the test before any write, and WriteLimits validates all three limits before the first command; delete the arithmetic test (037 part).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~ReadProbe|FullyQualifiedName~LabMsiWmi|FullyQualifiedName~LabAmdSmu"`.
- Resolves: LABCORE-007, LABCORE-008, LABCORE-021, LABCORE-034, LABCORE-V-003.

#### B160 Device Lab transport tidy

- Domain: lab. Depends on: B159.
- Files: src/WSGM.DeviceLab/Transports/LabHid.cs (new); `Capture/Live/LabRumble.Native.cs`; `LabRumbleRoutes.cs`; `LabRumblePad.cs`; `Transports/LabAuraLighting.cs`; `LabClawLighting.cs`; `LabIntelKx.cs`; `LabPawnIoModule.cs`; Transports/LabPins.cs (new); `Wizard/PawnIoSetup.cs`; `ManagerConflicts.cs`; `tests`.
- Steps: LABCORE-B4 without the LabPowerLog namespace move (032 no-change): general HID helper out of rumble code (024); one embedded pin reader (023); KX folder removed on a failed Open (022); one manager flag (033 rest).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabRumble|FullyQualifiedName~LabClawLighting|FullyQualifiedName~LabIntelKx|FullyQualifiedName~LabAmdSmu|FullyQualifiedName~PreflightTests"`.
- Resolves: LABCORE-022, LABCORE-023, LABCORE-024.

#### B161 Device Lab capture step buffer and evidence completeness

- Domain: lab. Depends on: B160.
- Files: src/WSGM.DeviceLab/Capture/Live/LabInputStepBuffer.cs (new); Capture/Live/ILabInputCapture.cs (new); `Capture/Live/LabInputCapture*.cs`; `Capture/Live/LabWmiFirmwareEvents.cs`; `Capture/CapturePrivacyPreview.cs`; `Capture/CaptureBundle.cs`; `Wizard/LabSystemDump*.cs`; `Gui/WizardWindow.Buttons.cs`; `Gui/WizardWindow.Sleep.cs`; `tests`.
- Steps: LABCORE-B5 corrected: LabInputStepBuffer holds Record, BeginStep, EndStep, NoiseMap and the report decision logic; LabInputCapture implements ILabInputCapture (P9); remove silent WMI and HID truncation and the issue, device, table and structure count caps, and the 6,000 per-step event cap, while the counted 1-in-50 noise sampling stays documented (027, D2 lists the byte bounds kept); LabTrace keeps the previous log instead of overwriting it (027 trace part, with B162); _ready is not disposed before the thread is joined (031); LABCORE-V-002: Begin reports whether the quarantine marker reached the disk, an unreadable list or unwritten marker skips the class, and watcher.Stop runs inside the same Begin/End; LABCORE-V-004: Shortcut always tracks state and only swallowing is conditional; LABUI-V-003: cancelled steps end in the cancel path; LABUI-V-004: one lane serializer shared by preview and bundle. Redaction stays as today (028 no-change).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabInputStepBuffer|FullyQualifiedName~Redaction|FullyQualifiedName~LabSystemDump|FullyQualifiedName~CapturePrivacyPreview"`.
- Resolves: LABCORE-027, LABCORE-031, LABCORE-V-002, LABCORE-V-004, LABUI-V-003, LABUI-V-004.

#### B162 Device Lab application, CLI and process hygiene

- Domain: lab. Depends on: B161.
- Files: `src/WSGM.DeviceLab/Program.cs`; `Application/DeviceLabApplication.cs`; `DeviceLabEnvironment.cs`; `LabTrace.cs`; `Preflight/DeviceLabDoctor.cs`; `Cli/DeviceLabCli.cs`; `Capture/ObserveOnlyCaptureWorkflow.cs`; `Probes/ReadProbeWorkerSupervisor.cs`; Application/SelfWorkerProcess.cs (new); `tests`.
- Steps: LABCORE-B6: one boundaries instance and facade from Program covering all 11 ForCurrentUser sites including PluginTestWorker and ReadProbeWorkerSupervisor (020, 040); one CI predicate and one elevation helper (019); LabTrace keeps today's folder (018 is dropped by maintainer decision, security theater); the self-worker launcher shared by the hardware worker and probes (017); merge duplicate staging tests (037 rest).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Cli|FullyQualifiedName~Application|FullyQualifiedName~Preflight"`.
- Resolves: LABCORE-017, LABCORE-019, LABCORE-020, LABCORE-037, LABCORE-040.

#### B163 Device Lab reports folder and one archive reader

- Domain: lab. Depends on: B020, B162.
- Files: src/WSGM.DeviceLab/Reports/* (LabReport, LabReview*, LabReviewArchive, LabPromote moved); `Cli/DeviceLabCli.cs`; `Gui/MainWindow.cs`; `Scaffolding/ScaffoldFromLabProjectWorkflow.cs`; `tests/.../Reports/*`.
- Steps: LABUI-B2 corrected: namespace move; LabReport.Read on LabReviewArchive (013) with LabReport.MaximumEntryBytes relocated into LabReviewArchive in the same change; no 120-character Compact in persisted details (015 part); one DescribeButton (022); offline report tooling out of the wizard folder (017). The rumble 'report' read stays.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Reports"`.
- Resolves: LABUI-013, LABUI-015, LABUI-017, LABUI-022.

#### B164 Device Lab export keeps every file in memory without caps

- Domain: lab. Depends on: B163.
- Files: `src/WSGM.DeviceLab/Wizard/LabExport.cs`; `Wizard/LabProject.cs`; `Reports/LabReviewArchive.cs`; `tests`.
- Steps: LABUI-014 corrected: delete MaximumFileBytes, MaximumFiles and MaximumTotalBytes from LabExport and keep the in-memory build, preview and single write the subtree AGENTS requires; LabReviewArchive gets its own entry and total constants as an untrusted-archive guard (D2); the manifest is read under the project lock and the export is cancellable (030). No staging file and no BeginAttempt skip (028 refuted).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LabExportTests|FullyQualifiedName~LabProjectTests|FullyQualifiedName~Reports"`.
- Resolves: LABUI-014, LABUI-030.

#### B165 Device Lab developer GUI runner and statics

- Domain: lab. Depends on: B164.
- Files: `src/WSGM.DeviceLab/Gui/App.cs`; `Gui/DeviceLabGui.cs`; `Program.cs`; `Gui/MainWindow.cs`; Gui/GuiOperationRunner.cs (new); `Gui/RecentPaths.cs`; `tests`.
- Steps: LABUI-B4 without new controls (LABUI-016 recorded as an accepted parity gap): App constructor with options (010); one boundaries instance from Program (032); a non-Avalonia runner with the post-completion cancel discard fixed (008) and no message truncation; known-key recent paths without caps (015 part); leaked process handle and repeated help windows (024).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Gui"`.
- Resolves: LABUI-008, LABUI-010, LABUI-024, LABUI-032.

#### B166 WizardSession owns one operation at a time

- Domain: lab. Depends on: B165.
- Files: src/WSGM.DeviceLab/Wizard/Session/{WizardSession,WizardServices,WizardStageContext,WizardOptions,IWizardUi,WizardBuildInfo}.cs (new); `Gui/WizardControls.cs`; `Gui/WizardWindow.cs`; Gui/WizardWindow.Hardware.cs (deleted); `Program.cs`; tests/.../Wizard/WizardSessionTests.cs (new); FakeWizardUi.cs (new).
- Steps: LABUI-B5 with verify batch problem 8: the session owns project, reservation, lazily started worker and capture, the stage loop, navigation, stop and notes (018) and the close order (cancel stage, await it, undo HidHide, dispose capture, dispose worker, release reservation, once) (001); chaining flags and WhenAll go (006); token names fixed (019); the post-stage result pages stay outside the running operation so Stop, Escape and stage-list clicks behave as today; test quality for the session (025).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~WizardSession"`.
- Resolves: LABUI-001, LABUI-006, LABUI-018, LABUI-019, LABUI-025.

#### B167 Preflight, Identity, SystemDump and Finish stage controllers

- Domain: lab. Depends on: B166.
- Files: src/WSGM.DeviceLab/Wizard/Stages/{PreflightStage,IdentityStage,SystemDumpStage,FinishStage}.cs (new); Gui/WizardWindow.SystemDump.cs (deleted); `Gui/WizardWindow.cs`; `tests`.
- Steps: LABUI-B6: move logic; the system dump finishes before its prompt and reports abandoned sections (020); viewing 'Finish and share' no longer changes the machine (033); evidence writes surface their failures.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~Stages"`.
- Resolves: LABUI-020, LABUI-033.

#### B168 Buttons and Motion stage controllers

- Domain: lab. Depends on: B167.
- Files: src/WSGM.DeviceLab/Wizard/Stages/{ButtonsStage,MotionStage,ModeCommandRun,ControllerInitRunner}.cs (new); Gui/WizardWindow.Buttons.cs, Motion.cs, ModeCommands.cs (deleted); Capture/Live/LabInputAnalysis.cs (IsPointer); `tests`.
- Steps: LABUI-B7: move logic; the extra-button cap is removed (015 rest); answer enums replace magic indices (029); the pointer filter is shared and curated facts leave UI literals (009). Mode commands stay in the wizard process behind the owner reservation (026 no-change).
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~ButtonsStage|FullyQualifiedName~MotionStage"`.
- Resolves: LABUI-009, LABUI-029.

#### B169 Rumble stage controller

- Domain: lab. Depends on: B168.
- Files: src/WSGM.DeviceLab/Wizard/Stages/Rumble/{RumbleStage,RumbleSession}.cs (new); `Gui/RumbleViews.cs`; Gui/WizardWindow.Rumble.cs (deleted); `tests`.
- Steps: LABUI-B8: session and output move; identical layout and the controller A/B answer. Tests with a fake worker: each route is recorded before its first write and gets one final zero on success, failure and cancel; a failed final zero fails the stage; a failed pulse is never replayed.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~RumbleStage"`.
- Resolves: structural move, no finding of its own (see Appendix A).

#### B170 Power test stage controllers

- Domain: lab. Depends on: B169.
- Files: src/WSGM.DeviceLab/Wizard/Stages/Power/{PowerStage,PowerTestGuard,AsusPowerTests,MsiPowerTests,ProcessorPowerTests}.cs (new); Gui/WizardWindow.Power.cs (power part); Gui/WizardWindow.ProcessorPower.cs (deleted); `tests`.
- Steps: LABUI-B9: move logic unchanged in order and values; machine updates off the UI thread with null-safe clears (011, 012); a stage error finishes Failed (007); WriteEvidenceOnce and WriteContext deleted for project.WriteEvidence, fail-closed in checkpoint callbacks (023). Record-before-write and readback-before-clear stay as the documented Device Lab exception.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~PowerStage|FullyQualifiedName~PowerTests"`.
- Resolves: LABUI-007, LABUI-011, LABUI-012, LABUI-023.

#### B171 Lighting and Sleep stage controllers

- Domain: lab. Depends on: B170.
- Files: src/WSGM.DeviceLab/Wizard/Stages/Power/LightingTests.cs (new); Wizard/Stages/SleepStage.cs (new); Gui/WizardWindow.ClawLighting.cs, Sleep.cs (deleted); `Transports/LabAuraLighting.cs`; `tests`.
- Steps: LABUI-B10 without a schema change: the Claw RGB test is gated by the record's lighting or hid-output mechanism; Aura zone names stay transport constants beside LabAuraLighting; the Claw lighting layout is passed as an argument (LABCORE-025). Tests: Claw profile restored exactly after a cancelled test, Aura record cleared after the tester is told, a skipped sleep cycle finishes Skipped.
- Tests: `dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~LightingTests|FullyQualifiedName~SleepStage"`.
- Resolves: LABCORE-025.

#### B172 HidHide adapter sharing and test hygiene

- Domain: input. Depends on: B171, B080. Decisions: D3.
- Files: `src/WSGM/Interop/NativeHidHide*.cs`; `src/WSGM/Shell/HidHideControl.cs`; `src/WSGM/Shell/HidHideOwnership.cs`; `src/WSGM.DeviceLab/Wizard/HidHideAllowance.cs`; `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj`; tests/WSGM.Tests/Fakes/FakeButtonSource.cs -> tests/Shared; `WSGM.UiTests.csproj`; `InputTests.cs`.
- Steps: INPUT-B8 under D3 (Device Lab becomes GPL and links the WSGM files as they are, with nothing relicensed or duplicated): NativeHidHide.Paths merges into NativeHidHide once FromDosPath stops logging and the Lab uses the shared control adapter (032); consistent IOCTL threading (033); FakeButtonSource moves to tests/Shared; the getter test goes.
- Tests: dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~HidHide|FullyQualifiedName~InputTests"; dotnet test tests\WSGM.DeviceLab.Tests\WSGM.DeviceLab.Tests.csproj --filter "FullyQualifiedName~HidHide".
- Resolves: INPUT-032, INPUT-033.

### Phase G: moves, documentation, reconciliation and the gate

#### B173 Surviving linked sources move to src/Shared; Device Lab becomes GPL

- Domain: build. Depends on: B172, B141, B031, B029. Decisions: D3.
- Files: `src/Shared/{Process,Boot,Install,Launch}/*`; `WSGM.csproj`; `WSGM.Launch.csproj`; `WSGM.LogonService.csproj`; `WSGM.PackagedLaunch.csproj`; `WSGM.Install.csproj`; `WSGM.DeviceLab.csproj`; `src/WSGM.DeviceLab/LICENSE`; `src/WSGM.DeviceLab/README.md`; `src/WSGM.DeviceLab/THIRD_PARTY_NOTICES.md`; .editorconfig blocks; ProjectGraphTests rows; WSGM.Tests.csproj (Launch alias); AGENTS/README path mentions and src/WSGM.DeviceLab/AGENTS.md (diffs shown with the batch and applied, D4).
- Steps: BUILD-B6 after the session and install batches, moving only files whose sharing survived them (019); NativePackageSource goes into WSGM.DeviceLab, its only consumer (LIBRARY-025); Device Lab is relicensed as GPL-3.0-or-later (D3): its LICENSE file, csproj licence metadata, README, AGENTS and notices change, the interop files it links (Kernel32, NativePathIdentity, NativeHidHide) stay GPL where they are with no relicensing, duplication or src/Shared/Interop folder, and the SDKs stay MIT (LABCORE-039, A02-F022); the Launch alias plus extern alias in the five test files that use Launch types (023); the INSTALL-032 home for launcher-shared sources; .editorconfig blocks follow the files.
- Tests: dotnet build WSGM.slnx -c Release -p:SkipNativeArtifacts=true; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~Boundaries|FullyQualifiedName~Launch|FullyQualifiedName~Logon|FullyQualifiedName~PackagedLaunch|FullyQualifiedName~BootManifest|FullyQualifiedName~AtomicFile".
- Resolves: BUILD-019, BUILD-023, INSTALL-032, LIBRARY-025, LABCORE-039, A02-F022.

#### B174 Source scrapers replaced; tools compiled in the gate

- Domain: build. Depends on: B173, B139, B141. Decisions: D12.
- Files: `eng/check-steam-module-discovery.mjs`; `package.json`; `tools/WsgmLibTest/{cdp,run-prod-sort,qam-harness}.mjs`; tools/WsgmLibTest/probe-*.js (retired); eng/verify.ps1 (tools step); `src/WSGM/Properties/AssemblyInfo.cs`; tools/DeckSpike and tools/SteamReceiver (deleted, D12).
- Steps: BUILD-B8 remainder: the C# call-order regexes become a WSGM.Tests Steam launch ordering test (013); live tools read ids from the shipped asset instead of scraping C# (016); the probe-*.js registry sweeps are retired because they break the CEF rule (BUILD Q4); tools/DeckSpike and tools/SteamReceiver are deleted (D12) and the remaining tools compile in verify with one restore that includes them and --no-restore builds (003, V-005); InternalsVisibleTo(DeckSpike) goes with DeckSpike (017).
- Tests: npm run steam-assets:check; npm run steam-assets:claims; dotnet build of each remaining tool; dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~SteamLaunchOrdering".
- Resolves: BUILD-003, BUILD-013, BUILD-016, BUILD-017, BUILD-V-005.

#### B175 Owned-staging ceremony replaced by one rule

- Domain: build. Depends on: B174. Decisions: D4.
- Files: `eng/publish-device-lab.ps1`; `eng/pack-device.ps1`; `eng/build-bundle.ps1`; `eng/plugin-package-common.ps1`; `tests/WSGM.DeviceLab.Tests/Eng/DevicePackageOutputTests.cs`; eng/AGENTS.md (diff); src/WSGM.DeviceLab/AGENTS.md (diff).
- Steps: BUILD-B9 with its D4 guidance diffs shown and applied: repository-owned output is cleared and recreated and anything outside the repository is refused, replacing four owned-staging implementations (030); the eng helper is no longer tested from the Device Lab suite by spawning pwsh (028).
- Tests: script parse; local ./eng/publish-device-lab.ps1 into publish/DeviceLab; ./eng/pack-device.ps1 -Source src/WSGM.Device.Msi.Claw -RequireGlyphs (offline).
- Resolves: BUILD-028, BUILD-030.

#### B176 Test helpers and build documentation

- Domain: build. Depends on: B175.
- Files: `tests/Shared/*`; `tests/WSGM.Tests/Builders/SplashConfigBuilder.cs`; `WSGM.UiTests.csproj`; `eng/update-ui-baselines.ps1`; `eng/dev-deploy.ps1`; `docs/ui.md`; `docs/logging.md`; `docs/steam-cef-system.md`; AGENTS.md and tests/WSGM.Tests/AGENTS.md (diffs shown with the batch and applied, D4).
- Steps: BUILD-B10: shared helpers renamed and single-consumer ones moved (025, 036); a completion-signal idiom documented for async tests (026); update-ui-baselines derives its case list (033); a stated target-framework rule (024); dev-deploy keeps writing its swap script as today (035 is dropped by maintainer decision, security theater); stale guidance and docs (034, logging docs updated for the kept PluginTrace).
- Tests: npm run format:check; dotnet test tests\WSGM.UiTests\WSGM.UiTests.csproj --filter "FullyQualifiedName~WSGM.UiTests.Settings".
- Resolves: BUILD-024, BUILD-025, BUILD-026, BUILD-033, BUILD-034, BUILD-036.

#### B177 Documentation pass and guidance proposals

- Domain: docs. Depends on: B176, B156, B145, B153, B062, B072, B132, B135, B101, B107, B149. Submodule: steam-ui-toolkit.
- Files: docs/*.md (boot-and-shell, elevation, device-integration, plugin-system, rtss, steam-cef-system, decisions); package READMEs and PROVENANCE (Claw, Ally, Intel, NVIDIA, AMD, IR); external/steam-ui-toolkit/docs/reference.md and README.md (child commit); AGENTS.md proposals as separate diffs.
- Steps: Z02 after simplification: describe actual owners, lifecycle and shutdown order, contracts, migration, error and uncertainty semantics, and real acceptance gaps; GPUIR-B9 package docs (037), PACKAGES-B7 recovery rule (readback-based recovery states for the Claw only, none for the Ally, D9), no command rollback, watchdog disarm, Ally write-through restore and the Ally write order SPL, then SPPT and FPPT, as HC writes them, with the Ally README and PROVENANCE citing HC (D8); the Device Lab licence (D3); toolkit reference with the exported JS script API inventory and retain, internalize or remove decisions (TOOLKITJS-028, critic 2.6) (no bridge trust-boundary note: U02A-SUTC-004 is dropped by maintainer decision, security theater), and the toolkit C# public API inventory with the plugin-contract closure marked; Device Lab AGENTS keeps describing the intended zero and probe rules that the code now meets (LABCORE-026, LABCORE-V-006); Settings guidance for the shared-field table (SETTINGS-042). Every AGENTS.md change is a separate diff shown with its batch and applied (D4), followed by eng/check-agent-guidance.ps1. Child doc edits commit and push in the child first. npm run format for parent Markdown only.
- Tests: npm run format:check; ./eng/check-agent-guidance.ps1; child: npm run prelude:claims in external/steam-ui-toolkit.
- Resolves: GPUIR-037, TOOLKITJS-028, LABCORE-026, LABCORE-V-006, SETTINGS-042.

#### B178 Ledger and coverage reconciliation

- Domain: docs. Depends on: B177.
- Files: (read-only on source) _plan/refactor-2.1/ledger-reconciliation.md.
- Steps: Z03: map every id in claude-findings-disposition.json (387 saved plus 12 provisional records, including the 165 ledger ids no review cites) and every A01, A02 and A02S01 id to the batch that resolved it or to a written no-change reason, using the Codex task mapping (W01, W02 -> WDC batches; T01, T02 -> toolkit C# batches; T03 -> toolkit JS and surface batches; F01 -> config batches; H01 -> session and install batches; H02 -> session shutdown) and the cited code; include the critic's 43 unreferenced Low/Nit ids and the six medium rows. Record the 21 production files the critic lists as 'grep-swept, not line-read' and their review status, and audit the 159 never-named test files for getter-only and copied-predicate tests, filing any real fix as an appended batch before B179. Update this plan's appendix. No source edits.
- Tests: `none (read-only)`.
- Resolves: structural move, no finding of its own (see Appendix A).

#### B179 Final automated gate and hand-off to manual acceptance

- Domain: docs. Depends on: B178.
- Files: (no source).
- Steps: Z04 on the committed head: frozen-foundation check (git diff 1329813f -- '*.csproj' Directory.Build.props external/viiper shows no Avalonia, FluentAvaloniaUI or VIIPER version or gitlink change); warning-free Release build including native payloads; .\eng\verify.ps1 once (now safe: the power tests use the port, config tests use unique mutexes, the topology wait test is gone); full WDC suite on net8 and net10; full toolkit .NET and Node suites; UI baselines unchanged and reviewed (refresh only for an intended change, after review). Then hand the build and the updated manual matrix to the maintainer; build.ps1 only on explicit direction. A failure is fixed in its owning area and rerun with its narrow filter; the full gate repeats only for broad impact, with the reason stated first.
- Tests: `.\eng\verify.ps1 (once, on the committed head)`.
- Resolves: structural move, no finding of its own (see Appendix A).

## Appendix A. Every finding id and where it is resolved

### B024 closure additions and corrections

| Id | Batch or reason |
| --- | --- |
| INSTALL-C-002 | B180, exact executable-filename matching; body in install-closure.md. |
| UNCOVERED-002 | B180, restore follow-up omitted from B016. |
| U04B-LFA-041 | B180, delete the unused EnableLua snapshot field and read. |

Ids come from the 19 domain reports, their verify files, the critic (`CRIT-`), the maintainer report (`USER-`) and the Codex audits (`A01-`, `A02-`, `A02S01-`). A batch id assigns the fix (two ids when split); its implementation status and check evidence establish what has actually landed. Otherwise the table gives the reason it needs no code change. Refuted findings are listed with the refutation. Install ids whose bodies were never written are individually retired by the closure review without inventing a subject or claiming a proven non-defect.


### USER

| Id | Batch or reason |
| --- | --- |
| USER-001 | B005 |

### CRIT

| Id | Batch or reason |
| --- | --- |
| CRIT-001 | B016 |
| CRIT-002 | B138 |
| CRIT-003 | B017 |
| CRIT-004 | B112 |
| CRIT-005 | B038 |
| CRIT-006 | B138 |

### SESSION

| Id | Batch or reason |
| --- | --- |
| SESSION-001 | B111 |
| SESSION-002 | B006 |
| SESSION-003 | Refuted (forced Shutdown skips the handler in Settings); the duplicate Steam pre-stop residual is handled in B111. |
| SESSION-004 | B111 |
| SESSION-005 | B111 |
| SESSION-006 | B111 |
| SESSION-007 | B006 |
| SESSION-008 | B111 |
| SESSION-009 | B006 |
| SESSION-010 | B124 |
| SESSION-011 | B124 |
| SESSION-012 | Refuted: IsDesktopShellRunning stays the authority; no mode-authority query (A3 dropped). |
| SESSION-013 | B124 |
| SESSION-014 | B124 |
| SESSION-015 | B124 |
| SESSION-016 | B115 |
| SESSION-017 | B039 |
| SESSION-018 | B116 |
| SESSION-019 | B140 |
| SESSION-020 | B136 |
| SESSION-021 | B136 |
| SESSION-022 | B115 |
| SESSION-023 | B115 |
| SESSION-024 | B115 |
| SESSION-025 | B115 |
| SESSION-026 | B116 |
| SESSION-027 | B042 |
| SESSION-028 | B141 |
| SESSION-029 | B115 |
| SESSION-030 | B140 |
| SESSION-031 | B111 |
| SESSION-032 | B006 |
| SESSION-033 | B140 |
| SESSION-034 | B112 |
| SESSION-035 | B112 |
| SESSION-036 | B112 |
| SESSION-037 | B112 |
| SESSION-038 | B112 |
| SESSION-039 | B112 |
| SESSION-040 | B112 |
| SESSION-041 | B112 |
| SESSION-042 | B112 |
| SESSION-043 | B114 |
| SESSION-044 | B114 |
| SESSION-045 | B114 |
| SESSION-046 | B114 |
| SESSION-047 | B031 |
| SESSION-048 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| SESSION-049 | B114 |
| SESSION-050 | B114 |
| SESSION-051 | B114 |
| SESSION-052 | B114 |
| SESSION-053 | B114 |
| SESSION-054 | Plausible only; IsRunning semantics kept until log evidence of a lingering helper exists. |
| SESSION-055 | B112 |
| SESSION-056 | B115 |
| SESSION-057 | B141 |
| SESSION-058 | B141 |
| SESSION-059 | B115 |
| SESSION-060 | B115 |
| SESSION-061 | B141 |
| SESSION-062 | B141 |
| SESSION-063 | B115 |
| SESSION-V-001 | B006 |
| SESSION-V-002 | B006 |
| SESSION-V-003 | B006 |
| SESSION-V-004 | B114 |
| SESSION-V-005 | B006 |

### CONFIG

| Id | Batch or reason |
| --- | --- |
| CONFIG-001 | B039 |
| CONFIG-002 | B040 |
| CONFIG-003 | B039 |
| CONFIG-004 | B039 |
| CONFIG-005 | B039 |
| CONFIG-006 | B038 |
| CONFIG-007 | B038 |
| CONFIG-008 | B039 |
| CONFIG-009 | B039 |
| CONFIG-010 | B038 |
| CONFIG-011 | B037 |
| CONFIG-012 | B039 |
| CONFIG-013 | B038 |
| CONFIG-014 | B038 |
| CONFIG-015 | B038 |
| CONFIG-016 | B038 |
| CONFIG-017 | B068 |
| CONFIG-018 | B040 |
| CONFIG-019 | B040 |
| CONFIG-020 | B040 |
| CONFIG-021 | B038 |
| CONFIG-022 | B013 |
| CONFIG-023 | B040 |
| CONFIG-024 | B042 |
| CONFIG-025 | B042 |
| CONFIG-026 | B042 |
| CONFIG-027 | Refuted: WDC EndpointCallback.Raise swallows the late release; nothing changes. |
| CONFIG-028 | B098 |
| CONFIG-029 | B098 |
| CONFIG-030 | B041 |
| CONFIG-031 | B041 |
| CONFIG-032 | B041 |
| CONFIG-033 | Reviewed no-change: the Log.Change key map bounds memory, not content. |
| CONFIG-034 | B041 |
| CONFIG-035 | B038 |
| CONFIG-036 | B039 |
| CONFIG-037 | B039 |
| CONFIG-038 | B018 |
| CONFIG-039 | B039 |
| CONFIG-040 | B039 |
| CONFIG-041 | B038 |
| CONFIG-042 | Refuted: deliberate normalization of injected configs. |
| CONFIG-043 | B040 |
| CONFIG-044 | B039 |
| CONFIG-045 | B040 |
| CONFIG-046 | B116 |
| CONFIG-V-001 | B039 |
| CONFIG-V-002 | B018 |
| CONFIG-V-003 | B122 |
| CONFIG-V-004 | Attended check first (elevated holder, unelevated opener); change the mutex ACL only if the check reproduces the failure. Listed under attended evidence. |
| CONFIG-V-005 | B039 |
| CONFIG-V-006 | B116 |
| CONFIG-V-007 | B040 |
| CONFIG-V-008 | B068 |

### DEVICE

| Id | Batch or reason |
| --- | --- |
| DEVICE-001 | B008 |
| DEVICE-002 | B081 |
| DEVICE-003 | B083 |
| DEVICE-004 | B083 |
| DEVICE-005 | B009 |
| DEVICE-006 | B082 |
| DEVICE-007 | B082 |
| DEVICE-008 | B089 |
| DEVICE-009 | B091 |
| DEVICE-010 | B089 |
| DEVICE-011 | B091 |
| DEVICE-012 | B091 |
| DEVICE-013 | B038 |
| DEVICE-014 | B088 |
| DEVICE-015 | B083 |
| DEVICE-016 | B083 |
| DEVICE-017 | B091 |
| DEVICE-018 | B093 |
| DEVICE-019 | B091 |
| DEVICE-020 | B091 |
| DEVICE-021 | B082 |
| DEVICE-022 | B093 |
| DEVICE-023 | B093 |
| DEVICE-024 | B093 |
| DEVICE-025 | B093 |
| DEVICE-026 | B091 |
| DEVICE-027 | B083 |
| DEVICE-028 | B082 |
| DEVICE-029 | B088 |
| DEVICE-030 | B081 |
| DEVICE-031 | B083 |
| DEVICE-032 | B089 |
| DEVICE-033 | B093 |
| DEVICE-034 | B093 |
| DEVICE-035 | B089 |
| DEVICE-036 | B093 |
| DEVICE-037 | B091 |
| DEVICE-038 | B082 |
| DEVICE-039 | B093 |
| DEVICE-040 | B090 |
| DEVICE-041 | B088 |
| DEVICE-042 | B088 |
| DEVICE-V-001 | B011 |
| DEVICE-V-002 | B012 |
| DEVICE-V-003 | B012 |
| DEVICE-V-004 | B012 |
| DEVICE-V-005 | B089 |

### INPUT

| Id | Batch or reason |
| --- | --- |
| INPUT-001 | B009 |
| INPUT-002 | B009 |
| INPUT-003 | Refuted: no caller produces the hidden pad; tidy-up folded into B009. |
| INPUT-004 | B009 |
| INPUT-005 | B073 |
| INPUT-006 | B073 |
| INPUT-007 | B073 |
| INPUT-008 | B074 |
| INPUT-009 | B075 |
| INPUT-010 | B076 |
| INPUT-011 | B076 |
| INPUT-012 | B077 |
| INPUT-013 | B078 |
| INPUT-014 | B074 |
| INPUT-015 | B073 |
| INPUT-016 | B073 |
| INPUT-017 | B073 |
| INPUT-018 | B074 |
| INPUT-019 | B073 |
| INPUT-020 | B073 |
| INPUT-021 | B073 |
| INPUT-022 | B078 |
| INPUT-023 | B078 |
| INPUT-024 | B076 |
| INPUT-025 | B080 |
| INPUT-026 | No defect; options, IDirectionalControl and TimeProvider injection would be test-only mechanism (simplify rule). |
| INPUT-027 | No change: the 64-step guard is a loop bound, the proposed replacement can loop forever. |
| INPUT-028 | B080 |
| INPUT-029 | B080 |
| INPUT-030 | B079 |
| INPUT-031 | B074 |
| INPUT-032 | B172 |
| INPUT-033 | B172 |
| INPUT-034 | B009 |
| INPUT-035 | Refuted: ControllerProcessPriority relies on _stateGate; moving the call adds a race or a lock. |
| INPUT-036 | B074 |
| INPUT-037 | B080 |
| INPUT-038 | B076 |
| INPUT-039 | B073 |
| INPUT-V-001 | B009 |
| INPUT-V-002 | B009 |
| INPUT-V-003 | B009 |
| INPUT-V-004 | B009 |
| INPUT-V-005 | B009 |

### SDK

| Id | Batch or reason |
| --- | --- |
| SDK-001 | B043 |
| SDK-002 | B010 |
| SDK-003 | B010 |
| SDK-004 | B146 |
| SDK-005 | B044 |
| SDK-006 | B044 |
| SDK-007 | B142 |
| SDK-008 | B147 |
| SDK-009 | B147 |
| SDK-010 | B045 |
| SDK-011 | B142 |
| SDK-012 | B147 |
| SDK-013 | B146 |
| SDK-014 | B146 |
| SDK-015 | B081 |
| SDK-016 | B142 |
| SDK-017 | Refuted: no package turns missing readback into a write gate; the real gate is SDK-V-002 (B022). |
| SDK-018 | B052 |
| SDK-019 | B052 |
| SDK-020 | B052 |
| SDK-021 | B044 |
| SDK-022 | B148 |
| SDK-023 | B148 |
| SDK-024 | B148 |
| SDK-025 | B146 |
| SDK-026 | B142 |
| SDK-027 | B142 |
| SDK-028 | B142 |
| SDK-029 | B142 |
| SDK-030 | B052 |
| SDK-031 | B147 |
| SDK-032 | B147 |
| SDK-033 | B146 |
| SDK-034 | B010 |
| SDK-035 | No change: Start and Stop run on the lifecycle lane; a lock adds mechanism for a safe race. |
| SDK-036 | B044 |
| SDK-037 | B044 |
| SDK-038 | B044 |
| SDK-039 | B149 |
| SDK-040 | Accepted bounded no-change: deadline registrations live at most 20 s and pruning cannot see disposed sources. |
| SDK-041 | No change: the 250 ms observation cadence is preserved by the plan. |
| SDK-042 | B142 |
| SDK-043 | B142 |
| SDK-044 | Accepted no-change: merging the two settings contracts needs a migration and UI work nobody asked for. |
| SDK-045 | Accepted no-change: the two icon vocabularies are documented. |
| SDK-046 | B142 |
| SDK-047 | B142 |
| SDK-048 | B082 |
| SDK-049 | B146 |
| SDK-V-001 | B147 |
| SDK-V-002 | B022 |
| SDK-V-003 | B147 |
| SDK-V-004 | B142 |
| SDK-V-005 | B142 |
| SDK-V-006 | B052 |
| SDK-V-007 | B142 |

### PACKAGES

| Id | Batch or reason |
| --- | --- |
| PACKAGES-001 | B143 |
| PACKAGES-002 | B021 |
| PACKAGES-003 | B021 |
| PACKAGES-004 | B021 |
| PACKAGES-005 | B022 |
| PACKAGES-006 | B023 |
| PACKAGES-007 | B022 |
| PACKAGES-008 | B143 |
| PACKAGES-009 | B143 |
| PACKAGES-010 | B143 |
| PACKAGES-011 | No change: maintainer decision D7 (WinRT first like HC, which is today's order). |
| PACKAGES-012 | B022 |
| PACKAGES-013 | No change: Ally power and fans are non-suspendable and stay Owned; re-acquiring on resume names no defect. |
| PACKAGES-014 | B144 |
| PACKAGES-015 | B144 |
| PACKAGES-016 | B021 |
| PACKAGES-017 | B145 |
| PACKAGES-018 | B145 |
| PACKAGES-019 | No change: the overlap follows only an abnormal dispose timeout; retention would add state without a defect. |
| PACKAGES-020 | B022 |
| PACKAGES-021 | B145 |
| PACKAGES-022 | B021 |
| PACKAGES-023 | B021 |
| PACKAGES-024 | No change: ActiveClock and delays stay process-wide (SDK keeps the process clock); test-only injection is mechanism. |
| PACKAGES-025 | No change: PluginTrace stays the process sink (critic conflict 18). |
| PACKAGES-026 | B142 |
| PACKAGES-027 | B144 |
| PACKAGES-028 | B144 |
| PACKAGES-029 | No change: the reader-fault protocols differ and have not drifted. |
| PACKAGES-030 | B144 |
| PACKAGES-031 | B022 |
| PACKAGES-032 | B145 |
| PACKAGES-033 | B145 |
| PACKAGES-034 | B142 |
| PACKAGES-035 | B143 |
| PACKAGES-036 | B143 |
| PACKAGES-037 | B143 |
| PACKAGES-038 | B145 |
| PACKAGES-039 | No change: the 64-entry channel is a real hook-thread boundary. |
| PACKAGES-V-001 | B021 |
| PACKAGES-V-002 | B143 |
| PACKAGES-V-003 | B022 |
| PACKAGES-V-004 | B022 |
| PACKAGES-V-005 | B022 |
| PACKAGES-V-006 | B021 |

### GPUIR

| Id | Batch or reason |
| --- | --- |
| GPUIR-001 | B153 |
| GPUIR-002 | B150 |
| GPUIR-003 | B150 |
| GPUIR-004 | B156 |
| GPUIR-005 | B153 |
| GPUIR-006 | B151 |
| GPUIR-007 | B153 |
| GPUIR-008 | B151 |
| GPUIR-009 | Refuted: the coordinator never calls sync while the session is lost and re-syncs on reopen. |
| GPUIR-010 | B153 |
| GPUIR-011 | B153 |
| GPUIR-012 | B152 |
| GPUIR-013 | B154 |
| GPUIR-014 | Refuted: PluginHost already cancels the active action budget on stop. |
| GPUIR-015 | B151 |
| GPUIR-016 | No change: the process-static epoch matches ADLX's process-wide scope. |
| GPUIR-017 | B151 |
| GPUIR-018 | B151 |
| GPUIR-019 | B151 |
| GPUIR-020 | B152 |
| GPUIR-021 | B150 |
| GPUIR-022 | B152 |
| GPUIR-023 | B152 |
| GPUIR-024 | B153 |
| GPUIR-025 | No change: the reopen backoff stays so a refused ctlInit does not cycle the DLL every 10 s. |
| GPUIR-026 | B153 |
| GPUIR-027 | Accepted duplication: Intel keeps its own CCD query (GPUIR-B5 skipped). |
| GPUIR-028 | B154 |
| GPUIR-029 | B154 |
| GPUIR-030 | B154 |
| GPUIR-031 | B156 |
| GPUIR-032 | B156 |
| GPUIR-033 | B154 |
| GPUIR-034 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| GPUIR-035 | IR half refuted; GPU half is a benign atomic reference swap. |
| GPUIR-036 | B153 |
| GPUIR-037 | B177 |
| GPUIR-038 | B152 |
| GPUIR-039 | No change: removing Intel's frame-rate limiter would remove a visible control (requirement 9). |
| GPUIR-040 | B148 |
| GPUIR-V-001 | B151 |
| GPUIR-V-002 | B150 |
| GPUIR-V-003 | B151 |
| GPUIR-V-004 | B154 |
| GPUIR-V-005 | B151 |
| GPUIR-V-006 | B150 |
| GPUIR-V-007 | B152 |

### STEAMHOST

| Id | Batch or reason |
| --- | --- |
| STEAMHOST-001 | B136 |
| STEAMHOST-002 | B084 |
| STEAMHOST-003 | B085 |
| STEAMHOST-004 | B054 |
| STEAMHOST-005 | B086 |
| STEAMHOST-006 | B054, B086 |
| STEAMHOST-007 | B084 |
| STEAMHOST-008 | B084 |
| STEAMHOST-009 | B084 |
| STEAMHOST-010 | B137 |
| STEAMHOST-011 | B136 |
| STEAMHOST-012 | B136 |
| STEAMHOST-013 | B137 |
| STEAMHOST-014 | B137 |
| STEAMHOST-015 | B138 |
| STEAMHOST-016 | B053 |
| STEAMHOST-017 | B139 |
| STEAMHOST-018 | B139 |
| STEAMHOST-019 | B139 |
| STEAMHOST-020 | B139 |
| STEAMHOST-021 | B087 |
| STEAMHOST-022 | B084 |
| STEAMHOST-023 | B087 |
| STEAMHOST-024 | B084 |
| STEAMHOST-025 | B084 |
| STEAMHOST-026 | B084 |
| STEAMHOST-027 | B086 |
| STEAMHOST-028 | B136 |
| STEAMHOST-029 | B138 |
| STEAMHOST-030 | B138 |
| STEAMHOST-031 | B138 |
| STEAMHOST-032 | B138 |
| STEAMHOST-033 | B139 |
| STEAMHOST-034 | B139 |
| STEAMHOST-035 | B137 |
| STEAMHOST-036 | B087 |
| STEAMHOST-037 | B054 |
| STEAMHOST-038 | B084 |
| STEAMHOST-039 | B136 |
| STEAMHOST-040 | B136 |
| STEAMHOST-041 | B084 |
| STEAMHOST-042 | B139 |
| STEAMHOST-043 | B139 |
| STEAMHOST-044 | B056 |
| STEAMHOST-045 | No change: published section ids stay today's strings, so no fold-id migration is needed. |
| STEAMHOST-046 | B136 |
| STEAMHOST-047 | B085 |
| STEAMHOST-V-001 | B018 |
| STEAMHOST-V-002 | B018 |
| STEAMHOST-V-003 | B032 |
| STEAMHOST-V-004 | B138 |
| STEAMHOST-V-005 | B053 |
| STEAMHOST-V-006 | B084 |
| STEAMHOST-V-007 | B086 |
| STEAMHOST-V-008 | B087 |
| STEAMHOST-V-009 | B057 |
| STEAMHOST-V-010 | B085 |

### TOOLKITCS

| Id | Batch or reason |
| --- | --- |
| TOOLKITCS-001 | B049 |
| TOOLKITCS-002 | B048 |
| TOOLKITCS-003 | B053 |
| TOOLKITCS-004 | B048 |
| TOOLKITCS-005 | B048 |
| TOOLKITCS-006 | B048 |
| TOOLKITCS-007 | B048 |
| TOOLKITCS-008 | B048 |
| TOOLKITCS-009 | B048 |
| TOOLKITCS-010 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| TOOLKITCS-011 | B048 |
| TOOLKITCS-012 | B054 |
| TOOLKITCS-013 | B053 |
| TOOLKITCS-014 | B053 |
| TOOLKITCS-015 | B053 |
| TOOLKITCS-016 | B053 |
| TOOLKITCS-017 | Refuted: the connection already logs the dropped oversized binding. |
| TOOLKITCS-018 | B053 |
| TOOLKITCS-019 | B053 |
| TOOLKITCS-020 | B049 |
| TOOLKITCS-021 | B053 |
| TOOLKITCS-022 | B053 |
| TOOLKITCS-023 | B053 |
| TOOLKITCS-024 | B053 |
| TOOLKITCS-025 | B054 |
| TOOLKITCS-026 | B054 |
| TOOLKITCS-027 | B054 |
| TOOLKITCS-028 | B054 |
| TOOLKITCS-029 | B054 |
| TOOLKITCS-030 | B054 |
| TOOLKITCS-031 | B054, B056 |
| TOOLKITCS-032 | B054 |
| TOOLKITCS-033 | B049 |
| TOOLKITCS-034 | B049 |
| TOOLKITCS-035 | B049 |
| TOOLKITCS-036 | B054 |
| TOOLKITCS-037 | B049 |
| TOOLKITCS-038 | B054 |
| TOOLKITCS-039 | B054 |
| TOOLKITCS-040 | B049 |
| TOOLKITCS-041 | B049 |
| TOOLKITCS-042 | B049 |
| TOOLKITCS-043 | B049 |
| TOOLKITCS-044 | B054 |
| TOOLKITCS-045 | B049 |
| TOOLKITCS-046 | B056 |
| TOOLKITCS-047 | B057 |
| TOOLKITCS-048 | B049 |
| TOOLKITCS-049 | B056 |
| TOOLKITCS-050 | B056 |
| TOOLKITCS-051 | B049 |
| TOOLKITCS-052 | B049 |
| TOOLKITCS-053 | B056 |
| TOOLKITCS-054 | B049 |
| TOOLKITCS-055 | B059 |
| TOOLKITCS-056 | B056 |
| TOOLKITCS-057 | B056 |
| TOOLKITCS-058 | B056 |
| TOOLKITCS-059 | B056 |
| TOOLKITCS-060 | B056 |
| TOOLKITCS-061 | B056 |
| TOOLKITCS-062 | B056 |
| TOOLKITCS-063 | B056 |
| TOOLKITCS-064 | B062 |
| TOOLKITCS-065 | B062 |
| TOOLKITCS-066 | B062 |
| TOOLKITCS-067 | B062 |
| TOOLKITCS-068 | B062 |
| TOOLKITCS-069 | B062 |
| TOOLKITCS-070 | B062 |
| TOOLKITCS-071 | B062 |
| TOOLKITCS-V-001 | B052 |
| TOOLKITCS-V-002 | B049 |
| TOOLKITCS-V-003 | B053 |
| TOOLKITCS-V-004 | B056 |

### TOOLKITJS

| Id | Batch or reason |
| --- | --- |
| TOOLKITJS-001 | B050 |
| TOOLKITJS-002 | B057 |
| TOOLKITJS-003 | B057 |
| TOOLKITJS-004 | B055 |
| TOOLKITJS-005 | B051 |
| TOOLKITJS-006 | B061 |
| TOOLKITJS-007 | B051 |
| TOOLKITJS-008 | B051 |
| TOOLKITJS-009 | B047 |
| TOOLKITJS-010 | B047 |
| TOOLKITJS-011 | B047 |
| TOOLKITJS-012 | B047 |
| TOOLKITJS-013 | B057 |
| TOOLKITJS-014 | B057 |
| TOOLKITJS-015 | B058 |
| TOOLKITJS-016 | B058 |
| TOOLKITJS-017 | B050 |
| TOOLKITJS-018 | B049 |
| TOOLKITJS-019 | B050 |
| TOOLKITJS-020 | B050 |
| TOOLKITJS-021 | B059 |
| TOOLKITJS-022 | B060 |
| TOOLKITJS-023 | B060 |
| TOOLKITJS-024 | B060 |
| TOOLKITJS-025 | B057 |
| TOOLKITJS-026 | B051 |
| TOOLKITJS-027 | No change: a toolkit-wide Prettier pass contradicts the toolkit AGENTS style rule; the shipped asset is already formatted. |
| TOOLKITJS-028 | B177 |
| TOOLKITJS-029 | B060 |
| TOOLKITJS-030 | Refuted: host disposal is terminal and the bridge replays state. |
| TOOLKITJS-031 | B060 |
| TOOLKITJS-032 | B060 |
| TOOLKITJS-033 | No change: the Extensions panel keeps its own subscription until a fixture proves the adopted rerender reaches it. |
| TOOLKITJS-034 | B060 |
| TOOLKITJS-035 | B057 |
| TOOLKITJS-036 | B051 |
| TOOLKITJS-037 | B032 |
| TOOLKITJS-038 | B047 |
| TOOLKITJS-V-001 | B050 |
| TOOLKITJS-V-002 | B050 |
| TOOLKITJS-V-003 | B051 |
| TOOLKITJS-V-004 | B051 |
| TOOLKITJS-V-005 | B060 |
| TOOLKITJS-V-006 | B057 |
| TOOLKITJS-V-007 | B057 |
| TOOLKITJS-V-008 | B059 |
| TOOLKITJS-V-009 | B058 |

### WDC

| Id | Batch or reason |
| --- | --- |
| WDC-001 | B067 |
| WDC-002 | B038 |
| WDC-003 | B067 |
| WDC-004 | B069 |
| WDC-005 | B065 |
| WDC-006 | B071 |
| WDC-007 | B067 |
| WDC-008 | B065 |
| WDC-009 | B066 |
| WDC-010 | B069 |
| WDC-011 | B071 |
| WDC-012 | B001 |
| WDC-013 | B002 |
| WDC-014 | B063 |
| WDC-015 | B067 |
| WDC-016 | B065 |
| WDC-017 | B065 |
| WDC-018 | B046 |
| WDC-019 | B072 |
| WDC-020 | B072 |
| WDC-021 | B067 |
| WDC-022 | B064 |
| WDC-023 | B069 |
| WDC-024 | B071 |
| WDC-025 | B067 |
| WDC-026 | B046 |
| WDC-V-001 | B067 |
| WDC-V-002 | B064 |
| WDC-V-003 | B065 |
| WDC-V-004 | B067 |
| WDC-V-005 | B066 |

### WINSVC

| Id | Batch or reason |
| --- | --- |
| WINSVC-001 | B094 |
| WINSVC-002 | B094 |
| WINSVC-003 | B099 |
| WINSVC-004 | B096 |
| WINSVC-005 | B094 |
| WINSVC-006 | B094 |
| WINSVC-007 | B097 |
| WINSVC-008 | B094 |
| WINSVC-009 | B090 |
| WINSVC-010 | B017 |
| WINSVC-011 | Refuted: the two original-mode owners are a recorded design and their revision guard prevents a black screen. |
| WINSVC-012 | B098 |
| WINSVC-013 | B095 |
| WINSVC-014 | B095 |
| WINSVC-015 | B095 |
| WINSVC-016 | B094 |
| WINSVC-017 | B114 |
| WINSVC-018 | B094 |
| WINSVC-019 | B099 |
| WINSVC-020 | B099 |
| WINSVC-021 | B065 |
| WINSVC-022 | B063 |
| WINSVC-023 | B064 |
| WINSVC-024 | B101 |
| WINSVC-025 | B101 |
| WINSVC-026 | B094 |
| WINSVC-027 | B098 |
| WINSVC-028 | B070 |
| WINSVC-029 | B065 |
| WINSVC-030 | B090 |
| WINSVC-031 | B100 |
| WINSVC-032 | B100 |
| WINSVC-033 | B100 |
| WINSVC-034 | Refuted: the no-snapshot restore writes the secure default on purpose; kept in B071. |
| WINSVC-035 | B097 |
| WINSVC-036 | B097 |
| WINSVC-037 | B115 |
| WINSVC-038 | B101 |
| WINSVC-039 | B101 |
| WINSVC-040 | B094 |
| WINSVC-041 | B094 |
| WINSVC-042 | B094 |
| WINSVC-043 | B101 |
| WINSVC-044 | B065 |
| WINSVC-045 | No change: both window procedures only post, so a guard there is cosmetic; the real guard is on EffectivePowerModeNotification (B091). |
| WINSVC-046 | B101 |
| WINSVC-V-001 | B094 |
| WINSVC-V-002 | B101 |
| WINSVC-V-003 | B091 |
| WINSVC-V-004 | B094 |
| WINSVC-V-005 | B065 |
| WINSVC-V-006 | B100 |
| WINSVC-V-007 | B094 |

### LIBRARY

| Id | Batch or reason |
| --- | --- |
| LIBRARY-001 | B015 |
| LIBRARY-002 | B106 |
| LIBRARY-003 | B015 |
| LIBRARY-004 | B015 |
| LIBRARY-005 | B015 |
| LIBRARY-006 | B103, B105 |
| LIBRARY-007 | B105 |
| LIBRARY-008 | B105 |
| LIBRARY-009 | B105 |
| LIBRARY-010 | B102 |
| LIBRARY-011 | B102 |
| LIBRARY-012 | B103 |
| LIBRARY-013 | B039 |
| LIBRARY-014 | B037 |
| LIBRARY-015 | B102 |
| LIBRARY-016 | B102 |
| LIBRARY-017 | B038 |
| LIBRARY-018 | B107 |
| LIBRARY-019 | B015 |
| LIBRARY-020 | B105 |
| LIBRARY-021 | B107 |
| LIBRARY-022 | B107 |
| LIBRARY-023 | B097 |
| LIBRARY-024 | B053 |
| LIBRARY-025 | B173 |
| LIBRARY-026 | B107 |
| LIBRARY-027 | B015 |
| LIBRARY-028 | B104 |
| LIBRARY-029 | No change: the LibraryDisk rewrite is seam churn with no defect behind it. |
| LIBRARY-030 | B014 |
| LIBRARY-031 | B015 |
| LIBRARY-032 | B107 |
| LIBRARY-033 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| LIBRARY-034 | Needs attended evidence of the store-title icon cache before any change. |
| LIBRARY-035 | B059 |
| LIBRARY-036 | B105 |
| LIBRARY-037 | B102 |
| LIBRARY-038 | B103 |
| LIBRARY-039 | B102 |
| LIBRARY-040 | Info: credentials stay in config.json and migrate unchanged. |
| LIBRARY-V-001 | B106 |
| LIBRARY-V-002 | B103 |
| LIBRARY-V-003 | B103 |
| LIBRARY-V-004 | B107 |
| LIBRARY-V-005 | B053 |
| LIBRARY-V-006 | B015 |
| LIBRARY-V-007 | B015 |

### OVERLAY

| Id | Batch or reason |
| --- | --- |
| OVERLAY-001 | B122 |
| OVERLAY-002 | B113 |
| OVERLAY-003 | B108 |
| OVERLAY-004 | B108 |
| OVERLAY-005 | B127 |
| OVERLAY-006 | B127 |
| OVERLAY-007 | B126 |
| OVERLAY-008 | B126 |
| OVERLAY-009 | B130 |
| OVERLAY-010 | B053 |
| OVERLAY-011 | B125 |
| OVERLAY-012 | B122 |
| OVERLAY-013 | B122 |
| OVERLAY-014 | B109 |
| OVERLAY-015 | B108 |
| OVERLAY-016 | B129 |
| OVERLAY-017 | B129 |
| OVERLAY-018 | B108 |
| OVERLAY-019 | B130 |
| OVERLAY-020 | B122 |
| OVERLAY-021 | B108 |
| OVERLAY-022 | B110 |
| OVERLAY-023 | B110 |
| OVERLAY-024 | B129 |
| OVERLAY-025 | B129 |
| OVERLAY-026 | B129 |
| OVERLAY-027 | B108 |
| OVERLAY-028 | No change: the polled owners publish no change events; adding them would add mechanism in other domains. |
| OVERLAY-029 | B108 |
| OVERLAY-030 | B130 |
| OVERLAY-031 | B129 |
| OVERLAY-032 | B126 |
| OVERLAY-033 | B126 |
| OVERLAY-034 | B110 |
| OVERLAY-035 | B127 |
| OVERLAY-036 | B130 |
| OVERLAY-037 | B132 |
| OVERLAY-038 | B132 |
| OVERLAY-039 | B132 |
| OVERLAY-040 | B130 |
| OVERLAY-041 | No change: plausible only and no reproduced loss; a post-dismissal write would itself be a behaviour change. |
| OVERLAY-042 | B132 |
| OVERLAY-043 | B130 |
| OVERLAY-044 | B131 |
| OVERLAY-045 | B131 |
| OVERLAY-046 | B132 |
| OVERLAY-047 | B132 |
| OVERLAY-048 | B132 |
| OVERLAY-049 | B127 |
| OVERLAY-050 | B132 |
| OVERLAY-V-001 | B128 |
| OVERLAY-V-002 | B123 |
| OVERLAY-V-003 | B108 |
| OVERLAY-V-004 | B127 |

### SETTINGS

| Id | Batch or reason |
| --- | --- |
| SETTINGS-001 | B013 |
| SETTINGS-002 | B013 |
| SETTINGS-003 | B013 |
| SETTINGS-004 | B013 |
| SETTINGS-005 | B117 |
| SETTINGS-006 | B117 |
| SETTINGS-007 | B118 |
| SETTINGS-008 | B117 |
| SETTINGS-009 | B119 |
| SETTINGS-010 | B123 |
| SETTINGS-011 | B123 |
| SETTINGS-012 | B117 |
| SETTINGS-013 | B117 |
| SETTINGS-014 | B120 |
| SETTINGS-015 | B123 |
| SETTINGS-016 | B123 |
| SETTINGS-017 | B120 |
| SETTINGS-018 | B121 |
| SETTINGS-019 | B135 |
| SETTINGS-020 | No change unless a stall is measured (simplify rule). |
| SETTINGS-021 | B119 |
| SETTINGS-022 | B133 |
| SETTINGS-023 | B135 |
| SETTINGS-024 | B121 |
| SETTINGS-025 | B135 |
| SETTINGS-026 | B135 |
| SETTINGS-027 | B134 |
| SETTINGS-028 | B134 |
| SETTINGS-029 | No change: reusing native vectors fixes no measured defect and forces a native rebuild. |
| SETTINGS-030 | No change unless the native file is rebuilt for another reason. |
| SETTINGS-031 | B135 |
| SETTINGS-032 | B135 |
| SETTINGS-033 | B135 |
| SETTINGS-034 | B135 |
| SETTINGS-035 | B135 |
| SETTINGS-036 | B135 |
| SETTINGS-037 | B135 |
| SETTINGS-038 | B135 |
| SETTINGS-039 | Optional; the page-local handlers are readable and left as they are. |
| SETTINGS-040 | B135 |
| SETTINGS-041 | B135 |
| SETTINGS-042 | B177 |
| SETTINGS-V-001 | B013 |
| SETTINGS-V-002 | B013 |
| SETTINGS-V-003 | B119 |
| SETTINGS-V-004 | B117 |
| SETTINGS-V-005 | B135 |
| SETTINGS-V-006 | B135 |
| SETTINGS-V-007 | B038 |
| SETTINGS-V-008 | Input owns the Settings GamepadService; recorded as an open item, no change in this refactor. |

### LABCORE

| Id | Batch or reason |
| --- | --- |
| LABCORE-001 | B158 |
| LABCORE-002 | B157 |
| LABCORE-003 | B004 |
| LABCORE-004 | No change: invoking a pre-cancelled call is the Lab's documented contract; refusing it would misreport curated init. |
| LABCORE-005 | B157 |
| LABCORE-006 | No change: replayed frames are earlier slider values; the stream's idle stop and final zero still apply. |
| LABCORE-007 | B159 |
| LABCORE-008 | B159 |
| LABCORE-009 | No change: retiring the .wsgmcap commands is feature removal (requirement 9); they stay frozen. |
| LABCORE-010 | B158 |
| LABCORE-011 | B158 |
| LABCORE-012 | B157 |
| LABCORE-013 | B157 |
| LABCORE-014 | No change: no caller reuses a session after a failed persist. |
| LABCORE-015 | B157 |
| LABCORE-016 | B157 |
| LABCORE-017 | B162 |
| LABCORE-018 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| LABCORE-019 | B162 |
| LABCORE-020 | B162 |
| LABCORE-021 | B159 |
| LABCORE-022 | B160 |
| LABCORE-023 | B160 |
| LABCORE-024 | B160 |
| LABCORE-025 | B171 |
| LABCORE-026 | B177 |
| LABCORE-027 | B161 |
| LABCORE-028 | No change: whole-token redaction would weaken a privacy redactor whose output leaves the machine. |
| LABCORE-029 | B158 |
| LABCORE-030 | No change: a second-wizard lock adds mechanism for a dev tool; documented. |
| LABCORE-031 | B161 |
| LABCORE-032 | No change: the LabPowerLog namespace move has a wide blast radius for pure tidying. |
| LABCORE-033 | B158 |
| LABCORE-034 | B159 |
| LABCORE-035 | B157 |
| LABCORE-036 | Accepted for the attended tool: capture allocation is not a product high-rate path. |
| LABCORE-037 | B162 |
| LABCORE-038 | Covered by the tests each lab batch adds; no separate batch. |
| LABCORE-039 | B173 |
| LABCORE-040 | B162 |
| LABCORE-V-001 | B158 |
| LABCORE-V-002 | B161 |
| LABCORE-V-003 | B159 |
| LABCORE-V-004 | B161 |
| LABCORE-V-005 | B158 |
| LABCORE-V-006 | B177 |

### LABUI

| Id | Batch or reason |
| --- | --- |
| LABUI-001 | B166 |
| LABUI-002 | B020 |
| LABUI-003 | B020 |
| LABUI-004 | No change: the review defects are fixed in B020; typed evidence records are optional mechanism. |
| LABUI-005 | Refuted: LabRumbleRoute serializes 'report'; the read stays. |
| LABUI-006 | B166 |
| LABUI-007 | B170 |
| LABUI-008 | B165 |
| LABUI-009 | B168 |
| LABUI-010 | B165 |
| LABUI-011 | B170 |
| LABUI-012 | B170 |
| LABUI-013 | B163 |
| LABUI-014 | B164 |
| LABUI-015 | B163 |
| LABUI-016 | Accepted parity gap: adding developer GUI controls would change the UI. |
| LABUI-017 | B163 |
| LABUI-018 | B166 |
| LABUI-019 | B166 |
| LABUI-020 | B167 |
| LABUI-021 | B020 |
| LABUI-022 | B163 |
| LABUI-023 | B170 |
| LABUI-024 | B165 |
| LABUI-025 | B166 |
| LABUI-026 | No change: mode commands stay in the wizard process, now behind the owner reservation (B158). |
| LABUI-027 | B158 |
| LABUI-028 | Refuted: JsonOptions is getter-only and an empty attempt directory is harmless. |
| LABUI-029 | B168 |
| LABUI-030 | B164 |
| LABUI-031 | Accepted: slider frames are an attended path. |
| LABUI-032 | B165 |
| LABUI-033 | B167 |
| LABUI-034 | Accepted: the summary line caps are display-only; every lane is listed with count and hash. |
| LABUI-V-001 | B019 |
| LABUI-V-002 | B020 |
| LABUI-V-003 | B161 |
| LABUI-V-004 | B161 |
| LABUI-V-005 | B158 |

### INSTALL

| Id | Batch or reason |
| --- | --- |
| INSTALL-001 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| INSTALL-002 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| INSTALL-003 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| INSTALL-004 | B024 (body never written; dispositioned by the closure) |
| INSTALL-005 | B030 |
| INSTALL-006 | B028 |
| INSTALL-007 | B025 |
| INSTALL-008 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| INSTALL-009 | B029 |
| INSTALL-010 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| INSTALL-011 | B024 (body never written; dispositioned by the closure) |
| INSTALL-012 | B024 (body never written; dispositioned by the closure) |
| INSTALL-013 | B024 (body never written; dispositioned by the closure) |
| INSTALL-014 | B024 (body never written; dispositioned by the closure) |
| INSTALL-015 | B030 |
| INSTALL-016 | B030 |
| INSTALL-017 | B030 |
| INSTALL-018 | B024 (body never written; dispositioned by the closure) |
| INSTALL-019 | B024 (body never written; dispositioned by the closure) |
| INSTALL-020 | B028 |
| INSTALL-021 | B024 (body never written; dispositioned by the closure) |
| INSTALL-022 | B024 (body never written; dispositioned by the closure) |
| INSTALL-023 | B024 (body never written; dispositioned by the closure) |
| INSTALL-024 | B024 (body never written; dispositioned by the closure) |
| INSTALL-025 | B024 (body never written; dispositioned by the closure) |
| INSTALL-026 | B031 |
| INSTALL-027 | B031 |
| INSTALL-028 | B024 (body never written; dispositioned by the closure) |
| INSTALL-029 | B024 (body never written; dispositioned by the closure) |
| INSTALL-030 | B024 (body never written; dispositioned by the closure) |
| INSTALL-031 | B024 (body never written; dispositioned by the closure) |
| INSTALL-032 | B173 |
| INSTALL-033 | B031 |
| INSTALL-034 | B024 (body never written; dispositioned by the closure) |
| INSTALL-035 | B024 (body never written; dispositioned by the closure) |
| INSTALL-036 | B024 (body never written; dispositioned by the closure) |
| INSTALL-037 | B024 (body never written; dispositioned by the closure) |
| INSTALL-038 | B024 (body never written; dispositioned by the closure) |
| INSTALL-039 | B024 (body never written; dispositioned by the closure) |
| INSTALL-040 | B024 (body never written; dispositioned by the closure) |
| INSTALL-041 | B024 (body never written; dispositioned by the closure) |
| INSTALL-042 | B024 (body never written; dispositioned by the closure) |
| INSTALL-043 | B024 (body never written; dispositioned by the closure) |
| INSTALL-044 | B024 (body never written; dispositioned by the closure) |
| INSTALL-045 | B024 (body never written; dispositioned by the closure) |
| INSTALL-046 | B024 (body never written; dispositioned by the closure) |
| INSTALL-047 | B030 |
| INSTALL-V-001 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| INSTALL-V-002 | B007 |
| INSTALL-V-003 | B007 |
| INSTALL-V-004 | B028 |
| INSTALL-V-005 | B028 |
| INSTALL-V-006 | B025 |
| INSTALL-V-007 | B030 |

### BUILD

| Id | Batch or reason |
| --- | --- |
| BUILD-001 | B037 |
| BUILD-002 | B032 |
| BUILD-003 | B174 |
| BUILD-004 | No Dependabot edit; the frozen-foundation diff check in B179 enforces requirement 12. |
| BUILD-005 | B033 |
| BUILD-006 | B033 |
| BUILD-007 | B036 |
| BUILD-008 | B036 |
| BUILD-009 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| BUILD-010 | B032 |
| BUILD-011 | B033 |
| BUILD-012 | B046 |
| BUILD-013 | B174 |
| BUILD-014 | B147 |
| BUILD-015 | B032 |
| BUILD-016 | B174 |
| BUILD-017 | B174 |
| BUILD-018 | B035 |
| BUILD-019 | B173 |
| BUILD-020 | No merge: pulling Log's statics into the launchers would break their dependency-light rule. |
| BUILD-021 | B034 |
| BUILD-022 | Keep: the preview tool is UI-test infrastructure; it is compiled in the gate by B174. |
| BUILD-023 | B173 |
| BUILD-024 | B176 |
| BUILD-025 | B176 |
| BUILD-026 | B176 |
| BUILD-027 | B147 |
| BUILD-028 | B175 |
| BUILD-029 | Keep: the API-version tripwire is a regression guard the SDK guide asks for. |
| BUILD-030 | B175 |
| BUILD-031 | Keep the tracked symlink until a Link-based include is proven to keep the avares paths. |
| BUILD-032 | B032 |
| BUILD-033 | B176 |
| BUILD-034 | B176 |
| BUILD-035 | Dropped by maintainer decision (security theater, DECISIONS.md). |
| BUILD-036 | B176 |
| BUILD-V-001 | B032 |
| BUILD-V-002 | Plan procedure: batches use narrow filters and dotnet build; eng/verify.ps1 runs only on a committed head (protocol). |
| BUILD-V-003 | B032 |
| BUILD-V-004 | Plan procedure: caller inventories exclude .claude/; removing stale worktrees is the maintainer's call. |
| BUILD-V-005 | B174 |

### A01

| Id | Batch or reason |
| --- | --- |
| A01-F001 | B001 |
| A01-F002 | B002 |
| A01-F003 | B063 |
| A01-F004 | B053 |
| A01-F005 | B063 |
| A01-F006 | B070 |

### A02

| Id | Batch or reason |
| --- | --- |
| A02-F001 | B045 |
| A02-F002 | B052 |
| A02-F003 | B043 |
| A02-F004 | Accepted bounded no-change (see SDK-040). |
| A02-F005 | B010 |
| A02-F006 | B010 |
| A02-F007 | B010 |
| A02-F008 | No change: the save-failure latch is dropped from A02_02 (sdk.verify batch problem 1). |
| A02-F009 | B010 |
| A02-F010 | B143 |
| A02-F011 | B157 |
| A02-F012 | B004 |
| A02-F013 | No change: the worker queue needs no count cap; client calls are bounded by their deadlines (simplify, no arbitrary limits). |
| A02-F014 | B157 |
| A02-F015 | B003 |
| A02-F016 | B003 |
| A02-F017 | B154 |
| A02-F018 | B154 |
| A02-F019 | B025 |
| A02-F020 | B036 |
| A02-F021 | B147 |
| A02-F022 | B173 |

### A02S01

| Id | Batch or reason |
| --- | --- |
| A02S01-F001 | B156 |
| A02S01-F002 | B156 |
| A02S01-F003 | B156 |
| A02S01-F004 | B156 |
| A02S01-F005 | B156 |
| A02S01-F006 | B156 |
| A02S01-F007 | B156 |
| A02S01-F008 | B156 |
| A02S01-F009 | B156 |
| A02S01-F010 | B156 |
| A02S01-F011 | B156 |
| A02S01-F012 | B156 |
| A02S01-F013 | B156 |
| A02S01-F014 | B156 |

## Appendix B. Earlier ledger ids

`claude-findings-disposition.json` holds 387 saved findings and 12 provisional records from the first Claude audit. A ledger id cited by a domain finding (the reports mark it as "covered") is resolved by that finding's batch in Appendix A. B178 writes the per-id table for every ledger id, including the 165 that no review cites, using the Codex task mapping (W01 and W02 to the WDC batches, T01 and T02 to the toolkit C# batches, T03 to the toolkit JavaScript and surface batches, F01 to the configuration batches, H01 to the session and install batches, H02 to B140).

Explicit assignments now:

- Medium rows the critic found unreferenced: U02A-SUTC-001 B053; U03A-SUTS-004 and U03A-SUTS-006 B051; U04A-LFA-008 B039; U04B-LFA-003 B016.
- U04B-LFA-001 B112; U04B-LFA-012 B029; U04A-LFA-010 and U04A-LFA-011 (functional part) B027; U04B-LFA-004 B095; U04B-LFA-013 to U04B-LFA-049 (bodies missing) B024.
- The critic's other unreferenced Low and Nit ids (U02A-SUTC-015, 016, 019, 024, 026, 027, 028, 041, 043, 046, 047, 048, 050; U02B-SUTC-007, 018, 020, 025, 029, 030, 039, 043; U03A-SUTS-008, 011, 012, 013, 017, 032, 033; U03B-SUTS-025, 026, 028, 032, 034, 040, 043; U04A-LFA-012, 021, 024, 030, 032, 033, 035) are assigned by B178 to the batch that edits the cited code or recorded as no-change with the reason; U02B-SUTC-007 is already fixed by B053.
- U01 rows, mapped from the batch column of `wdc.md` section 2.1: U01-001 B063; U01-002 B064; U01-003 B067; U01-004 B001; U01-005 B071; U01-006 B065; U01-007 B063; U01-008 B064, B067, B046, B071; U01-009 B002; U01-010 B065; U01-011 B065, B072; U01-012 B064; U01-013 B064; U01-014 B064; U01-015 B064; U01-016 B064; U01-017 B064; U01-018 B063; U01-019 B065; U01-020 B065; U01-021 B063; U01-022 B063; U01-023 B066; U01-024 B066, B063; U01-025 B066; U01-026 B066; U01-027 B067; U01-028 B072; U01-029 B070; U01-030 B067; U01-031 B069; U01-032 B069; U01-033 no change: no change, evidence gate; U01-034 B067; U01-035 B067; U01-036 B069, B071, B066; U01-037 B071; U01-038 B071; U01-039 B071; U01-040 no change: per-API decisions in 4.3; Intel/Lab CCD duplicates accepted (C36); U01-041 B046; U01-042 B046; U01-043 B046; U01-044 B046; U01-045 B046; U01-046 B063; U01-047 B064, B066; U01-048 B064, B066, B071; U01-049 B064; U01-050 B065; U01-051 B065; U01-052 B065; U01-053 B064; U01-054 B064; U01-055 B063; U01-056 B063; U01-057 B064; U01-058 B071; U01-059 B066; U01-060 B063, B066; U01-061 no change: docs only; no new mute overload (no consumer); U01-062 B066; U01-063 B069; U01-064 B067; U01-065 B067; U01-066 B072; U01-067 B046; U01-068 B072; U01-069 B072; U01-070 B072; U01-071 B072; U01-072 B072; U01-073 B046; U01-074 B064, B066, B071; U01-075 B071; U01-076 B071; U01-077 B071; U01-078 B071; U01-079 B066; U01-080 B046, B071; U01-081 B066; U01-082 no change: with touched code; U01-083 B065; U01-084 B063; U01-085 B071.
- Dropped by maintainer decision (security theater, DECISIONS.md): U02A-SUTC-004, U02A-SUTC-052, U02A-SUTC-061, U04B-LFA-002, U05-LFB-016 and the shared-directory half of U04A-LFA-011.
- Provisional records PV01 to PV12 are metadata and stay preserved verbatim; their observations are reconciled in B178.
