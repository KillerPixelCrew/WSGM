# Shell

Shell owns Explorer and session transitions, the tray host, live managers and integration
reconciliation, and destructive storage workflows. Read docs/boot-and-shell.md,
docs/device-integration.md, and docs/sd-cards.md before changing these paths.

## Session and integration

- Startup and teardown are serialized and fail open to a usable Windows session. Explorer recovery
  is idempotent and never depends on Avalonia, GPU, or a valid user configuration.
- CEF readiness gates Steam-dependent behavior; it must not block independent shell recovery or core
  UI indefinitely.
- Views report intent. Shell managers own the live lifecycle, and configuration stores persistent
  policy only.
- Disabling device integration completes the ordered make-safe sequence before disposing the plugin.
  No manager may recreate a disabled integration.
- Capability writes are serialized. An uncertain write is reported and is not retried automatically.
- TrayHost never coexists with Explorer's tray. Preserve the WM_COPYDATA UIPI allowance that lets
  unelevated applications reach elevated WSGM.
- Removable-card ownership is keyed by contentId rather than drive letter. Steam VDF edits are
  shape-checked, renumbered, backed up once, and atomically replaced.
- A card's name comes from its own libraryfolder.vdf marker. The label in Steam's libraryfolders.vdf
  belongs to a path registration and survives a card swap, so it must never name a card. A rename
  writes the marker whether or not Steam is running, and writes nothing else until that succeeds; an
  absent card cannot be renamed.
- A card scan goes stale the moment the reader is touched. Re-read the marker and require the
  content id to still match before acting on a scanned decision, and abandon the decision rather
  than apply it to whatever is in the reader now.

## SD-card formatting

SdFormatManager uses three destructive stages, not a fixed count of diskpart processes:

1. clean and create the partition;
2. wait up to the bounded volume-appearance deadline, then format, with at most three independently
   reverified format attempts;
3. assign a drive letter only when one is still needed.

Before starting, VerifyTarget requires a strict match for the selected removable disk. Before every
destructive diskpart invocation, reopen the physical drive and reread current identity. A known
system disk, changed known capacity or bus, or non-removable media aborts. Handle-open and
size-query failures are recorded as unreadable, while an unavailable bus query (`-1`) is tolerated
and can still compare as Same; no query failure is proof of a swap. After clean, filesystem and
partition identity no longer exist and cannot be used as swap evidence; equal-capacity media swaps
remain inherently indistinguishable.

Only a Steam library removed before erase can be restored. Once erase begins, the old identity is
retired. Preserve the distinct volume-appearance and no-volume diagnostics, including Format: volume
on disk N appeared after and no volume appeared, because they distinguish enumeration delay from
format failure.

Test orchestration with fakes and temporary paths. Never validate shell, hardware, Steam, or disk
workflows against the user's live session as part of the automated suite.
