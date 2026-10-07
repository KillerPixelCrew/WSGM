# The packaged-game launcher

How an imported Xbox, UWP or MSIX game gets Steam's overlay and Steam Input, why it needs a launcher
at all, what the two working routes do, how the same launcher follows a game another launcher
starts, and the attended evidence all of it rests on.

The feature is experimental. Anti-cheat compatibility is unverified, and a result from one title
says nothing about the next.

## Why a launcher exists

Steam launches what a shortcut's Target names and follows its process tree. A packaged game is not
started that way. Windows activates it through the shell, in a process tree Steam never sees, so a
shortcut pointing at the game gets Steam nothing: no overlay, no Steam Input, and a running state
that ends the moment activation returns.

`WSGM.PackagedLaunch.exe` is what the shortcut points at instead. Steam starts it, it asks Windows
to activate the game, and it stays alive for the whole session so Steam keeps reporting the shortcut
as running. It is a sibling of `WSGM.Launch`, not an extension of it. That wrapper de-elevates
ordinary Steam games and holds input leases, and is deliberately kept small.

## The shortcut contract

The [Game Library](game-library.md) writes the shortcut; the launcher reads it. Both compile the
same `src\Shared\Launch\PackagedLaunchCommand.cs`, so the two cannot drift.

```text
WSGM.PackagedLaunch.exe --aumid <PackageFamilyName>!<AppId> --mode steam-overlay|controller-only
                        [--multiplayer] [--acknowledge-ban-risk] [--args <text>]
                        [--diagnostics] [--report-privileges]
WSGM.PackagedLaunch.exe --recover
WSGM.PackagedLaunch.exe --help
```

A missing or malformed `--aumid`, an unrecognised `--mode` and any unknown option are refusals, not
defaults. `--multiplayer --mode steam-overlay` without `--acknowledge-ban-risk` is refused outright,
so a shortcut cannot carry an injecting route for a multiplayer title that nobody accepted the risk
for.

There is deliberately no `--runtime`. A package can be updated after its shortcut was written, so
the route is decided from the process activation produces, never from the command line.

## Choosing a route

Admission, classification and selection are separate steps. The shared command parser refuses a
multiplayer overlay request without acknowledgement. After activation, `PackageIdentity.Classify`
turns the seed process's AppContainer status and GDK helper/package evidence into `PackagedRuntime`.
The pure `LaunchRouteSelector.Select` then takes exactly two inputs: requested input mode and
classified runtime. Its theory tests cover the two invariants: controller-only never yields an
injecting strategy, and an unknown runtime never injects.

| Seed process                                                            | Route              | What it does                                                       |
| ----------------------------------------------------------------------- | ------------------ | ------------------------------------------------------------------ |
| AppContainer token                                                      | Native UWP         | Object broker, input bridge, foreground correction                 |
| Full trust, `gamelaunchhelper.exe` or package carries a GDK game config | Packaged Win32/GDK | Steam set up in the launch helper, then Steam's own child handoff  |
| Full trust, no GDK evidence                                             | None               | Supervises without injecting, reports the session degraded and why |

An unclassified runtime has no validated route, so there is nothing an acknowledgement could
authorise. It is never offered one.

## The packaged Win32/GDK route

Activation returns `gamelaunchhelper.exe`. Steam's session and components are set up in that helper
at once, and Steam's own child-process handoff carries the renderer into the real game. In the
successful trial the game already had the renderer at the supervisor's first observation, before
anything had been done to the game process.

This route does nothing to the game itself, on purpose. Three other orderings were tried and
recorded as failures. Activation alone kept Steam's running state but never reached the renderer.
Launching the game executable directly made it replace itself through Gaming Services outside
Steam's tracking. Launching the helper directly got the renderer into the helper, which `dllhost`
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
injection. That is the recorded configuration that registered with Steam and produced no overlay and
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
WSGM.PackagedLaunch.exe --follow [--dir "<install folder>"] [--marker "<instance folder>"] -- "<program>" <arguments>
```

At least one of `--dir` and `--marker` is required, and neither may be a drive root: every process
on the drive would count as the game and be killed with it. Both are written without a trailing
backslash, because under Windows' argument rules a backslash before the closing quote escapes it and
swallows the rest of the line. The composer trims them once for every source, and a shortcut written
before that is read back in the same form.

The program those launchers are started with usually hands the request to a copy that is already
running and exits at once. Steam reads that as the game stopping, and the shortcut's Steam Input
layout goes with the running state. So the follow mode stays alive instead:

- It starts the program with Explorer, or WSGM when Explorer is not running, as its parent, and with
  that parent's own user environment, so a launcher that stays in the tray is not in Steam's tree
  and does not hand Steam's launch variables to the games it starts later. This is the same
  parent-process start WSGM uses for its shell work (`src\Shared\Process\ParentProcessStart.cs`,
  linked into the launcher). When neither parent can be used it starts the program as its own child,
  with `SDL_GAMECONTROLLER_IGNORE_DEVICES` removed as for every child WSGM's launchers start, and
  says so.
- It finds the game as any process whose image is inside `--dir`, or, for Minecraft, a Java process
  whose command line names the `--marker` folder as a whole path: `instances\Pack` never matches
  `instances\Pack 2`. Forward slashes, the 8.3 form and the folder's real location behind a junction
  are all recognised. The launcher program itself is never the game. Only Java's `java.exe` and
  `javaw.exe` are read for a marker, because whatever matches is contained and killed with the game,
  and an editor or file manager opened on the instance folder must never be.
- It holds the game in its kill-on-close job, so stopping the shortcut in Steam stops the game. Only
  what it recognised goes in: a launcher the game starts, such as Ubisoft Connect from an Epic
  title, leaves the job, so it neither keeps the session alive nor dies with the game.
- It exits once the game has been gone for 15 seconds, which covers a bootstrapper handing over.
  While the job is empty it looks at the machine again, so a game that restarts itself outside the
  contained tree, as Epic's online services and Battle.net's patcher do, is found and contained
  rather than taken for an exit.
- It waits up to five minutes for the game to appear, since a launcher may update itself or ask for
  a sign-in first, and exits with "never appeared" after that. It stops waiting at once when the
  program it started failed with a non-zero exit code and no copy of it is running, since nothing is
  left to start the game. A zero exit is the ordinary handoff to a resident copy and ends nothing.

The program's arguments follow `--` and reach the program exactly as written, because a launcher's
own can carry quotes: Battle.net's `--exec="launch Pro"` does. That holds because the launcher reads
its own raw command line (`GetCommandLineW`), not the argument array .NET rebuilds from what Windows
split. Nothing is injected into a followed game, so Steam's overlay reaches it only if Steam gets
there on its own.

Discovery reads each process once. A process keeps its verdict while it stays in the snapshots under
the same id, parent and name, so a poll opens no handle to a process already judged, and the memory
is pruned to what is running. It polls every half second until the game appears and every two
seconds while establishing a followed session. Once its discovery window ends and the job has active
processes, only the job count is checked. Machine discovery resumes at half-second intervals while
the job is empty, including the exit grace. A process is known by its id and start time, so a new
game process that reuses a finished one's id is still contained.

For Minecraft, the shortcut starts the launcher with the instance rather than building a Java
command itself. A direct command would carry a Microsoft account token that expires within a day,
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
- A journal record stays until its package's release succeeds, however many sweeps that takes.
  Releasing is package-wide, so neither a sweep nor a launcher's own exit releases a package another
  running launcher still owns; the last one out does. Deciding that and releasing happen in one step
  under the journal's lock, so two launchers leaving together cannot both leave the package exempt,
  and a sweep cannot release a package a launcher is exempting at that moment.
- A session reports degraded, not complete, when its package could not be exempted or when the
  bridge installed the overlay but not the Steam Input route.
- Containment covers the target package family only, never `RuntimeBroker`, `ApplicationFrameHost`
  or `dllhost`.
- A mid-session failure records once, marks the session degraded and keeps supervising. Foreground
  correction keeps working, because that is the repair that worked.
- The follow mode never injects and never writes into any process. It only starts a program, reads
  process image paths and, for Java alone, command lines, and contains what it recognised. Nothing
  else ever joins its job.

## Logging

`%LOCALAPPDATA%\WSGM\packaged-launch.log`, with `launch.log`'s size, rotation and line shape. Never
logged: environment variable values or the environment block, SDDL, window titles, module lists, and
token SIDs beyond a yes/no and an integrity word. See [logging](logging.md).

## Session timing and exit contract

`GameSessionSupervisor` supplies observations to the pure `GameSessionExitDecision`. A packaged
title has 90 seconds to appear, a 30-second discovery window after the first match and a 5-second
empty-session grace. A followed title has five minutes to appear, a three-minute discovery window
and a 15-second exit grace. An empty containment job is not proof of exit: assignment can be refused
by an existing Windows job, or the game can restart outside the previous tree, so the supervisor
rechecks process identity before ending Steam's running state.

The job is kill-on-close during ordinary ownership and abrupt wrapper termination. Cooperative
cancellation deliberately calls `Abandon` before disposing it, leaving the game running and
returning the cancelled outcome. A forced process termination cannot run that release path. This
distinction applies to both packaged and followed sessions.

| Exit code | Meaning                                                                                      |
| --------- | -------------------------------------------------------------------------------------------- |
| `0`       | Game completed, help printed, or recovery finished with an empty journal                     |
| `1`       | No recognized game appeared, including a followed launcher that failed with no resident copy |
| `2`       | Invalid/refused command line                                                                 |
| `3`       | Package activation or followed-program start failed                                          |
| `4`       | Reserved refusal constant; the current entry point does not return it                        |
| `5`       | The game ran, but requested integration was degraded                                         |
| `6`       | Cooperative stop requested; the job was released before exit                                 |
| `7`       | `--recover` left package-exemption records, including records still owned by a live launcher |

## Source ownership

| Source                                                                                                                                                                                                                                                                        | Responsibility                                                                                              |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| [`Program.cs`](../src/WSGM.PackagedLaunch/Program.cs)                                                                                                                                                                                                                         | STA composition: parse, recover, activate, classify, choose route, supervise and unwind                     |
| [`Shared/Launch/PackagedLaunchCommand.cs`](../src/Shared/Launch/PackagedLaunchCommand.cs) and [`RawCommandLine.cs`](../src/WSGM.PackagedLaunch/RawCommandLine.cs)                                                                                                             | Shared shortcut vocabulary, command length/admission, follow arguments and raw Windows command-line slicing |
| [`Packaging`](../src/WSGM.PackagedLaunch/Packaging)                                                                                                                                                                                                                           | AAM activation, package identity, lifetime exemption and durable recovery-record ownership                  |
| [`Strategies`](../src/WSGM.PackagedLaunch/Strategies)                                                                                                                                                                                                                         | Pure route selector, helper-only Win32 setup and complete AppContainer bridge route                         |
| [`Injection/GameInjector.cs`](../src/WSGM.PackagedLaunch/Injection/GameInjector.cs)                                                                                                                                                                                           | Remote environment/DLL work, per-process timeout latch and refusal of uncertain retries                     |
| [`Injection/OverlayObjectBroker.cs`](../src/WSGM.PackagedLaunch/Injection/OverlayObjectBroker.cs) and [`OverlayObjectAllowList.cs`](../src/WSGM.PackagedLaunch/Injection/OverlayObjectAllowList.cs)                                                                           | Desktop Steam IPC object access and bounded broker vocabulary                                               |
| [`Bridge/Bridge.cpp`](../src/WSGM.PackagedLaunch/Bridge/Bridge.cpp) and [`InputActivation.cpp`](../src/WSGM.PackagedLaunch/Bridge/InputActivation.cpp)                                                                                                                        | Native MinHook object bridge and WinRT gamepad activation routing; built as `WsgmUwpBridge.dll`             |
| [`Session/GameSessionSupervisor.cs`](../src/WSGM.PackagedLaunch/Session/GameSessionSupervisor.cs), [`GameSessionJob.cs`](../src/WSGM.PackagedLaunch/Session/GameSessionJob.cs), [`GameSessionExitDecision.cs`](../src/WSGM.PackagedLaunch/Session/GameSessionExitDecision.cs) | Discovery/active-count observation, containment, timing and final outcome                                   |
| [`Session/FollowSession.cs`](../src/WSGM.PackagedLaunch/Session/FollowSession.cs), [`FollowedGame.cs`](../src/WSGM.PackagedLaunch/Session/FollowedGame.cs), [`DetachedStart.cs`](../src/WSGM.PackagedLaunch/Session/DetachedStart.cs)                                         | External-launcher start, path/Java-instance matching and injection-free supervision                         |
| [`Session/GameForegroundProxy.cs`](../src/WSGM.PackagedLaunch/Session/GameForegroundProxy.cs) and [`ForegroundPumpLifetime.cs`](../src/WSGM.PackagedLaunch/Session/ForegroundPumpLifetime.cs)                                                                                 | Owned foreground window/event pump and AppContainer resume correction                                       |
| [`Session/ProcessInspector.cs`](../src/WSGM.PackagedLaunch/Session/ProcessInspector.cs), [`Injection/CompleteModuleInspection.cs`](../src/WSGM.PackagedLaunch/Injection/CompleteModuleInspection.cs), [`Diagnostics`](../src/WSGM.PackagedLaunch/Diagnostics)                 | Native observations, complete module-inspection result and bounded diagnostic reporting                     |

Build staging is described in [development](development.md). Compilation and route-policy tests
cannot establish activation, overlay, controller, Alt-Tab or anti-cheat behavior; those remain
attended title-specific checks.

## Evidence: the attended trials of September 2026

The two routes and the rules above rest on attended Moonlighter, PowerWash Simulator 2 and Balatro
trials on September 13 and 14, 2026. They are the evidence behind the Game Library in
[#47](https://github.com/KillerPixelCrew/WSGM/issues/47) and the launch integration in
[#48](https://github.com/KillerPixelCrew/WSGM/issues/48), both of which shipped on September 22.
Nothing here generalizes past the recorded titles.

### Classify the runtime before choosing a launcher

Xbox is a source of games, not one process model. Both UWP and Win32 titles can be packaged, so the
Game Library identifies the application runtime and launch entry before it writes a shortcut, from
the package manifest and application metadata and, where present, `MicrosoftGame.config`. An
uncertain case is validated against the activated process's identity, token and window ownership.

| Runtime              | Observed example        | Demonstrated Steam integration route                                                                                                                    |
| -------------------- | ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| UWP / AppContainer   | Moonlighter             | AAM activation, early renderer injection, desktop IPC object broker, Unity WinRT gamepad activation bridge, and correction to the game-owned CoreWindow |
| Packaged Win32 / GDK | PowerWash Simulator 2   | AAM activation, early Steam environment/client/renderer injection into GameLaunchHelper, then Steam's own child-process handoff to the real game        |
| Ordinary Win32       | Existing launcher games | Preserve Steam's normal launch chain and select the actual game window when returning to it; add a workaround only after identifying a failing boundary |

An Xbox install, a WindowsApps path, an `.exe` extension or package identity alone classifies
nothing. PowerWash has package identity but no AppContainer token and owns its normal Unity window,
and its direct executable launch still replaced the initial process through Gaming Services. An
unknown classification stays explicit rather than selecting an injection route.

Runtime classification selects the technical route. The user's input mode is a separate choice:
single-player Steam integration, or controller-only through VIIPER's Xbox 360 target.
Controller-only never silently enables injection, and neither route carries a general anti-cheat
compatibility guarantee.

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
- Steam's `logs/gameprocess_log.txt`: which processes it tracks for this shortcut.
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
helper and does nothing to the game. That helper-only simplification has not been re-tested attended
since it was made.

A protected multiplayer title was discussed on September 14 and has not been tested. Current status
remains: **overlay and input work in the two recorded titles; anti-cheat compatibility is
unverified**. No game or anti-cheat has been selected for such a test.

A future trial records the tested game and build, package runtime, anti-cheat and version if known,
Steam version, launcher build and exact options, and whether delayed descendant setup was enabled.
It records launch, gameplay input, overlay/QAM, Alt-Tab recovery, exit, and any protection-system
response separately, and keeps any resulting compatibility finding specific to that configuration
and observation window.

The PowerWash trial used both Steam-client preloading and early renderer injection. It does not
establish that every preloaded component is necessary. Additional game engines, Steam updates, and
broader launcher coverage remain unverified. Two successful titles are not a universal compatibility
claim.

Such an investigation uses native process and window observations and file logs. The maintainer
reported Steam failures during CEF investigation and later shortcut-management calls, so CEF is not
used to diagnose these games, and a shortcut is not edited out from under an attended trial.
