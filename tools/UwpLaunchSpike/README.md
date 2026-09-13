# UWP launch supervisor spike

Research scaffold for issue 48: a persistent Steam-owned wrapper that activates an Xbox App /
MSIX title, keeps running for the whole session so Steam holds the shortcut in a running state,
and records what actually happened to process lineage, security context, and Steam overlay
injection.

This is exploratory instrumentation, not a WSGM feature. It is deliberately outside `WSGM.slnx`
and outside `build.ps1`, so `eng/verify.ps1` does not build or gate it.

## What it answers

The issue has two competing hypotheses for why Xbox titles do not get the Steam overlay:

1. **Process tree.** Steam only follows render processes inside the launch tree of the executable
   it started, and package activation puts the game out of tree.
2. **Security boundary.** Steam finds the process but cannot open it with injector rights.

The transcript separates the two. For every process it tracks it records the parent chain and
creation time (hypothesis 1) alongside integrity level, AppContainer identity, mitigation
policies, and the exact `OpenProcess` masks this unelevated wrapper can obtain (hypothesis 2).
It also reports the moment `gameoverlayrenderer64.dll` appears in any tracked process, and
whether Steam injected into the wrapper itself instead of the game.

## Build

```powershell
dotnet publish tools\UwpLaunchSpike\WSGM.UwpLaunchSpike.csproj -c Release -o publish\uwp-spike
```

Self-contained, so the published folder can be pointed at from a Steam shortcut without depending
on an installed runtime.

## Run

```powershell
publish\uwp-spike\WsgmUwpSpike.exe --aumid "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App" --probe-rights
```

Find an AUMID with `Get-StartApps`, or the package family with `Get-AppxPackage`.

Modes, which are the experiment matrix from the issue:

| `--mode` | What it tests |
| --- | --- |
| `aam` (default) | `IApplicationActivationManager::ActivateApplication`, no script interpreter in the chain |
| `shell` | Explorer-mediated `shell:AppsFolder\<AUMID>` activation |
| `powershell` | `powershell.exe -Command Start-Process shell:AppsFolder\...`, the shape most existing Xbox-to-Steam wrappers use |
| `exe` | A conventional Win32 child via `--target`, the control case for intact lineage |

Other flags worth knowing: `--probe-rights` performs the injector-grade `OpenProcess` probe,
`--hide-console` hides the window and stops writing to it, `--no-contain` lets the game outlive
the wrapper, `--match` adds an image-name hint when package identity alone does not find the game,
and `--log` redirects the transcript.

The transcript never depends on the console. A Steam-launched run stalled on its first console
write and produced nothing at all, so the file is written first and console output is skipped
entirely when the console is hidden or stdout is redirected.

Transcripts default to `%LOCALAPPDATA%\WSGM\uwp-spike\<timestamp>-<mode>.log`.

## Adding it to Steam

Add the published exe as a non-Steam game and put the AUMID and flags in Launch Options, for
example:

```
--aumid "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App" --probe-rights --hide-console
```

`--hide-console` matters for a Steam-launched run: a visible console window takes the foreground
away from the game at exactly the moment the overlay would attach, which is the thing being
measured. The transcript records everything the console would have shown.

`eng\add-uwp-spike-shortcut.ps1` writes the same entry into `shortcuts.vdf` directly:

```powershell
.\eng\add-uwp-spike-shortcut.ps1 -Aumid "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App" -DryRun
.\eng\add-uwp-spike-shortcut.ps1 -Aumid "11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App"
```

Steam must be closed for the real write, because Steam holds `shortcuts.vdf` in memory and
rewrites it on exit. `-DryRun` only parses and reports, so it is safe while Steam is open. The
previous file is backed up next to itself before every write.

With Steam already running, adding the entry through the live client instead avoids a restart.


## Reading a transcript

- **Wrapper context.** `ancestors` shows whether `steam.exe` is the direct parent. `steam env`
  shows which of `SteamAppId` / `SteamGameId` / `SteamOverlayGameId` reached the wrapper. The
  wrapper's own module list shows whether Steam injected the overlay into the wrapper.
- **Activation.** The `HRESULT` and the process id the activation returned, if any.
- **Supervision.** One block per process that appeared, then `OVERLAY:` lines when and if Steam's
  renderer shows up, then the exit lines.

Exit codes: `0` a game ran and ended, `1` no completed session, `2` bad arguments, `3` activation
failed.

## First measurements

Moonlighter (`11bitstudios.20925BA3921E0_gwy9gn5q9j1y6!App`), 2026-09-13, `--mode aam`, wrapper
started from a terminal rather than from Steam, so this says nothing yet about overlay injection:

- `ActivateApplication` returned the real game pid directly. There is no bootstrapper and no
  GameLaunchHelper in this title; `Moonlighter.exe` is the process from the first moment.
- The game's parent was `svchost.exe`, not the wrapper. Direct package activation with no
  PowerShell in the chain still leaves the game out of the wrapper's process tree, so removing the
  script interpreter alone does not restore lineage.
- The game runs at **low integrity inside an AppContainer** with `signature=StoreSignedOnly`
  (raw `0x2`, no audit bits). That policy reads like a hard block on any DLL that is not Store
  signed. **It is not one in practice** — see the RTSS result below, which falsifies that reading.
- The injector-grade `OpenProcess` masks all succeeded, but that run's wrapper was itself at high
  integrity. The rights probe only means something from a medium-integrity wrapper, which is what
  a Steam-launched run gives.

First Steam-launched run, same title and mode, observed from the client rather than from a
transcript (the wrapper stalled on its first console write and left a zero-byte file, since
fixed):

- **The wrapper works as Steam's lifetime anchor.** Steam launched it, held the shortcut in a
  running state for as long as the wrapper lived, and offered Stop. That is the part of the
  architecture the issue was least sure about, and it holds.
- **No Steam Overlay and no Steam Input reached the game.** Consistent with both obstacles above.
- **Stopping the shortcut in Steam did not stop the game.** Steam terminated the wrapper, and
  Moonlighter kept running out of tree with Steam showing the shortcut as stopped. The wrapper now
  puts the title's own binaries in a kill-on-close job object, so the kernel ends the game when
  the wrapper dies however it dies. Nested assignment succeeds even though a packaged app already
  sits in a system-managed job. `--no-contain` turns it off.

Injection and lifetime results, same title, wrapper started from a terminal:

- **The signature policy is not the wall.** RTSS loaded its own unsigned-by-Store
  `RTSSHooks64.dll` into the game 1.1s after launch, in the AppContainer, with
  `StoreSignedOnly` set. Whatever that policy gates, it is not third-party DLL loading here.
- **Steam's overlay renderer loads into the game.** A remote `LoadLibraryW` of
  `GameOverlayRenderer64.dll` from Steam's own install path returned a module base. No ACL work
  was needed: Steam's directory already carries an inherited `ALL APPLICATION PACKAGES (RX)` ACE,
  which is the permission the ReShade UWP route has to add by hand. Loading it is not sufficient
  on its own - the overlay did not become usable - so the renderer DLL is necessary but not the
  whole overlay.
- **Windows suspends the title whenever it loses the foreground.** All 66 threads sat in
  Suspended wait while another window had focus. That is ordinary PLM behaviour for a packaged
  app, and it explains why the game has no enumerable window from a background probe and why
  Steam's Resume cannot raise it: `SetForegroundWindow` does not wake a suspended package. It
  does not explain overlay or input failing during play, when the game does have focus.
- **A packaged process inherits nothing from the wrapper**, so none of Steam's launch variables
  reach it. `IPackageDebugSettings::EnableDebugging` takes both the suspension exemption and an
  environment block for the package's next launch, which is the one supported route for handing
  `SteamAppId` / `SteamGameId` / `SteamOverlayGameId` to a broker-started title. The wrapper now
  does both before activating, and `--observe` dumps a working overlay process for comparison.

Control case, Balatro launched normally from Steam and read with `--observe`:

| Module | Location |
| --- | --- |
| `steam_api64.dll` | the game's own folder |
| `steamclient64.dll` | Steam root |
| `tier0_s64.dll`, `vstdlib_s64.dll` | Steam root, pulled in by steamclient |
| `gameoverlayrenderer64.dll` | Steam root |

Its parent is `steam.exe`, and it runs with intact lineage and a normal window.

Read that table carefully, because the obvious conclusion from it is wrong. `steam_api64.dll` and
`steamclient64.dll` are there because Balatro is a Steam build that calls `SteamAPI_Init` for its
own purposes. **The overlay does not depend on that call**: an ordinary non-Steam shortcut ships no
`steam_api64.dll` and never makes it, yet gets the overlay anyway. The renderer negotiates with the
client over the pipe once both ends are up.

So the missing piece for a packaged title is not the Steam API, it is the launch environment. Steam
hands `SteamAppId` / `SteamGameId` / `SteamOverlayGameId` to whatever it starts, including the id it
calculates for a non-Steam shortcut, and the wrapper receives them - but a broker-activated package
inherits nothing from the wrapper, so the injected renderer comes up with no session to attach to.
`EnableDebugging`'s environment block is the one supported way to close that, and the wrapper now
uses it. `--call` and `--steam-api-init` exist to test the Steam-API hypothesis anyway, since a
measurement beats a deduction; the runs above are what happens when they are not used.

First Steam-launched run with injection, and what it settled:

- **Steam hands the wrapper the whole set.** `SteamAppId=2692480092`,
  `SteamGameId=SteamOverlayGameId=11564113940304625664` (`0xA080000040800000`: the calculated
  shortcut id with the shortcut type tag), plus `SteamClientLaunch`, `SteamEnv`, `SteamPath`,
  `SteamTenfoot`, `SteamGamepadUI`. Nothing has to be computed; Steam provides it.
- **Steam injects its renderer into the wrapper**, which has all of that environment and draws
  nothing. That is the architecture in one line: the overlay attaches to the process Steam
  launched, and the rendering happens in another process that has none of it.
- **`EnableDebugging` cannot carry the environment.** It answers `E_INVALIDARG` for any
  environment block and succeeds the moment one is not passed, so that parameter is the
  environment for the debugger command line, not for the app. The suspension exemption still
  works; environment forwarding through it does not.

What replaces it is `EnvironmentPatch`, which edits the game's own environment block through its
PEB. `GetEnvironmentVariableW` reads `ProcessParameters->Environment` on every call, so repointing
it changes what code loaded afterwards sees - and the renderer is injected after. Verified
mechanically against a live game: a 5892-byte block read, merged, and replaced with 44 variables.

Patching that block has a timing rule that has to be respected, found the hard way:

| Environment patch | Game |
| --- | --- |
| none | runs normally |
| ~30ms after the process appears | dies 1.1s later, every time |
| 8s after the process appears | runs on, 27s+ observed, with the variables in place |

Same bytes in every case, so it is a startup race rather than anything wrong with the block: the
loader and the packaged-app runtime are still bringing the process up in that window. Hence
`--env-delay`, defaulting to 8s, with injection held until 2s after it so the renderer never loads
before the variables it reads exist. The suspension exemption is not implicated - a run with
`EnableDebugging` active and no patch ran 50s untouched.

Still open: whether the renderer, injected into a process that now carries Steam's session
variables, actually registers and draws - and whether Steam Input follows it or needs its own
association.

## Caveats

- Run it unelevated. `ActivateApplication` is refused from a high-integrity process, and the whole
  point of the rights probe is to measure what a normal-integrity wrapper can reach.
- Process identification uses package family plus launch lineage plus `--match`. A title that
  hands off to a process with none of the three will not be tracked; add `--match` for it and say
  so in the findings.
- Nothing here injects anything. It only observes.
