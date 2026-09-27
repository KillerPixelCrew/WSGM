# The packaged-game launcher

How an imported Xbox, UWP or MSIX game gets Steam's overlay and Steam Input, why it needs a launcher
at all, what the two working routes actually do, how the same launcher follows a game another
launcher starts, and the attended evidence all of it rests on.

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

## Following another launcher's game

The Game Library also points titles that start through Epic Games, GOG Galaxy, Ubisoft Connect,
Battle.net, Amazon Games, Prism Launcher or ATLauncher at this launcher, in its follow mode:

```text
WSGM.PackagedLaunch.exe --follow --dir "<install folder>" [--marker "<instance folder>"] -- "<program>" <arguments>
```

The program those launchers are started with usually hands the request to a copy that is already
running and exits at once. Steam reads that as the game stopping, and the shortcut's Steam Input
layout goes with the running state. So the follow mode stays alive instead:

- It starts the program with Explorer, or WSGM when Explorer is not running, as its parent, so a
  launcher that stays in the tray is not in Steam's tree and cannot hold the shortcut running after
  the game exits. When neither can be used it starts the program as its own child and says so.
- It finds the game as any process whose image is inside `--dir`, or, for Minecraft, a Java process
  whose command line names the `--marker` folder, with forward slashes and the 8.3 form both
  recognised. The launcher program itself is never the game.
- It holds the game in its kill-on-close job, so stopping the shortcut in Steam stops the game, and
  exits once the game has been gone for 15 seconds, which covers a bootstrapper handing over.
- It waits up to five minutes for the game to appear, since a launcher may update itself or ask for
  a sign-in first, and exits with "never appeared" after that.

The program's arguments follow `--` verbatim, because a launcher's own can carry quotes:
Battle.net's `--exec="launch Pro"` does. Nothing is injected into a followed game, so Steam's
overlay reaches it only if Steam gets there on its own. `SDL_GAMECONTROLLER_IGNORE_DEVICES` is
removed from the program's environment, as for every child WSGM's launchers start.

For Minecraft, the shortcut starts the launcher with the instance rather than building a Java
command itself: a direct command would carry a Microsoft account token that expires within a day,
and refreshing it would mean WSGM handling the user's sign-in. ATLauncher is started with the
instance's name, which its `--launch` matches together with the safe name (`App.java`), and with
`--close-launcher --no-launcher-update`; Prism Launcher with the instance's folder, which is its id.

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
- The follow mode never injects and never writes into any process. It only starts a program, reads
  process image paths and, for Java alone, command lines, and contains what it recognised.

## Logging

`%LOCALAPPDATA%\WSGM\packaged-launch.log`, with `launch.log`'s size, rotation and line shape. Never
logged: environment variable values or the environment block, SDDL, window titles, module lists, and
token SIDs beyond a yes/no and an integrity word. See [logging](logging.md).

## Evidence: the attended trials of September 2026

The routes above rest on attended Moonlighter, PowerWash Simulator 2 and Balatro trials on September
13 and 14, 2026, the evidence behind the Game Library in
[#47](https://github.com/KillerPixelCrew/WSGM/issues/47) and the launch integration in
[#48](https://github.com/KillerPixelCrew/WSGM/issues/48), both of which shipped on September 22.
Nothing here generalizes past the recorded titles.

### Classify the runtime before choosing a launcher

Xbox is a source of games, not one process model. Both UWP and Win32 titles can be packaged. The
importer must identify the application runtime and launch entry before generating its shortcut. Use
package manifest/application metadata and, where present, `MicrosoftGame.config`; validate uncertain
cases against the activated process's identity, token, and window ownership.

| Runtime              | Observed example        | Demonstrated Steam integration route                                                                                                                    |
| -------------------- | ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| UWP / AppContainer   | Moonlighter             | AAM activation, early renderer injection, desktop IPC object broker, Unity WinRT gamepad activation bridge, and correction to the game-owned CoreWindow |
| Packaged Win32 / GDK | PowerWash Simulator 2   | AAM activation, early Steam environment/client/renderer injection into GameLaunchHelper, then Steam's own child-process handoff to the real game        |
| Ordinary Win32       | Existing launcher games | Preserve Steam's normal launch chain and select the actual game window when returning to it; add a workaround only after identifying a failing boundary |

Do not classify solely by an Xbox install, WindowsApps path, `.exe` extension, or the existence of
package identity. PowerWash has package identity but no AppContainer token and owns its normal Unity
window. Its direct executable launch still replaced the initial process through Gaming Services.
Unknown classification must remain explicit rather than automatically selecting an injection route.

Runtime classification selects the technical launcher. The user's input mode remains a separate
choice: single-player Steam integration or controller-only VIIPER Xbox 360. Controller-only must
never silently enable injection. Neither route carries a general anti-cheat compatibility guarantee.

### Conditions to establish and verify

1. **Keep the Steam session alive.** Steam needs a tracked process for the shortcut's lifetime. The
   persistent wrapper covers a brokered or replaced game process. Running state alone does not
   establish overlay or input integration.
2. **Reach the launch boundary before its game child starts.** A renderer in the first launcher is
   ineffective if that launcher delegates to an uninjected replacement. In PowerWash, injecting the
   helper returned by AAM early let Steam track and inject its subsequent game child. A reported
   parent PID alone is not proof that creation passed through Steam's hooks.
3. **Carry the correct Steam session identity.** The target needs the shortcut's launch environment
   when Steam's renderer initializes. Use the wrapper's actual Steam AppID/game ID. Strip
   `SDL_GAMECONTROLLER_IGNORE_DEVICES` from controlled children; the exclusion can hide Steam's own
   virtual controllers. Do not forward SDL exclusions during remote environment setup.
4. **Verify rendering in the real game.** Record renderer arrival and graphics-hook evidence in the
   process that owns the swap chain. A loaded DLL or a visible Steam UI elsewhere is insufficient.
   PowerWash's renderer was already present at first game observation, before the supervisor's
   delayed game-injection path. Moonlighter required its own rendering/IPC checks.
5. **Ensure shared IPC really is shared.** In an AppContainer, identical object names can resolve to
   private objects or fail token access checks. Moonlighter required a desktop broker for Steam's
   allowed mappings, events, and mutexes, including the UI paint event and correct mutex ownership.
   This bridge was unnecessary in the successful PowerWash trial.
6. **Associate foreground with the actual game window.** Steam Input attribution can change when
   foreground belongs to a console, launcher, or ApplicationFrameHost. Balatro's console
   demonstrated why return-to-game must select the game HWND. Moonlighter needed the game-owned
   CoreWindow, including correction after Alt-Tab. Never raise a game merely because it exists while
   another application is intentionally in front.
7. **Check the input API the game actually uses.** Moonlighter's Unity activation-factory path
   bypassed Steam's emulated WinRT gamepad even when a direct statics query found it. The scoped
   activation bridge corrected that path. PowerWash worked through its XInput path without that
   bridge. Controller enumeration is not proof that game input works.

### Diagnose a launcher game by the first failing boundary

Capture one launch and one foreground transition with correlated timestamps:

- Wrapper, launcher, replacement, and real-game PIDs, creation times, parent PIDs, and exit times.
- Package/AUMID, token integrity/AppContainer state, and the real game HWND/class/owner.
- Steam's `logs/gameprocess_log.txt`: which processes it actually tracks for this shortcut.
- Renderer modules and the corresponding renderer log, distinguishing wrapper from game output.
- Steam's `logs/controller.txt`: `Queueing activation for controller` beside foreground changes.
  AppIDs can be logged as signed 32-bit values; normalize before comparing with shortcut metadata.
- Manual gameplay input, overlay/QAM operation, Alt-Tab away/back, return-to-game, and clean exit.

If Steam loses the running state when a launcher exits, investigate lifetime tracking. If it keeps
the running state but tracks no game, investigate the activation/replacement boundary. If the
renderer is loaded but the overlay cannot draw, investigate graphics hooks and IPC. If Steam's UI
reacts behind the game, correlate foreground attribution and input API routing before assuming
another injection will fix it.

Moonlighter's input recovered when its CoreWindow was raised, without reinjection. Its final trial
passed repeated Alt-Tab. PowerWash's controller input and overlay passed; repeated Alt-Tab has not
been separately confirmed. These results support targeted repairs, not periodic reinjection.

### Remaining limits

The working PowerWash route is not only launch ordering. The launcher forwards remote environment
variables and loads Valve DLLs through `CreateRemoteThread`/`LoadLibraryW` in the helper. No custom
AppContainer bridge is used, but Valve signatures alone do not establish acceptance of this external
loading path by every anti-cheat.

The trial that produced this evidence also performed delayed remote writes and DLL loads in the game
itself, and could not say whether they mattered. The shipped route does not: it sets Steam up in the
helper and does nothing to the game. That is the helper-only simplification this section called for,
and it has not been re-tested attended since it was made.

A protected multiplayer title was discussed on September 14 and has not been tested. Current status
remains: **overlay and input work in the two recorded titles; anti-cheat compatibility is
unverified**. No game or anti-cheat has been selected for such a test.

Record the tested game/build, package runtime, anti-cheat and version if known, Steam version,
launcher build and exact options, and whether delayed descendant setup was enabled. Record launch,
gameplay input, overlay/QAM, Alt-Tab recovery, exit, and any protection-system response separately.
Keep any resulting compatibility finding specific to that configuration and observation window.

The PowerWash trial used both Steam-client preloading and early renderer injection. It does not
establish that every preloaded component is necessary. Additional game engines, Steam updates, and
broader launcher coverage remain unverified. Do not turn the two successful titles into a universal
compatibility claim.

For this investigation, use native process/window observations and file logs. The maintainer
reported Steam failures during CEF investigation and later shortcut-management calls. Do not use CEF
to diagnose these games, and do not edit a shortcut out from under an attended trial.
