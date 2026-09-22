# Importing an Xbox library into Steam

How installed Xbox, UWP and MSIX games become Steam shortcuts: what the scan looks at, what decides
each title's launch route, and the rules that keep a second run from duplicating or destroying what
the first one wrote.

Xbox is the only source. The interface behind it exists because ROM, folder and launcher sources are
specified to follow; if they are dropped, it should be deleted rather than kept as scaffolding.

## Getting to it

Right Quick Access Menu, the plugin tab, the "Game Library" row, "Import games…". That opens a full
page built from native Big Picture components, the same way the artwork changer's page is, so it
works on a controller from Game Mode without a desktop.

The row is WSGM's own and is not conditional on any plugin being installed. Its ids carry a reserved
prefix, so a package can neither answer for it nor displace it by choosing the same id.

## Scanning writes nothing

A scan discovers, classifies, matches against Steam's current shortcuts and against WSGM's own
records, and publishes a plan. The dry run is the default rather than a mode. Nothing is written
until the user applies.

Discovery enumerates the packages installed for the current user, skipping frameworks, resource
packages, bundles, non-Store signatures and anything whose status is not OK, and catching per
package so one bad package cannot fail the scan. The AUMID and the display name come from the
package's own app list entry rather than being composed or resolved by hand. Windows' own
publishers, and the Xbox plumbing that ships beside every title, are filtered out.

## Classifying the runtime

Three answers, each with a one-sentence reason the page shows.

| Verdict            | Evidence                                                                            |
| ------------------ | ----------------------------------------------------------------------------------- |
| Packaged Win32/GDK | A full-trust entry point, a GDK signal, and exactly one application in the manifest |
| Native UWP         | A WinRT entry point, no full-trust declaration, no game config, one application     |
| Unknown            | Everything else, with the specific case named                                       |

Unknown is a first-class answer, not a fallback. An unknown runtime has no validated launch route,
so it is listed, marked, and left unselectable rather than guessed at.

The manifest and game-config parsers are pure, bounded, and refuse DTDs and external entities. They
match by local name because manifest namespaces are versioned. The game-config parser keeps the
elements it does not model and surfaces them on the page, so the real schema is learned from
installed titles before any rule depends on more of it.

## "Is it a game?", and the multiplayer tag

Nothing in a UWP manifest says a title is a game. GDK evidence answers it on its own; for everything
else one lookup against Microsoft's public Store display catalog by package family name answers it,
along with the multiplayer capabilities the store page renders as "Online multiplayer" and the
official images. No sign-in, one request every 500 ms, and a title neither source can vouch for is
listed as an ordinary application, unselected.

A title the catalog reports as having multiplayer defaults to controller-only with no injection. The
user can move it to the overlay route only by accepting the ban risk explicitly, and that
acknowledgement is enforced in the backend, not the page: a page defect must not be able to put a
multiplayer title on the route that injects into it. A title with no multiplayer answer at all
defaults to Steam integration.

## Writing the shortcut

Shortcuts are written through the running Steam client, one at a time. There is no offline
`shortcuts.vdf` editing: if Steam is not running, the import refuses.

The Target is `WSGM.PackagedLaunch.exe` beside the running WSGM, and the identity travels in the
arguments, because a non-Steam shortcut ignores an exe-replacing launch option. The start directory
is the launcher's own, not the ACL'd `WindowsApps` path. A missing launcher refuses the whole apply.

A new app id is confirmed by two sources — what the add call returned, and a before/after diff of
Steam's own library — and **the diff is the authority**. Disagreement, or a diff that shows anything
other than exactly one new entry, records the entry as unconfirmed and stops the run. That write is
never retried: it may well have succeeded, and asking again is how a duplicate is created.

Identity is the AUMID, never the name. A second run yields add, skip, update, adopt, conflict or
remove:

- **Update** rewrites the launch fields in place. It never removes and re-adds, which would lose the
  id and the artwork attached to it.
- **Conflict** means the Target is no longer ours. It is never touched.
- **Remove** requires the record, the live entry, our Target and our AUMID to all agree, and is
  never pre-selected.

Applies are capped per run, each entry is re-matched immediately before its own write, and a failure
stops and reports how far it got. It does not roll back: removing a batch of somebody's shortcuts
over one failed write is the worse outcome.

Records live in `%LOCALAPPDATA%\WSGM\library-import.json` and keep the Target and launch options
verbatim, so "has the user edited this?" is an exact question rather than a guess.

## After the write

The catalog images from the one lookup that already happened are applied to the confirmed id, mapped
to the capsule whose shape each actually fills; an unmapped purpose is dropped rather than stretched
into the wrong slot. An image that will not download costs that capsule, not the import. SteamGridDB
remains available afterwards through "Change Artwork…" on the game's own menu.

A controller-only entry also gets its per-game profile written. See
[the packaged-game launcher](packaged-game-launcher.md#controller-only).

## Not in this pass

Every source except Xbox. Name-based catalog search. Renaming an existing shortcut. Steam
collections. Automatic or scheduled sync. Offline `shortcuts.vdf` editing as a fallback. Anti-cheat
detection and a protected-title denylist.
