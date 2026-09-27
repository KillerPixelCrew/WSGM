# Game Library

WSGM's own Steam ROM Manager: one system that brings other launchers' games into Steam with their
artwork, reachable from both of WSGM's surfaces. This page describes the finished shape, what each
part owns, and the rules that keep a second run from duplicating or destroying what the first one
wrote. How an imported Xbox game launches with Steam's overlay is in
[the packaged-game launcher](packaged-game-launcher.md).

The feature is experimental: anti-cheat compatibility is unverified.

## What a user can do

**In Steam:** Quick Access → the plugin tab → "Game Library" → "Import games…" opens a full page
built from Big Picture's own components. **In the overlay:** the Steam tab → the "Game Library"
tile, shown while Steam integration is on. Both show the same state and call the same backend, so a
change made in one is what the other shows next. The overlay has no artwork pictures; it hands the
artwork stages to the Steam page.

On the Steam page:

- **Sources.** A sidebar lists every launcher WSGM knows, each detected on its own. A found one has
  a checkbox and a title count; one that is not installed is shown greyed as "Not found". A
  shortcuts folder the user added is listed under Custom, with "Add folder…" opening a native folder
  picker. Unticking a source leaves it out of the next scan and never offers its imported titles for
  removal.
- **Review.** A poster grid grouped by source, with tabs for All, New, Imported, Needs attention and
  Left out, a search, and one artwork type shown at a time (portrait, wide, hero, logo, icon). Each
  card shows the image that will be applied, a selection check, what a sync would do, and how the
  title launches. LT and RT cycle the focused card's image in place, A selects, X switches the
  launch mode or route, Y opens the title's artwork and Menu its details.
- **One title's artwork** (Y on a card): every candidate for each artwork type, grouped by provider,
  with "Fix match" to search for the right game when the automatic match is wrong.
- **All artwork:** every selected title as a row, one column per artwork type. The D-pad moves
  between cells and LT/RT cycle an image, so a whole library is dressed without opening titles one
  by one. Bulk actions fill every title from the Store or SteamGridDB first, fill only empty slots,
  set one column for every title, or reset.
- **Details** (Menu on a card): how the title launches and why, the launch route or mode, what a
  sync would do, the chosen artwork per type, where it is installed, and its identity; "Don't
  import" and "Offer again".
- **Save to Steam** applies the selected titles: shortcuts, controller overrides and the chosen
  artwork. A title already in Steam whose artwork was changed here is saved as an artwork update
  without touching its shortcut.

## Sources

| Source           | Id           | Detected by                                                    | Titles from                                                                                       | Launch routes, default first                                                         |
| ---------------- | ------------ | -------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------ |
| Xbox             | `xbox`       | always                                                         | installed packages, classified by runtime; Store catalog for game, multiplayer and images         | the packaged launcher, in Steam-integration or controller-only mode                  |
| Epic Games       | `epic`       | the launcher's uninstall entry or its manifests folder         | `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`, DLC and engine parts dropped        | through the Epic launcher; the game's own executable                                 |
| GOG Galaxy       | `gog`        | `GOG.com\GalaxyClient\paths`                                   | `<id>_is1` uninstall entries by GOG.com and their `goggame-<id>.info` primary play task           | the game's executable; through Galaxy                                                |
| Ubisoft Connect  | `ubisoft`    | the launcher's uninstall entry                                 | `ubisoft\Launcher\Installs\<id>` and the launcher's configuration cache for names and executables | the game's executable; through Ubisoft Connect                                       |
| Battle.net       | `battlenet`  | the uninstall entry naming `--uid=battle.net`                  | uninstall entries with a Battle.net uid, mapped to product codes                                  | through Battle.net only                                                              |
| itch             | `itch`       | `%AppData%\itch\db\butler.db` or an uninstall entry named itch | the butler database's caves and their verdict executables                                         | the game's executable only                                                           |
| Amazon Games     | `amazon`     | the launcher's uninstall entry                                 | `GameInstallInfo.sqlite` installed rows and each game's `fuel.json`                               | through Amazon Games when the game needs its sign-in; otherwise the executable first |
| Prism Launcher   | `prism`      | its data folder and executable                                 | instance folders and their `instance.cfg` names                                                   | `prismlauncher.exe --launch <instance>` only                                         |
| ATLauncher       | `atlauncher` | its data folder and executable                                 | instance folders and their `instance.json` names                                                  | `ATLauncher.exe --launch <instance>` only                                            |
| Shortcuts folder | `folder:<n>` | the folder exists                                              | `.lnk`, `.url` and `.exe` files, optionally in subfolders                                         | the shortcut's own target and arguments; a `.url` through its protocol's handler     |

Playnite's library plugins and Steam ROM Manager's parsers in `_ref` are the reference for every
launcher except Prism Launcher and ATLauncher, which neither covers; those two follow their own
instance layouts and command lines. A ROM folder source comes with the emulator installer and is not
part of the Game Library on its own. Amazon Games and itch keep their installs in SQLite databases,
which are copied and read, never opened in place.

Battle.net titles come from the uninstall list and, for games it wrote no entry for, from the
agent's `product.db`, as Playnite reads both. itch's folders are resolved as butler's own
`Cave.GetInstallFolder` resolves them, a custom install folder first, from the column names in
butler's models; its queries fall back to narrower ones if a release renamed a column. ATLauncher's
`--launch` matches the instance's name or safe name (`App.java`), and it closes after starting the
game, which the follow mode covers.

A launcher route never goes through `explorer.exe` or a PowerShell wrapper. Explorer is not running
in Game Mode, and a wrapper that exits at once leaves Steam thinking the game stopped. A launcher's
URI is handed to the executable its protocol is registered to, with the URI as the argument that
registration names.

Every route through a launcher runs through `WSGM.PackagedLaunch --follow`, which starts the
launcher outside Steam's tree and stays alive while the game runs, so Steam shows it running and
keeps its controller layout; see
[following another launcher's game](packaged-game-launcher.md#following-another-launchers-game). It
needs the install folder, or for Minecraft the instance folder, to recognise the game. A `.url` in a
shortcuts folder has neither, so it runs the handler directly and Steam may lose track of it. A
direct route runs the game's own executable, which Steam tracks and injects its overlay into.

## The pipeline

```
sources ──detect/discover──▶ games ──plan──▶ entries ──choices──▶ review ──apply──▶ records
                                                │                                     │
                                         artwork candidates ──picks──────▶ shortcut, controller
                                         (Store, SteamGridDB,                override, chosen art
                                          Screenscraper)
```

| Part             | Home                                                                                                           | Owns                                                                                                                                                                                                                       |
| ---------------- | -------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Sources          | `Core\Library\LibrarySource.cs`, `Core\Library\Sources\`                                                       | Detecting one launcher and finding its installed games in one shape: identity, name, install path, launch routes, multiplayer, whether it is a game, catalog images. Nothing after discovery knows where a game came from. |
| Launch routes    | `Core\Library\ShortcutRoute.cs`, `Core\Library\ProtocolHandler.cs`, `Core\Library\PackagedLauncherShortcut.cs` | How a title's shortcut is written and recognised again. The packaged route composes a `WSGM.PackagedLaunch.exe` request; a command route is an exact Target, directory and arguments.                                      |
| Plan             | `Core\Library\ImportPlan.cs`                                                                                   | Pure: what a sync would do to each title, from the sources, WSGM's records and Steam's live shortcuts.                                                                                                                     |
| Choices, records | `Core\Library\ImportStateStore.cs`                                                                             | What the user decided per title (mode or route, "don't import", artwork picks, a fixed match) and what WSGM created.                                                                                                       |
| Artwork stage    | `Shell\GameLibraryArtwork.cs`                                                                                  | Candidates per title and artwork type, fetched in the background after a scan; picks, cycling, bulk fills; applying the picks.                                                                                             |
| Service          | `Shell\GameLibraryService.cs`                                                                                  | Scanning, choices, applying, the controller override; publishes the state both surfaces render.                                                                                                                            |
| Steam page       | `Shell\SteamLibraryImportSurface.cs`, `Core\SteamUiAssets\Source\library-import.ts`                            | Surface one: route `/wsgm/library-import`.                                                                                                                                                                                 |
| Toolkit          | `external\steam-ui-toolkit`: library capsule and file picker                                                   | The capsule drawn with Steam's own library classes, and a folder and file picker Steam does not have. WSGM lists the file system for it.                                                                                   |
| Overlay view     | `Overlay\GameLibraryView.cs`                                                                                   | Surface two: sources, review and apply, one level at a time.                                                                                                                                                               |
| Settings         | `AppConfig.GameLibrary`, Settings → Steam                                                                      | The mode new single-player Xbox titles start on, whether titles with no launch route are offered, which artwork provider a title starts on, the disabled sources and the shortcuts folders.                                |

## Identity

A title is the pair of its source and its key, never its display name. The key is the AUMID for
Xbox, the launcher's own id elsewhere, and the file's path relative to the folder for a shortcuts
folder. Records and choices are matched on the pair everywhere. A source id is never reused.

## Classifying how a title launches

Xbox titles are classified from the manifest and game config:

| Verdict            | Evidence                                                                            |
| ------------------ | ----------------------------------------------------------------------------------- |
| Packaged Win32/GDK | A full-trust entry point, a GDK signal, and exactly one application in the manifest |
| UWP                | A WinRT entry point, no full-trust declaration, no game config, one application     |
| Unrecognised       | Everything else, with the specific case named                                       |

A title with no validated route can only be imported controller-only and is hidden unless Settings
says to offer it. This is the source's evidence, not a launch argument: the launcher decides the
route again from the process it starts.

Every other source offers one or more command routes, each with its label and why. X on a card
switches between them; a title with one route has nothing to switch. Controller-only mode and the
ban-risk acknowledgement belong to the packaged route alone, because only that route injects.

## "Is it a game?", and the multiplayer tag

Nothing in a UWP manifest says a title is a game. GDK evidence answers it on its own. Otherwise one
lookup against Microsoft's public Store display catalog by package family name answers it, along
with the multiplayer capabilities and the official images. A package neither can vouch for is not
listed: every installed Store application carries the same package identity a game does, and the
first live review offered Paint and Clipchamp beside the games. The scan logs how many packages it
left out for that reason, and their names, so a UWP game the Store could not be asked about is found
in the log rather than lost. Launcher sources drop DLC, engine parts and tools the way their
references do, and what they list is a game.

A multiplayer Xbox title starts controller-only. The overlay route is available only by accepting
the ban risk explicitly, enforced in the backend rather than the page. A title with no multiplayer
answer starts on the mode Settings names, the Steam overlay by default.

## The user's choices

A scan rebuilds every entry from what the sources and Steam say now, and then lays the user's stored
choices over it. A launch mode or route picked and not yet applied, an acknowledgement, "don't
import", artwork picks and a fixed match all survive a rescan. A stored choice is judged again each
time by the same rule the command uses, so a title that has since become multiplayer does not keep
an overlay route nobody accepted the risk for, and a route the source no longer offers is dropped.
Once an apply writes a record, that title's route and mode choice is dropped: the record says what
Steam has. Its artwork picks are dropped once applied.

Only a title that is not imported yet can be left out. An imported title leaves Steam by being
removed, which is a separate, deliberate act. A title whose source is unticked is neither scanned
nor offered for removal: turning a launcher off is not a request to delete its games.

## Artwork

After a scan, the artwork stage gathers candidates for every listed title in the background, three
titles at a time with each title's five lookups sent together, starting with the ones a surface asks
for. The candidates are the source's catalog images (the Store, for Xbox) and the provider results
for the title's match. SteamGridDB is asked first. Screenscraper, a ROM database, is a fallback
asked only when SteamGridDB has nothing for the name, and "Fix match" searches the same way.
SteamGridDB takes four requests in flight and backs off on a 429. Anonymous Screenscraper is allowed
one thread, its requests queue behind one gate, and a game page it answered is kept for the session
rather than fetched once per artwork type. Rows fill in as their results arrive. The match is the
provider's first exact search result for the title's name, else its first result, or the game the
user picked with "Fix match", kept as a choice.

Each title starts on the first candidate from the provider Settings prefers, per artwork type. A
title Steam already has starts on "keep current" and changes nothing until the user picks an image.
Cycling, picking, clearing and the bulk fills change picks only; nothing is downloaded until Save to
Steam. On save, a title's picks are downloaded together and applied one at a time to the confirmed
shortcut through the same path the artwork page uses. A pick that fails to download or apply is
reported and does not stop the run.

## Writing to Steam

Shortcuts are written through the running Steam client, one at a time. There is no offline
`shortcuts.vdf` editing; if Steam is not running the apply refuses. For an Xbox title the Target is
`WSGM.PackagedLaunch.exe` beside the running WSGM and the key travels in the arguments, because a
non-Steam shortcut ignores an exe-replacing launch option. A missing launcher refuses the Xbox
titles in an apply; other sources do not need it.

A new app id is confirmed by the add call's answer and a before/after diff of Steam's library, and
**the diff is the authority**. Disagreement records the entry as unconfirmed and stops the run. That
write is never retried, because it may have succeeded and asking again is how a duplicate is made.
If a later scan still finds no such shortcut, the title is offered as an Add that says so and is
never ticked for the user: only they can look at the library and decide. Once Steam has accepted a
write, Stop waits for that entry to be recorded, so the next scan does not mistake WSGM's own change
for a hand edit.

- **Update** rewrites the launch fields in place, never remove-and-re-add, which would lose the id
  and its artwork. It only happens because the user changed an imported or adopted title's mode or
  route. An imported title with new artwork picks and nothing else changed is an artwork update: no
  shortcut call at all.
- **Adopt** takes over an unrecorded entry as what it currently launches, and records its live
  fields. For a command route that means a shortcut whose Target and arguments are exactly one the
  title's routes would write.
- **Conflict** means the entry was edited by hand: its Target is no longer ours, or its fields
  differ from what was written. It is never touched, because restoring the recorded command would
  silently undo the user's own change.
- **Remove** needs the record, the live entry and its fields to agree, and is never pre-selected. A
  record whose shortcut is already gone is cleaned up without a client call.

While an apply runs the list is read-only. Each applied title is deselected, so when more are
selected than one run takes, the next apply carries on with the rest. A controller override that
could not be written is reported as an error when the run ends, and so is a controller-only title
applied while controller management is off. An update or an adoption keeps the artwork the entry
already has unless the user picked something.

Each entry is re-checked against Steam immediately before its own write, keeping the user's chosen
action and rechecking only its premise. Applies are capped per run; a failure stops and reports how
far it got, without rolling back. Steam refusing to return a shortcut's details stops a scan rather
than being read as "not ours", which would add a duplicate.

A controller-only Xbox entry also gets a per-game profile pinning the Xbox 360 target, keyed by the
shortcut's identity; see [the packaged-game launcher](packaged-game-launcher.md#controller-only).
The record notes whether the import created that profile. Switching the title to Steam integration
or removing it clears the target, and removes the profile only when the import created it and
nothing else is left in it.

## The folder picker

Steam has no file or folder picker a page can open, and a Windows dialog would open behind Big
Picture with no controller support. The toolkit's picker is a Steam modal with drives and places,
the current path, and the folder's contents; A opens a folder, X uses the current folder, Y goes up
a level and B cancels. The page cannot read the disk, so WSGM answers its listing requests: the
drives, the known places, one folder's subfolders and the files matching the requested types. It
lists names only and never reads a file. A drive that is not ready or a folder that cannot be opened
is an error in the picker, not a failed page.

## Evidence

The sources, the plan and the service are covered by tests over fixtures. The installed Steam client
was checked offline on 2026-09-27: the library item class map, the checkbox module and the tabs
module each match one module. Seen live in Big Picture on the reference Claw the same evening: the
page's layout with Steam's checkbox, bare dropdown, text field, tabs and capsules, and a scan of
Xbox and Prism Launcher listing 21 titles grouped by source with their badges and selection marks.
That pass also found the first defect: the review state is far larger than the bridge's 16 KiB cap,
every publication after the scan was refused, and the page sat on "Scanning…" until the toolkit
gained a separate 1 MiB delivery cap. What has not had a live pass: the triggers reaching
`onButtonDown` as codes 7 and 8, the folder picker, the all-artwork and title views with real
images, a launch through each launcher's route, and the follow mode's parenting, recognition and
exit.

## Not in this pass

ROM folders, which come with the emulator installer. Scheduled sync. Steam collections. Renaming an
existing shortcut. Artwork pictures in the overlay.
