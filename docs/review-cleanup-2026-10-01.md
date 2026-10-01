# Source review cleanup, 2026-10-01

This records the whole cleanup following the parallel repository review. WSGM's feature set follows
the maintainer's needs; feature count is not a defect. The crash-loop breaker remains a deliberate
recovery mechanism. Findings below are source-level behavior and ownership problems, not fresh
live-machine observations.

| Reviewed finding                                                              | Result in this cleanup                                                                                                                                                                                           |
| ----------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| UAC and wake-policy helpers saved stale whole configurations                  | Mutate only owned fields against fresh config; serialize policy operations separately; clear only the restored snapshot.                                                                                         |
| Settings treated current controls as the values saved earlier                 | Acknowledge captured values and preserve edits made while persistence is awaiting completion. Device flags return to their saved baseline.                                                                       |
| Boot-manifest errors disappeared behind successful Settings/setup status      | Manifest writers return success; Settings and setup expose failure while retaining the old manifest.                                                                                                             |
| An update could reactivate a retained device package                          | Preserve exported disabled integration; choose activation only for fresh installation or explicit package choice.                                                                                                |
| Artwork response headers had a budget but streaming did not                   | One linked deadline spans headers, stream acquisition and every body read. Caller cancellation remains cancellation.                                                                                             |
| Overlay/boot forced collection and working-set eviction                       | Remove the trim service and its scheduling rather than forcing collection and paging after routine UI work.                                                                                                      |
| Lost virtual targets could leave the physical controller hidden               | Fault recovery cancels startup, releases controller ownership, and attempts unhide with a fresh cleanup budget. It preserves the user's saved selection. Native stalls remain explicitly unconfirmed.            |
| An initial plugin settings manifest could precede subscription                | Cache the validated manifest in the runtime and replay the latest value to its attached settings coordinator.                                                                                                    |
| Plugin timeouts included system sleep                                         | Use the SDK active-time deadline token; retain lifecycle ownership until late work ends and distinguish expiry from superseded mode cancellation.                                                                |
| Haptic output copied unchanged frames and allocated Claw report buffers       | Return an unchanged immutable frame when no channel needs dropping; reuse the serialized HID report buffer. The existing public frame reference type remains compatible.                                         |
| Ally motor deadband discarded stops                                           | Whole-frame and individual-channel transitions from nonzero to zero bypass deadband.                                                                                                                             |
| Claw reader loss left held input; Ally exceptional loss could do so           | Publish a neutral sample directly before reconnect, avoiding stale motion. Ordinary Ally disconnect already neutralized input and was not a new defect.                                                          |
| One Claw observation failure skipped the other services                       | Refresh services independently, retain per-service read failures, clear them after successful refresh/acquisition, and keep read failure separate from write availability.                                       |
| Ally keyboard failure retained held state or lost queued releases             | Withdraw only the keyboard source and admission, report overflow instead of dropping it silently, retain late pumps and reap them when complete, and invalidate stale fault callbacks.                           |
| Refresh discovery/original rate belonged to an old primary display            | Invalidate by target/resolution/depth; discard stale results, read originals by captured target, and apply/restore to that target.                                                                               |
| A strategy change at the same cap could leave the old pairing                 | Reevaluate pairing when the strategy or operating point changes, even when the cap itself is unchanged.                                                                                                          |
| Saved desktop layout/audio was unreachable after abnormal exit                | Share guarded return recovery across startup, explicit entry, escape, panic and shutdown; retain failures and protect newer snapshots.                                                                           |
| UI shutdown stopped after one disposal failure                                | Independently guard resources and tray retirement while keeping Explorer recovery reachable.                                                                                                                     |
| Setup rollback restored only App                                              | Journal the coherent file installation and registration version, recover before package detection/planning, and prevent normal runtime startup while incomplete.                                                 |
| Setup timed out while an external installer kept running                      | Continue owning and awaiting that child before rollback or another installation step.                                                                                                                            |
| Sign-in recovery mistook a folder Explorer PID for a desktop                  | Query a fixed-purpose shell-surface probe and dispose its process lifetime on timeout.                                                                                                                           |
| Explicit artwork clears disappeared from the apply list                       | Carry empty-address clears through the existing writer and settle them once.                                                                                                                                     |
| Animation promotion could lose ownership and overwrite an original backup     | Persist pending ownership before promotion, finish interrupted ownership on the next apply, and preserve both unowned replacement and existing original.                                                         |
| Theme archive failure could leave an earlier folder already replaced          | Stage and validate the whole package, journal folder promotion, restore interrupted updates, retain user files, and block competing theme mutations while store work is busy. Plain CSS themes remain supported. |
| Native launch assigned a job after the child started executing                | Create suspended, assign to the job, retain exact process identity, then resume. Preserve quoting and sanitized environment.                                                                                     |
| Packaged-game verdict cache trusted a reused PID                              | Include creation time, rejudge unknown/changed identity and refuse mismatched facts.                                                                                                                             |
| Startup auto-relaunch compared only process names and duplicated delayed work | Probe canonical paths, invalidate obsolete delayed callbacks, recheck immediately before dispatch and retain owed exits across inconclusive probes. Never repeat an uncertain launch.                            |
| Library tabs replaced React's dispatcher property independently               | Use the toolkit's shared memo ownership claim and remove only WSGM's transform. Generate the changed asset from TypeScript.                                                                                      |
| Motion shutdown disposed native state while its reader could still run        | Retain reader/native owners until actual completion; bounded callers report pending cleanup and late task faults are observed.                                                                                   |
| Claw documentation and matcher incorrectly claimed ordinary keyboard chords   | Restore the captured Win-down, orphan G/Tab-up, Win-up distinction. Complete keyboard chords pass, and failed synthetic release is not retried within the same burst. Update guidance and regression cases.      |

## Corrections and retained tradeoffs

The maintainer's captured Claw flow omits the target key down. The earlier assumption that ordinary
keyboard Win+G should be swallowed was wrong. No new hardware capture was taken here. Existing
HC-derived descriptions are reference evidence, not authority to override that capture.

The Claw power watchdog rereads the actual power limits before a later reassertion. That is an
observed-state reconciliation, so the broad claim of automatically retrying an uncertain write was
withdrawn. Ordinary Ally XInput disconnect already emitted a neutral sample; only its exceptional
failure boundary needed additional protection.

The Device SDK's public haptic frame remains a reference type. This cleanup removes avoidable copies
and report buffers; it does not claim a completely allocation-free native-to-plugin output contract.
Steam internals still change, and theme dependency installs can succeed individually before a later
dependency fails. Broad class decomposition and compatibility-breaking SDK work need their own
concrete requirements rather than being justified by a label such as duct tape.

## Validation and manual follow-up

Regression source covers firmware versus keyboard chords, failed synthetic-release repetition,
Settings acknowledgements, interrupted setup/theme/animation replacement, explicit artwork clears,
refresh identity and motor stops, IR rejection before versus uncertainty after emission, and native
environment serialization. Compilation and formatting passed before the maintainer authorized
continuing with automated validation on 2026-10-01, ahead of the live manual pass. All 179 focused
regression cases passed. The full gate initially exposed an outdated module-discovery fixture: it
evaluated the library-tab fragment without substituting the bridge namespace or supplying its new
shared memo gate. The fixture now uses the emitted ownership primitives and proves that removing
library tabs retains another consumer's memo claim. The composed Steam ownership checks then passed.

The broader run found a cleanup regression in plugin stop cancellation: the caller wait shared the
worker's cancellation source and could throw before the provider returned its cooperative
`Unconfirmed` result. Separate active-time deadline sources now distinguish internal worker stop
from caller cancellation without releasing lifecycle ownership early. Focused host/action checks
passed after this fix. The IR endpoint refusal check now expects its explicit no-emission exception.

Two older validation assumptions also needed correction. Sound overrides decode bounded
`data:audio/...;base64,...` assets, so the authority test now permits only that validated decoder
while rejecting other fetch calls. The September 30 collapsible-section and GPU-rail changes had
left interaction tests and six image references stale before this cleanup. Tests now open headings
through pointer input before editing, inspect the fold's retained body, and close windows before
checking fake-device disposal. Expected/actual images were inspected before refreshing those six
references. The nine device-page captures then passed.

Full-gate completion is recorded below after the final run.

The manual pass should exercise Settings save/second save, retained disabled integration on update,
Game/desktop transitions and recovery, controller disappearance/virtual-target failure, Claw QS
alongside keyboard Win+G/Win+Tab, small rumble stops, dock/resolution/strategy changes, library tabs
with native Quick Access rows, artwork clearing, theme/movie changes and launches behind a
short-lived launcher. Installer crash recovery needs an attended disposable installation scenario.
No deployment, Windows policy change, shell takeover, hardware action or live Steam evaluation was
performed by this cleanup task.
