# Issue 239 UI fixes (review findings L12, L13, L15)

Source review: `reports/issue-239-claude-review.md`, left unchanged. Nothing here was built, tested,
generated, rendered, formatted, deployed or tried in Steam. The root owner runs the shared batch.

## L12: toolkit `library-badge.ts` (fixed)

File: `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/library-badge.ts`.

- Projection cost. `installedView` now reuses its derived `allApps`, `visibleApps` and `apps` while
  the Steam lists it came from are the same objects (`local-install.allApps`, `type-games.allApps`,
  the badge reading; for visibility also both collections' `visibleApps`). Those Steam lists are
  still read on every access, so the grid's mobx observer keeps tracking them. `apps` returns one
  `Set` per derived list instead of a new one per read.
- Remounts. Only the element given the `local-install` collection is wrapped. Every other grid keeps
  Valve's element type, so it is never remounted and keeps its focus and scroll.
- Tools filter. `GetAppCountWithToolsFilter` now counts each added or dropped app with Steam's own
  rule (visible, and not `app_type == 4` unless `filter.BIncludesTools()`), read from the installed
  `chunk~2dcc5aaf7.js`. Managed shortcuts are never tools, so live counts do not change.
- The stale "reads Recheck and sends `recheck`" comment now describes the disabled Play button. The
  unused `classes.text` and its `ButtonText` requirement are removed.
- `eng/check-library.mjs` asserts list reuse, the tools rule and that an unrelated grid is not
  wrapped. `docs/reference.md` describes Installed-only wrapping, reuse and counting.

Limitation: an unrelated grid (All Games with a play-state filter, for example) no longer redraws
when availability changes. Its claimed filter applies on Steam's next redraw of that grid. The
Installed tab, play bar, tile badge and details stat still update immediately. List reuse helps only
where Steam's getters return the same array between changes. Where they do not, cost is linear per
read as before, with no new `Set` per `apps` read. The generated bundle (`SteamUiAssets` output,
review line `:9573`) still holds the old comment until the root regenerates assets.

## L13: `SteamUiSessionHost.cs` (fixed)

`SteamLibraryBadgeSurface.AvailabilityPatchId` now uses `ManagedAvailabilityWanted`:
`LibraryBadges.HasManagedShortcuts && LibraryAvailabilityWanted(switches)`. The badge and details
patches keep `LibraryAvailabilityWanted`, so decorative badges work with no managed shortcuts and do
not claim Steam's filter prototype. `OnLibraryBadgesChanged` already calls `ApplySwitchStates`, so
the gate installs when the first managed shortcut appears and is removed when the last one goes. The
module's publication switch is unchanged.

## L15: Overlay and Steam parity (fixed where supported)

Transient versus real removal. The backend publishes no removal marker, so
`GameLibraryState.Loading` (service busy: scan, apply or reconcile) is the supported signal.

- Overlay `RenderEntry`: a missing title while `Loading` renders `RenderEntryPending` with the
  title's name and "The library is being updated…". The next publication renders the level again. It
  shows the title if the title returned, or runs `LeaveRemovedEntry` (toast and neighbour focus)
  once the list has settled without it.
- Steam `ImportDetailsBody`: while the state is missing or `loading`, the sheet stays open with the
  same note. It closes with "no longer listed" only after the list settles without the title.
- Limitation: a title deleted during a busy operation shows the waiting note until the operation
  ends.

Recheck all storage. The Steam page's Storage section now has a "Recheck all storage" button,
disabled while busy. It sends `recheckAvailability` with `id: ""`, the same backend call as the
Overlay's row (`RecheckAvailabilityAsync("")`). `TryReadRecheck` accepts an empty id. No backend
contract changed.

Storage title list. The Steam storage sheet replaces the 12-name text line with a "Titles (n)" fact
and `renderSteamUiSelectRow` rows. Each row is keyed by id, shows the Overlay's `Describe` line
(`importEntryLine`) and opens the existing details sheet (`showImportDetails`, now shared with the
page). Rows come in pages of 24 behind "Load more", and the first row of each new page takes focus.
The details and storage sheets pass `openTitle` through, so artwork navigation works from any path.
The Overlay already had paged, openable rows and Recheck all, so nothing changed there.

## Not verified

- No compilation, check-library run, asset generation, rendering, CEF or Steam session.
- No visual check of the Steam storage sheet with many titles: scroll inside the modal, focus after
  "Load more" through `cloneElement(..., { autoFocus })`, and focus return after a stacked details
  sheet closes.
- No Overlay render of the pending title level.
- No live check of the claim that Steam's collection getters return stable arrays.

## Files

- `external/steam-ui-toolkit/src/SteamUiToolkit/SteamUiAssets/Source/gates/library-badge.ts`
- `external/steam-ui-toolkit/eng/check-library.mjs`
- `external/steam-ui-toolkit/docs/reference.md`
- `src/WSGM/Shell/SteamUiSessionHost.cs`
- `src/WSGM/Overlay/GameLibraryView.cs`, `src/WSGM/Overlay/GameLibraryView.Storage.cs`
- `src/WSGM/Core/SteamUiAssets/Source/library-import.ts`
