# PR #239 review fixes

The maintainer requested fixes for all five PR comments, followed by a full Claude review before
committing or pushing. The independent review is tracked in Orca run `run_b2cf78aaf6cd`, task
`task_5c3d63b4a3e9`. Claude was explicitly instructed to inspect the entire PR and toolkit delta and
report **all findings**, without a finding quota. Source edits, validation and final disposition are
owned by the coordinator.

## Original PR findings

| Finding                                                                                                            | Resolution                                                                                                                                                                                                 |
| ------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Generated dot segments rejected](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4409796498)         | Normalize dot segments in locally generated paths, portable publication and legacy local launch recipes. Rooted/escaping paths remain rejected; portable manifest validation remains strict.               |
| [Legacy collections never bound](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4409796512)          | Read observed collections under the serialized Steam operation boundary with account checks before/after; bind exact legacy IDs/membership before reading account projections. Preserve unresolved groups. |
| [Unresolved emulator receipts discarded](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4409796520)  | Refuse emulator reconciliation when unresolved legacy ownership could otherwise be treated as an unrecorded installation. Preserve the receipt and avoid duplicate creation.                               |
| [Transient hotplug failure never settled](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4409796524) | Reconcile failed/recovered library observations and status transitions, even when the volume mount point did not change.                                                                                   |
| [Artwork-only save writes card manifest](https://github.com/KillerPixelCrew/WSGM/pull/239#discussion_r4409796532)  | Determine artwork-only intent before portable publication and omit manifest publication for that operation.                                                                                                |

## Validation

Initial fixes compiled in Release with warnings treated as errors: the OverlayPreview application
graph and the managed launch helper both completed with zero warnings/errors. Raw output is in
`TestResults/issue-239/release-build.log` and `helper-build.log`.

## Independent review

The [full Claude report](issue-239-claude-review.md) preserves all 33 findings, uncertainties and
validation gaps. Exact worker changes are recorded in [storage fixes](issue-239-storage-fixes.md)
and [UI fixes](issue-239-ui-fixes.md). A final corrective review also checks the new manual-deletion
behavior before commit/push.

| Finding | Disposition                                                                                                                                                                                                                                            |
| ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| H1      | GUID paths authorize evidence/I/O; launches use freshly resolved conventional mount paths.                                                                                                                                                             |
| H2      | Missing confirmed legacy IDs can be explicitly imported into the active account while their unknown receipts are preserved. Conflicting emulator receipts affect only their installation; background sync never recreates missing confirmed shortcuts. |
| M1      | Invalid v1 mappings are dropped row by row; invalid v2 ownership remains fatal.                                                                                                                                                                        |
| M2      | Unchanged recipes do not rewrite the card. Per-title storage errors allow unrelated imports to continue; index errors remain fatal.                                                                                                                    |
| M3      | Admitted cleanup finishes without cancellation and retains uncertain no-retry receipts.                                                                                                                                                                |
| M4      | Invalid staging is cleared, checkpoints are copied under lock, and storage actions report errors individually.                                                                                                                                         |
| M5      | Acceptance rebinds all inventory rows, including missing recipes. Malformed cached recipes cannot authorize deletion.                                                                                                                                  |
| M6      | Completed known libraries do not repeatedly migrate or overwrite their checkpoint.                                                                                                                                                                     |
| M7      | Reparse rejection is scoped to dependencies on the card's bound volume.                                                                                                                                                                                |
| M8      | Collection failures become visible problems; scan and successful title bookkeeping can finish.                                                                                                                                                         |
| M9      | Exact observed collection IDs bind despite edited memberships. Absent legacy IDs remain unbound; new active-account receipts do not erase them.                                                                                                        |
| M10     | Unknown manifest, entry or path fields are unsupported and cannot offer Repair.                                                                                                                                                                        |
| L1      | Unknown unsupported libraries show zero cached titles.                                                                                                                                                                                                 |
| L2      | Local capture, binding, comparison, publication, launch and rebind normalize generated dot segments.                                                                                                                                                   |
| L3      | Legacy fixed-drive paths retain conventional behavior; portable aliases remain rejected intentionally.                                                                                                                                                 |
| L4      | Managed IDs must contain exactly 32 hexadecimal characters.                                                                                                                                                                                            |
| L5      | Foreign manifests require explicit acceptance; a blank card's first import remains supported.                                                                                                                                                          |
| L6      | Title/library Recheck scopes inspection, pruning and cleanup to its request.                                                                                                                                                                           |
| L7      | Retained serialization tradeoff: apply owns its coherent storage lane through artwork work. Reducing that lane requires a separately measured concurrency change.                                                                                      |
| L8      | AppId-zero uncertain cleanup receipts remain pending rather than being forgotten.                                                                                                                                                                      |
| L9      | Timeout recovery respects emulator initialization.                                                                                                                                                                                                     |
| L10     | Failed settled observations are logged.                                                                                                                                                                                                                |
| L11     | Legacy missing-content checks do not reject junction parents.                                                                                                                                                                                          |
| L12     | Installed projections reuse lists/sets, honor tools filtering and wrap only relevant grids; stale comments and unused resolution were removed.                                                                                                         |
| L13     | Availability patch activation requires managed shortcuts, independently of badges.                                                                                                                                                                     |
| L14     | Unknown identity supplies no account-specific AppIds. Preserving old-account state could affect another account's shortcut. A short startup presentation gap remains possible; the helper still checks content at launch.                              |
| L15     | Steam gains Recheck all and paged openable titles; transient loading preserves details on both surfaces.                                                                                                                                               |
| L16     | Two-argument Forget is a compile-time obsolete error; existing test callers use explicit accounts.                                                                                                                                                     |
| L17     | Retained safety invariant: a batch cannot reverse InventoryRemoved while cleanup receipts remain.                                                                                                                                                      |
| L18     | Retained intentionally: the maintainer requested CEF debugging and hang recovery. The isolated workaround targets the pinned MCP package.                                                                                                              |
| L19     | Removed inventory is excluded from emulator save-conversion dependencies.                                                                                                                                                                              |
| L20     | Retained safety invariant: failed index checkpoint writes remain fatal and visible.                                                                                                                                                                    |
| L21     | Retained safety invariant: unreadable manifests do not authorize cached launches. Successful settled checks clear transient failure.                                                                                                                   |

## Manual Steam deletion

A complete account-checked read updates that account's `RemovedFromSteam` receipt flag for confirmed
shortcuts. It preserves the title, portable inventory, unknown receipts and other accounts. Pending
cleanup and unconfirmed creation are not classified from absence. Reconciliation observes native
shortcut state even when no content cleanup is pending.

Imported games remain unticked explicit Add choices, excluded from bulk selection. Emulator sync
also preserves deletion; only the explicit enable action can request recreation in the captured
account. Startup, scans and hotplug cannot grant that request.

## Corrective review and final verification

The [second full corrective report](issue-239-claude-final-review.md) preserves all 13 further
findings. It explicitly confirmed the ordinary-title manual-deletion path. The following changes
were applied before commit/push:

| Finding | Disposition                                                                                                                                                                                                                                                                                  |
| ------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| F1      | Background reconciliation no longer runs collection synchronization. Collection creation/additions remain explicit Apply/settings actions.                                                                                                                                                   |
| F2      | Only an Off-to-On emulator shortcut transition requests recreation; launch-mode edits do not.                                                                                                                                                                                                |
| F3      | Recreation requests are cleared after each pass, including failure and not-ready paths.                                                                                                                                                                                                      |
| F4      | A native read without pending cleanup can update presence, but failure is a separate Steam-state-pending log entry rather than a failed storage check.                                                                                                                                       |
| F5      | An unconfirmed legacy creation can be retried only through an explicit unticked, non-bulk Add; its unknown receipt is retained. Edited legacy shortcuts show a conflict and remain protected.                                                                                                |
| F6      | Intentional emulator deletion produces a neutral notice explaining Off/On, not an exception.                                                                                                                                                                                                 |
| F7      | Storage Apply returns its actual success count separately from problems; invalidated staging produces an explanatory note.                                                                                                                                                                   |
| F8      | A title-only check cannot clear library-wide guards/checkpoints or clear the full-check error. An unchanged per-title import preserves those guards.                                                                                                                                         |
| F9      | Interrupted initial migration can recover only when revision-one inventory exactly matches all locally recorded recipes. Other foreign manifests still require review.                                                                                                                       |
| F10     | An uninitialized native collection store reports failure rather than an empty successful read.                                                                                                                                                                                               |
| F11     | Index-write failures propagate instead of becoming collection scan notes.                                                                                                                                                                                                                    |
| F12     | Batch availability checks reuse their mount context; independent helper launches still resolve mounts freshly.                                                                                                                                                                               |
| F13     | Remains a live validation gap: the Installed projection cache assumes Steam replaces its derived arrays when content or visibility changes. Native install/uninstall and hide/unhide with managed shortcuts require attended acceptance; no such mutation was performed on the user's games. |

Release builds of the application/OverlayPreview graph, managed helper and main test assembly all
finished with zero warnings/errors after the corrective changes. This is compilation proof, not test
execution. Generated Steam asset validation passed; SHA-256:
`AD8C8E1EF1C11D8056446BA4C4EEA5A77FC383CE69172F1BDE22AFEEF0F8D705`. Fourteen Overlay captures were
rendered at 980 x 640 and 1280 x 800, including the new transient loading details case; the affected
loading images were personally inspected. No baselines were promoted.

The development deployment used `eng/dev-deploy.ps1`, including stopping/restarting WSGM and Steam.
Installed `WSGM.dll` SHA-256: `C23E234D80E6E8CAEE6902B814C92E98398EB6501DED55778996ACFBC80A9610`.
Installed helper SHA-256: `4B64D7DEB02B3E40C63E8652E9670BCA9CA388A018C02963A0078593737D0510`.

In Steam's native UI, removed only the disposable `Storage availability sample` shortcut (AppId
`2402465863`). Native search then reported Library 0. The background reconciliation updated account
`71280049` to `RemovedFromSteam: true`, retained its receipt, kept all eight inventory titles, and
left `InventoryRemoved: false`. The currently inserted SDCard3 differs from the earlier sample card,
SDCard12, and was left untouched. The sample was deliberately not recreated. Raw local proof:
`TestResults/issue-239/manual-deletion-result.json` and `live/search-after-delete.txt`.

A subsequent popup screenshot encountered the CEF debugger-pause banner again and is excluded from
visual acceptance. The missed third auto-attach path belongs to the bundled DevTools frontend.
`steam-cef-mcp-loader.mjs` now disables all three startup waits in memory for the exact pinned
dependency, rejects an unexpected source layout, and never rewrites node_modules. Its offline
transformation check and both Node syntax checks passed. Steam was restarted through the deployment
script. A subsequent clean MCP session opened the empty and populated eight-title storage modal
without a debugger banner. Both screenshots were personally inspected, including title rows and
reachable Recheck/Close controls. Local evidence is `live/storage-clean.png` and
`live/storage-populated-clean.png`. Paging beyond 24 titles and controller focus return still need
attended acceptance. The final projection also avoids labelling a manually removed shortcut as
imported while its card is disconnected; it explains that reconnecting and scanning permits an
explicit re-add.

Transport-ready transitions now request another coalesced availability check, so an initial
pre-library observation does not wait for the five-minute recovery timer before trying again. The
final cold start verified `steam-ui.library-availability` at 15:08:41 after Steam's 15:08:22 start.
Steam and WSGM were responsive and the removed sample's account flag remained set.

Toolkit corrections were pushed as `bb17b68a0b67df36c3dc14ec85327820668989cd` before recording the
parent gitlink. All supervised workers are settled and released; the coordinator inbox is empty.

Automated suites and coverage remain deferred under the repository's manual-first validation policy.
No full verification gate, CI polling or release publication was run.
