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
  shortcuts folder the user added is listed under Custom. "Add folder…" opens the "Add a shortcuts
  folder" sheet: the folder, chosen in a native folder picker, whether its subfolders are read too,
  and which file types it offers. Unticking a source takes its titles out of the review at once,
  leaves it out of the next scan and never offers its imported titles for removal.
- **Collections.** A Steam checkbox under the sources keeps one Steam collection per launcher and
  per shortcuts folder, named after it and holding the titles WSGM imported from it. Turning it on
  brings titles imported earlier in at once; turning it off leaves the collections as they are.
- **Review.** A poster grid grouped by source, with tabs for All, New, Imported, Needs attention and
  Left out, a search, and one artwork type shown at a time (portrait, wide, hero, logo, icon). Each
  card shows the image that will be applied, a selection check, what a sync would do, and how the
  title launches. LT and RT cycle the focused card's image in place, A selects, X switches the
  launch mode or route, Y opens the title's artwork and Menu its details. A on a card that cannot be
  ticked says why. "Select all" takes what the tab and search show, never a removal, an add Steam
  may already have, or a title the user deleted from Steam; those are ticked one at a time.
- **One title's artwork** (Y on a card): every candidate for each artwork type, grouped by provider,
  with "Fix match" to search again under any name. Its results list SteamGridDB's and
  Screenscraper's games together, grouped by provider, and the user picks one.
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

| Source           | Id           | Detected by                                                                                                | Titles from                                                                                                                                                                      | Launch routes, default first                                                                |
| ---------------- | ------------ | ---------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Xbox             | `xbox`       | always                                                                                                     | Store-installed packages other than Windows' own, classified by runtime; the Store catalog, asked once per package family, for game, multiplayer and images                      | the packaged launcher, in Steam-integration or controller-only mode                         |
| Epic Games       | `epic`       | the launcher's uninstall entry or its manifests folder                                                     | `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`; DLC, engine parts, `UE_` engine installs and installs still downloading dropped                                    | through the Epic launcher; the game's own executable                                        |
| GOG Galaxy       | `gog`        | `GOG.com\GalaxyClient\paths`, or a GOG game's uninstall entry                                              | `<id>_is1` uninstall entries by GOG.com and their `goggame-<id>.info` primary play task                                                                                          | the game's executable; through Galaxy                                                       |
| Ubisoft Connect  | `ubisoft`    | the launcher's uninstall entry                                                                             | `ubisoft\Launcher\Installs\<id>` and the launcher's configuration cache for names, executables and working folders; the scan fails when there are installs and no readable cache | the game's executable; through Ubisoft Connect                                              |
| Battle.net       | `battlenet`  | the uninstall entry naming `--uid=battle.net`, or a classic game's entry                                   | uninstall entries with a Battle.net uid and the agent's `product.db`, mapped to product codes; the classic Diablo II and Warcraft III entries                                    | through Battle.net; the classic games from their own executable, with or without Battle.net |
| itch             | `itch`       | `%AppData%\itch\db\butler.db` or the app's uninstall entry                                                 | the butler database's caves and their verdict executables, resolved against the cave's current install folder                                                                    | the game's executable only                                                                  |
| Amazon Games     | `amazon`     | the app's uninstall entry, or `%LocalAppData%\Amazon Games\App\Amazon Games.exe`                           | `GameInstallInfo.sqlite` installed rows and each game's `fuel.json`                                                                                                              | through Amazon Games when the game needs its sign-in; otherwise the executable first        |
| Prism Launcher   | `prism`      | its uninstall entry, or `prismlauncher.exe` in `%LocalAppData%\Programs\PrismLauncher`                     | instance folders in its data folder, beside the executable when portable, else `%AppData%\PrismLauncher`, or where `InstanceDir` points, and their `instance.cfg` names          | `prismlauncher.exe --launch <instance>`, following the instance's Java process              |
| ATLauncher       | `atlauncher` | its uninstall entry, or `ATLauncher.exe` in `%AppData%\ATLauncher` or `%LocalAppData%\Programs\ATLauncher` | instance folders beside the executable or in `%AppData%\ATLauncher`, and their `instance.json` names                                                                             | `ATLauncher.exe --launch <instance>`, following the instance's Java process                 |
| Shortcuts folder | `folder:<n>` | the folder exists                                                                                          | `.lnk`, `.url` and `.exe` files, optionally in subfolders, leaving out anything Steam already runs                                                                               | the shortcut's own target and arguments; a `.url` through its protocol's handler            |

The detection rules for every launcher except Prism Launcher and ATLauncher follow Playnite's
library plugins and Steam ROM Manager's parsers as those projects publish them. Neither project is
vendored under `_ref`, so this repository holds no copy to compare against; the per-source remarks
in `Core\Library\Sources\` record what each rule was taken from. Prism Launcher and ATLauncher,
which neither covers, follow their own instance layouts and command lines. A ROM folder source comes
with the emulator installer and is not part of the Game Library on its own. Amazon Games and itch
keep their installs in SQLite databases, which are copied and read, never opened in place; a
database that cannot be read fails that source's scan rather than listing nothing.

Each detection and each scan reads Windows' installed-programs list once and hands the same list to
every launcher source, so nothing is cached between scans and an install shows at the next one.

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
| Artwork stage    | `Shell\GameLibraryArtwork.cs`                                                                                  | Candidates per title and artwork type, gathered in the background after a scan, and each title's match and status. It only gathers.                                                                                        |
| Artwork core     | `Core\Artwork\`: `ArtworkSearch`, the providers, `ArtworkRequestGate`, `ArtworkDownload`, `SteamArtwork`       | The automatic match and the merged search, each provider's pacing and memory, downloads, and applying an image to Steam by the format its bytes declare. Shared with the artwork page.                                     |
| Service          | `Shell\GameLibraryService.cs`                                                                                  | Scanning, choices, picks, cycling and bulk fills, applying, the controller override; every label both surfaces show; publishes the state both surfaces render.                                                             |
| Steam page       | `Shell\SteamLibraryImportSurface.cs`, `Core\SteamUiAssets\Source\library-import.ts`                            | Surface one: route `/wsgm/library-import`.                                                                                                                                                                                 |
| Toolkit          | `external\steam-ui-toolkit`                                                                                    | The page gate and modal frame, the capsule drawn with Steam's own library classes, a folder and file picker Steam does not have, the confirmed shortcut add, the one-call shortcut read, and state delivered in parts.     |
| Overlay view     | `Overlay\GameLibraryView.cs`                                                                                   | Surface two: sources, review and apply, one level at a time.                                                                                                                                                               |
| Settings         | `AppConfig.GameLibrary`, Settings → Steam                                                                      | The mode new single-player Xbox titles start on, whether titles with no launch route are offered, which artwork provider a title starts on, the disabled sources and the shortcuts folders.                                |

## Identity

A title is the pair of its source and its key, never its display name. The key is the AUMID for
Xbox, the launcher's own id elsewhere, and the file's path relative to the folder for a shortcuts
folder. Records and choices are matched on the pair everywhere. A source id is never reused.

The id a surface addresses a title by is derived from that pair, so the same title keeps it across
scans. An action a page sends after a rescan reaches the title it showed, never whichever title now
sits in the same place, and the user's selection survives the rescan.

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
with the multiplayer capabilities and the official images. The source reports a package neither can
vouch for as not a game, and the plan leaves it out unless WSGM already imported it: every installed
Store application carries the same package identity a game does, and the first live review offered
Paint and Clipchamp beside the games. The rule sits in the plan because only the plan sees the
records. An imported title whose Store lookup failed, offline for instance, is still handled like
any imported title instead of being offered for removal as uninstalled. Launcher sources drop DLC,
engine parts and tools the way their references do, and what they list is a game.

A multiplayer Xbox title starts controller-only. The overlay route is available only by accepting
the ban risk explicitly, enforced in the backend rather than the page. A title with no multiplayer
answer starts on the mode Settings names, the Steam overlay by default.

## The user's choices

A scan rebuilds every entry from what the sources and Steam say now, and then lays the user's stored
choices over it. A launch mode or route picked and not yet applied, an acknowledgement, "don't
import", artwork picks and a fixed match all survive a rescan. A stored choice is judged again each
time by the same rule the command uses, so a title that has since become multiplayer does not keep
an overlay route nobody accepted the risk for, and a route the source no longer offers is dropped.
Once an apply writes a record, everything in that title's choice that Steam now holds is dropped in
the same write: its mode, route and applied picks. A fixed match stays, because Steam keeps no trace
of which game the artwork came from. The choices of titles a fully read source no longer lists are
pruned.

Only a title that is not imported yet can be left out. An imported title leaves Steam by being
removed, which is a separate, deliberate act. A source's imported titles are offered for removal
only when the source was read in full:

- **Unticked, or failed to read:** neither listed nor offered. Turning a launcher off is not a
  request to delete its games, and a launcher that could not be read says nothing about them.
- **Reporting no titles at all** while titles were imported from it: not offered, with a notice. A
  launcher read wrongly is far likelier than every game uninstalled at once.
- **No longer on this machine**, an uninstalled launcher or a removed folder: its imported titles
  are offered for removal, never ticked, so they can still be taken out of Steam.

The records file is the only memory of which shortcuts are WSGM's, so a read that fails is never
taken for an empty file. A file that cannot be opened right now fails the operation and is read
again next time; one that does not parse is kept beside it as `library-import.json.corrupt-<time>`
before an empty state replaces it; a file written by a newer WSGM is left alone. A write is flushed
to disk before it replaces the file, and a write that fails stops the run.

## Artwork

After a scan, the artwork stage gathers candidates for every listed title in the background, three
titles at a time with each title's five lookups sent together, starting with the ones a surface asks
for. The candidates are the source's catalog images (the Store, for Xbox) and the provider results
for the title's match. Only static, non-adult images are asked for. A title's candidates arrive in
one update, not one per artwork type.

There are two rules for asking the providers, one for each question:

- **The automatic match**, with nobody looking, asks them in preference order
  (`ArtworkSearch.FindMatchAsync`). SteamGridDB knows Steam libraries and is asked first.
  Screenscraper, a ROM database, is asked only when SteamGridDB does not know the title, or its game
  there has no images. The match is that provider's first exact result for the title's name, else
  its first result. A provider that could not be asked (a rejected key, a spent quota, no
  connection) stops the match there and marks the title failed with the provider's reason; it never
  falls through to a provider that might pin a wrong game.
- **"Fix match"**, with the user choosing, searches every ready provider at once and lists their
  games together, the way the artwork page does. The game the user picks is kept as a choice.

A title's artwork status is ready, not found (every provider was asked and none had images), failed
(with the reason), or unavailable (no provider is set up, with what to do). Entering or correcting a
key or an account, or turning Screenscraper on, asks again for every title that failed, found
nothing or had no provider; any other settings change asks nothing again, because a miss costs
Screenscraper's daily allowance.

Each provider paces its own requests behind one gate (`ArtworkRequestGate`): SteamGridDB four at a
time, backing off on a 429, and Screenscraper one, since a free account, registered or not, is
allowed one thread. A page a provider answered is remembered for the session, checked both before
and behind the gate, and a failure is remembered for half a minute so the lookups queued behind it
do not each wait out the timeout. The background gathering waits behind every request a person is
waiting on, so the artwork page and "Fix match" are answered first. Screenscraper's media URLs carry
the request's credentials; the user's account is taken out of every URL before it is shown, stored
or logged, and put back only for the download, which goes through the same gate.

Each title starts on the first candidate from the provider Settings prefers, per artwork type. A
title Steam already has starts on "keep current" and changes nothing until the user picks an image.
Cycling, picking, clearing and the bulk fills change picks only; nothing is downloaded until Save to
Steam. "Fill empty slots" leaves a slot alone that already shows an image, a default or the artwork
Steam has. On save, a title's picks are downloaded together, each through the provider that serves
it, and applied one at a time to the confirmed shortcut through the same path the artwork page uses
(`SteamArtwork.ApplyManyFromUrlsAsync`). The image's format is read from its own bytes, never from
its URL; an image that is not PNG, JPEG, WEBP or ICO is refused. A pick that fails to download or
apply is reported and does not stop the run.

## Writing to Steam

Shortcuts are written through the running Steam client, one at a time. There is no offline
`shortcuts.vdf` editing; if Steam is not running the apply refuses. For an Xbox title the Target is
`WSGM.PackagedLaunch.exe` beside the running WSGM and the key travels in the arguments, because a
non-Steam shortcut ignores an exe-replacing launch option. A missing launcher refuses the Xbox
titles in an apply; other sources do not need it.

The toolkit's `SteamApps` makes every change one at a time, whoever asks, and confirms a new app id
by a before/after diff of Steam's library taken in the same call as the add: **the diff is the
authority**, and the fields are set on the entry the library gained and read back. WSGM's writer
(`SteamShortcutWriter`) holds the policy over it. An add that is not confirmed is recorded as
unconfirmed and stops the run; it is never retried, because it may well have succeeded and asking
again is how a duplicate is made. If a later scan still finds no such shortcut, the title is offered
as an Add that says so and is never ticked for the user: only they can look at the library and
decide. A field Steam did not keep as written is reported when the run ends. Once a write has been
sent, Stop waits for it to be recorded, so the next scan does not mistake WSGM's own change for a
hand edit.

- **Update** rewrites the Target, start directory and arguments in place, never remove-and-re-add,
  which would lose the id and its artwork. It happens because the user changed an imported or
  adopted title's mode or route, or because what WSGM would write for an untouched shortcut has
  changed: WSGM moved folders, or the source reports a different install. A title whose imported
  route is gone is offered its first route, unticked. An imported title with new artwork picks and
  nothing else changed is an artwork update: no shortcut call at all.
- **Adopt** takes over an unrecorded entry as what it currently launches, and records its live
  fields. It writes nothing, and so never touches a controller profile that may be the user's own.
  For a command route that means a shortcut that runs exactly one of the title's routes. A shortcut
  another title's record names is never adopted.
- **Conflict** means the entry was edited by hand: its Target or arguments are no longer exactly
  what was recorded. It is never touched, because restoring the recorded command would silently undo
  the user's own change.
- **Remove** needs the live entry's Target and arguments to be exactly the recorded ones, and is
  never pre-selected. A record whose shortcut is already gone is cleaned up without a client call.
- **A deleted shortcut:** a title whose recorded shortcut the user deleted from Steam is offered
  again as an Add, unticked; adding it releases the controller override the old id left behind.

While an apply runs the list is read-only. Each applied title is deselected and shows as imported
from then on, so when more are selected than one run takes, the next apply carries on with the rest.
Problems that do not stop the run - a controller override that could not be written, a
controller-only title applied while controller management is off, images that did not apply, a field
Steam did not keep - are reported together as an error when the run ends. An update or an adoption
keeps the artwork the entry already has unless the user picked something.

Steam's library is read once per run, in one call, and each entry's own shortcut is read again
immediately before its write, keeping the user's chosen action and rechecking only its premise: a
launch option the user edited since the scan is not overwritten. Applies are capped per run; a
failure stops and reports how far it got, without rolling back. A library that cannot be read whole
stops a scan rather than being read as "not ours", which would add a duplicate.

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
every publication after the scan was refused, and the page sat on "Scanning…". The bridge now
delivers a large state in parts the page reassembles, up to 32 MiB, and a state it still refuses
reaches the page as a visible refusal; the review publishes only what the cards draw, and the
evidence behind a title is asked for when its details open. What has not had a live pass: the
triggers reaching `onButtonDown` as codes 7 and 8, the folder picker and the folder sheet, the
all-artwork and title views with real images, the merged "Fix match" list, a launch through each
launcher's route, and the follow mode's parenting, recognition and exit.

## Steam collections

With the switch on, each run ends by bringing one user collection per source in step, through the
toolkit's `SteamCollections` over Steam's own `collectionStore`; it is what Steam shows as a library
category and syncs through the Steam Cloud. The import records keep each collection WSGM made by
Steam's id, never by its name, with the apps the last sync put in. A sync adds the source's
confirmed titles, takes back only the titles WSGM put in and no longer imports, and deletes a
collection left empty. A collection the user made with the same name stays theirs, one WSGM made
keeps its id when renamed, and a title the user dropped into it stays. An unticked source's
collection is left alone. A collection that cannot be updated is reported when the run ends and the
shortcuts stay as written. The group is the source; a ROM source groups by system when it arrives.

## Not in this pass

ROM folders, which come with the emulator installer, and so collections per emulated system.
Scheduled sync. Renaming an existing shortcut. Artwork pictures in the overlay.
