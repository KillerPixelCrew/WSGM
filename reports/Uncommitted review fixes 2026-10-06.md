# Review fixes, 6 October 2026

This records the corrections to
[the combined review](Uncommitted%20changes%20review%202026-10-06.md). All changes remain
uncommitted. Source corrections, automated checks and live acceptance are separate.

| Findings                           | Correction                                                                                                                                                                                                                                                                                                                                                           |
| ---------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Bugs 1–5, 15, 17, 26, 28–29, 32    | Canonical content/preference/emulator checks; typed source and launch kinds; friendly invalid-data refusals; preserved native/package/follow routes; shared contained startup and caller-integrity policy; isolated source parsing; one command composer and folder identity comparison.                                                                             |
| Bugs 3, 7–9, 14, 21, 24–25, 27, 33 | Complete launch-record comparison; record-only edits avoid Steam writes; stable/reused folder identities; independent cancellation and manager lifecycle; locked patch switch updates; normalized extensions; cleanup cancellation restores the pending plan; guarded source removal and shared display policy; consistent argument payloads.                        |
| Bugs 6, 12–13, 30                  | Provider-neutral artwork queries; unknown-ROM name fallback; ordinary failure isolation; actual-provider quota/auth pauses; signature/reset-aware persistent results and atomic coalesced writes.                                                                                                                                                                    |
| Bugs 10–11, 18–20, 22–23           | Retired binaries/cache pruning; remembered external-install removal; exact-version repair and separate channel installations; stable frontend identity independent of nightly cores; sibling cancellation; clearable defaults; actual native prerequisite destinations.                                                                                              |
| Bugs 16, 31                        | Safe empty-location rendering and direct Steam title writes without a readback poll.                                                                                                                                                                                                                                                                                 |
| Design/reuse                       | Shared volume, process, command, INI, download, hash and validation utilities; declarative emulator policy; one manual source and ROM collections per system; source caching and batched provisional receipts; host-projected choices; standalone manager backend, page and view; small progress publications; shared Steam dialog controls and scoped tile refresh. |
| Simplification/efficiency/style    | Removed unused APIs/fields, project data caps, duplicate redirect/probe/journal mechanisms, periodic store rewrites, metadata-file/IOCTL watchers, repeated per-ROM mount/prerequisite reads, full-tree publication walks, hidden manager subscriptions and formatting churn. C# cleanup and generated-asset formatting use the repository authorities.              |

The integration pass also corrects midscan edit loss, canceled-scan confirmations, observations for
superseded launch records and emulator save/log writes triggering content checks.

Issue 200's last successful validation time remains, with persistence on real state/metadata writes.
Durable ownership after an uncertain Steam add remains. Expected-volume, extraction and owned-path
boundaries remain because they prevent wrong-volume launches or deleting user data. The launcher's
existing RunAsInvoker policy handles administrator manifests without introducing a privileged
worker.

The Ally X crash report additionally exposed wrong RSR/AFMF native signatures. Those calls now use
their output-interface argument correctly; the two native contract regressions passed. A separate
crash-fix setup was built from the previously validated payload while the combined corrections were
being integrated.

Validation: scoped Rider Full Cleanup completed; warning-free Release builds, Prettier, asset drift
and guidance checks passed. The focused library/artwork/update/manager batch passed 178 tests. All
18 emitted Steam checks and module-discovery checks passed. Canonical setup rebuilt all six plugins
with no outdated packages; development deployment restarted WSGM and Steam with matching installed
app/helper hashes. Both independent Steam pages reached Verified in the startup log.

The delivered setup is version 2.1.0.1644. An independent module-discovery commit advanced the
checkout to bf31ab56 during the work; these review corrections remain uncommitted. Full emulator,
media, controller and Ally X tester acceptance remains manual. The full solution test/coverage gate
and UI baseline acceptance are deferred.
