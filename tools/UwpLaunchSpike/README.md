# UWP launch supervisor spike

Exploratory work for issue #48, outside `WSGM.slnx` and `build.ps1`. Steam launches a persistent
wrapper, Windows activates the packaged game, and the wrapper observes it and optionally injects
into it. This is not a finished WSGM feature.

## Build and launch

```powershell
dotnet publish tools/UwpLaunchSpike/WSGM.UwpLaunchSpike.csproj -c Release -o publish/uwp-spike
./tools/UwpLaunchSpike/build-bridge.ps1 -OutputDirectory publish/uwp-spike
```

The optional native bridge needs MSVC, CMake and the MinHook source from the already restored
`minhook-sys-0.1.1` Cargo crate, which `-MinHookSource` can point at explicitly. Its licence is
copied beside the DLL. Close the game and wait for the wrapper to exit before publishing there, and
use `publish/uwp-spike-investigation` while a trial is running.

The current Steam shortcut points at `publish/uwp-spike/WsgmUwpSpike.exe` with:

```text
--aumid "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App" --inject-steam-client --inject-steam-overlay --probe-rights --hide-console --ipc-bridge
```

`--ipc-bridge` is opt-in, and removing it restores the direct injection experiment. The steamclient
stack is still a comparison option; a non-Steam overlay does not inherently need `SteamAPI_Init`.
`--help` lists every flag. Launch modes are `aam` (default), `shell`, `powershell`, and `exe`, which
is a normal Win32 child supplied through `--target`. `--observe <pid|name>` reads an existing
process without activating or injecting into it.

Transcripts land in `%LOCALAPPDATA%/WSGM UwpLaunchSpike`, beside WSGM's own data directory rather
than inside it. With the bridge enabled, the renderer's own log is redirected through a brokered
file handle to `<transcript>.renderer-<pid>.log`, because the normal Steam renderer log otherwise
describes the wrapper and is misleading.

**Do not attach a debugger to Steam, and do not use CEF for this investigation at all.** A
CEF-related Steam failure came up during this work. On September 14 CEF shortcut management was
explicitly authorized for the PowerWash trial and produced another Steam failure, so that route is
closed too, shortcut edits included. Update the shortcut file only while Steam is stopped, with a
backup and with checks that preserve other entries and refuse the write if Steam restarts. The
previous Moonlighter options are saved locally in `publish/uwp-spike-shortcut-recovery.json` for
this session. Use native process, window and handle observation plus file logs for game diagnostics.

## Attended trials, 2026-09-13 and 2026-09-14

### PowerWash Simulator 2

The installed `FuturLabLtd.PowerWashSimulator2_1.0.286.0_x64__2xkwfxww5pj0p` package is a full-trust
Win32/GDK title. Its manifest activates `GameLaunchHelper.exe`, and `MicrosoftGame.config` names
`PowerWash Simulator 2.exe`. The game owns a normal `UnityWndClass` window, has no AppContainer
token, and loads `xinput1_3.dll`.

- **00:26, AAM.** Steam tracked the wrapper, but neither the helper nor the final game got Steam's
  renderer. No overlay, no gamepad input, and the Desktop Steam Input profile. Parent lineage on its
  own tells you nothing about whether Steam's hook propagated.
- **00:28, direct game executable.** Steam tracked the initial process (4568), which exited after
  about two seconds. The replacement game (14264) was untracked and had no Steam renderer. Same
  failure, plus the loss of Steam's running state.
- **00:30, wrapper to helper.** Steam tracked and injected helper 20924, but `dllhost.exe` started
  replacement helper 16928 at medium integrity, which launched game 7464 without Steam's renderer.
  Running state was restored, still with no overlay or input. That isolates the loss to the helper
  replacement.
- **Suspended creation probes** with `PROC_THREAD_ATTRIBUTE_PACKAGE_FULL_NAME` succeeded for the
  helper from PowerShell, including the same managed creation code and an explicit environment
  block, and each probe was terminated before its primary thread ran. The standalone wrapper failed
  with error 575 both inside and outside Steam, suspended creation included. Why the caller context
  differs is still unresolved, and the unsuccessful wrapper option was removed.
- **00:42, AAM, and this one works.** The wrapper forwarded Steam's environment and injected its
  client and renderer into the returned helper (7740) immediately. Steam then tracked the actual
  game (19296), which already had its renderer loaded at the supervisor's first observation, before
  the scheduled game injection. Steam selected the shortcut's XInput layout, and controller input
  and overlay both worked. No custom AppContainer bridge and no foreground proxy. Repeated Alt-Tab
  is not separately confirmed for PowerWash.

The working PowerWash shortcut uses the same wrapper executable with:

```text
--aumid "FuturLabLtd.PowerWashSimulator2_2xkwfxww5pj0p!Game" --inject-steam-client --inject-steam-overlay --probe-rights --hide-console --no-contain --no-proxy --allow-suspend
```

Direct executable children get `SDL_GAMECONTROLLER_IGNORE_DEVICES` removed from their inherited
environment, and AAM environment forwarding carries only Steam variables and no SDL ones. The
supervisor's delayed environment and injection checks were still enabled in the working trial, and
whether descendants that already inherited Steam's renderer need them is unknown. This is a result
for this installed title, not general GDK or anti-cheat compatibility.

The separate PowerWash shortcut has AppID `2464988290` and game ID `10587044090606518272`. The
Moonlighter shortcut is independent of it.

### Moonlighter

Package `11bitstudios.20925BA3921E0_1.14.30.2_x64__gwy9gn5q9j1y6` on this machine. Everything below
is specific to these trials and is not a compatibility guarantee.

- AAM starts the game outside Steam's descendant tree. The wrapper receives Steam's launch
  environment; the broker-created game does not inherit it.
- Injector rights and direct loading of Steam's renderer both succeed. Moonlighter is a
  low-integrity AppContainer, and RTSS loads into it too.
- The game has a `Windows.UI.Core.CoreWindow`. Depending on the transition it is either hosted in an
  `ApplicationFrameWindow` owned by ApplicationFrameHost, or is a top-level CoreWindow. Ordinary
  enumeration filtered both out, so the wrapper now declares
  [disableWindowFiltering](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests#disablewindowfiltering)
  and finds the game-owned window or its correctly associated frame. An empty enumeration was never
  evidence that the game had no window or was suspended.
- Steam's renderer explicitly hooks `IDXGIFactory2::CreateSwapChainForCoreWindow` and the game swap
  chain. The game's renderer log confirmed those hooks in the 23:19:55 launch.
- Before the bridge, Steam IPC objects inside the game lived under
  `\Sessions\1\AppContainerNamedObjects\<package SID>` while Steam's own lived under
  `\Sessions\1\BaseNamedObjects`, so identical short names referred to different objects. A probe
  using the game's impersonation token also got `STATUS_ACCESS_DENIED` opening Steam's desktop input
  and PID-stream objects. A temporary symbolic-link probe succeeded as the desktop user but did not
  solve the token access restriction, and its handles were closed.
- **23:11, first bridge trial.** Moonlighter PID 7260 was registered in Steam's process log and
  `gameoverlayui64.exe -pid 7260` started with the correct shortcut ID, and there was still no
  working overlay and no controller input. Initially owned stream mutexes had been left in the
  private namespace, which the next native build fixed.
- **23:15, corrected mutex bridge.** All observed stream handles used the desktop namespace. QAM
  appeared briefly, controller input still did not work, and it failed after Alt-Tab. Steam selected
  the game's layout during some CoreWindow foreground intervals and other layouts during foreground
  changes. Registration is not a functional pass.
- The separate renderer log showed `Failed creating CEF paint event: 5`. Bridging the exact
  `SteamWebHelper_GPUProcRenderEvent` object removed that error in the 23:22 launch, and QAM then
  survived Alt-Tab. Controller input still failed.
- Moonlighter's loaded UnityPlayer and GameAssembly reference `Windows.Gaming.Input.Gamepad`. Native
  inspection in PID 7704 showed `combase!RoGetActivationFactory` detoured into Steam's renderer. A
  direct `IGamepadStatics` request returned Steam's gamepad implementation, but `IActivationFactory`
  followed by `QueryInterface(IGamepadStatics)` returned Windows' original implementation and an
  empty list, and the GameAssembly activation path uses that second form.
- **23:45:32.** Raising the CoreWindow selected shortcut `2692480092` (signed `-1602487204`) in
  `Steam/logs/controller.txt`, and the injected diagnostic then read real button and stick changes
  from Steam's gamepad inside the game. That proved input transport independently of gameplay.
- **23:54.** Added the factory-query bridge and initially raised the CoreWindow. Gameplay input
  worked. One Alt-Tab broke it: the game frame became foreground and Steam kept layout `413080`. At
  23:57:20 I triggered the existing CoreWindow correction explicitly, which restored the shortcut
  layout, and input recovered in the same process with no reinjection.
- **00:00:55 on September 14 (PID 6156).** This build used the supervisor's existing foreground
  sample to request a correction when a frame stayed foreground. The log recorded successful
  frame-to-CoreWindow corrections at 00:01:07 and 00:01:13, Steam returned to the shortcut layout,
  and controller input, repeated Alt-Tab away and back, and QAM all worked. This build also
  restricted factory routing to game-side callers, after a diagnostic showed the previous build
  wrapping Steam's own queries twice.

## How the bridge works

Before loading Steam's renderer, the wrapper creates an unnamed mapping and two unnamed events, then
duplicates their handles into the game. It injects `WsgmUwpBridge.dll` and calls its known
`InitializeBridge` export outside DllMain. The DLL installs MinHook detours for named mapping,
mutex, event, and renderer-log file creation and opening. Calls have to originate in Steam's
renderer; unrelated game calls use the originals.

A request names one allowed Steam object for this game PID and shortcut ID, or one of the shared
input, PID-reporting, detour-reporting and UI-paint objects seen in the trials. The desktop worker
opens the object and duplicates its handle into the game. It does not rewrite Steam's ACLs, change
the game's token, create a substitute rendering window, or inject into ApplicationFrameHost.
Requests are serialized and event-driven. A timed-out native request disables further exchanges
rather than reusing an uncertain reply slot or silently falling back to private objects.

Initially owned mutexes are created without ownership in the broker, then acquired by the game
thread before the hook returns. That is an experimental compromise: it does not make creation and
game-thread acquisition atomic across processes, and it needs more lifecycle and concurrency work
before anything production uses it.

The broker retains the target process handle and stops when that process or the wrapper exits.
Duplicated game handles and the native hooks last for the game process lifetime. Do not unload the
bridge from a running game; another injection setup needs a fresh game process.

After renderer injection, `InitializeInputBridge` checks that a direct WinRT gamepad-statics request
reaches Steam. It then intercepts queries on the original Gamepad activation factory from
GameAssembly, UnityPlayer or the C++/CX runtime, sending `IGamepadStatics` and `IGamepadStatics2`
requests through Steam's activation hook. Steam and combase keep their original factory queries so
Steam does not wrap its own emulated controller twice. That caller scope is specific to this Unity
title and is not general UWP support. Factory references and hooks stay until game exit, and
`InputBridgeRoutes` reports how many redirected queries succeeded.

The build also produces `WsgmUwpInputProbe.dll` for attended native diagnostics. `CaptureInput`
takes an 8192-byte caller-owned buffer and reports the two factory paths, interface method owners,
controller counts and readings. `StartInputTrace` optionally installs pass-through enumeration
counters. Keep the buffer allocated until its remote call has completed, and keep the DLL loaded
until game exit. Neither probe touches Steam CEF.

## Activation and lifetime

`IPackageDebugSettings.EnableDebugging` with no debugger exempts the package from normal PLM
suspension. Its environment parameter was rejected with `E_INVALIDARG` in every recorded run, so
that experiment is no longer retried. The wrapper uses remote `SetEnvironmentVariableW` for
environment forwarding instead. The earlier raw PEB environment replacement caused startup exits and
is gone. Early forwarding is not repeated by the supervisor.

AAM duration is not process age. In the 22:46 trial activation took over four seconds, but the game
was created only 84 ms before it returned, and the renderer arrived around 380 ms after game
creation. That timestamp alone did not tell me whether it preceded swap-chain creation.

The foreground proxy forwards Steam's return-to-game activation to the game-owned CoreWindow.
Foreground events and the existing supervisor sample also correct a foreground frame, but only when
its child CoreWindow belongs to the tracked game PID. The proxy rechecks foreground before raising
the child, so a queued check cannot raise the game over another app. It adds no polling loop and
renders no overlay. Only the game's binaries go into the kill-on-close job; shared RuntimeBroker and
ApplicationFrameHost processes are excluded. On an ordinary wrapper exit, `DisableDebugging`
restores package lifetime management, but forced termination can skip that COM cleanup, so the debug
exemption is not crash-safe yet.

## The CreateProcess package-attribute alternative

The proposed package attribute is real, but its value is `0x00020008`, not `0x00020017`. The primary
[NtCoreLib implementation](https://github.com/googleprojectzero/sandbox-attacksurface-analysis-tools/blob/main/NtCoreLib/Win32/Process/Interop/Win32ProcessAttributes.cs)
uses attribute number 8. On this machine `UpdateProcThreadAttribute` accepted it, while
`CreateProcess` and the exploratory `CreateProcessAsUser` call both returned error 5 before creating
Moonlighter. The probe requested `CREATE_SUSPENDED` and would have discarded only its own new
process, and no game was created by these probes. That does not prove every direct activation
variant is impossible; package activation requirements are a separate thing from supplying identity.

## Where this stands

Managed Release publish and native Release compilation work independently of WSGM's gate. Follow the
repository's manual-first policy. A compiler pass, a loaded DLL, a matching object namespace or a
running overlay UI is not proof of working overlay and input.

Launch, controller input, QAM and repeated Alt-Tab away and back all passed in the September 14
Moonlighter trial, on this machine. Production integration, automatic injection policy, crash
recovery and general UWP compatibility are all out of scope, and #48 stays open.

Two explicit modes are planned for the eventual launcher:

- **Steam integration for single-player:** the overlay and input bridge demonstrated here.
- **Controller only:** switch VIIPER to its Xbox 360 target with no custom game injection, and never
  silently fall back to injection. Valve controller users in Desktop Mode may therefore have no
  multiplayer controller support, which is an accepted limitation.

Those modes are a design decision under #48. The spike implements neither the per-game selection UI
nor VIIPER session switching. The recorded trial ran an elevated wrapper against a low-integrity
AppContainer game, and the minimum privileges the wrapper actually needs are unknown.

**Anti-cheat compatibility is unverified.** Moonlighter uses a custom injected DLL and game-side
hooks. The PowerWash route uses Valve DLLs without that bridge, but still does remote setup in the
helper and delayed environment writes and DLL loads in the game, so it is not only about launch
ordering, and the proposed helper-only comparison is not implemented. Production defaults are
unchanged, and the spike has no anti-cheat detection and no protected-title denylist.

An attended protected-multiplayer test may happen in the next few days. No title has been chosen and
no result reported. When it happens, record the exact game, anti-cheat, and Steam and launcher
versions and options using the
[launcher handoff evidence checklist](../../docs/steam-launcher-handoff.md#remaining-limits). Until
then the status is unchanged: overlay and input work in the two recorded titles, and anti-cheat
compatibility is unverified.
