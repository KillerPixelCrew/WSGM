# SD cards and card libraries

The card manager and the Format SD Card flow in the overlay. Registering a card's library with a
running Steam, the duplicate-registration behaviour that makes a swapped card show the previous
card's games, and the reconcile on volume arrival and removal are in `docs\steam-cef.md`
("Registering a library with a running Steam"). The format mechanism itself, the three-diskpart
sequence and the volume-arrival wait it survives, is beside the code in `src\WSGM\Shell\AGENTS.md`.

## A card is named by its own marker, never by Steam's config label

`config\libraryfolders.vdf` carries a `label` per **registration at a path**, not per card. A reader
gives every card the same path, so when Steam re-registers that path for a different card it keeps
the label the previous card's registration had, now attached to the new card's `contentid`.

The **defect** was observed on the reference Claw on 2026-09-05, from that session's log and config:
two cards with different content ids, `SDCard9` in the reader and `SDCard10` in the drawer. Every
swap flipped the tracked name of the single content id `5449024381361189696` between the two, and
both cards presented as `SDCard9`. The log line was
`Card <id>: following Steam rename 'SDCard9' -> 'SDCard10'` and its inverse; the card's own
`libraryfolder.vdf` never stopped saying `SDCard9`. The **corrected** behaviour below has not been
run on the device — no card swap has been performed since the change.

So the card's own marker label is the only name WSGM follows. It is the only copy that travels with
the media, and it cannot be attributed to the wrong card:

- `LibraryTabManager.ScanLibraries` reads the marker's `label`; it no longer reads Steam's config
  labels at all, and `ResolveName` falls back only to values that also come from the media (the
  volume label, then the drive letter).
- `LibraryTabManager.MergeDiscovery` takes the marker label whenever the card carries one. A card
  with no marker label keeps the name it already has, so a rename made here is not lost to the
  drive-letter guess on the next scan.
- `CardLibraryConfig.Name` is a cache of that label, kept so the card still has a name in the
  manager and its tab while it is ejected and nothing can be read from it. `LastSteamLabel` and the
  two-way "follow a Steam-side rename" rule that used it are gone; there is no second name to
  reconcile.
- `RenameCardAsync` writes the marker through `TrySetMarkerLabel` whatever Steam is doing.
  Previously the marker was written only with Steam closed, so in game mode — where Steam always
  runs — a rename reached the live client and the volume label but never the media, and the
  authoritative copy drifted immediately.
- `CardVolumeMonitor` passes the marker label to `AddInstallFolder`, so the registration Steam
  builds for the card that is actually in the reader is labelled for that card instead of inheriting
  the previous one's. This is what keeps Steam's own storage page honest; WSGM no longer depends on
  it.

## The rename is ordered around the write that can fail

`TrySetMarkerLabel` runs first and everything else is conditional on it. It selects the block by
content id, so a card swapped between `FindMountedLetter` and the write cannot be relabelled, and it
reads the marker back afterwards, so a half-applied replace is reported rather than assumed. Only
once the media says the new name does the rename update the tracked cache, Steam's label and the
Windows volume label.

The order matters because the other two writes are addressed differently. Steam's is by content id
and would survive a mistake, but the volume label is by **drive letter**: applied after a failed
marker write it renames whichever card is in the reader now. And a tracked name the media does not
carry is reverted by the very next scan, so a rename that stopped at the cache would appear to work
and then silently undo itself.

**A card that is not in a reader cannot be renamed** — the manager answers "Insert the card to
rename it." Nothing can reach an absent card's marker, so such a rename could only live in the cache
until the card came back and the marker overwrote it. Before the marker became authoritative this
happened to stick, which is why it was previously allowed.

## A reconcile decision is re-checked against the media before it is applied

Each card in a `CardVolumeMonitor` pass costs a CEF round trip, so by the time a later card is acted
on the scan can be seconds old, and a reader takes a new card in far less than that.
`StillInTheReader` re-reads the marker immediately before the add or replace and abandons the
decision when the identity no longer matches — otherwise the pass would register the card that left,
or hand the card that arrived the previous one's label, and `Decide` would see a matching id
afterwards and never correct it. The swap raised its own notification, so the pass it schedules
decides again on what is actually there.

## Format SD Card lives inside the Card Manager

Formatting a card and managing tracked cards are one subject, so the Format button is a Card Manager
action rather than a Tools entry. `CardManagerView` raises `FormatRequested`; the overlay leaves
that sub-view and enters `PanelFormat`, because two Tools sub-views must not own the surface at
once. Cancel and Back return to the Card Manager through `LeaveFormatSubViewToOrigin`, which
rescans, so a card that was just formatted appears immediately.

The two feature toggles stay independent. `OverlayViewModel.ShowFormatInTools`
(`ShowSdCard && !ShowCardManager`) brings the Tools button back when the Card Manager is switched
off, so `Cef.SdFormat` can never be on with no way to reach it. `_formatReturnsToCards` is cleared
in `LeaveFormatSubView` so the `Activated` teardown cannot bounce a fresh summon back into the Card
Manager.
