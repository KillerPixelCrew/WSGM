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
- The game runs at **low integrity inside an AppContainer** with the **`StoreSignedOnly` binary
  signature mitigation** set. A process under that policy refuses to load a DLL that is not Store
  signed, which is a second, independent obstacle to Steam's overlay regardless of lineage.
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

The open question for the next Steam-launched run is what the transcript shows from inside a
medium-integrity wrapper: the real handle rights on the game, and whether
`gameoverlayrenderer64.dll` shows up anywhere at all, including in the wrapper itself.

## Caveats

- Run it unelevated. `ActivateApplication` is refused from a high-integrity process, and the whole
  point of the rights probe is to measure what a normal-integrity wrapper can reach.
- Process identification uses package family plus launch lineage plus `--match`. A title that
  hands off to a process with none of the three will not be tracked; add `--match` for it and say
  so in the findings.
- Nothing here injects anything. It only observes.
