# WSGM 2.1.0 fixes and simplification

WSGM is a hobby project. The goal is working behaviour and code one person can understand and maintain.
Fix the demonstrated defects with the smallest clear change. More classes, interfaces, state machines,
test scaffolding or process are not progress.

This revision replaces the earlier architecture and execution prescriptions. It preserves all 188 item
IDs, existing progress and the finding mappings below. The IDs are tracking references, not 188 separate
implementation ceremonies or a mandatory serial schedule. The old design is available in Git history.

## 1. Scope and precedence

Current maintainer instructions win, followed by [DECISIONS.md](DECISIONS.md) and this plan.
[Findings](findings/README.md) and their verification files establish the defects and evidence;
their proposed solutions, named classes and file splits are suggestions, not required architecture.
[requirements.md](requirements.md) records earlier requirements; its interview, delegation and
per-increment test permissions are superseded by current direct-work and end-only-validation instructions.
[batches.json](batches.json) is a compact index of the same items, not a dispatch task graph.

Keep the existing UI and workflows except for explicit maintainer decisions. Preserve settings,
profiles, credentials and Windows recovery state. Update in-repo callers together when contracts change.
Keep Avalonia and VIIPER fixed. Keep WSGM policy out of reusable libraries, and device-specific behaviour
in its package. Do not reset config to avoid migration work.

Remove arbitrary caps that discard user content; retain the agreed untrusted-input byte bounds in
section 4. Do not gate controls or writes on readback, retry uncertain writes automatically or add
same-user security hardening. High-rate input/telemetry must allocate and log nothing per sample.
Device Lab's attended evidence rules remain separate from runtime write behaviour.

## 2. Smallest useful solution

- Fix the existing method first. Delete stale state, duplicated work and forwarding wrappers before adding replacements.
- Keep a class together when it has one understandable lifetime. File length alone is not a defect.
- Extract a helper only for real duplication or an ownership boundary that is currently causing bugs.
  Do not require a controller per page, worker per operation or facade over a set of new owners.
- Use the existing lock, cancellation token and task when they solve the race. Add no general scheduler,
  recovery engine, registration framework or universal lifecycle API.
- Pass the concrete dependency or a small delegate when sufficient. Add an interface only where a real
  alternate implementation or isolated native boundary needs it; do not wrap every operation for tests.
- Keep recovery rules explicit near the affected write. Preserve unreadable records and originals;
  do not add speculative latches, rollback or retry machinery.

These rules also apply to already-applied refactor code. Source-applied status is not an endorsement of
every abstraction introduced. Remove unnecessary machinery during the affected work, preserving the fix.
Do not restart the whole refactor or roll back useful changes just to match a new class diagram.

## 3. Work order and execution

1. Finish config/profile fixes together: safe reads/writes, unchanged-save suppression, reload and recovery.
2. Fix device/controller/power lifecycle and shared ownership, keeping HC behaviour and safety cleanup.
3. Fix Steam transport/patches and their host consumers together, then library/artwork correctness.
4. Fix Windows services, overlay and Settings behaviour without changing the existing UI unnecessarily.
5. Finish SDK/package/GPU/IR and Device Lab defects, updating related callers and contracts together.
6. Reconcile the remaining findings, then validate and hand off manual acceptance.

Work within each area in coherent increments, not a new plan/review/report for each small edit.
Only actual source dependencies block work: for example update a changed contract with its consumers,
and establish the display rotation meaning before migrating saved layouts. The old blanket chains between
unrelated items are removed. Low-risk cleanup goes with the code it touches. Do not start another broad audit.

Work directly on master. Preserve unrelated changes; commit only owned paths at useful milestones.
No new agents or dispatch workflow unless requested. Keep one concise evidence record in
[implementation-todo.md](../implementation-todo.md), and update item status here regularly.
No review cycle, inventory or fixture package is required after each fix.

Tests, Rider cleanup and gates are deferred until implementation is finished, as instructed.
The latest task instruction defers builds and publishes until implementation is finished too.
At delivery, deploy only when the target application is stopped; do not launch or close it without direction.
Keep source/test API consumers coherent while editing, but do not run the test suite incrementally.
Run required Prettier formatting before committing its files. Pushes are also deferred because each push
starts CI verification. For child changes, publish the child before its parent gitlink at final delivery.
Do not change guidance/hooks to bypass these instructions. No live deployment, Steam manipulation,
service changes, hardware actions, firmware upload, release or version bump is authorized here.

At the end, run the required warning-free build, affected behavioural tests, independent WDC/toolkit
validation and the full gate once. Review UI baselines only where output changed. Report failures plainly;
rerun the narrow affected checks after corrections, repeating the full gate only for a real broad impact.
Manual and hardware acceptance remain the maintainer's and must not be inferred from automated checks.

## 4. Decisions and safety details that stay

[DECISIONS.md](DECISIONS.md) remains the binding decision record; do not duplicate its tables here.

The D2 byte bounds are retained exactly: manifest 256 KiB/depth 16; package 128 MiB per file/512 MiB total;
glyph document/asset/profile/notice bytes and raster dimensions; ThemeStoreClient 64/8 MiB; splash ZIP
bounds; sound-pack expanded-byte ZIP guard; animation repository download 64 MB; artwork 16 MiB image,
64 MiB cache and 4 MiB JSON; CDP 8 MiB responses/1 MiB notifications and request backpressure; IR 32 KiB
frame; LaunchPayload pipe bounds; Lab review-archive guard, 8 MiB firmware tables/64 KiB device properties;
EDID 32 KiB. These refuse with a visible failure rather than truncate or pretend data is absent.

Shutdown keeps one task and one deadline. Stop new work; join the existing power/startup work within the
deadline; restore AutoTDP before device stop; neutralize/release the controller and show HidHide;
stop device/common/GPU plugins; retire remaining transition/gate work and tray; restore Explorer only
when permitted and the tray is gone; return Steam's movie choice where required and retract Steam UI;
dispose UI feature owners and native providers, with MessageWindow last. Attempt later steps after a
failure. SessionEnd must never restore Explorer, and escalation only tightens the deadline.
This is an ordered cleanup routine, not a framework every owner must implement.

## 5. Manual acceptance changes

The matrix in `manual-acceptance-matrix.md` stays the maintainer's. These changes follow from the refined batches:

- **New rows.** M01-43 RTSS overlay at game start without opening the QAM, and after RTSS is closed mid-session (R on all). M01-44 tray "Exit WSGM" on a device session shows the physical pad again with the cloak off (A N, A D, R C, R X). M01-45 an update logs `Installer shutdown handoff completed ... outcome=Clean` and setup reports Completed without waiting out its grace period (R). M01-46 `--restore-shell` from Game Mode on a device session (R C, R X, A N, A D). M01-47 sign-out, restart and shutdown from the overlay power menu with no blocker screen and no Explorer start (R). M01-48 uninstall with `App\WSGM.exe` missing keeps the HidHide ledger and reports the controller may still be hidden (L). M01-49 logon-service elevated boot and the dirty-exit Explorer fallback (A). M01-50 `WSGM.Launch --deelevate [--input-lease]` under elevated Steam, including the UAC-off fail-open (A). M01-51 the overlay's application-profile editor names a profile and adds, edits and removes processes with the controller alone. M01-52 with an external display, the display-mode selector on the sheet lists and changes the modes of the display the sheet is on. M01-53 exit WSGM with no WSGM boot movie chosen and Steam's own startup-movie choice is back; uninstall restores Steam's boot-movie override and removes WSGM's `themes_custom` link. M01-54 in Settings, Escape during keyboard capture and the 3 s chord timeout keep the previous binding, and Clear clears it.
- **Rewritten.** M01-31: "Picker select, cancel, reopen; in a large folder enter two directories and go Back; the whole list renders as today; a stale listing never replaces the current one; explicit selection, cancel or unmount settles once." M01-42: the hardware-free run also requires the Device Lab `wizard` record folder to be absent.
- **Extended.** M01-03 adds theme, sound and animation changes in Steam and Device Integration enabled from the overlay banner while Settings is open. M01-17 adds a lock or sleep between AutoTDP writes, then exit restores the original. M01-19 adds a rotated display that stays rotated after entry and return. M01-21 and M01-22 check that the Wi-Fi, scan, consent and pairing texts read as today. M01-12 adds a crash ledger followed by a start with integration off. M01-25 adds a desktop with two internal Steam libraries, each badge showing its library's name. M01-37/38 run on firmware 0.5.0 (protocol 2) and Read built-in remotes lists every remote of a catalog above 32 KiB.

## 6. Finding items and current status

Every original item remains below. Earlier **Implemented** entries retain their recorded checks;
**Source applied** means edited source with validation deferred, and **In progress** stays open.
No status was promoted by this plan rewrite. Detailed evidence stays in the tracker.

| Item | Area | Direct outcome | Status |
| --- | --- | --- | --- |
| B001 | wdc | Land W02_01 (EDID validity bit) in WindowsDeviceControl | Implemented |
| B002 | wdc | W02_02 simplified: one internal power-action port | Implemented |
| B003 | ir | A02_04: IR UTF-8 reply bound and reply-document ownership | Implemented |
| B004 | lab | A02_03 with R4: safety zero stays armed until it succeeds | Implemented |
| B005 | perf | USER-001: RTSS starts with WSGM and is kept alive; overlay level never waits on an executable | Implemented |
| B006 | session | Every exit runs the session cleanup; OS end-session is SessionEnd | Implemented |
| B007 | install | Setup: uninstall never drops the HidHide ledger; Close starts WSGM only after success | Implemented |
| B008 | device | DEVICE-001: an unverified device stop no longer blocks every restart | Implemented |
| B009 | input | Never strand the physical controller | Implemented |
| B010 | sdk | A02_02 trimmed plus SDK-B1 journal and serializer correctness | Implemented |
| B011 | device | DEVICE-V-001: AutoTDP survives lock, sleep and restart; exit restore always runs | Implemented |
| B012 | device | Failed unlock resume restarts the cycle; passive detection keeps no runtime | Implemented |
| B013 | settings | Settings save starts from the fresh config; one shared-field table | Implemented |
| B014 | library | Golden composed-shortcut tests before any library move | Implemented |
| B015 | library | Library correctness fixes that need no new owners | Implemented |
| B016 | winsvc | Steam autostart takeover refuses when it cannot record the original | Implemented |
| B017 | winsvc | Windows power writes and hybrid cores stop gating on readback | Implemented |
| B018 | steamhost | An invalid theme update journal no longer stops the session | Implemented |
| B019 | lab | Device Lab: a synchronous Continue no longer loses the running stage | Implemented |
| B020 | lab | Device Lab review confirms real TDP, lighting and fan evidence | Implemented |
| B021 | packages | Claw command truthfulness, timeout classification and watchdog | Implemented |
| B022 | packages | Ally write-through restore in HC order and published values | Implemented |
| B023 | packages | Ally fan rollback removal | Implemented |
| B024 | install | Read-only closure of the unwritten install and U04B finding bodies | In progress |
| B025 | install | Logon service stops cleanly and gets one token seam | Implemented |
| B026 | install | Removed by maintainer decision | No change: maintainer decision |
| B027 | install | Updater download leaves no partial file | Implemented |
| B028 | install | Setup applies answers last and reports partial change truthfully | Implemented |
| B029 | install | De-elevation task deleted once after its dispatch budget | Implemented |
| B180 | install | Core startup closure fixes from B024 | Implemented |
| B181 | install | Remove omitted setup answer and bundle caps | Implemented |
| B182 | install | Logon native error ownership and omitted boot cap | Implemented |
| B183 | install | Retain failed package-exemption recovery intent | Implemented |
| B184 | install | Packaged inspection buffer and containment corrections | Implemented |
| B185 | install | Complete module inspection and foreground retirement | Implemented |
| B186 | install | Truthful load results and owner-thread exemption retirement | Implemented |
| B187 | install | Payload archive failure ownership | Implemented |
| B188 | install | Setup registry root ownership | Implemented |
| B030 | install | Match setup components exactly, refuse the wrong user identity and keep partial failures truthful. Reuse SetupEngine. | In progress |
| B031 | install | Keep shared process names and native declarations consistent across the launcher, setup and app. | Source applied |
| B032 | build | Fix checks that mutate files, missing build inputs and CI mistakes. Keep one final gate. | Source applied |
| B033 | build | Ship the right notices and controller payloads, using the existing dependency lock. | Source applied |
| B034 | build | Check real forbidden project references at final validation; avoid a second project graph framework. | Pending |
| B035 | build | Compile the actual plugin templates so examples cannot silently rot. | Source applied |
| B036 | build | Deduplicate pinned downloads and verify the real exported symbols without a new packaging layer. | Source applied |
| B037 | config | Give config users the same explicit root and store. Remove hidden path fallbacks; keep composition straightforward. | In progress |
| B038 | config | Preserve valid settings and plugin values, repair invalid enum values and remove arbitrary truncation. Use ordinary section rules; a generic metadata framework is not a requirement. | Source applied |
| B039 | config | Never overwrite unreadable config or recovery files. Keep atomic durable writes and one write lock, skip unchanged saves and distinguish missing files from failures. Preserve corrupt bytes before replacement. | Source applied |
| B040 | config | Serialize profile reloads and writes, stop queued work on close and honour the edit's changed flag. Reuse the existing service and fan-out. | Source applied |
| B041 | config | Keep log rotation from blocking appends and preserve command-line verbosity on reload. A separate logging subsystem is unnecessary. | Source applied |
| B042 | config | Keep desktop recovery until all restores succeed and clear only the record restored. Avoid repeated audio reads; test with temporary config and simple delegates. | Source applied |
| B043 | sdk | Dispatch clock cancellation off the clock thread and observe failures. Keep the existing process clock. | Source applied |
| B044 | sdk | Contain native callback and poll exceptions, dispose replaced reconnect sources and avoid per-sample tracing. | Source applied |
| B045 | sdk | Reject oversized or excessively nested manifests before parsing, including package manifests. Use the existing reader. | Source applied |
| B046 | wdc | Make WDC build independently and keep its tests off live hardware. Pin native layouts with meaningful byte fixtures. | Pending |
| B047 | toolkit | Keep one Steam fragment list and run existing script checks against the composed asset at final validation. | Pending |
| B048 | toolkit | Fix transport connection, cancellation and teardown races in the existing connection code. | Pending |
| B049 | toolkit | Resolve known Steam modules without invoking unknown exports; validate bridge payloads and keep command handlers off the pump. Fix the remaining parsing and logging defects locally. | Pending |
| B050 | toolkit | Handle interleaved bridge deliveries independently, clear disposed state and allow module resolution to recover after a transient failure. | Pending |
| B051 | toolkit | Restore displaced gate state on removal, refuse malformed updates and remove silent content caps. Extend the existing checks. | Pending |
| B052 | sdk | Remove redundant plugin text types, make manifests immutable and expose plugin tracing where needed. Update all consumers and the contract version together. | Pending |
| B053 | toolkit | Report whether a Steam write was sent, refused, applied or uncertain. Pass the existing client explicitly and serialize writes; preserve recovery records after uncertain results. | Pending |
| B054 | toolkit | Use one patch synchronization loop; apply the bridge first and remove it last. Remove failed patches without blind retries, isolate faulty modules and support ready-plugin module changes. | Pending |
| B055 | toolkit | Simplify duplicated QAM rendering code while preserving output. Split fragments only where it makes the source easier to read. | Pending |
| B056 | toolkit | Remove redundant toolkit wrappers and unused public APIs, fix payload contracts and update consumers/version together. Preserve useful read and error detail. | Pending |
| B057 | toolkit | Let WSGM supply QAM labels, layout and library names. Preserve existing layout and fold IDs; fix custom choices and raw OSD watt values. | Pending |
| B058 | toolkit | Keep uncommitted row drafts across unrelated updates; clear only the affected draft and show its write refusal. | Pending |
| B059 | toolkit | Enumerate picker folders off the UI thread, honour cancellation and ignore stale results. Keep the whole-list UI, supported path forms and one request timeout. | Pending |
| B060 | toolkit | Fix stale theme caches, expensive repeated carousel work and colour fallback bugs. Keep portal handling unless a real defect requires changing it. | Pending |
| B061 | toolkit | Replace fragile minified-token fingerprints using known source shapes. Verify in the current Steam client when attended testing is authorized. | Pending |
| B062 | toolkit | Keep meaningful toolkit regression tests and check real probe execution at final validation. | Pending |
| B063 | wdc | Return disposable watch registrations, contain watch failures and document callback threading. Remove redundant WSGM watch generations. | Pending |
| B064 | wdc | Identify Wi-Fi by SSID bytes and security, handle all matching profiles and return native failures accurately. Preserve WSGM wording and consent handling. | Pending |
| B065 | wdc | Fix radio locking and per-adapter results; bound and cancel pairing correctly, complete deferrals and contain callbacks. Keep Bluetooth policy in WSGM. | Pending |
| B066 | wdc | Fix audio HRESULT handling, disconnected caches and driver-buffer lifetimes; correct backlight policy reads and preview failures. Keep fallback labels in WSGM. | Pending |
| B067 | wdc | Serialize display writes, preserve rotation and handle unreadable targets accurately. A successful native write is success; retain persisted display compatibility and remove arbitrary enumeration caps. | Pending |
| B068 | config | Migrate only changed stored values once, preserving settings and recovery state. Keep one old-config copy and best-effort reads of newer files; no migration framework. | Pending |
| B069 | wdc | Return display write results with native detail and map wording in WSGM. Remove confirming reads and retain recovery only for writes that failed to dispatch. | Pending |
| B070 | wdc | Deduplicate the two display-settle waits with existing timing and cancellation. Avoid a general topology service. | Pending |
| B071 | wdc | Attempt every applicable wake and standby restore, collect failures and fix native registration/layout defects. Remove arbitrary buffer caps; preserve secure default restore behaviour. | Pending |
| B072 | wdc | Update WDC docs and metadata to actual behaviour; independently validate both supported frameworks at the end. | Pending |
| B073 | input | Remove per-sample controller allocations and duplicate routing state. Preserve capture/forwarding guards, neutral publication and safe backend disposal. | Source applied |
| B074 | input | Make controller and HidHide ownership explicit at composition, remove duplicated tool setup and expose coherent status. | Source applied |
| B075 | input | Pass the existing managed pad to overlay-launched Settings so controller navigation works. | Source applied |
| B076 | input | Track one Steam Input shim apply path and dispose its state with the session. Remove redundant reload tasks. | Pending |
| B077 | input | Keep the Steam Input lease reachable from Program through normal exit and panic. Balance overlapping UI claims using the concrete lease owner. | Pending |
| B078 | input | Do not replace a newer guide-chord binding with an old size-limited copy. Dispose existing subscriptions properly. | Source applied |
| B079 | input | Share one raw-touch registration between subscribers, separate gesture recognition from native input and release it after the last subscriber. | Pending |
| B080 | input | Escape, timeout and recorder failure retain the old binding; only Clear clears it. Fix local navigation/recorder duplication without new options plumbing. | In progress |
| B081 | device | Call the device runtime directly and delete the forwarding adapter. Bound lifecycle waits without unloading plugin code that is still running. | Source applied |
| B082 | device | Ignore stale command results, check cancellation immediately before dispatch and use active-time deadlines. Keep one router and log outside locks. | Source applied |
| B083 | device | Stop accepting device work when stopping, join one shutdown task within the deadline and retain running work safely. Track existing tasks; remove the failure-tracker wrapper. | Source applied |
| B084 | steamhost | Snapshot storage on the UI thread, derive switches consistently, avoid config IO under state locks and fix command-result/refusal defects. | Pending |
| B085 | steamhost | Apply Steam surface switches consistently through one path, preserving immediate cancellation and existing edge effects. | Pending |
| B086 | steamhost | Construct and dispose Steam backends in one place; avoid subscriptions during partial construction. Add/remove plugin modules on readiness without rebuilding the session host. | Pending |
| B087 | steamhost | Deduplicate QAM projections and subscriptions. Preserve observed and desired OSD watts; file splits are optional. | Pending |
| B088 | device | Fix device lifecycle serialization, partial startup and integration-off behaviour in the existing coordinator. Extract lifecycle code only if it removes tangled ownership. | Pending |
| B089 | device | Keep PL2 in the device entry and migrate old BoostWatts once. Share existing user-write rules and restore ordering without adding a policy framework. | Source applied |
| B090 | winsvc | Serialize machine-wide power changes using the existing scheme lock and make lifetime dependencies clear. No extra power scheduler. | Pending |
| B091 | device | Serialize sustained, boost, preset and AutoTDP writes through one existing owner. Restore AutoTDP originals before stopping the device; contain power callbacks. | Pending |
| B092 | device | Keep controller start/loss/release and claims in one place. Cancel pending start on suspend and show the physical pad on loss; extract only if ownership becomes clearer. | Pending |
| B093 | device | Fix config persistence, misleading capability mappings, token truncation and instance-state leaks. Remove unused defaults and update callers coherently. | Pending |
| B094 | winsvc | Capture format targets before awaits, fix RTSS/card/audio races and grow insufficient native buffers. Dispose UI-bound services before their message window. | Source applied |
| B095 | winsvc | Deduplicate console execution with clear refused/uncertain/success results. Refuse manager restore after unreadable config and bound the existing startup task at exit. | Source applied |
| B096 | winsvc | Reuse one storage snapshot across eject, format, Steam and library paths. Keep card-swap checks; do not build a revision/cache subsystem. | Pending |
| B097 | winsvc | Preserve every identity recheck around formatting, use volume identity for library writes and report uncertain diskpart outcomes truthfully. Isolate only destructive calls for tests. | In progress |
| B098 | winsvc | Remove test-only production display branches by supplying the real required inputs or test fakes. Preserve revision guards and original-mode recovery. | Source applied |
| B099 | winsvc | Restore the audio endpoint actually muted, move volume writes off the UI thread and dispose feedback. Share endpoint access only where it removes duplication. | In progress |
| B100 | winsvc | Remove arbitrary RTSS/LHM enumeration caps and use limited process-path queries. Keep frametime reads independent of OSD write access. | Source applied |
| B101 | winsvc | Join service loops on disposal, fix card-monitor triggers and keep preview compositions hardware-free. Add missing behaviour coverage at the end. | In progress |
| B102 | library | Use fakeable HTTP handlers in existing artwork providers, compute HasMore before filtering and deduplicate slot rules. Remove expired failures and redundant overloads. | Source applied |
| B103 | library | Keep artwork browsing across unrelated config reloads, handle cancellation/disposal safely and share apply/clear/find filename rules, including icons. | Source applied |
| B104 | library | Delete dead library code and deduplicate pure entry/projection rules. Do not create new classes just to shorten a file. | Source applied |
| B105 | library | Keep library disk work off the UI thread, preserve edit/write ordering, copy saved records and join background work within the caller's deadline. Extract workers only to untangle actual shared state. | Source applied |
| B106 | library | Prevent two titles from adopting one shortcut. Offer the second title its own Add and Steam shortcut. | Source applied |
| B107 | library | Report launcher-source failures instead of silently treating them as absent; share executable/command-line rules and fix monitor lifetime. Require evidence before changing catalog URL filtering. | Source applied |
| B108 | overlay | Fix overlay hot-path allocation, async exceptions, picker lifetime, missing choices and preview write guards. Preserve native pickers and plugin error detail. | Source applied |
| B109 | overlay | Keep keyboard, status and text-entry state with the overlay surface that owns it instead of process globals. | Pending |
| B110 | overlay | Fix navigation back policy and the render cycle, then remove the depth and cycle guards. Keep existing destination strings. | Pending |
| B111 | session | Parse startup options once and keep one shutdown request/task with a sticky failure code and SessionEnd reason. Simplify crash-loop ownership in existing startup code. | Pending |
| B112 | session | Share Explorer launch/path probing, dispose partial starts and honour cancellation/UAC refusal. Never start a competing process after an uncertain launch or kill Explorer. | Pending |
| B113 | overlay | Dispose overlay activation subscriptions correctly and do not construct them for an in-session preview. Keep overlay-test reopen controls. | Pending |
| B114 | session | Own one message window, contain native callbacks and dispose it last. Verify tray retirement and fix activation/window-finder races; preserve the relay. | Pending |
| B115 | session | Simplify existing Game Mode entry/recovery hooks, keep one pending-return record and undo monitor pauses on refusal. Remove duplicate Steam discovery/control work only where useful. | Pending |
| B116 | session | Fix power-event coalescing and watcher shutdown races in the current session code. Keep the last good config and isolate reload steps so one failure does not skip the rest. | Pending |
| B117 | settings | Supply Settings dependencies explicitly, remove hidden production fallbacks and apply external settings only when changed. Reuse existing save/apply paths. | Pending |
| B118 | settings | Discover displays off the UI thread in every composition; tests await the same discovery task. | Pending |
| B119 | settings | Wait for a Settings save on ordinary close without blocking OS shutdown. Report committed-but-apply-failed accurately and still apply after later splash/boot errors. | Pending |
| B120 | settings | Move shared hardware facts and accent values out of UI files to their actual consumers. Keep moves small. | Pending |
| B121 | settings | Route update/package work through existing Settings dependencies and cancel downloads when the window closes. | Pending |
| B122 | overlay | Keep test/preview power actions isolated from the machine, release overlay claims synchronously and chain pin writes without mutating shared config. | Pending |
| B123 | settings | Open or activate one Settings window per process with the current session mode and supplied actions. | Pending |
| B124 | session | Isolate optional startup failures, preserve desktop recovery and stop worker mutation of shared config. Extract transition logic only where it clarifies actual ownership. | Pending |
| B125 | overlay | Move session relaunch and reload policy out of overlay views into the existing session owner. | Pending |
| B126 | overlay | Share duplicated capability-row and commit-on-close code; recheck write eligibility at invocation. Preserve layout. | Pending |
| B127 | overlay | Fix reattached plugin panels, avoid whole-tree pin scans and simplify page code locally. A controller class per page is not required. | Pending |
| B128 | overlay | Fix launch-action recovery and lookup errors in the existing workflow; keep snapshots after uncertain writes and give every exit a visible result. | Pending |
| B129 | overlay | Make profile Name/Process fields controller-reachable press-to-edit rows, remove text caps and share list navigation only where duplicated. | Pending |
| B130 | overlay | Target the overlay's own display, load idle/policy values off the UI thread and ignore stale results. Reuse session state and simplify glyph caching. | Pending |
| B131 | overlay | Use production overlay input wiring in tests, remove duplicate/getter tests and cover touch/media lifetime at final validation. | Pending |
| B132 | overlay | Fix preview work, duplicate downloads and cancellation; simplify local disposal branches and stale comments. Preserve visual output. | Pending |
| B133 | settings | Show config read failures in Settings and keep IO off the UI thread. Use the existing strict store write path without a read-only mode. | Pending |
| B134 | settings | Raise backdrop changes only when changed and handle late native callbacks after stop. Use a few internal delegates for lifecycle tests. | Pending |
| B135 | settings | Remove the wake-source display cap, fix recorder/task continuations and retain bindings on failure. Deduplicate Settings helpers and preserve palette/layout values. | Pending |
| B136 | steamhost | Keep Steam readiness, enable/disable and Big Picture transitions ordered in the existing host. Cancel commands at shutdown start; retract patches after device cleanup. | Pending |
| B137 | steamhost | Write library tabs/badges only when changed, preserve press order and track sync work. Remove static state only where it breaks ownership. | Pending |
| B138 | steamhost | Fix content work cleanup and strict-save failures, remove silent media caps and return Steam's startup-movie choice at exit/uninstall. Restore only WSGM-owned files/links. | Pending |
| B139 | steamhost | Compute the asset hash from the asset, remove builder source rewriting and duplicate gate wrapping, and keep WSGM patch IDs separate. Preserve fold IDs. | Pending |
| B140 | session | Use the safety-first shutdown order below, one deadline and one task. Attempt later cleanup after failures; remove the repeated cleanup block without a shutdown framework. | Pending |
| B141 | session | Fix splash timing and dead session wiring; update docs after the actual ownership changes. | Pending |
| B142 | sdk | Remove SDK content caps and host-only types, fix manifest/identity validation and dispose all package owners. Update API version and every consumer together. | Pending |
| B143 | packages | Keep package recovery rules local and explicit: no automatic replay of uncertain Claw restores; Ally writes through without readbacks. Preserve originals, handle changed bindings and migrate old records safely. | Pending |
| B144 | packages | Deduplicate package value checks, write-budget checks and capability IDs where implementations truly match. Vendor-specific motion/lifecycle code can stay local. | Pending |
| B145 | packages | Snapshot Claw identity at start/resume, remove redundant command probes and fix transport descriptor/handle ownership. Preserve HC behaviour and timeouts. | Pending |
| B146 | sdk | Delete the plugin forwarding wrapper, report cleanup failure clearly and fix registration state/threading. Share loading code only where it reduces duplication. | Pending |
| B147 | sdk | Discover manifests off the UI thread, apply pending removals once at startup and fix package validation/SDK versions. Remove count caps; keep existing manager unless a split removes real coupling. | Pending |
| B148 | sdk | Keep GPU revision state per instance, supply AC state explicitly and await refresh/disposal work. Honour active-time command cancellation. | Pending |
| B149 | sdk | Remove only duplicate SDK tests; keep API boundary/native-import guards and add missing behavioural cases at the end. | Pending |
| B150 | gpu | Never reset unreadable GPU journals or disable unrelated/global controls because one per-app record failed. Report save failures without claiming the driver write was undone. | Pending |
| B151 | gpu | Pass GPU write admission explicitly, cache only real support outcomes, reuse native discovery within a pass and contain publication failures. Fix existing shared runtime rather than adding another layer. | Pending |
| B152 | gpu | Remove NVIDIA readback/pending machinery, clear records after dispatched restore and preserve them on failed writes. Size NVIDIA/AMD buffers from native counts and fix revision guards. | Pending |
| B153 | gpu | Move Intel writes off the UI thread and fix admission/support-cache/loop lifetime defects. Reuse shared GPU code where it reduces duplication; preserve written-value UI and reopen backoff. | Pending |
| B154 | ir | Classify IR refusals before emission, retain uncertain outcomes after emission and never overwrite unreadable libraries. Dispose a failed serial open and keep sequence status truthful. | Pending |
| B155 | ir | Simplify IR session/storage/publication code without changing IDs or labels. A three-class split is not an acceptance requirement. | Pending |
| B156 | ir | Fix firmware learn retirement, validation and storage errors; add protocol-2 catalog pages and matching host reassembly so large catalogs work. Build only; flashing remains attended. | Pending |
| B157 | lab | Fix Lab watchdog locking, EOF cleanup, wrong-token release and pending-client races. Stop per-sample logs and expose only the seams the worker tests need. | Pending |
| B158 | lab | Refuse unreadable machine records, attempt every startup recovery item under the owner reservation and start the worker only when needed. Preserve existing record locations. | Pending |
| B159 | lab | Deduplicate MSI_ACPI calls, handle null arguments/timeouts and validate every power limit before the first write. Preserve checkpoints when observations fail. | Pending |
| B160 | lab | Fix failed KX-open cleanup and duplicated HID/pin helpers. Keep transport structure simple. | Pending |
| B161 | lab | Keep complete capture evidence, finish cancelled steps and fix shortcut state/serializer races. Join capture before freeing its signal; refuse probes when quarantine recording fails. | Pending |
| B162 | lab | Use one Lab root, elevation check and worker launch path; preserve previous trace logs and remove duplicate staging tests. | Pending |
| B163 | lab | Reuse the existing Lab archive reader, retain full persisted detail and deduplicate button/report helpers. | Pending |
| B164 | lab | Export all evidence with cancellation and the project lock. Keep the existing in-memory export; bounds belong only on untrusted archive reads. | Pending |
| B165 | lab | Fix developer-runner cancellation, process-handle leaks and duplicate help windows. Reuse options/root and keep complete messages/recent paths. | Pending |
| B166 | lab | Track one wizard operation and close once in order: cancel/await, undo HidHide, dispose capture/worker, release reservation. Keep result pages outside the running operation. | Pending |
| B167 | lab | Complete dumps before prompting, report abandoned/evidence-write failures and make Finish viewing read-only. Separate stage code only when easier to maintain. | Pending |
| B168 | lab | Remove extra-button caps, replace magic answer indices and share pointer filtering. Keep mode commands under the existing reservation. | Pending |
| B169 | lab | Record rumble routes before writing and zero once on success/failure/cancel. A failed zero fails the stage; never replay a failed pulse. | Pending |
| B170 | lab | Keep power stage order/values, update records off the UI thread and fail closed on checkpoint writes. Preserve Lab readback-before-clear rules. | Pending |
| B171 | lab | Gate lighting on actual mechanisms, preserve exact restore after cancellation and finish skipped sleep stages as Skipped. | Pending |
| B172 | input | Share the existing HidHide adapter without relicensing GPL sources; fix IOCTL threading and remove duplicate tests. | Pending |
| B173 | build | Move only genuinely shared sources, fix single-consumer homes and relicense Device Lab as GPL per D3. SDKs stay MIT and VIIPER stays fixed. | Pending |
| B174 | build | Replace fragile source scrapers with existing assets/behaviour tests, delete the two retired tools and compile remaining tools in final validation. | Pending |
| B175 | build | Replace staging ceremony with one rule: recreate repository-owned output and refuse paths outside it. No staging framework. | Pending |
| B176 | build | Remove duplicated test helpers and stale build guidance; use completion signals where timing tests are flaky. Keep documentation proportional. | Pending |
| B177 | docs | Update docs for actual behaviour, recovery and remaining acceptance gaps. Inventory public APIs only as needed to account for removals and contract changes. | Pending |
| B178 | docs | Reconcile every finding and existing coverage gap against applied fixes or evidenced no-change. Keep unresolved work visible; no new audit programme or micro-batch graph. | Pending |
| B179 | docs | Run the final automated validation and report failures, then hand off the existing manual matrix. No release/deploy without explicit direction. | Pending |

## Appendix A. Every finding id and where it is resolved

### B024 closure additions and corrections

| Id | Batch or reason |
| --- | --- |
| INSTALL-C-002 | B180, exact executable-filename matching; body in install-closure.md. |
| UNCOVERED-002 | B180, restore follow-up omitted from B016. |
| U04B-LFA-041 | B180, delete the unused EnableLua snapshot field and read. |
| U04A-C-001 | B181, answer/bundle cap follow-up omitted from B028. |
| INSTALL-C-003 | B182, failed service open is not proof of absence. |
| INSTALL-C-004 | B182, owned short WTS responses must be freed. |
| U04A-LFA-021 | B182, omitted boot-manifest cap removal from B025. |
| INSTALL-C-005 | B183, failed new exemption must retain recovery intent for an older grant. |
| INSTALL-C-006 | B184, remove unlisted process-inspection byte caps. |
| INSTALL-C-007 | B184, unknown creation time cannot authorize containment. |
| INSTALL-C-008 | B184, resize allocation failure must not free the old buffer twice. |
| INSTALL-C-009 | B185, complete module list/path inspection without truncation. |
| INSTALL-C-010 | B185, foreground pump owns late-start retirement and signal lifetime. |
| INSTALL-C-011 | B186, attempted load is not a successful load result. |
| INSTALL-C-012 | B186, guarded shutdown requests with owner-thread exemption retirement. |
| INSTALL-C-013 | B187, release archive/stream when payload opening cannot transfer ownership. |
| INSTALL-C-014 | B030, RTSS body-stall cancellation and disposal through its fake download seam. |
| INSTALL-C-015 | B188, scoped disposal for owned Setup base registry keys. |
| INSTALL-C-001 | B030 (transaction/RTSS), B031 (broker); explicit scope corrections from B024. |

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
