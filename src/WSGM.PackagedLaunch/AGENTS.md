# WSGM.PackagedLaunch

This project is the launcher Steam starts for an imported Xbox, UWP or MSIX game. Steam launches it,
Windows activates the game outside Steam's launch tree, and it stays alive for the whole session so
Steam keeps the shortcut running. Its follow mode (`--follow`) does the same for a game another
launcher starts: it starts that launcher outside Steam's tree, recognises the game by its install
folder or, for Java, its instance folder, and stays alive while it runs. See "Following another
launcher's game" in `docs/packaged-game-launcher.md`. It is a sibling of `WSGM.Launch`, not an
extension of it: that wrapper de-elevates and holds input leases for ordinary Steam games and is
required to stay small.

The feature is explicitly experimental. Anti-cheat compatibility is unverified, and no result from
one title generalizes to another. Read the evidence section of `docs/packaged-game-launcher.md` before changing launch
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
- The follow mode never injects and never writes into a process, whatever the launcher. It reads
  image paths, reads a command line only for a Java process, and contains what it recognised. Its
  job lets children break away silently, so nothing it did not recognise, such as a launcher the
  game starts, is ever killed with the game.
- The follow mode starts its program under Explorer or WSGM through the shared
  `WSGM\Interop\ParentProcessStart.cs`, with that parent's user environment. Do not grow a second
  copy of the parent-process start here.
- Nothing polls the whole machine on a timer once the game is established. Lifetime comes from the
  containment job's own active count; discovery slows down as soon as the game appears, and the
  machine is looked at again only while the job is empty, for as long as the exit grace lasts.
- A process is identified by its id and start time, never its id alone.
- The follow command line is read with `GetCommandLineW`. `Environment.CommandLine` is rebuilt from
  the split argument array and loses the verbatim arguments the follow request promises.

## Logging

`%LOCALAPPDATA%\WSGM\packaged-launch.log`, with `launch.log`'s size, rotation and line shape.

Never logged: environment variable values or the environment block, SDDL, window titles, module
lists, and token SIDs beyond a yes/no and an integrity word. Names and counts diagnose a launch; the
rest is user content or an invitation to paste a session token into an issue.

Every refusal names the condition and, where the user can act, the fix. A control that silently does
nothing is a defect.

## Shared source

`Core\PackagedLaunchCommand.cs` is compiled into both WSGM and this project, so the importer and the
launcher cannot drift. `Interop\ParentProcessStart.cs` and `Interop\Win32Common.cs` come from WSGM,
and `SteamControllerExclusion.cs` and `RotatingFileLog.cs` from WSGM.Launch, the same way. That
means two copies of those types exist at runtime, so this project deliberately declares no
`InternalsVisibleTo`: the types worth testing are public, and the public surface uses this project's
own vocabulary rather than the shortcut's.

## Tests

Unit-testable without hardware, Steam or a package, and expected to stay that way: command parsing
and every refusal, route selection over the full matrix, the session exit decision, the
followed-game match rule, the raw command-line cut, and recovery-journal replay. I/O sits behind a
seam: the journal takes a path and a liveness predicate, so its rules are tested without starting
processes.

Attended only, and reported as such: package activation, any remote write, the bridge, real
controller switching, overlay and QAM operation, Alt-Tab recovery, clean exit, and a followed
launcher game's start, recognition and exit. A compiler pass or a loaded DLL is not evidence that
overlay and input work.
