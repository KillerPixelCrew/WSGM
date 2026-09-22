# WSGM.PackagedLaunch

This project is the launcher Steam starts for an imported Xbox, UWP or MSIX game. Steam launches it,
Windows activates the game outside Steam's launch tree, and it stays alive for the whole session so
Steam keeps the shortcut running. It is a sibling of `WSGM.Launch`, not an extension of it: that
wrapper de-elevates and holds input leases for ordinary Steam games and is required to stay small.

The feature is explicitly experimental. Anti-cheat compatibility is unverified, and no result from
one title generalizes to another. Read `docs/steam-launcher-handoff.md` before changing launch
behavior.

## Invariants

- Controller-only never injects, under any failure and for any runtime. The route selector is a pure
  function and its theory tests prove this; do not add a path around it.
- A runtime that could not be established never injects either. There is no validated route for a
  title that is neither an AppContainer nor a packaged Win32 game, and guessing one means writing
  into somebody's game on the strength of a guess. The session still runs and reports degraded.
- The route is decided from the activated process, never from the command line. A package can be
  updated after its shortcut was written, so the shortcut carries no runtime argument at all.
- An uncertain remote write is never retried. One remote operation that misses its budget latches
  all further remote work on that process off for the session.
- A missing or failed overlay bridge must not fall back to direct renderer injection. That is the
  recorded 23:11 configuration: it registered with Steam and produced no overlay and no input.
- `asInvoker` only. Never `requireAdministrator`, never self-elevate, and never compose this with
  `WSGM.Launch --deelevate`: a medium-integrity injector cannot open an elevated game.
- Every package-lifetime exemption is journalled before it is requested. Windows keeps a package out
  of lifetime management until something puts it back, so an exemption with no record is a game that
  is never suspended again for the rest of the machine's life.
- Nothing polls the whole machine on a timer once the game is established. Lifetime comes from the
  containment job's own active count; discovery slows down as soon as the game appears.

## Logging

`%LOCALAPPDATA%\WSGM\packaged-launch.log`, with `launch.log`'s size, rotation and line shape.

Never logged: environment variable values or the environment block, SDDL, window titles, module
lists, and token SIDs beyond a yes/no and an integrity word. Names and counts diagnose a launch; the
rest is user content or an invitation to paste a session token into an issue.

Every refusal names the condition and, where the user can act, the fix. A control that silently does
nothing is a defect.

## Shared source

`Core\PackagedLaunchCommand.cs` is compiled into both WSGM and this project, so the importer and the
launcher cannot drift. That means two copies of those types exist at runtime, so this project
deliberately declares no `InternalsVisibleTo`: the types worth testing are public, and the public
surface uses this project's own vocabulary rather than the shortcut's.

## Tests

Unit-testable without hardware, Steam or a package, and expected to stay that way: command parsing
and every refusal, route selection over the full matrix, the session exit decision, and
recovery-journal replay. I/O sits behind a seam — the journal takes a path and a liveness predicate,
so its rules are tested without starting processes.

Attended only, and reported as such: package activation, any remote write, the bridge, real
controller switching, overlay and QAM operation, Alt-Tab recovery, and clean exit. A compiler pass
or a loaded DLL is not evidence that overlay and input work.
