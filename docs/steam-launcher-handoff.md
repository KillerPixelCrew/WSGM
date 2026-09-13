# Steam overlay and input across launchers

This records the attended Moonlighter, PowerWash Simulator 2, and Balatro findings from September
13–14, 2026. It guides the future library importer in
[#47](https://github.com/KillerPixelCrew/WSGM/issues/47) and launch integration in
[#48](https://github.com/KillerPixelCrew/WSGM/issues/48). The implementation is an exploratory
[launch spike](../tools/UwpLaunchSpike/README.md), not a shipped universal launcher.

## Classify the runtime before choosing a launcher

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

## Conditions to establish and verify

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

## Diagnose a launcher game by the first failing boundary

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

## Remaining limits

The working PowerWash route is not only launch ordering. The spike forwards remote environment
variables and loads Valve DLLs through `CreateRemoteThread`/`LoadLibraryW` in the helper. Its
delayed descendant setup also performs remote environment writes and DLL loads in the game, even
when Steam already loaded the renderer. No custom AppContainer bridge is used, but Valve signatures
alone do not establish acceptance of this external loading path by every anti-cheat. A helper-only
comparison with no later custom game-process writes is the next simplification to validate.

On September 14 the maintainer indicated they may test a protected multiplayer title in the next few
days. That attended test is planned, not completed. Current status remains: **overlay and input work
in the two recorded titles; anti-cheat compatibility is unverified**. No game or anti-cheat has been
selected for that future test, and the helper-only simplification is not implemented.

Record the tested game/build, package runtime, anti-cheat and version if known, Steam version,
launcher build and exact options, and whether delayed descendant setup was enabled. Record launch,
gameplay input, overlay/QAM, Alt-Tab recovery, exit, and any protection-system response separately.
Keep any resulting compatibility finding specific to that configuration and observation window.

The PowerWash trial used both Steam-client preloading and early renderer injection. It does not
establish that every preloaded component is necessary. The supervisor also retained delayed
environment/injection checks for descendants; removing those needs a separate comparison. Minimum
privileges, crash recovery, additional game engines, Steam updates, and broader launcher coverage
remain unverified. Do not turn the two successful titles into a universal compatibility claim.

For this investigation, use native process/window observations and file logs. The maintainer
reported Steam failures during CEF investigation and later shortcut-management calls. Do not use CEF
to diagnose these games or edit their trial shortcuts; offline shortcut updates require Steam
stopped, a backup, and preservation of other entries.
