# The packaged-game launcher

How an imported Xbox, UWP or MSIX game gets Steam's overlay and Steam Input, why it needs a launcher
at all, and what the two working routes actually do. The attended evidence behind all of it is in
[Steam overlay and input across launchers](steam-launcher-handoff.md); this page is the shipped
mechanism.

The feature is experimental. Anti-cheat compatibility is unverified, and a result from one title
says nothing about the next.

## Why a launcher exists

Steam launches what a shortcut's Target names and follows its process tree. A packaged game is not
started that way. Windows activates it through the shell, in a process tree Steam never sees, so a
shortcut pointing at the game gets Steam nothing: no overlay, no Steam Input, and a running state
that ends the moment activation returns.

`WSGM.PackagedLaunch.exe` is what the shortcut points at instead. Steam starts it, it asks Windows
to activate the game, and it stays alive for the whole session so Steam keeps reporting the shortcut
as running. It is a sibling of `WSGM.Launch`, not an extension of it: that wrapper de-elevates
ordinary Steam games and holds input leases, and is deliberately kept small.

## The shortcut contract

The [Game Library](game-library.md) writes the shortcut; the launcher reads it. Both compile the
same `Core\PackagedLaunchCommand.cs`, so the two cannot drift.

```text
WSGM.PackagedLaunch.exe --aumid <PackageFamilyName>!<AppId> --mode steam-overlay|controller-only
                        [--multiplayer] [--acknowledge-ban-risk] [--args <text>]
                        [--diagnostics] [--report-privileges] [--recover] [--help]
```

A missing or malformed `--aumid`, an unrecognised `--mode` and any unknown option are refusals, not
defaults. `--multiplayer --mode steam-overlay` without `--acknowledge-ban-risk` is refused outright,
so a shortcut cannot carry an injecting route for a multiplayer title that nobody accepted the risk
for.

There is deliberately no `--runtime`. A package can be updated after its shortcut was written, so
the route is decided from the process activation actually produced, never from the command line.

## Choosing a route

Route selection is one pure function over (mode, is the seed an AppContainer, does the package carry
`MicrosoftGame.config`, multiplayer, acknowledged). Its theory tests assert the two rules that
matter: controller-only never yields an injecting strategy, and no route injects without evidence of
which runtime it is dealing with.

| Seed process                                  | Route              | What it does                                                       |
| --------------------------------------------- | ------------------ | ------------------------------------------------------------------ |
| AppContainer token                            | Native UWP         | Object broker, input bridge, foreground correction                 |
| Full trust, package carries a GDK game config | Packaged Win32/GDK | Steam set up in the launch helper, then Steam's own child handoff  |
| Full trust, no GDK evidence                   | None               | Supervises without injecting, reports the session degraded and why |

An unclassified runtime has no validated route, so there is nothing an acknowledgement could
authorise. It is never offered one.

## The packaged Win32/GDK route

Activation returns `gamelaunchhelper.exe`. Steam's session and components are set up in that helper
immediately, and Steam's own child-process handoff carries the renderer into the real game. In the
successful trial the game already had the renderer at the supervisor's first observation, before
anything had been done to the game process.

This route deliberately does nothing to the game itself. Three other orderings were tried and
recorded as failures: activation alone kept Steam's running state but never reached the renderer;
launching the game executable directly made it replace itself through Gaming Services outside
Steam's tracking; launching the helper directly got the renderer into the helper, which `dllhost`
then replaced without it.

## The native UWP route

An AppContainer cannot open the objects Steam's renderer expects to find, so three things happen
that the Win32 route does not need. A broker outside the container duplicates a fixed allow-list of
Steam's IPC objects in. A bridge routes the WinRT gamepad factory queries a Unity title makes
through Steam, so Steam Input reaches the game rather than the raw device. And foreground
attribution is corrected from the frame window to the game-owned CoreWindow, which is the repair
that made Alt-Tab work.

The foreground correction is the wrapper's own invisible window and writes nothing into the game, so
every AppContainer title gets it, whatever the route and whether or not the bridge set up. It is
also the only window Steam can activate for the wrapper, which is what makes Resume in Steam bring
the game back.

The bridge is the whole route. A missing or failed bridge never falls back to direct renderer
injection: that is the recorded configuration that registered with Steam and produced no overlay and
no input at all, which is worse than a clean refusal because it looks like it worked.

## Controller-only

Controller-only injects nothing, anywhere, under any failure. It is the default for a title the
Store reports as having multiplayer, and the only route offered for one whose runtime could not be
established.

It needs no channel back to WSGM. The importer writes an ordinary per-game profile keyed by the
identity Steam reports for the shortcut, with no process names, so the profile system switches the
managed controller to Xbox 360 for as long as Steam says the shortcut is running. The user sees it
in the Quick Access rows as an override like any other, and can change it there. See
[profiles](profiles.md).

Switching an imported entry back to the overlay route, or removing its shortcut, takes that profile
away again.

## Rules that do not bend

- An uncertain remote write is never retried. One remote operation that misses its budget latches
  all further remote work on that process off for the session.
- `asInvoker` only, never self-elevating, and never composed with `WSGM.Launch --deelevate`: a
  medium-integrity injector cannot open an elevated game. See [elevation](elevation.md).
- Every package-lifetime exemption is journalled before it is requested. Windows keeps a package out
  of lifetime management until something puts it back, so an exemption with no record is a game that
  is never suspended again for the rest of the machine's life. The journal is replayed at launcher
  startup and at WSGM's session start, through `--recover`. Uninstall runs the same sweep first and
  refuses to continue if any record is left, because deleting the journal and the launcher would
  throw away the only thing that could put that package back. The journal refuses a new record once
  it is full rather than write one its reader would never see.
- Every payload a route loads is x64. A route refuses a process that is not native x64 rather than
  writing into it, and the Game Library does not offer the overlay route for a package whose
  identity declares another architecture.
- A journal record stays until its package's release actually succeeds, however many sweeps that
  takes. Releasing is package-wide, so neither a sweep nor a launcher's own exit releases a package
  another running launcher still owns; the last one out does. Deciding that and releasing happen in
  one step under the journal's lock, so two launchers leaving together cannot both leave the package
  exempt, and a sweep cannot release a package a launcher is exempting at that moment.
- A session reports degraded, not complete, when its package could not be exempted or when the
  bridge installed the overlay but not the Steam Input route.
- Containment covers the target package family only, never `RuntimeBroker`, `ApplicationFrameHost`
  or `dllhost`.
- A mid-session failure records once, marks the session degraded and keeps supervising. Foreground
  correction keeps working, because that is the repair that actually worked.

## Logging

`%LOCALAPPDATA%\WSGM\packaged-launch.log`, with `launch.log`'s size, rotation and line shape. Never
logged: environment variable values or the environment block, SDDL, window titles, module lists, and
token SIDs beyond a yes/no and an integrity word. See [logging](logging.md).
