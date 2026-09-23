# Game Library

WSGM's own Steam ROM Manager: one system that brings other launchers' games into Steam, reachable
from both of WSGM's surfaces, with artwork as a stage of the same pipeline. This page describes the
finished shape, what each part owns, and the rules that keep a second run from duplicating or
destroying what the first one wrote. How an imported game actually launches with Steam's overlay is
in [the packaged-game launcher](packaged-game-launcher.md).

Xbox is the first source. The feature is experimental: anti-cheat compatibility is unverified.

## What a user can do

**In Steam:** Quick Access → the plugin tab → "Game Library" → "Import games…" opens a full page
built from native Big Picture components. **In the overlay:** the Steam Library panel → "Game
Library". Both show the same state and call the same backend, so a change made in one is what the
other shows next.

From either surface: scan every source; review every title with what a sync would do (add, update,
adopt, skip, remove, or edited by hand), how it launches and in which mode; select what to import;
change a title's launch mode, with an acknowledgement for a multiplayer title; leave a title out, or
offer it again; apply; and change any imported title's artwork. After an apply the review stays on
the result, so the user can walk the imported titles' artwork without a rescan.

## The pipeline

```
sources ──discover──▶ games ──plan──▶ entries ──choices──▶ review ──apply──▶ records
                                                                              │
                                                    shortcut, controller override,
                                                    Store artwork; then SteamGridDB per entry
```

| Part         | Home                                                                                                   | Owns                                                                                                                                                                                                                                                        |
| ------------ | ------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Sources      | `Core\Library\LibrarySource.cs`, `XboxLibrarySource.cs`                                                | Finding one launcher's installed games and describing them in one shape: identity, name, install path, launch route, multiplayer, whether it is a game, Store artwork. Everything after discovery is shared and knows nothing about where a game came from. |
| Launch route | `Core\Library\PackagedLauncherShortcut.cs`, `Core\PackagedLaunchCommand.cs`, `src\WSGM.PackagedLaunch` | How a shortcut for a packaged game is written, and reading one back. The only code that knows what such a shortcut looks like.                                                                                                                              |
| Plan         | `Core\Library\ImportPlan.cs`                                                                           | Pure: what a sync would do to each title, from the sources, WSGM's records and Steam's live shortcuts.                                                                                                                                                      |
| Choices      | `Core\Library\ImportStateStore.cs`                                                                     | What the user decided per title, kept across scans: a picked launch mode and acknowledgement, or "don't import".                                                                                                                                            |
| Records      | same                                                                                                   | What WSGM created: the exact Target and arguments written, the app id, how much Store art landed.                                                                                                                                                           |
| Service      | `Shell\GameLibraryService.cs`                                                                          | Scanning, choices, applying, the artwork stage, the controller override; publishes the state both surfaces render.                                                                                                                                          |
| Steam page   | `Shell\SteamLibraryImportSurface.cs`, `Core\SteamUiAssets\Source\library-import.ts`                    | Surface one: route `/wsgm/library-import`.                                                                                                                                                                                                                  |
| Overlay view | `Overlay\GameLibraryView.cs`                                                                           | Surface two, one level at a time.                                                                                                                                                                                                                           |
| Artwork      | `Shell\SteamArtworkBrowserSource.cs`, `Core\Artwork\`                                                  | Store art on confirmation; the artwork page for per-entry changes.                                                                                                                                                                                          |
| Settings     | `AppConfig.GameLibrary`, Settings → Steam                                                              | The mode new single-player titles start on; whether titles with no launch route are offered.                                                                                                                                                                |

## Identity

A title is the pair of its source and its key - the AUMID, for Xbox - never its display name. Two
titles can share a name, and two sources could share a key; no two titles share both. Records and
choices are matched on the pair everywhere.

## Classifying how a title launches

A source reports a route for each title: a label, whether a validated route exists, and why. For
Xbox the manifest and game config decide.

| Verdict            | Evidence                                                                            |
| ------------------ | ----------------------------------------------------------------------------------- |
| Packaged Win32/GDK | A full-trust entry point, a GDK signal, and exactly one application in the manifest |
| UWP                | A WinRT entry point, no full-trust declaration, no game config, one application     |
| Unrecognised       | Everything else, with the specific case named                                       |

A title with no validated route can only be imported controller-only and is hidden unless Settings
says to offer it. This is the source's evidence, not a launch argument: the launcher decides the
route again from the process it actually starts.

## "Is it a game?", and the multiplayer tag

Nothing in a UWP manifest says a title is a game. GDK evidence answers it on its own; otherwise one
lookup against Microsoft's public Store display catalog by package family name answers it, along
with the multiplayer capabilities and the official images. A title neither source can vouch for is
listed and selectable, but never ticked for the user.

A multiplayer title starts controller-only. The overlay route is available only by accepting the ban
risk explicitly, enforced in the backend rather than the page. A title with no multiplayer answer
starts on the mode Settings names, the Steam overlay by default.

## The user's choices

A scan rebuilds every entry from what the sources and Steam say now, and then lays the user's stored
choices over it. A launch mode picked and not yet applied, an acknowledgement, and "don't import"
all survive a rescan. A stored choice is judged again each time by the same rule the command uses,
so a title that has since become multiplayer does not keep an overlay route nobody accepted the risk
for. Once an apply writes a record, that title's choice is dropped: the record says what Steam has.

Only a title that is not imported yet can be left out. An imported title leaves Steam by being
removed, which is a separate, deliberate act.

## Writing to Steam

Shortcuts are written through the running Steam client, one at a time. There is no offline
`shortcuts.vdf` editing; if Steam is not running the apply refuses. The Target is
`WSGM.PackagedLaunch.exe` beside the running WSGM and the key travels in the arguments, because a
non-Steam shortcut ignores an exe-replacing launch option. A missing launcher refuses the apply.

A new app id is confirmed by the add call's answer and a before/after diff of Steam's library, and
**the diff is the authority**. Disagreement records the entry as unconfirmed and stops the run; that
write is never retried, because it may well have succeeded and asking again is how a duplicate is
made.

- **Update** rewrites the launch fields in place, never remove-and-re-add, which would lose the id
  and its artwork. Changing the mode of an imported or adopted title makes it an update.
- **Adopt** takes over an unrecorded entry as what it currently launches, and records its live
  fields.
- **Conflict** means the entry was edited by hand; it is never touched.
- **Remove** needs the record, the live entry, our Target and our key to agree, and is never
  pre-selected. A record whose shortcut is already gone is cleaned up without a client call.

While an apply runs the list is read-only: a mode changed between composing a shortcut and recording
it would be pinned while the shortcut still launched the old way. Each applied title is deselected,
so when more are selected than one run takes, the next apply carries on with the rest. A controller
override that could not be written is reported as an error when the run ends.

Each entry is re-checked against Steam immediately before its own write, keeping the user's chosen
action and rechecking only its premise. Applies are capped per run; a failure stops and reports how
far it got, without rolling back. Steam refusing to return a shortcut's details stops a scan rather
than being read as "not ours", which would add a duplicate.

## The artwork stage

When a write is confirmed, the Store's images are applied to the new shortcut, each to the capsule
whose shape it fills, and the count is recorded. The entry learns its app id at that moment, so
"Change artwork…" is offered straight away. It opens the artwork page for that shortcut exactly as
the game menu does - the host opens the artwork source for the title and answers with the route -
and passes the title along, because a shortcut created moments ago is not in Steam's list yet.
Steam's own back returns to the review. From the overlay, the controller opens the page, closes the
sheet, waits for the close and the input lease, and then focuses Steam.

A controller-only entry also gets a per-game profile pinning the Xbox 360 target, keyed by the
shortcut's identity; see [the packaged-game launcher](packaged-game-launcher.md#controller-only).

## Not in this pass

Further sources. Source-declared settings. A second route family. Title resolution for sources whose
names are messy. Picking artwork alternatives before apply rather than after. Steam collections.
Scheduled sync. Renaming an existing shortcut.
