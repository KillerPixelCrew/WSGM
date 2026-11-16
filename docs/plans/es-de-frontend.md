# ES-DE as a managed frontend

Status: implemented 2026-11-17, awaiting the maintainer's attended check. The shipped behavior is in
[Game Library](../game-library.md#es-de) and the decision in [decisions](../decisions.md).

## How it ended up

ES-DE starts emulators directly with WSGM-written commands; WSGM's launcher and the Steam Input
lease apply only to ES-DE's own shortcut. `WSGM.PackagedLaunch` is unchanged. An earlier draft
routed every ES-DE launch through a new launcher mode with a WSGM-written index; it was dropped as
more machinery than the feature needs.

Differences from the first draft:

- No automatic "Remove N games from Steam". Turning a folder off in ES-DE's page is Library
  Importer's source tick; games already imported are removed per title in Library Importer.
- No "Open ES-DE" command in the Overlay; ES-DE starts from its Steam shortcut, which carries the
  launch mode and lease.
- The links folder is `Emulation\es-de-roms` under the user's data root, outside every folder WSGM
  copies or snapshots.

## To confirm on a real install

- First start after install shows the games, not ES-DE's "no game files" dialog, with an
  `es_settings.xml` that held only `ROMDirectory`.
- ES-DE scans through the folder links (symbolic links when WSGM may create them, junctions
  otherwise) and lists systems from several folders.
- `%STARTDIR%="…"` with a quoted path, `%ROMRAW%`/`%GAMEDIRRAW%` inside a quoted argument and
  `%BASENAME%` behave as ES-DE's documentation describes, including a user folder with a space.
- A game started from ES-DE gets the controller and Steam's overlay through ES-DE's shortcut, and
  returns to ES-DE on exit.
- A RetroArch game started from ES-DE uses WSGM's `retroarch.cfg`, saves and BIOS folder.
- An SD-card ROM folder remounted under another letter is relinked by the next sync.
- An emulator update followed by an ES-DE restart starts games with the new version.

## Later, only if asked for

- PS3, Vita and PS4 through one link per detected game (`<Title>.ps3` folders ES-DE treats as
  games).
- WSGM's chosen artwork exported into ES-DE's media folder.
- Taking over an ES-DE someone installed themselves, which would replace its ROM folder.
