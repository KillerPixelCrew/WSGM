# UWP launch supervisor spike

Exploratory work for issue #48, outside `WSGM.slnx` and `build.ps1`. Steam launches a persistent
wrapper; Windows activates the packaged game; the wrapper observes and optionally injects into that
game. This is not a completed WSGM feature.

## Build and launch

```powershell
dotnet publish tools/UwpLaunchSpike/WSGM.UwpLaunchSpike.csproj -c Release -o publish/uwp-spike
./tools/UwpLaunchSpike/build-bridge.ps1 -OutputDirectory publish/uwp-spike
```

The optional native bridge uses MSVC, CMake, and the MinHook source from the already restored
`minhook-sys-0.1.1` Cargo crate. `-MinHookSource` can select that source explicitly. Its license is
copied beside the DLL. Close the game and wait for the wrapper to exit before publishing there; use
`publish/uwp-spike-investigation` while a trial is running.

The current Steam shortcut points at `publish/uwp-spike/WsgmUwpSpike.exe` with:

```text
--aumid "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App" --inject-steam-client --inject-steam-overlay --probe-rights --hide-console --ipc-bridge
```

`--ipc-bridge` is opt-in. Removing it restores the direct injection experiment. The steamclient
stack remains a comparison option; a non-Steam overlay does not inherently require SteamAPI_Init.
Use `--help` for the complete flags. Launch modes are `aam` (default), `shell`, `powershell`, and
`exe` (a normal Win32 child supplied through `--target`). `--observe <pid|name>` reads an existing
process without activating or injecting into it.

Transcripts go to `%LOCALAPPDATA%/WSGM/uwp-spike`. With the bridge enabled, the renderer's own log
is redirected through a brokered file handle to `<transcript>.renderer-<pid>.log`. The normal Steam
renderer log otherwise describes the wrapper and can be misleading.

Do not attach a debugger to Steam. The maintainer reported a CEF-related Steam failure during this
investigation. Use native process/window/handle observations and file logs for game diagnostics. On
September 14 the maintainer explicitly authorized CEF shortcut management for the PowerWash trial,
but reported another Steam failure during those calls. Stop using CEF for this investigation,
including shortcut edits. Update the shortcut file only while Steam is stopped, with a backup and
checks that preserve other entries and refuse a write if Steam restarts. Previous Moonlighter
options are recorded locally in `publish/uwp-spike-shortcut-recovery.json` for this session.

## Attended findings on 2026-09-13 and 2026-09-14

### PowerWash Simulator 2

The installed `FuturLabLtd.PowerWashSimulator2_1.0.286.0_x64__2xkwfxww5pj0p` package is a full-trust
Win32/GDK title. Its manifest activates `GameLaunchHelper.exe`; `MicrosoftGame.config` names
`PowerWash Simulator 2.exe`. The game owns a normal `UnityWndClass` window, has no AppContainer
token, and loads `xinput1_3.dll`.

- In the 00:26 AAM trial Steam tracked the wrapper, but neither the helper nor the final game
  received Steam's renderer. The maintainer confirmed no overlay or gamepad input and the Desktop
  Steam Input profile. Reported parent lineage did not establish Steam hook propagation.
- In the 00:28 direct game-executable trial Steam tracked the initial process (4568), which exited
  after about two seconds. The replacement game (14264) was not tracked and had no Steam renderer.
  The maintainer confirmed the same failure and loss of Steam's running state.
- In the 00:30 wrapper-to-helper trial Steam tracked and injected helper 20924, but `dllhost.exe`
  started replacement helper 16928 at medium integrity. That helper launched game 7464 without
  Steam's renderer. The maintainer confirmed the running state was restored, still with no overlay
  or input. This isolates the observed loss to the helper replacement.
- Suspended creation probes with `PROC_THREAD_ATTRIBUTE_PACKAGE_FULL_NAME` succeeded for the helper
  from PowerShell, including the same managed creation code and an explicit environment block. Each
  probe was terminated before its primary thread ran. The standalone wrapper failed with error 575
  both inside and outside Steam, including suspended creation. The differing caller context remains
  unresolved; the unsuccessful wrapper option was removed.
- The 00:42 AAM trial forwarded Steam's environment and injected its client/renderer into the
  returned helper (7740) immediately. Steam then tracked the actual game (19296), which had its
  renderer loaded by the supervisor's first observation, before scheduled game injection. Steam
  selected the shortcut's XInput layout. The maintainer confirmed working controller input and
  overlay. No custom AppContainer bridge or foreground proxy was used. Repeated Alt-Tab has not been
  separately confirmed for PowerWash.

The working PowerWash shortcut uses the same wrapper executable with:

```text
--aumid "FuturLabLtd.PowerWashSimulator2_2xkwfxww5pj0p!Game" --inject-steam-client --inject-steam-overlay --probe-rights --hide-console --no-contain --no-proxy --allow-suspend
```

Direct executable children have `SDL_GAMECONTROLLER_IGNORE_DEVICES` removed from their inherited
environment. AAM environment forwarding includes only Steam variables and excludes SDL variables.
The supervisor's existing delayed environment/injection checks remain enabled in the working trial;
their necessity for descendants that already inherited Steam's renderer has not been established.
This is a result for this installed title, not general GDK or anti-cheat compatibility.

The separate PowerWash shortcut has AppID `2464988290` and game ID `10587044090606518272`. The
Moonlighter shortcut remains independent.

### Moonlighter

Moonlighter is package `11bitstudios.20925BA3921E0_1.14.30.2_x64__gwy9gn5q9j1y6` on this machine.
The observations below are specific to these attended trials, not a compatibility guarantee.

- AAM starts the game outside Steam's descendant tree. The wrapper receives Steam's launch
  environment; the broker-created game does not inherit it.
- Injector rights and direct loading of Steam's renderer succeed. Moonlighter is a low-integrity
  AppContainer. RTSS also loads into it.
- The game has a `Windows.UI.Core.CoreWindow`. Depending on the transition it is hosted in an
  `ApplicationFrameWindow` owned by ApplicationFrameHost or appears as a top-level CoreWindow.
  Ordinary enumeration filtered these windows out. The wrapper now declares
  [disableWindowFiltering](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests#disablewindowfiltering)
  and finds the game-owned window or its correctly associated frame. Empty enumeration was not
  evidence that the game had no window or was suspended.
- Steam's renderer explicitly hooks `IDXGIFactory2::CreateSwapChainForCoreWindow` and the game swap
  chain. The game's renderer log confirmed those hooks in the 23:19:55 launch.
- Before the bridge, Steam IPC objects inside the game lived under
  `\Sessions\1\AppContainerNamedObjects\<package SID>`. Steam's corresponding objects lived under
  `\Sessions\1\BaseNamedObjects`. Identical short names referred to different objects. A probe using
  the game's impersonation token also received `STATUS_ACCESS_DENIED` opening Steam's desktop input
  and PID-stream objects. A temporary symbolic-link probe succeeded as the desktop user but did not
  solve that token access restriction; its handles were closed.
- The first bridge trial at 23:11 registered Moonlighter PID 7260 in Steam's process log and started
  `gameoverlayui64.exe -pid 7260` with the correct shortcut ID. The maintainer still saw neither a
  working overlay nor controller input. Initially owned stream mutexes had been left in the private
  namespace; the next native build corrected that omission.
- With the corrected mutex bridge at 23:15, the observed stream handles all used the desktop
  namespace. The maintainer saw QAM briefly, still had no controller input, and reported failure
  after Alt-Tab. Steam selected the game's layout during some CoreWindow foreground intervals, then
  selected other layouts during foreground changes. Registration is not a functional pass.
- The separate renderer log exposed `Failed creating CEF paint event: 5`. Bridging the exact
  `SteamWebHelper_GPUProcRenderEvent` object removed that error in the 23:22 launch. The maintainer
  confirmed that QAM then survived Alt-Tab, but controller input still failed.
- Moonlighter's loaded UnityPlayer and GameAssembly reference `Windows.Gaming.Input.Gamepad`. Native
  inspection in PID 7704 proved that `combase!RoGetActivationFactory` was detoured into Steam's
  renderer. A direct `IGamepadStatics` request returned Steam's gamepad implementation;
  `IActivationFactory` followed by `QueryInterface(IGamepadStatics)` returned Windows' original
  implementation and an empty list. The GameAssembly activation path uses that second form.
- At 23:45:32, raising the CoreWindow selected shortcut `2692480092` (signed `-1602487204`) in
  `Steam/logs/controller.txt`. The injected diagnostic then read real button and stick changes from
  Steam's gamepad inside the game. This proved input transport independently of gameplay.
- The 23:54 launch added the factory-query bridge and initially raised the CoreWindow. The
  maintainer confirmed working gameplay input. One Alt-Tab broke it: the game frame became
  foreground and Steam retained layout `413080`. At 23:57:20, explicitly triggering the existing
  CoreWindow correction restored the shortcut layout, and the maintainer confirmed input recovered
  in the same process without reinjection.
- The 00:00:55 follow-up launch on September 14 (PID 6156) used the supervisor's existing foreground
  sample to request correction when a frame remained foreground. Its log recorded successful
  frame-to-CoreWindow corrections at 00:01:07 and 00:01:13; Steam returned to the shortcut layout.
  The maintainer confirmed that controller input, repeated Alt-Tab away/back, and QAM all worked.
  This build also restricted factory routing to game-side callers after a diagnostic exposed
  duplicate wrapping of Steam's own queries in the previous build.

## How the bridge works

Before loading Steam's renderer, the wrapper creates an unnamed mapping and two unnamed events, then
duplicates their handles into the game. It injects `WsgmUwpBridge.dll` and calls its known
`InitializeBridge` export outside DllMain. The DLL installs MinHook detours for named mapping,
mutex, event, and renderer-log file creation/opening. Calls must originate in Steam's renderer;
unrelated game calls use the originals.

A request names one allowed Steam object for this game PID and shortcut ID, or the shared input,
PID-reporting, detour-reporting, and UI-paint objects observed in the trials. The desktop worker
opens the object and duplicates its handle into the game. It does not rewrite Steam's ACLs, change
the game's token, create a substitute rendering window, or inject into ApplicationFrameHost.
Requests are serialized and event-driven. A timed-out native request disables further exchanges
instead of reusing an uncertain reply slot or silently returning to private objects.

Initially owned mutexes are created without ownership in the broker, then acquired by the game
thread before the hook returns. This is an experimental compromise: it does not make creation and
game-thread acquisition atomic across processes. Further lifecycle and concurrency validation is
required before production use.

The broker retains the target process handle and stops when that process or the wrapper exits.
Duplicated game handles and the native hooks last for the game process lifetime. Do not unload the
bridge from a running game. A fresh game process is required for another injection setup.

After renderer injection, `InitializeInputBridge` verifies that a direct WinRT gamepad-statics
request reaches Steam. It then intercepts queries on the original Gamepad activation factory from
GameAssembly, UnityPlayer, or the C++/CX runtime. Requests for `IGamepadStatics` and
`IGamepadStatics2` go through Steam's activation hook. Steam and combase keep their original factory
queries so Steam does not wrap its own emulated controller twice. This caller scope is specific to
the inspected Unity title, not general UWP support. Factory references and hooks remain until game
exit; `InputBridgeRoutes` reports the number of successful redirected queries.

The build also produces `WsgmUwpInputProbe.dll` for attended native diagnostics. `CaptureInput`
accepts an 8192-byte caller-owned buffer and reports the two factory paths, interface method owners,
controller counts, and readings. `StartInputTrace` optionally installs pass-through enumeration
counters. Keep the buffer allocated until its remote call has completed, and keep the DLL loaded
until game exit. Neither probe accesses Steam CEF.

## Activation and lifetime findings

`IPackageDebugSettings.EnableDebugging` with no debugger exempts the package from normal PLM
suspension. Its environment parameter was rejected with `E_INVALIDARG` in the recorded runs; that
failed experiment is no longer retried. The wrapper uses remote `SetEnvironmentVariableW` for
environment forwarding. The previous raw PEB environment replacement caused startup exits and is no
longer used. Early forwarding is not repeated by the supervisor.

AAM duration is not process age: in the 22:46 trial activation took over four seconds, but the game
was created only 84 ms before it returned. The renderer arrived around 380 ms after game creation.
That timestamp alone did not establish whether it preceded swap-chain creation.

The foreground proxy forwards Steam's return-to-game activation to the game-owned CoreWindow.
Foreground events and the existing supervisor sample also correct a foreground frame, but only when
its child CoreWindow belongs to the tracked game PID. The proxy rechecks foreground before raising
the child, so a queued check does not raise the game over another app. It adds no polling loop and
does not render an overlay. Only the game's binaries enter the kill-on-close job; shared
RuntimeBroker and ApplicationFrameHost processes are excluded. On ordinary wrapper exit,
`DisableDebugging` restores package lifetime management. Forced termination can skip that COM
cleanup, so the debug exemption is not crash-safe yet.

## CreateProcess package-attribute alternative

The proposed package attribute is real, but its value is `0x00020008`, not `0x00020017`. The primary
[NtCoreLib implementation](https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools/blob/main/NtCoreLib/Win32/Process/Interop/Win32ProcessAttributes.cs)
uses attribute number 8. On this machine, `UpdateProcThreadAttribute` accepted it, while
`CreateProcess` and the exploratory `CreateProcessAsUser` call returned error 5 before creating
Moonlighter. The probe requested `CREATE_SUSPENDED` and would discard only its own new process. No
game was created by these probes. This does not prove that all direct activation variants are
impossible; package activation requirements remain distinct from supplying identity.

## Validation and remaining work

Managed Release publish and native Release compilation are available independently of WSGM's gate.
Follow the repository's manual-first policy. A compiler pass, loaded DLL, matching object namespace,
or running overlay UI is not proof of working overlay/input.

The maintainer passed launch, controller input, QAM, and repeated Alt-Tab away/back in the September
14 trial. This is a Moonlighter spike result on this machine. Production integration, automatic
injection policy, crash recovery, and general UWP compatibility remain out of scope; issue #48
remains open.

The maintainer selected two explicit modes for the eventual launcher:

- **Steam integration for single-player:** use the overlay and input bridge demonstrated here.
- **Controller only:** switch VIIPER to its Xbox 360 target without custom game injection. Valve
  controller users in Desktop Mode may consequently lack multiplayer controller support; this is an
  accepted limitation. Do not silently fall back to injection.

These launcher modes are a design decision under #48. The spike does not implement the per-game
selection UI or VIIPER session switching. The recorded trial ran with an elevated wrapper and a
low-integrity AppContainer game; the minimum necessary wrapper privileges remain unverified.

Anti-cheat compatibility is unverified. This spike injects a custom DLL and hooks code inside the
game; the use of Steam's genuine renderer does not establish approval for the custom bridge. The
Moonlighter result does not establish safety for protected multiplayer titles. Keep this approach
disabled for those titles unless compatibility is explicitly established. The spike does not
currently implement anti-cheat detection or an enforced protected-title denylist.
