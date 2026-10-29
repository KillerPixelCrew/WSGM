# PR #239 second review round

Base: `00ab2b380ba34ce24a0113ca18a7b19897133ff4`. The maintainer requested the same sequence: fix
the new comments, obtain a full Claude review reporting all findings, address that report, then
commit and push. No earlier comment was treated as a new finding merely because GitHub updated its
diff position.

| New finding                                                                                                            | Correction                                                                                                                                                                                                                                                             |
| ---------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Valid manifest entry deleted by stale cache](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4412880199) | Manifest-owned IDs are inspected by the manifest pass. A stale cached path cannot remove or replace that recipe. Cached-only inventory still receives actual-file reconciliation.                                                                                      |
| [Concurrent recreation request erased](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4412880217)        | Each emulator pass atomically takes its own request set. Only that batch expires, while requests queued during the pass remain pending for the next wake.                                                                                                              |
| [Artwork choices replay after application](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4412880231)    | Per-account shortcut receipts track confirmed explicit asset URLs, including clears. Scan omits matching settled picks only for the same account and AppId. Shared choices remain intact; a replacement shortcut resets its settlement. Failed slots remain unsettled. |

Artwork results follow the existing ordered-slot contract. Confirmed shortcut ownership and profile
ownership are persisted before slower artwork work. Downloads and unstarted slots remain
cancellable; each admitted native write finishes with a bounded outcome, and completed slot results
are returned on Stop. Settlement dictionaries are copied on every canonical/projection boundary and
validated on load.

Claude review: Orca run `run_f7dcf73e7dbf`, task `task_e984ed29bf85`, dispatch `ctx_6be4991224d3`.
Instructions explicitly required **all findings**, not a quota, across the complete corrective diff
and affected callers, including partial failures, cancellation, account migration, replacement
shortcuts and shared choices. The reviewer may write only its report; the coordinator owns code,
builds, deployment and Git mutations.

Initial Release application/OverlayPreview compilation and main test-assembly compilation both
passed with zero warnings/errors. Two new source regression cases cover account-specific settlement
after reload with shared choices retained, and detached dictionary mutation including an empty clear
URL. These cases were compiled, not executed. Raw output is under `TestResults/issue-239-round2/`.

## Claude review and corrections

The [complete Claude report](issue-239-round2-claude-review.md) records all eight actionable
findings and three informational notes. It confirmed the reported triggers were fixed before
identifying these further corrections, which were applied before committing:

| Finding                                                    | Resolution                                                                                                                                                                                                                                                                                        |
| ---------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| H1: subsequent edits erase settled shared picks            | The entry retains settled and pending picks separately. Saving another choice merges both, with pending selections taking precedence. Applying moves only handled slots into settled state. A service regression case covers changing mode and inspecting the shared choice from another account. |
| M1: uninterruptible artwork delays durable ownership       | Confirmed shortcut and controller-profile receipts are saved before artwork. Downloads accept cancellation; cancellation returns ordered outcomes for completed/unstarted slots, while admitted native writes finish without an uncertain cancellation.                                           |
| M2: unadmitted/unreached emulator requests disappear       | Requests not consumed by a not-ready or failed pass are restored without spinning the worker. Account mismatches are discarded once identity is known. Requests admitted to an installation attempt are not restored, preserving uncertain-write safety.                                          |
| M3: reverse manifest/cache disagreement destroys inventory | A missing manifest recipe plus a present or unreadable cached recipe is retained as a conflict, without deleting or replacing either. Automatic removal requires both recorded recipes to be confirmed missing.                                                                                   |
| L1: adoption copies another shortcut's settlement          | Settlement, artwork count and profile ownership are reused only for the same AppId, including Adopt rather than just Add.                                                                                                                                                                         |
| L2: failed slots differ across restart                     | Failed picks remain pending in the same session as well as after reload. Only handled picks are removed from the pending selection.                                                                                                                                                               |
| L3: explicit same-URL restore vanishes                     | Each explicit choice/clear gets an opaque revision. Matching settlement checks both URL and revision, so reselecting the same image remains a new request across scans and restarts.                                                                                                              |
| L4: cached pass has dead replacement logic                 | Present cached-only recipes are added directly; manifest-owned IDs are never overwritten by that branch.                                                                                                                                                                                          |
| I1: unlocked pending-pick access after await               | Pending picks are captured under the same lock as the image list.                                                                                                                                                                                                                                 |
| I2: JSON compatibility                                     | Missing new fields default safely; malformed receipt fields remain a strict index failure. Downgrading to older prerelease code can discard settlement metadata, so a later upgrade may replay those selections once.                                                                             |
| I3: coverage gaps                                          | Three focused regression cases were added/compiled, including service-level preservation. Automated execution, concurrency fixtures and live divergent-manifest/artwork cases remain pending under the manual-first policy.                                                                       |

## Delivery proof

- Final Release application/OverlayPreview build: zero warnings/errors. Final main test-assembly
  compilation: zero warnings/errors. The delivery application build also passed with warnings
  treated as errors after the same-AppId metadata guard was tightened.
- Three regression cases were added and compiled, including the service-level shared-choice
  preservation path. They were not executed; automated suites and coverage remain deferred under the
  repository's manual-first policy.
- Development deployment completed through `eng/dev-deploy.ps1`. Installed `WSGM.dll` SHA-256:
  `F1CCDCE4E3E0183611B2F6C1BC1BCF9B4FFA42265D6978770B78C142F6445AB9`.
- Steam and WSGM were running and responsive after deployment. No card files, real game artwork or
  emulator installations were mutated to simulate these cases. The divergent-manifest, concurrent
  toggle, partial-artwork and cancellation scenarios still need attended behavior acceptance.
- Raw output is in `TestResults/issue-239-round2/release-build-final.log`,
  `test-compilation-final.log`, `delivery-build.log` and `dev-deploy.log`.
- The Claude worker completed and was released. No worker terminal remained reclaimable.

No full verification gate, UI baseline changes or CI polling was run. There are no UI layout,
generated-asset or submodule changes in this round.
